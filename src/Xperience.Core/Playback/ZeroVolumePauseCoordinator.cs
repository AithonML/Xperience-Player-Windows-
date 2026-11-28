namespace Xperience.Core.Playback;

/// <summary>
/// Pure action returned by <see cref="ZeroVolumePauseCoordinator"/> when app or system volume changes.
/// </summary>
public enum ZeroVolumePauseAction
{
    None = 0,
    Pause = 1,
    Resume = 2
}

/// <summary>
/// Pure, unit-testable state machine coordinating "Pause at 0 volume" across BOTH
/// the in-app volume slider and Windows system master volume/mute state.
/// </summary>
public sealed class ZeroVolumePauseCoordinator
{
    private const double ZeroThreshold = 0.0005;

    private bool _enabled;
    private double _appVolume = 1.0;
    private bool _systemZeroOrMuted;
    private bool _autoPausedByZeroVolume;

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                _autoPausedByZeroVolume = false;
            }
        }
    }

    public bool IsAutoPausedByZeroVolume => _autoPausedByZeroVolume;

    public bool IsEffectivelyZero => _appVolume <= ZeroThreshold || _systemZeroOrMuted;

    public ZeroVolumePauseAction OnAppVolumeChanged(double volume, bool isCurrentlyPlaying)
    {
        _appVolume = Math.Clamp(volume, 0.0, 1.0);
        return Evaluate(isCurrentlyPlaying);
    }

    public ZeroVolumePauseAction OnSystemVolumeChanged(float masterVolumeScalar, bool isSystemMuted, bool isCurrentlyPlaying)
    {
        _systemZeroOrMuted = isSystemMuted || masterVolumeScalar <= (float)ZeroThreshold;
        return Evaluate(isCurrentlyPlaying);
    }

    public void OnManualPlayPauseOrTrackChange()
    {
        _autoPausedByZeroVolume = false;
    }

    private ZeroVolumePauseAction Evaluate(bool isCurrentlyPlaying)
    {
        if (!_enabled)
        {
            _autoPausedByZeroVolume = false;
            return ZeroVolumePauseAction.None;
        }

        if (IsEffectivelyZero)
        {
            if (isCurrentlyPlaying)
            {
                _autoPausedByZeroVolume = true;
                return ZeroVolumePauseAction.Pause;
            }

            return ZeroVolumePauseAction.None;
        }

        if (_autoPausedByZeroVolume && !isCurrentlyPlaying)
        {
            _autoPausedByZeroVolume = false;
            return ZeroVolumePauseAction.Resume;
        }

        return ZeroVolumePauseAction.None;
    }
}
