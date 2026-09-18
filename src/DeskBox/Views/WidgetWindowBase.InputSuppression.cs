using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private bool _trayHideInputSuppressed;
    private bool _trayHideTransparentStyleAdded;
    private bool _trayHideRootWasHitTestVisible;

    protected bool IsTrayHideInputSuppressed =>
        _trayHideInputSuppressed;

    /// <summary>
    /// Keeps the HWND available to the compositor for the exit animation while
    /// removing it from pointer interaction. The routed-event gate is the
    /// deterministic safety net; WS_EX_TRANSPARENT lets the click continue to
    /// the desktop on Windows versions where the layered WinUI HWND supports it.
    /// </summary>
    protected void SetTrayHideInputSuppressed(bool suppressed)
    {
        if (_trayHideInputSuppressed == suppressed)
        {
            return;
        }

        _trayHideInputSuppressed = suppressed;
        if (suppressed)
        {
            _trayHideRootWasHitTestVisible =
                RootElement.IsHitTestVisible;
            RootElement.IsHitTestVisible = false;

            if (HWnd == IntPtr.Zero)
            {
                return;
            }

            int extendedStyle = Win32Helper.GetWindowLong(
                HWnd,
                Win32Helper.GWL_EXSTYLE);
            _trayHideTransparentStyleAdded =
                (extendedStyle & Win32Helper.WS_EX_TRANSPARENT) == 0;
            if (_trayHideTransparentStyleAdded)
            {
                _ = Win32Helper.SetWindowLong(
                    HWnd,
                    Win32Helper.GWL_EXSTYLE,
                    extendedStyle | Win32Helper.WS_EX_TRANSPARENT);
                RefreshTrayHideInputStyle();
            }

            return;
        }

        RootElement.IsHitTestVisible =
            _trayHideRootWasHitTestVisible;
        if (_trayHideTransparentStyleAdded && HWnd != IntPtr.Zero)
        {
            int extendedStyle = Win32Helper.GetWindowLong(
                HWnd,
                Win32Helper.GWL_EXSTYLE);
            _ = Win32Helper.SetWindowLong(
                HWnd,
                Win32Helper.GWL_EXSTYLE,
                extendedStyle & ~Win32Helper.WS_EX_TRANSPARENT);
            RefreshTrayHideInputStyle();
        }

        _trayHideTransparentStyleAdded = false;
    }

    private void RefreshTrayHideInputStyle()
    {
        _ = Win32Helper.SetWindowPos(
            HWnd,
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
