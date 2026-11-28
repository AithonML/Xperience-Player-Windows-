using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Tracks permanent, lightweight network data usage per normalized track key and enforces user-defined data limits.
/// </summary>
public interface IDataUsageService
{
    /// <summary>
    /// Raised when a network data limit is reached and online playback must pause/stop.
    /// </summary>
    event EventHandler<string>? DataLimitReached;

    /// <summary>
    /// Raised whenever usage counters or limit settings change so UI views can refresh.
    /// </summary>
    event EventHandler? UsageChanged;

    /// <summary>
    /// Gets the current persisted data usage limit settings.
    /// </summary>
    DataUsageLimitSettings Settings { get; }

    /// <summary>
    /// Loads persisted data usage history and limit settings from storage.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if online streaming is currently blocked because the configured data limit has been reached.
    /// Local and downloaded offline tracks are never blocked.
    /// </summary>
    bool IsOnlineStreamingBlocked(DataUsageNetworkType networkType = DataUsageNetworkType.Wifi);

    /// <summary>
    /// Records external network bytes consumed for a track (audio, artwork, lyrics, or Apple Canvas).
    /// Local tracks are only recorded when <paramref name="bytes"/> &gt; 0.
    /// </summary>
    Task RecordUsageAsync(
        TrackItem? track,
        DataUsageCategory category,
        long bytes,
        string? qualityLabel = null,
        string? sourceLabel = null,
        DataUsageNetworkType networkType = DataUsageNetworkType.Wifi,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an online stream play count increment for smart download suggestions.
    /// </summary>
    Task RecordStreamPlayAsync(
        TrackItem track,
        string? qualityLabel = null,
        string? sourceLabel = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a previously streamed track as downloaded so it is no longer suggested for download.
    /// </summary>
    Task MarkTrackDownloadedAsync(TrackItem track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates and persists data usage limit settings.
    /// </summary>
    Task UpdateSettingsAsync(DataUsageLimitSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the current period's usage counters (for limit tracking) while keeping permanent history intact.
    /// </summary>
    Task ResetCurrentPeriodAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all stored data usage history.
    /// </summary>
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the current summary, per-song analysis (filtering local tracks to only those with external data &gt; 0),
    /// and smart download suggestions.
    /// </summary>
    DataUsageSummary GetSummary();

    /// <summary>
    /// Computes filtered Data Usage analytics: time-series chart points (Day/Week/Month),
    /// filtered category totals, and sorted per-song consumption rows.
    /// </summary>
    DataUsageAnalyticsResult QueryAnalytics(
        DataUsageChartGranularity chartGranularity,
        DataUsageDateFilter dateFilter,
        DateOnly? customStart,
        DateOnly? customEnd,
        DataUsageSortOrder sortOrder);
}
