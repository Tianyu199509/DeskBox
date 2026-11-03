using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    public async Task InitializeAsync()
    {
        var data = await _store.LoadAsync();

        SelectedDetailItem = null;
        IsCreatingDetailItem = false;
        _undoSnapshot = null;
        Items.Clear();
        _tombstones.Clear();
        foreach (var item in data.Items.OrderBy(item => item.SortOrder).ThenByDescending(item => item.UpdatedAt))
        {
            if (item.IsDeleted)
            {
                _tombstones.Add(item);
                continue;
            }

            Items.Add(new TodoItemViewModel(item, _localizationService, PreviewCacheScope));
        }

        NormalizeSortOrders();
        ApplySettings(updateFilter: true);
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        IsInitialized = true;
        await ReclaimUnreferencedAttachmentsAsync(startupSweep: true);
    }

    public async Task<TodoItemViewModel?> AddInputAsync()
    {
        ResolveDraftContextMetadata(
            out bool isImportant,
            out DateTimeOffset? dueDate,
            out bool hasDueTime,
            out string? colorMarker);
        var item = await AddItemAsync(InputText, isImportant, dueDate, colorMarker, hasDueTime);
        if (item is not null)
        {
            InputText = string.Empty;
            DraftImportant = false;
            SetDraftDueDate(null, hasDueTime: false);
            if (SelectedFilter == TodoFilter.Overdue && !item.IsOverdue)
            {
                SelectedFilter = _showAllTab
                    ? TodoFilter.All
                    : _showActiveTab
                        ? TodoFilter.Active
                        : _showTodayTab && IsInTodayView(item)
                            ? TodoFilter.Today
                            : SelectedFilter;
            }
        }

        return item;
    }

    public async Task<TodoItemViewModel?> AddItemAsync(
        string? text,
        bool isImportant = false,
        DateTimeOffset? dueDate = null,
        string? colorMarker = null,
        bool hasDueTime = true)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate, hasDueTime);
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalizedText,
            IsImportant = isImportant,
            DueDate = normalizedDueDate,
            HasDueTime = normalizedDueDate is not null && hasDueTime,
            ColorMarker = TodoItem.NormalizeColorMarker(colorMarker),
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService, PreviewCacheScope);

        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(viewModel);
        }
        else
        {
            Items.Insert(0, viewModel);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return viewModel;
    }

    public async Task<bool> UpdateItemTextAsync(string itemId, string? text)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        if (string.Equals(item.Text, normalizedText, StringComparison.Ordinal))
        {
            return true;
        }

        item.Text = normalizedText;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetCompletedAsync(string itemId, bool isCompleted)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (item.IsCompleted == isCompleted)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        if (isCompleted)
        {
            if (item.Recurrence is not null)
            {
                item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
            }

            item.IsCompleted = true;
            item.CompletedAt = now;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.Item.GeneratedNextItemId = null;

            if (TodoRecurrenceService.TryCreateNextOccurrence(item.Item, now, out TodoItem? nextItem) &&
                nextItem is not null)
            {
                item.Item.GeneratedNextItemId = nextItem.Id;
                int currentIndex = Items.IndexOf(item);
                var nextViewModel = new TodoItemViewModel(nextItem, _localizationService, PreviewCacheScope);
                Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), nextViewModel);
            }
        }
        else
        {
            TodoItemViewModel? generatedOccurrence = FindGeneratedOccurrence(item);
            if (generatedOccurrence is not null &&
                TodoRecurrenceService.ShouldRemoveGeneratedOccurrence(item.Item, generatedOccurrence.Item))
            {
                Items.Remove(generatedOccurrence);
            }

            item.IsCompleted = false;
            item.CompletedAt = null;
            item.Item.GeneratedNextItemId = null;
        }

        item.UpdatedAt = now;
        RefreshVisibleItems(item);
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> SetImportantAsync(string itemId, bool isImportant)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (item.IsImportant == isImportant)
        {
            return true;
        }

        item.IsImportant = isImportant;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetImportantAsync(IEnumerable<string> itemIds, bool isImportant)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id) && item.IsImportant != isImportant))
        {
            item.IsImportant = isImportant;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public bool CanApplyTabDrop(IEnumerable<string> itemIds, TodoFilter target)
    {
        var selectedIds = itemIds.ToHashSet(StringComparer.Ordinal);
        return target switch
        {
            TodoFilter.Active => Items.Any(item => selectedIds.Contains(item.Id) && item.IsCompleted),
            TodoFilter.Today => Items.Any(item => selectedIds.Contains(item.Id) && !IsDueToday(item)),
            TodoFilter.Important => Items.Any(item => selectedIds.Contains(item.Id) && !item.IsImportant),
            TodoFilter.Completed => Items.Any(item => selectedIds.Contains(item.Id) && !item.IsCompleted),
            _ => false
        };
    }

    public async Task<int> ApplyTabDropAsync(IEnumerable<string> itemIds, TodoFilter target)
    {
        string[] selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!CanApplyTabDrop(selectedIds, target))
        {
            return 0;
        }

        if (target == TodoFilter.Today)
        {
            return await SetDueDatePresetAsync(selectedIds, TodoDuePreset.Today);
        }

        if (target == TodoFilter.Important)
        {
            return await SetImportantAsync(selectedIds, true);
        }

        int changedCount = 0;
        bool completed = target == TodoFilter.Completed;
        foreach (string itemId in selectedIds)
        {
            TodoItemViewModel? item = FindItem(itemId);
            if (item is null || item.IsCompleted == completed)
            {
                continue;
            }

            if (await SetCompletedAsync(itemId, completed))
            {
                changedCount++;
            }
        }

        return changedCount;
    }

    public async Task<bool> SetColorMarkerAsync(string itemId, string? colorMarker)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        if (string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal))
        {
            return true;
        }

        item.ColorMarker = normalizedColorMarker;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetColorMarkerAsync(IEnumerable<string> itemIds, string? colorMarker)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        string? normalizedColorMarker = TodoItem.NormalizeColorMarker(colorMarker);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            if (string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal))
            {
                continue;
            }

            item.ColorMarker = normalizedColorMarker;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetDueDateAsync(string itemId, DateTimeOffset? dueDate, bool? hasDueTime = null)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        bool resolvedHasDueTime = dueDate is not null && (hasDueTime ?? true);
        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate, resolvedHasDueTime);
        TodoRecurrence? updatedRecurrence = normalizedDueDate is null
            ? null
            : item.Recurrence?.Clone();
        if (updatedRecurrence is not null)
        {
            updatedRecurrence.AnchorDueDate = normalizedDueDate;
        }

        if (Nullable.Equals(item.DueDate, normalizedDueDate) &&
            item.HasDueTime == resolvedHasDueTime &&
            AreRecurrenceEqual(item.Recurrence, updatedRecurrence) &&
            (normalizedDueDate is not null || item.ReminderOffsetMinutes is null))
        {
            return true;
        }

        item.DueDate = normalizedDueDate;
        item.HasDueTime = resolvedHasDueTime;
        item.Recurrence = updatedRecurrence;
        if (normalizedDueDate is null)
        {
            item.ReminderOffsetMinutes = null;
        }
        if (updatedRecurrence is null)
        {
            item.RecurrenceSeriesId = null;
            item.Item.GeneratedNextItemId = null;
        }
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetDueDateAsync(IEnumerable<string> itemIds, DateTimeOffset? dueDate, bool? hasDueTime = null)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        bool resolvedHasDueTime = dueDate is not null && (hasDueTime ?? true);
        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate, resolvedHasDueTime);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            TodoRecurrence? updatedRecurrence = normalizedDueDate is null
                ? null
                : item.Recurrence?.Clone();
            if (updatedRecurrence is not null)
            {
                updatedRecurrence.AnchorDueDate = normalizedDueDate;
            }

            if (Nullable.Equals(item.DueDate, normalizedDueDate) &&
                item.HasDueTime == resolvedHasDueTime &&
                AreRecurrenceEqual(item.Recurrence, updatedRecurrence) &&
                (normalizedDueDate is not null || item.ReminderOffsetMinutes is null))
            {
                continue;
            }

            item.DueDate = normalizedDueDate;
            item.HasDueTime = resolvedHasDueTime;
            item.Recurrence = updatedRecurrence;
            if (normalizedDueDate is null)
            {
                item.ReminderOffsetMinutes = null;
            }
            if (updatedRecurrence is null)
            {
                item.RecurrenceSeriesId = null;
                item.Item.GeneratedNextItemId = null;
            }
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetRecurrenceAsync(string itemId, string? mode)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, item.DueDate);
        if (AreRecurrenceEqual(item.Recurrence, recurrence))
        {
            return true;
        }

        if (item.DueDate is null && recurrence is not null)
        {
            return false;
        }

        item.Recurrence = recurrence;
        item.RecurrenceSeriesId = recurrence is null
            ? null
            : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
        item.Item.GeneratedNextItemId = null;
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetRecurrenceAsync(IEnumerable<string> itemIds, string? mode)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)))
        {
            TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, item.DueDate);
            if (item.DueDate is null && recurrence is not null)
            {
                continue;
            }

            if (AreRecurrenceEqual(item.Recurrence, recurrence))
            {
                continue;
            }

            item.Recurrence = recurrence;
            item.RecurrenceSeriesId = recurrence is null
                ? null
                : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
            item.Item.GeneratedNextItemId = null;
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SetReminderOffsetAsync(string itemId, int? offsetMinutes)
    {
        var item = FindItem(itemId);
        if (item is null || item.DueDate is null)
        {
            return false;
        }

        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        if (Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset))
        {
            return true;
        }

        item.ReminderOffsetMinutes = normalizedOffset;
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        if (TodoReminderOptions.IsReminderOff(normalizedOffset))
        {
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetReminderOffsetAsync(IEnumerable<string> itemIds, int? offsetMinutes)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        int? normalizedOffset = TodoReminderOptions.NormalizeOffsetMinutes(offsetMinutes);
        var now = DateTimeOffset.UtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id) && item.DueDate is not null))
        {
            if (Nullable.Equals(item.ReminderOffsetMinutes, normalizedOffset))
            {
                continue;
            }

            item.ReminderOffsetMinutes = normalizedOffset;
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            if (TodoReminderOptions.IsReminderOff(normalizedOffset))
            {
                item.SnoozedUntil = null;
                item.Item.SnoozeLastNotifiedAt = null;
            }

            item.UpdatedAt = now;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
        }

        return updatedCount;
    }

    public async Task<bool> SnoozeReminderAsync(string itemId, TimeSpan snoozeFor)
    {
        var item = FindItem(itemId);
        if (item is null ||
            item.IsCompleted ||
            item.DueDate is null ||
            snoozeFor <= TimeSpan.Zero)
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        item.SnoozedUntil = now.Add(snoozeFor);
        item.Item.SnoozeLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = item.DueDate;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public Task<bool> SnoozeReminderUntilAsync(string itemId, DateTimeOffset snoozedUntil) =>
        SnoozeReminderUntilAsync(itemId, snoozedUntil, DateTimeOffset.Now, rejectReminderOff: false);

    internal async Task<bool> SnoozeReminderUntilAsync(
        string itemId,
        DateTimeOffset snoozedUntil,
        DateTimeOffset now,
        bool rejectReminderOff)
    {
        var item = FindItem(itemId);
        if (item is null ||
            item.IsCompleted ||
            item.DueDate is null ||
            (rejectReminderOff && TodoReminderOptions.IsReminderOff(item.ReminderOffsetMinutes)) ||
            snoozedUntil <= now)
        {
            return false;
        }

        item.SnoozedUntil = snoozedUntil;
        item.Item.SnoozeLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = item.DueDate;
        item.UpdatedAt = now.ToUniversalTime();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }
}
