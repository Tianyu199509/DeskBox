using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services.DragDrop;
using Microsoft.UI.Dispatching;

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
    private const int E_POINTER = unchecked((int)0x80004003);
    private const ushort CF_HDROP = 15;

    private static readonly Lazy<bool> s_disableWin10MoveTargetGate = new(static () =>
        string.Equals(
            Environment.GetEnvironmentVariable("DESKBOX_DISABLE_WIN10_MOVE_TARGET_GATE"),
            "1",
            StringComparison.Ordinal));

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
    private static readonly ushort s_shellIdListFormat =
        Win32Helper.GetRegisteredClipboardFormat("Shell IDList Array");
    private static readonly ushort s_shellIdListOffsetFormat =
        Win32Helper.GetRegisteredClipboardFormat("Shell Object Offsets");
    private static readonly ushort s_fileNameWFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileNameW");
    private static readonly ushort s_fileNameAFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileName");
    private static readonly ushort s_fileNameMapWFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileNameMapW");
    private static readonly ushort s_fileNameMapAFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileNameMap");
    private static readonly ushort s_fileDescriptorWFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileGroupDescriptorW");
    private static readonly ushort s_fileDescriptorAFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileGroupDescriptor");
    private static readonly ushort s_fileContentsFormat =
        Win32Helper.GetRegisteredClipboardFormat("FileContents");
    private static readonly ushort s_uniformResourceLocatorWFormat =
        Win32Helper.GetRegisteredClipboardFormat("UniformResourceLocatorW");
    private static readonly ushort s_uniformResourceLocatorAFormat =
        Win32Helper.GetRegisteredClipboardFormat("UniformResourceLocator");

    private nint _inner;
    private readonly DispatcherQueue? _ownerDispatcher;
    private readonly Func<uint, uint>? _preferredDropEffectResolver;
    private readonly Func<OutboundMoveTargetTrust>? _moveTargetTrustProbe;
    // 0 = unset; otherwise single-effect DROPEFFECT_COPY (1), MOVE (2), or LINK (4).
    // Accessed via Volatile.Read/Write so ARM64 weak memory ordering never observes a
    // torn (hasFlag, effectValue) pair across threads.
    private int _preferredDropEffectState;
    private bool _hasPreferredDropEffect => Volatile.Read(ref _preferredDropEffectState) != 0;
    // Win10 single-bit Move target firewall: when enabled (1), file-carrying formats
    // (CF_HDROP, Shell IDList Array, FileName*, FileGroupDescriptor*, FileContents)
    // are only served while the cursor is over a verified Desktop, Explorer folder,
    // or DeskBox window, blocking third-party apps (WeChat/QQ/browsers) from receiving
    // a Move-only drop that could delete originals. Drag visual formats stay allowed.
    private int _win10MoveTargetGateEnabled;
    // 0 = unobserved, 1 = last check allowed, 2 = last check blocked.
    private int _lastWin10MoveGateOutcome;
    private int _win10MoveGateBlockedCount;

    private bool IsWin10MoveTargetGateActive =>
        Volatile.Read(ref _win10MoveTargetGateEnabled) != 0 &&
        !s_disableWin10MoveTargetGate.Value;

    internal bool WasLastWin10MoveTargetGateBlocked =>
        Volatile.Read(ref _lastWin10MoveGateOutcome) == 2;

    internal int Win10MoveTargetGateBlockedCount =>
        Volatile.Read(ref _win10MoveGateBlockedCount);

    internal FileDragSourceGuardDataObject(nint inner)
        : this(inner, DispatcherQueue.GetForCurrentThread(), preferredDropEffectResolver: null, moveTargetTrustProbe: null)
    {
    }

    internal FileDragSourceGuardDataObject(
        nint inner,
        DispatcherQueue? ownerDispatcher,
        Func<uint, uint>? preferredDropEffectResolver = null,
        Func<OutboundMoveTargetTrust>? moveTargetTrustProbe = null)
    {
        if (inner == 0)
        {
            throw new ArgumentException(
                "The inner data-object pointer is null.",
                nameof(inner));
        }

        _inner = inner;
        _ownerDispatcher = ownerDispatcher;
        _preferredDropEffectResolver = preferredDropEffectResolver;
        _moveTargetTrustProbe = moveTargetTrustProbe;
        AddRef(inner);
    }

    internal void SetWin10MoveTargetGateEnabled(bool enabled)
    {
        Volatile.Write(ref _win10MoveTargetGateEnabled, enabled ? 1 : 0);
        Volatile.Write(ref _lastWin10MoveGateOutcome, 0);
        Volatile.Write(ref _win10MoveGateBlockedCount, 0);
    }

    ~FileDragSourceGuardDataObject()
    {
        nint inner = Interlocked.Exchange(ref _inner, 0);
        if (inner == 0 || Environment.HasShutdownStarted)
        {
            return;
        }

        try
        {
            // Finalizers run on the GC thread in arbitrary order, so the WinRT
            // DispatcherQueue RCW may already be finalized/disposed. Always guard
            // RCW access so an ObjectDisposedException can never crash the process.
            if (_ownerDispatcher is { } dispatcher &&
                dispatcher.TryEnqueue(() => ReleaseInnerComPointer(inner)))
            {
                return;
            }
        }
        catch
        {
        }

        ReleaseInnerComPointer(inner);
    }

    private static void ReleaseInnerComPointer(nint inner)
    {
        if (inner == 0)
        {
            return;
        }

        try
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)
                (*(nint**)inner)[ReleaseSlot])(inner);
        }
        catch
        {
            // Never let a finalizer or shutting-down dispatcher tear down the process.
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

    internal static bool IsFilePayloadFormatId(ushort id) =>
        id == CF_HDROP ||
        (id != 0 && (
            id == s_shellIdListFormat ||
            id == s_shellIdListOffsetFormat ||
            id == s_fileNameWFormat ||
            id == s_fileNameAFormat ||
            id == s_fileNameMapWFormat ||
            id == s_fileNameMapAFormat ||
            id == s_fileDescriptorWFormat ||
            id == s_fileDescriptorAFormat ||
            id == s_fileContentsFormat ||
            id == s_uniformResourceLocatorWFormat ||
            id == s_uniformResourceLocatorAFormat));

    private static bool IsFilePayloadFormat(nint format) =>
        format != 0 && IsFilePayloadFormatId(((NativeFormatEtc*)format)->ClipboardFormat);

    private bool ShouldBlockFilePayloadForCurrentTarget(string caller, ushort formatId)
    {
        if (!IsWin10MoveTargetGateActive)
        {
            return false;
        }

        OutboundMoveTargetTrust trust;
        try
        {
            trust = _moveTargetTrustProbe is not null
                ? _moveTargetTrustProbe()
                : TargetAwareDropEffectPolicy.ClassifyCurrentCursorMoveTargetTrust();
        }
        catch
        {
            trust = OutboundMoveTargetTrust.Untrusted;
        }

        bool blocked = trust == OutboundMoveTargetTrust.Untrusted;
        int nextOutcome = blocked ? 2 : 1;
        if (blocked)
        {
            Interlocked.Increment(ref _win10MoveGateBlockedCount);
        }

        int previousOutcome = Interlocked.Exchange(ref _lastWin10MoveGateOutcome, nextOutcome);
        if (previousOutcome != nextOutcome)
        {
            App.Log(
                $"[DragProtocol] stage=Win10MoveTargetGate action={(blocked ? "Block" : "Allow")} " +
                $"caller={caller} format={formatId} trust={trust}");
        }

        return blocked;
    }

    private static bool IsValidPreferredFormatEtc(nint format)
    {
        if (format == 0 || s_preferredDropEffectFormat == 0)
        {
            return false;
        }

        NativeFormatEtc* fmt = (NativeFormatEtc*)format;
        return fmt->ClipboardFormat == s_preferredDropEffectFormat &&
               (fmt->Aspect == 0 || (fmt->Aspect & 1) != 0) &&
               fmt->Index is -1 or 0 &&
               (fmt->MediumType & 1) != 0;
    }

    // DROPEFFECT_COPY (1), DROPEFFECT_MOVE (2), DROPEFFECT_LINK (4) — exactly
    // one effect. Every other bit pattern (including DROPEFFECT_NONE and any
    // combination mask) spells "no preference" and must not be answered.
    private static bool IsServiceablePreferredDropEffect(uint value) =>
        value is 1 or 2 or 4;

    public int GetData(nint format, nint medium)
    {
        if (format == 0 || medium == 0)
        {
            return E_POINTER;
        }

        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format))
        {
            if (!IsValidPreferredFormatEtc(format))
            {
                return DV_E_FORMATETC;
            }

            uint preferred = ResolveEffectivePreferredDropEffect();
            if (preferred == 0)
            {
                return DV_E_FORMATETC;
            }

            if (Win32Helper.TryCreateGlobalMemory(
                    BitConverter.GetBytes(preferred),
                    out nint memory))
            {
                ((NativeStorageMedium*)medium)->MediumType = 1;
                ((NativeStorageMedium*)medium)->Content = memory;
                ((NativeStorageMedium*)medium)->ReleaseUnknown = 0;
                return 0;
            }

            return unchecked((int)0x8007000E); // E_OUTOFMEMORY
        }

        if (IsFilePayloadFormat(format) &&
            ShouldBlockFilePayloadForCurrentTarget("GetData", ((NativeFormatEtc*)format)->ClipboardFormat))
        {
            return DV_E_FORMATETC;
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(GetDataSlot))(
            _inner, format, medium);
    }

    private uint ResolveEffectivePreferredDropEffect()
    {
        uint configured = (uint)Volatile.Read(ref _preferredDropEffectState);
        if (configured == 0)
        {
            return 0;
        }

        if (_preferredDropEffectResolver is { } resolver)
        {
            uint resolved = resolver(configured);
            // Returning 0 (DROPEFFECT_NONE) intentionally leaves Preferred DropEffect
            // unset (DV_E_FORMATETC) for Explorer folder windows so Windows applies its
            // native same-volume Move / cross-volume & USB Copy rule.
            return resolved == 0 || IsServiceablePreferredDropEffect(resolved)
                ? resolved
                : configured;
        }

        return configured;
    }

    public int GetDataHere(nint format, nint medium)
    {
        if (format == 0 || medium == 0)
        {
            return E_POINTER;
        }

        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format))
        {
            if (!IsValidPreferredFormatEtc(format))
            {
                return DV_E_FORMATETC;
            }

            uint preferred = ResolveEffectivePreferredDropEffect();
            if (preferred == 0)
            {
                return DV_E_FORMATETC;
            }

            NativeStorageMedium* caller = (NativeStorageMedium*)medium;
            if (caller->MediumType == 1 && caller->Content != 0)
            {
                // The caller owns the buffer; a malformed target could pass a
                // tiny HGLOBAL, so verify capacity before writing the DWORD.
                if (OleDropTargetNativeMethods.GlobalSize(caller->Content) < sizeof(uint))
                {
                    return DV_E_FORMATETC;
                }

                nint locked = OleDropTargetNativeMethods.GlobalLock(caller->Content);
                if (locked != 0)
                {
                    *(uint*)locked = preferred;
                    OleDropTargetNativeMethods.GlobalUnlock(caller->Content);
                    return 0;
                }
            }

            return DV_E_FORMATETC;
        }

        if (IsFilePayloadFormat(format) &&
            ShouldBlockFilePayloadForCurrentTarget("GetDataHere", ((NativeFormatEtc*)format)->ClipboardFormat))
        {
            return DV_E_FORMATETC;
        }

        return ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(GetDataHereSlot))(
            _inner, format, medium);
    }

    public int QueryGetData(nint format)
    {
        if (format == 0)
        {
            return E_POINTER;
        }

        if (IsCompletionFormat(format))
        {
            return DV_E_FORMATETC;
        }

        if (IsPreferredFormat(format))
        {
            if (!IsValidPreferredFormatEtc(format))
            {
                return DV_E_FORMATETC;
            }

            return ResolveEffectivePreferredDropEffect() != 0
                ? 0
                : DV_E_FORMATETC;
        }

        if (IsFilePayloadFormat(format) &&
            ShouldBlockFilePayloadForCurrentTarget("QueryGetData", ((NativeFormatEtc*)format)->ClipboardFormat))
        {
            return DV_E_FORMATETC;
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
            return E_POINTER; // caller keeps ownership.
        }

        if (IsCompletionFormat(format))
        {
            ushort formatId = ((NativeFormatEtc*)format)->ClipboardFormat;
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
                Volatile.Write(ref _preferredDropEffectState, (int)value);
            }
            else
            {
                Volatile.Write(ref _preferredDropEffectState, 0);
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
            if (storageMedium->MediumType != 1 ||
                storageMedium->Content == 0 ||
                OleDropTargetNativeMethods.GlobalSize(storageMedium->Content) < sizeof(uint))
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
            return E_POINTER;
        }

        const uint DataDirGet = 1;
        bool blockFilePayload = direction == DataDirGet &&
            IsWin10MoveTargetGateActive &&
            ShouldBlockFilePayloadForCurrentTarget("EnumFormatEtc", formatId: 0);
        if (!blockFilePayload)
        {
            if (direction != DataDirGet || !_hasPreferredDropEffect || s_preferredDropEffectFormat == 0)
            {
                return ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(EnumFormatEtcSlot))(
                    _inner, direction, enumerator);
            }
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
                     item.ClipboardFormat == s_preferredDropEffectFormat) ||
                    (blockFilePayload && IsFilePayloadFormatId(item.ClipboardFormat)))
                {
                    if (item.TargetDevice != 0)
                    {
                        ShellItemDragNativeMethods.CoTaskMemFree(item.TargetDevice);
                    }

                    continue;
                }

                formats.Add(item);
            }

            if (!blockFilePayload &&
                ResolveEffectivePreferredDropEffect() != 0 &&
                s_preferredDropEffectFormat != 0)
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
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, int>)Slot(DAdviseSlot))(
            _inner, format, flags, adviseSink, connection);

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
    private static nint s_activeWrapperPointer;

    internal static bool HasActiveManagedRoot => s_activeWrapper is not null;

    /// <summary>
    /// Arms or disarms the Win10 single-bit <c>Move</c> target firewall on the active
    /// guard wrapper. Call after <c>DragUI.SetContentFromDataPackage()</c> so local drag
    /// thumbnail extraction always succeeds, and before returning from <c>DragStarting</c>.
    /// </summary>
    internal static void ConfigureActiveWin10MoveTargetGate(bool enabled)
    {
        FileDragSourceGuardDataObject? active = s_activeWrapper;
        if (active is null)
        {
            return;
        }

        active.SetWin10MoveTargetGateEnabled(enabled);
        if (enabled)
        {
            App.Log(
                $"[DragProtocol] stage=Win10MoveTargetGateConfigured enabled=True " +
                $"disabledByEnv={s_disableWin10MoveTargetGate.Value}");
        }
    }

    internal static bool ConsumeActiveWin10MoveTargetGateBlockedFlag() =>
        s_activeWrapper?.WasLastWin10MoveTargetGateBlocked == true;

    /// <summary>
    /// Clears the explicit managed root once the platform drag loop finishes.
    /// Any remaining native reference held by COM / WinRT keeps the CCW alive
    /// until its final <c>Release</c>.
    /// </summary>
    internal static void ClearActiveManagedRoot()
    {
        s_activeWrapper = null;
        s_activeWrapperPointer = 0;
    }

    /// <summary>
    /// Returns an IDataObject pointer for a guard wrapper around
    /// <paramref name="shellDataObject"/>. The caller owns one reference on
    /// the returned pointer and must release it with
    /// <see cref="ReleaseInterfacePointer"/>.
    /// </summary>
    internal static nint CreateInterfacePointer(nint shellDataObject) =>
        CreateInterfacePointer(shellDataObject, preferredDropEffectResolver: null, moveTargetTrustProbe: null);

    /// <summary>
    /// Creates a guarded Shell <c>IDataObject</c> with Target-Aware Fail-Closed
    /// <c>Preferred DropEffect</c> resolution enabled: <c>DROPEFFECT_MOVE</c> is only
    /// served to verified Explorer Desktop/Folder views and DeskBox's own windows,
    /// downgrading fail-closed to <c>DROPEFFECT_COPY</c> for third-party receivers.
    /// </summary>
    internal static nint CreateTargetAwareInterfacePointer(
        nint shellDataObject,
        Func<OutboundMoveTargetTrust>? moveTargetTrustProbe = null) =>
        CreateInterfacePointer(
            shellDataObject,
            TargetAwareDropEffectPolicy.ResolvePreferredDropEffect,
            moveTargetTrustProbe);

    internal static nint CreateInterfacePointer(
        nint shellDataObject,
        Func<uint, uint>? preferredDropEffectResolver,
        Func<OutboundMoveTargetTrust>? moveTargetTrustProbe = null)
    {
        var wrapper = new FileDragSourceGuardDataObject(
            shellDataObject,
            DispatcherQueue.GetForCurrentThread(),
            preferredDropEffectResolver,
            moveTargetTrustProbe);
        s_activeWrapper = wrapper;
        nint pointer = (nint)ComInterfaceMarshaller<INativeDataObject>
            .ConvertToUnmanaged(wrapper);
        if (pointer == 0)
        {
            s_activeWrapper = null;
            s_activeWrapperPointer = 0;
            throw new InvalidOperationException(
                "Source-generated IDataObject marshalling returned a null pointer.");
        }

        s_activeWrapperPointer = pointer;
        return pointer;
    }

    internal static void ReleaseInterfacePointer(
        nint pointer,
        bool keepManagedRootForActiveDrag = false)
    {
        if (pointer != 0)
        {
            if (!keepManagedRootForActiveDrag && s_activeWrapperPointer == pointer)
            {
                s_activeWrapper = null;
                s_activeWrapperPointer = 0;
            }

            ComInterfaceMarshaller<INativeDataObject>.Free((void*)pointer);
        }
    }
}
