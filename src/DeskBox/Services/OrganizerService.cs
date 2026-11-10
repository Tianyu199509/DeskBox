using DeskBox.Models;

namespace DeskBox.Services;

public sealed class OrganizerService
{
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;

    // Read-only view of the default-path desktop organization journal used
    // to protect a pending transaction's receipts from retention.
    private readonly DesktopOrganizationRecoveryStore _sharedRecoveryStore;
    private readonly Func<string> _desktopPathProvider;
    private readonly DesktopAutoOrganizationSuppressionRegistry _autoOrganizationSuppressions;

    /// <summary>
    /// The widget's mapped folder (or the destination folder inside it) is
    /// missing or on an offline drive. Inherits DirectoryNotFoundException so
    /// background consumers — the auto-organization watcher's storage
    /// recovery catch — classify it as recoverable unavailability instead of
    /// spending finite retry budget. The English message is a log diagnostic;
    /// user-facing copy comes from Widget.Import.MappedFolderUnavailable.
    /// </summary>
    public sealed class MappedFolderUnavailableException : DirectoryNotFoundException
    {
        internal MappedFolderUnavailableException(string path)
            : base($"The mapped folder is unavailable: '{path}'.")
            => Path = path;

        public string Path { get; }
    }

    /// <summary>
    /// The drop destination resolved outside the widget's mapped folder.
    /// Inherits InvalidOperationException — the request is invalid for the
    /// widget's current state, the storage itself is not broken — which also
    /// keeps existing callers that catch InvalidOperationException working.
    /// The English message is a log diagnostic; user-facing copy comes from
    /// Widget.Import.DestinationOutsideMappedRoot.
    /// </summary>
    public sealed class DestinationOutsideMappedRootException : InvalidOperationException
    {
        internal DestinationOutsideMappedRootException(string destinationPath, string mappedRootPath)
            : base("The requested destination is outside the widget's mapped folder.")
            => (DestinationPath, MappedRootPath) = (destinationPath, mappedRootPath);

        public string DestinationPath { get; }

        public string MappedRootPath { get; }
    }

    private sealed record DropPreparation(
        string RootPath,
        IReadOnlyList<string> SourcePaths);

    public OrganizerService(
        SettingsService settingsService,
        FileService fileService,
        Func<string>? desktopPathProvider = null)
        : this(
            settingsService,
            fileService,
            desktopPathProvider,
            new DesktopAutoOrganizationSuppressionRegistry(
                ledgerPath: Path.Combine(
                    DeskBoxDataPathService.Current.DataDirectory,
                    "desktop-organization-suppressions.json")))
    {
    }

    internal OrganizerService(
        SettingsService settingsService,
        FileService fileService,
        Func<string>? desktopPathProvider,
        DesktopAutoOrganizationSuppressionRegistry autoOrganizationSuppressions,
        string? recoveryJournalPath = null)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _desktopPathProvider = desktopPathProvider ?? GetDefaultDesktopPath;
        _autoOrganizationSuppressions = autoOrganizationSuppressions;
        // Read-only view of the desktop organization journal used to protect
        // a pending transaction's receipts from retention. The path is
        // injectable so tests never touch the real data directory.
        _sharedRecoveryStore = new DesktopOrganizationRecoveryStore(recoveryJournalPath);
    }

    internal DesktopAutoOrganizationSuppressionRegistry AutoOrganizationSuppressions =>
        _autoOrganizationSuppressions;

    public OrganizationHistoryEntry? GetLatestUndoableEntry()
    {
        return _settingsService.OrganizationHistory.Entries
            .Where(entry => entry.CanUndo && !entry.IsUndone && !entry.IsFailed && entry.Items.Count > 0)
            .OrderByDescending(entry => entry.TimestampUtc)
            .FirstOrDefault();
    }

    public async Task<OrganizerOperationResult> OrganizeDropAsync(
        WidgetConfig widget,
        string widgetName,
        IEnumerable<string> sourcePaths,
        bool move,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default,
        IProgress<FileService.FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? destinationFolderPath = null)
    {
        if (string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            throw new InvalidOperationException("This widget does not have a managed folder path.");
        }

        string[] sourcePathSnapshot = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        DropPreparation preparation = await Task.Run(
            () => PrepareDrop(
                widget.MappedFolderPath,
                destinationFolderPath,
                sourcePathSnapshot),
            cancellationToken);
        string rootPath = preparation.RootPath;
        IReadOnlyList<string> normalizedSourcePaths = preparation.SourcePaths;

        if (normalizedSourcePaths.Count == 0)
        {
            throw new InvalidOperationException("No items were available to organize.");
        }

        Dictionary<string, HashSet<string>?> preExistingMergeSnapshots =
            new(StringComparer.OrdinalIgnoreCase);
        try
        {
            IReadOnlyList<FileService.FileTransferPlan> plans = await Task.Run(
                () => CreateTransferPlans(rootPath, normalizedSourcePaths),
                cancellationToken);

            if (plans.Count == 0)
            {
                return new OrganizerOperationResult
                {
                    History = CreateHistoryEntry(
                        widget.Id,
                        widgetName,
                        OrganizationActionType.ManagedDrop,
                        move,
                        [],
                        canUndo: false)
                };
            }

            if (move)
            {
                preExistingMergeSnapshots = await Task.Run(
                    () => SnapshotPreExistingMergeDestinations(plans),
                    cancellationToken);
            }

            var results = await _fileService.ExecuteTransferPlanAsync(
                plans,
                move,
                useShellProgress,
                ownerWindowHandle,
                progress,
                cancellationToken);
            if (move && results.Count > 0)
            {
                await RebaseMovedShortcutsOffThreadAsync(results);
            }

            (IReadOnlyList<FileService.FileTransferResult> undoableResults, bool allMergesUndoable) = move
                ? await Task.Run(
                    () => ExpandResultsForSafeFolderMergeUndo(results, preExistingMergeSnapshots),
                    cancellationToken)
                : (results, false);

            // Capture undo receipts off the UI thread: each is a native
            // open+stat pair, and a 2000-item drop would otherwise freeze
            // the caller for seconds after the transfer already finished.
            // Copy imports can never be undone, so they skip the cost.
            bool canSafelyUndo = move && allMergesUndoable && undoableResults.Count > 0;
            var receipts = canSafelyUndo
                ? await CaptureUndoReceiptsAsync(undoableResults)
                : UndoReceiptBatch.Empty;
            var historyEntry = CreateHistoryEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.ManagedDrop,
                move,
                (canSafelyUndo ? undoableResults : results).Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath,
                    TargetWidgetId = widget.Id,
                    TargetWidgetName = widgetName,
                    // Durable undo receipt: undo verifies the object at the
                    // destination against the identity recorded here, never a
                    // fresh capture of whatever later occupies the path.
                    DestinationIdentity = canSafelyUndo ? receipts.GetValue(result.DestinationPath) : null
                }).ToList(),
                canUndo: canSafelyUndo);

            // Snapshot before the retention policy may compact the same
            // entry: callers render per-run results from this list.
            var completedItems = ReferenceEquals(undoableResults, results)
                ? historyEntry.Items.ToList()
                : results.Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath,
                    TargetWidgetId = widget.Id,
                    TargetWidgetName = widgetName
                }).ToList();
            await AddHistoryEntryAsync(historyEntry);
            return new OrganizerOperationResult
            {
                History = historyEntry,
                CompletedItems = completedItems
            };
        }
        catch (Exception ex) when (
            ex is FileService.IFileTransferWithCompletedResults partial)
        {
            IReadOnlyList<FileService.FileTransferResult> completedResults =
                partial.CompletedResults;
            if (completedResults.Count > 0)
            {
                bool allCompletedSourcesRemoved = move && completedResults.All(
                    result =>
                        !File.Exists(result.SourcePath) &&
                        !Directory.Exists(result.SourcePath));
                if (move)
                {
                    await RebaseMovedShortcutsOffThreadAsync(completedResults);
                }

                (IReadOnlyList<FileService.FileTransferResult> undoablePartialResults, bool allPartialMergesUndoable) =
                    allCompletedSourcesRemoved
                        ? await Task.Run(
                            () => ExpandResultsForSafeFolderMergeUndo(completedResults, preExistingMergeSnapshots),
                            CancellationToken.None)
                        : (completedResults, false);
                bool canUndoCompletedMove =
                    allCompletedSourcesRemoved &&
                    allPartialMergesUndoable &&
                    undoablePartialResults.Count > 0;

                var partialReceipts = canUndoCompletedMove
                    ? await CaptureUndoReceiptsAsync(undoablePartialResults)
                    : UndoReceiptBatch.Empty;
                await AddHistoryEntryAsync(CreateHistoryEntry(
                    widget.Id,
                    widgetName,
                    OrganizationActionType.ManagedDrop,
                    move,
                    (canUndoCompletedMove ? undoablePartialResults : completedResults).Select(result =>
                        new OrganizationHistoryItem
                        {
                            Name = Path.GetFileName(result.DestinationPath),
                            SourcePath = result.SourcePath,
                            DestinationPath = result.DestinationPath,
                            TargetWidgetId = widget.Id,
                            TargetWidgetName = widgetName,
                            DestinationIdentity = canUndoCompletedMove
                                ? partialReceipts.GetValue(result.DestinationPath)
                                : null
                        }).ToList(),
                    canUndo: canUndoCompletedMove));
            }

            App.Log(
                $"[Organizer] Import ended with partial results " +
                $"widget={widget.Id} completed={completedResults.Count} " +
                $"requested={normalizedSourcePaths.Count} move={move}: {ex}");
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await AddHistoryEntryAsync(CreateFailureEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.ManagedDrop,
                move,
                normalizedSourcePaths,
                ex.Message));
            throw;
        }
    }

    private static DropPreparation PrepareDrop(
        string mappedFolderPath,
        string? destinationFolderPath,
        IReadOnlyList<string> sourcePaths)
    {
        string mappedRootPath = Path.GetFullPath(mappedFolderPath);
        string rootPath = string.IsNullOrWhiteSpace(destinationFolderPath)
            ? mappedRootPath
            : Path.GetFullPath(destinationFolderPath);
        if (!Directory.Exists(rootPath))
        {
            throw new MappedFolderUnavailableException(rootPath);
        }

        if (!FileService.TryIsPathUnderDirectoryResolved(
                rootPath,
                mappedRootPath,
                out bool isUnderMappedRoot) ||
            !isUnderMappedRoot)
        {
            throw new DestinationOutsideMappedRootException(rootPath, mappedRootPath);
        }

        string[] normalizedSourcePaths = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        return new DropPreparation(rootPath, normalizedSourcePaths);
    }

    internal static IReadOnlyList<FileService.FileTransferPlan> CreateTransferPlans(
        string rootPath,
        IReadOnlyList<string> sourcePaths)
    {
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return sourcePaths
            .Where(path => !FileService.IsEntryDirectlyInDirectoryResolved(
                path,
                rootPath))
            .Select(path =>
            {
                string destinationPath = FileService.ResolveTransferDestinationPath(
                    path,
                    rootPath,
                    reservedPaths,
                    reservedFilePaths);
                return new FileService.FileTransferPlan(path, destinationPath);
            })
            .ToArray();
    }

    public async Task<OrganizerOperationResult> MoveItemBackToDesktopAsync(
        WidgetConfig widget,
        string widgetName,
        WidgetItem item,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        return await MoveItemsBackToDesktopAsync(
            widget,
            widgetName,
            [item.Path],
            useShellProgress,
            ownerWindowHandle);
    }

    public async Task<OrganizerOperationResult> MoveItemsBackToDesktopAsync(
        WidgetConfig widget,
        string widgetName,
        IEnumerable<string> sourcePaths,
        bool useShellProgress = false,
        IntPtr ownerWindowHandle = default)
    {
        if (string.IsNullOrWhiteSpace(widget.MappedFolderPath))
        {
            throw new InvalidOperationException("This widget does not have a folder path.");
        }

        var normalizedSourcePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToList();
        if (normalizedSourcePaths.Count == 0)
        {
            throw new FileNotFoundException("No items to restore could be found.");
        }

        string desktopPath = _desktopPathProvider();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = normalizedSourcePaths
            .Select(sourcePath => new FileService.FileTransferPlan(
                sourcePath,
                FileService.GetAvailablePath(Path.Combine(desktopPath, Path.GetFileName(sourcePath)), reservedPaths)))
            .ToList();
        string operationId = Guid.NewGuid().ToString("N");
        _autoOrganizationSuppressions.BeginOperation(operationId, plans);

        try
        {
            var results = await _fileService.ExecuteTransferPlanAsync(
                plans,
                move: true,
                useShellProgress,
                ownerWindowHandle);
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                results.Select(result => result.DestinationPath));

            if (results.Count > 0)
            {
                await RebaseMovedShortcutsOffThreadAsync(results);
            }

            var receipts = await CaptureUndoReceiptsAsync(results);
            var historyEntry = CreateHistoryEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.MoveBackToDesktop,
                move: true,
                results.Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath,
                    TargetWidgetId = widget.Id,
                    TargetWidgetName = widgetName,
                    DestinationIdentity = receipts.GetValue(result.DestinationPath)
                }).ToList(),
                canUndo: true);

            // Snapshot before the retention policy may compact the same
            // entry: callers render per-run results from this list.
            var completedItems = historyEntry.Items.ToList();
            await AddHistoryEntryAsync(historyEntry);
            return new OrganizerOperationResult
            {
                History = historyEntry,
                CompletedItems = completedItems
            };
        }
        catch (Exception ex)
        {
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                plans.Select(plan => plan.DestinationPath));
            // Explorer semantics: items that physically completed before the
            // failure ride the exception — record them as an undoable entry
            // instead of folding them into the failure record.
            IReadOnlyList<FileService.FileTransferResult> completed =
                ex is FileService.IFileTransferWithCompletedResults partial
                    ? partial.CompletedResults
                    : [];
            if (completed.Count > 0)
            {
                await RebaseMovedShortcutsOffThreadAsync(completed);
                var partialReceipts = await CaptureUndoReceiptsAsync(completed);
                await AddHistoryEntryAsync(CreateHistoryEntry(
                    widget.Id,
                    widgetName,
                    OrganizationActionType.MoveBackToDesktop,
                    move: true,
                    completed.Select(result => new OrganizationHistoryItem
                    {
                        Name = Path.GetFileName(result.DestinationPath),
                        SourcePath = result.SourcePath,
                        DestinationPath = result.DestinationPath,
                        TargetWidgetId = widget.Id,
                        TargetWidgetName = widgetName,
                        DestinationIdentity = partialReceipts.GetValue(result.DestinationPath)
                    }).ToList(),
                    canUndo: true));
            }

            // A user-cancelled Shell transfer is not a failure: whatever
            // did not move was declined in the Shell's own dialog, not
            // lost. Record only the completed set; the retired legacy
            // bridge recorded nothing for cancelled remainder either.
            bool userCancelledShellTransfer =
                ex is FileService.FileTransferCanceledException;
            string[] failedPaths = userCancelledShellTransfer
                ? []
                : normalizedSourcePaths
                    .Except(
                        completed.Select(result => result.SourcePath),
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            if (failedPaths.Length > 0)
            {
                await AddHistoryEntryAsync(CreateFailureEntry(
                    widget.Id,
                    widgetName,
                    OrganizationActionType.MoveBackToDesktop,
                    move: true,
                    failedPaths,
                    ex.Message));
            }

            throw;
        }
    }

    private static Task RebaseMovedShortcutsOffThreadAsync(
        IReadOnlyList<FileService.FileTransferResult> results)
    {
        if (results.Count == 0)
        {
            return Task.CompletedTask;
        }

        (string SourcePath, string DestinationPath)[] movedPairs = results
            .Select(result => (result.SourcePath, result.DestinationPath))
            .ToArray();
        return Task.Run(
            () => _ = Helpers.ShortcutHelper.RebaseMovedShortcuts(movedPairs),
            CancellationToken.None);
    }

    private static string GetDefaultDesktopPath()
    {
#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
        if (AotShellMoveFixture.TryGetOwnedDesktopPath(out string ownedDesktopPath))
        {
            return ownedDesktopPath;
        }
#endif
        return Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
    }

    public async Task<bool> UndoLatestAsync()
    {
        var latestEntry = GetLatestUndoableEntry();
        if (latestEntry is null)
        {
            return false;
        }

        await UndoAsync(latestEntry.Id);
        return true;
    }

    public async Task UndoAsync(string historyEntryId, IntPtr ownerWindowHandle = default)
    {
        var historyEntry = _settingsService.OrganizationHistory.Entries
            .FirstOrDefault(entry => string.Equals(entry.Id, historyEntryId, StringComparison.Ordinal));

        if (historyEntry is null || !historyEntry.CanUndo || historyEntry.IsUndone || historyEntry.IsFailed)
        {
            throw new InvalidOperationException("The selected history entry cannot be undone.");
        }

        if (historyEntry.ActionType == OrganizationActionType.DesktopOrganization)
        {
            var transaction = new DesktopOrganizationTransaction(_settingsService, _fileService)
            {
                AutoOrganizationSuppressions = _autoOrganizationSuppressions
            };
            await transaction.UndoAsync(historyEntryId, ownerWindowHandle);
            return;
        }

        // Only not-yet-restored items participate: a retry after a partial
        // failure must never move an already-restored item again (its undo
        // target is gone, and re-running it against whatever reappeared at
        // that path would relocate an unrelated object).
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(OrganizationHistoryItem Item, FileService.FileTransferPlan Plan)>(
            historyEntry.Items.Count);
        foreach (var item in historyEntry.Items.Where(item => !item.IsRestored))
        {
            if (!File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath))
            {
                throw new InvalidOperationException($"Could not find undo target: {item.Name}");
            }

            // Undo authority comes from the receipt recorded at move time.
            // A replacement that later occupies the destination path fails
            // this check; legacy entries without a receipt have no automatic
            // undo at all rather than a freshly captured identity.
            if (!FileService.UndoReceiptStillMatches(
                    item.DestinationPath,
                    item.DestinationIdentity))
            {
                throw new InvalidOperationException(
                    $"The undo target changed on disk and can no longer be " +
                    $"undone safely: {item.Name}");
            }

            string? sourceParentDir = Path.GetDirectoryName(item.SourcePath);
            if (!string.IsNullOrWhiteSpace(sourceParentDir))
            {
                Directory.CreateDirectory(sourceParentDir);
            }

            string restorePath = FileService.GetAvailablePath(item.SourcePath, reservedPaths);
            pending.Add((item, new FileService.FileTransferPlan(item.DestinationPath, restorePath)));
        }

        var plans = pending.Select(pair => pair.Plan).ToList();
        string operationId = Guid.NewGuid().ToString("N");
        _autoOrganizationSuppressions.BeginOperation(operationId, plans);
        try
        {
            IReadOnlyList<FileService.FileTransferResult> results =
                await _fileService.ExecuteTransferPlanAsync(plans, move: true);
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                results.Select(result => result.DestinationPath));
            if (results.Count > 0)
            {
                await RebaseMovedShortcutsOffThreadAsync(results);
            }
        }
        catch (Exception ex)
        {
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                plans.Select(plan => plan.DestinationPath));
            // Explorer semantics: record the items that physically completed
            // before the failure so a retry only targets the rest.
            if (ex is FileService.IFileTransferWithCompletedResults partial)
            {
                if (partial.CompletedResults.Count > 0)
                {
                    await RebaseMovedShortcutsOffThreadAsync(partial.CompletedResults);
                }
                foreach (FileService.FileTransferResult result in partial.CompletedResults)
                {
                    var match = pending.FirstOrDefault(pair => string.Equals(
                        pair.Plan.SourcePath,
                        result.SourcePath,
                        StringComparison.OrdinalIgnoreCase));
                    if (match.Item is null)
                    {
                        continue;
                    }

                    match.Item.IsRestored = true;
                    match.Item.RestoredPath = result.DestinationPath;
                    match.Item.DestinationPath = result.DestinationPath;
                }

                historyEntry.IsUndone = historyEntry.Items.All(item => item.IsRestored);
                historyEntry.CanUndo = !historyEntry.IsUndone;
                await _settingsService.OrganizationHistory.SaveCheckedAsync();
                await _settingsService.SaveAsync(notifySubscribers: false);
            }

            throw;
        }

        historyEntry.IsUndone = true;
        historyEntry.CanUndo = false;
        foreach (var (item, plan) in pending)
        {
            item.IsRestored = true;
            item.RestoredPath = plan.DestinationPath;
            item.DestinationPath = plan.DestinationPath;
        }

        await _settingsService.OrganizationHistory.SaveCheckedAsync();
        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    public async Task<OrganizationHistoryEntry?> RecordCompletedMoveHistoryAsync(
        WidgetConfig widget,
        string widgetName,
        IReadOnlyList<FileService.FileTransferResult> results,
        IReadOnlyDictionary<string, HashSet<string>?>? preExistingMergeSnapshots = null)
    {
        if (results.Count == 0)
        {
            return null;
        }

        IReadOnlyList<FileService.FileTransferResult> movedResults = results
            .Where(static r =>
                !string.IsNullOrWhiteSpace(r.SourcePath) &&
                !string.IsNullOrWhiteSpace(r.DestinationPath) &&
                !File.Exists(r.SourcePath) &&
                !Directory.Exists(r.SourcePath))
            .ToList();
        if (movedResults.Count == 0)
        {
            return null;
        }

        await RebaseMovedShortcutsOffThreadAsync(movedResults);

        var snapshots = preExistingMergeSnapshots is not null
            ? new Dictionary<string, HashSet<string>?>(preExistingMergeSnapshots, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        (IReadOnlyList<FileService.FileTransferResult> undoableResults, bool allMergesUndoable) =
            await Task.Run(() => ExpandResultsForSafeFolderMergeUndo(movedResults, snapshots));
        bool canSafelyUndo = allMergesUndoable && undoableResults.Count > 0;
        UndoReceiptBatch receipts = canSafelyUndo
            ? await CaptureUndoReceiptsAsync(undoableResults)
            : UndoReceiptBatch.Empty;
        var entry = CreateHistoryEntry(
            widget.Id,
            widgetName,
            OrganizationActionType.ManagedDrop,
            move: true,
            (canSafelyUndo ? undoableResults : movedResults).Select(result => new OrganizationHistoryItem
            {
                Name = Path.GetFileName(result.DestinationPath),
                SourcePath = result.SourcePath,
                DestinationPath = result.DestinationPath,
                TargetWidgetId = widget.Id,
                TargetWidgetName = widgetName,
                DestinationIdentity = canSafelyUndo ? receipts.GetValue(result.DestinationPath) : null,
            }).ToList(),
            canUndo: canSafelyUndo);
        await AddHistoryEntryAsync(entry);
        return entry;
    }

    private const int MaxFolderMergeUndoSnapshotEntries = 4096;

    internal static Dictionary<string, HashSet<string>?> SnapshotPreExistingMergeDestinations(
        IReadOnlyList<FileService.FileTransferPlan> plans)
    {
        var snapshots = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (FileService.FileTransferPlan plan in plans)
        {
            string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.DestinationPath));
            if (!Directory.Exists(dest) || FileService.IsFileSystemLink(dest))
            {
                continue;
            }

            snapshots[dest] = TryCaptureRelativeSubtreeEntries(dest);
        }

        return snapshots;
    }

    private static HashSet<string>? TryCaptureRelativeSubtreeEntries(string rootDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(rootDir);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (set.Count >= MaxFolderMergeUndoSnapshotEntries)
                    {
                        return null;
                    }

                    string rel = Path.GetRelativePath(rootDir, entry);
                    set.Add(rel);
                    if (Directory.Exists(entry) && !FileService.IsFileSystemLink(entry))
                    {
                        pending.Push(entry);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        return set;
    }

    internal static (IReadOnlyList<FileService.FileTransferResult> UndoableResults, bool AllMergesUndoable)
        ExpandResultsForSafeFolderMergeUndo(
            IReadOnlyList<FileService.FileTransferResult> results,
            Dictionary<string, HashSet<string>?> preExistingMergeSnapshots)
    {
        if (preExistingMergeSnapshots.Count == 0)
        {
            return (results, true);
        }

        var expanded = new List<FileService.FileTransferResult>(results.Count);
        foreach (FileService.FileTransferResult result in results)
        {
            string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.DestinationPath));
            if (!preExistingMergeSnapshots.TryGetValue(dest, out HashSet<string>? preExisting))
            {
                expanded.Add(result);
                continue;
            }

            // Never record the pre-existing destination folder itself as an undo
            // unit: undoing it would move files that lived there before the merge.
            if (preExisting is null || !Directory.Exists(dest))
            {
                return ([], false);
            }

            Dictionary<string, string>? exactSourceByDestination = null;
            if (result.MergedLeafResults is { Count: > 0 })
            {
                exactSourceByDestination = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileService.FileTransferResult leaf in result.MergedLeafResults)
                {
                    if (!string.IsNullOrWhiteSpace(leaf.SourcePath) &&
                        !string.IsNullOrWhiteSpace(leaf.DestinationPath))
                    {
                        exactSourceByDestination[Path.GetFullPath(leaf.DestinationPath)] =
                            Path.GetFullPath(leaf.SourcePath);
                    }
                }
            }

            string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.SourcePath));
            var walk = new Stack<string>();
            walk.Push(dest);
            while (walk.Count > 0)
            {
                string current = walk.Pop();
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateFileSystemEntries(current);
                }
                catch
                {
                    return ([], false);
                }

                foreach (string child in children)
                {
                    string rel = Path.GetRelativePath(dest, child);
                    if (!preExisting.Contains(rel))
                    {
                        string fullChild = Path.GetFullPath(child);
                        string resolvedSourcePath;
                        if (exactSourceByDestination is not null &&
                            exactSourceByDestination.TryGetValue(fullChild, out string? mappedSourcePath))
                        {
                            resolvedSourcePath = mappedSourcePath;
                        }
                        else if (TryResolveUnnumberedMergeRelativePath(rel, preExisting, out string unnumberedRel))
                        {
                            resolvedSourcePath = Path.Combine(sourceRoot, unnumberedRel);
                        }
                        else
                        {
                            resolvedSourcePath = Path.Combine(sourceRoot, rel);
                        }

                        expanded.Add(new FileService.FileTransferResult(
                            resolvedSourcePath,
                            child));
                    }
                    else if (Directory.Exists(child) && !FileService.IsFileSystemLink(child))
                    {
                        walk.Push(child);
                    }
                }
            }
        }

        return (expanded, true);
    }

    private static bool TryResolveUnnumberedMergeRelativePath(
        string relativePath,
        HashSet<string> preExisting,
        out string unnumberedRelativePath)
    {
        unnumberedRelativePath = string.Empty;
        string? directoryPart = Path.GetDirectoryName(relativePath);
        string fileName = Path.GetFileName(relativePath);
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int openParen = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (openParen <= 0 || !stem.EndsWith(')'))
        {
            return false;
        }

        ReadOnlySpan<char> numberSlice = stem.AsSpan(openParen + 2, stem.Length - openParen - 3);
        if (!int.TryParse(numberSlice, out int suffixNumber) || suffixNumber < 2)
        {
            return false;
        }

        string originalFileName = stem[..openParen] + extension;
        string candidateRel = string.IsNullOrEmpty(directoryPart)
            ? originalFileName
            : Path.Combine(directoryPart, originalFileName);
        if (!preExisting.Contains(candidateRel))
        {
            return false;
        }

        unnumberedRelativePath = candidateRel;
        return true;
    }

    private async Task AddHistoryEntryAsync(OrganizationHistoryEntry entry)
    {
        var history = _settingsService.OrganizationHistory.Entries;
        history.Insert(0, entry);

        // The global budget and entry cap run here too, so a long session of
        // ordinary imports cannot grow the history without bound between
        // compaction passes — but never at the cost of the file operation
        // that already physically completed. When the journal state cannot
        // be established, only the brand-new entry is capped and older
        // entries wait for the next safe compaction pass.
        bool journalStateKnown = await TryRunRetentionPolicyAsync(history, entry);
        if (!journalStateKnown)
        {
            OrganizationHistoryPolicy.CapEntryReceipts(entry);
        }

        // Checked-but-ignored: a receipt persistence failure must not turn a
        // physically completed file operation into a reported failure (the
        // pre-split settings save had the same best-effort semantics).
        await _settingsService.OrganizationHistory.SaveCheckedAsync();
        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    /// <summary>
    /// Runs the full retention policy when the recovery journal state is
    /// reliably known (absent, or read with its protected transaction id).
    /// Returns false when the journal exists but cannot be read right now —
    /// a replace race or a corrupt file must not fail an ordinary import —
    /// and the caller falls back to capping only the new entry.
    /// </summary>
    private async Task<bool> TryRunRetentionPolicyAsync(
        List<OrganizationHistoryEntry> history,
        OrganizationHistoryEntry newEntry)
    {
        try
        {
            if (!_sharedRecoveryStore.HasPendingJournal)
            {
                OrganizationHistoryPolicy.ApplyRetentionPolicy(history);
                return true;
            }

            var journal = await _sharedRecoveryStore.LoadAsync();
            if (journal is null)
            {
                // The journal vanished between the check and the read; no
                // transaction needs protection.
                OrganizationHistoryPolicy.ApplyRetentionPolicy(history);
                return true;
            }

            OrganizationHistoryPolicy.ApplyRetentionPolicy(history, journal.TransactionId);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[Organizer] Recovery journal could not be read for retention; " +
                $"only the new entry is capped this round: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Captures undo receipts for a batch of completed transfers off the UI
    /// thread. Each receipt is a native open + double stat; on a 2000-item
    /// drop that would otherwise freeze the caller for seconds after the
    /// physical transfer already finished. Serial on purpose: the destination
    /// volume may be a slow SATA/USB device where concurrent metadata seeks
    /// regress, so raise the parallelism only with measured evidence.
    /// </summary>
    private static async Task<UndoReceiptBatch> CaptureUndoReceiptsAsync(
        IReadOnlyList<FileService.FileTransferResult> results)
    {
        if (results.Count == 0)
        {
            return UndoReceiptBatch.Empty;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var receipts = await Task.Run(() =>
        {
            var map = new Dictionary<string, Models.DesktopOrganizationDestinationIdentity?>(
                results.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (FileService.FileTransferResult result in results)
            {
                if (!map.ContainsKey(result.DestinationPath))
                {
                    map[result.DestinationPath] = FileService.CaptureUndoReceiptIdentity(
                        result.DestinationPath);
                }
            }

            return map;
        });
        App.Log(
            $"[OrganizerPerf] receiptCount={results.Count} " +
            $"receiptMs={stopwatch.ElapsedMilliseconds}");
        return new UndoReceiptBatch(receipts);
    }

    private readonly struct UndoReceiptBatch(
        Dictionary<string, Models.DesktopOrganizationDestinationIdentity?> map)
    {
        public static readonly UndoReceiptBatch Empty = new([]);

        public Models.DesktopOrganizationDestinationIdentity? GetValue(
            string destinationPath) =>
            map.TryGetValue(destinationPath, out var identity) ? identity : null;
    }

    private static OrganizationHistoryEntry CreateHistoryEntry(
        string widgetId,
        string widgetName,
        string actionType,
        bool move,
        List<OrganizationHistoryItem> items,
        bool canUndo)
    {
        return new OrganizationHistoryEntry
        {
            WidgetId = widgetId,
            WidgetName = widgetName,
            ActionType = actionType,
            TransferMode = move ? "Move" : "Copy",
            CanUndo = canUndo,
            Items = items
        };
    }

    private static OrganizationHistoryEntry CreateFailureEntry(
        string widgetId,
        string widgetName,
        string actionType,
        bool move,
        IEnumerable<string> sourcePaths,
        string errorMessage)
    {
        return new OrganizationHistoryEntry
        {
            WidgetId = widgetId,
            WidgetName = widgetName,
            ActionType = actionType,
            TransferMode = move ? "Move" : "Copy",
            ErrorMessage = errorMessage,
            Items = sourcePaths
                .Select(path => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(path),
                    SourcePath = path,
                    DestinationPath = string.Empty
                })
                .ToList()
        };
    }
}
