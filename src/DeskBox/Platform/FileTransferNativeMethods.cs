using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// kernel32 file-handle identity and disposition entry points used by the file
/// transfer engine.
/// </summary>
internal static unsafe partial class FileTransferNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInfo
    {
        // FILE_DISPOSITION_INFO carries a 1-byte BOOLEAN, not a 4-byte BOOL.
        // Back with a blittable byte so [LibraryImport] requires no runtime marshalling.
        private byte _delete;

        public bool Delete
        {
            readonly get => _delete != 0;
            set => _delete = value ? (byte)1 : (byte)0;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfo lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileBasicInfoByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileBasicInfo lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FileIdInfo
    {
        // Must mirror the native FILE_ID_INFO exactly: the volume serial is
        // ULONGLONG (8 bytes), not DWORD — an undersized layout makes
        // GetFileInformationByHandleEx reject the buffer outright.
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileAttributeTagByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileAttributeTagInfo lpFileInformation,
        int dwBufferSize);
}
