using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Playback;

/// <summary>
/// Thread-safe playback session queue controller implementing Android parity semantics:
/// retains played history (<c>0..CurrentIndex-1</c>), active track (<c>CurrentIndex</c>),
/// and upcoming queue (<c>CurrentIndex+1..Count-1</c>) alongside an incrementing <see cref="QueueEpoch"/>
/// for stale async recommendation rejection.
/// </summary>
public sealed class PlaybackQueueController
{
    private readonly object _gate = new();
    private readonly List<TrackItem> _items = [];
    private readonly List<TrackItem> _canonicalOrder = [];
    private readonly HashSet<TrackItem> _playNextPinned = new(ReferenceEqualityComparer.Instance);
    private int _currentIndex = -1;
    private long _queueEpoch;

    /// <summary>
    /// Gets the currently active track, or <c>null</c> when the queue is empty.
    /// </summary>
    public TrackItem? CurrentTrack
    {
        get
        {
            lock (_gate)
            {
                return _currentIndex >= 0 && _currentIndex < _items.Count
                    ? _items[_currentIndex]
                    : null;
            }
        }
    }

    /// <summary>
    /// Gets the zero-based index of <see cref="CurrentTrack"/> within the full session queue, or <c>-1</c> if empty.
    /// </summary>
    public int CurrentIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentIndex;
            }
        }
    }

    /// <summary>
    /// Gets the monotonically increasing epoch incremented whenever the queue is replaced
    /// via <see cref="SetQueue"/> or <see cref="PlaySingleTrack"/>.
    /// </summary>
    public long QueueEpoch
    {
        get
        {
            lock (_gate)
            {
                return _queueEpoch;
            }
        }
    }

    /// <summary>
    /// Gets the total number of tracks in the session queue (history + current + upcoming).
    /// </summary>
    public int TotalCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a previous track exists in the session history.
    /// </summary>
    public bool HasPrevious
    {
        get
        {
            lock (_gate)
            {
                return _currentIndex > 0;
            }
        }
    }

    /// <summary>
    /// Returns a value indicating whether <see cref="SkipNext(PlaybackRepeatMode)"/> can immediately advance
    /// under the specified <paramref name="repeatMode"/>.
    /// </summary>
    public bool HasNext(PlaybackRepeatMode repeatMode)
    {
        lock (_gate)
        {
            if (_items.Count == 0 || _currentIndex < 0)
            {
                return false;
            }

            return _currentIndex < _items.Count - 1 || repeatMode != PlaybackRepeatMode.None;
        }
    }

    /// <summary>
    /// Returns a snapshot of the upcoming tracks strictly after <see cref="CurrentIndex"/>.
    /// </summary>
    public IReadOnlyList<TrackItem> GetUpcomingQueue()
    {
        lock (_gate)
        {
            if (_currentIndex < 0 || _currentIndex >= _items.Count - 1)
            {
                return [];
            }

            int start = _currentIndex + 1;
            return _items.GetRange(start, _items.Count - start);
        }
    }

    /// <summary>
    /// Returns a snapshot of previously played tracks strictly before <see cref="CurrentIndex"/> in playback order.
    /// </summary>
    public IReadOnlyList<TrackItem> GetHistory()
    {
        lock (_gate)
        {
            if (_currentIndex <= 0)
            {
                return [];
            }

            return _items.GetRange(0, _currentIndex);
        }
    }

    /// <summary>
    /// Returns a snapshot of all tracks in the session queue (history, current, and upcoming).
    /// </summary>
    public IReadOnlyList<TrackItem> GetAllTracksSnapshot()
    {
        lock (_gate)
        {
            return _items.Count == 0 ? [] : [.. _items];
        }
    }

    /// <summary>
    /// Replaces the entire session queue with <paramref name="tracks"/>, clamps <paramref name="startIndex"/>,
    /// increments <see cref="QueueEpoch"/>, and returns the active track (or <c>null</c> if empty).
    /// </summary>
    public TrackItem? SetQueue(IReadOnlyList<TrackItem> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        lock (_gate)
        {
            _items.Clear();
            _canonicalOrder.Clear();
            _playNextPinned.Clear();
            foreach (var track in tracks)
            {
                if (track is not null)
                {
                    _items.Add(track);
                    _canonicalOrder.Add(track);
                }
            }

            _queueEpoch++;

            if (_items.Count == 0)
            {
                _currentIndex = -1;
                return null;
            }

            _currentIndex = Math.Clamp(startIndex, 0, _items.Count - 1);
            return _items[_currentIndex];
        }
    }

    /// <summary>
    /// Replaces the session queue starting in shuffled order while preserving <paramref name="canonicalTracks"/>
    /// so disabling Shuffle later restores the original canonical sequence.
    /// </summary>
    public TrackItem? SetQueueShuffled(
        IReadOnlyList<TrackItem> canonicalTracks,
        int? startCanonicalIndex = null,
        Random? rng = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalTracks);

        lock (_gate)
        {
            _items.Clear();
            _canonicalOrder.Clear();
            _playNextPinned.Clear();

            foreach (var track in canonicalTracks)
            {
                if (track is not null)
                {
                    _canonicalOrder.Add(track);
                }
            }

            _queueEpoch++;

            if (_canonicalOrder.Count == 0)
            {
                _currentIndex = -1;
                return null;
            }

            var random = rng ?? Random.Shared;
            int chosenIdx = startCanonicalIndex.HasValue
                ? Math.Clamp(startCanonicalIndex.Value, 0, _canonicalOrder.Count - 1)
                : random.Next(_canonicalOrder.Count);

            _items.Add(_canonicalOrder[chosenIdx]);
            for (int i = 0; i < _canonicalOrder.Count; i++)
            {
                if (i != chosenIdx)
                {
                    _items.Add(_canonicalOrder[i]);
                }
            }

            for (int i = _items.Count - 1; i > 1; i--)
            {
                int j = random.Next(1, i + 1);
                (_items[i], _items[j]) = (_items[j], _items[i]);
            }

            _currentIndex = 0;
            return _items[0];
        }
    }

    /// <summary>
    /// Replaces the session queue with a single seed track at index 0 and increments <see cref="QueueEpoch"/>.
    /// </summary>
    public TrackItem PlaySingleTrack(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return SetQueue([track], 0)!;
    }

    /// <summary>
    /// Replaces the active <see cref="CurrentTrack"/> slot in place (e.g., when resolving natural duration)
    /// without altering <see cref="QueueEpoch"/> or queue order.
    /// </summary>
    public bool ReplaceCurrentTrack(TrackItem updatedTrack)
    {
        ArgumentNullException.ThrowIfNull(updatedTrack);

        lock (_gate)
        {
            if (_currentIndex < 0 || _currentIndex >= _items.Count)
            {
                return false;
            }

            var oldTrack = _items[_currentIndex];
            _items[_currentIndex] = updatedTrack;
            if (_playNextPinned.Remove(oldTrack))
            {
                _playNextPinned.Add(updatedTrack);
            }

            int canonIdx = FindCanonicalIndexLocked(oldTrack);
            if (canonIdx >= 0)
            {
                _canonicalOrder[canonIdx] = updatedTrack;
            }

            return true;
        }
    }

    /// <summary>
    /// Advances to the next track in the queue. Returns <c>null</c> only when the queue is empty or when
    /// already at the last track with <see cref="PlaybackRepeatMode.None"/>; otherwise advances
    /// modulo queue size and returns the new <see cref="CurrentTrack"/>.
    /// </summary>
    public TrackItem? SkipNext(PlaybackRepeatMode repeatMode)
    {
        lock (_gate)
        {
            return SkipNextLocked(repeatMode);
        }
    }

    /// <summary>
    /// Epoch-guarded variant of <see cref="SkipNext(PlaybackRepeatMode)"/> that atomically verifies
    /// <paramref name="expectedEpoch"/> matches <see cref="QueueEpoch"/> before advancing.
    /// </summary>
    public TrackItem? SkipNext(PlaybackRepeatMode repeatMode, long expectedEpoch)
    {
        lock (_gate)
        {
            if (expectedEpoch != _queueEpoch)
            {
                return null;
            }

            return SkipNextLocked(repeatMode);
        }
    }

    /// <summary>
    /// Moves back one track in the session history if available; otherwise returns <c>null</c>.
    /// </summary>
    public TrackItem? SkipPrevious()
    {
        lock (_gate)
        {
            if (_currentIndex <= 0 || _items.Count == 0)
            {
                return null;
            }

            _currentIndex--;
            return _items[_currentIndex];
        }
    }

    /// <summary>
    /// Jumps directly to <c>CurrentIndex + 1 + upcomingIndex</c> without incrementing <see cref="QueueEpoch"/>,
    /// returning the newly active track or <c>null</c> if out of range.
    /// </summary>
    public TrackItem? PlayUpcomingIndex(int upcomingIndex)
    {
        lock (_gate)
        {
            if (_currentIndex < 0 || upcomingIndex < 0)
            {
                return null;
            }

            int targetIndex = _currentIndex + 1 + upcomingIndex;
            if (targetIndex >= _items.Count)
            {
                return null;
            }

            _currentIndex = targetIndex;
            return _items[_currentIndex];
        }
    }

    /// <summary>
    /// Reorders a track strictly within the upcoming queue (<c>CurrentIndex + 1 .. Count - 1</c>)
    /// without incrementing <see cref="QueueEpoch"/>.
    /// </summary>
    public bool MoveUpcomingItem(int fromUpcomingIndex, int toUpcomingIndex)
    {
        lock (_gate)
        {
            if (_currentIndex < 0)
            {
                return false;
            }

            int upcomingStart = _currentIndex + 1;
            int upcomingCount = _items.Count - upcomingStart;
            if (upcomingCount <= 1 ||
                fromUpcomingIndex < 0 || fromUpcomingIndex >= upcomingCount ||
                toUpcomingIndex < 0 || toUpcomingIndex >= upcomingCount ||
                fromUpcomingIndex == toUpcomingIndex)
            {
                return false;
            }

            int fromAbs = upcomingStart + fromUpcomingIndex;
            int toAbs = upcomingStart + toUpcomingIndex;
            var item = _items[fromAbs];
            _items.RemoveAt(fromAbs);
            _items.Insert(toAbs, item);
            return true;
        }
    }

    /// <summary>
    /// Inserts <paramref name="track"/> immediately after <see cref="CurrentIndex"/> (or initializes a single-track
    /// queue if empty) without incrementing <see cref="QueueEpoch"/>.
    /// </summary>
    public void EnqueuePlayNext(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_gate)
        {
            if (_items.Count == 0 || _currentIndex < 0)
            {
                _items.Add(track);
                _canonicalOrder.Add(track);
                _currentIndex = 0;
                return;
            }

            _items.Insert(_currentIndex + 1, track);
            _playNextPinned.Add(track);
            int canonIdx = FindCanonicalIndexLocked(_items[_currentIndex]);
            if (canonIdx >= 0 && canonIdx < _canonicalOrder.Count)
            {
                _canonicalOrder.Insert(canonIdx + 1, track);
            }
            else
            {
                _canonicalOrder.Add(track);
            }
        }
    }

    /// <summary>
    /// Appends <paramref name="track"/> to the tail of the queue (or initializes a single-track queue if empty)
    /// without incrementing <see cref="QueueEpoch"/>.
    /// </summary>
    public void EnqueueTrack(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_gate)
        {
            if (_items.Count == 0 || _currentIndex < 0)
            {
                _items.Add(track);
                _canonicalOrder.Add(track);
                _currentIndex = 0;
                return;
            }

            _items.Add(track);
            _canonicalOrder.Add(track);
        }
    }

    /// <summary>
    /// Fisher-Yates shuffles only the non-pinned upcoming portion (<c>CurrentIndex + 1 .. Count - 1</c>)
    /// without incrementing <see cref="QueueEpoch"/>, preserving canonical order for later unshuffling.
    /// </summary>
    /// <returns><c>true</c> if at least two upcoming items were reordered or shuffled; otherwise <c>false</c>.</returns>
    public bool ShuffleUpcoming(Random? rng = null)
    {
        lock (_gate)
        {
            if (_currentIndex < 0)
            {
                return false;
            }

            if (_canonicalOrder.Count == 0 && _items.Count > 0)
            {
                _canonicalOrder.AddRange(_items);
            }

            int start = _currentIndex + 1;
            int count = _items.Count - start;
            if (count <= 1)
            {
                return false;
            }

            var pinned = new List<TrackItem>();
            var unpinned = new List<TrackItem>(count);
            for (int i = start; i < _items.Count; i++)
            {
                var item = _items[i];
                if (_playNextPinned.Contains(item))
                {
                    pinned.Add(item);
                }
                else
                {
                    unpinned.Add(item);
                }
            }

            if (unpinned.Count <= 1 && pinned.Count == 0)
            {
                return false;
            }

            var random = rng ?? Random.Shared;
            for (int i = unpinned.Count - 1; i > 0; i--)
            {
                int j = random.Next(0, i + 1);
                (unpinned[i], unpinned[j]) = (unpinned[j], unpinned[i]);
            }

            int writeIdx = start;
            foreach (var p in pinned)
            {
                _items[writeIdx++] = p;
            }

            foreach (var u in unpinned)
            {
                _items[writeIdx++] = u;
            }

            return true;
        }
    }

    /// <summary>
    /// Restores the upcoming queue (<c>CurrentIndex + 1 .. Count - 1</c>) back to its canonical unshuffled sequence
    /// relative to <see cref="CurrentTrack"/>, keeping played history and pinned "Play Next" tracks intact.
    /// </summary>
    public bool UnshuffleUpcoming()
    {
        lock (_gate)
        {
            if (_currentIndex < 0 || _canonicalOrder.Count == 0)
            {
                return false;
            }

            int start = _currentIndex + 1;
            int count = _items.Count - start;
            if (count <= 1)
            {
                return false;
            }

            var current = _items[_currentIndex];
            var pinned = new List<TrackItem>();
            var remaining = new List<TrackItem>(count);
            for (int i = start; i < _items.Count; i++)
            {
                var item = _items[i];
                if (_playNextPinned.Contains(item))
                {
                    pinned.Add(item);
                }
                else
                {
                    remaining.Add(item);
                }
            }

            if (remaining.Count <= 1)
            {
                return false;
            }

            int anchorIdx = FindCanonicalIndexLocked(current);
            var usedCanonicalSlots = new HashSet<int>();
            var keyedWithRank = new List<(TrackItem Track, int CanonicalRank, int FallbackOrder)>(remaining.Count);

            for (int idx = 0; idx < remaining.Count; idx++)
            {
                var item = remaining[idx];
                int slot = FindAvailableCanonicalIndexLocked(item, anchorIdx, usedCanonicalSlots);
                if (slot >= 0)
                {
                    usedCanonicalSlots.Add(slot);
                    int rank = anchorIdx >= 0
                        ? (slot > anchorIdx ? slot - anchorIdx - 1 : (_canonicalOrder.Count - anchorIdx - 1) + slot)
                        : slot;
                    keyedWithRank.Add((item, rank, idx));
                }
                else
                {
                    keyedWithRank.Add((item, int.MaxValue, idx));
                }
            }

            keyedWithRank.Sort((a, b) =>
            {
                int cmp = a.CanonicalRank.CompareTo(b.CanonicalRank);
                return cmp != 0 ? cmp : a.FallbackOrder.CompareTo(b.FallbackOrder);
            });

            bool changed = false;
            int writeIdx = start;
            foreach (var p in pinned)
            {
                if (!ReferenceEquals(_items[writeIdx], p))
                {
                    changed = true;
                }
                _items[writeIdx++] = p;
            }

            foreach (var entry in keyedWithRank)
            {
                if (!ReferenceEquals(_items[writeIdx], entry.Track))
                {
                    changed = true;
                }
                _items[writeIdx++] = entry.Track;
            }

            return changed;
        }
    }

    /// <summary>
    /// Atomically captures the full queue, current index, and canonical unshuffled order for session persistence.
    /// </summary>
    public (IReadOnlyList<TrackItem> Items, int CurrentIndex, IReadOnlyList<TrackItem> CanonicalOrder) GetSessionSnapshot()
    {
        lock (_gate)
        {
            IReadOnlyList<TrackItem> items = _items.Count == 0 ? [] : [.. _items];
            IReadOnlyList<TrackItem> canonical = _canonicalOrder.Count == 0
                ? items
                : [.. _canonicalOrder];
            return (items, _currentIndex, canonical);
        }
    }

    /// <summary>
    /// Restores a previously persisted queue session atomically without discarding canonical unshuffled order.
    /// </summary>
    public TrackItem? RestoreSession(
        IReadOnlyList<TrackItem> items,
        int currentIndex,
        IReadOnlyList<TrackItem>? canonicalOrder = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        lock (_gate)
        {
            _items.Clear();
            _canonicalOrder.Clear();
            _playNextPinned.Clear();

            foreach (var track in items)
            {
                if (track is not null)
                {
                    _items.Add(track);
                }
            }

            if (canonicalOrder is { Count: > 0 })
            {
                foreach (var track in canonicalOrder)
                {
                    if (track is not null)
                    {
                        _canonicalOrder.Add(track);
                    }
                }
            }
            else
            {
                _canonicalOrder.AddRange(_items);
            }

            _queueEpoch++;

            if (_items.Count == 0)
            {
                _currentIndex = -1;
                return null;
            }

            _currentIndex = Math.Clamp(currentIndex, 0, _items.Count - 1);
            return _items[_currentIndex];
        }
    }

    /// <summary>
    /// Determines whether speculative prefetching of similar tracks should trigger for the current position.
    /// Eligible only when <paramref name="repeatMode"/> is <see cref="PlaybackRepeatMode.None"/> and
    /// <see cref="CurrentIndex"/> is within <paramref name="thresholdFromEnd"/> tracks of the end (including a 1-track queue).
    /// </summary>
    public bool ShouldPrefetchNext(PlaybackRepeatMode repeatMode, int thresholdFromEnd = 1)
    {
        lock (_gate)
        {
            return ShouldPrefetchNextLocked(repeatMode, thresholdFromEnd);
        }
    }

    /// <summary>
    /// Returns the normalized identifier of the last track in the session queue, or <c>null</c> if the queue is empty.
    /// </summary>
    public string? TailTrackId()
    {
        lock (_gate)
        {
            return TailTrackIdLocked();
        }
    }

    /// <summary>
    /// Atomically captures prefetch eligibility, current <see cref="QueueEpoch"/>, and <see cref="TailTrackId"/>.
    /// </summary>
    public (bool ShouldPrefetch, long Epoch, string? TailTrackId) GetPrefetchSnapshot(
        PlaybackRepeatMode repeatMode,
        int thresholdFromEnd = 1)
    {
        lock (_gate)
        {
            bool shouldPrefetch = ShouldPrefetchNextLocked(repeatMode, thresholdFromEnd);
            return (shouldPrefetch, _queueEpoch, TailTrackIdLocked());
        }
    }

    /// <summary>
    /// Collects up to <paramref name="maxSeeds"/> unique seed tracks in Android priority order:
    /// <see cref="CurrentTrack"/>, backward history (<c>CurrentIndex - 1 .. 0</c>), then tail after active
    /// (<c>Count - 1 .. CurrentIndex + 1</c>), deduplicated by normalized ID and <c>title||artist</c>.
    /// </summary>
    public IReadOnlyList<TrackItem> SeedTracksForSimilar(int maxSeeds = 5)
    {
        lock (_gate)
        {
            if (_items.Count == 0 || maxSeeds <= 0)
            {
                return [];
            }

            int activeIdx = Math.Clamp(_currentIndex, 0, _items.Count - 1);
            var seeds = new List<TrackItem>(Math.Min(maxSeeds, _items.Count));
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenTitleArtist = new HashSet<string>(StringComparer.Ordinal);

            void TryAddSeed(TrackItem track)
            {
                if (seeds.Count >= maxSeeds)
                {
                    return;
                }

                var normalizedIds = TrackDeduplicationHelper.ExtractNormalizedTrackIds(track);
                foreach (string normId in normalizedIds)
                {
                    if (seenIds.Contains(normId))
                    {
                        return;
                    }
                }

                bool hasTitle = !string.IsNullOrWhiteSpace(track.Title);
                string titleArtistKey = hasTitle
                    ? TrackDeduplicationHelper.BuildTitleArtistKey(track.Title, track.Artist)
                    : string.Empty;

                if (hasTitle && seenTitleArtist.Contains(titleArtistKey))
                {
                    return;
                }

                foreach (string normId in normalizedIds)
                {
                    seenIds.Add(normId);
                }

                if (hasTitle)
                {
                    seenTitleArtist.Add(titleArtistKey);
                }

                seeds.Add(track);
            }

            // 1. Active track
            TryAddSeed(_items[activeIdx]);

            // 2. Backward history: activeIdx - 1 down to 0
            for (int i = activeIdx - 1; i >= 0 && seeds.Count < maxSeeds; i--)
            {
                TryAddSeed(_items[i]);
            }

            // 3. Tail after active: lastIndex down to activeIdx + 1
            for (int i = _items.Count - 1; i > activeIdx && seeds.Count < maxSeeds; i--)
            {
                TryAddSeed(_items[i]);
            }

            return seeds;
        }
    }

    /// <summary>
    /// Returns a case-insensitive set containing all raw IDs, <c>ytm_</c>-stripped IDs, <c>ytm_</c>-prefixed IDs,
    /// and <see cref="TrackItem.YouTubeVideoId"/> values across the entire session queue.
    /// </summary>
    public HashSet<string> GetExcludedTrackIds()
    {
        lock (_gate)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in _items)
            {
                TrackDeduplicationHelper.AddExclusionFormsForTrack(track, excluded);
            }

            return excluded;
        }
    }

    /// <summary>
    /// Filters <paramref name="candidates"/> against the current session queue and within the batch itself,
    /// rejecting null/blank titles, duplicate normalized IDs, and duplicate <c>title||artist</c> keys.
    /// </summary>
    public IReadOnlyList<TrackItem> FilterUniqueCandidates(IEnumerable<TrackItem>? candidates)
    {
        lock (_gate)
        {
            return FilterUniqueCandidatesLocked(candidates);
        }
    }

    /// <summary>
    /// Atomically verifies <paramref name="expectedEpoch"/> matches <see cref="QueueEpoch"/>, filters unique
    /// candidates via <see cref="FilterUniqueCandidates"/>, appends them to the tail of the queue,
    /// and returns the appended tracks.
    /// </summary>
    public IReadOnlyList<TrackItem> AppendSimilarTracks(IEnumerable<TrackItem>? candidates, long expectedEpoch)
    {
        lock (_gate)
        {
            if (expectedEpoch != _queueEpoch)
            {
                return [];
            }

            var unique = FilterUniqueCandidatesLocked(candidates);
            if (unique.Count == 0)
            {
                return [];
            }

            _items.AddRange(unique);
            _canonicalOrder.AddRange(unique);

            if (_currentIndex > 30)
            {
                int remove = _currentIndex - 30;
                _items.RemoveRange(0, remove);
                _currentIndex -= remove;
            }

            return unique;
        }
    }

    private TrackItem? SkipNextLocked(PlaybackRepeatMode repeatMode)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        if (_currentIndex < 0)
        {
            _currentIndex = 0;
            return _items[0];
        }

        if (_currentIndex >= _items.Count - 1)
        {
            if (repeatMode == PlaybackRepeatMode.None)
            {
                return null;
            }

            _currentIndex = 0;
            return _items[0];
        }

        _currentIndex++;
        return _items[_currentIndex];
    }

    private bool ShouldPrefetchNextLocked(PlaybackRepeatMode repeatMode, int thresholdFromEnd)
    {
        if (repeatMode != PlaybackRepeatMode.None || _items.Count == 0 || _currentIndex < 0)
        {
            return false;
        }

        int safeThreshold = Math.Max(0, thresholdFromEnd);
        int triggerIndex = Math.Max(0, _items.Count - 1 - safeThreshold);
        return _currentIndex >= triggerIndex;
    }

    private string? TailTrackIdLocked()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        var tail = _items[^1];
        string normId = TrackDeduplicationHelper.NormalizeTrackId(tail.Id);
        if (!string.IsNullOrEmpty(normId))
        {
            return normId;
        }

        string normVideoId = TrackDeduplicationHelper.NormalizeTrackId(tail.YouTubeVideoId);
        if (!string.IsNullOrEmpty(normVideoId))
        {
            return normVideoId;
        }

        return TrackDeduplicationHelper.BuildTitleArtistKey(tail.Title, tail.Artist);
    }

    private List<TrackItem> FilterUniqueCandidatesLocked(IEnumerable<TrackItem>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        var existingTitleArtist = new HashSet<string>(StringComparer.Ordinal);

        foreach (var existing in _items)
        {
            foreach (string normId in TrackDeduplicationHelper.ExtractNormalizedTrackIds(existing))
            {
                existingIds.Add(normId);
            }

            if (!string.IsNullOrWhiteSpace(existing.Title))
            {
                existingTitleArtist.Add(TrackDeduplicationHelper.BuildTitleArtistKey(existing.Title, existing.Artist));
            }
        }

        var accepted = new List<TrackItem>();
        foreach (var candidate in candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Title))
            {
                continue;
            }

            var candidateIds = TrackDeduplicationHelper.ExtractNormalizedTrackIds(candidate);
            bool duplicateId = false;
            foreach (string cid in candidateIds)
            {
                if (existingIds.Contains(cid))
                {
                    duplicateId = true;
                    break;
                }
            }

            if (duplicateId)
            {
                continue;
            }

            string titleArtistKey = TrackDeduplicationHelper.BuildTitleArtistKey(candidate.Title, candidate.Artist);
            if (existingTitleArtist.Contains(titleArtistKey))
            {
                continue;
            }

            foreach (string cid in candidateIds)
            {
                existingIds.Add(cid);
            }

            existingTitleArtist.Add(titleArtistKey);
            accepted.Add(candidate);
        }

        return accepted;
    }

    private int FindCanonicalIndexLocked(TrackItem? track)
    {
        if (track is null || _canonicalOrder.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < _canonicalOrder.Count; i++)
        {
            if (ReferenceEquals(_canonicalOrder[i], track))
            {
                return i;
            }
        }

        string? key = BuildCanonicalMatchKey(track);
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < _canonicalOrder.Count; i++)
        {
            if (string.Equals(BuildCanonicalMatchKey(_canonicalOrder[i]), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindAvailableCanonicalIndexLocked(TrackItem track, int anchorIdx, HashSet<int> usedSlots)
    {
        int count = _canonicalOrder.Count;
        if (count == 0)
        {
            return -1;
        }

        int startSearch = anchorIdx >= 0 ? (anchorIdx + 1) % count : 0;

        for (int offset = 0; offset < count; offset++)
        {
            int idx = (startSearch + offset) % count;
            if (!usedSlots.Contains(idx) && ReferenceEquals(_canonicalOrder[idx], track))
            {
                return idx;
            }
        }

        string? key = BuildCanonicalMatchKey(track);
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int offset = 0; offset < count; offset++)
        {
            int idx = (startSearch + offset) % count;
            if (!usedSlots.Contains(idx) &&
                string.Equals(BuildCanonicalMatchKey(_canonicalOrder[idx]), key, StringComparison.OrdinalIgnoreCase))
            {
                return idx;
            }
        }

        return -1;
    }

    private static string BuildCanonicalMatchKey(TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId))
        {
            return "ytm:" + track.YouTubeVideoId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(track.Id))
        {
            return "id:" + track.Id.Trim();
        }

        if (!string.IsNullOrWhiteSpace(track.AudioSourceUri))
        {
            return "uri:" + track.AudioSourceUri.Trim();
        }

        return TrackDeduplicationHelper.BuildTitleArtistKey(track.Title, track.Artist);
    }
}
