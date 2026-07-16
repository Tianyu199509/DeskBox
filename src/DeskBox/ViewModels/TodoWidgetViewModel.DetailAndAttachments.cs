using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    public void SetFilter(TodoFilter filter)
    {
        SelectedFilter = filter;
    }

    public void SetDraftDueDatePreset(TodoDuePreset preset)
    {
        DateTimeOffset today = new(DateTime.Now.Date);
        int daysUntilSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7;
        }

        DraftDueDate = preset switch
        {
            TodoDuePreset.Today => WithEndOfDay(today),
            TodoDuePreset.Tomorrow => WithEndOfDay(today.AddDays(1)),
            TodoDuePreset.ThisWeek => WithEndOfDay(today.AddDays(daysUntilSunday)),
            TodoDuePreset.NextMonday => WithEndOfDay(today.AddDays(daysUntilMonday)),
            TodoDuePreset.Clear => null,
            _ => null
        };
    }

    public void SetColorFilter(TodoColorFilter filter)
    {
        SelectedColorFilter = filter;
    }

    public TodoItemViewModel? OpenDetail(string itemId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        foreach (TodoItemViewModel entry in Items)
        {
            entry.CancelEdit();
        }

        item.BeginEdit();
        IsCreatingDetailItem = false;
        SelectedDetailItem = item;
        return item;
    }

    public TodoItemViewModel OpenNewDetail()
    {
        CloseDetail();
        foreach (TodoItemViewModel entry in Items)
        {
            entry.CancelEdit();
        }

        var now = DateTimeOffset.UtcNow;
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom
                ? Items.Count
                : 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService);
        viewModel.BeginEdit();
        IsCreatingDetailItem = true;
        SelectedDetailItem = viewModel;
        return viewModel;
    }

    public async Task<TodoItemViewModel?> FinalizeDetailAsync(string? title)
    {
        TodoItemViewModel? item = SelectedDetailItem;
        if (item is null)
        {
            return null;
        }

        string normalizedTitle = NormalizeText(title);
        if (!IsCreatingDetailItem)
        {
            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                await UpdateItemTextAsync(item.Id, normalizedTitle);
            }

            item.CancelEdit();
            SelectedDetailItem = null;
            return item;
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            item.CancelEdit();
            SelectedDetailItem = null;
            IsCreatingDetailItem = false;
            return null;
        }

        item.Text = normalizedTitle;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.CancelEdit();
        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(item);
        }
        else
        {
            Items.Insert(0, item);
        }

        IsCreatingDetailItem = false;
        SelectedDetailItem = null;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return item;
    }

    public void CloseDetail()
    {
        SelectedDetailItem?.CancelEdit();
        SelectedDetailItem = null;
        IsCreatingDetailItem = false;
    }

    public async Task<TodoStepViewModel?> AddStepAsync(string itemId, string? text)
    {
        TodoItemViewModel? item = FindItem(itemId);
        string normalizedText = NormalizeText(text);
        if (item is null || string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var step = new TodoStep
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalizedText,
            SortOrder = item.Steps.Count
        };
        var stepViewModel = new TodoStepViewModel(step);
        item.Item.Steps.Add(step);
        item.Steps.Add(stepViewModel);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return stepViewModel;
    }

    public async Task<bool> SetStepCompletedAsync(string itemId, string stepId, bool isCompleted)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        if (item is null || step is null)
        {
            return false;
        }

        step.IsCompleted = isCompleted;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> UpdateStepTextAsync(string itemId, string stepId, string? text)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        string normalizedText = NormalizeText(text);
        if (item is null || step is null || string.IsNullOrWhiteSpace(normalizedText))
        {
            return false;
        }

        step.Text = normalizedText;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync();
        return true;
    }

    public async Task<bool> DeleteStepAsync(string itemId, string stepId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoStepViewModel? step = item?.Steps.FirstOrDefault(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        if (item is null || step is null)
        {
            return false;
        }

        item.Steps.Remove(step);
        item.Item.Steps.RemoveAll(entry => string.Equals(entry.Id, stepId, StringComparison.Ordinal));
        for (int index = 0; index < item.Steps.Count; index++)
        {
            item.Steps[index].SortOrder = index;
        }

        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<bool> UpdateNotesAsync(string itemId, string? notes)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        string? normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (string.Equals(item.Item.Notes, normalizedNotes, StringComparison.Ordinal))
        {
            return true;
        }

        item.Item.Notes = normalizedNotes;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return true;
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentAsync(
        string itemId,
        string filePath,
        string displayName,
        string type)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var attachment = new TodoAttachment
        {
            Id = Guid.NewGuid().ToString("N"),
            FilePath = filePath.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(filePath) : displayName.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? "file" : type.Trim(),
            StorageMode = TodoAttachment.LinkedStorageMode,
            AddedAt = DateTimeOffset.UtcNow
        };
        return await AddAttachmentAsync(item, attachment);
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentPathAsync(
        string itemId,
        string filePath,
        bool? copyToManagedStorageOverride = null)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        bool copyToManagedStorage = copyToManagedStorageOverride ??
            (_settingsService is not null &&
             SettingsService.NormalizeAttachmentStorageMode(_settingsService.Settings.AttachmentStorageMode) ==
             SettingsService.AttachmentStorageModeCopy);
        TodoAttachment? attachment = await AttachmentStorageService.ImportPathAsync(
            filePath,
            GetManagedAttachmentDirectory(itemId),
            copyToManagedStorage);
        return attachment is null ? null : await AddAttachmentAsync(item, attachment);
    }

    public async Task<int> AddDroppedAttachmentsAsync(
        string itemId,
        IReadOnlyList<DroppedFilePath> droppedFiles)
    {
        int addedCount = 0;
        foreach (DroppedFilePath droppedFile in droppedFiles)
        {
            TodoAttachmentViewModel? attachment = await AddAttachmentPathAsync(
                itemId,
                droppedFile.Path,
                droppedFile.ForceManagedCopy ? true : null);
            if (attachment is not null)
            {
                addedCount++;
            }
        }

        return addedCount;
    }

    public async Task<TodoAttachmentViewModel?> AddAttachmentStreamAsync(
        string itemId,
        Stream stream,
        string? fileName)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        TodoAttachment? attachment = await AttachmentStorageService.SaveStreamAsync(
            stream,
            fileName,
            GetManagedAttachmentDirectory(itemId));
        return attachment is null ? null : await AddAttachmentAsync(item, attachment);
    }

    private async Task<TodoAttachmentViewModel?> AddAttachmentAsync(
        TodoItemViewModel item,
        TodoAttachment attachment)
    {
        if (item.Attachments.Any(existing =>
                string.Equals(existing.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            return item.Attachments.First(existing =>
                string.Equals(existing.FilePath, attachment.FilePath, StringComparison.OrdinalIgnoreCase));
        }

        var attachmentViewModel = new TodoAttachmentViewModel(attachment);
        item.Item.Attachments.Add(attachment);
        item.Attachments.Add(attachmentViewModel);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return attachmentViewModel;
    }

    public async Task<bool> DeleteAttachmentAsync(string itemId, string attachmentId)
    {
        TodoItemViewModel? item = FindItem(itemId);
        TodoAttachmentViewModel? attachment = item?.Attachments.FirstOrDefault(entry =>
            string.Equals(entry.Id, attachmentId, StringComparison.Ordinal));
        if (item is null || attachment is null)
        {
            return false;
        }

        item.Attachments.Remove(attachment);
        item.Item.Attachments.RemoveAll(entry => string.Equals(entry.Id, attachmentId, StringComparison.Ordinal));
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        if (attachment.Attachment.IsManagedCopy && File.Exists(attachment.FilePath))
        {
            try
            {
                File.Delete(attachment.FilePath);
            }
            catch (Exception ex)
            {
                App.Log($"[Todo] Failed to delete managed attachment '{attachment.FilePath}': {ex.Message}");
            }
        }
        return true;
    }

    private string GetManagedAttachmentDirectory(string itemId)
    {
        return Path.Combine(_store.AttachmentDirectory, itemId);
    }

    public void ToggleExpanded(string itemId)
    {
        var item = FindItem(itemId);
        if (item is null)
        {
            return;
        }

        if (item.IsExpanded)
        {
            item.IsExpanded = false;
            return;
        }

        foreach (var other in Items)
        {
            if (!string.Equals(other.Id, itemId, StringComparison.Ordinal))
            {
                other.IsExpanded = false;
                other.CancelEdit();
            }
        }

        item.IsExpanded = true;
    }

    public void CollapseAllExpanded()
    {
        foreach (var item in Items)
        {
            if (item.IsExpanded)
            {
                item.CancelEdit();
                item.IsExpanded = false;
            }
        }
    }

    public TodoItemViewModel? FocusReminderItem(string? itemId, bool preferTodayFilter)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var item = FindItem(itemId);
        if (item is null)
        {
            return null;
        }

        SelectedColorFilter = TodoColorFilter.All;
        SelectedFilter = preferTodayFilter && IsDueToday(item)
            ? TodoFilter.Today
            : TodoFilter.All;

        if (!VisibleItems.Contains(item))
        {
            SelectedFilter = TodoFilter.All;
        }

        foreach (var visibleItem in Items)
        {
            visibleItem.IsCopySelected = false;
        }

        item.IsCopySelected = true;
        return item;
    }
}
