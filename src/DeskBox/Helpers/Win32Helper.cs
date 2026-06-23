using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// P/Invoke helpers for Win32 window management and shell operations.
/// </summary>
public static partial class Win32Helper
{
    // ────────────────────────────────────────────────────────────────
    //  SetWindowPos – Z-order manipulation
    // ────────────────────────────────────────────────────────────────

    /// <summary>Places the window at the bottom of the Z order.</summary>
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = 1;
    public static readonly IntPtr HWND_TOPMOST = -1;
    public static readonly IntPtr HWND_NOTOPMOST = -2;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWNOACTIVATE = 4;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_ROOT = 2;

    // ────────────────────────────────────────────────────────────────
    //  Extended window styles
    // ────────────────────────────────────────────────────────────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x00000002;

    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_BORDER = 0x00800000;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_DLGFRAME = 0x00400000;
    public const int WS_THICKFRAME = 0x00040000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    public static bool HasMouseButtonActivity()
    {
        return HasAsyncKeyActivity(0x01) ||
               HasAsyncKeyActivity(0x02) ||
               HasAsyncKeyActivity(0x04) ||
               HasAsyncKeyActivity(0x05) ||
               HasAsyncKeyActivity(0x06);
    }

    private static bool HasAsyncKeyActivity(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8001) != 0;
    }

    // ────────────────────────────────────────────────────────────────
    //  Shell operations
    // ────────────────────────────────────────────────────────────────

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ShellExecute(
        IntPtr hwnd,
        string lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    [DllImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [DllImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", EntryPoint = "DefSubclassProc", SetLastError = true)]
    public static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    public delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    public static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    public static bool TryGetNotifyIconRect(IntPtr hWnd, Guid id, out RECT iconLocation)
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = hWnd,
            guidItem = id
        };

        return Shell_NotifyIconGetRect(ref identifier, out iconLocation) == 0 &&
               iconLocation.Right > iconLocation.Left &&
               iconLocation.Bottom > iconLocation.Top;
    }


    // ────────────────────────────────────────────────────────────────
    //  Convenience methods
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Push a window to the bottom of the Z-order so it sits at desktop level.
    /// </summary>
    public static void SetWindowToBottom(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Bring a window above other normal windows without making it topmost.
    /// </summary>
    public static void BringWindowToFront(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Raise a window above normal application windows without leaving it always-on-top.
    /// </summary>
    public static void BringWindowTemporarilyToFront(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Keep a window topmost while a native modal dialog is open.
    /// </summary>
    public static void SetWindowTopMost(IntPtr hWnd)
    {
        SetWindowTopMost(hWnd, showWindow: true);
    }

    /// <summary>
    /// Keep a window topmost, optionally without forcing a hidden owner window visible.
    /// </summary>
    public static void SetWindowTopMost(IntPtr hWnd, bool showWindow)
    {
        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (showWindow)
        {
            flags |= SWP_SHOWWINDOW;
        }

        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            flags);
    }

    /// <summary>
    /// Remove topmost state from a window without changing its size or position.
    /// </summary>
    public static void ClearWindowTopMost(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static IReadOnlyList<IntPtr> FindVisibleDialogWindowsForCurrentProcess(IntPtr excludeHwnd)
    {
        uint currentProcessId = (uint)Environment.ProcessId;
        var windows = new List<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || hWnd == excludeHwnd || !IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId != currentProcessId)
            {
                return true;
            }

            string className = GetWindowClassName(hWnd);
            string title = GetWindowTitle(hWnd);
            if (className.Equals("#32770", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Select Folder", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("选择文件夹", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("浏览文件夹", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        int length = GetWindowText(hWnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var builder = new StringBuilder(256);
        int length = GetClassName(hWnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;
    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    public const int DWMSBT_TABBEDWINDOW = 4;
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_DISABLED = 0;
    public const int ACCENT_ENABLE_GRADIENT = 1;
    public const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    public const int ACCENT_ENABLE_HOSTBACKDROP = 5;
    /// <summary>
    /// Force a window to use dark mode (or light mode) for its system backdrop and borders.
    /// </summary>
    public static void SetWindowTheme(IntPtr hWnd, bool isDark)
    {
        int darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }

    public static void ApplyFullWindowFrame(IntPtr hWnd)
    {
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };

        DwmExtendFrameIntoClientArea(hWnd, ref margins);
    }

    public static void ApplyAccentBlur(IntPtr hWnd, Windows.UI.Color tintColor, double opacity, bool enabled)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);

        int accentState;
        int accentFlags = 2;
        uint gradientColor = ToAbgr(ApplyAlpha(tintColor, opacity));

        if (enabled)
        {
            accentState = opacity <= 0.01
                ? ACCENT_ENABLE_BLURBEHIND
                : ACCENT_ENABLE_ACRYLICBLURBEHIND;
        }
        else if (opacity <= 0.001)
        {
            accentState = ACCENT_DISABLED;
            accentFlags = 0;
            gradientColor = 0;
        }
        else
        {
            accentState = ACCENT_ENABLE_GRADIENT;
        }

        var accent = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = gradientColor,
            AnimationId = 0
        };

        int accentSize = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            bool applied = SetWindowCompositionAttribute(hWnd, ref data);
            int lastError = Marshal.GetLastWin32Error();
            global::DeskBox.App.LogVerbose(
                $"[Composition] hwnd=0x{hWnd.ToInt64():X} enabled={enabled} opacity={opacity:F3} " +
                $"accentState={DescribeAccentState(accent.AccentState)} gradient=0x{accent.GradientColor:X8} " +
                $"applied={applied} lastError={lastError}");
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    public static void DisableAccentPolicy(IntPtr hWnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_DISABLED,
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0
        };

        ApplyAccentPolicy(hWnd, accent, enabled: false, opacity: 0.0);
    }

    private static void ApplyAccentPolicy(IntPtr hWnd, AccentPolicy accent, bool enabled, double opacity)
    {
        int accentSize = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            bool applied = SetWindowCompositionAttribute(hWnd, ref data);
            int lastError = Marshal.GetLastWin32Error();
            global::DeskBox.App.LogVerbose(
                $"[Composition] hwnd=0x{hWnd.ToInt64():X} enabled={enabled} opacity={opacity:F3} " +
                $"accentState={DescribeAccentState(accent.AccentState)} gradient=0x{accent.GradientColor:X8} " +
                $"applied={applied} lastError={lastError}");
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private static Windows.UI.Color ApplyAlpha(Windows.UI.Color color, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255);
        return Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static uint ToAbgr(Windows.UI.Color color)
    {
        return ((uint)color.A << 24) |
               ((uint)color.B << 16) |
               ((uint)color.G << 8) |
               color.R;
    }

    private static string DescribeAccentState(int accentState)
    {
        return accentState switch
        {
            ACCENT_DISABLED => "Disabled",
            ACCENT_ENABLE_GRADIENT => "Gradient",
            ACCENT_ENABLE_TRANSPARENTGRADIENT => "TransparentGradient",
            ACCENT_ENABLE_BLURBEHIND => "BlurBehind",
            ACCENT_ENABLE_ACRYLICBLURBEHIND => "AcrylicBlurBehind",
            ACCENT_ENABLE_HOSTBACKDROP => "HostBackdrop",
            _ => $"Unknown({accentState})"
        };
    }

    /// <summary>
    /// Queries the registry to check if Windows is currently using dark theme for apps.
    /// </summary>
    public static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch { }
        return false;
    }

    public static bool IsKeyPressed(Windows.System.VirtualKey key)
    {
        return (GetKeyState((int)key) & 0x8000) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public static bool TryGetMonitorWorkArea(int x, int y, out RECT monitor, out RECT workArea)
    {
        var point = new POINT
        {
            X = x,
            Y = y
        };
        IntPtr handle = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (handle == IntPtr.Zero)
        {
            monitor = default;
            workArea = default;
            return false;
        }

        var info = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(handle, ref info))
        {
            monitor = default;
            workArea = default;
            return false;
        }

        monitor = info.rcMonitor;
        workArea = info.rcWork;
        return true;
    }

    /// <summary>
    /// Open a file or URL using the default associated application.
    /// </summary>
    public static void OpenFile(string path)
    {
        ShellExecute(IntPtr.Zero, "open", path, null, null, SW_SHOWNORMAL);
    }

    /// <summary>
    /// Open Windows Explorer with the specified file selected.
    /// </summary>
    public static void ShowInExplorer(string path)
    {
        ShellExecute(IntPtr.Zero, "open", "explorer.exe",
            $"/select,\"{path}\"", null, SW_SHOWNORMAL);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    public static partial int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        ref IntPtr dispatcherQueueController);

    private static IntPtr _dispatcherQueueController = IntPtr.Zero;

    /// <summary>
    /// Ensures that a UWP DispatcherQueue is initialized on the current thread.
    /// Required for UWP composition API usage (e.g. transparent backdrops).
    /// </summary>
    public static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null)
        {
            return;
        }

        if (_dispatcherQueueController == IntPtr.Zero)
        {
            DispatcherQueueOptions options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                threadType = 2, // DQTYPE_THREAD_CURRENT
                apartmentType = 2 // DQTAT_COM_STA
            };

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }
    }
}
