// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Windows.Graphics;

namespace DeskBox.Views;

internal enum WidgetCompactState
{
    Expanded,
    Collapsed,
    ExpandPending,
    Expanding,
    ExpandedTransient,
    ExpandedPinned,
    Interacting,
    DropExpanded,
    CollapsePending,
    Collapsing
}

internal enum WidgetCompactWarmupSliceResult
{
    Blocked,
    Progressed,
    RenderCommitStarted,
    Ready
}

public abstract partial class WidgetWindowBase
{
    private const int SmartCollapseProbeMs = 220;
    private const int DragRestoreDelayMs = 420;
    // The overlay drag bar's collapse cue tracks the physical cursor: entry
    // hints are verified against GetCursorPos (Windows delivers spurious
    // WM_MOUSEMOVE with shifted client coordinates while the widget morphs
    // its bounds, so event coordinates cannot be trusted in either
    // direction), and the restore waits out a short outside-streak so
    // boundary jitter cannot flap the arrow.
    private const int OverlayDragCuePollIntervalMs = 150;
    private const int OverlayDragCueRestoreOutsideTicks = 2;
    private const double OverlayDragCueSideToleranceDips = 10;
    private const double OverlayDragCueBottomToleranceDips = 20;
    private const int CompactExpansionUrgentWarmupInitialDelayMs = 16;
    private const int CompactExpansionWarmupRetryDelayMs = 320;
    private const int CompactExpansionUrgentWarmupRetryDelayMs = 48;
    private const int CompactExpansionWarmupSpacingMs = 24;
    private const int CompactExpansionPrimeNodeBudget = 48;
    private const double CompactExpansionPrimeTimeBudgetMs = 4;
    // The urgent lane (pointer waiting on the capsule, or a hosted-content
    // swap's imminent re-entry) primes with a whole frame budget instead of
    // a quarter of one: a switched-in member's tree primes in one or two
    // slices (~60-90 ms) instead of ~20 (~930 ms at 48 nodes/slice), so the
    // re-entry expand does not sit in the readiness hold while the slice
    // queue drains (2026-10-20 group tab-switch hover delay).
    private const int CompactExpansionPrimeNodeBudgetUrgent = 192;
    private const double CompactExpansionPrimeTimeBudgetUrgentMs = 16;
    private const int CompactHoverRecoveryProbeMs = 120;
    internal const int PostCollapseSoftTrimDelayMs = 15_000;
    internal const int PostCollapseHibernationDelayMs = 90_000;
    private const int MaxPooledOneShotTimers = 12;
    private const int CompactDragSessionRecoveryProbeMs = 120;
    private const int CompactLayerRestoreFallbackMs = 120;
    private static readonly int[] CompactBoundsSettleDelaysMs = [80, 320, 900];
    private static readonly SemaphoreSlim CompactExpansionWarmupGate = new(1, 1);
    private static readonly List<WeakReference<WidgetWindowBase>> CompactHoverRecoveryTargets = [];
    private static DispatcherQueueTimer? s_compactHoverRecoveryTimer;

    private OwnedOneShotDispatcherTimer? _collapseHoverTimer;
    private OwnedOneShotDispatcherTimer? _collapseLeaveTimer;
    private OwnedOneShotDispatcherTimer? _collapseDragRestoreTimer;
    private OwnedOneShotDispatcherTimer? _compactDragSessionRecoveryTimer;
    private OwnedOneShotDispatcherTimer? _compactBoundsSettleTimer;
    private OwnedOneShotDispatcherTimer? _collapseAnimationWatchdogTimer;
    private OwnedOneShotDispatcherTimer? _compactExpansionReadinessDeadlineTimer;
    private OwnedOneShotDispatcherTimer? _compactExpansionReadyCallbackDeadlineTimer;
    private OwnedOneShotDispatcherTimer? _compactLayerRestoreFallbackTimer;
    private OwnedOneShotDispatcherTimer? _deferredContentHydrationFallbackTimer;
    private OwnedOneShotDispatcherTimer? _collapsedSoftTrimTimer;
    private OwnedOneShotDispatcherTimer? _collapsedContentHibernationTimer;
    private OneShotTimerPool<DispatcherQueueTimer>? _oneShotTimerPool;
    private bool _isNativeCompactMouseTracking;
    private bool _nativeCompactMouseActivityQueued;
    private bool _nativeCompactMouseLeaveQueued;
    private DispatcherQueueTimer? _overlayDragCuePollTimer;
    private int _overlayDragCueOutsideStreak;
    private CancellationTokenSource? _compactExpansionWarmupCancellation;
    private IDisposable? _collapseAnimationFrameRegistration;
    private IDisposable? _compactLayerRestoreFrameRegistration;
    private readonly List<Action> _compactExpansionReadyCallbacks = [];
    private RectInt32 _collapseAnimationFrom;
    private RectInt32 _collapseAnimationTo;
    private WidgetCompactExpansionAnchor? _collapseAnimationAnchor;
    private PointInt32 _collapseAnimationPivot;
    private RectInt32? _stableCompactBounds;
    private RectInt32? _expandedInteractionStartBounds;
    private RectInt32? _compactInteractionStartBounds;
    private SizeInt32? _compactArrangementSizeOverride;
    private double? _observedCompactWidth;
    private WidgetCompactPlacement? _observedCompactPlacement;
    private string _observedCompactExpansionDirection =
        SettingsService.WidgetCompactExpansionDirectionAuto;
    private long _collapseAnimationStarted;
    private int _collapseAnimationDurationMs;
    private bool _collapseAnimationRedirectedMidFlight;
    private long _collapseAnimationGeneration;
    private long _compactLayerRestoreGeneration;
    private long _expandedWidgetLayerLeaseGeneration;

    /// <summary>
    /// True while an expanded capsule lease owns this window's layer; derived
    /// hosts must not force-demote the window until the collapse re-beds it.
    /// </summary>
    protected bool HasExpandedWidgetLayerLease => _expandedWidgetLayerLeaseGeneration != 0;

    /// <summary>
    /// Ends the expanded capsule lease without touching the layer. Manager
    /// teardown uses this: the whole group returns to its resting layer even
    /// while a capsule is expanded, so the lease must not outlive the session.
    /// The stale generation is handed back so the manager releases its side of
    /// the lease in the same step; otherwise the manager lease stays active
    /// and keeps every restore funnel gated until this window vanishes.
    /// </summary>
    protected void EndExpandedWidgetLayerLease()
    {
        long generation = _expandedWidgetLayerLeaseGeneration;
        _expandedWidgetLayerLeaseGeneration = 0;
        _isRaisedForExpandedState = false;
        _restoreDesktopLayerAfterExpandedState = false;
        if (generation > 0)
        {
            App.Current.WidgetManager?.TearDownExpandedWidgetLayer(
                HWnd,
                generation,
                "manager-teardown");
        }
    }
    private long _compactExpansionRequestGeneration;
    private PendingCompactExpansion? _pendingCompactExpansion;
    private WidgetCompactAnimationFrameTracker? _compactAnimationFrameTracker;
    private bool _perfWasWarmed;
    private double _perfDeferredHoldMs;
    private double _perfSetStateRefreshMs;
    private double _perfSetStateRaiseMs;
    private double _perfSetStateResolveMs;
    private double _perfFreezeLayoutMs;
    private double _perfBorderAndDpiMs;
    private double _perfPrepareShellMs;
    private double _perfPostClockStartMs;
    private double _perfWorkingSetStartMB;
    private int _perfXamlSizeChanges;
    private int _perfXamlLayouts;
    private int _perfPeakConcurrent;
    private bool _perfLayoutHooksAttached;
    private string? _pendingCompactPerfLine;
    private double _compactWarmupRenderBeginElapsedMs;
    private int _compactWarmupRenderFramesRemaining;
    private TaskCompletionSource<bool>? _compactWarmupFramesCommitted;
    private EventHandler<object>? _compactWarmupFrameHandler;
    private EventHandler<object>? _deferredContentHydrationFrameHandler;
    private long _deferredExpandedContentHydrationGeneration;
    private bool _deferExpandedContentHydrationForActiveTransition;
    private bool _isHoldingBoundsTransitionForDeferredHydration;
    private WidgetCompactTransitionVisualProfile _collapseAnimationVisualProfile =
        WidgetCompactTransitionVisualProfile.Resolve(
            SettingsService.WidgetCompactAnimationSmooth,
            SettingsService.DefaultWidgetCompactAnimationDurationMs,
            true);
    private bool _collapseInitialized;
    private bool _targetCollapsed;
    private bool _dragExpandedFromCollapsed;
    private bool _isCompactDragInside;
    private bool _isPointerOverCompactActions;
    private bool _isPointerOverCompactMoveHandle;
    private bool _suppressSmartExpansionUntilPointerExit;
    private bool _suppressHoverExpansionAfterDragUntilPointerExit;
    private bool _isPointerOverWidget;
    private bool _isPointerOverCompactExpansionZone;
    private string _compactMoveHintText = string.Empty;
    private string _compactExpandHintText = string.Empty;
    private bool _isCollapseAnimationRendering;
    private readonly WidgetAnimationFramePacingPolicy _collapseAnimationPacing = new();
    private RectInt32 _collapseAnimationLastCommittedBounds;
    private bool _isShellTransitionActive;
    private bool _isBoundsInteractionActive;
    private bool _isRaisedForExpandedState;
    private bool _restoreDesktopLayerAfterExpandedState;
    private int _compactLayerRestoreCommittedFrames;
    private bool _isSmartPinnedOpen;
    private bool _isCompactExpansionWarmupRunning;
    private bool _isCompactExpansionWarmupUrgent;
    private bool _isCompactExpansionWarmed;
    private bool _isCompactExpansionVisualTreePrimed;
    private long _compactExpansionWarmupEpoch = -1;
    private long _compactExpansionWorkingSetEpoch = -1;
    private long _hostedContentSwapTimestamp;
    private Queue<DependencyObject>? _compactExpansionPrimeQueue;
    private bool _isCompactHoverRecoveryRegistered;
    private bool _compactInteractionReconcileQueued;
    private long _compactRoutedPointerAuthorityDeadline;
    private bool _collapseHoverTimerAllowsInteractionRegionDwell;
    private int _compactInteractionDepth;
    private int _compactBoundsSettleStage;
    private WidgetCompactState _compactState = WidgetCompactState.Expanded;
    private WidgetCompactViewState _compactViewState = WidgetCompactViewState.Open;
    private WidgetCollapseBehavior _lastEffectiveCollapseBehavior = WidgetCollapseBehavior.System;
    private WidgetCompactExpansionAnchor? _compactExpansionAnchor;

    private sealed record PendingCompactExpansion(
        long Generation,
        bool PersistManualState,
        bool Animate,
        int? DurationMilliseconds,
        bool AllowDuringInteraction,
        bool RequireHoverEligibility,
        bool AllowInteractionRegionDwell,
        long RequestedAtTimestamp);

    private bool IsCompactExpansionReady =>
        !IsCollapsedContentHibernated &&
        WidgetCompactWarmupPolicy.IsExpansionReady(
            _isCompactExpansionWarmed,
            _compactExpansionWarmupEpoch,
            App.MemoryCleanupEpoch);

    private bool IsWarmLiveContentExpansionReady =>
        IsCompactExpansionReady &&
        WidgetShellControl.ShellContent is not null &&
        (IsKeepCapsuleContentWarmEnabled ||
         _compactExpansionWorkingSetEpoch == App.WorkingSetTrimEpoch);

    /// <summary>
    /// True while compact bounds are active or transitioning. Derived windows use this
    /// to preserve the configured expanded width and height during compact movement.
    /// </summary>
    protected bool IsWidgetCollapsedBoundsActive { get; private set; }

    protected bool IsWidgetCollapsed => _targetCollapsed;

    /// <summary>
    /// True while the native window is morphing between compact and expanded
    /// bounds. Derived content hosts use this to defer expensive SizeChanged
    /// work until the final geometry is available.
    /// </summary>
    protected bool IsCompactTransitionActive =>
        _isCollapseAnimationRendering || _isShellTransitionActive;

    protected bool IsCompactBoundsStateActive =>
        IsWidgetCollapsedBoundsActive || _targetCollapsed;

    protected bool IsCompactArrangementDragActive { get; private set; }

    internal WidgetCompactViewState CurrentCompactViewState => _compactViewState;

    internal bool IsSettledCollapsedCapsule =>
        IsWidgetCollapsedBoundsActive &&
        !IsCompactTransitionActive &&
        _targetCollapsed;

    public bool IsCompactArrangementActive => _collapseInitialized && _targetCollapsed;

    public void ApplyCompactArrangement(RectInt32 bounds, bool constrainSize)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyCompactArrangement(bounds, constrainSize));
            return;
        }

        if (IsClosing)
        {
            return;
        }

        _stableCompactBounds = bounds;
        _compactArrangementSizeOverride = constrainSize
            ? new SizeInt32(bounds.Width, bounds.Height)
            : null;
        InvalidateCompactExpansionReadiness();
        ObserveCompactOverrides();
        if (!_collapseInitialized)
        {
            return;
        }

        if (!_targetCollapsed)
        {
            ReanchorExpandedToCompact(bounds, preserveAnchor: false);
            return;
        }

        ApplyCollapsedStateImmediately(collapsed: true);
        QueueCompactExpansionWarmup();
    }

    public void ClearCompactArrangementConstraint()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ClearCompactArrangementConstraint);
            return;
        }

        _compactArrangementSizeOverride = null;
        _stableCompactBounds = null;
        InvalidateCompactExpansionReadiness();
        QueueCompactExpansionWarmup();
    }

    public void PreviewCompactArrangement(RectInt32 bounds)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => PreviewCompactArrangement(bounds));
            return;
        }

        if (IsClosing || !_collapseInitialized || !_targetCollapsed)
        {
            return;
        }

        _stableCompactBounds = bounds;
        _compactArrangementSizeOverride = new SizeInt32(bounds.Width, bounds.Height);
        MoveWindowWithoutPersisting(bounds);
    }

    protected bool BeginCompactArrangementDrag()
    {
        IsCompactArrangementDragActive = IsCompactBoundsStateActive &&
            !IsCompactTransitionActive &&
            App.Current?.WidgetManager?.BeginCapsuleBarDrag(
                Config.Id,
                reorderMember: !WidgetShellControl.IsCompactMoveHandlePress) == true;
        return IsCompactArrangementDragActive;
    }

    protected bool TryMoveCompactArrangement(
        RectInt32 proposedBounds,
        out RectInt32 resolvedBounds)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            return manager.TryMoveCapsuleBar(Config.Id, proposedBounds, out resolvedBounds);
        }

        resolvedBounds = proposedBounds;
        return false;
    }

    protected void CompleteCompactArrangementDrag()
    {
        if (!IsCompactArrangementDragActive)
        {
            return;
        }

        App.Current?.WidgetManager?.CompleteCapsuleBarDrag(Config.Id);
        IsCompactArrangementDragActive = false;
    }

    protected WidgetCollapseBehavior EffectiveCollapseBehavior =>
        WidgetCollapseBehaviorNames.Resolve(
            Config,
            SettingsService.Settings.WidgetCollapseBehavior);

    protected virtual bool SupportsCompactDropExpansion =>
        Config.WidgetKind is WidgetKind.File or WidgetKind.Todo or WidgetKind.QuickCapture;

    protected string ResolveEffectiveCompactContentMode()
    {
        return WidgetCompactPrivacyPolicy.ResolveContentMode(
            SettingsService.Settings.WidgetCompactContentMode,
            SettingsService.Settings.WidgetCompactHideSensitiveContent,
            Config.WidgetKind);
    }

    protected virtual WidgetCompactPresentation CreateCompactPresentation()
    {
        var localization = App.Current.LocalizationService;
        return new WidgetCompactPresentation(
            Config.Name,
            string.Empty,
            WidgetShellControl.TitleGlyph,
            localization.T("Widget.Compact.DropHint"));
    }

    protected virtual Task OnCompactPreviousRequestedAsync() => Task.CompletedTask;

    protected virtual Task OnCompactPrimaryActionRequestedAsync() => Task.CompletedTask;

    protected virtual Task OnCompactPlayPauseRequestedAsync() => Task.CompletedTask;

    protected virtual Task OnCompactNextRequestedAsync() => Task.CompletedTask;

    protected bool TryHandleCompactActivation(KeyRoutedEventArgs e)
    {
        if (e.Handled || !_targetCollapsed ||
            e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) ||
            HasBlockingFlyoutOpen())
        {
            return false;
        }

        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        if (behavior == WidgetCollapseBehavior.Expanded)
        {
            return false;
        }

        _isSmartPinnedOpen = behavior == WidgetCollapseBehavior.Smart;
        SetCollapsedState(
            false,
            persistManualState: behavior == WidgetCollapseBehavior.Click,
            animate: true);
        e.Handled = true;
        return true;
    }

    protected bool TryHandleCompactEscape()
    {
        if (_targetCollapsed || HasBlockingFlyoutOpen())
        {
            return false;
        }

        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        if (behavior is not (WidgetCollapseBehavior.Smart or WidgetCollapseBehavior.Click))
        {
            return false;
        }

        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        _isSmartPinnedOpen = false;
        _suppressSmartExpansionUntilPointerExit = true;
        SetCollapsedState(
            true,
            persistManualState: behavior == WidgetCollapseBehavior.Click,
            animate: true,
            allowDuringInteraction: true);
        return true;
    }

    protected void RefreshCompactPresentation(bool reapplySurfaceState = true)
    {
        if (!_collapseInitialized || IsClosing)
        {
            return;
        }

        WidgetShellControl.SetCompactPresentation(CreateCompactPresentation());
        if (!reapplySurfaceState)
        {
            return;
        }
        if (_targetCollapsed)
        {
            ApplyCompactSurfaceState();
        }
    }

    protected void ExpandWidgetFromHost()
    {
        if (!_collapseInitialized || IsClosing || !_targetCollapsed)
        {
            return;
        }

        SetCollapsedState(
            false,
            persistManualState: false,
            animate: true,
            allowDuringInteraction: true);
    }

    protected void CollapseWidgetFromHost()
    {
        _isSmartPinnedOpen = false;
        _suppressSmartExpansionUntilPointerExit = true;
        _compactInteractionDepth = 0;
        if (EffectiveCollapseBehavior == WidgetCollapseBehavior.Expanded)
        {
            return;
        }

        SetCollapsedState(
            true,
            persistManualState: EffectiveCollapseBehavior == WidgetCollapseBehavior.Click,
            animate: true,
            allowDuringInteraction: true);
    }

    protected void SetCollapseBehaviorOverride(WidgetCollapseBehavior behavior)
    {
        WidgetCollapseBehaviorNames.SetOverride(Config, behavior);
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        App.Current?.WidgetManager?.SetWidgetGroupCollapseBehavior(Config, behavior);
        _isSmartPinnedOpen = false;
        ApplyCompactTooltips();
        ApplyEffectiveCollapseBehavior(animate: true);
        App.Current?.WidgetManager?.RefreshCapsuleBarLayout();
    }

    /// <summary>
    /// Per-widget expansion direction override. Null restores "follow global".
    /// Direction is a transient expansion constraint: it takes effect on the
    /// next expansion and never moves the capsule or an already open panel.
    /// </summary>
    protected void SetCompactExpansionDirectionOverride(string? direction)
    {
        WidgetCompactExpansionDirectionPolicy.SetOverride(Config, direction);
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        ApplyCompactExpansionDirectionChange();
    }

    private void ApplyCompactExpansionDirectionChange()
    {
        _compactExpansionAnchor = null;
        _observedCompactExpansionDirection = EffectiveCompactExpansionDirection;
        CancelPendingCompactExpansion();
        InvalidateCompactExpansionReadiness();
        CancelCompactExpansionWarmup();
        if (_targetCollapsed)
        {
            QueueCompactExpansionWarmup();
        }
    }

    /// <summary>
    /// Computes the first capsule bounds when a widget that has never been
    /// collapsed enters the compact state. Existing placements are preserved.
    /// The derived bounds are deliberately transient (feedback 340): a
    /// capsule placement may only be created by a user placement commit or
    /// by the capsule bar arrangement, never fabricated from the expanded
    /// window corner and persisted — restore-period expanded geometry is not
    /// user intent, and persisting it stranded capsules at scattered panel
    /// corners across restarts. Until the user drags the capsule (or the bar
    /// arrangement assigns a slot), the capsule simply follows the expanded
    /// window's current region.
    /// </summary>
    private void EnsureCompactPlacementFromExpandedBounds()
    {
        if (_targetCollapsed ||
            !UsesCompactExpansionGeometry() ||
            IsClosing ||
            Config.CompactPlacement is not null)
        {
            return;
        }

        DeriveCompactPlacementFromExpandedBounds();
    }

    /// <param name="capturePlacement">
    /// True only for the deliberate user action of entering compact behavior
    /// from always-expanded with an existing placement: that action re-commits
    /// the capsule to the window's current region (legacy semantics, keeps the
    /// visible capsule and the stored placement from diverging). Every other
    /// caller leaves the placement untouched (transient derivation).
    /// </param>
    private void DeriveCompactPlacementFromExpandedBounds(bool capturePlacement = false)
    {
        if (_targetCollapsed ||
            !UsesCompactExpansionGeometry() ||
            IsClosing)
        {
            return;
        }

        InvalidateStableCompactBounds();
        RectInt32 expanded = GetCurrentWindowBounds();
        RectInt32 measuredCompact = GetCompactBounds(expanded);
        WidgetCompactExpansionAnchor sourceAnchor =
            WidgetCompactExpansionCalculator.FromPositionAnchor(Config.PositionAnchor) ??
            WidgetCompactExpansionAnchor.LeftTop;
        WidgetCompactExpansionAnchor anchor =
            WidgetCompactExpansionDirectionPolicy.Apply(
                EffectiveCompactExpansionDirection,
                [sourceAnchor])[0];
        PointInt32 pivot = WidgetCompactExpansionCalculator.GetPivot(expanded, anchor);
        RectInt32 fresh = WidgetCompactExpansionCalculator.CreateBoundsFromPivot(
            pivot,
            new SizeInt32(measuredCompact.Width, measuredCompact.Height),
            anchor);
        _compactExpansionAnchor = anchor;
        if (capturePlacement)
        {
            CaptureCompactPlacement(fresh, persist: true);
            return;
        }

        // Transient visual memo only: no CaptureCompactPlacement here, so a
        // first collapse never fabricates Config.CompactPlacement from the
        // expanded window corner (feedback 340).
        _stableCompactBounds = fresh;
    }

    protected void ResetCompactWidthOverride()
    {
        if (_isDisplayTopologyTransitionActive || Config.CompactWidth is null)
        {
            return;
        }

        Config.CompactWidth = null;
        _stableCompactBounds = null;
        ObserveCompactOverrides();
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        SynchronizeWidgetGroupLayout();

        if (!_collapseInitialized || !_targetCollapsed || IsClosing)
        {
            return;
        }

        RectInt32 current = GetCurrentWindowBounds();
        RectInt32 target = GetCompactBounds(current);
        _stableCompactBounds = target;
        StartBoundsTransition(
            current,
            target,
            collapsed: true,
            durationMs: ResolveCompactTransitionDuration(requestedDurationMs: null));
    }

    protected void BeginCompactInteraction()
    {
        App.NotifyMemoryCleanupActivity();
        _compactInteractionDepth++;
        CancelTimer(ref _collapseLeaveTimer);
        if (UsesSmartCollapseBehavior() && !_targetCollapsed)
        {
            _compactState = WidgetCompactState.Interacting;
        }
        UpdateCompactViewState();
    }

    protected IDisposable AcquireCompactInteraction(string reason)
    {
        BeginCompactInteraction();
        return new CompactInteractionLease(this, reason);
    }

    internal IDisposable AcquirePopoverInteraction(string reason) =>
        AcquireCompactInteraction(reason);

    protected void EndCompactInteraction()
    {
        _compactInteractionDepth = Math.Max(0, _compactInteractionDepth - 1);
        UpdateCompactViewState();
        if (_compactInteractionDepth == 0 &&
            UsesSmartCollapseBehavior() &&
            !_isSmartPinnedOpen &&
            !_isPointerOverWidget &&
            !_isCompactDragInside)
        {
            ScheduleSmartCollapse();
        }

        if (_compactInteractionDepth == 0)
        {
            // Settings and context-menu changes can arrive while a flyout or
            // bounds interaction temporarily blocks a collapse. Reconcile the
            // effective behavior again after that interaction ends instead of
            // dropping the requested state permanently.
            ApplyEffectiveCollapseBehavior(animate: true);

            // A collapse-behavior menu can switch a collapsed widget from
            // Click to Smart while the flyout still owns an interaction. In
            // that case SetCollapsedState intentionally defers work, so arm
            // the native hover fallback as soon as the interaction closes.
            // Without this, the first activating click is what accidentally
            // arms recovery by taking the widget through another collapse.
            StartCompactHoverRecoveryProbe();
            SynchronizeCompactHoverFromCurrentCursor();
            TryScheduleCompactHoverExpansion();
            QueueCompactInteractionReconcile();
        }
    }

    private void QueueCompactInteractionReconcile()
    {
        if (_compactInteractionReconcileQueued || !_collapseInitialized || IsClosing)
        {
            return;
        }

        _compactInteractionReconcileQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _compactInteractionReconcileQueued = false;
            if (!_collapseInitialized || IsClosing || _compactInteractionDepth != 0)
            {
                return;
            }

            if (UsesSmartCollapseBehavior() && !_targetCollapsed)
            {
                // Flyout.Closed fires before the routed pointer state has fully
                // settled. Re-read the native cursor on the following UI turn
                // so Smart cannot remain stranded in its first expanded state.
                _isPointerOverWidget = IsPointerPhysicallyInsideWindow();
                _isPointerOverCompactExpansionZone = false;
                _isPointerOverCompactMoveHandle = false;
                _isPointerOverCompactActions = false;
                _suppressSmartExpansionUntilPointerExit = false;
                UpdateCompactViewState();
            }

            ApplyEffectiveCollapseBehavior(animate: true);
            if (UsesSmartCollapseBehavior() &&
                !_targetCollapsed &&
                !_isSmartPinnedOpen)
            {
                ScheduleSmartCollapse(SmartCollapseProbeMs);
            }
            else if (_targetCollapsed)
            {
                StartCompactHoverRecoveryProbe();
                SynchronizeCompactHoverFromCurrentCursor();
            }
        }))
        {
            _compactInteractionReconcileQueued = false;
        }
    }

    private void ReleaseCompactInteraction(string reason)
    {
        EndCompactInteraction();
        App.Log($"[Compact] Interaction released reason={reason} depth={_compactInteractionDepth}");
    }

    protected void BeginWidgetBoundsInteraction()
    {
        CaptureExpandedPairInteraction();
        _isBoundsInteractionActive = true;
        BeginCompactInteraction();
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
    }

    protected void EndWidgetBoundsInteraction()
    {
        _isBoundsInteractionActive = false;
        EndCompactInteraction();
        UpdateCompactViewState();
        _expandedInteractionStartBounds = null;
        _compactInteractionStartBounds = null;
    }

    protected void InitializeWidgetCollapse()
    {
        if (_collapseInitialized)
        {
            return;
        }

        _collapseInitialized = true;
        WidgetShellControl.CollapseRequested += WidgetShellControl_CollapseRequested;
        WidgetShellControl.ExpandRequested += WidgetShellControl_ExpandRequested;
        WidgetShellControl.OverlayDragCueArmRequested += WidgetShellControl_OverlayDragCueArmRequested;
        WidgetShellControl.CompactBodyExpandRequested += WidgetShellControl_CompactBodyExpandRequested;
        WidgetShellControl.CompactPointerEntered += WidgetShellControl_CompactPointerEntered;
        WidgetShellControl.CompactPointerMoved += WidgetShellControl_CompactPointerMoved;
        WidgetShellControl.CompactPointerExited += WidgetShellControl_CompactPointerExited;
        WidgetShellControl.CompactExpansionPointerEntered += WidgetShellControl_CompactExpansionPointerEntered;
        WidgetShellControl.CompactExpansionPointerExited += WidgetShellControl_CompactExpansionPointerExited;
        WidgetShellControl.CompactPointerPressed += WidgetShellControl_CompactPointerPressed;
        WidgetShellControl.CompactActionPointerEntered += WidgetShellControl_CompactActionPointerEntered;
        WidgetShellControl.CompactActionPointerExited += WidgetShellControl_CompactActionPointerExited;
        WidgetShellControl.CompactMoveHandlePointerEntered += WidgetShellControl_CompactMoveHandlePointerEntered;
        WidgetShellControl.CompactMoveHandlePointerExited += WidgetShellControl_CompactMoveHandlePointerExited;
        WidgetShellControl.CompactDragEntered += WidgetShellControl_CompactDragEntered;
        WidgetShellControl.CompactDragLeft += WidgetShellControl_CompactDragLeft;
        WidgetShellControl.CompactDropCompleted += WidgetShellControl_CompactDropCompleted;
        WidgetShellControl.CompactPreviousRequested += WidgetShellControl_CompactPreviousRequested;
        WidgetShellControl.CompactPrimaryActionRequested += WidgetShellControl_CompactPrimaryActionRequested;
        WidgetShellControl.CompactPlayPauseRequested += WidgetShellControl_CompactPlayPauseRequested;
        WidgetShellControl.CompactNextRequested += WidgetShellControl_CompactNextRequested;
        SettingsService.SettingsChanged += CollapseSettingsChanged;
        App.MemoryCleanupEpochAdvanced += App_MemoryCleanupEpochAdvanced;
        App.Current.LocalizationService.LanguageChanged += CollapseLanguageChanged;

        RefreshCompactPresentation();
        ApplyCompactTooltips();
        ApplyCollapseBehaviorVisuals();
        bool initiallyCollapsed = EffectiveCollapseBehavior switch
        {
            WidgetCollapseBehavior.Smart => true,
            WidgetCollapseBehavior.Click => Config.IsCollapsed,
            _ => false
        };
        ApplyCollapsedStateImmediately(initiallyCollapsed);
        ObserveCompactOverrides();
        QueueCompactExpansionWarmup();
        StartCompactHoverRecoveryProbe();
    }

    protected void CleanupWidgetCollapse()
    {
        if (!_collapseInitialized)
        {
            return;
        }

        _collapseInitialized = false;
        CancelCompactExpansionWarmup();
        StopCompactHoverRecoveryProbe();
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        CancelTimer(ref _collapseDragRestoreTimer);
        CancelTimer(ref _compactDragSessionRecoveryTimer);
        CancelTimer(ref _compactBoundsSettleTimer);
        CancelTimer(ref _collapseAnimationWatchdogTimer);
        CancelPendingCompactExpansion();
        CancelTimer(ref _compactExpansionReadyCallbackDeadlineTimer);
        CancelDeferredExpandedLayerRestore();
        CancelDeferredExpandedContentHydration();
        CancelCollapsedHibernationTimers();
        _oneShotTimerPool?.DrainAll();
        _isNativeCompactMouseTracking = false;
        _nativeCompactMouseActivityQueued = false;
        _nativeCompactMouseLeaveQueued = false;
        ReleaseExpandedWidgetLayerLease("compact-cleanup");
        StopCollapseAnimation();
        WidgetShellControl.CancelResponsiveLayoutTransition();
        WidgetShellControl.HideCompactHint();

        WidgetShellControl.CollapseRequested -= WidgetShellControl_CollapseRequested;
        WidgetShellControl.ExpandRequested -= WidgetShellControl_ExpandRequested;
        WidgetShellControl.OverlayDragCueArmRequested -= WidgetShellControl_OverlayDragCueArmRequested;
        ReleaseOverlayDragCuePollTimer();
        WidgetShellControl.CompactBodyExpandRequested -= WidgetShellControl_CompactBodyExpandRequested;
        WidgetShellControl.CompactPointerEntered -= WidgetShellControl_CompactPointerEntered;
        WidgetShellControl.CompactPointerMoved -= WidgetShellControl_CompactPointerMoved;
        WidgetShellControl.CompactPointerExited -= WidgetShellControl_CompactPointerExited;
        WidgetShellControl.CompactExpansionPointerEntered -= WidgetShellControl_CompactExpansionPointerEntered;
        WidgetShellControl.CompactExpansionPointerExited -= WidgetShellControl_CompactExpansionPointerExited;
        WidgetShellControl.CompactPointerPressed -= WidgetShellControl_CompactPointerPressed;
        WidgetShellControl.CompactActionPointerEntered -= WidgetShellControl_CompactActionPointerEntered;
        WidgetShellControl.CompactActionPointerExited -= WidgetShellControl_CompactActionPointerExited;
        WidgetShellControl.CompactMoveHandlePointerEntered -= WidgetShellControl_CompactMoveHandlePointerEntered;
        WidgetShellControl.CompactMoveHandlePointerExited -= WidgetShellControl_CompactMoveHandlePointerExited;
        WidgetShellControl.CompactDragEntered -= WidgetShellControl_CompactDragEntered;
        WidgetShellControl.CompactDragLeft -= WidgetShellControl_CompactDragLeft;
        WidgetShellControl.CompactDropCompleted -= WidgetShellControl_CompactDropCompleted;
        WidgetShellControl.CompactPreviousRequested -= WidgetShellControl_CompactPreviousRequested;
        WidgetShellControl.CompactPrimaryActionRequested -= WidgetShellControl_CompactPrimaryActionRequested;
        WidgetShellControl.CompactPlayPauseRequested -= WidgetShellControl_CompactPlayPauseRequested;
        WidgetShellControl.CompactNextRequested -= WidgetShellControl_CompactNextRequested;
        SettingsService.SettingsChanged -= CollapseSettingsChanged;
        App.MemoryCleanupEpochAdvanced -= App_MemoryCleanupEpochAdvanced;
        App.Current.LocalizationService.LanguageChanged -= CollapseLanguageChanged;
    }

    private void App_MemoryCleanupEpochAdvanced()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(App_MemoryCleanupEpochAdvanced);
            return;
        }

        // A working-set trim made the primed expansion layout cold again
        // (IsCompactExpansionReady now fails the epoch check). Re-arm the
        // background warm-up so the next hover expand does not hit the
        // readiness deadline; the slice still defers to application idle.
        QueueCompactExpansionWarmup();
    }

    internal bool IsTargetCollapsed => _targetCollapsed;

    private bool IsKeepCapsuleContentWarmEnabled =>
        SettingsService?.Settings.Performance.KeepCapsuleContentWarm == true;

    internal void ReconcileCollapsedContentWarmthPreference()
    {
        if (!_collapseInitialized || !_targetCollapsed || IsClosing)
        {
            return;
        }

        if (IsKeepCapsuleContentWarmEnabled)
        {
            CancelTimer(ref _collapsedContentHibernationTimer);
            QueueCompactExpansionWarmup();
            return;
        }

        if (!HasUserCompactExpansionIntent() && !_isCompactExpansionWarmupUrgent)
        {
            CancelCompactExpansionWarmup();
        }

        ArmCollapsedContentHibernationTimer();
    }

    protected virtual bool IsCollapsedContentHibernated => false;

    protected virtual bool TryHibernateCollapsedContent() => false;

    protected virtual bool EnsureCollapsedContentViewAttached() => false;

    protected virtual void OnCollapsedSoftTrim()
    {
        WidgetShellControl.TrimCollapsedTransientVisuals();
    }

    private void CancelCollapsedHibernationTimers()
    {
        CancelTimer(ref _collapsedSoftTrimTimer);
        CancelTimer(ref _collapsedContentHibernationTimer);
    }

    protected void ArmCollapsedContentHibernationIfSettled()
    {
        if (_collapseInitialized && _targetCollapsed && !IsClosing)
        {
            ArmCollapsedContentHibernationTimer();
            if (IsKeepCapsuleContentWarmEnabled)
            {
                QueueCompactExpansionWarmup();
            }
        }
    }

    private void ArmCollapsedContentHibernationTimer()
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsClosing ||
            IsCollapsedContentHibernated ||
            _isPointerOverWidget ||
            _isCompactDragInside)
        {
            return;
        }

        if (_collapsedSoftTrimTimer is null)
        {
            ScheduleTimer(
                ref _collapsedSoftTrimTimer,
                PostCollapseSoftTrimDelayMs,
                OnCollapsedSoftTrimTimerElapsed);
        }

        if (IsKeepCapsuleContentWarmEnabled)
        {
            CancelTimer(ref _collapsedContentHibernationTimer);
            return;
        }

        if (_collapsedContentHibernationTimer is not null)
        {
            return;
        }

        ScheduleTimer(
            ref _collapsedContentHibernationTimer,
            PostCollapseHibernationDelayMs,
            OnCollapsedContentHibernationTimerElapsed);
    }

    private void OnCollapsedSoftTrimTimerElapsed()
    {
        _collapsedSoftTrimTimer = null;
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsClosing ||
            IsCollapsedContentHibernated ||
            _isPointerOverWidget ||
            _isCompactDragInside ||
            _isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            _isBoundsInteractionActive ||
            _compactInteractionDepth > 0 ||
            HasBlockingFlyoutOpen())
        {
            return;
        }

        OnCollapsedSoftTrim();
    }

    private void OnCollapsedContentHibernationTimerElapsed()
    {
        _collapsedContentHibernationTimer = null;
        if (IsKeepCapsuleContentWarmEnabled)
        {
            return;
        }
        CancelTimer(ref _collapsedSoftTrimTimer);
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsClosing ||
            IsCollapsedContentHibernated ||
            _isPointerOverWidget ||
            _isCompactDragInside ||
            _isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            _isBoundsInteractionActive ||
            _compactInteractionDepth > 0 ||
            HasBlockingFlyoutOpen())
        {
            return;
        }

        CancelCompactExpansionWarmup();
        if (TryHibernateCollapsedContent())
        {
            InvalidateCompactExpansionReadiness();
            App.LogVerbose(
                $"[Compact] Hibernated collapsed content view kind={Config.WidgetKind} id={Config.Id}");
        }
    }

    private bool HasUserCompactExpansionIntent(bool attachHibernated = false) =>
        attachHibernated ||
        _pendingCompactExpansion is not null ||
        _isPointerOverCompactActions ||
        _isPointerOverCompactMoveHandle ||
        (_isPointerOverCompactExpansionZone && UsesSmartCollapseBehavior()) ||
        _isPointerOverWidget;

    private void QueueCompactExpansionWarmup(bool urgent = false, bool attachHibernated = false)
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsCompactExpansionReady ||
            IsClosing)
        {
            return;
        }

        bool keepWarm = IsKeepCapsuleContentWarmEnabled;
        bool hasUserIntent = HasUserCompactExpansionIntent(attachHibernated);
        urgent = urgent && hasUserIntent;
        bool hasPendingReadyCallbacks = _compactExpansionReadyCallbacks.Count > 0;
        if (!urgent && !hasPendingReadyCallbacks && !keepWarm)
        {
            return;
        }

        if (!hasUserIntent && !keepWarm)
        {
            ArmCollapsedContentHibernationTimer();
            return;
        }

        if (IsCollapsedContentHibernated && !attachHibernated)
        {
            if (!keepWarm || urgent)
            {
                // Keep hibernated/cold leaf views detached during hover and visibility
                // wakeups: shell-first expansion morphs immediately at full frame rate
                // and hydrates the leaf view on the stationary expanded panel instead
                // of stalling the UI thread under a moving cursor.
                return;
            }
        }

        if (!keepWarm &&
            urgent &&
            _isPointerOverWidget &&
            _pendingCompactExpansion is null &&
            _compactExpansionWorkingSetEpoch != App.WorkingSetTrimEpoch)
        {
            // After an idle working-set trim, avoid faulting expanded visual pages
            // under pointer hover; shell-first expansion animates smoothly first.
            return;
        }

        CancelCollapsedHibernationTimers();
        if ((urgent || attachHibernated || !IsCollapsedContentHibernated) &&
            EnsureCollapsedContentViewAttached())
        {
            InvalidateCompactExpansionReadiness();
        }

        if (_compactExpansionWarmupCancellation is not null)
        {
            if (!_compactExpansionWarmupCancellation.IsCancellationRequested &&
                (!urgent || _isCompactExpansionWarmupUrgent))
            {
                return;
            }

            // Wake an idle/background warm-up and promote this widget ahead of
            // the remaining cold-start queue. The old run observes cancellation
            // and cannot clear the new CTS.
            _compactExpansionWarmupCancellation.Cancel();
        }

        var cancellation = new CancellationTokenSource();
        _compactExpansionWarmupCancellation = cancellation;
        _isCompactExpansionWarmupUrgent = urgent;
        _ = RunCompactExpansionWarmupAsync(cancellation, urgent);
    }

    private async Task RunCompactExpansionWarmupAsync(
        CancellationTokenSource cancellation,
        bool urgent)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await Task.Delay(
                urgent
                    ? CompactExpansionUrgentWarmupInitialDelayMs
                    : WidgetCompactWarmupSchedulePolicy.GetInitialDelayMilliseconds(
                        Config.WidgetKind),
                token);
            string? lastDeferral = null;
            while (!token.IsCancellationRequested &&
                   _collapseInitialized &&
                   _targetCollapsed &&
                   !IsClosing &&
                   !IsCompactExpansionReady)
            {
                WidgetCompactWarmupSnapshot warmupSnapshot =
                    CaptureCompactExpansionWarmupSnapshot(urgent);
                if (!WidgetCompactWarmupPolicy.CanRun(warmupSnapshot))
                {
                    string deferral =
                        $"urgent={urgent} pointer={_isPointerOverWidget} " +
                        $"interactionDepth={_compactInteractionDepth} " +
                        $"windowVisible={warmupSnapshot.IsWindowVisible} " +
                        $"rootLoaded={RootElement.IsLoaded} " +
                        $"contentReady={IsCompactExpansionWarmupContentReady} " +
                        $"kind={Config.WidgetKind} id={Config.Id}";
                    if (!WidgetCompactWarmupPolicy.ShouldKeepWaiting(warmupSnapshot))
                    {
                        PerformanceLogger.Mark(
                            "CompactExpansionWarmupDeferred",
                            $"action=stop-until-shown {deferral}");
                        return;
                    }

                    if (!string.Equals(deferral, lastDeferral, StringComparison.Ordinal))
                    {
                        lastDeferral = deferral;
                        PerformanceLogger.Mark(
                            "CompactExpansionWarmupDeferred",
                            $"action=retry {deferral}");
                    }

                    await Task.Delay(
                        urgent
                            ? CompactExpansionUrgentWarmupRetryDelayMs
                            : CompactExpansionWarmupRetryDelayMs,
                        token);
                    continue;
                }

                lastDeferral = null;
                bool gateEntered = false;
                WidgetCompactWarmupSliceResult result =
                    WidgetCompactWarmupSliceResult.Blocked;
                try
                {
                    // Background windows never line up behind the semaphore.
                    // This leaves only the active slice in front of a pointer-
                    // promoted urgent warm-up instead of every startup widget.
                    gateEntered = urgent
                        ? await WaitForCompactExpansionWarmupGateAsync(token)
                        : await CompactExpansionWarmupGate.WaitAsync(0, token);
                    if (gateEntered && CanRunCompactExpansionWarmup(urgent))
                    {
                        string warmupReason = urgent ? "visible-critical" : "background-idle";
                        result = await TryRunCompactExpansionWarmupSliceOnUiThreadAsync(
                            warmupReason,
                            token,
                            urgent);
                        if (result == WidgetCompactWarmupSliceResult.RenderCommitStarted)
                        {
                            await WaitForCompactExpansionRenderWarmupFramesAsync(token);
                            result = await CompleteCompactExpansionRenderWarmupOnUiThreadAsync(
                                warmupReason,
                                token,
                                urgent);
                        }
                    }
                }
                finally
                {
                    if (gateEntered)
                    {
                        CompactExpansionWarmupGate.Release();
                    }
                }

                if (result == WidgetCompactWarmupSliceResult.Ready)
                {
                    return;
                }

                await Task.Delay(
                    result == WidgetCompactWarmupSliceResult.Progressed
                        ? urgent
                            ? CompactExpansionUrgentWarmupInitialDelayMs
                            : CompactExpansionWarmupSpacingMs
                        : urgent
                            ? CompactExpansionUrgentWarmupRetryDelayMs
                            : CompactExpansionWarmupRetryDelayMs,
                    token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[CompactWarmup] Failed {Config.Name}#{Config.Id}: {ex.Message}");
        }
        finally
        {
            bool ownsWarmupSlot =
                ReferenceEquals(_compactExpansionWarmupCancellation, cancellation);
            if (ownsWarmupSlot)
            {
                _compactExpansionWarmupCancellation = null;
                _isCompactExpansionWarmupUrgent = false;
            }

            cancellation.Dispose();

            if (ownsWarmupSlot &&
                token.IsCancellationRequested &&
                _collapseInitialized &&
                _targetCollapsed &&
                !IsCompactExpansionReady &&
                !IsClosing &&
                HWnd != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(HWnd))
            {
                DispatcherQueue.TryEnqueue(() => QueueCompactExpansionWarmup());
            }
        }
    }

    private static async Task<bool> WaitForCompactExpansionWarmupGateAsync(
        CancellationToken cancellationToken)
    {
        await CompactExpansionWarmupGate.WaitAsync(cancellationToken);
        return true;
    }

    private bool CanRunCompactExpansionWarmup(bool urgent = false)
    {
        return WidgetCompactWarmupPolicy.CanRun(
            CaptureCompactExpansionWarmupSnapshot(urgent));
    }

    private WidgetCompactWarmupSnapshot CaptureCompactExpansionWarmupSnapshot(
        bool urgent)
    {
        bool applicationIdle = urgent
            ? App.Current?.CanRunCriticalCompactExpansionWarmup == true
            : App.Current?.CanRunCompactExpansionWarmup == true;
        return new WidgetCompactWarmupSnapshot(
            IsCollapseInitialized: _collapseInitialized,
            IsCollapsed: _targetCollapsed && WidgetShellControl.IsCollapsed,
            IsExpansionWarmed: IsCompactExpansionReady,
            IsClosing: IsClosing,
            IsAnimationActive: _isCollapseAnimationRendering || _isShellTransitionActive,
            IsPointerOverWidget: urgent ? false : _isPointerOverWidget,
            HasActiveInteraction:
                _isBoundsInteractionActive ||
                _isCompactDragInside ||
                IsDragging ||
                IsResizing ||
                HasBlockingFlyoutOpen() ||
                (!urgent && _compactInteractionDepth > 0),
            IsWindowVisible: HWnd != IntPtr.Zero && Win32Helper.IsWindowVisible(HWnd),
            IsContentReady: RootElement.IsLoaded && IsCompactExpansionWarmupContentReady,
            IsApplicationIdle: applicationIdle);
    }

    private Task<WidgetCompactWarmupSliceResult>
        TryRunCompactExpansionWarmupSliceOnUiThreadAsync(
        string reason,
        CancellationToken cancellationToken,
        bool urgent)
    {
        var completion = new TaskCompletionSource<WidgetCompactWarmupSliceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueuePriority priority = urgent
            ? DispatcherQueuePriority.High
            : DispatcherQueuePriority.Low;
        if (!DispatcherQueue.TryEnqueue(priority, () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(
                        TryRunCompactExpansionWarmupSlice(reason, urgent));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetResult(WidgetCompactWarmupSliceResult.Blocked);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private WidgetCompactWarmupSliceResult TryRunCompactExpansionWarmupSlice(
        string reason,
        bool urgent)
    {
        if (IsCompactExpansionReady)
        {
            return WidgetCompactWarmupSliceResult.Ready;
        }

        if (!_collapseInitialized ||
            !_targetCollapsed ||
            !WidgetShellControl.IsCollapsed ||
            IsClosing ||
            _isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            // Any capsule's bounds transition owns the shared frame clock; a
            // warm-up layout here would stall its callbacks. This slice runs
            // on the UI thread, so reading coordinator state is safe.
            WidgetCompactAnimationCoordinator.HasActiveAnimations ||
            _isCompactExpansionWarmupRunning ||
            !RootElement.IsLoaded ||
            !IsCompactExpansionWarmupContentReady)
        {
            PerformanceLogger.Mark(
                "CompactExpansionWarmupSliceBlocked",
                $"collapseInitialized={_collapseInitialized} " +
                $"targetCollapsed={_targetCollapsed} " +
                $"shellCollapsed={WidgetShellControl.IsCollapsed} " +
                $"closing={IsClosing} animation={_isCollapseAnimationRendering} " +
                $"shellTransition={_isShellTransitionActive} " +
                $"anyAnimation={WidgetCompactAnimationCoordinator.HasActiveAnimations} " +
                $"running={_isCompactExpansionWarmupRunning} " +
                $"rootLoaded={RootElement.IsLoaded} " +
                $"contentReady={IsCompactExpansionWarmupContentReady} " +
                $"reason={reason} kind={Config.WidgetKind} id={Config.Id}");
            return WidgetCompactWarmupSliceResult.Blocked;
        }

        if (IsCollapsedContentHibernated)
        {
            if (!EnsureCollapsedContentViewAttached())
            {
                return WidgetCompactWarmupSliceResult.Blocked;
            }

            InvalidateCompactExpansionReadiness();
            return WidgetCompactWarmupSliceResult.Progressed;
        }

        if (!_isCompactExpansionVisualTreePrimed)
        {
            PrimeCompactExpansionVisualTree(reason, urgent);
            return WidgetCompactWarmupSliceResult.Progressed;
        }

        _isCompactExpansionWarmupRunning = true;
        long started = Stopwatch.GetTimestamp();
        bool renderCommitArmed = false;
        try
        {
            RectInt32 current = GetCurrentWindowBounds();
            RectInt32 compactBounds = GetStableCompactBounds(current);
            WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compactBounds);
            double dpiScale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
            _ = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
                SettingsService.Settings.WidgetCornerPreference);
            _ = WindowsCompatibilityService.ResolveEffectiveWidgetCompactMediaCornerMode(
                SettingsService.Settings.WidgetCompactMediaCornerMode);

            // Prime compact presentation, WinRT/accessibility setup, and hold the
            // expanded subtree across a real DirectComposition render commit behind
            // the opaque capsule chrome so Loaded, DirectWrite, and DComp visuals
            // realize during idle instead of the first live expansion frame.
            RefreshCompactPresentation();
            _ = ResolveCompactTransitionDuration(requestedDurationMs: null);
            ApplyCompactBorderVisuals();
            bool warmed = WidgetShellControl.BeginCompactExpansionRenderWarmup(
                layout.ExpandedBounds.Width / Math.Max(0.01, dpiScale),
                layout.ExpandedBounds.Height / Math.Max(0.01, dpiScale),
                layout.Anchor);
            if (!warmed)
            {
                PerformanceLogger.Mark(
                    "CompactExpansionWarmupLayoutRejected",
                    $"reason={reason} kind={Config.WidgetKind} id={Config.Id}");
                return WidgetCompactWarmupSliceResult.Blocked;
            }

            _compactWarmupRenderBeginElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AttachCompactExpansionRenderWarmupFrameHook();
            renderCommitArmed = true;
            return WidgetCompactWarmupSliceResult.RenderCommitStarted;
        }
        finally
        {
            if (!renderCommitArmed)
            {
                _isCompactExpansionWarmupRunning = false;
            }
        }
    }

    private void AttachCompactExpansionRenderWarmupFrameHook()
    {
        DetachCompactExpansionRenderWarmupFrameHook();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _compactWarmupFramesCommitted = tcs;
        _compactWarmupRenderFramesRemaining = 2;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (--_compactWarmupRenderFramesRemaining <= 0)
            {
                DetachCompactExpansionRenderWarmupFrameHook();
                tcs.TrySetResult(true);
            }
        };
        _compactWarmupFrameHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private void DetachCompactExpansionRenderWarmupFrameHook()
    {
        if (_compactWarmupFrameHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _compactWarmupFrameHandler = null;
        }

        _compactWarmupFramesCommitted = null;
        _compactWarmupRenderFramesRemaining = 0;
    }

    private async Task WaitForCompactExpansionRenderWarmupFramesAsync(
        CancellationToken cancellationToken)
    {
        Task framesTask = _compactWarmupFramesCommitted?.Task ?? Task.CompletedTask;
        if (framesTask.IsCompleted)
        {
            return;
        }

        var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelSignal);
        await Task.WhenAny(framesTask, Task.Delay(96), cancelSignal.Task);
    }

    private Task<WidgetCompactWarmupSliceResult>
        CompleteCompactExpansionRenderWarmupOnUiThreadAsync(
        string reason,
        CancellationToken cancellationToken,
        bool urgent)
    {
        var completion = new TaskCompletionSource<WidgetCompactWarmupSliceResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueuePriority priority = urgent
            ? DispatcherQueuePriority.High
            : DispatcherQueuePriority.Low;
        if (!DispatcherQueue.TryEnqueue(priority, () =>
            {
                try
                {
                    WidgetCompactWarmupSliceResult result =
                        CompleteCompactExpansionRenderWarmup(reason, cancellationToken.IsCancellationRequested);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        completion.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetResult(WidgetCompactWarmupSliceResult.Blocked);
        }

        return completion.Task;
    }

    private WidgetCompactWarmupSliceResult CompleteCompactExpansionRenderWarmup(
        string reason,
        bool cancelled)
    {
        long endStarted = Stopwatch.GetTimestamp();
        DetachCompactExpansionRenderWarmupFrameHook();
        bool committed = WidgetShellControl.EndCompactExpansionRenderWarmup();
        _isCompactExpansionWarmupRunning = false;

        if (!committed ||
            cancelled ||
            !_collapseInitialized ||
            !_targetCollapsed ||
            IsClosing)
        {
            return WidgetCompactWarmupSliceResult.Blocked;
        }

        _isCompactExpansionWarmed = true;
        _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
        _compactExpansionWorkingSetEpoch = App.WorkingSetTrimEpoch;
        double elapsedMs = _compactWarmupRenderBeginElapsedMs +
            Stopwatch.GetElapsedTime(endStarted).TotalMilliseconds;
        PerformanceLogger.Mark(
            "CompactExpansionWarmup",
            $"elapsedMs={elapsedMs:F1} reason={reason} kind={Config.WidgetKind} id={Config.Id}");
        if (elapsedMs >= 16.7)
        {
            App.Log(
                $"[CompactWarmup] Slow layout kind={Config.WidgetKind} " +
                $"id={Config.Id} elapsedMs={elapsedMs:F1} reason={reason}");
        }
        ArmCollapsedContentHibernationTimer();
        DispatcherQueue.TryEnqueue(OnCompactExpansionReady);
        return WidgetCompactWarmupSliceResult.Ready;
    }

    private void PrimeCompactExpansionVisualTree(string reason, bool urgent)
    {
        if (_compactExpansionPrimeQueue is null)
        {
            _compactExpansionPrimeQueue = new Queue<DependencyObject>();
            _compactExpansionPrimeQueue.Enqueue(WidgetShellControl);
            if (WidgetShellControl.ShellContent is DependencyObject bodyRoot &&
                !ReferenceEquals(bodyRoot, WidgetShellControl))
            {
                _compactExpansionPrimeQueue.Enqueue(bodyRoot);
            }
        }
        long started = Stopwatch.GetTimestamp();
        int nodeBudget = urgent
            ? CompactExpansionPrimeNodeBudgetUrgent
            : CompactExpansionPrimeNodeBudget;
        double timeBudgetMs = urgent
            ? CompactExpansionPrimeTimeBudgetUrgentMs
            : CompactExpansionPrimeTimeBudgetMs;
        int visited = 0;
        while (_compactExpansionPrimeQueue.Count > 0)
        {
            DependencyObject current = _compactExpansionPrimeQueue.Dequeue();
            if (current is Control control)
            {
                control.ApplyTemplate();
            }

            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < childCount; index++)
            {
                _compactExpansionPrimeQueue.Enqueue(
                    VisualTreeHelper.GetChild(current, index));
            }

            visited++;
            if (visited >= nodeBudget ||
                Stopwatch.GetElapsedTime(started).TotalMilliseconds >=
                    timeBudgetMs)
            {
                PerformanceLogger.Mark(
                    "CompactExpansionPrimeSlice",
                    $"completed=false visited={visited} " +
                    $"remaining={_compactExpansionPrimeQueue.Count} " +
                    $"reason={reason} kind={Config.WidgetKind} id={Config.Id}");
                return;
            }
        }

        _compactExpansionPrimeQueue = null;
        _isCompactExpansionVisualTreePrimed = true;
        PerformanceLogger.Mark(
            "CompactExpansionPrimeSlice",
            $"completed=true visited={visited} reason={reason} " +
            $"kind={Config.WidgetKind} id={Config.Id}");
    }

    private void CancelCompactExpansionWarmup()
    {
        _compactExpansionWarmupCancellation?.Cancel();
    }

    private void InvalidateCompactExpansionReadiness()
    {
        _isCompactExpansionWarmed = false;
        _isCompactExpansionVisualTreePrimed = false;
        _compactExpansionWarmupEpoch = -1;
        _compactExpansionPrimeQueue = null;
    }

    /// <summary>
    /// Runs visible-content resume work only after a collapsed window has
    /// rebuilt the layout needed for its next expansion. Callers should still
    /// guard the callback with their own visibility generation.
    /// </summary>
    protected void RunAfterCompactExpansionReady(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsCompactExpansionReady ||
            !HasUserCompactExpansionIntent() ||
            (IsCollapsedContentHibernated && !_isPointerOverCompactExpansionZone))
        {
            DispatcherQueue.TryEnqueue(() => callback());
            return;
        }

        _compactExpansionReadyCallbacks.Add(callback);
        // Visibility resume is background preparation. Only an actual pointer
        // candidate should jump ahead of the startup/wake queue.
        QueueCompactExpansionWarmup(urgent: _isPointerOverCompactExpansionZone);
        if (_compactExpansionReadyCallbackDeadlineTimer is null)
        {
            ScheduleTimer(
                ref _compactExpansionReadyCallbackDeadlineTimer,
                WidgetCompactExpansionReadinessPolicy.DefaultDeadlineMilliseconds * 8,
                FlushCompactExpansionReadyCallbacks);
        }
    }

    private void OnCompactExpansionReady()
    {
        if (!IsCompactExpansionReady || IsClosing)
        {
            return;
        }

        ResumePendingCompactExpansion(deadlineElapsed: false);

        if (_isCollapseAnimationRendering || _isShellTransitionActive)
        {
            return;
        }

        if (_compactExpansionReadyCallbacks.Count == 0)
        {
            return;
        }

        FlushCompactExpansionReadyCallbacks();
    }

    private void FlushCompactExpansionReadyCallbacks()
    {
        CancelTimer(ref _compactExpansionReadyCallbackDeadlineTimer);
        if (IsClosing || _compactExpansionReadyCallbacks.Count == 0)
        {
            return;
        }

        if (_isCollapseAnimationRendering || _isShellTransitionActive)
        {
            ScheduleTimer(
                ref _compactExpansionReadyCallbackDeadlineTimer,
                WidgetCompactExpansionReadinessPolicy.DefaultDeadlineMilliseconds,
                FlushCompactExpansionReadyCallbacks);
            return;
        }

        Action[] callbacks = _compactExpansionReadyCallbacks.ToArray();
        _compactExpansionReadyCallbacks.Clear();
        foreach (Action callback in callbacks)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                App.Log($"[CompactWarmup] Ready callback failed: {ex.Message}");
            }
        }
    }

    private void WidgetShellControl_HostedContentChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() =>
                WidgetShellControl_HostedContentChanged(sender, e));
            return;
        }

        // A grouped host can swap the live body while it is collapsed. The
        // previous member's warm-up and hover request are no longer valid for
        // the incoming tree. In particular, a pointer that remains over the
        // persistent group HWND does not produce another routed PointerEntered,
        // so merely warming the new tree can leave Smart expansion permanently
        // unarmed until the user clicks.
        _hostedContentSwapTimestamp = Stopwatch.GetTimestamp();
        CancelTimer(ref _collapseHoverTimer);
        CancelPendingCompactExpansion();
        InvalidateCompactExpansionReadiness();
        CancelCompactExpansionWarmup();
        if (_targetCollapsed && _compactExpansionWarmupCancellation is null)
        {
            // Swapped while collapsed: the user is interacting with the new
            // member right now and the next hover expand is imminent, so the
            // re-warm takes the urgent lane (the pointer-over gate would
            // otherwise pin it to background idle until the pointer leaves).
            QueueCompactExpansionWarmup(urgent: true);
        }

        // Let SetContent finish attaching the incoming view before measuring
        // the compact body. The native probe then reconstructs hover state even
        // when WinUI correctly omits a second entry event for the shared HWND.
        DispatcherQueue.TryEnqueue(RearmCompactHoverAfterHostedContentChange);
    }

    protected void RearmCompactHoverAfterHostedContentChange()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RearmCompactHoverAfterHostedContentChange);
            return;
        }

        if (!_collapseInitialized ||
            !_targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            IsClosing)
        {
            return;
        }

        CancelTimer(ref _collapseHoverTimer);
        _isPointerOverWidget = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        _compactState = WidgetCompactState.Collapsed;
        UpdateCompactViewState();
        QueueCompactExpansionWarmup();
        StartCompactHoverRecoveryProbe();
        SynchronizeCompactHoverFromCurrentCursor();

        WidgetCompactInteractionSnapshot snapshot =
            CaptureCompactInteractionSnapshot();
        if (snapshot.IsPointerInside &&
            !WidgetCompactInteractionPolicy.CanHoverExpand(
                EffectiveCollapseBehavior,
                snapshot,
                allowInteractionRegionDwell: true))
        {
            App.Log(
                $"[Compact] Hosted-content hover rearm blocked " +
                $"kind={Config.WidgetKind} id={Config.Id} " +
                $"interactionDepth={snapshot.InteractionDepth} " +
                $"boundsInteraction={snapshot.IsBoundsInteractionActive} " +
                $"dragging={snapshot.IsDragging} resizing={snapshot.IsResizing} " +
                $"dropInside={snapshot.IsDropInside} " +
                $"blockingSurface={snapshot.HasBlockingSurface}");
        }
    }

    /// <summary>
    /// Returns a transient Smart-mode expansion to its stable capsule bounds
    /// before tray visibility animation captures the resting window position.
    /// Manual/pinned expansion remains untouched.
    /// </summary>
    protected void PrepareCompactHostForTrayHide()
    {
        if (!_collapseInitialized ||
            IsClosing ||
            _targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            _isSmartPinnedOpen)
        {
            return;
        }

        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        // Tray hide is a system path over freshly restored geometry: it must
        // never fabricate a persisted capsule placement (feedback 340). The
        // collapse below still lands on the expanded window's corner, but
        // only transiently — the following SetCollapsedState derives those
        // bounds without capturing them.
        SetCollapsedState(
            collapsed: true,
            persistManualState: false,
            animate: false,
            durationMs: 0,
            allowDuringInteraction: true);

        // The window is about to be hidden, so there is no frame to wait for
        // before releasing the temporary expanded-state layer.
        CancelDeferredExpandedLayerRestore();
        RestoreLayerAfterExpandedState();
    }

    /// <summary>
    /// Synchronizes compact hover state from the native cursor position. WinUI can
    /// miss the first routed PointerEntered after a no-activate window is created
    /// or shown from the tray; the probe keeps Smart mode hover-only and avoids
    /// requiring an activating click.
    /// </summary>
    protected void NotifyCompactHostVisibilityChanged(bool isVisible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => NotifyCompactHostVisibilityChanged(isVisible));
            return;
        }

        if (!isVisible)
        {
            CancelPendingCompactExpansion();
            CancelCompactExpansionWarmup();
            _compactExpansionReadyCallbacks.Clear();
            CancelTimer(ref _compactExpansionReadyCallbackDeadlineTimer);
            StopCompactHoverRecoveryProbe();
            StopOverlayDragCuePoll();
            WidgetShellControl.ResetTransientCompactPointerState();
            ResetCompactPointerStateAfterHide();
            return;
        }

        WidgetShellControl.ResumeVisualActivity();
        if (_targetCollapsed)
        {
            RefreshCompactPresentation();
        }

        // Visibility notifications can be coalesced when AppWindow.Show and
        // ShowWindow(SW_SHOWNOACTIVATE) are used together. Always rebuild the
        // pointer state from the real cursor instead of trusting routed state
        // left over from the previous visible lifetime.
        WidgetShellControl.ResetTransientCompactPointerState();
        ResetCompactPointerStateAfterHide();
        // The host is now visible and its content is present, so the expansion
        // layout warm-up can finally run. Promote it to urgent once so the first
        // user-triggered expand does not have to wait on the background warm-up
        // (which would otherwise race the first hover and cause first-expand jank).
        QueueCompactExpansionWarmup(urgent: true);
        StartCompactHoverRecoveryProbe();
        SynchronizeCompactHoverFromCurrentCursor();
    }

    private void StartCompactHoverRecoveryProbe()
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            IsClosing ||
            HWnd == IntPtr.Zero ||
            !Win32Helper.IsWindowVisible(HWnd))
        {
            return;
        }

        PruneCompactHoverRecoveryTargets();
        bool alreadyTracked = CompactHoverRecoveryTargets.Any(reference =>
            reference.TryGetTarget(out WidgetWindowBase? target) &&
            ReferenceEquals(target, this));
        if (!alreadyTracked)
        {
            CompactHoverRecoveryTargets.Add(new WeakReference<WidgetWindowBase>(this));
        }

        _isCompactHoverRecoveryRegistered = true;

        if (s_compactHoverRecoveryTimer is null)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(CompactHoverRecoveryProbeMs);
            timer.Tick += CompactHoverRecoveryTimer_Tick;
            s_compactHoverRecoveryTimer = timer;
            timer.Start();
        }
        else if (!s_compactHoverRecoveryTimer.IsRunning)
        {
            s_compactHoverRecoveryTimer.Start();
        }
    }

    private void StopCompactHoverRecoveryProbe()
    {
        _isCompactHoverRecoveryRegistered = false;
        PruneCompactHoverRecoveryTargets();
    }

    private void SynchronizeCompactHoverFromCurrentCursor()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        RunCompactHoverRecoveryProbe(cursor, GetPointerRootWindow(cursor));
    }

    // ── Overlay drag cue arbitration ─────────────────────────────
    // The collapse arrow's restore verdict comes from the physical cursor
    // versus the bar's live screen rectangle, never from routed pointer
    // events: the widget morphs its HWND bounds during Smart expansion,
    // which makes Windows deliver spurious WM_MOUSEMOVE with shifted client
    // coordinates under a stationary cursor, and enter/exit pairing is not
    // guaranteed by WinUI. Arming stays event-driven for responsiveness; a
    // false arm self-heals on the next poll tick.

    private void WidgetShellControl_OverlayDragCueArmRequested(
        object? sender,
        EventArgs e)
    {
        // During a compact/expand bounds transition the window morphs under
        // the cursor, so the bar can sweep across a stationary pointer and
        // fire a false entry. Arm requests are dropped until the morph
        // settles.
        if (IsCompactTransitionActive)
        {
            return;
        }

        // The entered hint is verified against the physical cursor before the
        // arrow shows: spurious hints arrive during bounds animations, and
        // event coordinates cannot be trusted in either direction.
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) ||
            !IsScreenPointWithinOverlayDragCueZone(cursor))
        {
            return;
        }

        WidgetShellControl.SetOverlayDragCueActive(true);
        _overlayDragCueOutsideStreak = 0;
        if (_overlayDragCuePollTimer is null)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(OverlayDragCuePollIntervalMs);
            timer.Tick += OverlayDragCuePollTimer_Tick;
            _overlayDragCuePollTimer = timer;
        }

        if (!_overlayDragCuePollTimer.IsRunning)
        {
            _overlayDragCuePollTimer.Start();
        }

        App.LogVerbose(
            "[DragCue] Poll started. " +
            $"cursor={cursor.X},{cursor.Y} zone={DescribeOverlayDragCueScreenZone()}");
    }

    private void OverlayDragCuePollTimer_Tick(DispatcherQueueTimer sender, object args) =>
        OverlayDragCuePollTimer_Tick();

    private void OverlayDragCuePollTimer_Tick()
    {
        if (IsClosing || !WidgetShellControl.IsOverlayDragCueArmed)
        {
            StopOverlayDragCuePoll();
            return;
        }

        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) ||
            IsScreenPointWithinOverlayDragCueZone(cursor))
        {
            // The physical cursor is inside the bar's zone, or its position is
            // momentarily unreadable — neither may advance the streak.
            _overlayDragCueOutsideStreak = 0;
            return;
        }

        if (++_overlayDragCueOutsideStreak >= OverlayDragCueRestoreOutsideTicks)
        {
            App.Log(
                "[DragCue] Restored bar. " +
                $"cursor={cursor.X},{cursor.Y} " +
                $"zone={DescribeOverlayDragCueScreenZone()}");
            StopOverlayDragCuePoll();
            WidgetShellControl.SetOverlayDragCueActive(false);
        }
    }

    private void StopOverlayDragCuePoll()
    {
        _overlayDragCuePollTimer?.Stop();
        _overlayDragCueOutsideStreak = 0;
    }

    private void ReleaseOverlayDragCuePollTimer()
    {
        StopOverlayDragCuePoll();
        if (_overlayDragCuePollTimer is not { } timer)
        {
            return;
        }

        _overlayDragCuePollTimer = null;
        timer.Tick -= OverlayDragCuePollTimer_Tick;
    }

    private bool IsScreenPointWithinOverlayDragCueZone(Win32Helper.POINT cursor)
    {
        if (!TryGetOverlayDragCueScreenZone(out Windows.Foundation.Rect zone))
        {
            // Unreadable geometry must not restore the cue.
            return true;
        }

        return cursor.X >= zone.X &&
            cursor.X < zone.X + zone.Width &&
            cursor.Y >= zone.Y &&
            cursor.Y < zone.Y + zone.Height;
    }

    private string DescribeOverlayDragCueScreenZone()
    {
        return TryGetOverlayDragCueScreenZone(out Windows.Foundation.Rect zone)
            ? $"{zone.X:F0},{zone.Y:F0}+{zone.Width:F0}x{zone.Height:F0}"
            : "<unavailable>";
    }

    private bool TryGetOverlayDragCueScreenZone(out Windows.Foundation.Rect zone)
    {
        zone = default;
        if (WidgetShellControl.OverlayDragHandleElement is not { } handle ||
            handle.Visibility != Visibility.Visible ||
            handle.ActualWidth <= 0 ||
            handle.ActualHeight <= 0 ||
            handle.XamlRoot is null)
        {
            return false;
        }

        try
        {
            // The bar's screen rectangle is recomputed on every tick so a
            // bounds animation moving the window cannot strand the verdict in
            // a stale coordinate frame. The root origin MUST come from
            // ClientToScreen (like TryGetPointerScreenPoint, which the window
            // drag path has proven): GetWindowRect includes any invisible
            // frame, which silently displaces the zone off the visible bar.
            var clientOrigin = default(Win32Helper.POINT);
            if (!Win32Helper.ClientToScreen(HWnd, ref clientOrigin))
            {
                return false;
            }

            Windows.Foundation.Point topLeft = handle.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = handle.XamlRoot.RasterizationScale;
            double left = clientOrigin.X +
                ((topLeft.X - OverlayDragCueSideToleranceDips) * scale);
            double top = clientOrigin.Y + (topLeft.Y * scale);
            double right = clientOrigin.X +
                ((topLeft.X + handle.ActualWidth + OverlayDragCueSideToleranceDips) * scale);
            double bottom = clientOrigin.Y +
                ((topLeft.Y + handle.ActualHeight + OverlayDragCueBottomToleranceDips) * scale);
            zone = new Windows.Foundation.Rect(
                left,
                top,
                Math.Max(0, right - left),
                Math.Max(0, bottom - top));
            return true;
        }
        catch
        {
            // A transform that fails mid-layout must not restore the cue.
            return false;
        }
    }

    internal uint CreateObservedCompactSubclassMessage(IntPtr hWnd, uint message)
    {
        if (message is Win32Helper.WM_SETCURSOR or Win32Helper.WM_MOUSEMOVE)
        {
            if (_collapseInitialized &&
                _targetCollapsed &&
                !IsClosing &&
                UsesSmartCollapseBehavior())
            {
                if (!_isNativeCompactMouseTracking && hWnd != IntPtr.Zero)
                {
                    _isNativeCompactMouseTracking = Win32Helper.TryTrackMouseLeave(hWnd);
                }

                if ((!_isPointerOverWidget || s_compactHoverRecoveryTimer is not { IsRunning: true }) &&
                    !_nativeCompactMouseActivityQueued)
                {
                    _nativeCompactMouseActivityQueued = true;
                    UiDispatch.PostAfterCallback(OnDeferredNativeCompactMouseActivity);
                }
            }
        }
        else if (message == Win32Helper.WM_MOUSELEAVE)
        {
            _isNativeCompactMouseTracking = false;
            if (_collapseInitialized &&
                _targetCollapsed &&
                !IsClosing &&
                UsesSmartCollapseBehavior() &&
                !_nativeCompactMouseLeaveQueued)
            {
                _nativeCompactMouseLeaveQueued = true;
                UiDispatch.PostAfterCallback(OnDeferredNativeCompactMouseLeave);
            }
        }

        return message;
    }

    private void OnDeferredNativeCompactMouseActivity()
    {
        _nativeCompactMouseActivityQueued = false;
        if (!_collapseInitialized || !_targetCollapsed || IsClosing || !UsesSmartCollapseBehavior())
        {
            return;
        }

        StartCompactHoverRecoveryProbe();
        SynchronizeCompactHoverFromCurrentCursor();
    }

    private void OnDeferredNativeCompactMouseLeave()
    {
        _nativeCompactMouseLeaveQueued = false;
        if (!_collapseInitialized || !_targetCollapsed || IsClosing || !UsesSmartCollapseBehavior())
        {
            return;
        }

        SynchronizeCompactHoverFromCurrentCursor();
    }

    private static bool HasActiveCompactHoverOrDragProbeTarget()
    {
        for (int i = 0; i < CompactHoverRecoveryTargets.Count; i++)
        {
            if (CompactHoverRecoveryTargets[i].TryGetTarget(out WidgetWindowBase? target) &&
                target._isCompactHoverRecoveryRegistered &&
                (target._isPointerOverWidget ||
                 target._isCompactDragInside ||
                 target._nativeCompactMouseActivityQueued ||
                 target._nativeCompactMouseLeaveQueued ||
                 target.IsDragging ||
                 target.IsCompactArrangementDragActive))
            {
                return true;
            }
        }

        return false;
    }

    private static void CompactHoverRecoveryTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return;
        }

        IntPtr pointerRootWindow = GetPointerRootWindow(cursor);

        for (int i = 0; i < CompactHoverRecoveryTargets.Count; i++)
        {
            if (CompactHoverRecoveryTargets[i].TryGetTarget(out WidgetWindowBase? target) &&
                target._isCompactHoverRecoveryRegistered)
            {
                target.RunCompactHoverRecoveryProbe(cursor, pointerRootWindow);
            }
        }

        PruneCompactHoverRecoveryTargets();
        if (!HasActiveCompactHoverOrDragProbeTarget())
        {
            sender.Stop();
        }
    }

    private static void PruneCompactHoverRecoveryTargets()
    {
        for (int i = CompactHoverRecoveryTargets.Count - 1; i >= 0; i--)
        {
            if (!CompactHoverRecoveryTargets[i].TryGetTarget(out WidgetWindowBase? target) ||
                !target._isCompactHoverRecoveryRegistered)
            {
                CompactHoverRecoveryTargets.RemoveAt(i);
            }
        }

        if (CompactHoverRecoveryTargets.Count != 0 ||
            s_compactHoverRecoveryTimer is not { } timer)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= CompactHoverRecoveryTimer_Tick;
        s_compactHoverRecoveryTimer = null;
    }

    private static IntPtr GetPointerRootWindow(Win32Helper.POINT cursor)
    {
        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor);
        if (pointerWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr pointerRootWindow = Win32Helper.GetAncestor(pointerWindow, Win32Helper.GA_ROOT);
        return pointerRootWindow != IntPtr.Zero ? pointerRootWindow : pointerWindow;
    }

    private void RunCompactHoverRecoveryProbe(
        Win32Helper.POINT cursor,
        IntPtr pointerRootWindow)
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            IsClosing ||
            HWnd == IntPtr.Zero ||
            !Win32Helper.IsWindowVisible(HWnd))
        {
            _isCompactHoverRecoveryRegistered = false;
            return;
        }

        ReconcileCompactDragStateAfterPointerRelease();

        if (_isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            !WidgetShellControl.IsCollapsed ||
            !RootElement.IsLoaded)
        {
            return;
        }

        SynchronizeCompactHoverFromNativeCursor(cursor, pointerRootWindow);
    }

    private void SynchronizeCompactHoverFromNativeCursor(
        Win32Helper.POINT cursor,
        IntPtr pointerRootWindow)
    {
        RectInt32 windowBounds = GetActualWindowBounds();
        bool pointerInsideWindow =
            cursor.X >= windowBounds.X &&
            cursor.X < windowBounds.X + windowBounds.Width &&
            cursor.Y >= windowBounds.Y &&
            cursor.Y < windowBounds.Y + windowBounds.Height;
        bool nativeRootCanReceivePointer =
            CanReceivePointerFromRootWindow(pointerRootWindow);
        if (!WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
                pointerInsideWindow,
                nativeRootCanReceivePointer,
                HasRecentCompactRoutedPointerAuthority()))
        {
            bool hadPointerState =
                _isPointerOverWidget ||
                _isPointerOverCompactExpansionZone ||
                _isPointerOverCompactMoveHandle ||
                _isPointerOverCompactActions;
            if (hadPointerState)
            {
                ResetCompactPointerStateAfterPhysicalExit();
            }
            if (!hadPointerState &&
                (IsCollapsedContentHibernated || _collapsedContentHibernationTimer is not null))
            {
                return;
            }
            ArmCollapsedContentHibernationTimer();
            return;
        }

        bool pointerInsideExpansionZone = IsScreenPointInsideElement(
            cursor,
            WidgetShellControl.CompactBodyElement,
            windowBounds);
        bool pointerInsideMoveHandle = IsScreenPointInsideElement(
            cursor,
            WidgetShellControl.CompactMoveHandleElement,
            windowBounds);
        _isPointerOverWidget = true;
        // Native hover recovery can run without a routed PointerEntered event.
        // Rebuild the left drag-zone flag from the actual cursor geometry before
        // deciding whether hover expansion is eligible.
        _isPointerOverCompactMoveHandle = pointerInsideMoveHandle;
        if (!pointerInsideExpansionZone)
        {
            if (_isPointerOverCompactExpansionZone)
            {
                _isPointerOverCompactExpansionZone = false;
                CancelTimer(ref _collapseHoverTimer);
                UpdateCompactViewState();
            }

            // The trailing action strip may use the longer anti-accidental
            // dwell, but the left identity strip remains drag-only.
            TryScheduleCompactHoverExpansion(
                allowInteractionRegionDwell: !pointerInsideMoveHandle);
            return;
        }

        bool recoveredMissingRoutedEntry = !_isPointerOverCompactExpansionZone;
        _isPointerOverCompactExpansionZone = true;
        _isPointerOverCompactMoveHandle = pointerInsideMoveHandle;
        _isPointerOverCompactActions = false;
        CancelTimer(ref _collapseLeaveTimer);
        UpdateCompactViewState();
        QueueCompactExpansionWarmup(urgent: true);
        TryScheduleCompactHoverExpansion();

        if (recoveredMissingRoutedEntry)
        {
            PerformanceLogger.Mark(
                "CompactHoverRecovered",
                $"kind={Config.WidgetKind} id={Config.Id}");
        }
    }

    private bool IsScreenPointInsideElement(
        Win32Helper.POINT cursor,
        FrameworkElement? element,
        RectInt32 windowBounds)
    {
        if (element is null ||
            element.Visibility != Visibility.Visible ||
            element.XamlRoot is null ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point topLeft = element.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = element.XamlRoot.RasterizationScale;
            double left = windowBounds.X + (topLeft.X * scale);
            double top = windowBounds.Y + (topLeft.Y * scale);
            double right = left + (element.ActualWidth * scale);
            double bottom = top + (element.ActualHeight * scale);
            return cursor.X >= left &&
                cursor.X < right &&
                cursor.Y >= top &&
                cursor.Y < bottom;
        }
        catch
        {
            return false;
        }
    }

    private void ResetCompactPointerStateAfterPhysicalExit()
    {
        ClearCompactRoutedPointerAuthority();
        WidgetShellControl.ResetTransientCompactPointerState();
        if (!_isPointerOverWidget &&
            !_isPointerOverCompactExpansionZone &&
            !_isPointerOverCompactMoveHandle &&
            !_isPointerOverCompactActions)
        {
            _suppressSmartExpansionUntilPointerExit = false;
            _suppressHoverExpansionAfterDragUntilPointerExit = false;
            return;
        }

        _isPointerOverWidget = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        _suppressSmartExpansionUntilPointerExit = false;
        _suppressHoverExpansionAfterDragUntilPointerExit = false;
        CancelTimer(ref _collapseHoverTimer);
        UpdateCompactViewState();
    }

    private void ResetCompactPointerStateAfterHide()
    {
        ClearCompactRoutedPointerAuthority();
        _isPointerOverWidget = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        _suppressSmartExpansionUntilPointerExit = false;
        _suppressHoverExpansionAfterDragUntilPointerExit = false;
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        UpdateCompactViewState();
    }

    private void CollapseLanguageChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CollapseLanguageChanged);
            return;
        }

        RefreshCompactPresentation();
        ApplyCompactTooltips();
    }

    private void ApplyCompactTooltips()
    {
        var localization = App.Current.LocalizationService;
        bool usesCapsuleBar = SettingsService.NormalizeWidgetCapsuleArrangementMode(
                SettingsService.Settings.WidgetCapsuleArrangementMode) ==
            SettingsService.WidgetCapsuleArrangementBar;
        string overlayDragHandleTooltipKey =
            EffectiveCollapseBehavior switch
            {
                WidgetCollapseBehavior.Smart => "Widget.CollapseBehavior.Smart",
                WidgetCollapseBehavior.Click => "Widget.Compact.MoveOrCollapse",
                _ => "Widget.Compact.Move"
            };
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CollapseActionButton,
            localization.T("Widget.Compact.Collapse"));
        WidgetShellControl.SetOverlayDragHandleLabel(
            localization.T(overlayDragHandleTooltipKey));
        _compactMoveHintText = localization.T(
            usesCapsuleBar ? "Widget.Compact.MoveBar" : "Widget.Compact.Move");
        _compactExpandHintText = localization.T("Widget.Compact.Expand");
        WidgetShellControl.SetCompactHintTexts(
            _compactMoveHintText,
            _compactExpandHintText);
        WidgetShellControl.SetCompactHostAccessibility(
            _compactMoveHintText,
            _compactExpandHintText,
            localization.T("Widget.Compact.Reorder"),
            localization.T("Widget.Compact.Expand"));
    }

    private void CollapseSettingsChanged()
    {
        bool appearanceOnly =
            SettingsService.LastNotifiedChangeKind == SettingsChangeKind.Appearance;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ApplyCollapseSettingsChanged(appearanceOnly));
            return;
        }

        ApplyCollapseSettingsChanged(appearanceOnly);
    }

    private void ApplyCollapseSettingsChanged(bool appearanceOnly)
    {
        if (appearanceOnly ||
            !_collapseInitialized || IsClosing)
        {
            // Compact geometry reacts to layout/compact settings only; the
            // appearance preview channel owns visual refreshes.
            return;
        }

        string compactExpansionDirection = EffectiveCompactExpansionDirection;
        bool compactExpansionDirectionChanged = !string.Equals(
            _observedCompactExpansionDirection,
            compactExpansionDirection,
            StringComparison.Ordinal);
        if (compactExpansionDirectionChanged)
        {
            ApplyCompactExpansionDirectionChange();
        }

        if (_observedCompactWidth != Config.CompactWidth ||
            !ReferenceEquals(_observedCompactPlacement, Config.CompactPlacement))
        {
            _stableCompactBounds = null;
            ObserveCompactOverrides();
        }

        RefreshCompactPresentation();
        ApplyCompactTooltips();
        if (compactExpansionDirectionChanged &&
            EffectiveCollapseBehavior == _lastEffectiveCollapseBehavior)
        {
            // A direction-only settings change is declarative. Keep the
            // currently visible capsule or expanded panel exactly where it is;
            // eligibility is evaluated when the next expansion is requested.
            QueueCompactExpansionWarmup();
            return;
        }

        ApplyEffectiveCollapseBehavior(animate: true);
        if (!compactExpansionDirectionChanged &&
            !_targetCollapsed &&
            UsesCompactExpansionGeometry())
        {
            RectInt32 current = GetCurrentWindowBounds();
            RectInt32 compact = GetStableCompactBounds(current);
            ReanchorExpandedToCompact(compact, preserveAnchor: false);
        }
    }

    private bool UsesSmartCollapseBehavior()
    {
        return EffectiveCollapseBehavior == WidgetCollapseBehavior.Smart;
    }

    private void ApplyEffectiveCollapseBehavior(bool animate)
    {
        WidgetCollapseBehavior previousBehavior = _lastEffectiveCollapseBehavior;
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        bool enteredCompactBehavior =
            previousBehavior == WidgetCollapseBehavior.Expanded &&
            behavior is WidgetCollapseBehavior.Click or WidgetCollapseBehavior.Smart;
        if (enteredCompactBehavior && !_targetCollapsed)
        {
            // Entering capsule mode from always-expanded collapses in place:
            // the current window region is the source of the transient
            // capsule bounds, so any dormant remembered capsule spot is not
            // resurrected. Only the deliberate re-commit of an existing
            // placement captures (legacy semantics); a widget that has never
            // been collapsed stays transient (feedback 340).
            DeriveCompactPlacementFromExpandedBounds(
                capturePlacement: Config.CompactPlacement is not null);
        }

        ApplyCollapseBehaviorVisuals();
        WidgetCompactTransitionReason transitionReason =
            WidgetCompactTransitionPolicy.ResolveReason(previousBehavior, behavior);
        bool enteredClickBehavior =
            previousBehavior != WidgetCollapseBehavior.Click &&
            behavior == WidgetCollapseBehavior.Click;
        bool enteredSmartBehavior =
            previousBehavior != WidgetCollapseBehavior.Smart &&
            behavior == WidgetCollapseBehavior.Smart;
        if (enteredSmartBehavior)
        {
            SynchronizeCompactPointerStateForSmartEntry();
        }

        if (enteredClickBehavior && !Config.IsCollapsed)
        {
            // Selecting click-to-expand must produce a visible capsule
            // immediately. Persist that manual state before attempting the
            // transition so a temporarily open flyout cannot lose it.
            Config.IsCollapsed = true;
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
            SynchronizeWidgetGroupLayout();
        }

        WidgetCompactInteractionSnapshot snapshot = CaptureCompactInteractionSnapshot();
        bool desiredCollapsed = EffectiveCollapseBehavior switch
        {
            WidgetCollapseBehavior.Expanded => false,
            WidgetCollapseBehavior.Click => Config.IsCollapsed,
            WidgetCollapseBehavior.Smart => _targetCollapsed
                ? !WidgetCompactInteractionPolicy.CanHoverExpand(behavior, snapshot)
                : WidgetCompactInteractionPolicy.CanAutoCollapse(behavior, snapshot),
            _ => false
        };
        // A global mode switch can affect every HWND in the same dispatcher
        // turn. Settling that bulk transition immediately avoids running many
        // expensive live-resize animations concurrently; ordinary hover/click
        // transitions continue to use the configured animation.
        bool shouldAnimateTransition = animate && !enteredSmartBehavior;
        SetCollapsedState(
            desiredCollapsed,
            persistManualState: false,
            animate: shouldAnimateTransition,
            transitionReason: transitionReason);
        WidgetCompactInteractionSnapshot appliedSnapshot = CaptureCompactInteractionSnapshot();
        if (desiredCollapsed && _targetCollapsed)
        {
            // A collapse right after a hosted-content swap (group tab
            // switch) is a likely "switched, leaving, coming right back":
            // warm the new member urgently so the re-entry hover expands at
            // the dwell instead of racing switch-settle animations on the
            // shared frame clock and falling to the live-layout fallback.
            QueueCompactExpansionWarmup(
                urgent: WidgetCompactWarmupSchedulePolicy
                    .ShouldWarmUrgentlyAfterHostedContentSwap(
                        _hostedContentSwapTimestamp == 0
                            ? double.PositiveInfinity
                            : Stopwatch.GetElapsedTime(_hostedContentSwapTimestamp)
                                .TotalMilliseconds));
            StartCompactHoverRecoveryProbe();
            SynchronizeCompactHoverFromCurrentCursor();
        }
        else if (WidgetCompactInteractionPolicy.ShouldRetryAutoCollapse(
            behavior,
            appliedSnapshot))
        {
            ScheduleSmartCollapse(SmartCollapseProbeMs);
        }
    }

    private void SynchronizeCompactPointerStateForSmartEntry()
    {
        WidgetCompactInteractionSnapshot synchronized =
            WidgetCompactInteractionPolicy.SynchronizeForSmartEntry(
                CaptureCompactInteractionSnapshot(),
                IsPointerPhysicallyInsideWindow());

        _isPointerOverWidget = synchronized.IsPointerInside;
        _isPointerOverCompactExpansionZone = synchronized.IsExpansionZoneActive;
        _isPointerOverCompactMoveHandle = synchronized.IsPointerOverMoveHandle;
        _isPointerOverCompactActions = synchronized.IsPointerOverActions;
        _suppressSmartExpansionUntilPointerExit = synchronized.SuppressHoverExpansion;
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        UpdateCompactViewState();
    }

    private void ApplyCollapseBehaviorVisuals()
    {
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        if (_lastEffectiveCollapseBehavior != behavior)
        {
            _lastEffectiveCollapseBehavior = behavior;
            _isSmartPinnedOpen = false;
            _suppressSmartExpansionUntilPointerExit = false;
            _suppressHoverExpansionAfterDragUntilPointerExit = false;
        }
        bool canCollapse = behavior != WidgetCollapseBehavior.Expanded;
        bool usesCapsuleBar = SettingsService.NormalizeWidgetCapsuleArrangementMode(
                SettingsService.Settings.WidgetCapsuleArrangementMode) ==
            SettingsService.WidgetCapsuleArrangementBar;
        WidgetShellControl.SetCollapseActionAvailable(canCollapse);
        WidgetShellControl.SetCompactInteractionMode(behavior == WidgetCollapseBehavior.Smart);
        WidgetShellControl.SetCompactReorderEnabled(usesCapsuleBar && canCollapse);
        OnCollapseBehaviorChanged(behavior);
    }

    private void WidgetShellControl_CollapseRequested(object? sender, RoutedEventArgs e)
    {
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        CollapseWidgetFromHost();
    }

    private void WidgetShellControl_ExpandRequested(object? sender, RoutedEventArgs e)
    {
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        _suppressSmartExpansionUntilPointerExit = false;
        _suppressHoverExpansionAfterDragUntilPointerExit = false;
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        if (behavior == WidgetCollapseBehavior.Smart)
        {
            _isSmartPinnedOpen = true;
        }
        SetCollapsedState(
            false,
            persistManualState: behavior == WidgetCollapseBehavior.Click,
            animate: true);
    }

    private void WidgetShellControl_CompactBodyExpandRequested(object? sender, RoutedEventArgs e)
    {
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        _suppressSmartExpansionUntilPointerExit = false;
        _suppressHoverExpansionAfterDragUntilPointerExit = false;
        _isSmartPinnedOpen = false;
        SetCollapsedState(
            false,
            persistManualState: EffectiveCollapseBehavior == WidgetCollapseBehavior.Click,
            animate: true);
    }

    private void WidgetShellControl_CompactPointerEntered(object? sender, EventArgs e)
    {
        ReconcileCompactDragStateAfterPointerRelease();
        bool establishesNewPointerIntent = !_isPointerOverWidget;
        MarkCompactRoutedPointerActivity();
        ReleaseSmartHoverSuppressionAfterPointerIntent(
            establishesNewPointerIntent);
        _isPointerOverWidget = true;
        bool pointerInsideMoveHandle = false;
        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            pointerInsideMoveHandle = IsScreenPointInsideElement(
                cursor,
                WidgetShellControl.CompactMoveHandleElement,
                GetActualWindowBounds());
        }

        // The outer capsule entry can arrive before a child PointerEntered.
        // Resolve the left drag zone from native cursor geometry first so a
        // fast entry cannot queue an expansion before the child cancels it.
        _isPointerOverCompactMoveHandle = pointerInsideMoveHandle;
        _isPointerOverCompactExpansionZone = !pointerInsideMoveHandle;
        CancelTimer(ref _collapseLeaveTimer);
        if (pointerInsideMoveHandle)
        {
            CancelTimer(ref _collapseHoverTimer);
        }
        if (!_targetCollapsed && UsesSmartCollapseBehavior())
        {
            _compactState = _isSmartPinnedOpen
                ? WidgetCompactState.ExpandedPinned
                : WidgetCompactState.ExpandedTransient;
        }
        UpdateCompactViewState();
        // Let the child region finish hit testing before scheduling hover
        // expansion. This prevents the outer entry from briefly treating the
        // left drag strip as the body when layout is still settling.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPointerOverWidget ||
                !_targetCollapsed ||
                !UsesSmartCollapseBehavior())
            {
                return;
            }

            SynchronizeCompactHoverFromCurrentCursor();
        });
    }

    private void WidgetShellControl_CompactPointerMoved(object? sender, EventArgs e)
    {
        ReconcileCompactDragStateAfterPointerRelease();
        if (!_targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            IsClosing ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        MarkCompactRoutedPointerActivity();

        bool recoveredMissingRoutedEntry = !_isPointerOverWidget;
        ReleaseSmartHoverSuppressionAfterPointerIntent(
            establishesNewPointerIntent: true);

        // Receiving this routed move proves that this HWND owns the pointer.
        // Rebuild the compact-region flags from real geometry without relying
        // on WindowFromPoint, which may still identify an Explorer desktop host
        // for a no-activate window. This repairs the missing PointerEntered that
        // can follow a Todo/QuickCapture content swap in a persistent group HWND.
        RunCompactHoverRecoveryProbe(cursor, HWnd);

        if (recoveredMissingRoutedEntry && _isPointerOverWidget)
        {
            PerformanceLogger.Mark(
                "CompactHoverPointerMoveRecovered",
                $"kind={Config.WidgetKind} id={Config.Id}");
        }
    }

    private void ReleaseSmartHoverSuppressionAfterPointerIntent(
        bool establishesNewPointerIntent)
    {
        if (!_suppressSmartExpansionUntilPointerExit || !establishesNewPointerIntent)
        {
            return;
        }

        bool transitionActive = _isCollapseAnimationRendering || _isShellTransitionActive;
        bool canRelease = transitionActive
            ? _targetCollapsed && WidgetShellControl.IsCollapsed
            : WidgetCompactInteractionPolicy
                .CanReleaseHoverSuppressionAfterRoutedPointerIntent(
                    establishesNewPointerIntent,
                    _targetCollapsed,
                    WidgetShellControl.IsCollapsed,
                    isBoundsTransitionActive: false,
                    isShellTransitionActive: false);
        if (!canRelease)
        {
            return;
        }

        _suppressSmartExpansionUntilPointerExit = false;
        PerformanceLogger.Mark(
            "CompactHoverSuppressionReleased",
            $"kind={Config.WidgetKind} id={Config.Id}");
    }

    private void WidgetShellControl_CompactExpansionPointerEntered(object? sender, EventArgs e)
    {
        MarkCompactRoutedPointerActivity();
        if (IsNativePointerOverCompactMoveHandle())
        {
            _isPointerOverCompactMoveHandle = true;
            _isPointerOverCompactExpansionZone = false;
            CancelTimer(ref _collapseHoverTimer);
            UpdateCompactViewState();
            return;
        }

        // Child-region entry is authoritative. Window resize and asynchronous content
        // layout can occasionally prevent WinUI from raising the matching outer event.
        _isPointerOverWidget = true;
        _isPointerOverCompactExpansionZone = true;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        CancelTimer(ref _collapseLeaveTimer);
        // Hover intent precedes every expansion; start paying the shared
        // animation clock's cold-start cost here instead of on frame one.
        WidgetCompactAnimationCoordinator.PreArmFrameClock(HWnd);
        QueueCompactExpansionWarmup(urgent: true);
        TryScheduleCompactHoverExpansion();
    }

    private void WidgetShellControl_CompactExpansionPointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactExpansionZone = false;
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
            UpdateCompactViewState();
        }
    }

    private void WidgetShellControl_CompactPointerExited(object? sender, EventArgs e)
    {
        ClearCompactRoutedPointerAuthority();
        _isPointerOverWidget = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        _suppressSmartExpansionUntilPointerExit = false;
        _suppressHoverExpansionAfterDragUntilPointerExit = false;
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
        }
        UpdateCompactViewState();
        if (_targetCollapsed)
        {
            ArmCollapsedContentHibernationTimer();
            if (IsKeepCapsuleContentWarmEnabled)
            {
                QueueCompactExpansionWarmup();
            }
        }
        if (UsesSmartCollapseBehavior() && !_isCompactDragInside && !_isSmartPinnedOpen)
        {
            ScheduleSmartCollapse();
        }
    }

    private void WidgetShellControl_CompactPointerPressed(object? sender, EventArgs e)
    {
        if (!_targetCollapsed)
        {
            return;
        }

        CancelTimer(ref _collapseHoverTimer);
        _compactState = WidgetCompactState.Collapsed;
    }

    private void WidgetShellControl_CompactActionPointerEntered(object? sender, EventArgs e)
    {
        MarkCompactRoutedPointerActivity();
        _isPointerOverCompactActions = true;
        CancelTimer(ref _collapseHoverTimer);
        WidgetCompactAnimationCoordinator.PreArmFrameClock(HWnd);
        QueueCompactExpansionWarmup(urgent: true);
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion(
            allowInteractionRegionDwell: true);
    }

    private void WidgetShellControl_CompactActionPointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactActions = false;
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion();
    }

    private void WidgetShellControl_CompactMoveHandlePointerEntered(object? sender, EventArgs e)
    {
        MarkCompactRoutedPointerActivity();
        _isPointerOverCompactMoveHandle = true;
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
            WidgetCompactAnimationCoordinator.PreArmFrameClock(HWnd);
            QueueCompactExpansionWarmup(urgent: true);
        }
        UpdateCompactViewState();
        // The identity strip is drag-only. Hover expansion begins only after
        // the pointer crosses into the middle body region.
    }

    private void WidgetShellControl_CompactMoveHandlePointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactMoveHandle = false;
        UpdateCompactViewState();
    }

    private void TryScheduleCompactHoverExpansion(
        bool allowInteractionRegionDwell = false)
    {
        // The routed child events can briefly disagree while the compact layer
        // is being re-laid out. The native cursor position is authoritative for
        // the left drag strip: it must never start an expansion timer.
        if (IsNativePointerOverCompactMoveHandle())
        {
            _isPointerOverCompactMoveHandle = true;
            CancelTimer(ref _collapseHoverTimer);
            return;
        }

        WidgetCompactInteractionSnapshot snapshot =
            CaptureCompactInteractionSnapshot();
        if (!WidgetCompactInteractionPolicy.CanHoverExpand(
                EffectiveCollapseBehavior,
                snapshot,
                allowInteractionRegionDwell))
        {
            return;
        }

        if (_collapseHoverTimer is not null)
        {
            // Moving from an interaction region into the body upgrades the
            // conservative dwell to the user's normal hover response delay.
            if (_collapseHoverTimerAllowsInteractionRegionDwell &&
                !allowInteractionRegionDwell)
            {
                CancelTimer(ref _collapseHoverTimer);
            }
            else
            {
                return;
            }
        }

        int configuredDelay =
            SettingsService.NormalizeWidgetCompactExpandDelayMs(
                SettingsService.Settings.WidgetCompactExpandDelayMs);
        int effectiveDelay =
            WidgetCompactInteractionPolicy.ResolveHoverExpandDelayMilliseconds(
                configuredDelay,
                allowInteractionRegionDwell);
        bool scheduledForInteractionRegion = allowInteractionRegionDwell;
        _collapseHoverTimerAllowsInteractionRegionDwell =
            scheduledForInteractionRegion;
        _compactState = WidgetCompactState.ExpandPending;
        WidgetCompactAnimationCoordinator.PreArmFrameClock(HWnd);
        // The hover dwell is otherwise dead time. Starting the pointer-
        // promoted warm-up now lets a cold capsule finish its expanded
        // layout inside the dwell instead of inside the readiness deadline.
        QueueCompactExpansionWarmup(urgent: true);
        ScheduleTimer(
            ref _collapseHoverTimer,
            effectiveDelay,
            () =>
            {
                _collapseHoverTimer = null;
                _collapseHoverTimerAllowsInteractionRegionDwell = false;
                if (IsNativePointerOverCompactMoveHandle())
                {
                    _isPointerOverCompactMoveHandle = true;
                    return;
                }
                if (WidgetCompactInteractionPolicy.CanHoverExpand(
                        EffectiveCollapseBehavior,
                        CaptureCompactInteractionSnapshot(),
                        scheduledForInteractionRegion) &&
                    IsPointerRoutedToThisWindow())
                {
                    PerformanceLogger.Mark(
                        "CompactHoverExpandRequested",
                        $"region={(scheduledForInteractionRegion ? "interaction" : "body")} " +
                        $"delayMs={effectiveDelay} kind={Config.WidgetKind} id={Config.Id}");
                    SetCollapsedState(
                        false,
                        persistManualState: false,
                        animate: true,
                        requireHoverEligibility: true,
                        allowInteractionRegionHoverEligibility:
                            scheduledForInteractionRegion);
                }
            });
    }

    private bool IsNativePointerOverCompactMoveHandle()
    {
        return Win32Helper.GetCursorPos(out Win32Helper.POINT cursor) &&
            IsScreenPointInsideElement(
                cursor,
                WidgetShellControl.CompactMoveHandleElement,
                GetActualWindowBounds());
    }

    private bool IsPointerRoutedToThisWindow()
    {
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return false;
        }

        bool pointerInsideWindow = IsPointerPhysicallyInsideWindow();
        bool nativeRootCanReceivePointer =
            CanReceivePointerFromRootWindow(GetPointerRootWindow(cursor));
        return WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
            pointerInsideWindow,
            nativeRootCanReceivePointer,
            HasRecentCompactRoutedPointerAuthority());
    }

    private void MarkCompactRoutedPointerActivity()
    {
        int configuredDelay =
            SettingsService.NormalizeWidgetCompactExpandDelayMs(
                SettingsService.Settings.WidgetCompactExpandDelayMs);
        int lifetimeMilliseconds =
            WidgetCompactInteractionPolicy.ResolveRoutedPointerAuthorityLifetimeMilliseconds(
                configuredDelay,
                CompactHoverRecoveryProbeMs);
        _compactRoutedPointerAuthorityDeadline = Stopwatch.GetTimestamp() +
            (long)Math.Ceiling(
                lifetimeMilliseconds * Stopwatch.Frequency / 1000d);
    }

    private bool HasRecentCompactRoutedPointerAuthority()
    {
        return _compactRoutedPointerAuthorityDeadline != 0 &&
            Stopwatch.GetTimestamp() <= _compactRoutedPointerAuthorityDeadline;
    }

    private void ClearCompactRoutedPointerAuthority()
    {
        _compactRoutedPointerAuthorityDeadline = 0;
    }

    private bool CanReceivePointerFromRootWindow(IntPtr pointerRootWindow)
    {
        if (pointerRootWindow == HWnd)
        {
            return true;
        }

        // Before the widget receives its first click, WindowFromPoint can
        // report an Explorer desktop host instead of the no-activate WinUI
        // HWND. Explorer can replace WorkerW/SHELLDLL_DefView hosts after
        // Show Desktop, display changes, or an Explorer restart, so the
        // originally attached owner is not the only valid desktop receiver.
        // ShellDesktopDropTarget also verifies Explorer process identity and
        // rejects taskbar/application windows, preserving foreground blocking.
        if (ShellDesktopDropTarget.IsDesktopWindow(pointerRootWindow))
        {
            return true;
        }

        IntPtr desktopOwner = Win32Helper.GetWindowLongPtr(HWnd, Win32Helper.GWLP_HWNDPARENT);
        if (desktopOwner == IntPtr.Zero)
        {
            return false;
        }

        IntPtr desktopOwnerRoot = Win32Helper.GetAncestor(desktopOwner, Win32Helper.GA_ROOT);
        return pointerRootWindow == desktopOwner ||
            (desktopOwnerRoot != IntPtr.Zero && pointerRootWindow == desktopOwnerRoot);
    }

    protected void NotifyCompactDragEnteredFromHost() =>
        WidgetShellControl_CompactDragEntered(this, EventArgs.Empty);

    protected void NotifyCompactDragLeftFromHost() =>
        WidgetShellControl_CompactDragLeft(this, EventArgs.Empty);

    protected void NotifyCompactDropCompletedFromHost() =>
        WidgetShellControl_CompactDropCompleted(this, EventArgs.Empty);

    private void WidgetShellControl_CompactDragEntered(object? sender, EventArgs e)
    {
        if (!SupportsCompactDropExpansion)
        {
            return;
        }

        _isCompactDragInside = true;
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        CancelTimer(ref _collapseDragRestoreTimer);
        ScheduleCompactDragSessionRecoveryProbe();
        UpdateCompactViewState();

        if (!_dragExpandedFromCollapsed && (_targetCollapsed || IsWidgetCollapsedBoundsActive))
        {
            _dragExpandedFromCollapsed = true;
            bool animateDragExpansion = Config.WidgetKind == WidgetKind.File;
            SetCollapsedState(
                false,
                persistManualState: false,
                // File grids use the configured capsule transition so their
                // drag-open behavior visually matches hover/click expansion.
                // The session flag above prevents child DragEnter churn from
                // restarting the animation during the same drag.
                animate: animateDragExpansion);
        }
    }

    private void ReconcileCompactDragStateAfterPointerRelease()
    {
        bool clearedShellDrag = WidgetShellControl.TryClearStaleShellDragSessionAfterPointerRelease();
        if (!clearedShellDrag &&
            (!_isCompactDragInside || Win32Helper.IsAnyMouseButtonDown()))
        {
            return;
        }

        _isCompactDragInside = false;
        _dragExpandedFromCollapsed = false;
        CancelTimer(ref _collapseDragRestoreTimer);
        UpdateCompactViewState();
        App.Log(
            $"[Compact] Cleared stale drag session before pointer hover " +
            $"kind={Config.WidgetKind} id={Config.Id}");
    }

    private void WidgetShellControl_CompactDragLeft(object? sender, EventArgs e)
    {
        CancelTimer(ref _compactDragSessionRecoveryTimer);
        _isCompactDragInside = false;
        UpdateCompactViewState();
        if (!_dragExpandedFromCollapsed)
        {
            if (UsesSmartCollapseBehavior() && !_isPointerOverWidget && !_isSmartPinnedOpen)
            {
                ScheduleSmartCollapse();
            }
            return;
        }
        // Window growth can produce a transient DragLeave. A delayed restore gives
        // the routed drag events time to re-enter the newly expanded bounds.
        ScheduleDragRestore(DragRestoreDelayMs);
    }

    private void WidgetShellControl_CompactDropCompleted(object? sender, EventArgs e)
    {
        CancelTimer(ref _compactDragSessionRecoveryTimer);
        _isCompactDragInside = false;
        UpdateCompactViewState();
        if (_dragExpandedFromCollapsed)
        {
            ScheduleDragRestore(900);
        }
    }

    private async void WidgetShellControl_CompactPreviousRequested(object? sender, RoutedEventArgs e)
    {
        await OnCompactPreviousRequestedAsync();
    }

    private async void WidgetShellControl_CompactPrimaryActionRequested(object? sender, RoutedEventArgs e)
    {
        await OnCompactPrimaryActionRequestedAsync();
        RefreshCompactPresentation();
    }

    private async void WidgetShellControl_CompactPlayPauseRequested(object? sender, RoutedEventArgs e)
    {
        await OnCompactPlayPauseRequestedAsync();
    }

    private async void WidgetShellControl_CompactNextRequested(object? sender, RoutedEventArgs e)
    {
        await OnCompactNextRequestedAsync();
    }

    private void ScheduleDragRestore(int delayMs)
    {
        ScheduleTimer(ref _collapseDragRestoreTimer, delayMs, () =>
        {
            if (_isCompactDragInside || !_dragExpandedFromCollapsed)
            {
                return;
            }

            _dragExpandedFromCollapsed = false;
            bool shouldCollapse = EffectiveCollapseBehavior == WidgetCollapseBehavior.Smart ||
                EffectiveCollapseBehavior == WidgetCollapseBehavior.Click && Config.IsCollapsed;
            if (shouldCollapse)
            {
                SetCollapsedState(
                    true,
                    persistManualState: false,
                    animate: false,
                    durationMs: 0);
            }
        });
    }

    private void ScheduleCompactDragSessionRecoveryProbe()
    {
        ScheduleTimer(
            ref _compactDragSessionRecoveryTimer,
            CompactDragSessionRecoveryProbeMs,
            ProbeCompactDragSessionState);
    }

    private void ProbeCompactDragSessionState()
    {
        if (!_isCompactDragInside || IsClosing)
        {
            return;
        }

        if (!IsPointerPhysicallyInsideWindow() &&
            WidgetShellControl.TryEndShellDragSessionAfterNativePointerExit())
        {
            App.Log(
                $"[Compact] Recovered missing drag leave from native pointer " +
                $"kind={Config.WidgetKind} id={Config.Id}");
            return;
        }

        if (Win32Helper.IsAnyMouseButtonDown())
        {
            ScheduleCompactDragSessionRecoveryProbe();
            return;
        }

        // Host-driven drag expansion (Elevated pointer drag or Native OLE drop
        // target) sets _isCompactDragInside without setting WidgetShellControl's
        // XAML _isShellDragActive flag. Always clear _isCompactDragInside once
        // all mouse buttons are up so smart auto-collapse never wedges open.
        _ = WidgetShellControl.TryClearStaleShellDragSessionAfterPointerRelease();

        _isCompactDragInside = false;
        UpdateCompactViewState();
        if (_dragExpandedFromCollapsed)
        {
            ScheduleDragRestore(DragRestoreDelayMs);
        }
        else if (UsesSmartCollapseBehavior() &&
                 !_isPointerOverWidget &&
                 !_isSmartPinnedOpen)
        {
            ScheduleSmartCollapse();
        }

        App.Log(
            $"[Compact] Recovered drag session after pointer release " +
            $"kind={Config.WidgetKind} id={Config.Id}");
    }

    protected bool UsesCompactExpansionGeometry()
    {
        return EffectiveCollapseBehavior != WidgetCollapseBehavior.Expanded;
    }

    private string EffectiveCompactExpansionDirection =>
        WidgetCompactExpansionDirectionPolicy.ResolveEffective(
            Config,
            SettingsService.Settings.WidgetCompactExpansionDirection);

    private WidgetCompactExpansionLayout ResolveCompactExpansionLayout(
        RectInt32 compactBounds,
        SizeInt32? requestedSize = null,
        bool freezeResolvedAnchor = false,
        bool requireFullSize = false)
    {
        RectInt32 workArea = ResolveCompactWorkArea(compactBounds);
        SizeInt32 physicalSize = requestedSize ?? ResolveExpandedPhysicalSize(workArea);
        physicalSize = WidgetCompactBoundsCalculator.ResolveExpandedSizeForWidthMode(
            compactBounds,
            physicalSize,
            SettingsService.Settings.WidgetCompactWidthMode);
        IReadOnlyList<WidgetCompactExpansionAnchor> automaticAnchors =
            freezeResolvedAnchor && _compactExpansionAnchor is { } activeAnchor
                ? [activeAnchor]
                : ResolveCompactExpansionAnchorOrder(compactBounds, workArea);
        IReadOnlyList<WidgetCompactExpansionAnchor> anchors =
            WidgetCompactExpansionDirectionPolicy.Apply(
                EffectiveCompactExpansionDirection,
                automaticAnchors);
        bool strictFixedDirection = requireFullSize &&
            WidgetCompactExpansionDirectionPolicy.RequiresFullSize(
                EffectiveCompactExpansionDirection);
        return WidgetCompactExpansionCalculator.Resolve(
            compactBounds,
            physicalSize,
            workArea,
            anchors,
            strictFixedDirection);
    }

    /// <summary>
    /// Resolves the layout an expansion request should use. A fixed direction
    /// keeps its direction and only adapts the expanded size to the space
    /// actually available; automatic direction may still pick another anchor.
    /// The result can always expand, so an expansion request never hard-fails
    /// for lack of room.
    /// </summary>
    private WidgetCompactExpansionLayout ResolveRequestedCompactExpansion(
        RectInt32 compactBounds)
    {
        WidgetCompactExpansionLayout strict = ResolveCompactExpansionLayout(
            compactBounds,
            requireFullSize: true);
        return strict.CanExpand
            ? strict
            : ResolveCompactExpansionLayout(compactBounds);
    }

    private void LogCompactExpansionBlocked(
        RectInt32 compactBounds,
        WidgetCompactExpansionLayout layout)
    {
        RectInt32 workArea = ResolveCompactWorkArea(compactBounds);
        PointInt32 pivot = layout.Pivot;
        bool anchorsBottom = layout.Anchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        int availableHeight = anchorsBottom
            ? pivot.Y - workArea.Y
            : workArea.Y + workArea.Height - pivot.Y;
        int availableWidth = layout.Anchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom
                ? pivot.X - workArea.X
                : workArea.X + workArea.Width - pivot.X;
        string direction = EffectiveCompactExpansionDirection;
        App.LogVerbose(
            $"[Compact] Expansion blocked by fixed direction " +
            $"kind={Config.WidgetKind} id={Config.Id} " +
            $"direction={direction} compact=({compactBounds.X},{compactBounds.Y},{compactBounds.Width},{compactBounds.Height}) " +
            $"requested=({layout.RequestedSize.Width},{layout.RequestedSize.Height}) " +
            $"available=({availableWidth},{availableHeight}) " +
            $"anchor={layout.Anchor}");
    }

    private RectInt32 ResolveCompactWorkArea(RectInt32 compactBounds)
    {
        var center = new PointInt32(
            compactBounds.X + Math.Max(1, compactBounds.Width) / 2,
            compactBounds.Y + Math.Max(1, compactBounds.Height) / 2);
        return Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
            center,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
    }

    private SizeInt32 ResolveExpandedPhysicalSize(RectInt32 workArea)
    {
        if (Config.BoundsCoordinateVersion < WidgetConfig.CurrentBoundsCoordinateVersion)
        {
            RectInt32 legacy = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
            return new SizeInt32(legacy.Width, legacy.Height);
        }

        double scale = WidgetPositioningService.GetDpiScale(workArea);
        double logicalWidth = double.IsFinite(Config.Width)
            ? Math.Max(SettingsService.MinWidgetWidth, Config.Width)
            : SettingsService.DefaultWidgetWidth;
        double logicalHeight = double.IsFinite(Config.Height)
            ? Math.Max(SettingsService.MinWidgetHeight, Config.Height)
            : SettingsService.DefaultWidgetHeight;
        return new SizeInt32(
            WidgetPositioningService.ToPhysicalPixels(logicalWidth, scale),
            WidgetPositioningService.ToPhysicalPixels(logicalHeight, scale));
    }

    private IReadOnlyList<WidgetCompactExpansionAnchor> ResolveCompactExpansionAnchorOrder(
        RectInt32 compactBounds,
        RectInt32 workArea)
    {
        string arrangement = SettingsService.NormalizeWidgetCapsuleArrangementMode(
            SettingsService.Settings.WidgetCapsuleArrangementMode);
        string placement = arrangement == SettingsService.WidgetCapsuleArrangementBar
            ? SettingsService.NormalizeWidgetCapsuleBarPlacement(
                SettingsService.Settings.WidgetCapsuleBarPlacement)
            : SettingsService.WidgetCapsuleBarPlacementFloating;
        bool preferRightAnchor = compactBounds.X + compactBounds.Width / 2 >=
            workArea.X + workArea.Width / 2;
        bool preferBottomAnchor = compactBounds.Y + compactBounds.Height / 2 >=
            workArea.Y + workArea.Height / 2;

        List<WidgetCompactExpansionAnchor> anchors = placement switch
        {
            SettingsService.WidgetCapsuleBarPlacementTop => preferRightAnchor
                ? [WidgetCompactExpansionAnchor.RightTop, WidgetCompactExpansionAnchor.LeftTop]
                : [WidgetCompactExpansionAnchor.LeftTop, WidgetCompactExpansionAnchor.RightTop],
            SettingsService.WidgetCapsuleBarPlacementBottom => preferRightAnchor
                ? [WidgetCompactExpansionAnchor.RightBottom, WidgetCompactExpansionAnchor.LeftBottom]
                : [WidgetCompactExpansionAnchor.LeftBottom, WidgetCompactExpansionAnchor.RightBottom],
            SettingsService.WidgetCapsuleBarPlacementLeft => preferBottomAnchor
                ? [WidgetCompactExpansionAnchor.LeftBottom, WidgetCompactExpansionAnchor.LeftTop]
                : [WidgetCompactExpansionAnchor.LeftTop, WidgetCompactExpansionAnchor.LeftBottom],
            SettingsService.WidgetCapsuleBarPlacementRight => preferBottomAnchor
                ? [WidgetCompactExpansionAnchor.RightBottom, WidgetCompactExpansionAnchor.RightTop]
                : [WidgetCompactExpansionAnchor.RightTop, WidgetCompactExpansionAnchor.RightBottom],
            _ => ResolveFloatingAnchorOrder(preferRightAnchor, preferBottomAnchor)
        };

        WidgetCompactExpansionAnchor? preferred = _compactExpansionAnchor ??
            WidgetCompactExpansionCalculator.FromPositionAnchor(
                Config.CompactPlacement?.PositionAnchor ?? Config.PositionAnchor);
        if (preferred is { } preferredAnchor && anchors.Remove(preferredAnchor))
        {
            anchors.Insert(0, preferredAnchor);
        }

        return anchors;
    }

    private static List<WidgetCompactExpansionAnchor> ResolveFloatingAnchorOrder(
        bool preferRightAnchor,
        bool preferBottomAnchor)
    {
        WidgetCompactExpansionAnchor preferred = (preferRightAnchor, preferBottomAnchor) switch
        {
            (true, true) => WidgetCompactExpansionAnchor.RightBottom,
            (true, false) => WidgetCompactExpansionAnchor.RightTop,
            (false, true) => WidgetCompactExpansionAnchor.LeftBottom,
            _ => WidgetCompactExpansionAnchor.LeftTop
        };
        return preferred switch
        {
            WidgetCompactExpansionAnchor.RightBottom =>
                [preferred, WidgetCompactExpansionAnchor.RightTop, WidgetCompactExpansionAnchor.LeftBottom, WidgetCompactExpansionAnchor.LeftTop],
            WidgetCompactExpansionAnchor.RightTop =>
                [preferred, WidgetCompactExpansionAnchor.RightBottom, WidgetCompactExpansionAnchor.LeftTop, WidgetCompactExpansionAnchor.LeftBottom],
            WidgetCompactExpansionAnchor.LeftBottom =>
                [preferred, WidgetCompactExpansionAnchor.LeftTop, WidgetCompactExpansionAnchor.RightBottom, WidgetCompactExpansionAnchor.RightTop],
            _ =>
                [preferred, WidgetCompactExpansionAnchor.LeftBottom, WidgetCompactExpansionAnchor.RightTop, WidgetCompactExpansionAnchor.RightBottom]
        };
    }

    private void ApplyCollapsedStateImmediately(bool collapsed)
    {
        CancelDeferredExpandedContentHydration();
        StopCollapseAnimation();
        WidgetShellControl.CancelResponsiveLayoutTransition();
        _targetCollapsed = collapsed;
        IsWidgetCollapsedBoundsActive = collapsed;
        _compactState = collapsed ? WidgetCompactState.Collapsed : WidgetCompactState.Expanded;
        UpdateCompactViewState();
        if (!collapsed)
        {
            CancelCollapsedHibernationTimers();
            EnsureCollapsedContentViewAttached();
        }
        else
        {
            ArmCollapsedContentHibernationTimer();
        }

        WidgetShellControl.SetCollapsed(collapsed, SettingsService.Settings.WidgetCompactContentMode);
        OnCompactVisualStateChanged(collapsed);
        if (IsKeepCapsuleContentWarmEnabled)
        {
            App.Current?.ReconcileQuiescenceWorkingSetTrimSchedule();
        }
        RefreshCompactPresentation();

        if (!collapsed)
        {
            WidgetShellControl.ClearCompactBitmaps();
            StopCompactHoverRecoveryProbe();
            if (UsesCompactExpansionGeometry())
            {
                RectInt32 expandedCurrent = GetCurrentWindowBounds();
                RectInt32 compact = GetStableCompactBounds(expandedCurrent);
                EnsureCompactPlacement(compact);
                WidgetCompactExpansionLayout layout =
                    ResolveRequestedCompactExpansion(compact);
                _compactExpansionAnchor = layout.Anchor;
                MoveWindowWithoutPersisting(layout.ExpandedBounds);
            }
            return;
        }

        RectInt32 current = GetCurrentWindowBounds();
        RectInt32 target = GetCompactBounds(current);
        EnsureCompactPlacement(target);
        target = GetCompactBounds(current);
        _stableCompactBounds = target;
        MoveWindowWithoutPersisting(target);
        ApplyCompactSurfaceState();
        StartCompactBoundsSettlement();
        StartCompactHoverRecoveryProbe();
    }

    protected void SettleCompactBoundsAfterHostShown()
    {
        if (!_collapseInitialized || !_targetCollapsed || IsClosing)
        {
            return;
        }

        EnsureCurrentCompactBounds();
        StartCompactBoundsSettlement();
        StartCompactHoverRecoveryProbe();
        SynchronizeCompactHoverFromCurrentCursor();
        if (IsKeepCapsuleContentWarmEnabled)
        {
            QueueCompactExpansionWarmup();
        }
    }

    private void StartCompactBoundsSettlement()
    {
        CancelTimer(ref _compactBoundsSettleTimer);
        _compactBoundsSettleStage = 0;
        ScheduleNextCompactBoundsSettlement();
    }

    private void ScheduleNextCompactBoundsSettlement()
    {
        if (!_targetCollapsed ||
            _compactBoundsSettleStage >= CompactBoundsSettleDelaysMs.Length)
        {
            return;
        }

        int delay = CompactBoundsSettleDelaysMs[_compactBoundsSettleStage++];
        ScheduleTimer(ref _compactBoundsSettleTimer, delay, () =>
        {
            EnsureCurrentCompactBounds();
            ScheduleNextCompactBoundsSettlement();
        });
    }

    private void EnsureCurrentCompactBounds()
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            _isCollapseAnimationRendering ||
            IsClosing ||
            IsDragging ||
            IsResizing ||
            TrayAnimation.IsPositionTransitionActive)
        {
            return;
        }

        RectInt32 current = GetCurrentWindowBounds();
        RectInt32 target = GetCompactBounds(current);
        if (!BoundsEqual(current, target))
        {
            App.LogVerbose(
                $"[CompactBounds] settle {Config.Name}#{Config.Id} " +
                $"current=({current.X},{current.Y},{current.Width},{current.Height}) " +
                $"target=({target.X},{target.Y},{target.Width},{target.Height})");
            MoveWindowWithoutPersisting(target);
        }
    }

    /// <summary>
    /// Manager-driven compact-state request (H4): transitions the real window
    /// into or out of the collapsed capsule without persisting a manual
    /// collapse preference — used by the disconnect-collapse policy, which
    /// owns its own persisted marker instead.
    /// </summary>
    public void RequestCompactState(bool collapsed, bool animate)
    {
        if (collapsed == IsCompactBoundsStateActive)
        {
            return;
        }

        SetCollapsedState(collapsed, persistManualState: false, animate: animate);
    }

    private void SetCollapsedState(
        bool collapsed,
        bool persistManualState,
        bool animate,
        int? durationMs = null,
        bool allowDuringInteraction = false,
        bool requireHoverEligibility = false,
        bool allowInteractionRegionHoverEligibility = false,
        bool bypassExpansionReadiness = false,
        WidgetCompactTransitionReason transitionReason =
            WidgetCompactTransitionReason.Interaction)
    {
        if (!_collapseInitialized || IsClosing ||
            (collapsed && !allowDuringInteraction &&
                (_isBoundsInteractionActive || _compactInteractionDepth > 0 || HasBlockingFlyoutOpen())))
        {
            return;
        }

        bool wasTargetCollapsed = _targetCollapsed;
        bool compactBoundsWereActive = IsWidgetCollapsedBoundsActive;
        bool transitionWasActive =
            _isCollapseAnimationRendering || _isShellTransitionActive;
        bool expandingFromCompact = !collapsed &&
            (_targetCollapsed || IsWidgetCollapsedBoundsActive);
        WidgetCompactExpansionLayout? preparedExpansionLayout = null;

        if (collapsed && !_targetCollapsed)
        {
            // A shrinking/moving HWND can pass beneath a stationary pointer.
            // Block routed hover expansion until that pointer physically exits
            // the settled capsule, otherwise collapse can reverse mid-flight.
            _suppressSmartExpansionUntilPointerExit = true;
            CancelTimer(ref _collapseHoverTimer);
        }

        if (collapsed)
        {
            CancelDeferredExpandedContentHydration();
            CancelPendingCompactExpansion();
            if (!wasTargetCollapsed &&
                !IsCollapsedContentHibernated &&
                WidgetShellControl.ShellContent is not null &&
                !WidgetShellControl.IsCompactDeferredContentPresentationActive)
            {
                _isCompactExpansionWarmed = true;
                _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
                _compactExpansionWorkingSetEpoch = App.WorkingSetTrimEpoch;
            }

            if (!_targetCollapsed && UsesCompactExpansionGeometry())
            {
                // First collapse computes its bounds in place but never
                // persists them (feedback 340): restore-period expanded
                // geometry must not become the widget's capsule placement.
                EnsureCompactPlacementFromExpandedBounds();
            }
        }

        if (!collapsed &&
            _targetCollapsed &&
            UsesCompactExpansionGeometry())
        {
            if (expandingFromCompact &&
                // Re-sync only an existing placement. When the capsule bounds
                // were transiently derived (no placement yet), capturing here
                // would launder restore-period geometry into a permanent
                // placement the user never committed (feedback 340).
                Config.CompactPlacement is not null &&
                WidgetCompactTransitionPolicy.ShouldCaptureCurrentCompactPlacement(
                    transitionReason,
                    wasTargetCollapsed,
                    compactBoundsWereActive,
                    transitionWasActive))
            {
                // Capture exactly the capsule that the user can currently see.
                // CapturePlacement no longer rewrites its edge from the chosen
                // expansion direction, so this is a zero-movement state sync.
                CaptureCompactPlacement(GetCurrentWindowBounds(), persist: false);
            }

            RectInt32 compact = GetStableCompactBounds(GetCurrentWindowBounds());
            WidgetCompactExpansionLayout readinessLayout =
                ResolveRequestedCompactExpansion(compact);
            preparedExpansionLayout = readinessLayout;
        }

        bool restoresPersistedExpandedPlacement =
            !collapsed &&
            transitionReason == WidgetCompactTransitionReason.CollapseBehaviorDisabled;
        if (restoresPersistedExpandedPlacement)
        {
            CancelPendingCompactExpansion();
            bypassExpansionReadiness = true;
        }

        int plannedExpandDurationMs = !collapsed && animate
            ? ResolveCompactTransitionDuration(durationMs)
            : 0;
        bool willAnimateExpandMorph = !collapsed &&
            _targetCollapsed &&
            !restoresPersistedExpandedPlacement &&
            plannedExpandDurationMs > 0 &&
            WidgetCompactAnimationCoordinator.HasBoundsTransitionCapacity &&
            WidgetCompactTransitionVisualProfile.Resolve(
                SettingsService.Settings.WidgetCompactAnimationEffect,
                plannedExpandDurationMs,
                SystemAnimationsEnabled()).IsAnimated;
        bool deferExpandedContentHydration =
            willAnimateExpandMorph && !IsWarmLiveContentExpansionReady;

        if (!collapsed)
        {
            CancelCollapsedHibernationTimers();
            if (deferExpandedContentHydration)
            {
                // Shell-first expansion: never block or drop morph frames on cold
                // XAML construction, first-pass layout, or post-trim page faults.
                // The capsule shell expands immediately at full frame rate and
                // hydrates + fades in leaf content once the panel settles.
                CancelPendingCompactExpansion();
                CancelCompactExpansionWarmup();
                bypassExpansionReadiness = true;
                _perfDeferredHoldMs = 0;
            }
            else
            {
                if (EnsureCollapsedContentViewAttached())
                {
                    InvalidateCompactExpansionReadiness();
                }

                if (!willAnimateExpandMorph)
                {
                    bypassExpansionReadiness = true;
                }
            }
        }

        if (!collapsed &&
            _targetCollapsed &&
            !bypassExpansionReadiness &&
            WidgetCompactExpansionReadinessPolicy.Decide(
                IsCompactExpansionReady,
                deadlineElapsed: false) ==
                WidgetCompactExpansionReadinessDecision.WaitForWarmup)
        {
            DeferCompactExpansionUntilReady(
                persistManualState,
                animate,
                durationMs,
                allowDuringInteraction,
                requireHoverEligibility,
                allowInteractionRegionHoverEligibility);
            return;
        }

        if (persistManualState && Config.IsCollapsed != collapsed)
        {
            Config.IsCollapsed = collapsed;
            // Manual expand/collapse ends disconnect-collapse tracking
            // (spec 5.7): reconnect must not auto-expand a surface the
            // user touched while its home display was away.
            App.Current?.WidgetManager?.ClearDisconnectCollapseMarker(Config);
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
            SynchronizeWidgetGroupLayout();
        }

        if (!bypassExpansionReadiness)
        {
            _perfDeferredHoldMs = 0;
        }
        _perfWasWarmed = IsWarmLiveContentExpansionReady;
        string contentMode = SettingsService.Settings.WidgetCompactContentMode;
        long perfRefreshStart = Stopwatch.GetTimestamp();
        RefreshCompactPresentation();
        _perfSetStateRefreshMs = Stopwatch.GetElapsedTime(perfRefreshStart).TotalMilliseconds;

        if (collapsed == _targetCollapsed && !_isCollapseAnimationRendering)
        {
            if (restoresPersistedExpandedPlacement)
            {
                ResetCompactGeometryForExpandedMode();
                RectInt32 current = GetCurrentWindowBounds();
                RectInt32 persistedExpanded = ResolvePersistedExpandedHostBounds();
                if (!BoundsEqual(current, persistedExpanded))
                {
                    _compactState = WidgetCompactState.Expanding;
                    UpdateCompactViewState();
                    StartBoundsTransition(
                        current,
                        persistedExpanded,
                        collapsed: false,
                        animate ? ResolveCompactTransitionDuration(durationMs) : 0);
                    return;
                }

                IsWidgetCollapsedBoundsActive = false;
                _compactState = WidgetCompactState.Expanded;
            }

            if (collapsed)
            {
                _compactState = WidgetCompactState.Collapsed;
            }
            WidgetShellControl.SetCollapsed(collapsed, contentMode);
            OnCompactVisualStateChanged(collapsed);
            UpdateCompactViewState();
            if (collapsed)
            {
                ApplyCompactSurfaceState();
                StartCompactHoverRecoveryProbe();
                RectInt32 current = GetCurrentWindowBounds();
                RectInt32 compact = GetCompactBounds(current);
                if (current.Width != compact.Width || current.Height != compact.Height)
                {
                    StartBoundsTransition(
                        current,
                        compact,
                        collapsed,
                        animate ? ResolveCompactTransitionDuration(durationMs) : 0);
                }
            }
            else
            {
                StopCompactHoverRecoveryProbe();
            }
            return;
        }

        _targetCollapsed = collapsed;
        OnCompactVisualStateChanged(collapsed);
        _compactState = collapsed ? WidgetCompactState.Collapsing : WidgetCompactState.Expanding;
        UpdateCompactViewState();
        _perfSetStateRaiseMs = 0;
        if (!collapsed)
        {
            StopCompactHoverRecoveryProbe();
            CancelTimer(ref _compactBoundsSettleTimer);
            if (!restoresPersistedExpandedPlacement)
            {
                long perfRaiseStart = Stopwatch.GetTimestamp();
                RaiseForExpandedState();
                _perfSetStateRaiseMs = Stopwatch.GetElapsedTime(perfRaiseStart).TotalMilliseconds;
            }
        }
        long perfResolveStart = Stopwatch.GetTimestamp();
        RectInt32 from = GetCurrentWindowBounds();
        RectInt32 to;
        WidgetCompactExpansionAnchor? transitionAnchor = null;
        PointInt32 transitionPivot = default;
        if (collapsed)
        {
            IsWidgetCollapsedBoundsActive = true;
            to = GetCompactBounds(from);
            EnsureCompactPlacement(to);
            to = GetCompactBounds(from);
            _stableCompactBounds = to;

            WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
                to,
                new SizeInt32(from.Width, from.Height),
                freezeResolvedAnchor: _compactExpansionAnchor is not null,
                requireFullSize: true);
            if (layout.CanExpand)
            {
                _compactExpansionAnchor = layout.Anchor;
                transitionAnchor = layout.Anchor;
                transitionPivot = layout.Pivot;
                if (!transitionWasActive && !BoundsEqual(from, layout.ExpandedBounds))
                {
                    from = layout.ExpandedBounds;
                    MoveWindowWithoutPersisting(from);
                }
            }
            else
            {
                // Never pre-shrink an expanded panel to a tiny constrained
                // rectangle just to begin collapse. The fallback transition
                // may move as it shrinks, but still lands on the unchanged
                // user-owned capsule bounds.
                _compactExpansionAnchor = null;
                LogCompactExpansionBlocked(to, layout);
            }
        }
        else
        {
            if (restoresPersistedExpandedPlacement)
            {
                ResetCompactGeometryForExpandedMode();
                to = ResolvePersistedExpandedHostBounds();
            }
            else
            {
                RectInt32 compactBounds = GetStableCompactBounds(from);
                WidgetCompactExpansionLayout layout = preparedExpansionLayout ??
                    ResolveRequestedCompactExpansion(compactBounds);
                _compactExpansionAnchor = layout.Anchor;
                transitionAnchor = layout.Anchor;
                transitionPivot = layout.Pivot;
                to = layout.ExpandedBounds;
            }
        }

        _perfSetStateResolveMs = Stopwatch.GetElapsedTime(perfResolveStart).TotalMilliseconds;
        StartBoundsTransition(
            from,
            to,
            collapsed,
            animate ? (collapsed ? ResolveCompactTransitionDuration(durationMs) : plannedExpandDurationMs) : 0,
            transitionAnchor,
            transitionPivot,
            deferExpandedContentHydration);
    }

    private RectInt32 ResolvePersistedExpandedHostBounds()
    {
        RectInt32 persistedContentBounds =
            WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        return ExpandContentBoundsToHost(persistedContentBounds);
    }

    private void ResetCompactGeometryForExpandedMode()
    {
        CancelTimer(ref _compactBoundsSettleTimer);
        CancelDeferredExpandedLayerRestore();
        RestoreLayerAfterExpandedState();
        _compactArrangementSizeOverride = null;
        _stableCompactBounds = null;
        _compactExpansionAnchor = null;
    }

    private void DeferCompactExpansionUntilReady(
        bool persistManualState,
        bool animate,
        int? durationMs,
        bool allowDuringInteraction,
        bool requireHoverEligibility,
        bool allowInteractionRegionDwell)
    {
        long generation = ++_compactExpansionRequestGeneration;
        // A re-request while an identical deferral is already parked must
        // inherit its hold clock: the dwelling pointer re-issues the expand
        // request every dwell cycle, and a fresh clock per request would
        // reset the 320 ms ceiling each time (2026-10-20 perf trail: eight
        // deadline extensions for a single expand).
        long requestedAt = _pendingCompactExpansion is { } parked &&
            _compactState == WidgetCompactState.ExpandPending
                ? parked.RequestedAtTimestamp
                : Stopwatch.GetTimestamp();
        _pendingCompactExpansion = new PendingCompactExpansion(
            generation,
            persistManualState,
            animate,
            durationMs,
            allowDuringInteraction,
            requireHoverEligibility,
            allowInteractionRegionDwell,
            requestedAt);
        _compactState = WidgetCompactState.ExpandPending;
        UpdateCompactViewState();

        QueueCompactExpansionWarmup(urgent: true);
        ScheduleTimer(
            ref _compactExpansionReadinessDeadlineTimer,
            WidgetCompactExpansionReadinessPolicy.DefaultDeadlineMilliseconds,
            () => HandlePendingCompactExpansionDeadline(generation));
    }

    private void HandlePendingCompactExpansionDeadline(long generation)
    {
        if (_pendingCompactExpansion?.Generation != generation)
        {
            return;
        }

        // The capsule has not moved yet. A warm-up that is alive and allowed
        // to run is worth one bounded extra hold; a live-layout fallback runs
        // the first expanded layout inside the animation's opening frames.
        // The cumulative hold keeps that "bounded" honest across repeat
        // extensions and repeat requests.
        double totalHeldMs = Stopwatch.GetElapsedTime(
            _pendingCompactExpansion.RequestedAtTimestamp).TotalMilliseconds;
        int extensionMs = WidgetCompactExpansionReadinessPolicy
            .ResolveDeadlineExtensionMilliseconds(
                warmupActive: _compactExpansionWarmupCancellation is not null,
                warmupCanRunNow: CanRunCompactExpansionWarmup(urgent: true),
                totalHeldMilliseconds: totalHeldMs);
        if (extensionMs > 0)
        {
            PerformanceLogger.Mark(
                "CompactExpansionDeadlineExtended",
                $"extensionMs={extensionMs} kind={Config.WidgetKind} id={Config.Id}");
            ScheduleTimer(
                ref _compactExpansionReadinessDeadlineTimer,
                extensionMs,
                () =>
                {
                    if (_pendingCompactExpansion?.Generation == generation)
                    {
                        ResumePendingCompactExpansion(deadlineElapsed: true);
                    }
                });
            return;
        }

        ResumePendingCompactExpansion(deadlineElapsed: true);
    }

    private void ResumePendingCompactExpansion(bool deadlineElapsed)
    {
        PendingCompactExpansion? request = _pendingCompactExpansion;
        if (request is null || IsClosing || !_targetCollapsed)
        {
            return;
        }

        WidgetCompactExpansionReadinessDecision decision =
            WidgetCompactExpansionReadinessPolicy.Decide(
                IsCompactExpansionReady,
                deadlineElapsed);
        if (decision == WidgetCompactExpansionReadinessDecision.WaitForWarmup)
        {
            return;
        }

        _pendingCompactExpansion = null;
        CancelTimer(ref _compactExpansionReadinessDeadlineTimer);
        if (request.RequireHoverEligibility &&
            (!WidgetCompactInteractionPolicy.CanHoverExpand(
                EffectiveCollapseBehavior,
                CaptureCompactInteractionSnapshot(),
                request.AllowInteractionRegionDwell) ||
             !IsPointerRoutedToThisWindow()))
        {
            _compactState = WidgetCompactState.Collapsed;
            UpdateCompactViewState();
            return;
        }

        if (decision == WidgetCompactExpansionReadinessDecision.ExpandWithLiveLayoutFallback)
        {
            App.LogVerbose(
                $"[CompactWarmup] Deadline fallback " +
                $"kind={Config.WidgetKind} id={Config.Id}");
        }

        _perfDeferredHoldMs = Stopwatch.GetElapsedTime(request.RequestedAtTimestamp).TotalMilliseconds;
        CancelCompactExpansionWarmup();
        SetCollapsedState(
            false,
            request.PersistManualState,
            request.Animate,
            durationMs: request.DurationMilliseconds,
            allowDuringInteraction: request.AllowDuringInteraction,
            requireHoverEligibility: request.RequireHoverEligibility,
            allowInteractionRegionHoverEligibility:
                request.AllowInteractionRegionDwell,
            bypassExpansionReadiness: true);
    }

    private void CancelPendingCompactExpansion()
    {
        _compactExpansionRequestGeneration++;
        _pendingCompactExpansion = null;
        _perfDeferredHoldMs = 0;
        CancelTimer(ref _compactExpansionReadinessDeadlineTimer);
    }

    private void StartBoundsTransition(
        RectInt32 from,
        RectInt32 to,
        bool collapsed,
        int durationMs,
        WidgetCompactExpansionAnchor? expansionAnchor = null,
        PointInt32 expansionPivot = default,
        bool deferContentHydration = false)
    {
        bool redirectedMidFlight = _isCollapseAnimationRendering || _isShellTransitionActive;
        double remainingShare = 1.0;
        if (redirectedMidFlight)
        {
            double previousSpan = Math.Max(
                Math.Abs(_collapseAnimationTo.Width - _collapseAnimationFrom.Width),
                Math.Abs(_collapseAnimationTo.Height - _collapseAnimationFrom.Height));
            double currentRemainingSpan = Math.Max(
                Math.Abs(to.Width - from.Width),
                Math.Abs(to.Height - from.Height));
            if (previousSpan > 1)
            {
                remainingShare = Math.Clamp(
                    currentRemainingSpan / previousSpan,
                    WidgetCompactTransitionVisualProfile.MinTurnDurationFactor,
                    1.0);
            }
        }

        CancelDeferredExpandedLayerRestore();
        CancelDeferredExpandedContentHydration();
        StopCollapseAnimation();
        if (collapsed)
        {
            RefreshCompactPresentation(reapplySurfaceState: false);
        }

        bool isInstantTransition =
            durationMs <= 0 ||
            BoundsEqual(from, to) ||
            !WidgetCompactAnimationCoordinator.HasBoundsTransitionCapacity;
        bool effectiveDeferContentHydration =
            !collapsed && deferContentHydration && !isInstantTransition;
        _deferExpandedContentHydrationForActiveTransition = effectiveDeferContentHydration;
        if (!collapsed && deferContentHydration && !effectiveDeferContentHydration)
        {
            EnsureCollapsedContentViewAttached();
        }

        _perfWorkingSetStartMB = Environment.WorkingSet / (1024d * 1024d);
        _collapseAnimationAnchor = expansionAnchor;
        _collapseAnimationPivot = expansionPivot;
        long perfFreezeStart = Stopwatch.GetTimestamp();
        double dpiScale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        WidgetShellControl.BeginResponsiveLayoutTransition(
            collapsed,
            to.Width / Math.Max(0.01, dpiScale),
            to.Height / Math.Max(0.01, dpiScale),
            expansionAnchor ?? WidgetCompactExpansionAnchor.LeftTop,
            frozenWindowWidth: (collapsed ? from.Width : to.Width) / Math.Max(0.01, dpiScale),
            frozenWindowHeight: (collapsed ? from.Height : to.Height) / Math.Max(0.01, dpiScale),
            hasHorizontalMorph: Math.Abs(from.Width - to.Width) > 2,
            deferContentPresentation: effectiveDeferContentHydration);
        _perfFreezeLayoutMs = Stopwatch.GetElapsedTime(perfFreezeStart).TotalMilliseconds;
        long generation = ++_collapseAnimationGeneration;

        if (isInstantTransition)
        {
            // Instant transition: no rendering frames will fire, so apply
            // the backdrop synchronously (no animation to stutter).
            if (!collapsed)
            {
                ApplyBackdropPreference();
            }
            MoveWindowWithoutPersisting(to);
            CompleteBoundsTransition(collapsed, generation);
            return;
        }

        WidgetCompactTransitionVisualProfile resolvedProfile =
            WidgetCompactTransitionVisualProfile.Resolve(
                SettingsService.Settings.WidgetCompactAnimationEffect,
                durationMs,
                SystemAnimationsEnabled());
        int effectiveDurationMs = resolvedProfile.ResolveEffectiveDurationMilliseconds(
            collapsed,
            remainingShare);
        int activeDurationMs = effectiveDurationMs > 0 ? effectiveDurationMs : durationMs;

        _collapseAnimationFrom = from;
        _collapseAnimationTo = to;
        _collapseAnimationDurationMs = activeDurationMs;
        _collapseAnimationRedirectedMidFlight = redirectedMidFlight;
        long transitionPrepStarted = Stopwatch.GetTimestamp();
        int refreshRateHz = WidgetAnimationDisplayTiming.GetRefreshRateHz(HWnd);
        _collapseAnimationLastCommittedBounds = from;
        _collapseAnimationPacing.Reset(
            0,
            WidgetCompactAnimationCoordinator.GetFrameBudgetMilliseconds(HWnd));
        string cornerPreference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            SettingsService.Settings.WidgetCornerPreference);
        string mediaCornerMode = WindowsCompatibilityService.ResolveEffectiveWidgetCompactMediaCornerMode(
            SettingsService.Settings.WidgetCompactMediaCornerMode);
        ApplyCompactBorderVisuals();
        long perfShellPrepStart = Stopwatch.GetTimestamp();
        _perfBorderAndDpiMs = Stopwatch.GetElapsedTime(transitionPrepStarted, perfShellPrepStart).TotalMilliseconds;
        _collapseAnimationVisualProfile = resolvedProfile with
        {
            DurationMilliseconds = activeDurationMs
        };
        _isShellTransitionActive = WidgetShellControl.PrepareCompactTransition(
            collapsed,
            GetCornerRadiusFromPreference(),
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                mediaCornerMode,
                cornerPreference),
            _collapseAnimationVisualProfile,
            deferContentPresentation: effectiveDeferContentHydration);
        // Anchor the animation clock after preparation: the refresh-rate
        // query, border visuals, and composition animation starts above used
        // to consume the opening progress, skipping the first eased frames.
        _collapseAnimationStarted = Stopwatch.GetTimestamp();
        _perfPrepareShellMs = Stopwatch.GetElapsedTime(perfShellPrepStart, _collapseAnimationStarted).TotalMilliseconds;
        PerformanceLogger.Mark(
            "CompactBoundsTransitionPrep",
            $"prepMs={Stopwatch.GetElapsedTime(transitionPrepStarted, _collapseAnimationStarted).TotalMilliseconds:F1} " +
            $"durationMs={activeDurationMs} kind={Config.WidgetKind} id={Config.Id}");
        _compactAnimationFrameTracker = new WidgetCompactAnimationFrameTracker(
            _collapseAnimationStarted,
            refreshRateHz);
        _isCollapseAnimationRendering = true;
        _perfXamlSizeChanges = 0;
        _perfXamlLayouts = 0;
        _perfPeakConcurrent = Math.Max(1, WidgetCompactAnimationCoordinator.ActiveBoundsTransitionCount + 1);
        AttachCompactPerfLayoutHooks();
        long perfPostClockStart = Stopwatch.GetTimestamp();
        _collapseAnimationFrameRegistration?.Dispose();
        _collapseAnimationFrameRegistration =
            WidgetCompactAnimationCoordinator.RegisterBoundsTransition(CollapseAnimationRendering, HWnd);
        OnCompactBoundsTransitionActiveChanged(true);
        SimplifyBackdropForInteraction();
        ScheduleTimer(
            ref _collapseAnimationWatchdogTimer,
            Math.Max(300, activeDurationMs + 320),
            CompleteBoundsTransitionAfterTimeout);
        _perfPostClockStartMs = Stopwatch.GetElapsedTime(perfPostClockStart).TotalMilliseconds;
    }

    private void CompleteBoundsTransitionAfterTimeout()
    {
        if (!_isCollapseAnimationRendering)
        {
            return;
        }

        bool collapsed = _targetCollapsed;
        long generation = _collapseAnimationGeneration;
        bool holdTransitionForDeferredHydration =
            !collapsed && _deferExpandedContentHydrationForActiveTransition;
        App.Log($"[Compact] Bounds transition watchdog recovered generation={generation}");
        StopCollapseAnimation(
            cancelShellTransition: false,
            completeMetrics: false,
            keepBoundsTransitionActive: holdTransitionForDeferredHydration);
        CommitCollapseAnimationBounds(
            _collapseAnimationTo,
            Stopwatch.GetTimestamp(),
            generation,
            () => FinishBoundsTransition(collapsed, generation, "watchdog-recovered"));
    }

    /// <summary>
    /// Announces that a compact bounds-transition animation now owns (or has
    /// released) the UI thread's frame budget. Contents that push incremental
    /// visual updates — icon hydration batches above all — can hold them until
    /// the transition releases. Instant transitions never announce activation.
    /// </summary>
    protected virtual void OnCompactBoundsTransitionActiveChanged(bool isActive)
    {
    }

    private void CollapseAnimationRendering()
    {
        long frameTimestamp = Stopwatch.GetTimestamp();
        _perfPeakConcurrent = Math.Max(
            _perfPeakConcurrent,
            WidgetCompactAnimationCoordinator.ActiveBoundsTransitionCount);
        double frameBudgetMs = WidgetCompactAnimationCoordinator.GetFrameBudgetMilliseconds(HWnd);
        _compactAnimationFrameTracker?.RecordTick(frameTimestamp, frameBudgetMs);
        double elapsedMs = Stopwatch.GetElapsedTime(_collapseAnimationStarted, frameTimestamp).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMs / Math.Max(1, _collapseAnimationDurationMs), 0, 1);
        if (!_collapseAnimationPacing.ShouldSubmit(
                elapsedMs,
                frameBudgetMs,
                force: progress >= 1))
        {
            return;
        }

        double eased = _collapseAnimationVisualProfile.EaseSurfaceProgress(
            _targetCollapsed,
            progress,
            _collapseAnimationRedirectedMidFlight);
        RectInt32 bounds = _collapseAnimationAnchor is { } expansionAnchor
            ? WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
                _collapseAnimationFrom,
                _collapseAnimationTo,
                _collapseAnimationPivot,
                expansionAnchor,
                eased)
            : InterpolateBounds(_collapseAnimationFrom, _collapseAnimationTo, eased);
        WidgetShellControl.SetCompactTransitionProgress(_targetCollapsed, progress, eased);

        if (progress < 1)
        {
            CommitCollapseAnimationBounds(bounds, frameTimestamp, _collapseAnimationGeneration);
            return;
        }

        bool collapsed = _targetCollapsed;
        long generation = _collapseAnimationGeneration;
        bool holdTransitionForDeferredHydration =
            !collapsed && _deferExpandedContentHydrationForActiveTransition;
        StopCollapseAnimation(
            cancelShellTransition: false,
            completeMetrics: false,
            keepBoundsTransitionActive: holdTransitionForDeferredHydration);
        CommitCollapseAnimationBounds(
            _collapseAnimationTo,
            frameTimestamp,
            generation,
            () => FinishBoundsTransition(collapsed, generation));
    }

    private void CommitCollapseAnimationBounds(
        RectInt32 bounds,
        long frameTimestamp,
        long generation,
        Action? completed = null)
    {
        bool boundsChanged = !BoundsEqual(_collapseAnimationLastCommittedBounds, bounds);
        void OnCommitted()
        {
            if (generation != _collapseAnimationGeneration)
            {
                return;
            }

            long timestamp = Stopwatch.GetTimestamp();
            double workMs = Stopwatch.GetElapsedTime(frameTimestamp, timestamp).TotalMilliseconds;
            _collapseAnimationPacing.RecordSubmission(
                Stopwatch.GetElapsedTime(_collapseAnimationStarted, frameTimestamp).TotalMilliseconds,
                workMs);
            if (boundsChanged)
            {
                _collapseAnimationLastCommittedBounds = bounds;
                _compactAnimationFrameTracker?.RecordBoundsUpdate(timestamp, workMs);
            }
            completed?.Invoke();
        }

        void OnCommitFailed()
        {
            if (generation != _collapseAnimationGeneration)
            {
                return;
            }

            // A failed native move must release frozen layout and animation
            // ownership, but must never be counted as a successful submission.
            StopCollapseAnimation(cancelShellTransition: false, completeMetrics: false);
            App.Log($"[Compact] Bounds submission failed generation={generation}");
            FinishBoundsTransition(_targetCollapsed, generation, "bounds-failed");
        }

        if (boundsChanged || completed is not null)
        {
            MoveWindowWithoutPersisting(bounds,
                suppressRedraw: completed is null,
                committed: OnCommitted,
                commitFailed: OnCommitFailed);
        }
        else
        {
            OnCommitted();
        }
    }

    private void FinishBoundsTransition(bool collapsed, long generation, string outcome = "completed")
    {
        DetachCompactPerfLayoutHooks();
        CompleteCompactAnimationMetrics(outcome, deferPerfLog: true);
        long completeStart = Stopwatch.GetTimestamp();
        CompleteBoundsTransition(collapsed, generation);
        double completeMs = Stopwatch.GetElapsedTime(completeStart).TotalMilliseconds;
        if (_pendingCompactPerfLine is { } perfLine)
        {
            _pendingCompactPerfLine = null;
            App.Log($"[CompactPerf] {perfLine} completeMs={completeMs:F1}");
        }
        _perfDeferredHoldMs = 0;
    }

    private void CompleteBoundsTransition(bool collapsed, long generation)
    {
        RestoreBackdropAfterInteraction();
        UpdateCustomPanoramaViewport();
        if (generation != _collapseAnimationGeneration || collapsed != _targetCollapsed)
        {
            CancelDeferredExpandedContentHydration();
            return;
        }

        bool deferContentHydration = !collapsed && _deferExpandedContentHydrationForActiveTransition;
        _deferExpandedContentHydrationForActiveTransition = false;
        bool anchorsBottom = _compactExpansionAnchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;

        WidgetShellControl.CompleteCompactTransition(
            collapsed,
            SettingsService.Settings.WidgetCompactContentMode,
            keepContentDeferred: deferContentHydration);
        WidgetShellControl.CompleteResponsiveLayoutTransition();
        _isShellTransitionActive = false;
        IsWidgetCollapsedBoundsActive = collapsed;
        OnCompactVisualStateChanged(collapsed);
        _compactState = collapsed
            ? WidgetCompactState.Collapsed
            : _dragExpandedFromCollapsed
                ? WidgetCompactState.DropExpanded
                : _compactInteractionDepth > 0
                    ? WidgetCompactState.Interacting
                    : UsesSmartCollapseBehavior()
                        ? _isSmartPinnedOpen
                            ? WidgetCompactState.ExpandedPinned
                            : WidgetCompactState.ExpandedTransient
                        : WidgetCompactState.Expanded;
        UpdateCompactViewState();
        ApplyWindowCornerPreference();
        if (IsKeepCapsuleContentWarmEnabled)
        {
            App.Current?.ReconcileQuiescenceWorkingSetTrimSchedule();
        }
        if (collapsed)
        {
            ApplyCompactSurfaceState();
            ScheduleExpandedLayerRestoreAfterFrameCommit(generation);
            ArmCollapsedContentHibernationTimer();
            StartCompactHoverRecoveryProbe();
            SynchronizeCompactHoverFromCurrentCursor();
            QueueCompactExpansionWarmup();
        }
        else
        {
            WidgetShellControl.ClearCompactBitmaps();
            StopCompactHoverRecoveryProbe();
            ApplyBackdropPreference();
            if (UsesSmartCollapseBehavior() && !_isSmartPinnedOpen)
            {
                ScheduleSmartCollapse(SmartCollapseProbeMs);
            }

            if (deferContentHydration)
            {
                ScheduleDeferredExpandedContentHydration(generation, anchorsBottom);
                return;
            }

            _isCompactExpansionWarmed = true;
            _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
            _compactExpansionWorkingSetEpoch = App.WorkingSetTrimEpoch;
        }

        OnCompactExpansionReady();
    }

    private void ScheduleDeferredExpandedContentHydration(
        long transitionGeneration,
        bool anchorsBottom)
    {
        long hydrationGeneration = ++_deferredExpandedContentHydrationGeneration;
        if (_deferredContentHydrationFrameHandler is { } previousHandler)
        {
            CompositionTarget.Rendering -= previousHandler;
            _deferredContentHydrationFrameHandler = null;
        }

        CancelTimer(ref _deferredContentHydrationFallbackTimer);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_deferredContentHydrationFrameHandler, handler))
            {
                _deferredContentHydrationFrameHandler = null;
            }

            HydrateDeferredExpandedContent(
                hydrationGeneration,
                transitionGeneration,
                anchorsBottom);
        };
        _deferredContentHydrationFrameHandler = handler;
        CompositionTarget.Rendering += handler;
        ScheduleTimer(
            ref _deferredContentHydrationFallbackTimer,
            48,
            () => HydrateDeferredExpandedContent(
                hydrationGeneration,
                transitionGeneration,
                anchorsBottom));
    }

    private void HydrateDeferredExpandedContent(
        long hydrationGeneration,
        long transitionGeneration,
        bool anchorsBottom)
    {
        if (hydrationGeneration != _deferredExpandedContentHydrationGeneration)
        {
            return;
        }

        _deferredExpandedContentHydrationGeneration++;
        if (_deferredContentHydrationFrameHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _deferredContentHydrationFrameHandler = null;
        }

        CancelTimer(ref _deferredContentHydrationFallbackTimer);
        bool releaseTransitionHold = _isHoldingBoundsTransitionForDeferredHydration;
        _isHoldingBoundsTransitionForDeferredHydration = false;

        if (IsClosing ||
            !_collapseInitialized ||
            _targetCollapsed ||
            _isCollapseAnimationRendering ||
            transitionGeneration != _collapseAnimationGeneration)
        {
            if (releaseTransitionHold)
            {
                OnCompactBoundsTransitionActiveChanged(false);
            }

            return;
        }

        long hydrateStarted = Stopwatch.GetTimestamp();
        try
        {
            EnsureCollapsedContentViewAttached();
            WidgetShellControl.RevealDeferredExpandedContent(
                animate: SystemAnimationsEnabled(),
                anchorsBottom: anchorsBottom);
        }
        finally
        {
            if (releaseTransitionHold)
            {
                OnCompactBoundsTransitionActiveChanged(false);
            }
        }

        if (WidgetShellControl.ShellContent is not null)
        {
            _isCompactExpansionWarmed = true;
            _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
            _compactExpansionWorkingSetEpoch = App.WorkingSetTrimEpoch;
        }

        double hydrateMs = Stopwatch.GetElapsedTime(hydrateStarted).TotalMilliseconds;
        PerformanceLogger.Mark(
            "CompactDeferredContentHydrated",
            $"hydrateMs={hydrateMs:F1} hasContent={WidgetShellControl.ShellContent is not null} " +
            $"kind={Config.WidgetKind} id={Config.Id}");
        App.LogVerbose(
            $"[CompactPerf] DeferredContentHydrated kind={Config.WidgetKind} id={Config.Id} " +
            $"hydrateMs={hydrateMs:F1} hasContent={WidgetShellControl.ShellContent is not null}");
        OnCompactExpansionReady();
    }

    private void CancelDeferredExpandedContentHydration()
    {
        _deferredExpandedContentHydrationGeneration++;
        _deferExpandedContentHydrationForActiveTransition = false;
        if (_deferredContentHydrationFrameHandler is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _deferredContentHydrationFrameHandler = null;
        }

        CancelTimer(ref _deferredContentHydrationFallbackTimer);
        if (_isHoldingBoundsTransitionForDeferredHydration)
        {
            _isHoldingBoundsTransitionForDeferredHydration = false;
            OnCompactBoundsTransitionActiveChanged(false);
        }
    }

    protected void ApplyCompactSurfaceState()
    {
        ApplyBackdropPreference();
        ApplyCompactBorderVisuals();
        string preference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            SettingsService.Settings.WidgetCornerPreference);
        string mediaCornerMode = WindowsCompatibilityService.ResolveEffectiveWidgetCompactMediaCornerMode(
            SettingsService.Settings.WidgetCompactMediaCornerMode);
        double outerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(preference);
        WidgetShellControl.SetCompactCornerRadii(
            outerRadius,
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(preference),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                mediaCornerMode,
                preference));

    }

    protected bool CanResizeCurrentWidgetState(string? direction)
    {
        return WidgetCompactInteractionPolicy.CanResize(
            _isCollapseAnimationRendering,
            IsCompactBoundsStateActive,
            direction);
    }

    protected (int MinWidth, int MaxWidth) GetCompactPhysicalWidthLimits()
    {
        double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        double maximumWidth = UsesAlignedCompactWidth()
            ? WidgetCompactBoundsCalculator.MaxAlignedWidth
            : WidgetCompactBoundsCalculator.MaxWidth;
        return (
            Math.Max(1, (int)Math.Round(WidgetCompactBoundsCalculator.MinWidth * scale)),
            Math.Max(1, (int)Math.Round(maximumWidth * scale)));
    }

    protected void PersistCompletedWidgetResize(RectInt32 bounds)
    {
        if (_isDisplayTopologyTransitionActive)
        {
            return;
        }

        if (IsCompactBoundsStateActive)
        {
            bool hadArrangementSizeConstraint =
                _compactArrangementSizeOverride is not null;
            if (hadArrangementSizeConstraint)
            {
                // A capsule-bar arrangement stores a physical size override.
                // If it keeps the pre-drag width until the manager refreshes,
                // compact-bound settlement can visibly snap a Win10 HWND back
                // on pointer release. Make the just-resized size authoritative
                // for that hand-off window.
                _compactArrangementSizeOverride = new SizeInt32(
                    bounds.Width,
                    bounds.Height);
            }

            double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
            double logicalWidth = bounds.Width / Math.Max(scale, 0.01);
            double normalizedWidth = UsesAlignedCompactWidth()
                ? WidgetCompactBoundsCalculator.ClampAlignedLogicalWidth(logicalWidth)
                : WidgetCompactBoundsCalculator.ClampLogicalWidth(logicalWidth);
            if (UsesAlignedCompactWidth())
            {
                Config.Width = normalizedWidth;
                Config.CompactWidth = null;
            }
            else
            {
                Config.CompactWidth = normalizedWidth;
            }
            CaptureCompactPlacement(bounds, persist: false);
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
            SynchronizeWidgetGroupLayout();
            App.Current?.WidgetManager?.RefreshCapsuleBarLayout();
            App.Log(
                $"[Resize] Compact width committed kind={Config.WidgetKind} " +
                $"id={Config.Id} hwnd=0x{HWnd.ToInt64():X} " +
                $"physical={bounds.Width} logical={normalizedWidth:0.##} " +
                $"arranged={hadArrangementSizeConstraint}");
            return;
        }

        if (UsesCompactExpansionGeometry())
        {
            // A completed manual resize is the source of truth: never re-anchor
            // the panel back to the capsule corner (that used to snap the panel
            // back to its pre-resize position). Only clamp into the work area,
            // then let the capsule follow the adjusted panel below.
            bounds = ClampBoundsIntoWorkArea(bounds, ResolveCompactWorkArea(bounds));
            RectInt32 current = GetCurrentWindowBounds();
            if (!BoundsEqual(current, bounds))
            {
                MoveWindowWithoutPersisting(bounds);
            }

            SynchronizeAlignedCompactBoundsAfterExpandedResize(bounds);
        }

        CapturePositionAnchor(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            preserveCurrentEdge: true);
        if (UsesCompactExpansionGeometry())
        {
            RecaptureCompactPlacementAfterExpandedResize(bounds);
        }

        UpdateConfigBoundsFromPhysical(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            persist: true);
        // Resize keeps its specialized anchor capture above; this commit only
        // updates the surface home / entry authority (usually a no-op for
        // home since a resize rarely crosses displays).
        App.Current?.WidgetManager?.CommitUserPlacement(
            Config,
            CollapseHostBoundsToContent(bounds),
            bounds,
            WidgetPlacementSource.Resize,
            captureIntent: false);
        App.Current?.WidgetManager?.RefreshCapsuleBarLayout();
    }

    protected RectInt32 AnchorExpandedResizeBounds(RectInt32 proposedBounds)
    {
        if (IsCompactBoundsStateActive)
        {
            return proposedBounds;
        }

        if (UsesAlignedCompactWidth() &&
            ResizeDirection is "Left" or "Right" &&
            _expandedInteractionStartBounds is { } expandedStart &&
            _compactInteractionStartBounds is { } compactStart &&
            Math.Abs(expandedStart.Width - compactStart.Width) <= 1)
        {
            WidgetCompactExpansionAnchor activeAnchor = _compactExpansionAnchor ??
                WidgetCompactExpansionCalculator.FromPositionAnchor(
                    Config.CompactPlacement?.PositionAnchor ?? Config.PositionAnchor) ??
                WidgetCompactExpansionAnchor.LeftTop;
            _compactExpansionAnchor =
                WidgetCompactExpansionCalculator.ResolveHorizontalResizeAnchor(
                    activeAnchor,
                    ResizeDirection);
        }

        // During a manual resize the grabbed edge must stay the free edge.
        // Re-anchoring to the capsule corner here would pin the anchor-side
        // edges and mirror the drag onto the opposite side, so the user's
        // proposed bounds are applied as-is. The capsule relationship is
        // re-established from the final bounds when the resize completes.
        if (!string.IsNullOrEmpty(ResizeDirection))
        {
            return proposedBounds;
        }

        return AnchorExpandedBoundsToCompact(proposedBounds);
    }

    private void RecaptureCompactPlacementAfterExpandedResize(RectInt32 expandedBounds)
    {
        if (Config.CompactPlacement is null)
        {
            return;
        }

        // The capsule keeps the corner it actually occupied when the resize
        // began. Config.PositionAnchor is the monitor-edge home for topology
        // restore and may legitimately disagree with the capsule's corner;
        // deriving the capsule from it here once pinned capsules to the
        // panel's bottom edge after height resizes. Observed geometry is
        // authoritative, the expansion anchor is the fallback, and the
        // position anchor is only a last resort.
        WidgetCompactExpansionAnchor anchor = ResolveObservedCompactAnchor() ??
            _compactExpansionAnchor ??
            WidgetCompactExpansionCalculator.FromPositionAnchor(
                Config.PositionAnchor) ??
            WidgetCompactExpansionAnchor.LeftTop;

        bool aligned = UsesAlignedCompactWidth();
        double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        // In aligned mode the capsule width tracks the panel width. Derive it from
        // the final physical bounds because Config.Width is only updated later by
        // UpdateConfigBoundsFromPhysical.
        double? compactWidth = aligned
            ? expandedBounds.Width / Math.Max(scale, 0.01)
            : Config.CompactWidth;
        RectInt32 compactBounds = WidgetCompactBoundsCalculator.Calculate(
            expandedBounds,
            WidgetCompactExpansionCalculator.ToPositionAnchor(anchor),
            scale,
            ResolveEffectiveCompactContentMode(),
            Config.WidgetKind,
            compactWidth,
            clampCustomWidth: !aligned,
            heightContentMode: SettingsService.Settings.WidgetCompactContentMode);
        CaptureCompactPlacement(compactBounds, persist: false);
        _compactExpansionAnchor = anchor;
    }

    private WidgetCompactExpansionAnchor? ResolveObservedCompactAnchor()
    {
        if (_expandedInteractionStartBounds is not { } expandedStart ||
            _compactInteractionStartBounds is not { } compactStart)
        {
            return null;
        }

        return WidgetCompactExpansionCalculator.ResolveAnchorFromObservedCompactBounds(
            compactStart,
            expandedStart);
    }

    private void SynchronizeAlignedCompactBoundsAfterExpandedResize(RectInt32 expandedBounds)
    {
        if (!UsesAlignedCompactWidth() ||
            ResizeDirection is not ("Left" or "Right") ||
            _compactInteractionStartBounds is not { } compactStart)
        {
            return;
        }

        double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        double logicalWidth = WidgetCompactBoundsCalculator.ClampAlignedLogicalWidth(
            expandedBounds.Width / Math.Max(scale, 0.01));
        int compactWidth = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        int compactX = ResizeDirection == "Left"
            ? compactStart.X + compactStart.Width - compactWidth
            : compactStart.X;
        var compactBounds = new RectInt32(
            compactX,
            compactStart.Y,
            compactWidth,
            compactStart.Height);
        compactBounds = ClampBoundsIntoWorkArea(
            compactBounds,
            ResolveCompactWorkArea(compactBounds));
        if (_compactArrangementSizeOverride is not null)
        {
            _compactArrangementSizeOverride = new SizeInt32(
                compactBounds.Width,
                compactBounds.Height);
        }
        CaptureCompactPlacement(compactBounds, persist: false);
    }

    protected RectInt32 CompleteExpandedWidgetDrag(
        RectInt32 finalBounds,
        bool coordinateCapsuleBar = true)
    {
        if (!UsesCompactExpansionGeometry() ||
            IsCompactBoundsStateActive ||
            _expandedInteractionStartBounds is not { } expandedStart ||
            _compactInteractionStartBounds is not { } compactStart)
        {
            return finalBounds;
        }

        int deltaX = finalBounds.X - expandedStart.X;
        int deltaY = finalBounds.Y - expandedStart.Y;
        if (deltaX == 0 && deltaY == 0)
        {
            return finalBounds;
        }

        var shiftedCompact = new RectInt32(
            compactStart.X + deltaX,
            compactStart.Y + deltaY,
            compactStart.Width,
            compactStart.Height);
        if (coordinateCapsuleBar &&
            App.Current?.WidgetManager?.MoveCapsuleBarFromExpandedWidget(
                Config.Id,
                deltaX,
                deltaY,
                out RectInt32 arrangedCompact) == true)
        {
            shiftedCompact = arrangedCompact;
        }
        else
        {
            RectInt32 workArea = ResolveCompactWorkArea(shiftedCompact);
            shiftedCompact = ClampBoundsIntoWorkArea(shiftedCompact, workArea);
            CaptureCompactPlacement(shiftedCompact, persist: false);
        }
        // The drop position is authoritative: never pull the panel back onto
        // a capsule anchor corner. Re-resolve the anchor from the capsule's
        // new position only as bookkeeping for the next collapse/expand cycle;
        // a stale pre-drag direction must not survive into it.
        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            shiftedCompact,
            new SizeInt32(finalBounds.Width, finalBounds.Height),
            requireFullSize: true);
        _compactExpansionAnchor = layout.CanExpand ? layout.Anchor : null;

        RectInt32 clamped = ClampBoundsIntoWorkArea(
            finalBounds,
            ResolveCompactWorkArea(finalBounds));
        if (!BoundsEqual(finalBounds, clamped))
        {
            MoveWindowWithoutPersisting(clamped);
        }

        return clamped;
    }

    private void ReanchorExpandedToCompact(RectInt32 compactBounds, bool preserveAnchor)
    {
        if (!UsesCompactExpansionGeometry() ||
            IsCompactBoundsStateActive ||
            _isBoundsInteractionActive ||
            _isCollapseAnimationRendering ||
            IsClosing)
        {
            return;
        }

        RectInt32 current = GetCurrentWindowBounds();
        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            compactBounds,
            new SizeInt32(current.Width, current.Height),
            freezeResolvedAnchor: preserveAnchor && _compactExpansionAnchor is not null,
            requireFullSize: true);
        if (!layout.CanExpand)
        {
            _compactExpansionAnchor = null;
            LogCompactExpansionBlocked(compactBounds, layout);
            return;
        }
        _compactExpansionAnchor = layout.Anchor;
        if (!BoundsEqual(current, layout.ExpandedBounds))
        {
            MoveWindowWithoutPersisting(layout.ExpandedBounds);
        }
    }

    private void CaptureExpandedPairInteraction()
    {
        _expandedInteractionStartBounds = null;
        _compactInteractionStartBounds = null;
        if (!UsesCompactExpansionGeometry() || IsCompactBoundsStateActive)
        {
            return;
        }

        RectInt32 expanded = GetCurrentWindowBounds();
        RectInt32 compact = GetStableCompactBounds(expanded);

        // Defensive: if the resolved compact bounds land on a different
        // display than the expanded window, the persisted CompactPlacement
        // is stale (e.g. the widget was moved across monitors while in
        // Expanded mode, or the display configuration changed).  Discard
        // the stale placement and recompute from the current expanded
        // bounds so the drag pair stays consistent.
        var expandedDisplay = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
            expanded, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var compactDisplay = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
            compact, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (expandedDisplay.DisplayId.Value != compactDisplay.DisplayId.Value)
        {
            Config.CompactPlacement = null;
            InvalidateStableCompactBounds();
            compact = GetCompactBounds(expanded);
            _stableCompactBounds = compact;
        }

        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            compact,
            new SizeInt32(expanded.Width, expanded.Height),
            freezeResolvedAnchor: _compactExpansionAnchor is not null,
            requireFullSize: true);
        _compactExpansionAnchor = layout.CanExpand ? layout.Anchor : null;
        _expandedInteractionStartBounds = expanded;
        _compactInteractionStartBounds = compact;
    }

    private RectInt32 AnchorExpandedBoundsToCompact(RectInt32 expandedBounds)
    {
        if (!UsesCompactExpansionGeometry() || IsCompactBoundsStateActive)
        {
            return expandedBounds;
        }

        RectInt32 compact = _compactInteractionStartBounds ??
            GetStableCompactBounds(expandedBounds);
        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            compact,
            new SizeInt32(expandedBounds.Width, expandedBounds.Height),
            freezeResolvedAnchor: _compactExpansionAnchor is not null,
            requireFullSize: true);
        if (!layout.CanExpand)
        {
            _compactExpansionAnchor = null;
            return expandedBounds;
        }
        _compactExpansionAnchor = layout.Anchor;
        return layout.ExpandedBounds;
    }

    private static RectInt32 ClampBoundsIntoWorkArea(RectInt32 bounds, RectInt32 workArea)
    {
        int width = Math.Min(Math.Max(1, bounds.Width), Math.Max(1, workArea.Width));
        int height = Math.Min(Math.Max(1, bounds.Height), Math.Max(1, workArea.Height));
        int maxX = workArea.X + workArea.Width - width;
        int maxY = workArea.Y + workArea.Height - height;
        return new RectInt32(
            Math.Clamp(bounds.X, workArea.X, maxX),
            Math.Clamp(bounds.Y, workArea.Y, maxY),
            width,
            height);
    }

    protected RectInt32 ResolveWidgetBoundsForCurrentState() =>
        ResolveWidgetBoundsForCurrentState(out _);

    protected RectInt32 ResolveWidgetBoundsForCurrentState(out bool placementFellBack)
    {
        RectInt32 contentBounds =
            WidgetPositioningService.ResolveBoundsForCurrentTopology(
                Config,
                out placementFellBack);
        RectInt32 compact = GetCompactBounds(contentBounds);
        if (IsCompactBoundsStateActive)
        {
            return compact;
        }

        if (!UsesCompactExpansionGeometry())
        {
            return ExpandContentBoundsToHost(contentBounds);
        }

        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            compact,
            requireFullSize: true);
        if (!layout.CanExpand)
        {
            // A fixed direction is a hard user constraint. During restore keep
            // the persisted expanded bounds rather than restoring a tiny,
            // work-area-clamped rectangle.
            LogCompactExpansionBlocked(compact, layout);
            return ExpandContentBoundsToHost(contentBounds);
        }
        _compactExpansionAnchor = layout.Anchor;
        return ExpandContentBoundsToHost(layout.ExpandedBounds);
    }

    private RectInt32 GetCompactBounds(RectInt32 expandedOrCurrent)
    {
        // During WM_DPICHANGED, XamlRoot.RasterizationScale can still describe
        // the previous monitor. Resolve the scale from the target bounds so the
        // expanded and compact calculations use the same stable display DPI.
        RectInt32 targetWorkArea = ResolveCompactWorkArea(expandedOrCurrent);
        double scale = WidgetPositioningService.GetDpiScale(targetWorkArea);
        string contentMode = ResolveEffectiveCompactContentMode();

        RectInt32 resolved = WidgetCompactBoundsCalculator.Resolve(
            Config,
            expandedOrCurrent,
            scale,
            contentMode,
            alignToExpandedWidth: UsesAlignedCompactWidth(),
            heightContentMode: SettingsService.Settings.WidgetCompactContentMode);
        if (_compactArrangementSizeOverride is { } arrangedSize)
        {
            resolved = new RectInt32(
                resolved.X,
                resolved.Y,
                Math.Max(1, arrangedSize.Width),
                // Arrangement previews may retain a size captured before the
                // content mode changed. Height is a global visual invariant;
                // always use the freshly resolved capsule height.
                resolved.Height);
        }
        return _stableCompactBounds is { } stable
            ? WidgetCompactBoundsCalculator.ApplySizeToStablePlacement(
                stable,
                resolved.Width,
                resolved.Height,
                Config.CompactPlacement?.PositionAnchor ?? Config.PositionAnchor)
            : resolved;
    }

    private RectInt32 GetStableCompactBounds(RectInt32 fallback)
    {
        RectInt32 resolved = GetCompactBounds(fallback);
        _stableCompactBounds ??= resolved;
        return resolved;
    }

    protected void InvalidateStableCompactBounds()
    {
        _stableCompactBounds = null;
    }

    private void EnsureCompactPlacement(RectInt32 bounds)
    {
        if (Config.CompactPlacement is not null)
        {
            return;
        }

        // Memo-only (feedback 340): collapse transitions stabilize their
        // capsule bounds in the visual memo, but a placement is never created
        // here — only user placement commits and the capsule bar arrangement
        // write persisted capsule coordinates.
        _stableCompactBounds = bounds;
    }

    protected void CaptureCompactPlacement(RectInt32 bounds, bool persist)
    {
        if (persist && _isDisplayTopologyTransitionActive)
        {
            return;
        }

        _stableCompactBounds = bounds;
        WidgetCompactBoundsCalculator.CapturePlacement(
            Config,
            bounds);
        ObserveCompactOverrides();
        if (!persist)
        {
            return;
        }

        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        SynchronizeWidgetGroupLayout();
    }

    private void ObserveCompactOverrides()
    {
        _observedCompactWidth = Config.CompactWidth;
        _observedCompactPlacement = Config.CompactPlacement;
        _observedCompactExpansionDirection = EffectiveCompactExpansionDirection;
    }

    private RectInt32 GetCurrentWindowBounds()
    {
        return GetActualWindowBounds();
    }

    private void MoveWindowWithoutPersisting(
        RectInt32 bounds,
        bool suppressRedraw = false,
        Action? committed = null,
        Action? commitFailed = null)
    {
        uint flags = Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE;
        if (suppressRedraw)
        {
            // During the per-frame morph the swapchain already holds the
            // pre-laid-out content, so skip the internal BitBlt and the
            // WM_PAINT invalidation that SetWindowPos would otherwise emit.
            flags |= Win32Helper.SWP_NOREDRAW | Win32Helper.SWP_NOCOPYBITS;
        }

        if (WidgetCompactAnimationCoordinator.TryQueueBoundsMove(
            HWnd,
            bounds,
            flags,
            beforeCommit: () => IsApplyingBounds = true,
            afterCommit: success =>
            {
                IsApplyingBounds = false;
                if (success)
                {
                    committed?.Invoke();
                }
                else
                {
                    commitFailed?.Invoke();
                }
            },
            fallback: () => AppWindow.MoveAndResize(bounds)))
        {
            return;
        }

        IsApplyingBounds = true;
        try
        {
            bool moved = Win32Helper.SetWindowPos(
                HWnd,
                IntPtr.Zero,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags);
            if (!moved)
            {
                AppWindow.MoveAndResize(bounds);
            }
        }
        catch
        {
            IsApplyingBounds = false;
            commitFailed?.Invoke();
            throw;
        }
        finally
        {
            IsApplyingBounds = false;
        }
        committed?.Invoke();
    }

    private void AttachCompactPerfLayoutHooks()
    {
        if (_perfLayoutHooksAttached)
        {
            return;
        }

        _perfLayoutHooksAttached = true;
        RootElement.SizeChanged += HandleCompactPerfRootSizeChanged;
        RootElement.LayoutUpdated += HandleCompactPerfRootLayoutUpdated;
    }

    private void DetachCompactPerfLayoutHooks()
    {
        if (!_perfLayoutHooksAttached)
        {
            return;
        }

        _perfLayoutHooksAttached = false;
        RootElement.SizeChanged -= HandleCompactPerfRootSizeChanged;
        RootElement.LayoutUpdated -= HandleCompactPerfRootLayoutUpdated;
    }

    private void HandleCompactPerfRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _perfXamlSizeChanges++;
    }

    private void HandleCompactPerfRootLayoutUpdated(object? sender, object e)
    {
        _perfXamlLayouts++;
    }

    private void StopCollapseAnimation(
        bool cancelShellTransition = true,
        bool completeMetrics = true,
        bool keepBoundsTransitionActive = false)
    {
        DetachCompactPerfLayoutHooks();
        DetachCompactExpansionRenderWarmupFrameHook();
        WidgetShellControl.AbortCompactExpansionRenderWarmup();
        CancelTimer(ref _collapseAnimationWatchdogTimer);
        if (completeMetrics)
        {
            CompleteCompactAnimationMetrics();
        }
        if (!_isCollapseAnimationRendering && !_isShellTransitionActive)
        {
            if (!keepBoundsTransitionActive && _isHoldingBoundsTransitionForDeferredHydration)
            {
                _isHoldingBoundsTransitionForDeferredHydration = false;
                OnCompactBoundsTransitionActiveChanged(false);
            }

            if (cancelShellTransition)
            {
                _deferExpandedContentHydrationForActiveTransition = false;
            }

            _collapseAnimationAnchor = null;
            return;
        }

        if (keepBoundsTransitionActive)
        {
            _isHoldingBoundsTransitionForDeferredHydration = true;
        }
        else
        {
            _isHoldingBoundsTransitionForDeferredHydration = false;
            OnCompactBoundsTransitionActiveChanged(false);
        }

        _isCollapseAnimationRendering = false;
        _collapseAnimationFrameRegistration?.Dispose();
        _collapseAnimationFrameRegistration = null;
        if (_isShellTransitionActive)
        {
            if (cancelShellTransition)
            {
                _deferExpandedContentHydrationForActiveTransition = false;
                WidgetShellControl.CancelCompactTransition();
            }

            _isShellTransitionActive = false;
        }
        _collapseAnimationAnchor = null;
    }

    private void CompleteCompactAnimationMetrics(string outcome = "cancelled", bool deferPerfLog = false)
    {
        WidgetCompactAnimationFrameTracker? tracker = _compactAnimationFrameTracker;
        if (tracker is null)
        {
            return;
        }

        _compactAnimationFrameTracker = null;
        string tickSeries = tracker.FormatTickIntervals();
        WidgetCompactAnimationFrameSummary summary = tracker.Complete(Stopwatch.GetTimestamp());
        double wsEndMB = Environment.WorkingSet / (1024d * 1024d);
        string perfSummary =
            $"kind={Config.WidgetKind} id={Config.Id} collapsed={_targetCollapsed} outcome={outcome} " +
            $"concurrent={_perfPeakConcurrent} warmed={_perfWasWarmed} holdMs={_perfDeferredHoldMs:F1} " +
            $"wsMB={_perfWorkingSetStartMB:F1}->{wsEndMB:F1} " +
            $"preMs={_perfSetStateRefreshMs:F1}/{_perfSetStateRaiseMs:F1}/{_perfSetStateResolveMs:F1} " +
            $"prepMs={_perfFreezeLayoutMs:F1}/{_perfBorderAndDpiMs:F1}/{_perfPrepareShellMs:F1}/{_perfPostClockStartMs:F1} " +
            $"ticks={summary.FrameCount} boundsUpdates={summary.BoundsUpdateCount} missed={summary.EstimatedDroppedFrames} " +
            $"firstTickMs={summary.FirstFrameIntervalMilliseconds:F1} " +
            $"maxTickMs={summary.MaximumFrameIntervalMilliseconds:F1}@{summary.MaximumFrameIntervalIndex} " +
            $"maxWorkMs={summary.MaximumSubmissionWorkMilliseconds:F1} " +
            $"elapsedMs={summary.ElapsedMilliseconds:F1}/{_collapseAnimationDurationMs} " +
            $"xaml={_perfXamlSizeChanges}/{_perfXamlLayouts} " +
            $"tickMs=[{tickSeries}]";
        if (deferPerfLog)
        {
            _pendingCompactPerfLine = perfSummary;
        }
        else
        {
            _pendingCompactPerfLine = null;
            App.Log($"[CompactPerf] {perfSummary} completeMs=0.0");
            _perfDeferredHoldMs = 0;
        }
        string details =
            $"kind={Config.WidgetKind} id={Config.Id} collapsed={_targetCollapsed} " +
            $"outcome={outcome} initialRefreshHz={summary.RefreshRateHz} ticks={summary.FrameCount} " +
            $"boundsUpdates={summary.BoundsUpdateCount} " +
            $"estimatedMissedTicks={summary.EstimatedDroppedFrames} " +
            $"maxTickMs={summary.MaximumFrameIntervalMilliseconds:F1} " +
            $"maxBoundsIntervalMs={summary.MaximumBoundsUpdateIntervalMilliseconds:F1} " +
            $"maxWorkMs={summary.MaximumSubmissionWorkMilliseconds:F1} " +
            $"targetIntervalMs={_collapseAnimationPacing.TargetIntervalMilliseconds:F1} " +
            $"initialBudgetMs={summary.FrameBudgetMilliseconds:F1} " +
            $"elapsedMs={summary.ElapsedMilliseconds:F1}";
        PerformanceLogger.Mark("CompactAnimation", details);
        if (summary.EstimatedDroppedFrames > 0)
        {
            App.Log($"[CompactAnimation] Frame budget missed {details}");
        }
        else
        {
            App.LogVerbose($"[CompactAnimation] {details}");
        }
    }

    protected bool UsesAlignedCompactWidth() =>
        SettingsService.NormalizeWidgetCompactWidthMode(
            SettingsService.Settings.WidgetCompactWidthMode) ==
        SettingsService.WidgetCompactWidthModeAligned;

    private void RaiseForExpandedState()
    {
        CancelDeferredExpandedLayerRestore();
        if (_isRaisedForExpandedState)
        {
            if (!WidgetLayerService.TryBringAbovePeerWidgetsAtDesktopLayer(HWnd))
            {
                WidgetLayerService.BringAbovePeerWidgets(HWnd);
            }
            AcquireExpandedWidgetLayerLease("expanded-state-reasserted");
            return;
        }

        // A tray-raised group is already above normal application windows, but
        // the expanding widget still needs to move above its sibling widgets.
        // Do not mark it for desktop-layer restoration because the manager owns
        // the raised lifetime of the whole group.
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            WidgetLayerService.BringAbovePeerWidgets(HWnd);
            HoldExpandedPeerLayerLease(restoreDesktopLayer: false);
            AcquireExpandedWidgetLayerLease("expanded-state-tray-raised");
            return;
        }

        // The widget may physically sit above application windows even though
        // the tray-raised state was already released: the release only clears
        // TopMost and leaves the windows in place (e.g. the user opened and
        // closed another window after the raise).  Marking it for desktop-layer
        // restoration here would push it to HWND_BOTTOM on collapse — below the
        // app window — while its siblings stay on top.  Detect the physical
        // Z-order instead and keep the collapse a pure visual change.
        //
        // A pinned group floats only through owner-band promotion residue (an
        // activation or an Explorer-hosted launch). Hover expansion holds no
        // interaction lease, so the group is re-bedded first; otherwise the
        // expanded capsule rides the promoted band and floats above covering
        // applications (#574). Dynamic mode keeps the float-preserving paths.
        if (!IsPhysicallyAtDesktopBottom() &&
            WidgetLayerService.UsesDesktopPinnedMode() &&
            App.Current.WidgetManager?.TryRebedDesktopPinnedGroupBeforeExpand(
                HWnd,
                "expanded-state-floating-rebed") == true &&
            IsPhysicallyAtDesktopBottom())
        {
            // Re-bedded: fall through to the bedded expand path below.
        }
        else if (!IsPhysicallyAtDesktopBottom())
        {
            // A native hover-recovery probe can expand the exposed part of a
            // capsule while another application is still foreground. Raising
            // to the top of the normal band here would overtake that app but
            // would not create a tray/session restore lifetime, leaving the
            // capsule visually stuck above it. Preserve the foreign foreground
            // and only reorder this widget among the windows behind it.
            if (WidgetLayerService.TryBringAbovePeerWidgetsBehindForeground(HWnd))
            {
                HoldExpandedPeerLayerLease(restoreDesktopLayer: false);
                AcquireExpandedWidgetLayerLease("expanded-state-behind-foreground");
                App.LogVerbose(
                    $"[ZOrder] RaiseForExpandedState: preserved foreign foreground " +
                    $"hwnd=0x{HWnd.ToInt64():X}");
                return;
            }

            App.LogVerbose(
                $"[ZOrder] RaiseForExpandedState: widget floats above app window, " +
                $"skip desktop-restore mark hwnd=0x{HWnd.ToInt64():X}");
            if (!WidgetLayerService.TryBringAbovePeerWidgetsAtDesktopLayer(HWnd))
            {
                WidgetLayerService.BringAbovePeerWidgets(HWnd);
            }
            HoldExpandedPeerLayerLease(restoreDesktopLayer: false);
            AcquireExpandedWidgetLayerLease("expanded-state-floating");
            return;
        }

        HoldExpandedPeerLayerLease(restoreDesktopLayer: true);
        if (!WidgetLayerService.TryBringAbovePeerWidgetsAtDesktopLayer(HWnd))
        {
            WidgetLayerService.BringAbovePeerWidgets(HWnd);
        }
        AcquireExpandedWidgetLayerLease("expanded-state-desktop");
    }

    private void AcquireExpandedWidgetLayerLease(string reason)
    {
        _expandedWidgetLayerLeaseGeneration =
            App.Current.WidgetManager?.AcquireExpandedWidgetLayer(HWnd, reason) ?? 0;
    }

    private bool ReleaseExpandedWidgetLayerLease(string reason)
    {
        long generation = _expandedWidgetLayerLeaseGeneration;
        _expandedWidgetLayerLeaseGeneration = 0;
        return generation > 0 &&
            App.Current.WidgetManager?.ReleaseExpandedWidgetLayer(
                HWnd,
                generation,
                reason) == true;
    }

    /// <summary>
    /// Clears an expanded-lease residue left by a hide that happened while
    /// this capsule was expanded: the manager voids its slot once the owner
    /// is invisible, but the window-local generation survives the hidden
    /// period and would keep refusing to bed after the re-show. The residue
    /// is kept only while the manager still attributes the lease here.
    /// </summary>
    protected void ReconcileStaleExpandedWidgetLayerLease()
    {
        long generation = _expandedWidgetLayerLeaseGeneration;
        if (generation == 0 ||
            App.Current.WidgetManager?.OwnsExpandedWidgetLayer(HWnd, generation) == true)
        {
            return;
        }

        _expandedWidgetLayerLeaseGeneration = 0;
        _isRaisedForExpandedState = false;
        _restoreDesktopLayerAfterExpandedState = false;
        App.LogVerbose(
            $"[ZOrder] Stale expanded lease residue cleared on show " +
            $"generation={generation} hwnd=0x{HWnd.ToInt64():X}");
    }

    private void HoldExpandedPeerLayerLease(bool restoreDesktopLayer)
    {
        _isRaisedForExpandedState = true;
        _restoreDesktopLayerAfterExpandedState = restoreDesktopLayer;
        if (restoreDesktopLayer)
        {
            IsAtDesktopLayer = false;
            KeepRaisedUntilDeactivate = true;
            RestoreDesktopLayerWhenIdle = false;
        }

        LastElevateForInteractionUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Walks the Z-order below this window and reports whether it physically
    /// rests at the desktop layer: the first visible non-DeskBox window below
    /// it must be a desktop shell window (Progman/WorkerW).  When the first
    /// foreign window below is a normal app window, this window is currently
    /// floating above that app (e.g. after a released tray raise).  Minimized
    /// and cloaked windows are skipped like the shared bedding classifier:
    /// iconic windows rest at the normal band's bottom by design, so one
    /// hanging below this widget is not evidence of floating.
    /// </summary>
    private bool IsPhysicallyAtDesktopBottom()
    {
        const long WS_MINIMIZE = 0x20000000;
        IntPtr current = Win32Helper.GetWindow(HWnd, Win32Helper.GW_HWNDNEXT);
        while (current != IntPtr.Zero)
        {
            if (Win32Helper.IsWindowVisible(current) &&
                !Win32Helper.IsWindowCloaked(current) &&
                (Win32Helper.GetWindowLongPtr(current, Win32Helper.GWL_STYLE).ToInt64() &
                    WS_MINIMIZE) == 0 &&
                !App.Current.IsDeskBoxWindow(current))
            {
                return WindowHasShellClass(current, ["Progman", "WorkerW"]);
            }

            current = Win32Helper.GetWindow(current, Win32Helper.GW_HWNDNEXT);
        }

        return true;
    }

    private static bool WindowHasShellClass(IntPtr hWnd, string[] classNames)
    {
        var buffer = new System.Text.StringBuilder(256);
        int length = Win32Helper.GetClassName(hWnd, buffer, buffer.Capacity);
        if (length <= 0)
        {
            return false;
        }

        string actual = buffer.ToString();
        foreach (string className in classNames)
        {
            if (string.Equals(actual, className, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void ScheduleExpandedLayerRestoreAfterFrameCommit(long generation)
    {
        CancelDeferredExpandedLayerRestore();
        if (!_isRaisedForExpandedState)
        {
            return;
        }

        _compactLayerRestoreGeneration = generation;
        _compactLayerRestoreCommittedFrames = 0;
        _compactLayerRestoreFrameRegistration =
            WidgetCompactAnimationCoordinator.Register(() =>
            {
                _compactLayerRestoreCommittedFrames++;
                TryCompleteDeferredExpandedLayerRestore(deadlineElapsed: false);
            }, HWnd);
        ScheduleTimer(
            ref _compactLayerRestoreFallbackTimer,
            CompactLayerRestoreFallbackMs,
            () => TryCompleteDeferredExpandedLayerRestore(deadlineElapsed: true));
    }

    private void TryCompleteDeferredExpandedLayerRestore(bool deadlineElapsed)
    {
        bool anyTransitionActive =
            _isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            WidgetCompactAnimationCoordinator.ActiveBoundsTransitionCount > 0;
        WidgetCompactLayerRestoreDecision decision =
            WidgetCompactLayerRestorePolicy.Decide(
                IsClosing,
                _collapseInitialized,
                _targetCollapsed,
                anyTransitionActive,
                _collapseAnimationGeneration,
                _compactLayerRestoreGeneration,
                _compactLayerRestoreCommittedFrames,
                deadlineElapsed);
        if (decision == WidgetCompactLayerRestoreDecision.WaitForFrameCommit)
        {
            if (deadlineElapsed && anyTransitionActive)
            {
                ScheduleTimer(
                    ref _compactLayerRestoreFallbackTimer,
                    CompactLayerRestoreFallbackMs,
                    () => TryCompleteDeferredExpandedLayerRestore(deadlineElapsed: true));
            }
            return;
        }

        CancelDeferredExpandedLayerRestore();
        if (decision == WidgetCompactLayerRestoreDecision.Restore)
        {
            RestoreLayerAfterExpandedState();
        }
    }

    private void CancelDeferredExpandedLayerRestore()
    {
        _compactLayerRestoreGeneration = 0;
        _compactLayerRestoreCommittedFrames = 0;
        _compactLayerRestoreFrameRegistration?.Dispose();
        _compactLayerRestoreFrameRegistration = null;
        CancelTimer(ref _compactLayerRestoreFallbackTimer);
    }

    private void RestoreLayerAfterExpandedState()
    {
        if (!_isRaisedForExpandedState)
        {
            return;
        }

        bool restoreDesktopLayer = _restoreDesktopLayerAfterExpandedState;
        _isRaisedForExpandedState = false;
        _restoreDesktopLayerAfterExpandedState = false;
        if (!restoreDesktopLayer)
        {
            ReleaseExpandedWidgetLayerLease("expanded-peer-layer-released");
            return;
        }

        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd);
        ReleaseExpandedWidgetLayerLease("expanded-state-restored");
    }

    private void ScheduleSmartCollapse(int? delayMs = null)
    {
        if (!UsesSmartCollapseBehavior() || _isSmartPinnedOpen || _targetCollapsed)
        {
            return;
        }

        _compactState = WidgetCompactState.CollapsePending;
        int effectiveDelay = delayMs ?? SettingsService.NormalizeWidgetCompactCollapseDelayMs(
            SettingsService.Settings.WidgetCompactCollapseDelayMs);
        ScheduleTimer(ref _collapseLeaveTimer, effectiveDelay, () =>
        {
            if (!UsesSmartCollapseBehavior() || _isSmartPinnedOpen || _targetCollapsed)
            {
                return;
            }

            bool pointerInside = IsPointerPhysicallyInsideWindow();
            _isPointerOverWidget = pointerInside;
            WidgetCompactInteractionSnapshot snapshot = CaptureCompactInteractionSnapshot();
            if (WidgetCompactInteractionPolicy.CanAutoCollapse(
                EffectiveCollapseBehavior,
                snapshot))
            {
                SetCollapsedState(true, persistManualState: false, animate: true);
                return;
            }

            if (pointerInside)
            {
                _compactState = WidgetCompactState.ExpandedTransient;
                UpdateCompactViewState();
                ScheduleSmartCollapse(SmartCollapseProbeMs);
                return;
            }

            if (snapshot.HasActiveInteraction)
            {
                _compactState = WidgetCompactState.Interacting;
                UpdateCompactViewState();
                ScheduleSmartCollapse(SmartCollapseProbeMs);
                return;
            }
        });
    }

    private WidgetCompactInteractionSnapshot CaptureCompactInteractionSnapshot()
    {
        return new WidgetCompactInteractionSnapshot(
            IsCollapsed: _targetCollapsed,
            IsPinned: _isSmartPinnedOpen,
            IsPointerInside: _isPointerOverWidget,
            IsExpansionZoneActive: _isPointerOverCompactExpansionZone,
            IsPointerOverMoveHandle: _isPointerOverCompactMoveHandle,
            IsPointerOverActions: _isPointerOverCompactActions,
            IsDropInside: _isCompactDragInside,
            IsBoundsInteractionActive: _isBoundsInteractionActive,
            InteractionDepth: _compactInteractionDepth,
            IsDragging: IsDragging,
            IsResizing: IsResizing,
            HasBlockingSurface: HasBlockingFlyoutOpen(),
            SuppressHoverExpansion: _suppressSmartExpansionUntilPointerExit ||
                _suppressHoverExpansionAfterDragUntilPointerExit);
    }

    private void UpdateCompactViewState()
    {
        WidgetCompactViewState next = WidgetCompactInteractionPolicy.ResolveViewState(
            EffectiveCollapseBehavior,
            CaptureCompactInteractionSnapshot());
        if (_compactViewState == next)
        {
            return;
        }

        App.LogVerbose(
            $"[Compact] View state {Config.Name}#{Config.Id}: {_compactViewState} -> {next}");
        _compactViewState = next;
    }

    private int ResolveCompactTransitionDuration(int? requestedDurationMs)
    {
        int configuredDuration = requestedDurationMs ??
            SettingsService.NormalizeWidgetCompactAnimationDurationMs(
                SettingsService.Settings.WidgetCompactAnimationDurationMs);
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve(
                SettingsService.Settings.WidgetCompactAnimationEffect,
                configuredDuration,
                SystemAnimationsEnabled());
        return requestedDurationMs.HasValue && profile.IsAnimated
            ? configuredDuration
            : profile.DurationMilliseconds;
    }

    private static bool SystemAnimationsEnabled()
    {
        return WindowsCompatibilityService.ShouldAnimate;
    }

    private bool IsPointerPhysicallyInsideWindow()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return _isPointerOverWidget;
        }

        RectInt32 bounds = GetActualWindowBounds();
        return cursor.X >= bounds.X &&
            cursor.X < bounds.X + bounds.Width &&
            cursor.Y >= bounds.Y &&
            cursor.Y < bounds.Y + bounds.Height;
    }

    private OneShotTimerPool<DispatcherQueueTimer> GetOrCreateOneShotTimerPool()
    {
        return _oneShotTimerPool ??= new OneShotTimerPool<DispatcherQueueTimer>(
            createTimer: () =>
            {
                DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
                timer.IsRepeating = false;
                PerformanceLogger.RecordTransientUiTimerCreated();
                return timer;
            },
            releaseTimer: static timer =>
            {
                timer.Stop();
                PerformanceLogger.RecordTransientUiTimerReleased();
            },
            maxRetained: MaxPooledOneShotTimers);
    }

    private void ReturnOneShotTimerToPool(DispatcherQueueTimer timer)
    {
        bool forceRelease = IsClosing || !_collapseInitialized;
        if (_oneShotTimerPool is { } pool)
        {
            pool.Return(timer, forceRelease);
        }
        else
        {
            timer.Stop();
            PerformanceLogger.RecordTransientUiTimerReleased();
        }
    }

    private void ScheduleTimer(
        ref OwnedOneShotDispatcherTimer? field,
        int delayMs,
        Action action)
    {
        CancelTimer(ref field);
        DispatcherQueueTimer pooledTimer = GetOrCreateOneShotTimerPool().Rent();
        var timer = new OwnedOneShotDispatcherTimer(
            pooledTimer,
            ReturnOneShotTimerToPool,
            TimeSpan.FromMilliseconds(delayMs),
            action);
        field = timer;
        timer.Start();
    }

    private static void CancelTimer(ref OwnedOneShotDispatcherTimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    /// <summary>
    /// Owns both sides of a pooled one-shot DispatcherQueueTimer subscription.
    /// Stopping a WinRT timer is not enough to release its managed event source;
    /// the Tick handler is detached and the underlying timer is returned to the
    /// window's bounded pool so transient compact interactions allocate 0 OS handles.
    /// </summary>
    private sealed class OwnedOneShotDispatcherTimer : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly Action<DispatcherQueueTimer> _returnTimer;
        private Action? _action;
        private bool _isDisposed;

        public OwnedOneShotDispatcherTimer(
            DispatcherQueueTimer timer,
            Action<DispatcherQueueTimer> returnTimer,
            TimeSpan interval,
            Action action)
        {
            _timer = timer;
            _returnTimer = returnTimer;
            _action = action;
            _timer.IsRepeating = false;
            _timer.Interval = interval;
            _timer.Tick += Timer_Tick;
        }

        public void Start()
        {
            if (!_isDisposed)
            {
                _timer.Start();
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _action = null;
            _returnTimer(_timer);
        }

        private void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            Action? action = _action;
            Dispose();
            if (action is null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                // CoreMessagingXP bypasses Application.UnhandledException and
                // fail-fasts with 0xc000027b before any log queue flush when a
                // DispatcherQueueTimer.Tick handler throws.
                if (Application.Current is App app)
                {
                    app.ReportUnhandledUiCallbackException(
                        "WidgetWindowBase.OwnedOneShotDispatcherTimer",
                        ex);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private static RectInt32 InterpolateBounds(RectInt32 from, RectInt32 to, double progress)
    {
        return new RectInt32(
            Lerp(from.X, to.X, progress),
            Lerp(from.Y, to.Y, progress),
            Math.Max(1, Lerp(from.Width, to.Width, progress)),
            Math.Max(1, Lerp(from.Height, to.Height, progress)));
    }

    private static int Lerp(int from, int to, double progress) =>
        (int)Math.Round(from + ((to - from) * progress));

    private static bool BoundsEqual(RectInt32 left, RectInt32 right) =>
        left.X == right.X &&
        left.Y == right.Y &&
        left.Width == right.Width &&
        left.Height == right.Height;

    private sealed class CompactInteractionLease : IDisposable
    {
        private WidgetWindowBase? _owner;
        private readonly string _reason;

        public CompactInteractionLease(WidgetWindowBase owner, string reason)
        {
            _owner = owner;
            _reason = reason;
        }

        public void Dispose()
        {
            WidgetWindowBase? owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseCompactInteraction(_reason);
        }
    }
}
