// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Distinguishes Explorer's blank desktop surface from desktop icons. The
/// list-view hit test uses memory allocated in Explorer because LVM_HITTEST
/// contains a process-local pointer and is not marshalled by USER32.
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
                return true;
            }

            current = Win32Helper.GetParent(current);
        }

        return listView != IntPtr.Zero &&
               IsBlankListViewPoint(listView, screenPoint);
    }

    private static bool IsBlankListViewPoint(
        IntPtr listView,
        Win32Helper.POINT screenPoint)
    {
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

        IntPtr process = OpenProcess(
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

            remoteBuffer = VirtualAllocEx(
                process,
                IntPtr.Zero,
                (UIntPtr)structureSize,
                MemCommit | MemReserve,
                PageReadWrite);
            if (remoteBuffer == IntPtr.Zero ||
                !WriteProcessMemory(
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
                !ReadProcessMemory(
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
                VirtualFreeEx(process, remoteBuffer, UIntPtr.Zero, MemRelease);
            }

            if (localBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(localBuffer);
            }

            CloseHandle(process);
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        IntPtr buffer,
        UIntPtr size,
        out UIntPtr written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        IntPtr buffer,
        UIntPtr size,
        out UIntPtr read);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
