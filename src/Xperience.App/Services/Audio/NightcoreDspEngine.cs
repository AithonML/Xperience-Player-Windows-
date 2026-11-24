using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Render;
using WinRT;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services.Audio;

/// <summary>
/// Modular real-time Audio DSP Engine implementing the 1-Click "Nightcore" transformation,
/// coupled speed/pitch resampling (<c>preservesPitch = false</c>), independent WSOLA speed/pitch control,
/// 3-stage series Nightcore filter chain (Low-Shelf 90 Hz +6 dB Q=0.7 -> Peaking 3.5 kHz +2 dB Q=1.0
/// -> Soft-Knee Dynamics Compressor -6 dB 4:1 3ms/150ms), 5-band parametric Equalizer, Bass/Treble shelves,
/// and 150ms per-sample exponential anti-click ramping.
///
/// Renders 100% of audible audio through native Windows Media Foundation (<see cref="MediaFoundationPcmDecoder"/>)
/// -> real-time 32-bit IEEE float PCM DSP -> Windows <see cref="AudioGraph"/> (<see cref="AudioFrameInputNode"/>
/// -> <see cref="AudioDeviceOutputNode"/>), so Nightcore, Pitch, Speed, Equalizer, and Limiter are genuinely
/// applied to every audio sample sent to the speakers with zero track reloads or cuts.
/// </summary>
public sealed class NightcoreDspEngine : IDisposable
{
    private const int DefaultSampleRate = 48000;
    private const int RingBufferCapacityFrames = 120_000; // 2.5 seconds at 48 kHz
    private const int DecoderRefillThresholdFrames = 48_000; // Keep >= 1.0s decoded ahead
    private const int DecoderReadChunkFrames = 4096;
    private const int FilterUpdateBlockFrames = 64; // Recompute biquad coefficients every 64 frames (~1.33ms) during ramps
    private const double Ln100 = 4.605170185988091; // ln(100): reaches 99% of target in RampDurationSeconds (150ms)

    private readonly ILogger<NightcoreDspEngine> _logger;
    private readonly object _dspLock = new();
    private readonly object _ringLock = new();
    private readonly object _graphLock = new();
    private readonly object _frameEnqueueLock = new();
    private readonly object _seekLock = new();
    private readonly SemaphoreSlim _graphInitLock = new(1, 1);

    // Native Windows AudioGraph output stage (AudioFrameInputNode -> AudioDeviceOutputNode)
    private AudioGraph? _audioGraph;
    private AudioDeviceOutputNode? _deviceOutputNode;
    private AudioFrameInputNode? _frameInputNode;
    private MediaPlayer? _attachedMediaPlayer;
    private volatile bool _audioGraphInitialized;
    private volatile bool _audioGraphFaulted;
    private volatile bool _isUsingAudioGraphForCurrentTrack;
    private volatile bool _isGraphPlaying;
    private volatile bool _isSeeking;

    // Active Media Foundation decoder & background producer loop
    private MediaFoundationPcmDecoder? _decoder;
    private CancellationTokenSource? _decoderLoopCts;
    private Task? _decoderLoopTask;
    private readonly AutoResetEvent _decoderWakeSignal = new(false);
    private float[]? _pcmRingBuffer;
    private int _ringReadFrame;
    private int _ringWriteFrame;
    private int _ringAvailableFrames;
    private volatile bool _decoderReachedEof;
    private volatile bool _trackCompletedFired;
    private int _decoderSampleRate = DefaultSampleRate;
    private int _outputSampleRate = DefaultSampleRate;
    private TimeSpan _trackDuration = TimeSpan.Zero;
    private double _sourcePositionFrames;
    private long _trackGeneration;
    private long _seekGeneration;

    // Resampler sliding history window (interleaved stereo floats, lazily allocated on playback)
    private float[]? _resampleWindow;
    private int _resampleWindowFrames;
    private double _resampleReadPos;
    private float[]? _pullTempBuffer;
    private float[]? _quantumOutputBuffer;

    // Independent Speed/Pitch WSOLA time-scaler (lazily allocated only when speed != pitch)
    private WsolaStereoTimeScaler? _wsolaTimeScaler;
    private bool _wasUsingIndependentTimeScale;

    // Sample-level IEEE 754 float Biquad & Compressor states (Stereo L/R)
    private readonly BiquadFilterPair _nightcoreLowShelfFilter = new();
    private readonly BiquadFilterPair _nightcorePeakingFilter = new();
    private readonly BiquadFilterPair _userBassShelfFilter = new();
    private readonly BiquadFilterPair _userTrebleShelfFilter = new();
    private readonly BiquadFilterPair[] _userParametricBands =
    [
        new BiquadFilterPair(),
        new BiquadFilterPair(),
        new BiquadFilterPair(),
        new BiquadFilterPair(),
        new BiquadFilterPair()
    ];
    private readonly SoftKneeDynamicsCompressor _dynamicsCompressor = new();

    // Live per-sample exponentially ramped DSP parameters (150ms anti-click smoothing)
    private double _currentPlaybackRate = 1.0;
    private double _targetPlaybackRate = 1.0;

    private double _currentPitchMultiplier = 1.0;
    private double _targetPitchMultiplier = 1.0;

    private double _currentNightcoreLowShelfDb;
    private double _targetNightcoreLowShelfDb;

    private double _currentNightcorePeakingDb;
    private double _targetNightcorePeakingDb;

    private double _currentCompressorBlend;
    private double _targetCompressorBlend;

    private readonly double[] _currentUserBandDb = new double[5];
    private readonly double[] _targetUserBandDb = new double[5];

    private double _currentBassBoostDb;
    private double _targetBassBoostDb;

    private double _currentTrebleBoostDb;
    private double _targetTrebleBoostDb;

    private double _currentMasterVolume = 0.85;
    private double _targetMasterVolume = 0.85;
    private bool _isMuted;
    private bool _preservesPitch = true;
    private bool _biquadDirty = true;
    private int _disposed;

    /// <summary>
    /// Raised when the active decoded track finishes playing through the DSP pipeline.
    /// </summary>
    public event EventHandler? TrackPlaybackCompleted;

    /// <summary>
    /// Raised when the effective playback rate changes so the audio service and UI stay synchronized.
    /// </summary>
    public event EventHandler<double>? EffectiveRateChanged;

    /// <summary>
    /// Gets the canonical Nightcore 3-stage DSP specification.
    /// </summary>
    public NightcoreDspChainSpec Specification { get; } = new();

    /// <summary>
    /// Gets the instantaneous ramped playback rate (e.g. smoothly transitioning 1.00x &lt;-&gt; 1.22x over 150ms).
    /// </summary>
    public double CurrentPlaybackRate => Volatile.Read(ref _currentPlaybackRate);

    /// <summary>
    /// Gets the instantaneous ramped pitch multiplier (e.g. 1.22x / +3.44 semitones when Nightcore is ON).
    /// </summary>
    public double CurrentPitchMultiplier => Volatile.Read(ref _currentPitchMultiplier);

    /// <summary>
    /// Gets a value indicating whether pitch preservation (time-stretching) is active.
    /// Always <c>false</c> in Nightcore mode so pitch scales naturally with playback rate.
    /// </summary>
    public bool PreservesPitch => _preservesPitch;

    /// <summary>
    /// Gets the instantaneous ramped Nightcore Low-Shelf gain in dB (0.0 to +6.0 dB at 90 Hz, Q = 0.7).
    /// </summary>
    public double CurrentNightcoreLowShelfGainDb => Volatile.Read(ref _currentNightcoreLowShelfDb);

    /// <summary>
    /// Gets the instantaneous ramped Nightcore Peaking gain in dB (0.0 to +2.0 dB at 3.5 kHz, Q = 1.0).
    /// </summary>
    public double CurrentNightcorePeakingGainDb => Volatile.Read(ref _currentNightcorePeakingDb);

    /// <summary>
    /// Gets a value indicating whether the current track is actively routed through the real-time
    /// PCM DSP -> <see cref="AudioGraph"/> pipeline.
    /// </summary>
    public bool IsUsingAudioGraphForCurrentTrack => _isUsingAudioGraphForCurrentTrack;

    /// <summary>
    /// Gets a value indicating whether the <see cref="AudioGraph"/> output pipeline is currently playing.
    /// </summary>
    public bool IsGraphPlaying => _isGraphPlaying;

    /// <summary>
    /// Gets a value indicating whether a seek operation is currently flushing and priming the decoder.
    /// </summary>
    public bool IsSeeking => _isSeeking;

    /// <summary>
    /// Gets the exact playback position of the active track in source time.
    /// </summary>
    public TimeSpan GraphPosition
    {
        get
        {
            if (!_isUsingAudioGraphForCurrentTrack)
            {
                return TimeSpan.Zero;
            }

            int sr = Math.Max(8000, Volatile.Read(ref _decoderSampleRate));
            double frames = Volatile.Read(ref _sourcePositionFrames);
            var pos = TimeSpan.FromSeconds(Math.Max(0.0, frames / sr));
            var dur = _trackDuration;
            return dur > TimeSpan.Zero && pos > dur ? dur : pos;
        }
    }

    /// <summary>
    /// Gets the natural duration of the currently decoded track.
    /// </summary>
    public TimeSpan GraphDuration => _isUsingAudioGraphForCurrentTrack ? _trackDuration : TimeSpan.Zero;

    public NightcoreDspEngine(ILogger<NightcoreDspEngine> logger)
    {
        _logger = logger;
        _dynamicsCompressor.Configure(
            sampleRate: _outputSampleRate,
            thresholdDb: Specification.CompressorThresholdDb,
            ratio: Specification.CompressorRatio,
            attackSeconds: Specification.CompressorAttackSeconds,
            releaseSeconds: Specification.CompressorReleaseSeconds);
        RecalculateBiquadCoefficientsLocked();
    }

    /// <summary>
    /// Attaches the fallback/companion <see cref="MediaPlayer"/> instance so playback rate remains synchronized
    /// if a stream ever cannot be opened by Media Foundation.
    /// </summary>
    public void AttachMediaPlayer(MediaPlayer mediaPlayer)
    {
        ArgumentNullException.ThrowIfNull(mediaPlayer);
        lock (_dspLock)
        {
            _attachedMediaPlayer = mediaPlayer;
            SyncMediaPlayerFallbackRateLocked();
        }
    }

    /// <summary>
    /// Ensures the native Windows <see cref="AudioGraph"/> (<see cref="AudioFrameInputNode"/> ->
    /// <see cref="AudioDeviceOutputNode"/>) is initialized and ready for real-time 32-bit IEEE float PCM rendering.
    /// </summary>
    public async Task<bool> EnsureAudioGraphInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _audioGraphFaulted)
        {
            return false;
        }

        if (_audioGraphInitialized && _audioGraph is not null && _deviceOutputNode is not null && _frameInputNode is not null)
        {
            return true;
        }

        try
        {
            await _graphInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _audioGraphFaulted)
            {
                return false;
            }

            if (_audioGraphInitialized && _audioGraph is not null && _deviceOutputNode is not null && _frameInputNode is not null)
            {
                return true;
            }

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault
            };

            var graphResult = await AudioGraph.CreateAsync(settings).AsTask(cancellationToken).ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success || graphResult.Graph is null)
            {
                _logger.LogWarning("AudioGraph creation failed ({Status}); falling back to MediaPlayer.", graphResult.Status);
                _audioGraphFaulted = true;
                return false;
            }

            var graph = graphResult.Graph;
            var outputResult = await graph.CreateDeviceOutputNodeAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success || outputResult.DeviceOutputNode is null)
            {
                _logger.LogWarning("AudioGraph DeviceOutputNode creation failed ({Status}); falling back to MediaPlayer.", outputResult.Status);
                graph.Dispose();
                _audioGraphFaulted = true;
                return false;
            }

            uint graphSampleRate = Math.Clamp(graph.EncodingProperties.SampleRate, 8000u, 192000u);
            var pcmFloatProps = AudioEncodingProperties.CreatePcm(graphSampleRate, 2, 32);
            pcmFloatProps.Subtype = MediaEncodingSubtypes.Float;

            var frameInputNode = graph.CreateFrameInputNode(pcmFloatProps);
            var deviceOutput = outputResult.DeviceOutputNode;
            frameInputNode.AddOutgoingConnection(deviceOutput);
            frameInputNode.Stop();
            frameInputNode.QuantumStarted += OnFrameInputQuantumStarted;
            frameInputNode.AudioFrameCompleted += OnAudioFrameCompleted;
            graph.UnrecoverableErrorOccurred += OnAudioGraphUnrecoverableError;

            lock (_graphLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    graph.UnrecoverableErrorOccurred -= OnAudioGraphUnrecoverableError;
                    frameInputNode.QuantumStarted -= OnFrameInputQuantumStarted;
                    frameInputNode.AudioFrameCompleted -= OnAudioFrameCompleted;
                    frameInputNode.Dispose();
                    deviceOutput.Dispose();
                    graph.Dispose();
                    return false;
                }

                _audioGraph = graph;
                _deviceOutputNode = deviceOutput;
                _frameInputNode = frameInputNode;
                _audioGraphInitialized = true;
            }

            lock (_dspLock)
            {
                _outputSampleRate = (int)graphSampleRate;
                _dynamicsCompressor.Configure(
                    sampleRate: _outputSampleRate,
                    thresholdDb: Specification.CompressorThresholdDb,
                    ratio: Specification.CompressorRatio,
                    attackSeconds: Specification.CompressorAttackSeconds,
                    releaseSeconds: Specification.CompressorReleaseSeconds);
                _wsolaTimeScaler?.Configure(_outputSampleRate);
                RecalculateBiquadCoefficientsLocked();
            }

            _logger.LogInformation(
                "Initialized Windows AudioGraph real-time PCM DSP output at {SampleRate} Hz (32-bit float stereo -> Nightcore/EQ/Pitch/Speed/Compressor).",
                graphSampleRate);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Windows AudioGraph PCM output; falling back to MediaPlayer.");
            _audioGraphFaulted = true;
            return false;
        }
        finally
        {
            try
            {
                _graphInitLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently during shutdown
            }
        }
    }

    private void OnAudioGraphUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        _logger.LogWarning("AudioGraph encountered unrecoverable error ({Error}); resetting graph for clean re-initialization.", args.Error);
        lock (_graphLock)
        {
            lock (_frameEnqueueLock)
            {
                _isGraphPlaying = false;
                if (!ReferenceEquals(_audioGraph, sender))
                {
                    return;
                }

                _audioGraphInitialized = false;
                try
                {
                    if (_frameInputNode is not null)
                    {
                        _frameInputNode.QuantumStarted -= OnFrameInputQuantumStarted;
                        _frameInputNode.AudioFrameCompleted -= OnAudioFrameCompleted;
                        _frameInputNode.Stop();
                        _frameInputNode.Dispose();
                    }

                    sender.UnrecoverableErrorOccurred -= OnAudioGraphUnrecoverableError;
                    sender.Stop();
                    _deviceOutputNode?.Dispose();
                    sender.Dispose();
                }
                catch
                {
                    // Ignore transient teardown errors on faulted graph
                }
                finally
                {
                    _frameInputNode = null;
                    _deviceOutputNode = null;
                    _audioGraph = null;
                }
            }
        }
    }

    /// <summary>
    /// Opens a local audio file (.m4a, .mp3, .flac, .wav, etc.) or HTTP/HTTPS audio URL through
    /// Windows Media Foundation (<see cref="MediaFoundationPcmDecoder"/>) and attaches it to the live
    /// real-time PCM DSP -> <see cref="AudioGraph"/> pipeline.
    /// Cleans up any previous track's decoder, buffers, and filter states first.
    /// </summary>
    public async Task<bool> TryLoadTrackFileAsync(
        string filePathOrUrl,
        CancellationToken cancellationToken = default,
        TimeSpan? initialSeekPosition = null)
    {
        ResetAndCleanupCurrentTrackNodes();

        if (string.IsNullOrWhiteSpace(filePathOrUrl))
        {
            return false;
        }

        if (!await EnsureAudioGraphInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        long gen = Interlocked.Increment(ref _trackGeneration);
        Interlocked.Increment(ref _seekGeneration);
        int targetSr = Volatile.Read(ref _outputSampleRate);

        MediaFoundationPcmDecoder? decoder = null;
        string? openError = null;
        try
        {
            decoder = await Task.Run(
                () => MediaFoundationPcmDecoder.TryOpen(filePathOrUrl, targetSr, out openError),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            decoder?.Dispose();
            return false;
        }

        if (decoder is null)
        {
            _logger.LogWarning("MediaFoundationPcmDecoder could not open '{Source}': {Reason}", filePathOrUrl, openError);
            return false;
        }

        if (Volatile.Read(ref _disposed) != 0 || gen != Interlocked.Read(ref _trackGeneration) || cancellationToken.IsCancellationRequested)
        {
            decoder.Dispose();
            return false;
        }

        TimeSpan startPosition = TimeSpan.Zero;
        if (initialSeekPosition is { } requestedSeek && requestedSeek > TimeSpan.Zero)
        {
            var clampedSeek = decoder.Duration > TimeSpan.Zero && requestedSeek > decoder.Duration
                ? decoder.Duration
                : requestedSeek;
            if (decoder.Seek(clampedSeek))
            {
                startPosition = clampedSeek;
            }
            else
            {
                _logger.LogWarning(
                    "Initial seek to {Position} failed on '{Source}' (HR=0x{HResult:X8}); starting from 0:00.",
                    clampedSeek,
                    filePathOrUrl,
                    decoder.LastSeekHResult);
            }
        }

        // Pre-decode an initial ~250ms priming window so frame 0 plays instantaneously with zero underrun
        int primeFrameCount = Math.Min(12000, RingBufferCapacityFrames / 4);
        var primeFrames = ArrayPool<float>.Shared.Rent(primeFrameCount * 2);
        int primedFrames;
        bool primeEof;
        try
        {
            primedFrames = decoder.ReadStereoFrames(primeFrames, 0, primeFrameCount, out primeEof);

            lock (_ringLock)
            {
                _ringReadFrame = 0;
                _ringWriteFrame = 0;
                _ringAvailableFrames = 0;
                if (primedFrames > 0)
                {
                    WriteRingBufferLocked(primeFrames, 0, primedFrames);
                }
                _decoderReachedEof = primeEof;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(primeFrames);
        }

        lock (_dspLock)
        {
            _decoderSampleRate = Math.Max(8000, decoder.SampleRate);
            _trackDuration = decoder.Duration;
            _sourcePositionFrames = startPosition.TotalSeconds * _decoderSampleRate;
            _trackCompletedFired = false;
            FlushDspBuffersLocked();
        }

        var loopCts = new CancellationTokenSource();
        lock (_graphLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || gen != Interlocked.Read(ref _trackGeneration))
            {
                loopCts.Dispose();
                decoder.Dispose();
                return false;
            }

            _decoder = decoder;
            _decoderLoopCts = loopCts;
            _isUsingAudioGraphForCurrentTrack = true;
            _decoderLoopTask = Task.Run(() => RunDecoderFillLoop(decoder, gen, loopCts.Token));
        }

        return true;
    }

    /// <summary>
    /// Starts or resumes real-time playback on the active <see cref="AudioGraph"/> PCM DSP pipeline.
    /// </summary>
    public void StartGraphPlayback()
    {
        lock (_graphLock)
        {
            if (!_isUsingAudioGraphForCurrentTrack || _audioGraph is null || _frameInputNode is null)
            {
                return;
            }

            lock (_frameEnqueueLock)
            {
                try
                {
                    // Pre-queue 2 quanta (~20ms) BEFORE enabling QuantumStarted/Start for serialized, click-free start
                    int quantumFrames = Math.Clamp(_audioGraph.SamplesPerQuantum, 256, 2048);
                    if (_frameInputNode.QueuedSampleCount < (ulong)(quantumFrames * 2))
                    {
                        EnqueueProcessedAudioFrameLocked(_frameInputNode, quantumFrames);
                        EnqueueProcessedAudioFrameLocked(_frameInputNode, quantumFrames);
                    }

                    _isGraphPlaying = true;
                    _frameInputNode.Start();
                    _audioGraph.Start();
                }
                catch (Exception ex)
                {
                    _isGraphPlaying = false;
                    _logger.LogDebug(ex, "Transient error starting AudioGraph PCM playback.");
                }
            }
        }
    }

    /// <summary>
    /// Pauses real-time playback on the active <see cref="AudioGraph"/> PCM DSP pipeline.
    /// </summary>
    public void PauseGraphPlayback()
    {
        lock (_graphLock)
        {
            _isGraphPlaying = false;
            if (!_isUsingAudioGraphForCurrentTrack || _audioGraph is null || _frameInputNode is null)
            {
                return;
            }

            lock (_frameEnqueueLock)
            {
                try
                {
                    _frameInputNode.Stop();
                    _frameInputNode.DiscardQueuedFrames();
                    _audioGraph.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Transient error pausing AudioGraph PCM playback.");
                }
            }
        }
    }

    /// <summary>
    /// Seeks the active track to <paramref name="position"/>, flushes ring/resampler/WSOLA/biquad buffers,
    /// and pre-fills decoded audio from the new timestamp so seeking is instant and click-free.
    /// </summary>
    public void SeekGraphPlayback(TimeSpan position)
    {
        _ = TrySeekGraphPlayback(position);
    }

    /// <summary>
    /// Seeks the active track to <paramref name="position"/> with full decoder-loop generation fencing
    /// and guaranteed playback recovery, returning <c>true</c> if the underlying Media Foundation decoder
    /// repositioned successfully.
    /// </summary>
    public bool TrySeekGraphPlayback(TimeSpan position)
    {
        lock (_seekLock)
        {
            MediaFoundationPcmDecoder? decoder;
            bool wasPlaying;
            long seekGen = Interlocked.Increment(ref _seekGeneration);
            _isSeeking = true;

            lock (_graphLock)
            {
                decoder = _decoder;
                wasPlaying = _isGraphPlaying;
                if (!_isUsingAudioGraphForCurrentTrack || decoder is null)
                {
                    lock (_dspLock)
                    {
                        FlushDspBuffersLocked();
                    }

                    _isSeeking = false;
                    return false;
                }

                // Silence audio callback and wait for any in-flight AddFrame before stopping & discarding frames
                _isGraphPlaying = false;
                lock (_frameEnqueueLock)
                {
                    try
                    {
                        _frameInputNode?.Stop();
                        _frameInputNode?.DiscardQueuedFrames();
                    }
                    catch
                    {
                        // Ignore transient stop/discard error
                    }
                }
            }

            try
            {
                var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                if (_trackDuration > TimeSpan.Zero && clamped > _trackDuration)
                {
                    clamped = _trackDuration;
                }

                if (!decoder.Seek(clamped))
                {
                    _logger.LogWarning(
                        "MediaFoundationPcmDecoder.Seek({Position}) failed (HR=0x{HResult:X8}); resuming existing stream.",
                        clamped,
                        decoder.LastSeekHResult);
                    _decoderWakeSignal.Set();
                    return false;
                }

                const int primeFramesCount = 8192;
                var primeBuf = ArrayPool<float>.Shared.Rent(primeFramesCount * 2);
                int primed;
                bool primeEof;
                try
                {
                    primed = decoder.ReadStereoFrames(primeBuf, 0, primeFramesCount, out primeEof);

                    // Guard against imprecise container seek tables hitting premature EOF before track end
                    if (primed == 0 &&
                        primeEof &&
                        _trackDuration > TimeSpan.FromMilliseconds(500) &&
                        clamped < _trackDuration - TimeSpan.FromMilliseconds(500))
                    {
                        var retryPos = clamped > TimeSpan.FromMilliseconds(350)
                            ? clamped - TimeSpan.FromMilliseconds(350)
                            : TimeSpan.Zero;
                        if (decoder.Seek(retryPos))
                        {
                            int retryPrimed = decoder.ReadStereoFrames(primeBuf, 0, primeFramesCount, out bool retryEof);
                            if (retryPrimed > 0)
                            {
                                primed = retryPrimed;
                                primeEof = retryEof;
                                clamped = retryPos;
                            }
                        }
                    }

                    if (Volatile.Read(ref _disposed) != 0 ||
                        seekGen != Interlocked.Read(ref _seekGeneration) ||
                        !ReferenceEquals(_decoder, decoder))
                    {
                        return false;
                    }

                    lock (_ringLock)
                    {
                        _ringReadFrame = 0;
                        _ringWriteFrame = 0;
                        _ringAvailableFrames = 0;
                        if (primed > 0)
                        {
                            WriteRingBufferLocked(primeBuf, 0, primed);
                        }
                        _decoderReachedEof = primeEof;
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(primeBuf);
                }

                lock (_dspLock)
                {
                    _sourcePositionFrames = clamped.TotalSeconds * Math.Max(8000, _decoderSampleRate);
                    _trackCompletedFired = false;
                    FlushDspBuffersLocked();
                }

                _decoderWakeSignal.Set();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error during AudioGraph seek to {Position}.", position);
                _decoderWakeSignal.Set();
                return false;
            }
            finally
            {
                _isSeeking = false;
                if (wasPlaying &&
                    Volatile.Read(ref _disposed) == 0 &&
                    _isUsingAudioGraphForCurrentTrack &&
                    ReferenceEquals(_decoder, decoder))
                {
                    StartGraphPlayback();
                }
            }
        }
    }

    /// <summary>
    /// Updates the master output volume (0.0 to 1.0) and mute state smoothly per sample without clicks.
    /// </summary>
    public void SetMasterVolume(double volume, bool isMuted)
    {
        lock (_dspLock)
        {
            _isMuted = isMuted;
            _targetMasterVolume = isMuted ? 0.0 : Math.Clamp(volume, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Stops, disconnects, and disposes the active track's decoder &amp; background producer loop,
    /// discards queued <see cref="AudioFrameInputNode"/> frames, and zeros all ring, resampler,
    /// WSOLA, Biquad delay lines, and compressor envelopes to guarantee zero memory leaks or audio glitches.
    /// </summary>
    public void ResetAndCleanupCurrentTrackNodes()
    {
        Interlocked.Increment(ref _trackGeneration);
        Interlocked.Increment(ref _seekGeneration);

        CancellationTokenSource? ctsToCancel;
        MediaFoundationPcmDecoder? decoderToDispose;

        lock (_graphLock)
        {
            _isGraphPlaying = false;
            _isUsingAudioGraphForCurrentTrack = false;

            lock (_frameEnqueueLock)
            {
                try
                {
                    _frameInputNode?.Stop();
                    _frameInputNode?.DiscardQueuedFrames();
                    _audioGraph?.Stop();
                }
                catch
                {
                    // Ignore transient AudioGraph stop errors
                }
            }

            ctsToCancel = _decoderLoopCts;
            _decoderLoopCts = null;
            decoderToDispose = _decoder;
            _decoder = null;
        }

        if (ctsToCancel is not null)
        {
            try { ctsToCancel.Cancel(); } catch { }
            _decoderWakeSignal.Set();
            try { ctsToCancel.Dispose(); } catch { }
        }

        if (decoderToDispose is not null)
        {
            try { decoderToDispose.Dispose(); } catch { }
        }

        lock (_ringLock)
        {
            _ringReadFrame = 0;
            _ringWriteFrame = 0;
            _ringAvailableFrames = 0;
            _decoderReachedEof = false;
            if (_pcmRingBuffer is not null)
            {
                Array.Clear(_pcmRingBuffer);
            }
        }

        lock (_dspLock)
        {
            _sourcePositionFrames = 0.0;
            _trackDuration = TimeSpan.Zero;
            _trackCompletedFired = false;
            FlushDspBuffersLocked();
        }
    }

    /// <summary>
    /// Transitions the DSP engine to the target <paramref name="state"/> using a smooth 150ms
    /// per-sample exponential anti-click ramp (zero track reload, zero clicks, vinyl-like speed/pitch glide).
    /// </summary>
    public void TransitionToState(AudioDspState state, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(state);

        double rateSnapshot;
        lock (_dspLock)
        {
            _preservesPitch = state.PreservesPitch;

            double targetRate = Math.Clamp(state.EffectivePlaybackSpeed, 0.50, 2.00);
            double targetPitch = Math.Clamp(state.EffectivePitchMultiplier, 0.50, 2.00);
            double targetNightcoreLowShelf = state.IsNightcoreActive ? Specification.LowShelfGainDb : 0.0;
            double targetNightcorePeaking = state.IsNightcoreActive ? Specification.PeakingGainDb : 0.0;
            double targetCompressorBlend = state.IsNightcoreActive ? 1.0 : (state.IsEqualizerEnabled ? 0.40 : 0.0);

            double targetBass = state.IsEqualizerEnabled ? Math.Clamp(state.BassBoostDb, -12.0, 12.0) : 0.0;
            double targetTreble = state.IsEqualizerEnabled ? Math.Clamp(state.TrebleBoostDb, -12.0, 12.0) : 0.0;

            _targetPlaybackRate = targetRate;
            _targetPitchMultiplier = targetPitch;
            _targetNightcoreLowShelfDb = targetNightcoreLowShelf;
            _targetNightcorePeakingDb = targetNightcorePeaking;
            _targetCompressorBlend = targetCompressorBlend;
            _targetBassBoostDb = targetBass;
            _targetTrebleBoostDb = targetTreble;

            for (int i = 0; i < 5; i++)
            {
                double rawBand = i < state.BandGainsDb.Count ? state.BandGainsDb[i] : 0.0;
                _targetUserBandDb[i] = state.IsEqualizerEnabled ? Math.Clamp(rawBand, -12.0, 12.0) : 0.0;
            }

            if (immediate || Specification.RampDurationSeconds <= 0.005)
            {
                _currentPlaybackRate = targetRate;
                _currentPitchMultiplier = targetPitch;
                _currentNightcoreLowShelfDb = targetNightcoreLowShelf;
                _currentNightcorePeakingDb = targetNightcorePeaking;
                _currentCompressorBlend = targetCompressorBlend;
                _currentBassBoostDb = targetBass;
                _currentTrebleBoostDb = targetTreble;
                for (int i = 0; i < 5; i++)
                {
                    _currentUserBandDb[i] = _targetUserBandDb[i];
                }

                RecalculateBiquadCoefficientsLocked();
            }
            else
            {
                _biquadDirty = true;
            }

            SyncMediaPlayerFallbackRateLocked();
            rateSnapshot = _targetPlaybackRate;
        }

        EffectiveRateChanged?.Invoke(this, rateSnapshot);
    }

    /// <summary>
    /// Processes an interleaved stereo 32-bit float PCM buffer in-place through the series Biquad + Compressor chain:
    /// 1. Stage 1: Nightcore Low-Shelf Biquad Filter (90 Hz, +6 dB, Q = 0.7)
    /// 2. Stage 2: Nightcore Peaking Biquad Filter (3.5 kHz, +2 dB, Q = 1.0)
    /// 3. User 5-Band Parametric Equalizer + Bass/Treble Tone Shelves
    /// 4. Stage 3: Soft-Knee Dynamics Compressor / Limiter (-6 dB threshold, 4:1 ratio, 3ms attack, 150ms release)
    /// </summary>
    public void ProcessPcmInterleaved(Span<float> interleavedStereoPcm)
    {
        if (interleavedStereoPcm.Length < 2)
        {
            return;
        }

        lock (_dspLock)
        {
            int frameCount = interleavedStereoPcm.Length / 2;
            ApplyFilterAndDynamicsChainLocked(interleavedStereoPcm, frameCount, applyMasterVolume: false);
        }
    }

    /// <summary>
    /// Runs the COMPLETE end-to-end Nightcore DSP pipeline (Speed/Pitch coupled Hermite resampling OR
    /// independent WSOLA + Hermite resampling -> 150ms exponential ramp -> 90 Hz Low-Shelf -> 3.5 kHz Peaking
    /// -> 5-band EQ + Bass/Treble -> -6 dB 4:1 3ms/150ms Soft-Knee Compressor) on an offline stereo PCM buffer.
    /// Uses the exact same code path as live speaker playback so verification tests measure the true audible output.
    /// </summary>
    public int ProcessOfflineStream(
        ReadOnlySpan<float> inputStereoPcm,
        Span<float> outputStereoPcm,
        int sampleRate = DefaultSampleRate,
        bool snapParametersImmediately = true)
    {
        int inputFrames = inputStereoPcm.Length / 2;
        int maxOutputFrames = outputStereoPcm.Length / 2;
        if (inputFrames <= 4 || maxOutputFrames <= 0)
        {
            return 0;
        }

        lock (_dspLock)
        {
            _outputSampleRate = Math.Max(8000, sampleRate);
            _decoderSampleRate = _outputSampleRate;
            _dynamicsCompressor.Configure(
                sampleRate: _outputSampleRate,
                thresholdDb: Specification.CompressorThresholdDb,
                ratio: Specification.CompressorRatio,
                attackSeconds: Specification.CompressorAttackSeconds,
                releaseSeconds: Specification.CompressorReleaseSeconds);
            _wsolaTimeScaler?.Configure(_outputSampleRate);
            FlushDspBuffersLocked();

            if (snapParametersImmediately)
            {
                _currentPlaybackRate = _targetPlaybackRate;
                _currentPitchMultiplier = _targetPitchMultiplier;
                _currentNightcoreLowShelfDb = _targetNightcoreLowShelfDb;
                _currentNightcorePeakingDb = _targetNightcorePeakingDb;
                _currentCompressorBlend = _targetCompressorBlend;
                _currentBassBoostDb = _targetBassBoostDb;
                _currentTrebleBoostDb = _targetTrebleBoostDb;
                for (int i = 0; i < 5; i++)
                {
                    _currentUserBandDb[i] = _targetUserBandDb[i];
                }
            }
            RecalculateBiquadCoefficientsLocked();

            float[] inputCopy = inputStereoPcm.ToArray();
            int inputCursorFrames = 0;
            int writtenTotalFrames = 0;
            const int blockFrames = 480;

            while (writtenTotalFrames < maxOutputFrames)
            {
                int chunk = Math.Min(blockFrames, maxOutputFrames - writtenTotalFrames);
                Span<float> destSlice = outputStereoPcm.Slice(writtenTotalFrames * 2, chunk * 2);

                int produced = RenderStereoBlockLocked(
                    destSlice,
                    chunk,
                    pullSourceFrames: (float[] buf, int reqFrames) =>
                    {
                        int avail = inputFrames - inputCursorFrames;
                        int take = Math.Min(reqFrames, avail);
                        if (take > 0)
                        {
                            Array.Copy(inputCopy, inputCursorFrames * 2, buf, 0, take * 2);
                            inputCursorFrames += take;
                        }
                        return take;
                    },
                    isEndOfInput: () => inputCursorFrames >= inputFrames,
                    applyMasterVolume: false);

                if (produced <= 0)
                {
                    break;
                }

                writtenTotalFrames += produced;
            }

            return writtenTotalFrames;
        }
    }

    /// <summary>
    /// Computes the real-time spectral gain multipliers for Bass (20-250 Hz), Mid (250-2500 Hz),
    /// and Treble (2.5-16 kHz) so the 60/120 FPS Win2D GPU visualizer reflects the active Nightcore &amp; EQ curve.
    /// </summary>
    public void GetVisualizerSpectralGainMultipliers(
        out float bassMultiplier,
        out float midMultiplier,
        out float trebleMultiplier)
    {
        double lowDb = Volatile.Read(ref _currentNightcoreLowShelfDb)
                     + Volatile.Read(ref _currentBassBoostDb)
                     + ((_currentUserBandDb[0] + _currentUserBandDb[1]) * 0.5);

        double midDb = _currentUserBandDb[2] + (Volatile.Read(ref _currentNightcorePeakingDb) * 0.5);

        double highDb = Volatile.Read(ref _currentNightcorePeakingDb)
                      + Volatile.Read(ref _currentTrebleBoostDb)
                      + ((_currentUserBandDb[3] + _currentUserBandDb[4]) * 0.5);

        bassMultiplier = (float)Math.Clamp(Math.Pow(10.0, lowDb / 28.0), 0.65, 1.55);
        midMultiplier = (float)Math.Clamp(Math.Pow(10.0, midDb / 28.0), 0.70, 1.40);
        trebleMultiplier = (float)Math.Clamp(Math.Pow(10.0, highDb / 28.0), 0.70, 1.45);
    }

    private void OnFrameInputQuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_isGraphPlaying || _isSeeking || !_isUsingAudioGraphForCurrentTrack)
        {
            return;
        }

        int requiredFrames = args.RequiredSamples;
        if (requiredFrames <= 0)
        {
            return;
        }

        lock (_frameEnqueueLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_isGraphPlaying || _isSeeking || !_isUsingAudioGraphForCurrentTrack)
            {
                return;
            }

            try
            {
                EnqueueProcessedAudioFrameLocked(sender, requiredFrames);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Transient AudioFrameInputNode quantum error.");
            }
        }
    }

    private static void OnAudioFrameCompleted(AudioFrameInputNode sender, AudioFrameCompletedEventArgs args)
    {
        try
        {
            args?.Frame?.Dispose();
        }
        catch
        {
            // Ignore transient COM disposal race on completed AudioFrame
        }
    }

    private void EnqueueProcessedAudioFrameLocked(AudioFrameInputNode frameInputNode, int requiredFrames)
    {
        int floatCount = requiredFrames * 2;
        if (_quantumOutputBuffer is null || _quantumOutputBuffer.Length < floatCount)
        {
            int nextSize = _quantumOutputBuffer is null
                ? Math.Max(floatCount, 4096 * 2)
                : Math.Max(floatCount, _quantumOutputBuffer.Length * 2);
            _quantumOutputBuffer = new float[nextSize];
        }

        var quantumBuffer = _quantumOutputBuffer;
        bool fireCompleted = false;
        int producedFrames;

        lock (_dspLock)
        {
            producedFrames = RenderStereoBlockLocked(
                quantumBuffer.AsSpan(0, floatCount),
                requiredFrames,
                pullSourceFrames: ReadFromRingBuffer,
                isEndOfInput: () => _decoderReachedEof && Volatile.Read(ref _ringAvailableFrames) <= 0,
                applyMasterVolume: true);

            if (producedFrames < requiredFrames)
            {
                Array.Clear(quantumBuffer, producedFrames * 2, (requiredFrames - producedFrames) * 2);
            }

            if (producedFrames == 0 &&
                _decoderReachedEof &&
                Volatile.Read(ref _ringAvailableFrames) <= 0 &&
                !_trackCompletedFired)
            {
                _trackCompletedFired = true;
                fireCompleted = true;
            }
        }

        uint byteCapacity = (uint)(floatCount * sizeof(float));
        var frame = new AudioFrame(byteCapacity);
        try
        {
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                reference.As<IMemoryBufferByteAccess>().GetBuffer(out IntPtr destPtr, out uint actualCap);
                int maxFloats = Math.Min(floatCount, (int)(actualCap / sizeof(float)));
                if (maxFloats > 0 && destPtr != IntPtr.Zero)
                {
                    Marshal.Copy(quantumBuffer, 0, destPtr, maxFloats);
                    buffer.Length = (uint)(maxFloats * sizeof(float));
                }
            }

            frameInputNode.AddFrame(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        if (fireCompleted)
        {
            // Notify AudioPlayerService off the audio thread so track advancement never blocks AudioGraph
            _ = Task.Run(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    TrackPlaybackCompleted?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    /// <summary>
    /// Core real-time DSP render engine:
    /// 1. Pulls source PCM and executes either Coupled 4-Point Hermite Resampling (Nightcore / Vinyl mode,
    ///    <c>preservesPitch = false</c>) OR Independent WSOLA Time-Scale + 4-Point Hermite Pitch Resampling.
    /// 2. Advances exact source frame cursor (<see cref="_sourcePositionFrames"/>) for zero progress-bar drift.
    /// 3. Runs the 150ms per-sample exponential anti-click ramp across rate, pitch, EQ, compressor, and volume.
    /// 4. Processes stereo output through Low-Shelf 90Hz (+6dB Q=0.7) -> Peaking 3.5kHz (+2dB Q=1.0)
    ///    -> User 5-Band EQ + Bass/Treble -> Soft-Knee Dynamics Compressor (-6dB, 4:1, 3ms/150ms).
    /// </summary>
    private WsolaStereoTimeScaler EnsureWsolaTimeScalerLocked()
    {
        if (_wsolaTimeScaler is null)
        {
            _wsolaTimeScaler = new WsolaStereoTimeScaler();
            _wsolaTimeScaler.Configure(_outputSampleRate);
        }

        return _wsolaTimeScaler;
    }

    private float[] EnsureRingBufferLocked()
    {
        return _pcmRingBuffer ??= new float[RingBufferCapacityFrames * 2];
    }

    private int RenderStereoBlockLocked(
        Span<float> outputStereo,
        int requestedFrames,
        Func<float[], int, int> pullSourceFrames,
        Func<bool> isEndOfInput,
        bool applyMasterVolume)
    {
        var resampleWindow = _resampleWindow ??= new float[32768 * 2];
        _pullTempBuffer ??= new float[16384 * 2];

        double sampleRate = Math.Max(8000, _outputSampleRate);
        double rampSec = Math.Max(0.015, Specification.RampDurationSeconds); // 0.150s (150ms)
        double alphaPerSample = 1.0 - Math.Exp(-Ln100 / (rampSec * sampleRate));
        double sourceRateRatio = (double)Math.Max(8000, _decoderSampleRate) / sampleRate;

        // Determine if we need independent WSOLA time-scaling (speed != pitch) vs pure coupled Hermite resampling
        bool needIndependentTimeScale =
            Math.Abs(_currentPlaybackRate - _currentPitchMultiplier) > 0.005 ||
            Math.Abs(_targetPlaybackRate - _targetPitchMultiplier) > 0.005;

        if (!needIndependentTimeScale && _wasUsingIndependentTimeScale)
        {
            _wsolaTimeScaler?.Reset();
            _wasUsingIndependentTimeScale = false;
        }
        else if (needIndependentTimeScale && !_wasUsingIndependentTimeScale)
        {
            EnsureWsolaTimeScalerLocked().Reset();
            _wasUsingIndependentTimeScale = true;
        }

        int writtenFrames = 0;

        while (writtenFrames < requestedFrames)
        {
            // Advance per-sample exponential ramp for Speed & Pitch (150ms vinyl turntable feel)
            _currentPlaybackRate = StepExponential(_currentPlaybackRate, _targetPlaybackRate, alphaPerSample);
            _currentPitchMultiplier = StepExponential(_currentPitchMultiplier, _targetPitchMultiplier, alphaPerSample);

            double resampleStep;
            if (!needIndependentTimeScale)
            {
                // PURE COUPLED RESAMPLING (Nightcore mode: 1.22x speed + 1.22x pitch = +3.44 semitones, preservesPitch = false)
                resampleStep = Math.Clamp(_currentPlaybackRate, 0.50, 2.00) * sourceRateRatio;
                EnsureResampleWindowHasFramesLocked(pullSourceFrames, minLookaheadFrames: 8, useWsola: false, tempoRatio: 1.0);
            }
            else
            {
                // INDEPENDENT SPEED & PITCH: WSOLA time-scales by (speed / pitch), then Hermite resamples by pitch!
                double pitch = Math.Clamp(_currentPitchMultiplier, 0.50, 2.00);
                double speed = Math.Clamp(_currentPlaybackRate, 0.50, 2.00);
                double tempoRatio = Math.Clamp(speed / pitch, 0.25, 4.0);
                resampleStep = pitch * sourceRateRatio;
                EnsureResampleWindowHasFramesLocked(pullSourceFrames, minLookaheadFrames: 8, useWsola: true, tempoRatio: tempoRatio);
            }

            int i1 = (int)_resampleReadPos;
            if (i1 >= _resampleWindowFrames - 2)
            {
                if (isEndOfInput())
                {
                    break;
                }

                // Buffer momentarily dry while decoder wakes up — output silence without corrupting phase
                outputStereo[writtenFrames * 2] = 0f;
                outputStereo[(writtenFrames * 2) + 1] = 0f;
                writtenFrames++;
                continue;
            }

            float frac = (float)(_resampleReadPos - i1);
            int i0 = Math.Max(0, i1 - 1);
            int i2 = Math.Min(_resampleWindowFrames - 1, i1 + 1);
            int i3 = Math.Min(_resampleWindowFrames - 1, i1 + 2);

            outputStereo[writtenFrames * 2] = HermiteInterpolate(
                resampleWindow[i0 * 2],
                resampleWindow[i1 * 2],
                resampleWindow[i2 * 2],
                resampleWindow[i3 * 2],
                frac);

            outputStereo[(writtenFrames * 2) + 1] = HermiteInterpolate(
                resampleWindow[(i0 * 2) + 1],
                resampleWindow[(i1 * 2) + 1],
                resampleWindow[(i2 * 2) + 1],
                resampleWindow[(i3 * 2) + 1],
                frac);

            _resampleReadPos += resampleStep;
            _sourcePositionFrames += Math.Clamp(_currentPlaybackRate, 0.50, 2.00) * sourceRateRatio;
            writtenFrames++;

            // Periodically compact the sliding resampler window so indices stay small & cache-hot
            if (_resampleReadPos >= 4096.0)
            {
                CompactResampleWindowLocked();
            }
        }

        if (writtenFrames > 0)
        {
            ApplyFilterAndDynamicsChainLocked(outputStereo, writtenFrames, applyMasterVolume);
        }

        return writtenFrames;
    }

    private void ApplyFilterAndDynamicsChainLocked(Span<float> stereoPcm, int frameCount, bool applyMasterVolume)
    {
        double sampleRate = Math.Max(8000, _outputSampleRate);
        double rampSec = Math.Max(0.015, Specification.RampDurationSeconds);
        double alphaPerSample = 1.0 - Math.Exp(-Ln100 / (rampSec * sampleRate));
        double alphaPerBlock = 1.0 - Math.Exp(-Ln100 * FilterUpdateBlockFrames / (rampSec * sampleRate));

        int processed = 0;
        while (processed < frameCount)
        {
            int subBlock = Math.Min(FilterUpdateBlockFrames, frameCount - processed);

            // Smoothly ramp filter gains every 64 frames (~1.33 ms) over 150ms exponential curve
            bool anyFilterRamping = AdvanceFilterGainRampsLocked(alphaPerBlock);
            if (anyFilterRamping || _biquadDirty)
            {
                RecalculateBiquadCoefficientsLocked();
                _biquadDirty = false;
            }

            float compBlend = (float)Math.Clamp(_currentCompressorBlend, 0.0, 1.0);

            for (int f = 0; f < subBlock; f++)
            {
                int idx = (processed + f) * 2;
                float left = stereoPcm[idx];
                float right = stereoPcm[idx + 1];

                // Stage 1: Nightcore Low-Shelf Biquad Filter (Sub-bass punch: 90 Hz, +6.0 dB, Q = 0.7)
                _nightcoreLowShelfFilter.ProcessStereo(ref left, ref right);

                // Stage 2: Nightcore Peaking Biquad Filter (Vocal clarity & high brightness: 3.5 kHz, +2.0 dB, Q = 1.0)
                _nightcorePeakingFilter.ProcessStereo(ref left, ref right);

                // Stage 2B: User Graves/Agudos Tone Shelves & 5-Band Parametric Equalizer
                _userBassShelfFilter.ProcessStereo(ref left, ref right);
                for (int b = 0; b < _userParametricBands.Length; b++)
                {
                    _userParametricBands[b].ProcessStereo(ref left, ref right);
                }
                _userTrebleShelfFilter.ProcessStereo(ref left, ref right);

                // Stage 3: Soft-Knee Feed-Forward Dynamics Compressor / Limiter (-6 dB, 4:1, 3ms attack, 150ms release)
                if (compBlend > 0.001f)
                {
                    float compL = left;
                    float compR = right;
                    _dynamicsCompressor.ProcessStereo(ref compL, ref compR);
                    left += (compL - left) * compBlend;
                    right += (compR - right) * compBlend;
                }

                // Stage 4: Click-free per-sample smoothed Master Volume & Sleep Timer fade-out
                if (applyMasterVolume)
                {
                    _currentMasterVolume = StepExponential(_currentMasterVolume, _targetMasterVolume, alphaPerSample);
                    float vol = (float)_currentMasterVolume;
                    left *= vol;
                    right *= vol;
                }

                stereoPcm[idx] = left;
                stereoPcm[idx + 1] = right;
            }

            processed += subBlock;
        }
    }

    private bool AdvanceFilterGainRampsLocked(double alphaBlock)
    {
        bool changed = false;

        changed |= StepFilterParameter(ref _currentNightcoreLowShelfDb, _targetNightcoreLowShelfDb, alphaBlock);
        changed |= StepFilterParameter(ref _currentNightcorePeakingDb, _targetNightcorePeakingDb, alphaBlock);
        changed |= StepFilterParameter(ref _currentCompressorBlend, _targetCompressorBlend, alphaBlock);
        changed |= StepFilterParameter(ref _currentBassBoostDb, _targetBassBoostDb, alphaBlock);
        changed |= StepFilterParameter(ref _currentTrebleBoostDb, _targetTrebleBoostDb, alphaBlock);

        for (int i = 0; i < 5; i++)
        {
            changed |= StepFilterParameter(ref _currentUserBandDb[i], _targetUserBandDb[i], alphaBlock);
        }

        return changed;
    }

    private static bool StepFilterParameter(ref double current, double target, double alpha)
    {
        double diff = target - current;
        if (Math.Abs(diff) < 1e-4)
        {
            if (current != target)
            {
                current = target;
                return true;
            }
            return false;
        }

        current += diff * alpha;
        return true;
    }

    private static double StepExponential(double current, double target, double alpha)
    {
        double diff = target - current;
        if (Math.Abs(diff) < 1e-5)
        {
            return target;
        }
        return current + (diff * alpha);
    }

    private void EnsureResampleWindowHasFramesLocked(
        Func<float[], int, int> pullSourceFrames,
        int minLookaheadFrames,
        bool useWsola,
        double tempoRatio)
    {
        var resampleWindow = _resampleWindow ??= new float[32768 * 2];
        var pullTempBuffer = _pullTempBuffer ??= new float[16384 * 2];

        int remaining = _resampleWindowFrames - (int)_resampleReadPos;
        if (remaining >= minLookaheadFrames)
        {
            return;
        }

        CompactResampleWindowLocked();

        if (!useWsola)
        {
            int spaceFrames = (resampleWindow.Length / 2) - _resampleWindowFrames;
            int toPull = Math.Min(spaceFrames, 4096);
            if (toPull <= 0)
            {
                return;
            }

            int pulled = pullSourceFrames(pullTempBuffer, toPull);
            if (pulled > 0)
            {
                Array.Copy(pullTempBuffer, 0, resampleWindow, _resampleWindowFrames * 2, pulled * 2);
                _resampleWindowFrames += pulled;
            }
        }
        else
        {
            // Feed raw PCM into WSOLA and pull time-scaled stereo frames into _resampleWindow
            var wsola = EnsureWsolaTimeScalerLocked();
            int safetyLoops = 0;
            while (_resampleWindowFrames - (int)_resampleReadPos < minLookaheadFrames && safetyLoops++ < 8)
            {
                if (wsola.AvailableOutputFrames < 512)
                {
                    int pulled = pullSourceFrames(pullTempBuffer, 2048);
                    if (pulled > 0)
                    {
                        wsola.WriteInput(pullTempBuffer.AsSpan(0, pulled * 2), tempoRatio);
                    }
                    else
                    {
                        wsola.Flush(tempoRatio);
                    }
                }

                int spaceFrames = (resampleWindow.Length / 2) - _resampleWindowFrames;
                if (spaceFrames <= 0)
                {
                    break;
                }

                int drained = wsola.ReadOutput(resampleWindow.AsSpan(_resampleWindowFrames * 2, spaceFrames * 2));
                if (drained <= 0)
                {
                    break;
                }

                _resampleWindowFrames += drained;
            }
        }
    }

    private void CompactResampleWindowLocked()
    {
        if (_resampleWindow is null)
        {
            _resampleWindowFrames = 0;
            _resampleReadPos = 0.0;
            return;
        }

        int dropFrames = Math.Max(0, (int)_resampleReadPos - 2); // Keep 2 history frames for Hermite y0/y1
        if (dropFrames <= 0)
        {
            return;
        }

        int keepFrames = Math.Max(0, _resampleWindowFrames - dropFrames);
        if (keepFrames > 0)
        {
            Array.Copy(_resampleWindow, dropFrames * 2, _resampleWindow, 0, keepFrames * 2);
        }

        _resampleWindowFrames = keepFrames;
        _resampleReadPos -= dropFrames;
        if (_resampleReadPos < 0.0)
        {
            _resampleReadPos = 0.0;
        }
    }

    private void RunDecoderFillLoop(MediaFoundationPcmDecoder decoder, long generation, CancellationToken token)
    {
        var chunk = new float[DecoderReadChunkFrames * 2];

        try
        {
            while (!token.IsCancellationRequested &&
                   Volatile.Read(ref _disposed) == 0 &&
                   generation == Interlocked.Read(ref _trackGeneration))
            {
                if (_isSeeking)
                {
                    _decoderWakeSignal.WaitOne(10);
                    continue;
                }

                int freeFrames;
                bool eof;
                lock (_ringLock)
                {
                    freeFrames = RingBufferCapacityFrames - _ringAvailableFrames;
                    eof = _decoderReachedEof;
                }

                if (eof || freeFrames < DecoderReadChunkFrames)
                {
                    _decoderWakeSignal.WaitOne(20);
                    continue;
                }

                long seekGenBefore = Interlocked.Read(ref _seekGeneration);
                int toRead = Math.Min(DecoderReadChunkFrames, freeFrames);
                int read = decoder.ReadStereoFrames(chunk, 0, toRead, out bool reachedEof);

                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _trackGeneration))
                {
                    break;
                }

                // Discard pre-seek reads/EOF flags if a seek occurred while ReadStereoFrames was executing
                if (seekGenBefore != Interlocked.Read(ref _seekGeneration))
                {
                    continue;
                }

                lock (_ringLock)
                {
                    if (seekGenBefore != Interlocked.Read(ref _seekGeneration))
                    {
                        continue;
                    }

                    if (read > 0)
                    {
                        WriteRingBufferLocked(chunk, 0, read);
                    }

                    if (reachedEof)
                    {
                        _decoderReachedEof = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Decoder producer loop exited.");
        }
    }

    private void WriteRingBufferLocked(float[] source, int sourceFrameOffset, int frameCount)
    {
        int maxWritable = RingBufferCapacityFrames - _ringAvailableFrames;
        int count = Math.Min(frameCount, maxWritable);
        if (count <= 0)
        {
            return;
        }

        var ring = EnsureRingBufferLocked();
        int firstPart = Math.Min(count, RingBufferCapacityFrames - _ringWriteFrame);
        Array.Copy(source, sourceFrameOffset * 2, ring, _ringWriteFrame * 2, firstPart * 2);

        int secondPart = count - firstPart;
        if (secondPart > 0)
        {
            Array.Copy(source, (sourceFrameOffset + firstPart) * 2, ring, 0, secondPart * 2);
        }

        _ringWriteFrame = (_ringWriteFrame + count) % RingBufferCapacityFrames;
        _ringAvailableFrames += count;
    }

    private int ReadFromRingBuffer(float[] destination, int maxFrames)
    {
        int copied;
        bool shouldWakeDecoder;

        lock (_ringLock)
        {
            var ring = _pcmRingBuffer;
            copied = ring is null ? 0 : Math.Min(maxFrames, _ringAvailableFrames);
            if (copied > 0 && ring is not null)
            {
                int firstPart = Math.Min(copied, RingBufferCapacityFrames - _ringReadFrame);
                Array.Copy(ring, _ringReadFrame * 2, destination, 0, firstPart * 2);

                int secondPart = copied - firstPart;
                if (secondPart > 0)
                {
                    Array.Copy(ring, 0, destination, firstPart * 2, secondPart * 2);
                }

                _ringReadFrame = (_ringReadFrame + copied) % RingBufferCapacityFrames;
                _ringAvailableFrames -= copied;
            }

            shouldWakeDecoder = !_decoderReachedEof && _ringAvailableFrames < DecoderRefillThresholdFrames;
        }

        if (shouldWakeDecoder)
        {
            _decoderWakeSignal.Set();
        }

        return copied;
    }

    private void RecalculateBiquadCoefficientsLocked()
    {
        int fs = Math.Max(8000, _outputSampleRate);

        // Stage 1: Nightcore Low-Shelf Filter (Sub-bass punch: 90 Hz, +6.0 dB, Q = 0.7)
        _nightcoreLowShelfFilter.ConfigureLowShelf(
            sampleRate: fs,
            frequencyHz: Specification.LowShelfFrequencyHz,
            gainDb: _currentNightcoreLowShelfDb,
            q: Specification.LowShelfQ);

        // Stage 2: Nightcore Peaking Filter (Vocal clarity & high brightness: 3500 Hz, +2.0 dB, Q = 1.0)
        _nightcorePeakingFilter.ConfigurePeakingEq(
            sampleRate: fs,
            frequencyHz: Specification.PeakingFrequencyHz,
            gainDb: _currentNightcorePeakingDb,
            q: Specification.PeakingQ);

        // User Bass (Low-Shelf 100 Hz) & Treble (High-Shelf 10 kHz)
        _userBassShelfFilter.ConfigureLowShelf(
            sampleRate: fs,
            frequencyHz: 100.0,
            gainDb: _currentBassBoostDb,
            q: 0.707);

        _userTrebleShelfFilter.ConfigureHighShelf(
            sampleRate: fs,
            frequencyHz: 10000.0,
            gainDb: _currentTrebleBoostDb,
            q: 0.707);

        // User 5-Band Parametric Equalizer (60 Hz, 230 Hz, 910 Hz, 3.6 kHz, 14 kHz)
        for (int i = 0; i < _userParametricBands.Length; i++)
        {
            _userParametricBands[i].ConfigurePeakingEq(
                sampleRate: fs,
                frequencyHz: AudioDspState.DefaultFrequenciesHz[i],
                gainDb: _currentUserBandDb[i],
                q: 1.0);
        }
    }

    private void SyncMediaPlayerFallbackRateLocked()
    {
        if (_attachedMediaPlayer is not null && !_isUsingAudioGraphForCurrentTrack)
        {
            try
            {
                _attachedMediaPlayer.PlaybackSession.PlaybackRate = Math.Clamp(_targetPlaybackRate, 0.5, 2.0);
            }
            catch
            {
                // Ignore if MediaPlayer has no active session
            }
        }
    }

    private void FlushDspBuffersLocked()
    {
        _resampleWindowFrames = 0;
        _resampleReadPos = 0.0;
        _wasUsingIndependentTimeScale = false;
        _wsolaTimeScaler?.Reset();
        _nightcoreLowShelfFilter.Reset();
        _nightcorePeakingFilter.Reset();
        _userBassShelfFilter.Reset();
        _userTrebleShelfFilter.Reset();
        for (int i = 0; i < _userParametricBands.Length; i++)
        {
            _userParametricBands[i].Reset();
        }
        _dynamicsCompressor.Reset();
    }

    private static float HermiteInterpolate(float y0, float y1, float y2, float y3, float mu)
    {
        float c0 = y1;
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - (2.5f * y1) + (2.0f * y2) - (0.5f * y3);
        float c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));
        return ((c3 * mu + c2) * mu + c1) * mu + c0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ResetAndCleanupCurrentTrackNodes();
        _decoderWakeSignal.Dispose();

        lock (_graphLock)
        {
            lock (_frameEnqueueLock)
            {
                try
                {
                    if (_frameInputNode is not null)
                    {
                        _frameInputNode.QuantumStarted -= OnFrameInputQuantumStarted;
                        _frameInputNode.AudioFrameCompleted -= OnAudioFrameCompleted;
                        _frameInputNode.Stop();
                        _frameInputNode.Dispose();
                    }

                    if (_audioGraph is not null)
                    {
                        _audioGraph.UnrecoverableErrorOccurred -= OnAudioGraphUnrecoverableError;
                        _audioGraph.Stop();
                    }

                    _deviceOutputNode?.Dispose();
                    _audioGraph?.Dispose();
                }
                catch
                {
                    // Ignore shutdown disposal race
                }
                finally
                {
                    _audioGraphInitialized = false;
                    _frameInputNode = null;
                    _deviceOutputNode = null;
                    _audioGraph = null;
                }
            }
        }

        _graphInitLock.Dispose();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    /// <summary>
    /// Stereo Direct Form II Transposed Biquad IIR Filter implementing Robert Bristow-Johnson (RBJ)
    /// Audio EQ Cookbook Low-Shelf, High-Shelf, and Peaking EQ transfer functions.
    /// </summary>
    private sealed class BiquadFilterPair
    {
        private double _b0 = 1.0;
        private double _b1;
        private double _b2;
        private double _a1;
        private double _a2;

        // Stereo delay states (Direct Form II Transposed)
        private double _z1L;
        private double _z2L;
        private double _z1R;
        private double _z2R;
        private bool _isBypassed = true;

        public void Reset()
        {
            _z1L = 0.0;
            _z2L = 0.0;
            _z1R = 0.0;
            _z2R = 0.0;
        }

        public void ConfigureLowShelf(int sampleRate, double frequencyHz, double gainDb, double q)
        {
            if (Math.Abs(gainDb) < 0.01)
            {
                SetBypass();
                return;
            }

            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * Math.Clamp(frequencyHz, 20.0, sampleRate * 0.45) / sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * Math.Max(0.1, q));
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double b0 = a * ((a + 1.0) - ((a - 1.0) * cosW0) + twoSqrtAAlpha);
            double b1 = 2.0 * a * ((a - 1.0) - ((a + 1.0) * cosW0));
            double b2 = a * ((a + 1.0) - ((a - 1.0) * cosW0) - twoSqrtAAlpha);
            double a0 = (a + 1.0) + ((a - 1.0) * cosW0) + twoSqrtAAlpha;
            double a1 = -2.0 * ((a - 1.0) + ((a + 1.0) * cosW0));
            double a2 = (a + 1.0) + ((a - 1.0) * cosW0) - twoSqrtAAlpha;

            SetNormalizedCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void ConfigureHighShelf(int sampleRate, double frequencyHz, double gainDb, double q)
        {
            if (Math.Abs(gainDb) < 0.01)
            {
                SetBypass();
                return;
            }

            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * Math.Clamp(frequencyHz, 200.0, sampleRate * 0.45) / sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * Math.Max(0.1, q));
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

            double b0 = a * ((a + 1.0) + ((a - 1.0) * cosW0) + twoSqrtAAlpha);
            double b1 = -2.0 * a * ((a - 1.0) + ((a + 1.0) * cosW0));
            double b2 = a * ((a + 1.0) + ((a - 1.0) * cosW0) - twoSqrtAAlpha);
            double a0 = (a + 1.0) - ((a - 1.0) * cosW0) + twoSqrtAAlpha;
            double a1 = 2.0 * ((a - 1.0) - ((a + 1.0) * cosW0));
            double a2 = (a + 1.0) - ((a - 1.0) * cosW0) - twoSqrtAAlpha;

            SetNormalizedCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void ConfigurePeakingEq(int sampleRate, double frequencyHz, double gainDb, double q)
        {
            if (Math.Abs(gainDb) < 0.01)
            {
                SetBypass();
                return;
            }

            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * Math.Clamp(frequencyHz, 20.0, sampleRate * 0.45) / sampleRate;
            double cosW0 = Math.Cos(w0);
            double sinW0 = Math.Sin(w0);
            double alpha = sinW0 / (2.0 * Math.Max(0.1, q));

            double b0 = 1.0 + (alpha * a);
            double b1 = -2.0 * cosW0;
            double b2 = 1.0 - (alpha * a);
            double a0 = 1.0 + (alpha / a);
            double a1 = -2.0 * cosW0;
            double a2 = 1.0 - (alpha / a);

            SetNormalizedCoefficients(b0, b1, b2, a0, a1, a2);
        }

        public void ProcessStereo(ref float left, ref float right)
        {
            if (_isBypassed)
            {
                return;
            }

            double inL = double.IsFinite(left) ? left : 0.0;
            double outL = (_b0 * inL) + _z1L;
            if (!double.IsFinite(outL))
            {
                _z1L = 0.0;
                _z2L = 0.0;
                left = 0f;
            }
            else
            {
                _z1L = (_b1 * inL) - (_a1 * outL) + _z2L;
                _z2L = (_b2 * inL) - (_a2 * outL);
                left = (float)outL;
            }

            double inR = double.IsFinite(right) ? right : 0.0;
            double outR = (_b0 * inR) + _z1R;
            if (!double.IsFinite(outR))
            {
                _z1R = 0.0;
                _z2R = 0.0;
                right = 0f;
            }
            else
            {
                _z1R = (_b1 * inR) - (_a1 * outR) + _z2R;
                _z2R = (_b2 * inR) - (_a2 * outR);
                right = (float)outR;
            }
        }

        private void SetBypass()
        {
            _b0 = 1.0;
            _b1 = 0.0;
            _b2 = 0.0;
            _a1 = 0.0;
            _a2 = 0.0;
            _isBypassed = true;
        }

        private void SetNormalizedCoefficients(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            double invA0 = 1.0 / a0;
            _b0 = b0 * invA0;
            _b1 = b1 * invA0;
            _b2 = b2 * invA0;
            _a1 = a1 * invA0;
            _a2 = a2 * invA0;
            _isBypassed = false;
        }
    }

    /// <summary>
    /// Stage 3 Feed-Forward Soft-Knee Dynamics Compressor / Limiter
    /// (Threshold: -6.0 dB, Ratio: 4:1, Attack: 0.003s / 3ms, Release: 0.150s / 150ms)
    /// protecting the output stage from clipping when the +6 dB Nightcore Low-Shelf boost is engaged.
    /// </summary>
    private sealed class SoftKneeDynamicsCompressor
    {
        private const double SoftKneeWidthDb = 3.0;

        private double _thresholdDb = -6.0;
        private double _ratio = 4.0;
        private double _attackCoeff;
        private double _releaseCoeff;
        private double _envelopeLinear;

        public void Configure(
            int sampleRate,
            double thresholdDb,
            double ratio,
            double attackSeconds,
            double releaseSeconds)
        {
            int fs = Math.Max(8000, sampleRate);
            _thresholdDb = thresholdDb;
            _ratio = Math.Max(1.0, ratio);
            _attackCoeff = Math.Exp(-1.0 / (Math.Max(0.0005, attackSeconds) * fs));
            _releaseCoeff = Math.Exp(-1.0 / (Math.Max(0.005, releaseSeconds) * fs));
        }

        public void Reset()
        {
            _envelopeLinear = 0.0;
        }

        public void ProcessStereo(ref float left, ref float right)
        {
            double peak = Math.Max(Math.Abs(left), Math.Abs(right));
            double coeff = peak > _envelopeLinear ? _attackCoeff : _releaseCoeff;
            _envelopeLinear = (coeff * _envelopeLinear) + ((1.0 - coeff) * peak);

            double gainLinear = 1.0;
            if (_envelopeLinear > 1e-5)
            {
                double envDb = 20.0 * Math.Log10(_envelopeLinear);
                double halfKnee = SoftKneeWidthDb * 0.5;
                double diffDb = envDb - _thresholdDb;
                double gainReductionDb = 0.0;

                if (diffDb >= halfKnee)
                {
                    // Full 4:1 compression above soft knee
                    double compressedOverDb = diffDb / _ratio;
                    gainReductionDb = compressedOverDb - diffDb;
                }
                else if (diffDb > -halfKnee)
                {
                    // Smooth quadratic soft-knee transition
                    double kneeX = diffDb + halfKnee;
                    gainReductionDb = ((1.0 / _ratio) - 1.0) * (kneeX * kneeX) / (2.0 * SoftKneeWidthDb);
                }

                if (gainReductionDb < -1e-4)
                {
                    gainLinear = Math.Pow(10.0, gainReductionDb / 20.0);
                }
            }

            left = SoftClip((float)(left * gainLinear));
            right = SoftClip((float)(right * gainLinear));
        }

        private static float SoftClip(float sample)
        {
            if (sample > 0.98f)
            {
                return 0.98f + (0.02f * MathF.Tanh((sample - 0.98f) * 50.0f));
            }
            if (sample < -0.98f)
            {
                return -0.98f + (0.02f * MathF.Tanh((sample + 0.98f) * 50.0f));
            }
            return sample;
        }
    }

    /// <summary>
    /// Real-time stereo WSOLA (Waveform Similarity Overlap-Add) time-scaler enabling independent
    /// manual Playback Speed (with pitch preservation) and independent manual Pitch Shift
    /// when Nightcore / Coupled Resampling mode is OFF.
    /// </summary>
    private sealed class WsolaStereoTimeScaler
    {
        private int _sampleRate = DefaultSampleRate;
        private int _windowFrames = 1024;
        private int _overlapFrames = 512;
        private int _searchRadiusFrames = 256;

        private float[] _inputBuf = new float[32768 * 2];
        private int _inputFrames;
        private double _inputPosition;

        private float[] _outputBuf = new float[32768 * 2];
        private int _outputReadFrame;
        private int _outputWriteFrame;

        private float[] _prevTail = new float[1024 * 2];
        private bool _hasPrevTail;

        public int AvailableOutputFrames => Math.Max(0, _outputWriteFrame - _outputReadFrame);

        public void Configure(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            // ~22ms Hann overlap-add window, 50% overlap, ~6ms cross-correlation search radius
            _windowFrames = Math.Clamp((int)(_sampleRate * 0.022), 512, 2048);
            if ((_windowFrames & 1) != 0) _windowFrames++;
            _overlapFrames = _windowFrames / 2;
            _searchRadiusFrames = Math.Clamp((int)(_sampleRate * 0.006), 96, 384);
            _prevTail = new float[_overlapFrames * 2];
            Reset();
        }

        public void Reset()
        {
            _inputFrames = 0;
            _inputPosition = 0.0;
            _outputReadFrame = 0;
            _outputWriteFrame = 0;
            _hasPrevTail = false;
            Array.Clear(_prevTail);
        }

        public void WriteInput(ReadOnlySpan<float> stereoInput, double tempoRatio)
        {
            int frames = stereoInput.Length / 2;
            if (frames <= 0)
            {
                return;
            }

            EnsureInputCapacity(_inputFrames + frames);
            stereoInput.CopyTo(_inputBuf.AsSpan(_inputFrames * 2, frames * 2));
            _inputFrames += frames;

            ProcessAvailableFrames(Math.Clamp(tempoRatio, 0.25, 4.0));
        }

        public void Flush(double tempoRatio)
        {
            ProcessAvailableFrames(Math.Clamp(tempoRatio, 0.25, 4.0));
        }

        public int ReadOutput(Span<float> destinationStereo)
        {
            int maxFrames = destinationStereo.Length / 2;
            int avail = AvailableOutputFrames;
            int take = Math.Min(maxFrames, avail);
            if (take <= 0)
            {
                return 0;
            }

            _outputBuf.AsSpan(_outputReadFrame * 2, take * 2).CopyTo(destinationStereo);
            _outputReadFrame += take;

            if (_outputReadFrame >= _outputWriteFrame)
            {
                _outputReadFrame = 0;
                _outputWriteFrame = 0;
            }
            else if (_outputReadFrame >= 4096)
            {
                int rem = _outputWriteFrame - _outputReadFrame;
                Array.Copy(_outputBuf, _outputReadFrame * 2, _outputBuf, 0, rem * 2);
                _outputReadFrame = 0;
                _outputWriteFrame = rem;
            }

            return take;
        }

        private void ProcessAvailableFrames(double tempoRatio)
        {
            int hopOut = _overlapFrames;
            double hopIn = hopOut * tempoRatio;
            int neededAhead = _windowFrames + _searchRadiusFrames + 2;

            while ((int)_inputPosition + neededAhead < _inputFrames)
            {
                int nominalStart = (int)_inputPosition;
                int bestStart = nominalStart;

                if (_hasPrevTail)
                {
                    // Find best waveform-similarity alignment within [-searchRadius, +searchRadius]
                    int minStart = Math.Max(0, nominalStart - _searchRadiusFrames);
                    int maxStart = Math.Min(_inputFrames - _windowFrames, nominalStart + _searchRadiusFrames);
                    double bestCorr = double.NegativeInfinity;

                    // Stride by 4 samples for fast low-CPU correlation search
                    for (int cand = minStart; cand <= maxStart; cand += 2)
                    {
                        double corr = 0.0;
                        double energy = 1e-6;
                        for (int k = 0; k < _overlapFrames; k += 4)
                        {
                            float a = _prevTail[k * 2] + _prevTail[(k * 2) + 1];
                            float b = _inputBuf[(cand + k) * 2] + _inputBuf[((cand + k) * 2) + 1];
                            corr += a * b;
                            energy += b * b;
                        }

                        double score = corr / Math.Sqrt(energy);
                        if (score > bestCorr)
                        {
                            bestCorr = score;
                            bestStart = cand;
                        }
                    }
                }

                EnsureOutputCapacity(_outputWriteFrame + hopOut);

                // Overlap-add first half with previous tail using raised-cosine cross-fade
                if (_hasPrevTail)
                {
                    for (int k = 0; k < _overlapFrames; k++)
                    {
                        float fadeIn = (float)k / _overlapFrames;
                        float fadeOut = 1.0f - fadeIn;
                        int outIdx = (_outputWriteFrame + k) * 2;
                        int inIdx = (bestStart + k) * 2;

                        _outputBuf[outIdx] = (_prevTail[k * 2] * fadeOut) + (_inputBuf[inIdx] * fadeIn);
                        _outputBuf[outIdx + 1] = (_prevTail[(k * 2) + 1] * fadeOut) + (_inputBuf[inIdx + 1] * fadeIn);
                    }
                }
                else
                {
                    Array.Copy(_inputBuf, bestStart * 2, _outputBuf, _outputWriteFrame * 2, _overlapFrames * 2);
                }

                _outputWriteFrame += hopOut;

                // Save second half as next overlap tail
                Array.Copy(_inputBuf, (bestStart + _overlapFrames) * 2, _prevTail, 0, _overlapFrames * 2);
                _hasPrevTail = true;

                _inputPosition += hopIn;
            }

            // Compact consumed input frames
            int drop = Math.Max(0, (int)_inputPosition - _searchRadiusFrames - 4);
            if (drop >= 4096)
            {
                int rem = Math.Max(0, _inputFrames - drop);
                if (rem > 0)
                {
                    Array.Copy(_inputBuf, drop * 2, _inputBuf, 0, rem * 2);
                }
                _inputFrames = rem;
                _inputPosition -= drop;
            }
        }

        private void EnsureInputCapacity(int requiredFrames)
        {
            int requiredFloats = requiredFrames * 2;
            if (_inputBuf.Length < requiredFloats)
            {
                Array.Resize(ref _inputBuf, Math.Max(requiredFloats, _inputBuf.Length * 2));
            }
        }

        private void EnsureOutputCapacity(int requiredFrames)
        {
            int requiredFloats = requiredFrames * 2;
            if (_outputBuf.Length < requiredFloats)
            {
                Array.Resize(ref _outputBuf, Math.Max(requiredFloats, _outputBuf.Length * 2));
            }
        }
    }
}
