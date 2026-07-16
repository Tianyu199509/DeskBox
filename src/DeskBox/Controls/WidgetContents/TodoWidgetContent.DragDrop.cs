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

        DeskBoxDragData.SetTodoColorMarker(e.Data, colorMarker);
        e.Data.RequestedOperation = DataPackageOperation.Link;
        e.Data.Properties.Title = App.Current.LocalizationService.T(
            TodoItem.GetColorMarkerLocalizationKey(colorMarker));
    }

    private void ColorFilterButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button &&
            e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
        {
            _pressedColorFilterButton = button;
            _colorFilterDragStartPoint = e.GetCurrentPoint(RootGrid).Position;
        }
    }

    private void RegisterColorFilterHandledEvents()
    {
        if (_colorFilterHandledEventsRegistered)
        {
            return;
        }

        _colorFilterHandledEventsRegistered = true;
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
                new PointerEventHandler(ColorFilterButton_PointerPressed),
                handledEventsToo: true);
            button.AddHandler(
                UIElement.PointerMovedEvent,
                new PointerEventHandler(ColorFilterButton_PointerMoved),
                handledEventsToo: true);
        }
    }

    private async void ColorFilterButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isStartingColorFilterDrag ||
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
        try
        {
            await button.StartDragAsync(e.GetCurrentPoint(button));
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to start color marker drag: {ex.Message}");
        }
        finally
        {
            _suppressColorFilterClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(350);
            _pressedColorFilterButton = null;
            _isStartingColorFilterDrag = false;
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
        ResetTodoReorderVisualState();

        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
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
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsGlyphVisible = true;
            SetTodoItemHoverState(sender as DependencyObject, true);
        }
    }

    private void TodoItem_DragLeave(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat) ||
            DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.Handled = true;
            SetTodoItemHoverState(sender as DependencyObject, false);
        }

        ResetTodoReorderVisualState();
    }

    private async void TodoItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoItemViewModel item } || ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ResetTodoReorderVisualState();
            return;
        }

        if (e.DataView.Contains(DeskBoxDragData.TodoColorMarkerFormat))
        {
            e.Handled = true;
            SetTodoItemHoverState(sender as DependencyObject, false);
            string? colorMarker = TodoItem.NormalizeColorMarker(
                await DeskBoxDragData.TryGetTodoColorMarkerAsync(e.DataView));
            if (colorMarker is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ResetTodoReorderVisualState();
                return;
            }

            await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
            e.AcceptedOperation = DataPackageOperation.Link;
            ResetTodoReorderVisualState();
            return;
        }

        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            ResetTodoReorderVisualState();
            return;
        }

        e.Handled = true;
        SetTodoItemHoverState(sender as DependencyObject, false);
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            int addedCount = await ViewModel.AddDroppedAttachmentsAsync(item.Id, batch.Files);
            e.AcceptedOperation = addedCount > 0
                ? DataPackageOperation.Copy
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

    private TodoFilter GetSelectedSegmentFilter()
    {
        return TodoFilterSegmented?.SelectedIndex switch
        {
            1 => TodoFilter.Today,
            2 => TodoFilter.Important,
            3 => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static int GetFilterSegmentIndex(TodoFilter filter)
    {
        return filter switch
        {
            TodoFilter.Today => 1,
            TodoFilter.Important => 2,
            TodoFilter.Completed => 3,
            _ => 0
        };
    }

    private void ApplyColorFilterButtonState(Button button, bool isSelected)
    {
        button.Style = (Style)Resources[isSelected
            ? "TodoColorFilterSelectedButtonStyle"
            : "TodoColorFilterButtonStyle"];
    }

    private async void ItemCompletionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        PlayCompletionToggleAnimation(element);
        await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
        ApplyDetailCompletionVisualState();
    }

    private static void PlayCompletionToggleAnimation(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (visual is null)
        {
            return;
        }

        var compositor = visual.Compositor;
        if (compositor is null)
        {
            return;
        }

        visual.StopAnimation("Scale");
        visual.CenterPoint = new Vector3(
            (float)(element.ActualSize.X * 0.5),
            (float)(element.ActualSize.Y * 0.5),
            0f);

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1.0f),
            new Vector2(0.3f, 1.0f));

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(300);
        scaleAnimation.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1.0f));
        scaleAnimation.InsertKeyFrame(0.4f, new Vector3(1.3f, 1.3f, 1.0f), easing);
        scaleAnimation.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f), easing);

        visual.StartAnimation("Scale", scaleAnimation);
    }

    private async void ImportantItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        await DeleteItemAsync(item, element);
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
        if (draggedItem is not null &&
            selectedItems.Count > 0 &&
            selectedItems.Contains(draggedItem))
        {
            _draggedTodoItemId = null;
            TodoListView.CanReorderItems = false;
            string text = selectedItems.Count == 1
                ? TodoClipboardFormatter.FormatSingle(selectedItems[0], App.Current.LocalizationService)
                : TodoClipboardFormatter.FormatBatch(selectedItems, App.Current.LocalizationService);
            if (string.IsNullOrWhiteSpace(text))
            {
                e.Cancel = true;
                ResetTodoReorderVisualState();
                return;
            }

            DeskBoxDragData.SetText(e.Data, text, DeskBoxDragData.SourceTodo);
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
            TodoListView.CanReorderItems = true;
            DeskBoxDragData.SetText(
                e.Data,
                TodoClipboardFormatter.FormatSingle(draggedItem, App.Current.LocalizationService),
                DeskBoxDragData.SourceTodo);
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
        else
        {
            e.Cancel = true;
            ResetTodoReorderVisualState();
        }
    }

    private void TodoItem_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        item.PropertyChanged -= TodoItem_PropertyChanged;
        item.PropertyChanged += TodoItem_PropertyChanged;
        ApplyTodoItemTooltips(sender, item);
        SetTodoItemHoverState(sender, false);
    }

    private async void TodoListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            _draggedTodoItemId = null;
            ResetTodoReorderVisualState();
            return;
        }

        try
        {
            var movedItem = ViewModel.VisibleItems.FirstOrDefault(item =>
                string.Equals(item.Id, _draggedTodoItemId, StringComparison.Ordinal));
            if (movedItem is not null)
            {
                await ViewModel.MoveItemAsync(movedItem.Id, ViewModel.VisibleItems.IndexOf(movedItem));
            }
        }
        finally
        {
            _draggedTodoItemId = null;
            ResetTodoReorderVisualState();
        }
    }

    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        ResetTodoReorderVisualState();
        await HandleExternalTextDragOverAsync(e);
    }

    private async void TodoListView_DragOver(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        ResetTodoReorderVisualState();
        await HandleExternalTextDragOverAsync(e);
    }

    private void ExternalTodoDrag_DragLeave(object sender, DragEventArgs e)
    {
        ResetTodoReorderVisualState();
    }

    private async Task HandleExternalTextDragOverAsync(DragEventArgs e)
    {
        if (ViewModel is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = DeskBoxDragData.HasDroppedFiles(e.DataView) ||
                                  await HasDroppedTodoTextAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = e.AcceptedOperation != DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_draggedTodoItemId))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = await ImportExternalDropAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
            ResetTodoReorderVisualState();
        }
    }

    internal async Task<bool> CanImportExternalDropAsync(DataPackageView dataView)
    {
        return DeskBoxDragData.HasDroppedFiles(dataView) ||
            await HasDroppedTodoTextAsync(dataView);
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
                        durationMs: CopyToastMs,
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
                durationMs: CopyToastMs,
                clearUndoOnHide: false);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import dropped content: {ex}");
            ShowUndoToast(
                App.Current.LocalizationService.T("Todo.DropFailed"),
                durationMs: UndoToastMs,
                clearUndoOnHide: false);
            return false;
        }
        finally
        {
            ResetTodoReorderVisualState();
        }
    }

    private static async Task<bool> HasDroppedTodoTextAsync(DataPackageView dataView)
    {
        string? text = await TryGetDroppedTodoTextAsync(dataView);
        return !string.IsNullOrWhiteSpace(text);
    }

    private static async Task<string?> TryGetDroppedTodoTextAsync(DataPackageView dataView)
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

        text = text.Trim();
        return text.Length <= QuickCaptureClipboardService.MaxClipboardTextCharacters
            ? text
            : text[..QuickCaptureClipboardService.MaxClipboardTextCharacters].Trim();
    }

    private static string BuildDroppedTodoTitle(IReadOnlyList<DroppedFilePath> files)
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
