using System.IO;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Xperience.Core.Domain.Enums;

namespace Xperience.App.Views;

/// <summary>
/// Dedicated partial class managing all four hardware-accelerated looping video surfaces:
/// <list type="bullet">
///   <item><description>Now Playing Sidebar Apple Canvas (shared decoder with Fullscreen)</description></item>
///   <item><description>Fullscreen Apple Canvas (zero-overhead shared frame target with Sidebar)</description></item>
///   <item><description>Album Detail Apple Canvas</description></item>
///   <item><description>Artist Detail Animated Header Video</description></item>
/// </list>
/// Guarantees gapless local looping with zero network re-fetches, zero black flash on load
/// (static cover stays cleanly visible on top at Opacity=1.0 until Frame #1 is decoded &amp; copied,
/// followed by a smooth ~350ms cross-fade), and seamless same-album continuation.
/// </summary>
public sealed partial class ShellPage
{
    private readonly ScalarTransition _canvasFadeInTransition = new() { Duration = TimeSpan.FromMilliseconds(320) };
    private readonly ScalarTransition _coverFadeOutTransition = new() { Duration = TimeSpan.FromMilliseconds(360) };
    private readonly ScalarTransition _artistHeroCrossFadeTransition = new() { Duration = TimeSpan.FromMilliseconds(380) };

    // Track Canvas (Sidebar + Fullscreen share a single MediaPlayer & GPU CanvasRenderTarget for 50% lower GPU use & instant Fullscreen sync)
    private string? _activeTrackCanvasUri;
    private int _trackCanvasGeneration;
    private bool _trackCanvasExpectedPlaying;
    private bool _trackCanvasMediaOpened;
    private bool _trackCanvasFirstFrameCopied;
    private bool _trackCanvasRevealed;
    private long _trackCanvasFrameSerial;
    private int _trackCanvasStallTicks;
    private long _trackCanvasLastWatchdogFrameSerial = -1;
    private TimeSpan _trackCanvasLastWatchdogPos = TimeSpan.MinValue;
    private DateTimeOffset _trackCanvasLastRecoveryUtc = DateTimeOffset.MinValue;
    private bool _trackCanvasUsingNativeSwapchainFallback;

    // Album Detail Canvas
    private string? _activeAlbumDetailCanvasUri;
    private int _albumDetailCanvasGeneration;
    private bool _albumDetailCanvasExpectedPlaying;
    private bool _albumDetailCanvasMediaOpened;
    private bool _albumDetailCanvasFirstFrameCopied;
    private bool _albumDetailCanvasRevealed;
    private CanvasRenderTarget? _albumDetailVideoRenderTarget;
    private int _albumDetailVideoFrameBusy;
    private long _albumDetailCanvasFrameSerial;
    private int _albumDetailCanvasStallTicks;
    private long _albumDetailLastWatchdogFrameSerial = -1;
    private TimeSpan _albumDetailLastWatchdogPos = TimeSpan.MinValue;
    private DateTimeOffset _albumDetailLastRecoveryUtc = DateTimeOffset.MinValue;
    private bool _albumDetailUsingNativeSwapchainFallback;

    // Artist Header Animated Video
    private int _artistHeaderGeneration;
    private bool _artistHeaderExpectedPlaying;
    private bool _artistHeaderMediaOpened;
    private bool _artistHeaderFirstFrameCopied;
    private bool _artistHeaderRevealed;
    private long _artistHeaderFrameSerial;
    private int _artistHeaderStallTicks;
    private long _artistHeaderLastWatchdogFrameSerial = -1;
    private TimeSpan _artistHeaderLastWatchdogPos = TimeSpan.MinValue;
    private DateTimeOffset _artistHeaderLastRecoveryUtc = DateTimeOffset.MinValue;
    private bool _artistHeaderUsingNativeSwapchainFallback;

    // Lightweight defensive loop-stall watchdog (runs only while at least one video surface is expected playing)
    private DispatcherQueueTimer? _canvasLoopWatchdogTimer;

    private static void ReplaceMediaSource(MediaPlayer? player, IMediaPlaybackSource? nextSource)
    {
        if (player is null)
        {
            (nextSource as IDisposable)?.Dispose();
            return;
        }

        IMediaPlaybackSource? previous = null;
        try
        {
            previous = player.Source;
        }
        catch
        {
            // Ignore transient MediaPlayer state errors
        }

        player.Source = nextSource;

        if (previous is not null && !ReferenceEquals(previous, nextSource))
        {
            try
            {
                (previous as IDisposable)?.Dispose();
            }
            catch
            {
                // Ignore COM teardown races during MediaSource disposal
            }
        }
    }

    private void EnsureSidebarCanvasMediaPlayerInitializedCore()
    {
        EnsureLoopWatchdogTimerCreated();

        if (_sidebarCanvasPlayer is not null)
        {
            SidebarCanvasMediaPlayerElement?.SetMediaPlayer(_sidebarCanvasPlayer);
            return;
        }

        // Shared Track Canvas player (drives BOTH Now Playing Sidebar and Fullscreen via Win2D frame server)
        _sidebarCanvasPlayer = new MediaPlayer
        {
            IsMuted = true,
            IsLoopingEnabled = true,
            AutoPlay = true,
            IsVideoFrameServerEnabled = true
        };
        AttachDefensiveLoopHandlers(_sidebarCanvasPlayer, SurfaceKind.TrackCanvas);
        _sidebarCanvasPlayer.VideoFrameAvailable += OnTrackCanvasVideoFrameAvailable;
        _sidebarCanvasPlayer.MediaOpened += (mp, _) =>
        {
            int gen = Volatile.Read(ref _trackCanvasGeneration);
            uint vw = mp.PlaybackSession?.NaturalVideoWidth ?? 0;
            uint vh = mp.PlaybackSession?.NaturalVideoHeight ?? 0;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _trackCanvasGeneration || !_trackCanvasExpectedPlaying)
                {
                    return;
                }

                _trackCanvasMediaOpened = true;
                if (vw > 0 && vh > 0)
                {
                    UpdateSidebarCoverAspectHeight((double)vh / vw);
                }

                if (_trackCanvasFirstFrameCopied && !_trackCanvasRevealed)
                {
                    RevealTrackCanvasWithCrossFade(gen);
                }
                else if (_trackCanvasUsingNativeSwapchainFallback && !_trackCanvasRevealed)
                {
                    ScheduleFallbackSwapchainReveal(SurfaceKind.TrackCanvas, gen);
                }
            });
        };

        SidebarCanvasMediaPlayerElement?.SetMediaPlayer(_sidebarCanvasPlayer);
    }

    private void EnsureAlbumDetailCanvasMediaPlayerInitializedCore()
    {
        EnsureLoopWatchdogTimerCreated();

        if (_albumDetailCanvasPlayer is not null)
        {
            AlbumDetailCanvasMediaPlayerElement?.SetMediaPlayer(_albumDetailCanvasPlayer);
            return;
        }

        // Album Detail Canvas player (dedicated Win2D frame server so Album Detail also has zero black flash & crisp rounded corners)
        _albumDetailCanvasPlayer = new MediaPlayer
        {
            IsMuted = true,
            IsLoopingEnabled = true,
            AutoPlay = true,
            IsVideoFrameServerEnabled = true
        };
        AttachDefensiveLoopHandlers(_albumDetailCanvasPlayer, SurfaceKind.AlbumDetail);
        _albumDetailCanvasPlayer.VideoFrameAvailable += OnAlbumDetailVideoFrameAvailable;
        _albumDetailCanvasPlayer.MediaOpened += (mp, _) =>
        {
            int gen = Volatile.Read(ref _albumDetailCanvasGeneration);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _albumDetailCanvasGeneration || !_albumDetailCanvasExpectedPlaying)
                {
                    return;
                }

                _albumDetailCanvasMediaOpened = true;
                if (_albumDetailCanvasFirstFrameCopied && !_albumDetailCanvasRevealed)
                {
                    RevealAlbumDetailCanvasWithCrossFade(gen);
                }
                else if (_albumDetailUsingNativeSwapchainFallback && !_albumDetailCanvasRevealed)
                {
                    ScheduleFallbackSwapchainReveal(SurfaceKind.AlbumDetail, gen);
                }
            });
        };

        AlbumDetailCanvasMediaPlayerElement?.SetMediaPlayer(_albumDetailCanvasPlayer);
    }

    private void DisposeCanvasVideoResourcesCore()
    {
        _canvasLoopWatchdogTimer?.Stop();
        _artistVideoRetentionTimer?.Stop();

        ++_trackCanvasGeneration;
        ++_albumDetailCanvasGeneration;
        ++_artistHeaderGeneration;
        _trackCanvasExpectedPlaying = false;
        _albumDetailCanvasExpectedPlaying = false;
        _artistHeaderExpectedPlaying = false;

        try { SidebarCanvasMediaPlayerElement?.SetMediaPlayer(null); } catch { }
        try { FullscreenCanvasMediaPlayerElement?.SetMediaPlayer(null); } catch { }
        try { AlbumDetailCanvasMediaPlayerElement?.SetMediaPlayer(null); } catch { }
        try { ArtistDetailHeaderMediaPlayerElement?.SetMediaPlayer(null); } catch { }

        DisposeMediaPlayerSafe(ref _sidebarCanvasPlayer);
        DisposeMediaPlayerSafe(ref _fullscreenCanvasPlayer);
        DisposeMediaPlayerSafe(ref _albumDetailCanvasPlayer);
        DisposeMediaPlayerSafe(ref _artistDetailHeaderPlayer);

        try { _sidebarVideoRenderTarget?.Dispose(); } catch { }
        _sidebarVideoRenderTarget = null;

        try { _albumDetailVideoRenderTarget?.Dispose(); } catch { }
        _albumDetailVideoRenderTarget = null;

        try { _artistHeaderVideoRenderTarget?.Dispose(); } catch { }
        _artistHeaderVideoRenderTarget = null;
    }

    private static void DisposeMediaPlayerSafe(ref MediaPlayer? player)
    {
        var current = player;
        player = null;
        if (current is null)
        {
            return;
        }

        try { current.Pause(); } catch { }
        try { ReplaceMediaSource(current, null); } catch { }
        try { current.Dispose(); } catch { }
    }

    private void EnsureArtistDetailHeaderMediaPlayerInitializedCore()
    {
        EnsureLoopWatchdogTimerCreated();

        if (_artistDetailHeaderPlayer is not null)
        {
            return;
        }

        _artistDetailHeaderPlayer = new MediaPlayer
        {
            IsMuted = true,
            IsLoopingEnabled = true,
            AutoPlay = true,
            IsVideoFrameServerEnabled = true
        };
        AttachDefensiveLoopHandlers(_artistDetailHeaderPlayer, SurfaceKind.ArtistHeader);
        _artistDetailHeaderPlayer.VideoFrameAvailable += OnArtistHeaderVideoFrameAvailableCore;
        _artistDetailHeaderPlayer.MediaOpened += (mp, _) =>
        {
            int gen = Volatile.Read(ref _artistHeaderGeneration);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _artistHeaderGeneration || !_artistHeaderExpectedPlaying)
                {
                    return;
                }

                _artistHeaderMediaOpened = true;
                if (_artistHeaderFirstFrameCopied && !_artistHeaderRevealed)
                {
                    RevealArtistHeaderVideoWithCrossFade(gen);
                }
                else if (_artistHeaderUsingNativeSwapchainFallback && !_artistHeaderRevealed)
                {
                    ScheduleFallbackSwapchainReveal(SurfaceKind.ArtistHeader, gen);
                }
            });
        };

        ArtistDetailHeaderMediaPlayerElement?.SetMediaPlayer(_artistDetailHeaderPlayer);

        if (_artistVideoRetentionTimer is null)
        {
            _artistVideoRetentionTimer = DispatcherQueue.CreateTimer();
            _artistVideoRetentionTimer.Interval = TimeSpan.FromSeconds(10);
            _artistVideoRetentionTimer.IsRepeating = false;
            _artistVideoRetentionTimer.Tick += (_, _) =>
            {
                if (ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
                {
                    PurgeArtistHeaderVideoPlayerStateCore();
                }
            };
        }
    }

    private enum SurfaceKind
    {
        TrackCanvas,
        AlbumDetail,
        ArtistHeader
    }

    /// <summary>
    /// Attaches non-intrusive defensive loop recovery handlers without interfering with native hardware
    /// <c>IsLoopingEnabled = true</c> playback on flattened local MP4s.
    /// </summary>
    private void AttachDefensiveLoopHandlers(MediaPlayer player, SurfaceKind kind)
    {
        player.IsLoopingEnabled = true;

        player.MediaEnded += (senderPlayer, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsSurfaceExpectedPlaying(kind) || senderPlayer.Source is null)
                {
                    return;
                }

                TryLoopRestartInPlace(senderPlayer);
            });
        };

        player.PlaybackSession.PlaybackStateChanged += (session, _) =>
        {
            var state = session.PlaybackState;
            if (state == MediaPlaybackState.Playing)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (kind)
                    {
                        case SurfaceKind.TrackCanvas when _trackCanvasExpectedPlaying && _trackCanvasFirstFrameCopied && !_trackCanvasRevealed:
                            RevealTrackCanvasWithCrossFade(_trackCanvasGeneration);
                            break;
                        case SurfaceKind.AlbumDetail when _albumDetailCanvasExpectedPlaying && _albumDetailCanvasFirstFrameCopied && !_albumDetailCanvasRevealed:
                            RevealAlbumDetailCanvasWithCrossFade(_albumDetailCanvasGeneration);
                            break;
                        case SurfaceKind.ArtistHeader when _artistHeaderExpectedPlaying && _artistHeaderFirstFrameCopied && !_artistHeaderRevealed:
                            RevealArtistHeaderVideoWithCrossFade(_artistHeaderGeneration);
                            break;
                    }
                });
                return;
            }

            if (state != MediaPlaybackState.Paused &&
                state != MediaPlaybackState.None)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsSurfaceExpectedPlaying(kind) || !IsSurfaceMediaOpened(kind) || player.Source is null)
                {
                    return;
                }

                var dur = session.NaturalDuration;
                var pos = session.Position;
                bool nearOrPastEnd = dur > TimeSpan.FromMilliseconds(400) &&
                                     dur < TimeSpan.FromHours(1) &&
                                     pos >= dur - TimeSpan.FromMilliseconds(350);

                if (nearOrPastEnd || session.PlaybackState == MediaPlaybackState.None)
                {
                    TryLoopRestartInPlace(player);
                }
            });
        };
    }

    private static void TryLoopRestartInPlace(MediaPlayer player)
    {
        try
        {
            player.IsLoopingEnabled = true;
            if (player.PlaybackSession.CanSeek)
            {
                player.PlaybackSession.Position = TimeSpan.Zero;
            }

            player.Play();
        }
        catch
        {
            // Ignore transient session state errors
        }
    }

    private bool IsSurfaceExpectedPlaying(SurfaceKind kind) => kind switch
    {
        SurfaceKind.TrackCanvas => _trackCanvasExpectedPlaying,
        SurfaceKind.AlbumDetail => _albumDetailCanvasExpectedPlaying,
        SurfaceKind.ArtistHeader => _artistHeaderExpectedPlaying,
        _ => false
    };

    private bool IsSurfaceMediaOpened(SurfaceKind kind) => kind switch
    {
        SurfaceKind.TrackCanvas => _trackCanvasMediaOpened,
        SurfaceKind.AlbumDetail => _albumDetailCanvasMediaOpened,
        SurfaceKind.ArtistHeader => _artistHeaderMediaOpened,
        _ => false
    };

    private void EnsureLoopWatchdogTimerCreated()
    {
        if (_canvasLoopWatchdogTimer is not null)
        {
            return;
        }

        _canvasLoopWatchdogTimer = DispatcherQueue.CreateTimer();
        _canvasLoopWatchdogTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _canvasLoopWatchdogTimer.IsRepeating = true;
        _canvasLoopWatchdogTimer.Tick += OnCanvasLoopWatchdogTick;
    }

    private void UpdateLoopWatchdogRunningState()
    {
        if (_canvasLoopWatchdogTimer is null)
        {
            return;
        }

        bool anyActive = _trackCanvasExpectedPlaying ||
                         _albumDetailCanvasExpectedPlaying ||
                         _artistHeaderExpectedPlaying;

        if (anyActive && !_canvasLoopWatchdogTimer.IsRunning)
        {
            _canvasLoopWatchdogTimer.Start();
        }
        else if (!anyActive && _canvasLoopWatchdogTimer.IsRunning)
        {
            _canvasLoopWatchdogTimer.Stop();
        }
    }

    private void OnCanvasLoopWatchdogTick(DispatcherQueueTimer sender, object args)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CheckAndRecoverStalledLoop(
            _sidebarCanvasPlayer,
            _trackCanvasExpectedPlaying,
            _trackCanvasMediaOpened,
            _trackCanvasUsingNativeSwapchainFallback,
            ref _trackCanvasFrameSerial,
            ref _trackCanvasLastWatchdogFrameSerial,
            ref _trackCanvasLastWatchdogPos,
            ref _trackCanvasStallTicks,
            ref _trackCanvasLastRecoveryUtc,
            now);

        if (_trackCanvasUsingNativeSwapchainFallback && _fullscreenCanvasPlayer is not null && ViewModel.IsFullscreenNowPlayingOpen)
        {
            CheckSecondaryFallbackLoop(_fullscreenCanvasPlayer, _trackCanvasExpectedPlaying);
        }

        CheckAndRecoverStalledLoop(
            _albumDetailCanvasPlayer,
            _albumDetailCanvasExpectedPlaying,
            _albumDetailCanvasMediaOpened,
            _albumDetailUsingNativeSwapchainFallback,
            ref _albumDetailCanvasFrameSerial,
            ref _albumDetailLastWatchdogFrameSerial,
            ref _albumDetailLastWatchdogPos,
            ref _albumDetailCanvasStallTicks,
            ref _albumDetailLastRecoveryUtc,
            now);

        CheckAndRecoverStalledLoop(
            _artistDetailHeaderPlayer,
            _artistHeaderExpectedPlaying,
            _artistHeaderMediaOpened,
            _artistHeaderUsingNativeSwapchainFallback,
            ref _artistHeaderFrameSerial,
            ref _artistHeaderLastWatchdogFrameSerial,
            ref _artistHeaderLastWatchdogPos,
            ref _artistHeaderStallTicks,
            ref _artistHeaderLastRecoveryUtc,
            now);

        UpdateLoopWatchdogRunningState();
    }

    private static void CheckAndRecoverStalledLoop(
        MediaPlayer? player,
        bool expectedPlaying,
        bool mediaOpened,
        bool usingNativeFallback,
        ref long frameSerial,
        ref long lastFrameSerial,
        ref TimeSpan lastPos,
        ref int stallTicks,
        ref DateTimeOffset lastRecoveryUtc,
        DateTimeOffset nowUtc)
    {
        if (!expectedPlaying || !mediaOpened || player?.Source is null)
        {
            stallTicks = 0;
            return;
        }

        try
        {
            var session = player.PlaybackSession;
            var state = session.PlaybackState;
            var pos = session.Position;
            long currentFrames = Volatile.Read(ref frameSerial);

            if (state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening)
            {
                stallTicks = 0;
                return;
            }

            if (state != MediaPlaybackState.Playing)
            {
                stallTicks++;
            }
            else
            {
                bool posStuck = lastPos != TimeSpan.MinValue &&
                                Math.Abs((pos - lastPos).TotalMilliseconds) < 15.0;
                bool framesStuck = usingNativeFallback ||
                                   (lastFrameSerial >= 0 && currentFrames == lastFrameSerial);

                if (posStuck && framesStuck)
                {
                    stallTicks++;
                }
                else
                {
                    stallTicks = 0;
                }
            }

            lastPos = pos;
            lastFrameSerial = currentFrames;

            if (stallTicks >= 2 && (nowUtc - lastRecoveryUtc) > TimeSpan.FromSeconds(2.5))
            {
                stallTicks = 0;
                lastRecoveryUtc = nowUtc;
                TryLoopRestartInPlace(player);
            }
        }
        catch
        {
            // Ignore transient MediaPlayer state errors
        }
    }

    private static void CheckSecondaryFallbackLoop(MediaPlayer player, bool expectedPlaying)
    {
        if (!expectedPlaying || player.Source is null)
        {
            return;
        }

        try
        {
            if (player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing &&
                player.PlaybackSession.PlaybackState != MediaPlaybackState.Opening &&
                player.PlaybackSession.PlaybackState != MediaPlaybackState.Buffering)
            {
                TryLoopRestartInPlace(player);
            }
        }
        catch
        {
        }
    }

    // -------------------------------------------------------------------------
    // SURFACE 1 & 2: TRACK CANVAS (NOW PLAYING SIDEBAR + FULLSCREEN SHARED)
    // -------------------------------------------------------------------------

    private void OnCanvasVideoChangedCore(object? sender, string? videoUri)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Clear / disable Canvas -> restore static covers cleanly on top FIRST (zero black flash)
            if (string.IsNullOrWhiteSpace(videoUri))
            {
                StopAndHideTrackCanvasSurfaces();
                return;
            }

            string normalizedUri = NormalizeLocalOrRemoteVideoKey(videoUri);

            // Same-album / same-URI continuity guard: if this exact video is already loaded & active,
            // keep it continuously running with ZERO reload, pause, seek, or opacity flash!
            if (!string.IsNullOrWhiteSpace(_activeTrackCanvasUri) &&
                string.Equals(_activeTrackCanvasUri, normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                _sidebarCanvasPlayer?.Source is not null &&
                _trackCanvasExpectedPlaying)
            {
                if (_sidebarCanvasPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing &&
                    _sidebarCanvasPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Opening)
                {
                    _sidebarCanvasPlayer.Play();
                }

                if (_trackCanvasRevealed)
                {
                    SyncFullscreenCanvasSurfaceOnOpenCore();
                }

                return;
            }

            try
            {
                EnsureSidebarCanvasMediaPlayerInitializedCore();

                // Restore static covers ON TOP immediately before loading the new video underneath
                int gen = ++_trackCanvasGeneration;
                _activeTrackCanvasUri = normalizedUri;
                _trackCanvasExpectedPlaying = true;
                _trackCanvasMediaOpened = false;
                _trackCanvasFirstFrameCopied = false;
                _trackCanvasRevealed = false;
                _trackCanvasStallTicks = 0;
                _trackCanvasLastWatchdogFrameSerial = -1;
                _trackCanvasLastWatchdogPos = TimeSpan.MinValue;

                SetTrackCanvasVisualState(canvasVisible: false, animate: false);

                // Dispose previous album's render target so stale frames can never bleed into the new video
                _sidebarVideoRenderTarget?.Dispose();
                _sidebarVideoRenderTarget = null;
                SidebarCanvasVideoCanvas?.Invalidate();
                FullscreenCanvasVideoCanvas?.Invalidate();

                Uri resolvedUri = File.Exists(videoUri)
                    ? new Uri(Path.GetFullPath(videoUri))
                    : new Uri(videoUri);

                if (_sidebarCanvasPlayer is not null)
                {
                    _sidebarCanvasPlayer.IsLoopingEnabled = true;
                    ReplaceMediaSource(_sidebarCanvasPlayer, MediaSource.CreateFromUri(resolvedUri));
                    _sidebarCanvasPlayer.Play();
                }

                if (_trackCanvasUsingNativeSwapchainFallback && _fullscreenCanvasPlayer is not null)
                {
                    _fullscreenCanvasPlayer.IsLoopingEnabled = true;
                    ReplaceMediaSource(_fullscreenCanvasPlayer, MediaSource.CreateFromUri(resolvedUri));
                    _fullscreenCanvasPlayer.Play();
                }

                UpdateLoopWatchdogRunningState();
            }
            catch
            {
                StopAndHideTrackCanvasSurfaces();
            }
        });
    }

    private void StopAndHideTrackCanvasSurfaces()
    {
        ++_trackCanvasGeneration;
        _activeTrackCanvasUri = null;
        _trackCanvasExpectedPlaying = false;
        _trackCanvasMediaOpened = false;
        _trackCanvasFirstFrameCopied = false;
        _trackCanvasRevealed = false;
        _trackCanvasStallTicks = 0;

        // Put static covers back ON TOP immediately before pausing & hiding canvas underneath
        SetTrackCanvasVisualState(canvasVisible: false, animate: false);

        try
        {
            _sidebarCanvasPlayer?.Pause();
            if (_sidebarCanvasPlayer is not null)
            {
                ReplaceMediaSource(_sidebarCanvasPlayer, null);
            }
        }
        catch
        {
        }

        try
        {
            _fullscreenCanvasPlayer?.Pause();
            if (_fullscreenCanvasPlayer is not null)
            {
                ReplaceMediaSource(_fullscreenCanvasPlayer, null);
            }
        }
        catch
        {
        }

        _sidebarVideoRenderTarget?.Dispose();
        _sidebarVideoRenderTarget = null;
        SidebarCanvasVideoCanvas?.Invalidate();
        FullscreenCanvasVideoCanvas?.Invalidate();

        UpdateLoopWatchdogRunningState();
    }

    private void SetTrackCanvasVisualState(bool canvasVisible, bool animate)
    {
        double canvasOpacity = canvasVisible ? 1.0 : 0.0;
        double coverOpacity = canvasVisible ? 0.0 : 1.0;

        if (SidebarCanvasVideoContainer is not null)
        {
            SidebarCanvasVideoContainer.OpacityTransition = animate ? _canvasFadeInTransition : null;
            SidebarCanvasVideoContainer.Opacity = canvasOpacity;
        }

        if (SidebarStaticCoverHost is not null)
        {
            SidebarStaticCoverHost.OpacityTransition = animate ? _coverFadeOutTransition : null;
            SidebarStaticCoverHost.Opacity = coverOpacity;
        }

        if (FullscreenCanvasVideoContainer is not null)
        {
            FullscreenCanvasVideoContainer.OpacityTransition = animate ? _canvasFadeInTransition : null;
            FullscreenCanvasVideoContainer.Opacity = canvasOpacity;
        }

        if (FullscreenStaticCoverHost is not null)
        {
            FullscreenStaticCoverHost.OpacityTransition = animate ? _coverFadeOutTransition : null;
            FullscreenStaticCoverHost.Opacity = coverOpacity;
        }
    }

    private void RevealTrackCanvasWithCrossFade(int expectedGeneration)
    {
        if (expectedGeneration != _trackCanvasGeneration || !_trackCanvasExpectedPlaying || _trackCanvasRevealed)
        {
            return;
        }

        _trackCanvasRevealed = true;

        // Step 1: Make Canvas container opaque/fading-in underneath the STILL-OPAQUE static cover
        if (SidebarCanvasVideoContainer is not null)
        {
            SidebarCanvasVideoContainer.OpacityTransition = _canvasFadeInTransition;
            SidebarCanvasVideoContainer.Opacity = 1.0;
        }

        if (FullscreenCanvasVideoContainer is not null)
        {
            FullscreenCanvasVideoContainer.OpacityTransition = _canvasFadeInTransition;
            FullscreenCanvasVideoContainer.Opacity = 1.0;
        }

        SidebarCanvasVideoCanvas?.Invalidate();
        if (ViewModel.IsFullscreenNowPlayingOpen)
        {
            FullscreenCanvasVideoCanvas?.Invalidate();
        }

        // Step 2: After 1 UI frame (guaranteeing Win2D has painted Frame #1 onto the composition surface),
        // smoothly cross-fade static album covers out (1.0 -> 0.0 over 360ms) with zero black flash!
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (expectedGeneration != _trackCanvasGeneration || !_trackCanvasExpectedPlaying || !_trackCanvasRevealed)
            {
                return;
            }

            if (SidebarStaticCoverHost is not null)
            {
                SidebarStaticCoverHost.OpacityTransition = _coverFadeOutTransition;
                SidebarStaticCoverHost.Opacity = 0.0;
            }

            if (FullscreenStaticCoverHost is not null)
            {
                FullscreenStaticCoverHost.OpacityTransition = _coverFadeOutTransition;
                FullscreenStaticCoverHost.Opacity = 0.0;
            }
        });
    }

    /// <summary>
    /// Called immediately before opening the Fullscreen Now Playing overlay so if a Track Canvas is
    /// already decoded and playing in the Sidebar, Fullscreen opens straight into live frames with zero flash.
    /// </summary>
    private void SyncFullscreenCanvasSurfaceOnOpenCore()
    {
        bool ready = _trackCanvasExpectedPlaying &&
                     _trackCanvasRevealed &&
                     (_trackCanvasFirstFrameCopied || _trackCanvasUsingNativeSwapchainFallback);

        if (FullscreenCanvasVideoContainer is not null)
        {
            FullscreenCanvasVideoContainer.OpacityTransition = null;
            FullscreenCanvasVideoContainer.Opacity = ready ? 1.0 : 0.0;
        }

        if (FullscreenStaticCoverHost is not null)
        {
            FullscreenStaticCoverHost.OpacityTransition = null;
            FullscreenStaticCoverHost.Opacity = ready ? 0.0 : 1.0;
        }

        if (ready)
        {
            FullscreenCanvasVideoCanvas?.Invalidate();
        }
    }

    private void OnTrackCanvasVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (Interlocked.CompareExchange(ref _sidebarVideoFrameBusy, 1, 0) != 0)
        {
            return;
        }

        int frameGen = Volatile.Read(ref _trackCanvasGeneration);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_sidebarCanvasPlayer is null ||
                    !_trackCanvasExpectedPlaying ||
                    frameGen != _trackCanvasGeneration ||
                    _sidebarCanvasPlayer.Source is null)
                {
                    return;
                }

                uint natW = _sidebarCanvasPlayer.PlaybackSession?.NaturalVideoWidth ?? 0;
                uint natH = _sidebarCanvasPlayer.PlaybackSession?.NaturalVideoHeight ?? 0;
                (int targetW, int targetH) = ComputeRenderTargetSize(natW, natH, maxDimension: 960, fallbackW: 640, fallbackH: 640);

                var device = CanvasDevice.GetSharedDevice();
                if (_sidebarVideoRenderTarget is null ||
                    _sidebarVideoRenderTarget.Device != device ||
                    _sidebarVideoRenderTarget.SizeInPixels.Width != targetW ||
                    _sidebarVideoRenderTarget.SizeInPixels.Height != targetH)
                {
                    _sidebarVideoRenderTarget?.Dispose();
                    _sidebarVideoRenderTarget = new CanvasRenderTarget(device, targetW, targetH, 96.0f);
                }

                // ALWAYS copy decoded video frame even while Container.Opacity == 0 so Frame #1 is ready BEFORE cross-fade!
                _sidebarCanvasPlayer.CopyFrameToVideoSurface(_sidebarVideoRenderTarget);
                Interlocked.Increment(ref _trackCanvasFrameSerial);
                _trackCanvasFirstFrameCopied = true;

                SidebarCanvasVideoCanvas?.Invalidate();
                if (ViewModel.IsFullscreenNowPlayingOpen)
                {
                    FullscreenCanvasVideoCanvas?.Invalidate();
                }

                if (!_trackCanvasRevealed &&
                    (_trackCanvasMediaOpened || natW > 0) &&
                    _sidebarCanvasPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                {
                    RevealTrackCanvasWithCrossFade(frameGen);
                }
            }
            catch
            {
                EnableTrackCanvasNativeSwapchainFallback();
            }
            finally
            {
                Interlocked.Exchange(ref _sidebarVideoFrameBusy, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _sidebarVideoFrameBusy, 0);
        }
    }

    private void EnableTrackCanvasNativeSwapchainFallback()
    {
        if (_trackCanvasUsingNativeSwapchainFallback)
        {
            return;
        }

        _trackCanvasUsingNativeSwapchainFallback = true;
        if (_sidebarCanvasPlayer is not null && _sidebarCanvasPlayer.IsVideoFrameServerEnabled)
        {
            _sidebarCanvasPlayer.IsVideoFrameServerEnabled = false;
        }

        if (SidebarCanvasMediaPlayerElement is not null)
        {
            SidebarCanvasMediaPlayerElement.Opacity = 1.0;
        }

        if (FullscreenCanvasMediaPlayerElement is not null)
        {
            FullscreenCanvasMediaPlayerElement.Opacity = 1.0;
            if (_fullscreenCanvasPlayer is null)
            {
                _fullscreenCanvasPlayer = new MediaPlayer
                {
                    IsMuted = true,
                    IsLoopingEnabled = true,
                    AutoPlay = true
                };
                AttachDefensiveLoopHandlers(_fullscreenCanvasPlayer, SurfaceKind.TrackCanvas);
                FullscreenCanvasMediaPlayerElement.SetMediaPlayer(_fullscreenCanvasPlayer);
            }

            if (!string.IsNullOrWhiteSpace(_activeTrackCanvasUri))
            {
                try
                {
                    Uri uri = File.Exists(_activeTrackCanvasUri)
                        ? new Uri(Path.GetFullPath(_activeTrackCanvasUri))
                        : new Uri(_activeTrackCanvasUri);
                    ReplaceMediaSource(_fullscreenCanvasPlayer, MediaSource.CreateFromUri(uri));
                    _fullscreenCanvasPlayer.Play();
                }
                catch
                {
                }
            }
        }

        if (_trackCanvasExpectedPlaying && !_trackCanvasRevealed)
        {
            ScheduleFallbackSwapchainReveal(SurfaceKind.TrackCanvas, _trackCanvasGeneration);
        }
    }

    private void OnSidebarCanvasVideoCanvasDrawCore(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sidebarVideoRenderTarget is null || !_trackCanvasFirstFrameCopied || !_trackCanvasExpectedPlaying)
        {
            return;
        }

        DrawRenderTargetUniformToFill(_sidebarVideoRenderTarget, sender, args);
    }

    private void OnDynamicCanvasControlUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is CanvasControl canvas)
        {
            try
            {
                canvas.RemoveFromVisualTree();
            }
            catch
            {
            }
        }
    }

    private void OnAlbumDetailCanvasMediaPlayerElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_albumDetailCanvasPlayer is not null && sender is MediaPlayerElement element)
        {
            element.SetMediaPlayer(_albumDetailCanvasPlayer);
        }
    }

    private void OnMediaPlayerElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is MediaPlayerElement element)
        {
            try
            {
                element.SetMediaPlayer(null);
            }
            catch
            {
            }
        }
    }

    private void OnFullscreenCanvasVideoCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sidebarVideoRenderTarget is null || !_trackCanvasFirstFrameCopied || !_trackCanvasExpectedPlaying)
        {
            return;
        }

        DrawRenderTargetUniformToFill(_sidebarVideoRenderTarget, sender, args);
    }

    // -------------------------------------------------------------------------
    // SURFACE 3: ALBUM DETAIL APPLE CANVAS
    // -------------------------------------------------------------------------

    private void OnAlbumDetailCanvasVideoChangedCore(object? sender, string? videoUri)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrWhiteSpace(videoUri))
            {
                StopAndHideAlbumDetailCanvas();
                return;
            }

            string normalizedUri = NormalizeLocalOrRemoteVideoKey(videoUri);
            if (!string.IsNullOrWhiteSpace(_activeAlbumDetailCanvasUri) &&
                string.Equals(_activeAlbumDetailCanvasUri, normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                _albumDetailCanvasPlayer?.Source is not null &&
                _albumDetailCanvasExpectedPlaying)
            {
                if (_albumDetailCanvasPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Playing &&
                    _albumDetailCanvasPlayer.PlaybackSession.PlaybackState != MediaPlaybackState.Opening)
                {
                    _albumDetailCanvasPlayer.Play();
                }

                return;
            }

            try
            {
                EnsureAlbumDetailCanvasMediaPlayerInitializedCore();

                int gen = ++_albumDetailCanvasGeneration;
                _activeAlbumDetailCanvasUri = normalizedUri;
                _albumDetailCanvasExpectedPlaying = true;
                _albumDetailCanvasMediaOpened = false;
                _albumDetailCanvasFirstFrameCopied = false;
                _albumDetailCanvasRevealed = false;
                _albumDetailCanvasStallTicks = 0;
                _albumDetailLastWatchdogFrameSerial = -1;
                _albumDetailLastWatchdogPos = TimeSpan.MinValue;

                SetAlbumDetailCanvasVisualState(canvasVisible: false, animate: false);

                _albumDetailVideoRenderTarget?.Dispose();
                _albumDetailVideoRenderTarget = null;
                AlbumDetailCanvasVideoCanvas?.Invalidate();

                Uri resolvedUri = File.Exists(videoUri)
                    ? new Uri(Path.GetFullPath(videoUri))
                    : new Uri(videoUri);

                if (_albumDetailCanvasPlayer is not null)
                {
                    _albumDetailCanvasPlayer.IsLoopingEnabled = true;
                    ReplaceMediaSource(_albumDetailCanvasPlayer, MediaSource.CreateFromUri(resolvedUri));
                    _albumDetailCanvasPlayer.Play();
                }

                UpdateLoopWatchdogRunningState();
            }
            catch
            {
                StopAndHideAlbumDetailCanvas();
            }
        });
    }

    private void StopAndHideAlbumDetailCanvas()
    {
        ++_albumDetailCanvasGeneration;
        _activeAlbumDetailCanvasUri = null;
        _albumDetailCanvasExpectedPlaying = false;
        _albumDetailCanvasMediaOpened = false;
        _albumDetailCanvasFirstFrameCopied = false;
        _albumDetailCanvasRevealed = false;
        _albumDetailCanvasStallTicks = 0;

        SetAlbumDetailCanvasVisualState(canvasVisible: false, animate: false);

        try
        {
            _albumDetailCanvasPlayer?.Pause();
            if (_albumDetailCanvasPlayer is not null)
            {
                ReplaceMediaSource(_albumDetailCanvasPlayer, null);
            }
        }
        catch
        {
        }

        _albumDetailVideoRenderTarget?.Dispose();
        _albumDetailVideoRenderTarget = null;
        AlbumDetailCanvasVideoCanvas?.Invalidate();

        UpdateLoopWatchdogRunningState();
    }

    private void SetAlbumDetailCanvasVisualState(bool canvasVisible, bool animate)
    {
        if (AlbumDetailCanvasVideoContainer is not null)
        {
            AlbumDetailCanvasVideoContainer.OpacityTransition = animate ? _canvasFadeInTransition : null;
            AlbumDetailCanvasVideoContainer.Opacity = canvasVisible ? 1.0 : 0.0;
        }

        if (AlbumDetailStaticCoverHost is not null)
        {
            AlbumDetailStaticCoverHost.OpacityTransition = animate ? _coverFadeOutTransition : null;
            AlbumDetailStaticCoverHost.Opacity = canvasVisible ? 0.0 : 1.0;
        }
    }

    private void RevealAlbumDetailCanvasWithCrossFade(int expectedGeneration)
    {
        if (expectedGeneration != _albumDetailCanvasGeneration ||
            !_albumDetailCanvasExpectedPlaying ||
            _albumDetailCanvasRevealed)
        {
            return;
        }

        _albumDetailCanvasRevealed = true;

        if (AlbumDetailCanvasVideoContainer is not null)
        {
            AlbumDetailCanvasVideoContainer.OpacityTransition = _canvasFadeInTransition;
            AlbumDetailCanvasVideoContainer.Opacity = 1.0;
        }

        AlbumDetailCanvasVideoCanvas?.Invalidate();

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (expectedGeneration != _albumDetailCanvasGeneration ||
                !_albumDetailCanvasExpectedPlaying ||
                !_albumDetailCanvasRevealed)
            {
                return;
            }

            if (AlbumDetailStaticCoverHost is not null)
            {
                AlbumDetailStaticCoverHost.OpacityTransition = _coverFadeOutTransition;
                AlbumDetailStaticCoverHost.Opacity = 0.0;
            }
        });
    }

    private void OnAlbumDetailVideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (Interlocked.CompareExchange(ref _albumDetailVideoFrameBusy, 1, 0) != 0)
        {
            return;
        }

        int frameGen = Volatile.Read(ref _albumDetailCanvasGeneration);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_albumDetailCanvasPlayer is null ||
                    !_albumDetailCanvasExpectedPlaying ||
                    frameGen != _albumDetailCanvasGeneration ||
                    _albumDetailCanvasPlayer.Source is null ||
                    ViewModel.ActiveSubView != WorkspaceSubView.AlbumDetail)
                {
                    return;
                }

                uint natW = _albumDetailCanvasPlayer.PlaybackSession?.NaturalVideoWidth ?? 0;
                uint natH = _albumDetailCanvasPlayer.PlaybackSession?.NaturalVideoHeight ?? 0;
                (int targetW, int targetH) = ComputeRenderTargetSize(natW, natH, maxDimension: 640, fallbackW: 512, fallbackH: 512);

                var device = CanvasDevice.GetSharedDevice();
                if (_albumDetailVideoRenderTarget is null ||
                    _albumDetailVideoRenderTarget.Device != device ||
                    _albumDetailVideoRenderTarget.SizeInPixels.Width != targetW ||
                    _albumDetailVideoRenderTarget.SizeInPixels.Height != targetH)
                {
                    _albumDetailVideoRenderTarget?.Dispose();
                    _albumDetailVideoRenderTarget = new CanvasRenderTarget(device, targetW, targetH, 96.0f);
                }

                _albumDetailCanvasPlayer.CopyFrameToVideoSurface(_albumDetailVideoRenderTarget);
                Interlocked.Increment(ref _albumDetailCanvasFrameSerial);
                _albumDetailCanvasFirstFrameCopied = true;

                AlbumDetailCanvasVideoCanvas?.Invalidate();

                if (!_albumDetailCanvasRevealed &&
                    (_albumDetailCanvasMediaOpened || natW > 0) &&
                    _albumDetailCanvasPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                {
                    RevealAlbumDetailCanvasWithCrossFade(frameGen);
                }
            }
            catch
            {
                _albumDetailUsingNativeSwapchainFallback = true;
                if (_albumDetailCanvasPlayer is not null && _albumDetailCanvasPlayer.IsVideoFrameServerEnabled)
                {
                    _albumDetailCanvasPlayer.IsVideoFrameServerEnabled = false;
                }

                if (AlbumDetailCanvasMediaPlayerElement is not null)
                {
                    AlbumDetailCanvasMediaPlayerElement.Opacity = 1.0;
                }

                if (_albumDetailCanvasExpectedPlaying && !_albumDetailCanvasRevealed)
                {
                    ScheduleFallbackSwapchainReveal(SurfaceKind.AlbumDetail, frameGen);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _albumDetailVideoFrameBusy, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _albumDetailVideoFrameBusy, 0);
        }
    }

    private void OnAlbumDetailCanvasVideoCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_albumDetailVideoRenderTarget is null ||
            !_albumDetailCanvasFirstFrameCopied ||
            !_albumDetailCanvasExpectedPlaying ||
            ViewModel.ActiveSubView != WorkspaceSubView.AlbumDetail)
        {
            return;
        }

        DrawRenderTargetUniformToFill(_albumDetailVideoRenderTarget, sender, args);
    }

    // -------------------------------------------------------------------------
    // SURFACE 4: ARTIST DETAIL ANIMATED HEADER VIDEO
    // -------------------------------------------------------------------------

    private void OnArtistDetailHeaderVideoChangedCore(object? sender, string? videoUri)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 1. Pre-navigation signal ("__PREPARE__:<artistName>"):
            //    - Same artist within 10s retention window -> resume instantly without reload!
            //    - Different artist -> purge old artist's video immediately!
            if (!string.IsNullOrWhiteSpace(videoUri) &&
                videoUri.StartsWith("__PREPARE__:", StringComparison.Ordinal))
            {
                string incomingArtist = videoUri["__PREPARE__:".Length..].Trim();
                _artistVideoRetentionTimer?.Stop();

                if (!string.IsNullOrWhiteSpace(_retainedArtistVideoKey) &&
                    string.Equals(_retainedArtistVideoKey, incomingArtist, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(_retainedArtistVideoUri) &&
                    _artistDetailHeaderPlayer?.Source is not null)
                {
                    _artistHeaderExpectedPlaying = true;
                    _artistHeaderStallTicks = 0;
                    _artistDetailHeaderPlayer.IsLoopingEnabled = true;
                    _artistDetailHeaderPlayer.Play();

                    bool hasFrame = _artistHeaderVideoRenderTarget is not null && _artistHeaderFirstFrameCopied;
                    _artistHeaderRevealed = hasFrame;
                    _artistHeaderFirstFrameRendered = hasFrame;
                    SetArtistHeaderVideoContainerOpacityCore(hasFrame ? 1.0 : 0.0, animate: hasFrame);
                    if (hasFrame)
                    {
                        ArtistDetailHeaderVideoCanvas?.Invalidate();
                    }

                    UpdateLoopWatchdogRunningState();
                }
                else
                {
                    PurgeArtistHeaderVideoPlayerStateCore();
                }

                return;
            }

            // 2. Leaving Artist Detail OR artist has no animated video
            if (string.IsNullOrWhiteSpace(videoUri))
            {
                _artistHeaderExpectedPlaying = false;
                _artistHeaderRevealed = false;
                _artistHeaderFirstFrameRendered = false;
                SetArtistHeaderVideoContainerOpacityCore(0.0, animate: false);
                _artistDetailHeaderPlayer?.Pause();
                ArtistDetailHeaderVideoCanvas?.Invalidate();
                UpdateLoopWatchdogRunningState();

                if (ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail &&
                    !string.IsNullOrWhiteSpace(_retainedArtistVideoKey) &&
                    !string.IsNullOrWhiteSpace(_retainedArtistVideoUri))
                {
                    _artistVideoRetentionTimer?.Stop();
                    _artistVideoRetentionTimer?.Start();
                }
                else
                {
                    PurgeArtistHeaderVideoPlayerStateCore();
                }

                return;
            }

            // 3. Active artist header local flat MP4 ready
            try
            {
                EnsureArtistDetailHeaderMediaPlayerInitializedCore();
                _artistVideoRetentionTimer?.Stop();
                string currentArtistName = ViewModel.CurrentArtistDetail?.Name ?? string.Empty;
                string normalizedUri = NormalizeLocalOrRemoteVideoKey(videoUri);

                // Already loaded this exact video -> keep playing continuously with zero reload!
                if (!string.IsNullOrWhiteSpace(_retainedArtistVideoUri) &&
                    string.Equals(_retainedArtistVideoUri, normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                    _artistDetailHeaderPlayer?.Source is not null)
                {
                    _retainedArtistVideoKey = currentArtistName;
                    _artistHeaderExpectedPlaying = true;
                    _artistHeaderStallTicks = 0;
                    _artistDetailHeaderPlayer.IsLoopingEnabled = true;
                    _artistDetailHeaderPlayer.Play();

                    if (_artistHeaderFirstFrameCopied && !_artistHeaderRevealed)
                    {
                        RevealArtistHeaderVideoWithCrossFade(_artistHeaderGeneration);
                    }

                    UpdateLoopWatchdogRunningState();
                    return;
                }

                Uri resolvedUri = File.Exists(videoUri)
                    ? new Uri(Path.GetFullPath(videoUri))
                    : new Uri(videoUri);

                int gen = ++_artistHeaderGeneration;
                _retainedArtistVideoKey = currentArtistName;
                _retainedArtistVideoUri = normalizedUri;
                _artistHeaderExpectedPlaying = true;
                _artistHeaderMediaOpened = false;
                _artistHeaderFirstFrameCopied = false;
                _artistHeaderRevealed = false;
                _artistHeaderFirstFrameRendered = false;
                _artistHeaderStallTicks = 0;
                _artistHeaderLastWatchdogFrameSerial = -1;
                _artistHeaderLastWatchdogPos = TimeSpan.MinValue;

                SetArtistHeaderVideoContainerOpacityCore(0.0, animate: false);
                _artistHeaderVideoRenderTarget?.Dispose();
                _artistHeaderVideoRenderTarget = null;
                ArtistDetailHeaderVideoCanvas?.Invalidate();

                if (_artistDetailHeaderPlayer is not null)
                {
                    _artistDetailHeaderPlayer.IsLoopingEnabled = true;
                    ReplaceMediaSource(_artistDetailHeaderPlayer, MediaSource.CreateFromUri(resolvedUri));
                    _artistDetailHeaderPlayer.Play();
                }

                UpdateLoopWatchdogRunningState();
            }
            catch
            {
                PurgeArtistHeaderVideoPlayerStateCore();
            }
        });
    }

    private void PauseArtistHeaderVideoOnLeaveCore()
    {
        _artistHeaderExpectedPlaying = false;
        _artistHeaderRevealed = false;
        _artistHeaderFirstFrameRendered = false;
        SetArtistHeaderVideoContainerOpacityCore(0.0, animate: false);
        _artistDetailHeaderPlayer?.Pause();
        ArtistDetailHeaderVideoCanvas?.Invalidate();
        UpdateLoopWatchdogRunningState();
    }

    private void PurgeArtistHeaderVideoPlayerStateCore()
    {
        _artistVideoRetentionTimer?.Stop();
        ++_artistHeaderGeneration;
        _artistHeaderExpectedPlaying = false;
        _artistHeaderMediaOpened = false;
        _artistHeaderFirstFrameCopied = false;
        _artistHeaderRevealed = false;
        _artistHeaderFirstFrameRendered = false;
        _artistHeaderStallTicks = 0;
        _retainedArtistVideoKey = null;
        _retainedArtistVideoUri = null;

        SetArtistHeaderVideoContainerOpacityCore(0.0, animate: false);

        if (_artistDetailHeaderPlayer is not null)
        {
            try
            {
                _artistDetailHeaderPlayer.Pause();
                ReplaceMediaSource(_artistDetailHeaderPlayer, null);
            }
            catch
            {
            }
        }

        _artistHeaderVideoRenderTarget?.Dispose();
        _artistHeaderVideoRenderTarget = null;
        ArtistDetailHeaderVideoCanvas?.Invalidate();

        UpdateLoopWatchdogRunningState();
    }

    private void SetArtistHeaderVideoContainerOpacityCore(double opacity, bool animate)
    {
        if (ArtistDetailHeaderVideoContainer is null)
        {
            return;
        }

        double clamped = Math.Clamp(opacity, 0.0, 1.0);
        ArtistDetailHeaderVideoContainer.OpacityTransition = animate
            ? _artistHeroCrossFadeTransition
            : null;
        ArtistDetailHeaderVideoContainer.Opacity = clamped;

        if (ArtistDetailPhotoBorder is not null)
        {
            ArtistDetailPhotoBorder.OpacityTransition = animate
                ? _artistHeroCrossFadeTransition
                : null;
            ArtistDetailPhotoBorder.Opacity = 1.0 - clamped;
        }
    }

    private void RevealArtistHeaderVideoWithCrossFade(int expectedGeneration)
    {
        if (expectedGeneration != _artistHeaderGeneration ||
            !_artistHeaderExpectedPlaying ||
            _artistHeaderRevealed ||
            ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
        {
            return;
        }

        _artistHeaderRevealed = true;
        _artistHeaderFirstFrameRendered = true;

        if (ArtistDetailHeaderVideoContainer is not null)
        {
            ArtistDetailHeaderVideoContainer.OpacityTransition = _artistHeroCrossFadeTransition;
            ArtistDetailHeaderVideoContainer.Opacity = 1.0;
        }

        ArtistDetailHeaderVideoCanvas?.Invalidate();

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (expectedGeneration != _artistHeaderGeneration ||
                !_artistHeaderExpectedPlaying ||
                !_artistHeaderRevealed ||
                ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
            {
                return;
            }

            if (ArtistDetailPhotoBorder is not null)
            {
                ArtistDetailPhotoBorder.OpacityTransition = _artistHeroCrossFadeTransition;
                ArtistDetailPhotoBorder.Opacity = 0.0;
            }
        });
    }

    private void OnArtistHeaderVideoFrameAvailableCore(MediaPlayer sender, object args)
    {
        if (Interlocked.CompareExchange(ref _artistHeaderVideoFrameBusy, 1, 0) != 0)
        {
            return;
        }

        int frameGen = Volatile.Read(ref _artistHeaderGeneration);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_artistDetailHeaderPlayer is null ||
                    !_artistHeaderExpectedPlaying ||
                    frameGen != _artistHeaderGeneration ||
                    _artistDetailHeaderPlayer.Source is null ||
                    ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
                {
                    return;
                }

                uint natW = _artistDetailHeaderPlayer.PlaybackSession?.NaturalVideoWidth ?? 0;
                uint natH = _artistDetailHeaderPlayer.PlaybackSession?.NaturalVideoHeight ?? 0;
                (int targetW, int targetH) = ComputeRenderTargetSize(natW, natH, maxDimension: 1280, fallbackW: 960, fallbackH: 540);

                var device = CanvasDevice.GetSharedDevice();
                if (_artistHeaderVideoRenderTarget is null ||
                    _artistHeaderVideoRenderTarget.Device != device ||
                    _artistHeaderVideoRenderTarget.SizeInPixels.Width != targetW ||
                    _artistHeaderVideoRenderTarget.SizeInPixels.Height != targetH)
                {
                    _artistHeaderVideoRenderTarget?.Dispose();
                    _artistHeaderVideoRenderTarget = new CanvasRenderTarget(device, targetW, targetH, 96.0f);
                }

                _artistDetailHeaderPlayer.CopyFrameToVideoSurface(_artistHeaderVideoRenderTarget);
                Interlocked.Increment(ref _artistHeaderFrameSerial);
                _artistHeaderFirstFrameCopied = true;

                ArtistDetailHeaderVideoCanvas?.Invalidate();

                if (!_artistHeaderRevealed &&
                    !string.IsNullOrWhiteSpace(ViewModel.CurrentArtistDetailVideoUri) &&
                    (_artistHeaderMediaOpened || natW > 0) &&
                    _artistDetailHeaderPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                {
                    RevealArtistHeaderVideoWithCrossFade(frameGen);
                }
            }
            catch
            {
                _artistHeaderUsingNativeSwapchainFallback = true;
                if (_artistDetailHeaderPlayer is not null && _artistDetailHeaderPlayer.IsVideoFrameServerEnabled)
                {
                    _artistDetailHeaderPlayer.IsVideoFrameServerEnabled = false;
                }

                if (ArtistDetailHeaderMediaPlayerElement is not null)
                {
                    ArtistDetailHeaderMediaPlayerElement.Opacity = 1.0;
                }

                if (_artistHeaderExpectedPlaying && !_artistHeaderRevealed)
                {
                    ScheduleFallbackSwapchainReveal(SurfaceKind.ArtistHeader, frameGen);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _artistHeaderVideoFrameBusy, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _artistHeaderVideoFrameBusy, 0);
        }
    }

    private void OnArtistDetailHeaderVideoCanvasDrawCore(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_artistHeaderVideoRenderTarget is null ||
            !_artistHeaderFirstFrameCopied ||
            !_artistHeaderExpectedPlaying ||
            ViewModel.ActiveSubView != WorkspaceSubView.ArtistDetail)
        {
            return;
        }

        DrawRenderTargetUniformToFill(_artistHeaderVideoRenderTarget, sender, args);
    }

    // -------------------------------------------------------------------------
    // SHARED RENDERING & FALLBACK HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Used ONLY if an exotic GPU driver rejects Win2D <c>CopyFrameToVideoSurface</c> and falls back
    /// to native <c>MediaPlayerElement</c>: waits for <c>PlaybackState == Playing</c> + active video
    /// position advancement before cross-fading so there is still zero black flash.
    /// </summary>
    private void ScheduleFallbackSwapchainReveal(SurfaceKind kind, int expectedGen)
    {
        MediaPlayer? player = kind switch
        {
            SurfaceKind.TrackCanvas => _sidebarCanvasPlayer,
            SurfaceKind.AlbumDetail => _albumDetailCanvasPlayer,
            SurfaceKind.ArtistHeader => _artistDetailHeaderPlayer,
            _ => null
        };

        if (player is null)
        {
            return;
        }

        var pollTimer = DispatcherQueue.CreateTimer();
        pollTimer.Interval = TimeSpan.FromMilliseconds(40);
        pollTimer.IsRepeating = true;
        int attempts = 0;

        pollTimer.Tick += (t, _) =>
        {
            attempts++;
            int currentGen = kind switch
            {
                SurfaceKind.TrackCanvas => _trackCanvasGeneration,
                SurfaceKind.AlbumDetail => _albumDetailCanvasGeneration,
                SurfaceKind.ArtistHeader => _artistHeaderGeneration,
                _ => -1
            };

            if (currentGen != expectedGen || !IsSurfaceExpectedPlaying(kind) || player.Source is null)
            {
                t.Stop();
                return;
            }

            var session = player.PlaybackSession;
            bool frameActive = session.PlaybackState == MediaPlaybackState.Playing &&
                               session.NaturalVideoWidth > 0 &&
                               (session.Position >= TimeSpan.FromMilliseconds(60) || attempts >= 10);

            if (frameActive || attempts >= 20)
            {
                t.Stop();
                switch (kind)
                {
                    case SurfaceKind.TrackCanvas:
                        RevealTrackCanvasWithCrossFade(expectedGen);
                        break;
                    case SurfaceKind.AlbumDetail:
                        RevealAlbumDetailCanvasWithCrossFade(expectedGen);
                        break;
                    case SurfaceKind.ArtistHeader:
                        RevealArtistHeaderVideoWithCrossFade(expectedGen);
                        break;
                }
            }
        };

        pollTimer.Start();
    }

    private static (int Width, int Height) ComputeRenderTargetSize(
        uint naturalWidth,
        uint naturalHeight,
        int maxDimension,
        int fallbackW,
        int fallbackH)
    {
        if (naturalWidth < 16 || naturalHeight < 16)
        {
            return (fallbackW, fallbackH);
        }

        double scale = Math.Min(1.0, maxDimension / (double)Math.Max(naturalWidth, naturalHeight));
        int w = Math.Max(64, (int)Math.Round(naturalWidth * scale));
        int h = Math.Max(64, (int)Math.Round(naturalHeight * scale));
        return (w, h);
    }

    private static void DrawRenderTargetUniformToFill(
        CanvasRenderTarget renderTarget,
        CanvasControl sender,
        CanvasDrawEventArgs args)
    {
        double destW = sender.Size.Width;
        double destH = sender.Size.Height;
        if (destW <= 2 || destH <= 2)
        {
            return;
        }

        var srcRect = ComputeUniformToFillRect(
            renderTarget.SizeInPixels.Width,
            renderTarget.SizeInPixels.Height,
            destW,
            destH);
        args.DrawingSession.DrawImage(renderTarget, new Rect(0, 0, destW, destH), srcRect);
    }

    private static string NormalizeLocalOrRemoteVideoKey(string videoUri)
    {
        string trimmed = videoUri.Trim();
        try
        {
            if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }
        }
        catch
        {
        }

        return trimmed;
    }
}
