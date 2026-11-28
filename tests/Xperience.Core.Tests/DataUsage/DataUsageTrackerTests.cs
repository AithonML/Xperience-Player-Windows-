using Xperience.Core.DataUsage;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xunit;

namespace Xperience.Core.Tests.DataUsage;

public sealed class DataUsageTrackerTests
{
    private static TrackItem MakeOnlineTrack(string videoId, string title, string artist) => new()
    {
        Id = $"ytm_{videoId}",
        YouTubeVideoId = videoId,
        Title = title,
        Artist = artist,
        Album = "Test Album",
        SourceType = MediaSourceType.Online,
        AudioSourceUri = $"ytmusic://{videoId}"
    };

    private static TrackItem MakeLocalTrack(string path, string title, string artist) => new()
    {
        Id = $"local_{title}",
        Title = title,
        Artist = artist,
        Album = "Local Album",
        SourceType = MediaSourceType.Local,
        AudioSourceUri = path
    };

    [Fact]
    public void LocalSongs_AreIncludedOnlyWhenExternalNetworkBytesConsumed()
    {
        var tracker = new DataUsageTracker();
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var localNoNet = MakeLocalTrack(@"C:\Music\offline.flac", "Pure Offline", "Artist A");
        var localWithLyrics = MakeLocalTrack(@"C:\Music\with_lrc.flac", "Local With Online Lyrics", "Artist B");

        // Local track plays offline -> no network bytes, stream play not counted
        Assert.False(tracker.RecordStreamPlay(localNoNet, "FLAC", "Local", now));
        Assert.False(tracker.RecordBytes(localNoNet, DataUsageCategory.Audio, 0, "FLAC", "Local", DataUsageNetworkType.Wifi, now));

        // Second local track fetches external LRC lyrics + Apple Canvas + external cover art
        Assert.True(tracker.RecordBytes(localWithLyrics, DataUsageCategory.Lyrics, 4_096, null, "LRCLIB", DataUsageNetworkType.Wifi, now));
        Assert.True(tracker.RecordBytes(localWithLyrics, DataUsageCategory.Artwork, 65_536, null, "Cover Art", DataUsageNetworkType.Wifi, now));
        Assert.True(tracker.RecordBytes(localWithLyrics, DataUsageCategory.Canvas, 2_500_000, null, "Apple Music Canvas", DataUsageNetworkType.Wifi, now));

        var summary = tracker.BuildSummary(new DataUsageLimitSettings(), now);

        Assert.Single(summary.SongAnalysis);
        var item = summary.SongAnalysis[0];
        Assert.Equal("Local With Online Lyrics", item.Title);
        Assert.True(item.IsLocal);
        Assert.Equal(4_096, item.LyricsBytes);
        Assert.Equal(65_536, item.ArtworkBytes);
        Assert.Equal(2_500_000, item.CanvasBytes);
        Assert.Equal(2_569_632, item.TotalBytes);
        Assert.Equal(65_536, summary.TotalArtworkBytes);
        // Local tracks are never suggested for download
        Assert.Empty(summary.DownloadSuggestions);
    }

    [Fact]
    public void SmartDownloadSuggestions_IncludesStreamedTracksWithTwoOrMorePlays_AndExcludesDownloaded()
    {
        var tracker = new DataUsageTracker();
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var song1 = MakeOnlineTrack("vid11111111", "Repeat Hit", "Artist 1");
        var song2 = MakeOnlineTrack("vid22222222", "One Play Only", "Artist 2");

        tracker.RecordBytes(song1, DataUsageCategory.Audio, 4_000_000, "AAC 160 kbps", "YouTube Music", DataUsageNetworkType.Wifi, now);
        tracker.RecordStreamPlay(song1, "AAC 160 kbps", "YouTube Music", now);
        tracker.RecordStreamPlay(song1, "AAC 160 kbps", "YouTube Music", now.AddMinutes(4));

        tracker.RecordBytes(song2, DataUsageCategory.Audio, 3_000_000, "AAC 128 kbps", "YouTube Music", DataUsageNetworkType.Wifi, now);
        tracker.RecordStreamPlay(song2, "AAC 128 kbps", "YouTube Music", now);

        var summaryBefore = tracker.BuildSummary(new DataUsageLimitSettings(), now);
        Assert.Single(summaryBefore.DownloadSuggestions);
        Assert.Equal("Repeat Hit", summaryBefore.DownloadSuggestions[0].Title);
        Assert.Equal(2, summaryBefore.DownloadSuggestions[0].StreamPlayCount);

        // Once downloaded, it disappears from suggestions
        tracker.MarkTrackDownloaded(song1);
        var summaryAfter = tracker.BuildSummary(new DataUsageLimitSettings(), now);
        Assert.Empty(summaryAfter.DownloadSuggestions);
    }

    [Fact]
    public void LimitEnforcement_DetectsPeriodLimitReached_AndResetClearsPeriodWhileKeepingHistory()
    {
        var tracker = new DataUsageTracker();
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var song = MakeOnlineTrack("vid33333333", "Lossless Song", "Artist 3");
        var settings = new DataUsageLimitSettings
        {
            WifiLimitEnabled = true,
            WifiLimitBytes = 10_000_000, // 10 MB
            Period = DataUsageLimitPeriod.Monthly
        };

        tracker.RecordBytes(song, DataUsageCategory.Audio, 12_000_000, "FLAC 16-bit/44.1 kHz", "FLAC Lossless", DataUsageNetworkType.Wifi, now);
        Assert.True(tracker.IsLimitReached(settings, DataUsageNetworkType.Wifi, now));

        // Reset period unblocks limit while preserving all-time permanent song history
        tracker.ResetCurrentPeriod(now);
        Assert.False(tracker.IsLimitReached(settings, DataUsageNetworkType.Wifi, now));

        var summary = tracker.BuildSummary(settings, now);
        Assert.Equal(0, summary.PeriodWifiBytes);
        Assert.Equal(12_000_000, summary.AllTimeWifiBytes);
        Assert.Single(summary.SongAnalysis);
    }

    [Fact]
    public void QueryAnalytics_SupportsDayWeekMonthCharts_DateFilters_AndSortOrders()
    {
        var tracker = new DataUsageTracker();
        var today = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var weekAgo = today.AddDays(-4);
        var oldDay = today.AddDays(-20);

        var trackAlpha = MakeOnlineTrack("vidA", "Alpha Song", "Artist A");
        var trackZeta = MakeOnlineTrack("vidZ", "Zeta Song", "Artist Z");
        var trackMid = MakeOnlineTrack("vidM", "Mid Song", "Artist M");

        tracker.RecordBytes(trackZeta, DataUsageCategory.Audio, 5_000_000, "AAC 160", "YouTube Music", DataUsageNetworkType.Wifi, today);
        tracker.RecordBytes(trackAlpha, DataUsageCategory.Artwork, 200_000, null, "Cover Art", DataUsageNetworkType.Wifi, weekAgo);
        tracker.RecordBytes(trackMid, DataUsageCategory.Audio, 8_000_000, "FLAC", "FLAC Lossless", DataUsageNetworkType.Wifi, oldDay);

        // Chart granularities
        var byDay = tracker.QueryAnalytics(today, DataUsageChartGranularity.Day, DataUsageDateFilter.AllTime, null, null, DataUsageSortOrder.HighestConsumption);
        Assert.Equal(14, byDay.ChartPoints.Count);
        Assert.Equal(13_200_000, byDay.FilteredTotalBytes);
        Assert.Equal("Mid Song", byDay.SongAnalysis[0].Title);
        Assert.Equal("Zeta Song", byDay.SongAnalysis[1].Title);
        Assert.Equal("Alpha Song", byDay.SongAnalysis[2].Title);

        var byWeek = tracker.QueryAnalytics(today, DataUsageChartGranularity.Week, DataUsageDateFilter.AllTime, null, null, DataUsageSortOrder.Alphabetical);
        Assert.Equal(8, byWeek.ChartPoints.Count);
        Assert.Equal("Alpha Song", byWeek.SongAnalysis[0].Title);
        Assert.Equal("Mid Song", byWeek.SongAnalysis[1].Title);
        Assert.Equal("Zeta Song", byWeek.SongAnalysis[2].Title);

        var byMonth = tracker.QueryAnalytics(today, DataUsageChartGranularity.Month, DataUsageDateFilter.AllTime, null, null, DataUsageSortOrder.Date);
        Assert.Equal(6, byMonth.ChartPoints.Count);
        Assert.Equal("Zeta Song", byMonth.SongAnalysis[0].Title);
        Assert.Equal("Alpha Song", byMonth.SongAnalysis[1].Title);
        Assert.Equal("Mid Song", byMonth.SongAnalysis[2].Title);

        // Filters: Week excludes 20-day-old track
        var weekOnly = tracker.QueryAnalytics(today, DataUsageChartGranularity.Day, DataUsageDateFilter.Week, null, null, DataUsageSortOrder.HighestConsumption);
        Assert.Equal(5_200_000, weekOnly.FilteredTotalBytes);
        Assert.Equal(2, weekOnly.SongAnalysis.Count);

        // Filter: Today only
        var todayOnly = tracker.QueryAnalytics(today, DataUsageChartGranularity.Day, DataUsageDateFilter.Day, null, null, DataUsageSortOrder.HighestConsumption);
        Assert.Equal(5_000_000, todayOnly.FilteredTotalBytes);
        Assert.Single(todayOnly.SongAnalysis);

        // Filter: Custom range targeting oldDay .. weekAgo
        var custom = tracker.QueryAnalytics(
            today,
            DataUsageChartGranularity.Day,
            DataUsageDateFilter.CustomRange,
            DateOnly.FromDateTime(oldDay.UtcDateTime),
            DateOnly.FromDateTime(weekAgo.UtcDateTime),
            DataUsageSortOrder.HighestConsumption);
        Assert.Equal(8_200_000, custom.FilteredTotalBytes);
        Assert.Equal(2, custom.SongAnalysis.Count);
    }

    [Fact]
    public void ResetCurrentPeriod_PreservesHistoricalChartBucketsWhileZeroingPeriodCounter()
    {
        var tracker = new DataUsageTracker();
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var song = MakeOnlineTrack("vid44444444", "Chart Song", "Artist 4");
        var settings = new DataUsageLimitSettings { WifiLimitEnabled = true, WifiLimitBytes = 20_000_000, Period = DataUsageLimitPeriod.Monthly };

        tracker.RecordBytes(song, DataUsageCategory.Audio, 12_000_000, "FLAC", "FLAC Lossless", DataUsageNetworkType.Wifi, now);
        tracker.ResetCurrentPeriod(now);

        Assert.Equal(0, tracker.BuildSummary(settings, now).PeriodWifiBytes);
        var analytics = tracker.QueryAnalytics(now, DataUsageChartGranularity.Day, DataUsageDateFilter.AllTime, null, null, DataUsageSortOrder.HighestConsumption);
        Assert.Equal(12_000_000, analytics.ChartPoints.Sum(p => p.TotalBytes));
    }
}
