using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Xperience.App.Controls;

/// <summary>
/// Captures the unscaled column and tile layout geometry for a child inside <see cref="RecentlyPlayedMosaicPanel"/>,
/// enabling jitter-free scroll-edge scale transforms anchored to the column pivot.
/// </summary>
public readonly record struct MosaicTileLayout(
    double ColumnLeft,
    double ColumnWidth,
    double TileTop,
    double TileHeight,
    double ColumnTotalHeight,
    int ColumnIndex,
    int TilesInColumn);

/// <summary>
/// Horizontal irregular Bento mosaic panel matching Android's Recently Played <c>BentoGrid</c>:
/// alternates 2-tile and 3-tile columns with organically staggered tile heights
/// (2-tile: 220/180 or 180/220; 3-tile: 125/125/138 or 138/125/125 at 412 DIP total height).
/// </summary>
public sealed class RecentlyPlayedMosaicPanel : Panel
{
    private readonly Dictionary<UIElement, MosaicTileLayout> _tileLayouts = [];

    public static readonly DependencyProperty ColumnWidthProperty =
        DependencyProperty.Register(
            nameof(ColumnWidth),
            typeof(double),
            typeof(RecentlyPlayedMosaicPanel),
            new PropertyMetadata(192.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MosaicHeightProperty =
        DependencyProperty.Register(
            nameof(MosaicHeight),
            typeof(double),
            typeof(RecentlyPlayedMosaicPanel),
            new PropertyMetadata(412.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(RecentlyPlayedMosaicPanel),
            new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(RecentlyPlayedMosaicPanel),
            new PropertyMetadata(12.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SingleItemSizeProperty =
        DependencyProperty.Register(
            nameof(SingleItemSize),
            typeof(double),
            typeof(RecentlyPlayedMosaicPanel),
            new PropertyMetadata(220.0, OnLayoutPropertyChanged));

    /// <summary>
    /// Gets or sets the width of each mosaic column in DIPs.
    /// </summary>
    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the total height of multi-item mosaic columns in DIPs.
    /// </summary>
    public double MosaicHeight
    {
        get => (double)GetValue(MosaicHeightProperty);
        set => SetValue(MosaicHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the horizontal gap between mosaic columns in DIPs.
    /// </summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the vertical gap between tiles within a mosaic column in DIPs.
    /// </summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>
    /// Gets or sets the square size used when only a single item is present.
    /// </summary>
    public double SingleItemSize
    {
        get => (double)GetValue(SingleItemSizeProperty);
        set => SetValue(SingleItemSizeProperty, value);
    }

    /// <summary>
    /// Raised after the mosaic finishes arranging its children so the host carousel can synchronize
    /// scroll-edge scales with the newly laid-out column geometry.
    /// </summary>
    public event EventHandler? MosaicArranged;

    /// <summary>
    /// Attempts to retrieve the most recent unscaled layout geometry for the specified child element.
    /// </summary>
    public bool TryGetTileLayout(UIElement child, out MosaicTileLayout layout)
        => _tileLayouts.TryGetValue(child, out layout);

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RecentlyPlayedMosaicPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }

    private (double TotalWidth, double TotalHeight) ComputeLayouts(List<UIElement> visibleChildren, List<(UIElement Child, MosaicTileLayout Layout)> results)
    {
        results.Clear();
        int count = visibleChildren.Count;
        if (count == 0)
        {
            return (0.0, 0.0);
        }

        if (count == 1)
        {
            double size = Math.Max(80.0, SingleItemSize);
            results.Add((visibleChildren[0], new MosaicTileLayout(0.0, size, 0.0, size, size, 0, 1)));
            return (size, size);
        }

        double colWidth = Math.Max(80.0, ColumnWidth);
        double totalHeight = Math.Max(180.0, MosaicHeight);
        double colGap = Math.Max(0.0, ColumnSpacing);
        double rowGap = Math.Max(0.0, RowSpacing);

        // Two-card heights: 220 / 180 (sum = 400 at 412 totalHeight & 12 gap)
        double twoAvailable = Math.Max(40.0, totalHeight - rowGap);
        double twoTall = Math.Round(twoAvailable * (220.0 / 400.0));
        double twoShort = Math.Max(20.0, twoAvailable - twoTall);

        // Three-card heights: 125 / 125 / 138 (sum = 388 at 412 totalHeight & 2*12 gaps)
        double threeAvailable = Math.Max(60.0, totalHeight - (2.0 * rowGap));
        double threeSmall = Math.Round(threeAvailable * (125.0 / 388.0));
        double threeLarge = Math.Max(20.0, threeAvailable - (2.0 * threeSmall));

        int index = 0;
        int colIndex = 0;
        double x = 0.0;

        while (index < count)
        {
            int remaining = count - index;
            bool wantThree = (colIndex % 2) == 1;
            bool reverseHeights = ((colIndex / 2) % 2) == 1;

            if (wantThree && remaining >= 3)
            {
                double h0 = reverseHeights ? threeLarge : threeSmall;
                double h1 = threeSmall;
                double h2 = reverseHeights ? threeSmall : threeLarge;

                double y0 = 0.0;
                double y1 = h0 + rowGap;
                double y2 = y1 + h1 + rowGap;

                results.Add((visibleChildren[index], new MosaicTileLayout(x, colWidth, y0, h0, totalHeight, colIndex, 3)));
                results.Add((visibleChildren[index + 1], new MosaicTileLayout(x, colWidth, y1, h1, totalHeight, colIndex, 3)));
                results.Add((visibleChildren[index + 2], new MosaicTileLayout(x, colWidth, y2, h2, totalHeight, colIndex, 3)));
                index += 3;
            }
            else if (remaining >= 2)
            {
                double h0 = reverseHeights ? twoShort : twoTall;
                double h1 = reverseHeights ? twoTall : twoShort;

                double y0 = 0.0;
                double y1 = h0 + rowGap;

                results.Add((visibleChildren[index], new MosaicTileLayout(x, colWidth, y0, h0, totalHeight, colIndex, 2)));
                results.Add((visibleChildren[index + 1], new MosaicTileLayout(x, colWidth, y1, h1, totalHeight, colIndex, 2)));
                index += 2;
            }
            else
            {
                results.Add((visibleChildren[index], new MosaicTileLayout(x, colWidth, 0.0, totalHeight, totalHeight, colIndex, 1)));
                index += 1;
            }

            colIndex++;
            x += colWidth;
            if (index < count)
            {
                x += colGap;
            }
        }

        return (x, totalHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            _tileLayouts.Clear();
            return new Size(0, 0);
        }

        var visibleChildren = new List<UIElement>(count);
        for (int i = 0; i < count; i++)
        {
            UIElement child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
            }
            else
            {
                visibleChildren.Add(child);
            }
        }

        var layouts = new List<(UIElement Child, MosaicTileLayout Layout)>(visibleChildren.Count);
        var (totalWidth, totalHeight) = ComputeLayouts(visibleChildren, layouts);

        _tileLayouts.Clear();
        for (int i = 0; i < layouts.Count; i++)
        {
            var (child, layout) = layouts[i];
            _tileLayouts[child] = layout;
            child.Measure(new Size(layout.ColumnWidth, layout.TileHeight));
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            _tileLayouts.Clear();
            return finalSize;
        }

        var visibleChildren = new List<UIElement>(count);
        for (int i = 0; i < count; i++)
        {
            UIElement child = Children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
            else
            {
                visibleChildren.Add(child);
            }
        }

        var layouts = new List<(UIElement Child, MosaicTileLayout Layout)>(visibleChildren.Count);
        var (totalWidth, totalHeight) = ComputeLayouts(visibleChildren, layouts);

        _tileLayouts.Clear();
        for (int i = 0; i < layouts.Count; i++)
        {
            var (child, layout) = layouts[i];
            _tileLayouts[child] = layout;
            child.Arrange(new Rect(layout.ColumnLeft, layout.TileTop, layout.ColumnWidth, layout.TileHeight));
        }

        MosaicArranged?.Invoke(this, EventArgs.Empty);
        return new Size(Math.Max(finalSize.Width, totalWidth), Math.Max(finalSize.Height, totalHeight));
    }
}
