using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
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
    /// Moves an item to a new position within the Items collection.
    /// Only effective when SortMode is Manual; otherwise the call is ignored.
    /// </summary>
    public bool TryReorderItem(WidgetItem item, int targetIndex)
    {
        if (Config.SortMode != WidgetSortMode.Manual || !IsAtMappedRoot)
        {
            return false;
        }

        int currentIndex = Items.IndexOf(item);
        if (currentIndex < 0 || currentIndex == targetIndex)
        {
            return false;
        }

        // Clamp targetIndex to valid range.
        targetIndex = Math.Clamp(targetIndex, 0, Items.Count - 1);
        if (currentIndex == targetIndex)
        {
            return false;
        }

        Items.Move(currentIndex, targetIndex);
        NormalizeSortOrder();
        SyncConfigItemsOrder();
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        return true;
    }

    /// <summary>
    /// Moves an item to a new position without persisting to config.
    /// Used for real-time reordering during drag-over for visual feedback.
    /// Switches to Manual mode if needed.  Call PersistManualOrder on drop.
    /// </summary>
    public bool MoveItemForReorder(WidgetItem item, int targetIndex)
    {
        if (Config.SortMode != WidgetSortMode.Manual)
        {
            return false;
        }

        int currentIndex = Items.IndexOf(item);
        if (currentIndex < 0)
        {
            return false;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Items.Count - 1);
        if (currentIndex == targetIndex)
        {
            return false;
        }

        Items.Move(currentIndex, targetIndex);
        return true;
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
                await LoadFolderContentsAsync(CurrentFolderPath);
                return;
            }

            FolderPathSnapshot snapshot =
                await FileService.CaptureDirectChildSnapshotAsync(changeBatch.WatchedPath);
            if (!FolderSnapshotStatusPolicy.IsSuccessful(snapshot.Status))
            {
                App.Log(
                    $"[FolderRefresh] Incremental root unavailable; retaining snapshot: " +
                    $"'{changeBatch.WatchedPath}'");
                return;
            }

            foreach (var change in changeBatch.Changes)
            {
                await ApplyFolderChangeAsync(change, snapshot);
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

    private bool ShouldUseFullReload(FolderChangeBatch changeBatch, string mappedFolderPath)
    {
        if (changeBatch.RequiresFullReload || changeBatch.Changes.Count == 0 || changeBatch.Changes.Count > IncrementalRefreshBatchThreshold)
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

    private async Task ApplyFolderChangeAsync(
        FolderChange change,
        FolderPathSnapshot snapshot)
    {
        if (change.ChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrWhiteSpace(change.OldFullPath))
        {
            int previousManualIndex = Config.SortMode == WidgetSortMode.Manual
                ? FindItemIndexByPath(change.OldFullPath)
                : -1;
            FolderEntryRefreshStatus oldState =
                FileService.ClassifyDirectChild(snapshot, change.OldFullPath);
            FolderEntryRefreshStatus newState =
                FileService.ClassifyDirectChild(snapshot, change.FullPath);
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
            FileService.ClassifyDirectChild(snapshot, change.FullPath);
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

    private async Task UpsertFolderItemAsync(
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
            return;
        }

        int existingIndex = FindItemIndexByPath(path);
        if (Config.SortMode == WidgetSortMode.Manual && existingIndex >= 0)
        {
            AssignAddedAt(item);
            item.SortOrder = existingIndex;
            Items[existingIndex] = item;
            NormalizeSortOrder();
            PersistManualOrderSnapshotIfChanged();
            StartItemHydration();
            return;
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
        NormalizeSortOrder();
        PersistManualOrderSnapshotIfChanged();
        StartItemHydration();
    }

    private void RemoveItemByPath(string path, bool persistManualOrder = true)
    {
        int index = FindItemIndexByPath(path);
        if (index < 0)
        {
            return;
        }

        Items.RemoveAt(index);
        RemoveFileAddedAt(path);
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

        for (int index = 0; index < Items.Count; index++)
        {
            if (CompareItems(candidate, Items[index]) < 0)
            {
                return index;
            }
        }

        return Items.Count;
    }

    private void NormalizeSortOrder()
    {
        for (int index = 0; index < Items.Count; index++)
        {
            Items[index].SortOrder = index;
        }
    }

    private static void ApplyRuntimeItemData(
        WidgetItem target,
        WidgetItem source,
        bool preserveExistingIconWhenMissing = false)
    {
        target.Name = source.Name;
        target.Path = source.Path;
        target.TargetPath = source.TargetPath;
        if (!preserveExistingIconWhenMissing || source.Icon is not null)
        {
            target.Icon = source.Icon;
        }
        target.FileSize = source.FileSize;
        if (source.IsFolderItemCountLoaded)
        {
            target.FolderItemCount = source.FolderItemCount;
        }
        target.IsFolderItemCountLoaded = source.IsFolderItemCountLoaded;
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

    private string BuildRenameFileName(string sanitizedName, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return sanitizedName;
        }

        if (_showFileExtensions)
        {
            return sanitizedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? sanitizedName
                : sanitizedName + extension;
        }

        return sanitizedName + extension;
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
