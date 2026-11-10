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

        TextSize = SettingsService.NormalizeTextSize(
            (_settingsService.Settings.TodoListTextSize > 0 ? _settingsService.Settings.TodoListTextSize : _settingsService.Settings.TextSize));
        ContentTextSize = SettingsService.NormalizeTextSize(
            (_settingsService.Settings.TodoContentTextSize > 0 ? _settingsService.Settings.TodoContentTextSize : _settingsService.Settings.TextSize));
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

    internal TodoItemViewModel? FindItem(string itemId)  // internal: TodoReminderService single-writer routing (R15)
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

    private TodoItemViewModel? FindDeferredGeneratedOccurrence(TodoItemViewModel item)
    {
        if (!item.IsCompleted || string.IsNullOrWhiteSpace(item.Item.GeneratedNextItemId))
        {
            return null;
        }

        TodoItemViewModel? generated = FindItem(item.Item.GeneratedNextItemId);
        return generated is not null && IsDeferredFutureOccurrence(generated)
            ? generated
            : null;
    }

    private bool IsDeferredFutureOccurrence(TodoItemViewModel item) =>
        TodoRecurrenceService.IsDeferredOccurrence(item.Item, CurrentLocalDate);

    private bool IsSameDayCompletedRecurringItem(TodoItemViewModel item)
    {
        return item.IsCompleted &&
               item.HasRecurrence &&
               !string.IsNullOrWhiteSpace(item.Item.GeneratedNextItemId) &&
               item.CompletedAt is { } completedAt &&
               completedAt.ToLocalTime().Date == CurrentLocalDate;
    }

    private bool IsSameDayCompletedRecurringInTodayView(TodoItemViewModel item)
    {
        return IsSameDayCompletedRecurringItem(item) &&
               item.DueDate is { } dueDate &&
               dueDate.ToLocalTime().Date <= CurrentLocalDate;
    }

    public bool RefreshIfDateChanged()
    {
        DateTime currentLocalDate = CurrentLocalDate;
        if (currentLocalDate == _lastEvaluatedLocalDate)
        {
            return false;
        }

        _lastEvaluatedLocalDate = currentLocalDate;
        foreach (TodoItemViewModel item in Items)
        {
            item.RefreshDateDependentProperties();
        }

        RefreshVisibleItems();
        RefreshCountProperties();
        return true;
    }

    internal async Task SaveAsync()  // internal: TodoReminderService single-writer routing (R15)
    {
        NormalizeSortOrders();
        var liveIds = new HashSet<string>(
            Items.Select(item => item.Item.Id),
            StringComparer.Ordinal);
        // Tombstones persist alongside live items so merge restores cannot
        // resurrect deleted entries; a live item always shadows its stub.
        var persistedTombstones = _tombstones
            .Where(item => !liveIds.Contains(item.Id))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(MaxRetainedTombstones)
            .ToList();
        _tombstones.Clear();
        _tombstones.AddRange(persistedTombstones);
        await _store.SaveAsync(new TodoWidgetData
        {
            Items = Items.Select(item => item.Item).Concat(persistedTombstones).ToList()
        });
    }

    private void RefreshVisibleItems(TodoItemViewModel? preferredMovedItem = null)
    {
        ResetRecurringHistoryState();
        ResetTodayGroupHeaderState();

        var filteredItems = Items.Where(ShouldShowItem).ToList();
        filteredItems.Sort((left, right) => CompareVisibleItemsForFilter(left, right, SelectedFilter));

        var activeItems = filteredItems
            .Where(item => !item.IsCompleted &&
                           (SelectedFilter != TodoFilter.Today ||
                            _isTodayOverdueExpanded ||
                            !IsOverdueFromPreviousDays(item)))
            .ToList();

        if (SelectedFilter == TodoFilter.Today &&
            _isTodayOverdueExpanded &&
            activeItems.Any(IsOverdueFromPreviousDays))
        {
            TodoItemViewModel? firstTodayActive = activeItems.FirstOrDefault(item => !IsOverdueFromPreviousDays(item));
            firstTodayActive?.UpdateTodayGroupHeaderState(true);
        }

        var completedItems = filteredItems
            .Where(item => item.IsCompleted)
            .ToList();

        var desiredItems = new List<TodoItemViewModel>(filteredItems.Count);
        desiredItems.AddRange(activeItems);
        desiredItems.AddRange(BuildCompletedVisibleItems(completedItems));

        SynchronizeVisibleItems(desiredItems, preferredMovedItem);
        _cachedVisibleItemsSource = null;
        OnPropertyChanged(nameof(VisibleItemsSource));
        RefreshVisibleStateProperties();
    }

    private void SynchronizeVisibleItems(
        IReadOnlyList<TodoItemViewModel> desiredItems,
        TodoItemViewModel? preferredMovedItem)
    {
        var desiredSet = desiredItems.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int index = VisibleItems.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(VisibleItems[index]))
            {
                VisibleItems.RemoveAt(index);
            }
        }

        if (preferredMovedItem is not null)
        {
            int currentIndex = VisibleItems.IndexOf(preferredMovedItem);
            int desiredIndex = IndexOfReference(desiredItems, preferredMovedItem);
            if (currentIndex >= 0 && desiredIndex >= 0 && currentIndex != desiredIndex)
            {
                VisibleItems.Move(
                    currentIndex,
                    Math.Min(desiredIndex, VisibleItems.Count - 1));
            }
        }

        for (int desiredIndex = 0; desiredIndex < desiredItems.Count; desiredIndex++)
        {
            TodoItemViewModel desiredItem = desiredItems[desiredIndex];
            if (desiredIndex < VisibleItems.Count &&
                ReferenceEquals(VisibleItems[desiredIndex], desiredItem))
            {
                continue;
            }

            int currentIndex = VisibleItems.IndexOf(desiredItem);
            if (currentIndex >= 0)
            {
                VisibleItems.Move(currentIndex, desiredIndex);
            }
            else
            {
                VisibleItems.Insert(desiredIndex, desiredItem);
            }
        }
    }

    private static int IndexOfReference(
        IReadOnlyList<TodoItemViewModel> items,
        TodoItemViewModel target)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private void ResetRecurringHistoryState()
    {
        foreach (var item in Items)
        {
            item.UpdateRecurringHistoryState(isLead: false, isExpanded: false, itemCount: 0);
        }
    }

    private void ResetTodayGroupHeaderState()
    {
        foreach (var item in Items)
        {
            item.UpdateTodayGroupHeaderState(false);
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

    private bool MatchesActiveColorFilter(TodoItemViewModel item)
    {
        string? selectedColorMarker = MapColorFilterToMarker(SelectedColorFilter);
        return selectedColorMarker is null ||
               string.Equals(item.ColorMarker, selectedColorMarker, StringComparison.Ordinal);
    }

    private bool MatchesSearchQuery(TodoItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return true;
        }

        string query = _searchQuery.Trim();
        if (item.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Notes) &&
            item.Notes.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (TodoStepViewModel step in item.Steps)
        {
            if (step.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldShowItem(TodoItemViewModel item)
    {
        if (!MatchesSearchQuery(item))
        {
            return false;
        }

        if (!MatchesActiveColorFilter(item))
        {
            return false;
        }

        if (IsDeferredFutureOccurrence(item))
        {
            return SelectedFilter switch
            {
                TodoFilter.ThisWeek => IsDueThisWeek(item),
                TodoFilter.ThisMonth => IsDueThisMonth(item),
                _ => false
            };
        }

        if (!ShowCompletedTasks &&
            item.IsCompleted &&
            SelectedFilter != TodoFilter.Completed &&
            !IsSameDayCompletedRecurringItem(item))
        {
            return false;
        }

        return SelectedFilter switch
        {
            TodoFilter.Active => !item.IsCompleted,
            TodoFilter.Today => IsInTodayView(item) || IsSameDayCompletedRecurringInTodayView(item),
            TodoFilter.Overdue => item.IsOverdue,
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
        OnPropertyChanged(nameof(TodayDueOnlyCount));
        OnPropertyChanged(nameof(TodayFilterCount));
        OnPropertyChanged(nameof(OverdueFilterCount));
        OnPropertyChanged(nameof(TodayOverdueCount));
        OnPropertyChanged(nameof(TodayOverdueBarVisibility));
        OnPropertyChanged(nameof(TodayOverdueHeaderText));
        OnPropertyChanged(nameof(OverdueTabSummaryText));
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
        OnPropertyChanged(nameof(OverdueFilterText));
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
        OnPropertyChanged(nameof(TodayOverdueCount));
        OnPropertyChanged(nameof(TodayOverdueBarVisibility));
        OnPropertyChanged(nameof(TodayOverdueHeaderText));
        OnPropertyChanged(nameof(OverdueTabSummaryText));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(ColorFilterVisibility));
        RefreshColorFilterVisibilityProperties();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AddPlaceholderText));
        OnPropertyChanged(nameof(ExpandInputTooltipText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(QuickComposerHintText));
        OnPropertyChanged(nameof(DraftImportantTooltipText));
        OnPropertyChanged(nameof(DraftDueDateText));
        OnPropertyChanged(nameof(DraftRecurrenceText));
        OnPropertyChanged(nameof(DraftRecurrenceTooltipText));
        OnPropertyChanged(nameof(TodayOverdueHeaderText));
        OnPropertyChanged(nameof(OverdueTabSummaryText));
        OnPropertyChanged(nameof(TodayOverdueToggleTooltipText));
        OnPropertyChanged(nameof(RescheduleOverdueText));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(ActiveFilterText));
        OnPropertyChanged(nameof(TodayFilterText));
        OnPropertyChanged(nameof(OverdueFilterText));
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
        OnPropertyChanged(nameof(DetailSaveText));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(RenameText));
        OnPropertyChanged(nameof(ConvertToQuickCaptureText));
        OnPropertyChanged(nameof(DueDateText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(CloseSearchText));
        OnPropertyChanged(nameof(ClearSearchText));
        OnPropertyChanged(nameof(ShowTodayOverdueSection));
        OnPropertyChanged(nameof(DetailAddFileText));
        OnPropertyChanged(nameof(DetailRemoveAttachmentText));
        OnPropertyChanged(nameof(DetailFileMissingText));
        OnPropertyChanged(nameof(DetailStepsText));
        OnPropertyChanged(nameof(DetailAddStepText));
        OnPropertyChanged(nameof(DetailNotesText));
        OnPropertyChanged(nameof(DetailNotesPlaceholderText));
        OnPropertyChanged(nameof(DetailNotesEditText));
        OnPropertyChanged(nameof(DetailNotesDoneText));
        OnPropertyChanged(nameof(DetailNotesRetryText));
        OnPropertyChanged(nameof(DetailNotesSaveFailedText));
        OnPropertyChanged(nameof(DetailMoreActionsText));
        OnPropertyChanged(nameof(DetailResizePanesText));
        OnPropertyChanged(nameof(DetailResizeTitleEditorText));
        OnPropertyChanged(nameof(WideDetailEmptyTitle));
        OnPropertyChanged(nameof(WideDetailEmptyText));
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
        OnPropertyChanged(nameof(LayoutPreference));
        OnPropertyChanged(nameof(UseWideDetailPane));
        OnPropertyChanged(nameof(AutoSelectFirstInWideLayout));
        OnPropertyChanged(nameof(ItemPreviewLineCount));
        OnPropertyChanged(nameof(EditorEnterBehavior));
        OnPropertyChanged(nameof(QuickComposerHintText));

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
            SettingsService.TodoDefaultFilterOverdue => TodoFilter.Overdue,
            SettingsService.TodoDefaultFilterThisWeek => TodoFilter.ThisWeek,
            SettingsService.TodoDefaultFilterThisMonth => TodoFilter.ThisMonth,
            SettingsService.TodoDefaultFilterImportant => TodoFilter.Important,
            SettingsService.TodoDefaultFilterCompleted => TodoFilter.Completed,
            _ => TodoFilter.All
        };
    }

    private static string NormalizeNewTaskPosition(string? value)
    {
        return SettingsService.NormalizeTodoNewTaskPosition(value);
    }

    private static DateTimeOffset? NormalizeDueDate(DateTimeOffset? dueDate, bool hasDueTime = true)
    {
        if (dueDate is not { } value)
        {
            return null;
        }

        if (!hasDueTime)
        {
            return TodoWidgetStore.NormalizeAllDayDueDate(value);
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

    private DateTimeOffset? ResolvePresetDueDate(TodoDuePreset preset)
    {
        DateTimeOffset today = TodoWidgetStore.NormalizeAllDayDueDate(_clock());
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        return preset switch
        {
            TodoDuePreset.Today => today,
            TodoDuePreset.Tomorrow => TodoWidgetStore.NormalizeAllDayDueDate(today.AddDays(1)),
            TodoDuePreset.ThisWeek => TodoWidgetStore.NormalizeAllDayDueDate(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => TodoWidgetStore.NormalizeAllDayDueDate(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };
    }

    private bool IsDueToday(TodoItemViewModel item)
    {
        return item.DueDate is { } dueDate &&
               dueDate.ToLocalTime().Date == CurrentLocalDate;
    }

    internal bool IsOverdueFromPreviousDays(TodoItemViewModel item)
    {
        return IsOverdueFromPreviousDays(item, CurrentLocalDate);
    }

    internal static bool IsOverdueFromPreviousDays(TodoItemViewModel item, DateTime localToday)
    {
        return !item.IsCompleted &&
               item.DueDate is { } dueDate &&
               dueDate.ToLocalTime().Date < localToday.Date;
    }

    private bool IsInTodayView(TodoItemViewModel item)
    {
        return IsDueToday(item) || IsOverdueFromPreviousDays(item);
    }

    private bool IsDueThisWeek(TodoItemViewModel item)
    {
        if (item.DueDate is not { } dueDate)
        {
            return false;
        }

        DateTime today = CurrentLocalDate;
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateTime weekStart = today.AddDays(-daysSinceMonday);
        DateTime nextWeekStart = weekStart.AddDays(7);
        DateTime localDueDate = dueDate.ToLocalTime().Date;
        return localDueDate >= weekStart && localDueDate < nextWeekStart;
    }

    private bool IsDueThisMonth(TodoItemViewModel item)
    {
        if (item.DueDate is not { } dueDate)
        {
            return false;
        }

        DateTime localDueDate = dueDate.ToLocalTime().Date;
        DateTime today = CurrentLocalDate;
        return localDueDate.Year == today.Year && localDueDate.Month == today.Month;
    }

    private void ApplyTabVisibilitySettings(AppSettings settings)
    {
        _showTabBar = settings.TodoShowTabBar;
        _showAllTab = settings.TodoShowAllTab;
        _showActiveTab = settings.TodoShowActiveTab;
        _showTodayTab = settings.TodoShowTodayTab;
        _showOverdueTab = settings.Todo.TodoShowOverdueTab;
        _showThisWeekTab = settings.TodoShowThisWeekTab;
        _showThisMonthTab = settings.TodoShowThisMonthTab;
        _showImportantTab = settings.TodoShowImportantTab;
        _showCompletedTab = settings.TodoShowCompletedTab;
        OnPropertyChanged(nameof(TabBarVisibility));
        OnPropertyChanged(nameof(AllTabVisibility));
        OnPropertyChanged(nameof(ActiveTabVisibility));
        OnPropertyChanged(nameof(TodayTabVisibility));
        OnPropertyChanged(nameof(OverdueTabVisibility));
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
        TodoFilter.Overdue => _showOverdueTab,
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

    private int CompareVisibleItemsForFilter(
        TodoItemViewModel? left,
        TodoItemViewModel? right,
        TodoFilter filter)
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

        if (filter == TodoFilter.Today && !left.IsCompleted && !right.IsCompleted)
        {
            bool leftOverdue = IsOverdueFromPreviousDays(left);
            bool rightOverdue = IsOverdueFromPreviousDays(right);
            if (leftOverdue != rightOverdue)
            {
                return leftOverdue ? -1 : 1;
            }
        }

        return CompareVisibleItems(left, right);
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

        if (left.IsCompleted && right.IsCompleted)
        {
            int completedCompare = Nullable.Compare(right.CompletedAt, left.CompletedAt);
            if (completedCompare != 0)
            {
                return completedCompare;
            }
        }

        int sortCompare = left.SortOrder.CompareTo(right.SortOrder);
        return sortCompare != 0
            ? sortCompare
            : right.UpdatedAt.CompareTo(left.UpdatedAt);
    }

    private bool ShouldCountInNonCompletedFilter(TodoItemViewModel item)
    {
        return ShowCompletedTasks || !item.IsCompleted || IsSameDayCompletedRecurringItem(item);
    }

    private int GetColorFilterCount(string colorMarker)
    {
        return Items.Count(item =>
            !IsDeferredFutureOccurrence(item) &&
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

    private void CaptureDeleteUndoSnapshot(IReadOnlyList<TodoItemViewModel> deletedItems, string message)
    {
        var deletedSet = deletedItems.ToHashSet(ReferenceEqualityComparer.Instance);
        var entries = new List<TodoUndoDeletedEntry>(deletedItems.Count);
        for (int index = 0; index < Items.Count; index++)
        {
            TodoItemViewModel candidate = Items[index];
            if (deletedSet.Contains(candidate))
            {
                entries.Add(new TodoUndoDeletedEntry(index, TodoWidgetStore.CloneItem(candidate.Item)));
            }
        }

        _undoSnapshot = new TodoUndoSnapshot(entries, message);
        RefreshUndoProperties();
    }

    private void RefreshUndoProperties()
    {
        OnPropertyChanged(nameof(CanUndoLastAction));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(UndoBarVisibility));
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
