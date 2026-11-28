using Xperience.Core.Playback;
using Xunit;

namespace Xperience.Core.Tests.Playback;

public sealed class TaskbarToolbarStateCalculatorTests
{
    [Fact]
    public void HasLoadedTrack_RejectsNullEmptyAndSentinel()
    {
        Assert.False(TaskbarToolbarStateCalculator.HasLoadedTrack(null));
        Assert.False(TaskbarToolbarStateCalculator.HasLoadedTrack(string.Empty));
        Assert.False(TaskbarToolbarStateCalculator.HasLoadedTrack("   "));
        Assert.False(TaskbarToolbarStateCalculator.HasLoadedTrack(TaskbarToolbarStateCalculator.EmptyStateTrackId));
        Assert.True(TaskbarToolbarStateCalculator.HasLoadedTrack("track_123"));
    }

    [Fact]
    public void BuildButtons_WhenNoTrackLoaded_DisablesAllFiveSymmetricButtons()
    {
        var snapshot = new TaskbarToolbarSnapshot(
            HasLoadedTrack: false,
            IsPlaying: true,
            IsCurrentTrackLiked: true,
            IsShuffleEnabled: false);

        var buttons = TaskbarToolbarStateCalculator.BuildButtons(snapshot);

        Assert.Equal(TaskbarToolbarStateCalculator.ButtonCount, buttons.Count);
        Assert.Equal(TaskbarButtonId.Like, buttons[0].Id);
        Assert.Equal(TaskbarButtonId.Previous, buttons[1].Id);
        Assert.Equal(TaskbarButtonId.PlayPause, buttons[2].Id);
        Assert.Equal(TaskbarButtonId.Next, buttons[3].Id);
        Assert.Equal(TaskbarButtonId.Shuffle, buttons[4].Id);

        Assert.All(buttons, b => Assert.False(b.IsEnabled));
        Assert.Equal(TaskbarIconKind.LikeOff, buttons[0].Icon);
        Assert.Equal("Like", buttons[0].Tooltip);
        Assert.Equal(TaskbarIconKind.Play, buttons[2].Icon);
        Assert.Equal("Play", buttons[2].Tooltip);
        Assert.Equal(TaskbarIconKind.ShuffleOff, buttons[4].Icon);
        Assert.Equal("Shuffle: Off", buttons[4].Tooltip);
    }

    [Fact]
    public void BuildButtons_WhenPlayingLikedAndShuffled_UpdatesIconsTooltipsAndEnabledState()
    {
        var snapshot = new TaskbarToolbarSnapshot(
            HasLoadedTrack: true,
            IsPlaying: true,
            IsCurrentTrackLiked: true,
            IsShuffleEnabled: true);

        var buttons = TaskbarToolbarStateCalculator.BuildButtons(snapshot);

        Assert.All(buttons, b => Assert.True(b.IsEnabled));
        Assert.Equal(TaskbarIconKind.LikeOn, buttons[0].Icon);
        Assert.Equal("Unlike", buttons[0].Tooltip);
        Assert.Equal(TaskbarIconKind.Previous, buttons[1].Icon);
        Assert.Equal("Previous", buttons[1].Tooltip);
        Assert.Equal(TaskbarIconKind.Pause, buttons[2].Icon);
        Assert.Equal("Pause", buttons[2].Tooltip);
        Assert.Equal(TaskbarIconKind.Next, buttons[3].Icon);
        Assert.Equal("Next", buttons[3].Tooltip);
        Assert.Equal(TaskbarIconKind.ShuffleOn, buttons[4].Icon);
        Assert.Equal("Shuffle: On", buttons[4].Tooltip);
    }

    [Fact]
    public void BuildButtons_WhenPausedNotLikedNotShuffled_ReflectsIdleLoadedState()
    {
        var snapshot = new TaskbarToolbarSnapshot(
            HasLoadedTrack: true,
            IsPlaying: false,
            IsCurrentTrackLiked: false,
            IsShuffleEnabled: false);

        var buttons = TaskbarToolbarStateCalculator.BuildButtons(snapshot);

        Assert.All(buttons, b => Assert.True(b.IsEnabled));
        Assert.Equal(TaskbarIconKind.LikeOff, buttons[0].Icon);
        Assert.Equal("Like", buttons[0].Tooltip);
        Assert.Equal(TaskbarIconKind.Play, buttons[2].Icon);
        Assert.Equal("Play", buttons[2].Tooltip);
        Assert.Equal(TaskbarIconKind.ShuffleOff, buttons[4].Icon);
        Assert.Equal("Shuffle: Off", buttons[4].Tooltip);
    }

    [Fact]
    public void TryMapButtonId_ValidatesKnownIdsAndRejectsUnknown()
    {
        Assert.True(TaskbarToolbarStateCalculator.TryMapButtonId((uint)TaskbarButtonId.Like, out var like));
        Assert.Equal(TaskbarButtonId.Like, like);

        Assert.True(TaskbarToolbarStateCalculator.TryMapButtonId((uint)TaskbarButtonId.PlayPause, out var playPause));
        Assert.Equal(TaskbarButtonId.PlayPause, playPause);

        Assert.True(TaskbarToolbarStateCalculator.TryMapButtonId((uint)TaskbarButtonId.Shuffle, out var shuffle));
        Assert.Equal(TaskbarButtonId.Shuffle, shuffle);

        Assert.False(TaskbarToolbarStateCalculator.TryMapButtonId(9999, out _));
        Assert.False(TaskbarToolbarStateCalculator.TryMapButtonId(0, out _));
    }

    [Fact]
    public void AreEqual_DetectsIdenticalAndChangedDescriptors()
    {
        var a = TaskbarToolbarStateCalculator.BuildButtons(new TaskbarToolbarSnapshot(true, false, false, false));
        var b = TaskbarToolbarStateCalculator.BuildButtons(new TaskbarToolbarSnapshot(true, false, false, false));
        var c = TaskbarToolbarStateCalculator.BuildButtons(new TaskbarToolbarSnapshot(true, true, false, false));

        Assert.True(TaskbarToolbarStateCalculator.AreEqual(a, b));
        Assert.False(TaskbarToolbarStateCalculator.AreEqual(a, c));
        Assert.False(TaskbarToolbarStateCalculator.AreEqual(a, null));
    }
}
