using System.Windows;
using WpfPanel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace VintageStoryModManager.Views;

public class OverlappingTagPanel : WpfPanel
{
    public static readonly DependencyProperty MaxSpacingProperty = DependencyProperty.Register(
        nameof(MaxSpacing),
        typeof(double),
        typeof(OverlappingTagPanel),
        new FrameworkPropertyMetadata(6d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    private readonly List<Rect> _arrangeRects = new();

    public OverlappingTagPanel()
    {
        ClipToBounds = true;
    }

    public double MaxSpacing
    {
        get => (double)GetValue(MaxSpacingProperty);
        set => SetValue(MaxSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childCount = InternalChildren.Count;
        if (childCount == 0) return new Size(0d, 0d);

        var widths = new double[childCount];
        var heights = new double[childCount];

        for (var i = 0; i < childCount; i++)
        {
            var child = InternalChildren[i];
            if (child is null) continue;

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[i] = child.DesiredSize.Width;
            heights[i] = child.DesiredSize.Height;
        }

        return ComputeDesiredSize(availableSize, widths, heights);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var childCount = InternalChildren.Count;
        if (childCount == 0) return finalSize;

        var widths = new double[childCount];
        var heights = new double[childCount];

        for (var i = 0; i < childCount; i++)
        {
            var child = InternalChildren[i];
            if (child is null) continue;

            widths[i] = child.DesiredSize.Width;
            heights[i] = child.DesiredSize.Height;
        }

        var arrangedSize = ArrangeChildren(finalSize, widths, heights);

        for (var i = 0; i < childCount; i++)
        {
            var child = InternalChildren[i];
            if (child is null) continue;

            var rect = i < _arrangeRects.Count
                ? _arrangeRects[i]
                : new Rect(new Size(widths[i], heights[i]));

            child.Arrange(rect);
            SetZIndex(child, childCount - i);
        }

        return arrangedSize;
    }

    private Size ComputeDesiredSize(Size availableSize, IReadOnlyList<double> widths, IReadOnlyList<double> heights)
    {
        var widthConstraint = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0d, availableSize.Width);
        var heightConstraint = double.IsInfinity(availableSize.Height)
            ? double.PositiveInfinity
            : Math.Max(0d, availableSize.Height);

        DetermineSpacing(widthConstraint, heightConstraint, widths, heights, null, out var measuredWidth,
            out var measuredHeight);

        return new Size(measuredWidth, measuredHeight);
    }

    private Size ArrangeChildren(Size finalSize, IReadOnlyList<double> widths, IReadOnlyList<double> heights)
    {
        var widthConstraint =
            double.IsInfinity(finalSize.Width) ? double.PositiveInfinity : Math.Max(0d, finalSize.Width);
        var heightConstraint = double.IsInfinity(finalSize.Height)
            ? double.PositiveInfinity
            : Math.Max(0d, finalSize.Height);

        DetermineSpacing(widthConstraint, heightConstraint, widths, heights, _arrangeRects, out var measuredWidth,
            out var measuredHeight);

        return new Size(measuredWidth, measuredHeight);
    }

    private double DetermineSpacing(
        double widthConstraint,
        double heightConstraint,
        IReadOnlyList<double> widths,
        IReadOnlyList<double> heights,
        List<Rect>? rects,
        out double measuredWidth,
        out double measuredHeight)
    {
        var count = widths.Count;
        if (count == 0)
        {
            measuredWidth = 0d;
            measuredHeight = 0d;
            rects?.Clear();
            return 0d;
        }

        var spacingUpper = MaxSpacing;
        if (count > 1)
        {
            var spacingFalloff = MaxSpacing / Math.Sqrt(count);
            spacingUpper = Math.Max(0d, Math.Min(MaxSpacing, spacingFalloff));
        }

        var spacingLower = 0d;

        if (!double.IsInfinity(widthConstraint) && count > 1)
        {
            var totalWidth = 0d;
            for (var i = 0; i < count; i++) totalWidth += widths[i];

            var candidate = (widthConstraint - totalWidth) / (count - 1);
            spacingLower = Math.Max(0d, Math.Min(spacingUpper, candidate));
        }

        spacingLower = Math.Min(spacingLower, spacingUpper);

        var bestSpacing = spacingUpper;
        var heightAtUpper = ComputeLayout(widthConstraint, spacingUpper, widths, heights, null, out var widthAtUpper);
        var bestHeight = heightAtUpper;
        var bestWidth = widthAtUpper;

        if (!double.IsInfinity(heightConstraint) && heightAtUpper > heightConstraint && count > 1)
        {
            var heightAtLower =
                ComputeLayout(widthConstraint, spacingLower, widths, heights, null, out var widthAtLower);

            if (heightAtLower > heightConstraint)
            {
                bestSpacing = spacingLower;
                bestHeight = heightAtLower;
                bestWidth = widthAtLower;
            }
            else
            {
                var lo = spacingLower;
                var hi = spacingUpper;

                for (var iteration = 0; iteration < 48; iteration++)
                {
                    var mid = (lo + hi) * 0.5d;
                    var heightAtMid = ComputeLayout(widthConstraint, mid, widths, heights, null, out var widthAtMid);

                    if (heightAtMid <= heightConstraint)
                    {
                        bestSpacing = mid;
                        bestHeight = heightAtMid;
                        bestWidth = widthAtMid;
                        hi = mid;
                    }
                    else
                    {
                        lo = mid;
                    }
                }
            }
        }

        if (rects is not null)
        {
            rects.Clear();
            ComputeLayout(widthConstraint, bestSpacing, widths, heights, rects, out _);
        }

        measuredWidth = bestWidth;
        measuredHeight = bestHeight;
        return bestSpacing;
    }

    private double ComputeLayout(
        double widthConstraint,
        double spacing,
        IReadOnlyList<double> widths,
        IReadOnlyList<double> heights,
        List<Rect>? rects,
        out double measuredWidth)
    {
        rects?.Clear();

        var count = widths.Count;
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        var maxWidth = 0d;

        const double epsilon = 0.1d;

        for (var i = 0; i < count; i++)
        {
            var childWidth = widths[i];
            var childHeight = heights[i];

            if (!double.IsInfinity(widthConstraint) && x > 0d && x + childWidth > widthConstraint + epsilon)
            {
                y += rowHeight;
                rowHeight = 0d;
                x = 0d;
            }

            var childX = x;
            if (childX < 0d) childX = 0d;

            var rect = new Rect(childX, y, childWidth, childHeight);
            rects?.Add(rect);

            rowHeight = Math.Max(rowHeight, childHeight);
            maxWidth = Math.Max(maxWidth, rect.Right);

            x = childX + childWidth + spacing;
            if (x < 0d) x = 0d;
        }

        var totalHeight = y + rowHeight;
        measuredWidth = double.IsInfinity(widthConstraint) ? maxWidth : Math.Min(maxWidth, widthConstraint);
        return totalHeight;
    }
}