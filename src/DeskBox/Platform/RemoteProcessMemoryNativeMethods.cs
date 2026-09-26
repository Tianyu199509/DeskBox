using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// kernel32 remote-process-memory entry points used by the desktop blank
/// hit-test probe that used to declare them privately. Signatures moved
/// verbatim.
/// </summary>
internal static partial class RemoteProcessMemoryNativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr VirtualAllocEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        IntPtr buffer,
        UIntPtr size,
        out UIntPtr written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        IntPtr buffer,
        UIntPtr size,
        out UIntPtr read);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);
}
