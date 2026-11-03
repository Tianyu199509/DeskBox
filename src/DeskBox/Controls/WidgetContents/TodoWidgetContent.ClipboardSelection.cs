using System.ComponentModel;
using System.Numerics;
using DeskBox.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private void CopyTodoItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItemViewModel item })
        {
            CopyTodoItemText(item);
        }
    }

    private void CopyTodoItemText(TodoItemViewModel item)
    {
        string text = TodoClipboardFormatter.FormatSingle(item, App.Current.LocalizationService);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Copied"),
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy item {item.Id}: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.CopyFailed"),
                clearUndoOnHide: false);
        }
    }

    private void CopySelectedTodoItems()
    {
        CopySelectedTodoItems(GetSelectedCopyItemsInVisibleOrder());
    }

    private void CopySelectedTodoItems(IReadOnlyList<TodoItemViewModel> selectedItems)
    {
        if (selectedItems.Count == 0)
        {
            return;
        }

        if (selectedItems.Count == 1)
        {
            CopyTodoItemText(selectedItems[0]);
            return;
        }

        var localization = App.Current.LocalizationService;
        string text = TodoClipboardFormatter.FormatBatch(selectedItems, localization);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            SetClipboardText(text);
            ShowUndoToast(
                localization.Format("Todo.CopiedCount", selectedItems.Count),
                clearUndoOnHide: false);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to copy {selectedItems.Count} selected items: {ex}");
            ShowUndoToast(
                localization.T("Todo.CopyFailed"),
                clearUndoOnHide: false);
        }
    }

    private static void SetClipboardText(string text)
    {
        var dataPackage = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        dataPackage.SetText(text);
        DeskBoxClipboardWriteScope.MarkWrite(text: text);
        Clipboard.SetContent(dataPackage);
        try
        {
            // SetContent is the actual copy operation. Flush only transfers
            // ownership to the system so the content can outlive this process;
            // clipboard contention may make it throw even though the copy has
            // already succeeded, so it must not turn success feedback into a
            // false failure.
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Clipboard content was set but flush failed: {ex.Message}");
        }
    }

    private void ToggleTodoSelection(TodoItemViewModel item)
    {
        item.IsCopySelected = !item.IsCopySelected;
        _copySelectionAnchorId = item.Id;
    }

    private void SelectTodoRange(TodoItemViewModel item)
    {
        if (ViewModel is null || ViewModel.VisibleItems.Count == 0)
        {
            return;
        }

        int endIndex = ViewModel.VisibleItems.IndexOf(item);
        if (endIndex < 0)
        {
            return;
        }

        int startIndex = endIndex;
        if (!string.IsNullOrWhiteSpace(_copySelectionAnchorId))
        {
            for (int index = 0; index < ViewModel.VisibleItems.Count; index++)
            {
                if (string.Equals(ViewModel.VisibleItems[index].Id, _copySelectionAnchorId, StringComparison.Ordinal))
                {
                    startIndex = index;
                    break;
                }
            }
        }

        int first = Math.Min(startIndex, endIndex);
        int last = Math.Max(startIndex, endIndex);
        ClearCopySelection();
        for (int index = first; index <= last; index++)
        {
            ViewModel.VisibleItems[index].IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems[startIndex].Id;
    }

    private void SelectAllVisibleTodoItems()
    {
        if (ViewModel is null)
        {
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        foreach (var item in ViewModel.VisibleItems)
        {
            item.IsCopySelected = true;
        }

        _copySelectionAnchorId = ViewModel.VisibleItems.FirstOrDefault()?.Id;
    }

    private bool CanStartTodoBoxSelection(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        return FindTodoItemContainer(source) is null &&
               !HasAncestorOfType<ScrollBar>(source) &&
               !HasAncestorOfType<ButtonBase>(source) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private void UpdateTodoSelectionRectangleVisual()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        Canvas.SetLeft(TodoSelectionRectangle, selectionRect.X);
        Canvas.SetTop(TodoSelectionRectangle, selectionRect.Y);
        TodoSelectionRectangle.Width = Math.Max(0, selectionRect.Width);
        TodoSelectionRectangle.Height = Math.Max(0, selectionRect.Height);
        TodoSelectionRectangle.Visibility = selectionRect.Width > 0 && selectionRect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CacheTodoSelectionHitTestItems(ListViewBase listView)
    {
        _selectionHitTestItems = [];

        if (ViewModel is null)
        {
            return;
        }

        foreach (var item in ViewModel.VisibleItems)
        {
            if (listView.ContainerFromItem(item) is not SelectorItem container ||
                container.Visibility != Visibility.Visible ||
                container.ActualWidth <= 0 ||
                container.ActualHeight <= 0)
            {
                continue;
            }

            var target = FindVisualChild<Grid>(container, "TodoItemRoot") ?? container as FrameworkElement;
            if (target is null || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            {
                continue;
            }

            var topLeft = target.TransformToVisual(TodoSelectionOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var bounds = new Windows.Foundation.Rect(
                topLeft.X,
                topLeft.Y,
                target.ActualWidth,
                target.ActualHeight);
            _selectionHitTestItems.Add(new TodoSelectionHitTestItem(item, bounds));
        }
    }

    private void ApplyTodoSelectionRectanglePreview()
    {
        var selectionRect = GetSelectionRect(_selectionStartPoint, _selectionCurrentPoint);
        var selectedItems = new HashSet<TodoItemViewModel>(_selectionSnapshot);

        foreach (var hitTestItem in _selectionHitTestItems)
        {
            if (RectsIntersect(selectionRect, hitTestItem.Bounds))
            {
                selectedItems.Add(hitTestItem.Item);
            }
        }

        ApplyTodoSelectionPreview(selectedItems);
    }

    private void FinishTodoSelectionRectangle(ListViewBase listView)
    {
        bool shouldHandle = _selectionPointerPressed || _isBoxSelecting;
        _selectionPointerPressed = false;

        if (_isBoxSelecting)
        {
            ApplyTodoSelectionRectanglePreview();
            _copySelectionAnchorId = ViewModel?.VisibleItems.FirstOrDefault(item => item.IsCopySelected)?.Id;
        }

        _isBoxSelecting = false;
        _selectionSnapshot = [];
        _selectionPreviewItems = [];
        _selectionHitTestItems = [];
        TodoSelectionRectangle.Visibility = Visibility.Collapsed;
        TodoSelectionRectangle.Width = 0;
        TodoSelectionRectangle.Height = 0;

        if (shouldHandle)
        {
            listView.Focus(FocusState.Programmatic);
        }
    }

    private void ApplyTodoSelectionPreview(HashSet<TodoItemViewModel> selectedItems)
    {
        if (ViewModel is null)
        {
            return;
        }

        _selectionPreviewItems = selectedItems;
        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = selectedItems.Contains(item);
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

    private void ClearCopySelection()
    {
        if (ViewModel is null)
        {
            _copySelectionAnchorId = null;
            return;
        }

        foreach (var item in ViewModel.Items)
        {
            item.IsCopySelected = false;
        }

        _copySelectionAnchorId = null;
    }

    private bool HasCopySelection()
    {
        return ViewModel?.Items.Any(item => item.IsCopySelected) == true;
    }

    private IReadOnlyList<TodoItemViewModel> GetSelectedCopyItemsInVisibleOrder()
    {
        if (ViewModel is null)
        {
            return [];
        }

        return ViewModel.VisibleItems
            .Where(item => item.IsCopySelected)
            .ToList();
    }

    private Task PickCustomDueDateAsync(TodoItemViewModel? item)
    {
        if (ViewModel is null)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        if (!EnsureCustomDueDateOverlayRealized())
        {
            return Task.CompletedTask;
        }

        _customDueDateItem = item;
        _customDueDateItemIds = null;
        DateTimeOffset dueDate = item?.DueDate ?? ViewModel.DraftDueDate ?? GetDefaultCustomDueDate();
        bool includeTime = item is not null
            ? item.HasDueTime
            : ViewModel.DraftHasDueTime;
        if (CustomDueDatePicker is not null)
        {
            CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
            CustomDueDatePicker.Date = dueDate;
        }

        ConfigureCustomDueTimeState(dueDate, includeTime);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay!.Visibility = Visibility.Visible;
        CustomDueDatePicker?.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }
}
