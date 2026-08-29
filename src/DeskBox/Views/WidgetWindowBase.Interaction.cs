// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
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
        TopMostSafetyTimer.Start();
    }

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
            if (WidgetTemporaryRaiseLeasePolicy.ShouldDeferSafetyRestore(
                    IsDragging,
                    IsResizing,
                    HasBlockingFlyoutOpen(),
                    App.Current.WidgetManager is { IsWidgetInteractionActive: true }))
            {
                App.LogVerbose($"[ZOrder] {LogPrefix} safety timer: defer restore hwnd=0x{HWnd.ToInt64():X}");
                sender.Start();
                return;
            }

            App.Log($"[ZOrder] {LogPrefix} safety timer: force restore hwnd=0x{HWnd.ToInt64():X}");
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

        return foregroundWindow == HWnd ||
               Win32Helper.GetAncestor(foregroundWindow, Win32Helper.GA_ROOTOWNER) == HWnd;
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
            if (force || RestoreDesktopLayerWhenIdle)
            {
                RestoreDesktopLayerWhenIdle = true;
            }

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
        IsAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} ClearTopMostOnly hwnd=0x{HWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
        App.Current.WidgetManager?.QueueIdleWidgetZOrderNormalization(
            $"{LogPrefix}-desktop-layer-restored");
    }

    // ── Drag logic ─────────────────────────────────────────────

    protected void BeginWindowDragCore(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        CancelPendingTitleBarDragFrame();
        BeginWidgetBoundsInteraction();
        IsDragging = true;
        _deferTitleBarDragConfigUpdates = true;
        SimplifyBackdropForInteraction();
        HasMovedTitleBarDrag = false;
        DisplayChangeWatcher?.SuppressRestore();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
        _isCoordinatedMoveDrag =
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            App.Current?.WidgetManager?.TryBeginCoordinatedMove(HWnd) == true;
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
            App.Current?.ResizeGuideOverlay.BeginDrag(HWnd, RootElement);
        }
    }

    protected void ContinueWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;
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
        _pendingTitleBarDragFrame = new PendingTitleBarDragFrame(
            new RectInt32(
                InitialWindowPos.X + deltaX,
                InitialWindowPos.Y + deltaY,
                InitialWindowSize.Width,
                InitialWindowSize.Height),
            deltaX,
            deltaY);
        _titleBarDragFrameRegistration ??=
            WidgetCompactAnimationCoordinator.Register(ApplyPendingTitleBarDragFrame);
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

        RectInt32 snappedBounds =
            App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(
                frame.ProposedBounds)
            ?? frame.ProposedBounds;
        ApplyWindowBounds(
            snappedBounds.X,
            snappedBounds.Y,
            snappedBounds.Width,
            snappedBounds.Height,
            persist: false,
            updateConfig: false);
        if (!IsCompactBoundsStateActive)
        {
            App.Current?.WidgetManager?.UpdateWidgetGroupDragPreview(Config.Id);
        }
    }

    private void FlushPendingTitleBarDragFrame()
    {
        ApplyPendingTitleBarDragFrame();
        CancelPendingTitleBarDragFrame();
    }

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

        App.Current?.ResizeGuideOverlay.EndDrag();

        if (_isCoordinatedMoveDrag &&
            App.Current?.WidgetManager?.CompleteCoordinatedMove(HWnd, hasMoved) == true)
        {
            _isCoordinatedMoveDrag = false;
            App.Current.WidgetManager.CancelWidgetGroupDrag(Config.Id);
            HasMovedTitleBarDrag = false;
            App.Current.WidgetManager.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                "coordinated-drag-ended");
            e.Handled = true;
            return;
        }

        _isCoordinatedMoveDrag = false;

        CompleteCompactArrangementDrag();
        RectInt32 finalBounds = GetActualWindowBounds();
        finalBounds = CompleteExpandedWidgetDrag(finalBounds);
        CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
        UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
        EndWidgetBoundsInteraction();
        OnDragEnd(hasMoved);
        if (hasMoved && !IsCompactBoundsStateActive)
        {
            _ = App.Current?.WidgetManager?.CompleteWidgetGroupDragAsync(Config.Id);
        }
        else
        {
            App.Current?.WidgetManager?.CancelWidgetGroupDrag(Config.Id);
        }

        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        RestoreBackdropAfterInteraction();
        QueueBackdropRefresh();
        App.Current?.WidgetManager?.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
            "drag-ended");
        e.Handled = true;
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

        BeginWidgetBoundsInteraction();
        IsResizing = true;
        // Per-commit config sync from AppWindow.Changed would fight the
        // frame-coordinator cadence; the final bounds are persisted once in
        // ResizeBorder_PointerReleasedCore instead.
        _deferInteractiveResizeConfigUpdates = true;
        ResizeDirection = direction;
        DisplayChangeWatcher?.SuppressRestore();
        OnResizeStart();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
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

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;

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
            QueueInteractiveResizeBounds(compactSnapped);
            e.Handled = true;
            return;
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
        snapped = AnchorExpandedResizeBounds(snapped);
        QueueInteractiveResizeBounds(snapped);
        e.Handled = true;
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
        App.Current?.ResizeGuideOverlay.EndDrag();

        if (_isCoordinatedMoveDrag &&
            App.Current?.WidgetManager?.CompleteCoordinatedMove(HWnd, hasMoved) == true)
        {
            _isCoordinatedMoveDrag = false;
            App.Current.WidgetManager.CancelWidgetGroupDrag(Config.Id);
            HasMovedTitleBarDrag = false;
            App.Current.WidgetManager.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                "coordinated-drag-capture-lost");
            e.Handled = true;
            return;
        }

        _isCoordinatedMoveDrag = false;
        CompleteCompactArrangementDrag();
        RectInt32 finalBounds = GetActualWindowBounds();
        finalBounds = CompleteExpandedWidgetDrag(finalBounds);
        CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
        UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
        EndWidgetBoundsInteraction();
        OnDragEnd(hasMoved);
        if (hasMoved && !IsCompactBoundsStateActive)
        {
            _ = App.Current?.WidgetManager?.CompleteWidgetGroupDragAsync(Config.Id);
        }
        else
        {
            App.Current?.WidgetManager?.CancelWidgetGroupDrag(Config.Id);
        }
        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        RestoreBackdropAfterInteraction();
        QueueBackdropRefresh();
        App.Current?.WidgetManager?.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
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
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    // ── Tray animation helpers ─────────────────────────────────

    protected WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return TrayAnimation.CreateProfile(WidgetAnimationSettings.From(SettingsService.Settings));
    }

    protected void LogTrayWindow(string message)
    {
        App.LogVerbose(Diagnostics.FormatTrayWindowMessage(message));
    }

    // ── Cleanup ────────────────────────────────────────────────
}
