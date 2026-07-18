// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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

public abstract partial class WidgetWindowBase
{
    private const int SmartCollapseProbeMs = 220;
    private const int DragRestoreDelayMs = 420;
    private static readonly int[] CompactBoundsSettleDelaysMs = [80, 320, 900];

    private DispatcherQueueTimer? _collapseHoverTimer;
    private DispatcherQueueTimer? _collapseLeaveTimer;
    private DispatcherQueueTimer? _collapseDragRestoreTimer;
    private DispatcherQueueTimer? _compactBoundsSettleTimer;
    private DispatcherQueueTimer? _collapseAnimationWatchdogTimer;
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
    private long _collapseAnimationStarted;
    private int _collapseAnimationDurationMs;
    private long _collapseAnimationGeneration;
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
    private bool _isShellTransitionActive;
    private bool _isBoundsInteractionActive;
    private bool _isRaisedForExpandedState;
    private bool _isSmartPinnedOpen;
    private int _compactInteractionDepth;
    private int _compactBoundsSettleStage;
    private WidgetCompactState _compactState = WidgetCompactState.Expanded;
    private WidgetCompactViewState _compactViewState = WidgetCompactViewState.Open;
    private WidgetCollapseBehavior _lastEffectiveCollapseBehavior = WidgetCollapseBehavior.System;
    private WidgetCompactExpansionAnchor? _compactExpansionAnchor;

    /// <summary>
    /// True while compact bounds are active or transitioning. Derived windows use this
    /// to preserve the configured expanded width and height during compact movement.
    /// </summary>
    protected bool IsWidgetCollapsedBoundsActive { get; private set; }

    protected bool IsWidgetCollapsed => _targetCollapsed;

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
        SettingsService.Settings.WidgetCapsuleModeEnabled
            ? WidgetCollapseBehaviorNames.Resolve(Config, SettingsService.Settings.WidgetCollapseBehavior)
            : WidgetCollapseBehavior.Expanded;

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

    protected void SetCollapseBehaviorOverride(WidgetCollapseBehavior behavior)
    {
        WidgetCollapseBehaviorNames.SetOverride(Config, behavior);
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        _isSmartPinnedOpen = false;
        ApplyCompactTooltips();
        ApplyEffectiveCollapseBehavior(animate: true);
    }

    protected void ResetCompactWidthOverride()
    {
        if (Config.CompactWidth is null)
        {
            return;
        }

        Config.CompactWidth = null;
        _stableCompactBounds = null;
        ObserveCompactOverrides();
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);

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
            TryScheduleCompactHoverExpansion();
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
    }

    protected void CleanupWidgetCollapse()
    {
        if (!_collapseInitialized)
        {
            return;
        }

        _collapseInitialized = false;
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
        CancelTimer(ref _collapseDragRestoreTimer);
        CancelTimer(ref _compactBoundsSettleTimer);
        CancelTimer(ref _collapseAnimationWatchdogTimer);
        StopCollapseAnimation();
        WidgetShellControl.CancelResponsiveLayoutTransition();

        WidgetShellControl.CollapseRequested -= WidgetShellControl_CollapseRequested;
        WidgetShellControl.ExpandRequested -= WidgetShellControl_ExpandRequested;
        WidgetShellControl.CompactBodyExpandRequested -= WidgetShellControl_CompactBodyExpandRequested;
        WidgetShellControl.CompactPointerEntered -= WidgetShellControl_CompactPointerEntered;
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
        App.Current.LocalizationService.LanguageChanged -= CollapseLanguageChanged;
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
        bool usesCapsuleBar = SettingsService.Settings.WidgetCapsuleModeEnabled &&
            SettingsService.NormalizeWidgetCapsuleArrangementMode(
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
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(CollapseSettingsChanged);
            return;
        }

        if (!_collapseInitialized || IsClosing)
        {
            return;
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
        ApplyCollapseBehaviorVisuals();
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
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
        SetCollapsedState(desiredCollapsed, persistManualState: false, animate: animate);
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
        bool usesCapsuleBar = SettingsService.Settings.WidgetCapsuleModeEnabled &&
            SettingsService.NormalizeWidgetCapsuleArrangementMode(
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
        _isPointerOverWidget = true;
        CancelTimer(ref _collapseLeaveTimer);
        if (!_targetCollapsed && UsesSmartCollapseBehavior())
        {
            _compactState = _isSmartPinnedOpen
                ? WidgetCompactState.ExpandedPinned
                : WidgetCompactState.ExpandedTransient;
        }
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion();
    }

    private void WidgetShellControl_CompactExpansionPointerEntered(object? sender, EventArgs e)
    {
        // Child-region entry is authoritative. Window resize and asynchronous content
        // layout can occasionally prevent WinUI from raising the matching outer event.
        _isPointerOverWidget = true;
        _isPointerOverCompactExpansionZone = true;
        _isPointerOverCompactMoveHandle = false;
        _isPointerOverCompactActions = false;
        CancelTimer(ref _collapseLeaveTimer);
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
        _isPointerOverCompactActions = true;
        CancelTimer(ref _collapseHoverTimer);
        UpdateCompactViewState();
    }

    private void WidgetShellControl_CompactActionPointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactActions = false;
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion();
    }

    private void WidgetShellControl_CompactMoveHandlePointerEntered(object? sender, EventArgs e)
    {
        _isPointerOverCompactMoveHandle = true;
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
        }
        UpdateCompactViewState();
    }

    private void WidgetShellControl_CompactMoveHandlePointerExited(object? sender, EventArgs e)
    {
        _isPointerOverCompactMoveHandle = false;
        UpdateCompactViewState();
        TryScheduleCompactHoverExpansion();
    }

    private void TryScheduleCompactHoverExpansion()
    {
        if (_collapseHoverTimer is not null ||
            !WidgetCompactInteractionPolicy.CanHoverExpand(
            EffectiveCollapseBehavior,
            CaptureCompactInteractionSnapshot()))
        {
            return;
        }

        _compactState = WidgetCompactState.ExpandPending;
        ScheduleTimer(
            ref _collapseHoverTimer,
            SettingsService.NormalizeWidgetCompactExpandDelayMs(
                SettingsService.Settings.WidgetCompactExpandDelayMs),
            () =>
            {
                _collapseHoverTimer = null;
                if (WidgetCompactInteractionPolicy.CanHoverExpand(
                    EffectiveCollapseBehavior,
                    CaptureCompactInteractionSnapshot()))
                {
                    SetCollapsedState(false, persistManualState: false, animate: true);
                }
            });
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
        UpdateCompactViewState();

        if (!_dragExpandedFromCollapsed && (_targetCollapsed || IsWidgetCollapsedBoundsActive))
        {
            _dragExpandedFromCollapsed = true;
            SetCollapsedState(
                false,
                persistManualState: false,
                animate: true,
                durationMs: Math.Min(
                    SettingsService.NormalizeWidgetCompactAnimationDurationMs(
                        SettingsService.Settings.WidgetCompactAnimationDurationMs),
                    180));
        }
    }

    private void WidgetShellControl_CompactDragLeft(object? sender, EventArgs e)
    {
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
                SetCollapsedState(true, persistManualState: false, animate: true);
            }
        });
    }

    protected bool UsesCompactExpansionGeometry()
    {
        return SettingsService.Settings.WidgetCapsuleModeEnabled &&
            EffectiveCollapseBehavior != WidgetCollapseBehavior.Expanded;
    }

    private WidgetCompactExpansionLayout ResolveCompactExpansionLayout(
        RectInt32 compactBounds,
        SizeInt32? requestedSize = null,
        bool freezeResolvedAnchor = false)
    {
        RectInt32 workArea = ResolveCompactWorkArea(compactBounds);
        SizeInt32 physicalSize = requestedSize ?? ResolveExpandedPhysicalSize(workArea);
        IReadOnlyList<WidgetCompactExpansionAnchor> anchors =
            freezeResolvedAnchor && _compactExpansionAnchor is { } activeAnchor
                ? [activeAnchor]
                : ResolveCompactExpansionAnchorOrder(compactBounds, workArea);
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
        RefreshCompactPresentation();

        if (!collapsed)
        {
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
    }

    protected void SettleCompactBoundsAfterHostShown()
    {
        if (!_collapseInitialized || !_targetCollapsed || IsClosing)
        {
            return;
        }

        EnsureCurrentCompactBounds();
        StartCompactBoundsSettlement();
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
        bool allowDuringInteraction = false)
    {
        if (!_collapseInitialized || IsClosing ||
            (collapsed && !allowDuringInteraction &&
                (_isBoundsInteractionActive || _compactInteractionDepth > 0 || HasBlockingFlyoutOpen())))
        {
            return;
        }

        if (persistManualState && Config.IsCollapsed != collapsed)
        {
            Config.IsCollapsed = collapsed;
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
        }

        string contentMode = SettingsService.Settings.WidgetCompactContentMode;
        RefreshCompactPresentation();

        if (collapsed == _targetCollapsed && !_isCollapseAnimationRendering)
        {
            if (collapsed)
            {
                _compactState = WidgetCompactState.Collapsed;
            }
            WidgetShellControl.SetCollapsed(collapsed, contentMode);
            UpdateCompactViewState();
            if (collapsed)
            {
                ApplyCompactSurfaceState();
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
            return;
        }

        bool expandingFromCompact = !collapsed &&
            (_targetCollapsed || IsWidgetCollapsedBoundsActive);
        _targetCollapsed = collapsed;
        _compactState = collapsed ? WidgetCompactState.Collapsing : WidgetCompactState.Expanding;
        UpdateCompactViewState();
        if (!collapsed)
        {
            CancelTimer(ref _compactBoundsSettleTimer);
            RaiseForExpandedState();
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
            if (expandingFromCompact)
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

        StartBoundsTransition(
            from,
            to,
            collapsed,
            animate ? ResolveCompactTransitionDuration(durationMs) : 0,
            transitionAnchor,
            transitionPivot);
    }

    private void StartBoundsTransition(
        RectInt32 from,
        RectInt32 to,
        bool collapsed,
        int durationMs,
        WidgetCompactExpansionAnchor? expansionAnchor = null,
        PointInt32 expansionPivot = default)
    {
        StopCollapseAnimation();
        _collapseAnimationAnchor = expansionAnchor;
        _collapseAnimationPivot = expansionPivot;
        double dpiScale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        WidgetShellControl.BeginResponsiveLayoutTransition(
            collapsed,
            to.Width / Math.Max(0.01, dpiScale),
            to.Height / Math.Max(0.01, dpiScale));
        if (!collapsed)
        {
            ApplyBackdropPreference();
        }
        long generation = ++_collapseAnimationGeneration;

        if (durationMs <= 0 || BoundsEqual(from, to))
        {
            MoveWindowWithoutPersisting(to);
            CompleteBoundsTransition(collapsed, generation);
            return;
        }

        _collapseAnimationFrom = from;
        _collapseAnimationTo = to;
        _collapseAnimationDurationMs = durationMs;
        _collapseAnimationStarted = Stopwatch.GetTimestamp();
        string cornerPreference = SettingsService.Settings.WidgetCornerPreference;
        ApplyCompactBorderVisuals();
        _isShellTransitionActive = WidgetShellControl.PrepareCompactTransition(
            collapsed,
            GetCornerRadiusFromPreference(),
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(cornerPreference),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                SettingsService.Settings.WidgetCompactMediaCornerMode,
                cornerPreference));
        _isCollapseAnimationRendering = true;
        CompositionTarget.Rendering -= CollapseAnimationRendering;
        CompositionTarget.Rendering += CollapseAnimationRendering;
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
        StopCollapseAnimation();
        MoveWindowWithoutPersisting(_collapseAnimationTo);
        CompleteBoundsTransition(collapsed, generation);
    }

    private void CollapseAnimationRendering(object? sender, object args)
    {
        double elapsedMs = Stopwatch.GetElapsedTime(_collapseAnimationStarted).TotalMilliseconds;
        double progress = Math.Clamp(elapsedMs / Math.Max(1, _collapseAnimationDurationMs), 0, 1);
        string effect = SettingsService.NormalizeWidgetCompactAnimationEffect(
            SettingsService.Settings.WidgetCompactAnimationEffect);
        double eased = effect == SettingsService.WidgetCompactAnimationSnappy
            ? 1 - Math.Pow(1 - progress, 5)
            : 1 - Math.Pow(1 - progress, 3);
        RectInt32 bounds = _collapseAnimationAnchor is { } expansionAnchor
            ? WidgetCompactExpansionCalculator.InterpolateAnchoredBounds(
                _collapseAnimationFrom,
                _collapseAnimationTo,
                _collapseAnimationPivot,
                expansionAnchor,
                eased)
            : InterpolateBounds(_collapseAnimationFrom, _collapseAnimationTo, eased);
        MoveWindowWithoutPersisting(bounds);
        WidgetShellControl.SetCompactTransitionProgress(_targetCollapsed, eased);

        if (progress < 1)
        {
            return;
        }

        bool collapsed = _targetCollapsed;
        long generation = _collapseAnimationGeneration;
        StopCollapseAnimation();
        MoveWindowWithoutPersisting(_collapseAnimationTo);
        CompleteBoundsTransition(collapsed, generation);
    }

    private void CompleteBoundsTransition(bool collapsed, long generation)
    {
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
            RestoreLayerAfterExpandedState();
        }
        else
        {
            ApplyBackdropPreference();
            if (UsesSmartCollapseBehavior() && !_isSmartPinnedOpen)
            {
                ScheduleSmartCollapse(SmartCollapseProbeMs);
            }
        }
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
        if (_isCollapseAnimationRendering)
        {
            return false;
        }

        return !IsCompactBoundsStateActive || direction is "Left" or "Right";
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
        if (IsCompactBoundsStateActive)
        {
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
            App.Current?.WidgetManager?.RefreshCapsuleBarLayout();
            return;
        }

        bounds = AnchorExpandedBoundsToCompact(bounds);
        RectInt32 current = GetCurrentWindowBounds();
        if (!BoundsEqual(current, bounds))
        {
            MoveWindowWithoutPersisting(bounds);
        }

        SynchronizeAlignedCompactBoundsAfterExpandedResize(bounds);

        CapturePositionAnchor(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            preserveCurrentEdge: true);
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

        return AnchorExpandedBoundsToCompact(proposedBounds);
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

    protected RectInt32 CompleteExpandedWidgetDrag(RectInt32 finalBounds)
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
        if (App.Current?.WidgetManager?.MoveCapsuleBarFromExpandedWidget(
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
        RectInt32 expanded = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        RectInt32 compact = GetCompactBounds(expanded);
        if (IsCompactBoundsStateActive)
        {
            return compact;
        }

        if (!UsesCompactExpansionGeometry())
        {
            return expanded;
        }

        WidgetCompactExpansionLayout layout = ResolveCompactExpansionLayout(compact);
        _compactExpansionAnchor = layout.Anchor;
        return layout.ExpandedBounds;
    }

    private RectInt32 GetCompactBounds(RectInt32 expandedOrCurrent)
    {
        double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        string contentMode = ResolveEffectiveCompactContentMode();

        RectInt32 resolved = WidgetCompactBoundsCalculator.Resolve(
            Config,
            expandedOrCurrent,
            scale,
            contentMode,
            alignToExpandedWidth: UsesAlignedCompactWidth());
        if (_compactArrangementSizeOverride is { } arrangedSize)
        {
            resolved = new RectInt32(
                resolved.X,
                resolved.Y,
                Math.Max(1, arrangedSize.Width),
                Math.Max(1, arrangedSize.Height));
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
        _stableCompactBounds = bounds;
        WidgetCompactBoundsCalculator.CapturePlacement(Config, bounds);
        ObserveCompactOverrides();
        if (!persist)
        {
            return;
        }

        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
    }

    private void ObserveCompactOverrides()
    {
        _observedCompactWidth = Config.CompactWidth;
        _observedCompactPlacement = Config.CompactPlacement;
    }

    private RectInt32 GetCurrentWindowBounds()
    {
        return GetActualWindowBounds();
    }

    private void MoveWindowWithoutPersisting(RectInt32 bounds)
    {
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
                Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
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

    private void StopCollapseAnimation()
    {
        CancelTimer(ref _collapseAnimationWatchdogTimer);
        if (!_isCollapseAnimationRendering && !_isShellTransitionActive)
        {
            _collapseAnimationAnchor = null;
            return;
        }

        _isCollapseAnimationRendering = false;
        CompositionTarget.Rendering -= CollapseAnimationRendering;
        if (_isShellTransitionActive)
        {
            WidgetShellControl.CancelCompactTransition();
            _isShellTransitionActive = false;
        }
        _collapseAnimationAnchor = null;
    }

    private bool UsesAlignedCompactWidth() =>
        SettingsService.NormalizeWidgetCompactWidthMode(
            SettingsService.Settings.WidgetCompactWidthMode) ==
        SettingsService.WidgetCompactWidthModeAligned;

    private void RaiseForExpandedState()
    {
        if (_isRaisedForExpandedState)
        {
            WidgetLayerService.BringAbovePeerWidgets(HWnd);
            return;
        }

        // A tray-raised group is already above normal application windows, but
        // the expanding widget still needs to move above its sibling widgets.
        // Do not mark it for desktop-layer restoration because the manager owns
        // the raised lifetime of the whole group.
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            WidgetLayerService.BringAbovePeerWidgets(HWnd);
            return;
        }

        _isRaisedForExpandedState = true;
        IsAtDesktopLayer = false;
        KeepRaisedUntilDeactivate = true;
        RestoreDesktopLayerWhenIdle = false;
        LastElevateForInteractionUtc = DateTime.UtcNow;
        WidgetLayerService.BringAbovePeerWidgets(HWnd);
    }

    private void RestoreLayerAfterExpandedState()
    {
        if (!_isRaisedForExpandedState)
        {
            return;
        }

        _isRaisedForExpandedState = false;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd);
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
        if (!SystemAnimationsEnabled())
        {
            return 0;
        }

        string effect = SettingsService.NormalizeWidgetCompactAnimationEffect(
            SettingsService.Settings.WidgetCompactAnimationEffect);
        if (effect == SettingsService.WidgetCompactAnimationNone)
        {
            return 0;
        }

        int duration = requestedDurationMs ?? SettingsService.NormalizeWidgetCompactAnimationDurationMs(
            SettingsService.Settings.WidgetCompactAnimationDurationMs);
        return duration;
    }

    private static bool SystemAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
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

    private void ScheduleTimer(ref DispatcherQueueTimer? field, int delayMs, Action action)
    {
        CancelTimer(ref field);
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        field = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static void CancelTimer(ref DispatcherQueueTimer? timer)
    {
        timer?.Stop();
        timer = null;
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
