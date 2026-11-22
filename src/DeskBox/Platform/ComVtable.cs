using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Direct COM vtable slot invocation. Classic ComImport RCW marshaling does
/// not exist on NativeAOT — the call dies at runtime with "COM Interop
/// requires ComWrapper instance registered for marshalling" (proven by the
/// 1.5.9 retail acceptance on 2026-10-20: the Open With submenu enumerated
/// nothing on every machine that ran the AOT installer, while Debug builds
/// stayed green because JIT ships the RCW machinery). Resolving the slot's
/// function pointer and invoking it through an
/// <see cref="UnmanagedFunctionPointerAttribute"/> delegate behaves
/// identically on JIT and AOT, so callers keep one code path instead of a
/// runtime fork. Slot indices count from the interface's first method after
/// IUnknown (slots 0-2) and must be verified against the Windows SDK header
/// at every call site.
/// </summary>
internal static partial class ComVtable
{
    /// <summary>
    /// Resolves the raw function pointer for one vtable slot without allocating
    /// a managed delegate wrapper. Callers invoke it via <c>delegate* unmanaged[Stdcall]</c>.
    /// </summary>
    internal static unsafe IntPtr SlotPointer(IntPtr comObject, int slot) =>
        (*(IntPtr**)comObject)[slot];

    /// <summary>
    /// Resolves the delegate for one vtable slot. The returned delegate
    /// carries the interface pointer as its first argument.
    /// </summary>
    internal static T Slot<T>(IntPtr comObject, int slot) where T : class
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr proc = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    /// <summary>
    /// IUnknown::QueryInterface (slot 0) through a raw function pointer — the
    /// zero-allocation counterpart of <see cref="SlotPointer"/> for callers that
    /// hold a bare COM pointer without any ComImport declaration.
    /// Release the returned pointer with <see cref="Marshal.Release"/>.
    /// </summary>
    internal static unsafe int QueryInterface(
        IntPtr comObject, ref Guid iid, out IntPtr ppv)
    {
        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)SlotPointer(comObject, 0);
        fixed (Guid* iidPtr = &iid)
        fixed (IntPtr* ppvPtr = &ppv)
        {
            return queryInterface(comObject, iidPtr, ppvPtr);
        }
    }

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateInstance(
        ref Guid clsid,
        IntPtr punkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    /// <summary>
    /// CoCreateInstance with an in-process server, returning the raw
    /// interface pointer. Release it with <see cref="Marshal.Release"/>.
    /// </summary>
    internal static bool TryCreateInProcessInstance(
        ref Guid clsid,
        ref Guid iid,
        out IntPtr instance)
    {
        int hr = CoCreateInstance(
            ref clsid,
            IntPtr.Zero,
            ClsctxInprocServer,
            ref iid,
            out instance);
        return hr >= 0 && instance != IntPtr.Zero;
    }

    private const uint ClsctxInprocServer = 0x1;
}
