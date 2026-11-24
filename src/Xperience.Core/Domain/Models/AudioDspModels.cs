namespace Xperience.Core.Domain.Models;

/// <summary>
/// Built-in and custom equalizer presets available in the Fullscreen Audio DSP menu.
/// </summary>
public enum EqualizerPresetId
{
    Flat = 0,
    BassBoost = 1,
    VocalClarity = 2,
    Electronic = 3,
    Rock = 4,
    Pop = 5,
    Acoustic = 6,
    Nightcore = 7,
    Custom = 8
}

/// <summary>
/// Sleep timer duration mode for automatic playback fade-out and pause.
/// </summary>
public enum SleepTimerPresetMode
{
    Off = 0,
    Minutes5 = 5,
    Minutes15 = 15,
    Minutes30 = 30,
    Minutes45 = 45,
    Minutes60 = 60,
    EndOfTrack = 999,
    Custom = 1000
}

/// <summary>
/// Immutable specification for a single parametric equalizer band.
/// </summary>
public sealed record EqualizerBandSpec(
    int Index,
    double FrequencyHz,
    string Label,
    double GainDb,
    double QFactor = 1.0);

/// <summary>
/// Canonical real-time DSP specification for the 1-Click "Nightcore" mode:
/// coupled speed and pitch resampling (1.22x, preservesPitch = false),
/// 3-stage series Biquad filter &amp; dynamics chain (Low-Shelf 90Hz +6dB Q=0.7,
/// Peaking 3.5kHz +2dB Q=1.0, Dynamics Compressor -6dB 4:1 3ms/150ms),
/// and 150ms exponential anti-click ramp.
/// </summary>
public sealed record NightcoreDspChainSpec
{
    public const double DefaultNightcoreRate = 1.22;
    public const double MinNightcoreRate = 1.20;
    public const double MaxNightcoreRate = 1.25;

    // 1. Coupled Resampling (Speed + Pitch without time-stretch artifacts)
    public double PlaybackRate { get; init; } = DefaultNightcoreRate;
    public bool PreservesPitch { get; init; } = false;

    // 2A. Stage 1: Low-Shelf Biquad Filter (Sub-bass punch)
    public double LowShelfFrequencyHz { get; init; } = 90.0;
    public double LowShelfGainDb { get; init; } = 6.0;
    public double LowShelfQ { get; init; } = 0.7;

    // 2B. Stage 2: Peaking Biquad Filter (Vocal clarity & high brightness)
    public double PeakingFrequencyHz { get; init; } = 3500.0;
    public double PeakingGainDb { get; init; } = 2.0;
    public double PeakingQ { get; init; } = 1.0;

    // 2C. Stage 3: Soft-Knee Dynamics Compressor / Limiter
    public double CompressorThresholdDb { get; init; } = -6.0;
    public double CompressorRatio { get; init; } = 4.0;
    public double CompressorAttackSeconds { get; init; } = 0.003;
    public double CompressorReleaseSeconds { get; init; } = 0.150;

    // 3. Anti-Click Exponential Ramp Duration (150ms)
    public double RampDurationSeconds { get; init; } = 0.150;
}

/// <summary>
/// Complete snapshot of the Audio DSP Engine state (Nightcore mode, Equalizer bands,
/// Graves/Agudos tone controls, Playback Speed, and Pitch Shift).
/// </summary>
public sealed record AudioDspState
{
    public const string StorageKey = "AudioDspSettings_v1";

    public static readonly double[] DefaultFrequenciesHz = [60.0, 230.0, 910.0, 3600.0, 14000.0];
    public static readonly string[] DefaultBandLabels = ["60 Hz", "230 Hz", "910 Hz", "3.6 kHz", "14 kHz"];

    public bool IsNightcoreActive { get; init; }
    public double NightcoreRate { get; init; } = NightcoreDspChainSpec.DefaultNightcoreRate;
    public bool IsEqualizerEnabled { get; init; } = true;
    public EqualizerPresetId ActivePreset { get; init; } = EqualizerPresetId.Flat;
    public IReadOnlyList<double> BandGainsDb { get; init; } = [0.0, 0.0, 0.0, 0.0, 0.0];

    /// <summary>
    /// Low-frequency tone control (Graves / Bass boost in dB, -12 to +12 dB).
    /// </summary>
    public double BassBoostDb { get; init; }

    /// <summary>
    /// High-frequency tone control (Agudos / Treble boost in dB, -12 to +12 dB).
    /// </summary>
    public double TrebleBoostDb { get; init; }

    /// <summary>
    /// User-selected playback speed outside Nightcore mode (0.50x to 2.00x).
    /// </summary>
    public double ManualPlaybackSpeed { get; init; } = 1.0;

    /// <summary>
    /// User-selected pitch shift in semitones outside Nightcore mode (-12.0 to +12.0 st).
    /// </summary>
    public double ManualPitchSemitones { get; init; } = 0.0;

    /// <summary>
    /// Whether manual speed changes also couple pitch proportionally (vinyl resampling style).
    /// Always forced to <c>true</c> when <see cref="IsNightcoreActive"/> is active.
    /// </summary>
    public bool CouplePitchToSpeed { get; init; }

    /// <summary>
    /// Gets the effective playback rate currently applied to the audio engine.
    /// </summary>
    public double EffectivePlaybackSpeed =>
        IsNightcoreActive
            ? Math.Clamp(NightcoreRate, NightcoreDspChainSpec.MinNightcoreRate, NightcoreDspChainSpec.MaxNightcoreRate)
            : Math.Clamp(ManualPlaybackSpeed, 0.50, 2.00);

    /// <summary>
    /// Gets a value indicating whether time-stretching pitch preservation is disabled.
    /// In Nightcore mode, <c>PreservesPitch</c> is always <c>false</c> so pitch rises naturally with speed.
    /// </summary>
    public bool PreservesPitch =>
        !IsNightcoreActive && !CouplePitchToSpeed && Math.Abs(ManualPitchSemitones) < 0.01;

    /// <summary>
    /// Gets the effective pitch shift in semitones relative to original recording pitch.
    /// When Nightcore is active at 1.22x, pitch rises naturally by ~+3.44 semitones.
    /// </summary>
    public double EffectivePitchSemitones =>
        IsNightcoreActive
            ? 12.0 * Math.Log2(EffectivePlaybackSpeed)
            : CouplePitchToSpeed
                ? (12.0 * Math.Log2(EffectivePlaybackSpeed)) + ManualPitchSemitones
                : ManualPitchSemitones;

    /// <summary>
    /// Gets the effective linear pitch frequency multiplier.
    /// </summary>
    public double EffectivePitchMultiplier =>
        IsNightcoreActive
            ? EffectivePlaybackSpeed
            : Math.Pow(2.0, EffectivePitchSemitones / 12.0);

    /// <summary>
    /// Returns the canonical 5-band gain curve (in dB) for a given <see cref="EqualizerPresetId"/>.
    /// </summary>
    public static double[] GetPresetBandGains(EqualizerPresetId preset) => preset switch
    {
        EqualizerPresetId.Flat => [0.0, 0.0, 0.0, 0.0, 0.0],
        EqualizerPresetId.BassBoost => [6.0, 4.5, 0.5, 1.0, 1.5],
        EqualizerPresetId.VocalClarity => [-1.5, -0.5, 3.5, 4.5, 2.0],
        EqualizerPresetId.Electronic => [5.5, 3.0, -1.0, 2.5, 4.5],
        EqualizerPresetId.Rock => [4.5, 2.5, -1.0, 3.0, 4.0],
        EqualizerPresetId.Pop => [2.5, 3.5, 1.5, 2.5, 3.0],
        EqualizerPresetId.Acoustic => [2.0, 1.5, 2.0, 3.0, 2.5],
        EqualizerPresetId.Nightcore => [6.0, 3.5, 0.5, 2.0, 2.5],
        _ => [0.0, 0.0, 0.0, 0.0, 0.0]
    };

    /// <summary>
    /// Gets the display name for an <see cref="EqualizerPresetId"/>.
    /// </summary>
    public static string GetPresetDisplayName(EqualizerPresetId preset) => preset switch
    {
        EqualizerPresetId.Flat => "Flat",
        EqualizerPresetId.BassBoost => "Bass Boost",
        EqualizerPresetId.VocalClarity => "Vocal Clarity",
        EqualizerPresetId.Electronic => "Electronic / EDM",
        EqualizerPresetId.Rock => "Rock",
        EqualizerPresetId.Pop => "Pop",
        EqualizerPresetId.Acoustic => "Acoustic",
        EqualizerPresetId.Nightcore => "Nightcore Punch",
        EqualizerPresetId.Custom => "Custom",
        _ => "Flat"
    };
}

/// <summary>
/// Live telemetry snapshot of the Music Sleep Timer ("Temporizador de música").
/// </summary>
public sealed record SleepTimerState
{
    public static readonly SleepTimerState Inactive = new()
    {
        IsActive = false,
        Mode = SleepTimerPresetMode.Off,
        RemainingTime = TimeSpan.Zero,
        TotalDuration = TimeSpan.Zero,
        StopAtEndOfCurrentTrack = false,
        IsFadingOut = false
    };

    public bool IsActive { get; init; }
    public SleepTimerPresetMode Mode { get; init; } = SleepTimerPresetMode.Off;
    public TimeSpan RemainingTime { get; init; } = TimeSpan.Zero;
    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;
    public bool StopAtEndOfCurrentTrack { get; init; }
    public bool IsFadingOut { get; init; }

    /// <summary>
    /// Short visual countdown badge label displayed on the Fullscreen Player pill.
    /// </summary>
    public string FormattedBadgeText
    {
        get
        {
            if (!IsActive)
            {
                return string.Empty;
            }

            if (StopAtEndOfCurrentTrack)
            {
                return "End of Track";
            }

            int totalSecs = Math.Max(0, (int)Math.Ceiling(RemainingTime.TotalSeconds));
            int hours = totalSecs / 3600;
            int mins = (totalSecs % 3600) / 60;
            int secs = totalSecs % 60;

            return hours > 0
                ? $"{hours}:{mins:D2}:{secs:D2}"
                : $"{mins:D2}:{secs:D2}";
        }
    }

    /// <summary>
    /// Descriptive subtitle shown inside the Sleep Timer configuration panel.
    /// </summary>
    public string StatusSubtitle
    {
        get
        {
            if (!IsActive)
            {
                return "Sleep timer off";
            }

            if (IsFadingOut)
            {
                return "Fading out volume smoothly...";
            }

            if (StopAtEndOfCurrentTrack)
            {
                return "Playback will pause after the current track";
            }

            return $"Music will pause in {FormattedBadgeText}";
        }
    }
}
