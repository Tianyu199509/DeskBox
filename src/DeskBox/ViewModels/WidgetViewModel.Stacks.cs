using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

public readonly record struct WidgetVisibleInsertionAnchor(
    string TargetOrderKey,
    int VisibleIndex);

public partial class WidgetViewModel
{
    private const string LooseItemOrderKeyPrefix = "Item:";
    private const string ManualStackKeyPrefix = "Manual:";
    private readonly ObservableCollection<WidgetItem> _stackDisplayItems = [];
    private readonly Dictionary<string, WidgetStackItem> _stackItems = [];
    private bool _fileStacksEnabled;
    private string _fileStackGroupBy = SettingsService.FileStackGroupByKind;
    private int _fileStackThreshold = SettingsService.DefaultFileStackThreshold;
    private string _fileStackOrderBy = SettingsService.FileStackOrderByWidget;
    private string _fileStackOpenMode = SettingsService.FileStackOpenModeInline;
    private string? _expandedStackKey;
    private bool _stackRebuildQueued;
    private bool _hasBuiltStackDisplay;
    private bool _isSynchronizingItemsFromStackProjection;
    private int _deferredStackRebuildDepth;
    private bool _deferredStackRebuildPending;
    private bool _legacyStackMigrationQueued;
    private DispatcherQueueTimer? _stackDateBoundaryTimer;
    private HashSet<string> _disabledStacks = new(StringComparer.Ordinal);
    private Dictionary<string, string> _stackNameOverrides = new(StringComparer.Ordinal);
    private List<string> _stackOrder = [];
    private Dictionary<string, List<string>> _stackMemberOverrides =
        new(StringComparer.Ordinal);
    private Dictionary<string, WidgetFolderSortOverride> _subfolderSortOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _subfolderDisplayOrders =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _subfolderManualItemOrders =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<WidgetItem> VisibleItems => UsesStackProjection
        ? _stackDisplayItems
        : Items;

    // The master switch now owns the whole feature, including manual
    // stacks. Keeping manual stacks alive with the feature disabled made the
    // master and the automatic-grouping switch indistinguishable.
    public bool UsesStackProjection => FileStacksEnabled;

    /// <summary>
    /// Captures the visible display unit immediately after an insertion point.
    /// The external import path must use this stable key when a stack
    /// projection is active; a visible integer index is not an index into
    /// <see cref="Items"/> once a stack collapses multiple members.
    /// </summary>
    internal WidgetVisibleInsertionAnchor? CaptureVisibleInsertionAnchor(
        IReadOnlyList<WidgetItem> visibleItems,
        int visibleInsertionIndex)
    {
        if (!UsesStackProjection ||
            visibleInsertionIndex < 0 ||
            visibleInsertionIndex >= visibleItems.Count)
        {
            return null;
        }

        WidgetItem target = visibleItems[visibleInsertionIndex];
        string? targetKey = ResolveVisibleDisplayUnitOrderKey(target);
        return string.IsNullOrWhiteSpace(targetKey)
            ? null
            : new WidgetVisibleInsertionAnchor(
                targetKey,
                visibleInsertionIndex);
    }

    /// <summary>
    /// Applies an external file import at the captured visible display-unit
    /// anchor. This updates the stack order metadata separately from the raw
    /// folder item collection, preserving the ordering the user saw in the
    /// icon surface.
    /// </summary>
    internal void ApplyImportedStackInsertion(
        IReadOnlyList<string> destinationPaths,
        WidgetVisibleInsertionAnchor anchor)
    {
        if (!UsesStackProjection || destinationPaths.Count == 0)
        {
            return;
        }

        // A file can leave a manual stack, disappear in Explorer, and be
        // dropped back into this widget before the source surface's delayed
        // reconciliation runs. Remove the old membership before rebuilding
        // the projection; otherwise ResolveDisplayUnitOrderKey resolves the
        // re-imported file to its historical stack and the visible insertion
        // line is silently ignored.
        string[] detachedStackKeys =
            DetachImportedRootInsertionStackMembership(destinationPaths);
        RebuildStackDisplayItems();
        List<string> movingKeys = destinationPaths
            .Select(ResolveDisplayUnitOrderKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (movingKeys.Count == 0)
        {
            if (detachedStackKeys.Length > 0)
            {
                PersistStackCustomizations();
            }

            return;
        }

        // Keep unknown historical keys. They are intentionally retained so a
        // temporarily unavailable item can recover its prior position. Only
        // the explicitly imported units are removed and reinserted.
        IReadOnlyList<string> activeOrder = GetActiveFolderDisplayOrder();
        List<string> order;
        if (detachedStackKeys.Length > 0)
        {
            // Removing the last members of a manual stack can dissolve its
            // display unit. In that case the old active order no longer
            // contains the newly loose members, so start from the rebuilt
            // visible order and retain only genuinely unknown historical
            // keys as a recovery tail.
            HashSet<string> currentKeys = GetCurrentDisplayUnitOrder()
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> detachedKeys = detachedStackKeys
                .ToHashSet(StringComparer.Ordinal);
            order = GetCurrentDisplayUnitOrder();
            order.AddRange(activeOrder.Where(key =>
                !currentKeys.Contains(key) &&
                !detachedKeys.Contains(key)));
        }
        else
        {
            order = activeOrder.Count > 0
                ? activeOrder.ToList()
                : GetCurrentDisplayUnitOrder();
        }
        order.RemoveAll(key => movingKeys.Contains(key, StringComparer.Ordinal));

        int insertionIndex = order.IndexOf(anchor.TargetOrderKey);
        if (insertionIndex < 0)
        {
            // A projection can rebuild between DragOver and import. The
            // captured visible index is only a bounded fallback when its
            // stable neighbor disappeared during that refresh.
            insertionIndex = Math.Clamp(
                anchor.VisibleIndex,
                0,
                order.Count);
        }

        order.InsertRange(
            Math.Clamp(insertionIndex, 0, order.Count),
            movingKeys);
        SetActiveFolderDisplayOrder(order);
        if (detachedStackKeys.Length > 0)
        {
            PersistStackCustomizations();
        }
        else
        {
            PersistStackDisplayOrder();
        }

        App.LogVerbose(
            $"[FileStack] Root import insertion widget={Config.Id} " +
            $"paths={destinationPaths.Count} movingUnits={movingKeys.Count} " +
            $"detachedStacks={string.Join(',', detachedStackKeys)} " +
            $"anchor={anchor.TargetOrderKey} index={anchor.VisibleIndex}");
    }

    public bool FileStacksEnabled
    {
        get => _fileStacksEnabled;
        private set => SetProperty(ref _fileStacksEnabled, value);
    }

    public string FileStackGroupBy
    {
        get => _fileStackGroupBy;
        private set => SetProperty(ref _fileStackGroupBy, value);
    }

    public int FileStackThreshold
    {
        get => _fileStackThreshold;
        private set => SetProperty(ref _fileStackThreshold, value);
    }

    public string FileStackOrderBy
    {
        get => _fileStackOrderBy;
        private set => SetProperty(ref _fileStackOrderBy, value);
    }

    public string FileStackOpenMode
    {
        get => _fileStackOpenMode;
        private set
        {
            if (SetProperty(ref _fileStackOpenMode, value))
            {
                OnPropertyChanged(nameof(UsesStackPopover));
            }
        }
    }

    public bool UsesStackPopover => string.Equals(
        FileStackOpenMode,
        SettingsService.FileStackOpenModePopover,
        StringComparison.Ordinal);

    public bool HasDisabledStacks => _disabledStacks.Count > 0;

    public bool HasExpandedStack => !string.IsNullOrWhiteSpace(
        _expandedStackKey);

    public WidgetStackItem? GetExpandedStack() =>
        _expandedStackKey is not null &&
        _stackItems.TryGetValue(
            _expandedStackKey,
            out WidgetStackItem? stack)
                ? stack
                : null;

    public WidgetStackItem? FindStackByKey(string stackKey) =>
        !string.IsNullOrWhiteSpace(stackKey) &&
        _stackItems.TryGetValue(stackKey, out WidgetStackItem? stack)
            ? stack
            : null;

    public bool FileStacksFollowGlobalDefaults =>
        WidgetFileStackSettings.FollowsGlobalDefaults(Config);

    public bool FileStacksEnabledFollowsGlobal =>
        WidgetFileStackSettings.GetEnabledOverride(Config) is null;

    public bool FileStackGroupByFollowsGlobal =>
        WidgetFileStackSettings.GetGroupByOverride(Config) is null;

    public bool FileStackThresholdFollowsGlobal =>
        WidgetFileStackSettings.GetThresholdOverride(Config) is null;

    public bool FileStackOrderByFollowsGlobal =>
        WidgetFileStackSettings.GetOrderByOverride(Config) is null;

    public bool FileStackOpenModeFollowsGlobal =>
        WidgetFileStackSettings.GetOpenModeOverride(Config) is null;

    public void SetStackExpanded(WidgetStackItem stack, bool expanded)
    {
        if (UsesStackPopover)
        {
            if (_expandedStackKey is not null)
            {
                _expandedStackKey = null;
                if (!TryCollapseExpandedStackRun())
                {
                    RebuildStackDisplayItems();
                }
            }
            return;
        }

        string? targetKey = expanded ? stack.StackKey : null;
        if (string.Equals(
                _expandedStackKey,
                targetKey,
                StringComparison.Ordinal))
        {
            return;
        }

        if (TryApplyStackExpansionDelta(stack, expanded))
        {
            return;
        }

        _expandedStackKey = targetKey;
        RebuildStackDisplayItems();
    }

    /// <summary>
    /// Applies a pure expansion-state delta directly to the projected list.
    /// An expand/collapse toggle never changes membership or ordering, so the
    /// regroup/reorder pipeline can be skipped entirely; the run of visible
    /// members must match the stack's members exactly or the caller falls
    /// back to the full rebuild. Verification happens before any mutation.
    /// </summary>
    private bool TryApplyStackExpansionDelta(
        WidgetStackItem target,
        bool expanded)
    {
        if (!UsesStackProjection)
        {
            return false;
        }

        if (_expandedStackKey is not null)
        {
            if (!TryCollapseExpandedStackRun())
            {
                return false;
            }
            _expandedStackKey = null;
        }

        if (!expanded)
        {
            return true;
        }

        int targetIndex = IndexOfStackDisplayItem(target.StackKey);
        if (targetIndex < 0 || target.Members.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < target.Members.Count; i++)
        {
            WidgetItem member = target.Members[i];
            member.IsStackChild = true;
            _stackDisplayItems.Insert(targetIndex + 1 + i, member);
        }
        target.SetExpanded(true);
        _expandedStackKey = target.StackKey;
        return true;
    }

    /// <summary>
    /// Removes the visible member run of the currently expanded stack after
    /// verifying it matches the stack's members reference-for-reference.
    /// Returns false without mutating when the projection does not have the
    /// expected shape.
    /// </summary>
    private bool TryCollapseExpandedStackRun()
    {
        if (_expandedStackKey is not { } key)
        {
            return true;
        }

        int index = IndexOfStackDisplayItem(key);
        if (index < 0 ||
            _stackDisplayItems[index] is not WidgetStackItem stack)
        {
            return false;
        }

        int runEnd = index + 1;
        while (runEnd < _stackDisplayItems.Count &&
               _stackDisplayItems[runEnd].IsStackChild)
        {
            runEnd++;
        }

        int runLength = runEnd - (index + 1);
        if (runLength != stack.Members.Count)
        {
            return false;
        }

        for (int i = 0; i < runLength; i++)
        {
            if (!ReferenceEquals(
                    _stackDisplayItems[index + 1 + i],
                    stack.Members[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < runLength; i++)
        {
            _stackDisplayItems[index + 1].IsStackChild = false;
            _stackDisplayItems.RemoveAt(index + 1);
        }
        stack.SetExpanded(false);
        return true;
    }

    private int IndexOfStackDisplayItem(string stackKey)
    {
        for (int index = 0; index < _stackDisplayItems.Count; index++)
        {
            if (_stackDisplayItems[index] is WidgetStackItem stack &&
                string.Equals(
                    stack.StackKey,
                    stackKey,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Makes a real file item addressable by the ItemsControl. Newly created
    /// entries can immediately join a collapsed automatic stack, in which case
    /// the stack must be expanded before the item can be scrolled to or edited.
    /// </summary>
    public bool RevealItemForInteraction(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        WidgetItem? item = Items.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                itemPath,
                StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        if (!UsesStackProjection)
        {
            return true;
        }

        RebuildStackDisplayItems();
        WidgetStackItem? containingStack = _stackDisplayItems
            .OfType<WidgetStackItem>()
            .FirstOrDefault(stack => stack.Members.Any(member =>
                string.Equals(
                    member.Path,
                    itemPath,
                    StringComparison.OrdinalIgnoreCase)));
        if (containingStack is not null &&
            !containingStack.IsExpanded)
        {
            SetStackExpanded(containingStack, expanded: true);
        }

        return _stackDisplayItems.Any(candidate =>
            ReferenceEquals(candidate, item));
    }

    /// <summary>
    /// Prepares a projected file item for direct manipulation without changing
    /// either of the user's sorting preferences. Sorting is committed only when
    /// a drop actually changes an order.
    /// </summary>
    public bool PrepareVisibleItemReorder(WidgetItem item)
    {
        if (!UsesStackProjection)
        {
            return false;
        }

        if (item.IsStackChild)
        {
            return PrepareStackMemberReorder(item);
        }

        if (item is WidgetStackItem ||
            !_stackDisplayItems.Any(candidate =>
                ReferenceEquals(candidate, item)))
        {
            return false;
        }

        return true;
    }

    public bool PrepareStackMemberReorder(WidgetItem item)
    {
        if (!UsesStackProjection ||
            !item.IsStackChild ||
            string.IsNullOrWhiteSpace(_expandedStackKey))
        {
            return false;
        }

        return item.IsStackChild;
    }

    /// <summary>
    /// Reorders a member within the currently expanded stack. Editing an
    /// automatic stack first turns it into an explicit manual stack so future
    /// rule changes cannot silently rewrite the user's order.
    /// target is expressed in VisibleItems coordinates so both window hosts
    /// can share the exact same stack-boundary and persistence behavior.
    /// </summary>
    public bool MoveExpandedStackMemberForReorder(
        WidgetItem item,
        int visibleTargetIndex)
    {
        if (!PrepareStackMemberReorder(item) ||
            _expandedStackKey is null ||
            !_stackItems.TryGetValue(
                _expandedStackKey,
                out WidgetStackItem? stack))
        {
            return false;
        }

        if (!stack.IsManual)
        {
            stack = ConvertStackToManual(stack, []);
        }

        int stackIndex = IndexOfReference(
            _stackDisplayItems,
            stack,
            0);
        int currentVisibleIndex = IndexOfReference(
            _stackDisplayItems,
            item,
            0);
        if (stackIndex < 0 || currentVisibleIndex < 0)
        {
            return false;
        }

        int firstMemberIndex = stackIndex + 1;
        int lastMemberIndex =
            firstMemberIndex + stack.Members.Count - 1;
        int targetVisibleIndex = Math.Clamp(
            visibleTargetIndex,
            firstMemberIndex,
            lastMemberIndex);
        if (targetVisibleIndex == currentVisibleIndex)
        {
            return false;
        }

        int targetMemberIndex =
            targetVisibleIndex - firstMemberIndex;
        WidgetItem targetMember = stack.Members[targetMemberIndex];
        if (!_stackMemberOverrides.TryGetValue(
                stack.StackKey,
                out List<string>? overridePaths) ||
            !TryMoveStackMemberOverride(
                overridePaths,
                item.Path,
                targetMember.Path))
        {
            return false;
        }

        PersistStackCustomizations();
        return true;
    }

    internal static bool TryMoveStackMemberOverride(
        List<string> paths,
        string sourcePath,
        string targetPath)
    {
        int sourceIndex = paths.FindIndex(path =>
            string.Equals(
                NormalizeStackMemberPath(path),
                NormalizeStackMemberPath(sourcePath),
                StringComparison.OrdinalIgnoreCase));
        int targetIndex = paths.FindIndex(path =>
            string.Equals(
                NormalizeStackMemberPath(path),
                NormalizeStackMemberPath(targetPath),
                StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 ||
            targetIndex < 0 ||
            sourceIndex == targetIndex)
        {
            return false;
        }

        string path = paths[sourceIndex];
        paths.RemoveAt(sourceIndex);
        paths.Insert(targetIndex, path);
        return true;
    }

    internal static bool TryMoveStackMemberOverrides(
        List<string> paths,
        IReadOnlyCollection<string> sourcePaths,
        int insertionIndex)
    {
        if (paths.Count == 0 || sourcePaths.Count == 0)
        {
            return false;
        }

        var normalizedSources = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeStackMemberPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedSources.Count == 0)
        {
            return false;
        }

        List<string> original = paths.ToList();
        List<string> moving = paths
            .Where(path => normalizedSources.Contains(
                NormalizeStackMemberPath(path)))
            .ToList();
        if (moving.Count == 0)
        {
            return false;
        }

        int clampedInsertionIndex = Math.Clamp(
            insertionIndex,
            0,
            paths.Count);
        int movingBeforeInsertion = paths
            .Take(clampedInsertionIndex)
            .Count(path => normalizedSources.Contains(
                NormalizeStackMemberPath(path)));
        paths.RemoveAll(path => normalizedSources.Contains(
            NormalizeStackMemberPath(path)));
        int adjustedInsertionIndex = Math.Clamp(
            clampedInsertionIndex - movingBeforeInsertion,
            0,
            paths.Count);
        paths.InsertRange(adjustedInsertionIndex, moving);
        return !paths.SequenceEqual(
            original,
            StringComparer.OrdinalIgnoreCase);
    }

    // Block move for top-level display-unit order keys (loose files and
    // stack cards). targetPosition counts the non-moving units that stay
    // ahead of the block; after the removal it is directly an index into the
    // remainder, so it survives membership mutations that shifted raw
    // display indices between capture and commit.
    internal static bool TryMoveDisplayUnitOverrides(
        List<string> order,
        IReadOnlyCollection<string> movingKeys,
        int targetPosition)
    {
        if (order.Count == 0 || movingKeys.Count == 0)
        {
            return false;
        }

        HashSet<string> movingKeySet = movingKeys
            .ToHashSet(StringComparer.Ordinal);
        List<string> moving = order
            .Where(movingKeySet.Contains)
            .ToList();
        if (moving.Count == 0)
        {
            return false;
        }

        List<string> original = order.ToList();
        int insertionIndex = Math.Clamp(
            targetPosition,
            0,
            order.Count - moving.Count);
        order.RemoveAll(movingKeySet.Contains);
        order.InsertRange(insertionIndex, moving);
        return !order.SequenceEqual(
            original,
            StringComparer.Ordinal);
    }

    public bool MoveStackMembersForReorder(
        string stackKey,
        IEnumerable<WidgetItem> draggedItems,
        int memberInsertionIndex)
    {
        if (!UsesStackProjection ||
            string.IsNullOrWhiteSpace(stackKey) ||
            !_stackItems.TryGetValue(
                stackKey,
                out WidgetStackItem? stack))
        {
            return false;
        }

        HashSet<string> stackPaths = stack.Members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] draggedPaths = NormalizeStackMembers(draggedItems)
            .Select(item => NormalizeStackMemberPath(item.Path))
            .Where(stackPaths.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (draggedPaths.Length == 0)
        {
            return false;
        }

        List<string> currentPaths = stack.Members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToList();
        if (!TryMoveStackMemberOverrides(
                currentPaths,
                draggedPaths,
                memberInsertionIndex))
        {
            return false;
        }

        if (!stack.IsManual)
        {
            stack = ConvertStackToManual(stack, []);
        }

        if (!_stackMemberOverrides.TryGetValue(
                stack.StackKey,
                out List<string>? overridePaths) ||
            !TryMoveStackMemberOverrides(
                overridePaths,
                draggedPaths,
                memberInsertionIndex))
        {
            return false;
        }

        PersistStackCustomizations();
        return true;
    }

    public bool MoveVisibleItemForReorder(
        WidgetItem item,
        int visibleInsertionIndex)
    {
        if (!UsesStackProjection)
        {
            return false;
        }

        if (item.IsStackChild)
        {
            WidgetStackItem? containingStack = FindContainingStack(item);
            if (containingStack is null)
            {
                return false;
            }

            int currentVisibleIndex = IndexOfReference(
                _stackDisplayItems,
                item,
                0);
            if (currentVisibleIndex < 0)
            {
                return false;
            }

            int stackIndex = IndexOfReference(
                _stackDisplayItems,
                containingStack,
                0);
            int firstMemberIndex = stackIndex + 1;
            int insertionAfterMembers =
                firstMemberIndex + containingStack.Members.Count;
            if (visibleInsertionIndex <= stackIndex ||
                visibleInsertionIndex >= insertionAfterMembers)
            {
                int cappedDetachIndex = Math.Clamp(
                    visibleInsertionIndex,
                    0,
                    _stackDisplayItems.Count);
                string movingKey = GetLooseItemOrderKey(item);
                HashSet<string> singleMovingKey = new(StringComparer.Ordinal)
                {
                    movingKey
                };
                int targetTopLevelPosition = 0;
                for (int index = 0; index < cappedDetachIndex; index++)
                {
                    WidgetItem candidate = _stackDisplayItems[index];
                    if (WillTopLevelDisplayUnitRemainAfterDetach(
                            candidate,
                            singleMovingKey))
                    {
                        targetTopLevelPosition++;
                    }
                }

                if (!RemoveItemFromStack(item))
                {
                    return false;
                }

                List<string> currentOrder = GetCurrentDisplayUnitOrder();
                if (TryMoveDisplayUnitOverrides(
                        currentOrder,
                        [movingKey],
                        targetTopLevelPosition))
                {
                    EnsureSubfolderManualSortOverrideOnLooseReorder();
                    SetActiveFolderDisplayOrder(
                        MergeWithRetainedNonVisibleOrderKeys(currentOrder));
                    PersistStackDisplayOrder();
                }

                return true;
            }

            int targetVisibleIndex = visibleInsertionIndex;
            if (targetVisibleIndex > currentVisibleIndex)
            {
                targetVisibleIndex--;
            }

            return MoveExpandedStackMemberForReorder(
                item,
                targetVisibleIndex);
        }

        return PrepareVisibleItemReorder(item) &&
            MoveDisplayUnitForReorder(
                GetLooseItemOrderKey(item),
                visibleInsertionIndex);
    }

    /// <summary>
    /// Block-aware multi-selection variant of
    /// <see cref="MoveVisibleItemForReorder"/>. The dragged items keep their
    /// visible relative order and land as one contiguous block. Inline
    /// expanded-stack members whose insertion point stays inside that stack
    /// reorder within it (reusing the plural member move); members dropped
    /// outside their stack region are detached first, mirroring the
    /// single-item boundary semantics. Every phase funnels into at most one
    /// projection rebuild + persist, so a large selection cannot trigger a
    /// per-item rebuild storm.
    /// </summary>
    public bool MoveVisibleItemsForReorder(
        IReadOnlyList<WidgetItem> draggedItems,
        int visibleInsertionIndex)
    {
        if (!UsesStackProjection || draggedItems.Count == 0)
        {
            return false;
        }

        HashSet<WidgetItem> draggedSet = new(
            draggedItems,
            ReferenceEqualityComparer.Instance);
        List<WidgetItem> block = _stackDisplayItems
            .Where(draggedSet.Contains)
            .ToList();
        if (block.Count == 0)
        {
            return false;
        }

        if (block.Count == 1)
        {
            return MoveVisibleItemForReorder(
                block[0],
                visibleInsertionIndex);
        }

        int cappedIndex = Math.Clamp(
            visibleInsertionIndex,
            0,
            _stackDisplayItems.Count);

        // Classify before mutating anything: the insertion index is only
        // valid against the display list this drag started from.
        Dictionary<WidgetStackItem, List<WidgetItem>> memberMoves = new(
            ReferenceEqualityComparer.Instance);
        List<WidgetItem> looseOrDetached = [];
        foreach (WidgetItem item in block)
        {
            if (item is WidgetStackItem)
            {
                continue;
            }

            if (!item.IsStackChild)
            {
                looseOrDetached.Add(item);
                continue;
            }

            WidgetStackItem? containingStack = FindContainingStack(item);
            if (containingStack is null)
            {
                continue;
            }

            int stackIndex = IndexOfReference(
                _stackDisplayItems,
                containingStack,
                0);
            int memberEndExclusive =
                stackIndex + 1 + containingStack.Members.Count;
            if (cappedIndex > stackIndex && cappedIndex < memberEndExclusive)
            {
                if (!memberMoves.TryGetValue(
                        containingStack,
                        out List<WidgetItem>? members))
                {
                    members = [];
                    memberMoves[containingStack] = members;
                }

                members.Add(item);
            }
            else
            {
                looseOrDetached.Add(item);
            }
        }

        // Resolve the landing position once, as "how many non-moving
        // top-level units stay ahead of the block". Later phases (member
        // reorder, detach rebuild) can shift raw indices but not this count.
        HashSet<string> movingKeys = looseOrDetached
            .Select(GetLooseItemOrderKey)
            .ToHashSet(StringComparer.Ordinal);
        int targetTopLevelPosition = 0;
        for (int index = 0; index < cappedIndex; index++)
        {
            WidgetItem candidate = _stackDisplayItems[index];
            if (WillTopLevelDisplayUnitRemainAfterDetach(candidate, movingKeys))
            {
                targetTopLevelPosition++;
            }
        }

        bool changed = false;
        foreach (KeyValuePair<WidgetStackItem, List<WidgetItem>> move in
             memberMoves)
        {
            int stackIndex = IndexOfReference(
                _stackDisplayItems,
                move.Key,
                0);
            if (stackIndex < 0)
            {
                continue;
            }

            int memberInsertionIndex = Math.Clamp(
                cappedIndex - (stackIndex + 1),
                0,
                move.Key.Members.Count);
            changed |= MoveStackMembersForReorder(
                move.Key.StackKey,
                move.Value,
                memberInsertionIndex);
        }

        bool membershipChanged = false;
        if (looseOrDetached.Any(item => item.IsStackChild))
        {
            membershipChanged = DetachStackMembersForBlockReorder(
                looseOrDetached.Where(item => item.IsStackChild).ToList());
            if (membershipChanged)
            {
                // Make the detached members visible as loose units so the
                // block move below sees their order keys.
                RebuildStackDisplayItems();
            }
        }

        if (looseOrDetached.Count > 0)
        {
            List<string> currentOrder = GetCurrentDisplayUnitOrder();
            List<string> orderedMovingKeys = looseOrDetached
                .Select(GetLooseItemOrderKey)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            bool orderChanged = TryMoveDisplayUnitOverrides(
                currentOrder,
                orderedMovingKeys,
                targetTopLevelPosition);
            if (orderChanged)
            {
                EnsureSubfolderManualSortOverrideOnLooseReorder();
                SetActiveFolderDisplayOrder(
                    MergeWithRetainedNonVisibleOrderKeys(currentOrder));
            }

            if (membershipChanged)
            {
                PersistStackCustomizations();
            }
            else if (orderChanged)
            {
                PersistStackDisplayOrder();
            }

            changed |= membershipChanged || orderChanged;
        }

        return changed;
    }

    // Mirrors RemoveItemsFromStack's dissolution semantics for a block
    // reorder detach, but defers persistence so the caller can bundle the
    // membership change with the display-order move into one rebuild + save.
    private bool DetachStackMembersForBlockReorder(
        IReadOnlyList<WidgetItem> items)
    {
        Dictionary<WidgetStackItem, List<string>> removalsByStack = new(
            ReferenceEqualityComparer.Instance);
        foreach (WidgetItem item in items)
        {
            WidgetStackItem? stack = FindContainingStack(item);
            if (stack is null)
            {
                continue;
            }

            string path = NormalizeStackMemberPath(item.Path);
            if (!removalsByStack.TryGetValue(
                    stack,
                    out List<string>? paths))
            {
                paths = [];
                removalsByStack[stack] = paths;
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        bool changed = false;
        foreach (KeyValuePair<WidgetStackItem, List<string>> entry in
             removalsByStack)
        {
            WidgetStackItem stack = entry.Key;
            HashSet<string> removedPaths = entry.Value
                .Where(path => stack.Members.Any(member =>
                    string.Equals(
                        NormalizeStackMemberPath(member.Path),
                        path,
                        StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (removedPaths.Count == 0)
            {
                continue;
            }

            if (stack.Members.Count - removedPaths.Count < 2)
            {
                if (stack.IsManual)
                {
                    List<string>? replacementOrder = null;
                    if (_stackMemberOverrides.TryGetValue(
                            stack.StackKey,
                            out List<string>? storedMembers))
                    {
                        replacementOrder = BuildDissolvedRemovalOrder(
                            storedMembers,
                            removedPaths);
                    }

                    RemoveManualStackCustomization(
                        stack.StackKey,
                        replacementOrder);
                }
                else
                {
                    _disabledStacks.Add(stack.StackKey);
                    _expandedStackKey = null;
                }

                changed = true;
                continue;
            }

            if (!stack.IsManual)
            {
                // Rare: partially emptying an automatic stack keeps it
                // alive as a manual stack. The conversion persists on its
                // own; the block persist after it still runs only once.
                stack = ConvertStackToManual(stack, []);
            }

            if (!_stackMemberOverrides.TryGetValue(
                    stack.StackKey,
                    out List<string>? members))
            {
                continue;
            }

            List<string> detachedOrderedPaths = members
                .Where(path => removedPaths.Contains(NormalizeStackMemberPath(path)))
                .ToList();
            List<string> survivingOrderedPaths = members
                .Where(path => !removedPaths.Contains(NormalizeStackMemberPath(path)))
                .ToList();
            if (detachedOrderedPaths.Count == 0)
            {
                continue;
            }

            if (survivingOrderedPaths.Count < 2)
            {
                RemoveManualStackCustomization(
                    stack.StackKey,
                    survivingOrderedPaths.Concat(detachedOrderedPaths).ToList());
            }
            else
            {
                members.Clear();
                members.AddRange(survivingOrderedPaths);
                InsertDetachedMembersAfterStackInOrder(
                    stack.StackKey,
                    detachedOrderedPaths);
            }

            changed = true;
        }

        return changed;
    }

    internal void StabilizeStackDisplay()
    {
        RebuildStackDisplayItems();
        foreach (var stack in _stackItems.Values)
        {
            stack.RefreshPresentationState();
        }
    }

    internal void PrepareStackDisplayForReuse()
    {
        if (!_hasBuiltStackDisplay || _stackRebuildQueued)
        {
            StabilizeStackDisplay();
            return;
        }

        // Source changes and settings already rebuild (or queue a rebuild of)
        // this projection while detached. Keep its stable items on a warm switch.
        PerformanceLogger.RecordStackProjectionReuseSkip();
        foreach (var stack in _stackItems.Values)
        {
            stack.RefreshPresentationState();
        }
    }

    public void SetFileStacksEnabledOverride(bool? enabled)
    {
        WidgetFileStackSettings.SetEnabledOverride(Config, enabled);
        PersistStackOverrides();
    }

    public void SetFileStackGroupByOverride(string? groupBy)
    {
        WidgetFileStackSettings.SetGroupByOverride(Config, groupBy);
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            WidgetFileStackSettings.SetEnabledOverride(Config, true);
        }
        PersistStackOverrides();
    }

    public void SetFileStackThresholdOverride(int? threshold)
    {
        WidgetFileStackSettings.SetThresholdOverride(Config, threshold);
        PersistStackOverrides();
    }

    public void SetFileStackOrderByOverride(string? orderBy)
    {
        WidgetFileStackSettings.SetOrderByOverride(Config, orderBy);
        PersistStackOverrides();
    }

    public void SetFileStackOpenModeOverride(string? openMode)
    {
        WidgetFileStackSettings.SetOpenModeOverride(Config, openMode);
        PersistStackOverrides();
    }

    public void ClearFileStackOverrides()
    {
        WidgetFileStackSettings.ClearOverrides(Config);
        PersistStackOverrides();
    }

    public void SetStackDisabled(string stackKey, bool disabled)
    {
        if (disabled)
        {
            _disabledStacks.Add(stackKey);
        }
        else
        {
            _disabledStacks.Remove(stackKey);
        }
        WidgetFileStackSettings.SetDisabledStacks(Config, _disabledStacks);
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public void SetStackNameOverride(string stackKey, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _stackNameOverrides.Remove(stackKey);
        }
        else
        {
            _stackNameOverrides[stackKey] = name.Trim();
        }
        WidgetFileStackSettings.SetStackNameOverrides(Config, _stackNameOverrides);
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public void SetStackOrder(List<string> order)
    {
        SetActiveFolderDisplayOrder(order.ToList());
        PersistActiveFolderDisplayOrderToMetadata();
        PersistStackOverrides();
        QueueStackDisplayRebuild();
    }

    public void ClearStackDisplayOrderOverride()
    {
        if (TryGetCurrentSubfolderScopeKey(out string scopeKey))
        {
            if (!_subfolderDisplayOrders.Remove(scopeKey))
            {
                return;
            }

            WidgetFileStackSettings.SetSubfolderDisplayOrders(
                Config,
                _subfolderDisplayOrders);
        }
        else
        {
            if (_stackOrder.Count == 0)
            {
                return;
            }

            _stackOrder = [];
            WidgetFileStackSettings.SetStackOrder(Config, null);
        }

        _settingsService.UpdateWidget(
            Config,
            notifySubscribers: false);
        _settingsService.SaveDebounced(
            notifySubscribers: false);
        RebuildStackDisplayItems();
    }

    public bool CreateManualStack(
        IEnumerable<WidgetItem> selectedItems)
    {
        bool projectionWasEnabled = UsesStackProjection;
        List<WidgetItem> members =
            NormalizeStackMembers(selectedItems);
        if (members.Count < 2)
        {
            return false;
        }

        IReadOnlyList<string> activeOrder = GetActiveFolderDisplayOrder();
        bool maintainExplicitOrder =
            EffectiveSortMode == WidgetSortMode.Manual ||
            activeOrder.Count > 0;
        List<string> visibleOrder = maintainExplicitOrder
            ? (projectionWasEnabled
                ? GetCurrentDisplayUnitOrder()
                : Items.Select(GetLooseItemOrderKey).ToList())
            : [];
        int insertionIndex = maintainExplicitOrder
            ? ResolveManualStackInsertionIndex(
                visibleOrder,
                members)
            : 0;
        if (maintainExplicitOrder)
        {
            SetActiveFolderDisplayOrder(
                MergeWithRetainedNonVisibleOrderKeys(visibleOrder));
        }

        HashSet<string> memberPaths = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveMemberOverrides(memberPaths);

        string stackKey =
            $"{ManualStackKeyPrefix}{Guid.NewGuid():N}";
        _stackMemberOverrides[stackKey] = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToList();
        if (maintainExplicitOrder)
        {
            InsertStackIntoOrderRemovingMembers(
                stackKey,
                memberPaths,
                insertionIndex);
        }
        _expandedStackKey = null;
        PersistStackCustomizations();
        if (projectionWasEnabled != UsesStackProjection)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }
        return true;
    }

    private WidgetStackItem ConvertStackToManual(
        WidgetStackItem sourceStack,
        IEnumerable<WidgetItem> additionalItems)
    {
        List<WidgetItem> members = NormalizeStackMembers(
            sourceStack.Members.Concat(additionalItems));
        if (members.Count < 2 || sourceStack.IsManual)
        {
            return sourceStack;
        }

        List<string> visibleOrder = GetCurrentDisplayUnitOrder();
        int insertionIndex = visibleOrder.IndexOf(sourceStack.StackKey);
        if (insertionIndex < 0)
        {
            insertionIndex = ResolveManualStackInsertionIndex(
                visibleOrder,
                members);
        }

        SetActiveFolderDisplayOrder(
            MergeWithRetainedNonVisibleOrderKeys(visibleOrder));
        HashSet<string> memberPaths = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveMemberOverrides(memberPaths);

        string manualKey =
            $"{ManualStackKeyPrefix}{Guid.NewGuid():N}";
        _stackMemberOverrides[manualKey] = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToList();
        _stackNameOverrides.Remove(sourceStack.StackKey);
        _stackNameOverrides[manualKey] = sourceStack.Name;
        _disabledStacks.Remove(sourceStack.StackKey);

        InsertStackIntoOrderRemovingMembers(
            manualKey,
            memberPaths,
            insertionIndex,
            replacedStackKey: sourceStack.StackKey);
        _expandedStackKey = sourceStack.IsExpanded
            ? manualKey
            : null;
        PersistStackCustomizations();
        return _stackItems.TryGetValue(
            manualKey,
            out WidgetStackItem? manualStack)
                ? manualStack
                : sourceStack;
    }

    public bool AddItemsToStack(
        string stackKey,
        IEnumerable<WidgetItem> draggedItems)
    {
        if (!UsesStackProjection ||
            string.IsNullOrWhiteSpace(stackKey) ||
            !_stackItems.TryGetValue(
                stackKey,
                out WidgetStackItem? targetStack))
        {
            return false;
        }

        HashSet<string> targetPaths = targetStack.Members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<WidgetItem> members = NormalizeStackMembers(
                draggedItems)
            .Where(item => !targetPaths.Contains(
                NormalizeStackMemberPath(item.Path)))
            .ToList();
        if (members.Count == 0)
        {
            return false;
        }

        if (!targetStack.IsManual)
        {
            ConvertStackToManual(targetStack, members);
            return true;
        }

        SetActiveFolderDisplayOrder(
            MergeWithRetainedNonVisibleOrderKeys(
                GetCurrentDisplayUnitOrder()));
        HashSet<string> memberPaths = members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RemoveMemberOverrides(memberPaths);
        if (!_stackMemberOverrides.TryGetValue(
                stackKey,
                out List<string>? forcedMembers))
        {
            forcedMembers = [];
            _stackMemberOverrides[stackKey] = forcedMembers;
        }

        foreach (string path in memberPaths)
        {
            if (!forcedMembers.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase))
            {
                forcedMembers.Add(path);
            }
        }

        HashSet<string> addedLooseKeys = memberPaths
            .Select(path => LooseItemOrderKeyPrefix + path.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        List<string> updatedOrder = GetActiveFolderDisplayOrder()
            .Where(key => !addedLooseKeys.Contains(key))
            .Where(key =>
                !key.StartsWith(
                    ManualStackKeyPrefix,
                    StringComparison.Ordinal) ||
                _stackMemberOverrides.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SetActiveFolderDisplayOrder(updatedOrder);
        PersistStackCustomizations();
        return true;
    }

    public bool CanRemoveItemFromStack(WidgetItem item) =>
        item.IsStackChild && FindContainingStack(item) is not null;

    public bool RemoveItemsFromStack(
        string stackKey,
        IEnumerable<WidgetItem> selectedItems)
    {
        if (!UsesStackProjection ||
            string.IsNullOrWhiteSpace(stackKey) ||
            !_stackItems.TryGetValue(
                stackKey,
                out WidgetStackItem? stack))
        {
            return false;
        }

        HashSet<string> stackPaths = stack.Members
            .Select(item => NormalizeStackMemberPath(item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> removedPaths = NormalizeStackMembers(selectedItems)
            .Select(item => NormalizeStackMemberPath(item.Path))
            .Where(stackPaths.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedPaths.Count == 0)
        {
            return false;
        }

        bool projectionWasEnabled = UsesStackProjection;
        int remainingMemberCount = stack.Members.Count - removedPaths.Count;
        if (remainingMemberCount < 2)
        {
            if (stack.IsManual)
            {
                List<string>? replacementOrder = null;
                if (_stackMemberOverrides.TryGetValue(
                        stack.StackKey,
                        out List<string>? storedMembers))
                {
                    replacementOrder = BuildDissolvedRemovalOrder(
                        storedMembers,
                        removedPaths);
                }

                RemoveManualStackCustomization(
                    stack.StackKey,
                    replacementOrder);
            }
            else
            {
                _disabledStacks.Add(stack.StackKey);
                _expandedStackKey = null;
            }

            PersistStackCustomizations();
            if (projectionWasEnabled != UsesStackProjection)
            {
                OnPropertyChanged(nameof(VisibleItems));
            }
            return true;
        }

        if (!stack.IsManual)
        {
            stack = ConvertStackToManual(stack, []);
        }

        if (!_stackMemberOverrides.TryGetValue(
                stack.StackKey,
                out List<string>? members))
        {
            return false;
        }

        List<string> detachedOrderedPaths = members
            .Where(path => removedPaths.Contains(NormalizeStackMemberPath(path)))
            .ToList();
        List<string> survivingOrderedPaths = members
            .Where(path => !removedPaths.Contains(NormalizeStackMemberPath(path)))
            .ToList();
        if (detachedOrderedPaths.Count == 0)
        {
            return false;
        }

        if (survivingOrderedPaths.Count < 2)
        {
            RemoveManualStackCustomization(
                stack.StackKey,
                survivingOrderedPaths.Concat(detachedOrderedPaths).ToList());
        }
        else
        {
            members.Clear();
            members.AddRange(survivingOrderedPaths);
            InsertDetachedMembersAfterStackInOrder(
                stack.StackKey,
                detachedOrderedPaths);
        }

        PersistStackCustomizations();
        if (projectionWasEnabled != UsesStackProjection)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }
        return true;
    }

    public bool RemoveItemFromStack(WidgetItem item)
    {
        WidgetStackItem? stack = FindContainingStack(item);
        if (stack is null)
        {
            return false;
        }

        if (!stack.IsManual)
        {
            if (stack.Members.Count <= 2)
            {
                _disabledStacks.Add(stack.StackKey);
                _expandedStackKey = null;
                PersistStackCustomizations();
                return true;
            }

            stack = ConvertStackToManual(stack, []);
        }

        if (!_stackMemberOverrides.TryGetValue(
                stack.StackKey,
                out List<string>? members))
        {
            return false;
        }

        bool projectionWasEnabled = UsesStackProjection;
        string itemPath = NormalizeStackMemberPath(item.Path);
        List<string> detachedOrderedPaths = members
            .Where(path => string.Equals(
                NormalizeStackMemberPath(path),
                itemPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (detachedOrderedPaths.Count == 0)
        {
            return false;
        }

        List<string> survivingOrderedPaths = members
            .Where(path => !string.Equals(
                NormalizeStackMemberPath(path),
                itemPath,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (survivingOrderedPaths.Count < 2)
        {
            RemoveManualStackCustomization(
                stack.StackKey,
                survivingOrderedPaths.Concat(detachedOrderedPaths).ToList());
        }
        else
        {
            members.Clear();
            members.AddRange(survivingOrderedPaths);
            InsertDetachedMembersAfterStackInOrder(
                stack.StackKey,
                detachedOrderedPaths);
        }

        PersistStackCustomizations();
        if (projectionWasEnabled != UsesStackProjection)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }
        return true;
    }

    public bool DissolveStack(WidgetStackItem stack)
    {
        if (!stack.IsManual)
        {
            SetStackDisabled(stack.StackKey, disabled: true);
            return true;
        }

        bool projectionWasEnabled = UsesStackProjection;
        if (!_stackMemberOverrides.ContainsKey(stack.StackKey))
        {
            return false;
        }

        RemoveManualStackCustomization(stack.StackKey);
        PersistStackCustomizations();
        if (projectionWasEnabled != UsesStackProjection)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }
        return true;
    }

    private WidgetStackItem? FindContainingStack(WidgetItem item) =>
        _stackDisplayItems.OfType<WidgetStackItem>().FirstOrDefault(stack =>
            stack.Members.Any(member => ReferenceEquals(member, item)));

    private bool ShouldPreserveLooseMemberOrderInStackOrder() =>
        EffectiveSortMode == WidgetSortMode.Manual ||
        GetActiveFolderDisplayOrder().Any(key =>
            key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal));

    private static List<string> BuildDissolvedRemovalOrder(
        IReadOnlyList<string> storedMembers,
        IReadOnlySet<string> removedPaths)
    {
        List<string> survivors = [];
        List<string> detached = [];
        foreach (string path in storedMembers)
        {
            if (removedPaths.Contains(NormalizeStackMemberPath(path)))
            {
                detached.Add(path);
            }
            else
            {
                survivors.Add(path);
            }
        }

        survivors.AddRange(detached);
        return survivors;
    }

    private void InsertStackIntoOrderRemovingMembers(
        string stackKey,
        IReadOnlySet<string> memberPaths,
        int insertionIndex,
        string? replacedStackKey = null)
    {
        List<string> order = GetActiveFolderDisplayOrder().ToList();
        HashSet<string> memberOrderKeys = memberPaths
            .Select(path => LooseItemOrderKeyPrefix + path.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(replacedStackKey))
        {
            int replacedIndex = order.IndexOf(replacedStackKey);
            if (replacedIndex >= 0)
            {
                insertionIndex = replacedIndex;
            }
        }

        for (int index = order.Count - 1; index >= 0; index--)
        {
            string key = order[index];
            bool shouldRemove =
                (!string.IsNullOrEmpty(replacedStackKey) &&
                 string.Equals(key, replacedStackKey, StringComparison.Ordinal)) ||
                (key.StartsWith(ManualStackKeyPrefix, StringComparison.Ordinal) &&
                 !_stackMemberOverrides.ContainsKey(key)) ||
                memberOrderKeys.Contains(key);
            if (!shouldRemove)
            {
                continue;
            }

            order.RemoveAt(index);
            if (index < insertionIndex)
            {
                insertionIndex--;
            }
        }

        int clampedIndex = Math.Clamp(insertionIndex, 0, order.Count);
        order.Insert(clampedIndex, stackKey);
        SetActiveFolderDisplayOrder(order);
    }

    private void InsertDetachedMembersAfterStackInOrder(
        string stackKey,
        IReadOnlyList<string> detachedMemberPaths)
    {
        if (detachedMemberPaths.Count == 0 ||
            !ShouldPreserveLooseMemberOrderInStackOrder())
        {
            return;
        }

        List<string> order = GetActiveFolderDisplayOrder().ToList();
        if (order.Count == 0 &&
            EffectiveSortMode == WidgetSortMode.Manual &&
            _hasBuiltStackDisplay)
        {
            order = GetCurrentDisplayUnitOrder();
        }

        if (!order.Contains(stackKey, StringComparer.Ordinal))
        {
            return;
        }

        List<string> detachedKeys = detachedMemberPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
                LooseItemOrderKeyPrefix +
                NormalizeStackMemberPath(path).ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (detachedKeys.Count == 0)
        {
            return;
        }

        HashSet<string> detachedSet = detachedKeys.ToHashSet(StringComparer.Ordinal);
        order.RemoveAll(detachedSet.Contains);
        int stackIndex = order.IndexOf(stackKey);
        if (stackIndex >= 0)
        {
            order.InsertRange(stackIndex + 1, detachedKeys);
        }

        SetActiveFolderDisplayOrder(order);
    }

    private void RemoveManualStackCustomization(
        string stackKey,
        IReadOnlyList<string>? explicitReplacementMemberPaths = null)
    {
        IReadOnlyList<string>? memberPaths = explicitReplacementMemberPaths;
        if (memberPaths is null &&
            _stackMemberOverrides.TryGetValue(stackKey, out List<string>? storedPaths))
        {
            memberPaths = storedPaths;
        }

        string? owningSubfolderKey = null;
        List<string>? targetOrder = null;
        bool preserveMemberOrderSlots = false;

        if (_stackOrder.Contains(stackKey, StringComparer.Ordinal))
        {
            targetOrder = _stackOrder;
            preserveMemberOrderSlots =
                memberPaths is { Count: > 0 } &&
                (Config.SortMode == WidgetSortMode.Manual ||
                 _stackOrder.Any(key =>
                     key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal)));
        }
        else
        {
            foreach ((string folderKey, List<string> subOrder) in _subfolderDisplayOrders)
            {
                if (subOrder.Contains(stackKey, StringComparer.Ordinal))
                {
                    owningSubfolderKey = folderKey;
                    targetOrder = subOrder.ToList();
                    preserveMemberOrderSlots =
                        memberPaths is { Count: > 0 } &&
                        ShouldPreserveLooseSlotsForSubfolder(folderKey, subOrder);
                    break;
                }
            }
        }

        if (targetOrder is null &&
            memberPaths is { Count: > 0 } &&
            EffectiveSortMode == WidgetSortMode.Manual &&
            _hasBuiltStackDisplay &&
            GetActiveFolderDisplayOrder().Count == 0)
        {
            List<string> liveUnits = GetCurrentDisplayUnitOrder();
            if (liveUnits.Contains(stackKey, StringComparer.Ordinal))
            {
                targetOrder = liveUnits;
                preserveMemberOrderSlots = true;
                if (TryGetCurrentSubfolderScopeKey(out string activeSubKey))
                {
                    owningSubfolderKey = activeSubKey;
                }
            }
        }

        _stackMemberOverrides.Remove(stackKey);
        _stackNameOverrides.Remove(stackKey);
        _disabledStacks.Remove(stackKey);
        if (targetOrder is not null)
        {
            int stackOrderIndex = targetOrder.IndexOf(stackKey);
            if (preserveMemberOrderSlots &&
                stackOrderIndex >= 0 &&
                memberPaths is not null)
            {
                List<string> memberOrderKeys = memberPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path =>
                        LooseItemOrderKeyPrefix +
                        NormalizeStackMemberPath(path).ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                HashSet<string> memberOrderKeySet =
                    memberOrderKeys.ToHashSet(StringComparer.Ordinal);
                for (int index = targetOrder.Count - 1; index >= 0; index--)
                {
                    if (memberOrderKeySet.Contains(targetOrder[index]))
                    {
                        targetOrder.RemoveAt(index);
                        if (index < stackOrderIndex)
                        {
                            stackOrderIndex--;
                        }
                    }
                }

                int currentStackIndex = targetOrder.IndexOf(stackKey);
                if (currentStackIndex >= 0)
                {
                    targetOrder.RemoveAt(currentStackIndex);
                    targetOrder.InsertRange(currentStackIndex, memberOrderKeys);
                }
                else
                {
                    int clampedIndex = Math.Clamp(stackOrderIndex, 0, targetOrder.Count);
                    targetOrder.InsertRange(clampedIndex, memberOrderKeys);
                }
            }
            else
            {
                targetOrder.RemoveAll(key => string.Equals(
                    key,
                    stackKey,
                    StringComparison.Ordinal));
            }

            List<string> deduplicated = targetOrder
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (owningSubfolderKey is not null)
            {
                if (deduplicated.Count == 0)
                {
                    _subfolderDisplayOrders.Remove(owningSubfolderKey);
                }
                else
                {
                    _subfolderDisplayOrders[owningSubfolderKey] = deduplicated;
                }
            }
            else
            {
                _stackOrder = deduplicated;
            }
        }

        if (string.Equals(
                _expandedStackKey,
                stackKey,
                StringComparison.Ordinal))
        {
            _expandedStackKey = null;
        }
    }

    private bool ShouldPreserveLooseSlotsForSubfolder(
        string folderKey,
        IReadOnlyList<string> subOrder)
    {
        if (_subfolderSortOverrides.TryGetValue(folderKey, out WidgetFolderSortOverride overrideState))
        {
            return overrideState.SortMode == WidgetSortMode.Manual;
        }

        return (_subfolderManualItemOrders.TryGetValue(folderKey, out List<string>? manualItems) &&
                manualItems.Count > 0) ||
               subOrder.Any(key => key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal));
    }

    public void MoveStackUp(string stackKey)
    {
        List<string> visibleOrder = GetCurrentDisplayUnitOrder();
        int idx = visibleOrder.IndexOf(stackKey);
        if (idx <= 0) return;
        (visibleOrder[idx - 1], visibleOrder[idx]) =
            (visibleOrder[idx], visibleOrder[idx - 1]);
        SetStackOrder(MergeWithRetainedNonVisibleOrderKeys(visibleOrder));
    }

    public bool CanMoveStackUp(string stackKey) =>
        GetCurrentDisplayUnitOrder().IndexOf(stackKey) > 0;

    public void MoveStackDown(string stackKey)
    {
        List<string> visibleOrder = GetCurrentDisplayUnitOrder();
        int idx = visibleOrder.IndexOf(stackKey);
        if (idx < 0 || idx >= visibleOrder.Count - 1) return;
        (visibleOrder[idx + 1], visibleOrder[idx]) =
            (visibleOrder[idx], visibleOrder[idx + 1]);
        SetStackOrder(MergeWithRetainedNonVisibleOrderKeys(visibleOrder));
    }

    public bool CanMoveStackDown(string stackKey)
    {
        List<string> order = GetCurrentDisplayUnitOrder();
        int index = order.IndexOf(stackKey);
        return index >= 0 && index < order.Count - 1;
    }

    public bool MoveStackForReorder(
        string stackKey,
        int visibleInsertionIndex)
    {
        if (!UsesStackProjection ||
            string.IsNullOrWhiteSpace(stackKey))
        {
            return false;
        }

        return MoveDisplayUnitForReorder(
            stackKey,
            visibleInsertionIndex);
    }

    private bool MoveDisplayUnitForReorder(
        string orderKey,
        int visibleInsertionIndex)
    {
        List<string> currentOrder = GetCurrentDisplayUnitOrder();
        if (!currentOrder.Contains(orderKey, StringComparer.Ordinal))
        {
            return false;
        }

        int desiredIndex = 0;
        int cappedIndex = Math.Clamp(
            visibleInsertionIndex,
            0,
            _stackDisplayItems.Count);
        for (int index = 0; index < cappedIndex; index++)
        {
            WidgetItem candidate = _stackDisplayItems[index];
            if (!IsTopLevelDisplayUnit(candidate))
            {
                continue;
            }

            string candidateKey = GetDisplayUnitOrderKey(candidate);
            if (!string.Equals(
                    candidateKey,
                    orderKey,
                    StringComparison.Ordinal))
            {
                desiredIndex++;
            }
        }

        List<string> reordered = currentOrder
            .Where(key => !string.Equals(
                key,
                orderKey,
                StringComparison.Ordinal))
            .ToList();
        desiredIndex = Math.Clamp(
            desiredIndex,
            0,
            reordered.Count);
        reordered.Insert(desiredIndex, orderKey);
        if (currentOrder.SequenceEqual(
                reordered,
                StringComparer.Ordinal))
        {
            return false;
        }

        if (orderKey.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal))
        {
            EnsureSubfolderManualSortOverrideOnLooseReorder();
        }

        SetActiveFolderDisplayOrder(
            MergeWithRetainedNonVisibleOrderKeys(reordered));
        PersistStackDisplayOrder();
        return true;
    }

    private List<string> MergeWithRetainedNonVisibleOrderKeys(
        IReadOnlyList<string> reorderedVisibleUnits)
    {
        IReadOnlyList<string> activeOrder = GetActiveFolderDisplayOrder();
        var result = new List<string>(reorderedVisibleUnits.Count + activeOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in reorderedVisibleUnits)
        {
            if (seen.Add(key))
            {
                result.Add(key);
            }
        }

        if (activeOrder.Count == 0)
        {
            return result;
        }

        HashSet<string> loadedItemOrderKeys = Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(GetLooseItemOrderKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string historicalKey in activeOrder)
        {
            if (string.IsNullOrWhiteSpace(historicalKey) ||
                seen.Contains(historicalKey))
            {
                continue;
            }

            if (historicalKey.StartsWith(ManualStackKeyPrefix, StringComparison.Ordinal) &&
                !_stackMemberOverrides.ContainsKey(historicalKey))
            {
                continue;
            }

            if (historicalKey.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal) &&
                loadedItemOrderKeys.Contains(historicalKey))
            {
                continue;
            }

            if (seen.Add(historicalKey))
            {
                result.Add(historicalKey);
            }
        }

        return result;
    }

    private IReadOnlyList<string> GetActiveFolderDisplayOrder()
    {
        if (TryGetCurrentSubfolderScopeKey(out string scopeKey))
        {
            return _subfolderDisplayOrders.TryGetValue(scopeKey, out List<string>? order)
                ? order
                : [];
        }

        return _stackOrder;
    }

    private void SetActiveFolderDisplayOrder(IEnumerable<string> order)
    {
        List<string> normalized = order
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (TryGetCurrentSubfolderScopeKey(out string scopeKey))
        {
            if (normalized.Count == 0)
            {
                _subfolderDisplayOrders.Remove(scopeKey);
            }
            else
            {
                _subfolderDisplayOrders[scopeKey] = normalized;
            }

            return;
        }

        _stackOrder = normalized;
    }

    private void PersistActiveFolderDisplayOrderToMetadata()
    {
        WidgetFileStackSettings.SetStackOrder(
            Config,
            _stackOrder.Count == 0 ? null : _stackOrder);
        WidgetFileStackSettings.SetSubfolderDisplayOrders(
            Config,
            _subfolderDisplayOrders);
    }

    private void EnsureSubfolderManualSortOverrideOnLooseReorder()
    {
        if (!TryGetCurrentSubfolderScopeKey(out string scopeKey))
        {
            return;
        }

        if (_subfolderSortOverrides.TryGetValue(scopeKey, out WidgetFolderSortOverride existing) &&
            existing.SortMode == WidgetSortMode.Manual)
        {
            return;
        }

        _subfolderSortOverrides[scopeKey] = new WidgetFolderSortOverride(
            WidgetSortMode.Manual,
            false);
        WidgetFileStackSettings.SetSubfolderSortOverrides(
            Config,
            _subfolderSortOverrides);
        NotifyFolderSortPropertiesChanged();
    }

    internal void SyncFlatReorderWithDormantStackDisplayOrder()
    {
        IReadOnlyList<string> activeOrder = GetActiveFolderDisplayOrder();
        if (!activeOrder.Any(key =>
                key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal)))
        {
            return;
        }

        var pathToManualStackKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string stackKey, List<string> memberPaths) in _stackMemberOverrides)
        {
            if (!stackKey.StartsWith(ManualStackKeyPrefix, StringComparison.Ordinal) ||
                memberPaths.Count < 2)
            {
                continue;
            }

            foreach (string memberPath in memberPaths)
            {
                string normalizedPath = NormalizeStackMemberPath(memberPath);
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                {
                    pathToManualStackKey.TryAdd(normalizedPath, stackKey);
                }
            }
        }

        var rebuilt = new List<string>(Items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (WidgetItem item in Items)
        {
            string normalizedPath = NormalizeStackMemberPath(item.Path);
            if (pathToManualStackKey.TryGetValue(normalizedPath, out string? manualStackKey))
            {
                if (seen.Add(manualStackKey))
                {
                    rebuilt.Add(manualStackKey);
                }

                continue;
            }

            string looseKey = GetLooseItemOrderKey(item);
            if (seen.Add(looseKey))
            {
                rebuilt.Add(looseKey);
            }
        }

        HashSet<string> loadedItemKeys = Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(GetLooseItemOrderKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string historicalKey in activeOrder)
        {
            if (seen.Contains(historicalKey))
            {
                continue;
            }

            if (historicalKey.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal) &&
                loadedItemKeys.Contains(historicalKey))
            {
                continue;
            }

            if (historicalKey.StartsWith(ManualStackKeyPrefix, StringComparison.Ordinal) &&
                !_stackMemberOverrides.ContainsKey(historicalKey))
            {
                continue;
            }

            if (seen.Add(historicalKey))
            {
                rebuilt.Add(historicalKey);
            }
        }

        SetActiveFolderDisplayOrder(rebuilt);
        PersistActiveFolderDisplayOrderToMetadata();
    }

    private static bool WillTopLevelDisplayUnitRemainAfterDetach(
        WidgetItem candidate,
        IReadOnlySet<string> movingKeys)
    {
        if (!IsTopLevelDisplayUnit(candidate))
        {
            return false;
        }

        if (candidate is WidgetStackItem stack)
        {
            return !movingKeys.Contains(stack.StackKey) &&
                stack.Members.Any(member =>
                    !movingKeys.Contains(GetLooseItemOrderKey(member)));
        }

        return !movingKeys.Contains(GetDisplayUnitOrderKey(candidate));
    }

    private List<string> GetCurrentDisplayUnitOrder()
    {
        return _stackDisplayItems
            .Where(IsTopLevelDisplayUnit)
            .Select(GetDisplayUnitOrderKey)
            .ToList();
    }

    private static bool IsTopLevelDisplayUnit(
        WidgetItem item) =>
        item is WidgetStackItem || !item.IsStackChild;

    private static string GetDisplayUnitOrderKey(
        WidgetItem item) =>
        item is WidgetStackItem stack
            ? stack.StackKey
            : GetLooseItemOrderKey(item);

    private string? ResolveVisibleDisplayUnitOrderKey(
        WidgetItem item)
    {
        if (item is WidgetStackItem stack)
        {
            return stack.StackKey;
        }

        if (item.IsStackChild)
        {
            return FindContainingStack(item)?.StackKey;
        }

        return GetLooseItemOrderKey(item);
    }

    private string? ResolveDisplayUnitOrderKey(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        WidgetItem? item = Items.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Path,
                path,
                StringComparison.OrdinalIgnoreCase));
        return item is null
            ? null
            : ResolveVisibleDisplayUnitOrderKey(item);
    }

    private static string GetLooseItemOrderKey(WidgetItem item)
    {
        string path;
        try
        {
            path = Path.GetFullPath(item.Path);
        }
        catch (Exception)
        {
            path = item.Path;
        }

        return LooseItemOrderKeyPrefix + path.ToUpperInvariant();
    }

    private void PersistStackDisplayOrder()
    {
        PersistActiveFolderDisplayOrderToMetadata();
        WidgetFileStackSettings.SetSubfolderSortOverrides(
            Config,
            _subfolderSortOverrides);
        _settingsService.UpdateWidget(
            Config,
            notifySubscribers: false);
        _settingsService.SaveDebounced(
            notifySubscribers: false);
        RebuildStackDisplayItems();
    }

    private void PersistStackCustomizations()
    {
        WidgetFileStackSettings.SetDisabledStacks(
            Config,
            _disabledStacks);
        WidgetFileStackSettings.SetStackNameOverrides(
            Config,
            _stackNameOverrides);
        WidgetFileStackSettings.SetStackMemberOverrides(
            Config,
            _stackMemberOverrides);
        PersistActiveFolderDisplayOrderToMetadata();
        WidgetFileStackSettings.SetSubfolderSortOverrides(
            Config,
            _subfolderSortOverrides);
        WidgetFileStackSettings.SetSubfolderManualItemOrders(
            Config,
            _subfolderManualItemOrders);
        _settingsService.UpdateWidget(
            Config,
            notifySubscribers: false);
        _settingsService.SaveDebounced(
            notifySubscribers: false);
        RebuildStackDisplayItems();
    }

    private void PersistStackOverrides()
    {
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
        ApplyStackSettings();
        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOpenModeFollowsGlobal));
    }

    private void InitializeStacks()
    {
        _fileStacksEnabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        _fileStackGroupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        _fileStackThreshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        _fileStackOrderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        _fileStackOpenMode = WidgetFileStackSettings.ResolveOpenMode(
            Config,
            _settingsService.Settings.FileStackOpenMode);
        _disabledStacks = WidgetFileStackSettings.GetDisabledStacks(Config);
        _stackNameOverrides = WidgetFileStackSettings.GetStackNameOverrides(Config);
        _stackOrder = WidgetFileStackSettings.GetStackOrder(Config);
        _stackMemberOverrides =
            WidgetFileStackSettings.GetStackMemberOverrides(Config);
        _subfolderSortOverrides =
            WidgetFileStackSettings.GetSubfolderSortOverrides(Config);
        _subfolderDisplayOrders =
            WidgetFileStackSettings.GetSubfolderDisplayOrders(Config);
        _subfolderManualItemOrders =
            WidgetFileStackSettings.GetSubfolderManualItemOrders(Config);
        MigrateLegacySubfolderOrdersFromRoot();
        Items.CollectionChanged += StackSourceItems_CollectionChanged;
        ScheduleStackDateBoundaryRefresh();
        QueueStackDisplayRebuild();
    }

    private void CleanupStacks()
    {
        Items.CollectionChanged -= StackSourceItems_CollectionChanged;
        if (_stackDateBoundaryTimer is not null)
        {
            _stackDateBoundaryTimer.Stop();
            _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;
            _stackDateBoundaryTimer = null;
        }
    }

    private void StackSourceItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSynchronizingItemsFromStackProjection)
        {
            return;
        }

        QueueStackDisplayRebuild();
    }

    private StackRebuildDeferralScope DeferStackDisplayRebuild()
    {
        _deferredStackRebuildDepth++;
        return new StackRebuildDeferralScope(this);
    }

    private void EndDeferredStackDisplayRebuild()
    {
        if (_deferredStackRebuildDepth <= 0 || --_deferredStackRebuildDepth > 0)
        {
            return;
        }

        if (_deferredStackRebuildPending)
        {
            _deferredStackRebuildPending = false;
            QueueStackDisplayRebuild();
        }
    }

    private readonly struct StackRebuildDeferralScope(WidgetViewModel owner) : IDisposable
    {
        public void Dispose() => owner.EndDeferredStackDisplayRebuild();
    }

    private void QueueStackDisplayRebuild()
    {
        _hasBuiltStackDisplay = false;
        if (_deferredStackRebuildDepth > 0)
        {
            _deferredStackRebuildPending = true;
            return;
        }

        if (_stackRebuildQueued)
        {
            return;
        }

        if (_itemMutationBatchDepth > 0)
        {
            // A bulk import defers the rebuild to the batch finalization;
            // per-insert rebuilds would regroup the whole display list once
            // per dispatcher pass. The staleness flag above stays set so
            // mid-batch readers know the projection is behind.
            MarkItemMutationBatchDirty();
            return;
        }

        if (_dispatcherQueue is null)
        {
            RebuildStackDisplayItems();
            return;
        }

        _stackRebuildQueued = true;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (_stackRebuildQueued)
            {
                RebuildStackDisplayItems();
            }
        }))
        {
            _stackRebuildQueued = false;
        }
    }

    private void ApplyStackSettings()
    {
        bool projectionWasEnabled = UsesStackProjection;
        bool enabled = WidgetFileStackSettings.ResolveEnabled(
            Config,
            _settingsService.Settings.FileStacksEnabled);
        string groupBy = WidgetFileStackSettings.ResolveGroupBy(
            Config,
            _settingsService.Settings.FileStackGroupBy);
        int threshold = WidgetFileStackSettings.ResolveThreshold(
            Config,
            _settingsService.Settings.FileStackThreshold);
        string orderBy = WidgetFileStackSettings.ResolveOrderBy(
            Config,
            _settingsService.Settings.FileStackOrderBy);
        string openMode = WidgetFileStackSettings.ResolveOpenMode(
            Config,
            _settingsService.Settings.FileStackOpenMode);
        var disabledStacks = WidgetFileStackSettings.GetDisabledStacks(Config);
        var nameOverrides = WidgetFileStackSettings.GetStackNameOverrides(Config);
        var stackOrder = WidgetFileStackSettings.GetStackOrder(Config);
        var stackMemberOverrides =
            WidgetFileStackSettings.GetStackMemberOverrides(Config);
        var subfolderSortOverrides =
            WidgetFileStackSettings.GetSubfolderSortOverrides(Config);
        var subfolderDisplayOrders =
            WidgetFileStackSettings.GetSubfolderDisplayOrders(Config);
        var subfolderManualItemOrders =
            WidgetFileStackSettings.GetSubfolderManualItemOrders(Config);

        if (projectionWasEnabled && !enabled)
        {
            // A disabled projection exposes the raw Items collection. Flatten
            // the currently visible stack order first so switching the mode
            // does not suddenly reveal an older, unrelated Items order.
            SynchronizeItemsOrderFromStackProjection();
            subfolderManualItemOrders = new Dictionary<string, List<string>>(
                _subfolderManualItemOrders,
                StringComparer.OrdinalIgnoreCase);
        }

        FileStacksEnabled = enabled;
        FileStackGroupBy = groupBy;
        FileStackThreshold = threshold;
        FileStackOrderBy = orderBy;
        FileStackOpenMode = openMode;
        _disabledStacks = disabledStacks;
        _stackNameOverrides = nameOverrides;
        _stackOrder = stackOrder;
        _stackMemberOverrides = stackMemberOverrides;
        _subfolderSortOverrides = subfolderSortOverrides;
        _subfolderDisplayOrders = subfolderDisplayOrders;
        _subfolderManualItemOrders = subfolderManualItemOrders;
        MigrateLegacySubfolderOrdersFromRoot();
        if (UsesStackPopover)
        {
            _expandedStackKey = null;
        }
        bool sourceChanged = projectionWasEnabled != UsesStackProjection;
        if (!UsesStackProjection)
        {
            _expandedStackKey = null;
        }

        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
        if (sourceChanged)
        {
            OnPropertyChanged(nameof(VisibleItems));
        }

        OnPropertyChanged(nameof(FileStacksFollowGlobalDefaults));
        OnPropertyChanged(nameof(FileStacksEnabledFollowsGlobal));
        OnPropertyChanged(nameof(FileStackGroupByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackThresholdFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOrderByFollowsGlobal));
        OnPropertyChanged(nameof(FileStackOpenModeFollowsGlobal));
        OnPropertyChanged(nameof(HasDisabledStacks));
    }

    private void SynchronizeItemsOrderFromStackProjection()
    {
        if (_stackDisplayItems.Count == 0)
        {
            return;
        }

        // Automatic sort modes remain governed by their selected comparator;
        // only Manual mode needs the visual stack order flattened into the
        // underlying collection.
        if (EffectiveSortMode != WidgetSortMode.Manual)
        {
            SortItems();
            return;
        }

        var desired = new List<WidgetItem>(Items.Count);
        var included = new HashSet<WidgetItem>();
        foreach (WidgetItem projected in _stackDisplayItems)
        {
            if (projected is WidgetStackItem stack)
            {
                foreach (WidgetItem member in stack.Members)
                {
                    if (Items.Contains(member) && included.Add(member))
                    {
                        desired.Add(member);
                    }
                }

                continue;
            }

            if (!projected.IsStackChild &&
                Items.Contains(projected) &&
                included.Add(projected))
            {
                desired.Add(projected);
            }
        }

        // A provider or watcher can add an item between projection rebuilds.
        // Preserve such items instead of dropping them during normalization.
        foreach (WidgetItem item in Items)
        {
            if (included.Add(item))
            {
                desired.Add(item);
            }
        }

        _isSynchronizingItemsFromStackProjection = true;
        try
        {
            for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                int currentIndex = Items.IndexOf(desired[targetIndex]);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    Items.Move(currentIndex, targetIndex);
                }
            }

            NormalizeSortOrder();
        }
        finally
        {
            _isSynchronizingItemsFromStackProjection = false;
        }

        PersistManualOrderSnapshotIfChanged();
    }

    private void RebuildStackDisplayItems()
    {
        // A synchronous rebuild also consumes any queued source changes, so the
        // dispatcher callback does not repeat the same work after a group switch.
        _stackRebuildQueued = false;
        _hasBuiltStackDisplay = false;
        PerformanceLogger.RecordStackProjectionRebuild();
        foreach (var item in Items)
        {
            item.IsStackChild = false;
        }

        if (!UsesStackProjection)
        {
            _stackDisplayItems.Clear();
            _hasBuiltStackDisplay = true;
            return;
        }

        IReadOnlyList<WidgetStackGroup> automaticGroups =
            FileStacksEnabled && _settingsService.Settings.FileStackAutoStacking
            ? WidgetStackGroupingService.Group(
                Items,
                FileStackGroupBy,
                orderBy: FileStackOrderBy,
                customRules: _settingsService.Settings.FileStackCustomRules,
                unmatchedBehavior:
                    _settingsService.Settings.FileStackUnmatchedBehavior)
            : Items.Select((item, index) => new WidgetStackGroup(
                    WidgetStackCategory.Other,
                    [item],
                    $"Loose:{index}:{item.Path}",
                    CanStack: false))
                .ToList();
        IReadOnlyList<WidgetStackGroup> groups =
            ApplyStackMemberOverrides(automaticGroups);
        if (_expandedStackKey is not null &&
            !groups.Any(group =>
                ShouldProjectAsStack(group) &&
                group.EffectiveKey == _expandedStackKey))
        {
            _expandedStackKey = null;
        }

        var projected = new List<WidgetItem>();
        foreach (StackDisplayUnit unit in
                 OrderDisplayUnits(BuildDisplayUnits(groups)))
        {
            if (unit.LooseItem is { } looseItem)
            {
                projected.Add(looseItem);
                continue;
            }

            WidgetStackGroup group = unit.StackGroup!;
            string key = group.EffectiveKey;
            bool expanded = string.Equals(
                key,
                _expandedStackKey,
                StringComparison.Ordinal);
            projected.Add(CreateStackItem(group, expanded));
            if (!expanded)
            {
                continue;
            }

            foreach (WidgetItem item in group.Items)
            {
                item.IsStackChild = true;
                projected.Add(item);
            }
        }

        ReconcileStackDisplayItems(projected);
        HashSet<string> activeStackKeys = projected
            .OfType<WidgetStackItem>()
            .Select(stack => stack.StackKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string staleKey in _stackItems.Keys
                     .Where(key => !activeStackKeys.Contains(key))
                     .ToArray())
        {
            _stackItems.Remove(staleKey);
        }

        QueueLegacyAutomaticStackMigration(projected);
        _hasBuiltStackDisplay = true;
        if (EffectiveSortMode == WidgetSortMode.Manual &&
            Items.Count > 0)
        {
            SynchronizeItemsOrderFromStackProjection();
        }
    }

    private void QueueLegacyAutomaticStackMigration(
        IReadOnlyList<WidgetItem> projected)
    {
        if (_legacyStackMigrationQueued ||
            projected.OfType<WidgetStackItem>().FirstOrDefault(stack =>
                !stack.IsManual &&
                _stackMemberOverrides.ContainsKey(stack.StackKey)) is
                not { } legacyStack)
        {
            return;
        }

        string stackKey = legacyStack.StackKey;
        if (_dispatcherQueue is null)
        {
            _legacyStackMigrationQueued = true;
            try
            {
                if (_stackItems.TryGetValue(
                        stackKey,
                        out WidgetStackItem? current) &&
                    !current.IsManual &&
                    _stackMemberOverrides.ContainsKey(stackKey))
                {
                    ConvertStackToManual(current, []);
                }
            }
            finally
            {
                _legacyStackMigrationQueued = false;
            }

            return;
        }

        _legacyStackMigrationQueued = true;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            _legacyStackMigrationQueued = false;
            if (_stackItems.TryGetValue(
                    stackKey,
                    out WidgetStackItem? current) &&
                !current.IsManual &&
                _stackMemberOverrides.ContainsKey(stackKey))
            {
                ConvertStackToManual(current, []);
            }
        }))
        {
            _legacyStackMigrationQueued = false;
        }
    }

    private List<StackDisplayUnit> BuildDisplayUnits(
        IReadOnlyList<WidgetStackGroup> groups)
    {
        var units = new List<StackDisplayUnit>();
        foreach (WidgetStackGroup group in groups)
        {
            bool useStack = ShouldProjectAsStack(group);
            if (useStack)
            {
                units.Add(new StackDisplayUnit(
                    group.EffectiveKey,
                    group,
                    null));
                continue;
            }

            units.AddRange(group.Items.Select(item =>
                new StackDisplayUnit(
                    GetLooseItemOrderKey(item),
                    null,
                    item)));
        }

        return units;
    }

    private bool ShouldProjectAsStack(
        WidgetStackGroup group)
    {
        int minimumCount = group.ForceStack
            ? 2
            : FileStackThreshold;
        return group.CanStack &&
            !_disabledStacks.Contains(group.EffectiveKey) &&
            group.Items.Count >= minimumCount;
    }

    private IReadOnlyList<WidgetStackGroup>
        ApplyStackMemberOverrides(
            IReadOnlyList<WidgetStackGroup> automaticGroups)
    {
        if (_stackMemberOverrides.Count == 0)
        {
            return automaticGroups;
        }

        Dictionary<string, WidgetItem> itemsByPath = Items
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(
                item => NormalizeStackMemberPath(item.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var assignedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var manualStackKeyByMemberPath =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        var forcedByStack =
            new Dictionary<string, List<WidgetItem>>(
                StringComparer.Ordinal);
        foreach ((string stackKey, List<string> paths) in
                 _stackMemberOverrides)
        {
            bool isManual = stackKey.StartsWith(
                ManualStackKeyPrefix,
                StringComparison.Ordinal);
            var members = new List<WidgetItem>();
            foreach (string path in paths)
            {
                string normalizedPath =
                    NormalizeStackMemberPath(path);
                if (assignedPaths.Add(normalizedPath) &&
                    itemsByPath.TryGetValue(
                        normalizedPath,
                        out WidgetItem? item))
                {
                    members.Add(item);
                    if (isManual)
                    {
                        manualStackKeyByMemberPath[normalizedPath] = stackKey;
                    }
                }
            }

            if (members.Count > 0)
            {
                forcedByStack[stackKey] = members;
            }
        }

        var result = new List<WidgetStackGroup>();
        foreach (WidgetStackGroup group in automaticGroups)
        {
            var referencedManualStackKeys = new List<string>();
            foreach (WidgetItem candidate in group.Items)
            {
                if (manualStackKeyByMemberPath.TryGetValue(
                        NormalizeStackMemberPath(candidate.Path),
                        out string? manualStackKey) &&
                    forcedByStack.ContainsKey(manualStackKey) &&
                    !referencedManualStackKeys.Contains(
                        manualStackKey,
                        StringComparer.Ordinal))
                {
                    referencedManualStackKeys.Add(manualStackKey);
                }
            }

            List<WidgetItem> members = group.Items
                .Where(item => !assignedPaths.Contains(
                    NormalizeStackMemberPath(item.Path)))
                .ToList();
            bool hasForcedMembers =
                forcedByStack.Remove(
                    group.EffectiveKey,
                    out List<WidgetItem>? forcedMembers);
            if (hasForcedMembers &&
                forcedMembers is not null)
            {
                members.AddRange(forcedMembers);
            }

            if (members.Count > 0)
            {
                result.Add(group with
                {
                    Items = members,
                    ForceStack = hasForcedMembers
                });
            }

            foreach (string manualStackKey in referencedManualStackKeys)
            {
                if (forcedByStack.Remove(
                        manualStackKey,
                        out List<WidgetItem>? manualMembers) &&
                    manualMembers is not null)
                {
                    result.Add(CreateForcedStackGroup(
                        manualStackKey,
                        manualMembers));
                }
            }
        }

        foreach ((string stackKey, List<WidgetItem> members) in
                 forcedByStack)
        {
            result.Add(CreateForcedStackGroup(
                stackKey,
                members));
        }

        return result;
    }

    private WidgetStackGroup CreateForcedStackGroup(
        string stackKey,
        List<WidgetItem> members)
    {
        bool manual = stackKey.StartsWith(
            ManualStackKeyPrefix,
            StringComparison.Ordinal);
        WidgetStackCategory category =
            !manual &&
            Enum.TryParse(
                stackKey,
                ignoreCase: false,
                out WidgetStackCategory parsedCategory)
                ? parsedCategory
                : WidgetStackCategory.Other;
        return new WidgetStackGroup(
            category,
            members,
            stackKey,
            manual
                ? _localizationService.T(
                    "Widget.Stack.ManualDefaultName")
                : null,
            CanStack: true,
            ForceStack: true);
    }

    private List<WidgetItem> NormalizeStackMembers(
        IEnumerable<WidgetItem> items)
    {
        return items
            .Where(item =>
                item is not WidgetStackItem &&
                Items.Contains(item) &&
                !string.IsNullOrWhiteSpace(item.Path))
            .DistinctBy(
                item => NormalizeStackMemberPath(item.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int ResolveManualStackInsertionIndex(
        List<string> currentOrder,
        IReadOnlyList<WidgetItem> members)
    {
        int insertionIndex = currentOrder.Count;
        foreach (WidgetItem member in members)
        {
            string unitKey = GetLooseItemOrderKey(member);
            int index = currentOrder
                .IndexOf(unitKey);
            if (index >= 0)
            {
                insertionIndex = Math.Min(
                    insertionIndex,
                    index);
                continue;
            }

            WidgetStackItem? containingStack =
                _stackItems.Values.FirstOrDefault(stack =>
                    stack.Members.Any(candidate =>
                        ReferenceEquals(candidate, member)));
            if (containingStack is not null)
            {
                index = currentOrder.IndexOf(
                    containingStack.StackKey);
                if (index >= 0)
                {
                    bool anySurvivor = containingStack.Members.Any(candidate =>
                        !members.Any(selected =>
                            ReferenceEquals(selected, candidate)));
                    insertionIndex = Math.Min(
                        insertionIndex,
                        anySurvivor ? index + 1 : index);
                }
            }
        }

        return insertionIndex;
    }

    private void RemoveMemberOverrides(
        IReadOnlySet<string> paths)
    {
        foreach (string stackKey in
                 _stackMemberOverrides.Keys.ToArray())
        {
            _stackMemberOverrides[stackKey].RemoveAll(path =>
                paths.Contains(
                    NormalizeStackMemberPath(path)));
            if (_stackMemberOverrides[stackKey].Count == 0)
            {
                _stackMemberOverrides.Remove(stackKey);
            }
            else if (stackKey.StartsWith(
                         ManualStackKeyPrefix,
                         StringComparison.Ordinal) &&
                     _stackMemberOverrides[stackKey].Count < 2)
            {
                RemoveManualStackCustomization(stackKey);
            }
        }
    }

    private string[] DetachImportedRootInsertionStackMembership(
        IEnumerable<string> paths)
    {
        HashSet<string> normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeStackMemberPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Count == 0 || _stackMemberOverrides.Count == 0)
        {
            return [];
        }

        string[] detachedStackKeys = _stackMemberOverrides
            .Where(entry => entry.Value.Any(path => normalizedPaths.Contains(
                NormalizeStackMemberPath(path))))
            .Select(entry => entry.Key)
            .ToArray();
        if (detachedStackKeys.Length == 0)
        {
            return [];
        }

        RemoveMemberOverrides(normalizedPaths);
        return detachedStackKeys;
    }

    internal void UpdateStackMemberOverridePath(
        string sourcePath,
        string destinationPath)
    {
        string normalizedSource =
            NormalizeStackMemberPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDestination =
            NormalizeStackMemberPath(destinationPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedSource) ||
            string.IsNullOrWhiteSpace(normalizedDestination))
        {
            return;
        }

        bool changed = false;
        foreach (List<string> paths in
                 _stackMemberOverrides.Values)
        {
            for (int index = 0; index < paths.Count; index++)
            {
                string rebased = WidgetPathRebaseHelper.RebasePath(
                    paths[index],
                    normalizedSource,
                    normalizedDestination);
                if (!string.Equals(paths[index], rebased, StringComparison.Ordinal))
                {
                    paths[index] = rebased;
                    changed = true;
                }
            }
        }

        if (RebaseLooseOrderKeyListInPlace(_stackOrder, normalizedSource, normalizedDestination))
        {
            changed = true;
        }

        var rebasedSubfolderSorts = new Dictionary<string, WidgetFolderSortOverride>(
            StringComparer.OrdinalIgnoreCase);
        bool subfolderSortsChanged = false;
        foreach ((string folderKey, WidgetFolderSortOverride sortState) in _subfolderSortOverrides)
        {
            string rebasedKey = WidgetFileStackSettings.NormalizeFolderScopeKey(
                WidgetPathRebaseHelper.RebasePath(folderKey, normalizedSource, normalizedDestination));
            if (!string.Equals(folderKey, rebasedKey, StringComparison.Ordinal))
            {
                subfolderSortsChanged = true;
            }

            rebasedSubfolderSorts[rebasedKey] = sortState;
        }

        if (subfolderSortsChanged)
        {
            _subfolderSortOverrides = rebasedSubfolderSorts;
            changed = true;
        }

        var rebasedSubfolderDisplayOrders = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        bool subfolderDisplayOrdersChanged = false;
        foreach ((string folderKey, List<string> subOrder) in _subfolderDisplayOrders)
        {
            string rebasedKey = WidgetFileStackSettings.NormalizeFolderScopeKey(
                WidgetPathRebaseHelper.RebasePath(folderKey, normalizedSource, normalizedDestination));
            List<string> copy = subOrder.ToList();
            bool listChanged = RebaseLooseOrderKeyListInPlace(
                copy,
                normalizedSource,
                normalizedDestination);
            if (!string.Equals(folderKey, rebasedKey, StringComparison.Ordinal) || listChanged)
            {
                subfolderDisplayOrdersChanged = true;
            }

            rebasedSubfolderDisplayOrders[rebasedKey] = copy;
        }

        if (subfolderDisplayOrdersChanged)
        {
            _subfolderDisplayOrders = rebasedSubfolderDisplayOrders;
            changed = true;
        }

        var rebasedSubfolderItemOrders = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        bool subfolderItemOrdersChanged = false;
        foreach ((string folderKey, List<string> itemPaths) in _subfolderManualItemOrders)
        {
            string rebasedKey = WidgetFileStackSettings.NormalizeFolderScopeKey(
                WidgetPathRebaseHelper.RebasePath(folderKey, normalizedSource, normalizedDestination));
            var rebasedPaths = new List<string>(itemPaths.Count);
            bool listChanged = false;
            foreach (string itemPath in itemPaths)
            {
                string rebasedPath = WidgetPathRebaseHelper.RebasePath(
                    itemPath,
                    normalizedSource,
                    normalizedDestination);
                if (!string.Equals(itemPath, rebasedPath, StringComparison.Ordinal))
                {
                    listChanged = true;
                }

                rebasedPaths.Add(rebasedPath);
            }

            if (!string.Equals(folderKey, rebasedKey, StringComparison.Ordinal) || listChanged)
            {
                subfolderItemOrdersChanged = true;
            }

            rebasedSubfolderItemOrders[rebasedKey] = rebasedPaths;
        }

        if (subfolderItemOrdersChanged)
        {
            _subfolderManualItemOrders = rebasedSubfolderItemOrders;
            changed = true;
        }

        if (changed)
        {
            PersistStackCustomizations();
        }
    }

    private static bool RebaseLooseOrderKeyListInPlace(
        List<string> orderKeys,
        string oldRoot,
        string newRoot)
    {
        bool changed = false;
        for (int index = 0; index < orderKeys.Count; index++)
        {
            string key = orderKeys[index];
            if (!key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string path = key[LooseItemOrderKeyPrefix.Length..];
            string rebasedPath = WidgetPathRebaseHelper.RebasePath(path, oldRoot, newRoot);
            if (!string.Equals(path, rebasedPath, StringComparison.OrdinalIgnoreCase))
            {
                orderKeys[index] = LooseItemOrderKeyPrefix + rebasedPath.ToUpperInvariant();
                changed = true;
            }
        }

        return changed;
    }

    private void MigrateLegacySubfolderOrdersFromRoot()
    {
        string? rootCandidate = _mappedFolderTraversalPath ?? MappedFolderPath ?? Config.MappedFolderPath;
        if (string.IsNullOrWhiteSpace(rootCandidate) || _stackOrder.Count == 0)
        {
            return;
        }

        string rootFullPath;
        try
        {
            rootFullPath = Path.GetFullPath(rootCandidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return;
        }

        string rootPrefix = rootFullPath + Path.DirectorySeparatorChar;
        var remainingRootOrder = new List<string>(_stackOrder.Count);
        foreach (string key in _stackOrder)
        {
            string? ownerDirectory = ResolveOrderKeyParentDirectory(key);
            if (!string.IsNullOrWhiteSpace(ownerDirectory) &&
                ownerDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string subScopeKey = WidgetFileStackSettings.NormalizeFolderScopeKey(ownerDirectory);
                if (!string.IsNullOrWhiteSpace(subScopeKey))
                {
                    if (!_subfolderDisplayOrders.TryGetValue(subScopeKey, out List<string>? subOrder))
                    {
                        subOrder = [];
                        _subfolderDisplayOrders[subScopeKey] = subOrder;
                    }

                    if (!subOrder.Contains(key, StringComparer.Ordinal))
                    {
                        subOrder.Add(key);
                    }

                    continue;
                }
            }

            remainingRootOrder.Add(key);
        }

        _stackOrder = remainingRootOrder;
    }

    private string? ResolveOrderKeyParentDirectory(string key)
    {
        string? itemPath = null;
        if (key.StartsWith(LooseItemOrderKeyPrefix, StringComparison.Ordinal))
        {
            itemPath = key[LooseItemOrderKeyPrefix.Length..];
        }
        else if (key.StartsWith(ManualStackKeyPrefix, StringComparison.Ordinal) &&
                 _stackMemberOverrides.TryGetValue(key, out List<string>? members) &&
                 members.Count > 0)
        {
            itemPath = members[0];
        }

        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(itemPath))
                ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private void RemoveStackMemberOverridePaths(
        IEnumerable<string> paths)
    {
        HashSet<string> normalizedPaths = paths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeStackMemberPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Count == 0)
        {
            return;
        }

        bool projectionWasEnabled = UsesStackProjection;
        int entryCount = _stackMemberOverrides.Count;
        int pathCount = _stackMemberOverrides.Values
            .Sum(value => value.Count);
        RemoveMemberOverrides(normalizedPaths);
        if (entryCount != _stackMemberOverrides.Count ||
            pathCount != _stackMemberOverrides.Values
                .Sum(value => value.Count))
        {
            PersistStackCustomizations();
            if (projectionWasEnabled != UsesStackProjection)
            {
                OnPropertyChanged(nameof(VisibleItems));
            }
        }
    }

    private static string NormalizeStackMemberPath(
        string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private List<StackDisplayUnit> OrderDisplayUnits(
        IReadOnlyList<StackDisplayUnit> units)
    {
        IReadOnlyList<string> activeOrder = GetActiveFolderDisplayOrder();
        if (activeOrder.Count == 0)
        {
            return units.ToList();
        }

        var ordered = new List<StackDisplayUnit>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in activeOrder)
        {
            StackDisplayUnit? unit = units.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.OrderKey,
                    key,
                    StringComparison.Ordinal));
            if (unit is not null && known.Add(unit.OrderKey))
            {
                ordered.Add(unit);
            }
        }

        foreach (StackDisplayUnit unit in units)
        {
            if (known.Add(unit.OrderKey))
            {
                ordered.Add(unit);
            }
        }

        return ordered;
    }

    private WidgetStackItem CreateStackItem(WidgetStackGroup group, bool expanded)
    {
        string key = group.EffectiveKey;
        string name = group.DisplayName ?? GetStackCategoryName(group.Category);
        if (_stackNameOverrides.TryGetValue(key, out string? customName) && !string.IsNullOrWhiteSpace(customName))
        {
            name = customName;
        }
        if (!_stackItems.TryGetValue(key, out var stack))
        {
            stack = new WidgetStackItem
            {
                Category = group.Category,
                StackKey = key
            };
            _stackItems[key] = stack;
        }

        stack.Update(
            group.Items,
            name,
            _localizationService.Format("Widget.Stack.ItemCount", group.Items.Count),
            _localizationService.T(expanded
                ? "Widget.Stack.State.Expanded"
                : "Widget.Stack.State.Collapsed"),
            _localizationService.T("Widget.Stack.Collapse"),
            expanded,
            IconTileWidth,
            IconTileHeight,
            IconTileMargin,
            IconTilePadding,
            IconImageSize,
            Math.Clamp(Math.Round(IconImageSize * 0.76), 14, IconImageSize),
            IconLabelMaxWidth,
            IconLabelFontSize,
            ListItemMargin,
            ListItemPadding,
            ListIconSize);
        return stack;
    }

    private void RefreshStackLayoutMetrics()
    {
        foreach (WidgetStackItem stack in _stackItems.Values)
        {
            stack.UpdateLayoutMetrics(
                IconTileWidth,
                IconTileHeight,
                IconTileMargin,
                IconTilePadding,
                IconImageSize,
                Math.Clamp(Math.Round(IconImageSize * 0.76), 14, IconImageSize),
                IconLabelMaxWidth,
                IconLabelFontSize,
                ListItemMargin,
                ListItemPadding,
                ListIconSize);
        }
    }

    private void ReconcileStackDisplayItems(IReadOnlyList<WidgetItem> desired)
    {
        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            WidgetItem desiredItem = desired[targetIndex];
            if (targetIndex < _stackDisplayItems.Count &&
                ReferenceEquals(_stackDisplayItems[targetIndex], desiredItem))
            {
                continue;
            }

            int existingIndex = IndexOfReference(_stackDisplayItems, desiredItem, targetIndex + 1);
            if (existingIndex >= 0)
            {
                _stackDisplayItems.Move(existingIndex, targetIndex);
            }
            else
            {
                _stackDisplayItems.Insert(targetIndex, desiredItem);
            }
        }

        while (_stackDisplayItems.Count > desired.Count)
        {
            _stackDisplayItems.RemoveAt(_stackDisplayItems.Count - 1);
        }
    }

    private static int IndexOfReference(
        IReadOnlyList<WidgetItem> items,
        WidgetItem candidate,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], candidate))
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record StackDisplayUnit(
        string OrderKey,
        WidgetStackGroup? StackGroup,
        WidgetItem? LooseItem);

    private string GetStackCategoryName(WidgetStackCategory category) =>
        _localizationService.T($"Widget.Stack.Category.{category}");

    private void ScheduleStackDateBoundaryRefresh()
    {
        if (_dispatcherQueue is null)
        {
            return;
        }

        _stackDateBoundaryTimer ??= _dispatcherQueue.CreateTimer();
        _stackDateBoundaryTimer.Stop();
        _stackDateBoundaryTimer.Tick -= StackDateBoundaryTimer_Tick;

        bool usesDateGrouping = FileStackGroupBy is
            SettingsService.FileStackGroupByDateAdded or
            SettingsService.FileStackGroupByDateModified;
        if (!FileStacksEnabled || !usesDateGrouping)
        {
            return;
        }

        DateTime now = DateTime.Now;
        _stackDateBoundaryTimer.Interval = now.Date.AddDays(1).AddSeconds(1) - now;
        _stackDateBoundaryTimer.IsRepeating = false;
        _stackDateBoundaryTimer.Tick += StackDateBoundaryTimer_Tick;
        _stackDateBoundaryTimer.Start();
    }

    private void StackDateBoundaryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        sender.Tick -= StackDateBoundaryTimer_Tick;
        RebuildStackDisplayItems();
        ScheduleStackDateBoundaryRefresh();
    }
}
