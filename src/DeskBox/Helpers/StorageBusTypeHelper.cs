using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves the physical bus behind a volume so drives that report as fixed
/// disks (portable SSDs, UASP enclosures, fixed-mode flash drives) can still
/// be recognized as detachable hardware. <see cref="DriveInfo.DriveType"/>
/// alone cannot make that distinction.
/// </summary>
internal static class StorageBusTypeHelper
{
    internal const int BusType1394 = 0x04;
    internal const int BusTypeUsb = 0x07;
    internal const int BusTypeSd = 0x0C;
    internal const int BusTypeMmc = 0x0D;
    internal const int BusTypeVirtual = 0x0E;
    internal const int BusTypeFileBackedVirtual = 0x0F;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    // STORAGE_PROPERTY_QUERY with PropertyId = StorageDeviceProperty and
    // QueryType = PropertyStandardQuery: two zeroed DWORDs.
    private static readonly byte[] s_storageDeviceQuery = new byte[8];

    // BusType lives at offset 28 inside STORAGE_DEVICE_DESCRIPTOR.
    private const int BusTypeOffset = 28;
    private const int DescriptorHeaderSize = 36;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateVolumeHandle(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint ioControlCode,
        byte[] inBuffer,
        uint inBufferSize,
        byte[] outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Returns the STORAGE_BUS_TYPE of the volume hosting <paramref name="driveRoot"/>,
    /// or null when it cannot be determined (missing drive, network path, denied access).
    /// </summary>
    public static int? TryGetBusType(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return null;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(driveRoot.Trim()));
        }
        catch (Exception)
        {
            return null;
        }

        if (root.Length != 2 || root[1] != ':')
        {
            return null;
        }

        // Query-only access (desiredAccess 0) is enough for property IOCTLs and
        // works without elevation on volumes the user cannot read.
        IntPtr handle = CreateVolumeHandle(
            @"\\.\" + root,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            byte[] descriptor = new byte[4096];
            if (!DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    s_storageDeviceQuery,
                    (uint)s_storageDeviceQuery.Length,
                    descriptor,
                    (uint)descriptor.Length,
                    out uint bytesReturned,
                    IntPtr.Zero))
            {
                return null;
            }

            // Version holds the size of the descriptor header actually returned.
            if (bytesReturned < DescriptorHeaderSize ||
                BitConverter.ToInt32(descriptor, 0) < BusTypeOffset + sizeof(int))
            {
                return null;
            }

            return BitConverter.ToInt32(descriptor, BusTypeOffset);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// True when the bus type belongs to hardware or virtual media that can
    /// disappear without notice (USB, FireWire, memory cards, VHD mounts).
    /// </summary>
    public static bool IsTransientBus(int? busType)
    {
        return busType is BusTypeUsb or
            BusType1394 or
            BusTypeSd or
            BusTypeMmc or
            BusTypeVirtual or
            BusTypeFileBackedVirtual;
    }
}
