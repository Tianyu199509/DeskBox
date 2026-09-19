using DeskBox.Platform;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

/// <summary>
/// Thin raw-COM wrapper around the Shell drag-image manager. Forwarding the
/// IDropTarget callbacks keeps Explorer's source-provided drag image intact
/// instead of asking XAML to synthesize a differently sized preview.
/// </summary>
internal sealed unsafe partial class NativeDropImageManager : IDisposable
{
    private const uint ClsctxInprocServer = 0x1;
    private const int DragEnterVtableSlot = 3;
    private const int DragLeaveVtableSlot = 4;
    private const int DragOverVtableSlot = 5;
    private const int DropVtableSlot = 6;
    private const int ShowVtableSlot = 7;
    private const int ReleaseVtableSlot = 2;

    private static readonly Guid s_dragDropHelperClassId =
        new("4657278A-411B-11D2-839A-00C04FD918D0");
    private static readonly Guid s_dropTargetHelperInterfaceId =
        new("4657278B-411B-11D2-839A-00C04FD918D0");

    private nint _pointer;
    private bool _isDragActive;

    private NativeDropImageManager(nint pointer)
    {
        _pointer = pointer;
    }

    internal bool IsDragActive => _isDragActive;

    internal static NativeDropImageManager? TryCreate()
    {
        int result = Ole32NativeMethods.CoCreateInstance(
            in s_dragDropHelperClassId,
            0,
            ClsctxInprocServer,
            in s_dropTargetHelperInterfaceId,
            out nint pointer);
        return result >= 0 && pointer != 0
            ? new NativeDropImageManager(pointer)
            : null;
    }

    internal void DragEnter(
        nint targetWindow,
        nint dataObject,
        NativeDropTarget.POINT point,
        uint effect)
    {
        if (_pointer == 0 || dataObject == 0)
        {
            return;
        }

        if (_isDragActive)
        {
            DragLeave();
        }

        var callback = (delegate* unmanaged[Stdcall]<
            nint,
            nint,
            nint,
            NativeDropTarget.POINT*,
            uint,
            int>)GetVtableEntry(DragEnterVtableSlot);
        NativeDropTarget.POINT localPoint = point;
        _isDragActive = callback(
            _pointer,
            targetWindow,
            dataObject,
            &localPoint,
            effect) >= 0;
    }

    internal void DragOver(
        NativeDropTarget.POINT point,
        uint effect)
    {
        if (_pointer == 0 || !_isDragActive)
        {
            return;
        }

        var callback = (delegate* unmanaged[Stdcall]<
            nint,
            NativeDropTarget.POINT*,
            uint,
            int>)GetVtableEntry(DragOverVtableSlot);
        NativeDropTarget.POINT localPoint = point;
        _ = callback(_pointer, &localPoint, effect);
    }

    internal void Show(bool visible)
    {
        if (_pointer == 0 || !_isDragActive)
        {
            return;
        }

        var callback = (delegate* unmanaged[Stdcall]<nint, int, int>)
            GetVtableEntry(ShowVtableSlot);
        _ = callback(_pointer, visible ? 1 : 0);
    }

    internal void DragLeave()
    {
        if (_pointer == 0 || !_isDragActive)
        {
            return;
        }

        var callback = (delegate* unmanaged[Stdcall]<nint, int>)
            GetVtableEntry(DragLeaveVtableSlot);
        _ = callback(_pointer);
        _isDragActive = false;
    }

    internal void Drop(
        nint dataObject,
        NativeDropTarget.POINT point,
        uint effect)
    {
        if (_pointer == 0 || !_isDragActive || dataObject == 0)
        {
            return;
        }

        var callback = (delegate* unmanaged[Stdcall]<
            nint,
            nint,
            NativeDropTarget.POINT*,
            uint,
            int>)GetVtableEntry(DropVtableSlot);
        NativeDropTarget.POINT localPoint = point;
        _ = callback(
            _pointer,
            dataObject,
            &localPoint,
            effect);
        _isDragActive = false;
    }

    public void Dispose()
    {
        if (_pointer == 0)
        {
            return;
        }

        DragLeave();
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)
            GetVtableEntry(ReleaseVtableSlot);
        _ = release(_pointer);
        _pointer = 0;
    }

    private nint GetVtableEntry(int slot)
    {
        nint* vtable = *(nint**)_pointer;
        if (vtable == null || vtable[slot] == 0)
        {
            throw new InvalidOperationException(
                $"The IDropTargetHelper vtable does not contain slot {slot}.");
        }

        return vtable[slot];
    }
}
