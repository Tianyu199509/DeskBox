using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DeskBox.Controls;

/// <summary>
/// A lightweight, AOT-safe horizontal wrap panel that left-aligns each wrapped
/// line with configurable horizontal and vertical spacing. Collapsed children
/// are skipped so conditionally hidden action buttons never introduce phantom
/// spacing gaps, and MeasureOverride claims only the widest wrapped line so the
/// panel remains compact inside right-aligned SettingsCard content slots while
/// wrapping cleanly onto multiple lines when constrained on narrow viewports.
/// </summary>
public sealed partial class WrapFlowPanel : Panel
{
    /// <summary>
    /// Tolerance during MeasureOverride so minor IEEE-754 float rounding does not
    /// prematurely split a line that fits the available width.
    /// </summary>
    internal const double MeasureWrapTolerance = 0.05;

    /// <summary>
    /// Tolerance during ArrangeOverride so WinRT <see cref="Size"/> 32-bit float
    /// truncation and WinUI <c>UseLayoutRounding</c> pixel snapping (e.g. 0.8 DIP
    /// steps at 125% DPI) never wrap a line that MeasureOverride kept on one row.
    /// </summary>
    internal const double ArrangeWrapTolerance = 0.75;

    private bool _lastMeasuredUnbounded;
    private double _lastUnwrappedWidth;
    private double _constrainedArrangeWidth = double.NaN;
    private bool _remeasureQueued;

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrapFlowPanel),
            new PropertyMetadata(0d, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrapFlowPanel),
            new PropertyMetadata(0d, OnSpacingChanged));

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

    private static void OnSpacingChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var panel = (WrapFlowPanel)sender;
        panel._constrainedArrangeWidth = double.NaN;
        panel.InvalidateMeasure();
    }

    internal static Size ComputeMeasuredSize(
        ReadOnlySpan<Size> visibleChildSizes,
        double availableWidth,
        double horizontalSpacing,
        double verticalSpacing,
        double constrainedUnboundedWidth = double.NaN)
    {
        bool unbounded = double.IsInfinity(availableWidth);
        double lineWidth = 0;
        double lineThickness = 0;
        double maxLineWidth = 0;
        double totalHeight = 0;

        for (int i = 0; i < visibleChildSizes.Length; i++)
        {
            Size desired = visibleChildSizes[i];
            if (!unbounded &&
                lineWidth > 0 &&
                lineWidth + horizontalSpacing + desired.Width > availableWidth + MeasureWrapTolerance)
            {
                maxLineWidth = Math.Max(maxLineWidth, lineWidth);
                totalHeight += lineThickness + verticalSpacing;
                lineWidth = desired.Width;
                lineThickness = desired.Height;
            }
            else
            {
                lineWidth = lineWidth == 0
                    ? desired.Width
                    : lineWidth + horizontalSpacing + desired.Width;
                lineThickness = Math.Max(lineThickness, desired.Height);
            }
        }

        maxLineWidth = Math.Max(maxLineWidth, lineWidth);
        totalHeight += lineThickness;

        // Round upward to an exact binary fraction (1/64 = 2^-6) so storing into
        // Windows.Foundation.Size's 32-bit float Width never truncates below maxLineWidth.
        double floatSafeWidth = maxLineWidth > 0
            ? Math.Ceiling(maxLineWidth * 64.0) / 64.0
            : 0;

        if (unbounded && !double.IsNaN(constrainedUnboundedWidth) && constrainedUnboundedWidth > 0)
        {
            Size wrappedAtConstraint = ComputeMeasuredSize(
                visibleChildSizes,
                constrainedUnboundedWidth,
                horizontalSpacing,
                verticalSpacing,
                double.NaN);
            totalHeight = Math.Max(totalHeight, wrappedAtConstraint.Height);
        }

        return new Size(
            unbounded ? floatSafeWidth : Math.Min(floatSafeWidth, availableWidth),
            totalHeight);
    }

    internal static double ComputeArrangement(
        ReadOnlySpan<Size> visibleChildSizes,
        Size finalSize,
        double horizontalSpacing,
        double verticalSpacing,
        Span<Rect> destinationRects)
    {
        double x = 0;
        double y = 0;
        double lineThickness = 0;

        for (int i = 0; i < visibleChildSizes.Length; i++)
        {
            Size desired = visibleChildSizes[i];
            if (x > 0 && x + horizontalSpacing + desired.Width > finalSize.Width + ArrangeWrapTolerance)
            {
                y += lineThickness + verticalSpacing;
                x = 0;
                lineThickness = 0;
            }

            double childX = x == 0 ? 0 : x + horizontalSpacing;
            double arrangedWidth = Math.Min(desired.Width, Math.Max(0, finalSize.Width - childX));
            destinationRects[i] = new Rect(childX, y, arrangedWidth, desired.Height);
            x = childX + arrangedWidth;
            lineThickness = Math.Max(lineThickness, desired.Height);
        }

        return y + lineThickness;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _remeasureQueued = false;
        bool unbounded = double.IsInfinity(availableSize.Width);
        _lastMeasuredUnbounded = unbounded;
        if (!unbounded)
        {
            _constrainedArrangeWidth = double.NaN;
        }

        int childCount = Children.Count;
        var visibleSizes = new List<Size>(childCount);
        for (int index = 0; index < childCount; index++)
        {
            UIElement child = Children[index];
            child.Measure(availableSize);
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            visibleSizes.Add(child.DesiredSize);
        }

        ReadOnlySpan<Size> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(visibleSizes);
        if (unbounded)
        {
            _lastUnwrappedWidth = ComputeMeasuredSize(
                span,
                double.PositiveInfinity,
                HorizontalSpacing,
                VerticalSpacing,
                double.NaN).Width;
        }

        return ComputeMeasuredSize(
            span,
            availableSize.Width,
            HorizontalSpacing,
            VerticalSpacing,
            _constrainedArrangeWidth);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int childCount = Children.Count;
        var visibleChildren = new List<UIElement>(childCount);
        var visibleSizes = new List<Size>(childCount);
        for (int index = 0; index < childCount; index++)
        {
            UIElement child = Children[index];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            visibleChildren.Add(child);
            visibleSizes.Add(child.DesiredSize);
        }

        var rects = new Rect[visibleChildren.Count];
        double arrangedHeight = ComputeArrangement(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(visibleSizes),
            finalSize,
            HorizontalSpacing,
            VerticalSpacing,
            rects);

        for (int i = 0; i < visibleChildren.Count; i++)
        {
            visibleChildren[i].Arrange(rects[i]);
        }

        if (_lastMeasuredUnbounded)
        {
            bool needsConstraintUpdate = false;
            if (arrangedHeight > finalSize.Height + MeasureWrapTolerance &&
                (double.IsNaN(_constrainedArrangeWidth) ||
                 Math.Abs(_constrainedArrangeWidth - finalSize.Width) > MeasureWrapTolerance))
            {
                _constrainedArrangeWidth = finalSize.Width;
                needsConstraintUpdate = true;
            }
            else if (!double.IsNaN(_constrainedArrangeWidth))
            {
                if (finalSize.Width + ArrangeWrapTolerance >= _lastUnwrappedWidth)
                {
                    _constrainedArrangeWidth = double.NaN;
                    needsConstraintUpdate = true;
                }
                else if (Math.Abs(_constrainedArrangeWidth - finalSize.Width) > ArrangeWrapTolerance &&
                         Math.Abs(arrangedHeight - finalSize.Height) > MeasureWrapTolerance)
                {
                    _constrainedArrangeWidth = finalSize.Width;
                    needsConstraintUpdate = true;
                }
            }

            if (needsConstraintUpdate && !_remeasureQueued)
            {
                _remeasureQueued = true;
                if (DispatcherQueue?.TryEnqueue(() =>
                    {
                        _remeasureQueued = false;
                        InvalidateMeasure();
                    }) != true)
                {
                    _remeasureQueued = false;
                    InvalidateMeasure();
                }
            }
        }

        return new Size(finalSize.Width, Math.Max(finalSize.Height, arrangedHeight));
    }
}
