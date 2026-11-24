using System;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// Android 13–inspired squiggly progress bar rendered with Win2D.
/// Animates a smooth tapered sinusoidal wave with radial under-glow when playing,
/// flattens cleanly when paused, and supports click-and-drag scrubbing.
/// </summary>
public sealed class SquigglyProgressBarControl : Grid
{
    private const float WaveLength = 26f;
    private const float MaxAmplitude = 3.8f;
    private const float StrokeWidth = 3.2f;
    private const float TaperLength = 14f;
    private const float PhaseSpeed = 2f * MathF.PI * 1.15f;
    private const float HorizontalTrackPadding = 13f;

    private CanvasControl? _canvas;
    private InputSystemCursor? _handCursor;
    private CanvasStrokeStyle? _roundStrokeStyle;
    private long _visibilityCallbackToken;
    private bool _isRenderingHooked;
    private bool _isPointerOver;
    private bool _isDragging;
    private double _dragProgress;
    private long _lastRenderTimestampTicks;
    private float _wavePhase;
    private float _currentAmplitude;
    private float _thumbScale = 1f;

    /// <summary>
    /// Identifies the <see cref="Progress"/> dependency property (0.0 to 1.0).
    /// </summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(SquigglyProgressBarControl),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsPlaying"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(SquigglyProgressBarControl),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="AccentColor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(SquigglyProgressBarControl),
            new PropertyMetadata(Color.FromArgb(255, 239, 68, 68), OnVisualPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsLightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsLightModeProperty =
        DependencyProperty.Register(
            nameof(IsLightMode),
            typeof(bool),
            typeof(SquigglyProgressBarControl),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    /// <summary>
    /// Raised when the user finishes a tap or drag seek gesture with the normalized progress (0.0 to 1.0).
    /// </summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>
    /// Raised while the user is actively scrubbing so callers can preview the target timestamp.
    /// </summary>
    public event EventHandler<double>? ScrubbingProgressChanged;

    /// <summary>
    /// Raised when active pointer scrubbing begins or ends.
    /// </summary>
    public event EventHandler<bool>? ScrubbingStateChanged;

    /// <summary>
    /// Initializes a new instance of <see cref="SquigglyProgressBarControl"/>.
    /// </summary>
    public SquigglyProgressBarControl()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        MinHeight = 26;
        EnsureHandCursor();
        EnsureCanvasCreated();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    private void EnsureHandCursor()
    {
        _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);
        ProtectedCursor = _handCursor;
    }

    private void EnsureCanvasCreated()
    {
        if (_canvas is not null)
        {
            return;
        }

        _canvas = new CanvasControl
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _canvas.Draw += OnCanvasDraw;
        Children.Add(_canvas);
    }

    /// <summary>
    /// Gets or sets the normalized playback progress in the range [0.0, 1.0].
    /// </summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// Gets or sets whether audio playback is currently active.
    /// </summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>
    /// Gets or sets the primary wave and thumb color.
    /// </summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>
    /// Gets or sets whether light mode styling should be used for the inactive track.
    /// </summary>
    public bool IsLightMode
    {
        get => (bool)GetValue(IsLightModeProperty);
        set => SetValue(IsLightModeProperty, value);
    }

    /// <summary>
    /// Gets whether the user is currently dragging the thumb.
    /// </summary>
    public bool IsScrubbing => _isDragging;

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SquigglyProgressBarControl control)
        {
            control.EnsureRenderLoopIfNeeded();
            control.InvalidateCanvasSafe();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureHandCursor();
        EnsureCanvasCreated();

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        _lastRenderTimestampTicks = Environment.TickCount64;
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookRenderLoop();
        EffectiveViewportChanged -= OnEffectiveViewportChanged;

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        ProtectedCursor = null;
        _handCursor?.Dispose();
        _handCursor = null;

        _roundStrokeStyle?.Dispose();
        _roundStrokeStyle = null;

        if (_canvas is not null)
        {
            _canvas.Draw -= OnCanvasDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (IsEffectivelyVisible())
        {
            EnsureRenderLoopIfNeeded();
            InvalidateCanvasSafe();
        }
        else
        {
            UnhookRenderLoop();
        }
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (IsEffectivelyVisible())
        {
            EnsureRenderLoopIfNeeded();
            InvalidateCanvasSafe();
        }
        else
        {
            UnhookRenderLoop();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        EnsureRenderLoopIfNeeded();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        EnsureRenderLoopIfNeeded();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        CapturePointer(e.Pointer);
        _dragProgress = ComputeProgressFromPoint(e.GetCurrentPoint(this).Position.X);
        ScrubbingStateChanged?.Invoke(this, true);
        ScrubbingProgressChanged?.Invoke(this, _dragProgress);
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _dragProgress = ComputeProgressFromPoint(e.GetCurrentPoint(this).Position.X);
        ScrubbingProgressChanged?.Invoke(this, _dragProgress);
        InvalidateCanvasSafe();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        double target = ComputeProgressFromPoint(e.GetCurrentPoint(this).Position.X);
        CompleteScrub(target, emitSeek: true, e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            CompleteScrub(_dragProgress, emitSeek: false, e.Pointer);
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            CompleteScrub(_dragProgress, emitSeek: true, pointer: null);
        }
    }

    private void CompleteScrub(double progress, bool emitSeek, Pointer? pointer)
    {
        _isDragging = false;
        if (pointer != null)
        {
            ReleasePointerCapture(pointer);
        }

        ScrubbingStateChanged?.Invoke(this, false);
        if (emitSeek)
        {
            Progress = progress;
            SeekRequested?.Invoke(this, progress);
        }

        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private double ComputeProgressFromPoint(double pointerX)
    {
        double width = ActualWidth;
        double startX = HorizontalTrackPadding;
        double endX = Math.Max(startX + 1.0, width - HorizontalTrackPadding);
        return Math.Clamp((pointerX - startX) / (endX - startX), 0.0, 1.0);
    }

    private bool IsEffectivelyVisible()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || ActualWidth <= 4 || ActualHeight <= 4)
        {
            return false;
        }

        DependencyObject? current = this;
        int depth = 0;
        while (current is not null && depth < 16)
        {
            if (current is UIElement element &&
                (element.Visibility != Visibility.Visible || element.Opacity <= 0.001))
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
            depth++;
        }

        return true;
    }

    private void EnsureRenderLoopIfNeeded()
    {
        if (!IsEffectivelyVisible())
        {
            UnhookRenderLoop();
            return;
        }

        float targetAmplitude = IsPlaying ? MaxAmplitude : 0f;
        float targetThumbScale = (_isDragging || _isPointerOver) ? 1.32f : 1.0f;

        bool needsAnimation =
            (IsPlaying && ActualWidth > 4) ||
            MathF.Abs(_currentAmplitude - targetAmplitude) > 0.015f ||
            MathF.Abs(_thumbScale - targetThumbScale) > 0.005f;

        if (needsAnimation && !_isRenderingHooked)
        {
            _lastRenderTimestampTicks = Environment.TickCount64;
            CompositionTarget.Rendering += OnCompositionRendering;
            _isRenderingHooked = true;
        }
    }

    private void UnhookRenderLoop()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _isRenderingHooked = false;
        }
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        if (!IsEffectivelyVisible())
        {
            UnhookRenderLoop();
            return;
        }

        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastRenderTimestampTicks) / 1000f, 0.001f, 0.05f);
        _lastRenderTimestampTicks = now;

        float targetAmplitude = IsPlaying ? MaxAmplitude : 0f;
        float ampLerp = 1f - MathF.Exp(-10f * dt);
        _currentAmplitude += (targetAmplitude - _currentAmplitude) * ampLerp;
        if (MathF.Abs(targetAmplitude - _currentAmplitude) < 0.01f)
        {
            _currentAmplitude = targetAmplitude;
        }

        float targetThumbScale = _isDragging ? 1.38f : (_isPointerOver ? 1.24f : 1.0f);
        float thumbLerp = 1f - MathF.Exp(-14f * dt);
        _thumbScale += (targetThumbScale - _thumbScale) * thumbLerp;
        if (MathF.Abs(targetThumbScale - _thumbScale) < 0.004f)
        {
            _thumbScale = targetThumbScale;
        }

        if (_currentAmplitude > 0.02f)
        {
            _wavePhase = (_wavePhase + PhaseSpeed * dt) % (2f * MathF.PI);
        }

        InvalidateCanvasSafe();

        bool stillAnimating =
            IsPlaying ||
            MathF.Abs(_currentAmplitude - targetAmplitude) > 0.01f ||
            MathF.Abs(_thumbScale - targetThumbScale) > 0.004f;

        if (!stillAnimating)
        {
            UnhookRenderLoop();
        }
    }

    private void InvalidateCanvasSafe()
    {
        if (_canvas is not null &&
            _canvas.ActualWidth > 4 &&
            _canvas.ActualHeight > 4 &&
            IsEffectivelyVisible())
        {
            _canvas.Invalidate();
        }
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= 8f || height <= 8f)
        {
            return;
        }

        var ds = args.DrawingSession;
        float startX = HorizontalTrackPadding;
        float endX = Math.Max(startX + 1f, width - HorizontalTrackPadding);
        float trackWidth = endX - startX;
        float centerY = height * 0.5f;

        float normalizedProgress = (float)Math.Clamp(_isDragging ? _dragProgress : Progress, 0.0, 1.0);
        float activeX = startX + trackWidth * normalizedProgress;

        Color accent = AccentColor;
        Color inactiveColor = IsLightMode
            ? Color.FromArgb(62, 15, 23, 42)
            : Color.FromArgb(72, 255, 255, 255);

        var roundStrokeStyle = _roundStrokeStyle ??= new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };

        // 1. Inactive right-side track segment
        if (activeX < endX - 0.5f)
        {
            ds.DrawLine(activeX, centerY, endX, centerY, inactiveColor, StrokeWidth, roundStrokeStyle);
        }

        // 2. Active left-side squiggly wave + radial under-glow
        if (activeX > startX + 0.5f)
        {
            float ampFraction = Math.Clamp(_currentAmplitude / MaxAmplitude, 0f, 1f);
            byte glowAlpha = (byte)Math.Clamp(255f * 0.28f * ampFraction, 0f, 85f);
            Color glowColor = Color.FromArgb(glowAlpha, accent.R, accent.G, accent.B);

            if (_currentAmplitude < 0.08f)
            {
                if (glowAlpha > 0)
                {
                    ds.DrawLine(startX, centerY, activeX, centerY, glowColor, StrokeWidth * 2.15f, roundStrokeStyle);
                }

                ds.DrawLine(startX, centerY, activeX, centerY, accent, StrokeWidth, roundStrokeStyle);
            }
            else
            {
                using var pathBuilder = new CanvasPathBuilder(ds);
                float sampleY0 = ComputeWaveY(startX, startX, activeX, centerY);
                pathBuilder.BeginFigure(startX, sampleY0);

                for (float x = startX + 2f; x < activeX; x += 2f)
                {
                    pathBuilder.AddLine(x, ComputeWaveY(x, startX, activeX, centerY));
                }

                pathBuilder.AddLine(activeX, ComputeWaveY(activeX, startX, activeX, centerY));
                pathBuilder.EndFigure(CanvasFigureLoop.Open);

                using var geometry = CanvasGeometry.CreatePath(pathBuilder);
                if (glowAlpha > 0)
                {
                    ds.DrawGeometry(geometry, glowColor, StrokeWidth * 2.15f, roundStrokeStyle);
                }

                ds.DrawGeometry(geometry, accent, StrokeWidth, roundStrokeStyle);
            }
        }

        // 3. Glowing circular thumb (halo -> solid ring -> white inner dot)
        float haloRadius = 13.5f * _thumbScale;
        Color haloInner = Color.FromArgb(95, accent.R, accent.G, accent.B);
        Color haloOuter = Color.FromArgb(0, accent.R, accent.G, accent.B);
        using (var haloBrush = new CanvasRadialGradientBrush(ds, haloInner, haloOuter)
        {
            Center = new System.Numerics.Vector2(activeX, centerY),
            RadiusX = haloRadius,
            RadiusY = haloRadius
        })
        {
            ds.FillCircle(activeX, centerY, haloRadius, haloBrush);
        }

        float thumbRadius = 6.2f * _thumbScale;
        ds.FillCircle(activeX, centerY, thumbRadius, accent);

        float innerDotRadius = 2.6f * _thumbScale;
        ds.FillCircle(activeX, centerY, innerDotRadius, Colors.White);
    }

    private float ComputeWaveY(float x, float startX, float activeX, float centerY)
    {
        float startTaper = Math.Clamp((x - startX) / (TaperLength * 0.5f), 0f, 1f);
        float endTaper = Math.Clamp((activeX - x) / TaperLength, 0f, 1f);
        float rawTaper = Math.Min(startTaper, endTaper);
        float taper = rawTaper * rawTaper * (3f - 2f * rawTaper);
        float angle = (2f * MathF.PI * ((x - startX) / WaveLength)) - _wavePhase;
        return centerY + (_currentAmplitude * taper * MathF.Sin(angle));
    }
}
