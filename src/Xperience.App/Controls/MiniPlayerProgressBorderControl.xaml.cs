using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// GPU-accelerated (Win2D + Composition) rounded-pill perimeter progress border and gelatinous
/// press/scrub physics host for the Floating Mini Player capsule.
/// Mirrors Android's <c>PillPerimeterProgress.kt</c> and <c>FloatingMiniPlayer.kt</c>:
/// <list type="bullet">
/// <item>Clockwise perimeter progress arc starting at top-center <c>(width / 2, 0)</c> with 3-pass HSV-calibrated ambient bloom.</item>
/// <item>Smooth 220ms crossfade into an indeterminate orbiting loading sweep when <see cref="IsLoading"/> is <c>true</c>.</item>
/// <item>Springy gelatinous squash-and-stretch physics on press, border hold-to-scrub, horizontal drag-to-scrub, and release recoil.</item>
/// <item>Automatic gesture disambiguation: quick click on capsule body raises <see cref="Clicked"/> while child transport buttons remain unaffected; hold-on-border (190ms) or horizontal drag (&gt;= 6px) captures live progress scrubbing.</item>
/// </list>
/// </summary>
[ContentProperty(Name = nameof(InnerContent))]
public sealed partial class MiniPlayerProgressBorderControl : UserControl
{
    private const int PerimeterSampleCount = 256;
    private const int ClosestRefineSteps = 8;

    private const float LoadingCycleSeconds = 1.05f;
    private const float LoadingBaseLapsPerSec = 0.52f;
    private const float LoadingCycleGain = 0.44f;
    private const float LoadingMinSweep = 0.08f;
    private const float LoadingMaxSweepDelta = 0.44f;

    private const float LoadingCrossfadeDurationSec = 0.22f;
    private const float AccentColorTransitionSec = 0.42f;
    private const long BorderHoldTimeoutMs = 190L;
    private const float BorderGrabBandPx = 22f;
    private const float DragThresholdPx = 6f;
    private const float HoldCancelSlopPx = 8f;
    private const long PendingSeekTimeoutMs = 450L;
    private const double PendingSeekCatchupTolerance = 0.025;

    private const float IdleStrokeWidth = 2.4f;
    private const float LoadingBaseStrokeWidth = 2.8f;
    private const float ScrubTargetStrokeWidth = 4.5f;

    private IThemeService? _themeService;
    private CanvasControl? _canvas;
    private InputSystemCursor? _handCursor;
    private CanvasStrokeStyle? _roundStrokeStyle;
    private long _visibilityCallbackToken;
    private long _cornerRadiusCallbackToken;

    private PillPerimeterGeometry? _geometry;
    private float _lastGeomWidth = -1f;
    private float _lastGeomHeight = -1f;
    private float _lastGeomRadius = -1f;

    private bool _isRenderingHooked;
    private long _lastRenderTickMs;

    // Color interpolation (420ms FastOutSlowIn)
    private Color _renderedAccentColor = Color.FromArgb(255, 45, 212, 191);
    private Color _accentAnimStartColor = Color.FromArgb(255, 45, 212, 191);
    private Color _accentAnimTargetColor = Color.FromArgb(255, 45, 212, 191);
    private float _accentAnimElapsedSec = AccentColorTransitionSec;

    // Indeterminate loading sweep & 220ms blend
    private float _loadingBlend;
    private float _loadingElapsedSec;

    // Pending seek hold window (prevents snap-back while audio service seeks)
    private double? _pendingSeekFraction;
    private long _pendingSeekSetTickMs;

    // Pointer & scrub state
    private bool _isPointerOver;
    private bool _isBodyPressed;
    private bool _isScrubbing;
    private bool _isPerimeterModeScrub;
    private bool _borderHoldEligible;
    private uint _activePointerId;
    private Point _pressStartPoint;
    private Point _lastPointerPoint;
    private long _pressStartTickMs;
    private double _scrubFraction;
    private float _lastPerimeterAnchorFraction;

    // Spring physics state (semi-implicit Euler sub-stepped to match Compose spring(dampingRatio, stiffness))
    private SpringState _scrubMorphSpring = new(0f, 0f, 0.40f, 420f);
    private SpringState _pressScaleSpring = new(1f, 1f, 0.72f, 260f);
    private SpringState _jellyScaleXSpring = new(1f, 1f, 0.36f, 380f);
    private SpringState _jellyScaleYSpring = new(1f, 1f, 0.36f, 380f);
    private SpringState _jellyOffsetXSpring = new(0f, 0f, 0.48f, 340f);

    // Two-stage squash/stretch kick sequence (65ms enter / 55ms exit FastOutSlowIn before bouncy spring settle)
    private JellyStage _activeJellyStage = JellyStage.None;
    private float _jellyStageElapsedSec;
    private float _jellyStageDurationSec;
    private float _jellyStageStartX = 1f;
    private float _jellyStageStartY = 1f;
    private float _jellyStagePeakX = 1f;
    private float _jellyStagePeakY = 1f;
    private float _jellyStageSettleX = 1f;
    private float _jellyStageSettleY = 1f;

    private Vector3 _lastAppliedVisualScale = Vector3.One;
    private Vector3 _lastAppliedVisualOffset = Vector3.Zero;

    /// <summary>
    /// Identifies the <see cref="InnerContent"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty InnerContentProperty =
        DependencyProperty.Register(
            nameof(InnerContent),
            typeof(object),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(null));

    /// <summary>
    /// Identifies the <see cref="Progress"/> dependency property (normalized playback progress in <c>[0.0, 1.0]</c>).
    /// </summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(double),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(0.0, OnProgressChanged));

    /// <summary>
    /// Identifies the <see cref="IsLoading"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(
            nameof(IsLoading),
            typeof(bool),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(false, OnIsLoadingChanged));

    /// <summary>
    /// Identifies the <see cref="CanSeek"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty CanSeekProperty =
        DependencyProperty.Register(
            nameof(CanSeek),
            typeof(bool),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(true));

    /// <summary>
    /// Identifies the <see cref="AccentColor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(Color.FromArgb(255, 45, 212, 191), OnAccentColorChanged));

    /// <summary>
    /// Identifies the <see cref="PillCornerRadius"/> dependency property.
    /// Set to <c>&lt;= 0</c> (default) to automatically match full-pill radius (<c>Height / 2</c>) or <see cref="Control.CornerRadius"/>.
    /// </summary>
    public static readonly DependencyProperty PillCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(PillCornerRadius),
            typeof(double),
            typeof(MiniPlayerProgressBorderControl),
            new PropertyMetadata(0.0, OnGeometryPropertyChanged));

    /// <summary>
    /// Raised when the user releases a border-hold or horizontal drag scrub gesture with the target normalized progress (<c>0.0..1.0</c>).
    /// </summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>
    /// Raised continuously while scrubbing with the live preview normalized progress (<c>0.0..1.0</c>).
    /// </summary>
    public event EventHandler<double>? ScrubProgressChanged;

    /// <summary>
    /// Raised when interactive scrubbing begins (<c>true</c>) or ends (<c>false</c>).
    /// </summary>
    public event EventHandler<bool>? ScrubbingChanged;

    /// <summary>
    /// Raised when the user performs a quick click/tap on the mini player body (outside child buttons),
    /// typically used to open the Now Playing sidebar.
    /// </summary>
    public event EventHandler? Clicked;

    /// <summary>
    /// Initializes a new instance of <see cref="MiniPlayerProgressBorderControl"/>.
    /// </summary>
    public MiniPlayerProgressBorderControl()
    {
        InitializeComponent();

        ElementCompositionPreview.SetIsTranslationEnabled(HostGrid, true);
        EnsureHandCursor();

        _canvas = PerimeterCanvas;
        if (_canvas is not null)
        {
            _canvas.Draw += OnPerimeterCanvasDraw;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;

        HostGrid.SizeChanged += OnHostSizeChanged;
        HostGrid.PointerEntered += OnHostPointerEntered;
        HostGrid.PointerExited += OnHostPointerExited;
        HostGrid.PointerPressed += OnHostPointerPressed;
        HostGrid.PointerMoved += OnHostPointerMoved;
        HostGrid.PointerReleased += OnHostPointerReleased;
        HostGrid.PointerCanceled += OnHostPointerCanceled;
        HostGrid.PointerCaptureLost += OnHostPointerCaptureLost;
    }

    private void EnsureHandCursor()
    {
        _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);
        ProtectedCursor = _handCursor;
    }

    /// <summary>
    /// Gets or sets the inner mini player element (e.g., <see cref="LiquidGlassPanel"/>).
    /// </summary>
    public object? InnerContent
    {
        get => GetValue(InnerContentProperty);
        set => SetValue(InnerContentProperty, value);
    }

    /// <summary>
    /// Gets or sets the normalized playback progress in <c>[0.0, 1.0]</c>.
    /// </summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// Gets or sets whether the current track is resolving or buffering, activating the orbiting loading sweep.
    /// </summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>
    /// Gets or sets whether interactive scrubbing is allowed (automatically suppressed while <see cref="IsLoading"/> is <c>true</c>).
    /// </summary>
    public bool CanSeek
    {
        get => (bool)GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    /// <summary>
    /// Gets or sets the dynamic album ambient accent color driving the perimeter border and glow.
    /// </summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    /// <summary>
    /// Gets or sets an explicit corner radius in DIPs; <c>&lt;= 0</c> falls back to <see cref="Control.CornerRadius"/> or full-pill (<c>Height / 2</c>).
    /// </summary>
    public double PillCornerRadius
    {
        get => (double)GetValue(PillCornerRadiusProperty);
        set => SetValue(PillCornerRadiusProperty, value);
    }

    /// <summary>
    /// Gets a value indicating whether the user is actively scrubbing the mini player progress.
    /// </summary>
    public bool IsScrubbing => _isScrubbing;

    /// <summary>
    /// Gets the currently displayed progress fraction (live scrub preview, pending seek, or bound <see cref="Progress"/>).
    /// </summary>
    public double DisplayProgress
    {
        get
        {
            if (_isScrubbing)
            {
                return Math.Clamp(_scrubFraction, 0.0, 1.0);
            }

            if (_pendingSeekFraction.HasValue)
            {
                return Math.Clamp(_pendingSeekFraction.Value, 0.0, 1.0);
            }

            return Math.Clamp(Progress, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Resets any active scrub or pending seek lock when the active track changes.
    /// </summary>
    public void ResetForTrackChange()
    {
        _pendingSeekFraction = null;
        if (_isScrubbing)
        {
            EndScrub(commitSeek: false, pointer: null);
        }

        InvalidateCanvasSafe();
    }

    /// <summary>
    /// Applies a programmatic gelatinous squash-and-stretch impulse to the mini player capsule.
    /// </summary>
    /// <param name="pressX">Normalized or local X coordinate of the press along the pill.</param>
    /// <param name="isPressed">True while pressed/dragging; false on release for elastic recoil.</param>
    /// <param name="scrubDelta">Optional horizontal drag delta in DIPs to inject velocity-coupled stretch.</param>
    public void ApplyJellyImpulse(double pressX, bool isPressed, double scrubDelta = 0.0)
    {
        if (!IsLoaded || Visibility != Visibility.Visible)
        {
            return;
        }

        if (isPressed)
        {
            if (Math.Abs(scrubDelta) > 0.01)
            {
                float stretch = Math.Clamp((float)(scrubDelta * 0.0022), -0.032f, 0.032f);
                _jellyScaleXSpring.Velocity += MathF.Abs(stretch) * 14f;
                _jellyScaleYSpring.Velocity -= MathF.Abs(stretch) * 12f;
                _jellyOffsetXSpring.Target = Math.Clamp((float)(scrubDelta * 0.14), -3.5f, 3.5f);
            }
            else
            {
                float width = Math.Max(1f, (float)HostGrid.ActualWidth);
                float normalizedX = pressX is >= 0.0 and <= 1.0
                    ? (float)pressX
                    : Math.Clamp((float)pressX / width, 0f, 1f);
                float bias = (normalizedX - 0.5f) * 2f;
                _pressScaleSpring.Target = 1.036f;
                _jellyScaleXSpring.Velocity += 0.22f;
                _jellyScaleYSpring.Velocity -= 0.26f;
                _jellyOffsetXSpring.Target = bias * 1.4f;
            }
        }
        else
        {
            _pressScaleSpring.Target = _isPointerOver ? 1.015f : 1.0f;
            _jellyOffsetXSpring.Target = 0f;
            StartJellyStage(
                JellyStage.ExitScrub,
                durationSec: 0.055f,
                peakX: 0.982f,
                peakY: 1.022f,
                settleX: 1.0f,
                settleY: 1.0f);
        }

        EnsureRenderLoopIfNeeded();
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MiniPlayerProgressBorderControl control)
        {
            return;
        }

        double newProgress = Math.Clamp((double)e.NewValue, 0.0, 1.0);
        if (control._pendingSeekFraction.HasValue &&
            Math.Abs(newProgress - control._pendingSeekFraction.Value) < PendingSeekCatchupTolerance)
        {
            control._pendingSeekFraction = null;
        }

        control.InvalidateCanvasSafe();
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MiniPlayerProgressBorderControl control)
        {
            return;
        }

        if ((bool)e.NewValue && control._isScrubbing)
        {
            control.EndScrub(commitSeek: false, pointer: null);
        }

        control.EnsureRenderLoopIfNeeded();
        control.InvalidateCanvasSafe();
    }

    private static void OnAccentColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MiniPlayerProgressBorderControl control)
        {
            return;
        }

        var nextColor = (Color)e.NewValue;
        if (!control.IsLoaded || control.Visibility != Visibility.Visible)
        {
            control._renderedAccentColor = nextColor;
            control._accentAnimStartColor = nextColor;
            control._accentAnimTargetColor = nextColor;
            control._accentAnimElapsedSec = AccentColorTransitionSec;
            control.InvalidateCanvasSafe();
            return;
        }

        control._accentAnimStartColor = control._renderedAccentColor;
        control._accentAnimTargetColor = nextColor;
        control._accentAnimElapsedSec = 0f;
        control.EnsureRenderLoopIfNeeded();
        control.InvalidateCanvasSafe();
    }

    private static void OnGeometryPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MiniPlayerProgressBorderControl control)
        {
            control.RebuildGeometryIfNeeded(force: true);
            control.InvalidateCanvasSafe();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureHandCursor();

        if (_canvas is null && HostGrid is not null)
        {
            var nextCanvas = new CanvasControl
            {
                Margin = new Thickness(-16),
                ClearColor = Colors.Transparent,
                IsHitTestVisible = false
            };
            nextCanvas.Draw += OnPerimeterCanvasDraw;
            HostGrid.Children.Add(nextCanvas);
            _canvas = nextCanvas;
            PerimeterCanvas = nextCanvas;
        }

        if (Application.Current is App app)
        {
            _themeService = app.Services.GetService<IThemeService>();
            if (_themeService is not null)
            {
                _themeService.ThemeChanged -= OnThemeServiceChanged;
                _themeService.ThemeChanged += OnThemeServiceChanged;
            }
        }

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        if (_cornerRadiusCallbackToken == 0)
        {
            _cornerRadiusCallbackToken = RegisterPropertyChangedCallback(CornerRadiusProperty, OnCornerRadiusPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        _renderedAccentColor = AccentColor;
        _accentAnimStartColor = AccentColor;
        _accentAnimTargetColor = AccentColor;
        _accentAnimElapsedSec = AccentColorTransitionSec;
        _loadingBlend = (IsLoading && !_isScrubbing) ? 1f : 0f;
        _lastRenderTickMs = Environment.TickCount64;

        RebuildGeometryIfNeeded(force: true);
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;

        if (_themeService is not null)
        {
            _themeService.ThemeChanged -= OnThemeServiceChanged;
            _themeService = null;
        }

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        if (_cornerRadiusCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(CornerRadiusProperty, _cornerRadiusCallbackToken);
            _cornerRadiusCallbackToken = 0;
        }

        CancelInteractionAndResetVisuals();
        UnhookRenderLoop();

        ProtectedCursor = null;
        _handCursor?.Dispose();
        _handCursor = null;

        _roundStrokeStyle?.Dispose();
        _roundStrokeStyle = null;

        if (_canvas is not null)
        {
            _canvas.Draw -= OnPerimeterCanvasDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }

        PerimeterCanvas = null!;
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        EnsureRenderLoopIfNeeded();
        if (IsAncestorChainVisible())
        {
            InvalidateCanvasSafe();
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Visibility != Visibility.Visible)
        {
            CancelInteractionAndResetVisuals();
            UnhookRenderLoop();
        }
        else
        {
            _lastRenderTickMs = Environment.TickCount64;
            RebuildGeometryIfNeeded(force: true);
            EnsureRenderLoopIfNeeded();
            InvalidateCanvasSafe();
        }
    }

    private void OnCornerRadiusPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        RebuildGeometryIfNeeded(force: true);
        InvalidateCanvasSafe();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        InvalidateCanvasSafe();
    }

    private void OnThemeServiceChanged(object? sender, ThemePaletteDefinition e)
    {
        DispatcherQueue.TryEnqueue(InvalidateCanvasSafe);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RebuildGeometryIfNeeded(force: false);
        ApplyHostVisualTransform(force: true);
        InvalidateCanvasSafe();
    }

    private void OnHostPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        if (!_isBodyPressed && !_isScrubbing)
        {
            _pressScaleSpring.Target = 1.015f;
            EnsureRenderLoopIfNeeded();
        }
    }

    private void OnHostPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        if (!_isBodyPressed && !_isScrubbing)
        {
            _pressScaleSpring.Target = 1.0f;
            EnsureRenderLoopIfNeeded();
        }
    }

    private void OnHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsFromChildButton(e.OriginalSource))
        {
            return;
        }

        var point = e.GetCurrentPoint(HostGrid);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        RebuildGeometryIfNeeded(force: false);

        _isBodyPressed = true;
        _activePointerId = e.Pointer.PointerId;
        _pressStartPoint = point.Position;
        _lastPointerPoint = point.Position;
        _pressStartTickMs = Environment.TickCount64;

        // Check whether press lands inside Android's 22px perimeter grab band for 190ms hold-to-scrub
        _borderHoldEligible = false;
        if (CanSeekNow && _geometry is not null)
        {
            var hit = _geometry.ClosestFraction(new Vector2((float)point.Position.X, (float)point.Position.Y));
            float bandPx = MathF.Min(BorderGrabBandPx, (float)HostGrid.ActualHeight * 0.36f);
            _borderHoldEligible = hit.DistancePx <= bandPx;
        }

        // Tactile iOS 26 / Android press grow (1.0 -> 1.04)
        _pressScaleSpring.Target = 1.04f;
        HostGrid.CapturePointer(e.Pointer);
        EnsureRenderLoopIfNeeded();
        e.Handled = true;
    }

    private void OnHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isBodyPressed || e.Pointer.PointerId != _activePointerId)
        {
            return;
        }

        var currentPos = e.GetCurrentPoint(HostGrid).Position;
        double dx = currentPos.X - _pressStartPoint.X;
        double dy = currentPos.Y - _pressStartPoint.Y;
        double stepDx = currentPos.X - _lastPointerPoint.X;
        double distFromStart = Math.Sqrt((dx * dx) + (dy * dy));
        _lastPointerPoint = currentPos;

        if (!_isScrubbing)
        {
            if (distFromStart > HoldCancelSlopPx)
            {
                _borderHoldEligible = false;
            }

            // Horizontal press-and-drag >= 6px engages smooth gelatinous scrubbing
            if (CanSeekNow && Math.Abs(dx) >= DragThresholdPx && Math.Abs(dx) >= Math.Abs(dy) * 0.75)
            {
                float anchorFraction = 0f;
                if (_geometry is not null)
                {
                    anchorFraction = _geometry.ClosestFraction(
                        new Vector2((float)currentPos.X, (float)currentPos.Y)).Fraction;
                }

                BeginScrub(anchorFraction, perimeterMode: false);
                ApplyHorizontalScrubDelta(dx);
                ScrubProgressChanged?.Invoke(this, _scrubFraction);
                InvalidateCanvasSafe();
            }

            e.Handled = true;
            return;
        }

        // Active scrub update
        if (_isPerimeterModeScrub && _geometry is not null)
        {
            var hit = _geometry.ClosestFraction(
                new Vector2((float)currentPos.X, (float)currentPos.Y),
                hintFraction: _lastPerimeterAnchorFraction);
            float delta = ShortestCircularDelta(_lastPerimeterAnchorFraction, hit.Fraction);
            _lastPerimeterAnchorFraction = hit.Fraction;
            _scrubFraction = Math.Clamp(_scrubFraction + delta, 0.0, 1.0);
        }
        else
        {
            ApplyHorizontalScrubDelta(stepDx);
        }

        // Subtle velocity-coupled jelly deformation while actively dragging
        float velStretch = Math.Clamp((float)(stepDx * 0.0020), -0.026f, 0.026f);
        if (MathF.Abs(velStretch) > 0.001f && _activeJellyStage == JellyStage.None)
        {
            _jellyScaleXSpring.Velocity += MathF.Abs(velStretch) * 10f;
            _jellyScaleYSpring.Velocity -= MathF.Abs(velStretch) * 8.5f;
        }

        _jellyOffsetXSpring.Target = Math.Clamp((float)((currentPos.X - _pressStartPoint.X) * 0.035), -3.2f, 3.2f);
        ScrubProgressChanged?.Invoke(this, _scrubFraction);
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
        e.Handled = true;
    }

    private void OnHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isBodyPressed || e.Pointer.PointerId != _activePointerId)
        {
            return;
        }

        _isBodyPressed = false;
        _borderHoldEligible = false;
        HostGrid.ReleasePointerCapture(e.Pointer);

        if (_isScrubbing)
        {
            EndScrub(commitSeek: true, pointer: null);
        }
        else
        {
            // Quick click -> open Now Playing sidebar with subtle tactile tap bounce
            _pressScaleSpring.Target = _isPointerOver ? 1.015f : 1.0f;
            _jellyOffsetXSpring.Target = 0f;
            _jellyScaleXSpring.Velocity += 0.24f;
            _jellyScaleYSpring.Velocity -= 0.24f;
            EnsureRenderLoopIfNeeded();

            var releasePos = e.GetCurrentPoint(HostGrid).Position;
            double dx = releasePos.X - _pressStartPoint.X;
            double dy = releasePos.Y - _pressStartPoint.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) < 14.0)
            {
                Clicked?.Invoke(this, EventArgs.Empty);
            }
        }

        e.Handled = true;
    }

    private void OnHostPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_isBodyPressed || _isScrubbing)
        {
            _isBodyPressed = false;
            _borderHoldEligible = false;
            HostGrid.ReleasePointerCapture(e.Pointer);
            if (_isScrubbing)
            {
                EndScrub(commitSeek: false, pointer: null);
            }
            else
            {
                _pressScaleSpring.Target = _isPointerOver ? 1.015f : 1.0f;
                _jellyOffsetXSpring.Target = 0f;
                EnsureRenderLoopIfNeeded();
            }
        }
    }

    private void OnHostPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isBodyPressed || _isScrubbing)
        {
            _isBodyPressed = false;
            _borderHoldEligible = false;
            if (_isScrubbing)
            {
                EndScrub(commitSeek: true, pointer: null);
            }
            else
            {
                _pressScaleSpring.Target = _isPointerOver ? 1.015f : 1.0f;
                _jellyOffsetXSpring.Target = 0f;
                EnsureRenderLoopIfNeeded();
            }
        }
    }

    private bool CanSeekNow => CanSeek && !IsLoading;

    private void BeginScrub(float initialAnchorFraction, bool perimeterMode)
    {
        if (_isScrubbing)
        {
            return;
        }

        _scrubFraction = DisplayProgress;
        _lastPerimeterAnchorFraction = initialAnchorFraction;
        _isPerimeterModeScrub = perimeterMode;
        _isScrubbing = true;
        _borderHoldEligible = false;

        // Android PillPerimeterProgress.kt:713-727 enter jelly springs
        _scrubMorphSpring.DampingRatio = 0.40f;
        _scrubMorphSpring.Stiffness = 420f;
        _scrubMorphSpring.Target = 1f;

        _pressScaleSpring.Target = 1f;
        StartJellyStage(
            JellyStage.EnterScrub,
            durationSec: 0.065f,
            peakX: 1.028f,
            peakY: 0.968f,
            settleX: 1.012f,
            settleY: 1.012f);

        ScrubbingChanged?.Invoke(this, true);
        ScrubProgressChanged?.Invoke(this, _scrubFraction);
        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private void EndScrub(bool commitSeek, Pointer? pointer)
    {
        if (!_isScrubbing)
        {
            return;
        }

        double target = Math.Clamp(_scrubFraction, 0.0, 1.0);
        _isScrubbing = false;
        _isPerimeterModeScrub = false;

        if (pointer is not null)
        {
            HostGrid.ReleasePointerCapture(pointer);
        }

        // Android PillPerimeterProgress.kt:729-743 release recoil jelly springs
        _scrubMorphSpring.DampingRatio = 0.44f;
        _scrubMorphSpring.Stiffness = 390f;
        _scrubMorphSpring.Target = 0f;

        _pressScaleSpring.Target = _isPointerOver ? 1.015f : 1.0f;
        _jellyOffsetXSpring.Target = 0f;
        StartJellyStage(
            JellyStage.ExitScrub,
            durationSec: 0.055f,
            peakX: 0.978f,
            peakY: 1.028f,
            settleX: 1.0f,
            settleY: 1.0f);

        ScrubbingChanged?.Invoke(this, false);

        if (commitSeek)
        {
            _pendingSeekFraction = target;
            _pendingSeekSetTickMs = Environment.TickCount64;
            SeekRequested?.Invoke(this, target);
        }

        EnsureRenderLoopIfNeeded();
        InvalidateCanvasSafe();
    }

    private void ApplyHorizontalScrubDelta(double deltaX)
    {
        double scrubWidth = Math.Max(150.0, HostGrid.ActualWidth);
        _scrubFraction = Math.Clamp(_scrubFraction + (deltaX / scrubWidth), 0.0, 1.0);
    }

    private void StartJellyStage(
        JellyStage stage,
        float durationSec,
        float peakX,
        float peakY,
        float settleX,
        float settleY)
    {
        _activeJellyStage = stage;
        _jellyStageElapsedSec = 0f;
        _jellyStageDurationSec = durationSec;
        _jellyStageStartX = _jellyScaleXSpring.Value;
        _jellyStageStartY = _jellyScaleYSpring.Value;
        _jellyStagePeakX = peakX;
        _jellyStagePeakY = peakY;
        _jellyStageSettleX = settleX;
        _jellyStageSettleY = settleY;
        _jellyScaleXSpring.Velocity = 0f;
        _jellyScaleYSpring.Velocity = 0f;
        _jellyScaleXSpring.DampingRatio = 0.36f;
        _jellyScaleXSpring.Stiffness = 380f;
        _jellyScaleYSpring.DampingRatio = 0.36f;
        _jellyScaleYSpring.Stiffness = 380f;
    }

    private void CancelInteractionAndResetVisuals()
    {
        _isPointerOver = false;
        _isBodyPressed = false;
        _borderHoldEligible = false;
        if (_isScrubbing)
        {
            _isScrubbing = false;
            _isPerimeterModeScrub = false;
            ScrubbingChanged?.Invoke(this, false);
        }

        _activeJellyStage = JellyStage.None;
        _scrubMorphSpring.SnapTo(0f);
        _pressScaleSpring.SnapTo(1f);
        _jellyScaleXSpring.SnapTo(1f);
        _jellyScaleYSpring.SnapTo(1f);
        _jellyOffsetXSpring.SnapTo(0f);
        ApplyHostVisualTransform(force: true);
    }

    private bool IsFromChildButton(object? originalSource)
    {
        DependencyObject? current = originalSource as DependencyObject;
        while (current is not null && !ReferenceEquals(current, HostGrid) && !ReferenceEquals(current, this))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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

    private void EnsureRenderLoopIfNeeded()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || !IsAncestorChainVisible())
        {
            UnhookRenderLoop();
            return;
        }

        if (NeedsAnimationFrame() && !_isRenderingHooked)
        {
            _lastRenderTickMs = Environment.TickCount64;
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

    private bool NeedsAnimationFrame()
    {
        bool showLoading = IsLoading && !_isScrubbing;
        float targetLoadingBlend = showLoading ? 1f : 0f;

        return showLoading
            || MathF.Abs(_loadingBlend - targetLoadingBlend) > 0.002f
            || _accentAnimElapsedSec < AccentColorTransitionSec
            || _borderHoldEligible
            || _pendingSeekFraction.HasValue
            || _activeJellyStage != JellyStage.None
            || !_scrubMorphSpring.IsSettled
            || !_pressScaleSpring.IsSettled
            || !_jellyScaleXSpring.IsSettled
            || !_jellyScaleYSpring.IsSettled
            || !_jellyOffsetXSpring.IsSettled;
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        if (!IsLoaded || Visibility != Visibility.Visible || !IsAncestorChainVisible())
        {
            UnhookRenderLoop();
            return;
        }

        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastRenderTickMs) / 1000f, 0.001f, 0.05f);
        _lastRenderTickMs = now;

        bool canvasNeedsRedraw = false;

        // 1. Check 190ms border hold-to-scrub activation
        if (_isBodyPressed && _borderHoldEligible && !_isScrubbing && CanSeekNow)
        {
            if (now - _pressStartTickMs >= BorderHoldTimeoutMs)
            {
                float anchorFraction = 0f;
                if (_geometry is not null)
                {
                    anchorFraction = _geometry.ClosestFraction(
                        new Vector2((float)_lastPointerPoint.X, (float)_lastPointerPoint.Y)).Fraction;
                }

                BeginScrub(anchorFraction, perimeterMode: true);
                canvasNeedsRedraw = true;
            }
        }

        // 2. Pending seek safety timeout (450ms matching Android PillPerimeterProgress.kt:670)
        if (_pendingSeekFraction.HasValue && (now - _pendingSeekSetTickMs) >= PendingSeekTimeoutMs)
        {
            _pendingSeekFraction = null;
            canvasNeedsRedraw = true;
        }

        // 3. Smooth 220ms crossfade & loading sweep clock
        bool showLoading = IsLoading && !_isScrubbing;
        float targetLoadingBlend = showLoading ? 1f : 0f;
        if (MathF.Abs(_loadingBlend - targetLoadingBlend) > 0.001f)
        {
            float step = dt / LoadingCrossfadeDurationSec;
            _loadingBlend = showLoading
                ? MathF.Min(1f, _loadingBlend + step)
                : MathF.Max(0f, _loadingBlend - step);
            canvasNeedsRedraw = true;
        }
        else
        {
            _loadingBlend = targetLoadingBlend;
        }

        if (_loadingBlend > 0.005f)
        {
            _loadingElapsedSec += dt;
            canvasNeedsRedraw = true;
        }
        else if (!showLoading && _loadingElapsedSec != 0f)
        {
            _loadingElapsedSec = 0f;
        }

        // 4. Smooth 420ms ambient accent color transition
        if (_accentAnimElapsedSec < AccentColorTransitionSec)
        {
            _accentAnimElapsedSec = MathF.Min(AccentColorTransitionSec, _accentAnimElapsedSec + dt);
            float t = EaseInOutCubic(_accentAnimElapsedSec / AccentColorTransitionSec);
            _renderedAccentColor = LerpColor(_accentAnimStartColor, _accentAnimTargetColor, t);
            canvasNeedsRedraw = true;
        }

        // 5. Scrub morph spring (drives stroke width, guide track & scrub thumb)
        float prevMorph = _scrubMorphSpring.Value;
        _scrubMorphSpring.Step(dt);
        if (MathF.Abs(_scrubMorphSpring.Value - prevMorph) > 0.0005f)
        {
            canvasNeedsRedraw = true;
        }

        // 6. Press & two-stage jelly squash/stretch springs
        _pressScaleSpring.Step(dt);
        _jellyOffsetXSpring.Step(dt);

        if (_activeJellyStage != JellyStage.None)
        {
            _jellyStageElapsedSec += dt;
            float progress = Math.Clamp(_jellyStageElapsedSec / Math.Max(0.001f, _jellyStageDurationSec), 0f, 1f);
            float eased = FastOutSlowIn(progress);
            _jellyScaleXSpring.Value = _jellyStageStartX + ((_jellyStagePeakX - _jellyStageStartX) * eased);
            _jellyScaleYSpring.Value = _jellyStageStartY + ((_jellyStagePeakY - _jellyStageStartY) * eased);

            if (progress >= 1f)
            {
                _activeJellyStage = JellyStage.None;
                _jellyScaleXSpring.Target = _jellyStageSettleX;
                _jellyScaleYSpring.Target = _jellyStageSettleY;
                // Preserve subtle exit momentum into the bouncy settle spring
                _jellyScaleXSpring.Velocity = (_jellyStagePeakX - _jellyStageStartX) * 4.5f;
                _jellyScaleYSpring.Velocity = (_jellyStagePeakY - _jellyStageStartY) * 4.5f;
            }
        }
        else
        {
            _jellyScaleXSpring.Step(dt);
            _jellyScaleYSpring.Step(dt);
        }

        ApplyHostVisualTransform(force: false);

        if (canvasNeedsRedraw)
        {
            InvalidateCanvasSafe();
        }

        if (!NeedsAnimationFrame())
        {
            UnhookRenderLoop();
        }
    }

    private void ApplyHostVisualTransform(bool force)
    {
        float w = (float)HostGrid.ActualWidth;
        float h = (float)HostGrid.ActualHeight;
        if (w < 2f || h < 2f)
        {
            return;
        }

        float effectivePress = _isScrubbing ? 1f : _pressScaleSpring.Value;
        float sx = Math.Clamp(effectivePress * _jellyScaleXSpring.Value, 0.90f, 1.12f);
        float sy = Math.Clamp(effectivePress * _jellyScaleYSpring.Value, 0.90f, 1.12f);
        float tx = Math.Clamp(_jellyOffsetXSpring.Value, -5f, 5f);

        var nextScale = new Vector3(sx, sy, 1f);
        var nextOffset = new Vector3(tx, 0f, 0f);

        if (!force &&
            Vector3.DistanceSquared(nextScale, _lastAppliedVisualScale) < 1e-7f &&
            Vector3.DistanceSquared(nextOffset, _lastAppliedVisualOffset) < 1e-5f)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(HostGrid);
        visual.CenterPoint = new Vector3(w * 0.5f, h * 0.5f, 0f);
        visual.Scale = nextScale;
        visual.Properties.InsertVector3("Translation", nextOffset);

        _lastAppliedVisualScale = nextScale;
        _lastAppliedVisualOffset = nextOffset;
    }

    private void RebuildGeometryIfNeeded(bool force)
    {
        float w = (float)HostGrid.ActualWidth;
        float h = (float)HostGrid.ActualHeight;
        if (w <= 4f || h <= 4f)
        {
            _geometry = null;
            return;
        }

        float resolvedRadius = ResolveCornerRadius(w, h);
        if (!force &&
            _geometry is not null &&
            MathF.Abs(w - _lastGeomWidth) < 0.25f &&
            MathF.Abs(h - _lastGeomHeight) < 0.25f &&
            MathF.Abs(resolvedRadius - _lastGeomRadius) < 0.25f)
        {
            return;
        }

        _lastGeomWidth = w;
        _lastGeomHeight = h;
        _lastGeomRadius = resolvedRadius;
        _geometry = new PillPerimeterGeometry(w, h, resolvedRadius);
    }

    private float ResolveCornerRadius(float width, float height)
    {
        float maxRadius = MathF.Min(width, height) * 0.5f;
        if (PillCornerRadius > 0.0)
        {
            return Math.Clamp((float)PillCornerRadius, 1f, maxRadius);
        }

        if (CornerRadius.TopLeft > 0.0)
        {
            return Math.Clamp((float)CornerRadius.TopLeft, 1f, maxRadius);
        }

        return maxRadius;
    }

    private bool ResolveIsLightTheme()
    {
        if (ActualTheme == ElementTheme.Light)
        {
            return true;
        }

        if (ActualTheme == ElementTheme.Dark)
        {
            return false;
        }

        return _themeService is not null && !_themeService.CurrentTheme.IsDark;
    }

    private void InvalidateCanvasSafe()
    {
        if (_canvas is not null &&
            Visibility == Visibility.Visible &&
            _canvas.ActualWidth > 4 &&
            _canvas.ActualHeight > 4 &&
            IsAncestorChainVisible())
        {
            _canvas.Invalidate();
        }
    }

    private void OnPerimeterCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float canvasW = (float)sender.Size.Width;
        float canvasH = (float)sender.Size.Height;
        float pillW = (float)HostGrid.ActualWidth;
        float pillH = (float)HostGrid.ActualHeight;
        if (canvasW <= 8f || canvasH <= 8f || pillW <= 4f || pillH <= 4f)
        {
            return;
        }

        RebuildGeometryIfNeeded(force: false);
        var geometry = _geometry;
        if (geometry is null)
        {
            return;
        }

        float offsetX = (canvasW - pillW) * 0.5f;
        float offsetY = (canvasH - pillH) * 0.5f;

        bool isLightTheme = ResolveIsLightTheme();
        Color glowColor = AdjustAmbientColorForPerimeter(_renderedAccentColor, isLightTheme);

        float clampedLoading = Math.Clamp(EaseInOutCubic(_loadingBlend), 0f, 1f);
        float playbackVisibility = Math.Clamp(1f - clampedLoading, 0f, 1f);
        float positiveMorph = MathF.Max(0f, _scrubMorphSpring.Value);
        float clampedMorph = Math.Clamp(_scrubMorphSpring.Value, 0f, 1f);

        float strokeWidth = MathF.Max(
            IdleStrokeWidth * 0.85f,
            IdleStrokeWidth + ((ScrubTargetStrokeWidth - IdleStrokeWidth) * positiveMorph));

        var ds = args.DrawingSession;
        var strokeStyle = _roundStrokeStyle ??= new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };

        // 1. Subtle full-pill guide track while scrubbing
        if (clampedMorph > 0.01f && playbackVisibility > 0.01f)
        {
            float guideAlpha = (isLightTheme ? 0.24f : 0.18f) * clampedMorph * playbackVisibility;
            ds.DrawRoundedRectangle(
                offsetX,
                offsetY,
                pillW,
                pillH,
                geometry.Radius,
                geometry.Radius,
                WithAlpha(glowColor, guideAlpha),
                strokeWidth * 0.72f);
        }

        // 2. Clockwise playback progress arc starting at top-center
        float displayFraction = (float)DisplayProgress;
        if (playbackVisibility > 0.01f && displayFraction > 0.001f)
        {
            if (displayFraction >= 0.9998f)
            {
                DrawGlowingClosedPill(
                    ds,
                    offsetX,
                    offsetY,
                    pillW,
                    pillH,
                    geometry.Radius,
                    glowColor,
                    strokeWidth,
                    playbackVisibility,
                    clampedMorph,
                    isLightTheme);
            }
            else
            {
                using var progressGeometry = geometry.CreateSegmentGeometry(
                    ds,
                    offsetX,
                    offsetY,
                    startFraction: 0f,
                    sweepFraction: displayFraction);
                if (progressGeometry is not null)
                {
                    DrawGlowingPerimeterStroke(
                        ds,
                        progressGeometry,
                        glowColor,
                        strokeWidth,
                        playbackVisibility,
                        clampedMorph,
                        isLightTheme,
                        strokeStyle);
                }
            }
        }

        // 3. Progressive clockwise indeterminate loading sweep while buffering/resolving
        if (clampedLoading > 0.01f)
        {
            var (startFrac, sweepFrac) = ComputeLoadingArc(_loadingElapsedSec);
            float loadingStroke = LoadingBaseStrokeWidth
                + ((ScrubTargetStrokeWidth - LoadingBaseStrokeWidth) * clampedMorph * 0.4f);

            using var loadingGeometry = geometry.CreateSegmentGeometry(
                ds,
                offsetX,
                offsetY,
                startFraction: startFrac,
                sweepFraction: sweepFrac);
            if (loadingGeometry is not null)
            {
                DrawGlowingPerimeterStroke(
                    ds,
                    loadingGeometry,
                    glowColor,
                    loadingStroke,
                    clampedLoading,
                    emphasis: 0.55f,
                    isLightTheme,
                    strokeStyle);
            }
        }

        // 4. Glowing circular scrub thumb at leading progress edge during scrub mode
        float thumbScale = positiveMorph * playbackVisibility;
        if (thumbScale > 0.02f)
        {
            Vector2 localCenter = geometry.PositionAt(displayFraction);
            var thumbCenter = new Vector2(offsetX + localCenter.X, offsetY + localCenter.Y);
            float clampedScale = MathF.Min(1f, thumbScale);

            float haloRadius = MathF.Max(1f, 15f * thumbScale);
            using (var haloBrush = new CanvasRadialGradientBrush(
                sender,
                new CanvasGradientStop[]
                {
                    new CanvasGradientStop(0.0f, WithAlpha(glowColor, 0.58f * clampedScale)),
                    new CanvasGradientStop(0.5f, WithAlpha(glowColor, 0.18f * clampedScale)),
                    new CanvasGradientStop(1.0f, Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B))
                })
            {
                Center = thumbCenter,
                RadiusX = haloRadius,
                RadiusY = haloRadius
            })
            {
                ds.FillCircle(thumbCenter.X, thumbCenter.Y, haloRadius, haloBrush);
            }

            ds.FillCircle(
                thumbCenter.X,
                thumbCenter.Y,
                MathF.Max(0.5f, 7.4f * thumbScale),
                Color.FromArgb((byte)Math.Clamp(MathF.Round(255f * 0.24f * clampedScale), 0f, 255f), 0, 0, 0));

            ds.FillCircle(
                thumbCenter.X,
                thumbCenter.Y,
                MathF.Max(0.5f, 6.3f * thumbScale),
                WithAlpha(glowColor, clampedScale));

            ds.FillCircle(
                thumbCenter.X,
                thumbCenter.Y,
                MathF.Max(0.3f, 2.6f * thumbScale),
                Color.FromArgb((byte)Math.Clamp(MathF.Round(255f * clampedScale), 0f, 255f), 255, 255, 255));
        }
    }

    private static void DrawGlowingPerimeterStroke(
        CanvasDrawingSession ds,
        CanvasGeometry geometry,
        Color color,
        float coreWidth,
        float alphaMultiplier,
        float emphasis,
        bool isLightTheme,
        CanvasStrokeStyle strokeStyle)
    {
        float outerGlowAlpha = (isLightTheme ? 0.16f : 0.20f) + (0.12f * emphasis);
        float innerGlowAlpha = (isLightTheme ? 0.30f : 0.36f) + (0.14f * emphasis);

        ds.DrawGeometry(
            geometry,
            WithAlpha(color, outerGlowAlpha * alphaMultiplier),
            coreWidth + 7.0f,
            strokeStyle);

        ds.DrawGeometry(
            geometry,
            WithAlpha(color, innerGlowAlpha * alphaMultiplier),
            coreWidth + 2.8f,
            strokeStyle);

        Color luminousCore = isLightTheme
            ? color
            : LerpColor(color, Colors.White, 0.16f);

        ds.DrawGeometry(
            geometry,
            WithAlpha(luminousCore, 0.96f * alphaMultiplier),
            coreWidth,
            strokeStyle);
    }

    private static void DrawGlowingClosedPill(
        CanvasDrawingSession ds,
        float x,
        float y,
        float width,
        float height,
        float radius,
        Color color,
        float coreWidth,
        float alphaMultiplier,
        float emphasis,
        bool isLightTheme)
    {
        float outerGlowAlpha = (isLightTheme ? 0.16f : 0.20f) + (0.12f * emphasis);
        float innerGlowAlpha = (isLightTheme ? 0.30f : 0.36f) + (0.14f * emphasis);

        ds.DrawRoundedRectangle(x, y, width, height, radius, radius, WithAlpha(color, outerGlowAlpha * alphaMultiplier), coreWidth + 7.0f);
        ds.DrawRoundedRectangle(x, y, width, height, radius, radius, WithAlpha(color, innerGlowAlpha * alphaMultiplier), coreWidth + 2.8f);

        Color luminousCore = isLightTheme
            ? color
            : LerpColor(color, Colors.White, 0.16f);
        ds.DrawRoundedRectangle(x, y, width, height, radius, radius, WithAlpha(luminousCore, 0.96f * alphaMultiplier), coreWidth);
    }

    private static (float StartFraction, float SweepFraction) ComputeLoadingArc(float elapsedSeconds)
    {
        float safeTime = MathF.Max(0f, elapsedSeconds);
        float totalCycles = safeTime / LoadingCycleSeconds;
        float cycleIndex = MathF.Floor(totalCycles);
        float cyclePhase = totalCycles - cycleIndex;

        float baseStart = (safeTime * LoadingBaseLapsPerSec) + (cycleIndex * LoadingCycleGain);
        float startUnwrapped;
        float sweep;

        if (cyclePhase < 0.5f)
        {
            float grow = EaseInOutCubic(cyclePhase * 2f);
            startUnwrapped = baseStart;
            sweep = LoadingMinSweep + (grow * LoadingMaxSweepDelta);
        }
        else
        {
            float shrink = EaseInOutCubic((cyclePhase - 0.5f) * 2f);
            startUnwrapped = baseStart + (shrink * LoadingCycleGain);
            sweep = LoadingMinSweep + ((1f - shrink) * LoadingMaxSweepDelta);
        }

        return (WrapFraction(startUnwrapped), Math.Clamp(sweep, 0.02f, 0.96f));
    }

    private static float WrapFraction(float fraction)
    {
        float wrapped = ((fraction % 1f) + 1f) % 1f;
        return wrapped >= 1f ? 0f : wrapped;
    }

    private static float ShortestCircularDelta(float fromFraction, float toFraction)
    {
        float delta = WrapFraction(toFraction) - WrapFraction(fromFraction);
        if (delta > 0.5f)
        {
            delta -= 1f;
        }

        if (delta < -0.5f)
        {
            delta += 1f;
        }

        return delta;
    }

    private static float EaseInOutCubic(float t)
    {
        float x = Math.Clamp(t, 0f, 1f);
        if (x < 0.5f)
        {
            return 4f * x * x * x;
        }

        float inv = (-2f * x) + 2f;
        return 1f - ((inv * inv * inv) * 0.5f);
    }

    private static float FastOutSlowIn(float t)
    {
        float x = Math.Clamp(t, 0f, 1f);
        // Smooth cubic approximation matching Android's FastOutSlowInEasing
        return x * x * (3f - (2f * x));
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        byte a = (byte)Math.Clamp(MathF.Round(255f * Math.Clamp(alpha, 0f, 1f)), 0f, 255f);
        return Color.FromArgb(a, color.R, color.G, color.B);
    }

    private static Color LerpColor(Color from, Color to, float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (byte)Math.Clamp(MathF.Round(from.A + ((to.A - from.A) * clamped)), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(from.R + ((to.R - from.R) * clamped)), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(from.G + ((to.G - from.G) * clamped)), 0f, 255f),
            (byte)Math.Clamp(MathF.Round(from.B + ((to.B - from.B) * clamped)), 0f, 255f));
    }

    private static Color AdjustAmbientColorForPerimeter(Color color, bool isLightTheme)
    {
        RgbToHsv(color.R, color.G, color.B, out float h, out float s, out float v);
        bool isAchromatic = s < 0.08f;
        if (isLightTheme)
        {
            if (!isAchromatic)
            {
                s = Math.Clamp(s, 0.55f, 1.0f);
            }

            v = Math.Clamp(v, 0.42f, 0.74f);
        }
        else
        {
            if (!isAchromatic)
            {
                s = Math.Clamp(s, 0.45f, 1.0f);
            }

            v = Math.Clamp(v, 0.82f, 0.98f);
        }

        return HsvToColor(color.A, h, s, v);
    }

    private static void RgbToHsv(byte rByte, byte gByte, byte bByte, out float h, out float s, out float v)
    {
        float r = rByte / 255f;
        float g = gByte / 255f;
        float b = bByte / 255f;

        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        v = max;
        s = max <= 1e-5f ? 0f : delta / max;

        if (delta <= 1e-5f)
        {
            h = 0f;
            return;
        }

        if (max == r)
        {
            h = 60f * (((g - b) / delta) % 6f);
        }
        else if (max == g)
        {
            h = 60f * (((b - r) / delta) + 2f);
        }
        else
        {
            h = 60f * (((r - g) / delta) + 4f);
        }

        if (h < 0f)
        {
            h += 360f;
        }
    }

    private static Color HsvToColor(byte alpha, float h, float s, float v)
    {
        float c = v * s;
        float x = c * (1f - MathF.Abs(((h / 60f) % 2f) - 1f));
        float m = v - c;

        float r;
        float g;
        float b;
        if (h < 60f)
        {
            (r, g, b) = (c, x, 0f);
        }
        else if (h < 120f)
        {
            (r, g, b) = (x, c, 0f);
        }
        else if (h < 180f)
        {
            (r, g, b) = (0f, c, x);
        }
        else if (h < 240f)
        {
            (r, g, b) = (0f, x, c);
        }
        else if (h < 300f)
        {
            (r, g, b) = (x, 0f, c);
        }
        else
        {
            (r, g, b) = (c, 0f, x);
        }

        return Color.FromArgb(
            alpha,
            (byte)Math.Clamp(MathF.Round((r + m) * 255f), 0f, 255f),
            (byte)Math.Clamp(MathF.Round((g + m) * 255f), 0f, 255f),
            (byte)Math.Clamp(MathF.Round((b + m) * 255f), 0f, 255f));
    }

    private enum JellyStage
    {
        None,
        EnterScrub,
        ExitScrub
    }

    private struct SpringState
    {
        public float Value;
        public float Velocity;
        public float Target;
        public float DampingRatio;
        public float Stiffness;

        public SpringState(float initialValue, float target, float dampingRatio, float stiffness)
        {
            Value = initialValue;
            Velocity = 0f;
            Target = target;
            DampingRatio = dampingRatio;
            Stiffness = stiffness;
        }

        public readonly bool IsSettled =>
            MathF.Abs(Value - Target) < 0.0006f && MathF.Abs(Velocity) < 0.004f;

        public void SnapTo(float value)
        {
            Value = value;
            Target = value;
            Velocity = 0f;
        }

        public void Step(float dt)
        {
            if (IsSettled)
            {
                Value = Target;
                Velocity = 0f;
                return;
            }

            const float maxSubStep = 1f / 240f;
            int steps = Math.Max(1, (int)MathF.Ceiling(dt / maxSubStep));
            float h = dt / steps;
            float damping = 2f * DampingRatio * MathF.Sqrt(Stiffness);

            for (int i = 0; i < steps; i++)
            {
                float accel = (-Stiffness * (Value - Target)) - (damping * Velocity);
                Velocity += accel * h;
                Value += Velocity * h;
            }

            if (IsSettled)
            {
                Value = Target;
                Velocity = 0f;
            }
        }
    }

    private readonly struct PerimeterHit
    {
        public PerimeterHit(float fraction, float distancePx)
        {
            Fraction = fraction;
            DistancePx = distancePx;
        }

        public float Fraction { get; }
        public float DistancePx { get; }
    }

    /// <summary>
    /// Analytical clockwise rounded-pill perimeter contour starting at top-center <c>(width / 2, 0)</c>.
    /// Supports exact sub-arc cubic Bézier path generation and O(1) contour evaluation with zero faceting.
    /// </summary>
    private sealed class PillPerimeterGeometry
    {
        private readonly float _width;
        private readonly float _height;
        private readonly float _radius;
        private readonly float _cx;
        private readonly float _hHalf;
        private readonly float _hFull;
        private readonly float _vFull;
        private readonly float _arcLen;

        private readonly float _s1;
        private readonly float _s2;
        private readonly float _s3;
        private readonly float _s4;
        private readonly float _s5;
        private readonly float _s6;
        private readonly float _s7;
        private readonly float _s8;

        private readonly float[] _sampleX = new float[PerimeterSampleCount];
        private readonly float[] _sampleY = new float[PerimeterSampleCount];

        public PillPerimeterGeometry(float width, float height, float cornerRadius)
        {
            _width = width;
            _height = height;
            _radius = Math.Clamp(cornerRadius, 1f, MathF.Min(width, height) * 0.5f);
            _cx = width * 0.5f;
            _hHalf = MathF.Max(0f, (width * 0.5f) - _radius);
            _hFull = _hHalf * 2f;
            _vFull = MathF.Max(0f, height - (2f * _radius));
            _arcLen = 0.5f * MathF.PI * _radius;

            _s1 = _hHalf;
            _s2 = _s1 + _arcLen;
            _s3 = _s2 + _vFull;
            _s4 = _s3 + _arcLen;
            _s5 = _s4 + _hFull;
            _s6 = _s5 + _arcLen;
            _s7 = _s6 + _vFull;
            _s8 = _s7 + _arcLen;
            TotalLength = _s8 + _hHalf;

            for (int i = 0; i < PerimeterSampleCount; i++)
            {
                Vector2 pt = PositionAt((float)i / PerimeterSampleCount);
                _sampleX[i] = pt.X;
                _sampleY[i] = pt.Y;
            }
        }

        public float Radius => _radius;
        public float TotalLength { get; }

        public Vector2 PositionAt(float fraction)
        {
            float dist = WrapFraction(fraction) * TotalLength;
            return PositionAtDistance(dist);
        }

        public Vector2 PositionAtDistance(float dist)
        {
            float s = ((dist % TotalLength) + TotalLength) % TotalLength;
            float r = _radius;
            float w = _width;
            float h = _height;

            if (s <= _s1)
            {
                return new Vector2(_cx + s, 0f);
            }

            if (s <= _s2)
            {
                float a = (-MathF.PI * 0.5f) + ((s - _s1) / r);
                return new Vector2((w - r) + (r * MathF.Cos(a)), r + (r * MathF.Sin(a)));
            }

            if (s <= _s3)
            {
                return new Vector2(w, r + (s - _s2));
            }

            if (s <= _s4)
            {
                float a = (s - _s3) / r;
                return new Vector2((w - r) + (r * MathF.Cos(a)), (h - r) + (r * MathF.Sin(a)));
            }

            if (s <= _s5)
            {
                return new Vector2((w - r) - (s - _s4), h);
            }

            if (s <= _s6)
            {
                float a = (MathF.PI * 0.5f) + ((s - _s5) / r);
                return new Vector2(r + (r * MathF.Cos(a)), (h - r) + (r * MathF.Sin(a)));
            }

            if (s <= _s7)
            {
                return new Vector2(0f, (h - r) - (s - _s6));
            }

            if (s <= _s8)
            {
                float a = MathF.PI + ((s - _s7) / r);
                return new Vector2(r + (r * MathF.Cos(a)), r + (r * MathF.Sin(a)));
            }

            return new Vector2(r + (s - _s8), 0f);
        }

        public CanvasGeometry? CreateSegmentGeometry(
            ICanvasResourceCreator creator,
            float offsetX,
            float offsetY,
            float startFraction,
            float sweepFraction)
        {
            float clampedSweep = Math.Clamp(sweepFraction, 0f, 1f);
            if (clampedSweep <= 0.0002f || TotalLength <= 1f)
            {
                return null;
            }

            float startDist = WrapFraction(startFraction) * TotalLength;
            float sweepDist = clampedSweep * TotalLength;
            float endDist = startDist + sweepDist;

            using var builder = new CanvasPathBuilder(creator);
            Vector2 startPt = PositionAtDistance(startDist);
            builder.BeginFigure(offsetX + startPt.X, offsetY + startPt.Y);

            if (endDist <= TotalLength)
            {
                AppendDistanceRange(builder, offsetX, offsetY, startDist, endDist);
            }
            else
            {
                AppendDistanceRange(builder, offsetX, offsetY, startDist, TotalLength);
                AppendDistanceRange(builder, offsetX, offsetY, 0f, MathF.Min(TotalLength, endDist - TotalLength));
            }

            builder.EndFigure(CanvasFigureLoop.Open);
            return CanvasGeometry.CreatePath(builder);
        }

        public PerimeterHit ClosestFraction(Vector2 point, float? hintFraction = null, float maxHalfWindowFraction = 0.35f)
        {
            int bestIdx = 0;
            float bestDistSq = float.MaxValue;

            if (!hintFraction.HasValue)
            {
                for (int i = 0; i < PerimeterSampleCount; i++)
                {
                    float dx = point.X - _sampleX[i];
                    float dy = point.Y - _sampleY[i];
                    float dSq = (dx * dx) + (dy * dy);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = i;
                    }
                }
            }
            else
            {
                int centerIdx = ((int)MathF.Round(WrapFraction(hintFraction.Value) * PerimeterSampleCount)) % PerimeterSampleCount;
                int halfWindow = (int)MathF.Round(Math.Clamp(maxHalfWindowFraction, 0.05f, 0.5f) * PerimeterSampleCount);
                for (int offset = -halfWindow; offset <= halfWindow; offset++)
                {
                    int idx = (((centerIdx + offset) % PerimeterSampleCount) + PerimeterSampleCount) % PerimeterSampleCount;
                    float dx = point.X - _sampleX[idx];
                    float dy = point.Y - _sampleY[idx];
                    float dSq = (dx * dx) + (dy * dy);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = idx;
                    }
                }
            }

            float stepFrac = 1f / PerimeterSampleCount;
            float lo = (bestIdx * stepFrac) - stepFrac;
            float hi = (bestIdx * stepFrac) + stepFrac;

            for (int iter = 0; iter < ClosestRefineSteps; iter++)
            {
                float m1 = lo + ((hi - lo) / 3f);
                float m2 = hi - ((hi - lo) / 3f);
                Vector2 p1 = PositionAt(m1);
                Vector2 p2 = PositionAt(m2);
                float d1 = Vector2.DistanceSquared(point, p1);
                float d2 = Vector2.DistanceSquared(point, p2);
                if (d1 < d2)
                {
                    hi = m2;
                }
                else
                {
                    lo = m1;
                }
            }

            float refinedFraction = WrapFraction((lo + hi) * 0.5f);
            Vector2 refinedPoint = PositionAt(refinedFraction);
            return new PerimeterHit(refinedFraction, Vector2.Distance(point, refinedPoint));
        }

        private void AppendDistanceRange(CanvasPathBuilder builder, float ox, float oy, float fromDist, float toDist)
        {
            if (toDist - fromDist <= 1e-4f)
            {
                return;
            }

            float r = _radius;
            float w = _width;
            float h = _height;

            // 1. Top-right horizontal line [0, _s1]
            if (TryOverlap(fromDist, toDist, 0f, _s1, out _, out float e1))
            {
                builder.AddLine(ox + _cx + e1, oy);
            }

            // 2. Top-right quarter arc [_s1, _s2]
            if (TryOverlap(fromDist, toDist, _s1, _s2, out float b2, out float e2))
            {
                float a0 = (-MathF.PI * 0.5f) + ((b2 - _s1) / r);
                float a1 = (-MathF.PI * 0.5f) + ((e2 - _s1) / r);
                AddCircularArcBezier(builder, ox + (w - r), oy + r, r, a0, a1);
            }

            // 3. Right vertical line [_s2, _s3]
            if (TryOverlap(fromDist, toDist, _s2, _s3, out _, out float e3))
            {
                builder.AddLine(ox + w, oy + r + (e3 - _s2));
            }

            // 4. Bottom-right quarter arc [_s3, _s4]
            if (TryOverlap(fromDist, toDist, _s3, _s4, out float b4, out float e4))
            {
                float a0 = (b4 - _s3) / r;
                float a1 = (e4 - _s3) / r;
                AddCircularArcBezier(builder, ox + (w - r), oy + (h - r), r, a0, a1);
            }

            // 5. Bottom horizontal line [_s4, _s5]
            if (TryOverlap(fromDist, toDist, _s4, _s5, out _, out float e5))
            {
                builder.AddLine(ox + (w - r) - (e5 - _s4), oy + h);
            }

            // 6. Bottom-left quarter arc [_s5, _s6]
            if (TryOverlap(fromDist, toDist, _s5, _s6, out float b6, out float e6))
            {
                float a0 = (MathF.PI * 0.5f) + ((b6 - _s5) / r);
                float a1 = (MathF.PI * 0.5f) + ((e6 - _s5) / r);
                AddCircularArcBezier(builder, ox + r, oy + (h - r), r, a0, a1);
            }

            // 7. Left vertical line [_s6, _s7]
            if (TryOverlap(fromDist, toDist, _s6, _s7, out _, out float e7))
            {
                builder.AddLine(ox, oy + (h - r) - (e7 - _s6));
            }

            // 8. Top-left quarter arc [_s7, _s8]
            if (TryOverlap(fromDist, toDist, _s7, _s8, out float b8, out float e8))
            {
                float a0 = MathF.PI + ((b8 - _s7) / r);
                float a1 = MathF.PI + ((e8 - _s7) / r);
                AddCircularArcBezier(builder, ox + r, oy + r, r, a0, a1);
            }

            // 9. Top-left horizontal line [_s8, TotalLength]
            if (TryOverlap(fromDist, toDist, _s8, TotalLength, out _, out float e9))
            {
                builder.AddLine(ox + r + (e9 - _s8), oy);
            }
        }

        private static bool TryOverlap(float a0, float a1, float b0, float b1, out float start, out float end)
        {
            start = MathF.Max(a0, b0);
            end = MathF.Min(a1, b1);
            return (end - start) > 1e-4f;
        }

        private static void AddCircularArcBezier(
            CanvasPathBuilder builder,
            float centerX,
            float centerY,
            float radius,
            float startAngle,
            float endAngle)
        {
            float sweep = endAngle - startAngle;
            if (sweep <= 1e-5f)
            {
                return;
            }

            float k = (4f / 3f) * MathF.Tan(sweep * 0.25f) * radius;
            float cos0 = MathF.Cos(startAngle);
            float sin0 = MathF.Sin(startAngle);
            float cos1 = MathF.Cos(endAngle);
            float sin1 = MathF.Sin(endAngle);

            var p0 = new Vector2(centerX + (radius * cos0), centerY + (radius * sin0));
            var p3 = new Vector2(centerX + (radius * cos1), centerY + (radius * sin1));
            var c1 = new Vector2(p0.X - (k * sin0), p0.Y + (k * cos0));
            var c2 = new Vector2(p3.X + (k * sin1), p3.Y - (k * cos1));

            builder.AddCubicBezier(c1, c2, p3);
        }
    }
}
