using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Controls;

[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("0000010E-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface INativeDataObject
{
    [PreserveSig]
    int GetData(nint format, nint medium);

    [PreserveSig]
    int GetDataHere(nint format, nint medium);

    [PreserveSig]
    int QueryGetData(nint format);

    [PreserveSig]
    int GetCanonicalFormatEtc(nint formatIn, nint formatOut);

    [PreserveSig]
    int SetData(nint format, nint medium, int release);

    [PreserveSig]
    int EnumFormatEtc(uint direction, nint enumerator);

    [PreserveSig]
    int DAdvise(nint format, uint flags, nint adviseSink, nint connection);

    [PreserveSig]
    int DUnadvise(uint connection);

    [PreserveSig]
    int EnumDAdvise(nint enumerator);
}

/// <summary>
/// Passes every data format through to the Shell file data object unchanged,
/// except two side channels the object cannot carry itself:
/// 1. Preferred DropEffect — the drag infrastructure's operation hint. A
///    native Shell IDataObject rejects SetData for it, so the value is stored
///    on this wrapper and served back on GetData. Only a single-effect value
///    (Copy, Move, or Link) is serviceable: DROPEFFECT_NONE and multi-effect
///    masks are the engine's "no preference" spellings, and answering them
///    would expose a Preferred DropEffect whose value means nothing — some
///    Win10/third-party targets treat "format present" differently from
///    "format absent". An unserviceable value keeps the format unset, which
///    is exactly what a Shell-native source without a preference looks like.
/// 2. Completion receipts (Performed/Logical Performed DropEffect, Paste
///    Succeeded, TargetCLSID) — consumed here, never delegated or answered:
///    a receiver's Move reply must not authorize source cleanup.
/// This policy applies to every caller and does not depend on process or
/// window identification.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class FileDragSourceGuardDataObject :
    INativeDataObject
{
    private const int GetDataSlot = 3;
    private const int GetDataHereSlot = 4;
    private const int QueryGetDataSlot = 5;
    private const int GetCanonicalFormatEtcSlot = 6;
    private const int SetDataSlot = 7;
    private const int EnumFormatEtcSlot = 8;
    private const int DAdviseSlot = 9;
    private const int DUnadviseSlot = 10;
    private const int EnumDAdviseSlot = 11;
    private const int ReleaseSlot = 2;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);

    private static readonly ushort s_performedDropEffectFormat =
        Win32Helper.GetRegisteredClipboardFormat("Performed DropEffect");
    private static readonly ushort s_logicalPerformedDropEffectFormat =
        Win32Helper.GetRegisteredClipboardFormat("Logical Performed DropEffect");
    private static readonly ushort s_pasteSucceededFormat =
        Win32Helper.GetRegisteredClipboardFormat("Paste Succeeded");
    private static readonly ushort s_targetClsidFormat =
        Win32Helper.GetRegisteredClipboardFormat("TargetCLSID");
    private static readonly ushort s_preferredDropEffectFormat =
        Win32Helper.GetRegisteredClipboardFormat("Preferred DropEffect");

    private nint _inner;
    private uint _preferredDropEffect;
    private bool _hasPreferredDropEffect;

    internal FileDragSourceGuardDataObject(nint inner)
    {
        if (inner == 0)
        {
            throw new ArgumentException(
                "The inner data-object pointer is null.",
                nameof(inner));
        }

        _inner = inner;
        AddRef(inner);
    }

    ~FileDragSourceGuardDataObject()
    {
        nint inner = Interlocked.Exchange(ref _inner, 0);
        if (inner != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)
                (*(nint**)inner)[ReleaseSlot])(inner);
        }
    }

    private static bool IsCompletionFormatId(ushort id) =>
        id != 0 && (id == s_performedDropEffectFormat ||
            id == s_logicalPerformedDropEffectFormat ||
            id == s_pasteSucceededFormat || id == s_targetClsidFormat);

    private static bool IsCompletionFormat(nint format) =>
        format != 0 && IsCompletionFormatId(((NativeFormatEtc*)format)->ClipboardFormat);

    private static bool IsPreferredFormat(nint format) =>
        format != 0 &&
        s_preferredDropEffectFormat != 0 &&
        ((NativeFormatEtc*)format)->ClipboardFormat == s_preferredDropEffectFormat;

    // DROPEFFECT_COPY (1), DROPEFFECT_MOVE (2), DROPEFFECT_LINK (4) — exactly
    // one effect. Every other bit pattern (including DROPEFFECT_NONE and any
    // combination mask) spells "no preference" and must not be answered.
    private static bool IsServiceablePreferredDropEffect(uint value) =>
        value is 1 or 2 or 4;

    public int GetData(nint format, nint medium)
    {
        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format) && _hasPreferredDropEffect &&
            medium != 0)
        {
            if (Win32Helper.TryCreateGlobalMemory(
                    BitConverter.GetBytes(_preferredDropEffect),
                    out nint memory))
            {
                ((NativeStorageMedium*)medium)->MediumType = 1;
                ((NativeStorageMedium*)medium)->Content = memory;
                ((NativeStorageMedium*)medium)->ReleaseUnknown = 0;
                return 0;
            }

            return unchecked((int)0x8007000E); // E_OUTOFMEMORY
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(GetDataSlot))(
            _inner, format, medium);
    }

    public int GetDataHere(nint format, nint medium)
    {
        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format) && _hasPreferredDropEffect &&
            medium != 0)
        {
            NativeStorageMedium* caller = (NativeStorageMedium*)medium;
            if (caller->MediumType == 1 && caller->Content != 0)
            {
                // The caller owns the buffer; a malformed target could pass a
                // tiny HGLOBAL, so verify capacity before writing the DWORD.
                if (OleDropTargetNativeMethods.GlobalSize(caller->Content)
                        .ToInt64() < sizeof(uint))
                {
                    return DV_E_FORMATETC;
                }

                nint locked = OleDropTargetNativeMethods.GlobalLock(caller->Content);
                if (locked != 0)
                {
                    *(uint*)locked = _preferredDropEffect;
                    OleDropTargetNativeMethods.GlobalUnlock(caller->Content);
                    return 0;
                }
            }

            return DV_E_FORMATETC;
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(GetDataHereSlot))(
            _inner, format, medium);
    }

    public int QueryGetData(nint format)
    {
        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format) && _hasPreferredDropEffect)
        {
            return 0;
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(QueryGetDataSlot))(
            _inner, format);
    }

    public int GetCanonicalFormatEtc(nint formatIn, nint formatOut) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(GetCanonicalFormatEtcSlot))(
            _inner, formatIn, formatOut);

    public int SetData(nint format, nint medium, int release)
    {
        if (format == 0 || medium == 0)
        {
            return unchecked((int)0x80004003); // E_POINTER; caller keeps ownership.
        }

        if (IsCompletionFormat(format))
        {
            ushort formatId = ((NativeFormatEtc*)format)->ClipboardFormat;
            Interlocked.Increment(ref s_completionReceiptSequence);
            App.Log($"[DragProtocol] stage=CompletionReceiptIgnored " +
                $"format={formatId} value=0x{ReadMediumDword(medium):X} " +
                "policy=SourceGuard");
            ConsumeMedium(medium, release);
            return 0;
        }

        // Preferred DropEffect is stored on this wrapper: the inner Shell
        // object rejects SetData for the format. Only single-effect values
        // are serviceable; DROPEFFECT_NONE and multi-effect masks mean "no
        // preference" and keep the format unset so targets see exactly what
        // a preference-less Shell source would show (or not show).
        if (IsPreferredFormat(format))
        {
            uint value = ReadMediumDword(medium);
            if (IsServiceablePreferredDropEffect(value))
            {
                _preferredDropEffect = value;
                _hasPreferredDropEffect = true;
            }
            else
            {
                _preferredDropEffect = 0;
                _hasPreferredDropEffect = false;
                App.LogVerbose(
                    $"[DragProtocol] stage=PreferredDropEffectUnset " +
                    $"value=0x{value:X} policy=SourceGuard");
            }

            ConsumeMedium(medium, release);
            return 0;
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int, int>)Slot(SetDataSlot))(
            _inner, format, medium, release);
    }

    private static uint ReadMediumDword(nint medium)
    {
        try
        {
            var storageMedium = (NativeStorageMedium*)medium;
            if (storageMedium->MediumType != 1 || storageMedium->Content == 0)
            {
                return 0;
            }

            nint pointer = OleDropTargetNativeMethods.GlobalLock(
                storageMedium->Content);
            if (pointer == 0)
            {
                return 0;
            }

            try
            {
                return (uint)Marshal.ReadInt32(pointer);
            }
            finally
            {
                OleDropTargetNativeMethods.GlobalUnlock(storageMedium->Content);
            }
        }
        catch
        {
            return 0;
        }
    }

    private static void ConsumeMedium(nint medium, int release)
    {
        if (release != 0)
        {
            Win32Helper.ReleaseStorageMedium(ref *(NativeStorageMedium*)medium);
        }
    }

    public int EnumFormatEtc(uint direction, nint enumerator)
    {
        if (enumerator == 0)
        {
            return unchecked((int)0x80004003); // E_POINTER
        }

        const uint DataDirGet = 1;
        if (direction != DataDirGet || !_hasPreferredDropEffect || s_preferredDropEffectFormat == 0)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(EnumFormatEtcSlot))(
                _inner, direction, enumerator);
        }

        nint innerEnumerator = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(EnumFormatEtcSlot))(
            _inner, direction, (nint)(&innerEnumerator));
        if (hr < 0 || innerEnumerator == 0)
        {
            *(nint*)enumerator = innerEnumerator;
            return hr;
        }

        var formats = new List<NativeFormatEtc>(16);
        try
        {
            var nextFn = (delegate* unmanaged[Stdcall]<nint, uint, NativeFormatEtc*, uint*, int>)
                (*(nint**)innerEnumerator)[3];
            while (true)
            {
                NativeFormatEtc item = default;
                uint fetched = 0;
                int nextHr = nextFn(innerEnumerator, 1, &item, &fetched);
                if (nextHr < 0 || fetched == 0)
                {
                    break;
                }

                if (IsCompletionFormatId(item.ClipboardFormat) ||
                    (s_preferredDropEffectFormat != 0 &&
                     item.ClipboardFormat == s_preferredDropEffectFormat))
                {
                    if (item.TargetDevice != 0)
                    {
                        ShellItemDragNativeMethods.CoTaskMemFree(item.TargetDevice);
                    }

                    continue;
                }

                formats.Add(item);
            }

            if (_hasPreferredDropEffect && s_preferredDropEffectFormat != 0)
            {
                formats.Add(new NativeFormatEtc
                {
                    ClipboardFormat = s_preferredDropEffectFormat,
                    TargetDevice = 0,
                    Aspect = 1, // DVASPECT_CONTENT
                    Index = -1,
                    MediumType = 1, // TYMED_HGLOBAL
                });
            }

            ReadOnlySpan<NativeFormatEtc> span = CollectionsMarshal.AsSpan(formats);
            NativeFormatEtc emptyPlaceholder = default;
            fixed (NativeFormatEtc* pinned = span)
            {
                NativeFormatEtc* arrayPtr = span.Length > 0 ? pinned : &emptyPlaceholder;
                int createHr = ShellItemDragNativeMethods.SHCreateStdEnumFmtEtc(
                    (uint)span.Length,
                    arrayPtr,
                    out nint stdEnumerator);
                *(nint*)enumerator = createHr >= 0 ? stdEnumerator : 0;
                return createHr;
            }
        }
        finally
        {
            foreach (NativeFormatEtc item in formats)
            {
                if (item.TargetDevice != 0)
                {
                    ShellItemDragNativeMethods.CoTaskMemFree(item.TargetDevice);
                }
            }

            ((delegate* unmanaged[Stdcall]<nint, uint>)
                (*(nint**)innerEnumerator)[ReleaseSlot])(innerEnumerator);
        }
    }

    public int DAdvise(nint format, uint flags, nint adviseSink, nint connection) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int>)Slot(DAdviseSlot))(
            _inner, format, (nint)flags, adviseSink, connection);

    public int DUnadvise(uint connection) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(DUnadviseSlot))(
            _inner, connection);

    public int EnumDAdvise(nint enumerator) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(EnumDAdviseSlot))(
            _inner, enumerator);

    private nint Slot(int slot)
    {
        nint inner = _inner;
        if (inner == 0)
        {
            throw new ObjectDisposedException(
                nameof(FileDragSourceGuardDataObject));
        }

        nint* vtable = *(nint**)inner;
        if (vtable == null || vtable[slot] == 0)
        {
            throw new InvalidOperationException(
                $"The Shell data-object vtable does not contain slot {slot}.");
        }

        return vtable[slot];
    }

    private static void AddRef(nint unknown) =>
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[1])(unknown);

    /// <summary>
    /// Explicit managed root for the wrapper of the most recent drag. The
    /// source-generated CCW pins the object while native references exist,
    /// but during a drag the only native reference is the one WinUI's
    /// undocumented IDataObjectProvider contract is assumed to hold. Rooting
    /// the wrapper here makes its survival independent of that assumption;
    /// the next drag replaces it, letting the previous wrapper (and its CCW)
    /// be collected so its finalizer releases the inner data object.
    /// </summary>
    private static FileDragSourceGuardDataObject? s_activeWrapper;

    // Diagnostic only: the count proves the firewall saw a receiver's
    // completion claim without ever acting on it. Nothing in the app may
    // consume a receipt as proof a transfer happened.
    private static int s_completionReceiptSequence;

    /// <summary>
    /// Returns an IDataObject pointer for a guard wrapper around
    /// <paramref name="shellDataObject"/>. The caller owns one reference on
    /// the returned pointer and must release it with
    /// <see cref="ReleaseInterfacePointer"/>.
    /// </summary>
    internal static nint CreateInterfacePointer(nint shellDataObject)
    {
        var wrapper = new FileDragSourceGuardDataObject(shellDataObject);
        s_activeWrapper = wrapper;
        nint pointer = (nint)ComInterfaceMarshaller<INativeDataObject>
            .ConvertToUnmanaged(wrapper);
        if (pointer == 0)
        {
            s_activeWrapper = null;
            throw new InvalidOperationException(
                "Source-generated IDataObject marshalling returned a null pointer.");
        }

        return pointer;
    }

    internal static void ReleaseInterfacePointer(nint pointer)
    {
        if (pointer != 0)
        {
            ComInterfaceMarshaller<INativeDataObject>.Free((void*)pointer);
        }
    }
}
