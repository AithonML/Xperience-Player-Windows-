using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _isHydratingPlaybackAndDataSettings;
    private readonly HashSet<string> _recordedArtworkSessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recordedCanvasSessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly DateTime ProcessStartUtc = GetProcessStartUtcSafe();

    private static DateTime GetProcessStartUtcSafe()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    // -------------------------------------------------------------------------
    // Settings Category Visibilities (Playback & Quality + Data Usage)
    // -------------------------------------------------------------------------
    public Visibility SettingsPlaybackCategoryVisibility =>
        SelectedSettingsCategory == SettingsCategory.PlaybackAndQuality ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SettingsDataUsageCategoryVisibility =>
        SelectedSettingsCategory == SettingsCategory.DataUsage ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSettingsPlaybackActive => SelectedSettingsCategory == SettingsCategory.PlaybackAndQuality;
    public bool IsSettingsDataUsageActive => SelectedSettingsCategory == SettingsCategory.DataUsage;

    public double SettingsCatPlaybackActiveOpacity => SelectedSettingsCategory == SettingsCategory.PlaybackAndQuality ? 1.0 : 0.0;
    public double SettingsCatDataUsageActiveOpacity => SelectedSettingsCategory == SettingsCategory.DataUsage ? 1.0 : 0.0;

    // -------------------------------------------------------------------------
    // Feature 1 & 3 & 4a: Streaming Quality, Gapless Preload, Pause at 0 Volume
    // -------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStreamingQuality))]
    [NotifyPropertyChangedFor(nameof(StreamingQualityDescription))]
    [NotifyPropertyChangedFor(nameof(FlacFallbackHintVisibility))]
    public partial int SelectedStreamingQualityIndex { get; set; } = (int)StreamingAudioQuality.High160;

    [ObservableProperty]
    public partial bool GaplessPreloadEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool PauseAtZeroVolumeEnabled { get; set; } = false;

    public StreamingAudioQuality SelectedStreamingQuality =>
        Enum.IsDefined(typeof(StreamingAudioQuality), SelectedStreamingQualityIndex)
            ? (StreamingAudioQuality)SelectedStreamingQualityIndex
            : StreamingAudioQuality.High160;

    public Visibility FlacFallbackHintVisibility =>
        SelectedStreamingQuality == StreamingAudioQuality.FlacLossless ? Visibility.Visible : Visibility.Collapsed;

    public string StreamingQualityDescription => SelectedStreamingQuality switch
    {
        StreamingAudioQuality.Low64 => "Low (64 kbps) — Saves bandwidth with lightweight YouTube Music audio.",
        StreamingAudioQuality.Regular128 => "Regular (128 kbps) — Balanced everyday YouTube Music audio quality.",
        StreamingAudioQuality.High160 => "High (160 kbps / max YT Music) — Highest standard YouTube Music stream.",
        StreamingAudioQuality.FlacLossless => "FLAC Lossless — Direct verified lossless FLAC stream. Automatically falls back to YouTube Music High if unavailable.",
        _ => string.Empty
    };

    partial void OnSelectedStreamingQualityIndexChanged(int value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistPlaybackBehaviorSettingsAsync();
        }
    }

    partial void OnGaplessPreloadEnabledChanged(bool value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistPlaybackBehaviorSettingsAsync();
        }
    }

    partial void OnPauseAtZeroVolumeEnabledChanged(bool value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistPlaybackBehaviorSettingsAsync();
        }
    }

    private async Task HydratePlaybackAndDataSettingsAsync()
    {
        _isHydratingPlaybackAndDataSettings = true;
        try
        {
            var behavior = await _storageService.LoadAsync<PlaybackBehaviorSettings>(
                PlaybackBehaviorSettings.StorageKey,
                defaultValue: new PlaybackBehaviorSettings());
            behavior ??= new PlaybackBehaviorSettings();

            SelectedStreamingQualityIndex = (int)behavior.OnlineQuality;
            GaplessPreloadEnabled = behavior.GaplessPreloadEnabled;
            PauseAtZeroVolumeEnabled = behavior.PauseAtZeroVolumeEnabled;
            _audioPlayerService.BehaviorSettings = behavior;

            await _dataUsageService.InitializeAsync();
            var limitSettings = _dataUsageService.Settings;
            DataLimitEnabled = limitSettings.WifiLimitEnabled;
            DataLimitValueMb = Math.Max(10, Math.Round(limitSettings.WifiLimitBytes / (1024.0 * 1024.0), 0));
            SelectedDataLimitPeriodIndex = (int)limitSettings.Period;

            SyncLocalScanFilterFromService();
            RefreshDataUsageSummary();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hydrate playback & data settings.");
        }
        finally
        {
            _isHydratingPlaybackAndDataSettings = false;
        }
    }

    private async Task PersistPlaybackBehaviorSettingsAsync()
    {
        var updated = new PlaybackBehaviorSettings
        {
            OnlineQuality = SelectedStreamingQuality,
            GaplessPreloadEnabled = GaplessPreloadEnabled,
            PauseAtZeroVolumeEnabled = PauseAtZeroVolumeEnabled
        };

        _audioPlayerService.BehaviorSettings = updated;
        await _storageService.SaveAsync(PlaybackBehaviorSettings.StorageKey, updated);
    }

    // -------------------------------------------------------------------------
    // Feature 1b: Song Information Modal (Real Metadata + Certified Quality)
    // -------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SongInfoModalVisibility))]
    public partial bool IsSongInfoModalOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SongInfoTitle))]
    [NotifyPropertyChangedFor(nameof(SongInfoArtist))]
    [NotifyPropertyChangedFor(nameof(SongInfoAlbum))]
    [NotifyPropertyChangedFor(nameof(SongInfoDuration))]
    [NotifyPropertyChangedFor(nameof(SongInfoSource))]
    [NotifyPropertyChangedFor(nameof(SongInfoQuality))]
    [NotifyPropertyChangedFor(nameof(SongInfoCodec))]
    [NotifyPropertyChangedFor(nameof(SongInfoBitrate))]
    [NotifyPropertyChangedFor(nameof(SongInfoSampleRate))]
    [NotifyPropertyChangedFor(nameof(SongInfoBitDepth))]
    [NotifyPropertyChangedFor(nameof(SongInfoChannels))]
    [NotifyPropertyChangedFor(nameof(SongInfoFileSize))]
    [NotifyPropertyChangedFor(nameof(SongInfoPathOrUri))]
    [NotifyPropertyChangedFor(nameof(SongInfoIsrc))]
    [NotifyPropertyChangedFor(nameof(SongInfoYear))]
    [NotifyPropertyChangedFor(nameof(SongInfoGenre))]
    [NotifyPropertyChangedFor(nameof(SongInfoLyricsProvider))]
    [NotifyPropertyChangedFor(nameof(SongInfoFlacFallbackVisibility))]
    [NotifyPropertyChangedFor(nameof(SongInfoFlacFallbackText))]
    public partial TrackPlaybackInfo? ActiveSongInfo { get; set; }

    [ObservableProperty]
    public partial string CurrentPlaybackQualityBadge { get; set; } = string.Empty;

    public Visibility SongInfoModalVisibility => IsSongInfoModalOpen ? Visibility.Visible : Visibility.Collapsed;
    public string SongInfoTitle => ActiveSongInfo?.Title ?? CurrentTrack?.Title ?? "—";
    public string SongInfoArtist => ActiveSongInfo?.Artist ?? CurrentTrack?.Artist ?? "—";
    public string SongInfoAlbum => ActiveSongInfo?.Album ?? CurrentTrack?.Album ?? "—";
    public string SongInfoDuration => ActiveSongInfo?.FormattedDuration ?? CurrentTrack?.FormattedDuration ?? "0:00";
    public string SongInfoSource => ActiveSongInfo?.SourceDisplayLabel ?? "—";
    public string SongInfoQuality => ActiveSongInfo?.ActualQualityLabel ?? "—";
    public string SongInfoCodec => ActiveSongInfo?.Codec ?? "—";
    public string SongInfoBitrate => ActiveSongInfo?.FormattedBitrate ?? "—";
    public string SongInfoSampleRate => ActiveSongInfo?.FormattedSampleRate ?? "—";
    public string SongInfoBitDepth => ActiveSongInfo?.FormattedBitDepth ?? "—";
    public string SongInfoChannels => ActiveSongInfo?.FormattedChannels ?? "—";
    public string SongInfoFileSize => ActiveSongInfo?.FormattedFileSize ?? "—";
    public string SongInfoPathOrUri => ActiveSongInfo?.FilePathOrUri ?? "—";
    public string SongInfoIsrc => string.IsNullOrWhiteSpace(ActiveSongInfo?.Isrc) ? "—" : ActiveSongInfo!.Isrc;
    public string SongInfoYear => ActiveSongInfo is { ReleaseYear: > 0 } ? ActiveSongInfo.ReleaseYear.ToString() : "—";
    public string SongInfoGenre => string.IsNullOrWhiteSpace(ActiveSongInfo?.Genre) ? "—" : ActiveSongInfo!.Genre;
    public string SongInfoLyricsProvider => string.IsNullOrWhiteSpace(ActiveSongInfo?.LyricsProviderName)
        ? (string.IsNullOrWhiteSpace(CurrentLyricsProviderBadge) ? "—" : CurrentLyricsProviderBadge)
        : ActiveSongInfo!.LyricsProviderName;
    public Visibility SongInfoFlacFallbackVisibility =>
        ActiveSongInfo?.IsFlacFallback == true ? Visibility.Visible : Visibility.Collapsed;
    public string SongInfoFlacFallbackText =>
        ActiveSongInfo?.FallbackReason ?? "FLAC Lossless stream unavailable; playing YouTube Music High.";

    private void OnCurrentTrackPlaybackInfoChanged(object? sender, TrackPlaybackInfo? info)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            ActiveSongInfo = info;
            if (info is not null)
            {
                CurrentPlaybackQualityBadge = info.IsFlacFallback
                    ? $"{info.ActualQualityLabel} (FLAC Fallback)"
                    : info.ActualQualityLabel;
            }
            else
            {
                CurrentPlaybackQualityBadge = string.Empty;
            }
        });
    }

    [RelayCommand]
    public void OpenSongInfo(TrackItem? targetTrack = null)
    {
        var track = targetTrack ?? CurrentTrack;
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PopulateActiveSongInfoForTrack(track);
        ContextMenuTrack = track;
        IsTrackInfoModalOpen = true;
    }

    internal void PopulateActiveSongInfoForTrack(TrackItem? track)
    {
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            ActiveSongInfo = null;
            return;
        }

        var liveInfo = _audioPlayerService.CurrentTrackPlaybackInfo;
        if (liveInfo is not null && string.Equals(liveInfo.TrackId, track.Id, StringComparison.OrdinalIgnoreCase))
        {
            ActiveSongInfo = liveInfo with
            {
                LyricsProviderName = string.IsNullOrWhiteSpace(liveInfo.LyricsProviderName)
                    ? (CurrentLyricsProviderBadge ?? string.Empty)
                    : liveInfo.LyricsProviderName
            };
        }
        else
        {
            ActiveSongInfo = BuildFallbackTrackInfo(track);
        }
    }

    [RelayCommand]
    public void CloseSongInfo()
    {
        IsSongInfoModalOpen = false;
        IsTrackInfoModalOpen = false;
    }

    private TrackPlaybackInfo BuildFallbackTrackInfo(TrackItem track)
    {
        bool isLocal = track.SourceType == MediaSourceType.Local ||
                       (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath));
        string path = !string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath)
            ? track.LocalOfflineFilePath
            : track.AudioSourceUri;

        long fileSize = 0;
        string codec = isLocal ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() : "AAC";
        int bitrate = 0;
        int sampleRate = 0;
        int channels = 0;
        int bitDepth = 0;
        string isrc = string.Empty;

        if (isLocal && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                fileSize = new FileInfo(path).Length;
                using var tagFile = TagLib.File.Create(path);
                if (tagFile.Properties is not null)
                {
                    bitrate = tagFile.Properties.AudioBitrate;
                    sampleRate = tagFile.Properties.AudioSampleRate;
                    channels = tagFile.Properties.AudioChannels;
                    bitDepth = tagFile.Properties.BitsPerSample;
                }
                isrc = tagFile.Tag?.ISRC ?? string.Empty;
            }
            catch
            {
                // Keep defaults
            }
        }

        PlaybackSourceKind sourceKind = track.SourceType == MediaSourceType.Local
            ? PlaybackSourceKind.Local
            : (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) ? PlaybackSourceKind.DownloadedOffline : PlaybackSourceKind.YouTubeMusic);

        string sourceLabel = sourceKind switch
        {
            PlaybackSourceKind.Local => "Local File",
            PlaybackSourceKind.DownloadedOffline => "Downloaded Offline",
            _ => "YouTube Music"
        };

        string qualityLabel = bitrate > 0 ? $"{codec} {bitrate} kbps" : (string.IsNullOrWhiteSpace(track.QualityBadge) ? codec : track.QualityBadge);

        return new TrackPlaybackInfo(
            TrackId: track.Id,
            Title: track.Title,
            Artist: track.Artist,
            Album: string.IsNullOrWhiteSpace(track.Album) ? "—" : track.Album,
            Duration: track.Duration,
            SourceKind: sourceKind,
            SourceDisplayLabel: sourceLabel,
            ProviderLabel: sourceLabel,
            RequestedQuality: SelectedStreamingQuality,
            ActualQualityLabel: qualityLabel,
            IsFlacFallback: false,
            FallbackReason: null,
            Codec: codec,
            BitrateKbps: bitrate,
            SampleRateHz: sampleRate,
            ChannelCount: channels,
            BitDepth: bitDepth,
            FileSizeBytes: fileSize,
            FilePathOrUri: isLocal ? path : $"ytmusic://{track.YouTubeVideoId}",
            Isrc: isrc,
            ReleaseYear: track.ReleaseYear,
            Genre: track.Genre,
            TrackNumber: track.TrackNumber,
            LyricsProviderName: track.LyricsProviderName);
    }

    // -------------------------------------------------------------------------
    // Feature 2: Data Usage & Restrictions + Advanced Breakdown + Smart Suggestions
    // -------------------------------------------------------------------------
    [ObservableProperty]
    public partial bool DataLimitEnabled { get; set; } = false;

    [ObservableProperty]
    public partial double DataLimitValueMb { get; set; } = 2048;

    [ObservableProperty]
    public partial int SelectedDataLimitPeriodIndex { get; set; } = (int)DataUsageLimitPeriod.Monthly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataUsageAdvancedVisibility))]
    public partial bool IsDataUsageAdvancedExpanded { get; set; } = false;

    [ObservableProperty]
    public partial string DataUsagePeriodWifiText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageAllTimeWifiText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageLimitLabelText { get; set; } = "2.00 GB";

    [ObservableProperty]
    public partial double DataUsageLimitProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataLimitReachedBannerVisibility))]
    public partial bool IsDataLimitReached { get; set; }

    [ObservableProperty]
    public partial string DataLimitReachedBannerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DataUsageTotalAudioText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageTotalArtworkText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageTotalLyricsText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageTotalCanvasText { get; set; } = "0 B";

    [ObservableProperty]
    public partial string DataUsageFilteredTotalText { get; set; } = "0 B";

    [ObservableProperty]
    public partial int SelectedDataUsageGranularityIndex { get; set; } = (int)DataUsageChartGranularity.Day;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataUsageCustomRangeVisibility))]
    public partial int SelectedDataUsageFilterIndex { get; set; } = (int)DataUsageDateFilter.AllTime;

    [ObservableProperty]
    public partial int SelectedDataUsageSortIndex { get; set; } = (int)DataUsageSortOrder.HighestConsumption;

    [ObservableProperty]
    public partial DateTimeOffset? DataUsageCustomStartDate { get; set; } = DateTimeOffset.UtcNow.AddDays(-6);

    [ObservableProperty]
    public partial DateTimeOffset? DataUsageCustomEndDate { get; set; } = DateTimeOffset.UtcNow;

    [ObservableProperty]
    public partial IReadOnlyList<DataUsageChartPoint> DataUsageChartPoints { get; set; } = Array.Empty<DataUsageChartPoint>();

    public Visibility DataUsageCustomRangeVisibility =>
        SelectedDataUsageFilterIndex == (int)DataUsageDateFilter.CustomRange ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DataUsageAdvancedVisibility => IsDataUsageAdvancedExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DataLimitReachedBannerVisibility => IsDataLimitReached ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasDataUsageSongsVisibility => DataUsageSongAnalysis.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyDataUsageSongsVisibility => DataUsageSongAnalysis.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HasDownloadSuggestionsVisibility => DataUsageDownloadSuggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyDownloadSuggestionsVisibility => DataUsageDownloadSuggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<DataUsageSongAnalysisItem> DataUsageSongAnalysis { get; } = [];
    public ObservableCollection<DataUsageDownloadSuggestionItem> DataUsageDownloadSuggestions { get; } = [];

    partial void OnDataLimitEnabledChanged(bool value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistDataUsageSettingsAsync();
        }
    }

    partial void OnDataLimitValueMbChanged(double value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistDataUsageSettingsAsync();
        }
    }

    partial void OnSelectedDataLimitPeriodIndexChanged(int value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = PersistDataUsageSettingsAsync();
        }
    }

    partial void OnIsDataUsageAdvancedExpandedChanged(bool value)
    {
        RefreshDataUsageSummary();
    }

    partial void OnSelectedDataUsageGranularityIndexChanged(int value) => RefreshDataUsageSummary();
    partial void OnSelectedDataUsageFilterIndexChanged(int value) => RefreshDataUsageSummary();
    partial void OnSelectedDataUsageSortIndexChanged(int value) => RefreshDataUsageSummary();
    partial void OnDataUsageCustomStartDateChanged(DateTimeOffset? value) => RefreshDataUsageSummary();
    partial void OnDataUsageCustomEndDateChanged(DateTimeOffset? value) => RefreshDataUsageSummary();

    private async Task PersistDataUsageSettingsAsync()
    {
        long limitBytes = (long)Math.Max(10, DataLimitValueMb) * 1024L * 1024L;
        var period = Enum.IsDefined(typeof(DataUsageLimitPeriod), SelectedDataLimitPeriodIndex)
            ? (DataUsageLimitPeriod)SelectedDataLimitPeriodIndex
            : DataUsageLimitPeriod.Monthly;

        var updated = _dataUsageService.Settings with
        {
            WifiLimitEnabled = DataLimitEnabled,
            WifiLimitBytes = limitBytes,
            Period = period
        };

        await _dataUsageService.UpdateSettingsAsync(updated);
        RefreshDataUsageSummary();
    }

    private void OnDataUsageChanged(object? sender, EventArgs e)
    {
        _dispatcherQueue?.TryEnqueue(RefreshDataUsageSummary);
    }

    private void OnDataUsageLimitReached(object? sender, string message)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            IsDataLimitReached = true;
            DataLimitReachedBannerText = message;
            StatusMessage = message;
            RefreshDataUsageSummary();
        });
    }

    [RelayCommand]
    public void RefreshDataUsageSummary()
    {
        var summary = _dataUsageService.GetSummary();
        DataUsagePeriodWifiText = summary.FormattedPeriodWifi;
        DataUsageAllTimeWifiText = summary.FormattedAllTimeWifi;
        DataUsageLimitLabelText = summary.FormattedWifiLimit;
        DataUsageLimitProgressPercent = summary.LimitProgressPercent;
        IsDataLimitReached = summary.IsWifiLimitReached;
        if (summary.IsWifiLimitReached && string.IsNullOrWhiteSpace(DataLimitReachedBannerText))
        {
            DataLimitReachedBannerText = $"Data limit ({summary.FormattedWifiLimit}) reached. Online playback is paused; local songs still play.";
        }

        var granularity = Enum.IsDefined(typeof(DataUsageChartGranularity), SelectedDataUsageGranularityIndex)
            ? (DataUsageChartGranularity)SelectedDataUsageGranularityIndex
            : DataUsageChartGranularity.Day;
        var filter = Enum.IsDefined(typeof(DataUsageDateFilter), SelectedDataUsageFilterIndex)
            ? (DataUsageDateFilter)SelectedDataUsageFilterIndex
            : DataUsageDateFilter.AllTime;
        var sortOrder = Enum.IsDefined(typeof(DataUsageSortOrder), SelectedDataUsageSortIndex)
            ? (DataUsageSortOrder)SelectedDataUsageSortIndex
            : DataUsageSortOrder.HighestConsumption;
        DateOnly? customStart = DataUsageCustomStartDate is { } cs ? DateOnly.FromDateTime(cs.UtcDateTime) : null;
        DateOnly? customEnd = DataUsageCustomEndDate is { } ce ? DateOnly.FromDateTime(ce.UtcDateTime) : null;

        var analytics = _dataUsageService.QueryAnalytics(granularity, filter, customStart, customEnd, sortOrder);
        DataUsageFilteredTotalText = analytics.FormattedFilteredTotal;
        DataUsageTotalAudioText = TrackPlaybackInfo.FormatByteSize(analytics.FilteredAudioBytes);
        DataUsageTotalArtworkText = TrackPlaybackInfo.FormatByteSize(analytics.FilteredArtworkBytes);
        DataUsageTotalLyricsText = TrackPlaybackInfo.FormatByteSize(analytics.FilteredLyricsBytes);
        DataUsageTotalCanvasText = TrackPlaybackInfo.FormatByteSize(analytics.FilteredCanvasBytes);
        DataUsageChartPoints = analytics.ChartPoints;

        DataUsageSongAnalysis.Clear();
        foreach (var item in analytics.SongAnalysis.Take(100))
        {
            DataUsageSongAnalysis.Add(item with { ShowAdvanced = IsDataUsageAdvancedExpanded });
        }

        DataUsageDownloadSuggestions.Clear();
        foreach (var sug in summary.DownloadSuggestions.Take(25))
        {
            DataUsageDownloadSuggestions.Add(sug);
        }

        OnPropertyChanged(nameof(HasDataUsageSongsVisibility));
        OnPropertyChanged(nameof(EmptyDataUsageSongsVisibility));
        OnPropertyChanged(nameof(HasDownloadSuggestionsVisibility));
        OnPropertyChanged(nameof(EmptyDownloadSuggestionsVisibility));
    }

    [RelayCommand]
    public void ToggleDataUsageAdvanced()
    {
        IsDataUsageAdvancedExpanded = !IsDataUsageAdvancedExpanded;
    }

    [RelayCommand]
    public async Task ResetDataUsagePeriodAsync()
    {
        await _dataUsageService.ResetCurrentPeriodAsync();
        IsDataLimitReached = false;
        DataLimitReachedBannerText = string.Empty;
        RefreshDataUsageSummary();
        StatusMessage = "Data usage period counter reset.";
    }

    [RelayCommand]
    public async Task ClearDataUsageHistoryAsync()
    {
        await _dataUsageService.ClearHistoryAsync();
        IsDataLimitReached = false;
        DataLimitReachedBannerText = string.Empty;
        RefreshDataUsageSummary();
        StatusMessage = "Data usage history cleared.";
    }

    [RelayCommand]
    public async Task DownloadSuggestedTrackAsync(DataUsageDownloadSuggestionItem? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        var track = new TrackItem
        {
            Id = !string.IsNullOrWhiteSpace(suggestion.YouTubeVideoId) ? $"ytm_{suggestion.YouTubeVideoId}" : suggestion.TrackKey,
            Title = suggestion.Title,
            Artist = suggestion.Artist,
            Album = suggestion.Album,
            YouTubeVideoId = suggestion.YouTubeVideoId,
            AudioSourceUri = !string.IsNullOrWhiteSpace(suggestion.YouTubeVideoId) ? $"ytmusic://{suggestion.YouTubeVideoId}" : string.Empty,
            SourceType = MediaSourceType.Online
        };

        StatusMessage = $"Downloading '{track.Title}' for offline listening...";
        var result = await _musicDownloadService.DownloadTrackAsync(track);
        if (result.Success)
        {
            await _dataUsageService.MarkTrackDownloadedAsync(track);
            RefreshDataUsageSummary();
            StatusMessage = $"Downloaded '{track.Title}' ({result.FormatBadge}).";
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? $"Download failed for '{track.Title}'.";
        }
    }

    internal void RecordExternalLyricsDataUsage(TrackItem track, IReadOnlyList<LyricLine> lines, string providerName)
    {
        if (track is null || lines is null || lines.Count == 0)
        {
            return;
        }

        // Only record network data when fetched from an external online provider (not embedded/local .lrc)
        if (string.IsNullOrWhiteSpace(providerName) ||
            providerName.Contains("Local", StringComparison.OrdinalIgnoreCase) ||
            providerName.Contains("Embedded", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        long estimatedBytes = 0;
        foreach (var line in lines)
        {
            estimatedBytes += System.Text.Encoding.UTF8.GetByteCount(line.Text ?? string.Empty) + 16;
        }

        if (estimatedBytes > 0)
        {
            _ = _dataUsageService.RecordUsageAsync(
                track,
                DataUsageCategory.Lyrics,
                estimatedBytes,
                qualityLabel: null,
                sourceLabel: providerName);
        }
    }

    internal void RecordExternalCanvasDataUsage(TrackItem track, string? videoUri, string providerName)
    {
        if (track is null || string.IsNullOrWhiteSpace(videoUri))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(providerName) &&
            providerName.Contains("Offline", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string canonicalUri = videoUri.Trim();
        try
        {
            if (Uri.TryCreate(canonicalUri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            {
                canonicalUri = parsed.LocalPath;
            }
            if (File.Exists(canonicalUri))
            {
                canonicalUri = Path.GetFullPath(canonicalUri);
            }
        }
        catch
        {
            // Keep trimmed URI
        }

        string sessionKey = canonicalUri;
        try
        {
            var (_, normalizedUrl, _) = Services.ArtworkCacheManager.NormalizeArtworkRequest(canonicalUri);
            if (!string.IsNullOrWhiteSpace(normalizedUrl))
            {
                sessionKey = normalizedUrl;
            }
        }
        catch
        {
            // Keep canonicalUri
        }
        lock (_recordedCanvasSessionKeys)
        {
            if (!_recordedCanvasSessionKeys.Add(sessionKey))
            {
                return;
            }
        }

        long bytes = 0;
        try
        {
            if (File.Exists(canonicalUri))
            {
                var info = new FileInfo(canonicalUri);
                // Only charge if the cached file was newly created during this session;
                // pre-existing disk cache hits from prior runs cost zero network data.
                if (info.CreationTimeUtc >= ProcessStartUtc.AddSeconds(-2))
                {
                    bytes = info.Length;
                }
            }
        }
        catch
        {
            // Ignore IO errors
        }

        if (bytes > 0)
        {
            _ = _dataUsageService.RecordUsageAsync(
                track,
                DataUsageCategory.Canvas,
                bytes,
                qualityLabel: null,
                sourceLabel: providerName);
        }
    }

    internal async Task TrackRemoteArtworkDataUsageAsync(TrackItem track, CancellationToken cancellationToken)
    {
        if (track is null ||
            string.IsNullOrWhiteSpace(track.CoverArtUri) ||
            string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string rawUri = track.CoverArtUri.Trim();
        bool isRemoteHttp = rawUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            rawUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // Only actual remote HTTP(S) downloads incur network usage; cached CoverCache/local artwork costs 0 B.
        if (!isRemoteHttp)
        {
            return;
        }

        string canonicalKey = Services.ArtworkCacheManager.NormalizeArtworkRequest(rawUri).CanonicalKey;
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            canonicalKey = rawUri;
        }
        lock (_recordedArtworkSessionKeys)
        {
            if (!_recordedArtworkSessionKeys.Add(canonicalKey))
            {
                return;
            }
        }

        try
        {
            var outcome = await Services.ArtworkCacheManager.GetOrDownloadAsync(
                rawUri,
                minDimensionPx: 544,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!outcome.FromCache && outcome.NetworkBytesDownloaded > 0)
            {
                await _dataUsageService.RecordUsageAsync(
                    track,
                    DataUsageCategory.Artwork,
                    outcome.NetworkBytesDownloaded,
                    qualityLabel: null,
                    sourceLabel: "Cover Art",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore superseded track selection
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote artwork data usage accounting skipped for '{Title}'.", track.Title);
        }
    }

    // -------------------------------------------------------------------------
    // Feature 4b: Local Scan Blacklist & Whitelist + Shallow/Deep Controls
    // -------------------------------------------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLocalScanFilterMode))]
    [NotifyPropertyChangedFor(nameof(LocalScanFilterModeDescription))]
    public partial int SelectedLocalScanFilterModeIndex { get; set; } = (int)LocalScanFilterMode.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalScanDepthLabel))]
    public partial bool GlobalDeepScanEnabled { get; set; } = true;

    public LocalScanFilterMode SelectedLocalScanFilterMode =>
        Enum.IsDefined(typeof(LocalScanFilterMode), SelectedLocalScanFilterModeIndex)
            ? (LocalScanFilterMode)SelectedLocalScanFilterModeIndex
            : LocalScanFilterMode.All;

    public string GlobalScanDepthLabel => GlobalDeepScanEnabled
        ? "Deep (Include Subfolders)"
        : "Shallow (Selected Folder Only)";

    public string LocalScanFilterModeDescription => SelectedLocalScanFilterMode switch
    {
        LocalScanFilterMode.Blacklist => "Blacklist Mode — Scans all library folders while excluding folders marked as Blacklist.",
        LocalScanFilterMode.Whitelist => "Whitelist Mode — Scans ONLY folders explicitly marked as Whitelist.",
        _ => "All Folders — Scans all configured library folders without Blacklist/Whitelist filtering."
    };

    public ObservableCollection<FolderScanRule> LocalScanFilterRules { get; } = [];
    public Visibility HasLocalScanRulesVisibility => LocalScanFilterRules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyLocalScanRulesVisibility => LocalScanFilterRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedLocalScanFilterModeIndexChanged(int value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = SaveLocalScanFilterSettingsAsync();
        }
    }

    partial void OnGlobalDeepScanEnabledChanged(bool value)
    {
        if (!_isHydratingPlaybackAndDataSettings)
        {
            _ = SaveLocalScanFilterSettingsAsync();
        }
    }

    private void SyncLocalScanFilterFromService()
    {
        var filter = _catalogService.GetLocalScanFilterSettings();
        SelectedLocalScanFilterModeIndex = (int)filter.FilterMode;
        GlobalDeepScanEnabled = filter.GlobalDeepScan;
        LocalScanFilterRules.Clear();
        foreach (var rule in filter.Rules)
        {
            LocalScanFilterRules.Add(rule);
        }

        OnPropertyChanged(nameof(HasLocalScanRulesVisibility));
        OnPropertyChanged(nameof(EmptyLocalScanRulesVisibility));
    }

    [RelayCommand]
    public Task PickAndAddBlacklistFolderAsync() => PickAndAddFolderScanRuleAsync(FolderScanListType.Blacklist);

    [RelayCommand]
    public Task PickAndAddWhitelistFolderAsync() => PickAndAddFolderScanRuleAsync(FolderScanListType.Whitelist);

    private async Task PickAndAddFolderScanRuleAsync(FolderScanListType listType)
    {
        try
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List
            };
            folderPicker.FileTypeFilter.Add("*");

            if (App.MainWindowInstance is Window mainWindow)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
            }

            var picked = await folderPicker.PickSingleFolderAsync();
            if (picked is not null && !string.IsNullOrWhiteSpace(picked.Path))
            {
                await AddFolderScanRuleAsync(picked.Path, listType);
                string label = listType == FolderScanListType.Blacklist ? "Blacklist" : "Whitelist";
                StatusMessage = $"Added '{picked.Name}' to {label}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pick folder for scan rule.");
        }
    }

    public async Task AddFolderScanRuleAsync(string folderPath, FolderScanListType listType)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        string normalized = Xperience.Core.Playback.LocalScanFilterEvaluator.NormalizeDirectoryPath(folderPath);
        string displayName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalized;
        }

        var existing = LocalScanFilterRules.FirstOrDefault(r =>
            string.Equals(r.FolderPath, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            int idx = LocalScanFilterRules.IndexOf(existing);
            LocalScanFilterRules[idx] = existing with { ListType = listType };
        }
        else
        {
            LocalScanFilterRules.Add(new FolderScanRule
            {
                FolderPath = normalized,
                DisplayName = displayName,
                ListType = listType,
                DepthMode = FolderScanDepthMode.InheritGlobal
            });
        }

        // Automatically activate matching filter mode if currently set to All
        if (SelectedLocalScanFilterMode == LocalScanFilterMode.All)
        {
            SelectedLocalScanFilterModeIndex = listType == FolderScanListType.Blacklist
                ? (int)LocalScanFilterMode.Blacklist
                : (int)LocalScanFilterMode.Whitelist;
        }

        OnPropertyChanged(nameof(HasLocalScanRulesVisibility));
        OnPropertyChanged(nameof(EmptyLocalScanRulesVisibility));
        await SaveLocalScanFilterSettingsAsync();
    }

    [RelayCommand]
    public async Task ToggleFolderRuleListTypeAsync(FolderScanRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        int idx = LocalScanFilterRules.IndexOf(rule);
        if (idx < 0)
        {
            return;
        }

        var nextList = rule.ListType == FolderScanListType.Blacklist
            ? FolderScanListType.Whitelist
            : FolderScanListType.Blacklist;

        LocalScanFilterRules[idx] = rule with { ListType = nextList };
        await SaveLocalScanFilterSettingsAsync();
    }

    [RelayCommand]
    public async Task CycleFolderRuleDepthModeAsync(FolderScanRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        int idx = LocalScanFilterRules.IndexOf(rule);
        if (idx < 0)
        {
            return;
        }

        var nextDepth = rule.DepthMode switch
        {
            FolderScanDepthMode.InheritGlobal => FolderScanDepthMode.Shallow,
            FolderScanDepthMode.Shallow => FolderScanDepthMode.Deep,
            _ => FolderScanDepthMode.InheritGlobal
        };

        LocalScanFilterRules[idx] = rule with { DepthMode = nextDepth };
        await SaveLocalScanFilterSettingsAsync();
    }

    [RelayCommand]
    public async Task RemoveFolderRuleAsync(FolderScanRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        if (LocalScanFilterRules.Remove(rule))
        {
            OnPropertyChanged(nameof(HasLocalScanRulesVisibility));
            OnPropertyChanged(nameof(EmptyLocalScanRulesVisibility));
            await SaveLocalScanFilterSettingsAsync();
        }
    }

    [RelayCommand]
    public async Task RescanLocalLibraryAsync()
    {
        StatusMessage = "Rescanning local library with active Blacklist/Whitelist rules...";
        await _catalogService.RescanLocalLibraryAsync();
        _isLibraryDirty = true;
        await RefreshCatalogAsync();
        StatusMessage = $"Local library updated ({Songs.Count} songs).";
    }

    private async Task SaveLocalScanFilterSettingsAsync()
    {
        var settings = new LocalScanFilterSettings
        {
            FilterMode = SelectedLocalScanFilterMode,
            GlobalDeepScan = GlobalDeepScanEnabled,
            Rules = [.. LocalScanFilterRules]
        };

        await _catalogService.UpdateLocalScanFilterSettingsAsync(settings);
        _isLibraryDirty = true;
        await RefreshCatalogAsync();
    }
}
