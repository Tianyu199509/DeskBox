using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Thin raw-COM wrapper around the Shell drag-image manager. Forwarding the
/// IDropTarget callbacks keeps Explorer's source-provided drag image intact
/// instead of asking XAML to synthesize a differently sized preview.
/// Shared across <see cref="NativeDropTarget"/> instances on the UI STA and
/// released as soon as no drag visual session remains active.
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

    private static readonly object s_gate = new();
    private static readonly HashSet<NativeDropTarget> s_leasedTargets = [];
    private static NativeDropImageManager? s_shared;
    private static NativeDropTarget? s_activeOwner;

    private nint _pointer;
    private bool _isDragActive;

    private NativeDropImageManager(nint pointer)
    {
        _pointer = pointer;
    }

    internal static bool HasSharedInstance => s_shared is not null;

    internal static bool TryDragEnter(
        NativeDropTarget owner,
        nint targetWindow,
        nint dataObject,
        NativeDropTarget.POINT point,
        uint effect)
    {
        if (dataObject == 0)
        {
            return false;
        }

        lock (s_gate)
        {
            NativeDropImageManager? shared = s_shared ??= TryCreateCore();
            if (shared is null)
            {
                return false;
            }

            if (s_activeOwner is not null &&
                !ReferenceEquals(s_activeOwner, owner) &&
                shared._isDragActive)
            {
                shared.DragLeaveCore();
            }

            bool entered = shared.DragEnterCore(
                targetWindow,
                dataObject,
                point,
                effect);
            if (!entered)
            {
                s_leasedTargets.Remove(owner);
                if (ReferenceEquals(s_activeOwner, owner))
                {
                    s_activeOwner = null;
                }

                ReleaseSharedIfIdleLocked();
                return false;
            }

            s_leasedTargets.Add(owner);
            s_activeOwner = owner;
            return true;
        }
    }

    internal static void DragOver(
        NativeDropTarget owner,
        NativeDropTarget.POINT point,
        uint effect)
    {
        lock (s_gate)
        {
            if (ReferenceEquals(s_activeOwner, owner))
            {
                s_shared?.DragOverCore(point, effect);
            }
        }
    }

    internal static void Show(
        NativeDropTarget owner,
        bool visible)
    {
        lock (s_gate)
        {
            if (ReferenceEquals(s_activeOwner, owner))
            {
                s_shared?.ShowCore(visible);
            }
        }
    }

    internal static void DragLeave(NativeDropTarget owner)
    {
        lock (s_gate)
        {
            s_leasedTargets.Remove(owner);
            if (ReferenceEquals(s_activeOwner, owner))
            {
                s_shared?.DragLeaveCore();
                s_activeOwner = null;
            }

            ReleaseSharedIfIdleLocked();
        }
    }

    internal static void Drop(
        NativeDropTarget owner,
        nint dataObject,
        NativeDropTarget.POINT point,
        uint effect)
    {
        lock (s_gate)
        {
            s_leasedTargets.Remove(owner);
            if (ReferenceEquals(s_activeOwner, owner))
            {
                s_shared?.DropCore(dataObject, point, effect);
                s_activeOwner = null;
            }

            ReleaseSharedIfIdleLocked();
        }
    }

    private static NativeDropImageManager? TryCreateCore()
    {
        try
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
        catch
        {
            return null;
        }
    }

    private static void ReleaseSharedIfIdleLocked()
    {
        if (s_leasedTargets.Count > 0 || s_shared is not { } shared)
        {
            return;
        }

        s_shared = null;
        s_activeOwner = null;
        shared.Dispose();
    }

    private bool DragEnterCore(
        nint targetWindow,
        nint dataObject,
        NativeDropTarget.POINT point,
        uint effect)
    {
        if (_pointer == 0 || dataObject == 0)
        {
            return false;
        }

        if (_isDragActive)
        {
            DragLeaveCore();
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
        return _isDragActive;
    }

    private void DragOverCore(
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

    private void ShowCore(bool visible)
    {
        if (_pointer == 0 || !_isDragActive)
        {
            return;
        }

        var callback = (delegate* unmanaged[Stdcall]<nint, int, int>)
            GetVtableEntry(ShowVtableSlot);
        _ = callback(_pointer, visible ? 1 : 0);
    }

    private void DragLeaveCore()
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

    private void DropCore(
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

        DragLeaveCore();
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
