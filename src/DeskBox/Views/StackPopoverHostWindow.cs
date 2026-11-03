using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using static DeskBox.Platform.MonitorAndBackdropInterop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// hidden with its SystemBackdrop released between shows, and its content tree
/// stays realized — no container churn, no island churn, and no offscreen
/// TopMost swapchain compositing while closed.
/// </summary>
internal sealed class StackPopoverHostWindow : Window
{
    private const int DwmwaCloak = 13;
    private const int SwShowNoActivate = 4;
    private const uint GwChild = 5;

    private readonly AppWindow _appWindow;
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private DesktopAcrylicBackdrop? _neutralBackdrop;
    private SolidTintSystemBackdrop? _fallbackTransparentBackdrop;
    private WidgetMaterialBackdropAppearance? _savedAppearance;
    private bool _savedFollowMaterialStyle;
    private FrameworkElement? _content;
    private KeyEventHandler? _previewKeyDownHandler;
    private KeyEventHandler? _keyDownHandler;
    private bool _closed;
    private bool _isShown;
    private bool _isPreparing;

    private readonly Win32Helper.SubclassProc _inputSubclassProc;
    private bool _inputSubclassInstalled;

    private static readonly UIntPtr InputSubclassId = new(0xDDB2);

    public StackPopoverHostWindow(IntPtr ownerWindowHandle)
    {
        Title = "DeskBox Stack Popover";

        _inputSubclassProc = CallbackDepth.WrapSubclassProc(InputSubclassProc);
        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

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
        SetCloaked(true);

        // The popover is TOPMOST and can swallow clicks that should dismiss a
        // native Shell menu opened from inside it (the menu lives in the
        // context-menu proxy process), so close the menu explicitly on press.
        _inputSubclassInstalled = Win32Helper.SetWindowSubclass(
            WindowHandle,
            _inputSubclassProc,
            InputSubclassId,
            UIntPtr.Zero);

        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated ||
                _closed ||
                !_isShown)
            {
                return;
            }

            if (_content?.XamlRoot is { } xamlRoot &&
                VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0)
            {
                return;
            }

            DeactivatedByOutsideClick?.Invoke();
        };
    }

    /// <summary>
    /// Raised when another window takes activation while this host is shown —
    /// the equivalent of popup light dismiss.
    /// </summary>
    internal event Action? DeactivatedByOutsideClick;

    /// <summary>
    /// Raised from the host root's key route so Escape works regardless of
    /// whether the list, title, or close button currently owns focus, while
    /// allowing an active text editor (such as stack rename) to handle Escape first.
    /// </summary>
    internal event Action? EscapeRequested;

    internal IntPtr WindowHandle { get; }

    internal bool IsVisible => !_closed && _isShown;

    internal void SetContent(FrameworkElement content)
    {
        if (_content is not null)
        {
            if (_previewKeyDownHandler is not null)
            {
                _content.RemoveHandler(
                    UIElement.PreviewKeyDownEvent,
                    _previewKeyDownHandler);
            }

            if (_keyDownHandler is not null)
            {
                _content.RemoveHandler(
                    UIElement.KeyDownEvent,
                    _keyDownHandler);
            }
        }

        _content = content;
        _previewKeyDownHandler = new KeyEventHandler(Content_PreviewKeyDown);
        _keyDownHandler = new KeyEventHandler(Content_KeyDown);
        content.AddHandler(
            UIElement.PreviewKeyDownEvent,
            _previewKeyDownHandler,
            handledEventsToo: true);
        content.AddHandler(
            UIElement.KeyDownEvent,
            _keyDownHandler,
            handledEventsToo: false);
        Content = content;
    }

    internal void ShowAt(RectInt32 bounds)
    {
        if (_closed)
        {
            return;
        }

        PrepareForShow(bounds);
        RevealPrepared(bounds);
    }

    internal void PrepareForShow(RectInt32 bounds)
    {
        if (_closed)
        {
            return;
        }

        _isPreparing = true;
        if (!_isShown)
        {
            SetCloaked(true);
        }

        EnsureBackdropAttached();
        _appWindow.MoveAndResize(bounds);
        if (!_appWindow.IsVisible)
        {
            _ = ShowWindow(WindowHandle, SwShowNoActivate);
        }

        _content?.UpdateLayout();
    }

    internal void RevealPrepared(RectInt32 bounds)
    {
        if (_closed)
        {
            return;
        }

        EnsureBackdropAttached();
        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOOWNERZORDER);

        _appWindow.MoveAndResize(bounds);
        if (_appWindow.Size.Width != bounds.Width ||
            _appWindow.Size.Height != bounds.Height)
        {
            _appWindow.MoveAndResize(bounds);
        }

        if (!_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        SetCloaked(false);
        _isPreparing = false;
        _isShown = true;
        ActivateInput();
    }

    /// <summary>
    /// Ensures the popover window owns Win32 foreground/island keyboard focus so
    /// hosted text controls (such as stack title rename) receive keyboard and IME input.
    /// </summary>
    internal void ActivateInput(UIElement? focusTarget = null)
    {
        if (_closed)
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(
            WindowHandle,
            Win32Helper.GWL_EXSTYLE);
        if ((extendedStyle & Win32Helper.WS_EX_NOACTIVATE) != 0)
        {
            extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
            _ = Win32Helper.SetWindowLong(
                WindowHandle,
                Win32Helper.GWL_EXSTYLE,
                extendedStyle);
        }

        _ = SetForegroundWindow(WindowHandle);
        _ = SetActiveWindow(WindowHandle);
        Activate();

        IntPtr childIslandHwnd = GetWindow(WindowHandle, GwChild);
        _ = SetFocus(childIslandHwnd != IntPtr.Zero ? childIslandHwnd : WindowHandle);

        if (focusTarget is Control control)
        {
            _ = control.Focus(FocusState.Programmatic);
        }
        else
        {
            focusTarget?.Focus(FocusState.Programmatic);
        }
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

        _isShown = false;
        _isPreparing = false;
        SetCloaked(true);
        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }

        ReleaseActiveBackdrop();
    }

    internal void UpdateAppearance(
        WidgetMaterialBackdropAppearance materialAppearance,
        bool followMaterialStyle)
    {
        if (_closed)
        {
            return;
        }

        _savedAppearance = materialAppearance;
        _savedFollowMaterialStyle = followMaterialStyle;
        Win32Helper.SetWindowTheme(WindowHandle, materialAppearance.IsDark);

        if (_isShown || _isPreparing)
        {
            ApplyBackdropCore(materialAppearance, followMaterialStyle);
        }
    }

    private void EnsureBackdropAttached()
    {
        if (_closed || _savedAppearance is not { } appearance)
        {
            return;
        }

        if (SystemBackdrop is not null)
        {
            return;
        }

        ApplyBackdropCore(appearance, _savedFollowMaterialStyle);
    }

    private void ApplyBackdropCore(
        WidgetMaterialBackdropAppearance materialAppearance,
        bool followMaterialStyle)
    {
        if (followMaterialStyle &&
            WidgetMaterialSystemBackdrop.IsSupported(
                materialAppearance.MaterialType))
        {
            _neutralBackdrop = null;
            DisposeFallbackBackdrop();
            _materialBackdrop ??= new WidgetMaterialSystemBackdrop(
                materialAppearance);
            _materialBackdrop.UpdateAppearance(materialAppearance);
            if (!ReferenceEquals(SystemBackdrop, _materialBackdrop))
            {
                SystemBackdrop = _materialBackdrop;
            }
        }
        else if (WindowsCompatibilityService.SupportsDesktopAcrylic)
        {
            DisposeMaterialBackdrop();
            DisposeFallbackBackdrop();
            try
            {
                _neutralBackdrop ??= new DesktopAcrylicBackdrop();
                if (!ReferenceEquals(SystemBackdrop, _neutralBackdrop))
                {
                    SystemBackdrop = _neutralBackdrop;
                }
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[FileStack] Popover DesktopAcrylicBackdrop unavailable: {ex.Message}");
                _neutralBackdrop = null;
                ApplyTransparentFallbackBackdrop();
            }
        }
        else
        {
            DisposeMaterialBackdrop();
            _neutralBackdrop = null;
            ApplyTransparentFallbackBackdrop();
        }
    }

    private void ApplyTransparentFallbackBackdrop()
    {
        try
        {
            _fallbackTransparentBackdrop ??= new SolidTintSystemBackdrop(
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (!ReferenceEquals(SystemBackdrop, _fallbackTransparentBackdrop))
            {
                SystemBackdrop = _fallbackTransparentBackdrop;
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[FileStack] Popover transparent tint fallback unavailable: {ex.Message}");
            DisposeFallbackBackdrop();
            SystemBackdrop = null;
        }
    }

    private void ReleaseActiveBackdrop()
    {
        SystemBackdrop = null;
        DisposeMaterialBackdrop();
        _neutralBackdrop = null;
        DisposeFallbackBackdrop();
    }

    private void DisposeMaterialBackdrop()
    {
        if (_materialBackdrop is null)
        {
            return;
        }

        _materialBackdrop.Dispose();
        _materialBackdrop = null;
    }

    private void DisposeFallbackBackdrop()
    {
        if (_fallbackTransparentBackdrop is null)
        {
            return;
        }

        _fallbackTransparentBackdrop.Dispose();
        _fallbackTransparentBackdrop = null;
    }

    private void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Win32Helper.TrySetDwmWindowAttribute(WindowHandle, DwmwaCloak, ref value);
    }

    internal void Destroy()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _isShown = false;
        _isPreparing = false;
        if (_inputSubclassInstalled)
        {
            _ = Win32Helper.RemoveWindowSubclass(
                WindowHandle,
                _inputSubclassProc,
                InputSubclassId);
            _inputSubclassInstalled = false;
        }

        if (_content is not null)
        {
            if (_previewKeyDownHandler is not null)
            {
                _content.RemoveHandler(
                    UIElement.PreviewKeyDownEvent,
                    _previewKeyDownHandler);
            }

            if (_keyDownHandler is not null)
            {
                _content.RemoveHandler(
                    UIElement.KeyDownEvent,
                    _keyDownHandler);
            }
        }

        ReleaseActiveBackdrop();
        _content = null;
        _previewKeyDownHandler = null;
        _keyDownHandler = null;
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

        // Let focused text editors (such as stack rename TextBox or active IME)
        // process Escape first; unhandled Escape still reaches Content_KeyDown.
        if (IsInsideTextInputControl(args.OriginalSource as DependencyObject))
        {
            return;
        }

        args.Handled = true;
        EscapeRequested?.Invoke();
    }

    private void Content_KeyDown(
        object sender,
        KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || args.Handled || _closed)
        {
            return;
        }

        args.Handled = true;
        EscapeRequested?.Invoke();
    }

    private static bool IsInsideTextInputControl(DependencyObject? source)
    {
        DependencyObject? current = source;
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            if (current is TextBox or RichEditBox or PasswordBox or AutoSuggestBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private IntPtr InputSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        // Every press inside the popover closes an open native context menu:
        // the menu is hosted by another process, and this window's permanent
        // TOPMOST placement means the click would otherwise never reach it.
        if (message is Win32Helper.WM_LBUTTONDOWN
            or Win32Helper.WM_RBUTTONDOWN
            or Win32Helper.WM_MBUTTONDOWN
            or Win32Helper.WM_XBUTTONDOWN)
        {
            ShellContextMenuProxy.CancelOpenMenu();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

}
