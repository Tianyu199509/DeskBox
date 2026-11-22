using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Region keyboard navigation for the settings window. WinUI 3 desktop has
/// no built-in F6/FocusScope cycling, so F6 rotates focus between the three
/// visual regions (search box / search button, navigation pane, content area),
/// Ctrl+F jumps straight to the settings search box (expanding a collapsed
/// pane when needed), and Alt+Left / GoBack pop nested settings sub-pages.
/// Regions whose first focusable element cannot be found (collapsed content,
/// hidden pane) are skipped rather than dropping the keystroke.
/// </summary>
public sealed partial class SettingsWindow
{
    private const int FocusRegionSearch = 0;
    private const int FocusRegionNavigation = 1;
    private const int FocusRegionContent = 2;
    private const int FocusRegionCount = 3;

    private bool HasOpenOverlayPopup()
    {
        if (SettingsRoot.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        IReadOnlyList<Popup> openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot);
        return openPopups.Count > 0;
    }

    private bool HasOpenModalDialogPopup()
    {
        if (SettingsRoot.XamlRoot is not { } xamlRoot)
        {
            return false;
        }

        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
        {
            if (popup.Child is ContentDialog)
            {
                return true;
            }
        }

        return false;
    }

    private void SearchFocusAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isClosed || HasOpenModalDialogPopup())
        {
            return;
        }

        if (!SettingsNavigationView.IsPaneOpen)
        {
            SettingsNavigationView.IsPaneOpen = true;
            SettingsNavigationView.UpdateLayout();
        }

        if (!SettingsSearchBox.Focus(FocusState.Keyboard))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed)
                {
                    SettingsSearchBox.Focus(FocusState.Keyboard);
                }
            });
        }

        args.Handled = true;
    }

    private void BackNavigationAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isClosed || HasOpenOverlayPopup())
        {
            return;
        }

        if (TryNavigateBack())
        {
            args.Handled = true;
        }
    }

    private void SettingsRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled ||
            e.Key != Windows.System.VirtualKey.GoBack ||
            _isClosed ||
            HasOpenOverlayPopup())
        {
            return;
        }

        if (TryNavigateBack())
        {
            e.Handled = true;
        }
    }

    private void RegionCycleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        bool backward = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift);
        CycleFocusRegions(backward);
        args.Handled = true;
    }

    private void CycleFocusRegions(bool backward)
    {
        int currentIndex = ResolveFocusedRegionIndex();
        if (currentIndex < 0)
        {
            object? focused = SettingsRoot.XamlRoot is { } xamlRoot
                ? FocusManager.GetFocusedElement(xamlRoot)
                : FocusManager.GetFocusedElement();
            if (focused is not null && !ReferenceEquals(focused, SettingsRoot))
            {
                // Focus sits outside the tracked regions — a modal dialog,
                // flyout or popup. Moving focus here would land underneath the
                // modal surface (and Enter could activate shadowed controls),
                // so the keystroke is swallowed instead.
                return;
            }

            // No focused element inside the regions (fresh activation): start
            // from the first region.
            currentIndex = FocusRegionSearch;
            backward = true;
        }

        for (int step = 1; step <= FocusRegionCount; step++)
        {
            int next = currentIndex + (backward ? -step : step);
            next = ((next % FocusRegionCount) + FocusRegionCount) % FocusRegionCount;
            if (TryFocusRegionByIndex(next))
            {
                return;
            }
        }
    }

    private int ResolveFocusedRegionIndex()
    {
        object? rawFocused = SettingsRoot.XamlRoot is { } xamlRoot
            ? FocusManager.GetFocusedElement(xamlRoot)
            : FocusManager.GetFocusedElement();
        if (rawFocused is not DependencyObject focused || ReferenceEquals(focused, SettingsRoot))
        {
            return -1;
        }

        DependencyObject? autoSuggestArea = FindDescendantByName(SettingsNavigationView, "AutoSuggestArea");
        DependencyObject? cursor = focused;
        while (cursor is not null)
        {
            if (ReferenceEquals(cursor, SettingsContentRoot))
            {
                return FocusRegionContent;
            }

            if (ReferenceEquals(cursor, SettingsSearchBox) ||
                (autoSuggestArea is not null && ReferenceEquals(cursor, autoSuggestArea)))
            {
                return FocusRegionSearch;
            }

            if (ReferenceEquals(cursor, SettingsNavigationView))
            {
                return FocusRegionNavigation;
            }

            cursor = VisualTreeHelper.GetParent(cursor);
        }

        return -1;
    }

    private bool TryFocusRegionByIndex(int regionIndex) => regionIndex switch
    {
        FocusRegionSearch => TryFocusSearchRegion(),
        FocusRegionNavigation => TryFocusNavigationPaneRegion(),
        FocusRegionContent => TryFocusRegion(SettingsContentRoot),
        _ => false
    };

    private bool TryFocusSearchRegion()
    {
        if (SettingsNavigationView.IsPaneOpen &&
            SettingsSearchBox.Visibility == Visibility.Visible &&
            SettingsSearchBox.Focus(FocusState.Programmatic))
        {
            return true;
        }

        if (FindDescendantByName(SettingsNavigationView, "PaneAutoSuggestButton") is Control searchButton &&
            searchButton.Visibility == Visibility.Visible &&
            searchButton.IsEnabled &&
            searchButton.Focus(FocusState.Programmatic))
        {
            return true;
        }

        return false;
    }

    private bool TryFocusNavigationPaneRegion()
    {
        if (SettingsNavigationView.IsPaneOpen ||
            SettingsNavigationView.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact)
        {
            if (SettingsNavigationView.SelectedItem is Control selectedItem &&
                selectedItem.Visibility == Visibility.Visible &&
                selectedItem.IsEnabled &&
                selectedItem.Focus(FocusState.Programmatic))
            {
                return true;
            }

            if (FindDescendantByName(SettingsNavigationView, "ItemsContainerGrid") is UIElement itemsGrid &&
                TryFocusRegion(itemsGrid))
            {
                return true;
            }
        }

        if (FindDescendantByName(SettingsNavigationView, "PaneToggleButtonGrid") is UIElement toggleGrid &&
            TryFocusRegion(toggleGrid))
        {
            return true;
        }

        return false;
    }

    private static DependencyObject? FindDescendantByName(DependencyObject root, string targetName)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            DependencyObject current = queue.Dequeue();
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is FrameworkElement fe &&
                    string.Equals(fe.Name, targetName, StringComparison.Ordinal))
                {
                    return fe;
                }

                queue.Enqueue(child);
            }
        }

        return null;
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
