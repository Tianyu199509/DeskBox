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
            new DesktopAutoOrganizationSuppressionRegistry())
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

    public IReadOnlyList<OrganizationHistoryEntry> GetRecentHistory(int maxCount = 6)
    {
        return _settingsService.OrganizationHistory.Entries
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Max(0, maxCount))
            .ToList();
    }

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

            var results = await _fileService.ExecuteTransferPlanAsync(
                plans,
                move,
                useShellProgress,
                ownerWindowHandle,
                progress,
                cancellationToken);
            // Capture undo receipts off the UI thread: each is a native
            // open+stat pair, and a 2000-item drop would otherwise freeze
            // the caller for seconds after the transfer already finished.
            // Copy imports can never be undone, so they skip the cost.
            var receipts = move
                ? await CaptureUndoReceiptsAsync(results)
                : UndoReceiptBatch.Empty;
            var historyEntry = CreateHistoryEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.ManagedDrop,
                move,
                results.Select(result => new OrganizationHistoryItem
                {
                    Name = Path.GetFileName(result.DestinationPath),
                    SourcePath = result.SourcePath,
                    DestinationPath = result.DestinationPath,
                    TargetWidgetId = widget.Id,
                    TargetWidgetName = widgetName,
                    // Durable undo receipt: undo verifies the object at the
                    // destination against the identity recorded here, never a
                    // fresh capture of whatever later occupies the path.
                    DestinationIdentity = receipts.GetValue(result.DestinationPath)
                }).ToList(),
                canUndo: move);

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
        catch (Exception ex) when (
            ex is FileService.IFileTransferWithCompletedResults partial)
        {
            IReadOnlyList<FileService.FileTransferResult> completedResults =
                partial.CompletedResults;
            if (completedResults.Count > 0)
            {
                bool canUndoCompletedMove = move && completedResults.All(
                    result =>
                        !File.Exists(result.SourcePath) &&
                        !Directory.Exists(result.SourcePath));
                var partialReceipts = canUndoCompletedMove
                    ? await CaptureUndoReceiptsAsync(completedResults)
                    : UndoReceiptBatch.Empty;
                await AddHistoryEntryAsync(CreateHistoryEntry(
                    widget.Id,
                    widgetName,
                    OrganizationActionType.ManagedDrop,
                    move,
                    completedResults.Select(result =>
                        new OrganizationHistoryItem
                        {
                            Name = Path.GetFileName(result.DestinationPath),
                            SourcePath = result.SourcePath,
                            DestinationPath = result.DestinationPath,
                            TargetWidgetId = widget.Id,
                            TargetWidgetName = widgetName,
                            DestinationIdentity = partialReceipts.GetValue(result.DestinationPath)
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
        if (!Directory.Exists(rootPath) ||
            !FileService.TryIsPathUnderDirectoryResolved(
                rootPath,
                mappedRootPath,
                out bool isUnderMappedRoot) ||
            !isUnderMappedRoot)
        {
            throw new InvalidOperationException(
                "The requested destination is outside the widget's mapped folder.");
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
        return sourcePaths
            .Where(path => !FileService.IsEntryDirectlyInDirectoryResolved(
                path,
                rootPath))
            .Select(path =>
            {
                string destinationPath = FileService.GetAvailablePath(
                    Path.Combine(rootPath, Path.GetFileName(path)),
                    reservedPaths);
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

            string[] failedPaths = normalizedSourcePaths
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
                await _settingsService.SaveAsync(notifySubscribers: false);
            }

            throw;
        }

        historyEntry.IsUndone = true;
        historyEntry.CanUndo = false;
        foreach (var (item, plan) in pending)
        {
            item.DestinationPath = plan.DestinationPath;
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
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
