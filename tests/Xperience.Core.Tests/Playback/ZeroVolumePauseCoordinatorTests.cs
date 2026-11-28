using Xperience.Core.Playback;
using Xunit;

namespace Xperience.Core.Tests.Playback;

public sealed class ZeroVolumePauseCoordinatorTests
{
    [Fact]
    public void Disabled_NeverPausesOrResumes()
    {
        var coord = new ZeroVolumePauseCoordinator { IsEnabled = false };
        Assert.Equal(ZeroVolumePauseAction.None, coord.OnAppVolumeChanged(0.0, isCurrentlyPlaying: true));
        Assert.Equal(ZeroVolumePauseAction.None, coord.OnSystemVolumeChanged(0.0f, isSystemMuted: true, isCurrentlyPlaying: true));
        Assert.False(coord.IsAutoPausedByZeroVolume);
    }

    [Fact]
    public void AppVolumeZero_PausesAndResumesWhenRaisedAboveZero()
    {
        var coord = new ZeroVolumePauseCoordinator { IsEnabled = true };
        Assert.Equal(ZeroVolumePauseAction.Pause, coord.OnAppVolumeChanged(0.0, isCurrentlyPlaying: true));
        Assert.True(coord.IsAutoPausedByZeroVolume);

        Assert.Equal(ZeroVolumePauseAction.Resume, coord.OnAppVolumeChanged(0.5, isCurrentlyPlaying: false));
        Assert.False(coord.IsAutoPausedByZeroVolume);
    }

    [Fact]
    public void SystemVolumeZeroOrMuted_PausesAndResumesWhenRestored()
    {
        var coord = new ZeroVolumePauseCoordinator { IsEnabled = true };
        Assert.Equal(ZeroVolumePauseAction.Pause, coord.OnSystemVolumeChanged(0.0f, isSystemMuted: false, isCurrentlyPlaying: true));
        Assert.True(coord.IsAutoPausedByZeroVolume);

        Assert.Equal(ZeroVolumePauseAction.Resume, coord.OnSystemVolumeChanged(0.4f, isSystemMuted: false, isCurrentlyPlaying: false));
        Assert.False(coord.IsAutoPausedByZeroVolume);

        // System mute also triggers pause
        Assert.Equal(ZeroVolumePauseAction.Pause, coord.OnSystemVolumeChanged(0.4f, isSystemMuted: true, isCurrentlyPlaying: true));
        Assert.Equal(ZeroVolumePauseAction.Resume, coord.OnSystemVolumeChanged(0.4f, isSystemMuted: false, isCurrentlyPlaying: false));
    }

    [Fact]
    public void BothAppAndSystemZero_OnlyResumesWhenBothAreAboveZero()
    {
        var coord = new ZeroVolumePauseCoordinator { IsEnabled = true };
        Assert.Equal(ZeroVolumePauseAction.Pause, coord.OnAppVolumeChanged(0.0, isCurrentlyPlaying: true));
        Assert.Equal(ZeroVolumePauseAction.None, coord.OnSystemVolumeChanged(0.0f, isSystemMuted: false, isCurrentlyPlaying: false));

        // Raising only app volume while system is still 0 must NOT resume
        Assert.Equal(ZeroVolumePauseAction.None, coord.OnAppVolumeChanged(0.7, isCurrentlyPlaying: false));
        Assert.True(coord.IsAutoPausedByZeroVolume);

        // Raising system volume too -> now resumes
        Assert.Equal(ZeroVolumePauseAction.Resume, coord.OnSystemVolumeChanged(0.6f, isSystemMuted: false, isCurrentlyPlaying: false));
        Assert.False(coord.IsAutoPausedByZeroVolume);
    }

    [Fact]
    public void ManualPlayPause_ClearsAutoPausedFlag()
    {
        var coord = new ZeroVolumePauseCoordinator { IsEnabled = true };
        Assert.Equal(ZeroVolumePauseAction.Pause, coord.OnSystemVolumeChanged(0.0f, isSystemMuted: false, isCurrentlyPlaying: true));
        coord.OnManualPlayPauseOrTrackChange();
        Assert.False(coord.IsAutoPausedByZeroVolume);

        // Raising system volume after manual interaction must NOT auto-resume
        Assert.Equal(ZeroVolumePauseAction.None, coord.OnSystemVolumeChanged(0.5f, isSystemMuted: false, isCurrentlyPlaying: false));
    }
}
