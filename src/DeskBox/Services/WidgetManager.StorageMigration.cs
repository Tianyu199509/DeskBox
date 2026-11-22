using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    private readonly SemaphoreSlim _storageMigrationGate = new(1, 1);
    private volatile bool _isStorageMigrationRunning;
    private ManagedStorageMigrationService? _storageMigration;
    internal ManagedStorageMigrationService StorageMigration =>
        _storageMigration ??= new(_settingsService.ManagedStorageMigrationDirectory);
    internal ManagedStorageMigrationDiagnostic? StorageMigrationDiagnostic => _storageMigration?.Diagnostic;
    internal bool IsStorageMigrationRunning => _isStorageMigrationRunning;

    /// <summary>
    /// Main-process post-commit hook for host-level side effects (Desktop shortcut
    /// sync and Explorer Quick Access pin migration) so they run before empty
    /// origin pruning and do not depend on the Settings window remaining open.
    /// </summary>
    internal Func<string, string, Task>? PostManagedStorageMigrationCommitAsync { get; set; }

    public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
        string newRootPath, ManagedStorageMigrationOptions? options = null)
    {
        options ??= new();
        await _storageMigrationGate.WaitAsync(options.CancellationToken);
        bool organizationGateAcquired = false;
        List<ManagedStorageMigrationFolder> activeFolders = [];
        List<ManagedStorageMigrationFolder> folders = [];
        bool watchersSuspended = false;
        bool committed = false;
        try
        {
            await DesktopOrganizationTransaction.OperationGate.WaitAsync(options.CancellationToken);
            organizationGateAcquired = true;
            _isStorageMigrationRunning = true;

            string oldRoot = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
            if (ManagedStorageMigrationService.Same(oldRoot, newRoot))
                return new(0, oldRoot, newRoot, 0, 0, false, Mode: ManagedStorageMigrationMode.EmptySwitch);

            var widgets = CaptureStorageMigrationWidgets();
            activeFolders = BuildStorageMigrationFolders(oldRoot, newRoot, widgets);
            var orphanFolders = BuildOrphanMigrationFolders(oldRoot, newRoot, activeFolders);
            folders = options.IncludeOrphanFolders && orphanFolders.Count > 0
                ? [.. activeFolders, .. orphanFolders]
                : activeFolders;
            int migratedOrphans = options.IncludeOrphanFolders ? orphanFolders.Count : 0;
            int skippedOrphans = options.IncludeOrphanFolders ? 0 : orphanFolders.Count;

            string[] preExistingDestinationFolders = ManagedStorageOwnershipFileOps.CaptureExistingChildNames(
                newRoot,
                ManagedStorageMigrationService.TemporaryFolderName);

            await SetManagedStorageMigrationBusyAsync(activeFolders.Select(f => f.WidgetId), true);
            using var transfer = _fileService.TransferSessions.Begin(folders.Select(f =>
                new FileTransferRegistration(f.SourceFolder, f.DestinationFolder, SourceIsDirectory: true)), isMove: true);

            bool useSameVolumeMove = await StorageMigration.ShouldUseSameVolumeMoveAsync(newRoot, folders);

            if (useSameVolumeMove)
            {
                App.Log($"[ManagedStorageMigration] Same-volume atomic move requested widgets={activeFolders.Count} orphans={migratedOrphans} source='{oldRoot}' target='{newRoot}'");
                foreach (var folder in activeFolders)
                {
                    await SuspendFileWidgetWatchersAsync(folder.WidgetId);
                }
                watchersSuspended = true;

                var (journal, createdNewRoot) = await StorageMigration.MoveSameVolumeAsync(oldRoot, newRoot, folders, options);
                ManagedStorageOwnershipFileOps.CommitMigrationDestinationOwnership(
                    newRoot,
                    folders.Select(static f => f.ManagedFolderName),
                    preExistingDestinationFolders);

                if (!ValidateWidgetSnapshotUnchanged(oldRoot, widgets, activeFolders))
                {
                    ManagedStorageMigrationService.RollbackSameVolumeMoves(journal, createdNewRoot);
                    throw new IOException("Widget storage settings changed while moving. No migration settings were committed.");
                }

                int movedFiles = journal.FileCount;
                journal.State = "Committing";
                await StorageMigration.SaveAsync(journal);
                options.Progress?.Report(new(ManagedStorageMigrationPhase.Committing, movedFiles, movedFiles,
                    journal.TotalBytes, journal.TotalBytes, null, StepIndex: 1, TotalSteps: 1));

                try
                {
                    await CommitStorageMigrationSettingsAsync(oldRoot, newRoot, widgets, activeFolders);
                }
                catch
                {
                    ManagedStorageMigrationService.RollbackSameVolumeMoves(journal, createdNewRoot);
                    throw;
                }

                committed = true;
                journal.State = "Committed";
                journal.Entries.Clear();
                await TryRebaseCommittedOrganizationHistoryAsync(activeFolders, refreshCopiedDestinationIdentities: false);
                try { await StorageMigration.SaveAsync(journal); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Commit receipt will reconcile on startup: {ex}"); }

                foreach (var folder in activeFolders)
                {
                    try { await RefreshFileWidgetAsync(folder.WidgetId); }
                    catch (Exception ex) { App.Log($"[ManagedStorageMigration] Committed widget refresh failed: {ex}"); }
                }
                watchersSuspended = false;

                try { SyncStorageFolderEntries(oldRoot); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Post-commit shortcut sync deferred: {ex.Message}"); }

                await RunPostCommitHostSyncAsync(oldRoot, newRoot);

                ManagedStorageOwnershipFileOps.TryPruneEmptyAbandonedOriginRoot(
                    oldRoot,
                    folders.Select(static f => f.ManagedFolderName).ToArray(),
                    ManagedStorageMigrationService.TemporaryFolderName);

                bool oldRootExists = Directory.Exists(StorageMigrationNativeMethods.Extended(oldRoot));
                App.Log($"[ManagedStorageMigration] Same-volume committed id={journal.Id} files={movedFiles} bytes={journal.TotalBytes} oldRootExists={oldRootExists}");
                options.Progress?.Report(new(ManagedStorageMigrationPhase.Completed, movedFiles, movedFiles,
                    journal.TotalBytes, journal.TotalBytes, null, StepIndex: 1, TotalSteps: 1));
                return new ManagedStorageMigrationResult(
                    activeFolders.Count,
                    oldRoot,
                    newRoot,
                    movedFiles,
                    journal.TotalBytes,
                    Resumed: false,
                    TemporaryBytes: 0,
                    Mode: ManagedStorageMigrationMode.SameVolumeMove,
                    OriginalsRetained: false,
                    CleanedSourceFileCount: movedFiles,
                    RemainingSourceFileCount: 0,
                    MigratedOrphanFolderCount: migratedOrphans,
                    SkippedOrphanFolderCount: skippedOrphans,
                    OldRootStillExists: oldRootExists);
            }
            else
            {
                App.Log($"[ManagedStorageMigration] Cross-volume transaction requested widgets={activeFolders.Count} orphans={migratedOrphans} retainOriginals={options.RetainOriginals} source='{oldRoot}' target='{newRoot}'");
                var (journal, resumed) = await StorageMigration.CopyAsync(oldRoot, newRoot, folders, options);
                ManagedStorageOwnershipFileOps.CommitMigrationDestinationOwnership(
                    newRoot,
                    folders.Select(static f => f.ManagedFolderName),
                    preExistingDestinationFolders);
                options.CancellationToken.ThrowIfCancellationRequested();

                // A component created/removed/repointed during copying invalidates the
                // whole batch. Never silently commit a stale widget snapshot.
                if (!ValidateWidgetSnapshotUnchanged(oldRoot, widgets, activeFolders))
                    throw new IOException("Widget storage settings changed while copying. No migration settings were committed.");

                int copiedFiles = journal.Entries.Count(e => !e.IsDirectory);
                int totalSteps = options.RetainOriginals ? 2 : 3;
                journal.State = "Committing";
                await StorageMigration.SaveAsync(journal);
                options.Progress?.Report(new(ManagedStorageMigrationPhase.Committing, copiedFiles, copiedFiles,
                    journal.TotalBytes, journal.TotalBytes, null, StepIndex: 2, TotalSteps: totalSteps));

                await CommitStorageMigrationSettingsAsync(oldRoot, newRoot, widgets, activeFolders);

                committed = true;
                journal.State = "Committed";
                if (options.RetainOriginals)
                {
                    journal.Entries.Clear();
                    journal.CleanupPending = false;
                    journal.RemainingSourceFiles = 0;
                }
                else
                {
                    journal.CleanupPending = copiedFiles > 0;
                    journal.RemainingSourceFiles = copiedFiles;
                }

                await TryRebaseCommittedOrganizationHistoryAsync(activeFolders, refreshCopiedDestinationIdentities: true);
                try { await StorageMigration.SaveAsync(journal); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Commit receipt will reconcile on startup: {ex}"); }

                StorageMigrationNativeMethods.TryCleanTaskTemporaryFiles(journal.TemporaryDirectory);

                // Refresh widgets BEFORE source cleanup so FolderWatchers release
                // directory handles on oldRoot and start watching newRoot.
                foreach (var folder in activeFolders)
                {
                    try { await RefreshFileWidgetAsync(folder.WidgetId); }
                    catch (Exception ex) { App.Log($"[ManagedStorageMigration] Committed widget refresh failed: {ex}"); }
                }

                try { SyncStorageFolderEntries(oldRoot); }
                catch (Exception ex) { App.Log($"[ManagedStorageMigration] Post-commit shortcut sync deferred: {ex.Message}"); }

                await RunPostCommitHostSyncAsync(oldRoot, newRoot);

                int cleanedSourceFiles = 0;
                int remainingSourceFiles = 0;
                if (!options.RetainOriginals)
                {
                    var cleanup = await StorageMigration.CleanupVerifiedSourcesAsync(
                        journal,
                        options.Progress,
                        options.CancellationToken);
                    cleanedSourceFiles = cleanup.CleanedFileCount;
                    remainingSourceFiles = cleanup.RemainingFileCount;
                    if (remainingSourceFiles == 0)
                    {
                        ManagedStorageOwnershipFileOps.TryPruneEmptyAbandonedOriginRoot(
                            oldRoot,
                            folders.Select(static f => f.ManagedFolderName).ToArray(),
                            ManagedStorageMigrationService.TemporaryFolderName);
                    }
                }

                bool oldRootExists = Directory.Exists(StorageMigrationNativeMethods.Extended(oldRoot));
                App.Log($"[ManagedStorageMigration] Committed id={journal.Id} files={copiedFiles} bytes={journal.TotalBytes} retainOriginals={options.RetainOriginals} cleaned={cleanedSourceFiles} remaining={remainingSourceFiles}");
                options.Progress?.Report(new(ManagedStorageMigrationPhase.Completed, copiedFiles, copiedFiles,
                    journal.TotalBytes, journal.TotalBytes, null, StepIndex: totalSteps, TotalSteps: totalSteps));
                return new ManagedStorageMigrationResult(
                    activeFolders.Count,
                    oldRoot,
                    newRoot,
                    copiedFiles,
                    journal.TotalBytes,
                    resumed,
                    journal.TemporaryBytes,
                    Mode: ManagedStorageMigrationMode.CrossVolumeCopy,
                    OriginalsRetained: options.RetainOriginals,
                    CleanedSourceFileCount: cleanedSourceFiles,
                    RemainingSourceFileCount: remainingSourceFiles,
                    MigratedOrphanFolderCount: migratedOrphans,
                    SkippedOrphanFolderCount: skippedOrphans,
                    OldRootStillExists: oldRootExists);
            }
        }
        catch (Exception ex)
        {
            if (watchersSuspended && !committed)
            {
                foreach (var folder in activeFolders)
                {
                    try { await ResumeFileWidgetWatchersIfStoppedAsync(folder.WidgetId); }
                    catch { }
                }
            }

            await StorageMigration.RecordStoppedAsync(ex);
            long? temporaryBytes = StorageMigration.Diagnostic?.TemporaryBytes;
            bool hasPartialCopies = StorageMigration.HasUnfinishedCopiesForDestination(
                SettingsService.NormalizeManagedStorageRootPath(newRootPath));
            if (ex is OperationCanceledException)
            {
                App.Log("[ManagedStorageMigration] User stopped the task; original files retained.");
                throw new StorageMigrationStoppedException(ex, temporaryBytes, hasPartialCopies);
            }
            App.Log($"[ManagedStorageMigration] Failed; source files retained: {ex}");
            var failure = ex as StorageMigrationException ??
                new StorageMigrationException(
                    ManagedStorageMigrationPresentation.Classify(ex),
                    ex.Message,
                    ex,
                    ManagedStorageMigrationPresentation.ExtractBlockingPath(ex));
            failure.TemporaryBytes = temporaryBytes;
            throw failure;
        }
        finally
        {
            _isStorageMigrationRunning = false;
            try { await SetManagedStorageMigrationBusyAsync(activeFolders.Select(f => f.WidgetId), false); }
            finally
            {
                if (organizationGateAcquired)
                {
                    DesktopOrganizationTransaction.OperationGate.Release();
                }

                _storageMigrationGate.Release();
            }
        }
    }

    public async Task<ManagedStorageMigrationCleanupResult> RetryManagedStorageSourceCleanupAsync(
        string newRootPath, CancellationToken token = default)
    {
        await _storageMigrationGate.WaitAsync(token);
        try
        {
            string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
            return await StorageMigration.RetrySourceCleanupAsync(newRoot, token);
        }
        finally
        {
            _storageMigrationGate.Release();
        }
    }

    public async Task<ManagedStorageMigrationCleanupResult> CleanupInterruptedMigrationCopiesAsync(
        string newRootPath, CancellationToken token = default)
    {
        await _storageMigrationGate.WaitAsync(token);
        try
        {
            string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
            return await StorageMigration.CleanupInterruptedDestinationCopiesAsync(newRoot, token);
        }
        finally
        {
            _storageMigrationGate.Release();
        }
    }

    internal async Task RecoverManagedStorageCommitAsync()
    {
        try
        {
            bool hasUnresolvedPendingCommit = false;
            foreach (var journal in await StorageMigration.ReadPendingCommitsAsync())
            {
                var activeFolders = journal.Folders.Where(static f => !f.IsOrphan).ToList();
                var widgets = activeFolders.Select(f => _settingsService.Settings.WidgetLayout.Widgets
                    .FirstOrDefault(w => w.Id == f.WidgetId && !IsDeleted(w.Id))).ToList();
                if (widgets.Any(w => w is null))
                {
                    hasUnresolvedPendingCommit = true;
                    continue;
                }

                bool isSameVolume = string.Equals(
                    journal.Mode,
                    nameof(ManagedStorageMigrationMode.SameVolumeMove),
                    StringComparison.Ordinal);

                if (isSameVolume)
                {
                    bool allAtDestination = journal.Folders.All(f =>
                        Directory.Exists(f.DestinationFolder) && !Directory.Exists(f.SourceFolder));
                    bool allAtSource = journal.Folders.All(f =>
                        Directory.Exists(f.SourceFolder) && !Directory.Exists(f.DestinationFolder));

                    if (!allAtDestination && !allAtSource && journal.State == "Moving")
                    {
                        // Crash happened mid-move before Committing: return moved folders to source.
                        ManagedStorageMigrationService.RollbackSameVolumeMoves(journal, createdNewRoot: false);
                        allAtSource = journal.Folders.All(f =>
                            Directory.Exists(f.SourceFolder) && !Directory.Exists(f.DestinationFolder));
                    }

                    if (allAtDestination)
                    {
                        _settingsService.MutateGraphUnderLock(() =>
                        {
                            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = journal.NewRootPath;
                            for (int i = 0; i < widgets.Count; i++)
                            {
                                WidgetPathRebaseHelper.RebaseFolderTree(
                                    widgets[i]!,
                                    activeFolders[i].OriginalMappedFolderPath,
                                    activeFolders[i].DestinationFolder);
                                widgets[i]!.MappedFolderPath = activeFolders[i].DestinationFolder;
                                widgets[i]!.ManagedFolderName = activeFolders[i].ManagedFolderName;
                            }
                        });
                        if (await _settingsService.SaveCheckedAsync(notifySubscribers: false))
                        {
                            await TryRebaseCommittedOrganizationHistoryAsync(activeFolders, refreshCopiedDestinationIdentities: false);
                            journal.State = "Committed";
                            journal.Entries.Clear();
                            await StorageMigration.SaveAsync(journal);
                        }
                        else
                        {
                            hasUnresolvedPendingCommit = true;
                        }
                    }
                    else if (allAtSource)
                    {
                        _settingsService.MutateGraphUnderLock(() =>
                        {
                            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = journal.OldRootPath;
                            for (int i = 0; i < widgets.Count; i++)
                            {
                                WidgetPathRebaseHelper.RebaseFolderTree(
                                    widgets[i]!,
                                    activeFolders[i].DestinationFolder,
                                    activeFolders[i].OriginalMappedFolderPath);
                                widgets[i]!.MappedFolderPath = activeFolders[i].OriginalMappedFolderPath;
                                widgets[i]!.ManagedFolderName = activeFolders[i].OriginalManagedFolderName;
                            }
                        });
                        if (await _settingsService.SaveCheckedAsync(notifySubscribers: false))
                        {
                            journal.State = "Stopped";
                            await StorageMigration.SaveAsync(journal);
                        }
                        else
                        {
                            hasUnresolvedPendingCommit = true;
                        }
                    }
                    else
                    {
                        hasUnresolvedPendingCommit = true;
                    }

                    continue;
                }

                bool allNew = activeFolders.Select((f, i) =>
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.DestinationFolder)).All(v => v);
                bool known = activeFolders.Select((f, i) =>
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.DestinationFolder) ||
                    ManagedStorageMigrationService.Same(widgets[i]!.MappedFolderPath, f.OriginalMappedFolderPath)).All(v => v);
                string root = _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath;
                if (allNew && ManagedStorageMigrationService.Same(root, journal.NewRootPath))
                {
                    await TryRebaseCommittedOrganizationHistoryAsync(activeFolders, refreshCopiedDestinationIdentities: true);
                    journal.State = "Committed";
                    journal.Entries.Clear();
                    await StorageMigration.SaveAsync(journal);
                }
                else if (known && (ManagedStorageMigrationService.Same(root, journal.NewRootPath) ||
                                  ManagedStorageMigrationService.Same(root, journal.OldRootPath)))
                {
                    // Interrupted metadata commit: return every affected mapping
                    // to its untouched original. No file access/deletion is needed,
                    // even when a BitLocker target has not yet been unlocked.
                    _settingsService.MutateGraphUnderLock(() =>
                    {
                        _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = journal.OldRootPath;
                        for (int i = 0; i < widgets.Count; i++)
                        {
                            WidgetPathRebaseHelper.RebaseFolderTree(
                                widgets[i]!,
                                activeFolders[i].DestinationFolder,
                                activeFolders[i].OriginalMappedFolderPath);
                            widgets[i]!.MappedFolderPath = activeFolders[i].OriginalMappedFolderPath;
                            widgets[i]!.ManagedFolderName = activeFolders[i].OriginalManagedFolderName;
                        }
                    });
                    if (await _settingsService.SaveCheckedAsync(notifySubscribers: false))
                    {
                        journal.State = "Verified";
                        await StorageMigration.SaveAsync(journal);
                    }
                    else
                    {
                        hasUnresolvedPendingCommit = true;
                    }
                }
                else
                {
                    hasUnresolvedPendingCommit = true;
                }
            }

            if (!hasUnresolvedPendingCommit)
            {
                string currentRoot = SettingsService.NormalizeManagedStorageRootPath(
                    _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
                if (ManagedStoragePathService.TryInferUnanimousManagedStorageRoot(
                        _settingsService.Settings.WidgetLayout.Widgets,
                        _settingsService.Settings.WidgetLayout.DeletedWidgetIds,
                        currentRoot,
                        AppDistributionService.Current.IsMicrosoftStore,
                        requireExistingDirectory: true,
                        out string inferredRoot))
                {
                    _settingsService.MutateGraphUnderLock(() =>
                    {
                        _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = inferredRoot;
                    });
                    await _settingsService.SaveCheckedAsync(notifySubscribers: false);
                    SyncStorageFolderEntries();
                    App.Log(
                        $"[ManagedStorageMigration] Reconciled drifted default storage root '{currentRoot}' -> '{inferredRoot}' " +
                        "from live managed widgets.");
                }
            }
        }
        catch (Exception ex) { App.Log($"[ManagedStorageMigration] Recovery needs attention; copies retained: {ex}"); }
    }

    private async Task CommitStorageMigrationSettingsAsync(
        string oldRoot,
        string newRoot,
        List<WidgetConfig> widgets,
        List<ManagedStorageMigrationFolder> activeFolders)
    {
        void ApplyNewPaths()
        {
            _settingsService.MutateGraphUnderLock(() =>
            {
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = newRoot;
                foreach (var widget in widgets)
                {
                    var folder = activeFolders.Single(f => f.WidgetId == widget.Id);
                    WidgetPathRebaseHelper.RebaseFolderTree(
                        widget,
                        folder.OriginalMappedFolderPath,
                        folder.DestinationFolder);
                    widget.ManagedFolderName = folder.ManagedFolderName;
                    widget.MappedFolderPath = folder.DestinationFolder;
                }
            });
        }

        void RevertOldPaths()
        {
            _settingsService.MutateGraphUnderLock(() =>
            {
                _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = oldRoot;
                foreach (var widget in widgets)
                {
                    var folder = activeFolders.Single(f => f.WidgetId == widget.Id);
                    WidgetPathRebaseHelper.RebaseFolderTree(
                        widget,
                        folder.DestinationFolder,
                        folder.OriginalMappedFolderPath);
                    widget.ManagedFolderName = folder.OriginalManagedFolderName;
                    widget.MappedFolderPath = folder.OriginalMappedFolderPath;
                }
            });
        }

        try
        {
            await RunOnUiThreadIfAvailableAsync(() =>
            {
                ApplyNewPaths();
                return Task.CompletedTask;
            });

            if (!await _settingsService.SaveCheckedAsync())
                throw new IOException("The verified migration could not persist the new storage settings.");
        }
        catch
        {
            await RunOnUiThreadIfAvailableAsync(() =>
            {
                RevertOldPaths();
                return Task.CompletedTask;
            });
            await _settingsService.SaveCheckedAsync(notifySubscribers: false);
            throw;
        }
    }

    private async Task RunPostCommitHostSyncAsync(string oldRoot, string newRoot)
    {
        if (PostManagedStorageMigrationCommitAsync is not { } hook)
        {
            return;
        }

        try
        {
            await hook(oldRoot, newRoot);
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Post-commit host sync deferred: {ex.Message}");
        }
    }

    private async Task SetManagedStorageMigrationBusyAsync(IEnumerable<string> widgetIds, bool isBusy)
    {
        string[] snapshot = widgetIds.Distinct(StringComparer.Ordinal).ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        await RunOnUiThreadIfAvailableAsync(() =>
        {
            foreach (string widgetId in snapshot)
            {
                FindLoadedFileWidgetAdapter(widgetId)?.SetMigrationBusy(isBusy);
            }

            return Task.CompletedTask;
        });
    }

    private Task RunOnUiThreadIfAvailableAsync(Func<Task> action) =>
        RunOnUiThreadAsync(action);

    private async Task TryRebaseCommittedOrganizationHistoryAsync(
        IReadOnlyList<ManagedStorageMigrationFolder> folders,
        bool refreshCopiedDestinationIdentities)
    {
        try
        {
            bool historyChanged = false;
            foreach (var folder in folders)
            {
                if (_settingsService.OrganizationHistory.RebaseWidgetDirectory(
                        folder.WidgetId,
                        folder.OriginalMappedFolderPath,
                        folder.DestinationFolder,
                        refreshCopiedDestinationIdentities: refreshCopiedDestinationIdentities))
                {
                    historyChanged = true;
                }
            }

            if (historyChanged)
            {
                await _settingsService.OrganizationHistory.SaveCheckedAsync();
            }

            if (_organizerService?.DesktopIconPositions is not null)
            {
                foreach (var folder in folders)
                {
                    await _organizerService.DesktopIconPositions.RelocateDirectoryPrefixAsync(
                        folder.OriginalMappedFolderPath,
                        folder.DestinationFolder);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Organization history rebase deferred: {ex.Message}");
        }
    }

    internal Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
        string newRootPath, CancellationToken token)
    {
        string oldRoot = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
        var activeFolders = BuildStorageMigrationFolders(oldRoot, newRoot, CaptureStorageMigrationWidgets());
        var orphanFolders = BuildOrphanMigrationFolders(oldRoot, newRoot, activeFolders);
        return StorageMigration.PreviewAsync(newRoot, activeFolders, orphanFolders, token);
    }

    private List<WidgetConfig> CaptureStorageMigrationWidgets()
    {
        List<WidgetConfig> snapshot = [];
        _settingsService.MutateGraphUnderLock(() =>
        {
            snapshot = _settingsService.Settings.WidgetLayout.Widgets
                .Where(w => w.WidgetKind == WidgetKind.File && w.FollowsDefaultStoragePath && !IsDeleted(w.Id))
                .ToList();
        });
        return snapshot;
    }

    private bool ValidateWidgetSnapshotUnchanged(
        string oldRoot,
        IReadOnlyList<WidgetConfig> widgets,
        IReadOnlyList<ManagedStorageMigrationFolder> activeFolders)
    {
        bool unchanged = false;
        _settingsService.MutateGraphUnderLock(() =>
        {
            unchanged =
                ManagedStorageMigrationService.Same(oldRoot, _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath) &&
                _settingsService.Settings.WidgetLayout.Widgets
                    .Where(w => w.WidgetKind == WidgetKind.File && w.FollowsDefaultStoragePath && !IsDeleted(w.Id))
                    .Select(w => w.Id)
                    .ToHashSet()
                    .SetEquals(widgets.Select(w => w.Id)) &&
                !widgets.Any(w => activeFolders.All(f => f.WidgetId != w.Id ||
                    f.OriginalMappedFolderPath != w.MappedFolderPath ||
                    f.OriginalManagedFolderName != w.ManagedFolderName));
        });
        return unchanged;
    }

    private List<ManagedStorageMigrationFolder> BuildOrphanMigrationFolders(
        string oldRoot,
        string newRoot,
        IReadOnlyList<ManagedStorageMigrationFolder> activeFolders)
    {
        var result = new List<ManagedStorageMigrationFolder>();
        IReadOnlyList<ManagedStorageFolderCleanupCandidate> orphans;
        try
        {
            orphans = GetOrphanManagedStorageFolders();
        }
        catch
        {
            return result;
        }

        var usedNames = activeFolders
            .Select(static f => f.ManagedFolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in orphans)
        {
            string name = candidate.Name;
            if (string.IsNullOrWhiteSpace(name) ||
                name is "." or ".." ||
                Path.GetFileName(name) != name ||
                name.Equals(ManagedStorageMigrationService.TemporaryFolderName, StringComparison.OrdinalIgnoreCase) ||
                !usedNames.Add(name))
            {
                continue;
            }

            string source = Path.GetFullPath(candidate.Path);
            string destination = Path.Combine(newRoot, name);
            if (ManagedStoragePathService.IsSameOrDescendant(destination, source) ||
                ManagedStoragePathService.IsSameOrDescendant(source, destination))
            {
                continue;
            }

            result.Add(new ManagedStorageMigrationFolder(
                WidgetId: "orphan:" + name,
                WidgetName: name,
                ManagedFolderName: name,
                SourceFolder: source,
                DestinationFolder: destination,
                OriginalManagedFolderName: name,
                OriginalMappedFolderPath: source,
                IsOrphan: true));
        }

        return result;
    }

    private List<ManagedStorageMigrationFolder> BuildStorageMigrationFolders(
        string oldRoot, string newRoot, IReadOnlyList<WidgetConfig> widgets)
    {
        ManagedStorageRootValidationResult validation =
            ManagedStoragePathService.ValidateStorageRootCandidate(
                newRoot,
                AppDistributionService.Current.IsMicrosoftStore);
        if (!validation.IsValid)
        {
            StorageMigrationProblem problem = validation.Problem == ManagedStorageRootValidationProblem.DesktopOverlap
                ? StorageMigrationProblem.RootsOverlap
                : StorageMigrationProblem.Unsupported;
            throw new StorageMigrationException(
                problem,
                $"The selected storage root '{newRoot}' is not supported ({validation.Problem}).",
                blockingPath: newRoot);
        }

        if (ManagedStoragePathService.IsSameOrDescendant(newRoot, oldRoot) ||
            ManagedStoragePathService.IsSameOrDescendant(oldRoot, newRoot))
            throw new StorageMigrationException(StorageMigrationProblem.RootsOverlap,
                "The old and new storage roots must not contain one another.",
                blockingPath: newRoot);

        var folders = new List<ManagedStorageMigrationFolder>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var widget in widgets)
        {
            string name = string.IsNullOrWhiteSpace(widget.ManagedFolderName)
                ? CreateManagedFolderName(widget.Name, widget.Id) : widget.ManagedFolderName;
            if (name is "." or ".." || Path.GetFileName(name) != name ||
                name.Equals(ManagedStorageMigrationService.TemporaryFolderName, StringComparison.OrdinalIgnoreCase) ||
                !names.Add(name))
                throw new IOException("Managed folder names must be unique, direct children of the selected root.");
            string source = Path.GetFullPath(string.IsNullOrWhiteSpace(widget.MappedFolderPath)
                ? Path.Combine(oldRoot, name) : widget.MappedFolderPath);
            string destination = Path.Combine(newRoot, name);
            try { EnsureFileWidgetPathAvailable(destination, widget.Id, candidateFollowsDefaultStoragePath: true); }
            catch (InvalidOperationException ex)
            {
                throw new StorageMigrationException(StorageMigrationProblem.DestinationConflict, ex.Message, ex, blockingPath: destination);
            }
            folders.Add(new(widget.Id, widget.Name, name, source, destination,
                widget.ManagedFolderName, widget.MappedFolderPath));
        }
        foreach (var source in folders)
        foreach (var target in folders)
        {
            if (ManagedStoragePathService.IsSameOrDescendant(target.DestinationFolder, source.SourceFolder) ||
                ManagedStoragePathService.IsSameOrDescendant(source.SourceFolder, target.DestinationFolder))
                throw new StorageMigrationException(StorageMigrationProblem.RootsOverlap,
                    "Source and destination widget folders must not overlap.",
                    blockingPath: target.DestinationFolder);
        }

        return folders;
    }

    /// <summary>
    /// Fast path for changing the default storage root when all managed widgets
    /// are empty and no orphan kept folders exist. Returns <c>null</c> when any
    /// managed widget holds files, orphan folders exist, or unfinished resume
    /// state requires the interactive migration dialog.
    /// </summary>
    internal async Task<ManagedStorageMigrationResult?> TrySwitchEmptyManagedStorageRootAsync(
        string newRootPath, CancellationToken token = default)
    {
        string oldRoot = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        string newRoot = SettingsService.NormalizeManagedStorageRootPath(newRootPath);
        if (ManagedStorageMigrationService.Same(oldRoot, newRoot))
        {
            return new ManagedStorageMigrationResult(0, oldRoot, newRoot, 0, 0, false,
                Mode: ManagedStorageMigrationMode.EmptySwitch);
        }

        var widgets = CaptureStorageMigrationWidgets();
        var plannedFolders = BuildStorageMigrationFolders(oldRoot, newRoot, widgets);
        foreach (var folder in plannedFolders)
        {
            if (Directory.Exists(folder.SourceFolder) &&
                Directory.EnumerateFileSystemEntries(StorageMigrationNativeMethods.Extended(folder.SourceFolder)).Any())
            {
                return null;
            }
        }

        if (GetOrphanManagedStorageFolders().Count > 0)
        {
            return null;
        }

        // Clear empty DeskBox-owned destination folders before preview so switching
        // back and forth between empty roots succeeds seamlessly.
        foreach (var folder in plannedFolders)
        {
            ManagedStorageOwnershipFileOps.TryClearEmptyOwnedDestinationFolder(
                newRoot,
                folder.ManagedFolderName,
                folder.DestinationFolder);
        }

        ManagedStorageMigrationPreview preview =
            await PreviewManagedStorageMigrationAsync(newRoot, token);
        if (preview.FileCount > 0 ||
            preview.CopiesRemoved ||
            preview.HasPendingResumeCopies ||
            preview.OrphanFolderCount > 0)
        {
            return null;
        }

        ManagedStorageMigrationResult result = await UpdateDefaultManagedStorageRootAsync(
            newRoot,
            new ManagedStorageMigrationOptions(CancellationToken: token));

        ManagedStorageOwnershipFileOps.TryPruneEmptyAbandonedOriginRoot(
            oldRoot,
            plannedFolders.Select(static f => f.ManagedFolderName).ToArray(),
            ManagedStorageMigrationService.TemporaryFolderName);

        return result with
        {
            Mode = ManagedStorageMigrationMode.EmptySwitch,
            OldRootStillExists = Directory.Exists(StorageMigrationNativeMethods.Extended(oldRoot))
        };
    }
}
