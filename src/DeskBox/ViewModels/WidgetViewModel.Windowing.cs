using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DeskBox.Models;

namespace DeskBox.ViewModels;

/// <summary>
/// Render-window projection for large folders. The XAML items controls bind
/// <see cref="RenderedItems"/>, a prefix of <see cref="WidgetViewModel.VisibleItems"/>.
/// Folders at or below the activation threshold render everything (identical
/// to the previous full-list binding); larger folders render incrementally so
/// opening them cannot lay out thousands of tiles at once. Metadata hydration
/// follows the same prefix, so icons, folder counts, shortcut targets, and
/// shell kinds are only resolved for items the user can actually see.
///
/// State model: <see cref="_renderWindowBudget"/> is the persistent large-folder
/// window budget and only ever grows (except on an explicit
/// <see cref="ResetRenderWindow"/> for folder navigation). The effective target
/// is computed per reconcile — full below the threshold, clamped to the budget
/// above it. Writing the rendered count of a small folder back into the budget
/// used to freeze a later bulk import at that small prefix with no scrollbar.
/// </summary>
public partial class WidgetViewModel
{
    private const int RenderWindowActivationThreshold = 300;
    private const int RenderWindowInitialSize = 30;
    private const int RenderWindowGrowChunk = 200;

    public ObservableCollection<WidgetItem> RenderedItems { get; } = [];

    private int _renderWindowBudget = RenderWindowInitialSize;
    private bool _renderWindowReconcileQueued;
    private bool _pendingPostBatchHydration;
    private bool _pendingHealAfterActiveHydration;
    private string? _openPopoverStackKey;
    private bool _isIconHydrationDormant;
    private bool _activeHydrationSkippedIconsWhileDormant;

    /// <summary>
    /// Hydration snapshots the rendered prefix synchronously at startup. When a
    /// caller starts hydration immediately after mutating <see cref="Items"/>
    /// (such as <see cref="LoadFolderContentsAsync"/> during initial load or the
    /// first cold-start group tab switch), the stack display rebuild and render
    /// window reconcile may still be sitting in the dispatcher queue. Flushing
    /// them synchronously here guarantees the first hydration pass sees the
    /// actual rendered items instead of an empty pre-reconcile prefix.
    /// </summary>
    internal void EnsureProjectionAndRenderWindowSynchronizedForHydration()
    {
        if (_isDisposed ||
            _itemMutationBatchDepth > 0 ||
            _deferredStackRebuildDepth > 0)
        {
            return;
        }

        if (UsesStackProjection && (!_hasBuiltStackDisplay || _stackRebuildQueued))
        {
            RebuildStackDisplayItems();
        }

        if (_renderWindowReconcileQueued ||
            RenderedItems.Count != EffectiveRenderTarget)
        {
            _renderWindowReconcileQueued = false;
            ReconcileRenderWindow();
            RenderWindowSourceChanged?.Invoke();
        }
    }

    /// <summary>
    /// Fired (coalesced per dispatcher pass, and deferred to the batch
    /// finalization for bulk imports) after the rendered prefix reconciled
    /// against a source change. The view listens to re-check viewport
    /// coverage: bulk imports change the item count without changing any
    /// geometry, so Loaded/SizeChanged alone cannot keep the window covering
    /// the viewport across the activation threshold.
    /// </summary>
    internal event Action? RenderWindowSourceChanged;

    private int VisibleItemCount => UsesStackProjection
        ? _stackDisplayItems.Count
        : Items.Count;

    private int EffectiveRenderTarget =>
        DeriveRenderTarget(_renderWindowBudget, VisibleItemCount);

    /// <summary>
    /// Pure target derivation shared with behavior tests: folders at or below
    /// the activation threshold render in full; larger folders render the
    /// budget, clamped to the item count. Deriving the target per reconcile —
    /// instead of writing the rendered count of a small folder back into the
    /// budget — is what lets a later bulk import grow past that prefix.
    /// </summary>
    internal static int DeriveRenderTarget(int budget, int visibleItemCount)
    {
        if (visibleItemCount <= RenderWindowActivationThreshold)
        {
            return visibleItemCount;
        }

        return Math.Min(budget, visibleItemCount);
    }

    internal bool CanGrowRenderWindow => EffectiveRenderTarget < VisibleItemCount;

    /// <summary>
    /// Items eligible for metadata hydration (icons, folder counts, shortcut
    /// targets, shell kinds). Stack grouping reads <c>ShellKind</c> from every
    /// item, so the full list stays eligible while stacks are enabled;
    /// otherwise a windowed folder hydrates only its rendered prefix and
    /// picks the remaining items up as the window grows.
    /// </summary>
    internal IEnumerable<WidgetItem> HydrationUniverseItems => UsesStackProjection
        ? Items
        : RenderedItems;

    internal void SetOpenPopoverStackKey(string? stackKey)
    {
        if (string.Equals(_openPopoverStackKey, stackKey, StringComparison.Ordinal))
        {
            return;
        }

        _openPopoverStackKey = stackKey;
        if (!string.IsNullOrWhiteSpace(stackKey) &&
            FindStackByKey(stackKey) is { } stack &&
            stack.Members.Any(member =>
                member.Icon is null ||
                (member.IsFolder && !member.IsFolderItemCountLoaded) ||
                (member.IsShortcut && !member.IsShortcutTargetLoaded)) &&
            IsItemHydrationActive)
        {
            StartItemHydration();
        }
    }

    internal void ResumeIconHydrationFromDormancy() =>
        _isIconHydrationDormant = false;

    internal IEnumerable<WidgetItem> ResolveIconHydrationItems(
        IEnumerable<WidgetItem> fallbackUniverse)
    {
        if (_isIconHydrationDormant)
        {
            return [];
        }

        if (!UsesStackProjection)
        {
            return fallbackUniverse;
        }

        HashSet<WidgetItem> displayed = ComputeDisplayedIconKeepSet(RenderedItems);
        if (!string.IsNullOrWhiteSpace(_openPopoverStackKey) &&
            FindStackByKey(_openPopoverStackKey) is { } popoverStack)
        {
            foreach (WidgetItem member in popoverStack.Members)
            {
                displayed.Add(member);
            }
        }

        return Items.Where(displayed.Contains);
    }

    internal bool HasMissingTargetIcons() =>
        ResolveIconHydrationItems(HydrationUniverseItems)
            .Any(item => item.Icon is null);

    internal IEnumerable<WidgetItem> ResolveDisplayedMetadataHydrationItems(
        IEnumerable<WidgetItem> fallbackUniverse)
    {
        if (!UsesStackProjection)
        {
            return fallbackUniverse;
        }

        HashSet<WidgetItem> displayed = ComputeDisplayedIconKeepSet(RenderedItems);
        if (!string.IsNullOrWhiteSpace(_openPopoverStackKey) &&
            FindStackByKey(_openPopoverStackKey) is { } popoverStack)
        {
            foreach (WidgetItem member in popoverStack.Members)
            {
                displayed.Add(member);
            }
        }

        return Items.Where(displayed.Contains);
    }

    internal IEnumerable<WidgetItem> ResolveFolderCountHydrationItems(
        IEnumerable<WidgetItem> fallbackUniverse) =>
        ResolveDisplayedMetadataHydrationItems(fallbackUniverse);

    internal IEnumerable<WidgetItem> ResolveShortcutHydrationItems(
        IEnumerable<WidgetItem> fallbackUniverse) =>
        ResolveDisplayedMetadataHydrationItems(fallbackUniverse);

    internal IEnumerable<WidgetItem> ResolveShellKindHydrationItems(
        IEnumerable<WidgetItem> fallbackUniverse)
    {
        if (UsesStackProjection &&
            _settingsService.Settings.FileWidget.FileStackAutoStacking &&
            string.Equals(
                FileStackGroupBy,
                Services.SettingsService.FileStackGroupByKind,
                StringComparison.Ordinal))
        {
            return fallbackUniverse.Where(item => !item.IsFolder);
        }

        return ResolveDisplayedMetadataHydrationItems(fallbackUniverse);
    }

    internal bool HasMissingDisplayedItemMetadata() =>
        ResolveDisplayedMetadataHydrationItems(HydrationUniverseItems)
            .Any(item =>
                (item.IsFolder && !item.IsFolderItemCountLoaded) ||
                (item.IsShortcut && !item.IsShortcutTargetLoaded));

    private void AttachRenderWindowTracking()
    {
        Items.CollectionChanged += OnItemsChangedForRenderWindow;
        _stackDisplayItems.CollectionChanged += OnItemsChangedForRenderWindow;
    }

    private void OnItemsChangedForRenderWindow(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        QueueRenderWindowReconcile();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(VisibleItems))
        {
            QueueRenderWindowReconcile();
        }
    }

    private void QueueRenderWindowReconcile()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_itemMutationBatchDepth > 0)
        {
            // A bulk import defers this to the batch finalization; per-insert
            // reconciles would re-mirror the prefix once per dispatcher pass
            // and log a line each.
            MarkItemMutationBatchDirty();
            return;
        }

        if (_renderWindowReconcileQueued)
        {
            return;
        }

        _renderWindowReconcileQueued = true;
        void ApplyRenderWindowReconcile()
        {
            _renderWindowReconcileQueued = false;
            if (_isDisposed)
            {
                return;
            }

            ReconcileRenderWindow();
            RenderWindowSourceChanged?.Invoke();
            if (_pendingPostBatchHydration)
            {
                // Hydration snapshots HydrationUniverseItems synchronously at
                // startup, so the batch finalization defers its start to here
                // — after this callback has applied the settled prefix —
                // instead of starting it against the stale one at scope exit.
                _pendingPostBatchHydration = false;
                StartItemHydration();
            }
            else if (!_isIconHydrationDormant)
            {
                // Acquisition half of the release/acquire pairing: icons
                // released while nothing displayed them come back as their
                // items re-enter the display set. Stack expansion folds
                // members into the projection here, which is the model-level
                // equivalent of a recycled container being realized again.
                HealReleasedItemIconsIfIdle();
            }
        }

        if (_dispatcherQueue is null)
        {
            ApplyRenderWindowReconcile();
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => ApplyRenderWindowReconcile()))
        {
            // The queued flag (and a deferred hydration start with it) must
            // not survive a failed enqueue, or the widget would never
            // reconcile or hydrate again. Only reachable at dispatcher
            // shutdown.
            _renderWindowReconcileQueued = false;
            _pendingPostBatchHydration = false;
        }
    }

    /// <summary>
    /// Defers a hydration start to the queued render reconcile callback. The
    /// queues are enqueue-only and hydration snapshots the rendered prefix
    /// synchronously, so starting hydration at the call site would read the
    /// prefix the reconcile has not applied yet.
    /// </summary>
    private void QueuePostBatchHydration()
    {
        if (_isDisposed)
        {
            return;
        }

        _pendingPostBatchHydration = true;
        QueueRenderWindowReconcile();
    }

    /// <summary>
    /// Called when the browsed folder itself changes (navigating into or out
    /// of a folder) so a window grown in a previous folder does not carry
    /// over. Folder refreshes intentionally keep the grown window.
    /// </summary>
    internal void ResetRenderWindow()
    {
        _renderWindowBudget = RenderWindowInitialSize;
        ReconcileRenderWindow();
    }

    /// <summary>
    /// Raises the budget so the rendered prefix covers a viewport-sized page.
    /// Strictly monotonic: a request below the current budget (including a
    /// page computed before any items loaded) never shrinks it, and only
    /// <see cref="ResetRenderWindow"/> may lower the budget.
    /// </summary>
    internal void EnsureRenderWindowCoversViewport(int minimumCount)
    {
        if (_isDisposed || minimumCount <= _renderWindowBudget)
        {
            return;
        }

        _renderWindowBudget = minimumCount;
        ReconcileRenderWindow();
        StartItemHydration();
    }

    /// <summary>
    /// The smallest render window that fills a viewport: enough columns for
    /// the width, enough rows for the height plus two buffer rows so the
    /// scrollbar has extent to grow from. Pure so tests can pin it.
    /// </summary>
    internal static int ComputeViewportRenderMinimum(
        double viewportWidth,
        double viewportHeight,
        double itemWidth,
        double itemHeight,
        int bufferRows)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 ||
            itemWidth <= 0 || itemHeight <= 0)
        {
            return RenderWindowInitialSize;
        }

        int columns = (int)Math.Ceiling(viewportWidth / itemWidth);
        int rows = (int)Math.Ceiling(viewportHeight / itemHeight) + bufferRows;
        return Math.Max(RenderWindowInitialSize, columns * Math.Max(1, rows));
    }

    internal void GrowRenderWindow(int chunk = RenderWindowGrowChunk)
    {
        if (_isDisposed || !CanGrowRenderWindow)
        {
            return;
        }

        _renderWindowBudget = _renderWindowBudget + Math.Max(1, chunk);
        ReconcileRenderWindow();
        // Hydration skips items that already resolved, so this pass only
        // picks up the newly rendered prefix members — and, under the stack
        // projection, any member whose icon was released while folded.
        StartItemHydration();
    }

    /// <summary>
    /// Expands the window just far enough to cover <paramref name="item"/>
    /// (used before scrolling an item into view), aligned up to a grow chunk.
    /// The window size, not the whole folder, bounds the layout cost: a reveal
    /// into a thousand-item folder must not realize every tile.
    /// </summary>
    internal void EnsureItemRendered(WidgetItem item)
    {
        if (_isDisposed || RenderedItems.Contains(item) || !CanGrowRenderWindow)
        {
            return;
        }

        int targetIndex = IndexInVisibleItems(item);
        if (targetIndex < 0)
        {
            // Reachable in the stack projection when the item is folded into a
            // collapsed stack; there is nothing to render until it expands.
            return;
        }

        _renderWindowBudget = Math.Max(
            _renderWindowBudget,
            ComputeRenderWindowTargetCount(
                targetIndex,
                EffectiveRenderTarget,
                VisibleItemCount));
        ReconcileRenderWindow();
        StartItemHydration();
    }

    private int IndexInVisibleItems(WidgetItem item)
    {
        int index = 0;
        foreach (WidgetItem candidate in VisibleItems)
        {
            if (ReferenceEquals(candidate, item))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    /// <summary>
    /// Window size that just covers <paramref name="itemIndex"/>, aligned up
    /// to a grow chunk so nearby reveals do not re-trigger growth. Unknown
    /// indices (<c>-1</c>) keep the current window.
    /// </summary>
    internal static int ComputeRenderWindowTargetCount(
        int itemIndex,
        int currentCount,
        int visibleCount)
    {
        if (itemIndex < 0)
        {
            return currentCount;
        }

        int requiredCount = itemIndex + 1;
        int aligned =
            ((requiredCount + RenderWindowGrowChunk - 1) / RenderWindowGrowChunk) *
            RenderWindowGrowChunk;
        return Math.Min(
            visibleCount,
            Math.Max(currentCount + 1, aligned));
    }

    private void ReconcileRenderWindow()
    {
        ReconcileRenderWindowPrefix(
            VisibleItems,
            RenderedItems,
            EffectiveRenderTarget,
            VisibleItemCount);
        if (VisibleItemCount > RenderWindowActivationThreshold)
        {
            // Verbose since 1.5.4: one line per reconcile on large folders
            // dominated the log during the batch-operation memory work.
            // Set DESKBOX_VERBOSE_LOG=1 to bring back the dead-end
            // ("files exist but nothing scrolls") diagnostics.
            App.LogVerbose(
                $"[RenderWindow] visible={VisibleItemCount} " +
                $"rendered={RenderedItems.Count} budget={_renderWindowBudget}");
        }
    }

    /// <summary>
    /// Pure prefix mirror shared with behavior tests: renders the first
    /// <paramref name="renderTarget"/> visible items in place (move/insert/
    /// remove by index, never a Reset). The caller derives
    /// <paramref name="renderTarget"/> from the threshold rule and the
    /// budget; nothing here writes the budget back.
    /// </summary>
    internal static void ReconcileRenderWindowPrefix(
        IEnumerable<WidgetItem> visibleItems,
        ObservableCollection<WidgetItem> renderedItems,
        int renderTarget,
        int visibleItemCount)
    {
        int targetCount = Math.Min(renderTarget, visibleItemCount);
        var desired = new List<WidgetItem>(targetCount);
        int collected = 0;
        foreach (WidgetItem item in visibleItems)
        {
            if (collected >= targetCount)
            {
                break;
            }

            desired.Add(item);
            collected++;
        }

        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            WidgetItem desiredItem = desired[targetIndex];
            if (targetIndex < renderedItems.Count &&
                ReferenceEquals(renderedItems[targetIndex], desiredItem))
            {
                continue;
            }

            int existingIndex = IndexOfReference(
                renderedItems,
                desiredItem,
                targetIndex + 1);
            if (existingIndex >= 0)
            {
                renderedItems.Move(existingIndex, targetIndex);
            }
            else
            {
                renderedItems.Insert(
                    Math.Min(targetIndex, renderedItems.Count),
                    desiredItem);
            }
        }

        while (renderedItems.Count > desired.Count)
        {
            renderedItems.RemoveAt(renderedItems.Count - 1);
        }
    }
}
