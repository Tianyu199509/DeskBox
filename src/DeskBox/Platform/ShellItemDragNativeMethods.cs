using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// shell32 PIDL/data-object and ole32 CoTaskMemFree entry points used by the
/// native Shell file drag provider that used to declare them privately.
/// Signatures moved verbatim.
/// </summary>
internal static unsafe partial class ShellItemDragNativeMethods
{
    [LibraryImport(
        "shell32.dll",
        EntryPoint = "SHParseDisplayName",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHParseDisplayName(
        string name,
        nint bindContext,
        out nint itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [LibraryImport("shell32.dll")]
    internal static partial nint ILFindLastID(nint itemIdList);

    [LibraryImport("shell32.dll")]
    internal static partial int SHCreateDataObject(
        nint folderItemIdList,
        uint itemCount,
        nint* childItemIdLists,
        nint innerDataObject,
        Guid* interfaceId,
        out nint dataObject);

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(nint value);
}
