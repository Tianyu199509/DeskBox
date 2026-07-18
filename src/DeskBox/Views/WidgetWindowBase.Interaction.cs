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

    protected void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = true;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost skipped pinned hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        IsAtDesktopLayer = false;
        KeepRaisedUntilDeactivate = true;
        RestoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost hwnd=0x{HWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    protected void StartTopMostSafetyTimer()
    {
        if (!Win32Helper.IsWindowTopMost(HWnd))
        {
            TopMostSafetyTimer?.Stop();
            return;
        }

        if (TopMostSafetyTimer is null)
        {
            TopMostSafetyTimer = DispatcherQueue.CreateTimer();
            TopMostSafetyTimer.IsRepeating = false;
            TopMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            TopMostSafetyTimer.Tick += (_, _) =>
            {
                TopMostSafetyTimer?.Stop();
                if (!IsAtDesktopLayer &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    if (ShouldDeferDesktopLayerRestore())
                    {
                        App.LogVerbose($"[ZOrder] {LogPrefix} safety timer: defer restore hwnd=0x{HWnd.ToInt64():X}");
                        TopMostSafetyTimer?.Start();
                        return;
                    }

                    App.Log($"[ZOrder] {LogPrefix} safety timer: force restore hwnd=0x{HWnd.ToInt64():X}");
                    RestoreDesktopLayer(force: true);
                }
            };
        }
        else
        {
            TopMostSafetyTimer.Stop();
        }
        TopMostSafetyTimer.Start();
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

        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    protected void ClearTopMostOnly()
    {
        IsAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} ClearTopMostOnly hwnd=0x{HWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    // ── Drag logic ─────────────────────────────────────────────

    protected void BeginWindowDragCore(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        BeginWidgetBoundsInteraction();
        IsDragging = true;
        HasMovedTitleBarDrag = false;
        DisplayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
        bool movesCapsuleBar = BeginCompactArrangementDrag();
        DragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        if (!movesCapsuleBar)
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

        int newX = InitialWindowPos.X + deltaX;
        int newY = InitialWindowPos.Y + deltaY;

        var proposedBounds = new RectInt32(newX, newY, InitialWindowSize.Width, InitialWindowSize.Height);
        if (!TryMoveCompactArrangement(proposedBounds, out _))
        {
            var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
                ?? proposedBounds;
            ApplyWindowBounds(
                snappedBounds.X,
                snappedBounds.Y,
                snappedBounds.Width,
                snappedBounds.Height,
                persist: false);
        }
        e.Handled = true;
    }

    protected void EndWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement?.ReleasePointerCapture(e.Pointer);
        DragCaptureElement = null;

        App.Current?.ResizeGuideOverlay.EndDrag();

        CompleteCompactArrangementDrag();
        RectInt32 finalBounds = GetActualWindowBounds();
        finalBounds = CompleteExpandedWidgetDrag(finalBounds);
        CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
        UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
        EndWidgetBoundsInteraction();
        OnDragEnd(hasMoved);

        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
        e.Handled = true;
    }

    // ── Resize logic ───────────────────────────────────────────

    protected void ResizeBorder_PointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (IsSizeLocked || sender is not FrameworkElement element)
        {
            return;
        }

        string direction = element.Tag as string ?? string.Empty;
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
        ResizeDirection = direction;
        DisplayChangeWatcher?.SuppressRestore();
        OnResizeStart();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        RectInt32 initialBounds = GetActualWindowBounds();
        InitialWindowPos = new PointInt32(initialBounds.X, initialBounds.Y);
        InitialWindowSize = new SizeInt32(initialBounds.Width, initialBounds.Height);
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
            ApplyWindowBounds(
                compactSnapped.X,
                compactSnapped.Y,
                compactSnapped.Width,
                compactSnapped.Height,
                persist: false);
            e.Handled = true;
            return;
        }

        var minSize = GetPhysicalMinimumWindowSize(
            InitialWindowPos.X,
            InitialWindowPos.Y,
            InitialWindowSize.Width,
            InitialWindowSize.Height);

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
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing || sender is not FrameworkElement element)
        {
            return;
        }

        IsResizing = false;
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        PersistCompletedWidgetResize(GetActualWindowBounds());
        ResizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        OnResizeEnd();
        DisplayChangeWatcher?.ResumeRestore();
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

        IsResizing = false;
        DragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndResize();
        PersistCompletedWidgetResize(GetActualWindowBounds());
        ResizeDirection = string.Empty;
        EndWidgetBoundsInteraction();
        OnResizeEnd();
        DisplayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    protected InputSystemCursorShape GetResizeCursorShapeForCurrentState(string? direction)
    {
        if (IsSizeLocked || !CanResizeCurrentWidgetState(direction))
        {
            return InputSystemCursorShape.Arrow;
        }

        return direction switch
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

        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement = null;
        App.Current?.ResizeGuideOverlay.EndDrag();
        CompleteCompactArrangementDrag();
        RectInt32 finalBounds = GetActualWindowBounds();
        finalBounds = CompleteExpandedWidgetDrag(finalBounds);
        CapturePositionAnchor(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height);
        UpdateConfigBoundsFromPhysical(finalBounds.X, finalBounds.Y, finalBounds.Width, finalBounds.Height, persist: true);
        EndWidgetBoundsInteraction();
        OnDragEnd(hasMoved);
        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
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
