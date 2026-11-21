using System.Buffers;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// user32 clipboard plus kernel32 global-memory and shell32 HDROP entry
/// points used by the Shell clipboard file-list helper that used to declare
/// them privately.
/// </summary>
internal static partial class ClipboardNativeMethods
{
    [LibraryImport("user32.dll", EntryPoint = "OpenClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(IntPtr newOwner);

    [LibraryImport("user32.dll", EntryPoint = "CloseClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", EntryPoint = "EmptyClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    internal static partial IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardData", SetLastError = true)]
    internal static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll", EntryPoint = "IsClipboardFormatAvailable", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormat(string format);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalAlloc", SetLastError = true)]
    internal static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalSize", SetLastError = true)]
    internal static partial nuint GlobalSize(IntPtr memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    internal static partial IntPtr GlobalLock(IntPtr memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr memoryHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalFree", SetLastError = true)]
    internal static partial IntPtr GlobalFree(IntPtr memoryHandle);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    internal static unsafe partial uint DragQueryFileNative(
        IntPtr dropHandle,
        uint fileIndex,
        char* fileName,
        uint fileNameLength);

    internal static unsafe uint QueryDroppedFileCount(IntPtr dropHandle) =>
        DragQueryFileNative(dropHandle, 0xFFFFFFFF, null, 0);

    internal static unsafe string? TryQueryDroppedFilePath(IntPtr dropHandle, uint fileIndex)
    {
        uint length = DragQueryFileNative(dropHandle, fileIndex, null, 0);
        if (length == 0 || length >= int.MaxValue)
        {
            return null;
        }

        int capacity = checked((int)length + 1);
        char[]? rented = null;
        Span<char> buffer = capacity <= 512
            ? stackalloc char[capacity]
            : (rented = ArrayPool<char>.Shared.Rent(capacity)).AsSpan(0, capacity);
        try
        {
            fixed (char* ptr = buffer)
            {
                uint copied = DragQueryFileNative(dropHandle, fileIndex, ptr, (uint)capacity);
                if (copied == 0)
                {
                    return null;
                }

                int actualLen = (int)Math.Min(copied, (uint)length);
                return new string(buffer[..actualLen]);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
