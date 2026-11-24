using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// GPU-accelerated (Win2D) shimmer skeleton placeholder tailored for synchronized lyrics panels
/// (both Now Playing sidebar and Fullscreen). Renders stanza-grouped rounded lyric-line bars
/// with a synchronized diagonal shimmer sweep, subtle breathing pulse, and automatic render-loop
/// suspension whenever collapsed, inactive, or unloaded.
/// </summary>
public sealed partial class LyricsSkeletonControl : UserControl
{
    private const float ShimmerPeriodSeconds = 1.45f;
    private const float PulsePeriodSeconds = 2.60f;
    private const float ShimmerBandWidthRatio = 0.62f;

    // Natural musical lyric-line width ratios (grouped into visual stanzas)
    private static readonly float[] LyricWidthRatios =
    [
        0.58f, 0.80f, 0.46f, 0.72f,
        0.88f, 0.64f, 0.50f, 0.78f,
        0.42f, 0.74f, 0.60f, 0.52f
    ];

    private CanvasControl? _canvas;
    private long _visibilityCallbackToken;
    private bool _isRenderingHooked;
    private long _lastRenderTickMs;
    private float _shimmerElapsedSec;
    private float _pulseElapsedSec;

    /// <summary>
    /// Identifies the <see cref="IsActive"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(LyricsSkeletonControl),
            new PropertyMetadata(true, OnVisualOrStateChanged));

    /// <summary>
    /// Identifies the <see cref="IsLightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsLightModeProperty =
        DependencyProperty.Register(
            nameof(IsLightMode),
            typeof(bool),
            typeof(LyricsSkeletonControl),
            new PropertyMetadata(false, OnVisualOrStateChanged));

    /// <summary>
    /// Identifies the <see cref="IsFullscreen"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsFullscreenProperty =
        DependencyProperty.Register(
            nameof(IsFullscreen),
            typeof(bool),
            typeof(LyricsSkeletonControl),
            new PropertyMetadata(false, OnVisualOrStateChanged));

    /// <summary>
    /// Identifies the <see cref="AccentColor"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(LyricsSkeletonControl),
            new PropertyMetadata(Color.FromArgb(255, 45, 212, 191), OnVisualOrStateChanged));

    /// <summary>
    /// Initializes a new instance of <see cref="LyricsSkeletonControl"/>.
    /// </summary>
    public LyricsSkeletonControl()
    {
        InitializeComponent();
        _canvas = ShimmerCanvas;
        if (_canvas is not null)
        {
            _canvas.Draw += OnShimmerCanvasDraw;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnControlSizeChanged;
    }

    /// <summary>
    /// Gets or sets whether the shimmer and pulse animation loop is active while visible.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>
    /// Gets or sets whether light-mode shimmer tones should be rendered.
    /// </summary>
    public bool IsLightMode
    {
        get => (bool)GetValue(IsLightModeProperty);
        set => SetValue(IsLightModeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether Fullscreen lyric typography proportions and vertical edge fading are enabled.
    /// </summary>
    public bool IsFullscreen
    {
        get => (bool)GetValue(IsFullscreenProperty);
        set => SetValue(IsFullscreenProperty, value);
    }

    /// <summary>
    /// Gets or sets the dynamic album cover accent color used to subtly tint the shimmer highlight.
    /// </summary>
    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    private static void OnVisualOrStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsSkeletonControl control)
        {
            control.UpdateRenderLoopState();
            control.InvalidateCanvasSafe();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_canvas is null && RootGrid is not null)
        {
            var nextCanvas = new CanvasControl
            {
                ClearColor = Colors.Transparent,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            nextCanvas.Draw += OnShimmerCanvasDraw;
            RootGrid.Children.Add(nextCanvas);
            _canvas = nextCanvas;
            ShimmerCanvas = nextCanvas;
        }

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        UpdateRenderLoopState();
        InvalidateCanvasSafe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        UnhookRenderLoop();

        if (_canvas is not null)
        {
            _canvas.Draw -= OnShimmerCanvasDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }

        ShimmerCanvas = null!;
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        UpdateRenderLoopState();
        if (IsAncestorChainVisible())
        {
            InvalidateCanvasSafe();
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateRenderLoopState();
        if (Visibility == Visibility.Visible)
        {
            InvalidateCanvasSafe();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        InvalidateCanvasSafe();
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRenderLoopState();
        InvalidateCanvasSafe();
    }

    private void UpdateRenderLoopState()
    {
        bool shouldAnimate = IsLoaded && Visibility == Visibility.Visible && IsActive && IsAncestorChainVisible();
        if (shouldAnimate)
        {
            if (!_isRenderingHooked)
            {
                _lastRenderTickMs = Environment.TickCount64;
                CompositionTarget.Rendering += OnCompositionRendering;
                _isRenderingHooked = true;
            }
        }
        else
        {
            UnhookRenderLoop();
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
        if (!IsLoaded || Visibility != Visibility.Visible || !IsActive || !IsAncestorChainVisible())
        {
            UnhookRenderLoop();
            return;
        }

        long now = Environment.TickCount64;
        float dt = Math.Clamp((now - _lastRenderTickMs) / 1000f, 0.001f, 0.05f);
        _lastRenderTickMs = now;

        _shimmerElapsedSec = (_shimmerElapsedSec + dt) % ShimmerPeriodSeconds;
        _pulseElapsedSec = (_pulseElapsedSec + dt) % PulsePeriodSeconds;
        InvalidateCanvasSafe();
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

    private void OnShimmerCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= 16f || height <= 16f)
        {
            return;
        }

        var rootDs = args.DrawingSession;

        if (IsFullscreen)
        {
            using var contentList = new CanvasCommandList(rootDs);
            using (var contentDs = contentList.CreateDrawingSession())
            {
                DrawLyricSkeletonLines(sender, contentDs, width, height);
            }

            using var maskList = new CanvasCommandList(rootDs);
            using (var maskDs = maskList.CreateDrawingSession())
            {
                var maskStops = new[]
                {
                    new CanvasGradientStop(0.00f, Color.FromArgb(0, 255, 255, 255)),
                    new CanvasGradientStop(0.14f, Colors.White),
                    new CanvasGradientStop(0.84f, Colors.White),
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
            DrawLyricSkeletonLines(sender, rootDs, width, height);
        }
    }

    private void DrawLyricSkeletonLines(
        ICanvasResourceCreator creator,
        CanvasDrawingSession ds,
        float width,
        float height)
    {
        bool isLight = IsLightMode;
        bool isFullscreen = IsFullscreen;

        float padX = isFullscreen ? 20f : 16f;
        float contentW = MathF.Max(60f, width - (padX * 2f));
        float baseLineH = isFullscreen ? 23f : 15f;
        float lineSpacing = isFullscreen ? 22f : 16f;
        float stanzaExtraGap = isFullscreen ? 18f : 14f;
        float startY = isFullscreen ? MathF.Max(36f, height * 0.11f) : 18f;

        Color accent = AccentColor;
        Color baseTint = isLight
            ? Color.FromArgb(22, 15, 23, 42)
            : Color.FromArgb(26, 255, 255, 255);

        // Blend subtle album cover accent into the shimmer highlight peak
        Color highlightRgb = isLight
            ? BlendColor(Color.FromArgb(255, 15, 23, 42), accent, 0.24f)
            : BlendColor(Colors.White, accent, 0.22f);

        Color highlightTint = isLight
            ? Color.FromArgb(52, highlightRgb.R, highlightRgb.G, highlightRgb.B)
            : Color.FromArgb(72, highlightRgb.R, highlightRgb.G, highlightRgb.B);

        float shimmerProgress = _shimmerElapsedSec / ShimmerPeriodSeconds;
        float pulsePhase = (_pulseElapsedSec / PulsePeriodSeconds) * MathF.Tau;
        float band = MathF.Max(contentW * ShimmerBandWidthRatio, 80f);
        float sweepStartX = padX - band + (shimmerProgress * (contentW + (band * 2f)));
        float focalY = height * 0.38f;

        float y = startY;
        for (int i = 0; i < LyricWidthRatios.Length && y < height - 12f; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                y += stanzaExtraGap;
                if (y >= height - 12f)
                {
                    break;
                }
            }

            float lineCenterY = y + (baseLineH * 0.5f);
            float distNorm = MathF.Min(1f, MathF.Abs(lineCenterY - focalY) / MathF.Max(120f, height * 0.58f));
            bool isFocal = i == 3 || i == 4;

            // Focal lines slightly taller & brighter, mirroring SynchronizedLyricsCanvasControl depth hierarchy
            float lineH = isFocal ? baseLineH * 1.18f : baseLineH;
            float radius = lineH * 0.5f;
            float depthAlpha = isFullscreen
                ? (1.0f - (distNorm * 0.58f))
                : (1.0f - (distNorm * 0.44f));

            // Gentle staggered harmonic wave across lyric lines
            float wave = 0.86f + (0.14f * MathF.Sin(pulsePhase - (i * 0.45f)));
            float finalOpacity = Math.Clamp(depthAlpha * wave, 0.22f, 1.0f);

            float lineW = MathF.Max(48f, contentW * LyricWidthRatios[i]);
            float diagOffset = i * (isFullscreen ? 14f : 10f);
            float sx = sweepStartX - diagOffset;
            float ex = sx + band;

            Color cBase = ScaleAlpha(baseTint, finalOpacity);
            Color cHighlight = ScaleAlpha(highlightTint, finalOpacity * (isFocal ? 1.18f : 1.0f));

            using var shimmerBrush = new CanvasLinearGradientBrush(
                creator,
                new CanvasGradientStop[]
                {
                    new CanvasGradientStop(0.0f, cBase),
                    new CanvasGradientStop(0.5f, cHighlight),
                    new CanvasGradientStop(1.0f, cBase)
                })
            {
                StartPoint = new Vector2(sx, y),
                EndPoint = new Vector2(ex, y + lineH * 0.4f)
            };

            ds.FillRoundedRectangle(padX, y, lineW, lineH, radius, radius, shimmerBrush);

            // Secondary subtle trailing phrase pill on select lines for authentic multi-phrase lyric rhythm
            if (i == 2 || i == 6 || i == 9)
            {
                float gap = isFullscreen ? 12f : 9f;
                float remaining = contentW - lineW - gap;
                if (remaining > 38f)
                {
                    float tailW = MathF.Min(remaining * 0.52f, isFullscreen ? 110f : 68f);
                    ds.FillRoundedRectangle(padX + lineW + gap, y, tailW, lineH, radius, radius, shimmerBrush);
                }
            }

            y += lineH + lineSpacing;
        }
    }

    private static Color ScaleAlpha(Color color, float factor)
    {
        byte a = (byte)Math.Clamp((int)MathF.Round(color.A * factor), 0, 255);
        return Color.FromArgb(a, color.R, color.G, color.B);
    }

    private static Color BlendColor(Color a, Color b, float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            255,
            (byte)Math.Clamp((int)MathF.Round(a.R + ((b.R - a.R) * clamped)), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a.G + ((b.G - a.G) * clamped)), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a.B + ((b.B - a.B) * clamped)), 0, 255));
    }
}
