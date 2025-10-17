using System;
using System.Collections.Generic;
using System.Windows;
using WpfPanel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace VintageStoryModManager.Views;

public class OverlappingTagPanel : WpfPanel
{
    public OverlappingTagPanel()
    {
        ClipToBounds = true;
    }

    private readonly List<Rect> _arrangeRects = new();

    public static readonly DependencyProperty MaxSpacingProperty = DependencyProperty.Register(
        nameof(MaxSpacing),
        typeof(double),
        typeof(OverlappingTagPanel),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MaxSpacing
    {
        get => (double)GetValue(MaxSpacingProperty);
        set => SetValue(MaxSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int childCount = InternalChildren.Count;
        if (childCount == 0)
        {
            return new Size(0d, 0d);
        }

        double[] widths = new double[childCount];
        double[] heights = new double[childCount];

        for (int i = 0; i < childCount; i++)
        {
            UIElement child = InternalChildren[i];
            if (child is null)
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[i] = child.DesiredSize.Width;
            heights[i] = child.DesiredSize.Height;
        }

        return ComputeDesiredSize(availableSize, widths, heights);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int childCount = InternalChildren.Count;
        if (childCount == 0)
        {
            return finalSize;
        }

        double[] widths = new double[childCount];
        double[] heights = new double[childCount];

        for (int i = 0; i < childCount; i++)
        {
            UIElement child = InternalChildren[i];
            if (child is null)
            {
                continue;
            }

            widths[i] = child.DesiredSize.Width;
            heights[i] = child.DesiredSize.Height;
        }

        Size arrangedSize = ArrangeChildren(finalSize, widths, heights);

        for (int i = 0; i < childCount; i++)
        {
            UIElement child = InternalChildren[i];
            if (child is null)
            {
                continue;
            }

            Rect rect = i < _arrangeRects.Count
                ? _arrangeRects[i]
                : new Rect(new Size(widths[i], heights[i]));

            child.Arrange(rect);
            WpfPanel.SetZIndex(child, childCount - i);
        }

        return arrangedSize;
    }

    private Size ComputeDesiredSize(Size availableSize, IReadOnlyList<double> widths, IReadOnlyList<double> heights)
    {
        double widthConstraint = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0d, availableSize.Width);
        double heightConstraint = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : Math.Max(0d, availableSize.Height);

        DetermineSpacing(widthConstraint, heightConstraint, widths, heights, null, out double measuredWidth, out double measuredHeight);

        return new Size(measuredWidth, measuredHeight);
    }

    private Size ArrangeChildren(Size finalSize, IReadOnlyList<double> widths, IReadOnlyList<double> heights)
    {
        double widthConstraint = double.IsInfinity(finalSize.Width) ? double.PositiveInfinity : Math.Max(0d, finalSize.Width);
        double heightConstraint = double.IsInfinity(finalSize.Height) ? double.PositiveInfinity : Math.Max(0d, finalSize.Height);

        DetermineSpacing(widthConstraint, heightConstraint, widths, heights, _arrangeRects, out double measuredWidth, out double measuredHeight);

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
        int count = widths.Count;
        if (count == 0)
        {
            measuredWidth = 0d;
            measuredHeight = 0d;
            rects?.Clear();
            return 0d;
        }

        double spacingUpper = MaxSpacing;
        if (count > 1)
        {
            double spacingFalloff = MaxSpacing / Math.Sqrt(count);
            spacingUpper = Math.Max(0d, Math.Min(MaxSpacing, spacingFalloff));
        }
        double spacingLower = 0d;

        if (!double.IsInfinity(widthConstraint) && count > 1)
        {
            double totalWidth = 0d;
            for (int i = 0; i < count; i++)
            {
                totalWidth += widths[i];
            }

            double candidate = (widthConstraint - totalWidth) / (count - 1);
            spacingLower = Math.Max(0d, Math.Min(spacingUpper, candidate));
        }

        spacingLower = Math.Min(spacingLower, spacingUpper);

        double bestSpacing = spacingUpper;
        double heightAtUpper = ComputeLayout(widthConstraint, spacingUpper, widths, heights, null, out double widthAtUpper);
        double bestHeight = heightAtUpper;
        double bestWidth = widthAtUpper;

        if (!double.IsInfinity(heightConstraint) && heightAtUpper > heightConstraint && count > 1)
        {
            double heightAtLower = ComputeLayout(widthConstraint, spacingLower, widths, heights, null, out double widthAtLower);

            if (heightAtLower > heightConstraint)
            {
                bestSpacing = spacingLower;
                bestHeight = heightAtLower;
                bestWidth = widthAtLower;
            }
            else
            {
                double lo = spacingLower;
                double hi = spacingUpper;

                for (int iteration = 0; iteration < 48; iteration++)
                {
                    double mid = (lo + hi) * 0.5d;
                    double heightAtMid = ComputeLayout(widthConstraint, mid, widths, heights, null, out double widthAtMid);

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

        int count = widths.Count;
        double x = 0d;
        double y = 0d;
        double rowHeight = 0d;
        double maxWidth = 0d;

        const double epsilon = 0.1d;

        for (int i = 0; i < count; i++)
        {
            double childWidth = widths[i];
            double childHeight = heights[i];

            if (!double.IsInfinity(widthConstraint) && x > 0d && x + childWidth > widthConstraint + epsilon)
            {
                y += rowHeight;
                rowHeight = 0d;
                x = 0d;
            }

            double childX = x;
            if (childX < 0d)
            {
                childX = 0d;
            }

            Rect rect = new Rect(childX, y, childWidth, childHeight);
            rects?.Add(rect);

            rowHeight = Math.Max(rowHeight, childHeight);
            maxWidth = Math.Max(maxWidth, rect.Right);

            x = childX + childWidth + spacing;
            if (x < 0d)
            {
                x = 0d;
            }
        }

        double totalHeight = y + rowHeight;
        measuredWidth = double.IsInfinity(widthConstraint) ? maxWidth : Math.Min(maxWidth, widthConstraint);
        return totalHeight;
    }
}
