using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.DataUsage;

/// <summary>
/// Pure, deterministic core engine for lightweight permanent network data usage tracking,
/// period-based limit enforcement, local external-data filtering, and smart download suggestions.
/// </summary>
public sealed class DataUsageTracker
{
    public const int DefaultMinPlaysForDownloadSuggestion = 2;

    private readonly DataUsageStoreSnapshot _store;

    public DataUsageTracker(DataUsageStoreSnapshot? initialStore = null)
    {
        _store = initialStore ?? new DataUsageStoreSnapshot();
        _store.TrackRefs ??= new Dictionary<string, DataUsageTrackRef>(StringComparer.OrdinalIgnoreCase);
        _store.TrackRecords ??= new Dictionary<string, DataUsageTrackRecord>(StringComparer.OrdinalIgnoreCase);
        _store.DailyBuckets ??= new Dictionary<string, DataUsageDailyBucket>(StringComparer.OrdinalIgnoreCase);
        _store.DailyTrackRecords ??= new Dictionary<string, Dictionary<string, DataUsageDailyTrackRecord>>(StringComparer.OrdinalIgnoreCase);
        _store.PeriodResetDayBaselineWifiBytes ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _store.PeriodResetDayBaselineMobileBytes ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    }

    public DataUsageStoreSnapshot Snapshot => _store;

    /// <summary>
    /// Builds a compact normalized track key (`ytm:{videoId}` for online tracks, `local:{sha256_12}` for local files).
    /// </summary>
    public static string BuildTrackKey(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            return $"ytm:{track.YouTubeVideoId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            track.Id.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase) &&
            track.Id.Length > 4)
        {
            return $"ytm:{track.Id[4..].Trim()}";
        }

        string seed = !string.IsNullOrWhiteSpace(track.AudioSourceUri)
            ? track.AudioSourceUri.Trim().ToLowerInvariant()
            : $"{track.Artist?.Trim().ToLowerInvariant()}|{track.Title?.Trim().ToLowerInvariant()}";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        string shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return track.SourceType == MediaSourceType.Local ? $"local:{shortHash}" : $"online:{shortHash}";
    }

    public static string BuildTrackKeyFromMetadata(string title, string artist, bool isLocal = false, string? videoId = null)
    {
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            return $"ytm:{videoId.Trim()}";
        }

        string seed = $"{artist?.Trim().ToLowerInvariant()}|{title?.Trim().ToLowerInvariant()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        string shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return isLocal ? $"local:{shortHash}" : $"online:{shortHash}";
    }

    /// <summary>
    /// Records network bytes consumed for a track. Returns true if the record was mutated.
    /// </summary>
    public bool RecordBytes(
        TrackItem? track,
        DataUsageCategory category,
        long bytes,
        string? qualityLabel,
        string? sourceLabel,
        DataUsageNetworkType networkType,
        DateTimeOffset utcNow)
    {
        if (bytes <= 0)
        {
            return false;
        }

        string trackKey;
        DataUsageTrackRef trackRef;
        if (track is not null)
        {
            trackKey = BuildTrackKey(track);
            trackRef = new DataUsageTrackRef
            {
                Title = track.Title ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                Album = track.Album ?? string.Empty,
                IsLocal = track.SourceType == MediaSourceType.Local,
                IsDownloaded = track.IsDownloadedOffline || !string.IsNullOrWhiteSpace(track.LocalOfflineFilePath),
                YouTubeVideoId = track.YouTubeVideoId ?? string.Empty
            };
        }
        else
        {
            trackKey = "system:catalog";
            trackRef = new DataUsageTrackRef
            {
                Title = "Catalog & Metadata",
                Artist = "Xperience",
                Album = string.Empty,
                IsLocal = false,
                IsDownloaded = false
            };
        }

        if (_store.TrackRefs.TryGetValue(trackKey, out var existingRef))
        {
            _store.TrackRefs[trackKey] = existingRef with
            {
                Title = string.IsNullOrWhiteSpace(trackRef.Title) ? existingRef.Title : trackRef.Title,
                Artist = string.IsNullOrWhiteSpace(trackRef.Artist) ? existingRef.Artist : trackRef.Artist,
                Album = string.IsNullOrWhiteSpace(trackRef.Album) ? existingRef.Album : trackRef.Album,
                IsDownloaded = existingRef.IsDownloaded || trackRef.IsDownloaded,
                YouTubeVideoId = string.IsNullOrWhiteSpace(trackRef.YouTubeVideoId) ? existingRef.YouTubeVideoId : trackRef.YouTubeVideoId
            };
        }
        else
        {
            _store.TrackRefs[trackKey] = trackRef;
        }

        if (!_store.TrackRecords.TryGetValue(trackKey, out var record))
        {
            record = new DataUsageTrackRecord { TrackKey = trackKey };
            _store.TrackRecords[trackKey] = record;
        }

        bool isMobile = networkType == DataUsageNetworkType.Mobile;
        switch (category)
        {
            case DataUsageCategory.Audio:
                if (isMobile) record.MobileAudioBytes += bytes; else record.WifiAudioBytes += bytes;
                break;
            case DataUsageCategory.Artwork:
                if (isMobile) record.MobileArtworkBytes += bytes; else record.WifiArtworkBytes += bytes;
                break;
            case DataUsageCategory.Lyrics:
                if (isMobile) record.MobileLyricsBytes += bytes; else record.WifiLyricsBytes += bytes;
                break;
            case DataUsageCategory.Canvas:
                if (isMobile) record.MobileCanvasBytes += bytes; else record.WifiCanvasBytes += bytes;
                break;
        }

        if (!string.IsNullOrWhiteSpace(qualityLabel))
        {
            record.LastQualityLabel = qualityLabel;
        }

        if (!string.IsNullOrWhiteSpace(sourceLabel))
        {
            record.LastSourceLabel = sourceLabel;
        }

        record.LastPlayedUnixMs = utcNow.ToUnixTimeMilliseconds();

        string dateKey = utcNow.UtcDateTime.ToString("yyyy-MM-dd");
        if (!_store.DailyBuckets.TryGetValue(dateKey, out var bucket))
        {
            bucket = new DataUsageDailyBucket { DateKey = dateKey };
            _store.DailyBuckets[dateKey] = bucket;
        }

        if (isMobile)
        {
            bucket.MobileBytes += bytes;
        }
        else
        {
            bucket.WifiBytes += bytes;
        }

        switch (category)
        {
            case DataUsageCategory.Audio:
                bucket.AudioBytes += bytes;
                break;
            case DataUsageCategory.Artwork:
                bucket.ArtworkBytes += bytes;
                break;
            case DataUsageCategory.Lyrics:
                bucket.LyricsBytes += bytes;
                break;
            case DataUsageCategory.Canvas:
                bucket.CanvasBytes += bytes;
                break;
        }

        if (!_store.DailyTrackRecords.TryGetValue(dateKey, out var dayTracks))
        {
            dayTracks = new Dictionary<string, DataUsageDailyTrackRecord>(StringComparer.OrdinalIgnoreCase);
            _store.DailyTrackRecords[dateKey] = dayTracks;
        }

        if (!dayTracks.TryGetValue(trackKey, out var dayTrackRec))
        {
            dayTrackRec = new DataUsageDailyTrackRecord();
            dayTracks[trackKey] = dayTrackRec;
        }

        switch (category)
        {
            case DataUsageCategory.Audio:
                if (isMobile) dayTrackRec.MobileAudioBytes += bytes; else dayTrackRec.WifiAudioBytes += bytes;
                break;
            case DataUsageCategory.Artwork:
                if (isMobile) dayTrackRec.MobileArtworkBytes += bytes; else dayTrackRec.WifiArtworkBytes += bytes;
                break;
            case DataUsageCategory.Lyrics:
                if (isMobile) dayTrackRec.MobileLyricsBytes += bytes; else dayTrackRec.WifiLyricsBytes += bytes;
                break;
            case DataUsageCategory.Canvas:
                if (isMobile) dayTrackRec.MobileCanvasBytes += bytes; else dayTrackRec.WifiCanvasBytes += bytes;
                break;
        }

        dayTrackRec.LastPlayedUnixMs = Math.Max(dayTrackRec.LastPlayedUnixMs, utcNow.ToUnixTimeMilliseconds());

        return true;
    }

    /// <summary>
    /// Increments the online stream play counter for a track (non-local only).
    /// </summary>
    public bool RecordStreamPlay(
        TrackItem track,
        string? qualityLabel,
        string? sourceLabel,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.SourceType == MediaSourceType.Local || track.IsDownloadedOrLocal)
        {
            return false;
        }

        string trackKey = BuildTrackKey(track);
        if (!_store.TrackRefs.ContainsKey(trackKey))
        {
            _store.TrackRefs[trackKey] = new DataUsageTrackRef
            {
                Title = track.Title ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                Album = track.Album ?? string.Empty,
                IsLocal = false,
                IsDownloaded = false,
                YouTubeVideoId = track.YouTubeVideoId ?? string.Empty
            };
        }

        if (!_store.TrackRecords.TryGetValue(trackKey, out var record))
        {
            record = new DataUsageTrackRecord { TrackKey = trackKey };
            _store.TrackRecords[trackKey] = record;
        }

        record.StreamPlayCount++;
        if (!string.IsNullOrWhiteSpace(qualityLabel))
        {
            record.LastQualityLabel = qualityLabel;
        }

        if (!string.IsNullOrWhiteSpace(sourceLabel))
        {
            record.LastSourceLabel = sourceLabel;
        }

        record.LastPlayedUnixMs = utcNow.ToUnixTimeMilliseconds();

        string dateKey = utcNow.UtcDateTime.ToString("yyyy-MM-dd");
        if (!_store.DailyTrackRecords.TryGetValue(dateKey, out var dayTracks))
        {
            dayTracks = new Dictionary<string, DataUsageDailyTrackRecord>(StringComparer.OrdinalIgnoreCase);
            _store.DailyTrackRecords[dateKey] = dayTracks;
        }

        if (!dayTracks.TryGetValue(trackKey, out var dayTrackRec))
        {
            dayTrackRec = new DataUsageDailyTrackRecord();
            dayTracks[trackKey] = dayTrackRec;
        }

        dayTrackRec.StreamPlayCount++;
        dayTrackRec.LastPlayedUnixMs = Math.Max(dayTrackRec.LastPlayedUnixMs, record.LastPlayedUnixMs);

        return true;
    }

    public bool MarkTrackDownloaded(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        string trackKey = BuildTrackKey(track);
        if (_store.TrackRefs.TryGetValue(trackKey, out var existing))
        {
            _store.TrackRefs[trackKey] = existing with { IsDownloaded = true };
            return true;
        }

        return false;
    }

    public (long WifiBytes, long MobileBytes) ComputePeriodUsage(
        DataUsageLimitSettings settings,
        DateTimeOffset utcNow)
    {
        long allWifi = 0;
        long allMobile = 0;
        foreach (var rec in _store.TrackRecords.Values)
        {
            allWifi += rec.TotalWifiBytes;
            allMobile += rec.TotalMobileBytes;
        }

        if (settings.Period == DataUsageLimitPeriod.Total)
        {
            return (
                Math.Max(0, allWifi - _store.TotalResetBaselineWifiBytes),
                Math.Max(0, allMobile - _store.TotalResetBaselineMobileBytes));
        }

        DateOnly today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        DateOnly startDate = settings.Period switch
        {
            DataUsageLimitPeriod.Daily => today,
            DataUsageLimitPeriod.Weekly => today.AddDays(-6),
            DataUsageLimitPeriod.Monthly => new DateOnly(today.Year, today.Month, 1),
            _ => DateOnly.MinValue
        };

        if (settings.LastManualResetUnixMs > 0)
        {
            DateOnly resetDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(settings.LastManualResetUnixMs).UtcDateTime);
            if (resetDate > startDate)
            {
                startDate = resetDate;
            }
        }

        long periodWifi = 0;
        long periodMobile = 0;
        foreach (var bucket in _store.DailyBuckets.Values)
        {
            if (DateOnly.TryParseExact(bucket.DateKey, "yyyy-MM-dd", out var d) && d >= startDate && d <= today)
            {
                long baselineWifi = _store.PeriodResetDayBaselineWifiBytes.TryGetValue(bucket.DateKey, out long bw) ? bw : 0;
                long baselineMobile = _store.PeriodResetDayBaselineMobileBytes.TryGetValue(bucket.DateKey, out long bm) ? bm : 0;
                periodWifi += Math.Max(0, bucket.WifiBytes - baselineWifi);
                periodMobile += Math.Max(0, bucket.MobileBytes - baselineMobile);
            }
        }

        return (periodWifi, periodMobile);
    }

    public bool IsLimitReached(
        DataUsageLimitSettings settings,
        DataUsageNetworkType networkType,
        DateTimeOffset utcNow)
    {
        var (periodWifi, periodMobile) = ComputePeriodUsage(settings, utcNow);
        if (networkType == DataUsageNetworkType.Mobile)
        {
            return settings.MobileLimitEnabled && settings.MobileLimitBytes > 0 && periodMobile >= settings.MobileLimitBytes;
        }

        return settings.WifiLimitEnabled && settings.WifiLimitBytes > 0 && periodWifi >= settings.WifiLimitBytes;
    }

    public void ResetCurrentPeriod(DateTimeOffset utcNow)
    {
        long allWifi = _store.TrackRecords.Values.Sum(r => r.TotalWifiBytes);
        long allMobile = _store.TrackRecords.Values.Sum(r => r.TotalMobileBytes);
        _store.TotalResetBaselineWifiBytes = allWifi;
        _store.TotalResetBaselineMobileBytes = allMobile;

        // Snapshot cumulative baselines on buckets up to today instead of deleting DailyBuckets,
        // so historical consumption charts remain intact while period limit counters reset to 0.
        DateOnly today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        foreach (var bucket in _store.DailyBuckets.Values)
        {
            if (DateOnly.TryParseExact(bucket.DateKey, "yyyy-MM-dd", out var d) && d <= today)
            {
                _store.PeriodResetDayBaselineWifiBytes[bucket.DateKey] = bucket.WifiBytes;
                _store.PeriodResetDayBaselineMobileBytes[bucket.DateKey] = bucket.MobileBytes;
            }
        }
    }

    public void ClearAllHistory()
    {
        _store.TrackRefs.Clear();
        _store.TrackRecords.Clear();
        _store.DailyBuckets.Clear();
        _store.DailyTrackRecords.Clear();
        _store.TotalResetBaselineWifiBytes = 0;
        _store.TotalResetBaselineMobileBytes = 0;
        _store.PeriodResetDayBaselineWifiBytes.Clear();
        _store.PeriodResetDayBaselineMobileBytes.Clear();
    }

    public DataUsageSummary BuildSummary(
        DataUsageLimitSettings settings,
        DateTimeOffset utcNow,
        int minPlaysForSuggestion = DefaultMinPlaysForDownloadSuggestion)
    {
        var (periodWifi, periodMobile) = ComputePeriodUsage(settings, utcNow);

        long allWifi = 0;
        long allMobile = 0;
        long totalAudio = 0;
        long totalArtwork = 0;
        long totalLyrics = 0;
        long totalCanvas = 0;

        var analysis = new List<DataUsageSongAnalysisItem>();
        var suggestions = new List<DataUsageDownloadSuggestionItem>();

        foreach (var kvp in _store.TrackRecords)
        {
            var rec = kvp.Value;
            allWifi += rec.TotalWifiBytes;
            allMobile += rec.TotalMobileBytes;
            totalAudio += rec.TotalAudioBytes;
            totalArtwork += rec.TotalArtworkBytes;
            totalLyrics += rec.TotalLyricsBytes;
            totalCanvas += rec.TotalCanvasBytes;

            if (string.Equals(kvp.Key, "system:catalog", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _store.TrackRefs.TryGetValue(kvp.Key, out var trackRef);
            bool isLocal = trackRef?.IsLocal ?? kvp.Key.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
            bool isDownloaded = trackRef?.IsDownloaded ?? false;

            // Requirement: Include local songs ONLY if they consumed external network data > 0
            if (isLocal && rec.TotalBytes <= 0)
            {
                continue;
            }

            if (rec.TotalBytes <= 0 && rec.StreamPlayCount <= 0)
            {
                continue;
            }

            string title = trackRef?.Title ?? kvp.Key;
            string artist = trackRef?.Artist ?? string.Empty;
            string album = trackRef?.Album ?? string.Empty;
            string videoId = trackRef?.YouTubeVideoId ?? string.Empty;

            analysis.Add(new DataUsageSongAnalysisItem(
                TrackKey: kvp.Key,
                Title: title,
                Artist: artist,
                Album: album,
                IsLocal: isLocal,
                IsDownloaded: isDownloaded,
                YouTubeVideoId: videoId,
                StreamPlayCount: rec.StreamPlayCount,
                TotalBytes: rec.TotalBytes,
                WifiBytes: rec.TotalWifiBytes,
                MobileBytes: rec.TotalMobileBytes,
                AudioBytes: rec.TotalAudioBytes,
                ArtworkBytes: rec.TotalArtworkBytes,
                LyricsBytes: rec.TotalLyricsBytes,
                CanvasBytes: rec.TotalCanvasBytes,
                LastQualityLabel: rec.LastQualityLabel,
                LastSourceLabel: rec.LastSourceLabel,
                LastPlayedUnixMs: rec.LastPlayedUnixMs));

            if (!isLocal && !isDownloaded && rec.StreamPlayCount >= minPlaysForSuggestion)
            {
                suggestions.Add(new DataUsageDownloadSuggestionItem(
                    TrackKey: kvp.Key,
                    Title: title,
                    Artist: artist,
                    Album: album,
                    YouTubeVideoId: videoId,
                    StreamPlayCount: rec.StreamPlayCount,
                    TotalBytes: rec.TotalBytes,
                    LastQualityLabel: rec.LastQualityLabel));
            }
        }

        analysis.Sort((a, b) =>
        {
            int cmp = b.TotalBytes.CompareTo(a.TotalBytes);
            return cmp != 0 ? cmp : b.LastPlayedUnixMs.CompareTo(a.LastPlayedUnixMs);
        });

        suggestions.Sort((a, b) =>
        {
            int cmp = b.StreamPlayCount.CompareTo(a.StreamPlayCount);
            return cmp != 0 ? cmp : b.TotalBytes.CompareTo(a.TotalBytes);
        });

        bool wifiLimitReached = settings.WifiLimitEnabled &&
                                settings.WifiLimitBytes > 0 &&
                                periodWifi >= settings.WifiLimitBytes;

        return new DataUsageSummary(
            PeriodWifiBytes: periodWifi,
            PeriodMobileBytes: periodMobile,
            AllTimeWifiBytes: allWifi,
            AllTimeMobileBytes: allMobile,
            TotalAudioBytes: totalAudio,
            TotalArtworkBytes: totalArtwork,
            TotalLyricsBytes: totalLyrics,
            TotalCanvasBytes: totalCanvas,
            WifiLimitEnabled: settings.WifiLimitEnabled,
            WifiLimitBytes: settings.WifiLimitBytes,
            IsWifiLimitReached: wifiLimitReached,
            Period: settings.Period,
            SongAnalysis: analysis,
            DownloadSuggestions: suggestions);
    }

    /// <summary>
    /// Computes time-series chart points (Day, Week, or Month granularity), date-filtered category totals,
    /// and sorted per-song consumption items.
    /// </summary>
    public DataUsageAnalyticsResult QueryAnalytics(
        DateTimeOffset utcNow,
        DataUsageChartGranularity chartGranularity,
        DataUsageDateFilter dateFilter,
        DateOnly? customStart,
        DateOnly? customEnd,
        DataUsageSortOrder sortOrder)
    {
        DateOnly today = DateOnly.FromDateTime(utcNow.UtcDateTime);
        var (rangeStart, rangeEnd) = ResolveDateFilterWindow(today, dateFilter, customStart, customEnd);

        var chartPoints = BuildChartPoints(today, chartGranularity, dateFilter, rangeStart, rangeEnd);

        long filteredAudio = 0;
        long filteredArtwork = 0;
        long filteredLyrics = 0;
        long filteredCanvas = 0;
        long filteredTotal = 0;
        var analysis = new List<DataUsageSongAnalysisItem>();

        if (dateFilter == DataUsageDateFilter.AllTime)
        {
            foreach (var kvp in _store.TrackRecords)
            {
                var rec = kvp.Value;
                filteredAudio += rec.TotalAudioBytes;
                filteredArtwork += rec.TotalArtworkBytes;
                filteredLyrics += rec.TotalLyricsBytes;
                filteredCanvas += rec.TotalCanvasBytes;
                filteredTotal += rec.TotalBytes;

                AddSongAnalysisRowIfEligible(
                    analysis,
                    kvp.Key,
                    rec.TotalAudioBytes,
                    rec.TotalArtworkBytes,
                    rec.TotalLyricsBytes,
                    rec.TotalCanvasBytes,
                    rec.TotalWifiBytes,
                    rec.TotalMobileBytes,
                    rec.StreamPlayCount,
                    rec.LastQualityLabel,
                    rec.LastSourceLabel,
                    rec.LastPlayedUnixMs);
            }
        }
        else
        {
            var aggregated = new Dictionary<string, (long WifiAudio, long WifiArt, long WifiLyrics, long WifiCanvas, long MobAudio, long MobArt, long MobLyrics, long MobCanvas, int Plays, long LastPlayed)>(StringComparer.OrdinalIgnoreCase);
            var allDailyTotals = new Dictionary<string, (long Audio, long Art, long Lyrics, long Canvas, long Wifi, long Mobile, int Plays)>(StringComparer.OrdinalIgnoreCase);

            foreach (var dayKvp in _store.DailyTrackRecords)
            {
                if (!DateOnly.TryParseExact(dayKvp.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    continue;
                }

                bool inRange = day >= rangeStart && day <= rangeEnd;
                foreach (var trackKvp in dayKvp.Value)
                {
                    var dRec = trackKvp.Value;
                    allDailyTotals.TryGetValue(trackKvp.Key, out var allCur);
                    allDailyTotals[trackKvp.Key] = (
                        allCur.Audio + dRec.WifiAudioBytes + dRec.MobileAudioBytes,
                        allCur.Art + dRec.WifiArtworkBytes + dRec.MobileArtworkBytes,
                        allCur.Lyrics + dRec.WifiLyricsBytes + dRec.MobileLyricsBytes,
                        allCur.Canvas + dRec.WifiCanvasBytes + dRec.MobileCanvasBytes,
                        allCur.Wifi + dRec.WifiAudioBytes + dRec.WifiArtworkBytes + dRec.WifiLyricsBytes + dRec.WifiCanvasBytes,
                        allCur.Mobile + dRec.MobileAudioBytes + dRec.MobileArtworkBytes + dRec.MobileLyricsBytes + dRec.MobileCanvasBytes,
                        allCur.Plays + dRec.StreamPlayCount);

                    if (!inRange)
                    {
                        continue;
                    }

                    aggregated.TryGetValue(trackKvp.Key, out var cur);
                    aggregated[trackKvp.Key] = (
                        cur.WifiAudio + dRec.WifiAudioBytes,
                        cur.WifiArt + dRec.WifiArtworkBytes,
                        cur.WifiLyrics + dRec.WifiLyricsBytes,
                        cur.WifiCanvas + dRec.WifiCanvasBytes,
                        cur.MobAudio + dRec.MobileAudioBytes,
                        cur.MobArt + dRec.MobileArtworkBytes,
                        cur.MobLyrics + dRec.MobileLyricsBytes,
                        cur.MobCanvas + dRec.MobileCanvasBytes,
                        cur.Plays + dRec.StreamPlayCount,
                        Math.Max(cur.LastPlayed, dRec.LastPlayedUnixMs));
                }
            }

            // Include legacy unbucketed TrackRecords whose LastPlayedUnixMs falls inside the selected date window.
            foreach (var kvp in _store.TrackRecords)
            {
                var rec = kvp.Value;
                allDailyTotals.TryGetValue(kvp.Key, out var bucketed);
                long remWifiAudio = Math.Max(0, rec.WifiAudioBytes - (bucketed.Wifi > 0 ? Math.Min(rec.WifiAudioBytes, bucketed.Audio) : 0));
                long remTotal = Math.Max(0, rec.TotalBytes - (bucketed.Wifi + bucketed.Mobile));
                if (remTotal <= 0 || rec.LastPlayedUnixMs <= 0)
                {
                    continue;
                }

                var lastDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(rec.LastPlayedUnixMs).UtcDateTime);
                if (lastDate >= rangeStart && lastDate <= rangeEnd)
                {
                    long remAudio = Math.Max(0, rec.TotalAudioBytes - bucketed.Audio);
                    long remArt = Math.Max(0, rec.TotalArtworkBytes - bucketed.Art);
                    long remLyrics = Math.Max(0, rec.TotalLyricsBytes - bucketed.Lyrics);
                    long remCanvas = Math.Max(0, rec.TotalCanvasBytes - bucketed.Canvas);
                    long remWifi = Math.Max(0, rec.TotalWifiBytes - bucketed.Wifi);
                    long remMob = Math.Max(0, rec.TotalMobileBytes - bucketed.Mobile);
                    _ = remWifiAudio;

                    aggregated.TryGetValue(kvp.Key, out var cur);
                    aggregated[kvp.Key] = (
                        cur.WifiAudio + Math.Min(remAudio, remWifi),
                        cur.WifiArt + Math.Min(remArt, remWifi),
                        cur.WifiLyrics + Math.Min(remLyrics, remWifi),
                        cur.WifiCanvas + Math.Min(remCanvas, remWifi),
                        cur.MobAudio + Math.Max(0, remAudio - remWifi),
                        cur.MobArt + Math.Max(0, remArt - remWifi),
                        cur.MobLyrics + Math.Max(0, remLyrics - remWifi),
                        cur.MobCanvas + Math.Max(0, remCanvas - remWifi),
                        cur.Plays + Math.Max(0, rec.StreamPlayCount - bucketed.Plays),
                        Math.Max(cur.LastPlayed, rec.LastPlayedUnixMs));
                }
            }

            foreach (var kvp in aggregated)
            {
                var agg = kvp.Value;
                long audio = agg.WifiAudio + agg.MobAudio;
                long art = agg.WifiArt + agg.MobArt;
                long lyrics = agg.WifiLyrics + agg.MobLyrics;
                long canvas = agg.WifiCanvas + agg.MobCanvas;
                long wifi = agg.WifiAudio + agg.WifiArt + agg.WifiLyrics + agg.WifiCanvas;
                long mobile = agg.MobAudio + agg.MobArt + agg.MobLyrics + agg.MobCanvas;
                long total = wifi + mobile;

                filteredAudio += audio;
                filteredArtwork += art;
                filteredLyrics += lyrics;
                filteredCanvas += canvas;
                filteredTotal += total;

                _store.TrackRecords.TryGetValue(kvp.Key, out var baseRec);
                AddSongAnalysisRowIfEligible(
                    analysis,
                    kvp.Key,
                    audio,
                    art,
                    lyrics,
                    canvas,
                    wifi,
                    mobile,
                    agg.Plays,
                    baseRec?.LastQualityLabel ?? string.Empty,
                    baseRec?.LastSourceLabel ?? string.Empty,
                    agg.LastPlayed > 0 ? agg.LastPlayed : (baseRec?.LastPlayedUnixMs ?? 0));
            }
        }

        SortSongAnalysis(analysis, sortOrder);

        return new DataUsageAnalyticsResult(
            ChartPoints: chartPoints,
            FilteredTotalBytes: filteredTotal,
            FilteredAudioBytes: filteredAudio,
            FilteredArtworkBytes: filteredArtwork,
            FilteredLyricsBytes: filteredLyrics,
            FilteredCanvasBytes: filteredCanvas,
            SongAnalysis: analysis);
    }

    private void AddSongAnalysisRowIfEligible(
        List<DataUsageSongAnalysisItem> destination,
        string trackKey,
        long audioBytes,
        long artworkBytes,
        long lyricsBytes,
        long canvasBytes,
        long wifiBytes,
        long mobileBytes,
        int streamPlayCount,
        string lastQualityLabel,
        string lastSourceLabel,
        long lastPlayedUnixMs)
    {
        if (string.Equals(trackKey, "system:catalog", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _store.TrackRefs.TryGetValue(trackKey, out var trackRef);
        bool isLocal = trackRef?.IsLocal ?? trackKey.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
        bool isDownloaded = trackRef?.IsDownloaded ?? false;
        long totalBytes = wifiBytes + mobileBytes;

        if (isLocal && totalBytes <= 0)
        {
            return;
        }

        if (totalBytes <= 0 && streamPlayCount <= 0)
        {
            return;
        }

        destination.Add(new DataUsageSongAnalysisItem(
            TrackKey: trackKey,
            Title: trackRef?.Title ?? trackKey,
            Artist: trackRef?.Artist ?? string.Empty,
            Album: trackRef?.Album ?? string.Empty,
            IsLocal: isLocal,
            IsDownloaded: isDownloaded,
            YouTubeVideoId: trackRef?.YouTubeVideoId ?? string.Empty,
            StreamPlayCount: streamPlayCount,
            TotalBytes: totalBytes,
            WifiBytes: wifiBytes,
            MobileBytes: mobileBytes,
            AudioBytes: audioBytes,
            ArtworkBytes: artworkBytes,
            LyricsBytes: lyricsBytes,
            CanvasBytes: canvasBytes,
            LastQualityLabel: lastQualityLabel,
            LastSourceLabel: lastSourceLabel,
            LastPlayedUnixMs: lastPlayedUnixMs));
    }

    private static (DateOnly Start, DateOnly End) ResolveDateFilterWindow(
        DateOnly today,
        DataUsageDateFilter filter,
        DateOnly? customStart,
        DateOnly? customEnd)
    {
        return filter switch
        {
            DataUsageDateFilter.Day => (today, today),
            DataUsageDateFilter.Week => (today.AddDays(-6), today),
            DataUsageDateFilter.Month => (today.AddDays(-29), today),
            DataUsageDateFilter.CustomRange => NormalizeCustomRange(customStart, customEnd, today),
            _ => (DateOnly.MinValue, DateOnly.MaxValue)
        };
    }

    private static (DateOnly Start, DateOnly End) NormalizeCustomRange(DateOnly? customStart, DateOnly? customEnd, DateOnly today)
    {
        DateOnly s = customStart ?? today.AddDays(-29);
        DateOnly e = customEnd ?? today;
        return s <= e ? (s, e) : (e, s);
    }

    private List<DataUsageChartPoint> BuildChartPoints(
        DateOnly today,
        DataUsageChartGranularity granularity,
        DataUsageDateFilter dateFilter,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        DateOnly chartEnd = dateFilter == DataUsageDateFilter.AllTime || rangeEnd == DateOnly.MaxValue
            ? today
            : rangeEnd;

        DateOnly chartStart = dateFilter == DataUsageDateFilter.AllTime || rangeStart == DateOnly.MinValue
            ? granularity switch
            {
                DataUsageChartGranularity.Day => chartEnd.AddDays(-13),
                DataUsageChartGranularity.Week => GetWeekStartMonday(chartEnd).AddDays(-7 * 7),
                DataUsageChartGranularity.Month => new DateOnly(chartEnd.Year, chartEnd.Month, 1).AddMonths(-5),
                _ => chartEnd.AddDays(-13)
            }
            : rangeStart;

        if (chartStart > chartEnd)
        {
            (chartStart, chartEnd) = (chartEnd, chartStart);
        }

        if (granularity == DataUsageChartGranularity.Day && chartEnd.DayNumber - chartStart.DayNumber > 60)
        {
            chartStart = chartEnd.AddDays(-60);
        }

        var bucketByDate = new Dictionary<DateOnly, DataUsageDailyBucket>();
        foreach (var kvp in _store.DailyBuckets)
        {
            if (DateOnly.TryParseExact(kvp.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                bucketByDate[d] = kvp.Value;
            }
        }

        var points = new List<DataUsageChartPoint>();

        if (granularity == DataUsageChartGranularity.Day)
        {
            for (var d = chartStart; d <= chartEnd; d = d.AddDays(1))
            {
                long total = 0, audio = 0, art = 0, lyrics = 0, canvas = 0;
                if (bucketByDate.TryGetValue(d, out var b))
                {
                    total = b.WifiBytes + b.MobileBytes;
                    audio = b.AudioBytes;
                    art = b.ArtworkBytes;
                    lyrics = b.LyricsBytes;
                    canvas = b.CanvasBytes;
                }

                points.Add(new DataUsageChartPoint(
                    Label: d.ToString("MM/dd", CultureInfo.InvariantCulture),
                    TooltipTitle: d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StartDate: d,
                    EndDate: d,
                    TotalBytes: total,
                    AudioBytes: audio,
                    ArtworkBytes: art,
                    LyricsBytes: lyrics,
                    CanvasBytes: canvas));
            }
        }
        else if (granularity == DataUsageChartGranularity.Week)
        {
            var cursor = GetWeekStartMonday(chartStart);
            while (cursor <= chartEnd)
            {
                var wStart = cursor < chartStart ? chartStart : cursor;
                var wEndRaw = cursor.AddDays(6);
                var wEnd = wEndRaw > chartEnd ? chartEnd : wEndRaw;

                long total = 0, audio = 0, art = 0, lyrics = 0, canvas = 0;
                for (var d = wStart; d <= wEnd; d = d.AddDays(1))
                {
                    if (bucketByDate.TryGetValue(d, out var b))
                    {
                        total += b.WifiBytes + b.MobileBytes;
                        audio += b.AudioBytes;
                        art += b.ArtworkBytes;
                        lyrics += b.LyricsBytes;
                        canvas += b.CanvasBytes;
                    }
                }

                points.Add(new DataUsageChartPoint(
                    Label: cursor.ToString("MM/dd", CultureInfo.InvariantCulture),
                    TooltipTitle: $"{wStart:MMM d} - {wEnd:MMM d, yyyy}",
                    StartDate: wStart,
                    EndDate: wEnd,
                    TotalBytes: total,
                    AudioBytes: audio,
                    ArtworkBytes: art,
                    LyricsBytes: lyrics,
                    CanvasBytes: canvas));

                cursor = cursor.AddDays(7);
            }
        }
        else
        {
            var cursor = new DateOnly(chartStart.Year, chartStart.Month, 1);
            while (cursor <= chartEnd)
            {
                var mStart = cursor < chartStart ? chartStart : cursor;
                var mEndRaw = cursor.AddMonths(1).AddDays(-1);
                var mEnd = mEndRaw > chartEnd ? chartEnd : mEndRaw;

                long total = 0, audio = 0, art = 0, lyrics = 0, canvas = 0;
                for (var d = mStart; d <= mEnd; d = d.AddDays(1))
                {
                    if (bucketByDate.TryGetValue(d, out var b))
                    {
                        total += b.WifiBytes + b.MobileBytes;
                        audio += b.AudioBytes;
                        art += b.ArtworkBytes;
                        lyrics += b.LyricsBytes;
                        canvas += b.CanvasBytes;
                    }
                }

                points.Add(new DataUsageChartPoint(
                    Label: cursor.ToString("MMM yy", CultureInfo.InvariantCulture),
                    TooltipTitle: cursor.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                    StartDate: mStart,
                    EndDate: mEnd,
                    TotalBytes: total,
                    AudioBytes: audio,
                    ArtworkBytes: art,
                    LyricsBytes: lyrics,
                    CanvasBytes: canvas));

                cursor = cursor.AddMonths(1);
            }
        }

        return points;
    }

    private static DateOnly GetWeekStartMonday(DateOnly date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }

    private static void SortSongAnalysis(List<DataUsageSongAnalysisItem> items, DataUsageSortOrder sortOrder)
    {
        items.Sort((a, b) => sortOrder switch
        {
            DataUsageSortOrder.Date =>
                b.LastPlayedUnixMs.CompareTo(a.LastPlayedUnixMs) is int dc && dc != 0
                    ? dc
                    : b.TotalBytes.CompareTo(a.TotalBytes),
            DataUsageSortOrder.Alphabetical =>
                StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title) is int ac && ac != 0
                    ? ac
                    : StringComparer.OrdinalIgnoreCase.Compare(a.Artist, b.Artist),
            _ =>
                b.TotalBytes.CompareTo(a.TotalBytes) is int hc && hc != 0
                    ? hc
                    : b.LastPlayedUnixMs.CompareTo(a.LastPlayedUnixMs)
        });
    }
}
