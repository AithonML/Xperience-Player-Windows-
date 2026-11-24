using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services.Audio;

/// <summary>
/// High-level modular Audio DSP &amp; 1-Click Nightcore controller separated from UI logic.
/// Coordinates state transitions, persistence via <see cref="IStorageService"/>, and real-time
/// 150ms exponential parameter ramping on <see cref="NightcoreDspEngine"/>.
/// </summary>
public sealed class AudioDspController : IAudioDspController
{
    private readonly NightcoreDspEngine _dspEngine;
    private readonly IStorageService _storageService;
    private readonly ILogger<AudioDspController> _logger;
    private readonly object _stateLock = new();
    private readonly object _saveLock = new();
    private AudioDspState _currentState = new();
    private CancellationTokenSource? _saveDebounceCts;
    private int _disposed;

    /// <inheritdoc />
    public event EventHandler<AudioDspState>? StateChanged;

    /// <inheritdoc />
    public AudioDspState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <inheritdoc />
    public NightcoreDspChainSpec NightcoreSpec => _dspEngine.Specification;

    public AudioDspController(
        NightcoreDspEngine dspEngine,
        IStorageService storageService,
        ILogger<AudioDspController> logger)
    {
        _dspEngine = dspEngine;
        _storageService = storageService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            var saved = await _storageService.LoadAsync<AudioDspState>(
                    AudioDspState.StorageKey,
                    defaultValue: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (saved is not null)
            {
                var normalizedBands = new double[5];
                if (saved.BandGainsDb is not null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        normalizedBands[i] = i < saved.BandGainsDb.Count
                            ? Math.Clamp(saved.BandGainsDb[i], -12.0, 12.0)
                            : 0.0;
                    }
                }

                var restored = saved with
                {
                    NightcoreRate = Math.Clamp(
                        saved.NightcoreRate,
                        NightcoreDspChainSpec.MinNightcoreRate,
                        NightcoreDspChainSpec.MaxNightcoreRate),
                    BandGainsDb = normalizedBands,
                    BassBoostDb = Math.Clamp(saved.BassBoostDb, -12.0, 12.0),
                    TrebleBoostDb = Math.Clamp(saved.TrebleBoostDb, -12.0, 12.0),
                    ManualPlaybackSpeed = Math.Clamp(saved.ManualPlaybackSpeed, 0.50, 2.00),
                    ManualPitchSemitones = Math.Clamp(saved.ManualPitchSemitones, -12.0, 12.0)
                };

                lock (_stateLock)
                {
                    _currentState = restored;
                }

                _dspEngine.TransitionToState(restored, immediate: true);
                StateChanged?.Invoke(this, restored);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Using default AudioDspState on startup.");
        }
    }

    /// <inheritdoc />
    public bool ToggleNightcoreMode()
    {
        bool nextState = !CurrentState.IsNightcoreActive;
        SetNightcoreMode(nextState);
        return nextState;
    }

    /// <inheritdoc />
    public void SetNightcoreMode(bool enabled)
    {
        UpdateState(state => state with
        {
            IsNightcoreActive = enabled
        });

        _logger.LogInformation(
            "Nightcore 1-Click DSP Mode toggled {State} (Rate={Rate:0.00}x, PreservesPitch={PreservesPitch}, LowShelf=90Hz +6dB Q=0.7, Peaking=3.5kHz +2dB Q=1.0, Compressor=-6dB 4:1 3ms/150ms, Ramp=150ms).",
            enabled ? "ON" : "OFF",
            CurrentState.EffectivePlaybackSpeed,
            CurrentState.PreservesPitch);
    }

    /// <inheritdoc />
    public void SetNightcoreRate(double rate)
    {
        double clamped = Math.Round(
            Math.Clamp(rate, NightcoreDspChainSpec.MinNightcoreRate, NightcoreDspChainSpec.MaxNightcoreRate),
            2);

        UpdateState(state => state with
        {
            NightcoreRate = clamped
        });
    }

    /// <inheritdoc />
    public void SetEqualizerEnabled(bool enabled)
    {
        UpdateState(state => state with
        {
            IsEqualizerEnabled = enabled
        });
    }

    /// <inheritdoc />
    public void ApplyEqualizerPreset(EqualizerPresetId preset)
    {
        double[] gains = AudioDspState.GetPresetBandGains(preset);
        double bass = preset switch
        {
            EqualizerPresetId.BassBoost => 4.5,
            EqualizerPresetId.Electronic => 3.5,
            EqualizerPresetId.Nightcore => 5.0,
            EqualizerPresetId.Flat => 0.0,
            _ => CurrentState.BassBoostDb
        };
        double treble = preset switch
        {
            EqualizerPresetId.VocalClarity => 2.5,
            EqualizerPresetId.Electronic => 3.0,
            EqualizerPresetId.Nightcore => 2.0,
            EqualizerPresetId.Flat => 0.0,
            _ => CurrentState.TrebleBoostDb
        };

        UpdateState(state => state with
        {
            IsEqualizerEnabled = true,
            ActivePreset = preset,
            BandGainsDb = gains,
            BassBoostDb = bass,
            TrebleBoostDb = treble
        });
    }

    /// <inheritdoc />
    public void SetEqualizerBandGain(int bandIndex, double gainDb)
    {
        if (bandIndex is < 0 or >= 5)
        {
            return;
        }

        double clamped = Math.Round(Math.Clamp(gainDb, -12.0, 12.0), 1);

        UpdateState(state =>
        {
            var nextBands = new double[5];
            for (int i = 0; i < 5; i++)
            {
                nextBands[i] = i < state.BandGainsDb.Count ? state.BandGainsDb[i] : 0.0;
            }
            nextBands[bandIndex] = clamped;

            return state with
            {
                IsEqualizerEnabled = true,
                ActivePreset = EqualizerPresetId.Custom,
                BandGainsDb = nextBands
            };
        });
    }

    /// <inheritdoc />
    public void SetBassBoost(double gainDb)
    {
        double clamped = Math.Round(Math.Clamp(gainDb, -12.0, 12.0), 1);
        UpdateState(state => state with
        {
            IsEqualizerEnabled = true,
            BassBoostDb = clamped
        });
    }

    /// <inheritdoc />
    public void SetTrebleBoost(double gainDb)
    {
        double clamped = Math.Round(Math.Clamp(gainDb, -12.0, 12.0), 1);
        UpdateState(state => state with
        {
            IsEqualizerEnabled = true,
            TrebleBoostDb = clamped
        });
    }

    /// <inheritdoc />
    public void SetManualPlaybackSpeed(double speed)
    {
        double clamped = Math.Round(Math.Clamp(speed, 0.50, 2.00), 2);
        UpdateState(state =>
        {
            // If user manually adjusts speed outside [1.20, 1.25] while Nightcore is on, switch to manual speed control
            bool keepNightcore = state.IsNightcoreActive &&
                                 clamped >= NightcoreDspChainSpec.MinNightcoreRate &&
                                 clamped <= NightcoreDspChainSpec.MaxNightcoreRate;

            return state with
            {
                IsNightcoreActive = keepNightcore,
                NightcoreRate = keepNightcore ? clamped : state.NightcoreRate,
                ManualPlaybackSpeed = clamped
            };
        });
    }

    /// <inheritdoc />
    public void SetManualPitchSemitones(double semitones)
    {
        double clamped = Math.Round(Math.Clamp(semitones, -12.0, 12.0), 1);
        UpdateState(state => state with
        {
            IsNightcoreActive = false,
            ManualPitchSemitones = clamped
        });
    }

    /// <inheritdoc />
    public void SetCouplePitchToSpeed(bool coupled)
    {
        UpdateState(state => state with
        {
            CouplePitchToSpeed = coupled
        });
    }

    /// <inheritdoc />
    public void ResetAllToDefaults()
    {
        var defaults = new AudioDspState
        {
            IsNightcoreActive = false,
            NightcoreRate = NightcoreDspChainSpec.DefaultNightcoreRate,
            IsEqualizerEnabled = true,
            ActivePreset = EqualizerPresetId.Flat,
            BandGainsDb = [0.0, 0.0, 0.0, 0.0, 0.0],
            BassBoostDb = 0.0,
            TrebleBoostDb = 0.0,
            ManualPlaybackSpeed = 1.0,
            ManualPitchSemitones = 0.0,
            CouplePitchToSpeed = false
        };

        UpdateState(_ => defaults);
    }

    private void UpdateState(Func<AudioDspState, AudioDspState> mutator)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        AudioDspState updated;
        lock (_stateLock)
        {
            updated = mutator(_currentState);
            _currentState = updated;
        }

        _dspEngine.TransitionToState(updated, immediate: false);
        StateChanged?.Invoke(this, updated);
        SchedulePersistState(updated);
    }

    private void SchedulePersistState(AudioDspState snapshot)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_saveLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cts.Dispose();
                return;
            }

            previous = _saveDebounceCts;
            _saveDebounceCts = cts;
        }

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch
            {
                // Ignore transient CTS cancellation race
            }
        }

        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    await _storageService.SaveAsync(AudioDspState.StorageKey, snapshot, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during rapid slider dragging
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist AudioDspState.");
            }
            finally
            {
                lock (_saveLock)
                {
                    if (ReferenceEquals(_saveDebounceCts, cts))
                    {
                        _saveDebounceCts = null;
                    }
                }

                try
                {
                    cts.Dispose();
                }
                catch
                {
                    // Ignore shutdown disposal race
                }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? pending;
        lock (_saveLock)
        {
            pending = _saveDebounceCts;
            _saveDebounceCts = null;
        }

        if (pending is not null)
        {
            try
            {
                pending.Cancel();
            }
            catch
            {
                // Ignore shutdown errors
            }
        }
    }
}
