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
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DateTimeOffset? dueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };

        return await SetDueDateAsync(itemId, dueDate);
    }

    public Task<int> SetDueDatePresetAsync(IEnumerable<string> itemIds, TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DateTimeOffset? dueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };

        return SetDueDateAsync(itemIds, dueDate);
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

        CaptureUndoSnapshot(_localizationService.T("Todo.Undo.Deleted"));
        if (string.Equals(SelectedDetailItem?.Id, itemId, StringComparison.Ordinal))
        {
            SelectedDetailItem = null;
        }
        Items.Remove(item);
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
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

        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.DeletedSelected", itemsToDelete.Count));
        if (SelectedDetailItem is not null && selectedIds.Contains(SelectedDetailItem.Id))
        {
            SelectedDetailItem = null;
        }

        foreach (var item in itemsToDelete)
        {
            Items.Remove(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return itemsToDelete.Count;
    }

    public async Task<int> ClearCompletedAsync()
    {
        var completedItems = Items.Where(item => item.IsCompleted).ToList();
        if (completedItems.Count == 0)
        {
            return 0;
        }

        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.ClearedCompleted", completedItems.Count));
        foreach (var item in completedItems)
        {
            Items.Remove(item);
        }

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return completedItems.Count;
    }

    public async Task<int> ClearAllAsync()
    {
        if (Items.Count == 0)
        {
            return 0;
        }

        int removedCount = Items.Count;
        CaptureUndoSnapshot(_localizationService.Format("Todo.Undo.ClearedAll", removedCount));
        Items.Clear();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
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

    public async Task<bool> UndoLastActionAsync()
    {
        if (_undoSnapshot is null)
        {
            return false;
        }

        Items.Clear();
        foreach (var item in _undoSnapshot.Items.Select(CloneTodoItem))
        {
            Items.Add(new TodoItemViewModel(item, _localizationService));
        }

        _undoSnapshot = null;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        return true;
    }

    public void DismissUndo()
    {
        if (_undoSnapshot is null)
        {
            return;
        }

        _undoSnapshot = null;
        RefreshUndoProperties();
    }
}
