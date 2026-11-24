using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Tactile 1-Click "Nightcore" real-time DSP pill toggle button with neon glow state,
/// subtle breathing pulse micro-interaction, and clear <c>"Nightcore ON"</c> indicator.
/// </summary>
public sealed partial class NightcorePillButton : UserControl
{
    public static readonly DependencyProperty IsNightcoreActiveProperty =
        DependencyProperty.Register(
            nameof(IsNightcoreActive),
            typeof(bool),
            typeof(NightcorePillButton),
            new PropertyMetadata(false, OnIsNightcoreActivePropertyChanged));

    public static readonly DependencyProperty RateLabelProperty =
        DependencyProperty.Register(
            nameof(RateLabel),
            typeof(string),
            typeof(NightcorePillButton),
            new PropertyMetadata("1.22x", OnRateLabelPropertyChanged));

    private readonly DispatcherTimer _pulseTimer;
    private IAudioDspController? _dspController;
    private long _visibilityCallbackToken;
    private bool _pulsePhaseHigh;
    private bool? _appliedActiveVisualState;

    private LinearGradientBrush? _activeGradientBrush;
    private SolidColorBrush? _activeBorderBrush;
    private SolidColorBrush? _activeIconBrush;
    private SolidColorBrush? _inactiveBackgroundBrush;
    private SolidColorBrush? _inactiveBorderBrush;
    private SolidColorBrush? _inactiveIconBrush;

    /// <summary>
    /// Raised when the user clicks the Nightcore pill button.
    /// </summary>
    public event EventHandler<bool>? NightcoreToggled;

    /// <summary>
    /// Gets or sets a value indicating whether Nightcore mode is currently active.
    /// </summary>
    public bool IsNightcoreActive
    {
        get => (bool)GetValue(IsNightcoreActiveProperty);
        set => SetValue(IsNightcoreActiveProperty, value);
    }

    /// <summary>
    /// Gets or sets the displayed coupled speed multiplier badge text (e.g. "1.22x").
    /// </summary>
    public string RateLabel
    {
        get => (string)GetValue(RateLabelProperty);
        set => SetValue(RateLabelProperty, value);
    }

    public NightcorePillButton()
    {
        InitializeComponent();

        _pulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(680)
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pulseTimer.Tick -= OnPulseTimerTick;
        _pulseTimer.Tick += OnPulseTimerTick;

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        if (Application.Current is App app)
        {
            _dspController = app.Services.GetService<IAudioDspController>();
            if (_dspController is not null)
            {
                _dspController.StateChanged -= OnDspControllerStateChanged;
                _dspController.StateChanged += OnDspControllerStateChanged;
                SyncFromDspState(_dspController.CurrentState);
                return;
            }
        }

        UpdateVisualState(IsNightcoreActive);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pulseTimer.Stop();
        _pulseTimer.Tick -= OnPulseTimerTick;

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        if (_dspController is not null)
        {
            _dspController.StateChanged -= OnDspControllerStateChanged;
            _dspController = null;
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdatePulseTimerState();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        UpdatePulseTimerState();
    }

    private bool IsAncestorChainVisible()
    {
        DependencyObject? current = this;
        int depth = 0;
        while (current is not null && depth < 16)
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
            depth++;
        }

        return true;
    }

    private void UpdatePulseTimerState()
    {
        bool shouldPulse = IsLoaded && Visibility == Visibility.Visible && IsNightcoreActive && IsAncestorChainVisible();
        if (shouldPulse)
        {
            if (!_pulseTimer.IsEnabled)
            {
                _pulseTimer.Start();
            }
        }
        else
        {
            _pulseTimer.Stop();
        }
    }

    private void OnDspControllerStateChanged(object? sender, AudioDspState state)
    {
        if (DispatcherQueue is not null && !DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => SyncFromDspState(state));
            return;
        }

        SyncFromDspState(state);
    }

    private void SyncFromDspState(AudioDspState state)
    {
        RateLabel = $"{state.NightcoreRate:0.00}x";
        IsNightcoreActive = state.IsNightcoreActive;
        UpdateVisualState(state.IsNightcoreActive);
    }

    private static void OnIsNightcoreActivePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NightcorePillButton control && e.NewValue is bool active)
        {
            control.UpdateVisualState(active);
        }
    }

    private static void OnRateLabelPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NightcorePillButton control && e.NewValue is string text && control.RateBadgeText is not null)
        {
            control.RateBadgeText.Text = text;
        }
    }

    private void OnTogglePillButtonClicked(object sender, RoutedEventArgs e)
    {
        bool nextState;
        if (_dspController is not null)
        {
            nextState = _dspController.ToggleNightcoreMode();
        }
        else
        {
            nextState = !IsNightcoreActive;
            IsNightcoreActive = nextState;
        }

        // Tactile pop micro-interaction
        RootContainer.Scale = new Vector3(1.06f, 1.06f, 1.0f);
        _ = ResetScaleAfterBounceAsync();

        NightcoreToggled?.Invoke(this, nextState);
    }

    private async Task ResetScaleAfterBounceAsync()
    {
        await Task.Delay(150);
        if (RootContainer is not null)
        {
            RootContainer.Scale = Vector3.One;
        }
    }

    private void UpdateVisualState(bool isActive)
    {
        if (LabelTextBlock is null || PillAccentSurfaceBorder is null)
        {
            return;
        }

        if (isActive)
        {
            RateBadgeText.Text = RateLabel;
        }

        if (_appliedActiveVisualState != isActive)
        {
            _appliedActiveVisualState = isActive;

            if (isActive)
            {
                LabelTextBlock.Text = "Nightcore ON";
                RateBadgeBorder.Visibility = Visibility.Visible;

                if (_activeGradientBrush is null)
                {
                    // Neon Magenta -> Violet -> Cyan gradient surface and bright neon border
                    var gradient = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 1)
                    };
                    gradient.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0xCC, 0xEC, 0x48, 0x99) });
                    gradient.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Color.FromArgb(0xCC, 0x93, 0x33, 0xEA) });
                    gradient.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0xCC, 0x06, 0xB6, 0xD4) });
                    _activeGradientBrush = gradient;
                }

                _activeBorderBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x7A, 0xC6));
                _activeIconBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xF0, 0xFA));

                PillAccentSurfaceBorder.Background = _activeGradientBrush;
                PillAccentSurfaceBorder.BorderBrush = _activeBorderBrush;
                NightcoreIcon.Foreground = _activeIconBrush;
                NeonGlowHaloBorder.Opacity = 0.85;
                ActivePulseDot.Opacity = 0.90;
            }
            else
            {
                LabelTextBlock.Text = "Nightcore";
                RateBadgeBorder.Visibility = Visibility.Collapsed;

                _inactiveBackgroundBrush ??= new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
                _inactiveBorderBrush ??= new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
                _inactiveIconBrush ??= new SolidColorBrush(Colors.White);

                PillAccentSurfaceBorder.Background = _inactiveBackgroundBrush;
                PillAccentSurfaceBorder.BorderBrush = _inactiveBorderBrush;
                NightcoreIcon.Foreground = _inactiveIconBrush;
                NeonGlowHaloBorder.Opacity = 0.0;
                ActivePulseDot.Opacity = 0.0;
            }
        }

        UpdatePulseTimerState();
    }

    private void OnPulseTimerTick(object? sender, object e)
    {
        if (!IsNightcoreActive || !IsLoaded || Visibility != Visibility.Visible || !IsAncestorChainVisible())
        {
            _pulseTimer.Stop();
            if (!IsNightcoreActive)
            {
                NeonGlowHaloBorder.Opacity = 0.0;
            }
            return;
        }

        _pulsePhaseHigh = !_pulsePhaseHigh;
        NeonGlowHaloBorder.Opacity = _pulsePhaseHigh ? 0.95 : 0.42;
        ActivePulseDot.Opacity = _pulsePhaseHigh ? 1.0 : 0.45;
    }
}
