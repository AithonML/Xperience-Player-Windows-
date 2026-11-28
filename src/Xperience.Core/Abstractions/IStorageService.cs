using Xperience.Core.Domain.Models;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Provides asynchronous, non-blocking persistence for user settings, custom themes, and catalog state.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Reads and deserializes a value from persistent storage by key.
    /// </summary>
    /// <typeparam name="T">Target deserialization type.</typeparam>
    /// <param name="key">Unique storage key.</param>
    /// <param name="defaultValue">Fallback value if the key does not exist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted value or <paramref name="defaultValue"/>.</returns>
    Task<T?> LoadAsync<T>(string key, T? defaultValue = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes and persists a value to storage asynchronously.
    /// </summary>
    /// <typeparam name="T">Type of value to store.</typeparam>
    /// <param name="key">Unique storage key.</param>
    /// <param name="value">Value instance to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the persisted <see cref="PlayerUiSettings"/>, falling back to defaults
    /// (<see cref="PlayerUiSettings.ShowFullscreenSeekButtons"/> = <c>false</c>) if the setting is absent or unreadable.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A non-null <see cref="PlayerUiSettings"/> instance.</returns>
    Task<PlayerUiSettings> LoadPlayerUiSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied <see cref="PlayerUiSettings"/> snapshot to local storage.
    /// </summary>
    /// <param name="settings">Settings snapshot to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SavePlayerUiSettingsAsync(PlayerUiSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates and persists <see cref="PlayerUiSettings.ShowFullscreenSeekButtons"/> atomically
    /// while preserving all other fields in <see cref="PlayerUiSettings"/>.
    /// </summary>
    /// <param name="show">True to show Skip Back / Skip Forward seek buttons in Fullscreen Now Playing; false to hide them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated <see cref="PlayerUiSettings"/> snapshot.</returns>
    Task<PlayerUiSettings> SetShowFullscreenSeekButtonsAsync(bool show, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates and persists GPU visualizer enabled state and selected preset ID atomically
    /// while preserving all other fields in <see cref="PlayerUiSettings"/>.
    /// </summary>
    Task<PlayerUiSettings> SetVisualizerPreferencesAsync(
        bool isEnabled,
        string selectedVisualizerId,
        string? lastActiveVisualizerId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a JSON payload to a specific file path on disk.
    /// </summary>
    /// <typeparam name="T">Type of object to export.</typeparam>
    /// <param name="filePath">Destination file path.</param>
    /// <param name="value">Value to serialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportJsonFileAsync<T>(string filePath, T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports and deserializes a JSON payload from a specific file path on disk.
    /// </summary>
    /// <typeparam name="T">Type of object to import.</typeparam>
    /// <param name="filePath">Source file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized object instance.</returns>
    Task<T?> ImportJsonFileAsync<T>(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the user's persisted recent search queries ordered most-recent first (up to 12 entries).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordered list of recent search strings.</returns>
    Task<IReadOnlyList<string>> LoadRecentSearchesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a search query to the top of recent search history (trimmed, case-insensitively deduplicated,
    /// moved to index 0, and capped at 12 entries) and returns the updated ordered list.
    /// </summary>
    /// <param name="query">Search text to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ordered list of recent search strings.</returns>
    Task<IReadOnlyList<string>> AddRecentSearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a specific search query (case-insensitively) from persisted recent search history
    /// and returns the updated ordered list.
    /// </summary>
    /// <param name="query">Search text to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated ordered list of recent search strings.</returns>
    Task<IReadOnlyList<string>> RemoveRecentSearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all persisted recent search queries from local storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearRecentSearchesAsync(CancellationToken cancellationToken = default);
}
