using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Collections;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Playback;
using Xperience.Core.Services;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Xperience.App.Services;

/// <summary>
/// Lightweight reverse-engineered YouTube Music client (<c>WEB_REMIX</c> InnerTube + multi-client player resolver
/// with WebView2 <c>base.js</c> cipher solving and <c>YoutubeExplode</c> fallback) inspired by <c>sigma67/ytmusicapi</c>.
/// Supports authenticated cookies (<c>SAPISIDHASH</c>) for age-restricted / explicit tracks, high-res metadata,
/// and direct M4A/AAC audio stream resolution without 403 Forbidden interruptions.
/// </summary>
public sealed partial class YouTubeMusicService : IYouTubeMusicService, IDisposable
{
    private const string YtMusicSearchEndpoint = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";
    private const string YtMusicBrowseEndpoint = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false";
    private const string YtMusicNextEndpoint = "https://music.youtube.com/youtubei/v1/next?prettyPrint=false";
    private const string YtMusicPlayerEndpoint = "https://music.youtube.com/youtubei/v1/player?prettyPrint=false";
    private const string YtPlayerEndpoint = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    private const string MwebPlayerEndpoint = "https://m.youtube.com/youtubei/v1/player?prettyPrint=false";
    private const string AccountMenuEndpoint = "https://music.youtube.com/youtubei/v1/account/account_menu?prettyPrint=false";
    private const string AccountSwitcherEndpoint = "https://music.youtube.com/getAccountSwitcherEndpoint";
    private const string SwJsDataEndpoint = "https://www.youtube.com/sw.js_data?hl=en";

    private const string WebRemixClientId = "67";
    private const string WebRemixClientVersion = "1.20260213.01.00";
    private const string WebClientId = "1";
    private const string WebClientVersion = "2.20260213.01.00";
    private const string MwebClientId = "2";
    private const string MwebClientVersion = "2.20260213.01.00";
    private const string TvHtml5ClientId = "7";
    private const string TvHtml5ClientVersion = "7.20260114.12.00";
    private const string TvHtml5UserAgent =
        "Mozilla/5.0 (SMART-TV; LINUX; Tizen 7.0) AppleWebKit/537.36 (KHTML, like Gecko) 94.0.4606.31/7.0 TV Safari/537.36";
    private const string AndroidClientId = "3";
    private const string AndroidClientVersion = "20.10.38";
    private const string AndroidUserAgent =
        "com.google.android.youtube/20.10.38 (Linux; U; Android 14; en_US; Pixel 7 Build/AP2A.240805.005) gzip";
    private const string AndroidVrClientId = "28";
    private const string AndroidVrClientVersion = "1.65.10";
    private const string AndroidVrUserAgent =
        "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
    private const string IosClientId = "5";
    private const string IosClientVersion = "20.10.4";
    private const string IosUserAgent =
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";
    private const string VisionOsClientVersion = "0.1";
    private const string VisionOsUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15";
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";
    private const string MobileWebUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Mobile Safari/537.36";

    private const string YtMusicSongsFilterParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";
    private const string YtMusicAlbumsFilterParams = "EgWKAQIYAWoMEA4QChADEAQQCRAF";
    private const string YtMusicArtistsFilterParams = "EgWKAQIgAWoMEA4QChADEAQQCRAF";
    private const string YtMusicPlaylistsFilterParams = "EgWKAQIoAWoMEA4QChADEAQQCRAF";
    private const string MoodsAndGenresBrowseId = "FEmusic_moods_and_genres";
    private const string DefaultDiscoveryQuery = "Top Global Hits";
    private static readonly TimeSpan MoodsCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RichSearchCacheTtl = TimeSpan.FromMinutes(15);

    private const int StripedLockCount = 64;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly YoutubeClient _youtubeClient;
    private readonly IPlayerScriptResolver _scriptResolver;
    private readonly ILogger<YouTubeMusicService> _logger;
    private readonly SemaphoreSlim[] _stripedLocks = CreateStripedLocks();
    private readonly BoundedLruCache<string, ResolvedAudioStream> _streamCache = new(maxCapacity: 80, comparer: StringComparer.Ordinal);
    private readonly BoundedLruCache<string, CatalogQueryResult> _searchCache = new(maxCapacity: 40, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, (YouTubeMusicSearchResult Result, DateTimeOffset ExpiresAtUtc)> _richSearchCache = new(maxCapacity: 30, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, (MoodGenreDetailResult Detail, DateTimeOffset ExpiresAtUtc)> _moodGenreDetailCache = new(maxCapacity: 30, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, string> _artistAvatarCache = new(maxCapacity: 500, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, string> _artistAudienceCache = new(maxCapacity: 500, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, int> _albumTrackCountCache = new(maxCapacity: 500, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, string> _atvCounterpartCache = new(maxCapacity: 500, comparer: StringComparer.Ordinal);
    private readonly BoundedLruCache<string, string> _officialOmvCounterpartCache = new(maxCapacity: 500, comparer: StringComparer.Ordinal);
    private readonly BoundedLruCache<string, OfficialMusicVideoMetadata?> _officialVideoMetadataCache = new(maxCapacity: 250, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly object _artistAvatarLoadLock = new();
    private readonly object _artistAudienceLoadLock = new();
    private readonly object _atvCounterpartLoadLock = new();
    private volatile bool _artistAvatarCacheLoaded;
    private volatile bool _artistAudienceCacheLoaded;
    private volatile bool _atvCounterpartCacheLoaded;
    private readonly object _moodsCacheLock = new();
    private (MoodsAndGenresFeed Feed, DateTimeOffset ExpiresAtUtc)? _moodsAndGenresCache;
    private readonly SemaphoreSlim _visitorLock = new(1, 1);
    private readonly SemaphoreSlim _accountMetaLock = new(1, 1);
    private readonly string _cacheDirectory;
    private readonly string _coverCacheDirectory;
    private readonly string _artistAvatarCacheFilePath;
    private readonly string _artistAudienceCacheFilePath;
    private readonly string _atvCounterpartCacheFilePath;
    private readonly WindowsAccountSessionStore _sessionStore = new();
    private readonly string _legacyWebView2LoginDirectory;
    private readonly string _streamTraceFilePath;
    private readonly object _traceLock = new();
    private string? _cachedVisitorData;
    private string? _cachedVisitorCookies;
    private DateTimeOffset _visitorDataExpiry = DateTimeOffset.MinValue;
    private string _cookieHeader = string.Empty;
    private string _sapisid = string.Empty;
    private string _sessionVisitorData = string.Empty;
    private string? _dataSyncId;
    private bool _triedAccountMetaSync;
    private volatile bool _isSessionInvalidated;
    private int _disposed;

    private static SemaphoreSlim[] CreateStripedLocks()
    {
        var locks = new SemaphoreSlim[StripedLockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new SemaphoreSlim(1, 1);
        }

        return locks;
    }

    private SemaphoreSlim GetStripedLock(string key, StringComparer comparer)
    {
        int hash = comparer.GetHashCode(key) & 0x7FFFFFFF;
        return _stripedLocks[hash % StripedLockCount];
    }

    /// <inheritdoc />
    public event EventHandler? AccountSessionChanged;

    /// <inheritdoc />
    public bool IsAuthenticated => !_isSessionInvalidated && YouTubeAuthHelper.HasYouTubeAccountSession(_cookieHeader);

    /// <inheritdoc />
    public bool IsSessionExpired => _isSessionInvalidated;

    /// <inheritdoc />
    public string ConnectedAccountLabel { get; private set; } = "Not signed in";

    /// <inheritdoc />
    public string? LastStreamFailureReason { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeMusicService"/> class.
    /// </summary>
    public YouTubeMusicService(ILogger<YouTubeMusicService> logger)
    {
        _logger = logger;
        _youtubeClient = new YoutubeClient();
        var apiHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(new SafeRedirectDelegatingHandler(apiHandler, YouTubeSecurityPolicy.IsAllowedYouTubeApiHost))
        {
            Timeout = TimeSpan.FromSeconds(18)
        };

        var downloadHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _downloadHttpClient = new HttpClient(new SafeRedirectDelegatingHandler(downloadHandler, YouTubeSecurityPolicy.IsAllowedYouTubeApiHost))
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        string appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer");
        _cacheDirectory = Path.Combine(appDataRoot, "OnlineCache");
        _coverCacheDirectory = Path.Combine(_cacheDirectory, "CoverCache");
        _artistAvatarCacheFilePath = Path.Combine(_cacheDirectory, "artist_avatars_cache.json");
        _artistAudienceCacheFilePath = Path.Combine(_cacheDirectory, "artist_audience_cache.json");
        _atvCounterpartCacheFilePath = Path.Combine(_cacheDirectory, "atv_counterparts_cache.json");
        _legacyWebView2LoginDirectory = Path.Combine(appDataRoot, "WebView2Login");
        _streamTraceFilePath = Path.Combine(_cacheDirectory, "ytm_stream_trace.log");
        _scriptResolver = new WindowsWebView2PlayerScriptSolver(_downloadHttpClient, _logger, _cacheDirectory, TraceStream);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            Directory.CreateDirectory(_coverCacheDirectory);

            // Artist avatar, artist audience, and ATV counterpart disk caches are loaded lazily on first use
            var savedSession = _sessionStore.Load();
            if (savedSession is not null && !string.IsNullOrWhiteSpace(savedSession.CookieHeader))
            {
                ApplyCookieState(
                    savedSession.CookieHeader,
                    savedSession.AccountLabel,
                    savedSession.VisitorData,
                    savedSession.DataSyncId);
            }

            // Best-effort cleanup of any legacy persistent WebView2Login profile folder from prior versions
            if (Directory.Exists(_legacyWebView2LoginDirectory))
            {
                string legacyDir = _legacyWebView2LoginDirectory;
                _ = Task.Run(() => TryDeleteDirectorySafe(legacyDir));
            }
        }
        catch
        {
            // Transient IO fallback
        }
    }

    private void EnsureArtistAvatarCacheLoaded()
    {
        if (_artistAvatarCacheLoaded)
        {
            return;
        }

        lock (_artistAvatarLoadLock)
        {
            if (_artistAvatarCacheLoaded)
            {
                return;
            }

            LoadStringCacheFromDiskSafe(_artistAvatarCacheFilePath, _artistAvatarCache);
            _artistAvatarCacheLoaded = true;
        }
    }

    private void EnsureArtistAudienceCacheLoaded()
    {
        if (_artistAudienceCacheLoaded)
        {
            return;
        }

        lock (_artistAudienceLoadLock)
        {
            if (_artistAudienceCacheLoaded)
            {
                return;
            }

            LoadStringCacheFromDiskSafe(_artistAudienceCacheFilePath, _artistAudienceCache);
            _artistAudienceCacheLoaded = true;
        }
    }

    private void EnsureAtvCounterpartCacheLoaded()
    {
        if (_atvCounterpartCacheLoaded)
        {
            return;
        }

        lock (_atvCounterpartLoadLock)
        {
            if (_atvCounterpartCacheLoaded)
            {
                return;
            }

            LoadStringCacheFromDiskSafe(_atvCounterpartCacheFilePath, _atvCounterpartCache);
            _atvCounterpartCacheLoaded = true;
        }
    }

    private static void LoadStringCacheFromDiskSafe(string filePath, BoundedLruCache<string, string> targetCache)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            using var stream = File.OpenRead(filePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (loaded is null)
            {
                return;
            }

            foreach (var kvp in loaded)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value) && !targetCache.ContainsKey(kvp.Key))
                {
                    targetCache.Set(kvp.Key, kvp.Value);
                }
            }
        }
        catch
        {
            // Transient IO fallback
        }
    }

    /// <inheritdoc />
    public async Task<CatalogQueryResult> SearchCatalogAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        string effectiveQuery = string.IsNullOrWhiteSpace(query)
            ? DefaultDiscoveryQuery
            : query.Trim();

        if (_searchCache.TryGet(effectiveQuery, out var cachedResult) &&
            (cachedResult.Songs.Count > 0 || cachedResult.Albums.Count > 0 || cachedResult.Artists.Count > 0))
        {
            return cachedResult;
        }

        try
        {
            var songsTask = ExecuteSongsSearchAsync(effectiveQuery, useSongsFilter: true, cancellationToken);
            var albumsTask = ExecuteAlbumsSearchAsync(effectiveQuery, strictlyAlbumsOnly: true, cancellationToken);
            var artistsTask = ExecuteArtistsSearchAsync(effectiveQuery, limit: 10, cancellationToken);

            await Task.WhenAll(songsTask, albumsTask, artistsTask).ConfigureAwait(false);

            var tracks = await songsTask.ConfigureAwait(false);
            if (tracks.Count == 0)
            {
                tracks = await ExecuteSongsSearchAsync(effectiveQuery, useSongsFilter: false, cancellationToken).ConfigureAwait(false);
            }

            var albums = await albumsTask.ConfigureAwait(false);
            if (albums.Count == 0 && tracks.Count > 0)
            {
                albums = BuildAlbumsFromTracks(tracks);
            }

            var directArtists = await artistsTask.ConfigureAwait(false);
            var (artists, recommended) = await MergeAndEnrichSearchArtistsAsync(
                directArtists,
                tracks,
                limit: 10,
                cancellationToken).ConfigureAwait(false);

            var artworkUris = tracks.Select(t => t.CoverArtUri)
                .Concat(albums.Select(a => a.CoverArtUri))
                .Concat(artists.Select(a => a.AvatarUri))
                .Concat(recommended.Select(a => a.AvatarUri));
            var warmedMap = await WarmArtworkCacheAsync(artworkUris, cancellationToken).ConfigureAwait(false);

            var warmedTracks = ApplyWarmedTrackArtworks(tracks, warmedMap);
            var warmedAlbums = ApplyWarmedAlbumArtworks(albums, warmedMap);
            var warmedArtists = ApplyWarmedArtistArtworks(artists, warmedMap);
            var warmedRecommended = ApplyWarmedArtistArtworks(recommended, warmedMap);

            var result = new CatalogQueryResult(warmedTracks, warmedAlbums, warmedArtists, warmedRecommended);
            if (warmedTracks.Count > 0 || warmedAlbums.Count > 0 || warmedArtists.Count > 0)
            {
                _searchCache.Set(effectiveQuery, result);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Music search failed for query '{Query}'.", effectiveQuery);
            return new CatalogQueryResult([], [], [], []);
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetArtistAvatarUriAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        var (avatarUri, _) = await GetArtistSummaryAsync(artistName, cancellationToken).ConfigureAwait(false);
        return avatarUri;
    }

    /// <inheritdoc />
    public async Task<(string? AvatarUri, string? MonthlyAudienceText)> GetArtistSummaryAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return (null, null);
        }

        string primaryArtist = artistName.Split([',', ';', '/'], 2, StringSplitOptions.TrimEntries)[0].Trim();
        if (string.IsNullOrWhiteSpace(primaryArtist) ||
            string.Equals(primaryArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        EnsureArtistAvatarCacheLoaded();
        EnsureArtistAudienceCacheLoaded();
        bool hasCachedAvatar = _artistAvatarCache.TryGet(primaryArtist, out var cachedAvatar) && !string.IsNullOrWhiteSpace(cachedAvatar);
        bool hasCachedAudience = _artistAudienceCache.TryGet(primaryArtist, out var cachedAudience) && !string.IsNullOrWhiteSpace(cachedAudience);
        if (hasCachedAvatar && hasCachedAudience)
        {
            return (cachedAvatar, cachedAudience);
        }

        try
        {
            var bodyDict = new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?>
                {
                    ["client"] = new Dictionary<string, object?>
                    {
                        ["clientName"] = "WEB_REMIX",
                        ["clientVersion"] = "1.20250219.01.00",
                        ["hl"] = "en",
                        ["gl"] = "US"
                    }
                },
                ["query"] = primaryArtist,
                ["params"] = YtMusicArtistsFilterParams
            };

            string jsonPayload = JsonSerializer.Serialize(bodyDict);
            using var request = new HttpRequestMessage(HttpMethod.Post, YtMusicSearchEndpoint)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (cachedAvatar, cachedAudience);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var (avatarUrl, audienceSubtitle) = ExtractFirstArtistRendererSummaryRecursive(doc.RootElement);
            string? resolvedAvatar = !string.IsNullOrWhiteSpace(avatarUrl) ? avatarUrl : cachedAvatar;
            string? resolvedAudience = !string.IsNullOrWhiteSpace(audienceSubtitle) ? audienceSubtitle : cachedAudience;

            if (!string.IsNullOrWhiteSpace(resolvedAvatar) && !hasCachedAvatar)
            {
                _artistAvatarCache.Set(primaryArtist, resolvedAvatar!);
                PersistArtistAvatarCacheSafe();
            }

            if (!string.IsNullOrWhiteSpace(resolvedAudience) && !hasCachedAudience)
            {
                _artistAudienceCache.Set(primaryArtist, resolvedAudience!);
                PersistArtistAudienceCacheSafe();
            }

            return (resolvedAvatar, resolvedAudience);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve YouTube Music artist summary for '{Artist}'.", primaryArtist);
        }

        return (cachedAvatar, cachedAudience);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlbumItem>> SearchAlbumsAsync(
        string query,
        bool strictlyAlbumsOnly = true,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAlbumsSearchAsync(query, strictlyAlbumsOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CatalogQueryResult> FetchChartsCatalogAsync(CancellationToken cancellationToken = default)
    {
        var tracks = new List<TrackItem>();
        var albums = new List<AlbumItem>();
        var artists = new List<ArtistItem>();
        var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);
        var seenAlbumKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenArtistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = "FEmusic_charts"
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                payload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            if (doc is not null)
            {
                ExtractResponsiveTrackItemsRecursive(doc.RootElement, tracks, seenTrackIds);
                var parsedHome = ParseHomeFeedResponse(doc.RootElement, isPersonalized: false);
                foreach (var t in parsedHome.PersonalizedTracks)
                {
                    if (seenTrackIds.Add(t.YouTubeVideoId))
                    {
                        tracks.Add(t);
                    }
                }

                foreach (var a in parsedHome.PersonalizedAlbums)
                {
                    string key = $"{a.Title.Trim()}::{a.Artist.Trim()}";
                    if (seenAlbumKeys.Add(key))
                    {
                        albums.Add(a);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FEmusic_charts fetch failed; falling back to global top searches.");
        }

        if (tracks.Count < 18 || albums.Count < 8)
        {
            var topSongsTask = ExecuteSongsSearchAsync("Top Songs Global", useSongsFilter: true, cancellationToken);
            var topAlbumsTask = ExecuteAlbumsSearchAsync("Top Albums Global", strictlyAlbumsOnly: true, cancellationToken);
            await Task.WhenAll(topSongsTask, topAlbumsTask).ConfigureAwait(false);

            foreach (var t in await topSongsTask.ConfigureAwait(false))
            {
                if (seenTrackIds.Add(t.YouTubeVideoId))
                {
                    tracks.Add(t);
                }
            }

            foreach (var a in await topAlbumsTask.ConfigureAwait(false))
            {
                string key = $"{a.Title.Trim()}::{a.Artist.Trim()}";
                if (seenAlbumKeys.Add(key))
                {
                    albums.Add(a);
                }
            }
        }

        if (albums.Count == 0 && tracks.Count > 0)
        {
            albums.AddRange(BuildAlbumsFromTracks(tracks));
        }

        var (builtArtists, builtRecommended) = await BuildArtistsFromTracksAsync(tracks, cancellationToken).ConfigureAwait(false);
        foreach (var art in builtArtists)
        {
            if (seenArtistNames.Add(art.Name.Trim()))
            {
                artists.Add(art);
            }
        }

        return new CatalogQueryResult(tracks, albums, artists, builtRecommended);
    }

    private static (string? AvatarUrl, string? AudienceSubtitle) ExtractFirstArtistRendererSummaryRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                string thumb = ExtractHighResThumbnailUrl(renderer);
                string? audience = ExtractArtistAudienceFromRenderer(renderer);
                if (!string.IsNullOrWhiteSpace(thumb) || !string.IsNullOrWhiteSpace(audience))
                {
                    return (
                        string.IsNullOrWhiteSpace(thumb) ? null : thumb,
                        string.IsNullOrWhiteSpace(audience) ? null : audience);
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                var found = ExtractFirstArtistRendererSummaryRecursive(prop.Value);
                if (!string.IsNullOrWhiteSpace(found.AvatarUrl) || !string.IsNullOrWhiteSpace(found.AudienceSubtitle))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = ExtractFirstArtistRendererSummaryRecursive(item);
                if (!string.IsNullOrWhiteSpace(found.AvatarUrl) || !string.IsNullOrWhiteSpace(found.AudienceSubtitle))
                {
                    return found;
                }
            }
        }

        return (null, null);
    }

    private static string? ExtractArtistAudienceFromRenderer(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() < 2)
        {
            return null;
        }

        if (flexCols[1].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col1) &&
            col1.TryGetProperty("text", out var textObj) &&
            textObj.TryGetProperty("runs", out var runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                string t = run.TryGetProperty("text", out var tp) ? tp.GetString()?.Trim() ?? string.Empty : string.Empty;
                if (IsAudienceMetricText(t))
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static bool IsAudienceMetricText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("audience", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("listener", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("subscriber", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("monthly", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("mensual", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("suscriptor", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("oyente", StringComparison.OrdinalIgnoreCase);
    }

    private void PersistArtistAvatarCacheSafe()
    {
        EnsureArtistAvatarCacheLoaded();
        PersistCacheDictionarySafe(_artistAvatarCacheFilePath, _artistAvatarCache);
    }

    private void PersistArtistAudienceCacheSafe()
    {
        EnsureArtistAudienceCacheLoaded();
        PersistCacheDictionarySafe(_artistAudienceCacheFilePath, _artistAudienceCache);
    }

    private void PersistAtvCounterpartCacheSafe()
    {
        EnsureAtvCounterpartCacheLoaded();
        PersistCacheDictionarySafe(_atvCounterpartCacheFilePath, _atvCounterpartCache);
    }

    private static void PersistCacheDictionarySafe(string filePath, BoundedLruCache<string, string> cache)
    {
        try
        {
            var snapshot = cache.ToDictionarySnapshot();
            using var stream = File.Create(filePath);
            JsonSerializer.Serialize(stream, snapshot);
        }
        catch
        {
            // Ignore concurrent file write contention
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> GetAlbumTracksAsync(
        string artistName,
        string albumTitle,
        string? fallbackCoverArtUri = null,
        CancellationToken cancellationToken = default,
        string? explicitBrowseId = null)
    {
        var profile = await GetAlbumProfileAsync(
            artistName,
            albumTitle,
            fallbackCoverArtUri,
            explicitBrowseId,
            cancellationToken).ConfigureAwait(false);
        return profile?.Tracks ?? [];
    }

    /// <inheritdoc />
    public async Task<int?> GetAlbumTrackCountAsync(
        string? browseId,
        string artistName,
        string albumTitle,
        CancellationToken cancellationToken = default)
    {
        string normalizedBrowseId = browseId?.Trim() ?? string.Empty;
        string titleKey = $"{artistName?.Trim() ?? string.Empty}::{albumTitle?.Trim() ?? string.Empty}";

        if (!string.IsNullOrWhiteSpace(normalizedBrowseId) &&
            _albumTrackCountCache.TryGet(normalizedBrowseId, out int cachedById) &&
            cachedById > 0)
        {
            return cachedById;
        }

        if (!string.IsNullOrWhiteSpace(albumTitle) &&
            _albumTrackCountCache.TryGet(titleKey, out int cachedByTitle) &&
            cachedByTitle > 0)
        {
            return cachedByTitle;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(normalizedBrowseId) && !string.IsNullOrWhiteSpace(albumTitle))
            {
                normalizedBrowseId = await FindAlbumBrowseIdAsync(
                    artistName ?? string.Empty,
                    albumTitle,
                    useAlbumsFilter: true,
                    cancellationToken).ConfigureAwait(false) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(normalizedBrowseId))
            {
                return null;
            }

            if (normalizedBrowseId.StartsWith("RDCLAK", StringComparison.OrdinalIgnoreCase) ||
                normalizedBrowseId.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                normalizedBrowseId.StartsWith("OLAK5uy_", StringComparison.OrdinalIgnoreCase))
            {
                normalizedBrowseId = $"VL{normalizedBrowseId}";
            }

            if (_albumTrackCountCache.TryGet(normalizedBrowseId, out int cachedResolved) && cachedResolved > 0)
            {
                return cachedResolved;
            }

            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(hl: "en", gl: "US"),
                ["browseId"] = normalizedBrowseId
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                browsePayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
            var tracks = new List<TrackItem>();
            bool isPlaylistBrowse = normalizedBrowseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase);
            ExtractAlbumShelfTracksRecursive(
                doc.RootElement,
                artistName ?? string.Empty,
                albumTitle ?? string.Empty,
                string.Empty,
                isPlaylistBrowse,
                tracks,
                seenVideoIds);

            int realCount = tracks.Count;
            if (realCount <= 0)
            {
                realCount = ExtractTrackCountNumberRecursive(doc.RootElement);
            }

            if (realCount > 0)
            {
                _albumTrackCountCache.Set(normalizedBrowseId, realCount);
                if (!string.IsNullOrWhiteSpace(albumTitle))
                {
                    _albumTrackCountCache.Set(titleKey, realCount);
                }

                return realCount;
            }
        }
        catch
        {
            // Non-fatal metadata enrichment fallback
        }

        return null;
    }

    private static int ExtractTrackCountNumberRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("secondSubtitle", out var secondSub))
            {
                string text = ExtractAllRunsText(secondSub);
                int parsed = ParseTrackCountFromText(text);
                if (parsed > 0)
                {
                    return parsed;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                int found = ExtractTrackCountNumberRecursive(prop.Value);
                if (found > 0)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                int found = ExtractTrackCountNumberRecursive(item);
                if (found > 0)
                {
                    return found;
                }
            }
        }

        return 0;
    }

    private static int ParseTrackCountFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var match = Regex.Match(text, @"\b(\d{1,3})\s+(?:songs?|tracks?|canciones?)\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int count) && count > 0
            ? count
            : 0;
    }

    /// <inheritdoc />
    public async Task<AlbumDetailProfile?> GetAlbumProfileAsync(
        string artistName,
        string albumTitle,
        string? fallbackCoverArtUri = null,
        string? explicitBrowseId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) && string.IsNullOrWhiteSpace(explicitBrowseId))
        {
            return null;
        }

        try
        {
            // 1. Use explicit browseId (MPREb_... or VL...) if available; otherwise search YouTube Music matching title & artist
            string? albumBrowseId = !string.IsNullOrWhiteSpace(explicitBrowseId) ? explicitBrowseId.Trim() : null;
            if (string.IsNullOrWhiteSpace(albumBrowseId) &&
                (albumTitle.StartsWith("MPREb_", StringComparison.Ordinal) || albumTitle.StartsWith("VL", StringComparison.Ordinal)))
            {
                albumBrowseId = albumTitle;
            }

            if (string.IsNullOrWhiteSpace(albumBrowseId))
            {
                albumBrowseId = await FindAlbumBrowseIdAsync(artistName, albumTitle, useAlbumsFilter: true, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(albumBrowseId))
            {
                albumBrowseId = await FindAlbumBrowseIdAsync(artistName, albumTitle, useAlbumsFilter: false, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(albumBrowseId))
            {
                return null;
            }

            bool isPlaylistBrowse = albumBrowseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ||
                                    albumBrowseId.StartsWith("RDCLAK", StringComparison.OrdinalIgnoreCase) ||
                                    albumBrowseId.StartsWith("PL", StringComparison.OrdinalIgnoreCase);
            if (albumBrowseId.StartsWith("RDCLAK", StringComparison.OrdinalIgnoreCase) ||
                albumBrowseId.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                albumBrowseId.StartsWith("OLAK5uy_", StringComparison.OrdinalIgnoreCase))
            {
                albumBrowseId = $"VL{albumBrowseId}";
                isPlaylistBrowse = true;
            }

            // 2. Call the official InnerTube browse endpoint for this album/playlist
            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(hl: "en", gl: "US"),
                ["browseId"] = albumBrowseId
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                browsePayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            string resolvedTitle = albumTitle;
            string resolvedArtist = string.IsNullOrWhiteSpace(artistName) ? "Unknown Artist" : artistName;
            int releaseYear = 0;
            string description = string.Empty;
            string headerCoverUri = string.Empty;

            ExtractAlbumHeaderMetadataRecursive(
                doc.RootElement,
                ref resolvedTitle,
                ref resolvedArtist,
                ref releaseYear,
                ref description,
                ref headerCoverUri);

            string coverUri = !string.IsNullOrWhiteSpace(headerCoverUri)
                ? headerCoverUri
                : (!string.IsNullOrWhiteSpace(fallbackCoverArtUri)
                    ? fallbackCoverArtUri
                    : ExtractHighResThumbnailUrl(doc.RootElement));

            var albumTracks = new List<TrackItem>();
            var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);

            ExtractAlbumShelfTracksRecursive(
                doc.RootElement,
                resolvedArtist,
                resolvedTitle,
                coverUri,
                isPlaylistBrowse,
                albumTracks,
                seenVideoIds);

            if (albumTracks.Count > 0)
            {
                _albumTrackCountCache.Set(albumBrowseId, albumTracks.Count);
                if (!string.IsNullOrWhiteSpace(resolvedTitle))
                {
                    _albumTrackCountCache.Set($"{resolvedArtist.Trim()}::{resolvedTitle.Trim()}", albumTracks.Count);
                }
            }

            if (releaseYear > 1900)
            {
                for (int i = 0; i < albumTracks.Count; i++)
                {
                    var t = albumTracks[i];
                    albumTracks[i] = new TrackItem
                    {
                        Id = t.Id,
                        YouTubeVideoId = t.YouTubeVideoId,
                        Title = t.Title,
                        Artist = t.Artist,
                        Album = resolvedTitle,
                        Duration = t.Duration,
                        CoverArtUri = t.CoverArtUri,
                        AudioSourceUri = t.AudioSourceUri,
                        SourceType = t.SourceType,
                        IsExplicit = t.IsExplicit,
                        QualityBadge = t.QualityBadge,
                        Genre = t.Genre,
                        ReleaseYear = releaseYear,
                        TrackNumber = t.TrackNumber,
                        PlayCountText = t.PlayCountText,
                        PlayCount = t.PlayCount,
                        Lyrics = t.Lyrics
                    };
                }
            }

            var otherVersions = new List<AlbumItem>();
            var otherAlbums = new List<AlbumItem>();
            ExtractAlbumOtherReleasesRecursive(doc.RootElement, resolvedArtist, otherVersions, otherAlbums);

            // Filter out self-references
            otherVersions.RemoveAll(v =>
                string.Equals(v.BrowseId, albumBrowseId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase));

            var seenVersionTitles = new HashSet<string>(
                otherVersions.Select(v => v.Title),
                StringComparer.OrdinalIgnoreCase);

            otherAlbums.RemoveAll(a =>
                string.Equals(a.BrowseId, albumBrowseId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase) ||
                seenVersionTitles.Contains(a.Title));

            // 3. Enrich "More by [Artist]" (and alternate versions) directly from the artist's YouTube Music discography
            if (!string.IsNullOrWhiteSpace(resolvedArtist) &&
                !string.Equals(resolvedArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) &&
                otherAlbums.Count < 8)
            {
                var artistProfile = await GetArtistProfileAsync(resolvedArtist, cancellationToken).ConfigureAwait(false);
                if (artistProfile is not null)
                {
                    foreach (var release in artistProfile.CombinedAlbums.Concat(artistProfile.SinglesAndEps))
                    {
                        if (string.IsNullOrWhiteSpace(release.Title) ||
                            string.Equals(release.BrowseId, albumBrowseId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(release.Title, resolvedTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (IsAlternateEditionTitle(resolvedTitle, release.Title))
                        {
                            if (seenVersionTitles.Add(release.Title))
                            {
                                otherVersions.Add(release);
                            }
                        }
                        else if (!seenVersionTitles.Contains(release.Title) &&
                                 otherAlbums.All(a => !string.Equals(a.Title, release.Title, StringComparison.OrdinalIgnoreCase)))
                        {
                            otherAlbums.Add(release);
                        }
                    }
                }
            }

            int totalMinutes = Math.Max(1, (int)Math.Round(albumTracks.Sum(t => t.Duration.TotalMinutes)));
            string songsLabel = albumTracks.Count == 1 ? "1 song" : $"{albumTracks.Count} songs";
            string metadataSubtitle = releaseYear > 1900
                ? $"{releaseYear} • {songsLabel} • {totalMinutes} min"
                : $"{songsLabel} • {totalMinutes} min";

            return new AlbumDetailProfile
            {
                Id = $"ytm_alb_{albumBrowseId}",
                Title = resolvedTitle,
                Artist = resolvedArtist,
                CoverArtUri = coverUri,
                ReleaseYear = releaseYear,
                MetadataSubtitle = metadataSubtitle,
                Description = description,
                AlbumInformation = !string.IsNullOrWhiteSpace(description)
                    ? $"{metadataSubtitle}\n{description}"
                    : metadataSubtitle,
                Tracks = albumTracks,
                OtherVersions = otherVersions.Take(12).ToList(),
                OtherAlbums = otherAlbums.Take(16).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch official YouTube Music album profile for '{Artist} - {Album}'.", artistName, albumTitle);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ArtistDetailProfile?> GetArtistProfileAsync(
        string artistName,
        CancellationToken cancellationToken = default,
        string? explicitBrowseId = null)
    {
        if (string.IsNullOrWhiteSpace(artistName) && string.IsNullOrWhiteSpace(explicitBrowseId))
        {
            return null;
        }

        string primaryArtist = artistName.Split([',', ';', '/'], 2, StringSplitOptions.TrimEntries)[0].Trim();
        if (string.IsNullOrWhiteSpace(primaryArtist))
        {
            primaryArtist = artistName.Trim();
        }

        try
        {
            string? ucBrowseId = NormalizeExplicitArtistBrowseId(explicitBrowseId);
            string? searchAvatarUri = null;
            string? officialArtistName = null;

            if (string.IsNullOrWhiteSpace(ucBrowseId))
            {
                // 1. Search YouTube Music with the Artists filter to resolve the official channel browseId (UC...) & square avatar
                (ucBrowseId, searchAvatarUri, officialArtistName) =
                    await FindArtistBrowseIdAndAvatarAsync(primaryArtist, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(ucBrowseId))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(searchAvatarUri))
            {
                EnsureArtistAvatarCacheLoaded();
                _artistAvatarCache.Set(primaryArtist, searchAvatarUri!);
                PersistArtistAvatarCacheSafe();
            }

            // 2. Call the official InnerTube browse endpoint for this Artist channel (UC...)
            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = new Dictionary<string, object?>
                {
                    ["client"] = new Dictionary<string, object?>
                    {
                        ["clientName"] = "WEB_REMIX",
                        ["clientVersion"] = "1.20250219.01.00",
                        ["hl"] = "en",
                        ["gl"] = "US"
                    }
                },
                ["browseId"] = ucBrowseId
            };

            string jsonBody = JsonSerializer.Serialize(browsePayload);
            using var request = new HttpRequestMessage(HttpMethod.Post, YtMusicBrowseEndpoint)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");
            ApplyAccountAuthHeaders(request);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            string resolvedName = !string.IsNullOrWhiteSpace(officialArtistName) ? officialArtistName! : primaryArtist;
            string bannerUri = string.Empty;
            string subscribersText = string.Empty;
            string monthlyListenersText = string.Empty;
            string biography = string.Empty;

            // 3. Parse Header (musicImmersiveHeaderRenderer or musicVisualHeaderRenderer)
            if (doc.RootElement.TryGetProperty("header", out var headerObj))
            {
                JsonElement hdr = default;
                if (headerObj.TryGetProperty("musicImmersiveHeaderRenderer", out var immHdr))
                {
                    hdr = immHdr;
                }
                else if (headerObj.TryGetProperty("musicVisualHeaderRenderer", out var visHdr))
                {
                    hdr = visHdr;
                }

                if (hdr.ValueKind == JsonValueKind.Object)
                {
                    if (hdr.TryGetProperty("title", out var titleObj))
                    {
                        string t = ExtractAllRunsText(titleObj);
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            resolvedName = t;
                        }
                    }

                    bannerUri = ExtractHighResBannerThumbnailUrl(hdr);

                    if (hdr.TryGetProperty("monthlyListenerCount", out var mlObj))
                    {
                        monthlyListenersText = ExtractAllRunsText(mlObj);
                    }

                    if (hdr.TryGetProperty("subscriptionButton", out var subBtn) &&
                        subBtn.TryGetProperty("subscribeButtonRenderer", out var subRenderer))
                    {
                        if (subRenderer.TryGetProperty("subscriberCountText", out var scObj))
                        {
                            subscribersText = ExtractAllRunsText(scObj);
                        }
                        else if (subRenderer.TryGetProperty("shortSubscriberCountText", out var sscObj))
                        {
                            subscribersText = ExtractAllRunsText(sscObj);
                        }
                    }

                    if (hdr.TryGetProperty("description", out var descObj))
                    {
                        biography = CleanArtistBiographyText(ExtractAllRunsText(descObj));
                    }
                }
            }

            var topTracks = new List<TrackItem>();
            var studioAlbums = new List<AlbumItem>();
            var singlesAndEps = new List<AlbumItem>();
            var artistPlaylists = new List<AlbumItem>();
            var similarArtists = new List<ArtistItem>();
            var officialVideos = new List<TrackItem>();
            var featuredReleases = new List<AlbumItem>();
            string? topSongsPlaylistBrowseId = null;

            // 4. Parse Artist Page Sections (Top Songs, Albums, Singles & EPs, Videos, Playlists/Featured On, Related Artists, Description)
            if (doc.RootElement.TryGetProperty("contents", out var contentsRoot) &&
                contentsRoot.TryGetProperty("singleColumnBrowseResultsRenderer", out var scbr) &&
                scbr.TryGetProperty("tabs", out var tabsArr) &&
                tabsArr.ValueKind == JsonValueKind.Array &&
                tabsArr.GetArrayLength() > 0 &&
                tabsArr[0].TryGetProperty("tabRenderer", out var tabRenderer) &&
                tabRenderer.TryGetProperty("content", out var tabContent) &&
                tabContent.TryGetProperty("sectionListRenderer", out var slr) &&
                slr.TryGetProperty("contents", out var sectionsArr) &&
                sectionsArr.ValueKind == JsonValueKind.Array)
            {
                var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var section in sectionsArr.EnumerateArray())
                {
                    if (section.TryGetProperty("musicShelfRenderer", out var shelfRenderer))
                    {
                        if (shelfRenderer.TryGetProperty("title", out var shelfTitleObj))
                        {
                            topSongsPlaylistBrowseId ??= FindFirstPropertyString(shelfTitleObj, "browseId");
                        }

                        if (shelfRenderer.TryGetProperty("bottomEndpoint", out var bottomEp))
                        {
                            topSongsPlaylistBrowseId ??= FindFirstPropertyString(bottomEp, "browseId");
                        }

                        if (shelfRenderer.TryGetProperty("contents", out var shelfItems) &&
                            shelfItems.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in shelfItems.EnumerateArray())
                            {
                                if (item.TryGetProperty("musicResponsiveListItemRenderer", out var trackRenderer))
                                {
                                    var parsed = TryParseArtistTopTrackRenderer(trackRenderer, resolvedName, (uint)(topTracks.Count + 1));
                                    if (parsed is not null && seenTrackIds.Add(parsed.YouTubeVideoId))
                                    {
                                        topTracks.Add(parsed);
                                    }
                                }
                            }
                        }
                    }
                    else if (section.TryGetProperty("musicCarouselShelfRenderer", out var carouselRenderer))
                    {
                        string shelfHeader = string.Empty;
                        if (carouselRenderer.TryGetProperty("header", out var cHdr) &&
                            cHdr.TryGetProperty("musicCarouselShelfBasicHeaderRenderer", out var basicHdr) &&
                            basicHdr.TryGetProperty("title", out var cTitleObj))
                        {
                            shelfHeader = ExtractAllRunsText(cTitleObj);
                        }

                        ParseArtistCarouselShelf(
                            carouselRenderer,
                            shelfHeader,
                            resolvedName,
                            studioAlbums,
                            singlesAndEps,
                            artistPlaylists,
                            similarArtists,
                            officialVideos,
                            featuredReleases);
                    }
                    else if (section.TryGetProperty("musicDescriptionShelfRenderer", out var descShelf))
                    {
                        if (string.IsNullOrWhiteSpace(biography) &&
                            descShelf.TryGetProperty("description", out var dObj))
                        {
                            biography = CleanArtistBiographyText(ExtractAllRunsText(dObj));
                        }

                        if (string.IsNullOrWhiteSpace(monthlyListenersText) &&
                            descShelf.TryGetProperty("subheader", out var subHdrObj))
                        {
                            monthlyListenersText = ExtractAllRunsText(subHdrObj);
                        }
                    }
                }
            }

            // 5. Fetch extended top tracks, official videos fallback (if none in shelf), and latest release by month/year in parallel
            Task<List<TrackItem>>? extendedTopTask = (!string.IsNullOrWhiteSpace(topSongsPlaylistBrowseId) &&
                                                      topSongsPlaylistBrowseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
                ? FetchArtistTopSongsPlaylistAsync(topSongsPlaylistBrowseId!, resolvedName, cancellationToken)
                : null;

            Task<IReadOnlyList<TrackItem>>? searchVideosTask = officialVideos.Count == 0
                ? SearchArtistOfficialVideosAsync(resolvedName, 10, cancellationToken)
                : null;

            var latestReleaseTask = ResolveArtistLatestReleaseByMonthAsync(
                resolvedName,
                studioAlbums,
                singlesAndEps,
                featuredReleases,
                cancellationToken);

            if (extendedTopTask is not null)
            {
                var extendedTopTracks = await extendedTopTask.ConfigureAwait(false);
                if (extendedTopTracks.Count > topTracks.Count)
                {
                    topTracks = extendedTopTracks;
                }
            }

            if (searchVideosTask is not null)
            {
                var searchedVideos = await searchVideosTask.ConfigureAwait(false);
                foreach (var v in searchedVideos)
                {
                    if (officialVideos.All(x => !string.Equals(x.YouTubeVideoId, v.YouTubeVideoId, StringComparison.Ordinal)))
                    {
                        officialVideos.Add(v);
                    }
                }
            }

            var (latestRelease, latestTypeBadge, latestDateText) = await latestReleaseTask.ConfigureAwait(false);

            // Order top tracks by numeric play count descending when play counts are available
            if (topTracks.Any(t => t.PlayCount > 0))
            {
                topTracks = topTracks
                    .OrderByDescending(t => t.PlayCount)
                    .Take(15)
                    .Select((t, idx) => new TrackItem
                    {
                        Id = t.Id,
                        YouTubeVideoId = t.YouTubeVideoId,
                        Title = t.Title,
                        Artist = t.Artist,
                        Album = t.Album,
                        Duration = t.Duration,
                        CoverArtUri = t.CoverArtUri,
                        AudioSourceUri = t.AudioSourceUri,
                        SourceType = t.SourceType,
                        IsExplicit = t.IsExplicit,
                        QualityBadge = t.QualityBadge,
                        Genre = t.Genre,
                        ReleaseYear = t.ReleaseYear,
                        TrackNumber = (uint)(idx + 1),
                        PlayCountText = t.PlayCountText,
                        PlayCount = t.PlayCount,
                        Lyrics = t.Lyrics
                    })
                    .ToList();
            }

            string avatarFinal = !string.IsNullOrWhiteSpace(searchAvatarUri)
                ? searchAvatarUri!
                : (!string.IsNullOrWhiteSpace(bannerUri) ? bannerUri : (topTracks.FirstOrDefault()?.CoverArtUri ?? string.Empty));

            string bannerFinal = !string.IsNullOrWhiteSpace(bannerUri) ? bannerUri : avatarFinal;

            string subtitleFinal = !string.IsNullOrWhiteSpace(monthlyListenersText)
                ? monthlyListenersText
                : (!string.IsNullOrWhiteSpace(subscribersText)
                    ? $"{subscribersText} subscribers"
                    : "Official Artist");

            return new ArtistDetailProfile
            {
                Name = resolvedName,
                AvatarUri = avatarFinal,
                BannerImageUri = bannerFinal,
                SubscribersCountText = subscribersText,
                MonthlyListenersText = monthlyListenersText,
                Biography = biography,
                MetadataSubtitle = subtitleFinal,
                TopTracks = topTracks,
                CombinedAlbums = studioAlbums,
                SinglesAndEps = singlesAndEps,
                ArtistPlaylists = artistPlaylists,
                SimilarArtists = similarArtists,
                LatestRelease = latestRelease,
                LatestReleaseTypeBadge = latestTypeBadge,
                LatestReleaseDateText = latestDateText,
                OfficialVideos = officialVideos.Take(12).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch official YouTube Music artist profile for '{Artist}'.", primaryArtist);
            return null;
        }
    }

    private static string? NormalizeExplicitArtistBrowseId(string? rawBrowseId)
    {
        if (string.IsNullOrWhiteSpace(rawBrowseId))
        {
            return null;
        }

        string clean = rawBrowseId.Trim();
        if (clean.StartsWith("ytm_sim_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[8..];
        }
        else if (clean.StartsWith("ytm_artist_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[11..];
        }

        if (clean.StartsWith("MPLAUC", StringComparison.Ordinal))
        {
            clean = clean[4..];
        }

        return clean.StartsWith("UC", StringComparison.Ordinal) && clean.Length >= 10 ? clean : null;
    }

    private async Task<(string? BrowseId, string? AvatarUri, string? ArtistName)> FindArtistBrowseIdAndAvatarAsync(
        string artistName,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["query"] = artistName,
            ["params"] = YtMusicArtistsFilterParams
        };

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicSearchEndpoint,
            payload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return (null, null, null);
        }

        var candidates = new List<ArtistItem>(12);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cacheUpdated = false;
        ExtractSearchArtistsRecursive(doc.RootElement, candidates, seen, 12, ref cacheUpdated);

        if (candidates.Count == 0)
        {
            return (null, null, null);
        }

        string normTarget = NormalizeMatchText(artistName);
        foreach (var cand in candidates)
        {
            string? ucId = cand.ExtractChannelBrowseId();
            if (string.IsNullOrWhiteSpace(ucId))
            {
                continue;
            }

            if (string.Equals(cand.Name.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(normTarget) &&
                 string.Equals(NormalizeMatchText(cand.Name), normTarget, StringComparison.Ordinal)))
            {
                return (ucId, cand.AvatarUri, cand.Name.Trim());
            }
        }

        // Never blindly redirect to candidates[0] (e.g. "Sheena" -> "Sheena Ringo") when no exact artist match exists.
        return (null, null, null);
    }

    private async Task<List<TrackItem>> FetchArtistTopSongsPlaylistAsync(
        string playlistBrowseId,
        string defaultArtistName,
        CancellationToken cancellationToken)
    {
        try
        {
            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = playlistBrowseId
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                browsePayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return [];
            }

            var results = new List<TrackItem>();
            var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
            ExtractArtistTopTracksRecursive(doc.RootElement, defaultArtistName, results, seenVideoIds);
            return results.Take(15).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void ExtractArtistTopTracksRecursive(
        JsonElement element,
        string defaultArtistName,
        List<TrackItem> results,
        HashSet<string> seenVideoIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicCarouselShelfRenderer", out _))
            {
                return;
            }

            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                var track = TryParseArtistTopTrackRenderer(renderer, defaultArtistName, (uint)(results.Count + 1));
                if (track is not null && seenVideoIds.Add(track.YouTubeVideoId))
                {
                    results.Add(track);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractArtistTopTracksRecursive(prop.Value, defaultArtistName, results, seenVideoIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractArtistTopTracksRecursive(item, defaultArtistName, results, seenVideoIds);
            }
        }
    }

    private static TrackItem? TryParseArtistTopTrackRenderer(
        JsonElement renderer,
        string defaultArtist,
        uint trackNumber)
    {
        string? rawPlaylistVideoId = null;
        if (renderer.TryGetProperty("playlistItemData", out var pid) &&
            pid.TryGetProperty("videoId", out var vidProp))
        {
            rawPlaylistVideoId = vidProp.GetString();
        }

        string? videoId = ExtractPreferredAudioVideoId(renderer);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string? officialVideoId = null;
        if (!string.IsNullOrWhiteSpace(rawPlaylistVideoId) &&
            !string.Equals(rawPlaylistVideoId, videoId, StringComparison.Ordinal) &&
            string.Equals(ExtractMusicVideoType(renderer), "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase))
        {
            officialVideoId = rawPlaylistVideoId;
        }

        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() < 1)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = defaultArtist;
        string album = string.Empty;
        string playCountText = string.Empty;
        long playCount = 0;
        TimeSpan duration = TimeSpan.FromMinutes(3.5);
        int releaseYear = DateTime.Now.Year;

        for (int colIdx = 1; colIdx < flexCols.GetArrayLength(); colIdx++)
        {
            var col = flexCols[colIdx];
            string colText = ExtractRunsText(col);
            if (string.IsNullOrWhiteSpace(colText))
            {
                continue;
            }

            if (IsPlayCountLabel(colText))
            {
                playCountText = colText;
                playCount = ParseApproximatePlayCount(colText);
                continue;
            }

            string? colBrowseId = FindFirstPropertyString(col, "browseId");
            string? colPageType = FindFirstPropertyString(col, "pageType");

            if (string.Equals(colPageType, "MUSIC_PAGE_TYPE_ALBUM", StringComparison.OrdinalIgnoreCase) ||
                (colBrowseId is not null && colBrowseId.StartsWith("MPREb_", StringComparison.Ordinal)))
            {
                album = colText;
                continue;
            }

            if (colIdx == 1)
            {
                string parsedArtist = artist;
                string parsedAlbum = album;
                ParseMetadataRuns(col, ref parsedArtist, ref parsedAlbum, ref duration, ref releaseYear);
                if (!string.IsNullOrWhiteSpace(parsedArtist) &&
                    !string.Equals(parsedArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) &&
                    !IsPlayCountLabel(parsedArtist))
                {
                    artist = parsedArtist;
                }

                if (!string.IsNullOrWhiteSpace(parsedAlbum) &&
                    !string.Equals(parsedAlbum, "YouTube Music", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(parsedAlbum, "Streaming", StringComparison.OrdinalIgnoreCase))
                {
                    album = parsedAlbum;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(album))
        {
            album = title;
        }

        if (renderer.TryGetProperty("fixedColumns", out var fixedCols) &&
            fixedCols.ValueKind == JsonValueKind.Array &&
            fixedCols.GetArrayLength() > 0)
        {
            string fixedText = ExtractRunsText(fixedCols[0]).Trim();
            if (TryParseDurationString(fixedText, out var parsedFixedDuration))
            {
                duration = parsedFixedDuration;
            }
        }

        string coverArtUri = ExtractHighResThumbnailUrl(renderer);
        if (string.IsNullOrWhiteSpace(coverArtUri))
        {
            coverArtUri = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_{videoId}",
            YouTubeVideoId = videoId,
            OfficialVideoId = officialVideoId ?? string.Empty,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverArtUri = coverArtUri,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(renderer),
            QualityBadge = "HQ AAC",
            Genre = "Streaming",
            ReleaseYear = releaseYear,
            TrackNumber = trackNumber,
            PlayCountText = playCountText,
            PlayCount = playCount,
            Lyrics =
            [
                new LyricLine(TimeSpan.Zero, $"♪ {title} — {artist}", IsSynchronized: false),
                new LyricLine(TimeSpan.FromSeconds(4), $"Album • {album}", IsSynchronized: false)
            ]
        };
    }

    private static void ParseArtistCarouselShelf(
        JsonElement carouselRenderer,
        string shelfHeader,
        string artistName,
        List<AlbumItem> studioAlbums,
        List<AlbumItem> singlesAndEps,
        List<AlbumItem> artistPlaylists,
        List<ArtistItem> similarArtists,
        List<TrackItem> officialVideos,
        List<AlbumItem> featuredReleases)
    {
        if (!carouselRenderer.TryGetProperty("contents", out var itemsArr) ||
            itemsArr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string headerLower = shelfHeader.ToLowerInvariant();
        bool isVideosShelf = headerLower.Contains("video") || headerLower.Contains("vídeo") || headerLower.Contains("videoclip");
        bool isLatestReleaseShelf = headerLower.Contains("latest") || headerLower.Contains("último") ||
                                    headerLower.Contains("ultimo") || headerLower.Contains("new release") ||
                                    headerLower.Contains("lanzamiento");
        bool isSinglesShelf = headerLower.Contains("single") || headerLower.Contains("sencillo") || headerLower.Contains("ep");
        bool isAlbumsShelf = !isSinglesShelf && (headerLower.Contains("álbum") || headerLower.Contains("album"));
        bool isPlaylistsShelf = headerLower.Contains("destacado") || headerLower.Contains("featured") ||
                                headerLower.Contains("lista") || headerLower.Contains("playlist");
        bool isSimilarShelf = headerLower.Contains("guste") || headerLower.Contains("like") ||
                              headerLower.Contains("similar") || headerLower.Contains("fans");

        foreach (var itemWrapper in itemsArr.EnumerateArray())
        {
            if (!itemWrapper.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
            {
                continue;
            }

            string title = twoRow.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj) : string.Empty;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string subtitle = twoRow.TryGetProperty("subtitle", out var sObj) ? ExtractAllRunsText(sObj) : string.Empty;
            string? browseId = null;
            string? pageType = null;
            string? watchVideoId = null;

            if (twoRow.TryGetProperty("navigationEndpoint", out var navEp))
            {
                browseId = FindFirstPropertyString(navEp, "browseId");
                pageType = FindFirstPropertyString(navEp, "pageType");
                watchVideoId = FindFirstPropertyString(navEp, "videoId");
            }

            string aspectRatio = twoRow.TryGetProperty("aspectRatio", out var arProp) ? (arProp.GetString() ?? string.Empty) : string.Empty;
            string musicVideoType = ExtractMusicVideoType(twoRow);

            // Detect Official Videos shelf or 16:9 / OMV video items
            if (isVideosShelf ||
                !string.IsNullOrWhiteSpace(watchVideoId) && string.IsNullOrWhiteSpace(browseId) ||
                aspectRatio.Contains("RECTANGLE_16_9", StringComparison.OrdinalIgnoreCase) ||
                IsVideoMusicVideoType(musicVideoType))
            {
                var videoTrack = TryParseArtistVideoTwoRowItem(twoRow, artistName, (uint)(officialVideos.Count + 1));
                if (videoTrack is not null &&
                    officialVideos.All(v => !string.Equals(v.YouTubeVideoId, videoTrack.YouTubeVideoId, StringComparison.Ordinal)))
                {
                    officialVideos.Add(videoTrack);
                }

                continue;
            }

            string coverUri = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
                ? ExtractHighResThumbnailUrl(tr)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(coverUri))
            {
                coverUri = ExtractHighResThumbnailUrl(twoRow);
            }

            if (isSimilarShelf ||
                string.Equals(pageType, "MUSIC_PAGE_TYPE_ARTIST", StringComparison.OrdinalIgnoreCase) ||
                (browseId is not null && browseId.StartsWith("UC", StringComparison.Ordinal)))
            {
                if (similarArtists.All(a => !string.Equals(a.Name, title, StringComparison.OrdinalIgnoreCase)))
                {
                    string audienceText = IsAudienceMetricText(subtitle) ? subtitle : string.Empty;
                    similarArtists.Add(new ArtistItem
                    {
                        Id = !string.IsNullOrWhiteSpace(browseId) ? browseId : $"ytm_sim_{title}",
                        BrowseId = browseId ?? string.Empty,
                        Name = title,
                        AvatarUri = coverUri,
                        Genre = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : "Similar Artist",
                        MonthlyAudienceText = audienceText,
                        IsRecommended = true,
                        SourceType = MediaSourceType.Online
                    });
                }

                continue;
            }

            if (isPlaylistsShelf ||
                string.Equals(pageType, "MUSIC_PAGE_TYPE_PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
                (browseId is not null && (browseId.StartsWith("VL", StringComparison.Ordinal) || browseId.StartsWith("RDCLAK", StringComparison.Ordinal))))
            {
                if (!string.IsNullOrWhiteSpace(browseId) &&
                    artistPlaylists.All(p => !string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    artistPlaylists.Add(new AlbumItem
                    {
                        Id = $"ytm_pl_{browseId}",
                        BrowseId = browseId,
                        Title = title,
                        Artist = artistName,
                        Subtitle = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : shelfHeader,
                        ReleaseYear = 0,
                        TrackCount = ParseTrackCountFromText(subtitle),
                        CoverArtUri = coverUri,
                        SourceType = MediaSourceType.Online
                    });
                }

                continue;
            }

            if (isSinglesShelf ||
                subtitle.Contains("Single", StringComparison.OrdinalIgnoreCase) ||
                subtitle.Contains("Sencillo", StringComparison.OrdinalIgnoreCase) ||
                subtitle.Contains("EP", StringComparison.OrdinalIgnoreCase))
            {
                int year = ExtractYearFromSubtitle(subtitle);
                int month = ExtractMonthFromText(subtitle);
                bool isEp = subtitle.Contains("EP", StringComparison.OrdinalIgnoreCase);
                var singleItem = new AlbumItem
                {
                    Id = !string.IsNullOrWhiteSpace(browseId) ? $"ytm_single_{browseId}" : $"ytm_single_{title}",
                    BrowseId = browseId ?? string.Empty,
                    Title = title,
                    Artist = artistName,
                    Subtitle = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (year > 0 ? $"Single • {year}" : "Single / EP"),
                    ReleaseYear = year > 1900 ? year : 0,
                    ReleaseMonth = month,
                    ReleaseDateText = FormatReleaseMonthYearBadge(year, month),
                    IsSingleOrEp = true,
                    TrackCount = isEp ? ParseTrackCountFromText(subtitle) : 1,
                    CoverArtUri = coverUri,
                    SourceType = MediaSourceType.Online
                };

                if (isLatestReleaseShelf)
                {
                    featuredReleases.Add(singleItem);
                }

                if (singlesAndEps.All(s => !string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    singlesAndEps.Add(singleItem);
                }

                continue;
            }

            if (isAlbumsShelf ||
                isLatestReleaseShelf ||
                string.Equals(pageType, "MUSIC_PAGE_TYPE_ALBUM", StringComparison.OrdinalIgnoreCase) ||
                (browseId is not null && browseId.StartsWith("MPREb_", StringComparison.Ordinal)))
            {
                int year = ExtractYearFromSubtitle(subtitle);
                int month = ExtractMonthFromText(subtitle);
                var albumItem = new AlbumItem
                {
                    Id = !string.IsNullOrWhiteSpace(browseId) ? $"ytm_alb_{browseId}" : $"ytm_alb_{title}",
                    BrowseId = browseId ?? string.Empty,
                    Title = title,
                    Artist = artistName,
                    Subtitle = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (year > 0 ? $"Album • {year}" : "Album"),
                    ReleaseYear = year > 1900 ? year : 0,
                    ReleaseMonth = month,
                    ReleaseDateText = FormatReleaseMonthYearBadge(year, month),
                    IsSingleOrEp = false,
                    TrackCount = ParseTrackCountFromText(subtitle),
                    CoverArtUri = coverUri,
                    SourceType = MediaSourceType.Online
                };

                if (isLatestReleaseShelf)
                {
                    featuredReleases.Add(albumItem);
                }

                if (studioAlbums.All(a => !string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    studioAlbums.Add(albumItem);
                }
            }
        }
    }

    private static int ExtractYearFromSubtitle(string subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return 0;
        }

        var m = YearRegex().Match(subtitle);
        return m.Success && int.TryParse(m.Value, out int yr) ? yr : 0;
    }

    private static bool IsPlayCountLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("reproducciones", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("views", StringComparison.OrdinalIgnoreCase);
    }

    private static long ParseApproximatePlayCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // Normalize non-breaking spaces and decimal separators (e.g., "60 M reproducciones", "1,2 mil M", "850 K")
        string cleaned = text.Replace('\u00A0', ' ').Trim();
        var numMatch = PlayCountNumberRegex().Match(cleaned);
        if (!numMatch.Success)
        {
            return 0;
        }

        string rawNum = numMatch.Groups[1].Value;
        if (rawNum.Contains(',') && !rawNum.Contains('.'))
        {
            rawNum = rawNum.Replace(',', '.');
        }
        else
        {
            rawNum = rawNum.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        if (!double.TryParse(rawNum, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            return 0;
        }

        string upper = cleaned.ToUpperInvariant();
        if (upper.Contains("MIL M") || upper.Contains("B "))
        {
            val *= 1_000_000_000d;
        }
        else if (upper.Contains(" M ") || upper.EndsWith(" M") || upper.Contains("M "))
        {
            val *= 1_000_000d;
        }
        else if (upper.Contains(" K ") || upper.Contains(" MIL ") || upper.Contains("K "))
        {
            val *= 1_000d;
        }

        return (long)Math.Round(val);
    }

    private static string CleanArtistBiographyText(string rawBio)
    {
        if (string.IsNullOrWhiteSpace(rawBio))
        {
            return string.Empty;
        }

        int wikiIdx = rawBio.IndexOf("Fuente: Wikipedia", StringComparison.OrdinalIgnoreCase);
        if (wikiIdx < 0)
        {
            wikiIdx = rawBio.IndexOf("From Wikipedia", StringComparison.OrdinalIgnoreCase);
        }

        if (wikiIdx > 0)
        {
            rawBio = rawBio[..wikiIdx];
        }

        return rawBio.Trim();
    }

    private static string ExtractHighResBannerThumbnailUrl(JsonElement renderer)
    {
        string? rawUrl = ExtractRawThumbnailUrlFromElement(renderer);
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        // Upgrade wide banner thumbnail to ultra-crisp 1920x800 while preserving trailing Google CDN tokens/flags
        return NormalizeHighResImageUrl(rawUrl, targetWidthPx: 1920, targetHeightPx: 800);
    }

    private static string ExtractAllRunsText(JsonElement textContainer)
    {
        if (textContainer.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!textContainer.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var tProp))
            {
                sb.Append(tProp.GetString());
            }
        }

        return sb.ToString().Trim();
    }

    private static void ExtractAlbumHeaderMetadataRecursive(
        JsonElement element,
        ref string title,
        ref string artist,
        ref int releaseYear,
        ref string description,
        ref string coverUri)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveHeaderRenderer", out var respHdr) &&
                respHdr.ValueKind == JsonValueKind.Object)
            {
                ParseAlbumHeaderRenderer(respHdr, ref title, ref artist, ref releaseYear, ref description, ref coverUri);
            }
            else if (element.TryGetProperty("musicDetailHeaderRenderer", out var detHdr) &&
                     detHdr.ValueKind == JsonValueKind.Object)
            {
                ParseAlbumHeaderRenderer(detHdr, ref title, ref artist, ref releaseYear, ref description, ref coverUri);
            }
            else if (element.TryGetProperty("musicDescriptionShelfRenderer", out var descShelf) &&
                     descShelf.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrWhiteSpace(description) &&
                    descShelf.TryGetProperty("description", out var descObj))
                {
                    description = CleanArtistBiographyText(ExtractAllRunsText(descObj));
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractAlbumHeaderMetadataRecursive(prop.Value, ref title, ref artist, ref releaseYear, ref description, ref coverUri);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractAlbumHeaderMetadataRecursive(item, ref title, ref artist, ref releaseYear, ref description, ref coverUri);
            }
        }
    }

    private static void ParseAlbumHeaderRenderer(
        JsonElement hdr,
        ref string title,
        ref string artist,
        ref int releaseYear,
        ref string description,
        ref string coverUri)
    {
        if (hdr.TryGetProperty("title", out var titleObj))
        {
            string parsedTitle = ExtractAllRunsText(titleObj);
            if (!string.IsNullOrWhiteSpace(parsedTitle))
            {
                title = parsedTitle;
            }
        }

        if (hdr.TryGetProperty("straplineTextOne", out var strapObj))
        {
            string parsedArtist = ExtractAllRunsText(strapObj);
            if (!string.IsNullOrWhiteSpace(parsedArtist))
            {
                artist = parsedArtist;
            }
        }

        if (hdr.TryGetProperty("subtitle", out var subObj))
        {
            string subText = ExtractAllRunsText(subObj);
            int yr = ExtractYearFromSubtitle(subText);
            if (yr > 1900)
            {
                releaseYear = yr;
            }

            if (string.IsNullOrWhiteSpace(artist) ||
                string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
            {
                string subArtist = ExtractArtistFromSubtitle(subText);
                if (!string.IsNullOrWhiteSpace(subArtist))
                {
                    artist = subArtist;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(coverUri))
        {
            string thumb = ExtractHighResThumbnailUrl(hdr);
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                coverUri = thumb;
            }
        }

        if (string.IsNullOrWhiteSpace(description) &&
            hdr.TryGetProperty("description", out var descProp) &&
            descProp.ValueKind == JsonValueKind.Object)
        {
            if (descProp.TryGetProperty("musicDescriptionShelfRenderer", out var mdsr) &&
                mdsr.ValueKind == JsonValueKind.Object &&
                mdsr.TryGetProperty("description", out var innerDesc))
            {
                description = CleanArtistBiographyText(ExtractAllRunsText(innerDesc));
            }
            else
            {
                description = CleanArtistBiographyText(ExtractAllRunsText(descProp));
            }
        }
    }

    private static bool IsAlternateEditionTitle(string titleA, string titleB)
    {
        if (string.IsNullOrWhiteSpace(titleA) || string.IsNullOrWhiteSpace(titleB))
        {
            return false;
        }

        string baseA = ExtractCanonicalAlbumBaseTitle(titleA);
        string baseB = ExtractCanonicalAlbumBaseTitle(titleB);
        if (baseA.Length < 3 || baseB.Length < 3)
        {
            return false;
        }

        return string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(titleA.Trim(), titleB.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCanonicalAlbumBaseTitle(string rawTitle)
    {
        string s = rawTitle.Trim();
        int parenIdx = s.IndexOf('(');
        if (parenIdx > 2)
        {
            s = s[..parenIdx];
        }

        int bracketIdx = s.IndexOf('[');
        if (bracketIdx > 2)
        {
            s = s[..bracketIdx];
        }

        int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 2)
        {
            s = s[..dashIdx];
        }

        return s.Trim();
    }

    private async Task<string?> FindAlbumBrowseIdAsync(
        string artistName,
        string albumTitle,
        bool useAlbumsFilter,
        CancellationToken cancellationToken)
    {
        string query = $"{artistName} {albumTitle}".Trim();
        var bodyDict = new Dictionary<string, object?>
        {
            ["context"] = new Dictionary<string, object?>
            {
                ["client"] = new Dictionary<string, object?>
                {
                    ["clientName"] = "WEB_REMIX",
                    ["clientVersion"] = "1.20250219.01.00",
                    ["hl"] = "en",
                    ["gl"] = "US"
                }
            },
            ["query"] = query
        };

        if (useAlbumsFilter)
        {
            bodyDict["params"] = YtMusicAlbumsFilterParams;
        }

        string jsonPayload = JsonSerializer.Serialize(bodyDict);
        using var request = new HttpRequestMessage(HttpMethod.Post, YtMusicSearchEndpoint)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://music.youtube.com/");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string rawJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var candidates = new List<(string BrowseId, string Title, string Subtitle)>();
            CollectAlbumSearchCandidatesRecursive(doc.RootElement, candidates);

            if (candidates.Count > 0)
            {
                string targetTitle = albumTitle.Trim();
                string targetBase = ExtractCanonicalAlbumBaseTitle(targetTitle);
                string targetArtist = artistName.Trim();

                var best = candidates
                    .Select(c =>
                    {
                        int score = 0;
                        if (string.Equals(c.Title, targetTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 120;
                        }
                        else if (string.Equals(ExtractCanonicalAlbumBaseTitle(c.Title), targetBase, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 85;
                        }
                        else if (c.Title.Contains(targetTitle, StringComparison.OrdinalIgnoreCase) ||
                                 targetTitle.Contains(c.Title, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 45;
                        }

                        if (!string.IsNullOrWhiteSpace(targetArtist) &&
                            c.Subtitle.Contains(targetArtist, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 50;
                        }

                        return (c.BrowseId, Score: score);
                    })
                    .OrderByDescending(x => x.Score)
                    .First();

                if (best.Score > 0 || string.IsNullOrWhiteSpace(albumTitle))
                {
                    return best.BrowseId;
                }
            }
        }
        catch
        {
            // Fall through to regex fallback
        }

        var match = AlbumBrowseIdRegex().Match(rawJson);
        return match.Success ? match.Value : null;
    }

    private static void CollectAlbumSearchCandidatesRecursive(
        JsonElement element,
        List<(string BrowseId, string Title, string Subtitle)> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                string? browseId = renderer.TryGetProperty("navigationEndpoint", out var navEp)
                    ? FindFirstPropertyString(navEp, "browseId")
                    : FindFirstPropertyString(renderer, "browseId");

                if (!string.IsNullOrWhiteSpace(browseId) &&
                    browseId.StartsWith("MPREb_", StringComparison.Ordinal))
                {
                    string title = string.Empty;
                    string subtitle = string.Empty;
                    if (renderer.TryGetProperty("flexColumns", out var flexCols) &&
                        flexCols.ValueKind == JsonValueKind.Array &&
                        flexCols.GetArrayLength() > 0)
                    {
                        title = ExtractRunsText(flexCols[0]).Trim();
                        if (flexCols.GetArrayLength() > 1)
                        {
                            subtitle = ExtractRunsText(flexCols[1]).Trim();
                        }
                    }

                    candidates.Add((browseId, title, subtitle));
                }

                return;
            }

            if (element.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
            {
                string? browseId = twoRow.TryGetProperty("navigationEndpoint", out var navEp)
                    ? FindFirstPropertyString(navEp, "browseId")
                    : null;

                if (!string.IsNullOrWhiteSpace(browseId) &&
                    browseId.StartsWith("MPREb_", StringComparison.Ordinal))
                {
                    string title = twoRow.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj) : string.Empty;
                    string subtitle = twoRow.TryGetProperty("subtitle", out var sObj) ? ExtractAllRunsText(sObj) : string.Empty;
                    candidates.Add((browseId, title, subtitle));
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                CollectAlbumSearchCandidatesRecursive(prop.Value, candidates);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectAlbumSearchCandidatesRecursive(item, candidates);
            }
        }
    }

    private static void ExtractAlbumShelfTracksRecursive(
        JsonElement element,
        string defaultArtist,
        string albumTitle,
        string albumCoverUri,
        bool isPlaylistBrowse,
        List<TrackItem> results,
        HashSet<string> seenVideoIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Only extract from musicShelfRenderer (official album tracklist), NOT from carousel recommendations at the bottom
            if (element.TryGetProperty("musicCarouselShelfRenderer", out _))
            {
                return;
            }

            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                var track = TryParseAlbumTrackRenderer(renderer, defaultArtist, albumTitle, albumCoverUri, isPlaylistBrowse, (uint)(results.Count + 1));
                if (track is not null && seenVideoIds.Add(track.YouTubeVideoId))
                {
                    results.Add(track);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractAlbumShelfTracksRecursive(prop.Value, defaultArtist, albumTitle, albumCoverUri, isPlaylistBrowse, results, seenVideoIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractAlbumShelfTracksRecursive(item, defaultArtist, albumTitle, albumCoverUri, isPlaylistBrowse, results, seenVideoIds);
            }
        }
    }

    private static TrackItem? TryParseAlbumTrackRenderer(
        JsonElement renderer,
        string defaultArtist,
        string albumTitle,
        string albumCoverUri,
        bool isPlaylistBrowse,
        uint trackNumber)
    {
        string? rawPlaylistVideoId = null;
        if (renderer.TryGetProperty("playlistItemData", out var pid) &&
            pid.TryGetProperty("videoId", out var vidProp))
        {
            rawPlaylistVideoId = vidProp.GetString();
        }

        string? videoId = ExtractPreferredAudioVideoId(renderer);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string? officialVideoId = null;
        if (!string.IsNullOrWhiteSpace(rawPlaylistVideoId) &&
            !string.Equals(rawPlaylistVideoId, videoId, StringComparison.Ordinal) &&
            string.Equals(ExtractMusicVideoType(renderer), "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase))
        {
            officialVideoId = rawPlaylistVideoId;
        }

        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() < 1)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = defaultArtist;
        string parsedAlbum = albumTitle;
        TimeSpan duration = TimeSpan.FromMinutes(3.5);
        int releaseYear = DateTime.Now.Year;

        if (flexCols.GetArrayLength() > 1)
        {
            ParseMetadataRuns(flexCols[1], ref artist, ref parsedAlbum, ref duration, ref releaseYear);
            if (string.Equals(artist, "Unknown Artist", StringComparison.OrdinalIgnoreCase) ||
                artist.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
                artist.Contains("views", StringComparison.OrdinalIgnoreCase))
            {
                artist = defaultArtist;
            }
        }

        if (renderer.TryGetProperty("fixedColumns", out var fixedCols) &&
            fixedCols.ValueKind == JsonValueKind.Array &&
            fixedCols.GetArrayLength() > 0)
        {
            string fixedText = ExtractRunsText(fixedCols[0]).Trim();
            if (TryParseDurationString(fixedText, out var parsedFixedDuration))
            {
                duration = parsedFixedDuration;
            }
        }

        string rowCover = ExtractHighResThumbnailUrl(renderer);
        if (isPlaylistBrowse &&
            !string.IsNullOrWhiteSpace(rawPlaylistVideoId) &&
            !string.Equals(rawPlaylistVideoId, videoId, StringComparison.Ordinal) &&
            rowCover.Contains("i.ytimg.com/vi/", StringComparison.OrdinalIgnoreCase))
        {
            rowCover = string.Empty;
        }

        string finalCover = isPlaylistBrowse
            ? (!string.IsNullOrWhiteSpace(rowCover) ? rowCover : albumCoverUri)
            : (!string.IsNullOrWhiteSpace(albumCoverUri) ? albumCoverUri : rowCover);
        if (string.IsNullOrWhiteSpace(finalCover))
        {
            finalCover = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_{videoId}",
            YouTubeVideoId = videoId,
            OfficialVideoId = officialVideoId ?? string.Empty,
            Title = title,
            Artist = artist,
            Album = albumTitle,
            Duration = duration,
            CoverArtUri = finalCover,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(renderer),
            QualityBadge = "HQ AAC",
            Genre = "Streaming",
            ReleaseYear = releaseYear,
            TrackNumber = trackNumber,
            Lyrics =
            [
                new LyricLine(TimeSpan.Zero, $"♪ {title} — {artist}", IsSynchronized: false),
                new LyricLine(TimeSpan.FromSeconds(4), $"Album • {albumTitle}", IsSynchronized: false)
            ]
        };
    }

    private sealed record ResolvedAudioStream(
        string Url,
        string ClientName,
        string UserAgent,
        string? Origin,
        string? Referer,
        string? Cookie,
        long? ContentLength,
        bool AllowChunkedRangeRetry,
        DateTimeOffset ExpiresAt,
        string Codec = "AAC",
        int BitrateKbps = 0,
        int SampleRateHz = 0,
        int ChannelCount = 2);

    private sealed record PlayerClientSpec(
        string ClientName,
        string? ClientId,
        string ClientVersion,
        string EndpointUrl,
        string UserAgent,
        string Origin,
        string Referer,
        string? DeviceMake = null,
        string? DeviceModel = null,
        string? OsName = null,
        string? OsVersion = null,
        int? AndroidSdkVersion = null,
        int? UtcOffsetMinutes = null,
        bool SendOriginOnDownload = true,
        bool SendPlaybackContext = false,
        bool DecipherNChallenge = true,
        bool AllowChunkedRangeRetry = false);

    private sealed record AudioFormatCandidate(
        string Url,
        string Sp = "",
        string Signature = "",
        long? ContentLength = null,
        string MimeType = "",
        string Codec = "AAC",
        int BitrateBps = 0,
        int SampleRateHz = 0,
        int ChannelCount = 2);

    private static List<(PlayerClientSpec Spec, bool UseAuth)> BuildStreamPlayerSpecs(bool isSignedIn)
    {
        var visionOsSpec = new PlayerClientSpec(
            ClientName: "VISIONOS",
            ClientId: null,
            ClientVersion: VisionOsClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: VisionOsUserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            DeviceMake: "Apple",
            DeviceModel: "RealityDevice14,1",
            OsName: "visionOS",
            OsVersion: "2.0",
            UtcOffsetMinutes: 0,
            SendOriginOnDownload: false,
            SendPlaybackContext: false,
            DecipherNChallenge: true,
            AllowChunkedRangeRetry: false);

        var webRemixSpec = new PlayerClientSpec(
            ClientName: "WEB_REMIX",
            ClientId: WebRemixClientId,
            ClientVersion: WebRemixClientVersion,
            EndpointUrl: YtMusicPlayerEndpoint,
            UserAgent: DesktopUserAgent,
            Origin: YouTubeAuthHelper.YouTubeMusicOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeMusicOrigin}/",
            SendOriginOnDownload: true,
            SendPlaybackContext: true,
            DecipherNChallenge: true,
            AllowChunkedRangeRetry: true);

        var mwebSpec = new PlayerClientSpec(
            ClientName: "MWEB",
            ClientId: MwebClientId,
            ClientVersion: MwebClientVersion,
            EndpointUrl: MwebPlayerEndpoint,
            UserAgent: MobileWebUserAgent,
            Origin: "https://m.youtube.com",
            Referer: "https://m.youtube.com/",
            SendOriginOnDownload: true,
            SendPlaybackContext: true,
            DecipherNChallenge: true,
            AllowChunkedRangeRetry: true);

        var tvHtml5Spec = new PlayerClientSpec(
            ClientName: "TVHTML5",
            ClientId: TvHtml5ClientId,
            ClientVersion: TvHtml5ClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: TvHtml5UserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            SendOriginOnDownload: true,
            SendPlaybackContext: true,
            DecipherNChallenge: true,
            AllowChunkedRangeRetry: true);

        var webSpec = new PlayerClientSpec(
            ClientName: "WEB",
            ClientId: WebClientId,
            ClientVersion: WebClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: DesktopUserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            SendOriginOnDownload: true,
            SendPlaybackContext: true,
            DecipherNChallenge: true,
            AllowChunkedRangeRetry: true);

        var androidSpec = new PlayerClientSpec(
            ClientName: "ANDROID",
            ClientId: AndroidClientId,
            ClientVersion: AndroidClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: AndroidUserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            DeviceMake: "Google",
            DeviceModel: "Pixel 7",
            OsName: "Android",
            OsVersion: "14",
            AndroidSdkVersion: 34,
            SendOriginOnDownload: false,
            DecipherNChallenge: false);

        var iosSpec = new PlayerClientSpec(
            ClientName: "IOS",
            ClientId: IosClientId,
            ClientVersion: IosClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: IosUserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            DeviceMake: "Apple",
            DeviceModel: "iPhone16,2",
            OsName: "iPhone",
            OsVersion: "18.3.2.22D82",
            SendOriginOnDownload: false,
            DecipherNChallenge: false);

        var androidVrSpec = new PlayerClientSpec(
            ClientName: "ANDROID_VR",
            ClientId: AndroidVrClientId,
            ClientVersion: AndroidVrClientVersion,
            EndpointUrl: YtPlayerEndpoint,
            UserAgent: AndroidVrUserAgent,
            Origin: YouTubeAuthHelper.YouTubeWwwOrigin,
            Referer: $"{YouTubeAuthHelper.YouTubeWwwOrigin}/",
            DeviceMake: "Oculus",
            DeviceModel: "Quest 3",
            OsName: "Android",
            OsVersion: "12L",
            AndroidSdkVersion: 32,
            SendOriginOnDownload: false,
            DecipherNChallenge: false);

        // Never pair web Cookie/SAPISIDHASH auth with non-web clients (InnerTube rejects them with HTTP 400).
        // When signed in, start with unauthenticated VISIONOS (fast & direct for non-age-restricted tracks),
        // then try authenticated web-family clients for age-restricted/explicit tracks, then unauth native fallbacks.
        return isSignedIn
            ? [
                (visionOsSpec with { ClientName = "VISIONOS_ANON" }, false),
                (webRemixSpec, true),
                (mwebSpec, true),
                (tvHtml5Spec, true),
                (webSpec, true),
                (webRemixSpec with { ClientName = "WEB_REMIX_ANON" }, false),
                (mwebSpec with { ClientName = "MWEB_ANON" }, false),
                (androidSpec with { ClientName = "ANDROID_ANON" }, false),
                (iosSpec with { ClientName = "IOS_ANON" }, false),
                (androidVrSpec with { ClientName = "ANDROID_VR_ANON" }, false)
            ]
            : [
                (visionOsSpec, false),
                (webRemixSpec with { ClientName = "WEB_REMIX_ANON" }, false),
                (mwebSpec with { ClientName = "MWEB_ANON" }, false),
                (androidSpec, false),
                (iosSpec, false),
                (androidVrSpec, false)
            ];
    }

    private const long MaxStreamTraceFileBytes = 256 * 1024;

    private void TraceStream(string message)
    {
#if DEBUG
        try
        {
            lock (_traceLock)
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n";
                if (File.Exists(_streamTraceFilePath) &&
                    new FileInfo(_streamTraceFilePath).Length >= MaxStreamTraceFileBytes)
                {
                    File.WriteAllText(_streamTraceFilePath, line);
                }
                else
                {
                    File.AppendAllText(_streamTraceFilePath, line);
                }
            }
        }
        catch
        {
            // Ignore trace IO errors
        }
#else
        _ = message;
#endif
    }

    private static bool IsPlayabilityBlocked(string? status)
    {
        string clean = status?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clean))
        {
            return false;
        }

        return !string.Equals(clean, "OK", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(clean, "LIVE_STREAM_OFFLINE", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<string?> ResolveAudioStreamUriAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAudioStreamWithMetadataAsync(
            videoId,
            StreamingAudioQuality.High160,
            cancellationToken).ConfigureAwait(false);
        return resolved?.PlaybackUri;
    }

    /// <inheritdoc />
    public async Task<ResolvedPlaybackStream?> ResolveAudioStreamWithMetadataAsync(
        string videoId,
        StreamingAudioQuality requestedQuality,
        CancellationToken cancellationToken = default,
        bool resolveCanonicalAtv = true)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        LastStreamFailureReason = null;
        videoId = ExtractCleanVideoId(videoId);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        StreamingAudioQuality effectiveYtQuality = requestedQuality == StreamingAudioQuality.FlacLossless
            ? StreamingAudioQuality.High160
            : requestedQuality;
        string tierSuffix = StreamingQualitySelector.GetCacheTierSuffix(effectiveYtQuality);
        string variantSuffix = resolveCanonicalAtv ? tierSuffix : $"omv_{tierSuffix}";
        string streamCacheKey = $"{videoId}:{variantSuffix}";

        TraceStream($"ResolveAudioStreamWithMetadataAsync start raw/clean='{videoId}' quality={effectiveYtQuality} canonicalAtv={resolveCanonicalAtv} isAuth={IsAuthenticated} expired={IsSessionExpired}");

        // Validate saved session / sync account metadata if not yet checked this run
        if (IsAuthenticated && !_triedAccountMetaSync)
        {
            await EnsureAccountSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
            TraceStream($"Post-sync account meta: isAuth={IsAuthenticated} expired={IsSessionExpired} hasDataSync={!string.IsNullOrWhiteSpace(_dataSyncId)}");
        }

        if (resolveCanonicalAtv)
        {
            // Ensure standard Song playback streams/caches the canonical studio audio song (MUSIC_VIDEO_TYPE_ATV) rather than OMV/UGC
            videoId = await ResolveCanonicalAtvVideoIdAsync(videoId, cancellationToken).ConfigureAwait(false);
            streamCacheKey = $"{videoId}:{variantSuffix}";
            TraceStream($"Canonical ATV videoId='{videoId}'");
        }

        // 1. Instant Cache: Check quality-scoped cache file first (and legacy .m4a for High160)
        string targetFilePath = Path.Combine(_cacheDirectory, $"{videoId}_{variantSuffix}.m4a");
        if (TryGetValidCachedFile(targetFilePath, out string? existingPath) && existingPath is not null)
        {
            return BuildResolvedYouTubeStreamFromFile(existingPath, requestedQuality, effectiveYtQuality, downloadedNetworkBytes: 0);
        }

        if (resolveCanonicalAtv && effectiveYtQuality == StreamingAudioQuality.High160)
        {
            string legacyPath = Path.Combine(_cacheDirectory, $"{videoId}.m4a");
            if (TryGetValidCachedFile(legacyPath, out string? legacyExisting) && legacyExisting is not null)
            {
                return BuildResolvedYouTubeStreamFromFile(legacyExisting, requestedQuality, effectiveYtQuality, downloadedNetworkBytes: 0);
            }
        }

        // 2. Acquire per-video+quality download lock from fixed striped pool to deduplicate concurrent requests
        var downloadLock = GetStripedLock($"audio:{streamCacheKey}", StringComparer.Ordinal);
        await downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetValidCachedFile(targetFilePath, out existingPath) && existingPath is not null)
            {
                return BuildResolvedYouTubeStreamFromFile(existingPath, requestedQuality, effectiveYtQuality, downloadedNetworkBytes: 0);
            }

            // 3. Multi-client InnerTube resolver (uses authenticated web clients with user cookies for age-restricted/explicit tracks)
            var failedClients = new HashSet<string>(StringComparer.Ordinal);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                ResolvedAudioStream? resolved = null;
                if (_streamCache.TryGet(streamCacheKey, out var cachedStream) &&
                    cachedStream.ExpiresAt > DateTimeOffset.UtcNow &&
                    !failedClients.Contains(cachedStream.ClientName))
                {
                    resolved = cachedStream;
                }
                else
                {
                    resolved = await ResolveDirectAudioStreamAsync(videoId, failedClients, effectiveYtQuality, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null)
                    {
                        _streamCache.Set(streamCacheKey, resolved);
                    }
                }

                if (resolved is null)
                {
                    TraceStream($"ResolveDirectAudioStreamAsync returned null for '{videoId}' (failed={string.Join(",", failedClients)})");
                    break;
                }

                string tempFilePath = Path.Combine(_cacheDirectory, $"{videoId}_{variantSuffix}_{Guid.NewGuid():N}.part");
                try
                {
                    string downloadUrl = BuildStreamDownloadUrl(resolved.Url, resolved.ContentLength);
                    long downloaded = await DownloadUrlToFileAsync(downloadUrl, resolved, tempFilePath, cancellationToken)
                        .ConfigureAwait(false);
                    TraceStream($"DownloadUrlToFileAsync ({resolved.ClientName}) result={downloaded}");

                    if (downloaded == -403 &&
                        resolved.AllowChunkedRangeRetry &&
                        !resolved.Url.Contains("range=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(tempFilePath))
                        {
                            try { File.Delete(tempFilePath); } catch { }
                        }

                        _logger.LogInformation("Retrying {Client} with chunked range download for '{VideoId}'.", resolved.ClientName, videoId);
                        downloaded = await DownloadChunkedToFileAsync(resolved, tempFilePath, cancellationToken)
                            .ConfigureAwait(false);
                        TraceStream($"DownloadChunkedToFileAsync ({resolved.ClientName}) result={downloaded}");
                    }

                    if (downloaded > 50_000)
                    {
                        NeutralizeDashMp4EditList(tempFilePath);
                        if (File.Exists(targetFilePath))
                        {
                            File.Delete(targetFilePath);
                        }

                        File.Move(tempFilePath, targetFilePath, overwrite: true);
                        long fileLen = new FileInfo(targetFilePath).Length;
                        TraceStream($"Cached audio ({resolved.ClientName}) for '{videoId}' tier={tierSuffix} size={fileLen}");
                        return BuildResolvedYouTubeStreamFromFile(
                            targetFilePath,
                            requestedQuality,
                            effectiveYtQuality,
                            downloadedNetworkBytes: fileLen,
                            hintCodec: resolved.Codec,
                            hintBitrateKbps: resolved.BitrateKbps,
                            hintSampleRateHz: resolved.SampleRateHz,
                            hintChannels: resolved.ChannelCount);
                    }

                    _streamCache.Remove(streamCacheKey);
                    failedClients.Add(resolved.ClientName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    TraceStream($"Error downloading stream ({resolved.ClientName}) for '{videoId}': {ex.GetType().Name}: {ex.Message}");
                    _streamCache.Remove(streamCacheKey);
                    failedClients.Add(resolved.ClientName);
                }
                finally
                {
                    if (File.Exists(tempFilePath))
                    {
                        try { File.Delete(tempFilePath); } catch { }
                    }
                }
            }

            // 4. Emergency fallback: YoutubeExplode (in case InnerTube cipher/download failed)
            try
            {
                _logger.LogInformation("Attempting YoutubeExplode fallback for '{VideoId}'...", videoId);
                var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, cancellationToken).ConfigureAwait(false);
                var allAudioStreams = manifest.GetAudioOnlyStreams().ToList();
                int maxBps = StreamingQualitySelector.GetTargetMaxBitrateBps(effectiveYtQuality);
                var audioStreamInfo = effectiveYtQuality is StreamingAudioQuality.Low64 or StreamingAudioQuality.Regular128
                    ? allAudioStreams
                        .Where(s => s.Bitrate.BitsPerSecond <= maxBps)
                        .OrderByDescending(s => s.Container == Container.Mp4 ? 1 : 0)
                        .ThenByDescending(s => s.Bitrate.BitsPerSecond)
                        .FirstOrDefault()
                      ?? allAudioStreams.OrderBy(s => s.Bitrate.BitsPerSecond).FirstOrDefault()
                    : allAudioStreams.Where(s => s.Container == Container.Mp4).GetWithHighestBitrate()
                      ?? allAudioStreams.GetWithHighestBitrate();

                if (audioStreamInfo is not null)
                {
                    string tempFallback = Path.Combine(_cacheDirectory, $"{videoId}_{tierSuffix}_{Guid.NewGuid():N}.part");
                    try
                    {
                        await _youtubeClient.Videos.Streams.DownloadAsync(audioStreamInfo, tempFallback, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (File.Exists(tempFallback) && new FileInfo(tempFallback).Length > 50_000)
                        {
                            NeutralizeDashMp4EditList(tempFallback);
                            if (File.Exists(targetFilePath))
                            {
                                File.Delete(targetFilePath);
                            }

                            File.Move(tempFallback, targetFilePath, overwrite: true);
                            long fileLen = new FileInfo(targetFilePath).Length;
                            return BuildResolvedYouTubeStreamFromFile(
                                targetFilePath,
                                requestedQuality,
                                effectiveYtQuality,
                                downloadedNetworkBytes: fileLen,
                                hintCodec: StreamingQualitySelector.ExtractCodecLabel(audioStreamInfo is IAudioStreamInfo aInfo ? aInfo.AudioCodec : "AAC"),
                                hintBitrateKbps: (int)Math.Round(audioStreamInfo.Bitrate.KiloBitsPerSecond));
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempFallback))
                        {
                            try { File.Delete(tempFallback); } catch { }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "YoutubeExplode fallback failed for '{VideoId}'.", videoId);
            }
        }
        finally
        {
            downloadLock.Release();
        }

        return null;
    }

    private static ResolvedPlaybackStream BuildResolvedYouTubeStreamFromFile(
        string filePath,
        StreamingAudioQuality requestedQuality,
        StreamingAudioQuality actualQuality,
        long downloadedNetworkBytes,
        string? hintCodec = null,
        int hintBitrateKbps = 0,
        int hintSampleRateHz = 0,
        int hintChannels = 0)
    {
        long fileSize = 0;
        string codec = string.IsNullOrWhiteSpace(hintCodec) ? "AAC" : hintCodec;
        int bitrateKbps = hintBitrateKbps;
        int sampleRateHz = hintSampleRateHz;
        int channels = hintChannels;

        try
        {
            if (File.Exists(filePath))
            {
                fileSize = new FileInfo(filePath).Length;
                using var tagFile = TagLib.File.Create(filePath);
                var props = tagFile.Properties;
                if (props is not null)
                {
                    if (props.AudioBitrate > 0) bitrateKbps = props.AudioBitrate;
                    if (props.AudioSampleRate > 0) sampleRateHz = props.AudioSampleRate;
                    if (props.AudioChannels > 0) channels = props.AudioChannels;
                    var firstCodec = props.Codecs?.FirstOrDefault(c => c is not null);
                    if (firstCodec is not null && !string.IsNullOrWhiteSpace(firstCodec.Description))
                    {
                        string desc = firstCodec.Description;
                        if (desc.Contains("MPEG-4 Audio", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("AAC", StringComparison.OrdinalIgnoreCase))
                        {
                            codec = "AAC";
                        }
                        else if (desc.Contains("Opus", StringComparison.OrdinalIgnoreCase))
                        {
                            codec = "Opus";
                        }
                        else
                        {
                            codec = desc;
                        }
                    }
                }
            }
        }
        catch
        {
            // Keep format hints if TagLibSharp cannot inspect container
        }

        if (channels <= 0)
        {
            channels = 2;
        }

        return new ResolvedPlaybackStream(
            PlaybackUri: filePath,
            SourceKind: PlaybackSourceKind.YouTubeMusic,
            ProviderLabel: "YouTube Music",
            RequestedQuality: requestedQuality,
            ActualQuality: actualQuality,
            IsFlacFallback: false,
            FallbackReason: null,
            Codec: codec,
            BitrateKbps: bitrateKbps,
            SampleRateHz: sampleRateHz,
            ChannelCount: channels,
            BitDepth: 0,
            FileSizeBytes: fileSize,
            Isrc: string.Empty,
            DownloadedNetworkBytes: downloadedNetworkBytes);
    }

    private bool TryGetValidCachedFile(string targetFilePath, out string? path)
    {
        if (File.Exists(targetFilePath))
        {
            try
            {
                var fi = new FileInfo(targetFilePath);
                if (fi.Length > 50_000)
                {
                    NeutralizeDashMp4EditList(targetFilePath);
                    _logger.LogInformation("Using cached YouTube Music audio '{Path}' ({Size:N0} bytes).", targetFilePath, fi.Length);
                    path = targetFilePath;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed inspecting cached file '{Path}'.", targetFilePath);
            }
        }

        path = null;
        return false;
    }

    private async Task<ResolvedAudioStream?> ResolveDirectAudioStreamAsync(
        string canonicalVideoId,
        HashSet<string> skipClients,
        StreamingAudioQuality requestedQuality,
        CancellationToken cancellationToken)
    {
        bool isSignedIn = IsAuthenticated;
        var specs = BuildStreamPlayerSpecs(isSignedIn);

        (string VisitorData, string Cookies)? anonVisitorAndCookies = null;
        async Task<(string VisitorData, string Cookies)?> EnsureAnonVisitorAndCookiesAsync()
        {
            anonVisitorAndCookies ??= await GetVisitorDataAndCookiesAsync(cancellationToken).ConfigureAwait(false);
            return anonVisitorAndCookies;
        }

        string? userVisitorData = !string.IsNullOrWhiteSpace(_sessionVisitorData) ? _sessionVisitorData : null;

        bool sawAuthBlock = false;
        foreach (var (spec, useAuth) in specs)
        {
            if (skipClients.Contains(spec.ClientName))
            {
                continue;
            }

            string? visitorData;
            string? anonCookies;
            if (useAuth)
            {
                visitorData = userVisitorData ?? _cachedVisitorData;
                anonCookies = null;
            }
            else
            {
                var pair = await EnsureAnonVisitorAndCookiesAsync().ConfigureAwait(false);
                visitorData = !string.IsNullOrWhiteSpace(pair?.VisitorData) ? pair!.Value.VisitorData : _cachedVisitorData;
                anonCookies = !string.IsNullOrWhiteSpace(pair?.Cookies) ? pair!.Value.Cookies : YouTubeAuthHelper.DefaultSocsCookie;
            }

            var (resolved, blockedByAuth) = await ExecutePlayerStreamResolutionAsync(
                canonicalVideoId,
                spec,
                useAuth,
                visitorData,
                anonCookies,
                requestedQuality,
                cancellationToken).ConfigureAwait(false);
            sawAuthBlock |= blockedByAuth;
            if (resolved is not null)
            {
                return resolved;
            }
        }

        if (sawAuthBlock)
        {
            LastStreamFailureReason = IsSessionExpired
                ? "Your YouTube Music session expired. Sign in again in Settings > YouTube Music to play Explicit / age-restricted songs."
                : (!IsAuthenticated
                    ? "Sign in to YouTube Music in Settings to play Explicit / age-restricted songs."
                    : "YouTube Music blocked age-restricted playback for this track.");
        }

        return null;
    }

    private async Task<(ResolvedAudioStream? Stream, bool BlockedByAuth)> ExecutePlayerStreamResolutionAsync(
        string videoId,
        PlayerClientSpec spec,
        bool useAuth,
        string? visitorData,
        string? anonCookies,
        StreamingAudioQuality requestedQuality,
        CancellationToken cancellationToken)
    {
        bool sawAuthBlock = false;
        int? sts = spec.SendPlaybackContext
            ? await _scriptResolver.GetSignatureTimestampAsync(cancellationToken).ConfigureAwait(false)
            : null;

        bool canDelegate = useAuth && IsAuthenticated &&
            (YouTubeAuthHelper.GetDelegatedPageId(_dataSyncId) is not null ||
             YouTubeAuthHelper.GetOnBehalfOfUser(_dataSyncId) is not null);
        bool[] attemptPageIds = canDelegate ? [true, false] : [false];

        foreach (bool includeDelegation in attemptPageIds)
        {
            try
            {
                var clientObj = new Dictionary<string, object?>
                {
                    ["clientName"] = spec.ClientName.EndsWith("_ANON", StringComparison.Ordinal)
                        ? spec.ClientName[..^"_ANON".Length]
                        : spec.ClientName,
                    ["clientVersion"] = spec.ClientVersion,
                    ["hl"] = "en",
                    ["gl"] = "US"
                };
                if (!string.IsNullOrWhiteSpace(spec.DeviceMake)) clientObj["deviceMake"] = spec.DeviceMake;
                if (!string.IsNullOrWhiteSpace(spec.DeviceModel)) clientObj["deviceModel"] = spec.DeviceModel;
                if (!string.IsNullOrWhiteSpace(spec.OsName)) clientObj["osName"] = spec.OsName;
                if (!string.IsNullOrWhiteSpace(spec.OsVersion)) clientObj["osVersion"] = spec.OsVersion;
                if (spec.AndroidSdkVersion.HasValue) clientObj["androidSdkVersion"] = spec.AndroidSdkVersion.Value;
                if (!string.IsNullOrWhiteSpace(visitorData)) clientObj["visitorData"] = visitorData;
                if (spec.UtcOffsetMinutes.HasValue) clientObj["utcOffsetMinutes"] = spec.UtcOffsetMinutes.Value;

                var contextObj = new Dictionary<string, object?>
                {
                    ["client"] = clientObj
                };

                if (useAuth && IsAuthenticated)
                {
                    var userObj = new Dictionary<string, object?>
                    {
                        ["lockedSafetyMode"] = false
                    };
                    if (includeDelegation)
                    {
                        string? onBehalfOf = YouTubeAuthHelper.GetOnBehalfOfUser(_dataSyncId);
                        if (!string.IsNullOrWhiteSpace(onBehalfOf))
                        {
                            userObj["onBehalfOfUser"] = onBehalfOf;
                        }
                    }

                    contextObj["user"] = userObj;
                }

                var payload = new Dictionary<string, object?>
                {
                    ["context"] = contextObj,
                    ["videoId"] = videoId,
                    ["contentCheckOk"] = true,
                    ["racyCheckOk"] = true
                };

                if (sts.HasValue)
                {
                    payload["playbackContext"] = new Dictionary<string, object?>
                    {
                        ["contentPlaybackContext"] = new Dictionary<string, object?>
                        {
                            ["signatureTimestamp"] = sts.Value,
                            ["html5Preference"] = "HTML5_PREF_WANTS"
                        }
                    };
                }

                string jsonBody = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, spec.EndpointUrl)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                request.Headers.TryAddWithoutValidation("User-Agent", spec.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation("Origin", spec.Origin);
                request.Headers.TryAddWithoutValidation("Referer", spec.Referer);
                if (!string.IsNullOrWhiteSpace(spec.ClientId))
                {
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", spec.ClientId);
                }

                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", spec.ClientVersion);

                if (useAuth && IsAuthenticated)
                {
                    ApplyPlayerAuthHeaders(
                        request,
                        origin: spec.Origin,
                        includePageId: includeDelegation,
                        fallbackVisitorData: visitorData);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(visitorData))
                    {
                        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
                    }

                    if (!string.IsNullOrWhiteSpace(anonCookies))
                    {
                        request.Headers.TryAddWithoutValidation("Cookie", anonCookies);
                    }
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                int statusCode = (int)response.StatusCode;
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                {
                    TraceStream($"{spec.ClientName} player HTTP {statusCode} auth={useAuth} video={videoId} delegation={includeDelegation}");
                    if ((statusCode == 400 || statusCode == 403) && includeDelegation)
                    {
                        continue;
                    }

                    return (null, sawAuthBlock);
                }

                using var doc = ParseInnerTubeJson(body);
                if (doc is null)
                {
                    TraceStream($"{spec.ClientName} failed JSON parse video={videoId}");
                    return (null, sawAuthBlock);
                }

                string playStatus = string.Empty;
                string playReason = string.Empty;
                if (doc.RootElement.TryGetProperty("playabilityStatus", out var playability))
                {
                    if (playability.TryGetProperty("status", out var stProp))
                    {
                        playStatus = stProp.GetString()?.Trim() ?? string.Empty;
                    }

                    if (playability.TryGetProperty("reason", out var rProp))
                    {
                        playReason = rProp.GetString()?.Trim() ?? string.Empty;
                    }
                }

                TraceStream($"{spec.ClientName} player status='{playStatus}' reason='{playReason}' auth={useAuth} sts={sts} video={videoId}");

                if (string.Equals(playStatus, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(playStatus, "AGE_VERIFICATION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                    playReason.Contains("age", StringComparison.OrdinalIgnoreCase) ||
                    playReason.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
                {
                    sawAuthBlock = true;
                }

                if (IsPlayabilityBlocked(playStatus))
                {
                    if (includeDelegation)
                    {
                        continue;
                    }

                    return (null, sawAuthBlock);
                }

                if (!doc.RootElement.TryGetProperty("streamingData", out var streamingData))
                {
                    TraceStream($"{spec.ClientName} missing streamingData video={videoId}");
                    return (null, sawAuthBlock);
                }

                var candidate = PickBestAudioCandidate(streamingData, requestedQuality);
                if (candidate is null)
                {
                    TraceStream($"{spec.ClientName} no audio candidate video={videoId}");
                    return (null, sawAuthBlock);
                }

                bool hadSig = !string.IsNullOrWhiteSpace(candidate.Signature);
                string? resolvedUrl = hadSig || spec.DecipherNChallenge
                    ? await _scriptResolver.TransformStreamUrlAsync(
                        candidate.Url,
                        candidate.Sp,
                        candidate.Signature,
                        cancellationToken).ConfigureAwait(false)
                    : candidate.Url;

                if (string.IsNullOrWhiteSpace(resolvedUrl) ||
                    !YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(resolvedUrl))
                {
                    TraceStream($"{spec.ClientName} decipher/validation FAILED hadSig={hadSig} sp='{candidate.Sp}' video={videoId}");
                    return (null, sawAuthBlock);
                }
                TraceStream($"{spec.ClientName} decipher OK hadSig={hadSig} changed={!string.Equals(resolvedUrl, candidate.Url, StringComparison.Ordinal)} video={videoId}");

                string? downloadCookie = null;
                if (spec.SendOriginOnDownload)
                {
                    string? rawCookie = useAuth && IsAuthenticated
                        ? YouTubeAuthHelper.EnsureConsentCookie(_cookieHeader)
                        : (!string.IsNullOrWhiteSpace(anonCookies) ? anonCookies : null);
                    downloadCookie = YouTubeSecurityPolicy.StripAuthCookiesForCdn(rawCookie);
                }

                return (new ResolvedAudioStream(
                    Url: resolvedUrl,
                    ClientName: spec.ClientName,
                    UserAgent: spec.UserAgent,
                    Origin: spec.SendOriginOnDownload ? spec.Origin : null,
                    Referer: spec.SendOriginOnDownload ? spec.Referer : null,
                    Cookie: downloadCookie,
                    ContentLength: candidate.ContentLength,
                    AllowChunkedRangeRetry: spec.AllowChunkedRangeRetry,
                    ExpiresAt: DateTimeOffset.UtcNow.AddHours(4),
                    Codec: candidate.Codec,
                    BitrateKbps: candidate.BitrateBps > 0 ? (int)Math.Round(candidate.BitrateBps / 1000.0) : 0,
                    SampleRateHz: candidate.SampleRateHz,
                    ChannelCount: candidate.ChannelCount), sawAuthBlock);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Client} player resolution error (auth={Auth}) for '{VideoId}'.", spec.ClientName, useAuth, videoId);
                if (!includeDelegation)
                {
                    return (null, sawAuthBlock);
                }
            }
        }

        return (null, sawAuthBlock);
    }

    private static HttpRequestMessage BuildStreamDownloadRequest(string url, ResolvedAudioStream resolved)
    {
        if (!YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(url))
        {
            throw new InvalidOperationException("Blocked download request to non-allowlisted stream URL.");
        }

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", resolved.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(resolved.Origin))
        {
            req.Headers.TryAddWithoutValidation("Origin", resolved.Origin);
        }

        if (!string.IsNullOrWhiteSpace(resolved.Referer))
        {
            req.Headers.TryAddWithoutValidation("Referer", resolved.Referer);
        }

        string? sanitizedCookie = YouTubeSecurityPolicy.StripAuthCookiesForCdn(resolved.Cookie);
        if (!string.IsNullOrWhiteSpace(sanitizedCookie))
        {
            req.Headers.TryAddWithoutValidation("Cookie", sanitizedCookie);
        }

        return req;
    }

    private async Task<long> DownloadUrlToFileAsync(
        string downloadUrl,
        ResolvedAudioStream resolved,
        string tempFilePath,
        CancellationToken cancellationToken)
    {
        using var req = BuildStreamDownloadRequest(downloadUrl, resolved);
        using var response = await _downloadHttpClient.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        int code = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode && code != 206)
        {
            _logger.LogDebug("Download failed ({Client}) with HTTP {Code}.", resolved.ClientName, code);
            return -code;
        }

        await using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        return fs.Length;
    }

    private async Task<long> DownloadChunkedToFileAsync(
        ResolvedAudioStream resolved,
        string tempFilePath,
        CancellationToken cancellationToken)
    {
        if (!resolved.ContentLength.HasValue || resolved.ContentLength.Value <= 0)
        {
            return -1;
        }

        long totalExpected = resolved.ContentLength.Value;
        const long chunkSize = 512_000;
        long offset = 0;
        string sep = resolved.Url.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        await using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        while (offset < totalExpected)
        {
            long end = Math.Min(offset + chunkSize - 1, totalExpected - 1);
            string chunkUrl = $"{resolved.Url}{sep}range={offset}-{end}&hl=en";
            using var req = BuildStreamDownloadRequest(chunkUrl, resolved);
            using var response = await _downloadHttpClient.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            int code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode && code != 206)
            {
                _logger.LogDebug("Chunk download failed ({Client} {Offset}-{End}) HTTP {Code}.", resolved.ClientName, offset, end, code);
                return -code;
            }

            long before = fs.Position;
            await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            long written = fs.Position - before;
            if (written <= 0)
            {
                break;
            }

            offset += written;
        }

        await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
        return fs.Length;
    }

    private static string BuildStreamDownloadUrl(string streamUrl, long? contentLength)
    {
        if (streamUrl.Contains("range=", StringComparison.OrdinalIgnoreCase))
        {
            return streamUrl;
        }

        long? clen = contentLength is > 0 ? contentLength : null;
        if (!clen.HasValue)
        {
            var m = ContentLengthQueryRegex().Match(streamUrl);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long parsedClen) && parsedClen > 0)
            {
                clen = parsedClen;
            }
        }

        long rangeEnd = clen.HasValue ? clen.Value - 1 : 9_898_988;
        string sep = streamUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{streamUrl}{sep}range=0-{rangeEnd}&hl=en";
    }

    private static AudioFormatCandidate? PickBestAudioCandidate(
        JsonElement streamingData,
        StreamingAudioQuality requestedQuality = StreamingAudioQuality.High160)
    {
        if (streamingData.TryGetProperty("adaptiveFormats", out var adaptiveFormats) &&
            adaptiveFormats.ValueKind == JsonValueKind.Array)
        {
            var best = PickBestAudioCandidateFromArray(adaptiveFormats, requireAudioMime: true, requestedQuality);
            if (best is not null)
            {
                return best;
            }
        }

        if (streamingData.TryGetProperty("formats", out var formats) &&
            formats.ValueKind == JsonValueKind.Array)
        {
            var best = PickBestAudioCandidateFromArray(formats, requireAudioMime: false, requestedQuality);
            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }

    private static AudioFormatCandidate? PickBestAudioCandidateFromArray(
        JsonElement formatsArray,
        bool requireAudioMime,
        StreamingAudioQuality requestedQuality)
    {
        var descriptors = new List<YouTubeAudioFormatDescriptor>();

        foreach (var format in formatsArray.EnumerateArray())
        {
            var candidate = ParseAudioFormatCandidate(format);
            if (candidate is null)
            {
                continue;
            }

            string mimeType = format.TryGetProperty("mimeType", out var mimeProp)
                ? (mimeProp.GetString() ?? string.Empty).Trim()
                : string.Empty;

            bool hasAudio = requireAudioMime
                ? mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                : mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                  (format.TryGetProperty("audioQuality", out var aqProp) && !string.IsNullOrWhiteSpace(aqProp.GetString())) ||
                  (format.TryGetProperty("audioChannels", out var acProp) && acProp.TryGetInt32(out int ch) && ch > 0);

            if (!hasAudio)
            {
                continue;
            }

            int bitrate = format.TryGetProperty("bitrate", out var brProp) && brProp.TryGetInt32(out int br)
                ? br
                : 0;

            int sampleRate = 0;
            if (format.TryGetProperty("audioSampleRate", out var srProp))
            {
                if (srProp.ValueKind == JsonValueKind.String && int.TryParse(srProp.GetString(), out int srParsed))
                {
                    sampleRate = srParsed;
                }
                else if (srProp.ValueKind == JsonValueKind.Number && srProp.TryGetInt32(out int srNum))
                {
                    sampleRate = srNum;
                }
            }

            int channels = format.TryGetProperty("audioChannels", out var chProp) && chProp.TryGetInt32(out int chVal) && chVal > 0
                ? chVal
                : 2;

            descriptors.Add(new YouTubeAudioFormatDescriptor(
                Url: candidate.Url,
                Sp: candidate.Sp,
                Signature: candidate.Signature,
                MimeType: mimeType,
                Codec: StreamingQualitySelector.ExtractCodecLabel(mimeType),
                BitrateBps: bitrate,
                SampleRateHz: sampleRate,
                Channels: channels,
                ContentLength: candidate.ContentLength));
        }

        var selected = StreamingQualitySelector.SelectBestFormat(descriptors, requestedQuality);
        return selected is null
            ? null
            : new AudioFormatCandidate(
                Url: selected.Url,
                Sp: selected.Sp,
                Signature: selected.Signature,
                ContentLength: selected.ContentLength,
                MimeType: selected.MimeType,
                Codec: selected.Codec,
                BitrateBps: selected.BitrateBps,
                SampleRateHz: selected.SampleRateHz,
                ChannelCount: selected.Channels);
    }

    private static AudioFormatCandidate? ParseAudioFormatCandidate(JsonElement format)
    {
        long? contentLength = null;
        if (format.TryGetProperty("contentLength", out var clProp))
        {
            if (clProp.ValueKind == JsonValueKind.String &&
                long.TryParse(clProp.GetString()?.Trim(), out long parsedStr))
            {
                contentLength = parsedStr;
            }
            else if (clProp.ValueKind == JsonValueKind.Number && clProp.TryGetInt64(out long parsedNum))
            {
                contentLength = parsedNum;
            }
        }

        if (format.TryGetProperty("url", out var urlProp))
        {
            string? directUrl = urlProp.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                return new AudioFormatCandidate(directUrl, ContentLength: contentLength);
            }
        }

        string? cipherStr = null;
        if (format.TryGetProperty("signatureCipher", out var scProp))
        {
            cipherStr = scProp.GetString()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(cipherStr) && format.TryGetProperty("cipher", out var cProp))
        {
            cipherStr = cProp.GetString()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(cipherStr))
        {
            return null;
        }

        string parsedUrl = string.Empty;
        string parsedSp = "signature";
        string parsedSig = string.Empty;

        foreach (string pair in cipherStr.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx <= 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..eqIdx].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pair[(eqIdx + 1)..].Replace('+', ' '));

            switch (key)
            {
                case "url":
                    parsedUrl = value.Trim();
                    break;
                case "sp":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        parsedSp = value.Trim();
                    }
                    break;
                case "s":
                    parsedSig = value.Trim();
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(parsedUrl)
            ? null
            : new AudioFormatCandidate(parsedUrl, parsedSp, parsedSig, contentLength);
    }

    /// <summary>
    /// Verifies whether <paramref name="videoId"/> is a pure YouTube Music studio audio track (<c>MUSIC_VIDEO_TYPE_ATV</c>).
    /// When the ID points to an official music video (<c>OMV</c>) or user upload (<c>UGC</c>), resolves the canonical
    /// <c>ATV</c> counterpart via InnerTube <c>player</c> + filtered <c>Songs</c> search and cleans up any stale OMV cache.
    /// </summary>
    private async Task<string> ResolveCanonicalAtvVideoIdAsync(
        string videoId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return videoId;
        }

        EnsureAtvCounterpartCacheLoaded();
        if (_atvCounterpartCache.TryGet(videoId, out var cachedAtvId) &&
            !string.IsNullOrWhiteSpace(cachedAtvId))
        {
            return cachedAtvId;
        }

        try
        {
            Dictionary<string, object?> BuildAtvPayload(bool includeUser) => new()
            {
                ["context"] = BuildWebRemixContext(includeUser: includeUser),
                ["videoId"] = videoId,
                ["contentCheckOk"] = true,
                ["racyCheckOk"] = true
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicPlayerEndpoint,
                BuildAtvPayload(includeUser: true),
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            if (doc is null || !doc.RootElement.TryGetProperty("videoDetails", out var videoDetails))
            {
                return videoId;
            }

            string playabilityStatus = doc.RootElement.TryGetProperty("playabilityStatus", out var playObj) &&
                                       playObj.TryGetProperty("status", out var stProp)
                ? stProp.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            string musicVideoType = videoDetails.TryGetProperty("musicVideoType", out var mvtProp)
                ? mvtProp.GetString() ?? string.Empty
                : string.Empty;

            if (IsPureAudioMusicVideoType(musicVideoType))
            {
                if (!IsPlayabilityBlocked(playabilityStatus))
                {
                    _atvCounterpartCache.Set(videoId, videoId);
                    PersistAtvCounterpartCacheSafe();
                }

                return videoId;
            }

            if (string.Equals(playabilityStatus, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(playabilityStatus, "UNPLAYABLE", StringComparison.OrdinalIgnoreCase))
            {
                return videoId;
            }

            string rawTitle = videoDetails.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString() ?? string.Empty
                : string.Empty;
            string rawAuthor = videoDetails.TryGetProperty("author", out var authorProp)
                ? authorProp.GetString() ?? string.Empty
                : string.Empty;

            bool looksVideoTitled = rawTitle.Contains("Official Video", StringComparison.OrdinalIgnoreCase) ||
                                    rawTitle.Contains("Music Video", StringComparison.OrdinalIgnoreCase) ||
                                    rawTitle.Contains("Visualizer", StringComparison.OrdinalIgnoreCase) ||
                                    rawTitle.Contains("Video Oficial", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(rawTitle) ||
                (!IsVideoMusicVideoType(musicVideoType) && !string.IsNullOrWhiteSpace(musicVideoType) && !looksVideoTitled))
            {
                return videoId;
            }

            bool isUgc = string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase);
            string cleanAuthor = CleanAuthorForAtvSearch(rawAuthor);
            string cleanTitle = CleanTitleForAtvSearch(rawTitle, cleanAuthor);

            if (isUgc)
            {
                // Fan-uploaded UGC videos often use the uploader's channel name as author and put "Artist - Title" in rawTitle.
                int splitIdx = cleanTitle.IndexOf(" - ", StringComparison.Ordinal);
                if (splitIdx <= 0)
                {
                    splitIdx = cleanTitle.IndexOf(" – ", StringComparison.Ordinal);
                }

                if (splitIdx > 0 && splitIdx < cleanTitle.Length - 3)
                {
                    cleanAuthor = cleanTitle[..splitIdx].Trim();
                    cleanTitle = cleanTitle[(splitIdx + 3)..].Trim();
                }
            }

            string searchQuery = string.IsNullOrWhiteSpace(cleanAuthor)
                ? cleanTitle
                : $"{cleanAuthor} {cleanTitle}".Trim();

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return videoId;
            }

            var songCandidates = await ExecuteSongsSearchAsync(searchQuery, useSongsFilter: true, cancellationToken).ConfigureAwait(false);
            var bestCandidate = SelectBestAtvCandidate(videoId, cleanTitle, cleanAuthor, songCandidates, requireArtistMatch: isUgc);
            if (bestCandidate is null)
            {
                return videoId;
            }

            _logger.LogInformation(
                "Upgraded OMV/video '{OriginalVideoId}' ({Type}) to canonical ATV song '{AtvVideoId}' ('{Title}' - '{Artist}').",
                videoId,
                string.IsNullOrWhiteSpace(musicVideoType) ? "UnknownType" : musicVideoType,
                bestCandidate.YouTubeVideoId,
                bestCandidate.Title,
                bestCandidate.Artist);

            _atvCounterpartCache.Set(videoId, bestCandidate.YouTubeVideoId);
            _atvCounterpartCache.Set(bestCandidate.YouTubeVideoId, bestCandidate.YouTubeVideoId);
            PersistAtvCounterpartCacheSafe();
            DeleteStaleCachedAudioSafe(videoId);

            return bestCandidate.YouTubeVideoId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Canonical ATV resolution skipped for '{VideoId}'.", videoId);
            return videoId;
        }
    }

    private void DeleteStaleCachedAudioSafe(string obsoleteVideoId)
    {
        try
        {
            string obsoletePath = Path.Combine(_cacheDirectory, $"{obsoleteVideoId}.m4a");
            if (File.Exists(obsoletePath))
            {
                File.Delete(obsoletePath);
                _logger.LogInformation("Removed stale OMV audio cache file '{Path}'.", obsoletePath);
            }
        }
        catch
        {
            // Ignore file lock or transient IO failure during cleanup
        }
    }

    private static TrackItem? SelectBestAtvCandidate(
        string originalVideoId,
        string cleanTitle,
        string cleanAuthor,
        List<TrackItem> candidates,
        bool requireArtistMatch = false)
    {
        string normTargetTitle = NormalizeMatchText(cleanTitle);
        string normTargetAuthor = NormalizeMatchText(cleanAuthor);
        if (string.IsNullOrWhiteSpace(normTargetTitle))
        {
            return null;
        }

        string[] variantKeywords = ["remix", "live", "acoustic", "sped up", "slowed", "karaoke", "instrumental", "cover", "reverb"];
        TrackItem? best = null;
        int bestScore = int.MinValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate.YouTubeVideoId) ||
                string.Equals(candidate.YouTubeVideoId, originalVideoId, StringComparison.Ordinal))
            {
                continue;
            }

            string normCandTitle = NormalizeMatchText(CleanTitleForAtvSearch(candidate.Title, cleanAuthor));
            string normCandArtist = NormalizeMatchText(candidate.Artist);
            if (string.IsNullOrWhiteSpace(normCandTitle))
            {
                continue;
            }

            int score = 0;
            if (string.Equals(normCandTitle, normTargetTitle, StringComparison.Ordinal))
            {
                score += 100;
            }
            else if (normCandTitle.Contains(normTargetTitle, StringComparison.Ordinal) ||
                     normTargetTitle.Contains(normCandTitle, StringComparison.Ordinal))
            {
                score += 65;
            }
            else
            {
                var targetWords = normTargetTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var candWords = normCandTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int matches = targetWords.Count(w => w.Length >= 2 && candWords.Contains(w, StringComparer.Ordinal));
                if (targetWords.Length == 0 || matches * 2 < targetWords.Length)
                {
                    continue;
                }

                score += 45;
            }

            bool artistMatched = false;
            if (!string.IsNullOrWhiteSpace(normTargetAuthor) && !string.IsNullOrWhiteSpace(normCandArtist))
            {
                if (string.Equals(normCandArtist, normTargetAuthor, StringComparison.Ordinal))
                {
                    score += 60;
                    artistMatched = true;
                }
                else if (normCandArtist.Contains(normTargetAuthor, StringComparison.Ordinal) ||
                         normTargetAuthor.Contains(normCandArtist, StringComparison.Ordinal))
                {
                    score += 45;
                    artistMatched = true;
                }
            }

            if (requireArtistMatch && !artistMatched)
            {
                continue;
            }

            foreach (string keyword in variantKeywords)
            {
                bool targetHas = normTargetTitle.Contains(keyword, StringComparison.Ordinal);
                bool candHas = normCandTitle.Contains(keyword, StringComparison.Ordinal);
                if (targetHas != candHas)
                {
                    score -= 80;
                }
            }

            score += Math.Max(0, 15 - i);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= 60 ? best : null;
    }

    private static string CleanAuthorForAtvSearch(string rawAuthor)
    {
        if (string.IsNullOrWhiteSpace(rawAuthor))
        {
            return string.Empty;
        }

        string cleaned = rawAuthor.Trim();
        if (cleaned.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^" - Topic".Length].Trim();
        }

        if (cleaned.Length > 4 && cleaned.EndsWith("VEVO", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^4].Trim();
        }

        return cleaned;
    }

    private static string CleanTitleForAtvSearch(string rawTitle, string cleanAuthor)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string cleaned = VideoTitleTagRegex().Replace(rawTitle, string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(cleanAuthor))
        {
            string normAuthor = NormalizeMatchText(cleanAuthor);
            int dashIdx = cleaned.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx <= 0)
            {
                dashIdx = cleaned.IndexOf(" – ", StringComparison.Ordinal);
            }

            if (dashIdx > 0 && !string.IsNullOrWhiteSpace(normAuthor))
            {
                string leftNorm = NormalizeMatchText(cleaned[..dashIdx]);
                if (!string.IsNullOrWhiteSpace(leftNorm) &&
                    (leftNorm.Equals(normAuthor, StringComparison.Ordinal) ||
                     leftNorm.Contains(normAuthor, StringComparison.Ordinal) ||
                     normAuthor.Contains(leftNorm, StringComparison.Ordinal)))
                {
                    cleaned = cleaned[(dashIdx + 3)..].Trim();
                }
            }
        }

        return cleaned.Trim(' ', '-', '–');
    }

    private static string NormalizeMatchText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string lower = input.ToLowerInvariant().Normalize(NormalizationForm.FormKC);
        string cleaned = NonAlphanumericSpaceRegex().Replace(lower, " ");
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Neutralizes the ISO-BMFF <c>edts</c> (Edit List) atom inside YouTube DASH fragmented MP4 (<c>ftypdash</c>) headers
    /// by converting its 4-byte box type to <c>free</c>. Without this, Windows Media Foundation's native MP4 splitter
    /// misinterprets the empty base <c>stts</c> table + <c>elst</c> duration as a full-track skip and immediately jumps to <c>MediaEnded</c>.
    /// </summary>
    private static void NeutralizeDashMp4EditList(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            int inspectLen = (int)Math.Min(8192, fs.Length);
            if (inspectLen < 64)
            {
                return;
            }

            byte[] header = new byte[inspectLen];
            int bytesRead = fs.Read(header, 0, inspectLen);
            bool modified = false;

            for (int i = 4; i <= bytesRead - 8; i++)
            {
                if (header[i] == (byte)'e' &&
                    header[i + 1] == (byte)'d' &&
                    header[i + 2] == (byte)'t' &&
                    header[i + 3] == (byte)'s')
                {
                    header[i] = (byte)'f';
                    header[i + 1] = (byte)'r';
                    header[i + 2] = (byte)'e';
                    header[i + 3] = (byte)'e';
                    modified = true;
                }
            }

            if (modified)
            {
                fs.Position = 0;
                fs.Write(header, 0, bytesRead);
                fs.Flush();
            }
        }
        catch
        {
            // Ignore if file is concurrently read-only
        }
    }

    private async Task<List<TrackItem>> ExecuteSongsSearchAsync(
        string query,
        bool useSongsFilter,
        CancellationToken cancellationToken)
    {
        var bodyDict = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["query"] = query
        };

        if (useSongsFilter)
        {
            bodyDict["params"] = YtMusicSongsFilterParams;
        }

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicSearchEndpoint,
            bodyDict,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var tracks = new List<TrackItem>();
        var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);

        ExtractResponsiveTrackItemsRecursive(doc.RootElement, tracks, seenVideoIds);
        return tracks;
    }

    private static void ExtractResponsiveTrackItemsRecursive(
        JsonElement element,
        List<TrackItem> results,
        HashSet<string> seenVideoIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                var track = TryParseResponsiveListItem(renderer);
                if (track is not null && seenVideoIds.Add(track.YouTubeVideoId))
                {
                    results.Add(track);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractResponsiveTrackItemsRecursive(prop.Value, results, seenVideoIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractResponsiveTrackItemsRecursive(item, results, seenVideoIds);
            }
        }
    }

    private static TrackItem? TryParseResponsiveListItem(JsonElement renderer)
    {
        // 1. Extract canonical ATV videoId (preferring MPTC track-credits ID over OMV music video ID)
        string? atvFromCredits = ExtractTrackCreditsAtvVideoId(renderer);
        string? videoId = !string.IsNullOrWhiteSpace(atvFromCredits)
            ? atvFromCredits
            : ExtractPreferredAudioVideoId(renderer);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        // Discard pure music video / UGC items that have no MPTC studio audio counterpart
        if (string.IsNullOrWhiteSpace(atvFromCredits) &&
            IsVideoMusicVideoType(ExtractMusicVideoType(renderer)))
        {
            return null;
        }

        // 2. Extract flexColumns (Column 0 = Title, Column 1 = Artist • Album • Duration)
        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() < 2)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = "Unknown Artist";
        string album = "Single";
        TimeSpan duration = TimeSpan.FromMinutes(3.5);
        int releaseYear = 0;

        ParseMetadataRuns(flexCols[1], ref artist, ref album, ref duration, ref releaseYear);

        // Also check fixedColumns for duration if present
        if (renderer.TryGetProperty("fixedColumns", out var fixedCols) &&
            fixedCols.ValueKind == JsonValueKind.Array &&
            fixedCols.GetArrayLength() > 0)
        {
            string fixedText = ExtractRunsText(fixedCols[0]).Trim();
            if (TryParseDurationString(fixedText, out var parsedFixedDuration))
            {
                duration = parsedFixedDuration;
            }
        }

        // 3. Extract high-resolution album cover URL
        string coverArtUri = ExtractHighResThumbnailUrl(renderer);
        if (string.IsNullOrWhiteSpace(coverArtUri))
        {
            coverArtUri = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_{videoId}",
            YouTubeVideoId = videoId,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverArtUri = coverArtUri,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(renderer),
            QualityBadge = "HQ AAC",
            Genre = "Streaming",
            ReleaseYear = releaseYear,
            Lyrics =
            [
                new LyricLine(TimeSpan.Zero, $"♪ {title} — {artist}", IsSynchronized: false),
                new LyricLine(TimeSpan.FromSeconds(4), $"Album • {album}", IsSynchronized: false)
            ]
        };
    }

    internal static bool HasExplicitBadge(JsonElement renderer)
    {
        if (renderer.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return ContainsExplicitBadgeInArray(renderer, "badges") ||
               ContainsExplicitBadgeInArray(renderer, "subtitleBadges") ||
               ContainsExplicitBadgeInArray(renderer, "thumbnailBadges") ||
               ContainsExplicitBadgeInArray(renderer, "titleBadges");
    }

    private static bool ContainsExplicitBadgeInArray(JsonElement renderer, string propertyName)
    {
        if (!renderer.TryGetProperty(propertyName, out var badges) ||
            badges.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (badge.TryGetProperty("musicInlineBadgeRenderer", out var inlineBadge))
            {
                if (IsExplicitBadgeObject(inlineBadge))
                {
                    return true;
                }
            }

            if (badge.TryGetProperty("metadataBadgeRenderer", out var metaBadge))
            {
                if (IsExplicitBadgeObject(metaBadge))
                {
                    return true;
                }
            }

            if (IsExplicitIconOrStyle(badge))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitBadgeObject(JsonElement badgeObj)
    {
        if (badgeObj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (IsExplicitIconOrStyle(badgeObj))
        {
            return true;
        }

        if (badgeObj.TryGetProperty("accessibilityData", out var outerA11y) &&
            outerA11y.TryGetProperty("accessibilityData", out var innerA11y) &&
            innerA11y.TryGetProperty("label", out var labelProp))
        {
            string label = labelProp.GetString()?.Trim() ?? string.Empty;
            if (string.Equals(label, "Explicit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Explícito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Explicito", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(label, "Contenido explícito", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitIconOrStyle(JsonElement obj)
    {
        if (obj.TryGetProperty("icon", out var iconObj) &&
            iconObj.TryGetProperty("iconType", out var iconTypeProp) &&
            string.Equals(iconTypeProp.GetString(), "MUSIC_EXPLICIT_BADGE", StringComparison.Ordinal))
        {
            return true;
        }

        if (obj.TryGetProperty("style", out var styleProp) &&
            string.Equals(styleProp.GetString(), "BADGE_STYLE_TYPE_EXPLICIT", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static async Task<JsonDocument?> ParseInnerTubeJsonStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            // Most InnerTube JSON responses start with '{', while some begin with Google's ")]}'" XSSI prefix.
            // Buffer into a compact UTF-8 MemoryStream only if we need to strip the 4-5 byte XSSI prefix,
            // avoiding multi-MB UTF-16 string allocations on the Large Object Heap.
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, 16384, cancellationToken).ConfigureAwait(false);
            if (ms.Length == 0)
            {
                return null;
            }

            if (!ms.TryGetBuffer(out ArraySegment<byte> seg) || seg.Array is null)
            {
                seg = new ArraySegment<byte>(ms.ToArray());
            }

            int offset = seg.Offset;
            int count = seg.Count;
            if (count >= 4 &&
                seg.Array[offset] == (byte)')' &&
                seg.Array[offset + 1] == (byte)']' &&
                seg.Array[offset + 2] == (byte)'\'' &&
                seg.Array[offset + 3] == (byte)'}')
            {
                offset += 4;
                count -= 4;
                while (count > 0 && (seg.Array[offset] == (byte)'\r' || seg.Array[offset] == (byte)'\n'))
                {
                    offset++;
                    count--;
                }
            }

            if (count <= 0)
            {
                return null;
            }

            using var trimmedStream = new MemoryStream(seg.Array, offset, count, writable: false);
            return await JsonDocument.ParseAsync(trimmedStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static JsonDocument? ParseInnerTubeJson(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        string cleaned = rawBody;
        if (cleaned.StartsWith(")]}'", StringComparison.Ordinal))
        {
            int newlineIdx = cleaned.IndexOf('\n');
            cleaned = newlineIdx >= 0 ? cleaned[(newlineIdx + 1)..] : cleaned[4..];
        }

        try
        {
            return JsonDocument.Parse(cleaned);
        }
        catch
        {
            return null;
        }
    }

    private static void ParseMetadataRuns(
        JsonElement flexColumn,
        ref string artist,
        ref string album,
        ref TimeSpan duration,
        ref int releaseYear)
    {
        if (!flexColumn.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var colRenderer) ||
            !colRenderer.TryGetProperty("text", out var textObj) ||
            !textObj.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var artistParts = new List<string>();
        var nonSeparatorRuns = new List<(string Text, string? BrowseId, string? PageType)>();

        foreach (var run in runs.EnumerateArray())
        {
            string text = run.TryGetProperty("text", out var tProp) ? tProp.GetString()?.Trim() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(text) || text is "•" or "·" or "-" or "," or "&")
            {
                continue;
            }

            if ( string.Equals(text, "Song", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(text, "Video", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(text, "Single", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(text, "EP", StringComparison.OrdinalIgnoreCase) ||
                 IsPlayCountLabel(text))
            {
                continue;
            }

            if (TryParseDurationString(text, out var parsedDuration))
            {
                duration = parsedDuration;
                continue;
            }

            if (text.Length == 4 && int.TryParse(text, out int yr) && yr is >= 1950 and <= 2035)
            {
                releaseYear = yr;
                continue;
            }

            string? browseId = FindFirstPropertyString(run, "browseId");
            string? pageType = FindFirstPropertyString(run, "pageType");

            if (string.Equals(pageType, "MUSIC_PAGE_TYPE_ALBUM", StringComparison.OrdinalIgnoreCase) ||
                (browseId is not null && browseId.StartsWith("MPREb_", StringComparison.Ordinal)))
            {
                album = text;
            }
            else if (string.Equals(pageType, "MUSIC_PAGE_TYPE_ARTIST", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(pageType, "MUSIC_PAGE_TYPE_USER_CHANNEL", StringComparison.OrdinalIgnoreCase) ||
                     (browseId is not null && browseId.StartsWith("UC", StringComparison.Ordinal)))
            {
                artistParts.Add(text);
            }
            else
            {
                nonSeparatorRuns.Add((text, browseId, pageType));
            }
        }

        if (artistParts.Count > 0)
        {
            artist = string.Join(", ", artistParts.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        else if (nonSeparatorRuns.Count > 0)
        {
            artist = nonSeparatorRuns[0].Text;
            if (nonSeparatorRuns.Count > 1 && string.Equals(album, "Single", StringComparison.Ordinal))
            {
                album = nonSeparatorRuns[1].Text;
            }
        }
    }

    internal static string NormalizeHighResThumbnailUrl(string? rawUrl, int targetSizePx = 544) =>
        NormalizeHighResImageUrl(rawUrl, targetSizePx, targetSizePx);

    private static string NormalizeHighResImageUrl(string? rawUrl, int targetWidthPx, int targetHeightPx)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        string trimmed = rawUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = $"https:{trimmed}";
        }

        // Split off any query string so signed i.ytimg.com (?sqp=...&rs=...) or query parameters are never corrupted
        int queryIdx = trimmed.IndexOf('?');
        string pathPart = queryIdx >= 0 ? trimmed[..queryIdx] : trimmed;
        string queryPart = queryIdx >= 0 ? trimmed[queryIdx..] : string.Empty;

        // Only rewrite Google image CDN resize tokens (=w...-h... or =s...) on the path portion, preserving trailing flags/signatures
        bool isGoogleImageHost = pathPart.Contains("googleusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
                                 pathPart.Contains("ggpht.com/", StringComparison.OrdinalIgnoreCase);

        if (isGoogleImageHost || pathPart.Contains("=w", StringComparison.Ordinal) || pathPart.Contains("=s", StringComparison.Ordinal))
        {
            pathPart = ThumbnailSizeRegex().Replace(pathPart, $"=w{targetWidthPx}-h{targetHeightPx}");
            if (targetWidthPx == targetHeightPx)
            {
                pathPart = SquareSizeSuffixRegex().Replace(pathPart, $"=s{targetWidthPx}");
            }

            // Ensure WIC-compatible JPEG when Google returns an explicit WebP format flag (-rw) on the image token segment
            int eqIdx = pathPart.LastIndexOf('=');
            if (isGoogleImageHost && eqIdx >= 0 && eqIdx < pathPart.Length - 1)
            {
                string prefix = pathPart[..eqIdx];
                string token = pathPart[eqIdx..];
                if (token.Contains("-rw", StringComparison.Ordinal))
                {
                    token = WebpFormatFlagRegex().Replace(token, "-rj");
                    pathPart = prefix + token;
                }
            }
        }

        return pathPart + queryPart;
    }

    private static string ExtractHighResThumbnailUrl(JsonElement renderer)
    {
        string? rawUrl = ExtractRawThumbnailUrlFromElement(renderer);
        return NormalizeHighResThumbnailUrl(rawUrl, targetSizePx: 544);
    }

    private static string? ExtractRawThumbnailUrlFromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 1. Direct thumbnails array inside element
        if (TryGetLastThumbnailUrlFromContainer(element, out string? directUrl))
        {
            return directUrl;
        }

        // 2. element.musicThumbnailRenderer or element.croppedSquareThumbnailRenderer
        if (TryGetThumbnailUrlFromRendererProperties(element, out string? directRendererUrl))
        {
            return directRendererUrl;
        }

        // 3. element.thumbnail (musicThumbnailRenderer, croppedSquareThumbnailRenderer, thumbnailRenderer, or direct thumbnails)
        if (element.TryGetProperty("thumbnail", out var thumbRoot) && thumbRoot.ValueKind == JsonValueKind.Object)
        {
            if (TryGetLastThumbnailUrlFromContainer(thumbRoot, out string? thumbDirect))
            {
                return thumbDirect;
            }

            if (TryGetThumbnailUrlFromRendererProperties(thumbRoot, out string? nestedRendererUrl))
            {
                return nestedRendererUrl;
            }

            if (thumbRoot.TryGetProperty("thumbnailRenderer", out var nestedTr) &&
                TryGetThumbnailUrlFromRendererProperties(nestedTr, out string? nestedTrUrl))
            {
                return nestedTrUrl;
            }
        }

        // 4. element.thumbnailRenderer (used by musicTwoRowItemRenderer)
        if (element.TryGetProperty("thumbnailRenderer", out var trRoot) && trRoot.ValueKind == JsonValueKind.Object)
        {
            if (TryGetThumbnailUrlFromRendererProperties(trRoot, out string? trUrl))
            {
                return trUrl;
            }

            if (TryGetLastThumbnailUrlFromContainer(trRoot, out string? trDirectUrl))
            {
                return trDirectUrl;
            }
        }

        // 5. element.background (used by MPREb_... album/playlist browse root)
        if (element.TryGetProperty("background", out var bgRoot) &&
            TryGetThumbnailUrlFromRendererProperties(bgRoot, out string? bgUrl))
        {
            return bgUrl;
        }

        // 6. element.foregroundThumbnail (used by visual headers)
        if (element.TryGetProperty("foregroundThumbnail", out var fgRoot) &&
            TryGetThumbnailUrlFromRendererProperties(fgRoot, out string? fgUrl))
        {
            return fgUrl;
        }

        // 7. Recursive fallback for nested header / microformat renderers
        return FindFirstMusicThumbnailUrlRecursive(element);
    }

    private static bool TryGetThumbnailUrlFromRendererProperties(JsonElement container, out string? url)
    {
        url = null;
        if (container.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (container.TryGetProperty("musicThumbnailRenderer", out var mtr) &&
            TryGetLastThumbnailUrlFromMusicThumbnailRenderer(mtr, out url))
        {
            return true;
        }

        if (container.TryGetProperty("croppedSquareThumbnailRenderer", out var cstr) &&
            TryGetLastThumbnailUrlFromMusicThumbnailRenderer(cstr, out url))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetLastThumbnailUrlFromMusicThumbnailRenderer(JsonElement mtr, out string? url)
    {
        url = null;
        if (mtr.ValueKind == JsonValueKind.Object &&
            mtr.TryGetProperty("thumbnail", out var innerThumb) &&
            TryGetLastThumbnailUrlFromContainer(innerThumb, out url))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetLastThumbnailUrlFromContainer(JsonElement container, out string? url)
    {
        url = null;
        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("thumbnails", out var thumbArray) &&
            thumbArray.ValueKind == JsonValueKind.Array &&
            thumbArray.GetArrayLength() > 0)
        {
            // Scan backwards to pick the highest-resolution non-blank URL (matching Android)
            string? bestUrl = null;
            int bestWidth = -1;
            for (int i = thumbArray.GetArrayLength() - 1; i >= 0; i--)
            {
                var entry = thumbArray[i];
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("url", out var urlProp))
                {
                    continue;
                }

                string? candidate = urlProp.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                int width = entry.TryGetProperty("width", out var wProp) && wProp.TryGetInt32(out int parsedW)
                    ? parsedW
                    : 0;

                if (bestUrl is null || width > bestWidth)
                {
                    bestUrl = candidate;
                    bestWidth = width;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestUrl))
            {
                url = bestUrl;
                return true;
            }
        }

        return false;
    }

    private static string? FindFirstMusicThumbnailUrlRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetThumbnailUrlFromRendererProperties(element, out string? url))
            {
                return url;
            }

            if (element.TryGetProperty("microformatDataRenderer", out var micro) &&
                micro.TryGetProperty("thumbnail", out var microThumb) &&
                TryGetLastThumbnailUrlFromContainer(microThumb, out string? microUrl))
            {
                return microUrl;
            }

            foreach (var prop in element.EnumerateObject())
            {
                string? childUrl = FindFirstMusicThumbnailUrlRecursive(prop.Value);
                if (!string.IsNullOrWhiteSpace(childUrl))
                {
                    return childUrl;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? itemUrl = FindFirstMusicThumbnailUrlRecursive(item);
                if (!string.IsNullOrWhiteSpace(itemUrl))
                {
                    return itemUrl;
                }
            }
        }

        return null;
    }

    private static string ExtractRunsText(JsonElement columnElement)
    {
        if (!columnElement.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var colRenderer) &&
            !columnElement.TryGetProperty("musicResponsiveListItemFixedColumnRenderer", out colRenderer))
        {
            return string.Empty;
        }

        if (!colRenderer.TryGetProperty("text", out var textObj) ||
            !textObj.TryGetProperty("runs", out var runs) ||
            runs.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var tProp))
            {
                sb.Append(tProp.GetString());
            }
        }

        return sb.ToString().Trim();
    }

    private static string? FindFirstPropertyString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            foreach (var child in element.EnumerateObject())
            {
                var found = FindFirstPropertyString(child.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindFirstPropertyString(item, propertyName);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? ExtractTrackCreditsAtvVideoId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("browseId", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? raw = prop.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var match = TrackCreditsBrowseIdRegex().Match(raw);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }

            foreach (var child in element.EnumerateObject())
            {
                string? found = ExtractTrackCreditsAtvVideoId(child.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = ExtractTrackCreditsAtvVideoId(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? ExtractPreferredAudioVideoId(JsonElement renderer)
    {
        // Unauthenticated YouTube Music responses often substitute OMV music video IDs in playlistItemData/watchEndpoint
        // while keeping the canonical ATV song videoId inside the Track Credits menu item ("MPTC" + videoId).
        string? atvFromCredits = ExtractTrackCreditsAtvVideoId(renderer);
        if (!string.IsNullOrWhiteSpace(atvFromCredits))
        {
            return atvFromCredits;
        }

        if (renderer.TryGetProperty("playlistItemData", out var pid) &&
            pid.TryGetProperty("videoId", out var vidProp))
        {
            string? pidVideoId = vidProp.GetString();
            if (!string.IsNullOrWhiteSpace(pidVideoId))
            {
                return pidVideoId;
            }
        }

        return FindFirstPropertyString(renderer, "videoId");
    }

    private static string ExtractMusicVideoType(JsonElement element) =>
        FindFirstPropertyString(element, "musicVideoType") ?? string.Empty;

    private static bool IsPureAudioMusicVideoType(string? musicVideoType) =>
        string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_ATV", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoMusicVideoType(string? musicVideoType) =>
        string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDurationString(string input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input) || !DurationRegex().IsMatch(input))
        {
            return false;
        }

        var parts = input.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int mins) &&
            int.TryParse(parts[1], out int secs))
        {
            duration = new TimeSpan(0, mins, secs);
            return true;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int hrs) &&
            int.TryParse(parts[1], out int m) &&
            int.TryParse(parts[2], out int s))
        {
            duration = new TimeSpan(hrs, m, s);
            return true;
        }

        return false;
    }

    private static List<AlbumItem> BuildAlbumsFromTracks(List<TrackItem> tracks)
    {
        return tracks
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Album) ? t.Title : t.Album, StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(g =>
            {
                var first = g.First();
                string albumTitle = string.IsNullOrWhiteSpace(first.Album) ? first.Title : first.Album;
                bool isSingle = string.IsNullOrWhiteSpace(first.Album) ||
                                string.Equals(first.Album, "Single", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(albumTitle, first.Title, StringComparison.OrdinalIgnoreCase);
                int groupedCount = g.Count();
                int trackCount = groupedCount > 1 ? groupedCount : (isSingle ? 1 : 0);

                return new AlbumItem
                {
                    Id = $"ytm_alb_{first.YouTubeVideoId}",
                    Title = albumTitle,
                    Artist = first.Artist,
                    ReleaseYear = first.ReleaseYear > 1900 ? first.ReleaseYear : 0,
                    TrackCount = trackCount,
                    CoverArtUri = first.CoverArtUri,
                    SourceType = MediaSourceType.Online
                };
            })
            .ToList();
    }

    private async Task<(List<ArtistItem> Artists, List<ArtistItem> Recommended)> BuildArtistsFromTracksAsync(
        List<TrackItem> tracks,
        CancellationToken cancellationToken)
    {
        var artistGroups = tracks
            .GroupBy(t => t.Artist.Split([',', '&'], 2, StringSplitOptions.TrimEntries)[0], StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && !g.Key.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
            .Take(14)
            .ToList();

        // Resolve official YouTube Music artist portraits & monthly audience subtitles in parallel (using cache first)
        using var gate = new SemaphoreSlim(6, 6);
        var avatarTasks = artistGroups
            .Select(async g =>
            {
                var first = g.First();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (officialAvatar, audienceSubtitle) = await GetArtistSummaryAsync(g.Key, cancellationToken).ConfigureAwait(false);
                    return new ArtistItem
                    {
                        Id = $"ytm_art_{first.YouTubeVideoId}",
                        Name = g.Key,
                        AvatarUri = !string.IsNullOrWhiteSpace(officialAvatar) ? officialAvatar! : first.CoverArtUri,
                        Genre = !string.IsNullOrWhiteSpace(audienceSubtitle) ? audienceSubtitle! : "YouTube Music",
                        MonthlyAudienceText = audienceSubtitle ?? string.Empty,
                        IsRecommended = false,
                        SourceType = MediaSourceType.Online
                    };
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToList();

        var artists = (await Task.WhenAll(avatarTasks).ConfigureAwait(false)).ToList();

        var recommended = artists
            .Skip(1)
            .Take(8)
            .Select(a => new ArtistItem
            {
                Id = $"ytm_rec_{a.Id}",
                Name = a.Name,
                AvatarUri = a.AvatarUri,
                Genre = !string.IsNullOrWhiteSpace(a.MonthlyAudienceText) ? a.MonthlyAudienceText : "Featured Artist",
                MonthlyAudienceText = a.MonthlyAudienceText,
                IsRecommended = true,
                SourceType = MediaSourceType.Online
            })
            .ToList();

        return (artists, recommended);
    }

    private async Task<(string VisitorData, string Cookies)?> GetVisitorDataAndCookiesAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(_cachedVisitorData) && now < _visitorDataExpiry)
        {
            return (_cachedVisitorData, _cachedVisitorCookies ?? YouTubeAuthHelper.DefaultSocsCookie);
        }

        await _visitorLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedVisitorData) && now < _visitorDataExpiry)
            {
                return (_cachedVisitorData, _cachedVisitorCookies ?? YouTubeAuthHelper.DefaultSocsCookie);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, SwJsDataEndpoint);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/20.10.38, (Linux; U; ANDROID 11), gzip");
            req.Headers.TryAddWithoutValidation("Origin", YouTubeAuthHelper.YouTubeWwwOrigin);
            req.Headers.TryAddWithoutValidation("Cookie", YouTubeAuthHelper.DefaultSocsCookie);

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var cookieList = new List<string> { YouTubeAuthHelper.DefaultSocsCookie };
            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (string header in setCookies)
                {
                    string cookieVal = header.Split(';', 2)[0].Trim();
                    if (!string.IsNullOrWhiteSpace(cookieVal))
                    {
                        cookieList.Add(cookieVal);
                    }
                }
            }

            string formattedCookies = YouTubeAuthHelper.MergeCookieStrings(cookieList.ToArray());
            string raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = VisitorDataRegex().Match(raw);
            if (match.Success)
            {
                _cachedVisitorData = match.Groups[1].Value;
                _cachedVisitorCookies = formattedCookies;
                _visitorDataExpiry = DateTimeOffset.UtcNow.AddHours(6);
                return (_cachedVisitorData, formattedCookies);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore visitorData fallback
        }
        finally
        {
            _visitorLock.Release();
        }

        return null;
    }

    private static string ExtractCleanVideoId(string videoIdOrUri)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrUri))
        {
            return string.Empty;
        }

        string raw = videoIdOrUri.Trim();
        if (raw.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["ytmusic://".Length..].Trim();
        }
        else if (raw.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw["ytm_".Length..].Trim();
        }

        if (raw.Contains("v=", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(raw, @"[?&]v=([a-zA-Z0-9_\-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        else if (raw.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(raw, @"youtu\.be/([a-zA-Z0-9_\-]{11})");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        var idMatch = Regex.Match(raw, @"^[a-zA-Z0-9_\-]{11}$");
        if (idMatch.Success)
        {
            return idMatch.Value;
        }

        return raw;
    }

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex ThumbnailSizeRegex();

    [GeneratedRegex(@"=s\d+(?=$|-)")]
    private static partial Regex SquareSizeSuffixRegex();

    [GeneratedRegex(@"-rw(?=$|-)")]
    private static partial Regex WebpFormatFlagRegex();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"MPREb_[a-zA-Z0-9_\-]+")]
    private static partial Regex AlbumBrowseIdRegex();

    [GeneratedRegex(@"\b(19\d{2}|20\d{2})\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)")]
    private static partial Regex PlayCountNumberRegex();

    [GeneratedRegex(@"""(Cg[a-zA-Z0-9_%\-]{10,})""")]
    private static partial Regex VisitorDataRegex();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex ExactFourDigitYearRegex();

    [GeneratedRegex(@"^MPTC([a-zA-Z0-9_\-]{11})$")]
    private static partial Regex TrackCreditsBrowseIdRegex();

    [GeneratedRegex(@"\s*[\(\[](?:official\s+(?:music\s+|lyric\s+|4k\s+|hd\s+)?video|music\s+video|video\s+oficial|video\s*clip|lyric\s+video|official\s+visuali[sz]er|visuali[sz]er|official\s+audio|audio\s+only|lyrics?|m/?v)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTitleTagRegex();

    [GeneratedRegex(@"[^a-z0-9\s]")]
    private static partial Regex NonAlphanumericSpaceRegex();

    [GeneratedRegex(@"[?&]clen=(\d+)")]
    private static partial Regex ContentLengthQueryRegex();

    /// <inheritdoc />
    public async Task ConnectAccountCookieAsync(
        string cookieHeader,
        string? accountLabel = null,
        string? visitorData = null,
        string? dataSyncId = null,
        CancellationToken cancellationToken = default)
    {
        _isSessionInvalidated = false;
        ApplyCookieState(cookieHeader, accountLabel, visitorData, dataSyncId);
        _streamCache.Clear();
        _searchCache.Clear();
        _richSearchCache.Clear();
        _moodGenreDetailCache.Clear();
        lock (_moodsCacheLock)
        {
            _moodsAndGenresCache = null;
        }

        _triedAccountMetaSync = false;

        if (YouTubeAuthHelper.HasYouTubeAccountSession(_cookieHeader))
        {
            await EnsureAccountSessionMetadataAsync(cancellationToken, forceRefresh: true).ConfigureAwait(false);
        }

        if (!_isSessionInvalidated)
        {
            PersistAccountSessionSafe();
        }

        AccountSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task DisconnectAccountAsync(CancellationToken cancellationToken = default)
    {
        _cookieHeader = string.Empty;
        _sapisid = string.Empty;
        _sessionVisitorData = string.Empty;
        _dataSyncId = null;
        _cachedVisitorData = null;
        _cachedVisitorCookies = null;
        _visitorDataExpiry = DateTimeOffset.MinValue;
        _triedAccountMetaSync = false;
        _isSessionInvalidated = false;
        _streamCache.Clear();
        _searchCache.Clear();
        _richSearchCache.Clear();
        _moodGenreDetailCache.Clear();
        lock (_moodsCacheLock)
        {
            _moodsAndGenresCache = null;
        }

        ConnectedAccountLabel = "Not signed in";
        _sessionStore.Delete();
        TryDeleteDirectorySafe(_legacyWebView2LoginDirectory);
        try
        {
            lock (_traceLock)
            {
                if (File.Exists(_streamTraceFilePath))
                {
                    File.Delete(_streamTraceFilePath);
                }
            }
        }
        catch
        {
            // Ignore trace log delete errors
        }

        AccountSessionChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void PersistAccountSessionSafe()
    {
        try
        {
            _sessionStore.Save(new YouTubeAccountSessionData(
                CookieHeader: _cookieHeader,
                AccountLabel: ConnectedAccountLabel,
                VisitorData: _sessionVisitorData,
                DataSyncId: _dataSyncId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist encrypted YouTube Music session file.");
        }
    }

    private static void TryDeleteDirectorySafe(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Ignore directory delete errors
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> GetAccountPersonalizedRecommendationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return [];
        }

        try
        {
            var browsePayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = "FEmusic_home"
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                browsePayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return [];
            }

            var tracks = new List<TrackItem>();
            var seenVideoIds = new HashSet<string>(StringComparer.Ordinal);

            ExtractResponsiveTrackItemsRecursive(doc.RootElement, tracks, seenVideoIds);
            ExtractTwoRowHomeItemsRecursive(doc.RootElement, tracks, seenVideoIds);

            return tracks.Take(16).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch personalized FEmusic_home feed from YouTube Music account.");
            return [];
        }
    }

    private void ApplyCookieState(
        string rawCookies,
        string? accountLabel,
        string? visitorData = null,
        string? dataSyncId = null)
    {
        if (string.IsNullOrWhiteSpace(rawCookies))
        {
            return;
        }

        _cookieHeader = YouTubeAuthHelper.MergeCookieStrings(rawCookies);
        _sapisid = YouTubeAuthHelper.ExtractSapisid(_cookieHeader) ?? string.Empty;

        string? cleanVisitor = YouTubeAuthHelper.DecodePercentEncoded(visitorData);
        if (!string.IsNullOrWhiteSpace(cleanVisitor))
        {
            _sessionVisitorData = cleanVisitor;
        }

        string? cleanDataSync = YouTubeAuthHelper.DecodePercentEncoded(dataSyncId);
        if (!string.IsNullOrWhiteSpace(cleanDataSync))
        {
            _dataSyncId = cleanDataSync;
        }

        if (YouTubeAuthHelper.HasYouTubeAccountSession(_cookieHeader))
        {
            ConnectedAccountLabel = string.IsNullOrWhiteSpace(accountLabel)
                ? "Connected Account"
                : accountLabel.Trim();
        }
        else
        {
            ConnectedAccountLabel = "Not signed in (SAPISID cookie missing)";
        }
    }

    private void ApplyAccountAuthHeaders(
        HttpRequestMessage request,
        string origin = YouTubeAuthHelper.YouTubeMusicOrigin,
        bool includePageId = true) =>
        ApplyAuthHeadersCore(
            request,
            origin,
            includePageId,
            fallbackVisitorData: _cachedVisitorData,
            ensureConsentCookie: false);

    private void ApplyPlayerAuthHeaders(
        HttpRequestMessage request,
        string origin,
        bool includePageId,
        string? fallbackVisitorData) =>
        ApplyAuthHeadersCore(
            request,
            origin,
            includePageId,
            fallbackVisitorData: fallbackVisitorData,
            ensureConsentCookie: true);

    private void ApplyAuthHeadersCore(
        HttpRequestMessage request,
        string origin,
        bool includePageId,
        string? fallbackVisitorData,
        bool ensureConsentCookie)
    {
        if (!IsAuthenticated)
        {
            return;
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string cookieHeader = ensureConsentCookie
            ? YouTubeAuthHelper.EnsureConsentCookie(_cookieHeader)
            : _cookieHeader;

        request.Headers.TryAddWithoutValidation("X-Youtube-Bootstrap-Logged-In", "true");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("X-Origin", origin);
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");

        string? visitor = !string.IsNullOrWhiteSpace(_sessionVisitorData) ? _sessionVisitorData : fallbackVisitorData;
        if (!string.IsNullOrWhiteSpace(visitor))
        {
            request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        }

        if (includePageId)
        {
            string? delegatedPageId = YouTubeAuthHelper.GetDelegatedPageId(_dataSyncId);
            if (!string.IsNullOrWhiteSpace(delegatedPageId))
            {
                request.Headers.TryAddWithoutValidation("X-Goog-PageId", delegatedPageId);
            }
        }

        string? auth = YouTubeAuthHelper.ComputeAuthorizationHeader(
            _cookieHeader,
            timestamp,
            origin,
            _dataSyncId);
        if (!string.IsNullOrWhiteSpace(auth))
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }
    }

    private async Task EnsureAccountSessionMetadataAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!YouTubeAuthHelper.HasYouTubeAccountSession(_cookieHeader))
        {
            return;
        }

        if (!forceRefresh && _triedAccountMetaSync)
        {
            return;
        }

        await _accountMetaLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _triedAccountMetaSync)
            {
                return;
            }

            _triedAccountMetaSync = true;
            var payload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["deviceTheme"] = "DEVICE_THEME_SELECTED",
                ["userInterfaceTheme"] = "USER_INTERFACE_THEME_DARK"
            };

            using var menuDoc = await ExecuteInnerTubePostDocAsync(
                AccountMenuEndpoint,
                payload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            bool updated = false;
            bool confirmedSignedIn = false;
            bool explicitlyLoggedOut = false;

            if (menuDoc is not null)
            {
                string? accountName = ExtractAccountNameFromMenu(menuDoc.RootElement);
                bool hasSignedOutMarker = ContainsUnauthenticatedAccountMenuMarker(menuDoc.RootElement);

                string? dataSync = FindFirstPropertyString(menuDoc.RootElement, "dataSyncId")
                    ?? FindFirstPropertyString(menuDoc.RootElement, "datasyncId");
                if (!string.IsNullOrWhiteSpace(dataSync))
                {
                    _dataSyncId = YouTubeAuthHelper.DecodePercentEncoded(dataSync) ?? _dataSyncId;
                    updated = true;
                }

                if (!string.IsNullOrWhiteSpace(accountName) && !hasSignedOutMarker)
                {
                    confirmedSignedIn = true;
                    _isSessionInvalidated = false;
                    if (string.IsNullOrWhiteSpace(ConnectedAccountLabel) ||
                        ConnectedAccountLabel.Contains("Connected", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ConnectedAccountLabel, "My YouTube Music", StringComparison.OrdinalIgnoreCase) ||
                        ConnectedAccountLabel.Contains("expired", StringComparison.OrdinalIgnoreCase))
                    {
                        ConnectedAccountLabel = accountName;
                    }

                    updated = true;
                }
                else if (hasSignedOutMarker)
                {
                    explicitlyLoggedOut = true;
                }
            }

            if (!confirmedSignedIn && (string.IsNullOrWhiteSpace(_dataSyncId) || explicitlyLoggedOut))
            {
                using var switcherReq = new HttpRequestMessage(HttpMethod.Get, AccountSwitcherEndpoint);
                switcherReq.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
                switcherReq.Headers.TryAddWithoutValidation("Accept", "*/*");
                switcherReq.Headers.TryAddWithoutValidation("Origin", YouTubeAuthHelper.YouTubeMusicOrigin);
                switcherReq.Headers.TryAddWithoutValidation("Referer", $"{YouTubeAuthHelper.YouTubeMusicOrigin}/");
                switcherReq.Headers.TryAddWithoutValidation("X-Goog-Api-Format-Version", "1");
                switcherReq.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", WebRemixClientId);
                switcherReq.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", WebRemixClientVersion);
                ApplyAccountAuthHeaders(switcherReq, includePageId: false);

                using var switcherResp = await _httpClient.SendAsync(switcherReq, cancellationToken).ConfigureAwait(false);
                int switcherCode = (int)switcherResp.StatusCode;
                if (switcherResp.IsSuccessStatusCode)
                {
                    string raw = await switcherResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var switcherDoc = ParseInnerTubeJson(raw);
                    if (switcherDoc is not null)
                    {
                        string? codeVal = switcherDoc.RootElement.TryGetProperty("code", out var cProp) ? cProp.GetString() : null;
                        bool isSuccessCode = string.Equals(codeVal, "SUCCESS", StringComparison.OrdinalIgnoreCase);
                        string? accountName = ExtractAccountNameFromMenu(switcherDoc.RootElement);
                        string? dataSync = FindFirstPropertyString(switcherDoc.RootElement, "dataSyncId")
                            ?? FindFirstPropertyString(switcherDoc.RootElement, "datasyncId");

                        if (!string.IsNullOrWhiteSpace(dataSync))
                        {
                            _dataSyncId = YouTubeAuthHelper.DecodePercentEncoded(dataSync) ?? _dataSyncId;
                            updated = true;
                        }

                        if (isSuccessCode || !string.IsNullOrWhiteSpace(accountName))
                        {
                            confirmedSignedIn = true;
                            explicitlyLoggedOut = false;
                            _isSessionInvalidated = false;
                            if (!string.IsNullOrWhiteSpace(accountName) &&
                                (string.IsNullOrWhiteSpace(ConnectedAccountLabel) ||
                                 ConnectedAccountLabel.Contains("Connected", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ConnectedAccountLabel, "My YouTube Music", StringComparison.OrdinalIgnoreCase) ||
                                 ConnectedAccountLabel.Contains("expired", StringComparison.OrdinalIgnoreCase)))
                            {
                                ConnectedAccountLabel = accountName;
                            }

                            updated = true;
                        }
                    }
                }
                else if (switcherCode is 400 or 401 or 403)
                {
                    explicitlyLoggedOut = true;
                }
            }

            if (!confirmedSignedIn && explicitlyLoggedOut)
            {
                _isSessionInvalidated = true;
                ConnectedAccountLabel = "Session expired — Sign in again";
                TraceStream("Account session validation failed: YouTube reported logged_in=0 / expired cookies.");
                AccountSessionChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (updated)
            {
                PersistAccountSessionSafe();
                if (confirmedSignedIn)
                {
                    AccountSessionChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed refreshing YouTube Music account metadata.");
        }
        finally
        {
            _accountMetaLock.Release();
        }
    }

    private static bool ContainsUnauthenticatedAccountMenuMarker(JsonElement root)
    {
        if (TryFindObjectByPropertyName(root, "signInEndpoint", out _))
        {
            return true;
        }

        string raw = root.GetRawText();
        return raw.Contains("\"key\":\"logged_in\",\"value\":\"0\"", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("\"key\":\"yt_li\",\"value\":\"0\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractAccountNameFromMenu(JsonElement root)
    {
        if (TryFindObjectByPropertyName(root, "activeAccountHeaderRenderer", out var activeHdr))
        {
            if (activeHdr.TryGetProperty("accountName", out var accName))
            {
                string name = ExtractAllRunsText(accName).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            if (activeHdr.TryGetProperty("name", out var nameObj))
            {
                string name = ExtractAllRunsText(nameObj).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        if (TryFindObjectByPropertyName(root, "accountItem", out var accItem) &&
            accItem.TryGetProperty("accountName", out var itemAccName))
        {
            string name = ExtractAllRunsText(itemAccName).Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    private static bool TryFindObjectByPropertyName(JsonElement element, string targetProperty, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(targetProperty, out var prop) && prop.ValueKind == JsonValueKind.Object)
            {
                found = prop;
                return true;
            }

            foreach (var p in element.EnumerateObject())
            {
                if (TryFindObjectByPropertyName(p.Value, targetProperty, out found))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindObjectByPropertyName(item, targetProperty, out found))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private static bool IsTwoRowVideoOnlyItem(
        JsonElement twoRow,
        string subtitle,
        string shelfTitle,
        string? creditsAtvId)
    {
        if (!string.IsNullOrWhiteSpace(creditsAtvId))
        {
            return false;
        }

        if (IsVideoMusicVideoType(ExtractMusicVideoType(twoRow)))
        {
            return true;
        }

        if (twoRow.TryGetProperty("aspectRatio", out var ratio) &&
            (ratio.GetString()?.Contains("RECTANGLE", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        return subtitle.Contains("views", StringComparison.OrdinalIgnoreCase) ||
               subtitle.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(shelfTitle) && shelfTitle.Contains("video", StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractTwoRowHomeItemsRecursive(
        JsonElement element,
        List<TrackItem> results,
        HashSet<string> seenVideoIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
            {
                string? creditsAtvId = ExtractTrackCreditsAtvVideoId(twoRow);
                string? videoId = !string.IsNullOrWhiteSpace(creditsAtvId)
                    ? creditsAtvId
                    : FindFirstPropertyString(twoRow, "videoId");
                string subtitle = twoRow.TryGetProperty("subtitle", out var rawSubObj)
                    ? ExtractAllRunsText(rawSubObj).Trim()
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(videoId) &&
                    !IsTwoRowVideoOnlyItem(twoRow, subtitle, string.Empty, creditsAtvId) &&
                    seenVideoIds.Add(videoId))
                {
                    string title = string.Empty;
                    if (twoRow.TryGetProperty("title", out var titleObj) &&
                        titleObj.TryGetProperty("runs", out var tRuns) &&
                        tRuns.ValueKind == JsonValueKind.Array &&
                        tRuns.GetArrayLength() > 0 &&
                        tRuns[0].TryGetProperty("text", out var tText))
                    {
                        title = tText.GetString()?.Trim() ?? string.Empty;
                    }

                    string artist = "Featured Artist";
                    if (twoRow.TryGetProperty("subtitle", out var subObj) &&
                        subObj.TryGetProperty("runs", out var sRuns) &&
                        sRuns.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in sRuns.EnumerateArray())
                        {
                            string txt = r.TryGetProperty("text", out var rp) ? rp.GetString()?.Trim() ?? string.Empty : string.Empty;
                            if (!string.IsNullOrWhiteSpace(txt) &&
                                txt is not "•" and not "Song" and not "Video" &&
                                !IsPlayCountLabel(txt))
                            {
                                artist = txt;
                                break;
                            }
                        }
                    }

                    string cover = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
                        ? ExtractHighResThumbnailUrl(tr)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(cover))
                    {
                        cover = ExtractHighResThumbnailUrl(twoRow);
                    }

                    if (string.IsNullOrWhiteSpace(cover) && !string.IsNullOrWhiteSpace(videoId))
                    {
                        cover = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                    }

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        results.Add(new TrackItem
                        {
                            Id = $"ytm_{videoId}",
                            YouTubeVideoId = videoId,
                            Title = title,
                            Artist = artist,
                            Album = "Recommended Mix",
                            Duration = TimeSpan.FromMinutes(3.5),
                            CoverArtUri = cover,
                            AudioSourceUri = $"ytmusic://{videoId}",
                            SourceType = MediaSourceType.Online,
                            IsExplicit = HasExplicitBadge(twoRow),
                            QualityBadge = "HQ AAC",
                            Genre = "Personalized",
                            ReleaseYear = DateTime.Now.Year
                        });
                    }
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractTwoRowHomeItemsRecursive(prop.Value, results, seenVideoIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractTwoRowHomeItemsRecursive(item, results, seenVideoIds);
            }
        }
    }

    // =========================================================================
    // Rich Home Discovery (FEmusic_home, FEmusic_explore, FEmusic_new_releases_albums, /next)
    // =========================================================================

    private Dictionary<string, object?> BuildWebRemixContext(string hl = "en", string gl = "US", bool includeUser = true)
    {
        bool isSignedIn = IsAuthenticated;
        string? visitorData = !string.IsNullOrWhiteSpace(_sessionVisitorData) ? _sessionVisitorData : _cachedVisitorData;
        string? onBehalfOf = includeUser && isSignedIn ? YouTubeAuthHelper.GetOnBehalfOfUser(_dataSyncId) : null;

        var clientObj = new Dictionary<string, object?>
        {
            ["clientName"] = "WEB_REMIX",
            ["clientVersion"] = WebRemixClientVersion,
            ["hl"] = hl,
            ["gl"] = gl
        };
        if (!string.IsNullOrWhiteSpace(visitorData))
        {
            clientObj["visitorData"] = visitorData;
        }

        var ctx = new Dictionary<string, object?>
        {
            ["client"] = clientObj,
            ["request"] = new Dictionary<string, object?>
            {
                ["internalExperimentFlags"] = Array.Empty<object>(),
                ["useSsl"] = true
            }
        };

        if (includeUser && isSignedIn && !string.IsNullOrWhiteSpace(onBehalfOf))
        {
            ctx["user"] = new Dictionary<string, object?>
            {
                ["lockedSafetyMode"] = false,
                ["onBehalfOfUser"] = onBehalfOf
            };
        }

        return ctx;
    }

    private async Task<JsonDocument?> ExecuteInnerTubePostDocAsync(
        string endpointUrl,
        Dictionary<string, object?> payload,
        bool includeAuthHeaders,
        CancellationToken cancellationToken,
        bool includePageId = true)
    {
        try
        {
            bool canRetryWithoutDelegation = includeAuthHeaders &&
                                             includePageId &&
                                             IsAuthenticated &&
                                             YouTubeAuthHelper.GetDelegatedPageId(_dataSyncId) is not null;

            string jsonBody = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", YouTubeAuthHelper.YouTubeMusicOrigin);
            request.Headers.TryAddWithoutValidation("Referer", $"{YouTubeAuthHelper.YouTubeMusicOrigin}/");
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Format-Version", "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", WebRemixClientId);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", WebRemixClientVersion);
            if (includeAuthHeaders)
            {
                ApplyAccountAuthHeaders(request, YouTubeAuthHelper.YouTubeMusicOrigin, includePageId);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                if ((code == 400 || code == 403) && canRetryWithoutDelegation)
                {
                    if (payload.TryGetValue("context", out var ctxObj) &&
                        ctxObj is Dictionary<string, object?> ctxDict &&
                        ctxDict.ContainsKey("user"))
                    {
                        ctxDict.Remove("user");
                    }

                    return await ExecuteInnerTubePostDocAsync(
                        endpointUrl,
                        payload,
                        includeAuthHeaders,
                        cancellationToken,
                        includePageId: false).ConfigureAwait(false);
                }

                return null;
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ParseInnerTubeJsonStreamAsync(responseStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "InnerTube request failed for endpoint {Endpoint}.", endpointUrl);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<YtmHomeFeedResult> FetchPersonalizedHomeFeedAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsAuthenticated && !_triedAccountMetaSync)
        {
            await EnsureAccountSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        bool isPersonalized = IsAuthenticated;
        var initialPayload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["browseId"] = "FEmusic_home"
        };

        using var initialDoc = await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            initialPayload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (initialDoc is null)
        {
            return new YtmHomeFeedResult([], [], [], [], [], [], isPersonalized);
        }

        var pages = new List<YtmHomeFeedResult>
        {
            ParseHomeFeedResponse(initialDoc.RootElement, isPersonalized)
        };

        string? continuation = ExtractNextContinuationToken(initialDoc.RootElement);
        int depth = 0;
        while (!string.IsNullOrWhiteSpace(continuation) && depth < 2)
        {
            var contPayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["continuation"] = continuation
            };

            using var contDoc = await ExecuteInnerTubePostDocAsync(
                YtMusicBrowseEndpoint,
                contPayload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);

            if (contDoc is null)
            {
                break;
            }

            pages.Add(ParseHomeFeedResponse(contDoc.RootElement, isPersonalized));
            continuation = ExtractNextContinuationToken(contDoc.RootElement);
            depth++;
        }

        return MergeHomeFeedResults(pages, isPersonalized);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<NewReleaseItem> NewReleases, IReadOnlyList<RadioStationItem> Radios)> FetchExploreAndNewReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        var releasesTask = ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = "FEmusic_new_releases_albums"
            },
            includeAuthHeaders: true,
            cancellationToken);

        var exploreTask = ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["browseId"] = "FEmusic_explore"
            },
            includeAuthHeaders: true,
            cancellationToken);

        await Task.WhenAll(releasesTask, exploreTask).ConfigureAwait(false);

        using var releasesDoc = await releasesTask.ConfigureAwait(false);
        using var exploreDoc = await exploreTask.ConfigureAwait(false);

        var releaseAlbums = releasesDoc is not null
            ? ParseNewReleasesResponse(releasesDoc.RootElement)
            : [];

        var exploreReleases = exploreDoc is not null
            ? ParseNewReleasesResponse(exploreDoc.RootElement)
            : [];

        YtmHomeFeedResult? exploreFeed = exploreDoc is not null
            ? ParseHomeFeedResponse(exploreDoc.RootElement, IsAuthenticated)
            : null;

        var combinedReleases = new Dictionary<string, NewReleaseItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in releaseAlbums.Concat(exploreReleases).Concat(exploreFeed?.NewReleases ?? []))
        {
            string key = $"{item.Title.Trim()}::{item.Artist.Trim()}";
            combinedReleases.TryAdd(key, item);
        }

        var exploreRadios = exploreFeed?.RadioStations ?? [];
        return (combinedReleases.Values.ToList(), exploreRadios);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecommendedPlaylistItem>> SearchPlaylistsAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["query"] = query,
            ["params"] = YtMusicPlaylistsFilterParams
        };

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicSearchEndpoint,
            payload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (doc is null)
        {
            return [];
        }

        var results = new List<RecommendedPlaylistItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractPlaylistsRecursive(doc.RootElement, results, seenIds, limit);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlbumItem>> GetSimilarAlbumsForSeedAsync(
        string seedAlbumTitle,
        string seedArtist,
        string? seedBrowseId = null,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        var collected = new Dictionary<string, AlbumItem>(StringComparer.OrdinalIgnoreCase);
        string normalizedSeedTitle = seedAlbumTitle.Trim();

        void AddCandidates(IEnumerable<AlbumItem> candidates)
        {
            foreach (var album in candidates)
            {
                if (collected.Count >= limit)
                {
                    break;
                }

                string titleTrim = album.Title.Trim();
                if (string.IsNullOrWhiteSpace(titleTrim) ||
                    string.Equals(titleTrim, normalizedSeedTitle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(titleTrim, $"{normalizedSeedTitle} (Single)", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string key = $"{titleTrim}::{album.Artist.Trim()}";
                collected.TryAdd(key, album);
            }
        }

        // 1. Direct album browseId ("Other versions" & "Other releases / Fans might also like")
        if (!string.IsNullOrWhiteSpace(seedBrowseId) && !seedBrowseId.StartsWith("local_", StringComparison.OrdinalIgnoreCase))
        {
            var relatedFromAlbum = await FetchAlbumOtherReleasesAsync(seedBrowseId, seedArtist, cancellationToken).ConfigureAwait(false);
            AddCandidates(relatedFromAlbum);
        }

        // 2. Seed artist profile & up to 3 similar artists' albums (interleaved)
        if (collected.Count < limit &&
            !string.IsNullOrWhiteSpace(seedArtist) &&
            !string.Equals(seedArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            var artistProfile = await GetArtistProfileAsync(seedArtist, cancellationToken).ConfigureAwait(false);
            if (artistProfile is not null)
            {
                AddCandidates(artistProfile.CombinedAlbums.Concat(artistProfile.SinglesAndEps));

                var simArtistNames = artistProfile.SimilarArtists
                    .Take(3)
                    .Select(a => a.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToList();

                if (simArtistNames.Count > 0 && collected.Count < limit)
                {
                    var simTasks = simArtistNames.Select(async simName =>
                    {
                        var simProfile = await GetArtistProfileAsync(simName, cancellationToken).ConfigureAwait(false);
                        if (simProfile is not null && simProfile.CombinedAlbums.Count > 0)
                        {
                            return (IReadOnlyList<AlbumItem>)simProfile.CombinedAlbums;
                        }

                        return await ExecuteAlbumsSearchAsync(simName, strictlyAlbumsOnly: true, cancellationToken).ConfigureAwait(false);
                    }).ToList();

                    var simLists = await Task.WhenAll(simTasks).ConfigureAwait(false);
                    int maxRounds = simLists.Length > 0 ? simLists.Max(l => l.Count) : 0;
                    for (int round = 0; round < maxRounds && collected.Count < limit; round++)
                    {
                        foreach (var list in simLists)
                        {
                            if (round < list.Count)
                            {
                                AddCandidates([list[round]]);
                            }
                        }
                    }
                }
            }
        }

        // 3. Fallback strict album search
        if (collected.Count < limit)
        {
            var queries = new List<string>();
            if (!string.IsNullOrWhiteSpace(seedArtist) &&
                !string.Equals(seedArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
            {
                queries.Add(seedArtist);
            }

            if (!string.IsNullOrWhiteSpace(seedAlbumTitle))
            {
                queries.Add($"{seedAlbumTitle} {seedArtist} album".Trim());
            }

            foreach (string q in queries)
            {
                if (collected.Count >= limit)
                {
                    break;
                }

                var searchAlbums = await ExecuteAlbumsSearchAsync(q, strictlyAlbumsOnly: true, cancellationToken).ConfigureAwait(false);
                AddCandidates(searchAlbums);
            }
        }

        return collected.Values.Take(limit).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> ResolveRadioStationTracksAsync(
        RadioStationItem station,
        CancellationToken cancellationToken = default)
    {
        string cleanPlId = station.PlaylistId.StartsWith("VL", StringComparison.OrdinalIgnoreCase)
            ? station.PlaylistId[2..]
            : station.PlaylistId;

        string? seedVideoId = station.SeedVideoId;
        if (!string.IsNullOrWhiteSpace(seedVideoId))
        {
            string resolvedSeed = await ResolveCanonicalAtvVideoIdAsync(seedVideoId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(resolvedSeed, seedVideoId, StringComparison.Ordinal))
            {
                if (string.Equals(cleanPlId, $"RDAMVM{seedVideoId}", StringComparison.Ordinal))
                {
                    cleanPlId = $"RDAMVM{resolvedSeed}";
                }

                seedVideoId = resolvedSeed;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["isAudioOnly"] = true
        };

        if (!string.IsNullOrWhiteSpace(cleanPlId))
        {
            payload["playlistId"] = cleanPlId;
        }
        else if (!string.IsNullOrWhiteSpace(seedVideoId))
        {
            payload["playlistId"] = $"RDAMVM{seedVideoId}";
        }

        if (!string.IsNullOrWhiteSpace(seedVideoId))
        {
            payload["videoId"] = seedVideoId;
        }

        if (!string.IsNullOrWhiteSpace(station.Params))
        {
            payload["params"] = station.Params;
        }

        using (var doc = await ExecuteInnerTubePostDocAsync(YtMusicNextEndpoint, payload, includeAuthHeaders: true, cancellationToken).ConfigureAwait(false))
        {
            if (doc is not null)
            {
                var parsed = ParseNextPanelTracks(doc.RootElement);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }

        // Secondary /next attempt with RDAMVM seed if primary playlistId returned empty
        if (!string.IsNullOrWhiteSpace(seedVideoId) &&
            !string.Equals(cleanPlId, $"RDAMVM{seedVideoId}", StringComparison.Ordinal))
        {
            var fallbackPayload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["isAudioOnly"] = true,
                ["playlistId"] = $"RDAMVM{seedVideoId}",
                ["videoId"] = seedVideoId
            };

            using var fallbackDoc = await ExecuteInnerTubePostDocAsync(YtMusicNextEndpoint, fallbackPayload, includeAuthHeaders: true, cancellationToken).ConfigureAwait(false);
            if (fallbackDoc is not null)
            {
                var fallbackTracks = ParseNextPanelTracks(fallbackDoc.RootElement);
                if (fallbackTracks.Count > 0)
                {
                    return fallbackTracks;
                }
            }
        }

        var searchFallback = await SearchCatalogAsync(station.Title, cancellationToken).ConfigureAwait(false);
        return searchFallback.Songs;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackItem>> ResolvePlaylistTracksAsync(
        RecommendedPlaylistItem playlist,
        CancellationToken cancellationToken = default)
    {
        string rawId = !string.IsNullOrWhiteSpace(playlist.BrowseId) ? playlist.BrowseId : playlist.PlaylistId;
        if (string.IsNullOrWhiteSpace(rawId))
        {
            var searchFallback = await SearchCatalogAsync(playlist.Title, cancellationToken).ConfigureAwait(false);
            return searchFallback.Songs;
        }

        string browseId = rawId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ? rawId : $"VL{rawId}";
        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["browseId"] = browseId
        };

        using (var browseDoc = await ExecuteInnerTubePostDocAsync(YtMusicBrowseEndpoint, payload, includeAuthHeaders: true, cancellationToken).ConfigureAwait(false))
        {
            if (browseDoc is not null)
            {
                var browseTracks = ParsePlaylistBrowseTracks(browseDoc.RootElement);
                if (browseTracks.Count > 0)
                {
                    return browseTracks;
                }
            }
        }

        if (playlist.PlaylistId.StartsWith("RD", StringComparison.OrdinalIgnoreCase))
        {
            var radioTracks = await ResolveRadioStationTracksAsync(
                new RadioStationItem(playlist.Id, playlist.Title, playlist.Subtitle, playlist.ArtworkUri, playlist.PlaylistId),
                cancellationToken).ConfigureAwait(false);
            if (radioTracks.Count > 0)
            {
                return radioTracks;
            }
        }

        var finalFallback = await SearchCatalogAsync(playlist.Title, cancellationToken).ConfigureAwait(false);
        return finalFallback.Songs;
    }

    private async Task<List<AlbumItem>> FetchAlbumOtherReleasesAsync(
        string browseId,
        string artistName,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["browseId"] = browseId
        };

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            payload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (doc is null)
        {
            return [];
        }

        var otherVersions = new List<AlbumItem>();
        var otherAlbums = new List<AlbumItem>();
        ExtractAlbumOtherReleasesRecursive(doc.RootElement, artistName, otherVersions, otherAlbums);
        otherAlbums.AddRange(otherVersions);
        return otherAlbums;
    }

    private static void ExtractAlbumOtherReleasesRecursive(
        JsonElement element,
        string defaultArtist,
        List<AlbumItem> otherVersions,
        List<AlbumItem> otherAlbums)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicCarouselShelfRenderer", out var carousel))
            {
                string shelfTitle = string.Empty;
                if (carousel.TryGetProperty("header", out var header) &&
                    header.TryGetProperty("musicCarouselShelfBasicHeaderRenderer", out var basicHeader) &&
                    basicHeader.TryGetProperty("title", out var titleObj))
                {
                    shelfTitle = ExtractAllRunsText(titleObj).ToLowerInvariant();
                }

                bool isOtherVersions = shelfTitle.Contains("version") ||
                                       shelfTitle.Contains("versión") ||
                                       shelfTitle.Contains("edition");
                bool isOtherAlbums = !isOtherVersions &&
                                     (shelfTitle.Contains("more by") ||
                                      shelfTitle.Contains("más de") ||
                                      shelfTitle.Contains("release") ||
                                      shelfTitle.Contains("lanzamiento") ||
                                      shelfTitle.Contains("other") ||
                                      shelfTitle.Contains("album") ||
                                      shelfTitle.Contains("álbum"));

                var targetList = isOtherVersions ? otherVersions : (isOtherAlbums ? otherAlbums : null);
                if (targetList is not null &&
                    carousel.TryGetProperty("contents", out var contents) &&
                    contents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contents.EnumerateArray())
                    {
                        if (!item.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
                        {
                            continue;
                        }

                        string title = twoRow.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj).Trim() : string.Empty;
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            continue;
                        }

                        string subtitle = twoRow.TryGetProperty("subtitle", out var sObj) ? ExtractAllRunsText(sObj).Trim() : string.Empty;
                        string? browseId = twoRow.TryGetProperty("navigationEndpoint", out var navEp)
                            ? FindFirstPropertyString(navEp, "browseId")
                            : null;
                        if (string.IsNullOrWhiteSpace(browseId) || browseId.StartsWith("UC", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if ((twoRow.TryGetProperty("aspectRatio", out var ratio) &&
                             (ratio.GetString()?.Contains("RECTANGLE", StringComparison.OrdinalIgnoreCase) ?? false)) ||
                            subtitle.Contains("views", StringComparison.OrdinalIgnoreCase) ||
                            subtitle.Contains("reproducc", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string coverUri = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
                            ? ExtractHighResThumbnailUrl(tr)
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(coverUri))
                        {
                            coverUri = ExtractHighResThumbnailUrl(twoRow);
                        }
                        int year = ExtractYearFromSubtitle(subtitle);
                        string albArtist = ExtractArtistFromSubtitle(subtitle);
                        if (string.IsNullOrWhiteSpace(albArtist) ||
                            albArtist.Equals("Explicit", StringComparison.OrdinalIgnoreCase))
                        {
                            albArtist = string.IsNullOrWhiteSpace(defaultArtist) ? "Unknown Artist" : defaultArtist;
                        }

                        bool isSingleRelease = subtitle.Contains("Single", StringComparison.OrdinalIgnoreCase) ||
                                               subtitle.Contains("Sencillo", StringComparison.OrdinalIgnoreCase);

                        targetList.Add(new AlbumItem
                        {
                            Id = $"ytm_alb_{browseId}",
                            BrowseId = browseId,
                            Title = title,
                            Artist = albArtist,
                            Subtitle = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (year > 0 ? $"Album • {year}" : "Album"),
                            ReleaseYear = year > 1900 ? year : 0,
                            TrackCount = isSingleRelease ? 1 : ParseTrackCountFromText(subtitle),
                            CoverArtUri = coverUri,
                            SourceType = MediaSourceType.Online
                        });
                    }
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractAlbumOtherReleasesRecursive(prop.Value, defaultArtist, otherVersions, otherAlbums);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                ExtractAlbumOtherReleasesRecursive(child, defaultArtist, otherVersions, otherAlbums);
            }
        }
    }

    private async Task<List<AlbumItem>> ExecuteAlbumsSearchAsync(
        string query,
        bool strictlyAlbumsOnly,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["query"] = query,
            ["params"] = YtMusicAlbumsFilterParams
        };

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicSearchEndpoint,
            payload,
            includeAuthHeaders: false,
            cancellationToken).ConfigureAwait(false);

        if (doc is null)
        {
            return [];
        }

        var albums = new List<AlbumItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractResponsiveAlbumsRecursive(doc.RootElement, albums, seenIds, strictlyAlbumsOnly);
        return albums;
    }

    private static void ExtractResponsiveAlbumsRecursive(
        JsonElement element,
        List<AlbumItem> results,
        HashSet<string> seenIds,
        bool strictlyAlbumsOnly)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                var album = TryParseResponsiveAlbumItem(renderer, strictlyAlbumsOnly);
                if (album is not null && seenIds.Add(album.Id))
                {
                    results.Add(album);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractResponsiveAlbumsRecursive(prop.Value, results, seenIds, strictlyAlbumsOnly);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractResponsiveAlbumsRecursive(item, results, seenIds, strictlyAlbumsOnly);
            }
        }
    }

    private static AlbumItem? TryParseResponsiveAlbumItem(JsonElement renderer, bool strictlyAlbumsOnly)
    {
        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() == 0)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string type = "Album";
        string artist = "Unknown Artist";
        int year = 0;

        if (flexCols.GetArrayLength() > 1 &&
            flexCols[1].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col1) &&
            col1.TryGetProperty("text", out var textObj) &&
            textObj.TryGetProperty("runs", out var runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                string t = run.TryGetProperty("text", out var tp) ? tp.GetString()?.Trim() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(t) || t == "•")
                {
                    continue;
                }

                if (t.Equals("Album", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Álbum", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Sencillo", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("EP", StringComparison.OrdinalIgnoreCase))
                {
                    type = t;
                }
                else if (ExactFourDigitYearRegex().IsMatch(t) && int.TryParse(t, out int parsedYear))
                {
                    year = parsedYear;
                }
                else if (string.Equals(artist, "Unknown Artist", StringComparison.Ordinal))
                {
                    artist = t;
                }
            }
        }

        if (strictlyAlbumsOnly &&
            (type.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("Sencillo", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string? browseId = renderer.TryGetProperty("navigationEndpoint", out var navEp)
            ? FindFirstPropertyString(navEp, "browseId")
            : null;
        string coverUri = ExtractHighResThumbnailUrl(renderer);

        bool isSingleType = type.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
                            type.Equals("Sencillo", StringComparison.OrdinalIgnoreCase);

        return new AlbumItem
        {
            Id = !string.IsNullOrWhiteSpace(browseId) ? $"ytm_alb_{browseId}" : $"ytm_alb_{title.GetHashCode()}",
            BrowseId = browseId ?? string.Empty,
            Title = title,
            Artist = artist,
            Subtitle = year > 1900 ? $"{type} • {year}" : type,
            ReleaseYear = year > 1900 ? year : 0,
            TrackCount = isSingleType ? 1 : 0,
            CoverArtUri = coverUri,
            SourceType = MediaSourceType.Online
        };
    }

    private YtmHomeFeedResult ParseHomeFeedResponse(JsonElement root, bool isPersonalized)
    {
        var tracks = new List<TrackItem>();
        var seenTrackIds = new HashSet<string>(StringComparer.Ordinal);
        var albums = new List<AlbumItem>();
        var seenAlbumKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playlists = new List<RecommendedPlaylistItem>();
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var radios = new List<RadioStationItem>();
        var seenRadioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newReleases = new List<NewReleaseItem>();
        var seenReleaseKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in ExtractBrowseSections(root))
        {
            string shelfTitle = ExtractSectionTitle(section);
            if (!TryGetSectionItemsArray(section, out var itemsArray))
            {
                continue;
            }

            foreach (var entry in itemsArray.EnumerateArray())
            {
                if (entry.TryGetProperty("musicResponsiveListItemRenderer", out var responsive))
                {
                    var track = TryParseResponsiveListItem(responsive);
                    if (track is not null)
                    {
                        if (seenTrackIds.Add(track.Id))
                        {
                            tracks.Add(track);
                        }

                        if (IsNewReleaseShelf(shelfTitle))
                        {
                            string relKey = $"{track.Title.Trim()}::{track.Artist.Trim()}";
                            if (seenReleaseKeys.Add(relKey))
                            {
                                newReleases.Add(new NewReleaseItem(
                                    Id: $"nr_{track.Id}",
                                    Title: track.Title,
                                    Artist: track.Artist,
                                    Subtitle: $"Single • {track.Artist}",
                                    ArtworkUri: track.CoverArtUri,
                                    IsAlbum: false,
                                    VideoId: track.YouTubeVideoId));
                            }
                        }
                    }

                    var radio = ExtractMenuRadioFromResponsive(responsive, shelfTitle);
                    if (radio is not null && seenRadioIds.Add(radio.PlaylistId))
                    {
                        radios.Add(radio);
                    }
                }

                if (entry.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
                {
                    ClassifyTwoRowHomeItem(
                        twoRow,
                        shelfTitle,
                        tracks,
                        seenTrackIds,
                        albums,
                        seenAlbumKeys,
                        playlists,
                        seenPlaylistIds,
                        radios,
                        seenRadioIds,
                        newReleases,
                        seenReleaseKeys);
                }
            }
        }

        var topPickCandidates = BuildInitialTopPickCandidates(tracks, playlists, newReleases, radios, isPersonalized);
        return new YtmHomeFeedResult(tracks, albums, playlists, radios, newReleases, topPickCandidates, isPersonalized);
    }

    private static List<NewReleaseItem> ParseNewReleasesResponse(JsonElement root)
    {
        var results = new List<NewReleaseItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in ExtractBrowseSections(root))
        {
            string shelfTitle = ExtractSectionTitle(section);
            string lowerShelf = shelfTitle.ToLowerInvariant();
            bool isExcludedExploreShelf =
                lowerShelf.Contains("mood") ||
                lowerShelf.Contains("genre") ||
                lowerShelf.Contains("género") ||
                lowerShelf.Contains("estado") ||
                lowerShelf.Contains("podcast") ||
                lowerShelf.Contains("chart") ||
                lowerShelf.Contains("lista");

            if (isExcludedExploreShelf || !TryGetSectionItemsArray(section, out var itemsArray))
            {
                continue;
            }

            foreach (var entry in itemsArray.EnumerateArray())
            {
                if (entry.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
                {
                    var release = ParseTwoRowAsNewRelease(twoRow, shelfTitle);
                    if (release is not null && seenKeys.Add($"{release.Title.Trim()}::{release.Artist.Trim()}"))
                    {
                        results.Add(release);
                    }
                }

                if (entry.TryGetProperty("musicResponsiveListItemRenderer", out var responsive) &&
                    (IsNewReleaseShelf(shelfTitle) || lowerShelf.Contains("trending") || lowerShelf.Contains("tendenc")))
                {
                    var track = TryParseResponsiveListItem(responsive);
                    if (track is not null && seenKeys.Add($"{track.Title.Trim()}::{track.Artist.Trim()}"))
                    {
                        results.Add(new NewReleaseItem(
                            Id: $"nr_{track.Id}",
                            Title: track.Title,
                            Artist: track.Artist,
                            Subtitle: $"Single • {track.Artist}",
                            ArtworkUri: track.CoverArtUri,
                            IsAlbum: false,
                            VideoId: track.YouTubeVideoId));
                    }
                }
            }
        }

        return results;
    }

    private static NewReleaseItem? ParseTwoRowAsNewRelease(JsonElement twoRow, string shelfTitle)
    {
        string title = twoRow.TryGetProperty("title", out var titleObj) ? ExtractAllRunsText(titleObj).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string subtitle = twoRow.TryGetProperty("subtitle", out var subObj) ? ExtractAllRunsText(subObj).Trim() : string.Empty;
        string browseId = string.Empty;
        string pageType = string.Empty;
        string? creditsAtvId = ExtractTrackCreditsAtvVideoId(twoRow);
        string rawWatchVideoId = string.Empty;

        if (twoRow.TryGetProperty("navigationEndpoint", out var navEp))
        {
            browseId = navEp.TryGetProperty("browseEndpoint", out var be) ? FindFirstPropertyString(be, "browseId") ?? string.Empty : string.Empty;
            pageType = ExtractBrowsePageType(navEp);
            rawWatchVideoId = navEp.TryGetProperty("watchEndpoint", out var we) ? FindFirstPropertyString(we, "videoId") ?? string.Empty : string.Empty;
        }

        string videoId = !string.IsNullOrWhiteSpace(creditsAtvId) ? creditsAtvId : rawWatchVideoId;

        if (browseId.StartsWith("UC", StringComparison.Ordinal) ||
            pageType == "MUSIC_PAGE_TYPE_ARTIST" ||
            pageType == "MUSIC_PAGE_TYPE_PODCAST_SHOW")
        {
            return null;
        }

        if (pageType == "MUSIC_PAGE_TYPE_PLAYLIST" || browseId.StartsWith("VL", StringComparison.Ordinal))
        {
            return null;
        }

        bool isSingleSubtitle = subtitle.StartsWith("Single", StringComparison.OrdinalIgnoreCase) ||
                                subtitle.StartsWith("Sencillo", StringComparison.OrdinalIgnoreCase) ||
                                subtitle.Contains("• Single", StringComparison.OrdinalIgnoreCase) ||
                                subtitle.Contains("• Sencillo", StringComparison.OrdinalIgnoreCase);
        bool isAlbumLike = pageType == "MUSIC_PAGE_TYPE_ALBUM" ||
                           browseId.StartsWith("MPRE", StringComparison.Ordinal) ||
                           browseId.StartsWith("OLAK", StringComparison.Ordinal) ||
                           subtitle.StartsWith("Album", StringComparison.OrdinalIgnoreCase) ||
                           subtitle.StartsWith("EP", StringComparison.OrdinalIgnoreCase) ||
                           subtitle.StartsWith("Álbum", StringComparison.OrdinalIgnoreCase) ||
                           isSingleSubtitle;

        if (!isAlbumLike && string.IsNullOrWhiteSpace(videoId) && !IsNewReleaseShelf(shelfTitle))
        {
            return null;
        }

        if (!isAlbumLike && !string.IsNullOrWhiteSpace(videoId) && IsTwoRowVideoOnlyItem(twoRow, subtitle, shelfTitle, creditsAtvId))
        {
            return null;
        }

        string thumb = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
            ? ExtractHighResThumbnailUrl(tr)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(thumb))
        {
            thumb = ExtractHighResThumbnailUrl(twoRow);
        }

        if (string.IsNullOrWhiteSpace(thumb) && !string.IsNullOrWhiteSpace(videoId))
        {
            thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        string artist = ExtractArtistFromSubtitle(subtitle);
        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = "Featured Artist";
        }

        var yearMatch = YearRegex().Match(subtitle);
        string? yearStr = yearMatch.Success ? yearMatch.Value : null;
        string id = !string.IsNullOrWhiteSpace(browseId)
            ? $"nr_alb_{browseId}"
            : (!string.IsNullOrWhiteSpace(videoId) ? $"nr_vid_{videoId}" : $"nr_{title.GetHashCode()}");

        bool isAlbum = isAlbumLike && !isSingleSubtitle && string.IsNullOrWhiteSpace(videoId);

        return new NewReleaseItem(
            Id: id,
            Title: title,
            Artist: artist,
            Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (isAlbum ? $"Album • {artist}" : $"Single • {artist}"),
            ArtworkUri: thumb,
            IsAlbum: isAlbum,
            BrowseId: string.IsNullOrWhiteSpace(browseId) ? null : browseId,
            VideoId: string.IsNullOrWhiteSpace(videoId) ? null : videoId,
            Year: yearStr);
    }

    private static void ClassifyTwoRowHomeItem(
        JsonElement twoRow,
        string shelfTitle,
        List<TrackItem> tracks,
        HashSet<string> seenTrackIds,
        List<AlbumItem> albums,
        HashSet<string> seenAlbumKeys,
        List<RecommendedPlaylistItem> playlists,
        HashSet<string> seenPlaylistIds,
        List<RadioStationItem> radios,
        HashSet<string> seenRadioIds,
        List<NewReleaseItem> newReleases,
        HashSet<string> seenReleaseKeys)
    {
        string title = twoRow.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string subtitle = twoRow.TryGetProperty("subtitle", out var sObj) ? ExtractAllRunsText(sObj).Trim() : string.Empty;
        string? creditsAtvId = ExtractTrackCreditsAtvVideoId(twoRow);
        string browseId = string.Empty;
        string pageType = string.Empty;
        string rawWatchVideoId = string.Empty;
        string videoId = !string.IsNullOrWhiteSpace(creditsAtvId) ? creditsAtvId : string.Empty;
        string playlistId = string.Empty;
        string? epParams = null;

        if (twoRow.TryGetProperty("navigationEndpoint", out var navEp))
        {
            browseId = navEp.TryGetProperty("browseEndpoint", out var be) ? FindFirstPropertyString(be, "browseId") ?? string.Empty : string.Empty;
            pageType = ExtractBrowsePageType(navEp);
            if (navEp.TryGetProperty("watchEndpoint", out var we))
            {
                rawWatchVideoId = FindFirstPropertyString(we, "videoId") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    videoId = rawWatchVideoId;
                }

                playlistId = FindFirstPropertyString(we, "playlistId") ?? string.Empty;
                epParams = FindFirstPropertyString(we, "params");
            }

            if (string.IsNullOrWhiteSpace(playlistId) && navEp.TryGetProperty("watchPlaylistEndpoint", out var wpe))
            {
                playlistId = FindFirstPropertyString(wpe, "playlistId") ?? string.Empty;
                epParams ??= FindFirstPropertyString(wpe, "params");
            }

            if (string.IsNullOrWhiteSpace(playlistId) && browseId.StartsWith("VL", StringComparison.Ordinal))
            {
                playlistId = browseId[2..];
            }
        }

        if (!string.IsNullOrWhiteSpace(creditsAtvId) &&
            !string.IsNullOrWhiteSpace(rawWatchVideoId) &&
            !string.Equals(creditsAtvId, rawWatchVideoId, StringComparison.Ordinal) &&
            string.Equals(playlistId, $"RDAMVM{rawWatchVideoId}", StringComparison.OrdinalIgnoreCase))
        {
            playlistId = $"RDAMVM{creditsAtvId}";
        }

        string thumb = twoRow.TryGetProperty("thumbnailRenderer", out var tr)
            ? ExtractHighResThumbnailUrl(tr)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(thumb))
        {
            thumb = ExtractHighResThumbnailUrl(twoRow);
        }

        if (string.IsNullOrWhiteSpace(thumb) && !string.IsNullOrWhiteSpace(videoId))
        {
            thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        bool radioShelf = IsRadioShelf(shelfTitle);
        bool mixOrPlaylistShelf = IsPlaylistOrMixShelf(shelfTitle);
        bool newReleaseShelf = IsNewReleaseShelf(shelfTitle);

        // 1. Radio station check
        bool explicitPlaylistBrowse = browseId.StartsWith("VL", StringComparison.Ordinal) || pageType == "MUSIC_PAGE_TYPE_PLAYLIST";
        bool isRadioSeedPrefix = playlistId.StartsWith("RDEM", StringComparison.Ordinal) ||
                                 playlistId.StartsWith("RDAO", StringComparison.Ordinal) ||
                                 playlistId.StartsWith("RDAMPL", StringComparison.Ordinal) ||
                                 subtitle.Contains("radio", StringComparison.OrdinalIgnoreCase);

        bool isRadioItem = (radioShelf && (!string.IsNullOrWhiteSpace(playlistId) || !string.IsNullOrWhiteSpace(videoId))) ||
                           (!explicitPlaylistBrowse &&
                            playlistId.StartsWith("RD", StringComparison.Ordinal) &&
                            !playlistId.StartsWith("RDCLAK", StringComparison.Ordinal) &&
                            !playlistId.StartsWith("RDTMAK", StringComparison.Ordinal) &&
                            (string.IsNullOrWhiteSpace(videoId) || isRadioSeedPrefix)) ||
                           (explicitPlaylistBrowse && isRadioSeedPrefix);

        if (isRadioItem)
        {
            string resolvedPlId = !string.IsNullOrWhiteSpace(playlistId)
                ? playlistId
                : (!string.IsNullOrWhiteSpace(videoId) ? $"RDAMVM{videoId}" : string.Empty);

            if (!string.IsNullOrWhiteSpace(resolvedPlId) && seenRadioIds.Add(resolvedPlId))
            {
                radios.Add(new RadioStationItem(
                    Id: $"radio_{resolvedPlId}",
                    Title: title,
                    Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (!string.IsNullOrWhiteSpace(shelfTitle) ? shelfTitle : "Radio Mix"),
                    ArtworkUri: thumb,
                    PlaylistId: resolvedPlId,
                    SeedVideoId: string.IsNullOrWhiteSpace(videoId) ? null : videoId,
                    Params: string.IsNullOrWhiteSpace(epParams) ? null : epParams));
            }

            return;
        }

        // 2. Album check
        bool isAlbum = pageType == "MUSIC_PAGE_TYPE_ALBUM" ||
                       browseId.StartsWith("MPRE", StringComparison.Ordinal) ||
                       browseId.StartsWith("OLAK", StringComparison.Ordinal);

        if (isAlbum)
        {
            string artist = ExtractArtistFromSubtitle(subtitle);
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = "Featured Artist";
            }

            int year = ExtractYearFromSubtitle(subtitle);
            bool isSingleRelease = subtitle.StartsWith("Single", StringComparison.OrdinalIgnoreCase) ||
                                   subtitle.StartsWith("Sencillo", StringComparison.OrdinalIgnoreCase);
            string albumKey = $"{title.Trim()}::{artist.Trim()}";
            if (seenAlbumKeys.Add(albumKey))
            {
                albums.Add(new AlbumItem
                {
                    Id = $"ytm_alb_{(!string.IsNullOrWhiteSpace(browseId) ? browseId : title.GetHashCode().ToString())}",
                    BrowseId = browseId,
                    Title = title,
                    Artist = artist,
                    Subtitle = !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (year > 0 ? $"Album • {year}" : "Album"),
                    ReleaseYear = year > 1900 ? year : 0,
                    TrackCount = isSingleRelease ? 1 : ParseTrackCountFromText(subtitle),
                    CoverArtUri = thumb,
                    SourceType = MediaSourceType.Online
                });
            }

            if (newReleaseShelf && seenReleaseKeys.Add(albumKey))
            {
                var yrMatch = YearRegex().Match(subtitle);
                newReleases.Add(new NewReleaseItem(
                    Id: $"nr_alb_{(!string.IsNullOrWhiteSpace(browseId) ? browseId : title.GetHashCode().ToString())}",
                    Title: title,
                    Artist: artist,
                    Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (isSingleRelease ? $"Single • {artist}" : $"Album • {artist}"),
                    ArtworkUri: thumb,
                    IsAlbum: !isSingleRelease,
                    BrowseId: string.IsNullOrWhiteSpace(browseId) ? null : browseId,
                    Year: yrMatch.Success ? yrMatch.Value : null));
            }

            return;
        }

        // 3. Curated Playlist / Mixed for you check
        bool isPlaylist = pageType == "MUSIC_PAGE_TYPE_PLAYLIST" ||
                          browseId.StartsWith("VL", StringComparison.Ordinal) ||
                          playlistId.StartsWith("RDCLAK", StringComparison.Ordinal) ||
                          playlistId.StartsWith("RDTMAK", StringComparison.Ordinal) ||
                          playlistId.StartsWith("PL", StringComparison.Ordinal) ||
                          (mixOrPlaylistShelf && !string.IsNullOrWhiteSpace(playlistId));

        if (isPlaylist && !browseId.StartsWith("UC", StringComparison.Ordinal))
        {
            string cleanPlId = !string.IsNullOrWhiteSpace(playlistId)
                ? playlistId
                : (browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId);

            if (!string.IsNullOrWhiteSpace(cleanPlId) && seenPlaylistIds.Add(cleanPlId))
            {
                string resolvedBrowse = !string.IsNullOrWhiteSpace(browseId) ? browseId : $"VL{cleanPlId}";
                playlists.Add(new RecommendedPlaylistItem(
                    Id: $"pl_{cleanPlId}",
                    Title: title,
                    Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : (!string.IsNullOrWhiteSpace(shelfTitle) ? shelfTitle : "Made for you"),
                    ArtworkUri: thumb,
                    PlaylistId: cleanPlId,
                    BrowseId: resolvedBrowse,
                    Author: ExtractPlaylistAuthor(subtitle),
                    TrackCountText: ExtractTrackCount(subtitle)));
            }

            return;
        }

        // 4. Song (discard video-only OMV/UGC items without ATV credits ID)
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            if (IsTwoRowVideoOnlyItem(twoRow, subtitle, shelfTitle, creditsAtvId))
            {
                return;
            }

            string artist = ExtractArtistFromSubtitle(subtitle);
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = "Featured Artist";
            }

            string trackId = $"ytm_{videoId}";
            if (seenTrackIds.Add(trackId))
            {
                tracks.Add(new TrackItem
                {
                    Id = trackId,
                    YouTubeVideoId = videoId,
                    Title = title,
                    Artist = artist,
                    Album = "Single",
                    Duration = TimeSpan.FromMinutes(3.5),
                    CoverArtUri = thumb,
                    AudioSourceUri = $"ytmusic://{videoId}",
                    SourceType = MediaSourceType.Online,
                    IsExplicit = HasExplicitBadge(twoRow),
                    QualityBadge = "HQ AAC",
                    Genre = "Streaming",
                    ReleaseYear = DateTime.Now.Year
                });
            }

            if (newReleaseShelf || shelfTitle.Contains("trending", StringComparison.OrdinalIgnoreCase))
            {
                string relKey = $"{title.Trim()}::{artist.Trim()}";
                if (seenReleaseKeys.Add(relKey))
                {
                    var yrMatch = YearRegex().Match(subtitle);
                    newReleases.Add(new NewReleaseItem(
                        Id: $"nr_vid_{videoId}",
                        Title: title,
                        Artist: artist,
                        Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : $"Single • {artist}",
                        ArtworkUri: thumb,
                        IsAlbum: false,
                        VideoId: videoId,
                        Year: yrMatch.Success ? yrMatch.Value : null));
                }
            }
        }
    }

    private static RadioStationItem? ExtractMenuRadioFromResponsive(JsonElement renderer, string shelfTitle)
    {
        if (!renderer.TryGetProperty("navigationEndpoint", out var navEp) ||
            !navEp.TryGetProperty("watchEndpoint", out var watchEp))
        {
            return null;
        }

        string videoId = FindFirstPropertyString(watchEp, "videoId") ?? FindFirstPropertyString(renderer, "videoId") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string playlistId = FindFirstPropertyString(watchEp, "playlistId") ?? string.Empty;
        if (!playlistId.StartsWith("RD", StringComparison.Ordinal) && !IsRadioShelf(shelfTitle))
        {
            return null;
        }

        if (!renderer.TryGetProperty("flexColumns", out var flexCols) ||
            flexCols.ValueKind != JsonValueKind.Array ||
            flexCols.GetArrayLength() == 0)
        {
            return null;
        }

        string title = ExtractRunsText(flexCols[0]).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string artist = "Featured Artist";
        string album = "Single";
        TimeSpan dur = TimeSpan.FromMinutes(3.5);
        int yr = 0;
        if (flexCols.GetArrayLength() > 1)
        {
            ParseMetadataRuns(flexCols[1], ref artist, ref album, ref dur, ref yr);
        }

        string resolvedPlId = !string.IsNullOrWhiteSpace(playlistId) ? playlistId : $"RDAMVM{videoId}";
        string thumb = ExtractHighResThumbnailUrl(renderer);
        if (string.IsNullOrWhiteSpace(thumb))
        {
            thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new RadioStationItem(
            Id: $"radio_{resolvedPlId}",
            Title: IsRadioShelf(shelfTitle) ? title : $"{title} Radio",
            Subtitle: !string.IsNullOrWhiteSpace(artist) && !string.Equals(artist, "Unknown Artist", StringComparison.Ordinal) ? artist : "Radio Mix",
            ArtworkUri: thumb,
            PlaylistId: resolvedPlId,
            SeedVideoId: videoId);
    }

    private static List<TopPickItem> BuildInitialTopPickCandidates(
        List<TrackItem> tracks,
        List<RecommendedPlaylistItem> playlists,
        List<NewReleaseItem> newReleases,
        List<RadioStationItem> radios,
        bool isPersonalized)
    {
        var picks = new List<TopPickItem>();

        foreach (var track in tracks.Where(t => t.HasCoverArt).Concat(tracks.Where(t => !t.HasCoverArt)).Take(2))
        {
            picks.Add(new TopPickItem(
                Id: $"tp_track_{track.Id}",
                Eyebrow: isPersonalized ? "QUICK PICK FOR YOU" : "TRENDING PICK",
                Title: track.Title,
                Subtitle: string.IsNullOrWhiteSpace(track.Album) ? track.Artist : $"{track.Artist} • {track.Album}",
                ArtworkUri: track.CoverArtUri,
                CollageArtworks: [],
                Kind: TopPickKind.PersonalStation,
                PlaylistId: $"RDAMVM{track.YouTubeVideoId}",
                VideoId: track.YouTubeVideoId,
                ArtistName: track.Artist,
                SeedTrack: track,
                AmbientHexColor: "#FF182232"));
        }

        var leadRadio = radios.FirstOrDefault(r => r.HasArtwork) ?? radios.FirstOrDefault();
        if (leadRadio is not null)
        {
            picks.Add(new TopPickItem(
                Id: $"tp_radio_{leadRadio.PlaylistId}",
                Eyebrow: "PERSONAL STATION",
                Title: leadRadio.Title,
                Subtitle: leadRadio.Subtitle,
                ArtworkUri: leadRadio.ArtworkUri,
                CollageArtworks: [],
                Kind: TopPickKind.PersonalStation,
                PlaylistId: leadRadio.PlaylistId,
                VideoId: leadRadio.SeedVideoId,
                AmbientHexColor: "#FF1E1A2D"));
        }

        foreach (var pl in playlists.Where(p => p.HasArtwork).Concat(playlists.Where(p => !p.HasArtwork)).Take(2))
        {
            picks.Add(new TopPickItem(
                Id: $"tp_pl_{pl.PlaylistId}",
                Eyebrow: "MIXED FOR YOU",
                Title: pl.Title,
                Subtitle: pl.Subtitle,
                ArtworkUri: pl.ArtworkUri,
                CollageArtworks: [],
                Kind: TopPickKind.FeaturedPlaylist,
                BrowseId: pl.BrowseId,
                PlaylistId: pl.PlaylistId,
                AmbientHexColor: "#FF17272D"));
        }

        var leadRelease = newReleases.FirstOrDefault(nr => nr.HasArtwork) ?? newReleases.FirstOrDefault();
        if (leadRelease is not null)
        {
            string releaseSubtitle = !string.IsNullOrWhiteSpace(leadRelease.Subtitle) &&
                                     leadRelease.Subtitle.Contains(leadRelease.Artist, StringComparison.OrdinalIgnoreCase)
                ? leadRelease.Subtitle
                : $"{leadRelease.Artist} • {leadRelease.TypeBadgeText}";
            picks.Add(new TopPickItem(
                Id: $"tp_nr_{leadRelease.Id}",
                Eyebrow: "NEW RELEASE",
                Title: leadRelease.Title,
                Subtitle: releaseSubtitle,
                ArtworkUri: leadRelease.ArtworkUri,
                CollageArtworks: [],
                Kind: TopPickKind.NewRelease,
                BrowseId: leadRelease.BrowseId,
                VideoId: leadRelease.VideoId,
                ArtistName: leadRelease.Artist,
                AlbumTitle: (leadRelease.IsAlbum || !string.IsNullOrWhiteSpace(leadRelease.BrowseId)) ? leadRelease.Title : null,
                AmbientHexColor: "#FF2A1C1E"));
        }

        return picks;
    }

    private static YtmHomeFeedResult MergeHomeFeedResults(List<YtmHomeFeedResult> pages, bool isPersonalized)
    {
        var tracks = new Dictionary<string, TrackItem>(StringComparer.Ordinal);
        var albums = new Dictionary<string, AlbumItem>(StringComparer.OrdinalIgnoreCase);
        var playlists = new Dictionary<string, RecommendedPlaylistItem>(StringComparer.OrdinalIgnoreCase);
        var radios = new Dictionary<string, RadioStationItem>(StringComparer.OrdinalIgnoreCase);
        var releases = new Dictionary<string, NewReleaseItem>(StringComparer.OrdinalIgnoreCase);
        var picks = new Dictionary<string, TopPickItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            foreach (var t in page.PersonalizedTracks) tracks.TryAdd(t.Id, t);
            foreach (var a in page.PersonalizedAlbums) albums.TryAdd($"{a.Title.Trim()}::{a.Artist.Trim()}", a);
            foreach (var p in page.PlaylistsMadeForYou) playlists.TryAdd(p.PlaylistId, p);
            foreach (var r in page.RadioStations) radios.TryAdd(r.PlaylistId, r);
            foreach (var nr in page.NewReleases) releases.TryAdd($"{nr.Title.Trim()}::{nr.Artist.Trim()}", nr);
            foreach (var pk in page.FeaturedTopPicks) picks.TryAdd(pk.Id, pk);
        }

        return new YtmHomeFeedResult(
            tracks.Values.ToList(),
            albums.Values.ToList(),
            playlists.Values.ToList(),
            radios.Values.ToList(),
            releases.Values.ToList(),
            picks.Values.ToList(),
            isPersonalized);
    }

    private static void ExtractPlaylistsRecursive(
        JsonElement element,
        List<RecommendedPlaylistItem> results,
        HashSet<string> seenIds,
        int limit)
    {
        if (results.Count >= limit)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (renderer.TryGetProperty("flexColumns", out var flexCols) &&
                    flexCols.ValueKind == JsonValueKind.Array &&
                    flexCols.GetArrayLength() > 0)
                {
                    string title = ExtractRunsText(flexCols[0]).Trim();
                    string subtitle = flexCols.GetArrayLength() > 1 ? ExtractRunsText(flexCols[1]).Trim() : string.Empty;
                    string browseId = renderer.TryGetProperty("navigationEndpoint", out var navEp)
                        ? FindFirstPropertyString(navEp, "browseId") ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(browseId))
                    {
                        browseId = FindFirstPropertyString(flexCols[0], "browseId") ?? string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(browseId))
                    {
                        string? fallbackPlId = FindFirstPropertyString(renderer, "playlistId");
                        if (!string.IsNullOrWhiteSpace(fallbackPlId))
                        {
                            browseId = fallbackPlId.StartsWith("VL", StringComparison.Ordinal) ? fallbackPlId : $"VL{fallbackPlId}";
                        }
                    }

                    string cleanPlId = browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId[2..] : browseId;

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.IsNullOrWhiteSpace(cleanPlId) &&
                        seenIds.Add(cleanPlId))
                    {
                        string thumb = ExtractHighResThumbnailUrl(renderer);
                        results.Add(new RecommendedPlaylistItem(
                            Id: $"pl_{cleanPlId}",
                            Title: title,
                            Subtitle: !string.IsNullOrWhiteSpace(subtitle) ? subtitle : "Curated Playlist",
                            ArtworkUri: thumb,
                            PlaylistId: cleanPlId,
                            BrowseId: browseId.StartsWith("VL", StringComparison.Ordinal) ? browseId : $"VL{cleanPlId}",
                            Author: ExtractPlaylistAuthor(subtitle),
                            TrackCountText: ExtractTrackCount(subtitle)));
                    }
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (results.Count >= limit)
                {
                    break;
                }

                ExtractPlaylistsRecursive(prop.Value, results, seenIds, limit);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (results.Count >= limit)
                {
                    break;
                }

                ExtractPlaylistsRecursive(item, results, seenIds, limit);
            }
        }
    }

    private static List<TrackItem> ParseNextPanelTracks(JsonElement root)
    {
        var tracks = new List<TrackItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        ExtractNextPanelTracksRecursive(root, tracks, seenIds);
        return tracks;
    }

    private static void ExtractNextPanelTracksRecursive(
        JsonElement element,
        List<TrackItem> results,
        HashSet<string> seenIds)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("playlistPanelVideoRenderer", out var renderer))
            {
                var track = TryParsePlaylistPanelVideoRenderer(renderer);
                if (track is not null && seenIds.Add(track.Id))
                {
                    results.Add(track);
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                ExtractNextPanelTracksRecursive(prop.Value, results, seenIds);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractNextPanelTracksRecursive(item, results, seenIds);
            }
        }
    }

    private static TrackItem? TryParsePlaylistPanelVideoRenderer(JsonElement renderer)
    {
        string rawVideoId = renderer.TryGetProperty("videoId", out var vidProp) ? vidProp.GetString() ?? string.Empty : string.Empty;
        string? creditsAtvId = ExtractTrackCreditsAtvVideoId(renderer);
        string videoId = !string.IsNullOrWhiteSpace(creditsAtvId) ? creditsAtvId : rawVideoId;
        if (string.IsNullOrWhiteSpace(videoId) && renderer.TryGetProperty("navigationEndpoint", out var navEp))
        {
            videoId = FindFirstPropertyString(navEp, "videoId") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        string title = renderer.TryGetProperty("title", out var tObj) ? ExtractAllRunsText(tObj).Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var tokens = new List<string>();
        JsonElement bylineObj = default;
        bool hasByline = renderer.TryGetProperty("longBylineText", out bylineObj) ||
                         renderer.TryGetProperty("shortBylineText", out bylineObj);
        if (hasByline && bylineObj.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in runs.EnumerateArray())
            {
                string rawText = r.TryGetProperty("text", out var rp) ? rp.GetString() ?? string.Empty : string.Empty;
                foreach (string part in rawText.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!IsPlayCountLabel(part))
                    {
                        tokens.Add(part);
                    }
                }
            }
        }

        string artist = tokens.Count > 0 ? tokens[0] : "Featured Artist";
        string album = tokens.Count > 1 && !ExactFourDigitYearRegex().IsMatch(tokens[1]) && !tokens[1].Contains(':')
            ? tokens[1]
            : "Radio Mix";

        string lengthStr = renderer.TryGetProperty("lengthText", out var lObj) ? ExtractAllRunsText(lObj).Trim() : string.Empty;
        TimeSpan duration = TryParseDurationString(lengthStr, out var parsedDur) ? parsedDur : TimeSpan.FromMinutes(3.5);

        string artwork = ExtractHighResThumbnailUrl(renderer);
        if (string.IsNullOrWhiteSpace(artwork))
        {
            artwork = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        return new TrackItem
        {
            Id = $"ytm_{videoId}",
            YouTubeVideoId = videoId,
            Title = title,
            Artist = artist,
            Album = album,
            Duration = duration,
            CoverArtUri = artwork,
            AudioSourceUri = $"ytmusic://{videoId}",
            SourceType = MediaSourceType.Online,
            IsExplicit = HasExplicitBadge(renderer),
            QualityBadge = "HQ AAC",
            Genre = "Streaming",
            ReleaseYear = DateTime.Now.Year
        };
    }

    private static List<TrackItem> ParsePlaylistBrowseTracks(JsonElement root)
    {
        var tracks = new List<TrackItem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("contents", out var contents) &&
            contents.TryGetProperty("twoColumnBrowseResultsRenderer", out var twoCol) &&
            twoCol.TryGetProperty("secondaryContents", out var secondary))
        {
            ExtractResponsiveTrackItemsRecursive(secondary, tracks, seenIds);
        }

        if (tracks.Count == 0)
        {
            ExtractResponsiveTrackItemsRecursive(root, tracks, seenIds);
        }

        return tracks;
    }

    private static List<JsonElement> ExtractBrowseSections(JsonElement root)
    {
        var result = new List<JsonElement>();
        if (root.TryGetProperty("contents", out var contents) &&
            contents.TryGetProperty("singleColumnBrowseResultsRenderer", out var sc) &&
            sc.TryGetProperty("tabs", out var tabs) &&
            tabs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabs.EnumerateArray())
            {
                if (tab.TryGetProperty("tabRenderer", out var tr) &&
                    tr.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("sectionListRenderer", out var slr) &&
                    slr.TryGetProperty("contents", out var secContents) &&
                    secContents.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sec in secContents.EnumerateArray())
                    {
                        result.Add(sec);
                    }
                }
            }
        }

        if (root.TryGetProperty("continuationContents", out var cont))
        {
            if (cont.TryGetProperty("sectionListContinuation", out var slc) &&
                slc.TryGetProperty("contents", out var slcContents) &&
                slcContents.ValueKind == JsonValueKind.Array)
            {
                foreach (var sec in slcContents.EnumerateArray())
                {
                    result.Add(sec);
                }
            }

            if (cont.TryGetProperty("musicShelfContinuation", out _))
            {
                result.Add(cont);
            }
        }

        return result;
    }

    private static string ExtractSectionTitle(JsonElement section)
    {
        if (section.TryGetProperty("musicCarouselShelfRenderer", out var carousel) &&
            carousel.TryGetProperty("header", out var h) &&
            h.TryGetProperty("musicCarouselShelfBasicHeaderRenderer", out var bh) &&
            bh.TryGetProperty("title", out var t))
        {
            return ExtractAllRunsText(t).Trim();
        }

        if (section.TryGetProperty("musicShelfRenderer", out var shelf) &&
            shelf.TryGetProperty("title", out var st))
        {
            return ExtractAllRunsText(st).Trim();
        }

        if (section.TryGetProperty("gridRenderer", out var grid) &&
            grid.TryGetProperty("header", out var gh) &&
            gh.TryGetProperty("gridHeaderRenderer", out var ghr) &&
            ghr.TryGetProperty("title", out var gt))
        {
            return ExtractAllRunsText(gt).Trim();
        }

        return string.Empty;
    }

    private static bool TryGetSectionItemsArray(JsonElement section, out JsonElement itemsArray)
    {
        if (section.TryGetProperty("musicCarouselShelfRenderer", out var carousel) &&
            carousel.TryGetProperty("contents", out itemsArray) &&
            itemsArray.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (section.TryGetProperty("musicShelfRenderer", out var shelf) &&
            shelf.TryGetProperty("contents", out itemsArray) &&
            itemsArray.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (section.TryGetProperty("musicShelfContinuation", out var shelfCont) &&
            shelfCont.TryGetProperty("contents", out itemsArray) &&
            itemsArray.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (section.TryGetProperty("gridRenderer", out var grid) &&
            grid.TryGetProperty("items", out itemsArray) &&
            itemsArray.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        itemsArray = default;
        return false;
    }

    private static string? ExtractNextContinuationToken(JsonElement root)
    {
        if (root.TryGetProperty("contents", out var contents) &&
            contents.TryGetProperty("singleColumnBrowseResultsRenderer", out var sc) &&
            sc.TryGetProperty("tabs", out var tabs) &&
            tabs.ValueKind == JsonValueKind.Array &&
            tabs.GetArrayLength() > 0 &&
            tabs[0].TryGetProperty("tabRenderer", out var tr) &&
            tr.TryGetProperty("content", out var content) &&
            content.TryGetProperty("sectionListRenderer", out var slr) &&
            slr.TryGetProperty("continuations", out var conts) &&
            conts.ValueKind == JsonValueKind.Array &&
            conts.GetArrayLength() > 0 &&
            conts[0].TryGetProperty("nextContinuationData", out var ncd) &&
            ncd.TryGetProperty("continuation", out var tok))
        {
            string? token = tok.GetString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        if (root.TryGetProperty("continuationContents", out var cc) &&
            cc.TryGetProperty("sectionListContinuation", out var slc) &&
            slc.TryGetProperty("continuations", out var conts2) &&
            conts2.ValueKind == JsonValueKind.Array &&
            conts2.GetArrayLength() > 0 &&
            conts2[0].TryGetProperty("nextContinuationData", out var ncd2) &&
            ncd2.TryGetProperty("continuation", out var tok2))
        {
            string? token = tok2.GetString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string ExtractBrowsePageType(JsonElement navEp)
    {
        if (navEp.TryGetProperty("browseEndpoint", out var be) &&
            be.TryGetProperty("browseEndpointContextSupportedConfigs", out var cfg) &&
            cfg.TryGetProperty("browseEndpointContextMusicConfig", out var mcfg) &&
            mcfg.TryGetProperty("pageType", out var pt))
        {
            return pt.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool IsRadioShelf(string shelfTitle)
    {
        string lower = shelfTitle.ToLowerInvariant();
        return lower.Contains("radio") ||
               lower.Contains("station") ||
               lower.Contains("estación") ||
               lower.Contains("estaciones");
    }

    private static bool IsPlaylistOrMixShelf(string shelfTitle)
    {
        string lower = shelfTitle.ToLowerInvariant();
        return lower.Contains("mix") ||
               lower.Contains("playlist") ||
               lower.Contains("for you") ||
               lower.Contains("para ti") ||
               lower.Contains("focus") ||
               lower.Contains("workout") ||
               lower.Contains("mood") ||
               lower.Contains("featured") ||
               lower.Contains("community") ||
               lower.Contains("hecho para ti");
    }

    private static bool IsNewReleaseShelf(string shelfTitle)
    {
        string lower = shelfTitle.ToLowerInvariant();
        return lower.Contains("new release") ||
               lower.Contains("new album") ||
               lower.Contains("new music") ||
               lower.Contains("novedad") ||
               lower.Contains("nuevo") ||
               lower.Contains("nuevos") ||
               lower.Contains("reciente");
    }

    private static string ExtractArtistFromSubtitle(string subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return string.Empty;
        }

        foreach (string rawPart in subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart.Equals("Album", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Single", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("EP", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Álbum", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Sencillo", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Song", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Canción", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                ExactFourDigitYearRegex().IsMatch(rawPart) ||
                rawPart.Contains("views", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Contains("reproducc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return rawPart;
        }

        return string.Empty;
    }

    private static string? ExtractPlaylistAuthor(string subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return null;
        }

        foreach (string rawPart in subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!rawPart.Equals("Playlist", StringComparison.OrdinalIgnoreCase) &&
                !rawPart.Contains("song", StringComparison.OrdinalIgnoreCase) &&
                !rawPart.Contains("track", StringComparison.OrdinalIgnoreCase) &&
                !rawPart.Contains("cancion", StringComparison.OrdinalIgnoreCase))
            {
                return rawPart;
            }
        }

        return null;
    }

    private static string? ExtractTrackCount(string subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return null;
        }

        foreach (string rawPart in subtitle.Split('•', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart.Contains("song", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Contains("track", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Contains("cancion", StringComparison.OrdinalIgnoreCase))
            {
                return rawPart;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> WarmArtworkCacheAsync(
        IEnumerable<string> artworkUris,
        CancellationToken cancellationToken = default)
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (artworkUris is null)
        {
            return result;
        }

        var distinctRemoteUris = artworkUris
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctRemoteUris.Count == 0)
        {
            return result;
        }

        try
        {
            Directory.CreateDirectory(_coverCacheDirectory);
        }
        catch
        {
            return result;
        }

        var toDownload = new List<(string OriginalUri, string NormalizedUri)>();
        foreach (string originalUri in distinctRemoteUris)
        {
            string normalizedUri = NormalizeHighResThumbnailUrl(originalUri, targetSizePx: 544);
            if (string.IsNullOrWhiteSpace(normalizedUri))
            {
                continue;
            }

            if (ArtworkCacheManager.TryGetCachedFileUri(normalizedUri, 544, out string? existingFileUri, out _) &&
                !string.IsNullOrWhiteSpace(existingFileUri))
            {
                result[originalUri] = existingFileUri!;
                continue;
            }

            toDownload.Add((originalUri, normalizedUri));
        }

        if (toDownload.Count == 0)
        {
            return result;
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(TimeSpan.FromSeconds(4));
        using var gate = new SemaphoreSlim(4, 4);

        var tasks = toDownload.Select(async item =>
        {
            try
            {
                await gate.WaitAsync(budgetCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var outcome = await ArtworkCacheManager.GetOrDownloadAsync(
                    item.NormalizedUri,
                    minDimensionPx: 544,
                    budgetCts.Token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(outcome.LocalFileUri))
                {
                    result[item.OriginalUri] = outcome.LocalFileUri!;
                }
            }
            catch (OperationCanceledException)
            {
                // Budget elapsed; caller gracefully keeps the normalized HTTPS URL.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Artwork cache warm skipped for '{Uri}'.", item.NormalizedUri);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private async Task<string?> DownloadAndCacheCoverAsync(
        string imageUrl,
        string hash,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedCoverFileUri(hash, out string? cachedUri))
        {
            return cachedUri;
        }

        var fileLock = GetStripedLock($"cover:{hash}", StringComparer.OrdinalIgnoreCase);
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedCoverFileUri(hash, out cachedUri))
            {
                return cachedUri;
            }

            string tempPath = Path.Combine(_coverCacheDirectory, $"ytm_{hash}_{Guid.NewGuid():N}.tmp");
            try
            {
                var (ext, isWebp) = await StreamImageToTempFileAsync(imageUrl, tempPath, cancellationToken).ConfigureAwait(false);
                if (ext is null && isWebp &&
                    (imageUrl.Contains("googleusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
                     imageUrl.Contains("ggpht.com/", StringComparison.OrdinalIgnoreCase)))
                {
                    // Retry once forcing JPEG encoding (-rj) so WinUI WIC never fails on WebP-only environments
                    string jpegUrl = ForceGoogleJpegUrl(imageUrl);
                    if (!string.Equals(jpegUrl, imageUrl, StringComparison.Ordinal))
                    {
                        (ext, _) = await StreamImageToTempFileAsync(jpegUrl, tempPath, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (ext is null || !File.Exists(tempPath))
                {
                    return null;
                }

                string targetPath = Path.Combine(_coverCacheDirectory, $"ytm_{hash}{ext}");
                File.Move(tempPath, targetPath, overwrite: true);
                return new Uri(Path.GetFullPath(targetPath)).AbsoluteUri;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<(string? Extension, bool IsWebp)> StreamImageToTempFileAsync(
        string url,
        string tempPath,
        CancellationToken cancellationToken)
    {
        const long maxImageBytes = 8_000_000;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/gif,image/bmp;q=0.9,*/*;q=0.5");

        using var resp = await _downloadHttpClient.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return (null, false);
        }

        if (resp.Content.Headers.ContentLength is > maxImageBytes)
        {
            return (null, false);
        }

        await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] header = new byte[16];
        int headerRead = 0;
        while (headerRead < 12)
        {
            int n = await netStream.ReadAsync(header.AsMemory(headerRead, header.Length - headerRead), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            headerRead += n;
        }

        if (headerRead < 12)
        {
            return (null, false);
        }

        string? ext = DetectWicSafeImageExtension(header);
        if (ext is null)
        {
            return (null, IsWebpPayload(header));
        }

        long totalBytes = headerRead;
        byte[] copyBuf = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            await using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16384,
                useAsync: true))
            {
                await fs.WriteAsync(header.AsMemory(0, headerRead), cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    int read = await netStream.ReadAsync(copyBuf.AsMemory(0, copyBuf.Length), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    totalBytes += read;
                    if (totalBytes > maxImageBytes)
                    {
                        return (null, false);
                    }

                    await fs.WriteAsync(copyBuf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            return totalBytes >= 200 ? (ext, false) : (null, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuf);
        }
    }

    private bool TryGetCachedCoverFileUri(string hash, out string? fileUri)
    {
        string[] exts = [".jpg", ".png", ".gif", ".bmp"];
        foreach (string ext in exts)
        {
            string candidate = Path.Combine(_coverCacheDirectory, $"ytm_{hash}{ext}");
            if (File.Exists(candidate))
            {
                try
                {
                    if (new FileInfo(candidate).Length > 200)
                    {
                        fileUri = new Uri(Path.GetFullPath(candidate)).AbsoluteUri;
                        return true;
                    }
                }
                catch
                {
                    // Ignore transient IO check
                }
            }
        }

        fileUri = null;
        return false;
    }

    private static string ComputeCoverCacheHash(string url)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string? DetectWicSafeImageExtension(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return null;
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return ".jpg";
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return ".gif";
        }

        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return ".bmp";
        }

        return null;
    }

    private static bool IsWebpPayload(byte[] bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    private static string ForceGoogleJpegUrl(string url)
    {
        int queryIdx = url.IndexOf('?');
        string pathPart = queryIdx >= 0 ? url[..queryIdx] : url;
        string queryPart = queryIdx >= 0 ? url[queryIdx..] : string.Empty;

        int eqIdx = pathPart.LastIndexOf('=');
        if (eqIdx >= 0 && eqIdx < pathPart.Length - 1)
        {
            string token = pathPart[eqIdx..];
            if (token.Contains("-rw", StringComparison.Ordinal))
            {
                token = WebpFormatFlagRegex().Replace(token, "-rj");
            }
            else if (!token.Contains("-rj", StringComparison.Ordinal))
            {
                token += "-rj";
            }

            return pathPart[..eqIdx] + token + queryPart;
        }

        return $"{pathPart}=w544-h544-l90-rj{queryPart}";
    }

    /// <inheritdoc />
    public async Task<MoodsAndGenresFeed> GetMoodsAndGenresAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_moodsCacheLock)
        {
            if (_moodsAndGenresCache is { } cached &&
                DateTimeOffset.UtcNow < cached.ExpiresAtUtc &&
                cached.Feed.HasSections)
            {
                return cached.Feed;
            }
        }

        try
        {
            if (IsAuthenticated && !_triedAccountMetaSync)
            {
                try
                {
                    await EnsureAccountSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Non-fatal: proceed with browse fallback
                }
            }

            using var doc = await ExecuteBrowseWithAnonFallbackAsync(
                MoodsAndGenresBrowseId,
                browseParams: null,
                cancellationToken).ConfigureAwait(false);

            if (doc is not null)
            {
                var parsed = ParseMoodsAndGenresResponse(doc.RootElement);
                if (parsed.HasSections)
                {
                    lock (_moodsCacheLock)
                    {
                        _moodsAndGenresCache = (parsed, DateTimeOffset.UtcNow.Add(MoodsCacheTtl));
                    }

                    return parsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Music FEmusic_moods_and_genres fetch failed; returning fallback genres.");
        }

        return BuildFallbackMoodsAndGenresFeed();
    }

    /// <inheritdoc />
    public async Task<MoodGenreDetailResult> GetMoodGenreCategoryContentAsync(
        string browseId,
        string? @params,
        string categoryTitle,
        CancellationToken cancellationToken = default)
    {
        string cleanBrowseId = browseId?.Trim() ?? string.Empty;
        string? cleanParams = string.IsNullOrWhiteSpace(@params) ? null : @params.Trim();
        string cleanTitle = categoryTitle?.Trim() ?? string.Empty;
        string stripeColorHex = ResolveCachedMoodStripeColorHex(cleanBrowseId, cleanParams, cleanTitle);

        if (string.IsNullOrWhiteSpace(cleanBrowseId) && string.IsNullOrWhiteSpace(cleanTitle))
        {
            return new MoodGenreDetailResult(cleanTitle, cleanBrowseId, cleanParams, [], [], [], stripeColorHex);
        }

        string cacheKey = !string.IsNullOrWhiteSpace(cleanBrowseId)
            ? $"{cleanBrowseId}|{cleanParams ?? string.Empty}"
            : $"title::{cleanTitle}";

        if (_moodGenreDetailCache.TryGet(cacheKey, out var cachedEntry) &&
            DateTimeOffset.UtcNow < cachedEntry.ExpiresAtUtc)
        {
            var cachedDetail = cachedEntry.Detail;
            if (!string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(cachedDetail.CategoryTitle))
            {
                return cachedDetail with { CategoryTitle = cleanTitle };
            }

            return cachedDetail;
        }

        try
        {
            string resolvedTitle = cleanTitle;
            var playlists = new List<RecommendedPlaylistItem>();
            var albums = new List<AlbumItem>();
            var songs = new List<TrackItem>();

            bool isFallbackGenre = cleanBrowseId.StartsWith("fallback_genre:", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(cleanBrowseId) && !isFallbackGenre)
            {
                using var doc = await ExecuteBrowseWithAnonFallbackAsync(
                    cleanBrowseId,
                    cleanParams,
                    cancellationToken).ConfigureAwait(false);

                if (doc is not null)
                {
                    if (string.IsNullOrWhiteSpace(resolvedTitle))
                    {
                        resolvedTitle = ExtractMoodHeaderTitle(doc.RootElement);
                    }

                    var feed = ParseHomeFeedResponse(doc.RootElement, IsAuthenticated);
                    var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pl in feed.PlaylistsMadeForYou)
                    {
                        if (!string.IsNullOrWhiteSpace(pl.PlaylistId) && seenPlaylistIds.Add(pl.PlaylistId))
                        {
                            playlists.Add(pl);
                        }
                    }

                    foreach (var radio in feed.RadioStations)
                    {
                        if (!string.IsNullOrWhiteSpace(radio.PlaylistId) && seenPlaylistIds.Add(radio.PlaylistId))
                        {
                            playlists.Add(new RecommendedPlaylistItem(
                                Id: radio.Id,
                                Title: radio.Title,
                                Subtitle: radio.Subtitle,
                                ArtworkUri: radio.ArtworkUri,
                                PlaylistId: radio.PlaylistId,
                                BrowseId: radio.PlaylistId.StartsWith("VL", StringComparison.Ordinal) ? radio.PlaylistId : $"VL{radio.PlaylistId}"));
                        }
                    }

                    albums.AddRange(feed.PersonalizedAlbums);
                    songs.AddRange(feed.PersonalizedTracks);
                }
            }

            // Fallback to parallel YouTube Music search if browse returned empty (e.g. offline fallback tile clicked once online)
            if (playlists.Count == 0 && albums.Count == 0 && songs.Count == 0 && !string.IsNullOrWhiteSpace(resolvedTitle))
            {
                var fallbackPlaylistsTask = SearchPlaylistsAsync(resolvedTitle, limit: 12, cancellationToken);
                var fallbackAlbumsTask = ExecuteAlbumsSearchAsync(resolvedTitle, strictlyAlbumsOnly: true, cancellationToken);
                var fallbackSongsTask = ExecuteSongsSearchAsync(resolvedTitle, useSongsFilter: true, cancellationToken);
                await Task.WhenAll(fallbackPlaylistsTask, fallbackAlbumsTask, fallbackSongsTask).ConfigureAwait(false);

                playlists.AddRange(await fallbackPlaylistsTask.ConfigureAwait(false));
                albums.AddRange(await fallbackAlbumsTask.ConfigureAwait(false));
                songs.AddRange(await fallbackSongsTask.ConfigureAwait(false));

                if (songs.Count == 0)
                {
                    songs.AddRange(await ExecuteSongsSearchAsync(resolvedTitle, useSongsFilter: false, cancellationToken).ConfigureAwait(false));
                }
            }

            // Warm all covers via WIC disk cache so WinUI <Image> never drops WebP/remote thumbnails
            var artworkUris = playlists.Select(p => p.ArtworkUri)
                .Concat(albums.Select(a => a.CoverArtUri))
                .Concat(songs.Select(s => s.CoverArtUri));
            var warmedMap = await WarmArtworkCacheAsync(artworkUris, cancellationToken).ConfigureAwait(false);

            var warmedPlaylists = ApplyWarmedPlaylistArtworks(playlists, warmedMap);
            var warmedAlbums = ApplyWarmedAlbumArtworks(albums, warmedMap);
            var warmedSongs = ApplyWarmedTrackArtworks(songs, warmedMap);

            var detail = new MoodGenreDetailResult(
                CategoryTitle: string.IsNullOrWhiteSpace(resolvedTitle) ? "Moods & Genres" : resolvedTitle,
                BrowseId: cleanBrowseId,
                Params: cleanParams,
                Playlists: warmedPlaylists,
                Albums: warmedAlbums,
                Songs: warmedSongs,
                StripeColorHex: stripeColorHex);

            if (detail.HasAnyContent)
            {
                _moodGenreDetailCache.Set(cacheKey, (detail, DateTimeOffset.UtcNow.Add(MoodsCacheTtl)));
            }

            return detail;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load YouTube Music mood/genre category '{Title}' ({BrowseId}).", cleanTitle, cleanBrowseId);
            return new MoodGenreDetailResult(
                string.IsNullOrWhiteSpace(cleanTitle) ? "Moods & Genres" : cleanTitle,
                cleanBrowseId,
                cleanParams,
                [],
                [],
                [],
                stripeColorHex);
        }
    }

    /// <inheritdoc />
    public async Task<YouTubeMusicSearchResult> SearchRichCatalogAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        string cleanQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return YouTubeMusicSearchResult.Empty();
        }

        if (_richSearchCache.TryGet(cleanQuery, out var cached) &&
            DateTimeOffset.UtcNow < cached.ExpiresAtUtc &&
            cached.Result.HasAnyResults)
        {
            return cached.Result;
        }

        try
        {
            var songsTask = ExecuteSongsSearchAsync(cleanQuery, useSongsFilter: true, cancellationToken);
            var albumsTask = ExecuteAlbumsSearchAsync(cleanQuery, strictlyAlbumsOnly: true, cancellationToken);
            var artistsTask = ExecuteArtistsSearchAsync(cleanQuery, limit: 10, cancellationToken);
            var playlistsTask = SearchPlaylistsAsync(cleanQuery, limit: 10, cancellationToken);

            await Task.WhenAll(songsTask, albumsTask, artistsTask, playlistsTask).ConfigureAwait(false);

            var songs = await songsTask.ConfigureAwait(false);
            if (songs.Count == 0)
            {
                songs = await ExecuteSongsSearchAsync(cleanQuery, useSongsFilter: false, cancellationToken).ConfigureAwait(false);
            }

            var albums = await albumsTask.ConfigureAwait(false);
            if (albums.Count == 0 && songs.Count > 0)
            {
                albums = BuildAlbumsFromTracks(songs);
            }

            var directArtists = await artistsTask.ConfigureAwait(false);
            var (artists, _) = await MergeAndEnrichSearchArtistsAsync(
                directArtists,
                songs,
                limit: 10,
                cancellationToken).ConfigureAwait(false);

            var playlists = (await playlistsTask.ConfigureAwait(false)).ToList();

            var artworkUris = songs.Select(s => s.CoverArtUri)
                .Concat(albums.Select(a => a.CoverArtUri))
                .Concat(artists.Select(a => a.AvatarUri))
                .Concat(playlists.Select(p => p.ArtworkUri));
            var warmedMap = await WarmArtworkCacheAsync(artworkUris, cancellationToken).ConfigureAwait(false);

            var warmedSongs = ApplyWarmedTrackArtworks(songs, warmedMap);
            var warmedAlbums = ApplyWarmedAlbumArtworks(albums, warmedMap);
            var warmedArtists = ApplyWarmedArtistArtworks(artists, warmedMap);
            var warmedPlaylists = ApplyWarmedPlaylistArtworks(playlists, warmedMap);

            var topResult = SelectSearchTopResult(
                cleanQuery,
                warmedSongs,
                warmedAlbums,
                warmedArtists,
                warmedPlaylists);

            var result = new YouTubeMusicSearchResult(
                Query: cleanQuery,
                TopResult: topResult,
                Songs: warmedSongs,
                Albums: warmedAlbums,
                Artists: warmedArtists,
                Playlists: warmedPlaylists);

            if (result.HasAnyResults)
            {
                _richSearchCache.Set(cleanQuery, (result, DateTimeOffset.UtcNow.Add(RichSearchCacheTtl)));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube Music rich search failed for query '{Query}'.", cleanQuery);
            return YouTubeMusicSearchResult.Empty(cleanQuery);
        }
    }

    private async Task<JsonDocument?> ExecuteBrowseWithAnonFallbackAsync(
        string browseId,
        string? browseParams,
        CancellationToken cancellationToken)
    {
        var authPayload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(includeUser: true),
            ["browseId"] = browseId
        };
        if (!string.IsNullOrWhiteSpace(browseParams))
        {
            authPayload["params"] = browseParams;
        }

        var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            authPayload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);
        if (doc is not null || !IsAuthenticated)
        {
            return doc;
        }

        var anonPayload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(includeUser: false),
            ["browseId"] = browseId
        };
        if (!string.IsNullOrWhiteSpace(browseParams))
        {
            anonPayload["params"] = browseParams;
        }

        return await ExecuteInnerTubePostDocAsync(
            YtMusicBrowseEndpoint,
            anonPayload,
            includeAuthHeaders: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static MoodsAndGenresFeed ParseMoodsAndGenresResponse(JsonElement root)
    {
        var sections = new List<MoodGenreSection>();
        var seenCategoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in ExtractBrowseSections(root))
        {
            string sectionTitle = ExtractSectionTitle(section);
            if (!TryGetSectionItemsArray(section, out var itemsArray))
            {
                continue;
            }

            var parsedItems = new List<MoodGenreCategoryItem>();
            foreach (var entry in itemsArray.EnumerateArray())
            {
                if (entry.TryGetProperty("musicNavigationButtonRenderer", out var navButton))
                {
                    var category = TryParseMoodNavigationButton(navButton);
                    if (category is not null && seenCategoryIds.Add(category.Id))
                    {
                        parsedItems.Add(category);
                    }

                    continue;
                }

                if (entry.TryGetProperty("musicTwoRowItemRenderer", out var twoRow))
                {
                    string title = twoRow.TryGetProperty("title", out var titleEl)
                        ? ExtractAllRunsText(titleEl).Trim()
                        : string.Empty;

                    string browseId = string.Empty;
                    string? catParams = null;
                    if (twoRow.TryGetProperty("navigationEndpoint", out var navEp) &&
                        navEp.TryGetProperty("browseEndpoint", out var browseEp))
                    {
                        browseId = browseEp.TryGetProperty("browseId", out var bidProp)
                            ? bidProp.GetString()?.Trim() ?? string.Empty
                            : string.Empty;
                        if (browseEp.TryGetProperty("params", out var paramsProp))
                        {
                            string? rawParams = paramsProp.GetString()?.Trim();
                            catParams = string.IsNullOrWhiteSpace(rawParams) ? null : rawParams;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(browseId))
                    {
                        string uniqueId = $"{browseId}_{(string.IsNullOrWhiteSpace(catParams) ? title.ToLowerInvariant() : catParams)}";
                        if (seenCategoryIds.Add(uniqueId))
                        {
                            parsedItems.Add(new MoodGenreCategoryItem(
                                Id: uniqueId,
                                Title: title,
                                BrowseId: browseId,
                                Params: catParams));
                        }
                    }
                }
            }

            if (parsedItems.Count > 0)
            {
                sections.Add(new MoodGenreSection(
                    Title: string.IsNullOrWhiteSpace(sectionTitle) ? "Moods & Genres" : sectionTitle,
                    Items: parsedItems));
            }
        }

        return new MoodsAndGenresFeed(sections, IsFromYouTubeMusic: sections.Count > 0);
    }

    private static MoodGenreCategoryItem? TryParseMoodNavigationButton(JsonElement navButton)
    {
        string title = navButton.TryGetProperty("buttonText", out var buttonText)
            ? ExtractAllRunsText(buttonText).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        JsonElement clickEndpoint = default;
        bool hasEndpoint = navButton.TryGetProperty("clickCommand", out clickEndpoint) ||
                           navButton.TryGetProperty("navigationEndpoint", out clickEndpoint);
        if (!hasEndpoint || !clickEndpoint.TryGetProperty("browseEndpoint", out var browseEndpoint))
        {
            return null;
        }

        string browseId = browseEndpoint.TryGetProperty("browseId", out var bidProp)
            ? bidProp.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(browseId))
        {
            return null;
        }

        string? catParams = null;
        if (browseEndpoint.TryGetProperty("params", out var paramsProp))
        {
            string? rawParams = paramsProp.GetString()?.Trim();
            catParams = string.IsNullOrWhiteSpace(rawParams) ? null : rawParams;
        }

        long rawStripeColor = 0L;
        if (navButton.TryGetProperty("solid", out var solid) &&
            solid.TryGetProperty("leftStripeColor", out var colorEl))
        {
            if (colorEl.ValueKind == JsonValueKind.Number)
            {
                if (!colorEl.TryGetInt64(out rawStripeColor) && colorEl.TryGetDouble(out double d))
                {
                    rawStripeColor = (long)d;
                }
            }
            else if (colorEl.ValueKind == JsonValueKind.String)
            {
                _ = long.TryParse(colorEl.GetString(), out rawStripeColor);
            }
        }

        var (argb, hex) = MoodGenreCategoryItem.NormalizeStripeColor(rawStripeColor);
        string uniqueId = $"{browseId}_{(string.IsNullOrWhiteSpace(catParams) ? title.ToLowerInvariant() : catParams)}";

        return new MoodGenreCategoryItem(
            Id: uniqueId,
            Title: title,
            BrowseId: browseId,
            Params: catParams,
            StripeColorHex: hex,
            StripeColorArgb: argb);
    }

    private string ResolveCachedMoodStripeColorHex(string browseId, string? browseParams, string categoryTitle)
    {
        MoodsAndGenresFeed? cachedFeed = null;
        lock (_moodsCacheLock)
        {
            cachedFeed = _moodsAndGenresCache?.Feed;
        }

        var sections = cachedFeed?.Sections ?? BuildFallbackMoodsAndGenresFeed().Sections;
        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                bool idMatch = !string.IsNullOrWhiteSpace(browseId) &&
                               string.Equals(item.BrowseId, browseId, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(item.Params ?? string.Empty, browseParams ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                bool titleMatch = !string.IsNullOrWhiteSpace(categoryTitle) &&
                                  string.Equals(item.Title, categoryTitle, StringComparison.OrdinalIgnoreCase);
                if (idMatch || titleMatch)
                {
                    return item.StripeColorHex;
                }
            }
        }

        return "#FF6366F1";
    }

    private static MoodsAndGenresFeed BuildFallbackMoodsAndGenresFeed()
    {
        var fallbackGenres = new (string Id, string Title, uint Argb)[]
        {
            ("pop", "Pop", 0xFFE91E63u),
            ("rock", "Rock", 0xFFD32F2Fu),
            ("blues", "Blues", 0xFF1976D2u),
            ("hiphop", "Hip-Hop", 0xFF512DA8u),
            ("electronic", "Electronic", 0xFF00ACC1u),
            ("rnb", "R&B & Soul", 0xFF6A1B9Au),
            ("jazz", "Jazz", 0xFF795548u),
            ("indie", "Indie & Alternative", 0xFF00796Bu),
            ("kpop", "K-Pop", 0xFF9C27B0u),
            ("reggaeton", "Reggaeton", 0xFFE64A19u),
            ("classical", "Classical", 0xFF827717u),
            ("lofi", "Lo-Fi Beats", 0xFF455A64u),
            ("synthwave", "Synthwave", 0xFFC2185Bu),
            ("metal", "Metal", 0xFF37474Fu),
            ("ambient", "Ambient & Chill", 0xFF00838Fu),
            ("folk", "Folk & Acoustic", 0xFF33691Eu),
            ("country", "Country", 0xFF5D4037u),
            ("latinurban", "Latin Urban", 0xFFF4511Eu),
            ("house", "House & Techno", 0xFF0288D1u),
            ("afrobeat", "Afrobeat", 0xFFFBC02Du)
        };

        var items = fallbackGenres
            .Select(g => new MoodGenreCategoryItem(
                Id: $"genre_{g.Id}",
                Title: g.Title,
                BrowseId: $"fallback_genre:{g.Id}",
                Params: g.Id,
                StripeColorHex: MoodGenreCategoryItem.FormatArgbHex(g.Argb),
                StripeColorArgb: g.Argb))
            .ToList();

        return new MoodsAndGenresFeed(
            Sections: [new MoodGenreSection("Genres", items)],
            IsFromYouTubeMusic: false);
    }

    private static string ExtractMoodHeaderTitle(JsonElement root)
    {
        if (root.TryGetProperty("header", out var header) &&
            header.TryGetProperty("musicHeaderRenderer", out var mhr) &&
            mhr.TryGetProperty("title", out var titleEl))
        {
            string direct = ExtractAllRunsText(titleEl).Trim();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        return FindHeaderRendererTitleRecursive(root) ?? string.Empty;
    }

    private static string? FindHeaderRendererTitleRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if ((element.TryGetProperty("musicHeaderRenderer", out var hr) ||
                 element.TryGetProperty("musicDetailHeaderRenderer", out hr)) &&
                hr.TryGetProperty("title", out var titleEl))
            {
                string text = ExtractAllRunsText(titleEl).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                string? found = FindHeaderRendererTitleRecursive(prop.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string? found = FindHeaderRendererTitleRecursive(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private async Task<List<ArtistItem>> ExecuteArtistsSearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["context"] = BuildWebRemixContext(),
                ["query"] = query,
                ["params"] = YtMusicArtistsFilterParams
            };

            using var doc = await ExecuteInnerTubePostDocAsync(
                YtMusicSearchEndpoint,
                payload,
                includeAuthHeaders: true,
                cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return [];
            }

            var results = new List<ArtistItem>(limit);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool cacheUpdated = false;
            ExtractSearchArtistsRecursive(doc.RootElement, results, seenKeys, limit, ref cacheUpdated);
            if (cacheUpdated)
            {
                PersistArtistAvatarCacheSafe();
                PersistArtistAudienceCacheSafe();
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "YouTube Music artist search failed for '{Query}'.", query);
            return [];
        }
    }

    private void ExtractSearchArtistsRecursive(
        JsonElement element,
        List<ArtistItem> results,
        HashSet<string> seenKeys,
        int limit,
        ref bool cacheUpdated)
    {
        if (results.Count >= limit)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("musicCardShelfRenderer", out var card))
            {
                string name = card.TryGetProperty("title", out var titleObj) ? ExtractAllRunsText(titleObj).Trim() : string.Empty;
                string subtitle = card.TryGetProperty("subtitle", out var subObj) ? ExtractAllRunsText(subObj).Trim() : string.Empty;
                JsonElement navEp = default;
                bool hasNav = false;
                if (card.TryGetProperty("title", out var tRunsContainer) &&
                    tRunsContainer.TryGetProperty("runs", out var tRuns) &&
                    tRuns.ValueKind == JsonValueKind.Array &&
                    tRuns.GetArrayLength() > 0 &&
                    tRuns[0].TryGetProperty("navigationEndpoint", out navEp))
                {
                    hasNav = true;
                }
                else if (card.TryGetProperty("onTap", out navEp))
                {
                    hasNav = true;
                }

                string browseId = hasNav ? (FindFirstPropertyString(navEp, "browseId") ?? string.Empty) : string.Empty;
                string pageType = hasNav ? ExtractBrowsePageType(navEp) : string.Empty;
                bool isArtistCard = pageType == "MUSIC_PAGE_TYPE_ARTIST" ||
                                    browseId.StartsWith("UC", StringComparison.Ordinal) ||
                                    browseId.StartsWith("FEmusic_library_privately_owned_artist_detail", StringComparison.Ordinal) ||
                                    IsArtistSubtitleLabel(subtitle);

                if (isArtistCard && !string.IsNullOrWhiteSpace(name) && seenKeys.Add(name))
                {
                    string avatarUrl = ExtractHighResThumbnailUrl(card);
                    string audience = ExtractAudienceSegmentFromSubtitle(subtitle);
                    if (!string.IsNullOrWhiteSpace(avatarUrl))
                    {
                        EnsureArtistAvatarCacheLoaded();
                        if (!_artistAvatarCache.ContainsKey(name))
                        {
                            _artistAvatarCache.Set(name, avatarUrl);
                            cacheUpdated = true;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(audience))
                    {
                        EnsureArtistAudienceCacheLoaded();
                        if (!_artistAudienceCache.ContainsKey(name))
                        {
                            _artistAudienceCache.Set(name, audience);
                            cacheUpdated = true;
                        }
                    }

                    string artistId = browseId.StartsWith("UC", StringComparison.Ordinal)
                        ? $"ytm_sim_{browseId}"
                        : $"ytm_artist_{(!string.IsNullOrWhiteSpace(browseId) ? browseId : name.GetHashCode().ToString())}";

                    results.Add(new ArtistItem
                    {
                        Id = artistId,
                        BrowseId = browseId,
                        Name = name,
                        AvatarUri = avatarUrl,
                        Genre = !string.IsNullOrWhiteSpace(audience) ? audience : "YouTube Music",
                        MonthlyAudienceText = audience,
                        SourceType = MediaSourceType.Online
                    });

                    if (results.Count >= limit)
                    {
                        return;
                    }
                }
            }

            if (element.TryGetProperty("musicResponsiveListItemRenderer", out var renderer))
            {
                if (renderer.TryGetProperty("flexColumns", out var flexCols) &&
                    flexCols.ValueKind == JsonValueKind.Array &&
                    flexCols.GetArrayLength() > 0)
                {
                    string name = ExtractRunsText(flexCols[0]).Trim();
                    string subtitle = flexCols.GetArrayLength() > 1 ? ExtractRunsText(flexCols[1]).Trim() : string.Empty;

                    JsonElement navEp = default;
                    bool hasNav = renderer.TryGetProperty("navigationEndpoint", out navEp);
                    if (!hasNav &&
                        flexCols[0].TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var col0) &&
                        col0.TryGetProperty("text", out var col0Text) &&
                        col0Text.TryGetProperty("runs", out var col0Runs) &&
                        col0Runs.ValueKind == JsonValueKind.Array &&
                        col0Runs.GetArrayLength() > 0 &&
                        col0Runs[0].TryGetProperty("navigationEndpoint", out navEp))
                    {
                        hasNav = true;
                    }

                    string browseId = hasNav ? (FindFirstPropertyString(navEp, "browseId") ?? string.Empty) : string.Empty;
                    string pageType = hasNav ? ExtractBrowsePageType(navEp) : string.Empty;
                    string watchVideoId = hasNav ? (FindFirstPropertyString(navEp, "videoId") ?? string.Empty) : string.Empty;

                    bool isArtistRow = pageType == "MUSIC_PAGE_TYPE_ARTIST" ||
                                       browseId.StartsWith("UC", StringComparison.Ordinal) ||
                                       browseId.StartsWith("FEmusic_library_privately_owned_artist_detail", StringComparison.Ordinal) ||
                                       (string.IsNullOrWhiteSpace(watchVideoId) && IsArtistSubtitleLabel(subtitle));

                    if (isArtistRow && !string.IsNullOrWhiteSpace(name) && seenKeys.Add(name))
                    {
                        string avatarUrl = ExtractHighResThumbnailUrl(renderer);
                        string audience = ExtractArtistAudienceFromRenderer(renderer) ?? ExtractAudienceSegmentFromSubtitle(subtitle);
                        if (!string.IsNullOrWhiteSpace(avatarUrl))
                        {
                            EnsureArtistAvatarCacheLoaded();
                            if (!_artistAvatarCache.ContainsKey(name))
                            {
                                _artistAvatarCache.Set(name, avatarUrl);
                                cacheUpdated = true;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(audience))
                        {
                            EnsureArtistAudienceCacheLoaded();
                            if (!_artistAudienceCache.ContainsKey(name))
                            {
                                _artistAudienceCache.Set(name, audience);
                                cacheUpdated = true;
                            }
                        }

                        string artistId = browseId.StartsWith("UC", StringComparison.Ordinal)
                            ? $"ytm_sim_{browseId}"
                            : $"ytm_artist_{(!string.IsNullOrWhiteSpace(browseId) ? browseId : name.GetHashCode().ToString())}";

                        results.Add(new ArtistItem
                        {
                            Id = artistId,
                            BrowseId = browseId,
                            Name = name,
                            AvatarUri = avatarUrl,
                            Genre = !string.IsNullOrWhiteSpace(audience) ? audience : "YouTube Music",
                            MonthlyAudienceText = audience,
                            SourceType = MediaSourceType.Online
                        });
                    }
                }

                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (results.Count >= limit)
                {
                    break;
                }

                ExtractSearchArtistsRecursive(prop.Value, results, seenKeys, limit, ref cacheUpdated);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (results.Count >= limit)
                {
                    break;
                }

                ExtractSearchArtistsRecursive(item, results, seenKeys, limit, ref cacheUpdated);
            }
        }
    }

    private static bool IsArtistSubtitleLabel(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return false;
        }

        return subtitle.Contains("artist", StringComparison.OrdinalIgnoreCase) ||
               subtitle.Contains("artista", StringComparison.OrdinalIgnoreCase) ||
               IsAudienceMetricText(subtitle);
    }

    private static string ExtractAudienceSegmentFromSubtitle(string? subtitle)
    {
        if (string.IsNullOrWhiteSpace(subtitle))
        {
            return string.Empty;
        }

        foreach (string part in subtitle.Split(['•', '·', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsAudienceMetricText(part))
            {
                return part;
            }
        }

        return string.Empty;
    }

    private async Task<(List<ArtistItem> Artists, List<ArtistItem> Recommended)> MergeAndEnrichSearchArtistsAsync(
        List<ArtistItem> directArtists,
        List<TrackItem> tracks,
        int limit,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, ArtistItem>(StringComparer.OrdinalIgnoreCase);

        static string PrimaryKey(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? string.Empty
                : name.Split([',', '&', ';', '/'], 2, StringSplitOptions.TrimEntries)[0].Trim();

        foreach (var artist in directArtists)
        {
            string key = PrimaryKey(artist.Name);
            if (string.IsNullOrWhiteSpace(key) || key.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = artist;
            }
            else if (!existing.HasCoverArt && artist.HasCoverArt)
            {
                merged[key] = CloneArtistWithAvatar(existing, artist.AvatarUri,
                    string.IsNullOrWhiteSpace(existing.MonthlyAudienceText) ? artist.MonthlyAudienceText : existing.MonthlyAudienceText);
            }
        }

        if (merged.Count < limit && tracks.Count > 0)
        {
            foreach (var track in tracks)
            {
                if (merged.Count >= limit)
                {
                    break;
                }

                string key = PrimaryKey(track.Artist);
                if (string.IsNullOrWhiteSpace(key) || key.Equals("Unknown Artist", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!merged.ContainsKey(key))
                {
                    merged[key] = new ArtistItem
                    {
                        Id = $"ytm_art_{track.YouTubeVideoId}",
                        Name = key,
                        AvatarUri = string.Empty,
                        Genre = "YouTube Music",
                        SourceType = MediaSourceType.Online
                    };
                }
            }
        }

        var ordered = merged.Values.Take(limit).ToArray();
        var missingIndices = new List<int>();
        for (int i = 0; i < ordered.Length; i++)
        {
            if (!ordered[i].HasCoverArt || string.IsNullOrWhiteSpace(ordered[i].MonthlyAudienceText))
            {
                missingIndices.Add(i);
            }
        }

        if (missingIndices.Count > 0)
        {
            using var gate = new SemaphoreSlim(6, 6);
            var enrichTasks = missingIndices.Select(async idx =>
            {
                var artist = ordered[idx];
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var (officialAvatar, audience) = await GetArtistSummaryAsync(artist.Name, cancellationToken).ConfigureAwait(false);
                    string resolvedAvatar = !string.IsNullOrWhiteSpace(officialAvatar) ? officialAvatar! : artist.AvatarUri;
                    string resolvedAudience = !string.IsNullOrWhiteSpace(audience) ? audience! : artist.MonthlyAudienceText;
                    ordered[idx] = CloneArtistWithAvatar(artist, resolvedAvatar, resolvedAudience);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(enrichTasks).ConfigureAwait(false);
        }

        var recommended = ordered
            .Skip(1)
            .Take(8)
            .Select(a => new ArtistItem
            {
                Id = $"ytm_rec_{a.Id}",
                Name = a.Name,
                AvatarUri = a.AvatarUri,
                Genre = !string.IsNullOrWhiteSpace(a.MonthlyAudienceText) ? a.MonthlyAudienceText : "Featured Artist",
                MonthlyAudienceText = a.MonthlyAudienceText,
                TrackCount = a.TrackCount,
                AlbumCount = a.AlbumCount,
                IsRecommended = true,
                SourceType = a.SourceType
            })
            .ToList();

        return (ordered.ToList(), recommended);
    }

    private static SearchTopResultItem? SelectSearchTopResult(
        string query,
        IReadOnlyList<TrackItem> songs,
        IReadOnlyList<AlbumItem> albums,
        IReadOnlyList<ArtistItem> artists,
        IReadOnlyList<RecommendedPlaylistItem> playlists)
    {
        string normalizedQuery = NormalizeMatchText(query);

        if (artists.Count > 0)
        {
            ArtistItem? exactArtist = null;
            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                for (int i = 0; i < artists.Count; i++)
                {
                    if (string.Equals(NormalizeMatchText(artists[i].Name), normalizedQuery, StringComparison.Ordinal))
                    {
                        exactArtist = artists[i];
                        break;
                    }
                }
            }

            if (exactArtist is not null || (songs.Count == 0 && albums.Count == 0))
            {
                var chosenArtist = exactArtist ?? artists[0];
                return new SearchTopResultItem(
                    Kind: SearchTopResultKind.Artist,
                    Title: chosenArtist.Name,
                    Subtitle: chosenArtist.DisplaySubtitle,
                    ArtworkUri: chosenArtist.AvatarUri,
                    Artist: chosenArtist);
            }
        }

        if (albums.Count > 0)
        {
            var topAlbum = albums[0];
            string normAlbum = NormalizeMatchText(topAlbum.Title);
            if (!string.IsNullOrWhiteSpace(normalizedQuery) &&
                string.Equals(normAlbum, normalizedQuery, StringComparison.Ordinal))
            {
                return new SearchTopResultItem(
                    Kind: SearchTopResultKind.Album,
                    Title: topAlbum.Title,
                    Subtitle: topAlbum.ArtistYearSubtitle,
                    ArtworkUri: topAlbum.CoverArtUri,
                    Album: topAlbum);
            }
        }

        if (songs.Count > 0)
        {
            var topSong = songs[0];
            return new SearchTopResultItem(
                Kind: SearchTopResultKind.Song,
                Title: topSong.Title,
                Subtitle: topSong.ArtistAlbumDotSubtitle,
                ArtworkUri: topSong.CoverArtUri,
                Track: topSong);
        }

        if (artists.Count > 0)
        {
            var topArtist = artists[0];
            return new SearchTopResultItem(
                Kind: SearchTopResultKind.Artist,
                Title: topArtist.Name,
                Subtitle: topArtist.DisplaySubtitle,
                ArtworkUri: topArtist.AvatarUri,
                Artist: topArtist);
        }

        if (albums.Count > 0)
        {
            var topAlbum = albums[0];
            return new SearchTopResultItem(
                Kind: SearchTopResultKind.Album,
                Title: topAlbum.Title,
                Subtitle: topAlbum.ArtistYearSubtitle,
                ArtworkUri: topAlbum.CoverArtUri,
                Album: topAlbum);
        }

        if (playlists.Count > 0)
        {
            var topPlaylist = playlists[0];
            return new SearchTopResultItem(
                Kind: SearchTopResultKind.Playlist,
                Title: topPlaylist.Title,
                Subtitle: topPlaylist.Subtitle,
                ArtworkUri: topPlaylist.ArtworkUri,
                Playlist: topPlaylist);
        }

        return null;
    }

    private static List<TrackItem> ApplyWarmedTrackArtworks(
        IReadOnlyList<TrackItem> tracks,
        IReadOnlyDictionary<string, string> warmedMap)
    {
        if (tracks.Count == 0 || warmedMap.Count == 0)
        {
            return tracks is List<TrackItem> list ? list : tracks.ToList();
        }

        var result = new List<TrackItem>(tracks.Count);
        foreach (var track in tracks)
        {
            if (!string.IsNullOrWhiteSpace(track.CoverArtUri) &&
                warmedMap.TryGetValue(track.CoverArtUri.Trim(), out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(new TrackItem
                {
                    Id = track.Id,
                    Title = track.Title,
                    Artist = track.Artist,
                    Album = track.Album,
                    Duration = track.Duration,
                    CoverArtUri = localUri,
                    AudioSourceUri = track.AudioSourceUri,
                    YouTubeVideoId = track.YouTubeVideoId,
                    SourceType = track.SourceType,
                    IsLiked = track.IsLiked,
                    IsCurrentlyPlaying = track.IsCurrentlyPlaying,
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
                });
            }
            else
            {
                result.Add(track);
            }
        }

        return result;
    }

    private static List<AlbumItem> ApplyWarmedAlbumArtworks(
        IReadOnlyList<AlbumItem> albums,
        IReadOnlyDictionary<string, string> warmedMap)
    {
        if (albums.Count == 0 || warmedMap.Count == 0)
        {
            return albums is List<AlbumItem> list ? list : albums.ToList();
        }

        var result = new List<AlbumItem>(albums.Count);
        foreach (var album in albums)
        {
            if (!string.IsNullOrWhiteSpace(album.CoverArtUri) &&
                warmedMap.TryGetValue(album.CoverArtUri.Trim(), out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(new AlbumItem
                {
                    Id = album.Id,
                    Title = album.Title,
                    Artist = album.Artist,
                    ReleaseYear = album.ReleaseYear,
                    TrackCount = album.TrackCount,
                    CoverArtUri = localUri,
                    SourceType = album.SourceType,
                    BrowseId = album.BrowseId,
                    Subtitle = album.Subtitle
                });
            }
            else
            {
                result.Add(album);
            }
        }

        return result;
    }

    private static List<ArtistItem> ApplyWarmedArtistArtworks(
        IReadOnlyList<ArtistItem> artists,
        IReadOnlyDictionary<string, string> warmedMap)
    {
        if (artists.Count == 0 || warmedMap.Count == 0)
        {
            return artists is List<ArtistItem> list ? list : artists.ToList();
        }

        var result = new List<ArtistItem>(artists.Count);
        foreach (var artist in artists)
        {
            if (!string.IsNullOrWhiteSpace(artist.AvatarUri) &&
                warmedMap.TryGetValue(artist.AvatarUri.Trim(), out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(CloneArtistWithAvatar(artist, localUri, artist.MonthlyAudienceText));
            }
            else
            {
                result.Add(artist);
            }
        }

        return result;
    }

    private static List<RecommendedPlaylistItem> ApplyWarmedPlaylistArtworks(
        IReadOnlyList<RecommendedPlaylistItem> playlists,
        IReadOnlyDictionary<string, string> warmedMap)
    {
        if (playlists.Count == 0 || warmedMap.Count == 0)
        {
            return playlists is List<RecommendedPlaylistItem> list ? list : playlists.ToList();
        }

        var result = new List<RecommendedPlaylistItem>(playlists.Count);
        foreach (var playlist in playlists)
        {
            if (!string.IsNullOrWhiteSpace(playlist.ArtworkUri) &&
                warmedMap.TryGetValue(playlist.ArtworkUri.Trim(), out string? localUri) &&
                !string.IsNullOrWhiteSpace(localUri))
            {
                result.Add(playlist with { ArtworkUri = localUri });
            }
            else
            {
                result.Add(playlist);
            }
        }

        return result;
    }

    private static ArtistItem CloneArtistWithAvatar(ArtistItem artist, string avatarUri, string monthlyAudienceText) => new()
    {
        Id = artist.Id,
        BrowseId = artist.BrowseId,
        Name = artist.Name,
        AvatarUri = avatarUri,
        Genre = !string.IsNullOrWhiteSpace(monthlyAudienceText) ? monthlyAudienceText : artist.Genre,
        MonthlyAudienceText = monthlyAudienceText,
        TrackCount = artist.TrackCount,
        AlbumCount = artist.AlbumCount,
        IsRecommended = artist.IsRecommended,
        SourceType = artist.SourceType
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        (_scriptResolver as IDisposable)?.Dispose();
        _httpClient.Dispose();
        _downloadHttpClient.Dispose();
        _visitorLock.Dispose();
        _accountMetaLock.Dispose();
        foreach (var sem in _stripedLocks)
        {
            sem.Dispose();
        }
    }
}
