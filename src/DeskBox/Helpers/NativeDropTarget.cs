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
    uint KeyState);

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
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
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

    // ── State ──

    private readonly IntPtr _hwnd;
    private readonly NativeDropTargetComObject _comObject;
    private readonly Func<bool> _defaultMoveProvider;
    private readonly Func<bool>? _followWindowsProvider;
    private readonly Func<uint, NativeDropDescriptionText?>? _dropDescriptionProvider;
    private readonly Func<bool>? _useShellVisualProvider;
    private readonly NativeDropImageManager? _dropImageManager;
    private nint _activeDataObject;
    private bool _hasDropDescriptionAttempt;
    private uint _lastDropDescriptionEffect;
    private NativeDropDescriptionText? _lastDropDescriptionText;
    private bool _shellVisualActive;
    private bool _registered;
    private bool _rightButtonDragActive;
    private IReadOnlyList<string> _dragPathHints = [];

    private sealed record ShellApplicationDropItem(
        string AppUserModelId,
        string DisplayName);

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
    /// Runs inside the OLE Drop callback before the import events, while the
    /// source data object is alive, so the handler can delegate the drop to a
    /// Shell drop target (drop onto an application shortcut). Returning
    /// Handled=true consumes the drop; no import events are raised.
    /// </summary>
    internal Func<NativeDropLaunchRequest, ShellDropLaunchResult>? LaunchDropHandler
    {
        get;
        set;
    }

    /// <summary>
    /// Returns the widget id currently hosted by this drop target's window, or
    /// null when unknown. A native drag-out stamped with the same id is the
    /// drag returning to its own widget: the drag is refused (no launch, no
    /// import, no relocation) so the source files stay untouched.
    /// </summary>
    internal Func<string?>? SelfDragSourceWidgetProvider { get; set; }

    private bool _isSelfSourceDrag;

    private const uint DropeffectNone = 0;

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

    internal bool IsRegistered => _registered;

    static NativeDropTarget()
    {
        s_fileGroupDescriptorFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileGroupDescriptorW");
        s_fileContentsFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("FileContents");
        s_shellIdListFormat = (ushort)OleDropTargetNativeMethods.RegisterClipboardFormatW("Shell IDList Array");

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
        try
        {
            _dropImageManager = NativeDropImageManager.TryCreate();
        }
        catch
        {
            _dropImageManager = null;
        }
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
                $"shellImageHelper={_dropImageManager is not null}");
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
        _dropImageManager?.Dispose();
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
        EndNativeDragVisual();
        _rightButtonDragActive =
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        uint allowedEffects = effect;
        InspectDragData(dataObject);
        _isSelfSourceDrag = IsSelfSourceDrag(dataObject);
        _dragPathHints = HasFileData && !HasVirtualFileData
            ? TryExtractHDropPathHints(dataObject)
            : [];
        DragEnterEvent?.Invoke(point.X, point.Y, HasFileData);

        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            HasShellApplicationData,
            _defaultMoveProvider(),
            followWindows: GetFollowWindowsSetting());
        if (_isSelfSourceDrag)
        {
            // The drag left this widget and came back; nothing here may
            // accept it. 1a scope: reorder-through-native-drag is not wired
            // yet, so the honest feedback is the "no drop" cursor.
            effect = DropeffectNone;
            return S_OK;
        }

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
        uint allowedEffects = effect;
        _rightButtonDragActive |=
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        DragOverEvent?.Invoke(point.X, point.Y);

        effect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            HasFileData,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            HasShellApplicationData,
            _defaultMoveProvider(),
            followWindows: GetFollowWindowsSetting());
        if (_isSelfSourceDrag)
        {
            effect = DropeffectNone;
            return S_OK;
        }

        UpdateShellVisual(point, effect);
        return S_OK;
    }

    internal int OnDragLeave()
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
        if (_isSelfSourceDrag)
        {
            // The drag returned to its own widget: refuse before the launch
            // delegate and the import pipeline can touch anything, and keep
            // the source files exactly where they are.
            ClearActiveDropDescriptionAndReleaseDataObject();
            _shellVisualActive = false;
            ResetDragDataState();
            _rightButtonDragActive = false;
            _isSelfSourceDrag = false;
            effect = DropeffectNone;
            App.Log(
                "[DropTarget] NativeDrop refused self-source drag " +
                "(returned to its own widget)");
            return S_OK;
        }

        uint allowedEffects = effect;
        bool shellApplicationDrop = HasShellApplicationData;
        bool virtualFileDrop = HasVirtualFileData;
        bool defaultMove = _defaultMoveProvider();
        bool followWindows = GetFollowWindowsSetting();
        bool rightButtonDrag = _rightButtonDragActive ||
            NativeDropEffectPolicy.IsRightButtonDrag(keyState);
        uint feedbackEffect = NativeDropEffectPolicy.ResolveFeedbackEffect(
            hasFileData: true,
            HasVirtualFileData,
            keyState,
            allowedEffects,
            shellApplicationDrop,
            defaultMove,
            followWindows: followWindows);
        IReadOnlyList<string> paths = shellApplicationDrop
            ? TryExtractShellApplicationShortcuts(dataObject)
            : [];
        bool createdShellApplicationLinks = paths.Count > 0;
        bool containsTemporaryFiles = createdShellApplicationLinks;
        if (paths.Count == 0)
        {
            (paths, containsTemporaryFiles) = TryExtractFilePaths(dataObject);
        }
        bool copyRequested = NativeDropEffectPolicy.ShouldCopyMappedTransfer(
            containsTemporaryFiles,
            keyState,
            defaultMove,
            followWindows: followWindows);
        bool shortcutRequested = createdShellApplicationLinks ||
            NativeDropEffectPolicy.ShouldCreateMappedShortcut(
                containsTemporaryFiles,
                keyState);
        if (_shellVisualActive)
        {
            _dropImageManager?.Drop(
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
                effect = NativeDropEffectPolicy.None;
                return S_OK;
            }

            if (launch.Handled)
            {
                effect = launch.Effect;
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
                return S_OK;
            }
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
                keyState));
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

        return S_OK;
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

    private void InspectDragData(nint dataObject)
    {
        bool hasHDropData = TryHasHDropData(dataObject);
        bool hasVirtualDescriptorData = TryHasVirtualFileData(dataObject);
        HasVirtualFileData = NativeDropEffectPolicy.IsVirtualOnlyFileData(
            hasHDropData,
            hasVirtualDescriptorData);
        HasShellApplicationData =
            !hasHDropData && TryHasShellApplicationData(dataObject);
        HasFileData =
            hasHDropData || HasVirtualFileData || HasShellApplicationData;
    }

    private void ResetDragDataState()
    {
        HasFileData = false;
        HasVirtualFileData = false;
        HasShellApplicationData = false;
        _dragPathHints = [];
        _isSelfSourceDrag = false;
    }

    private bool IsSelfSourceDrag(nint dataObject)
    {
        if (SelfDragSourceWidgetProvider is null)
        {
            return false;
        }

        if (!NativeFileDragOut.TryReadSourceTag(
                dataObject,
                out string sourceWidgetId,
                out _))
        {
            return false;
        }

        string? hostedWidgetId = SelfDragSourceWidgetProvider();
        return hostedWidgetId is not null &&
            string.Equals(sourceWidgetId, hostedWidgetId, StringComparison.Ordinal);
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
                _dropImageManager?.DragLeave();
                _shellVisualActive = false;
                ClearActiveDropDescription();
            }
            return;
        }

        UpdateActiveDropDescription(effect);
        if (!_shellVisualActive)
        {
            _dropImageManager?.DragEnter(
                _hwnd,
                _activeDataObject,
                point,
                effect);
            _shellVisualActive =
                _dropImageManager?.IsDragActive == true;
        }
        else
        {
            _dropImageManager?.DragOver(point, effect);
        }

        _dropImageManager?.Show(visible: true);
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
        _dropImageManager?.DragLeave();
        _shellVisualActive = false;
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
        ClearActiveDropDescription();
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

    private static bool TryHasShellApplicationData(IntPtr pDataObj)
    {
        if (!TryQueryFormat(
                pDataObj,
                s_shellIdListFormat,
                TYMED_HGLOBAL,
                -1))
        {
            return false;
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
                return false;
            }

            try
            {
                return ReadShellApplicationDropItems(medium.Content).Count > 0;
            }
            finally
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            return false;
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

    private (IReadOnlyList<string> Paths, bool ContainsTemporaryFiles) TryExtractFilePaths(IntPtr pDataObj)
    {
        if (pDataObj == IntPtr.Zero)
        {
            return ([], false);
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
                        return (paths, false);
                    }
                }
                finally
                {
                    OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
                }
            }

            IReadOnlyList<string> virtualPaths = ExtractVirtualFiles(dataObject);
            return (virtualPaths, virtualPaths.Count > 0);
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Failed to extract file paths: {ex.Message}");
            return ([], false);
        }
    }

    private static IReadOnlyList<string> TryExtractShellApplicationShortcuts(
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
            if (dataObject.GetData(
                    ref format,
                    out NativeStorageMedium medium) != S_OK ||
                medium.Content == IntPtr.Zero)
            {
                return [];
            }

            IReadOnlyList<ShellApplicationDropItem> applications;
            try
            {
                applications = ReadShellApplicationDropItems(medium.Content);
            }
            finally
            {
                OleDropTargetNativeMethods.ReleaseStgMedium(ref medium);
            }

            return MaterializeShellApplicationShortcuts(applications);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DropTarget] Failed to extract AppsFolder Shell items: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<ShellApplicationDropItem>
        ReadShellApplicationDropItems(IntPtr shellIdListHandle)
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

            var applications = new List<ShellApplicationDropItem>();
            var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    if (!TryNormalizePackagedApplicationId(
                            parsingName,
                            out string appUserModelId) ||
                        !seenAppIds.Add(appUserModelId))
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

                    applications.Add(new ShellApplicationDropItem(
                        appUserModelId,
                        displayName));
                }
                finally
                {
                    OleDropTargetNativeMethods.ILFree(absolutePidl);
                }
            }

            return applications;
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

    private static IReadOnlyList<string> MaterializeShellApplicationShortcuts(
        IReadOnlyList<ShellApplicationDropItem> applications)
    {
        if (applications.Count == 0)
        {
            return [];
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>(applications.Count);
        foreach (ShellApplicationDropItem application in applications)
        {
            string fileName = FileService.SanitizeFileSystemName(
                application.DisplayName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "Application";
            }

            string shortcutPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, $"{fileName}.lnk"));
            try
            {
                ShortcutHelper.CreateShellApplicationShortcut(
                    shortcutPath,
                    application.AppUserModelId,
                    application.DisplayName);
                paths.Add(shortcutPath);
                App.Log(
                    $"[DropTarget] Materialized AppsFolder shortcut " +
                    $"app='{application.AppUserModelId}' name='{application.DisplayName}'");
            }
            catch (Exception ex)
            {
                try { File.Delete(shortcutPath); } catch { }
                App.Log(
                    $"[DropTarget] Failed to materialize AppsFolder shortcut " +
                    $"app='{application.AppUserModelId}': {ex.Message}");
            }
        }

        if (paths.Count == 0)
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }

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

    private static IReadOnlyList<string> ExtractVirtualFiles(NativeOleDataObject dataObject)
    {
        var descriptorFormat = new NativeFormatEtc
        {
            ClipboardFormat = s_fileGroupDescriptorFormat,
            TargetDevice = IntPtr.Zero,
            Aspect = DVASPECT_CONTENT,
            Index = -1,
            MediumType = TYMED_HGLOBAL,
        };
        if (dataObject.GetData(
                ref descriptorFormat,
                out NativeStorageMedium descriptorMedium) != S_OK ||
            descriptorMedium.Content == IntPtr.Zero)
        {
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
            return [];
        }

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var paths = new List<string>();
        // One drag can materialize many streams; the budget is per drop so a
        // crafted multi-descriptor payload cannot multiply the cap.
        long remainingBudgetBytes = MaxVirtualDropBytes;
        for (int index = 0; index < descriptors.Count; index++)
        {
            FILEDESCRIPTORW descriptor = descriptors[index];
            if ((descriptor.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                continue;
            }

            string fileName = FileService.SanitizeFileSystemName(
                Path.GetFileName(descriptor.cFileName));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"Dropped file {index + 1}";
            }

            string destinationPath = FileService.GetAvailablePath(
                Path.Combine(temporaryDirectory, fileName));
            if (TrySaveVirtualFileContents(
                    dataObject,
                    index,
                    destinationPath,
                    ref remainingBudgetBytes))
            {
                string resolvedPath =
                    VirtualDropFileNameResolver.AddMissingExtensionFromContent(
                        destinationPath);
                if (!string.Equals(
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
                paths.Add(resolvedPath);
            }
        }

        if (paths.Count == 0)
        {
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
        ref long remainingBudgetBytes)
    {
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
                SaveGlobalMemory(actualMedium, destinationPath, ref remainingBudgetBytes);
                return true;
            }

            App.Log(
                $"[DropTarget] Unexpected FileContents tymed=0x{actualTymed:X} " +
                $"for index={index}");
            return false;
        }
        catch (IOException ex) when (remainingBudgetBytes <= 0)
        {
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

    private static void SaveGlobalMemory(IntPtr memoryHandle, string destinationPath, ref long remainingBudgetBytes)
    {
        long size = OleDropTargetNativeMethods.GlobalSize(memoryHandle).ToInt64();
        if (size < 0 || size > int.MaxValue)
        {
            throw new IOException("Virtual file memory payload is too large.");
        }

        if (size > remainingBudgetBytes)
        {
            remainingBudgetBytes = 0;
            throw new IOException(
                $"Virtual file memory payload of {size} bytes exceeds the budget.");
        }

        IntPtr pointer = OleDropTargetNativeMethods.GlobalLock(memoryHandle);
        if (pointer == IntPtr.Zero)
        {
            throw new IOException("Could not lock virtual file memory payload.");
        }

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            File.WriteAllBytes(destinationPath, bytes);
            remainingBudgetBytes -= size;
        }
        finally
        {
            OleDropTargetNativeMethods.GlobalUnlock(memoryHandle);
        }
    }
}
