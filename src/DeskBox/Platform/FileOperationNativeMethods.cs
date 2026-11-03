using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// ole32 COM apartment/creation and shell32 parsing-name entry points used
/// by the IFileOperation transfer engine that used to declare them
/// privately. Signatures moved verbatim.
/// </summary>
internal static partial class FileOperationNativeMethods
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr reserved, uint coInitialize);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(ref Guid classId, IntPtr outerUnknown, uint classContext, ref Guid interfaceId, out IntPtr fileOperation);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IntPtr shellItem);

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string lpDeviceName, char* lpTargetPath, uint ucchMax);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        char* lpszVolumeName,
        uint cchBufferLength);

    /// <summary>
    /// Verifies that a headless recycle operation targets a fixed local volume
    /// with an active Recycle Bin (excluding network shares, removable USB/SD
    /// media, RAM disks, <c>SUBST</c> virtual drives, and volumes configured
    /// with <c>NukeOnDelete=1</c>).
    /// </summary>
    internal static unsafe bool CanHeadlessRecyclePath(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) ||
                root.Length < 2 ||
                root[1] != Path.VolumeSeparatorChar)
            {
                return false;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                return false;
            }

            string deviceName = root[..2];
            Span<char> targetBuffer = stackalloc char[512];
            fixed (char* targetPtr = targetBuffer)
            {
                uint chars = QueryDosDevice(deviceName, targetPtr, (uint)targetBuffer.Length);
                if (chars > 0)
                {
                    ReadOnlySpan<char> target = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(targetPtr);
                    // SUBST drives map to "\??\<folder>" and have no Recycle Bin.
                    if (target.StartsWith(@"\??\", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            if (IsRecycleBinNukeOnDeleteEnabled(root))
            {
                return false;
            }

            var rbInfo = new ShQueryRbInfo
            {
                cbSize = Marshal.SizeOf<ShQueryRbInfo>(),
            };
            return SHQueryRecycleBin(root, ref rbInfo) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe bool IsRecycleBinNukeOnDeleteEnabled(string root)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? bitBucket = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket");
            if (bitBucket is null)
            {
                return false;
            }

            if (bitBucket.GetValue("NukeOnDelete") is int globalNuke && globalNuke != 0)
            {
                return true;
            }

            string mountPoint = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            Span<char> volumeBuffer = stackalloc char[64];
            fixed (char* volumePtr = volumeBuffer)
            {
                if (GetVolumeNameForVolumeMountPoint(mountPoint, volumePtr, (uint)volumeBuffer.Length))
                {
                    string volumeName = new string(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(volumePtr))
                        .TrimEnd(Path.DirectorySeparatorChar);
                    int braceStart = volumeName.IndexOf('{');
                    if (braceStart >= 0)
                    {
                        string guidToken = volumeName[braceStart..];
                        using Microsoft.Win32.RegistryKey? volKey = bitBucket.OpenSubKey($@"Volume\{guidToken}");
                        if (volKey?.GetValue("NukeOnDelete") is int volNuke)
                        {
                            return volNuke != 0;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
