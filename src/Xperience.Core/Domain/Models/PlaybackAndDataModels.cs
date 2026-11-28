using System.Text.Json.Serialization;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Persisted playback quality, gapless preloading, and volume-behavior preferences.
/// </summary>
public sealed record PlaybackBehaviorSettings
{
    public const string StorageKey = "PlaybackBehaviorSettings_v1";

    /// <summary>
    /// Selected online streaming audio quality tier on Windows (defaults to High 160kbps / max YT Music).
    /// </summary>
    public StreamingAudioQuality OnlineQuality { get; init; } = StreamingAudioQuality.High160;

    /// <summary>
    /// Enables preloading the next track in the queue before the current track ends (enabled by default).
    /// </summary>
    public bool GaplessPreloadEnabled { get; init; } = true;

    /// <summary>
    /// Automatically pauses playback when volume drops to 0 and resumes when volume rises above 0 (disabled by default).
    /// </summary>
    public bool PauseAtZeroVolumeEnabled { get; init; } = false;
}

/// <summary>
/// Certified stream resolution payload returned by YouTube Music or direct FLAC lossless resolvers.
/// </summary>
public sealed record ResolvedPlaybackStream(
    string PlaybackUri,
    PlaybackSourceKind SourceKind,
    string ProviderLabel,
    StreamingAudioQuality RequestedQuality,
    StreamingAudioQuality ActualQuality,
    bool IsFlacFallback = false,
    string? FallbackReason = null,
    string Codec = "",
    int BitrateKbps = 0,
    int SampleRateHz = 0,
    int ChannelCount = 0,
    int BitDepth = 0,
    long FileSizeBytes = 0,
    string Isrc = "",
    long DownloadedNetworkBytes = 0)
{
    public string FormatQualityLabel()
    {
        if (SourceKind is PlaybackSourceKind.FlacGdStudio or PlaybackSourceKind.FlacTidal or PlaybackSourceKind.FlacQobuz ||
            string.Equals(Codec, "FLAC", StringComparison.OrdinalIgnoreCase))
        {
            string depthPart = BitDepth > 0 ? $"{BitDepth}-bit" : "Lossless";
            string ratePart = SampleRateHz > 0 ? $"/{SampleRateHz / 1000.0:0.#} kHz" : string.Empty;
            string brPart = BitrateKbps > 0 ? $" • {BitrateKbps} kbps" : string.Empty;
            return $"FLAC {depthPart}{ratePart}{brPart}";
        }

        string codecLabel = string.IsNullOrWhiteSpace(Codec) ? "AAC" : Codec.ToUpperInvariant();
        if (BitrateKbps > 0)
        {
            return $"{codecLabel} {BitrateKbps} kbps";
        }

        return ActualQuality switch
        {
            StreamingAudioQuality.Low64 => $"{codecLabel} 64 kbps",
            StreamingAudioQuality.Regular128 => $"{codecLabel} 128 kbps",
            StreamingAudioQuality.High160 => $"{codecLabel} 160 kbps",
            StreamingAudioQuality.FlacLossless => "FLAC Lossless",
            _ => codecLabel
        };
    }
}

/// <summary>
/// Real-time metadata and certified audio quality snapshot for the Song Information view.
/// </summary>
public sealed record TrackPlaybackInfo(
    string TrackId,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    PlaybackSourceKind SourceKind,
    string SourceDisplayLabel,
    string ProviderLabel,
    StreamingAudioQuality RequestedQuality,
    string ActualQualityLabel,
    bool IsFlacFallback,
    string? FallbackReason,
    string Codec,
    int BitrateKbps,
    int SampleRateHz,
    int ChannelCount,
    int BitDepth,
    long FileSizeBytes,
    string FilePathOrUri,
    string Isrc,
    int ReleaseYear,
    string Genre,
    uint TrackNumber,
    string LyricsProviderName)
{
    public string FormattedDuration => $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";

    public string FormattedSampleRate => SampleRateHz > 0 ? $"{SampleRateHz / 1000.0:0.#} kHz ({SampleRateHz} Hz)" : "—";

    public string FormattedBitrate => BitrateKbps > 0 ? $"{BitrateKbps} kbps" : "—";

    public string FormattedBitDepth => BitDepth > 0 ? $"{BitDepth}-bit" : "—";

    public string FormattedChannels => ChannelCount switch
    {
        1 => "Mono (1 ch)",
        2 => "Stereo (2 ch)",
        > 2 => $"{ChannelCount} ch",
        _ => "—"
    };

    public string FormattedFileSize => FormatByteSize(FileSizeBytes);

    public static string FormatByteSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.00} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0.0} KB";
        }

        return $"{bytes} B";
    }
}

/// <summary>
/// Persisted network data usage restriction settings (aligned with Android schema).
/// </summary>
public sealed record DataUsageLimitSettings
{
    public const string StorageKey = "DataUsageLimitSettings_v1";

    public bool WifiLimitEnabled { get; init; } = false;

    public long WifiLimitBytes { get; init; } = 2L * 1024 * 1024 * 1024; // 2 GB default

    /// <summary>
    /// Retained in shared cross-platform schema (Android-only in UI).
    /// </summary>
    public bool MobileLimitEnabled { get; init; } = false;

    /// <summary>
    /// Retained in shared cross-platform schema (Android-only in UI).
    /// </summary>
    public long MobileLimitBytes { get; init; } = 500L * 1024 * 1024;

    public DataUsageLimitPeriod Period { get; init; } = DataUsageLimitPeriod.Monthly;

    public long LastManualResetUnixMs { get; init; }
}

/// <summary>
/// Compact normalized track reference stored once per unique track key in permanent data usage history.
/// </summary>
public sealed record DataUsageTrackRef
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public bool IsLocal { get; init; }
    public bool IsDownloaded { get; init; }
    public string YouTubeVideoId { get; init; } = string.Empty;
}

/// <summary>
/// Compact permanent byte-counter record for a single normalized track key.
/// </summary>
public sealed class DataUsageTrackRecord
{
    public string TrackKey { get; set; } = string.Empty;
    public long WifiAudioBytes { get; set; }
    public long WifiArtworkBytes { get; set; }
    public long WifiLyricsBytes { get; set; }
    public long WifiCanvasBytes { get; set; }
    public long MobileAudioBytes { get; set; }
    public long MobileArtworkBytes { get; set; }
    public long MobileLyricsBytes { get; set; }
    public long MobileCanvasBytes { get; set; }
    public int StreamPlayCount { get; set; }
    public string LastQualityLabel { get; set; } = string.Empty;
    public string LastSourceLabel { get; set; } = string.Empty;
    public long LastPlayedUnixMs { get; set; }

    [JsonIgnore]
    public long TotalWifiBytes => WifiAudioBytes + WifiArtworkBytes + WifiLyricsBytes + WifiCanvasBytes;

    [JsonIgnore]
    public long TotalMobileBytes => MobileAudioBytes + MobileArtworkBytes + MobileLyricsBytes + MobileCanvasBytes;

    [JsonIgnore]
    public long TotalAudioBytes => WifiAudioBytes + MobileAudioBytes;

    [JsonIgnore]
    public long TotalArtworkBytes => WifiArtworkBytes + MobileArtworkBytes;

    [JsonIgnore]
    public long TotalLyricsBytes => WifiLyricsBytes + MobileLyricsBytes;

    [JsonIgnore]
    public long TotalCanvasBytes => WifiCanvasBytes + MobileCanvasBytes;

    [JsonIgnore]
    public long TotalBytes => TotalWifiBytes + TotalMobileBytes;
}

/// <summary>
/// Daily aggregate bucket for fast Daily/Weekly/Monthly period limit and chart calculations.
/// </summary>
public sealed class DataUsageDailyBucket
{
    public string DateKey { get; set; } = string.Empty; // yyyy-MM-dd (UTC)
    public long WifiBytes { get; set; }
    public long MobileBytes { get; set; }
    public long AudioBytes { get; set; }
    public long ArtworkBytes { get; set; }
    public long LyricsBytes { get; set; }
    public long CanvasBytes { get; set; }
}

/// <summary>
/// Compact per-day, per-track consumption ledger enabling exact date-range filtering without losing legacy totals.
/// </summary>
public sealed class DataUsageDailyTrackRecord
{
    public long WifiAudioBytes { get; set; }
    public long WifiArtworkBytes { get; set; }
    public long WifiLyricsBytes { get; set; }
    public long WifiCanvasBytes { get; set; }
    public long MobileAudioBytes { get; set; }
    public long MobileArtworkBytes { get; set; }
    public long MobileLyricsBytes { get; set; }
    public long MobileCanvasBytes { get; set; }
    public int StreamPlayCount { get; set; }
    public long LastPlayedUnixMs { get; set; }

    [JsonIgnore]
    public long TotalWifiBytes => WifiAudioBytes + WifiArtworkBytes + WifiLyricsBytes + WifiCanvasBytes;

    [JsonIgnore]
    public long TotalMobileBytes => MobileAudioBytes + MobileArtworkBytes + MobileLyricsBytes + MobileCanvasBytes;

    [JsonIgnore]
    public long TotalAudioBytes => WifiAudioBytes + MobileAudioBytes;

    [JsonIgnore]
    public long TotalArtworkBytes => WifiArtworkBytes + MobileArtworkBytes;

    [JsonIgnore]
    public long TotalLyricsBytes => WifiLyricsBytes + MobileLyricsBytes;

    [JsonIgnore]
    public long TotalCanvasBytes => WifiCanvasBytes + MobileCanvasBytes;

    [JsonIgnore]
    public long TotalBytes => TotalWifiBytes + TotalMobileBytes;
}

/// <summary>
/// Persisted standalone JSON store for lightweight permanent data usage history.
/// </summary>
public sealed class DataUsageStoreSnapshot
{
    public const string StorageKey = "DataUsageHistory_v1";

    public Dictionary<string, DataUsageTrackRef> TrackRefs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DataUsageTrackRecord> TrackRecords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DataUsageDailyBucket> DailyBuckets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, DataUsageDailyTrackRecord>> DailyTrackRecords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long TotalResetBaselineWifiBytes { get; set; }
    public long TotalResetBaselineMobileBytes { get; set; }
    public Dictionary<string, long> PeriodResetDayBaselineWifiBytes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> PeriodResetDayBaselineMobileBytes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Single bar/point in the interactive Data Usage history chart.
/// </summary>
public sealed record DataUsageChartPoint(
    string Label,
    string TooltipTitle,
    DateOnly StartDate,
    DateOnly EndDate,
    long TotalBytes,
    long AudioBytes,
    long ArtworkBytes,
    long LyricsBytes,
    long CanvasBytes)
{
    public string FormattedTotalBytes => TrackPlaybackInfo.FormatByteSize(TotalBytes);
}

/// <summary>
/// Filtered and sorted Data Usage analytics result for charts, filtered breakdown, and song list.
/// </summary>
public sealed record DataUsageAnalyticsResult(
    IReadOnlyList<DataUsageChartPoint> ChartPoints,
    long FilteredTotalBytes,
    long FilteredAudioBytes,
    long FilteredArtworkBytes,
    long FilteredLyricsBytes,
    long FilteredCanvasBytes,
    IReadOnlyList<DataUsageSongAnalysisItem> SongAnalysis)
{
    public string FormattedFilteredTotal => TrackPlaybackInfo.FormatByteSize(FilteredTotalBytes);
}

/// <summary>
/// Projection of a single track's network consumption for the Detailed Analysis UI.
/// </summary>
public sealed record DataUsageSongAnalysisItem(
    string TrackKey,
    string Title,
    string Artist,
    string Album,
    bool IsLocal,
    bool IsDownloaded,
    string YouTubeVideoId,
    int StreamPlayCount,
    long TotalBytes,
    long WifiBytes,
    long MobileBytes,
    long AudioBytes,
    long ArtworkBytes,
    long LyricsBytes,
    long CanvasBytes,
    string LastQualityLabel,
    string LastSourceLabel,
    long LastPlayedUnixMs,
    bool ShowAdvanced = false)
{
    public string FormattedTotalBytes => TrackPlaybackInfo.FormatByteSize(TotalBytes);
    public string FormattedAudioBytes => TrackPlaybackInfo.FormatByteSize(AudioBytes);
    public string FormattedArtworkBytes => TrackPlaybackInfo.FormatByteSize(ArtworkBytes);
    public string FormattedLyricsBytes => TrackPlaybackInfo.FormatByteSize(LyricsBytes);
    public string FormattedCanvasBytes => TrackPlaybackInfo.FormatByteSize(CanvasBytes);
    public string Subtitle => string.IsNullOrWhiteSpace(LastQualityLabel)
        ? Artist
        : $"{Artist} • {LastQualityLabel}";
    public string FormattedLastPlayed => LastPlayedUnixMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(LastPlayedUnixMs).ToLocalTime().ToString("MMM d, yyyy HH:mm")
        : string.Empty;
    public string SourceBadge => IsLocal ? "LOCAL (EXT DATA)" : (string.IsNullOrWhiteSpace(LastSourceLabel) ? "ONLINE" : LastSourceLabel);
}

/// <summary>
/// Smart suggestion encouraging the user to download frequently streamed songs.
/// </summary>
public sealed record DataUsageDownloadSuggestionItem(
    string TrackKey,
    string Title,
    string Artist,
    string Album,
    string YouTubeVideoId,
    int StreamPlayCount,
    long TotalBytes,
    string LastQualityLabel)
{
    public string FormattedBytes => TrackPlaybackInfo.FormatByteSize(TotalBytes);
    public string ReasonSummary => $"{StreamPlayCount} streams • {FormattedBytes} used";
}

/// <summary>
/// Aggregated Data Usage statistics &amp; projections for Settings -> Data Usage UI.
/// </summary>
public sealed record DataUsageSummary(
    long PeriodWifiBytes,
    long PeriodMobileBytes,
    long AllTimeWifiBytes,
    long AllTimeMobileBytes,
    long TotalAudioBytes,
    long TotalArtworkBytes,
    long TotalLyricsBytes,
    long TotalCanvasBytes,
    bool WifiLimitEnabled,
    long WifiLimitBytes,
    bool IsWifiLimitReached,
    DataUsageLimitPeriod Period,
    IReadOnlyList<DataUsageSongAnalysisItem> SongAnalysis,
    IReadOnlyList<DataUsageDownloadSuggestionItem> DownloadSuggestions)
{
    public string FormattedPeriodWifi => TrackPlaybackInfo.FormatByteSize(PeriodWifiBytes);
    public string FormattedAllTimeWifi => TrackPlaybackInfo.FormatByteSize(AllTimeWifiBytes);
    public string FormattedWifiLimit => TrackPlaybackInfo.FormatByteSize(WifiLimitBytes);
    public double LimitProgressPercent => WifiLimitEnabled && WifiLimitBytes > 0
        ? Math.Clamp((PeriodWifiBytes * 100.0) / WifiLimitBytes, 0.0, 100.0)
        : 0.0;
}

/// <summary>
/// Per-folder Blacklist or Whitelist scanning rule with optional shallow/deep override.
/// </summary>
public sealed record FolderScanRule
{
    public string FolderPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public FolderScanListType ListType { get; init; } = FolderScanListType.Blacklist;
    public FolderScanDepthMode DepthMode { get; init; } = FolderScanDepthMode.InheritGlobal;

    [JsonIgnore]
    public bool IsBlacklist => ListType == FolderScanListType.Blacklist;

    [JsonIgnore]
    public bool IsWhitelist => ListType == FolderScanListType.Whitelist;

    [JsonIgnore]
    public string ListBadgeText => IsBlacklist ? "BLACKLIST" : "WHITELIST";

    [JsonIgnore]
    public string DepthBadgeText => DepthMode switch
    {
        FolderScanDepthMode.Shallow => "SHALLOW",
        FolderScanDepthMode.Deep => "DEEP",
        _ => "INHERIT"
    };
}

/// <summary>
/// Persisted local music scanner filter configuration (Blacklist/Whitelist + global/per-folder depth).
/// </summary>
public sealed record LocalScanFilterSettings
{
    public const string StorageKey = "LocalScanFilterSettings_v1";

    public LocalScanFilterMode FilterMode { get; init; } = LocalScanFilterMode.All;

    /// <summary>
    /// Global scan depth default: true = Deep (include subfolders), false = Shallow (folder only).
    /// </summary>
    public bool GlobalDeepScan { get; init; } = true;

    public List<FolderScanRule> Rules { get; init; } = [];
}

/// <summary>
/// Persisted active queue, song, progress, and transport state restored when the app reopens.
/// </summary>
public sealed record PersistedPlaybackSessionState
{
    public const string StorageKey = "PlaybackSessionState_v1";

    public List<TrackItem> QueueItems { get; init; } = [];
    public List<TrackItem> CanonicalOrder { get; init; } = [];
    public int CurrentIndex { get; init; } = -1;
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public bool IsShuffleEnabled { get; init; }
    public PlaybackRepeatMode RepeatMode { get; init; } = PlaybackRepeatMode.None;
    public double Volume { get; init; } = 0.85;
    public bool IsMuted { get; init; }
    public long SavedAtUnixMs { get; init; }
}
