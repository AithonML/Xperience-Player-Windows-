using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Live visual Music Sleep Timer ("Temporizador de música") countdown badge displayed on the
/// Fullscreen Now Playing screen whenever a sleep timer is active.
/// </summary>
public sealed partial class SleepTimerBadgeControl : UserControl
{
    private ISleepTimerService? _sleepTimerService;
    private bool _pulseHigh = true;

    /// <summary>
    /// Raised when the user clicks the main countdown badge to inspect or change sleep timer settings.
    /// </summary>
    public event EventHandler? BadgeClicked;

    public SleepTimerBadgeControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _sleepTimerService = app.Services.GetService<ISleepTimerService>();
            if (_sleepTimerService is not null)
            {
                _sleepTimerService.StateChanged -= OnSleepTimerStateChanged;
                _sleepTimerService.StateChanged += OnSleepTimerStateChanged;
                ApplyState(_sleepTimerService.CurrentState);
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_sleepTimerService is not null)
        {
            _sleepTimerService.StateChanged -= OnSleepTimerStateChanged;
        }
    }

    private void OnSleepTimerStateChanged(object? sender, SleepTimerState state)
    {
        if (DispatcherQueue is not null && !DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyState(state));
            return;
        }

        ApplyState(state);
    }

    private void ApplyState(SleepTimerState state)
    {
        if (RootBadgeGrid is null || CountdownTextBlock is null)
        {
            return;
        }

        if (!state.IsActive)
        {
            RootBadgeGrid.Visibility = Visibility.Collapsed;
            return;
        }

        RootBadgeGrid.Visibility = Visibility.Visible;
        CountdownTextBlock.Text = state.FormattedBadgeText;
        Extend5MinButton.Visibility = state.StopAtEndOfCurrentTrack ? Visibility.Collapsed : Visibility.Visible;

        _pulseHigh = !_pulseHigh;
        PulseIndicatorDot.Opacity = _pulseHigh ? 1.0 : 0.38;
    }

    private void OnMainBadgeButtonClicked(object sender, RoutedEventArgs e)
    {
        BadgeClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnExtend5MinButtonClicked(object sender, RoutedEventArgs e)
    {
        _sleepTimerService?.AddMinutes(5);
    }

    private void OnCancelTimerButtonClicked(object sender, RoutedEventArgs e)
    {
        _sleepTimerService?.Cancel();
    }
}
