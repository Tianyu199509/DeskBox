using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public static class WidgetSegmentedLayoutHelper
{
    public static void ApplyNaturalItemWidths(Segmented segmented)
    {
        var visibleItems = segmented.Items
            .OfType<SegmentedItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToList();
        if (visibleItems.Count == 0)
        {
            return;
        }

        foreach (var item in visibleItems)
        {
            item.Width = double.NaN;
            item.MaxWidth = double.PositiveInfinity;
            item.MinWidth = 0;
            item.ClearValue(Microsoft.UI.Xaml.Controls.Control.PaddingProperty);
            item.ClearValue(FrameworkElement.MinHeightProperty);
        }
    }

    public static void ApplyEqualItemWidths(Segmented segmented)
    {
        var visibleItems = segmented.Items
            .OfType<SegmentedItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToList();
        if (segmented.ActualWidth <= 0 || visibleItems.Count == 0)
        {
            return;
        }

        double itemWidth = Math.Max(0, Math.Floor(segmented.ActualWidth / visibleItems.Count));
        foreach (var item in visibleItems)
        {
            item.Width = itemWidth;
            item.MaxWidth = itemWidth;
            item.MinWidth = 0;
            item.Padding = new Thickness(4, 1, 4, 2);
            item.MinHeight = Math.Max(24, segmented.MinHeight - 3);
        }
    }
}
