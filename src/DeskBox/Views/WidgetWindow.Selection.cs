// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// Partial class containing item selection, box-selection (rubber band),
/// hit-testing, and visual tree helper logic for WidgetWindow.
/// </summary>
public sealed partial class WidgetWindow
{
    private sealed record SelectionHitTestItem(
        WidgetItem Item,
        Border? Surface,
        Windows.Foundation.Rect Bounds);

    private int _lastSelectionAnchorIndex = -1;
    private bool _selectionPointerPressed;
    private bool _isBoxSelecting;
    private Windows.Foundation.Point _selectionStartPoint;
    private Windows.Foundation.Point _selectionCurrentPoint;
    private List<WidgetItem> _selectionSnapshot = [];
    private HashSet<WidgetItem> _selectionPreviewItems = [];
    private List<SelectionHitTestItem> _selectionHitTestItems = [];
    private Dictionary<WidgetItem, Border> _selectionSurfaceByItem = [];
    private bool _isSynchronizingSelection;

    // ── Selection accessors ────────────────────────────────────

    private ListViewBase? GetActiveItemsView()
    {
        if (ViewModel.IsIconMode)
        {
            return ItemsGridView;
        }

        if (ViewModel.IsListMode)
        {
            return ItemsListView;
        }

        return null;
    }

    private List<WidgetItem> GetSelectedItems()
    {
        return GetActiveItemsView()?.SelectedItems
            .OfType<WidgetItem>()
            .Where(item => item is not WidgetStackItem)
            .ToList() ?? [];
    }

    private WidgetItem? GetPrimarySelectedItem()
    {
        return GetSelectedItems().FirstOrDefault();
    }

    public void ClearItemSelection()
    {
        ClearItemSelectionCore(clearCutState: false);
    }

    private void ClearOtherWidgetSelections()
    {
        App.Current.WidgetManager?.ClearSelectionsExcept(ViewModel.Config.Id);
    }

    // ── Selection state ────────────────────────────────────────

    private void ApplySelectionState(ListViewBase? listView)
    {
        var selectedItems = listView?.SelectedItems.OfType<WidgetItem>().ToHashSet() ?? [];
        foreach (var item in ViewModel.Items)
        {
            item.IsSelected = selectedItems.Contains(item);
        }

        ApplyCutState();
        UpdateInteractiveSurfaces();
    }

    private void ApplySelectionPreview(HashSet<WidgetItem> selectedItems)
    {
        foreach (var item in _selectionPreviewItems)
        {
            if (!selectedItems.Contains(item))
            {
                SetItemSelectionPreview(item, false);
            }
        }

        foreach (var item in selectedItems)
        {
            if (!_selectionPreviewItems.Contains(item))
            {
                SetItemSelectionPreview(item, true);
            }
        }

        _selectionPreviewItems = selectedItems;
    }

    private void SetItemSelectionPreview(WidgetItem item, bool isSelected)
    {
        if (item.IsSelected == isSelected)
        {
            return;
        }

        item.IsSelected = isSelected;
        if (_selectionSurfaceByItem.TryGetValue(item, out var surface))
        {
            ApplyWidgetItemSurfaceState(surface, ItemSurfaceState.Normal);
            return;
        }

        if (FindItemSurface(item) is Border border)
        {
            ApplyWidgetItemSurfaceState(border, ItemSurfaceState.Normal);
        }
    }

    private void SynchronizeListViewSelection(ListViewBase listView, IEnumerable<WidgetItem> selectedItems)
    {
        _isSynchronizingSelection = true;
        try
        {
            listView.SelectedItems.Clear();
            foreach (var item in selectedItems)
            {
                listView.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private void SelectSingleItem(WidgetItem item)
    {
        var listView = GetActiveItemsView();
        if (listView is null)
        {
            return;
        }

        ClearOtherWidgetSelections();
        listView.SelectedItems.Clear();
        listView.SelectedItems.Add(item);
        listView.ScrollIntoView(item);
        ApplySelectionState(listView);
    }

    // ── Selection change event ─────────────────────────────────

    private void ItemsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            if (_isSynchronizingSelection)
            {
                return;
            }

            if (e.AddedItems.Count > 0)
            {
                ClearOtherWidgetSelections();
            }

            ApplySelectionState(listView);
            _lastSelectionAnchorIndex = GetPrimarySelectedItem() is { } item
                ? ViewModel.Items.IndexOf(item)
                : -1;
        }
    }

    // ── Box selection (rubber band) ────────────────────────────

    private async void ItemsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        bool canStartBoxSelection = CanStartBoxSelection(e.OriginalSource);
        if (properties.IsLeftButtonPressed &&
            canStartBoxSelection &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) &&
            GetExpandedStack() is { } expandedStack)
        {
            RootGrid.Focus(FocusState.Programmatic);
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
            e.Handled = true;
            await SetStackExpandedWithAnimationAsync(expandedStack, expanded: false);
            return;
        }

        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) ||
            !canStartBoxSelection)
        {
            return;
        }

        RootGrid.Focus(FocusState.Programmatic);
        ClearOtherWidgetSelections();
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control)
            ? GetSelectedItems()
            : [];
        _selectionPreviewItems = new HashSet<WidgetItem>(_selectionSnapshot);
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];

        if (!Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            listView.SelectedItems.Clear();
            ApplySelectionState(listView);
        }
    }

    private void ItemsView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_isBoxSelecting && GetSelectionDragDistance(_selectionStartPoint, _selectionCurrentPoint) < 6.0)
        {
            return;
        }

        if (!_isBoxSelecting)
        {
            _isBoxSelecting = true;
            listView.CapturePointer(e.Pointer);
            if (_selectionHitTestItems.Count == 0)
            {
                CacheSelectionHitTestItems(listView);
            }
        }

        UpdateSelectionRectangleVisual();
        ApplySelectionRectanglePreview();
        e.Handled = true;
    }

    private void ItemsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        FinishSelectionRectangle(listView);
        if (_isBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        if (!_settingsService.Settings.DoubleClickToOpen &&
            !_isBoxSelecting &&
            properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased &&
            e.OriginalSource is FrameworkElement element &&
            element.DataContext is WidgetItem item &&
            item is not WidgetStackItem &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            OpenItem(item);
        }
    }

    private void ItemsView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishSelectionRectangle(listView);
        }
    }

    // ── Box selection helpers ──────────────────────────────────

    private bool CanStartBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return !IsWithinInteractiveSurface(source) &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(SelectionRectangle, selectionRect.X);
        Canvas.SetTop(SelectionRectangle, selectionRect.Y);
        SelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        SelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        SelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CacheSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];

        foreach (var item in ViewModel.Items)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindItemSurface(item) ?? container;
            var topLeft = target.TransformToVisual(SelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            var surface = target as Border;

            _selectionHitTestItems.Add(new SelectionHitTestItem(item, surface, bounds));
            if (surface is not null)
            {
                _selectionSurfaceByItem[item] = surface;
            }
        }
    }

    private void ApplySelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<WidgetItem>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplySelectionPreview(selectedItems);
    }

    private void FinishSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplySelectionRectanglePreview();
            SynchronizeListViewSelection(
                listView,
                ViewModel.Items.Where(item => _selectionPreviewItems.Contains(item)));
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        _selectionSurfaceByItem = [];
        SelectionRectangle.Visibility = Visibility.Collapsed;
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            ApplySelectionState(listView);
        }
    }

    private static Windows.Foundation.Rect GetSelectionRect(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double x = Math.Min(startPoint.X, endPoint.X);
        double y = Math.Min(startPoint.Y, endPoint.Y);
        double width = Math.Abs(endPoint.X - startPoint.X);
        double height = Math.Abs(endPoint.Y - startPoint.Y);
        return new Windows.Foundation.Rect(x, y, width, height);
    }

    private static double GetSelectionDragDistance(
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Point endPoint)
    {
        double deltaX = endPoint.X - startPoint.X;
        double deltaY = endPoint.Y - startPoint.Y;
        return Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
    }

    private static bool RectsIntersect(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        return first.X < second.X + second.Width &&
               first.X + first.Width > second.X &&
               first.Y < second.Y + second.Height &&
               first.Y + first.Height > second.Y;
    }

    // ── Item surface lookup ────────────────────────────────────

    private FrameworkElement? FindItemSurface(WidgetItem item)
    {
        if (GetActiveItemsView()?.ContainerFromItem(item) is not SelectorItem container)
        {
            return null;
        }

        if (TryGetDescendant<Border>(container, out var border) && border.Tag as string == "InteractiveSurface")
        {
            return border;
        }

        return container;
    }

    // ── Visual tree helpers ────────────────────────────────────

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
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

    private static bool IsWithinInteractiveSurface(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Border border &&
                border.Tag as string is "InteractiveSurface" or "StackSurface")
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
