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
        if (Config.SortMode != WidgetSortMode.Manual)
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
        _settingsService.SaveDebounced(notifySubscribers: false);
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
        if (Config.SortMode != WidgetSortMode.Manual)
        {
            return;
        }

        NormalizeSortOrder();
        SyncConfigItemsOrder();
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        _settingsService.SaveDebounced(notifySubscribers: false);
    }

    /// <summary>
    /// Rebuilds Config.Items to match the current Items collection order.
    /// Called after a manual reorder to persist the new order.
    /// </summary>
    private void SyncConfigItemsOrder()
    {
        Config.Items.Clear();
        foreach (var item in Items)
        {
            Config.Items.Add(new WidgetItemConfig
            {
                Path = item.Path,
                SortOrder = item.SortOrder
            });
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

    private async Task ConfigureFolderWatchersAsync(string? folderPath)
    {
        _folderWatcher.Stop();
        _publicFolderWatcher.Stop();

        if (string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        await _folderWatcher.StartAsync(folderPath);

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            await _publicFolderWatcher.StartAsync(publicDesktop);
        }
    }

    private async void OnFolderChanged(FolderChangeBatch changeBatch)
    {
        if (string.IsNullOrEmpty(MappedFolderPath))
        {
            return;
        }

        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.OnFolderChanged",
            $"id={Config.Id} changes={changeBatch.Changes.Count} fullReload={changeBatch.RequiresFullReload}");

        await _folderRefreshGate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(MappedFolderPath))
            {
                return;
            }

            if (ShouldUseFullReload(changeBatch, MappedFolderPath))
            {
                await LoadFolderContentsAsync(MappedFolderPath);
                return;
            }

            foreach (var change in changeBatch.Changes)
            {
                await ApplyFolderChangeAsync(change);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[FolderRefresh] Incremental refresh failed for '{MappedFolderPath}': {ex}");
            if (!string.IsNullOrEmpty(MappedFolderPath))
            {
                await LoadFolderContentsAsync(MappedFolderPath);
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

    private async Task ApplyFolderChangeAsync(FolderChange change)
    {
        if (change.ChangeType == WatcherChangeTypes.Renamed && !string.IsNullOrWhiteSpace(change.OldFullPath))
        {
            TransferFileAddedAt(change.OldFullPath, change.FullPath);
            RemoveItemByPath(change.OldFullPath);
            await UpsertFolderItemAsync(change.FullPath);
            return;
        }

        if (change.ChangeType == WatcherChangeTypes.Deleted)
        {
            RemoveItemByPath(change.FullPath);
            return;
        }

        await UpsertFolderItemAsync(change.FullPath);
    }

    private async Task UpsertFolderItemAsync(string path)
    {
        var item = await _fileService.TryCreateWidgetItemAsync(
            path,
            hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
            showImageFilesAsIcons: _showImageFilesAsIcons,
            showFileExtensions: _showFileExtensions,
            hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
            loadIcon: false,
            loadFolderItemCount: false);
        if (item is null)
        {
            RemoveItemByPath(path);
            return;
        }

        int existingIndex = FindItemIndexByPath(path);
        if (existingIndex >= 0)
        {
            Items.RemoveAt(existingIndex);
        }

        AssignAddedAt(item);

        int insertIndex = GetSortedInsertIndex(item);
        item.SortOrder = insertIndex;
        Items.Insert(insertIndex, item);
        NormalizeSortOrder();
        StartItemHydration();
    }

    private void RemoveItemByPath(string path)
    {
        int index = FindItemIndexByPath(path);
        if (index < 0)
        {
            return;
        }

        Items.RemoveAt(index);
        RemoveFileAddedAt(path);
        NormalizeSortOrder();
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

    private static void ApplyRuntimeItemData(WidgetItem target, WidgetItem source)
    {
        target.Name = source.Name;
        target.Path = source.Path;
        target.TargetPath = source.TargetPath;
        target.Icon = source.Icon;
        target.FileSize = source.FileSize;
        target.FolderItemCount = source.FolderItemCount;
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
