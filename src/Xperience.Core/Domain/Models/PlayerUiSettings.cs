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
}
