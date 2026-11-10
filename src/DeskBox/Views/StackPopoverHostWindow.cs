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
    private readonly IntPtr _ownerWindowHandle;
    private Grid? _hostRoot;
    private SystemBackdropElement? _backdropElement;
    private WidgetMaterialSystemBackdrop? _materialBackdrop;
    private SolidTintSystemBackdrop? _fallbackTransparentBackdrop;
    private WidgetMaterialBackdropAppearance? _savedAppearance;
    private bool _savedFollowMaterialStyle;
    private FrameworkElement? _content;
    private KeyEventHandler? _previewKeyDownHandler;
    private KeyEventHandler? _keyDownHandler;
    private INativeDropHostBridge? _nativeDropBridge;
    private bool _closed;
    private bool _isShown;
    private bool _isPreparing;
    private bool _externalDragPassthroughApplied;
    private long _showSessionId;

    private readonly Win32Helper.SubclassProc _inputSubclassProc;
    private bool _inputSubclassInstalled;

    private static readonly UIntPtr InputSubclassId = new(0xDDB2);

    public StackPopoverHostWindow(IntPtr ownerWindowHandle)
    {
        Title = "DeskBox Stack Popover";
        _ownerWindowHandle = ownerWindowHandle;

        _inputSubclassProc = CallbackDepth.WrapSubclassProc(InputSubclassProc);
        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

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
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
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

            long sessionId = _showSessionId;
            Win32Helper.POINT? deactivationCursor =
                Win32Helper.GetCursorPos(out Win32Helper.POINT cursorPoint)
                    ? cursorPoint
                    : null;
            // Defer _appWindow.Hide() out of the WM_ACTIVATE callback frame:
            // calling ShowWindow(SW_HIDE) synchronously while Win32 is mid-way
            // through activating the clicked DeskBox widget triggers fallback
            // activation that skips WS_EX_TOOLWINDOW widgets and hands the
            // foreground to Explorer/foreign apps, collapsing raised groups.
            UiDispatch.PostAfterCallback(() =>
            {
                if (_closed || !_isShown || sessionId != _showSessionId)
                {
                    return;
                }

                if (_content?.XamlRoot is { } currentXamlRoot &&
                    VisualTreeHelper.GetOpenPopupsForXamlRoot(currentXamlRoot).Count > 0)
                {
                    return;
                }

                IntPtr fg = Win32Helper.GetForegroundWindow();
                if (fg == WindowHandle)
                {
                    return;
                }

                if (DeskBox.Helpers.TransientInputFocusGuard.IsWindowsTextInputExperienceWindow(fg) ||
                    DeskBox.Helpers.TransientInputFocusGuard.IsCurrentProcessPopupBridge(fg))
                {
                    DeskBox.Helpers.TransientInputFocusGuard.WatchPopupWindowInputExperienceFocus(
                        DispatcherQueue,
                        WindowHandle,
                        () => !_closed && _isShown && sessionId == _showSessionId,
                        () =>
                        {
                            if (!_closed && _isShown && sessionId == _showSessionId)
                            {
                                LastDeactivationCursorPoint = deactivationCursor;
                                DeactivatedByOutsideClick?.Invoke();
                            }
                        });
                    return;
                }

                LastDeactivationCursorPoint = deactivationCursor;
                DeactivatedByOutsideClick?.Invoke();
            });
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

    internal event Action? UndoRequested;

    internal IntPtr WindowHandle { get; }

    internal Win32Helper.POINT? LastDeactivationCursorPoint { get; private set; }

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

        _backdropElement = new SystemBackdropElement
        {
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _hostRoot = new Grid();
        WidgetTextShadow.SetScope(_hostRoot, true);
        if (_savedAppearance is { } savedAppearance)
        {
            WidgetTextShadow.SetPlateOpaque(
                _hostRoot,
                ResolvePopoverPlateOpaque(savedAppearance, _savedFollowMaterialStyle));
        }
        _hostRoot.Children.Add(_backdropElement);
        _hostRoot.Children.Add(content);
        SyncBackdropElementCornerRadius();
        Content = _hostRoot;
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

        ++_showSessionId;
        LastDeactivationCursorPoint = null;
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
        _nativeDropBridge?.EnsureRegistered();
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

    internal void RevealPreparedWithoutActivation(RectInt32 bounds)
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
            _ = ShowWindow(WindowHandle, SwShowNoActivate);
        }

        SetCloaked(false);
        _isPreparing = false;
        _isShown = true;
        _nativeDropBridge?.EnsureRegistered();
    }

    internal void SetExternalDragPassthrough(bool enabled)
    {
        if (_closed || WindowHandle == IntPtr.Zero)
        {
            return;
        }

        double targetOpacity = enabled ? 0.18 : 1.0;
        if (_hostRoot is not null && Math.Abs(_hostRoot.Opacity - targetOpacity) > 0.01)
        {
            _hostRoot.Opacity = targetOpacity;
        }

        if (_externalDragPassthroughApplied == enabled)
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE);
        int updatedStyle = enabled
            ? (extendedStyle | Win32Helper.WS_EX_TRANSPARENT)
            : (extendedStyle & ~Win32Helper.WS_EX_TRANSPARENT);
        if (updatedStyle != extendedStyle)
        {
            _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE, updatedStyle);
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
        }

        _externalDragPassthroughApplied = enabled;
    }

    internal void AttachNativeDropBridge(INativeDropHostBridge? bridge)
    {
        if (ReferenceEquals(_nativeDropBridge, bridge))
        {
            _nativeDropBridge?.EnsureRegistered();
            return;
        }

        _nativeDropBridge?.Dispose();
        _nativeDropBridge = _closed ? null : bridge;
        if (_closed)
        {
            bridge?.Dispose();
            return;
        }

        _nativeDropBridge?.EnsureRegistered();
    }

    internal void HidePopover(bool restoreOwnerForeground = false)
    {
        if (_closed)
        {
            return;
        }

        SetExternalDragPassthrough(false);

        ++_showSessionId;
        _isShown = false;
        _isPreparing = false;
        SetCloaked(true);

        IntPtr foregroundBeforeHide = Win32Helper.GetForegroundWindow();
        if (restoreOwnerForeground &&
            foregroundBeforeHide == WindowHandle &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            _ownerWindowHandle != IntPtr.Zero &&
            Win32Helper.IsWindow(_ownerWindowHandle) &&
            Win32Helper.IsWindowVisible(_ownerWindowHandle))
        {
            _ = SetForegroundWindow(_ownerWindowHandle);
            _ = SetActiveWindow(_ownerWindowHandle);
            foregroundBeforeHide = _ownerWindowHandle;
        }

        if (_appWindow.IsVisible)
        {
            _appWindow.Hide();
        }

        if (foregroundBeforeHide != IntPtr.Zero &&
            foregroundBeforeHide != WindowHandle &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            App.Current?.IsDeskBoxWindow(foregroundBeforeHide) == true &&
            Win32Helper.IsWindow(foregroundBeforeHide) &&
            Win32Helper.IsWindowVisible(foregroundBeforeHide) &&
            Win32Helper.GetForegroundWindow() != foregroundBeforeHide)
        {
            _ = SetForegroundWindow(foregroundBeforeHide);
            _ = SetActiveWindow(foregroundBeforeHide);
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
        if (_hostRoot is not null)
        {
            WidgetTextShadow.SetPlateOpaque(
                _hostRoot,
                ResolvePopoverPlateOpaque(materialAppearance, followMaterialStyle));
        }
        Win32Helper.SetWindowTheme(WindowHandle, materialAppearance.IsDark);
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        SyncBackdropElementCornerRadius();

        if (_isShown || _isPreparing)
        {
            ApplyBackdropCore(materialAppearance, followMaterialStyle);
        }
    }

    private static bool ResolvePopoverPlateOpaque(
        WidgetMaterialBackdropAppearance materialAppearance,
        bool followMaterialStyle) =>
        !followMaterialStyle ||
        WidgetTextShadow.ShouldSuppressForOpaquePlate(
            materialAppearance.SurfaceOpacity,
            materialAppearance.MaterialType,
            hasImageBackground: false);

    private void SyncBackdropElementCornerRadius()
    {
        if (_backdropElement is null)
        {
            return;
        }

        if (_backdropElement.CornerRadius != default)
        {
            _backdropElement.CornerRadius = default;
        }

        double radius = (_content as Border)?.CornerRadius.TopLeft ?? 0;
        _materialBackdrop?.SetCornerRadius(Math.Max(0, radius));
    }

    private void EnsureBackdropAttached()
    {
        if (_closed || _savedAppearance is not { } appearance)
        {
            return;
        }

        SyncBackdropElementCornerRadius();
        if (SystemBackdrop is not null &&
            (_backdropElement is null || _backdropElement.Visibility == Visibility.Visible ||
             (_savedFollowMaterialStyle && !WidgetMaterialSystemBackdrop.IsSupported(appearance.MaterialType))))
        {
            return;
        }

        ApplyBackdropCore(appearance, _savedFollowMaterialStyle);
    }

    private void ApplyBackdropCore(
        WidgetMaterialBackdropAppearance materialAppearance,
        bool followMaterialStyle)
    {
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        ApplyTransparentFallbackBackdrop();

        WidgetMaterialBackdropAppearance? targetAppearance = null;
        if (followMaterialStyle &&
            WidgetMaterialSystemBackdrop.IsSupported(
                materialAppearance.MaterialType))
        {
            targetAppearance = materialAppearance;
        }
        else if (!followMaterialStyle && WindowsCompatibilityService.SupportsDesktopAcrylic)
        {
            targetAppearance = new WidgetMaterialBackdropAppearance(
                SettingsService.WidgetMaterialTypeAcrylic,
                materialAppearance.IsDark,
                AccentColorHelper.DefaultAccentColor,
                SurfaceOpacity: 0.85,
                MaterialIntensity: 0.5);
        }

        if (targetAppearance is { } activeAppearance)
        {
            _materialBackdrop ??= new WidgetMaterialSystemBackdrop(
                activeAppearance,
                closeElementTargetOnDisconnect: true);
            SyncBackdropElementCornerRadius();
            _materialBackdrop.UpdateAppearance(activeAppearance);
            if (_backdropElement is not null)
            {
                _backdropElement.Visibility = Visibility.Visible;
                if (!ReferenceEquals(_backdropElement.SystemBackdrop, _materialBackdrop))
                {
                    _backdropElement.SystemBackdrop = _materialBackdrop;
                }
            }
        }
        else
        {
            SyncBackdropElementCornerRadius();
            _materialBackdrop?.Suspend();
            if (_backdropElement is not null)
            {
                _backdropElement.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyTransparentFallbackBackdrop()
    {
        try
        {
            Win32Helper.EnableTransparentWindowHost(WindowHandle);
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
        _materialBackdrop?.Suspend();
        if (_backdropElement is not null)
        {
            _backdropElement.Visibility = Visibility.Collapsed;
        }
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
        if (_fallbackTransparentBackdrop is not null)
        {
            _fallbackTransparentBackdrop.Dispose();
            _fallbackTransparentBackdrop = null;
        }

        Win32Helper.DisableTransparentWindowHost(WindowHandle);
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
        _nativeDropBridge?.Dispose();
        _nativeDropBridge = null;
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
        DisposeMaterialBackdrop();
        _backdropElement = null;
        _hostRoot = null;
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
        if (_closed ||
            DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(args))
        {
            return;
        }

        if (args.Key == Windows.System.VirtualKey.Z &&
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift) &&
            !Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu) &&
            !IsInsideTextInputControl(args.OriginalSource as DependencyObject))
        {
            args.Handled = true;
            UndoRequested?.Invoke();
            return;
        }

        if (args.Key != Windows.System.VirtualKey.Escape)
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
        if (args.Key != Windows.System.VirtualKey.Escape ||
            args.Handled ||
            _closed ||
            DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(args))
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
            if (current is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
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
