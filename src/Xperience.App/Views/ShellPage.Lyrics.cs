using Microsoft.UI.Xaml;
using Windows.UI;
using Xperience.App.Controls;

namespace Xperience.App.Views;

public sealed partial class ShellPage
{
    private bool _isLyricsFeatureInitialized;

    /// <summary>
    /// Hooks lyrics display-state transitions from <see cref="ViewModel"/> so floating sync pills
    /// clear immediately on track change and both Win2D lyrics canvases recenter cleanly when lyrics load.
    /// </summary>
    private void InitializeLyricsVisualFeatures()
    {
        if (_isLyricsFeatureInitialized)
        {
            return;
        }

        _isLyricsFeatureInitialized = true;
        ViewModel.LyricsDisplayStateChanged += OnViewModelLyricsDisplayStateChanged;
    }

    private void OnViewModelLyricsDisplayStateChanged(object? sender, bool hasLyrics)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!hasLyrics)
            {
                if (SidebarLyricsSyncPill is not null)
                {
                    SidebarLyricsSyncPill.Visibility = Visibility.Collapsed;
                }

                if (FullscreenLyricsSyncPillHost is not null)
                {
                    FullscreenLyricsSyncPillHost.Visibility = Visibility.Collapsed;
                }

                if (SidebarSynchronizedLyricsControl is not null)
                {
                    SidebarSynchronizedLyricsControl.ActiveLineIndex = -1;
                }

                if (FullscreenSynchronizedLyricsControl is not null)
                {
                    FullscreenSynchronizedLyricsControl.ActiveLineIndex = -1;
                }

                return;
            }

            // Force immediate redraw & center once Visibility flips Collapsed -> Visible (including while paused)
            SidebarSynchronizedLyricsControl?.RecenterOnActiveLine();
            FullscreenSynchronizedLyricsControl?.RecenterOnActiveLine();
        });
    }

    /// <summary>
    /// Propagates the dynamic album cover vibrant accent color to the Now Playing and Fullscreen
    /// lyrics skeleton and sad musical note empty-state controls so they harmonize with the active cover.
    /// </summary>
    private void UpdateLyricsStateAccentColor(Color vibrantAccent)
    {
        if (SidebarLyricsSkeletonControl is not null)
        {
            SidebarLyricsSkeletonControl.AccentColor = vibrantAccent;
        }

        if (FullscreenLyricsSkeletonControl is not null)
        {
            FullscreenLyricsSkeletonControl.AccentColor = vibrantAccent;
        }

        if (SidebarLyricsNotFoundControl is not null)
        {
            SidebarLyricsNotFoundControl.AccentColor = vibrantAccent;
        }

        if (FullscreenLyricsNotFoundControl is not null)
        {
            FullscreenLyricsNotFoundControl.AccentColor = vibrantAccent;
        }
    }
}
