using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Native interop for the documented Open With association APIs
/// (SHAssocEnumHandlers / IAssocHandler). These are Shell-owned objects that
/// read association data only; no third-party context menu handler is ever
/// loaded through this path, so the shell-extension process isolation
/// contract is preserved. Vtable order and IIDs are verified against the
/// Windows SDK 10.0.26100 shobjidl_core.h header.
///
/// Every COM call is a raw vtable slot invocation (see <see cref="ComVtable"/>,
/// 2026-10-20): classic ComImport RCW marshaling does not exist on NativeAOT,
/// and the 1.5.9 retail installer shipped an empty Open With submenu because
/// of it — the IL2050 suppression silenced the analyzer, the runtime still
/// refused the marshaling.
/// </summary>
internal static class ShellOpenWithInterop
{
    internal const uint AssocFilterRecommended = 0x1;

    internal static readonly Guid IID_IDataObject =
        new("0000010e-0000-0000-c000-000000000046");

    private static readonly Guid IID_IShellItem =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // IID_IShellItemArray and BHID_DataObject verified against the Windows
    // SDK 10.0.26100 headers (shobjidl_core.h, ShlGuid.h).
    private static readonly Guid IID_IShellItemArray =
        new("b63ea76d-1f85-456f-a19c-48159efa858b");

    private static readonly Guid BHID_DataObject =
        new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");

    // ---- vtable delegates (COM stdcall; the interface pointer itself is
    // the first parameter; slot counts start after IUnknown) ----

    // IEnumAssocHandlers::Next — slot 3, the interface's only method. The
    // element array stays a plain IntPtr[] in a delegate signature: the old
    // ComImport form needed an explicit LPArray marshal attribute because
    // interface-method arrays default to SAFEARRAY, and the SafeArray
    // marshaler hard-crashed the CLR on IntPtr[] (2026-10-16); delegates
    // never engage that marshaler at all.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumHandlersNext(
        IntPtr self, uint celt, IntPtr[] rgelt, out uint pceltFetched);

    // IShellItemArray::BindToHandler — slot 3, first after IUnknown.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShellItemArrayBindToHandler(
        IntPtr self,
        IntPtr bindContext,
        ref Guid handlerId,
        ref Guid iid,
        out IntPtr bound);

    // IAssocHandler — GetName(3), GetUIName(4), GetIconLocation(5),
    // IsRecommended(6), MakeDefault(7), Invoke(8), CreateInvoker(9).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AssocHandlerGetUIName(
        IntPtr self, out IntPtr uiDisplayName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AssocHandlerGetIconLocation(
        IntPtr self, out IntPtr iconPath, out int iconIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AssocHandlerInvoke(IntPtr self, IntPtr dataObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(
        string extension,
        uint filter,
        out IntPtr enumHandlers);

    /// <summary>
    /// Creates the recommended-handler enumerator for one extension.
    /// Release the returned pointer with <see cref="Marshal.Release"/>.
    /// </summary>
    internal static bool TryEnumHandlers(
        string extension,
        out IntPtr enumHandlers)
    {
        int hr = SHAssocEnumHandlers(
            extension,
            AssocFilterRecommended,
            out enumHandlers);
        return hr >= 0 && enumHandlers != IntPtr.Zero;
    }

    /// <summary>IEnumAssocHandlers::Next for a batch of handlers.</summary>
    internal static int HandlersNext(
        IntPtr enumHandlers,
        IntPtr[] rgelt,
        out uint pceltFetched)
    {
        return ComVtable.Slot<EnumHandlersNext>(enumHandlers, 3)(
            enumHandlers,
            (uint)rgelt.Length,
            rgelt,
            out pceltFetched);
    }

    /// <summary>IAssocHandler::GetUIName — slot 4.</summary>
    internal static int HandlerGetUIName(
        IntPtr handler,
        out IntPtr uiDisplayName)
    {
        return ComVtable.Slot<AssocHandlerGetUIName>(handler, 4)(
            handler,
            out uiDisplayName);
    }

    /// <summary>IAssocHandler::GetIconLocation — slot 5.</summary>
    internal static int HandlerGetIconLocation(
        IntPtr handler,
        out IntPtr iconPath,
        out int iconIndex)
    {
        return ComVtable.Slot<AssocHandlerGetIconLocation>(handler, 5)(
            handler,
            out iconPath,
            out iconIndex);
    }

    /// <summary>IAssocHandler::Invoke — slot 8.</summary>
    internal static int HandlerInvoke(IntPtr handler, IntPtr dataObject)
    {
        return ComVtable.Slot<AssocHandlerInvoke>(handler, 8)(
            handler,
            dataObject);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        out IntPtr shellItem);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(
        IntPtr shellItem,
        ref Guid riid,
        out IntPtr shellItemArray);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr buffer);

    /// <summary>
    /// Builds the Shell data object for one item so it can be handed to
    /// <c>IAssocHandler::Invoke</c>, which accepts an IDataObject. The
    /// documented route is IShellItemArray.BindToHandler(BHID_DataObject);
    /// passing the IShellItem pointer straight to Invoke is an access
    /// violation, and SHCreateShellItemArrayFromShellItem does not accept
    /// IID_IDataObject. Returns the data object pointer; release it with
    /// <see cref="ReleaseDataObject"/> after the invoke returns.
    /// </summary>
    internal static bool TryCreateDataObjectForPath(
        string path,
        out IntPtr dataObject)
    {
        dataObject = IntPtr.Zero;
        Guid shellItemId = IID_IShellItem;
        Guid arrayId = IID_IShellItemArray;
        IntPtr shellItem = IntPtr.Zero;
        IntPtr arrayPointer = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ref shellItemId,
                out shellItem);
            SHCreateShellItemArrayFromShellItem(
                shellItem,
                ref arrayId,
                out arrayPointer);

            Guid handlerId = BHID_DataObject;
            Guid dataObjectId = IID_IDataObject;
            int hr = ComVtable.Slot<ShellItemArrayBindToHandler>(arrayPointer, 3)(
                arrayPointer,
                IntPtr.Zero,
                ref handlerId,
                ref dataObjectId,
                out dataObject);
            return hr >= 0 && dataObject != IntPtr.Zero;
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException)
        {
            ReleaseDataObject(ref dataObject);
            return false;
        }
        finally
        {
            if (arrayPointer != IntPtr.Zero)
            {
                Marshal.Release(arrayPointer);
            }

            if (shellItem != IntPtr.Zero)
            {
                Marshal.Release(shellItem);
            }
        }
    }

    internal static void ReleaseDataObject(ref IntPtr dataObject)
    {
        if (dataObject != IntPtr.Zero)
        {
            Marshal.Release(dataObject);
            dataObject = IntPtr.Zero;
        }
    }

    private const uint OaifAllowRegistration = 0x00000001;
    private const uint OaifExec = 0x00000004;
    private const int HResultErrorCancelled = unchecked((int)0x800704C7);

    /// <summary>
    /// Shows the system Open With dialog (SHOpenWithDialog — the documented
    /// API for exactly this; the <c>openas</c> verb via ShellExecuteEx is
    /// rejected with access denied from this process, proven by the
    /// open-with repro harness). Modal on the calling STA thread; user
    /// cancellation counts as success.
    /// </summary>
    internal static bool TryShowOpenAsDialog(
        IntPtr ownerWindow,
        string filePath)
    {
        var info = new OpenAsInfo
        {
            File = filePath,
            Class = null,
            Flags = OaifAllowRegistration | OaifExec
        };

        try
        {
            SHOpenWithDialog(ownerWindow, ref info);
            return true;
        }
        catch (COMException ex) when (ex.HResult == HResultErrorCancelled)
        {
            return true;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode,
        PreserveSig = false)]
    private static extern void SHOpenWithDialog(
        IntPtr parentWindow,
        ref OpenAsInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public uint Flags;
    }
}
