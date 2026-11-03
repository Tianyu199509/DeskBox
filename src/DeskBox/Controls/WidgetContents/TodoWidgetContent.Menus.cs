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
    private void TodoItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not TodoItemViewModel item)
        {
            return;
        }

        DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(this, "todo-context-menu");
        TodoListView.Focus(FocusState.Programmatic);
        if (!item.IsCopySelected)
        {
            ClearCopySelection();
            item.IsCopySelected = true;
            _copySelectionAnchorId = item.Id;
        }

        CreateItemFlyout(item, element).ShowAt(
            element,
            new FlyoutShowOptions { Position = e.GetPosition(element) });
        e.Handled = true;
    }

    private MenuFlyout CreateItemFlyout(TodoItemViewModel item, FrameworkElement anchor)
    {
        var selectedItems = GetSelectedCopyItemsInVisibleOrder();
        if (selectedItems.Count > 1 && item.IsCopySelected)
        {
            return CreateMultiItemFlyout(selectedItems, anchor);
        }

        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;

        var editItem = CreateTodoContextCommand("Todo.Menu.Edit", "\uE70F");
        editItem.Click += async (_, _) =>
        {
            flyout.Hide();
            ClearCopySelection();
            ClearTodoListContainerSelection();
            CloseCustomDueDateOverlay();
            MarkDetailSelectionExplicit();
            if (await OpenDetailItemAsync(item.Id) is null || ViewModel is null)
            {
                return;
            }

            ViewModel.BeginEdit(item.Id);
            _ = EnsureDetailPageRealized();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (DetailTitleTextBox is not null)
                {
                    DetailTitleTextBox.Focus(FocusState.Programmatic);
                    DetailTitleTextBox.Select(DetailTitleTextBox.Text?.Length ?? 0, 0);
                }
            });
        };
        flyout.Items.Add(editItem);

        var renameItem = CreateTodoContextCommand("Todo.Menu.Rename", "\uE8AC");
        renameItem.Click += (_, _) =>
        {
            flyout.Hide();
            OpenTodoInlineEdit(item);
        };
        flyout.Items.Add(renameItem);

        var completeItem = CreateTodoContextCommand(
            item.IsCompleted ? "Todo.Menu.MarkActive" : "Todo.Menu.MarkCompleted",
            "\uE73E");
        completeItem.Click += async (_, _) =>
        {
            flyout.Hide();
            bool updated = await SetCompletedWithFeedbackAsync(item, !item.IsCompleted);
            if (updated)
            {
                SynchronizeTodoCompletionCheckBox(item);
            }
        };
        flyout.Items.Add(completeItem);

        var importantItem = CreateTodoContextCommand(
            item.IsImportant ? "Todo.Menu.UnmarkImportant" : "Todo.Menu.MarkImportant",
            item.IsImportant ? "\uE734" : "\uE735");
        importantItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await SetImportantWithFeedbackAsync(item, !item.IsImportant);
        };
        flyout.Items.Add(importantItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = selectedItems.Count > 1 && item.IsCopySelected
                ? localization.Format("Todo.Menu.CopySelected", selectedItems.Count)
                : localization.T("Todo.Menu.Copy"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            flyout.Hide();
            if (selectedItems.Count > 1 && item.IsCopySelected)
            {
                CopySelectedTodoItems(selectedItems);
            }
            else
            {
                CopyTodoItemText(item);
            }
        };
        flyout.Items.Add(copyItem);

        var convertItem = CreateTodoContextCommand("Todo.Menu.ConvertToQuickCapture", "\uE70B");
        convertItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ConvertTodoItemsToQuickCaptureWithFeedbackAsync([item]);
        };
        flyout.Items.Add(convertItem);

        var deleteItem = CreateTodoContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteItemAsync(item);
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var colorMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateColorMarkerItem(item, null));
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (string colorMarker in TodoItem.SupportedColorMarkers)
            {
                menu.Items.Add(CreateColorMarkerItem(item, colorMarker));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.ColorMarker", "\uE790", colorMenu, flyout));

        var dueDateMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Today, localization.T("Todo.Due.Today")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek")));
            menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday")));
            menu.Items.Add(new MenuFlyoutSeparator());
            var customDueDateItem = new MenuFlyoutItem
            {
                Text = localization.T("Todo.Due.Custom"),
                Icon = new FontIcon { Glyph = "\uE8A5" }
            };
            customDueDateItem.Click += async (_, _) => await PickCustomDueDateAsync(item);
            menu.Items.Add(customDueDateItem);
            if (item.DueDate is not null)
            {
                menu.Items.Add(CreateDuePresetItem(item, TodoDuePreset.Clear, localization.T("Todo.Due.Clear")));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.DueDate", "\uE787", dueDateMenu, flyout));

        var reminderMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            menu.Items.Add(CreateReminderOffsetItem(item, null));
            menu.Items.Add(CreateReminderOffsetItem(item, TodoReminderOptions.ReminderOff));
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (int offsetMinutes in TodoReminderOptions.SupportedOffsetMinutes)
            {
                menu.Items.Add(CreateReminderOffsetItem(item, offsetMinutes));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Reminder", "\uEA8F", reminderMenu, flyout, item.DueDate is not null));

        var recurrenceMenu = CreateLazyTodoMenu(flyout, menu =>
        {
            foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
            {
                menu.Items.Add(CreateRecurrenceItem(item, recurrenceMode));
            }
        });
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Recurrence", "\uE823", recurrenceMenu, flyout));

        if (item.DueDate is not null &&
            !item.IsCompleted &&
            !TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes))
        {
            var snoozeMenu = CreateLazyTodoMenu(flyout, menu =>
            {
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.10Minutes"), TimeSpan.FromMinutes(10)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.30Minutes"), TimeSpan.FromMinutes(30)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.OneHour"), TimeSpan.FromHours(1)));
                menu.Items.Add(CreateSnoozeItem(item, localization.T("Todo.Snooze.Tomorrow"), GetTomorrowSnoozeTime()));
            });
            flyout.Items.Add(CreateTodoContextSubmenu(
                "Todo.Menu.Snooze", "\uE823", snoozeMenu, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private static MenuFlyout CreateLazyTodoMenu(
        MenuFlyout owner,
        Action<MenuFlyout> populate)
    {
        var menu = new MenuFlyout();
        populate(menu);
        foreach (var menuItem in menu.Items.OfType<MenuFlyoutItem>())
        {
            menuItem.Click += (_, _) => owner.Hide();
        }
        return menu;
    }

    private MenuFlyout CreateMultiItemFlyout(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        FrameworkElement anchor)
    {
        var flyout = new MenuFlyout();
        var localization = App.Current.LocalizationService;
        string[] selectedIds = selectedItems.Select(item => item.Id).ToArray();

        bool anyActive = selectedItems.Any(item => !item.IsCompleted);
        var batchCompleteItem = CreateTodoContextCommand(
            anyActive ? "Todo.Menu.MarkCompleted" : "Todo.Menu.MarkActive",
            "\uE73E");
        batchCompleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            if (ViewModel is not null)
            {
                int changed = await ViewModel.SetCompletedBatchAsync(selectedIds, anyActive);
                if (changed > 0 && !anyActive)
                {
                    ShowTodoStatus("Todo.Status.MarkedActive");
                }

                foreach (TodoItemViewModel selected in selectedItems)
                {
                    SynchronizeTodoCompletionCheckBox(selected);
                }
            }
        };
        flyout.Items.Add(batchCompleteItem);

        bool anyUnimportant = selectedItems.Any(item => !item.IsImportant);
        var batchImportantItem = CreateTodoContextCommand(
            anyUnimportant ? "Todo.Menu.MarkImportant" : "Todo.Menu.UnmarkImportant",
            anyUnimportant ? "\uE735" : "\uE734");
        batchImportantItem.Click += async (_, _) =>
        {
            flyout.Hide();
            if (ViewModel is not null)
            {
                int changed = await ViewModel.SetImportantAsync(selectedIds, anyUnimportant);
                if (changed > 0)
                {
                    ShowTodoStatus(anyUnimportant
                        ? "Todo.Status.MarkedImportant"
                        : "Todo.Status.UnmarkedImportant");
                }
            }
        };
        flyout.Items.Add(batchImportantItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = localization.Format("Todo.Menu.CopySelected", selectedItems.Count),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            flyout.Hide();
            CopySelectedTodoItems(selectedItems);
        };
        flyout.Items.Add(copyItem);

        var convertSelectedItem = CreateTodoContextCommand("Todo.Menu.ConvertToQuickCapture", "\uE70B");
        convertSelectedItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ConvertTodoItemsToQuickCaptureWithFeedbackAsync(selectedItems);
        };
        flyout.Items.Add(convertSelectedItem);

        var exportSelectedItem = new MenuFlyoutItem
        {
            Text = localization.Format("Todo.Export.SelectedMenuItem", selectedItems.Count),
            Icon = new FontIcon { Glyph = "\uEDE1" }
        };
        exportSelectedItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await ExportSelectedItemsAsync(selectedItems, anchor);
        };
        flyout.Items.Add(exportSelectedItem);

        var deleteItem = CreateTodoContextCommand("Common.Delete", "\uE74D");
        deleteItem.Click += async (_, _) =>
        {
            flyout.Hide();
            await DeleteSelectedItemsAsync(selectedIds);
        };
        flyout.Items.Add(new MenuFlyoutSeparator());

        var colorMenu = new MenuFlyout();
        colorMenu.Items.Add(CreateBatchColorMarkerItem(selectedItems, selectedIds, null, flyout));
        colorMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (string colorMarker in TodoItem.SupportedColorMarkers)
        {
            colorMenu.Items.Add(CreateBatchColorMarkerItem(selectedItems, selectedIds, colorMarker, flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.ColorMarker", "\uE790", colorMenu, flyout));

        bool hasDueDate = selectedItems.Any(item => item.DueDate is not null);
        var dueDateMenu = new MenuFlyout();
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.Today, localization.T("Todo.Due.Today"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.Tomorrow, localization.T("Todo.Due.Tomorrow"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.ThisWeek, localization.T("Todo.Due.ThisWeek"), flyout));
        dueDateMenu.Items.Add(CreateBatchDuePresetItem(
            selectedIds, TodoDuePreset.NextMonday, localization.T("Todo.Due.NextMonday"), flyout));
        dueDateMenu.Items.Add(new MenuFlyoutSeparator());
        var customDueDateItem = new MenuFlyoutItem
        {
            Text = localization.T("Todo.Due.Custom"),
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        customDueDateItem.Click += async (_, _) =>
        {
            flyout.Hide();
            TodoItemViewModel? initialItem = selectedItems.FirstOrDefault(item => item.DueDate is not null);
            await PickBatchCustomDueDateAsync(
                selectedIds,
                initialItem?.DueDate,
                initialItem?.HasDueTime == true);
        };
        dueDateMenu.Items.Add(customDueDateItem);
        if (hasDueDate)
        {
            dueDateMenu.Items.Add(CreateBatchDuePresetItem(
                selectedIds, TodoDuePreset.Clear, localization.T("Todo.Due.Clear"), flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.DueDate", "\uE787", dueDateMenu, flyout));

        var reminderMenu = new MenuFlyout();
        reminderMenu.Items.Add(CreateBatchReminderOffsetItem(selectedItems, selectedIds, null, flyout));
        reminderMenu.Items.Add(CreateBatchReminderOffsetItem(
            selectedItems,
            selectedIds,
            TodoReminderOptions.ReminderOff,
            flyout));
        reminderMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (int offsetMinutes in TodoReminderOptions.SupportedOffsetMinutes)
        {
            reminderMenu.Items.Add(CreateBatchReminderOffsetItem(
                selectedItems,
                selectedIds,
                offsetMinutes,
                flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Reminder", "\uEA8F", reminderMenu, flyout, hasDueDate));

        var recurrenceMenu = new MenuFlyout();
        foreach (string recurrenceMode in TodoRecurrenceMode.SupportedModes)
        {
            recurrenceMenu.Items.Add(CreateBatchRecurrenceItem(
                selectedItems, selectedIds, recurrenceMode, flyout));
        }
        flyout.Items.Add(CreateTodoContextSubmenu(
            "Todo.Menu.Recurrence", "\uE823", recurrenceMenu, flyout));

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    private MenuFlyoutItem CreateBatchColorMarkerItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        string? colorMarker,
        MenuFlyout owner)
    {
        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        bool isSelected = selectedItems.All(item =>
            string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal));
        var menuItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoItem.GetColorMarkerLocalizationKey(colorMarker)),
            Icon = colorMarker is null
                ? new FontIcon { Glyph = isSelected ? "\uE73E" : "\uE711" }
                : CreateColorMarkerIcon(colorMarker, isSelected)
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetColorMarkerAsync(selectedIds, colorMarker);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchReminderOffsetItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        int? offsetMinutes,
        MenuFlyout owner)
    {
        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        var dueItems = selectedItems.Where(item => item.DueDate is not null).ToList();
        bool isSelected = dueItems.Count > 0 &&
                          dueItems.All(item => Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset));
        var menuItem = new MenuFlyoutItem
        {
            Text = FormatReminderOffsetText(normalizedOffset),
            Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetReminderOffsetAsync(selectedIds, normalizedOffset);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchDuePresetItem(
        IReadOnlyList<string> selectedIds,
        TodoDuePreset preset,
        string text,
        MenuFlyout owner)
    {
        var menuItem = new MenuFlyoutItem { Text = text };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetDueDatePresetAsync(selectedIds, preset);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateBatchRecurrenceItem(
        IReadOnlyList<TodoItemViewModel> selectedItems,
        IReadOnlyList<string> selectedIds,
        string recurrenceMode,
        MenuFlyout owner)
    {
        string normalizedMode = TodoRecurrenceMode.Normalize(recurrenceMode);
        bool isSelected = selectedItems.Count > 0 &&
                          selectedItems.All(item => string.Equals(item.RecurrenceMode, normalizedMode, StringComparison.Ordinal));
        var menuItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoRecurrenceService.GetLocalizationKey(normalizedMode)),
            Icon = isSelected ? new FontIcon { Glyph = "\uE73E" } : null
        };
        menuItem.Click += async (_, _) =>
        {
            owner.Hide();
            if (ViewModel is not null)
            {
                await ViewModel.SetRecurrenceAsync(selectedIds, normalizedMode);
            }
        };
        return menuItem;
    }

    private MenuFlyoutItem CreateTodoContextCommand(string localizationKey, string glyph)
    {
        return new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
    }

    private MenuFlyoutSubItem CreateTodoContextSubmenu(
        string localizationKey,
        string glyph,
        MenuFlyout submenu,
        MenuFlyout owner,
        bool isEnabled = true)
    {
        foreach (var menuItem in submenu.Items.OfType<MenuFlyoutItem>())
        {
            menuItem.Click += (_, _) => owner.Hide();
        }

        var subItem = new MenuFlyoutSubItem
        {
            Text = App.Current.LocalizationService.T(localizationKey),
            Icon = new FontIcon { Glyph = glyph },
            IsEnabled = isEnabled
        };
        while (submenu.Items.Count > 0)
        {
            MenuFlyoutItemBase item = submenu.Items[0];
            submenu.Items.RemoveAt(0);
            subItem.Items.Add(item);
        }

        return subItem;
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
        if (isSelected)
        {
            // The checkmark goes through the default SymbolThemeFontFamily
            // (a Fluent/MDL2 fallback chain, both of which resolve) like
            // every other E73E glyph in this file.
            return new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 12,
                Foreground = new SolidColorBrush(color)
            };
        }

        // The unselected dot is geometry, not a glyph. Segoe UI Symbol's
        // font file is removed on Win11 25H2 (26300) while its registry
        // entry remains, and the first measure of a FontIcon pinned to
        // that family fails in CFontFamily::GetTextLineBoundsMetrics with
        // E_UNEXPECTED — a stowed exception that fail-fasts the process
        // the moment the color submenu opens.
        return new PathIcon
        {
            Data = new EllipseGeometry
            {
                Center = new Windows.Foundation.Point(8, 8),
                RadiusX = 5,
                RadiusY = 5
            },
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

    private MenuFlyoutItem CreateRecurrenceItem(TodoItemViewModel item, string recurrenceMode)
    {
        string normalizedMode = TodoRecurrenceMode.Normalize(recurrenceMode);
        bool isSelected = string.Equals(item.RecurrenceMode, normalizedMode, StringComparison.Ordinal);
        var recurrenceItem = new MenuFlyoutItem
        {
            Text = App.Current.LocalizationService.T(TodoRecurrenceService.GetLocalizationKey(normalizedMode)),
            Icon = isSelected
                ? new FontIcon { Glyph = "\uE73E" }
                : null
        };
        recurrenceItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetRecurrenceAsync(item.Id, normalizedMode);
            }
        };
        return recurrenceItem;
    }

    private MenuFlyoutItem CreateReminderOffsetItem(TodoItemViewModel item, int? offsetMinutes)
    {
        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        bool isSelected = Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset);
        var reminderItem = new MenuFlyoutItem
        {
            Text = FormatReminderOffsetText(normalizedOffset),
            Icon = isSelected
                ? new FontIcon { Glyph = "\uE73E" }
                : null
        };
        reminderItem.Click += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetReminderOffsetAsync(item.Id, normalizedOffset);
            }
        };
        return reminderItem;
    }

    private MenuFlyoutItem CreateSnoozeItem(TodoItemViewModel item, string text, TimeSpan snoozeFor)
    {
        var snoozeItem = new MenuFlyoutItem
        {
            Text = text
        };
        snoozeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null &&
                await ViewModel.SnoozeReminderAsync(item.Id, snoozeFor))
            {
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.Snooze.Set", text),
                    clearUndoOnHide: false);
            }
        };
        return snoozeItem;
    }

    private MenuFlyoutItem CreateSnoozeItem(TodoItemViewModel item, string text, DateTimeOffset snoozedUntil)
    {
        var snoozeItem = new MenuFlyoutItem
        {
            Text = text
        };
        snoozeItem.Click += async (_, _) =>
        {
            if (ViewModel is not null &&
                await ViewModel.SnoozeReminderUntilAsync(item.Id, snoozedUntil))
            {
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.Snooze.Set", text),
                    clearUndoOnHide: false);
            }
        };
        return snoozeItem;
    }

    private static DateTimeOffset GetTomorrowSnoozeTime()
    {
        DateTime tomorrowMorning = DateTime.Now.Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(tomorrowMorning);
    }

    private static string FormatReminderOffsetText(int? offsetMinutes)
    {
        var localization = App.Current.LocalizationService;
        if (offsetMinutes is null)
        {
            int defaultOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                App.Current.SettingsService.Settings.TodoDefaultReminderOffsetMinutes);
            return localization.Format(
                "Todo.Reminder.Default",
                FormatExplicitReminderOffsetText(defaultOffsetMinutes));
        }

        if (TodoReminderOptions.IsReminderOff(offsetMinutes))
        {
            return localization.T("Todo.Reminder.Off");
        }

        return FormatExplicitReminderOffsetText(offsetMinutes.Value);
    }

    private static string FormatExplicitReminderOffsetText(int offsetMinutes)
    {
        var localization = App.Current.LocalizationService;
        return SettingsService.NormalizeTodoReminderOffsetMinutes(offsetMinutes) switch
        {
            0 => localization.T("Todo.Reminder.AtDueTime"),
            60 => localization.T("Todo.Reminder.OneHourBefore"),
            1440 => localization.T("Todo.Reminder.OneDayBefore"),
            int minutes => localization.Format("Todo.Reminder.MinutesBefore", minutes)
        };
    }

    private async Task ConvertTodoItemsToQuickCaptureWithFeedbackAsync(
        IReadOnlyList<TodoItemViewModel> items)
    {
        if (_isDisposed || items.Count == 0)
        {
            return;
        }

        TodoItem[] snapshots = items
            .Where(vm => !vm.Item.IsDeleted && !string.IsNullOrWhiteSpace(vm.Text))
            .Select(vm => TodoWidgetStore.CloneItem(vm.Item))
            .ToArray();
        if (snapshots.Length == 0 || App.Current.WidgetManager is not { } widgetManager)
        {
            return;
        }

        try
        {
            int createdCount = await widgetManager.ConvertTodoItemsToQuickCaptureAsync(snapshots);
            if (createdCount > 0)
            {
                ShowUndoToast(
                    App.Current.LocalizationService.Format("Todo.ConvertedToQuickCaptureCount", createdCount),
                    clearUndoOnHide: false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to convert {snapshots.Length} item(s) to QuickCapture: {ex}");
        }
    }
}
