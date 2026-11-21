using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// kernel32 entry points shared by the process, module, volume, and
/// file-identity call sites that used to declare them privately.
/// </summary>
internal static partial class Kernel32NativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation fileInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    internal static unsafe partial int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        char* packageFullName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentApplicationUserModelId")]
    internal static unsafe partial int GetCurrentApplicationUserModelId(
        ref uint applicationUserModelIdLength,
        char* applicationUserModelId);

    internal static unsafe int ProbeCurrentPackageFullNameLength(out uint length)
    {
        length = 0;
        return GetCurrentPackageFullName(ref length, null);
    }

    internal static unsafe int ProbeCurrentApplicationUserModelIdLength(out uint length)
    {
        length = 0;
        return GetCurrentApplicationUserModelId(ref length, null);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    /// <summary>
    /// Creates exactly one directory level (no intermediate parents) and
    /// fails with ERROR_ALREADY_EXISTS when the path exists — the return
    /// value is the atomic proof of creation ownership that
    /// Directory.CreateDirectory cannot give.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateDirectory(string pathName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathName(
        string fileName,
        Span<char> volumePathName,
        uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        Span<char> volumeName,
        uint bufferLength);

    internal const uint RestartNoCrash = 0x00000001;
    internal const uint RestartNoHang = 0x00000002;
    internal const uint RestartNoPatch = 0x00000004;
    internal const uint RestartNoReboot = 0x00000008;

    [LibraryImport("kernel32.dll", EntryPoint = "RegisterApplicationRestart",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterApplicationRestart(
        string? commandLine,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "UnregisterApplicationRestart")]
    internal static partial int UnregisterApplicationRestart();

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
