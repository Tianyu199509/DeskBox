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
        DateTimeOffset? dueDate = ResolvePresetDueDate(preset);
        SetDraftDueDate(dueDate, hasDueTime: false);
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

        IsCreatingDetailItem = false;
        SelectedDetailItem = item;
        return item;
    }

    private void ResolveDraftContextMetadata(
        out bool isImportant,
        out DateTimeOffset? dueDate,
        out bool hasDueTime,
        out string? colorMarker)
    {
        isImportant = _draftImportant || SelectedFilter == TodoFilter.Important;
        if (_draftDueDate is not null)
        {
            dueDate = _draftDueDate;
            hasDueTime = DraftHasDueTime;
        }
        else if (SelectedFilter == TodoFilter.Today)
        {
            dueDate = TodoWidgetStore.NormalizeAllDayDueDate(DateTimeOffset.Now);
            hasDueTime = false;
        }
        else
        {
            dueDate = null;
            hasDueTime = false;
        }

        colorMarker = MapColorFilterToMarker(SelectedColorFilter);
    }

    public TodoItemViewModel OpenNewDetailFromDraft()
    {
        ResolveDraftContextMetadata(
            out bool isImportant,
            out DateTimeOffset? dueDate,
            out bool hasDueTime,
            out string? colorMarker);
        string initialTitle = InputText ?? string.Empty;

        InputText = string.Empty;
        DraftImportant = false;
        SetDraftDueDate(null, hasDueTime: false);

        CloseDetail();
        foreach (TodoItemViewModel entry in Items)
        {
            entry.CancelEdit();
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate, hasDueTime);
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = string.Empty,
            IsImportant = isImportant,
            DueDate = normalizedDueDate,
            HasDueTime = normalizedDueDate is not null && hasDueTime,
            ColorMarker = TodoItem.NormalizeColorMarker(colorMarker),
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom
                ? Items.Count
                : 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService, PreviewCacheScope);
        viewModel.BeginEdit();
        if (!string.IsNullOrEmpty(initialTitle))
        {
            viewModel.EditText = initialTitle;
        }

        IsCreatingDetailItem = true;
        SelectedDetailItem = viewModel;
        return viewModel;
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
            HasDueTime = false,
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom
                ? Items.Count
                : 0
        };
        var viewModel = new TodoItemViewModel(item, _localizationService, PreviewCacheScope);
        viewModel.BeginEdit();
        IsCreatingDetailItem = true;
        SelectedDetailItem = viewModel;
        return viewModel;
    }

    public async Task<TodoItemViewModel?> FinalizeDetailAsync(string? title, bool closeDetail = true)
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

            if (closeDetail)
            {
                item.CancelEdit();
                SelectedDetailItem = null;
            }
            else
            {
                item.IsEditing = false;
            }

            return item;
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            item.CancelEdit();
            IsCreatingDetailItem = false;
            // A blank draft is never a valid detail selection. In dual-pane mode
            // callers normally keep saved details open, but retaining an empty
            // transient item here would leave a ghost detail that is not in Items.
            SelectedDetailItem = null;

            return null;
        }

        item.Text = normalizedTitle;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        if (closeDetail)
        {
            item.CancelEdit();
        }
        else
        {
            item.IsEditing = false;
            if (string.IsNullOrWhiteSpace(item.EditText))
            {
                item.EditText = normalizedTitle;
            }
        }

        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(item);
        }
        else
        {
            Items.Insert(0, item);
        }

        IsCreatingDetailItem = false;
        if (closeDetail)
        {
            SelectedDetailItem = null;
        }

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

        // Markdown source is the user's document. Preserve significant leading,
        // trailing, and line-ending whitespace instead of normalizing it here.
        string? normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        if (string.Equals(item.Item.Notes, normalizedNotes, StringComparison.Ordinal))
        {
            return true;
        }

        string? previousNotes = item.Item.Notes;
        DateTimeOffset previousUpdatedAt = item.UpdatedAt;
        item.Item.Notes = normalizedNotes;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        try
        {
            await SaveAsync();
            return true;
        }
        catch
        {
            // Keep persistence transactional from the editor's point of view.
            // The source editor retains the draft and can safely retry.
            item.Item.Notes = previousNotes;
            item.UpdatedAt = previousUpdatedAt;
            item.RefreshDetailProperties();
            throw;
        }
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

        var attachmentViewModel = new TodoAttachmentViewModel(attachment, PreviewCacheScope);
        item.Item.Attachments.Add(attachment);
        item.Attachments.Add(attachmentViewModel);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        item.RefreshDetailProperties();
        await SaveAsync();
        return attachmentViewModel;
    }

    public async Task<bool> DeleteAttachmentAsync(
        string itemId,
        string attachmentId,
        Func<string, Task<bool>>? recycleFileAsync = null)
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
            // Only files inside the managed attachment tree are eligible for
            // deletion, and they go through the recycle kernel.
            await AttachmentStorageService.TryDeleteManagedCopyAsync(
                attachment.FilePath,
                _store.AttachmentDirectory,
                recycleFileAsync ?? _recycleAttachmentFileAsync);
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
