// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
    private RectInt32 _collapseAnimationFrom;
    private RectInt32 _collapseAnimationTo;
    private RectInt32? _stableCompactBounds;
    private long _collapseAnimationStarted;
    private int _collapseAnimationDurationMs;
    private long _collapseAnimationGeneration;
    private bool _collapseInitialized;
    private bool _targetCollapsed;
    private bool _dragExpandedFromCollapsed;
    private bool _isCompactDragInside;
    private bool _isPointerOverWidget;
    private bool _isCollapseAnimationRendering;
    private bool _isShellTransitionActive;
    private bool _isBoundsInteractionActive;
    private bool _isRaisedForExpandedState;
    private bool _isSmartPinnedOpen;
    private int _compactInteractionDepth;
    private int _compactBoundsSettleStage;
    private WidgetCompactState _compactState = WidgetCompactState.Expanded;
    private WidgetCollapseBehavior _lastEffectiveCollapseBehavior = WidgetCollapseBehavior.System;

    /// <summary>
    /// True while compact bounds are active or transitioning. Derived windows use this
    /// to preserve the configured expanded width and height during compact movement.
    /// </summary>
    protected bool IsWidgetCollapsedBoundsActive { get; private set; }

    protected bool IsWidgetCollapsed => _targetCollapsed;

    protected bool IsCompactBoundsStateActive =>
        IsWidgetCollapsedBoundsActive || _targetCollapsed;

    protected WidgetCollapseBehavior EffectiveCollapseBehavior =>
        SettingsService.Settings.WidgetCapsuleModeEnabled
            ? WidgetCollapseBehaviorNames.Resolve(Config, SettingsService.Settings.WidgetCollapseBehavior)
            : WidgetCollapseBehavior.Expanded;

    protected virtual bool SupportsCompactDropExpansion =>
        Config.WidgetKind is WidgetKind.File or WidgetKind.QuickCapture or WidgetKind.Todo;

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

    protected virtual Task OnCompactPlayPauseRequestedAsync() => Task.CompletedTask;

    protected virtual Task OnCompactNextRequestedAsync() => Task.CompletedTask;

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
        if (EffectiveCollapseBehavior == WidgetCollapseBehavior.Expanded)
        {
            return;
        }

        SetCollapsedState(
            true,
            persistManualState: EffectiveCollapseBehavior == WidgetCollapseBehavior.Click,
            animate: true);
    }

    protected void SetCollapseBehaviorOverride(WidgetCollapseBehavior behavior)
    {
        WidgetCollapseBehaviorNames.SetOverride(Config, behavior);
        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
        _isSmartPinnedOpen = false;
        ApplyEffectiveCollapseBehavior(animate: true);
    }

    protected void BeginCompactInteraction()
    {
        _compactInteractionDepth++;
        CancelTimer(ref _collapseLeaveTimer);
        if (UsesSmartCollapseBehavior() && !_targetCollapsed)
        {
            _compactState = WidgetCompactState.Interacting;
        }
    }

    protected void EndCompactInteraction()
    {
        _compactInteractionDepth = Math.Max(0, _compactInteractionDepth - 1);
        if (_compactInteractionDepth == 0 &&
            UsesSmartCollapseBehavior() &&
            !_isSmartPinnedOpen &&
            !_isPointerOverWidget &&
            !_isCompactDragInside)
        {
            ScheduleSmartCollapse();
        }
    }

    protected void BeginWidgetBoundsInteraction()
    {
        _isBoundsInteractionActive = true;
        BeginCompactInteraction();
        CancelTimer(ref _collapseHoverTimer);
        CancelTimer(ref _collapseLeaveTimer);
    }

    protected void EndWidgetBoundsInteraction()
    {
        _isBoundsInteractionActive = false;
        EndCompactInteraction();
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
        WidgetShellControl.CompactPointerEntered += WidgetShellControl_CompactPointerEntered;
        WidgetShellControl.CompactPointerExited += WidgetShellControl_CompactPointerExited;
        WidgetShellControl.CompactPointerPressed += WidgetShellControl_CompactPointerPressed;
        WidgetShellControl.ExpandedInteractionRequested += WidgetShellControl_ExpandedInteractionRequested;
        WidgetShellControl.CompactDragEntered += WidgetShellControl_CompactDragEntered;
        WidgetShellControl.CompactDragLeft += WidgetShellControl_CompactDragLeft;
        WidgetShellControl.CompactDropCompleted += WidgetShellControl_CompactDropCompleted;
        WidgetShellControl.CompactPreviousRequested += WidgetShellControl_CompactPreviousRequested;
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
        StopCollapseAnimation();

        WidgetShellControl.CollapseRequested -= WidgetShellControl_CollapseRequested;
        WidgetShellControl.ExpandRequested -= WidgetShellControl_ExpandRequested;
        WidgetShellControl.CompactPointerEntered -= WidgetShellControl_CompactPointerEntered;
        WidgetShellControl.CompactPointerExited -= WidgetShellControl_CompactPointerExited;
        WidgetShellControl.CompactPointerPressed -= WidgetShellControl_CompactPointerPressed;
        WidgetShellControl.ExpandedInteractionRequested -= WidgetShellControl_ExpandedInteractionRequested;
        WidgetShellControl.CompactDragEntered -= WidgetShellControl_CompactDragEntered;
        WidgetShellControl.CompactDragLeft -= WidgetShellControl_CompactDragLeft;
        WidgetShellControl.CompactDropCompleted -= WidgetShellControl_CompactDropCompleted;
        WidgetShellControl.CompactPreviousRequested -= WidgetShellControl_CompactPreviousRequested;
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
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            WidgetShellControl.CollapseActionButton,
            localization.T("Widget.Compact.Collapse"));
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

        RefreshCompactPresentation();
        ApplyEffectiveCollapseBehavior(animate: true);
    }

    private bool UsesSmartCollapseBehavior()
    {
        return EffectiveCollapseBehavior == WidgetCollapseBehavior.Smart;
    }

    private void ApplyEffectiveCollapseBehavior(bool animate)
    {
        ApplyCollapseBehaviorVisuals();
        bool desiredCollapsed = EffectiveCollapseBehavior switch
        {
            WidgetCollapseBehavior.Expanded => false,
            WidgetCollapseBehavior.Click => Config.IsCollapsed,
            WidgetCollapseBehavior.Smart => !_isSmartPinnedOpen &&
                !_isCompactDragInside &&
                !_isPointerOverWidget &&
                _compactInteractionDepth == 0,
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
        }
        bool canCollapse = behavior != WidgetCollapseBehavior.Expanded;
        WidgetShellControl.SetCollapseActionAvailable(canCollapse);
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
        WidgetCollapseBehavior behavior = EffectiveCollapseBehavior;
        SetCollapsedState(
            false,
            persistManualState: behavior == WidgetCollapseBehavior.Click,
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
        if (UsesSmartCollapseBehavior() &&
            _targetCollapsed &&
            !_isCompactDragInside)
        {
            _compactState = WidgetCompactState.ExpandPending;
            ScheduleTimer(
                ref _collapseHoverTimer,
                SettingsService.NormalizeWidgetCompactExpandDelayMs(
                    SettingsService.Settings.WidgetCompactExpandDelayMs),
                () =>
                {
                    if (_isPointerOverWidget && !IsDragging && !IsResizing)
                    {
                        SetCollapsedState(false, persistManualState: false, animate: true);
                    }
                });
        }
    }

    private void WidgetShellControl_CompactPointerExited(object? sender, EventArgs e)
    {
        _isPointerOverWidget = false;
        CancelTimer(ref _collapseHoverTimer);
        if (_targetCollapsed)
        {
            _compactState = WidgetCompactState.Collapsed;
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

    private void WidgetShellControl_ExpandedInteractionRequested(object? sender, EventArgs e)
    {
        if (!UsesSmartCollapseBehavior() || _targetCollapsed)
        {
            return;
        }

        _compactState = WidgetCompactState.Interacting;
        CancelTimer(ref _collapseLeaveTimer);
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
        if (_dragExpandedFromCollapsed)
        {
            ScheduleDragRestore(900);
        }
    }

    private async void WidgetShellControl_CompactPreviousRequested(object? sender, RoutedEventArgs e)
    {
        await OnCompactPreviousRequestedAsync();
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

    private void ApplyCollapsedStateImmediately(bool collapsed)
    {
        StopCollapseAnimation();
        _targetCollapsed = collapsed;
        IsWidgetCollapsedBoundsActive = collapsed;
        _compactState = collapsed ? WidgetCompactState.Collapsed : WidgetCompactState.Expanded;
        WidgetShellControl.SetCollapsed(collapsed, SettingsService.Settings.WidgetCollapsedStyle);
        RefreshCompactPresentation();

        if (!collapsed)
        {
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
        int? durationMs = null)
    {
        if (!_collapseInitialized || IsClosing ||
            (collapsed && (_isBoundsInteractionActive || _compactInteractionDepth > 0 || HasBlockingFlyoutOpen())))
        {
            return;
        }

        if (persistManualState && Config.IsCollapsed != collapsed)
        {
            Config.IsCollapsed = collapsed;
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
        }

        string style = SettingsService.Settings.WidgetCollapsedStyle;
        RefreshCompactPresentation();

        if (collapsed == _targetCollapsed && !_isCollapseAnimationRendering)
        {
            if (collapsed)
            {
                _compactState = WidgetCompactState.Collapsed;
            }
            WidgetShellControl.SetCollapsed(collapsed, style);
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

        _targetCollapsed = collapsed;
        _compactState = collapsed ? WidgetCompactState.Collapsing : WidgetCompactState.Expanding;
        if (!collapsed)
        {
            CancelTimer(ref _compactBoundsSettleTimer);
            RaiseForExpandedState();
        }
        RectInt32 from = GetCurrentWindowBounds();
        RectInt32 to;
        if (collapsed)
        {
            IsWidgetCollapsedBoundsActive = true;
            to = GetCompactBounds(from);
            EnsureCompactPlacement(to);
            to = GetCompactBounds(from);
            _stableCompactBounds = to;
        }
        else
        {
            to = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
            if (UsesSmartCollapseBehavior() || _dragExpandedFromCollapsed)
            {
                RectInt32 compactBounds = GetStableCompactBounds(from);
                RectInt32 workArea = Microsoft.UI.Windowing.DisplayArea.GetFromRect(
                    compactBounds,
                    Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                to = WidgetCompactBoundsCalculator.AnchorExpandedBoundsToCompact(
                    compactBounds,
                    to,
                    Config.CompactPlacement?.PositionAnchor ?? Config.PositionAnchor,
                    workArea);
            }
        }

        StartBoundsTransition(
            from,
            to,
            collapsed,
            animate ? ResolveCompactTransitionDuration(durationMs) : 0);
    }

    private void StartBoundsTransition(RectInt32 from, RectInt32 to, bool collapsed, int durationMs)
    {
        StopCollapseAnimation();
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
        string collapsedStyle = SettingsService.Settings.WidgetCollapsedStyle;
        ApplyCompactBorderVisuals();
        _isShellTransitionActive = WidgetShellControl.PrepareCompactTransition(
            collapsed,
            GetCornerRadiusFromPreference(),
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(cornerPreference, collapsedStyle),
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(cornerPreference, collapsedStyle),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                SettingsService.Settings.WidgetCompactMediaCornerMode,
                cornerPreference,
                collapsedStyle));
        _isCollapseAnimationRendering = true;
        CompositionTarget.Rendering -= CollapseAnimationRendering;
        CompositionTarget.Rendering += CollapseAnimationRendering;
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
        RectInt32 bounds = InterpolateBounds(_collapseAnimationFrom, _collapseAnimationTo, eased);
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

        WidgetShellControl.CompleteCompactTransition(collapsed, SettingsService.Settings.WidgetCollapsedStyle);
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
        string preference = SettingsService.Settings.WidgetCornerPreference;
        string style = SettingsService.Settings.WidgetCollapsedStyle;
        double outerRadius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(preference, style);
        WidgetShellControl.SetCompactCornerRadii(
            outerRadius,
            WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(preference, style),
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(
                SettingsService.Settings.WidgetCompactMediaCornerMode,
                preference,
                style));

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
        return (
            Math.Max(1, (int)Math.Round(WidgetCompactBoundsCalculator.MinWidth * scale)),
            Math.Max(1, (int)Math.Round(WidgetCompactBoundsCalculator.MaxWidth * scale)));
    }

    protected void PersistCompletedWidgetResize(RectInt32 bounds)
    {
        if (IsCompactBoundsStateActive)
        {
            double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
            double logicalWidth = bounds.Width / Math.Max(scale, 0.01);
            Config.CompactWidth = WidgetCompactBoundsCalculator.ClampLogicalWidth(logicalWidth);
            CaptureCompactPlacement(bounds, persist: false);
            SettingsService.UpdateWidget(Config, notifySubscribers: false);
            SettingsService.SaveDebounced(notifySubscribers: false);
            return;
        }

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
    }

    protected RectInt32 ResolveWidgetBoundsForCurrentState()
    {
        RectInt32 expanded = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        return IsCompactBoundsStateActive
            ? GetCompactBounds(expanded)
            : expanded;
    }

    private RectInt32 GetCompactBounds(RectInt32 expandedOrCurrent)
    {
        double scale = Win32Helper.GetDpiScaleForWindow(HWnd, RootElement.XamlRoot);
        RectInt32 resolved = WidgetCompactBoundsCalculator.Resolve(
            Config,
            expandedOrCurrent,
            scale,
            SettingsService.Settings.WidgetCollapsedStyle);
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
        if (!persist)
        {
            return;
        }

        SettingsService.UpdateWidget(Config, notifySubscribers: false);
        SettingsService.SaveDebounced(notifySubscribers: false);
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
        if (!_isCollapseAnimationRendering && !_isShellTransitionActive)
        {
            return;
        }

        _isCollapseAnimationRendering = false;
        CompositionTarget.Rendering -= CollapseAnimationRendering;
        if (_isShellTransitionActive)
        {
            WidgetShellControl.CancelCompactTransition();
            _isShellTransitionActive = false;
        }
    }

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
            bool pointerInside = IsPointerPhysicallyInsideWindow();
            _isPointerOverWidget = pointerInside;
            if (pointerInside)
            {
                _compactState = WidgetCompactState.ExpandedTransient;
                ScheduleSmartCollapse(SmartCollapseProbeMs);
                return;
            }

            if (!UsesSmartCollapseBehavior() ||
                _isSmartPinnedOpen ||
                _isCompactDragInside ||
                _isBoundsInteractionActive ||
                _compactInteractionDepth > 0 ||
                IsDragging ||
                IsResizing ||
                HasBlockingFlyoutOpen())
            {
                _compactState = WidgetCompactState.Interacting;
                return;
            }

            SetCollapsedState(true, persistManualState: false, animate: true);
        });
    }

    private int ResolveCompactTransitionDuration(int? requestedDurationMs)
    {
        string effect = SettingsService.NormalizeWidgetCompactAnimationEffect(
            SettingsService.Settings.WidgetCompactAnimationEffect);
        if (effect == SettingsService.WidgetCompactAnimationNone)
        {
            return 0;
        }

        int duration = requestedDurationMs ?? SettingsService.NormalizeWidgetCompactAnimationDurationMs(
            SettingsService.Settings.WidgetCompactAnimationDurationMs);
        return effect switch
        {
            SettingsService.WidgetCompactAnimationSnappy => Math.Max(90, (int)Math.Round(duration * 0.72)),
            SettingsService.WidgetCompactAnimationSlow => Math.Max(360, (int)Math.Round(duration * 1.75)),
            _ => duration
        };
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
}
