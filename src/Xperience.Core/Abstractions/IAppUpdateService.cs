using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Provides GitHub Releases update detection, streaming download, and fail-closed SHA-256 integrity verification.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>
    /// Gets the compiled semantic version of the currently running application.
    /// </summary>
    SemVersion CurrentVersion { get; }

    /// <summary>
    /// Gets the formatted display string for <see cref="CurrentVersion"/> (e.g., <c>v1.0.0</c>).
    /// </summary>
    string CurrentVersionDisplay { get; }

    /// <summary>
    /// Queries GitHub Releases for the latest release, compares versions via SemVer 2.0.0,
    /// selects a compatible Windows installer asset, and resolves its expected SHA-256 checksum.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The structured check result.</returns>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the installer to the dedicated updates temp folder with progress reporting (0%–100%),
    /// computes its SHA-256 in streaming fashion, deletes the temporary file immediately on mismatch or error,
    /// and promotes the verified installer file only when integrity verification succeeds.
    /// </summary>
    /// <param name="release">The candidate release obtained from <see cref="CheckForUpdatesAsync"/>.</param>
    /// <param name="progress">Optional progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The download and verification result.</returns>
    Task<UpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes stale installer downloads and partial transfer files from the updates temporary directory.
    /// </summary>
    void CleanupStaleDownloads();
}
