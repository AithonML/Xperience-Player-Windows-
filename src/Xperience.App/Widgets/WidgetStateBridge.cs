using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.Widgets.Providers;
using Xperience.App.Services;
using Xperience.App.ViewModels;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;
using Xperience.Core.Widgets;

namespace Xperience.App.Widgets;

/// <summary>
/// Live in-process bridge synchronizing Xperience's audio playback state and personalized playlists
/// to <c>%LOCALAPPDATA%\XperienceMusicPlayer\widget_state.json</c> (and directly to Windows 11
/// <see cref="WidgetManager"/> when packaged), while executing incoming widget commands from
/// <see cref="WidgetIpcTransport"/> or CLI launch arguments.
/// </summary>
public sealed class WidgetStateBridge : IDisposable
{
    private const long PositionSyncThrottleMs = 2000;

    private readonly IAudioPlayerService _audioPlayerService;
    private readonly IMediaCatalogService _catalogService;
    private readonly ShellViewModel _shellViewModel;
    private readonly ILogger<WidgetStateBridge> _logger;
    private readonly WidgetStateFileStore _stateStore;
    private readonly WidgetIpcTransport _ipcTransport;
    private readonly object _stateLock = new();

    private DispatcherQueue? _dispatcherQueue;
    private WidgetStateSnapshot _currentSnapshot;
    private long _lastPositionPersistTickMs;
    private bool _initialized;
    private bool _disposed;

    public WidgetStateBridge(
        IAudioPlayerService audioPlayerService,
        IMediaCatalogService catalogService,
        ShellViewModel shellViewModel,
        ILogger<WidgetStateBridge> logger)
    {
        _audioPlayerService = audioPlayerService;
        _catalogService = catalogService;
        _shellViewModel = shellViewModel;
        _logger = logger;
        _stateStore = new WidgetStateFileStore();
        _currentSnapshot = _stateStore.Load();
        _ipcTransport = new WidgetIpcTransport(HandleWidgetCommandAsync);
    }

    /// <summary>
    /// Attaches playback/catalog listeners, starts the local widget IPC pipe server,
    /// performs an initial snapshot sync, and executes any pending widget launch command.
    /// Safe in both packaged MSIX and unpackaged debug launches.
    /// </summary>
    public async Task InitializeAsync(DispatcherQueue? dispatcherQueue, WidgetCommandMessage? pendingLaunchAction = null)
    {
        if (_disposed || _initialized)
        {
            return;
        }

        _initialized = true;
        _dispatcherQueue = dispatcherQueue;

        try
        {
            _audioPlayerService.CurrentTrackChanged += OnCurrentTrackChanged;
            _audioPlayerService.IsPlayingChanged += OnIsPlayingChanged;
            _audioPlayerService.PositionChanged += OnPositionChanged;
            _audioPlayerService.QueueChanged += OnQueueChanged;
            _shellViewModel.PropertyChanged += OnShellPropertyChanged;

            _ipcTransport.Start();

            // Persist initial playback snapshot immediately, then curate recommendations asynchronously.
            PersistPlaybackSnapshot(force: true);
            _ = Task.Run(() => RefreshRecommendationsAsync());

            if (pendingLaunchAction is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Give initial Home/catalog hydration a brief moment before running a cold-start playlist/playback action.
                        await Task.WhenAny(_shellViewModel.InitialHomeReadyTask, Task.Delay(3500)).ConfigureAwait(false);
                        await HandleWidgetCommandAsync(pendingLaunchAction).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pending widget launch action '{Verb}' failed.", pendingLaunchAction.Verb);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WidgetStateBridge initialization encountered a non-fatal error.");
        }
    }

    private void OnCurrentTrackChanged(object? sender, TrackItem? track)
    {
        PersistPlaybackSnapshot(force: true);
    }

    private void OnIsPlayingChanged(object? sender, bool isPlaying)
    {
        PersistPlaybackSnapshot(force: true);
    }

    private void OnPositionChanged(object? sender, PlaybackPositionEventArgs e)
    {
        long nowTick = Environment.TickCount64;
        if (nowTick - _lastPositionPersistTickMs < PositionSyncThrottleMs)
        {
            return;
        }

        PersistPlaybackSnapshot(force: false);
    }

    private void OnQueueChanged(object? sender, IReadOnlyList<TrackItem> queue)
    {
        PersistPlaybackSnapshot(force: true);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ShellViewModel.CurrentTrack), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ShellViewModel.IsPlaying), StringComparison.Ordinal))
        {
            PersistPlaybackSnapshot(force: true);
        }
    }

    private void PersistPlaybackSnapshot(bool force)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            long nowTick = Environment.TickCount64;
            if (!force && nowTick - _lastPositionPersistTickMs < PositionSyncThrottleMs)
            {
                return;
            }

            _lastPositionPersistTickMs = nowTick;

            var track = _audioPlayerService.CurrentTrack;
            var playback = new WidgetPlaybackSnapshot
            {
                TrackId = track?.Id,
                Title = track?.Title ?? string.Empty,
                Artist = track?.Artist ?? string.Empty,
                Album = track?.Album ?? string.Empty,
                ArtworkUri = track?.CoverArtUri,
                IsPlaying = _audioPlayerService.IsPlaying,
                IsLiked = track?.IsLiked ?? false,
                PositionMs = (long)_audioPlayerService.Position.TotalMilliseconds,
                DurationMs = (long)(_audioPlayerService.EffectiveDuration > TimeSpan.Zero
                    ? _audioPlayerService.EffectiveDuration.TotalMilliseconds
                    : _audioPlayerService.Duration.TotalMilliseconds),
                QueueSize = _audioPlayerService.Queue.Count,
                UpdatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            WidgetStateSnapshot nextSnapshot;
            lock (_stateLock)
            {
                nextSnapshot = _currentSnapshot with { Playback = playback };
                _currentSnapshot = nextSnapshot;
            }

            _stateStore.Save(nextSnapshot);
            TryPushToPackagedWidgetManager(nextSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-fatal error persisting widget playback snapshot.");
        }
    }

    /// <summary>
    /// Refreshes the curated playlist recommendations from the user's real library, smart playlists,
    /// liked songs, and Home discovery feed, then writes the updated snapshot.
    /// </summary>
    public async Task RefreshRecommendationsAsync(bool advanceRotation = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IReadOnlyList<UserPlaylist> userPlaylists = [];
            IReadOnlyList<UserPlaylist> smartPlaylists = [];
            IReadOnlyList<TrackItem> likedSongs = [];
            IReadOnlyList<RecommendedPlaylistItem> madeForYou = [];
            IReadOnlyList<RadioStationItem> radioStations = [];
            IReadOnlyList<TrackItem> recentlyPlayed = [];

            try { userPlaylists = await _catalogService.GetPlaylistsAsync().ConfigureAwait(false); } catch { }
            try { smartPlaylists = await _catalogService.GetSmartPlaylistSuggestionsAsync().ConfigureAwait(false); } catch { }
            try { likedSongs = await _catalogService.GetLikedSongsAsync().ConfigureAwait(false); } catch { }

            try
            {
                var feed = await _catalogService.GetHomeDiscoveryFeedAsync().ConfigureAwait(false);
                madeForYou = feed.PlaylistsMadeForYou ?? [];
                radioStations = feed.RadioStations ?? [];
                recentlyPlayed = feed.RecentlyPlayedTracks ?? [];
                if (smartPlaylists.Count == 0 && feed.SmartSuggestedPlaylists is { Count: > 0 })
                {
                    smartPlaylists = feed.SmartSuggestedPlaylists;
                }
            }
            catch
            {
                // Offline or catalog not yet hydrated — fall back to local playlists & liked songs
            }

            string? likedArtwork = likedSongs
                .Select(t => t.CoverArtUri)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

            var curated = WidgetRecommendationCurator.Curate(
                userPlaylists: userPlaylists,
                smartPlaylists: smartPlaylists,
                madeForYou: madeForYou,
                radioStations: radioStations,
                likedCount: likedSongs.Count,
                likedArtworkUri: likedArtwork,
                recentlyPlayedTracks: recentlyPlayed);

            WidgetStateSnapshot nextSnapshot;
            lock (_stateLock)
            {
                int nextOffset = advanceRotation ? _currentSnapshot.RotationOffset + 4 : _currentSnapshot.RotationOffset;
                nextSnapshot = _currentSnapshot with
                {
                    Recommendations = curated,
                    LikedSongsCount = likedSongs.Count,
                    RecommendationsUpdatedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RotationOffset = nextOffset
                };
                _currentSnapshot = nextSnapshot;
            }

            _stateStore.Save(nextSnapshot);
            TryPushToPackagedWidgetManager(nextSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Non-fatal error refreshing widget recommendations.");
        }
    }

    private async Task HandleWidgetCommandAsync(WidgetCommandMessage command)
    {
        if (_disposed || string.IsNullOrWhiteSpace(command.Verb))
        {
            return;
        }

        _logger.LogInformation("Executing widget command '{Verb}'...", command.Verb);

        switch (command.Verb.ToLowerInvariant())
        {
            case WidgetActionVerbs.PlayPause:
                await RunOnUiThreadAsync(async () =>
                {
                    if (_audioPlayerService.CurrentTrack is null ||
                        string.Equals(_audioPlayerService.CurrentTrack.Id, "empty_state_track", StringComparison.OrdinalIgnoreCase))
                    {
                        await StartFallbackPlaybackAsync();
                    }
                    else
                    {
                        await _shellViewModel.TogglePlayPauseCommand.ExecuteAsync(null);
                    }
                }).ConfigureAwait(false);
                PersistPlaybackSnapshot(force: true);
                break;

            case WidgetActionVerbs.Next:
                await RunOnUiThreadAsync(() => _shellViewModel.NextTrackCommand.ExecuteAsync(null)).ConfigureAwait(false);
                PersistPlaybackSnapshot(force: true);
                break;

            case WidgetActionVerbs.Previous:
                await RunOnUiThreadAsync(() => _shellViewModel.PreviousTrackCommand.ExecuteAsync(null)).ConfigureAwait(false);
                PersistPlaybackSnapshot(force: true);
                break;

            case WidgetActionVerbs.ToggleLike:
                await RunOnUiThreadAsync(() => _shellViewModel.ToggleLikeCurrentTrackCommand.ExecuteAsync(null)).ConfigureAwait(false);
                PersistPlaybackSnapshot(force: true);
                _ = Task.Run(() => RefreshRecommendationsAsync(advanceRotation: false));
                break;

            case WidgetActionVerbs.RefreshRecommendations:
                await RefreshRecommendationsAsync(advanceRotation: true).ConfigureAwait(false);
                break;

            case WidgetActionVerbs.OpenAmbient:
                await RunOnUiThreadAsync(async () =>
                {
                    ActivateMainWindowSafe();
                    await _shellViewModel.OpenFullscreenNowPlayingCommand.ExecuteAsync(null);
                }).ConfigureAwait(false);
                break;

            case WidgetActionVerbs.OpenApp:
                await RunOnUiThreadAsync(() =>
                {
                    ActivateMainWindowSafe();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                break;

            case WidgetActionVerbs.PlayPlaylist:
                await HandlePlayPlaylistCommandAsync(command).ConfigureAwait(false);
                PersistPlaybackSnapshot(force: true);
                break;
        }
    }

    private async Task HandlePlayPlaylistCommandAsync(WidgetCommandMessage command)
    {
        await RunOnUiThreadAsync(async () =>
        {
            ActivateMainWindowSafe();

            var sourceType = command.SourceType ?? WidgetPlaylistSourceType.Fallback;
            string playlistId = command.PlaylistId?.Trim() ?? string.Empty;

            if (sourceType == WidgetPlaylistSourceType.LikedSongs ||
                string.Equals(playlistId, WidgetRecommendationCurator.LikedSongsVirtualId, StringComparison.OrdinalIgnoreCase))
            {
                if (_shellViewModel.LikedSongs.Count > 0)
                {
                    await _shellViewModel.PlayAllLikedSongsCommand.ExecuteAsync(null);
                    return;
                }

                var liked = await _catalogService.GetLikedSongsAsync();
                if (liked.Count > 0)
                {
                    await _shellViewModel.PlayTrackFromListAsync(liked[0], liked);
                    return;
                }
            }

            if (sourceType is WidgetPlaylistSourceType.UserPlaylist or WidgetPlaylistSourceType.SmartPlaylist)
            {
                var userPlaylists = await _catalogService.GetPlaylistsAsync();
                var match = userPlaylists.FirstOrDefault(p =>
                    string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Title, command.PlaylistTitle, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    var smartPlaylists = await _catalogService.GetSmartPlaylistSuggestionsAsync();
                    match = smartPlaylists.FirstOrDefault(p =>
                        string.Equals(p.Id, playlistId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Title, command.PlaylistTitle, StringComparison.OrdinalIgnoreCase));
                }

                if (match is not null && match.Tracks.Count > 0)
                {
                    await _shellViewModel.PlayPlaylistDirectlyAsync(match);
                    return;
                }
            }

            if (sourceType is WidgetPlaylistSourceType.RadioStation or WidgetPlaylistSourceType.MadeForYou)
            {
                string targetRadioPlaylistId = !string.IsNullOrWhiteSpace(command.YtmPlaylistId)
                    ? command.YtmPlaylistId
                    : playlistId;

                var existingStation = _shellViewModel.HomeRadioStations.FirstOrDefault(r =>
                    string.Equals(r.PlaylistId, targetRadioPlaylistId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Id, playlistId, StringComparison.OrdinalIgnoreCase));

                var station = existingStation ?? new RadioStationItem(
                    Id: playlistId,
                    Title: command.PlaylistTitle ?? "Quick Radio",
                    Subtitle: "Quick Mix",
                    ArtworkUri: string.Empty,
                    PlaylistId: targetRadioPlaylistId,
                    SeedVideoId: command.SeedVideoId,
                    Params: command.Params);

                await _shellViewModel.PlayRadioStationAsync(station);
                return;
            }

            if (sourceType == WidgetPlaylistSourceType.RecentlyPlayed &&
                _shellViewModel.HomeRecentlyPlayed.Count > 0)
            {
                await _shellViewModel.PlayTrackFromListAsync(
                    _shellViewModel.HomeRecentlyPlayed[0],
                    _shellViewModel.HomeRecentlyPlayed);
                return;
            }

            // Fallback cards (Daily Mix / Late Night Vibes / Quick Radio) -> play best available mix
            await StartFallbackPlaybackAsync();
        }).ConfigureAwait(false);
    }

    private async Task StartFallbackPlaybackAsync()
    {
        if (_shellViewModel.HomeRecommendedTracks.Count > 0)
        {
            await _shellViewModel.PlayTrackFromListAsync(
                _shellViewModel.HomeRecommendedTracks[0],
                _shellViewModel.HomeRecommendedTracks);
            return;
        }

        if (_shellViewModel.HomeRecentlyPlayed.Count > 0)
        {
            await _shellViewModel.PlayTrackFromListAsync(
                _shellViewModel.HomeRecentlyPlayed[0],
                _shellViewModel.HomeRecentlyPlayed);
            return;
        }

        var liked = await _catalogService.GetLikedSongsAsync();
        if (liked.Count > 0)
        {
            await _shellViewModel.PlayTrackFromListAsync(liked[0], liked);
            return;
        }

        if (_shellViewModel.Songs.Count > 0)
        {
            await _shellViewModel.PlayTrackFromListAsync(_shellViewModel.Songs[0], _shellViewModel.Songs);
        }
    }

    private static void ActivateMainWindowSafe()
    {
        try
        {
            App.MainWindowInstance?.Activate();
        }
        catch
        {
            // Ignore window activation errors
        }
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    private static void TryPushToPackagedWidgetManager(WidgetStateSnapshot snapshot)
    {
        // Only invoke Windows App SDK WidgetManager when running with MSIX package identity.
        // Unpackaged debug launches skip this call completely to avoid COM/package-identity exceptions.
        if (!WindowsInstallerLauncher.IsPackagedIdentity())
        {
            return;
        }

        try
        {
            var manager = WidgetManager.GetDefault();
            var widgets = manager.GetWidgetInfos();
            if (widgets is null || widgets.Length == 0)
            {
                return;
            }

            foreach (var info in widgets)
            {
                if (info?.WidgetContext is not { } ctx || !ctx.IsActive)
                {
                    continue;
                }

                var options = new WidgetUpdateRequestOptions(ctx.Id)
                {
                    Template = WidgetAdaptiveCardBuilder.GetTemplateJson(ctx.DefinitionId),
                    Data = WidgetAdaptiveCardBuilder.BuildDataJson(ctx.DefinitionId, snapshot),
                    CustomState = ctx.DefinitionId
                };
                manager.UpdateWidget(options);
            }
        }
        catch
        {
            // Never throw from live widget push
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audioPlayerService.CurrentTrackChanged -= OnCurrentTrackChanged;
        _audioPlayerService.IsPlayingChanged -= OnIsPlayingChanged;
        _audioPlayerService.PositionChanged -= OnPositionChanged;
        _audioPlayerService.QueueChanged -= OnQueueChanged;
        _shellViewModel.PropertyChanged -= OnShellPropertyChanged;
        _ipcTransport.Dispose();
    }
}
