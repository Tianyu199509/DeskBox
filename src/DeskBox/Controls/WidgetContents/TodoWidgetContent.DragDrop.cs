using System.ComponentModel;
using DeskBox.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private void RedColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Red);
    }

    private void OrangeColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Orange);
    }

    private void YellowColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Yellow);
    }

    private void GreenColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Green);
    }

    private void BlueColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Blue);
    }

    private void PurpleColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Purple);
    }

    private void TealColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Teal);
    }

    private void PinkColorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectColorFilter(TodoColorFilter.Pink);
    }

    private void ColorFilterButton_DragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string colorMarker } ||
            TodoItem.NormalizeColorMarker(colorMarker) is null)
        {
            e.Cancel = true;
            return;
        }

        // 颜色是进程内状态，走字段直传：OLE 自定义格式只能异步读（Drop 里
        // 无 deferral 的 await 曾致丢/挂），且拖过桌面时会被 shell 逐格式
        // 探测（0x8000FFFF 崩溃向量）。镜像 _draggedTodoItemId 先例。
        _draggedColorMarker = colorMarker;
        e.Data.RequestedOperation = DataPackageOperation.Link;
        e.Data.Properties.Title = App.Current.LocalizationService.T(
            TodoItem.GetColorMarkerLocalizationKey(colorMarker));
    }

    private void ColorFilterButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (DragDropPermissionService.IsCurrentProcessElevatedCached())
        {
            return;
        }

        if (sender is Button button &&
            e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _pressedColorFilterButton = button;
            _colorFilterDragStartPoint = e.GetCurrentPoint(RootGrid).Position;
        }
    }

    private PointerEventHandler? _colorFilterPointerPressedHandler;
    private PointerEventHandler? _colorFilterPointerMovedHandler;

    private void RegisterColorFilterHandledEvents()
    {
        if (_colorFilterHandledEventsRegistered)
        {
            return;
        }

        _colorFilterHandledEventsRegistered = true;
        _colorFilterPointerPressedHandler ??= new PointerEventHandler(ColorFilterButton_PointerPressed);
        _colorFilterPointerMovedHandler ??= new PointerEventHandler(ColorFilterButton_PointerMoved);
        foreach (Button button in new[]
                 {
                     RedColorFilterButton,
                     OrangeColorFilterButton,
                     YellowColorFilterButton,
                     GreenColorFilterButton,
                     BlueColorFilterButton,
                     PurpleColorFilterButton,
                     TealColorFilterButton,
                     PinkColorFilterButton
                 })
        {
            button.AddHandler(
                UIElement.PointerPressedEvent,
                _colorFilterPointerPressedHandler,
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerMovedEvent,
                _colorFilterPointerMovedHandler,
                handledEventsToo: true);
        }
    }

    private void UnregisterColorFilterHandledEvents()
    {
        if (!_colorFilterHandledEventsRegistered)
        {
            return;
        }

        _colorFilterHandledEventsRegistered = false;
        foreach (Button button in new[]
                 {
                     RedColorFilterButton,
                     OrangeColorFilterButton,
                     YellowColorFilterButton,
                     GreenColorFilterButton,
                     BlueColorFilterButton,
                     PurpleColorFilterButton,
                     TealColorFilterButton,
                     PinkColorFilterButton
                 })
        {
            if (_colorFilterPointerPressedHandler is not null)
            {
                button.RemoveHandler(
                    UIElement.PointerPressedEvent,
                    _colorFilterPointerPressedHandler);
            }

            if (_colorFilterPointerMovedHandler is not null)
            {
                button.RemoveHandler(
                    UIElement.PointerMovedEvent,
                    _colorFilterPointerMovedHandler);
            }
        }

        _colorFilterPointerPressedHandler = null;
        _colorFilterPointerMovedHandler = null;
    }

    private async void ColorFilterButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isStartingColorFilterDrag ||
            DragDropPermissionService.IsCurrentProcessElevatedCached() ||
            sender is not Button button ||
            !ReferenceEquals(button, _pressedColorFilterButton))
        {
            return;
        }

        var point = e.GetCurrentPoint(RootGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressedColorFilterButton = null;
            return;
        }

        double deltaX = point.Position.X - _colorFilterDragStartPoint.X;
        double deltaY = point.Position.Y - _colorFilterDragStartPoint.Y;
        if ((deltaX * deltaX) + (deltaY * deltaY) < 25)
        {
            return;
        }

        _isStartingColorFilterDrag = true;
        _suppressColorFilterClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
        e.Handled = true;
        // 常开日志：色点拖拽一旦在原生管线内炸掉会带走整个进程且无堆栈
        // （#528/#565 两次定案都丢了帧），起点和落点必须留在日志里。
        string dragColor = button.Tag as string ?? "unknown";
        App.Log($"[Todo] Color marker drag start color={dragColor}");
        try
        {
            DataPackageOperation outcome = await button.StartDragAsync(e.GetCurrentPoint(button));
            App.Log($"[Todo] Color marker drag completed color={dragColor} outcome={outcome}");
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to start color marker drag color={dragColor}: {ex.Message}");
        }
        finally
        {
            _suppressColorFilterClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(350);
            _pressedColorFilterButton = null;
            _isStartingColorFilterDrag = false;
            // StartDragAsync 返回即拖拽已终结（无论落点）：清掉进程内标记兜底。
            _draggedColorMarker = null;
        }
    }

    private void ColorFilterButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedColorFilterButton = null;
    }

    private void ColorFilterButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStartingColorFilterDrag)
        {
            _pressedColorFilterButton = null;
        }
    }

    private void TodoItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is Border
            {
                DataContext: TodoItemViewModel
            } reorderBorder &&
            !string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            bool insertAfter =
                e.GetPosition(reorderBorder).Y >= reorderBorder.ActualHeight / 2;
            e.Handled = true;
            e.AcceptedOperation =
                DeskBoxDragData.ResolveInternalMetadataOperation(
                    e.AllowedOperations);
            e.DragUIOverride.IsGlyphVisible = true;
            ApplyTodoReorderDropState(
                reorderBorder,
                active: true,
                insertAfter);
            return;
        }

        ClearTodoReorderDropState();

        if (_draggedColorMarker is not null)
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.IsGlyphVisible = true;
            SetTodoItemHoverState(sender as DependencyObject, true);
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            e.AcceptedOperation =
                DeskBoxDragData.GetFileAssociationOperation(
                    e.DataView,
                    e.AllowedOperations);
            ApplyFileAssociationDragFeedback(e);
            SetTodoItemHoverState(sender as DependencyObject, true);
        }
    }

    private void TodoItem_DragLeave(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId) &&
            sender is Border reorderBorder)
        {
            e.Handled = true;
            ApplyTodoReorderDropState(
                reorderBorder,
                active: false,
                insertAfter: false);
            return;
        }

        if (_draggedColorMarker is not null ||
            DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetTodoItemHoverState(sender as DependencyObject, false);
        }

        ResetTodoReorderVisualState();
    }

    private async void TodoItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: TodoItemViewModel item } border ||
            ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ResetTodoReorderVisualState();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            await DropTodoItemAtRowAsync(border, item, e);
            return;
        }

        if (_draggedColorMarker is { } dragColor)
        {
            e.Handled = true;
            SetTodoItemHoverState(border, false);
            // 同步取走标记再 yield：拖拽事务随时可能在首个 await 后终结。
            _draggedColorMarker = null;
            string? colorMarker = TodoItem.NormalizeColorMarker(dragColor);
            if (colorMarker is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ResetTodoReorderVisualState();
                return;
            }

            // 存盘要 await：deferral 保住拖拽事务与源侧结果；ResilientJsonStore
            // 只重试一种 IOException，其余存盘异常在 async void 里上抛即全局
            // 故障，catch 收敛为日志。
            var colorDeferral = e.GetDeferral();
            try
            {
                await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
                App.Log($"[Todo] Color marker drop applied color={colorMarker}");
                e.AcceptedOperation = DataPackageOperation.Link;
            }
            catch (Exception ex)
            {
                App.Log($"[Todo] Color marker drop failed color={colorMarker}: {ex.Message}");
                e.AcceptedOperation = DataPackageOperation.None;
            }
            finally
            {
                colorDeferral.Complete();
                ResetTodoReorderVisualState();
            }
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            ResetTodoReorderVisualState();
            return;
        }

        e.Handled = true;
        SetTodoItemHoverState(border, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            int addedCount = await ViewModel.AddDroppedAttachmentsAsync(item.Id, batch.Files);
            e.AcceptedOperation = addedCount > 0
                ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to attach dropped files: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    private void SelectColorFilter(TodoColorFilter filter)
    {
        if (DateTimeOffset.UtcNow <= _suppressColorFilterClickUntil)
        {
            return;
        }

        if (ViewModel is null)
        {
            return;
        }

        ViewModel.SetColorFilter(ViewModel.SelectedColorFilter == filter
            ? TodoColorFilter.All
            : filter);
        RefreshFilterButtons();
    }

    private void SelectFilter(TodoFilter filter)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.SelectedFilter == filter)
        {
            RefreshFilterButtons();
            return;
        }

        ViewModel.SetFilter(filter);
        RefreshFilterButtons();
    }

    private void RefreshFilterButtons()
    {
        if (TodoFilterSegmented is null || ViewModel is null)
        {
            return;
        }

        int selectedIndex = GetFilterSegmentIndex(ViewModel.SelectedFilter);
        if (TodoFilterSegmented.SelectedIndex != selectedIndex)
        {
            TodoFilterSegmented.SelectedIndex = selectedIndex;
        }

        ApplyColorFilterButtonState(RedColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Red);
        ApplyColorFilterButtonState(OrangeColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Orange);
        ApplyColorFilterButtonState(YellowColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Yellow);
        ApplyColorFilterButtonState(GreenColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Green);
        ApplyColorFilterButtonState(BlueColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Blue);
        ApplyColorFilterButtonState(PurpleColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Purple);
        ApplyColorFilterButtonState(TealColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Teal);
        ApplyColorFilterButtonState(PinkColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Pink);
    }

    private TodoFilter GetSelectedSegmentFilter() =>
        DeskBox.Controls.Todo.TodoFilterBarController.GetFilterFromSegmentIndex(
            TodoFilterSegmented?.SelectedIndex);

    private static int GetFilterSegmentIndex(TodoFilter filter) =>
        DeskBox.Controls.Todo.TodoFilterBarController.GetSegmentIndex(filter);

    private void ApplyColorFilterButtonState(Button button, bool isSelected)
    {
        button.Style = (Style)Resources[isSelected
            ? "TodoColorFilterSelectedButtonStyle"
            : "TodoColorFilterButtonStyle"];
    }

    private async void ItemCompletionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not CheckBox checkBox ||
            checkBox.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        bool updated = await SetCompletedWithFeedbackAsync(item, checkBox.IsChecked == true);
        if (updated)
        {
            SynchronizeTodoCompletionCheckBox(item);
            DispatcherQueue.TryEnqueue(() => SynchronizeTodoCompletionCheckBox(item));
        }
        else if (ReferenceEquals(checkBox.DataContext, item))
        {
            checkBox.IsChecked = item.IsCompleted;
        }

    }

    private async void ImportantItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await SetImportantWithFeedbackAsync(item, !item.IsImportant);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await DeleteItemAsync(item);
    }

    private void RecurringHistoryToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        ViewModel.ToggleRecurringHistoryGroup(item.RecurrenceSeriesId);
    }

    private void TodoListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var draggedItem = e.Items.OfType<TodoItemViewModel>().FirstOrDefault();
        var selectedItems = GetSelectedCopyItemsInVisibleOrder();
        _draggedTodoItemIds.Clear();
        if (draggedItem is not null &&
            selectedItems.Count > 1 &&
            selectedItems.Contains(draggedItem))
        {
            _draggedTodoItemId = null;
            _draggedTodoItemIds.AddRange(selectedItems.Select(item => item.Id));
            TodoListView.CanReorderItems = false;
            string text = TodoClipboardFormatter.FormatBatch(
                selectedItems,
                App.Current.LocalizationService);
            if (string.IsNullOrWhiteSpace(text))
            {
                _draggedTodoItemIds.Clear();
                e.Cancel = true;
                ResetTodoReorderVisualState();
                return;
            }

            DeskBoxDragData.SetText(e.Data, text, DeskBoxDragData.SourceTodo);
            // Export drags are copy-only: advertising Move would let a Win10
            // Explorer drop move backing files (and still prompts the picker).
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            e.Data.Properties.Title = App.Current.LocalizationService.Format("Todo.CopiedCount", selectedItems.Count);
            return;
        }

        if (selectedItems.Count > 0)
        {
            ClearCopySelection();
        }

        _draggedTodoItemId = draggedItem?.Id;
        if (draggedItem is not null)
        {
            _draggedTodoItemIds.Add(draggedItem.Id);
            // VisibleItemsSource is object[] in Native AOT. Keep WinUI's
            // native reordering disabled and persist the row-drop position in
            // TodoItem_Drop instead.
            TodoListView.CanReorderItems = false;
            DeskBoxDragData.SetText(
                e.Data,
                TodoClipboardFormatter.FormatSingle(draggedItem, App.Current.LocalizationService),
                DeskBoxDragData.SourceTodo);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        else
        {
            _draggedTodoItemIds.Clear();
            e.Cancel = true;
            ResetTodoReorderVisualState();
        }
    }

    private void TodoTab_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null ||
            _draggedTodoItemIds.Count == 0 ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetTodoTabDropTarget(tag, out TodoFilter target) ||
            !ViewModel.CanApplyTabDrop(_draggedTodoItemIds, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        e.AcceptedOperation =
            DeskBoxDragData.ResolveInternalMetadataOperation(
                e.AllowedOperations);
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.Caption = App.Current.LocalizationService.T(
            DeskBox.Controls.Todo.TodoFilterBarController.GetTabDropCaptionKey(target));
    }

    private async void TodoTab_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null ||
            _draggedTodoItemIds.Count == 0 ||
            sender is not FrameworkElement { Tag: string tag } ||
            !TryGetTodoTabDropTarget(tag, out TodoFilter target) ||
            !ViewModel.CanApplyTabDrop(_draggedTodoItemIds, target))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            int changedCount = await ViewModel.ApplyTabDropAsync(_draggedTodoItemIds.ToArray(), target);
            e.AcceptedOperation = changedCount > 0
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
            if (changedCount > 0)
            {
                SelectFilter(target);
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.DropTab.Applied", changedCount),
                    clearUndoOnHide: false);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static bool TryGetTodoTabDropTarget(string tag, out TodoFilter target) =>
        DeskBox.Controls.Todo.TodoFilterBarController.TryGetTabDropTarget(tag, out target);

    private void ClearSubscribedTodoRows() =>
        _rowVisuals.ClearSubscribedRows(TodoItem_PropertyChanged);

    private void TodoItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (!_rowVisuals.AttachRow(sender, TodoItem_PropertyChanged, out TodoItemViewModel? item) ||
            item is null)
        {
            return;
        }

        SetTodoItemHoverState(sender, false);
        if (sender is Border border)
        {
            ApplyTodoReorderDropState(
                border,
                active: false,
                insertAfter: false);
        }

        var parts = _rowVisuals.GetOrResolveParts(
            sender,
            static root => new DeskBox.Controls.Todo.TodoRowVisualController.RowVisualParts(
                FindVisualChild<Border>(root, "TodoItemHoverBackground"),
                FindVisualChild<Border>(root, "TodoItemActionHost"),
                FindVisualChild<CheckBox>(root, "TodoCompletionCheckBox")));
        if ((parts.CompletionCheckBox ?? FindVisualChild<CheckBox>(sender, "TodoCompletionCheckBox")) is { } checkBox)
        {
            checkBox.IsChecked = item.IsCompleted;
        }
    }

    private async Task DropTodoItemAtRowAsync(
        Border border,
        TodoItemViewModel targetItem,
        DragEventArgs e)
    {
        string? draggedItemId = _draggedTodoItemId;
        if (ViewModel is null || string.IsNullOrWhiteSpace(draggedItemId))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        bool insertAfter = e.GetPosition(border).Y >= border.ActualHeight / 2;
        int targetIndex = TodoDragPackage.ResolveManualDropTargetIndex(
            ViewModel.VisibleItems,
            draggedItemId,
            targetItem.Id,
            insertAfter);
        if (targetIndex < 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.Handled = true;
        ApplyTodoReorderDropState(
            border,
            active: false,
            insertAfter: false);
        var deferral = e.GetDeferral();
        try
        {
            bool persisted = await ViewModel.MoveItemAsync(
                draggedItemId,
                targetIndex);
            e.AcceptedOperation = persisted
                ? DataPackageOperation.Move
                : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Reorder failed: {ex}");
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            ApplyTodoReorderDropState(
                border,
                active: false,
                insertAfter: false);
            deferral.Complete();
        }
    }

    private void ApplyTodoReorderDropState(
        Border border,
        bool active,
        bool insertAfter) =>
        _rowVisuals.ApplyReorderDropState(border, active, insertAfter);

    private void ClearTodoReorderDropState() =>
        _rowVisuals.ClearReorderDropState();

    private void TodoListView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        _draggedTodoItemId = null;
        _draggedTodoItemIds.Clear();
        ResetTodoReorderVisualState();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTodoItemIds.Count > 0)
        {
            return;
        }

        ResetTodoReorderVisualState();
        HandleExternalDragOver(e);
    }

    private void TodoListView_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedTodoItemIds.Count > 0)
        {
            return;
        }

        ResetTodoReorderVisualState();
        HandleExternalDragOver(e);
    }

    private void ExternalTodoDrag_DragLeave(object sender, DragEventArgs e)
    {
        ResetTodoReorderVisualState();
    }

    private void HandleExternalDragOver(DragEventArgs e)
    {
        if (ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        bool supported =
            DeskBoxDragData.HasDroppedFiles(e.DataView) ||
            e.DataView.Contains(DeskBoxDragData.TextFormat) ||
            e.DataView.Contains(StandardDataFormats.Text) ||
            e.DataView.Contains(StandardDataFormats.WebLink);
        e.AcceptedOperation = supported
            ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                ? DeskBoxDragData.GetFileAssociationOperation(
                    e.DataView,
                    e.AllowedOperations)
                : DataPackageOperation.Copy
            : DataPackageOperation.None;
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            ApplyFileAssociationDragFeedback(e);
        }
        else
        {
            e.DragUIOverride.IsGlyphVisible = supported;
        }
        e.Handled = supported;
    }

    private static void SuppressNativeFileDragOverride(DragEventArgs e)
    {
        e.DragUIOverride.IsContentVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private static void ApplyFileAssociationDragFeedback(DragEventArgs e)
    {
        if (!DeskBoxDragData.IsInternalFileDrag(e.DataView))
        {
            SuppressNativeFileDragOverride(e);
            return;
        }

        e.DragUIOverride.IsContentVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.Caption = App.Current.LocalizationService.T(
            "Widget.Compact.TodoDropHint");
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_draggedTodoItemIds.Count > 0)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = await ImportExternalDropAsync(e.DataView)
                ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                    : DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    internal static bool CanImportExternalDropData(DataPackageView dataView)
    {
        return DeskBoxDragData.HasDroppedFiles(dataView) ||
               dataView.Contains(DeskBoxDragData.TextFormat) ||
               dataView.Contains(StandardDataFormats.Text) ||
               dataView.Contains(StandardDataFormats.WebLink);
    }

    internal bool CanImportExternalDrop(DataPackageView dataView)
    {
        return DeskBoxDragData.HasDroppedFiles(dataView) ||
               dataView.Contains(DeskBoxDragData.TextFormat) ||
               dataView.Contains(StandardDataFormats.Text) ||
               dataView.Contains(StandardDataFormats.WebLink);
    }

    internal async Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(dataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
                if (batch.Files.Count == 0 || ViewModel is null)
                {
                    string? fallbackText = await TryGetDroppedTodoTextAsync(dataView);
                    if (string.IsNullOrWhiteSpace(fallbackText) || ViewModel is null)
                    {
                        return false;
                    }

                    await ViewModel.AddItemAsync(fallbackText);
                    return true;
                }

                TodoItemViewModel? targetItem = ViewModel.IsDetailPageOpen
                    ? ViewModel.SelectedDetailItem
                    : await ViewModel.AddItemAsync(BuildDroppedTodoTitle(batch.Files));
                if (targetItem is null)
                {
                    return false;
                }

                int addedCount = await ViewModel.AddDroppedAttachmentsAsync(targetItem.Id, batch.Files);
                if (addedCount > 0)
                {
                    ShowUndoToast(
                        App.Current.LocalizationService.T("Todo.Dropped"),
                        clearUndoOnHide: false);
                }

                return addedCount > 0;
            }

            string? text = await TryGetDroppedTodoTextAsync(dataView);
            if (string.IsNullOrWhiteSpace(text) || ViewModel is null)
            {
                return false;
            }

            await ViewModel.AddItemAsync(text);
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Dropped"),
                clearUndoOnHide: false);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import dropped content: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.DropFailed"),
                clearUndoOnHide: false);
            return false;
        }
        finally
        {
            ResetTodoReorderVisualState();
        }
    }

    internal async Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        TodoItemViewModel? targetItem)
    {
        if (files.Count == 0 || ViewModel is null)
        {
            return false;
        }

        try
        {
            TodoItemViewModel? item = targetItem ??
                (ViewModel.IsDetailPageOpen
                    ? ViewModel.SelectedDetailItem
                    : await ViewModel.AddItemAsync(BuildDroppedTodoTitle(files)));
            if (item is null)
            {
                return false;
            }

            int addedCount = await ViewModel.AddDroppedAttachmentsAsync(
                item.Id,
                files);
            if (addedCount <= 0)
            {
                return false;
            }

            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.Dropped"),
                clearUndoOnHide: false);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import native dropped files: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.DropFailed"),
                clearUndoOnHide: false);
            return false;
        }
    }

    internal static async Task<string?> TryGetDroppedTodoTextAsync(DataPackageView dataView)
    {
        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        string? text = await DeskBoxDragData.TryGetTextAsync(dataView);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        const int maxDroppedTodoTextCharacters = 64 * 1024;
        text = text.Trim();
        return text.Length <= maxDroppedTodoTextCharacters
            ? text
            : text[..maxDroppedTodoTextCharacters].Trim();
    }

    internal static string BuildDroppedTodoTitle(IReadOnlyList<DroppedFilePath> files)
    {
        string title = files.Count == 1
            ? System.IO.Path.GetFileNameWithoutExtension(files[0].DisplayName)
            : string.Join(", ", files.Take(3).Select(file => file.DisplayName));
        if (files.Count > 3)
        {
            title = $"{title} +{files.Count - 3}";
        }

        return string.IsNullOrWhiteSpace(title) ? "Attachment" : title;
    }
}
