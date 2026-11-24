using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Xperience.App.Controls;

/// <summary>
/// Interactive Liquid Glass Volume Slider whose pointer thumb rests as a compact solid circle,
/// and when held/dragged (<c>PointerPressed</c>) grows in size and morphs into a live refractive
/// <see cref="LiquidGlassPanel"/> lens orb before returning smoothly to its resting state upon release.
/// </summary>
public sealed partial class LiquidGlassVolumeSlider : UserControl
{
    private bool _isDragging;
    private DispatcherQueueTimer? _collapseThumbTimer;

    /// <summary>
    /// Gets whether the volume slider thumb is currently being dragged by the user.
    /// </summary>
    public bool IsDragging => _isDragging;

    /// <summary>
    /// Identifies the <see cref="Value"/> dependency property (0.0 to 100.0).
    /// </summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(LiquidGlassVolumeSlider),
            new PropertyMetadata(85.0, OnValueChanged));

    /// <summary>
    /// Gets or sets the volume percentage value in the range [0, 100].
    /// </summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0.0, 100.0));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiquidGlassVolumeSlider"/> class.
    /// </summary>
    public LiquidGlassVolumeSlider()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerPressed += OnSliderPointerPressed;
        PointerMoved += OnSliderPointerMoved;
        PointerReleased += OnSliderPointerReleased;
        PointerCanceled += OnSliderPointerCanceled;
        PointerCaptureLost += OnSliderPointerCaptureLost;
        PointerEntered += OnSliderPointerEntered;
        PointerExited += OnSliderPointerExited;
        PointerWheelChanged += OnSliderPointerWheelChanged;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LiquidGlassVolumeSlider slider)
        {
            slider.UpdateVisualLayout();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisualLayout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_collapseThumbTimer is not null)
        {
            _collapseThumbTimer.Stop();
            _collapseThumbTimer.Tick -= OnCollapseThumbTimerTick;
            _collapseThumbTimer = null;
        }

        _isDragging = false;
        ThumbContainer.Scale = Vector3.One;
        RestingSolidThumb.Opacity = 1.0;
        RestingSolidThumb.Scale = Vector3.One;
        HeldLiquidGlassThumb.Opacity = 0.0;
        HeldLiquidGlassThumb.Scale = new Vector3(0.52f, 0.52f, 1.0f);
        HeldLiquidGlassThumb.Visibility = Visibility.Collapsed;
        HeldThumbGlassPanel.Visibility = Visibility.Collapsed;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisualLayout();
    }

    private void OnSliderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            RestingSolidThumb.Scale = new Vector3(1.12f, 1.12f, 1.0f);
        }
    }

    private void OnSliderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            RestingSolidThumb.Scale = Vector3.One;
        }
    }

    private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootSliderGrid).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        CapturePointer(e.Pointer);
        SetHeldLiquidGlassMorphState(isHeld: true);
        UpdateValueFromPointer(e);
        e.Handled = true;
    }

    private void OnSliderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        UpdateValueFromPointer(e);
        e.Handled = true;
    }

    private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            UpdateValueFromPointer(e);
            EndDrag(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnSliderPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
    }

    private void OnSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        SetHeldLiquidGlassMorphState(isHeld: false);
    }

    private void OnSliderPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(RootSliderGrid).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            double step = delta > 0 ? 4.0 : -4.0;
            Value = Math.Clamp(Value + step, 0.0, 100.0);
            e.Handled = true;
        }
    }

    private void EndDrag(Pointer pointer)
    {
        _isDragging = false;
        try
        {
            ReleasePointerCapture(pointer);
        }
        catch
        {
            // Ignore if pointer capture was already released
        }

        SetHeldLiquidGlassMorphState(isHeld: false);
    }

    /// <summary>
    /// Transitions the volume thumb between its resting solid circle and its expanded
    /// refractive Liquid Glass lens orb while held.
    /// </summary>
    private void SetHeldLiquidGlassMorphState(bool isHeld)
    {
        if (isHeld)
        {
            _collapseThumbTimer?.Stop();

            if (HeldLiquidGlassThumb.Visibility != Visibility.Visible)
            {
                HeldLiquidGlassThumb.Visibility = Visibility.Visible;
            }

            if (HeldThumbGlassPanel.Visibility != Visibility.Visible)
            {
                HeldThumbGlassPanel.Visibility = Visibility.Visible;
            }

            ThumbContainer.Scale = new Vector3(1.16f, 1.16f, 1.0f);
            RestingSolidThumb.Opacity = 0.0;
            RestingSolidThumb.Scale = new Vector3(0.45f, 0.45f, 1.0f);

            HeldLiquidGlassThumb.Opacity = 1.0;
            HeldLiquidGlassThumb.Scale = Vector3.One;
            HeldThumbGlassPanel.InvalidateGlass();
        }
        else
        {
            ThumbContainer.Scale = Vector3.One;
            RestingSolidThumb.Opacity = 1.0;
            RestingSolidThumb.Scale = Vector3.One;

            HeldLiquidGlassThumb.Opacity = 0.0;
            HeldLiquidGlassThumb.Scale = new Vector3(0.52f, 0.52f, 1.0f);

            if (HeldLiquidGlassThumb.Visibility == Visibility.Visible)
            {
                if (_collapseThumbTimer is null)
                {
                    _collapseThumbTimer = DispatcherQueue.CreateTimer();
                    _collapseThumbTimer.Interval = TimeSpan.FromMilliseconds(240);
                    _collapseThumbTimer.IsRepeating = false;
                    _collapseThumbTimer.Tick += OnCollapseThumbTimerTick;
                }

                if (!_collapseThumbTimer.IsRunning)
                {
                    _collapseThumbTimer.Start();
                }
            }
        }
    }

    private void OnCollapseThumbTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isDragging)
        {
            HeldLiquidGlassThumb.Visibility = Visibility.Collapsed;
            HeldThumbGlassPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateValueFromPointer(PointerRoutedEventArgs e)
    {
        double totalWidth = RootSliderGrid.ActualWidth;
        const double trackInset = 10.0;
        double usableWidth = Math.Max(1.0, totalWidth - (trackInset * 2.0));

        double pointerX = e.GetCurrentPoint(RootSliderGrid).Position.X;
        double ratio = Math.Clamp((pointerX - trackInset) / usableWidth, 0.0, 1.0);
        Value = Math.Round(ratio * 100.0, 1);
    }

    private void UpdateVisualLayout()
    {
        double totalWidth = RootSliderGrid.ActualWidth;
        if (totalWidth <= 16.0)
        {
            return;
        }

        const double trackMargin = 8.0;
        const double trackPadding = 1.5;
        double innerTrackWidth = Math.Max(4.0, totalWidth - ((trackMargin + trackPadding) * 2.0));
        double ratio = Math.Clamp(Value / 100.0, 0.0, 1.0);

        VolumeFillBar.Width = Math.Max(0.0, innerTrackWidth * ratio);

        double thumbCenterMinX = trackMargin + trackPadding;
        double thumbCenterX = thumbCenterMinX + (innerTrackWidth * ratio);
        float thumbLeftX = (float)(thumbCenterX - (ThumbContainer.Width * 0.5));

        ThumbContainer.Translation = new Vector3(thumbLeftX, 0f, 0f);
        if (_isDragging)
        {
            HeldThumbGlassPanel.InvalidateGlass();
        }
    }
}
