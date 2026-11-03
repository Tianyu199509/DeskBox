using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private static readonly TimeSpan GroupKeyboardSwitchCooldown =
        TimeSpan.FromMilliseconds(450);
    private bool _groupKeyboardTabGestureActive;
    private DateTimeOffset _lastGroupKeyboardSwitchAt;

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (CurrentContent is FileWidgetContentAdapter file)
        {
            // The transparent resize grid sits above the content along every
            // window edge. Reuse the file surface's normal feedback there so
            // the cursor does not briefly report a forbidden drop even though
            // the native window drop target can accept it.
            file.ApplyHostEdgeDragOverFeedback(e);
            return;
        }

        if (!IsCompactBoundsStateActive || CurrentContent is not TodoWidgetContentAdapter todo)
        {
            return;
        }

        e.AcceptedOperation = todo.CanImportExternalDrop(e.DataView)
            ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                ? DeskBoxDragData.GetFileAssociationOperation(
                    e.DataView,
                    e.AllowedOperations)
                : DataPackageOperation.Copy
            : DataPackageOperation.None;
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            bool isInternalFileDrag =
                DeskBoxDragData.IsInternalFileDrag(e.DataView);
            e.DragUIOverride.IsContentVisible = isInternalFileDrag;
            e.DragUIOverride.IsGlyphVisible = isInternalFileDrag;
            e.DragUIOverride.IsCaptionVisible = isInternalFileDrag;
            e.DragUIOverride.Caption = isInternalFileDrag
                ? App.Current.LocalizationService.T(
                    "Widget.Compact.TodoDropHint")
                : string.Empty;
        }
        else
        {
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption =
                e.AcceptedOperation == DataPackageOperation.None
                    ? string.Empty
                    : App.Current.LocalizationService.T(
                        "Widget.Compact.TodoDropHint");
        }
        e.Handled = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (CurrentContent is FileWidgetContentAdapter file)
        {
            file.HandleHostEdgeDrop(e);
            return;
        }

        if (!IsCompactBoundsStateActive || CurrentContent is not TodoWidgetContentAdapter todo)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            e.AcceptedOperation = await todo.ImportExternalDropAsync(e.DataView)
                ? DeskBoxDragData.HasDroppedFiles(e.DataView)
                    ? DeskBoxDragData.GetFileAssociationOperation(e.DataView)
                    : DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CurrentContent is FileWidgetContentAdapter fileSurface &&
            await fileSurface.TryHandleClipboardShortcutAsync(e))
        {
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Tab ||
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            return;
        }

        if (_groupKeyboardTabGestureActive ||
            DateTimeOffset.UtcNow - _lastGroupKeyboardSwitchAt <
            GroupKeyboardSwitchCooldown)
        {
            e.Handled = true;
            return;
        }

        if (ContentWidgetShell.TryHandleGroupKeyboardNavigation(e))
        {
            _groupKeyboardTabGestureActive = true;
            _lastGroupKeyboardSwitchAt = DateTimeOffset.UtcNow;
            e.Handled = true;
        }
    }

    private void RootGrid_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            _groupKeyboardTabGestureActive = false;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.OriginalSource is DependencyObject source && HasAncestorOfType<TextBox>(source))
        {
            return;
        }

        if (TryHandleCompactActivation(e))
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape && TryHandleCompactEscape())
        {
            e.Handled = true;
        }
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsGroupNavigationInput(e.OriginalSource))
        {
            return;
        }
        var properties = e.GetCurrentPoint(ContentWidgetShell.TitleBar).Properties;
        if (!properties.IsLeftButtonPressed) return;
        if (ContentWidgetShell.TitleEditorContent is TextBox &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitTitleRenameAsync();
            e.Handled = true;
            return;
        }

        if (ShouldOpenTitleBarFlyout(e.OriginalSource) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            // Raising the whole group (band ordering + foreground) is window
            // surgery and must not run on the pointer-pressed callback frame
            // (drag-freeze family). The deferral sits at this call site only:
            // the manager's raise sequence itself stays atomic so the reassert
            // path never splits across turns. The foreground snapshot guard
            // drops the raise if the user moved focus elsewhere during the
            // deferred turn; the drag guard skips it when the press turned
            // into a drag before the unit landed.
            IntPtr foregroundAtEnqueue = Win32Helper.GetForegroundWindow();
            IntPtr windowHandle = HWnd;
            UiDispatch.PostAfterCallback(() =>
            {
                // The drag guard gates on real movement: IsDragging is set
                // synchronously by BeginWindowDragCore later in THIS press
                // dispatch, so a bare check would drop the raise forever.
                if (!Win32Helper.IsWindowInCurrentProcess(windowHandle) ||
                    (IsDragging && HasMovedTitleBarDrag))
                {
                    return;
                }

                IntPtr foregroundNow = Win32Helper.GetForegroundWindow();
                if (foregroundNow != windowHandle &&
                    foregroundNow != foregroundAtEnqueue)
                {
                    App.LogVerbose(
                        "[ZOrder] Deferred title-bar group raise dropped: " +
                        "foreground moved on");
                    return;
                }

                if (WidgetLayerService.UsesDynamicGroupClickRaiseMode())
                {
                    App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(
                        windowHandle);
                }
                else if (!WidgetLayerService.UsesDesktopPinnedMode() &&
                         !WidgetLayerService.UsesQuickRevealMode())
                {
                    App.Current.WidgetManager?.RebedDynamicPassiveSiblingsAfterSingleActivation(
                        windowHandle,
                        "title-single-press");
                }
            });
        }
        if (_config.IsPositionLocked || IsCompactTransitionActive) return;
        BeginWindowDragCore(e, ContentWidgetShell.TitleBar);
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsGroupNavigationInput(source) &&
               !IsWithin(source, ContentWidgetShell.PositionLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.SizeLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.AddActionButton) &&
               !IsWithin(source, ContentWidgetShell.MoreActionButton) &&
               !IsWithin(source, ContentWidgetShell.CloseActionButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

    private bool IsGroupNavigationInput(object? source) =>
        source is DependencyObject element &&
        ContentWidgetShell.GroupNavigationElement is { } groupNav &&
        IsWithin(element, groupNav) &&
        (HasAncestorOfType<TabViewItem>(element) || HasAncestorOfType<ButtonBase>(element));

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAncestorOfType<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground != IntPtr.Zero && !App.Current.IsDeskBoxWindow(foreground))
        {
            App.Current.WidgetManager?.RestoreTemporarilyRaisedWidgetsToDesktopLayer(
                "content-title-released");
        }
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_config.IsPositionLocked) return;
        var properties = e.GetCurrentPoint(ContentWidgetShell.DragHandleElement).Properties;
        if (!properties.IsLeftButtonPressed) return;
        BeginWindowDragCore(e, ContentWidgetShell.DragHandleElement);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        DragPointerCaptureLostCore(sender, e);
    }

    private void DragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        DragPointerCaptureLostCore(sender, e);
    }

    protected override void OnDragEnd(bool hasMoved)
    {
        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    // ── Resize handlers (delegate to base) ─────────────────────

    private void ResizeBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerPressedCore(sender, e);
    }

    private void ResizeBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerMovedCore(sender, e);
    }

    private void ResizeBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerReleasedCore(sender, e);
    }

    private void ResizeBorder_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResizeBorder_PointerCaptureLostCore(sender, e);
    }

    protected override void OnResizeEnd()
    {
        if (CurrentContent is IWidgetInteractiveResizeContent resizeContent)
        {
            double titleHeight = Math.Max(0, ContentWidgetShell.ActualTitleBarHeight);
            resizeContent.CompleteInteractiveResize(
                Math.Max(1, RootGrid.ActualWidth),
                Math.Max(1, RootGrid.ActualHeight - titleHeight));
        }

        if (RestoreDesktopLayerWhenIdle)
        {
            RestoreDesktopLayer();
        }
    }

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        var shape = element is FrameworkElement frameworkElement
            ? GetResizeCursorShapeForCurrentState(frameworkElement.Tag as string)
            : InputSystemCursorShape.Arrow;

        UiElementCursorHelper.SetCursor(element, shape);
    }

    // ── Activation ─────────────────────────────────────────────

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _groupKeyboardTabGestureActive = false;
            _contentHost.OnDeactivated();
            if (Visible && !IsAtDesktopLayer &&
                !IsRaisedFromManager &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - LastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] Content Deactivated→QueueRestore hwnd=0x{HWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }
            return;
        }

        _contentHost.OnActivated();
        // A long-hidden release dropped the decoded custom background; the
        // surface is cheap to rebuild from disk on the way back in.
        RecoverCustomBackgroundIfReleased();
        // Native activation (a body click on a resting widget) lifts the
        // window without going through any raise path, so IsAtDesktopLayer
        // never flipped and the Deactivated funnel above stayed blind — the
        // window then floats above foreign apps with no owner for its
        // restore. Every explicit raise path has already flipped the flag
        // by now; the desktop-pinned band keeps its own activation funnels.
        bool activatedFromRestingDynamicLayer =
            IsAtDesktopLayer &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            !WidgetLayerService.UsesQuickRevealMode();
        if (IsAtDesktopLayer && !WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = false;
        }
        // Deliberately synchronous (callback-isolation exemption): deferring
        // this reassert one dispatcher turn turns its SetForegroundWindow
        // (active) from a no-op — the window is still the foreground when the
        // handler runs — into a REAL foreground switch against whichever
        // sibling the raise batch activated next. Each switch raises another
        // Activated event, whose deferred reassert switches again: a 3ms
        // activation ping-pong ring across the whole group (2026-10-15
        // incident, 1805 band-split transactions during one raise). The
        // manager's own guards (session flag, foreground ownership, the
        // 400ms reassert suppression) bound the synchronous form.
        App.Current.WidgetManager?.ReassertRaisedWidgetGroupAfterDeskBoxActivation(
            HWnd,
            "content-window-activated");
        if (activatedFromRestingDynamicLayer &&
            App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
        {
            DeferDynamicRestingClickRaiseScope();
        }
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible ||
                IsAtDesktopLayer ||
                IsRaisedFromManager ||
                ShouldDeferDesktopLayerRestore() ||
                (DateTime.UtcNow - LastElevateForInteractionUtc).TotalMilliseconds <= 300)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero ||
                App.Current.IsDeskBoxWindow(foregroundWindow))
            {
                // Zero means activation is mid-transition (owned popover
                // hand-off); do not read the gap as a DeskBox leave.
                RestoreDesktopLayerWhenIdle = false;
                return;
            }

            RestoreDesktopLayerWhenIdle = true;
            // Raised-session holds are decided inline: while the tray session
            // is alive its restore monitor owns the layer; otherwise this
            // window restores itself.
            if (App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                if (App.Current.WidgetManager is { HasActiveTemporaryRaiseLease: true } manager &&
                    manager.RestoreTemporarilyRaisedWidgetsToDesktopLayer("deskbox-leave"))
                {
                    return;
                }

                RestoreDesktopLayer(force: true);
            }
        });
    }

    // ── Tray animation ─────────────────────────────────────────

    private void ShowWithoutActivation(bool persistVisibility)
    {
        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        UpdatePersistedVisibility(isVisible: true, persistVisibility);

        ApplyBackdropPreference();
        NotifyCompactHostVisibilityChanged(true);
        QueueVisibleContentResume();
    }

    private void QueueVisibleContentResume()
    {
        int generation = ++_contentVisibilityGeneration;
        if (!Visible || generation != _contentVisibilityGeneration || IsClosing)
        {
            return;
        }

        _contentHost.OnWindowVisibilityChanged(true);
        PerformanceLogger.Mark(
            "WidgetVisibleContentFirstFrameReady",
            $"kind={_config.WidgetKind} id={_config.Id}");
    }

    private void NotifyVisibleContentRevealCompleted()
    {
        if (!Visible || IsClosing)
        {
            return;
        }

        int generation = _contentVisibilityGeneration;
        if (_queuedContentRevealGeneration == generation)
        {
            return;
        }

        _queuedContentRevealGeneration = generation;
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(RevealCompletedBackgroundDelayMs);
                if (!Visible ||
                    IsClosing ||
                    generation != _contentVisibilityGeneration)
                {
                    return;
                }

                _contentHost.OnWindowRevealCompleted();
                PerformanceLogger.Mark(
                    "WidgetVisibleContentResumed",
                    $"kind={_config.WidgetKind} id={_config.Id}");
            }))
        {
            _queuedContentRevealGeneration = -1;
        }
    }

    private void NotifyVisibleContentSuspended()
    {
        _contentVisibilityGeneration++;
        _contentHost.OnWindowVisibilityChanged(false);
    }

    private void UpdatePersistedVisibility(bool isVisible, bool persistVisibility)
    {
        _config.IsVisible = isVisible;
        bool groupVisibilityHandled =
            App.Current?.WidgetManager?.SetWidgetGroupVisibility(
                _config,
                isVisible,
                persistVisibility) == true;
        if (persistVisibility && !groupVisibilityHandled)
        {
            SettingsService.SaveDebounced();
        }
    }

    private void DeferDynamicRestingClickRaiseScope()
    {
        IntPtr activatedHandle = HWnd;
        UiDispatch.PostAfterCallback(() =>
        {
            if (!Win32Helper.IsWindowInCurrentProcess(activatedHandle) ||
                (IsDragging && HasMovedTitleBarDrag) ||
                App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
            {
                return;
            }

            IntPtr foregroundNow = Win32Helper.GetForegroundWindow();
            if (foregroundNow != activatedHandle &&
                Win32Helper.GetAncestor(foregroundNow, Win32Helper.GA_ROOTOWNER) != activatedHandle)
            {
                return;
            }

            if (WidgetLayerService.UsesDynamicGroupClickRaiseMode())
            {
                App.Current.WidgetManager?.PromotePeerWidgetsAfterDynamicBodyActivation(
                    activatedHandle);
            }
            else
            {
                App.Current.WidgetManager?.RebedDynamicPassiveSiblingsAfterSingleActivation(
                    activatedHandle,
                    "content-body-single-activated");
            }
        });
    }

    private void PushToBottom(bool showWindow = true)
    {
        IsRaisedFromManager = false;
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow);
        App.LogVerbose($"[ZOrder] Content PushToBottom hwnd=0x{HWnd.ToInt64():X}");
        App.Current.WidgetManager?.QueueIdleWidgetZOrderNormalization(
            "content-pushed-to-desktop");
    }
}
