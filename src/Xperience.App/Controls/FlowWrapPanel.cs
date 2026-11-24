using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Xperience.App.Controls;

/// <summary>
/// A lightweight horizontal wrapping panel (matching Android Compose's <c>FlowRow</c>)
/// for genre pills and filter chips.
/// </summary>
public sealed class FlowWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(FlowWrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(FlowWrapPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlowWrapPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0.0, availableSize.Width);

        double hGap = Math.Max(0.0, HorizontalSpacing);
        double vGap = Math.Max(0.0, VerticalSpacing);

        double lineWidth = 0.0;
        double lineHeight = 0.0;
        double totalWidth = 0.0;
        double totalHeight = 0.0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            Size desired = child.DesiredSize;

            double needed = lineWidth > 0.0 ? hGap + desired.Width : desired.Width;
            if (lineWidth > 0.0 && (lineWidth + needed) > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += (totalHeight > 0.0 ? vGap : 0.0) + lineHeight;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth += needed;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        if (lineWidth > 0.0 || lineHeight > 0.0)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += (totalHeight > 0.0 ? vGap : 0.0) + lineHeight;
        }

        double finalWidth = double.IsInfinity(maxWidth) ? totalWidth : Math.Min(maxWidth, Math.Max(totalWidth, availableSize.Width));
        return new Size(Math.Max(0.0, finalWidth), Math.Max(0.0, totalHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = Math.Max(0.0, finalSize.Width);
        double hGap = Math.Max(0.0, HorizontalSpacing);
        double vGap = Math.Max(0.0, VerticalSpacing);

        double x = 0.0;
        double y = 0.0;
        double lineHeight = 0.0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            Size desired = child.DesiredSize;
            if (x > 0.0 && (x + desired.Width) > maxWidth)
            {
                x = 0.0;
                y += lineHeight + vGap;
                lineHeight = 0.0;
            }

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width + hGap;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, y + lineHeight));
    }
}
