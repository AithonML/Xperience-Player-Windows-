using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Interactive Win2D bar chart displaying network consumption over Day, Week, or Month intervals
/// with hover tooltips showing Audio, Artwork, Lyrics, and Canvas breakdowns.
/// </summary>
public sealed class DataUsageChartControl : Grid
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(IReadOnlyList<DataUsageChartPoint>),
            typeof(DataUsageChartControl),
            new PropertyMetadata(Array.Empty<DataUsageChartPoint>(), OnVisualPropertyChanged));

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(DataUsageChartControl),
            new PropertyMetadata(Color.FromArgb(255, 45, 212, 191), OnVisualPropertyChanged));

    public static readonly DependencyProperty IsLightThemeProperty =
        DependencyProperty.Register(
            nameof(IsLightTheme),
            typeof(bool),
            typeof(DataUsageChartControl),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty EmptyStateTextProperty =
        DependencyProperty.Register(
            nameof(EmptyStateText),
            typeof(string),
            typeof(DataUsageChartControl),
            new PropertyMetadata("No consumption data recorded for this period.", OnVisualPropertyChanged));

    private readonly CanvasControl _canvas;
    private int _hoveredIndex = -1;
    private readonly List<Rect> _barHitRects = new();

    public DataUsageChartControl()
    {
        MinHeight = 200;
        Height = 216;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);

        _canvas = new CanvasControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _canvas.Draw += OnCanvasDraw;
        Children.Add(_canvas);

        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
        Unloaded += OnUnloaded;
    }

    public IReadOnlyList<DataUsageChartPoint> Points
    {
        get => (IReadOnlyList<DataUsageChartPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public bool IsLightTheme
    {
        get => (bool)GetValue(IsLightThemeProperty);
        set => SetValue(IsLightThemeProperty, value);
    }

    public string EmptyStateText
    {
        get => (string)GetValue(EmptyStateTextProperty);
        set => SetValue(EmptyStateTextProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataUsageChartControl control)
        {
            if (e.Property == PointsProperty)
            {
                control._hoveredIndex = -1;
            }

            control._canvas.Invalidate();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hoveredIndex = -1;
        _barHitRects.Clear();
        CanvasDevice.GetSharedDevice()?.Trim();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_canvas).Position;
        UpdateHoverIndex(pos);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
        {
            var pos = e.GetCurrentPoint(_canvas).Position;
            UpdateHoverIndex(pos);
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            _canvas.Invalidate();
        }
    }

    private void UpdateHoverIndex(Point pos)
    {
        int found = -1;
        for (int i = 0; i < _barHitRects.Count; i++)
        {
            var r = _barHitRects[i];
            if (pos.X >= r.Left && pos.X <= r.Right && pos.Y >= 8 && pos.Y <= _canvas.ActualHeight - 4)
            {
                found = i;
                break;
            }
        }

        if (found != _hoveredIndex)
        {
            _hoveredIndex = found;
            _canvas.Invalidate();
        }
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        _barHitRects.Clear();
        var ds = args.DrawingSession;
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        if (width <= 24f || height <= 48f)
        {
            return;
        }

        bool isLight = ActualTheme == ElementTheme.Light || IsLightTheme;
        Color primaryInk = isLight ? Color.FromArgb(230, 18, 24, 34) : Color.FromArgb(235, 244, 247, 252);
        Color mutedInk = isLight ? Color.FromArgb(145, 50, 60, 74) : Color.FromArgb(150, 192, 204, 224);
        Color gridLineColor = isLight ? Color.FromArgb(26, 20, 30, 45) : Color.FromArgb(28, 255, 255, 255);
        Color trackSlotColor = isLight ? Color.FromArgb(14, 15, 23, 42) : Color.FromArgb(14, 255, 255, 255);

        var points = Points ?? Array.Empty<DataUsageChartPoint>();
        if (points.Count == 0)
        {
            using var emptyFmt = new CanvasTextFormat
            {
                FontSize = 12.5f,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(EmptyStateText ?? string.Empty, new Rect(12, 12, width - 24, height - 24), mutedInk, emptyFmt);
            return;
        }

        float leftPad = 56f;
        float rightPad = 14f;
        float topPad = 16f;
        float bottomPad = 28f;
        float plotWidth = Math.Max(40f, width - leftPad - rightPad);
        float plotHeight = Math.Max(40f, height - topPad - bottomPad);

        long maxBytes = 0;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].TotalBytes > maxBytes)
            {
                maxBytes = points[i].TotalBytes;
            }
        }

        // Keep an unobtrusive floor scale when all points are zero so the chart still looks grounded.
        long scaleMax = maxBytes > 0 ? maxBytes : 1024L * 1024L;

        using var axisFmt = new CanvasTextFormat
        {
            FontSize = 10.5f,
            HorizontalAlignment = CanvasHorizontalAlignment.Right,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        const int GridSteps = 3;
        for (int s = 0; s <= GridSteps; s++)
        {
            float frac = s / (float)GridSteps;
            float y = topPad + plotHeight - (frac * plotHeight);
            ds.DrawLine(leftPad, y, leftPad + plotWidth, y, gridLineColor, 1f);

            long labelBytes = (long)Math.Round(scaleMax * frac);
            string label = TrackPlaybackInfo.FormatByteSize(labelBytes);
            ds.DrawText(label, new Rect(0, y - 9f, leftPad - 8f, 18f), mutedInk, axisFmt);
        }

        int count = points.Count;
        float slotWidth = plotWidth / count;
        float barWidth = Math.Clamp(slotWidth * 0.56f, 6f, 34f);

        Color accentTop = AccentColor.A > 0 ? AccentColor : Color.FromArgb(255, 45, 212, 191);
        Color accentBottom = Color.FromArgb(
            180,
            (byte)Math.Max(0, accentTop.R * 0.68f),
            (byte)Math.Max(0, accentTop.G * 0.68f),
            (byte)Math.Max(0, accentTop.B * 0.68f));

        Color hoverTop = Color.FromArgb(
            255,
            (byte)Math.Min(255, accentTop.R + 28),
            (byte)Math.Min(255, accentTop.G + 28),
            (byte)Math.Min(255, accentTop.B + 28));

        using var xFmt = new CanvasTextFormat
        {
            FontSize = 10f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Top
        };

        int labelStep = count switch
        {
            <= 10 => 1,
            <= 20 => 2,
            <= 35 => 4,
            _ => Math.Max(1, count / 8)
        };

        for (int i = 0; i < count; i++)
        {
            float slotLeft = leftPad + (i * slotWidth);
            _barHitRects.Add(new Rect(slotLeft, topPad, slotWidth, plotHeight + bottomPad));

            float barX = slotLeft + ((slotWidth - barWidth) * 0.5f);
            float cornerRadius = Math.Min(6f, barWidth * 0.42f);

            // Subtle slot pillar behind each bar
            ds.FillRoundedRectangle(
                new Rect(barX, topPad, barWidth, plotHeight),
                cornerRadius,
                cornerRadius,
                trackSlotColor);

            var pt = points[i];
            if (pt.TotalBytes > 0)
            {
                float ratio = (float)Math.Clamp(pt.TotalBytes / (double)scaleMax, 0.0, 1.0);
                float barH = Math.Max(4f, ratio * plotHeight);
                float barY = topPad + plotHeight - barH;
                bool isHovered = i == _hoveredIndex;

                using var barBrush = new CanvasLinearGradientBrush(
                    ds,
                    new[]
                    {
                        new CanvasGradientStop(0f, isHovered ? hoverTop : accentTop),
                        new CanvasGradientStop(1f, accentBottom)
                    })
                {
                    StartPoint = new Vector2(barX, barY),
                    EndPoint = new Vector2(barX, barY + barH)
                };

                ds.FillRoundedRectangle(
                    new Rect(barX, barY, barWidth, barH),
                    cornerRadius,
                    cornerRadius,
                    barBrush);

                if (isHovered)
                {
                    ds.DrawRoundedRectangle(
                        new Rect(barX - 0.5f, barY - 0.5f, barWidth + 1f, barH + 1f),
                        cornerRadius,
                        cornerRadius,
                        Color.FromArgb(200, 255, 255, 255),
                        1.25f);
                }
            }

            if (i % labelStep == 0 || i == count - 1)
            {
                ds.DrawText(
                    pt.Label,
                    new Rect(slotLeft - 14f, topPad + plotHeight + 6f, slotWidth + 28f, 18f),
                    i == _hoveredIndex ? primaryInk : mutedInk,
                    xFmt);
            }
        }

        if (_hoveredIndex >= 0 && _hoveredIndex < count)
        {
            DrawTooltip(ds, width, topPad, plotHeight, points[_hoveredIndex], _barHitRects[_hoveredIndex], isLight, accentTop);
        }
        else if (maxBytes == 0)
        {
            using var overlayFmt = new CanvasTextFormat
            {
                FontSize = 11.5f,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(EmptyStateText ?? string.Empty, new Rect(leftPad, topPad, plotWidth, plotHeight), mutedInk, overlayFmt);
        }
    }

    private static void DrawTooltip(
        CanvasDrawingSession ds,
        float canvasWidth,
        float topPad,
        float plotHeight,
        DataUsageChartPoint pt,
        Rect slotRect,
        bool isLight,
        Color accentColor)
    {
        float tipW = 196f;
        float tipH = 84f;
        float centerX = (float)(slotRect.Left + (slotRect.Width * 0.5));
        float tipX = Math.Clamp(centerX - (tipW * 0.5f), 8f, Math.Max(8f, canvasWidth - tipW - 8f));
        float tipY = Math.Max(4f, topPad + 4f);
        if (tipY + tipH > topPad + plotHeight)
        {
            tipY = Math.Max(4f, topPad + plotHeight - tipH);
        }

        Color bg = isLight ? Color.FromArgb(245, 248, 250, 254) : Color.FromArgb(242, 18, 22, 30);
        Color border = isLight ? Color.FromArgb(46, 20, 30, 45) : Color.FromArgb(54, 255, 255, 255);
        Color titleColor = isLight ? Color.FromArgb(245, 16, 22, 32) : Color.FromArgb(248, 250, 252, 255);
        Color bodyColor = isLight ? Color.FromArgb(190, 50, 62, 80) : Color.FromArgb(200, 204, 214, 230);

        var rect = new Rect(tipX, tipY, tipW, tipH);
        ds.FillRoundedRectangle(rect, 8f, 8f, bg);
        ds.DrawRoundedRectangle(rect, 8f, 8f, border, 1f);

        using var headerFmt = new CanvasTextFormat { FontSize = 11.5f, FontWeight = FontWeights.SemiBold };
        using var totalFmt = new CanvasTextFormat
        {
            FontSize = 12f,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Right
        };
        using var rowFmt = new CanvasTextFormat { FontSize = 10.5f };

        ds.DrawText(pt.TooltipTitle, new Rect(tipX + 10, tipY + 7, tipW - 86, 16), titleColor, headerFmt);
        ds.DrawText(pt.FormattedTotalBytes, new Rect(tipX + 10, tipY + 7, tipW - 20, 16), accentColor, totalFmt);

        string line1 = $"Audio: {TrackPlaybackInfo.FormatByteSize(pt.AudioBytes)}  •  Art: {TrackPlaybackInfo.FormatByteSize(pt.ArtworkBytes)}";
        string line2 = $"Lyrics: {TrackPlaybackInfo.FormatByteSize(pt.LyricsBytes)}  •  Canvas: {TrackPlaybackInfo.FormatByteSize(pt.CanvasBytes)}";
        ds.DrawText(line1, new Rect(tipX + 10, tipY + 32, tipW - 20, 18), bodyColor, rowFmt);
        ds.DrawText(line2, new Rect(tipX + 10, tipY + 51, tipW - 20, 18), bodyColor, rowFmt);
    }
}
