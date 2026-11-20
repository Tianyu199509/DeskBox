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
/// Thrown when a virtual-file COM stream or global-memory payload exceeds the
/// dynamic free-space-aware materialization budget.
/// </summary>
internal sealed class VirtualDropBudgetExceededException : IOException
{
    public long MaxBytes { get; }

    public VirtualDropBudgetExceededException(long maxBytes, string? message = null)
        : base(message ?? $"The dropped stream exceeds the {maxBytes}-byte materialization budget.")
    {
        MaxBytes = maxBytes;
    }
}

/// <summary>
/// Reads the ISequentialStream::Read slot inherited by IStream without creating
/// a built-in COM RCW. The surrounding STGMEDIUM owns the stream pointer until
/// ReleaseStgMedium is called by the drop target.
/// </summary>
internal static unsafe class NativeComStreamReader
{
    private const int QueryInterfaceVtableSlot = 0;
    private const int ReleaseVtableSlot = 2;
    private const int ReadVtableSlot = 3;
    private const int SeekVtableSlot = 5;
    private const uint StreamSeekSet = 0;
    private const int EndOfStream = 1; // S_FALSE
    private const int BufferSize = 81920;
    private static readonly Guid s_iidIStream = new("0000000c-0000-0000-c000-000000000046");

    public static void CopyTo(nint streamPointer, Stream destination)
    {
        CopyTo(streamPointer, destination, long.MaxValue, declaredFileSize: null);
    }

    /// <summary>
    /// Copies with a hard byte budget. A drag source controls the stream
    /// length, so materialization must stop at a sane cap instead of letting
    /// one crafted "file" exhaust memory or disk.
    /// </summary>
    public static void CopyTo(nint streamPointer, Stream destination, long maxBytes)
    {
        CopyTo(streamPointer, destination, maxBytes, declaredFileSize: null);
    }

    public static void CopyTo(
        nint streamPointer,
        Stream destination,
        long maxBytes,
        long? declaredFileSize)
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

        if (maxBytes < 0)
        {
            throw new VirtualDropBudgetExceededException(maxBytes);
        }

        nint* vtable = *(nint**)streamPointer;
        if (vtable == null || vtable[ReadVtableSlot] == 0)
        {
            throw new InvalidOperationException(
                $"The COM stream vtable does not contain slot {ReadVtableSlot}.");
        }

        // Best-effort rewind when the COM object implements IStream (some sources
        // hand out a stream positioned at EOF after filling it). Pure ISequentialStream
        // objects (or 4-slot test doubles) safely skip this step.
        TrySeekToOrigin(streamPointer, vtable);

        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)
            vtable[ReadVtableSlot];
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        long totalBytes = 0;

        try
        {
            fixed (byte* bufferPointer = buffer)
            {
                while (true)
                {
                    uint bytesRead = 0;
                    int result = read(
                        streamPointer,
                        bufferPointer,
                        (uint)BufferSize,
                        &bytesRead);
                    if (result < 0)
                    {
                        Marshal.ThrowExceptionForHR(result);
                    }

                    if (bytesRead > BufferSize)
                    {
                        throw new IOException(
                            "The COM stream returned more bytes than the supplied buffer can hold.");
                    }

                    if (bytesRead > 0)
                    {
                        if (bytesRead > maxBytes - totalBytes)
                        {
                            throw new VirtualDropBudgetExceededException(maxBytes);
                        }

                        totalBytes += bytesRead;
                        destination.Write(buffer, 0, (int)bytesRead);
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (result == EndOfStream)
                    {
                        // Chunked non-EOF streams occasionally return S_FALSE with a
                        // partial read before EOF; keep reading when FD_FILESIZE tells
                        // us more bytes remain, otherwise honor S_FALSE as EOF.
                        if (declaredFileSize is > 0 && totalBytes < declaredFileSize.Value)
                        {
                            continue;
                        }

                        break;
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        if (declaredFileSize is > 0 && totalBytes < declaredFileSize.Value)
        {
            throw new IOException(
                $"The dropped stream ended prematurely ({totalBytes} of {declaredFileSize.Value} declared bytes).");
        }
    }

    private static void TrySeekToOrigin(nint streamPointer, nint* vtable)
    {
        if (vtable[QueryInterfaceVtableSlot] == 0)
        {
            return;
        }

        nint iStream = 0;
        try
        {
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
                vtable[QueryInterfaceVtableSlot];
            Guid iid = s_iidIStream;
            if (queryInterface(streamPointer, &iid, &iStream) < 0 || iStream == 0)
            {
                return;
            }

            nint* iStreamVtable = *(nint**)iStream;
            if (iStreamVtable != null && iStreamVtable[SeekVtableSlot] != 0)
            {
                var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)
                    iStreamVtable[SeekVtableSlot];
                ulong newPos = 0;
                _ = seek(iStream, 0L, StreamSeekSet, &newPos);
            }
        }
        catch
        {
            // Forward-only or non-seekable streams may reject Seek; proceed from current position.
        }
        finally
        {
            if (iStream != 0)
            {
                nint* iStreamVtable = *(nint**)iStream;
                if (iStreamVtable != null && iStreamVtable[ReleaseVtableSlot] != 0)
                {
                    var release = (delegate* unmanaged[Stdcall]<nint, uint>)
                        iStreamVtable[ReleaseVtableSlot];
                    _ = release(iStream);
                }
            }
        }
    }
}
