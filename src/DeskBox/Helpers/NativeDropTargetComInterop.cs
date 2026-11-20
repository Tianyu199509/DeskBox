using DeskBox.Platform;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeskBox.Helpers;

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface INativeDropTarget
{
    [PreserveSig]
    int DragEnter(
        nint dataObject,
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect);

    [PreserveSig]
    int DragOver(
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(
        nint dataObject,
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect);
}

[GeneratedComClass]
internal sealed partial class NativeDropTargetComObject : INativeDropTarget
{
    private readonly NativeDropTarget _owner;

    internal NativeDropTargetComObject(NativeDropTarget owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public int DragEnter(
        nint dataObject,
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect)
    {
        return _owner.OnDragEnter(dataObject, keyState, point, ref effect);
    }

    public int DragOver(
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect)
    {
        return _owner.OnDragOver(keyState, point, ref effect);
    }

    public int DragLeave()
    {
        return _owner.OnDragLeave();
    }

    public int Drop(
        nint dataObject,
        uint keyState,
        NativeDropTarget.POINT point,
        ref uint effect)
    {
        return _owner.OnDrop(dataObject, keyState, point, ref effect);
    }
}

internal static partial class NativeDropTargetComInterop
{
    internal const int DragDropAlreadyRegisteredHResult = unchecked((int)0x80040101);

    internal static void Register(nint hwnd, INativeDropTarget dropTarget)
    {
        ArgumentNullException.ThrowIfNull(dropTarget);

        nint dropTargetPointer = AcquireInterfacePointer(dropTarget);
        try
        {
            Marshal.ThrowExceptionForHR(Ole32NativeMethods.RegisterDragDrop(hwnd, dropTargetPointer));
        }
        finally
        {
            ReleaseInterfacePointer(dropTargetPointer);
        }
    }

    internal static bool TryRegister(
        nint hwnd,
        INativeDropTarget dropTarget,
        bool replaceExisting,
        out bool alreadyRegistered)
    {
        ArgumentNullException.ThrowIfNull(dropTarget);
        alreadyRegistered = false;

        nint dropTargetPointer = AcquireInterfacePointer(dropTarget);
        try
        {
            int hr = Ole32NativeMethods.RegisterDragDrop(hwnd, dropTargetPointer);
            if (hr == 0)
            {
                return true;
            }

            if (hr == DragDropAlreadyRegisteredHResult)
            {
                if (replaceExisting)
                {
                    _ = Ole32NativeMethods.RevokeDragDrop(hwnd);
                    hr = Ole32NativeMethods.RegisterDragDrop(hwnd, dropTargetPointer);
                    if (hr == 0)
                    {
                        return true;
                    }
                }

                alreadyRegistered = true;
                return false;
            }

            Marshal.ThrowExceptionForHR(hr);
            return false;
        }
        finally
        {
            ReleaseInterfacePointer(dropTargetPointer);
        }
    }

    internal static void Revoke(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(Ole32NativeMethods.RevokeDragDrop(hwnd));
    }

    internal static unsafe nint AcquireInterfacePointer(INativeDropTarget dropTarget)
    {
        ArgumentNullException.ThrowIfNull(dropTarget);

        nint pointer = (nint)ComInterfaceMarshaller<INativeDropTarget>.ConvertToUnmanaged(
            dropTarget);
        if (pointer == 0)
        {
            throw new InvalidOperationException(
                "Source-generated IDropTarget marshalling returned a null pointer.");
        }

        return pointer;
    }

    internal static unsafe void ReleaseInterfacePointer(nint pointer)
    {
        if (pointer != 0)
        {
            ComInterfaceMarshaller<INativeDropTarget>.Free((void*)pointer);
        }
    }
}
