using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

/// <summary>
/// Modular partial ViewModel slice managing Fullscreen Audio DSP, Equalizer, Playback Speed,
/// Pitch/Tone, 1-Click Nightcore Mode, and Visual Music Sleep Timer state and commands.
/// </summary>
public sealed partial class ShellViewModel
{
    private IAudioDspController? _lazyDspController;
    private ISleepTimerService? _lazySleepTimerService;
    private bool _dspControllerSubscribed;
    private bool _sleepTimerSubscribed;

    /// <summary>
    /// Raised when the ViewModel requests the Fullscreen <c>AudioDspPanelControl</c> to switch to a specific tab
    /// ("Equalizer", "SpeedPitch", or "SleepTimer").
    /// </summary>
    public event EventHandler<string>? AudioDspTabRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioDspPanelVisibility))]
    public partial bool IsAudioDspPanelOpen { get; set; }

    [ObservableProperty]
    public partial string ActiveAudioDspTab { get; set; } = "Equalizer";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NightcoreMenuLabel))]
    public partial bool IsNightcoreModeActive { get; set; }

    [ObservableProperty]
    public partial string EffectiveSpeedBadgeText { get; set; } = "1.00x";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepTimerMenuLabel))]
    public partial string ActiveSleepTimerBadgeText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SleepTimerMenuLabel))]
    public partial bool IsSleepTimerActive { get; set; }

    /// <summary>
    /// Gets the visibility of the floating Audio DSP &amp; Sleep Timer panel in Fullscreen Now Playing.
    /// </summary>
    public Visibility AudioDspPanelVisibility =>
        IsAudioDspPanelOpen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Gets the context menu label for the 1-Click Nightcore Mode action.
    /// </summary>
    public string NightcoreMenuLabel =>
        IsNightcoreModeActive ? "Nightcore ON (1.22x Active DSP)" : "Nightcore Mode (1-Click DSP)";

    /// <summary>
    /// Gets the context menu label for the Music Sleep Timer action.
    /// </summary>
    public string SleepTimerMenuLabel =>
        IsSleepTimerActive && !string.IsNullOrWhiteSpace(ActiveSleepTimerBadgeText)
            ? $"Sleep Timer ({ActiveSleepTimerBadgeText})"
            : "Sleep Timer...";

    private IAudioDspController? ResolveDspController()
    {
        if (_lazyDspController is null && Application.Current is App app)
        {
            _lazyDspController = app.Services.GetService<IAudioDspController>();
            EnsureDspSubscriptions();
        }

        return _lazyDspController;
    }

    private ISleepTimerService? ResolveSleepTimerService()
    {
        if (_lazySleepTimerService is null && Application.Current is App app)
        {
            _lazySleepTimerService = app.Services.GetService<ISleepTimerService>();
            EnsureDspSubscriptions();
        }

        return _lazySleepTimerService;
    }

    private void OnDspControllerStateChanged(object? sender, AudioDspState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsNightcoreModeActive = state.IsNightcoreActive;
            EffectiveSpeedBadgeText = $"{state.EffectivePlaybackSpeed:0.00}x";
        });
    }

    private void OnSleepTimerStateChanged(object? sender, SleepTimerState timerState)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsSleepTimerActive = timerState.IsActive;
            ActiveSleepTimerBadgeText = timerState.FormattedBadgeText;
        });
    }

    private void EnsureDspSubscriptions()
    {
        if (_lazyDspController is not null && !_dspControllerSubscribed)
        {
            _dspControllerSubscribed = true;
            _lazyDspController.StateChanged -= OnDspControllerStateChanged;
            _lazyDspController.StateChanged += OnDspControllerStateChanged;

            var current = _lazyDspController.CurrentState;
            IsNightcoreModeActive = current.IsNightcoreActive;
            EffectiveSpeedBadgeText = $"{current.EffectivePlaybackSpeed:0.00}x";
        }

        if (_lazySleepTimerService is not null && !_sleepTimerSubscribed)
        {
            _sleepTimerSubscribed = true;
            _lazySleepTimerService.StateChanged -= OnSleepTimerStateChanged;
            _lazySleepTimerService.StateChanged += OnSleepTimerStateChanged;

            var currentTimer = _lazySleepTimerService.CurrentState;
            IsSleepTimerActive = currentTimer.IsActive;
            ActiveSleepTimerBadgeText = currentTimer.FormattedBadgeText;
        }
    }

    [RelayCommand]
    private void ToggleAudioDspPanel()
    {
        ResolveDspController();
        ResolveSleepTimerService();
        IsAudioDspPanelOpen = !IsAudioDspPanelOpen;
        if (IsAudioDspPanelOpen)
        {
            IsVisualizerSelectorOpen = false;
        }
    }

    [RelayCommand]
    private void CloseAudioDspPanel()
    {
        IsAudioDspPanelOpen = false;
    }

    [RelayCommand]
    private void ContextMenuOpenEqualizer()
    {
        ResolveDspController();
        IsTrackContextMenuOpen = false;
        IsVisualizerSelectorOpen = false;
        ActiveAudioDspTab = "Equalizer";
        IsAudioDspPanelOpen = true;
        AudioDspTabRequested?.Invoke(this, "Equalizer");
    }

    [RelayCommand]
    private void ContextMenuOpenSpeedAndPitch()
    {
        ResolveDspController();
        IsTrackContextMenuOpen = false;
        IsVisualizerSelectorOpen = false;
        ActiveAudioDspTab = "SpeedPitch";
        IsAudioDspPanelOpen = true;
        AudioDspTabRequested?.Invoke(this, "SpeedPitch");
    }

    [RelayCommand]
    private void ContextMenuToggleNightcore()
    {
        var controller = ResolveDspController();
        IsTrackContextMenuOpen = false;
        if (controller is not null)
        {
            bool active = controller.ToggleNightcoreMode();
            IsNightcoreModeActive = active;
            StatusMessage = active
                ? "Nightcore ON: 1.22x Resampling + Low-Shelf 90Hz (+6dB) + Peaking 3.5kHz (+2dB) + Limiter (-6dB)"
                : "Nightcore mode disabled (150ms smooth ramp)";
        }
    }

    [RelayCommand]
    private void ContextMenuOpenSleepTimer()
    {
        ResolveSleepTimerService();
        IsTrackContextMenuOpen = false;
        IsVisualizerSelectorOpen = false;
        ActiveAudioDspTab = "SleepTimer";
        IsAudioDspPanelOpen = true;
        AudioDspTabRequested?.Invoke(this, "SleepTimer");
    }
}
