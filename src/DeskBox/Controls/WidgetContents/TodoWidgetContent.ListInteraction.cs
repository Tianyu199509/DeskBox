using System.ComponentModel;
using System.Numerics;
using DeskBox.Platform;
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
    private async void TodoListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        if (isCtrlPressed && e.Key == VirtualKey.A)
        {
            SelectAllVisibleTodoItems();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == VirtualKey.C)
        {
            CopySelectedTodoItems();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Delete)
        {
            IReadOnlyList<TodoItemViewModel> selectedItems =
                GetSelectedCopyItemsInVisibleOrder();
            if (selectedItems.Count > 0)
            {
                e.Handled = true;
                if (selectedItems.Count == 1)
                {
                    await DeleteItemAsync(selectedItems[0]);
                }
                else
                {
                    await DeleteSelectedItemsAsync(
                        selectedItems.Select(item => item.Id).ToArray());
                }
                return;
            }
        }

        if (e.Key == VirtualKey.Escape && HasCopySelection())
        {
            ClearCopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape &&
            ViewModel?.Items.Any(item => item.IsExpanded) == true)
        {
            ViewModel.CollapseAllExpanded();
            e.Handled = true;
        }
    }

    private async void TodoWidgetContent_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || ViewModel is null)
        {
            return;
        }

        if (CustomDueDateOverlay is { Visibility: Visibility.Visible })
        {
            CloseCustomDueDateOverlay();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsDetailPageOpen)
        {
            await CloseDetailAsync();
            e.Handled = true;
        }
    }

    private void TodoListView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        var properties = e.GetCurrentPoint(listView).Properties;
        // VisibleItemsSource is an AOT-safe object[] projection. Native
        // ListView reordering tries to mutate that fixed-size array, so row
        // drop handlers own reordering instead.
        listView.CanReorderItems = false;

        if (!properties.IsLeftButtonPressed ||
            Win32Helper.IsKeyPressed(VirtualKey.Shift) ||
            !CanStartTodoBoxSelection(e.OriginalSource))
        {
            if (CanStartTodoBoxSelection(e.OriginalSource))
            {
                ViewModel?.CollapseAllExpanded();
            }
            return;
        }

        _copyTapGeneration++;
        TodoListView.Focus(FocusState.Programmatic);
        _selectionPointerPressed = true;
        _isBoxSelecting = false;
        _selectionStartPoint = e.GetCurrentPoint(TodoSelectionOverlay).Position;
        _selectionCurrentPoint = _selectionStartPoint;
        _selectionSnapshot = Win32Helper.IsKeyPressed(VirtualKey.Control)
            ? GetSelectedCopyItemsInVisibleOrder().ToList()
            : [];
        _selectionPreviewItems = new HashSet<TodoItemViewModel>(_selectionSnapshot);
        _selectionHitTestItems = [];

        if (!Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            ClearCopySelection();
        }
    }

    private void TodoListView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView || !_selectionPointerPressed)
        {
            return;
        }

        _selectionCurrentPoint = e.GetCurrentPoint(TodoSelectionOverlay).Position;
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
                CacheTodoSelectionHitTestItems(listView);
            }
        }

        UpdateTodoSelectionRectangleVisual();
        ApplyTodoSelectionRectanglePreview();
        e.Handled = true;
    }

    private void TodoListView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase listView)
        {
            return;
        }

        bool wasBoxSelecting = _isBoxSelecting;
        FinishTodoSelectionRectangle(listView);
        if (wasBoxSelecting)
        {
            listView.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        ResetTodoReorderVisualState();
    }

    private void TodoListView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            FinishTodoSelectionRectangle(listView);
            ResetTodoReorderVisualState();
        }
    }

    private void ResetTodoReorderVisualState()
    {
        TodoListView.CanReorderItems = false;
        ClearTodoReorderDropState();
    }

    private void TodoItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, true);
    }

    private void TodoItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetTodoItemHoverState(sender as DependencyObject, false);
    }

    private void TodoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TodoItemViewModel item)
        {
            return;
        }

        if (e.PropertyName is not (nameof(TodoItemViewModel.IsCompleted) or nameof(TodoItemViewModel.ColorMarker)))
        {
            return;
        }

        foreach (var visibleItem in TodoListView.Items)
        {
            if (ReferenceEquals(visibleItem, item) &&
                TodoListView.ContainerFromItem(visibleItem) is FrameworkElement container)
            {
                if (e.PropertyName == nameof(TodoItemViewModel.IsCompleted))
                {
                    var parts = _rowVisuals.GetOrResolveParts(
                        container,
                        static root => new DeskBox.Controls.Todo.TodoRowVisualController.RowVisualParts(
                            FindVisualChild<Border>(root, "TodoItemHoverBackground"),
                            FindVisualChild<Border>(root, "TodoItemActionHost"),
                            FindVisualChild<CheckBox>(root, "TodoCompletionCheckBox")));
                    if ((parts.CompletionCheckBox ?? FindVisualChild<CheckBox>(container, "TodoCompletionCheckBox")) is { } checkBox)
                    {
                        checkBox.IsChecked = item.IsCompleted;
                    }
                }

                SetTodoItemHoverState(container, false);
                break;
            }
        }
    }

    private void SynchronizeTodoCompletionCheckBox(TodoItemViewModel item)
    {
        if (TodoListView.ContainerFromItem(item) is DependencyObject container)
        {
            var parts = _rowVisuals.GetOrResolveParts(
                container,
                static root => new DeskBox.Controls.Todo.TodoRowVisualController.RowVisualParts(
                    FindVisualChild<Border>(root, "TodoItemHoverBackground"),
                    FindVisualChild<Border>(root, "TodoItemActionHost"),
                    FindVisualChild<CheckBox>(root, "TodoCompletionCheckBox")));
            if ((parts.CompletionCheckBox ?? FindVisualChild<CheckBox>(container, "TodoCompletionCheckBox")) is { } checkBox)
            {
                checkBox.IsChecked = item.IsCompleted;
            }
        }
    }

    private async void TodoListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TodoItemViewModel item || ViewModel is null)
        {
            return;
        }

        ClearCopySelection();
        ClearTodoListContainerSelection();
        MarkDetailSelectionExplicit();
        if (await OpenDetailItemAsync(item.Id) is null)
        {
            return;
        }

    }

    private static bool IsInteractiveTodoSource(object? source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase or TextBox or CheckBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ClearTodoListContainerSelection()
    {
        TodoListView.SelectedItem = null;
        foreach (object visibleItem in TodoListView.Items)
        {
            if (TodoListView.ContainerFromItem(visibleItem) is SelectorItem container)
            {
                container.IsSelected = false;
            }
        }
    }
}
