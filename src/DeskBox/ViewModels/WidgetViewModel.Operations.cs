using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public OrganizationHistoryEntry? GetLatestUndoableHistory() =>
        _organizerService.GetLatestUndoableEntry();

    public Task<OrganizationHistoryEntry?> RecordSubfolderMoveUndoAsync(
        IReadOnlyList<FileService.FileTransferResult> results,
        IReadOnlyDictionary<string, HashSet<string>?>? preExistingMergeSnapshots = null) =>
        _organizerService.RecordCompletedMoveHistoryAsync(
            Config,
            Name,
            results,
            preExistingMergeSnapshots);

    public async Task<int> UndoHistoryEntryAsync(string historyId)
    {
        OrganizationHistoryEntry? entry = _settingsService.OrganizationHistory.Entries
            .FirstOrDefault(h => string.Equals(h.Id, historyId, StringComparison.Ordinal));
        if (entry is null || !entry.CanUndo || entry.IsUndone)
        {
            return 0;
        }

        int previouslyRestored = entry.Items.Count(static item => item.IsRestored);
        await _organizerService.UndoAsync(entry.Id);
        int nowRestored = entry.Items.Count(static item => item.IsRestored);
        return Math.Max(0, nowRestored - previouslyRestored);
    }

    public Task InitializeAsync()
    {
        return InitializeAsync(CancellationToken.None);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IsLoading = true;
        try
        {
            EnsureFolderBackedConfig();
            cancellationToken.ThrowIfCancellationRequested();
            MappedFolderPath = Config.MappedFolderPath;
            _mappedFolderTraversalPath = ResolveMappedFolderTraversalPath();
            if (!string.IsNullOrWhiteSpace(MappedFolderPath) &&
                !string.IsNullOrWhiteSpace(_mappedFolderTraversalPath) &&
                !PathsEqual(MappedFolderPath, _mappedFolderTraversalPath) &&
                WidgetPathRebaseHelper.RebaseFolderTree(
                    Config,
                    MappedFolderPath,
                    _mappedFolderTraversalPath,
                    rebaseMappedFolderPath: false))
            {
                ApplyStackSettings();
                _settingsService.UpdateWidget(Config, notifySubscribers: false);
            }
            SetCurrentFolderPath(_mappedFolderTraversalPath);
            await ConfigureFolderWatchersAsync(CurrentFolderPath, cancellationToken);
            await ReloadFolderContentsAsync(
                CurrentFolderPath!,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsInitialized = !_isDisposed;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Add file or folder items to the widget, or move/copy them into a mapped folder.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportPathsAsync(
        IEnumerable<string> paths,
        bool? moveWhenMapped = null,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default,
        IProgress<FileService.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? preferredManualIndex = null,
        bool activateManualSortOnSuccess = false,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            LastImportSkippedUndisplayableCount = 0;
            return [];
        }

        // Entries the widget item list can never represent (hidden files)
        // must not be moved into managed storage: the folder would hold an
        // invisible file no tile can show. The probe does filesystem I/O, so
        // partition off the dispatcher thread.
        (List<string> displayablePaths, int skippedUndisplayableCount) =
            await Task.Run(() => PartitionDisplayableImportPaths(normalizedPaths));
        LastImportSkippedUndisplayableCount = skippedUndisplayableCount;
        if (skippedUndisplayableCount > 0)
        {
            App.Log(
                $"[Import] Refused undisplayable entries widget={Config.Id} " +
                $"skipped={skippedUndisplayableCount} " +
                $"requested={normalizedPaths.Count}");
        }

        if (displayablePaths.Count == 0)
        {
            return [];
        }

        EnsureFolderBackedConfig();
        MappedFolderPath = Config.MappedFolderPath;
        if (string.IsNullOrWhiteSpace(CurrentFolderPath))
        {
            _mappedFolderTraversalPath = ResolveMappedFolderTraversalPath();
            SetCurrentFolderPath(_mappedFolderTraversalPath);
        }
        else
        {
            await RefreshMappedRootTraversalPathAsync(cancellationToken);
        }

        string destinationFolderPath = CurrentFolderPath!;
        bool shouldMove = moveWhenMapped ?? ShouldMoveManagedItems(
            displayablePaths,
            destinationFolderPath);
        OrganizerOperationResult operation;
        try
        {
            operation = await _organizerService.OrganizeDropAsync(
                Config,
                Name,
                displayablePaths,
                shouldMove,
                useShellProgress,
                ownerWindowHandle,
                progress,
                cancellationToken,
                destinationFolderPath);
        }
        catch (Exception ex) when (
            ex is FileService.IFileTransferWithCompletedResults partial)
        {
            await ApplyImportedTransferResultsAsync(
                partial.CompletedResults,
                shouldMove,
                destinationFolderPath,
                preferredManualIndex,
                activateManualSortOnSuccess,
                preferredStackAnchor);
            throw;
        }

        // CompletedItems (not the persisted History entry) carries this
        // run's receipts: oversized batches are compacted to a summary
        // before the entry is returned.
        await ApplyImportedTransferResultsAsync(
            operation.CompletedItems.Select(item => new FileService.FileTransferResult(
                item.SourcePath,
                item.DestinationPath)),
            shouldMove,
            destinationFolderPath,
            preferredManualIndex,
            activateManualSortOnSuccess,
            preferredStackAnchor);

        return operation.CompletedItems
            .Select(item => Path.GetFullPath(item.SourcePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Number of paths the most recent <see cref="ImportPathsAsync"/> call
    /// refused because the widget item list can never display them. Import
    /// surfaces read this to explain a short count instead of reporting
    /// success for files that were never transferred.
    /// </summary>
    internal int LastImportSkippedUndisplayableCount { get; private set; }

    private static (List<string> DisplayablePaths, int SkippedUndisplayableCount)
        PartitionDisplayableImportPaths(IReadOnlyList<string> paths)
    {
        var displayablePaths = new List<string>(paths.Count);
        int skippedCount = 0;
        foreach (string path in paths)
        {
            if (FileService.IsFilteredFromWidgetDisplay(path))
            {
                skippedCount++;
            }
            else
            {
                displayablePaths.Add(path);
            }
        }

        return (displayablePaths, skippedCount);
    }

    private async Task ApplyImportedTransferResultsAsync(
        IEnumerable<FileService.FileTransferResult> results,
        bool shouldMove,
        string destinationFolderPath,
        int? preferredManualIndex = null,
        bool activateManualSortOnSuccess = false,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        FileService.FileTransferResult[] materialized = results
            .Where(result =>
                !string.IsNullOrWhiteSpace(result.SourcePath) &&
                !string.IsNullOrWhiteSpace(result.DestinationPath))
            .GroupBy(
                result => result.SourcePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();

        FolderSortStateSnapshot? originalSortState = null;
        bool insertedAny = false;
        var importedDestinationPaths = new List<string>();
        int nextManualIndex = preferredManualIndex ?? -1;

        // One scope for the whole batch: removals, the manual-mode switch and
        // every per-file upsert defer normalization, persistence, hydration
        // and the render/stack rebuild queues to the single finalization at
        // scope exit. The stack-anchor insertion and sort-mode commit below
        // deliberately run after that finalization so they observe the
        // normalized, settled item list.
        IDisposable batchScope = EnterItemMutationScope();
        var upsertStopwatch = Stopwatch.StartNew();
        try
        {
            if (shouldMove)
            {
                foreach (string sourcePath in materialized.Select(
                             result => result.SourcePath))
                {
                    if (Path.GetDirectoryName(sourcePath)?.Equals(
                            destinationFolderPath,
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        RemoveItemByPath(sourcePath);
                    }
                }
            }

            if (activateManualSortOnSuccess &&
                (preferredManualIndex.HasValue ||
                 preferredStackAnchor.HasValue) &&
                EffectiveSortMode != WidgetSortMode.Manual)
            {
                originalSortState = CaptureCurrentFolderSortStateAndActivateManual();
            }

            foreach (string destinationPath in materialized.Select(
                         result => result.DestinationPath))
            {
                if (!File.Exists(destinationPath) &&
                    !Directory.Exists(destinationPath))
                {
                    continue;
                }

                RecordFileAddedAt(destinationPath, DateTimeOffset.Now);
                bool inserted = await UpsertFolderItemAsync(
                    destinationPath,
                    nextManualIndex >= 0 ? nextManualIndex : null);
                if (inserted && nextManualIndex >= 0)
                {
                    nextManualIndex++;
                }

                insertedAny |= inserted;
                if (inserted)
                {
                    importedDestinationPaths.Add(destinationPath);
                }
            }
        }
        finally
        {
            upsertStopwatch.Stop();
            var finalizeStopwatch = Stopwatch.StartNew();
            batchScope.Dispose();
            finalizeStopwatch.Stop();
            if (materialized.Length > 0)
            {
                // upsertLoopMs and finalizeSyncMs together bound the
                // post-processing this widget runs on the UI thread; neither
                // alone is the before/after number.
                App.Log(
                    $"[OrganizerPerf] importBatch attempted={materialized.Length} " +
                    $"inserted={importedDestinationPaths.Count} " +
                    $"upsertLoopMs={upsertStopwatch.ElapsedMilliseconds} " +
                    $"finalizeSyncMs={finalizeStopwatch.ElapsedMilliseconds}");
            }
        }

        if (insertedAny &&
            preferredStackAnchor is { } stackAnchor)
        {
            ApplyImportedStackInsertion(
                importedDestinationPaths,
                stackAnchor);
        }

        if (originalSortState is { } capturedSortState)
        {
            if (insertedAny)
            {
                // UpsertFolderItemAsync persists a manual snapshot after the
                // first successful result. Keep the explicit mode/order
                // write here as a final idempotent commit for an empty or
                // already-present destination path batch.
                PersistManualOrder();
            }
            else
            {
                RestoreCurrentFolderSortState(capturedSortState);
            }
        }
    }

    /// <summary>
    /// Applies a previously created destination batch (for example shortcut
    /// files) at the external-drop insertion point. The automatic sort mode is
    /// committed only when at least one destination can be represented in the
    /// widget; an ACL/provider race therefore cannot leave a phantom Manual
    /// mode behind.
    /// </summary>
    internal async Task<int> ApplyManualInsertionAsync(
        IEnumerable<string> destinationPaths,
        int preferredManualIndex,
        bool activateManualSortOnSuccess,
        WidgetVisibleInsertionAnchor? preferredStackAnchor = null)
    {
        string[] paths = destinationPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return 0;
        }

        FolderSortStateSnapshot? originalSortState = null;
        if (activateManualSortOnSuccess &&
            EffectiveSortMode != WidgetSortMode.Manual)
        {
            originalSortState = CaptureCurrentFolderSortStateAndActivateManual();
        }

        int insertedCount = 0;
        var importedDestinationPaths = new List<string>();
        int nextManualIndex = Math.Max(0, preferredManualIndex);
        using (EnterItemMutationScope())
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    continue;
                }

                RecordFileAddedAt(path, DateTimeOffset.Now);
                if (await UpsertFolderItemAsync(path, nextManualIndex))
                {
                    insertedCount++;
                    nextManualIndex++;
                    importedDestinationPaths.Add(path);
                }
            }
        }

        if (insertedCount > 0 &&
            preferredStackAnchor is { } stackAnchor)
        {
            ApplyImportedStackInsertion(
                importedDestinationPaths,
                stackAnchor);
        }

        if (originalSortState is { } capturedSortState)
        {
            if (insertedCount > 0)
            {
                PersistManualOrder();
            }
            else
            {
                RestoreCurrentFolderSortState(capturedSortState);
            }
        }

        return insertedCount;
    }

    internal async Task<IReadOnlyList<string>> GetConfirmedMissingPathsAsync(
        IEnumerable<string> paths)
    {
        var confirmedMissing = new List<string>();
        foreach (IGrouping<string, string> group in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(path => !string.IsNullOrWhiteSpace(Path.GetDirectoryName(path)))
                     .GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase))
        {
            FolderPathSnapshot snapshot =
                await FileService.CaptureDirectChildSnapshotAsync(group.Key);
            if (!FolderSnapshotStatusPolicy.IsSuccessful(snapshot.Status))
            {
                continue;
            }

            confirmedMissing.AddRange(group.Where(path =>
                FileService.ClassifyDirectChild(snapshot, path) ==
                FolderEntryRefreshStatus.NotFound));
        }

        return confirmedMissing;
    }

    /// <summary>
    /// Toggle between icon and list views.
    /// </summary>
    [RelayCommand]
    public void ToggleViewMode()
    {
        ViewMode = ViewMode == ViewMode.Icon ? ViewMode.List : ViewMode.Icon;
        Config.ViewMode = ViewMode;
        _settingsService.SaveDebounced();
    }

    /// <summary>
    /// Open an item using the host window as the owner of any Shell UI.
    /// </summary>
    public FileService.OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)
    {
        var result = FileService.OpenItem(item, ownerHwnd);
        if (result == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(item.Path);
        }

        return result;
    }

    /// <summary>
    /// Opens an item on the bounded Shell worker and applies any collection
    /// change back on the caller's context.
    /// </summary>
    public async Task<FileService.OpenItemResult> OpenItemAsync(
        WidgetItem item,
        IntPtr ownerHwnd,
        CancellationToken cancellationToken = default)
    {
        string itemPath = item.Path;
        var result = await FileService.OpenItemAsync(
            item,
            ownerHwnd,
            cancellationToken);
        if (result == FileService.OpenItemResult.ShortcutDeleted)
        {
            RemoveItemByPath(itemPath);
        }

        return result;
    }

    /// <summary>
    /// Reveal an item in Explorer.
    /// </summary>
    [RelayCommand]
    public void ShowInExplorer(WidgetItem item)
    {
        FileService.ShowInExplorer(item);
    }

    public async Task<int> MoveItemBackToDesktopAsync(
        WidgetItem item,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var operation = await _organizerService.MoveItemBackToDesktopAsync(
            Config,
            Name,
            item,
            useShellProgress,
            ownerWindowHandle);
        if (operation.CompletedItems.Any(entry => string.Equals(entry.SourcePath, item.Path, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveItemByPath(item.Path);
            RemoveStackMemberOverridePaths([item.Path]);
            return 1;
        }

        return 0;
    }

    public async Task<int> MoveItemsBackToDesktopAsync(
        IEnumerable<WidgetItem> items,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(MappedFolderPath))
        {
            return 0;
        }

        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        OrganizerOperationResult operation;
        try
        {
            operation = await _organizerService.MoveItemsBackToDesktopAsync(
                Config,
                Name,
                targets.Select(item => item.Path),
                useShellProgress,
                ownerWindowHandle);
        }
        catch (FileService.FileTransferCanceledException ex)
        {
            // A shell transfer the user cancelled mid-way: the organizer
            // already recorded the completed set's history, so reconcile
            // the view and return the count — the surface turns it into the
            // same moved-N/none-moved feedback the retired legacy bridge's
            // silent partial return used to produce.
            var canceledMovedPaths = ex.CompletedResults
                .Select(result => result.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in targets.Where(item => canceledMovedPaths.Contains(item.Path)))
            {
                RemoveItemByPath(item.Path);
            }

            RemoveStackMemberOverridePaths(canceledMovedPaths);
            return canceledMovedPaths.Count;
        }

        // CompletedItems (not the persisted History entry) carries this
        // run's receipts: oversized batches are compacted to a summary
        // before the entry is returned.
        var movedSourcePaths = operation.CompletedItems
            .Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in targets.Where(item => movedSourcePaths.Contains(item.Path)))
        {
            RemoveItemByPath(item.Path);
        }

        RemoveStackMemberOverridePaths(movedSourcePaths);

        return movedSourcePaths.Count;
    }

    public async Task RefreshFromConfigAsync(
        bool clearIconCacheBeforeHydration = true)
    {
        using var perfScope = PerformanceLogger.Measure(
            "WidgetViewModel.RefreshFromConfig",
            $"id={Config.Id} name={Name}");

        Config.WidgetKind = WidgetKind.File;
        Config.IsDisabled = false;
        EnsureFolderBackedConfig();

        MappedFolderPath = Config.MappedFolderPath;
        SetCurrentFolderPath(ResolveCurrentFolderForMappedRoot());
        OnPropertyChanged(nameof(FollowsDefaultStoragePath));
        ApplyStackSettings();

        if (!clearIconCacheBeforeHydration)
        {
            // The freshness ledger at the refresh merge owns icon
            // invalidation now; a recovery refresh additionally resets
            // retry-state markers so timed-out sources get a fresh attempt —
            // the wake-recovery property the blanket clear used to carry,
            // minus the visible placeholder churn on mapped folders.
            _fileService.ResetIconTransientFailures();
        }

        await ConfigureFolderWatchersAsync(CurrentFolderPath);
        await ReloadFolderContentsAsync(
            CurrentFolderPath!,
            clearIconCacheBeforeHydration);
        UpdateDependentProperties();
    }

    /// <summary>
    /// Lightweight folder refresh that only reloads the contents without
    /// restarting the folder watcher.  Use this when the watcher is already
    /// running and you just need to re-read the current disk state — e.g.
    /// after a drag-out operation where the Shell may still be moving files.
    /// Uses the same semaphore as <see cref="OnFolderChanged"/> to avoid
    /// concurrent <see cref="LoadFolderContentsAsync"/> calls.
    /// </summary>
    public async Task RefreshFolderContentsAsync()
    {
        if (_isDisposed || string.IsNullOrEmpty(CurrentFolderPath))
        {
            return;
        }

        string? refreshPath = await RefreshMappedRootTraversalPathAsync();
        if (string.IsNullOrWhiteSpace(refreshPath))
        {
            return;
        }

        await ReloadFolderContentsAsync(refreshPath);
        if (!_isDisposed)
        {
            UpdateDependentProperties();
        }
    }

    private async Task<bool> ReloadFolderContentsAsync(
        string expectedFolderPath,
        bool clearIconCacheBeforeHydration = false,
        CancellationToken cancellationToken = default,
        Action? beforeItemsReplaced = null,
        bool allowFolderPathTransition = false)
    {
        if (_isDisposed)
        {
            return false;
        }

        var gateStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _folderRefreshGate.WaitAsync(cancellationToken);
        gateStopwatch.Stop();
        if (gateStopwatch.ElapsedMilliseconds > 300)
        {
            // A long gate wait means another load (usually a watcher-triggered
            // full reload) still owned the folder while the user navigated.
            App.Log(
                $"[FolderLoad] Refresh gate waitMs={gateStopwatch.ElapsedMilliseconds} " +
                $"path='{expectedFolderPath}'");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_isDisposed ||
                string.IsNullOrEmpty(CurrentFolderPath) ||
                (!allowFolderPathTransition && !string.Equals(
                    Path.GetFullPath(CurrentFolderPath),
                    Path.GetFullPath(expectedFolderPath),
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return await LoadFolderContentsAsync(
                expectedFolderPath,
                clearIconCacheBeforeHydration,
                cancellationToken,
                beforeItemsReplaced);
        }
        finally
        {
            _folderRefreshGate.Release();
        }
    }

    public Task HandleItemsMovedOutAsync(IEnumerable<string> sourcePaths)
    {
        var normalizedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (normalizedPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        // The mirror of the import batch: an external drag-out of 2000 files
        // used to pay the per-removal derived work 2000 times - most
        // visibly one UpdateWidget/SaveDebounced reschedule per departing
        // item through the AddedAt persistence (convicted by allocation
        // tracing in the 2026-09-17 memory investigation). The mutation
        // scope folds them into one end-of-batch finalization; each real
        // departure still runs RemoveItemByPath itself.
        using (EnterItemMutationScope())
        {
            foreach (var path in normalizedPaths)
            {
                RemoveItemByPath(path);
            }

            RemoveStackMemberOverridePaths(normalizedPaths);
        }

        return Task.CompletedTask;
    }

    public async Task RenameItemAsync(WidgetItem item, string newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string sanitizedName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.NameRequired"));
        }

        string sourcePath = Path.GetFullPath(item.Path);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Validation.FolderUnknown"));
        }

        string destinationName = FileService.ResolveRenameDestination(
            Path.GetFileName(sourcePath),
            sanitizedName,
            item.IsFolder,
            ShortcutHelper.IsShortcutPath(sourcePath),
            _showFileExtensions,
            out bool requiresExtensionChangeConfirmation);
        string destinationPath = Path.Combine(parentDirectory, destinationName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return;
        }

        // Ask before any other validation so a declined extension change never
        // surfaces follow-up errors (e.g. a name collision with the new name).
        if (requiresExtensionChangeConfirmation &&
            ConfirmExtensionChangeHandler?.Invoke(sourcePath, destinationPath) != true)
        {
            App.Log(
                $"[Widget] Rename extension change declined " +
                $"source='{sourcePath}' destination='{destinationPath}'");
            return;
        }

        bool isCaseOnlyRename = FileService.IsCaseOnlyPathChange(
            sourcePath,
            destinationPath);
        if (!isCaseOnlyRename &&
            (File.Exists(destinationPath) || Directory.Exists(destinationPath)))
        {
            throw new IOException(_localizationService.T("Widget.Validation.TargetExists"));
        }

        bool refreshGateHeld = false;
        bool restartWatchers = false;
        try
        {
            if (isCaseOnlyRename)
            {
                await _folderRefreshGate.WaitAsync();
                refreshGateHeld = true;
                _folderWatcher?.Stop();
                _publicFolderWatcher?.Stop();
                restartWatchers = true;
            }

            await _fileService.RenameEntryAsync(sourcePath, destinationPath);
            TransferFileAddedAt(sourcePath, destinationPath);
            var refreshedItem = await _fileService.CreateWidgetItemAsync(
                destinationPath,
                hideShortcutArrowOverlay: _hideShortcutArrowOverlay,
                showImageFilesAsIcons: _showImageFilesAsIcons,
                showFileExtensions: _showFileExtensions,
                hideShortcutExtensionWhenShowingFileExtensions: _hideShortcutExtensionWhenShowingFileExtensions,
                loadIcon: false,
                loadFolderItemCount: false,
                loadShortcutTarget: false);
            ApplyRuntimeItemData(item, refreshedItem);
            UpdateStackMemberOverridePath(
                sourcePath,
                destinationPath);
            StartItemHydration();

            int originalIndex = Items.IndexOf(item);
            if (originalIndex >= 0)
            {
                if (EffectiveSortMode != WidgetSortMode.Manual)
                {
                    Items.RemoveAt(originalIndex);
                    Items.Insert(GetSortedInsertIndex(item), item);
                }

                NormalizeSortOrder();
                PersistManualOrderSnapshotIfChanged();
            }
        }
        finally
        {
            try
            {
                if (restartWatchers &&
                    !_isDisposed &&
                    !string.IsNullOrWhiteSpace(CurrentFolderPath))
                {
                    await ConfigureFolderWatchersAsync(CurrentFolderPath);
                }
            }
            finally
            {
                if (refreshGateHeld)
                {
                    _folderRefreshGate.Release();
                }
            }
        }
    }

    public async Task<FileDeleteBatchResult> DeleteItemsAsync(
        IEnumerable<WidgetItem> items,
        bool recycle = true,
        IntPtr ownerHandle = default)
    {
        var targets = items
            .Where(item => item is not null)
            .Distinct()
            .ToList();

        if (targets.Count == 0)
        {
            return new FileDeleteBatchResult(0, []);
        }

        int deletedCount = 0;
        var failures = new List<FileDeleteFailure>();
        var successfulPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ownerHandle != IntPtr.Zero)
        {
            int permanentlyDeletedCount = 0;
            try
            {
                FileService.ShellDeleteOutcome? shellOutcome = null;
                IReadOnlySet<string> deletedPaths =
                    await _fileService.DeleteEntriesWithShellAsync(
                        targets.Select(item => item.Path),
                        recycle,
                        ownerHandle,
                        outcome => shellOutcome = outcome);

                var failedByPath = shellOutcome?.Failures
                    .ToDictionary(f => f.Path, f => f.HResult, StringComparer.OrdinalIgnoreCase);
                foreach (WidgetItem item in targets)
                {
                    string normalizedPath = Path.GetFullPath(item.Path);
                    if (deletedPaths.Contains(normalizedPath))
                    {
                        Items.Remove(item);
                        RemoveFileAddedAt(item.Path);
                        successfulPaths.Add(normalizedPath);
                        deletedCount++;
                        if (shellOutcome?.PermanentlyDeletedPaths.Contains(normalizedPath) == true)
                        {
                            permanentlyDeletedCount++;
                        }

                        continue;
                    }

                    if (failedByPath is not null &&
                        failedByPath.TryGetValue(normalizedPath, out int hr))
                    {
                        string errorMessage = System.Runtime.InteropServices.Marshal.GetExceptionForHR(hr)?.Message
                            ?? $"0x{hr:X8}";
                        failures.Add(new FileDeleteFailure(
                            item.Path,
                            item.Name,
                            errorMessage));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A Shell failure can happen after a subset of a batch has
                // completed. Keep the model in sync with the filesystem and
                // report only entries that still exist as failures.
                foreach (WidgetItem item in targets)
                {
                    string normalizedPath = Path.GetFullPath(item.Path);
                    if (!File.Exists(normalizedPath) &&
                        !Directory.Exists(normalizedPath))
                    {
                        Items.Remove(item);
                        RemoveFileAddedAt(item.Path);
                        successfulPaths.Add(normalizedPath);
                        deletedCount++;
                        continue;
                    }

                    failures.Add(new FileDeleteFailure(
                        item.Path,
                        item.Name,
                        ex.Message));
                }
            }

            NormalizeSortOrder();
            RemoveStackMemberOverridePaths(
                targets
                    .Where(item => successfulPaths.Contains(Path.GetFullPath(item.Path)))
                    .Select(item => item.Path));
            return new FileDeleteBatchResult(deletedCount, failures, permanentlyDeletedCount);
        }

        foreach (var item in targets)
        {
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            {
                Items.Remove(item);
                RemoveFileAddedAt(item.Path);
                successfulPaths.Add(Path.GetFullPath(item.Path));
                deletedCount++;
                continue;
            }

            try
            {
                bool deleted = await _fileService.DeleteEntryAsync(
                    item.Path,
                    recycle,
                    ownerHandle);
                if (!deleted)
                {
                    // The user answered "No" to the Shell confirmation; keep
                    // the item and do not count it as deleted or failed.
                    continue;
                }

                Items.Remove(item);
                RemoveFileAddedAt(item.Path);
                successfulPaths.Add(Path.GetFullPath(item.Path));
                deletedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new FileDeleteFailure(
                    item.Path,
                    item.Name,
                    ex.Message));
            }
        }

        NormalizeSortOrder();
        RemoveStackMemberOverridePaths(
            targets
                .Where(item => successfulPaths.Contains(Path.GetFullPath(item.Path)))
                .Select(item => item.Path));
        return new FileDeleteBatchResult(deletedCount, failures);
    }

    /// <summary>
    /// Recycles dropped paths for the Recycle Bin namespace tile — the
    /// bin-drop entry point, where the payload is a list of paths (external
    /// desktop/Explorer files) rather than <see cref="WidgetItem"/>s. The
    /// Shell delete carries its own confirmation per the user's Explorer
    /// settings, exactly like the item delete pipeline. Model entries whose
    /// paths were actually deleted are removed in the same pass.
    /// </summary>
    public async Task<FileDeleteBatchResult> DeleteDroppedPathsForBinAsync(
        IReadOnlyList<string> paths,
        IntPtr ownerHandle)
    {
        string[] targets = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(path => path.Length > 0 &&
                (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
        {
            return new FileDeleteBatchResult(0, []);
        }

        int deletedCount = 0;
        var failures = new List<FileDeleteFailure>();
        if (ownerHandle == IntPtr.Zero)
        {
            return new FileDeleteBatchResult(0, failures);
        }

        int permanentlyDeletedCount = 0;
        try
        {
            FileService.ShellDeleteOutcome? shellOutcome = null;
            IReadOnlySet<string> deletedPaths =
                await _fileService.DeleteEntriesWithShellAsync(
                    targets,
                    recycle: true,
                    ownerHandle,
                    outcome => shellOutcome = outcome);
            deletedCount = deletedPaths.Count;
            permanentlyDeletedCount = shellOutcome?.PermanentlyDeletedPaths.Count ?? 0;
            if (shellOutcome is { Failures.Count: > 0 })
            {
                foreach (FileService.ShellDeleteItemFailure failure in shellOutcome.Failures)
                {
                    string message = System.Runtime.InteropServices.Marshal.GetExceptionForHR(failure.HResult)?.Message
                        ?? $"0x{failure.HResult:X8}";
                    failures.Add(new FileDeleteFailure(
                        failure.Path,
                        Path.GetFileName(failure.Path),
                        message));
                }
            }

            // Keep the model in sync: same-widget drops arrive as paths, and
            // the watcher refresh alone would leave a stale tile visible
            // until the next folder pass.
            foreach (WidgetItem item in Items.ToList())
            {
                if (deletedPaths.Contains(Path.GetFullPath(item.Path)))
                {
                    Items.Remove(item);
                    RemoveFileAddedAt(item.Path);
                }
            }

            NormalizeSortOrder();
            RemoveStackMemberOverridePaths(
                targets.Where(deletedPaths.Contains));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failures.Add(new FileDeleteFailure(
                targets[0],
                Path.GetFileName(targets[0]),
                ex.Message));
        }

        return new FileDeleteBatchResult(deletedCount, failures, permanentlyDeletedCount);
    }

    public void UpdateBounds(double x, double y, double width, double height, bool persist)
    {
        Config.X = x;
        Config.Y = y;
        Config.Width = width;
        Config.Height = height;

        if (persist)
        {
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
        }
    }

    /// <summary>
    /// Rename the widget.
    /// </summary>
    public async Task RenameAsync(string newName)
    {
        if (App.Current?.WidgetManager is { } widgetManager)
        {
            await widgetManager.RenameWidgetAsync(Config.Id, newName);
            Name = Config.Name;
            MappedFolderPath = Config.MappedFolderPath;
            SetCurrentFolderPath(ResolveCurrentFolderForMappedRoot());
            OnPropertyChanged(nameof(FollowsDefaultStoragePath));
            return;
        }

        Name = newName;
        Config.Name = newName;
        Config.IsDefaultTitle = false;
        _settingsService.UpdateWidget(Config);
    }

    public void SetPositionLocked(bool value)
    {
        if (IsPositionLocked == value)
        {
            return;
        }

        IsPositionLocked = value;
        Config.IsPositionLocked = value;
        _settingsService.UpdateWidget(Config);
    }

    public void SetSizeLocked(bool value)
    {
        if (IsSizeLocked == value)
        {
            return;
        }

        IsSizeLocked = value;
        Config.IsSizeLocked = value;
        _settingsService.UpdateWidget(Config);
    }
}
