using Microsoft.UI.Xaml;

namespace Xperience.App.Views;

/// <summary>
/// Modular partial code-behind slice for <see cref="ShellPage"/> handling Fullscreen Audio DSP,
/// Equalizer, Playback Speed, Pitch/Tone, 1-Click Nightcore, and Visual Sleep Timer interactions.
/// </summary>
public sealed partial class ShellPage
{
    private void OpenAudioDspPanelTab(string tab)
    {
        var panel = FullscreenAudioDspPanel ?? (FindName(nameof(FullscreenAudioDspPanel)) as Controls.AudioDspPanelControl);
        if (panel is not null)
        {
            panel.OpenTab(tab);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            var deferred = FullscreenAudioDspPanel ?? (FindName(nameof(FullscreenAudioDspPanel)) as Controls.AudioDspPanelControl);
            deferred?.OpenTab(tab);
        });
    }

    private void OnFullscreenAudioDspButtonClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleAudioDspPanelCommand.Execute(null);
        if (ViewModel.IsAudioDspPanelOpen)
        {
            OpenAudioDspPanelTab(ViewModel.ActiveAudioDspTab);
        }
    }

    private void OnFullscreenSleepTimerBadgeClicked(object? sender, EventArgs e)
    {
        ViewModel.ContextMenuOpenSleepTimerCommand.Execute(null);
        OpenAudioDspPanelTab("SleepTimer");
    }

    private void OnFullscreenAudioDspPanelCloseRequested(object? sender, EventArgs e)
    {
        ViewModel.CloseAudioDspPanelCommand.Execute(null);
    }

    private void OnContextMenuEqualizerClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ContextMenuOpenEqualizerCommand.Execute(null);
        OpenAudioDspPanelTab("Equalizer");
    }

    private void OnContextMenuSpeedPitchClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ContextMenuOpenSpeedAndPitchCommand.Execute(null);
        OpenAudioDspPanelTab("SpeedPitch");
    }

    private void OnContextMenuSleepTimerClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ContextMenuOpenSleepTimerCommand.Execute(null);
        OpenAudioDspPanelTab("SleepTimer");
    }
}
