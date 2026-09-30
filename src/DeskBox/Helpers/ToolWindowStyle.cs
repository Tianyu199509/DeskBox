using System.ComponentModel;
using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Hides helper HWNDs from the taskbar and Alt+Tab without calling the
/// shell-dependent AppWindow.IsShownInSwitchers API during logon.
/// </summary>
internal static class ToolWindowStyle
{
    private const int AppWindowStyle = 0x00040000;

    internal static int GetExtendedStyle(int existing) =>
        (existing | Win32Helper.WS_EX_TOOLWINDOW) & ~AppWindowStyle;

    internal static void Apply(IntPtr hwnd)
    {
        int current = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
        Win32Helper.SetLastError(0);
        IntPtr previous = Win32Helper.SetWindowLongPtr(
            hwnd, Win32Helper.GWL_EXSTYLE, new IntPtr(GetExtendedStyle(current)));
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0)
        {
            throw new Win32Exception(error);
        }
    }
}
