using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        LayoutDensityScale = NormalizeDensity(_settingsService.Settings.LayoutDensityScale);
        ApplySettings(updateFilter: false);
    }

    public void ApplySettings(bool updateFilter)
    {
        if (_settingsService is null)
        {
            return;
        }

        ApplyTodoSettings(_settingsService.Settings, updateFilter);
    }

    private TodoItemViewModel? FindItem(string itemId)
    {
        TodoItemViewModel? item = Items.FirstOrDefault(entry =>
            string.Equals(entry.Id, itemId, StringComparison.Ordinal));
        if (item is not null)
        {
            return item;
        }

        return IsCreatingDetailItem &&
               string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal)
            ? SelectedDetailItem
            : null;
    }

    private static double NormalizeDensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, SettingsService.MinLayoutDensityScale, SettingsService.MaxLayoutDensityScale)
            : SettingsService.DefaultLayoutDensityScale;

    private static double Lerp(double min, double max, double t) => min + ((max - min) * t);

    private static Thickness UniformThickness(double value)
    {
        double rounded = Math.Round(value);
        return new Thickness(rounded);
    }

    private TodoItemViewModel? FindGeneratedOccurrence(TodoItemViewModel item)
    {
        return string.IsNullOrWhiteSpace(item.Item.GeneratedNextItemId)
            ? null
            : FindItem(item.Item.GeneratedNextItemId);
    }

    private async Task SaveAsync()
    {
        NormalizeSortOrders();
        await _store.SaveAsync(new TodoWidgetData
        {
            Items = Items.Select(item => item.Item).ToList()
        });
    }

    private void RefreshVisibleItems()
    {
        VisibleItems.Clear();
        ResetRecurringHistoryState();

        var filteredItems = Items.Where(ShouldShowItem).ToList();
        filteredItems.Sort(CompareVisibleItems);

        var activeItems = filteredItems
            .Where(item => !item.IsCompleted)
            .ToList();
        var completedItems = filteredItems
            .Where(item => item.IsCompleted)
            .ToList();

        foreach (var item in activeItems)
        {
            VisibleItems.Add(item);
        }

        foreach (var item in BuildCompletedVisibleItems(completedItems))
        {
            VisibleItems.Add(item);
        }

        RefreshVisibleStateProperties();
    }

    private void ResetRecurringHistoryState()
    {
        foreach (var item in Items)
        {
            item.UpdateRecurringHistoryState(isLead: false, isExpanded: false, itemCount: 0);
        }
    }

    private List<TodoItemViewModel> BuildCompletedVisibleItems(IReadOnlyList<TodoItemViewModel> completedItems)
    {
        if (completedItems.Count == 0)
        {
            return [];
        }

        var groupBuckets = completedItems
            .Where(IsRecurringHistoryCandidate)
            .GroupBy(GetRecurringHistoryGroupKey, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key!,
                group => group
                    .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
                    .ThenByDescending(item => item.UpdatedAt)
                    .ToList(),
                StringComparer.Ordinal);

        var usedGroupKeys = new HashSet<string>(StringComparer.Ordinal);
        var completedDisplayItems = new List<TodoItemViewModel>();

        foreach (var item in completedItems)
        {
            string? groupKey = GetRecurringHistoryGroupKey(item);
            if (groupKey is null ||
                !groupBuckets.TryGetValue(groupKey, out List<TodoItemViewModel>? groupItems) ||
                groupItems.Count <= 1)
            {
                completedDisplayItems.Add(item);
                continue;
            }

            if (!usedGroupKeys.Add(groupKey))
            {
                continue;
            }

            bool isExpanded = _expandedRecurringHistoryGroupKeys.Contains(groupKey);
            groupItems[0].UpdateRecurringHistoryState(
                isLead: true,
                isExpanded: isExpanded,
                itemCount: groupItems.Count);
            completedDisplayItems.Add(groupItems[0]);

            if (isExpanded)
            {
                for (int index = 1; index < groupItems.Count; index++)
                {
                    completedDisplayItems.Add(groupItems[index]);
                }
            }
        }

        PruneRecurringHistoryExpansionState(
            groupBuckets
                .Where(entry => entry.Value.Count > 1)
                .Select(entry => entry.Key));
        return completedDisplayItems;
    }

    private bool ShouldShowItem(TodoItemViewModel item)
    {
        string? selectedColorMarker = MapColorFilterToMarker(SelectedColorFilter);
        if (selectedColorMarker is not null &&
            !string.Equals(item.ColorMarker, selectedColorMarker, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ShowCompletedTasks &&
            item.IsCompleted &&
            SelectedFilter != TodoFilter.Completed)
        {
            return false;
        }

        return SelectedFilter switch
        {
            TodoFilter.Active => !item.IsCompleted,
            TodoFilter.Today => IsDueToday(item),
            TodoFilter.ThisWeek => IsDueThisWeek(item),
            TodoFilter.ThisMonth => IsDueThisMonth(item),
            TodoFilter.Important => item.IsImportant,
            TodoFilter.Completed => item.IsCompleted,
            _ => true
        };
    }

    private void NormalizeSortOrders()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].SortOrder = index;
        }
    }

    private void RefreshCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(AllFilterCount));
        OnPropertyChanged(nameof(TodayFilterCount));
        OnPropertyChanged(nameof(ThisWeekFilterCount));
        OnPropertyChanged(nameof(ThisMonthFilterCount));
        OnPropertyChanged(nameof(ImportantFilterCount));
        OnPropertyChanged(nameof(RedColorFilterCount));
        OnPropertyChanged(nameof(OrangeColorFilterCount));
        OnPropertyChanged(nameof(YellowColorFilterCount));
        OnPropertyChanged(nameof(GreenColorFilterCount));
        OnPropertyChanged(nameof(BlueColorFilterCount));
        OnPropertyChanged(nameof(PurpleColorFilterCount));
        OnPropertyChanged(nameof(TealColorFilterCount));
        OnPropertyChanged(nameof(PinkColorFilterCount));
        OnPropertyChanged(nameof(AnyColorFilterCount));
        OnPropertyChanged(nameof(HasCompletedItems));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ItemsLeftText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(TodayFilterText));
        OnPropertyChanged(nameof(ThisWeekFilterText));
        OnPropertyChanged(nameof(ThisMonthFilterText));
        OnPropertyChanged(nameof(ImportantFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
        OnPropertyChanged(nameof(ColorFilterVisibility));
        RefreshColorFilterVisibilityProperties();
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(IsAllTasksCompletedEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ListHeaderText));
    }

    private void RefreshVisibleStateProperties()
    {
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(ListVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ColorFilterVisibility));
        RefreshColorFilterVisibilityProperties();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AddPlaceholderText));
        OnPropertyChanged(nameof(ExpandInputTooltipText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(TodayFilterText));
        OnPropertyChanged(nameof(ThisWeekFilterText));
        OnPropertyChanged(nameof(ThisMonthFilterText));
        OnPropertyChanged(nameof(ImportantFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ClearCompletedText));
        OnPropertyChanged(nameof(ItemsLeftText));
        OnPropertyChanged(nameof(UndoActionText));
        OnPropertyChanged(nameof(ListHeaderText));
        OnPropertyChanged(nameof(DetailBackText));
        OnPropertyChanged(nameof(DetailAddFileText));
        OnPropertyChanged(nameof(DetailRemoveAttachmentText));
        OnPropertyChanged(nameof(DetailFileMissingText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ColorMarkerText));
        OnPropertyChanged(nameof(RedColorText));
        OnPropertyChanged(nameof(OrangeColorText));
        OnPropertyChanged(nameof(YellowColorText));
        OnPropertyChanged(nameof(GreenColorText));
        OnPropertyChanged(nameof(BlueColorText));
        OnPropertyChanged(nameof(PurpleColorText));
        OnPropertyChanged(nameof(TealColorText));
        OnPropertyChanged(nameof(PinkColorText));
        foreach (var item in Items)
        {
            item.RefreshLocalizedText();
        }
    }

    private void ApplyTodoSettings(AppSettings settings, bool updateFilter)
    {
        string normalizedNewTaskPosition = NormalizeNewTaskPosition(settings.TodoNewTaskPosition);
        bool newTaskPositionChanged = !string.Equals(
            NewTaskPosition,
            normalizedNewTaskPosition,
            StringComparison.Ordinal);

        NewTaskPosition = normalizedNewTaskPosition;
        TabStyle = settings.TodoTabStyle;
        ApplyTabVisibilitySettings(settings);
        ShowCompletedTasks = settings.TodoShowCompletedTasks;
        ShowFooterStats = settings.TodoShowFooterStats;
        ShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        ConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;
        OnPropertyChanged(nameof(ItemPreviewLineCount));

        bool filterChanged = false;
        if (updateFilter)
        {
            TodoFilter defaultFilter = MapDefaultFilter(settings.TodoDefaultFilter);
            filterChanged = SelectedFilter != defaultFilter;
            SelectedFilter = defaultFilter;
        }
        else if (!IsFilterVisible(SelectedFilter))
        {
            TodoFilter fallbackFilter = MapDefaultFilter(SettingsService.GetFirstVisibleTodoTab(settings));
            filterChanged = SelectedFilter != fallbackFilter;
            SelectedFilter = fallbackFilter;
        }

        if (newTaskPositionChanged && !filterChanged)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
        }
    }

    private static TodoFilter MapDefaultFilter(string? defaultFilter)
    {
        return defaultFilter switch
        {
            SettingsService.TodoDefaultFilterActive => TodoFilter.Active,
            SettingsService.TodoDefaultFilterToday => TodoFilter.Today,
            SettingsService.TodoDefaultFilterThisWeek => TodoFilter.ThisWeek,
            SettingsService.TodoDefaultFilterThisMonth => TodoFilter.ThisMonth,
            SettingsService.TodoDefaultFilterImportant => TodoFilter.Important,
            SettingsService.TodoDefaultFilterCompleted => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static string NormalizeNewTaskPosition(string? value)
    {
        return value == SettingsService.TodoNewTaskPositionBottom
            ? SettingsService.TodoNewTaskPositionBottom
            : SettingsService.TodoNewTaskPositionTop;
    }

    private static DateTimeOffset? NormalizeDueDate(DateTimeOffset? dueDate)
    {
        if (dueDate is not { } value)
        {
            return null;
        }

        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Offset);
    }

    private static bool IsDueToday(TodoItemViewModel item)
    {
        return item.DueDate is { } dueDate &&
               dueDate.ToLocalTime().Date == DateTimeOffset.Now.Date;
    }

    private static bool IsDueThisWeek(TodoItemViewModel item)
    {
        if (item.DueDate is not { } dueDate)
        {
            return false;
        }

        DateTime today = DateTime.Today;
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateTime weekStart = today.AddDays(-daysSinceMonday);
        DateTime nextWeekStart = weekStart.AddDays(7);
        DateTime localDueDate = dueDate.ToLocalTime().Date;
        return localDueDate >= weekStart && localDueDate < nextWeekStart;
    }

    private static bool IsDueThisMonth(TodoItemViewModel item)
    {
        if (item.DueDate is not { } dueDate)
        {
            return false;
        }

        DateTime localDueDate = dueDate.ToLocalTime().Date;
        DateTime today = DateTime.Today;
        return localDueDate.Year == today.Year && localDueDate.Month == today.Month;
    }

    private void ApplyTabVisibilitySettings(AppSettings settings)
    {
        _showTabBar = settings.TodoShowTabBar;
        _showAllTab = settings.TodoShowAllTab;
        _showActiveTab = settings.TodoShowActiveTab;
        _showTodayTab = settings.TodoShowTodayTab;
        _showThisWeekTab = settings.TodoShowThisWeekTab;
        _showThisMonthTab = settings.TodoShowThisMonthTab;
        _showImportantTab = settings.TodoShowImportantTab;
        _showCompletedTab = settings.TodoShowCompletedTab;
        OnPropertyChanged(nameof(TabBarVisibility));
        OnPropertyChanged(nameof(AllTabVisibility));
        OnPropertyChanged(nameof(ActiveTabVisibility));
        OnPropertyChanged(nameof(TodayTabVisibility));
        OnPropertyChanged(nameof(ThisWeekTabVisibility));
        OnPropertyChanged(nameof(ThisMonthTabVisibility));
        OnPropertyChanged(nameof(ImportantTabVisibility));
        OnPropertyChanged(nameof(CompletedTabVisibility));
        OnPropertyChanged(nameof(VisibleTabCount));
    }

    private bool IsFilterVisible(TodoFilter filter) => filter switch
    {
        TodoFilter.Active => _showActiveTab,
        TodoFilter.Today => _showTodayTab,
        TodoFilter.ThisWeek => _showThisWeekTab,
        TodoFilter.ThisMonth => _showThisMonthTab,
        TodoFilter.Important => _showImportantTab,
        TodoFilter.Completed => _showCompletedTab,
        _ => _showAllTab
    };

    private static DateTimeOffset WithEndOfDay(DateTimeOffset date)
    {
        return new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            23,
            59,
            0,
            date.Offset);
    }

    private static int CompareVisibleItems(TodoItemViewModel? left, TodoItemViewModel? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        if (left.IsCompleted != right.IsCompleted)
        {
            return left.IsCompleted ? 1 : -1;
        }

        int sortCompare = left.SortOrder.CompareTo(right.SortOrder);
        return sortCompare != 0
            ? sortCompare
            : right.UpdatedAt.CompareTo(left.UpdatedAt);
    }

    private bool ShouldCountInNonCompletedFilter(TodoItemViewModel item)
    {
        return ShowCompletedTasks || !item.IsCompleted;
    }

    private int GetColorFilterCount(string colorMarker)
    {
        return Items.Count(item =>
            string.Equals(item.ColorMarker, colorMarker, StringComparison.Ordinal) &&
            ShouldCountInNonCompletedFilter(item));
    }

    private void RefreshColorFilterVisibilityProperties()
    {
        OnPropertyChanged(nameof(RedColorFilterVisibility));
        OnPropertyChanged(nameof(OrangeColorFilterVisibility));
        OnPropertyChanged(nameof(YellowColorFilterVisibility));
        OnPropertyChanged(nameof(GreenColorFilterVisibility));
        OnPropertyChanged(nameof(BlueColorFilterVisibility));
        OnPropertyChanged(nameof(PurpleColorFilterVisibility));
        OnPropertyChanged(nameof(TealColorFilterVisibility));
        OnPropertyChanged(nameof(PinkColorFilterVisibility));
    }

    private static string? MapColorFilterToMarker(TodoColorFilter filter)
    {
        return filter switch
        {
            TodoColorFilter.Red => TodoItem.RedColorMarker,
            TodoColorFilter.Orange => TodoItem.OrangeColorMarker,
            TodoColorFilter.Yellow => TodoItem.YellowColorMarker,
            TodoColorFilter.Green => TodoItem.GreenColorMarker,
            TodoColorFilter.Blue => TodoItem.BlueColorMarker,
            TodoColorFilter.Purple => TodoItem.PurpleColorMarker,
            TodoColorFilter.Teal => TodoItem.TealColorMarker,
            TodoColorFilter.Pink => TodoItem.PinkColorMarker,
            _ => null
        };
    }

    private static bool IsRecurringHistoryCandidate(TodoItemViewModel item)
    {
        return item.IsCompleted &&
               item.Recurrence is not null &&
               GetRecurringHistoryGroupKey(item) is not null;
    }

    private static string? GetRecurringHistoryGroupKey(TodoItemViewModel item)
    {
        if (item.Recurrence is null)
        {
            return null;
        }

        string? seriesId = TodoRecurrenceService.NormalizeSeriesId(item.RecurrenceSeriesId);
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            return seriesId;
        }

        if (item.DueDate is not { } dueDate)
        {
            return null;
        }

        string recurrenceMode = TodoRecurrenceMode.Normalize(item.Recurrence.Mode);
        long anchorTicks = (item.Recurrence.AnchorDueDate ?? dueDate).UtcTicks;
        return $"{recurrenceMode}|{anchorTicks}|{item.Text}";
    }

    private static string? NormalizeRecurringHistoryGroupKey(string? groupKey)
    {
        return string.IsNullOrWhiteSpace(groupKey)
            ? null
            : groupKey.Trim();
    }

    private void PruneRecurringHistoryExpansionState(IEnumerable<string> activeGroupKeys)
    {
        var activeKeys = activeGroupKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        _expandedRecurringHistoryGroupKeys.RemoveWhere(groupKey => !activeKeys.Contains(groupKey));
    }

    private string FormatFilterText(string key, int count)
    {
        return $"{_localizationService.T(key)} {count}";
    }

    private int GetUnderlyingInsertIndex(int targetVisibleIndex, TodoItemViewModel movingItem)
    {
        var visibleWithoutMoving = VisibleItems
            .Where(item => !string.Equals(item.Id, movingItem.Id, StringComparison.Ordinal))
            .ToList();
        targetVisibleIndex = Math.Clamp(targetVisibleIndex, 0, visibleWithoutMoving.Count);
        if (targetVisibleIndex >= visibleWithoutMoving.Count)
        {
            return Items.Count;
        }

        return Math.Max(0, Items.IndexOf(visibleWithoutMoving[targetVisibleIndex]));
    }

    private void CaptureUndoSnapshot(string message)
    {
        _undoSnapshot = new TodoUndoSnapshot(
            Items.Select(item => CloneTodoItem(item.Item)).ToList(),
            message);
        RefreshUndoProperties();
    }

    private void RefreshUndoProperties()
    {
        OnPropertyChanged(nameof(CanUndoLastAction));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(UndoBarVisibility));
    }

    private static TodoItem CloneTodoItem(TodoItem item)
    {
        return new TodoItem
        {
            Id = item.Id,
            Text = item.Text,
            IsCompleted = item.IsCompleted,
            IsImportant = item.IsImportant,
            ColorMarker = item.ColorMarker,
            DueDate = item.DueDate,
            Recurrence = item.Recurrence?.Clone(),
            Steps = item.Steps.Select(step => new TodoStep
            {
                Id = step.Id,
                Text = step.Text,
                IsCompleted = step.IsCompleted,
                SortOrder = step.SortOrder
            }).ToList(),
            Notes = item.Notes,
            Attachments = item.Attachments.Select(attachment => new TodoAttachment
            {
                Id = attachment.Id,
                FilePath = attachment.FilePath,
                DisplayName = attachment.DisplayName,
                Type = attachment.Type,
                StorageMode = attachment.StorageMode,
                AddedAt = attachment.AddedAt
            }).ToList(),
            CompletedAt = item.CompletedAt,
            ReminderLastNotifiedAt = item.ReminderLastNotifiedAt,
            ReminderDismissedForDueDate = item.ReminderDismissedForDueDate,
            ReminderOffsetMinutes = item.ReminderOffsetMinutes,
            SnoozedUntil = item.SnoozedUntil,
            SnoozeLastNotifiedAt = item.SnoozeLastNotifiedAt,
            RecurrenceSeriesId = item.RecurrenceSeriesId,
            GeneratedNextItemId = item.GeneratedNextItemId,
            SortOrder = item.SortOrder,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static bool AreRecurrenceEqual(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        return string.Equals(leftMode, rightMode, StringComparison.Ordinal) &&
               Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate);
    }

    private static string NormalizeText(string? text)
    {
        return text?.Trim() ?? string.Empty;
    }
}
