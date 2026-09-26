using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Platform;

/// <summary>
/// ole32/user32/kernel32/shell32 entry points used by the OLE drop-target
/// bridge that used to declare them privately inside
/// <see cref="NativeDropTarget"/>. Signatures moved verbatim.
/// </summary>
internal static partial class OleDropTargetNativeMethods
{
    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref NativeStorageMedium medium);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr value);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint DragQueryFile(IntPtr hDrop, uint fileIndex, System.Text.StringBuilder? fileName, uint bufferSize);

    [DllImport("shell32.dll")]
    internal static extern IntPtr ILCombine(IntPtr parent, IntPtr child);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(IntPtr itemIdList);

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetNameFromIDList(
        IntPtr itemIdList,
        uint displayNameType,
        out IntPtr displayName);
}
