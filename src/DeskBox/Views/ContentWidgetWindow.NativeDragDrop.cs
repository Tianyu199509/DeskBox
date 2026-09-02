using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
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
    private CancellationTokenSource? _groupFileDropPollCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer?
        _nativeFileDropRegistrationTimer;
    private int _nativeFileDropRegistrationAttempts;
    private Task? _groupFileDropCacheTask;
    private DroppedFileBatch? _groupFileDropBatch;
    private CancellationTokenSource? _pendingNativeFileDropCts;
    private DateTimeOffset _lastXamlFileDropUtc = DateTimeOffset.MinValue;
    private bool _isCachingGroupFileDrop;
    private bool _isGroupFileDropTracking;
    private bool _isGroupFileManualImporting;
    private long _nativeFileDropPointerGeneration;
    private long _groupFileDropGeneration;
    private bool _groupFileDropFormatCached;
    private bool _groupFileDropRequiresFallback;
    private WidgetItem? _nativeFileDropItemTarget;
    // DropIntentEvent is raised immediately before the legacy DropEvent. The
    // flag prevents the compatibility callback from queueing the same import a
    // second time while keeping DropEvent available to older consumers.
    private bool _nativeDropIntentHandledForLegacyCallback;

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
        _nativeFileDropSubclassProc ??= NativeFileDropSubclassProc;
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
                    ShouldFollowWindowsNativeFileDrop,
                    ShouldShortcutOutsideDesktopNativeFileDrop);
                target.DragEnterEvent += NativeFileDropTarget_DragEnterEvent;
                target.DragOverEvent += NativeFileDropTarget_DragOverEvent;
                target.DragLeaveEvent += NativeFileDropTarget_DragLeaveEvent;
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

    private bool HasRegisteredContentBridgeDropTarget()
    {
        foreach ((IntPtr window, NativeDropTarget target) in
                 _nativeFileDropTargets)
        {
            if (window != HWnd && target.IsRegistered)
            {
                return true;
            }
        }

        return false;
    }

    private void QueueNativeFileDropTargetRegistration()
    {
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

    private bool ShouldUseNativeFileDropVisual()
    {
        return CurrentContent switch
        {
            FileSurfaceContent
            {
                SuppressesNativeShellDragVisual: false
            } => true,
            QuickCaptureSurfaceContent => true,
            TodoWidgetContentAdapter => true,
            _ => false
        };
    }

    private bool ShouldDefaultNativeFileDropToMove()
    {
        return CurrentContent is FileSurfaceContent &&
               string.Equals(
                   App.Current.SettingsService.Settings.ManagedDropAction,
                   SettingsService.ManagedDropActionMove,
                   StringComparison.Ordinal);
    }

    private bool ShouldFollowWindowsNativeFileDrop()
    {
        return string.Equals(
            App.Current.SettingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
    }

    private bool ShouldShortcutOutsideDesktopNativeFileDrop()
    {
        return string.Equals(
            App.Current.SettingsService.Settings.ManagedDropAction,
            SettingsService.ManagedDropActionShortcutOutsideDesktop,
            StringComparison.Ordinal);
    }

    private NativeDropDescriptionText? CreateNativeFileDropDescription(
        uint effect)
    {
        if (effect == NativeDropEffectPolicy.None)
        {
            return null;
        }

        if (CurrentContent is QuickCaptureSurfaceContent)
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

        if (CurrentContent is FileSurfaceContent &&
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
                IsFolder: true,
                Name.Length: > 0
            } folder
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
        BeginGroupFileDropTracking(e.DataView);
    }

    private void GroupFileDrop_DragOver(object sender, DragEventArgs e)
    {
        BeginGroupFileDropTracking(e.DataView);
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

            StopGroupFileDropTracking(disposeCachedBatch: true);
            App.Log(
                $"[DropDiagnostic] Group drag leave cleared tracking " +
                $"id={_config.Id} hwnd=0x{HWnd.ToInt64():X}");
        });
    }

    private void GroupFileDrop_Drop(object sender, DragEventArgs e)
    {
        // The nested FileSurfaceContent received a normal Drop event, so the
        // fallback must not import the cached source a second time.
        _lastXamlFileDropUtc = DateTimeOffset.UtcNow;
        CancelPendingNativeFileDropFallback();
        StopGroupFileDropTracking(disposeCachedBatch: true);
    }

    private void BeginGroupFileDropTracking(DataPackageView dataView)
    {
        if (IsClosing ||
            CurrentContent is not FileSurfaceContent file ||
            file.IsImportBusy ||
            file.IsInternalReorderDrag(dataView))
        {
            return;
        }

        // DragEnter is routed again while the pointer crosses nested child
        // elements. Cache the OLE format scan until a real window leave or a
        // Drop calls StopGroupFileDropTracking.
        if (!_groupFileDropFormatCached)
        {
            _groupFileDropRequiresFallback =
                RequiresGroupManualDropFallback(dataView);
            _groupFileDropFormatCached = true;
        }

        if (!_groupFileDropRequiresFallback)
        {
            return;
        }

        if (!_isGroupFileDropTracking)
        {
            _isGroupFileDropTracking = true;
            _groupFileDropGeneration++;
            DisposeGroupFileDropBatch();
        }

        long generation = _groupFileDropGeneration;
        if (_groupFileDropBatch is null && !_isCachingGroupFileDrop)
        {
            _isCachingGroupFileDrop = true;
            _groupFileDropCacheTask = CacheGroupFileDropAsync(dataView, generation);
        }

        EnsureGroupFileDropPoll(generation);
    }

    private static bool RequiresGroupManualDropFallback(DataPackageView dataView)
    {
        bool hasFileDrop = dataView.AvailableFormats.Any(format =>
            format.Contains("FileDrop", StringComparison.OrdinalIgnoreCase));
        bool hasVirtualFileDescriptor = dataView.AvailableFormats.Any(format =>
            format.Contains("FileGroupDescriptor", StringComparison.OrdinalIgnoreCase));
        return hasFileDrop && !hasVirtualFileDescriptor;
    }

    private async Task CacheGroupFileDropAsync(
        DataPackageView dataView,
        long generation)
    {
        DroppedFileBatch? batch = null;
        try
        {
            batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
        }
        catch (Exception ex)
        {
            App.Log($"[DropDiagnostic] Group drop pre-cache failed: {ex.Message}");
        }

        if (generation != _groupFileDropGeneration ||
            !_isGroupFileDropTracking)
        {
            batch?.Dispose();
            return;
        }

        _isCachingGroupFileDrop = false;
        DisposeGroupFileDropBatch();
        _groupFileDropBatch = batch;
        App.Log(
            $"[DropDiagnostic] Group drop pre-cache count=" +
            $"{batch?.Files.Count ?? 0}");
    }

    private void EnsureGroupFileDropPoll(long generation)
    {
        if (_groupFileDropPollCts is not null)
        {
            return;
        }

        _groupFileDropPollCts = new CancellationTokenSource();
        CancellationToken token = _groupFileDropPollCts.Token;
        _ = Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(30, timeoutCts.Token);
                    if (Win32Helper.IsKeyDown(0x01))
                    {
                        continue;
                    }

                    DispatcherQueue.TryEnqueue(
                        () => OnGroupFileDropPollDetectedRelease(generation));
                    return;
                }
            }
            catch (TaskCanceledException)
            {
                // A real Drop event or a new drag ended this fallback path.
            }
            catch (Exception ex)
            {
                App.Log($"[DropDiagnostic] Group drop poll failed: {ex.Message}");
            }
        }, token);
    }

    private async void OnGroupFileDropPollDetectedRelease(long generation)
    {
        // Give a healthy WinUI Drop event a chance to arrive before falling
        // back. Its handled-events-too observer clears the tracking state.
        await Task.Delay(80);
        if (generation != _groupFileDropGeneration ||
            !_isGroupFileDropTracking ||
            _isGroupFileManualImporting)
        {
            return;
        }

        if (!IsPointerOverContentWindow())
        {
            StopGroupFileDropTracking(disposeCachedBatch: true);
            return;
        }

        if (_groupFileDropBatch is null &&
            _groupFileDropCacheTask is { IsCompleted: false } cacheTask)
        {
            await Task.WhenAny(cacheTask, Task.Delay(300));
        }

        if (generation != _groupFileDropGeneration ||
            !_isGroupFileDropTracking)
        {
            return;
        }

        DroppedFileBatch? batch = _groupFileDropBatch;
        _groupFileDropBatch = null;
        if (batch is null || batch.Files.Count == 0)
        {
            batch?.Dispose();
            StopGroupFileDropTracking(disposeCachedBatch: false);
            App.Log("[DropDiagnostic] Group fallback found no importable cached files.");
            return;
        }

        _isGroupFileManualImporting = true;
        StopGroupFileDropTracking(disposeCachedBatch: false);
        try
        {
            if (CurrentContent is FileSurfaceContent file)
            {
                file.SetHostWindowHandle(HWnd);
                bool containsTemporaryFiles = batch.Files.Any(
                    droppedFile => droppedFile.ForceManagedCopy);
                await file.ImportNativeDroppedFilesAsync(
                    batch.Files.Select(droppedFile => droppedFile.Path).ToArray(),
                    containsTemporaryFiles);
            }
        }
        finally
        {
            batch.Dispose();
            _isGroupFileManualImporting = false;
        }
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
        _isGroupFileDropTracking = false;
        _groupFileDropGeneration++;
        _isCachingGroupFileDrop = false;
        _groupFileDropCacheTask = null;
        _groupFileDropFormatCached = false;
        _groupFileDropRequiresFallback = false;
        if (_groupFileDropPollCts is not null)
        {
            _groupFileDropPollCts.Cancel();
            _groupFileDropPollCts.Dispose();
            _groupFileDropPollCts = null;
        }

        if (disposeCachedBatch)
        {
            DisposeGroupFileDropBatch();
        }
    }

    private void DisposeGroupFileDropBatch()
    {
        _groupFileDropBatch?.Dispose();
        _groupFileDropBatch = null;
    }

    private void RemoveNativeFileDropBridge()
    {
        ReleaseNativeFileDropRegistrationTimer();
        CancelPendingNativeFileDropFallback();
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
            IReadOnlyList<string> paths = Win32Helper.GetDroppedFilePaths(
                (IntPtr)wParam);
            App.Log(
                $"[DropDiagnostic] content id={_config.Id} stage=NativeDropFiles " +
                $"count={paths.Count}");
            QueueNativeFileDropImport(
                paths,
                containsTemporaryFiles: false,
                copyWhenMapped: null);
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void NativeFileDropTarget_DragEnterEvent(
        int screenX,
        int screenY,
        bool hasFileData)
    {
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
        System.Threading.Interlocked.Increment(
            ref _nativeFileDropPointerGeneration);
        _nativeFileDropItemTarget = null;
        RunOnNativeFileDropUiThread(file =>
            file.ClearDragSessionVisualState());
    }

    private void ObserveNativeFileDragPointer(
        int screenX,
        int screenY,
        bool hasFileData,
        IReadOnlyList<string>? pathHints = null,
        long pointerGeneration = 0)
    {
        RunOnNativeFileDropUiThread(file =>
        {
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

            WidgetItem? nativeTarget = hasFileData &&
                CurrentContent is FileSurfaceContent
                    ? NormalizeNativeFileDropItemTarget(
                        FindNativeDropDataContext<WidgetItem>(
                            screenX,
                            screenY))
                    : null;
            _nativeFileDropItemTarget = nativeTarget;
            file.ObserveNativeDragPointer(
                screenX,
                screenY,
                hasFileData,
                pathHints,
                nativeTarget);
        });
    }

    private IReadOnlyList<string> GetNativeFileDropPathHints()
    {
        return _nativeFileDropTargets.Values
            .Where(target => target.HasFileData)
            .SelectMany(target => target.DragPathHints)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RunOnNativeFileDropUiThread(Action<FileSurfaceContent> action)
    {
        void Invoke()
        {
            if (!IsClosing && CurrentContent is FileSurfaceContent file)
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
        RunOnNativeFileDropUiThread(file =>
            file.CaptureNativeDropInsertion(screenX, screenY));
        ScheduleNativeFileDropFallback(
            paths,
            containsTemporaryFiles,
            copyWhenMapped,
            screenX,
            screenY);
        _nativeFileDropItemTarget = null;
    }

    private void NativeFileDropTarget_DropIntentEvent(
        NativeDropIntentEventArgs args)
    {
        System.Threading.Interlocked.Increment(
            ref _nativeFileDropPointerGeneration);
        _nativeDropIntentHandledForLegacyCallback = true;
        bool followWindows = ShouldFollowWindowsNativeFileDrop();
        FileDropIntent? forcedIntent = args.ShortcutRequested &&
            !args.ContainsTemporaryFiles
                ? FileDropIntent.Shortcut
                : followWindows &&
                  (args.KeyState & NativeDropEffectPolicy.ControlKeyState) != 0
                    ? FileDropIntent.Copy
                    : followWindows &&
                      (args.KeyState & NativeDropEffectPolicy.ShiftKeyState) != 0
                        ? FileDropIntent.Move
                        : null;
        App.Log(
            $"[DropDiagnostic] content id={_config.Id} stage=NativeDropIntent " +
            $"count={args.Paths.Count} temporary={args.ContainsTemporaryFiles} " +
            $"shortcut={args.ShortcutRequested} rightButton={args.RightButtonDrag} " +
            $"effect={args.FeedbackEffect}");

        // Capture the insertion index synchronously on the UI thread before
        // Explorer's native drag loop sends DragLeave and clears the preview.
        RunOnNativeFileDropUiThread(file =>
            file.CaptureNativeDropInsertion(args.ScreenX, args.ScreenY));

        // A right-button drag is intentionally resolved after release. The
        // native OLE callback cannot wait for a WinUI flyout without blocking
        // Explorer's drag loop, so the target keeps the source untouched and
        // lets DeskBox execute the selected operation asynchronously.
        if (args.RightButtonDrag)
        {
            QueueNativeRightButtonDropChoice(args);
        }
        else
        {
            ScheduleNativeFileDropFallback(
                args.Paths,
                args.ContainsTemporaryFiles,
                args.CopyRequested,
                args.ScreenX,
                args.ScreenY,
                forcedIntent);
        }

        _nativeFileDropItemTarget = null;
    }

    private void QueueNativeRightButtonDropChoice(
        NativeDropIntentEventArgs args)
    {
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                FileDropIntent choice =
                    await ShowNativeRightButtonDropChoiceAsync(args);
                if (choice == FileDropIntent.None)
                {
                    RunOnNativeFileDropUiThread(file =>
                        file.ClearPendingNativeDropInsertion());
                    if (args.ContainsTemporaryFiles)
                    {
                        CleanupNativeTemporaryDropFiles(args.Paths);
                    }

                    return;
                }

                bool copyWhenMapped = choice != FileDropIntent.Move;
                ScheduleNativeFileDropFallback(
                    args.Paths,
                    args.ContainsTemporaryFiles,
                    copyWhenMapped,
                    args.ScreenX,
                    args.ScreenY,
                    choice == FileDropIntent.Shortcut
                        ? FileDropIntent.Shortcut
                        : null);
            }))
        {
            if (args.ContainsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(args.Paths);
            }
        }
    }

    private async Task<FileDropIntent> ShowNativeRightButtonDropChoiceAsync(
        NativeDropIntentEventArgs args)
    {
        // Quick Capture and Todo have their own import contracts; keep their
        // native right-button drop deterministic instead of presenting file
        // grid operations that those surfaces cannot honor.
        if (CurrentContent is not FileSurfaceContent ||
            string.IsNullOrWhiteSpace(
                App.Current.SettingsService.Settings.ManagedDropAction) ||
            RootGrid.XamlRoot is null)
        {
            return FileDropIntent.Copy;
        }

        var completion = new TaskCompletionSource<FileDropIntent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var menu = new MenuFlyout();
        AddNativeRightButtonChoice(
            menu,
            App.Current.LocalizationService.T("Common.Copy"),
            FileDropIntent.Copy,
            completion);
        if (!string.IsNullOrWhiteSpace(
                CurrentContent is FileSurfaceContent file
                    ? file.ViewModel.MappedFolderPath
                    : null))
        {
            AddNativeRightButtonChoice(
                menu,
                App.Current.LocalizationService.T("Common.Move"),
                FileDropIntent.Move,
                completion);
            if (!args.ContainsTemporaryFiles)
            {
                AddNativeRightButtonChoice(
                    menu,
                    App.Current.LocalizationService.T("Widget.CreateShortcut"),
                    FileDropIntent.Shortcut,
                    completion);
            }
        }

        AddNativeRightButtonChoice(
            menu,
            App.Current.LocalizationService.T("Common.Cancel"),
            FileDropIntent.None,
            completion);
        menu.Closed += (_, _) =>
            completion.TrySetResult(FileDropIntent.None);

        try
        {
            menu.ShowAt(
                RootGrid,
                GetNativeDropMenuPoint(args.ScreenX, args.ScreenY));
        }
        catch (Exception ex)
        {
            App.Log($"[DropTarget] Right-button choice menu failed: {ex.Message}");
            return FileDropIntent.Copy;
        }

        return await completion.Task;
    }

    private static void AddNativeRightButtonChoice(
        MenuFlyout menu,
        string text,
        FileDropIntent intent,
        TaskCompletionSource<FileDropIntent> completion)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = intent };
        item.Click += (_, _) =>
        {
            completion.TrySetResult(intent);
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

    private void ScheduleNativeFileDropFallback(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool copyWhenMapped,
        int screenX,
        int screenY,
        FileDropIntent? forcedIntent = null)
    {
        if (paths.Count == 0)
        {
            return;
        }

        string[] ownedPaths = paths.ToArray();
        if (DateTimeOffset.UtcNow - _lastXamlFileDropUtc <
            TimeSpan.FromMilliseconds(500))
        {
            // The routed WinUI path already committed this drop. Do not leave
            // the native preview index armed for a later unrelated import.
            RunOnNativeFileDropUiThread(file =>
                file.ClearPendingNativeDropInsertion());
            if (containsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(ownedPaths);
            }

            App.LogVerbose(
                "[DropTarget] Native import skipped because WinUI handled the drop.");
            return;
        }

        CancelPendingNativeFileDropFallback();
        var cancellation = new CancellationTokenSource();
        _pendingNativeFileDropCts = cancellation;
        _ = CompleteNativeFileDropFallbackAsync(
            ownedPaths,
            containsTemporaryFiles,
            copyWhenMapped,
            screenX,
            screenY,
            forcedIntent,
            cancellation);
    }

    private async Task CompleteNativeFileDropFallbackAsync(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool copyWhenMapped,
        int screenX,
        int screenY,
        FileDropIntent? forcedIntent,
        CancellationTokenSource cancellation)
    {
        bool importOwnsTemporaryFiles = false;
        try
        {
            await Task.Delay(120, cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_pendingNativeFileDropCts, cancellation) ||
                DateTimeOffset.UtcNow - _lastXamlFileDropUtc <
                TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            QueueNativeFileDropImport(
                paths,
                containsTemporaryFiles,
                copyWhenMapped,
                screenX,
                screenY,
                forcedIntent);
            importOwnsTemporaryFiles = true;
        }
        catch (OperationCanceledException)
        {
            // The routed WinUI Drop path completed first.
        }
        finally
        {
            if (ReferenceEquals(_pendingNativeFileDropCts, cancellation))
            {
                _pendingNativeFileDropCts = null;
            }

            if (containsTemporaryFiles && !importOwnsTemporaryFiles)
            {
                CleanupNativeTemporaryDropFiles(paths);
            }

            cancellation.Dispose();
        }
    }

    private void CancelPendingNativeFileDropFallback()
    {
        CancellationTokenSource? cancellation = _pendingNativeFileDropCts;
        _pendingNativeFileDropCts = null;
        cancellation?.Cancel();
    }

    private void QueueNativeFileDropImport(
        IReadOnlyList<string> paths,
        bool containsTemporaryFiles,
        bool? copyWhenMapped,
        int? screenX = null,
        int? screenY = null,
        FileDropIntent? forcedIntent = null)
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
                    forcedIntent)))
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
        FileDropIntent? forcedIntent)
    {
        try
        {
            if (IsClosing)
            {
                return;
            }

            switch (CurrentContent)
            {
                case FileSurfaceContent file:
                    file.SetHostWindowHandle(HWnd);
                    await file.ImportNativeDroppedFilesAsync(
                        paths,
                        containsTemporaryFiles,
                        copyWhenMapped,
                        NormalizeNativeFileDropItemTarget(
                            FindNativeDropDataContext<WidgetItem>(
                                screenX,
                                screenY)),
                        forcedIntent,
                        screenX,
                        screenY);
                    break;

                case QuickCaptureSurfaceContent quickCapture:
                    await quickCapture.ImportNativeDroppedFilesAsync(
                        CreateNativeDroppedFilePaths(
                            paths,
                            containsTemporaryFiles),
                        FindNativeDropDataContext<QuickCaptureItemViewModel>(
                            screenX,
                            screenY));
                    break;

                case TodoWidgetContentAdapter todo:
                    await todo.ImportNativeDroppedFilesAsync(
                        CreateNativeDroppedFilePaths(
                            paths,
                            containsTemporaryFiles),
                        FindNativeDropDataContext<TodoItemViewModel>(
                            screenX,
                            screenY));
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

    private static IReadOnlyList<DroppedFilePath> CreateNativeDroppedFilePaths(
        IReadOnlyList<string> paths,
        bool forceManagedCopy)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DroppedFilePath(
                path,
                Path.GetFileName(
                    path.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)),
                forceManagedCopy))
            .ToArray();
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

    private static WidgetItem? NormalizeNativeFileDropItemTarget(
        WidgetItem? item)
    {
        return item is WidgetStackItem || item is { IsFolder: true }
            ? item
            : null;
    }

    private static void CleanupNativeTemporaryDropFiles(
        IReadOnlyList<string> paths)
    {
        string temporaryRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "DeskBox",
            "VirtualDrops"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        foreach (string directory in paths
                     .Select(Path.GetDirectoryName)
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Select(directory => Path.GetFullPath(directory!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string normalizedDirectory = directory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!normalizedDirectory.StartsWith(
                    temporaryRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[DropTarget] Failed to clean grouped virtual drop directory: {ex.Message}");
            }
        }
    }
}
