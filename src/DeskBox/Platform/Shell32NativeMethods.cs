using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// shell32 parsing-name and object-properties entry points shared by the
/// shortcut and context-menu helpers that used to declare them privately.
/// </summary>
internal static partial class Shell32NativeMethods
{
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid riid,
        out nint shellItem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SHObjectProperties(
        IntPtr hwnd,
        uint shopObjectType,
        string pszObjectName,
        string? pszPropertyPage);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHParseDisplayName(
        string name,
        IntPtr bindContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);
}
