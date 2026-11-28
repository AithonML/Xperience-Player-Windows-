using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Xperience.App.ViewModels;

namespace Xperience.App.Controls;

/// <summary>
/// High-precision synchronized lyrics canvas inspired by the Android Xperience player.
/// Renders word/syllable-level sweep illumination, active letter scale &amp; lift,
/// radial glow, distance-based optical blur &amp; edge fading, and clears blur/opacity
/// back to crisp readability whenever the user scrolls manually.
/// </summary>
public sealed class SynchronizedLyricsCanvasControl : Grid
{
    private const long DefaultLineDurationMs = 3200;
    private const long MinWordDurationMs = 60;
    private const long ScaleReleaseTailMs = 200;
    private const long GlowReleaseTailMs = 380;
    private const long MaxExtrapolationMs = 160;
    private const long SeekSnapThresholdMs = 220;
    private const long UserScrollPauseMs = 3800;

    private const float HorizontalPadding = 14f;
    private const float WordGap = 5.5f;
    private const float RowGap = 3.5f;
    private const float LineGap = 15f;
    private const float DefaultSyncFontSize = 19.5f;
    private const float DefaultPlainFontSize = 15.5f;

    private CanvasControl? _canvas;
    private readonly List<MeasuredLineLayout> _measuredLines = [];
    private INotifyCollectionChanged? _subscribedCollection;
    private InputSystemCursor? _handCursor;
    private long _visibilityCallbackToken;
    private bool _layoutsDirty = true;
    private float _lastLayoutWidth = -1f;

    private bool _isRenderingHooked;
    private long _lastFrameTicks;
    private double _anchorPositionMs;
    private long _anchorTimestampTicks;
    private double _interpolatedPositionMs;
    private double _lastObservedPositionSec = -1.0;

    private float _scrollY;
    private float _targetScrollY;
    private float _maxScrollY;
    private bool _isUserScrolling;
    private long _lastUserScrollMs;
    private float _userScrollBlend;
    private float _smoothedActiveCenterY;
    private int _lastAutoScrollLineIndex = -1;
    private int _hoveredLineIndex = -1;

    private bool _isPointerDown;
    private bool _isDraggingScroll;
    private double _pointerDownY;
    private double _lastPointerY;

    /// <summary>
    /// Identifies the <see cref="Lines"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty LinesProperty =
        DependencyProperty.Register(
            nameof(Lines),
            typeof(IReadOnlyList<SynchronizedLyricLineViewModel>),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(null, OnLinesChanged));

    /// <summary>
    /// Identifies the <see cref="PositionSeconds"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PositionSecondsProperty =
        DependencyProperty.Register(
            nameof(PositionSeconds),
            typeof(double),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(0.0, OnPositionSecondsChanged));

    /// <summary>
    /// Identifies the <see cref="IsPlaying"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(false, OnPlaybackStateChanged));

    /// <summary>
    /// Identifies the <see cref="ActiveLineIndex"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ActiveLineIndexProperty =
        DependencyProperty.Register(
            nameof(ActiveLineIndex),
            typeof(int),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(-1, OnPlaybackStateChanged));

    /// <summary>
    /// Identifies the <see cref="AccentColor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(Color.FromArgb(255, 239, 68, 68), OnVisualStyleChanged));

    /// <summary>
    /// Identifies the <see cref="IsLightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsLightModeProperty =
        DependencyProperty.Register(
            nameof(IsLightMode),
            typeof(bool),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(false, OnVisualStyleChanged));

    /// <summary>
    /// Identifies the <see cref="SyncFontSize"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty SyncFontSizeProperty =
        DependencyProperty.Register(
            nameof(SyncFontSize),
            typeof(double),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata((double)DefaultSyncFontSize, OnTypographyChanged));

    /// <summary>
    /// Identifies the <see cref="EnableVerticalEdgeFade"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty EnableVerticalEdgeFadeProperty =
        DependencyProperty.Register(
            nameof(EnableVerticalEdgeFade),
            typeof(bool),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(false, OnVisualStyleChanged));

    /// <summary>
    /// Identifies the <see cref="IsSuspended"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsSuspendedProperty =
        DependencyProperty.Register(
            nameof(IsSuspended),
            typeof(bool),
            typeof(SynchronizedLyricsCanvasControl),
            new PropertyMetadata(false, OnVisibilityLikeChanged));

    /// <summary>
    /// Raised when the user taps a lyric line to seek playback.
    /// </summary>
    public event EventHandler<SynchronizedLyricLineViewModel>? LineClicked;

    /// <summary>
    /// Raised when manual scroll defocus-override state changes (true while user scrolled off-sync).
    /// </summary>
    public event EventHandler<bool>? UserScrollingChanged;

    /// <summary>
    /// Initializes a new instance of <see cref="SynchronizedLyricsCanvasControl"/>.
    /// </summary>
    public SynchronizedLyricsCanvasControl()
    {
        Background = new SolidColorBrush(Colors.Transparent);

        EnsureCanvasCreated();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += OnPointerExited;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
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
    /// Gets or sets the lyric lines collection displayed by the control.
    /// </summary>
    public IReadOnlyList<SynchronizedLyricLineViewModel>? Lines
    {
        get => (IReadOnlyList<SynchronizedLyricLineViewModel>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>
    /// Gets or sets the current playback position in seconds.
    /// </summary>
    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    /// <summary>
    /// Gets or sets whether playback is actively advancing.
    /// </summary>
    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently active lyric line index.
    /// </summary>
    public int ActiveLineIndex
    {
        get => (int)GetValue(ActiveLineIndexProperty);
        set => SetValue(ActiveLineIndexProperty, value);
    }

    /// <summary>
    /// Gets or sets the accent color used for the radial word glow.
    /// </summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>
    /// Gets or sets whether light mode styling is active.
    /// </summary>
    public bool IsLightMode
    {
        get => (bool)GetValue(IsLightModeProperty);
        set => SetValue(IsLightModeProperty, value);
    }

    /// <summary>
    /// Gets or sets the base font size for synchronized lyrics lines (default 19.5 for sidebar; larger in Fullscreen).
    /// </summary>
    public double SyncFontSize
    {
        get => (double)GetValue(SyncFontSizeProperty);
        set => SetValue(SyncFontSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether to composite the rendered lyrics through a smooth top-and-bottom
    /// pixel-accurate vertical alpha gradient mask for borderless floating surfaces.
    /// </summary>
    public bool EnableVerticalEdgeFade
    {
        get => (bool)GetValue(EnableVerticalEdgeFadeProperty);
        set => SetValue(EnableVerticalEdgeFadeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether rendering is suspended (e.g., while Fullscreen overlay covers the Sidebar).
    /// </summary>
    public bool IsSuspended
    {
        get => (bool)GetValue(IsSuspendedProperty);
        set => SetValue(IsSuspendedProperty, value);
    }

    /// <summary>
    /// Gets whether manual user scrolling is currently overriding auto-center blur &amp; fade.
    /// </summary>
    public bool IsUserScrolling => _isUserScrolling;

    /// <summary>
    /// Resumes automatic lyric tracking and smoothly scrolls back to the active line.
    /// </summary>
    public void RecenterOnActiveLine()
    {
        SetUserScrolling(false);
        _lastAutoScrollLineIndex = -1;
        EnsureRenderLoop();
        InvalidateCanvasSafe();
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SynchronizedLyricsCanvasControl control)
        {
            return;
        }

        if (control._subscribedCollection != null)
        {
            control._subscribedCollection.CollectionChanged -= control.OnLinesCollectionChanged;
            control._subscribedCollection = null;
        }

        if (e.NewValue is INotifyCollectionChanged incc)
        {
            control._subscribedCollection = incc;
            incc.CollectionChanged += control.OnLinesCollectionChanged;
        }

        control.ResetForNewLyrics();
    }

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResetForNewLyrics();
    }

    private static void OnVisibilityLikeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SynchronizedLyricsCanvasControl control)
        {
            return;
        }

        if (control.IsEffectivelyVisible())
        {
            control.EnsureRenderLoop();
            control.InvalidateCanvasSafe();
        }
        else
        {
            control.UnhookRenderLoop();
        }
    }

    private void ResetForNewLyrics()
    {
        DisposeMeasuredLayouts();
        try { CanvasDevice.GetSharedDevice()?.Trim(); } catch { }
        _layoutsDirty = true;
        _scrollY = 0f;
        _targetScrollY = 0f;
        _lastAutoScrollLineIndex = -1;
        _hoveredLineIndex = -1;
        SetUserScrolling(false);
        _userScrollBlend = 0f;
        EnsureRenderLoop();
        InvalidateCanvasSafe();
    }

    private static void OnPositionSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SynchronizedLyricsCanvasControl control)
        {
            return;
        }

        double seconds = (double)e.NewValue;
        double ms = Math.Max(0.0, seconds * 1000.0);
        long nowTicks = Stopwatch.GetTimestamp();

        if (control._lastObservedPositionSec < 0 ||
            Math.Abs(ms - control._interpolatedPositionMs) > SeekSnapThresholdMs)
        {
            control._interpolatedPositionMs = ms;
        }

        control._lastObservedPositionSec = seconds;
        control._anchorPositionMs = ms;
        control._anchorTimestampTicks = nowTicks;

        control.EnsureRenderLoop();
        control.InvalidateCanvasSafe();
    }

    private static void OnPlaybackStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SynchronizedLyricsCanvasControl control)
        {
            control._anchorPositionMs = control.PositionSeconds * 1000.0;
            control._anchorTimestampTicks = Stopwatch.GetTimestamp();
            control.EnsureRenderLoop();
            control.InvalidateCanvasSafe();
        }
    }

    private static void OnVisualStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SynchronizedLyricsCanvasControl control)
        {
            control.InvalidateCanvasSafe();
        }
    }

    private static void OnTypographyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SynchronizedLyricsCanvasControl control)
        {
            control._layoutsDirty = true;
            control._lastAutoScrollLineIndex = -1;
            control.InvalidateCanvasSafe();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureCanvasCreated();

        if (_subscribedCollection is null && Lines is INotifyCollectionChanged incc)
        {
            _subscribedCollection = incc;
            incc.CollectionChanged += OnLinesCollectionChanged;
        }

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        if (_measuredLines.Count == 0)
        {
            _layoutsDirty = true;
        }

        _lastFrameTicks = Stopwatch.GetTimestamp();
        _anchorTimestampTicks = _lastFrameTicks;
        _anchorPositionMs = PositionSeconds * 1000.0;
        _interpolatedPositionMs = _anchorPositionMs;
        EnsureRenderLoop();
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

        DisposeMeasuredLayouts();
        _layoutsDirty = true;
        _lastLayoutWidth = -1f;

        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnLinesCollectionChanged;
            _subscribedCollection = null;
        }

        ProtectedCursor = null;
        _handCursor?.Dispose();
        _handCursor = null;

        if (_canvas is not null)
        {
            _canvas.Draw -= OnCanvasDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }

        try
        {
            CanvasDevice.GetSharedDevice()?.Trim();
        }
        catch
        {
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (IsEffectivelyVisible())
        {
            EnsureRenderLoop();
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
            EnsureRenderLoop();
            InvalidateCanvasSafe();
        }
        else
        {
            UnhookRenderLoop();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        float w = (float)e.NewSize.Width;
        if (MathF.Abs(w - _lastLayoutWidth) > 1f)
        {
            _layoutsDirty = true;
            _lastAutoScrollLineIndex = -1;
        }

        EnsureRenderLoop();
        InvalidateCanvasSafe();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_maxScrollY <= 1f)
        {
            return;
        }

        int wheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        float step = -(wheelDelta / 120f) * 58f;
        _targetScrollY = Math.Clamp(_targetScrollY + step, 0f, _maxScrollY);
        _scrollY = Math.Clamp(_scrollY + step * 0.45f, 0f, _maxScrollY);
        MarkUserScrollActivity();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPointerDown = true;
        _isDraggingScroll = false;
        _pointerDownY = point.Position.Y;
        _lastPointerY = point.Position.Y;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        if (_isPointerDown)
        {
            double dy = pos.Y - _lastPointerY;
            _lastPointerY = pos.Y;

            if (!_isDraggingScroll && Math.Abs(pos.Y - _pointerDownY) > 6.0)
            {
                _isDraggingScroll = true;
            }

            if (_isDraggingScroll)
            {
                float nextScroll = Math.Clamp(_scrollY - (float)dy, 0f, _maxScrollY);
                _scrollY = nextScroll;
                _targetScrollY = nextScroll;
                MarkUserScrollActivity();
                InvalidateCanvasSafe();
                e.Handled = true;
                return;
            }
        }

        UpdateHoverLine((float)pos.Y);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        var pos = e.GetCurrentPoint(this).Position;
        bool wasDragging = _isDraggingScroll;
        _isPointerDown = false;
        _isDraggingScroll = false;
        ReleasePointerCapture(e.Pointer);

        if (!wasDragging)
        {
            int hitIdx = HitTestLine((float)pos.Y);
            if (hitIdx >= 0 && Lines != null && hitIdx < Lines.Count)
            {
                SetUserScrolling(false);
                _lastAutoScrollLineIndex = -1;
                LineClicked?.Invoke(this, Lines[hitIdx]);
            }
        }

        e.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredLineIndex != -1)
        {
            _hoveredLineIndex = -1;
            ProtectedCursor = null;
            InvalidateCanvasSafe();
        }
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = false;
        _isDraggingScroll = false;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPointerDown = false;
        _isDraggingScroll = false;
    }

    private void MarkUserScrollActivity()
    {
        _lastUserScrollMs = Environment.TickCount64;
        SetUserScrolling(true);
        EnsureRenderLoop();
        InvalidateCanvasSafe();
    }

    private void SetUserScrolling(bool value)
    {
        if (_isUserScrolling == value)
        {
            return;
        }

        _isUserScrolling = value;
        UserScrollingChanged?.Invoke(this, value);
    }

    private int HitTestLine(float pointerY)
    {
        float contentY = pointerY + _scrollY;
        for (int i = 0; i < _measuredLines.Count; i++)
        {
            var line = _measuredLines[i];
            if (contentY >= line.TopY - 8f && contentY <= line.BottomY + 8f)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateHoverLine(float pointerY)
    {
        int hit = HitTestLine(pointerY);
        if (hit != _hoveredLineIndex)
        {
            _hoveredLineIndex = hit;
            if (hit >= 0)
            {
                _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);
                ProtectedCursor = _handCursor;
            }
            else
            {
                ProtectedCursor = null;
            }

            InvalidateCanvasSafe();
        }
    }

    private bool IsEffectivelyVisible()
    {
        if (IsSuspended || !IsLoaded || Visibility != Visibility.Visible || ActualWidth <= 4 || ActualHeight <= 4)
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

    private void EnsureRenderLoop()
    {
        if (!IsEffectivelyVisible())
        {
            UnhookRenderLoop();
            return;
        }

        bool needsLoop =
            IsPlaying ||
            _isUserScrolling ||
            MathF.Abs(_userScrollBlend - (_isUserScrolling ? 1f : 0f)) > 0.005f ||
            MathF.Abs(_scrollY - _targetScrollY) > 0.4f;

        if (needsLoop && !_isRenderingHooked)
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
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

        long nowTicks = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)((nowTicks - _lastFrameTicks) / (double)Stopwatch.Frequency), 0.001f, 0.05f);
        _lastFrameTicks = nowTicks;

        if (IsPlaying)
        {
            double elapsedSinceAnchorMs = (nowTicks - _anchorTimestampTicks) * 1000.0 / Stopwatch.Frequency;
            double extrapolatedMs = _anchorPositionMs + Math.Clamp(elapsedSinceAnchorMs, 0.0, MaxExtrapolationMs);
            if (extrapolatedMs > _interpolatedPositionMs)
            {
                double blend = 1.0 - Math.Exp(-16.0 * dt);
                _interpolatedPositionMs += (extrapolatedMs - _interpolatedPositionMs) * blend;
            }
        }
        else
        {
            _interpolatedPositionMs = _anchorPositionMs;
        }

        if (_isUserScrolling && Environment.TickCount64 - _lastUserScrollMs >= UserScrollPauseMs)
        {
            SetUserScrolling(false);
            _lastAutoScrollLineIndex = -1;
        }

        float targetBlend = _isUserScrolling ? 1f : 0f;
        float blendSpeed = _isUserScrolling ? 14f : 5.5f;
        _userScrollBlend += (targetBlend - _userScrollBlend) * (1f - MathF.Exp(-blendSpeed * dt));
        if (MathF.Abs(targetBlend - _userScrollBlend) < 0.003f)
        {
            _userScrollBlend = targetBlend;
        }

        if (!_isDraggingScroll)
        {
            float scrollLerp = 1f - MathF.Exp(-8.5f * dt);
            _scrollY += (_targetScrollY - _scrollY) * scrollLerp;
            if (MathF.Abs(_targetScrollY - _scrollY) < 0.35f)
            {
                _scrollY = _targetScrollY;
            }
        }

        InvalidateCanvasSafe();

        bool keepRunning =
            IsPlaying ||
            _isUserScrolling ||
            MathF.Abs(_userScrollBlend - targetBlend) > 0.004f ||
            MathF.Abs(_scrollY - _targetScrollY) > 0.4f;

        if (!keepRunning)
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
        if (width <= 12f || height <= 12f)
        {
            return;
        }

        var lines = Lines;
        if (lines == null || lines.Count == 0)
        {
            return;
        }

        if (_layoutsDirty || MathF.Abs(width - _lastLayoutWidth) > 1f)
        {
            RebuildLayouts(sender, lines, width, height);
        }

        if (_measuredLines.Count == 0)
        {
            return;
        }

        bool hasSynchronized = _measuredLines[0].IsSynchronized;
        int activeIndex = hasSynchronized
            ? ResolveActiveLineIndex(_measuredLines, _interpolatedPositionMs, ActiveLineIndex)
            : -1;

        if (!_isUserScrolling && hasSynchronized && activeIndex >= 0 && activeIndex < _measuredLines.Count)
        {
            var activeLine = _measuredLines[activeIndex];
            float lineCenterY = (activeLine.TopY + activeLine.BottomY) * 0.5f;
            float desiredTopScroll = Math.Clamp(lineCenterY - height * 0.38f, 0f, _maxScrollY);

            if (activeIndex != _lastAutoScrollLineIndex || MathF.Abs(desiredTopScroll - _targetScrollY) > 2f)
            {
                _targetScrollY = desiredTopScroll;
                if (_lastAutoScrollLineIndex == -1)
                {
                    _scrollY = desiredTopScroll;
                    _smoothedActiveCenterY = lineCenterY;
                }

                _lastAutoScrollLineIndex = activeIndex;
                EnsureRenderLoop();
            }
        }

        var rootDs = args.DrawingSession;
        bool isLight = IsLightMode;
        bool useAlphaMask = EnableVerticalEdgeFade;
        Color accent = AccentColor;
        Color litInk = isLight ? Color.FromArgb(255, 15, 23, 42) : Colors.White;
        Color inactiveBaseInk = isLight ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 248, 250, 252);
        Color glowTint = ComputeGlowTint(accent, isLight);

        if (useAlphaMask)
        {
            using var contentList = new CanvasCommandList(rootDs);
            using (var contentDs = contentList.CreateDrawingSession())
            {
                DrawLyricsPass(contentDs, width, height, hasSynchronized, activeIndex, isLight, litInk, inactiveBaseInk, glowTint, useAlphaMask: true);
            }

            using var maskList = new CanvasCommandList(rootDs);
            using (var maskDs = maskList.CreateDrawingSession())
            {
                var maskStops = new[]
                {
                    new CanvasGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)),
                    new CanvasGradientStop(0.10f, Colors.White),
                    new CanvasGradientStop(0.88f, Colors.White),
                    new CanvasGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255))
                };
                using var maskBrush = new CanvasLinearGradientBrush(maskDs, maskStops)
                {
                    StartPoint = new Vector2(0f, 0f),
                    EndPoint = new Vector2(0f, height)
                };
                maskDs.FillRectangle(0f, 0f, width, height, maskBrush);
            }

            using var alphaMask = new AlphaMaskEffect
            {
                Source = contentList,
                AlphaMask = maskList
            };
            rootDs.DrawImage(alphaMask);
        }
        else
        {
            DrawLyricsPass(rootDs, width, height, hasSynchronized, activeIndex, isLight, litInk, inactiveBaseInk, glowTint, useAlphaMask: false);
        }
    }

    private void DrawLyricsPass(
        CanvasDrawingSession ds,
        float width,
        float height,
        bool hasSynchronized,
        int activeIndex,
        bool isLight,
        Color litInk,
        Color inactiveBaseInk,
        Color glowTint,
        bool useAlphaMask)
    {
        // Bucket blurred inactive lines into 3 shared blur tiers so each frame allocates at most 3
        // CanvasCommandList + GaussianBlurEffect pairs instead of 15-20 per-line GPU intermediates.
        CanvasCommandList?[] tierLists = new CanvasCommandList?[3];
        CanvasDrawingSession?[] tierSessions = new CanvasDrawingSession?[3];
        float[] tierBlurAmounts = [1.05f, 2.35f, 3.95f];

        try
        {
            for (int i = 0; i < _measuredLines.Count; i++)
            {
                var line = _measuredLines[i];
                float screenTop = line.TopY - _scrollY;
                float screenBottom = line.BottomY - _scrollY;
                if (screenBottom < -70f || screenTop > height + 70f)
                {
                    continue;
                }

                float lineCenterScreenY = (screenTop + screenBottom) * 0.5f;
                bool isActive = hasSynchronized && i == activeIndex;
                int distance = hasSynchronized && activeIndex >= 0 ? Math.Abs(i - activeIndex) : 0;

                float syncAlpha = !hasSynchronized ? 0.90f : (distance switch
                {
                    0 => 1.00f,
                    1 => 0.62f,
                    2 => 0.40f,
                    3 => 0.24f,
                    _ => 0.14f
                });

                float syncBlur = !hasSynchronized ? 0f : (distance switch
                {
                    0 => 0f,
                    1 => 0.9f,
                    2 => 2.1f,
                    3 => 3.3f,
                    _ => 4.3f
                });

                // Fade + blur smoothly as lines exit top or bottom viewport edges
                float topVis = Math.Clamp((lineCenterScreenY - 12f) / 64f, 0f, 1f);
                float bottomVis = Math.Clamp((height - 14f - lineCenterScreenY) / 72f, 0f, 1f);
                float edgeVis = Math.Min(topVis, bottomVis);
                float edgeAlphaMul = useAlphaMask ? 1.0f : (0.16f + 0.84f * edgeVis);
                float edgeBlur = (1f - edgeVis) * (useAlphaMask ? 2.6f : 4.2f);

                float targetNormalAlpha = Math.Max(0.80f, syncAlpha);
                float finalAlpha = isActive
                    ? Lerp(edgeAlphaMul, 1.0f, _userScrollBlend)
                    : Lerp(syncAlpha * edgeAlphaMul, targetNormalAlpha, _userScrollBlend);

                float combinedSyncBlur = isActive ? edgeBlur * 0.65f : (syncBlur + edgeBlur);
                float finalBlur = Lerp(combinedSyncBlur, 0f, _userScrollBlend);

                if (i == _hoveredLineIndex)
                {
                    Color hoverBg = isLight
                        ? Color.FromArgb(24, 15, 23, 42)
                        : Color.FromArgb(24, 255, 255, 255);
                    ds.FillRoundedRectangle(
                        HorizontalPadding - 8f,
                        screenTop - 4f,
                        width - (HorizontalPadding * 2f) + 16f,
                        (line.BottomY - line.TopY) + 8f,
                        12f,
                        12f,
                        hoverBg);
                }

                if (isActive)
                {
                    DrawActiveLine(ds, line, -_scrollY, litInk, glowTint, finalAlpha);
                }
                else if (finalBlur <= 0.22f)
                {
                    DrawInactiveLineDirect(ds, line, -_scrollY, inactiveBaseInk, finalAlpha);
                }
                else
                {
                    int tier = finalBlur < 1.65f ? 0 : (finalBlur < 3.15f ? 1 : 2);
                    if (tierSessions[tier] is null)
                    {
                        tierLists[tier] = new CanvasCommandList(ds);
                        tierSessions[tier] = tierLists[tier]!.CreateDrawingSession();
                    }

                    DrawInactiveLineDirect(tierSessions[tier]!, line, -_scrollY, inactiveBaseInk, finalAlpha);
                }
            }

            for (int t = 0; t < tierLists.Length; t++)
            {
                if (tierLists[t] is null)
                {
                    continue;
                }

                tierSessions[t]?.Dispose();
                tierSessions[t] = null;

                using var blurEffect = new GaussianBlurEffect
                {
                    Source = tierLists[t],
                    BlurAmount = tierBlurAmounts[t],
                    BorderMode = EffectBorderMode.Soft,
                    Optimization = EffectOptimization.Speed
                };
                ds.DrawImage(blurEffect);
            }
        }
        finally
        {
            for (int t = 0; t < tierLists.Length; t++)
            {
                tierSessions[t]?.Dispose();
                tierLists[t]?.Dispose();
            }
        }
    }

    private void DrawActiveLine(
        CanvasDrawingSession ds,
        MeasuredLineLayout line,
        float offsetY,
        Color litInk,
        Color glowTint,
        float lineAlpha)
    {
        Color unlitInk = Color.FromArgb(
            (byte)Math.Clamp(MathF.Round(litInk.A * 0.36f * lineAlpha), 0, 255),
            litInk.R,
            litInk.G,
            litInk.B);
        Color fullLitInk = Color.FromArgb(
            (byte)Math.Clamp(MathF.Round(litInk.A * lineAlpha), 0, 255),
            litInk.R,
            litInk.G,
            litInk.B);

        var prevTransform = ds.Transform;
        foreach (var word in line.Words)
        {
            var state = ComputeWordVisualState(word.Token, _interpolatedPositionMs);
            float cx = word.X + word.Width * 0.5f;
            float cy = word.Y + offsetY + word.Height * 0.5f + state.LiftY;

            if (state.GlowIntensity > 0.01f)
            {
                float haloCenterX = word.X + Math.Clamp(state.SweepProgress, 0.15f, 0.9f) * word.Width;
                float outerRadius = Math.Max(word.Width * 0.72f, 26f);
                byte outerA = (byte)Math.Clamp(MathF.Round(138f * state.GlowIntensity * lineAlpha), 0, 220);
                if (outerA > 1)
                {
                    using var haloBrush = new CanvasRadialGradientBrush(
                        ds,
                        Color.FromArgb(outerA, glowTint.R, glowTint.G, glowTint.B),
                        Color.FromArgb(0, glowTint.R, glowTint.G, glowTint.B))
                    {
                        Center = new Vector2(haloCenterX, cy),
                        RadiusX = outerRadius,
                        RadiusY = word.Height * 1.15f
                    };
                    ds.FillEllipse(haloCenterX, cy, outerRadius, word.Height * 1.15f, haloBrush);
                }
            }

            ds.Transform =
                Matrix3x2.CreateScale(state.Scale, new Vector2(cx, cy)) *
                Matrix3x2.CreateTranslation(0f, state.LiftY) *
                prevTransform;

            if (state.SweepProgress <= 0.005f)
            {
                ds.DrawTextLayout(word.Layout, word.X, word.Y + offsetY, unlitInk);
            }
            else if (state.SweepProgress >= 0.995f)
            {
                ds.DrawTextLayout(word.Layout, word.X, word.Y + offsetY, fullLitInk);
            }
            else
            {
                float feather = 0.18f;
                float litEnd = Math.Clamp(state.SweepProgress - feather, 0f, 1f);
                float unlitStart = Math.Clamp(state.SweepProgress + feather, litEnd, 1f);

                var stops = new[]
                {
                    new CanvasGradientStop(0f, fullLitInk),
                    new CanvasGradientStop(litEnd, fullLitInk),
                    new CanvasGradientStop(unlitStart, unlitInk),
                    new CanvasGradientStop(1f, unlitInk)
                };

                using var sweepBrush = new CanvasLinearGradientBrush(ds, stops)
                {
                    StartPoint = new Vector2(word.X, word.Y + offsetY),
                    EndPoint = new Vector2(word.X + Math.Max(word.Width, 1f), word.Y + offsetY)
                };
                ds.DrawTextLayout(word.Layout, word.X, word.Y + offsetY, sweepBrush);
            }

            ds.Transform = prevTransform;
        }
    }

    private static void DrawInactiveLineDirect(
        CanvasDrawingSession ds,
        MeasuredLineLayout line,
        float offsetY,
        Color baseInk,
        float alpha)
    {
        byte a = (byte)Math.Clamp(MathF.Round(alpha * 255f), 0, 255);
        if (a <= 1)
        {
            return;
        }

        Color tint = Color.FromArgb(a, baseInk.R, baseInk.G, baseInk.B);
        var prevTransform = ds.Transform;
        foreach (var word in line.Words)
        {
            if (line.IsSynchronized)
            {
                float cx = word.X + word.Width * 0.5f;
                float cy = word.Y + offsetY + word.Height * 0.5f;
                ds.Transform = Matrix3x2.CreateScale(0.95f, new Vector2(cx, cy)) * prevTransform;
            }

            ds.DrawTextLayout(word.Layout, word.X, word.Y + offsetY, tint);
            ds.Transform = prevTransform;
        }
    }

    private void RebuildLayouts(
        CanvasControl sender,
        IReadOnlyList<SynchronizedLyricLineViewModel> lines,
        float width,
        float viewportHeight)
    {
        DisposeMeasuredLayouts();
        _lastLayoutWidth = width;
        _layoutsDirty = false;

        bool hasSynced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].IsSynchronized)
            {
                hasSynced = true;
                break;
            }
        }

        float syncSize = (float)Math.Clamp(SyncFontSize, 12.0, 44.0);
        float plainSize = syncSize * (DefaultPlainFontSize / DefaultSyncFontSize);
        float scaleFactor = syncSize / DefaultSyncFontSize;
        float effectiveWordGap = WordGap * scaleFactor;
        float effectiveRowGap = RowGap * scaleFactor;
        float effectiveLineGap = LineGap * scaleFactor;

        float fontSize = hasSynced ? syncSize : plainSize;
        using var textFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Display, Segoe UI",
            FontSize = fontSize,
            FontWeight = hasSynced ? FontWeights.Bold : FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        float maxContentWidth = Math.Max(80f, width - (HorizontalPadding * 2f));
        float currentY = viewportHeight * 0.30f;

        for (int i = 0; i < lines.Count; i++)
        {
            var vm = lines[i];
            long lineStartMs = (long)vm.Timestamp.TotalMilliseconds;
            long nextStartMs = (i + 1 < lines.Count)
                ? (long)lines[i + 1].Timestamp.TotalMilliseconds
                : lineStartMs + DefaultLineDurationMs;
            long lineEndMs = vm.EndTimestamp > vm.Timestamp
                ? (long)vm.EndTimestamp.TotalMilliseconds
                : (nextStartMs > lineStartMs ? nextStartMs : lineStartMs + DefaultLineDurationMs);

            var wordTokens = TokenizeLine(vm, lineStartMs, lineEndMs);
            if (wordTokens.Count == 0)
            {
                currentY += effectiveLineGap * 0.8f;
                _measuredLines.Add(new MeasuredLineLayout(lineStartMs, lineEndMs, vm.IsSynchronized, currentY, currentY + 12f, []));
                continue;
            }

            var placedWords = new List<MeasuredWordLayout>(wordTokens.Count);
            float cursorX = HorizontalPadding;
            float rowTop = currentY;
            float rowHeight = fontSize * 1.28f;
            float maxBottom = rowTop + rowHeight;

            for (int w = 0; w < wordTokens.Count; w++)
            {
                var token = wordTokens[w];
                var textLayout = new CanvasTextLayout(sender.Device, token.Text, textFormat, maxContentWidth, 200f);
                float wordWidth = (float)textLayout.LayoutBounds.Width + 1f;
                float wordHeight = Math.Max(rowHeight, (float)textLayout.LayoutBounds.Height);

                if (cursorX > HorizontalPadding && cursorX + wordWidth > width - HorizontalPadding)
                {
                    cursorX = HorizontalPadding;
                    rowTop += rowHeight + effectiveRowGap;
                }

                placedWords.Add(new MeasuredWordLayout(token, textLayout, cursorX, rowTop, wordWidth, wordHeight));
                maxBottom = Math.Max(maxBottom, rowTop + wordHeight);

                float advance = token.AppendSpaceAfter ? (wordWidth + effectiveWordGap) : wordWidth;
                cursorX += advance;
            }

            _measuredLines.Add(new MeasuredLineLayout(
                lineStartMs,
                lineEndMs,
                vm.IsSynchronized,
                currentY,
                maxBottom,
                placedWords));

            currentY = maxBottom + effectiveLineGap;
        }

        float totalContentHeight = currentY + viewportHeight * 0.44f;
        _maxScrollY = Math.Max(0f, totalContentHeight - viewportHeight);
    }

    private void DisposeMeasuredLayouts()
    {
        for (int i = 0; i < _measuredLines.Count; i++)
        {
            foreach (var word in _measuredLines[i].Words)
            {
                word.Layout.Dispose();
            }
        }

        _measuredLines.Clear();
    }

    private static int ResolveActiveLineIndex(
        IReadOnlyList<MeasuredLineLayout> lines,
        double positionMs,
        int fallbackActiveIndex)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        long pos = (long)positionMs;
        int found = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartMs <= pos)
            {
                found = i;
            }
            else
            {
                break;
            }
        }

        return found >= 0 ? found : (fallbackActiveIndex >= 0 ? fallbackActiveIndex : 0);
    }

    private static List<LyricWordToken> TokenizeLine(
        SynchronizedLyricLineViewModel vm,
        long lineStartMs,
        long lineEndMs)
    {
        var result = new List<LyricWordToken>();

        // 1. Real syllable/word timings available from LRC / YouLyPlus / Apple-style timed lyrics
        if (vm.Syllables != null && vm.Syllables.Count > 0)
        {
            var currentWordText = new System.Text.StringBuilder();
            var currentSpans = new List<LyricGlyphSpan>();
            long wordStartMs = long.MaxValue;
            long wordEndMs = 0;

            void FlushCurrentWord(bool appendSpace)
            {
                if (currentWordText.Length == 0 || currentSpans.Count == 0)
                {
                    currentWordText.Clear();
                    currentSpans.Clear();
                    wordStartMs = long.MaxValue;
                    wordEndMs = 0;
                    return;
                }

                result.Add(new LyricWordToken(
                    currentWordText.ToString(),
                    wordStartMs,
                    Math.Max(wordStartMs + MinWordDurationMs, wordEndMs),
                    appendSpace,
                    [.. currentSpans]));

                currentWordText.Clear();
                currentSpans.Clear();
                wordStartMs = long.MaxValue;
                wordEndMs = 0;
            }

            foreach (var syl in vm.Syllables)
            {
                string raw = syl.Text ?? string.Empty;
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                bool leadingSpace = char.IsWhiteSpace(raw[0]);
                bool trailingSpace = char.IsWhiteSpace(raw[^1]);
                string trimmed = raw.Trim();

                if (leadingSpace && currentWordText.Length > 0)
                {
                    FlushCurrentWord(appendSpace: true);
                }

                if (trimmed.Length == 0)
                {
                    if (currentWordText.Length > 0)
                    {
                        FlushCurrentWord(appendSpace: true);
                    }
                    continue;
                }

                long sMs = (long)syl.StartTime.TotalMilliseconds;
                long eMs = Math.Max(sMs + MinWordDurationMs, (long)syl.EndTime.TotalMilliseconds);
                int charOffset = currentWordText.Length;
                currentWordText.Append(trimmed);
                currentSpans.Add(new LyricGlyphSpan(charOffset, trimmed.Length, sMs, eMs));
                wordStartMs = Math.Min(wordStartMs, sMs);
                wordEndMs = Math.Max(wordEndMs, eMs);

                if (trailingSpace)
                {
                    FlushCurrentWord(appendSpace: true);
                }
            }

            FlushCurrentWord(appendSpace: false);
            if (result.Count > 0)
            {
                return result;
            }
        }

        // 2. Fallback: synthesize expressive character-weighted word timings from line timestamps
        string text = vm.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var rawTokens = SplitDisplayTokens(text);
        if (rawTokens.Count == 0)
        {
            return result;
        }

        long rawDuration = Math.Max(MinWordDurationMs * rawTokens.Count, lineEndMs - lineStartMs);
        long cappedDuration = Math.Min(rawDuration, Math.Max(DefaultLineDurationMs, rawTokens.Count * 620L));

        double totalWeight = 0;
        var weights = new double[rawTokens.Count];
        for (int i = 0; i < rawTokens.Count; i++)
        {
            string tok = rawTokens[i].word;
            double w = 0.0;
            foreach (char ch in tok)
            {
                w += char.IsLetterOrDigit(ch) ? 1.0 : 0.45;
            }

            if (tok.EndsWith(',') || tok.EndsWith(';'))
            {
                w += 0.35;
            }
            else if (tok.EndsWith('.') || tok.EndsWith('!') || tok.EndsWith('?'))
            {
                w += 0.65;
            }

            weights[i] = Math.Max(0.8, w);
            totalWeight += weights[i];
        }

        double cursorMs = lineStartMs;
        for (int i = 0; i < rawTokens.Count; i++)
        {
            double spanMs = Math.Max(MinWordDurationMs, (weights[i] / totalWeight) * cappedDuration);
            long sMs = (long)Math.Round(cursorMs);
            long eMs = (i == rawTokens.Count - 1)
                ? lineStartMs + cappedDuration
                : (long)Math.Round(cursorMs + spanMs);
            eMs = Math.Max(sMs + MinWordDurationMs, eMs);

            string word = rawTokens[i].word;
            result.Add(new LyricWordToken(
                word,
                sMs,
                eMs,
                rawTokens[i].appendSpace,
                [new LyricGlyphSpan(0, word.Length, sMs, eMs)]));
            cursorMs += spanMs;
        }

        return result;
    }

    private static List<(string word, bool appendSpace)> SplitDisplayTokens(string text)
    {
        var tokens = new List<(string, bool)>();
        var buf = new System.Text.StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (buf.Length > 0)
                {
                    tokens.Add((buf.ToString(), true));
                    buf.Clear();
                }
            }
            else if (IsCjkChar(c))
            {
                if (buf.Length > 0)
                {
                    tokens.Add((buf.ToString(), false));
                    buf.Clear();
                }

                tokens.Add((c.ToString(), false));
            }
            else
            {
                buf.Append(c);
            }
        }

        if (buf.Length > 0)
        {
            tokens.Add((buf.ToString(), false));
        }

        return tokens;
    }

    private static bool IsCjkChar(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\uAC00' && c <= '\uD7AF');

    private static WordVisualState ComputeWordVisualState(LyricWordToken token, double positionMs)
    {
        if (positionMs < token.StartMs)
        {
            return new WordVisualState(0.94f, 0f, 0f, 0f);
        }

        if (positionMs >= token.EndMs)
        {
            double sinceEnd = positionMs - token.EndMs;
            float peakBoost = token.Text.Length <= 5 ? 0.16f : (token.Text.Length <= 9 ? 0.14f : 0.12f);
            float scaleRelease = 1f - Math.Clamp((float)(sinceEnd / ScaleReleaseTailMs), 0f, 1f);
            scaleRelease *= scaleRelease;
            float scale = 1.00f + (peakBoost * 0.28f * scaleRelease);
            float lift = -4.5f * 0.25f * scaleRelease;

            float glowRelease = 1f - Math.Clamp((float)(sinceEnd / GlowReleaseTailMs), 0f, 1f);
            float glow = 0.08f + (0.65f * glowRelease * glowRelease);
            return new WordVisualState(scale, lift, glow, 1f);
        }

        // Compute syllable-weighted progress inside the word
        float sweep = 0f;
        int totalChars = Math.Max(1, token.Text.Length);
        if (token.Spans.Count <= 1)
        {
            sweep = Math.Clamp((float)((positionMs - token.StartMs) / Math.Max(1.0, token.EndMs - token.StartMs)), 0f, 1f);
        }
        else
        {
            int completedChars = 0;
            foreach (var span in token.Spans)
            {
                if (positionMs >= span.EndMs)
                {
                    completedChars = span.CharOffset + span.CharLength;
                }
                else if (positionMs > span.StartMs)
                {
                    float frac = Math.Clamp((float)((positionMs - span.StartMs) / Math.Max(1.0, span.EndMs - span.StartMs)), 0f, 1f);
                    sweep = (completedChars + span.CharLength * frac) / totalChars;
                    completedChars = -1;
                    break;
                }
                else
                {
                    sweep = (float)completedChars / totalChars;
                    completedChars = -1;
                    break;
                }
            }

            if (completedChars >= 0)
            {
                sweep = 1f;
            }
        }

        float attack = SmoothStep(Math.Clamp(sweep / 0.26f, 0f, 1f));
        float arch = 0.72f + 0.28f * MathF.Sin(sweep * MathF.PI);
        float envelope = attack * arch;
        float activeBoost = token.Text.Length <= 5 ? 0.16f : (token.Text.Length <= 9 ? 0.14f : 0.12f);
        float baseScale = 0.94f + 0.06f * SmoothStep(Math.Clamp(sweep / 0.34f, 0f, 1f));
        float activeScale = baseScale + activeBoost * envelope;
        float activeLift = -4.5f * envelope;
        float activeGlow = Math.Clamp(0.25f + 0.75f * envelope, 0f, 1f);

        return new WordVisualState(activeScale, activeLift, activeGlow, sweep);
    }

    private static Color ComputeGlowTint(Color accent, bool isLight)
    {
        if (isLight)
        {
            return accent;
        }

        byte r = (byte)Math.Clamp((int)(accent.R + (255 - accent.R) * 0.34f), 0, 255);
        byte g = (byte)Math.Clamp((int)(accent.G + (255 - accent.G) * 0.34f), 0, 255);
        byte b = (byte)Math.Clamp((int)(accent.B + (255 - accent.B) * 0.34f), 0, 255);
        return Color.FromArgb(255, r, g, b);
    }

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private sealed record LyricGlyphSpan(int CharOffset, int CharLength, long StartMs, long EndMs);

    private sealed record LyricWordToken(
        string Text,
        long StartMs,
        long EndMs,
        bool AppendSpaceAfter,
        IReadOnlyList<LyricGlyphSpan> Spans);

    private readonly record struct WordVisualState(
        float Scale,
        float LiftY,
        float GlowIntensity,
        float SweepProgress);

    private sealed record MeasuredWordLayout(
        LyricWordToken Token,
        CanvasTextLayout Layout,
        float X,
        float Y,
        float Width,
        float Height);

    private sealed record MeasuredLineLayout(
        long StartMs,
        long EndMs,
        bool IsSynchronized,
        float TopY,
        float BottomY,
        IReadOnlyList<MeasuredWordLayout> Words);
}
