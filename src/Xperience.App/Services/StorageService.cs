using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Services;

/// <summary>
/// Thread-safe, asynchronous JSON storage service backed by the local application data directory.
/// Heavy catalog, playlist, and history collections are stored in standalone JSON files so small
/// preference updates never rewrite or retain multi-megabyte payloads in memory.
/// </summary>
public sealed class StorageService : IStorageService, IDisposable
{
    private const string RecentSearchesStorageKey = "RecentSearches_v1";
    private const string LegacyRecentSearchesStorageKey = "recent_searches_list";
    private const int MaxRecentSearches = 12;

    private static readonly HashSet<string> StandaloneBlobKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalCatalogTracks_v2",
        "RecentlyPlayedTracks_v1",
        "RecentlyPlayedAlbums_v1",
        "LikedOnlineTracks_v1",
        "UserPlaylists_v1",
        "PlaybackHistoryLog_v1",
        "TrackAddedAt_v1",
        "DataUsageHistory_v1",
        "PlaybackSessionState_v1"
    };

    private readonly ILogger<StorageService> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly string _storageDirectory;
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private Dictionary<string, JsonElement> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StorageService"/> class.
    /// </summary>
    /// <param name="logger">Structured logger instance.</param>
    public StorageService(ILogger<StorageService> logger)
    {
        _logger = logger;
        _storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer");
        Directory.CreateDirectory(_storageDirectory);
        _settingsFilePath = Path.Combine(_storageDirectory, "user_settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static bool IsStandaloneKey(string key) => StandaloneBlobKeys.Contains(key);

    private string GetStandaloneFilePath(string key) => Path.Combine(_storageDirectory, $"{key}.json");

    /// <inheritdoc />
    public async Task<T?> LoadAsync<T>(string key, T? defaultValue = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (IsDisposed)
        {
            return defaultValue;
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStandaloneKey(key))
            {
                string standalonePath = GetStandaloneFilePath(key);
                if (File.Exists(standalonePath))
                {
                    var standaloneValue = await ReadStandaloneFileCoreAsync<T>(standalonePath, cancellationToken).ConfigureAwait(false);
                    return standaloneValue is not null ? standaloneValue : defaultValue;
                }

                // Backward-compatible migration: check legacy user_settings.json once and split out to <key>.json
                await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
                if (_cache.TryGetValue(key, out var legacyElement))
                {
                    if (legacyElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        _cache.Remove(key);
                        DetachRemainingCacheElements();
                        await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
                        return defaultValue;
                    }

                    var migrated = legacyElement.Deserialize<T>(_jsonOptions);
                    if (migrated is not null)
                    {
                        await WriteStandaloneFileCoreAsync(standalonePath, migrated, cancellationToken).ConfigureAwait(false);
                        _cache.Remove(key);
                        DetachRemainingCacheElements();
                        await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
                        return migrated;
                    }
                }

                return defaultValue;
            }

            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            if (_cache.TryGetValue(key, out var element))
            {
                if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return defaultValue;
                }

                var deserialized = element.Deserialize<T>(_jsonOptions);
                return deserialized is not null ? deserialized : defaultValue;
            }

            return defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load setting key '{Key}'. Returning default value.", key);
            return defaultValue;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (IsDisposed)
        {
            return;
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStandaloneKey(key))
            {
                string standalonePath = GetStandaloneFilePath(key);
                await WriteStandaloneFileCoreAsync(standalonePath, value, cancellationToken).ConfigureAwait(false);

                // Prune any legacy copy from user_settings.json if it was already loaded in memory
                if (_isLoaded && _cache.Remove(key))
                {
                    DetachRemainingCacheElements();
                    await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            var serializedElement = JsonSerializer.SerializeToElement(value, _jsonOptions);
            _cache[key] = serializedElement;

            await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist setting key '{Key}'.", key);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlayerUiSettings> LoadPlayerUiSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return ReadPlayerUiSettingsCore();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PlayerUiSettings. Returning defaults.");
            return new PlayerUiSettings();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public Task SavePlayerUiSettingsAsync(PlayerUiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SaveAsync(PlayerUiSettings.StorageKey, settings, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PlayerUiSettings> SetShowFullscreenSeekButtonsAsync(bool show, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            var updated = ReadPlayerUiSettingsCore() with { ShowFullscreenSeekButtons = show };
            _cache[PlayerUiSettings.StorageKey] = JsonSerializer.SerializeToElement(updated, _jsonOptions);
            await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ShowFullscreenSeekButtons={Show}.", show);
            return new PlayerUiSettings { ShowFullscreenSeekButtons = show };
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlayerUiSettings> SetVisualizerPreferencesAsync(
        bool isEnabled,
        string selectedVisualizerId,
        string? lastActiveVisualizerId = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedSelected = string.IsNullOrWhiteSpace(selectedVisualizerId)
            ? (isEnabled ? "NeonTerrainPeaks" : "BackdropOnly")
            : selectedVisualizerId.Trim();

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);

            var current = ReadPlayerUiSettingsCore();
            string resolvedLastActive = !string.IsNullOrWhiteSpace(lastActiveVisualizerId) &&
                                        !string.Equals(lastActiveVisualizerId, "BackdropOnly", StringComparison.OrdinalIgnoreCase)
                ? lastActiveVisualizerId.Trim()
                : (!string.Equals(normalizedSelected, "BackdropOnly", StringComparison.OrdinalIgnoreCase)
                    ? normalizedSelected
                    : current.LastActiveVisualizerId);

            var updated = current with
            {
                IsGpuVisualizerEnabled = isEnabled,
                SelectedVisualizerId = normalizedSelected,
                LastActiveVisualizerId = string.IsNullOrWhiteSpace(resolvedLastActive)
                    ? "NeonTerrainPeaks"
                    : resolvedLastActive
            };

            _cache[PlayerUiSettings.StorageKey] = JsonSerializer.SerializeToElement(updated, _jsonOptions);
            await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist visualizer preferences.");
            return new PlayerUiSettings
            {
                IsGpuVisualizerEnabled = isEnabled,
                SelectedVisualizerId = normalizedSelected,
                LastActiveVisualizerId = lastActiveVisualizerId ?? "NeonTerrainPeaks"
            };
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ExportJsonFileAsync<T>(string filePath, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T?> ImportJsonFileAsync<T>(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return default;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> LoadRecentSearchesAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            return ReadRecentSearchesCore();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent searches. Returning empty history.");
            return [];
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> AddRecentSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        string clean = query?.Trim() ?? string.Empty;

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            var list = ReadRecentSearchesCore();
            if (string.IsNullOrWhiteSpace(clean))
            {
                return list;
            }

            list.RemoveAll(existing => string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, clean);
            if (list.Count > MaxRecentSearches)
            {
                list.RemoveRange(MaxRecentSearches, list.Count - MaxRecentSearches);
            }

            _cache[RecentSearchesStorageKey] = JsonSerializer.SerializeToElement(list, _jsonOptions);
            _cache.Remove(LegacyRecentSearchesStorageKey);
            await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist recent search query '{Query}'.", clean);
            return string.IsNullOrWhiteSpace(clean) ? [] : [clean];
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RemoveRecentSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        string clean = query?.Trim() ?? string.Empty;

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            var list = ReadRecentSearchesCore();
            if (string.IsNullOrWhiteSpace(clean))
            {
                return list;
            }

            int removed = list.RemoveAll(existing => string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                _cache[RecentSearchesStorageKey] = JsonSerializer.SerializeToElement(list, _jsonOptions);
                _cache.Remove(LegacyRecentSearchesStorageKey);
                await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            return list;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove recent search query '{Query}'.", clean);
            return [];
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearRecentSearchesAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken).ConfigureAwait(false);
            _cache[RecentSearchesStorageKey] = JsonSerializer.SerializeToElement(Array.Empty<string>(), _jsonOptions);
            _cache.Remove(LegacyRecentSearchesStorageKey);
            await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear recent searches.");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private List<string> ReadRecentSearchesCore()
    {
        if (_cache.TryGetValue(RecentSearchesStorageKey, out var element))
        {
            return ParseRecentSearchesElement(element);
        }

        if (_cache.TryGetValue(LegacyRecentSearchesStorageKey, out var legacyElement))
        {
            return ParseRecentSearchesElement(legacyElement);
        }

        return [];
    }

    private List<string> ParseRecentSearchesElement(JsonElement element)
    {
        var results = new List<string>(MaxRecentSearches);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddCandidate(string? raw)
        {
            if (results.Count >= MaxRecentSearches || string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string clean = raw.Trim();
            if (clean.Length > 0 && seen.Add(clean))
            {
                results.Add(clean);
            }
        }

        try
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        TryAddCandidate(item.GetString());
                    }
                    else if (item.ValueKind == JsonValueKind.Object &&
                             item.TryGetProperty("query", out var queryProp) &&
                             queryProp.ValueKind == JsonValueKind.String)
                    {
                        TryAddCandidate(queryProp.GetString());
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                string rawDelimited = element.GetString() ?? string.Empty;
                foreach (string part in rawDelimited.Split("||", StringSplitOptions.RemoveEmptyEntries))
                {
                    TryAddCandidate(part);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Corrupted '{Key}' payload; falling back to empty recent searches.", RecentSearchesStorageKey);
            return [];
        }

        return results;
    }

    private PlayerUiSettings ReadPlayerUiSettingsCore()
    {
        if (_cache.TryGetValue(PlayerUiSettings.StorageKey, out var element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            try
            {
                return element.Deserialize<PlayerUiSettings>(_jsonOptions) ?? new PlayerUiSettings();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Corrupted '{Key}' payload; falling back to default PlayerUiSettings.", PlayerUiSettings.StorageKey);
                return new PlayerUiSettings();
            }
        }

        if (_cache.TryGetValue(nameof(PlayerUiSettings.ShowFullscreenSeekButtons), out var legacyElement) &&
            legacyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return new PlayerUiSettings
            {
                ShowFullscreenSeekButtons = legacyElement.GetBoolean()
            };
        }

        return new PlayerUiSettings();
    }

    private async Task<T?> ReadStandaloneFileCoreAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16384,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standalone storage file '{Path}' was corrupted or unreadable.", filePath);
            return default;
        }
    }

    private async Task WriteStandaloneFileCoreAsync<T>(string filePath, T value, CancellationToken cancellationToken)
    {
        string tempPath = $"{filePath}.tmp";
        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16384,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    private void DetachRemainingCacheElements()
    {
        if (_cache.Count == 0)
        {
            return;
        }

        // Clone remaining small JsonElements so the original multi-MB JsonDocument buffer can be collected immediately
        var keys = _cache.Keys.ToArray();
        foreach (string k in keys)
        {
            _cache[k] = _cache[k].Clone();
        }
    }

    private async Task PersistCacheCoreAsync(CancellationToken cancellationToken)
    {
        string tempPath = $"{_settingsFilePath}.tmp";
        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, _cache, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, _settingsFilePath, overwrite: true);
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_isLoaded)
        {
            return;
        }

        if (File.Exists(_settingsFilePath))
        {
            try
            {
                await using var stream = new FileStream(
                    _settingsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (loaded is not null)
                {
                    _cache = new Dictionary<string, JsonElement>(loaded, StringComparer.OrdinalIgnoreCase);

                    // Drop any already-migrated standalone keys that happen to still exist in user_settings.json
                    bool prunedStaleStandalone = false;
                    foreach (string standaloneKey in StandaloneBlobKeys)
                    {
                        if (_cache.ContainsKey(standaloneKey) && File.Exists(GetStandaloneFilePath(standaloneKey)))
                        {
                            _cache.Remove(standaloneKey);
                            prunedStaleStandalone = true;
                        }
                    }

                    if (prunedStaleStandalone)
                    {
                        DetachRemainingCacheElements();
                        await PersistCacheCoreAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Settings file was corrupted or unreadable; starting with fresh cache.");
            }
        }

        _isLoaded = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ioLock.Dispose();
    }
}
