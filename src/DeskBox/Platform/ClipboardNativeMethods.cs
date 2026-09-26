using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

/// <summary>
/// user32 clipboard plus kernel32 global-memory and shell32 HDROP entry
/// points used by the Shell clipboard file-list helper that used to declare
/// them privately. Signatures moved verbatim.
/// </summary>
internal static partial class ClipboardNativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr memoryHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr memoryHandle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint DragQueryFile(
        IntPtr dropHandle,
        uint fileIndex,
        StringBuilder? fileName,
        uint fileNameLength);
}
