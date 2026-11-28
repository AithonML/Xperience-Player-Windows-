using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Layout presets for <see cref="SearchSkeletonControl"/> matching Android's Search screen shimmer loaders.
/// </summary>
public enum SearchSkeletonMode
{
    /// <summary>
    /// Two sections of 3x2 rounded category cards (66px tall, 16px corner radius) for the Moods &amp; Genres explorer.
    /// </summary>
    MoodsAndGenres,

    /// <summary>
    /// Curated Playlists/Albums card row (132x132, 18px radius) followed by 5 track row skeletons (48x48 art) for the Mood/Genre right detail pane.
    /// </summary>
    MoodDetail,

    /// <summary>
    /// Full search results skeleton: Top Result card, 4 Songs rows, circular Artists row (120x120), and square Albums row (132x132).
    /// </summary>
    SearchResults,

    /// <summary>
    /// Structured Home discovery skeleton: Top Picks tall editorial cards, Recently Played bento mosaic, and square Albums shelf.
    /// </summary>
    HomeFeed
}

/// <summary>
/// GPU-accelerated (Win2D) synchronized shimmer skeleton loader for the dedicated Search page.
/// Mirrors Android's <c>SkeletonPlaceholders.kt</c> and <c>SearchScreen.kt</c>:
/// <list type="bullet">
/// <item>1350ms linear horizontal shimmer sweep with band width <c>0.55 * width</c>.</item>
/// <item>Theme-aware Dark (White 8% / 20%) and Light (Black 7% / 16%) shimmer brushes.</item>
/// <item>Automatically pauses its render loop whenever <see cref="UIElement.Visibility"/> is <see cref="Visibility.Collapsed"/>, <see cref="IsActive"/> is <c>false</c>, or unloaded.</item>
/// </list>
/// </summary>
public sealed partial class SearchSkeletonControl : UserControl
{
    private const float ShimmerPeriodSeconds = 1.35f;
    private const float ShimmerBandWidthRatio = 0.55f;

    // Android SkeletonPlaceholders.kt:101-110 exact alpha values
    private static readonly Color DarkBaseColor = Color.FromArgb(20, 255, 255, 255);      // White 8%
    private static readonly Color DarkHighlightColor = Color.FromArgb(51, 255, 255, 255); // White 20%
    private static readonly Color DarkCardSurfaceColor = Color.FromArgb(14, 255, 255, 255);
    private static readonly Color DarkCardBorderColor = Color.FromArgb(22, 255, 255, 255);

    private static readonly Color LightBaseColor = Color.FromArgb(18, 0, 0, 0);           // Black 7%
    private static readonly Color LightHighlightColor = Color.FromArgb(41, 0, 0, 0);      // Black 16%
    private static readonly Color LightCardSurfaceColor = Color.FromArgb(12, 0, 0, 0);
    private static readonly Color LightCardBorderColor = Color.FromArgb(18, 0, 0, 0);

    private static readonly float[] TrackTitleWidthRatios = [0.70f, 0.56f, 0.74f, 0.62f, 0.68f];
    private static readonly float[] TrackSubtitleWidthRatios = [0.45f, 0.38f, 0.48f, 0.41f, 0.44f];

    private IThemeService? _themeService;
    private CanvasControl? _canvas;
    private long _visibilityCallbackToken;
    private long _paddingCallbackToken;

    private bool _isRenderingHooked;
    private long _lastRenderTickMs;
    private float _shimmerElapsedSec;

    /// <summary>
    /// Identifies the <see cref="Mode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(
            nameof(Mode),
            typeof(SearchSkeletonMode),
            typeof(SearchSkeletonControl),
            new PropertyMetadata(SearchSkeletonMode.SearchResults, OnLayoutOrStatePropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsActive"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(SearchSkeletonControl),
            new PropertyMetadata(true, OnIsActivePropertyChanged));

    /// <summary>
    /// Initializes a new instance of <see cref="SearchSkeletonControl"/>.
    /// </summary>
    public SearchSkeletonControl()
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

        UpdateIntrinsicHeight();
    }

    /// <summary>
    /// Gets or sets the skeleton layout preset (<see cref="SearchSkeletonMode.MoodsAndGenres"/>,
    /// <see cref="SearchSkeletonMode.MoodDetail"/>, or <see cref="SearchSkeletonMode.SearchResults"/>).
    /// </summary>
    public SearchSkeletonMode Mode
    {
        get => (SearchSkeletonMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>
    /// Gets or sets whether shimmer animation is enabled while visible.
    /// Setting this to <c>false</c> or setting <see cref="UIElement.Visibility"/> to <see cref="Visibility.Collapsed"/> pauses animation immediately.
    /// </summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnLayoutOrStatePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchSkeletonControl control)
        {
            control.UpdateIntrinsicHeight();
            control.InvalidateCanvasSafe();
        }
    }

    private static void OnIsActivePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchSkeletonControl control)
        {
            control.UpdateRenderLoopState();
            control.InvalidateCanvasSafe();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _themeService = app.Services.GetService<IThemeService>();
            if (_themeService is not null)
            {
                _themeService.ThemeChanged -= OnThemeServiceChanged;
                _themeService.ThemeChanged += OnThemeServiceChanged;
            }
        }

        if (_canvas is null && RootGrid is not null)
        {
            var nextCanvas = new CanvasControl
            {
                ClearColor = Colors.Transparent,
                IsHitTestVisible = false
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

        if (_paddingCallbackToken == 0)
        {
            _paddingCallbackToken = RegisterPropertyChangedCallback(PaddingProperty, OnPaddingPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;

        UpdateIntrinsicHeight();
        UpdateRenderLoopState();
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

        if (_paddingCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(PaddingProperty, _paddingCallbackToken);
            _paddingCallbackToken = 0;
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

    private void OnPaddingPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateIntrinsicHeight();
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

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRenderLoopState();
        InvalidateCanvasSafe();
    }

    private void UpdateIntrinsicHeight()
    {
        if (RootGrid is null)
        {
            return;
        }

        double contentHeight = Mode switch
        {
            SearchSkeletonMode.MoodsAndGenres => 536.0,
            SearchSkeletonMode.MoodDetail => 568.0,
            SearchSkeletonMode.SearchResults => 828.0,
            SearchSkeletonMode.HomeFeed => 920.0,
            _ => 560.0
        };

        double verticalPad = Math.Max(0.0, Padding.Top) + Math.Max(0.0, Padding.Bottom);
        RootGrid.Height = contentHeight + verticalPad;
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

    private void OnShimmerCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        float totalW = (float)sender.Size.Width;
        float totalH = (float)sender.Size.Height;
        if (totalW <= 16f || totalH <= 16f)
        {
            return;
        }

        float padLeft = MathF.Max(0f, (float)Padding.Left);
        float padTop = MathF.Max(0f, (float)Padding.Top);
        float padRight = MathF.Max(0f, (float)Padding.Right);
        float contentW = MathF.Max(64f, totalW - padLeft - padRight);

        bool isLight = ResolveIsLightTheme();
        Color baseColor = isLight ? LightBaseColor : DarkBaseColor;
        Color highlightColor = isLight ? LightHighlightColor : DarkHighlightColor;
        Color cardSurface = isLight ? LightCardSurfaceColor : DarkCardSurfaceColor;
        Color cardBorder = isLight ? LightCardBorderColor : DarkCardBorderColor;

        // Android SkeletonPlaceholders.kt:118-126 synchronized shimmer band
        float progress = _shimmerElapsedSec / ShimmerPeriodSeconds;
        float band = MathF.Max(contentW * ShimmerBandWidthRatio, 1f);
        float startX = padLeft - band + (progress * (contentW + (band * 2f)));
        float endX = startX + band;

        var ds = args.DrawingSession;
        using var shimmerBrush = new CanvasLinearGradientBrush(
            sender,
            new CanvasGradientStop[]
            {
                new CanvasGradientStop(0.0f, baseColor),
                new CanvasGradientStop(0.5f, highlightColor),
                new CanvasGradientStop(1.0f, baseColor)
            })
        {
            StartPoint = new Vector2(startX, padTop),
            EndPoint = new Vector2(endX, padTop)
        };

        switch (Mode)
        {
            case SearchSkeletonMode.MoodsAndGenres:
                DrawMoodsAndGenresSkeleton(ds, shimmerBrush, padLeft, padTop, contentW);
                break;

            case SearchSkeletonMode.MoodDetail:
                DrawMoodDetailSkeleton(ds, shimmerBrush, padLeft, padTop, contentW);
                break;

            case SearchSkeletonMode.HomeFeed:
                DrawHomeFeedSkeleton(ds, shimmerBrush, cardSurface, cardBorder, padLeft, padTop, contentW);
                break;

            case SearchSkeletonMode.SearchResults:
            default:
                DrawSearchResultsSkeleton(ds, shimmerBrush, cardSurface, cardBorder, padLeft, padTop, contentW);
                break;
        }
    }

    private static void DrawHomeFeedSkeleton(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        Color cardSurface,
        Color cardBorder,
        float x,
        float y,
        float width)
    {
        // Section 1: "Top picks for you" tall editorial cards (204x274, 24px radius)
        ds.FillRoundedRectangle(x, y, 180f, 20f, 8f, 8f, shimmerBrush);
        y += 34f;

        const float topPickW = 204f;
        const float topPickH = 264f;
        const float gap = 14f;
        int topPickCount = Math.Max(2, (int)((width + gap) / (topPickW + gap)));
        for (int i = 0; i < Math.Min(topPickCount, 6); i++)
        {
            float cx = x + (i * (topPickW + gap));
            if (cx + 40f > x + width)
            {
                break;
            }

            ds.FillRoundedRectangle(cx, y, topPickW, topPickH, 24f, 24f, cardSurface);
            ds.DrawRoundedRectangle(cx, y, topPickW, topPickH, 24f, 24f, cardBorder, 1f);
            ds.FillRoundedRectangle(cx + 14f, y + topPickH - 58f, 84f, 10f, 5f, 5f, shimmerBrush);
            ds.FillRoundedRectangle(cx + 14f, y + topPickH - 40f, 148f, 15f, 6f, 6f, shimmerBrush);
            ds.FillRoundedRectangle(cx + 14f, y + topPickH - 20f, 110f, 10f, 5f, 5f, shimmerBrush);
        }

        y += topPickH + 30f;

        // Section 2: "Recently Played" bento mosaic cards
        ds.FillRoundedRectangle(x, y, 160f, 20f, 8f, 8f, shimmerBrush);
        y += 34f;

        const float bentoColW = 196f;
        const float bentoH = 240f;
        int bentoCols = Math.Max(2, (int)((width + 12f) / (bentoColW + 12f)));
        for (int i = 0; i < Math.Min(bentoCols, 5); i++)
        {
            float cx = x + (i * (bentoColW + 12f));
            if (cx + 40f > x + width)
            {
                break;
            }

            if (i % 2 == 0)
            {
                ds.FillRoundedRectangle(cx, y, bentoColW, bentoH, 22f, 22f, shimmerBrush);
            }
            else
            {
                float halfH = (bentoH - 12f) * 0.5f;
                ds.FillRoundedRectangle(cx, y, bentoColW, halfH, 18f, 18f, shimmerBrush);
                ds.FillRoundedRectangle(cx, y + halfH + 12f, bentoColW, halfH, 18f, 18f, shimmerBrush);
            }
        }

        y += bentoH + 30f;

        // Section 3: Album recommendations shelf (168x168 square cards + title/artist lines)
        ds.FillRoundedRectangle(x, y, 210f, 20f, 8f, 8f, shimmerBrush);
        y += 34f;

        const float albSize = 164f;
        int albCount = Math.Max(2, (int)((width + gap) / (albSize + gap)));
        for (int i = 0; i < Math.Min(albCount, 6); i++)
        {
            float cx = x + (i * (albSize + gap));
            if (cx + 40f > x + width)
            {
                break;
            }

            ds.FillRoundedRectangle(cx, y, albSize, albSize, 18f, 18f, shimmerBrush);
            ds.FillRoundedRectangle(cx, y + albSize + 10f, albSize * 0.78f, 13f, 6f, 6f, shimmerBrush);
            ds.FillRoundedRectangle(cx, y + albSize + 28f, albSize * 0.52f, 11f, 5f, 5f, shimmerBrush);
        }
    }

    private static void DrawMoodsAndGenresSkeleton(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        float x,
        float startY,
        float width)
    {
        const float colSpacing = 10f;
        const float rowSpacing = 10f;
        const float cardHeight = 66f;
        const float cardRadius = 16f;

        float y = startY;
        float colWidth = MathF.Max(24f, (width - colSpacing) * 0.5f);

        for (int sectionIdx = 0; sectionIdx < 2; sectionIdx++)
        {
            if (sectionIdx > 0)
            {
                y += 22f;
            }

            float headerRatio = sectionIdx == 0 ? 0.44f : 0.32f;
            float headerW = MathF.Min(width * headerRatio, 240f);
            ds.FillRoundedRectangle(x, y, headerW, 20f, 6f, 6f, shimmerBrush);
            y += 20f + 14f;

            for (int row = 0; row < 3; row++)
            {
                ds.FillRoundedRectangle(x, y, colWidth, cardHeight, cardRadius, cardRadius, shimmerBrush);
                ds.FillRoundedRectangle(x + colWidth + colSpacing, y, colWidth, cardHeight, cardRadius, cardRadius, shimmerBrush);
                y += cardHeight + (row < 2 ? rowSpacing : 0f);
            }
        }
    }

    private static void DrawMoodDetailSkeleton(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        float x,
        float startY,
        float width)
    {
        float y = startY;

        // Section 1: Playlists / Featured Albums horizontal card row (132x132, 18px radius)
        float header1W = MathF.Min(width * 0.34f, 240f);
        ds.FillRoundedRectangle(x, y, header1W, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 12f;

        DrawSquareCardRow(ds, shimmerBrush, x, y, width, cardSize: 132f, radius: 18f, spacing: 12f, maxCards: 5);
        y += 168f + 24f;

        // Section 2: Curated Songs rows (5x 48x48 thumb + 70% / 45% lines)
        float header2W = MathF.Min(width * 0.28f, 200f);
        ds.FillRoundedRectangle(x, y, header2W, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 10f;

        DrawTrackRows(ds, shimmerBrush, x, y, width, count: 5);
    }

    private static void DrawSearchResultsSkeleton(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        Color cardSurface,
        Color cardBorder,
        float x,
        float startY,
        float width)
    {
        float y = startY;

        // 1. Top Result hero card skeleton
        float topHeaderW = MathF.Min(width * 0.20f, 168f);
        ds.FillRoundedRectangle(x, y, topHeaderW, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 10f;

        float topCardW = MathF.Min(width, 380f);
        const float topCardH = 84f;
        ds.FillRoundedRectangle(x, y, topCardW, topCardH, 18f, 18f, cardSurface);
        ds.DrawRoundedRectangle(x + 0.5f, y + 0.5f, topCardW - 1f, topCardH - 1f, 18f, 18f, cardBorder, 1f);

        float topArtX = x + 12f;
        float topArtY = y + 12f;
        ds.FillRoundedRectangle(topArtX, topArtY, 60f, 60f, 12f, 12f, shimmerBrush);

        float topTextX = topArtX + 60f + 14f;
        float topTextAvailW = MathF.Max(40f, (x + topCardW - 16f) - topTextX);
        ds.FillRoundedRectangle(topTextX, y + 24f, topTextAvailW * 0.68f, 15f, 4f, 4f, shimmerBrush);
        ds.FillRoundedRectangle(topTextX, y + 47f, topTextAvailW * 0.44f, 11f, 4f, 4f, shimmerBrush);
        y += topCardH + 22f;

        // 2. Songs section (header 24% + 4 track rows)
        float songsHeaderW = MathF.Min(width * 0.24f, 190f);
        ds.FillRoundedRectangle(x, y, songsHeaderW, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 10f;

        DrawTrackRows(ds, shimmerBrush, x, y, width, count: 4);
        y += (4 * 62f) + 22f;

        // 3. Artists section (header 26% + 120x120 circular artist cards)
        float artistsHeaderW = MathF.Min(width * 0.26f, 200f);
        ds.FillRoundedRectangle(x, y, artistsHeaderW, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 12f;

        DrawArtistCircleRow(ds, shimmerBrush, x, y, width, avatarSize: 120f, spacing: 16f, maxCards: 5);
        y += 141f + 22f;

        // 4. Albums section (header 22% + 132x132 rounded album cards)
        float albumsHeaderW = MathF.Min(width * 0.22f, 185f);
        ds.FillRoundedRectangle(x, y, albumsHeaderW, 18f, 5f, 5f, shimmerBrush);
        y += 18f + 12f;

        DrawSquareCardRow(ds, shimmerBrush, x, y, width, cardSize: 132f, radius: 18f, spacing: 12f, maxCards: 5);
    }

    private static void DrawTrackRows(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        float x,
        float startY,
        float width,
        int count)
    {
        const float thumbSize = 48f;
        const float thumbRadius = 10f;
        const float textGap = 12f;
        const float rowStride = 62f;

        float textX = x + thumbSize + textGap;
        float textAvailW = MathF.Max(48f, MathF.Min(width - thumbSize - textGap, 560f));

        for (int i = 0; i < count; i++)
        {
            float rowY = startY + (i * rowStride) + 7f;
            ds.FillRoundedRectangle(x, rowY, thumbSize, thumbSize, thumbRadius, thumbRadius, shimmerBrush);

            float titleRatio = TrackTitleWidthRatios[i % TrackTitleWidthRatios.Length];
            float subtitleRatio = TrackSubtitleWidthRatios[i % TrackSubtitleWidthRatios.Length];

            ds.FillRoundedRectangle(textX, rowY + 9f, textAvailW * titleRatio, 14f, 4f, 4f, shimmerBrush);
            ds.FillRoundedRectangle(textX, rowY + 29f, textAvailW * subtitleRatio, 11f, 4f, 4f, shimmerBrush);
        }
    }

    private static void DrawSquareCardRow(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        float x,
        float y,
        float width,
        float cardSize,
        float radius,
        float spacing,
        int maxCards)
    {
        int fittingCount = Math.Clamp((int)MathF.Floor((width + spacing) / (cardSize + spacing)), 1, maxCards);
        for (int i = 0; i < fittingCount; i++)
        {
            float cardX = x + (i * (cardSize + spacing));
            if (cardX + cardSize > x + width + 1f)
            {
                break;
            }

            ds.FillRoundedRectangle(cardX, y, cardSize, cardSize, radius, radius, shimmerBrush);
            ds.FillRoundedRectangle(cardX, y + cardSize + 8f, cardSize * 0.85f, 13f, 4f, 4f, shimmerBrush);
            ds.FillRoundedRectangle(cardX, y + cardSize + 25f, cardSize * 0.55f, 11f, 4f, 4f, shimmerBrush);
        }
    }

    private static void DrawArtistCircleRow(
        CanvasDrawingSession ds,
        ICanvasBrush shimmerBrush,
        float x,
        float y,
        float width,
        float avatarSize,
        float spacing,
        int maxCards)
    {
        int fittingCount = Math.Clamp((int)MathF.Floor((width + spacing) / (avatarSize + spacing)), 1, maxCards);
        float radius = avatarSize * 0.5f;
        float labelW = avatarSize * 0.72f;
        float labelInset = (avatarSize - labelW) * 0.5f;

        for (int i = 0; i < fittingCount; i++)
        {
            float cardX = x + (i * (avatarSize + spacing));
            if (cardX + avatarSize > x + width + 1f)
            {
                break;
            }

            ds.FillCircle(cardX + radius, y + radius, radius, shimmerBrush);
            ds.FillRoundedRectangle(cardX + labelInset, y + avatarSize + 8f, labelW, 13f, 4f, 4f, shimmerBrush);
        }
    }
}
