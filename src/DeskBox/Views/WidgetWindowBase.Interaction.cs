// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    protected void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        // While an expanded capsule lease is held, the expand flow already
        // owns this window's layer (band top below app windows, or band
        // bottom in pinned mode until collapse). The TOPMOST interaction
        // pulse would make it jump above application windows — and in
        // pinned mode would bury it — for the pulse duration.
        if (_expandedWidgetLayerLeaseGeneration != 0)
        {
            LastElevateForInteractionUtc = DateTime.UtcNow;
            OnElevated();
            return;
        }

        LastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        OnElevated();
    }

    protected void HoldTemporaryTopMost(bool showWindow = true)
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = true;
            IsRaisedFromManager = false;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow);
            App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost skipped pinned hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        IsAtDesktopLayer = false;
        KeepRaisedUntilDeactivate = true;
        RestoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(HWnd, showWindow);
        App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost hwnd=0x{HWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    public void RaiseTemporarilyFromManager()
    {
        if (!Visible || IsClosing)
        {
            return;
        }

        LastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        IsRaisedFromManager = !IsAtDesktopLayer;
    }

    internal void AdoptManagerRaisedStateAfterPreparedShow()
    {
        if (!Visible || IsClosing)
        {
            return;
        }

        // ShowPreparedRaisedFromTray already settled the native Z-order while
        // the HWND was cloaked. Preserve the manager-owned raised lifetime
        // without issuing a second visible Z-order transaction.
        LastElevateForInteractionUtc = DateTime.UtcNow;
        IsRaisedFromManager = !IsAtDesktopLayer;
    }

    protected void StartTopMostSafetyTimer()
    {
        // Temporary raises intentionally pulse TOPMOST and immediately return
        // to the normal band. Logical desktop-layer state, not WS_EX_TOPMOST,
        // determines whether a safety restoration is still required.
        if (!WidgetTemporaryRaiseLeasePolicy.CanRestoreDesktopLayer(
                Visible,
                IsHideAnimationRunning,
                IsClosing) ||
            !WidgetTemporaryRaiseLeasePolicy.ShouldArmSafetyRestore(
                IsAtDesktopLayer))
        {
            TopMostSafetyTimer?.Stop();
            return;
        }

        if (TopMostSafetyTimer is null)
        {
            TopMostSafetyTimer = DispatcherQueue.CreateTimer();
            TopMostSafetyTimer.IsRepeating = false;
            TopMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            TopMostSafetyTimer.Tick += TopMostSafetyTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            TopMostSafetyTimer.Stop();
        }
        _safetyRestoreLeaseDeferrals = 0;
        TopMostSafetyTimer.Start();
    }

    private static int _safetyForceRestoreLoggedThisSession;

    // Lease deferrals are bounded (unlike the four routine defer predicates,
    // which re-arm indefinitely by design): if the lease's owner gave up, the
    // net must still reach the window within a few intervals.
    private const int MaxSafetyRestoreLeaseDeferrals = 3;
    private int _safetyRestoreLeaseDeferrals;

    private void TopMostSafetyTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!WidgetTemporaryRaiseLeasePolicy.CanRestoreDesktopLayer(
                Visible,
                IsHideAnimationRunning,
                IsClosing))
        {
            CancelPendingDesktopLayerRestore();
            return;
        }

        if (!IsAtDesktopLayer &&
            App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            // The manager's temporary-raise lease owns this window's descent
            // (settle ~2.3s plus busy retries) and cancels this timer when it
            // lands; deferring while it is held stops the net from stealing
            // the restore 300ms early on every startup raise.
            bool routineDefer = WidgetTemporaryRaiseLeasePolicy.ShouldDeferSafetyRestore(
                IsDragging,
                IsResizing,
                HasBlockingFlyoutOpen(),
                App.Current.WidgetManager is { IsWidgetInteractionActive: true });
            bool leaseDefer = IsRaisedFromManager &&
                _safetyRestoreLeaseDeferrals < MaxSafetyRestoreLeaseDeferrals;
            if (routineDefer || leaseDefer)
            {
                if (leaseDefer && !routineDefer)
                {
                    _safetyRestoreLeaseDeferrals++;
                }

                App.LogVerbose(
                    $"[ZOrder] {LogPrefix} safety timer: defer restore hwnd=0x{HWnd.ToInt64():X} " +
                    $"routine={routineDefer} lease={leaseDefer}");
                sender.Start();
                return;
            }

            // The restore itself is routine — the normal descent path
            // missed and this net is the designed backstop. Only the
            // first hit per session stays at the default level so a
            // regression still surfaces in field logs.
            string forceRestoreLine =
                $"[ZOrder] {LogPrefix} safety timer: force restore hwnd=0x{HWnd.ToInt64():X}";
            if (System.Threading.Interlocked.Exchange(ref _safetyForceRestoreLoggedThisSession, 1) == 0)
            {
                App.Log(forceRestoreLine);
            }
            else
            {
                App.LogVerbose(forceRestoreLine);
            }

            RestoreDesktopLayer(force: true);
        }
    }

    protected void ReleaseTopMostSafetyTimer()
    {
        DispatcherQueueTimer? timer = TopMostSafetyTimer;
        if (timer is null)
        {
            return;
        }

        TopMostSafetyTimer = null;
        timer.Stop();
        timer.Tick -= TopMostSafetyTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    protected virtual bool OwnsTransientForegroundWindow(IntPtr foregroundWindow) =>
        HWnd != IntPtr.Zero &&
        WidgetEditorPopoverWindow.IsPopoverOwnedBy(foregroundWindow, HWnd);

    protected bool ShouldDeferDesktopLayerRestore()
    {
        if (IsDragging ||
            IsResizing ||
            HasBlockingFlyoutOpen() ||
            App.Current.WidgetManager is { IsWidgetInteractionActive: true })
        {
            return true;
        }

        IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        return WidgetLayerService.IsWindowOrOwnedByWidget(foregroundWindow, HWnd) ||
               OwnsTransientForegroundWindow(foregroundWindow);
    }

    protected void RestoreDesktopLayer(bool force = false)
    {
        if (!WidgetTemporaryRaiseLeasePolicy.CanRestoreDesktopLayer(
                Visible,
                IsHideAnimationRunning,
                IsClosing))
        {
            CancelPendingDesktopLayerRestore();
            return;
        }

        if (!force && !RestoreDesktopLayerWhenIdle && KeepRaisedUntilDeactivate)
        {
            return;
        }

        if (!force && (IsDragging || IsResizing || HasBlockingFlyoutOpen()))
        {
            // Deep review R17 (G1 audit): the inner `force ||` was dead
            // (always false here) — simplified to the plain idempotent set.
            RestoreDesktopLayerWhenIdle = true;
            return;
        }

        CancelPendingDesktopLayerRestore();
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    protected void CancelPendingDesktopLayerRestore()
    {
        TopMostSafetyTimer?.Stop();
        IsRaisedFromManager = false;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
    }

    protected void ClearTopMostOnly()
    {
        IsRaisedFromManager = false;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(
            HWnd,
            out RelativeLayerRestoreDisposition disposition);
        // PreservePeerOrder deliberately leaves the window at its current
        // level: raised before the restore, still raised after it. Marking it
        // bedded here would blind the Deactivated funnel that owns its later
        // descent (the flyout-Esc lingering window). The placed branches
        // (DesktopBottom / BehindForeground) end on a resting window.
        if (disposition != RelativeLayerRestoreDisposition.PreservePeerOrder)
        {
            IsAtDesktopLayer = true;
        }
        else
        {
            App.Current.WidgetManager?.QueueIdleWidgetZOrderNormalization(
                $"{LogPrefix}-desktop-layer-restored");
        }

        App.LogVerbose($"[ZOrder] {LogPrefix} ClearTopMostOnly hwnd=0x{HWnd.ToInt64():X} fg=0x{foreground.ToInt64():X} disposition={disposition}");
    }

    // ── Drag logic ─────────────────────────────────────────────

    protected virtual void OnWidgetBoundsInteractionStarting()
    {
    }

    protected void BeginWindowDragCore(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        OnWidgetBoundsInteractionStarting();
        CancelPendingTitleBarDragFrame();
        _titleBarDragFrameMetrics = new BoundsInteractionFrameMetrics();
        BeginWidgetBoundsInteraction();
        IsDragging = true;
        _deferTitleBarDragConfigUpdates = true;
        SimplifyBackdropForInteraction();
        HasMovedTitleBarDrag = false;
        DisplayChangeWatcher?.SuppressRestore();
        if (!Win32Helper.TryGetPointerScreenPoint(HWnd, RootElement, e, out InitialPointerPt))
        {
            Win32Helper.GetCursorPos(out InitialPointerPt);
        }
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
        double initScale = Win32Helper.GetDpiScaleForPoint(InitialPointerPt.X, InitialPointerPt.Y);
        _initialDragDpiScale = double.IsFinite(initScale) && initScale > 0 ? initScale : 1.0;
        WidgetShellControl.SetDragHandleToolTipSuppressed(true);
        App? app = App.Current;
        WidgetManager? manager = app?.WidgetManager;
        _isCoordinatedMoveDrag =
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            manager?.TryBeginCoordinatedMove(HWnd) == true;
        if (!_isCoordinatedMoveDrag)
        {
            ElevateForInteraction();
        }

        bool movesCapsuleBar = !_isCoordinatedMoveDrag && BeginCompactArrangementDrag();
        DragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        if (!movesCapsuleBar && !_isCoordinatedMoveDrag)
        {
            app?.ResizeGuideOverlay.BeginDrag(HWnd, RootElement);
            if (!IsCompactBoundsStateActive)
            {
                double anchorNormX = initialBounds.Width > 0
                    ? (double)(InitialPointerPt.X - initialBounds.X) / initialBounds.Width
                    : 0.5;
                double anchorNormY = initialBounds.Height > 0
                    ? (double)(InitialPointerPt.Y - initialBounds.Y) / initialBounds.Height
                    : 0.06;
                manager?.BeginWidgetGroupDragSession(Config.Id, anchorNormX, anchorNormY);
            }
        }
    }

    protected void ContinueWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        if (!e.GetCurrentPoint(RootElement).Properties.IsLeftButtonPressed &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.LeftButton))
        {
            EndWindowDragCore(e);
            return;
        }

        if (!Win32Helper.TryGetPointerScreenPoint(HWnd, RootElement, e, out var currentPt) &&
            !Win32Helper.GetCursorPos(out currentPt))
        {
            e.Handled = true;
            return;
        }
        int deltaX = currentPt.X - InitialPointerPt.X;
        int deltaY = currentPt.Y - InitialPointerPt.Y;
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (!HasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            HasMovedTitleBarDrag = true;
            WidgetShellControl.NotifyCompactDragMoved();
        }

        QueueTitleBarDragFrame(deltaX, deltaY);
        e.Handled = true;
    }

    private void QueueTitleBarDragFrame(int deltaX, int deltaY)
    {
        _titleBarDragFrameMetrics?.RecordPointerSample();
        _pendingTitleBarDragFrame = new PendingTitleBarDragFrame(
            new RectInt32(
                InitialWindowPos.X + deltaX,
                InitialWindowPos.Y + deltaY,
                InitialWindowSize.Width,
                InitialWindowSize.Height),
            deltaX,
            deltaY);
        _titleBarDragFrameRegistration ??=
            WidgetCompactAnimationCoordinator.Register(ApplyPendingTitleBarDragFrame, HWnd, paceToDisplay: true);
    }

    private void ApplyPendingTitleBarDragFrame()
    {
        if (!IsDragging || _pendingTitleBarDragFrame is not { } frame)
        {
            return;
        }

        // PointerMoved may arrive much faster than DWM can present frames,
        // especially on Win10 with several Acrylic HWNDs. Consume only the
        // latest pointer sample for this frame so snap/group work and native
        // window commits never scale with raw mouse-report frequency.
        _pendingTitleBarDragFrame = null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ApplyTitleBarDragFrame(frame);
        }
        finally
        {
            _titleBarDragFrameMetrics?.RecordUpdate(
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                WidgetCompactAnimationCoordinator.GetFrameBudgetMilliseconds(HWnd));
        }
    }

    private void ApplyTitleBarDragFrame(PendingTitleBarDragFrame frame)
    {
        if (_isCoordinatedMoveDrag)
        {
            App.Current?.WidgetManager?.UpdateCoordinatedMove(
                HWnd,
                frame.DeltaX,
                frame.DeltaY);
            return;
        }

        if (TryMoveCompactArrangement(frame.ProposedBounds, out _))
        {
            return;
        }

        int currentPointerX = InitialPointerPt.X + frame.DeltaX;
        int currentPointerY = InitialPointerPt.Y + frame.DeltaY;
        double targetScale = Win32Helper.GetDpiScaleForPoint(currentPointerX, currentPointerY);
        RectInt32 crossDpiBounds = WidgetPositioningService.CalculateCrossDpiDragBounds(
            InitialWindowPos,
            InitialWindowSize,
            new PointInt32(InitialPointerPt.X, InitialPointerPt.Y),
            _initialDragDpiScale,
            new PointInt32(currentPointerX, currentPointerY),
            targetScale);

        // Resolve the merge candidate first: while the pointer rests on a
        // joinable target, edge snapping is suspended so the two intents
        // (align beside / stack onto) never fight over the window position.
        bool groupTargetEngaged =
            !IsCompactBoundsStateActive &&
            App.Current?.WidgetManager?.UpdateWidgetGroupDragPreview(Config.Id) == true;
        RectInt32 snappedBounds =
            App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(
                crossDpiBounds,
                suspendSnap: groupTargetEngaged)
            ?? crossDpiBounds;
        ApplyWindowBounds(
            snappedBounds.X,
            snappedBounds.Y,
            snappedBounds.Width,
            snappedBounds.Height,
            persist: false,
            updateConfig: false);
    }

    private void FlushPendingTitleBarDragFrame()
    {
        if (CallbackDepth.Depth == 0)
        {
            ApplyPendingTitleBarDragFrame();
        }
        CancelPendingTitleBarDragFrame();
        CompleteBoundsInteractionFrameMetrics(ref _titleBarDragFrameMetrics, "drag");
    }

    /// <summary>
    /// Physical bounds the window had when the current title drag began;
    /// a drag-merge undo returns the widget here.
    /// </summary>
    private RectInt32 GetTitleBarDragOriginBounds() =>
        new(
            InitialWindowPos.X,
            InitialWindowPos.Y,
            InitialWindowSize.Width,
            InitialWindowSize.Height);

    private void CancelPendingTitleBarDragFrame()
    {
        _pendingTitleBarDragFrame = null;
        _titleBarDragFrameRegistration?.Dispose();
        _titleBarDragFrameRegistration = null;
    }

    protected void EndWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        FlushPendingTitleBarDragFrame();
        _deferTitleBarDragConfigUpdates = false;
        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement?.ReleasePointerCapture(e.Pointer);
        DragCaptureElement = null;

        FinishTitleBarDragCore(
            hasMoved,
            "coordinated-drag-ended",
            "drag-ended");
        e.Handled = true;
    }

    private void FinishTitleBarDragCore(
        bool hasMoved,
        string coordinatedRestoreReason,
        string normalRestoreReason)
    {
        WidgetShellControl.SetDragHandleToolTipSuppressed(false);
        App? app = App.Current;
        app?.ResizeGuideOverlay.EndDrag();
        WidgetManager? manager = app?.WidgetManager;

        if (_isCoordinatedMoveDrag &&
            manager?.CompleteCoordinatedMove(HWnd, hasMoved) == true)
        {
            _isCoordinatedMoveDrag = false;
            manager.CancelWidgetGroupDrag(Config.Id);
            HasMovedTitleBarDrag = false;
            manager.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                coordinatedRestoreReason);
            return;
        }

        _isCoordinatedMoveDrag = false;

        // If the release lands on an armed merge target, cloak the dragged
        // source immediately and skip the redundant drag placement commit and
        // 8-window Z-order bounce; fallback runs only if the merge is rejected.
        if (hasMoved &&
            !IsCompactBoundsStateActive &&
            manager?.IsWidgetGroupDragMergeReady(Config.Id) == true)
        {
            RectInt32 originBounds = GetTitleBarDragOriginBounds();
            EndWidgetBoundsInteraction();
            DisplayChangeWatcher?.ResumeRestore();
            RestoreBackdropAfterInteraction();
            HasMovedTitleBarDrag = false;
            _ = manager.CompleteWidgetGroupDragAsync(
                Config.Id,
                originBounds,
                onNotMergedFallback: () =>
                {
                    if (!IsClosing)
                    {
                        CompleteNonMergedDragPlacementAndRestore(
                            hasMoved: true,
                            normalRestoreReason);
                    }
                });
            return;
        }

        manager?.CancelWidgetGroupDrag(Config.Id);
        EndWidgetBoundsInteraction();
        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        CompleteNonMergedDragPlacementAndRestore(hasMoved, normalRestoreReason);
    }

    private void CompleteNonMergedDragPlacementAndRestore(
        bool hasMoved,
        string layerRestoreReason)
    {
        if (hasMoved)
        {
            CompleteCompactArrangementDrag();
            RectInt32 finalBounds = GetActualWindowBounds();
            finalBounds = CompleteExpandedWidgetDrag(finalBounds);
            CommitUserPlacementFromWindow(WidgetPlacementSource.Drag);
            if (_targetCollapsed)
            {
                // The pointer is still resting on the just-dropped capsule; an
                // immediate Smart hover-expand would look like the placement
                // snapped back on its own.
                _suppressHoverExpansionAfterDragUntilPointerExit = true;
            }
        }

        OnDragEnd(hasMoved);
        RestoreBackdropAfterInteraction();
        QueueBackdropRefresh();
        App.Current?.WidgetManager?.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
            layerRestoreReason);
    }

    // ── Resize logic ───────────────────────────────────────────

    protected void ResizeBorder_PointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (IsSizeLocked || IsResizing || sender is not FrameworkElement element)
        {
            return;
        }

        string direction = WidgetCompactInteractionPolicy.ResolveResizeDirection(
            IsCompactBoundsStateActive,
            element.Tag as string);
        if (!CanResizeCurrentWidgetState(direction))
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        OnWidgetBoundsInteractionStarting();
        BeginWidgetBoundsInteraction();
        IsResizing = true;
        // Per-commit config sync from AppWindow.Changed would fight the
        // frame-coordinator cadence; the final bounds are persisted once in
        // ResizeBorder_PointerReleasedCore instead.
        _deferInteractiveResizeConfigUpdates = true;
        ResizeDirection = direction;
        DisplayChangeWatcher?.SuppressRestore();
        OnResizeStart();
        if (!Win32Helper.TryGetPointerScreenPoint(HWnd, RootElement, e, out InitialPointerPt))
        {
            Win32Helper.GetCursorPos(out InitialPointerPt);
        }
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
        double initScale = Win32Helper.GetDpiScaleForPoint(InitialPointerPt.X, InitialPointerPt.Y);
        _initialDragDpiScale = double.IsFinite(initScale) && initScale > 0 ? initScale : 1.0;
        _interactiveResizeMinimumSize = GetPhysicalMinimumWindowSize(
            initialBounds.X,
            initialBounds.Y,
            initialBounds.Width,
            initialBounds.Height);
        BeginInteractiveResizePerformanceSession();
        SimplifyBackdropForInteraction();
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(HWnd, RootElement);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerMovedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing)
        {
            return;
        }

        if (Win32Helper.TryGetPointerScreenPoint(HWnd, RootElement, e, out var currentPt) ||
            Win32Helper.GetCursorPos(out currentPt))
        {
            QueueInteractiveResizePointer(new PointInt32(currentPt.X, currentPt.Y));
        }
        e.Handled = true;
    }

    private RectInt32 ResolveInteractiveResizeBounds(PointInt32 currentPt)
    {
        int deltaX = currentPt.X - InitialPointerPt.X;
        int deltaY = currentPt.Y - InitialPointerPt.Y;

        int newWidth = InitialWindowSize.Width;
        int newHeight = InitialWindowSize.Height;
        int newX = InitialWindowPos.X;
        int newY = InitialWindowPos.Y;

        if (IsCompactBoundsStateActive)
        {
            var limits = GetCompactPhysicalWidthLimits();
            if (ResizeDirection == "Right")
            {
                newWidth = Math.Clamp(InitialWindowSize.Width + deltaX, limits.MinWidth, limits.MaxWidth);
            }
            else if (ResizeDirection == "Left")
            {
                int rightEdge = InitialWindowPos.X + InitialWindowSize.Width;
                newWidth = Math.Clamp(InitialWindowSize.Width - deltaX, limits.MinWidth, limits.MaxWidth);
                newX = rightEdge - newWidth;
            }

            var compactProposed = new RectInt32(newX, newY, newWidth, newHeight);
            var compactSnapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(
                compactProposed,
                ResizeDirection,
                limits.MinWidth,
                limits.MaxWidth);
            return compactSnapped;
        }

        SizeInt32 minSize = _interactiveResizeMinimumSize;

        if (ResizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width + deltaX);
        }
        else if (ResizeDirection.Contains("Left"))
        {
            int rightEdge = InitialWindowPos.X + InitialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (ResizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height + deltaY);
        }
        else if (ResizeDirection.Contains("Top"))
        {
            int bottomEdge = InitialWindowPos.Y + InitialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, ResizeDirection);
        return AnchorExpandedResizeBounds(snapped);
    }

    private void CommitInteractiveResizeBounds()
    {
        // Keep the resize guard active while the final coalesced frame is
        // committed. In particular, compact-bound settlement must not restore
        // the pre-drag capsule width before Win10 has exposed the final HWND
        // rectangle to GetWindowRect.
        FlushPendingInteractiveResizeBounds();
        RectInt32 finalBounds = GetActualWindowBounds();

        // Capture-lost can be raised synchronously when pointer capture is
        // released below. Mark the gesture complete first, but keep AppWindow
        // config synchronization deferred until the authoritative bounds have
        // been persisted and any capsule-bar constraint has been refreshed.
        IsResizing = false;
        try
        {
            PersistCompletedWidgetResize(finalBounds);
        }
        finally
        {
            _deferInteractiveResizeConfigUpdates = false;
        }
    }

    protected void ResizeBorder_PointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing || sender is not FrameworkElement element)
        {
            return;
        }

        CommitInteractiveResizeBounds();
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        ResizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        OnResizeEnd();
        EndInteractiveResizePerformanceSession();
        DisplayChangeWatcher?.ResumeRestore();
        RestoreBackdropAfterInteraction();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    /// <summary>
    /// Handles PointerCaptureLost for resize borders.  If the system steals
    /// pointer capture mid-resize (e.g., alt-tab, UAC, tablet mode), the
    /// PointerReleased event never fires and the resize guide highlights
    /// would be leaked forever.  This method ensures cleanup.
    /// </summary>
    protected void ResizeBorder_PointerCaptureLostCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing)
        {
            return;
        }

        CommitInteractiveResizeBounds();
        DragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        ResizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        OnResizeEnd();
        EndInteractiveResizePerformanceSession();
        DisplayChangeWatcher?.ResumeRestore();
        RestoreBackdropAfterInteraction();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    protected InputSystemCursorShape GetResizeCursorShapeForCurrentState(string? direction)
    {
        string resolvedDirection = WidgetCompactInteractionPolicy.ResolveResizeDirection(
            IsCompactBoundsStateActive,
            direction);
        if (IsSizeLocked || !CanResizeCurrentWidgetState(resolvedDirection))
        {
            return InputSystemCursorShape.Arrow;
        }

        return resolvedDirection switch
        {
            "Left" or "Right" => InputSystemCursorShape.SizeWestEast,
            "Top" or "Bottom" => InputSystemCursorShape.SizeNorthSouth,
            "TopLeft" or "BottomRight" => InputSystemCursorShape.SizeNorthwestSoutheast,
            "TopRight" or "BottomLeft" => InputSystemCursorShape.SizeNortheastSouthwest,
            _ => InputSystemCursorShape.Arrow
        };
    }

    /// <summary>
    /// Handles PointerCaptureLost for drag-move.  Same rationale as
    /// ResizeBorder_PointerCaptureLostCore — ensures EndDrag is called
    /// even when the system steals pointer capture mid-drag.
    /// </summary>
    protected void DragPointerCaptureLostCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        FlushPendingTitleBarDragFrame();
        _deferTitleBarDragConfigUpdates = false;
        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement = null;

        FinishTitleBarDragCore(
            hasMoved,
            "coordinated-drag-capture-lost",
            "drag-capture-lost");
        e.Handled = true;
    }

    // ── Interaction layer helpers ──────────────────────────────

    protected void BeginInteractionLayer(string reason, bool elevate = true)
    {
        BeginCompactInteraction();
        App.Current.WidgetManager?.BeginWidgetInteraction(reason);
        if (elevate)
        {
            ElevateForInteraction();
        }
    }

    protected void ReleaseInteractionLayer(string reason)
    {
        EndCompactInteraction();
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        // While the tray raised session is alive its restore monitor owns the
        // layer; outside a session this window restores itself.
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        RestoreDesktopLayer();
    }

    // ── Chained flyout handoff ───────────────────────────────

    /// <summary>
    /// Holds the compact and widget-manager interaction leases across a
    /// chained flyout transition, where one flyout closes and a successor
    /// opens on a later dispatcher turn (for example a context menu handing
    /// off to the foreground color picker). The lease intentionally does not
    /// elevate; the successor flyout's own show path owns Z-order.
    /// </summary>
    protected IDisposable AcquireFlyoutHandoff(string reason)
    {
        BeginCompactInteraction();
        WidgetManager? widgetManager = App.Current.WidgetManager;
        widgetManager?.BeginWidgetInteraction(reason);
        return new FlyoutInteractionHandoff(this, widgetManager, reason);
    }

    protected void QueueInteractionGuardedShow(
        IDisposable? interactionHandoff,
        Func<Task> showAsync)
    {
        if (DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // The show delegate acquires its own interaction (see the
                // window-level flyout show helpers) before this handoff is
                // released, so a grouped Smart capsule cannot collapse
                // between the two flyouts.
                await showAsync();
            }
            catch (Exception ex)
            {
                App.Log($"{LogPrefix} Queued flyout failed: {ex}");
            }
            finally
            {
                interactionHandoff?.Dispose();
            }
        }))
        {
            return;
        }

        interactionHandoff?.Dispose();
    }

    private sealed class FlyoutInteractionHandoff : IDisposable
    {
        private WidgetWindowBase? _owner;
        private WidgetManager? _widgetManager;
        private readonly string _reason;

        public FlyoutInteractionHandoff(
            WidgetWindowBase owner,
            WidgetManager? widgetManager,
            string reason)
        {
            _owner = owner;
            _widgetManager = widgetManager;
            _reason = reason;
        }

        public void Dispose()
        {
            WidgetWindowBase? owner = Interlocked.Exchange(ref _owner, null);
            WidgetManager? widgetManager = Interlocked.Exchange(
                ref _widgetManager,
                null);
            if (owner is null)
            {
                return;
            }

            owner.EndCompactInteraction();
            widgetManager?.EndWidgetInteraction($"{_reason}-completed");
        }
    }

    // ── Tray animation helpers ─────────────────────────────────

    private WidgetAnimationOptions? _trayAnimationOptionsOverride;

    protected WidgetAnimationOptions GetEffectiveTrayAnimationOptions() =>
        _trayAnimationOptionsOverride ?? WidgetAnimationSettings.From(SettingsService.Settings);

    protected void SetTrayAnimationOptionsOverrideCore(WidgetAnimationOptions? optionsOverride)
    {
        _trayAnimationOptionsOverride = optionsOverride;
    }

    protected WidgetTrayAnimationProfile GetTrayAnimationProfile(bool isShowing = true)
    {
        return TrayAnimation.CreateProfile(
            GetEffectiveTrayAnimationOptions(),
            isShowing,
            WindowsCompatibilityService.AreAnimationsEnabled);
    }

    protected WidgetTrayAnimationProfile GetSingleWidgetAnimationProfile(bool isShowing)
    {
        return TrayAnimation.CreateSingleWidgetLifecycleProfile(
            GetEffectiveTrayAnimationOptions(),
            isShowing,
            WindowsCompatibilityService.AreAnimationsEnabled);
    }

    protected void LogTrayWindow(string message)
    {
        App.LogVerbose(Diagnostics.FormatTrayWindowMessage(message));
    }

    // ── Cleanup ────────────────────────────────────────────────
}
