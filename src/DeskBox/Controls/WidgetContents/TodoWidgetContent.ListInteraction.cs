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
    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isDisposed ||
            ViewModel is null ||
            DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        bool isCtrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool isAlt = Win32Helper.IsKeyPressed(VirtualKey.Menu);
        bool isShift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        bool isInsideTextInput = TransientInputFocusGuard.IsInsideTextInputControl(
            e.OriginalSource as DependencyObject);

        if (isAlt && !isCtrl && !isShift && e.Key == VirtualKey.Left &&
            !_isDualPane && ViewModel.IsDetailPageOpen)
        {
            e.Handled = true;
            await CloseDetailAsync();
            return;
        }

        if (isCtrl && !isAlt && !isShift && e.Key == VirtualKey.N)
        {
            e.Handled = true;
            FocusQuickAdd();
            return;
        }

        if (isCtrl && !isAlt && !isShift && e.Key == VirtualKey.F &&
            (_isDualPane || !ViewModel.IsDetailPageOpen))
        {
            e.Handled = true;
            OpenSearchBar();
            return;
        }

        if (isInsideTextInput)
        {
            return;
        }

        if (isCtrl && !isAlt && !isShift && e.Key == VirtualKey.Z)
        {
            if (ViewModel.CanUndoLastAction)
            {
                e.Handled = true;
                await ViewModel.UndoLastActionAsync();
                ShowUndoToast(
                    App.Current.LocalizationService.T("Common.Undone"),
                    clearUndoOnHide: false);
            }
            return;
        }

        if (isCtrl && !isAlt && !isShift && e.Key == VirtualKey.V)
        {
            e.Handled = true;
            await PasteIntoTodoFromClipboardAsync();
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isDisposed || ViewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid);
        if (point.Properties.IsXButton1Pressed &&
            !_isDualPane &&
            ViewModel.IsDetailPageOpen)
        {
            e.Handled = true;
            _ = CloseDetailAsync();
        }
    }

    private IReadOnlyList<TodoItemViewModel> GetKeyboardTargetTodoItems()
    {
        IReadOnlyList<TodoItemViewModel> selected = GetSelectedCopyItemsInVisibleOrder();
        if (selected.Count > 0)
        {
            return selected;
        }

        if (ViewModel?.SelectedDetailItem is { } detail &&
            ViewModel.VisibleItems.Contains(detail))
        {
            return [detail];
        }

        return [];
    }

    private void SelectSingleTodoItemForKeyboard(TodoItemViewModel item)
    {
        ClearCopySelection();
        item.IsCopySelected = true;
        _copySelectionAnchorId = item.Id;
        TodoListView?.ScrollIntoView(item);
        if (_isDualPane)
        {
            MarkDetailSelectionExplicit();
            _ = OpenDetailItemAsync(item.Id);
        }

        ApplyHostFocusSelectionVisuals();
    }

    private async void TodoListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e) ||
            TransientInputFocusGuard.IsInsideTextInputControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        bool isCtrlPressed = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool isShiftPressed = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        if (isCtrlPressed && e.Key == VirtualKey.A)
        {
            SelectAllVisibleTodoItems();
            ApplyHostFocusSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (isCtrlPressed && e.Key == VirtualKey.C)
        {
            CopySelectedTodoItems();
            e.Handled = true;
            return;
        }

        if (!isCtrlPressed && (e.Key == VirtualKey.Down || e.Key == VirtualKey.Up) &&
            ViewModel is { VisibleItems.Count: > 0 })
        {
            int count = ViewModel.VisibleItems.Count;
            int currentIndex = -1;
            IReadOnlyList<TodoItemViewModel> currentTargets = GetKeyboardTargetTodoItems();
            if (currentTargets.Count > 0)
            {
                TodoItemViewModel reference = e.Key == VirtualKey.Down
                    ? currentTargets[^1]
                    : currentTargets[0];
                currentIndex = ViewModel.VisibleItems.IndexOf(reference);
            }

            int nextIndex = currentIndex < 0
                ? (e.Key == VirtualKey.Down ? 0 : count - 1)
                : Math.Clamp(currentIndex + (e.Key == VirtualKey.Down ? 1 : -1), 0, count - 1);
            TodoItemViewModel nextItem = ViewModel.VisibleItems[nextIndex];
            if (isShiftPressed)
            {
                SelectTodoRange(nextItem);
                TodoListView.ScrollIntoView(nextItem);
                ApplyHostFocusSelectionVisuals();
            }
            else
            {
                SelectSingleTodoItemForKeyboard(nextItem);
            }

            e.Handled = true;
            return;
        }

        if (!isCtrlPressed && e.Key == VirtualKey.Space)
        {
            IReadOnlyList<TodoItemViewModel> targets = GetKeyboardTargetTodoItems();
            if (targets.Count > 0 && ViewModel is not null)
            {
                e.Handled = true;
                if (targets.Count == 1)
                {
                    TodoItemViewModel single = targets[0];
                    bool updated = await SetCompletedWithFeedbackAsync(single, !single.IsCompleted);
                    if (updated)
                    {
                        SynchronizeTodoCompletionCheckBox(single);
                    }
                }
                else
                {
                    bool markCompleted = targets.Any(t => !t.IsCompleted);
                    string[] ids = targets.Select(t => t.Id).ToArray();
                    int changed = await ViewModel.SetCompletedBatchAsync(ids, markCompleted);
                    if (changed > 0 && !markCompleted)
                    {
                        ShowTodoStatus("Todo.Status.MarkedActive");
                    }

                    foreach (TodoItemViewModel target in targets)
                    {
                        SynchronizeTodoCompletionCheckBox(target);
                    }
                }
                return;
            }
        }

        if (!isCtrlPressed && e.Key == VirtualKey.Enter)
        {
            IReadOnlyList<TodoItemViewModel> targets = GetKeyboardTargetTodoItems();
            if (targets.Count > 0)
            {
                e.Handled = true;
                TodoItemViewModel target = targets[0];
                MarkDetailSelectionExplicit();
                if (await OpenDetailItemAsync(target.Id) is not null && _isDualPane)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (DetailTitleTextBox is not null)
                        {
                            DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-detail-title");
                            DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(DetailTitleTextBox);
                            DetailTitleTextBox.Focus(FocusState.Programmatic);
                            int caret = DetailTitleTextBox.Text?.Length ?? 0;
                            DetailTitleTextBox.Select(caret, 0);
                        }
                    });
                }
                return;
            }
        }

        if (!isCtrlPressed && e.Key == VirtualKey.F2)
        {
            IReadOnlyList<TodoItemViewModel> targets = GetKeyboardTargetTodoItems();
            if (targets.Count > 0)
            {
                e.Handled = true;
                OpenTodoInlineEdit(targets[0]);
                return;
            }
        }

        if (e.Key == VirtualKey.Delete)
        {
            IReadOnlyList<TodoItemViewModel> selectedItems = GetKeyboardTargetTodoItems();
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
            return;
        }

        if (e.Key == VirtualKey.Escape && ViewModel?.IsSearchOpen == true)
        {
            CloseSearchBar(focusList: true);
            e.Handled = true;
        }
    }

    private async void TodoWidgetContent_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape ||
            ViewModel is null ||
            DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (CustomDueDateOverlay is { Visibility: Visibility.Visible })
        {
            CloseCustomDueDateOverlay();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsDetailPageOpen && !_isDualPane)
        {
            await CloseDetailAsync();
            e.Handled = true;
            return;
        }

        if (ViewModel.IsSearchOpen)
        {
            CloseSearchBar(focusList: true);
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
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-list-background");
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

        bool isCtrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool isShift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-list-select");
        ClearTodoListContainerSelection();

        if (isShift)
        {
            SelectTodoRange(item);
            TodoListView.Focus(FocusState.Programmatic);
            ApplyHostFocusSelectionVisuals();
            return;
        }

        if (isCtrl)
        {
            ToggleTodoSelection(item);
            TodoListView.Focus(FocusState.Programmatic);
            ApplyHostFocusSelectionVisuals();
            return;
        }

        ClearCopySelection();
        _copySelectionAnchorId = item.Id;
        if (_isDualPane)
        {
            item.IsCopySelected = true;
        }

        MarkDetailSelectionExplicit();
        if (await OpenDetailItemAsync(item.Id) is null)
        {
            return;
        }

        if (_isDualPane)
        {
            TodoListView.Focus(FocusState.Programmatic);
        }

        ApplyHostFocusSelectionVisuals();
    }

    private void TodoListView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not DependencyObject container)
        {
            return;
        }

        Border? rowRoot = FindVisualChild<Border>(container, "TodoItemRoot");
        UIElement? animationTarget = (UIElement?)rowRoot ?? args.ItemContainer;
        if (args.InRecycleQueue)
        {
            if (animationTarget is not null)
            {
                ElementCompositionPreview.GetElementVisual(animationTarget).Opacity = 1f;
            }
            return;
        }

        ApplyHostFocusSelectionVisualToRow(container);

        if (args.Item is not TodoItemViewModel vm ||
            ViewModel is null ||
            !string.Equals(ViewModel.LastSingleCreatedItemId, vm.Id, StringComparison.Ordinal))
        {
            if (animationTarget is not null)
            {
                ElementCompositionPreview.GetElementVisual(animationTarget).Opacity = 1f;
            }
            return;
        }

        ViewModel.ClearLastSingleCreatedItemId(vm.Id);
        if (!IsLoaded || !WindowsCompatibilityService.ShouldAnimate || animationTarget is null)
        {
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(animationTarget, true);
            var visual = ElementCompositionPreview.GetElementVisual(animationTarget);
            var compositor = visual.Compositor;
            TimeSpan duration = TimeSpan.FromMilliseconds(140);

            var easeOut = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1f),
                new Vector2(0.3f, 1f));

            var translationAnim = compositor.CreateVector3KeyFrameAnimation();
            translationAnim.InsertKeyFrame(0f, new Vector3(0f, -6f, 0f));
            translationAnim.InsertKeyFrame(1f, Vector3.Zero, easeOut);
            translationAnim.Duration = duration;

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, 0f);
            opacityAnim.InsertKeyFrame(1f, 1f, easeOut);
            opacityAnim.Duration = duration;

            visual.StartAnimation("Translation", translationAnim);
            visual.StartAnimation("Opacity", opacityAnim);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Todo] Row entrance animation skipped: {ex.Message}");
        }
    }

    private void TodoStepExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoItemViewModel item })
        {
            item.IsStepsExpanded = !item.IsStepsExpanded;
        }
    }

    private async void InlineStepCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not CheckBox { DataContext: TodoStepViewModel step } checkBox ||
            FindTodoItemContainer(checkBox)?.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        bool updated = await ViewModel.SetStepCompletedAsync(
            item.Id,
            step.Id,
            checkBox.IsChecked == true);
        if (!updated && ReferenceEquals(checkBox.DataContext, step))
        {
            checkBox.IsChecked = step.IsCompleted;
        }
    }

    private async void TodoOpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoItemViewModel item } ||
            string.IsNullOrWhiteSpace(item.PrimaryUrl) ||
            !Uri.TryCreate(item.PrimaryUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to launch URL '{uri}': {ex.Message}");
        }
    }

    private void TodoQuickDueDateItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TodoItemViewModel item } button ||
            ViewModel is null)
        {
            return;
        }

        MenuFlyout flyout = CreateDueDateFlyout(item);
        flyout.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    private void TodoItemTitle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isDualPane ||
            sender is not FrameworkElement { DataContext: TodoItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        OpenTodoInlineEdit(item);
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
