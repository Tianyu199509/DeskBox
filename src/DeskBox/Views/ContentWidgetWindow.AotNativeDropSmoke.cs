#if DESKBOX_NATIVE_AOT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal unsafe AotNativeDropCallbackResult InvokeAotNativeHDropCallbacks(
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        uint keyState,
        bool leaveWithoutDrop,
        bool stopAfterDragOver = false)
    {
        if (GetAotNativeFileDropTarget() is not { IsRegistered: true } target)
        {
            throw new InvalidOperationException(
                "The real ContentWidgetWindow OLE drop target is not registered.");
        }

        string[] normalizedPaths = paths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0 ||
            normalizedPaths.Any(path =>
                !File.Exists(path) && !Directory.Exists(path)))
        {
            throw new InvalidOperationException(
                "The AOT HDROP probe requires existing filesystem paths.");
        }

        using var dataObject = new AotNativeHDropDataObject(normalizedPaths);
        nint interfacePointer = target.AcquireAotSmokeInterfacePointer();
        try
        {
            nint* vtable = *(nint**)interfacePointer;
            if (vtable is null ||
                vtable[3] == 0 ||
                vtable[4] == 0 ||
                vtable[5] == 0 ||
                vtable[6] == 0)
            {
                throw new InvalidOperationException(
                    "The generated IDropTarget CCW is missing a required callback slot.");
            }

            var dragEnter = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[3];
            var dragOver = (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[4];
            var dragLeave = (delegate* unmanaged[Stdcall]<nint, int>)vtable[5];
            var drop = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                NativeDropTarget.POINT,
                uint*,
                int>)vtable[6];

            var point = new NativeDropTarget.POINT
            {
                X = screenX,
                Y = screenY
            };
            uint enterEffect =
                NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
            int enterResult = dragEnter(
                interfacePointer,
                dataObject.Pointer,
                keyState,
                point,
                &enterEffect);
            uint overEffect =
                NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
            int overResult = dragOver(
                interfacePointer,
                keyState,
                point,
                &overEffect);

            int leaveResult = int.MinValue;
            int dropResult = int.MinValue;
            uint completionEffect = NativeDropEffectPolicy.None;
            if (stopAfterDragOver)
            {
                // Keep the OLE session alive long enough for the caller to
                // inspect the native screen-point fallback independently from
                // DragLeave. The caller must subsequently invoke the leave slot.
            }
            else if (leaveWithoutDrop)
            {
                leaveResult = dragLeave(interfacePointer);
            }
            else
            {
                completionEffect =
                    NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move;
                dropResult = drop(
                    interfacePointer,
                    dataObject.Pointer,
                    keyState,
                    point,
                    &completionEffect);
            }

            return new AotNativeDropCallbackResult(
                target.IsRegistered,
                normalizedPaths,
                screenX,
                screenY,
                keyState,
                leaveWithoutDrop,
                stopAfterDragOver,
                enterResult,
                overResult,
                leaveResult,
                dropResult,
                enterEffect,
                overEffect,
                completionEffect);
        }
        finally
        {
            NativeDropTargetComInterop.ReleaseInterfacePointer(interfacePointer);
        }
    }

    internal unsafe int InvokeAotNativeDragLeaveCallback()
    {
        if (GetAotNativeFileDropTarget() is not { IsRegistered: true } target)
        {
            throw new InvalidOperationException(
                "The real ContentWidgetWindow OLE drop target is not registered.");
        }

        nint interfacePointer = target.AcquireAotSmokeInterfacePointer();
        try
        {
            nint* vtable = *(nint**)interfacePointer;
            if (vtable is null || vtable[5] == 0)
            {
                throw new InvalidOperationException(
                    "The generated IDropTarget CCW has no DragLeave slot.");
            }

            var dragLeave = (delegate* unmanaged[Stdcall]<nint, int>)vtable[5];
            return dragLeave(interfacePointer);
        }
        finally
        {
            NativeDropTargetComInterop.ReleaseInterfacePointer(interfacePointer);
        }
    }

    private NativeDropTarget? GetAotNativeFileDropTarget()
    {
        return _nativeFileDropTargets.TryGetValue(HWnd, out NativeDropTarget? root)
            ? root
            : _nativeFileDropTargets.Values.FirstOrDefault(target =>
                target.IsRegistered);
    }
}

internal sealed unsafe class AotNativeHDropDataObject : IDisposable
{
    private const int SlotCount = 12;
    private const ushort FileDropClipboardFormat = 15;
    private const uint ContentAspect = 1;
    private const uint HGlobalMedium = 1;
    private const int Success = 0;
    private const int FormatError = unchecked((int)0x80040064);
    private const int GeneralFailure = unchecked((int)0x80004005);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int NoInterface = unchecked((int)0x80004002);
    private const int AdviseNotSupported = unchecked((int)0x80040003);

    // The product drop target retains the incoming data object
    // (Marshal.AddRef/Marshal.Release for shell drop descriptions and the
    // drag-image helper), and the shell itself queries SetData and
    // QueryInterface on it. A vtable without a complete IUnknown therefore
    // crashes the audited process with a null-pointer call, so every
    // IDataObject slot must carry a valid entry.
    private static readonly Guid IUnknownIdentifier = new(
        0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    private static readonly Guid IDataObjectIdentifier = new(
        0x0000010E, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    private readonly string[] _paths;
    private readonly GCHandle _selfHandle;
    private readonly nint* _vtable;
    private readonly nint* _instance;
    private int _references;
    private bool _disposed;
    private bool _destroyed;

    internal AotNativeHDropDataObject(IReadOnlyList<string> paths)
    {
        _paths = paths.ToArray();
        _vtable = (nint*)NativeMemory.AllocZeroed(
            SlotCount,
            (nuint)sizeof(nint));
        _instance = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        _vtable[0] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            Guid*,
            nint*,
            int>)&QueryInterface;
        _vtable[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        _vtable[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        _vtable[3] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int>)&GetData;
        _vtable[4] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int>)&GetDataHere;
        _vtable[5] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            int>)&QueryGetData;
        _vtable[6] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeFormatEtc*,
            int>)&GetCanonicalFormatEtc;
        _vtable[7] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            NativeStorageMedium*,
            int,
            int>)&SetData;
        _vtable[8] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            uint,
            nint*,
            int>)&EnumFormatEtc;
        _vtable[9] = (nint)(delegate* unmanaged[Stdcall]<
            nint,
            NativeFormatEtc*,
            uint,
            nint,
            uint*,
            int>)&DAdvise;
        _vtable[10] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&DUnadvise;
        _vtable[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&EnumDAdvise;
        _instance[0] = (nint)_vtable;
        _instance[1] = GCHandle.ToIntPtr(_selfHandle);
    }

    internal nint Pointer => (nint)_instance;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(
        nint self,
        Guid* interfaceId,
        nint* interfacePointer)
    {
        if (interfacePointer is not null)
        {
            *interfacePointer = 0;
        }
        if (self == 0 || interfaceId is null || interfacePointer is null)
        {
            return NoInterface;
        }

        if (*interfaceId != IUnknownIdentifier &&
            *interfaceId != IDataObjectIdentifier)
        {
            return NoInterface;
        }

        *interfacePointer = self;
        // Returning the object must take a reference (COM contract), but an
        // UnmanagedCallersOnly entry point cannot be invoked directly, so the
        // increment is inlined from the shared owner lookup.
        AotNativeHDropDataObject? queried = TryGetOwner(self);
        if (queried is not null)
        {
            _ = Interlocked.Increment(ref queried._references);
        }

        return Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self)
    {
        AotNativeHDropDataObject? owner = TryGetOwner(self);
        if (owner is null)
        {
            return 0;
        }

        // The product retains the data object across OLE callbacks (drop
        // descriptions and the shell drag-image helper). The reference count
        // keeps the native memory alive until the product releases it, even
        // after the harness has finished driving its callbacks.
        return (uint)Interlocked.Increment(ref owner._references);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        AotNativeHDropDataObject? owner = TryGetOwner(self);
        if (owner is null)
        {
            return 0;
        }

        int count = Interlocked.Decrement(ref owner._references);
        if (count == 0 && owner._disposed)
        {
            owner.DestroyNativeStorage();
        }

        return (uint)count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDataHere(
        nint self,
        NativeFormatEtc* format,
        NativeStorageMedium* medium)
    {
        return NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetCanonicalFormatEtc(
        nint self,
        NativeFormatEtc* format,
        NativeFormatEtc* canonical)
    {
        return NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetData(
        nint self,
        NativeFormatEtc* format,
        NativeStorageMedium* medium,
        int release)
    {
        if (medium is null)
        {
            return FormatError;
        }

        // Accept and discard shell-owned storage (DROPDESCRIPTION,
        // DragImageBits) exactly like a real Explorer drag source. When the
        // caller transfers ownership, release the storage it handed over.
        if (release != 0)
        {
            NativeStorageMedium owned = *medium;
            Win32Helper.ReleaseStorageMedium(ref owned);
            *medium = owned;
        }

        return Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumFormatEtc(nint self, uint direction, nint* enumerator)
    {
        if (enumerator is not null)
        {
            *enumerator = 0;
        }

        return NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DAdvise(
        nint self,
        NativeFormatEtc* format,
        uint flags,
        nint sink,
        uint* connection)
    {
        if (connection is not null)
        {
            *connection = 0;
        }

        return AdviseNotSupported;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DUnadvise(nint self, uint connection)
    {
        return AdviseNotSupported;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumDAdvise(nint self, nint* enumerator)
    {
        if (enumerator is not null)
        {
            *enumerator = 0;
        }

        return AdviseNotSupported;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryGetData(nint self, NativeFormatEtc* format)
    {
        if (self == 0 || format is null)
        {
            return FormatError;
        }

        return format->ClipboardFormat == FileDropClipboardFormat &&
               format->Aspect == ContentAspect &&
               format->Index == -1 &&
               (format->MediumType & HGlobalMedium) != 0
            ? Success
            : FormatError;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(
        nint self,
        NativeFormatEtc* format,
        NativeStorageMedium* medium)
    {
        if (medium is not null)
        {
            *medium = default;
        }
        if (QueryFormat(self, format) != Success || medium is null)
        {
            return FormatError;
        }

        try
        {
            AotNativeHDropDataObject owner = GetOwner(self);
            nint hDrop = owner.CreateHDrop();
            *medium = new NativeStorageMedium
            {
                MediumType = HGlobalMedium,
                Content = hDrop,
                ReleaseUnknown = 0
            };
            return Success;
        }
        catch
        {
            return GeneralFailure;
        }
    }

    private static int QueryFormat(nint self, NativeFormatEtc* format)
    {
        if (self == 0 || format is null)
        {
            return FormatError;
        }

        return format->ClipboardFormat == FileDropClipboardFormat &&
               format->Aspect == ContentAspect &&
               format->Index == -1 &&
               (format->MediumType & HGlobalMedium) != 0
            ? Success
            : FormatError;
    }

    private static AotNativeHDropDataObject GetOwner(nint self)
    {
        return TryGetOwner(self) ??
            throw new InvalidOperationException(
                "The AOT HDROP data object lost its managed owner.");
    }

    private static AotNativeHDropDataObject? TryGetOwner(nint self)
    {
        if (self == 0)
        {
            return null;
        }

        nint handlePointer = ((nint*)self)[1];
        return GCHandle.FromIntPtr(handlePointer).Target as AotNativeHDropDataObject;
    }

    private void DestroyNativeStorage()
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _selfHandle.Free();
        NativeMemory.Free(_instance);
        NativeMemory.Free(_vtable);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The product may still hold AddRef'd references that it will
        // release in a later OLE callback (for example the explicit
        // DragLeave probe). Defer the native teardown to the final Release.
        if (Volatile.Read(ref _references) == 0)
        {
            DestroyNativeStorage();
        }
    }

    private nint CreateHDrop()
    {
        byte[] pathBytes = Encoding.Unicode.GetBytes(
            string.Join('\0', _paths) + "\0\0");
        nuint totalBytes = checked((nuint)(20 + pathBytes.Length));
        nint hDrop = AotNativeDropWin32.GlobalAlloc(
            AotNativeDropWin32.Moveable | AotNativeDropWin32.ZeroInitialize,
            totalBytes);
        if (hDrop == 0)
        {
            throw new OutOfMemoryException(
                "GlobalAlloc failed for the AOT HDROP payload.");
        }

        nint locked = AotNativeDropWin32.GlobalLock(hDrop);
        if (locked == 0)
        {
            _ = AotNativeDropWin32.GlobalFree(hDrop);
            throw new InvalidOperationException(
                "GlobalLock failed for the AOT HDROP payload.");
        }

        try
        {
            Marshal.WriteInt32(locked, 0, 20);
            Marshal.WriteInt32(locked, 4, 0);
            Marshal.WriteInt32(locked, 8, 0);
            Marshal.WriteInt32(locked, 12, 0);
            Marshal.WriteInt32(locked, 16, 1);
            Marshal.Copy(pathBytes, 0, locked + 20, pathBytes.Length);
        }
        finally
        {
            _ = AotNativeDropWin32.GlobalUnlock(hDrop);
        }

        return hDrop;
    }
}

internal sealed record AotNativeDropCallbackResult(
    bool TargetRegistered,
    IReadOnlyList<string> Paths,
    int ScreenX,
    int ScreenY,
    uint KeyState,
    bool LeaveWithoutDrop,
    bool StoppedAfterDragOver,
    int DragEnterHResult,
    int DragOverHResult,
    int DragLeaveHResult,
    int DropHResult,
    uint DragEnterEffect,
    uint DragOverEffect,
    uint CompletionEffect);
#endif
