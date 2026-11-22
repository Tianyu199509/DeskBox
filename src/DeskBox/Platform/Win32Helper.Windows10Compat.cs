using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskBox.Platform;

public static partial class Win32Helper
{
    internal const uint AbmGetTaskbarPos = 0x00000005;
    internal const int AbeLeft = 0;
    internal const int AbeTop = 1;
    internal const int AbeRight = 2;
    internal const int AbeBottom = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [LibraryImport("shell32.dll")]
    private static partial UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>
    /// Reads Windows' taskbar/shell theme (<c>SystemUsesLightTheme</c>), falling
    /// back to <c>AppsUseLightTheme</c> (<see cref="IsSystemDarkMode"/>) only when
    /// the key is absent. Stock Windows 10 defaults to Dark taskbar (0) + Light
    /// apps (1), so tray icon contrast must follow <c>SystemUsesLightTheme</c>.
    /// </summary>
    public static bool IsSystemTaskbarDarkMode()
    {
        if (DeskBox.Services.WindowsCompatibilityService.IsHighContrast &&
            DeskBox.Services.WindowsCompatibilityService.TryGetUiColor(
                Windows.UI.ViewManagement.UIColorType.Background) is { } hcBg)
        {
            double luma = (0.299 * hcBg.R) + (0.587 * hcBg.G) + (0.114 * hcBg.B);
            return luma < 128.0;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? systemVal = key?.GetValue("SystemUsesLightTheme");
            if (systemVal is int sysInt)
            {
                return sysInt == 0;
            }
        }
        catch
        {
        }

        return IsSystemDarkMode();
    }

    /// <summary>
    /// Queries the primary Windows taskbar edge (<c>ABE_LEFT</c>=0, <c>ABE_TOP</c>=1,
    /// <c>ABE_RIGHT</c>=2, <c>ABE_BOTTOM</c>=3) and bounding rect via
    /// <c>SHAppBarMessage(ABM_GETTASKBARPOS)</c>. Works reliably on Windows 10 for
    /// Top/Left/Right/Bottom docking, auto-hide taskbars, and when the tray icon
    /// lives inside <c>NotifyIconOverflowWindow</c>.
    /// </summary>
    public static bool TryGetTaskbarPosition(out int edge, out RECT rect)
    {
        edge = AbeBottom;
        rect = default;
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
            };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == UIntPtr.Zero)
            {
                return false;
            }

            edge = (int)data.uEdge;
            rect = data.rc;
            return edge is AbeLeft or AbeTop or AbeRight or AbeBottom;
        }
        catch
        {
            return false;
        }
    }
}
