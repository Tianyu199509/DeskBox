using System.ComponentModel;
using System.Numerics;
using DeskBox.Contracts;
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
    private Task PickBatchCustomDueDateAsync(
        IReadOnlyList<string> itemIds,
        DateTimeOffset? initialDueDate,
        bool initialHasDueTime = false)
    {
        if (ViewModel is null || itemIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        if (!EnsureCustomDueDateOverlayRealized())
        {
            return Task.CompletedTask;
        }

        _customDueDateItem = null;
        _customDueDateItemIds = itemIds.ToArray();
        DateTimeOffset dueDate = initialDueDate ?? GetDefaultCustomDueDate();
        if (CustomDueDatePicker is not null)
        {
            CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
            CustomDueDatePicker.Date = dueDate;
        }

        ConfigureCustomDueTimeState(dueDate, includeTime: initialDueDate is not null && initialHasDueTime);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay!.Visibility = Visibility.Visible;
        CustomDueDatePicker?.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    private void CustomDueDateCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCustomDueDateOverlay();
    }

    private void CustomDueIncludeTimeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncingCustomDueIncludeTime || CustomDueIncludeTimeToggle is null)
        {
            return;
        }

        _customDueIncludeTime = CustomDueIncludeTimeToggle.IsOn;
        UpdateCustomDueTimeControlsVisibility();
    }

    private void CustomDueTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomDueTimeButton is null)
        {
            return;
        }

        var flyout = new TimePickerFlyout
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 1,
            Time = _customDueTime
        };
        flyout.TimePicked += (_, args) =>
        {
            _customDueTime = args.NewTime;
            UpdateCustomDueTimeText();
        };

        flyout.ShowAt(CustomDueTimeButton, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.Top
        });
    }

        private async void CustomDueDateSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            CloseCustomDueDateOverlay();
            return;
        }

        DateTimeOffset selectedDate = CustomDueDatePicker?.Date ?? DateTimeOffset.Now;
        bool hasDueTime = _customDueIncludeTime;
        DateTimeOffset selectedDueDate = hasDueTime
            ? CombineCustomDueDateAndTime(selectedDate)
            : TodoWidgetStore.NormalizeAllDayDueDate(selectedDate);
        TodoItemViewModel? customDueDateItem = _customDueDateItem;
        IReadOnlyList<string>? customDueDateItemIds = _customDueDateItemIds;
        CloseCustomDueDateOverlay();

        if (customDueDateItem is { } item)
        {
            await ViewModel.SetDueDateAsync(item.Id, selectedDueDate, hasDueTime);
        }
        else if (customDueDateItemIds is { Count: > 0 } itemIds)
        {
            await ViewModel.SetDueDateAsync(itemIds, selectedDueDate, hasDueTime);
        }
        else
        {
            ViewModel.SetDraftDueDate(selectedDueDate, hasDueTime);
        }
    }

    private void CloseCustomDueDateOverlay()
    {
        _customDueDateItem = null;
        _customDueDateItemIds = null;
        if (CustomDueDateOverlay is not null)
        {
            CustomDueDateOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigureCustomDueTimeState(DateTimeOffset dueDate, bool includeTime)
    {
        _customDueIncludeTime = includeTime;
        if (includeTime)
        {
            SetCustomDueTime(dueDate);
        }
        else
        {
            _customDueTime = new TimeSpan(9, 0, 0);
            UpdateCustomDueTimeText();
        }

        if (CustomDueIncludeTimeToggle is not null)
        {
            _isSyncingCustomDueIncludeTime = true;
            try
            {
                CustomDueIncludeTimeToggle.IsOn = includeTime;
            }
            finally
            {
                _isSyncingCustomDueIncludeTime = false;
            }
        }

        UpdateCustomDueTimeControlsVisibility();
    }

    private void UpdateCustomDueTimeControlsVisibility()
    {
        if (CustomDueTimeButton is not null)
        {
            CustomDueTimeButton.Visibility = _customDueIncludeTime
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SetCustomDueTime(DateTimeOffset dueDate)
    {
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        _customDueTime = new TimeSpan(localDueDate.Hour, localDueDate.Minute, 0);
        UpdateCustomDueTimeText();
    }

    private void UpdateCustomDueTimeText()
    {
        if (CustomDueTimeText is not null)
        {
            CustomDueTimeText.Text = $"{_customDueTime.Hours:00}:{_customDueTime.Minutes:00}";
        }
    }

    private DateTimeOffset CombineCustomDueDateAndTime(DateTimeOffset selectedDate)
    {
        DateTimeOffset localDate = selectedDate.ToLocalTime();
        var localDateTime = new DateTime(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            _customDueTime.Hours,
            _customDueTime.Minutes,
            0,
            DateTimeKind.Local);

        return new DateTimeOffset(localDateTime);
    }

    private static DateTimeOffset GetDefaultCustomDueDate() =>
        TodoWidgetStore.NormalizeAllDayDueDate(DateTimeOffset.Now);

    private Task DeleteItemAsync(TodoItemViewModel item) =>
        DeleteTodoItemWithTransitionAsync(item);

    private async Task DeleteTodoItemWithTransitionAsync(TodoItemViewModel item)
    {
        if (ViewModel is null)
        {
            return;
        }

        bool isOpenDetail = ReferenceEquals(ViewModel.SelectedDetailItem, item);
        if (!isOpenDetail)
        {
            await ViewModel.DeleteItemAsync(item.Id);
            return;
        }

        if (_isClosingDetail)
        {
            return;
        }

        _isClosingDetail = true;
        try
        {
            if (!await PlayDetailExitAnimationAsync(item))
            {
                return;
            }

            await ViewModel.DeleteItemAsync(item.Id);
            ClearTodoListContainerSelection();
            Focus(FocusState.Programmatic);
        }
        finally
        {
            ResetDetailTransition();
        }
    }

    private async Task DeleteSelectedItemsAsync(IReadOnlyList<string> selectedIds)
    {
        if (ViewModel is null || selectedIds.Count == 0)
        {
            return;
        }

        ClearCopySelection();
        await ViewModel.DeleteItemsAsync(selectedIds);
    }

    private async void TodoEditSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveTodoEditAsync();
    }

    private void TodoEditCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseTodoEdit();
    }

    private async void TodoEditTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CloseTodoEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (ShouldSubmitTodoEditor(e))
            {
                await SaveTodoEditAsync();
            }
            else
            {
                TextBoxEditorShortcutHelper.InsertLineBreak(TodoEditTextBox);
            }
        }
    }

    private async Task SaveTodoEditAsync()
    {
        if (ViewModel is null)
        {
            CloseTodoEdit();
            return;
        }

        if (_editingItem is not { } item)
        {
            CloseTodoEdit();
            return;
        }

        bool updated;
        try
        {
            updated = await ViewModel.UpdateItemTextAsync(item.Id, TodoInlineEditor.Text);
        }
        catch (Exception ex)
        {
            // A failed store commit must not escape the async void callers
            // (save button / Enter key) — an uncaught IO failure crash-kills
            // the process. Keep the editor open with the user's text so the
            // input is not lost while the failure is logged.
            App.Log($"[Todo] Save edit failed id={item.Id}: {ex}");
            TodoEditTextBox.Focus(FocusState.Programmatic);
            TodoEditTextBox.SelectAll();
            return;
        }

        if (!updated)
        {
            TodoEditTextBox.Focus(FocusState.Programmatic);
            TodoEditTextBox.SelectAll();
            return;
        }

        CloseTodoEdit();
        ShowTodoStatus("Todo.Status.Saved");
    }

    private void CommitOrCloseTodoInlineEdit()
    {
        if (_editingItem is { } editingItem &&
            TodoInlineEditor is { Visibility: Visibility.Visible } &&
            ViewModel is { } vm)
        {
            string editedText = TodoInlineEditor.Text;
            CloseTodoEdit();
            if (!string.IsNullOrWhiteSpace(editedText) &&
                !string.Equals(editingItem.Text, editedText.Trim(), StringComparison.Ordinal))
            {
                _ = vm.UpdateItemTextAsync(editingItem.Id, editedText);
            }

            return;
        }

        CloseTodoEdit();
    }

    private void CloseTodoEdit()
    {
        _editingItem = null;
        if (TodoInlineEditor is null)
        {
            return;
        }

        TodoInlineEditor.Visibility = Visibility.Collapsed;
        TodoInlineEditor.Text = string.Empty;
        TodoInlineEditor.Title = App.Current.LocalizationService.T("Todo.Menu.Edit");
    }

    private void SetTodoItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        // Hover is a pointer state, so the list row uses the neutral hover
        // surface - the same tone the XAML declares for it - instead of an
        // accent tint.
        var hoverBackground = NeutralInteractionBrush.Fill(
            itemRoot as FrameworkElement);
        var parts = _rowVisuals.GetOrResolveParts(
            itemRoot,
            static root => new DeskBox.Controls.Todo.TodoRowVisualController.RowVisualParts(
                FindVisualChild<Border>(root, "TodoItemHoverBackground"),
                FindVisualChild<Border>(root, "TodoItemActionHost"),
                FindVisualChild<CheckBox>(root, "TodoCompletionCheckBox")));

        if ((parts.HoverBackground ?? FindVisualChild<Border>(itemRoot, "TodoItemHoverBackground")) is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = SharedBrushCache.GetOrCreate(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if ((parts.ActionHost ?? FindVisualChild<Border>(itemRoot, "TodoItemActionHost")) is { } actions)
        {
            actions.Opacity = isHovered ? 1 : 0;
            actions.IsHitTestVisible = isHovered;
        }

    }

    private void ApplyEditorVisualStyle()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        if (TodoInlineEditor is not null)
        {
            TodoInlineEditor.OverlaySurface.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
            TodoInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
            TodoInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
            TodoEditTextBox.Background = new SolidColorBrush(GetNeutralInputSurfaceColor(isDark));
            TodoEditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
            TodoEditTextBox.Foreground = GetBrushResourceOrFallback(
                "TextFillColorPrimaryBrush",
                isDark ? Colors.White : Colors.Black);
        }

        ApplyCustomDueDateVisualStyle(isDark);
    }

    private void ApplyCustomDueDateVisualStyle(bool isDark)
    {
        if (CustomDueDateOverlay is null)
        {
            return;
        }

        CustomDueDateOverlay.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        CustomDueDateOverlay.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        CustomDueDateOverlay.BorderThickness = new Thickness(0.8);
    }

    private void ApplySelectionRectangleStyle()
    {
        if (TodoSelectionRectangle is null)
        {
            return;
        }

        // Marquee selection is a pointer state, so it uses the same neutral
        // palette as every other transient interaction visual.
        TodoSelectionRectangle.Background = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.Fill(TodoSelectionRectangle));
        TodoSelectionRectangle.BorderBrush = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.Line(TodoSelectionRectangle));
    }

    private static Windows.UI.Color GetNeutralOverlaySurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x2A, 0x30, 0x38)
            : ColorHelper.FromArgb(0xFF, 0xFB, 0xFC, 0xFD);
    }

    private static Windows.UI.Color GetNeutralInputSurfaceColor(bool isDark)
    {
        return isDark
            ? ColorHelper.FromArgb(0xFF, 0x22, 0x28, 0x30)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    private Brush GetNeutralOverlayBorderBrush(bool isDark)
    {
        return GetBrushResourceOrFallback(
            "CardStrokeColorDefaultBrush",
            isDark ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    private Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        // Resolve by this element's own theme: a bare application-scope
        // lookup would follow the system theme and invert the resource
        // whenever the widget's theme override disagrees with it.
        return NeutralInteractionBrush.ResolveThemedResource(resourceKey, this) ??
               new SolidColorBrush(fallbackColor);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length != 6 ||
            !byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) ||
            !byte.TryParse(value.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green) ||
            !byte.TryParse(value.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            return Colors.Gray;
        }

        return ColorHelper.FromArgb(0xFF, red, green, blue);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string? name = null)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed &&
                (name is null || string.Equals(typed.Name, name, StringComparison.Ordinal)))
            {
                return typed;
            }

            if (FindVisualChild<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static FrameworkElement? FindTodoItemContainer(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Grid grid &&
                grid.DataContext is TodoItemViewModel)
            {
                return grid;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T)
            {
                return true;
            }

            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.ClearCompletedAsync();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.UndoLastActionAsync();
            ShowUndoToast(App.Current.LocalizationService.T("Common.Undone"));
        }
    }

    private void ShowUndoToast(
        string text,
        string? actionText = null,
        bool clearUndoOnHide = true)
    {
        long generation = ++_undoToastGeneration;
        TodoWidgetViewModel? vm = ViewModel;
        Func<Task>? action = string.IsNullOrWhiteSpace(actionText)
            ? null
            : async () =>
            {
                if (vm is not null && !vm.IsDisposed)
                {
                    await vm.UndoLastActionAsync();
                }
            };
        var request = new WidgetFeedbackRequest(
            text,
            WidgetFeedbackSeverity.Success,
            "todo-status",
            actionText,
            action);
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(request));
        UndoToast.Opacity = 0;
        UndoToast.IsHitTestVisible = false;
        UndoToastActionButton.Visibility = Visibility.Collapsed;
        _ = HideUndoToastAfterDelayAsync(
            generation,
            (int)request.DisplayDuration.TotalMilliseconds,
            clearUndoOnHide,
            vm);
    }

    private void ShowTodoStatus(string resourceKey) =>
        ShowUndoToast(
            App.Current.LocalizationService.T(resourceKey),
            clearUndoOnHide: false);

    private async Task<bool> SetCompletedWithFeedbackAsync(
        TodoItemViewModel item,
        bool isCompleted)
    {
        if (ViewModel is null || !await ViewModel.SetCompletedAsync(item.Id, isCompleted))
        {
            return false;
        }

        ShowTodoStatus(isCompleted
            ? "Todo.Status.MarkedCompleted"
            : "Todo.Status.MarkedActive");
        return true;
    }

    private async Task<bool> SetImportantWithFeedbackAsync(
        TodoItemViewModel item,
        bool isImportant)
    {
        if (ViewModel is null || !await ViewModel.SetImportantAsync(item.Id, isImportant))
        {
            return false;
        }

        ShowTodoStatus(isImportant
            ? "Todo.Status.MarkedImportant"
            : "Todo.Status.UnmarkedImportant");
        return true;
    }

    private async Task HideUndoToastAfterDelayAsync(
        long generation,
        int durationMs,
        bool clearUndo,
        TodoWidgetViewModel? vm)
    {
        await Task.Delay(durationMs);
        if (_isDisposed || DispatcherQueue is null)
        {
            if (clearUndo &&
                Interlocked.CompareExchange(ref _undoToastGeneration, generation + 1, generation) == generation)
            {
                vm?.DismissUndo();
            }

            return;
        }

        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _undoToastGeneration)
            {
                HideUndoToast(clearUndo, vm);
            }
        });
        if (!queued &&
            clearUndo &&
            Interlocked.CompareExchange(ref _undoToastGeneration, generation + 1, generation) == generation)
        {
            vm?.DismissUndo();
        }
    }

    private void HideUndoToast(bool clearUndo, TodoWidgetViewModel? vm = null)
    {
        _undoToastGeneration++;
        if (!_isDisposed)
        {
            UndoToast.Opacity = 0;
            UndoToast.IsHitTestVisible = false;
            UndoToastActionButton.Visibility = Visibility.Collapsed;
        }

        if (clearUndo)
        {
            (vm ?? ViewModel)?.DismissUndo();
        }
    }
}
