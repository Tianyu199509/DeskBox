using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// shell32 SHCreateDataObject plus the kernel32 global-memory entry points
/// used by the HDROP data-object builder that used to declare them
/// privately. Signatures moved verbatim.
/// </summary>
internal static unsafe partial class HdropDataObjectNativeMethods
{
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateDataObject")]
    internal static partial int SHCreateDataObject(
        nint parentFolder,
        uint childCount,
        nint childPidls,
        nint bindContext,
        in Guid riid,
        out nint dataObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial void GlobalFree(nint memory);
}
