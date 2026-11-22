using System.Globalization;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services.CustomWidgetCapabilities;

internal sealed record TodoLiveWidgetMutationBridge(
    Func<string, string, DateTimeOffset?, TodoPriority, Task<bool?>> TryAddAsync,
    Func<string, string, Task<bool?>> TryToggleAsync,
    Func<string, string, Task<bool?>> TryRemoveAsync,
    Func<string, string, Task<bool?>> TryCyclePriorityAsync,
    Func<string, DateOnly?, Task<int?>> TryClearCompletedAsync,
    Func<string, string, string?, DateTimeOffset?, bool, TodoPriority?, Task<bool?>>? TryUpdateAsync = null,
    Func<string, DateOnly, Task<int?>>? TryRolloverOverdueAsync = null);

/// <summary>
/// Thread-safe write bridge for Custom Widgets mutating host <see cref="TodoWidgetStore"/> stores.
/// Routes to the authoritative live <see cref="TodoWidgetViewModel"/> on the UI thread when open,
/// and applies full recurrence, sort-order, tombstone, and attachment hygiene when mutating headless stores.
/// </summary>
internal static class TodoCapabilityMutations
{
    private const string FallbackTodoWidgetId = CustomWidgetHostSnapshotService.FallbackTodoWidgetId;
    private static readonly SemaphoreSlim s_mutationLock = new(1, 1);

    public static async Task<bool> AddAsync(
        SettingsService? settingsService,
        string title,
        string? dueIso,
        string? priority,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        string trimmedTitle = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return false;
        }

        string widgetsRoot = ResolveMutationWidgetsRoot(settingsService, widgetsDataRootOverride);
        bool allowLiveBridge = liveBridge is not null && IsLiveHostWidgetsRoot(widgetsDataRootOverride, settingsService);
        await s_mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(widgetsRoot);
            List<(string Id, string ListName)> candidates = resolveCandidateWidgets(settingsService, widgetsRoot);
            string targetWidgetId = candidates.Count > 0 && !string.IsNullOrWhiteSpace(candidates[0].Id)
                ? candidates[0].Id
                : FallbackTodoWidgetId;

            DateTimeOffset? parsedDueDate = TryParseLocalDueIso(dueIso);
            TodoPriority parsedPriority = ParsePriorityEnum(priority);

            if (allowLiveBridge)
            {
                bool? liveAdded = await liveBridge!
                    .TryAddAsync(targetWidgetId, trimmedTitle, parsedDueDate, parsedPriority)
                    .ConfigureAwait(false);
                if (liveAdded.HasValue)
                {
                    return liveAdded.Value;
                }
            }

            var store = new TodoWidgetStore(widgetsRoot, targetWidgetId);
            TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
            data.Items ??= [];

            (bool isImportant, string? colorMarker) = MapPriorityInput(priority);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            bool insertBottom = string.Equals(
                TodoOptionKinds.NormalizeNewTaskPosition(settingsService?.Settings.Todo.TodoNewTaskPosition),
                TodoOptionKinds.NewTaskPositionBottom,
                StringComparison.Ordinal);

            var item = new TodoItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = trimmedTitle,
                IsCompleted = false,
                IsImportant = isImportant,
                ColorMarker = colorMarker,
                DueDate = parsedDueDate.HasValue ? TodoWidgetStore.NormalizeAllDayDueDate(parsedDueDate.Value) : null,
                HasDueTime = false,
                SortOrder = 0,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
                DeviceId = DeviceIdentity.Id,
                IsDeleted = false
            };

            if (insertBottom)
            {
                item.SortOrder = data.Items
                    .Where(static i => i is not null && !i.IsDeleted)
                    .Select(static i => i.SortOrder)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
                data.Items.Add(item);
            }
            else
            {
                foreach (TodoItem existing in data.Items)
                {
                    if (existing is not null && !existing.IsDeleted)
                    {
                        existing.SortOrder += 1;
                    }
                }

                data.Items.Insert(0, item);
            }

            await store.SaveAsync(data).ConfigureAwait(false);
            await notifyRefresherAsync(targetWidgetId).ConfigureAwait(false);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            s_mutationLock.Release();
        }
    }

    public static Task<bool> ToggleAsync(
        SettingsService? settingsService,
        string itemId,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        return MutateSingleItemAsync(
            settingsService,
            itemId,
            widgetsDataRootOverride,
            resolveCandidateWidgets,
            notifyRefresherAsync,
            liveBridge,
            liveMutator: (bridge, widgetId, id) => bridge.TryToggleAsync(widgetId, id),
            applyOfflineAsync: static (store, data, item, nowUtc) =>
            {
                bool nextCompleted = !item.IsCompleted;
                if (nextCompleted)
                {
                    if (item.Recurrence is not null)
                    {
                        item.RecurrenceSeriesId ??= Guid.NewGuid().ToString("N");
                    }

                    item.SnoozedUntil = null;
                    item.SnoozeLastNotifiedAt = null;
                    item.GeneratedNextItemId = null;

                    if (TodoRecurrenceService.TryCreateNextOccurrence(item, nowUtc, out TodoItem? nextItem) &&
                        nextItem is not null)
                    {
                        item.GeneratedNextItemId = nextItem.Id;
                        int currentIndex = data.Items.IndexOf(item);
                        data.Items.Insert(Math.Clamp(currentIndex + 1, 0, data.Items.Count), nextItem);
                    }

                    item.IsCompleted = true;
                    item.CompletedAt = nowUtc;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(item.GeneratedNextItemId))
                    {
                        TodoItem? generated = data.Items.FirstOrDefault(candidate =>
                            candidate is not null &&
                            !candidate.IsDeleted &&
                            string.Equals(candidate.Id, item.GeneratedNextItemId, StringComparison.Ordinal));
                        if (generated is not null &&
                            TodoRecurrenceService.ShouldRemoveGeneratedOccurrence(item, generated))
                        {
                            TombstoneOfflineItem(generated, nowUtc);
                        }
                    }

                    item.GeneratedNextItemId = null;
                    item.CompletedAt = null;
                    item.IsCompleted = false;
                }

                item.UpdatedAt = nowUtc;
                item.DeviceId = DeviceIdentity.Id;
                _ = store;
                return Task.FromResult((Changed: true, NeedsAttachmentSweep: false));
            });
    }

    public static Task<bool> RemoveAsync(
        SettingsService? settingsService,
        string itemId,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        return MutateSingleItemAsync(
            settingsService,
            itemId,
            widgetsDataRootOverride,
            resolveCandidateWidgets,
            notifyRefresherAsync,
            liveBridge,
            liveMutator: (bridge, widgetId, id) => bridge.TryRemoveAsync(widgetId, id),
            applyOfflineAsync: static (store, data, item, nowUtc) =>
            {
                TodoItem? deferred = FindDeferredGeneratedOccurrenceInStore(
                    data.Items,
                    item,
                    DateTimeOffset.Now.Date);
                TombstoneOfflineItem(item, nowUtc);
                if (deferred is not null)
                {
                    TombstoneOfflineItem(deferred, nowUtc);
                }

                _ = store;
                return Task.FromResult((Changed: true, NeedsAttachmentSweep: true));
            });
    }

    public static Task<bool> CyclePriorityAsync(
        SettingsService? settingsService,
        string itemId,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        return MutateSingleItemAsync(
            settingsService,
            itemId,
            widgetsDataRootOverride,
            resolveCandidateWidgets,
            notifyRefresherAsync,
            liveBridge,
            liveMutator: (bridge, widgetId, id) => bridge.TryCyclePriorityAsync(widgetId, id),
            applyOfflineAsync: static (store, data, item, nowUtc) =>
            {
                string current = ResolvePriorityString(item);
                string next = current switch
                {
                    "normal" => "medium",
                    "medium" => "high",
                    _ => "normal"
                };
                (bool isImportant, string? colorMarker) = MapPriorityInput(next);
                item.IsImportant = isImportant;
                item.ColorMarker = colorMarker;
                item.UpdatedAt = nowUtc;
                item.DeviceId = DeviceIdentity.Id;

                if (FindDeferredGeneratedOccurrenceInStore(data.Items, item, DateTimeOffset.Now.Date) is { } deferred)
                {
                    deferred.IsImportant = isImportant;
                    deferred.ColorMarker = colorMarker;
                    deferred.UpdatedAt = nowUtc;
                    deferred.DeviceId = DeviceIdentity.Id;
                }

                _ = store;
                return Task.FromResult((Changed: true, NeedsAttachmentSweep: false));
            });
    }

    public static async Task<int> ClearCompletedAsync(
        SettingsService? settingsService,
        string? dueIsoFilter,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        DateOnly? filterDate = null;
        if (!string.IsNullOrWhiteSpace(dueIsoFilter))
        {
            string trimmed = dueIsoFilter.Trim();
            if (string.Equals(trimmed, "today", StringComparison.OrdinalIgnoreCase))
            {
                filterDate = DateOnly.FromDateTime(DateTime.Now);
            }
            else if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                filterDate = parsed;
            }
            else
            {
                return 0;
            }
        }

        string widgetsRoot = ResolveMutationWidgetsRoot(settingsService, widgetsDataRootOverride);
        bool allowLiveBridge = liveBridge is not null && IsLiveHostWidgetsRoot(widgetsDataRootOverride, settingsService);
        if (!Directory.Exists(widgetsRoot) && !allowLiveBridge)
        {
            return 0;
        }

        await s_mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(string Id, string ListName)> candidates = resolveCandidateWidgets(settingsService, widgetsRoot);
            if (candidates.Count == 0)
            {
                return 0;
            }

            int clearedCount = 0;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTime localToday = DateTimeOffset.Now.Date;

            foreach ((string widgetId, _) in candidates)
            {
                if (allowLiveBridge)
                {
                    int? liveCleared = await liveBridge!
                        .TryClearCompletedAsync(widgetId, filterDate)
                        .ConfigureAwait(false);
                    if (liveCleared.HasValue)
                    {
                        clearedCount += liveCleared.Value;
                        continue;
                    }
                }

                if (!HasExistingStoreFile(widgetsRoot, widgetId))
                {
                    continue;
                }

                var store = new TodoWidgetStore(widgetsRoot, widgetId);
                TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
                if (data.Items is not { Count: > 0 })
                {
                    continue;
                }

                var matchedCompleted = new List<TodoItem>();
                foreach (TodoItem item in data.Items)
                {
                    if (item is null || item.IsDeleted || !item.IsCompleted)
                    {
                        continue;
                    }

                    if (filterDate.HasValue)
                    {
                        if (!item.DueDate.HasValue)
                        {
                            continue;
                        }

                        DateOnly itemLocalDate = DateOnly.FromDateTime(item.DueDate.Value.ToLocalTime().DateTime);
                        if (itemLocalDate != filterDate.Value)
                        {
                            continue;
                        }
                    }

                    matchedCompleted.Add(item);
                }

                if (matchedCompleted.Count == 0)
                {
                    continue;
                }

                var itemsToTombstone = new List<TodoItem>(matchedCompleted);
                foreach (TodoItem completedItem in matchedCompleted)
                {
                    if (FindDeferredGeneratedOccurrenceInStore(data.Items, completedItem, localToday) is { } deferred &&
                        !itemsToTombstone.Contains(deferred))
                    {
                        itemsToTombstone.Add(deferred);
                    }
                }

                foreach (TodoItem entry in itemsToTombstone)
                {
                    TombstoneOfflineItem(entry, nowUtc);
                }

                await store.SaveAsync(data).ConfigureAwait(false);
                await RecycleOfflineStoreAttachmentsAsync(store, data).ConfigureAwait(false);
                await notifyRefresherAsync(widgetId).ConfigureAwait(false);

                clearedCount += matchedCompleted.Count;
            }

            return clearedCount;
        }
        catch
        {
            return 0;
        }
        finally
        {
            s_mutationLock.Release();
        }
    }

    public static Task<bool> UpdateAsync(
        SettingsService? settingsService,
        string itemId,
        string? title,
        string? dueIso,
        string? priority,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        string? trimmedTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        DateOnly localToday = DateOnly.FromDateTime(DateTime.Now);
        if (!TryParseFlexibleDueIso(dueIso, localToday, out bool updateDueDate, out DateTimeOffset? parsedDueDate) ||
            !TryParseOptionalPriority(priority, out TodoPriority? parsedPriority) ||
            (trimmedTitle is null && !updateDueDate && parsedPriority is null))
        {
            return Task.FromResult(false);
        }

        return MutateSingleItemAsync(
            settingsService,
            itemId,
            widgetsDataRootOverride,
            resolveCandidateWidgets,
            notifyRefresherAsync,
            liveBridge,
            liveMutator: (bridge, widgetId, id) => bridge.TryUpdateAsync is not null
                ? bridge.TryUpdateAsync(widgetId, id, trimmedTitle, parsedDueDate, updateDueDate, parsedPriority)
                : Task.FromResult<bool?>(null),
            applyOfflineAsync: (store, data, item, nowUtc) =>
            {
                if (trimmedTitle is not null)
                {
                    item.Text = trimmedTitle;
                }

                if (updateDueDate)
                {
                    item.DueDate = parsedDueDate.HasValue
                        ? TodoWidgetStore.NormalizeAllDayDueDate(parsedDueDate.Value)
                        : null;
                    item.HasDueTime = false;
                    item.SnoozedUntil = null;
                    item.SnoozeLastNotifiedAt = null;
                }

                if (parsedPriority.HasValue)
                {
                    (bool isImportant, string? colorMarker) = MapPriorityEnum(parsedPriority.Value);
                    item.IsImportant = isImportant;
                    item.ColorMarker = colorMarker;
                    if (FindDeferredGeneratedOccurrenceInStore(data.Items, item, DateTimeOffset.Now.Date) is { } deferred)
                    {
                        deferred.IsImportant = isImportant;
                        deferred.ColorMarker = colorMarker;
                        deferred.UpdatedAt = nowUtc;
                        deferred.DeviceId = DeviceIdentity.Id;
                    }
                }

                item.UpdatedAt = nowUtc;
                item.DeviceId = DeviceIdentity.Id;
                _ = store;
                return Task.FromResult((Changed: true, NeedsAttachmentSweep: false));
            });
    }

    public static async Task<int> RolloverOverdueAsync(
        SettingsService? settingsService,
        string? targetDueIso,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge = null)
    {
        DateOnly localToday = DateOnly.FromDateTime(DateTime.Now);
        if (!TryResolveRolloverTargetDate(targetDueIso, localToday, out DateOnly targetDate))
        {
            return 0;
        }

        string widgetsRoot = ResolveMutationWidgetsRoot(settingsService, widgetsDataRootOverride);
        bool allowLiveBridge = liveBridge is not null && IsLiveHostWidgetsRoot(widgetsDataRootOverride, settingsService);
        if (!Directory.Exists(widgetsRoot) && !allowLiveBridge)
        {
            return 0;
        }

        await s_mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(string Id, string ListName)> candidates = resolveCandidateWidgets(settingsService, widgetsRoot);
            if (candidates.Count == 0)
            {
                return 0;
            }

            int rolledCount = 0;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset normalizedTarget = TodoWidgetStore.NormalizeAllDayDueDate(ToLocalMidnight(targetDate));

            foreach ((string widgetId, _) in candidates)
            {
                if (allowLiveBridge && liveBridge!.TryRolloverOverdueAsync is not null)
                {
                    int? liveRolled = await liveBridge
                        .TryRolloverOverdueAsync(widgetId, targetDate)
                        .ConfigureAwait(false);
                    if (liveRolled.HasValue)
                    {
                        rolledCount += liveRolled.Value;
                        continue;
                    }
                }

                if (!HasExistingStoreFile(widgetsRoot, widgetId))
                {
                    continue;
                }

                var store = new TodoWidgetStore(widgetsRoot, widgetId);
                TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
                if (data.Items is not { Count: > 0 })
                {
                    continue;
                }

                int widgetRolled = 0;
                foreach (TodoItem item in data.Items)
                {
                    if (item is null || item.IsDeleted || item.IsCompleted || !item.DueDate.HasValue)
                    {
                        continue;
                    }

                    DateOnly itemLocalDate = DateOnly.FromDateTime(item.DueDate.Value.ToLocalTime().DateTime);
                    if (itemLocalDate >= localToday)
                    {
                        continue;
                    }

                    item.DueDate = normalizedTarget;
                    item.HasDueTime = false;
                    item.SnoozedUntil = null;
                    item.SnoozeLastNotifiedAt = null;
                    item.UpdatedAt = nowUtc;
                    item.DeviceId = DeviceIdentity.Id;
                    widgetRolled++;
                }

                if (widgetRolled == 0)
                {
                    continue;
                }

                await store.SaveAsync(data).ConfigureAwait(false);
                await notifyRefresherAsync(widgetId).ConfigureAwait(false);

                rolledCount += widgetRolled;
            }

            return rolledCount;
        }
        catch
        {
            return 0;
        }
        finally
        {
            s_mutationLock.Release();
        }
    }

    internal static string ResolvePriorityString(TodoItem item)
    {
        string? normalizedColor = TodoItem.NormalizeColorMarker(item.ColorMarker);
        if (item.IsImportant || string.Equals(normalizedColor, TodoItem.RedColorMarker, StringComparison.Ordinal))
        {
            return "high";
        }

        if (normalizedColor is TodoItem.OrangeColorMarker or TodoItem.YellowColorMarker)
        {
            return "medium";
        }

        return "normal";
    }

    private static async Task<bool> MutateSingleItemAsync(
        SettingsService? settingsService,
        string itemId,
        string? widgetsDataRootOverride,
        Func<SettingsService?, string, List<(string Id, string ListName)>> resolveCandidateWidgets,
        Func<string, Task> notifyRefresherAsync,
        TodoLiveWidgetMutationBridge? liveBridge,
        Func<TodoLiveWidgetMutationBridge, string, string, Task<bool?>> liveMutator,
        Func<TodoWidgetStore, TodoWidgetData, TodoItem, DateTimeOffset, Task<(bool Changed, bool NeedsAttachmentSweep)>> applyOfflineAsync)
    {
        string trimmedId = itemId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedId))
        {
            return false;
        }

        string widgetsRoot = ResolveMutationWidgetsRoot(settingsService, widgetsDataRootOverride);
        bool allowLiveBridge = liveBridge is not null && IsLiveHostWidgetsRoot(widgetsDataRootOverride, settingsService);
        if (!Directory.Exists(widgetsRoot) && !allowLiveBridge)
        {
            return false;
        }

        await s_mutationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(string Id, string ListName)> candidates = resolveCandidateWidgets(settingsService, widgetsRoot);
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

            foreach ((string widgetId, _) in candidates)
            {
                if (allowLiveBridge)
                {
                    bool? liveOutcome = await liveMutator(liveBridge!, widgetId, trimmedId).ConfigureAwait(false);
                    if (liveOutcome == true)
                    {
                        return true;
                    }

                    if (liveOutcome == false)
                    {
                        continue;
                    }
                }

                if (!HasExistingStoreFile(widgetsRoot, widgetId))
                {
                    continue;
                }

                var store = new TodoWidgetStore(widgetsRoot, widgetId);
                TodoWidgetData data = await store.LoadAsync().ConfigureAwait(false);
                if (data.Items is not { Count: > 0 })
                {
                    continue;
                }

                TodoItem? target = data.Items.FirstOrDefault(i =>
                    i is not null &&
                    !i.IsDeleted &&
                    string.Equals(i.Id, trimmedId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    continue;
                }

                (bool changed, bool needsSweep) = await applyOfflineAsync(store, data, target, nowUtc).ConfigureAwait(false);
                if (!changed)
                {
                    return false;
                }

                await store.SaveAsync(data).ConfigureAwait(false);
                if (needsSweep)
                {
                    await RecycleOfflineStoreAttachmentsAsync(store, data).ConfigureAwait(false);
                }

                await notifyRefresherAsync(widgetId).ConfigureAwait(false);

                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            s_mutationLock.Release();
        }
    }

    private static string ResolveMutationWidgetsRoot(SettingsService? settingsService, string? widgetsDataRootOverride) =>
        string.IsNullOrWhiteSpace(widgetsDataRootOverride)
            ? CustomWidgetHostSnapshotService.ResolveWidgetsRootForSettings(settingsService)
            : widgetsDataRootOverride;

    private static bool IsLiveHostWidgetsRoot(string? widgetsDataRootOverride, SettingsService? settingsService)
    {
        if (string.IsNullOrWhiteSpace(widgetsDataRootOverride))
        {
            return settingsService is null ||
                   string.IsNullOrWhiteSpace(settingsService.DataDirectoryPath) ||
                   PathsEqual(
                       settingsService.DataDirectoryPath,
                       DeskBoxDataPathService.Current.DataDirectory);
        }

        return PathsEqual(widgetsDataRootOverride, CustomWidgetHostSnapshotService.ResolveWidgetsRoot(null));
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static TodoItem? FindDeferredGeneratedOccurrenceInStore(
        IList<TodoItem> items,
        TodoItem item,
        DateTime localToday)
    {
        if (!item.IsCompleted || string.IsNullOrWhiteSpace(item.GeneratedNextItemId))
        {
            return null;
        }

        TodoItem? generated = items.FirstOrDefault(candidate =>
            candidate is not null &&
            !candidate.IsDeleted &&
            string.Equals(candidate.Id, item.GeneratedNextItemId, StringComparison.Ordinal));
        return generated is not null && TodoRecurrenceService.IsDeferredOccurrence(generated, localToday)
            ? generated
            : null;
    }

    private static void TombstoneOfflineItem(TodoItem item, DateTimeOffset nowUtc)
    {
        item.IsDeleted = true;
        item.UpdatedAt = nowUtc;
        item.DeviceId = DeviceIdentity.Id;
        TodoWidgetStore.StripTombstonePayload(item);
    }

    private static async Task RecycleOfflineStoreAttachmentsAsync(TodoWidgetStore store, TodoWidgetData data)
    {
        try
        {
            if (!Directory.Exists(store.AttachmentDirectory))
            {
                return;
            }

            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tombstoneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TodoItem item in data.Items)
            {
                if (item is null)
                {
                    continue;
                }

                if (item.IsDeleted)
                {
                    if (!string.IsNullOrWhiteSpace(item.Id))
                    {
                        tombstoneIds.Add(item.Id);
                    }

                    continue;
                }

                foreach (TodoAttachment attachment in item.Attachments ?? [])
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

            var fileService = new FileService();
            await AttachmentStorageService.RecycleUnreferencedManagedCopiesAsync(
                store.AttachmentDirectory,
                referencedPaths,
                path => fileService.DeleteEntryAsync(path, recycle: true),
                tombstoneIds,
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static bool HasExistingStoreFile(string widgetsRoot, string widgetId)
    {
        string storeFile = Path.Combine(widgetsRoot, widgetId, "todo.json");
        return File.Exists(storeFile) || File.Exists(ResilientJsonStore.GetBackupPath(storeFile));
    }

    private static DateTimeOffset? TryParseLocalDueIso(string? dueIso)
    {
        if (string.IsNullOrWhiteSpace(dueIso))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(dueIso.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly localDate))
        {
            return null;
        }

        DateTime localMidnight = localDate.ToDateTime(TimeOnly.MinValue);
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset);
    }

    private static TodoPriority ParsePriorityEnum(string? priority)
    {
        string normalized = priority?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "high" => TodoPriority.High,
            "medium" => TodoPriority.Medium,
            _ => TodoPriority.Normal
        };
    }

    private static (bool IsImportant, string? ColorMarker) MapPriorityInput(string? priority)
    {
        string normalized = priority?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "high" => (true, TodoItem.RedColorMarker),
            "medium" => (false, TodoItem.OrangeColorMarker),
            _ => (false, null)
        };
    }

    private static (bool IsImportant, string? ColorMarker) MapPriorityEnum(TodoPriority priority) =>
        priority switch
        {
            TodoPriority.High => (true, TodoItem.RedColorMarker),
            TodoPriority.Medium => (false, TodoItem.OrangeColorMarker),
            _ => (false, null)
        };

    private static DateTimeOffset ToLocalMidnight(DateOnly date)
    {
        DateTime localMidnight = date.ToDateTime(TimeOnly.MinValue);
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, offset);
    }

    private static bool TryParseFlexibleDueIso(
        string? rawDue,
        DateOnly localToday,
        out bool shouldUpdate,
        out DateTimeOffset? dueDate)
    {
        if (string.IsNullOrWhiteSpace(rawDue))
        {
            shouldUpdate = false;
            dueDate = null;
            return true;
        }

        string trimmed = rawDue.Trim();
        string lower = trimmed.ToLowerInvariant();
        if (lower is "clear" or "none")
        {
            shouldUpdate = true;
            dueDate = null;
            return true;
        }

        DateOnly parsedDate;
        if (string.Equals(lower, "today", StringComparison.Ordinal))
        {
            parsedDate = localToday;
        }
        else if (lower is "tomorrow" or "+1d")
        {
            parsedDate = localToday.AddDays(1);
        }
        else if (lower.Length >= 3 &&
                 lower[0] == '+' &&
                 lower[^1] == 'd' &&
                 int.TryParse(lower[1..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int deltaDays) &&
                 deltaDays is >= -365 and <= 3650)
        {
            parsedDate = localToday.AddDays(deltaDays);
        }
        else if (!DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
        {
            shouldUpdate = false;
            dueDate = null;
            return false;
        }

        shouldUpdate = true;
        dueDate = ToLocalMidnight(parsedDate);
        return true;
    }

    private static bool TryParseOptionalPriority(string? rawPriority, out TodoPriority? parsedPriority)
    {
        if (string.IsNullOrWhiteSpace(rawPriority))
        {
            parsedPriority = null;
            return true;
        }

        switch (rawPriority.Trim().ToLowerInvariant())
        {
            case "high":
            case "p1":
                parsedPriority = TodoPriority.High;
                return true;
            case "medium":
            case "p2":
                parsedPriority = TodoPriority.Medium;
                return true;
            case "normal":
            case "p3":
                parsedPriority = TodoPriority.Normal;
                return true;
            default:
                parsedPriority = null;
                return false;
        }
    }

    private static bool TryResolveRolloverTargetDate(string? targetDueIso, DateOnly localToday, out DateOnly targetDate)
    {
        if (string.IsNullOrWhiteSpace(targetDueIso))
        {
            targetDate = localToday;
            return true;
        }

        string trimmed = targetDueIso.Trim();
        string lower = trimmed.ToLowerInvariant();
        if (string.Equals(lower, "today", StringComparison.Ordinal))
        {
            targetDate = localToday;
            return true;
        }

        if (lower is "tomorrow" or "+1d")
        {
            targetDate = localToday.AddDays(1);
            return true;
        }

        return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out targetDate);
    }
}
