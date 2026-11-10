using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
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
        _lastEvaluatedLocalDate = CurrentLocalDate;
        Items.Clear();
        _tombstones.Clear();
        foreach (var item in data.Items.OrderBy(item => item.SortOrder).ThenByDescending(item => item.UpdatedAt))
        {
            if (item.IsDeleted)
            {
                _tombstones.Add(item);
                continue;
            }

            Items.Add(CreateItemViewModel(item));
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
            out string? colorMarker,
            out string recurrenceMode);
        var item = await AddItemAsync(InputText, isImportant, dueDate, colorMarker, hasDueTime, recurrenceMode);
        if (item is not null)
        {
            _suppressDraftManualOverrideTracking = true;
            try
            {
                InputText = string.Empty;
            }
            finally
            {
                _suppressDraftManualOverrideTracking = false;
            }

            ResetDraftComposerState(clearMetadata: true);
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
        bool hasDueTime = true,
        string? recurrenceMode = null)
    {
        string normalizedText = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return null;
        }

        var now = CurrentUtcNow;
        string normalizedRecurrenceMode = TodoRecurrenceMode.Normalize(recurrenceMode);
        bool hasRecurrence = !string.Equals(normalizedRecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);
        if (hasRecurrence && dueDate is null)
        {
            dueDate = TodoWidgetStore.NormalizeAllDayDueDate(_clock());
            hasDueTime = false;
        }

        DateTimeOffset? normalizedDueDate = NormalizeDueDate(dueDate, hasDueTime);
        TodoRecurrence? recurrence = hasRecurrence
            ? TodoRecurrenceService.CreateRecurrence(normalizedRecurrenceMode, normalizedDueDate)
            : null;
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = normalizedText,
            IsImportant = isImportant,
            DueDate = normalizedDueDate,
            HasDueTime = normalizedDueDate is not null && hasDueTime,
            Recurrence = recurrence,
            RecurrenceSeriesId = recurrence is null ? null : Guid.NewGuid().ToString("N"),
            ColorMarker = TodoItem.NormalizeColorMarker(colorMarker),
            CreatedAt = now,
            UpdatedAt = now,
            SortOrder = 0
        };
        var viewModel = CreateItemViewModel(item);

        if (NewTaskPosition == SettingsService.TodoNewTaskPositionBottom)
        {
            Items.Add(viewModel);
        }
        else
        {
            Items.Insert(0, viewModel);
        }

        _lastSingleCreatedItemId = viewModel.Id;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return viewModel;
    }

    private static readonly Regex MarkdownTaskPrefixRegex = new(
        @"^\s*(?:[-*+]\s*\[(?<checked>[xX ])\]\s*|(?<box>[☐☑✓✔✅])\s*|[-*+•]\s+|\d{1,3}[.)]\s+)",
        RegexOptions.CultureInvariant);

    internal static (string Text, bool IsCompleted) StripPastedTaskPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (string.Empty, false);
        }

        string trimmed = line.Trim();
        Match match = MarkdownTaskPrefixRegex.Match(trimmed);
        if (!match.Success)
        {
            return (trimmed, false);
        }

        string remainder = trimmed[match.Length..].Trim();
        if (string.IsNullOrWhiteSpace(remainder))
        {
            return (string.Empty, false);
        }

        bool isCompleted = false;
        if (match.Groups["checked"].Success)
        {
            string checkChar = match.Groups["checked"].Value;
            isCompleted = string.Equals(checkChar, "x", StringComparison.OrdinalIgnoreCase);
        }
        else if (match.Groups["box"].Success)
        {
            string box = match.Groups["box"].Value;
            isCompleted = box is "☑" or "✓" or "✔" or "✅";
        }

        return (remainder, isCompleted);
    }

    public async Task<IReadOnlyList<TodoItemViewModel>> AddPastedTextItemsWithUndoAsync(string? rawClipboardText, int maxBatchLines = 50)
    {
        if (string.IsNullOrWhiteSpace(rawClipboardText))
        {
            return [];
        }

        string[] rawLines = rawClipboardText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var parsedLines = new List<(string Text, bool IsCompleted)>();
        foreach (string rawLine in rawLines)
        {
            (string stripped, bool isCompleted) = StripPastedTaskPrefix(rawLine);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                parsedLines.Add((stripped, isCompleted));
                if (parsedLines.Count >= maxBatchLines)
                {
                    break;
                }
            }
        }

        if (parsedLines.Count == 0)
        {
            return [];
        }

        bool contextImportant = SelectedFilter == TodoFilter.Important;
        DateTimeOffset? contextDueDate = SelectedFilter == TodoFilter.Today
            ? TodoWidgetStore.NormalizeAllDayDueDate(_clock())
            : null;
        string? contextColor = MapColorFilterToMarker(SelectedColorFilter);
        DateTimeOffset now = CurrentUtcNow;
        bool insertBottom = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom;
        int topInsertIndex = 0;

        var createdViewModels = new List<TodoItemViewModel>(parsedLines.Count);
        var createdIds = new List<string>(parsedLines.Count);

        foreach ((string lineText, bool lineCompleted) in parsedLines)
        {
            var todoItem = new TodoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = lineText,
                IsCompleted = lineCompleted,
                CompletedAt = lineCompleted ? now : null,
                IsImportant = contextImportant,
                DueDate = contextDueDate,
                HasDueTime = false,
                ColorMarker = contextColor,
                CreatedAt = now,
                UpdatedAt = now,
                SortOrder = 0
            };
            var vm = CreateItemViewModel(todoItem);
            if (insertBottom)
            {
                Items.Add(vm);
            }
            else
            {
                Items.Insert(Math.Clamp(topInsertIndex++, 0, Items.Count), vm);
            }

            createdViewModels.Add(vm);
            createdIds.Add(vm.Id);
        }

        _lastSingleCreatedItemId = createdViewModels.Count == 1 ? createdViewModels[0].Id : null;
        _undoSnapshot = new TodoUndoSnapshot(
            [],
            _localizationService.T("Todo.Dropped"),
            AddedItemIdsToRetract: createdIds);

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        return createdViewModels;
    }

    public async Task<int> ImportQuickCaptureItemsAsync(IReadOnlyList<QuickCaptureItem> sourceItems)
    {
        if (sourceItems is null || sourceItems.Count == 0)
        {
            return 0;
        }

        QuickCaptureItem[] validItems = sourceItems
            .Where(item => item is not null &&
                           (!string.IsNullOrWhiteSpace(item.Body) || !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Url) || (item.Attachments?.Count ?? 0) > 0))
            .ToArray();
        if (validItems.Length == 0)
        {
            return 0;
        }

        string fallbackTitle = _localizationService.T("QuickCapture.Untitled");
        DateTimeOffset now = CurrentUtcNow;
        bool contextImportant = SelectedFilter == TodoFilter.Important;
        DateTimeOffset? contextDueDate = SelectedFilter == TodoFilter.Today
            ? TodoWidgetStore.NormalizeAllDayDueDate(_clock())
            : null;
        string? contextColor = MapColorFilterToMarker(SelectedColorFilter);
        bool insertBottom = NewTaskPosition == SettingsService.TodoNewTaskPositionBottom;
        int topInsertIndex = 0;
        var createdIds = new List<string>();

        foreach (QuickCaptureItem source in validItems)
        {
            IReadOnlyList<(string Title, bool IsCompleted, List<TodoStep> Steps, string? Notes)> blueprints =
                QuickCaptureTagParser.SplitItemsForTodoConversion(source, fallbackTitle);

            for (int index = 0; index < blueprints.Count; index++)
            {
                (string title, bool isCompleted, List<TodoStep> steps, string? notes) = blueprints[index];
                var todoItem = new TodoItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = title,
                    IsCompleted = isCompleted,
                    CompletedAt = isCompleted ? now : null,
                    IsImportant = source.IsPinned || contextImportant,
                    DueDate = contextDueDate,
                    HasDueTime = false,
                    ColorMarker = contextColor,
                    Steps = steps,
                    Notes = notes,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                if (index == 0)
                {
                    foreach (TodoAttachment attachment in source.Attachments ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(attachment.FilePath))
                        {
                            continue;
                        }

                        TodoAttachment cloned = attachment.Clone();
                        if (cloned.IsManagedCopy && File.Exists(cloned.FilePath))
                        {
                            TodoAttachment? imported = await AttachmentStorageService.ImportPathAsync(
                                cloned.FilePath,
                                Path.Combine(_store.AttachmentDirectory, todoItem.Id),
                                copyToManagedStorage: true);
                            if (imported is not null)
                            {
                                todoItem.Attachments.Add(imported);
                                continue;
                            }
                        }

                        todoItem.Attachments.Add(cloned);
                    }
                }

                TodoItemViewModel vm = CreateItemViewModel(todoItem);
                if (insertBottom)
                {
                    Items.Add(vm);
                }
                else
                {
                    Items.Insert(Math.Clamp(topInsertIndex++, 0, Items.Count), vm);
                }

                createdIds.Add(vm.Id);
            }
        }

        if (createdIds.Count == 0)
        {
            return 0;
        }

        _lastSingleCreatedItemId = createdIds.Count == 1 ? createdIds[0] : null;
        _undoSnapshot = new TodoUndoSnapshot(
            [],
            _localizationService.T("Todo.Dropped"),
            AddedItemIdsToRetract: createdIds);

        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        RefreshUndoProperties();
        await SaveAsync();
        return createdIds.Count;
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

        var now = CurrentUtcNow;
        item.Text = normalizedText;
        item.UpdatedAt = now;
        if (FindDeferredGeneratedOccurrence(item) is { } deferred)
        {
            deferred.Text = normalizedText;
            deferred.UpdatedAt = now;
        }
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

        var now = CurrentUtcNow;
        if (isCompleted)
        {
            var previousModified = new TodoUndoModifiedEntry(
                item.Id,
                item.DueDate,
                item.HasDueTime,
                item.Item.ReminderDismissedForDueDate,
                item.SnoozedUntil,
                item.Recurrence?.Clone(),
                PreviousIsCompleted: item.IsCompleted,
                PreviousCompletedAt: item.CompletedAt,
                PreviousGeneratedNextItemId: item.Item.GeneratedNextItemId,
                PreviousRecurrenceSeriesId: item.RecurrenceSeriesId,
                PreviousSnoozeLastNotifiedAt: item.Item.SnoozeLastNotifiedAt);
            List<string>? generatedIds = null;

            if (item.Recurrence is not null)
            {
                item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
            }

            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.Item.GeneratedNextItemId = null;

            if (TodoRecurrenceService.TryCreateNextOccurrence(item.Item, now, out TodoItem? nextItem) &&
                nextItem is not null)
            {
                item.Item.GeneratedNextItemId = nextItem.Id;
                generatedIds = [nextItem.Id];
                int currentIndex = Items.IndexOf(item);
                var nextViewModel = CreateItemViewModel(nextItem);
                Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), nextViewModel);
            }

            item.CompletedAt = now;
            item.IsCompleted = true;
            item.RefreshDateDependentProperties();

            _undoSnapshot = new TodoUndoSnapshot(
                [],
                _localizationService.T("Todo.Undo.Completed"),
                [previousModified],
                generatedIds);
            RefreshUndoProperties();
        }
        else
        {
            TodoItemViewModel? generatedOccurrence = FindGeneratedOccurrence(item);
            if (generatedOccurrence is not null &&
                TodoRecurrenceService.ShouldRemoveGeneratedOccurrence(item.Item, generatedOccurrence.Item))
            {
                TombstoneItem(generatedOccurrence);
            }

            item.Item.GeneratedNextItemId = null;
            item.CompletedAt = null;
            item.IsCompleted = false;
            item.RefreshDateDependentProperties();
        }

        item.UpdatedAt = now;
        RefreshVisibleItems(item);
        RefreshCountProperties();
        await SaveAsync();
        return true;
    }

    public async Task<int> SetCompletedBatchAsync(IEnumerable<string> itemIds, bool isCompleted)
    {
        string[] distinctIds = itemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctIds.Length == 0)
        {
            return 0;
        }

        if (distinctIds.Length == 1)
        {
            TodoItemViewModel? single = FindItem(distinctIds[0]);
            if (single is null || single.IsCompleted == isCompleted)
            {
                return 0;
            }

            return await SetCompletedAsync(single.Id, isCompleted) ? 1 : 0;
        }

        var now = CurrentUtcNow;
        var modifiedEntries = new List<TodoUndoModifiedEntry>();
        var generatedIds = new List<string>();
        int changedCount = 0;

        foreach (string itemId in distinctIds)
        {
            TodoItemViewModel? item = FindItem(itemId);
            if (item is null || item.IsCompleted == isCompleted)
            {
                continue;
            }

            if (isCompleted)
            {
                modifiedEntries.Add(new TodoUndoModifiedEntry(
                    item.Id,
                    item.DueDate,
                    item.HasDueTime,
                    item.Item.ReminderDismissedForDueDate,
                    item.SnoozedUntil,
                    item.Recurrence?.Clone(),
                    PreviousIsCompleted: item.IsCompleted,
                    PreviousCompletedAt: item.CompletedAt,
                    PreviousGeneratedNextItemId: item.Item.GeneratedNextItemId,
                    PreviousRecurrenceSeriesId: item.RecurrenceSeriesId,
                    PreviousSnoozeLastNotifiedAt: item.Item.SnoozeLastNotifiedAt));

                if (item.Recurrence is not null)
                {
                    item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
                }

                item.SnoozedUntil = null;
                item.Item.SnoozeLastNotifiedAt = null;
                item.Item.GeneratedNextItemId = null;

                if (TodoRecurrenceService.TryCreateNextOccurrence(item.Item, now, out TodoItem? nextItem) &&
                    nextItem is not null)
                {
                    item.Item.GeneratedNextItemId = nextItem.Id;
                    generatedIds.Add(nextItem.Id);
                    int currentIndex = Items.IndexOf(item);
                    var nextViewModel = CreateItemViewModel(nextItem);
                    Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), nextViewModel);
                }

                item.CompletedAt = now;
                item.IsCompleted = true;
                item.RefreshDateDependentProperties();
            }
            else
            {
                TodoItemViewModel? generatedOccurrence = FindGeneratedOccurrence(item);
                if (generatedOccurrence is not null &&
                    TodoRecurrenceService.ShouldRemoveGeneratedOccurrence(item.Item, generatedOccurrence.Item))
                {
                    TombstoneItem(generatedOccurrence);
                }

                item.Item.GeneratedNextItemId = null;
                item.CompletedAt = null;
                item.IsCompleted = false;
                item.RefreshDateDependentProperties();
            }

            item.UpdatedAt = now;
            changedCount++;
        }

        if (changedCount == 0)
        {
            return 0;
        }

        if (isCompleted && modifiedEntries.Count > 0)
        {
            _undoSnapshot = new TodoUndoSnapshot(
                [],
                _localizationService.T("Todo.Undo.Completed"),
                modifiedEntries,
                generatedIds.Count > 0 ? generatedIds : null);
            RefreshUndoProperties();
        }

        RefreshVisibleItems();
        RefreshCountProperties();
        await SaveAsync();
        return changedCount;
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

        var now = CurrentUtcNow;
        item.IsImportant = isImportant;
        item.UpdatedAt = now;
        if (FindDeferredGeneratedOccurrence(item) is { } deferred)
        {
            deferred.IsImportant = isImportant;
            deferred.UpdatedAt = now;
        }
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
        var now = CurrentUtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id) && item.IsImportant != isImportant).ToList())
        {
            item.IsImportant = isImportant;
            item.UpdatedAt = now;
            if (FindDeferredGeneratedOccurrence(item) is { } deferred)
            {
                deferred.IsImportant = isImportant;
                deferred.UpdatedAt = now;
            }
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

        var now = CurrentUtcNow;
        item.ColorMarker = normalizedColorMarker;
        item.UpdatedAt = now;
        if (FindDeferredGeneratedOccurrence(item) is { } deferred)
        {
            deferred.ColorMarker = normalizedColorMarker;
            deferred.UpdatedAt = now;
        }
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
        var now = CurrentUtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)).ToList())
        {
            if (string.Equals(item.ColorMarker, normalizedColorMarker, StringComparison.Ordinal))
            {
                continue;
            }

            item.ColorMarker = normalizedColorMarker;
            item.UpdatedAt = now;
            if (FindDeferredGeneratedOccurrence(item) is { } deferred)
            {
                deferred.ColorMarker = normalizedColorMarker;
                deferred.UpdatedAt = now;
            }
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

        var now = CurrentUtcNow;
        TodoItemViewModel? existingDeferred = FindDeferredGeneratedOccurrence(item);
        item.DueDate = normalizedDueDate;
        item.HasDueTime = resolvedHasDueTime;
        item.Recurrence = updatedRecurrence;
        if (normalizedDueDate is null)
        {
            item.ReminderOffsetMinutes = null;
        }
        if (updatedRecurrence is null)
        {
            if (existingDeferred is not null)
            {
                TombstoneItem(existingDeferred);
            }
            item.RecurrenceSeriesId = null;
            item.Item.GeneratedNextItemId = null;
        }
        else if (existingDeferred is not null &&
                 TodoRecurrenceService.TryGetNextDueDate(item.Item, item.CompletedAt ?? now, out DateTimeOffset recomputedNextDue))
        {
            existingDeferred.DueDate = recomputedNextDue;
            existingDeferred.HasDueTime = resolvedHasDueTime;
            existingDeferred.Recurrence = updatedRecurrence.Clone();
            existingDeferred.Item.ReminderLastNotifiedAt = null;
            existingDeferred.Item.ReminderDismissedForDueDate = null;
            existingDeferred.SnoozedUntil = null;
            existingDeferred.Item.SnoozeLastNotifiedAt = null;
            existingDeferred.UpdatedAt = now;
        }
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = now;
        item.RefreshDateDependentProperties();
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
        var now = CurrentUtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)).ToList())
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

            TodoItemViewModel? existingDeferred = FindDeferredGeneratedOccurrence(item);
            item.DueDate = normalizedDueDate;
            item.HasDueTime = resolvedHasDueTime;
            item.Recurrence = updatedRecurrence;
            if (normalizedDueDate is null)
            {
                item.ReminderOffsetMinutes = null;
            }
            if (updatedRecurrence is null)
            {
                if (existingDeferred is not null)
                {
                    TombstoneItem(existingDeferred);
                }
                item.RecurrenceSeriesId = null;
                item.Item.GeneratedNextItemId = null;
            }
            else if (existingDeferred is not null &&
                     TodoRecurrenceService.TryGetNextDueDate(item.Item, item.CompletedAt ?? now, out DateTimeOffset recomputedNextDue))
            {
                existingDeferred.DueDate = recomputedNextDue;
                existingDeferred.HasDueTime = resolvedHasDueTime;
                existingDeferred.Recurrence = updatedRecurrence.Clone();
                existingDeferred.Item.ReminderLastNotifiedAt = null;
                existingDeferred.Item.ReminderDismissedForDueDate = null;
                existingDeferred.SnoozedUntil = null;
                existingDeferred.Item.SnoozeLastNotifiedAt = null;
                existingDeferred.UpdatedAt = now;
            }
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            item.RefreshDateDependentProperties();
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
        bool wantsRecurrence = !string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal);
        DateTimeOffset? targetDueDate = item.DueDate;
        bool assignedDefaultToday = false;
        if (wantsRecurrence && targetDueDate is null)
        {
            targetDueDate = TodoWidgetStore.NormalizeAllDayDueDate(_clock());
            assignedDefaultToday = true;
        }

        TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, targetDueDate);
        if (!assignedDefaultToday && AreRecurrenceEqual(item.Recurrence, recurrence))
        {
            return true;
        }

        var now = CurrentUtcNow;
        TodoItemViewModel? existingDeferred = FindDeferredGeneratedOccurrence(item);
        if (assignedDefaultToday)
        {
            item.DueDate = targetDueDate;
            item.HasDueTime = false;
        }

        item.Recurrence = recurrence;
        item.RecurrenceSeriesId = recurrence is null
            ? null
            : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
        if (recurrence is null)
        {
            if (existingDeferred is not null)
            {
                TombstoneItem(existingDeferred);
            }
            item.Item.GeneratedNextItemId = null;
        }
        else if (item.IsCompleted)
        {
            if (existingDeferred is not null &&
                TodoRecurrenceService.TryGetNextDueDate(item.Item, item.CompletedAt ?? now, out DateTimeOffset recomputedNextDue))
            {
                existingDeferred.DueDate = recomputedNextDue;
                existingDeferred.HasDueTime = item.HasDueTime;
                existingDeferred.Recurrence = recurrence.Clone();
                existingDeferred.RecurrenceSeriesId = item.RecurrenceSeriesId;
                existingDeferred.Item.ReminderLastNotifiedAt = null;
                existingDeferred.Item.ReminderDismissedForDueDate = null;
                existingDeferred.SnoozedUntil = null;
                existingDeferred.Item.SnoozeLastNotifiedAt = null;
                existingDeferred.UpdatedAt = now;
            }
            else if (existingDeferred is null &&
                     TodoRecurrenceService.TryCreateNextOccurrence(item.Item, item.CompletedAt ?? now, out TodoItem? nextItem) &&
                     nextItem is not null)
            {
                item.Item.GeneratedNextItemId = nextItem.Id;
                int currentIndex = Items.IndexOf(item);
                Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), CreateItemViewModel(nextItem));
            }
        }
        else
        {
            item.Item.GeneratedNextItemId = null;
        }
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.Item.SnoozeLastNotifiedAt = null;
        item.UpdatedAt = now;
        item.RefreshDateDependentProperties();
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
        bool wantsRecurrence = !string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal);
        var now = CurrentUtcNow;
        DateTimeOffset defaultToday = TodoWidgetStore.NormalizeAllDayDueDate(_clock());
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id)).ToList())
        {
            DateTimeOffset? targetDueDate = item.DueDate;
            bool assignedDefaultToday = false;
            if (wantsRecurrence && targetDueDate is null)
            {
                targetDueDate = defaultToday;
                assignedDefaultToday = true;
            }

            TodoRecurrence? recurrence = TodoRecurrenceService.CreateRecurrence(normalizedMode, targetDueDate);
            if (!assignedDefaultToday && AreRecurrenceEqual(item.Recurrence, recurrence))
            {
                continue;
            }

            TodoItemViewModel? existingDeferred = FindDeferredGeneratedOccurrence(item);
            if (assignedDefaultToday)
            {
                item.DueDate = targetDueDate;
                item.HasDueTime = false;
            }

            item.Recurrence = recurrence;
            item.RecurrenceSeriesId = recurrence is null
                ? null
                : item.RecurrenceSeriesId ?? Guid.NewGuid().ToString("N");
            if (recurrence is null)
            {
                if (existingDeferred is not null)
                {
                    TombstoneItem(existingDeferred);
                }
                item.Item.GeneratedNextItemId = null;
            }
            else if (item.IsCompleted)
            {
                if (existingDeferred is not null &&
                    TodoRecurrenceService.TryGetNextDueDate(item.Item, item.CompletedAt ?? now, out DateTimeOffset recomputedNextDue))
                {
                    existingDeferred.DueDate = recomputedNextDue;
                    existingDeferred.HasDueTime = item.HasDueTime;
                    existingDeferred.Recurrence = recurrence.Clone();
                    existingDeferred.RecurrenceSeriesId = item.RecurrenceSeriesId;
                    existingDeferred.Item.ReminderLastNotifiedAt = null;
                    existingDeferred.Item.ReminderDismissedForDueDate = null;
                    existingDeferred.SnoozedUntil = null;
                    existingDeferred.Item.SnoozeLastNotifiedAt = null;
                    existingDeferred.UpdatedAt = now;
                }
                else if (existingDeferred is null &&
                         TodoRecurrenceService.TryCreateNextOccurrence(item.Item, item.CompletedAt ?? now, out TodoItem? nextItem) &&
                         nextItem is not null)
                {
                    item.Item.GeneratedNextItemId = nextItem.Id;
                    int currentIndex = Items.IndexOf(item);
                    Items.Insert(Math.Clamp(currentIndex + 1, 0, Items.Count), CreateItemViewModel(nextItem));
                }
            }
            else
            {
                item.Item.GeneratedNextItemId = null;
            }
            item.Item.ReminderLastNotifiedAt = null;
            item.Item.ReminderDismissedForDueDate = null;
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
            item.UpdatedAt = now;
            item.RefreshDateDependentProperties();
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

        var now = CurrentUtcNow;
        item.ReminderOffsetMinutes = normalizedOffset;
        item.Item.ReminderLastNotifiedAt = null;
        item.Item.ReminderDismissedForDueDate = null;
        if (TodoReminderOptions.IsReminderOff(normalizedOffset))
        {
            item.SnoozedUntil = null;
            item.Item.SnoozeLastNotifiedAt = null;
        }

        if (FindDeferredGeneratedOccurrence(item) is { } deferred)
        {
            deferred.ReminderOffsetMinutes = normalizedOffset;
            deferred.Item.ReminderLastNotifiedAt = null;
            deferred.Item.ReminderDismissedForDueDate = null;
            if (TodoReminderOptions.IsReminderOff(normalizedOffset))
            {
                deferred.SnoozedUntil = null;
                deferred.Item.SnoozeLastNotifiedAt = null;
            }
            deferred.UpdatedAt = now;
        }

        item.UpdatedAt = now;
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
        var now = CurrentUtcNow;
        int updatedCount = 0;
        foreach (var item in Items.Where(item => selectedIds.Contains(item.Id) && item.DueDate is not null).ToList())
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

            if (FindDeferredGeneratedOccurrence(item) is { } deferred)
            {
                deferred.ReminderOffsetMinutes = normalizedOffset;
                deferred.Item.ReminderLastNotifiedAt = null;
                deferred.Item.ReminderDismissedForDueDate = null;
                if (TodoReminderOptions.IsReminderOff(normalizedOffset))
                {
                    deferred.SnoozedUntil = null;
                    deferred.Item.SnoozeLastNotifiedAt = null;
                }
                deferred.UpdatedAt = now;
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
