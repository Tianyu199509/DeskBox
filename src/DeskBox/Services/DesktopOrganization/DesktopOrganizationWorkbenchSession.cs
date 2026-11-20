using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Owns mutable user customizations (exclusions, opt-ins, cross-box moves,
/// custom boxes, renames, destination overrides, and tile selection) for an
/// active Desktop Organization session without mutating the base scan plan.
/// </summary>
internal sealed class DesktopOrganizationWorkbenchSession
{
    public sealed record CustomBoxDefinition(
        string WidgetId,
        string Title,
        string TargetDirectoryPath,
        bool AllowEmptyUntilPopulated = false);

    public sealed record MoveResult(
        int MovedCount,
        int SkippedLockedCount);

    private readonly Func<DesktopOrganizationPlan, IReadOnlyCollection<string>, DesktopOrganizationPlan> _rebuildWithOptionalItems;
    private readonly Func<IReadOnlyDictionary<string, WidgetConfig>> _widgetsByIdProvider;

    private readonly HashSet<string> _excludedSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optionalIncludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _newSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _renamedBucketKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reassignedItemBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CustomBoxDefinition> _customBoxes = new(StringComparer.Ordinal);
    private readonly List<string> _customBoxOrder = [];
    private readonly Dictionary<string, string> _boxTitleOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DesktopOrganizationTargetSelection> _targetSelections = new(StringComparer.Ordinal);

    private readonly Dictionary<string, DesktopOrganizationFileSnapshot> _baseItemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defaultBucketByPath = new(StringComparer.OrdinalIgnoreCase);

    private int _nextCustomBoxNumber = 1;
    private string? _selectionAnchorPath;
    private string? _focusedBucketKey;

    public DesktopOrganizationWorkbenchSession(
        Func<DesktopOrganizationPlan, IReadOnlyCollection<string>, DesktopOrganizationPlan> rebuildWithOptionalItems,
        Func<IReadOnlyDictionary<string, WidgetConfig>> widgetsByIdProvider)
    {
        _rebuildWithOptionalItems = rebuildWithOptionalItems ??
            throw new ArgumentNullException(nameof(rebuildWithOptionalItems));
        _widgetsByIdProvider = widgetsByIdProvider ??
            throw new ArgumentNullException(nameof(widgetsByIdProvider));
    }

    public DesktopOrganizationPlan? BasePlan { get; private set; }

    public DesktopOrganizationPlan? EffectivePlan { get; private set; }

    public DesktopOrganizationExecutionResult? LastExecutionResult { get; set; }

    public IReadOnlySet<string> SelectedSourcePaths => _selectedSourcePaths;

    public IReadOnlySet<string> ExcludedSourcePaths => _excludedSourcePaths;

    public IReadOnlySet<string> OptionalIncludedPaths => _optionalIncludedPaths;

    public IReadOnlySet<string> NewSourcePaths => _newSourcePaths;

    public IReadOnlyDictionary<string, string> ReassignedItemBuckets => _reassignedItemBuckets;

    public IReadOnlyDictionary<string, DesktopOrganizationTargetSelection> TargetSelections => _targetSelections;

    public string? FocusedBucketKey => _focusedBucketKey;

    public bool HasActiveUserCustomizations =>
        _excludedSourcePaths.Count > 0 ||
        _optionalIncludedPaths.Count > 0 ||
        _reassignedItemBuckets.Count > 0 ||
        _customBoxes.Count > 0 ||
        _boxTitleOverrides.Count > 0 ||
        _targetSelections.Count > 0;

    public static string GetStableBucketKey(DesktopOrganizationTargetPlan target) =>
        !string.IsNullOrWhiteSpace(target.SourceBucketId)
            ? target.SourceBucketId
            : target.TargetWidgetId;

    public void SetInitialPlan(DesktopOrganizationPlan? plan)
    {
        BasePlan = plan;
        LastExecutionResult = null;
        ResetCustomizationsCore();
        RebuildEffectivePlan();
    }

    public void UpdateSourceScopes(DesktopOrganizationPlan updatedScopeBasePlan)
    {
        BasePlan = updatedScopeBasePlan;
        LastExecutionResult = null;
        RebuildEffectivePlan();
    }

    public CustomBoxDefinition CreateEmptyCustomBox(string defaultTitlePrefix = "New Box")
    {
        string title = BuildNextCustomBoxTitle(defaultTitlePrefix);
        string widgetId = $"custom-{Guid.NewGuid():N}";
        string storageRoot = BasePlan?.StorageRootPath ?? string.Empty;
        HashSet<string> reservedDirs = CollectReservedDirectories();
        string directoryPath = string.IsNullOrWhiteSpace(storageRoot)
            ? DesktopOrganizationPlanner.SanitizeFolderName(title)
            : FileService.GetAvailablePath(
                Path.Combine(storageRoot, DesktopOrganizationPlanner.SanitizeFolderName(title)),
                reservedDirs);

        var customBox = new CustomBoxDefinition(widgetId, title, directoryPath, AllowEmptyUntilPopulated: true);
        _customBoxes.Add(widgetId, customBox);
        _customBoxOrder.Add(widgetId);
        _selectedSourcePaths.Clear();
        _selectionAnchorPath = null;
        LastExecutionResult = null;
        RebuildEffectivePlan();
        return customBox;
    }

    public void RemoveEmptyCustomBox(string targetWidgetOrBucketKey)
    {
        string? bucketKey = ResolveBucketKey(targetWidgetOrBucketKey);
        if (string.IsNullOrWhiteSpace(bucketKey) || !_customBoxes.Remove(bucketKey))
        {
            return;
        }

        _customBoxOrder.Remove(bucketKey);
        _boxTitleOverrides.Remove(bucketKey);
        _renamedBucketKeys.Remove(bucketKey);
        _targetSelections.Remove(bucketKey);
        LastExecutionResult = null;
        RebuildEffectivePlan();
    }

    public void ResetCustomizations()
    {
        LastExecutionResult = null;
        ResetCustomizationsCore();
        RebuildEffectivePlan();
    }

    public DesktopOrganizationPreviewReconciliation ApplyRescanPlan(DesktopOrganizationPlan newScanPlan)
    {
        IReadOnlyCollection<DesktopOrganizationFileSnapshot> previousItems = GetAllSnapshots(BasePlan);
        IReadOnlyCollection<DesktopOrganizationFileSnapshot> currentItems = GetAllSnapshots(newScanPlan);
        DesktopOrganizationPreviewReconciliation reconciliation = DesktopOrganizationPreviewReconciliation.Calculate(
            previousItems,
            currentItems,
            _excludedSourcePaths,
            _optionalIncludedPaths,
            _newSourcePaths);

        BasePlan = newScanPlan;
        LastExecutionResult = null;
        var currentByPath = currentItems.ToDictionary(item => item.SourcePath, StringComparer.OrdinalIgnoreCase);

        _excludedSourcePaths.Clear();
        foreach (string path in reconciliation.ExcludedPaths)
        {
            // Drop any path whose base eligibility changed to non-eligible
            // (e.g., replaced on disk by a locked junction/system file).
            if (currentByPath.TryGetValue(path, out DesktopOrganizationFileSnapshot? snapshot) && snapshot.IsEligible)
            {
                _excludedSourcePaths.Add(path);
            }
        }

        _optionalIncludedPaths.Clear();
        foreach (string path in reconciliation.OptionalPaths)
        {
            if (currentByPath.TryGetValue(path, out DesktopOrganizationFileSnapshot? snapshot) && snapshot.CanOptIn)
            {
                _optionalIncludedPaths.Add(path);
            }
        }

        _newSourcePaths.Clear();
        foreach (string path in reconciliation.NewPaths)
        {
            _newSourcePaths.Add(path);
        }

        var livePaths = currentItems
            .Select(item => item.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string key in _reassignedItemBuckets.Keys.Where(path => !livePaths.Contains(path)).ToList())
        {
            _reassignedItemBuckets.Remove(key);
        }

        _selectedSourcePaths.RemoveWhere(path => !livePaths.Contains(path));
        if (_selectionAnchorPath is not null && !livePaths.Contains(_selectionAnchorPath))
        {
            _selectionAnchorPath = null;
        }

        RebuildEffectivePlan();
        return reconciliation;
    }

    public MoveResult ExcludeItems(IEnumerable<string> sourcePaths)
    {
        if (BasePlan is null)
        {
            return new MoveResult(0, 0);
        }

        int movedCount = 0;
        int skippedCount = 0;
        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            if (!TryGetBaseSnapshot(fullPath, out DesktopOrganizationFileSnapshot? baseSnapshot))
            {
                continue;
            }

            if (baseSnapshot.IsEligible)
            {
                bool changed = _excludedSourcePaths.Add(fullPath);
                changed |= _reassignedItemBuckets.Remove(fullPath);
                if (changed || IsItemCurrentlyInTargets(fullPath))
                {
                    movedCount++;
                }
            }
            else if (baseSnapshot.CanOptIn)
            {
                bool removedOptIn = _optionalIncludedPaths.Remove(fullPath);
                bool removedReassign = _reassignedItemBuckets.Remove(fullPath);
                // Critical P1 invariant: never pollute ExcludedSourcePaths with an
                // item that is naturally retained/opt-in in BasePlan.
                _excludedSourcePaths.Remove(fullPath);
                if (removedOptIn || removedReassign)
                {
                    movedCount++;
                }
            }
            else
            {
                skippedCount++;
            }

            _selectedSourcePaths.Remove(fullPath);
        }

        if (movedCount > 0)
        {
            LastExecutionResult = null;
            RebuildEffectivePlan();
        }

        return new MoveResult(movedCount, skippedCount);
    }

    public MoveResult IncludeItems(IEnumerable<string> sourcePaths, string? targetBucketKey = null)
    {
        if (BasePlan is null)
        {
            return new MoveResult(0, 0);
        }

        int movedCount = 0;
        int skippedCount = 0;
        string? normalizedTargetBucket = ResolveBucketKey(targetBucketKey);

        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            if (!TryGetBaseSnapshot(fullPath, out DesktopOrganizationFileSnapshot? baseSnapshot))
            {
                continue;
            }

            if (baseSnapshot.IsEligible)
            {
                bool changed = _excludedSourcePaths.Remove(fullPath);
                if (!string.IsNullOrWhiteSpace(normalizedTargetBucket))
                {
                    changed |= SetReassignmentIfNotDefault(fullPath, normalizedTargetBucket);
                }
                else
                {
                    changed |= _reassignedItemBuckets.Remove(fullPath);
                }

                if (changed)
                {
                    movedCount++;
                }
            }
            else if (baseSnapshot.CanOptIn)
            {
                bool changed = _optionalIncludedPaths.Add(fullPath);
                _excludedSourcePaths.Remove(fullPath);
                if (!string.IsNullOrWhiteSpace(normalizedTargetBucket))
                {
                    changed |= SetReassignmentIfNotDefault(fullPath, normalizedTargetBucket);
                }

                if (changed)
                {
                    movedCount++;
                }
            }
            else
            {
                skippedCount++;
            }

            _selectedSourcePaths.Remove(fullPath);
        }

        if (movedCount > 0)
        {
            if (!string.IsNullOrWhiteSpace(normalizedTargetBucket) &&
                _customBoxes.TryGetValue(normalizedTargetBucket, out CustomBoxDefinition? existingCustom) &&
                existingCustom.AllowEmptyUntilPopulated)
            {
                _customBoxes[normalizedTargetBucket] = existingCustom with { AllowEmptyUntilPopulated = false };
            }

            LastExecutionResult = null;
            RebuildEffectivePlan();
        }

        return new MoveResult(movedCount, skippedCount);
    }

    public int RestoreManualExclusions()
    {
        if (BasePlan is null || _excludedSourcePaths.Count == 0)
        {
            return 0;
        }

        int restored = _excludedSourcePaths.Count;
        _excludedSourcePaths.Clear();
        LastExecutionResult = null;
        RebuildEffectivePlan();
        return restored;
    }

    public MoveResult MoveItemsToBucket(IEnumerable<string> sourcePaths, string destinationTargetWidgetOrBucketKey)
    {
        if (BasePlan is null || string.IsNullOrWhiteSpace(destinationTargetWidgetOrBucketKey))
        {
            return new MoveResult(0, 0);
        }

        string? destinationBucketKey = ResolveBucketKey(destinationTargetWidgetOrBucketKey);
        if (string.IsNullOrWhiteSpace(destinationBucketKey))
        {
            return new MoveResult(0, 0);
        }

        int movedCount = 0;
        int skippedCount = 0;

        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            if (!TryGetBaseSnapshot(fullPath, out DesktopOrganizationFileSnapshot? baseSnapshot))
            {
                continue;
            }

            if (baseSnapshot.IsEligible)
            {
                bool wasExcluded = _excludedSourcePaths.Remove(fullPath);
                bool assigned = SetReassignmentIfNotDefault(fullPath, destinationBucketKey);
                if (wasExcluded || assigned)
                {
                    movedCount++;
                }
            }
            else if (baseSnapshot.CanOptIn)
            {
                bool addedOptIn = _optionalIncludedPaths.Add(fullPath);
                _excludedSourcePaths.Remove(fullPath);
                bool assigned = SetReassignmentIfNotDefault(fullPath, destinationBucketKey);
                if (addedOptIn || assigned)
                {
                    movedCount++;
                }
            }
            else
            {
                skippedCount++;
            }

            _selectedSourcePaths.Remove(fullPath);
        }

        if (movedCount > 0)
        {
            if (_customBoxes.TryGetValue(destinationBucketKey, out CustomBoxDefinition? existingCustom) &&
                existingCustom.AllowEmptyUntilPopulated)
            {
                _customBoxes[destinationBucketKey] = existingCustom with { AllowEmptyUntilPopulated = false };
            }

            LastExecutionResult = null;
            RebuildEffectivePlan();
        }

        return new MoveResult(movedCount, skippedCount);
    }

    public CustomBoxDefinition? CreateCustomBox(
        IEnumerable<string> sourcePaths,
        string? requestedTitle = null,
        string defaultTitlePrefix = "New Box")
    {
        if (BasePlan is null)
        {
            return null;
        }

        List<string> movablePaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => TryGetBaseSnapshot(path, out DesktopOrganizationFileSnapshot? snapshot) &&
                           (snapshot.IsEligible || snapshot.CanOptIn))
            .ToList();

        if (movablePaths.Count == 0)
        {
            return null;
        }

        string title = string.IsNullOrWhiteSpace(requestedTitle)
            ? BuildNextCustomBoxTitle(defaultTitlePrefix)
            : SanitizeDisplayTitle(requestedTitle);
        string widgetId = $"custom-{Guid.NewGuid():N}";
        HashSet<string> reservedDirs = CollectReservedDirectories();
        string sanitizedFolder = DesktopOrganizationPlanner.SanitizeFolderName(title);
        string directoryPath = FileService.GetAvailablePath(
            Path.Combine(BasePlan.StorageRootPath, sanitizedFolder),
            reservedDirs);

        var customBox = new CustomBoxDefinition(widgetId, title, directoryPath);
        _customBoxes.Add(widgetId, customBox);
        _customBoxOrder.Add(widgetId);

        MoveResult moveResult = MoveItemsToBucket(movablePaths, widgetId);
        if (moveResult.MovedCount == 0)
        {
            _customBoxes.Remove(widgetId);
            _customBoxOrder.Remove(widgetId);
            return null;
        }

        return customBox;
    }

    public bool RenameBox(string targetWidgetOrBucketKey, string newTitle)
    {
        if (BasePlan is null || EffectivePlan is null)
        {
            return false;
        }

        string sanitizedTitle = SanitizeDisplayTitle(newTitle);
        if (string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            return false;
        }

        DesktopOrganizationTargetPlan? target = FindEffectiveTarget(targetWidgetOrBucketKey);
        if (target is null)
        {
            return false;
        }

        string bucketKey = GetStableBucketKey(target);
        if (_customBoxes.TryGetValue(bucketKey, out CustomBoxDefinition? customBox))
        {
            HashSet<string> reservedDirs = CollectReservedDirectories(excludingBucketKey: bucketKey);
            string newDirectory = target.CreatesWidget
                ? FileService.GetAvailablePath(
                    Path.Combine(BasePlan.StorageRootPath, DesktopOrganizationPlanner.SanitizeFolderName(sanitizedTitle)),
                    reservedDirs)
                : customBox.TargetDirectoryPath;

            _customBoxes[bucketKey] = customBox with
            {
                Title = sanitizedTitle,
                TargetDirectoryPath = newDirectory
            };
        }

        _boxTitleOverrides[bucketKey] = sanitizedTitle;
        _renamedBucketKeys.Add(bucketKey);
        LastExecutionResult = null;
        RebuildEffectivePlan();
        return true;
    }

    public void SetTargetDestinationSelection(
        string targetWidgetOrBucketKey,
        DesktopOrganizationTargetSelection selection)
    {
        string? bucketKey = ResolveBucketKey(targetWidgetOrBucketKey) ?? targetWidgetOrBucketKey;
        if (string.IsNullOrWhiteSpace(bucketKey))
        {
            return;
        }

        _targetSelections[bucketKey] = selection;
        LastExecutionResult = null;
        RebuildEffectivePlan();
    }

    public void HandleTileClick(
        string sourcePath,
        string bucketKey,
        IReadOnlyList<string> orderedBucketPaths,
        bool isCtrlDown,
        bool isShiftDown)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(sourcePath);
        _focusedBucketKey = bucketKey;

        if (isShiftDown &&
            !string.IsNullOrWhiteSpace(_selectionAnchorPath) &&
            TryFindIndex(orderedBucketPaths, _selectionAnchorPath, out int anchorIndex) &&
            TryFindIndex(orderedBucketPaths, fullPath, out int currentIndex))
        {
            if (!isCtrlDown)
            {
                _selectedSourcePaths.Clear();
            }

            int start = Math.Min(anchorIndex, currentIndex);
            int end = Math.Max(anchorIndex, currentIndex);
            for (int i = start; i <= end; i++)
            {
                _selectedSourcePaths.Add(Path.GetFullPath(orderedBucketPaths[i]));
            }

            return;
        }

        if (isCtrlDown)
        {
            if (!_selectedSourcePaths.Add(fullPath))
            {
                _selectedSourcePaths.Remove(fullPath);
            }

            _selectionAnchorPath = fullPath;
            return;
        }

        if (_selectedSourcePaths.Count == 1 && _selectedSourcePaths.Contains(fullPath))
        {
            _selectedSourcePaths.Clear();
            _selectionAnchorPath = null;
            return;
        }

        _selectedSourcePaths.Clear();
        _selectedSourcePaths.Add(fullPath);
        _selectionAnchorPath = fullPath;
    }

    public void ToggleTileSelection(string sourcePath, string bucketKey)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(sourcePath);
        _focusedBucketKey = bucketKey;
        if (!_selectedSourcePaths.Add(fullPath))
        {
            _selectedSourcePaths.Remove(fullPath);
        }

        _selectionAnchorPath = fullPath;
    }

    public void SelectAllInBucket(IEnumerable<string> bucketSourcePaths, string bucketKey)
    {
        _focusedBucketKey = bucketKey;
        _selectedSourcePaths.Clear();
        string? first = null;
        foreach (string path in bucketSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(path);
            _selectedSourcePaths.Add(fullPath);
            first ??= fullPath;
        }

        _selectionAnchorPath = first;
    }

    public bool ClearTileSelection()
    {
        if (_selectedSourcePaths.Count == 0 && _selectionAnchorPath is null)
        {
            return false;
        }

        _selectedSourcePaths.Clear();
        _selectionAnchorPath = null;
        return true;
    }

    public void SetFocusedBucket(string? bucketKey)
    {
        _focusedBucketKey = bucketKey;
    }

    public IReadOnlyList<string> GetDragPayloadForTargetItem(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        string fullPath = Path.GetFullPath(sourcePath);
        if (_selectedSourcePaths.Contains(fullPath) && _selectedSourcePaths.Count > 1)
        {
            var effectiveTargetPaths = EffectivePlan is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : EffectivePlan.Targets
                    .SelectMany(target => target.Items)
                    .Select(item => item.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<string> selectedInTargets = _selectedSourcePaths
                .Where(effectiveTargetPaths.Contains)
                .ToList();

            if (selectedInTargets.Count > 0)
            {
                if (!selectedInTargets.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    selectedInTargets.Add(fullPath);
                }

                return selectedInTargets;
            }
        }

        return [fullPath];
    }

    public IReadOnlySet<string> GetSelectedTargetWidgetIds()
    {
        if (EffectivePlan is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return EffectivePlan.Targets
            .Where(target => target.Items.Count > 0)
            .Select(target => target.TargetWidgetId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, DesktopOrganizationTargetSelection> BuildTargetSelectionsForExecution()
    {
        if (EffectivePlan is null || _targetSelections.Count == 0)
        {
            return new Dictionary<string, DesktopOrganizationTargetSelection>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, DesktopOrganizationTargetSelection>(StringComparer.Ordinal);
        foreach (DesktopOrganizationTargetPlan target in EffectivePlan.Targets)
        {
            string bucketKey = GetStableBucketKey(target);
            if (_targetSelections.TryGetValue(bucketKey, out DesktopOrganizationTargetSelection? selection))
            {
                result[target.SourceBucketId] = new DesktopOrganizationTargetSelection
                {
                    SourceBucketId = target.SourceBucketId,
                    IsSelected = selection.IsSelected,
                    DestinationMode = selection.DestinationMode,
                    ExistingWidgetId = selection.ExistingWidgetId
                };
            }
        }

        return result;
    }

    public DesktopOrganizationPreviewSummary CalculateSummary() =>
        EffectivePlan is null
            ? default
            : DesktopOrganizationPreviewSummary.Calculate(
                EffectivePlan,
                new Dictionary<string, DesktopOrganizationTargetSelection>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool TryGetEffectiveTarget(string targetWidgetOrBucketKey, out DesktopOrganizationTargetPlan? target)
    {
        target = FindEffectiveTarget(targetWidgetOrBucketKey);
        return target is not null;
    }

    public void RebuildEffectivePlan()
    {
        _baseItemsByPath.Clear();
        _defaultBucketByPath.Clear();

        if (BasePlan is null)
        {
            EffectivePlan = null;
            return;
        }

        foreach (DesktopOrganizationFileSnapshot snapshot in GetAllSnapshots(BasePlan))
        {
            _baseItemsByPath.TryAdd(snapshot.SourcePath, snapshot);
        }

        DesktopOrganizationPlan workingBase = _optionalIncludedPaths.Count > 0
            ? _rebuildWithOptionalItems(BasePlan, _optionalIncludedPaths.ToArray())
            : BasePlan;

        foreach (DesktopOrganizationTargetPlan target in workingBase.Targets)
        {
            string bucketKey = GetStableBucketKey(target);
            foreach (DesktopOrganizationFileSnapshot item in target.Items)
            {
                _defaultBucketByPath.TryAdd(item.SourcePath, bucketKey);
            }
        }

        var workingTargetsByKey = new Dictionary<string, DesktopOrganizationTargetPlan>(StringComparer.Ordinal);
        var projectedItemsByBucket = new Dictionary<string, List<DesktopOrganizationFileSnapshot>>(StringComparer.Ordinal);
        var orderedBucketKeys = new List<string>();
        var heldReassignedItems = new List<(DesktopOrganizationFileSnapshot Item, string TargetBucketKey, string FallbackBucketKey)>();
        var userExcludedItems = new List<DesktopOrganizationFileSnapshot>();

        void EnsureBucketSlot(DesktopOrganizationTargetPlan template)
        {
            string bucketKey = GetStableBucketKey(template);
            if (!workingTargetsByKey.ContainsKey(bucketKey))
            {
                workingTargetsByKey.Add(bucketKey, template);
                projectedItemsByBucket.Add(bucketKey, []);
                orderedBucketKeys.Add(bucketKey);
            }
        }

        foreach (DesktopOrganizationTargetPlan target in workingBase.Targets)
        {
            string bucketKey = GetStableBucketKey(target);
            EnsureBucketSlot(target);

            foreach (DesktopOrganizationFileSnapshot item in target.Items)
            {
                if (_excludedSourcePaths.Contains(item.SourcePath))
                {
                    userExcludedItems.Add(item with { ExclusionReason = DesktopOrganizationExclusionReason.UserChoice });
                    continue;
                }

                if (_reassignedItemBuckets.TryGetValue(item.SourcePath, out string? reassignedBucket) &&
                    !string.Equals(reassignedBucket, bucketKey, StringComparison.Ordinal))
                {
                    heldReassignedItems.Add((item, reassignedBucket, bucketKey));
                    continue;
                }

                projectedItemsByBucket[bucketKey].Add(item);
            }
        }

        IReadOnlyDictionary<string, WidgetConfig> widgetsById = _widgetsByIdProvider();
        foreach (string customWidgetId in _customBoxOrder)
        {
            if (!_customBoxes.TryGetValue(customWidgetId, out CustomBoxDefinition? box))
            {
                continue;
            }

            var template = new DesktopOrganizationTargetPlan
            {
                SourceBucketId = box.WidgetId,
                CategoryId = string.Empty,
                TargetWidgetId = box.WidgetId,
                SuggestedDisplayName = box.Title,
                TargetDirectoryPath = box.TargetDirectoryPath,
                CreatesWidget = true,
                AutoBindCategoryRule = false,
                Items = []
            };
            EnsureBucketSlot(template);
        }

        foreach ((DesktopOrganizationFileSnapshot item, string targetBucketKey, string fallbackBucketKey) in heldReassignedItems)
        {
            if (projectedItemsByBucket.TryGetValue(targetBucketKey, out List<DesktopOrganizationFileSnapshot>? destinationList))
            {
                destinationList.Add(item);
            }
            else
            {
                _reassignedItemBuckets.Remove(item.SourcePath);
                projectedItemsByBucket[fallbackBucketKey].Add(item);
            }
        }

        // Collect all non-overridden existing directories up front so box renames
        // avoid colliding with existing widgets or untouched boxes.
        var reservedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WidgetConfig widget in widgetsById.Values)
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                reservedDirectories.Add(Path.GetFullPath(widget.MappedFolderPath));
            }
        }

        foreach (string bucketKey in orderedBucketKeys)
        {
            DesktopOrganizationTargetPlan template = workingTargetsByKey[bucketKey];
            if (!_boxTitleOverrides.ContainsKey(bucketKey) &&
                !string.IsNullOrWhiteSpace(template.TargetDirectoryPath))
            {
                reservedDirectories.Add(Path.GetFullPath(template.TargetDirectoryPath));
            }
        }

        var finalTargets = new List<DesktopOrganizationTargetPlan>();
        var survivingCustomBoxIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string bucketKey in orderedBucketKeys)
        {
            List<DesktopOrganizationFileSnapshot> items = projectedItemsByBucket[bucketKey];
            bool keepEmptyCustomBox = _customBoxes.TryGetValue(bucketKey, out CustomBoxDefinition? customBox) &&
                                      customBox.AllowEmptyUntilPopulated;
            if (items.Count == 0 && !keepEmptyCustomBox)
            {
                continue;
            }

            if (customBox is not null)
            {
                survivingCustomBoxIds.Add(bucketKey);
            }

            DesktopOrganizationTargetPlan template = workingTargetsByKey[bucketKey];
            DesktopOrganizationTargetPlan resolved = ApplyBucketOverrides(
                workingBase.StorageRootPath,
                bucketKey,
                template,
                items,
                widgetsById,
                reservedDirectories);

            int existingIndex = !resolved.CreatesWidget
                ? finalTargets.FindIndex(existing =>
                    !existing.CreatesWidget &&
                    string.Equals(existing.TargetWidgetId, resolved.TargetWidgetId, StringComparison.Ordinal))
                : -1;
            if (existingIndex >= 0)
            {
                DesktopOrganizationTargetPlan existingTarget = finalTargets[existingIndex];
                var seenPaths = existingTarget.Items
                    .Select(item => item.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var mergedItems = new List<DesktopOrganizationFileSnapshot>(existingTarget.Items);
                foreach (DesktopOrganizationFileSnapshot candidate in resolved.Items)
                {
                    if (seenPaths.Add(candidate.SourcePath))
                    {
                        mergedItems.Add(candidate);
                    }
                }

                finalTargets[existingIndex] = existingTarget.CloneWith(
                    existingTarget.TargetWidgetId,
                    existingTarget.SuggestedDisplayName,
                    existingTarget.TargetDirectoryPath,
                    createsWidget: false,
                    mergedItems,
                    autoBindCategoryRule: existingTarget.AutoBindCategoryRule && resolved.AutoBindCategoryRule);
            }
            else
            {
                finalTargets.Add(resolved);
            }
        }

        PruneEmptyCustomBoxes(survivingCustomBoxIds);

        var finalExcluded = new List<DesktopOrganizationFileSnapshot>(
            workingBase.ExcludedItems.Count + userExcludedItems.Count);
        foreach (DesktopOrganizationFileSnapshot item in workingBase.ExcludedItems)
        {
            if (!IsItemPresentInTargets(finalTargets, item.SourcePath))
            {
                finalExcluded.Add(item);
            }
        }

        finalExcluded.AddRange(userExcludedItems);

        EffectivePlan = new DesktopOrganizationPlan
        {
            Id = workingBase.Id,
            DesktopPath = workingBase.DesktopPath,
            PublicDesktopPath = workingBase.PublicDesktopPath,
            PublicDesktopUnavailable = workingBase.PublicDesktopUnavailable,
            IncludePersonalDesktop = workingBase.IncludePersonalDesktop,
            IncludePublicDesktop = workingBase.IncludePublicDesktop,
            SourceItems = workingBase.SourceItems,
            StorageRootPath = workingBase.StorageRootPath,
            Targets = finalTargets,
            ExcludedItems = finalExcluded
        };
    }

    private DesktopOrganizationTargetPlan ApplyBucketOverrides(
        string storageRootPath,
        string bucketKey,
        DesktopOrganizationTargetPlan template,
        List<DesktopOrganizationFileSnapshot> items,
        IReadOnlyDictionary<string, WidgetConfig> widgetsById,
        HashSet<string> reservedDirectories)
    {
        if (_targetSelections.TryGetValue(bucketKey, out DesktopOrganizationTargetSelection? selection) &&
            selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget &&
            !string.IsNullOrWhiteSpace(selection.ExistingWidgetId) &&
            widgetsById.TryGetValue(selection.ExistingWidgetId, out WidgetConfig? existingWidget) &&
            !string.IsNullOrWhiteSpace(existingWidget.MappedFolderPath))
        {
            string displayName = _boxTitleOverrides.TryGetValue(bucketKey, out string? renamed)
                ? renamed
                : existingWidget.Name;
            return template.CloneWith(
                existingWidget.Id,
                displayName,
                Path.GetFullPath(existingWidget.MappedFolderPath),
                createsWidget: false,
                items,
                autoBindCategoryRule: false);
        }

        if (_boxTitleOverrides.TryGetValue(bucketKey, out string? customTitle))
        {
            bool autoBindRule = template.AutoBindCategoryRule && !_renamedBucketKeys.Contains(bucketKey);
            if (template.CreatesWidget)
            {
                string folderName = DesktopOrganizationPlanner.SanitizeFolderName(customTitle);
                string directoryPath = FileService.GetAvailablePath(
                    Path.Combine(storageRootPath, folderName),
                    reservedDirectories);
                reservedDirectories.Add(directoryPath);
                return template.CloneWith(
                    template.TargetWidgetId,
                    customTitle,
                    directoryPath,
                    createsWidget: true,
                    items,
                    autoBindCategoryRule: autoBindRule);
            }

            return template.CloneWith(
                template.TargetWidgetId,
                customTitle,
                template.TargetDirectoryPath,
                createsWidget: false,
                items,
                autoBindCategoryRule: false);
        }

        if (_customBoxes.ContainsKey(bucketKey))
        {
            string directoryPath = FileService.GetAvailablePath(template.TargetDirectoryPath, reservedDirectories);
            reservedDirectories.Add(directoryPath);
            return template.CloneWith(
                template.TargetWidgetId,
                template.SuggestedDisplayName,
                directoryPath,
                createsWidget: true,
                items,
                autoBindCategoryRule: false);
        }

        return template.CloneWith(
            template.TargetWidgetId,
            template.SuggestedDisplayName,
            template.TargetDirectoryPath,
            template.CreatesWidget,
            items);
    }

    private void PruneEmptyCustomBoxes(HashSet<string> survivingCustomBoxIds)
    {
        foreach (string obsoleteId in _customBoxes.Keys
                     .Where(id => !survivingCustomBoxIds.Contains(id))
                     .ToList())
        {
            _customBoxes.Remove(obsoleteId);
            _customBoxOrder.Remove(obsoleteId);
            _boxTitleOverrides.Remove(obsoleteId);
            _renamedBucketKeys.Remove(obsoleteId);
            _targetSelections.Remove(obsoleteId);
            foreach (string path in _reassignedItemBuckets
                         .Where(pair => string.Equals(pair.Value, obsoleteId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToList())
            {
                _reassignedItemBuckets.Remove(path);
            }
        }
    }

    private bool SetReassignmentIfNotDefault(string fullPath, string destinationBucketKey)
    {
        if (_defaultBucketByPath.TryGetValue(fullPath, out string? defaultBucket) &&
            string.Equals(defaultBucket, destinationBucketKey, StringComparison.Ordinal))
        {
            return _reassignedItemBuckets.Remove(fullPath);
        }

        if (_reassignedItemBuckets.TryGetValue(fullPath, out string? existing) &&
            string.Equals(existing, destinationBucketKey, StringComparison.Ordinal))
        {
            return false;
        }

        _reassignedItemBuckets[fullPath] = destinationBucketKey;
        return true;
    }

    private string? ResolveBucketKey(string? targetWidgetOrBucketKey)
    {
        if (string.IsNullOrWhiteSpace(targetWidgetOrBucketKey))
        {
            return null;
        }

        if (_customBoxes.ContainsKey(targetWidgetOrBucketKey))
        {
            return targetWidgetOrBucketKey;
        }

        DesktopOrganizationTargetPlan? match = FindEffectiveTarget(targetWidgetOrBucketKey);
        return match is not null ? GetStableBucketKey(match) : targetWidgetOrBucketKey;
    }

    private DesktopOrganizationTargetPlan? FindEffectiveTarget(string targetWidgetOrBucketKey)
    {
        if (EffectivePlan is null || string.IsNullOrWhiteSpace(targetWidgetOrBucketKey))
        {
            return null;
        }

        return EffectivePlan.Targets.FirstOrDefault(target =>
            string.Equals(target.TargetWidgetId, targetWidgetOrBucketKey, StringComparison.Ordinal) ||
            string.Equals(GetStableBucketKey(target), targetWidgetOrBucketKey, StringComparison.Ordinal));
    }

    private HashSet<string> CollectReservedDirectories(string? excludingBucketKey = null)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WidgetConfig widget in _widgetsByIdProvider().Values)
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                reserved.Add(Path.GetFullPath(widget.MappedFolderPath));
            }
        }

        if (EffectivePlan is not null)
        {
            foreach (DesktopOrganizationTargetPlan target in EffectivePlan.Targets)
            {
                if (!string.IsNullOrWhiteSpace(excludingBucketKey) &&
                    string.Equals(GetStableBucketKey(target), excludingBucketKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(target.TargetDirectoryPath))
                {
                    reserved.Add(Path.GetFullPath(target.TargetDirectoryPath));
                }
            }
        }

        return reserved;
    }

    private string BuildNextCustomBoxTitle(string defaultTitlePrefix)
    {
        var usedTitles = EffectivePlan?.Targets
            .Select(target => target.SuggestedDisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            int candidateNumber = _nextCustomBoxNumber++;
            string candidate = candidateNumber == 1
                ? defaultTitlePrefix
                : $"{defaultTitlePrefix} {candidateNumber}";
            if (!usedTitles.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private bool TryGetBaseSnapshot(string fullPath, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DesktopOrganizationFileSnapshot? snapshot)
    {
        if (_baseItemsByPath.TryGetValue(fullPath, out snapshot))
        {
            return true;
        }

        snapshot = null;
        return false;
    }

    private bool IsItemCurrentlyInTargets(string fullPath) =>
        EffectivePlan is not null && IsItemPresentInTargets(EffectivePlan.Targets, fullPath);

    private static bool IsItemPresentInTargets(
        IReadOnlyCollection<DesktopOrganizationTargetPlan> targets,
        string fullPath)
    {
        foreach (DesktopOrganizationTargetPlan target in targets)
        {
            foreach (DesktopOrganizationFileSnapshot item in target.Items)
            {
                if (string.Equals(item.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyCollection<DesktopOrganizationFileSnapshot> GetAllSnapshots(DesktopOrganizationPlan? plan)
    {
        if (plan is null)
        {
            return [];
        }

        return (plan.SourceItems.Count > 0
                ? plan.SourceItems
                : plan.Targets.SelectMany(target => target.Items).Concat(plan.ExcludedItems))
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string SanitizeDisplayTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string cleaned = string.Concat(rawTitle.Where(ch => !char.IsControl(ch))).Trim();
        const int maxLength = 64;
        return cleaned.Length > maxLength ? cleaned[..maxLength].Trim() : cleaned;
    }

    private static bool TryFindIndex(IReadOnlyList<string> paths, string targetPath, out int foundIndex)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(paths[i]), targetPath, StringComparison.OrdinalIgnoreCase))
            {
                foundIndex = i;
                return true;
            }
        }

        foundIndex = -1;
        return false;
    }

    private void ResetCustomizationsCore()
    {
        _excludedSourcePaths.Clear();
        _optionalIncludedPaths.Clear();
        _newSourcePaths.Clear();
        _selectedSourcePaths.Clear();
        _renamedBucketKeys.Clear();
        _reassignedItemBuckets.Clear();
        _customBoxes.Clear();
        _customBoxOrder.Clear();
        _boxTitleOverrides.Clear();
        _targetSelections.Clear();
        _nextCustomBoxNumber = 1;
        _selectionAnchorPath = null;
        _focusedBucketKey = null;
    }
}
