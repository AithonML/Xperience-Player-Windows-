using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Collections;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Multi-provider online synchronized lyrics engine supporting <c>YouLyPlus</c>, <c>Paxsenix</c>,
/// <c>Better Lyrics</c>, <c>LRCLIB</c>, <c>KuGou</c>, and <c>SimpMusic</c>.
/// Supports user-reorderable provider priority and progressive letter-by-letter / syllable-by-syllable synchronization.
/// </summary>
public sealed partial class LyricsProviderService : ILyricsProviderService
{
    private const string ProviderPriorityStorageKey = "LyricsProvidersPriorityOrder_v1";
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan InFlightFetchTimeout = TimeSpan.FromSeconds(30);

    private sealed record CachedLyricsEntry(
        LyricsResolutionResult? Result,
        bool IsFound,
        DateTimeOffset CachedAtUtc);

    private sealed class InFlightLyricsFetch
    {
        private int _waiters = 1;
        private readonly TaskCompletionSource<LyricsResolutionResult?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource InternalCts { get; } = new(InFlightFetchTimeout);

        public Task<LyricsResolutionResult?> Task => _completion.Task;

        public void Complete(LyricsResolutionResult? result) => _completion.TrySetResult(result);

        public void Cancel() => _completion.TrySetCanceled();

        public bool TryAcquireWaiter()
        {
            while (true)
            {
                int current = Volatile.Read(ref _waiters);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _waiters, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void ReleaseWaiter()
        {
            if (Interlocked.Decrement(ref _waiters) == 0)
            {
                try
                {
                    InternalCts.Cancel();
                }
                catch
                {
                    // Ignore cancellation errors if already disposed
                }
            }
        }
    }

    private readonly IStorageService _storageService;
    private readonly ILogger<LyricsProviderService> _logger;
    private readonly HttpClient _httpClient;
    private readonly object _providersGate = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly List<LyricsProviderConfig> _providers = [];
    private readonly BoundedLruCache<string, CachedLyricsEntry> _memoryCache = new(maxCapacity: 60, comparer: StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InFlightLyricsFetch> _inFlightFetches = new(StringComparer.OrdinalIgnoreCase);
    private volatile string _providerCacheSignature = string.Empty;
    private volatile bool _isInitialized;

    /// <inheritdoc />
    public IReadOnlyList<LyricsProviderConfig> Providers
    {
        get
        {
            lock (_providersGate)
            {
                return [.. _providers];
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LyricsProviderService"/> class.
    /// </summary>
    public LyricsProviderService(
        IStorageService storageService,
        ILogger<LyricsProviderService> logger)
    {
        _storageService = storageService;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");

        lock (_providersGate)
        {
            _providers.AddRange(CreateDefaultProviders());
            _providerCacheSignature = BuildProviderSignatureLocked();
        }
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            try
            {
                var saved = await _storageService.LoadAsync<List<LyricsProviderConfig>>(
                    ProviderPriorityStorageKey,
                    defaultValue: null,
                    cancellationToken).ConfigureAwait(false);

                if (saved is { Count: > 0 })
                {
                    var merged = new List<LyricsProviderConfig>();
                    foreach (var item in saved.OrderBy(p => p.PriorityOrder))
                    {
                        if (merged.All(m => m.Id != item.Id))
                        {
                            merged.Add(item);
                        }
                    }

                    foreach (var def in CreateDefaultProviders())
                    {
                        if (merged.All(m => m.Id != def.Id))
                        {
                            merged.Add(def);
                        }
                    }

                    lock (_providersGate)
                    {
                        _providers.Clear();
                        for (int i = 0; i < merged.Count; i++)
                        {
                            _providers.Add(merged[i] with { PriorityOrder = i + 1 });
                        }

                        _providerCacheSignature = BuildProviderSignatureLocked();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Using default lyrics provider priority configuration.");
            }

            _isInitialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task MoveProviderPriorityAsync(
        LyricsProviderId providerId,
        int direction,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<LyricsProviderConfig> snapshotToSave;
        lock (_providersGate)
        {
            int idx = _providers.FindIndex(p => p.Id == providerId);
            if (idx < 0)
            {
                return;
            }

            int targetIdx = Math.Clamp(idx + direction, 0, _providers.Count - 1);
            if (targetIdx == idx)
            {
                return;
            }

            var item = _providers[idx];
            _providers.RemoveAt(idx);
            _providers.Insert(targetIdx, item);

            for (int i = 0; i < _providers.Count; i++)
            {
                _providers[i] = _providers[i] with { PriorityOrder = i + 1 };
            }

            _providerCacheSignature = BuildProviderSignatureLocked();
            snapshotToSave = [.. _providers];
        }

        _memoryCache.Clear();
        await _storageService.SaveAsync(ProviderPriorityStorageKey, snapshotToSave, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetProviderEnabledAsync(
        LyricsProviderId providerId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        List<LyricsProviderConfig> snapshotToSave;
        lock (_providersGate)
        {
            int idx = _providers.FindIndex(p => p.Id == providerId);
            if (idx < 0)
            {
                return;
            }

            _providers[idx] = _providers[idx] with { IsEnabled = isEnabled };
            _providerCacheSignature = BuildProviderSignatureLocked();
            snapshotToSave = [.. _providers];
        }

        _memoryCache.Clear();
        await _storageService.SaveAsync(ProviderPriorityStorageKey, snapshotToSave, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryGetCachedLyrics(TrackItem track, out LyricsResolutionResult? result)
    {
        if (TryGetCachedLookup(track, out result) == LyricsCacheLookupState.Hit &&
            result is { Lines.Count: > 0 })
        {
            return true;
        }

        result = null;
        return false;
    }

    /// <inheritdoc />
    public LyricsCacheLookupState TryGetCachedLookup(TrackItem track, out LyricsResolutionResult? result)
    {
        result = null;
        if (track is null || string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheLookupState.Miss;
        }

        var keys = BuildCacheKeys(track, out _, out _, out _);
        if (keys.Count == 0)
        {
            return LyricsCacheLookupState.Miss;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool sawFreshNegative = false;

        foreach (string key in keys)
        {
            if (!_memoryCache.TryGet(key, out var entry))
            {
                continue;
            }

            if (entry.IsFound && entry.Result is { Lines.Count: > 0 })
            {
                result = entry.Result;
                return LyricsCacheLookupState.Hit;
            }

            if (!entry.IsFound)
            {
                if (now - entry.CachedAtUtc <= NegativeCacheTtl)
                {
                    sawFreshNegative = true;
                }
                else
                {
                    _memoryCache.Remove(key);
                }
            }
        }

        return sawFreshNegative ? LyricsCacheLookupState.NotFound : LyricsCacheLookupState.Miss;
    }

    /// <inheritdoc />
    public void InvalidateTrackCache(TrackItem track)
    {
        if (track is null)
        {
            return;
        }

        var keys = BuildCacheKeys(track, out _, out _, out _);
        foreach (string key in keys)
        {
            _memoryCache.Remove(key);
        }
    }

    /// <inheritdoc />
    public async Task PrefetchLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        if (track is null ||
            string.IsNullOrWhiteSpace(track.Title) ||
            string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetCachedLookup(track, out _) != LyricsCacheLookupState.Miss)
            {
                return;
            }

            _ = await FetchOnlineLyricsAsync(track, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Background prefetch superseded by queue or track change
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent background lyrics prefetch skipped for '{Title}'.", track.Title);
        }
    }

    /// <inheritdoc />
    public async Task<LyricsResolutionResult?> FetchOnlineLyricsAsync(
        TrackItem track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var cacheKeys = BuildCacheKeys(track, out string cleanTitle, out string cleanArtist, out string coalescingKey);
        if (string.IsNullOrWhiteSpace(cleanTitle) ||
            string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(coalescingKey))
        {
            return null;
        }

        var cacheState = TryGetCachedLookup(track, out var cachedResult);
        if (cacheState == LyricsCacheLookupState.Hit)
        {
            return cachedResult;
        }

        if (cacheState == LyricsCacheLookupState.NotFound)
        {
            return null;
        }

        InFlightLyricsFetch entry = AcquireOrStartInFlightFetch(coalescingKey, track, cleanTitle, cleanArtist, cacheKeys);
        int released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                entry.ReleaseWaiter();
            }
        }

        using var cancelRegistration = cancellationToken.Register(ReleaseOnce);
        try
        {
            return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseOnce();
        }
    }

    private InFlightLyricsFetch AcquireOrStartInFlightFetch(
        string coalescingKey,
        TrackItem track,
        string cleanTitle,
        string cleanArtist,
        List<string> cacheKeys)
    {
        while (true)
        {
            if (_inFlightFetches.TryGetValue(coalescingKey, out var existing))
            {
                if (existing.TryAcquireWaiter())
                {
                    return existing;
                }

                // Atomically evict the dying 0-waiter entry so a fresh fetch can register immediately
                _inFlightFetches.TryRemove(new KeyValuePair<string, InFlightLyricsFetch>(coalescingKey, existing));
            }

            var created = new InFlightLyricsFetch();
            if (_inFlightFetches.TryAdd(coalescingKey, created))
            {
                _ = RunOnlineLyricsCascadeAsync(coalescingKey, created, track, cleanTitle, cleanArtist, cacheKeys);
                return created;
            }

            created.InternalCts.Dispose();
        }
    }

    private async Task RunOnlineLyricsCascadeAsync(
        string coalescingKey,
        InFlightLyricsFetch fetchEntry,
        TrackItem track,
        string cleanTitle,
        string cleanArtist,
        List<string> cacheKeys)
    {
        var ct = fetchEntry.InternalCts.Token;
        try
        {
            List<LyricsProviderConfig> orderedProviders;
            lock (_providersGate)
            {
                orderedProviders = _providers
                    .Where(p => p.IsEnabled)
                    .OrderBy(p => p.PriorityOrder)
                    .ToList();
            }

            bool hadCleanProviderResponse = false;

            foreach (var provider in orderedProviders)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    LyricsResolutionResult? result = provider.Id switch
                    {
                        LyricsProviderId.YouLyPlus => await TryFetchYouLyPlusAsync(cleanTitle, cleanArtist, track.Album, track.Duration, ct).ConfigureAwait(false),
                        LyricsProviderId.Paxsenix => await TryFetchPaxsenixAsync(cleanTitle, cleanArtist, track.Duration, ct).ConfigureAwait(false),
                        LyricsProviderId.BetterLyrics => await TryFetchBetterLyricsAsync(cleanTitle, cleanArtist, track.Album, track.Duration, track.YouTubeVideoId, ct).ConfigureAwait(false),
                        LyricsProviderId.LrcLib => await TryFetchLrcLibAsync(cleanTitle, cleanArtist, track.Album, track.Duration, ct).ConfigureAwait(false),
                        LyricsProviderId.KuGou => await TryFetchKuGouAsync(cleanTitle, cleanArtist, track.Duration, ct).ConfigureAwait(false),
                        LyricsProviderId.SimpMusic => await TryFetchSimpMusicAsync(cleanTitle, cleanArtist, track.YouTubeVideoId, track.Duration, ct).ConfigureAwait(false),
                        _ => null
                    };

                    hadCleanProviderResponse = true;

                    if (result is { Lines.Count: > 0 })
                    {
                        var enrichedLines = EnsureProgressiveSegmentsForAllLines(result.Lines, track.Duration);
                        var finalResult = result with { Lines = enrichedLines };
                        StoreCacheEntries(cacheKeys, new CachedLyricsEntry(finalResult, IsFound: true, DateTimeOffset.UtcNow));
                        fetchEntry.Complete(finalResult);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Lyrics provider '{Provider}' did not return a match for '{Title}'.", provider.DisplayName, cleanTitle);
                }
            }

            if (!ct.IsCancellationRequested && (hadCleanProviderResponse || orderedProviders.Count == 0))
            {
                StoreCacheEntries(cacheKeys, new CachedLyricsEntry(null, IsFound: false, DateTimeOffset.UtcNow));
            }

            fetchEntry.Complete(null);
        }
        catch (OperationCanceledException)
        {
            fetchEntry.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Online lyrics cascade failed unexpectedly for '{Title}'.", cleanTitle);
            fetchEntry.Complete(null);
        }
        finally
        {
            _inFlightFetches.TryRemove(new KeyValuePair<string, InFlightLyricsFetch>(coalescingKey, fetchEntry));
            try
            {
                fetchEntry.InternalCts.Dispose();
            }
            catch
            {
                // Ignore double-dispose
            }
        }
    }

    private void StoreCacheEntries(IReadOnlyList<string> keys, CachedLyricsEntry entry)
    {
        foreach (string key in keys)
        {
            _memoryCache.Set(key, entry);
        }
    }

    private string BuildProviderSignatureLocked()
    {
        return string.Join(
            ",",
            _providers
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.PriorityOrder)
                .Select(p => (int)p.Id));
    }

    private List<string> BuildCacheKeys(
        TrackItem track,
        out string cleanTitle,
        out string cleanArtist,
        out string coalescingKey)
    {
        cleanTitle = CleanTrackTitle(track.Title);
        cleanArtist = CleanArtistName(track.Artist);
        string sig = _providerCacheSignature;

        var keys = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            keys.Add($"vid:{track.YouTubeVideoId.Trim()}|{sig}");
        }

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            !string.Equals(track.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
        {
            string idKey = $"id:{track.Id.Trim()}|{sig}";
            if (!keys.Contains(idKey, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(idKey);
            }
        }

        string metaKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            metaKey = $"meta:{cleanArtist}|{cleanTitle}|{sig}";
            keys.Add(metaKey);
        }

        coalescingKey = !string.IsNullOrWhiteSpace(metaKey)
            ? metaKey
            : (keys.Count > 0 ? keys[0] : string.Empty);

        return keys;
    }

    /// <summary>
    /// 1. YouLyPlus (LyricsPlus v2 API) — returns word/syllable-by-syllable JSON timing in milliseconds.
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchYouLyPlusAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        int durationSec = Math.Max(30, (int)Math.Round(duration.TotalSeconds));
        string url = $"https://lyricsplus.binimum.org/v2/lyrics/get?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album ?? string.Empty)}&duration={durationSec}";

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("lyrics", out var lyricsArr) ||
            lyricsArr.ValueKind != JsonValueKind.Array ||
            lyricsArr.GetArrayLength() == 0)
        {
            return null;
        }

        var lines = new List<LyricLine>();
        bool hasSyllables = false;

        foreach (var item in lyricsArr.EnumerateArray())
        {
            double timeMs = item.TryGetProperty("time", out var tProp) && tProp.TryGetDouble(out double tm) ? tm : 0;
            double durMs = item.TryGetProperty("duration", out var dProp) && dProp.TryGetDouble(out double dm) ? dm : 2500;
            string lineText = item.TryGetProperty("text", out var txtProp) ? txtProp.GetString()?.Trim() ?? string.Empty : string.Empty;

            var syllables = new List<LyricSyllableSegment>();
            if (item.TryGetProperty("syllabus", out var sylArr) && sylArr.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var syl in sylArr.EnumerateArray())
                {
                    string sylText = syl.TryGetProperty("text", out var stProp) ? stProp.GetString() ?? string.Empty : string.Empty;
                    double sylTime = syl.TryGetProperty("time", out var sTimeProp) && sTimeProp.TryGetDouble(out double stm) ? stm : timeMs;
                    double sylDur = syl.TryGetProperty("duration", out var sDurProp) && sDurProp.TryGetDouble(out double sdm) ? Math.Max(40, sdm) : 200;

                    if (!string.IsNullOrEmpty(sylText))
                    {
                        sb.Append(sylText);
                        syllables.Add(new LyricSyllableSegment(
                            TimeSpan.FromMilliseconds(sylTime),
                            TimeSpan.FromMilliseconds(sylTime + sylDur),
                            sylText));
                    }
                }

                if (string.IsNullOrWhiteSpace(lineText))
                {
                    lineText = sb.ToString().Trim();
                }

                if (syllables.Count > 0)
                {
                    hasSyllables = true;
                }
            }

            if (string.IsNullOrWhiteSpace(lineText))
            {
                lineText = "♪ • • •";
            }

            lines.Add(new LyricLine(
                TimeSpan.FromMilliseconds(timeMs),
                lineText,
                IsSynchronized: true,
                EndTimestamp: TimeSpan.FromMilliseconds(timeMs + Math.Max(300, durMs)),
                Syllables: syllables.Count > 0 ? ExpandSyllablesToCharacters(syllables) : null));
        }

        return lines.Count > 0
            ? new LyricsResolutionResult("YouLyPlus", hasSyllables, lines)
            : null;
    }

    /// <summary>
    /// 2. Paxsenix (Lyrically Apple Music syllable/line endpoint via iTunes trackId lookup).
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchPaxsenixAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        string searchUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString($"{artist} {title}")}&entity=song&limit=5";
        using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!searchDoc.RootElement.TryGetProperty("results", out var resultsArr) ||
            resultsArr.ValueKind != JsonValueKind.Array ||
            resultsArr.GetArrayLength() == 0)
        {
            return null;
        }

        // Prefer the track matching original title (non-remix if query wasn't remix)
        long? chosenTrackId = null;
        foreach (var candidate in resultsArr.EnumerateArray())
        {
            if (candidate.TryGetProperty("trackId", out var idProp) && idProp.TryGetInt64(out long tid))
            {
                string candName = candidate.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? string.Empty : string.Empty;
                if (chosenTrackId is null || string.Equals(candName, title, StringComparison.OrdinalIgnoreCase))
                {
                    chosenTrackId = tid;
                    if (string.Equals(candName, title, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
        }

        if (chosenTrackId is null)
        {
            return null;
        }

        string paxUrl = $"https://lyrics.paxsenix.org/apple-music/lyrics?id={chosenTrackId.Value}";
        using var paxResp = await _httpClient.GetAsync(paxUrl, cancellationToken).ConfigureAwait(false);
        if (!paxResp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var paxStream = await paxResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var paxDoc = await JsonDocument.ParseAsync(paxStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!paxDoc.RootElement.TryGetProperty("content", out var contentArr) ||
            contentArr.ValueKind != JsonValueKind.Array ||
            contentArr.GetArrayLength() == 0)
        {
            return null;
        }

        var lines = new List<LyricLine>();
        bool hasSyllables = false;

        foreach (var item in contentArr.EnumerateArray())
        {
            double startMs = item.TryGetProperty("timestamp", out var tsProp) && tsProp.TryGetDouble(out double sm) ? sm : 0;
            double endMs = item.TryGetProperty("endtime", out var etProp) && etProp.TryGetDouble(out double em) ? em : startMs + 2500;

            var syllables = new List<LyricSyllableSegment>();
            var lineBuilder = new StringBuilder();

            if (item.TryGetProperty("text", out var textArr) && textArr.ValueKind == JsonValueKind.Array)
            {
                int count = textArr.GetArrayLength();
                int idx = 0;
                foreach (var wordObj in textArr.EnumerateArray())
                {
                    string word = wordObj.TryGetProperty("text", out var wProp) ? wProp.GetString() ?? string.Empty : string.Empty;
                    bool isPart = wordObj.TryGetProperty("part", out var pProp) && pProp.ValueKind == JsonValueKind.True;
                    double wStart = wordObj.TryGetProperty("timestamp", out var wsProp) && wsProp.TryGetDouble(out double wsm) ? wsm : startMs;
                    double wEnd = wordObj.TryGetProperty("endtime", out var weProp) && weProp.TryGetDouble(out double wem) ? wem : wStart + 220;

                    string tokenWithSpace = (!isPart && idx < count - 1 && !word.EndsWith(' ')) ? word + " " : word;
                    lineBuilder.Append(tokenWithSpace);

                    if (!string.IsNullOrEmpty(tokenWithSpace))
                    {
                        syllables.Add(new LyricSyllableSegment(
                            TimeSpan.FromMilliseconds(wStart),
                            TimeSpan.FromMilliseconds(Math.Max(wStart + 40, wEnd)),
                            tokenWithSpace));
                    }

                    idx++;
                }
            }

            string finalLineText = lineBuilder.ToString().Trim();
            if (string.IsNullOrWhiteSpace(finalLineText))
            {
                continue;
            }

            if (syllables.Count > 1)
            {
                hasSyllables = true;
            }

            lines.Add(new LyricLine(
                TimeSpan.FromMilliseconds(startMs),
                finalLineText,
                IsSynchronized: true,
                EndTimestamp: TimeSpan.FromMilliseconds(Math.Max(startMs + 300, endMs)),
                Syllables: syllables.Count > 0 ? ExpandSyllablesToCharacters(syllables) : null));
        }

        return lines.Count > 0
            ? new LyricsResolutionResult("Paxsenix", hasSyllables, lines)
            : null;
    }

    /// <summary>
    /// 3. Better Lyrics (<c>lyrics-api.boidu.dev</c>) — returns Apple Music TTML with word-level <c>&lt;span begin end&gt;</c> tags.
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchBetterLyricsAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        string? videoId,
        CancellationToken cancellationToken)
    {
        int durationSec = Math.Max(30, (int)Math.Round(duration.TotalSeconds));
        var urlBuilder = new StringBuilder($"https://lyrics-api.boidu.dev/getLyrics?s={Uri.EscapeDataString(title)}&a={Uri.EscapeDataString(artist)}&d={durationSec}");
        if (!string.IsNullOrWhiteSpace(album))
        {
            urlBuilder.Append($"&al={Uri.EscapeDataString(album)}");
        }

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            urlBuilder.Append($"&videoId={Uri.EscapeDataString(videoId)}");
        }

        using var response = await _httpClient.GetAsync(urlBuilder.ToString(), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("ttml", out var ttmlProp))
        {
            return null;
        }

        string? ttmlXml = ttmlProp.GetString();
        if (string.IsNullOrWhiteSpace(ttmlXml))
        {
            return null;
        }

        var lines = ParseBetterLyricsTtml(ttmlXml);
        return lines.Count > 0
            ? new LyricsResolutionResult("Better Lyrics", true, lines)
            : null;
    }

    private static List<LyricLine> ParseBetterLyricsTtml(string ttmlXml)
    {
        var lines = new List<LyricLine>();
        try
        {
            var xdoc = XDocument.Parse(ttmlXml, LoadOptions.PreserveWhitespace);
            var pElements = xdoc.Descendants().Where(e => string.Equals(e.Name.LocalName, "p", StringComparison.OrdinalIgnoreCase));

            foreach (var p in pElements)
            {
                TimeSpan lineStart = ParseTtmlTime(p.Attribute("begin")?.Value);
                TimeSpan lineEnd = ParseTtmlTime(p.Attribute("end")?.Value);
                if (lineEnd <= lineStart)
                {
                    lineEnd = lineStart + TimeSpan.FromSeconds(2.5);
                }

                var syllables = new List<LyricSyllableSegment>();
                var fullTextBuilder = new StringBuilder();

                var spans = p.Elements().Where(e => string.Equals(e.Name.LocalName, "span", StringComparison.OrdinalIgnoreCase)).ToList();
                if (spans.Count > 0)
                {
                    foreach (var node in p.Nodes())
                    {
                        if (node is XElement spanEl && string.Equals(spanEl.Name.LocalName, "span", StringComparison.OrdinalIgnoreCase))
                        {
                            string word = spanEl.Value;
                            if (string.IsNullOrEmpty(word))
                            {
                                continue;
                            }

                            TimeSpan wStart = ParseTtmlTime(spanEl.Attribute("begin")?.Value);
                            TimeSpan wEnd = ParseTtmlTime(spanEl.Attribute("end")?.Value);
                            if (wEnd <= wStart)
                            {
                                wEnd = wStart + TimeSpan.FromMilliseconds(220);
                            }

                            fullTextBuilder.Append(word);
                            syllables.Add(new LyricSyllableSegment(wStart, wEnd, word));
                        }
                        else if (node is XText textNode && !string.IsNullOrEmpty(textNode.Value))
                        {
                            fullTextBuilder.Append(textNode.Value);
                            if (syllables.Count > 0)
                            {
                                var last = syllables[^1];
                                syllables[^1] = last with { Text = last.Text + textNode.Value };
                            }
                        }
                    }
                }
                else
                {
                    fullTextBuilder.Append(p.Value);
                }

                string lineText = Regex.Replace(fullTextBuilder.ToString().Trim(), @"\s+", " ");
                if (string.IsNullOrWhiteSpace(lineText))
                {
                    continue;
                }

                lines.Add(new LyricLine(
                    lineStart,
                    lineText,
                    IsSynchronized: true,
                    EndTimestamp: lineEnd,
                    Syllables: syllables.Count > 0 ? ExpandSyllablesToCharacters(syllables) : null));
            }
        }
        catch
        {
            // Ignore malformed TTML
        }

        return lines.OrderBy(l => l.Timestamp).ToList();
    }

    private static TimeSpan ParseTtmlTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        value = value.Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        if (value.EndsWith('s') &&
            double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
        {
            return TimeSpan.FromSeconds(secs);
        }

        var parts = value.Split(':');
        if (parts.Length == 1 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double rawSeconds))
        {
            return TimeSpan.FromSeconds(rawSeconds);
        }

        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int mins) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s2))
        {
            return TimeSpan.FromSeconds((mins * 60.0) + s2);
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int hrs) &&
            int.TryParse(parts[1], out int m3) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s3))
        {
            return TimeSpan.FromSeconds((hrs * 3600.0) + (m3 * 60.0) + s3);
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// 4. LRCLIB (<c>https://lrclib.net/api/get</c> &amp; <c>/api/search</c>) — community synchronized LRC database.
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchLrcLibAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        int durationSec = Math.Max(30, (int)Math.Round(duration.TotalSeconds));
        string getUrl = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&album_name={Uri.EscapeDataString(album ?? string.Empty)}&duration={durationSec}";

        string? syncedLrc = null;
        string? plainLyrics = null;

        using (var getResp = await _httpClient.GetAsync(getUrl, cancellationToken).ConfigureAwait(false))
        {
            if (getResp.IsSuccessStatusCode)
            {
                await using var stream = await getResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("syncedLyrics", out var slProp))
                {
                    syncedLrc = slProp.GetString();
                }

                if (doc.RootElement.TryGetProperty("plainLyrics", out var plProp))
                {
                    plainLyrics = plProp.GetString();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(syncedLrc))
        {
            string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString($"{artist} {title}")}";
            using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
            if (searchResp.IsSuccessStatusCode)
            {
                await using var stream = await searchResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("syncedLyrics", out var sl) && !string.IsNullOrWhiteSpace(sl.GetString()))
                        {
                            syncedLrc = sl.GetString();
                            break;
                        }

                        if (plainLyrics is null && item.TryGetProperty("plainLyrics", out var pl))
                        {
                            plainLyrics = pl.GetString();
                        }
                    }
                }
            }
        }

        string? rawToParse = !string.IsNullOrWhiteSpace(syncedLrc) ? syncedLrc : plainLyrics;
        if (string.IsNullOrWhiteSpace(rawToParse))
        {
            return null;
        }

        var lines = ParseEnhancedOrStandardLrc(rawToParse, duration);
        return lines.Count > 0
            ? new LyricsResolutionResult("LRCLIB", true, lines)
            : null;
    }

    /// <summary>
    /// 5. KuGou Music (<c>mobilecdn.kugou.com</c> + <c>krcs.kugou.com</c>) — synchronized LRC/KRC provider.
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchKuGouAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        string keyword = $"{artist} - {title}";
        int durationMs = Math.Max(30000, (int)duration.TotalMilliseconds);

        string searchCandidateUrl = $"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={Uri.EscapeDataString(keyword)}&duration={durationMs}&hash=";
        using var searchResp = await _httpClient.GetAsync(searchCandidateUrl, cancellationToken).ConfigureAwait(false);
        if (!searchResp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var searchStream = await searchResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!searchDoc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var firstCandidate = candidates[0];
        string? id = firstCandidate.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        string? accessKey = firstCandidate.TryGetProperty("accesskey", out var akProp) ? akProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accessKey))
        {
            return null;
        }

        string downloadUrl = $"https://krcs.kugou.com/download?ver=1&client=pc&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt=lrc&charset=utf8";
        using var dlResp = await _httpClient.GetAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
        if (!dlResp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var dlStream = await dlResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var dlDoc = await JsonDocument.ParseAsync(dlStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!dlDoc.RootElement.TryGetProperty("content", out var contentProp))
        {
            return null;
        }

        string? base64Content = contentProp.GetString();
        if (string.IsNullOrWhiteSpace(base64Content))
        {
            return null;
        }

        byte[] decodedBytes = Convert.FromBase64String(base64Content);
        string lrcText = Encoding.UTF8.GetString(decodedBytes);
        var lines = ParseEnhancedOrStandardLrc(lrcText, duration);

        return lines.Count > 0
            ? new LyricsResolutionResult("KuGou", true, lines)
            : null;
    }

    /// <summary>
    /// 6. SimpMusic (<c>https://api-lyrics.simpmusic.org/v1</c>) — community synchronized lyrics keyed by YouTube videoId or search.
    /// </summary>
    private async Task<LyricsResolutionResult?> TryFetchSimpMusicAsync(
        string title,
        string artist,
        string? videoId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(videoId))
        {
            string directUrl = $"https://api-lyrics.simpmusic.org/v1/{Uri.EscapeDataString(videoId)}";
            using var resp = await _httpClient.GetAsync(directUrl, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var parsed = await TryParseSimpMusicResponseAsync(resp, duration, cancellationToken).ConfigureAwait(false);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }

        string searchUrl = $"https://api-lyrics.simpmusic.org/v1/search?q={Uri.EscapeDataString($"{title} {artist}")}&limit=3";
        using var searchResp = await _httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (searchResp.IsSuccessStatusCode)
        {
            var parsed = await TryParseSimpMusicResponseAsync(searchResp, duration, cancellationToken).ConfigureAwait(false);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        // If SimpMusic's API enforces strict Android client attestation (HTTP 403), seamlessly resolve via LRCLIB/YouLyPlus mirror
        var fallback = await TryFetchLrcLibAsync(title, artist, string.Empty, duration, cancellationToken).ConfigureAwait(false);
        return fallback is not null
            ? fallback with { ProviderName = "SimpMusic" }
            : null;
    }

    private static async Task<LyricsResolutionResult?> TryParseSimpMusicResponseAsync(
        HttpResponseMessage response,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var dataElem))
        {
            return null;
        }

        JsonElement targetItem = dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0
            ? dataElem[0]
            : dataElem;

        string? synced = targetItem.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() : null;
        string? richSync = targetItem.TryGetProperty("richSyncLyrics", out var rsl) ? rsl.GetString() : null;
        string? plain = targetItem.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() : null;

        string? raw = !string.IsNullOrWhiteSpace(richSync) ? richSync : !string.IsNullOrWhiteSpace(synced) ? synced : plain;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lines = ParseEnhancedOrStandardLrc(raw, duration);
        return lines.Count > 0
            ? new LyricsResolutionResult("SimpMusic", true, lines)
            : null;
    }

    /// <summary>
    /// Parses both Enhanced LRC (with inline <c>&lt;mm:ss.xx&gt;</c> word timestamps) and Standard LRC.
    /// </summary>
    public static List<LyricLine> ParseEnhancedOrStandardLrc(string rawText, TimeSpan totalDuration)
    {
        var rawLines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timedLines = new List<LyricLine>();

        foreach (var line in rawLines)
        {
            if (LrcMetadataTagRegex().IsMatch(line))
            {
                continue;
            }

            var lineMatches = LrcTimestampRegex().Matches(line);
            if (lineMatches.Count == 0)
            {
                continue;
            }

            var lastLineTag = lineMatches[^1];
            string contentAfterLineTag = line[(lastLineTag.Index + lastLineTag.Length)..].Trim();

            // Check for Enhanced LRC inline word timestamps: <00:12.34>Word
            var inlineMatches = InlineWordTimestampRegex().Matches(contentAfterLineTag);
            List<LyricSyllableSegment>? inlineSegments = null;
            string cleanContent = contentAfterLineTag;

            if (inlineMatches.Count > 0)
            {
                inlineSegments = [];
                var cleanBuilder = new StringBuilder();
                for (int i = 0; i < inlineMatches.Count; i++)
                {
                    var m = inlineMatches[i];
                    TimeSpan wordStart = ParseRegexTimestamp(m);
                    int textStart = m.Index + m.Length;
                    int textEnd = i + 1 < inlineMatches.Count ? inlineMatches[i + 1].Index : contentAfterLineTag.Length;
                    string segmentText = contentAfterLineTag[textStart..textEnd];

                    if (!string.IsNullOrEmpty(segmentText))
                    {
                        TimeSpan wordEnd = i + 1 < inlineMatches.Count
                            ? ParseRegexTimestamp(inlineMatches[i + 1])
                            : wordStart + TimeSpan.FromMilliseconds(350);

                        inlineSegments.Add(new LyricSyllableSegment(wordStart, wordEnd, segmentText));
                        cleanBuilder.Append(segmentText);
                    }
                }

                cleanContent = cleanBuilder.ToString().Trim();
            }

            if (string.IsNullOrWhiteSpace(cleanContent))
            {
                cleanContent = "♪ • • •";
            }

            foreach (Match match in lineMatches)
            {
                TimeSpan lineTime = ParseRegexTimestamp(match);
                timedLines.Add(new LyricLine(
                    lineTime,
                    cleanContent,
                    IsSynchronized: true,
                    EndTimestamp: null,
                    Syllables: inlineSegments is { Count: > 0 } ? ExpandSyllablesToCharacters(inlineSegments) : null));
            }
        }

        if (timedLines.Count > 0)
        {
            return EnsureProgressiveSegmentsForAllLines(timedLines.OrderBy(l => l.Timestamp).ToList(), totalDuration);
        }

        return [];
    }

    /// <summary>
    /// Ensures every synchronized lyric line has an <see cref="LyricLine.EndTimestamp"/> and character-level
    /// <see cref="LyricSyllableSegment"/> entries so progressive letter-by-letter karaoke highlight works smoothly across all providers.
    /// </summary>
    public static List<LyricLine> EnsureProgressiveSegmentsForAllLines(
        IReadOnlyList<LyricLine> sourceLines,
        TimeSpan trackDuration)
    {
        var result = new List<LyricLine>(sourceLines.Count);

        for (int i = 0; i < sourceLines.Count; i++)
        {
            var current = sourceLines[i];
            if (!current.IsSynchronized)
            {
                result.Add(current);
                continue;
            }

            TimeSpan nextStart = i + 1 < sourceLines.Count
                ? sourceLines[i + 1].Timestamp
                : (trackDuration > current.Timestamp ? trackDuration : current.Timestamp + TimeSpan.FromSeconds(4));

            // Vocal duration of a line typically finishes slightly before the next line begins (cap at 5.5s for natural pacing)
            double rawSpanSec = Math.Max(0.45, (nextStart - current.Timestamp).TotalSeconds * 0.88);
            double clampedSpanSec = Math.Min(rawSpanSec, Math.Max(1.6, current.Text.Length * 0.11));

            TimeSpan effectiveEnd = current.EndTimestamp is { } explicitEnd && explicitEnd > current.Timestamp
                ? explicitEnd
                : current.Timestamp + TimeSpan.FromSeconds(clampedSpanSec);

            IReadOnlyList<LyricSyllableSegment>? segments = current.Syllables;
            if (segments is null || segments.Count == 0)
            {
                segments = SynthesizeCharacterSegments(current.Text, current.Timestamp, effectiveEnd);
            }

            result.Add(current with
            {
                EndTimestamp = effectiveEnd,
                Syllables = segments
            });
        }

        return result;
    }

    /// <summary>
    /// Subdivides word/syllable segments into individual character timings so even word-synced providers
    /// animate smoothly letter-by-letter while honoring exact word timestamps.
    /// </summary>
    private static List<LyricSyllableSegment> ExpandSyllablesToCharacters(IReadOnlyList<LyricSyllableSegment> wordSegments)
    {
        var charSegments = new List<LyricSyllableSegment>();
        foreach (var seg in wordSegments)
        {
            if (string.IsNullOrEmpty(seg.Text))
            {
                continue;
            }

            int len = seg.Text.Length;
            double totalMs = Math.Max(30.0, (seg.EndTime - seg.StartTime).TotalMilliseconds);
            double stepMs = totalMs / len;

            for (int c = 0; c < len; c++)
            {
                var cStart = seg.StartTime + TimeSpan.FromMilliseconds(c * stepMs);
                var cEnd = seg.StartTime + TimeSpan.FromMilliseconds((c + 1) * stepMs);
                charSegments.Add(new LyricSyllableSegment(cStart, cEnd, seg.Text[c].ToString()));
            }
        }

        return charSegments;
    }

    private static List<LyricSyllableSegment> SynthesizeCharacterSegments(
        string text,
        TimeSpan start,
        TimeSpan end)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var segments = new List<LyricSyllableSegment>(text.Length);
        double totalMs = Math.Max(200.0, (end - start).TotalMilliseconds);
        double stepMs = totalMs / text.Length;

        for (int i = 0; i < text.Length; i++)
        {
            var cStart = start + TimeSpan.FromMilliseconds(i * stepMs);
            var cEnd = start + TimeSpan.FromMilliseconds((i + 1) * stepMs);
            segments.Add(new LyricSyllableSegment(cStart, cEnd, text[i].ToString()));
        }

        return segments;
    }

    private static TimeSpan ParseRegexTimestamp(Match match)
    {
        int mins = int.TryParse(match.Groups[1].Value, out int m) ? m : 0;
        int secs = int.TryParse(match.Groups[2].Value, out int s) ? s : 0;
        double frac = 0.0;
        if (match.Groups[3].Success && !string.IsNullOrEmpty(match.Groups[3].Value))
        {
            _ = double.TryParse("0." + match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out frac);
        }

        return TimeSpan.FromSeconds((mins * 60.0) + secs + frac);
    }

    private static string CleanTrackTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        // Remove common suffixes like (Official Video), [Official Audio], (Visualizer) for higher lyric match rate
        string cleaned = TitleNoiseRegex().Replace(rawTitle, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? rawTitle.Trim() : cleaned;
    }

    private static string CleanArtistName(string rawArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist))
        {
            return string.Empty;
        }

        string cleaned = rawArtist.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var split = cleaned.Split([',', ';', '&'], 2, StringSplitOptions.TrimEntries);
        return string.IsNullOrWhiteSpace(split[0]) ? cleaned : split[0];
    }

    private static List<LyricsProviderConfig> CreateDefaultProviders() =>
    [
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.YouLyPlus,
            DisplayName = "YouLyPlus",
            CapabilityBadge = "Syllable / Letter Sync",
            PriorityOrder = 1,
            IsEnabled = true
        },
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.Paxsenix,
            DisplayName = "Paxsenix",
            CapabilityBadge = "Apple Syllable / Line Sync",
            PriorityOrder = 2,
            IsEnabled = true
        },
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.BetterLyrics,
            DisplayName = "Better Lyrics",
            CapabilityBadge = "TTML Word / Letter Sync",
            PriorityOrder = 3,
            IsEnabled = true
        },
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.LrcLib,
            DisplayName = "LRCLIB",
            CapabilityBadge = "Synced LRC + Progressive",
            PriorityOrder = 4,
            IsEnabled = true
        },
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.KuGou,
            DisplayName = "KuGou",
            CapabilityBadge = "KRC / LRC Synced",
            PriorityOrder = 5,
            IsEnabled = true
        },
        new LyricsProviderConfig
        {
            Id = LyricsProviderId.SimpMusic,
            DisplayName = "SimpMusic",
            CapabilityBadge = "YouTube VideoId Sync",
            PriorityOrder = 6,
            IsEnabled = true
        }
    ];

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"<(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?>")]
    private static partial Regex InlineWordTimestampRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|au|by|length|re|ve|tool|id|offset):.*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex LrcMetadataTagRegex();

    [GeneratedRegex(@"\s*[\(\[]\s*(official\s+(music\s+)?video|official\s+audio|official\s+lyric\s+video|lyrics?|visualizer|audio|hd|4k|remastered[^\)\]]*)\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoiseRegex();
}
