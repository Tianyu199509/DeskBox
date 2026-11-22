using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.DragDrop;

namespace DeskBox.Helpers;

/// <summary>
/// Complete intent information captured at the native OLE Drop boundary.
/// The legacy <see cref="NativeDropTarget.DropEvent"/> remains available for
/// consumers that only need copy/move, while this event preserves modifier
/// state for asynchronous imports.
/// </summary>
public sealed record NativeDropIntentEventArgs(
    IReadOnlyList<string> Paths,
    int ScreenX,
    int ScreenY,
    bool ContainsTemporaryFiles,
    bool CopyRequested,
    bool ShortcutRequested,
    bool RightButtonDrag,
    uint FeedbackEffect,
    uint KeyState,
    uint AllowedEffects,
    long GestureId = 0,
    IReadOnlyList<DroppedItemOriginKind>? ItemOrigins = null,
    int MaterializationSkippedCount = 0);

/// <summary>
/// Everything a shortcut-launch consumer needs to delegate a drop to the
/// Shell's own drop target. Raised synchronously inside the OLE Drop callback
/// while the source data object pointer is still valid.
/// </summary>
internal sealed record NativeDropLaunchRequest(
    IReadOnlyList<string> Paths,
    nint DataObject,
    uint KeyState,
    int ScreenX,
    int ScreenY,
    uint AllowedEffects,
    bool RightButtonDrag,
    bool ContainsTemporaryFiles);

/// <summary>
/// COM IDropTarget implementation that bridges native OLE drag-drop to .NET events.
/// Replaces the legacy WM_DROPFILES approach, providing real-time drag-over feedback.
/// </summary>
public sealed partial class NativeDropTarget : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SIZEL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal unsafe struct FILEDESCRIPTORW
    {
        internal const int MaxPathChars = 260;

        public uint dwFlags;
        public Guid clsid;
        public SIZEL sizel;
        public POINTL pointl;
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public fixed char cFileName[MaxPathChars];

        internal readonly string GetFileName()
        {
            fixed (char* ptr = cFileName)
            {
                var span = new ReadOnlySpan<char>(ptr, MaxPathChars);
                int nul = span.IndexOf('\0');
                return new string(nul >= 0 ? span[..nul] : span);
            }
        }

        internal void SetFileName(string? value)
        {
            fixed (char* ptr = cFileName)
            {
                var dest = new Span<char>(ptr, MaxPathChars);
                dest.Clear();
                if (!string.IsNullOrEmpty(value))
                {
                    int copyLen = Math.Min(value.Length, MaxPathChars - 1);
                    value.AsSpan(0, copyLen).CopyTo(dest);
                }
            }
        }

        internal static FILEDESCRIPTORW Create(
            string fileName,
            uint dwFlags = 0,
            uint dwFileAttributes = 0,
            uint nFileSizeHigh = 0,
            uint nFileSizeLow = 0)
        {
            var descriptor = new FILEDESCRIPTORW
            {
                dwFlags = dwFlags,
                dwFileAttributes = dwFileAttributes,
                nFileSizeHigh = nFileSizeHigh,
                nFileSizeLow = nFileSizeLow,
            };
            descriptor.SetFileName(fileName);
            return descriptor;
        }
    }

    // ── Constants ──

    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const uint TYMED_ISTREAM = 4;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint SfgaoFilesys = 0x40000000;
    private const uint SfgaoFolder = 0x20000000;
    private const uint SfgaoStream = 0x00400000;
    private static readonly Guid s_iidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid s_bhidStream = new("1cebb3ab-7c10-499a-a417-92ca16c4cb83");
    private static readonly Guid s_iidIStream = new("0000000c-0000-0000-c000-000000000046");
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int MaxShellApplicationDropItems = 256;
    private const string AppsFolderPrefix = "shell:AppsFolder\\";
    private const string AppsFolderClsidPrefix =
        "shell:::{4234d49b-0245-4df3-b780-3893943456e1}\\";

    // ── P/Invoke ──
    // ole32/user32/kernel32/shell32 entry points live in
    // DeskBox.Platform.OleDropTargetNativeMethods.

    private const uint CF_HDROP = 15;
    private static readonly ushort s_fileGroupDescriptorFormat;
    private static readonly ushort s_fileContentsFormat;
    private static readonly ushort s_shellIdListFormat;
    private static readonly ushort s_performedDropEffectFormat;
    private static readonly ushort s_logicalPerformedDropEffectFormat;
    private static long s_nextGestureId;

    // ── State ──

    private readonly IntPtr _hwnd;
    private readonly NativeDropTargetComObject _comObject;
    private Func<bool> _defaultMoveProvider;
    private Func<bool>? _followWindowsProvider;
    private Func<uint, NativeDropDescriptionText?>? _dropDescriptionProvider;
    private Func<bool>? _useShellVisualProvider;
    private nint _activeDataObject;
    private bool _hasDropDescriptionAttempt;
    private uint _lastDropDescriptionEffect;
    private NativeDropDescriptionText? _lastDropDescriptionText;
    private bool _shellVisualActive;
    private bool _shellVisualShown;
    private bool _registered;
    private bool _blockedByExistingRegistration;
    private volatile bool _disposed;
    private bool _rightButtonDragActive;
    private bool _hasVirtualDescriptorData;
    private bool _hasShellIdListFormat;
    private bool _isAsyncCapableSource;
    private long _activeGestureId;
    private IReadOnlyList<string> _dragPathHints = [];
    private SourceOriginSnapshot? _cachedDragSourceOriginSnapshot;

    internal enum ShellIdListItemKind
    {
        Application,
        FileSystemPath,
        VirtualNamespace,
        StreamObject,
    }

    internal sealed record ShellIdListDropItem(
        ShellIdListItemKind Kind,
        string AppUserModelId,
        string ParsingName,
        string DisplayName,
        string FileSystemPath,
        byte[]? RawPidl = null);

    /// <summary>Fired when a drag enters the window. Provides screen coordinates and whether file data is available.</summary>
    public event Action<int, int, bool>? DragEnterEvent;

    /// <summary>Fired as the drag moves over the window. Provides screen coordinates.</summary>
    public event Action<int, int>? DragOverEvent;

    /// <summary>Fired when the drag leaves the window without dropping.</summary>
    public event Action? DragLeaveEvent;

    /// <summary>Fired when files are dropped. Provides the list of file paths and screen coordinates.</summary>
    public event Action<IReadOnlyList<string>, int, int, bool, bool>? DropEvent;

    /// <summary>
    /// Fired with the complete native drop intent. This is preferred by
    /// asynchronous consumers that need Alt/Ctrl+Shift shortcut and right
    /// button drag state after the OLE callback has released its data object.
    /// </summary>
    public event Action<NativeDropIntentEventArgs>? DropIntentEvent;

    /// <summary>
    /// Optional gate consulted synchronously inside the OLE Drop callback,
    /// before the completion effect is committed. Return true for a path the
    /// receiving surface can never display: the whole drop is then refused
    /// with no effect, because reporting Move for files the import pipeline
    /// will reject would let the drag source delete them.
    /// </summary>
    public Func<string, bool>? UndisplayablePathProbe { get; set; }

    /// <summary>
    /// Fired when <see cref="UndisplayablePathProbe"/> blocked a drop.
    /// Provides the requested path count. Runs on the OLE thread.
    /// </summary>
    public event Action<int>? UndisplayableDropBlocked;

    /// <summary>
    /// Fired when an accepted drop produced zero importable items after its
    /// deferred extraction (async virtual payloads and Shell-object
    /// materialization alike). The hover promised a link the extraction could
    /// not honor, so the silence must surface as user feedback. Runs on the
    /// UI continuation after the OLE callback returned.
    /// </summary>
    public event Action? ZeroResultDropBlocked;

    /// <summary>
    /// Fired when a drop was refused because the import pipeline was still
    /// busy. The source kept everything. Runs inside the OLE Drop callback.
    /// </summary>
    public event Action? ImportBusyDropBlocked;

    /// <summary>
    /// Optional probe consulted inside DragEnter/DragOver/Drop to decide
    /// whether the dragged paths and the destination live on the same
    /// volume. When unset the feedback assumes same-volume semantics.
    /// </summary>
    internal Func<IReadOnlyList<string>, bool>? SameVolumeProbe { get; set; }

    /// <summary>
    /// Budget-aware same-volume probe allowing hover paths to stay bounded
    /// (80ms) while Drop / right-drag menu resolution allows a deeper cold-disk
    /// probe (1500ms).
    /// </summary>
    internal Func<IReadOnlyList<string>, TimeSpan, bool>? SameVolumeWithBudgetProbe { get; set; }

    /// <summary>
    /// Optional probe consulted inside DragEnter/DragOver/Drop to decide
    /// whether the target surface currently has a mapped managed folder.
    /// Non-file or already-mapped surfaces default to true.
    /// </summary>
    internal Func<bool>? HasMappedFolderProbe { get; set; }

    /// <summary>
    /// Optional probe consulted inside DragEnter/DragOver/Drop to classify
    /// whether the hovered target is a directory surface, Recycle Bin tile,
    /// or application shortcut tile.
    /// </summary>
    internal Func<TransferTargetKind>? TargetKindProbe { get; set; }

    internal long ActiveGestureId => _activeGestureId;

    internal static long AllocateGestureId() =>
        System.Threading.Interlocked.Increment(ref s_nextGestureId);

    /// <summary>
    /// Optional gate consulted inside the OLE Drop callback before the
    /// completion effect is committed. Returning true refuses the drop so a
    /// busy import pipeline is never silently dropped after the source was
    /// told the drop succeeded.
    /// </summary>
    internal Func<bool>? ImportBusyProbe { get; set; }

    /// <summary>
    /// Returns the shortcut tile currently under the drag hover (if any) so
    /// the deferred-materialization completion can verify the hover target
    /// has not been replaced by a newer gesture before delegating the launch.
    /// Without this continuity check the completion-time claim would stamp
    /// consumption windows onto a *later* drop's gesture state.
    /// </summary>
    internal Func<string?>? LaunchTargetPathProbe { get; set; }

    /// <summary>
    /// Runs inside the OLE Drop callback before the import events, while the
    /// source data object is alive, so the handler can delegate the drop to
    /// a Shell drop target (drop onto an application shortcut). Returning
    /// Handled=true consumes the drop; no import events are raised.
    /// </summary>
    internal Func<NativeDropLaunchRequest, ShellDropLaunchResult>? LaunchDropHandler
    {
        get;
        set;
    }



    /// <summary>
    /// Whether the current drag payload contains file drop data (CF_HDROP).
    /// Valid between DragEnter and DragLeave/Drop.
    /// </summary>
    public bool HasFileData { get; private set; }

    /// <summary>
    /// Physical file paths exposed by CF_HDROP at DragEnter. These are a
    /// lightweight preview hint only; the Drop callback still re-reads and
    /// validates the authoritative payload.
    /// </summary>
    public IReadOnlyList<string> DragPathHints => _dragPathHints;

    public bool HasVirtualFileData { get; private set; }

    /// <summary>
    /// Whether the current non-filesystem payload can contain an AppsFolder
    /// Shell object. Physical CF_HDROP payloads always take precedence so this
    /// state cannot alter their normal move/copy behavior.
    /// </summary>
    public bool HasShellApplicationData { get; private set; }

    /// <summary>
    /// Whether the current non-filesystem payload contains materializable
    /// Shell namespace objects outside AppsFolder (Recycle Bin, This PC, ...).
    /// These honor the Link hover by writing a PIDL-based .lnk (pure virtual
    /// items) or importing the file-system path the item resolves to; items
    /// without a consumable identity still read as no-drop. Physical
    /// CF_HDROP payloads always take precedence.
    /// </summary>
    public bool HasShellNamespaceData { get; private set; }

    internal bool HasActiveShellDragWindow { get; private set; }

    internal bool IsRegistered => _registered;

    internal bool IsRegistrationResolved => _registered || _blockedByExistingRegistration;

    /// <summary>
    /// When true, a <c>DRAGDROP_E_ALREADYREGISTERED</c> collision revokes the
    /// prior OLE drop target on this HWND and retries once (used on Win10
    /// <c>DesktopChildSiteBridge</c> so Store MSIX cross-widget drops land on
    /// <see cref="NativeDropTarget"/> rather than WinUI's StorageFile bridge).
    /// </summary>
    internal bool ReplaceExistingRegistration { get; set; }

    static NativeDropTarget()
    {
        s_fileGroupDescriptorFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileGroupDescriptorW");
        s_fileContentsFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileContents");
        s_shellIdListFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Shell IDList Array");
        s_performedDropEffectFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Performed DropEffect");
        s_logicalPerformedDropEffectFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Logical Performed DropEffect");
    }

    public NativeDropTarget(IntPtr hwnd, Func<bool>? defaultMoveProvider = null)
        : this(
            hwnd,
            defaultMoveProvider,
            dropDescriptionProvider: null,
            useShellVisualProvider: null,
            followWindowsProvider: null)
    {
    }

    internal NativeDropTarget(
        IntPtr hwnd,
        Func<bool>? defaultMoveProvider,
        Func<uint, NativeDropDescriptionText?>? dropDescriptionProvider,
        Func<bool>? useShellVisualProvider = null,
        Func<bool>? followWindowsProvider = null)
    {
        _hwnd = hwnd;
        _defaultMoveProvider = defaultMoveProvider ?? (() => true);
        _followWindowsProvider = followWindowsProvider;
        _dropDescriptionProvider = dropDescriptionProvider;
        _useShellVisualProvider = useShellVisualProvider;
        _comObject = new NativeDropTargetComObject(this);
    }

    public void Register()
    {
        bool replaceExisting = ReplaceExistingRegistration;
        if (_registered || (_blockedByExistingRegistration && !replaceExisting))
        {
            return;
        }

        try
        {
            // Ensure OLE is initialized on the HWND's owning UI STA thread
            // rather than in the static constructor (which can run on an MTA worker).
            _ = OleDropTargetNativeMethods.OleInitialize(IntPtr.Zero);
            if (NativeDropTargetComInterop.TryRegister(
                    _hwnd,
                    _comObject,
                    replaceExisting,
                    out bool alreadyRegistered))
            {
                _registered = true;
                _blockedByExistingRegistration = false;
                App.Log(
                    $"[DropTarget] Registered IDropTarget for hwnd=0x{_hwnd.ToInt64():X} " +
                    $"shellImageHelper=shared replaced={replaceExisting}");
            }
            else if (alreadyRegistered)
            {
                _blockedByExistingRegistration = true;
                App.LogVerbose(
                    $"[DropTarget] HWND already has an OLE IDropTarget; standing down " +
                    $"hwnd=0x{_hwnd.ToInt64():X}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] RegisterDragDrop failed: {ex.Message}");
        }
    }

    public void Unregister()
    {
        EndNativeDragVisual();
        if (!_registered)
        {
            return;
        }

        try
        {
            NativeDropTargetComInterop.Revoke(_hwnd);
        }
        catch
        {
        }
        _registered = false;
    }

    public void Dispose()
    {
        _disposed = true;
        Unregister();
        DragEnterEvent = null;
        DragOverEvent = null;
        DragLeaveEvent = null;
        DropEvent = null;
        DropIntentEvent = null;
        UndisplayableDropBlocked = null;
        ZeroResultDropBlocked = null;
        ImportBusyDropBlocked = null;
        UndisplayablePathProbe = null;
        SameVolumeProbe = null;
        SameVolumeWithBudgetProbe = null;
        HasMappedFolderProbe = null;
        TargetKindProbe = null;
        ImportBusyProbe = null;
        LaunchTargetPathProbe = null;
        LaunchDropHandler = null;
        _defaultMoveProvider = static () => true;
        _followWindowsProvider = null;
        _dropDescriptionProvider = null;
        _useShellVisualProvider = null;
    }

#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
    internal nint AcquireAotSmokeInterfacePointer()
    {
        return NativeDropTargetComInterop.AcquireInterfacePointer(_comObject);
    }
#endif

    internal int OnDragEnter(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        CallbackDepth.Enter();
        try
        {
            return OnDragEnterCore(dataObject, keyState, point, ref effect);
        }
        finally
        {
            CallbackDepth.Exit();
        }
    }

    internal static uint AugmentWin10BrokeredModifierKeyState(
        uint keyState,
        bool? isWindows11OrLater = null,
        Func<Windows.System.VirtualKey, bool>? isKeyPressed = null)
    {
        // On Windows 10, WinRT/UWP brokered drags arrive at IDropTarget with
        // grfKeyState == 0 because DataExchange.dll strips mouse/modifier state.
        // Supplement Ctrl/Shift/Alt from GetAsyncKeyState only when keyState is 0
        // on Win10 so classic Win32 OLE drags (which populate accurate keyState)
        // stay untouched.
        if (keyState != 0 || (isWindows11OrLater ?? WindowsCompatibilityService.IsWindows11OrLater))
        {
            return keyState;
        }

        Func<Windows.System.VirtualKey, bool> probe = isKeyPressed ?? Win32Helper.IsAsyncKeyPressed;
        uint augmented = 0;
        if (probe(Windows.System.VirtualKey.Control))
        {
            augmented |= NativeDropEffectPolicy.ControlKeyState;
        }

        if (probe(Windows.System.VirtualKey.Shift))
        {
            augmented |= NativeDropEffectPolicy.ShiftKeyState;
        }

        if (probe(Windows.System.VirtualKey.Menu))
        {
            augmented |= NativeDropEffectPolicy.AltKeyState;
        }

        return augmented;
    }

    private int OnDragEnterCore(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        keyState = AugmentWin10BrokeredModifierKeyState(keyState);
        EndNativeDragVisual();
        _activeGestureId = AllocateGestureId();
        _rightButtonDragActive =
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        uint allowedEffects = effect;
        InspectDragData(dataObject);
        bool canProbeHDropHintsOnHover = ShouldProbeHDropHintsOnHover(
            HasFileData,
            HasVirtualFileData,
            _hasVirtualDescriptorData,
            _isAsyncCapableSource,
            _hasShellIdListFormat,
            ActiveDeskBoxDragRegistry.HasActiveDrag,
            DeskBoxDataPathService.Current.IsDevelopmentRoot);
        _dragPathHints = canProbeHDropHintsOnHover
            ? TryExtractHDropPathHints(dataObject)
            : [];
        DragEnterEvent?.Invoke(point.X, point.Y, HasFileData);

        SourceOriginSnapshot sourceOrigin = _cachedDragSourceOriginSnapshot ??=
            ClassifyHoverOrDropSources(_dragPathHints);
        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            HasShellApplicationData || HasShellNamespaceData,
            _defaultMoveProvider(),
            followWindows: GetFollowWindowsSetting(),
            sameVolume: GetSameVolume(_dragPathHints),
            protectDefaultMoveAsCopy: sourceOrigin.IsProtectedCopyByDefault,
            hasMappedFolder: GetHasMappedFolder(),
            targetKind: GetTargetKind(),
            domain: ResolveDragDomain(_dragPathHints),
            sourceOrigin: sourceOrigin);
        if (HasFileData)
        {
            RetainActiveDataObject(dataObject);
            UpdateShellVisual(point, effect);
        }
        return S_OK;
    }

    internal int OnDragOver(
        uint keyState,
        POINT point,
        ref uint effect)
    {
        CallbackDepth.Enter();
        try
        {
            return OnDragOverCore(keyState, point, ref effect);
        }
        finally
        {
            CallbackDepth.Exit();
        }
    }

    private int OnDragOverCore(
        uint keyState,
        POINT point,
        ref uint effect)
    {
        keyState = AugmentWin10BrokeredModifierKeyState(keyState);
        uint allowedEffects = effect;
        _rightButtonDragActive |=
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        DragOverEvent?.Invoke(point.X, point.Y);

        SourceOriginSnapshot sourceOrigin = _cachedDragSourceOriginSnapshot ??=
            ClassifyHoverOrDropSources(_dragPathHints);
        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            HasShellApplicationData || HasShellNamespaceData,
            _defaultMoveProvider(),
            followWindows: GetFollowWindowsSetting(),
            sameVolume: GetSameVolume(_dragPathHints),
            protectDefaultMoveAsCopy: sourceOrigin.IsProtectedCopyByDefault,
            hasMappedFolder: GetHasMappedFolder(),
            targetKind: GetTargetKind(),
            domain: ResolveDragDomain(_dragPathHints),
            sourceOrigin: sourceOrigin);
        UpdateShellVisual(point, effect);
        return S_OK;
    }

    internal int OnDragLeave()
    {
        CallbackDepth.Enter();
        try
        {
            return OnDragLeaveCore();
        }
        finally
        {
            CallbackDepth.Exit();
        }
    }

    private int OnDragLeaveCore()
    {
        EndNativeDragVisual();
        ResetDragDataState();
        _rightButtonDragActive = false;
        DragLeaveEvent?.Invoke();
        return S_OK;
    }

    internal int OnDrop(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        CallbackDepth.Enter();
        try
        {
            return OnDropCore(dataObject, keyState, point, ref effect);
        }
        finally
        {
            CallbackDepth.Exit();
        }
    }

    private int OnDropCore(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
        keyState = AugmentWin10BrokeredModifierKeyState(keyState);
        long gestureId = _activeGestureId != 0 ? _activeGestureId : AllocateGestureId();
        uint allowedEffects = effect;
        bool shellApplicationDrop = HasShellApplicationData;
        bool shellNamespaceDrop = HasShellNamespaceData;
        bool virtualFileDrop = HasVirtualFileData;
        bool defaultMove = _defaultMoveProvider();
        bool followWindows = GetFollowWindowsSetting();
        bool rightButtonDrag = _rightButtonDragActive ||
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        SourceOriginSnapshot hintOrigin = ClassifyHoverOrDropSources(_dragPathHints);
        TransferTargetKind targetKind = GetTargetKind();
        TransferDomain hintDomain = ResolveDragDomain(_dragPathHints);
        uint feedbackEffect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            hasFileData: true,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            shellApplicationDrop || shellNamespaceDrop,
            defaultMove,
            followWindows: followWindows,
            sameVolume: GetSameVolume(_dragPathHints),
            protectDefaultMoveAsCopy: hintOrigin.IsProtectedCopyByDefault,
            hasMappedFolder: GetHasMappedFolder(),
            targetKind: targetKind,
            domain: hintDomain,
            sourceOrigin: hintOrigin);
        if (targetKind == TransferTargetKind.UnsupportedSurface)
        {
            if (_shellVisualActive)
            {
                NativeDropImageManager.DragLeave(this);
                _shellVisualActive = false;
                _shellVisualShown = false;
            }

            ClearActiveDropDescriptionAndReleaseDataObject();
            ResetDragDataState();
            _rightButtonDragActive = false;
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            WriteLogicalPerformedDropEffect(dataObject, effect);
            return S_OK;
        }

        if ((virtualFileDrop || shellApplicationDrop || shellNamespaceDrop) &&
            dataObject != 0)
        {
            // Virtual-only drops (browser downloads, shell virtual files),
            // shell-application links, and materializable Shell namespace
            // objects (Recycle Bin, This PC, ...) all pay their extraction
            // inside the source's drag loop — cross-process FILECONTENTS
            // streams, or PIDL parsing plus .lnk materialization. All leave
            // the OLE callback frame entirely, per the shell's asynchronous
            // drop protocol (IDataObjectAsyncCapability): negotiate
            // StartOperation before returning, extract on the dedicated MTA
            // worker or the UI STA (shell namespace APIs fail on MTA),
            // write the receipt and EndOperation after extraction, and only
            // then run the launch/import continuation on the UI thread.
            return HandleDeferredDropMaterialization(
                dataObject,
                keyState,
                point,
                ref effect,
                allowedEffects,
                feedbackEffect,
                rightButtonDrag,
                defaultMove,
                followWindows,
                shellApplicationDrop,
                shellNamespaceDrop,
                gestureId);
        }

        IReadOnlyList<string> paths = [];
        IReadOnlyList<DroppedItemOriginKind> itemOrigins = [];
        bool createdShellApplicationLinks = false;
        bool containsTemporaryFiles = false;
        bool hadVirtualDescriptorData = _hasVirtualDescriptorData;
        bool hadShellIdListFormat = _hasShellIdListFormat;
        bool hadAsyncCapableSource = _isAsyncCapableSource;
        (paths, itemOrigins, containsTemporaryFiles) = TryExtractFilePathsWithOrigins(
            dataObject,
            hadShellIdListFormat,
            hadVirtualDescriptorData || hadAsyncCapableSource,
            out bool deferVirtualFallback,
            out int syncSkippedCount);
        if (deferVirtualFallback && dataObject != 0)
        {
            uint fallbackFeedbackEffect = NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: true,
                keyState,
                allowedEffects,
                hasShellApplicationData: false,
                defaultMove,
                followWindows: followWindows,
                sameVolume: GetSameVolume(_dragPathHints),
                hasMappedFolder: GetHasMappedFolder(),
                targetKind: targetKind,
                domain: hintDomain,
                sourceOrigin: hintOrigin);
            return HandleDeferredDropMaterialization(
                dataObject,
                keyState,
                point,
                ref effect,
                allowedEffects,
                fallbackFeedbackEffect,
                rightButtonDrag,
                defaultMove,
                followWindows,
                shellApplicationDrop: false,
                shellNamespaceDrop: false,
                gestureId: gestureId);
        }

        // The transfer decision must respect the allowed effects the source
        // announced (the same constraint the feedback effect applies). A
        // copy-only source whose files are moved would delete originals the
        // source never sanctioned for deletion.
        IReadOnlyList<string> effectivePaths = paths.Count > 0 ? paths : _dragPathHints;
        SourceOriginSnapshot dropOrigin = ClassifyHoverOrDropSources(
            effectivePaths,
            PathOriginClassifier.DefaultDropProbeBudget);
        TransferDomain dropDomain = ResolveDragDomain(effectivePaths);
        bool allItemsTemporary = itemOrigins.Count > 0
            ? itemOrigins.All(static kind => kind != DroppedItemOriginKind.RealFileSystemPath)
            : containsTemporaryFiles;
        bool sourceCanCopy = !dropOrigin.HasAppExecutionAliasSource &&
            (dropDomain == TransferDomain.InternalDeskBox ||
             (allowedEffects & NativeDropEffectPolicy.Copy) != 0);
        bool sourceCanMove = !allItemsTemporary &&
            !dropOrigin.HasAppExecutionAliasSource &&
            !dropOrigin.IsMoveProtectedSystemOrSharedSource &&
            (dropDomain == TransferDomain.InternalDeskBox ||
             (allowedEffects & NativeDropEffectPolicy.Move) != 0);
        bool dropSameVolume = GetSameVolume(
            effectivePaths,
            PathOriginClassifier.DefaultDropProbeBudget);
        bool copyRequested = NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            allItemsTemporary,
            keyState,
            defaultMove,
            followWindows: followWindows,
            sameVolume: dropSameVolume,
            canCopy: sourceCanCopy,
            canMove: sourceCanMove,
            protectDefaultMoveAsCopy: dropOrigin.IsProtectedCopyByDefault,
            sourceOrigin: dropOrigin,
            domain: dropDomain);
        bool shortcutRequested = createdShellApplicationLinks ||
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                allItemsTemporary,
                keyState,
                followWindows: followWindows,
                allowedEffects: allowedEffects,
                sourceOrigin: dropOrigin);
        uint hoverFeedbackEffect = feedbackEffect;
        feedbackEffect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            hasFileData: true,
            hasVirtualFileData: allItemsTemporary,
            keyState,
            allowedEffects,
            hasShellApplicationData: shellApplicationDrop || shellNamespaceDrop,
            defaultMove,
            followWindows: followWindows,
            sameVolume: dropSameVolume,
            hasMappedFolder: GetHasMappedFolder(),
            targetKind: targetKind,
            domain: dropDomain,
            sourceOrigin: dropOrigin);
        App.Log($"[DropTarget] SourceDecision gesture={gestureId} hintCount={_dragPathHints.Count} " +
            $"pathCount={effectivePaths.Count} hintProtection={hintOrigin.InboundProtection} " +
            $"dropProtection={dropOrigin.InboundProtection} moveProtected={dropOrigin.IsMoveProtectedSystemOrSharedSource} " +
            $"hoverEffect={hoverFeedbackEffect} dropEffect={feedbackEffect} copyRequested={copyRequested}");
        if (_shellVisualActive)
        {
            NativeDropImageManager.Drop(
                this,
                dataObject,
                point,
                feedbackEffect);
        }
        ClearActiveDropDescriptionAndReleaseDataObject();
        _shellVisualActive = false;
        _shellVisualShown = false;
        ResetDragDataState();
        _rightButtonDragActive = false;

        // Always log so we can tell whether the native OLE drop target receives
        // CF_HDROP drops (WeChat / Explorer) at all, and what it extracted.
        App.Log(
            $"[DropTarget] NativeDrop received count={paths.Count} " +
            $"temp={containsTemporaryFiles} shellApps={createdShellApplicationLinks} " +
            $"allowed={allowedEffects} feedback={feedbackEffect} " +
            $"keyState={keyState} virtual={virtualFileDrop} " +
            $"defaultMove={defaultMove} copyRequested={copyRequested}");
        App.LogVerbose(
            $"[DropTarget] NativeDrop paths={string.Join(" | ", paths)}");

        // A launch consumer (drop onto an application shortcut) delegates to the
        // Shell drop target synchronously while the source data object is alive.
        // Handled=true means the drop never becomes an import. Consumed=true
        // means a shortcut owned the gesture and did not launch it: refuse at
        // the OLE level so the drag source keeps its files, and never let the
        // import pipeline run - importing relocates the user's files.
        if (paths.Count > 0 &&
            LaunchDropHandler is { } launchHandler &&
            !shellApplicationDrop)
        {
            ShellDropLaunchResult launch = launchHandler(new NativeDropLaunchRequest(
                paths,
                dataObject,
                keyState,
                point.X,
                point.Y,
                allowedEffects,
                rightButtonDrag,
                containsTemporaryFiles));
            if (launch.Consumed)
            {
                if (containsTemporaryFiles)
                {
                    IReadOnlyList<string> refusedPaths = paths;
                    _ = Task.Run(() => NativeDropMaterializationWorker
                        .CleanupVirtualDropDirectories(refusedPaths));
                }

                effect = NativeDropEffectPolicy.None;
                WritePerformedDropEffect(dataObject, effect);
                WriteLogicalPerformedDropEffect(dataObject, effect);
                return S_OK;
            }

            if (launch.Handled)
            {
                effect = launch.Effect;
                WritePerformedDropEffect(dataObject, effect);
                WriteLogicalPerformedDropEffect(dataObject, effect);
                return S_OK;
            }
        }

        // Refuse the entire gesture before committing an effect when any real
        // (non-virtual, non-app-link) path can never be displayed by the
        // receiving surface. A partial acceptance cannot be expressed in the
        // OLE completion effect: returning Move after skipping entries would
        // let the source delete files the import never took.
        if (paths.Count > 0 &&
            !shellApplicationDrop &&
            !containsTemporaryFiles &&
            UndisplayablePathProbe is { } undisplayableProbe)
        {
            int undisplayableCount = paths.Count(undisplayableProbe);
            if (undisplayableCount > 0)
            {
                App.Log(
                    $"[DropTarget] Refused undisplayable drop count={paths.Count} " +
                    $"undisplayable={undisplayableCount}");
                try
                {
                    UndisplayableDropBlocked?.Invoke(undisplayableCount);
                }
                catch (Exception ex)
                {
                    App.Log($"[DropTarget] Undisplayable-drop notify failed: {ex.Message}");
                }

                effect = NativeDropEffectPolicy.None;
                WritePerformedDropEffect(dataObject, effect);
                WriteLogicalPerformedDropEffect(dataObject, effect);
                return S_OK;
            }
        }

        // A busy import pipeline cannot accept a drop: refuse it before the
        // completion effect commits so the source is never told the drop
        // succeeded while the files are silently skipped.
        if (paths.Count > 0 && IsImportBusy())
        {
            App.Log(
                $"[DropTarget] Refused drop: import pipeline busy " +
                $"count={paths.Count}");
            if (containsTemporaryFiles)
            {
                IReadOnlyList<string> refusedPaths = paths;
                _ = Task.Run(() => NativeDropMaterializationWorker
                    .CleanupVirtualDropDirectories(refusedPaths));
            }

            NotifyImportBusyRefused();
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            WriteLogicalPerformedDropEffect(dataObject, effect);
            return S_OK;
        }

        if (paths.Count > 0 && (rightButtonDrag || feedbackEffect != NativeDropEffectPolicy.None))
        {
            DropIntentEvent?.Invoke(new NativeDropIntentEventArgs(
                paths,
                point.X,
                point.Y,
                containsTemporaryFiles,
                copyRequested,
                shortcutRequested,
                rightButtonDrag,
                feedbackEffect,
                keyState,
                allowedEffects,
                gestureId,
                itemOrigins,
                syncSkippedCount));
            DropEvent?.Invoke(
                paths,
                point.X,
                point.Y,
                containsTemporaryFiles,
                copyRequested);
            effect = NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects,
                createdShellApplicationLinks || shortcutRequested);
        }
        else
        {
            if (paths.Count > 0 && containsTemporaryFiles)
            {
                IReadOnlyList<string> refusedPaths = paths;
                _ = Task.Run(() => NativeDropMaterializationWorker
                    .CleanupVirtualDropDirectories(refusedPaths));
            }
            else if (paths.Count == 0 && syncSkippedCount > 0)
            {
                App.Log(
                    $"[DropTarget] Sync staging produced zero items (skipped={syncSkippedCount}); " +
                    "notifying zero-result drop blocked.");
                try
                {
                    ZeroResultDropBlocked?.Invoke();
                }
                catch (Exception ex)
                {
                    App.Log($"[DropTarget] Zero-result notify failed: {ex.Message}");
                }
            }

            effect = NativeDropEffectPolicy.None;
        }

        WritePerformedDropEffect(dataObject, effect);
        // Never advertise Logical Performed DropEffect = Move before the async
        // import actually finishes (user may still cancel a conflict dialog).
        WriteLogicalPerformedDropEffect(dataObject, effect);
        return S_OK;
    }

    /// <summary>
    /// The deferred half of the drop for virtual-only data and
    /// shell-application links: hands the data object to the materialization
    /// worker and returns from the OLE callback without extracting anything.
    /// Runs inside OnDropCore (callback frame); the only heavy work done here
    /// is the async-capability handshake, whose StartOperation call is only
    /// valid exactly here — before the callback returns.
    /// </summary>
    private int HandleDeferredDropMaterialization(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect,
        uint allowedEffects,
        uint feedbackEffect,
        bool rightButtonDrag,
        bool defaultMove,
        bool followWindows,
        bool shellApplicationDrop,
        bool shellNamespaceDrop,
        long gestureId = 0)
    {
        // Capacity is checked BEFORE the marshal: a refused stream that is
        // never unmarshaled would leak the marshal packet plus its hold on
        // the source object (CoReleaseMarshalData on a hung source would
        // block the UI thread, so the packet is simply never created).
        if (!NativeDropMaterializationWorker.CanAcceptJob)
        {
            App.Log(
                "[DropTarget] Refused deferred drop: materialization queue " +
                "is full");
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            WriteLogicalPerformedDropEffect(dataObject, effect);
            return S_OK;
        }

        // A busy import pipeline cannot accept the drop; refuse synchronously
        // with a none receipt so the source is never told the drop succeeded.
        if (IsImportBusy())
        {
            App.Log("[DropTarget] Refused virtual drop: import pipeline busy");
            NotifyImportBusyRefused();
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            WriteLogicalPerformedDropEffect(dataObject, effect);
            return S_OK;
        }

        // Capture the hover target now, inside the callback: the completion
        // only delegates to the launch handler when the hover target survived
        // the materialization window unchanged, so a later gesture's state
        // is never stamped by this drop's completion.
        string? launchTargetPathAtDrop =
            !shellApplicationDrop && !shellNamespaceDrop && !rightButtonDrag
                ? LaunchTargetPathProbe?.Invoke()
                : null;

        // StartOperation must happen before this callback returns: it is what
        // tells an async source (browser download drags) to keep serving
        // FILECONTENTS after DoDragDrop returns. Sources without the
        // capability take the plain marshal-and-worker path — the remote stub
        // manager keeps the source object alive while the marshaled
        // reference exists.
        bool asyncStarted = false;
        if (NativeDropAsyncCapability.TryQuery(dataObject, out NativeDropAsyncCapability capability))
        {
            try
            {
                if (capability.GetAsyncMode())
                {
                    if (capability.StartOperation() >= 0)
                    {
                        asyncStarted = true;
                        App.LogVerbose(
                            "[DropTarget] Async drop negotiated with the source");
                    }
                    else
                    {
                        // An async-capable source rejecting the handshake is
                        // the single most useful ecosystem-conflict signal
                        // (delayed-render CF_HDROP sources et al.); default
                        // level so field logs see it without verbose.
                        App.Log(
                            "[DropTarget] Async source rejected " +
                            "StartOperation; falling back to the marshal " +
                            "path.");
                    }
                }
            }
            finally
            {
                // The worker re-queries the capability for EndOperation on its
                // own apartment; this thread's reference ends here.
                _ = capability.Release();
            }
        }

        bool isVirtualStreamDrop = !shellApplicationDrop && !shellNamespaceDrop;
        bool sourceCanCopy = (allowedEffects & NativeDropEffectPolicy.Copy) != 0 ||
            (isVirtualStreamDrop && (allowedEffects & NativeDropEffectPolicy.Link) != 0);
        bool sourceCanMove = (allowedEffects & NativeDropEffectPolicy.Move) != 0;
        bool copyRequested = NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            containsTemporaryFiles: true,
            keyState,
            defaultMove,
            followWindows: followWindows,
            sameVolume: GetSameVolume(_dragPathHints),
            canCopy: sourceCanCopy,
            canMove: sourceCanMove);
        bool shortcutRequested = shellApplicationDrop || shellNamespaceDrop ||
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles: true,
                keyState);
        uint completionEffect = NativeDropEffectPolicy.ResolveCompletionEffect(
            hasExtractedPaths: true,
            allowedEffects,
            createdShellApplicationLinks: shortcutRequested,
            hasVirtualOnlyData: isVirtualStreamDrop);
        int dropX = point.X;
        int dropY = point.Y;
        uint capturedKeyState = keyState;
        uint capturedAllowedEffects = allowedEffects;
        uint capturedFeedbackEffect = feedbackEffect;
        bool capturedRightButtonDrag = rightButtonDrag;

        if (shellApplicationDrop || shellNamespaceDrop)
        {
            if (!asyncStarted)
            {
                // Non-async Shell IDList sources only guarantee IDataObject
                // validity until IDropTarget::Drop returns. Materialize
                // synchronously on the UI STA inside the Drop frame, write
                // receipts immediately, and hand the already-materialized paths
                // to the UI import pipeline via PostAfterCallback.
                IReadOnlyList<string> extractedPaths = [];
                IReadOnlyList<DroppedItemOriginKind> extractedOrigins = [];
                int shellSkippedCount = 0;
                uint committedEffect = NativeDropEffectPolicy.None;
                try
                {
                    extractedPaths = TryExtractShellIdListItems(
                        dataObject,
                        out extractedOrigins,
                        out shellSkippedCount);
                    committedEffect = extractedPaths.Count > 0
                        ? completionEffect
                        : NativeDropEffectPolicy.None;
                }
                catch (Exception ex)
                {
                    App.Log(
                        "[DropTarget] Sync shell-object extraction failed: " +
                        ex.Message);
                    extractedPaths = [];
                    extractedOrigins = [];
                    committedEffect = NativeDropEffectPolicy.None;
                }

                FinishDragVisualAndState(dataObject, point, feedbackEffect);
                effect = committedEffect;
                WritePerformedDropEffect(dataObject, committedEffect);
                WriteLogicalPerformedDropEffect(dataObject, committedEffect);
                App.Log(
                    $"[DropTarget] NativeDrop materialized non-async shell-object synchronously " +
                    $"count={extractedPaths.Count} skipped={shellSkippedCount}");
                PostShellIdListCompletion(
                    extractedPaths,
                    extractedOrigins,
                    shellSkippedCount,
                    dropX,
                    dropY,
                    copyRequested,
                    capturedKeyState,
                    capturedAllowedEffects,
                    capturedFeedbackEffect,
                    capturedRightButtonDrag,
                    defaultMove,
                    followWindows,
                    sourceCanCopy,
                    sourceCanMove,
                    shellApplicationDrop,
                    launchTargetPathAtDrop,
                    gestureId);
                return S_OK;
            }

            // Shell namespace APIs used by the AppsFolder and namespace-item
            // extraction (SHGetNameFromIDList & friends) bind in-proc shell
            // COM objects that fail on the MTA worker (2026-10-15 field
            // incident: start-menu drags extracted zero items on the worker).
            // Because asyncStarted == true, the source keeps the data object
            // alive until EndOperation is called after extraction.
            _ = System.Runtime.InteropServices.Marshal.AddRef(dataObject);
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            App.Log(
                "[DropTarget] NativeDrop deferred async shell-object extraction " +
                "to UI turn");
            UiDispatch.PostAfterCallback(() =>
            {
                IReadOnlyList<string> extractedPaths = [];
                IReadOnlyList<DroppedItemOriginKind> extractedOrigins = [];
                int shellSkippedCount = 0;
                try
                {
                    extractedPaths = TryExtractShellIdListItems(
                        dataObject,
                        out extractedOrigins,
                        out shellSkippedCount);
                    uint committedEffect = extractedPaths.Count > 0
                        ? completionEffect
                        : NativeDropEffectPolicy.None;
                    int operationResult = committedEffect != NativeDropEffectPolicy.None
                        ? S_OK
                        : unchecked((int)0x80004005);
                    WritePerformedDropEffect(dataObject, committedEffect);
                    WriteLogicalPerformedDropEffect(dataObject, committedEffect);
                    EndAsyncOperationIfStarted(
                        dataObject,
                        asyncStarted,
                        operationResult,
                        committedEffect);
                }
                catch (Exception ex)
                {
                    App.Log(
                        "[DropTarget] Deferred shell-app extraction failed: " +
                        ex.Message);
                    try
                    {
                        WritePerformedDropEffect(
                            dataObject,
                            NativeDropEffectPolicy.None);
                        WriteLogicalPerformedDropEffect(
                            dataObject,
                            NativeDropEffectPolicy.None);
                    }
                    catch
                    {
                        // Best effort; the source keeps its files.
                    }

                    EndAsyncOperationIfStarted(dataObject, asyncStarted);
                }
                finally
                {
                    _ = System.Runtime.InteropServices.Marshal.Release(dataObject);
                }

                CompleteShellIdListExtraction(
                    extractedPaths,
                    extractedOrigins,
                    shellSkippedCount,
                    dropX,
                    dropY,
                    copyRequested,
                    capturedKeyState,
                    capturedAllowedEffects,
                    capturedFeedbackEffect,
                    capturedRightButtonDrag,
                    defaultMove,
                    followWindows,
                    sourceCanCopy,
                    sourceCanMove,
                    shellApplicationDrop,
                    launchTargetPathAtDrop,
                    gestureId);
            });

            effect = completionEffect;
            return S_OK;
        }

        if (!asyncStarted)
        {
            // Non-async virtual sources (archivers, mail clients, legacy apps
            // that do not advertise IDataObjectAsyncCapability) invalidate their
            // IStream handles or purge temporary staging folders as soon as
            // IDropTarget::Drop returns. Materialize synchronously before
            // returning S_OK, then dispatch the import continuation via
            // PostAfterCallback outside the OLE callback frame.
            IReadOnlyList<string> extractedPaths = [];
            IReadOnlyList<DroppedItemOriginKind> syncOrigins = [];
            int syncMaterializationSkipped = 0;
            uint committedEffect = NativeDropEffectPolicy.None;
            try
            {
                extractedPaths = ExtractDeferredWorkerFilesWithOrigins(
                    new NativeOleDataObject(dataObject),
                    out syncOrigins,
                    out syncMaterializationSkipped);
                committedEffect = extractedPaths.Count > 0
                    ? completionEffect
                    : NativeDropEffectPolicy.None;
            }
            catch (Exception ex)
            {
                App.Log(
                    "[DropTarget] Sync virtual-file extraction failed: " +
                    ex.Message);
                extractedPaths = [];
                syncOrigins = [];
                committedEffect = NativeDropEffectPolicy.None;
            }

            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            effect = committedEffect;
            WritePerformedDropEffect(dataObject, committedEffect);
            WriteLogicalPerformedDropEffect(dataObject, committedEffect);
            App.Log(
                $"[DropTarget] NativeDrop materialized non-async virtual payload synchronously " +
                $"count={extractedPaths.Count} skipped={syncMaterializationSkipped}");
            IReadOnlyList<string> capturedPaths = extractedPaths;
            IReadOnlyList<DroppedItemOriginKind> capturedOrigins = syncOrigins;
            int capturedSkipped = syncMaterializationSkipped;
            UiDispatch.PostAfterCallback(() => OnDeferredMaterializationCompleted(
                capturedPaths,
                dropX,
                dropY,
                copyRequested,
                shortcutRequested,
                capturedKeyState,
                capturedAllowedEffects,
                capturedFeedbackEffect,
                capturedRightButtonDrag,
                shellApplicationDrop,
                launchTargetPathAtDrop,
                containsTemporaryFiles: true,
                gestureId: gestureId,
                itemOrigins: capturedOrigins,
                materializationSkippedCount: capturedSkipped));
            return S_OK;
        }

        nint marshalStream = 0;
        try
        {
            int hr = Win32Helper.CoMarshalInterThreadInterfaceInStream(
                in Win32Helper.IDataObjectComInterfaceId,
                dataObject,
                out marshalStream);
            if (hr < 0 || marshalStream == 0)
            {
                App.Log(
                    $"[DropTarget] Virtual drop marshal failed hr=0x{hr:X8}");
                FinishDragVisualAndState(dataObject, point, feedbackEffect);
                effect = NativeDropEffectPolicy.None;
                WritePerformedDropEffect(dataObject, effect);
                EndAsyncOperationIfStarted(dataObject, asyncStarted);
                return S_OK;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Virtual drop marshal threw: {ex.Message}");
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            EndAsyncOperationIfStarted(dataObject, asyncStarted);
            return S_OK;
        }

        if (!NativeDropMaterializationWorker.TryEnqueue(
                marshalStream,
                asyncStarted,
                completionEffect,
                completionEffect,
                (extractedPaths, workerOrigins, workerSkippedCount) => OnDeferredMaterializationCompleted(
                    extractedPaths,
                    dropX,
                    dropY,
                    copyRequested,
                    shortcutRequested,
                    capturedKeyState,
                    capturedAllowedEffects,
                    capturedFeedbackEffect,
                    capturedRightButtonDrag,
                    shellApplicationDrop,
                    launchTargetPathAtDrop,
                    containsTemporaryFiles: true,
                    gestureId: gestureId,
                    itemOrigins: workerOrigins,
                    materializationSkippedCount: workerSkippedCount),
                confirmAcceptBeforeReceipt: () =>
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    if (IsImportBusy())
                    {
                        App.Log("[DropTarget] Refused deferred virtual drop before receipt: import pipeline busy.");
                        NotifyImportBusyRefused();
                        return false;
                    }

                    return true;
                }))
        {
            // Unreachable in practice (capacity pre-check above; only the UI
            // thread enqueues), kept as a hard stop: the marshal packet is
            // abandoned rather than risk a blocking rewind on a hung source.
            App.Log(
                "[DropTarget] Refused deferred drop after marshal: queue " +
                "full; marshal packet abandoned");
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            effect = NativeDropEffectPolicy.None;
            WritePerformedDropEffect(dataObject, effect);
            EndAsyncOperationIfStarted(dataObject, asyncStarted);
            return S_OK;
        }

        FinishDragVisualAndState(dataObject, point, feedbackEffect);
        App.Log("[DropTarget] NativeDrop deferred virtual materialization to worker");

        // The OLE effect commits optimistically (copy semantics — a receipt
        // never lets a virtual source delete anything); the worker rewrites
        // the final receipt after extraction really finished.
        effect = completionEffect;
        return S_OK;
    }

    private void PostShellIdListCompletion(
        IReadOnlyList<string> extractedPaths,
        IReadOnlyList<DroppedItemOriginKind> extractedOrigins,
        int shellSkippedCount,
        int dropX,
        int dropY,
        bool copyRequested,
        uint capturedKeyState,
        uint capturedAllowedEffects,
        uint capturedFeedbackEffect,
        bool capturedRightButtonDrag,
        bool defaultMove,
        bool followWindows,
        bool sourceCanCopy,
        bool sourceCanMove,
        bool shellApplicationDrop,
        string? launchTargetPathAtDrop,
        long gestureId)
    {
        UiDispatch.PostAfterCallback(() => CompleteShellIdListExtraction(
            extractedPaths,
            extractedOrigins,
            shellSkippedCount,
            dropX,
            dropY,
            copyRequested,
            capturedKeyState,
            capturedAllowedEffects,
            capturedFeedbackEffect,
            capturedRightButtonDrag,
            defaultMove,
            followWindows,
            sourceCanCopy,
            sourceCanMove,
            shellApplicationDrop,
            launchTargetPathAtDrop,
            gestureId));
    }

    private void CompleteShellIdListExtraction(
        IReadOnlyList<string> extractedPaths,
        IReadOnlyList<DroppedItemOriginKind> extractedOrigins,
        int shellSkippedCount,
        int dropX,
        int dropY,
        bool copyRequested,
        uint capturedKeyState,
        uint capturedAllowedEffects,
        uint capturedFeedbackEffect,
        bool capturedRightButtonDrag,
        bool defaultMove,
        bool followWindows,
        bool sourceCanCopy,
        bool sourceCanMove,
        bool shellApplicationDrop,
        string? launchTargetPathAtDrop,
        long gestureId)
    {
        IReadOnlyList<DroppedItemOriginKind> shellOrigins =
            extractedOrigins.Count == extractedPaths.Count
                ? extractedOrigins
                : ClassifyShellIdListExtractedOrigins(extractedPaths);
        bool hasShellTemporaryLinks = shellOrigins.Any(
            static origin => origin == DroppedItemOriginKind.ShellNamespaceLinkOnly);
        bool hasShellTemporaryStreams = shellOrigins.Any(
            static origin => origin == DroppedItemOriginKind.TransientArchiveOrCache);
        bool containsShellTemporaryEntries = hasShellTemporaryLinks || hasShellTemporaryStreams;
        SourceOriginSnapshot shellOriginSnapshot = ClassifyHoverOrDropSources(
            extractedPaths,
            PathOriginClassifier.DefaultDropProbeBudget);
        (bool shellCopyRequested, bool shellShortcutRequested) =
            ResolveShellIdListPostExtractionIntent(
                shellApplicationDrop,
                hasShellTemporaryLinks,
                hasShellTemporaryStreams,
                capturedRightButtonDrag,
                capturedFeedbackEffect,
                copyRequested,
                capturedKeyState,
                defaultMove,
                followWindows,
                sameVolume: GetSameVolume(
                    extractedPaths,
                    PathOriginClassifier.DefaultDropProbeBudget),
                sourceCanCopy,
                sourceCanMove: !shellOriginSnapshot.IsMoveProtectedSystemOrSharedSource && sourceCanMove,
                protectDefaultMoveAsCopy: shellOriginSnapshot.IsProtectedCopyByDefault);

        OnDeferredMaterializationCompleted(
            extractedPaths,
            dropX,
            dropY,
            shellCopyRequested,
            shellShortcutRequested,
            capturedKeyState,
            capturedAllowedEffects,
            capturedFeedbackEffect,
            capturedRightButtonDrag,
            shellApplicationDrop,
            launchTargetPathAtDrop,
            containsShellTemporaryEntries,
            gestureId,
            shellOrigins,
            shellSkippedCount);
    }

    /// <summary>
    /// Ensures Shell IDList-only drops that advertised and receipted Link or Copy
    /// during hover/Drop never silently upgrade into a destructive Move when the
    /// extracted PIDLs turn out to point at real filesystem items (FL-05).
    /// </summary>
    internal static (bool CopyRequested, bool ShortcutRequested) ResolveShellIdListPostExtractionIntent(
        bool shellApplicationDrop,
        bool hasShellTemporaryLinks,
        bool hasShellTemporaryStreams,
        bool rightButtonDrag,
        uint feedbackEffect,
        bool defaultCopyRequested,
        uint keyState,
        bool defaultMove,
        bool followWindows,
        bool sameVolume,
        bool sourceCanCopy,
        bool sourceCanMove,
        bool protectDefaultMoveAsCopy)
    {
        if (shellApplicationDrop || hasShellTemporaryLinks)
        {
            return (CopyRequested: true, ShortcutRequested: true);
        }

        if (hasShellTemporaryStreams)
        {
            return (CopyRequested: true, ShortcutRequested: false);
        }

        if (!rightButtonDrag)
        {
            if (feedbackEffect == NativeDropEffectPolicy.Link)
            {
                return (CopyRequested: true, ShortcutRequested: true);
            }

            if (feedbackEffect == NativeDropEffectPolicy.Copy)
            {
                return (CopyRequested: true, ShortcutRequested: false);
            }
        }

        bool copyRequested = NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            containsTemporaryFiles: false,
            keyState,
            defaultMove,
            followWindows: followWindows,
            sameVolume: sameVolume,
            canCopy: sourceCanCopy,
            canMove: sourceCanMove,
            protectDefaultMoveAsCopy: protectDefaultMoveAsCopy);
        bool shortcutRequested = NativeDropEffectPolicy.ShouldCreateMappedShortcut(
            containsTemporaryFiles: false,
            keyState);
        return (copyRequested, shortcutRequested);
    }

    /// <summary>
    /// The shared drag-visual/state teardown every exit of the drop path must
    /// run — success, refusal, and failure alike. Skipping it on a refusal
    /// keeps the DragEnter AddRef on the data object alive until the next
    /// drag (or window close), pinning the source's object for no reason.
    /// </summary>
    private void FinishDragVisualAndState(nint dataObject, POINT point, uint feedbackEffect)
    {
        if (_shellVisualActive)
        {
            NativeDropImageManager.Drop(this, dataObject, point, feedbackEffect);
        }

        ClearActiveDropDescriptionAndReleaseDataObject();
        _shellVisualActive = false;
        _shellVisualShown = false;
        ResetDragDataState();
        _rightButtonDragActive = false;
    }

    private static void EndAsyncOperationIfStarted(
        nint dataObject,
        bool asyncStarted,
        int operationResult = unchecked((int)0x80004005),
        uint finalEffect = NativeDropEffectPolicy.None)
    {
        if (!asyncStarted)
        {
            return;
        }

        if (NativeDropAsyncCapability.TryQuery(dataObject, out NativeDropAsyncCapability capability))
        {
            try
            {
                _ = capability.EndOperation(
                    operationResult,
                    finalEffect);
            }
            finally
            {
                _ = capability.Release();
            }
        }
    }

    /// <summary>
    /// Lands on the UI thread after the worker finished materializing. Runs
    /// the same gating the synchronous path ran inside the callback: the
    /// shortcut-launch consumer first (never for shell-application links —
    /// the synchronous path excluded them from launching too), then the
    /// import events. The receipt and EndOperation were already written by
    /// the worker before this runs.</summary>
    private void OnDeferredMaterializationCompleted(
        IReadOnlyList<string> extractedPaths,
        int dropX,
        int dropY,
        bool copyRequested,
        bool shortcutRequested,
        uint keyState,
        uint allowedEffects,
        uint feedbackEffect,
        bool rightButtonDrag,
        bool shellApplicationDrop,
        string? launchTargetPathAtDrop,
        bool containsTemporaryFiles = true,
        long gestureId = 0,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null,
        int materializationSkippedCount = 0)
    {
        if (extractedPaths.Count == 0)
        {
            // Contract ⑫ close-out: the hover promised a link/copy the
            // deferred extraction could not honor; surface it as feedback
            // instead of failing silently.
            try
            {
                ZeroResultDropBlocked?.Invoke();
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DropTarget] Zero-result notify failed: {ex.Message}");
            }

            return;
        }

        // Launch delegation only when the hover target survived the
        // materialization window unchanged: a newer gesture may have replaced
        // it, and stamping consumption windows from THIS drop's completion
        // would swallow the newer gesture's release.
        if (!shellApplicationDrop &&
            launchTargetPathAtDrop is not null &&
            LaunchTargetPathProbe?.Invoke() == launchTargetPathAtDrop &&
            LaunchDropHandler is { } launchHandler &&
            !rightButtonDrag)
        {
            ShellDropLaunchResult launch = launchHandler(new NativeDropLaunchRequest(
                extractedPaths,
                DataObject: 0,
                keyState,
                dropX,
                dropY,
                allowedEffects,
                RightButtonDrag: false,
                ContainsTemporaryFiles: containsTemporaryFiles));
            if (launch.Consumed || launch.Handled)
            {
                if (!launch.Handled && containsTemporaryFiles)
                {
                    IReadOnlyList<string> unlaunchedPaths = extractedPaths;
                    _ = Task.Run(() => NativeDropMaterializationWorker
                        .CleanupVirtualDropDirectories(unlaunchedPaths));
                }

                return;
            }
        }

        if (IsImportBusy())
        {
            App.Log(
                $"[DropTarget] Refused deferred drop: import pipeline " +
                $"busy count={extractedPaths.Count}");
            NotifyImportBusyRefused();
            if (containsTemporaryFiles)
            {
                // The worker already receipted optimistically; the least-bad
                // recovery is deleting the materialized files so they do not
                // linger forever in the temp root.
                IReadOnlyList<string> refusedPaths = extractedPaths;
                _ = Task.Run(() => NativeDropMaterializationWorker
                    .CleanupVirtualDropDirectories(refusedPaths));
            }

            return;
        }

        DropIntentEvent?.Invoke(new NativeDropIntentEventArgs(
            extractedPaths,
            dropX,
            dropY,
            containsTemporaryFiles,
            copyRequested,
            shortcutRequested,
            rightButtonDrag,
            feedbackEffect,
            keyState,
            allowedEffects,
            gestureId,
            itemOrigins,
            materializationSkippedCount));
        DropEvent?.Invoke(
            extractedPaths,
            dropX,
            dropY,
            containsTemporaryFiles,
            copyRequested);
    }

    private bool GetFollowWindowsSetting()
    {
        try
        {
            return _followWindowsProvider?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    // The OLE drop protocol asks the target to record the effect it
    // actually performed back onto the source's data object. Explorer reads
    // this receipt when present; our own guarded data object consumes it.
    // Internal for the materialization worker, which writes the receipt on
    // its own thread after extraction completes (async-drop protocol).
    internal static void WritePerformedDropEffect(nint dataObject, uint effect) =>
        WriteDropEffectFormat(dataObject, s_performedDropEffectFormat, effect);

    internal static void WriteLogicalPerformedDropEffect(nint dataObject, uint logicalEffect) =>
        WriteDropEffectFormat(dataObject, s_logicalPerformedDropEffectFormat, logicalEffect);

    private static void WriteDropEffectFormat(nint dataObject, ushort formatId, uint effect)
    {
        if (dataObject == 0 || formatId == 0)
        {
            return;
        }

        Span<byte> payload = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, effect);
        if (!Win32Helper.TryCreateGlobalMemory(payload, out nint buffer))
        {
            return;
        }

        try
        {
            var format = new NativeFormatEtc
            {
                ClipboardFormat = formatId,
                TargetDevice = 0,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL
            };
            var medium = new NativeStorageMedium
            {
                MediumType = TYMED_HGLOBAL,
                Content = buffer,
                ReleaseUnknown = 0
            };
            int result = new NativeOleDataObject(dataObject)
                .SetData(ref format, ref medium, release: true);
            if (result < 0)
            {
                Win32Helper.FreeGlobalMemory(buffer);
            }
        }
        catch
        {
            Win32Helper.FreeGlobalMemory(buffer);
        }
    }

    private bool GetSameVolume(
        IReadOnlyList<string> paths,
        TimeSpan? probeBudget = null)
    {
        try
        {
            if (SameVolumeWithBudgetProbe is { } budgetedProbe)
            {
                return budgetedProbe(paths, probeBudget ?? PathOriginClassifier.DefaultHoverProbeBudget);
            }

            return SameVolumeProbe?.Invoke(paths) ?? true;
        }
        catch
        {
            // Unknown volume pairing is the same-volume default this path
            // has always reported; a wrong guess only affects the glyph.
            return true;
        }
    }

    internal static bool ShouldProbeHDropHintsOnHover(
        bool hasFileData,
        bool hasVirtualFileData,
        bool hasVirtualDescriptorData,
        bool isAsyncCapableSource,
        bool hasShellIdListFormat,
        bool hasActiveDeskBoxDrag,
        bool isDevelopmentRoot)
    {
        if (!hasFileData || hasVirtualFileData)
        {
            return false;
        }

        // Standard Windows Shell filesystem drags (Desktop / Explorer CIDLData
        // and DeskBox SHCreateDataObject) always carry in-memory CF_HDROP and
        // Shell IDList Array alongside FileGroupDescriptorW and
        // IDataObjectAsyncCapability. Probing CF_HDROP when Shell IDList Array
        // is present (with CF_HDROP available, so zipfldr/Outlook virtual-only
        // drags are excluded by hasVirtualFileData) or during an active DeskBox
        // drag resolves real paths in microseconds with zero disk extraction.
        // Delayed-rendering archive/FTP sources (7-Zip, WinRAR, Bandizip,
        // PeaZip, WinSCP, FileZilla) and non-Shell hybrid browser/Outlook drags
        // omit Shell IDList Array and remain blocked during hover.
        if (hasActiveDeskBoxDrag || hasShellIdListFormat)
        {
            return true;
        }

        return !hasVirtualDescriptorData &&
               !isAsyncCapableSource &&
               isDevelopmentRoot;
    }

    private bool GetHasMappedFolder()
    {
        try
        {
            return HasMappedFolderProbe?.Invoke() ?? true;
        }
        catch
        {
            return true;
        }
    }

    private TransferTargetKind GetTargetKind()
    {
        try
        {
            return TargetKindProbe?.Invoke() ?? TransferTargetKind.DirectorySurface;
        }
        catch
        {
            return TransferTargetKind.DirectorySurface;
        }
    }

    private static TransferDomain ResolveDragDomain(IReadOnlyList<string> paths)
    {
        return paths.Count > 0 && ActiveDeskBoxDragRegistry.TryMatch(paths, out _, out _)
            ? TransferDomain.InternalDeskBox
            : TransferDomain.ExternalInbound;
    }

    private static SourceOriginSnapshot ClassifyHoverOrDropSources(
        IReadOnlyList<string> paths,
        TimeSpan? probeBudget = null)
    {
        return paths.Count > 0
            ? PathOriginClassifier.ClassifySources(
                paths,
                probeBudget ?? PathOriginClassifier.DefaultHoverProbeBudget)
            : SourceOriginSnapshot.Empty;
    }

    private bool IsImportBusy()
    {
        try
        {
            return ImportBusyProbe?.Invoke() == true;
        }
        catch
        {
            return false;
        }
    }

    private void NotifyImportBusyRefused()
    {
        try
        {
            ImportBusyDropBlocked?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Import-busy notify failed: {ex.Message}");
        }
    }

    private void InspectDragData(nint dataObject)
    {
        bool hasHDropData = TryHasHDropData(dataObject);
        bool hasVirtualDescriptorData = TryHasVirtualFileData(dataObject);
        _hasVirtualDescriptorData = hasVirtualDescriptorData;
        _hasShellIdListFormat = hasHDropData &&
            TryQueryFormat(
                dataObject,
                s_shellIdListFormat,
                TYMED_HGLOBAL,
                -1);
        _isAsyncCapableSource = hasHDropData &&
            TryQueryAsyncModeEnabled(dataObject);
        HasVirtualFileData = NativeDropEffectPolicy.IsVirtualOnlyFileData(
            hasHDropData,
            hasVirtualDescriptorData);
        // Strict Shell-object classification (2026-10-15 field incident
        // reinstated; 2026-10-16 extended to materializable namespace items):
        // a presence-only probe made every IDList-only drag (Explorer zip
        // folders et al.) promise a copy/link hover it could not honor. The
        // strict parse is gated on !hasHDrop, so its cost only ever applies
        // to the rare shell-object drag class — ordinary file drags never
        // pay it. AppsFolder items keep their application-link path; other
        // Shell namespace items (Recycle Bin, This PC, ...) are accepted
        // only when they carry an identity Drop can honor — a file-system
        // path for HDROP-equivalent import, or a parsing name for PIDL-based
        // .lnk materialization (mirrors the paste-path judgment). Items with
        // neither still read as no-drop. The Drop-side extraction runs on
        // the UI STA (see HandleDeferredDropMaterialization).
        HasShellApplicationData = false;
        HasShellNamespaceData = false;
        if (!hasHDropData && !HasVirtualFileData)
        {
            TryClassifyShellIdListDrag(
                dataObject,
                out bool hasApplications,
                out bool hasNamespaceItems);
            HasShellApplicationData = hasApplications;
            HasShellNamespaceData = hasNamespaceItems;
        }

        HasFileData =
            hasHDropData || HasVirtualFileData || HasShellApplicationData ||
            HasShellNamespaceData;
        HasActiveShellDragWindow =
            HasFileData && NativeDropDescriptionWriter.HasDragWindow(dataObject);
    }

    private void ResetDragDataState()
    {
        HasFileData = false;
        HasVirtualFileData = false;
        HasShellApplicationData = false;
        HasShellNamespaceData = false;
        HasActiveShellDragWindow = false;
        _hasVirtualDescriptorData = false;
        _hasShellIdListFormat = false;
        _isAsyncCapableSource = false;
        _activeGestureId = 0;
        _dragPathHints = [];
        _cachedDragSourceOriginSnapshot = null;
    }

    private static bool TryQueryAsyncModeEnabled(nint dataObject)
    {
        if (dataObject == 0 ||
            !NativeDropAsyncCapability.TryQuery(dataObject, out NativeDropAsyncCapability capability))
        {
            return false;
        }

        try
        {
            return capability.GetAsyncMode();
        }
        catch
        {
            return false;
        }
        finally
        {
            _ = capability.Release();
        }
    }

    private bool ShouldUseShellVisual()
    {
        if (!HasFileData)
        {
            return false;
        }

        try
        {
            return _useShellVisualProvider?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateShellVisual(POINT point, uint effect)
    {
        if (!ShouldUseShellVisual())
        {
            if (_shellVisualActive)
            {
                NativeDropImageManager.DragLeave(this);
                _shellVisualActive = false;
                _shellVisualShown = false;
                ClearActiveDropDescription();
            }
            return;
        }

        if (!_shellVisualActive)
        {
            _shellVisualActive = NativeDropImageManager.TryDragEnter(
                this,
                _hwnd,
                _activeDataObject,
                point,
                effect);
            _shellVisualShown = false;
            UpdateActiveDropDescription(effect);
        }
        else
        {
            UpdateActiveDropDescription(effect);
            NativeDropImageManager.DragOver(this, point, effect);
        }

        if (_shellVisualActive && !_shellVisualShown)
        {
            NativeDropImageManager.Show(this, visible: true);
            _shellVisualShown = true;
        }
    }

    private void RetainActiveDataObject(nint dataObject)
    {
        if (dataObject == 0)
        {
            return;
        }

        try
        {
            Marshal.AddRef(dataObject);
            _activeDataObject = dataObject;
            _hasDropDescriptionAttempt = false;
            _lastDropDescriptionEffect = NativeDropEffectPolicy.None;
            _lastDropDescriptionText = null;
        }
        catch
        {
            _activeDataObject = 0;
        }
    }

    private void UpdateActiveDropDescription(uint effect)
    {
        if (_activeDataObject == 0 || _dropDescriptionProvider is null)
        {
            return;
        }

        NativeDropDescriptionText? text;
        try
        {
            text = _dropDescriptionProvider(effect);
        }
        catch
        {
            return;
        }

        if (_hasDropDescriptionAttempt &&
            _lastDropDescriptionEffect == effect &&
            _lastDropDescriptionText == text)
        {
            return;
        }

        bool wasAttempted = _hasDropDescriptionAttempt;
        bool hadCustomDescription = _lastDropDescriptionText.HasValue;
        _hasDropDescriptionAttempt = true;
        _lastDropDescriptionEffect = effect;
        _lastDropDescriptionText = text;
        if (text is { } value)
        {
            bool applied = NativeDropDescriptionWriter.TryApply(
                _activeDataObject,
                effect,
                value);
            App.LogVerbose(
                $"[DropTarget] Shell description effect={effect} " +
                $"applied={applied} target={value.Insert}");
        }
        else if (hadCustomDescription ||
                 (!wasAttempted && ActiveDeskBoxDragRegistry.HasActiveNativeShellDrag))
        {
            _ = NativeDropDescriptionWriter.TryClear(_activeDataObject);
        }
    }

    private void EndNativeDragVisual()
    {
        ClearActiveDropDescriptionAndReleaseDataObject();
        if (_shellVisualActive)
        {
            NativeDropImageManager.DragLeave(this);
            _shellVisualActive = false;
            _shellVisualShown = false;
        }
    }

    private void ClearActiveDropDescription()
    {
        _hasDropDescriptionAttempt = false;
        _lastDropDescriptionEffect = NativeDropEffectPolicy.None;
        _lastDropDescriptionText = null;
        if (_activeDataObject != 0)
        {
            _ = NativeDropDescriptionWriter.TryClear(_activeDataObject);
        }
    }

    private void ClearActiveDropDescriptionAndReleaseDataObject()
    {
        nint dataObject = _activeDataObject;
        _activeDataObject = 0;
        _hasDropDescriptionAttempt = false;
        _lastDropDescriptionEffect = NativeDropEffectPolicy.None;
        _lastDropDescriptionText = null;
        if (dataObject == 0)
        {
            return;
        }

        _ = NativeDropDescriptionWriter.TryClear(dataObject);
        try
        {
            Marshal.Release(dataObject);
        }
        catch
        {
        }
    }

    // ── Data extraction helpers ──

    private bool TryHasHDropData(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            return dataObject.QueryGetData(ref format) == S_OK;
        }
        catch
        {
            return false;
        }
    }

    private bool TryHasVirtualFileData(IntPtr pDataObj)
    {
        return TryQueryFormat(pDataObj, s_fileGroupDescriptorFormat, TYMED_HGLOBAL, -1);
    }

    private static IReadOnlyList<string> TryExtractHDropPathHints(
        IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            int hr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (hr < 0)
            {
                return [];
            }

            try
            {
                return hr == S_OK && medium.Content != IntPtr.Zero
                    ? GetDroppedFiles(medium.Content)
                    : [];
            }
            finally
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[DropTarget] Failed to read CF_HDROP path hints: {ex.Message}");
            return [];
        }
    }

    private static void TryClassifyShellIdListDrag(
        IntPtr pDataObj,
        out bool hasApplications,
        out bool hasNamespaceItems)
    {
        hasApplications = false;
        hasNamespaceItems = false;
        if (!TryQueryFormat(
                pDataObj,
                s_shellIdListFormat,
                TYMED_HGLOBAL,
                -1))
        {
            return;
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = s_shellIdListFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };
            int hr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (hr < 0)
            {
                return;
            }

            try
            {
                if (hr != S_OK || medium.Content == IntPtr.Zero)
                {
                    return;
                }

                foreach (ShellIdListDropItem item in ReadShellIdListDropItems(
                             medium.Content))
                {
                    if (item.Kind == ShellIdListItemKind.Application)
                    {
                        hasApplications = true;
                    }
                    else
                    {
                        hasNamespaceItems = true;
                    }

                    if (hasApplications && hasNamespaceItems)
                    {
                        return;
                    }
                }
            }
            finally
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            // Classification is best-effort: an unreadable payload keeps its
            // honest no-drop reading.
        }
    }

    private static bool TryQueryFormat(IntPtr pDataObj, ushort clipboardFormat, uint tymed, int index)
    {
        if (pDataObj == IntPtr.Zero || clipboardFormat == 0)
        {
            return false;
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = clipboardFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = index,
                MediumType = tymed,
            };
            return dataObject.QueryGetData(ref format) == S_OK;
        }
        catch
        {
            return false;
        }
    }

    private (
        IReadOnlyList<string> Paths,
        IReadOnlyList<DroppedItemOriginKind> Origins,
        bool ContainsTemporaryFiles) TryExtractFilePathsWithOrigins(
        IntPtr pDataObj,
        bool hasShellIdList,
        bool canDeferAsyncOrVirtualFallback,
        out bool deferVirtualFallback,
        out int skippedOrFailedCount)
    {
        deferVirtualFallback = false;
        skippedOrFailedCount = 0;
        if (pDataObj == IntPtr.Zero)
        {
            return ([], [], false);
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            int hr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (hr >= 0)
            {
                try
                {
                    if (hr == S_OK && medium.Content != IntPtr.Zero)
                    {
                        IReadOnlyList<string> paths = GetDroppedFiles(medium.Content);
                        if (paths.Count > 0)
                        {
                            IReadOnlyList<string> stagedPaths =
                                DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                                    paths,
                                    hasShellIdList,
                                    out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
                                    out bool stagedAny,
                                    out skippedOrFailedCount);
                            return (stagedPaths, itemOrigins, stagedAny);
                        }
                    }
                }
                finally
                {
                    OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
                }
            }

            if (canDeferAsyncOrVirtualFallback)
            {
                // Mixed/async CF_HDROP sources (New Outlook, WebView2) that
                // return DV_E_FORMATETC or empty before StartOperation must
                // materialize on the dedicated MTA worker rather than reading
                // IStream inline on the UI STA inside the OLE Drop callback.
                deferVirtualFallback = true;
                return ([], [], false);
            }

            return ([], [], false);
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to extract file paths: {ex.Message}");
            if (canDeferAsyncOrVirtualFallback)
            {
                deferVirtualFallback = true;
            }

            return ([], [], false);
        }
    }

    internal static IReadOnlyList<string> ExtractDeferredWorkerFilesWithOrigins(
        NativeOleDataObject dataObject,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
        out int skippedOrFailedCount)
    {
        skippedOrFailedCount = 0;
        try
        {
            var format = new NativeFormatEtc
            {
                ClipboardFormat = (ushort)CF_HDROP,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };

            int hr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (hr >= 0)
            {
                try
                {
                    if (hr == S_OK && medium.Content != IntPtr.Zero)
                    {
                        IReadOnlyList<string> hdropPaths = GetDroppedFiles(medium.Content);
                        if (hdropPaths.Count > 0)
                        {
                            IReadOnlyList<string> staged =
                                DropStagingFileOps.StageWorkerHDropPaths(
                                    hdropPaths,
                                    out int hdropSkipped);
                            if (staged.Count > 0)
                            {
                                skippedOrFailedCount = hdropSkipped;
                                itemOrigins = DropStagingFileOps.CreateUniformOrigins(
                                    staged.Count,
                                    DroppedItemOriginKind.TransientArchiveOrCache);
                                return staged;
                            }

                            skippedOrFailedCount = hdropSkipped;
                        }
                    }
                }
                finally
                {
                    OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DropTarget] Worker CF_HDROP probe failed: {ex.Message}");
        }

        IReadOnlyList<string> virtualPaths = ExtractVirtualFiles(
            dataObject,
            out int virtualSkipped,
            out _);
        skippedOrFailedCount += virtualSkipped;
        itemOrigins = DropStagingFileOps.CreateUniformOrigins(
            virtualPaths.Count,
            DroppedItemOriginKind.MaterializedVirtualStream);
        return virtualPaths;
    }

    // Internal for the contract tests; the real callers keep it on the UI
    // STA one dispatcher turn out of the OLE callback (see
    // HandleDeferredDropMaterialization).
    internal static IReadOnlyList<string> TryExtractShellIdListItems(
        IntPtr pDataObj) =>
        TryExtractShellIdListItems(pDataObj, out _, out _);

    internal static IReadOnlyList<string> TryExtractShellIdListItems(
        IntPtr pDataObj,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins) =>
        TryExtractShellIdListItems(pDataObj, out itemOrigins, out _);

    internal static IReadOnlyList<string> TryExtractShellIdListItems(
        IntPtr pDataObj,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
        out int skippedOrFailedCount)
    {
        itemOrigins = [];
        skippedOrFailedCount = 0;
        if (pDataObj == IntPtr.Zero || s_shellIdListFormat == 0)
        {
            return [];
        }

        try
        {
            var dataObject = new NativeOleDataObject(pDataObj);
            var format = new NativeFormatEtc
            {
                ClipboardFormat = s_shellIdListFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = -1,
                MediumType = TYMED_HGLOBAL,
            };
            int shellIdListHr = dataObject.GetData(ref format, out NativeStorageMedium medium);
            if (shellIdListHr != S_OK || medium.Content == IntPtr.Zero)
            {
                if (shellIdListHr >= 0)
                {
                    OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
                }

                App.LogVerbose(
                    "[DropTarget] ShellIdList extraction: SHELLIDLIST GetData " +
                    $"failed hr=0x{shellIdListHr:X8} empty={medium.Content == IntPtr.Zero}");
                return [];
            }

            IReadOnlyList<ShellIdListDropItem> items;
            try
            {
                items = ReadShellIdListDropItems(medium.Content);
            }
            finally
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
            }

            if (items.Count == 0)
            {
                App.LogVerbose(
                    "[DropTarget] ShellIdList extraction: PIDL parse produced " +
                    "no items (structure or identity rejection)");
            }

            return MaterializeShellIdListItemsWithOrigins(items, out itemOrigins, out skippedOrFailedCount);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DropTarget] Failed to extract Shell IDList items: {ex.Message}");
            itemOrigins = [];
            return [];
        }
    }

    private static IReadOnlyList<ShellIdListDropItem> ReadShellIdListDropItems(
        IntPtr shellIdListHandle)
    {
        nuint rawBufferSize = OleDropTargetNativeMethods.GlobalSize(shellIdListHandle);
        if (rawBufferSize < sizeof(uint) + (2 * sizeof(uint)) ||
            rawBufferSize > int.MaxValue)
        {
            return [];
        }
        long bufferSize = (long)rawBufferSize;

        IntPtr bufferStart = OleDropTargetNativeMethods.GlobalLock(shellIdListHandle);
        if (bufferStart == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            uint childCount = unchecked((uint)Marshal.ReadInt32(bufferStart));
            if (childCount == 0 || childCount > MaxShellApplicationDropItems)
            {
                return [];
            }

            long offsetCount = (long)childCount + 1;
            long headerSize = sizeof(uint) + (offsetCount * sizeof(uint));
            if (headerSize > bufferSize)
            {
                return [];
            }

            uint parentOffset = unchecked((uint)Marshal.ReadInt32(
                bufferStart,
                sizeof(uint)));
            if (!TryGetValidatedPidl(
                    bufferStart,
                    bufferSize,
                    headerSize,
                    parentOffset,
                    out IntPtr parentPidl))
            {
                return [];
            }

            var items = new List<ShellIdListDropItem>();
            var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenParsingNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < childCount; index++)
            {
                int offsetPosition = checked(
                    sizeof(uint) + ((int)index + 1) * sizeof(uint));
                uint childOffset = unchecked((uint)Marshal.ReadInt32(
                    bufferStart,
                    offsetPosition));
                if (!TryGetValidatedPidl(
                        bufferStart,
                        bufferSize,
                        headerSize,
                        childOffset,
                        out IntPtr childPidl))
                {
                    continue;
                }

                IntPtr absolutePidl = OleDropTargetNativeMethods.ILCombine(parentPidl, childPidl);
                if (absolutePidl == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    string parsingName = ReadShellDisplayName(
                        absolutePidl,
                        SIGDN_DESKTOPABSOLUTEPARSING);
                    if (TryNormalizePackagedApplicationId(
                            parsingName,
                            out string appUserModelId))
                    {
                        if (!seenAppIds.Add(appUserModelId))
                        {
                            continue;
                        }

                        string displayName = ReadShellDisplayName(
                            absolutePidl,
                            SIGDN_NORMALDISPLAY);
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            displayName = appUserModelId[..appUserModelId.IndexOf('!')];
                        }

                        items.Add(new ShellIdListDropItem(
                            ShellIdListItemKind.Application,
                            appUserModelId,
                            parsingName,
                            displayName,
                            FileSystemPath: string.Empty,
                            RawPidl: ShellLinkVtableInterop.CopyValidatedPidlBytes(absolutePidl)));
                        continue;
                    }

                    // Non-AppsFolder namespace item: accept only an identity
                    // Drop can honor — a file-system path imports like
                    // CF_HDROP, a pure virtual parsing name materializes as
                    // a PIDL-based .lnk (mirrors the paste-path judgment in
                    // ShellClipboardHelper).
                    string fileSystemPath = ReadShellDisplayName(
                        absolutePidl,
                        SIGDN_FILESYSPATH);
                    if (!TryClassifyShellNamespaceItem(
                            parsingName,
                            fileSystemPath,
                            out bool isFileSystemPath) ||
                        !seenParsingNames.Add(parsingName))
                    {
                        continue;
                    }

                    string namespaceDisplayName = ReadShellDisplayName(
                        absolutePidl,
                        SIGDN_NORMALDISPLAY);
                    if (string.IsNullOrWhiteSpace(namespaceDisplayName))
                    {
                        namespaceDisplayName = parsingName;
                    }

                    ShellIdListItemKind namespaceKind = isFileSystemPath
                        ? ShellIdListItemKind.FileSystemPath
                        : TryIsNonFolderStreamShellItem(absolutePidl)
                            ? ShellIdListItemKind.StreamObject
                            : ShellIdListItemKind.VirtualNamespace;

                    items.Add(new ShellIdListDropItem(
                        namespaceKind,
                        AppUserModelId: string.Empty,
                        parsingName,
                        namespaceDisplayName,
                        isFileSystemPath ? fileSystemPath : string.Empty,
                        isFileSystemPath
                            ? null
                            : ShellLinkVtableInterop.CopyValidatedPidlBytes(absolutePidl)));
                }
                finally
                {
                    OleDropTargetNativeMethods.ILFree(absolutePidl);
                }
            }

            return items;
        }
        finally
        {
            _ = OleDropTargetNativeMethods.GlobalUnlock(shellIdListHandle);
        }
    }

    private static bool TryGetValidatedPidl(
        IntPtr bufferStart,
        long bufferSize,
        long headerSize,
        uint offset,
        out IntPtr pidl)
    {
        pidl = IntPtr.Zero;
        if (offset < headerSize || offset >= bufferSize)
        {
            return false;
        }

        IntPtr candidate = IntPtr.Add(bufferStart, checked((int)offset));
        IntPtr bufferEnd = IntPtr.Add(bufferStart, checked((int)bufferSize));
        if (!IsPidlWithinBuffer(candidate, bufferEnd))
        {
            return false;
        }

        pidl = candidate;
        return true;
    }

    private static bool IsPidlWithinBuffer(IntPtr pidl, IntPtr bufferEnd)
    {
        long cursor = pidl.ToInt64();
        long end = bufferEnd.ToInt64();
        while (cursor <= end - sizeof(ushort))
        {
            ushort itemSize = unchecked((ushort)Marshal.ReadInt16(new IntPtr(cursor)));
            if (itemSize == 0)
            {
                return true;
            }

            if (itemSize < sizeof(ushort) || cursor > end - itemSize)
            {
                return false;
            }

            cursor += itemSize;
        }

        return false;
    }

    private static string ReadShellDisplayName(
        IntPtr itemIdList,
        uint displayNameType)
    {
        IntPtr value = IntPtr.Zero;
        int hresult = OleDropTargetNativeMethods.SHGetNameFromIDList(
            itemIdList,
            displayNameType,
            out value);
        if (hresult < 0 || value == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            OleDropTargetNativeMethods.CoTaskMemFree(value);
        }
    }

    internal static bool TryNormalizePackagedApplicationId(
        string? parsingName,
        out string appUserModelId)
    {
        appUserModelId = string.Empty;
        string candidate = parsingName?.Trim() ?? string.Empty;
        foreach (string prefix in new[]
                 {
                     AppsFolderPrefix,
                     AppsFolderClsidPrefix,
                     "::{4234d49b-0245-4df3-b780-3893943456e1}\\"
                 })
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[prefix.Length..];
                break;
            }
        }

        int separatorIndex = candidate.IndexOf('!');
        if (candidate.Length is 0 or > 1024 ||
            separatorIndex <= 0 ||
            separatorIndex >= candidate.Length - 1 ||
            candidate.Contains('\0') ||
            candidate.Contains('\\') ||
            candidate.Contains('/') ||
            candidate.Contains(':') ||
            candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        appUserModelId = candidate;
        return true;
    }

    /// <summary>
    /// Classifies a non-AppsFolder Shell namespace item by the identity Drop
    /// can actually honor: a file-system path imports like CF_HDROP, a pure
    /// virtual parsing name materializes as a PIDL-based .lnk, and anything
    /// without a consumable identity stays no-drop. Mirrors the paste-path
    /// <c>ShellNamespaceClipboardItem</c> judgment (an empty
    /// SIGDN_FILESYSPATH means a purely virtual item such as This PC or the
    /// Recycle Bin).
    /// </summary>
    internal static bool TryClassifyShellNamespaceItem(
        string? parsingName,
        string? fileSystemPath,
        out bool isFileSystemPath)
    {
        isFileSystemPath = false;
        string candidate = parsingName?.Trim() ?? string.Empty;
        if (candidate.Length == 0 ||
            candidate.Length > 1024 ||
            candidate.Contains('\0'))
        {
            return false;
        }

        string path = fileSystemPath?.Trim() ?? string.Empty;
        if (path.Length > 0 && !path.Contains('\0'))
        {
            isFileSystemPath = true;
        }

        return true;
    }

    internal static bool ContainsVirtualDropStagedPaths(
        IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        foreach (string rawPath in paths)
        {
            if (DropStagingFileOps.IsUnderVirtualDropsDirectory(rawPath))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<DroppedItemOriginKind> ClassifyShellIdListExtractedOrigins(
        IReadOnlyList<string> extractedPaths)
    {
        if (extractedPaths.Count == 0)
        {
            return [];
        }

        var origins = new DroppedItemOriginKind[extractedPaths.Count];
        for (int i = 0; i < extractedPaths.Count; i++)
        {
            origins[i] = ContainsVirtualDropStagedPaths([extractedPaths[i]])
                ? DroppedItemOriginKind.ShellNamespaceLinkOnly
                : DroppedItemOriginKind.RealFileSystemPath;
        }

        return origins;
    }

    internal static IReadOnlyList<string> MaterializeShellIdListItems(
        IReadOnlyList<ShellIdListDropItem> items) =>
        MaterializeShellIdListItemsWithOrigins(items, out _, out _);

    internal static IReadOnlyList<string> MaterializeShellIdListItemsWithOrigins(
        IReadOnlyList<ShellIdListDropItem> items,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins) =>
        MaterializeShellIdListItemsWithOrigins(items, out itemOrigins, out _);

    internal static IReadOnlyList<string> MaterializeShellIdListItemsWithOrigins(
        IReadOnlyList<ShellIdListDropItem> items,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
        out int skippedOrFailedCount)
    {
        skippedOrFailedCount = 0;
        if (items.Count == 0)
        {
            itemOrigins = [];
            return [];
        }

        string? temporaryDirectory = null;
        int materializedTempCount = 0;
        long remainingBudgetBytes = DropStagingBudgetPolicy.ResolveDynamicByteBudget(
            DropStagingFileOps.GetVirtualDropsRootDirectory());
        var paths = new List<string>(items.Count);
        var origins = new List<DroppedItemOriginKind>(items.Count);
        foreach (ShellIdListDropItem item in items)
        {
            if (item.Kind == ShellIdListItemKind.FileSystemPath)
            {
                // HDROP-equivalent import: the namespace item resolves to a
                // real file-system path and joins the normal transfer flow.
                paths.Add(item.FileSystemPath);
                origins.Add(DroppedItemOriginKind.RealFileSystemPath);
                continue;
            }

            if (temporaryDirectory is null)
            {
                string virtualDropsRoot = DropStagingFileOps.GetVirtualDropsRootDirectory();
                DropStagingFileOps.EnsureSafeStagingRootDirectory(DropStagingFileOps.GetStagingRootDirectory());
                DropStagingFileOps.EnsureSafeStagingRootDirectory(virtualDropsRoot);
                temporaryDirectory = Path.Combine(
                    virtualDropsRoot,
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
            }

            string fileName = FileService.SanitizeFileSystemName(
                item.DisplayName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = item.Kind == ShellIdListItemKind.Application
                    ? "Application"
                    : item.Kind == ShellIdListItemKind.StreamObject
                        ? "ShellStreamFile"
                        : "ShellObject";
            }

            if (item.Kind == ShellIdListItemKind.StreamObject)
            {
                string streamDestPath = FileService.GetAvailablePath(
                    Path.Combine(temporaryDirectory, fileName));
                if (item.RawPidl is { Length: >= 4 } streamPidl &&
                    TrySaveShellItemStream(streamPidl, streamDestPath, ref remainingBudgetBytes))
                {
                    App.Log(
                        $"[DropTarget] Extracted Shell stream object " +
                        $"target='{item.ParsingName}' name='{fileName}'");
                    paths.Add(streamDestPath);
                    origins.Add(DroppedItemOriginKind.TransientArchiveOrCache);
                    materializedTempCount++;
                }
                else
                {
                    skippedOrFailedCount++;
                }

                continue;
            }

            string shortcutPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, $"{fileName}.lnk"));
            try
            {
                bool createdFromRawPidl = item.RawPidl is { Length: >= 4 } rawPidl &&
                    ShortcutHelper.TryCreateShortcutFromRawPidl(
                        shortcutPath,
                        rawPidl,
                        item.DisplayName);

                if (!createdFromRawPidl)
                {
                    if (item.Kind == ShellIdListItemKind.Application)
                    {
                        ShortcutHelper.CreateShellApplicationShortcut(
                            shortcutPath,
                            item.AppUserModelId,
                            item.DisplayName);
                    }
                    else
                    {
                        ShortcutHelper.CreateShellNamespaceShortcut(
                            shortcutPath,
                            item.ParsingName,
                            item.DisplayName);
                    }
                }

                if (item.Kind == ShellIdListItemKind.Application)
                {
                    App.Log(
                        $"[DropTarget] Materialized AppsFolder shortcut " +
                        $"app='{item.AppUserModelId}' name='{item.DisplayName}' rawPidl={createdFromRawPidl}");
                }
                else
                {
                    App.Log(
                        $"[DropTarget] Materialized namespace shortcut " +
                        $"target='{item.ParsingName}' name='{item.DisplayName}' rawPidl={createdFromRawPidl}");
                }

                paths.Add(shortcutPath);
                origins.Add(DroppedItemOriginKind.ShellNamespaceLinkOnly);
                materializedTempCount++;
            }
            catch (Exception ex)
            {
                skippedOrFailedCount++;
                try { File.Delete(shortcutPath); } catch { }
                App.Log(
                    $"[DropTarget] Failed to materialize Shell item " +
                    $"target='{item.ParsingName}': {ex.Message}");
            }
        }

        if (temporaryDirectory is not null && materializedTempCount == 0)
        {
            DropStagingFileOps.CleanupVirtualDropBatchDirectory(temporaryDirectory);
        }

        itemOrigins = origins;
        return paths;
    }

    private static unsafe bool TryIsNonFolderStreamShellItem(IntPtr absolutePidl)
    {
        if (absolutePidl == IntPtr.Zero)
        {
            return false;
        }

        Guid shellItemIid = s_iidIShellItem;
        if (OleDropTargetNativeMethods.SHCreateItemFromIDList(absolutePidl, in shellItemIid, out IntPtr shellItem) < 0 ||
            shellItem == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            nint* vtbl = *(nint**)shellItem;
            var getAttributes = (delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)vtbl[6];
            uint attrs = 0;
            int hr = getAttributes(shellItem, SfgaoFilesys | SfgaoFolder | SfgaoStream, &attrs);
            return hr >= 0 &&
                   (attrs & SfgaoFilesys) == 0 &&
                   (attrs & SfgaoFolder) == 0 &&
                   (attrs & SfgaoStream) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            _ = Marshal.Release(shellItem);
        }
    }

    private static unsafe bool TrySaveShellItemStream(
        byte[] rawPidlBytes,
        string destinationPath,
        ref long remainingBudgetBytes)
    {
        if (!ShellLinkVtableInterop.IsValidPidlBytes(rawPidlBytes) ||
            remainingBudgetBytes <= 0)
        {
            return false;
        }

        IntPtr shellItem = IntPtr.Zero;
        IntPtr streamPointer = IntPtr.Zero;
        try
        {
            fixed (byte* pidlPtr = rawPidlBytes)
            {
                Guid shellItemIid = s_iidIShellItem;
                int hr = OleDropTargetNativeMethods.SHCreateItemFromIDList(
                    (IntPtr)pidlPtr,
                    in shellItemIid,
                    out shellItem);
                if (hr < 0 || shellItem == IntPtr.Zero)
                {
                    return false;
                }
            }

            nint* vtbl = *(nint**)shellItem;
            var bindToHandler = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, IntPtr*, int>)vtbl[3];
            Guid bhidStream = s_bhidStream;
            Guid streamIid = s_iidIStream;
            int bindHr = bindToHandler(shellItem, 0, &bhidStream, &streamIid, &streamPointer);
            if (bindHr < 0 || streamPointer == IntPtr.Zero)
            {
                return false;
            }

            SaveComStream(streamPointer, destinationPath, ref remainingBudgetBytes);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to save Shell stream '{destinationPath}': {ex.Message}");
            try { File.Delete(destinationPath); } catch { }
            return false;
        }
        finally
        {
            if (streamPointer != IntPtr.Zero)
            {
                _ = Marshal.Release(streamPointer);
            }

            if (shellItem != IntPtr.Zero)
            {
                _ = Marshal.Release(shellItem);
            }
        }
    }

    private static IReadOnlyList<string> GetDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        uint count = ClipboardNativeMethods.QueryDroppedFileCount(hDrop);
        for (uint i = 0; i < count; i++)
        {
            string? path = ClipboardNativeMethods.TryQueryDroppedFilePath(hDrop, i);
            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
