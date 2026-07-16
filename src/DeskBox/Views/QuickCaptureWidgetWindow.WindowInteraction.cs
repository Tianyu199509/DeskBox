using System.Diagnostics;
using System.Numerics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Media.Ocr;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class QuickCaptureWidgetWindow
{
    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool isLeftButtonPressed = e.GetCurrentPoint(TitleBarGrid).Properties.IsLeftButtonPressed;
        if (isLeftButtonPressed &&
            QuickCaptureShell.TitleEditorContent is TextBox &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitTitleRenameAsync();
            e.Handled = true;
            return;
        }

        if (isLeftButtonPressed && ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(_hWnd);
        }

        BeginWindowDrag(e, TitleBarGrid, focusWhenClicked: true);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void DragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginWindowDrag(e, QuickCaptureShell.DragHandleElement, focusWhenClicked: false);
    }

    private void DragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ContinueWindowDrag(e);
    }

    private void DragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndWindowDrag(e);
    }

    private void BeginWindowDrag(PointerRoutedEventArgs e, FrameworkElement captureElement, bool focusWhenClicked)
    {
        if (ViewModel.Config.IsPositionLocked)
        {
            return;
        }

        var properties = e.GetCurrentPoint(captureElement).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        _focusRootAfterDragClick = focusWhenClicked;
        BeginWindowDragCore(e, captureElement);
    }

    private void ContinueWindowDrag(PointerRoutedEventArgs e)
    {
        ContinueWindowDragCore(e);
    }

    private void EndWindowDrag(PointerRoutedEventArgs e)
    {
        EndWindowDragCore(e);
    }

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

    private void ResizeBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        string tag = element.Tag as string ?? string.Empty;
        var shape = GetResizeCursorShapeForCurrentState(tag);
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
    }

    private void QuickCaptureWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ApplyBackdropPreference());

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (Visible && !_isAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true } &&
                (DateTime.UtcNow - _lastElevateForInteractionUtc).TotalMilliseconds > 300)
            {
                App.Log($"[ZOrder] QuickCapture Deactivated→QueueRestore hwnd=0x{_hWnd.ToInt64():X}");
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }

            return;
        }

        if (args.WindowActivationState != WindowActivationState.PointerActivated ||
            !Visible ||
            !_isAtDesktopLayer ||
            _isDragging ||
            _isResizing ||
            WidgetLayerService.UsesDesktopPinnedMode() ||
            (App.Current.WidgetManager is { WidgetsRaisedFromTray: true }))
        {
            App.LogVerbose($"[ZOrder] QuickCapture PointerActivated BLOCKED hwnd=0x{_hWnd.ToInt64():X} visible={Visible} atDesktop={_isAtDesktopLayer} raised={App.Current.WidgetManager?.WidgetsRaisedFromTray}");
            return;
        }

        App.Log($"[ZOrder] QuickCapture PointerActivated→Elevate hwnd=0x{_hWnd.ToInt64():X}");
        _isAtDesktopLayer = false;
        _keepRaisedUntilDeactivate = true;
        _restoreDesktopLayerWhenIdle = false;
        PlayTrayRaiseAnimation();
        StartTopMostSafetyTimer();
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible || _isAtDesktopLayer)
            {
                return;
            }

            IntPtr foregroundWindow = Win32Helper.GetForegroundWindow();
            bool foregroundIsWidget = App.Current.WidgetManager is { } wm && wm.IsWidgetWindow(foregroundWindow);
            if (foregroundIsWidget)
            {
                _restoreDesktopLayerWhenIdle = false;
                return;
            }

            _restoreDesktopLayerWhenIdle = true;
            if (App.Current.WidgetManager is { } widgetManager)
            {
                if (!widgetManager.RequestRestoreRaisedWidgetsToDesktopLayer("quick-window-deactivated"))
                {
                    RestoreDesktopLayer(force: true);
                }
            }
            else
            {
                RestoreDesktopLayer(force: true);
            }
        });
    }

    private void ShowConfirmMenu(FrameworkElement anchor, string title, string actionText, Func<Task> confirmedAction)
    {
        _pendingDeleteConfirmFlyout?.Hide();

        var flyout = WidgetCompactConfirmationMenuBuilder.CreateDeleteConfirmation(
            title,
            actionText,
            confirmedAction);
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_pendingDeleteConfirmFlyout, flyout))
            {
                _pendingDeleteConfirmFlyout = null;
            }
        };

        _pendingDeleteConfirmFlyout = flyout;
        ShowFlyoutWithElevation(flyout, anchor);
    }
}
