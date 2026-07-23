using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// P/Invoke helpers for Win32 window management and shell operations.
/// </summary>
public static partial class Win32Helper
{
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);

    public static void TrimCurrentProcessWorkingSet()
    {
        try
        {
            EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
        }
    }

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
    public const uint SWP_NOZORDER = 0x0004;
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

    // ── DeferWindowPos: atomic multi-window position batch ──────────
    // Moving N windows through one HDWP transaction commits all positions
    // to DWM in a single batch, so grouped widgets slide in lockstep
    // instead of staggering per-window SetWindowPos calls.

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr BeginDeferWindowPos(int nNumWindows);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr DeferWindowPos(
        IntPtr hWinPosInfo,
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDeferWindowPos(IntPtr hWinPosInfo);

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
    public static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;
    public const uint SMTO_NORMAL = 0x0000;

    // ────────────────────────────────────────────────────────────────
    //  Extended window styles
    // ────────────────────────────────────────────────────────────────

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x00000002;

    public const int GWL_STYLE = -16;
    public const int GWLP_HWNDPARENT = -8;
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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("kernel32.dll")]
    public static partial void SetLastError(uint dwErrCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint crKey,
        byte bAlpha,
        uint dwFlags);

    public const uint MSGFLT_ALLOW = 1;
    public const uint WM_DROPFILES = 0x0233;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_COPYGLOBALDATA = 0x0049;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(
        IntPtr hWnd,
        uint message,
        uint action,
        IntPtr changeFilterStruct);

    [LibraryImport("shell32.dll")]
    public static partial void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, StringBuilder? fileName, uint bufferSize);

    [LibraryImport("shell32.dll")]
    public static partial void DragFinish(IntPtr hDrop);

    public static void AllowShellDragDropMessages(IntPtr hWnd)
    {
        AllowWindowMessage(hWnd, WM_DROPFILES, "WM_DROPFILES");
        AllowWindowMessage(hWnd, WM_COPYDATA, "WM_COPYDATA");
        AllowWindowMessage(hWnd, WM_COPYGLOBALDATA, "WM_COPYGLOBALDATA");
        DragAcceptFiles(hWnd, true);
    }

    public static IReadOnlyList<string> GetDroppedFilePaths(IntPtr hDrop)
    {
        var paths = new List<string>();
        try
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint index = 0; index < count; index++)
            {
                uint length = DragQueryFile(hDrop, index, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var builder = new StringBuilder((int)length + 1);
                uint copied = DragQueryFile(hDrop, index, builder, (uint)builder.Capacity);
                if (copied > 0)
                {
                    paths.Add(builder.ToString());
                }
            }
        }
        finally
        {
            DragFinish(hDrop);
        }

        return paths;
    }

    private static void AllowWindowMessage(IntPtr hWnd, uint message, string name)
    {
        bool changed = ChangeWindowMessageFilterEx(hWnd, message, MSGFLT_ALLOW, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();
        global::DeskBox.App.LogVerbose(
            $"[WindowMessageFilter] hwnd=0x{hWnd.ToInt64():X} message={name} changed={changed} lastError={error}");
    }

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

    public static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

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
        return IsKeyDown(virtualKey);
    }

    /// <summary>
    /// Returns true if any mouse button was pressed since this thread last
    /// queried that button. Uses the low bit of GetAsyncKeyState, which resets
    /// per calling thread on every query — call periodically from one thread
    /// to detect click edges between polls.
    /// </summary>
    public static bool HasMouseButtonPressSinceLastQuery()
    {
        return HasAsyncKeyPressSinceLastQuery(0x01) ||
               HasAsyncKeyPressSinceLastQuery(0x02) ||
               HasAsyncKeyPressSinceLastQuery(0x04) ||
               HasAsyncKeyPressSinceLastQuery(0x05) ||
               HasAsyncKeyPressSinceLastQuery(0x06);
    }

    private static bool HasAsyncKeyPressSinceLastQuery(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x0001) != 0;
    }

    // ────────────────────────────────────────────────────────────────
    //  Shell operations
    // ────────────────────────────────────────────────────────────────

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;

    // Window sizing / hit-test messages and codes (borderless window drag + resize).
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCDESTROY = 0x0082;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_EXITSIZEMOVE = 0x0232;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    public const int SM_CXSIZEFRAME = 32;
    public const int SM_CYSIZEFRAME = 33;

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static partial IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static partial IntPtr SetWindowsMouseHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ShellExecute(
        IntPtr hwnd,
        string lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo openAsInfo);

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
        bool r1 = SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        bool r2 = SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        App.LogVerbose($"[ZOrder] SetWindowToBottom hwnd=0x{hWnd.ToInt64():X} r1={r1} r2={r2}");
    }

    /// <summary>
    /// Push a window to desktop level without using HWND_BOTTOM.
    /// This prevents the window from being hidden by Win+D while keeping it at desktop level.
    /// </summary>
    public static void SetWindowToDesktopLevel(IntPtr hWnd)
    {
        bool r = SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        App.Log($"[ZOrder] SetWindowToDesktopLevel hwnd=0x{hWnd.ToInt64():X} r={r}");
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

    public static bool IsWindowTopMost(IntPtr hWnd)
    {
        return (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
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

    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_CLOAK = 13;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMNCRP_USEWINDOWSTYLE = 0;
    public const int DWMNCRP_DISABLED = 1;
    public const int DWMNCRP_ENABLED = 2;
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

    /// <summary>
    /// Sets the DWM system border color for a window.
    /// Pass 0xFFFFFFFE for no border, 0xFFFFFFFF for automatic.
    /// </summary>
    public static void SetWindowBorderColor(IntPtr hWnd, int colorRef)
    {
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MonitorInfoPrimary = 0x00000001;

    public readonly record struct MonitorWorkAreaInfo(RECT Monitor, RECT WorkArea, string DeviceName, bool IsPrimary);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private enum MonitorDpiType
    {
        EffectiveDpi = 0
    }

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(
        IntPtr hmonitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

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

    public static double GetDpiScaleForPoint(int x, int y)
    {
        var point = new POINT
        {
            X = x,
            Y = y
        };
        IntPtr handle = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (handle == IntPtr.Zero)
        {
            return 1.0;
        }

        try
        {
            int hr = GetDpiForMonitor(handle, MonitorDpiType.EffectiveDpi, out uint dpiX, out _);
            return hr == 0 && dpiX > 0
                ? dpiX / 96.0
                : 1.0;
        }
        catch (DllNotFoundException)
        {
            return 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    /// <summary>
    /// Gets the DPI scale for a window handle, using the XAML RasterizationScale
    /// when available and falling back to GetDpiForWindow.
    /// </summary>
    public static double GetDpiScaleForWindow(IntPtr hWnd, Microsoft.UI.Xaml.XamlRoot? xamlRoot)
    {
        double xamlScale = xamlRoot?.RasterizationScale ?? 0;
        if (xamlScale > 0)
        {
            return xamlScale;
        }

        try
        {
            uint dpi = GetDpiForWindow(hWnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    public static IReadOnlyList<(RECT Monitor, RECT WorkArea)> GetMonitorWorkAreas()
    {
        return GetMonitorWorkAreaInfos()
            .Select(area => (area.Monitor, area.WorkArea))
            .ToList();
    }

    public static IReadOnlyList<MonitorWorkAreaInfo> GetMonitorWorkAreaInfos()
    {
        var areas = new List<MonitorWorkAreaInfo>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                    szDevice = string.Empty
                };
                if (GetMonitorInfoEx(hMonitor, ref info))
                {
                    areas.Add(new MonitorWorkAreaInfo(
                        info.rcMonitor,
                        info.rcWork,
                        info.szDevice ?? string.Empty,
                        (info.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary));
                }
                else
                {
                    var fallbackInfo = new MONITORINFO
                    {
                        cbSize = Marshal.SizeOf<MONITORINFO>()
                    };
                    if (GetMonitorInfo(hMonitor, ref fallbackInfo))
                    {
                        areas.Add(new MonitorWorkAreaInfo(
                            fallbackInfo.rcMonitor,
                            fallbackInfo.rcWork,
                            string.Empty,
                            (fallbackInfo.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary));
                    }
                }

                return true;
            },
            IntPtr.Zero);

        return areas;
    }

    /// <summary>
    /// Open a file or URL using the default associated application.
    /// </summary>
    /// <remarks>
    /// Launches via <see cref="Process.Start(ProcessStartInfo)"/> with
    /// <see cref="ProcessStartInfo.UseShellExecute"/> = true, which internally calls
    /// ShellExecuteEx — the same modern path Explorer uses. This resolves associations
    /// (including packaged/UWP handlers and per-user UserChoice) the same way
    /// double-clicking in Explorer does, avoiding the silent failures the legacy
    /// ShellExecuteW P/Invoke produced for certain file types (e.g. .md whose handler is
    /// a Store app or whose UserChoice hash is fragile). <paramref name="ownerWindow"/>
    /// is forwarded to the "Open With" fallback so any system dialog has a real parent
    /// and is not hidden behind a topmost widget.
    /// </remarks>
    public static bool OpenFileOrChooseApp(IntPtr ownerWindow, string path)
    {
        App.Log($"[DIAG] OpenFileOrChooseApp enter owner=0x{ownerWindow.ToInt64():X} path='{path}'");
        // Win32 ERROR_NO_ASSOCIATION: "No application is associated with the specified
        // file for this operation." ShellExecuteEx surfaces this (1155) rather than the
        // legacy SE_ERR_NOASSOC (31) that raw ShellExecute returns.
        const int ErrorNoAssociation = 1155;

        string? directory = Path.GetDirectoryName(path);
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "open",
            WorkingDirectory = string.IsNullOrEmpty(directory) ? string.Empty : directory
        };

        // ELECTRON_RUN_AS_NODE=1 makes any Electron-based default handler (MarkText,
        // VS Code, Obsidian, ...) run as a plain Node.js process and crash trying to
        // execute the target file as a script. Explorer doesn't carry this variable, so
        // double-clicking there works; a process launched from developer tooling that
        // sets it (e.g., an Electron-based host shell) would otherwise break opening
        // such files. UseShellExecute=true cannot set a custom env block, so temporarily
        // clear it from this process's environment around the launch and restore it
        // afterwards, so the child behaves like it does from Explorer.
        string? savedElectronRunAsNode = Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
        if (savedElectronRunAsNode is not null)
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        }

        try
        {
            Process.Start(startInfo);
            App.Log($"[DIAG] Process.Start OK path='{path}' electronVarStripped={savedElectronRunAsNode is not null}");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            // No default handler registered (or the UserChoice association is broken).
            // Offer the system "Open With" dialog with the real owner window so the user
            // can pick an app instead of getting a silent no-op.
            App.Log($"[OpenFile] No association for '{path}' (ERROR_NO_ASSOCIATION). Falling back to Open With.");

            var openAsInfo = new OpenAsInfo
            {
                File = path,
                Class = null,
                Flags = OpenAsInfoFlags.AllowRegistration | OpenAsInfoFlags.Execute
            };

            int hResult = SHOpenWithDialog(ownerWindow, ref openAsInfo);
            if (hResult < 0)
            {
                App.Log($"[OpenFile] Open With failed with HRESULT 0x{hResult:X8} for '{path}'");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[OpenFile] Failed to open '{path}': {ex.Message}");
            return false;
        }
        finally
        {
            if (savedElectronRunAsNode is not null)
            {
                Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", savedElectronRunAsNode);
            }
        }
    }

    public static void OpenFile(string path)
    {
        _ = OpenFileOrChooseApp(IntPtr.Zero, path);
    }

    /// <summary>
    /// Open a file or URL using the default associated application, forwarding a real
    /// owner window handle so any system UI (Open With, UAC) is parented correctly
    /// instead of being left behind a topmost widget. Returns false on failure rather
    /// than swallowing it.
    /// </summary>
    public static bool OpenFile(IntPtr ownerWindow, string path)
    {
        return OpenFileOrChooseApp(ownerWindow, path);
    }

    [Flags]
    private enum OpenAsInfoFlags : uint
    {
        AllowRegistration = 0x00000001,
        Execute = 0x00000004
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public OpenAsInfoFlags Flags;
    }

    /// <summary>
    /// Open Windows Explorer with the specified file selected.
    /// </summary>
    public static void ShowInExplorer(string path)
    {
        var result = ShellExecute(IntPtr.Zero, "open", "explorer.exe",
            $"/select,\"{path}\"", null, SW_SHOWNORMAL);

        // ShellExecute returns an error code (<= 32) when it fails.
        if ((long)result <= 32)
        {
            App.Log($"[ShowInExplorer] ShellExecute failed for '{path}', error code={(long)result}");
        }
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
