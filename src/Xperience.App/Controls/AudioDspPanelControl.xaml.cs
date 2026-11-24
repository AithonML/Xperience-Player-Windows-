using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Modular Fullscreen Player Audio DSP, Equalizer, Playback Speed, Pitch/Tone, 1-Click Nightcore,
/// and Music Sleep Timer control panel.
/// </summary>
public sealed partial class AudioDspPanelControl : UserControl
{
    private IAudioDspController? _dspController;
    private ISleepTimerService? _sleepTimerService;
    private bool _suppressUiEvents;

    /// <summary>
    /// Raised when the user clicks the panel's Close ('X') button.
    /// </summary>
    public event EventHandler? CloseRequested;

    public AudioDspPanelControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Switches the active tab inside the DSP panel ("Equalizer", "SpeedPitch", or "SleepTimer").
    /// </summary>
    public void OpenTab(string tabKey)
    {
        string normalized = (tabKey ?? "Equalizer").Trim();
        bool isSpeed = normalized.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains("Pitch", StringComparison.OrdinalIgnoreCase);
        bool isTimer = normalized.Contains("Sleep", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains("Timer", StringComparison.OrdinalIgnoreCase);

        EqualizerTabPanel.Visibility = (!isSpeed && !isTimer) ? Visibility.Visible : Visibility.Collapsed;
        SpeedPitchTabPanel.Visibility = isSpeed ? Visibility.Visible : Visibility.Collapsed;
        SleepTimerTabPanel.Visibility = isTimer ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
        var idleBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

        TabBtnEqualizer.Background = (!isSpeed && !isTimer) ? activeBrush : idleBrush;
        TabBtnSpeedPitch.Background = isSpeed ? activeBrush : idleBrush;
        TabBtnSleepTimer.Background = isTimer ? activeBrush : idleBrush;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _dspController = app.Services.GetService<IAudioDspController>();
            _sleepTimerService = app.Services.GetService<ISleepTimerService>();

            if (_dspController is not null)
            {
                _dspController.StateChanged -= OnDspStateChanged;
                _dspController.StateChanged += OnDspStateChanged;
                SyncFromDspState(_dspController.CurrentState);
            }

            if (_sleepTimerService is not null)
            {
                _sleepTimerService.StateChanged -= OnSleepTimerStateChanged;
                _sleepTimerService.StateChanged += OnSleepTimerStateChanged;
                SyncFromSleepTimerState(_sleepTimerService.CurrentState);
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_dspController is not null)
        {
            _dspController.StateChanged -= OnDspStateChanged;
        }

        if (_sleepTimerService is not null)
        {
            _sleepTimerService.StateChanged -= OnSleepTimerStateChanged;
        }
    }

    private void OnDspStateChanged(object? sender, AudioDspState state)
    {
        if (DispatcherQueue is not null && !DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SyncFromDspState(state));
            return;
        }

        SyncFromDspState(state);
    }

    private void OnSleepTimerStateChanged(object? sender, SleepTimerState state)
    {
        if (DispatcherQueue is not null && !DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SyncFromSleepTimerState(state));
            return;
        }

        SyncFromSleepTimerState(state);
    }

    private void SyncFromDspState(AudioDspState state)
    {
        if (EqBand0Slider is null)
        {
            return;
        }

        _suppressUiEvents = true;
        try
        {
            // 1. Nightcore Hero Section
            double ncSemitones = 12.0 * Math.Log2(state.NightcoreRate);
            NightcoreRateSlider.Value = state.NightcoreRate;
            NightcoreRateValueText.Text = $"{state.NightcoreRate:0.00}x (+{ncSemitones:0.1} st)";
            NightcoreRateSliderRow.Visibility = state.IsNightcoreActive ? Visibility.Visible : Visibility.Collapsed;

            NightcoreHeroCardBorder.BorderBrush = state.IsNightcoreActive
                ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x4D, 0xA6))
                : new SolidColorBrush(Color.FromArgb(0x48, 0xEC, 0x48, 0x99));

            NightcoreChainStatusText.Text = state.IsNightcoreActive
                ? $"ACTIVE: Resampling {state.EffectivePlaybackSpeed:0.00}x (+{state.EffectivePitchSemitones:0.1} st • preservesPitch=OFF) + 90Hz (+6dB) + 3.5kHz (+2dB)"
                : "Coupled Speed + Pitch (1.22x • +3.44 st) + 90Hz Punch + 3.5kHz Presence";

            HeaderSummarySubtitle.Text = state.IsNightcoreActive
                ? $"Nightcore ON ({state.EffectivePlaybackSpeed:0.00}x) • 150ms exponential ramp"
                : $"EQ: {AudioDspState.GetPresetDisplayName(state.ActivePreset)} • Speed {state.EffectivePlaybackSpeed:0.00}x";

            // 2. Equalizer & Tone Sliders
            BassBoostSlider.Value = state.BassBoostDb;
            BassBoostValueText.Text = FormatDb(state.BassBoostDb);

            TrebleBoostSlider.Value = state.TrebleBoostDb;
            TrebleBoostValueText.Text = FormatDb(state.TrebleBoostDb);

            double b0 = state.BandGainsDb.Count > 0 ? state.BandGainsDb[0] : 0.0;
            double b1 = state.BandGainsDb.Count > 1 ? state.BandGainsDb[1] : 0.0;
            double b2 = state.BandGainsDb.Count > 2 ? state.BandGainsDb[2] : 0.0;
            double b3 = state.BandGainsDb.Count > 3 ? state.BandGainsDb[3] : 0.0;
            double b4 = state.BandGainsDb.Count > 4 ? state.BandGainsDb[4] : 0.0;

            EqBand0Slider.Value = b0;
            EqBand0ValueText.Text = FormatDb(b0);

            EqBand1Slider.Value = b1;
            EqBand1ValueText.Text = FormatDb(b1);

            EqBand2Slider.Value = b2;
            EqBand2ValueText.Text = FormatDb(b2);

            EqBand3Slider.Value = b3;
            EqBand3ValueText.Text = FormatDb(b3);

            EqBand4Slider.Value = b4;
            EqBand4ValueText.Text = FormatDb(b4);

            // 3. Playback Speed & Pitch
            PlaybackSpeedSlider.Value = state.EffectivePlaybackSpeed;
            PlaybackSpeedValueText.Text = $"{state.EffectivePlaybackSpeed:0.00}x";

            PitchSemitonesSlider.Value = state.IsNightcoreActive ? state.EffectivePitchSemitones : state.ManualPitchSemitones;
            PitchSemitonesValueText.Text = FormatSemitones(state.EffectivePitchSemitones);

            CouplePitchToSpeedToggle.IsOn = state.IsNightcoreActive || state.CouplePitchToSpeed;
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void SyncFromSleepTimerState(SleepTimerState state)
    {
        if (SleepTimerHeroCountdownText is null)
        {
            return;
        }

        if (!state.IsActive)
        {
            SleepTimerHeroCountdownText.Text = "Off";
            SleepTimerHeroSubtitleText.Text = "Select a duration to smoothly fade out and pause music automatically.";
            SleepTimerActiveActionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SleepTimerHeroCountdownText.Text = $"⏱ {state.FormattedBadgeText}";
        SleepTimerHeroSubtitleText.Text = state.StatusSubtitle;
        SleepTimerActiveActionsPanel.Visibility = Visibility.Visible;
    }

    private void OnTabEqualizerClicked(object sender, RoutedEventArgs e) => OpenTab("Equalizer");

    private void OnTabSpeedPitchClicked(object sender, RoutedEventArgs e) => OpenTab("SpeedPitch");

    private void OnTabSleepTimerClicked(object sender, RoutedEventArgs e) => OpenTab("SleepTimer");

    private void OnResetAllDspClicked(object sender, RoutedEventArgs e)
    {
        _dspController?.ResetAllToDefaults();
    }

    private void OnClosePanelClicked(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnNightcoreRateSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        _dspController.SetNightcoreRate(e.NewValue);
    }

    private void OnEqPresetButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_dspController is null || sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse<EqualizerPresetId>(tag, ignoreCase: true, out var preset))
        {
            _dspController.ApplyEqualizerPreset(preset);
        }
    }

    private void OnBassBoostSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        BassBoostValueText.Text = FormatDb(e.NewValue);
        _dspController.SetBassBoost(e.NewValue);
    }

    private void OnTrebleBoostSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        TrebleBoostValueText.Text = FormatDb(e.NewValue);
        _dspController.SetTrebleBoost(e.NewValue);
    }

    private void OnEqBandSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null || sender is not FrameworkElement { Tag: string tagStr })
        {
            return;
        }

        if (int.TryParse(tagStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bandIndex))
        {
            _dspController.SetEqualizerBandGain(bandIndex, e.NewValue);
        }
    }

    private void OnPlaybackSpeedSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        PlaybackSpeedValueText.Text = $"{e.NewValue:0.00}x";
        _dspController.SetManualPlaybackSpeed(e.NewValue);
    }

    private void OnQuickSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_dspController is null || sender is not FrameworkElement { Tag: string speedStr })
        {
            return;
        }

        if (double.TryParse(speedStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
        {
            _dspController.SetManualPlaybackSpeed(speed);
        }
    }

    private void OnPitchSemitonesSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        PitchSemitonesValueText.Text = FormatSemitones(e.NewValue);
        _dspController.SetManualPitchSemitones(e.NewValue);
    }

    private void OnQuickPitchButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_dspController is null || sender is not FrameworkElement { Tag: string pitchStr })
        {
            return;
        }

        if (double.TryParse(pitchStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double semitones))
        {
            _dspController.SetManualPitchSemitones(semitones);
        }
    }

    private void OnCouplePitchToSpeedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _dspController is null)
        {
            return;
        }

        _dspController.SetCouplePitchToSpeed(CouplePitchToSpeedToggle.IsOn);
    }

    private void OnSleepTimerPresetClicked(object sender, RoutedEventArgs e)
    {
        if (_sleepTimerService is null || sender is not FrameworkElement { Tag: string minsStr })
        {
            return;
        }

        if (int.TryParse(minsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins))
        {
            var mode = mins switch
            {
                5 => SleepTimerPresetMode.Minutes5,
                15 => SleepTimerPresetMode.Minutes15,
                30 => SleepTimerPresetMode.Minutes30,
                45 => SleepTimerPresetMode.Minutes45,
                60 => SleepTimerPresetMode.Minutes60,
                _ => SleepTimerPresetMode.Custom
            };

            if (mode == SleepTimerPresetMode.Custom)
            {
                _sleepTimerService.StartCustomMinutes(mins);
            }
            else
            {
                _sleepTimerService.StartPreset(mode);
            }
        }
    }

    private void OnSleepTimerEndOfTrackClicked(object sender, RoutedEventArgs e)
    {
        _sleepTimerService?.StartPreset(SleepTimerPresetMode.EndOfTrack);
    }

    private void OnSleepTimerAdd5MinClicked(object sender, RoutedEventArgs e)
    {
        _sleepTimerService?.AddMinutes(5);
    }

    private void OnSleepTimerCancelClicked(object sender, RoutedEventArgs e)
    {
        _sleepTimerService?.Cancel();
    }

    private void OnCustomTimerSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CustomTimerMinutesLabel is not null)
        {
            int mins = (int)Math.Round(e.NewValue);
            CustomTimerMinutesLabel.Text = $"Custom: {mins} min";
        }
    }

    private void OnStartCustomTimerClicked(object sender, RoutedEventArgs e)
    {
        if (_sleepTimerService is null || CustomTimerMinutesSlider is null)
        {
            return;
        }

        int mins = (int)Math.Round(CustomTimerMinutesSlider.Value);
        _sleepTimerService.StartCustomMinutes(mins);
    }

    private static string FormatDb(double gainDb) =>
        gainDb > 0.01 ? $"+{gainDb:0.0} dB" : $"{gainDb:0.0} dB";

    private static string FormatSemitones(double st) =>
        st > 0.01 ? $"+{st:0.1} st" : $"{st:0.1} st";
}
