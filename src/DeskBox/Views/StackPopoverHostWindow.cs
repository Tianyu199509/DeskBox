using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Persistent borderless host for the stack popover. Replaces the previous
/// unconstrained XAML Popup, whose top-level hwnd island leaked native memory
/// on every open/close cycle (verified: constrained popups return to baseline,
/// unconstrained ones never do). The window is created once per surface,
/// hidden between shows, and its content tree stays realized — no container
/// churn, no island churn.
/// </summary>
internal sealed class StackPopoverHostWindow : Window
{

    private readonly AppWindow _appWindow;
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private DesktopAcrylicBackdrop? _neutralBackdrop;
    private FrameworkElement? _content;
    private KeyEventHandler? _previewKeyDownHandler;
    private bool _closed;
    private readonly IntPtr _ownerWindowHandle;

    public StackPopoverHostWindow(IntPtr ownerWindowHandle)
    {
        _ownerWindowHandle = ownerWindowHandle;
        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        if (ownerWindowHandle != IntPtr.Zero)
        {
            _ = Win32Helper.SetWindowLongPtr(
                WindowHandle,
                Win32Helper.GWLP_HWNDPARENT,
                ownerWindowHandle);
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            WindowHandle,
            Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        // Permanently topmost: quick-reveal layer keeps widgets in the
        // TOPMOST band, and dynamically toggling WS_EX_TOPMOST on open/close
        // reorders the whole band through DWM (the flash/layering cycle we
        // kept hitting). A permanently-topmost popover is invisible while
        // hidden, sits above its owner in every layer mode while shown, and
        // never triggers a band migration at runtime.
        extendedStyle |= Win32Helper.WS_EX_TOPMOST;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(
            WindowHandle,
            Win32Helper.GWL_EXSTYLE,
            extendedStyle);

        int style = Win32Helper.GetWindowLong(
            WindowHandle,
            Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
            Win32Helper.WS_BORDER |
            Win32Helper.WS_DLGFRAME |
            Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(
            WindowHandle,
            Win32Helper.GWL_STYLE,
            style);
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);
        // WS_EX_TOPMOST alone is a style flag; the window must also be
        // placed into the TOPMOST band via SetWindowPos for it to take
        // effect. Do this while the window is still invisible — it cannot
        // affect any other window's z-order or repaint.
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE);
        Win32Helper.SetWindowBorderColor(
            WindowHandle,
            unchecked((int)0xFFFFFFFE));
        Win32Helper.ApplyFullWindowFrame(WindowHandle);

        // Bring the window into the shown state exactly once, parked off the
        // visible desktop. From this point the window is permanently visible
        // in z-order and permanently TOPMOST; open/close cycles only move it
        // between the off-screen parking slot and its on-screen bounds.
        //
        // Parking (instead of DWM cloaking) matters for correctness: a cloaked
        // window stops presenting frames, so revealing it composed the
        // previously opened stack's stale frame for a visible beat before the
        // refreshed content arrived. An off-screen window keeps rendering, so
        // the first on-screen frame is always the current one. Parking also
        // preserves the original reason for avoiding Show/Hide here: neither
        // z-order nor the TOPMOST band changes when only the position moves.
        _appWindow.MoveAndResize(BuildParkedBounds(320, 240));
        _appWindow.Show();

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated &&
                !_closed)
            {
                DeactivatedByOutsideClick?.Invoke();
            }
        };
    }

    /// <summary>
    /// Raised when another window takes activation while this host is shown —
    /// the equivalent of popup light dismiss.
    /// </summary>
    internal event Action? DeactivatedByOutsideClick;

    /// <summary>
    /// Raised from the host root's preview route so Escape works regardless
    /// of whether the list, title, or close button currently owns focus.
    /// </summary>
    internal event Action? EscapeRequested;

    internal IntPtr WindowHandle { get; }

    internal bool IsVisible => !_closed && !_parked;

    private bool _parked = true;

    internal void SetContent(FrameworkElement content)
    {
        if (_content is not null && _previewKeyDownHandler is not null)
        {
            _content.RemoveHandler(
                UIElement.PreviewKeyDownEvent,
                _previewKeyDownHandler);
        }

        _content = content;
        _previewKeyDownHandler = new KeyEventHandler(Content_PreviewKeyDown);
        content.AddHandler(
            UIElement.PreviewKeyDownEvent,
            _previewKeyDownHandler,
            handledEventsToo: true);
        Content = content;
    }

    internal void PrepareForShow(RectInt32 bounds)
    {
        if (_closed)
        {
            return;
        }

        // Apply the final size while still parked off-screen, then flush layout
        // so the island rebinds and re-measures its content at the new geometry.
        // Moving on-screen is deliberately a separate operation: when a cached
        // tree switches stacks, its new surface needs time to reach the
        // compositor before the old presented texture may be exposed again.
        _appWindow.MoveAndResize(new RectInt32(
            -32000,
            -32000,
            Math.Max(1, bounds.Width),
            Math.Max(1, bounds.Height)));
        _parked = true;
        _content?.UpdateLayout();
    }

    internal void RevealPrepared(RectInt32 bounds)
    {
        if (_closed)
        {
            return;
        }

        // Position-only reveal. The caller decides whether the prepared tree
        // can move immediately (first/same-stack open) or after two committed
        // composition frames (switching between cached stacks).
        _appWindow.MoveAndResize(bounds);
        _parked = false;
        Activate();
    }

    internal void UpdateBounds(RectInt32 bounds)
    {
        if (!_closed)
        {
            _appWindow.MoveAndResize(bounds);
        }
    }

    internal void HidePopover()
    {
        if (_closed)
        {
            return;
        }

        // Park off-screen instead of hiding or cloaking: the window keeps its
        // z-order and TOPMOST band, and it keeps presenting frames so the next
        // reveal never shows stale content.
        _appWindow.MoveAndResize(
            BuildParkedBounds(_appWindow.Size.Width, _appWindow.Size.Height));
        _parked = true;
    }

    private static RectInt32 BuildParkedBounds(int width, int height) =>
        new(-32000, -32000, Math.Max(1, width), Math.Max(1, height));

    internal void UpdateAppearance(
        WidgetMaterialBackdropAppearance materialAppearance,
        bool followMaterialStyle)
    {
        if (_closed)
        {
            return;
        }

        // Both styles MUST keep a SystemBackdrop on the window. Without one,
        // WinUI falls back to the layered-window composition path (per-pixel
        // alpha blending), which makes show/hide of a rounded popover visibly
        // slower and causes the busy-cursor stall. Neutral keeps the plain
        // acrylic backdrop under a semi-transparent solid surface — the
        // painted look stays, the fast DWM composition path is preserved.
        if (followMaterialStyle &&
            WidgetMaterialSystemBackdrop.IsSupported(
                materialAppearance.MaterialType))
        {
            _neutralBackdrop = null;
            _materialBackdrop ??= new WidgetMaterialSystemBackdrop(
                materialAppearance);
            _materialBackdrop.UpdateAppearance(materialAppearance);
            if (!ReferenceEquals(SystemBackdrop, _materialBackdrop))
            {
                SystemBackdrop = _materialBackdrop;
            }
        }
        else
        {
            _materialBackdrop = null;
            _neutralBackdrop ??= new DesktopAcrylicBackdrop();
            if (!ReferenceEquals(SystemBackdrop, _neutralBackdrop))
            {
                SystemBackdrop = _neutralBackdrop;
            }
        }

        Win32Helper.SetWindowTheme(WindowHandle, materialAppearance.IsDark);
    }

    internal void Destroy()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (_content is not null && _previewKeyDownHandler is not null)
        {
            _content.RemoveHandler(
                UIElement.PreviewKeyDownEvent,
                _previewKeyDownHandler);
        }
        SystemBackdrop = null;
        _materialBackdrop = null;
        _content = null;
        _previewKeyDownHandler = null;
        Content = null;
        Close();
    }

    private void Content_PreviewKeyDown(
        object sender,
        KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || _closed)
        {
            return;
        }

        args.Handled = true;
        EscapeRequested?.Invoke();
    }


}
