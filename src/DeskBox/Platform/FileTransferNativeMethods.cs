using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Platform;

/// <summary>
/// shell32 shell-copy and kernel32 file-handle identity/disposition entry
/// points used by the file transfer engine that used to declare them
/// privately inside <c>FileService</c>. Signatures moved verbatim.
/// </summary>
internal static unsafe partial class FileTransferNativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct ShFileOperation
    {
        public IntPtr WindowHandle;
        public uint Function;
        public char* From;
        public char* To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public char* ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref ShFileOperation fileOperation);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileDispositionInfo
    {
        // FILE_DISPOSITION_INFO carries a 1-byte BOOLEAN, not a 4-byte BOOL:
        // without U1 marshaling the native call sees garbage.
        [MarshalAs(UnmanagedType.U1)]
        public bool Delete;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFileInformationByHandle(
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

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    internal static extern bool SetFileBasicInfoByHandle(
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

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern unsafe bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FileIdInfo lpFileInformation,
        int dwBufferSize);
}
