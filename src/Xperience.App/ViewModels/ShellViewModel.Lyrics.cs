using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Xperience.Core.Collections;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

public sealed partial class ShellViewModel
{
    private static readonly TimeSpan NegativeLyricsCacheTtl = TimeSpan.FromMinutes(4);

    private sealed record CachedTrackLyricsEntry(
        IReadOnlyList<LyricLine> Lines,
        string ProviderName,
        bool IsFound,
        DateTimeOffset CachedAtUtc);

    private readonly BoundedLruCache<string, CachedTrackLyricsEntry> _resolvedTrackLyricsCache =
        new(maxCapacity: 60, StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _lyricsLookupCts;
    private int _lyricsLookupGeneration;
    private TrackItem? _activeLyricsTrack;

    private CancellationTokenSource? _nextLyricsPrefetchCts;
    private string? _activePrefetchNextTrackKey;

    /// <summary>
    /// Raised when the lyrics display state transitions: <c>true</c> when real lyrics are populated and ready to render,
    /// <c>false</c> when lyrics were cleared, are loading, or were not found.
    /// </summary>
    public event EventHandler<bool>? LyricsDisplayStateChanged;

    /// <summary>
    /// Gets or sets whether synchronized lyrics are actively being fetched for the current track.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsContentVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsNotFoundVisibility))]
    public partial bool IsLyricsLoading { get; set; }

    /// <summary>
    /// Gets or sets whether lyrics lookup finished with no lyrics found (or no track loaded).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsNotFoundVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsContentVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsLoadingVisibility))]
    public partial bool IsLyricsNotFound { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the current track has real lyric lines loaded in <see cref="CurrentLyrics"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsContentVisibility))]
    [NotifyPropertyChangedFor(nameof(LyricsNotFoundVisibility))]
    public partial bool HasLoadedLyrics { get; set; }

    /// <summary>
    /// Gets or sets the 100% English heading shown by the animated Sad Musical Note not-found control.
    /// </summary>
    [ObservableProperty]
    public partial string LyricsEmptyTitle { get; set; } = "No track playing";

    /// <summary>
    /// Gets or sets the 100% English subtitle shown by the animated Sad Musical Note not-found control.
    /// </summary>
    [ObservableProperty]
    public partial string LyricsEmptySubtitle { get; set; } = "Play a song to view synchronized lyrics.";

    /// <summary>
    /// Gets or sets the 100% English helper text shown under the not-found subtitle.
    /// </summary>
    [ObservableProperty]
    public partial string LyricsEmptyHelperText { get; set; } = "You can import a local .LRC file above anytime.";

    /// <summary>
    /// Gets the visibility of the shimmer skeleton loading UI in both Now Playing and Fullscreen lyrics panels.
    /// </summary>
    public Visibility LyricsLoadingVisibility =>
        IsLyricsLoading ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the animated Sad Musical Note empty/not-found state in both Now Playing and Fullscreen lyrics panels.
    /// </summary>
    public Visibility LyricsNotFoundVisibility =>
        !IsLyricsLoading && IsLyricsNotFound ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the synchronized lyrics Win2D canvas in both Now Playing and Fullscreen lyrics panels.
    /// </summary>
    public Visibility LyricsContentVisibility =>
        !IsLyricsLoading && !IsLyricsNotFound && HasLoadedLyrics ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Immediately clears stale lyrics from the previous track on the UI thread, checks synchronous prefetch/memory caches
    /// for zero-flicker display, and otherwise enters the shimmer skeleton state while resolving lyrics asynchronously
    /// under a track-generation cancellation token.
    /// </summary>
    internal void BeginTrackLyricsPipeline(TrackItem? track, bool forceRefresh = false)
    {
        if (!forceRefresh &&
            track is not null &&
            !string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase) &&
            _activeLyricsTrack is not null &&
            IsSameTrack(_activeLyricsTrack, track))
        {
            // Already started/loaded for this exact track (e.g. optimistic selection followed by CurrentTrackChanged)
            ScheduleNextTrackLyricsPrefetch(delayMs: 200);
            return;
        }

        CancelCurrentLyricsLookup();
        int generation = Interlocked.Increment(ref _lyricsLookupGeneration);

        // 1. Immediately wipe previous song's lyrics so stale text NEVER lingers on screen
        CurrentLyrics.Clear();
        _activeLyricIndex = -1;
        HasLoadedLyrics = false;
        LyricsDisplayStateChanged?.Invoke(this, false);

        if (track is null ||
            string.IsNullOrWhiteSpace(track.Title) ||
            string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            _activeLyricsTrack = null;
            IsLyricsLoading = false;
            LyricsEmptyTitle = "No track playing";
            LyricsEmptySubtitle = "Play a song to view synchronized lyrics.";
            LyricsEmptyHelperText = "You can import a local .LRC file above anytime.";
            IsLyricsNotFound = true;
            CurrentLyricsProviderBadge = "Synced Lyrics";
            return;
        }

        _activeLyricsTrack = track;

        if (forceRefresh)
        {
            InvalidateTrackLyricsCacheEntries(track);
            _lyricsProviderService.InvalidateTrackCache(track);
        }
        else
        {
            // 2. Synchronous instant hit check (VM local+online cache -> LyricsProviderService prefetch cache)
            var vmCacheState = TryLookupVmLyricsCache(track, out var vmCachedEntry);
            if (vmCacheState == LyricsCacheLookupState.Hit && vmCachedEntry is not null)
            {
                ApplyResolvedLyricsOnUiThread(track, vmCachedEntry.Lines, vmCachedEntry.ProviderName);
                ScheduleNextTrackLyricsPrefetch(delayMs: 120);
                return;
            }

            if (vmCacheState == LyricsCacheLookupState.NotFound)
            {
                ApplyLyricsNotFoundOnUiThread();
                ScheduleNextTrackLyricsPrefetch(delayMs: 120);
                return;
            }

            var onlineCacheState = _lyricsProviderService.TryGetCachedLookup(track, out var onlineCached);
            if (onlineCacheState == LyricsCacheLookupState.Hit &&
                onlineCached is { Lines.Count: > 0 } &&
                HasRealLyrics(onlineCached.Lines, onlineCached.ProviderName))
            {
                StoreVmLyricsCache(track, new CachedTrackLyricsEntry(
                    onlineCached.Lines,
                    onlineCached.ProviderName,
                    IsFound: true,
                    DateTimeOffset.UtcNow));

                ApplyResolvedLyricsOnUiThread(track, onlineCached.Lines, onlineCached.ProviderName);
                ScheduleNextTrackLyricsPrefetch(delayMs: 120);
                return;
            }
        }

        // 3. Cache miss -> show shimmer skeleton immediately and fetch asynchronously
        var cts = new CancellationTokenSource();
        _lyricsLookupCts = cts;

        IsLyricsNotFound = false;
        HasLoadedLyrics = false;
        IsLyricsLoading = true;
        CurrentLyricsProviderBadge = "Searching lyrics...";

        _ = ResolveTrackLyricsAsyncCore(track, generation, cts.Token);
        ScheduleNextTrackLyricsPrefetch(delayMs: 850);
    }

    /// <summary>
    /// Silently prefetches and caches the next song's lyrics in the background whenever the queue has an upcoming track.
    /// Safe to invoke from track changes and queue updates (Play Next, Reorder, Shuffle, Autoplay append).
    /// </summary>
    internal void ScheduleNextTrackLyricsPrefetch(int delayMs = 350)
    {
        TrackItem? nextTrack = FindNextUpcomingTrackForPrefetch();
        if (nextTrack is null)
        {
            CancelNextTrackLyricsPrefetch();
            return;
        }

        string nextKey = BuildPrimaryTrackLyricsKey(nextTrack);
        if (string.IsNullOrWhiteSpace(nextKey))
        {
            return;
        }

        // Already resolved in VM cache (hit or fresh not-found)
        if (TryLookupVmLyricsCache(nextTrack, out _) != LyricsCacheLookupState.Miss)
        {
            return;
        }

        // For online tracks, also skip if LyricsProviderService already cached it
        if (nextTrack.SourceType == MediaSourceType.Online &&
            _lyricsProviderService.TryGetCachedLookup(nextTrack, out _) != LyricsCacheLookupState.Miss)
        {
            return;
        }

        // Avoid restarting an identical in-progress prefetch when tail items are appended to the queue
        if (string.Equals(_activePrefetchNextTrackKey, nextKey, StringComparison.OrdinalIgnoreCase) &&
            _nextLyricsPrefetchCts is { IsCancellationRequested: false })
        {
            return;
        }

        CancelNextTrackLyricsPrefetch();
        var cts = new CancellationTokenSource();
        _nextLyricsPrefetchCts = cts;
        _activePrefetchNextTrackKey = nextKey;

        _ = RunSilentNextTrackLyricsPrefetchAsync(nextTrack, nextKey, delayMs, cts.Token);
    }

    /// <summary>
    /// Clears cached lyrics when provider order or enabled flags change in Settings Studio.
    /// </summary>
    internal void InvalidateAllLyricsCachesForProviderChange()
    {
        _resolvedTrackLyricsCache.Clear();
        CancelNextTrackLyricsPrefetch();
    }

    /// <summary>
    /// Applies user-imported local .LRC lyrics immediately and updates all caches.
    /// </summary>
    internal void ApplyImportedLrcLyricsForTrack(TrackItem track, IReadOnlyList<LyricLine> parsedLines)
    {
        if (track is null || parsedLines is null || parsedLines.Count == 0)
        {
            return;
        }

        CancelCurrentLyricsLookup();
        Interlocked.Increment(ref _lyricsLookupGeneration);
        _activeLyricsTrack = track;

        _lyricsProviderService.InvalidateTrackCache(track);
        StoreVmLyricsCache(track, new CachedTrackLyricsEntry(
            parsedLines,
            "Local .LRC",
            IsFound: true,
            DateTimeOffset.UtcNow));

        ApplyResolvedLyricsOnUiThread(track, parsedLines, "Local .LRC");
    }

    private async Task ResolveTrackLyricsAsyncCore(TrackItem track, int generation, CancellationToken token)
    {
        try
        {
            // Clone lightweight metadata carrier so catalog fallback mutation on cancellation never corrupts the live TrackItem
            var lookupTarget = CreateDetachedLyricsLookupCopy(track);
            var resolvedLines = await _catalogService.ResolveTrackLyricsAsync(lookupTarget, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || generation != Volatile.Read(ref _lyricsLookupGeneration))
            {
                return;
            }

            string providerName = lookupTarget.LyricsProviderName ?? string.Empty;
            bool hasReal = HasRealLyrics(resolvedLines, providerName);

            if (hasReal)
            {
                StoreVmLyricsCache(track, new CachedTrackLyricsEntry(
                    resolvedLines,
                    providerName,
                    IsFound: true,
                    DateTimeOffset.UtcNow));
            }
            else
            {
                StoreVmLyricsCache(track, new CachedTrackLyricsEntry(
                    [],
                    string.Empty,
                    IsFound: false,
                    DateTimeOffset.UtcNow));
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != Volatile.Read(ref _lyricsLookupGeneration) ||
                    !IsSameTrack(CurrentTrack, track))
                {
                    return;
                }

                if (hasReal)
                {
                    ApplyResolvedLyricsOnUiThread(track, resolvedLines, providerName);
                    RecordExternalLyricsDataUsage(track, resolvedLines, providerName);
                }
                else
                {
                    ApplyLyricsNotFoundOnUiThread();
                }

                ScheduleNextTrackLyricsPrefetch(delayMs: 150);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track selection
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lyrics resolution failed for '{Title}'.", track.Title);

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested ||
                    generation != Volatile.Read(ref _lyricsLookupGeneration) ||
                    !IsSameTrack(CurrentTrack, track))
                {
                    return;
                }

                ApplyLyricsNotFoundOnUiThread();
            });
        }
    }

    private async Task RunSilentNextTrackLyricsPrefetchAsync(
        TrackItem nextTrack,
        string nextKey,
        int delayMs,
        CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // Use a detached lookup copy so background resolution never mutates the live queue TrackItem
            var lookupCopy = CreateDetachedLyricsLookupCopy(nextTrack);
            var resolvedLines = await _catalogService.ResolveTrackLyricsAsync(lookupCopy, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            string providerName = lookupCopy.LyricsProviderName ?? string.Empty;
            if (HasRealLyrics(resolvedLines, providerName))
            {
                StoreVmLyricsCache(nextTrack, new CachedTrackLyricsEntry(
                    resolvedLines,
                    providerName,
                    IsFound: true,
                    DateTimeOffset.UtcNow));
            }
            else if (_lyricsProviderService.TryGetCachedLookup(nextTrack, out _) == LyricsCacheLookupState.NotFound)
            {
                StoreVmLyricsCache(nextTrack, new CachedTrackLyricsEntry(
                    [],
                    string.Empty,
                    IsFound: false,
                    DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled because queue or current track changed
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent background lyrics prefetch skipped for '{Title}'.", nextTrack.Title);
        }
        finally
        {
            if (string.Equals(_activePrefetchNextTrackKey, nextKey, StringComparison.OrdinalIgnoreCase))
            {
                _activePrefetchNextTrackKey = null;
            }
        }
    }

    private void ApplyResolvedLyricsOnUiThread(
        TrackItem track,
        IReadOnlyList<LyricLine> lines,
        string providerName)
    {
        CurrentLyricsProviderBadge = string.IsNullOrWhiteSpace(providerName)
            ? "Synced Lyrics"
            : $"Provider • {providerName}";

        PopulateCurrentLyrics(lines);
        HasLoadedLyrics = CurrentLyrics.Count > 0;
        IsLyricsNotFound = !HasLoadedLyrics;
        IsLyricsLoading = false;

        if (HasLoadedLyrics)
        {
            UpdateActiveLyricForPosition(TimeSpan.FromSeconds(CurrentPositionSeconds), forceNotify: true);
            LyricsDisplayStateChanged?.Invoke(this, true);
        }
        else
        {
            ApplyLyricsNotFoundOnUiThread();
        }
    }

    private void ApplyLyricsNotFoundOnUiThread()
    {
        CurrentLyrics.Clear();
        _activeLyricIndex = -1;
        HasLoadedLyrics = false;
        LyricsEmptyTitle = "No lyrics found";
        LyricsEmptySubtitle = "We couldn't find synced lyrics for this track.";
        LyricsEmptyHelperText = "You can import a local .LRC file above anytime.";
        CurrentLyricsProviderBadge = "No Lyrics";
        IsLyricsNotFound = true;
        IsLyricsLoading = false;
        LyricsDisplayStateChanged?.Invoke(this, false);
    }

    private void CancelCurrentLyricsLookup()
    {
        var previous = Interlocked.Exchange(ref _lyricsLookupCts, null);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch
            {
                // Ignore already-disposed CTS
            }
            finally
            {
                previous.Dispose();
            }
        }
    }

    private void CancelNextTrackLyricsPrefetch()
    {
        _activePrefetchNextTrackKey = null;
        var previous = Interlocked.Exchange(ref _nextLyricsPrefetchCts, null);
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch
            {
                // Ignore already-disposed CTS
            }
            finally
            {
                previous.Dispose();
            }
        }
    }

    private TrackItem? FindNextUpcomingTrackForPrefetch()
    {
        var upcoming = _audioPlayerService.Queue;
        if (upcoming is null || upcoming.Count == 0)
        {
            return null;
        }

        TrackItem? active = CurrentTrack;
        foreach (var candidate in upcoming)
        {
            if (candidate is null ||
                string.IsNullOrWhiteSpace(candidate.Title) ||
                string.Equals(candidate.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (active is not null && IsSameTrack(active, candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private LyricsCacheLookupState TryLookupVmLyricsCache(TrackItem track, out CachedTrackLyricsEntry? entry)
    {
        entry = null;
        var keys = BuildTrackLyricsCacheKeys(track);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool sawFreshNegative = false;

        foreach (string key in keys)
        {
            if (!_resolvedTrackLyricsCache.TryGet(key, out var cached) || cached is null)
            {
                continue;
            }

            if (cached.IsFound && cached.Lines.Count > 0)
            {
                entry = cached;
                return LyricsCacheLookupState.Hit;
            }

            if (!cached.IsFound)
            {
                if (now - cached.CachedAtUtc <= NegativeLyricsCacheTtl)
                {
                    sawFreshNegative = true;
                }
                else
                {
                    _resolvedTrackLyricsCache.Remove(key);
                }
            }
        }

        return sawFreshNegative ? LyricsCacheLookupState.NotFound : LyricsCacheLookupState.Miss;
    }

    private void StoreVmLyricsCache(TrackItem track, CachedTrackLyricsEntry entry)
    {
        foreach (string key in BuildTrackLyricsCacheKeys(track))
        {
            _resolvedTrackLyricsCache.Set(key, entry);
        }
    }

    private void InvalidateTrackLyricsCacheEntries(TrackItem track)
    {
        foreach (string key in BuildTrackLyricsCacheKeys(track))
        {
            _resolvedTrackLyricsCache.Remove(key);
        }
    }

    private static List<string> BuildTrackLyricsCacheKeys(TrackItem track)
    {
        var keys = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            keys.Add($"vid:{track.YouTubeVideoId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            !string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            string idKey = $"id:{track.Id.Trim()}";
            if (!keys.Contains(idKey, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(idKey);
            }
        }

        string normTitle = NormalizeLyricLookupTitle(track.Title);
        string normArtist = NormalizeLyricLookupArtist(track.Artist);
        if (!string.IsNullOrWhiteSpace(normTitle))
        {
            keys.Add($"meta:{normArtist}|{normTitle}");
        }

        return keys;
    }

    private static string BuildPrimaryTrackLyricsKey(TrackItem track)
    {
        var keys = BuildTrackLyricsCacheKeys(track);
        return keys.Count > 0 ? keys[0] : string.Empty;
    }

    private static TrackItem CreateDetachedLyricsLookupCopy(TrackItem source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Artist = source.Artist,
        Album = source.Album,
        Duration = source.Duration,
        AudioSourceUri = source.AudioSourceUri,
        YouTubeVideoId = source.YouTubeVideoId,
        SourceType = source.SourceType,
        LocalOfflineFilePath = source.LocalOfflineFilePath,
        IsDownloadedOffline = source.IsDownloadedOffline
    };

    private static bool IsSameTrack(TrackItem? a, TrackItem? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(b.Id))
        {
            return string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(a.Title, b.Title, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters out empty lists and legacy synthetic metadata placeholders
    /// (e.g. "♪ Title — Artist", "Album • ...", "Click 'Import .LRC'...") so tracks without real lyrics
    /// cleanly display the animated Sad Musical Note not-found state.
    /// </summary>
    private static bool HasRealLyrics(IReadOnlyList<LyricLine>? lines, string? providerName)
    {
        if (lines is null || lines.Count == 0)
        {
            return false;
        }

        if (string.Equals(providerName, "Unsynced", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var line in lines)
        {
            if (line.IsSynchronized)
            {
                return true;
            }
        }

        int realPlainLines = 0;
        foreach (var line in lines)
        {
            string text = line.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) ||
                text.StartsWith("♪ ", StringComparison.Ordinal) ||
                text.StartsWith("Album • ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Audio Stream • ", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Import .LRC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            realPlainLines++;
        }

        return realPlainLines > 0;
    }

    private static string NormalizeLyricLookupTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string cleaned = LyricTitleNoiseRegex().Replace(rawTitle, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? rawTitle.Trim() : cleaned;
    }

    private static string NormalizeLyricLookupArtist(string rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return string.Empty;
        }

        string cleaned = rawArtist.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var parts = cleaned.Split([',', ';', '&'], 2, StringSplitOptions.TrimEntries);
        return string.IsNullOrWhiteSpace(parts[0]) ? cleaned : parts[0];
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(official\s+(music\s+)?video|official\s+audio|official\s+lyric\s+video|lyrics?|visualizer|audio|hd|4k|remastered[^\)\]]*)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex LyricTitleNoiseRegex();
}
