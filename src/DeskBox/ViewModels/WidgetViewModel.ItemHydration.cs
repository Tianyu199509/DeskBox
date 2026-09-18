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
    private void EnsureFolderBackedConfig()
    {
        if (!string.IsNullOrWhiteSpace(Config.MappedFolderPath))
        {
            Config.MappedFolderPath = Path.GetFullPath(Config.MappedFolderPath);
            return;
        }

        Config.FollowsDefaultStoragePath = true;
        Config.ManagedFolderName = string.IsNullOrWhiteSpace(Config.ManagedFolderName)
            ? CreateAvailableManagedFolderName(Config.Name, Config.Id)
            : Config.ManagedFolderName;
        Config.MappedFolderPath = Path.Combine(
            SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath),
            Config.ManagedFolderName);
        Directory.CreateDirectory(Config.MappedFolderPath);
        Config.Items.Clear();
        ResetAddedAtTracking();
        _settingsService.SaveDebounced();
    }

    private string CreateAvailableManagedFolderName(string displayName, string widgetId)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) || Directory.Exists(Path.Combine(rootPath, candidate)))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private async Task<bool> LoadFolderContentsAsync(
        string folderPath,
        bool clearIconCacheBeforeHydration = false,
        CancellationToken cancellationToken = default,
        Action? beforeItemsReplaced = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.LoadFolderContents",
            $"id={Config.Id} path={folderPath}");
        var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        long enumerateMs = -1;

        IReadOnlyList<WidgetItem> items;
        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (folderPath.Equals(userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            FolderEnumerationResult userResult = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    userDesktop,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            FolderEnumerationResult publicResult = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    publicDesktop,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);

            if (!FolderSnapshotStatusPolicy.IsSuccessful(userResult.Status) ||
                !FolderSnapshotStatusPolicy.IsSuccessful(publicResult.Status))
            {
                App.Log(
                    $"[FolderRefresh] Desktop snapshot incomplete; retaining existing items " +
                    $"user={userResult.Status} public={publicResult.Status}");
                return false;
            }

            items = userResult.Items.Concat(publicResult.Items)
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => !item.IsFolder)
                .ThenBy(item => item.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        else
        {
            var enumerateStopwatch = System.Diagnostics.Stopwatch.StartNew();
            FolderEnumerationResult result = await Task.Run(
                () => _fileService.EnumerateDirectoryForRefreshAsync(
                    folderPath,
                    hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                    showImageFilesAsIcons: _showImageFilesAsIcons,
                    showFileExtensions: _showFileExtensions,
                    hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                    loadIcons: false,
                    loadFolderItemCounts: false),
                cancellationToken).WaitAsync(cancellationToken);
            enumerateStopwatch.Stop();
            enumerateMs = enumerateStopwatch.ElapsedMilliseconds;
            if (!FolderSnapshotStatusPolicy.IsSuccessful(result.Status))
            {
                App.Log(
                    $"[FolderRefresh] Snapshot {result.Status}; retaining existing items for '{folderPath}'");
                return false;
            }

            items = result.Items;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_itemMutationBatchDepth > 0)
        {
            // Commit-point race guard. This reload may have started before an
            // import batch opened, and the enumeration above awaited seconds
            // on the thread pool while the Shell transfer finished and the
            // batch began. The snapshot is stale relative to files the
            // in-flight batch has already upserted; applying it mid-batch
            // would delete live items (files stay on disk, the UI just loses
            // them until the next refresh). Drop the snapshot and defer one
            // authoritative refresh to the batch finalization. No await may
            // appear between this check and the Items mutation below.
            _pendingFolderRefreshAfterBatch = true;
            MarkItemMutationBatchDirty();
            App.Log(
                "[FolderRefresh] Snapshot deferred to batch finalization " +
                $"'{folderPath}'");
            return false;
        }

        long afterEnumerateMs = loadStopwatch.ElapsedMilliseconds;
        ApplyPersistedAddedTimes(items);
        cancellationToken.ThrowIfCancellationRequested();
        beforeItemsReplaced?.Invoke();
        long afterAddedMs = loadStopwatch.ElapsedMilliseconds;
        SyncFolderItems(items);
        long afterSyncMs = loadStopwatch.ElapsedMilliseconds;
        SortItems();
        long afterSortMs = loadStopwatch.ElapsedMilliseconds;
        if (clearIconCacheBeforeHydration)
        {
            ClearCurrentItemIconCache();
        }

        StartItemHydration();
        if (loadStopwatch.ElapsedMilliseconds > 300)
        {
            // Folder entry is the one interaction users expect to be instant;
            // log slow loads by default so big-folder regressions surface
            // without opting into performance logging. enumMs includes
            // thread-pool queueing, addedMs covers persisted-time merge and
            // the pre-replace callback, syncMs the Items collection sync,
            // sortMs the final sort pass.
            App.Log(
                $"[FolderLoad] Slow load items={items.Count} " +
                $"totalMs={loadStopwatch.ElapsedMilliseconds} " +
                $"enumMs={(enumerateMs < 0 ? afterEnumerateMs : enumerateMs)} " +
                $"addedMs={afterAddedMs - afterEnumerateMs} " +
                $"syncMs={afterSyncMs - afterAddedMs} " +
                $"sortMs={afterSortMs - afterSyncMs} " +
                $"path='{folderPath}'");
        }

        return true;
    }

    private void SyncFolderItems(IReadOnlyList<WidgetItem> refreshedItems)
    {
        var existingByPath = Items
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var refreshedPaths = refreshedItems
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Config.SortMode == WidgetSortMode.Manual)
        {
            List<string> liveOrderPaths = Items.Select(item => item.Path).ToList();
            List<WidgetItem> snapshotItems = refreshedItems
                .Select(refreshedItem =>
                {
                    if (existingByPath.TryGetValue(refreshedItem.Path, out WidgetItem? existingItem))
                    {
                        ApplyRuntimeItemData(
                            existingItem,
                            refreshedItem,
                            preserveExistingIconWhenMissing: true);
                        return existingItem;
                    }

                    return refreshedItem;
                })
                .ToList();
            IReadOnlyList<WidgetItem> reconciled = WidgetManualOrderPolicy.Reconcile(
                snapshotItems,
                liveOrderPaths,
                Config.Items,
                item => item.Path);

            ApplyReconciledManualOrder(reconciled);
            NormalizeSortOrder();
            PersistManualOrderSnapshotIfChanged();
            return;
        }

        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!refreshedPaths.Contains(Items[index].Path))
            {
                Items.RemoveAt(index);
            }
        }

        for (int targetIndex = 0; targetIndex < refreshedItems.Count; targetIndex++)
        {
            var refreshedItem = refreshedItems[targetIndex];
            if (!existingByPath.TryGetValue(refreshedItem.Path, out var existingItem))
            {
                Items.Insert(targetIndex, refreshedItem);
                continue;
            }

            ApplyRuntimeItemData(
                existingItem,
                refreshedItem,
                preserveExistingIconWhenMissing: true);
            int currentIndex = Items.IndexOf(existingItem);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, existingItem);
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }

        NormalizeSortOrder();
    }

    private void ApplyReconciledManualOrder(IReadOnlyList<WidgetItem> reconciled)
    {
        var retained = reconciled.ToHashSet();
        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(Items[index]))
            {
                Items.RemoveAt(index);
            }
        }

        for (int targetIndex = 0; targetIndex < reconciled.Count; targetIndex++)
        {
            WidgetItem item = reconciled[targetIndex];
            int currentIndex = Items.IndexOf(item);
            if (currentIndex < 0)
            {
                Items.Insert(targetIndex, item);
            }
            else if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void StartItemHydration()
    {
        if (_isDisposed)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _itemHydrationGeneration);
        var cancellation = new CancellationTokenSource();
        CancelItemHydration(
            Interlocked.Exchange(
                ref _itemHydrationCancellation,
                cancellation));
        Interlocked.Increment(ref _itemHydrationActiveCount);
        _ = RunItemHydrationAsync(generation, cancellation);
    }

    /// <summary>
    /// True while an item-hydration pass (icons, folder counts, shortcut
    /// targets, shell kinds) is still running for the current items.
    /// Callers that treat "no icon loaded yet" as a failure must wait for
    /// this to turn false first: on a cold cache the pass legitimately runs
    /// for seconds before the first batch resolves.
    /// </summary>
    internal bool IsItemHydrationActive => Volatile.Read(ref _itemHydrationActiveCount) > 0;

    private static void CancelItemHydration(
        CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous generation completed and disposed its source while
            // a new snapshot was being scheduled.
        }
    }

    private async Task RunItemHydrationAsync(
        int generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(
                HydrateIconsWithRetryAsync(generation, cancellation.Token),
                HydrateFolderItemCountsAsync(generation, cancellation.Token),
                HydrateShortcutTargetsThenShellKindsAsync(
                    generation,
                    cancellation.Token));
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested)
        {
            // A newer folder snapshot owns hydration now. Cancellation is
            // expected and avoids retaining stale item batches during rapid
            // stack/folder navigation.
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ItemHydration] Generation failed for widget '{Name}' " +
                $"({Config.Id}): {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _itemHydrationActiveCount);
            Interlocked.CompareExchange(
                ref _itemHydrationCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void ClearCurrentItemIconCache()
    {
        foreach (var item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                item.Icon = null;
                _fileService.ClearIconCache(item.Path, _hideShortcutArrowOverlay, _showImageFilesAsIcons);
            }
        }
    }

    private void RefreshAllIcons()
    {
        ClearCurrentItemIconCache();
        StartItemHydration();
    }

    private async Task HydrateIconsWithRetryAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        await HydrateIconsAsync(
            generation,
            clearCacheBeforeLoad: false,
            cancellationToken: cancellationToken);

        for (int retry = 0; retry < IconHydrationRetryCount; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration) ||
                !Items.Any(item => item.Icon is null))
            {
                return;
            }

            await Task.Delay(
                s_iconHydrationRetryDelays[
                    Math.Min(retry, s_iconHydrationRetryDelays.Length - 1)],
                cancellationToken);
            await HydrateIconsAsync(
                generation,
                clearCacheBeforeLoad: true,
                cancellationToken: cancellationToken);
        }
    }

    private async Task HydrateIconsAsync(
        int generation,
        bool clearCacheBeforeLoad,
        CancellationToken cancellationToken)
    {
        var items = HydrationUniverseItems
            .Where(item => item.Icon is null)
            .OrderByDescending(item => item.IsShortcut)
            .ThenBy(item => item.SortOrder)
            .ToList();

        for (int start = 0; start < items.Count; start += IconHydrationBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = items
                .Skip(start)
                .Take(IconHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(item => HydrateIconAsync(
                    item,
                    generation,
                    clearCacheBeforeLoad,
                    cancellationToken))
                .ToArray();
            var results = await Task.WhenAll(batch);
            await WaitForCompactTransitionHydrationPauseAsync(generation, cancellationToken);

            foreach (var (item, icon) in results)
            {
                if (item is null)
                {
                    continue;
                }

                if (generation != Volatile.Read(ref _itemHydrationGeneration) ||
                    !Items.Contains(item))
                {
                    return;
                }

                SetItemIcon(item, icon, item.Path, generation);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// A compact bounds-transition animation owns the UI thread's frame
    /// budget. Holding a resolved batch here also holds the next batch's
    /// bitmap decode, which the icon pipeline dispatches onto the UI thread.
    /// </summary>
    private async Task WaitForCompactTransitionHydrationPauseAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        while (!_isDisposed &&
            Volatile.Read(ref _compactTransitionHydrationPauseCount) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            await Task.Delay(
                CompactTransitionHydrationPausePollMilliseconds,
                cancellationToken);
        }
    }

    internal void PauseIconHydrationForCompactTransition()
    {
        Interlocked.Increment(ref _compactTransitionHydrationPauseCount);
    }

    internal void ResumeIconHydrationAfterCompactTransition()
    {
        Interlocked.Decrement(ref _compactTransitionHydrationPauseCount);
    }

    private async Task<(WidgetItem? Item, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon)> HydrateIconAsync(
        WidgetItem item,
        int generation,
        bool clearCacheBeforeLoad,
        CancellationToken cancellationToken)
    {
        string path = item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, null);
        }

        try
        {
            if (clearCacheBeforeLoad)
            {
                _fileService.ClearIconCache(
                    path,
                    _hideShortcutArrowOverlay,
                    _showImageFilesAsIcons,
                    resetTransientFailures: false);
            }

            var icon = await _fileService.GetIconAsync(
                    path,
                    _hideShortcutArrowOverlay,
                    _showImageFilesAsIcons,
                    _iconDecodePixelWidth)
                .WaitAsync(cancellationToken);
            return (item, icon);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[IconHydration] Failed to load icon for '{path}' in widget '{Name}' ({Config.Id}): {ex.Message}");
            return (item, null);
        }
    }

    private async Task HydrateFolderItemCountsAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        var folders = HydrationUniverseItems
            .Where(item => item.IsFolder && !item.IsFolderItemCountLoaded)
            .ToList();
        int processed = 0;

        foreach (var item in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration) ||
                !Items.Contains(item) ||
                !Directory.Exists(item.Path))
            {
                return;
            }

            string path = item.Path;
            try
            {
                int count = await _fileService
                    .CountVisibleChildrenAsync(path)
                    .WaitAsync(cancellationToken);
                SetFolderItemCount(item, count, path, generation);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the last known count and leave the item retryable. A
                // transient UNC/provider failure must not become a cached zero.
                MarkFolderItemCountUnavailable(item, path, generation, ex);
            }
            processed++;

            if (processed % FolderCountHydrationBatchSize == 0)
            {
                await Task.Delay(
                    FolderCountHydrationYieldMs,
                    cancellationToken);
            }
        }
    }

    private async Task HydrateShortcutTargetsThenShellKindsAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        await HydrateShortcutTargetsAsync(generation, cancellationToken);
        await HydrateShellKindsAsync(generation, cancellationToken);
    }

    private async Task HydrateShortcutTargetsAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        var shortcuts = HydrationUniverseItems
            .Where(item => item.IsShortcut)
            .OrderBy(item => item.SortOrder)
            .ToList();

        for (int start = 0; start < shortcuts.Count; start += ShortcutTargetHydrationBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = shortcuts
                .Skip(start)
                .Take(ShortcutTargetHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(async item =>
                {
                    string expectedPath = item.Path;
                    string targetPath = await _fileService
                        .GetStoredShortcutTargetAsync(expectedPath)
                        .WaitAsync(cancellationToken);
                    return (Item: item, ExpectedPath: expectedPath, TargetPath: targetPath);
                })
                .ToArray();
            var results = await Task.WhenAll(batch);

            foreach (var result in results)
            {
                SetShortcutTarget(
                    result.Item,
                    result.TargetPath,
                    result.ExpectedPath,
                    generation);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task HydrateShellKindsAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        var items = HydrationUniverseItems
            .Where(item => !item.IsShellKindLoaded)
            .OrderBy(item => item.SortOrder)
            .ToList();

        for (int start = 0; start < items.Count; start += ShellKindHydrationBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _itemHydrationGeneration))
            {
                return;
            }

            var batch = items
                .Skip(start)
                .Take(ShellKindHydrationBatchSize)
                .Where(item => Items.Contains(item) && !string.IsNullOrWhiteSpace(item.Path))
                .Select(async item =>
                {
                    string expectedPath = item.Path;
                    string kind = await _fileService
                        .GetShellKindAsync(item)
                        .WaitAsync(cancellationToken);
                    return (Item: item, ExpectedPath: expectedPath, Kind: kind);
                })
                .ToArray();
            var results = await Task.WhenAll(batch);

            foreach (var result in results)
            {
                SetShellKind(
                    result.Item,
                    result.Kind,
                    result.ExpectedPath,
                    generation);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void SetItemIcon(
        WidgetItem item,
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage? icon,
        string expectedPath,
        int generation)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.Icon = icon;
            }

            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.Icon = icon;
            }
        });
    }

    private void SetFolderItemCount(WidgetItem item, int count, string expectedPath, int generation)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.FolderItemCount = count;
                item.IsFolderItemCountLoaded = true;
            }

            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.FolderItemCount = count;
                item.IsFolderItemCountLoaded = true;
            }
        });
    }

    private void SetShortcutTarget(
        WidgetItem item,
        string targetPath,
        string expectedPath,
        int generation)
    {
        void Apply()
        {
            if (CanApplyHydrationResult(item, expectedPath, generation))
            {
                item.TargetPath = targetPath;
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void MarkFolderItemCountUnavailable(
        WidgetItem item,
        string expectedPath,
        int generation,
        Exception exception)
    {
        App.LogVerbose(
            $"[FolderRefresh] Folder count unavailable for '{expectedPath}': " +
            exception.Message);

        void Apply()
        {
            if (!CanApplyHydrationResult(item, expectedPath, generation))
            {
                return;
            }

            // Preserve the previous value and deliberately keep the loaded
            // flag false so the next hydration generation retries it.
            item.IsFolderItemCountLoaded = false;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private void SetShellKind(WidgetItem item, string kind, string expectedPath, int generation)
    {
        void Apply()
        {
            if (!CanApplyHydrationResult(item, expectedPath, generation))
            {
                return;
            }

            bool categoryMayChange = !string.Equals(item.ShellKind, kind, StringComparison.OrdinalIgnoreCase);
            item.ShellKind = kind;
            item.IsShellKindLoaded = true;
            if (categoryMayChange && FileStackGroupBy == SettingsService.FileStackGroupByKind)
            {
                QueueStackDisplayRebuild();
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(Apply);
        }
    }

    private bool CanApplyHydrationResult(WidgetItem item, string expectedPath, int generation)
    {
        return generation == Volatile.Read(ref _itemHydrationGeneration) &&
               Items.Contains(item) &&
               string.Equals(item.Path, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshShortcutIconsAsync()
    {
        int shortcutCount = Items.Count(item => item.IsShortcut);
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshShortcutIcons",
            $"id={Config.Id} count={shortcutCount}");

        foreach (var item in Items.Where(item => item.IsShortcut))
        {
            item.Icon = await _fileService.GetIconAsync(
                item.Path,
                _hideShortcutArrowOverlay,
                _showImageFilesAsIcons,
                _iconDecodePixelWidth);
        }
    }

    private void RefreshItemDisplayNames()
    {
        foreach (var item in Items)
        {
            item.Name = FileService.GetDisplayName(
                item.Path,
                item.IsFolder,
                _showFileExtensions,
                _hideShortcutExtensionWhenShowingFileExtensions);
        }

        SortItems();
    }
}
