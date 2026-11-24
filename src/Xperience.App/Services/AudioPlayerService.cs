using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xperience.App.Services.Audio;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Playback;

namespace Xperience.App.Services;

/// <summary>
/// Production audio playback engine powered by <see cref="NightcoreDspEngine"/> (Windows <c>AudioGraph</c>
/// real-time Biquad/Dynamics DSP &amp; coupled speed/pitch resampler) and <see cref="MediaPlayer"/>
/// supporting both local files and online streaming URIs with non-blocking position telemetry and
/// progressive similar-track queueing.
/// </summary>
public sealed class AudioPlayerService : IAudioPlayerService
{
    private readonly IYouTubeMusicService _youTubeMusicService;
    private readonly NightcoreDspEngine _dspEngine;
    private readonly ILogger<AudioPlayerService> _logger;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Timer _telemetryTimer;
    private readonly PlaybackQueueController _queueController = new();
    private readonly ProgressiveQueueManager _progressiveQueueManager;
    private readonly object _loadCtsLock = new();
    private CancellationTokenSource? _activeLoadCts;
    private readonly float[] _smoothedBands = new float[64];
    private float[] _trackBassEnvelope = [];
    private float[] _trackMidEnvelope = [];
    private float[] _trackTrebleEnvelope = [];
    private int _trackSeed = 1337;
    private int _activePlayRequestId;
    private int _repeatMode = (int)PlaybackRepeatMode.None;
    private volatile bool _isPlaying;
    private volatile bool _isShuffleEnabled;
    private volatile bool _isLoadingMedia;
    private volatile bool _isCurrentTrackDeferred;
    private volatile bool _pendingAutoPlayAfterLoad;
    private long _pendingLoadSeekTicks = -1;
    private long _pendingMediaPlayerSeekTicks = -1;
    private double _volume = 0.85;
    private bool _isMuted;
    private TimeSpan _lastSessionPosition = TimeSpan.Zero;
    private long _lastSessionTick = Stopwatch.GetTimestamp();
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public event EventHandler<TrackItem?>? CurrentTrackChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? IsPlayingChanged;

    /// <inheritdoc />
    public event EventHandler<PlaybackPositionEventArgs>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<TrackItem>>? QueueChanged;

    /// <inheritdoc />
    public event EventHandler<string>? PlaybackErrorOccurred;

    /// <inheritdoc />
    public TrackItem? CurrentTrack => _queueController.CurrentTrack;

    /// <inheritdoc />
    public bool IsPlaying => _isPlaying;

    /// <inheritdoc />
    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            _volume = clamped;
            _mediaPlayer.Volume = clamped;
            _dspEngine.SetMasterVolume(clamped, _isMuted);
        }
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            _mediaPlayer.IsMuted = value;
            _dspEngine.SetMasterVolume(_volume, value);
        }
    }

    /// <inheritdoc />
    public PlaybackRepeatMode RepeatMode
    {
        get => (PlaybackRepeatMode)Volatile.Read(ref _repeatMode);
        set
        {
            Volatile.Write(ref _repeatMode, (int)value);
            if (IsDisposed)
            {
                return;
            }

            if (value != PlaybackRepeatMode.None)
            {
                _progressiveQueueManager.CancelEndAdvance();
            }

            _progressiveQueueManager.MaybePrefetch(value);
        }
    }

    /// <inheritdoc />
    public bool IsShuffleEnabled
    {
        get => _isShuffleEnabled;
        set => _isShuffleEnabled = value;
    }

    /// <inheritdoc />
    public TimeSpan Position { get; private set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Duration { get; private set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public double EffectivePlaybackRate => _dspEngine.CurrentPlaybackRate;

    /// <inheritdoc />
    public TimeSpan EffectiveDuration
    {
        get
        {
            double rate = Math.Max(0.25, _dspEngine.CurrentPlaybackRate);
            return Duration > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Duration.TotalSeconds / rate)
                : TimeSpan.Zero;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TrackItem> Queue => _queueController.GetUpcomingQueue();

    /// <inheritdoc />
    public Func<IReadOnlyList<TrackItem>, IReadOnlySet<string>, int, IReadOnlyList<TrackItem>>? FallbackSimilarTracksProvider
    {
        get => _progressiveQueueManager.FallbackSimilarTracksProvider;
        set => _progressiveQueueManager.FallbackSimilarTracksProvider = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioPlayerService"/> class.
    /// </summary>
    public AudioPlayerService(
        IYouTubeMusicService youTubeMusicService,
        NightcoreDspEngine dspEngine,
        ILogger<AudioPlayerService> logger)
    {
        _youTubeMusicService = youTubeMusicService;
        _dspEngine = dspEngine;
        _logger = logger;
        _mediaPlayer = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            Volume = 0.85
        };

        _dspEngine.AttachMediaPlayer(_mediaPlayer);
        _dspEngine.TrackPlaybackCompleted += OnDspGraphTrackCompleted;
        _dspEngine.EffectiveRateChanged += OnDspEffectiveRateChanged;

        _progressiveQueueManager = new ProgressiveQueueManager(
            queueController: _queueController,
            fetchSimilarTracksAsync: (seeds, excludedIds, maxCount, ct) =>
                _youTubeMusicService.FetchSimilarTracksForQueueAsync(seeds, excludedIds, maxCount, ct),
            fallbackSimilarTracksProvider: null,
            onQueueUpdated: EmitQueueChanged,
            onAutoAdvanceAsync: OnProgressiveAutoAdvanceAsync,
            onQueueEndedExhaustedAsync: OnQueueEndedExhaustedAsync,
            log: (message, ex) =>
            {
                if (ex is not null)
                {
                    _logger.LogWarning(ex, "{Message}", message);
                }
                else
                {
                    _logger.LogDebug("{Message}", message);
                }
            });

        _mediaPlayer.PlaybackSession.PositionChanged += OnPlaybackSessionPositionChanged;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _mediaPlayer.MediaOpened += OnMediaOpened;
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.MediaFailed += OnMediaFailed;

        _telemetryTimer = new Timer(OnTelemetryTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public async Task SetQueueAsync(
        IReadOnlyList<TrackItem> tracks,
        int startIndex = 0,
        bool autoPlay = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (IsDisposed)
        {
            return;
        }

        var activeTrack = _queueController.SetQueue(tracks, startIndex);
        _progressiveQueueManager.OnQueueReplaced();
        EmitQueueChanged();

        if (activeTrack is null)
        {
            _isCurrentTrackDeferred = false;
            HandleEmptyQueueCleared();
            return;
        }

        if (!autoPlay)
        {
            // Defer decoder, AudioGraph, and YouTube WebView2 spin-up until user initiates playback or seeks
            Interlocked.Increment(ref _activePlayRequestId);
            CancelActiveLoadCts();
            _isLoadingMedia = false;
            _isPlaying = false;
            _isCurrentTrackDeferred = true;
            _pendingAutoPlayAfterLoad = false;
            Interlocked.Exchange(ref _pendingLoadSeekTicks, -1L);
            Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            _dspEngine.ResetAndCleanupCurrentTrackNodes();
            try
            {
                _mediaPlayer.Pause();
                ReplaceMediaSource(_mediaPlayer, null);
            }
            catch
            {
                // Ignore transient pause/source reset state
            }

            Duration = activeTrack.Duration;
            Position = TimeSpan.Zero;
            _lastSessionPosition = TimeSpan.Zero;
            _lastSessionTick = Stopwatch.GetTimestamp();
            _trackSeed = StringComparer.OrdinalIgnoreCase.GetHashCode(activeTrack.Id ?? activeTrack.Title);

            CurrentTrackChanged?.Invoke(this, activeTrack);
            IsPlayingChanged?.Invoke(this, false);
            PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
            return;
        }

        _isCurrentTrackDeferred = false;
        _isLoadingMedia = true;
        await LoadAndPlayTrackAsync(
            activeTrack,
            autoPlay: true,
            triggerPrefetch: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PlaySingleTrackAsync(
        TrackItem track,
        bool autoPlay = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (IsDisposed)
        {
            return;
        }

        _isLoadingMedia = true;
        var activeTrack = _queueController.PlaySingleTrack(track);
        _progressiveQueueManager.OnQueueReplaced();
        EmitQueueChanged();

        await LoadAndPlayTrackAsync(
            activeTrack,
            autoPlay: autoPlay,
            triggerPrefetch: autoPlay,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PlayTrackAsync(
        TrackItem track,
        IEnumerable<TrackItem>? upcomingQueue = null,
        bool autoPlay = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (upcomingQueue is not null)
        {
            var combined = new List<TrackItem> { track };
            foreach (var upcoming in upcomingQueue)
            {
                if (upcoming is not null)
                {
                    combined.Add(upcoming);
                }
            }

            return SetQueueAsync(
                combined,
                startIndex: 0,
                autoPlay: autoPlay,
                cancellationToken: cancellationToken);
        }

        return PlayTrackPreservingQueueAsync(track, autoPlay, cancellationToken);
    }

    private async Task PlayTrackPreservingQueueAsync(
        TrackItem track,
        bool autoPlay,
        CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return;
        }

        _progressiveQueueManager.CancelEndAdvance();

        var current = _queueController.CurrentTrack;
        if (current is not null &&
            (ReferenceEquals(current, track) ||
             (!string.IsNullOrWhiteSpace(track.Id) &&
              string.Equals(current.Id, track.Id, StringComparison.OrdinalIgnoreCase))))
        {
            _queueController.ReplaceCurrentTrack(track);
        }
        else if (_queueController.TotalCount == 0)
        {
            _queueController.EnqueueTrack(track);
        }
        else
        {
            int matchedUpcomingIndex = -1;
            if (!string.IsNullOrWhiteSpace(track.Id))
            {
                var upcoming = _queueController.GetUpcomingQueue();
                for (int i = 0; i < upcoming.Count; i++)
                {
                    if (string.Equals(upcoming[i].Id, track.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedUpcomingIndex = i;
                        break;
                    }
                }
            }

            if (matchedUpcomingIndex >= 0 && _queueController.PlayUpcomingIndex(matchedUpcomingIndex) is not null)
            {
                _queueController.ReplaceCurrentTrack(track);
            }
            else
            {
                _queueController.EnqueuePlayNext(track);
                _queueController.PlayUpcomingIndex(0);
            }
        }

        EmitQueueChanged();
        await LoadAndPlayTrackAsync(
            track,
            autoPlay: autoPlay,
            triggerPrefetch: autoPlay,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenSource ReplaceActiveLoadCts(CancellationToken callerToken)
    {
        CancellationTokenSource nextCts = callerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(callerToken)
            : new CancellationTokenSource();

        CancellationTokenSource? previous;
        lock (_loadCtsLock)
        {
            previous = _activeLoadCts;
            _activeLoadCts = nextCts;
        }

        if (previous is not null)
        {
            try { previous.Cancel(); } catch { }
            try { previous.Dispose(); } catch { }
        }

        return nextCts;
    }

    private void CancelActiveLoadCts()
    {
        CancellationTokenSource? previous;
        lock (_loadCtsLock)
        {
            previous = _activeLoadCts;
            _activeLoadCts = null;
        }

        if (previous is not null)
        {
            try { previous.Cancel(); } catch { }
            try { previous.Dispose(); } catch { }
        }
    }

    private async Task LoadAndPlayTrackAsync(
        TrackItem track,
        bool autoPlay,
        bool triggerPrefetch,
        CancellationToken cancellationToken,
        TimeSpan? initialSeekPosition = null,
        bool notifyTrackChanged = true)
    {
        if (IsDisposed)
        {
            return;
        }

        int requestId = Interlocked.Increment(ref _activePlayRequestId);
        var loadCts = ReplaceActiveLoadCts(cancellationToken);
        var linkedToken = loadCts.Token;
        _isCurrentTrackDeferred = false;
        _pendingAutoPlayAfterLoad = autoPlay;
        _isLoadingMedia = true;
        Interlocked.Exchange(
            ref _pendingLoadSeekTicks,
            initialSeekPosition is { } requestedSeekInit && requestedSeekInit > TimeSpan.Zero
                ? requestedSeekInit.Ticks
                : -1L);
        Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);

        // Immediately stop & silence outgoing playback BEFORE any network/file resolution begins
        _isPlaying = false;
        SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
        _dspEngine.ResetAndCleanupCurrentTrackNodes();
        try
        {
            _mediaPlayer.Pause();
            ReplaceMediaSource(_mediaPlayer, null);
        }
        catch
        {
            // Ignore transient pause/source reset state
        }

        if (track.Duration > TimeSpan.Zero || notifyTrackChanged)
        {
            Duration = track.Duration;
        }

        Position = initialSeekPosition is { } requestedSeek && requestedSeek > TimeSpan.Zero
            ? requestedSeek
            : TimeSpan.Zero;
        _lastSessionPosition = Position;
        _lastSessionTick = Stopwatch.GetTimestamp();
        _trackSeed = StringComparer.OrdinalIgnoreCase.GetHashCode(track.Id ?? track.Title);
        _ = Task.Run(() => BuildTrackAcousticEnvelope(track.AudioSourceUri));

        if (notifyTrackChanged)
        {
            CurrentTrackChanged?.Invoke(this, track);
        }

        IsPlayingChanged?.Invoke(this, false);

        if (triggerPrefetch)
        {
            _progressiveQueueManager.MaybePrefetch(RepeatMode);
        }

        try
        {
            string? playbackUri = track.AudioSourceUri;

            // Prioritize local / downloaded offline files over online streaming
            if (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath))
            {
                playbackUri = track.LocalOfflineFilePath;
            }
            else if (!string.IsNullOrWhiteSpace(playbackUri) && File.Exists(playbackUri))
            {
                // Already a valid local file path on disk
            }
            else if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) ||
                (!string.IsNullOrWhiteSpace(playbackUri) && playbackUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(track.Id) && track.Id.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase)))
            {
                string videoId = !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
                    ? track.YouTubeVideoId
                    : !string.IsNullOrWhiteSpace(playbackUri) && playbackUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase)
                        ? playbackUri
                        : TrackDeduplicationHelper.StripYtmPrefix(track.Id);

                playbackUri = await _youTubeMusicService.ResolveAudioStreamUriAsync(videoId, linkedToken).ConfigureAwait(false);
            }

            // If disposed or user selected another track while this stream was resolving, abort this superseded request
            if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || linkedToken.IsCancellationRequested)
            {
                return;
            }

            long openSeekTicks = Volatile.Read(ref _pendingLoadSeekTicks);
            TimeSpan? openSeek = openSeekTicks > 0 ? TimeSpan.FromTicks(openSeekTicks) : null;

            bool loadedInAudioGraph = false;
            if (!string.IsNullOrWhiteSpace(playbackUri) && File.Exists(playbackUri))
            {
                var fullPath = Path.GetFullPath(playbackUri);
                _ = Task.Run(() => BuildTrackAcousticEnvelope(fullPath));

                loadedInAudioGraph = await _dspEngine.TryLoadTrackFileAsync(fullPath, linkedToken, openSeek).ConfigureAwait(false);
                if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || linkedToken.IsCancellationRequested)
                {
                    _dspEngine.ResetAndCleanupCurrentTrackNodes();
                    return;
                }

                if (loadedInAudioGraph)
                {
                    ReplaceMediaSource(_mediaPlayer, null);
                    UpdateNaturalDuration(_dspEngine.GraphDuration);
                }
                else
                {
                    ReplaceMediaSource(_mediaPlayer, MediaSource.CreateFromUri(new Uri(fullPath)));
                }
            }
            else if (!string.IsNullOrWhiteSpace(playbackUri) && Uri.TryCreate(playbackUri, UriKind.Absolute, out var uri))
            {
                loadedInAudioGraph = await _dspEngine.TryLoadTrackFileAsync(uri.AbsoluteUri, linkedToken, openSeek).ConfigureAwait(false);
                if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || linkedToken.IsCancellationRequested)
                {
                    _dspEngine.ResetAndCleanupCurrentTrackNodes();
                    return;
                }

                if (loadedInAudioGraph)
                {
                    ReplaceMediaSource(_mediaPlayer, null);
                    UpdateNaturalDuration(_dspEngine.GraphDuration);
                }
                else
                {
                    ReplaceMediaSource(_mediaPlayer, MediaSource.CreateFromUri(uri));
                }
            }
            else
            {
                _logger.LogWarning("Playback URI could not be resolved for track '{Title}'.", track.Title);
                _isLoadingMedia = false;
                _isPlaying = false;
                SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
                IsPlayingChanged?.Invoke(this, false);
                string reason = _youTubeMusicService.LastStreamFailureReason
                    ?? $"Could not resolve playable stream for '{track.Title}'.";
                PlaybackErrorOccurred?.Invoke(this, reason);
                return;
            }

            long finalPendingTicks = Interlocked.Exchange(ref _pendingLoadSeekTicks, -1L);
            if (finalPendingTicks >= 0)
            {
                var pendingSeek = TimeSpan.FromTicks(finalPendingTicks);
                var clampedSeek = Duration > TimeSpan.Zero && pendingSeek > Duration ? Duration : pendingSeek;
                Position = clampedSeek;
                _lastSessionPosition = clampedSeek;
                _lastSessionTick = Stopwatch.GetTimestamp();
                try
                {
                    if (loadedInAudioGraph)
                    {
                        // Re-seek only if user scrubbed to a different position during TryLoadTrackFileAsync
                        // or if initial seek wasn't applied during open.
                        if (openSeek is null ||
                            Math.Abs((clampedSeek - openSeek.Value).TotalMilliseconds) > 50.0 ||
                            Math.Abs((_dspEngine.GraphPosition - clampedSeek).TotalMilliseconds) > 250.0)
                        {
                            _dspEngine.TrySeekGraphPlayback(clampedSeek);
                        }
                    }
                    else
                    {
                        // Defer seek to OnMediaOpened unless MediaPlayer already opened and reports CanSeek
                        Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, clampedSeek.Ticks);
                        if (_mediaPlayer.PlaybackSession.CanSeek)
                        {
                            _mediaPlayer.PlaybackSession.Position = clampedSeek;
                            Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);
                        }
                    }
                }
                catch
                {
                    // Ignore transient initial seek errors; OnMediaOpened will apply _pendingMediaPlayerSeekTicks
                }
            }

            bool shouldPlay = _pendingAutoPlayAfterLoad;
            if (shouldPlay)
            {
                if (loadedInAudioGraph)
                {
                    _dspEngine.StartGraphPlayback();
                }
                else
                {
                    try
                    {
                        _mediaPlayer.PlaybackSession.PlaybackRate = Math.Clamp(_dspEngine.CurrentPlaybackRate, 0.5, 2.0);
                    }
                    catch
                    {
                        // Ignore transient rate assignment
                    }
                    _mediaPlayer.Play();
                }

                _isPlaying = true;
                _lastSessionTick = Stopwatch.GetTimestamp();
                SetTelemetryInterval(25, 28);
            }
            else
            {
                if (loadedInAudioGraph)
                {
                    _dspEngine.PauseGraphPlayback();
                }
                else
                {
                    _mediaPlayer.Pause();
                }

                _isPlaying = false;
                SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (OperationCanceledException) when (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || linkedToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId))
            {
                return;
            }

            _isPlaying = false;
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId))
            {
                return;
            }

            _logger.LogWarning(ex, "Could not open media source '{Uri}'.", track.AudioSourceUri);
            _isPlaying = false;
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
        }
        finally
        {
            if (requestId == Volatile.Read(ref _activePlayRequestId))
            {
                _isLoadingMedia = false;
            }
        }

        if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId))
        {
            return;
        }

        // Apply any late seek that arrived in the narrow window right before _isLoadingMedia cleared
        long lateSeekTicks = Interlocked.Exchange(ref _pendingLoadSeekTicks, -1L);
        if (lateSeekTicks >= 0)
        {
            Seek(TimeSpan.FromTicks(lateSeekTicks));
        }

        IsPlayingChanged?.Invoke(this, _isPlaying);
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
    }

    private void HandleEmptyQueueCleared()
    {
        Interlocked.Increment(ref _activePlayRequestId);
        CancelActiveLoadCts();
        _isLoadingMedia = false;
        _isCurrentTrackDeferred = false;
        _pendingAutoPlayAfterLoad = false;
        Interlocked.Exchange(ref _pendingLoadSeekTicks, -1L);
        Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);

        _dspEngine.ResetAndCleanupCurrentTrackNodes();
        try
        {
            _mediaPlayer.Pause();
            ReplaceMediaSource(_mediaPlayer, null);
        }
        catch
        {
            // Ignore transient pause/reset errors
        }

        SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
        _isPlaying = false;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        _lastSessionPosition = TimeSpan.Zero;
        _lastSessionTick = Stopwatch.GetTimestamp();

        CurrentTrackChanged?.Invoke(this, null);
        IsPlayingChanged?.Invoke(this, false);
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(TimeSpan.Zero, TimeSpan.Zero));
    }

    private void EmitQueueChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        QueueChanged?.Invoke(this, _queueController.GetUpcomingQueue());
    }

    private void SetTelemetryInterval(int dueTimeMs, int periodMs)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            _telemetryTimer.Change(dueTimeMs, periodMs);
        }
        catch (ObjectDisposedException)
        {
            // Timer disposed concurrently during shutdown
        }
    }

    private async Task OnProgressiveAutoAdvanceAsync(TrackItem nextTrack)
    {
        if (IsDisposed)
        {
            return;
        }

        EmitQueueChanged();
        await LoadAndPlayTrackAsync(
            nextTrack,
            autoPlay: true,
            triggerPrefetch: false,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private Task OnQueueEndedExhaustedAsync()
    {
        if (IsDisposed)
        {
            return Task.CompletedTask;
        }

        _isLoadingMedia = false;

        try
        {
            if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
            {
                _dspEngine.PauseGraphPlayback();
            }
            else
            {
                _mediaPlayer.Pause();
            }
        }
        catch
        {
            // Ignore transient pause state
        }

        SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
        _isPlaying = false;
        Position = TimeSpan.Zero;
        _lastSessionPosition = TimeSpan.Zero;
        _lastSessionTick = Stopwatch.GetTimestamp();

        try
        {
            if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
            {
                _dspEngine.SeekGraphPlayback(TimeSpan.Zero);
            }
            else
            {
                _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
            }
        }
        catch
        {
            // Ignore seek failure if playback session is ended/idle
        }

        IsPlayingChanged?.Invoke(this, false);
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task TogglePlayPauseAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        if (!_isPlaying)
        {
            var current = CurrentTrack;
            if (current is null)
            {
                return;
            }

            if (_isCurrentTrackDeferred)
            {
                _isCurrentTrackDeferred = false;
                _progressiveQueueManager.CancelEndAdvance();
                TimeSpan? resumeSeek = Position > TimeSpan.Zero ? Position : null;
                await LoadAndPlayTrackAsync(
                    current,
                    autoPlay: true,
                    triggerPrefetch: true,
                    cancellationToken: CancellationToken.None,
                    initialSeekPosition: resumeSeek,
                    notifyTrackChanged: false).ConfigureAwait(false);
                return;
            }

            if (_isLoadingMedia)
            {
                // Track is already loading (e.g. from a seek or track switch); ensure it plays when ready
                _pendingAutoPlayAfterLoad = true;
                return;
            }

            bool hasSource = _dspEngine.IsUsingAudioGraphForCurrentTrack;
            if (!hasSource)
            {
                try
                {
                    hasSource = _mediaPlayer.Source is not null;
                }
                catch
                {
                    // Ignore transient source read error
                }
            }

            if (!hasSource)
            {
                _progressiveQueueManager.CancelEndAdvance();
                TimeSpan? resumeSeek = Position > TimeSpan.Zero ? Position : null;
                await LoadAndPlayTrackAsync(
                    current,
                    autoPlay: true,
                    triggerPrefetch: true,
                    cancellationToken: CancellationToken.None,
                    initialSeekPosition: resumeSeek,
                    notifyTrackChanged: false).ConfigureAwait(false);
                return;
            }
        }
        else if (_isLoadingMedia)
        {
            _pendingAutoPlayAfterLoad = false;
            _isPlaying = false;
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            IsPlayingChanged?.Invoke(this, false);
            return;
        }

        try
        {
            if (_isPlaying)
            {
                _progressiveQueueManager.CancelEndAdvance();
                if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
                {
                    _dspEngine.PauseGraphPlayback();
                }
                else
                {
                    _mediaPlayer.Pause();
                }

                _isPlaying = false;
                SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _progressiveQueueManager.CancelEndAdvance();
                if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
                {
                    _dspEngine.StartGraphPlayback();
                }
                else
                {
                    try
                    {
                        _mediaPlayer.PlaybackSession.PlaybackRate = Math.Clamp(_dspEngine.CurrentPlaybackRate, 0.5, 2.0);
                    }
                    catch
                    {
                        // Ignore transient playback rate error
                    }
                    _mediaPlayer.Play();
                }

                _isPlaying = true;
                _lastSessionPosition = Position;
                _lastSessionTick = Stopwatch.GetTimestamp();
                SetTelemetryInterval(25, 28);
                _progressiveQueueManager.MaybePrefetch(RepeatMode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playback toggle fallback.");
            _isPlaying = !_isPlaying;
            if (_isPlaying)
            {
                _progressiveQueueManager.MaybePrefetch(RepeatMode);
            }
            else
            {
                _progressiveQueueManager.CancelEndAdvance();
            }
        }

        IsPlayingChanged?.Invoke(this, _isPlaying);
    }

    /// <inheritdoc />
    public async Task NextAsync()
    {
        if (IsDisposed || _queueController.TotalCount == 0)
        {
            return;
        }

        var repeatMode = RepeatMode;
        if (_queueController.HasNext(repeatMode))
        {
            _progressiveQueueManager.CancelEndAdvance();
        }

        var nextTrack = _queueController.SkipNext(repeatMode);
        if (nextTrack is not null)
        {
            _progressiveQueueManager.CancelEndAdvance();
            EmitQueueChanged();
            await LoadAndPlayTrackAsync(
                nextTrack,
                autoPlay: true,
                triggerPrefetch: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (repeatMode == PlaybackRepeatMode.None)
        {
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            await _progressiveQueueManager.ResolveAndAdvanceFromEndAsync(repeatMode).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task PreviousAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        _progressiveQueueManager.CancelEndAdvance();

        if (Position.TotalSeconds > 4.0 || !_queueController.HasPrevious)
        {
            if (CurrentTrack is not null)
            {
                Seek(TimeSpan.Zero);
            }

            return;
        }

        var prevTrack = _queueController.SkipPrevious();
        if (prevTrack is null)
        {
            if (CurrentTrack is not null)
            {
                Seek(TimeSpan.Zero);
            }

            return;
        }

        EmitQueueChanged();
        await LoadAndPlayTrackAsync(
            prevTrack,
            autoPlay: true,
            triggerPrefetch: true,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        if (IsDisposed)
        {
            return;
        }

        var effectiveDuration = Duration > TimeSpan.Zero
            ? Duration
            : (CurrentTrack?.Duration ?? TimeSpan.Zero);
        double clampedSeconds = effectiveDuration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds, 0.0, effectiveDuration.TotalSeconds)
            : Math.Max(0.0, position.TotalSeconds);
        var clamped = TimeSpan.FromSeconds(clampedSeconds);

        Position = clamped;
        _lastSessionPosition = clamped;
        _lastSessionTick = Stopwatch.GetTimestamp();

        if (_isCurrentTrackDeferred)
        {
            var current = CurrentTrack;
            if (current is not null)
            {
                _isCurrentTrackDeferred = false;
                _progressiveQueueManager.CancelEndAdvance();
                PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
                _ = LoadAndPlayTrackAsync(
                    current,
                    autoPlay: true,
                    triggerPrefetch: true,
                    cancellationToken: CancellationToken.None,
                    initialSeekPosition: clamped,
                    notifyTrackChanged: false);
                return;
            }
        }

        if (_isLoadingMedia)
        {
            // Remember the latest scrubbed timestamp so LoadAndPlayTrackAsync applies it as soon as media opens
            Interlocked.Exchange(ref _pendingLoadSeekTicks, clamped.Ticks);
            PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
            return;
        }

        try
        {
            if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
            {
                bool seekSucceeded = _dspEngine.TrySeekGraphPlayback(clamped);
                if (!seekSucceeded)
                {
                    var actualGraphPos = _dspEngine.GraphPosition;
                    Position = actualGraphPos;
                    _lastSessionPosition = actualGraphPos;
                    _lastSessionTick = Stopwatch.GetTimestamp();
                }

                // Guarantee playback remains running after seek if we were playing
                if (_isPlaying && !_dspEngine.IsGraphPlaying)
                {
                    _dspEngine.StartGraphPlayback();
                    SetTelemetryInterval(25, 28);
                }
            }
            else if (_mediaPlayer.Source is not null)
            {
                var session = _mediaPlayer.PlaybackSession;
                if (session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.None || !session.CanSeek)
                {
                    Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, clamped.Ticks);
                }
                else
                {
                    Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);
                    session.Position = clamped;
                    if (_isPlaying && session.PlaybackState == MediaPlaybackState.Paused)
                    {
                        _mediaPlayer.Play();
                    }
                }
            }
        }
        catch
        {
            // If MediaPlayer session isn't seekable yet, retain pending seek for OnMediaOpened
            if (!_dspEngine.IsUsingAudioGraphForCurrentTrack)
            {
                Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, clamped.Ticks);
            }
        }

        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
    }

    /// <inheritdoc />
    public void SeekRelative(TimeSpan delta)
    {
        Seek(Position + delta);
    }

    /// <inheritdoc />
    public PlaybackRepeatMode CycleRepeatMode()
    {
        var next = RepeatMode switch
        {
            PlaybackRepeatMode.None => PlaybackRepeatMode.RepeatAll,
            PlaybackRepeatMode.RepeatAll => PlaybackRepeatMode.RepeatOne,
            _ => PlaybackRepeatMode.None
        };

        RepeatMode = next;
        return next;
    }

    /// <inheritdoc />
    public bool ToggleShuffle()
    {
        if (IsDisposed)
        {
            return _isShuffleEnabled;
        }

        bool nextState = !_isShuffleEnabled;
        _isShuffleEnabled = nextState;

        if (nextState)
        {
            if (_queueController.ShuffleUpcoming())
            {
                EmitQueueChanged();
            }

            _progressiveQueueManager.MaybePrefetch(RepeatMode);
        }

        return nextState;
    }

    /// <inheritdoc />
    public void EnqueueTrack(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (IsDisposed)
        {
            return;
        }

        bool wasEmpty = _queueController.CurrentTrack is null;
        _queueController.EnqueueTrack(track);
        if (wasEmpty)
        {
            NotifyPromotedEmptyQueueTrack(track);
            return;
        }

        EmitQueueChanged();
        _progressiveQueueManager.MaybePrefetch(RepeatMode);
    }

    /// <inheritdoc />
    public void EnqueuePlayNext(TrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (IsDisposed)
        {
            return;
        }

        bool wasEmpty = _queueController.CurrentTrack is null;
        _queueController.EnqueuePlayNext(track);
        if (wasEmpty)
        {
            NotifyPromotedEmptyQueueTrack(track);
            return;
        }

        EmitQueueChanged();
        _progressiveQueueManager.MaybePrefetch(RepeatMode);
    }

    private void NotifyPromotedEmptyQueueTrack(TrackItem track)
    {
        Duration = track.Duration;
        Position = TimeSpan.Zero;
        _lastSessionPosition = TimeSpan.Zero;
        _lastSessionTick = Stopwatch.GetTimestamp();

        EmitQueueChanged();
        CurrentTrackChanged?.Invoke(this, track);
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
    }

    /// <inheritdoc />
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!_queueController.MoveUpcomingItem(fromIndex, toIndex))
        {
            return;
        }

        EmitQueueChanged();
        _progressiveQueueManager.MaybePrefetch(RepeatMode);
    }

    /// <inheritdoc />
    public async Task PlayQueueIndexAsync(int index)
    {
        if (IsDisposed)
        {
            return;
        }

        _progressiveQueueManager.CancelEndAdvance();
        var targetTrack = _queueController.PlayUpcomingIndex(index);
        if (targetTrack is null)
        {
            return;
        }

        EmitQueueChanged();
        await LoadAndPlayTrackAsync(
            targetTrack,
            autoPlay: true,
            triggerPrefetch: true,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SampleSpectrum(
        Span<float> spectrumBands,
        out float bassEnergy,
        out float midEnergy,
        out float trebleEnergy)
    {
        int count = Math.Min(spectrumBands.Length, _smoothedBands.Length);
        if (count <= 0)
        {
            bassEnergy = 0f;
            midEnergy = 0f;
            trebleEnergy = 0f;
            return;
        }

        double posSec = Position.TotalSeconds;
        double durSec = Math.Max(1.0, Duration.TotalSeconds);
        float masterGain = IsMuted ? 0f : (float)Math.Clamp(Volume, 0.15, 1.0);

        float envBass = 0.62f;
        float envMid = 0.56f;
        float envTreble = 0.48f;

        var bassArr = _trackBassEnvelope;
        var midArr = _trackMidEnvelope;
        var trebArr = _trackTrebleEnvelope;

        if (bassArr.Length > 8 && durSec > 0.5)
        {
            double norm = Math.Clamp(posSec / durSec, 0.0, 0.9999);
            double exactIdx = norm * (bassArr.Length - 1);
            int idx0 = (int)exactIdx;
            int idx1 = Math.Min(idx0 + 1, bassArr.Length - 1);
            float frac = (float)(exactIdx - idx0);

            envBass = bassArr[idx0] + ((bassArr[idx1] - bassArr[idx0]) * frac);
            envMid = midArr[idx0] + ((midArr[idx1] - midArr[idx0]) * frac);
            envTreble = trebArr[idx0] + ((trebArr[idx1] - trebArr[idx0]) * frac);
        }

        _dspEngine.GetVisualizerSpectralGainMultipliers(
            out float dspBassGain,
            out float dspMidGain,
            out float dspTrebleGain);

        float rateScale = (float)Math.Clamp(_dspEngine.CurrentPlaybackRate, 0.5, 2.0);
        float tempoHz = (1.95f + ((Math.Abs(_trackSeed) % 35) * 0.025f)) * rateScale;
        float t = (float)posSec;

        // Beat & sub-harmonic pulse locked to playback timestamp
        float kickWave = MathF.Pow(Math.Max(0f, MathF.Sin(t * tempoHz * MathF.PI)), 3.0f);
        float snareWave = MathF.Pow(Math.Max(0f, MathF.Sin((t * tempoHz * 0.5f * MathF.PI) + 1.57f)), 4.0f);

        float bSum = 0f;
        float mSum = 0f;
        float tSum = 0f;

        for (int i = 0; i < count; i++)
        {
            float freqNorm = (float)i / Math.Max(1, count - 1);
            float target;

            if (IsPlaying && masterGain > 0.01f)
            {
                float phase1 = (t * (3.4f + (i * 0.37f))) + (i * 0.41f) + (_trackSeed & 0xFF);
                float phase2 = (t * (6.1f + (i * 0.19f))) - (i * 0.73f);
                float phase3 = (t * (11.3f - (freqNorm * 4.2f))) + (i * 1.15f);

                float harmonic = (MathF.Sin(phase1) * 0.42f)
                               + (MathF.Cos(phase2) * 0.33f)
                               + (MathF.Sin(phase3) * 0.25f);
                harmonic = Math.Clamp((harmonic + 1.0f) * 0.5f, 0.04f, 1.0f);

                if (freqNorm < 0.25f)
                {
                    // Bass region (20 - 250 Hz) + Nightcore 90Hz Low-Shelf / EQ Bass response
                    float bassShape = 1.0f - (freqNorm * 1.6f);
                    target = ((envBass * 0.58f) + (kickWave * 0.42f)) * (0.45f + (0.55f * harmonic)) * bassShape * dspBassGain;
                }
                else if (freqNorm < 0.68f)
                {
                    // Mid / Vocal harmonic region (250 - 2500 Hz)
                    float midCenter = 1.0f - (MathF.Abs(freqNorm - 0.46f) * 1.6f);
                    target = ((envMid * 0.62f) + (snareWave * 0.28f)) * harmonic * Math.Clamp(midCenter, 0.45f, 1.0f) * dspMidGain;
                }
                else
                {
                    // Treble / Hi-hat region (2.5 - 16 kHz) + Nightcore 3.5kHz Peaking / EQ Treble response
                    float shimmer = MathF.Abs(MathF.Sin((t * 17.5f) + (i * 1.9f)));
                    target = envTreble * ((harmonic * 0.55f) + (shimmer * 0.45f)) * 0.88f * dspTrebleGain;
                }

                target = Math.Clamp(target * masterGain * 1.22f, 0.03f, 1.0f);
            }
            else
            {
                // Gentle idle breathing wave when paused
                float idleTime = Environment.TickCount64 * 0.0018f;
                target = 0.035f + (0.025f * MathF.Sin(idleTime + (i * 0.22f)));
            }

            // Fast attack, smooth gravity decay
            float current = _smoothedBands[i];
            float lerpFactor = target > current ? 0.52f : 0.16f;
            current += (target - current) * lerpFactor;
            _smoothedBands[i] = current;
            spectrumBands[i] = current;

            if (freqNorm < 0.25f)
            {
                bSum += current;
            }
            else if (freqNorm < 0.68f)
            {
                mSum += current;
            }
            else
            {
                tSum += current;
            }
        }

        int bassCount = Math.Max(1, (int)(count * 0.25f));
        int midCount = Math.Max(1, (int)(count * 0.43f));
        int trebCount = Math.Max(1, count - bassCount - midCount);

        bassEnergy = Math.Clamp(bSum / bassCount, 0f, 1f);
        midEnergy = Math.Clamp(mSum / midCount, 0f, 1f);
        trebleEnergy = Math.Clamp(tSum / trebCount, 0f, 1f);
    }

    /// <summary>
    /// Extracts a 1024-slice acoustic energy signature across the audio file on a background thread
    /// so the GPU visualizer reacts to the real dynamic loudness, transients, and spectral density of the song.
    /// </summary>
    private void BuildTrackAcousticEnvelope(string? audioFilePath)
    {
        const int slices = 1024;
        var bass = new float[slices];
        var mid = new float[slices];
        var treble = new float[slices];

        try
        {
            if (!string.IsNullOrWhiteSpace(audioFilePath) && File.Exists(audioFilePath))
            {
                using var fs = new FileStream(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long length = fs.Length;
                // Skip ID3v2 / FLAC header block at start of file
                long dataStart = Math.Min(length / 10, 65536);
                long dataLength = Math.Max(1024, length - dataStart);
                byte[] sampleBuf = new byte[256];

                for (int s = 0; s < slices; s++)
                {
                    long offset = dataStart + ((dataLength - sampleBuf.Length) * s / slices);
                    fs.Seek(offset, SeekOrigin.Begin);
                    int read = fs.Read(sampleBuf, 0, sampleBuf.Length);
                    if (read <= 4)
                    {
                        bass[s] = 0.55f;
                        mid[s] = 0.50f;
                        treble[s] = 0.42f;
                        continue;
                    }

                    float lowAcc = 0f;
                    float midAcc = 0f;
                    float highAcc = 0f;
                    int prev = sampleBuf[0] - 128;

                    for (int k = 1; k < read; k++)
                    {
                        int curr = sampleBuf[k] - 128;
                        int diff = Math.Abs(curr - prev);
                        lowAcc += Math.Abs(curr) / 128f;
                        midAcc += diff / 192f;
                        if (k > 1)
                        {
                            int prev2 = sampleBuf[k - 2] - 128;
                            highAcc += Math.Abs(curr - (2 * prev) + prev2) / 256f;
                        }

                        prev = curr;
                    }

                    bass[s] = Math.Clamp(0.28f + ((lowAcc / read) * 1.15f), 0.18f, 1.0f);
                    mid[s] = Math.Clamp(0.24f + ((midAcc / read) * 1.25f), 0.15f, 1.0f);
                    treble[s] = Math.Clamp(0.20f + ((highAcc / read) * 1.20f), 0.12f, 1.0f);
                }

                _trackBassEnvelope = bass;
                _trackMidEnvelope = mid;
                _trackTrebleEnvelope = treble;
                return;
            }
        }
        catch
        {
            // Fallback to default envelope if stream cannot be sampled
        }

        Array.Fill(bass, 0.65f);
        Array.Fill(mid, 0.58f);
        Array.Fill(treble, 0.48f);
        _trackBassEnvelope = bass;
        _trackMidEnvelope = mid;
        _trackTrebleEnvelope = treble;
    }

    private void OnTelemetryTick(object? state)
    {
        if (IsDisposed || !_isPlaying)
        {
            return;
        }

        try
        {
            long nowTick = Stopwatch.GetTimestamp();
            double effectiveRate = Math.Clamp(_dspEngine.CurrentPlaybackRate, 0.25, 4.0);

            if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
            {
                if (_dspEngine.IsSeeking)
                {
                    return;
                }

                var graphPos = _dspEngine.GraphPosition;
                var graphDur = _dspEngine.GraphDuration;

                if (Math.Abs((graphPos - _lastSessionPosition).TotalMilliseconds) > 10.0)
                {
                    _lastSessionPosition = graphPos;
                    _lastSessionTick = nowTick;
                    Position = graphPos;
                }
                else
                {
                    double elapsedSec = Stopwatch.GetElapsedTime(_lastSessionTick, nowTick).TotalSeconds * effectiveRate;
                    var interpolated = _lastSessionPosition + TimeSpan.FromSeconds(Math.Min(elapsedSec, 1.85));
                    if (Duration > TimeSpan.Zero && interpolated > Duration)
                    {
                        interpolated = Duration;
                    }

                    Position = interpolated;
                }

                UpdateNaturalDuration(graphDur);
                PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
                return;
            }

            if (Volatile.Read(ref _pendingMediaPlayerSeekTicks) >= 0)
            {
                return;
            }

            var session = _mediaPlayer.PlaybackSession;
            var rawSessionPos = session.Position;

            if (Math.Abs((rawSessionPos - _lastSessionPosition).TotalMilliseconds) > 15.0)
            {
                _lastSessionPosition = rawSessionPos;
                _lastSessionTick = nowTick;
                Position = rawSessionPos;
            }
            else if (session.PlaybackState == MediaPlaybackState.Playing)
            {
                double elapsedSec = Stopwatch.GetElapsedTime(_lastSessionTick, nowTick).TotalSeconds * effectiveRate;
                var interpolated = _lastSessionPosition + TimeSpan.FromSeconds(Math.Min(elapsedSec, 1.85));
                if (Duration > TimeSpan.Zero && interpolated > Duration)
                {
                    interpolated = Duration;
                }

                Position = interpolated;
            }

            UpdateNaturalDuration(session.NaturalDuration);

            PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
        }
        catch
        {
            // Ignore transient session access errors
        }
    }

    private void OnDspEffectiveRateChanged(object? sender, double newRate)
    {
        if (IsDisposed)
        {
            return;
        }

        _lastSessionPosition = Position;
        _lastSessionTick = Stopwatch.GetTimestamp();
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
    }

    private void OnDspGraphTrackCompleted(object? sender, EventArgs e)
    {
        OnMediaEnded(_mediaPlayer, EventArgs.Empty);
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (IsDisposed || _dspEngine.IsUsingAudioGraphForCurrentTrack)
        {
            return;
        }

        try
        {
            var session = sender.PlaybackSession;
            UpdateNaturalDuration(session.NaturalDuration);

            long pendingTicks = Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, -1L);
            if (pendingTicks >= 0)
            {
                var target = TimeSpan.FromTicks(pendingTicks);
                if (Duration > TimeSpan.Zero && target > Duration)
                {
                    target = Duration;
                }

                if (session.CanSeek)
                {
                    session.Position = target;
                    Position = target;
                    _lastSessionPosition = target;
                    _lastSessionTick = Stopwatch.GetTimestamp();
                    PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
                }
            }

            session.PlaybackRate = Math.Clamp(_dspEngine.CurrentPlaybackRate, 0.5, 2.0);
            if (_isPlaying && session.PlaybackState != MediaPlaybackState.Playing)
            {
                sender.Play();
            }
        }
        catch
        {
            // Ignore transient session access errors on MediaOpened
        }
    }

    private void OnPlaybackSessionPositionChanged(MediaPlaybackSession sender, object args)
    {
        if (IsDisposed || _dspEngine.IsUsingAudioGraphForCurrentTrack || Volatile.Read(ref _pendingMediaPlayerSeekTicks) >= 0)
        {
            return;
        }

        var pos = sender.Position;
        _lastSessionPosition = pos;
        _lastSessionTick = Stopwatch.GetTimestamp();
        Position = pos;
        UpdateNaturalDuration(sender.NaturalDuration);

        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
    }

    private void UpdateNaturalDuration(TimeSpan naturalDuration)
    {
        if (naturalDuration <= TimeSpan.Zero)
        {
            return;
        }

        Duration = naturalDuration;
        var current = _queueController.CurrentTrack;
        if (!_isLoadingMedia &&
            current is not null &&
            Math.Abs((current.Duration - naturalDuration).TotalSeconds) > 1.0)
        {
            var updated = new TrackItem
            {
                Id = current.Id,
                Title = current.Title,
                Artist = current.Artist,
                Album = current.Album,
                Duration = naturalDuration,
                CoverArtUri = current.CoverArtUri,
                AudioSourceUri = current.AudioSourceUri,
                YouTubeVideoId = current.YouTubeVideoId,
                SourceType = current.SourceType,
                IsLiked = current.IsLiked,
                IsCurrentlyPlaying = current.IsCurrentlyPlaying,
                IsExplicit = current.IsExplicit,
                DateAddedMs = current.DateAddedMs,
                QualityBadge = current.QualityBadge,
                Genre = current.Genre,
                ReleaseYear = current.ReleaseYear,
                TrackNumber = current.TrackNumber,
                PlayCountText = current.PlayCountText,
                PlayCount = current.PlayCount,
                LyricsProviderName = current.LyricsProviderName,
                Lyrics = current.Lyrics,
                IsDownloadedOffline = current.IsDownloadedOffline,
                LocalOfflineFilePath = current.LocalOfflineFilePath
            };

            if (!_isLoadingMedia && ReferenceEquals(_queueController.CurrentTrack, current))
            {
                _queueController.ReplaceCurrentTrack(updated);
            }
        }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (IsDisposed || _dspEngine.IsUsingAudioGraphForCurrentTrack)
        {
            return;
        }

        var state = sender.PlaybackState;
        // Never flip _isPlaying to false during transient Opening/Buffering transitions (such as seeking!)
        if (state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering)
        {
            return;
        }

        if (_isLoadingMedia && state != MediaPlaybackState.Playing)
        {
            return;
        }

        bool playing = state == MediaPlaybackState.Playing;
        if (_isPlaying != playing)
        {
            _isPlaying = playing;
            if (playing)
            {
                _lastSessionTick = Stopwatch.GetTimestamp();
                SetTelemetryInterval(25, 28);
                _progressiveQueueManager.MaybePrefetch(RepeatMode);
            }
            else
            {
                SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            }

            IsPlayingChanged?.Invoke(this, _isPlaying);
        }
    }

    private async void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (IsDisposed || _isLoadingMedia)
        {
            return;
        }

        try
        {
            if (RepeatMode == PlaybackRepeatMode.RepeatOne && CurrentTrack is not null)
            {
                Seek(TimeSpan.Zero);
                if (_dspEngine.IsUsingAudioGraphForCurrentTrack)
                {
                    _dspEngine.StartGraphPlayback();
                }
                else
                {
                    _mediaPlayer.Play();
                }

                _isPlaying = true;
                _lastSessionPosition = TimeSpan.Zero;
                _lastSessionTick = Stopwatch.GetTimestamp();
                SetTelemetryInterval(25, 28);
                IsPlayingChanged?.Invoke(this, true);
                return;
            }

            await NextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unhandled error while handling media end transition.");
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger.LogError(
            "Windows MediaPlayer failed playback: Error={Error}, ExtendedErrorCode={ExtendedErrorCode:X8}, Message={Message}",
            args.Error,
            args.ExtendedErrorCode?.HResult ?? 0,
            args.ErrorMessage);
    }

    private static void ReplaceMediaSource(MediaPlayer? player, IMediaPlaybackSource? nextSource)
    {
        if (player is null)
        {
            (nextSource as IDisposable)?.Dispose();
            return;
        }

        IMediaPlaybackSource? previous = null;
        try
        {
            previous = player.Source;
        }
        catch
        {
            // Ignore transient COM source read failure
        }

        try
        {
            player.Source = nextSource;
        }
        catch
        {
            // Ignore transient COM source assignment failure
        }

        if (previous is not null && !ReferenceEquals(previous, nextSource))
        {
            try
            {
                if (previous is MediaPlaybackItem playbackItem)
                {
                    playbackItem.Source?.Dispose();
                }
                else if (previous is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Ignore transient COM disposal race on previous MediaSource
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _activePlayRequestId);
        CancelActiveLoadCts();
        _dspEngine.TrackPlaybackCompleted -= OnDspGraphTrackCompleted;
        _dspEngine.EffectiveRateChanged -= OnDspEffectiveRateChanged;
        _dspEngine.Dispose();
        _progressiveQueueManager.Dispose();
        _telemetryTimer.Dispose();
        _mediaPlayer.PlaybackSession.PositionChanged -= OnPlaybackSessionPositionChanged;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mediaPlayer.MediaOpened -= OnMediaOpened;
        _mediaPlayer.MediaEnded -= OnMediaEnded;
        _mediaPlayer.MediaFailed -= OnMediaFailed;
        ReplaceMediaSource(_mediaPlayer, null);
        _mediaPlayer.Dispose();
    }
}
