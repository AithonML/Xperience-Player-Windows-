using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Updates;

/// <summary>
/// Secure GitHub Releases auto-update service implementing SemVer 2.0.0 comparison,
/// Windows architecture-aware asset selection, streaming %TEMP% downloads with progress,
/// and fail-closed streaming SHA-256 verification.
/// </summary>
public sealed class GitHubAppUpdateService : IAppUpdateService, IDisposable
{
    public const string DefaultLatestReleaseEndpoint =
        "https://api.github.com/repos/AithonML/Xperience-Player-Windows-/releases/latest";

    private const int DownloadBufferSize = 81920;
    private const int MaxChecksumFileBytes = 1024 * 1024; // 1 MB safety limit for checksum manifests
    private static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(25);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Action<string>? _log;
    private readonly string _latestReleaseEndpoint;
    private readonly string _updatesDirectory;
    private readonly Architecture? _architectureOverride;
    private readonly bool _isPackagedApp;
    private readonly TimeSpan _metadataTimeout;
    private readonly string _userAgentHeaderValue;
    private bool _disposed;

    /// <inheritdoc />
    public SemVersion CurrentVersion { get; }

    /// <inheritdoc />
    public string CurrentVersionDisplay => CurrentVersion.DisplayTag;

    /// <summary>
    /// Gets the absolute directory path where verified installer downloads are staged.
    /// </summary>
    public string UpdatesDirectory => _updatesDirectory;

    /// <summary>
    /// Initializes a new instance of <see cref="GitHubAppUpdateService"/>.
    /// </summary>
    public GitHubAppUpdateService(
        SemVersion currentVersion,
        Action<string>? log = null,
        HttpClient? httpClient = null,
        string? latestReleaseEndpoint = null,
        string? updatesDirectory = null,
        Architecture? architectureOverride = null,
        bool isPackagedApp = false,
        TimeSpan? metadataTimeout = null)
    {
        CurrentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _log = log;
        _latestReleaseEndpoint = string.IsNullOrWhiteSpace(latestReleaseEndpoint)
            ? DefaultLatestReleaseEndpoint
            : latestReleaseEndpoint;
        _updatesDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(updatesDirectory)
                ? Path.Combine(Path.GetTempPath(), "XperienceMusicPlayer", "Updates")
                : updatesDirectory);
        _architectureOverride = architectureOverride;
        _isPackagedApp = isPackagedApp;
        _metadataTimeout = metadataTimeout is { TotalMilliseconds: > 0 } customTimeout
            ? customTimeout
            : DefaultMetadataTimeout;
        _userAgentHeaderValue = $"XperienceMusicPlayer-Windows/{CurrentVersion.NormalizedString}";

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(15)
            };
            _ownsHttpClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!WindowsReleaseAssetSelector.IsValidHttpsUrl(_latestReleaseEndpoint))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                CurrentVersion,
                ErrorMessage: "Invalid update endpoint configuration.");
        }

        try
        {
            using var metadataCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            metadataCts.CancelAfter(_metadataTimeout);
            var metadataToken = metadataCts.Token;

            using var request = CreateGitHubApiRequest(_latestReleaseEndpoint);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                metadataToken).ConfigureAwait(false);

            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !WindowsReleaseAssetSelector.IsValidHttpsUrl(finalUri.AbsoluteUri))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.NetworkError,
                    CurrentVersion,
                    ErrorMessage: "Refused non-HTTPS redirect from update endpoint.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log("GitHub Releases returned 404 (no published release found yet).");
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoPublishedReleases,
                    CurrentVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                string message = $"GitHub API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                Log($"Update check failed: {message}");
                return new UpdateCheckResult(
                    UpdateCheckStatus.NetworkError,
                    CurrentVersion,
                    ErrorMessage: message);
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(metadataToken)
                .ConfigureAwait(false);
            var releaseDto = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(
                contentStream,
                JsonOptions,
                metadataToken).ConfigureAwait(false);

            if (releaseDto is null || releaseDto.Draft || string.IsNullOrWhiteSpace(releaseDto.TagName))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoPublishedReleases,
                    CurrentVersion);
            }

            if (!SemVerComparer.TryParse(releaseDto.TagName, out var latestVersion) || latestVersion is null)
            {
                Log($"Unable to parse GitHub release tag '{releaseDto.TagName}' as SemVer 2.0.0.");
                return new UpdateCheckResult(
                    UpdateCheckStatus.Error,
                    CurrentVersion,
                    ErrorMessage: $"Unrecognized release version tag: {releaseDto.TagName}");
            }

            if (!SemVerComparer.IsNewer(latestVersion, CurrentVersion))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    CurrentVersion,
                    LatestVersion: latestVersion);
            }

            var selectedAsset = _architectureOverride.HasValue
                ? WindowsReleaseAssetSelector.SelectInstallerAsset(releaseDto.Assets, _architectureOverride.Value, _isPackagedApp)
                : WindowsReleaseAssetSelector.SelectForCurrentEnvironment(releaseDto.Assets, _isPackagedApp);

            if (selectedAsset is null)
            {
                Log($"Newer release {latestVersion.DisplayTag} found, but no compatible Windows installer asset matched this architecture.");
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoCompatibleAsset,
                    CurrentVersion,
                    LatestVersion: latestVersion,
                    ErrorMessage: "No compatible Windows installer was found for this system architecture.");
            }

            string? expectedSha256 = await ResolveExpectedSha256Async(releaseDto.Assets, selectedAsset, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(expectedSha256))
            {
                Log($"Fail-closed: Release {latestVersion.DisplayTag} asset '{selectedAsset.Name}' has no valid SHA-256 digest or checksum entry.");
                return new UpdateCheckResult(
                    UpdateCheckStatus.MissingChecksum,
                    CurrentVersion,
                    LatestVersion: latestVersion,
                    ErrorMessage: "Security verification blocked update: no valid SHA-256 checksum was provided for the installer.");
            }

            var releaseInfo = new UpdateReleaseInfo(
                Version: latestVersion,
                TagName: releaseDto.TagName.Trim(),
                ReleaseName: string.IsNullOrWhiteSpace(releaseDto.Name) ? latestVersion.DisplayTag : releaseDto.Name.Trim(),
                ReleaseNotes: releaseDto.Body?.Trim() ?? string.Empty,
                ReleaseUrl: WindowsReleaseAssetSelector.IsValidHttpsUrl(releaseDto.HtmlUrl)
                    ? releaseDto.HtmlUrl!
                    : "https://github.com/AithonML/Xperience-Player-Windows-/releases",
                PublishedAt: releaseDto.PublishedAt,
                SelectedAsset: selectedAsset,
                ExpectedSha256: expectedSha256);

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                CurrentVersion,
                LatestVersion: latestVersion,
                AvailableUpdate: releaseInfo);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log("Update check timed out.");
            return new UpdateCheckResult(
                UpdateCheckStatus.NetworkError,
                CurrentVersion,
                ErrorMessage: "The update check timed out.");
        }
        catch (HttpRequestException ex)
        {
            Log($"Network error while checking for updates: {ex.Message}");
            return new UpdateCheckResult(
                UpdateCheckStatus.NetworkError,
                CurrentVersion,
                ErrorMessage: ex.Message);
        }
        catch (JsonException ex)
        {
            Log($"Invalid JSON payload from GitHub Releases API: {ex.Message}");
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                CurrentVersion,
                ErrorMessage: "Invalid release metadata received from GitHub.");
        }
        catch (Exception ex)
        {
            Log($"Unexpected error while checking for updates: {ex.Message}");
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                CurrentVersion,
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<UpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        // Fail-closed pre-flight validation
        if (!Sha256ChecksumParser.TryNormalizeSha256(release.ExpectedSha256, out string normalizedExpectedSha256))
        {
            return new UpdateDownloadResult(
                Succeeded: false,
                IsIntegrityMismatch: true,
                ErrorMessage: "Invalid or missing expected SHA-256 digest.");
        }

        if (release.SelectedAsset is null ||
            !WindowsReleaseAssetSelector.IsValidHttpsUrl(release.SelectedAsset.BrowserDownloadUrl))
        {
            return new UpdateDownloadResult(
                Succeeded: false,
                ErrorMessage: "Installer download URL must use HTTPS.");
        }

        if (!TryBuildSafeDestinationPaths(release.SelectedAsset.Name, out string partFilePath, out string finalFilePath))
        {
            return new UpdateDownloadResult(
                Succeeded: false,
                ErrorMessage: "Installer filename failed security sanitization.");
        }

        progress?.Report(new UpdateDownloadProgress(
            UpdateDownloadPhase.CleaningTemp,
            BytesDownloaded: 0,
            TotalBytes: release.SelectedAsset.Size > 0 ? release.SelectedAsset.Size : null,
            ProgressPercent: 0,
            IsIndeterminate: true));

        CleanupStaleDownloads();
        Directory.CreateDirectory(_updatesDirectory);

        bool promoted = false;
        try
        {
            using var request = CreateDownloadRequest(release.SelectedAsset.BrowserDownloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !WindowsReleaseAssetSelector.IsValidHttpsUrl(finalUri.AbsoluteUri))
            {
                return new UpdateDownloadResult(
                    Succeeded: false,
                    ExpectedSha256: normalizedExpectedSha256,
                    ErrorMessage: "Refused non-HTTPS redirect during installer download.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateDownloadResult(
                    Succeeded: false,
                    ExpectedSha256: normalizedExpectedSha256,
                    ErrorMessage: $"Download failed with HTTP {(int)response.StatusCode}.");
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            if ((!totalBytes.HasValue || totalBytes.Value <= 0) && release.SelectedAsset.Size > 0)
            {
                totalBytes = release.SelectedAsset.Size;
            }

            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadPhase.Downloading,
                BytesDownloaded: 0,
                TotalBytes: totalBytes,
                ProgressPercent: 0,
                IsIndeterminate: !totalBytes.HasValue));

            long downloadedBytes = 0;
            double lastReportedPercent = -1;
            long lastReportTicks = Environment.TickCount64;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
            try
            {
                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var fileStream = new FileStream(
                    partFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: DownloadBufferSize,
                    useAsync: true);

                while (true)
                {
                    int bytesRead = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, DownloadBufferSize),
                        cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);

                    downloadedBytes += bytesRead;

                    double percent = totalBytes.HasValue && totalBytes.Value > 0
                        ? Math.Clamp((downloadedBytes * 100.0) / totalBytes.Value, 0.0, 100.0)
                        : 0.0;

                    long nowTicks = Environment.TickCount64;
                    bool shouldReport = !totalBytes.HasValue
                        ? (nowTicks - lastReportTicks) >= 120
                        : (percent - lastReportedPercent >= 0.5) || (nowTicks - lastReportTicks >= 100);

                    if (shouldReport)
                    {
                        lastReportedPercent = percent;
                        lastReportTicks = nowTicks;
                        progress?.Report(new UpdateDownloadProgress(
                            UpdateDownloadPhase.Downloading,
                            BytesDownloaded: downloadedBytes,
                            TotalBytes: totalBytes,
                            ProgressPercent: percent,
                            IsIndeterminate: !totalBytes.HasValue));
                    }
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadPhase.VerifyingIntegrity,
                BytesDownloaded: downloadedBytes,
                TotalBytes: totalBytes ?? downloadedBytes,
                ProgressPercent: 100.0,
                IsIndeterminate: true));

            var (isValid, computedSha256) = await StreamingFileVerifier.VerifyAndDeleteOnMismatchAsync(
                partFilePath,
                normalizedExpectedSha256,
                cancellationToken).ConfigureAwait(false);

            if (!isValid)
            {
                Log($"SHA-256 mismatch for '{release.SelectedAsset.Name}'. Expected: {normalizedExpectedSha256}, Computed: {computedSha256 ?? "<null>"}. Temporary file deleted.");

                progress?.Report(new UpdateDownloadProgress(
                    UpdateDownloadPhase.Failed,
                    BytesDownloaded: downloadedBytes,
                    TotalBytes: totalBytes,
                    ProgressPercent: 0,
                    IsIndeterminate: false));

                return new UpdateDownloadResult(
                    Succeeded: false,
                    ComputedSha256: computedSha256,
                    ExpectedSha256: normalizedExpectedSha256,
                    IsIntegrityMismatch: true,
                    ErrorMessage: "SHA-256 integrity verification failed. The downloaded file was deleted for your safety.");
            }

            // Promote verified .part file to final installer path ONLY after SHA-256 passes
            File.Move(partFilePath, finalFilePath, overwrite: true);
            promoted = true;

            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadPhase.ReadyToInstall,
                BytesDownloaded: downloadedBytes,
                TotalBytes: totalBytes ?? downloadedBytes,
                ProgressPercent: 100.0,
                IsIndeterminate: false));

            return new UpdateDownloadResult(
                Succeeded: true,
                VerifiedFilePath: finalFilePath,
                ComputedSha256: computedSha256,
                ExpectedSha256: normalizedExpectedSha256);
        }
        catch (OperationCanceledException)
        {
            StreamingFileVerifier.TryDeleteFile(partFilePath);
            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadPhase.Cancelled,
                BytesDownloaded: 0,
                TotalBytes: null,
                ProgressPercent: 0,
                IsIndeterminate: false));

            return new UpdateDownloadResult(
                Succeeded: false,
                ExpectedSha256: normalizedExpectedSha256,
                IsCancelled: true,
                ErrorMessage: "Update download was cancelled.");
        }
        catch (Exception ex)
        {
            StreamingFileVerifier.TryDeleteFile(partFilePath);
            Log($"Update download or verification failed: {ex.Message}");
            progress?.Report(new UpdateDownloadProgress(
                UpdateDownloadPhase.Failed,
                BytesDownloaded: 0,
                TotalBytes: null,
                ProgressPercent: 0,
                IsIndeterminate: false));

            return new UpdateDownloadResult(
                Succeeded: false,
                ExpectedSha256: normalizedExpectedSha256,
                ErrorMessage: ex.Message);
        }
        finally
        {
            if (!promoted)
            {
                StreamingFileVerifier.TryDeleteFile(partFilePath);
            }
        }
    }

    /// <inheritdoc />
    public void CleanupStaleDownloads()
    {
        try
        {
            if (!Directory.Exists(_updatesDirectory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(_updatesDirectory))
            {
                StreamingFileVerifier.TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            Log($"Non-critical error while cleaning up stale update files: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates and sanitizes an asset filename against path traversal and illegal characters,
    /// producing safe staged (<c>.part</c>) and final paths strictly inside <see cref="UpdatesDirectory"/>.
    /// </summary>
    public bool TryBuildSafeDestinationPaths(string rawAssetName, out string partFilePath, out string finalFilePath)
    {
        partFilePath = string.Empty;
        finalFilePath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawAssetName))
        {
            return false;
        }

        // Reject any explicit path separators or traversal segments before extracting filename
        if (rawAssetName.Contains('/') || rawAssetName.Contains('\\') || rawAssetName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string safeName = Path.GetFileName(rawAssetName.Trim());
        if (string.IsNullOrWhiteSpace(safeName) ||
            safeName is "." or ".." ||
            safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !WindowsReleaseAssetSelector.IsSupportedInstallerExtension(safeName))
        {
            return false;
        }

        string rootWithSep = _updatesDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _updatesDirectory
            : _updatesDirectory + Path.DirectorySeparatorChar;

        string candidateFinal = Path.GetFullPath(Path.Combine(_updatesDirectory, safeName));
        string candidatePart = Path.GetFullPath(Path.Combine(_updatesDirectory, $"{safeName}.{Guid.NewGuid():N}.part"));

        if (!candidateFinal.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
            !candidatePart.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        finalFilePath = candidateFinal;
        partFilePath = candidatePart;
        return true;
    }

    private async Task<string?> ResolveExpectedSha256Async(
        IReadOnlyList<GitHubAssetDto> releaseAssets,
        GitHubAssetDto selectedAsset,
        CancellationToken cancellationToken)
    {
        // 1. Primary source: GitHub Releases API native asset digest field
        if (Sha256ChecksumParser.TryParseAssetDigest(selectedAsset.Digest, out string digestHex))
        {
            return digestHex;
        }

        // 2. Fallback: checksum manifest or per-asset .sha256 file attached to the release
        var checksumAssets = Sha256ChecksumParser.FindChecksumAssets(releaseAssets, selectedAsset.Name);
        Exception? lastTransientNetworkError = null;
        foreach (var (checksumAsset, isAssetSpecific) in checksumAssets)
        {
            if (checksumAsset.Size > MaxChecksumFileBytes)
            {
                Log($"Skipping oversized checksum asset '{checksumAsset.Name}' ({checksumAsset.Size} bytes).");
                continue;
            }

            try
            {
                string? text = await DownloadSmallTextAssetAsync(checksumAsset.BrowserDownloadUrl, cancellationToken)
                    .ConfigureAwait(false);
                if (Sha256ChecksumParser.TryExtractHashFromChecksumText(
                        text,
                        selectedAsset.Name,
                        isAssetSpecific,
                        out string extractedHex))
                {
                    return extractedHex;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                Log($"Timed out downloading checksum asset '{checksumAsset.Name}'.");
                lastTransientNetworkError = ex;
            }
            catch (HttpRequestException ex)
            {
                Log($"Network error downloading checksum asset '{checksumAsset.Name}': {ex.Message}");
                lastTransientNetworkError = ex;
            }
            catch (Exception ex)
            {
                Log($"Failed to read checksum asset '{checksumAsset.Name}': {ex.Message}");
            }
        }

        if (lastTransientNetworkError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastTransientNetworkError).Throw();
        }

        return null;
    }

    private async Task<string?> DownloadSmallTextAssetAsync(string httpsUrl, CancellationToken cancellationToken)
    {
        if (!WindowsReleaseAssetSelector.IsValidHttpsUrl(httpsUrl))
        {
            return null;
        }

        using var checksumCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        checksumCts.CancelAfter(_metadataTimeout);
        var checksumToken = checksumCts.Token;

        using var request = CreateDownloadRequest(httpsUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            checksumToken).ConfigureAwait(false);

        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !WindowsReleaseAssetSelector.IsValidHttpsUrl(finalUri.AbsoluteUri))
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > MaxChecksumFileBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(checksumToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int totalRead = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, 4096), checksumToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > MaxChecksumFileBytes)
                {
                    return null;
                }

                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        byte[] rawBytes = memory.GetBuffer();
        int offset = 0;
        int count = (int)memory.Length;

        // Strip UTF-8 BOM (0xEF 0xBB 0xBF) if present
        if (count >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            offset = 3;
            count -= 3;
        }

        return Encoding.UTF8.GetString(rawBytes, offset, count);
    }

    private HttpRequestMessage CreateGitHubApiRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgentHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private HttpRequestMessage CreateDownloadRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgentHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private void Log(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch
        {
            // Never let logging failures break update workflows
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
