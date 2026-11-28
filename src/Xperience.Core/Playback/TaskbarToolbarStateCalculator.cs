namespace Xperience.Core.Playback;

/// <summary>
/// Stable Win32 command identifiers for the Windows Taskbar thumbnail toolbar buttons.
/// Ordered symmetrically from left to right around the center Play/Pause button.
/// </summary>
public enum TaskbarButtonId : uint
{
    /// <summary>
    /// Leftmost button: toggles favorite/liked state on the currently playing track.
    /// </summary>
    Like = 1001,

    /// <summary>
    /// Left-center button: restarts the current track or skips to the previous track.
    /// </summary>
    Previous = 1002,

    /// <summary>
    /// Center primary button: toggles between Play and Pause.
    /// </summary>
    PlayPause = 1003,

    /// <summary>
    /// Right-center button: skips to the next track in the playback queue.
    /// </summary>
    Next = 1004,

    /// <summary>
    /// Rightmost symmetric companion button: toggles Shuffle playback mode.
    /// </summary>
    Shuffle = 1005
}

/// <summary>
/// Visual glyph variants rendered for the Windows Taskbar thumbnail toolbar buttons.
/// </summary>
public enum TaskbarIconKind
{
    LikeOff = 0,
    LikeOn = 1,
    Previous = 2,
    Play = 3,
    Pause = 4,
    Next = 5,
    ShuffleOff = 6,
    ShuffleOn = 7
}

/// <summary>
/// Immutable playback snapshot used to compute the Windows Taskbar thumbnail toolbar state.
/// </summary>
public sealed record TaskbarToolbarSnapshot(
    bool HasLoadedTrack,
    bool IsPlaying,
    bool IsCurrentTrackLiked,
    bool IsShuffleEnabled);

/// <summary>
/// Platform-neutral descriptor for a single Windows Taskbar thumbnail toolbar button.
/// </summary>
public sealed record TaskbarButtonDescriptor(
    TaskbarButtonId Id,
    TaskbarIconKind Icon,
    string Tooltip,
    bool IsEnabled);

/// <summary>
/// Pure state calculator for the 5-button symmetric Windows Taskbar thumbnail toolbar:
/// <c>[Like] [Previous] [Play/Pause] [Next] [Shuffle]</c>.
/// </summary>
public static class TaskbarToolbarStateCalculator
{
    /// <summary>
    /// Sentinel track identifier used when no real track is loaded in the player.
    /// </summary>
    public const string EmptyStateTrackId = "empty_state_track";

    /// <summary>
    /// Total number of symmetric thumbnail toolbar buttons.
    /// </summary>
    public const int ButtonCount = 5;

    /// <summary>
    /// Determines whether the specified track identifier represents a real loaded track.
    /// </summary>
    public static bool HasLoadedTrack(string? trackId) =>
        !string.IsNullOrWhiteSpace(trackId) &&
        !string.Equals(trackId, EmptyStateTrackId, StringComparison.Ordinal);

    /// <summary>
    /// Builds the ordered 5-button descriptor array for the given playback <paramref name="snapshot"/>.
    /// </summary>
    public static IReadOnlyList<TaskbarButtonDescriptor> BuildButtons(TaskbarToolbarSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        bool enabled = snapshot.HasLoadedTrack;
        bool showLiked = enabled && snapshot.IsCurrentTrackLiked;
        bool showPause = enabled && snapshot.IsPlaying;
        bool showShuffleOn = snapshot.IsShuffleEnabled;

        return
        [
            new TaskbarButtonDescriptor(
                Id: TaskbarButtonId.Like,
                Icon: showLiked ? TaskbarIconKind.LikeOn : TaskbarIconKind.LikeOff,
                Tooltip: showLiked ? "Unlike" : "Like",
                IsEnabled: enabled),

            new TaskbarButtonDescriptor(
                Id: TaskbarButtonId.Previous,
                Icon: TaskbarIconKind.Previous,
                Tooltip: "Previous",
                IsEnabled: enabled),

            new TaskbarButtonDescriptor(
                Id: TaskbarButtonId.PlayPause,
                Icon: showPause ? TaskbarIconKind.Pause : TaskbarIconKind.Play,
                Tooltip: showPause ? "Pause" : "Play",
                IsEnabled: enabled),

            new TaskbarButtonDescriptor(
                Id: TaskbarButtonId.Next,
                Icon: TaskbarIconKind.Next,
                Tooltip: "Next",
                IsEnabled: enabled),

            new TaskbarButtonDescriptor(
                Id: TaskbarButtonId.Shuffle,
                Icon: showShuffleOn ? TaskbarIconKind.ShuffleOn : TaskbarIconKind.ShuffleOff,
                Tooltip: showShuffleOn ? "Shuffle: On" : "Shuffle: Off",
                IsEnabled: enabled)
        ];
    }

    /// <summary>
    /// Maps a Win32 <c>WM_COMMAND</c> button identifier back to a known <see cref="TaskbarButtonId"/>.
    /// </summary>
    public static bool TryMapButtonId(uint rawButtonId, out TaskbarButtonId buttonId)
    {
        switch ((TaskbarButtonId)rawButtonId)
        {
            case TaskbarButtonId.Like:
            case TaskbarButtonId.Previous:
            case TaskbarButtonId.PlayPause:
            case TaskbarButtonId.Next:
            case TaskbarButtonId.Shuffle:
                buttonId = (TaskbarButtonId)rawButtonId;
                return true;
            default:
                buttonId = default;
                return false;
        }
    }

    /// <summary>
    /// Compares two button descriptor sequences for exact value equality so redundant COM updates can be skipped.
    /// </summary>
    public static bool AreEqual(
        IReadOnlyList<TaskbarButtonDescriptor>? left,
        IReadOnlyList<TaskbarButtonDescriptor>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
