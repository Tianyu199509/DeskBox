#if DESKBOX_NATIVE_AOT
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// kernel32 global-memory entry points used by the native-drop AOT smoke
/// harness. Moved verbatim from the smoke source; the class stays
/// DESKBOX_NATIVE_AOT-gated exactly like its only caller, and retail builds
/// strip the smoke sources that reference it.
/// </summary>
internal static partial class AotNativeDropWin32
{
    internal const uint Moveable = 0x0002;
    internal const uint ZeroInitialize = 0x0040;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint memory);
}
#endif
