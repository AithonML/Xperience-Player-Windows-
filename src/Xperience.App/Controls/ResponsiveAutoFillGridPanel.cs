using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Xperience.App.Controls;

/// <summary>
/// A fluid auto-fill responsive grid panel for WinUI 3 <see cref="ItemsControl"/> layouts.
/// Cards or rows stretch evenly to occupy 100% of available horizontal width with zero trailing gap;
/// as soon as the viewport widens enough to fit an additional column at <see cref="MinItemWidth"/>,
/// the column count increments and all items snap cleanly to fill the full width.
/// </summary>
public sealed class ResponsiveAutoFillGridPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(
            nameof(MinItemWidth),
            typeof(double),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(184.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(
            nameof(MaxColumns),
            typeof(int),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(14.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(14.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemAspectRatioProperty =
        DependencyProperty.Register(
            nameof(ItemAspectRatio),
            typeof(double),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MinItemHeightProperty =
        DependencyProperty.Register(
            nameof(MinItemHeight),
            typeof(double),
            typeof(ResponsiveAutoFillGridPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>
    /// Gets or sets the minimum item width used to compute the fluid column count.
    /// </summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the maximum number of columns (0 or less means unlimited).
    /// </summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>
    /// Gets or sets the horizontal spacing in DIPs between adjacent columns.
    /// </summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the vertical spacing in DIPs between adjacent rows.
    /// </summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the optional height-to-width aspect ratio for items (e.g., 1.18 for portrait cards).
    /// When 0 or less, each row sizes vertically to the tallest child in that row.
    /// </summary>
    public double ItemAspectRatio
    {
        get => (double)GetValue(ItemAspectRatioProperty);
        set => SetValue(ItemAspectRatioProperty, value);
    }

    /// <summary>
    /// Gets or sets an optional minimum height floor for each row.
    /// </summary>
    public double MinItemHeight
    {
        get => (double)GetValue(MinItemHeightProperty);
        set => SetValue(MinItemHeightProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ResponsiveAutoFillGridPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    private (int Columns, double ItemWidth) ComputeColumnLayout(double availableWidth)
    {
        double minWidth = Math.Max(40.0, MinItemWidth);
        double colGap = Math.Max(0.0, ColumnSpacing);
        int maxCols = MaxColumns;

        double usableWidth = availableWidth;
        if (double.IsInfinity(usableWidth) || double.IsNaN(usableWidth) || usableWidth <= 0.0)
        {
            if (ActualWidth > 0.0)
            {
                usableWidth = ActualWidth;
            }
            else
            {
                int fallbackCols = maxCols > 0 ? maxCols : 4;
                usableWidth = (minWidth * fallbackCols) + (colGap * Math.Max(0, fallbackCols - 1));
            }
        }

        int columns = Math.Max(1, (int)Math.Floor((usableWidth + colGap) / (minWidth + colGap)));
        if (maxCols > 0)
        {
            columns = Math.Min(columns, maxCols);
        }

        double itemWidth = Math.Max(1.0, (usableWidth - (colGap * (columns - 1))) / columns);
        return (columns, itemWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var (columns, itemWidth) = ComputeColumnLayout(availableSize.Width);
        double colGap = Math.Max(0.0, ColumnSpacing);
        double rowGap = Math.Max(0.0, RowSpacing);
        double aspect = ItemAspectRatio;
        double minHeight = Math.Max(0.0, MinItemHeight);

        double fixedHeight = aspect > 0.0 ? Math.Max(minHeight, itemWidth * aspect) : 0.0;

        int visibleIndex = 0;
        double currentRowHeight = 0.0;
        double totalHeight = 0.0;
        int rowCount = 0;

        for (int i = 0; i < count; i++)
        {
            UIElement child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            if (fixedHeight > 0.0)
            {
                child.Measure(new Size(itemWidth, fixedHeight));
                currentRowHeight = fixedHeight;
            }
            else
            {
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                currentRowHeight = Math.Max(currentRowHeight, Math.Max(minHeight, child.DesiredSize.Height));
            }

            visibleIndex++;
            if (visibleIndex % columns == 0)
            {
                if (rowCount > 0)
                {
                    totalHeight += rowGap;
                }

                totalHeight += currentRowHeight;
                rowCount++;
                currentRowHeight = 0.0;
            }
        }

        if (visibleIndex % columns != 0)
        {
            if (rowCount > 0)
            {
                totalHeight += rowGap;
            }

            totalHeight += currentRowHeight;
        }

        double desiredWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? (itemWidth * columns) + (colGap * (columns - 1))
            : availableSize.Width;

        return new Size(Math.Max(0.0, desiredWidth), Math.Max(0.0, totalHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            return finalSize;
        }

        var (columns, itemWidth) = ComputeColumnLayout(finalSize.Width);
        double colGap = Math.Max(0.0, ColumnSpacing);
        double rowGap = Math.Max(0.0, RowSpacing);
        double aspect = ItemAspectRatio;
        double minHeight = Math.Max(0.0, MinItemHeight);
        double fixedHeight = aspect > 0.0 ? Math.Max(minHeight, itemWidth * aspect) : 0.0;

        // Collect visible children so each row arranges with uniform row height.
        var visibleChildren = new List<UIElement>(count);
        for (int i = 0; i < count; i++)
        {
            UIElement child = Children[i];
            if (child.Visibility != Visibility.Collapsed)
            {
                visibleChildren.Add(child);
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        double y = 0.0;
        int totalVisible = visibleChildren.Count;
        for (int rowStart = 0; rowStart < totalVisible; rowStart += columns)
        {
            int rowEnd = Math.Min(totalVisible, rowStart + columns);
            double rowHeight = fixedHeight;
            if (rowHeight <= 0.0)
            {
                rowHeight = minHeight;
                for (int i = rowStart; i < rowEnd; i++)
                {
                    rowHeight = Math.Max(rowHeight, visibleChildren[i].DesiredSize.Height);
                }
            }

            for (int i = rowStart; i < rowEnd; i++)
            {
                int col = i - rowStart;
                double x = col * (itemWidth + colGap);
                visibleChildren[i].Arrange(new Rect(x, y, itemWidth, rowHeight));
            }

            y += rowHeight;
            if (rowEnd < totalVisible)
            {
                y += rowGap;
            }
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, y));
    }
}
