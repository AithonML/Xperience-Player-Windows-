namespace Xperience.Core.Domain.Models;

/// <summary>
/// Supported target audio formats and quality tiers for offline track downloads.
/// </summary>
public enum DownloadAudioFormat
{
    /// <summary>
    /// Lossless FLAC (16-bit / 24-bit Hi-Res via Mornye Tidal/Qobuz/Deezer pipeline + hardware FLAC encoder).
    /// </summary>
    FlacLossless = 0,

    /// <summary>
    /// Maximum quality MP3 (320 kbps via YouTube Music stream + hardware MP3 encoder).
    /// </summary>
    Mp3MaxQuality = 1,

    /// <summary>
    /// Native M4A / AAC stream container from YouTube Music.
    /// </summary>
    M4aNative = 2
}

/// <summary>
/// User-configurable preferences controlling download quality, embedded metadata fields,
/// synchronized/static lyrics, and offline Apple Music Canvas video sidecars.
/// </summary>
public sealed class DownloadMetadataOptions
{
    /// <summary>
    /// Gets or sets the default audio format / quality used when downloading a track.
    /// </summary>
    public DownloadAudioFormat DefaultFormat { get; set; } = DownloadAudioFormat.FlacLossless;

    /// <summary>
    /// Gets or sets the target folder on disk where downloaded tracks and offline sidecars are stored.
    /// </summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Title, Artist, and Album Artist tags are embedded.
    /// </summary>
    public bool IncludeTitleAndArtist { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Album name, Track number, and Disc number tags are embedded.
    /// </summary>
    public bool IncludeAlbumAndTrackNumber { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether high-resolution cover artwork (1280x1280 from Tidal/Qobuz/Deezer/YTM) is embedded into the audio file.
    /// </summary>
    public bool IncludeHighResCoverArt { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Genre and Release Year / Date metadata are embedded.
    /// </summary>
    public bool IncludeGenreAndReleaseYear { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether canonical ISRC, Copyright, and Publisher tags (via Mornye Tidal/Qobuz/Deezer lookup) are embedded.
    /// </summary>
    public bool IncludeIsrcAndCopyright { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether ReplayGain and Peak loudness metadata from Tidal/Qobuz are embedded.
    /// </summary>
    public bool IncludeReplayGain { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether synchronized timestamped lyrics (.lrc sidecar + embedded tag) are downloaded and saved for offline playback.
    /// </summary>
    public bool IncludeSynchronizedLyrics { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether static plain-text lyrics are embedded when available.
    /// </summary>
    public bool IncludeStaticLyrics { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Apple Music Canvas animated cover (.canvas.mp4 sidecar) is downloaded alongside the song for offline mode.
    /// </summary>
    public bool IncludeAppleCanvasOffline { get; set; } = true;
}

/// <summary>
/// Represents the outcome of a complete track download operation (audio + tags + lyrics + Apple Canvas).
/// </summary>
public sealed record TrackDownloadResult(
    bool Success,
    string AudioFilePath,
    string? LrcFilePath,
    string? CanvasVideoFilePath,
    string FormatBadge,
    string ProviderSummary,
    string? ErrorMessage = null);

/// <summary>
/// Structured progress update emitted during a multi-stage track download.
/// </summary>
public sealed record TrackDownloadProgressReport(
    double Percent,
    string StageStatus,
    string ProviderBadge = "");

/// <summary>
/// Represents a verified track match or alternative recording version discovered across
/// Mornye's lossless/metadata providers (Tidal, Deezer, GDStudio Lossless, YouTube Music).
/// </summary>
public sealed class TrackDownloadCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public string CoverArtUri { get; init; } = string.Empty;
    public string Isrc { get; init; } = string.Empty;
    public string Copyright { get; init; } = string.Empty;
    public string ReplayGain { get; init; } = string.Empty;
    public string PeakAmplitude { get; init; } = string.Empty;
    public string Genre { get; init; } = "Pop";
    public string ProviderLabel { get; init; } = "Mornye Lossless";
    public string QualityBadge { get; init; } = "FLAC Lossless";
    public string VersionBadge { get; init; } = "Original Studio";
    public int MatchScorePercent { get; init; } = 98;
    public bool IsRecommended { get; set; }
    public string? YouTubeVideoId { get; init; }
    public string? TidalTrackId { get; init; }
    public string? QobuzTrackId { get; init; }
    public string? DeezerTrackId { get; init; }
    public string? GdStudioSource { get; init; }
    public string? GdStudioUrlId { get; init; }
    public int ReleaseYear { get; init; } = DateTime.Now.Year;
    public uint TrackNumber { get; init; } = 1;
    public uint DiscNumber { get; init; } = 1;

    public string FormattedDuration => Duration.TotalSeconds <= 0
        ? "--:--"
        : (Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss"));

    public string SubtitleSummary => string.IsNullOrWhiteSpace(Album)
        ? $"{Artist} • {FormattedDuration}"
        : $"{Artist} — {Album} ({FormattedDuration})";

    public string MatchBadgeText => IsRecommended
        ? $"★ Recommended ({MatchScorePercent}% Match)"
        : $"{MatchScorePercent}% Match • {VersionBadge}";

    public string ProviderAndIsrcText => string.IsNullOrWhiteSpace(Isrc)
        ? $"{ProviderLabel} • {QualityBadge}"
        : $"{ProviderLabel} • {QualityBadge} • ISRC {Isrc}";

    public const string FallbackTransparentCoverUri = "ms-appx:///Assets/Covers/transparent_pixel.png";

    public string SafeCoverArtUri => !string.IsNullOrWhiteSpace(CoverArtUri) ? CoverArtUri : FallbackTransparentCoverUri;

    public double CoverArtOpacity => string.IsNullOrWhiteSpace(CoverArtUri) ? 0.0 : 1.0;

    public double DefaultCoverIconOpacity => string.IsNullOrWhiteSpace(CoverArtUri) ? 1.0 : 0.0;
}
