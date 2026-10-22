using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

/// <summary>
/// Shared, allocation-free geometry and hit-test helpers for item selection.
/// The two hosts still own selection state and container synchronization.
/// </summary>
public static class FileItemSelectionGeometry
{
    public static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point start,
        Windows.Foundation.Point end)
    {
        return new Windows.Foundation.Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    public static double GetDragDistance(
        Windows.Foundation.Point start,
        Windows.Foundation.Point end)
    {
        double x = end.X - start.X;
        double y = end.Y - start.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    public static bool Intersects(
        Windows.Foundation.Rect first,
        Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    /// <summary>
    /// The list row's right-click primary zone: the union of the icon and
    /// label blocks, inflated by <paramref name="inflation"/> so thin rows
    /// stay clickable at their edges.
    /// </summary>
    public static Windows.Foundation.Rect BuildPrimaryContentZone(
        Windows.Foundation.Rect iconBounds,
        Windows.Foundation.Rect labelBounds,
        double inflation)
    {
        double left = Math.Min(iconBounds.X, labelBounds.X);
        double top = Math.Min(iconBounds.Y, labelBounds.Y);
        double right = Math.Max(
            iconBounds.X + iconBounds.Width,
            labelBounds.X + labelBounds.Width);
        double bottom = Math.Max(
            iconBounds.Y + iconBounds.Height,
            labelBounds.Y + labelBounds.Height);
        return new Windows.Foundation.Rect(
            left - inflation,
            top - inflation,
            right - left + inflation * 2,
            bottom - top + inflation * 2);
    }

    public static bool HasAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    public static bool IsWithinItemSurface(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                element.Tag as string is "InteractiveSurface" or "StackSurface")
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
