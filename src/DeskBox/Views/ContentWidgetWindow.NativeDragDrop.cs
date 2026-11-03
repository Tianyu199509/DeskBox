using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private static readonly UIntPtr ContentFileDropSubclassId = new(0xDDB1);
    private const string ContentBridgeWindowClass =
        "Microsoft.UI.Content.DesktopChildSiteBridge";

    private Win32Helper.SubclassProc? _nativeFileDropSubclassProc;
    private readonly Dictionary<IntPtr, NativeDropTarget>
        _nativeFileDropTargets = [];
    private bool _isNativeFileDropSubclassInstalled;
    private DragEventHandler? _groupFileDropDragEnterHandler;
    private DragEventHandler? _groupFileDropDragOverHandler;
    private DragEventHandler? _groupFileDropDragLeaveHandler;
    private DragEventHandler? _groupFileDropDropHandler;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer?
        _nativeFileDropRegistrationTimer;
    private int _nativeFileDropRegistrationAttempts;
    private bool _isXamlRoutedDragActive;
    private long _inboundGestureEpoch;
    private long _lastXamlClaimedGestureEpoch = -1;
    private string? _lastXamlClaimedDragSessionId;
    private string? _nativeHoveredGroupTitleDropMemberId;
    private long _lastNativeClaimedGestureId;
    private long _lastNativeDropCompletedTick64;
    private string? _lastNativeDropFirstPath;
    private const uint WmParentNotify = 0x0210;
    private const uint WmCreateChildEvent = 0x0001;

    private sealed record PendingNativeDragPointerFrame(
        int ScreenX,
        int ScreenY,
        bool HasFileData,
        IReadOnlyList<string>? PathHints,
        long PointerGeneration);

    private long _nativeFileDropPointerGeneration;
    private PendingNativeDragPointerFrame? _pendingNativeDragPointerFrame;
    private int _nativeDragPointerUiQueued;
    private string[] _cachedNativeDropPathHints = [];
    private IReadOnlyList<string>? _cachedNativeDropSourceRef;
    private WidgetItem? _nativeFileDropItemTarget;
    // DropIntentEvent is raised immediately before the legacy DropEvent. The
    // flag prevents the compatibility callback from queueing the same import a
    // second time while keeping DropEvent available to older consumers.
    private bool _nativeDropIntentHandledForLegacyCallback;
    // The application shortcut under the drag pointer, when the drop should
    // open the dragged files with the linked application instead of importing.
    private WidgetItem? _nativeFileDropLaunchTarget;

    /// <summary>
    /// File members in a group share this window's HWND. Install the same two
    /// native input paths as a standalone file widget: WM_DROPFILES for apps
    /// such as WeChat and an OLE IDropTarget for browsers' virtual files.
    /// </summary>
    protected override void ConfigureWindowExtra()
    {
        Win32Helper.AllowShellDragDropMessages(HWnd);
        InstallNativeFileDropBridge(registerDropTarget: false);
    }

    private void InstallNativeFileDropBridge(bool registerDropTarget = true)
    {
        _nativeFileDropSubclassProc ??= CallbackDepth.WrapSubclassProc(NativeFileDropSubclassProc);
        if (!_isNativeFileDropSubclassInstalled)
        {
            _isNativeFileDropSubclassInstalled = Win32Helper.SetWindowSubclass(
                HWnd,
                _nativeFileDropSubclassProc,
                ContentFileDropSubclassId,
                UIntPtr.Zero);
            App.LogVerbose(
                $"[DropDiagnostic] content id={_config.Id} stage=NativeSubclassInstall " +
                $"hwnd=0x{HWnd.ToInt64():X} installed={_isNativeFileDropSubclassInstalled}");
        }

        if (!registerDropTarget)
        {
            return;
        }

        PruneDestroyedNativeFileDropTargets();

        foreach (IntPtr targetWindow in GetNativeFileDropWindowHandles())
        {
            if (_nativeFileDropTargets.TryGetValue(
                    targetWindow,
                    out NativeDropTarget? existingTarget))
            {
                existingTarget.Register();
                continue;
            }

            try
            {
                var target = new NativeDropTarget(
                    targetWindow,
                    ShouldDefaultNativeFileDropToMove,
                    CreateNativeFileDropDescription,
                    ShouldUseNativeFileDropVisual,
                    ShouldFollowWindowsNativeFileDrop);
                target.DragEnterEvent += NativeFileDropTarget_DragEnterEvent;
                target.DragOverEvent += NativeFileDropTarget_DragOverEvent;
                target.DragLeaveEvent += NativeFileDropTarget_DragLeaveEvent;
                target.LaunchDropHandler = HandleNativeLaunchDrop;
                target.LaunchTargetPathProbe = () => _nativeFileDropLaunchTarget?.Path;
                target.UndisplayablePathProbe = IsNativeFileDropPathUndisplayable;
                target.SameVolumeProbe = IsNativeDropOnSameVolume;
                target.HasMappedFolderProbe = HasNativeDropMappedFolder;
                target.TargetKindProbe = GetNativeDropTargetKind;
                target.ImportBusyProbe = IsNativeDropImportBusy;
                target.UndisplayableDropBlocked += NativeFileDropTarget_UndisplayableDropBlocked;
                target.ZeroResultDropBlocked += NativeFileDropTarget_ZeroResultDropBlocked;
                target.ImportBusyDropBlocked += NativeFileDropTarget_ImportBusyDropBlocked;
                target.DropIntentEvent += NativeFileDropTarget_DropIntentEvent;
                target.DropEvent += NativeFileDropTarget_DropEvent;
                target.Register();
                _nativeFileDropTargets[targetWindow] = target;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DropTarget] Failed to register grouped IDropTarget " +
                    $"hwnd=0x{targetWindow.ToInt64():X}: {ex.Message}");
            }
        }
    }

    private IReadOnlyList<IntPtr> GetNativeFileDropWindowHandles()
    {
        var handles = new List<IntPtr> { HWnd };
        _ = Win32Helper.EnumChildWindows(
            HWnd,
            (childWindow, _) =>
            {
                var className = new System.Text.StringBuilder(128);
                int length = Win32Helper.GetClassName(
                    childWindow,
                    className,
                    className.Capacity);
                if (length > 0 && string.Equals(
                        className.ToString(),
                        ContentBridgeWindowClass,
                        StringComparison.Ordinal))
                {
                    handles.Add(childWindow);
                }

                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private void PruneDestroyedNativeFileDropTargets()
    {
        if (_nativeFileDropTargets.Count == 0)
        {
            return;
        }

        List<IntPtr>? staleWindows = null;
        foreach ((IntPtr window, NativeDropTarget target) in _nativeFileDropTargets)
        {
            if (window != HWnd && !Win32Helper.IsWindow(window))
            {
                staleWindows ??= [];
                staleWindows.Add(window);
                target.Dispose();
            }
        }

        if (staleWindows is not null)
        {
            foreach (IntPtr stale in staleWindows)
            {
                _nativeFileDropTargets.Remove(stale);
            }
        }
    }

    private bool HasRegisteredContentBridgeDropTarget()
    {
        foreach ((IntPtr window, NativeDropTarget target) in
                 _nativeFileDropTargets)
        {
            if (window != HWnd &&
                Win32Helper.IsWindow(window) &&
                target.IsRegistered)
            {
                return true;
            }
        }

        return false;
    }

    private void QueueNativeFileDropTargetRegistration()
    {
        if (!IsClosing)
        {
            InstallNativeFileDropBridge();
        }

        if (HasRegisteredContentBridgeDropTarget() ||
            _nativeFileDropRegistrationTimer is not null)
        {
            return;
        }

        _nativeFileDropRegistrationAttempts = 0;
        _nativeFileDropRegistrationTimer = DispatcherQueue.CreateTimer();
        _nativeFileDropRegistrationTimer.Interval =
            TimeSpan.FromMilliseconds(75);
        _nativeFileDropRegistrationTimer.IsRepeating = true;
        _nativeFileDropRegistrationTimer.Tick +=
            NativeFileDropRegistrationTimer_Tick;
        _nativeFileDropRegistrationTimer.Start();
    }

    private void NativeFileDropRegistrationTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (IsClosing)
        {
            ReleaseNativeFileDropRegistrationTimer();
            return;
        }

        _nativeFileDropRegistrationAttempts++;
        InstallNativeFileDropBridge();
        if (HasRegisteredContentBridgeDropTarget())
        {
            ReleaseNativeFileDropRegistrationTimer();
            return;
        }

        if (_nativeFileDropRegistrationAttempts >= 40)
        {
            App.Log(
                $"[DropTarget] WinUI content child was not available for registration " +
                $"hwnd=0x{HWnd.ToInt64():X}");
            ReleaseNativeFileDropRegistrationTimer();
        }
    }

    private void ReleaseNativeFileDropRegistrationTimer()
    {
        if (_nativeFileDropRegistrationTimer is null)
        {
            return;
        }

        _nativeFileDropRegistrationTimer.Stop();
        _nativeFileDropRegistrationTimer.Tick -=
            NativeFileDropRegistrationTimer_Tick;
        _nativeFileDropRegistrationTimer = null;
    }

    internal bool HasActiveNativeShellDragWindow =>
        _nativeFileDropTargets.Values.Any(static target => target.HasActiveShellDragWindow);

    private bool ShouldUseNativeFileDropVisual()
    {
        return CurrentContent switch
        {
            FileWidgetContentAdapter
            {
                SuppressesNativeShellDragVisual: false
            } => true,
            QuickCaptureWidgetContentAdapter => true,
            ClipboardWidgetContentAdapter => true,
            TodoWidgetContentAdapter => true,
            CustomWidgetContentAdapter { ViewModel.AcceptsFileDrops: true } => true,
            _ => false
        };
    }

    private bool ShouldDefaultNativeFileDropToMove()
    {
        return CurrentContent is FileWidgetContentAdapter &&
               string.Equals(
                   SettingsService.Settings.FileWidget.ManagedDropAction,
                   Services.SettingsService.ManagedDropActionMove,
                   StringComparison.Ordinal);
    }

    private bool ShouldFollowWindowsNativeFileDrop()
    {
        return CurrentContent is FileWidgetContentAdapter &&
               string.Equals(
                   SettingsService.Settings.FileWidget.ManagedDropAction,
                   Services.SettingsService.ManagedDropActionFollowWindows,
                   StringComparison.Ordinal);
    }

    private NativeDropDescriptionText? CreateNativeFileDropDescription(
        uint effect)
    {
        if (effect == NativeDropEffectPolicy.None)
        {
            return null;
        }

        if (CurrentContent is ClipboardWidgetContentAdapter)
        {
            return new NativeDropDescriptionText(
                "%1",
                App.Current.LocalizationService.T("Clipboard.Drop.AddToHistory"));
        }

        if (CurrentContent is QuickCaptureWidgetContentAdapter)
        {
            return new NativeDropDescriptionText(
                "%1",
                App.Current.LocalizationService.T(
                    "Widget.Compact.QuickCaptureDropHint"));
        }

        if (CurrentContent is TodoWidgetContentAdapter)
        {
            return new NativeDropDescriptionText(
                "%1",
                App.Current.LocalizationService.T(
                    "Widget.Compact.TodoDropHint"));
        }

        if (CurrentContent is CustomWidgetContentAdapter customDrop &&
            customDrop.ViewModel.AcceptsFileDrops)
        {
            return new NativeDropDescriptionText(
                "%1",
                App.Current.LocalizationService.T(
                    customDrop.ViewModel.EffectiveDropMode == "copy"
                        ? "CustomWidget.DropHint.Copy"
                        : "CustomWidget.DropHint.Link"));
        }

        if (CurrentContent is FileWidgetContentAdapter &&
            _nativeFileDropLaunchTarget is
            {
                Path.Length: > 0,
                Name.Length: > 0
            } launchTarget)
        {
            string openWith = ToShellDropDescriptionMessage(
                App.Current.LocalizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    launchTarget.Name));
            return openWith.Contains("%1", StringComparison.Ordinal)
                ? new NativeDropDescriptionText(openWith, launchTarget.Name)
                : new NativeDropDescriptionText("%1", openWith);
        }

        // The Recycle Bin namespace tile recycles the drop: its description
        // must say so, or the generic per-widget move/copy caption would
        // advertise an import that never happens.
        if (CurrentContent is FileWidgetContentAdapter &&
            _nativeFileDropItemTarget is { } descriptionBinCandidate &&
            ItemDropBehaviorPolicy.Resolve(descriptionBinCandidate) ==
                ItemDropBehavior.RecycleBinDelete)
        {
            string binMessage = ToShellDropDescriptionMessage(
                App.Current.LocalizationService.T(
                    "Widget.DropOnRecycleBinHint"));
            return binMessage.Contains("%1", StringComparison.Ordinal)
                ? new NativeDropDescriptionText(binMessage, string.Empty)
                : new NativeDropDescriptionText("%1", binMessage);
        }

        if (CurrentContent is FileWidgetContentAdapter &&
            _nativeFileDropItemTarget is WidgetStackItem stack)
        {
            string stackMessage = ToShellDropDescriptionMessage(
                App.Current.LocalizationService.T(
                    "Widget.Stack.DragCaption.Import"));
            return stackMessage.Contains("%1", StringComparison.Ordinal)
                ? new NativeDropDescriptionText(stackMessage, stack.Name)
                : null;
        }

        string? localizationKey = effect switch
        {
            NativeDropEffectPolicy.Copy => "Widget.CopyToFolder",
            NativeDropEffectPolicy.Move => "Widget.MoveToFolder",
            NativeDropEffectPolicy.Link => "Widget.CreateShortcut",
            _ => null
        };
        if (localizationKey is null)
        {
            return null;
        }

        string message = ToShellDropDescriptionMessage(
            App.Current.LocalizationService.T(localizationKey));
        if (effect == NativeDropEffectPolicy.Link &&
            !message.Contains("%1", StringComparison.Ordinal))
        {
            return new NativeDropDescriptionText("%1", message);
        }
        if (!message.Contains("%1", StringComparison.Ordinal))
        {
            return null;
        }

        string targetName = _nativeFileDropItemTarget is
            {
                Name.Length: > 0
            } folder &&
            ItemDropBehaviorPolicy.Resolve(folder) == ItemDropBehavior.FolderImport
                ? folder.Name
                : string.IsNullOrWhiteSpace(_config.Name)
                    ? "DeskBox"
                    : _config.Name.Trim();
        return new NativeDropDescriptionText(message, targetName);
    }

    internal static string ToShellDropDescriptionMessage(string localized)
    {
        string message = localized.Replace(
            "{0}",
            "%1",
            StringComparison.Ordinal);
        string[] quotedMarkers =
        [
            "\"%1\"",
            "“%1”",
            "„%1“",
            "«%1»",
            "« %1 »",
            "「%1」"
        ];
        foreach (string marker in quotedMarkers)
        {
            message = message.Replace(
                marker,
                "%1",
                StringComparison.Ordinal);
        }

        return message;
    }

    /// <summary>
    /// WinUI can issue DragLeave instead of Drop for some OLE sources (notably
    /// WeChat). Observe the routed events at the surface host, including events
    /// already handled by the nested file content, and commit a pre-cached
    /// payload only when mouse release happened over this HWND.
    /// </summary>
    private void InstallGroupFileDropFallbackHandlers()
    {
        if (_groupFileDropDragEnterHandler is not null)
        {
            return;
        }

        _groupFileDropDragEnterHandler = GroupFileDrop_DragEnter;
        _groupFileDropDragOverHandler = GroupFileDrop_DragOver;
        _groupFileDropDragLeaveHandler = GroupFileDrop_DragLeave;
        _groupFileDropDropHandler = GroupFileDrop_Drop;
        RootGrid.AddHandler(
            UIElement.DragEnterEvent,
            _groupFileDropDragEnterHandler,
            handledEventsToo: true);
        RootGrid.AddHandler(
            UIElement.DragOverEvent,
            _groupFileDropDragOverHandler,
            handledEventsToo: true);
        RootGrid.AddHandler(
            UIElement.DragLeaveEvent,
            _groupFileDropDragLeaveHandler,
            handledEventsToo: true);
        RootGrid.AddHandler(
            UIElement.DropEvent,
            _groupFileDropDropHandler,
            handledEventsToo: true);
    }

    private void RemoveGroupFileDropFallbackHandlers()
    {
        StopGroupFileDropTracking(disposeCachedBatch: true);
        if (_groupFileDropDragEnterHandler is null)
        {
            return;
        }

        RootGrid.RemoveHandler(
            UIElement.DragEnterEvent,
            _groupFileDropDragEnterHandler);
        RootGrid.RemoveHandler(
            UIElement.DragOverEvent,
            _groupFileDropDragOverHandler!);
        RootGrid.RemoveHandler(
            UIElement.DragLeaveEvent,
            _groupFileDropDragLeaveHandler!);
        RootGrid.RemoveHandler(
            UIElement.DropEvent,
            _groupFileDropDropHandler!);
        _groupFileDropDragEnterHandler = null;
        _groupFileDropDragOverHandler = null;
        _groupFileDropDragLeaveHandler = null;
        _groupFileDropDropHandler = null;
    }

    private void GroupFileDrop_DragEnter(object sender, DragEventArgs e)
    {
        if (!_isXamlRoutedDragActive)
        {
            _isXamlRoutedDragActive = true;
            _inboundGestureEpoch++;
        }

        BeginGroupFileDropTracking(e.DataView, e.AllowedOperations);
    }

    private void GroupFileDrop_DragOver(object sender, DragEventArgs e)
    {
        if (!_isXamlRoutedDragActive)
        {
            _isXamlRoutedDragActive = true;
            _inboundGestureEpoch++;
        }

        BeginGroupFileDropTracking(e.DataView, e.AllowedOperations);
    }

    private void GroupFileDrop_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave is routed from nested elements too. Defer one dispatcher
        // turn and inspect the real pointer HWND so moving between children in
        // the same group does not cancel a valid drop target.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsClosing || IsPointerOverContentWindow())
            {
                return;
            }

            _isXamlRoutedDragActive = false;
            StopGroupFileDropTracking(disposeCachedBatch: true);
            App.Log(
                $"[DropDiagnostic] Group drag leave cleared tracking " +
                $"id={_config.Id} hwnd=0x{HWnd.ToInt64():X}");
        });
    }

    private void GroupFileDrop_Drop(object sender, DragEventArgs e)
    {
        // Only mark the drop gesture as claimed by XAML when a real XAML drop
        // handler actually accepted it (e.Handled == true). Unhandled routed
        // Drop notifications over margins/dead zones let NativeDropTarget complete.
        if (e.Handled)
        {
            MarkXamlDropClaimed(e.DataView);
        }
        else
        {
            _isXamlRoutedDragActive = false;
        }

        StopGroupFileDropTracking(disposeCachedBatch: true);
    }

    private void MarkXamlDropClaimed(DataPackageView? dataView)
    {
        _isXamlRoutedDragActive = false;
        _lastXamlClaimedGestureEpoch = _inboundGestureEpoch;
        if (dataView is not null &&
            dataView.Properties.TryGetValue(
                DeskBoxDragData.DragSessionIdProperty,
                out object? rawSessionId) &&
            rawSessionId is string sessionId &&
            !string.IsNullOrWhiteSpace(sessionId))
        {
            _lastXamlClaimedDragSessionId = sessionId;
        }

        foreach (NativeDropTarget target in _nativeFileDropTargets.Values)
        {
            if (target.ActiveGestureId > _lastNativeClaimedGestureId)
            {
                _lastNativeClaimedGestureId = target.ActiveGestureId;
            }
        }
    }

    private void BeginGroupFileDropTracking(
        DataPackageView dataView,
        DataPackageOperation allowedOperations)
    {
        if (IsClosing ||
            CurrentContent is not FileWidgetContentAdapter file ||
            file.IsImportBusy ||
            file.IsInternalReorderDrag(dataView))
        {
            return;
        }

        // Never materialize DataPackageView streams on hover and never synthesize
        // drops by polling VK_LBUTTON: hover materialization consumes one-shot
        // streams and premature button polling steals right-button / swapped-button
        // / touch-pen drags mid-flight.
    }

    private bool IsPointerOverContentWindow()
    {
        if (!Win32Helper.GetCursorPos(out var cursor))
        {
            return false;
        }

        IntPtr pointerWindow = Win32Helper.WindowFromPoint(cursor);
        return pointerWindow != IntPtr.Zero &&
               Win32Helper.GetAncestor(pointerWindow, Win32Helper.GA_ROOT) ==
               Win32Helper.GetAncestor(HWnd, Win32Helper.GA_ROOT);
    }

    private void StopGroupFileDropTracking(bool disposeCachedBatch)
    {
        _ = disposeCachedBatch;
    }

    private void RemoveNativeFileDropBridge()
    {
        ReleaseNativeFileDropRegistrationTimer();
        _isXamlRoutedDragActive = false;
        if (_isNativeFileDropSubclassInstalled &&
            _nativeFileDropSubclassProc is not null)
        {
            Win32Helper.RemoveWindowSubclass(
                HWnd,
                _nativeFileDropSubclassProc,
                ContentFileDropSubclassId);
            _isNativeFileDropSubclassInstalled = false;
        }

        foreach (NativeDropTarget target in _nativeFileDropTargets.Values)
        {
            target.DragEnterEvent -=
                NativeFileDropTarget_DragEnterEvent;
            target.DragOverEvent -=
                NativeFileDropTarget_DragOverEvent;
            target.DragLeaveEvent -=
                NativeFileDropTarget_DragLeaveEvent;
            target.UndisplayableDropBlocked -=
                NativeFileDropTarget_UndisplayableDropBlocked;
            target.ZeroResultDropBlocked -=
                NativeFileDropTarget_ZeroResultDropBlocked;
            target.ImportBusyDropBlocked -=
                NativeFileDropTarget_ImportBusyDropBlocked;
            target.LaunchDropHandler = null;
            target.LaunchTargetPathProbe = null;
            target.UndisplayablePathProbe = null;
            target.SameVolumeProbe = null;
            target.HasMappedFolderProbe = null;
            target.TargetKindProbe = null;
            target.ImportBusyProbe = null;
            target.DropIntentEvent -= NativeFileDropTarget_DropIntentEvent;
            target.DropEvent -= NativeFileDropTarget_DropEvent;
            target.Dispose();
        }

        _nativeFileDropTargets.Clear();
    }

    private IntPtr NativeFileDropSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == Win32Helper.WM_DROPFILES)
        {
            IReadOnlyList<string> paths = Win32Helper.GetDroppedFilePathsAndScreenPoint(
                (IntPtr)wParam,
                hWnd,
                out int? dropScreenX,
                out int? dropScreenY);
            App.Log(
                $"[DropDiagnostic] content id={_config.Id} stage=NativeDropFiles " +
                $"count={paths.Count}");
            bool duplicateOfNativeDrop = paths.Count > 0 &&
                Environment.TickCount64 - _lastNativeDropCompletedTick64 < 250 &&
                string.Equals(
                    paths[0],
                    _lastNativeDropFirstPath,
                    StringComparison.OrdinalIgnoreCase);
            if (!duplicateOfNativeDrop && !TryConsumeLegacyDropFilesAsLaunch(paths))
            {
                IReadOnlyList<string> stagedPaths =
                    Platform.DropStagingFileOps.StageExternalTempDropPathsWithOrigins(
                        paths,
                        hasShellIdList: false,
                        out IReadOnlyList<DroppedItemOriginKind> itemOrigins,
                        out bool stagedAny);
                QueueNativeFileDropImport(
                    stagedPaths,
                    containsTemporaryFiles: stagedAny,
                    copyWhenMapped: null,
                    screenX: dropScreenX,
                    screenY: dropScreenY,
                    itemOrigins: itemOrigins);
            }

            return IntPtr.Zero;
        }

        if (message == WmParentNotify &&
            ((uint)wParam & 0xFFFFu) == WmCreateChildEvent &&
            !HasRegisteredContentBridgeDropTarget())
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsClosing)
                {
                    InstallNativeFileDropBridge();
                    if (HasRegisteredContentBridgeDropTarget())
                    {
                        ReleaseNativeFileDropRegistrationTimer();
                    }
                }
            });
        }

        // Any press inside this window must close an open native context menu.
        // These windows are created without activation, so clicking one does
        // not deactivate the menu's owner, and the Shell menu would otherwise
        // stay on screen until an item or Escape is chosen.
        if (IsContextMenuDismissingMessage(message))
        {
            ShellContextMenuProxy.CancelOpenMenu();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private static bool IsContextMenuDismissingMessage(uint message) =>
        message is Win32Helper.WM_LBUTTONDOWN
            or Win32Helper.WM_RBUTTONDOWN
            or Win32Helper.WM_MBUTTONDOWN
            or Win32Helper.WM_XBUTTONDOWN;

    /// <summary>
    /// The legacy <c>WM_DROPFILES</c> entry point (enabled by DragAcceptFiles)
    /// receives the same physical release as the OLE drop target. It must not
    /// import a gesture another entry point already resolved as a launch:
    /// importing relocates the user's files, which is exactly what dropping on
    /// an application shortcut must never do. Returns true when the drop is
    /// consumed and must not be imported.
    ///
    /// Deliberately no delegation here: this runs on the UI thread, and probing
    /// showed that handing a synthesized CF_HDROP object to a shortcut's drop
    /// handler can stall for seconds (or longer) on executable targets. The OLE
    /// path owns delegation because it holds the drag source's real data object.
    /// </summary>
    private bool TryConsumeLegacyDropFilesAsLaunch(IReadOnlyList<string> paths)
    {
        if (CurrentContent is not FileWidgetContentAdapter fileSurface || paths.Count == 0)
        {
            return false;
        }

        if (!fileSurface.WasLaunchConsumedRecently())
        {
            return false;
        }

        App.LogVerbose(
            "[DragProtocol] legacy WM_DROPFILES stood down after a launch " +
            $"consumed widget={_config.Id}");
        _nativeFileDropLaunchTarget = null;
        RunOnNativeFileDropUiThread(file =>
            file.ClearPendingNativeDropInsertion());
        return true;
    }

    private void NativeFileDropTarget_DragEnterEvent(
        int screenX,
        int screenY,
        bool hasFileData)
    {
        if (hasFileData)
        {
            RunOnUi(() =>
            {
                if (!IsClosing && (IsWidgetCollapsed || IsCompactTransitionActive))
                {
                    NotifyCompactDragEnteredFromHost();
                }
            });
        }

        long pointerGeneration =
            System.Threading.Interlocked.Increment(
                ref _nativeFileDropPointerGeneration);
        ObserveNativeFileDragPointer(
            screenX,
            screenY,
            hasFileData,
            GetNativeFileDropPathHints(),
            pointerGeneration);
    }

    private void NativeFileDropTarget_DragOverEvent(int screenX, int screenY)
    {
        bool hasFileData = _nativeFileDropTargets.Values.Any(
            target => target.HasFileData);
        long pointerGeneration =
            System.Threading.Interlocked.Increment(
                ref _nativeFileDropPointerGeneration);
        ObserveNativeFileDragPointer(
            screenX,
            screenY,
            hasFileData,
            GetNativeFileDropPathHints(),
            pointerGeneration);
    }

    private void NativeFileDropTarget_DragLeaveEvent()
    {
        _nativeHoveredGroupTitleDropMemberId = null;
        System.Threading.Interlocked.Increment(
            ref _nativeFileDropPointerGeneration);
        System.Threading.Interlocked.Exchange(
            ref _pendingNativeDragPointerFrame,
            null);
        _cachedNativeDropSourceRef = null;
        _cachedNativeDropPathHints = [];
        _nativeFileDropItemTarget = null;
        _nativeFileDropLaunchTarget = null;
        RunOnNativeFileDropUiThread(file =>
            file.ClearDragSessionVisualState());
        RunOnUi(() =>
        {
            WidgetShellControl.CancelGroupTabDragHover();
            WidgetShellControl.SetGroupTabFileDragActive(false);
            NotifyCompactDragLeftFromHost();
        });
    }

    private void ObserveNativeFileDragPointer(
        int screenX,
        int screenY,
        bool hasFileData,
        IReadOnlyList<string>? pathHints = null,
        long pointerGeneration = 0)
    {
        ObserveNativeGroupTabDragHover(
            screenX,
            screenY,
            hasFileData,
            pointerGeneration);

        var latestFrame = new PendingNativeDragPointerFrame(
            screenX,
            screenY,
            hasFileData,
            pathHints,
            pointerGeneration);
        System.Threading.Interlocked.Exchange(
            ref _pendingNativeDragPointerFrame,
            latestFrame);
        if (!DispatcherQueue.HasThreadAccess &&
            System.Threading.Interlocked.CompareExchange(
                ref _nativeDragPointerUiQueued,
                1,
                0) != 0)
        {
            return;
        }

        RunOnNativeFileDropUiThread(file =>
        {
            System.Threading.Interlocked.Exchange(
                ref _nativeDragPointerUiQueued,
                0);
            PendingNativeDragPointerFrame? frame =
                System.Threading.Interlocked.Exchange(
                    ref _pendingNativeDragPointerFrame,
                    null);
            if (frame is null)
            {
                return;
            }

            int screenX = frame.ScreenX;
            int screenY = frame.ScreenY;
            bool hasFileData = frame.HasFileData;
            IReadOnlyList<string>? pathHints = frame.PathHints;
            long pointerGeneration = frame.PointerGeneration;

            if (pointerGeneration != 0 &&
                pointerGeneration != System.Threading.Interlocked.Read(
                    ref _nativeFileDropPointerGeneration))
            {
                // Native OLE callbacks run off the UI thread. A queued
                // DragOver can arrive after a newer pointer location has
                // already been received, which used to re-apply an old stack
                // target over the user's current non-stack location.
                return;
            }

            ComputeNativeDropTargetsOnUiThread(
                screenX,
                screenY,
                hasFileData,
                pathHints,
                out WidgetItem? nativeTarget,
                out WidgetItem? launchTarget);
            _nativeFileDropItemTarget = nativeTarget;
            _nativeFileDropLaunchTarget = launchTarget;
            file.ObserveNativeDragPointer(
                screenX,
                screenY,
                hasFileData,
                pathHints,
                nativeTarget,
                _nativeFileDropLaunchTarget);
        });
    }

    private void ComputeNativeDropTargetsOnUiThread(
        int screenX,
        int screenY,
        bool hasFileData,
        IReadOnlyList<string>? pathHints,
        out WidgetItem? nativeTarget,
        out WidgetItem? launchTarget)
    {
        string? selfDragWidgetId = null;
        bool isSelfDrag = hasFileData &&
            pathHints is { Count: > 0 } hints &&
            ActiveDeskBoxDragRegistry.TryMatch(hints, out selfDragWidgetId, out _);
        bool isSameWidgetSelfDrag = isSelfDrag &&
            string.Equals(
                selfDragWidgetId,
                _config.Id,
                StringComparison.OrdinalIgnoreCase);
        WidgetItem? navFolderItem = hasFileData &&
            CurrentContent is FileWidgetContentAdapter navAdapter
                ? navAdapter.TryResolveFolderNavigationDropTarget(screenX, screenY)
                : null;
        if (navFolderItem is not null)
        {
            nativeTarget = navFolderItem;
            launchTarget = null;
            return;
        }

        WidgetItem? hitItem = hasFileData &&
            CurrentContent is FileWidgetContentAdapter
                ? FindNativeDropDataContext<WidgetItem>(
                    screenX,
                    screenY)
                : null;
        ItemDropBehavior hitBehavior = hitItem is { } hit
            ? ItemDropBehaviorPolicy.Resolve(hit)
            : ItemDropBehavior.None;
        if (isSameWidgetSelfDrag &&
            hitItem is not null &&
            hitBehavior is ItemDropBehavior.FolderImport or ItemDropBehavior.RecycleBinDelete &&
            !IsNativeDropPointOverItemIcon(screenX, screenY, hitItem))
        {
            hitBehavior = ItemDropBehavior.None;
        }

        nativeTarget = hitBehavior is
            ItemDropBehavior.FolderImport or
            ItemDropBehavior.StackImport or
            ItemDropBehavior.RecycleBinDelete
                ? hitItem
                : null;
        launchTarget =
            hitBehavior == ItemDropBehavior.Launch && !isSelfDrag
                ? hitItem
                : null;
    }

    private void RefreshNativeDropTargetsAtPointIfOnUiThread(
        int screenX,
        int screenY,
        IReadOnlyList<string>? paths)
    {
        if (IsClosing ||
            !DispatcherQueue.HasThreadAccess ||
            CurrentContent is not FileWidgetContentAdapter)
        {
            return;
        }

        ComputeNativeDropTargetsOnUiThread(
            screenX,
            screenY,
            hasFileData: true,
            paths,
            out WidgetItem? nativeTarget,
            out WidgetItem? launchTarget);
        _nativeFileDropItemTarget = nativeTarget;
        _nativeFileDropLaunchTarget = launchTarget;
    }

    private IReadOnlyList<string> GetNativeFileDropPathHints()
    {
        NativeDropTarget? singleActive = null;
        int activeCount = 0;
        foreach (NativeDropTarget target in _nativeFileDropTargets.Values)
        {
            if (target.HasFileData && target.DragPathHints.Count > 0)
            {
                singleActive = target;
                activeCount++;
            }
        }

        if (activeCount == 0)
        {
            return [];
        }

        if (activeCount == 1 && singleActive is not null)
        {
            if (ReferenceEquals(_cachedNativeDropSourceRef, singleActive.DragPathHints))
            {
                return _cachedNativeDropPathHints;
            }

            string[] normalized = singleActive.DragPathHints
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _cachedNativeDropSourceRef = singleActive.DragPathHints;
            _cachedNativeDropPathHints = normalized;
            return normalized;
        }

        return _nativeFileDropTargets.Values
            .Where(target => target.HasFileData)
            .SelectMany(target => target.DragPathHints)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RunOnNativeFileDropUiThread(Action<FileWidgetContentAdapter> action)
    {
        void Invoke()
        {
            if (!IsClosing && CurrentContent is FileWidgetContentAdapter file)
            {
                action(file);
            }
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Invoke();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Invoke);
        }
    }

    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsClosing)
                {
                    action();
                }
            });
        }
    }

    // Explorer-style drags may never surface as XAML drag events, so the
    // group-title tab dwell is also driven from the native DragOver stream.
    // Member-detach drags carry no file formats; hasFileData excludes them
    // on this path where the payload cannot be inspected.
    private void ObserveNativeGroupTabDragHover(
        int screenX,
        int screenY,
        bool hasFileData,
        long pointerGeneration)
    {
        RunOnUi(() =>
        {
            if (pointerGeneration != 0 &&
                pointerGeneration != System.Threading.Interlocked.Read(
                    ref _nativeFileDropPointerGeneration))
            {
                // Native OLE callbacks run off the UI thread; a queued
                // DragOver can be older than the newest pointer location.
                return;
            }
            // File-bearing drags freeze the group tab strip wherever they are
            // in the window: entering the strip already frozen skips the
            // one-frame widen TabView reserves on its DragEnter.
            WidgetShellControl.SetGroupTabFileDragActive(hasFileData);
            if (!hasFileData)
            {
                WidgetShellControl.CancelGroupTabDragHover();
                return;
            }
            if (RootGrid.XamlRoot is null)
            {
                return;
            }

            var clientPoint = new Win32Helper.POINT
            {
                X = screenX,
                Y = screenY
            };
            if (!Win32Helper.ScreenToClient(HWnd, ref clientPoint))
            {
                return;
            }

            double scale = RootGrid.XamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                scale = 1;
            }

            var point = new Windows.Foundation.Point(
                clientPoint.X / scale,
                clientPoint.Y / scale);
            string? hoveredMemberId = null;
            string? titleDropTargetId = null;
            if (WidgetShellControl.IsPointOverGroupTitleBar(RootGrid, point))
            {
                hoveredMemberId = WidgetShellControl.HitTestGroupTitleSwitchMemberId(
                    RootGrid,
                    point,
                    respectStackCooldown: true);
                titleDropTargetId = WidgetShellControl.HitTestGroupTitleDropTargetMemberId(
                    RootGrid,
                    point);
            }
            _nativeHoveredGroupTitleDropMemberId = titleDropTargetId;
            WidgetShellControl.ObserveGroupTitleDragHover(hoveredMemberId);
        });
    }

    private void NativeFileDropTarget_DropEvent(
        IReadOnlyList<string> paths,
        int screenX,
        int screenY,
        bool containsTemporaryFiles,
        bool copyWhenMapped)
    {
        System.Threading.Interlocked.Increment(
            ref _nativeFileDropPointerGeneration);
        if (_nativeDropIntentHandledForLegacyCallback)
        {
            // The richer callback below already owns this drop. Keep this
            // legacy event subscribed for compatibility with existing native
            // targets, but do not enqueue a duplicate filesystem operation.
            _nativeDropIntentHandledForLegacyCallback = false;
            return;
        }

        App.Log(
            $"[DropDiagnostic] content id={_config.Id} stage=NativeIDropTargetDrop " +
            $"count={paths.Count} temporary={containsTemporaryFiles} " +
            $"copyWhenMapped={copyWhenMapped}");
        // Native OLE commonly raises DragLeave immediately after Drop.  Latch
        // the visible insertion line before the asynchronous import is queued,
        // otherwise the leave cleanup clears it and the files fall back to the
        // append position.
        RefreshNativeDropTargetsAtPointIfOnUiThread(screenX, screenY, paths);
        WidgetItem? capturedDropItemTarget = NormalizeNativeFileDropItemTarget(
            _nativeFileDropItemTarget);
        if (capturedDropItemTarget is null)
        {
            RunOnNativeFileDropUiThread(file =>
                file.CaptureNativeDropInsertion(screenX, screenY));
        }
        else
        {
            RunOnNativeFileDropUiThread(file =>
                file.ClearPendingNativeDropInsertion());
        }
        DispatchNativeFileDropGesture(
            paths,
            containsTemporaryFiles,
            copyWhenMapped,
            screenX,
            screenY,
            capturedItemTarget: capturedDropItemTarget,
            hasCapturedItemTarget: true,
            gestureId: NativeDropTarget.AllocateGestureId());
        _nativeFileDropItemTarget = null;
        _nativeFileDropLaunchTarget = null;
        RunOnUi(() => WidgetShellControl.SetGroupTabFileDragActive(false));
    }

    /// <summary>
    /// Runs synchronously inside the OLE Drop callback when the pointer is on
    /// an application-shortcut tile: the dropped files are opened with the
    /// application the shortcut points at. A shortcut that cannot launch
    /// consumes the gesture: the user asked to open the file with that
    /// application, so the fallback is a notification, never the import that
    /// would move the file. Only a drop that no shortcut owned (NotAttempted)
    /// stays an ordinary import. The gesture claim, target capture, and OLE
    /// completion effect are decided synchronously here; the process launch
    /// itself hops one dispatcher turn so the callback frame never spawns a
    /// process (drag-freeze family).
    /// </summary>
    private ShellDropLaunchResult HandleNativeLaunchDrop(
        NativeDropLaunchRequest request)
    {
        RefreshNativeDropTargetsAtPointIfOnUiThread(
            request.ScreenX,
            request.ScreenY,
            request.Paths);
        if (CurrentContent is not FileWidgetContentAdapter ||
            request.RightButtonDrag ||
            _nativeFileDropLaunchTarget is not { Path.Length: > 0 } launchTarget ||
            ActiveDeskBoxDragRegistry.TryMatch(
                request.Paths, out _, out _))
        {
            // A DeskBox-originated drag never launches through the native
            // path: the XAML completion path applies the internal-drag launch
            // policy (icon hit-test, popover-member exclusion, self-file
            // exclusion) and reports the outcome there.
            return ShellDropLaunchResult.NotAttempted;
        }

        if (CurrentContent is FileWidgetContentAdapter launchSurface &&
            launchSurface.WasLaunchConsumedRecently())
        {
            // Another entry point (legacy WM_DROPFILES, or the routed XAML drop)
            // already resolved this release. Launching again would open the
            // application a second time, so stand down and keep the refusal.
            _nativeFileDropItemTarget = null;
            _nativeFileDropLaunchTarget = null;
            return ShellDropLaunchResult.AlreadyResolved;
        }

        // Claim the gesture on this thread: the legacy WM_DROPFILES message
        // for the same release can arrive before queued UI work would run,
        // and it must not turn this outcome into an import. With the launch
        // deferred, the claim has to happen inside the callback frame —
        // before OnDrop returns — or that race reopens.
        string shortcutPath = launchTarget.Path;
        string applicationName = launchTarget.Name;
        IReadOnlyList<string> droppedPaths = request.Paths;
        bool containsTemporaryFiles = request.ContainsTemporaryFiles;
        _nativeFileDropItemTarget = null;
        _nativeFileDropLaunchTarget = null;
        _lastNativeDropCompletedTick64 = Environment.TickCount64;
        _lastNativeDropFirstPath = droppedPaths.Count > 0 ? droppedPaths[0] : null;
        foreach (NativeDropTarget target in _nativeFileDropTargets.Values)
        {
            if (target.ActiveGestureId > _lastNativeClaimedGestureId)
            {
                _lastNativeClaimedGestureId = target.ActiveGestureId;
            }
        }
        MarkLaunchConsumedForCurrentContent();
        RunOnNativeFileDropUiThread(file =>
        {
            file.ClearDragSessionVisualState();
        });
        UiDispatch.PostAfterCallback(() =>
            LaunchDroppedFilesDeferred(
                shortcutPath,
                applicationName,
                droppedPaths,
                containsTemporaryFiles));

        // The completion effect is decided optimistically: the shortcut owned
        // this gesture and a Link receipt never deletes source files, so a
        // launch that fails after the fact is reported by the refusal toast
        // instead of retroactively rewriting the OLE outcome.
        return ShellDropLaunchResult.Launched(NativeDropEffectPolicy.Link, 0);
    }

    /// <summary>
    /// The deferred half of the shortcut launch: runs one dispatcher turn
    /// after the drop callback returned. The existence filter leaves the UI
    /// thread entirely — UNC paths stat in seconds each (contract ⑫) — while
    /// the launch itself stays serialized on the UI thread. Only paths that
    /// exist on disk reach the command line — a synthetic CF_HDROP can carry
    /// arbitrary flag-shaped strings ("/k", "-Command") that quoted-argument
    /// passing would forward to the launched application as argv tokens.
    /// </summary>
    private void LaunchDroppedFilesDeferred(
        string shortcutPath,
        string applicationName,
        IReadOnlyList<string> droppedPaths,
        bool containsTemporaryFiles)
    {
        IReadOnlyList<string> pathsToStat = droppedPaths;
        _ = Task.Run(() =>
        {
            string[] existingPaths = pathsToStat
                .Where(static p =>
                    !string.IsNullOrWhiteSpace(p) &&
                    (System.IO.File.Exists(p) || System.IO.Directory.Exists(p)))
                .ToArray();
            _ = DispatcherQueue.TryEnqueue(() => LaunchDroppedFilesResolved(
                shortcutPath,
                applicationName,
                droppedPaths,
                containsTemporaryFiles,
                existingPaths));
        });
    }

    private void LaunchDroppedFilesResolved(
        string shortcutPath,
        string applicationName,
        IReadOnlyList<string> droppedPaths,
        bool containsTemporaryFiles,
        string[] existingPaths)
    {
        if (IsClosing)
        {
            return;
        }

        if (existingPaths.Length == 0)
        {
            App.Log(
                "[ShortcutLaunch] Refused launch: no dropped path exists on " +
                $"disk for '{shortcutPath}'");
            RunOnNativeFileDropUiThread(file =>
                file.ShowShortcutLaunchRefusedFeedback(applicationName));
            if (containsTemporaryFiles)
            {
                // The recursive delete leaves the dispatcher turn; this unit
                // only landed here to stay off the OLE callback frame.
                IReadOnlyList<string> refusedPaths = droppedPaths;
                _ = Task.Run(() => CleanupNativeTemporaryDropFiles(refusedPaths));
            }

            return;
        }

        bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
            shortcutPath,
            existingPaths);
        RunOnNativeFileDropUiThread(file =>
        {
            if (!launched)
            {
                file.ShowShortcutLaunchRefusedFeedback(applicationName);
            }
        });
        if (launched && containsTemporaryFiles)
        {
            // The launched application may read the materialized virtual files
            // lazily, so the import path's synchronous VirtualDrops cleanup is
            // deferred past a generous startup window instead.
            _ = Task.Delay(TimeSpan.FromSeconds(20))
                .ContinueWith(_ => CleanupNativeTemporaryDropFiles(droppedPaths));
        }
        else if (containsTemporaryFiles)
        {
            IReadOnlyList<string> failedLaunchPaths = droppedPaths;
            _ = Task.Run(() => CleanupNativeTemporaryDropFiles(failedLaunchPaths));
        }
    }

    private void MarkLaunchConsumedForCurrentContent()
    {
        if (CurrentContent is FileWidgetContentAdapter fileSurface)
        {
            fileSurface.MarkNativeLaunchConsumed();
        }
    }

    private TransferTargetKind GetNativeDropTargetKind()
    {
        if (_nativeHoveredGroupTitleDropMemberId is { Length: > 0 } titleMemberId &&
            !string.Equals(CurrentContent?.WidgetId, titleMemberId, StringComparison.Ordinal))
        {
            if (!DoesGroupMemberAcceptFileDrop(titleMemberId))
            {
                return TransferTargetKind.UnsupportedSurface;
            }

            WidgetConfig? memberConfig = SettingsService.Settings.WidgetLayout.Widgets
                .FirstOrDefault(w => string.Equals(w.Id, titleMemberId, StringComparison.Ordinal));
            return memberConfig?.WidgetKind == WidgetKind.File
                ? TransferTargetKind.DirectorySurface
                : TransferTargetKind.AssociationOnlySurface;
        }

        if (CurrentContent is not FileWidgetContentAdapter)
        {
            return CurrentContentAcceptsFileDrops()
                ? TransferTargetKind.AssociationOnlySurface
                : TransferTargetKind.UnsupportedSurface;
        }

        if (_nativeFileDropLaunchTarget is { Path.Length: > 0 })
        {
            return TransferTargetKind.ApplicationShortcutTile;
        }

        if (_nativeFileDropItemTarget is { } item &&
            ItemDropBehaviorPolicy.Resolve(item) == ItemDropBehavior.RecycleBinDelete)
        {
            return TransferTargetKind.RecycleBinTile;
        }

        return TransferTargetKind.DirectorySurface;
    }

    private bool HasNativeDropMappedFolder()
    {
        if (CurrentContent is not FileWidgetContentAdapter file)
        {
            return true;
        }

        string prospective = TransferIntentEngine.ResolveProspectiveManagedDestination(
            file.CurrentFolderPath,
            file.ViewModel.MappedFolderPath ?? _config.MappedFolderPath,
            SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        return !string.IsNullOrWhiteSpace(prospective);
    }

    private bool IsNativeDropOnSameVolume(IReadOnlyList<string> paths)
    {
        if (CurrentContent is not FileWidgetContentAdapter file)
        {
            return true;
        }

        string destination = _nativeFileDropItemTarget is
            {
                IsFolder: true,
                Path.Length: > 0
            } targetFolder
                ? targetFolder.Path
                : TransferIntentEngine.ResolveProspectiveManagedDestination(
                    file.CurrentFolderPath,
                    file.ViewModel.MappedFolderPath ?? _config.MappedFolderPath,
                    SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
        if (string.IsNullOrWhiteSpace(destination))
        {
            // Without a resolvable destination folder the glyph falls back to
            // the same-volume default this path has always reported.
            return true;
        }

        return PathOriginClassifier.AreAllOnSameVolume(
            paths,
            destination,
            TimeSpan.FromMilliseconds(80));
    }

    private bool IsNativeDropImportBusy()
    {
        return CurrentContent is FileWidgetContentAdapter { IsImportBusy: true };
    }

    private bool IsNativeFileDropPathUndisplayable(string path)
    {
        // The widget display filters describe the file surface's item list;
        // quick capture and todo attachments keep every file they accept.
        return CurrentContent is FileWidgetContentAdapter &&
            FileService.IsFilteredFromWidgetDisplay(path);
    }

    private void NativeFileDropTarget_UndisplayableDropBlocked(int requestedCount)
    {
        RunOnNativeFileDropUiThread(file =>
            file.NotifyNativeDropBlockedUndisplayable(requestedCount));
    }

    private void NativeFileDropTarget_ZeroResultDropBlocked()
    {
        // Always enqueue one dispatcher turn: the zero-result notification
        // can fire from the OLE continuation, and the feedback surface must
        // not be touched inside a callback frame (contract §23).
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosing && CurrentContent is FileWidgetContentAdapter file)
            {
                file.NotifyNativeDropZeroResult();
            }
        });
    }

    private void NativeFileDropTarget_ImportBusyDropBlocked()
    {
        // Same discipline as the zero-result handler: the busy refusal fires
        // inside the OLE Drop callback frame, so the toast leaves the frame.
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosing && CurrentContent is FileWidgetContentAdapter file)
            {
                file.NotifyNativeDropImportBusy();
            }
        });
    }

    private void NativeFileDropTarget_DropIntentEvent(
        NativeDropIntentEventArgs args)
    {
        System.Threading.Interlocked.Increment(
            ref _nativeFileDropPointerGeneration);
        _nativeDropIntentHandledForLegacyCallback = true;
        bool followWindows = ShouldFollowWindowsNativeFileDrop();
        // The forced intent must obey the same allowed-effect constraints the
        // deferred re-resolution applies; otherwise Shift on a source that
        // never announced Move would force a transfer that deletes originals.
        uint allowedEffects = args.AllowedEffects;
        bool isDeskBoxOwnedSource = ActiveDeskBoxDragRegistry.TryMatch(
            args.Paths,
            out _,
            out string? matchedWidgetId,
            out _);
        SourceOriginSnapshot sourceOrigin = PathOriginClassifier.ClassifySources(
            args.Paths,
            TimeSpan.FromMilliseconds(80));
        bool allItemsTemporary = args.ItemOrigins is { Count: > 0 } origins
            ? origins.All(static o => o != DroppedItemOriginKind.RealFileSystemPath)
            : args.ContainsTemporaryFiles;
        bool sourceCanCopy = isDeskBoxOwnedSource ||
            (allowedEffects & NativeDropEffectPolicy.Copy) != 0;
        bool sourceCanMove = !allItemsTemporary &&
            !sourceOrigin.IsMoveProtectedSystemOrSharedSource &&
            (isDeskBoxOwnedSource ||
             (allowedEffects & NativeDropEffectPolicy.Move) != 0);
        bool ctrlDown =
            (args.KeyState & NativeDropEffectPolicy.ControlKeyState) != 0;
        bool shiftDown =
            (args.KeyState & NativeDropEffectPolicy.ShiftKeyState) != 0;
        FileDropIntent? forcedIntent = args.ShortcutRequested &&
            !allItemsTemporary
                ? FileDropIntent.Shortcut
                : ctrlDown && !shiftDown
                    ? (sourceCanCopy ? FileDropIntent.Copy : FileDropIntent.None)
                    : shiftDown && !ctrlDown
                        ? (!allItemsTemporary && sourceCanMove
                            ? FileDropIntent.Move
                            : FileDropIntent.None)
                        : null;
        App.Log(
            $"[DropDiagnostic] content id={_config.Id} stage=NativeDropIntent " +
            $"count={args.Paths.Count} temporary={args.ContainsTemporaryFiles} " +
            $"shortcut={args.ShortcutRequested} rightButton={args.RightButtonDrag} " +
            $"effect={args.FeedbackEffect}");

        RefreshNativeDropTargetsAtPointIfOnUiThread(
            args.ScreenX,
            args.ScreenY,
            args.Paths);

        if (isDeskBoxOwnedSource)
        {
            string? selfDragWidget = matchedWidgetId;
            // This drop came from our own file surface: for file widgets, the
            // routed/internal drag handling owns reorder, stack membership,
            // launches and cross-widget transfer, except same-window
            // folder/stack/recycle-bin tile drops (WinUI does not deliver the
            // routed Drop to item surfaces for a same-ListView drag).
            // Non-file widgets (e.g. collapsed Todo / QuickCapture capsules)
            // let DispatchNativeFileDropGesture arbitrate via DropGestureId
            // so capsule/border drops are never silently lost.
            if (CurrentContent is FileWidgetContentAdapter &&
                (_nativeFileDropItemTarget is null ||
                 !string.Equals(
                     selfDragWidget,
                     _config.Id,
                     StringComparison.OrdinalIgnoreCase)))
            {
                App.LogVerbose(
                    $"[DropTarget] Native import skipped: DeskBox self-drag " +
                    $"widget={selfDragWidget}");
                _nativeFileDropItemTarget = null;
                _nativeFileDropLaunchTarget = null;
                return;
            }

            if (CurrentContent is not FileWidgetContentAdapter &&
                string.Equals(
                    selfDragWidget,
                    _config.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _nativeFileDropItemTarget = null;
                _nativeFileDropLaunchTarget = null;
                return;
            }
        }

        WidgetItem? capturedDropItemTarget = NormalizeNativeFileDropItemTarget(
            _nativeFileDropItemTarget);

        // Capture the insertion index synchronously on the UI thread before
        // Explorer's native drag loop sends DragLeave and clears the preview,
        // unless the release targeted a specific child tile (folder/stack/bin).
        if (capturedDropItemTarget is null)
        {
            RunOnNativeFileDropUiThread(file =>
                file.CaptureNativeDropInsertion(args.ScreenX, args.ScreenY));
        }
        else
        {
            RunOnNativeFileDropUiThread(file =>
                file.ClearPendingNativeDropInsertion());
        }

        // A right-button drag is intentionally resolved after release. The
        // native OLE callback cannot wait for a WinUI flyout without blocking
        // Explorer's drag loop, so the target keeps the source untouched and
        // lets DeskBox execute the selected operation asynchronously.
        if (args.RightButtonDrag)
        {
            QueueNativeRightButtonDropChoice(args, capturedDropItemTarget);
        }
        else
        {
            DispatchNativeFileDropGesture(
                args.Paths,
                args.ContainsTemporaryFiles,
                args.CopyRequested,
                args.ScreenX,
                args.ScreenY,
                forcedIntent,
                allowedEffects,
                capturedDropItemTarget,
                hasCapturedItemTarget: true,
                gestureId: args.GestureId,
                itemOrigins: args.ItemOrigins);
        }

        RunOnUi(NotifyCompactDropCompletedFromHost);
        _nativeFileDropItemTarget = null;
        _nativeFileDropLaunchTarget = null;
    }

    private enum NativeRightDropChoice
    {
        Cancel,
        Copy,
        Move,
        Shortcut,
        OpenWithApp
    }

    private void QueueNativeRightButtonDropChoice(
        NativeDropIntentEventArgs args,
        WidgetItem? capturedItemTarget = null)
    {
        // Capture before enqueueing: the caller clears
        // _nativeFileDropLaunchTarget synchronously right after this method
        // returns, so a closure reading the field at dispatch time always
        // saw null and the "open with" menu item never appeared.
        WidgetItem? capturedLaunchTarget = _nativeFileDropLaunchTarget;
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
                WidgetItem? launchTarget = capturedLaunchTarget;
                NativeRightDropChoice choice =
                    await ShowNativeRightButtonDropChoiceAsync(args, launchTarget, capturedItemTarget);
                if (choice == NativeRightDropChoice.Cancel)
                {
                    RunOnNativeFileDropUiThread(file =>
                        file.ClearPendingNativeDropInsertion());
                    if (args.ContainsTemporaryFiles)
                    {
                        CleanupNativeTemporaryDropFiles(args.Paths);
                    }

                    return;
                }

                if (choice == NativeRightDropChoice.OpenWithApp)
                {
                    ExecuteRightButtonLaunchChoice(args, launchTarget);
                    return;
                }

                bool copyWhenMapped = choice != NativeRightDropChoice.Move;
                FileDropIntent? rightChoiceIntent = choice switch
                {
                    NativeRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                    NativeRightDropChoice.Copy => FileDropIntent.Copy,
                    NativeRightDropChoice.Move => FileDropIntent.Move,
                    _ => null,
                };
                DispatchNativeFileDropGesture(
                    args.Paths,
                    args.ContainsTemporaryFiles,
                    copyWhenMapped,
                    args.ScreenX,
                    args.ScreenY,
                    rightChoiceIntent,
                    args.AllowedEffects,
                    capturedItemTarget,
                    hasCapturedItemTarget: true,
                    gestureId: args.GestureId,
                    itemOrigins: args.ItemOrigins);
            }))
        {
            if (args.ContainsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(args.Paths);
            }
        }
    }

    /// <summary>
    /// The user chose "open with" in the right-button menu, so the files are
    /// opened with the shortcut's application. The real data object is already
    /// released at this point, which no longer matters: the launch passes the
    /// extracted paths as arguments instead of delegating a drop.
    /// </summary>
    private void ExecuteRightButtonLaunchChoice(
        NativeDropIntentEventArgs args,
        WidgetItem? launchTarget)
    {
        if (launchTarget is not { Path.Length: > 0 } shortcut || args.Paths.Count == 0)
        {
            CleanupIfTemporary(args);
            return;
        }

        string applicationName = shortcut.Name;
        string shortcutPath = shortcut.Path;
        MarkLaunchConsumedForCurrentContent();
        LaunchDroppedFilesDeferred(
            shortcutPath,
            applicationName,
            args.Paths,
            args.ContainsTemporaryFiles);
    }

    private void CleanupIfTemporary(NativeDropIntentEventArgs args)
    {
        RunOnNativeFileDropUiThread(file =>
            file.ClearPendingNativeDropInsertion());
        if (args.ContainsTemporaryFiles)
        {
            CleanupNativeTemporaryDropFiles(args.Paths);
        }
    }

    private async Task<NativeRightDropChoice> ShowNativeRightButtonDropChoiceAsync(
        NativeDropIntentEventArgs args,
        WidgetItem? launchTarget,
        WidgetItem? itemTarget = null)
    {
        if (!CurrentContentAcceptsFileDrops() || RootGrid.XamlRoot is null)
        {
            return NativeRightDropChoice.Cancel;
        }

        var completion = new TaskCompletionSource<NativeRightDropChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var menu = new MenuFlyout();

        if (CurrentContent is not FileWidgetContentAdapter ||
            string.IsNullOrWhiteSpace(
                SettingsService.Settings.FileWidget.ManagedDropAction))
        {
            AddNativeRightChoice(
                menu,
                App.Current.LocalizationService.T("Common.Copy"),
                NativeRightDropChoice.Copy,
                completion);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddNativeRightChoice(
                menu,
                App.Current.LocalizationService.T("Common.Cancel"),
                NativeRightDropChoice.Cancel,
                completion);
            menu.Closed += (_, _) =>
                completion.TrySetResult(NativeRightDropChoice.Cancel);
            try
            {
                menu.ShowAt(
                    RootGrid,
                    GetNativeDropMenuPoint(args.ScreenX, args.ScreenY));
            }
            catch (Exception ex)
            {
                App.Log($"[DropTarget] Right-button association choice menu failed: {ex.Message}");
                return NativeRightDropChoice.Cancel;
            }

            return await completion.Task;
        }

        bool isRecycleBinTarget = itemTarget is not null &&
            (ItemDropBehaviorPolicy.Resolve(itemTarget) == ItemDropBehavior.RecycleBinDelete ||
             NamespaceShortcutHelper.ResolveKind(itemTarget) == NamespaceShortcutKind.RecycleBin);
        if (isRecycleBinTarget)
        {
            bool binAllTemporary = args.ItemOrigins is { Count: > 0 } binOrigins
                ? binOrigins.All(static o => o != DroppedItemOriginKind.RealFileSystemPath)
                : args.ContainsTemporaryFiles;
            if (!binAllTemporary)
            {
                AddNativeRightChoice(
                    menu,
                    App.Current.LocalizationService.T("Common.Move"),
                    NativeRightDropChoice.Move,
                    completion);
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            AddNativeRightChoice(
                menu,
                App.Current.LocalizationService.T("Common.Cancel"),
                NativeRightDropChoice.Cancel,
                completion);
            menu.Closed += (_, _) =>
                completion.TrySetResult(NativeRightDropChoice.Cancel);
            try
            {
                menu.ShowAt(
                    RootGrid,
                    GetNativeDropMenuPoint(args.ScreenX, args.ScreenY));
            }
            catch (Exception ex)
            {
                App.Log($"[DropTarget] Right-button Recycle Bin choice menu failed: {ex.Message}");
                return NativeRightDropChoice.Cancel;
            }

            return await completion.Task;
        }

        if (launchTarget is { Path.Length: > 0 } shortcut)
        {
            AddNativeRightChoice(
                menu,
                App.Current.LocalizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    shortcut.Name),
                NativeRightDropChoice.OpenWithApp,
                completion);
        }

        bool allTemporary = args.ItemOrigins is { Count: > 0 } origins
            ? origins.All(static o => o != DroppedItemOriginKind.RealFileSystemPath)
            : args.ContainsTemporaryFiles;
        bool isDeskBoxDrag = ActiveDeskBoxDragRegistry.TryMatch(args.Paths, out _, out _);
        bool sourceCanCopy = isDeskBoxDrag ||
            allTemporary ||
            (args.AllowedEffects & NativeDropEffectPolicy.Copy) != 0;
        if (sourceCanCopy)
        {
            AddNativeRightChoice(
                menu,
                App.Current.LocalizationService.T("Common.Copy"),
                NativeRightDropChoice.Copy,
                completion);
        }

        bool hasTargetFolder = HasNativeDropMappedFolder() ||
            (itemTarget is not null &&
             ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(itemTarget, out _));
        if (hasTargetFolder)
        {
            SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
                args.Paths,
                TimeSpan.FromMilliseconds(80));
            if (!allTemporary &&
                !origin.IsMoveProtectedSystemOrSharedSource &&
                (isDeskBoxDrag ||
                 (args.AllowedEffects & NativeDropEffectPolicy.Move) != 0))
            {
                AddNativeRightChoice(
                    menu,
                    App.Current.LocalizationService.T("Common.Move"),
                    NativeRightDropChoice.Move,
                    completion);
            }
            if (!allTemporary)
            {
                AddNativeRightChoice(
                    menu,
                    App.Current.LocalizationService.T("Widget.CreateShortcut"),
                    NativeRightDropChoice.Shortcut,
                    completion);
            }
        }

        AddNativeRightChoice(
            menu,
            App.Current.LocalizationService.T("Common.Cancel"),
            NativeRightDropChoice.Cancel,
            completion);
        menu.Closed += (_, _) =>
            completion.TrySetResult(NativeRightDropChoice.Cancel);

        try
        {
            menu.ShowAt(
                RootGrid,
                GetNativeDropMenuPoint(args.ScreenX, args.ScreenY));
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Right-button choice menu failed: {ex.Message}");
            return NativeRightDropChoice.Copy;
        }

        return await completion.Task;
    }

    private static void AddNativeRightChoice(
        MenuFlyout menu,
        string text,
        NativeRightDropChoice choice,
        TaskCompletionSource<NativeRightDropChoice> completion)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = choice };
        item.Click += (_, _) =>
        {
            completion.TrySetResult(choice);
            menu.Hide();
        };
        menu.Items.Add(item);
    }

    private Windows.Foundation.Point GetNativeDropMenuPoint(
        int screenX,
        int screenY)
    {
        var clientPoint = new Win32Helper.POINT { X = screenX, Y = screenY };
        if (!Win32Helper.ScreenToClient(HWnd, ref clientPoint) ||
            RootGrid.XamlRoot is null)
        {
            return new Windows.Foundation.Point(0, 0);
        }

        double scale = RootGrid.XamlRoot.RasterizationScale;
        return new Windows.Foundation.Point(
            clientPoint.X / Math.Max(1, scale),
            clientPoint.Y / Math.Max(1, scale));
    }

    private bool IsDropGestureAlreadyClaimed(
        IReadOnlyList<string> paths,
        long gestureId,
        long xamlEpochAtDrop)
    {
        if (gestureId > 0 && gestureId <= _lastNativeClaimedGestureId)
        {
            return true;
        }

        if (xamlEpochAtDrop >= 0 && _lastXamlClaimedGestureEpoch == xamlEpochAtDrop)
        {
            return true;
        }

        if (ActiveDeskBoxDragRegistry.TryMatch(
                paths,
                out string matchedSessionId,
                out _,
                out _) &&
            !string.IsNullOrWhiteSpace(matchedSessionId) &&
            string.Equals(
                matchedSessionId,
                _lastXamlClaimedDragSessionId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private void DispatchNativeFileDropGesture(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool copyWhenMapped,
        int screenX,
        int screenY,
        FileDropIntent? forcedIntent = null,
        uint? allowedEffects = null,
        WidgetItem? capturedItemTarget = null,
        bool hasCapturedItemTarget = false,
        long gestureId = 0,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null)
    {
        if (paths.Count == 0)
        {
            return;
        }

        string[] ownedPaths = paths.ToArray();
        long capturedXamlEpoch = _isXamlRoutedDragActive ? _inboundGestureEpoch : -1;
        if (IsDropGestureAlreadyClaimed(ownedPaths, gestureId, capturedXamlEpoch))
        {
            RunOnNativeFileDropUiThread(file =>
                file.ClearPendingNativeDropInsertion());
            if (containsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(ownedPaths);
            }

            App.LogVerbose(
                "[DropTarget] Native import skipped because gesture was already claimed.");
            return;
        }

        _lastNativeDropCompletedTick64 = Environment.TickCount64;
        _lastNativeDropFirstPath = ownedPaths[0];

        if (!_isXamlRoutedDragActive)
        {
            if (gestureId > _lastNativeClaimedGestureId)
            {
                _lastNativeClaimedGestureId = gestureId;
            }

            QueueNativeFileDropImport(
                ownedPaths,
                containsTemporaryFiles,
                copyWhenMapped,
                screenX,
                screenY,
                forcedIntent,
                allowedEffects,
                capturedItemTarget,
                hasCapturedItemTarget,
                itemOrigins);
            return;
        }

        // An in-process XAML drag is active on this window: enqueue at Low
        // priority so any normal-priority UIElement.Drop turn on this
        // DispatcherQueue runs first with zero wall-clock timer sleep.
        if (!DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (IsClosing ||
                        IsDropGestureAlreadyClaimed(ownedPaths, gestureId, capturedXamlEpoch))
                    {
                        RunOnNativeFileDropUiThread(file =>
                            file.ClearPendingNativeDropInsertion());
                        if (containsTemporaryFiles)
                        {
                            CleanupNativeTemporaryDropFiles(ownedPaths);
                        }

                        return;
                    }

                    if (gestureId > _lastNativeClaimedGestureId)
                    {
                        _lastNativeClaimedGestureId = gestureId;
                    }

                    _ = ImportNativeFileDropAsync(
                        ownedPaths,
                        containsTemporaryFiles,
                        copyWhenMapped,
                        screenX,
                        screenY,
                        forcedIntent,
                        allowedEffects,
                        capturedItemTarget,
                        hasCapturedItemTarget,
                        itemOrigins);
                }))
        {
            if (containsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(ownedPaths);
            }
        }
    }

    private void QueueNativeFileDropImport(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped,
        int? screenX = null,
        int? screenY = null,
        FileDropIntent? forcedIntent = null,
        uint? allowedEffects = null,
        WidgetItem? capturedItemTarget = null,
        bool hasCapturedItemTarget = false,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(
                () => _ = ImportNativeFileDropAsync(
                    paths,
                    containsTemporaryFiles,
                    copyWhenMapped,
                    screenX,
                    screenY,
                    forcedIntent,
                    allowedEffects,
                    capturedItemTarget,
                    hasCapturedItemTarget,
                    itemOrigins)))
        {
            if (containsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(paths);
            }
        }
    }

    private async Task ImportNativeFileDropAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped,
        int? screenX,
        int? screenY,
        FileDropIntent? forcedIntent,
        uint? allowedEffects,
        WidgetItem? capturedItemTarget = null,
        bool hasCapturedItemTarget = false,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null)
    {
        try
        {
            if (IsClosing)
            {
                return;
            }

            string? activeBeforeSwitch = CurrentContent?.WidgetId;
            if (!await TryEnsureGroupTabDropTargetAsync(screenX, screenY))
            {
                return;
            }

            if (!string.Equals(activeBeforeSwitch, CurrentContent?.WidgetId, StringComparison.Ordinal))
            {
                capturedItemTarget = null;
                hasCapturedItemTarget = false;
            }

            switch (CurrentContent)
            {
                case FileWidgetContentAdapter file:
                    file.SetHostWindowHandle(HWnd);
                    file.HostHasActiveShellDragWindowProbe = () => HasActiveNativeShellDragWindow;
                    if (file.Surface is null && !IsCompactBoundsStateActive)
                    {
                        EnsureCollapsedContentViewAttached();
                        ArmCollapsedContentHibernationIfSettled();
                    }
                    WidgetItem? resolvedTargetItem = hasCapturedItemTarget
                        ? NormalizeNativeFileDropItemTarget(capturedItemTarget)
                        : NormalizeNativeFileDropItemTarget(
                            FindNativeDropDataContext<WidgetItem>(
                                screenX,
                                screenY));
                    await file.ImportNativeDroppedFilesAsync(
                        paths,
                        containsTemporaryFiles,
                        copyWhenMapped,
                        resolvedTargetItem,
                        forcedIntent,
                        screenX,
                        screenY,
                        allowedEffects,
                        itemOrigins);
                    break;

                case QuickCaptureWidgetContentAdapter quickCapture:
                    await quickCapture.ImportNativeDroppedFilesAsync(
                        CreateNativeDroppedFilePaths(
                            paths,
                            containsTemporaryFiles,
                            itemOrigins),
                        FindNativeDropDataContext<QuickCaptureItemViewModel>(
                            screenX,
                            screenY));
                    break;

                case TodoWidgetContentAdapter todo:
                    await todo.ImportNativeDroppedFilesAsync(
                        CreateNativeDroppedFilePaths(
                            paths,
                            containsTemporaryFiles,
                            itemOrigins),
                        FindNativeDropDataContext<TodoItemViewModel>(
                            screenX,
                            screenY));
                    break;

                case ClipboardWidgetContentAdapter clipboard:
                    await clipboard.ImportNativeDroppedFilesAsync(
                        CreateNativeDroppedFilePaths(
                            paths,
                            containsTemporaryFiles,
                            itemOrigins));
                    break;

                case CustomWidgetContentAdapter custom:
                    bool customForceCopy = containsTemporaryFiles ||
                        (itemOrigins is { Count: > 0 } &&
                         itemOrigins.Any(static kind => kind != DroppedItemOriginKind.RealFileSystemPath));
                    await custom.ImportNativeDroppedFilesAsync(
                        paths,
                        forceCopyToPortableStore: customForceCopy);
                    break;

                default:
                    App.Log(
                        $"[DropTarget] Ignored grouped native file drop " +
                        $"id={_config.Id}; the active member does not " +
                        "accept files.");
                    break;
            }
        }
        finally
        {
            if (containsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(paths);
            }
        }
    }

    internal bool CurrentContentAcceptsFileDrops() =>
        CurrentContent is FileWidgetContentAdapter or
            TodoWidgetContentAdapter or
            QuickCaptureWidgetContentAdapter or
            ClipboardWidgetContentAdapter ||
        (CurrentContent is CustomWidgetContentAdapter custom &&
         custom.ViewModel.AcceptsFileDrops);

    internal async Task<bool> TryEnsureGroupTabDropTargetAsync(int? screenX, int? screenY)
    {
        if (screenX is { } sx &&
            screenY is { } sy &&
            RootGrid.XamlRoot is not null)
        {
            var clientPoint = new Win32Helper.POINT { X = sx, Y = sy };
            if (Win32Helper.ScreenToClient(HWnd, ref clientPoint))
            {
                double scale = RootGrid.XamlRoot.RasterizationScale;
                if (scale <= 0)
                {
                    scale = 1;
                }

                var point = new Windows.Foundation.Point(
                    clientPoint.X / scale,
                    clientPoint.Y / scale);
                if (WidgetShellControl.IsPointOverGroupTitleBar(RootGrid, point))
                {
                    string? targetMemberId = WidgetShellControl.HitTestGroupTitleDropTargetMemberId(
                            RootGrid,
                            point) ??
                        WidgetShellControl.ConsumePendingGroupTabDropTargetId();
                    WidgetShellControl.CancelGroupTabDragHover();
                    WidgetShellControl.SetGroupTabFileDragActive(false);
                    if (string.IsNullOrWhiteSpace(targetMemberId))
                    {
                        return false;
                    }

                    if (!string.Equals(CurrentContent?.WidgetId, targetMemberId, StringComparison.Ordinal))
                    {
                        if (App.Current?.WidgetManager is not { } manager ||
                            !await manager.SwitchWidgetGroupMemberAsync(
                                targetMemberId,
                                WidgetGroupSwitchOrigin.DragHover))
                        {
                            return false;
                        }
                    }
                }
            }
        }

        return CurrentContentAcceptsFileDrops();
    }

    internal static uint ToNativeAllowedEffects(DataPackageOperation operations)
    {
        uint effects = NativeDropEffectPolicy.None;
        if (operations.HasFlag(DataPackageOperation.Copy))
        {
            effects |= NativeDropEffectPolicy.Copy;
        }

        if (operations.HasFlag(DataPackageOperation.Move))
        {
            effects |= NativeDropEffectPolicy.Move;
        }

        if (operations.HasFlag(DataPackageOperation.Link))
        {
            effects |= NativeDropEffectPolicy.Link;
        }

        return effects;
    }

    internal static FileDropIntent? ResolveSnapshotForcedIntent(
        bool ctrlDown,
        bool shiftDown,
        bool altDown,
        uint allowedEffects,
        bool containsTemporaryFiles,
        IReadOnlyList<string>? sourcePaths = null)
    {
        bool isDeskBoxDrag = sourcePaths is { Count: > 0 } &&
            ActiveDeskBoxDragRegistry.TryMatch(sourcePaths, out _, out _);
        SourceOriginSnapshot origin = sourcePaths is { Count: > 0 }
            ? PathOriginClassifier.ClassifySources(sourcePaths, TimeSpan.FromMilliseconds(80))
            : SourceOriginSnapshot.Empty;
        bool sourceCanCopy = isDeskBoxDrag ||
            (allowedEffects & NativeDropEffectPolicy.Copy) != 0;
        bool sourceCanMove = !origin.IsMoveProtectedSystemOrSharedSource &&
            (isDeskBoxDrag ||
             (allowedEffects & NativeDropEffectPolicy.Move) != 0);
        if (altDown && !ctrlDown && !shiftDown && !containsTemporaryFiles)
        {
            return FileDropIntent.Shortcut;
        }

        if (ctrlDown && !shiftDown)
        {
            return sourceCanCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if (shiftDown && !ctrlDown)
        {
            return !containsTemporaryFiles && sourceCanMove
                ? FileDropIntent.Move
                : FileDropIntent.None;
        }

        return null;
    }

    private static IReadOnlyList<DroppedFilePath> CreateNativeDroppedFilePaths(
        IReadOnlyList<string> paths,
        bool forceManagedCopy,
        IReadOnlyList<DroppedItemOriginKind>? itemOrigins = null)
    {
        var results = new List<DroppedFilePath>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < paths.Count; index++)
        {
            string rawPath = paths[index];
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                continue;
            }

            if ((!File.Exists(fullPath) && !Directory.Exists(fullPath)) ||
                !seen.Add(fullPath))
            {
                continue;
            }

            DroppedItemOriginKind originKind = itemOrigins is not null && index < itemOrigins.Count
                ? itemOrigins[index]
                : (forceManagedCopy || NativeDropTarget.ContainsVirtualDropStagedPaths([fullPath])
                    ? DroppedItemOriginKind.MaterializedVirtualStream
                    : DroppedItemOriginKind.RealFileSystemPath);
            bool itemForceCopy = itemOrigins is not null && index < itemOrigins.Count
                ? originKind != DroppedItemOriginKind.RealFileSystemPath
                : forceManagedCopy;
            results.Add(new DroppedFilePath(
                fullPath,
                Path.GetFileName(
                    fullPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)),
                itemForceCopy,
                originKind));
        }

        return results;
    }

    private T? FindNativeDropDataContext<T>(
        int? screenX,
        int? screenY)
        where T : class
    {
        if (screenX is null || screenY is null || RootGrid.XamlRoot is null)
        {
            return null;
        }

        var clientPoint = new Win32Helper.POINT
        {
            X = screenX.Value,
            Y = screenY.Value
        };
        if (!Win32Helper.ScreenToClient(HWnd, ref clientPoint))
        {
            return null;
        }

        double scale = RootGrid.XamlRoot.RasterizationScale;
        if (scale <= 0)
        {
            scale = 1;
        }

        var point = new Windows.Foundation.Point(
            clientPoint.X / scale,
            clientPoint.Y / scale);
        return VisualTreeHelper.FindElementsInHostCoordinates(point, RootGrid)
            .OfType<FrameworkElement>()
            .Select(element => element.DataContext)
            .OfType<T>()
            .FirstOrDefault();
    }

    private bool IsNativeDropPointOverItemIcon(
        int screenX,
        int screenY,
        WidgetItem targetItem)
    {
        if (RootGrid.XamlRoot is null)
        {
            return false;
        }

        var clientPoint = new Win32Helper.POINT
        {
            X = screenX,
            Y = screenY
        };
        if (!Win32Helper.ScreenToClient(HWnd, ref clientPoint))
        {
            return false;
        }

        double scale = RootGrid.XamlRoot.RasterizationScale;
        if (scale <= 0)
        {
            scale = 1;
        }

        var hostPoint = new Windows.Foundation.Point(
            clientPoint.X / scale,
            clientPoint.Y / scale);
        Controls.FileItemSurface? surface = VisualTreeHelper
            .FindElementsInHostCoordinates(hostPoint, RootGrid)
            .OfType<Controls.FileItemSurface>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, targetItem));
        if (surface?.IconHitTestElement is not { } iconHost)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point localPoint = RootGrid
                .TransformToVisual(iconHost)
                .TransformPoint(hostPoint);
            return ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
                0,
                0,
                iconHost.ActualWidth,
                iconHost.ActualHeight,
                slack: 6,
                localPoint.X,
                localPoint.Y);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static WidgetItem? NormalizeNativeFileDropItemTarget(
        WidgetItem? item)
    {
        return item is { } candidate &&
            ItemDropBehaviorPolicy.Resolve(candidate) is
                ItemDropBehavior.FolderImport or
                ItemDropBehavior.StackImport or
                ItemDropBehavior.RecycleBinDelete
            ? item
            : null;
    }

    private static void CleanupNativeTemporaryDropFiles(
        IReadOnlyList<string> paths)
    {
        // Shared implementation lives with the materialization worker so the
        // refusal branches in NativeDropTarget can clean up orphaned drops
        // with the same temp-root whitelist.
        NativeDropMaterializationWorker.CleanupVirtualDropDirectories(paths);
    }
}
