using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskBox.Platform;
using DeskBox.Services;

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
    IReadOnlyList<DroppedItemOriginKind>? ItemOrigins = null);

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
public sealed class NativeDropTarget : IDisposable
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct FILEDESCRIPTORW
    {
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

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
    }

    // ── Constants ──

    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const uint TYMED_ISTREAM = 4;
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int MaxShellApplicationDropItems = 256;
    // Per-drop ceiling for materialized virtual files. Legitimate browser
    // drops stay far below this; a crafted payload cannot exhaust the disk.
    private const long MaxVirtualDropBytes = 256L * 1024 * 1024;
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
    private bool _registered;
    private bool _rightButtonDragActive;
    private bool _hasVirtualDescriptorData;
    private bool _hasShellIdListFormat;
    private bool _isAsyncCapableSource;
    private long _activeGestureId;
    private IReadOnlyList<string> _dragPathHints = [];

    internal enum ShellIdListItemKind
    {
        Application,
        FileSystemPath,
        VirtualNamespace,
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

    static NativeDropTarget()
    {
        s_fileGroupDescriptorFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileGroupDescriptorW");
        s_fileContentsFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileContents");
        s_shellIdListFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Shell IDList Array");
        s_performedDropEffectFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Performed DropEffect");
        s_logicalPerformedDropEffectFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Logical Performed DropEffect");

        // Ensure OLE is initialized (WinUI 3 usually does this, but call
        // again is harmless if already initialized).
        try
        {
            OleDropTargetNativeMethods.OleInitialize(IntPtr.Zero);
        }
        catch
        {
        }
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
        if (_registered)
        {
            return;
        }

        try
        {
            NativeDropTargetComInterop.Register(_hwnd, _comObject);
            _registered = true;
            App.Log(
                $"[DropTarget] Registered IDropTarget for hwnd=0x{_hwnd.ToInt64():X} " +
                "shellImageHelper=shared");
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

    private int OnDragEnterCore(
        nint dataObject,
        uint keyState,
        POINT point,
        ref uint effect)
    {
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

        SourceOriginSnapshot sourceOrigin = ClassifyHoverOrDropSources(_dragPathHints);
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
        uint allowedEffects = effect;
        _rightButtonDragActive |=
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        DragOverEvent?.Invoke(point.X, point.Y);

        SourceOriginSnapshot sourceOrigin = ClassifyHoverOrDropSources(_dragPathHints);
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
            out bool deferVirtualFallback);
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
        SourceOriginSnapshot dropOrigin = ClassifyHoverOrDropSources(effectivePaths);
        TransferDomain dropDomain = ResolveDragDomain(effectivePaths);
        bool allItemsTemporary = itemOrigins.Count > 0
            ? itemOrigins.All(static kind => kind != DroppedItemOriginKind.RealFileSystemPath)
            : containsTemporaryFiles;
        bool sourceCanCopy = dropDomain == TransferDomain.InternalDeskBox ||
            (allowedEffects & NativeDropEffectPolicy.Copy) != 0;
        bool sourceCanMove = !allItemsTemporary &&
            !dropOrigin.IsMoveProtectedSystemOrSharedSource &&
            (dropDomain == TransferDomain.InternalDeskBox ||
             (allowedEffects & NativeDropEffectPolicy.Move) != 0);
        bool copyRequested = NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            allItemsTemporary,
            keyState,
            defaultMove,
            followWindows: followWindows,
            sameVolume: GetSameVolume(effectivePaths),
            canCopy: sourceCanCopy,
            canMove: sourceCanMove,
            protectDefaultMoveAsCopy: dropOrigin.IsProtectedCopyByDefault);
        bool shortcutRequested = createdShellApplicationLinks ||
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                allItemsTemporary,
                keyState);
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

        if (paths.Count > 0)
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
                itemOrigins));
            DropEvent?.Invoke(
                paths,
                point.X,
                point.Y,
                containsTemporaryFiles,
                copyRequested);
            effect = NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects,
                createdShellApplicationLinks);
        }
        else
        {
            effect = NativeDropEffectPolicy.None;
        }

        WritePerformedDropEffect(dataObject, effect);
        WriteLogicalPerformedDropEffect(
            dataObject,
            effect != NativeDropEffectPolicy.None && !rightButtonDrag
                ? feedbackEffect
                : effect);
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
            createdShellApplicationLinks: shellApplicationDrop ||
                shellNamespaceDrop,
            hasVirtualOnlyData: isVirtualStreamDrop);
        int dropX = point.X;
        int dropY = point.Y;
        uint capturedKeyState = keyState;
        uint capturedAllowedEffects = allowedEffects;
        uint capturedFeedbackEffect = feedbackEffect;
        bool capturedRightButtonDrag = rightButtonDrag;

        if (shellApplicationDrop || shellNamespaceDrop)
        {
            // Shell namespace APIs used by the AppsFolder and namespace-item
            // extraction (SHGetNameFromIDList & friends) bind in-proc shell
            // COM objects that fail on the MTA worker (2026-10-15 field
            // incident: start-menu drags extracted zero items on the worker).
            // The extraction therefore stays on the UI STA, one dispatcher
            // turn out of the callback frame; a plain AddRef keeps the data
            // object alive across the hop (same thread, no marshal needed)
            // and the receipt/EndOperation run here too, satisfying the
            // async protocol (everything happens after the Drop callback
            // returned).
            _ = System.Runtime.InteropServices.Marshal.AddRef(dataObject);
            FinishDragVisualAndState(dataObject, point, feedbackEffect);
            App.Log(
                "[DropTarget] NativeDrop deferred shell-object extraction " +
                "to UI turn");
            UiDispatch.PostAfterCallback(() =>
            {
                IReadOnlyList<string> extractedPaths = [];
                try
                {
                    extractedPaths = TryExtractShellIdListItems(dataObject);
                    uint committedEffect = extractedPaths.Count > 0
                        ? completionEffect
                        : NativeDropEffectPolicy.None;
                    WritePerformedDropEffect(dataObject, committedEffect);
                    WriteLogicalPerformedDropEffect(
                        dataObject,
                        committedEffect != NativeDropEffectPolicy.None && !capturedRightButtonDrag
                            ? capturedFeedbackEffect
                            : committedEffect);
                    EndAsyncOperationIfStarted(dataObject, asyncStarted);
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

                IReadOnlyList<DroppedItemOriginKind> shellOrigins =
                    ClassifyShellIdListExtractedOrigins(extractedPaths);
                bool hasShellTemporaryLinks = ContainsVirtualDropStagedPaths(extractedPaths);
                SourceOriginSnapshot shellOriginSnapshot = ClassifyHoverOrDropSources(extractedPaths);
                bool shellCopyRequested = hasShellTemporaryLinks
                    ? copyRequested
                    : NativeDropEffectPolicy.ShouldCopyMappedTransfer(
                        containsTemporaryFiles: false,
                        capturedKeyState,
                        defaultMove,
                        followWindows: followWindows,
                        sameVolume: GetSameVolume(extractedPaths),
                        canCopy: sourceCanCopy,
                        canMove: !shellOriginSnapshot.IsMoveProtectedSystemOrSharedSource && sourceCanMove,
                        protectDefaultMoveAsCopy: shellOriginSnapshot.IsProtectedCopyByDefault);
                bool shellShortcutRequested = shellApplicationDrop ||
                    hasShellTemporaryLinks ||
                    NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                        containsTemporaryFiles: false,
                        capturedKeyState);

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
                    hasShellTemporaryLinks,
                    gestureId,
                    shellOrigins);
            });

            effect = completionEffect;
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
                !capturedRightButtonDrag ? capturedFeedbackEffect : completionEffect,
                (extractedPaths, workerOrigins) => OnDeferredMaterializationCompleted(
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
                    itemOrigins: workerOrigins)))
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
        ResetDragDataState();
        _rightButtonDragActive = false;
    }

    private static void EndAsyncOperationIfStarted(nint dataObject, bool asyncStarted)
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
                    unchecked((int)0x80004005),
                    NativeDropEffectPolicy.None);
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
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null)
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
            itemOrigins));
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

    private bool GetSameVolume(IReadOnlyList<string> paths)
    {
        try
        {
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
        if (!hasFileData || hasVirtualFileData || hasVirtualDescriptorData)
        {
            return false;
        }

        // Standard Shell filesystem drags (Desktop / Explorer CIDLData) always
        // carry in-memory CF_HDROP alongside Shell IDList Array, while also
        // implementing IDataObjectAsyncCapability (GetAsyncMode == true).
        // Delayed-rendering CF_HDROP sources (7-Zip, WinRAR, Bandizip, PeaZip,
        // WinSCP, FileZilla) omit Shell IDList Array, and hybrid/virtual sources
        // (New Outlook, WebView2, browsers) advertise FileGroupDescriptorW or
        // omit Shell IDList Array while enabling async mode. Allowing CF_HDROP
        // probing when Shell IDList Array is present (with no virtual descriptor)
        // resolves real Desktop/Explorer paths in microseconds with zero disk I/O
        // while keeping archive/FTP/virtual sources untouched during hover.
        return hasShellIdListFormat ||
               hasActiveDeskBoxDrag ||
               (!isAsyncCapableSource && isDevelopmentRoot);
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

    private static SourceOriginSnapshot ClassifyHoverOrDropSources(IReadOnlyList<string> paths)
    {
        return paths.Count > 0
            ? PathOriginClassifier.ClassifySources(paths, TimeSpan.FromMilliseconds(80))
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
            UpdateActiveDropDescription(effect);
        }
        else
        {
            UpdateActiveDropDescription(effect);
            NativeDropImageManager.DragOver(this, point, effect);
        }

        if (_shellVisualActive)
        {
            NativeDropImageManager.Show(this, visible: true);
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
        else if (hadCustomDescription)
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

            if (dataObject.GetData(
                    ref format,
                    out NativeStorageMedium medium) != S_OK ||
                medium.Content == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                return GetDroppedFiles(medium.Content);
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
            if (dataObject.GetData(
                    ref format,
                    out NativeStorageMedium medium) != S_OK ||
                medium.Content == IntPtr.Zero)
            {
                return;
            }

            try
            {
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
        out bool deferVirtualFallback)
    {
        deferVirtualFallback = false;
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
            if (hr == S_OK && medium.Content != IntPtr.Zero)
            {
                try
                {
                    IReadOnlyList<string> paths = GetDroppedFiles(medium.Content);
                    if (paths.Count > 0)
                    {
                        IReadOnlyList<string> stagedPaths =
                            DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                                paths,
                                hasShellIdList,
                                out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
                                out bool stagedAny);
                        return (stagedPaths, itemOrigins, stagedAny);
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

    internal static IReadOnlyList<string> ExtractDeferredWorkerFiles(
        NativeOleDataObject dataObject) =>
        ExtractDeferredWorkerFilesWithOrigins(dataObject, out _);

    internal static IReadOnlyList<string> ExtractDeferredWorkerFilesWithOrigins(
        NativeOleDataObject dataObject,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins)
    {
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

            if (dataObject.GetData(ref format, out NativeStorageMedium medium) == S_OK &&
                medium.Content != IntPtr.Zero)
            {
                try
                {
                    IReadOnlyList<string> hdropPaths = GetDroppedFiles(medium.Content);
                    if (hdropPaths.Count > 0)
                    {
                        IReadOnlyList<string> staged =
                            DropStagingFileOps.StageWorkerHDropPaths(hdropPaths);
                        if (staged.Count > 0)
                        {
                            itemOrigins = DropStagingFileOps.CreateUniformOrigins(
                                staged.Count,
                                DroppedItemOriginKind.TransientArchiveOrCache);
                            return staged;
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

        IReadOnlyList<string> virtualPaths = ExtractVirtualFiles(dataObject);
        itemOrigins = DropStagingFileOps.CreateUniformOrigins(
            virtualPaths.Count,
            DroppedItemOriginKind.MaterializedVirtualStream);
        return virtualPaths;
    }

    // Internal for the contract tests; the real callers keep it on the UI
    // STA one dispatcher turn out of the OLE callback (see
    // HandleDeferredDropMaterialization).
    internal static IReadOnlyList<string> TryExtractShellIdListItems(
        IntPtr pDataObj)
    {
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

            return MaterializeShellIdListItems(items);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DropTarget] Failed to extract Shell IDList items: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<ShellIdListDropItem> ReadShellIdListDropItems(
        IntPtr shellIdListHandle)
    {
        long bufferSize = OleDropTargetNativeMethods.GlobalSize(shellIdListHandle).ToInt64();
        if (bufferSize < sizeof(uint) + (2 * sizeof(uint)) ||
            bufferSize > int.MaxValue)
        {
            return [];
        }

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

                    items.Add(new ShellIdListDropItem(
                        isFileSystemPath
                            ? ShellIdListItemKind.FileSystemPath
                            : ShellIdListItemKind.VirtualNamespace,
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

        string virtualDropsRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "DeskBox", "VirtualDrops"))) +
            Path.DirectorySeparatorChar;
        foreach (string rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(rawPath);
                if (fullPath.StartsWith(
                        virtualDropsRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
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
        MaterializeShellIdListItemsWithOrigins(items, out _);

    internal static IReadOnlyList<string> MaterializeShellIdListItemsWithOrigins(
        IReadOnlyList<ShellIdListDropItem> items,
        out IReadOnlyList<DroppedItemOriginKind> itemOrigins)
    {
        if (items.Count == 0)
        {
            itemOrigins = [];
            return [];
        }

        string? temporaryDirectory = null;
        int createdShortcutCount = 0;
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
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "DeskBox",
                    "VirtualDrops",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
            }

            string fileName = FileService.SanitizeFileSystemName(
                item.DisplayName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = item.Kind == ShellIdListItemKind.Application
                    ? "Application"
                    : "ShellObject";
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
                createdShortcutCount++;
            }
            catch (Exception ex)
            {
                try { File.Delete(shortcutPath); } catch { }
                App.Log(
                    $"[DropTarget] Failed to materialize Shell item " +
                    $"target='{item.ParsingName}': {ex.Message}");
            }
        }

        if (temporaryDirectory is not null && createdShortcutCount == 0)
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }

        itemOrigins = origins;
        return paths;
    }

    private static IReadOnlyList<string> GetDroppedFiles(IntPtr hDrop)
    {
        var paths = new List<string>();
        uint count = OleDropTargetNativeMethods.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            uint length = OleDropTargetNativeMethods.DragQueryFile(hDrop, i, null, 0);
            if (length == 0)
            {
                continue;
            }

            var builder = new System.Text.StringBuilder((int)length + 1);
            uint copied = OleDropTargetNativeMethods.DragQueryFile(hDrop, i, builder, (uint)builder.Capacity);
            if (copied > 0)
            {
                paths.Add(builder.ToString());
            }
        }

        return paths;
    }

    // Internal for the materialization worker, which runs the whole stream
    // read on its own apartment after the drop callback returned.
    internal static IReadOnlyList<string> ExtractVirtualFiles(NativeOleDataObject dataObject)
    {
        var descriptorFormat = new NativeFormatEtc
        {
            ClipboardFormat = s_fileGroupDescriptorFormat,
            TargetDevice = IntPtr.Zero,
            Aspect = DVASPECT_CONTENT,
            Index = -1,
            MediumType = TYMED_HGLOBAL,
        };
        int descriptorHr = dataObject.GetData(
            ref descriptorFormat,
            out NativeStorageMedium descriptorMedium);
        if (descriptorHr != S_OK || descriptorMedium.Content == IntPtr.Zero)
        {
            App.LogVerbose(
                "[DropTarget] Virtual extraction: FILEGROUPDESCRIPTOR GetData " +
                $"failed hr=0x{descriptorHr:X8} empty={descriptorMedium.Content == IntPtr.Zero}");
            return [];
        }

        List<FILEDESCRIPTORW> descriptors;
        try
        {
            descriptors = ReadVirtualFileDescriptors(descriptorMedium.Content);
        }
        finally
        {
            OleDropTargetNativeMethods.ReleaseStgMedium(ref descriptorMedium);
        }

        if (descriptors.Count == 0)
        {
            App.LogVerbose(
                "[DropTarget] Virtual extraction: descriptor parse produced " +
                "no entries");
            return [];
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>();
        var seenTopLevelEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One drag can materialize many streams; the budget is per drop so a
        // crafted multi-descriptor payload cannot multiply the cap.
        long remainingBudgetBytes = MaxVirtualDropBytes;
        for (int index = 0; index < descriptors.Count; index++)
        {
            FILEDESCRIPTORW descriptor = descriptors[index];
            bool isDirectory = (descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (!DropStagingFileOps.TryBuildVirtualDropDestinationPath(
                    temporaryDirectory,
                    descriptor.cFileName,
                    isDirectory,
                    index,
                    out string destinationPath,
                    out string topLevelEntryPath))
            {
                App.Log(
                    $"[DropTarget] Rejected unsafe or invalid virtual descriptor " +
                    $"index={index} name='{descriptor.cFileName}'");
                continue;
            }

            if (isDirectory)
            {
                if (seenTopLevelEntries.Add(topLevelEntryPath))
                {
                    paths.Add(topLevelEntryPath);
                }

                continue;
            }

            long? declaredFileSize = (descriptor.dwFlags & FD_FILESIZE) != 0
                ? unchecked((long)(((ulong)descriptor.nFileSizeHigh << 32) | descriptor.nFileSizeLow))
                : null;
            if (TrySaveVirtualFileContents(
                    dataObject,
                    index,
                    destinationPath,
                    ref remainingBudgetBytes,
                    out bool budgetExceeded,
                    declaredFileSize))
            {
                bool isTopLevelFile = string.Equals(
                    destinationPath,
                    topLevelEntryPath,
                    StringComparison.OrdinalIgnoreCase);
                string resolvedPath = isTopLevelFile
                    ? VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                        destinationPath)
                    : destinationPath;
                if (isTopLevelFile &&
                    !string.Equals(
                        resolvedPath,
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    App.Log(
                        $"[DropTarget] Added missing virtual-file extension " +
                        $"source='{destinationPath}' resolved='{resolvedPath}'");
                }

                // The stream came from a web context (browser drag); a local
                // file without MOTW would bypass SmartScreen scrutiny.
                ZoneIdentifierWriter.TryMarkFile(
                    resolvedPath,
                    ZoneIdentifierWriter.VirtualDropSource);
                string entryToImport = isTopLevelFile ? resolvedPath : topLevelEntryPath;
                if (seenTopLevelEntries.Add(entryToImport))
                {
                    paths.Add(entryToImport);
                }
            }
            else if (budgetExceeded)
            {
                App.Log(
                    "[DropTarget] Virtual drop aborted: payload exceeded " +
                    $"materialization budget ({MaxVirtualDropBytes} bytes) at index={index}.");
                DropStagingFileOps.CleanupVirtualDropBatchDirectory(temporaryDirectory);
                return [];
            }
        }

        if (paths.Count == 0)
        {
            App.LogVerbose(
                $"[DropTarget] Virtual extraction: {descriptors.Count} " +
                "descriptor(s) but no stream saved (FILECONTENTS reads all " +
                "failed)");
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }

        return paths;
    }

    internal static List<FILEDESCRIPTORW> ReadVirtualFileDescriptors(IntPtr descriptorHandle)
    {
        long bufferSize = OleDropTargetNativeMethods.GlobalSize(descriptorHandle).ToInt64();
        IntPtr pointer = OleDropTargetNativeMethods.GlobalLock(descriptorHandle);
        if (pointer == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = Marshal.ReadInt32(pointer);
            if (count <= 0 || count > 4096)
            {
                return [];
            }

            // The declared count comes from the drag source and cannot be
            // trusted: a small HGLOBAL with a large count would make the loop
            // below read past the allocation. Clamp to what the buffer can
            // actually hold, exactly like the Shell IDList reader above.
            int descriptorSize = Marshal.SizeOf<FILEDESCRIPTORW>();
            if (bufferSize > 0)
            {
                int capacity = (int)Math.Max(
                    0,
                    (bufferSize - sizeof(uint)) / descriptorSize);
                if (capacity < count)
                {
                    App.Log(
                        $"[DropTarget] FileGroupDescriptor count={count} exceeds " +
                        $"buffer={bufferSize}; clamped to {capacity}.");
                    count = capacity;
                }
            }

            if (count == 0)
            {
                return [];
            }

            var descriptors = new List<FILEDESCRIPTORW>(count);
            IntPtr descriptorPointer = IntPtr.Add(pointer, sizeof(uint));
            for (int index = 0; index < count; index++)
            {
                descriptors.Add(Marshal.PtrToStructure<FILEDESCRIPTORW>(
                    IntPtr.Add(descriptorPointer, index * descriptorSize)));
            }

            return descriptors;
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(descriptorHandle);
        }
    }

    private static bool TrySaveVirtualFileContents(
        NativeOleDataObject dataObject,
        int index,
        string destinationPath,
        ref long remainingBudgetBytes,
        out bool budgetExceeded,
        long? declaredFileSize = null)
    {
        budgetExceeded = false;
        // Try TYMED_ISTREAM first — FileContents from browser drag sources
        // (Chrome / Edge / Firefox) is strictly an IStream. Asking for the
        // combined mask TYMED_ISTREAM | TYMED_HGLOBAL in one FORMATETC is
        // unreliable across OLE sources: some reject the combined mask
        // outright (returning DV_E_FORMATETC), which made the widget silently
        // ignore browser drops even though DragEnter/Drop fired. Fall back to
        // TYMED_HGLOBAL for sources that provide the contents as a memory blob.
        NativeStorageMedium contentsMedium = default;
        uint actualTymed = 0;
        IntPtr actualMedium = IntPtr.Zero;
        foreach (uint tymed in new uint[] { TYMED_ISTREAM, TYMED_HGLOBAL })
        {
            var contentsFormat = new NativeFormatEtc
            {
                ClipboardFormat = s_fileContentsFormat,
                TargetDevice = IntPtr.Zero,
                Aspect = DVASPECT_CONTENT,
                Index = index,
                MediumType = tymed,
            };
            int hr = dataObject.GetData(ref contentsFormat, out contentsMedium);
            if (hr == S_OK && contentsMedium.Content != IntPtr.Zero)
            {
                actualTymed = contentsMedium.MediumType;
                actualMedium = contentsMedium.Content;
                break;
            }
            // Release any partially populated medium before retrying with a
            // different tymed (GetData may have set unionMember on failure).
            if (contentsMedium.Content != IntPtr.Zero)
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref contentsMedium);
                contentsMedium = default;
            }
        }

        if (actualMedium == IntPtr.Zero)
        {
            App.Log(
                $"[DropTarget] No FileContents payload for virtual file index={index} " +
                $"destination='{destinationPath}' (browser may not have provided a stream)");
            return false;
        }

        try
        {
            if ((actualTymed & TYMED_ISTREAM) != 0)
            {
                SaveComStream(actualMedium, destinationPath, ref remainingBudgetBytes);
                return true;
            }

            if ((actualTymed & TYMED_HGLOBAL) != 0)
            {
                SaveGlobalMemory(actualMedium, destinationPath, ref remainingBudgetBytes, declaredFileSize);
                return true;
            }

            App.Log(
                $"[DropTarget] Unexpected FileContents tymed=0x{actualTymed:X} " +
                $"for index={index}");
            return false;
        }
        catch (IOException ex) when (remainingBudgetBytes <= 0)
        {
            budgetExceeded = true;
            App.Log(
                $"[DropTarget] Virtual file index={index} exceeded the " +
                $"materialization budget ({MaxVirtualDropBytes} bytes): {ex.Message}");
            try { File.Delete(destinationPath); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to save virtual file '{destinationPath}': {ex.Message}");
            try { File.Delete(destinationPath); } catch { }
            return false;
        }
        finally
        {
            OleDropTargetNativeMethods.ReleaseStgMedium(ref contentsMedium);
        }
    }

    private static void SaveComStream(
        IntPtr streamPointer,
        string destinationPath,
        ref long remainingBudgetBytes)
    {
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long before = remainingBudgetBytes;
        try
        {
            NativeComStreamReader.CopyTo(streamPointer, destination, remainingBudgetBytes);
            remainingBudgetBytes = Math.Max(
                0,
                remainingBudgetBytes - destination.Length);
        }
        catch
        {
            remainingBudgetBytes = Math.Max(0, before - destination.Length);
            throw;
        }
    }

    internal static long ResolveEffectiveGlobalMemoryLength(long globalSize, long? declaredFileSize)
    {
        if (declaredFileSize is >= 0 && declaredFileSize.Value < globalSize)
        {
            return declaredFileSize.Value;
        }

        return globalSize;
    }

    private static void SaveGlobalMemory(
        IntPtr memoryHandle,
        string destinationPath,
        ref long remainingBudgetBytes,
        long? declaredFileSize = null)
    {
        long rawSize = OleDropTargetNativeMethods.GlobalSize(memoryHandle).ToInt64();
        long size = ResolveEffectiveGlobalMemoryLength(rawSize, declaredFileSize);
        if (size > remainingBudgetBytes)
        {
            remainingBudgetBytes = 0;
            throw new IOException(
                $"Virtual file memory payload of {size} bytes exceeds the budget.");
        }

        if (size < 0 || size > int.MaxValue)
        {
            throw new IOException("Virtual file memory payload is too large.");
        }

        IntPtr pointer = OleDropTargetNativeMethods.GlobalLock(memoryHandle);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Could not lock virtual file memory payload.");
        }

        try
        {
            var bytes = new byte[(int)size];
            if (bytes.Length > 0)
            {
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
            }

            File.WriteAllBytes(destinationPath, bytes);
            remainingBudgetBytes -= size;
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(memoryHandle);
        }
    }
}
