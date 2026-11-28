using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Event payload describing the real-time playback position and duration of the audio engine.
/// </summary>
/// <param name="Position">Current playback position.</param>
/// <param name="Duration">Total duration of the active media stream.</param>
public sealed record PlaybackPositionEventArgs(TimeSpan Position, TimeSpan Duration);

/// <summary>
/// Abstraction for the low-latency local and online audio playback engine.
/// </summary>
public interface IAudioPlayerService : IDisposable
{
    /// <summary>
    /// Raised when the currently active track changes.
    /// </summary>
    event EventHandler<TrackItem?>? CurrentTrackChanged;

    /// <summary>
    /// Raised when the playback state (Playing/Paused) changes.
    /// </summary>
    event EventHandler<bool>? IsPlayingChanged;

    /// <summary>
    /// Raised periodically during playback with updated position and duration metrics.
    /// </summary>
    event EventHandler<PlaybackPositionEventArgs>? PositionChanged;

    /// <summary>
    /// Raised when the playback queue is modified.
    /// </summary>
    event EventHandler<IReadOnlyList<TrackItem>>? QueueChanged;

    /// <summary>
    /// Raised when a track fails to resolve or play, carrying a user-facing reason message.
    /// </summary>
    event EventHandler<string>? PlaybackErrorOccurred;

    /// <summary>
    /// Raised when the certified metadata/quality snapshot of the currently playing track updates.
    /// </summary>
    event EventHandler<TrackPlaybackInfo?>? CurrentTrackPlaybackInfoChanged;

    /// <summary>
    /// Gets the certified real-time metadata and received audio quality snapshot for the active track.
    /// </summary>
    TrackPlaybackInfo? CurrentTrackPlaybackInfo { get; }

    /// <summary>
    /// Gets or sets the current playback behavior settings (quality tier, gapless preloading, pause at 0 volume).
    /// </summary>
    PlaybackBehaviorSettings BehaviorSettings { get; set; }

    /// <summary>
    /// Pauses playback immediately (e.g. when a data usage limit is reached).
    /// </summary>
    void PausePlayback();

    /// <summary>
    /// Gets the currently loaded track, if any.
    /// </summary>
    TrackItem? CurrentTrack { get; }

    /// <summary>
    /// Gets a value indicating whether audio is actively playing.
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// Gets or sets the master volume level (0.0 to 1.0).
    /// </summary>
    double Volume { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether audio output is muted.
    /// </summary>
    bool IsMuted { get; set; }

    /// <summary>
    /// Gets or sets the repeat mode.
    /// </summary>
    PlaybackRepeatMode RepeatMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether shuffle playback is enabled.
    /// </summary>
    bool IsShuffleEnabled { get; set; }

    /// <summary>
    /// Gets the current playback position.
    /// </summary>
    TimeSpan Position { get; }

    /// <summary>
    /// Gets the total duration of the current track.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Gets the effective real-time playback speed multiplier (e.g., 1.22x when Nightcore DSP is active).
    /// </summary>
    double EffectivePlaybackRate { get; }

    /// <summary>
    /// Gets the speed-adjusted effective duration of the current track (<see cref="Duration"/> / <see cref="EffectivePlaybackRate"/>).
    /// </summary>
    TimeSpan EffectiveDuration { get; }

    /// <summary>
    /// Gets the current upcoming playback queue.
    /// </summary>
    IReadOnlyList<TrackItem> Queue { get; }

    /// <summary>
    /// Gets or sets an optional offline/catalog fallback provider invoked when remote similar-track
    /// recommendations fail or return no unique candidates at queue exhaustion.
    /// </summary>
    Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? FallbackSimilarTracksProvider { get; set; }

    /// <summary>
    /// Replaces the session playback queue with <paramref name="tracks"/> and starts loading the track at
    /// <paramref name="startIndex"/>, bumping the queue epoch and triggering speculative prefetching when eligible.
    /// </summary>
    /// <param name="tracks">Ordered list of tracks forming the new playback session queue.</param>
    /// <param name="startIndex">Zero-based index of the track to load/play.</param>
    /// <param name="autoPlay">Whether to start playback immediately or load in paused state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetQueueAsync(
        IReadOnlyList<TrackItem> tracks,
        int startIndex = 0,
        bool autoPlay = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the session playback queue starting in shuffled order while remembering
    /// <paramref name="canonicalTracks"/> so turning Shuffle off restores the original sequence.
    /// </summary>
    Task SetShuffledQueueAsync(
        IReadOnlyList<TrackItem> canonicalTracks,
        int? startCanonicalIndex = null,
        bool autoPlay = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the active queue, canonical order, current track index, playback progress, shuffle/repeat, and volume.
    /// </summary>
    PersistedPlaybackSessionState? CapturePlaybackSessionSnapshot();

    /// <summary>
    /// Restores a previously persisted playback session into the paused deferred state with exact progress intact.
    /// </summary>
    bool RestorePlaybackSession(PersistedPlaybackSessionState session);

    /// <summary>
    /// Replaces the session playback queue with a single seed <paramref name="track"/> and immediately triggers
    /// speculative similar-track prefetching when eligible.
    /// </summary>
    /// <param name="track">The single seed track to play.</param>
    /// <param name="autoPlay">Whether to start playback immediately or load in paused state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PlaySingleTrackAsync(
        TrackItem track,
        bool autoPlay = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads and plays the specified track, optionally replacing the upcoming queue.
    /// </summary>
    /// <param name="track">The track to play.</param>
    /// <param name="upcomingQueue">Optional queue of upcoming tracks.</param>
    /// <param name="autoPlay">Whether to start playback immediately or load in paused state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PlayTrackAsync(
        TrackItem track,
        IEnumerable<TrackItem>? upcomingQueue = null,
        bool autoPlay = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles between Play and Pause states.
    /// </summary>
    Task TogglePlayPauseAsync();

    /// <summary>
    /// Advances playback to the next track in the queue.
    /// </summary>
    Task NextAsync();

    /// <summary>
    /// Returns to the previous track or restarts the current track.
    /// </summary>
    Task PreviousAsync();

    /// <summary>
    /// Seeks to a specific timestamp within the current track.
    /// </summary>
    /// <param name="position">Target playback timestamp.</param>
    void Seek(TimeSpan position);

    /// <summary>
    /// Seeks relative to the current playback position (e.g. -5s or +10s).
    /// </summary>
    /// <param name="delta">Relative time offset.</param>
    void SeekRelative(TimeSpan delta);

    /// <summary>
    /// Cycles through repeat modes (None -> RepeatAll -> RepeatOne -> None).
    /// </summary>
    /// <returns>The newly active <see cref="PlaybackRepeatMode"/>.</returns>
    PlaybackRepeatMode CycleRepeatMode();

    /// <summary>
    /// Toggles shuffle mode and optionally shuffles the current upcoming queue.
    /// </summary>
    /// <returns>The updated shuffle state.</returns>
    bool ToggleShuffle();

    /// <summary>
    /// Appends a track to the end of the upcoming queue.
    /// </summary>
    /// <param name="track">Track to enqueue.</param>
    void EnqueueTrack(TrackItem track);

    /// <summary>
    /// Inserts a track at the front of the upcoming queue so it plays immediately next.
    /// </summary>
    /// <param name="track">Track to play next.</param>
    void EnqueuePlayNext(TrackItem track);

    /// <summary>
    /// Moves a track within the upcoming queue from <paramref name="fromIndex"/> to <paramref name="toIndex"/>.
    /// </summary>
    /// <param name="fromIndex">Zero-based source index in <see cref="Queue"/>.</param>
    /// <param name="toIndex">Zero-based destination index in <see cref="Queue"/>.</param>
    void MoveQueueItem(int fromIndex, int toIndex);

    /// <summary>
    /// Plays the track at <paramref name="index"/> in the upcoming queue and advances the queue cleanly.
    /// </summary>
    /// <param name="index">Zero-based index in <see cref="Queue"/>.</param>
    Task PlayQueueIndexAsync(int index);

    /// <summary>
    /// Gets whether the active track is currently playing the Official Music Video (<c>OMV</c>) audio variant.
    /// </summary>
    bool IsVideoAudioModeActive { get; }

    /// <summary>
    /// Switches the active track's audio stream seamlessly between the studio Song (<c>ATV</c>) and
    /// Official Music Video (<c>OMV</c>) audio track, mapping the playback timestamp to the exact corresponding moment.
    /// </summary>
    Task<bool> SwitchTrackAudioVariantAsync(
        string? officialVideoId,
        bool useVideoAudio,
        Func<string?, string?, TimeSpan, TimeSpan>? resolveTargetPosition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Samples the current real-time 64-band frequency spectrum (0.0 to 1.0) and sub-band energies
    /// for 60/120 FPS GPU music visualizers.
    /// </summary>
    /// <param name="spectrumBands">Destination span (typically 64 floats) to receive normalized band magnitudes.</param>
    /// <param name="bassEnergy">Normalized low-frequency bass energy (20-250 Hz).</param>
    /// <param name="midEnergy">Normalized mid-frequency vocal/harmonic energy (250-2500 Hz).</param>
    /// <param name="trebleEnergy">Normalized high-frequency treble energy (2.5-16 kHz).</param>
    void SampleSpectrum(
        Span<float> spectrumBands,
        out float bassEnergy,
        out float midEnergy,
        out float trebleEnergy);
}
