using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

public readonly record struct ReorderInsertionIndicatorPlacement(
    Windows.Foundation.Rect Bounds,
    bool IsVertical);

/// <summary>
/// Computes a reorder insertion point from realized list/grid containers.
/// Virtualized controls do not create containers for items outside the
/// viewport, so treating a missing container as the end of the collection
/// makes a cross-page drag jump to the last item. This helper only uses the
/// realized range and preserves the previous valid point while the control is
/// between virtualization/layout passes.
/// </summary>
public static class ReorderDropIndexCalculator
{
    /// <summary>
    /// Locates a visual-only insertion marker between the neighboring
    /// realized containers. The marker is deliberately positioned on a
    /// separate overlay rather than inserted into the bound collection, so
    /// virtualization and scroll anchoring remain untouched while the
    /// pointer moves.
    /// </summary>
    public static bool TryGetInsertionIndicatorPlacement(
        ListViewBase list,
        UIElement overlay,
        int insertionIndex,
        Windows.Foundation.Point pointer,
        out ReorderInsertionIndicatorPlacement placement)
    {
        placement = default;
        if (list.Items.Count == 0)
        {
            return false;
        }

        int clampedIndex = Math.Clamp(
            insertionIndex,
            0,
            list.Items.Count);

        FrameworkElement? next = clampedIndex < list.Items.Count
            ? list.ContainerFromIndex(clampedIndex) as FrameworkElement
            : null;
        FrameworkElement? previous = clampedIndex > 0
            ? list.ContainerFromIndex(clampedIndex - 1) as FrameworkElement
            : null;
        FrameworkElement? reference = next ?? previous ??
            FindNearestRealizedContainer(list, clampedIndex);

        // An insertion point beyond the realized viewport has no container.
        // Use a nearby realized item only to obtain the marker size, then
        // anchor the marker to the current pointer position. As auto-scroll
        // brings the target into view, the next DragOver will snap it between
        // the neighboring containers.
        if (reference is null ||
            reference.ActualWidth <= 0 ||
            reference.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point pointerInOverlay =
                list.TransformToVisual(overlay).TransformPoint(pointer);

            bool isGrid = list is GridView;
            bool hasNextBounds = TryGetContainerBounds(
                next,
                overlay,
                out Windows.Foundation.Rect nextBounds);
            bool hasPreviousBounds = TryGetContainerBounds(
                previous,
                overlay,
                out Windows.Foundation.Rect previousBounds);

            const double glowThickness = 10;
            if (isGrid)
            {
                double x;
                double top;
                double height;
                if (hasNextBounds)
                {
                    bool sameRow = hasPreviousBounds &&
                        Math.Abs(previousBounds.Top - nextBounds.Top) < 1;
                    x = sameRow
                        ? (previousBounds.Right + nextBounds.Left) / 2
                        : nextBounds.Left - (glowThickness / 2);
                    top = nextBounds.Top;
                    height = nextBounds.Height;
                }
                else if (hasPreviousBounds)
                {
                    x = previousBounds.Right + (glowThickness / 2);
                    top = previousBounds.Top;
                    height = previousBounds.Height;
                }
                else
                {
                    x = pointerInOverlay.X;
                    top = pointerInOverlay.Y - (reference.ActualHeight / 2);
                    height = reference.ActualHeight;
                }

                placement = new ReorderInsertionIndicatorPlacement(
                    new Windows.Foundation.Rect(
                        x - (glowThickness / 2),
                        top,
                        glowThickness,
                        Math.Max(2, height)),
                    IsVertical: true);
                return true;
            }

            double left;
            double width;
            double y;
            if (hasNextBounds)
            {
                y = hasPreviousBounds
                    ? (previousBounds.Bottom + nextBounds.Top) / 2
                    : nextBounds.Top - (glowThickness / 2);
                left = nextBounds.Left;
                width = nextBounds.Width;
            }
            else if (hasPreviousBounds)
            {
                y = previousBounds.Bottom + (glowThickness / 2);
                left = previousBounds.Left;
                width = previousBounds.Width;
            }
            else
            {
                y = pointerInOverlay.Y;
                left = pointerInOverlay.X - (reference.ActualWidth / 2);
                width = reference.ActualWidth;
            }

            placement = new ReorderInsertionIndicatorPlacement(
                new Windows.Foundation.Rect(
                    left,
                    y - (glowThickness / 2),
                    Math.Max(2, width),
                    glowThickness),
                IsVertical: false);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetContainerBounds(
        FrameworkElement? container,
        UIElement overlay,
        out Windows.Foundation.Rect bounds)
    {
        bounds = default;
        if (container is null ||
            container.ActualWidth <= 0 ||
            container.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            bounds = container.TransformToVisual(overlay).TransformBounds(
                new Windows.Foundation.Rect(
                    0,
                    0,
                    container.ActualWidth,
                    container.ActualHeight));
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static int Compute(
        ListViewBase list,
        Windows.Foundation.Point position,
        int lastValidInsertionIndex = -1)
    {
        if (list.Items.Count == 0)
        {
            return 0;
        }

        bool isGrid = list is GridView;
        int firstVisibleIndex = int.MaxValue;
        int lastVisibleIndex = -1;
        int candidate = -1;
        GetRealizedIndexWindow(list, out int startIndex, out int endIndex);

        for (int index = startIndex; index <= endIndex; index++)
        {
            if (list.ContainerFromIndex(index) is not FrameworkElement container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            firstVisibleIndex = Math.Min(firstVisibleIndex, index);
            lastVisibleIndex = Math.Max(lastVisibleIndex, index);

            Windows.Foundation.Rect bounds;
            try
            {
                bounds = container.TransformToVisual(list).TransformBounds(
                    new Windows.Foundation.Rect(
                        0,
                        0,
                        container.ActualWidth,
                        container.ActualHeight));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                continue;
            }

            if (isGrid)
            {
                bool aboveRow = position.Y < bounds.Top;
                bool sameRow = position.Y >= bounds.Top && position.Y < bounds.Bottom;
                bool leftOfCenter = position.X < bounds.X + (bounds.Width / 2);
                if (aboveRow || (sameRow && leftOfCenter))
                {
                    candidate = index;
                    break;
                }
            }
            else if (position.Y < bounds.Top + (bounds.Height / 2))
            {
                candidate = index;
                break;
            }
        }

        if (candidate >= 0)
        {
            return Math.Clamp(candidate, 0, list.Items.Count);
        }

        if (lastVisibleIndex >= 0)
        {
            // The pointer is after the realized viewport. Insert after the
            // last realized item, not after the entire virtualized collection.
            return Math.Clamp(lastVisibleIndex + 1, 0, list.Items.Count);
        }

        if (lastValidInsertionIndex >= 0)
        {
            return Math.Clamp(lastValidInsertionIndex, 0, list.Items.Count);
        }

        return firstVisibleIndex == int.MaxValue
            ? 0
            : Math.Clamp(firstVisibleIndex, 0, list.Items.Count);
    }

    /// <summary>
    /// Returns whether the pointer is inside a realized item container. An
    /// external drop should not advertise an insertion point over an empty
    /// grid cell or the trailing blank area after the final item.
    /// </summary>
    public static bool IsPointerOverRealizedItem(
        ListViewBase list,
        Windows.Foundation.Point position)
    {
        // Walk the realized panel children instead of probing every item
        // through ContainerFromIndex. The latter is an O(n) virtualization
        // query on every native DragOver tick and is especially costly for a
        // large mapped folder.
        if (list.ItemsPanelRoot is not { } panel)
        {
            return false;
        }

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement container ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                Windows.Foundation.Rect bounds = container
                    .TransformToVisual(list)
                    .TransformBounds(
                        new Windows.Foundation.Rect(
                            0,
                            0,
                            container.ActualWidth,
                            container.ActualHeight));
                if (position.X >= bounds.Left &&
                    position.X < bounds.Right &&
                    position.Y >= bounds.Top &&
                    position.Y < bounds.Bottom)
                {
                    return true;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or InvalidOperationException)
            {
                // The item can be recycled between DragOver and layout. Treat
                // an unstable container as an invalid preview for this tick.
            }
        }

        return false;
    }

    private static void GetRealizedIndexWindow(
        ListViewBase list,
        out int startIndex,
        out int endIndex)
    {
        int count = list.Items.Count;
        if (count <= 0)
        {
            startIndex = 0;
            endIndex = -1;
            return;
        }

        (int panelFirst, int panelLast) = list.ItemsPanelRoot switch
        {
            ItemsWrapGrid wrap when wrap.FirstVisibleIndex >= 0 && wrap.LastVisibleIndex >= wrap.FirstVisibleIndex
                => (wrap.FirstVisibleIndex, wrap.LastVisibleIndex),
            ItemsStackPanel stack when stack.FirstVisibleIndex >= 0 && stack.LastVisibleIndex >= stack.FirstVisibleIndex
                => (stack.FirstVisibleIndex, stack.LastVisibleIndex),
            _ => (0, count - 1)
        };

        // Include one buffer index on each side when available for smooth boundary transitions.
        startIndex = Math.Max(0, panelFirst - 1);
        endIndex = Math.Min(count - 1, panelLast + 1);
    }

    private static FrameworkElement? FindNearestRealizedContainer(
        ListViewBase list,
        int insertionIndex)
    {
        GetRealizedIndexWindow(list, out int startIndex, out int endIndex);
        if (endIndex < startIndex)
        {
            return null;
        }

        int maxOffset = Math.Max(
            Math.Abs(insertionIndex - startIndex),
            Math.Abs(endIndex - insertionIndex)) + 1;
        for (int offset = 0; offset <= maxOffset; offset++)
        {
            int before = insertionIndex - 1 - offset;
            if (before >= startIndex &&
                before < list.Items.Count &&
                list.ContainerFromIndex(before) is FrameworkElement beforeContainer &&
                beforeContainer.ActualWidth > 0 &&
                beforeContainer.ActualHeight > 0)
            {
                return beforeContainer;
            }

            int after = insertionIndex + offset;
            if (after >= startIndex &&
                after <= endIndex &&
                list.ContainerFromIndex(after) is FrameworkElement container &&
                container.ActualWidth > 0 &&
                container.ActualHeight > 0)
            {
                return container;
            }
        }

        return null;
    }
}
