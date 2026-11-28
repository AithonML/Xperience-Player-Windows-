using Microsoft.UI.Xaml;
using Xperience.App.ViewModels;
using Xperience.Core.Domain.Entities;

namespace Xperience.App.Views;

public sealed partial class ShellPage
{
    private Windows.Media.Playback.MediaPlayer? _fullscreenMusicVideoPlayer;
    private string? _loadedFullscreenMusicVideoUri;
    private bool _isMusicVideoPlayerInitialized;
    private int _musicVideoLoadGeneration;
    private long _lastMusicVideoSeekTickMs;

    private void InitializeFullscreenMusicVideoFeatures()
    {
        if (_isMusicVideoPlayerInitialized)
        {
            return;
        }

        _isMusicVideoPlayerInitialized = true;
        ViewModel.MusicVideoStateChanged += OnViewModelMusicVideoStateChanged;
    }

    private void EnsureFullscreenMusicVideoPlayerCreated()
    {
        if (_fullscreenMusicVideoPlayer is not null)
        {
            return;
        }

        _fullscreenMusicVideoPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsMuted = true,
            IsLoopingEnabled = false,
            AutoPlay = false
        };

        _fullscreenMusicVideoPlayer.MediaOpened += (player, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (ViewModel.CurrentPositionSeconds > 0.25 &&
                        player.PlaybackSession.CanSeek)
                    {
                        player.PlaybackSession.Position = TimeSpan.FromSeconds(ViewModel.CurrentPositionSeconds);
                    }

                    SyncFullscreenVideoPlaybackRate(player);

                    if (ViewModel.IsFullscreenVideoMode)
                    {
                        SetMusicVideoContainersOpacity(1.0);

                        if (ViewModel.IsPlaying)
                        {
                            player.Play();
                        }
                    }
                }
                catch
                {
                    // Ignore transient session seek error during media open
                }
            });
        };

        _fullscreenMusicVideoPlayer.MediaFailed += (player, args) =>
        {
            int failedGen = Volatile.Read(ref _musicVideoLoadGeneration);
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (failedGen != Volatile.Read(ref _musicVideoLoadGeneration))
                {
                    return;
                }

                SetMusicVideoContainersOpacity(0.0);

                _loadedFullscreenMusicVideoUri = null;
                ReplaceMediaSource(_fullscreenMusicVideoPlayer, null);
                if (ViewModel.IsFullscreenVideoMode)
                {
                    await ViewModel.HandleMusicVideoPlaybackFailureAsync();
                }
            });
        };

        AttachActiveMusicVideoPlayerElement();
    }

    private void SetMusicVideoContainersOpacity(double opacity)
    {
        if (FullscreenMusicVideoContainer is not null)
        {
            FullscreenMusicVideoContainer.Opacity = opacity;
        }

        if (SidebarMusicVideoContainer is not null)
        {
            SidebarMusicVideoContainer.Opacity = opacity;
        }
    }

    private void AttachActiveMusicVideoPlayerElement()
    {
        if (_fullscreenMusicVideoPlayer is null)
        {
            return;
        }

        if (ViewModel.IsFullscreenNowPlayingOpen && FullscreenMusicVideoPlayerElement is not null)
        {
            try { SidebarMusicVideoPlayerElement?.SetMediaPlayer(null); } catch { }
            FullscreenMusicVideoPlayerElement.SetMediaPlayer(_fullscreenMusicVideoPlayer);
        }
        else if (SidebarMusicVideoPlayerElement is not null)
        {
            try { FullscreenMusicVideoPlayerElement?.SetMediaPlayer(null); } catch { }
            SidebarMusicVideoPlayerElement.SetMediaPlayer(_fullscreenMusicVideoPlayer);
        }
    }

    private void OnSidebarMusicVideoPlayerElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_fullscreenMusicVideoPlayer is not null && !ViewModel.IsFullscreenNowPlayingOpen &&
            sender is Microsoft.UI.Xaml.Controls.MediaPlayerElement element)
        {
            element.SetMediaPlayer(_fullscreenMusicVideoPlayer);
        }
    }

    private void OnSidebarMusicVideoPlayerElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MediaPlayerElement element)
        {
            try { element.SetMediaPlayer(null); } catch { }
        }
    }

    private void OnFullscreenMusicVideoPlayerElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_fullscreenMusicVideoPlayer is not null && sender is Microsoft.UI.Xaml.Controls.MediaPlayerElement element)
        {
            try { SidebarMusicVideoPlayerElement?.SetMediaPlayer(null); } catch { }
            element.SetMediaPlayer(_fullscreenMusicVideoPlayer);
        }
    }

    private void OnFullscreenMusicVideoPlayerElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MediaPlayerElement element)
        {
            try
            {
                element.SetMediaPlayer(null);
                if (_fullscreenMusicVideoPlayer is not null && ViewModel.IsFullscreenVideoMode)
                {
                    SidebarMusicVideoPlayerElement?.SetMediaPlayer(_fullscreenMusicVideoPlayer);
                }
            }
            catch
            {
            }
        }
    }

    private void DisposeFullscreenMusicVideoResources()
    {
        Interlocked.Increment(ref _musicVideoLoadGeneration);
        _loadedFullscreenMusicVideoUri = null;
        try { FullscreenMusicVideoPlayerElement?.SetMediaPlayer(null); } catch { }
        try { SidebarMusicVideoPlayerElement?.SetMediaPlayer(null); } catch { }
        DisposeMediaPlayerSafe(ref _fullscreenMusicVideoPlayer);
    }

    private void SyncFullscreenVideoPlaybackRate(Windows.Media.Playback.MediaPlayer player)
    {
        try
        {
            string badge = ViewModel.EffectiveSpeedBadgeText ?? "1.00x";
            string trimmed = badge.Trim().TrimEnd('x', 'X');
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double targetRate))
            {
                double clamped = Math.Clamp(targetRate, 0.25, 2.5);
                if (Math.Abs(player.PlaybackSession.PlaybackRate - clamped) > 0.02)
                {
                    player.PlaybackSession.PlaybackRate = clamped;
                }
            }
        }
        catch
        {
            // Ignore playback rate errors on streams that disallow variable rates
        }
    }

    private void OnViewModelMusicVideoStateChanged(object? sender, (string? StreamUri, bool IsVideoModeActive) state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplyFullscreenMusicVideoStateAsync(state.StreamUri, state.IsVideoModeActive);
        });
    }

    private async Task ApplyFullscreenMusicVideoStateAsync(string? streamUri, bool isVideoModeActive)
    {
        if (FullscreenMusicVideoContainer is null && SidebarMusicVideoContainer is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(streamUri) || !isVideoModeActive)
        {
            Interlocked.Increment(ref _musicVideoLoadGeneration);
            SetMusicVideoContainersOpacity(0.0);
            _fullscreenMusicVideoPlayer?.Pause();
            _loadedFullscreenMusicVideoUri = null;
            ReplaceMediaSource(_fullscreenMusicVideoPlayer, null);
            ResumeTrackCanvasIfNeeded();
            return;
        }

        PauseTrackCanvasForVideoMode();

        int generation = Interlocked.Increment(ref _musicVideoLoadGeneration);
        try
        {
            EnsureFullscreenMusicVideoPlayerCreated();
            if (_fullscreenMusicVideoPlayer is null)
            {
                return;
            }

            AttachActiveMusicVideoPlayerElement();

            if (!string.Equals(_loadedFullscreenMusicVideoUri, streamUri, StringComparison.OrdinalIgnoreCase))
            {
                _loadedFullscreenMusicVideoUri = streamUri;
                Uri resolvedUri = File.Exists(streamUri)
                    ? new Uri(Path.GetFullPath(streamUri))
                    : new Uri(streamUri);

                bool isHls = !File.Exists(streamUri) &&
                             (streamUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                              streamUri.Contains("hls_variant", StringComparison.OrdinalIgnoreCase) ||
                              streamUri.Contains("manifest/hls", StringComparison.OrdinalIgnoreCase));

                if (isHls)
                {
                    var amsResult = await Windows.Media.Streaming.Adaptive.AdaptiveMediaSource.CreateFromUriAsync(resolvedUri);
                    if (generation != Volatile.Read(ref _musicVideoLoadGeneration))
                    {
                        return;
                    }

                    if (amsResult.Status == Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceCreationStatus.Success &&
                        amsResult.MediaSource is not null)
                    {
                        ReplaceMediaSource(
                            _fullscreenMusicVideoPlayer,
                            Windows.Media.Core.MediaSource.CreateFromAdaptiveMediaSource(amsResult.MediaSource));
                    }
                    else
                    {
                        _loadedFullscreenMusicVideoUri = null;
                        ReplaceMediaSource(_fullscreenMusicVideoPlayer, null);
                        await ViewModel.HandleMusicVideoPlaybackFailureAsync();
                        return;
                    }
                }
                else
                {
                    ReplaceMediaSource(
                        _fullscreenMusicVideoPlayer,
                        Windows.Media.Core.MediaSource.CreateFromUri(resolvedUri));
                }
            }

            if (_fullscreenMusicVideoPlayer.PlaybackSession.CanSeek)
            {
                _fullscreenMusicVideoPlayer.PlaybackSession.Position =
                    TimeSpan.FromSeconds(Math.Max(0.0, ViewModel.CurrentPositionSeconds));
            }

            SyncFullscreenVideoPlaybackRate(_fullscreenMusicVideoPlayer);
            SetMusicVideoContainersOpacity(1.0);

            if (ViewModel.IsPlaying)
            {
                _fullscreenMusicVideoPlayer.Play();
            }
            else
            {
                _fullscreenMusicVideoPlayer.Pause();
            }
        }
        catch
        {
            SetMusicVideoContainersOpacity(0.0);

            _loadedFullscreenMusicVideoUri = null;
            ReplaceMediaSource(_fullscreenMusicVideoPlayer, null);
            if (ViewModel.IsFullscreenVideoMode)
            {
                await ViewModel.HandleMusicVideoPlaybackFailureAsync();
            }
        }
    }

    private void SyncFullscreenMusicVideoPositionAndPlayState()
    {
        if (!ViewModel.IsFullscreenVideoMode ||
            _fullscreenMusicVideoPlayer is null ||
            string.IsNullOrWhiteSpace(_loadedFullscreenMusicVideoUri))
        {
            return;
        }

        try
        {
            var session = _fullscreenMusicVideoPlayer.PlaybackSession;
            double targetSec = ViewModel.CurrentPositionSeconds;
            double videoSec = session.Position.TotalSeconds;

            if (session.CanSeek &&
                session.NaturalDuration.TotalSeconds > 1.0 &&
                targetSec < session.NaturalDuration.TotalSeconds)
            {
                long nowTickMs = Environment.TickCount64;
                if (Math.Abs(videoSec - targetSec) > 0.22 &&
                    nowTickMs - _lastMusicVideoSeekTickMs >= 600)
                {
                    _lastMusicVideoSeekTickMs = nowTickMs;
                    session.Position = TimeSpan.FromSeconds(targetSec);
                }
            }

            SyncFullscreenVideoPlaybackRate(_fullscreenMusicVideoPlayer);

            var playbackState = session.PlaybackState;
            if (ViewModel.IsPlaying &&
                playbackState != Windows.Media.Playback.MediaPlaybackState.Playing &&
                playbackState != Windows.Media.Playback.MediaPlaybackState.Opening &&
                playbackState != Windows.Media.Playback.MediaPlaybackState.Buffering)
            {
                _fullscreenMusicVideoPlayer.Play();
            }
            else if (!ViewModel.IsPlaying &&
                     playbackState == Windows.Media.Playback.MediaPlaybackState.Playing)
            {
                _fullscreenMusicVideoPlayer.Pause();
            }
        }
        catch
        {
            // Ignore transient session synchronization errors
        }
    }

    private async void OnArtistLatestReleaseCardClicked(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _lastRowSubButtonClickTick < 260)
        {
            return;
        }

        _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
        await ViewModel.OpenArtistLatestReleaseAsync();
    }

    private async void OnArtistLatestReleasePlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        await ViewModel.PlayArtistLatestReleaseAsync();
    }

    private async void OnArtistOfficialVideoCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrackItem videoTrack })
        {
            await ViewModel.PlayArtistOfficialVideoAsync(videoTrack);
        }
    }

    private async void OnSmartPlaylistCardClicked(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _lastRowSubButtonClickTick < 260)
        {
            return;
        }

        UserPlaylist? playlist = sender switch
        {
            FrameworkElement { Tag: PlaylistCardViewModel card } => card.Playlist,
            FrameworkElement { Tag: UserPlaylist pl } => pl,
            _ => null
        };

        if (playlist is not null)
        {
            _ = CatalogScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
            await ViewModel.OpenPlaylistDetailAsync(playlist);
        }
    }

    private async void OnSmartPlaylistQuickPlayClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        UserPlaylist? playlist = sender switch
        {
            FrameworkElement { Tag: UserPlaylist pl } => pl,
            FrameworkElement { Tag: PlaylistCardViewModel card } => card.Playlist,
            _ => null
        };

        if (playlist is not null)
        {
            await ViewModel.PlayPlaylistDirectlyAsync(playlist);
        }
    }

    private async void OnSmartPlaylistSaveToLibraryClicked(object sender, RoutedEventArgs e)
    {
        _lastRowSubButtonClickTick = Environment.TickCount64;
        UserPlaylist? playlist = sender switch
        {
            FrameworkElement { Tag: UserPlaylist pl } => pl,
            FrameworkElement { Tag: PlaylistCardViewModel card } => card.Playlist,
            _ => ViewModel.CurrentPlaylistDetail
        };

        if (playlist is not null)
        {
            await ViewModel.SaveSmartPlaylistToLibraryAsync(playlist);
        }
    }
}
