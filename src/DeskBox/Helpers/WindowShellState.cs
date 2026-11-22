// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskBox.Helpers;

/// <summary>
/// Best-effort shell window state. Early-logon sessions can surface E_NOTIMPL
/// from windowing APIs (observed on AppWindow.IsShownInSwitchers), so these
/// helpers degrade with a log line instead of failing the caller's window
/// initialization.
/// </summary>
internal static class WindowShellState
{
    private const int WsExAppWindow = 0x00040000;

    /// <summary>
    /// Keeps a helper window out of Alt+Tab. A window that still shows up in
    /// the switcher is an acceptable degraded state; breaking the caller is not.
    /// Also enforces <c>WS_EX_TOOLWINDOW</c> and clears <c>WS_EX_APPWINDOW</c>
    /// as a Win32 backstop when Win10 returns <c>E_NOTIMPL</c>.
    /// </summary>
    public static void TryHideFromSwitchers(AppWindow appWindow)
    {
        try
        {
            appWindow.IsShownInSwitchers = false;
        }
        catch (Exception ex)
        {
            App.Log($"[Window] IsShownInSwitchers not applied: {ex.Message}");
        }

        try
        {
            IntPtr hwnd = Win32Interop.GetWindowFromWindowId(appWindow.Id);
            if (hwnd != IntPtr.Zero && Win32Helper.IsWindow(hwnd))
            {
                int exStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
                int desired = (exStyle | Win32Helper.WS_EX_TOOLWINDOW) & ~WsExAppWindow;
                if (desired != exStyle)
                {
                    _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, desired);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Applies the borderless, fixed-size overlapped presenter shared by the
    /// popover hosts and the detach preview window.
    /// </summary>
    public static void TryApplyBorderlessOverlappedPresenter(AppWindow appWindow)
    {
        try
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Window] Borderless presenter not applied: {ex.Message}");
        }
    }

    public static void TryApplyFloatingPopupPresenter(AppWindow appWindow)
    {
        bool useNativeBorder = WindowsCompatibilityService.IsWindows11OrLater;
        try
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                // On Win10 (19041-19045), SetBorderAndTitleBar(true, false) with
                // IsResizable=false paints a thin light non-client top border strip
                // across dark/acrylic popups. Popups already draw a 1px XAML border.
                presenter.SetBorderAndTitleBar(useNativeBorder, false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Window] Floating popup presenter not applied: {ex.Message}");
        }

        if (!useNativeBorder)
        {
            try
            {
                IntPtr hwnd = Win32Interop.GetWindowFromWindowId(appWindow.Id);
                if (hwnd != IntPtr.Zero && Win32Helper.IsWindow(hwnd))
                {
                    int style = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_STYLE);
                    int stripped = style & ~(Win32Helper.WS_CAPTION |
                                             Win32Helper.WS_BORDER |
                                             Win32Helper.WS_DLGFRAME |
                                             Win32Helper.WS_THICKFRAME);
                    if (stripped != style)
                    {
                        _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_STYLE, stripped);
                        _ = Win32Helper.SetWindowPos(
                            hwnd,
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
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Applies consistent transparent extended-titlebar caption button colors
    /// (normal, inactive, hover, and pressed states) so caption glyphs never
    /// lose contrast when the app's explicit theme differs from the OS theme.
    /// </summary>
    public static void TryApplyExtendedTitleBarButtonColors(
        AppWindow? appWindow,
        bool isDark,
        bool syncProcessWideWin10Resources = true)
    {
        if (appWindow is null)
        {
            return;
        }

        if (!WindowsCompatibilityService.IsWindows11OrLater && syncProcessWideWin10Resources)
        {
            if (WindowsCompatibilityService.IsHighContrast)
            {
                TryClearWin10XamlCaptionResources();
            }
            else
            {
                TryUpdateWin10XamlCaptionResources(isDark);
            }
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        try
        {
            AppWindowTitleBar titleBar = appWindow.TitleBar;
            if (WindowsCompatibilityService.IsHighContrast)
            {
                titleBar.ButtonBackgroundColor = null;
                titleBar.ButtonInactiveBackgroundColor = null;
                titleBar.ButtonForegroundColor = null;
                titleBar.ButtonHoverForegroundColor = null;
                titleBar.ButtonPressedForegroundColor = null;
                titleBar.ButtonInactiveForegroundColor = null;
                titleBar.ButtonHoverBackgroundColor = null;
                titleBar.ButtonPressedBackgroundColor = null;
                return;
            }

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = isDark
                ? Windows.UI.Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0xB8, 0x10, 0x10, 0x10);
            titleBar.ButtonHoverBackgroundColor = isDark
                ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = isDark
                ? Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        }
        catch
        {
        }
    }

    private static void TryClearWin10XamlCaptionResources()
    {
        try
        {
            ResourceDictionary? res = Application.Current?.Resources;
            if (res is null)
            {
                return;
            }

            res.Remove("WindowCaptionForeground");
            res.Remove("WindowCaptionForegroundDisabled");
            res.Remove("WindowCaptionBackground");
            res.Remove("WindowCaptionBackgroundDisabled");
            res.Remove("WindowCaptionButtonBackgroundPointerOver");
            res.Remove("WindowCaptionButtonBackgroundPressed");
            res.Remove("WindowCaptionButtonStrokePointerOver");
            res.Remove("WindowCaptionButtonStrokePressed");
        }
        catch
        {
        }
    }

    private static void TryUpdateWin10XamlCaptionResources(bool isDark)
    {
        try
        {
            ResourceDictionary? res = Application.Current?.Resources;
            if (res is null)
            {
                return;
            }

            Windows.UI.Color fg = isDark ? Colors.White : Colors.Black;
            Windows.UI.Color fgDisabled = isDark
                ? Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x88, 0x10, 0x10, 0x10);
            Windows.UI.Color hoverBg = isDark
                ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            Windows.UI.Color pressedBg = isDark
                ? Windows.UI.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);

            res["WindowCaptionForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(fg);
            res["WindowCaptionForegroundDisabled"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(fgDisabled);
            res["WindowCaptionBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
            res["WindowCaptionBackgroundDisabled"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
            res["WindowCaptionButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(hoverBg);
            res["WindowCaptionButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(pressedBg);
            res["WindowCaptionButtonStrokePointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(fg);
            res["WindowCaptionButtonStrokePressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(fg);
        }
        catch
        {
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxTrackPoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxTrackInfo
    {
        public MinMaxTrackPoint Reserved;
        public MinMaxTrackPoint MaxSize;
        public MinMaxTrackPoint MaxPosition;
        public MinMaxTrackPoint MinTrackSize;
        public MinMaxTrackPoint MaxTrackSize;
    }

    /// <summary>
    /// Standard <c>WM_GETMINMAXINFO</c> handler for extended-titlebar windows:
    /// delegates to <c>DefSubclassProc</c> first so WinUI/DWM compute multi-monitor
    /// maximize bounds, then clamps the requested minimum track size to the host
    /// monitor's work area so small/high-DPI displays never clip window chrome.
    /// </summary>
    public static IntPtr ApplyMinTrackSizeWithWorkAreaClamp(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        double scale,
        int minWidthDip,
        int minHeightDip)
    {
        IntPtr result = Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
        if (lParam == IntPtr.Zero)
        {
            return result;
        }

        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        int targetMinW = Math.Max(1, (int)Math.Round(minWidthDip * normalizedScale, MidpointRounding.AwayFromZero));
        int targetMinH = Math.Max(1, (int)Math.Round(minHeightDip * normalizedScale, MidpointRounding.AwayFromZero));

        unsafe
        {
            IntPtr monitor = Win32Helper.MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new Win32Helper.MONITORINFO { cbSize = sizeof(Win32Helper.MONITORINFO) };
            if (Win32Helper.GetMonitorInfo(monitor, ref info))
            {
                int workW = Math.Max(320, info.rcWork.Right - info.rcWork.Left);
                int workH = Math.Max(280, info.rcWork.Bottom - info.rcWork.Top);
                targetMinW = Math.Min(targetMinW, workW);
                targetMinH = Math.Min(targetMinH, workH);
            }

            ref MinMaxTrackInfo minMaxInfo = ref *(MinMaxTrackInfo*)lParam;
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, targetMinW);
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, targetMinH);
        }

        return result;
    }

    /// <summary>
    /// Moves or resizes a window interactively, routing Windows 10 through
    /// <c>SetWindowPos</c> with <c>SWP_NOCOPYBITS | SWP_DEFERERASE</c> during
    /// resize to avoid DWM acrylic flicker and frame lag.
    /// </summary>
    public static void MoveWindowInteractive(
        AppWindow appWindow,
        IntPtr hwnd,
        int x,
        int y)
    {
        if (!WindowsCompatibilityService.IsWindows11OrLater && hwnd != IntPtr.Zero)
        {
            bool moved = Win32Helper.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                x,
                y,
                0,
                0,
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE);
            if (moved)
            {
                return;
            }
        }

        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public static void ResizeWindowInteractive(
        AppWindow appWindow,
        IntPtr hwnd,
        int x,
        int y,
        int width,
        int height)
    {
        if (!WindowsCompatibilityService.IsWindows11OrLater && hwnd != IntPtr.Zero)
        {
            bool resized = Win32Helper.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_NOCOPYBITS |
                Win32Helper.SWP_DEFERERASE);
            if (resized)
            {
                return;
            }
        }

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }
}
