using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xperience.Core.Abstractions;
using Xperience.Core.Collections;

namespace Xperience.App.Services;

/// <summary>
/// Production implementation of <see cref="ICanvasArtworkService"/> that integrates Apple Music Canvas
/// (<c>editorialVideo</c> via <c>amp-api.music.apple.com</c> with live WebPlay JWT token scraping,
/// inspired by <c>Convx</c>), plus fallback providers (<c>EchoMusicCanvas</c> and <c>ViviMusicCanvas</c>).
/// Automatically resolves Apple Music HLS master playlists to direct H.264 SDR (<c>avc1</c>) MP4 files
/// and caches them on disk for zero-jitter, hardware-accelerated looping in WinUI 3.
/// </summary>
public sealed partial class AppleMusicCanvasService : ICanvasArtworkService, IDisposable
{
    private const string AppleMusicBaseUrl = "https://amp-api.music.apple.com/";
    private const string AppleMusicBrowseUrl = "https://music.apple.com/us/browse";
    private const string EchoMusicFeedUrl = "https://canvas.echomusic.fun/canvas.json";
    private const string ViviMusicFeedUrl = "https://vivimusicanvas.mkmdevilmi.workers.dev/canvas.json";

    private const string FallbackSeedToken =
        "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6IldlYlBsYXlLaWQifQ" +
        ".eyJpc3MiOiJBTVBXZWJQbGF5IiwiaWF0IjoxNzc0NDU2MzgyLCJleHAiOjE3ODE3" +
        "MTM5ODIsInJvb3RfaHR0cHNfb3JpZ2luIjpbImFwcGxlLmNvbSJdfQ" +
        ".4n8qYF4qa18sL1E0G9A3qX35cD8wQ-IJcS9Bh8ZT8JV_yLBtVq46B-9-2ZS3EvWHuw3yK9BYFYAhAdTaDm38vQ";

    private static readonly string[] PreferredVideoVariants =
    [
        "motionDetailSquare",
        "motionSquareVideo1x1",
        "motionDetailRaw",
        "motionDetailTall",
        "motionTallVideo3x4",
        "motionDetailStatic"
    ];

    private static readonly string[] PreferredArtistVideoVariants =
    [
        "motionArtistFullscreen16x9",
        "motionArtistWide16x9",
        "wideVideoArtwork",
        "motionDetailWide",
        "motionDetailRaw",
        "motionArtistSquare1x1",
        "motionDetailSquare",
        "motionSquareVideo1x1",
        "motionDetailTall",
        "motionTallVideo3x4"
    ];

    private static readonly string[] WordmarkArtworkKeys =
    [
        "musicContentWhiteLogoTrimmed",
        "musicContentColorLogoTrimmed",
        "artistLogo",
        "centeredLogo",
        "brandLogo",
        "editorialLogo"
    ];

    private static readonly string[] ArtistHeroArtworkKeys =
    [
        "bannerUber",
        "artistBackground",
        "centeredBanner",
        "superHeroWide",
        "storeFlowcase",
        "hero",
        "subscriptionHero"
    ];

    private static readonly string[] BlacklistedAlbumKeywords =
    [
        "essentials",
        "greatest hits",
        "best of",
        "now that's what i call",
        "anthology",
        "collection",
        "complete",
        "hits",
        "playlist",
        "compilation",
        "dj mix",
        "karaoke",
        "tribute",
        "instrumental"
    ];

    private sealed record CachedArtistAssetsEntry(
        ArtistAssetsResult? Value,
        long UpdatedAtUnixMs,
        long ExpiresAtUnixMs,
        long StaleUntilUnixMs);

    private sealed record CachedArtistIdEntry(
        string Id,
        string Name,
        string Storefront,
        long ExpiresAtUnixMs);

    private sealed record CachedAlbumCanvasEntry(
        string VideoUri,
        string ProviderName,
        string? PreviewFrameUri,
        string? CanonicalCanvasKey,
        string? AppleAlbumId,
        long ExpiresAtUnixMs);

    private sealed record PersistedTokenData(string Token, long ExpiresAtEpochSec);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly BoundedLruCache<string, CanvasArtworkResult?> _memoryCache = new(maxCapacity: 80, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, CachedAlbumCanvasEntry> _albumCanvasCache = new(maxCapacity: 80, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<CanvasArtworkResult?>> _inFlightCanvasLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlightVideoDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, string?> _wordmarkCache = new(maxCapacity: 120, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, string?> _artistHeaderVideoCache = new(maxCapacity: 60, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, CachedArtistAssetsEntry> _artistAssetsCache = new(maxCapacity: 80, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly BoundedLruCache<string, CachedArtistIdEntry> _artistIdCache = new(maxCapacity: 200, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ArtistAssetsResult?>> _inFlightArtistAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistedCachesLoadLock = new();
    private volatile bool _persistedCachesLoaded;
    private int _disposed;
    private readonly string _diskCacheDir;
    private readonly string _wordmarkCacheFilePath;
    private readonly string _artistAssetsCacheFilePath;
    private readonly string _artistIdsCacheFilePath;
    private readonly string _albumCanvasCacheFilePath;
    private readonly string _tokenCacheFilePath;

    private string? _cachedBearerToken;
    private long _tokenExpiresAtEpochSec;
    private DateTimeOffset _tokenFetchedAt = DateTimeOffset.MinValue;

    private List<FallbackCanvasEntry>? _echoEntries;
    private DateTimeOffset _echoFetchedAt = DateTimeOffset.MinValue;
    private List<FallbackCanvasEntry>? _viviEntries;
    private DateTimeOffset _viviFetchedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppleMusicCanvasService"/> class.
    /// </summary>
    public AppleMusicCanvasService()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(18)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer",
            "CanvasCache");
        _wordmarkCacheFilePath = Path.Combine(_diskCacheDir, "artist_wordmarks_cache.json");
        _artistAssetsCacheFilePath = Path.Combine(_diskCacheDir, "artist_assets_v2.json");
        _artistIdsCacheFilePath = Path.Combine(_diskCacheDir, "artist_ids_v2.json");
        _albumCanvasCacheFilePath = Path.Combine(_diskCacheDir, "album_canvas_v2.json");
        _tokenCacheFilePath = Path.Combine(_diskCacheDir, "amp_token.json");

        try
        {
            Directory.CreateDirectory(_diskCacheDir);
        }
        catch
        {
            // Ignore directory creation errors on restricted filesystems
        }
    }

    private void EnsurePersistedCachesLoaded()
    {
        if (_persistedCachesLoaded)
        {
            return;
        }

        lock (_persistedCachesLoadLock)
        {
            if (_persistedCachesLoaded)
            {
                return;
            }

            try
            {
                LoadPersistedCachesSafe();
            }
            catch
            {
                // Ignore transient IO failures on lazy cache load
            }

            _persistedCachesLoaded = true;
        }
    }

    /// <inheritdoc />
    public CanvasArtworkResult? PeekCanvasArtwork(
        string title,
        string artist,
        string? album = null)
    {
        EnsurePersistedCachesLoaded();

        string cleanTitle = CleanSongTitle(title);
        string cleanArtist = CleanArtistName(artist);
        string cleanAlbum = CleanAlbumName(album);
        bool hasRealAlbum = IsSpecificRealAlbumName(album);

        if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(cleanAlbum))
        {
            return null;
        }

        string trackCacheKey = $"{Normalize(cleanTitle)}|{Normalize(cleanArtist)}|{Normalize(cleanAlbum)}|{hasRealAlbum}";
        if (_memoryCache.TryGet(trackCacheKey, out var memResult) && memResult is not null)
        {
            if (IsReadyLocalFlatMp4Path(memResult.VideoUri))
            {
                return memResult;
            }
        }

        if (hasRealAlbum)
        {
            string? albumCacheKey = BuildCanonicalAlbumCacheKey(cleanArtist, album);
            if (!string.IsNullOrWhiteSpace(albumCacheKey) &&
                _albumCanvasCache.TryGet(albumCacheKey, out var entry) &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < entry.ExpiresAtUnixMs)
            {
                string? readyPath = ResolveReadyLocalFlatMp4ForPeek(entry.VideoUri);
                if (!string.IsNullOrWhiteSpace(readyPath))
                {
                    var result = new CanvasArtworkResult(
                        VideoUri: readyPath!,
                        ProviderName: entry.ProviderName,
                        PreviewFrameUri: entry.PreviewFrameUri,
                        CanonicalCanvasKey: entry.CanonicalCanvasKey,
                        AppleAlbumId: entry.AppleAlbumId);
                    _memoryCache.Set(trackCacheKey, result);
                    return result;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> EnsureLocalLoopableVideoAsync(
        string videoUriOrPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoUriOrPath))
        {
            return null;
        }

        if (File.Exists(videoUriOrPath))
        {
            return await Task.Run(
                () => Mp4CanvasNormalizer.EnsureNormalizedLocalVideoFile(videoUriOrPath, _diskCacheDir),
                cancellationToken).ConfigureAwait(false);
        }

        if (videoUriOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            string directUrl = videoUriOrPath;
            if (directUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                string? resolvedMp4 = await ResolveHlsMasterPlaylistToAvcMp4Async(directUrl, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolvedMp4))
                {
                    directUrl = resolvedMp4!;
                }
            }

            string? cachedLocal = await DownloadToCacheIfPossibleAsync(directUrl, cancellationToken).ConfigureAwait(false);
            return cachedLocal ?? directUrl;
        }

        return videoUriOrPath;
    }

    /// <inheritdoc />
    public async Task<CanvasArtworkResult?> GetCanvasArtworkAsync(
        string title,
        string artist,
        string? album = null,
        CancellationToken cancellationToken = default)
    {
        EnsurePersistedCachesLoaded();

        string cleanTitle = CleanSongTitle(title);
        string cleanArtist = CleanArtistName(artist);
        string cleanAlbum = CleanAlbumName(album);
        bool hasRealAlbum = IsSpecificRealAlbumName(album);

        if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(cleanAlbum))
        {
            return null;
        }

        string cacheKey = $"{Normalize(cleanTitle)}|{Normalize(cleanArtist)}|{Normalize(cleanAlbum)}|{hasRealAlbum}";
        string? albumCacheKey = hasRealAlbum ? BuildCanonicalAlbumCacheKey(cleanArtist, album) : null;

        // 1. Instant hit from in-memory track cache (upgrading legacy fMP4 if needed)
        if (_memoryCache.TryGet(cacheKey, out var cachedResult) && cachedResult is not null)
        {
            string? ensured = await EnsureCanvasResultLocalAsync(cachedResult, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ensured))
            {
                var ready = ensured != cachedResult.VideoUri
                    ? cachedResult with { VideoUri = ensured }
                    : cachedResult;
                _memoryCache.Set(cacheKey, ready);
                return ready;
            }

            _memoryCache.Remove(cacheKey);
        }

        // 2. Instant hit from album-level cache (same album across different tracks & across app restarts -> 0 API calls!)
        if (!string.IsNullOrWhiteSpace(albumCacheKey) &&
            _albumCanvasCache.TryGet(albumCacheKey, out var albumEntry) &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < albumEntry.ExpiresAtUnixMs)
        {
            var candidate = new CanvasArtworkResult(
                VideoUri: albumEntry.VideoUri,
                ProviderName: albumEntry.ProviderName,
                PreviewFrameUri: albumEntry.PreviewFrameUri,
                CanonicalCanvasKey: albumEntry.CanonicalCanvasKey,
                AppleAlbumId: albumEntry.AppleAlbumId);
            string? ensured = await EnsureCanvasResultLocalAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ensured))
            {
                var ready = ensured != candidate.VideoUri
                    ? candidate with { VideoUri = ensured }
                    : candidate;
                _memoryCache.Set(cacheKey, ready);
                if (!string.Equals(albumEntry.VideoUri, ready.VideoUri, StringComparison.OrdinalIgnoreCase))
                {
                    _albumCanvasCache.Set(albumCacheKey, albumEntry with { VideoUri = ready.VideoUri });
                    PersistAlbumCanvasCacheSafe();
                }

                return ready;
            }
        }

        // 3. Deduplicate concurrent lookups for the exact same album / track (e.g. Album Detail + Now Playing at once)
        string lookupDedupeKey = !string.IsNullOrWhiteSpace(albumCacheKey) ? $"alb:{albumCacheKey}" : $"trk:{cacheKey}";
        var inFlight = _inFlightCanvasLookups.GetOrAdd(
            lookupDedupeKey,
            _ => ResolveCanvasArtworkCoreAsync(cleanTitle, cleanArtist, album, cleanAlbum, hasRealAlbum, cacheKey, albumCacheKey));

        try
        {
            var result = await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                _memoryCache.Set(cacheKey, result);
            }

            return result;
        }
        finally
        {
            if (inFlight.IsCompleted)
            {
                _inFlightCanvasLookups.TryRemove(new KeyValuePair<string, Task<CanvasArtworkResult?>>(lookupDedupeKey, inFlight));
            }
        }
    }

    private async Task<CanvasArtworkResult?> ResolveCanvasArtworkCoreAsync(
        string cleanTitle,
        string cleanArtist,
        string? rawAlbum,
        string cleanAlbum,
        bool hasRealAlbum,
        string cacheKey,
        string? albumCacheKey)
    {
        try
        {
            // 1. Primary Provider: Apple Music Editorial Video (Canvas) with strict album fidelity when album is known
            var appleResult = await TryGetAppleMusicCanvasAsync(
                cleanTitle,
                cleanArtist,
                hasRealAlbum ? (rawAlbum?.Trim() ?? cleanAlbum) : string.Empty,
                hasRealAlbum,
                CancellationToken.None).ConfigureAwait(false);
            if (appleResult is not null)
            {
                StoreResolvedCanvasInCaches(cacheKey, albumCacheKey, appleResult);
                return appleResult;
            }

            // When a specific real album/single title is known, do NOT fall back to album-less track feeds
            // (EchoMusic / ViviMusic lack album metadata and would return a studio album's Canvas for a standalone Single).
            if (hasRealAlbum)
            {
                return null;
            }

            // 2. Fallback Provider A: EchoMusic Canvas (only when album is unspecified/generic)
            var echoResult = await TryGetFallbackFeedCanvasAsync(
                EchoMusicFeedUrl,
                "EchoMusic Canvas",
                cleanTitle,
                cleanArtist,
                isEcho: true,
                CancellationToken.None).ConfigureAwait(false);
            if (echoResult is not null)
            {
                StoreResolvedCanvasInCaches(cacheKey, albumCacheKey, echoResult);
                return echoResult;
            }

            // 3. Fallback Provider B: ViviMusic Canvas (only when album is unspecified/generic)
            var viviResult = await TryGetFallbackFeedCanvasAsync(
                ViviMusicFeedUrl,
                "ViviMusic Canvas",
                cleanTitle,
                cleanArtist,
                isEcho: false,
                CancellationToken.None).ConfigureAwait(false);
            if (viviResult is not null)
            {
                StoreResolvedCanvasInCaches(cacheKey, albumCacheKey, viviResult);
                return viviResult;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded
        }
        catch
        {
            // Return null gracefully on transient network failures
        }

        return null;
    }

    private void StoreResolvedCanvasInCaches(string cacheKey, string? albumCacheKey, CanvasArtworkResult result)
    {
        _memoryCache.Set(cacheKey, result);
        if (!string.IsNullOrWhiteSpace(albumCacheKey))
        {
            long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (14L * 24 * 60 * 60 * 1000);
            _albumCanvasCache.Set(albumCacheKey, new CachedAlbumCanvasEntry(
                VideoUri: result.VideoUri,
                ProviderName: result.ProviderName,
                PreviewFrameUri: result.PreviewFrameUri,
                CanonicalCanvasKey: result.CanonicalCanvasKey,
                AppleAlbumId: result.AppleAlbumId,
                ExpiresAtUnixMs: expiresAt));
            PersistAlbumCanvasCacheSafe();
        }
    }

    private async Task<string?> EnsureCanvasResultLocalAsync(CanvasArtworkResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.VideoUri))
        {
            return null;
        }

        if (File.Exists(result.VideoUri))
        {
            if (IsReadyLocalFlatMp4Path(result.VideoUri))
            {
                return result.VideoUri;
            }

            return await Task.Run(
                () => Mp4CanvasNormalizer.EnsureNormalizedLocalVideoFile(result.VideoUri, _diskCacheDir),
                cancellationToken).ConfigureAwait(false);
        }

        if (result.VideoUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureLocalLoopableVideoAsync(result.VideoUri, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private string? BuildCanonicalAlbumCacheKey(string cleanArtist, string? rawAlbum)
    {
        string baseAlbum = ExtractBaseAlbumTitle(rawAlbum);
        if (string.IsNullOrWhiteSpace(baseAlbum))
        {
            return null;
        }

        string normArtist = Normalize(cleanArtist);
        return $"{normArtist}|{baseAlbum}";
    }

    private static bool IsReadyLocalFlatMp4Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 16_384)
            {
                return false;
            }

            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("canvas_v2_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !Mp4CanvasNormalizer.IsFragmentedMp4(path);
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveReadyLocalFlatMp4ForPeek(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
        {
            return null;
        }

        if (IsReadyLocalFlatMp4Path(uriOrPath))
        {
            return uriOrPath;
        }

        if (uriOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return GetExistingNormalizedMp4Path(uriOrPath);
        }

        return null;
    }

    private async Task<CanvasArtworkResult?> TryGetAppleMusicCanvasAsync(
        string song,
        string artist,
        string rawTargetAlbum,
        bool requireStrictAlbumMatch,
        CancellationToken cancellationToken)
    {
        string? token = await EnsureAppleMusicTokenAsync(forceRefresh: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        foreach (string storefront in new[] { "us", "gb" })
        {
            var result = await SearchStorefrontAsync(
                storefront,
                song,
                artist,
                rawTargetAlbum,
                requireStrictAlbumMatch,
                token!,
                cancellationToken);
            if (result.Status == SearchStatus.Unauthorized)
            {
                token = await EnsureAppleMusicTokenAsync(forceRefresh: true, cancellationToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                result = await SearchStorefrontAsync(
                    storefront,
                    song,
                    artist,
                    rawTargetAlbum,
                    requireStrictAlbumMatch,
                    token!,
                    cancellationToken);
            }

            if (result.Canvas is not null)
            {
                return result.Canvas;
            }
        }

        return null;
    }

    private enum SearchStatus
    {
        Success,
        NotFound,
        Unauthorized,
        Error
    }

    private async Task<(SearchStatus Status, CanvasArtworkResult? Canvas)> SearchStorefrontAsync(
        string storefront,
        string song,
        string artist,
        string rawTargetAlbum,
        bool requireStrictAlbumMatch,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        string primaryTerm = requireStrictAlbumMatch && !string.IsNullOrWhiteSpace(rawTargetAlbum)
            ? CleanAlbumName(rawTargetAlbum)
            : song;
        if (string.IsNullOrWhiteSpace(primaryTerm))
        {
            primaryTerm = song;
        }

        string query = string.IsNullOrWhiteSpace(artist) ? primaryTerm : $"{primaryTerm} {artist}".Trim();
        string encodedQuery = Uri.EscapeDataString(query);
        string searchUrl =
            $"{AppleMusicBaseUrl}v1/catalog/{storefront}/search?term={encodedQuery}&types=songs,albums&limit=12&extend=editorialVideo&include=albums";

        using var request = CreateAppleApiRequest(searchUrl, bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return (SearchStatus.Unauthorized, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (SearchStatus.Error, null);
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var resultsElement))
        {
            return (SearchStatus.NotFound, null);
        }

        string normSong = Normalize(song);
        string normArtist = Normalize(artist);
        string normAlbum = Normalize(rawTargetAlbum);

        // 1. Check albums returned directly in results.albums.data (which already have extend=editorialVideo)
        var matchedDirectAlbums = new List<((string RawVideoUrl, string? PreviewUrl) Video, int Score, string? AlbumId)>();
        if (resultsElement.TryGetProperty("albums", out var albumsObj) &&
            albumsObj.TryGetProperty("data", out var albumsData) &&
            albumsData.ValueKind == JsonValueKind.Array)
        {
            foreach (var albItem in albumsData.EnumerateArray())
            {
                if (!albItem.TryGetProperty("attributes", out var attr))
                {
                    continue;
                }

                string rawAlbArtist = GetString(attr, "artistName");
                string rawAlbName = GetString(attr, "name");
                string albArtist = Normalize(rawAlbArtist);
                string albName = Normalize(rawAlbName);

                bool artistMatches = string.IsNullOrWhiteSpace(normArtist) ||
                                     albArtist.Contains(normArtist, StringComparison.OrdinalIgnoreCase) ||
                                     normArtist.Contains(albArtist, StringComparison.OrdinalIgnoreCase) ||
                                     HasSharedArtistToken(albArtist, normArtist);

                if (!artistMatches)
                {
                    continue;
                }

                int albumMatchScore = 0;
                if (requireStrictAlbumMatch)
                {
                    albumMatchScore = ComputeFaithfulAlbumMatchScore(rawTargetAlbum, rawAlbName);
                    if (albumMatchScore <= 0)
                    {
                        // Strictly reject any album whose title does not faithfully match the requested album
                        continue;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(normAlbum))
                    {
                        albumMatchScore = ComputeFaithfulAlbumMatchScore(rawTargetAlbum, rawAlbName);
                    }
                    else if (!string.IsNullOrWhiteSpace(normSong) &&
                             string.Equals(ExtractBaseAlbumTitle(rawAlbName), normSong, StringComparison.OrdinalIgnoreCase))
                    {
                        albumMatchScore = 60;
                    }

                    if (albumMatchScore <= 0)
                    {
                        continue;
                    }
                }

                var directAlbumVideo = ExtractEditorialVideoInfo(attr);
                if (directAlbumVideo is not null)
                {
                    string directAlbumId = GetString(albItem, "id");
                    matchedDirectAlbums.Add((directAlbumVideo.Value, albumMatchScore, string.IsNullOrWhiteSpace(directAlbumId) ? null : directAlbumId));
                }
            }
        }

        if (matchedDirectAlbums.Count > 0)
        {
            var bestAlbumMatch = matchedDirectAlbums.OrderByDescending(a => a.Score).First();
            var resolved = await ResolveAndCacheAppleVideoAsync(
                bestAlbumMatch.Video.RawVideoUrl,
                bestAlbumMatch.Video.PreviewUrl,
                bestAlbumMatch.AlbumId,
                cancellationToken);
            if (resolved is not null)
            {
                return (SearchStatus.Success, resolved);
            }
        }

        // 2. Check songs in results.songs.data (enforcing strict album title match when requireStrictAlbumMatch is true)
        var candidateSongs = new List<(JsonElement Song, int Score, string? AlbumId)>();

        if (resultsElement.TryGetProperty("songs", out var songsObj) &&
            songsObj.TryGetProperty("data", out var songsData) &&
            songsData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in songsData.EnumerateArray())
            {
                if (!item.TryGetProperty("attributes", out var attr))
                {
                    continue;
                }

                string itemName = GetString(attr, "name");
                string itemArtist = GetString(attr, "artistName");
                string itemAlbum = GetString(attr, "albumName");

                string nName = Normalize(itemName);
                string nArtist = Normalize(itemArtist);
                string nAlbum = Normalize(itemAlbum);

                bool artistMatches = string.IsNullOrWhiteSpace(normArtist) ||
                                     nArtist.Contains(normArtist, StringComparison.OrdinalIgnoreCase) ||
                                     normArtist.Contains(nArtist, StringComparison.OrdinalIgnoreCase) ||
                                     HasSharedArtistToken(nArtist, normArtist);
                if (!artistMatches)
                {
                    continue;
                }

                int albumMatchScore = 0;
                if (requireStrictAlbumMatch)
                {
                    albumMatchScore = ComputeFaithfulAlbumMatchScore(rawTargetAlbum, itemAlbum);
                    if (albumMatchScore <= 0)
                    {
                        // Never use a song from a different album (e.g. reject "WOR$T GIRL IN AMERICA" when inside single "DANCE...")
                        continue;
                    }
                }
                else
                {
                    bool songMatches = !string.IsNullOrWhiteSpace(normSong) &&
                                       (nName.Contains(normSong, StringComparison.OrdinalIgnoreCase) ||
                                        normSong.Contains(nName, StringComparison.OrdinalIgnoreCase));
                    if (!songMatches)
                    {
                        continue;
                    }
                }

                int score = albumMatchScore;
                if (!string.IsNullOrWhiteSpace(normSong))
                {
                    if (string.Equals(nName, normSong, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 50;
                    }
                    else if (nName.StartsWith(normSong, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 25;
                    }
                }

                if (!string.IsNullOrWhiteSpace(normArtist) && string.Equals(nArtist, normArtist, StringComparison.OrdinalIgnoreCase))
                {
                    score += 35;
                }

                bool isBlacklisted = BlacklistedAlbumKeywords.Any(k => nAlbum.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (isBlacklisted)
                {
                    score -= 80;
                }
                else
                {
                    score += 20;
                }

                // Fast path: if the matching song object itself directly has editorialVideo
                string? songAlbumId = ExtractAlbumIdFromSong(item, attr);
                var directVideo = ExtractEditorialVideoInfo(attr);
                if (directVideo is not null)
                {
                    var resolved = await ResolveAndCacheAppleVideoAsync(directVideo.Value.RawVideoUrl, directVideo.Value.PreviewUrl, songAlbumId, cancellationToken);
                    if (resolved is not null)
                    {
                        return (SearchStatus.Success, resolved);
                    }
                }

                candidateSongs.Add((item.Clone(), score, songAlbumId));
            }
        }

        // 3. Inspect top distinct candidate Album IDs from matched songs (verifying album title fidelity)
        var checkedAlbumIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidateSongs.OrderByDescending(c => c.Score))
        {
            if (string.IsNullOrWhiteSpace(candidate.AlbumId) || !checkedAlbumIds.Add(candidate.AlbumId!))
            {
                continue;
            }

            if (checkedAlbumIds.Count > 4)
            {
                break;
            }

            var albumVideo = await FetchAlbumEditorialVideoAsync(
                storefront,
                candidate.AlbumId!,
                requireStrictAlbumMatch ? rawTargetAlbum : null,
                bearerToken,
                cancellationToken);
            if (albumVideo is not null)
            {
                var resolved = await ResolveAndCacheAppleVideoAsync(
                    albumVideo.Value.RawVideoUrl,
                    albumVideo.Value.PreviewUrl,
                    candidate.AlbumId,
                    cancellationToken);
                if (resolved is not null)
                {
                    return (SearchStatus.Success, resolved);
                }
            }
        }

        return (SearchStatus.NotFound, null);
    }

    private async Task<(string RawVideoUrl, string? PreviewUrl)?> FetchAlbumEditorialVideoAsync(
        string storefront,
        string albumId,
        string? requiredAlbumTitle,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        string url = $"{AppleMusicBaseUrl}v1/catalog/{storefront}/albums/{albumId}?extend=editorialVideo";
        using var request = CreateAppleApiRequest(url, bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataArr) ||
            dataArr.ValueKind != JsonValueKind.Array ||
            dataArr.GetArrayLength() == 0)
        {
            return null;
        }

        var albumObj = dataArr[0];
        if (albumObj.TryGetProperty("attributes", out var attr))
        {
            if (!string.IsNullOrWhiteSpace(requiredAlbumTitle))
            {
                string fetchedAlbumName = GetString(attr, "name");
                if (ComputeFaithfulAlbumMatchScore(requiredAlbumTitle, fetchedAlbumName) <= 0)
                {
                    return null;
                }
            }

            return ExtractEditorialVideoInfo(attr);
        }

        return null;
    }

    private static (string RawVideoUrl, string? PreviewUrl)? ExtractEditorialVideoInfo(JsonElement attributes)
    {
        var full = ExtractEditorialVideoInfoWithColor(attributes, PreferredVideoVariants);
        return full is null ? null : (full.Value.RawVideoUrl, full.Value.PreviewUrl);
    }

    private static (string RawVideoUrl, string? PreviewUrl, string? BgColorHex)? ExtractEditorialVideoInfoWithColor(
        JsonElement attributes,
        IReadOnlyList<string> preferredKeys)
    {
        if (!attributes.TryGetProperty("editorialVideo", out var editorialVideo) ||
            editorialVideo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var orderedKeys = new List<string>(preferredKeys.Count + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in preferredKeys)
        {
            if (seen.Add(key))
            {
                orderedKeys.Add(key);
            }
        }

        foreach (var prop in editorialVideo.EnumerateObject())
        {
            if (!prop.Name.Contains("Static", StringComparison.OrdinalIgnoreCase) && seen.Add(prop.Name))
            {
                orderedKeys.Add(prop.Name);
            }
        }

        foreach (string key in orderedKeys)
        {
            if (!editorialVideo.TryGetProperty(key, out var variant) || variant.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? videoUrl = TryGetFirstNonEmptyString(variant, "video", "videoUrl", "hlsUrl", "url");
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                string? previewUrl = null;
                string? bgHex = null;
                if (variant.TryGetProperty("previewFrame", out var previewObj) &&
                    previewObj.ValueKind == JsonValueKind.Object)
                {
                    string rawPreview = GetString(previewObj, "url");
                    if (!string.IsNullOrWhiteSpace(rawPreview))
                    {
                        previewUrl = rawPreview
                            .Replace("{w}", "600", StringComparison.Ordinal)
                            .Replace("{h}", "600", StringComparison.Ordinal)
                            .Replace("{c}", "bb", StringComparison.Ordinal)
                            .Replace("{f}", "jpg", StringComparison.Ordinal);
                    }

                    string rawBg = GetString(previewObj, "bgColor");
                    if (!string.IsNullOrWhiteSpace(rawBg))
                    {
                        bgHex = rawBg;
                    }
                }

                return (videoUrl!, previewUrl, bgHex);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves an Apple Music <c>.m3u8</c> HLS master playlist into a direct H.264 SDR (<c>avc1</c>) <c>.mp4</c> file,
    /// defragments it into a flat fast-start MP4 via <see cref="Mp4CanvasNormalizer"/>, and caches it locally on disk
    /// so WinUI 3 <c>MediaPlayer</c> plays from <c>t=0.0s</c> and loops gaplessly with zero network traffic.
    /// </summary>
    private async Task<CanvasArtworkResult?> ResolveAndCacheAppleVideoAsync(
        string rawVideoUrl,
        string? previewUrl,
        string? appleAlbumId,
        CancellationToken cancellationToken)
    {
        string directMp4Url = rawVideoUrl;

        if (rawVideoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            string? resolvedMp4 = await ResolveHlsMasterPlaylistToAvcMp4Async(rawVideoUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedMp4))
            {
                directMp4Url = resolvedMp4!;
            }
        }

        string? localFilePath = await DownloadToCacheIfPossibleAsync(directMp4Url, cancellationToken);
        string finalPlayableUri = localFilePath ?? directMp4Url;
        string canonicalKey = ComputeCanonicalUrlHash(directMp4Url);

        return new CanvasArtworkResult(
            VideoUri: finalPlayableUri,
            ProviderName: "Apple Music Canvas",
            PreviewFrameUri: previewUrl,
            CanonicalCanvasKey: canonicalKey,
            AppleAlbumId: appleAlbumId);
    }

    private async Task<string?> ResolveHlsMasterPlaylistToAvcMp4Async(
        string masterM3u8Url,
        CancellationToken cancellationToken,
        int preferredTargetWidth = 768,
        int preferredBandwidth = 2_200_000)
    {
        try
        {
            string masterContent = await _httpClient.GetStringAsync(masterM3u8Url, cancellationToken);
            string[] lines = masterContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            var avcVariants = new List<(int Resolution, int Bandwidth, string SubPlaylistUrl)>();
            Uri masterUri = new(masterM3u8Url);

            for (int i = 0; i < lines.Length - 1; i++)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Select standard H.264 SDR (avc1) streams so Windows Media Foundation decodes natively without HEVC extension
                if (!line.Contains("avc1", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string nextLine = lines[i + 1].Trim();
                if (nextLine.StartsWith('#'))
                {
                    continue;
                }

                int width = 486;
                var resMatch = ResolutionRegex().Match(line);
                if (resMatch.Success && int.TryParse(resMatch.Groups[1].Value, out int parsedW))
                {
                    width = parsedW;
                }

                int bandwidth = 1_000_000;
                var bwMatch = BandwidthRegex().Match(line);
                if (bwMatch.Success && int.TryParse(bwMatch.Groups[1].Value, out int parsedBw))
                {
                    bandwidth = parsedBw;
                }

                Uri subUri = new(masterUri, nextLine);
                avcVariants.Add((width, bandwidth, subUri.AbsoluteUri));
            }

            if (avcVariants.Count == 0)
            {
                return null;
            }

            var chosen = avcVariants
                .OrderBy(v => Math.Abs(v.Resolution - preferredTargetWidth))
                .ThenBy(v => Math.Abs(v.Bandwidth - preferredBandwidth))
                .First();

            string subM3u8Url = chosen.SubPlaylistUrl;
            string subContent = await _httpClient.GetStringAsync(subM3u8Url, cancellationToken);
            Uri subBaseUri = new(subM3u8Url);

            var mapMatch = ExtXMapUriRegex().Match(subContent);
            if (mapMatch.Success)
            {
                string relativeMp4 = mapMatch.Groups[1].Value;
                return new Uri(subBaseUri, relativeMp4).AbsoluteUri;
            }

            if (subM3u8Url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return subM3u8Url[..^5] + "-.mp4";
            }
        }
        catch
        {
            // Fall back to original URL if HLS manifest parsing fails
        }

        return null;
    }

    private static string ComputeCanonicalUrlHash(string videoUrl)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(videoUrl));
        return Convert.ToHexString(hashBytes)[..24].ToLowerInvariant();
    }

    private async Task<string?> DownloadToCacheIfPossibleAsync(string videoUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoUrl) ||
            videoUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string hashHex = ComputeCanonicalUrlHash(videoUrl);
        string v2FlatPath = Path.Combine(_diskCacheDir, $"canvas_v2_{hashHex}.mp4");

        // Fast path 1: Already normalized flat MP4 on disk -> 0ms, zero network
        if (File.Exists(v2FlatPath))
        {
            try
            {
                if (new FileInfo(v2FlatPath).Length > 16_384)
                {
                    return v2FlatPath;
                }
            }
            catch
            {
            }
        }

        // Fast path 2: Deduplicate concurrent downloads/migrations for the exact same video URL
        var inFlight = _inFlightVideoDownloads.GetOrAdd(
            hashHex,
            _ => DownloadAndNormalizeMp4CoreAsync(videoUrl, hashHex, v2FlatPath));

        try
        {
            return await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (inFlight.IsCompleted)
            {
                _inFlightVideoDownloads.TryRemove(new KeyValuePair<string, Task<string?>>(hashHex, inFlight));
            }
        }
    }

    private async Task<string?> DownloadAndNormalizeMp4CoreAsync(string videoUrl, string hashHex, string v2FlatPath)
    {
        try
        {
            Directory.CreateDirectory(_diskCacheDir);

            // Zero-network migration from pre-existing v1 cached fMP4 file if present
            string legacyPath = Path.Combine(_diskCacheDir, $"canvas_{hashHex}.mp4");
            if (File.Exists(legacyPath) && new FileInfo(legacyPath).Length > 16_384)
            {
                if (Mp4CanvasNormalizer.IsFragmentedMp4(legacyPath))
                {
                    if (Mp4CanvasNormalizer.TryFlattenFragmentedMp4(legacyPath, v2FlatPath) &&
                        File.Exists(v2FlatPath) &&
                        new FileInfo(v2FlatPath).Length > 16_384)
                    {
                        return v2FlatPath;
                    }
                }
                else
                {
                    File.Copy(legacyPath, v2FlatPath, overwrite: true);
                    return v2FlatPath;
                }
            }

            string tempRawPath = v2FlatPath + $".{Guid.NewGuid():N}.raw.tmp";
            try
            {
                using var response = await _httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                // Reject non-video responses (e.g. HTML error page or playlist text) when URL lacks .mp4
                string? contentType = response.Content.Headers.ContentType?.MediaType;
                if (!videoUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(contentType) &&
                    !contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase) &&
                    !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) &&
                    !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                await using (var fs = new FileStream(tempRawPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    await response.Content.CopyToAsync(fs, CancellationToken.None).ConfigureAwait(false);
                }

                if (!File.Exists(tempRawPath) || new FileInfo(tempRawPath).Length <= 16_384)
                {
                    return null;
                }

                // Verify ISO-BMFF MP4 signature ('ftyp' or 'moov' box type at byte offset 4)
                if (!HasValidMp4Header(tempRawPath))
                {
                    return null;
                }

                if (Mp4CanvasNormalizer.IsFragmentedMp4(tempRawPath))
                {
                    if (Mp4CanvasNormalizer.TryFlattenFragmentedMp4(tempRawPath, v2FlatPath) &&
                        File.Exists(v2FlatPath) &&
                        new FileInfo(v2FlatPath).Length > 16_384)
                    {
                        return v2FlatPath;
                    }
                }

                // Already flat MP4 (e.g. EchoMusic/ViviMusic) or fallback if flattening skipped
                NeutralizeMp4EditListsInPlace(tempRawPath);
                if (File.Exists(v2FlatPath))
                {
                    return v2FlatPath;
                }

                File.Move(tempRawPath, v2FlatPath, overwrite: true);
                return v2FlatPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempRawPath))
                    {
                        File.Delete(tempRawPath);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool HasValidMp4Header(string filePath)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(header) < 12)
            {
                return false;
            }

            return (header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p') ||
                   (header[4] == (byte)'m' && header[5] == (byte)'o' && header[6] == (byte)'o' && header[7] == (byte)'v') ||
                   (header[4] == (byte)'m' && header[5] == (byte)'d' && header[6] == (byte)'a' && header[7] == (byte)'t');
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Neutralizes MP4 <c>edts</c> (Edit List) atoms by renaming their 4-byte box type to <c>free</c>
    /// so Windows Media Foundation loops Apple Music fMP4/MP4 clips seamlessly with zero end-of-stream pause.
    /// </summary>
    private static void NeutralizeMp4EditListsInPlace(string filePath)
    {
        const int maxScanBytes = 524_288;
        byte[]? rented = null;
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            int scanLen = (int)Math.Min(fs.Length, maxScanBytes);
            if (scanLen < 12)
            {
                return;
            }

            rented = ArrayPool<byte>.Shared.Rent(scanLen);
            int totalRead = 0;
            while (totalRead < scanLen)
            {
                int n = fs.Read(rented, totalRead, scanLen - totalRead);
                if (n <= 0)
                {
                    break;
                }

                totalRead += n;
            }

            bool modified = false;
            int limit = totalRead - 8;
            for (int i = 4; i < limit; i++)
            {
                if (rented[i] == (byte)'e' &&
                    rented[i + 1] == (byte)'d' &&
                    rented[i + 2] == (byte)'t' &&
                    rented[i + 3] == (byte)'s')
                {
                    uint boxSize = (uint)((rented[i - 4] << 24) | (rented[i - 3] << 16) | (rented[i - 2] << 8) | rented[i - 1]);
                    if (boxSize >= 8 && boxSize <= 4096)
                    {
                        rented[i] = (byte)'f';
                        rented[i + 1] = (byte)'r';
                        rented[i + 2] = (byte)'e';
                        rented[i + 3] = (byte)'e';
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                fs.Position = 0;
                fs.Write(rented, 0, totalRead);
                fs.Flush();
            }
        }
        catch
        {
            // Ignore if file cannot be patched
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private async Task<string?> EnsureAppleMusicTokenAsync(
        bool forceRefresh,
        CancellationToken cancellationToken,
        string? rejectedToken = null)
    {
        EnsurePersistedCachesLoaded();

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(_cachedBearerToken) &&
            !string.Equals(_cachedBearerToken, rejectedToken, StringComparison.Ordinal) &&
            (_tokenExpiresAtEpochSec > nowSec + 60 || (DateTimeOffset.UtcNow - _tokenFetchedAt) < TimeSpan.FromHours(6)))
        {
            return _cachedBearerToken;
        }

        if (!forceRefresh && string.IsNullOrWhiteSpace(_cachedBearerToken))
        {
            long seedExp = ExtractJwtExpSeconds(FallbackSeedToken);
            if (seedExp > nowSec + 60 && !string.Equals(FallbackSeedToken, rejectedToken, StringComparison.Ordinal))
            {
                _cachedBearerToken = FallbackSeedToken;
                _tokenExpiresAtEpochSec = seedExp;
                _tokenFetchedAt = DateTimeOffset.UtcNow;
                return _cachedBearerToken;
            }
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrWhiteSpace(_cachedBearerToken) &&
                !string.Equals(_cachedBearerToken, rejectedToken, StringComparison.Ordinal) &&
                (!forceRefresh || rejectedToken is not null) &&
                (_tokenExpiresAtEpochSec > nowSec + 60 || (DateTimeOffset.UtcNow - _tokenFetchedAt) < TimeSpan.FromHours(6)))
            {
                return _cachedBearerToken;
            }

            if (rejectedToken is not null && string.Equals(_cachedBearerToken, rejectedToken, StringComparison.Ordinal))
            {
                _cachedBearerToken = null;
                _tokenExpiresAtEpochSec = 0;
            }

            string html = await _httpClient.GetStringAsync(AppleMusicBrowseUrl, cancellationToken).ConfigureAwait(false);

            // Fast path: <meta name="desktop-music-app/config/environment"> contains MEDIA_API.token with zero JS download
            var metaToken = TryExtractTokenFromMetaEnvironment(html, nowSec, rejectedToken);
            if (metaToken is not null)
            {
                _cachedBearerToken = metaToken.Value.Token;
                _tokenExpiresAtEpochSec = metaToken.Value.ExpSec;
                _tokenFetchedAt = DateTimeOffset.UtcNow;
                PersistTokenSafe(_cachedBearerToken, _tokenExpiresAtEpochSec);
                return _cachedBearerToken;
            }

            // Fallback: modern JS bundles (preferring non-legacy bundles first)
            var candidateBundleUrls = new List<string>();
            foreach (Match scriptMatch in BundleScriptRegex().Matches(html))
            {
                string src = scriptMatch.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(src))
                {
                    continue;
                }

                string resolved = src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? src
                    : $"https://music.apple.com{(src.StartsWith('/') ? src : "/" + src)}";
                if (!candidateBundleUrls.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                {
                    candidateBundleUrls.Add(resolved);
                }
            }

            foreach (string bundleUrl in candidateBundleUrls
                         .OrderBy(u => u.Contains("legacy", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .Take(4))
            {
                string jsContent;
                try
                {
                    jsContent = await _httpClient.GetStringAsync(bundleUrl, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                var jwtFound = FindValidJwtInText(jsContent, nowSec, rejectedToken);
                if (jwtFound is not null)
                {
                    _cachedBearerToken = jwtFound.Value.Token;
                    _tokenExpiresAtEpochSec = jwtFound.Value.ExpSec;
                    _tokenFetchedAt = DateTimeOffset.UtcNow;
                    PersistTokenSafe(_cachedBearerToken, _tokenExpiresAtEpochSec);
                    return _cachedBearerToken;
                }
            }

            var inlineJwt = FindValidJwtInText(html, nowSec, rejectedToken);
            if (inlineJwt is not null)
            {
                _cachedBearerToken = inlineJwt.Value.Token;
                _tokenExpiresAtEpochSec = inlineJwt.Value.ExpSec;
                _tokenFetchedAt = DateTimeOffset.UtcNow;
                PersistTokenSafe(_cachedBearerToken, _tokenExpiresAtEpochSec);
            }

            return _cachedBearerToken;
        }
        catch
        {
            return _cachedBearerToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static (string Token, long ExpSec)? TryExtractTokenFromMetaEnvironment(
        string html,
        long nowSec,
        string? rejectedToken)
    {
        foreach (Match match in MetaEnvironmentRegex().Matches(html))
        {
            string encoded = !string.IsNullOrWhiteSpace(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                continue;
            }

            string decoded = Uri.UnescapeDataString(encoded);
            try
            {
                using var doc = JsonDocument.Parse(decoded);
                string? token = null;
                if (doc.RootElement.TryGetProperty("MEDIA_API", out var mediaApi) &&
                    mediaApi.ValueKind == JsonValueKind.Object)
                {
                    token = GetString(mediaApi, "token");
                }

                if (string.IsNullOrWhiteSpace(token))
                {
                    token = GetString(doc.RootElement, "token");
                }

                if (!string.IsNullOrWhiteSpace(token) &&
                    !string.Equals(token, rejectedToken, StringComparison.Ordinal) &&
                    IsAppleWebPlayJwt(token))
                {
                    long exp = ExtractJwtExpSeconds(token);
                    if (exp == 0 || exp > nowSec + 60)
                    {
                        return (token, exp > 0 ? exp : nowSec + 21_600);
                    }
                }
            }
            catch
            {
                // Fall back to regex scan inside decoded string
            }

            var inline = FindValidJwtInText(decoded, nowSec, rejectedToken);
            if (inline is not null)
            {
                return inline;
            }
        }

        return null;
    }

    private static (string Token, long ExpSec)? FindValidJwtInText(
        string text,
        long nowSec,
        string? rejectedToken)
    {
        foreach (Match jwtMatch in JwtTokenRegex().Matches(text))
        {
            string candidate = jwtMatch.Value;
            if (string.Equals(candidate, rejectedToken, StringComparison.Ordinal) ||
                !IsAppleWebPlayJwt(candidate))
            {
                continue;
            }

            long exp = ExtractJwtExpSeconds(candidate);
            if (exp == 0 || exp > nowSec + 60)
            {
                return (candidate, exp > 0 ? exp : nowSec + 21_600);
            }
        }

        return null;
    }

    private static bool IsAppleWebPlayJwt(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (TryDecodeBase64Url(parts[0], out string header) &&
            header.Contains("WebPlayKid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryDecodeBase64Url(parts[1], out string payload))
        {
            return payload.Contains("AMPWebPlay", StringComparison.OrdinalIgnoreCase) ||
                   payload.Contains("WebPlay", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static long ExtractJwtExpSeconds(string jwt)
    {
        try
        {
            string[] parts = jwt.Split('.');
            if (parts.Length != 3 || !TryDecodeBase64Url(parts[1], out string payload))
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var expProp) &&
                expProp.ValueKind == JsonValueKind.Number &&
                expProp.TryGetInt64(out long exp))
            {
                return exp;
            }
        }
        catch
        {
            // Ignore malformed JWT payloads
        }

        return 0;
    }

    private async Task<CanvasArtworkResult?> TryGetFallbackFeedCanvasAsync(
        string feedUrl,
        string providerName,
        string song,
        string artist,
        bool isEcho,
        CancellationToken cancellationToken)
    {
        var entries = isEcho ? _echoEntries : _viviEntries;
        var fetchedAt = isEcho ? _echoFetchedAt : _viviFetchedAt;

        if (entries is null || (DateTimeOffset.UtcNow - fetchedAt) > TimeSpan.FromMinutes(30))
        {
            string json = await _httpClient.GetStringAsync(feedUrl, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var parsed = new List<FallbackCanvasEntry>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string s = GetString(item, "song");
                    string a = GetString(item, "artist");
                    string v = GetString(item, "videoUrl");
                    if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(v))
                    {
                        parsed.Add(new FallbackCanvasEntry(Normalize(s), Normalize(a), v));
                    }
                }

                entries = parsed;
                if (isEcho)
                {
                    _echoEntries = entries;
                    _echoFetchedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    _viviEntries = entries;
                    _viviFetchedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        if (entries is null || entries.Count == 0)
        {
            return null;
        }

        string normSong = Normalize(song);
        string normArtist = Normalize(artist);

        var match = entries.FirstOrDefault(e =>
            (e.NormalizedSong.Equals(normSong, StringComparison.OrdinalIgnoreCase) ||
             e.NormalizedSong.Contains(normSong, StringComparison.OrdinalIgnoreCase) ||
             normSong.Contains(e.NormalizedSong, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(normArtist) ||
             e.NormalizedArtist.Contains(normArtist, StringComparison.OrdinalIgnoreCase) ||
             normArtist.Contains(e.NormalizedArtist, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            return null;
        }

        string? cachedPath = await DownloadToCacheIfPossibleAsync(match.VideoUrl, cancellationToken);
        return new CanvasArtworkResult(
            VideoUri: cachedPath ?? match.VideoUrl,
            ProviderName: providerName,
            CanonicalCanvasKey: ComputeCanonicalUrlHash(match.VideoUrl));
    }

    private static HttpRequestMessage CreateAppleApiRequest(string url, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://music.apple.com/");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? ExtractAlbumIdFromSong(JsonElement songItem, JsonElement attributes)
    {
        if (songItem.TryGetProperty("relationships", out var rel) &&
            rel.TryGetProperty("albums", out var albumsRel) &&
            albumsRel.TryGetProperty("data", out var relData) &&
            relData.ValueKind == JsonValueKind.Array &&
            relData.GetArrayLength() > 0)
        {
            string id = GetString(relData[0], "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        string songUrl = GetString(attributes, "url");
        if (!string.IsNullOrWhiteSpace(songUrl))
        {
            var m = AlbumIdFromUrlRegex().Match(songUrl);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    private static bool TryDecodeBase64Url(string input, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            string padded = input.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }

            byte[] bytes = Convert.FromBase64String(padded);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CleanSongTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = ParentheticalSuffixRegex().Replace(raw, " ");
        int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 2)
        {
            string tail = s[(dashIdx + 3)..];
            if (tail.Contains("Remaster", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("Version", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("Live", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
                tail.Contains("Stereo", StringComparison.OrdinalIgnoreCase))
            {
                s = s[..dashIdx];
            }
        }

        return CollapseSpacesRegex().Replace(s, " ").Trim();
    }

    private static string CleanArtistName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = raw;
        if (s.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^8];
        }

        // Split on primary artist separator if multiple artists are joined
        string[] parts = s.Split([',', '&', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0] : s.Trim();
    }

    private static readonly HashSet<string> AllowedAlbumEditionModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "deluxe", "edition", "remaster", "remastered", "version", "single", "ep",
        "expanded", "explicit", "clean", "anniversary", "bonus", "track", "tracks",
        "special", "rare", "n", "and", "collector", "collectors", "complete",
        "standard", "encore", "platinum", "gold", "tour", "digital", "original"
    };

    private static bool IsSpecificRealAlbumName(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        string trimmed = album.Trim();
        return !trimmed.Equals("Single", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("EP", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Album", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Unknown Album", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Local Album", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("YouTube Music", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("YouTube Music For You", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Streaming", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Radio Mix", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Equals("Recommended Mix", StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeFaithfulAlbumMatchScore(string? requestedAlbumRaw, string? candidateAlbumRaw)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbumRaw) || string.IsNullOrWhiteSpace(candidateAlbumRaw))
        {
            return 0;
        }

        string reqTrimmed = StripSingleOrEpSuffix(requestedAlbumRaw);
        string candTrimmed = StripSingleOrEpSuffix(candidateAlbumRaw);

        string normReqFull = Normalize(reqTrimmed);
        string normCandFull = Normalize(candTrimmed);
        if (string.IsNullOrWhiteSpace(normReqFull) || string.IsNullOrWhiteSpace(normCandFull))
        {
            return 0;
        }

        if (string.Equals(normReqFull, normCandFull, StringComparison.OrdinalIgnoreCase))
        {
            return 120;
        }

        string baseReq = ExtractBaseAlbumTitle(reqTrimmed);
        string baseCand = ExtractBaseAlbumTitle(candTrimmed);
        if (string.IsNullOrWhiteSpace(baseReq) || string.IsNullOrWhiteSpace(baseCand))
        {
            return 0;
        }

        if (string.Equals(baseReq, baseCand, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        // Only allow prefix match if the extra tokens in the longer title are strictly edition modifiers (e.g. "Detour" vs "Detour Rare N Deluxe")
        string shorter = baseReq.Length <= baseCand.Length ? baseReq : baseCand;
        string longer = baseReq.Length <= baseCand.Length ? baseCand : baseReq;
        if (shorter.Length >= 3 &&
            longer.StartsWith(shorter + " ", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = longer[(shorter.Length + 1)..].Trim();
            string[] extraTokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (extraTokens.Length > 0 && extraTokens.All(t => AllowedAlbumEditionModifiers.Contains(t)))
            {
                return 80;
            }
        }

        return 0;
    }

    private static string StripSingleOrEpSuffix(string raw)
    {
        string s = raw.Trim();
        if (s.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^9].Trim();
        }
        else if (s.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^5].Trim();
        }

        return s;
    }

    private static string ExtractBaseAlbumTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = StripSingleOrEpSuffix(raw);
        int parenIdx = s.IndexOf('(');
        if (parenIdx > 1)
        {
            s = s[..parenIdx];
        }

        int bracketIdx = s.IndexOf('[');
        if (bracketIdx > 1)
        {
            s = s[..bracketIdx];
        }

        int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 1)
        {
            s = s[..dashIdx];
        }

        return Normalize(s);
    }

    private static string CleanAlbumName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = StripSingleOrEpSuffix(raw);
        s = ParentheticalSuffixRegex().Replace(s, " ");
        return CollapseSpacesRegex().Replace(s, " ").Trim();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string lower = value.ToLowerInvariant().Replace('$', 's');
        string alphanumeric = NonAlphanumericRegex().Replace(lower, " ");
        return CollapseSpacesRegex().Replace(alphanumeric, " ").Trim();
    }

    private static bool HasSharedArtistToken(string a, string b)
    {
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokensA.Any(ta => ta.Length >= 3 && tokensB.Any(tb => string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? TryGetFirstNonEmptyString(JsonElement element, params string[] keys)
    {
        foreach (string key in keys)
        {
            string val = GetString(element, key);
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return null;
    }

    private sealed record FallbackCanvasEntry(string NormalizedSong, string NormalizedArtist, string VideoUrl);

    /// <inheritdoc />
    public ArtistAssetsResult? PeekArtistAssets(string artistName)
    {
        string cleanArtist = CleanArtistName(artistName);
        if (string.IsNullOrWhiteSpace(cleanArtist) ||
            string.Equals(cleanArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string normKey = Normalize(cleanArtist);
        if (string.IsNullOrWhiteSpace(normKey))
        {
            return null;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EnsurePersistedCachesLoaded();
        if (!_artistAssetsCache.TryGet(normKey, out var entry) || entry.Value is null)
        {
            return null;
        }

        if (nowMs >= entry.StaleUntilUnixMs && nowMs >= entry.ExpiresAtUnixMs)
        {
            return null;
        }

        var result = entry.Value;
        if (!string.IsNullOrWhiteSpace(result.WordmarkUri))
        {
            string normalizedWordmark = NormalizeAppleWordmarkUrl(result.WordmarkUri);
            if (!string.IsNullOrWhiteSpace(normalizedWordmark) &&
                !string.Equals(normalizedWordmark, result.WordmarkUri, StringComparison.Ordinal))
            {
                result = result with { WordmarkUri = normalizedWordmark };
            }
        }
        if (!string.IsNullOrWhiteSpace(result.HeaderVideoUri))
        {
            // Only expose HeaderVideoUri synchronously from PeekArtistAssets when the normalized flat
            // local MP4 is ALREADY ready on disk — never feed a raw remote fMP4 (which has a 10s tfdt offset & stalls on loop)
            // directly to MediaPlayer before local normalization finishes asynchronously in GetArtistAssetsAsync!
            string? readyLocal = ResolveReadyLocalFlatMp4ForPeek(result.HeaderVideoUri);
            result = result with { HeaderVideoUri = readyLocal };
        }

        if (nowMs >= entry.ExpiresAtUnixMs)
        {
            // Stale-while-revalidate in the background
            _ = Task.Run(() => RefreshArtistAssetsSilentlyAsync(cleanArtist));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ArtistAssetsResult?> GetArtistAssetsAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        string cleanArtist = CleanArtistName(artistName);
        if (string.IsNullOrWhiteSpace(cleanArtist) ||
            string.Equals(cleanArtist, "Unknown Artist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string normKey = Normalize(cleanArtist);
        if (string.IsNullOrWhiteSpace(normKey))
        {
            return null;
        }

        EnsurePersistedCachesLoaded();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_artistAssetsCache.TryGet(normKey, out var cached) && nowMs < cached.ExpiresAtUnixMs)
        {
            if (cached.Value is not null)
            {
                return await ResolveFastPlayableVideoInAssetsAsync(cached.Value, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var inFlight = _inFlightArtistAssets.GetOrAdd(normKey, _ => ResolveAndCacheArtistAssetsCoreAsync(cleanArtist, normKey));
        try
        {
            var resolved = await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                return await ResolveFastPlayableVideoInAssetsAsync(resolved, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            if (inFlight.IsCompleted)
            {
                _inFlightArtistAssets.TryRemove(new KeyValuePair<string, Task<ArtistAssetsResult?>>(normKey, inFlight));
            }
        }
    }

    private async Task RefreshArtistAssetsSilentlyAsync(string cleanArtist)
    {
        try
        {
            await ResolveAndCacheArtistAssetsCoreAsync(cleanArtist, Normalize(cleanArtist)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort background refresh
        }
    }

    private async Task<ArtistAssetsResult?> ResolveAndCacheArtistAssetsCoreAsync(string cleanArtist, string normKey)
    {
        var (result, authoritative) = await FetchArtistAssetsFromAmpOrFallbackAsync(cleanArtist, normKey, CancellationToken.None)
            .ConfigureAwait(false);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (result is not null)
        {
            // Synchronize direct MP4 stream resolution immediately if editorial video is .m3u8 so subsequent opens are instant
            string? resolvedStreamUri = result.HeaderVideoUri;
            if (!string.IsNullOrWhiteSpace(resolvedStreamUri) &&
                resolvedStreamUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                string? directMp4 = await ResolveHlsMasterPlaylistToAvcMp4Async(
                    resolvedStreamUri,
                    CancellationToken.None,
                    preferredTargetWidth: 1280,
                    preferredBandwidth: 2_600_000).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(directMp4))
                {
                    resolvedStreamUri = directMp4;
                    result = result with { HeaderVideoUri = resolvedStreamUri };
                }
            }

            long hitTtlMs = 7L * 24 * 60 * 60 * 1000;
            long staleTtlMs = 30L * 24 * 60 * 60 * 1000;
            _artistAssetsCache.Set(normKey, new CachedArtistAssetsEntry(
                result,
                nowMs,
                nowMs + hitTtlMs,
                nowMs + staleTtlMs));
            _wordmarkCache.Set(cleanArtist, string.IsNullOrWhiteSpace(result.WordmarkUri) ? "NONE" : result.WordmarkUri);
            _artistHeaderVideoCache.Set(cleanArtist, string.IsNullOrWhiteSpace(result.HeaderVideoUri) ? "NONE" : result.HeaderVideoUri);
            PersistAllArtistCachesSafe();
            return result;
        }

        if (_artistAssetsCache.TryGet(normKey, out var existing) &&
            existing.Value is not null &&
            nowMs < existing.StaleUntilUnixMs)
        {
            return existing.Value;
        }

        if (authoritative)
        {
            long missTtlMs = 24L * 60 * 60 * 1000;
            _artistAssetsCache.Set(normKey, new CachedArtistAssetsEntry(null, nowMs, nowMs + missTtlMs, nowMs + missTtlMs));
            PersistAllArtistCachesSafe();
        }

        return null;
    }

    private async Task<ArtistAssetsResult> ResolveFastPlayableVideoInAssetsAsync(
        ArtistAssetsResult assets,
        CancellationToken cancellationToken)
    {
        string? rawVideo = assets.HeaderVideoUri;
        if (string.IsNullOrWhiteSpace(rawVideo))
        {
            return assets;
        }

        string directMp4 = rawVideo;
        if (directMp4.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            string? resolvedMp4 = await ResolveHlsMasterPlaylistToAvcMp4Async(
                directMp4,
                cancellationToken,
                preferredTargetWidth: 1280,
                preferredBandwidth: 2_600_000).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolvedMp4))
            {
                directMp4 = resolvedMp4!;
            }
        }

        // Check if already cached and flattened on disk -> 0ms instant local looping playback
        string? localCachedPath = GetExistingNormalizedMp4Path(directMp4);
        if (!string.IsNullOrWhiteSpace(localCachedPath))
        {
            return assets with { HeaderVideoUri = localCachedPath };
        }

        // Download & flatten Apple fMP4 -> standard flat MP4 once locally (or migrate legacy v1 file in ~10ms).
        // This eliminates both the 10-second tfdt priming dead-zone AND all EOF loop freezes / repeat HTTP traffic!
        if (directMp4.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            string? downloadedFlatPath = await DownloadToCacheIfPossibleAsync(directMp4, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(downloadedFlatPath))
            {
                return assets with { HeaderVideoUri = downloadedFlatPath };
            }
        }
        else if (File.Exists(directMp4))
        {
            string normalizedLocal = await Task.Run(
                () => Mp4CanvasNormalizer.EnsureNormalizedLocalVideoFile(directMp4, _diskCacheDir),
                cancellationToken).ConfigureAwait(false);
            return assets with { HeaderVideoUri = normalizedLocal };
        }

        return directMp4 != assets.HeaderVideoUri
            ? assets with { HeaderVideoUri = directMp4 }
            : assets;
    }

    private async Task<(ArtistAssetsResult? Result, bool Authoritative)> FetchArtistAssetsFromAmpOrFallbackAsync(
        string cleanArtist,
        string normKey,
        CancellationToken cancellationToken)
    {
        bool anyTransientError = false;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_artistIdCache.TryGet(normKey, out var cachedId) && nowMs < cachedId.ExpiresAtUnixMs)
        {
            var profileOutcome = await FetchAmpArtistProfileAsync(cachedId.Storefront, cachedId.Id, cleanArtist, cancellationToken)
                .ConfigureAwait(false);
            if (profileOutcome.Result is not null || profileOutcome.Authoritative)
            {
                return profileOutcome;
            }

            anyTransientError = true;
        }

        foreach (string storefront in new[] { "us", "gb" })
        {
            var searchOutcome = await SearchAppleArtistIdAsync(storefront, cleanArtist, cancellationToken).ConfigureAwait(false);
            if (searchOutcome.TransientError)
            {
                anyTransientError = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(searchOutcome.ArtistId))
            {
                continue;
            }

            long idTtlMs = 30L * 24 * 60 * 60 * 1000;
            _artistIdCache.Set(normKey, new CachedArtistIdEntry(
                searchOutcome.ArtistId!,
                searchOutcome.MatchedName ?? cleanArtist,
                storefront,
                nowMs + idTtlMs));

            var profileOutcome = await FetchAmpArtistProfileAsync(storefront, searchOutcome.ArtistId!, cleanArtist, cancellationToken)
                .ConfigureAwait(false);
            if (profileOutcome.Result is not null || profileOutcome.Authoritative)
            {
                return profileOutcome;
            }

            anyTransientError = true;
        }

        if (anyTransientError)
        {
            var htmlFallback = await FetchArtistAssetsFromHtmlFallbackAsync(cleanArtist, cancellationToken).ConfigureAwait(false);
            if (htmlFallback is not null)
            {
                return (htmlFallback, true);
            }
        }

        return (null, !anyTransientError);
    }

    private async Task<(string? ArtistId, string? MatchedName, bool TransientError)> SearchAppleArtistIdAsync(
        string storefront,
        string cleanArtist,
        CancellationToken cancellationToken)
    {
        string url = $"{AppleMusicBaseUrl}v1/catalog/{storefront}/search?term={Uri.EscapeDataString(cleanArtist)}&types=artists&limit=5";
        var (isSuccess, _, transientError, body) = await ExecuteAmpGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (transientError || !isSuccess || string.IsNullOrWhiteSpace(body))
        {
            return (null, null, true);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var resultsObj) ||
                !resultsObj.TryGetProperty("artists", out var artistsObj) ||
                !artistsObj.TryGetProperty("data", out var dataArr) ||
                dataArr.ValueKind != JsonValueKind.Array)
            {
                return (null, null, false);
            }

            string normTarget = Normalize(cleanArtist);
            string? bestId = null;
            string? bestName = null;
            int bestScore = int.MinValue;

            foreach (var item in dataArr.EnumerateArray())
            {
                string id = GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("attributes", out var attr))
                {
                    continue;
                }

                string name = GetString(attr, "name");
                string nName = Normalize(name);
                if (string.IsNullOrWhiteSpace(nName))
                {
                    continue;
                }

                bool matches = string.Equals(nName, normTarget, StringComparison.OrdinalIgnoreCase) ||
                               nName.Contains(normTarget, StringComparison.OrdinalIgnoreCase) ||
                               normTarget.Contains(nName, StringComparison.OrdinalIgnoreCase) ||
                               HasSharedArtistToken(nName, normTarget);
                if (!matches)
                {
                    continue;
                }

                int score = 0;
                if (string.Equals(nName, normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }
                else if (nName.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                }

                if (attr.TryGetProperty("editorialArtwork", out _) ||
                    attr.TryGetProperty("editorialVideo", out _) ||
                    attr.TryGetProperty("artwork", out _))
                {
                    score += 10;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = id;
                    bestName = name;
                }
            }

            return (bestId, bestName, false);
        }
        catch
        {
            return (null, null, true);
        }
    }

    private async Task<(ArtistAssetsResult? Result, bool Authoritative)> FetchAmpArtistProfileAsync(
        string storefront,
        string artistId,
        string fallbackArtistName,
        CancellationToken cancellationToken)
    {
        string url = $"{AppleMusicBaseUrl}v1/catalog/{storefront}/artists/{artistId}?extend=artwork,editorialArtwork,editorialVideo";
        var (isSuccess, _, transientError, body) = await ExecuteAmpGetAsync(url, cancellationToken).ConfigureAwait(false);
        if (transientError || !isSuccess || string.IsNullOrWhiteSpace(body))
        {
            return (null, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var dataArr) ||
                dataArr.ValueKind != JsonValueKind.Array ||
                dataArr.GetArrayLength() == 0)
            {
                return (null, true);
            }

            var item = dataArr[0];
            if (!item.TryGetProperty("attributes", out var attr))
            {
                return (null, true);
            }

            string resolvedName = GetString(attr, "name");
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                resolvedName = fallbackArtistName;
            }

            attr.TryGetProperty("editorialArtwork", out var editorialArtwork);
            attr.TryGetProperty("artwork", out var primaryArtwork);

            JsonElement? wordmarkObj = FindEditorialObjectByKeys(editorialArtwork, WordmarkArtworkKeys, allowLogoScan: true);
            string? wordmarkUri = null;
            if (wordmarkObj.HasValue)
            {
                string rawWordmarkUrl = GetString(wordmarkObj.Value, "url");
                if (!string.IsNullOrWhiteSpace(rawWordmarkUrl))
                {
                    string normalizedWm = NormalizeAppleWordmarkUrl(rawWordmarkUrl);
                    if (!string.IsNullOrWhiteSpace(normalizedWm))
                    {
                        wordmarkUri = normalizedWm;
                    }
                }
            }

            JsonElement? editorialHeroObj = FindEditorialObjectByKeys(editorialArtwork, ArtistHeroArtworkKeys, allowLogoScan: false);
            JsonElement? heroObj = editorialHeroObj ?? (primaryArtwork.ValueKind == JsonValueKind.Object ? primaryArtwork : null);
            string? bannerUri = null;
            if (heroObj.HasValue)
            {
                string rawHeroUrl = GetString(heroObj.Value, "url");
                if (!string.IsNullOrWhiteSpace(rawHeroUrl))
                {
                    int rawW = GetInt(heroObj.Value, "width");
                    int rawH = GetInt(heroObj.Value, "height");
                    int targetW = 1600;
                    int targetH = editorialHeroObj.HasValue ? 900 : 1200;
                    if (rawW > 0 && rawH > 0)
                    {
                        targetW = Math.Min(1600, rawW);
                        targetH = Math.Max(1, (int)((long)targetW * rawH / rawW));
                    }

                    bannerUri = rawHeroUrl
                        .Replace("\\/", "/", StringComparison.Ordinal)
                        .Replace("{w}", targetW.ToString(), StringComparison.OrdinalIgnoreCase)
                        .Replace("{h}", targetH.ToString(), StringComparison.OrdinalIgnoreCase)
                        .Replace("{c}", "cc", StringComparison.OrdinalIgnoreCase)
                        .Replace("{f}", "jpg", StringComparison.OrdinalIgnoreCase)
                        .Trim();
                }
            }

            var videoInfo = ExtractEditorialVideoInfoWithColor(attr, PreferredArtistVideoVariants);
            string? bgColorHex = PickFirstHex(heroObj, wordmarkObj, videoInfo?.BgColorHex, "bgColor");
            string? keyColorHex = PickFirstHex(heroObj, wordmarkObj, null, "textColor1");

            if (wordmarkUri is null &&
                videoInfo is null &&
                bannerUri is null &&
                bgColorHex is null)
            {
                return (null, true);
            }

            var result = new ArtistAssetsResult(
                AppleArtistId: artistId,
                ArtistName: resolvedName,
                WordmarkUri: wordmarkUri,
                HeaderVideoUri: videoInfo?.RawVideoUrl,
                BannerUri: bannerUri,
                BgColorHex: bgColorHex,
                KeyColorHex: keyColorHex);

            return (result, true);
        }
        catch
        {
            return (null, false);
        }
    }

    private async Task<(bool IsSuccess, bool Unauthorized, bool TransientError, string? Body)> ExecuteAmpGetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        string? token = await EnsureAppleMusicTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = await EnsureAppleMusicTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, true, true, null);
        }

        var first = await SendAmpGetAsync(url, token!, cancellationToken).ConfigureAwait(false);
        if (!first.Unauthorized)
        {
            return first;
        }

        string? refreshed = await EnsureAppleMusicTokenAsync(forceRefresh: true, cancellationToken, rejectedToken: token)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshed) || string.Equals(refreshed, token, StringComparison.Ordinal))
        {
            return first;
        }

        return await SendAmpGetAsync(url, refreshed!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool IsSuccess, bool Unauthorized, bool TransientError, string? Body)> SendAmpGetAsync(
        string url,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = CreateAppleApiRequest(url, bearerToken);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, true, true, null);
            }

            if (!resp.IsSuccessStatusCode)
            {
                return (false, false, true, null);
            }

            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (true, false, false, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, false, true, null);
        }
    }

    private static JsonElement? FindEditorialObjectByKeys(
        JsonElement editorialArtwork,
        IReadOnlyList<string> preferredKeys,
        bool allowLogoScan)
    {
        if (editorialArtwork.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in preferredKeys)
        {
            if (editorialArtwork.TryGetProperty(key, out var obj) &&
                obj.ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(GetString(obj, "url")))
            {
                return obj;
            }
        }

        if (allowLogoScan)
        {
            foreach (var prop in editorialArtwork.EnumerateObject())
            {
                if ((prop.Name.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Contains("wordmark", StringComparison.OrdinalIgnoreCase)) &&
                    prop.Value.ValueKind == JsonValueKind.Object &&
                    !string.IsNullOrWhiteSpace(GetString(prop.Value, "url")))
                {
                    return prop.Value;
                }
            }
        }

        return null;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.Number &&
            prop.TryGetInt32(out int val))
        {
            return val;
        }

        return 0;
    }

    private static string? PickFirstHex(
        JsonElement? heroObj,
        JsonElement? wordmarkObj,
        string? extraHex,
        string propertyName)
    {
        if (heroObj.HasValue)
        {
            string val = GetString(heroObj.Value, propertyName);
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        if (wordmarkObj.HasValue)
        {
            string val = GetString(wordmarkObj.Value, propertyName);
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }

        return !string.IsNullOrWhiteSpace(extraHex) ? extraHex : null;
    }

    /// <inheritdoc />
    public async Task<string?> GetArtistWordmarkLogoUriAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        var assets = await GetArtistAssetsAsync(artistName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(assets?.WordmarkUri))
        {
            string normalized = NormalizeAppleWordmarkUrl(assets.WordmarkUri);
            return string.IsNullOrWhiteSpace(normalized) ? assets.WordmarkUri : normalized;
        }

        string cleanArtist = CleanArtistName(artistName);
        if (!string.IsNullOrWhiteSpace(cleanArtist) &&
            _wordmarkCache.TryGet(cleanArtist, out string? cachedLogo))
        {
            if (string.Equals(cachedLogo, "NONE", StringComparison.Ordinal))
            {
                return null;
            }

            string normalized = NormalizeAppleWordmarkUrl(cachedLogo ?? string.Empty);
            return string.IsNullOrWhiteSpace(normalized) ? cachedLogo : normalized;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> GetArtistHeaderVideoUriAsync(
        string artistName,
        CancellationToken cancellationToken = default)
    {
        var assets = await GetArtistAssetsAsync(artistName, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(assets?.HeaderVideoUri) ? assets.HeaderVideoUri : null;
    }

    private string? GetExistingNormalizedMp4Path(string videoUrl)
    {
        try
        {
            string hashHex = ComputeCanonicalUrlHash(videoUrl);
            string v2Path = Path.Combine(_diskCacheDir, $"canvas_v2_{hashHex}.mp4");
            if (File.Exists(v2Path) && new FileInfo(v2Path).Length > 16_384)
            {
                return v2Path;
            }
        }
        catch
        {
            // Ignore filesystem probe errors
        }

        return null;
    }

    private async Task<ArtistAssetsResult?> FetchArtistAssetsFromHtmlFallbackAsync(
        string cleanArtist,
        CancellationToken cancellationToken)
    {
        try
        {
            string searchUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(cleanArtist)}&entity=musicArtist&limit=5";
            using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
            if (!searchResp.IsSuccessStatusCode)
            {
                return null;
            }

            string searchJson = await searchResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var searchDoc = JsonDocument.Parse(searchJson);
            if (!searchDoc.RootElement.TryGetProperty("results", out var resultsArr) ||
                resultsArr.ValueKind != JsonValueKind.Array ||
                resultsArr.GetArrayLength() == 0)
            {
                return null;
            }

            string? bestArtistUrl = null;
            string normTarget = Normalize(cleanArtist);

            foreach (var item in resultsArr.EnumerateArray())
            {
                string candName = GetString(item, "artistName");
                string candUrl = GetString(item, "artistLinkUrl");
                if (string.IsNullOrWhiteSpace(candUrl))
                {
                    continue;
                }

                if (string.Equals(Normalize(candName), normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    bestArtistUrl = candUrl;
                    break;
                }

                bestArtistUrl ??= candUrl;
            }

            if (string.IsNullOrWhiteSpace(bestArtistUrl))
            {
                return null;
            }

            int queryIdx = bestArtistUrl.IndexOf('?', StringComparison.Ordinal);
            if (queryIdx > 0)
            {
                bestArtistUrl = bestArtistUrl[..queryIdx];
            }

            using var pageReq = new HttpRequestMessage(HttpMethod.Get, bestArtistUrl);
            pageReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            pageReq.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var pageResp = await _httpClient.SendAsync(pageReq, cancellationToken).ConfigureAwait(false);
            if (!pageResp.IsSuccessStatusCode)
            {
                return null;
            }

            string html = await pageResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? resolvedWordmarkUrl = null;
            var logoMatch = ArtistLogoJsonRegex().Match(html);
            if (logoMatch.Success)
            {
                resolvedWordmarkUrl = NormalizeAppleWordmarkUrl(logoMatch.Groups[1].Value);
            }
            else
            {
                var pictureMatch = ArtistPictureWordmarkRegex().Match(html);
                if (pictureMatch.Success)
                {
                    resolvedWordmarkUrl = NormalizeAppleWordmarkUrl(pictureMatch.Groups[1].Value);
                }
            }

            string? rawHeaderM3u8Url = null;
            var wideVideoMatch = ArtistWideVideoJsonRegex().Match(html);
            if (wideVideoMatch.Success)
            {
                rawHeaderM3u8Url = wideVideoMatch.Groups[1].Value.Replace("\\/", "/", StringComparison.Ordinal).Trim();
            }

            if (string.IsNullOrWhiteSpace(resolvedWordmarkUrl) && string.IsNullOrWhiteSpace(rawHeaderM3u8Url))
            {
                return null;
            }

            return new ArtistAssetsResult(
                ArtistName: cleanArtist,
                WordmarkUri: string.IsNullOrWhiteSpace(resolvedWordmarkUrl) ? null : resolvedWordmarkUrl,
                HeaderVideoUri: string.IsNullOrWhiteSpace(rawHeaderM3u8Url) ? null : rawHeaderM3u8Url);
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

    private static string NormalizeAppleWordmarkUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        string cleaned = rawUrl.Replace("\\/", "/", StringComparison.Ordinal).Trim();
        if (cleaned.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Apple Music AMP returns both legacy ".png/{w}x{h}bb.{f}" and opaque-token "/{w}x{h}bb.jpg" URLs.
        // Requesting .jpg causes Apple's CDN to composite white lettering onto a solid #FFFFFF JPEG rectangle;
        // always force 800x266bb.png on mzstatic.com/image/thumb/ URLs so 32-bit RGBA alpha transparency is preserved.
        int thumbIdx = cleaned.IndexOf("/image/thumb/", StringComparison.OrdinalIgnoreCase);
        int lastSlash = cleaned.LastIndexOf('/');
        if (thumbIdx >= 0 && lastSlash > thumbIdx + "/image/thumb/".Length)
        {
            return $"{cleaned[..(lastSlash + 1)]}800x266bb.png";
        }

        string normalized = cleaned
            .Replace("{w}x{h}{c}.{f}", "800x266bb.png", StringComparison.OrdinalIgnoreCase)
            .Replace("{w}x{h}bb.{f}", "800x266bb.png", StringComparison.OrdinalIgnoreCase)
            .Replace("{w}", "800", StringComparison.OrdinalIgnoreCase)
            .Replace("{h}", "266", StringComparison.OrdinalIgnoreCase)
            .Replace("{c}", "bb", StringComparison.OrdinalIgnoreCase)
            .Replace("{f}", "png", StringComparison.OrdinalIgnoreCase)
            .Replace("800x266bb.webp", "800x266bb.png", StringComparison.OrdinalIgnoreCase)
            .Replace("400x133bb.webp", "800x266bb.png", StringComparison.OrdinalIgnoreCase);

        if (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized[..^4]}.png";
        }
        else if (normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized[..^5]}.png";
        }
        else if (normalized.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized[..^5]}.png";
        }

        return normalized;
    }

    private void LoadPersistedCachesSafe()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool upgradedWordmarks = false;
        bool upgradedArtistAssets = false;

        if (File.Exists(_wordmarkCacheFilePath))
        {
            using var stream = File.OpenRead(_wordmarkCacheFilePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(stream);
            if (loaded is not null)
            {
                foreach (var kvp in loaded)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && !_wordmarkCache.ContainsKey(kvp.Key))
                    {
                        string? val = kvp.Value;
                        if (!string.IsNullOrWhiteSpace(val) && !string.Equals(val, "NONE", StringComparison.Ordinal))
                        {
                            string sanitized = NormalizeAppleWordmarkUrl(val);
                            if (!string.IsNullOrWhiteSpace(sanitized) && !string.Equals(sanitized, val, StringComparison.Ordinal))
                            {
                                val = sanitized;
                                upgradedWordmarks = true;
                            }
                        }

                        _wordmarkCache.Set(kvp.Key, val);
                    }
                }
            }
        }

        if (File.Exists(_tokenCacheFilePath))
        {
            using var stream = File.OpenRead(_tokenCacheFilePath);
            var tokenData = JsonSerializer.Deserialize<PersistedTokenData>(stream);
            if (tokenData is not null && !string.IsNullOrWhiteSpace(tokenData.Token) && tokenData.ExpiresAtEpochSec > nowSec + 60)
            {
                _cachedBearerToken = tokenData.Token;
                _tokenExpiresAtEpochSec = tokenData.ExpiresAtEpochSec;
                _tokenFetchedAt = DateTimeOffset.UtcNow;
            }
        }

        if (File.Exists(_artistIdsCacheFilePath))
        {
            using var stream = File.OpenRead(_artistIdsCacheFilePath);
            var loadedIds = JsonSerializer.Deserialize<Dictionary<string, CachedArtistIdEntry>>(stream);
            if (loadedIds is not null)
            {
                foreach (var kvp in loadedIds)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value.ExpiresAtUnixMs > nowMs && !_artistIdCache.ContainsKey(kvp.Key))
                    {
                        _artistIdCache.Set(kvp.Key, kvp.Value);
                    }
                }
            }
        }

        if (File.Exists(_artistAssetsCacheFilePath))
        {
            using var stream = File.OpenRead(_artistAssetsCacheFilePath);
            var loadedAssets = JsonSerializer.Deserialize<Dictionary<string, CachedArtistAssetsEntry>>(stream);
            if (loadedAssets is not null)
            {
                foreach (var kvp in loadedAssets)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) &&
                        (kvp.Value.ExpiresAtUnixMs > nowMs || kvp.Value.StaleUntilUnixMs > nowMs) &&
                        !_artistAssetsCache.ContainsKey(kvp.Key))
                    {
                        var entryVal = kvp.Value;
                        if (entryVal.Value is not null && !string.IsNullOrWhiteSpace(entryVal.Value.WordmarkUri))
                        {
                            string sanitized = NormalizeAppleWordmarkUrl(entryVal.Value.WordmarkUri);
                            if (!string.IsNullOrWhiteSpace(sanitized) &&
                                !string.Equals(sanitized, entryVal.Value.WordmarkUri, StringComparison.Ordinal))
                            {
                                entryVal = entryVal with
                                {
                                    Value = entryVal.Value with { WordmarkUri = sanitized }
                                };
                                upgradedArtistAssets = true;
                            }
                        }

                        _artistAssetsCache.Set(kvp.Key, entryVal);
                    }
                }
            }
        }

        if (upgradedWordmarks || upgradedArtistAssets)
        {
            _ = Task.Run(() =>
            {
                try { PersistAllArtistCachesSafe(); } catch { }
            });
        }

        if (File.Exists(_albumCanvasCacheFilePath))
        {
            using var stream = File.OpenRead(_albumCanvasCacheFilePath);
            var loadedAlbums = JsonSerializer.Deserialize<Dictionary<string, CachedAlbumCanvasEntry>>(stream);
            if (loadedAlbums is not null)
            {
                foreach (var kvp in loadedAlbums)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) &&
                        !string.IsNullOrWhiteSpace(kvp.Value.VideoUri) &&
                        kvp.Value.ExpiresAtUnixMs > nowMs &&
                        !_albumCanvasCache.ContainsKey(kvp.Key))
                    {
                        _albumCanvasCache.Set(kvp.Key, kvp.Value);
                    }
                }
            }
        }
    }

    private void PersistAlbumCanvasCacheSafe()
    {
        try
        {
            EnsurePersistedCachesLoaded();
            using var stream = File.Create(_albumCanvasCacheFilePath);
            JsonSerializer.Serialize(stream, _albumCanvasCache.ToDictionarySnapshot());
        }
        catch
        {
            // Ignore concurrent file write contention
        }
    }

    private void PersistTokenSafe(string token, long expEpochSec)
    {
        try
        {
            using var stream = File.Create(_tokenCacheFilePath);
            JsonSerializer.Serialize(stream, new PersistedTokenData(token, expEpochSec));
        }
        catch
        {
            // Ignore file contention
        }
    }

    private void PersistAllArtistCachesSafe()
    {
        try
        {
            EnsurePersistedCachesLoaded();
            using (var s1 = File.Create(_wordmarkCacheFilePath))
            {
                JsonSerializer.Serialize(s1, _wordmarkCache.ToDictionarySnapshot());
            }

            using (var s2 = File.Create(_artistAssetsCacheFilePath))
            {
                JsonSerializer.Serialize(s2, _artistAssetsCache.ToDictionarySnapshot());
            }

            using (var s3 = File.Create(_artistIdsCacheFilePath))
            {
                JsonSerializer.Serialize(s3, _artistIdCache.ToDictionarySnapshot());
            }
        }
        catch
        {
            // Ignore concurrent file write contention
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _httpClient.Dispose();
        _tokenLock.Dispose();
    }

    [GeneratedRegex(@"<meta[^>]+name=[""']desktop-music-app/config/environment[""'][^>]+content=[""']([^""']+)[""']|<meta[^>]+content=[""']([^""']+)[""'][^>]+name=[""']desktop-music-app/config/environment[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaEnvironmentRegex();

    [GeneratedRegex(@"""(?:musicContentWhiteLogoTrimmed|musicContentColorLogoTrimmed|artistLogo)""\s*:\s*\{\s*(?:""dictionary""\s*:\s*\{)?[^}]*""url""\s*:\s*""(https://is\d-ssl\.mzstatic\.com/image/thumb/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistLogoJsonRegex();

    [GeneratedRegex(@"(https://is\d-ssl\.mzstatic\.com/image/thumb/Features[^""'\s<>]+?\.png/(?:800x266|400x133)bb\.(?:webp|png))", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistPictureWordmarkRegex();

    [GeneratedRegex(@"""(?:wideVideoArtwork|motionArtistFullscreen16x9|motionArtistWide16x9)""[\s\S]{1,1500}?""video""\s*:\s*""(https://mvod\.itunes\.apple\.com/[^""]+?\.m3u8)""", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistWideVideoJsonRegex();

    [GeneratedRegex(@"<script[^>]+src=""(/assets/[^""]*\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex BundleScriptRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex(@"/album/[^/]+/(\d+)")]
    private static partial Regex AlbumIdFromUrlRegex();

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"(?:^|,)BANDWIDTH=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BandwidthRegex();

    [GeneratedRegex(@"#EXT-X-MAP:URI=""([^""]+\.mp4)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExtXMapUriRegex();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(?:feat\.?|ft\.?|official|video|audio|lyric|visualizer|remaster|hd|4k|explicit|clean)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex ParentheticalSuffixRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseSpacesRegex();
}
