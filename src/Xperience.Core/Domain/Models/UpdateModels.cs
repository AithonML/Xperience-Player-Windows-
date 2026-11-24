using System.Text.Json.Serialization;
using Xperience.Core.Updates;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Persisted settings for automatic GitHub Releases update checking.
/// Stored inside <c>user_settings.json</c> under <see cref="StorageKey"/>.
/// </summary>
public sealed record UpdateSettings
{
    /// <summary>
    /// Canonical key used to persist <see cref="UpdateSettings"/> inside <c>user_settings.json</c>.
    /// </summary>
    public const string StorageKey = "AppUpdateSettings_v1";

    /// <summary>
    /// Gets a value indicating whether the app silently checks for updates on startup.
    /// Defaults to <c>true</c> (ON), including when migrating existing settings files that lack this key.
    /// </summary>
    public bool AutoCheckUpdatesOnStartup { get; init; } = true;
}

/// <summary>
/// Represents a Semantic Version (SemVer 2.0.0) with optional pre-release identifiers and build metadata.
/// </summary>
public sealed record SemVersion : IComparable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }

    public SemVersion(int major, int minor, int patch, string? preRelease = null, string? buildMetadata = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);

        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease.Trim();
        BuildMetadata = string.IsNullOrWhiteSpace(buildMetadata) ? null : buildMetadata.Trim();
    }

    /// <summary>
    /// Gets a value indicating whether this version carries pre-release identifiers.
    /// </summary>
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    /// <summary>
    /// Gets the canonical SemVer string without leading 'v' (excluding build metadata unless present).
    /// </summary>
    public string NormalizedString => PreRelease is not null
        ? $"{Major}.{Minor}.{Patch}-{PreRelease}"
        : $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// Gets a display tag formatted with leading 'v' (e.g. <c>v1.0.0</c>).
    /// </summary>
    public string DisplayTag => $"v{NormalizedString}";

    /// <inheritdoc />
    public int CompareTo(SemVersion? other) => SemVerComparer.Compare(this, other);

    /// <inheritdoc />
    public bool Equals(SemVersion? other) => other is not null && SemVerComparer.Compare(this, other) == 0;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <inheritdoc />
    public override string ToString() => BuildMetadata is not null
        ? $"{NormalizedString}+{BuildMetadata}"
        : NormalizedString;

    public static bool operator <(SemVersion? left, SemVersion? right) => SemVerComparer.Compare(left, right) < 0;
    public static bool operator <=(SemVersion? left, SemVersion? right) => SemVerComparer.Compare(left, right) <= 0;
    public static bool operator >(SemVersion? left, SemVersion? right) => SemVerComparer.Compare(left, right) > 0;
    public static bool operator >=(SemVersion? left, SemVersion? right) => SemVerComparer.Compare(left, right) >= 0;
}

/// <summary>
/// GitHub Releases API response DTO for <c>/repos/{owner}/{repo}/releases/latest</c>.
/// </summary>
public sealed record GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAssetDto> Assets { get; init; } = [];
}

/// <summary>
/// GitHub Release asset metadata DTO including optional SHA-256 <c>digest</c>.
/// </summary>
public sealed record GitHubAssetDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

/// <summary>
/// Describes the outcome of checking GitHub Releases for a newer application version.
/// </summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer verified-checksum release is available for this platform and architecture.</summary>
    UpdateAvailable = 0,

    /// <summary>The installed version is already equal to or newer than the latest published release.</summary>
    UpToDate = 1,

    /// <summary>No published release exists yet (GitHub returned 404 or only draft releases).</summary>
    NoPublishedReleases = 2,

    /// <summary>A newer version exists, but no compatible Windows installer asset matches this architecture.</summary>
    NoCompatibleAsset = 3,

    /// <summary>A newer version and asset exist, but no valid SHA-256 digest or checksum entry was found (fail-closed).</summary>
    MissingChecksum = 4,

    /// <summary>A network, timeout, or HTTP communication error prevented checking for updates.</summary>
    NetworkError = 5,

    /// <summary>An unexpected parsing or application error occurred during the update check.</summary>
    Error = 6
}

/// <summary>
/// Represents a verified candidate update ready to be presented to the user and downloaded.
/// </summary>
public sealed record UpdateReleaseInfo(
    SemVersion Version,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    GitHubAssetDto SelectedAsset,
    string ExpectedSha256);

/// <summary>
/// Result returned by <see cref="Abstractions.IAppUpdateService.CheckForUpdatesAsync"/>.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    SemVersion CurrentVersion,
    SemVersion? LatestVersion = null,
    UpdateReleaseInfo? AvailableUpdate = null,
    string? ErrorMessage = null);

/// <summary>
/// Phases reported during streaming download and cryptographic verification of an update installer.
/// </summary>
public enum UpdateDownloadPhase
{
    CleaningTemp = 0,
    Downloading = 1,
    VerifyingIntegrity = 2,
    ReadyToInstall = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Progress snapshot reported during streaming download and SHA-256 verification.
/// </summary>
public sealed record UpdateDownloadProgress(
    UpdateDownloadPhase Phase,
    long BytesDownloaded,
    long? TotalBytes,
    double ProgressPercent,
    bool IsIndeterminate);

/// <summary>
/// Result returned by <see cref="Abstractions.IAppUpdateService.DownloadAndVerifyUpdateAsync"/>.
/// </summary>
public sealed record UpdateDownloadResult(
    bool Succeeded,
    string? VerifiedFilePath = null,
    string? ComputedSha256 = null,
    string? ExpectedSha256 = null,
    bool IsIntegrityMismatch = false,
    bool IsCancelled = false,
    string? ErrorMessage = null);
