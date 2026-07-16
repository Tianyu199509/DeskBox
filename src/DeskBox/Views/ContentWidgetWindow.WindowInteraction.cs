using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
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
    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!IsCompactBoundsStateActive || CurrentContent is not TodoWidgetContentAdapter todo)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AcceptedOperation = await todo.CanImportExternalDropAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = e.AcceptedOperation == DataPackageOperation.None
                ? string.Empty
                : App.Current.LocalizationService.T("Widget.Compact.TodoDropHint");
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!IsCompactBoundsStateActive || CurrentContent is not TodoWidgetContentAdapter todo)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.Handled = true;
            e.AcceptedOperation = await todo.ImportExternalDropAsync(e.DataView)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
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
        var properties = e.GetCurrentPoint(ContentWidgetShell.TitleBar).Properties;
        if (!properties.IsLeftButtonPressed) return;
        if (ContentWidgetShell.TitleEditorContent is TextBox &&
            ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            _ = CommitTitleRenameAsync();
            e.Handled = true;
            return;
        }

        if (ShouldOpenTitleBarFlyout(e.OriginalSource))
        {
            App.Current.WidgetManager?.ActivateAllVisibleWidgetsFromTitle(HWnd);
        }
        if (_config.IsPositionLocked) return;
        BeginWindowDragCore(e, ContentWidgetShell.TitleBar);
    }

    private bool ShouldOpenTitleBarFlyout(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return true;
        }

        return !IsWithin(source, ContentWidgetShell.PositionLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.SizeLockActionButton) &&
               !IsWithin(source, ContentWidgetShell.AddActionButton) &&
               !IsWithin(source, ContentWidgetShell.MoreActionButton) &&
               !IsWithin(source, ContentWidgetShell.CloseActionButton) &&
               !HasAncestorOfType<TextBox>(source);
    }

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

        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    // ── Activation ─────────────────────────────────────────────

    private void ContentWidgetWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _contentHost.OnDeactivated();
            if (Visible && !IsAtDesktopLayer &&
                App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
            {
                QueueRestoreDesktopLayerIfForegroundLeavesDeskBox();
            }
            return;
        }

        _contentHost.OnActivated();
    }

    private void QueueRestoreDesktopLayerIfForegroundLeavesDeskBox()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            if (!Visible || IsAtDesktopLayer || ShouldDeferDesktopLayerRestore())
            {
                return;
            }

            App.Log($"[ZOrder] Content Deactivated->Restore hwnd=0x{HWnd.ToInt64():X}");
            RestoreDesktopLayer(force: true);
        });
    }

    // ── Tray animation ─────────────────────────────────────────

    private void ShowWithoutActivation(bool persistVisibility)
    {
        AppWindow.Show();
        Win32Helper.ShowWindow(HWnd, Win32Helper.SW_SHOWNOACTIVATE);
        Visible = true;
        _config.IsVisible = true;
        if (persistVisibility)
        {
            SettingsService.SaveDebounced();
        }

        ApplyBackdropPreference();
        _contentHost.OnWindowVisibilityChanged(true);
    }

    private void PushToBottom()
    {
        IsAtDesktopLayer = true;
        WidgetLayerService.MoveToDesktopBottom(HWnd);
        App.LogVerbose($"[ZOrder] Content PushToBottom hwnd=0x{HWnd.ToInt64():X}");
    }
}
