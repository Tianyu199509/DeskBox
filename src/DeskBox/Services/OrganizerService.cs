using DeskBox.Models;

namespace DeskBox.Services;

public sealed class OrganizerService
{
    private readonly SettingsService _settingsService;
    private readonly FileService _fileService;
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
        DesktopAutoOrganizationSuppressionRegistry autoOrganizationSuppressions)
    {
        _settingsService = settingsService;
        _fileService = fileService;
        _desktopPathProvider = desktopPathProvider ?? GetDefaultDesktopPath;
        _autoOrganizationSuppressions = autoOrganizationSuppressions;
    }

    internal DesktopAutoOrganizationSuppressionRegistry AutoOrganizationSuppressions =>
        _autoOrganizationSuppressions;

    public IReadOnlyList<OrganizationHistoryEntry> GetRecentHistory(int maxCount = 6)
    {
        return _settingsService.Settings.RecentOrganizationHistory
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(Math.Max(0, maxCount))
            .ToList();
    }

    public OrganizationHistoryEntry? GetLatestUndoableEntry()
    {
        return _settingsService.Settings.RecentOrganizationHistory
            .Where(entry => entry.CanUndo && !entry.IsUndone && !entry.IsFailed && entry.Items.Count > 0)
            .OrderByDescending(entry => entry.TimestampUtc)
            .FirstOrDefault();
    }

    public async Task<OrganizationHistoryEntry> OrganizeDropAsync(
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
                return CreateHistoryEntry(
                    widget.Id,
                    widgetName,
                    OrganizationActionType.ManagedDrop,
                    move,
                    [],
                    canUndo: false);
            }

            var results = await _fileService.ExecuteTransferPlanAsync(
                plans,
                move,
                useShellProgress,
                ownerWindowHandle,
                progress,
                cancellationToken);
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
                    TargetWidgetName = widgetName
                }).ToList(),
                canUndo: move);

            await AddHistoryEntryAsync(historyEntry);
            return historyEntry;
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
                            TargetWidgetName = widgetName
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

    public async Task<OrganizationHistoryEntry> MoveItemBackToDesktopAsync(
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

    public async Task<OrganizationHistoryEntry> MoveItemsBackToDesktopAsync(
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
                    TargetWidgetName = widgetName
                }).ToList(),
                canUndo: true);

            await AddHistoryEntryAsync(historyEntry);
            return historyEntry;
        }
        catch (Exception ex)
        {
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                plans.Select(plan => plan.DestinationPath));
            await AddHistoryEntryAsync(CreateFailureEntry(
                widget.Id,
                widgetName,
                OrganizationActionType.MoveBackToDesktop,
                move: true,
                normalizedSourcePaths,
                ex.Message));
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
        var historyEntry = _settingsService.Settings.RecentOrganizationHistory
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

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<FileService.FileTransferPlan>(historyEntry.Items.Count);

        foreach (var item in historyEntry.Items)
        {
            if (!File.Exists(item.DestinationPath) && !Directory.Exists(item.DestinationPath))
            {
                throw new InvalidOperationException($"Could not find undo target: {item.Name}");
            }

            string restorePath = FileService.GetAvailablePath(item.SourcePath, reservedPaths);
            plans.Add(new FileService.FileTransferPlan(item.DestinationPath, restorePath));
        }

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
        catch
        {
            _autoOrganizationSuppressions.CompleteOperation(
                operationId,
                plans.Select(plan => plan.DestinationPath));
            throw;
        }

        historyEntry.IsUndone = true;
        historyEntry.CanUndo = false;
        for (int index = 0; index < plans.Count; index++)
        {
            historyEntry.Items[index].DestinationPath = plans[index].DestinationPath;
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    private async Task AddHistoryEntryAsync(OrganizationHistoryEntry entry)
    {
        _settingsService.Settings.RecentOrganizationHistory.Insert(0, entry);
        await _settingsService.SaveAsync(notifySubscribers: false);
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
