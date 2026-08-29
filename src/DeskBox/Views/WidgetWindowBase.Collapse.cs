// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
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
    Ready
}

public abstract partial class WidgetWindowBase
{
    private const int SmartCollapseProbeMs = 220;
    private const int DragRestoreDelayMs = 420;
    private const int CompactExpansionUrgentWarmupInitialDelayMs = 16;
    private const int CompactExpansionWarmupRetryDelayMs = 320;
    private const int CompactExpansionUrgentWarmupRetryDelayMs = 48;
    private const int CompactExpansionWarmupSpacingMs = 24;
    private const int CompactExpansionPrimeNodeBudget = 48;
    private const double CompactExpansionPrimeTimeBudgetMs = 4;
    private const int CompactHoverRecoveryProbeMs = 120;
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
    private long _collapseAnimationGeneration;
    private long _compactLayerRestoreGeneration;
    private long _expandedWidgetLayerLeaseGeneration;
    private long _compactExpansionRequestGeneration;
    private PendingCompactExpansion? _pendingCompactExpansion;
    private WidgetCompactAnimationFrameTracker? _compactAnimationFrameTracker;
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
    private bool _isPointerOverWidget;
    private bool _isPointerOverCompactExpansionZone;
    private bool _isCollapseAnimationRendering;
    // Adaptive frame-skip: animations start at the display's native rate and
    // only step down (native -> ~60fps -> ~30fps) when coordinator ticks keep
    // overrunning the frame budget, so clean sub-multiples of the refresh rate
    // are preserved and high-refresh panels animate at full rate. The achieved
    // level is remembered for the session so saturated machines pay the
    // adaptation cost at most once.
    private static int s_compactSessionFrameSkipLevel = WidgetCompactFrameSkipPolicy.FullRateLevel;
    private int _collapseAnimationFrameSkipLevel = WidgetCompactFrameSkipPolicy.FullRateLevel;
    private int _collapseAnimationFrameSkip = 1;
    private int _collapseAnimationFrameIndex;
    private int _collapseAnimationRefreshRateHz;
    private double _collapseAnimationFrameBudgetMs;
    private int _collapseAnimationOverrunTicks;
    private int _collapseAnimationSampledTicks;
    private long _collapseAnimationLastTickTimestamp;
    private bool _isShellTransitionActive;
    private bool _isBoundsInteractionActive;
    private bool _isRaisedForExpandedState;
    private bool _restoreDesktopLayerAfterExpandedState;
    private int _compactLayerRestoreCommittedFrames;
    private bool _isSmartPinnedOpen;
    private bool _isTitleBarClickCollapseCandidate;
    private bool _isCompactExpansionWarmupRunning;
    private bool _isCompactExpansionWarmupUrgent;
    private bool _isCompactExpansionWarmed;
    private bool _isCompactExpansionVisualTreePrimed;
    private long _compactExpansionWarmupEpoch = -1;
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
        bool AllowInteractionRegionDwell);

    private bool IsCompactExpansionReady =>
        WidgetCompactWarmupPolicy.IsExpansionReady(
            _isCompactExpansionWarmed,
            _compactExpansionWarmupEpoch,
            App.MemoryCleanupEpoch);

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

    protected void RefreshCompactPresentation()
    {
        if (!_collapseInitialized || IsClosing)
        {
            return;
        }

        WidgetShellControl.SetCompactPresentation(CreateCompactPresentation());
        if (_targetCollapsed)
        {
            ApplyCompactSurfaceState();
        }
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

    protected void BeginTitleBarClickCollapse(PointerRoutedEventArgs e, bool isTitleArea)
    {
        CancelPendingTitleBarClickCollapse();
        if (!isTitleArea ||
            EffectiveCollapseBehavior != WidgetCollapseBehavior.Click ||
            _targetCollapsed ||
            !e.GetCurrentPoint(WidgetShellControl.TitleBar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isTitleBarClickCollapseCandidate = true;
    }

    protected void CompleteTitleBarClickCollapse(PointerRoutedEventArgs e, bool hasMoved)
    {
        bool isCandidate = _isTitleBarClickCollapseCandidate;
        _isTitleBarClickCollapseCandidate = false;
        if (!isCandidate ||
            hasMoved ||
            e.GetCurrentPoint(WidgetShellControl.TitleBar).Properties.PointerUpdateKind !=
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased ||
            EffectiveCollapseBehavior != WidgetCollapseBehavior.Click ||
            _targetCollapsed)
        {
            return;
        }

        // Let the current pointer-release handler finish its drag cleanup first,
        // then collapse on the next UI turn without a fixed click delay.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosing &&
                EffectiveCollapseBehavior == WidgetCollapseBehavior.Click &&
                !_targetCollapsed &&
                !HasBlockingFlyoutOpen())
            {
                CollapseWidgetFromHost();
            }
        });
    }

    protected void CancelPendingTitleBarClickCollapse()
    {
        _isTitleBarClickCollapseCandidate = false;
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
    /// Rebuilds the compact bounds from the currently visible expanded panel.
    /// Fixed Down keeps the panel's top/title edge; fixed Up keeps its bottom
    /// edge. Auto preserves the active expansion anchor when one exists.
    /// </summary>
    private void RefreshCompactPlacementFromExpandedBounds(bool persist)
    {
        if (_targetCollapsed || !UsesCompactExpansionGeometry() || IsClosing)
        {
            return;
        }

        WidgetCompactExpansionAnchor automaticAnchor =
            _compactExpansionAnchor ??
            WidgetCompactExpansionCalculator.FromPositionAnchor(
                Config.CompactPlacement?.PositionAnchor ?? Config.PositionAnchor) ??
            WidgetCompactExpansionAnchor.LeftTop;
        WidgetCompactExpansionAnchor anchor =
            WidgetCompactExpansionDirectionPolicy.Apply(
                SettingsService.Settings.WidgetCompactExpansionDirection,
                [automaticAnchor])[0];

        Config.CompactPlacement = null;
        InvalidateStableCompactBounds();
        RectInt32 expanded = GetCurrentWindowBounds();
        RectInt32 measuredCompact = GetCompactBounds(expanded);
        PointInt32 pivot = WidgetCompactExpansionCalculator.GetPivot(expanded, anchor);
        RectInt32 fresh = WidgetCompactExpansionCalculator.CreateBoundsFromPivot(
            pivot,
            new SizeInt32(measuredCompact.Width, measuredCompact.Height),
            anchor);
        _compactExpansionAnchor = anchor;
        CaptureCompactPlacement(fresh, persist);
    }

    private bool CompactPlacementNeedsDirectionRepair()
    {
        string direction = SettingsService.NormalizeWidgetCompactExpansionDirection(
            SettingsService.Settings.WidgetCompactExpansionDirection);
        if (direction == SettingsService.WidgetCompactExpansionDirectionAuto)
        {
            return false;
        }

        string? currentAnchor = Config.CompactPlacement?.PositionAnchor;
        return currentAnchor is null ||
            !string.Equals(
                currentAnchor,
                WidgetCompactExpansionDirectionPolicy.ApplyToPositionAnchor(
                    direction,
                    currentAnchor),
                StringComparison.Ordinal);
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
        ReleaseExpandedWidgetLayerLease("compact-cleanup");
        CancelPendingTitleBarClickCollapse();
        StopCollapseAnimation();
        WidgetShellControl.CancelResponsiveLayoutTransition();

        WidgetShellControl.CollapseRequested -= WidgetShellControl_CollapseRequested;
        WidgetShellControl.ExpandRequested -= WidgetShellControl_ExpandRequested;
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

    private void QueueCompactExpansionWarmup(bool urgent = false)
    {
        if (!_collapseInitialized ||
            !_targetCollapsed ||
            IsCompactExpansionReady ||
            IsClosing)
        {
            return;
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
            while (!token.IsCancellationRequested &&
                   _collapseInitialized &&
                   !IsClosing &&
                   !IsCompactExpansionReady)
            {
                if (!CanRunCompactExpansionWarmup(urgent))
                {
                    PerformanceLogger.Mark(
                        "CompactExpansionWarmupDeferred",
                        $"urgent={urgent} pointer={_isPointerOverWidget} " +
                        $"interactionDepth={_compactInteractionDepth} " +
                        $"windowVisible={HWnd != IntPtr.Zero && Win32Helper.IsWindowVisible(HWnd)} " +
                        $"rootLoaded={RootElement.IsLoaded} " +
                        $"contentReady={IsCompactExpansionWarmupContentReady} " +
                        $"kind={Config.WidgetKind} id={Config.Id}");
                    await Task.Delay(
                        urgent
                            ? CompactExpansionUrgentWarmupRetryDelayMs
                            : CompactExpansionWarmupRetryDelayMs,
                        token);
                    continue;
                }

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
                        result = await TryRunCompactExpansionWarmupSliceOnUiThreadAsync(
                            urgent ? "visible-critical" : "background-idle",
                            token,
                            urgent);
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
        bool applicationIdle = urgent
            ? App.Current?.CanRunCriticalCompactExpansionWarmup == true
            : App.Current?.CanRunCompactExpansionWarmup == true;
        var snapshot = new WidgetCompactWarmupSnapshot(
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
        return WidgetCompactWarmupPolicy.CanRun(snapshot);
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
                        TryRunCompactExpansionWarmupSlice(reason));
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
        string reason)
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
                $"running={_isCompactExpansionWarmupRunning} " +
                $"rootLoaded={RootElement.IsLoaded} " +
                $"contentReady={IsCompactExpansionWarmupContentReady} " +
                $"reason={reason} kind={Config.WidgetKind} id={Config.Id}");
            return WidgetCompactWarmupSliceResult.Blocked;
        }

        if (!_isCompactExpansionVisualTreePrimed)
        {
            PrimeCompactExpansionVisualTree(reason);
            return WidgetCompactWarmupSliceResult.Progressed;
        }

        _isCompactExpansionWarmupRunning = true;
        long started = Stopwatch.GetTimestamp();
        try
        {
            RectInt32 current = GetCurrentWindowBounds();
            RectInt32 compactBounds = GetStableCompactBounds(current);
            WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compactBounds);
            double dpiScale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
            string cornerPreference = SettingsService.Settings.WidgetCornerPreference;

            // Prime the small WinRT/accessibility setup work as well as the full
            // expanded XAML layout. Neither call changes the native window bounds.
            _ = ResolveCompactTransitionDuration(requestedDurationMs: null);
            ApplyCompactBorderVisuals();
            bool warmed = WidgetShellControl.WarmCompactExpansionLayout(
                layout.ExpandedBounds.Width / Math.Max(0.01, dpiScale),
                layout.ExpandedBounds.Height / Math.Max(0.01, dpiScale),
                GetCornerRadiusFromPreference(),
                WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(cornerPreference),
                WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(cornerPreference),
                WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                    SettingsService.Settings.WidgetCompactMediaCornerMode,
                    cornerPreference),
                SettingsService.Settings.WidgetCompactContentMode,
                layout.Anchor);
            if (!warmed)
            {
                PerformanceLogger.Mark(
                    "CompactExpansionWarmupLayoutRejected",
                    $"reason={reason} kind={Config.WidgetKind} id={Config.Id}");
                return WidgetCompactWarmupSliceResult.Blocked;
            }

            _isCompactExpansionWarmed = true;
            _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PerformanceLogger.Mark(
                "CompactExpansionWarmup",
                $"elapsedMs={elapsedMs:F1} reason={reason} kind={Config.WidgetKind} id={Config.Id}");
            if (elapsedMs >= 16.7)
            {
                App.Log(
                    $"[CompactWarmup] Slow layout kind={Config.WidgetKind} " +
                    $"id={Config.Id} elapsedMs={elapsedMs:F1} reason={reason}");
            }
            DispatcherQueue.TryEnqueue(OnCompactExpansionReady);
            return WidgetCompactWarmupSliceResult.Ready;
        }
        finally
        {
            _isCompactExpansionWarmupRunning = false;
        }
    }

    private void PrimeCompactExpansionVisualTree(string reason)
    {
        _compactExpansionPrimeQueue ??=
            new Queue<DependencyObject>([WidgetShellControl]);
        long started = Stopwatch.GetTimestamp();
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
            if (visited >= CompactExpansionPrimeNodeBudget ||
                Stopwatch.GetElapsedTime(started).TotalMilliseconds >=
                    CompactExpansionPrimeTimeBudgetMs)
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
        if (!_collapseInitialized || !_targetCollapsed || IsCompactExpansionReady)
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
        CancelTimer(ref _collapseHoverTimer);
        CancelPendingCompactExpansion();
        InvalidateCompactExpansionReadiness();
        CancelCompactExpansionWarmup();
        if (_targetCollapsed && _compactExpansionWarmupCancellation is null)
        {
            QueueCompactExpansionWarmup();
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
        // Re-derive the capsule from the visible panel before F7 captures its
        // resting position. This also repairs placements saved by older builds
        // with a screen-edge anchor that conflicts with the fixed direction.
        RefreshCompactPlacementFromExpandedBounds(persist: true);
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
            WidgetShellControl.ResetTransientCompactPointerState();
            ResetCompactPointerStateAfterHide();
            return;
        }

        WidgetShellControl.ResumeVisualActivity();

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
            ResetCompactPointerStateAfterPhysicalExit();
            return;
        }

        bool pointerInsideExpansionZone = IsScreenPointInsideElement(
            cursor,
            WidgetShellControl.CompactBodyElement,
            windowBounds);
        _isPointerOverWidget = true;
        if (!pointerInsideExpansionZone)
        {
            if (_isPointerOverCompactExpansionZone)
            {
                _isPointerOverCompactExpansionZone = false;
                CancelTimer(ref _collapseHoverTimer);
                UpdateCompactViewState();
            }

            // The icon/move handle and trailing action strip are deliberately
            // separate hit targets. They should still honor Smart hover after
            // a longer dwell, while a quick press continues to win and cancels
            // the pending expansion.
            TryScheduleCompactHoverExpansion(
                allowInteractionRegionDwell: true);
            return;
        }

        bool recoveredMissingRoutedEntry = !_isPointerOverCompactExpansionZone;
        _isPointerOverCompactExpansionZone = true;
        _isPointerOverCompactMoveHandle = false;
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
        FrameworkElement element,
        RectInt32 windowBounds)
    {
        if (element.Visibility != Visibility.Visible ||
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
            return;
        }

        _isPointerOverWidget = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        _suppressSmartExpansionUntilPointerExit = false;
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
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CollapseActionButton,
            localization.T("Widget.Compact.Collapse"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.OverlayDragHandleElement,
            localization.T(EffectiveCollapseBehavior == WidgetCollapseBehavior.Expanded
                ? "Widget.Compact.Move"
                : "Widget.Compact.MoveOrCollapse"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            WidgetShellControl.OverlayDragHandleElement,
            localization.T(EffectiveCollapseBehavior == WidgetCollapseBehavior.Expanded
                ? "Widget.Compact.Move"
                : "Widget.Compact.MoveOrCollapse"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CompactMoveHandleElement,
            localization.T(usesCapsuleBar ? "Widget.Compact.MoveBar" : "Widget.Compact.Move"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            WidgetShellControl.CompactMoveHandleElement,
            localization.T(usesCapsuleBar ? "Widget.Compact.MoveBar" : "Widget.Compact.Move"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CompactBodyElement,
            localization.T("Widget.Compact.Expand"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            WidgetShellControl.CompactBodyElement,
            localization.T("Widget.Compact.Expand"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CompactReorderHandleElement,
            localization.T("Widget.Compact.Reorder"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            WidgetShellControl.CompactReorderHandleElement,
            localization.T("Widget.Compact.Reorder"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CompactExpandActionButton,
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

        string compactExpansionDirection =
            SettingsService.NormalizeWidgetCompactExpansionDirection(
                SettingsService.Settings.WidgetCompactExpansionDirection);
        bool compactExpansionDirectionChanged = !string.Equals(
            _observedCompactExpansionDirection,
            compactExpansionDirection,
            StringComparison.Ordinal);
        if (compactExpansionDirectionChanged)
        {
            if (_targetCollapsed)
            {
                RectInt32 compact = GetStableCompactBounds(GetCurrentWindowBounds());
                CaptureCompactPlacement(compact, persist: true);
                _compactExpansionAnchor = null;
            }
            else if (!_targetCollapsed && UsesCompactExpansionGeometry())
            {
                RefreshCompactPlacementFromExpandedBounds(persist: true);
            }
            else
            {
                _observedCompactExpansionDirection = compactExpansionDirection;
            }
        }

        if (_observedCompactWidth != Config.CompactWidth ||
            !ReferenceEquals(_observedCompactPlacement, Config.CompactPlacement))
        {
            _stableCompactBounds = null;
            ObserveCompactOverrides();
        }

        RefreshCompactPresentation();
        ApplyCompactTooltips();
        ApplyEffectiveCollapseBehavior(animate: true);
        if (!_targetCollapsed && UsesCompactExpansionGeometry())
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
            // The expanded panel is the visual source of truth at this point.
            // Capture its direction-aware title edge before changing state;
            // doing this after SetCollapsedState would already be too late.
            RefreshCompactPlacementFromExpandedBounds(persist: true);
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
            QueueCompactExpansionWarmup();
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
        ReleaseSmartHoverSuppressionAfterSettledPointerIntent(
            establishesNewPointerIntent);
        _isPointerOverWidget = true;
        // Treat the compact surface as an expansion candidate immediately.
        // Child move/action regions cancel this state when they receive their
        // routed entry. This also covers the first hover of a no-activate desktop
        // widget when WinUI misses the text-region PointerEntered event.
        _isPointerOverCompactExpansionZone = true;
        CancelTimer(ref _collapseLeaveTimer);
        if (!_targetCollapsed && UsesSmartCollapseBehavior())
        {
            _compactState = _isSmartPinnedOpen
                ? WidgetCompactState.ExpandedPinned
                : WidgetCompactState.ExpandedTransient;
        }
        UpdateCompactViewState();
        QueueCompactExpansionWarmup(urgent: true);
        TryScheduleCompactHoverExpansion();
    }

    private void WidgetShellControl_CompactPointerMoved(object? sender, EventArgs e)
    {
        ReconcileCompactDragStateAfterPointerRelease();
        if (!_targetCollapsed ||
            !UsesSmartCollapseBehavior() ||
            _isCollapseAnimationRendering ||
            _isShellTransitionActive ||
            IsClosing ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        MarkCompactRoutedPointerActivity();

        bool recoveredMissingRoutedEntry = !_isPointerOverWidget;
        ReleaseSmartHoverSuppressionAfterSettledPointerIntent(
            recoveredMissingRoutedEntry);

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

    private void ReleaseSmartHoverSuppressionAfterSettledPointerIntent(
        bool establishesNewPointerIntent)
    {
        if (!_suppressSmartExpansionUntilPointerExit ||
            !WidgetCompactInteractionPolicy
                .CanReleaseHoverSuppressionAfterRoutedPointerIntent(
                    establishesNewPointerIntent,
                    _targetCollapsed,
                    WidgetShellControl.IsCollapsed,
                    _isCollapseAnimationRendering,
                    _isShellTransitionActive))
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
        // Child-region entry is authoritative. Window resize and asynchronous content
        // layout can occasionally prevent WinUI from raising the matching outer event.
        _isPointerOverWidget = true;
        _isPointerOverCompactExpansionZone = true;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        CancelTimer(ref _collapseLeaveTimer);
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
            TryScheduleCompactHoverExpansion(
                allowInteractionRegionDwell: true);
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
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
        }
        UpdateCompactViewState();
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
        }
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion(
            allowInteractionRegionDwell: true);
    }

    private void WidgetShellControl_CompactMoveHandlePointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactMoveHandle = false;
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion();
    }

    private void TryScheduleCompactHoverExpansion(
        bool allowInteractionRegionDwell = false)
    {
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
        ScheduleTimer(
            ref _collapseHoverTimer,
            effectiveDelay,
            () =>
            {
                _collapseHoverTimer = null;
                _collapseHoverTimerAllowsInteractionRegionDwell = false;
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
        if (!WidgetShellControl.TryClearStaleShellDragSessionAfterPointerRelease())
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

        if (!WidgetShellControl.TryClearStaleShellDragSessionAfterPointerRelease())
        {
            return;
        }

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

    private WidgetCompactExpansionLayout ResolveCompactExpansionLayout(
        RectInt32 compactBounds,
        SizeInt32? requestedSize = null,
        bool freezeResolvedAnchor = false)
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
                SettingsService.Settings.WidgetCompactExpansionDirection,
                automaticAnchors);
        return WidgetCompactExpansionCalculator.Resolve(
            compactBounds,
            physicalSize,
            workArea,
            anchors);
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
        StopCollapseAnimation();
        WidgetShellControl.CancelResponsiveLayoutTransition();
        _targetCollapsed = collapsed;
        IsWidgetCollapsedBoundsActive = collapsed;
        _compactState = collapsed ? WidgetCompactState.Collapsed : WidgetCompactState.Expanded;
        UpdateCompactViewState();
        WidgetShellControl.SetCollapsed(collapsed, SettingsService.Settings.WidgetCompactContentMode);
        OnCompactVisualStateChanged(collapsed);
        RefreshCompactPresentation();

        if (!collapsed)
        {
            StopCompactHoverRecoveryProbe();
            if (UsesCompactExpansionGeometry())
            {
                RectInt32 expandedCurrent = GetCurrentWindowBounds();
                RectInt32 compact = GetStableCompactBounds(expandedCurrent);
                EnsureCompactPlacement(compact);
                WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compact);
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
            CancelPendingCompactExpansion();
            if (!_targetCollapsed &&
                UsesCompactExpansionGeometry() &&
                CompactPlacementNeedsDirectionRepair())
            {
                RefreshCompactPlacementFromExpandedBounds(persist: true);
            }
        }

        bool restoresPersistedExpandedPlacement =
            !collapsed &&
            transitionReason == WidgetCompactTransitionReason.CollapseBehaviorDisabled;
        if (restoresPersistedExpandedPlacement)
        {
            CancelPendingCompactExpansion();
            bypassExpansionReadiness = true;
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
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
            SynchronizeWidgetGroupLayout();
        }

        string contentMode = SettingsService.Settings.WidgetCompactContentMode;
        RefreshCompactPresentation();

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

        bool wasTargetCollapsed = _targetCollapsed;
        bool compactBoundsWereActive = IsWidgetCollapsedBoundsActive;
        bool transitionWasActive =
            _isCollapseAnimationRendering || _isShellTransitionActive;
        bool expandingFromCompact = !collapsed &&
            (_targetCollapsed || IsWidgetCollapsedBoundsActive);
        _targetCollapsed = collapsed;
        OnCompactVisualStateChanged(collapsed);
        _compactState = collapsed ? WidgetCompactState.Collapsing : WidgetCompactState.Expanding;
        UpdateCompactViewState();
        if (!collapsed)
        {
            StopCompactHoverRecoveryProbe();
            CancelTimer(ref _compactBoundsSettleTimer);
            if (!restoresPersistedExpandedPlacement)
            {
                RaiseForExpandedState();
            }
        }
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
                freezeResolvedAnchor: _compactExpansionAnchor is not null);
            _compactExpansionAnchor = layout.Anchor;
            transitionAnchor = layout.Anchor;
            transitionPivot = layout.Pivot;
            if (!BoundsEqual(from, layout.ExpandedBounds))
            {
                from = layout.ExpandedBounds;
                MoveWindowWithoutPersisting(from);
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
                if (expandingFromCompact &&
                    WidgetCompactTransitionPolicy.ShouldCaptureCurrentCompactPlacement(
                        transitionReason,
                        wasTargetCollapsed,
                        compactBoundsWereActive,
                        transitionWasActive))
                {
                    CaptureCompactPlacement(from, persist: false);
                }

                RectInt32 compactBounds = GetStableCompactBounds(from);
                WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compactBounds);
                _compactExpansionAnchor = layout.Anchor;
                transitionAnchor = layout.Anchor;
                transitionPivot = layout.Pivot;
                to = layout.ExpandedBounds;
            }
        }

        StartBoundsTransition(
            from,
            to,
            collapsed,
            animate ? ResolveCompactTransitionDuration(durationMs) : 0,
            transitionAnchor,
            transitionPivot);
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
        _pendingCompactExpansion = new PendingCompactExpansion(
            generation,
            persistManualState,
            animate,
            durationMs,
            allowDuringInteraction,
            requireHoverEligibility,
            allowInteractionRegionDwell);
        _compactState = WidgetCompactState.ExpandPending;
        UpdateCompactViewState();

        QueueCompactExpansionWarmup(urgent: true);
        ScheduleTimer(
            ref _compactExpansionReadinessDeadlineTimer,
            WidgetCompactExpansionReadinessPolicy.DefaultDeadlineMilliseconds,
            () =>
            {
                if (_pendingCompactExpansion?.Generation == generation)
                {
                    ResumePendingCompactExpansion(deadlineElapsed: true);
                }
            });
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
        CancelTimer(ref _compactExpansionReadinessDeadlineTimer);
    }

    private void StartBoundsTransition(
        RectInt32 from,
        RectInt32 to,
        bool collapsed,
        int durationMs,
        WidgetCompactExpansionAnchor? expansionAnchor = null,
        PointInt32 expansionPivot = default)
    {
        CancelDeferredExpandedLayerRestore();
        StopCollapseAnimation();
        _collapseAnimationAnchor = expansionAnchor;
        _collapseAnimationPivot = expansionPivot;
        double dpiScale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        WidgetShellControl.BeginResponsiveLayoutTransition(
            collapsed,
            to.Width / Math.Max(0.01, dpiScale),
            to.Height / Math.Max(0.01, dpiScale),
            expansionAnchor ?? WidgetCompactExpansionAnchor.LeftTop,
            frozenWindowWidth: (collapsed ? from.Width : to.Width) / Math.Max(0.01, dpiScale),
            frozenWindowHeight: (collapsed ? from.Height : to.Height) / Math.Max(0.01, dpiScale));
        long generation = ++_collapseAnimationGeneration;

        if (durationMs <= 0 ||
            BoundsEqual(from, to) ||
            !WidgetCompactAnimationCoordinator.HasBoundsTransitionCapacity)
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

        _collapseAnimationFrom = from;
        _collapseAnimationTo = to;
        _collapseAnimationDurationMs = durationMs;
        _collapseAnimationStarted = Stopwatch.GetTimestamp();
        int refreshRateHz = Win32Helper.GetDisplayRefreshRateForWindow(HWnd);
        _collapseAnimationFrameSkipLevel = WidgetCompactFrameSkipPolicy.ClampLevel(
            s_compactSessionFrameSkipLevel);
        _collapseAnimationFrameSkip = WidgetCompactFrameSkipPolicy.ResolveSkip(
            refreshRateHz,
            _collapseAnimationFrameSkipLevel);
        _collapseAnimationFrameIndex = 0;
        _collapseAnimationRefreshRateHz = refreshRateHz;
        _collapseAnimationFrameBudgetMs = 1000.0 / Math.Max(1, refreshRateHz);
        _collapseAnimationOverrunTicks = 0;
        _collapseAnimationSampledTicks = 0;
        _collapseAnimationLastTickTimestamp = _collapseAnimationStarted;
        _compactAnimationFrameTracker = new WidgetCompactAnimationFrameTracker(
            _collapseAnimationStarted,
            refreshRateHz);
        string cornerPreference = SettingsService.Settings.WidgetCornerPreference;
        ApplyCompactBorderVisuals();
        _collapseAnimationVisualProfile = WidgetCompactTransitionVisualProfile.Resolve(
            SettingsService.Settings.WidgetCompactAnimationEffect,
            durationMs,
            SystemAnimationsEnabled());
        _isShellTransitionActive = WidgetShellControl.PrepareCompactTransition(
            collapsed,
            GetCornerRadiusFromPreference(),
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                SettingsService.Settings.WidgetCompactMediaCornerMode,
                cornerPreference),
            _collapseAnimationVisualProfile);
        _isCollapseAnimationRendering = true;
        _collapseAnimationFrameRegistration?.Dispose();
        _collapseAnimationFrameRegistration =
            WidgetCompactAnimationCoordinator.RegisterBoundsTransition(CollapseAnimationRendering);
        SimplifyBackdropForInteraction();
        ScheduleTimer(
            ref _collapseAnimationWatchdogTimer,
            Math.Max(300, durationMs + 320),
            CompleteBoundsTransitionAfterTimeout);
    }

    private void CompleteBoundsTransitionAfterTimeout()
    {
        if (!_isCollapseAnimationRendering)
        {
            return;
        }

        bool collapsed = _targetCollapsed;
        long generation = _collapseAnimationGeneration;
        App.Log($"[Compact] Bounds transition watchdog recovered generation={generation}");
        StopCollapseAnimation(cancelShellTransition: false);
        MoveWindowWithoutPersisting(_collapseAnimationTo);
        CompleteBoundsTransition(collapsed, generation);
    }

    private void CollapseAnimationRendering()
    {
        long frameTimestamp = Stopwatch.GetTimestamp();
        _compactAnimationFrameTracker?.RecordFrame(frameTimestamp);
        RecordCollapseAnimationTickCadence(frameTimestamp);

        // Adaptive frame-skip: advance the HWND resize only every N-th
        // coordinator tick. The level starts at the display's native rate and
        // escalates only while ticks keep missing the frame budget (see
        // RecordCollapseAnimationTickCadence). Progress is time-based, so
        // duration and easing are unchanged; the boundary updates on a
        // coarser, judder-free cadence (N stays a clean divisor-ish of the
        // refresh rate).
        _collapseAnimationFrameIndex++;
        if (_collapseAnimationFrameIndex % _collapseAnimationFrameSkip != 0)
        {
            return;
        }

        double elapsedMs = Stopwatch.GetElapsedTime(_collapseAnimationStarted, frameTimestamp).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMs / Math.Max(1, _collapseAnimationDurationMs), 0, 1);
        double eased = _collapseAnimationVisualProfile.EaseProgress(progress);
        RectInt32 bounds = _collapseAnimationAnchor is { } expansionAnchor
            ? WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
                _collapseAnimationFrom,
                _collapseAnimationTo,
                _collapseAnimationPivot,
                expansionAnchor,
                eased)
            : InterpolateBounds(_collapseAnimationFrom, _collapseAnimationTo, eased);
        MoveWindowWithoutPersisting(bounds, suppressRedraw: true);
        WidgetShellControl.SetCompactTransitionProgress(_targetCollapsed, eased);

        if (progress < 1)
        {
            return;
        }

        bool collapsed = _targetCollapsed;
        long generation = _collapseAnimationGeneration;
        StopCollapseAnimation(cancelShellTransition: false);
        MoveWindowWithoutPersisting(_collapseAnimationTo);
        CompleteBoundsTransition(collapsed, generation);
    }

    /// <summary>
    /// Samples the coordinator tick cadence during the transition and lowers
    /// the HWND resize rate one level when ticks keep missing the frame
    /// budget. The achieved level is sticky for the session so saturated
    /// machines pay the adaptation cost at most once, while machines with
    /// headroom keep animating at the display's native rate.
    /// </summary>
    private void RecordCollapseAnimationTickCadence(long frameTimestamp)
    {
        double intervalMs = Stopwatch
            .GetElapsedTime(_collapseAnimationLastTickTimestamp, frameTimestamp)
            .TotalMilliseconds;
        _collapseAnimationLastTickTimestamp = frameTimestamp;
        if (intervalMs <= 0)
        {
            return;
        }

        _collapseAnimationSampledTicks++;
        if (WidgetCompactFrameSkipPolicy.IsOverrun(
                intervalMs,
                _collapseAnimationFrameBudgetMs))
        {
            _collapseAnimationOverrunTicks++;
        }

        if (_collapseAnimationFrameSkipLevel >= WidgetCompactFrameSkipPolicy.ThirtyFpsLevel ||
            !WidgetCompactFrameSkipPolicy.ShouldEscalate(
                _collapseAnimationOverrunTicks,
                _collapseAnimationSampledTicks))
        {
            return;
        }

        _collapseAnimationFrameSkipLevel = WidgetCompactFrameSkipPolicy.Escalate(
            _collapseAnimationFrameSkipLevel);
        s_compactSessionFrameSkipLevel = _collapseAnimationFrameSkipLevel;
        _collapseAnimationFrameSkip = WidgetCompactFrameSkipPolicy.ResolveSkip(
            _collapseAnimationRefreshRateHz,
            _collapseAnimationFrameSkipLevel);
        _collapseAnimationOverrunTicks = 0;
        _collapseAnimationSampledTicks = 0;
        App.LogVerbose(
            $"[Compact] Frame budget misses persisted; HWND resize cadence " +
            $"level={_collapseAnimationFrameSkipLevel} skip={_collapseAnimationFrameSkip} " +
            $"refreshHz={_collapseAnimationRefreshRateHz}");
    }

    private void CompleteBoundsTransition(bool collapsed, long generation)
    {
        RestoreBackdropAfterInteraction();
        if (generation != _collapseAnimationGeneration || collapsed != _targetCollapsed)
        {
            return;
        }

        WidgetShellControl.CompleteCompactTransition(
            collapsed,
            SettingsService.Settings.WidgetCompactContentMode);
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
        if (collapsed)
        {
            ApplyCompactSurfaceState();
            ScheduleExpandedLayerRestoreAfterFrameCommit(generation);
            StartCompactHoverRecoveryProbe();
            SynchronizeCompactHoverFromCurrentCursor();
            QueueCompactExpansionWarmup();
        }
        else
        {
            StopCompactHoverRecoveryProbe();
            _isCompactExpansionWarmed = true;
            _compactExpansionWarmupEpoch = App.MemoryCleanupEpoch;
            ApplyBackdropPreference();
            if (UsesSmartCollapseBehavior() && !_isSmartPinnedOpen)
            {
                ScheduleSmartCollapse(SmartCollapseProbeMs);
            }
        }

        OnCompactExpansionReady();
    }

    private void ApplyCompactSurfaceState()
    {
        ApplyBackdropPreference();
        ApplyCompactBorderVisuals();
        string preference = SettingsService.Settings.WidgetCornerPreference;
        double outerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(preference);
        WidgetShellControl.SetCompactCornerRadii(
            outerRadius,
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(preference),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                SettingsService.Settings.WidgetCompactMediaCornerMode,
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
            Config.PositionAnchor,
            scale,
            ResolveEffectiveCompactContentMode(),
            Config.WidgetKind,
            compactWidth,
            clampCustomWidth: !aligned,
            heightContentMode: SettingsService.Settings.WidgetCompactContentMode);
        CaptureCompactPlacement(compactBounds, persist: false);
        _compactExpansionAnchor =
            WidgetCompactExpansionCalculator.FromPositionAnchor(Config.PositionAnchor) ??
            _compactExpansionAnchor;
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
        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(
            shiftedCompact,
            new SizeInt32(finalBounds.Width, finalBounds.Height),
            freezeResolvedAnchor: _compactExpansionAnchor is not null);
        _compactExpansionAnchor = layout.Anchor;
        if (!BoundsEqual(finalBounds, layout.ExpandedBounds))
        {
            MoveWindowWithoutPersisting(layout.ExpandedBounds);
        }
        return layout.ExpandedBounds;
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
            freezeResolvedAnchor: preserveAnchor && _compactExpansionAnchor is not null);
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
            freezeResolvedAnchor: _compactExpansionAnchor is not null);
        _compactExpansionAnchor = layout.Anchor;
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
            freezeResolvedAnchor: _compactExpansionAnchor is not null);
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

    protected RectInt32 ResolveWidgetBoundsForCurrentState()
    {
        RectInt32 contentBounds =
            WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        RectInt32 compact = GetCompactBounds(contentBounds);
        if (IsCompactBoundsStateActive)
        {
            return compact;
        }

        if (!UsesCompactExpansionGeometry())
        {
            return ExpandContentBoundsToHost(contentBounds);
        }

        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compact);
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

        CaptureCompactPlacement(bounds, persist: true);
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
            bounds,
            SettingsService.Settings.WidgetCompactExpansionDirection);
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
        _observedCompactExpansionDirection =
            SettingsService.NormalizeWidgetCompactExpansionDirection(
                SettingsService.Settings.WidgetCompactExpansionDirection);
    }

    private RectInt32 GetCurrentWindowBounds()
    {
        return GetActualWindowBounds();
    }

    private void MoveWindowWithoutPersisting(RectInt32 bounds, bool suppressRedraw = false)
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
            afterCommit: () => IsApplyingBounds = false,
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
        finally
        {
            IsApplyingBounds = false;
        }
    }

    private void StopCollapseAnimation(bool cancelShellTransition = true)
    {
        CancelTimer(ref _collapseAnimationWatchdogTimer);
        CompleteCompactAnimationMetrics();
        if (!_isCollapseAnimationRendering && !_isShellTransitionActive)
        {
            _collapseAnimationAnchor = null;
            return;
        }

        _isCollapseAnimationRendering = false;
        _collapseAnimationFrameIndex = 0;
        _collapseAnimationFrameRegistration?.Dispose();
        _collapseAnimationFrameRegistration = null;
        if (_isShellTransitionActive)
        {
            if (cancelShellTransition)
            {
                WidgetShellControl.CancelCompactTransition();
            }

            _isShellTransitionActive = false;
        }
        _collapseAnimationAnchor = null;
    }

    private void CompleteCompactAnimationMetrics()
    {
        WidgetCompactAnimationFrameTracker? tracker = _compactAnimationFrameTracker;
        if (tracker is null)
        {
            return;
        }

        _compactAnimationFrameTracker = null;
        WidgetCompactAnimationFrameSummary summary = tracker.Complete(Stopwatch.GetTimestamp());
        string details =
            $"kind={Config.WidgetKind} id={Config.Id} collapsed={_targetCollapsed} " +
            $"refreshHz={summary.RefreshRateHz} frames={summary.FrameCount} " +
            $"dropped={summary.EstimatedDroppedFrames} " +
            $"maxFrameMs={summary.MaximumFrameIntervalMilliseconds:F1} " +
            $"budgetMs={summary.FrameBudgetMilliseconds:F1} " +
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

    private bool UsesAlignedCompactWidth() =>
        SettingsService.NormalizeWidgetCompactWidthMode(
            SettingsService.Settings.WidgetCompactWidthMode) ==
        SettingsService.WidgetCompactWidthModeAligned;

    private void RaiseForExpandedState()
    {
        CancelDeferredExpandedLayerRestore();
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            // Expanding or interacting with a fixed-layer widget must not
            // acquire either a global or peer-only Z-order raise.
            _isRaisedForExpandedState = false;
            _restoreDesktopLayerAfterExpandedState = false;
            CancelPendingDesktopLayerRestore();
            _ = ReleaseExpandedWidgetLayerLease("fixed-layer-expanded");
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            App.LogVerbose(
                $"[ZOrder] RaiseForExpandedState skipped fixed-layer " +
                $"hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

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
        if (!IsPhysicallyAtDesktopBottom())
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
    /// floating above that app (e.g. after a released tray raise).
    /// </summary>
    private bool IsPhysicallyAtDesktopBottom()
    {
        IntPtr current = Win32Helper.GetWindow(HWnd, Win32Helper.GW_HWNDNEXT);
        while (current != IntPtr.Zero)
        {
            if (Win32Helper.IsWindowVisible(current) &&
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
            });
        ScheduleTimer(
            ref _compactLayerRestoreFallbackTimer,
            CompactLayerRestoreFallbackMs,
            () => TryCompleteDeferredExpandedLayerRestore(deadlineElapsed: true));
    }

    private void TryCompleteDeferredExpandedLayerRestore(bool deadlineElapsed)
    {
        WidgetCompactLayerRestoreDecision decision =
            WidgetCompactLayerRestorePolicy.Decide(
                IsClosing,
                _collapseInitialized,
                _targetCollapsed,
                _isCollapseAnimationRendering || _isShellTransitionActive,
                _collapseAnimationGeneration,
                _compactLayerRestoreGeneration,
                _compactLayerRestoreCommittedFrames,
                deadlineElapsed);
        if (decision == WidgetCompactLayerRestoreDecision.WaitForFrameCommit)
        {
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
            SuppressHoverExpansion: _suppressSmartExpansionUntilPointerExit);
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

    private void ScheduleTimer(
        ref OwnedOneShotDispatcherTimer? field,
        int delayMs,
        Action action)
    {
        CancelTimer(ref field);
        var timer = new OwnedOneShotDispatcherTimer(
            DispatcherQueue,
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
    /// Owns both sides of a one-shot DispatcherQueueTimer subscription.
    /// Stopping a WinRT timer is not enough to release its managed event source;
    /// the Tick handler must also be detached or every compact interaction leaves
    /// another timer and COM wrapper rooted for the rest of the process.
    /// </summary>
    private sealed class OwnedOneShotDispatcherTimer : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private Action? _action;
        private bool _isDisposed;

        public OwnedOneShotDispatcherTimer(
            DispatcherQueue dispatcherQueue,
            TimeSpan interval,
            Action action)
        {
            _action = action;
            _timer = dispatcherQueue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = interval;
            _timer.Tick += Timer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
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
            PerformanceLogger.RecordTransientUiTimerReleased();
        }

        private void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            Action? action = _action;
            Dispose();
            action?.Invoke();
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
