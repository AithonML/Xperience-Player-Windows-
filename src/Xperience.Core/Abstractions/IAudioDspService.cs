using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Modular Audio DSP &amp; 1-Click Nightcore controller separated from UI presentation logic.
/// Coordinates real-time coupled speed/pitch resampling, 3-stage Nightcore Biquad/Compressor filtering,
/// 5-band parametric equalization, Graves/Agudos tone controls, and 150ms anti-click exponential ramps.
/// </summary>
public interface IAudioDspController : IDisposable
{
    /// <summary>
    /// Raised whenever any DSP parameter (Nightcore mode, EQ band, Bass/Treble, Speed, or Pitch) changes.
    /// </summary>
    event EventHandler<AudioDspState>? StateChanged;

    /// <summary>
    /// Gets the current immutable DSP state snapshot.
    /// </summary>
    AudioDspState CurrentState { get; }

    /// <summary>
    /// Gets the canonical Nightcore 3-stage filter &amp; resampling specification.
    /// </summary>
    NightcoreDspChainSpec NightcoreSpec { get; }

    /// <summary>
    /// Initializes and restores persisted user DSP settings from storage.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles the 1-Click "Nightcore" real-time DSP mode ON or OFF with a 150ms anti-click exponential ramp,
    /// without cutting playback or reloading the audio stream.
    /// </summary>
    /// <returns>The updated Nightcore active state (<c>true</c> if active).</returns>
    bool ToggleNightcoreMode();

    /// <summary>
    /// Explicitly sets the 1-Click "Nightcore" real-time DSP mode state with a 150ms anti-click exponential ramp.
    /// </summary>
    /// <param name="enabled">Whether Nightcore mode should be active.</param>
    void SetNightcoreMode(bool enabled);

    /// <summary>
    /// Sets the Nightcore playback rate within [1.20x, 1.25x] (default 1.22x).
    /// </summary>
    /// <param name="rate">Coupled speed and pitch resampling rate.</param>
    void SetNightcoreRate(double rate);

    /// <summary>
    /// Enables or disables the multi-band equalizer stage.
    /// </summary>
    void SetEqualizerEnabled(bool enabled);

    /// <summary>
    /// Applies a built-in equalizer preset curve smoothly over 150ms.
    /// </summary>
    void ApplyEqualizerPreset(EqualizerPresetId preset);

    /// <summary>
    /// Adjusts the gain (in dB, -12 to +12 dB) of a specific equalizer band (0..4) smoothly over 150ms.
    /// </summary>
    void SetEqualizerBandGain(int bandIndex, double gainDb);

    /// <summary>
    /// Adjusts the low-frequency tone control (Graves / Bass boost, -12 to +12 dB).
    /// </summary>
    void SetBassBoost(double gainDb);

    /// <summary>
    /// Adjusts the high-frequency tone control (Agudos / Treble boost, -12 to +12 dB).
    /// </summary>
    void SetTrebleBoost(double gainDb);

    /// <summary>
    /// Adjusts the manual playback speed (0.50x to 2.00x). If Nightcore is active, exits Nightcore or updates manual speed.
    /// </summary>
    void SetManualPlaybackSpeed(double speed);

    /// <summary>
    /// Adjusts the manual pitch / tone shift in semitones (-12.0 to +12.0 semitones).
    /// </summary>
    void SetManualPitchSemitones(double semitones);

    /// <summary>
    /// Sets whether manual playback speed changes also shift pitch proportionally (resampling mode).
    /// </summary>
    void SetCouplePitchToSpeed(bool coupled);

    /// <summary>
    /// Resets all equalizer bands, tone controls, speed, pitch, and Nightcore state back to flat 1.0x defaults.
    /// </summary>
    void ResetAllToDefaults();
}

/// <summary>
/// Modular Music Sleep Timer ("Temporizador de música") service providing visual countdown telemetry
/// and smooth exponential volume fade-out when the timer expires.
/// </summary>
public interface ISleepTimerService : IDisposable
{
    /// <summary>
    /// Raised every second while the timer is active, and when the timer starts, expires, or is cancelled.
    /// </summary>
    event EventHandler<SleepTimerState>? StateChanged;

    /// <summary>
    /// Gets the current sleep timer telemetry snapshot.
    /// </summary>
    SleepTimerState CurrentState { get; }

    /// <summary>
    /// Starts the sleep timer using one of the standard preset modes (5m, 15m, 30m, 45m, 60m, EndOfTrack).
    /// </summary>
    void StartPreset(SleepTimerPresetMode mode);

    /// <summary>
    /// Starts the sleep timer for a custom duration in minutes (1 to 360 minutes).
    /// </summary>
    void StartCustomMinutes(int minutes);

    /// <summary>
    /// Adds additional minutes (e.g. +5m) to an active countdown timer.
    /// </summary>
    void AddMinutes(int extraMinutes);

    /// <summary>
    /// Cancels the active sleep timer and restores the user's normal playback volume if fading out.
    /// </summary>
    void Cancel();
}
