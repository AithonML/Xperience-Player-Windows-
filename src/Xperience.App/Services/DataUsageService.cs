using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.DataUsage;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Persistent, thread-safe network data usage tracker and limit enforcer backed by <see cref="IStorageService"/>.
/// </summary>
public sealed class DataUsageService : IDataUsageService
{
    private readonly IStorageService _storageService;
    private readonly ILogger<DataUsageService> _logger;
    private readonly object _gate = new();
    private DataUsageTracker _tracker = new();
    private DataUsageLimitSettings _settings = new();
    private bool _isInitialized;
    private bool _limitNotifiedForCurrentPeriod;

    public event EventHandler<string>? DataLimitReached;
    public event EventHandler? UsageChanged;

    public DataUsageLimitSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public DataUsageService(
        IStorageService storageService,
        ILogger<DataUsageService> logger)
    {
        _storageService = storageService;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            var savedSettings = await _storageService.LoadAsync<DataUsageLimitSettings>(
                DataUsageLimitSettings.StorageKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            var savedStore = await _storageService.LoadAsync<DataUsageStoreSnapshot>(
                DataUsageStoreSnapshot.StorageKey,
                defaultValue: null,
                cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (savedSettings is not null)
                {
                    _settings = savedSettings;
                }

                _tracker = new DataUsageTracker(savedStore);
                _isInitialized = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted data usage state; starting fresh.");
            lock (_gate)
            {
                _isInitialized = true;
            }
        }
    }

    public bool IsOnlineStreamingBlocked(DataUsageNetworkType networkType = DataUsageNetworkType.Wifi)
    {
        lock (_gate)
        {
            return _tracker.IsLimitReached(_settings, networkType, DateTimeOffset.UtcNow);
        }
    }

    public async Task RecordUsageAsync(
        TrackItem? track,
        DataUsageCategory category,
        long bytes,
        string? qualityLabel = null,
        string? sourceLabel = null,
        DataUsageNetworkType networkType = DataUsageNetworkType.Wifi,
        CancellationToken cancellationToken = default)
    {
        if (bytes <= 0)
        {
            return;
        }

        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        bool mutated;
        bool shouldNotifyLimit = false;
        string limitMessage = string.Empty;
        DataUsageStoreSnapshot snapshotToSave;

        lock (_gate)
        {
            bool wasBlocked = _tracker.IsLimitReached(_settings, networkType, DateTimeOffset.UtcNow);
            mutated = _tracker.RecordBytes(
                track,
                category,
                bytes,
                qualityLabel,
                sourceLabel,
                networkType,
                DateTimeOffset.UtcNow);

            bool nowBlocked = _tracker.IsLimitReached(_settings, networkType, DateTimeOffset.UtcNow);
            if (nowBlocked && (!wasBlocked || !_limitNotifiedForCurrentPeriod))
            {
                _limitNotifiedForCurrentPeriod = true;
                shouldNotifyLimit = true;
                limitMessage = $"Data usage limit ({TrackPlaybackInfo.FormatByteSize(_settings.WifiLimitBytes)}) reached. Online playback paused.";
            }

            snapshotToSave = _tracker.Snapshot;
        }

        if (mutated)
        {
            await PersistStoreSafeAsync(snapshotToSave, cancellationToken).ConfigureAwait(false);
            UsageChanged?.Invoke(this, EventArgs.Empty);
        }

        if (shouldNotifyLimit)
        {
            DataLimitReached?.Invoke(this, limitMessage);
        }
    }

    public async Task RecordStreamPlayAsync(
        TrackItem track,
        string? qualityLabel = null,
        string? sourceLabel = null,
        CancellationToken cancellationToken = default)
    {
        if (track is null)
        {
            return;
        }

        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        bool mutated;
        DataUsageStoreSnapshot snapshotToSave;
        lock (_gate)
        {
            mutated = _tracker.RecordStreamPlay(track, qualityLabel, sourceLabel, DateTimeOffset.UtcNow);
            snapshotToSave = _tracker.Snapshot;
        }

        if (mutated)
        {
            await PersistStoreSafeAsync(snapshotToSave, cancellationToken).ConfigureAwait(false);
            UsageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task MarkTrackDownloadedAsync(TrackItem track, CancellationToken cancellationToken = default)
    {
        if (track is null)
        {
            return;
        }

        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        bool mutated;
        DataUsageStoreSnapshot snapshotToSave;
        lock (_gate)
        {
            mutated = _tracker.MarkTrackDownloaded(track);
            snapshotToSave = _tracker.Snapshot;
        }

        if (mutated)
        {
            await PersistStoreSafeAsync(snapshotToSave, cancellationToken).ConfigureAwait(false);
            UsageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task UpdateSettingsAsync(DataUsageLimitSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            _settings = settings;
            if (!_tracker.IsLimitReached(_settings, DataUsageNetworkType.Wifi, DateTimeOffset.UtcNow))
            {
                _limitNotifiedForCurrentPeriod = false;
            }
        }

        try
        {
            await _storageService.SaveAsync(DataUsageLimitSettings.StorageKey, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist DataUsageLimitSettings.");
        }

        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetCurrentPeriodAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        DataUsageLimitSettings updatedSettings;
        DataUsageStoreSnapshot snapshotToSave;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _tracker.ResetCurrentPeriod(now);
            _settings = _settings with { LastManualResetUnixMs = now.ToUnixTimeMilliseconds() };
            _limitNotifiedForCurrentPeriod = false;
            updatedSettings = _settings;
            snapshotToSave = _tracker.Snapshot;
        }

        await _storageService.SaveAsync(DataUsageLimitSettings.StorageKey, updatedSettings, cancellationToken).ConfigureAwait(false);
        await PersistStoreSafeAsync(snapshotToSave, cancellationToken).ConfigureAwait(false);
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        DataUsageStoreSnapshot snapshotToSave;
        lock (_gate)
        {
            _tracker.ClearAllHistory();
            _limitNotifiedForCurrentPeriod = false;
            snapshotToSave = _tracker.Snapshot;
        }

        await PersistStoreSafeAsync(snapshotToSave, cancellationToken).ConfigureAwait(false);
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    public DataUsageSummary GetSummary()
    {
        lock (_gate)
        {
            return _tracker.BuildSummary(_settings, DateTimeOffset.UtcNow);
        }
    }

    public DataUsageAnalyticsResult QueryAnalytics(
        DataUsageChartGranularity chartGranularity,
        DataUsageDateFilter dateFilter,
        DateOnly? customStart,
        DateOnly? customEnd,
        DataUsageSortOrder sortOrder)
    {
        lock (_gate)
        {
            return _tracker.QueryAnalytics(
                DateTimeOffset.UtcNow,
                chartGranularity,
                dateFilter,
                customStart,
                customEnd,
                sortOrder);
        }
    }

    private async Task PersistStoreSafeAsync(DataUsageStoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await _storageService.SaveAsync(DataUsageStoreSnapshot.StorageKey, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist DataUsageHistory_v1.");
        }
    }
}
