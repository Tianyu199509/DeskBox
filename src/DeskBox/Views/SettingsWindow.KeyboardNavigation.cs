using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Region keyboard navigation for the settings window. WinUI 3 desktop has
/// no built-in F6/FocusScope cycling, so F6 rotates focus between the three
/// visual regions (title bar with the search box, navigation pane, content
/// area), and Ctrl+F jumps straight to the settings search box — the search
/// box is the primary navigation path across the 3800-line settings surface.
/// Regions whose first focusable element cannot be found (collapsed content,
/// empty pane) are skipped rather than dropping the keystroke.
/// </summary>
public sealed partial class SettingsWindow
{
    private void SearchFocusAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SettingsSearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void RegionCycleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        bool backward = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        CycleFocusRegions(backward);
        args.Handled = true;
    }

    private void CycleFocusRegions(bool backward)
    {
        UIElement[] regions = [AppTitleBar, SettingsNavigationView, SettingsContentRoot];

        int currentIndex = FindFocusRegionIndex(regions);
        if (currentIndex < 0)
        {
            if (FocusManager.GetFocusedElement() is not null)
            {
                // Focus sits outside the tracked regions — a modal dialog,
                // flyout or popup. Moving focus here would land underneath the
                // modal surface (and Enter could activate shadowed controls),
                // so the keystroke is swallowed instead.
                return;
            }

            // No focused element at all (fresh activation): start from the
            // first region.
            currentIndex = 0;
            backward = true;
        }

        for (int step = 1; step <= regions.Length; step++)
        {
            int next = currentIndex + (backward ? -step : step);
            next = ((next % regions.Length) + regions.Length) % regions.Length;
            if (TryFocusRegion(regions[next]))
            {
                return;
            }
        }
    }

    private static int FindFocusRegionIndex(UIElement[] regions)
    {
        if (FocusManager.GetFocusedElement() is not DependencyObject focused)
        {
            return -1;
        }

        while (focused is not null)
        {
            for (int i = 0; i < regions.Length; i++)
            {
                if (ReferenceEquals(focused, regions[i]))
                {
                    return i;
                }
            }
            focused = VisualTreeHelper.GetParent(focused);
        }

        return -1;
    }

    private static bool TryFocusRegion(UIElement region)
    {
        if (region.Visibility != Visibility.Visible)
        {
            return false;
        }
        if (region.Focus(FocusState.Programmatic))
        {
            return true;
        }

        // The region root itself is usually not focusable (Grid/NavigationView
        // content area); breadth-first walk for the first focusable descendant.
        // Focus() returns false for collapsed or disabled elements, so the walk
        // naturally skips them.
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(region);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is UIElement childElement &&
                    childElement.Visibility == Visibility.Visible &&
                    childElement.Focus(FocusState.Programmatic))
                {
                    return true;
                }
                queue.Enqueue(child);
            }
        }

        return false;
    }
}
