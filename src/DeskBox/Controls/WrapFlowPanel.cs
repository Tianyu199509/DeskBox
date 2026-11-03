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
        ((WrapFlowPanel)sender).InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double spacing = HorizontalSpacing;
        double lineGap = VerticalSpacing;
        double lineWidth = 0;
        double lineThickness = 0;
        double maxLineWidth = 0;
        double totalHeight = 0;
        bool unbounded = double.IsInfinity(availableSize.Width);

        for (int index = 0; index < Children.Count; index++)
        {
            UIElement child = Children[index];
            child.Measure(availableSize);
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            Size desired = child.DesiredSize;
            if (!unbounded &&
                lineWidth > 0 &&
                lineWidth + spacing + desired.Width > availableSize.Width)
            {
                maxLineWidth = Math.Max(maxLineWidth, lineWidth);
                totalHeight += lineThickness + lineGap;
                lineWidth = desired.Width;
                lineThickness = desired.Height;
            }
            else
            {
                lineWidth = lineWidth == 0
                    ? desired.Width
                    : lineWidth + spacing + desired.Width;
                lineThickness = Math.Max(lineThickness, desired.Height);
            }
        }

        maxLineWidth = Math.Max(maxLineWidth, lineWidth);
        totalHeight += lineThickness;

        return new Size(
            unbounded ? maxLineWidth : Math.Min(maxLineWidth, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double spacing = HorizontalSpacing;
        double lineGap = VerticalSpacing;
        double x = 0;
        double y = 0;
        double lineThickness = 0;

        for (int index = 0; index < Children.Count; index++)
        {
            UIElement child = Children[index];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            Size desired = child.DesiredSize;
            if (x > 0 && x + spacing + desired.Width > finalSize.Width)
            {
                y += lineThickness + lineGap;
                x = 0;
                lineThickness = 0;
            }

            double childX = x == 0 ? 0 : x + spacing;
            double arrangedWidth = Math.Min(desired.Width, Math.Max(0, finalSize.Width - childX));
            child.Arrange(new Rect(childX, y, arrangedWidth, desired.Height));
            x = childX + arrangedWidth;
            lineThickness = Math.Max(lineThickness, desired.Height);
        }

        return finalSize;
    }
}
