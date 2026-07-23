using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

public static class WidgetSegmentedLayoutHelper
{
    private static DispatcherQueue? s_dispatcher;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        s_dispatcher = dispatcher;
    }

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

        // Step 1: Clear previous fixed widths so the Segmented control
        // is not locked at a larger size from the previous layout.
        foreach (var item in visibleItems)
        {
            item.Width = double.NaN;
            item.MaxWidth = double.PositiveInfinity;
        }

        // Step 2: Apply new equal widths immediately.
        // After clearing, ActualWidth already reflects the current
        // available space (SizeChanged fires after layout).
        double itemWidth = Math.Max(0, Math.Floor(segmented.ActualWidth / visibleItems.Count));
        foreach (var item in visibleItems)
        {
            item.Width = itemWidth;
            item.MaxWidth = itemWidth;
            item.MinWidth = 0;
            item.Padding = new Thickness(4, 1, 4, 2);
            item.MinHeight = Math.Max(24, segmented.MinHeight - 3);
        }

        // Step 3: Schedule a follow-up pass on the next frame.
        // When the widget shrinks, the SizeChanged event may fire with
        // an ActualWidth that was still influenced by the old item widths.
        // Re-running on the next dispatcher tick ensures we get the true
        // final width after the parent has settled.
        var width = segmented.ActualWidth;
        var count = visibleItems.Count;
        s_dispatcher?.TryEnqueue(() =>
        {
            if (segmented.ActualWidth <= 0 || count == 0)
            {
                return;
            }

            // Guard: if the width hasn't changed since we applied, skip.
            if (Math.Abs(segmented.ActualWidth - width) < 0.5)
            {
                return;
            }

            double newWidth = Math.Max(0, Math.Floor(segmented.ActualWidth / count));
            foreach (var item in segmented.Items.OfType<SegmentedItem>()
                         .Where(i => i.Visibility == Visibility.Visible))
            {
                item.Width = newWidth;
                item.MaxWidth = newWidth;
            }
        });
    }
}
