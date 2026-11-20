using System.Globalization;
using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private List<Action<CustomWidgetEvaluationContext>> _retainedPatchers = [];
    private int _retainedPatchersVersion;
    private int _cachedPatchersSnapshotVersion = -1;
    private Action<CustomWidgetEvaluationContext>[] _cachedPatchersSnapshot = [];
    private int _cachedMetricSnapshotCount = -1;
    private Action<CustomWidgetEvaluationContext>[] _cachedMetricSnapshot = [];
    private readonly HashSet<FrameworkElement> _interactiveElements = [];
    private readonly Dictionary<string, WeakReference<FrameworkElement>> _elementsById = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasRetainedRoot;
    private int _buildingPairedSlotDepth;

    internal bool HasOpenOverlayFlyout =>
        _activeOpenFlyouts.Count > 0 ||
        _activeOpenPopovers.Count > 0;

    private static void FireAndForgetSafeUi(Func<Task> action)
    {
        _ = RunSafeUiAsync(action);
    }

    private static async Task RunSafeUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CustomWidget.Renderer] UI handler exception: {ex.Message}");
        }
    }

    internal void RegisterRetainedPatcher(Action<CustomWidgetEvaluationContext> patcher)
    {
        ArgumentNullException.ThrowIfNull(patcher);
        if (_buildingPairedSlotDepth > 0)
        {
            Action<CustomWidgetEvaluationContext> inner = patcher;
            _retainedPatchers.Add(liveCtx => inner(liveCtx.IsPairedSlot ? liveCtx : liveCtx.WithPairedSlot(true)));
        }
        else
        {
            _retainedPatchers.Add(patcher);
        }
        _retainedPatchersVersion++;
    }

    internal void MarkInteractiveElement(FrameworkElement element)
    {
        if (element is not null)
        {
            _interactiveElements.Add(element);
            try
            {
                DeskBox.Helpers.UiElementCursorHelper.SetCursor(element, Microsoft.UI.Input.InputSystemCursorShape.Hand);
            }
            catch
            {
            }
        }
    }

    private void ForgetRetainedSubtree(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }

        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();

        void Enqueue(DependencyObject? candidate)
        {
            if (candidate is not null && visited.Add(candidate))
            {
                pending.Push(candidate);
            }
        }

        Enqueue(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            _inspectableNodes.Remove(current);

            if (current is TeachingTip currentTip)
            {
                ForgetTeachingTip(currentTip);
            }

            if (current is FrameworkElement fe)
            {
                _interactiveElements.Remove(fe);
                ForgetElementById(fe);
                DetachTeachingTipTargets(fe);
                DeskBox.Helpers.FluentMotionHelper.StopCanvasAmbientMotion(fe);

                if (fe is DeskBox.Controls.MarkdownDocumentView mdView)
                {
                    ForgetMarkdownView(mdView);
                }
                else if (fe is MediaPlayerElement mpe)
                {
                    ForgetMediaPlayerElement(mpe);
                }
                else if (fe is CustomWidgetRecycleBinCard rbCard)
                {
                    ForgetRecycleBinCard(rbCard);
                }
            }

            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount; i++)
            {
                Enqueue(VisualTreeHelper.GetChild(current, i));
            }

            if (current is Border border)
            {
                Enqueue(border.Child);
            }
            else if (current is ContentControl cc)
            {
                Enqueue(cc.Content as DependencyObject);
            }
            else if (current is ContentPresenter cp)
            {
                Enqueue(cp.Content as DependencyObject);
            }
            else if (current is Panel panel)
            {
                foreach (UIElement panelChild in panel.Children)
                {
                    Enqueue(panelChild);
                }
            }
            else if (current is ItemsControl itemsControl)
            {
                foreach (object? item in itemsControl.Items)
                {
                    Enqueue(item as DependencyObject);
                }
            }
            else if (current is SplitView splitView)
            {
                Enqueue(splitView.Pane);
                Enqueue(splitView.Content);
            }
            else if (current is InfoBar infoBar)
            {
                Enqueue(infoBar.Content as DependencyObject);
                Enqueue(infoBar.ActionButton);
            }
        }
    }

    private void ForgetElementById(FrameworkElement detachedElement)
    {
        if (_elementsById.Count == 0)
        {
            return;
        }

        List<string>? staleIds = null;
        foreach ((string id, WeakReference<FrameworkElement> weakRef) in _elementsById)
        {
            if (!weakRef.TryGetTarget(out FrameworkElement? target) ||
                ReferenceEquals(target, detachedElement))
            {
                staleIds ??= [];
                staleIds.Add(id);
            }
        }

        if (staleIds is not null)
        {
            foreach (string id in staleIds)
            {
                _elementsById.Remove(id);
            }
        }
    }

    internal static T? UnwrapHorizontalStripContent<T>(DependencyObject? element)
        where T : DependencyObject
    {
        DependencyObject? cur = element;
        while (cur is not null)
        {
            if (cur is T matched)
            {
                return matched;
            }

            if (cur is Border border)
            {
                cur = border.Child;
            }
            else if (cur is ScrollViewer sv)
            {
                cur = sv.Content as DependencyObject;
            }
            else if (cur is ContentPresenter cp)
            {
                cur = cp.Content as DependencyObject;
            }
            else
            {
                break;
            }
        }

        return null;
    }

    private static ScrollViewer WrapInHorizontalStripScrollViewer(
        FrameworkElement content,
        bool stretchToViewport)
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = stretchToViewport ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Content = content
        };

        if (stretchToViewport)
        {
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            scrollViewer.SizeChanged += (_, e) =>
            {
                double vw = scrollViewer.ViewportWidth > 0 && double.IsFinite(scrollViewer.ViewportWidth)
                    ? scrollViewer.ViewportWidth
                    : e.NewSize.Width;
                if (double.IsFinite(vw) && vw > 0 && Math.Abs(content.MinWidth - vw) > 0.5)
                {
                    content.MinWidth = vw;
                }
            };
        }

        scrollViewer.PointerWheelChanged += (_, e) =>
        {
            if (scrollViewer.ScrollableWidth <= 0)
            {
                return;
            }

            Microsoft.UI.Input.PointerPointProperties props = e.GetCurrentPoint(scrollViewer).Properties;
            if (props.IsHorizontalMouseWheel)
            {
                return;
            }

            double delta = props.MouseWheelDelta * 0.5;
            if (Math.Abs(delta) < 0.01)
            {
                return;
            }

            double currentOffset = scrollViewer.HorizontalOffset;
            double targetOffset = Math.Clamp(currentOffset - delta, 0.0, scrollViewer.ScrollableWidth);
            if (Math.Abs(targetOffset - currentOffset) > 0.1)
            {
                scrollViewer.ChangeView(targetOffset, null, null, disableAnimation: true);
                e.Handled = true;
            }
        };

        return scrollViewer;
    }

    internal bool IsInteractiveElement(FrameworkElement element) =>
        element is not null && _interactiveElements.Contains(element);

    internal void RegisterElementById(string? rawId, FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(rawId) && element is not null)
        {
            _elementsById[rawId.Trim()] = new WeakReference<FrameworkElement>(element);
        }
    }

    internal FrameworkElement? TryResolveElementById(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return null;
        }

        if (_elementsById.TryGetValue(rawId.Trim(), out WeakReference<FrameworkElement>? weakRef) &&
            weakRef.TryGetTarget(out FrameworkElement? target))
        {
            return target;
        }

        return null;
    }

    private void ResetReconcilerState()
    {
        _retainedPatchers.Clear();
        _retainedPatchersVersion++;
        _cachedPatchersSnapshotVersion = -1;
        _cachedPatchersSnapshot = [];
        _cachedMetricSnapshotCount = -1;
        _cachedMetricSnapshot = [];
        _interactiveElements.Clear();
        _elementsById.Clear();
        _hasRetainedRoot = false;
    }

    private List<Action<CustomWidgetEvaluationContext>> CaptureScopedPatchers(Action buildAction)
    {
        List<Action<CustomWidgetEvaluationContext>> outerRetained = _retainedPatchers;
        int metricStart = _liveMetricUpdaters.Count;
        var scopedRetained = new List<Action<CustomWidgetEvaluationContext>>();
        _retainedPatchers = scopedRetained;
        try
        {
            buildAction();
        }
        finally
        {
            _retainedPatchers = outerRetained;
        }

        int addedMetrics = _liveMetricUpdaters.Count - metricStart;
        if (addedMetrics > 0)
        {
            scopedRetained.AddRange(_liveMetricUpdaters.GetRange(metricStart, addedMetrics));
            _liveMetricUpdaters.RemoveRange(metricStart, addedMetrics);
        }

        return scopedRetained;
    }

    internal void RegisterRetainedPatcherForTest(Action<CustomWidgetEvaluationContext> patcher, bool markRetainedRoot = true)
    {
        ArgumentNullException.ThrowIfNull(patcher);
        _retainedPatchers.Add(patcher);
        _retainedPatchersVersion++;
        if (markRetainedRoot)
        {
            _hasRetainedRoot = true;
        }
    }

    public bool TryPatchRetainedTree()
    {
        if (!_hasRetainedRoot)
        {
            return false;
        }

        CustomWidgetEvaluationContext ctx = _viewModel.CreateEvaluationContext();
        if (_cachedPatchersSnapshotVersion != _retainedPatchersVersion ||
            _cachedPatchersSnapshot.Length != _retainedPatchers.Count)
        {
            _cachedPatchersSnapshot = [.. _retainedPatchers];
            _cachedPatchersSnapshotVersion = _retainedPatchersVersion;
        }

        Action<CustomWidgetEvaluationContext>[] snapshot = _cachedPatchersSnapshot;
        foreach (Action<CustomWidgetEvaluationContext> patcher in snapshot)
        {
            patcher(ctx);
        }

        if (!_hasRetainedRoot)
        {
            return false;
        }

        if (_liveMetricUpdaters.Count > 0)
        {
            if (_cachedMetricSnapshotCount != _liveMetricUpdaters.Count)
            {
                _cachedMetricSnapshot = [.. _liveMetricUpdaters];
                _cachedMetricSnapshotCount = _liveMetricUpdaters.Count;
            }

            Action<CustomWidgetEvaluationContext>[] metricSnapshot = _cachedMetricSnapshot;
            foreach (Action<CustomWidgetEvaluationContext> updater in metricSnapshot)
            {
                updater(ctx);
            }
        }

        if (_openPopoversById.Count > 0)
        {
            PatchOpenPopovers(ctx, rebuildEmptyBodies: false);
        }

        return true;
    }

    private static void ApplyOuterSlotAlignmentOnly(FrameworkElement slotHost, CustomWidgetNodeSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.HorizontalAlignment))
        {
            slotHost.HorizontalAlignment = ParseHorizontalAlignment(spec.HorizontalAlignment);
        }

        if (!string.IsNullOrWhiteSpace(spec.VerticalAlignment))
        {
            slotHost.VerticalAlignment = ParseVerticalAlignment(spec.VerticalAlignment);
        }
    }

    private UIElement BuildConditionalWhenSlot(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext initialCtx,
        bool isRoot)
    {
        string whenExpr = spec.When!;
        var slotHost = new ContentPresenter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        ApplyOuterSlotAlignmentOnly(slotHost, spec);

        bool innerMaterialized = false;
        List<Action<CustomWidgetEvaluationContext>>? innerPatchers = null;

        void MaterializeInner(CustomWidgetEvaluationContext evalCtx)
        {
            UIElement built = new Grid();
            innerPatchers = CaptureScopedPatchers(() =>
            {
                built = BuildNodeCore(spec, evalCtx, isRoot, ignoreWhen: true);
            });
            slotHost.Content = built;
            innerMaterialized = true;
        }

        bool initialVisible = CustomWidgetExpressionEvaluator.EvaluateCondition(whenExpr, initialCtx);
        if (initialVisible)
        {
            MaterializeInner(initialCtx);
            slotHost.Visibility = Visibility.Visible;
        }
        else
        {
            slotHost.Visibility = Visibility.Collapsed;
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            bool nextVisible = CustomWidgetExpressionEvaluator.EvaluateCondition(whenExpr, liveCtx);
            if (!nextVisible)
            {
                if (slotHost.Visibility != Visibility.Collapsed)
                {
                    slotHost.Visibility = Visibility.Collapsed;
                }
                return;
            }

            bool wasCollapsed = slotHost.Visibility == Visibility.Collapsed;
            if (!innerMaterialized)
            {
                MaterializeInner(liveCtx);
                slotHost.Visibility = Visibility.Visible;
                DeskBox.Helpers.FluentMotionHelper.PlayEntranceSlideAndFade(slotHost, offsetY: 4f, durationMs: 140);
                return;
            }

            if (wasCollapsed)
            {
                slotHost.Visibility = Visibility.Visible;
                DeskBox.Helpers.FluentMotionHelper.PlayEntranceSlideAndFade(slotHost, offsetY: 4f, durationMs: 140);
            }

            if (innerPatchers is { Count: > 0 })
            {
                foreach (Action<CustomWidgetEvaluationContext> p in innerPatchers)
                {
                    p(liveCtx);
                }
            }
        });

        return slotHost;
    }

    private sealed class KeyedCollectionItemSlot
    {
        public required string Key { get; init; }
        public required UIElement Element { get; init; }
        public required List<Action<CustomWidgetEvaluationContext>> Patchers { get; init; }
        public required CustomWidgetRecord RecordSnapshot { get; set; }
        public required int IndexSnapshot { get; set; }
    }

    private FrameworkElement BuildRetainedCollectionView(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext initialCtx,
        Panel itemsPanel,
        bool wrapInScrollViewer,
        CustomWidgetEmptyStateSpec defaultEmptySpec,
        Func<CustomWidgetEvaluationContext, string, UIElement> itemBuilder)
    {
        string rawItemsBinding = spec.Items ?? spec.Collection ?? "logs";
        var slotsByKey = new Dictionary<string, KeyedCollectionItemSlot>(StringComparer.Ordinal);
        var hostGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        FrameworkElement emptyElement = BuildEmptyState(spec.EmptyState ?? defaultEmptySpec, initialCtx);
        FrameworkElement itemsHostElement = wrapInScrollViewer
            ? new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(itemsPanel)
            }
            : itemsPanel;

        hostGrid.Children.Add(emptyElement);
        hostGrid.Children.Add(itemsHostElement);

        (List<(CustomWidgetRecord Record, int Index, string Key, CustomWidgetEvaluationContext ItemCtx)> Filtered, HashSet<string> ActiveKeys, string ResolvedCollectionName) ResolveFilteredItems(
            CustomWidgetEvaluationContext evalCtx)
        {
            string resolvedCollectionName = string.Empty;
            if (!TryResolveComputedRecords(rawItemsBinding, evalCtx, out IReadOnlyList<CustomWidgetRecord> allRecords))
            {
                string collectionName = CustomWidgetExpressionEvaluator
                    .Interpolate(rawItemsBinding, evalCtx)
                    .Trim();
                if (string.IsNullOrWhiteSpace(collectionName))
                {
                    collectionName = "logs";
                }

                if (collectionName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    collectionName = collectionName["collections.".Length..];
                    int dot = collectionName.IndexOf('.');
                    if (dot > 0) collectionName = collectionName[..dot];
                }

                resolvedCollectionName = collectionName;
                allRecords = evalCtx.Collections.TryGetValue(collectionName, out List<CustomWidgetRecord>? list)
                    ? list
                    : [];
            }
            else if (!string.IsNullOrWhiteSpace(spec.Collection) &&
                     !spec.Collection.Contains("${", StringComparison.Ordinal))
            {
                string explicitCol = spec.Collection.Trim();
                if (explicitCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    explicitCol = explicitCol["collections.".Length..];
                }
                resolvedCollectionName = explicitCol;
            }

            string? filterEqualsVal = !string.IsNullOrWhiteSpace(spec.FilterEquals)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.FilterEquals, evalCtx)
                : null;

            int cap = spec.MaxItems > 0
                ? spec.MaxItems
                : (spec.MaxLines > 0 ? spec.MaxLines : int.MaxValue);

            int initialCapacity = Math.Min(allRecords.Count, cap);
            var filtered = new List<(CustomWidgetRecord Record, int Index, string Key, CustomWidgetEvaluationContext ItemCtx)>(initialCapacity);
            var usedKeys = new HashSet<string>(initialCapacity, StringComparer.Ordinal);
            for (int i = 0; i < allRecords.Count && filtered.Count < cap; i++)
            {
                CustomWidgetRecord rec = allRecords[i];
                if (rec.IsDeleted)
                {
                    continue;
                }

                CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(rec, i);
                if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                    !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(spec.ItemTemplate?.When) &&
                    !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.ItemTemplate.When, itemCtx))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(spec.FilterField) && filterEqualsVal is not null)
                {
                    string fv = rec.GetField(spec.FilterField);
                    if (!string.Equals(fv, filterEqualsVal, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                string baseKey = !string.IsNullOrWhiteSpace(rec.Id) ? rec.Id : $"__idx_{i}";
                string uniqueKey = usedKeys.Add(baseKey) ? baseKey : $"{baseKey}#{i}";
                if (!ReferenceEquals(uniqueKey, baseKey))
                {
                    usedKeys.Add(uniqueKey);
                }

                filtered.Add((rec, i, uniqueKey, itemCtx));
            }

            return (filtered, usedKeys, resolvedCollectionName);
        }

        bool usesLiveHostTokens = SubtreeReferencesLiveHostTokens(spec);
        bool hasReconciledOnce = false;
        long lastColVer = -1;
        long lastStateVer = -1;
        long lastCompVer = -1;
        string? lastCulture = null;
        IReadOnlyDictionary<string, string>? lastStateDictRef = null;
        IReadOnlyDictionary<string, List<CustomWidgetRecord>>? lastCollectionsDictRef = null;
        string lastResolvedColName = string.Empty;
        List<CustomWidgetRecord>? lastDirectListRef = null;
        int lastDirectListCount = -1;

        void ReconcileItems(CustomWidgetEvaluationContext evalCtx)
        {
            long curColVer = _viewModel.CollectionsMutationVersion;
            long curStateVer = _viewModel.StateMutationVersion;
            long curCompVer = _viewModel.ComputedMutationVersion;

            if (hasReconciledOnce &&
                !usesLiveHostTokens &&
                curColVer == lastColVer &&
                curStateVer == lastStateVer &&
                curCompVer == lastCompVer &&
                string.Equals(evalCtx.CultureName, lastCulture, StringComparison.Ordinal) &&
                ReferenceEquals(lastStateDictRef, evalCtx.State) &&
                ReferenceEquals(lastCollectionsDictRef, evalCtx.Collections))
            {
                bool listUnchanged = string.IsNullOrEmpty(lastResolvedColName)
                    || (evalCtx.Collections.TryGetValue(lastResolvedColName, out List<CustomWidgetRecord>? curList)
                        && ReferenceEquals(curList, lastDirectListRef)
                        && curList.Count == lastDirectListCount);
                if (listUnchanged)
                {
                    return;
                }
            }

            (List<(CustomWidgetRecord Record, int Index, string Key, CustomWidgetEvaluationContext ItemCtx)> nextItems, HashSet<string> activeKeys, string resolvedColName) =
                ResolveFilteredItems(evalCtx);

            hasReconciledOnce = true;
            lastColVer = curColVer;
            lastStateVer = curStateVer;
            lastCompVer = curCompVer;
            lastCulture = evalCtx.CultureName;
            lastStateDictRef = evalCtx.State;
            lastCollectionsDictRef = evalCtx.Collections;
            lastResolvedColName = resolvedColName;
            if (!string.IsNullOrEmpty(resolvedColName) &&
                evalCtx.Collections.TryGetValue(resolvedColName, out List<CustomWidgetRecord>? resolvedList))
            {
                lastDirectListRef = resolvedList;
                lastDirectListCount = resolvedList.Count;
            }
            else
            {
                lastDirectListRef = null;
                lastDirectListCount = -1;
            }

            if (nextItems.Count == 0)
            {
                emptyElement.Visibility = Visibility.Visible;
                itemsHostElement.Visibility = Visibility.Collapsed;
                if (itemsPanel.Children.Count > 0)
                {
                    foreach (UIElement child in itemsPanel.Children)
                    {
                        ForgetRetainedSubtree(child);
                    }
                    itemsPanel.Children.Clear();
                    slotsByKey.Clear();
                }
                return;
            }

            emptyElement.Visibility = Visibility.Collapsed;
            itemsHostElement.Visibility = Visibility.Visible;

            if (slotsByKey.Count > 0)
            {
                List<string>? staleKeys = null;
                foreach ((string existingKey, KeyedCollectionItemSlot staleSlot) in slotsByKey)
                {
                    if (!activeKeys.Contains(existingKey))
                    {
                        staleKeys ??= [];
                        staleKeys.Add(existingKey);
                        ForgetRetainedSubtree(staleSlot.Element);
                        itemsPanel.Children.Remove(staleSlot.Element);
                    }
                }

                if (staleKeys is not null)
                {
                    foreach (string k in staleKeys)
                    {
                        slotsByKey.Remove(k);
                    }
                }
            }

            for (int targetPos = 0; targetPos < nextItems.Count; targetPos++)
            {
                (CustomWidgetRecord rec, int sourceIdx, string key, CustomWidgetEvaluationContext itemCtx) = nextItems[targetPos];

                if (slotsByKey.TryGetValue(key, out KeyedCollectionItemSlot? existingSlot))
                {
                    if (targetPos >= itemsPanel.Children.Count ||
                        !ReferenceEquals(itemsPanel.Children[targetPos], existingSlot.Element))
                    {
                        int currentVisualIdx = itemsPanel.Children.IndexOf(existingSlot.Element);
                        if (currentVisualIdx != targetPos)
                        {
                            if (currentVisualIdx >= 0)
                            {
                                itemsPanel.Children.RemoveAt(currentVisualIdx);
                            }
                            if (targetPos <= itemsPanel.Children.Count)
                            {
                                itemsPanel.Children.Insert(targetPos, existingSlot.Element);
                            }
                            else
                            {
                                itemsPanel.Children.Add(existingSlot.Element);
                            }
                        }
                    }

                    existingSlot.RecordSnapshot = rec;
                    existingSlot.IndexSnapshot = sourceIdx;
                    foreach (Action<CustomWidgetEvaluationContext> patcher in existingSlot.Patchers)
                    {
                        patcher(itemCtx);
                    }
                }
                else
                {
                    UIElement builtElement = new Grid();
                    List<Action<CustomWidgetEvaluationContext>> itemPatchers = CaptureScopedPatchers(() =>
                    {
                        builtElement = itemBuilder(itemCtx, resolvedColName);
                    });

                    var newSlot = new KeyedCollectionItemSlot
                    {
                        Key = key,
                        Element = builtElement,
                        Patchers = itemPatchers,
                        RecordSnapshot = rec,
                        IndexSnapshot = sourceIdx
                    };
                    slotsByKey[key] = newSlot;

                    if (targetPos <= itemsPanel.Children.Count)
                    {
                        itemsPanel.Children.Insert(targetPos, builtElement);
                    }
                    else
                    {
                        itemsPanel.Children.Add(builtElement);
                    }
                }
            }

            if (string.Equals(spec.Type, "List", StringComparison.OrdinalIgnoreCase) &&
                IsDividedListVariant(CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, evalCtx)))
            {
                for (int visIdx = 0; visIdx < itemsPanel.Children.Count; visIdx++)
                {
                    if (itemsPanel.Children[visIdx] is Border rowBorder)
                    {
                        Thickness desired = visIdx > 0 ? new Thickness(0, 1, 0, 0) : new Thickness(0);
                        if (rowBorder.BorderThickness != desired)
                        {
                            rowBorder.BorderThickness = desired;
                        }
                    }
                }
            }
        }

        ReconcileItems(initialCtx);
        RegisterRetainedPatcher(ReconcileItems);

        return ApplyContainerVisualOverrides(hostGrid, spec, initialCtx);
    }

    private static bool SubtreeReferencesLiveHostTokens(CustomWidgetNodeSpec? node)
    {
        if (node is null)
        {
            return false;
        }

        if (ContainsLiveHostOrClockToken(node.When) ||
            ContainsLiveHostOrClockToken(node.Items) ||
            ContainsLiveHostOrClockToken(node.Collection) ||
            ContainsLiveHostOrClockToken(node.Filter) ||
            ContainsLiveHostOrClockToken(node.FilterEquals) ||
            ContainsLiveHostOrClockToken(node.Variant) ||
            ContainsLiveHostOrClockToken(node.Text) ||
            ContainsLiveHostOrClockToken(node.Title) ||
            ContainsLiveHostOrClockToken(node.Subtitle) ||
            ContainsLiveHostOrClockToken(node.Value) ||
            ContainsLiveHostOrClockToken(node.Bind) ||
            ContainsLiveHostOrClockToken(node.Progress) ||
            ContainsLiveHostOrClockToken(node.Color) ||
            ContainsLiveHostOrClockToken(node.TextColor) ||
            ContainsLiveHostOrClockToken(node.Background) ||
            ContainsLiveHostOrClockToken(node.BorderColor) ||
            ContainsLiveHostOrClockToken(node.Icon) ||
            ContainsLiveHostOrClockToken(node.Tooltip) ||
            ContainsLiveHostOrClockToken(node.Src) ||
            ContainsLiveHostOrClockToken(node.FilePath) ||
            ContainsLiveHostOrClockToken(node.Unit) ||
            ContainsLiveHostOrClockToken(node.OpacityExpression) ||
            ContainsLiveHostOrClockToken(node.EmptyState?.Title) ||
            ContainsLiveHostOrClockToken(node.EmptyState?.Subtitle))
        {
            return true;
        }

        for (int i = 0; i < node.Spans.Count; i++)
        {
            if (ContainsLiveHostOrClockToken(node.Spans[i].Text) ||
                ContainsLiveHostOrClockToken(node.Spans[i].Color))
            {
                return true;
            }
        }

        if (SubtreeReferencesLiveHostTokens(node.ItemTemplate) ||
            SubtreeReferencesLiveHostTokens(node.ActionButton) ||
            SubtreeReferencesLiveHostTokens(node.Pane))
        {
            return true;
        }

        for (int i = 0; i < node.HoverActions.Count; i++)
        {
            if (SubtreeReferencesLiveHostTokens(node.HoverActions[i]))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (SubtreeReferencesLiveHostTokens(node.Children[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsLiveHostOrClockToken(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        return raw.Contains("$clock", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$sys", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$weather", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$media", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$ai", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$device", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$clipboard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$storage", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$todo", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("$transform", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("settings.", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("now(", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("relativeTime(", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("tz(", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("countdownDays(", StringComparison.OrdinalIgnoreCase);
    }

    internal Brush BuildRadialGradientBrush(
        CustomWidgetRadialGradientSpec gradientSpec,
        CustomWidgetEvaluationContext ctx)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(Math.Clamp(gradientSpec.CenterX, 0.0, 1.0), Math.Clamp(gradientSpec.CenterY, 0.0, 1.0)),
            GradientOrigin = new Point(Math.Clamp(gradientSpec.CenterX, 0.0, 1.0), Math.Clamp(gradientSpec.CenterY, 0.0, 1.0)),
            RadiusX = Math.Clamp(gradientSpec.RadiusX, 0.05, 2.0),
            RadiusY = Math.Clamp(gradientSpec.RadiusY, 0.05, 2.0)
        };

        foreach (CustomWidgetGradientStopSpec stop in gradientSpec.Stops)
        {
            string resolvedStopColor = CustomWidgetExpressionEvaluator.Interpolate(stop.Color, ctx).Trim();
            Color parsed = TryParseRawColor(resolvedStopColor, out Color raw)
                ? raw
                : ResolveSemanticBrush(resolvedStopColor).Color;
            brush.GradientStops.Add(new GradientStop
            {
                Offset = Math.Clamp(stop.Offset, 0.0, 1.0),
                Color = parsed
            });
        }

        return brush;
    }

    private FrameworkElement ApplyMaterialAndAspectRatio(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.Backdrop) &&
            !string.Equals(spec.Backdrop, "none", StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> mode = spec.Backdrop.AsSpan().Trim();
            Brush glassBrush = CreateBackdropMaterialBrush(mode);
            Brush glassBorder = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(26, 255, 255, 255));

            if (element is Border b)
            {
                if (b.Background is null ||
                    (string.IsNullOrWhiteSpace(spec.Background) ||
                     TryParseRawColor(CustomWidgetExpressionEvaluator.Interpolate(spec.Background, ctx).Trim(), out Color rawBg) && rawBg.A > 0))
                {
                    b.Background = glassBrush;
                }
                if (string.IsNullOrWhiteSpace(spec.BorderColor))
                {
                    b.BorderBrush = glassBorder;
                    b.BorderThickness = new Thickness(1);
                }
            }
            else
            {
                element = new Border
                {
                    CornerRadius = CornerMedium,
                    Background = glassBrush,
                    BorderBrush = glassBorder,
                    BorderThickness = new Thickness(1),
                    Child = element
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.Shadow) &&
            !string.Equals(spec.Shadow, "none", StringComparison.OrdinalIgnoreCase))
        {
            float zDepth = string.Equals(spec.Shadow, "md", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(spec.Shadow, "lg", StringComparison.OrdinalIgnoreCase)
                ? 16f
                : 8f;
            try
            {
                element.Shadow = new ThemeShadow();
                element.Translation = new Vector3(0, 0, zDepth);
            }
            catch
            {
                // Headless test fallback when Composition shadow infrastructure is inactive.
            }
        }

        if (spec.AspectRatio is > 0.05 and <= 20.0 && !spec.Height.HasValue)
        {
            double ratio = spec.AspectRatio.Value;
            element.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 4)
                {
                    double targetH = Math.Round(e.NewSize.Width / ratio, 1);
                    if (double.IsNaN(element.Height) || Math.Abs(element.Height - targetH) > 0.5)
                    {
                        element.Height = targetH;
                    }
                }
            };
        }

        return element;
    }

    private Brush CreateBackdropMaterialBrush(ReadOnlySpan<char> mode)
    {
        if (mode.Equals("acrylictint", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("acrylic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new AcrylicBrush
                {
                    TintColor = IsDark ? Color.FromArgb(255, 24, 28, 36) : Color.FromArgb(255, 248, 250, 252),
                    TintOpacity = IsDark ? 0.52 : 0.62,
                    FallbackColor = IsDark ? Color.FromArgb(168, 24, 28, 36) : Color.FromArgb(188, 248, 250, 252)
                };
            }
            catch
            {
            }
        }

        return GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(172, 255, 255, 255));
    }

    private static TransitionCollection? CreateRepositionTransitions()
    {
        if (!FluentMotionHelper.AreAnimationsEnabled)
        {
            return null;
        }

        return
        [
            new RepositionThemeTransition()
        ];
    }

    private static void AttachInteractiveSurfaceFeedback(Border surface, UIElement? hoverRevealTarget = null)
    {
        if (!FluentMotionHelper.AreAnimationsEnabled)
        {
            return;
        }

        surface.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) };
        if (hoverRevealTarget is not null)
        {
            hoverRevealTarget.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) };
        }

        surface.PointerPressed += (_, _) => FluentMotionHelper.AnimateHoverScale(surface, 0.985f, durationMs: 95);
        surface.PointerReleased += (_, _) => FluentMotionHelper.AnimateHoverScale(surface, 1.0f, durationMs: 140);
        surface.PointerCanceled += (_, _) => FluentMotionHelper.AnimateHoverScale(surface, 1.0f, durationMs: 140);
        surface.PointerExited += (_, _) => FluentMotionHelper.AnimateHoverScale(surface, 1.0f, durationMs: 140);
    }

    internal static void UpdateArcPathInPlace(
        Microsoft.UI.Xaml.Shapes.Path arcPath,
        double centerX,
        double centerY,
        double radius,
        double startAngleDeg,
        double sweepAngleDeg,
        bool allowFullCircleEllipse = false)
    {
        if (allowFullCircleEllipse && sweepAngleDeg >= 359.9)
        {
            if (arcPath.Visibility != Visibility.Visible)
            {
                arcPath.Visibility = Visibility.Visible;
            }
            if (arcPath.Data is EllipseGeometry existingEllipse)
            {
                existingEllipse.Center = new Point(centerX, centerY);
                existingEllipse.RadiusX = radius;
                existingEllipse.RadiusY = radius;
            }
            else
            {
                arcPath.Data = new EllipseGeometry
                {
                    Center = new Point(centerX, centerY),
                    RadiusX = radius,
                    RadiusY = radius
                };
            }
            return;
        }

        double maxClamp = allowFullCircleEllipse ? 359.9 : 359.95;
        double minThreshold = allowFullCircleEllipse ? 0.2 : 0.15;
        double clampedSweep = Math.Clamp(sweepAngleDeg, 0, maxClamp);
        if (clampedSweep <= minThreshold)
        {
            if (arcPath.Visibility != Visibility.Collapsed)
            {
                arcPath.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (arcPath.Visibility != Visibility.Visible)
        {
            arcPath.Visibility = Visibility.Visible;
        }

        double startRad = startAngleDeg * (Math.PI / 180.0);
        double endRad = (startAngleDeg + clampedSweep) * (Math.PI / 180.0);
        var startPt = new Point(centerX + (radius * Math.Cos(startRad)), centerY + (radius * Math.Sin(startRad)));
        var endPt = new Point(centerX + (radius * Math.Cos(endRad)), centerY + (radius * Math.Sin(endRad)));
        var arcSize = new Size(radius, radius);
        bool isLargeArc = clampedSweep > 180.0;

        if (arcPath.Data is PathGeometry existingPg &&
            existingPg.Figures.Count == 1 &&
            existingPg.Figures[0].Segments.Count == 1 &&
            existingPg.Figures[0].Segments[0] is ArcSegment existingArc)
        {
            PathFigure existingFig = existingPg.Figures[0];
            existingFig.StartPoint = startPt;
            existingArc.Point = endPt;
            existingArc.Size = arcSize;
            existingArc.IsLargeArc = isLargeArc;
            existingArc.SweepDirection = SweepDirection.Clockwise;
            return;
        }

        var fig = new PathFigure
        {
            StartPoint = startPt,
            IsClosed = false,
            IsFilled = false
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = endPt,
            Size = arcSize,
            RotationAngle = 0,
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        var geom = new PathGeometry();
        geom.Figures.Add(fig);
        arcPath.Data = geom;
    }

    private static void AnimateGaugeSweep(
        Microsoft.UI.Xaml.Shapes.Path activePath,
        ref double currentSweep,
        double targetSweep,
        double centerX,
        double centerY,
        double radius,
        double startAngleDeg,
        int[] animVersionBox,
        bool allowFullCircleEllipse = false)
    {
        if (Math.Abs(targetSweep - currentSweep) < 0.01 && activePath.Data is not null)
        {
            return;
        }

        double startSweep = currentSweep;
        currentSweep = targetSweep;
        int myVersion = ++animVersionBox[0];

        if (!FluentMotionHelper.AreAnimationsEnabled ||
            activePath.XamlRoot is null ||
            Math.Abs(targetSweep - startSweep) < 1.5)
        {
            UpdateArcPathInPlace(activePath, centerX, centerY, radius, startAngleDeg, targetSweep, allowFullCircleEllipse);
            return;
        }

        Microsoft.UI.Dispatching.DispatcherQueue? dq = activePath.DispatcherQueue;
        if (dq is null)
        {
            UpdateArcPathInPlace(activePath, centerX, centerY, radius, startAngleDeg, targetSweep, allowFullCircleEllipse);
            return;
        }

        _ = RunGaugeSweepFramesAsync(
            activePath,
            startSweep,
            targetSweep,
            centerX,
            centerY,
            radius,
            startAngleDeg,
            allowFullCircleEllipse,
            dq,
            animVersionBox,
            myVersion);

        static async Task RunGaugeSweepFramesAsync(
            Microsoft.UI.Xaml.Shapes.Path targetPath,
            double from,
            double to,
            double cx,
            double cy,
            double r,
            double startDeg,
            bool fullCircleEllipse,
            Microsoft.UI.Dispatching.DispatcherQueue queue,
            int[] versionBox,
            int expectedVersion)
        {
            const int steps = 9;
            for (int step = 1; step <= steps; step++)
            {
                await Task.Delay(18);
                if (versionBox[0] != expectedVersion || targetPath.XamlRoot is null)
                {
                    return;
                }

                double t = (double)step / steps;
                double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
                double sweep = from + ((to - from) * eased);
                queue.TryEnqueue(() =>
                {
                    if (versionBox[0] == expectedVersion && targetPath.XamlRoot is not null)
                    {
                        UpdateArcPathInPlace(targetPath, cx, cy, r, startDeg, sweep, fullCircleEllipse);
                    }
                });
            }
        }
    }

    private static void TryAnimateTextChange(TextBlock tb)
    {
        try
        {
            if (tb.XamlRoot is null || !FluentMotionHelper.AreAnimationsEnabled)
            {
                return;
            }

            ElementCompositionPreview.SetIsTranslationEnabled(tb, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(tb);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f),
                new System.Numerics.Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(0f, new System.Numerics.Vector3(0f, -3f, 0f));
            slideAnim.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, decelerate);
            slideAnim.Duration = TimeSpan.FromMilliseconds(150);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, 0.38f);
            opacityAnim.InsertKeyFrame(1f, 1f, decelerate);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(150);

            visual.StartAnimation("Translation", slideAnim);
            visual.StartAnimation("Opacity", opacityAnim);
        }
        catch
        {
        }
    }
}
