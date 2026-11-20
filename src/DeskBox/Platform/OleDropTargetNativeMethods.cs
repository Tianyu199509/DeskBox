using System.Runtime.InteropServices;
using DeskBox.Helpers;

namespace DeskBox.Platform;

/// <summary>
/// ole32/user32/kernel32/shell32 entry points used by the OLE drop-target
/// bridge that used to declare them privately inside
/// <see cref="NativeDropTarget"/>.
/// </summary>
internal static partial class OleDropTargetNativeMethods
{
    [LibraryImport("ole32.dll", EntryPoint = "OleInitialize")]
    internal static partial int OleInitialize(IntPtr reserved);

    [LibraryImport("ole32.dll", EntryPoint = "OleUninitialize")]
    internal static partial void OleUninitialize();

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormatW(string lpszFormat);

    [LibraryImport("ole32.dll", EntryPoint = "ReleaseStgMedium")]
    internal static partial void ReleaseStgMedium(ref NativeStorageMedium medium);

    [LibraryImport("ole32.dll", EntryPoint = "CoTaskMemFree")]
    internal static partial void CoTaskMemFree(IntPtr value);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalLock", SetLastError = true)]
    internal static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalSize", SetLastError = true)]
    internal static partial nuint GlobalSize(IntPtr hMem);

    [LibraryImport("shell32.dll", EntryPoint = "ILCombine")]
    internal static partial IntPtr ILCombine(IntPtr parent, IntPtr child);

    [LibraryImport("shell32.dll", EntryPoint = "ILFree")]
    internal static partial void ILFree(IntPtr itemIdList);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetNameFromIDList")]
    internal static partial int SHGetNameFromIDList(
        IntPtr itemIdList,
        uint displayNameType,
        out IntPtr displayName);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromIDList")]
    internal static partial int SHCreateItemFromIDList(
        IntPtr itemIdList,
        in Guid riid,
        out IntPtr shellItem);
}
