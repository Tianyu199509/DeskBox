using System.Runtime.InteropServices;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class DesktopOrganizationCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly WidgetManager _widgetManager;
    private readonly OrganizerService _organizerService;
    private readonly LocalizationService _localizationService;
    private readonly DesktopOrganizationScanner _scanner;
    private readonly DesktopOrganizationPlanner _planner;
    private readonly DesktopOrganizationPlacementPlanner _placementPlanner = new();
    private readonly DesktopOrganizationTransaction _transaction;

    public DesktopOrganizationCoordinator(
        SettingsService settingsService,
        FileService fileService,
        WidgetManager widgetManager,
        OrganizerService organizerService,
        LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _widgetManager = widgetManager;
        _organizerService = organizerService;
        _localizationService = localizationService;
        var classifier = new DesktopOrganizationClassifier();
        _scanner = new DesktopOrganizationScanner(classifier);
        _planner = new DesktopOrganizationPlanner(new DesktopOrganizationRuleResolver());
        _transaction = new DesktopOrganizationTransaction(settingsService, fileService)
        {
            AutoOrganizationSuppressions = organizerService.AutoOrganizationSuppressions
        };
    }

    public async Task<DesktopOrganizationPlan> BuildPlanAsync(
        bool includeSlowItems = false,
        CancellationToken cancellationToken = default)
    {
        DesktopOrganizationScanResult scan =
            await _scanner.ScanAsync(includeSlowItems, cancellationToken);
        string root = SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.DefaultManagedStorageRootPath);
        DesktopOrganizationPlan plan = _planner.CreatePlan(
            scan,
            root,
            _settingsService.Settings.Widgets,
            _settingsService.Settings.DesktopOrganizationRules,
            ResolveCategoryName);

        AssignNonOverlappingBounds(plan);
        return plan;
    }

    /// <summary>
    /// Compiles the user's preview selections into an immutable execution
    /// plan. The scan plan is never mutated, so changing a combo box cannot
    /// leak into a later refresh or into another execution attempt.
    /// </summary>
    public DesktopOrganizationPlan CreateExecutionPlan(
        DesktopOrganizationPlan previewPlan,
        IReadOnlyCollection<DesktopOrganizationTargetSelection> selections,
        IReadOnlySet<string>? excludedSourcePaths = null)
    {
        var selectionByBucket = selections
            .Where(selection => !string.IsNullOrWhiteSpace(selection.SourceBucketId))
            .ToDictionary(selection => selection.SourceBucketId, StringComparer.Ordinal);
        var widgetsById = _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .ToDictionary(widget => widget.Id, StringComparer.Ordinal);
        var targetsByDestination = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.Ordinal);
        var retainedByChoice = new List<DesktopOrganizationFileSnapshot>();

        foreach (DesktopOrganizationTargetPlan source in previewPlan.Targets)
        {
            if (selectionByBucket.TryGetValue(source.SourceBucketId, out DesktopOrganizationTargetSelection? selection) &&
                !selection.IsSelected)
            {
                retainedByChoice.AddRange(source.Items.Select(item => item with
                {
                    ExclusionReason = DesktopOrganizationExclusionReason.UserChoice
                }));
                continue;
            }

            var includedItems = source.Items.Where(item => excludedSourcePaths?.Contains(item.SourcePath) != true).ToList();
            retainedByChoice.AddRange(source.Items.Where(item => excludedSourcePaths?.Contains(item.SourcePath) == true)
                .Select(item => item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice }));
            DesktopOrganizationTargetPlan target = source.CloneWith(source.TargetWidgetId,
                source.SuggestedDisplayName, source.TargetDirectoryPath, source.CreatesWidget, includedItems);
            bool shouldResolveExistingDestination =
                selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget ||
                !source.CreatesWidget;
            if (shouldResolveExistingDestination)
            {
                string? requestedWidgetId = selection?.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget
                    ? selection.ExistingWidgetId
                    : source.TargetWidgetId;
                if (string.IsNullOrWhiteSpace(requestedWidgetId) ||
                    !widgetsById.TryGetValue(requestedWidgetId, out WidgetConfig? widget) ||
                    string.IsNullOrWhiteSpace(widget.MappedFolderPath))
                {
                    throw new InvalidOperationException(
                        _localizationService.T("DesktopOrganization.Error.TargetUnavailable"));
                }

                target = source.CloneWith(
                    widget.Id,
                    widget.Name,
                    Path.GetFullPath(widget.MappedFolderPath),
                    createsWidget: false,
                    includedItems);
            }

            if (targetsByDestination.TryGetValue(target.TargetWidgetId, out DesktopOrganizationTargetPlan? merged))
            {
                targetsByDestination[target.TargetWidgetId] = merged.CloneWith(
                    merged.TargetWidgetId,
                    merged.SuggestedDisplayName,
                    merged.TargetDirectoryPath,
                    merged.CreatesWidget,
                    merged.Items.Concat(target.Items));
            }
            else
            {
                targetsByDestination.Add(target.TargetWidgetId, target);
            }
        }

        var executionPlan = new DesktopOrganizationPlan
        {
            Id = Guid.NewGuid().ToString("N"),
            DesktopPath = previewPlan.DesktopPath,
            PublicDesktopPath = previewPlan.PublicDesktopPath,
            IncludePersonalDesktop = previewPlan.IncludePersonalDesktop,
            IncludePublicDesktop = previewPlan.IncludePublicDesktop,
            SourceItems = previewPlan.SourceItems,
            PublicDesktopUnavailable = previewPlan.PublicDesktopUnavailable,
            StorageRootPath = previewPlan.StorageRootPath,
            Targets = targetsByDestination.Values
                .Where(target => target.Items.Count > 0)
                .ToList(),
            ExcludedItems = previewPlan.ExcludedItems
                .Concat(retainedByChoice)
                .ToList()
        };

        AssignNonOverlappingBounds(executionPlan);
        return executionPlan;
    }

    public DesktopOrganizationPlan CreatePreviewPlanWithOptionalItems(
        DesktopOrganizationPlan basePlan,
        IReadOnlyCollection<string> includedSourcePaths,
        bool? includePersonalDesktop = null,
        bool? includePublicDesktop = null)
    {
        var included = includedSourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DesktopOrganizationFileSnapshot[] allItems = (basePlan.SourceItems.Count > 0
                ? basePlan.SourceItems
                : basePlan.Targets.SelectMany(target => target.Items).Concat(basePlan.ExcludedItems))
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => item.CanOptIn && included.Contains(item.SourcePath)
                ? item with { ExclusionReason = DesktopOrganizationExclusionReason.None }
                : item)
            .ToArray();
        var scan = new DesktopOrganizationScanResult
        {
            DesktopPath = basePlan.DesktopPath,
            PublicDesktopPath = basePlan.PublicDesktopPath,
            PublicDesktopUnavailable = basePlan.PublicDesktopUnavailable,
            Items = allItems.ToList()
        };
        DesktopOrganizationPlan plan = _planner.CreatePlan(
            scan,
            basePlan.StorageRootPath,
            _settingsService.Settings.Widgets,
            _settingsService.Settings.DesktopOrganizationRules,
            ResolveCategoryName,
            includePersonalDesktop ?? basePlan.IncludePersonalDesktop,
            includePublicDesktop ?? basePlan.IncludePublicDesktop);
        AssignNonOverlappingBounds(plan);
        return plan;
    }

    public IReadOnlyList<DesktopOrganizationDestinationOption> GetDestinationOptions()
    {
        return _settingsService.Settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(widget => new DesktopOrganizationDestinationOption(
                widget.Id,
                widget.Name,
                Path.GetFullPath(widget.MappedFolderPath!),
                IsDynamic: false))
            .ToList();
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(plan, progress: null, cancellationToken);
    }

    public async Task<DesktopOrganizationExecutionResult> ExecuteAsync(
        DesktopOrganizationPlan plan,
        IProgress<DesktopOrganizationProgress>? progress,
        CancellationToken cancellationToken = default,
        IntPtr ownerWindowHandle = default)
    {
        string[] existingTargetIds = plan.Targets
            .Where(target => !target.CreatesWidget)
            .Select(target => target.TargetWidgetId)
            .ToArray();
        _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: true);
        DesktopOrganizationExecutionResult result;
        try
        {
            result = await _transaction.ExecuteAsync(plan, progress, cancellationToken, ownerWindowHandle);
        }
        finally
        {
            _widgetManager.SetDesktopOrganizationBusy(existingTargetIds, isBusy: false);
        }

        var shownWidgetIds = new List<string>();
        try
        {
            foreach (WidgetConfig widget in result.CreatedWidgets)
            {
                await _widgetManager.ShowWidgetAsync(widget.Id, reveal: true, autoRestoreOnReveal: false);
                shownWidgetIds.Add(widget.Id);
            }

            foreach (DesktopOrganizationTargetPlan target in plan.Targets.Where(target => !target.CreatesWidget))
            {
                await _widgetManager.RefreshFileWidgetAsync(target.TargetWidgetId);
            }

            return result;
        }
        catch (Exception ex)
        {
            // The file transaction is already committed. A window failure must
            // never erase its history or initiate an unsolicited public undo.
            App.Log($"[DesktopOrganization] Could not reveal an organized widget: {ex}");
            return result;
        }
    }

    public bool HasPendingRecovery => _transaction.HasPendingRecovery;

    public Task<int> RecoverPendingAsync(IntPtr ownerWindowHandle = default) => _transaction.RecoverPendingAsync(ownerWindowHandle);

    public DesktopOrganizationPlan CreateRetryPlan(DesktopOrganizationPlan previous, IReadOnlySet<string> remainingPaths)
    {
        var plan = DesktopOrganizationPlanner.CreateRetryPlan(previous, remainingPaths, _settingsService.Settings.Widgets);
        foreach (var target in plan.Targets.Where(target => !target.CreatesWidget))
        {
            if (!_settingsService.Settings.Widgets.Any(widget => widget.Id == target.TargetWidgetId &&
                !widget.IsDisabled && widget.WidgetKind == WidgetKind.File &&
                string.Equals(widget.MappedFolderPath, target.TargetDirectoryPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(_localizationService.T("DesktopOrganization.Error.TargetUnavailable"));
        }
        AssignNonOverlappingBounds(plan);
        return plan;
    }

    public async Task UndoAsync(string historyId, IntPtr ownerWindowHandle = default)
    {
        OrganizationHistoryEntry? history = _settingsService.Settings.RecentOrganizationHistory
            .FirstOrDefault(entry =>
                string.Equals(entry.Id, historyId, StringComparison.Ordinal));
        if (history is null)
        {
            throw new InvalidOperationException("The organization history entry no longer exists.");
        }

        await _organizerService.UndoAsync(historyId, ownerWindowHandle);
        foreach (OrganizationHistoryTarget target in history.Targets)
        {
            if (target.WasCreated && (!Directory.Exists(target.DirectoryPath) ||
                !Directory.EnumerateFileSystemEntries(target.DirectoryPath).Any()))
            {
                await _widgetManager.RemoveWidgetAsync(
                    target.WidgetId,
                    WidgetRemovalAction.RemoveWidgetOnly);
                _settingsService.Settings.DesktopOrganizationRules.RemoveAll(rule =>
                    string.Equals(
                        rule.TargetWidgetId,
                        target.WidgetId,
                        StringComparison.Ordinal));
                TryDeleteEmptyDirectory(target.DirectoryPath);
            }
            else
            {
                await _widgetManager.RefreshFileWidgetAsync(target.WidgetId);
            }
        }

        await _settingsService.SaveAsync(notifySubscribers: false);
    }

    private string ResolveCategoryName(string categoryId)
    {
        string key = $"DesktopOrganization.Category.{categoryId}";
        string localized = _localizationService.T(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? categoryId
            : localized;
    }

    private void AssignNonOverlappingBounds(DesktopOrganizationPlan plan)
    {
        if (plan.NewWidgetCount == 0)
        {
            return;
        }

        NativeRect nativeWorkArea = default;
        if (!SystemParametersInfo(SpiGetWorkArea, 0, ref nativeWorkArea, 0))
        {
            return;
        }

        double scale = Math.Max(1, GetDpiForSystem() / 96d);
        var workArea = new DesktopOrganizationRect(
            nativeWorkArea.Left,
            nativeWorkArea.Top,
            nativeWorkArea.Right - nativeWorkArea.Left,
            nativeWorkArea.Bottom - nativeWorkArea.Top);
        var occupied = _settingsService.Settings.Widgets
            .Where(widget => widget.IsVisible && !widget.IsDisabled)
            .Select(widget => new DesktopOrganizationRect(
                widget.X,
                widget.Y,
                widget.Width * scale,
                widget.Height * scale))
            .ToList();

        if (!_placementPlanner.TryAssignBounds(
                plan,
                workArea,
                occupied,
                _settingsService.Settings.DefaultWidgetWidth * scale,
                _settingsService.Settings.DefaultWidgetHeight * scale,
                DesktopOrganizationPlacementPlanner.DefaultEdgeMargin * scale,
                DesktopOrganizationPlacementPlanner.DefaultGap * scale))
        {
            throw new InvalidOperationException(
                _localizationService.T("DesktopOrganization.Error.NoLayoutSpace"));
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private const uint SpiGetWorkArea = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref NativeRect value,
        uint update);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
