using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    public void SetSortMode(WidgetSortMode mode)
    {
        if (Config.SortMode == mode)
        {
            // Toggle direction only for auto sort modes (not Manual).
            if (mode != WidgetSortMode.Manual)
            {
                Config.SortDescending = !Config.SortDescending;
            }
            else
            {
                // Clicking "Manual" when already manual — no-op.
                return;
            }
        }
        else
        {
            Config.SortMode = mode;
            Config.SortDescending = false;
        }

        // Manual mode: keep current order, just persist the mode.
        if (mode == WidgetSortMode.Manual)
        {
            NormalizeSortOrder();
            SyncConfigItemsOrder();
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
            OnPropertyChanged(nameof(SortModeLabel));
            return;
        }

        var sorted = Items.OrderBy(item => item, Comparer<WidgetItem>.Create(CompareItems)).ToList();
        Items.Clear();
        foreach (var item in sorted)
        {
            Items.Add(item);
        }
        NormalizeSortOrder();
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        OnPropertyChanged(nameof(SortModeLabel));
    }

    /// <summary>
    /// Applies a reorder drop on the flat (non-stack-projection) item list.
    /// Single items and multi-selections share one landing primitive: the
    /// computed insertion index already carries the moving-before-target
    /// compensation, so both shapes land exactly on the insertion line. The
    /// rebuild drops the ListView selection; the surface restores it by path
    /// (ScheduleSurfaceReorderSelectionRestore), mirroring the projection
    /// path. Call PersistManualOrder on drop.
    /// </summary>
    public bool MoveItemsForReorder(
        IReadOnlyList<WidgetItem> draggedItems,
        int targetIndex)
    {
        if (Config.SortMode != WidgetSortMode.Manual ||
            draggedItems.Count == 0)
        {
            return false;
        }

        if (!TryComputeItemBlockReorder(
                Items,
                draggedItems,
                targetIndex,
                out int insertionIndex))
        {
            return false;
        }

        ApplyItemBlockReorder(Items, draggedItems, insertionIndex);
        return true;
    }

    // Lands the dragged block at the post-removal insertion index through
    // one remove+insert rebuild. Sequential Items.Move calls cannot express
    // a block landing — removing a not-yet-placed dragged item shifts the
    // members already placed, so downward drops scattered (single items one
    // slot past the insertion line, multi-selections broke apart; regression
    // window 1.5.8-1.5.9, 9e414687 removed the single-item compensation).
    // The flat-path twin of the display-unit rebuild in Stacks.cs.
    internal static void ApplyItemBlockReorder(
        ObservableCollection<WidgetItem> items,
        IReadOnlyList<WidgetItem> draggedItems,
        int insertionIndex)
    {
        HashSet<WidgetItem> draggedSet = new(
            draggedItems,
            ReferenceEqualityComparer.Instance);
        List<WidgetItem> block = items
            .Where(draggedSet.Contains)
            .ToList();
        foreach (WidgetItem item in block)
        {
            int currentIndex = IndexOfReference(items, item, 0);
            if (currentIndex >= 0)
            {
                items.RemoveAt(currentIndex);
            }
        }

        for (int offset = 0; offset < block.Count; offset++)
        {
            items.Insert(
                Math.Clamp(insertionIndex + offset, 0, items.Count),
                block[offset]);
        }
    }

    // Computes where a contiguous block move lands: the insertion index
    // counts how many dragged items already sit ahead of the drop target, so
    // removing them from their old positions cannot shift the landing spot.
    // Returns false for an equivalent drop that would not change the order.
    internal static bool TryComputeItemBlockReorder(
        IReadOnlyList<WidgetItem> items,
        IReadOnlyList<WidgetItem> draggedItems,
        int targetIndex,
        out int insertionIndex)
    {
        insertionIndex = -1;
        HashSet<WidgetItem> draggedSet = new(
            draggedItems,
            ReferenceEqualityComparer.Instance);
        List<int> draggedIndices = new(draggedSet.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (draggedSet.Contains(items[index]))
            {
                draggedIndices.Add(index);
            }
        }

        if (draggedIndices.Count == 0)
        {
            return false;
        }

        int cappedIndex = Math.Clamp(targetIndex, 0, items.Count);
        int movingBeforeInsertion = draggedIndices
            .Count(index => index < cappedIndex);
        insertionIndex = Math.Clamp(
            cappedIndex - movingBeforeInsertion,
            0,
            items.Count - draggedIndices.Count);

        for (int offset = 0; offset < draggedIndices.Count; offset++)
        {
            if (draggedIndices[offset] != insertionIndex + offset)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Persists the current item order to config and settings.
    /// Called once after real-time reordering is complete (on drop).
    /// </summary>
    public void PersistManualOrder()
    {
        if (Config.SortMode != WidgetSortMode.Manual || !IsAtMappedRoot)
        {
            return;
        }

        NormalizeSortOrder();
        SyncConfigItemsOrder();
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
    }

    /// <summary>
    /// Rebuilds Config.Items to match the current Items collection order.
    /// Called after a manual reorder to persist the new order.
    /// </summary>
    private bool SyncConfigItemsOrder()
    {
        if (!IsAtMappedRoot)
        {
            return false;
        }

        bool changed = Config.Items.Count != Items.Count;
        if (!changed)
        {
            for (int index = 0; index < Items.Count; index++)
            {
                WidgetItem item = Items[index];
                WidgetItemConfig persisted = Config.Items[index];
                if (!string.Equals(item.Path, persisted.Path, StringComparison.OrdinalIgnoreCase) ||
                    persisted.SortOrder != index)
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        Config.Items.Clear();
        for (int index = 0; index < Items.Count; index++)
        {
            WidgetItem item = Items[index];
            Config.Items.Add(new WidgetItemConfig
            {
                Path = item.Path,
                SortOrder = index
            });
        }

        return true;
    }

    private void PersistManualOrderSnapshotIfChanged()
    {
        if (Config.SortMode == WidgetSortMode.Manual && SyncConfigItemsOrder())
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
    }

    public string SortModeLabel => Config.SortMode switch
    {
        WidgetSortMode.Size => _localizationService.T("Widget.Sort.Size"),
        WidgetSortMode.Type => _localizationService.T("Widget.Sort.Type"),
        WidgetSortMode.DateModified => _localizationService.T("Widget.Sort.DateModified"),
        WidgetSortMode.Manual => _localizationService.T("Widget.Sort.Manual"),
        _ => _localizationService.T("Widget.Sort.Name")
    };

    private void SortItems()
    {
        // Manual mode: never auto-sort; preserve user-defined order.
        if (Config.SortMode == WidgetSortMode.Manual)
        {
            NormalizeSortOrder();
            return;
        }

        var sortedItems = Items.ToList();
        sortedItems.Sort(CompareItems);
        for (int targetIndex = 0; targetIndex < sortedItems.Count; targetIndex++)
        {
            var item = sortedItems[targetIndex];
            int currentIndex = Items.IndexOf(item);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }

        NormalizeSortOrder();
    }

    private async Task ConfigureFolderWatchersAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        _folderWatcher.Stop();
        _publicFolderWatcher.Stop();

        cancellationToken.ThrowIfCancellationRequested();
        if (_isDisposed || string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        try
        {
            string watcherPath = folderPath;
            if (!string.IsNullOrWhiteSpace(MappedFolderPath) &&
                PathsEqual(
                    folderPath,
                    _mappedFolderTraversalPath ?? MappedFolderPath))
            {
                // Retain the logical mapping path inside the watcher so a
                // retargeted junction can be resolved on reconnect. The
                // watcher's public WatchedPath remains the physical target.
                watcherPath = MappedFolderPath;
            }

            await _folderWatcher.StartAsync(watcherPath).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
            if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
            {
                await _publicFolderWatcher.StartAsync(publicDesktop).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop bumps each watcher's generation, preventing a slow probe from
            // attaching after this content switch has already been superseded.
            _folderWatcher.Stop();
            _publicFolderWatcher.Stop();
            throw;
        }
    }

    private void OnFolderChanged(FolderChangeBatch changeBatch)
    {
        // FolderWatcherService uses an Action event and therefore cannot await
        // subscribers directly. Keep the event boundary synchronous and route
        // the async work through a task that owns its exception handling.
        _ = ProcessFolderChangedAsync(changeBatch);
    }

    private async Task ProcessFolderChangedAsync(FolderChangeBatch changeBatch)
    {
        if (_isDisposed ||
            string.IsNullOrEmpty(CurrentFolderPath) ||
            !IsCurrentWatcherBatch(changeBatch, CurrentFolderPath) ||
            !IsCurrentWatcherGeneration(changeBatch))
        {
            return;
        }

        if (_surfaceActivity.TryDeferChange())
        {
            return;
        }

        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.OnFolderChanged",
            $"id={Config.Id} changes={changeBatch.Changes.Count} fullReload={changeBatch.RequiresFullReload}");

        await _folderRefreshGate.WaitAsync();
        try
        {
            if (_isDisposed ||
                string.IsNullOrEmpty(CurrentFolderPath) ||
                !IsCurrentWatcherBatch(changeBatch, CurrentFolderPath) ||
                !IsCurrentWatcherGeneration(changeBatch))
            {
                return;
            }

            if (_surfaceActivity.TryDeferChange())
            {
                return;
            }

            if (ShouldUseFullReload(changeBatch, CurrentFolderPath))
            {
                // A full reload mid-import would re-sync, re-sort and
                // re-hydrate the whole list, throwing away the batching the
                // open scope just bought — and an import's event storm can
                // still trip the watcher's buffered-change cap or its
                // overflow/error channels. Defer one authoritative refresh to
                // the batch finalization instead of fighting the import for
                // the list.
                if (_itemMutationBatchDepth > 0)
                {
                    _pendingFolderRefreshAfterBatch = true;
                    MarkItemMutationBatchDirty();
                    return;
                }

                await LoadFolderContentsAsync(CurrentFolderPath);
                return;
            }

            // A large batch with a KNOWN change set no longer detours through
            // a full reload: coalesce the shell/antivirus event multiplication
            // to one change per path, then apply the batch inside a single
            // mutation scope so the whole batch pays one sort-order
            // normalization, one manual-order persistence, one hydration start
            // (which itself only processes items still missing metadata) and
            // one render reconcile - cost proportional to the batch, not the
            // folder. Only the state-loss signals in ShouldUseFullReload still
            // reload; the watcher's buffered-change cap routes true event
            // storms there.
            using (EnterItemMutationScope())
            {
                foreach (FolderChange change in CoalesceFolderChanges(changeBatch.Changes))
                {
                    await ApplyFolderChangeAsync(change);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[FolderRefresh] Incremental refresh failed for '{CurrentFolderPath}': {ex}");
            if (!_isDisposed && !string.IsNullOrEmpty(CurrentFolderPath))
            {
                try
                {
                    await LoadFolderContentsAsync(CurrentFolderPath);
                }
                catch (Exception fallbackEx)
                {
                    // A transient network/ACL failure must not escape to the
                    // dispatcher or fault an unobserved refresh task.
                    App.Log($"[FolderRefresh] Fallback refresh failed for '{CurrentFolderPath}': {fallbackEx}");
                }
            }
        }
        finally
        {
            _folderRefreshGate.Release();
        }
    }

    /// <summary>
    /// One authoritative refresh deferred out of an open batch mutation
    /// scope: the watcher asked for a full reload while an import was
    /// post-processing. Runs through the folder refresh gate so it cannot
    /// interleave a scheduled incremental pass.
    /// </summary>
    private async Task RunDeferredFolderRefreshAsync()
    {
        try
        {
            await _folderRefreshGate.WaitAsync();
            try
            {
                if (!_isDisposed && !string.IsNullOrEmpty(CurrentFolderPath))
                {
                    await LoadFolderContentsAsync(CurrentFolderPath);
                }
            }
            finally
            {
                _folderRefreshGate.Release();
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[FolderRefresh] Deferred post-batch refresh failed for " +
                $"'{CurrentFolderPath}': {ex}");
        }
    }

    /// <summary>
    /// Handles a shell icon change for a direct child folder (e.g. after a
    /// tool like Folder Painter rewrites its desktop.ini).  Clears the cached
    /// icon and re-hydrates just that item.
    /// </summary>
    private void OnFolderIconChanged(string folderPath)
    {
        if (_isDisposed || _surfaceActivity.TryDeferChange())
        {
            return;
        }

        int index = FindItemIndexByPath(folderPath);
        if (index < 0)
        {
            return;
        }

        Items[index].Icon = null;
        _fileService.ClearIconCache(folderPath, _hideShortcutArrowOverlay, _showImageFilesAsIcons);
        StartItemHydration();
    }

    /// <summary>
    /// Decides whether a watcher batch must pay a full reload. Only
    /// state-loss signals qualify: the watcher's overflow/error/query
    /// channels (ReadDirectoryChangesW provides just a blanket notification
    /// once its buffer overflows, so <c>RequiresFullReload</c> batches cannot
    /// trust their change list), an empty change list, the combined desktop
    /// roots (the incremental path applies single-root changes), and batches
    /// from a folder this widget no longer displays. Batch SIZE is
    /// deliberately not a criterion: a large batch with a known change set
    /// runs as a coalesced incremental pass inside one mutation scope, so a
    /// multi-folder drag storm no longer triggers reload-per-batch.
    /// </summary>
    internal static bool ShouldUseFullReload(FolderChangeBatch changeBatch, string mappedFolderPath)
    {
        if (changeBatch.RequiresFullReload || changeBatch.Changes.Count == 0)
        {
            return true;
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (mappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!changeBatch.WatchedPath.Equals(mappedFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mappedFolderPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase) &&
               changeBatch.Changes.Any(change => !FileService.IsPathUnderDirectory(change.FullPath, mappedFolderPath));
    }

    internal static bool IsCurrentWatcherBatch(
        FolderChangeBatch changeBatch,
        string mappedFolderPath)
    {
        if (changeBatch.WatchedPath.Equals(
                mappedFolderPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        return mappedFolderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase) &&
               changeBatch.WatchedPath.Equals(publicDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentWatcherGeneration(FolderChangeBatch changeBatch)
    {
        FolderWatcherService watcher =
            string.Equals(
                changeBatch.WatchedPath,
                CurrentFolderPath,
                StringComparison.OrdinalIgnoreCase)
                ? _folderWatcher
                : _publicFolderWatcher;
        return changeBatch.Generation == watcher.Generation;
    }

    /// <summary>
    /// Collapses a watcher batch to its last event per path, keeping the
    /// surviving events in their original batch order. Shell copies and
    /// antivirus scans report several events per path (Created plus
    /// LastWrite/Size/Attributes Changed); classification re-reads current
    /// disk state, so only the newest signal per path carries information
    /// and every older duplicate would just repeat a classification and an
    /// upsert. Renamed events are exempt: a rename carries its own old-path
    /// removal, and a later event against the renamed path (the copy engine
    /// writing the moved file) must not swallow that removal.
    /// </summary>
    internal static IReadOnlyList<FolderChange> CoalesceFolderChanges(
        IReadOnlyList<FolderChange> changes)
    {
        if (changes.Count <= 1)
        {
            return changes;
        }

        var lastEventIndexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < changes.Count; index++)
        {
            FolderChange change = changes[index];
            if (change.ChangeType != WatcherChangeTypes.Renamed)
            {
                lastEventIndexByPath[change.FullPath] = index;
            }
        }

        if (lastEventIndexByPath.Count == changes.Count)
        {
            // No renames and every path distinct - nothing to collapse.
            return changes;
        }

        var coalesced = new List<FolderChange>(changes.Count);
        for (int index = 0; index < changes.Count; index++)
        {
            FolderChange change = changes[index];
            if (change.ChangeType == WatcherChangeTypes.Renamed ||
                lastEventIndexByPath[change.FullPath] == index)
            {
                coalesced.Add(change);
            }
        }

        return coalesced;
    }

    private async Task ApplyFolderChangeAsync(FolderChange change)
    {
        if (change.ChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrWhiteSpace(change.OldFullPath))
        {
            int previousManualIndex = Config.SortMode == WidgetSortMode.Manual
                ? FindItemIndexByPath(change.OldFullPath)
                : -1;
            FolderEntryRefreshStatus oldState =
                await FileService.ClassifyDirectChildAsync(change.OldFullPath);
            FolderEntryRefreshStatus newState =
                await FileService.ClassifyDirectChildAsync(change.FullPath);
            if (ShouldRemoveExistingItem(WatcherChangeTypes.Renamed, oldState))
            {
                if (newState == FolderEntryRefreshStatus.Available)
                {
                    TransferFileAddedAt(change.OldFullPath, change.FullPath);
                }

                RemoveItemByPath(
                    change.OldFullPath,
                    persistManualOrder: previousManualIndex < 0);
            }

            if (newState == FolderEntryRefreshStatus.Available)
            {
                await UpsertFolderItemAsync(
                    change.FullPath,
                    previousManualIndex >= 0 ? previousManualIndex : null);
            }
            else if (newState == FolderEntryRefreshStatus.Filtered)
            {
                RemoveItemByPath(change.FullPath);
            }

            PersistManualOrderSnapshotIfChanged();
            return;
        }

        FolderEntryRefreshStatus state =
            await FileService.ClassifyDirectChildAsync(change.FullPath);
        if (ShouldRemoveExistingItem(change.ChangeType, state))
        {
            RemoveItemByPath(change.FullPath);
            return;
        }

        if (state == FolderEntryRefreshStatus.Available)
        {
            await UpsertFolderItemAsync(change.FullPath);
        }
    }

    internal static bool ShouldRemoveExistingItem(
        WatcherChangeTypes changeType,
        FolderEntryRefreshStatus state)
    {
        if (state == FolderEntryRefreshStatus.Filtered)
        {
            return true;
        }

        return state == FolderEntryRefreshStatus.NotFound &&
               (changeType == WatcherChangeTypes.Deleted ||
                changeType == WatcherChangeTypes.Renamed);
    }

    private async Task<bool> UpsertFolderItemAsync(
        string path,
        int? preferredManualIndex = null)
    {
        var item = await _fileService.TryCreateWidgetItemAsync(
            path,
            hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
            showImageFilesAsIcons: _showImageFilesAsIcons,
            showFileExtensions: _showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon: false,
            loadFolderItemCount: false,
            loadShortcutTarget: false);
        if (item is null)
        {
            // A null result can also mean an ACL/provider race. Incremental
            // callers classify explicit deletion before reaching this method,
            // so preserving the current item is the only safe fallback.
            return false;
        }

        // A file can leave and return to the same managed path without its
        // timestamp changing. Drop every source-scoped icon variant on a real
        // watcher event so an earlier generic/failed shortcut icon cannot be
        // reused after the item is reintroduced.
        _fileService.ClearIconCache(
            path,
            _hideShortcutArrowOverlay,
            _showImageFilesAsIcons,
            resetTransientFailures: true);

        int existingIndex = FindItemIndexForManagedMutation(path);
        if (Config.SortMode == WidgetSortMode.Manual && existingIndex >= 0)
        {
            AssignAddedAt(item);
            if (preferredManualIndex is { } requestedIndex)
            {
                // A file can leave the widget and later be dropped back in
                // before the watcher has removed its old item. In that race
                // the normal replacement path used to preserve the stale
                // index, ignoring the insertion line the user just chose.
                // Replace the object and move it as one operation so a
                // re-import always honors the current drop position.
                Items.RemoveAt(existingIndex);
                int adjustedIndex = requestedIndex;
                if (existingIndex < adjustedIndex)
                {
                    adjustedIndex--;
                }

                adjustedIndex = Math.Clamp(
                    adjustedIndex,
                    0,
                    Items.Count);
                item.SortOrder = adjustedIndex;
                Items.Insert(adjustedIndex, item);
            }
            else
            {
                item.SortOrder = existingIndex;
                Items[existingIndex] = item;
            }

            TrackManagedItemByPath(path, item);
            FinishItemUpsert();
            return true;
        }

        if (existingIndex >= 0)
        {
            Items.RemoveAt(existingIndex);
        }

        AssignAddedAt(item);

        int insertIndex = Config.SortMode == WidgetSortMode.Manual && preferredManualIndex.HasValue
            ? Math.Clamp(preferredManualIndex.Value, 0, Items.Count)
            : GetSortedInsertIndex(item);
        item.SortOrder = insertIndex;
        Items.Insert(insertIndex, item);
        TrackManagedItemByPath(path, item);
        FinishItemUpsert();
        return true;
    }

    /// <summary>
    /// Per-upsert derived work: sort-order normalization, manual-order
    /// persistence, metadata hydration. A batch mutation scope defers these
    /// to its single end-of-batch finalization, so a 2000-file import runs
    /// them once instead of once per file.
    /// </summary>
    private void FinishItemUpsert()
    {
        if (_itemMutationBatchDepth > 0)
        {
            MarkItemMutationBatchDirty();
            return;
        }

        NormalizeSortOrder();
        PersistManualOrderSnapshotIfChanged();
        StartItemHydration();
    }

    private void RemoveItemByPath(string path, bool persistManualOrder = true)
    {
        int index = FindItemIndexForManagedMutation(path);
        if (index < 0)
        {
            return;
        }

        _fileService.ClearIconCache(
            path,
            _hideShortcutArrowOverlay,
            _showImageFilesAsIcons,
            resetTransientFailures: true);
        Items.RemoveAt(index);
        UntrackManagedItemByPath(path);
        RemoveFileAddedAt(path);
        if (_itemMutationBatchDepth > 0)
        {
            MarkItemMutationBatchDirty();
            return;
        }

        NormalizeSortOrder();
        if (persistManualOrder)
        {
            PersistManualOrderSnapshotIfChanged();
        }
    }

    private int FindItemIndexByPath(string path)
    {
        for (int index = 0; index < Items.Count; index++)
        {
            if (string.Equals(Items[index].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private int GetSortedInsertIndex(WidgetItem candidate)
    {
        // Manual mode: always append to the end.
        if (Config.SortMode == WidgetSortMode.Manual)
        {
            return Items.Count;
        }

        // Items is kept sorted under the active sort mode, so the linear
        // first-strictly-greater scan is an upper-bound search: a 2000-file
        // import pays ~11 comparisons per insert instead of ~1000.
        return BinarySearchSortedInsertIndex(
            Items.Count,
            index => Items[index],
            candidate,
            CompareItems);
    }

    /// <summary>
    /// Upper-bound insert search shared with behavior tests: the first index
    /// whose item compares strictly greater than the candidate (equal keys
    /// land after the equal run), mirroring the linear scan it replaced.
    /// Requires the sequence to be sorted under the same comparison.
    /// </summary>
    internal static int BinarySearchSortedInsertIndex(
        int count,
        Func<int, WidgetItem> itemAt,
        WidgetItem candidate,
        Comparison<WidgetItem> compare)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (compare(candidate, itemAt(middle)) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private void NormalizeSortOrder()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].SortOrder = index;
        }
    }

    /// <summary>
    /// Pure shortcut-target freshness decision shared with behavior tests:
    /// refresh snapshots never read shortcut targets, so a stored target
    /// only goes stale when the .lnk itself changed (icon-ledger mismatch).
    /// An unstamped ledger (-1, extraction never succeeded) means "no
    /// information" and preserves the stored target.
    /// </summary>
    internal static bool ShouldDropStaleHydratedShortcutTarget(
        bool targetIsShortcut,
        long targetHydratedIconFileSize,
        DateTime targetHydratedIconStamp,
        bool sourceIsShortcut,
        long sourceFileSize,
        DateTime sourceLastModified)
    {
        return targetIsShortcut &&
               sourceIsShortcut &&
               targetHydratedIconFileSize >= 0 &&
               (targetHydratedIconFileSize != sourceFileSize ||
                targetHydratedIconStamp != sourceLastModified);
    }

    /// <summary>
    /// Pure icon-ledger decision shared with behavior tests: an icon
    /// reference is stale when the file's size or timestamp moved since
    /// extraction. This is the invalidation point that replaces
    /// lifecycle-triggered blanket icon clears: the merge drops exactly the
    /// changed items, the content-versioned cache key self-heals on the
    /// re-fetch, and unchanged items keep their icons untouched.
    /// </summary>
    internal static bool ShouldDropStaleHydratedIcon(
        bool hasIcon,
        long hydratedFileSize,
        DateTime hydratedStamp,
        long sourceFileSize,
        DateTime sourceStamp)
    {
        return hasIcon &&
               (hydratedFileSize != sourceFileSize ||
                hydratedStamp != sourceStamp);
    }

    /// <summary>
    /// Pure folder-count freshness decision shared with behavior tests.
    /// Refresh snapshots enumerate with folder counts unloaded, so the merge
    /// must not unconditionally copy the unloaded flag: a folder's
    /// visible-child count survives a refresh only when the folder's own
    /// LastWriteTime did not change and it was already loaded. Unconditional
    /// copying made every activation reconciliation re-enumerate every
    /// subfolder — a serial full directory scan each (the "mapped folder
    /// loads slowly" metadata churn).
    /// </summary>
    internal static bool DeriveFolderItemCountLoaded(
        bool targetIsFolder,
        bool targetIsLoaded,
        DateTime targetLastModified,
        bool sourceIsFolder,
        bool sourceIsLoaded,
        DateTime sourceLastModified)
    {
        return (targetIsFolder &&
                sourceIsFolder &&
                targetIsLoaded &&
                targetLastModified == sourceLastModified) ||
               sourceIsLoaded;
    }

    private static void ApplyRuntimeItemData(
        WidgetItem target,
        WidgetItem source,
        bool preserveExistingIconWhenMissing = false)
    {
        target.Name = source.Name;
        target.Path = source.Path;
        if (!string.IsNullOrWhiteSpace(source.TargetPath) ||
            string.IsNullOrWhiteSpace(target.TargetPath))
        {
            target.TargetPath = source.TargetPath;
        }
        else if (ShouldDropStaleHydratedShortcutTarget(
                     target.IsShortcut,
                     target.HydratedIconFileSize,
                     target.HydratedIconStamp,
                     source.IsShortcut,
                     source.FileSize,
                     source.LastModified))
        {
            // The .lnk itself changed since its target was read: drop the
            // stored target and the loaded flag so hydration re-reads it.
            // Snapshots never read targets, so without this guard every
            // refresh merge wiped the hydrated value.
            target.TargetPath = source.TargetPath;
            target.IsShortcutTargetLoaded = false;
        }
        if (!preserveExistingIconWhenMissing || source.Icon is not null)
        {
            target.Icon = source.Icon;
        }
        else if (ShouldDropStaleHydratedIcon(
                     target.Icon is not null,
                     target.HydratedIconFileSize,
                     target.HydratedIconStamp,
                     source.FileSize,
                     source.LastModified))
        {
            // Ledger mismatch: the file's size or timestamp moved since its
            // icon was extracted (sleep-time replacements, external edits).
            // Drop the reference so the hydration universe re-fetches under
            // the new, content-versioned cache key; matches keep the icon
            // without touching the cache at all.
            target.Icon = null;
        }
        target.FileSize = source.FileSize;
        if (source.IsFolderItemCountLoaded)
        {
            target.FolderItemCount = source.FolderItemCount;
        }
        // Computed before LastModified is overwritten below: the comparison
        // needs the target's previous timestamp.
        target.IsFolderItemCountLoaded = DeriveFolderItemCountLoaded(
            target.IsFolder,
            target.IsFolderItemCountLoaded,
            target.LastModified,
            source.IsFolder,
            source.IsFolderItemCountLoaded,
            source.LastModified);
        target.CreatedAt = source.CreatedAt;
        target.LastModified = source.LastModified;
        if (source.IsShellKindLoaded)
        {
            target.ShellKind = source.ShellKind;
            target.IsShellKindLoaded = true;
        }
        target.IsShortcut = source.IsShortcut;
        target.IsFolder = source.IsFolder;
    }

    private int CompareItems(WidgetItem left, WidgetItem right)
    {
        if (left.IsFolder != right.IsFolder)
        {
            return left.IsFolder ? -1 : 1;
        }

        int result = Config.SortMode switch
        {
            WidgetSortMode.Size => left.FileSize.CompareTo(right.FileSize),
            WidgetSortMode.Type => string.Compare(
                Path.GetExtension(left.Path),
                Path.GetExtension(right.Path),
                StringComparison.OrdinalIgnoreCase),
            WidgetSortMode.DateModified => left.LastModified.CompareTo(right.LastModified),
            WidgetSortMode.Manual => left.SortOrder.CompareTo(right.SortOrder),
            _ => NaturalStringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name)
        };

        if (result == 0)
        {
            result = NaturalStringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
        }

        if (result == 0)
        {
            result = NaturalStringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path);
        }

        return Config.SortDescending ? -result : result;
    }
}
