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
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent : UserControl
{
    private const int UndoToastMs = 4200;

    private string? _draggedTodoItemId;
    private TodoItemViewModel? _editingItem;
    private MenuFlyout? _pendingConfirmFlyout;
    private Flyout? _customDueDateFlyout;
    private long _undoToastGeneration;

    public TodoWidgetContent()
    {
        InitializeComponent();
        Loaded += TodoWidgetContent_Loaded;
        Unloaded += TodoWidgetContent_Unloaded;
        ActualThemeChanged += (_, _) => ApplyEditorVisualStyle();
    }

    public TodoWidgetContent(TodoWidgetViewModel viewModel)
        : this()
    {
        ViewModel = viewModel;
    }

    public TodoWidgetViewModel? ViewModel
    {
        get => DataContext as TodoWidgetViewModel;
        set
        {
            if (DataContext is TodoWidgetViewModel oldViewModel)
            {
                oldViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            DataContext = value;

            if (value is not null)
            {
                value.PropertyChanged += ViewModel_PropertyChanged;
            }

            RefreshFilterButtons();
        }
    }

    private void TodoWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        App.Current.LocalizationService.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedText();
        ApplyEditorVisualStyle();
        RefreshFilterButtons();
    }

    private void TodoWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        App.Current.LocalizationService.LanguageChanged -= OnLanguageChanged;
        CloseTodoEdit();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedFilter))
        {
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.SelectedColorFilter))
        {
            RefreshFilterButtons();
        }

        if (e.PropertyName == nameof(TodoWidgetViewModel.UndoText) &&
            ViewModel is { CanUndoLastAction: true })
        {
            ShowUndoToast(ViewModel.UndoText, ViewModel.UndoActionText);
        }
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedText();
    }

    private void ApplyLocalizedText()
    {
        if (TodoEditTitleText is null)
        {
            return;
        }

        var localization = App.Current.LocalizationService;
        TodoEditTitleText.Text = localization.T("Todo.Menu.Edit");
        TodoEditCancelButton.Content = localization.T("Common.Cancel");
        TodoEditSaveButton.Content = localization.T("Common.Save");
    }

    private async void AddTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (ViewModel is not null)
        {
            await ViewModel.AddInputAsync();
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.AddInputAsync();
            AddTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void AllFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectFilter(TodoFilter.All);
    }

    private void TodayFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectFilter(TodoFilter.Today);
    }

    private void ImportantFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectFilter(TodoFilter.Important);
    }

    private void CompletedFilterButton_Click(object sender, RoutedEventArgs e)
    {
        SelectFilter(TodoFilter.Completed);
    }

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

    private void SelectColorFilter(TodoColorFilter filter)
    {
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
        if (AllFilterButton is null || ViewModel is null)
        {
            return;
        }

        ApplyFilterButtonState(AllFilterButton, ViewModel.SelectedFilter == TodoFilter.All);
        ApplyFilterButtonState(TodayFilterButton, ViewModel.SelectedFilter == TodoFilter.Today);
        ApplyFilterButtonState(ImportantFilterButton, ViewModel.SelectedFilter == TodoFilter.Important);
        ApplyFilterButtonState(CompletedFilterButton, ViewModel.SelectedFilter == TodoFilter.Completed);
        ApplyColorFilterButtonState(RedColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Red);
        ApplyColorFilterButtonState(OrangeColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Orange);
        ApplyColorFilterButtonState(YellowColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Yellow);
        ApplyColorFilterButtonState(GreenColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Green);
        ApplyColorFilterButtonState(BlueColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Blue);
        ApplyColorFilterButtonState(PurpleColorFilterButton, ViewModel.SelectedColorFilter == TodoColorFilter.Purple);
    }

    private void ApplyFilterButtonState(Button button, bool isSelected)
    {
        button.Style = (Style)Resources[isSelected
            ? "TodoFilterSelectedButtonStyle"
            : "TodoFilterButtonStyle"];
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

        await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
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

    private void TodoListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedTodoItemId = e.Items.OfType<TodoItemViewModel>().FirstOrDefault()?.Id;
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
        }
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
                ApplyTodoItemTooltips(container, item);
                SetTodoItemHoverState(container, false);
                break;
            }
        }
    }

    private static void ApplyTodoItemTooltips(DependencyObject itemRoot, TodoItemViewModel item)
    {
        var localization = App.Current.LocalizationService;

        _ = localization;
    }

    private void TodoItemText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        BeginItemEdit(item);
        e.Handled = true;
    }

    private void TodoItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        CreateItemFlyout(item, element).ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private MenuFlyout CreateItemFlyout(TodoItemViewModel item, FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        var editItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Menu.Edit"),
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        editItem.Click += (_, _) => BeginItemEdit(item);
        flyout.Items.Add(editItem);

        var completeItem = new MenuFlyoutItem
        {
            Text = item.IsCompleted
                ? localization.T("Todo.Menu.MarkActive")
                : localization.T("Todo.Menu.MarkCompleted"),
            Icon = new FontIcon { Glyph = item.IsCompleted ? "\uE73A" : "\uE73E" }
        };
        completeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetCompletedAsync(item.Id, !item.IsCompleted);
            }
        };
        flyout.Items.Add(completeItem);

        var importantItem = new MenuFlyoutItem
        {
            Text = item.IsImportant
                ? localization.T("Todo.Menu.UnmarkImportant")
                : localization.T("Todo.Menu.MarkImportant"),
            Icon = new FontIcon { Glyph = item.IsImportant ? "\uE735" : "\uE734" }
        };
        importantItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetImportantAsync(item.Id, !item.IsImportant);
            }
        };
        flyout.Items.Add(importantItem);

        var colorSubItem = new MenuFlyoutSubItem
        {
            Text = localization.T("Todo.Menu.ColorMarker"),
            Icon = new FontIcon { Glyph = "\uE915" }
        };
        colorSubItem.Items.Add(CreateColorMarkerItem(item, null));
        colorSubItem.Items.Add(new MenuFlyoutSeparator());
        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            colorSubItem.Items.Add(CreateColorMarkerItem(item, colorMarker));
        }

        flyout.Items.Add(colorSubItem);

        var dueSubItem = new MenuFlyoutSubItem
        {
            Text = localization.T("Todo.Menu.DueDate"),
            Icon = new FontIcon { Glyph = "\uE787" }
        };
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Today, localization.T("Todo.Due.Today")));
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
        dueSubItem.Items.Add(CreateDuePresetItem(item, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
        dueSubItem.Items.Add(new MenuFlyoutSeparator());
        var customDueItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customDueItem.Click += async (_, _) => await PickCustomDueDateAsync(item);
        dueSubItem.Items.Add(customDueItem);
        var clearDueItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Clear"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        clearDueItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(item.Id, TodoDuePreset.Clear);
            }
        };
        dueSubItem.Items.Add(clearDueItem);
        flyout.Items.Add(dueSubItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        deleteItem.Click += (_, _) => ShowDeleteItemConfirmation(item, anchor);
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateColorMarkerItem(TodoItemViewModel item, string? colorMarker)
    {
        var localization = App.Current.LocalizationService;
        bool isSelected = string.Equals(
            item.ColorMarker,
            TodoItem.NormalizeColorMarker(colorMarker),
            StringComparison.Ordinal);
        var colorItem = new MenuFlyoutItem
        {
            Text = localization.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker)),
            Icon = colorMarker is null
                ? new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE711" }
                : CreateColorMarkerIcon(colorMarker, isSelected)
        };
        colorItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetColorMarkerAsync(item.Id, colorMarker);
            }
        };

        return colorItem;
    }

    private static IconElement CreateColorMarkerIcon(string colorMarker, bool isSelected)
    {
        var color = ParseColor(TodoItem.GetColorMarkerHex(colorMarker));
        return new FontIcon
        {
            Glyph = isSelected ? "\uE73E" : "\u25CF",
            FontFamily = isSelected ? new FontFamily("Segoe MDL2 Assets") : new FontFamily("Segoe UI Symbol"),
            FontSize = isSelected ? 12 : 10,
            Foreground = new SolidColorBrush(color)
        };
    }

    private MenuFlyoutItem CreateDuePresetItem(TodoItemViewModel item, TodoDuePreset preset, string text)
    {
        var dueItem = new MenuFlyoutItem
        {
            Text = text
        };
        dueItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(item.Id, preset);
            }
        };
        return dueItem;
    }

    private Task PickCustomDueDateAsync(TodoItemViewModel item)
    {
        if (ViewModel is null || XamlRoot is null)
        {
            return Task.CompletedTask;
        }

        var localization = App.Current.LocalizationService;
        _customDueDateFlyout?.Hide();

        var picker = new CalendarDatePicker
        {
            Width = 206,
            MinWidth = 0,
            Date = item.DueDate ?? DateTimeOffset.Now,
            MinDate = DateTimeOffset.Now.Date,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var root = new StackPanel
        {
            Width = 236,
            Padding = new Thickness(2),
            Spacing = 10
        };

        root.Children.Add(new TextBlock
        {
            Text = localization.T("Todo.Due.Custom"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.WrapWholeWords
        });
        root.Children.Add(picker);

        var actions = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var cancelButton = new Button
        {
            Content = localization.T("Common.Cancel"),
            Style = (Style)Resources["TodoConfirmCommandButtonStyle"]
        };
        var confirmButton = new Button
        {
            Content = localization.T("Common.Ok"),
            Style = (Style)Resources["TodoConfirmCommandButtonStyle"]
        };

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        ApplyEditorCommandButtonTheme(cancelButton, isDark, accentColor, isPrimary: false);
        ApplyEditorCommandButtonTheme(confirmButton, isDark, accentColor, isPrimary: true);

        actions.Children.Add(cancelButton);
        actions.Children.Add(confirmButton);
        root.Children.Add(actions);

        var flyout = new Flyout
        {
            Content = root,
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            LightDismissOverlayMode = LightDismissOverlayMode.Off
        };

        _customDueDateFlyout = flyout;
        cancelButton.Click += (_, _) => flyout.Hide();
        confirmButton.Click += async (_, _) =>
        {
            flyout.Hide();
            await ViewModel.SetDueDateAsync(item.Id, picker.Date);
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_customDueDateFlyout, flyout))
            {
                _customDueDateFlyout = null;
            }
        };
        flyout.ShowAt(this);
        return Task.CompletedTask;
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
                async () => await ViewModel.DeleteItemAsync(item.Id));
            return;
        }

        await ViewModel.DeleteItemAsync(item.Id);
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

    private void BeginItemEdit(TodoItemViewModel item)
    {
        _editingItem = item;
        TodoEditTextBox.Text = item.Text;
        ApplyEditorVisualStyle();
        TodoEditOverlay.Visibility = Visibility.Visible;
        TodoEditTextBox.Focus(FocusState.Programmatic);
        TodoEditTextBox.Select(TodoEditTextBox.Text.Length, 0);
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

        if (e.Key == VirtualKey.Enter &&
            Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            await SaveTodoEditAsync();
            e.Handled = true;
        }
    }

    private async Task SaveTodoEditAsync()
    {
        if (ViewModel is null || _editingItem is not { } item)
        {
            CloseTodoEdit();
            return;
        }

        bool updated = await ViewModel.UpdateItemTextAsync(item.Id, TodoEditTextBox.Text);
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
        if (TodoEditOverlay is null)
        {
            return;
        }

        TodoEditOverlay.Visibility = Visibility.Collapsed;
        TodoEditTextBox.Text = string.Empty;
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
        if (TodoEditOverlay is null)
        {
            return;
        }

        bool isDark = ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor() ?? AccentColorHelper.DefaultAccentColor;
        var overlayBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark ? ColorHelper.FromArgb(0xFF, 0x1F, 0x24, 0x2A) : ColorHelper.FromArgb(0xFF, 0xFB, 0xFC, 0xFD),
            accentMix: isDark ? 0.06 : 0.03,
            overlayMix: isDark ? 0.03 : 0.02);
        var inputBackground = BuildAccentSurfaceColor(
            isDark,
            accentColor,
            isDark ? ColorHelper.FromArgb(0xFF, 0x18, 0x1D, 0x22) : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            accentMix: isDark ? 0.04 : 0.02,
            overlayMix: isDark ? 0.02 : 0.0);

        TodoEditOverlay.Background = new SolidColorBrush(WithAlpha(overlayBackground, 0xFF));
        TodoEditOverlay.BorderBrush = new SolidColorBrush(isDark
            ? ColorHelper.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00));
        TodoEditOverlay.BorderThickness = new Thickness(0.8);
        TodoEditTextBox.Background = new SolidColorBrush(WithAlpha(inputBackground, 0xFF));
        TodoEditTextBox.BorderBrush = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x52 : (byte)0x3A));
        TodoEditTextBox.Foreground = GetBrushResourceOrFallback(
            "TextFillColorPrimaryBrush",
            isDark ? Colors.White : Colors.Black);
        ApplyEditorCommandButtonTheme(TodoEditCancelButton, isDark, accentColor, isPrimary: false);
        ApplyEditorCommandButtonTheme(TodoEditSaveButton, isDark, accentColor, isPrimary: true);
        ApplyActionButtonTheme(TodoEditCloseButton, isDark, accentColor);
    }

    private static void ApplyEditorCommandButtonTheme(Button button, bool isDark, Windows.UI.Color accentColor, bool isPrimary)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var background = new SolidColorBrush(WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark ? ColorHelper.FromArgb(0xFF, 0x24, 0x29, 0x30) : ColorHelper.FromArgb(0xFF, 0xFA, 0xFB, 0xFD),
                accentMix: isPrimary ? (isDark ? 0.16 : 0.08) : (isDark ? 0.04 : 0.02),
                overlayMix: isDark ? 0.02 : 0.01),
            0xFF));
        var hoverBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x24 : (byte)0x18));
        var pressedBackground = new SolidColorBrush(WithAlpha(accentColor, isDark ? (byte)0x36 : (byte)0x24));
        var border = new SolidColorBrush(WithAlpha(accentColor, isPrimary ? (isDark ? (byte)0x64 : (byte)0x42) : (byte)0x28));
        var foreground = new SolidColorBrush(isPrimary
            ? WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xE2)
            : isDark
                ? ColorHelper.FromArgb(0xE8, 0xF4, 0xF7, 0xFB)
                : ColorHelper.FromArgb(0xE8, 0x1D, 0x1F, 0x23));

        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = hoverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonBackgroundDisabled"] = transparent;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonBorderBrushDisabled"] = transparent;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
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

    private void ShowUndoToast(string text, string? actionText = null, int durationMs = UndoToastMs)
    {
        long generation = ++_undoToastGeneration;
        UndoToastText.Text = text;
        UndoToastActionButton.Content = actionText;
        UndoToastActionButton.Visibility = string.IsNullOrWhiteSpace(actionText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoToast.IsHitTestVisible = !string.IsNullOrWhiteSpace(actionText);
        UndoToast.Opacity = 1;
        _ = HideUndoToastAfterDelayAsync(generation, durationMs);
    }

    private async Task HideUndoToastAfterDelayAsync(long generation, int durationMs)
    {
        await Task.Delay(durationMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (generation == _undoToastGeneration)
            {
                HideUndoToast(clearUndo: true);
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

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };
        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = title,
            Icon = new FontIcon { Glyph = "\uE783" },
            IsEnabled = false
        });
        flyout.Items.Add(new MenuFlyoutSeparator());
        var confirmItem = new MenuFlyoutItem
        {
            Text = actionText,
            Icon = new FontIcon
            {
                Glyph = "\uE74D",
                Foreground = new SolidColorBrush(Colors.Red)
            }
        };
        confirmItem.Click += async (_, _) => await confirmedAction();
        flyout.Items.Add(confirmItem);

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
