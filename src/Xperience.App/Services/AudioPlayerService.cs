using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xperience.App.Services.Audio;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
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
    private readonly IMusicDownloadService _musicDownloadService;
    private readonly IDataUsageService _dataUsageService;
    private readonly NightcoreDspEngine _dspEngine;
    private readonly ILogger<AudioPlayerService> _logger;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Timer _telemetryTimer;
    private readonly PlaybackQueueController _queueController = new();
    private readonly ProgressiveQueueManager _progressiveQueueManager;
    private readonly object _loadCtsLock = new();
    private CancellationTokenSource? _activeLoadCts;
    private readonly object _preloadLock = new();
    private CancellationTokenSource? _preloadCts;
    private (string TrackKey, StreamingAudioQuality Quality, ResolvedPlaybackStream Stream)? _preloadedNext;
    private string? _preloadInFlightKey;
    private PlaybackBehaviorSettings _behaviorSettings = new();
    private TrackPlaybackInfo? _currentTrackPlaybackInfo;
    private readonly ZeroVolumePauseCoordinator _zeroVolumeCoordinator = new();
    private readonly WindowsSystemVolumeMonitor _systemVolumeMonitor;
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
    private volatile bool _isVideoAudioModeActive;
    private volatile bool _pendingAutoPlayAfterLoad;
    private string? _activeAudioPlaybackUri;
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
    public event EventHandler<TrackPlaybackInfo?>? CurrentTrackPlaybackInfoChanged;

    /// <inheritdoc />
    public TrackPlaybackInfo? CurrentTrackPlaybackInfo => _currentTrackPlaybackInfo;

    /// <inheritdoc />
    public PlaybackBehaviorSettings BehaviorSettings
    {
        get => _behaviorSettings;
        set
        {
            var next = value ?? new PlaybackBehaviorSettings();
            bool qualityChanged = _behaviorSettings.OnlineQuality != next.OnlineQuality;
            bool preloadDisabled = !next.GaplessPreloadEnabled;
            _behaviorSettings = next;
            _zeroVolumeCoordinator.IsEnabled = next.PauseAtZeroVolumeEnabled;
            if (next.PauseAtZeroVolumeEnabled && _isPlaying)
            {
                var action = _zeroVolumeCoordinator.OnSystemVolumeChanged(
                    _systemVolumeMonitor.CurrentMasterVolumeScalar,
                    _systemVolumeMonitor.IsSystemMuted,
                    isCurrentlyPlaying: _isPlaying);
                if (action == ZeroVolumePauseAction.Pause)
                {
                    PausePlaybackCore(clearZeroVolumeFlag: false);
                }
            }

            if (qualityChanged || preloadDisabled)
            {
                CancelAndClearNextPreload();
            }
            else if (next.GaplessPreloadEnabled && _isPlaying)
            {
                MaybeTriggerGaplessPreload();
            }
        }
    }

    /// <inheritdoc />
    public TrackItem? CurrentTrack => _queueController.CurrentTrack;

    /// <inheritdoc />
    public bool IsPlaying => _isPlaying;

    /// <inheritdoc />
    public bool IsVideoAudioModeActive => _isVideoAudioModeActive;

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

            if (!IsDisposed)
            {
                var action = _zeroVolumeCoordinator.OnAppVolumeChanged(clamped, _isPlaying);
                ApplyZeroVolumeAction(action);
            }
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
        IMusicDownloadService musicDownloadService,
        IDataUsageService dataUsageService,
        NightcoreDspEngine dspEngine,
        ILogger<AudioPlayerService> logger)
    {
        _youTubeMusicService = youTubeMusicService;
        _musicDownloadService = musicDownloadService;
        _dataUsageService = dataUsageService;
        _dspEngine = dspEngine;
        _logger = logger;
        _dataUsageService.DataLimitReached += OnDataLimitReached;
        _systemVolumeMonitor = new WindowsSystemVolumeMonitor(logger);
        _zeroVolumeCoordinator.OnAppVolumeChanged(_volume, isCurrentlyPlaying: false);
        _zeroVolumeCoordinator.OnSystemVolumeChanged(
            _systemVolumeMonitor.CurrentMasterVolumeScalar,
            _systemVolumeMonitor.IsSystemMuted,
            isCurrentlyPlaying: false);
        _systemVolumeMonitor.SystemVolumeChanged += OnSystemMasterVolumeChanged;
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
        if (_isShuffleEnabled && activeTrack is not null)
        {
            _queueController.ShuffleUpcoming();
        }

        await PrepareActivatedQueueTrackAsync(activeTrack, autoPlay, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetShuffledQueueAsync(
        IReadOnlyList<TrackItem> canonicalTracks,
        int? startCanonicalIndex = null,
        bool autoPlay = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalTracks);

        if (IsDisposed)
        {
            return;
        }

        _isShuffleEnabled = true;
        var activeTrack = _queueController.SetQueueShuffled(canonicalTracks, startCanonicalIndex);
        await PrepareActivatedQueueTrackAsync(activeTrack, autoPlay, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public PersistedPlaybackSessionState? CapturePlaybackSessionSnapshot()
    {
        if (IsDisposed)
        {
            return null;
        }

        var (items, currentIdx, canonical) = _queueController.GetSessionSnapshot();
        if (items.Count == 0 || currentIdx < 0 || currentIdx >= items.Count)
        {
            return null;
        }

        var current = items[currentIdx];
        if (string.Equals(current.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(current.Title))
        {
            return null;
        }

        const int maxTotal = 200;
        const int maxHistoryBefore = 40;
        int windowStart = Math.Max(0, currentIdx - maxHistoryBefore);
        int windowCount = Math.Min(maxTotal, items.Count - windowStart);

        var savedItems = new List<TrackItem>(windowCount);
        for (int i = windowStart; i < windowStart + windowCount; i++)
        {
            var t = items[i];
            if (t is not null && !string.Equals(t.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
            {
                savedItems.Add(t);
            }
        }

        if (savedItems.Count == 0)
        {
            return null;
        }

        int savedCurrentIdx = Math.Clamp(currentIdx - windowStart, 0, savedItems.Count - 1);
        var savedCanonical = canonical.Count <= 250
            ? canonical.Where(t => t is not null && !string.Equals(t.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase)).ToList()
            : savedItems.ToList();

        TimeSpan livePos = _dspEngine.IsUsingAudioGraphForCurrentTrack && _isPlaying
            ? _dspEngine.GraphPosition
            : Position;
        TimeSpan dur = Duration > TimeSpan.Zero ? Duration : current.Duration;
        double durSec = Math.Max(0.0, dur.TotalSeconds);
        double posSec = durSec > 0
            ? Math.Clamp(livePos.TotalSeconds, 0.0, durSec)
            : Math.Max(0.0, livePos.TotalSeconds);

        return new PersistedPlaybackSessionState
        {
            QueueItems = savedItems,
            CanonicalOrder = savedCanonical,
            CurrentIndex = savedCurrentIdx,
            PositionSeconds = posSec,
            DurationSeconds = durSec,
            IsShuffleEnabled = _isShuffleEnabled,
            RepeatMode = RepeatMode,
            Volume = _volume,
            IsMuted = _isMuted,
            SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    /// <inheritdoc />
    public bool RestorePlaybackSession(PersistedPlaybackSessionState session)
    {
        if (IsDisposed || session is null || session.QueueItems is null || session.QueueItems.Count == 0)
        {
            return false;
        }

        CancelAndClearNextPreload();
        var activeTrack = _queueController.RestoreSession(session.QueueItems, session.CurrentIndex, session.CanonicalOrder);
        if (activeTrack is null)
        {
            return false;
        }

        _isShuffleEnabled = session.IsShuffleEnabled;
        Volatile.Write(ref _repeatMode, (int)session.RepeatMode);
        double clampedVol = Math.Clamp(session.Volume, 0.0, 1.0);
        _volume = clampedVol;
        _isMuted = session.IsMuted;
        _mediaPlayer.Volume = clampedVol;
        _mediaPlayer.IsMuted = session.IsMuted;
        _dspEngine.SetMasterVolume(clampedVol, session.IsMuted);

        _progressiveQueueManager.OnQueueReplaced();
        EmitQueueChanged();
        EnterDeferredIdleState(
            activeTrack,
            TimeSpan.FromSeconds(Math.Max(0.0, session.PositionSeconds)),
            TimeSpan.FromSeconds(Math.Max(0.0, session.DurationSeconds)));

        return true;
    }

    private async Task PrepareActivatedQueueTrackAsync(
        TrackItem? activeTrack,
        bool autoPlay,
        CancellationToken cancellationToken)
    {
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
            EnterDeferredIdleState(activeTrack, TimeSpan.Zero, activeTrack.Duration);
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

    private void EnterDeferredIdleState(TrackItem activeTrack, TimeSpan initialPosition, TimeSpan initialDuration)
    {
        // Defer decoder, AudioGraph, and YouTube WebView2 spin-up until user initiates playback or seeks
        Interlocked.Increment(ref _activePlayRequestId);
        CancelActiveLoadCts();
        _isLoadingMedia = false;
        _isPlaying = false;
        _isCurrentTrackDeferred = true;
        _isVideoAudioModeActive = activeTrack.IsOfficialVideoItem;
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

        TimeSpan effectiveDur = initialDuration > TimeSpan.Zero ? initialDuration : activeTrack.Duration;
        double clampedSec = effectiveDur > TimeSpan.Zero
            ? Math.Clamp(initialPosition.TotalSeconds, 0.0, effectiveDur.TotalSeconds)
            : Math.Max(0.0, initialPosition.TotalSeconds);

        Duration = effectiveDur;
        Position = TimeSpan.FromSeconds(clampedSec);
        _lastSessionPosition = Position;
        _lastSessionTick = Stopwatch.GetTimestamp();
        _trackSeed = StringComparer.OrdinalIgnoreCase.GetHashCode(activeTrack.Id ?? activeTrack.Title);

        CurrentTrackChanged?.Invoke(this, activeTrack);
        IsPlayingChanged?.Invoke(this, false);
        PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
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
        _isVideoAudioModeActive = track.IsOfficialVideoItem;
        _activeAudioPlaybackUri = null;
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
            ResolvedPlaybackStream? resolvedStream = null;
            StreamingAudioQuality requestedQuality = _behaviorSettings.OnlineQuality;

            // Prioritize local / downloaded offline files over online streaming
            if (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath))
            {
                playbackUri = track.LocalOfflineFilePath;
                resolvedStream = InspectLocalAudioFile(playbackUri, PlaybackSourceKind.DownloadedOffline, "Downloaded Offline", requestedQuality);
            }
            else if (!string.IsNullOrWhiteSpace(playbackUri) && File.Exists(playbackUri))
            {
                // Already a valid local file path on disk
                resolvedStream = InspectLocalAudioFile(playbackUri, PlaybackSourceKind.Local, "Local File", requestedQuality);
            }
            else if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) ||
                (!string.IsNullOrWhiteSpace(playbackUri) && playbackUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(track.Id) && track.Id.StartsWith("ytm_", StringComparison.OrdinalIgnoreCase)))
            {
                // Enforce Data Usage Limit for online streams (local/downloaded offline files above are never blocked)
                if (_dataUsageService.IsOnlineStreamingBlocked())
                {
                    _isLoadingMedia = false;
                    _isPlaying = false;
                    SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
                    IsPlayingChanged?.Invoke(this, false);
                    string limitMsg = $"Data usage limit ({TrackPlaybackInfo.FormatByteSize(_dataUsageService.Settings.WifiLimitBytes)}) reached. Online playback paused.";
                    PlaybackErrorOccurred?.Invoke(this, limitMsg);
                    return;
                }

                // Check gapless preloaded next-track cache first for instant zero-gap handoff
                string trackPreloadKey = BuildPreloadTrackKey(track);
                if (TryConsumePreloadedStream(trackPreloadKey, requestedQuality, out var preloaded) && preloaded is not null)
                {
                    resolvedStream = preloaded;
                    playbackUri = preloaded.PlaybackUri;
                }
                else
                {
                    resolvedStream = await ResolveOnlineStreamForTrackAsync(track, requestedQuality, linkedToken).ConfigureAwait(false);
                    playbackUri = resolvedStream?.PlaybackUri;
                }

                if (resolvedStream is not null)
                {
                    if (track.IsOfficialVideoItem && resolvedStream.SourceKind == PlaybackSourceKind.YouTubeMusic)
                    {
                        resolvedStream = resolvedStream with
                        {
                            SourceKind = PlaybackSourceKind.YouTubeVideo,
                            ProviderLabel = "YouTube Video"
                        };
                    }

                    string qualityLabel = resolvedStream.FormatQualityLabel();
                    string sourceLabel = FormatSourceDisplayLabel(resolvedStream.SourceKind, resolvedStream.ProviderLabel, resolvedStream.IsFlacFallback);
                    if (resolvedStream.DownloadedNetworkBytes > 0)
                    {
                        _ = _dataUsageService.RecordUsageAsync(
                            track,
                            DataUsageCategory.Audio,
                            resolvedStream.DownloadedNetworkBytes,
                            qualityLabel,
                            sourceLabel,
                            DataUsageNetworkType.Wifi);
                    }

                    _ = _dataUsageService.RecordStreamPlayAsync(track, qualityLabel, sourceLabel);
                }
            }

            if (resolvedStream is not null)
            {
                UpdateCurrentTrackPlaybackInfo(track, resolvedStream);
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500, linkedToken).ConfigureAwait(false);
                        if (!linkedToken.IsCancellationRequested && requestId == Volatile.Read(ref _activePlayRequestId))
                        {
                            BuildTrackAcousticEnvelope(fullPath);
                        }
                    }
                    catch
                    {
                    }
                }, CancellationToken.None);

                loadedInAudioGraph = await _dspEngine.TryLoadTrackFileAsync(fullPath, linkedToken, openSeek).ConfigureAwait(false);
                if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || linkedToken.IsCancellationRequested)
                {
                    _dspEngine.ResetAndCleanupCurrentTrackNodes();
                    return;
                }

                _activeAudioPlaybackUri = fullPath;

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
                _zeroVolumeCoordinator.OnManualPlayPauseOrTrackChange();
                _lastSessionTick = Stopwatch.GetTimestamp();
                SetTelemetryInterval(25, 28);
                MaybeTriggerGaplessPreload();
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
        if (_isPlaying && _behaviorSettings.GaplessPreloadEnabled)
        {
            MaybeTriggerGaplessPreload();
        }
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
            _zeroVolumeCoordinator.OnManualPlayPauseOrTrackChange();
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
        else
        {
            if (_queueController.UnshuffleUpcoming())
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
    public async Task<bool> SwitchTrackAudioVariantAsync(
        string? officialVideoId,
        bool useVideoAudio,
        Func<string?, string?, TimeSpan, TimeSpan>? resolveTargetPosition = null,
        CancellationToken cancellationToken = default)
    {
        var track = CurrentTrack;
        if (IsDisposed || track is null || track.Id == "empty_state_track")
        {
            return false;
        }

        if (_isVideoAudioModeActive == useVideoAudio)
        {
            return true;
        }

        int requestId = Volatile.Read(ref _activePlayRequestId);
        bool wasPlaying = _isPlaying;
        TimeSpan currentPos = Position;
        StreamingAudioQuality requestedQuality = _behaviorSettings.OnlineQuality;

        string? previousSongPath = !_isVideoAudioModeActive ? _activeAudioPlaybackUri : null;
        string? targetUri = null;
        ResolvedPlaybackStream? resolvedStream = null;

        try
        {
            if (useVideoAudio)
            {
                string vidId = !string.IsNullOrWhiteSpace(officialVideoId)
                    ? officialVideoId
                    : (!string.IsNullOrWhiteSpace(track.OfficialVideoId) ? track.OfficialVideoId : track.YouTubeVideoId);
                if (string.IsNullOrWhiteSpace(vidId))
                {
                    return false;
                }

                resolvedStream = await _youTubeMusicService.ResolveAudioStreamWithMetadataAsync(
                    vidId,
                    requestedQuality,
                    cancellationToken,
                    resolveCanonicalAtv: false).ConfigureAwait(false);
                if (resolvedStream is null || string.IsNullOrWhiteSpace(resolvedStream.PlaybackUri))
                {
                    return false;
                }

                resolvedStream = resolvedStream with
                {
                    SourceKind = PlaybackSourceKind.YouTubeVideo,
                    ProviderLabel = "YouTube Video"
                };
                targetUri = resolvedStream.PlaybackUri;

                if (resolvedStream.DownloadedNetworkBytes > 0)
                {
                    _ = _dataUsageService.RecordUsageAsync(
                        track,
                        DataUsageCategory.Audio,
                        resolvedStream.DownloadedNetworkBytes,
                        resolvedStream.FormatQualityLabel(),
                        "YouTube Video",
                        DataUsageNetworkType.Wifi);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(track.LocalOfflineFilePath) && File.Exists(track.LocalOfflineFilePath))
                {
                    targetUri = track.LocalOfflineFilePath;
                    resolvedStream = InspectLocalAudioFile(targetUri, PlaybackSourceKind.DownloadedOffline, "Downloaded Offline", requestedQuality);
                }
                else if (!string.IsNullOrWhiteSpace(track.AudioSourceUri) && File.Exists(track.AudioSourceUri))
                {
                    targetUri = track.AudioSourceUri;
                    resolvedStream = InspectLocalAudioFile(targetUri, PlaybackSourceKind.Local, "Local File", requestedQuality);
                }
                else
                {
                    var songTrack = track.IsOfficialVideoItem
                        ? new TrackItem
                        {
                            Id = track.Id,
                            Title = track.Title,
                            Artist = track.Artist,
                            Album = track.Album,
                            Duration = track.Duration,
                            CoverArtUri = track.CoverArtUri,
                            AudioSourceUri = track.AudioSourceUri,
                            YouTubeVideoId = track.YouTubeVideoId,
                            SourceType = track.SourceType,
                            IsOfficialVideoItem = false
                        }
                        : track;

                    resolvedStream = await ResolveOnlineStreamForTrackAsync(songTrack, requestedQuality, cancellationToken)
                        .ConfigureAwait(false);
                    targetUri = resolvedStream?.PlaybackUri;
                    if (resolvedStream is not null && resolvedStream.DownloadedNetworkBytes > 0)
                    {
                        _ = _dataUsageService.RecordUsageAsync(
                            track,
                            DataUsageCategory.Audio,
                            resolvedStream.DownloadedNetworkBytes,
                            resolvedStream.FormatQualityLabel(),
                            FormatSourceDisplayLabel(resolvedStream.SourceKind, resolvedStream.ProviderLabel, resolvedStream.IsFlacFallback),
                            DataUsageNetworkType.Wifi);
                    }
                }

                if (string.IsNullOrWhiteSpace(targetUri))
                {
                    return false;
                }
            }

            if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            string? songPathForAlign = useVideoAudio ? previousSongPath : targetUri;
            string? videoPathForAlign = useVideoAudio ? targetUri : _activeAudioPlaybackUri;
            TimeSpan mappedPos = resolveTargetPosition is not null
                ? resolveTargetPosition(songPathForAlign, videoPathForAlign, currentPos)
                : currentPos;
            if (mappedPos < TimeSpan.Zero)
            {
                mappedPos = TimeSpan.Zero;
            }

            _isLoadingMedia = true;
            _dspEngine.PauseGraphPlayback();
            try { _mediaPlayer.Pause(); ReplaceMediaSource(_mediaPlayer, null); } catch { }

            bool loadedInAudioGraph = false;
            if (File.Exists(targetUri))
            {
                string fullPath = Path.GetFullPath(targetUri);
                loadedInAudioGraph = await _dspEngine.TryLoadTrackFileAsync(fullPath, cancellationToken, mappedPos)
                    .ConfigureAwait(false);
                if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || cancellationToken.IsCancellationRequested)
                {
                    _dspEngine.ResetAndCleanupCurrentTrackNodes();
                    return false;
                }

                _activeAudioPlaybackUri = fullPath;
                if (loadedInAudioGraph)
                {
                    UpdateNaturalDuration(_dspEngine.GraphDuration);
                }
                else
                {
                    Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, mappedPos.Ticks);
                    ReplaceMediaSource(_mediaPlayer, MediaSource.CreateFromUri(new Uri(fullPath)));
                }
            }
            else if (Uri.TryCreate(targetUri, UriKind.Absolute, out var uri))
            {
                loadedInAudioGraph = await _dspEngine.TryLoadTrackFileAsync(uri.AbsoluteUri, cancellationToken, mappedPos)
                    .ConfigureAwait(false);
                if (IsDisposed || requestId != Volatile.Read(ref _activePlayRequestId) || cancellationToken.IsCancellationRequested)
                {
                    _dspEngine.ResetAndCleanupCurrentTrackNodes();
                    return false;
                }

                _activeAudioPlaybackUri = uri.AbsoluteUri;
                if (loadedInAudioGraph)
                {
                    UpdateNaturalDuration(_dspEngine.GraphDuration);
                }
                else
                {
                    Interlocked.Exchange(ref _pendingMediaPlayerSeekTicks, mappedPos.Ticks);
                    ReplaceMediaSource(_mediaPlayer, MediaSource.CreateFromUri(uri));
                }
            }
            else
            {
                return false;
            }

            _isVideoAudioModeActive = useVideoAudio;
            if (resolvedStream is not null)
            {
                UpdateCurrentTrackPlaybackInfo(track, resolvedStream);
            }

            var actualPos = loadedInAudioGraph ? _dspEngine.GraphPosition : mappedPos;
            if (Duration > TimeSpan.Zero && actualPos > Duration)
            {
                actualPos = Duration;
            }

            Position = actualPos;
            _lastSessionPosition = actualPos;
            _lastSessionTick = Stopwatch.GetTimestamp();

            if (wasPlaying)
            {
                if (loadedInAudioGraph)
                {
                    _dspEngine.StartGraphPlayback();
                }
                else
                {
                    _mediaPlayer.Play();
                }

                _isPlaying = true;
                SetTelemetryInterval(25, 28);
            }
            else
            {
                _isPlaying = false;
                SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            }

            PositionChanged?.Invoke(this, new PlaybackPositionEventArgs(Position, Duration));
            IsPlayingChanged?.Invoke(this, _isPlaying);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SwitchTrackAudioVariantAsync failed (useVideoAudio={UseVideo}).", useVideoAudio);
            return false;
        }
        finally
        {
            _isLoadingMedia = false;
        }
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
        const int samplePoints = 128;
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

                for (int p = 0; p < samplePoints; p++)
                {
                    long offset = dataStart + ((dataLength - sampleBuf.Length) * p / samplePoints);
                    fs.Seek(offset, SeekOrigin.Begin);
                    int read = fs.Read(sampleBuf, 0, sampleBuf.Length);
                    float bVal = 0.55f;
                    float mVal = 0.50f;
                    float tVal = 0.42f;
                    if (read > 4)
                    {
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

                        bVal = Math.Clamp(0.28f + ((lowAcc / read) * 1.15f), 0.18f, 1.0f);
                        mVal = Math.Clamp(0.24f + ((midAcc / read) * 1.25f), 0.15f, 1.0f);
                        tVal = Math.Clamp(0.20f + ((highAcc / read) * 1.20f), 0.12f, 1.0f);
                    }

                    int startSlice = p * slices / samplePoints;
                    int endSlice = (p + 1) * slices / samplePoints;
                    for (int s = startSlice; s < endSlice; s++)
                    {
                        bass[s] = bVal;
                        mid[s] = mVal;
                        treble[s] = tVal;
                    }
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
    public void PausePlayback()
    {
        PausePlaybackCore(clearZeroVolumeFlag: true);
    }

    private void PausePlaybackCore(bool clearZeroVolumeFlag)
    {
        if (IsDisposed)
        {
            return;
        }

        if (clearZeroVolumeFlag)
        {
            _zeroVolumeCoordinator.OnManualPlayPauseOrTrackChange();
        }

        _pendingAutoPlayAfterLoad = false;
        _progressiveQueueManager.CancelEndAdvance();
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

        if (_isPlaying)
        {
            _isPlaying = false;
            SetTelemetryInterval(Timeout.Infinite, Timeout.Infinite);
            IsPlayingChanged?.Invoke(this, false);
        }
    }

    private void OnSystemMasterVolumeChanged(object? sender, (float MasterVolumeScalar, bool IsMuted) e)
    {
        if (IsDisposed)
        {
            return;
        }

        var action = _zeroVolumeCoordinator.OnSystemVolumeChanged(e.MasterVolumeScalar, e.IsMuted, _isPlaying);
        ApplyZeroVolumeAction(action);
    }

    private void ApplyZeroVolumeAction(ZeroVolumePauseAction action)
    {
        if (action == ZeroVolumePauseAction.Pause)
        {
            PausePlaybackCore(clearZeroVolumeFlag: false);
        }
        else if (action == ZeroVolumePauseAction.Resume)
        {
            _ = ResumeAfterZeroVolumeAsync();
        }
    }

    private async Task ResumeAfterZeroVolumeAsync()
    {
        if (IsDisposed || _isPlaying)
        {
            return;
        }

        await TogglePlayPauseAsync().ConfigureAwait(false);
    }

    private void OnDataLimitReached(object? sender, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        var current = CurrentTrack;
        // Only pause if the currently playing track is an online stream (local/downloaded tracks stay uninterrupted)
        if (current is not null && !current.IsDownloadedOrLocal)
        {
            PausePlaybackCore(clearZeroVolumeFlag: true);
        }

        CancelAndClearNextPreload();
        PlaybackErrorOccurred?.Invoke(this, message);
    }

    private async Task<ResolvedPlaybackStream?> ResolveOnlineStreamForTrackAsync(
        TrackItem track,
        StreamingAudioQuality requestedQuality,
        CancellationToken cancellationToken)
    {
        // 1. Direct certified FLAC Lossless streaming if requested
        if (requestedQuality == StreamingAudioQuality.FlacLossless)
        {
            var flacStream = await _musicDownloadService.ResolveDirectFlacStreamAsync(track, cancellationToken).ConfigureAwait(false);
            if (flacStream is not null)
            {
                return flacStream;
            }
        }

        // 2. YouTube Music resolution (at requested tier, or High160 fallback when FLAC was unavailable)
        string videoId = !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
            ? track.YouTubeVideoId
            : !string.IsNullOrWhiteSpace(track.AudioSourceUri) && track.AudioSourceUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase)
                ? track.AudioSourceUri
                : TrackDeduplicationHelper.StripYtmPrefix(track.Id);

        var ytStream = await _youTubeMusicService.ResolveAudioStreamWithMetadataAsync(
            videoId,
            requestedQuality,
            cancellationToken,
            resolveCanonicalAtv: !track.IsOfficialVideoItem).ConfigureAwait(false);

        if (ytStream is not null && requestedQuality == StreamingAudioQuality.FlacLossless)
        {
            ytStream = ytStream with
            {
                IsFlacFallback = true,
                FallbackReason = "FLAC Lossless stream unavailable for this track; playing YouTube Music High."
            };
        }

        return ytStream;
    }

    private static string BuildPreloadTrackKey(TrackItem track) =>
        !string.IsNullOrWhiteSpace(track.YouTubeVideoId)
            ? $"ytm:{track.YouTubeVideoId}"
            : (!string.IsNullOrWhiteSpace(track.Id) ? track.Id : $"{track.Artist}|{track.Title}");

    private bool TryConsumePreloadedStream(
        string trackKey,
        StreamingAudioQuality requestedQuality,
        out ResolvedPlaybackStream? stream)
    {
        lock (_preloadLock)
        {
            if (_preloadedNext is { } cached &&
                string.Equals(cached.TrackKey, trackKey, StringComparison.OrdinalIgnoreCase) &&
                cached.Quality == requestedQuality &&
                !string.IsNullOrWhiteSpace(cached.Stream.PlaybackUri) &&
                File.Exists(cached.Stream.PlaybackUri))
            {
                stream = cached.Stream;
                _preloadedNext = null;
                return true;
            }
        }

        stream = null;
        return false;
    }

    private void CancelAndClearNextPreload()
    {
        CancellationTokenSource? oldCts;
        lock (_preloadLock)
        {
            oldCts = _preloadCts;
            _preloadCts = null;
            _preloadedNext = null;
            _preloadInFlightKey = null;
        }

        if (oldCts is not null)
        {
            try { oldCts.Cancel(); } catch { }
            try { oldCts.Dispose(); } catch { }
        }
    }

    private void MaybeTriggerGaplessPreload()
    {
        if (IsDisposed || !_behaviorSettings.GaplessPreloadEnabled || _dataUsageService.IsOnlineStreamingBlocked())
        {
            return;
        }

        var upcoming = _queueController.GetUpcomingQueue();
        if (upcoming.Count == 0)
        {
            return;
        }

        var nextTrack = upcoming[0];
        if (nextTrack is null || nextTrack.IsDownloadedOrLocal)
        {
            return;
        }

        StreamingAudioQuality quality = _behaviorSettings.OnlineQuality;
        string key = BuildPreloadTrackKey(nextTrack);
        string composite = $"{key}|{quality}";

        CancellationTokenSource cts;
        lock (_preloadLock)
        {
            if (_preloadedNext is { } existing &&
                string.Equals(existing.TrackKey, key, StringComparison.OrdinalIgnoreCase) &&
                existing.Quality == quality)
            {
                return;
            }

            if (string.Equals(_preloadInFlightKey, composite, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try { _preloadCts?.Cancel(); } catch { }
            try { _preloadCts?.Dispose(); } catch { }
            cts = new CancellationTokenSource();
            _preloadCts = cts;
            _preloadInFlightKey = composite;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, cts.Token).ConfigureAwait(false);
                if (IsDisposed || cts.Token.IsCancellationRequested || !_behaviorSettings.GaplessPreloadEnabled || _dataUsageService.IsOnlineStreamingBlocked())
                {
                    return;
                }

                var resolved = await ResolveOnlineStreamForTrackAsync(nextTrack, quality, cts.Token).ConfigureAwait(false);
                if (resolved is null || cts.Token.IsCancellationRequested)
                {
                    return;
                }

                if (resolved.DownloadedNetworkBytes > 0)
                {
                    string qLabel = resolved.FormatQualityLabel();
                    string sLabel = FormatSourceDisplayLabel(resolved.SourceKind, resolved.ProviderLabel, resolved.IsFlacFallback);
                    await _dataUsageService.RecordUsageAsync(
                        nextTrack,
                        DataUsageCategory.Audio,
                        resolved.DownloadedNetworkBytes,
                        qLabel,
                        sLabel,
                        DataUsageNetworkType.Wifi,
                        cts.Token).ConfigureAwait(false);
                    // Mark DownloadedNetworkBytes = 0 on cached stream so playback does not double-count
                    resolved = resolved with { DownloadedNetworkBytes = 0 };
                }

                lock (_preloadLock)
                {
                    if (!cts.Token.IsCancellationRequested)
                    {
                        _preloadedNext = (key, quality, resolved);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on queue/track change
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Gapless next-track preload skipped for '{Title}'.", nextTrack.Title);
            }
            finally
            {
                lock (_preloadLock)
                {
                    if (string.Equals(_preloadInFlightKey, composite, StringComparison.OrdinalIgnoreCase))
                    {
                        _preloadInFlightKey = null;
                    }
                }
            }
        });
    }

    private static ResolvedPlaybackStream InspectLocalAudioFile(
        string filePath,
        PlaybackSourceKind sourceKind,
        string providerLabel,
        StreamingAudioQuality requestedQuality)
    {
        long fileSize = 0;
        string codec = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        int bitrateKbps = 0;
        int sampleRateHz = 0;
        int channels = 0;
        int bitDepth = 0;
        string isrc = string.Empty;

        try
        {
            if (File.Exists(filePath))
            {
                fileSize = new FileInfo(filePath).Length;
                using var tagFile = TagLib.File.Create(filePath);
                var props = tagFile.Properties;
                if (props is not null)
                {
                    bitrateKbps = props.AudioBitrate;
                    sampleRateHz = props.AudioSampleRate;
                    channels = props.AudioChannels;
                    bitDepth = props.BitsPerSample;
                    var firstCodec = props.Codecs?.FirstOrDefault(c => c is not null);
                    if (firstCodec is not null && !string.IsNullOrWhiteSpace(firstCodec.Description))
                    {
                        string desc = firstCodec.Description;
                        if (desc.Contains("FLAC", StringComparison.OrdinalIgnoreCase)) codec = "FLAC";
                        else if (desc.Contains("MPEG-4 Audio", StringComparison.OrdinalIgnoreCase) || desc.Contains("AAC", StringComparison.OrdinalIgnoreCase)) codec = "AAC";
                        else if (desc.Contains("MPEG", StringComparison.OrdinalIgnoreCase) && desc.Contains("Layer 3", StringComparison.OrdinalIgnoreCase)) codec = "MP3";
                        else if (desc.Contains("Opus", StringComparison.OrdinalIgnoreCase)) codec = "Opus";
                        else if (desc.Contains("Vorbis", StringComparison.OrdinalIgnoreCase)) codec = "Vorbis";
                        else if (desc.Contains("PCM", StringComparison.OrdinalIgnoreCase)) codec = "PCM / WAV";
                    }
                }

                isrc = tagFile.Tag?.ISRC ?? string.Empty;
            }
        }
        catch
        {
            // Keep extension fallback
        }

        StreamingAudioQuality actualQuality = string.Equals(codec, "FLAC", StringComparison.OrdinalIgnoreCase)
            ? StreamingAudioQuality.FlacLossless
            : StreamingAudioQuality.High160;

        return new ResolvedPlaybackStream(
            PlaybackUri: filePath,
            SourceKind: sourceKind,
            ProviderLabel: providerLabel,
            RequestedQuality: requestedQuality,
            ActualQuality: actualQuality,
            IsFlacFallback: false,
            FallbackReason: null,
            Codec: codec,
            BitrateKbps: bitrateKbps,
            SampleRateHz: sampleRateHz,
            ChannelCount: channels,
            BitDepth: bitDepth,
            FileSizeBytes: fileSize,
            Isrc: isrc,
            DownloadedNetworkBytes: 0);
    }

    private static string FormatSourceDisplayLabel(PlaybackSourceKind sourceKind, string providerLabel, bool isFlacFallback) =>
        sourceKind switch
        {
            PlaybackSourceKind.Local => "Local File",
            PlaybackSourceKind.DownloadedOffline => "Downloaded Offline",
            PlaybackSourceKind.YouTubeVideo => "YouTube Video",
            PlaybackSourceKind.FlacGdStudio => string.IsNullOrWhiteSpace(providerLabel) ? "FLAC Lossless (GDStudio)" : providerLabel,
            PlaybackSourceKind.FlacTidal => "FLAC Lossless (Tidal)",
            PlaybackSourceKind.FlacQobuz => "FLAC Lossless (Qobuz)",
            PlaybackSourceKind.YouTubeMusic => isFlacFallback ? "YouTube Music (FLAC Fallback)" : "YouTube Music",
            _ => providerLabel
        };

    private void UpdateCurrentTrackPlaybackInfo(TrackItem track, ResolvedPlaybackStream resolved)
    {
        var info = new TrackPlaybackInfo(
            TrackId: track.Id,
            Title: track.Title,
            Artist: track.Artist,
            Album: string.IsNullOrWhiteSpace(track.Album) ? "—" : track.Album,
            Duration: Duration > TimeSpan.Zero ? Duration : track.Duration,
            SourceKind: resolved.SourceKind,
            SourceDisplayLabel: FormatSourceDisplayLabel(resolved.SourceKind, resolved.ProviderLabel, resolved.IsFlacFallback),
            ProviderLabel: resolved.ProviderLabel,
            RequestedQuality: resolved.RequestedQuality,
            ActualQualityLabel: resolved.FormatQualityLabel(),
            IsFlacFallback: resolved.IsFlacFallback,
            FallbackReason: resolved.FallbackReason,
            Codec: string.IsNullOrWhiteSpace(resolved.Codec) ? "—" : resolved.Codec,
            BitrateKbps: resolved.BitrateKbps,
            SampleRateHz: resolved.SampleRateHz,
            ChannelCount: resolved.ChannelCount,
            BitDepth: resolved.BitDepth,
            FileSizeBytes: resolved.FileSizeBytes,
            FilePathOrUri: !string.IsNullOrWhiteSpace(track.LocalOfflineFilePath)
                ? track.LocalOfflineFilePath
                : (track.SourceType == MediaSourceType.Local ? track.AudioSourceUri : $"ytmusic://{track.YouTubeVideoId}"),
            Isrc: resolved.Isrc,
            ReleaseYear: track.ReleaseYear,
            Genre: track.Genre,
            TrackNumber: track.TrackNumber,
            LyricsProviderName: track.LyricsProviderName);

        _currentTrackPlaybackInfo = info;
        CurrentTrackPlaybackInfoChanged?.Invoke(this, info);
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
        CancelAndClearNextPreload();
        _systemVolumeMonitor.SystemVolumeChanged -= OnSystemMasterVolumeChanged;
        _systemVolumeMonitor.Dispose();
        _dataUsageService.DataLimitReached -= OnDataLimitReached;
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
