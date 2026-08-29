using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Models;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Helpers;

/// <summary>
/// P/Invoke helpers for Win32 window management and shell operations.
/// </summary>
public static partial class Win32Helper
{
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("dcomp.dll")]
    private static extern int DCompositionBoostCompositorClock(
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr objectHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int SetWindowRgn(
        IntPtr hWnd,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    /// <summary>
    /// Requests the DirectComposition compositor clock to run at its active
    /// cadence while a short interactive animation is in progress. This is a
    /// best-effort Windows capability and safely falls back on older systems.
    /// </summary>
    public static bool TrySetCompositorClockBoost(bool enabled)
    {
        try
        {
            return DCompositionBoostCompositorClock(enabled) >= 0;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Requests 1ms timer resolution only while an interactive Win10 animation
    /// owns a lease. Calls are reference-counted by the coordinator.
    /// </summary>
    public static bool TrySetHighResolutionTimer(bool enabled)
    {
        try
        {
            return (enabled ? TimeBeginPeriod(1) : TimeEndPeriod(1)) == 0;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileForFinalPath(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// Resolves a file or directory through the Windows object manager. This
    /// collapses junctions, symbolic links, SUBST drives and UNC aliases into
    /// the same final volume identity where Windows can open the path.
    /// </summary>
    public static bool TryGetFinalPath(string path, out string finalPath)
    {
        finalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = CreateFileForFinalPath(
                path,
                dwDesiredAccess: 0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var buffer = new StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                return false;
            }

            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            }

            if (length == 0)
            {
                return false;
            }

            finalPath = NormalizeFinalPath(buffer.ToString());
            return !string.IsNullOrWhiteSpace(finalPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\" + path[7..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);

    public static bool TrimCurrentProcessWorkingSet()
    {
        try
        {
            return EmptyWorkingSet(GetCurrentProcess());
        }
        catch
        {
            return false;
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
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOCOPYBITS = 0x0100;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_DEFERERASE = 0x2000;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumChildWindows(
        IntPtr hWndParent,
        EnumWindowsProc lpEnumFunc,
        IntPtr lParam);

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
    public const uint GW_OWNER = 4;

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

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
    internal static partial short GetAsyncKeyState(int vKey);

    public static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    /// <summary>
    /// Synthesizes key-up events for all modifier keys (Alt, Ctrl, Shift,
    /// including left/right variants).  This clears any "stuck" modifier
    /// state that can occur in RDP sessions where the modifier key-up
    /// event is lost or delayed by the remote desktop protocol.
    ///
    /// When RegisterHotKey registers e.g. Alt+D, a stuck Alt state causes
    /// every subsequent D press to be intercepted as Alt+D, making the D
    /// key appear dead.  Calling this right after the hotkey fires
    /// prevents that.
    /// </summary>
    public static void ReleaseAllModifiers()
    {
        // VK codes for all modifier keys
        int[] modifierVks =
        [
            0x10, // VK_SHIFT
            0x11, // VK_CONTROL
            0x12, // VK_MENU (Alt)
            0xA0, // VK_LSHIFT
            0xA1, // VK_RSHIFT
            0xA2, // VK_LCONTROL
            0xA3, // VK_RCONTROL
            0xA4, // VK_LMENU
            0xA5, // VK_RMENU
        ];

        foreach (int vk in modifierVks)
        {
            if (IsKeyDown(vk))
            {
                _ = TrySendKeyboardEvent((ushort)vk, KEYEVENTF_KEYUP, UIntPtr.Zero, out _);
            }
        }
    }

    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Sends a short, tagged keyboard press and verifies that both input
    /// records reached the Win32 input queue. The tag lets a low-level hook
    /// ignore its own synthetic events.
    /// </summary>
    public static unsafe bool TrySendTaggedKeyPress(
        ushort virtualKey,
        IntPtr extraInfo,
        out int errorCode)
    {
        var tag = new UIntPtr(unchecked((ulong)extraInfo.ToInt64()));
        INPUT* inputs = stackalloc INPUT[2];
        inputs[0] = CreateKeyboardInput(virtualKey, 0, tag);
        inputs[1] = CreateKeyboardInput(virtualKey, KEYEVENTF_KEYUP, tag);

        uint sent = SendInput(2, inputs, sizeof(INPUT));
        if (sent == 2)
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        if (errorCode == 0)
        {
            errorCode = 31; // ERROR_GEN_FAILURE
        }

        // If only the key-down record was accepted, make a best-effort key-up
        // call so a partial SendInput result cannot leave the synthetic key
        // logically pressed.
        if (sent == 1)
        {
            INPUT release = CreateKeyboardInput(virtualKey, KEYEVENTF_KEYUP, tag);
            _ = SendInput(1, &release, sizeof(INPUT));
        }

        return false;
    }

    private static unsafe bool TrySendKeyboardEvent(
        ushort virtualKey,
        uint flags,
        UIntPtr extraInfo,
        out int errorCode)
    {
        INPUT input = CreateKeyboardInput(virtualKey, flags, extraInfo);
        uint sent = SendInput(1, &input, sizeof(INPUT));
        if (sent == 1)
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        if (errorCode == 0)
        {
            errorCode = 31; // ERROR_GEN_FAILURE
        }

        return false;
    }

    private static INPUT CreateKeyboardInput(
        ushort virtualKey,
        uint flags,
        UIntPtr extraInfo)
    {
        return new INPUT
        {
            Type = 1, // INPUT_KEYBOARD
            Data = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = extraInfo
                }
            }
        };
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

    /// <summary>
    /// Returns true while any mouse button is physically held down. Uses the
    /// high bit of GetAsyncKeyState — the global physical state, which stays
    /// accurate regardless of which process received the click (unlike the
    /// low "since last query" bit). Sample this periodically and watch for an
    /// up→down transition to reliably detect clicks delivered to foreign windows.
    /// </summary>
    public static bool IsAnyMouseButtonDown()
    {
        return IsKeyDown(0x01) ||
               IsKeyDown(0x02) ||
               IsKeyDown(0x04) ||
               IsKeyDown(0x05) ||
               IsKeyDown(0x06);
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
    public const uint LLKHF_INJECTED = 0x00000010;
    public const uint WM_QUIT = 0x0012;
    public const uint PM_NOREMOVE = 0x0000;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_XBUTTONDOWN = 0x020B;

    // Window sizing / hit-test messages and codes (borderless window drag + resize).
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCDESTROY = 0x0082;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDBLCLK = 0x00A3;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int MA_ACTIVATE = 1;
    public const int MA_NOACTIVATE = 3;

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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

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
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(
        uint inputCount,
        INPUT* inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
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

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial IntPtr DispatchMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(
        out MSG lpMsg,
        IntPtr hWnd,
        uint wMsgFilterMin,
        uint wMsgFilterMax,
        uint wRemoveMsg);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(
        uint idThread,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

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
        BringWindowTemporarilyToFront(hWnd, showWindow: true);
    }

    /// <summary>
    /// Raise a window without optionally issuing another show request. Initial
    /// presentation already shows the HWND while it is DWM-cloaked, so its
    /// Z-order transaction must not generate two extra visible SHOWWINDOW
    /// transitions before the cloak is removed.
    /// </summary>
    public static void BringWindowTemporarilyToFront(
        IntPtr hWnd,
        bool showWindow)
    {
        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        if (showWindow)
        {
            flags |= SWP_SHOWWINDOW;
        }

        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            flags);
        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            flags);
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
        return FindVisibleDialogWindowsForCurrentProcess(
            excludeHwnd,
            requiredOwnerHwnd: IntPtr.Zero);
    }

    public static IReadOnlyList<IntPtr> FindVisibleDialogWindowsForCurrentProcess(
        IntPtr excludeHwnd,
        IntPtr requiredOwnerHwnd)
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

            if (requiredOwnerHwnd != IntPtr.Zero &&
                !IsWindowOwnedBy(hWnd, requiredOwnerHwnd))
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

    private static bool IsWindowOwnedBy(IntPtr windowHandle, IntPtr requiredOwnerHwnd)
    {
        IntPtr current = GetWindow(windowHandle, GW_OWNER);
        for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
        {
            if (current == requiredOwnerHwnd)
            {
                return true;
            }

            current = GetWindow(current, GW_OWNER);
        }

        return false;
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

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    /// <summary>
    /// Blocks until the next DWM composition pass completes. Used as the Win10
    /// present-aligned pacing source; returns false when dwmapi is unavailable
    /// so the caller can fall back to a refresh-derived timer.
    /// </summary>
    public static bool TryDwmFlush()
    {
        try
        {
            return DwmFlush() == 0;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Version-safe DWM attribute entry point.  Attributes 33, 34 and 38 are
    /// Windows 11 additions; silently skip them on the Win10 compatibility
    /// floor instead of relying on an ignored HRESULT at every call site.
    /// </summary>
    public static int TrySetDwmWindowAttribute(IntPtr hwnd, int attr, ref int value)
    {
        if ((attr is DWMWA_BORDER_COLOR or DWMWA_WINDOW_CORNER_PREFERENCE or DWMWA_SYSTEMBACKDROP_TYPE) &&
            !Services.WindowsCompatibilityService.SupportsWin11DwmAttributes)
        {
            return 0;
        }

        try
        {
            return DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
        }
        catch
        {
            return -1;
        }
    }

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
        TrySetDwmWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode);
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
        TrySetDwmWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref colorRef);
    }

    /// <summary>
    /// Pure accent-state selection so interaction-time simplification (keep the
    /// tint, drop the DWM blur) stays unit-testable.
    /// </summary>
    internal static int ResolveAccentState(bool enabled, bool blurEnabled, double opacity)
    {
        if (enabled)
        {
            if (!blurEnabled)
            {
                return ACCENT_ENABLE_TRANSPARENTGRADIENT;
            }

            return opacity <= 0.01
                ? ACCENT_ENABLE_BLURBEHIND
                : ACCENT_ENABLE_ACRYLICBLURBEHIND;
        }

        if (opacity <= 0.001)
        {
            return ACCENT_DISABLED;
        }

        return ACCENT_ENABLE_GRADIENT;
    }

    public static bool ApplyAccentBlur(
        IntPtr hWnd,
        Windows.UI.Color tintColor,
        double opacity,
        bool enabled,
        bool blurEnabled = true)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);

        int accentState = ResolveAccentState(enabled, blurEnabled, opacity);
        int accentFlags = 2;
        uint gradientColor = ToAbgr(ApplyAlpha(tintColor, opacity));
        if (accentState == ACCENT_DISABLED)
        {
            accentFlags = 0;
            gradientColor = 0;
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
            return applied;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    private const uint MonitorInfoPrimary = 0x00000001;

    public readonly record struct MonitorWorkAreaInfo(
        RECT Monitor,
        RECT WorkArea,
        string DeviceName,
        bool IsPrimary,
        double DpiScale);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int EnumCurrentSettings = -1;

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

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

    private static double GetDpiScaleForMonitor(IntPtr hMonitor)
    {
        try
        {
            int hr = GetDpiForMonitor(hMonitor, MonitorDpiType.EffectiveDpi, out uint dpiX, out _);
            return hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
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
    /// Returns the refresh rate of the monitor currently containing the window.
    /// Invalid driver values safely normalize to 60 Hz.
    /// </summary>
    public static int GetDisplayRefreshRateForWindow(IntPtr hWnd)
    {
        try
        {
            IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
            }

            var monitorInfo = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (!GetMonitorInfoEx(monitor, ref monitorInfo) ||
                string.IsNullOrWhiteSpace(monitorInfo.szDevice))
            {
                return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
            }

            var mode = new DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };
            return EnumDisplaySettings(monitorInfo.szDevice, EnumCurrentSettings, ref mode)
                ? WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, mode.dmDisplayFrequency))
                : WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
        }
    }

    /// <summary>
    /// Returns the refresh rate of the primary monitor for windowless pacing
    /// consumers. Invalid driver values safely normalize to 60 Hz.
    /// </summary>
    public static int GetPrimaryDisplayRefreshRate()
    {
        try
        {
            IntPtr monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
            if (monitor == IntPtr.Zero)
            {
                return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
            }

            var monitorInfo = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (!GetMonitorInfoEx(monitor, ref monitorInfo) ||
                string.IsNullOrWhiteSpace(monitorInfo.szDevice))
            {
                return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
            }

            var mode = new DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (short)Marshal.SizeOf<DEVMODE>()
            };
            return EnumDisplaySettings(monitorInfo.szDevice, EnumCurrentSettings, ref mode)
                ? WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, mode.dmDisplayFrequency))
                : WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz;
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
                        (info.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary,
                        GetDpiScaleForMonitor(hMonitor)));
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
                            (fallbackInfo.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary,
                            GetDpiScaleForMonitor(hMonitor)));
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
    /// First delegates to the running Explorer desktop process so launched applications
    /// inherit the current user shell environment rather than DeskBox's potentially stale
    /// startup environment. If Explorer is unavailable, falls back to ShellExecuteEx via
    /// <see cref="Process.Start(ProcessStartInfo)"/>. <paramref name="ownerWindow"/> is
    /// forwarded to the "Open With" fallback so any system dialog has a real parent.
    /// </remarks>
    public static bool OpenFileOrChooseApp(IntPtr ownerWindow, string path)
    {
        // Win32 ERROR_NO_ASSOCIATION: "No application is associated with the specified
        // file for this operation." ShellExecuteEx surfaces this (1155) rather than the
        // legacy SE_ERR_NOASSOC (31) that raw ShellExecute returns.
        const int ErrorNoAssociation = 1155;

        string directory = ResolveShellLaunchDirectory(path);
        if (ExplorerShellLaunchService.TryOpen(
                path,
                directory,
                "open",
                out string? explorerLaunchError))
        {
            return true;
        }

        App.Log(
            $"[OpenFile] Explorer-hosted launch unavailable for '{path}': " +
            $"{explorerLaunchError ?? "unknown error"}. Falling back to local ShellExecuteEx.");

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "open",
            WorkingDirectory = directory
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

    internal static string ResolveShellLaunchDirectory(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return string.Empty;
        }

        return Path.GetDirectoryName(path) ?? string.Empty;
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
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2, // DQTYPE_THREAD_CURRENT
                apartmentType = 2 // DQTAT_COM_STA
            };

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }
    }
}
