using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DeskBox.Controls;

/// <summary>
/// Lightweight horizontal wrap panel for the Desktop Organization mini-box canvas
/// and bottom "Stay on Desktop" tray.
/// </summary>
public sealed partial class OrganizationWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;

    public double VerticalSpacing { get; set; } = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxLineWidth = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : double.PositiveInfinity;

        double currentLineWidth = 0;
        double currentLineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        int itemsInLine = 0;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(maxLineWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            Size desired = child.DesiredSize;
            double spacing = itemsInLine > 0 ? HorizontalSpacing : 0;

            if (itemsInLine > 0 && currentLineWidth + spacing + desired.Width > maxLineWidth)
            {
                totalWidth = Math.Max(totalWidth, currentLineWidth);
                totalHeight += currentLineHeight + VerticalSpacing;
                currentLineWidth = desired.Width;
                currentLineHeight = desired.Height;
                itemsInLine = 1;
            }
            else
            {
                currentLineWidth += spacing + desired.Width;
                currentLineHeight = Math.Max(currentLineHeight, desired.Height);
                itemsInLine++;
            }
        }

        if (itemsInLine > 0)
        {
            totalWidth = Math.Max(totalWidth, currentLineWidth);
            totalHeight += currentLineHeight;
        }

        double resultWidth = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? Math.Max(totalWidth, availableSize.Width)
            : totalWidth;
        return new Size(resultWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxLineWidth = finalSize.Width > 0 ? finalSize.Width : double.PositiveInfinity;
        double x = 0;
        double y = 0;
        double currentLineHeight = 0;
        int itemsInLine = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            Size desired = child.DesiredSize;
            double spacing = itemsInLine > 0 ? HorizontalSpacing : 0;

            if (itemsInLine > 0 && x + spacing + desired.Width > maxLineWidth)
            {
                x = 0;
                y += currentLineHeight + VerticalSpacing;
                currentLineHeight = 0;
                spacing = 0;
                itemsInLine = 0;
            }

            x += spacing;
            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width;
            currentLineHeight = Math.Max(currentLineHeight, desired.Height);
            itemsInLine++;
        }

        return new Size(finalSize.Width, y + currentLineHeight);
    }
}
