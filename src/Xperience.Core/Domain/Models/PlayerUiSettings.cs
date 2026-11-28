namespace Xperience.Core.Domain.Models;

/// <summary>
/// Persisted user interface preferences for the Fullscreen Now Playing experience and player chrome.
/// </summary>
public sealed record PlayerUiSettings
{
    /// <summary>
    /// Canonical key used to persist <see cref="PlayerUiSettings"/> inside <c>user_settings.json</c>.
    /// </summary>
    public const string StorageKey = "PlayerUiSettings_v1";

    /// <summary>
    /// Gets a value indicating whether the Skip Back / Skip Forward (±seconds) seek buttons
    /// are shown in Fullscreen Now Playing. Defaults to <c>false</c> (hidden) to keep the
    /// Fullscreen transport deck clean; users can re-enable them in Settings.
    /// </summary>
    public bool ShowFullscreenSeekButtons { get; init; } = false;

    /// <summary>
    /// Gets a value indicating whether the GPU music visualizer overlay is enabled in Fullscreen Now Playing.
    /// Defaults to <c>false</c> (Off) and remembers the user's last selection across restarts.
    /// </summary>
    public bool IsGpuVisualizerEnabled { get; init; } = false;

    /// <summary>
    /// Gets the persisted active visualizer ID (<c>"BackdropOnly"</c> when Off).
    /// </summary>
    public string SelectedVisualizerId { get; init; } = "BackdropOnly";

    /// <summary>
    /// Gets the last non-Off visualizer ID chosen by the user for quick re-enabling.
    /// </summary>
    public string LastActiveVisualizerId { get; init; } = "NeonTerrainPeaks";
}
