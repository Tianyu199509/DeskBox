using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    public const int MaxLayoutUndoSnapshots = 25;

    private readonly List<LayoutUndoEntry> _layoutUndoStack = [];
    private LayoutUndoState? _interactiveLayoutUndoBeforeState;
    private int _inlineLayoutUndoDepth;
    private int _layoutUndoSuppressionDepth;

    public bool HasLayoutUndoHistory => _layoutUndoStack.Count > 0;

    /// <summary>
    /// Returns the UTC timestamp of the most recent layout snapshot available
    /// for unified Ctrl+Z ordering against organizer move history.
    /// </summary>
    public DateTimeOffset? GetLatestLayoutUndoTimestampUtc() =>
        _layoutUndoStack.Count > 0 ? _layoutUndoStack[^1].TimestampUtc : null;

    /// <summary>
    /// Returns the UTC timestamp of the most recent layout snapshot matching the
    /// currently visible folder scope (widget root vs active subfolder).
    /// </summary>
    public DateTimeOffset? GetLatestLayoutUndoTimestampForCurrentScopeUtc()
    {
        string? currentScopeKey = GetCurrentFolderScopeKey();
        for (int i = _layoutUndoStack.Count - 1; i >= 0; i--)
        {
            if (ScopeKeysMatch(_layoutUndoStack[i].State.ActiveFolderScopeKey, currentScopeKey))
            {
                return _layoutUndoStack[i].TimestampUtc;
            }
        }

        return null;
    }

    /// <summary>
    /// Begins an interactive multi-step layout mutation (such as live drag
    /// reordering). Coalesces all intermediate mutations until
    /// <see cref="CommitInteractiveLayoutUndoBatch"/> into a single undo entry.
    /// </summary>
    public void BeginInteractiveLayoutUndoBatch()
    {
        if (_layoutUndoSuppressionDepth > 0 || _interactiveLayoutUndoBeforeState is not null)
        {
            return;
        }

        _interactiveLayoutUndoBeforeState = CaptureLayoutUndoState();
    }

    /// <summary>
    /// Pushes a single layout undo snapshot if the interactive batch modified
    /// persistent layout or item order state.
    /// </summary>
    public bool CommitInteractiveLayoutUndoBatch()
    {
        LayoutUndoState? before = _interactiveLayoutUndoBeforeState;
        _interactiveLayoutUndoBeforeState = null;
        if (before is null || _layoutUndoSuppressionDepth > 0)
        {
            return false;
        }

        LayoutUndoState after = CaptureLayoutUndoState();
        if (before.EqualsState(after))
        {
            return false;
        }

        PushLayoutUndoSnapshot(before);
        return true;
    }

    /// <summary>
    /// Reverts any un-committed live preview mutations captured by
    /// <see cref="BeginInteractiveLayoutUndoBatch"/> without pushing an undo entry.
    /// </summary>
    public void RevertAndClearInteractiveLayoutUndoBatch()
    {
        LayoutUndoState? before = _interactiveLayoutUndoBeforeState;
        _interactiveLayoutUndoBeforeState = null;
        if (before is null)
        {
            return;
        }

        LayoutUndoState current = CaptureLayoutUndoState();
        if (!before.EqualsState(current))
        {
            RestoreLayoutUndoState(before);
        }
    }

    /// <summary>
    /// Restores the most recent widget layout snapshot (sort mode, manual
    /// order, stack membership, stack display order, stack names/disabled set,
    /// and subfolder overrides).
    /// </summary>
    public bool TryUndoLatestLayoutChange()
    {
        _interactiveLayoutUndoBeforeState = null;
        if (_layoutUndoStack.Count == 0)
        {
            return false;
        }

        LayoutUndoEntry entry = _layoutUndoStack[^1];
        _layoutUndoStack.RemoveAt(_layoutUndoStack.Count - 1);
        RestoreLayoutUndoState(entry.State);
        return true;
    }

    /// <summary>
    /// Restores the most recent layout snapshot captured in the currently
    /// visible folder scope (widget root vs active subfolder).
    /// </summary>
    public bool TryUndoLatestLayoutChangeForCurrentScope()
    {
        _interactiveLayoutUndoBeforeState = null;
        string? currentScopeKey = GetCurrentFolderScopeKey();
        for (int i = _layoutUndoStack.Count - 1; i >= 0; i--)
        {
            if (ScopeKeysMatch(_layoutUndoStack[i].State.ActiveFolderScopeKey, currentScopeKey))
            {
                LayoutUndoEntry entry = _layoutUndoStack[i];
                _layoutUndoStack.RemoveAt(i);
                RestoreLayoutUndoState(entry.State);
                return true;
            }
        }

        return false;
    }

    private string? GetCurrentFolderScopeKey() =>
        TryGetCurrentSubfolderScopeKey(out string scopeKey) ? scopeKey : null;

    private static bool ScopeKeysMatch(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal InlineLayoutUndoScope BeginInlineLayoutUndoScope()
    {
        if (_layoutUndoSuppressionDepth > 0 ||
            _interactiveLayoutUndoBeforeState is not null ||
            _inlineLayoutUndoDepth > 0)
        {
            _inlineLayoutUndoDepth++;
            return new InlineLayoutUndoScope(this, beforeState: null);
        }

        _inlineLayoutUndoDepth = 1;
        return new InlineLayoutUndoScope(this, CaptureLayoutUndoState());
    }

    private void EndInlineLayoutUndoScope(LayoutUndoState? beforeState)
    {
        if (_inlineLayoutUndoDepth > 0)
        {
            _inlineLayoutUndoDepth--;
        }

        if (_inlineLayoutUndoDepth != 0 ||
            beforeState is null ||
            _layoutUndoSuppressionDepth > 0 ||
            _interactiveLayoutUndoBeforeState is not null)
        {
            return;
        }

        LayoutUndoState after = CaptureLayoutUndoState();
        if (!beforeState.EqualsState(after))
        {
            PushLayoutUndoSnapshot(beforeState);
        }
    }

    private void PushLayoutUndoSnapshot(LayoutUndoState beforeState)
    {
        _layoutUndoStack.Add(new LayoutUndoEntry(beforeState, DateTimeOffset.UtcNow));
        while (_layoutUndoStack.Count > MaxLayoutUndoSnapshots)
        {
            _layoutUndoStack.RemoveAt(0);
        }
    }

    private LayoutUndoState CaptureLayoutUndoState()
    {
        string? activeScopeKey = TryGetCurrentSubfolderScopeKey(out string scopeKey)
            ? scopeKey
            : null;
        string[] activePaths = Items
            .Where(static item => !string.IsNullOrWhiteSpace(item.Path))
            .Select(static item => Path.GetFullPath(item.Path))
            .ToArray();

        WidgetItemConfig[] configItems = Config.Items
            .Select(static item => new WidgetItemConfig
            {
                Path = item.Path,
                SortOrder = item.SortOrder
            })
            .ToArray();

        return new LayoutUndoState(
            Config.SortMode,
            Config.SortDescending,
            configItems,
            activeScopeKey,
            activePaths,
            new HashSet<string>(_disabledStacks, StringComparer.Ordinal),
            new Dictionary<string, string>(_stackNameOverrides, StringComparer.Ordinal),
            [.. _stackOrder],
            CloneStringListDictionary(_stackMemberOverrides, StringComparer.Ordinal),
            new Dictionary<string, WidgetFolderSortOverride>(_subfolderSortOverrides, StringComparer.OrdinalIgnoreCase),
            CloneStringListDictionary(_subfolderDisplayOrders, StringComparer.OrdinalIgnoreCase),
            CloneStringListDictionary(_subfolderManualItemOrders, StringComparer.OrdinalIgnoreCase));
    }

    private void RestoreLayoutUndoState(LayoutUndoState state)
    {
        _layoutUndoSuppressionDepth++;
        try
        {
            Config.SortMode = state.SortMode;
            Config.SortDescending = state.SortDescending;

            Config.Items.Clear();
            foreach (WidgetItemConfig item in state.ConfigItems)
            {
                Config.Items.Add(new WidgetItemConfig
                {
                    Path = item.Path,
                    SortOrder = item.SortOrder
                });
            }

            _disabledStacks = new HashSet<string>(state.DisabledStacks, StringComparer.Ordinal);
            _stackNameOverrides = new Dictionary<string, string>(state.StackNameOverrides, StringComparer.Ordinal);
            _stackOrder = [.. state.StackOrder];
            _stackMemberOverrides = CloneStringListDictionary(state.StackMemberOverrides, StringComparer.Ordinal);
            _subfolderSortOverrides = new Dictionary<string, WidgetFolderSortOverride>(
                state.SubfolderSortOverrides,
                StringComparer.OrdinalIgnoreCase);
            _subfolderDisplayOrders = CloneStringListDictionary(
                state.SubfolderDisplayOrders,
                StringComparer.OrdinalIgnoreCase);
            _subfolderManualItemOrders = CloneStringListDictionary(
                state.SubfolderManualItemOrders,
                StringComparer.OrdinalIgnoreCase);

            WidgetFileStackSettings.SetDisabledStacks(Config, _disabledStacks);
            WidgetFileStackSettings.SetStackNameOverrides(Config, _stackNameOverrides);
            WidgetFileStackSettings.SetStackOrder(Config, _stackOrder.Count == 0 ? null : _stackOrder);
            WidgetFileStackSettings.SetStackMemberOverrides(Config, _stackMemberOverrides);
            WidgetFileStackSettings.SetSubfolderSortOverrides(Config, _subfolderSortOverrides);
            WidgetFileStackSettings.SetSubfolderDisplayOrders(Config, _subfolderDisplayOrders);
            WidgetFileStackSettings.SetSubfolderManualItemOrders(Config, _subfolderManualItemOrders);

            string? currentScopeKey = GetCurrentFolderScopeKey();
            if (currentScopeKey is not null)
            {
                _expandedStackKey = null;
            }

            using (DeferStackDisplayRebuild())
            {
                if (EffectiveSortMode != WidgetSortMode.Manual)
                {
                    var sorted = Items
                        .OrderBy(item => item, Comparer<WidgetItem>.Create(CompareItems))
                        .ToList();
                    ReplaceItemsInOrder(sorted);
                    NormalizeSortOrder();
                }
                else
                {
                    IReadOnlyList<string> desiredPaths = ResolveDesiredRestorePaths(state, currentScopeKey);
                    if (desiredPaths.Count > 0)
                    {
                        RestoreManualItemsOrder(desiredPaths);
                    }

                    NormalizeSortOrder();
                }

                // Clear any deferred flag accumulated during item mutations so closing
                // DeferStackDisplayRebuild does not fire a second rebuild immediately before
                // the explicit RebuildStackDisplayItems() call below.
                _deferredStackRebuildPending = false;
            }

            RebuildStackDisplayItems();
            NotifyFolderSortPropertiesChanged();
            OnPropertyChanged(nameof(VisibleItems));
            OnPropertyChanged(nameof(HasDisabledStacks));
            _settingsService.UpdateWidget(Config, notifySubscribers: false);
            _settingsService.SaveDebounced(notifySubscribers: false);
        }
        finally
        {
            _layoutUndoSuppressionDepth--;
        }
    }

    internal LayoutUndoSuppressionScope SuppressLayoutUndoScope() => new(this);

    internal sealed class LayoutUndoSuppressionScope : IDisposable
    {
        private readonly WidgetViewModel _owner;
        private bool _disposed;

        internal LayoutUndoSuppressionScope(WidgetViewModel owner)
        {
            _owner = owner;
            _owner._layoutUndoSuppressionDepth++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_owner._layoutUndoSuppressionDepth > 0)
            {
                _owner._layoutUndoSuppressionDepth--;
            }
        }
    }

    private void ReplaceItemsInOrder(IReadOnlyList<WidgetItem> orderedItems)
    {
        if (Items.SequenceEqual(orderedItems, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        Items.Clear();
        foreach (WidgetItem item in orderedItems)
        {
            Items.Add(item);
        }
    }

    private void RestoreManualItemsOrder(IReadOnlyList<string> desiredPaths)
    {
        var byPath = new Dictionary<string, WidgetItem>(StringComparer.OrdinalIgnoreCase);
        foreach (WidgetItem item in Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                byPath.TryAdd(Path.GetFullPath(item.Path), item);
            }
        }

        var reordered = new List<WidgetItem>(Items.Count);
        var placed = new HashSet<WidgetItem>(ReferenceEqualityComparer.Instance);
        foreach (string rawPath in desiredPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(rawPath);
            if (byPath.TryGetValue(fullPath, out WidgetItem? matched) && placed.Add(matched))
            {
                reordered.Add(matched);
            }
        }

        foreach (WidgetItem remaining in Items)
        {
            if (placed.Add(remaining))
            {
                reordered.Add(remaining);
            }
        }

        ReplaceItemsInOrder(reordered);
    }

    private static IReadOnlyList<string> ResolveDesiredRestorePaths(
        LayoutUndoState state,
        string? currentScopeKey)
    {
        if (string.Equals(currentScopeKey, state.ActiveFolderScopeKey, StringComparison.OrdinalIgnoreCase) &&
            state.ActiveFolderItemPaths.Length > 0)
        {
            return state.ActiveFolderItemPaths;
        }

        if (!string.IsNullOrWhiteSpace(currentScopeKey) &&
            state.SubfolderManualItemOrders.TryGetValue(currentScopeKey, out List<string>? subPaths) &&
            subPaths.Count > 0)
        {
            return subPaths;
        }

        return state.ConfigItems
            .OrderBy(static item => item.SortOrder)
            .Select(static item => item.Path)
            .ToArray();
    }

    private static Dictionary<string, List<string>> CloneStringListDictionary(
        IReadOnlyDictionary<string, List<string>> source,
        StringComparer keyComparer)
    {
        var clone = new Dictionary<string, List<string>>(source.Count, keyComparer);
        foreach ((string key, List<string> values) in source)
        {
            clone[key] = [.. values];
        }

        return clone;
    }

    internal readonly struct InlineLayoutUndoScope : IDisposable
    {
        private readonly WidgetViewModel _owner;
        private readonly LayoutUndoState? _beforeState;

        internal InlineLayoutUndoScope(WidgetViewModel owner, LayoutUndoState? beforeState)
        {
            _owner = owner;
            _beforeState = beforeState;
        }

        public void Dispose() => _owner.EndInlineLayoutUndoScope(_beforeState);
    }

    private sealed record LayoutUndoEntry(LayoutUndoState State, DateTimeOffset TimestampUtc);

    internal sealed class LayoutUndoState
    {
        public LayoutUndoState(
            WidgetSortMode sortMode,
            bool sortDescending,
            WidgetItemConfig[] configItems,
            string? activeFolderScopeKey,
            string[] activeFolderItemPaths,
            HashSet<string> disabledStacks,
            Dictionary<string, string> stackNameOverrides,
            string[] stackOrder,
            Dictionary<string, List<string>> stackMemberOverrides,
            Dictionary<string, WidgetFolderSortOverride> subfolderSortOverrides,
            Dictionary<string, List<string>> subfolderDisplayOrders,
            Dictionary<string, List<string>> subfolderManualItemOrders)
        {
            SortMode = sortMode;
            SortDescending = sortDescending;
            ConfigItems = configItems;
            ActiveFolderScopeKey = activeFolderScopeKey;
            ActiveFolderItemPaths = activeFolderItemPaths;
            DisabledStacks = disabledStacks;
            StackNameOverrides = stackNameOverrides;
            StackOrder = stackOrder;
            StackMemberOverrides = stackMemberOverrides;
            SubfolderSortOverrides = subfolderSortOverrides;
            SubfolderDisplayOrders = subfolderDisplayOrders;
            SubfolderManualItemOrders = subfolderManualItemOrders;
        }

        public WidgetSortMode SortMode { get; }
        public bool SortDescending { get; }
        public WidgetItemConfig[] ConfigItems { get; }
        public string? ActiveFolderScopeKey { get; }
        public string[] ActiveFolderItemPaths { get; }
        public HashSet<string> DisabledStacks { get; }
        public Dictionary<string, string> StackNameOverrides { get; }
        public string[] StackOrder { get; }
        public Dictionary<string, List<string>> StackMemberOverrides { get; }
        public Dictionary<string, WidgetFolderSortOverride> SubfolderSortOverrides { get; }
        public Dictionary<string, List<string>> SubfolderDisplayOrders { get; }
        public Dictionary<string, List<string>> SubfolderManualItemOrders { get; }

        public bool EqualsState(LayoutUndoState other) =>
            SortMode == other.SortMode &&
            SortDescending == other.SortDescending &&
            string.Equals(ActiveFolderScopeKey, other.ActiveFolderScopeKey, StringComparison.OrdinalIgnoreCase) &&
            ConfigItemsEqual(ConfigItems, other.ConfigItems) &&
            ActiveFolderItemPaths.SequenceEqual(other.ActiveFolderItemPaths, StringComparer.OrdinalIgnoreCase) &&
            DisabledStacks.SetEquals(other.DisabledStacks) &&
            StringDictionariesEqual(StackNameOverrides, other.StackNameOverrides) &&
            StackOrder.SequenceEqual(other.StackOrder, StringComparer.Ordinal) &&
            ListDictionariesEqual(StackMemberOverrides, other.StackMemberOverrides, StringComparer.OrdinalIgnoreCase) &&
            StructDictionariesEqual(SubfolderSortOverrides, other.SubfolderSortOverrides) &&
            ListDictionariesEqual(SubfolderDisplayOrders, other.SubfolderDisplayOrders, StringComparer.Ordinal) &&
            ListDictionariesEqual(SubfolderManualItemOrders, other.SubfolderManualItemOrders, StringComparer.OrdinalIgnoreCase);

        private static bool ConfigItemsEqual(WidgetItemConfig[] left, WidgetItemConfig[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i].SortOrder != right[i].SortOrder ||
                    !string.Equals(left[i].Path, right[i].Path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StringDictionariesEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach ((string key, string value) in left)
            {
                if (!right.TryGetValue(key, out string? otherValue) ||
                    !string.Equals(value, otherValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StructDictionariesEqual<TValue>(
            IReadOnlyDictionary<string, TValue> left,
            IReadOnlyDictionary<string, TValue> right)
            where TValue : struct, IEquatable<TValue>
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach ((string key, TValue value) in left)
            {
                if (!right.TryGetValue(key, out TValue otherValue) || !value.Equals(otherValue))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ListDictionariesEqual(
            IReadOnlyDictionary<string, List<string>> left,
            IReadOnlyDictionary<string, List<string>> right,
            StringComparer itemComparer)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach ((string key, List<string> value) in left)
            {
                if (!right.TryGetValue(key, out List<string>? otherValue) ||
                    !value.SequenceEqual(otherValue, itemComparer))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
