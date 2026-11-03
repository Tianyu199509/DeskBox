using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal enum DropTargetWindowKind
{
    Unknown = 0,
    ExplorerFileView = 1,
    DeskBoxOwnWindow = 2,
    NonExplorerWindow = 3,
}

internal readonly record struct DropReleaseSnapshot(
    DropTargetWindowKind TargetKind,
    bool EscapePressed,
    Win32Helper.POINT CursorPoint,
    bool IsDesktopShellView = false);

/// <summary>
/// Win32 platform primitives for the global bottom-center no-activate desktop
/// toast pill HUD (<see cref="DeskBox.Views.DesktopToastHudWindow"/>) and
/// post-drop target window classification.
/// </summary>
internal static unsafe class DesktopToastHudInterop
{
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmMouseActivate = 0x0021;
    internal const uint WmNcCalcSize = 0x0083;
    internal const uint WmNcDestroy = 0x0082;
    internal const int MaNoActivate = 3;
    internal const int SwShowNoActivate = 4;
    internal const int DwmwaCloak = 13;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public Win32Helper.POINT Reserved;
        public Win32Helper.POINT MaxSize;
        public Win32Helper.POINT MaxPosition;
        public Win32Helper.POINT MinTrackSize;
        public Win32Helper.POINT MaxTrackSize;
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const int VkEscape = 0x1B;
    private const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr WindowFromPoint(Win32Helper.POINT point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(Win32Helper.POINT pt, uint dwFlags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetDpiForMonitor(
        IntPtr hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    internal static DropReleaseSnapshot SampleDropReleaseState()
    {
        bool escapePressed = (GetAsyncKeyState(VkEscape) & unchecked((short)0x8000)) != 0;
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            return new DropReleaseSnapshot(DropTargetWindowKind.Unknown, escapePressed, default, IsDesktopShellView: false);
        }

        IntPtr hitHwnd = WindowFromPoint(pt);
        (DropTargetWindowKind kind, bool isDesktopShellView) = ClassifyWindowDetailed(hitHwnd);
        return new DropReleaseSnapshot(kind, escapePressed, pt, isDesktopShellView);
    }

    internal static DropTargetWindowKind ClassifyWindow(IntPtr hwnd) =>
        ClassifyWindowDetailed(hwnd).Kind;

    internal static (DropTargetWindowKind Kind, bool IsDesktopShellView) ClassifyWindowDetailed(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return (DropTargetWindowKind.Unknown, false);
        }

        if (Win32Helper.IsWindowInCurrentProcess(hwnd))
        {
            return (DropTargetWindowKind.DeskBoxOwnWindow, false);
        }

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return (DropTargetWindowKind.Unknown, false);
        }

        bool isExplorerProcess = IsExplorerProcessId(pid);
        if (!isExplorerProcess)
        {
            return (DropTargetWindowKind.NonExplorerWindow, false);
        }

        IntPtr rootHwnd = GetAncestor(hwnd, GaRoot);
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hwnd;
        }

        string rootClass = ReadWindowClassName(rootHwnd);
        bool isDesktopShellView = IsDesktopRootWindowClass(rootClass);

        // Inside explorer.exe, distinguish Desktop / Folder file views (and
        // Explorer's native #32768 drop confirmation menu) from the taskbar /
        // tray so hovering a taskbar button is never treated as a folder drop.
        IntPtr current = hwnd;
        for (int depth = 0; depth < 8 && current != IntPtr.Zero; depth++)
        {
            string className = ReadWindowClassName(current);
            if (IsExplorerTaskbarOrTrayClass(className))
            {
                return (DropTargetWindowKind.Unknown, false);
            }

            if (IsExplorerFileViewOrMenuClass(className))
            {
                return (DropTargetWindowKind.ExplorerFileView, isDesktopShellView);
            }

            current = GetParent(current);
        }

        if (rootHwnd != hwnd)
        {
            if (IsExplorerTaskbarOrTrayClass(rootClass))
            {
                return (DropTargetWindowKind.Unknown, false);
            }

            if (IsExplorerFileViewOrMenuClass(rootClass))
            {
                return (DropTargetWindowKind.ExplorerFileView, isDesktopShellView);
            }
        }

        return (DropTargetWindowKind.Unknown, false);
    }

    internal static bool IsDesktopRootWindowClass(string className) =>
        className.Equals("Progman", StringComparison.Ordinal) ||
        className.Equals("WorkerW", StringComparison.Ordinal);

    internal static bool IsExplorerTaskbarOrTrayClass(string className) =>
        className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
        className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
        className.Equals("NotifyIconOverflowWindow", StringComparison.Ordinal) ||
        className.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.Ordinal);

    internal static bool IsExplorerFileViewOrMenuClass(string className) =>
        className.Equals("Progman", StringComparison.Ordinal) ||
        className.Equals("WorkerW", StringComparison.Ordinal) ||
        className.Equals("CabinetWClass", StringComparison.Ordinal) ||
        className.Equals("ExploreWClass", StringComparison.Ordinal) ||
        className.Equals("SHELLDLL_DefView", StringComparison.Ordinal) ||
        className.Equals("DirectUIHWND", StringComparison.Ordinal) ||
        className.Equals("SysListView32", StringComparison.Ordinal) ||
        className.Equals("#32768", StringComparison.Ordinal);

    internal static void GetCursorMonitorWorkArea(
        out MonitorAndBackdropInterop.NativeRect workArea,
        out double rasterizationScale)
    {
        rasterizationScale = 1.0;
        workArea = new MonitorAndBackdropInterop.NativeRect
        {
            Left = 0,
            Top = 0,
            Right = 1920,
            Bottom = 1040,
        };

        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT pt))
        {
            pt = new Win32Helper.POINT { X = 960, Y = 540 };
        }

        IntPtr hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (hMonitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MonitorAndBackdropInterop.NativeMonitorInfo
        {
            CbSize = (uint)sizeof(MonitorAndBackdropInterop.NativeMonitorInfo),
        };
        if (MonitorAndBackdropInterop.GetMonitorInfoW(hMonitor, &info) != 0 &&
            info.Work.Right > info.Work.Left &&
            info.Work.Bottom > info.Work.Top)
        {
            workArea = info.Work;
        }

        try
        {
            if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                rasterizationScale = dpiX / 96.0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            rasterizationScale = 1.0;
        }
    }

    internal static void ApplyNoActivateHudWindowStyles(IntPtr hwnd, bool useNativeFlyoutFrame)
    {
        EnsureNoActivateExtendedStyle(hwnd);

        int style = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_STYLE);
        if (useNativeFlyoutFrame)
        {
            style &= ~(Win32Helper.WS_CAPTION |
                Win32Helper.WS_DLGFRAME |
                Win32Helper.WS_THICKFRAME);
            style |= Win32Helper.WS_BORDER;
        }
        else
        {
            style &= ~(Win32Helper.WS_CAPTION |
                Win32Helper.WS_BORDER |
                Win32Helper.WS_DLGFRAME |
                Win32Helper.WS_THICKFRAME);
        }

        _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_STYLE, style);

        _ = Win32Helper.SetWindowPos(
            hwnd,
            Win32Helper.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);

        if (useNativeFlyoutFrame)
        {
            ApplyNativeFlyoutDwmFrame(hwnd);
        }
        else
        {
            Win32Helper.DisableWindowFrameShadow(hwnd);
        }

        SetCloaked(hwnd, cloaked: true);
    }

    internal static void EnsureNoActivateExtendedStyle(IntPtr hwnd)
    {
        int extendedStyle = Win32Helper.GetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE);
        int desired = extendedStyle |
            Win32Helper.WS_EX_TOOLWINDOW |
            Win32Helper.WS_EX_TOPMOST |
            Win32Helper.WS_EX_NOACTIVATE;
        if (desired != extendedStyle)
        {
            _ = Win32Helper.SetWindowLong(hwnd, Win32Helper.GWL_EXSTYLE, desired);
        }
    }

    internal static void ApplyNativeFlyoutDwmFrame(IntPtr hwnd)
    {
        int ncPolicy = Win32Helper.DWMNCRP_ENABLED;
        Win32Helper.TrySetDwmWindowAttribute(hwnd, Win32Helper.DWMWA_NCRENDERING_POLICY, ref ncPolicy);

        int cornerPreference = Win32Helper.DWMWCP_ROUND;
        Win32Helper.TrySetDwmWindowAttribute(
            hwnd,
            Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference);

        Win32Helper.SetWindowBorderColor(hwnd, DwmColorDefault);
        Win32Helper.ApplyFullWindowFrame(hwnd);
    }

    internal static void RelaxMinTrackSize(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return;
        }

        NativeMinMaxInfo* info = (NativeMinMaxInfo*)lParam;
        info->MinTrackSize.X = 1;
        info->MinTrackSize.Y = 1;
    }

    internal static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Win32Helper.TrySetDwmWindowAttribute(hwnd, DwmwaCloak, ref value);
    }

    internal static HashSet<string> ReadDismissedHintsFile(string filePath)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(filePath))
            {
                foreach (string rawLine in File.ReadAllLines(filePath, System.Text.Encoding.UTF8))
                {
                    string trimmed = rawLine.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                    {
                        loaded.Add(trimmed);
                    }
                }
            }
        }
        catch
        {
        }

        return loaded;
    }

    internal static void WriteDismissedHintsFile(
        string dataDirectory,
        string fileName,
        IEnumerable<string> orderedIds)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            string targetPath = Path.Combine(dataDirectory, fileName);
            string tempPath = Path.Combine(
                dataDirectory,
                $"{fileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(tempPath, orderedIds, System.Text.Encoding.UTF8);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DragHud] Persist dismissed hints failed: {ex.Message}");
        }
    }

    internal static void DeleteDismissedHintsFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    internal static (bool Exists, long LastWriteUtcTicks, long Length) GetDismissedHintsFileStamp(
        string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return (false, 0, 0);
            }

            return (true, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    private static bool IsExplorerProcessId(uint pid)
    {
        if (pid == 0)
        {
            return false;
        }

        IntPtr shellHwnd = GetShellWindow();
        if (shellHwnd != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(shellHwnd, out uint shellPid);
            if (shellPid != 0 && shellPid == pid)
            {
                return true;
            }
        }

        string processName = ProcessImageNameResolver.GetProcessName(pid);
        return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(processName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWindowClassName(IntPtr hwnd)
    {
        char* buffer = stackalloc char[128];
        int length = MonitorAndBackdropInterop.GetClassNameW(hwnd, buffer, 128);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
