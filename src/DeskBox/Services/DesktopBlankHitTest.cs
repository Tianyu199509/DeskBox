// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Distinguishes Explorer's blank desktop surface from desktop icons. The
/// list-view hit test uses memory allocated in Explorer because LVM_HITTEST
/// contains a process-local pointer and is not marshalled by USER32. Class
/// matches are additionally restricted to the shell process: the same window
/// classes also appear inside third-party applications, and their blank areas
/// must not trigger desktop gestures.
/// </summary>
internal static partial class DesktopBlankHitTest
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint ListViewHitTest = 0x1000 + 18;
    private const uint SendMessageAbortIfHung = 0x0002;

    public static bool IsBlankDesktopPoint(Win32Helper.POINT screenPoint)
    {
        IntPtr pointWindow = Win32Helper.WindowFromPoint(screenPoint);
        if (pointWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr listView = IntPtr.Zero;
        IntPtr current = pointWindow;
        while (current != IntPtr.Zero)
        {
            string className = GetWindowClass(current);
            if (string.Equals(className, "SysListView32", StringComparison.Ordinal))
            {
                listView = current;
                break;
            }

            if (string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal))
            {
                listView = Win32Helper.FindWindowEx(
                    current,
                    IntPtr.Zero,
                    "SysListView32",
                    null);
                break;
            }

            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal))
            {
                return IsDesktopHostWindow(current);
            }

            current = Win32Helper.GetParent(current);
        }

        return listView != IntPtr.Zero &&
               IsShellDesktopListView(listView) &&
               IsBlankListViewPoint(listView, screenPoint);
    }

    /// <summary>
    /// Confirms a SysListView32 window belongs to the shell process. The class
    /// name alone is not proof: third-party applications host the same
    /// list-view class, so their blank areas would be mistaken for the
    /// desktop. Process ids are compared instead of process names because a
    /// replacement shell keeps its identity in the window registered via
    /// GetShellWindow. The class-only verdict is retained while Explorer is
    /// restarting and GetShellWindow temporarily returns zero.
    /// </summary>
    internal static bool IsShellDesktopListView(IntPtr listView)
    {
        if (listView == IntPtr.Zero)
        {
            return false;
        }

        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return true;
        }

        Win32Helper.GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        Win32Helper.GetWindowThreadProcessId(listView, out uint listProcessId);
        return shellProcessId == 0 ||
               listProcessId == 0 ||
               shellProcessId == listProcessId;
    }

    /// <summary>
    /// Confirms a Progman/WorkerW window in the ancestor chain belongs to the
    /// shell-owned desktop surface. Windows hosts that surface on a mix of
    /// Progman and top-level WorkerW windows, and wallpaper engines parent
    /// their content into it from a separate process, so the process id of the
    /// root window is compared with the shell window's instead of the hit
    /// window's own: content parented into the desktop stays part of it while
    /// third-party WorkerW windows outside the shell process are rejected.
    /// The class-only verdict is retained while Explorer is restarting and
    /// GetShellWindow temporarily returns zero.
    /// </summary>
    internal static bool IsDesktopHostWindow(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return false;
        }

        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return true;
        }

        IntPtr root = Win32Helper.GetAncestor(window, Win32Helper.GA_ROOT);
        Win32Helper.GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        Win32Helper.GetWindowThreadProcessId(
            root == IntPtr.Zero ? window : root,
            out uint rootProcessId);
        return shellProcessId == 0 ||
               rootProcessId == 0 ||
               shellProcessId == rootProcessId;
    }

    internal static bool IsVerifiedDesktopShellWindow(
        IntPtr windowHandle,
        bool allowDefView = true)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = Win32Helper.GetAncestor(windowHandle, Win32Helper.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            string rootClass = GetWindowClass(root);
            // An Explorer folder window (CabinetWClass/ExploreWClass) or common
            // file dialog (#32770) also hosts SHELLDLL_DefView inside explorer.exe
            // or the host app, and must never be mistaken for the desktop surface.
            if (string.Equals(rootClass, "CabinetWClass", StringComparison.Ordinal) ||
                string.Equals(rootClass, "ExploreWClass", StringComparison.Ordinal) ||
                string.Equals(rootClass, "#32770", StringComparison.Ordinal))
            {
                return false;
            }
        }

        IntPtr current = windowHandle;
        while (current != IntPtr.Zero)
        {
            string className = GetWindowClass(current);
            if (string.Equals(className, "Progman", StringComparison.Ordinal) ||
                string.Equals(className, "WorkerW", StringComparison.Ordinal) ||
                (allowDefView &&
                 string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal)))
            {
                return IsDesktopHostWindow(current);
            }

            current = Win32Helper.GetParent(current);
        }

        if (root != IntPtr.Zero)
        {
            string rootClass = GetWindowClass(root);
            if (string.Equals(rootClass, "Progman", StringComparison.Ordinal) ||
                string.Equals(rootClass, "WorkerW", StringComparison.Ordinal))
            {
                return IsDesktopHostWindow(root);
            }
        }

        return false;
    }

    private static bool IsBlankListViewPoint(
        IntPtr listView,
        Win32Helper.POINT screenPoint)
    {
        // When Windows "Show desktop icons" is unchecked, SHELLDLL_DefView hides
        // its child SysListView32. Every point on the desktop is visually blank.
        if (!Win32Helper.IsWindowVisible(listView))
        {
            return true;
        }

        Win32Helper.POINT clientPoint = screenPoint;
        if (!Win32Helper.ScreenToClient(listView, ref clientPoint))
        {
            return false;
        }

        Win32Helper.GetWindowThreadProcessId(listView, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        IntPtr process = RemoteProcessMemoryNativeMethods.OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite,
            false,
            processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        int structureSize = Marshal.SizeOf<ListViewHitTestInfo>();
        IntPtr localBuffer = IntPtr.Zero;
        IntPtr remoteBuffer = IntPtr.Zero;
        try
        {
            localBuffer = Marshal.AllocHGlobal(structureSize);
            var hitTest = new ListViewHitTestInfo
            {
                Point = clientPoint,
                ItemIndex = -1,
                SubItemIndex = -1,
                GroupIndex = -1
            };
            Marshal.StructureToPtr(hitTest, localBuffer, false);

            remoteBuffer = RemoteProcessMemoryNativeMethods.VirtualAllocEx(
                process,
                IntPtr.Zero,
                (UIntPtr)structureSize,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remoteBuffer == IntPtr.Zero ||
                !RemoteProcessMemoryNativeMethods.WriteProcessMemory(
                    process,
                    remoteBuffer,
                    localBuffer,
                    (UIntPtr)structureSize,
                    out _))
            {
                return false;
            }

            IntPtr delivered = Win32Helper.SendMessageTimeout(
                listView,
                ListViewHitTest,
                UIntPtr.Zero,
                remoteBuffer,
                SendMessageAbortIfHung,
                80,
                out _);
            if (delivered == IntPtr.Zero ||
                !RemoteProcessMemoryNativeMethods.ReadProcessMemory(
                    process,
                    remoteBuffer,
                    localBuffer,
                    (UIntPtr)structureSize,
                    out _))
            {
                return false;
            }

            hitTest = Marshal.PtrToStructure<ListViewHitTestInfo>(localBuffer);
            return hitTest.ItemIndex < 0;
        }
        finally
        {
            if (remoteBuffer != IntPtr.Zero)
            {
                RemoteProcessMemoryNativeMethods.VirtualFreeEx(process, remoteBuffer, UIntPtr.Zero, MemRelease);
            }

            if (localBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(localBuffer);
            }

            RemoteProcessMemoryNativeMethods.CloseHandle(process);
        }
    }

    private static string GetWindowClass(IntPtr windowHandle)
    {
        var className = new StringBuilder(128);
        int length = Win32Helper.GetClassName(
            windowHandle,
            className,
            className.Capacity);
        return length > 0 ? className.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewHitTestInfo
    {
        public Win32Helper.POINT Point;
        public uint Flags;
        public int ItemIndex;
        public int SubItemIndex;
        public int GroupIndex;
    }

    // kernel32 remote-process-memory entry points live in
    // DeskBox.Platform.RemoteProcessMemoryNativeMethods.
}
