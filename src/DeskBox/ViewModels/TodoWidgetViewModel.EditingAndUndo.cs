using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    public void ToggleRecurringHistoryGroup(string? groupKey)
    {
        string? normalizedGroupKey = NormalizeRecurringHistoryGroupKey(groupKey);
        if (normalizedGroupKey is null)
        {
            return;
        }

        if (!_expandedRecurringHistoryGroupKeys.Add(normalizedGroupKey))
        {
            _expandedRecurringHistoryGroupKeys.Remove(normalizedGroupKey);
        }

        RefreshVisibleItems();
        RefreshCountProperties();
    }

    public async Task<bool> SetDueDatePresetAsync(string itemId, TodoDuePreset preset)
    {
        DateTimeOffset? dueDate = ResolvePresetDueDate(preset);
        return await SetDueDateAsync(itemId, dueDate, hasDueTime: false);
    }

    public Task<int> SetDueDatePresetAsync(IEnumerable<string> itemIds, TodoDuePreset preset)
    {
        DateTimeOffset? dueDate = ResolvePresetDueDate(preset);
        return SetDueDateAsync(itemIds, dueDate, hasDueTime: false);
    }

    public void BeginEdit(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return;
        }

        foreach (var other in Items.Where(other => !string.Equals(other.Id, itemId, StringComparison.Ordinal)))
        {
            other.CancelEdit();
        }

        item.BeginEdit();
    }

    public void CancelEdit(string itemId)
    {
        FindItem(itemId)?.CancelEdit();
    }

    public async Task<bool> CommitEditAsync(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        bool updated = await UpdateItemTextAsync(itemId, item.EditText);
        if (updated)
        {
            item.IsEditing = false;
        }

        return updated;
    }

    public async Task<bool> DeleteItemAsync(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (IsCreatingDetailItem &&
            string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal))
        {
            item.CancelEdit();
            SelectedDetailItem = null;
            IsCreatingDetailItem = false;
            return true;
        }

        var itemsToDelete = new List<TodoItemViewModel> { item };
        if (FindDeferredGeneratedOccurrence(item) is { } deferred)
        {
            itemsToDelete.Add(deferred);
        }

        CaptureDeleteUndoSnapshot(itemsToDelete, _localizationService.T("Todo.Undo.Deleted"));
        if (SelectedDetailItem is not null &&
            itemsToDelete.Any(entry => string.Equals(entry.Id, SelectedDetailItem.Id, StringComparison.Ordinal)))
        {
            SelectedDetailItem = null;
        }
        foreach (TodoItemViewModel entry in itemsToDelete)
        {
            TombstoneItem(entry);
        }
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        await ReclaimUnreferencedAttachmentsAsync();
        return true;
    }

    public async Task<int> DeleteItemsAsync(IEnumerable<string> itemIds)
    {
        var selectedIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var itemsToDelete = Items
            .Where(item => selectedIds.Contains(item.Id))
            .ToList();
        if (itemsToDelete.Count == 0)
        {
            return 0;
        }

        int userSelectedCount = itemsToDelete.Count;
        foreach (TodoItemViewModel selectedItem in itemsToDelete.ToArray())
        {
            if (FindDeferredGeneratedOccurrence(selectedItem) is { } deferred &&
                !itemsToDelete.Contains(deferred))
            {
                itemsToDelete.Add(deferred);
            }
        }

        CaptureDeleteUndoSnapshot(itemsToDelete, _localizationService.Format("Todo.Undo.DeletedSelected", userSelectedCount));
        if (SelectedDetailItem is not null &&
            itemsToDelete.Any(entry => string.Equals(entry.Id, SelectedDetailItem.Id, StringComparison.Ordinal)))
        {
            SelectedDetailItem = null;
        }

        foreach (var item in itemsToDelete)
        {
            TombstoneItem(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        await ReclaimUnreferencedAttachmentsAsync();
        return userSelectedCount;
    }

    public async Task<int> ClearCompletedAsync()
    {
        var completedItems = Items.Where(item => item.IsCompleted).ToList();
        if (completedItems.Count == 0)
        {
            return 0;
        }

        CaptureDeleteUndoSnapshot(completedItems, _localizationService.Format("Todo.Undo.ClearedCompleted", completedItems.Count));
        foreach (var item in completedItems)
        {
            TombstoneItem(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        await ReclaimUnreferencedAttachmentsAsync();
        return completedItems.Count;
    }

    public async Task<int> ClearAllAsync()
    {
        if (Items.Count == 0)
        {
            return 0;
        }

        int removedCount = Items.Count;
        CaptureDeleteUndoSnapshot(Items, _localizationService.Format("Todo.Undo.ClearedAll", removedCount));
        var clearedAt = DateTimeOffset.UtcNow;
        foreach (var item in Items)
        {
            item.ReleaseAttachmentThumbnails();
            item.Item.IsDeleted = true;
            item.Item.UpdatedAt = clearedAt;
            TodoWidgetStore.StripTombstonePayload(item.Item);
            _tombstones.Add(item.Item);
        }

        Items.Clear();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        await ReclaimUnreferencedAttachmentsAsync();
        return removedCount;
    }

    public async Task<bool> MoveItemAsync(string itemId, int targetVisibleIndex)
    {
        var item = FindItem(itemId);
        if (item is null || targetVisibleIndex < 0)
        {
            return false;
        }

        int currentIndex = Items.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        int targetIndex = GetUnderlyingInsertIndex(targetVisibleIndex, item);
        if (targetIndex == currentIndex)
        {
            return true;
        }

        Items.RemoveAt(currentIndex);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Items.Count);
        Items.Insert(targetIndex, item);
        NormalizeSortOrders();
        RefreshVisibleItems();
        await SaveAsync();
        return true;
    }

    public void ToggleTodayOverdueExpanded()
    {
        _isTodayOverdueExpanded = !_isTodayOverdueExpanded;
        OnPropertyChanged(nameof(IsTodayOverdueExpanded));
        OnPropertyChanged(nameof(TodayOverdueExpandGlyph));
        RefreshVisibleItems();
    }

    public Task<int> RollOverdueToTodayAsync() => RescheduleOverdueToTodayAsync();

    public async Task<int> RescheduleOverdueToTodayAsync()
    {
        var targets = Items
            .Where(item => IsOverdueFromPreviousDays(item) && MatchesActiveColorFilter(item))
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        DateTimeOffset now = _clock().ToLocalTime();
        DateTime todayDate = now.Date;
        DateTimeOffset todayAllDay = TodoWidgetStore.NormalizeAllDayDueDate(now);
        DateTimeOffset utcNow = now.ToUniversalTime();
        var modifiedEntries = new List<TodoUndoModifiedEntry>(targets.Count);

        foreach (TodoItemViewModel item in targets)
        {
            modifiedEntries.Add(new TodoUndoModifiedEntry(
                item.Id,
                item.DueDate,
                item.HasDueTime,
                item.Item.ReminderDismissedForDueDate,
                item.SnoozedUntil,
                item.Recurrence?.Clone()));

            bool keepExplicitTime = item.HasDueTime && item.DueDate is not null;
            DateTimeOffset nextDueDate;
            if (keepExplicitTime)
            {
                DateTimeOffset localOld = item.DueDate!.Value.ToLocalTime();
                TimeZoneInfo localZone = TimeZoneInfo.Local;
                DateTime unspecifiedToday = new(
                    todayDate.Year,
                    todayDate.Month,
                    todayDate.Day,
                    localOld.Hour,
                    localOld.Minute,
                    localOld.Second,
                    DateTimeKind.Unspecified);
                if (localZone.IsInvalidTime(unspecifiedToday))
                {
                    unspecifiedToday = unspecifiedToday.AddHours(1);
                }

                nextDueDate = new DateTimeOffset(unspecifiedToday, localZone.GetUtcOffset(unspecifiedToday));
            }
            else
            {
                nextDueDate = todayAllDay;
            }

            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.DueDate = nextDueDate;
            item.HasDueTime = keepExplicitTime;
            if (item.Recurrence is not null)
            {
                item.Recurrence = new TodoRecurrence
                {
                    Mode = item.Recurrence.Mode,
                    AnchorDueDate = nextDueDate
                };
            }

            item.UpdatedAt = utcNow;
        }

        _undoSnapshot = new TodoUndoSnapshot(
            [],
            _localizationService.Format("Todo.Overdue.Rescheduled", modifiedEntries.Count),
            modifiedEntries);
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        return modifiedEntries.Count;
    }

    public async Task<bool> UndoLastActionAsync()
    {
        if (_undoSnapshot is null)
        {
            return false;
        }

        bool retractedItems = false;
        if (_undoSnapshot.AddedItemIdsToRetract is { Count: > 0 } retractIds)
        {
            foreach (string retractId in retractIds)
            {
                TodoItemViewModel? addedItem = FindItem(retractId);
                if (addedItem is null)
                {
                    continue;
                }

                if (SelectedDetailItem is not null &&
                    string.Equals(SelectedDetailItem.Id, addedItem.Id, StringComparison.Ordinal))
                {
                    SelectedDetailItem = null;
                    IsCreatingDetailItem = false;
                }

                TombstoneItem(addedItem);
                retractedItems = true;
            }
        }

        if (_undoSnapshot.ModifiedEntries is { Count: > 0 } modifiedEntries)
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            foreach (TodoUndoModifiedEntry mod in modifiedEntries)
            {
                TodoItemViewModel? item = FindItem(mod.ItemId);
                if (item is null)
                {
                    continue;
                }

                item.DueDate = mod.PreviousDueDate;
                item.HasDueTime = mod.PreviousHasDueTime;
                item.Item.ReminderDismissedForDueDate = mod.PreviousReminderDismissedForDueDate;
                item.SnoozedUntil = mod.PreviousSnoozedUntil;
                item.Recurrence = mod.PreviousRecurrence?.Clone();
                if (mod.PreviousIsCompleted is { } previousIsCompleted)
                {
                    item.Item.GeneratedNextItemId = mod.PreviousGeneratedNextItemId;
                    item.RecurrenceSeriesId = mod.PreviousRecurrenceSeriesId;
                    item.Item.SnoozeLastNotifiedAt = mod.PreviousSnoozeLastNotifiedAt;
                    item.CompletedAt = mod.PreviousCompletedAt;
                    item.IsCompleted = previousIsCompleted;
                    item.RefreshDateDependentProperties();
                }

                item.UpdatedAt = utcNow;
            }
        }

        var existingLiveIds = Items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (TodoUndoDeletedEntry entry in _undoSnapshot.DeletedEntries.OrderBy(entry => entry.OriginalIndex))
        {
            TodoItem restored = TodoWidgetStore.CloneItem(entry.ItemSnapshot);
            restored.IsDeleted = false;
            restored.UpdatedAt = DateTimeOffset.UtcNow;
            _tombstones.RemoveAll(stub => string.Equals(stub.Id, restored.Id, StringComparison.Ordinal));
            if (!existingLiveIds.Add(restored.Id))
            {
                continue;
            }

            int insertIndex = Math.Clamp(entry.OriginalIndex, 0, Items.Count);
            Items.Insert(insertIndex, CreateItemViewModel(restored));
        }

        _undoSnapshot = null;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        if (retractedItems)
        {
            await ReclaimUnreferencedAttachmentsAsync();
        }

        return true;
    }

    public void DismissUndo()
    {
        _ = DismissUndoAsync();
    }

    public async Task DismissUndoAsync()
    {
        if (_undoSnapshot is null)
        {
            return;
        }

        _undoSnapshot = null;
        RefreshUndoProperties();
        await ReclaimUnreferencedAttachmentsAsync();
    }

    internal async Task<int> ReclaimUnreferencedAttachmentsAsync(bool startupSweep = false)
    {
        if (_recycleAttachmentFileAsync is null)
        {
            return 0;
        }

        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddAttachmentPaths(IEnumerable<TodoAttachment>? attachments)
        {
            if (attachments is null)
            {
                return;
            }

            foreach (TodoAttachment attachment in attachments)
            {
                if (attachment is null || string.IsNullOrWhiteSpace(attachment.FilePath))
                {
                    continue;
                }

                try
                {
                    referencedPaths.Add(Path.GetFullPath(attachment.FilePath));
                }
                catch
                {
                    referencedPaths.Add(attachment.FilePath);
                }
            }
        }

        foreach (TodoItemViewModel liveItem in Items)
        {
            AddAttachmentPaths(liveItem.Item.Attachments);
        }

        if (IsCreatingDetailItem && SelectedDetailItem is not null)
        {
            AddAttachmentPaths(SelectedDetailItem.Item.Attachments);
        }

        if (_undoSnapshot is not null)
        {
            foreach (TodoUndoDeletedEntry undoEntry in _undoSnapshot.DeletedEntries)
            {
                AddAttachmentPaths(undoEntry.ItemSnapshot.Attachments);
            }
        }

        var knownTombstoneIds = _tombstones
            .Select(stub => stub.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
            _store.AttachmentDirectory,
            referencedPaths,
            _recycleAttachmentFileAsync,
            knownTombstoneIds,
            startupSweep ? TimeSpan.Zero : TimeSpan.FromMinutes(2));
    }

    // Soft-delete: the record stays in the store file as a tombstone (via
    // SaveAsync) so a merge restore cannot resurrect it as "unknown".
    private void TombstoneItem(TodoItemViewModel item)
    {
        item.ReleaseAttachmentThumbnails();
        item.Item.IsDeleted = true;
        item.Item.UpdatedAt = DateTimeOffset.UtcNow;
        TodoWidgetStore.StripTombstonePayload(item.Item);
        Items.Remove(item);
        _tombstones.Add(item.Item);
    }
}
