using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Provides real-time local filesystem audio scanning, ID3v2/FLAC/M4A/Ogg/WAV metadata and embedded artwork extraction,
/// LRC synchronized lyric parsing, YouTube Music personalized Home discovery, and Artist/Album detail resolution.
/// </summary>
public sealed partial class MediaCatalogService : IMediaCatalogService
{
    private const string PersistedCatalogKey = "LocalCatalogTracks_v2";
    private const string PersistedRecentlyPlayedKey = "RecentlyPlayedTracks_v1";
    private const string PersistedRecentlyPlayedAlbumsKey = "RecentlyPlayedAlbums_v1";
    private const string PersistedLikedOnlineTracksKey = "LikedOnlineTracks_v1";
    private const string PersistedLocalDirectoriesKey = "LocalMusicDirectories_v1";
    private const string PersistedPlaylistsKey = "UserPlaylists_v1";
    private const string PersistedTrackAddedAtKey = "TrackAddedAt_v1";
    private const int MaxPersistedRecentAlbums = 50;
    private const int MaxTrackAddedAtEntries = 5000;
    private static readonly TimeSpan OnlineLibraryCacheTtl = TimeSpan.FromMinutes(5);

    private static readonly string[] SupportedAudioExtensions =
    [
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".alac"
    ];

    private static readonly string[] FolderArtworkCandidateNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png",
        "folder.jpg", "folder.jpeg", "folder.png",
        "album.jpg", "album.jpeg", "album.png",
        "front.jpg", "front.png", "artwork.jpg", "artwork.png"
    ];

    private readonly IStorageService _storageService;
    private readonly IYouTubeMusicService _youTubeMusicService;
    private readonly ILyricsProviderService _lyricsProviderService;
    private readonly ICanvasArtworkService _canvasArtworkService;
    private readonly ILogger<MediaCatalogService> _logger;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly SemaphoreSlim _onlineLibraryLock = new(1, 1);

    private readonly List<TrackItem> _tracks = [];
    private readonly List<AlbumItem> _albums = [];
    private readonly List<ArtistItem> _artists = [];
    private readonly List<TrackItem> _recentlyPlayedTracks = [];
    private readonly List<RecentAlbumItem> _recentlyPlayedAlbums = [];
    private readonly List<TrackItem> _likedOnlineTracks = [];
    private readonly HashSet<string> _likedOnlineTrackIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _localMusicDirectories = [];
    private LocalScanFilterSettings _localScanFilterSettings = new();
    private readonly List<UserPlaylist> _playlists = [];
    private readonly Dictionary<string, long> _trackAddedAtMap = new(StringComparer.OrdinalIgnoreCase);
    private CatalogQueryResult? _cachedOnlineLibraryCatalog;
    private DateTimeOffset _onlineLibraryCachedAt = DateTimeOffset.MinValue;
    private string _albumArtCacheDir = string.Empty;
    private string _lyricsCacheDir = string.Empty;
    private bool _isInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaCatalogService"/> class.
    /// </summary>
    public MediaCatalogService(
        IStorageService storageService,
        IYouTubeMusicService youTubeMusicService,
        ILyricsProviderService lyricsProviderService,
        ICanvasArtworkService canvasArtworkService,
        ILogger<MediaCatalogService> logger)
    {
        _storageService = storageService;
        _youTubeMusicService = youTubeMusicService;
        _lyricsProviderService = lyricsProviderService;
        _canvasArtworkService = canvasArtworkService;
        _logger = logger;
        _youTubeMusicService.AccountSessionChanged += (_, _) => InvalidateOnlineLibraryCache();
    }

    private void InvalidateOnlineLibraryCache()
    {
        _cachedOnlineLibraryCatalog = null;
        _onlineLibraryCachedAt = DateTimeOffset.MinValue;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XperienceMusicPlayer");

            _albumArtCacheDir = Path.Combine(appDataRoot, "AlbumArtCache");
            _lyricsCacheDir = Path.Combine(appDataRoot, "LyricsCache");
            Directory.CreateDirectory(_albumArtCacheDir);
            Directory.CreateDirectory(_lyricsCacheDir);

            var savedScanFilter = await _storageService.LoadAsync<LocalScanFilterSettings>(
                LocalScanFilterSettings.StorageKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            if (savedScanFilter is not null)
            {
                _localScanFilterSettings = savedScanFilter;
            }

            // 1. Load persistent first-seen/added timestamps and previously scanned tracks
            var savedAddedAt = await _storageService.LoadAsync<Dictionary<string, long>>(
                PersistedTrackAddedAtKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            if (savedAddedAt is { Count: > 0 })
            {
                bool addedAtTrimmed = false;
                Dictionary<string, long>? trimmedSnapshot = null;
                lock (_trackAddedAtMap)
                {
                    foreach (var kvp in savedAddedAt)
                    {
                        if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value > 0)
                        {
                            _trackAddedAtMap[kvp.Key] = kvp.Value;
                        }
                    }

                    addedAtTrimmed = TrimTrackAddedAtMapLocked();
                    if (addedAtTrimmed)
                    {
                        trimmedSnapshot = new Dictionary<string, long>(_trackAddedAtMap, StringComparer.OrdinalIgnoreCase);
                    }
                }

                if (addedAtTrimmed && trimmedSnapshot is not null)
                {
                    await _storageService.SaveAsync(PersistedTrackAddedAtKey, trimmedSnapshot, cancellationToken).ConfigureAwait(false);
                }
            }

            var savedTracks = await _storageService.LoadAsync<List<TrackItem>>(
                PersistedCatalogKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            bool catalogDirty = false;
            if (savedTracks is { Count: > 0 })
            {
                foreach (var track in savedTracks)
                {
                    if (!string.IsNullOrWhiteSpace(track.AudioSourceUri) && File.Exists(track.AudioSourceUri))
                    {
                        bool hasLegacyPlaceholder = !string.IsNullOrWhiteSpace(track.CoverArtUri) &&
                            track.CoverArtUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase);
                        long resolvedAddedMs = track.DateAddedMs > 0
                            ? track.DateAddedMs
                            : ResolveLocalFileAddedAtMs(track.AudioSourceUri);

                        if (hasLegacyPlaceholder || track.DateAddedMs <= 0)
                        {
                            catalogDirty = true;
                            _tracks.Add(new TrackItem
                            {
                                Id = track.Id,
                                Title = track.Title,
                                Artist = track.Artist,
                                Album = track.Album,
                                Duration = track.Duration,
                                CoverArtUri = hasLegacyPlaceholder ? string.Empty : track.CoverArtUri,
                                AudioSourceUri = track.AudioSourceUri,
                                SourceType = track.SourceType,
                                IsLiked = track.IsLiked,
                                IsExplicit = track.IsExplicit,
                                DateAddedMs = resolvedAddedMs,
                                QualityBadge = track.QualityBadge,
                                Genre = track.Genre,
                                ReleaseYear = track.ReleaseYear,
                                TrackNumber = track.TrackNumber,
                                Lyrics = track.Lyrics
                            });
                        }
                        else
                        {
                            _tracks.Add(track);
                        }
                    }
                    else
                    {
                        catalogDirty = true;
                    }
                }
            }

            // 2. If catalog is empty on first run, automatically scan the user's Music directory if any audio files exist
            if (_tracks.Count == 0)
            {
                var userMusicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (!string.IsNullOrWhiteSpace(userMusicDir) && Directory.Exists(userMusicDir))
                {
                    await Task.Run(() =>
                    {
                        var files = EnumerateAudioFilesSafe(userMusicDir);
                        foreach (var file in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var track = CreateTrackFromFile(file);
                            _tracks.Add(track);
                        }
                    }, cancellationToken).ConfigureAwait(false);

                    if (_tracks.Count > 0)
                    {
                        catalogDirty = true;
                    }
                }
            }

            RebuildAlbumsAndArtistsFromTracks();

            if (catalogDirty && (_tracks.Count > 0 || savedTracks is { Count: > 0 }))
            {
                await _storageService.SaveAsync(PersistedCatalogKey, _tracks, cancellationToken).ConfigureAwait(false);
            }

            // 3. Load persisted Recently Played history (album-deduplicated) & Liked Online tracks
            // Defer reading atv_counterparts_cache.json lazily only if an online persisted track needs it
            var atvCounterparts = new Lazy<Dictionary<string, string>>(() => LoadAtvCounterpartMapSafe(appDataRoot));

            var savedRecent = await _storageService.LoadAsync<List<TrackItem>>(
                PersistedRecentlyPlayedKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            var sanitizedLegacyRecent = new List<TrackItem>();
            bool legacyRecentChanged = false;
            if (savedRecent is { Count: > 0 })
            {
                foreach (var rawItem in savedRecent.Take(MaxPersistedRecentAlbums))
                {
                    var sanitized = SanitizePersistedOnlineTrack(rawItem, atvCounterparts, out bool itemChanged);
                    legacyRecentChanged |= itemChanged;
                    if (!sanitizedLegacyRecent.Any(existing => IsSameAlbumOrTrack(existing, sanitized)))
                    {
                        sanitizedLegacyRecent.Add(sanitized);
                    }
                    else
                    {
                        legacyRecentChanged = true;
                    }
                }
            }

            var savedRecentAlbums = await _storageService.LoadAsync<List<RecentAlbumItem>>(
                PersistedRecentlyPlayedAlbumsKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            bool albumsChanged = false;
            _recentlyPlayedAlbums.Clear();

            if (savedRecentAlbums is { Count: > 0 })
            {
                foreach (var rawAlbum in savedRecentAlbums.Take(MaxPersistedRecentAlbums))
                {
                    var sanitizedAlbum = SanitizePersistedRecentAlbum(rawAlbum, atvCounterparts, out bool albChanged);
                    albumsChanged |= albChanged;
                    if (sanitizedAlbum is null)
                    {
                        albumsChanged = true;
                        continue;
                    }

                    int existingIdx = _recentlyPlayedAlbums.FindIndex(a => a.MatchesAlbum(sanitizedAlbum));
                    if (existingIdx < 0)
                    {
                        _recentlyPlayedAlbums.Add(sanitizedAlbum);
                    }
                    else
                    {
                        // Earlier entry in the persisted list is newer; keep its position and backfill any missing fields
                        _recentlyPlayedAlbums[existingIdx] = sanitizedAlbum.MergeWithNewer(_recentlyPlayedAlbums[existingIdx]);
                        albumsChanged = true;
                    }
                }
            }
            else if (sanitizedLegacyRecent.Count > 0)
            {
                // Migrate existing persisted Recently Played tracks into deduplicated albums on first load
                foreach (var legacyTrack in sanitizedLegacyRecent)
                {
                    var migrated = CreateRecentAlbumFromTrack(legacyTrack);
                    int existingIdx = _recentlyPlayedAlbums.FindIndex(a => a.MatchesAlbum(migrated));
                    if (existingIdx < 0)
                    {
                        _recentlyPlayedAlbums.Add(migrated);
                    }
                    else
                    {
                        _recentlyPlayedAlbums[existingIdx] = migrated.MergeWithNewer(_recentlyPlayedAlbums[existingIdx]);
                    }
                }

                albumsChanged = _recentlyPlayedAlbums.Count > 0;
            }

            _recentlyPlayedTracks.Clear();
            if (_recentlyPlayedAlbums.Count > 0)
            {
                foreach (var alb in _recentlyPlayedAlbums)
                {
                    if (alb.SeedTrack is not null)
                    {
                        _recentlyPlayedTracks.Add(alb.SeedTrack);
                    }
                }
            }
            else if (sanitizedLegacyRecent.Count > 0)
            {
                _recentlyPlayedTracks.AddRange(sanitizedLegacyRecent);
            }

            if (albumsChanged)
            {
                await _storageService.SaveAsync(PersistedRecentlyPlayedAlbumsKey, _recentlyPlayedAlbums, cancellationToken).ConfigureAwait(false);
            }

            if (legacyRecentChanged)
            {
                await _storageService.SaveAsync(PersistedRecentlyPlayedKey, _recentlyPlayedTracks, cancellationToken).ConfigureAwait(false);
            }

            var savedLikedOnline = await _storageService.LoadAsync<List<TrackItem>>(
                PersistedLikedOnlineTracksKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            if (savedLikedOnline is { Count: > 0 })
            {
                _likedOnlineTracks.Clear();
                _likedOnlineTrackIds.Clear();
                bool likedChanged = false;
                foreach (var rawItem in savedLikedOnline)
                {
                    var sanitized = SanitizePersistedOnlineTrack(rawItem, atvCounterparts, out bool itemChanged);
                    likedChanged |= itemChanged;
                    sanitized.IsLiked = true;
                    if (_likedOnlineTrackIds.Add(sanitized.Id))
                    {
                        _likedOnlineTracks.Add(sanitized);
                    }
                    else
                    {
                        likedChanged = true;
                    }
                }

                if (likedChanged)
                {
                    await _storageService.SaveAsync(PersistedLikedOnlineTracksKey, _likedOnlineTracks, cancellationToken).ConfigureAwait(false);
                }
            }

            // 4. Load persisted local music directories & user playlists
            var savedDirs = await _storageService.LoadAsync<List<string>>(
                PersistedLocalDirectoriesKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            _localMusicDirectories.Clear();
            if (savedDirs is not null)
            {
                foreach (var dir in savedDirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)))
                {
                    if (!_localMusicDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    {
                        _localMusicDirectories.Add(dir);
                    }
                }
            }
            else
            {
                var myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (!string.IsNullOrWhiteSpace(myMusic) && Directory.Exists(myMusic))
                {
                    _localMusicDirectories.Add(myMusic);
                }
            }

            var savedPlaylists = await _storageService.LoadAsync<List<UserPlaylist>>(
                PersistedPlaylistsKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);
            _playlists.Clear();
            if (savedPlaylists is { Count: > 0 })
            {
                // Ensure only user-saved playlists are loaded into permanent library
                _playlists.AddRange(savedPlaylists.Where(p => !p.IsSmartSuggestion));
            }

            await LoadPlaybackHistoryOnInitAsync(cancellationToken).ConfigureAwait(false);

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize local media catalog.");
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<CatalogQueryResult> QueryCatalogAsync(
        string? searchQuery,
        SearchCategoryFilter category,
        MediaSourceType sourceType,
        NavigationSection section,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var query = searchQuery?.Trim() ?? string.Empty;
        bool hasSearch = !string.IsNullOrEmpty(query);

        // 1. Online mode (YouTube Music): full personalized/charts Library catalog when browsing, or direct YTM search when searching
        if (sourceType == MediaSourceType.Online)
        {
            var onlineCatalog = hasSearch
                ? await QueryOnlineSearchCatalogAsync(query, cancellationToken).ConfigureAwait(false)
                : await GetOrBuildOnlineLibraryCatalogAsync(cancellationToken).ConfigureAwait(false);

            var syncedOnline = ApplyLiveTrackState(onlineCatalog, section);
            var onlineRecentlyAdded = BuildRecentlyAddedTracks(
                syncedOnline.Songs,
                includeLocalLiked: false,
                includeOnlineLiked: true,
                query);

            return FilterResultByCategory(syncedOnline, onlineRecentlyAdded, category);
        }

        // 2. Local catalog snapshot & enrichment
        var localCatalog = await QueryLocalCatalogCoreAsync(query, hasSearch, section, cancellationToken).ConfigureAwait(false);

        if (sourceType == MediaSourceType.Local)
        {
            // Keep Search bar fallback when user searches in Local mode and gets zero local songs
            if (hasSearch && localCatalog.Songs.Count == 0 && section != NavigationSection.Library)
            {
                var ytFallback = await QueryOnlineSearchCatalogAsync(query, cancellationToken).ConfigureAwait(false);
                var syncedFallback = ApplyLiveTrackState(ytFallback, section);
                var fallbackRecent = BuildRecentlyAddedTracks(
                    syncedFallback.Songs,
                    includeLocalLiked: false,
                    includeOnlineLiked: true,
                    query);
                return FilterResultByCategory(syncedFallback, fallbackRecent, category);
            }

            var localRecentlyAdded = BuildRecentlyAddedTracks(
                localCatalog.Songs,
                includeLocalLiked: true,
                includeOnlineLiked: false,
                query);

            return FilterResultByCategory(localCatalog, localRecentlyAdded, category);
        }

        // 3. All mode (Combined Local + YouTube Music, deduplicated 1:1 with Android's HomeViewModel merge)
        var onlineForAll = hasSearch
            ? await QueryOnlineSearchCatalogAsync(query, cancellationToken).ConfigureAwait(false)
            : await GetOrBuildOnlineLibraryCatalogAsync(cancellationToken).ConfigureAwait(false);
        var syncedOnlineForAll = ApplyLiveTrackState(onlineForAll, section);

        var mergedSongs = MergeAndDeduplicateSongs(localCatalog.Songs, syncedOnlineForAll.Songs);
        var mergedAlbums = MergeAndDeduplicateAlbums(localCatalog.Albums, syncedOnlineForAll.Albums);
        var mergedArtists = MergeAndDeduplicateArtists(localCatalog.Artists, syncedOnlineForAll.Artists);
        var mergedRecommended = MergeAndDeduplicateArtists(localCatalog.RecommendedArtists, syncedOnlineForAll.RecommendedArtists);

        var allRecentlyAdded = BuildRecentlyAddedTracks(
            mergedSongs,
            includeLocalLiked: true,
            includeOnlineLiked: true,
            query);

        return FilterResultByCategory(
            new CatalogQueryResult(mergedSongs, mergedAlbums, mergedArtists, mergedRecommended, allRecentlyAdded),
            allRecentlyAdded,
            category);
    }

    private static CatalogQueryResult FilterResultByCategory(
        CatalogQueryResult source,
        IReadOnlyList<TrackItem> recentlyAdded,
        SearchCategoryFilter category)
    {
        return new CatalogQueryResult(
            Songs: category is SearchCategoryFilter.All or SearchCategoryFilter.Songs ? source.Songs : [],
            Albums: category is SearchCategoryFilter.All or SearchCategoryFilter.Albums ? source.Albums : [],
            Artists: category is SearchCategoryFilter.All or SearchCategoryFilter.Artists ? source.Artists : [],
            RecommendedArtists: category is SearchCategoryFilter.All or SearchCategoryFilter.Artists ? source.RecommendedArtists : [],
            RecentlyAdded: category is SearchCategoryFilter.All or SearchCategoryFilter.Songs ? recentlyAdded : []);
    }

    private async Task<CatalogQueryResult> QueryLocalCatalogCoreAsync(
        string query,
        bool hasSearch,
        NavigationSection section,
        CancellationToken cancellationToken)
    {
        var localResult = await Task.Run(() =>
        {
            List<TrackItem> snapshotTracks;
            List<AlbumItem> snapshotAlbums;
            List<ArtistItem> snapshotArtists;

            lock (_tracks)
            {
                snapshotTracks = [.. _tracks];
                snapshotAlbums = [.. _albums];
                snapshotArtists = [.. _artists];
            }

            IEnumerable<TrackItem> filteredSongs = snapshotTracks;
            IEnumerable<AlbumItem> filteredAlbums = snapshotAlbums;
            IEnumerable<ArtistItem> filteredArtists = snapshotArtists.Where(a => !a.IsRecommended);
            IEnumerable<ArtistItem> recommended = snapshotArtists.Where(a => a.IsRecommended);

            if (section == NavigationSection.Liked)
            {
                filteredSongs = filteredSongs.Where(t => t.IsLiked);
            }

            if (hasSearch)
            {
                filteredSongs = filteredSongs.Where(t =>
                    t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Genre.Contains(query, StringComparison.OrdinalIgnoreCase));

                filteredAlbums = filteredAlbums.Where(a =>
                    a.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Artist.Contains(query, StringComparison.OrdinalIgnoreCase));

                filteredArtists = filteredArtists.Where(a =>
                    a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Genre.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            return new CatalogQueryResult(
                filteredSongs.ToList(),
                filteredAlbums.ToList(),
                filteredArtists.ToList(),
                recommended.ToList());
        }, cancellationToken).ConfigureAwait(false);

        if (localResult.Artists.Count > 0)
        {
            using var gate = new SemaphoreSlim(6, 6);
            var enrichedArtists = await Task.WhenAll(localResult.Artists.Select(async artist =>
            {
                if (!string.IsNullOrWhiteSpace(artist.AvatarUri) &&
                    (artist.AvatarUri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                     artist.AvatarUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)))
                {
                    return artist;
                }

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string? officialPortrait = await _youTubeMusicService.GetArtistAvatarUriAsync(artist.Name, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(officialPortrait))
                    {
                        var updated = new ArtistItem
                        {
                            Id = artist.Id,
                            Name = artist.Name,
                            AvatarUri = officialPortrait!,
                            Genre = artist.Genre,
                            MonthlyAudienceText = artist.MonthlyAudienceText,
                            TrackCount = artist.TrackCount,
                            AlbumCount = artist.AlbumCount,
                            IsRecommended = artist.IsRecommended,
                            SourceType = artist.SourceType
                        };

                        lock (_tracks)
                        {
                            int existingIdx = _artists.FindIndex(a => string.Equals(a.Id, artist.Id, StringComparison.Ordinal));
                            if (existingIdx >= 0)
                            {
                                _artists[existingIdx] = updated;
                            }
                        }

                        return updated;
                    }
                }
                finally
                {
                    gate.Release();
                }

                return artist;
            })).ConfigureAwait(false);

            // Warm local disk cache for remote artist portraits so WinUI <Image> never drops avatars
            var remoteArtistAvatars = enrichedArtists
                .Select(a => a.AvatarUri)
                .Where(IsRemoteHttpUri)
                .ToList();
            if (remoteArtistAvatars.Count > 0)
            {
                var warmed = await _youTubeMusicService.WarmArtworkCacheAsync(remoteArtistAvatars, cancellationToken).ConfigureAwait(false);
                if (warmed.Count > 0)
                {
                    for (int i = 0; i < enrichedArtists.Length; i++)
                    {
                        var a = enrichedArtists[i];
                        if (!string.IsNullOrWhiteSpace(a.AvatarUri) && warmed.TryGetValue(a.AvatarUri, out string? localUri))
                        {
                            enrichedArtists[i] = CloneArtistWithAvatar(a, localUri);
                        }
                    }
                }
            }

            localResult = new CatalogQueryResult(
                localResult.Songs,
                localResult.Albums,
                enrichedArtists,
                localResult.RecommendedArtists);
        }

        return localResult;
    }

    private async Task<CatalogQueryResult> QueryOnlineSearchCatalogAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var searchTask = _youTubeMusicService.SearchCatalogAsync(query, cancellationToken);
        var albumsTask = _youTubeMusicService.SearchAlbumsAsync(query, strictlyAlbumsOnly: false, cancellationToken);
        await Task.WhenAll(searchTask, albumsTask).ConfigureAwait(false);

        var ytResult = await searchTask.ConfigureAwait(false);
        var directAlbums = await albumsTask.ConfigureAwait(false);

        var combinedAlbums = new List<AlbumItem>();
        var seenAlb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alb in directAlbums.Concat(ytResult.Albums))
        {
            string key = $"{alb.Title.Trim()}::{alb.Artist.Trim()}";
            if (!string.IsNullOrWhiteSpace(alb.Title) && seenAlb.Add(key))
            {
                combinedAlbums.Add(alb);
            }
        }

        var stampedSongs = await StampOnlineAddedAtTimestampsAsync(ytResult.Songs, cancellationToken).ConfigureAwait(false);
        return await WarmCatalogArtworkAsync(
            new CatalogQueryResult(stampedSongs, combinedAlbums, ytResult.Artists, ytResult.RecommendedArtists),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogQueryResult> GetOrBuildOnlineLibraryCatalogAsync(CancellationToken cancellationToken)
    {
        var cached = _cachedOnlineLibraryCatalog;
        if (cached is not null &&
            cached.Songs.Count > 0 &&
            (DateTimeOffset.UtcNow - _onlineLibraryCachedAt) < OnlineLibraryCacheTtl)
        {
            return cached;
        }

        await _onlineLibraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedOnlineLibraryCatalog;
            if (cached is not null &&
                cached.Songs.Count > 0 &&
                (DateTimeOffset.UtcNow - _onlineLibraryCachedAt) < OnlineLibraryCacheTtl)
            {
                return cached;
            }

            var built = await BuildOnlineLibraryCatalogCoreAsync(cancellationToken).ConfigureAwait(false);
            if (built.Songs.Count > 0 || built.Albums.Count > 0 || built.Artists.Count > 0)
            {
                _cachedOnlineLibraryCatalog = built;
                _onlineLibraryCachedAt = DateTimeOffset.UtcNow;
            }

            return built;
        }
        finally
        {
            _onlineLibraryLock.Release();
        }
    }

    /// <summary>
    /// Builds the rich Online Music Library catalog aligned 1:1 with Android's MediaCatalogService:
    /// runs FEmusic_home + FEmusic_explore + seed artist profiles/albums/mixes (or FEmusic_charts + global hits when unseeded),
    /// enriches artist portraits &amp; monthly audience subtitles, stamps persistent first-seen timestamps,
    /// and warms local WIC-friendly disk cover cache via WarmArtworkCacheAsync.
    /// </summary>
    private async Task<CatalogQueryResult> BuildOnlineLibraryCatalogCoreAsync(CancellationToken cancellationToken)
    {
        List<string> seedArtists;
        string topGenre;

        lock (_recentlyPlayedAlbums)
        {
            seedArtists = _recentlyPlayedAlbums
                .Select(a => GetPrimaryArtistName(a.Artist))
                .Where(IsValidSeedArtist)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
        }

        if (seedArtists.Count < 3)
        {
            lock (_recentlyPlayedTracks)
            {
                foreach (var a in _recentlyPlayedTracks.Select(t => GetPrimaryArtistName(t.Artist)).Where(IsValidSeedArtist))
                {
                    if (!seedArtists.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        seedArtists.Add(a);
                        if (seedArtists.Count >= 3) break;
                    }
                }
            }
        }

        if (seedArtists.Count < 3)
        {
            lock (_likedOnlineTracks)
            {
                foreach (var a in _likedOnlineTracks.Select(t => GetPrimaryArtistName(t.Artist)).Where(IsValidSeedArtist))
                {
                    if (!seedArtists.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        seedArtists.Add(a);
                        if (seedArtists.Count >= 3) break;
                    }
                }
            }
        }

        if (seedArtists.Count < 3)
        {
            lock (_tracks)
            {
                foreach (var a in _tracks.Select(t => GetPrimaryArtistName(t.Artist)).Where(IsValidSeedArtist))
                {
                    if (!seedArtists.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        seedArtists.Add(a);
                        if (seedArtists.Count >= 3) break;
                    }
                }
            }
        }

        topGenre = ResolveTopSeedGenre();

        var homeFeedTask = _youTubeMusicService.FetchPersonalizedHomeFeedAsync(cancellationToken);
        var exploreTask = _youTubeMusicService.FetchExploreAndNewReleasesAsync(cancellationToken);
        var genreCatalogTask = _youTubeMusicService.SearchCatalogAsync($"{topGenre} hits songs", cancellationToken);

        var songs = new List<TrackItem>();
        var seenSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var albums = new List<AlbumItem>();
        var seenAlbumKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artists = new List<ArtistItem>();
        var seenArtistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSong(TrackItem t)
        {
            if (t is not null && !string.IsNullOrWhiteSpace(t.Title) && seenSongIds.Add(t.Id))
            {
                songs.Add(t);
            }
        }

        void AddAlbum(AlbumItem a)
        {
            if (a is null || string.IsNullOrWhiteSpace(a.Title))
            {
                return;
            }

            string key = $"{a.Title.Trim().ToLowerInvariant()}::{a.Artist.Trim().ToLowerInvariant()}";
            if (seenAlbumKeys.Add(key))
            {
                albums.Add(a);
            }
        }

        void AddArtist(ArtistItem a)
        {
            if (a is null || !IsValidSeedArtist(a.Name))
            {
                return;
            }

            if (seenArtistNames.Add(a.Name.Trim()))
            {
                artists.Add(new ArtistItem
                {
                    Id = a.Id,
                    Name = a.Name.Trim(),
                    AvatarUri = a.AvatarUri,
                    Genre = !string.IsNullOrWhiteSpace(a.Genre) ? a.Genre : "YouTube Music",
                    MonthlyAudienceText = a.MonthlyAudienceText,
                    TrackCount = a.TrackCount,
                    AlbumCount = a.AlbumCount,
                    IsRecommended = false,
                    SourceType = MediaSourceType.Online
                });
            }
        }

        if (seedArtists.Count > 0)
        {
            string recQuery = seedArtists.Count >= 2
                ? $"{seedArtists[0]} {seedArtists[1]} mix songs"
                : $"{seedArtists[0]} similar songs";

            var recCatalogTask = _youTubeMusicService.SearchCatalogAsync(recQuery, cancellationToken);
            var profileTasks = seedArtists
                .Take(3)
                .Select(a => SafeFetchArtistProfileAsync(a, cancellationToken))
                .ToList();
            var albumSearchTasks = seedArtists
                .Take(2)
                .Select(a => _youTubeMusicService.SearchAlbumsAsync(a, strictlyAlbumsOnly: true, cancellationToken))
                .ToList();

            await Task.WhenAll(
                homeFeedTask,
                exploreTask,
                genreCatalogTask,
                recCatalogTask,
                Task.WhenAll(profileTasks),
                Task.WhenAll(albumSearchTasks)).ConfigureAwait(false);

            var homeFeed = await homeFeedTask.ConfigureAwait(false);
            var (exploreReleases, _) = await exploreTask.ConfigureAwait(false);
            var genreCatalog = await genreCatalogTask.ConfigureAwait(false);
            var recCatalog = await recCatalogTask.ConfigureAwait(false);
            var seedProfiles = profileTasks.Select(t => t.Result).Where(p => p is not null).Cast<ArtistDetailProfile>().ToList();
            var seedAlbums = albumSearchTasks.SelectMany(t => t.Result).ToList();

            // Tracks: FEmusic_home + recCatalog + seed top tracks + genreCatalog (~50-75 rich songs)
            foreach (var t in homeFeed.PersonalizedTracks) AddSong(t);
            foreach (var t in recCatalog.Songs) AddSong(t);
            foreach (var profile in seedProfiles)
            {
                foreach (var t in profile.TopTracks.Take(8)) AddSong(t);
            }
            foreach (var t in genreCatalog.Songs) AddSong(t);

            // Artists: similarArtists from seed profiles (carry YTM monthly audience!) + seed artists + recCatalog + genreCatalog
            foreach (var profile in seedProfiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.Name))
                {
                    AddArtist(new ArtistItem
                    {
                        Id = $"ytm_art_{ComputeShortHash(profile.Name)}",
                        Name = profile.Name,
                        AvatarUri = profile.AvatarUri,
                        Genre = !string.IsNullOrWhiteSpace(profile.MonthlyListenersText) ? profile.MonthlyListenersText : "YouTube Music",
                        MonthlyAudienceText = profile.MonthlyListenersText,
                        IsRecommended = false,
                        SourceType = MediaSourceType.Online
                    });
                }

                foreach (var sim in profile.SimilarArtists)
                {
                    AddArtist(sim);
                }
            }

            foreach (var a in recCatalog.Artists) AddArtist(a);
            foreach (var a in genreCatalog.Artists) AddArtist(a);

            // Albums: FEmusic_home + seed profile discography + dedicated artist album searches + recCatalog + explore new releases + genreCatalog
            foreach (var a in homeFeed.PersonalizedAlbums) AddAlbum(a);
            foreach (var a in seedAlbums) AddAlbum(a);
            foreach (var profile in seedProfiles)
            {
                foreach (var a in profile.CombinedAlbums.Take(6)) AddAlbum(a);
            }
            foreach (var a in recCatalog.Albums) AddAlbum(a);
            foreach (var nr in exploreReleases.Where(r => r.IsAlbum && r.HasArtwork).Take(12))
            {
                _ = int.TryParse(nr.Year, out int nrYear);
                AddAlbum(new AlbumItem
                {
                    Id = !string.IsNullOrWhiteSpace(nr.BrowseId) ? $"ytm_alb_{nr.BrowseId}" : nr.Id,
                    BrowseId = nr.BrowseId ?? string.Empty,
                    Title = nr.Title,
                    Artist = nr.Artist,
                    ReleaseYear = nrYear > 1900 ? nrYear : 0,
                    TrackCount = 0,
                    CoverArtUri = nr.ArtworkUri,
                    Subtitle = nr.Subtitle,
                    SourceType = MediaSourceType.Online
                });
            }
            foreach (var a in genreCatalog.Albums) AddAlbum(a);
        }
        else
        {
            var chartsTask = _youTubeMusicService.FetchChartsCatalogAsync(cancellationToken);
            await Task.WhenAll(homeFeedTask, exploreTask, genreCatalogTask, chartsTask).ConfigureAwait(false);

            var homeFeed = await homeFeedTask.ConfigureAwait(false);
            var (exploreReleases, _) = await exploreTask.ConfigureAwait(false);
            var genreCatalog = await genreCatalogTask.ConfigureAwait(false);
            var charts = await chartsTask.ConfigureAwait(false);

            foreach (var t in homeFeed.PersonalizedTracks) AddSong(t);
            foreach (var t in charts.Songs) AddSong(t);
            foreach (var t in genreCatalog.Songs) AddSong(t);

            foreach (var a in charts.Artists) AddArtist(a);
            foreach (var a in genreCatalog.Artists) AddArtist(a);

            foreach (var a in homeFeed.PersonalizedAlbums) AddAlbum(a);
            foreach (var a in charts.Albums) AddAlbum(a);
            foreach (var nr in exploreReleases.Where(r => r.IsAlbum && r.HasArtwork).Take(12))
            {
                _ = int.TryParse(nr.Year, out int nrYear);
                AddAlbum(new AlbumItem
                {
                    Id = !string.IsNullOrWhiteSpace(nr.BrowseId) ? $"ytm_alb_{nr.BrowseId}" : nr.Id,
                    BrowseId = nr.BrowseId ?? string.Empty,
                    Title = nr.Title,
                    Artist = nr.Artist,
                    ReleaseYear = nrYear > 1900 ? nrYear : 0,
                    TrackCount = 0,
                    CoverArtUri = nr.ArtworkUri,
                    Subtitle = nr.Subtitle,
                    SourceType = MediaSourceType.Online
                });
            }
            foreach (var a in genreCatalog.Albums) AddAlbum(a);
        }

        // Enrich top online artists missing an official portrait or monthly audience metric
        var finalArtists = artists.Take(24).ToList();
        using (var gate = new SemaphoreSlim(6, 6))
        {
            var enriched = await Task.WhenAll(finalArtists.Select(async artist =>
            {
                bool needsAvatar = !artist.HasCoverArt;
                bool needsAudience = string.IsNullOrWhiteSpace(artist.MonthlyAudienceText);
                if (!needsAvatar && !needsAudience)
                {
                    return artist;
                }

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (officialAvatar, audience) = await _youTubeMusicService
                        .GetArtistSummaryAsync(artist.Name, cancellationToken)
                        .ConfigureAwait(false);

                    string resolvedAvatar = !string.IsNullOrWhiteSpace(officialAvatar) ? officialAvatar! : artist.AvatarUri;
                    string resolvedAudience = !string.IsNullOrWhiteSpace(audience) ? audience! : artist.MonthlyAudienceText;
                    if (!string.Equals(resolvedAvatar, artist.AvatarUri, StringComparison.Ordinal) ||
                        !string.Equals(resolvedAudience, artist.MonthlyAudienceText, StringComparison.Ordinal))
                    {
                        return new ArtistItem
                        {
                            Id = artist.Id,
                            Name = artist.Name,
                            AvatarUri = resolvedAvatar,
                            Genre = !string.IsNullOrWhiteSpace(resolvedAudience) ? resolvedAudience : artist.Genre,
                            MonthlyAudienceText = resolvedAudience,
                            TrackCount = artist.TrackCount,
                            AlbumCount = artist.AlbumCount,
                            IsRecommended = artist.IsRecommended,
                            SourceType = MediaSourceType.Online
                        };
                    }
                }
                catch
                {
                    // Keep original artist on transient enrichment error
                }
                finally
                {
                    gate.Release();
                }

                return artist;
            })).ConfigureAwait(false);

            finalArtists = [.. enriched];
        }

        var stampedSongs = await StampOnlineAddedAtTimestampsAsync(songs.Take(80).ToList(), cancellationToken).ConfigureAwait(false);
        var finalAlbums = albums.Take(40).ToList();

        // Enrich real track counts directly from YouTube Music album browse metadata (never fabricate "10 tracks")
        using (var albumGate = new SemaphoreSlim(8, 8))
        {
            var enrichedAlbums = await Task.WhenAll(finalAlbums.Select(async album =>
            {
                if (album.TrackCount > 0)
                {
                    return album;
                }

                await albumGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int? realCount = await _youTubeMusicService
                        .GetAlbumTrackCountAsync(album.BrowseId, album.Artist, album.Title, cancellationToken)
                        .ConfigureAwait(false);
                    if (realCount is > 0)
                    {
                        return new AlbumItem
                        {
                            Id = album.Id,
                            BrowseId = album.BrowseId,
                            Title = album.Title,
                            Artist = album.Artist,
                            ReleaseYear = album.ReleaseYear,
                            TrackCount = realCount.Value,
                            CoverArtUri = album.CoverArtUri,
                            Subtitle = album.Subtitle,
                            SourceType = album.SourceType
                        };
                    }
                }
                catch
                {
                    // Leave TrackCount = 0 so the badge stays cleanly hidden when unavailable
                }
                finally
                {
                    albumGate.Release();
                }

                return album;
            })).ConfigureAwait(false);

            finalAlbums = [.. enrichedAlbums];
        }

        var recommendedArtists = finalArtists.Take(12).ToList();

        return await WarmCatalogArtworkAsync(
            new CatalogQueryResult(stampedSongs, finalAlbums, finalArtists, recommendedArtists),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArtistDetailProfile?> SafeFetchArtistProfileAsync(string artistName, CancellationToken cancellationToken)
    {
        try
        {
            return await _youTubeMusicService.GetArtistProfileAsync(artistName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private string ResolveTopSeedGenre()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "YouTube Music", "Streaming", "Online", "Personalized", "Local Library"
        };

        lock (_recentlyPlayedTracks)
        {
            foreach (var g in _recentlyPlayedTracks.Select(t => t.Genre))
            {
                if (!string.IsNullOrWhiteSpace(g) && g.Length <= 24 && !excluded.Contains(g.Trim()))
                {
                    return g.Trim();
                }
            }
        }

        lock (_tracks)
        {
            foreach (var g in _tracks.Select(t => t.Genre))
            {
                if (!string.IsNullOrWhiteSpace(g) && g.Length <= 24 && !excluded.Contains(g.Trim()))
                {
                    return g.Trim();
                }
            }
        }

        return "Pop";
    }

    private static bool IsValidSeedArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return false;
        }

        string trimmed = artist.Trim();
        return !trimmed.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Featured Artist", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("YouTube Artist", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.All(char.IsDigit);
    }

    private CatalogQueryResult ApplyLiveTrackState(CatalogQueryResult catalog, NavigationSection section)
    {
        foreach (var song in catalog.Songs)
        {
            song.IsLiked = _likedOnlineTrackIds.Contains(song.Id);
            DecorateTrackWithLocalOfflineMatch(song);
        }

        var filteredSongs = section == NavigationSection.Liked
            ? catalog.Songs.Where(t => t.IsLiked).ToList()
            : catalog.Songs;

        return new CatalogQueryResult(
            filteredSongs,
            catalog.Albums,
            catalog.Artists,
            catalog.RecommendedArtists,
            catalog.RecentlyAdded);
    }

    private async Task<List<TrackItem>> StampOnlineAddedAtTimestampsAsync(
        IReadOnlyList<TrackItem> tracks,
        CancellationToken cancellationToken)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        long batchNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool mapChanged = false;
        var result = new List<TrackItem>(tracks.Count);

        lock (_trackAddedAtMap)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (string.IsNullOrWhiteSpace(t.Id))
                {
                    result.Add(t);
                    continue;
                }

                if (!_trackAddedAtMap.TryGetValue(t.Id, out long ts) || ts <= 0)
                {
                    ts = t.DateAddedMs > 0 ? t.DateAddedMs : (batchNowMs - (i * 1000L));
                    _trackAddedAtMap[t.Id] = ts;
                    mapChanged = true;
                }

                result.Add(t.DateAddedMs == ts ? t : CloneTrackWithAddedAt(t, ts));
            }

            if (mapChanged)
            {
                TrimTrackAddedAtMapLocked();
            }
        }

        if (mapChanged)
        {
            Dictionary<string, long> snapshot;
            lock (_trackAddedAtMap)
            {
                snapshot = new Dictionary<string, long>(_trackAddedAtMap, StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                await _storageService.SaveAsync(PersistedTrackAddedAtKey, snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore transient persistence contention
            }
        }

        return result;
    }

    private bool TrimTrackAddedAtMapLocked()
    {
        if (_trackAddedAtMap.Count <= MaxTrackAddedAtEntries)
        {
            return false;
        }

        int removeCount = _trackAddedAtMap.Count - MaxTrackAddedAtEntries;
        var oldestKeys = _trackAddedAtMap
            .OrderBy(kvp => kvp.Value)
            .Take(removeCount)
            .Select(kvp => kvp.Key)
            .ToArray();

        foreach (string key in oldestKeys)
        {
            _trackAddedAtMap.Remove(key);
        }

        return oldestKeys.Length > 0;
    }

    private async Task<CatalogQueryResult> WarmCatalogArtworkAsync(
        CatalogQueryResult catalog,
        CancellationToken cancellationToken)
    {
        var urisToWarm = catalog.Songs.Select(t => t.CoverArtUri)
            .Concat(catalog.Albums.Select(a => a.CoverArtUri))
            .Concat(catalog.Artists.Select(a => a.AvatarUri))
            .Where(IsRemoteHttpUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urisToWarm.Count == 0)
        {
            return catalog;
        }

        var warmedMap = await _youTubeMusicService.WarmArtworkCacheAsync(urisToWarm, cancellationToken).ConfigureAwait(false);
        if (warmedMap.Count == 0)
        {
            return catalog;
        }

        string Resolve(string uri) =>
            !string.IsNullOrWhiteSpace(uri) && warmedMap.TryGetValue(uri, out string? localUri)
                ? localUri
                : uri;

        var warmedSongs = catalog.Songs
            .Select(t =>
            {
                string resolved = Resolve(t.CoverArtUri);
                return string.Equals(resolved, t.CoverArtUri, StringComparison.Ordinal)
                    ? t
                    : CloneTrackWithCover(t, resolved);
            })
            .ToList();

        var warmedAlbums = catalog.Albums
            .Select(a =>
            {
                string resolved = Resolve(a.CoverArtUri);
                return string.Equals(resolved, a.CoverArtUri, StringComparison.Ordinal)
                    ? a
                    : CloneAlbumWithCover(a, resolved);
            })
            .ToList();

        var warmedArtists = catalog.Artists
            .Select(a =>
            {
                string resolved = Resolve(a.AvatarUri);
                return string.Equals(resolved, a.AvatarUri, StringComparison.Ordinal)
                    ? a
                    : CloneArtistWithAvatar(a, resolved);
            })
            .ToList();

        var warmedRecommended = catalog.RecommendedArtists
            .Select(a =>
            {
                string resolved = Resolve(a.AvatarUri);
                return string.Equals(resolved, a.AvatarUri, StringComparison.Ordinal)
                    ? a
                    : CloneArtistWithAvatar(a, resolved);
            })
            .ToList();

        return new CatalogQueryResult(warmedSongs, warmedAlbums, warmedArtists, warmedRecommended);
    }

    private IReadOnlyList<TrackItem> BuildRecentlyAddedTracks(
        IReadOnlyList<TrackItem> primarySongs,
        bool includeLocalLiked,
        bool includeOnlineLiked,
        string searchQuery)
    {
        bool hasSearch = !string.IsNullOrWhiteSpace(searchQuery);
        var byId = new Dictionary<string, TrackItem>(StringComparer.OrdinalIgnoreCase);

        void UpsertCandidate(TrackItem t)
        {
            if (t is null || string.IsNullOrWhiteSpace(t.Id))
            {
                return;
            }

            if (hasSearch &&
                !t.Title.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !t.Artist.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !t.Album.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) &&
                !t.Genre.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!byId.TryGetValue(t.Id, out var existing))
            {
                byId[t.Id] = t;
            }
            else if (t.DateAddedMs > 0 && existing.DateAddedMs <= 0)
            {
                byId[t.Id] = t;
            }
        }

        foreach (var t in primarySongs) UpsertCandidate(t);

        if (includeLocalLiked)
        {
            lock (_tracks)
            {
                foreach (var t in _tracks.Where(x => x.IsLiked))
                {
                    UpsertCandidate(t);
                }
            }
        }

        if (includeOnlineLiked)
        {
            lock (_likedOnlineTracks)
            {
                foreach (var t in _likedOnlineTracks)
                {
                    UpsertCandidate(t);
                }
            }
        }

        return byId.Values
            .OrderByDescending(t => t.DateAddedMs)
            .ThenBy(t => t.Title.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static List<TrackItem> MergeAndDeduplicateSongs(
        IReadOnlyList<TrackItem> localSongs,
        IReadOnlyList<TrackItem> onlineSongs)
    {
        var merged = new List<TrackItem>(localSongs.Count + onlineSongs.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in localSongs.Concat(onlineSongs))
        {
            if (seenIds.Add(t.Id))
            {
                merged.Add(t);
            }
        }

        return merged;
    }

    private static List<AlbumItem> MergeAndDeduplicateAlbums(
        IReadOnlyList<AlbumItem> localAlbums,
        IReadOnlyList<AlbumItem> onlineAlbums)
    {
        var merged = new List<AlbumItem>(localAlbums.Count + onlineAlbums.Count);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in localAlbums.Concat(onlineAlbums))
        {
            string key = a.Title.Trim();
            if (!string.IsNullOrWhiteSpace(key) && seenTitles.Add(key))
            {
                merged.Add(a);
            }
        }

        return merged;
    }

    private static List<ArtistItem> MergeAndDeduplicateArtists(
        IReadOnlyList<ArtistItem> localArtists,
        IReadOnlyList<ArtistItem> onlineArtists)
    {
        var merged = new List<ArtistItem>(localArtists.Count + onlineArtists.Count);
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in localArtists)
        {
            string key = a.Name.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !indexByName.ContainsKey(key))
            {
                indexByName[key] = merged.Count;
                merged.Add(a);
            }
        }

        foreach (var online in onlineArtists)
        {
            string key = online.Name.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (indexByName.TryGetValue(key, out int existingIdx))
            {
                var existing = merged[existingIdx];
                if ((!existing.HasCoverArt && online.HasCoverArt) ||
                    (string.IsNullOrWhiteSpace(existing.MonthlyAudienceText) && !string.IsNullOrWhiteSpace(online.MonthlyAudienceText)))
                {
                    merged[existingIdx] = new ArtistItem
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        AvatarUri = existing.HasCoverArt ? existing.AvatarUri : online.AvatarUri,
                        Genre = !string.IsNullOrWhiteSpace(existing.Genre) ? existing.Genre : online.Genre,
                        MonthlyAudienceText = !string.IsNullOrWhiteSpace(existing.MonthlyAudienceText) ? existing.MonthlyAudienceText : online.MonthlyAudienceText,
                        TrackCount = existing.TrackCount,
                        AlbumCount = existing.AlbumCount,
                        IsRecommended = existing.IsRecommended,
                        SourceType = existing.SourceType
                    };
                }
            }
            else
            {
                indexByName[key] = merged.Count;
                merged.Add(online);
            }
        }

        return merged;
    }

    private static bool IsRemoteHttpUri(string? uri) =>
        !string.IsNullOrWhiteSpace(uri) &&
        (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static long ResolveLocalFileAddedAtMs(string filePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                var lastWrite = File.GetLastWriteTimeUtc(filePath);
                if (lastWrite.Year > 1970)
                {
                    return new DateTimeOffset(lastWrite, TimeSpan.Zero).ToUnixTimeMilliseconds();
                }

                var created = File.GetCreationTimeUtc(filePath);
                if (created.Year > 1970)
                {
                    return new DateTimeOffset(created, TimeSpan.Zero).ToUnixTimeMilliseconds();
                }
            }
        }
        catch
        {
            // Fallback to current timestamp if file stat fails
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static TrackItem CloneTrackWithAddedAt(TrackItem t, long dateAddedMs) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        Duration = t.Duration,
        CoverArtUri = t.CoverArtUri,
        AudioSourceUri = t.AudioSourceUri,
        YouTubeVideoId = t.YouTubeVideoId,
        SourceType = t.SourceType,
        IsLiked = t.IsLiked,
        IsExplicit = t.IsExplicit,
        DateAddedMs = dateAddedMs,
        QualityBadge = t.QualityBadge,
        Genre = t.Genre,
        ReleaseYear = t.ReleaseYear,
        TrackNumber = t.TrackNumber,
        PlayCountText = t.PlayCountText,
        PlayCount = t.PlayCount,
        LyricsProviderName = t.LyricsProviderName,
        Lyrics = t.Lyrics,
        IsDownloadedOffline = t.IsDownloadedOffline,
        LocalOfflineFilePath = t.LocalOfflineFilePath
    };

    private static TrackItem CloneTrackWithCover(TrackItem t, string coverArtUri) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        Duration = t.Duration,
        CoverArtUri = coverArtUri,
        AudioSourceUri = t.AudioSourceUri,
        YouTubeVideoId = t.YouTubeVideoId,
        SourceType = t.SourceType,
        IsLiked = t.IsLiked,
        IsExplicit = t.IsExplicit,
        DateAddedMs = t.DateAddedMs,
        QualityBadge = t.QualityBadge,
        Genre = t.Genre,
        ReleaseYear = t.ReleaseYear,
        TrackNumber = t.TrackNumber,
        PlayCountText = t.PlayCountText,
        PlayCount = t.PlayCount,
        LyricsProviderName = t.LyricsProviderName,
        Lyrics = t.Lyrics,
        IsDownloadedOffline = t.IsDownloadedOffline,
        LocalOfflineFilePath = t.LocalOfflineFilePath
    };

    private static AlbumItem CloneAlbumWithCover(AlbumItem alb, string coverArtUri) => new()
    {
        Id = alb.Id,
        Title = alb.Title,
        Artist = alb.Artist,
        ReleaseYear = alb.ReleaseYear,
        TrackCount = alb.TrackCount,
        CoverArtUri = coverArtUri,
        SourceType = alb.SourceType,
        BrowseId = alb.BrowseId,
        Subtitle = alb.Subtitle
    };

    private static ArtistItem CloneArtistWithAvatar(ArtistItem art, string avatarUri) => new()
    {
        Id = art.Id,
        Name = art.Name,
        AvatarUri = avatarUri,
        Genre = art.Genre,
        MonthlyAudienceText = art.MonthlyAudienceText,
        TrackCount = art.TrackCount,
        AlbumCount = art.AlbumCount,
        IsRecommended = art.IsRecommended,
        SourceType = art.SourceType
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> ScanLocalFolderAsync(
        string folderPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return [];
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var discovered = await Task.Run(() =>
        {
            var results = new List<TrackItem>();
            var files = EnumerateAudioFilesSafe(folderPath, _localScanFilterSettings);

            int count = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var track = CreateTrackFromFile(file);
                results.Add(track);
                count++;
                progress?.Report(count);
            }

            lock (_tracks)
            {
                foreach (var item in results)
                {
                    var existingIdx = _tracks.FindIndex(t =>
                        string.Equals(t.AudioSourceUri, item.AudioSourceUri, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        var liked = _tracks[existingIdx].IsLiked;
                        item.IsLiked = liked;
                        _tracks[existingIdx] = item;
                    }
                    else
                    {
                        _tracks.Add(item);
                    }
                }

                RebuildAlbumsAndArtistsFromTracks();
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);

        await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return discovered;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> ImportLocalFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var imported = await Task.Run(() =>
        {
            var results = new List<TrackItem>();
            foreach (var file in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    continue;
                }

                if (!SupportedAudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var track = CreateTrackFromFile(file);
                results.Add(track);
            }

            lock (_tracks)
            {
                foreach (var item in results)
                {
                    var existingIdx = _tracks.FindIndex(t =>
                        string.Equals(t.AudioSourceUri, item.AudioSourceUri, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        var liked = _tracks[existingIdx].IsLiked;
                        item.IsLiked = liked;
                        _tracks[existingIdx] = item;
                    }
                    else
                    {
                        _tracks.Insert(0, item);
                    }
                }

                RebuildAlbumsAndArtistsFromTracks();
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);

        await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return imported;
    }

    /// <inheritdoc />
    public async Task<bool> ToggleTrackLikedAsync(
        string trackId,
        TrackItem? trackSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        bool newState = false;
        bool isLocalFound = false;
        lock (_tracks)
        {
            var track = _tracks.FirstOrDefault(t => t.Id == trackId);
            if (track is not null)
            {
                track.IsLiked = !track.IsLiked;
                newState = track.IsLiked;
                isLocalFound = true;
            }
        }

        if (!isLocalFound)
        {
            if (!_likedOnlineTrackIds.Add(trackId))
            {
                _likedOnlineTrackIds.Remove(trackId);
                lock (_likedOnlineTracks)
                {
                    _likedOnlineTracks.RemoveAll(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
                }

                await _storageService.SaveAsync(PersistedLikedOnlineTracksKey, _likedOnlineTracks.ToList(), cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (trackSnapshot is not null)
            {
                trackSnapshot.IsLiked = true;
                lock (_likedOnlineTracks)
                {
                    _likedOnlineTracks.RemoveAll(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
                    _likedOnlineTracks.Insert(0, trackSnapshot);
                }

                await _storageService.SaveAsync(PersistedLikedOnlineTracksKey, _likedOnlineTracks.ToList(), cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return newState;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> GetLikedSongsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var liked = new List<TrackItem>();
        lock (_tracks)
        {
            liked.AddRange(_tracks.Where(t => t.IsLiked));
        }

        lock (_likedOnlineTracks)
        {
            foreach (var online in _likedOnlineTracks)
            {
                if (liked.All(t => !string.Equals(t.Id, online.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    online.IsLiked = true;
                    liked.Add(online);
                }
            }
        }

        return liked;
    }

    /// <inheritdoc />
    public async Task RecordTrackPlayedAsync(TrackItem track, CancellationToken cancellationToken = default)
    {
        if (track is null || string.IsNullOrWhiteSpace(track.Title) || track.Id == "empty_state_track")
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var incomingAlbum = CreateRecentAlbumFromTrack(track);

        List<RecentAlbumItem> albumSnapshot;
        lock (_recentlyPlayedAlbums)
        {
            int existingIdx = _recentlyPlayedAlbums.FindIndex(a => a.MatchesAlbum(incomingAlbum));
            RecentAlbumItem entryToInsert = existingIdx >= 0
                ? _recentlyPlayedAlbums[existingIdx].MergeWithNewer(incomingAlbum)
                : incomingAlbum;

            _recentlyPlayedAlbums.RemoveAll(a => a.MatchesAlbum(incomingAlbum));
            _recentlyPlayedAlbums.Insert(0, entryToInsert);

            if (_recentlyPlayedAlbums.Count > MaxPersistedRecentAlbums)
            {
                _recentlyPlayedAlbums.RemoveRange(
                    MaxPersistedRecentAlbums,
                    _recentlyPlayedAlbums.Count - MaxPersistedRecentAlbums);
            }

            albumSnapshot = [.. _recentlyPlayedAlbums];
        }

        List<TrackItem> trackSnapshot;
        lock (_recentlyPlayedTracks)
        {
            _recentlyPlayedTracks.RemoveAll(t => IsSameAlbumOrTrack(t, track));
            _recentlyPlayedTracks.Insert(0, track);
            if (_recentlyPlayedTracks.Count > MaxPersistedRecentAlbums)
            {
                _recentlyPlayedTracks.RemoveRange(
                    MaxPersistedRecentAlbums,
                    _recentlyPlayedTracks.Count - MaxPersistedRecentAlbums);
            }

            trackSnapshot = [.. _recentlyPlayedTracks];
        }

        try
        {
            await _storageService.SaveAsync(PersistedRecentlyPlayedAlbumsKey, albumSnapshot, cancellationToken).ConfigureAwait(false);
            await _storageService.SaveAsync(PersistedRecentlyPlayedKey, trackSnapshot, cancellationToken).ConfigureAwait(false);
            await RecordPlaybackHistoryEventAsync(track, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore transient storage lock
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecentAlbumItem> GetRecentlyPlayedAlbumsSnapshot()
    {
        lock (_recentlyPlayedAlbums)
        {
            return [.. _recentlyPlayedAlbums];
        }
    }

    /// <inheritdoc />
    public async Task<HomeDiscoveryFeed> GetHomeDiscoveryFeedAsync(
        string? selectedGenreTag = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<RecentAlbumItem> recentAlbums;
        List<TrackItem> recentList;
        List<TrackItem> localSnapshot;
        List<TrackItem> likedSnapshot;
        List<AlbumItem> albumsSnapshot;

        lock (_recentlyPlayedAlbums)
        {
            recentAlbums = [.. _recentlyPlayedAlbums];
        }

        lock (_recentlyPlayedTracks)
        {
            recentList = [.. _recentlyPlayedTracks];
        }

        lock (_tracks)
        {
            localSnapshot = [.. _tracks];
        }

        lock (_likedOnlineTracks)
        {
            likedSnapshot = [.. _likedOnlineTracks];
        }

        lock (_albums)
        {
            albumsSnapshot = [.. _albums];
        }

        if (recentAlbums.Count == 0 && recentList.Count > 0)
        {
            foreach (var t in recentList)
            {
                var cand = CreateRecentAlbumFromTrack(t);
                if (recentAlbums.All(a => !a.MatchesAlbum(cand)))
                {
                    recentAlbums.Add(cand);
                }
            }
        }

        // Extract seed artists strictly from real user listening history (Recently Played + Liked).
        // When fresh (no playback history yet), keep seedArtists empty and load YouTube Music's general feed.
        var seedArtists = recentAlbums
            .Select(a => GetPrimaryArtistName(a.Artist))
            .Concat(recentList.Select(t => GetPrimaryArtistName(t.Artist)))
            .Concat(likedSnapshot.Select(t => GetPrimaryArtistName(t.Artist)))
            .Where(IsValidSeedArtist)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        bool hasUserSeeds = seedArtists.Count > 0;
        string? primarySeedArtist = hasUserSeeds ? seedArtists[0] : null;

        var excludedGenreLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "YouTube Music",
            "Streaming",
            "Online",
            "Personalized"
        };

        var genreTags = new List<string>();
        foreach (var g in recentList.Select(t => t.Genre))
        {
            if (!string.IsNullOrWhiteSpace(g) &&
                g.Length <= 24 &&
                !excludedGenreLabels.Contains(g.Trim()) &&
                !genreTags.Contains(g, StringComparer.OrdinalIgnoreCase))
            {
                genreTags.Add(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(g.ToLowerInvariant()));
            }
        }

        foreach (string defaultGenre in new[] { "Pop", "Hip-Hop", "Rock", "Electronic", "R&B", "Latin", "Alternative" })
        {
            if (genreTags.Count < 7 && !genreTags.Contains(defaultGenre, StringComparer.OrdinalIgnoreCase))
            {
                genreTags.Add(defaultGenre);
            }
        }

        string activeGenre = !string.IsNullOrWhiteSpace(selectedGenreTag)
            ? genreTags.FirstOrDefault(g => g.Equals(selectedGenreTag, StringComparison.OrdinalIgnoreCase))
              ?? genreTags.FirstOrDefault()
              ?? "Pop"
            : genreTags.FirstOrDefault() ?? "Pop";

        // 2. Fetch Home feed, Explore releases/radios, and either user-seeded recommendations (when history exists)
        //    or YouTube Music's general Charts/Home feed (when fresh with no listening history yet).
        var emptyCatalog = new CatalogQueryResult([], [], [], []);

        Task<CatalogQueryResult> recTask;
        Task<CatalogQueryResult> artistSearchTask;
        Task<ArtistDetailProfile?> seedArtistProfileTask;
        Task<IReadOnlyList<RecommendedPlaylistItem>> seedPlaylistTask;
        Task<CatalogQueryResult> chartsTask;

        if (hasUserSeeds && primarySeedArtist is not null)
        {
            string recQuery = seedArtists.Count >= 2
                ? $"{seedArtists[0]} {seedArtists[1]} mix songs"
                : $"{primarySeedArtist} similar songs";

            recTask = _youTubeMusicService.SearchCatalogAsync(recQuery, cancellationToken);
            artistSearchTask = _youTubeMusicService.SearchCatalogAsync($"{primarySeedArtist} related artists", cancellationToken);
            seedArtistProfileTask = _youTubeMusicService.GetArtistProfileAsync(primarySeedArtist, cancellationToken);
            seedPlaylistTask = _youTubeMusicService.SearchPlaylistsAsync($"{primarySeedArtist} mix", 6, cancellationToken);
            chartsTask = Task.FromResult(emptyCatalog);
        }
        else
        {
            recTask = Task.FromResult(emptyCatalog);
            artistSearchTask = Task.FromResult(emptyCatalog);
            seedArtistProfileTask = Task.FromResult<ArtistDetailProfile?>(null);
            seedPlaylistTask = Task.FromResult<IReadOnlyList<RecommendedPlaylistItem>>([]);
            chartsTask = _youTubeMusicService.FetchChartsCatalogAsync(cancellationToken);
        }

        var genreTask = _youTubeMusicService.SearchCatalogAsync($"{activeGenre} hits songs", cancellationToken);
        var accountRecTask = _youTubeMusicService.IsAuthenticated
            ? _youTubeMusicService.GetAccountPersonalizedRecommendationsAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<TrackItem>>([]);
        var homeFeedTask = _youTubeMusicService.FetchPersonalizedHomeFeedAsync(cancellationToken);
        var exploreAndReleasesTask = _youTubeMusicService.FetchExploreAndNewReleasesAsync(cancellationToken);
        var genrePlaylistTask = _youTubeMusicService.SearchPlaylistsAsync($"{activeGenre} hits", 6, cancellationToken);

        await Task.WhenAll(
            recTask,
            genreTask,
            artistSearchTask,
            seedArtistProfileTask,
            accountRecTask,
            homeFeedTask,
            exploreAndReleasesTask,
            seedPlaylistTask,
            genrePlaylistTask,
            chartsTask).ConfigureAwait(false);

        var recCatalog = await recTask.ConfigureAwait(false);
        var genreCatalog = await genreTask.ConfigureAwait(false);
        var relatedCatalog = await artistSearchTask.ConfigureAwait(false);
        var seedArtistProfile = await seedArtistProfileTask.ConfigureAwait(false);
        var accountPersonalizedTracks = await accountRecTask.ConfigureAwait(false);
        var homeFeed = await homeFeedTask.ConfigureAwait(false);
        var (exploreReleases, exploreRadios) = await exploreAndReleasesTask.ConfigureAwait(false);
        var seedPlaylists = await seedPlaylistTask.ConfigureAwait(false);
        var genrePlaylists = await genrePlaylistTask.ConfigureAwait(false);
        var chartsCatalog = await chartsTask.ConfigureAwait(false);

        // Combine Home feed tracks, account feed, and either personalized recommendations or general charts
        var combinedRecommendedSongs = new List<TrackItem>();
        foreach (var s in homeFeed.PersonalizedTracks
                     .Concat(accountPersonalizedTracks)
                     .Concat(recCatalog.Songs)
                     .Concat(chartsCatalog.Songs))
        {
            DecorateTrackWithLocalOfflineMatch(s);
            if (combinedRecommendedSongs.All(x => !string.Equals(x.Title, s.Title, StringComparison.OrdinalIgnoreCase)))
            {
                combinedRecommendedSongs.Add(s);
            }
        }

        foreach (var gs in genreCatalog.Songs)
        {
            DecorateTrackWithLocalOfflineMatch(gs);
        }

        foreach (var rs in recentList)
        {
            DecorateTrackWithLocalOfflineMatch(rs);
        }

        // Build related/featured artists from real user seeds when available, otherwise from general YouTube Music charts
        var relatedArtists = new List<ArtistItem>();
        if (seedArtistProfile?.SimilarArtists is { Count: > 0 })
        {
            foreach (var sim in seedArtistProfile.SimilarArtists)
            {
                if (relatedArtists.All(x => !string.Equals(x.Name, sim.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    relatedArtists.Add(sim);
                }
            }
        }

        foreach (var a in relatedCatalog.Artists
                     .Concat(recCatalog.Artists)
                     .Concat(recCatalog.RecommendedArtists)
                     .Concat(chartsCatalog.Artists)
                     .Concat(hasUserSeeds ? [] : genreCatalog.Artists))
        {
            if (relatedArtists.All(x => !string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
            {
                relatedArtists.Add(a);
            }
        }

        // Ensure all top related artists have official portraits rather than album covers
        for (int i = 0; i < Math.Min(12, relatedArtists.Count); i++)
        {
            var art = relatedArtists[i];
            var (officialAvatar, audience) = await _youTubeMusicService.GetArtistSummaryAsync(art.Name, cancellationToken).ConfigureAwait(false);
            string resolvedAvatar = !string.IsNullOrWhiteSpace(officialAvatar) ? officialAvatar! : art.AvatarUri;
            string resolvedAudience = !string.IsNullOrWhiteSpace(audience) ? audience! : art.MonthlyAudienceText;
            if (!string.Equals(art.AvatarUri, resolvedAvatar, StringComparison.Ordinal) ||
                !string.Equals(art.MonthlyAudienceText, resolvedAudience, StringComparison.Ordinal))
            {
                relatedArtists[i] = new ArtistItem
                {
                    Id = art.Id,
                    Name = art.Name,
                    AvatarUri = resolvedAvatar,
                    Genre = !string.IsNullOrWhiteSpace(resolvedAudience) ? resolvedAudience : art.Genre,
                    MonthlyAudienceText = resolvedAudience,
                    TrackCount = art.TrackCount,
                    AlbumCount = art.AlbumCount,
                    IsRecommended = art.IsRecommended,
                    SourceType = art.SourceType
                };
            }
        }

        // Combine Recommended Albums
        var recommendedAlbums = new List<AlbumItem>();
        foreach (var alb in homeFeed.PersonalizedAlbums
                     .Concat(recCatalog.Albums)
                     .Concat(chartsCatalog.Albums)
                     .Concat(genreCatalog.Albums)
                     .Concat(hasUserSeeds ? albumsSnapshot : []))
        {
            if (recommendedAlbums.All(x => !string.Equals(x.Title, alb.Title, StringComparison.OrdinalIgnoreCase)))
            {
                recommendedAlbums.Add(alb);
            }
        }

        // 3. Build Playlists Made For You (up to 14), prioritizing items with authentic YTM artwork
        var playlistsMap = new Dictionary<string, RecommendedPlaylistItem>(StringComparer.OrdinalIgnoreCase);
        var rawPlaylists = homeFeed.PlaylistsMadeForYou.Concat(seedPlaylists).Concat(genrePlaylists).ToList();
        foreach (var pl in rawPlaylists.Where(p => p.HasArtwork).Concat(rawPlaylists.Where(p => !p.HasArtwork)))
        {
            playlistsMap.TryAdd(pl.PlaylistId, pl);
            if (playlistsMap.Count >= 14)
            {
                break;
            }
        }

        var playlistsMadeForYou = playlistsMap.Values.Take(14).ToList();

        // 4. Build Radio Stations (up to 24), ensuring valid artwork & seeding RDAMVM track mixes if needed
        var radiosMap = new Dictionary<string, RadioStationItem>(StringComparer.OrdinalIgnoreCase);
        var rawRadios = homeFeed.RadioStations.Concat(exploreRadios).ToList();
        foreach (var radio in rawRadios.Where(r => r.HasArtwork).Concat(rawRadios.Where(r => !r.HasArtwork)))
        {
            var normalizedRadio = radio;
            if (!radio.HasArtwork && !string.IsNullOrWhiteSpace(radio.SeedVideoId))
            {
                normalizedRadio = radio with { ArtworkUri = $"https://i.ytimg.com/vi/{radio.SeedVideoId}/hqdefault.jpg" };
            }

            radiosMap.TryAdd(normalizedRadio.PlaylistId, normalizedRadio);
        }

        if (radiosMap.Count < 8)
        {
            foreach (var seedTrack in recentList.Concat(combinedRecommendedSongs))
            {
                if (radiosMap.Count >= 16)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(seedTrack.YouTubeVideoId))
                {
                    continue;
                }

                string plId = $"RDAMVM{seedTrack.YouTubeVideoId}";
                string stationArt = seedTrack.HasCoverArt
                    ? seedTrack.CoverArtUri
                    : $"https://i.ytimg.com/vi/{seedTrack.YouTubeVideoId}/hqdefault.jpg";
                radiosMap.TryAdd(plId, new RadioStationItem(
                    Id: $"radio_{plId}",
                    Title: $"{seedTrack.Title} Radio",
                    Subtitle: $"Radio • Based on {seedTrack.Artist}",
                    ArtworkUri: stationArt,
                    PlaylistId: plId,
                    SeedVideoId: seedTrack.YouTubeVideoId));
            }
        }

        var radioStations = radiosMap.Values.Take(24).ToList();

        // 5. Build New Releases (up to 24), prioritizing entries with valid artwork & falling back to recent albums
        var releasesMap = new Dictionary<string, NewReleaseItem>(StringComparer.OrdinalIgnoreCase);
        var rawReleases = exploreReleases.Concat(homeFeed.NewReleases).ToList();
        foreach (var nr in rawReleases.Where(r => r.HasArtwork).Concat(rawReleases.Where(r => !r.HasArtwork)))
        {
            var normalizedRelease = nr;
            if (!nr.HasArtwork && !string.IsNullOrWhiteSpace(nr.VideoId))
            {
                normalizedRelease = nr with { ArtworkUri = $"https://i.ytimg.com/vi/{nr.VideoId}/hqdefault.jpg" };
            }

            string key = $"{normalizedRelease.Title.Trim()}::{normalizedRelease.Artist.Trim()}";
            releasesMap.TryAdd(key, normalizedRelease);
            if (releasesMap.Count >= 24)
            {
                break;
            }
        }

        if (releasesMap.Count < 8)
        {
            foreach (var alb in recommendedAlbums.OrderByDescending(a => a.ReleaseYear))
            {
                if (releasesMap.Count >= 18)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(alb.Title) ||
                    RecentAlbumItem.IsGenericAlbumTitle(alb.Title))
                {
                    continue;
                }

                string key = $"{alb.Title.Trim()}::{alb.Artist.Trim()}";
                releasesMap.TryAdd(key, new NewReleaseItem(
                    Id: $"nr_{alb.Id}",
                    Title: alb.Title,
                    Artist: alb.Artist,
                    Subtitle: $"Album • {alb.Artist}",
                    ArtworkUri: alb.CoverArtUri,
                    IsAlbum: true,
                    BrowseId: string.IsNullOrWhiteSpace(alb.BrowseId) ? null : alb.BrowseId,
                    Year: alb.ReleaseYear > 0 ? alb.ReleaseYear.ToString() : null));
            }
        }

        var newReleases = releasesMap.Values.Take(24).ToList();

        // 6. Build up to 3 "More like: [Album]" sections (up to 15 similar albums each)
        var albumSeeds = SelectDistinctMoreLikeSeeds(recentList, likedSnapshot, albumsSnapshot, recommendedAlbums);
        var moreLikeTasks = albumSeeds.Select(async seed =>
        {
            var similarAlbums = await _youTubeMusicService.GetSimilarAlbumsForSeedAsync(
                seed.Title,
                seed.Artist,
                string.IsNullOrWhiteSpace(seed.BrowseId) ? null : seed.BrowseId,
                limit: 15,
                cancellationToken).ConfigureAwait(false);

            return new MoreLikeAlbumSectionData(
                SeedAlbumTitle: seed.Title,
                SeedArtist: seed.Artist,
                SeedCoverUri: seed.CoverArtUri,
                SeedAlbum: seed,
                Albums: similarAlbums);
        }).ToList();

        var resolvedMoreLike = await Task.WhenAll(moreLikeTasks).ConfigureAwait(false);
        var moreLikeSections = resolvedMoreLike.Where(s => s.Albums.Count >= 2).ToList();

        // 7. Build Top Picks for You editorial cards faithfully from YouTube Music shelves (no fake repeated collages)
        var topPicks = BuildCuratedTopPicks(
            recentList,
            combinedRecommendedSongs,
            recommendedAlbums,
            newReleases,
            playlistsMadeForYou,
            radioStations,
            relatedArtists,
            homeFeed.FeaturedTopPicks,
            _youTubeMusicService.IsAuthenticated);

        // 8. Warm local disk cache for Home shelf artwork via pooled HTTP/2 so WinUI never drops remote covers
        var recentAlbumsSlice = recentAlbums.Take(20).ToList();
        var artworkToWarm = newReleases.Select(r => r.ArtworkUri)
            .Concat(radioStations.Select(r => r.ArtworkUri))
            .Concat(topPicks.Select(p => p.ArtworkUri))
            .Concat(playlistsMadeForYou.Select(p => p.ArtworkUri))
            .Concat(recentAlbumsSlice.Select(a => a.CoverArtUri))
            .Concat(recommendedAlbums.Take(12).Select(a => a.CoverArtUri))
            .Where(u => !string.IsNullOrWhiteSpace(u) &&
                        (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

        var cachedArtworkMap = await _youTubeMusicService
            .WarmArtworkCacheAsync(artworkToWarm, cancellationToken)
            .ConfigureAwait(false);

        string ResolveDisplayArtwork(string uri) =>
            !string.IsNullOrWhiteSpace(uri) && cachedArtworkMap.TryGetValue(uri, out string? localUri)
                ? localUri
                : uri;

        var displayNewReleases = newReleases
            .Select(nr => nr with { ArtworkUri = ResolveDisplayArtwork(nr.ArtworkUri) })
            .ToList();
        var displayRadioStations = radioStations
            .Select(r => r with { ArtworkUri = ResolveDisplayArtwork(r.ArtworkUri) })
            .ToList();
        var displayTopPicks = topPicks
            .Select(p => p with { ArtworkUri = ResolveDisplayArtwork(p.ArtworkUri) })
            .ToList();
        var displayPlaylists = playlistsMadeForYou
            .Select(p => p with { ArtworkUri = ResolveDisplayArtwork(p.ArtworkUri) })
            .ToList();
        var displayRecommendedAlbums = recommendedAlbums
            .Take(12)
            .Select(alb =>
            {
                string resolved = ResolveDisplayArtwork(alb.CoverArtUri);
                return string.Equals(resolved, alb.CoverArtUri, StringComparison.Ordinal)
                    ? alb
                    : new AlbumItem
                    {
                        Id = alb.Id,
                        Title = alb.Title,
                        Artist = alb.Artist,
                        ReleaseYear = alb.ReleaseYear,
                        TrackCount = alb.TrackCount,
                        CoverArtUri = resolved,
                        SourceType = alb.SourceType,
                        BrowseId = alb.BrowseId,
                        Subtitle = alb.Subtitle
                    };
            })
            .ToList();
        var displayRecentAlbums = recentAlbumsSlice
            .Select(alb =>
            {
                string resolved = ResolveDisplayArtwork(alb.CoverArtUri);
                return string.Equals(resolved, alb.CoverArtUri, StringComparison.Ordinal)
                    ? alb
                    : new RecentAlbumItem
                    {
                        Id = alb.Id,
                        AlbumKey = alb.AlbumKey,
                        AlbumTitle = alb.AlbumTitle,
                        Artist = alb.Artist,
                        CoverArtUri = resolved,
                        BrowseId = alb.BrowseId,
                        ReleaseYear = alb.ReleaseYear,
                        SourceType = alb.SourceType,
                        IsSingleRelease = alb.IsSingleRelease,
                        LastPlayedAt = alb.LastPlayedAt,
                        SeedTrack = alb.SeedTrack
                    };
            })
            .ToList();

        string subtitle = hasUserSeeds
            ? $"Inspired by {string.Join(", ", seedArtists.Take(3))}"
            : "Discover trending music on YouTube Music";

        _cachedRecommendedPool = combinedRecommendedSongs.Concat(genreCatalog.Songs).Take(40).ToList();
        var smartSuggestedPlaylists = await GetSmartPlaylistSuggestionsAsync(cancellationToken).ConfigureAwait(false);

        return new HomeDiscoveryFeed(
            PersonalizedSubtitle: subtitle,
            RecentlyPlayedTracks: recentList.Take(20).ToList(),
            RecommendedTracks: combinedRecommendedSongs.Take(12).ToList(),
            GenreTags: genreTags,
            SelectedGenreTag: activeGenre,
            GenreTracks: genreCatalog.Songs.Take(12).ToList(),
            RelatedArtists: relatedArtists.Take(12).ToList(),
            RecommendedAlbums: displayRecommendedAlbums,
            TopPicks: displayTopPicks,
            MoreLikeAlbumSections: moreLikeSections,
            PlaylistsMadeForYou: displayPlaylists,
            RadioStations: displayRadioStations,
            NewReleases: displayNewReleases,
            IsPersonalized: _youTubeMusicService.IsAuthenticated,
            RecentlyPlayedAlbums: displayRecentAlbums,
            SmartSuggestedPlaylists: smartSuggestedPlaylists);
    }

    private static List<AlbumItem> SelectDistinctMoreLikeSeeds(
        IReadOnlyList<TrackItem> recentList,
        IReadOnlyList<TrackItem> likedTracks,
        IReadOnlyList<AlbumItem> libraryAlbums,
        IReadOnlyList<AlbumItem> recommendedAlbums)
    {
        var seeds = new List<AlbumItem>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsUsableAlbumName(string? albumTitle) =>
            !string.IsNullOrWhiteSpace(albumTitle) &&
            !albumTitle.Equals("Single", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("Recommended Mix", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("Radio Mix", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("YouTube Music For You", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) &&
            !albumTitle.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        void TryAddAlbum(AlbumItem? candidate)
        {
            if (seeds.Count >= 3 || candidate is null || !IsUsableAlbumName(candidate.Title))
            {
                return;
            }

            if (seenTitles.Add(candidate.Title.Trim()))
            {
                seeds.Add(candidate);
            }
        }

        foreach (var track in recentList.Concat(likedTracks))
        {
            if (seeds.Count >= 3)
            {
                break;
            }

            if (!IsUsableAlbumName(track.Album))
            {
                continue;
            }

            var matchingLib = libraryAlbums.FirstOrDefault(a => string.Equals(a.Title, track.Album, StringComparison.OrdinalIgnoreCase));
            TryAddAlbum(matchingLib ?? new AlbumItem
            {
                Id = $"seed_alb_{ComputeShortHash($"{track.Artist}_{track.Album}")}",
                Title = track.Album,
                Artist = GetPrimaryArtistName(track.Artist),
                ReleaseYear = track.ReleaseYear,
                CoverArtUri = track.CoverArtUri,
                SourceType = track.SourceType
            });
        }

        foreach (var alb in libraryAlbums.Concat(recommendedAlbums))
        {
            if (seeds.Count >= 3)
            {
                break;
            }

            TryAddAlbum(alb);
        }

        return seeds;
    }

    private static List<TopPickItem> BuildCuratedTopPicks(
        IReadOnlyList<TrackItem> recentList,
        IReadOnlyList<TrackItem> recommendedSongs,
        IReadOnlyList<AlbumItem> recommendedAlbums,
        IReadOnlyList<NewReleaseItem> newReleases,
        IReadOnlyList<RecommendedPlaylistItem> playlistsMadeForYou,
        IReadOnlyList<RadioStationItem> radioStations,
        IReadOnlyList<ArtistItem> relatedArtists,
        IReadOnlyList<TopPickItem> feedTopPicks,
        bool isPersonalized,
        int maxItems = 8)
    {
        var picks = new List<TopPickItem>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddPick(TopPickItem? candidate)
        {
            if (candidate is null || picks.Count >= maxItems)
            {
                return;
            }

            // Always strip synthetic collages so authentic YTM artwork renders cleanly
            var authentic = candidate.CollageArtworks.Count > 0
                ? candidate with { CollageArtworks = [] }
                : candidate;

            if (usedIds.Add(authentic.Id))
            {
                picks.Add(authentic);
            }
        }

        static TopPickItem BuildReleasePick(NewReleaseItem nr)
        {
            string releaseSubtitle = !string.IsNullOrWhiteSpace(nr.Subtitle) &&
                                     nr.Subtitle.Contains(nr.Artist, StringComparison.OrdinalIgnoreCase)
                ? nr.Subtitle
                : $"{nr.Artist} • {nr.TypeBadgeText}";
            return new TopPickItem(
                Id: $"tp_nr_{nr.Id}",
                Eyebrow: "NEW RELEASE",
                Title: nr.Title,
                Subtitle: releaseSubtitle,
                ArtworkUri: nr.ArtworkUri,
                CollageArtworks: [],
                Kind: TopPickKind.NewRelease,
                BrowseId: nr.BrowseId,
                VideoId: nr.VideoId,
                ArtistName: nr.Artist,
                AlbumTitle: (nr.IsAlbum || !string.IsNullOrWhiteSpace(nr.BrowseId)) ? nr.Title : null,
                AmbientHexColor: "#FF2B1C24");
        }

        static TopPickItem BuildPlaylistPick(RecommendedPlaylistItem pl, string eyebrow) =>
            new(
                Id: $"tp_pl_{pl.PlaylistId}",
                Eyebrow: eyebrow,
                Title: pl.Title,
                Subtitle: pl.Subtitle,
                ArtworkUri: pl.ArtworkUri,
                CollageArtworks: [],
                Kind: TopPickKind.FeaturedPlaylist,
                BrowseId: pl.BrowseId,
                PlaylistId: pl.PlaylistId,
                AmbientHexColor: "#FF1E2036");

        // 1. Personal station / Quick Pick hero card (authentic YTM FEmusic_home Quick Pick first)
        TopPickItem? firstStationPick =
            feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.PersonalStation && p.HasArtwork)
            ?? feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.PersonalStation);

        if (firstStationPick is null)
        {
            var leadRadio = radioStations.FirstOrDefault(r => r.HasArtwork) ?? radioStations.FirstOrDefault();
            if (leadRadio is not null)
            {
                firstStationPick = new TopPickItem(
                    Id: $"tp_radio_{leadRadio.PlaylistId}",
                    Eyebrow: isPersonalized ? "PERSONAL STATION" : "FEATURED RADIO",
                    Title: leadRadio.Title,
                    Subtitle: leadRadio.Subtitle,
                    ArtworkUri: leadRadio.ArtworkUri,
                    CollageArtworks: [],
                    Kind: TopPickKind.PersonalStation,
                    PlaylistId: leadRadio.PlaylistId,
                    VideoId: leadRadio.SeedVideoId,
                    AmbientHexColor: "#FF15293A");
            }
            else
            {
                var leadTrack = recommendedSongs.FirstOrDefault(t => t.HasCoverArt)
                                ?? recentList.FirstOrDefault(t => t.HasCoverArt)
                                ?? recommendedSongs.FirstOrDefault()
                                ?? recentList.FirstOrDefault();
                if (leadTrack is not null)
                {
                    firstStationPick = new TopPickItem(
                        Id: $"tp_track_{leadTrack.Id}",
                        Eyebrow: isPersonalized ? "PICKED FOR YOU" : "TRENDING NOW",
                        Title: leadTrack.Title,
                        Subtitle: string.IsNullOrWhiteSpace(leadTrack.Album) ? leadTrack.Artist : $"{leadTrack.Artist} • {leadTrack.Album}",
                        ArtworkUri: leadTrack.CoverArtUri,
                        CollageArtworks: [],
                        Kind: TopPickKind.PersonalStation,
                        PlaylistId: !string.IsNullOrWhiteSpace(leadTrack.YouTubeVideoId) ? $"RDAMVM{leadTrack.YouTubeVideoId}" : null,
                        VideoId: leadTrack.YouTubeVideoId,
                        ArtistName: GetPrimaryArtistName(leadTrack.Artist),
                        SeedTrack: leadTrack,
                        AmbientHexColor: "#FF15293A");
                }
            }
        }

        TryAddPick(firstStationPick);

        // 2. Featured New Release card (directly from YTM home/new releases)
        TopPickItem? releasePick =
            feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.NewRelease && p.HasArtwork)
            ?? feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.NewRelease);
        if (releasePick is null)
        {
            var leadRelease = newReleases.FirstOrDefault(nr => nr.HasArtwork) ?? newReleases.FirstOrDefault();
            if (leadRelease is not null)
            {
                releasePick = BuildReleasePick(leadRelease);
            }
        }

        TryAddPick(releasePick);

        // 3. Featured Playlist / Mixed for You card (Archive Mix / Supermix / authentic YTM artwork, NEVER synthetic 2x2 collage)
        TopPickItem? playlistPick =
            feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.FeaturedPlaylist && p.HasArtwork)
            ?? feedTopPicks.FirstOrDefault(p => p.Kind == TopPickKind.FeaturedPlaylist);
        if (playlistPick is null)
        {
            var leadPlaylist = playlistsMadeForYou.FirstOrDefault(p => p.HasArtwork) ?? playlistsMadeForYou.FirstOrDefault();
            if (leadPlaylist is not null)
            {
                playlistPick = BuildPlaylistPick(leadPlaylist, "MIXED FOR YOU");
            }
        }

        TryAddPick(playlistPick);

        // 4. Artist Spotlight card with official artist portrait & monthly audience text
        var spotlightArtist = relatedArtists.FirstOrDefault(a =>
                                  a.HasCoverArt &&
                                  !string.IsNullOrWhiteSpace(a.Genre) &&
                                  (a.Genre.Contains("audience", StringComparison.OrdinalIgnoreCase) ||
                                   a.Genre.Contains("subscriber", StringComparison.OrdinalIgnoreCase) ||
                                   a.Genre.Contains("listener", StringComparison.OrdinalIgnoreCase)))
                              ?? relatedArtists.FirstOrDefault(a => a.HasCoverArt);
        if (spotlightArtist is not null)
        {
            string? artistBrowseId = spotlightArtist.Id.StartsWith("UC", StringComparison.Ordinal)
                ? spotlightArtist.Id
                : (spotlightArtist.Id.StartsWith("ytm_sim_", StringComparison.Ordinal) &&
                   spotlightArtist.Id["ytm_sim_".Length..].StartsWith("UC", StringComparison.Ordinal)
                    ? spotlightArtist.Id["ytm_sim_".Length..]
                    : null);
            TryAddPick(new TopPickItem(
                Id: $"tp_artist_{spotlightArtist.Name}",
                Eyebrow: "ARTIST SPOTLIGHT",
                Title: spotlightArtist.Name,
                Subtitle: !string.IsNullOrWhiteSpace(spotlightArtist.Genre) ? spotlightArtist.Genre : "Explore essentials & top tracks",
                ArtworkUri: spotlightArtist.AvatarUri,
                CollageArtworks: [],
                Kind: TopPickKind.ArtistSpotlight,
                BrowseId: artistBrowseId,
                ArtistName: spotlightArtist.Name,
                AmbientHexColor: "#FF192A2A"));
        }

        // 5. Fill remaining slots up to maxItems (8) from remaining authentic YTM home candidates, playlists, releases, and albums
        foreach (var candidate in feedTopPicks.Where(p => p.HasArtwork).Concat(feedTopPicks.Where(p => !p.HasArtwork)))
        {
            if (picks.Count >= maxItems)
            {
                break;
            }

            TryAddPick(candidate);
        }

        foreach (var pl in playlistsMadeForYou)
        {
            if (picks.Count >= maxItems)
            {
                break;
            }

            TryAddPick(BuildPlaylistPick(pl, "MIXED FOR YOU"));
        }

        foreach (var nr in newReleases)
        {
            if (picks.Count >= maxItems)
            {
                break;
            }

            TryAddPick(BuildReleasePick(nr));
        }

        foreach (var alb in recommendedAlbums)
        {
            if (picks.Count >= maxItems)
            {
                break;
            }

            TryAddPick(new TopPickItem(
                Id: $"tp_alb_{alb.Id}",
                Eyebrow: "RECOMMENDED ALBUM",
                Title: alb.Title,
                Subtitle: alb.AlbumArtistSubtitle,
                ArtworkUri: alb.CoverArtUri,
                CollageArtworks: [],
                Kind: TopPickKind.NewRelease,
                BrowseId: string.IsNullOrWhiteSpace(alb.BrowseId) ? null : alb.BrowseId,
                ArtistName: alb.Artist,
                AlbumTitle: alb.Title,
                AmbientHexColor: "#FF252130"));
        }

        return picks.Take(maxItems).ToList();
    }

    /// <inheritdoc />
    public async Task<ArtistDetailProfile> GetArtistDetailAsync(
        string artistName,
        string? existingAvatarUri = null,
        CancellationToken cancellationToken = default,
        string? explicitBrowseId = null)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(artistName))
        {
            return new ArtistDetailProfile
            {
                Name = string.Empty,
                AvatarUri = existingAvatarUri ?? string.Empty
            };
        }

        string cleanArtist = GetPrimaryArtistName(artistName);

        // 1. Fetch official YouTube Music Artist Profile + Apple Music editorial assets (wordmark, motion video, banner, colors) in parallel
        var ytProfileTask = _youTubeMusicService.GetArtistProfileAsync(cleanArtist, cancellationToken, explicitBrowseId);
        var appleAssetsTask = _canvasArtworkService.GetArtistAssetsAsync(cleanArtist, cancellationToken);

        await Task.WhenAll(ytProfileTask, appleAssetsTask).ConfigureAwait(false);

        var ytProfile = await ytProfileTask.ConfigureAwait(false);
        var appleAssets = await appleAssetsTask.ConfigureAwait(false);
        string? customTitleLogoUri = appleAssets?.WordmarkUri;
        string? headerVideoUri = appleAssets?.HeaderVideoUri;
        string? keyColorHex = appleAssets?.BgColorHex ?? appleAssets?.KeyColorHex;

        // 2. Gather matching Local songs and Local albums for this artist
        List<TrackItem> localArtistTracks;
        List<AlbumItem> localArtistAlbums;
        lock (_tracks)
        {
            localArtistTracks = _tracks
                .Where(t => t.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase))
                .ToList();
            localArtistAlbums = _albums
                .Where(a => a.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // 3. If YouTube Music profile resolved, merge Local library matches with strict YouTube Music sections
        if (ytProfile is not null)
        {
            var topSongs = new List<TrackItem>();
            foreach (var ytSong in ytProfile.TopTracks)
            {
                ytSong.IsLiked = _likedOnlineTrackIds.Contains(ytSong.Id) || ytSong.IsLiked;
                DecorateTrackWithLocalOfflineMatch(ytSong);
                if (topSongs.All(x => !string.Equals(x.Title, ytSong.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    topSongs.Add(ytSong);
                }
            }

            foreach (var localSong in localArtistTracks)
            {
                if (topSongs.All(x => !string.Equals(x.Title, localSong.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    topSongs.Add(localSong);
                }
            }

            var mergedAlbums = new List<AlbumItem>();
            foreach (var alb in ytProfile.CombinedAlbums.Concat(localArtistAlbums))
            {
                if (!string.IsNullOrWhiteSpace(alb.Title) &&
                    mergedAlbums.All(x => !string.Equals(x.Title, alb.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    mergedAlbums.Add(alb);
                }
            }

            string resolvedAvatar = !string.IsNullOrWhiteSpace(ytProfile.AvatarUri)
                ? ytProfile.AvatarUri
                : (!string.IsNullOrWhiteSpace(existingAvatarUri) ? existingAvatarUri : appleAssets?.BannerUri ?? string.Empty);

            string resolvedBanner = !string.IsNullOrWhiteSpace(ytProfile.BannerImageUri)
                ? ytProfile.BannerImageUri
                : (!string.IsNullOrWhiteSpace(appleAssets?.BannerUri) ? appleAssets!.BannerUri! : resolvedAvatar);

            string resolvedBio = !string.IsNullOrWhiteSpace(ytProfile.Biography)
                ? ytProfile.Biography
                : $"{ytProfile.Name} • Official artist profile.";

            AlbumItem? latestRelease = ytProfile.LatestRelease
                ?? ytProfile.SinglesAndEps
                    .Concat(mergedAlbums)
                    .OrderByDescending(a => a.ReleaseYear)
                    .ThenByDescending(a => a.ReleaseMonth)
                    .FirstOrDefault();

            string latestTypeBadge = !string.IsNullOrWhiteSpace(ytProfile.LatestReleaseTypeBadge)
                ? ytProfile.LatestReleaseTypeBadge
                : (latestRelease?.ReleaseTypeBadgeText ?? string.Empty);

            string latestDateText = !string.IsNullOrWhiteSpace(ytProfile.LatestReleaseDateText)
                ? ytProfile.LatestReleaseDateText
                : (latestRelease is not null
                    ? YouTubeMusicService.FormatReleaseMonthYearBadge(latestRelease.ReleaseYear, latestRelease.ReleaseMonth)
                    : string.Empty);

            return new ArtistDetailProfile
            {
                Name = ytProfile.Name,
                AvatarUri = resolvedAvatar,
                BannerImageUri = resolvedBanner,
                CustomTitleLogoUri = customTitleLogoUri ?? string.Empty,
                HeaderVideoUri = headerVideoUri ?? string.Empty,
                KeyColorHex = keyColorHex ?? string.Empty,
                SubscribersCountText = ytProfile.SubscribersCountText,
                MonthlyListenersText = ytProfile.MonthlyListenersText,
                Biography = resolvedBio,
                MetadataSubtitle = ytProfile.MetadataSubtitle,
                TopTracks = topSongs.Take(15).ToList(),
                CombinedAlbums = mergedAlbums,
                SinglesAndEps = ytProfile.SinglesAndEps,
                ArtistPlaylists = ytProfile.ArtistPlaylists,
                SimilarArtists = ytProfile.SimilarArtists,
                LatestRelease = latestRelease,
                LatestReleaseTypeBadge = latestTypeBadge,
                LatestReleaseDateText = latestDateText,
                OfficialVideos = ytProfile.OfficialVideos
            };
        }

        // 4. Fallback if artist browse page was unreachable: use YouTube Music search + Local tracks only
        var ytSongsResult = await _youTubeMusicService.SearchCatalogAsync(cleanArtist, cancellationToken).ConfigureAwait(false);
        string fallbackAvatar = existingAvatarUri
            ?? ytSongsResult.Artists.FirstOrDefault(a => a.HasCoverArt)?.AvatarUri
            ?? ytSongsResult.Songs.FirstOrDefault(s => s.HasCoverArt)?.CoverArtUri
            ?? appleAssets?.BannerUri
            ?? string.Empty;
        string fallbackBanner = !string.IsNullOrWhiteSpace(appleAssets?.BannerUri)
            ? appleAssets!.BannerUri!
            : fallbackAvatar;

        var fallbackSongs = new List<TrackItem>();
        foreach (var s in ytSongsResult.Songs.Concat(localArtistTracks))
        {
            if (s.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase) ||
                cleanArtist.Contains(s.Artist, StringComparison.OrdinalIgnoreCase))
            {
                s.IsLiked = _likedOnlineTrackIds.Contains(s.Id) || s.IsLiked;
                DecorateTrackWithLocalOfflineMatch(s);
                if (fallbackSongs.All(x => !string.Equals(x.Title, s.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    fallbackSongs.Add(s);
                }
            }
        }

        var fallbackAlbums = new List<AlbumItem>();
        foreach (var alb in localArtistAlbums.Concat(ytSongsResult.Albums))
        {
            if (!string.IsNullOrWhiteSpace(alb.Title) &&
                fallbackAlbums.All(x => !string.Equals(x.Title, alb.Title, StringComparison.OrdinalIgnoreCase)))
            {
                fallbackAlbums.Add(alb);
            }
        }

        var fallbackVideos = await _youTubeMusicService.SearchArtistOfficialVideosAsync(cleanArtist, 8, cancellationToken).ConfigureAwait(false);
        var fallbackLatest = fallbackAlbums
            .OrderByDescending(a => a.ReleaseYear)
            .ThenByDescending(a => a.ReleaseMonth)
            .FirstOrDefault();

        return new ArtistDetailProfile
        {
            Name = cleanArtist,
            AvatarUri = fallbackAvatar,
            BannerImageUri = fallbackBanner,
            CustomTitleLogoUri = customTitleLogoUri ?? string.Empty,
            HeaderVideoUri = headerVideoUri ?? string.Empty,
            KeyColorHex = keyColorHex ?? string.Empty,
            Biography = $"{cleanArtist} • Artist profile and discography.",
            MetadataSubtitle = "Official Artist",
            TopTracks = fallbackSongs.Take(12).ToList(),
            CombinedAlbums = fallbackAlbums,
            SinglesAndEps = [],
            ArtistPlaylists = [],
            SimilarArtists = ytSongsResult.RecommendedArtists.Take(10).ToList(),
            LatestRelease = fallbackLatest,
            LatestReleaseTypeBadge = fallbackLatest?.ReleaseTypeBadgeText ?? "RELEASE",
            LatestReleaseDateText = fallbackLatest is not null
                ? YouTubeMusicService.FormatReleaseMonthYearBadge(fallbackLatest.ReleaseYear, fallbackLatest.ReleaseMonth)
                : string.Empty,
            OfficialVideos = fallbackVideos
        };
    }

    /// <inheritdoc />
    public async Task<AlbumDetailProfile> GetAlbumDetailAsync(
        AlbumItem album,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        string albumTitle = album.Title;
        string artistName = GetPrimaryArtistName(album.Artist);

        // 1. Check local tracks and local albums belonging to this artist/album
        List<TrackItem> localAlbumTracks;
        List<AlbumItem> localArtistOtherAlbums;
        lock (_tracks)
        {
            localAlbumTracks = _tracks
                .Where(t => string.Equals(t.Album, albumTitle, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrWhiteSpace(artistName) || t.Artist.Contains(artistName, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(t => t.TrackNumber == 0 ? 999u : t.TrackNumber)
                .ThenBy(t => t.Title)
                .ToList();

            localArtistOtherAlbums = _albums
                .Where(a => !string.Equals(a.Title, albumTitle, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(artistName) &&
                            a.Artist.Contains(artistName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // 2. Fetch Official YouTube Music Album Profile (metadata, editorial description, tracks, Other Versions, More by Artist)
        var ytAlbumProfile = await _youTubeMusicService.GetAlbumProfileAsync(
            artistName,
            albumTitle,
            album.CoverArtUri,
            album.BrowseId,
            cancellationToken).ConfigureAwait(false);

        string resolvedTitle = !string.IsNullOrWhiteSpace(ytAlbumProfile?.Title) ? ytAlbumProfile!.Title : albumTitle;
        string resolvedArtist = !string.IsNullOrWhiteSpace(ytAlbumProfile?.Artist) &&
                                !string.Equals(ytAlbumProfile.Artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase)
            ? ytAlbumProfile.Artist
            : artistName;

        var ytOfficialTracks = ytAlbumProfile?.Tracks ?? [];
        var albumTracks = new List<TrackItem>();

        if (ytOfficialTracks.Count > 0)
        {
            // Preserve the exact 1..N track order from YouTube Music's official album browse endpoint
            uint orderIndex = 1;
            foreach (var ytSong in ytOfficialTracks)
            {
                var matchingLocal = localAlbumTracks.FirstOrDefault(
                    lt => string.Equals(lt.Title, ytSong.Title, StringComparison.OrdinalIgnoreCase));

                if (matchingLocal is not null)
                {
                    albumTracks.Add(new TrackItem
                    {
                        Id = matchingLocal.Id,
                        Title = matchingLocal.Title,
                        Artist = matchingLocal.Artist,
                        Album = resolvedTitle,
                        Duration = matchingLocal.Duration,
                        CoverArtUri = matchingLocal.CoverArtUri,
                        AudioSourceUri = matchingLocal.AudioSourceUri,
                        YouTubeVideoId = !string.IsNullOrWhiteSpace(matchingLocal.YouTubeVideoId)
                            ? matchingLocal.YouTubeVideoId
                            : ytSong.YouTubeVideoId,
                        SourceType = matchingLocal.SourceType,
                        IsLiked = matchingLocal.IsLiked,
                        IsExplicit = matchingLocal.IsExplicit || ytSong.IsExplicit,
                        QualityBadge = matchingLocal.QualityBadge,
                        Genre = matchingLocal.Genre,
                        ReleaseYear = matchingLocal.ReleaseYear > 0 ? matchingLocal.ReleaseYear : ytSong.ReleaseYear,
                        TrackNumber = orderIndex++
                    });
                }
                else
                {
                    var trackItem = new TrackItem
                    {
                        Id = ytSong.Id,
                        Title = ytSong.Title,
                        Artist = string.IsNullOrWhiteSpace(ytSong.Artist) ? resolvedArtist : ytSong.Artist,
                        Album = resolvedTitle,
                        Duration = ytSong.Duration,
                        CoverArtUri = !string.IsNullOrWhiteSpace(ytSong.CoverArtUri) ? ytSong.CoverArtUri : album.CoverArtUri,
                        AudioSourceUri = ytSong.AudioSourceUri,
                        YouTubeVideoId = ytSong.YouTubeVideoId,
                        SourceType = ytSong.SourceType,
                        IsLiked = _likedOnlineTrackIds.Contains(ytSong.Id),
                        IsExplicit = ytSong.IsExplicit,
                        QualityBadge = ytSong.QualityBadge,
                        Genre = ytSong.Genre,
                        ReleaseYear = album.ReleaseYear > 1900 ? album.ReleaseYear : ytSong.ReleaseYear,
                        TrackNumber = orderIndex++
                    };
                    DecorateTrackWithLocalOfflineMatch(trackItem);
                    albumTracks.Add(trackItem);
                }
            }
        }
        else if (localAlbumTracks.Count > 0)
        {
            uint orderIndex = 1;
            foreach (var lt in localAlbumTracks)
            {
                albumTracks.Add(new TrackItem
                {
                    Id = lt.Id,
                    Title = lt.Title,
                    Artist = lt.Artist,
                    Album = lt.Album,
                    Duration = lt.Duration,
                    CoverArtUri = lt.CoverArtUri,
                    AudioSourceUri = lt.AudioSourceUri,
                    YouTubeVideoId = lt.YouTubeVideoId,
                    SourceType = lt.SourceType,
                    IsLiked = lt.IsLiked,
                    IsExplicit = lt.IsExplicit,
                    QualityBadge = lt.QualityBadge,
                    Genre = lt.Genre,
                    ReleaseYear = lt.ReleaseYear,
                    TrackNumber = lt.TrackNumber > 0 ? lt.TrackNumber : orderIndex++
                });
            }
        }
        else
        {
            // Fallback if album browse ID was not found: filter strictly by matching album title
            var fallbackSearch = await _youTubeMusicService.SearchCatalogAsync($"{resolvedArtist} {resolvedTitle}", cancellationToken).ConfigureAwait(false);
            foreach (var ytSong in fallbackSearch.Songs.Where(s =>
                         string.Equals(s.Album, resolvedTitle, StringComparison.OrdinalIgnoreCase) ||
                         s.Album.Contains(resolvedTitle, StringComparison.OrdinalIgnoreCase)))
            {
                if (albumTracks.All(t => !string.Equals(t.Title, ytSong.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    var trackItem = new TrackItem
                    {
                        Id = ytSong.Id,
                        Title = ytSong.Title,
                        Artist = string.IsNullOrWhiteSpace(ytSong.Artist) ? resolvedArtist : ytSong.Artist,
                        Album = resolvedTitle,
                        Duration = ytSong.Duration,
                        CoverArtUri = !string.IsNullOrWhiteSpace(ytSong.CoverArtUri) ? ytSong.CoverArtUri : album.CoverArtUri,
                        AudioSourceUri = ytSong.AudioSourceUri,
                        YouTubeVideoId = ytSong.YouTubeVideoId,
                        SourceType = ytSong.SourceType,
                        IsLiked = _likedOnlineTrackIds.Contains(ytSong.Id),
                        IsExplicit = ytSong.IsExplicit,
                        QualityBadge = ytSong.QualityBadge,
                        Genre = ytSong.Genre,
                        ReleaseYear = album.ReleaseYear > 1900 ? album.ReleaseYear : ytSong.ReleaseYear,
                        TrackNumber = (uint)(albumTracks.Count + 1)
                    };
                    DecorateTrackWithLocalOfflineMatch(trackItem);
                    albumTracks.Add(trackItem);
                }
            }
        }

        string coverUri = !string.IsNullOrWhiteSpace(ytAlbumProfile?.CoverArtUri)
            ? ytAlbumProfile!.CoverArtUri
            : (!string.IsNullOrWhiteSpace(album.CoverArtUri)
                ? album.CoverArtUri
                : albumTracks.FirstOrDefault(t => t.HasCoverArt)?.CoverArtUri ?? string.Empty);

        int totalMinutes = Math.Max(1, (int)Math.Round(albumTracks.Sum(t => t.Duration.TotalMinutes)));
        int year = (ytAlbumProfile?.ReleaseYear ?? 0) > 1900
            ? ytAlbumProfile!.ReleaseYear
            : (album.ReleaseYear > 1900
                ? album.ReleaseYear
                : albumTracks.FirstOrDefault(t => t.ReleaseYear > 1900)?.ReleaseYear ?? DateTime.Now.Year);

        string songsWord = albumTracks.Count == 1 ? "1 song" : $"{albumTracks.Count} songs";
        string metadataSubtitle = year > 1900
            ? $"{year} • {songsWord} • {totalMinutes} min"
            : $"{songsWord} • {totalMinutes} min";

        string description = ytAlbumProfile?.Description ?? string.Empty;
        string fullInfo = !string.IsNullOrWhiteSpace(description)
            ? $"{metadataSubtitle}\n{description}"
            : metadataSubtitle;

        var otherVersions = new List<AlbumItem>();
        if (ytAlbumProfile?.OtherVersions is { Count: > 0 })
        {
            foreach (var v in ytAlbumProfile.OtherVersions)
            {
                if (!string.Equals(v.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase) &&
                    otherVersions.All(x => !string.Equals(x.Title, v.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    otherVersions.Add(v);
                }
            }
        }

        // Build "More by [Artist]" strictly for this artist from YouTube Music + local artist albums
        var moreByArtist = new List<AlbumItem>();
        var ytMoreBy = ytAlbumProfile?.OtherAlbums ?? [];
        foreach (var cand in ytMoreBy.Concat(localArtistOtherAlbums))
        {
            if (!string.Equals(cand.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase) &&
                otherVersions.All(v => !string.Equals(v.Title, cand.Title, StringComparison.OrdinalIgnoreCase)) &&
                moreByArtist.All(x => !string.Equals(x.Title, cand.Title, StringComparison.OrdinalIgnoreCase)))
            {
                moreByArtist.Add(cand);
            }
        }

        return new AlbumDetailProfile
        {
            Id = !string.IsNullOrWhiteSpace(ytAlbumProfile?.Id) ? ytAlbumProfile!.Id : album.Id,
            Title = resolvedTitle,
            Artist = resolvedArtist,
            CoverArtUri = coverUri,
            ReleaseYear = year,
            MetadataSubtitle = metadataSubtitle,
            Description = description,
            AlbumInformation = fullInfo,
            Tracks = albumTracks,
            OtherVersions = otherVersions.Take(12).ToList(),
            OtherAlbums = moreByArtist.Take(16).ToList()
        };
    }

    /// <inheritdoc />
    public async Task ClearCatalogAsync(CancellationToken cancellationToken = default)
    {
        lock (_tracks)
        {
            _tracks.Clear();
            _albums.Clear();
            _artists.Clear();
        }

        await _storageService.SaveAsync(PersistedCatalogKey, new List<TrackItem>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LyricLine>> ResolveTrackLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // 1. Check local imported .lrc cache or companion .lrc file on disk first
        var localResolved = await Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_lyricsCacheDir))
                {
                    var cachedLrc = Path.Combine(_lyricsCacheDir, $"{track.Id}.lrc");
                    if (File.Exists(cachedLrc))
                    {
                        var cachedText = ReadTextFileAutoEncoding(cachedLrc);
                        var parsedCached = ParseLrcOrPlainLyrics(cachedText, track.Duration);
                        if (parsedCached.Count > 0)
                        {
                            var progressive = LyricsProviderService.EnsureProgressiveSegmentsForAllLines(parsedCached, track.Duration);
                            track.Lyrics = progressive;
                            track.LyricsProviderName = "Local .LRC";
                            return progressive;
                        }
                    }
                }

                var companionLrc = FindCompanionLrcFile(track.AudioSourceUri, track.Title, track.Artist);
                if (!string.IsNullOrWhiteSpace(companionLrc) && File.Exists(companionLrc))
                {
                    var diskText = ReadTextFileAutoEncoding(companionLrc);
                    var parsedDisk = ParseLrcOrPlainLyrics(diskText, track.Duration);
                    if (parsedDisk.Count > 0)
                    {
                        var progressive = LyricsProviderService.EnsureProgressiveSegmentsForAllLines(parsedDisk, track.Duration);
                        track.Lyrics = progressive;
                        track.LyricsProviderName = "Local .LRC";
                        return progressive;
                    }
                }

                if (!string.IsNullOrWhiteSpace(track.AudioSourceUri) && File.Exists(track.AudioSourceUri))
                {
                    using var tagFile = TagLib.File.Create(track.AudioSourceUri);
                    if (!string.IsNullOrWhiteSpace(tagFile.Tag.Lyrics))
                    {
                        var parsedEmbedded = ParseLrcOrPlainLyrics(tagFile.Tag.Lyrics, track.Duration);
                        if (parsedEmbedded.Count > 0)
                        {
                            var progressive = LyricsProviderService.EnsureProgressiveSegmentsForAllLines(parsedEmbedded, track.Duration);
                            track.Lyrics = progressive;
                            track.LyricsProviderName = "Embedded Tag";
                            return progressive;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve local .lrc lyrics for '{Title}'.", track.Title);
            }

            return (List<LyricLine>?)null;
        }, cancellationToken).ConfigureAwait(false);

        if (localResolved is { Count: > 0 })
        {
            return localResolved;
        }

        // 2. Query multi-provider online synchronized lyrics pipeline (YouLyPlus -> Paxsenix -> Better Lyrics -> LRCLIB -> KuGou -> SimpMusic)
        try
        {
            var onlineResult = await _lyricsProviderService.FetchOnlineLyricsAsync(track, cancellationToken).ConfigureAwait(false);
            if (onlineResult is { Lines.Count: > 0 })
            {
                track.Lyrics = onlineResult.Lines;
                track.LyricsProviderName = onlineResult.ProviderName;
                return onlineResult.Lines;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Online lyrics provider lookup failed for '{Title}'.", track.Title);
        }

        track.LyricsProviderName = "Unsynced";
        return track.Lyrics;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LyricLine>> ImportLrcFileForTrackAsync(
        string trackId,
        string lrcFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(lrcFilePath) || !File.Exists(lrcFilePath))
        {
            return [];
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var parsed = await Task.Run(() =>
        {
            var rawText = ReadTextFileAutoEncoding(lrcFilePath);
            TimeSpan duration = TimeSpan.FromMinutes(3.5);
            TrackItem? targetTrack = null;

            lock (_tracks)
            {
                targetTrack = _tracks.FirstOrDefault(t => t.Id == trackId);
                if (targetTrack is not null && targetTrack.Duration > TimeSpan.Zero)
                {
                    duration = targetTrack.Duration;
                }
            }

            var lines = ParseLrcOrPlainLyrics(rawText, duration);
            if (lines.Count > 0)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(_lyricsCacheDir))
                    {
                        var cachedPath = Path.Combine(_lyricsCacheDir, $"{trackId}.lrc");
                        File.Copy(lrcFilePath, cachedPath, overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not cache imported .lrc file.");
                }

                if (targetTrack is not null)
                {
                    targetTrack.Lyrics = lines;
                }
            }

            return (IReadOnlyList<LyricLine>)lines;
        }, cancellationToken).ConfigureAwait(false);

        if (parsed.Count > 0)
        {
            await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        return parsed;
    }

    /// <summary>
    /// Extracts complete audio metadata (Title, Artist, Album, Duration, Bitrate/SampleDepth, Genre, Year,
    /// Embedded Cover Art, and LRC/Embedded Lyrics) from a local audio file using TagLibSharp.
    /// </summary>
    private TrackItem CreateTrackFromFile(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        string fallbackArtist = "Unknown Artist";
        string fallbackTitle = fileName;

        var parts = fileName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            fallbackArtist = parts[0];
            fallbackTitle = parts[1];
        }

        string title = fallbackTitle;
        string artist = fallbackArtist;
        string album = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "Local Album";
        string genre = string.Empty;
        int releaseYear = 0;
        uint trackNumber = 0;
        TimeSpan duration = TimeSpan.Zero;
        string qualityBadge = ext;
        string? extractedCoverPath = null;
        string? embeddedLyricsText = null;
        bool isExplicit = false;

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;
            var props = tagFile.Properties;

            if (!string.IsNullOrWhiteSpace(tag.Title))
            {
                title = tag.Title.Trim();
            }

            if (tag.Performers is { Length: > 0 } && !string.IsNullOrWhiteSpace(tag.Performers[0]))
            {
                artist = string.Join(", ", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            else if (!string.IsNullOrWhiteSpace(tag.FirstAlbumArtist))
            {
                artist = tag.FirstAlbumArtist.Trim();
            }

            if (!string.IsNullOrWhiteSpace(tag.Album))
            {
                album = tag.Album.Trim();
            }

            if (!string.IsNullOrWhiteSpace(tag.FirstGenre))
            {
                genre = tag.FirstGenre.Trim();
            }

            if (tag.Year > 0)
            {
                releaseYear = (int)tag.Year;
            }

            trackNumber = tag.Track;

            if (props is not null)
            {
                if (props.Duration > TimeSpan.Zero)
                {
                    duration = props.Duration;
                }

                if (props.BitsPerSample > 16)
                {
                    qualityBadge = $"{ext} {props.BitsPerSample}-BIT";
                }
                else if (props.AudioBitrate > 0)
                {
                    qualityBadge = $"{ext} {props.AudioBitrate}K";
                }
            }

            // Extract embedded cover artwork from ID3v2 APIC / FLAC Picture / MP4 covr atoms
            if (tag.Pictures is { Length: > 0 })
            {
                var pic = tag.Pictures.FirstOrDefault(p => p?.Data?.Data is { Length: > 0 });
                if (pic?.Data?.Data is { Length: > 0 } imageBytes)
                {
                    extractedCoverPath = SaveEmbeddedCoverToCache(imageBytes, album, artist, filePath, pic.MimeType);
                }
            }

            if (!string.IsNullOrWhiteSpace(tag.Lyrics))
            {
                embeddedLyricsText = tag.Lyrics;
            }

            isExplicit = DetectLocalExplicitTag(tagFile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TagLib metadata fallback used for file '{File}'.", filePath);
        }

        // If no embedded artwork was inside the audio tag, search the track's folder for cover.jpg / folder.jpg
        extractedCoverPath ??= FindFolderCoverArt(Path.GetDirectoryName(filePath));

        string coverArtUri = !string.IsNullOrWhiteSpace(extractedCoverPath)
            ? extractedCoverPath
            : string.Empty;

        var lyrics = LoadOrParseLyrics(filePath, embeddedLyricsText, duration, title, artist, album, qualityBadge);
        var deterministicId = $"local_{ComputeShortHash(filePath)}";
        long dateAddedMs = ResolveLocalFileAddedAtMs(filePath);

        return new TrackItem
        {
            Id = deterministicId,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverArtUri = coverArtUri,
            AudioSourceUri = filePath,
            SourceType = MediaSourceType.Local,
            IsLiked = false,
            IsExplicit = isExplicit,
            DateAddedMs = dateAddedMs,
            QualityBadge = qualityBadge,
            Genre = genre,
            ReleaseYear = releaseYear,
            TrackNumber = trackNumber,
            Lyrics = lyrics
        };
    }

    private static bool DetectLocalExplicitTag(TagLib.File? tfile)
    {
        if (tfile is null)
        {
            return false;
        }

        try
        {
            if (tfile.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag appleTag)
            {
                var rtngBoxes = appleTag.DataBoxes(new[] { TagLib.ByteVector.FromString("rtng", TagLib.StringType.Latin1) });
                foreach (var box in rtngBoxes)
                {
                    if (box.Data is { Count: > 0 } && (box.Data[0] == 1 || box.Data[0] == 4))
                    {
                        return true;
                    }
                }

                var advisory = appleTag.GetDashBoxes("com.apple.iTunes", "ITUNESADVISORY");
                if (advisory.Any(v => v?.Trim() is "1" or "4"))
                {
                    return true;
                }
            }

            if (tfile.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3v2)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, "ITUNESADVISORY", false);
                string? val = frame?.Text?.FirstOrDefault()?.Trim();
                if (val is "1" or "4")
                {
                    return true;
                }
            }

            if (tfile.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            {
                string[] vals = xiph.GetField("ITUNESADVISORY");
                if (vals.Any(v => v?.Trim() is "1" or "4"))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed tags
        }

        return false;
    }

    /// <summary>
    /// Dynamically rebuilds the Albums, Artists, and Recommended Artists collections
    /// directly from the real metadata of the scanned tracks.
    /// </summary>
    private void RebuildAlbumsAndArtistsFromTracks()
    {
        _albums.Clear();
        _artists.Clear();

        if (_tracks.Count == 0)
        {
            return;
        }

        // 1. Build Albums grouped by (Album Title, Primary Artist) with accurate track counts
        var albumGroups = _tracks
            .GroupBy(t =>
            {
                string albTitle = string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album.Trim();
                string albArtist = GetPrimaryArtistName(t.Artist);
                return $"{albTitle.ToLowerInvariant()}::{albArtist.ToLowerInvariant()}";
            }, StringComparer.Ordinal)
            .OrderBy(g => g.First().Album, StringComparer.OrdinalIgnoreCase);

        foreach (var group in albumGroups)
        {
            var representative = group.FirstOrDefault(t => t.HasCoverArt)
                                 ?? group.First();
            string resolvedTitle = string.IsNullOrWhiteSpace(representative.Album) ? "Unknown Album" : representative.Album.Trim();
            string resolvedArtist = GetPrimaryArtistName(representative.Artist);
            int resolvedYear = group.Select(t => t.ReleaseYear).FirstOrDefault(y => y > 1900);

            _albums.Add(new AlbumItem
            {
                Id = $"alb_{ComputeShortHash(group.Key)}",
                Title = resolvedTitle,
                Artist = resolvedArtist,
                ReleaseYear = resolvedYear,
                TrackCount = group.Count(),
                CoverArtUri = representative.CoverArtUri,
                SourceType = MediaSourceType.Local
            });
        }

        // 2. Build Primary Artists grouped by Artist Name with song & album counts
        var artistGroups = _tracks
            .GroupBy(t => GetPrimaryArtistName(t.Artist), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in artistGroups)
        {
            if (group.Key.All(char.IsDigit))
            {
                continue;
            }

            var representative = group.FirstOrDefault(t => t.HasCoverArt)
                                 ?? group.First();
            int songCount = group.Count();
            int albumCount = group
                .Select(t => t.Album.Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a) && !a.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            _artists.Add(new ArtistItem
            {
                Id = $"art_{ComputeShortHash(group.Key)}",
                Name = group.Key,
                // Never use local album cover art as an artist portrait; official YouTube Music portraits are resolved in QueryCatalogAsync
                AvatarUri = string.Empty,
                Genre = !string.IsNullOrWhiteSpace(representative.Genre) ? representative.Genre : "Local Library",
                MonthlyAudienceText = string.Empty,
                TrackCount = songCount,
                AlbumCount = albumCount,
                IsRecommended = false,
                SourceType = MediaSourceType.Local
            });
        }

        // 3. Build "Artists You May Like" from distinct albums/tracks in the library
        var recommendedCandidates = _albums
            .Take(8)
            .Select(alb => new ArtistItem
            {
                Id = $"rec_{alb.Id}",
                Name = $"{alb.Artist} • {alb.Title}",
                AvatarUri = alb.CoverArtUri,
                Genre = alb.ReleaseYear > 1900 ? alb.ReleaseYear.ToString() : "Local Album",
                IsRecommended = true,
                SourceType = MediaSourceType.Local
            });

        _artists.AddRange(recommendedCandidates);
    }

    private string? SaveEmbeddedCoverToCache(
        byte[] imageBytes,
        string album,
        string artist,
        string filePath,
        string? mimeType)
    {
        try
        {
            string keySource = !string.IsNullOrWhiteSpace(album) && !string.Equals(album, "Local Album", StringComparison.OrdinalIgnoreCase)
                ? $"{artist}|{album}"
                : filePath;

            string hash = ComputeShortHash(keySource);
            string ext = DetermineImageExtension(imageBytes, mimeType);
            string targetPath = Path.Combine(_albumArtCacheDir, $"cover_{hash}{ext}");

            if (!File.Exists(targetPath))
            {
                File.WriteAllBytes(targetPath, imageBytes);
            }

            return targetPath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write embedded album cover to disk cache.");
            return null;
        }
    }

    private static string? FindFolderCoverArt(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return null;
        }

        try
        {
            foreach (var candidate in FolderArtworkCandidateNames)
            {
                var fullPath = Path.Combine(directoryPath, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            var anyImage = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    var ext = Path.GetExtension(f);
                    return string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
                });

            return anyImage;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LyricLine> LoadOrParseLyrics(
        string audioFilePath,
        string? embeddedLyrics,
        TimeSpan duration,
        string title,
        string artist,
        string album,
        string qualityBadge)
    {
        try
        {
            var companionLrc = FindCompanionLrcFile(audioFilePath, title, artist);
            string? rawLyrics = !string.IsNullOrWhiteSpace(companionLrc) && File.Exists(companionLrc)
                ? ReadTextFileAutoEncoding(companionLrc)
                : embeddedLyrics;

            if (!string.IsNullOrWhiteSpace(rawLyrics))
            {
                var parsed = ParseLrcOrPlainLyrics(rawLyrics, duration);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Ignore lyric file read errors
        }

        return
        [
            new LyricLine(TimeSpan.Zero, $"♪ {title} — {artist}", IsSynchronized: false),
            new LyricLine(TimeSpan.FromSeconds(4), $"Album • {album}", IsSynchronized: false),
            new LyricLine(TimeSpan.FromSeconds(8), $"Audio Stream • {qualityBadge}", IsSynchronized: false),
            new LyricLine(TimeSpan.FromSeconds(12), "Click 'Import .LRC' above or place a .lrc file next to this song for animated synchronized lyrics.", IsSynchronized: false)
        ];
    }

    private static string? FindCompanionLrcFile(string? audioFilePath, string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return null;
        }

        try
        {
            var exactLrc = Path.ChangeExtension(audioFilePath, ".lrc");
            if (File.Exists(exactLrc))
            {
                return exactLrc;
            }

            var dir = Path.GetDirectoryName(audioFilePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
            string[] candidateDirs =
            [
                dir,
                Path.Combine(dir, "Lyrics"),
                Path.Combine(dir, "lyrics"),
                Path.Combine(dir, "LRC"),
                Path.Combine(dir, "lrc")
            ];

            foreach (var searchDir in candidateDirs)
            {
                if (!Directory.Exists(searchDir))
                {
                    continue;
                }

                var lrcFiles = Directory.GetFiles(searchDir, "*.lrc", SearchOption.TopDirectoryOnly);
                if (lrcFiles.Length == 0)
                {
                    continue;
                }

                // 1. Exact base name match
                var exactMatch = lrcFiles.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
                if (exactMatch is not null)
                {
                    return exactMatch;
                }

                // 2. "Artist - Title" or "Title" match
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var artistTitle = $"{artist} - {title}";
                    var titleMatch = lrcFiles.FirstOrDefault(f =>
                    {
                        var lrcName = Path.GetFileNameWithoutExtension(f);
                        return string.Equals(lrcName, title, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(lrcName, artistTitle, StringComparison.OrdinalIgnoreCase) ||
                               (title.Length >= 3 && lrcName.Contains(title, StringComparison.OrdinalIgnoreCase));
                    });

                    if (titleMatch is not null)
                    {
                        return titleMatch;
                    }
                }
            }
        }
        catch
        {
            // Ignore directory access errors
        }

        return null;
    }

    private static string ReadTextFileAutoEncoding(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static List<LyricLine> ParseLrcOrPlainLyrics(string rawText, TimeSpan totalDuration)
    {
        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timedLines = new List<LyricLine>();
        double offsetMs = 0.0;

        foreach (var line in lines)
        {
            var offsetMatch = LrcOffsetRegex().Match(line);
            if (offsetMatch.Success &&
                double.TryParse(offsetMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double parsedOffset))
            {
                offsetMs = parsedOffset;
                continue;
            }

            // Skip standard LRC metadata tags like [ar:...], [ti:...], [al:...], [by:...]
            if (LrcMetadataTagRegex().IsMatch(line))
            {
                continue;
            }

            var matches = LrcTimestampRegex().Matches(line);
            if (matches.Count > 0)
            {
                var lastMatch = matches[^1];
                var lyricContent = line[(lastMatch.Index + lastMatch.Length)..].Trim();

                // Strip inline word-level Karako tags like <00:12.34> if present
                if (lyricContent.Contains('<'))
                {
                    lyricContent = InlineWordTimestampRegex().Replace(lyricContent, string.Empty).Trim();
                }

                if (string.IsNullOrWhiteSpace(lyricContent))
                {
                    lyricContent = "♪ • • •";
                }

                foreach (Match match in matches)
                {
                    if (int.TryParse(match.Groups[1].Value, out int minutes) &&
                        int.TryParse(match.Groups[2].Value, out int seconds))
                    {
                        double fractionSeconds = 0.0;
                        if (match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value))
                        {
                            _ = double.TryParse(
                                "0." + match.Groups[3].Value,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out fractionSeconds);
                        }

                        double totalSeconds = Math.Max(0.0, (minutes * 60.0) + seconds + fractionSeconds - (offsetMs / 1000.0));
                        timedLines.Add(new LyricLine(TimeSpan.FromSeconds(totalSeconds), lyricContent, IsSynchronized: true));
                    }
                }
            }
        }

        if (timedLines.Count > 0)
        {
            return timedLines.OrderBy(l => l.Timestamp).ToList();
        }

        // Plain untimed lyrics: distribute timestamps evenly across track duration
        var contentLines = lines.Where(l => !LrcMetadataTagRegex().IsMatch(l)).ToList();
        var plainResult = new List<LyricLine>(contentLines.Count);
        double totalSecs = Math.Max(30.0, totalDuration.TotalSeconds);
        for (int i = 0; i < contentLines.Count; i++)
        {
            double t = (totalSecs * i) / Math.Max(1, contentLines.Count);
            plainResult.Add(new LyricLine(TimeSpan.FromSeconds(t), contentLines[i], IsSynchronized: false));
        }

        return plainResult;
    }

    private static IEnumerable<string> EnumerateAudioFilesSafe(
        string rootFolder,
        LocalScanFilterSettings? filterSettings = null)
    {
        bool recurse = filterSettings?.GlobalDeepScan ?? true;
        if (filterSettings is not null)
        {
            string normRoot = Xperience.Core.Playback.LocalScanFilterEvaluator.NormalizeDirectoryPath(rootFolder);
            var matchingRule = filterSettings.Rules.FirstOrDefault(r =>
                string.Equals(
                    Xperience.Core.Playback.LocalScanFilterEvaluator.NormalizeDirectoryPath(r.FolderPath),
                    normRoot,
                    StringComparison.OrdinalIgnoreCase));
            if (matchingRule is not null)
            {
                recurse = Xperience.Core.Playback.LocalScanFilterEvaluator.ResolveEffectiveDeepScan(
                    matchingRule.DepthMode,
                    filterSettings.GlobalDeepScan);
            }
            else if (filterSettings.FilterMode == LocalScanFilterMode.Whitelist)
            {
                // When scanning a root directory under Whitelist mode, recurse if any whitelist rule is inside it
                bool hasNestedWhitelist = filterSettings.Rules.Any(r =>
                    r.ListType == FolderScanListType.Whitelist &&
                    Xperience.Core.Playback.LocalScanFilterEvaluator.NormalizeDirectoryPath(r.FolderPath)
                        .StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (hasNestedWhitelist)
                {
                    recurse = true;
                }
            }
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recurse,
            ReturnSpecialDirectories = false
        };

        try
        {
            return Directory.EnumerateFiles(rootFolder, "*.*", options)
                .Where(f => SupportedAudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => Xperience.Core.Playback.LocalScanFilterEvaluator.ShouldIncludeFile(f, filterSettings))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string GetPrimaryArtistName(string artistField)
    {
        if (string.IsNullOrWhiteSpace(artistField))
        {
            return "Unknown Artist";
        }

        var split = artistField.Split([',', ';', '/'], 2, StringSplitOptions.TrimEntries);
        return string.IsNullOrWhiteSpace(split[0]) ? artistField.Trim() : split[0];
    }

    private static string DetermineImageExtension(byte[] bytes, string? mimeType)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (!string.IsNullOrWhiteSpace(mimeType) && mimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        return ".jpg";
    }

    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private async Task PersistCatalogSnapshotAsync(CancellationToken cancellationToken)
    {
        List<TrackItem> snapshot;
        lock (_tracks)
        {
            snapshot = [.. _tracks];
        }

        await _storageService.SaveAsync(PersistedCatalogKey, snapshot, cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LrcOffsetRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|au|by|length|re|ve|tool|id):.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LrcMetadataTagRegex();

    [GeneratedRegex(@"<\d{1,3}:\d{2}(?:[\.:]\d{1,3})?>")]
    private static partial Regex InlineWordTimestampRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripHtmlTagsRegex();

    /// <inheritdoc />
    public IReadOnlyList<string> GetLocalMusicDirectories()
    {
        lock (_localMusicDirectories)
        {
            return [.. _localMusicDirectories];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> AddLocalMusicDirectoryAsync(
        string folderPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return [];
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        List<string> snapshotDirs;
        lock (_localMusicDirectories)
        {
            if (!_localMusicDirectories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                _localMusicDirectories.Add(normalized);
            }

            snapshotDirs = [.. _localMusicDirectories];
        }

        await _storageService.SaveAsync(PersistedLocalDirectoriesKey, snapshotDirs, cancellationToken).ConfigureAwait(false);
        return await ScanLocalFolderAsync(normalized, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveLocalMusicDirectoryAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefixWithSlash = normalized + Path.DirectorySeparatorChar;

        List<string> snapshotDirs;
        lock (_localMusicDirectories)
        {
            _localMusicDirectories.RemoveAll(d =>
                string.Equals(
                    Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
            snapshotDirs = [.. _localMusicDirectories];
        }

        lock (_tracks)
        {
            _tracks.RemoveAll(t =>
                !string.IsNullOrWhiteSpace(t.AudioSourceUri) &&
                (string.Equals(Path.GetDirectoryName(t.AudioSourceUri), normalized, StringComparison.OrdinalIgnoreCase) ||
                 t.AudioSourceUri.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase)));
            RebuildAlbumsAndArtistsFromTracks();
        }

        await _storageService.SaveAsync(PersistedLocalDirectoriesKey, snapshotDirs, cancellationToken).ConfigureAwait(false);
        await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public LocalScanFilterSettings GetLocalScanFilterSettings()
    {
        lock (_localMusicDirectories)
        {
            return _localScanFilterSettings with
            {
                Rules = [.. _localScanFilterSettings.Rules]
            };
        }
    }

    /// <inheritdoc />
    public async Task UpdateLocalScanFilterSettingsAsync(
        LocalScanFilterSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        lock (_localMusicDirectories)
        {
            _localScanFilterSettings = settings with
            {
                Rules = [.. settings.Rules]
            };
        }

        await _storageService.SaveAsync(LocalScanFilterSettings.StorageKey, settings, cancellationToken).ConfigureAwait(false);
        await RescanLocalLibraryAsync(progress: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RescanLocalLibraryAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<string> dirsToScan;
        LocalScanFilterSettings activeFilter;
        lock (_localMusicDirectories)
        {
            activeFilter = _localScanFilterSettings with { Rules = [.. _localScanFilterSettings.Rules] };
            dirsToScan = [.. _localMusicDirectories];
        }

        if (activeFilter.FilterMode == LocalScanFilterMode.Whitelist)
        {
            foreach (var rule in activeFilter.Rules.Where(r => r.ListType == FolderScanListType.Whitelist))
            {
                if (!string.IsNullOrWhiteSpace(rule.FolderPath) &&
                    Directory.Exists(rule.FolderPath) &&
                    !dirsToScan.Contains(rule.FolderPath, StringComparer.OrdinalIgnoreCase))
                {
                    dirsToScan.Add(rule.FolderPath);
                }
            }
        }

        var rescanned = await Task.Run(() =>
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<TrackItem>();
            int count = 0;

            foreach (string dir in dirsToScan)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string file in EnumerateAudioFilesSafe(dir, activeFilter))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seenPaths.Add(file))
                    {
                        continue;
                    }

                    var track = CreateTrackFromFile(file);
                    results.Add(track);
                    count++;
                    progress?.Report(count);
                }
            }

            lock (_tracks)
            {
                var likedPaths = new HashSet<string>(
                    _tracks.Where(t => t.IsLiked && !string.IsNullOrWhiteSpace(t.AudioSourceUri))
                           .Select(t => t.AudioSourceUri),
                    StringComparer.OrdinalIgnoreCase);

                _tracks.Clear();
                foreach (var item in results)
                {
                    if (likedPaths.Contains(item.AudioSourceUri))
                    {
                        item.IsLiked = true;
                    }

                    _tracks.Add(item);
                }

                RebuildAlbumsAndArtistsFromTracks();
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);

        await PersistCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public TrackItem? FindLocalOfflineEquivalent(TrackItem track)
    {
        if (track is null)
        {
            return null;
        }

        if (track.SourceType == MediaSourceType.Local &&
            !string.IsNullOrWhiteSpace(track.AudioSourceUri) &&
            File.Exists(track.AudioSourceUri))
        {
            return track;
        }

        if (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath))
        {
            lock (_tracks)
            {
                var byPath = _tracks.FirstOrDefault(t =>
                    string.Equals(t.AudioSourceUri, track.LocalOfflineFilePath, StringComparison.OrdinalIgnoreCase));
                if (byPath is not null)
                {
                    return byPath;
                }
            }
        }

        string cleanTitle = NormalizeComparisonToken(track.Title);
        string cleanArtist = NormalizeComparisonToken(GetPrimaryArtistName(track.Artist));

        lock (_tracks)
        {
            foreach (var local in _tracks)
            {
                if (string.IsNullOrWhiteSpace(local.AudioSourceUri) || !File.Exists(local.AudioSourceUri))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
                    string.Equals(local.YouTubeVideoId, track.YouTubeVideoId, StringComparison.Ordinal))
                {
                    return local;
                }

                string localTitle = NormalizeComparisonToken(local.Title);
                string localArtist = NormalizeComparisonToken(GetPrimaryArtistName(local.Artist));

                if (!string.IsNullOrWhiteSpace(cleanTitle) &&
                    string.Equals(localTitle, cleanTitle, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(cleanArtist) ||
                     string.Equals(localArtist, cleanArtist, StringComparison.OrdinalIgnoreCase) ||
                     local.Artist.Contains(cleanArtist, StringComparison.OrdinalIgnoreCase) ||
                     track.Artist.Contains(localArtist, StringComparison.OrdinalIgnoreCase)))
                {
                    return local;
                }
            }
        }

        return null;
    }

    private void DecorateTrackWithLocalOfflineMatch(TrackItem track)
    {
        if (track is null)
        {
            return;
        }

        var localMatch = FindLocalOfflineEquivalent(track);
        if (localMatch is not null && !string.IsNullOrWhiteSpace(localMatch.AudioSourceUri))
        {
            track.IsDownloadedOffline = true;
            track.LocalOfflineFilePath = localMatch.AudioSourceUri;
            track.IsLiked = track.IsLiked || localMatch.IsLiked;
        }
    }

    private static string NormalizeComparisonToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Trim();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_playlists)
        {
            foreach (var pl in _playlists)
            {
                foreach (var tr in pl.Tracks)
                {
                    DecorateTrackWithLocalOfflineMatch(tr);
                }
            }

            return [.. _playlists];
        }
    }

    /// <inheritdoc />
    public async Task<UserPlaylist> CreatePlaylistAsync(
        string title,
        string? description = null,
        string? customCoverPath = null,
        TrackItem? initialTrack = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var playlist = new UserPlaylist
        {
            Id = $"pl_{Guid.NewGuid():N}",
            Title = string.IsNullOrWhiteSpace(title) ? "My Playlist" : title.Trim(),
            Description = description?.Trim() ?? string.Empty,
            CustomCoverImagePath = customCoverPath?.Trim() ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            Tracks = []
        };

        if (initialTrack is not null)
        {
            DecorateTrackWithLocalOfflineMatch(initialTrack);
            playlist.Tracks.Add(initialTrack);
        }

        List<UserPlaylist> snapshot;
        lock (_playlists)
        {
            _playlists.Insert(0, playlist);
            snapshot = [.. _playlists];
        }

        await _storageService.SaveAsync(PersistedPlaylistsKey, snapshot, cancellationToken).ConfigureAwait(false);
        return playlist;
    }

    /// <inheritdoc />
    public async Task DeletePlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<UserPlaylist> snapshot;
        lock (_playlists)
        {
            _playlists.RemoveAll(p => string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase));
            snapshot = [.. _playlists];
        }

        await _storageService.SaveAsync(PersistedPlaylistsKey, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UserPlaylist?> AddTrackToPlaylistAsync(
        string playlistId,
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || track is null)
        {
            return null;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        DecorateTrackWithLocalOfflineMatch(track);

        UserPlaylist? target = null;
        List<UserPlaylist> snapshot;
        lock (_playlists)
        {
            target = _playlists.FirstOrDefault(p => string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                if (target.Tracks.All(t => !string.Equals(t.Id, track.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    target.Tracks.Add(track);
                }
            }

            snapshot = [.. _playlists];
        }

        if (target is not null)
        {
            await _storageService.SaveAsync(PersistedPlaylistsKey, snapshot, cancellationToken).ConfigureAwait(false);
        }

        return target;
    }

    /// <inheritdoc />
    public async Task<UserPlaylist?> RemoveTrackFromPlaylistAsync(
        string playlistId,
        string trackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(trackId))
        {
            return null;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        UserPlaylist? target = null;
        List<UserPlaylist> snapshot;
        lock (_playlists)
        {
            target = _playlists.FirstOrDefault(p => string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase));
            target?.Tracks.RemoveAll(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
            snapshot = [.. _playlists];
        }

        if (target is not null)
        {
            await _storageService.SaveAsync(PersistedPlaylistsKey, snapshot, cancellationToken).ConfigureAwait(false);
        }

        return target;
    }

    private static Dictionary<string, string> LoadAtvCounterpartMapSafe(string appDataRoot)
    {
        try
        {
            string mapPath = Path.Combine(appDataRoot, "OnlineCache", "atv_counterparts_cache.json");
            if (!File.Exists(mapPath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            using var stream = File.OpenRead(mapPath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return parsed is not null
                ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool IsViewOrPlayCountLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("views", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("reproducciones", StringComparison.OrdinalIgnoreCase);
    }

    private RecentAlbumItem CreateRecentAlbumFromTrack(TrackItem track)
    {
        string? matchingBrowseId = null;
        if (!RecentAlbumItem.IsGenericAlbumTitle(track.Album))
        {
            string normAlb = RecentAlbumItem.NormalizeIdentityToken(track.Album);
            string normPrimaryArtist = RecentAlbumItem.NormalizeIdentityToken(GetPrimaryArtistName(track.Artist));
            lock (_tracks)
            {
                var match = _albums.FirstOrDefault(a =>
                    !string.IsNullOrWhiteSpace(a.BrowseId) &&
                    string.Equals(RecentAlbumItem.NormalizeIdentityToken(a.Title), normAlb, StringComparison.Ordinal) &&
                    (RecentAlbumItem.IsGenericArtistName(a.Artist) ||
                     string.Equals(RecentAlbumItem.NormalizeIdentityToken(GetPrimaryArtistName(a.Artist)), normPrimaryArtist, StringComparison.Ordinal)));
                matchingBrowseId = match?.BrowseId;
            }
        }

        return RecentAlbumItem.FromTrack(track, matchingBrowseId);
    }

    private static bool IsSameAlbumOrTrack(TrackItem? a, TrackItem? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(a.Id) &&
            string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RecentAlbumItem.FromTrack(a).MatchesAlbum(RecentAlbumItem.FromTrack(b));
    }

    private RecentAlbumItem? SanitizePersistedRecentAlbum(
        RecentAlbumItem? raw,
        Lazy<Dictionary<string, string>> atvCounterparts,
        out bool modified)
    {
        modified = false;
        if (raw is null || string.IsNullOrWhiteSpace(raw.AlbumTitle))
        {
            return null;
        }

        TrackItem? sanitizedSeed = raw.SeedTrack;
        if (sanitizedSeed is not null)
        {
            sanitizedSeed = SanitizePersistedOnlineTrack(sanitizedSeed, atvCounterparts, out bool seedChanged);
            modified |= seedChanged;
        }

        string coverUri = raw.CoverArtUri ?? string.Empty;
        if (coverUri.EndsWith("princess_of_power.png", StringComparison.OrdinalIgnoreCase))
        {
            coverUri = string.Empty;
            modified = true;
        }
        else if (coverUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 coverUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 coverUri.StartsWith("//", StringComparison.Ordinal))
        {
            string normalizedCover = YouTubeMusicService.NormalizeHighResThumbnailUrl(coverUri);
            if (!string.Equals(normalizedCover, coverUri, StringComparison.Ordinal))
            {
                coverUri = normalizedCover;
                modified = true;
            }
        }

        if (string.IsNullOrWhiteSpace(coverUri) && sanitizedSeed is { HasCoverArt: true })
        {
            coverUri = sanitizedSeed.CoverArtUri;
            modified = true;
        }

        string rawArtist = raw.Artist ?? string.Empty;
        string primaryArtist = RecentAlbumItem.ExtractPrimaryArtist(rawArtist);
        string resolvedArtist = !string.IsNullOrWhiteSpace(primaryArtist) ? primaryArtist : rawArtist.Trim();
        string normTitle = RecentAlbumItem.NormalizeIdentityToken(raw.AlbumTitle);
        string normArtist = RecentAlbumItem.IsGenericArtistName(resolvedArtist) ? "*" : RecentAlbumItem.NormalizeIdentityToken(resolvedArtist);
        bool isSingle = raw.IsSingleRelease || RecentAlbumItem.IsGenericAlbumTitle(raw.AlbumTitle);
        string expectedKey = $"{(isSingle ? "sng" : "alb")}::{normTitle}::{normArtist}";

        if (!string.Equals(raw.AlbumKey, expectedKey, StringComparison.Ordinal) ||
            !string.Equals(raw.Artist, resolvedArtist, StringComparison.Ordinal))
        {
            modified = true;
        }

        if (!modified)
        {
            return raw;
        }

        return new RecentAlbumItem
        {
            Id = !string.IsNullOrWhiteSpace(raw.Id) ? raw.Id : $"recent_alb_{ComputeShortHash(expectedKey)}",
            AlbumKey = expectedKey,
            AlbumTitle = raw.AlbumTitle.Trim(),
            Artist = string.IsNullOrWhiteSpace(resolvedArtist) ? "Unknown Artist" : resolvedArtist,
            CoverArtUri = coverUri,
            BrowseId = raw.BrowseId ?? string.Empty,
            ReleaseYear = raw.ReleaseYear,
            SourceType = raw.SourceType,
            IsSingleRelease = isSingle,
            LastPlayedAt = raw.LastPlayedAt,
            SeedTrack = sanitizedSeed
        };
    }

    private static TrackItem SanitizePersistedOnlineTrack(
        TrackItem track,
        Lazy<Dictionary<string, string>> atvCounterparts,
        out bool modified)
    {
        modified = false;
        if (track.SourceType != MediaSourceType.Online)
        {
            return track;
        }

        string newAlbum = IsViewOrPlayCountLabel(track.Album) ? "Single" : track.Album;
        string newVideoId = track.YouTubeVideoId;
        string newId = track.Id;
        string newAudioSource = track.AudioSourceUri;
        string newCoverUri = track.CoverArtUri;

        if (!string.IsNullOrWhiteSpace(newCoverUri) &&
            (newCoverUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             newCoverUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             newCoverUri.StartsWith("//", StringComparison.Ordinal)))
        {
            newCoverUri = YouTubeMusicService.NormalizeHighResThumbnailUrl(newCoverUri);
        }

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
            atvCounterparts.Value.TryGetValue(track.YouTubeVideoId, out string? atvId) &&
            !string.IsNullOrWhiteSpace(atvId) &&
            !string.Equals(atvId, track.YouTubeVideoId, StringComparison.Ordinal))
        {
            newVideoId = atvId;
            newId = $"ytm_{atvId}";
            if (string.IsNullOrWhiteSpace(track.AudioSourceUri) ||
                track.AudioSourceUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase))
            {
                newAudioSource = $"ytmusic://{atvId}";
            }
        }

        if (string.Equals(newAlbum, track.Album, StringComparison.Ordinal) &&
            string.Equals(newVideoId, track.YouTubeVideoId, StringComparison.Ordinal) &&
            string.Equals(newCoverUri, track.CoverArtUri, StringComparison.Ordinal))
        {
            return track;
        }

        modified = true;
        return new TrackItem
        {
            Id = newId,
            Title = track.Title,
            Artist = track.Artist,
            Album = newAlbum,
            Duration = track.Duration,
            CoverArtUri = newCoverUri,
            AudioSourceUri = newAudioSource,
            YouTubeVideoId = newVideoId,
            SourceType = track.SourceType,
            IsLiked = track.IsLiked,
            IsExplicit = track.IsExplicit,
            DateAddedMs = track.DateAddedMs,
            QualityBadge = track.QualityBadge,
            Genre = track.Genre,
            ReleaseYear = track.ReleaseYear,
            TrackNumber = track.TrackNumber,
            PlayCountText = track.PlayCountText,
            PlayCount = track.PlayCount,
            LyricsProviderName = track.LyricsProviderName,
            Lyrics = track.Lyrics,
            IsDownloadedOffline = track.IsDownloadedOffline,
            LocalOfflineFilePath = track.LocalOfflineFilePath
        };
    }
}
