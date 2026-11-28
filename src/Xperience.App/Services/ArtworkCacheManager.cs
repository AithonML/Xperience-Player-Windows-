using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xperience.Core.Collections;

namespace Xperience.App.Services;

/// <summary>
/// Centralized two-tier (in-memory LRU + persistent disk <c>OnlineCache\CoverCache</c>) artwork cache
/// with single-flight request deduplication and canonical URL normalization so cover art is never
/// re-downloaded over the network across UI surfaces, album siblings, palette extraction, or app restarts.
/// </summary>
public static partial class ArtworkCacheManager
{
    public readonly record struct ArtworkFetchOutcome(
        string? LocalFilePath,
        string? LocalFileUri,
        long NetworkBytesDownloaded,
        bool FromCache);

    private sealed record CachedArtworkEntry(
        string LocalFilePath,
        string LocalFileUri,
        int MaxDimensionPx);

    private const long MaxDiskCacheBytes = 250L * 1024 * 1024;
    private const long TargetTrimBytes = 200L * 1024 * 1024;

    private static readonly string CoverCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XperienceMusicPlayer",
        "OnlineCache",
        "CoverCache");

    private static readonly HttpClient SharedImageHttpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly BoundedLruCache<string, CachedArtworkEntry> MemoryCache =
        new(maxCapacity: 512, comparer: StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Task<ArtworkFetchOutcome>> InFlightDownloads =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SupportedExtensions = [".jpg", ".png", ".gif", ".bmp"];
    private static int _diskTrimScheduled;

    static ArtworkCacheManager()
    {
        try
        {
            Directory.CreateDirectory(CoverCacheDirectory);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Computes the canonical deduplication key for a cover/artist artwork URL, stripping Google CDN
    /// size tokens (<c>=w544-h544...</c>, <c>=s544...</c>) so low-res and high-res variants of the same image share one slot.
    /// </summary>
    public static (string CanonicalKey, string NormalizedDownloadUrl, int TargetDimensionPx) NormalizeArtworkRequest(
        string rawUri,
        int requestedDimensionPx = 544)
    {
        string trimmed = (rawUri ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (string.Empty, string.Empty, requestedDimensionPx);
        }

        int targetDim = Math.Clamp(requestedDimensionPx, 96, 1920);
        if (trimmed.Contains("mzstatic.com/image/thumb/", StringComparison.OrdinalIgnoreCase))
        {
            // Keep Apple transparent PNG wordmarks distinct and preserve PNG format
            string key = "mz_" + ComputeShortSha256(trimmed);
            return (key, trimmed, Math.Max(targetDim, 544));
        }

        if (trimmed.Contains("googleusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("ggpht.com/", StringComparison.OrdinalIgnoreCase))
        {
            int eqIdx = trimmed.IndexOf('=', StringComparison.Ordinal);
            string basePath = eqIdx > 0 ? trimmed[..eqIdx] : trimmed;
            int cacheDim = Math.Max(544, targetDim);
            string highResUrl = $"{basePath}=w{cacheDim}-h{cacheDim}-l90-rj";
            string canonicalKey = "g_" + ComputeShortSha256(basePath);
            return (canonicalKey, highResUrl, cacheDim);
        }

        var ytMatch = YtImgRegex().Match(trimmed);
        if (ytMatch.Success)
        {
            string videoId = ytMatch.Groups[1].Value;
            string canonicalKey = "ytimg_" + videoId;
            return (canonicalKey, trimmed, Math.Max(480, targetDim));
        }

        return ("u_" + ComputeShortSha256(trimmed), trimmed, Math.Max(544, targetDim));
    }

    /// <summary>
    /// Performs a synchronous memory/disk cache probe (zero network I/O) for an artwork URI.
    /// </summary>
    public static bool TryGetCachedFileUri(
        string? rawUri,
        int minDimensionPx,
        out string? localFileUri,
        out string? localFilePath)
    {
        localFileUri = null;
        localFilePath = null;
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return false;
        }

        string trimmed = rawUri.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var (canonicalKey, normalizedUrl, _) = NormalizeArtworkRequest(trimmed, minDimensionPx);
        if (string.IsNullOrEmpty(canonicalKey))
        {
            return false;
        }

        if (MemoryCache.TryGet(canonicalKey, out var memEntry) &&
            memEntry is not null &&
            memEntry.MaxDimensionPx >= minDimensionPx &&
            File.Exists(memEntry.LocalFilePath))
        {
            localFilePath = memEntry.LocalFilePath;
            localFileUri = memEntry.LocalFileUri;
            return true;
        }

        // Check disk cache: new canonical filename + legacy YouTubeMusicService CoverCache filenames
        if (TryFindOnDisk(canonicalKey, normalizedUrl, trimmed, out string? diskPath, out int diskDim))
        {
            string diskUri = new Uri(Path.GetFullPath(diskPath!)).AbsoluteUri;
            MemoryCache.Set(canonicalKey, new CachedArtworkEntry(diskPath!, diskUri, diskDim));
            if (diskDim >= minDimensionPx)
            {
                localFilePath = diskPath;
                localFileUri = diskUri;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the locally cached file for <paramref name="rawUri"/>, downloading over HTTP at most once
    /// across all concurrent callers and persisting to <c>CoverCache</c>.
    /// Reports <c>NetworkBytesDownloaded &gt; 0</c> ONLY when bytes were actually transferred over the network.
    /// </summary>
    public static async Task<ArtworkFetchOutcome> GetOrDownloadAsync(
        string? rawUri,
        int minDimensionPx = 544,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return new ArtworkFetchOutcome(null, null, 0, FromCache: false);
        }

        string trimmed = rawUri.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var existingUri) && existingUri.IsFile)
        {
            string lp = existingUri.LocalPath;
            return File.Exists(lp)
                ? new ArtworkFetchOutcome(lp, existingUri.AbsoluteUri, 0, FromCache: true)
                : new ArtworkFetchOutcome(null, null, 0, FromCache: false);
        }

        if (File.Exists(trimmed))
        {
            string full = Path.GetFullPath(trimmed);
            return new ArtworkFetchOutcome(full, new Uri(full).AbsoluteUri, 0, FromCache: true);
        }

        if (TryGetCachedFileUri(trimmed, minDimensionPx, out string? cachedUri, out string? cachedPath))
        {
            return new ArtworkFetchOutcome(cachedPath, cachedUri, 0, FromCache: true);
        }

        var (canonicalKey, normalizedUrl, targetDim) = NormalizeArtworkRequest(trimmed, minDimensionPx);
        if (string.IsNullOrEmpty(canonicalKey) || string.IsNullOrEmpty(normalizedUrl))
        {
            return new ArtworkFetchOutcome(null, null, 0, FromCache: false);
        }

        // Single-flight deduplication: only 1 HTTP download runs per canonical key + dimension
        string flightKey = $"{canonicalKey}:{targetDim}";
        var sharedTask = InFlightDownloads.GetOrAdd(
            flightKey,
            _ => DownloadAndStoreAsync(canonicalKey, normalizedUrl, trimmed, targetDim));

        try
        {
            var outcome = await sharedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            if (sharedTask.IsCompleted)
            {
                InFlightDownloads.TryRemove(flightKey, out _);
            }
        }
    }

    /// <summary>
    /// Reads raw encoded image bytes for <paramref name="rawUri"/> using the two-tier cache
    /// (zero HTTP requests if already cached on disk or in memory).
    /// </summary>
    public static async Task<byte[]?> GetOrDownloadBytesAsync(
        string? rawUri,
        int minDimensionPx = 256,
        CancellationToken cancellationToken = default)
    {
        var outcome = await GetOrDownloadAsync(rawUri, minDimensionPx, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(outcome.LocalFilePath) && File.Exists(outcome.LocalFilePath))
        {
            try
            {
                return await File.ReadAllBytesAsync(outcome.LocalFilePath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        return null;
    }

    private static async Task<ArtworkFetchOutcome> DownloadAndStoreAsync(
        string canonicalKey,
        string normalizedUrl,
        string originalUrl,
        int targetDim)
    {
        try
        {
            Directory.CreateDirectory(CoverCacheDirectory);
        }
        catch
        {
            return new ArtworkFetchOutcome(null, null, 0, FromCache: false);
        }

        if (TryFindOnDisk(canonicalKey, normalizedUrl, originalUrl, out string? diskPath, out int diskDim) &&
            diskDim >= targetDim)
        {
            string diskUri = new Uri(Path.GetFullPath(diskPath!)).AbsoluteUri;
            MemoryCache.Set(canonicalKey, new CachedArtworkEntry(diskPath!, diskUri, diskDim));
            return new ArtworkFetchOutcome(diskPath, diskUri, 0, FromCache: true);
        }

        string tempPath = Path.Combine(CoverCacheDirectory, $"{canonicalKey}_{Guid.NewGuid():N}.tmp");
        try
        {
            var (ext, downloadedBytes, isWebp) = await StreamToTempAsync(normalizedUrl, tempPath, CancellationToken.None).ConfigureAwait(false);
            if (ext is null && isWebp &&
                (normalizedUrl.Contains("googleusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
                 normalizedUrl.Contains("ggpht.com/", StringComparison.OrdinalIgnoreCase)))
            {
                string jpegUrl = ForceGoogleJpeg(normalizedUrl);
                if (!string.Equals(jpegUrl, normalizedUrl, StringComparison.Ordinal))
                {
                    (ext, downloadedBytes, _) = await StreamToTempAsync(jpegUrl, tempPath, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (ext is null && !string.Equals(normalizedUrl, originalUrl, StringComparison.OrdinalIgnoreCase))
            {
                (ext, downloadedBytes, _) = await StreamToTempAsync(originalUrl, tempPath, CancellationToken.None).ConfigureAwait(false);
            }

            if (ext is null || !File.Exists(tempPath))
            {
                return new ArtworkFetchOutcome(null, null, 0, FromCache: false);
            }

            string finalPath = Path.Combine(CoverCacheDirectory, $"art_{canonicalKey}_{targetDim}{ext}");
            File.Move(tempPath, finalPath, overwrite: true);
            string finalUri = new Uri(Path.GetFullPath(finalPath)).AbsoluteUri;
            MemoryCache.Set(canonicalKey, new CachedArtworkEntry(finalPath, finalUri, targetDim));
            ScheduleDiskTrimIfNeeded();
            return new ArtworkFetchOutcome(finalPath, finalUri, downloadedBytes, FromCache: false);
        }
        catch
        {
            return new ArtworkFetchOutcome(null, null, 0, FromCache: false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static bool TryFindOnDisk(
        string canonicalKey,
        string normalizedUrl,
        string originalUrl,
        out string? foundPath,
        out int foundDim)
    {
        foundPath = null;
        foundDim = 0;

        try
        {
            if (!Directory.Exists(CoverCacheDirectory))
            {
                return false;
            }

            // 1. Check new canonical files (prefer highest dimension cached)
            foreach (int dim in new[] { 1920, 1080, 544, 480, 256 })
            {
                foreach (string ext in SupportedExtensions)
                {
                    string path = Path.Combine(CoverCacheDirectory, $"art_{canonicalKey}_{dim}{ext}");
                    if (IsValidImageFile(path))
                    {
                        foundPath = path;
                        foundDim = dim;
                        return true;
                    }
                }
            }

            // 2. Backward compatibility with existing YouTubeMusicService CoverCache files (`ytm_{sha256_16}.{ext}`)
            string[] legacyHashes =
            [
                ComputeShortSha256(normalizedUrl),
                ComputeShortSha256(originalUrl)
            ];

            foreach (string legacyHash in legacyHashes)
            {
                foreach (string ext in SupportedExtensions)
                {
                    string legacyPath = Path.Combine(CoverCacheDirectory, $"ytm_{legacyHash}{ext}");
                    if (IsValidImageFile(legacyPath))
                    {
                        foundPath = legacyPath;
                        foundDim = 544;
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsValidImageFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 200;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string? Extension, long Bytes, bool IsWebp)> StreamToTempAsync(
        string url,
        string tempPath,
        CancellationToken cancellationToken)
    {
        const long maxImageBytes = 8_000_000;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "image/png,image/jpeg,image/gif,image/bmp;q=0.9,*/*;q=0.5");

        using var resp = await SharedImageHttpClient.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength is > maxImageBytes)
        {
            return (null, 0, false);
        }

        await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] header = new byte[16];
        int headerRead = 0;
        while (headerRead < 12)
        {
            int n = await netStream.ReadAsync(header.AsMemory(headerRead, header.Length - headerRead), cancellationToken).ConfigureAwait(false);
            if (n <= 0) break;
            headerRead += n;
        }

        if (headerRead < 12)
        {
            return (null, 0, false);
        }

        string? ext = DetectImageExtension(header);
        if (ext is null)
        {
            bool isWebp = header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
                          header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';
            return (null, 0, isWebp);
        }

        long totalBytes = headerRead;
        byte[] buf = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16384, useAsync: true))
            {
                await fs.WriteAsync(header.AsMemory(0, headerRead), cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    int read = await netStream.ReadAsync(buf.AsMemory(0, buf.Length), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    totalBytes += read;
                    if (totalBytes > maxImageBytes)
                    {
                        return (null, 0, false);
                    }
                    await fs.WriteAsync(buf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            return totalBytes >= 200 ? (ext, totalBytes, false) : (null, 0, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static string? DetectImageExtension(ReadOnlySpan<byte> h)
    {
        if (h.Length < 12) return null;
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return ".png";
        if (h[0] == (byte)'G' && h[1] == (byte)'I' && h[2] == (byte)'F') return ".gif";
        if (h[0] == (byte)'B' && h[1] == (byte)'M') return ".bmp";
        return null;
    }

    private static string ForceGoogleJpeg(string url)
    {
        int eqIdx = url.IndexOf('=', StringComparison.Ordinal);
        if (eqIdx <= 0) return url;
        string suffix = url[eqIdx..];
        if (suffix.Contains("-rj", StringComparison.OrdinalIgnoreCase)) return url;
        return $"{url}-rj";
    }

    private static string ComputeShortSha256(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void ScheduleDiskTrimIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _diskTrimScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CoverCacheDirectory))
                {
                    return;
                }

                var files = new DirectoryInfo(CoverCacheDirectory)
                    .EnumerateFiles()
                    .Where(f => !f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    .Select(f => (File: f, f.Length, f.LastAccessTimeUtc))
                    .ToList();

                long total = files.Sum(x => x.Length);
                if (total <= MaxDiskCacheBytes)
                {
                    return;
                }

                foreach (var entry in files.OrderBy(x => x.LastAccessTimeUtc))
                {
                    if (total <= TargetTrimBytes)
                    {
                        break;
                    }

                    try
                    {
                        entry.File.Delete();
                        total -= entry.Length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _diskTrimScheduled, 0);
            }
        });
    }

    [GeneratedRegex(@"^https?://i\d?\.ytimg\.com/vi/([A-Za-z0-9_-]{11})/", RegexOptions.IgnoreCase)]
    private static partial Regex YtImgRegex();
}
