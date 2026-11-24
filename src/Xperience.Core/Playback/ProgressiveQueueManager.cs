using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Playback;

/// <summary>
/// Coordinates speculative similar-track prefetching, single in-flight fetch deduplication,
/// epoch invalidation, offline fallback resolution, and end-of-queue auto-advancement
/// with exact Android parity.
/// </summary>
public sealed class ProgressiveQueueManager : IDisposable
{
    /// <summary>
    /// Default number of similar tracks requested per batch.
    /// </summary>
    public const int DefaultBatchSize = 20;

    /// <summary>
    /// Remaining-track threshold from queue end at which speculative prefetch triggers (1 = penultimate or last track).
    /// </summary>
    public const int PrefetchThresholdFromEnd = 1;

    /// <summary>
    /// Maximum number of unique seed tracks collected from the session queue per fetch.
    /// </summary>
    public const int MaxSeeds = 5;

    private readonly PlaybackQueueController _queueController;
    private readonly Func<IReadOnlyList<TrackItem>, IReadOnlyCollection<string>, int, CancellationToken, Task<IReadOnlyList<TrackItem>>> _fetchSimilarTracksAsync;
    private readonly Action? _onQueueUpdated;
    private readonly Func<TrackItem, Task>? _onAutoAdvanceAsync;
    private readonly Func<Task>? _onQueueEndedExhaustedAsync;
    private readonly Action<string, Exception?>? _log;
    private readonly object _stateLock = new();

    private Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? _fallbackSimilarTracksProvider;
    private (long Epoch, string? TailTrackId)? _lastPrefetchedTailKey;
    private long _inFlightAppendEpoch = -1;
    private Task<IReadOnlyList<TrackItem>>? _inFlightAppendTask;
    private CancellationTokenSource? _inFlightAppendCts;
    private Task? _endAdvanceTask;
    private CancellationTokenSource? _endAdvanceCts;
    private bool _isDisposed;

    /// <summary>
    /// Gets or sets the optional offline/catalog fallback provider invoked when remote similar-track
    /// fetching returns zero unique candidates or fails.
    /// </summary>
    public Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? FallbackSimilarTracksProvider
    {
        get
        {
            lock (_stateLock)
            {
                return _fallbackSimilarTracksProvider;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _fallbackSimilarTracksProvider = value;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an end-of-queue resolution and auto-advance operation is currently in progress.
    /// </summary>
    public bool IsEndAdvanceActive
    {
        get
        {
            lock (_stateLock)
            {
                return _endAdvanceTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressiveQueueManager"/> class.
    /// </summary>
    public ProgressiveQueueManager(
        PlaybackQueueController queueController,
        Func<IReadOnlyList<TrackItem>, IReadOnlyCollection<string>, int, CancellationToken, Task<IReadOnlyList<TrackItem>>> fetchSimilarTracksAsync,
        Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? fallbackSimilarTracksProvider = null,
        Action? onQueueUpdated = null,
        Func<TrackItem, Task>? onAutoAdvanceAsync = null,
        Func<Task>? onQueueEndedExhaustedAsync = null,
        Action<string, Exception?>? log = null)
    {
        _queueController = queueController ?? throw new ArgumentNullException(nameof(queueController));
        _fetchSimilarTracksAsync = fetchSimilarTracksAsync ?? throw new ArgumentNullException(nameof(fetchSimilarTracksAsync));
        _fallbackSimilarTracksProvider = fallbackSimilarTracksProvider;
        _onQueueUpdated = onQueueUpdated;
        _onAutoAdvanceAsync = onAutoAdvanceAsync;
        _onQueueEndedExhaustedAsync = onQueueEndedExhaustedAsync;
        _log = log;
    }

    /// <summary>
    /// Cancels all in-flight append and end-advance jobs and clears the last prefetched tail key.
    /// Call whenever the queue is replaced (epoch incremented).
    /// </summary>
    public void OnQueueReplaced()
    {
        CancellationTokenSource? appendCts;
        CancellationTokenSource? endCts;

        lock (_stateLock)
        {
            _lastPrefetchedTailKey = null;

            appendCts = _inFlightAppendCts;
            _inFlightAppendCts = null;
            _inFlightAppendTask = null;
            _inFlightAppendEpoch = -1;

            endCts = _endAdvanceCts;
            _endAdvanceCts = null;
            _endAdvanceTask = null;
        }

        CancelSafe(appendCts);
        CancelSafe(endCts);
    }

    /// <summary>
    /// Cancels any pending end-of-queue resolution job without disturbing a valid in-flight prefetch
    /// for the current epoch (used on manual jump, previous track, or repeat mode change).
    /// </summary>
    public void CancelEndAdvance()
    {
        CancellationTokenSource? endCts;
        lock (_stateLock)
        {
            endCts = _endAdvanceCts;
            _endAdvanceCts = null;
            _endAdvanceTask = null;
        }

        CancelSafe(endCts);
    }

    /// <summary>
    /// Evaluates prefetch eligibility and, when eligible, starts a background fetch of up to
    /// <see cref="DefaultBatchSize"/> similar tracks. Deduplicates concurrent fetches for the same epoch
    /// and avoids repeating a prefetch for the same <c>(epoch, tailTrackId)</c>.
    /// </summary>
    public void MaybePrefetch(PlaybackRepeatMode repeatMode)
    {
        var (shouldPrefetch, epoch, tailTrackId) = _queueController.GetPrefetchSnapshot(
            repeatMode,
            PrefetchThresholdFromEnd);

        if (!shouldPrefetch)
        {
            return;
        }

        Task<IReadOnlyList<TrackItem>>? appendTask;
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            var tailKey = (epoch, tailTrackId);
            if (_lastPrefetchedTailKey.HasValue && _lastPrefetchedTailKey.Value == tailKey)
            {
                return;
            }

            if (_inFlightAppendTask is { IsCompleted: false } && _inFlightAppendEpoch == epoch)
            {
                return;
            }

            _lastPrefetchedTailKey = tailKey;
            appendTask = StartAppendTaskLocked(epoch);
        }

        _ = ObservePrefetchCompletionAsync(appendTask, epoch);
    }

    /// <summary>
    /// Resolves end-of-queue exhaustion under <paramref name="repeatMode"/>: checks for immediately available
    /// prefetched tracks first, else awaits/reuses an in-flight append (or launches a forced fresh fetch),
    /// and invokes either <c>onAutoAdvanceAsync</c> or <c>onQueueEndedExhaustedAsync</c>.
    /// Guards against duplicate concurrent end-advance executions.
    /// </summary>
    public Task ResolveAndAdvanceFromEndAsync(PlaybackRepeatMode repeatMode)
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

            if (_endAdvanceTask is { IsCompleted: false } existing)
            {
                return existing;
            }

            long expectedEpoch = _queueController.QueueEpoch;
            var cts = new CancellationTokenSource();
            _endAdvanceCts = cts;
            var task = Task.Run(() => ExecuteResolveAndAdvanceCoreAsync(repeatMode, expectedEpoch, cts));
            _endAdvanceTask = task;
            return task;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? appendCts;
        CancellationTokenSource? endCts;

        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _lastPrefetchedTailKey = null;

            appendCts = _inFlightAppendCts;
            _inFlightAppendCts = null;
            _inFlightAppendTask = null;
            _inFlightAppendEpoch = -1;

            endCts = _endAdvanceCts;
            _endAdvanceCts = null;
            _endAdvanceTask = null;
        }

        CancelSafe(appendCts);
        CancelSafe(endCts);
    }

    private Task<IReadOnlyList<TrackItem>> EnsureAppendSimilarAsync(long expectedEpoch, bool force)
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return Task.FromResult<IReadOnlyList<TrackItem>>([]);
            }

            if (_inFlightAppendTask is { IsCompleted: false } existing && _inFlightAppendEpoch == expectedEpoch)
            {
                return existing;
            }

            if (!force)
            {
                var tailKey = (expectedEpoch, _queueController.TailTrackId());
                if (_lastPrefetchedTailKey.HasValue && _lastPrefetchedTailKey.Value == tailKey)
                {
                    return Task.FromResult<IReadOnlyList<TrackItem>>([]);
                }

                _lastPrefetchedTailKey = tailKey;
            }

            return StartAppendTaskLocked(expectedEpoch);
        }
    }

    private Task<IReadOnlyList<TrackItem>> StartAppendTaskLocked(long expectedEpoch)
    {
        var cts = new CancellationTokenSource();
        _inFlightAppendCts = cts;
        _inFlightAppendEpoch = expectedEpoch;
        var task = Task.Run(() => ExecuteAppendSimilarCoreAsync(expectedEpoch, cts));
        _inFlightAppendTask = task;
        return task;
    }

    private async Task<IReadOnlyList<TrackItem>> ExecuteAppendSimilarCoreAsync(
        long expectedEpoch,
        CancellationTokenSource cts)
    {
        try
        {
            var ct = cts.Token;
            if (ct.IsCancellationRequested || expectedEpoch != _queueController.QueueEpoch)
            {
                return [];
            }

            var seeds = _queueController.SeedTracksForSimilar(MaxSeeds);
            if (seeds.Count == 0)
            {
                return [];
            }

            var excludedIds = _queueController.GetExcludedTrackIds();
            IReadOnlyList<TrackItem> appended = [];

            try
            {
                var remoteCandidates = await _fetchSimilarTracksAsync(
                    seeds,
                    excludedIds,
                    DefaultBatchSize,
                    ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                if (remoteCandidates is { Count: > 0 })
                {
                    appended = _queueController.AppendSimilarTracks(remoteCandidates, expectedEpoch);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke("Remote similar-tracks fetch failed; falling back to offline provider if available.", ex);
            }

            if (appended.Count == 0 && !ct.IsCancellationRequested && expectedEpoch == _queueController.QueueEpoch)
            {
                Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? fallback;
                lock (_stateLock)
                {
                    fallback = _fallbackSimilarTracksProvider;
                }

                if (fallback is not null)
                {
                    try
                    {
                        var latestExcludedIds = _queueController.GetExcludedTrackIds();
                        var fallbackCandidates = fallback(seeds, latestExcludedIds, DefaultBatchSize);
                        if (fallbackCandidates is { Count: > 0 })
                        {
                            appended = _queueController.AppendSimilarTracks(fallbackCandidates, expectedEpoch);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke("Fallback similar-tracks provider failed.", ex);
                    }
                }
            }

            return appended;
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_inFlightAppendCts, cts))
                {
                    _inFlightAppendCts = null;
                    _inFlightAppendTask = null;
                    _inFlightAppendEpoch = -1;
                }
            }

            cts.Dispose();
        }
    }

    private async Task ObservePrefetchCompletionAsync(Task<IReadOnlyList<TrackItem>> appendTask, long expectedEpoch)
    {
        try
        {
            var appended = await appendTask.ConfigureAwait(false);
            if (appended.Count == 0 || expectedEpoch != _queueController.QueueEpoch)
            {
                return;
            }

            bool suppressNotify;
            lock (_stateLock)
            {
                suppressNotify = _isDisposed || _endAdvanceTask is { IsCompleted: false };
            }

            if (!suppressNotify)
            {
                _onQueueUpdated?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the queue is replaced or disposed during speculative prefetch
        }
        catch (Exception ex)
        {
            _log?.Invoke("Speculative prefetch observation encountered an error.", ex);
        }
    }

    private bool IsEndAdvanceStillActive(CancellationTokenSource cts, long expectedEpoch)
    {
        lock (_stateLock)
        {
            return !_isDisposed &&
                   ReferenceEquals(_endAdvanceCts, cts) &&
                   !cts.IsCancellationRequested &&
                   expectedEpoch == _queueController.QueueEpoch;
        }
    }

    private bool TrySkipNextWhileActive(
        PlaybackRepeatMode repeatMode,
        long expectedEpoch,
        CancellationTokenSource cts,
        out TrackItem? nextTrack)
    {
        lock (_stateLock)
        {
            if (_isDisposed ||
                !ReferenceEquals(_endAdvanceCts, cts) ||
                cts.IsCancellationRequested ||
                expectedEpoch != _queueController.QueueEpoch)
            {
                nextTrack = null;
                return false;
            }

            nextTrack = _queueController.SkipNext(repeatMode, expectedEpoch);
            return true;
        }
    }

    private async Task ExecuteResolveAndAdvanceCoreAsync(
        PlaybackRepeatMode repeatMode,
        long expectedEpoch,
        CancellationTokenSource cts)
    {
        TrackItem? advancedTrack = null;
        bool wasCancelled = false;

        try
        {
            var ct = cts.Token;

            // 1. Atomically verify active non-cancelled state and re-check immediate SkipNext
            //    in case speculative prefetch already completed.
            if (!TrySkipNextWhileActive(repeatMode, expectedEpoch, cts, out var immediateNext))
            {
                wasCancelled = true;
                return;
            }

            if (immediateNext is not null)
            {
                if (IsEndAdvanceStillActive(cts, expectedEpoch))
                {
                    advancedTrack = immediateNext;
                    if (_onAutoAdvanceAsync is not null)
                    {
                        await _onAutoAdvanceAsync(immediateNext).ConfigureAwait(false);
                    }
                }
                else
                {
                    wasCancelled = true;
                }

                return;
            }

            // 2. If repeat mode is not None, queue is genuinely empty -> exhausted
            if (repeatMode != PlaybackRepeatMode.None)
            {
                if (IsEndAdvanceStillActive(cts, expectedEpoch))
                {
                    if (_onQueueEndedExhaustedAsync is not null)
                    {
                        await _onQueueEndedExhaustedAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    wasCancelled = true;
                }

                return;
            }

            if (!IsEndAdvanceStillActive(cts, expectedEpoch))
            {
                wasCancelled = true;
                return;
            }

            // 3. Await reused in-flight prefetch or start a forced fresh fetch
            try
            {
                var appendTask = EnsureAppendSimilarAsync(expectedEpoch, force: true);
                await appendTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke("End-of-queue similar-tracks resolution threw an unexpected error.", ex);
            }

            // 4. Atomically verify active non-cancelled state & epoch before retrying SkipNext
            if (!TrySkipNextWhileActive(repeatMode, expectedEpoch, cts, out var nextTrack))
            {
                wasCancelled = true;
                return;
            }

            if (nextTrack is not null)
            {
                if (IsEndAdvanceStillActive(cts, expectedEpoch))
                {
                    advancedTrack = nextTrack;
                    if (_onAutoAdvanceAsync is not null)
                    {
                        await _onAutoAdvanceAsync(nextTrack).ConfigureAwait(false);
                    }
                }
                else
                {
                    wasCancelled = true;
                }
            }
            else if (IsEndAdvanceStillActive(cts, expectedEpoch))
            {
                if (_onQueueEndedExhaustedAsync is not null)
                {
                    await _onQueueEndedExhaustedAsync().ConfigureAwait(false);
                }
            }
            else
            {
                wasCancelled = true;
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        finally
        {
            wasCancelled |= cts.IsCancellationRequested;

            lock (_stateLock)
            {
                if (ReferenceEquals(_endAdvanceCts, cts))
                {
                    _endAdvanceCts = null;
                    _endAdvanceTask = null;
                }
            }

            cts.Dispose();
        }

        // Trigger follow-up prefetch only AFTER clearing active end-advance state so onQueueUpdated isn't suppressed
        if (advancedTrack is not null && !wasCancelled && expectedEpoch == _queueController.QueueEpoch)
        {
            MaybePrefetch(repeatMode);
        }
    }

    private static void CancelSafe(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by completing task
        }
    }
}
