// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Helpers;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Partial class containing Storage logic for WidgetManager.
/// </summary>
public sealed partial class WidgetManager
{

    public void SyncMappedWidgetShortcut(string widgetId)
    {
        var config = FindConfig(widgetId);
        if (config is null || IsDeleted(widgetId))
        {
            return;
        }

        SyncMappedWidgetShortcut(config);
    }

    /// <summary>
    /// Recreates the managed storage root and syncs mapped-widget shortcuts.
    /// Returns false when the root cannot be created (e.g. its drive is
    /// currently detached); callers must treat that as non-fatal so widget
    /// restoration continues without the root.
    /// </summary>
    public bool SyncStorageFolderEntries()
    {
        string rootPath = GetManagedStorageRootPath();
        try
        {
            Directory.CreateDirectory(rootPath);
        }
        catch (Exception ex)
        {
            // A detached drive must not abort the rest of startup: widgets
            // restore with unavailable folders and reconnect when the drive
            // returns.
            App.Log($"[WidgetManager] Managed storage root is unavailable ('{rootPath}'): {ex.Message}");
            return false;
        }

        var activeWidgetIds = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
            .Select(widget => widget.Id)
            .ToHashSet(StringComparer.Ordinal);

        RemoveStaleMappedWidgetShortcuts(rootPath, activeWidgetIds);

        foreach (var config in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !IsDeleted(widget.Id))
                     .ToList())
        {
            if (config.FollowsDefaultStoragePath)
            {
                continue;
            }

            SyncMappedWidgetShortcut(config);
        }

        return true;
    }

    public bool CanCleanupManagedStorageForWidget(string widgetId)
    {
        var config = FindConfig(widgetId);
        return config is not null &&
               IsDefaultManagedStorageFolder(config.MappedFolderPath) &&
               config.FollowsDefaultStoragePath &&
               Directory.Exists(config.MappedFolderPath);
    }

    public IReadOnlyList<ManagedStorageFolderCleanupCandidate> GetOrphanManagedStorageFolders()
    {
        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(widget, rootPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateDirectories(rootPath)
            .Select(Path.GetFullPath)
            .Where(path => !activePaths.Contains(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Select(path => new ManagedStorageFolderCleanupCandidate(
                Path.GetFileName(path),
                path,
                CountDirectoryEntries(path)))
            .OrderBy(candidate => candidate.Name, NaturalStringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await MoveManagedFolderContentsToDesktopAsync(normalizedPath);
    }

    public async Task DeleteOrphanManagedStorageFolderAsync(string folderPath)
    {
        string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
        await _fileService.DeleteEntryAsync(normalizedPath, recycle: _recycleManagedFolderDeletes);
    }

    public async Task<int> RestoreOrphanManagedStorageFoldersAsync(IEnumerable<string> folderPaths)
    {
        int restoredCount = 0;
        bool canCreateWindow = CanCreateWidgetWindowOnCurrentThread();
        foreach (var folderPath in folderPaths)
        {
            string normalizedPath = ValidateOrphanManagedStorageFolderPath(folderPath);
            if (!Directory.Exists(normalizedPath))
            {
                continue;
            }

            string folderName = Path.GetFileName(normalizedPath);
            var config = new WidgetConfig
            {
                Name = string.IsNullOrWhiteSpace(folderName)
                    ? _localizationService.T("Widget.DefaultNameShort")
                    : folderName,
                WidgetKind = WidgetKind.File,
                MappedFolderPath = normalizedPath,
                FollowsDefaultStoragePath = true,
                ManagedFolderName = folderName,
                BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion,
                Width = _settingsService.Settings.DefaultWidgetWidth,
                Height = _settingsService.Settings.DefaultWidgetHeight,
                IsVisible = true,
                IsDisabled = false
            };

            _settingsService.Settings.Widgets.Add(config);
            if (canCreateWindow)
            {
                await CreateWidgetFromConfigAsync(config, revealAfterCreate: true);
            }
            restoredCount++;
            App.Log($"[WidgetManager] Restored orphan managed storage folder as widget: {normalizedPath} -> {config.Id}");
        }

        if (restoredCount > 0)
        {
            await _settingsService.SaveAsync();
        }

        return restoredCount;
    }

    public async Task NotifyItemsMovedOutAsync(string widgetId, IEnumerable<string> sourcePaths)
    {
        if (IsDeleted(widgetId))
        {
            return;
        }

        string[] paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        if (_fileWidgets.TryGetValue(widgetId, out var entry))
        {
            await entry.ViewModel.HandleItemsMovedOutAsync(paths);
            return;
        }

        ContentWidgetWindow? contentWindow =
            _contentWidgets.TryGetValue(widgetId, out var registeredWindow)
                ? registeredWindow
                : _contentWidgets.Values
                    .Distinct()
                    .FirstOrDefault(window =>
                        window.CurrentContent is FileWidgetContentAdapter surface &&
                        string.Equals(
                            surface.WidgetId,
                            widgetId,
                            StringComparison.Ordinal));
        if (contentWindow?.CurrentContent is not FileWidgetContentAdapter fileSurface ||
            !string.Equals(
                fileSurface.WidgetId,
                widgetId,
                StringComparison.Ordinal))
        {
            App.Log(
                $"[WidgetManager] Moved-out refresh target not loaded " +
                $"widget={widgetId} paths={paths.Length}");
            return;
        }

        await fileSurface.ViewModel.HandleItemsMovedOutAsync(paths);
    }

    public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath,
        ManagedStorageMigrationOptions? options = null)
    {
        string oldRootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        string normalizedNewRootPath = SettingsService.NormalizeManagedStorageRootPath(newRootPath);

        if (string.Equals(oldRootPath, normalizedNewRootPath, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            await _settingsService.SaveAsync();
            return new ManagedStorageMigrationResult(
                0,
                oldRootPath,
                normalizedNewRootPath,
                Array.Empty<ManagedStorageMigrationResidue>(),
                0,
                Array.Empty<ManagedStorageSkippedItem>());
        }

        var affectedWidgets = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File && widget.FollowsDefaultStoragePath && !IsDeleted(widget.Id))
            .Select(widget =>
            {
                string managedFolderName = string.IsNullOrWhiteSpace(widget.ManagedFolderName)
                    ? CreateManagedFolderName(widget.Name, widget.Id)
                    : widget.ManagedFolderName;
                string sourceFolder = string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                    ? Path.Combine(oldRootPath, managedFolderName)
                    : widget.MappedFolderPath;
                string destinationFolder = Path.Combine(normalizedNewRootPath, managedFolderName);

                return new
                {
                    Widget = widget,
                    ManagedFolderName = managedFolderName,
                    SourceFolder = sourceFolder,
                    DestinationFolder = destinationFolder
                };
            })
            .ToList();

        foreach (var widgetPlan in affectedWidgets)
        {
            EnsureFileWidgetPathAvailable(
                widgetPlan.DestinationFolder,
                widgetPlan.Widget.Id,
                candidateFollowsDefaultStoragePath: true);
            if (FileService.IsPathUnderDirectoryResolved(widgetPlan.DestinationFolder, widgetPlan.SourceFolder))
            {
                throw new InvalidOperationException(FormatFileWidgetPathConflictMessage(
                    widgetPlan.DestinationFolder,
                    widgetPlan.Widget.Name,
                    widgetPlan.SourceFolder));
            }
        }

        bool newRootPreExisted = Directory.Exists(normalizedNewRootPath);
        Directory.CreateDirectory(normalizedNewRootPath);

        // A non-empty destination folder is almost always the complete copy a
        // previous failed migration kept. Moving into it forks the trees into
        // "(2)" renamed duplicates, so the caller must recycle the stale copy
        // first and retry.
        List<string> staleDestinationFolders = affectedWidgets
            .Where(widgetPlan => Directory.Exists(widgetPlan.DestinationFolder) &&
                                 Directory.EnumerateFileSystemEntries(widgetPlan.DestinationFolder).Any())
            .Select(widgetPlan => widgetPlan.DestinationFolder)
            .ToList();
        if (staleDestinationFolders.Count > 0)
        {
            throw new ManagedStorageDestinationResidueException(staleDestinationFolders);
        }

        var completedMoves = new List<(string WidgetId, string WidgetName, string SourceFolder, string DestinationFolder)>(affectedWidgets.Count);
        var restoreBackFailures = new List<ManagedStorageRollbackFailure>();
        var residueReports = new List<ManagedStorageMigrationResidue>();
        var residueWidgetIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedItems = new List<ManagedStorageSkippedItem>();
        var unmigratedWidgetIds = new HashSet<string>(StringComparer.Ordinal);
        var originalWidgetStorage = affectedWidgets.ToDictionary(
            widget => widget.Widget.Id,
            widget => (widget.Widget.ManagedFolderName, widget.Widget.MappedFolderPath),
            StringComparer.Ordinal);
        CancellationToken cancellationToken = options?.CancellationToken ?? CancellationToken.None;

        int totalItems = options?.Progress is null && options?.OnItemError is null
            ? 0
            : await Task.Run(
                () => affectedWidgets.Sum(widgetPlan =>
                {
                    try
                    {
                        return Directory.Exists(widgetPlan.SourceFolder)
                            ? Directory.EnumerateFileSystemEntries(widgetPlan.SourceFolder).Count()
                            : 0;
                    }
                    catch
                    {
                        return 0;
                    }
                }),
                CancellationToken.None);

        int movedItemCount = 0;
        int completedItemsAcrossWidgets = 0;
        int completedWidgetCount = 0;
        long bytesBeforeCurrentWidget = 0;
        long currentWidgetBytes = 0;

        async Task<FileService.DirectoryMoveReport> RelocateWidgetFolderAsync(
            string widgetName,
            string sourceFolder,
            string destinationFolder)
        {
            int baseItems = completedItemsAcrossWidgets;
            long baseBytes = bytesBeforeCurrentWidget;
            currentWidgetBytes = 0;
            IProgress<FileService.FileTransferProgress>? folderProgress =
                options?.Progress is null
                    ? null
                    : new Progress<FileService.FileTransferProgress>(inner =>
                    {
                        currentWidgetBytes = inner.BytesTransferred;
                        options.Progress.Report(new ManagedStorageMigrationProgress(
                            inner.Phase,
                            completedWidgetCount,
                            affectedWidgets.Count,
                            widgetName,
                            inner.CurrentItemName,
                            Math.Min(totalItems, baseItems + inner.CompletedItems),
                            totalItems,
                            baseBytes + inner.BytesTransferred,
                            inner.BytesPerSecond,
                            inner.EstimatedRemaining));
                    });

            if (RelocateDirectoryForMigrationOverrideEx is { } overrideEx)
            {
                return await overrideEx(
                    sourceFolder,
                    destinationFolder,
                    folderProgress,
                    cancellationToken,
                    options?.OnItemError);
            }

            if (RelocateDirectoryForMigrationOverride is { } legacyOverride)
            {
                // Legacy test seam: no report is produced, so the widget is
                // always treated as fully migrated once the call returns.
                await legacyOverride(sourceFolder, destinationFolder);
                return new FileService.DirectoryMoveReport(-1, []);
            }

            return await _fileService.RelocateDirectoryAsync(
                sourceFolder,
                destinationFolder,
                folderProgress,
                cancellationToken,
                options?.OnItemError);
        }

        SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: true);
        try
        {
            if (affectedWidgets.Count > 0)
            {
                await Task.Delay(100);
            }

            foreach (var widgetPlan in affectedWidgets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool widgetMigrated = true;
                try
                {
                    FileService.DirectoryMoveReport report = await RelocateWidgetFolderAsync(
                        widgetPlan.Widget.Name,
                        widgetPlan.SourceFolder,
                        widgetPlan.DestinationFolder);
                    foreach (var skipped in report.SkippedItems)
                    {
                        skippedItems.Add(new ManagedStorageSkippedItem(
                            widgetPlan.Widget.Id,
                            widgetPlan.Widget.Name,
                            skipped.SourcePath,
                            skipped.DestinationPath,
                            skipped.ErrorKind,
                            skipped.Detail));
                    }

                    if (report.MovedItems > 0)
                    {
                        movedItemCount += report.MovedItems;
                        completedItemsAcrossWidgets += report.MovedItems;
                    }

                    completedItemsAcrossWidgets += report.SkippedItems.Count;
                    if (report.MovedItems == 0 && report.SkippedItems.Count > 0)
                    {
                        // Every entry of this folder was skipped, so the widget
                        // keeps pointing at its untouched source folder instead
                        // of an empty destination.
                        widgetMigrated = false;
                        unmigratedWidgetIds.Add(widgetPlan.Widget.Id);
                    }

                    bytesBeforeCurrentWidget += currentWidgetBytes;
                }
                catch (FileService.FileTransferPartialFailureException partialFailure)
                {
                    // A move that stopped midway already moved some items into
                    // the destination, but this folder never reaches
                    // completedMoves so the outer rollback cannot return them.
                    // Move them back now — otherwise the leftover destination
                    // items trip the stale-destination guard on every retry.
                    // A failed return is recorded as a rollback failure: the
                    // outer catch forwards the receipt to the retry flow.
                    restoreBackFailures.AddRange(await RestorePartiallyMigratedItemsAsync(
                        partialFailure.CompletedResults,
                        widgetPlan.Widget.Id,
                        widgetPlan.Widget.Name));
                    // A destination-cleanup failure carries no completed
                    // results — its stranded files live in the tree the
                    // transfer could not take back. It still needs a receipt
                    // or the partial copy would sit at the new root with no
                    // recovery path attached.
                    if (partialFailure.InnerException is
                        FileService.FileTransferDestinationCleanupException cleanup)
                    {
                        restoreBackFailures.Add(new ManagedStorageRollbackFailure(
                            widgetPlan.Widget.Id,
                            widgetPlan.Widget.Name,
                            cleanup.DestinationDirectory,
                            cleanup.SourceDirectory,
                            PreserveExisting: true,
                            cleanup.Message));
                    }

                    TryDeleteEmptyFolder(widgetPlan.DestinationFolder);
                    throw;
                }
                catch (FileService.FileTransferCanceledException canceled)
                {
                    // Same partial-tree hazard as a hard failure: items the
                    // cancelled folder move already delivered must go back so
                    // a retry does not hit the stale-destination guard.
                    restoreBackFailures.AddRange(await RestorePartiallyMigratedItemsAsync(
                        canceled.CompletedResults,
                        widgetPlan.Widget.Id,
                        widgetPlan.Widget.Name));
                    TryDeleteEmptyFolder(widgetPlan.DestinationFolder);
                    throw;
                }
                catch (Exception ex) when (
                    ex is FileService.FileTransferSourceCleanupException or
                        FileService.FileTransferSourceChangedException)
                {
                    // The destination tree already holds a complete copy (of
                    // an earlier snapshot for the changed variant). Failing
                    // the whole migration here would roll this widget back
                    // onto a partially deleted source folder; keep the copy,
                    // finish the migration, and report the leftover source.
                    App.Log(
                        $"[ManagedStorageMigration] Widget '{widgetPlan.Widget.Id}' " +
                        $"migrated with source residue '{widgetPlan.SourceFolder}': {ex.Message}");
                    residueReports.Add(new ManagedStorageMigrationResidue(
                        widgetPlan.Widget.Id,
                        widgetPlan.Widget.Name,
                        widgetPlan.SourceFolder,
                        ex.Message));
                    residueWidgetIds.Add(widgetPlan.Widget.Id);
                }

                if (widgetMigrated)
                {
                    completedMoves.Add((
                        widgetPlan.Widget.Id,
                        widgetPlan.Widget.Name,
                        widgetPlan.SourceFolder,
                        widgetPlan.DestinationFolder));
                }

                completedWidgetCount++;
                options?.Progress?.Report(new ManagedStorageMigrationProgress(
                    FileService.FileTransferPhase.Transferring,
                    completedWidgetCount,
                    affectedWidgets.Count,
                    widgetPlan.Widget.Name,
                    null,
                    Math.Min(totalItems, completedItemsAcrossWidgets),
                    totalItems,
                    bytesBeforeCurrentWidget,
                    null,
                    null));
            }

            _settingsService.Settings.DefaultManagedStorageRootPath = normalizedNewRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                if (unmigratedWidgetIds.Contains(widgetPlan.Widget.Id))
                {
                    continue;
                }

                widgetPlan.Widget.ManagedFolderName = widgetPlan.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = widgetPlan.DestinationFolder;
            }

            if (!await _settingsService.SaveCheckedAsync())
            {
                // The disk still holds the old root. Throwing here rolls the
                // directories and in-memory settings back while the persisted
                // settings never moved, so all three stay consistent.
                throw new InvalidOperationException(
                    $"Failed to persist the managed storage root change to '{normalizedNewRootPath}'.");
            }

            try
            {
                // Past the commit point: a failure while cleaning up the old
                // root's shortcut entries must not roll the physical migration
                // back. The startup storage sync repairs what it can.
                SyncStorageFolderEntries(oldRootPath);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[ManagedStorageMigration] Old-root shortcut cleanup skipped " +
                    $"for '{oldRootPath}': {ex.Message}");
            }

            try
            {
                // The migration is already committed at this point; a
                // desktop-shortcut sync failure (or a WinUI activation
                // failure in a non-app test host, where Application.Current
                // throws REGDB_E_CLASSNOTREG) must not roll it back.
                if (App.Current?.ManagedStorageDesktopShortcutService is { } shortcutService)
                {
                    await shortcutService.SyncAsync(oldRootPath);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[ManagedStorageMigration] Desktop shortcut sync skipped: {ex.Message}");
            }

            foreach (var widgetPlan in affectedWidgets)
            {
                try
                {
                    await RefreshFileWidgetAsync(widgetPlan.Widget.Id);
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Refresh failed for widget '{widgetPlan.Widget.Id}': {ex}");
                }
            }
        }
        catch (Exception originalFailure)
        {
            App.Log($"[ManagedStorageMigration] Migration failed, rolling back: {originalFailure}");
            _settingsService.Settings.DefaultManagedStorageRootPath = oldRootPath;
            foreach (var widgetPlan in affectedWidgets)
            {
                if (!originalWidgetStorage.TryGetValue(widgetPlan.Widget.Id, out var originalStorage))
                {
                    continue;
                }

                widgetPlan.Widget.ManagedFolderName = originalStorage.ManagedFolderName;
                widgetPlan.Widget.MappedFolderPath = originalStorage.MappedFolderPath;
            }

            // A commit that died between the two stores can leave
            // widget-layout.json carrying the new paths while settings.json
            // kept the old root — the in-memory restore above alone would
            // not heal that. Re-saving the restored state converges both
            // durable files back to the old root; files-old + durable-old is
            // the only consistent failure outcome.
            bool metadataRestored;
            try
            {
                metadataRestored = await _settingsService.SaveCheckedAsync(
                    notifySubscribers: false);
            }
            catch (Exception metadataException)
            {
                App.Log(
                    $"[ManagedStorageMigration] Durable-state rollback save " +
                    $"failed: {metadataException}");
                metadataRestored = false;
            }

            var rollbackFailures = new List<ManagedStorageRollbackFailure>();
            if (!metadataRestored)
            {
                rollbackFailures.Add(new ManagedStorageRollbackFailure(
                    affectedWidgets.FirstOrDefault()?.Widget.Id ?? string.Empty,
                    "managed storage metadata",
                    normalizedNewRootPath,
                    oldRootPath,
                    PreserveExisting: true,
                    "The durable widget mapping could not be restored; " +
                    "widget-layout.json may still reference the new root."));
            }

            foreach (var move in completedMoves.AsEnumerable().Reverse())
            {
                try
                {
                    if (residueWidgetIds.Contains(move.WidgetId))
                    {
                        // The source still holds the files the failed cleanup
                        // could not delete (possibly newer than the copy).
                        // Restore without overwriting them; a plain move-back
                        // would rename every shared child to "(2)".
                        await FileService.RestoreMigratedDirectoryPreservingExistingAsync(
                            move.DestinationFolder,
                            move.SourceFolder);
                    }
                    else
                    {
                        await _fileService.RelocateDirectoryAsync(move.DestinationFolder, move.SourceFolder);
                    }

                    // The preserving restore keeps same-named copies on both
                    // sides instead of overwriting: anything still left at
                    // the destination means the folder is still split and
                    // must stay on the rollback-failure list.
                    if (Directory.Exists(move.DestinationFolder) &&
                        Directory.EnumerateFileSystemEntries(move.DestinationFolder).Any())
                    {
                        rollbackFailures.Add(new ManagedStorageRollbackFailure(
                            move.WidgetId,
                            move.WidgetName,
                            move.DestinationFolder,
                            move.SourceFolder,
                            residueWidgetIds.Contains(move.WidgetId),
                            "Items remain at the destination after the restore."));
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[ManagedStorageMigration] Rollback failed for '{move.DestinationFolder}' -> '{move.SourceFolder}': {ex}");
                    rollbackFailures.Add(new ManagedStorageRollbackFailure(
                        move.WidgetId,
                        move.WidgetName,
                        move.DestinationFolder,
                        move.SourceFolder,
                        residueWidgetIds.Contains(move.WidgetId),
                        ex.Message));
                }
            }

            // A widget that never reached completedMoves can still have
            // items stranded at the destination: the partial-move restore
            // records those as rollback failures too.
            rollbackFailures.AddRange(restoreBackFailures);

            if (rollbackFailures.Count > 0)
            {
                // Files are now split across both roots and the widgets point
                // at the old root. A bare rethrow would hide them behind a
                // generic failure dialog, so hand the list to the UI (#112).
                throw new ManagedStorageRollbackFailureException(originalFailure, rollbackFailures);
            }

            // A failed migration that created the new root should not leave
            // its empty shell behind either.
            if (!newRootPreExisted)
            {
                TryDeleteEmptyFolder(normalizedNewRootPath);
            }

            throw;
        }
        finally
        {
            try
            {
                SetManagedStorageMigrationBusy(affectedWidgets.Select(widget => widget.Widget.Id), isBusy: false);
            }
            catch (Exception ex)
            {
                // Throwing out of the finally would surface an already-committed
                // migration as a failure to its caller.
                App.Log($"[ManagedStorageMigration] Failed to clear the busy state: {ex.Message}");
            }
        }

        return new ManagedStorageMigrationResult(
            completedMoves.Count,
            oldRootPath,
            normalizedNewRootPath,
            residueReports,
            movedItemCount,
            skippedItems);
    }

    /// <summary>
    /// Returns items that a partially completed folder move left at the
    /// destination, grouped by their original parent folder. A group that
    /// cannot move back becomes a rollback-failure receipt: the stranded
    /// files must reach the rollback-retry flow instead of silently
    /// splitting the widget's folder across both roots.
    /// </summary>
    private async Task<IReadOnlyList<ManagedStorageRollbackFailure>> RestorePartiallyMigratedItemsAsync(
        IReadOnlyList<FileService.FileTransferResult> completedResults,
        string widgetId,
        string widgetName)
    {
        var failures = new List<ManagedStorageRollbackFailure>();
        foreach (var group in completedResults
            .Select(result => new
            {
                result.DestinationPath,
                DestinationDirectory = Path.GetDirectoryName(result.DestinationPath),
                SourceDirectory = Path.GetDirectoryName(result.SourcePath)
            })
            .Where(item =>
                item.DestinationDirectory is not null &&
                item.SourceDirectory is not null)
            .GroupBy(item => (
                DestinationDirectory: item.DestinationDirectory!,
                SourceDirectory: item.SourceDirectory!)))
        {
            try
            {
                await _fileService.TransferItemsWithResultAsync(
                    group.Select(item => item.DestinationPath).ToList(),
                    group.Key.SourceDirectory,
                    move: true);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[ManagedStorageMigration] Failed to return partially " +
                    $"moved items to '{group.Key.SourceDirectory}': {ex.Message}");
                // The source folder can still hold items that never moved,
                // so the retry must never overwrite what is already there.
                failures.Add(new ManagedStorageRollbackFailure(
                    widgetId,
                    widgetName,
                    group.Key.DestinationDirectory,
                    group.Key.SourceDirectory,
                    PreserveExisting: true,
                    ex.Message));
            }
        }

        return failures;
    }

    /// <summary>
    /// Removes a folder left empty after a rollback step — best-effort, only
    /// when it exists and holds no entries. Never deletes content.
    /// </summary>
    private static void TryDeleteEmptyFolder(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath) &&
                !Directory.EnumerateFileSystemEntries(folderPath).Any())
            {
                Directory.Delete(folderPath, recursive: false);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ManagedStorageMigration] Empty folder cleanup failed " +
                $"for '{folderPath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Test seam for the per-widget directory relocation during a storage
    /// migration. Production code always uses the shared FileService; tests
    /// use it to inject deterministic copy/cleanup failures.
    /// </summary>
    internal Func<string, string, Task>? RelocateDirectoryForMigrationOverride { get; set; }

    /// <summary>
    /// Extended test seam matching the interactive FileService relocation
    /// overload (progress, cancellation, item-error decisions, move report).
    /// Applies to the first migration and to skipped-item retries alike.
    /// Takes precedence over <see cref="RelocateDirectoryForMigrationOverride"/>.
    /// </summary>
    internal Func<string, string,
        IProgress<FileService.FileTransferProgress>?,
        CancellationToken,
        Func<FileService.FileTransferItemError, Task<FileService.FileTransferItemAction>>?,
        Task<FileService.DirectoryMoveReport>>? RelocateDirectoryForMigrationOverrideEx { get; set; }

    /// <summary>
    /// Re-runs the move only for entries a migration skipped, grouped by their
    /// widget folder so the same per-item decisions apply. When every entry of
    /// a widget's old folder finally moved, a widget that stayed behind is
    /// repointed to the new root. Returns the items that are still unmoved.
    /// </summary>
    public async Task<IReadOnlyList<ManagedStorageSkippedItem>> RetrySkippedMigrationItemsAsync(
        IReadOnlyList<ManagedStorageSkippedItem> items,
        ManagedStorageMigrationOptions? options = null)
    {
        var remaining = new List<ManagedStorageSkippedItem>();
        if (items.Count == 0)
        {
            return remaining;
        }

        CancellationToken cancellationToken =
            options?.CancellationToken ?? CancellationToken.None;
        var groups = items
            .GroupBy(item => (
                SourceFolder: Path.GetDirectoryName(item.SourcePath) ?? string.Empty,
                DestinationFolder: Path.GetDirectoryName(item.DestinationPath) ?? string.Empty,
                item.WidgetId,
                item.WidgetName))
            .ToList();
        int completedItems = 0;

        void ReportItemProgress(string? widgetName, string? itemName)
        {
            options?.Progress?.Report(new ManagedStorageMigrationProgress(
                FileService.FileTransferPhase.Transferring,
                0,
                0,
                widgetName,
                itemName,
                completedItems,
                items.Count,
                0,
                null,
                null));
        }

        SetManagedStorageMigrationBusy(
            groups.Select(group => group.Key.WidgetId), isBusy: true);
        try
        {
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(group.Key.SourceFolder) ||
                    !Directory.Exists(group.Key.SourceFolder))
                {
                    // The old folder is already gone (cleaned up or recycled
                    // meanwhile): the entries can never move now.
                    foreach (var item in group)
                    {
                        remaining.Add(item with
                        {
                            ErrorKind = FileService.FileTransferItemErrorKind.NotFound
                        });
                        completedItems++;
                    }

                    continue;
                }

                bool retryFailed = false;
                try
                {
                    FileService.DirectoryMoveReport report =
                        RelocateDirectoryForMigrationOverrideEx is { } retryOverride
                            ? await retryOverride(
                                group.Key.SourceFolder,
                                group.Key.DestinationFolder,
                                null,
                                cancellationToken,
                                options?.OnItemError)
                            : await _fileService.RelocateDirectoryAsync(
                                group.Key.SourceFolder,
                                group.Key.DestinationFolder,
                                progress: null,
                                cancellationToken,
                                options?.OnItemError);
                    foreach (var skipped in report.SkippedItems)
                    {
                        remaining.Add(new ManagedStorageSkippedItem(
                            group.Key.WidgetId,
                            group.Key.WidgetName,
                            skipped.SourcePath,
                            skipped.DestinationPath,
                            skipped.ErrorKind,
                            skipped.Detail));
                    }

                    completedItems += group.Count() - report.SkippedItems.Count;
                }
                catch (FileService.FileTransferCanceledException canceled)
                {
                    // Same transaction rule as the first migration: undo what
                    // this retry already moved so the group stays fully
                    // skipped and the widget keeps pointing at its untouched
                    // source folder. A stranded undo becomes a rollback
                    // failure — a bare cancel would drop the receipt when
                    // the dialog closes.
                    IReadOnlyList<ManagedStorageRollbackFailure> undoFailures =
                        await RestorePartiallyMigratedItemsAsync(
                            canceled.CompletedResults,
                            group.Key.WidgetId,
                            group.Key.WidgetName);
                    TryDeleteEmptyFolder(group.Key.DestinationFolder);
                    if (undoFailures.Count > 0)
                    {
                        throw new ManagedStorageRollbackFailureException(
                            canceled,
                            undoFailures);
                    }

                    throw;
                }
                catch (FileService.FileTransferPartialFailureException partial)
                {
                    retryFailed = true;
                    var undoFailures = new List<ManagedStorageRollbackFailure>(
                        await RestorePartiallyMigratedItemsAsync(
                            partial.CompletedResults,
                            group.Key.WidgetId,
                            group.Key.WidgetName));
                    // Same stranded-destination case as the first migration:
                    // the cleanup failure carries no completed results, so
                    // without an explicit receipt the residue would sit at
                    // the destination untracked.
                    if (partial.InnerException is
                        FileService.FileTransferDestinationCleanupException cleanup)
                    {
                        undoFailures.Add(new ManagedStorageRollbackFailure(
                            group.Key.WidgetId,
                            group.Key.WidgetName,
                            cleanup.DestinationDirectory,
                            cleanup.SourceDirectory,
                            PreserveExisting: true,
                            cleanup.Message));
                    }

                    TryDeleteEmptyFolder(group.Key.DestinationFolder);
                    if (undoFailures.Count > 0)
                    {
                        throw new ManagedStorageRollbackFailureException(
                            partial,
                            undoFailures);
                    }

                    App.Log(
                        $"[ManagedStorageMigration] Skipped-item retry failed " +
                        $"for '{group.Key.SourceFolder}': {partial.InnerException?.Message ?? partial.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    retryFailed = true;
                    App.Log(
                        $"[ManagedStorageMigration] Skipped-item retry failed " +
                        $"for '{group.Key.SourceFolder}': {ex.Message}");
                }

                if (retryFailed)
                {
                    completedItems += group.Count();
                    foreach (var item in group)
                    {
                        if (!remaining.Any(entry => string.Equals(
                                entry.SourcePath,
                                item.SourcePath,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            remaining.Add(item);
                        }
                    }
                }

                // A widget whose folder was fully skipped kept pointing at the
                // old root. Once nothing is left there, repoint it so the
                // migrated files actually show up in the widget.
                var widget = _settingsService.Settings.Widgets.FirstOrDefault(
                    candidate => candidate.Id == group.Key.WidgetId);
                if (widget is not null &&
                    string.Equals(
                        widget.MappedFolderPath,
                        group.Key.SourceFolder,
                        StringComparison.OrdinalIgnoreCase) &&
                    (!Directory.Exists(group.Key.SourceFolder) ||
                     !Directory.EnumerateFileSystemEntries(group.Key.SourceFolder).Any()))
                {
                    widget.MappedFolderPath = group.Key.DestinationFolder;
                    if (!await _settingsService.SaveCheckedAsync())
                    {
                        // The physical move already landed; a persisted-old
                        // mapping would orphan the items at the destination
                        // (files NEW + durable OLD — the apparent-data-loss
                        // pair). Undo the repoint and move the group back so
                        // the failure leaves old + old on every layer.
                        widget.MappedFolderPath = group.Key.SourceFolder;
                        try
                        {
                            await _fileService.RelocateDirectoryAsync(
                                group.Key.DestinationFolder,
                                group.Key.SourceFolder,
                                progress: null,
                                cancellationToken,
                                onItemError: null);
                        }
                        catch (Exception rollbackException)
                        {
                            throw new ManagedStorageRollbackFailureException(
                                rollbackException,
                                [
                                    new ManagedStorageRollbackFailure(
                                        group.Key.WidgetId,
                                        group.Key.WidgetName,
                                        group.Key.DestinationFolder,
                                        group.Key.SourceFolder,
                                        PreserveExisting: true,
                                        "Persisting the repoint failed and the " +
                                        "moved items could not be returned to " +
                                        "the old folder.")
                                ]);
                        }

                        throw new InvalidOperationException(
                            $"Failed to persist the widget folder repoint " +
                            $"for '{group.Key.WidgetName}'.");
                    }

                    try
                    {
                        await RefreshFileWidgetAsync(widget.Id);
                    }
                    catch (Exception ex)
                    {
                        App.Log(
                            $"[ManagedStorageMigration] Refresh after skipped-item " +
                            $"retry failed for widget '{widget.Id}': {ex.Message}");
                    }
                }

                completedItems = Math.Min(completedItems, items.Count);
                ReportItemProgress(group.Key.WidgetName, null);
            }
        }
        finally
        {
            SetManagedStorageMigrationBusy(
                groups.Select(group => group.Key.WidgetId), isBusy: false);
        }

        return remaining;
    }

    /// <summary>
    /// Moves migration residue folders (old-root leftovers whose destination
    /// copy is complete) to the recycle bin. Only ever called after an
    /// explicit user confirmation.
    /// </summary>
    public async Task<int> DeleteMigrationResidueFoldersAsync(IEnumerable<string> folderPaths)
    {
        int recycledCount = 0;
        foreach (string folderPath in folderPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await _fileService.DeleteEntryAsync(folderPath, recycle: true))
                {
                    recycledCount++;
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[ManagedStorageMigration] Failed to recycle residue " +
                    $"folder '{folderPath}': {ex.Message}");
            }
        }

        return recycledCount;
    }

    /// <summary>
    /// Re-runs the rollback for folders a failed migration could not return to
    /// the old root. Uses the same conservative merge as the original rollback
    /// (existing files are never overwritten or deleted), so a retry after the
    /// user moved things around cannot destroy data. Returns the failures that
    /// still could not be returned; the caller keeps showing those.
    /// </summary>
    public async Task<IReadOnlyList<ManagedStorageRollbackFailure>> RetryMigrationRollbackAsync(
        IReadOnlyList<ManagedStorageRollbackFailure> failures)
    {
        var remaining = new List<ManagedStorageRollbackFailure>();
        if (failures.Count == 0)
        {
            return remaining;
        }

        SetManagedStorageMigrationBusy(failures.Select(failure => failure.WidgetId), isBusy: true);
        try
        {
            foreach (var failure in failures)
            {
                try
                {
                    if (failure.PreserveExisting)
                    {
                        await FileService.RestoreMigratedDirectoryPreservingExistingAsync(
                            failure.DestinationFolder,
                            failure.SourceFolder);
                    }
                    else
                    {
                        await _fileService.RelocateDirectoryAsync(
                            failure.DestinationFolder,
                            failure.SourceFolder);
                    }

                    // A preserving restore deliberately keeps same-named
                    // copies on both sides (and per-child failures are
                    // logged, not thrown): anything still left at the
                    // destination means the folder is still split — the
                    // receipt must survive so the user can resolve it.
                    if (Directory.Exists(failure.DestinationFolder) &&
                        Directory.EnumerateFileSystemEntries(failure.DestinationFolder).Any())
                    {
                        App.Log(
                            $"[ManagedStorageMigration] Rollback retry left " +
                            $"items at '{failure.DestinationFolder}' (conflicting copies kept).");
                        remaining.Add(failure);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[ManagedStorageMigration] Rollback retry failed for " +
                        $"'{failure.DestinationFolder}' -> '{failure.SourceFolder}': {ex.Message}");
                    remaining.Add(failure);
                    continue;
                }

                // The folder is physically back — a UI refresh failure must
                // not requeue it, or the next retry would try to move a
                // destination folder that no longer exists and wedge the
                // folder on the failure list forever.
                try
                {
                    await RefreshFileWidgetAsync(failure.WidgetId);
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[ManagedStorageMigration] Post-rollback refresh failed " +
                        $"for '{failure.WidgetId}': {ex.Message}");
                }
            }
        }
        finally
        {
            try
            {
                SetManagedStorageMigrationBusy(failures.Select(failure => failure.WidgetId), isBusy: false);
            }
            catch (Exception ex)
            {
                App.Log($"[ManagedStorageMigration] Failed to clear the busy state after rollback retry: {ex.Message}");
            }
        }

        return remaining;
    }

    private void SetManagedStorageMigrationBusy(IEnumerable<string> widgetIds, bool isBusy)
    {
        foreach (string widgetId in widgetIds.Distinct(StringComparer.Ordinal))
        {
            if (_fileWidgets.TryGetValue(widgetId, out var entry))
            {
                entry.SetMigrationBusy(isBusy);
                continue;
            }

            ContentWidgetWindow? contentWindow = _contentWidgets.Values
                .Distinct()
                .FirstOrDefault(window =>
                    window.CurrentContent is FileWidgetContentAdapter surface &&
                    string.Equals(surface.WidgetId, widgetId, StringComparison.Ordinal));
            if (contentWindow?.CurrentContent is FileWidgetContentAdapter fileSurface)
            {
                fileSurface.SetMigrationBusy(isBusy);
            }
        }
    }

    private async Task RenameManagedWidgetFolderAsync(WidgetConfig config, string newName)
    {
        if (!config.FollowsDefaultStoragePath)
        {
            return;
        }

        string rootPath = GetManagedStorageRootPath();
        string currentFolderPath = string.IsNullOrWhiteSpace(config.MappedFolderPath)
            ? Path.Combine(rootPath, config.ManagedFolderName ?? string.Empty)
            : Path.GetFullPath(config.MappedFolderPath);
        string currentFolderName = string.IsNullOrWhiteSpace(config.ManagedFolderName)
            ? Path.GetFileName(currentFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : config.ManagedFolderName;

        if (!Directory.Exists(currentFolderPath))
        {
            throw new DirectoryNotFoundException(currentFolderPath);
        }

        string desiredFolderName = FileService.SanitizeFileSystemName(newName);
        if (string.IsNullOrWhiteSpace(desiredFolderName))
        {
            desiredFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        if (string.Equals(currentFolderName, desiredFolderName, StringComparison.OrdinalIgnoreCase))
        {
            config.ManagedFolderName = currentFolderName;
            config.MappedFolderPath = currentFolderPath;
            return;
        }

        string destinationFolderPath = Path.Combine(rootPath, desiredFolderName);
        if (IsManagedWidgetNameInUse(newName, desiredFolderName, config.Id))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameExists"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(destinationFolderPath))
        {
            bool currentFolderIsEmpty = !Directory.EnumerateFileSystemEntries(currentFolderPath).Any();
            if (!currentFolderIsEmpty)
            {
                // Both sides hold files and merging them could shadow either
                // copy, so the rename stays blocked with an accurate message
                // (the folder usually belongs to a closed widget, #113).
                throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameUnavailable"));
            }

            // Never adopt a folder a live widget already claims (a mapped
            // widget's folder can sit under the managed root when the root
            // moved around it): two widgets sharing one directory means the
            // first "close and delete files" wipes the other's contents.
            // The shared resolved-path guard is required here — a junction
            // or symlink alias resolves to the claimed physical directory
            // while comparing lexical paths would wave it through.
            // Whether the destination is genuinely a closed widget's kept
            // folder is unprovable — RemoveWidgetImmediate drops the config
            // and only the id tombstone survives — so live-claim exclusion
            // is the only enforceable guard.
            EnsureFileWidgetPathAvailable(
                destinationFolderPath,
                excludedWidgetId: config.Id,
                candidateFollowsDefaultStoragePath: true);

            // The empty current folder is the default folder a fresh widget
            // got; the existing destination is the managed folder a closed
            // widget kept behind on purpose. Adopt it as the storage
            // location instead of dead-ending the name (feedback #113):
            // nothing inside either folder is moved, merged, or deleted, and
            // the kept contents show up in the widget again.
            Directory.Delete(currentFolderPath);
            App.Log(
                $"[WidgetManager] Managed folder rename adopted the existing " +
                $"folder '{destinationFolderPath}' for widget '{config.Id}'");
        }
        else if (File.Exists(destinationFolderPath))
        {
            // A file holds the name: the widget needs a directory and there
            // is nothing safe to adopt here.
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderNameUnavailable"));
        }

        if (!string.Equals(currentFolderPath, destinationFolderPath, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(destinationFolderPath))
        {
            await Task.Run(() => Directory.Move(currentFolderPath, destinationFolderPath));
        }

        WidgetFileStackSettings.RebaseManagedFolderPaths(
            config,
            currentFolderPath,
            destinationFolderPath);
        config.ManagedFolderName = desiredFolderName;
        config.MappedFolderPath = destinationFolderPath;

        await RefreshFileWidgetAsync(config.Id);
    }

    private void RemoveMappedWidgetShortcut(WidgetConfig config)
    {
        DeleteMappedWidgetShortcut(GetExistingMappedWidgetShortcutPath(config, GetManagedStorageRootPath()), config.Id);
    }

    private void DeleteMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath) ||
            !IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId))
        {
            return;
        }

        try
        {
            File.Delete(shortcutPath);
        }
        catch (Exception ex)
        {
            App.Log($"[MappedShortcut] Failed to delete shortcut '{shortcutPath}': {ex}");
        }
    }

    private void RemoveStaleMappedWidgetShortcuts(string rootPath, ISet<string> activeWidgetIds)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId) || activeWidgetIds.Contains(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private void RemoveAllMappedWidgetShortcuts(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        foreach (string shortcutPath in Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string? widgetId = GetDeskBoxMappedWidgetShortcutId(shortcutPath);
            if (string.IsNullOrWhiteSpace(widgetId))
            {
                continue;
            }

            DeleteMappedWidgetShortcut(shortcutPath, widgetId);
        }
    }

    private string GetExistingMappedWidgetShortcutPath(WidgetConfig config, string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(rootPath, "*.lnk", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => IsDeskBoxMappedWidgetShortcut(path, config.Id)) ?? string.Empty;
    }

    private string BuildAvailableMappedShortcutPath(
        string displayName,
        string widgetId,
        string rootPath,
        string currentShortcutPath)
    {
        string shortcutName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(shortcutName))
        {
            shortcutName = _localizationService.T("Widget.MappedShortcutBaseName");
        }

        string desiredPath = Path.Combine(rootPath, $"{shortcutName}.lnk");
        if (!string.IsNullOrWhiteSpace(currentShortcutPath) &&
            string.Equals(Path.GetFullPath(currentShortcutPath), desiredPath, StringComparison.OrdinalIgnoreCase))
        {
            return currentShortcutPath;
        }

        if (CanUseMappedShortcutPath(desiredPath, widgetId))
        {
            return desiredPath;
        }

        int suffix = 2;
        while (true)
        {
            string candidatePath = Path.Combine(rootPath, $"{shortcutName} ({suffix++}).lnk");
            if (CanUseMappedShortcutPath(candidatePath, widgetId))
            {
                return candidatePath;
            }
        }
    }

    private bool CanUseMappedShortcutPath(string shortcutPath, string widgetId)
    {
        if (!File.Exists(shortcutPath) && !Directory.Exists(shortcutPath))
        {
            return true;
        }

        return File.Exists(shortcutPath) && IsDeskBoxMappedWidgetShortcut(shortcutPath, widgetId);
    }

    private static bool IsDeskBoxMappedWidgetShortcut(string shortcutPath, string widgetId)
    {
        return string.Equals(
            GetDeskBoxMappedWidgetShortcutId(shortcutPath),
            widgetId,
            StringComparison.Ordinal);
    }

    private static string? GetDeskBoxMappedWidgetShortcutId(string shortcutPath)
    {
        var shortcut = ShortcutHelper.ReadStoredMetadata(shortcutPath);
        if (shortcut?.Description.StartsWith(ManagedShortcutDescriptionPrefix, StringComparison.Ordinal) != true)
        {
            return null;
        }

        return shortcut.Description[ManagedShortcutDescriptionPrefix.Length..];
    }

    private static string BuildMappedWidgetShortcutDescription(string widgetId)
    {
        return $"{ManagedShortcutDescriptionPrefix}{widgetId}";
    }

    private async Task ApplyWidgetRemovalActionAsync(WidgetConfig config, WidgetRemovalAction removalAction)
    {
        if (removalAction == WidgetRemovalAction.RemoveWidgetOnly)
        {
            return;
        }

        if (!config.FollowsDefaultStoragePath || !IsDefaultManagedStorageFolder(config.MappedFolderPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderActionOnlyDefault"));
        }

        string folderPath = Path.GetFullPath(config.MappedFolderPath!);
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        if (removalAction == WidgetRemovalAction.MoveManagedFolderContentsToDesktop)
        {
            await MoveManagedFolderContentsToDesktopAsync(folderPath);
            return;
        }

        if (removalAction == WidgetRemovalAction.DeleteManagedFolder)
        {
            await _fileService.DeleteEntryAsync(folderPath, recycle: _recycleManagedFolderDeletes);
        }
    }

    private async Task MoveManagedFolderContentsToDesktopAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        string desktopPath = _desktopPathProvider();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = Directory.EnumerateFileSystemEntries(folderPath)
            .Select(path => new FileService.FileTransferPlan(
                path,
                FileService.GetAvailablePath(Path.Combine(desktopPath, Path.GetFileName(path)), reservedPaths)))
            .ToList();

        if (plans.Count > 0)
        {
            await _fileService.ExecuteTransferPlanAsync(plans, move: true);
        }

        if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
        {
            Directory.Delete(folderPath, recursive: false);
        }
    }

    private string ValidateOrphanManagedStorageFolderPath(string folderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath);
        if (!IsDefaultManagedStorageFolder(normalizedPath))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderCleanupOnlyDefault"));
        }

        if (!Directory.Exists(normalizedPath))
        {
            return normalizedPath;
        }

        var activePaths = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !IsDeleted(widget.Id))
            .SelectMany(widget => GetPossibleManagedStoragePaths(
                widget,
                SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (activePaths.Contains(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            throw new InvalidOperationException(_localizationService.T("Widget.Error.ManagedFolderStillActive"));
        }

        return normalizedPath;
    }

    private bool IsDefaultManagedStorageFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        string rootPath = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(rootPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? parentPath = Path.GetDirectoryName(normalizedPath);
        return parentPath is not null &&
               string.Equals(
                   parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   rootPath,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetPossibleManagedStoragePaths(WidgetConfig widget, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            yield return Path.GetFullPath(widget.MappedFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (!string.IsNullOrWhiteSpace(widget.ManagedFolderName))
        {
            yield return Path.GetFullPath(Path.Combine(rootPath, widget.ManagedFolderName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static int CountDirectoryEntries(string folderPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folderPath).Count();
        }
        catch
        {
            return 0;
        }
    }

    private string BuildManagedFolderPath(string managedFolderName)
    {
        return Path.Combine(
            GetManagedStorageRootPath(),
            managedFolderName);
    }

    private string GetManagedStorageRootPath()
    {
        return SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
    }

    private string CreateManagedFolderName(
        string displayName,
        string? widgetId = null,
        string? reusableFolderPath = null)
    {
        string baseFolderName = FileService.SanitizeFileSystemName(displayName);
        if (string.IsNullOrWhiteSpace(baseFolderName))
        {
            baseFolderName = _localizationService.T("Widget.ManagedFolderBaseName");
        }

        string rootPath = GetManagedStorageRootPath();
        var usedNames = _settingsService.Settings.Widgets
            .Where(widget => widget.WidgetKind == WidgetKind.File &&
                             widget.FollowsDefaultStoragePath &&
                             !string.IsNullOrWhiteSpace(widget.ManagedFolderName) &&
                             !string.Equals(widget.Id, widgetId, StringComparison.Ordinal))
            .Select(widget => widget.ManagedFolderName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? reusablePath = string.IsNullOrWhiteSpace(reusableFolderPath)
            ? null
            : Path.GetFullPath(reusableFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = baseFolderName;
        int suffix = 2;
        while (usedNames.Contains(candidate) ||
               IsUnavailableManagedFolderPath(Path.Combine(rootPath, candidate), reusablePath))
        {
            candidate = $"{baseFolderName} ({suffix++})";
        }

        return candidate;
    }

    private bool IsManagedWidgetNameInUse(string displayName, string managedFolderName, string widgetId)
    {
        return _settingsService.Settings.Widgets.Any(widget =>
            widget.WidgetKind == WidgetKind.File &&
            widget.FollowsDefaultStoragePath &&
            !IsDeleted(widget.Id) &&
            !string.Equals(widget.Id, widgetId, StringComparison.Ordinal) &&
            (string.Equals(widget.Name.Trim(), displayName.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(widget.ManagedFolderName, managedFolderName, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsUnavailableManagedFolderPath(string folderPath, string? reusableFolderPath)
    {
        string normalizedPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (reusableFolderPath is not null &&
            string.Equals(normalizedPath, reusableFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Directory.Exists(normalizedPath) || File.Exists(normalizedPath);
    }

    private bool ShouldMoveManagedItems()
    {
        return string.Equals(
            _settingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionMove,
            StringComparison.Ordinal);
    }

}
