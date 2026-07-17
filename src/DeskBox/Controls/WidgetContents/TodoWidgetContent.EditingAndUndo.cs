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
    private Task PickBatchCustomDueDateAsync(
        IReadOnlyList<string> itemIds,
        DateTimeOffset? initialDueDate)
    {
        if (ViewModel is null || itemIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        CloseTodoEdit();
        _pendingConfirmFlyout?.Hide();
        _customDueDateItem = null;
        _customDueDateItemIds = itemIds.ToArray();
        DateTimeOffset dueDate = initialDueDate ?? GetDefaultCustomDueDate();
        CustomDueDatePicker.MinDate = DateTimeOffset.Now.Date;
        CustomDueDatePicker.Date = dueDate;
        SetCustomDueTime(dueDate);
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        CustomDueDateOverlay.Visibility = Visibility.Visible;
        CustomDueDatePicker.Focus(FocusState.Programmatic);
        return Task.CompletedTask;
    }

    private void CustomDueDateCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCustomDueDateOverlay();
    }

    private void CustomDueTimeButton_Click(object sender, RoutedEventArgs e)
    {
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

        DateTimeOffset selectedDate = CustomDueDatePicker.Date ?? DateTimeOffset.Now;
        DateTimeOffset selectedDueDate = CombineCustomDueDateAndTime(selectedDate);
        TodoItemViewModel? customDueDateItem = _customDueDateItem;
        IReadOnlyList<string>? customDueDateItemIds = _customDueDateItemIds;
        CloseCustomDueDateOverlay();

        if (customDueDateItem is { } item)
        {
            await ViewModel.SetDueDateAsync(item.Id, selectedDueDate);
        }
        else if (customDueDateItemIds is { Count: > 0 } itemIds)
        {
            await ViewModel.SetDueDateAsync(itemIds, selectedDueDate);
        }
        else
        {
            ViewModel.DraftDueDate = selectedDueDate;
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

    private static DateTimeOffset GetDefaultCustomDueDate()
    {
        DateTime today = DateTime.Now.Date;
        return new DateTimeOffset(new DateTime(today.Year, today.Month, today.Day, 23, 59, 0, DateTimeKind.Local));
    }

    private async Task DeleteItemAsync(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
                App.Current.LocalizationService.T("Common.Delete"),
                async () => await DeleteTodoItemWithTransitionAsync(item));
            return;
        }

        await DeleteTodoItemWithTransitionAsync(item);
    }

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

    private void ShowDeleteItemConfirmation(TodoItemViewModel item, FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.ConfirmBeforeDelete)
        {
            _ = ViewModel.DeleteItemAsync(item.Id);
            return;
        }

        ShowTodoConfirmMenu(
            anchor,
            App.Current.LocalizationService.T("Todo.DeleteConfirm.Title"),
            App.Current.LocalizationService.T("Common.Delete"),
            async () => await ViewModel.DeleteItemAsync(item.Id));
    }

    private void ShowDeleteSelectedConfirmation(IReadOnlyList<string> selectedIds, FrameworkElement anchor)
    {
        if (ViewModel is null || selectedIds.Count == 0)
        {
            return;
        }

        async Task DeleteSelectedAsync()
        {
            ClearCopySelection();
            await ViewModel.DeleteItemsAsync(selectedIds);
        }

        if (!ViewModel.ConfirmBeforeDelete)
        {
            _ = DeleteSelectedAsync();
            return;
        }

        ShowTodoConfirmMenu(
            anchor,
            App.Current.LocalizationService.Format("Todo.DeleteSelectedConfirm.Title", selectedIds.Count),
            App.Current.LocalizationService.T("Common.Delete"),
            DeleteSelectedAsync);
    }

        private void BeginItemEdit(TodoItemViewModel item)
    {
        ClearCopySelection();
        CloseCustomDueDateOverlay();

        if (!item.IsExpanded && ViewModel is not null)
        {
            ViewModel.ToggleExpanded(item.Id);
            TodoListView.ScrollIntoView(item);
        }

        ViewModel?.BeginEdit(item.Id);
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

        bool updated = await ViewModel.UpdateItemTextAsync(item.Id, TodoInlineEditor.Text);
        if (!updated)
        {
            TodoEditTextBox.Focus(FocusState.Programmatic);
            TodoEditTextBox.SelectAll();
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

    private static void SetTodoItemHoverState(DependencyObject? itemRoot, bool isHovered)
    {
        if (itemRoot is null)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var hoverBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x25, 0x28, 0x2F) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                accentMix: isDark ? 0.24 : 0.12,
                overlayMix: isDark ? 0.04 : 0.02),
            isDark ? (byte)0x6A : (byte)0x86);

        if (FindVisualChild<Border>(itemRoot, "TodoItemHoverBackground") is { } hoverBackgroundBorder)
        {
            hoverBackgroundBorder.Background = new SolidColorBrush(hoverBackground);
            hoverBackgroundBorder.Opacity = isHovered ? 1 : 0;
        }

        if (FindVisualChild<Border>(itemRoot, "TodoItemActionHost") is { } actions)
        {
            actions.Opacity = isHovered ? 1 : 0;
            actions.IsHitTestVisible = isHovered;
            actions.Background = new SolidColorBrush(WithAlpha(
                BuildAccentSurfaceColor(
                    isDark,
                    accentColor,
                    isDark ? ColorHelper.FromArgb(0xFF, 0x1E, 0x23, 0x29) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    accentMix: isDark ? 0.18 : 0.08,
                    overlayMix: isDark ? 0.03 : 0.02),
                0xFF));
            actions.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x4A : (byte)0x30));
            actions.BorderThickness = new Thickness(1);
            foreach (var button in FindVisualChildren<Button>(actions))
            {
                ApplyActionButtonTheme(button, isDark, accentColor);
            }
        }

        bool isCompleted = itemRoot is FrameworkElement { DataContext: TodoItemViewModel { IsCompleted: true } };
        if (FindVisualChild<Border>(itemRoot, "TodoCompletionBox") is { } completionBox)
        {
            completionBox.Background = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(Colors.Transparent);
            completionBox.BorderBrush = isCompleted
                ? new SolidColorBrush(WithAlpha(accentColor, 0xFF))
                : new SolidColorBrush(isHovered
                    ? WithAlpha(accentColor, isDark ? (byte)0xFF : (byte)0xE8)
                    : isDark
                        ? WithAlpha(accentColor, 0xD8)
                        : WithAlpha(accentColor, 0xB8));
        }
    }

    private void ApplyEditorVisualStyle()
    {
        if (TodoInlineEditor is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        TodoInlineEditor.OverlaySurface.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        TodoInlineEditor.OverlaySurface.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoInlineEditor.OverlaySurface.BorderThickness = new Thickness(0.8);
        TodoEditTextBox.Background = new SolidColorBrush(GetNeutralInputSurfaceColor(isDark));
        TodoEditTextBox.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        TodoEditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);

        CustomDueDateOverlay.Background = new SolidColorBrush(GetNeutralOverlaySurfaceColor(isDark));
        CustomDueDateOverlay.BorderBrush = GetNeutralOverlayBorderBrush(isDark);
        CustomDueDateOverlay.BorderThickness = new Thickness(0.8);
        ApplyDetailCompletionVisualState();
    }

    private void ApplyDetailCompletionVisualState()
    {
        if (DetailCompletionBox is null || ViewModel?.SelectedDetailItem is not { } item)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        DetailCompletionBox.Background = item.IsCompleted
            ? new SolidColorBrush(accentColor)
            : new SolidColorBrush(Colors.Transparent);
        DetailCompletionBox.BorderBrush = item.IsCompleted
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(isDark
                ? WithAlpha(accentColor, 0xD8)
                : WithAlpha(accentColor, 0xB8));
    }

    private void ApplySelectionRectangleStyle()
    {
        if (TodoSelectionRectangle is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        TodoSelectionRectangle.Background = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x2D : (byte)0x24));
        TodoSelectionRectangle.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xD8 : (byte)0xCC));
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

    private static Brush GetNeutralOverlayBorderBrush(bool isDark)
    {
        return GetBrushResourceOrFallback(
            "CardStrokeColorDefaultBrush",
            isDark ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    private static void ApplyActionButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var foreground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0xF2 : (byte)0xE2));

        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = transparent;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accentColor,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        var tintedColor = BlendColors(baseColor, accentColor, accentMix);
        var overlayColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        return BlendColors(tintedColor, overlayColor, overlayMix);
    }

    private static Windows.UI.Color BlendColors(Windows.UI.Color fromColor, Windows.UI.Color toColor, double amount)
    {
        amount = Math.Clamp(amount, 0.0, 1.0);

        static byte BlendChannel(byte from, byte to, double mix) =>
            (byte)Math.Clamp(Math.Round(from + ((to - from) * mix)), 0, 255);

        return ColorHelper.FromArgb(
            BlendChannel(fromColor.A, toColor.A, amount),
            BlendChannel(fromColor.R, toColor.R, amount),
            BlendChannel(fromColor.G, toColor.G, amount),
            BlendChannel(fromColor.B, toColor.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
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
        if (ViewModel is null ||
            sender is not FrameworkElement anchor)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearCompletedConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearCompleted"),
                async () => await ViewModel.ClearCompletedAsync());
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

    private void DismissUndoButton_Click(object sender, RoutedEventArgs e)
    {
        HideUndoToast(clearUndo: true);
    }

    private void ShowUndoToast(
        string text,
        string? actionText = null,
        int durationMs = UndoToastMs,
        bool clearUndoOnHide = true)
    {
        long generation = ++_undoToastGeneration;
        UndoToastText.Text = text;
        UndoToastActionButton.Content = actionText;
        UndoToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        UndoToast.Opacity = 1;
        _ = HideUndoToastAfterDelayAsync(generation, durationMs, clearUndoOnHide);
    }

    private async Task HideUndoToastAfterDelayAsync(long generation, int durationMs, bool clearUndo)
    {
        await Task.Delay(durationMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _undoToastGeneration)
            {
                HideUndoToast(clearUndo);
            }
        });
    }

    private void HideUndoToast(bool clearUndo)
    {
        _undoToastGeneration++;
        UndoToast.Opacity = 0;
        UndoToast.IsHitTestVisible = false;
        UndoToastActionButton.Visibility = Visibility.Collapsed;
        if (clearUndo)
        {
            ViewModel?.DismissUndo();
        }
    }

    public void ShowClearAllConfirmation(FrameworkElement anchor)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.ConfirmBeforeDelete)
        {
            ShowTodoConfirmMenu(
                anchor,
                App.Current.LocalizationService.T("Todo.ClearAllConfirm.Title"),
                App.Current.LocalizationService.T("Todo.ClearAll"),
                async () => await ViewModel.ClearAllAsync());
            return;
        }

        _ = ViewModel.ClearAllAsync();
    }

    private void ShowTodoConfirmMenu(
        FrameworkElement anchor,
        string title,
        string actionText,
        Func<Task> confirmedAction)
    {
        _pendingConfirmFlyout?.Hide();

        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            title,
            actionText,
            confirmedAction);
        _pendingConfirmFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pendingConfirmFlyout, flyout))
            {
                _pendingConfirmFlyout = null;
            }
        };
        flyout.ShowAt(anchor);
    }

    private static string GetDeleteConfirmPreviewText(string text)
    {
        string normalized = string.Join(
            " ",
            (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "Task";
        }

        return normalized.Length <= 34 ? normalized : $"{normalized[..34].Trim()}...";
    }
}
