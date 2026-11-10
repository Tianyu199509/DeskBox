using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFormatEtc
{
    public ushort ClipboardFormat;
    public nint TargetDevice;
    public uint Aspect;
    public int Index;
    public uint MediumType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeStorageMedium
{
    public uint MediumType;
    public nint Content;
    public nint ReleaseUnknown;
}

/// <summary>
/// Synchronously borrows the IDataObject pointer supplied to an IDropTarget
/// callback. The OLE caller owns the pointer for the duration of the callback,
/// so this boundary must not create or retain a runtime callable wrapper.
/// </summary>
internal readonly unsafe struct NativeOleDataObject
{
    private const int GetDataVtableSlot = 3;
    private const int QueryGetDataVtableSlot = 5;
    private const int SetDataVtableSlot = 7;
    private const int EnumFormatEtcVtableSlot = 8;

    private readonly nint _pointer;

    public NativeOleDataObject(nint pointer)
    {
        if (pointer == 0)
        {
            throw new ArgumentException("The OLE data-object pointer is null.", nameof(pointer));
        }

        _pointer = pointer;
    }

    public int GetData(ref NativeFormatEtc format, out NativeStorageMedium medium)
    {
        var getData = (delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int>)GetVtableEntry(GetDataVtableSlot);

        NativeStorageMedium localMedium = default;
        int result;
        fixed (NativeFormatEtc* formatPointer = &format)
        {
            result = getData(_pointer, formatPointer, &localMedium);
        }

        medium = localMedium;
        return result;
    }

    public int QueryGetData(ref NativeFormatEtc format)
    {
        var queryGetData = (delegate* unmanaged[Stdcall]<nint, NativeFormatEtc*, int>)
            GetVtableEntry(QueryGetDataVtableSlot);

        fixed (NativeFormatEtc* formatPointer = &format)
        {
            return queryGetData(_pointer, formatPointer);
        }
    }

    public int SetData(
        ref NativeFormatEtc format,
        ref NativeStorageMedium medium,
        bool release)
    {
        var setData = (delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int,
            int>)GetVtableEntry(SetDataVtableSlot);

        fixed (NativeFormatEtc* formatPointer = &format)
        fixed (NativeStorageMedium* mediumPointer = &medium)
        {
            return setData(
                _pointer,
                formatPointer,
                mediumPointer,
                release ? 1 : 0);
        }
    }

    public int EnumFormatEtcRaw(uint direction, nint enumerator)
    {
        var enumFn = (delegate* unmanaged[Stdcall]<nint, uint, nint, int>)
            GetVtableEntry(EnumFormatEtcVtableSlot);
        return enumFn(_pointer, direction, enumerator);
    }

    public HashSet<ushort> EnumerateGetFormatIds()
    {
        nint enumerator = 0;
        int hr = EnumFormatEtcRaw(1u, (nint)(&enumerator));
        if (hr < 0 || enumerator == 0)
        {
            throw new InvalidOperationException($"EnumFormatEtc(DATADIR_GET) failed with 0x{hr:X8}.");
        }

        var result = new HashSet<ushort>();
        try
        {
            var nextFn = (delegate* unmanaged[Stdcall]<nint, uint, NativeFormatEtc*, uint*, int>)
                (*(nint**)enumerator)[3];
            while (true)
            {
                NativeFormatEtc item = default;
                uint fetched = 0;
                int nextHr = nextFn(enumerator, 1u, &item, &fetched);
                if (nextHr < 0 || fetched == 0)
                {
                    break;
                }

                if (item.TargetDevice != 0)
                {
                    DeskBox.Platform.ShellItemDragNativeMethods.CoTaskMemFree(item.TargetDevice);
                }

                result.Add(item.ClipboardFormat);
            }
        }
        finally
        {
            var releaseFn = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)enumerator)[2];
            releaseFn(enumerator);
        }

        return result;
    }

    private nint GetVtableEntry(int slot)
    {
        nint* vtable = *(nint**)_pointer;
        if (vtable == null || vtable[slot] == 0)
        {
            throw new InvalidOperationException(
                $"The OLE data-object vtable does not contain slot {slot}.");
        }

        return vtable[slot];
    }
}

/// <summary>
/// Reads the ISequentialStream::Read slot inherited by IStream without creating
/// a built-in COM RCW. The surrounding STGMEDIUM owns the stream pointer until
/// ReleaseStgMedium is called by the drop target.
/// </summary>
internal static unsafe class NativeComStreamReader
{
    private const int ReadVtableSlot = 3;
    private const int EndOfStream = 1; // S_FALSE
    private const int BufferSize = 81920;

    public static void CopyTo(nint streamPointer, Stream destination)
    {
        CopyTo(streamPointer, destination, long.MaxValue);
    }

    /// <summary>
    /// Copies with a hard byte budget. A drag source controls the stream
    /// length, so materialization must stop at a sane cap instead of letting
    /// one crafted "file" exhaust memory or disk.
    /// </summary>
    public static void CopyTo(nint streamPointer, Stream destination, long maxBytes)
    {
        if (streamPointer == 0)
        {
            throw new ArgumentException("The COM stream pointer is null.", nameof(streamPointer));
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        nint* vtable = *(nint**)streamPointer;
        if (vtable == null || vtable[ReadVtableSlot] == 0)
        {
            throw new InvalidOperationException(
                $"The COM stream vtable does not contain slot {ReadVtableSlot}.");
        }

        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)
            vtable[ReadVtableSlot];
        var buffer = new byte[BufferSize];
        long totalBytes = 0;

        fixed (byte* bufferPointer = buffer)
        {
            while (true)
            {
                uint bytesRead = 0;
                int result = read(
                    streamPointer,
                    bufferPointer,
                    (uint)buffer.Length,
                    &bytesRead);
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }

                if (bytesRead > buffer.Length)
                {
                    throw new IOException(
                        "The COM stream returned more bytes than the supplied buffer can hold.");
                }

                if (bytesRead > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > maxBytes)
                    {
                        throw new IOException(
                            $"The dropped stream exceeds the {maxBytes}-byte materialization budget.");
                    }

                    destination.Write(buffer, 0, (int)bytesRead);
                }

                if (bytesRead == 0 || result == EndOfStream)
                {
                    return;
                }
            }
        }
    }
}
