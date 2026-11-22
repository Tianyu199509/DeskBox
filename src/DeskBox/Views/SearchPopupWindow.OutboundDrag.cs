using System.Diagnostics;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace DeskBox.Views;

public sealed partial class SearchPopupWindow
{
    // Drag state for result rows: distinguishes a click (execute) from a drag
    // (export the file/folder path to another app or widget) from anywhere on
    // a file/folder row. The candidate is pointer-bound (the model the grid's
    // manual drag uses, FileSurfaceContent.ElevatedPointerDrag): armed only
    // for a physical left-button press with panel pointer capture, re-checked
    // for button contact on every move, disarmed by release, capture loss,
    // right-tap, or popup hide. While a drag-out session is in flight the
    // flag blocks re-arming.
    private readonly record struct SearchDragOutCandidate(
        uint PointerId,
        SearchResultItem Item,
        SearchResultRowControl Row,
        Point StartPoint,
        bool IsRightButtonDrag = false);
    private SearchDragOutCandidate? _dragOutCandidate;
    private SearchResultItem? _pendingCtrlToggleOffOnRelease;
    private bool _dragOutInProgress;
    private bool _suppressNextSearchRightTap;
    private bool _isExternalFileDragActive;
    private bool _externalDragLeftPopupOnce;
    private bool _hasEnteredPlatformDragLoop;
    private bool _externalDragPassthroughApplied;
    private long _searchOutboundVisualRefreshSerial;
    private DispatcherQueueTimer? _externalDragFadeTimer;
    private MenuFlyout? _activeRightDropMenu;
    private bool _restoreResultFocusAfterFlyout;

    private string[]? _activeDragOutPaths;

    // Non-elevated runs use the WinUI platform drag (StartDragAsync): one
    // continuous OLE session from the first threshold move to the drop, with
    // the system drag image and Explorer's drop tips. Elevated runs cannot
    // start that session (StartDragAsync throws in an elevated process and
    // UIPI blocks OLE into Explorer), so only they use the pointer-tracked
    // ghost + DeskBox-performed transfer (or clipboard staging + UIPI toast).
    private static bool IsCurrentProcessElevatedForDrag() =>
        DragDropPermissionService.IsCurrentProcessElevatedCached();

    private void StartDragOut(SearchDragOutCandidate candidate, PointerRoutedEventArgs e)
    {
        _pendingCtrlToggleOffOnRelease = null;
        IReadOnlyList<SearchResultItem> draggedItems =
            SearchResultSelectionPolicy.ResolveDraggedItems(
                candidate.Item,
                _viewModel.CurrentResults
                    .Where(_multiSelectedItems.Contains)
                    .ToList());
        // Same rule as the grid pipeline: members that no longer exist drop
        // out silently, never a text ghost of a deleted file.
        string[] draggedPaths = draggedItems
            .Select(result => result.DetailPath)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(path) ||
                 File.Exists(path) ||
                 Directory.Exists(path)))
            .Select(path => path!)
            .ToArray();
        if (draggedPaths.Length == 0)
        {
            candidate.Row.ClearTransientPointerStates();
            _dragOutInProgress = false;
            App.Log(
                $"[SearchDragOut] canceled reason=no-existing-paths " +
                $"items={draggedItems.Count}");
            return;
        }

        candidate.Row.ClearTransientPointerStates();
        if (!IsCurrentProcessElevatedForDrag())
        {
            UIElement dragSource = candidate.Row;
            _activeDragOutPaths = draggedPaths;
            dragSource.DragStarting += ResultsRow_DragStarting;
            StartExternalDragFadeTimer();
            App.SafeFireAndForget(
                () => RunPlatformDragOutAsync(candidate, draggedPaths, dragSource, e),
                nameof(RunPlatformDragOutAsync));
            return;
        }

        if (candidate.IsRightButtonDrag)
        {
            _suppressNextSearchRightTap = true;
        }

        StartExternalDragFadeTimer();
        App.SafeFireAndForget(
            () => RunDragOutAsync(candidate, draggedPaths),
            nameof(RunDragOutAsync));
    }

    private async Task RunPlatformDragOutAsync(
        SearchDragOutCandidate candidate,
        string[] draggedPaths,
        UIElement dragSource,
        PointerRoutedEventArgs e)
    {
        DataPackageOperation dropResult = DataPackageOperation.None;
        bool shouldAutoHide = false;
        bool shouldSuppressDesktop = false;
        try
        {
            dropResult = await dragSource.StartDragAsync(
                e.GetCurrentPoint(dragSource));
            if (_hasEnteredPlatformDragLoop)
            {
                DropReleaseSnapshot releaseSnapshot = DesktopToastHudInterop.SampleDropReleaseState();
                bool leftPopup = _externalDragLeftPopupOnce || IsCursorOutsidePopupWindow();
                bool droppedOnShellSurface =
                    !releaseSnapshot.EscapePressed &&
                    leftPopup &&
                    releaseSnapshot.TargetKind is not (
                        DropTargetWindowKind.NonExplorerWindow or
                        DropTargetWindowKind.DeskBoxOwnWindow);
                bool sourceMovedOut = dropResult == DataPackageOperation.None &&
                                      !releaseSnapshot.EscapePressed &&
                                      leftPopup &&
                                      HasNonNetworkSourceDisappeared(draggedPaths);
                shouldSuppressDesktop = dropResult != DataPackageOperation.None ||
                                        droppedOnShellSurface;
                shouldAutoHide = (dropResult != DataPackageOperation.None || sourceMovedOut) &&
                                 !Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping this async boundary fail-fasts the process
            // through DispatcherQueue; the boundary always catches and attributes.
            App.Log(
                $"[SearchDragOut] StartDragAsync failed " +
                $"hresult=0x{ex.HResult:X8} type={ex.GetType().Name} " +
                $"item='{candidate.Item.DetailPath}'");
        }
        finally
        {
            StopExternalDragFadeTimer(restoreOpacity: !shouldAutoHide);
            FileDragSourceGuardDataObject.ClearActiveManagedRoot();
            dragSource.DragStarting -= ResultsRow_DragStarting;
            candidate.Row.ClearTransientPointerStates();
            ClearAllResultRowTransientStates();
            _dragOutInProgress = false;
            _activeDragOutPaths = null;
        }

        SuppressSearchDragOutDesktopArrivals(shouldSuppressDesktop, draggedPaths, "search-results");
        App.Log(
            $"[SearchDragOut] completed drop={dropResult} items={draggedPaths.Length}");

        // Default auto-collapse after a successful file drag-out drop (hold Alt to keep open).
        if (shouldAutoHide)
        {
            HidePopup();
        }
    }

    private void ResultsRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        string[] paths = _activeDragOutPaths ?? [];
        if (paths.Length == 0 || IsCurrentProcessElevatedForDrag())
        {
            args.Cancel = true;
            App.Log(
                $"[SearchDragOut] canceled " +
                $"reason={(paths.Length == 0 ? "no-paths" : "elevated")}");
            return;
        }

        // Same payload pipeline as the grid's file drag-out: a native Shell
        // IDataObject when attachable (Explorer-identical formats), a complete
        // StorageItems payload otherwise, never a partial one. Search drags
        // deliberately skip the DeskBox origin stamps — receivers treat them
        // as plain external file transfers.
        var fileService = CurrentApp?.FileService;
        bool prepared = FileItemDragPackage.TryPreparePaths(
            args.Data,
            paths,
            candidatePaths => fileService?.GetStorageItems(candidatePaths) ?? [],
            list => list.Count == 1
                ? Path.GetFileName(list[0])
                : list.Count.ToString(),
            "search",
            out FileItemDragPackageResult result);
        if (!prepared)
        {
            args.Cancel = true;
            return;
        }

        // DragUI setters are only legal before the drag starts; this handler
        // stays fully synchronous so that ordering is structural.
        _hasEnteredPlatformDragLoop = true;
        BeginSearchOutboundVisualRefreshSession();
        // Search results span arbitrary disk/USB/UNC paths rather than managed
        // file-box slots: default to Copy for ordinary paths (Shift still allows Move),
        // Copy-only for protected external/system sources, and Link for AppExecutionAlias stubs.
        bool hasAppAlias = FileItemDragPackage.ContainsAppExecutionAliasSource(result.SourcePaths);
        bool forceCopyOnly =
            DriveMediaInterop.ContainsProtectedInboundCopySource(result.SourcePaths) ||
            FileItemDragPackage.IsProtectedCopyOnlySource(result);
        if (hasAppAlias)
        {
            args.AllowedOperations = DataPackageOperation.Link;
            args.Data.RequestedOperation = DataPackageOperation.Link;
        }
        else if (forceCopyOnly)
        {
            args.AllowedOperations = WindowsCompatibilityService.IsWindows11OrLater
                ? FileItemDragPackage.ProtectedWin11Operations
                : DataPackageOperation.Copy;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
        }
        else
        {
            // Global search results can originate from arbitrary project or
            // system directories; default to Copy so plain drag-out never
            // relocates originals (holding Shift still allows explicit Move).
            args.AllowedOperations =
                FileItemDragPackage.ResolveDragOutAllowedOperations(
                    SettingsService.ManagedDropActionCopy,
                    sourcePaths: result.SourcePaths,
                    isProtectedCopyOnly: false,
                    appExecLinkProbe: static _ => false);
            args.Data.RequestedOperation =
                FileItemDragPackage.ResolveDragOutPreferredOperation(
                    SettingsService.ManagedDropActionCopy,
                    sourcePaths: result.SourcePaths,
                    isProtectedCopyOnly: false,
                    appExecLinkProbe: static _ => false);
        }

        args.DragUI.SetContentFromDataPackage();
        App.Log(
            $"[SearchDragOut] prepared paths={result.SourcePaths.Count} " +
            $"native={result.UsesNativeShellDataObject} " +
            $"allowed={args.AllowedOperations}");
    }

    private async Task RunDragOutAsync(
        SearchDragOutCandidate candidate,
        string[] draggedPaths)
    {
        bool dropSucceeded = false;
        bool shouldAutoHide = false;
        try
        {
            await Task.Yield();
            bool forceCopyOnly =
                Platform.DriveMediaInterop.ContainsProtectedInboundCopySource(draggedPaths) ||
                FileItemDragPackage.IsProtectedCopyOnlySource(draggedPaths);
            dropSucceeded = await ExecuteElevatedSearchOutboundDragAsync(
                draggedPaths,
                forceCopyOnly,
                originTag: "search-results",
                useSecondaryDragButton: candidate.IsRightButtonDrag);
            shouldAutoHide = dropSucceeded &&
                             !Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu);
        }
        catch (Exception ex)
        {
            // Exceptions escaping this fire-and-forget async boundary fail-fast
            // through DispatcherQueue; always catch and log with HRESULT.
            App.Log(
                $"[SearchDragOut] elevated drag failed " +
                $"hresult=0x{ex.HResult:X8} type={ex.GetType().Name} " +
                $"item='{candidate.Item.DetailPath}'");
        }
        finally
        {
            StopExternalDragFadeTimer(restoreOpacity: !shouldAutoHide);
            candidate.Row.ClearTransientPointerStates();
            ClearAllResultRowTransientStates();
            _dragOutInProgress = false;
            if (candidate.IsRightButtonDrag)
            {
                if (!DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () => _suppressNextSearchRightTap = false))
                {
                    _suppressNextSearchRightTap = false;
                }
            }
        }

        App.Log(
            $"[SearchDragOut] completed drop={dropSucceeded} items={draggedPaths.Length}");

        // Default auto-collapse after a successful file drag-out drop (hold Alt to keep open).
        if (shouldAutoHide)
        {
            HidePopup();
        }
    }

    private bool TryStartRecommendedExternalDragOut(
        UIElement dragSource,
        PointerRoutedEventArgs e,
        IReadOnlyList<string> candidatePaths,
        string originTag,
        Action suspendInternalDrag)
    {
        if (_dragOutInProgress ||
            e.Pointer.PointerDeviceType is not (PointerDeviceType.Mouse or PointerDeviceType.Touchpad))
        {
            return false;
        }

        string[] existingPaths = candidatePaths
            .Where(static path =>
                !string.IsNullOrWhiteSpace(path) &&
                (DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(path) ||
                 File.Exists(path) ||
                 Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existingPaths.Length == 0)
        {
            return false;
        }

        if (!IsCurrentProcessElevatedForDrag())
        {
            // Sample the pointer point on dragSource BEFORE suspending internal
            // drag state, and keep pointer capture on dragSource until
            // StartDragAsync enters the platform drag loop so fast flicks across
            // the window edge never drop the XAML pointer contact.
            PointerPoint dragPoint = e.GetCurrentPoint(dragSource);
            suspendInternalDrag();
            _dragOutInProgress = true;
            _activeDragOutPaths = existingPaths;
            dragSource.DragStarting += RecommendedApp_DragStarting;
            StartExternalDragFadeTimer();
            App.Log(
                $"[SearchDragOut] handoff source={originTag} paths={existingPaths.Length}");
            App.SafeFireAndForget(
                () => RunPlatformRecommendedDragOutAsync(dragSource, dragPoint, existingPaths, originTag),
                nameof(RunPlatformRecommendedDragOutAsync));
            return true;
        }

        suspendInternalDrag();
        _dragOutInProgress = true;
        StartExternalDragFadeTimer();
        App.Log(
            $"[SearchDragOut] handoff source={originTag} paths={existingPaths.Length} elevated=True");
        App.SafeFireAndForget(
            () => RunRecommendedDragOutAsync(dragSource, existingPaths, originTag),
            nameof(RunRecommendedDragOutAsync));
        return true;
    }

    private async Task RunPlatformRecommendedDragOutAsync(
        UIElement dragSource,
        PointerPoint dragPoint,
        string[] draggedPaths,
        string originTag)
    {
        DataPackageOperation dropResult = DataPackageOperation.None;
        bool shouldAutoHide = false;
        bool shouldSuppressDesktop = false;
        try
        {
            dropResult = await dragSource.StartDragAsync(dragPoint);
            if (_hasEnteredPlatformDragLoop)
            {
                DropReleaseSnapshot releaseSnapshot = DesktopToastHudInterop.SampleDropReleaseState();
                bool leftPopup = _externalDragLeftPopupOnce || IsCursorOutsidePopupWindow();
                bool droppedOnShellSurface =
                    !releaseSnapshot.EscapePressed &&
                    leftPopup &&
                    releaseSnapshot.TargetKind is not (
                        DropTargetWindowKind.NonExplorerWindow or
                        DropTargetWindowKind.DeskBoxOwnWindow);
                shouldSuppressDesktop = dropResult != DataPackageOperation.None ||
                                        droppedOnShellSurface;
                shouldAutoHide = dropResult != DataPackageOperation.None &&
                                 !RecommendedAppsGrid.IsEditMode &&
                                 !Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[SearchDragOut] StartDragAsync failed " +
                $"hresult=0x{ex.HResult:X8} type={ex.GetType().Name} " +
                $"source={originTag} item='{draggedPaths[0]}'");
        }
        finally
        {
            StopExternalDragFadeTimer(restoreOpacity: !shouldAutoHide);
            FileDragSourceGuardDataObject.ClearActiveManagedRoot();
            dragSource.DragStarting -= RecommendedApp_DragStarting;
            dragSource.ReleasePointerCaptures();
            _dragOutInProgress = false;
            _activeDragOutPaths = null;
        }

        SuppressSearchDragOutDesktopArrivals(shouldSuppressDesktop, draggedPaths, originTag);
        App.Log(
            $"[SearchDragOut] completed source={originTag} drop={dropResult} items={draggedPaths.Length}");

        // Keep edit mode open so batch-organizing users can continue dragging
        // icons; in normal mode auto-collapse after drop unless Alt is held.
        if (shouldAutoHide)
        {
            HidePopup();
        }
    }

    private void RecommendedApp_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        string[] paths = _activeDragOutPaths ?? [];
        if (paths.Length == 0 || IsCurrentProcessElevatedForDrag())
        {
            args.Cancel = true;
            App.Log(
                $"[SearchDragOut] canceled source=recommend " +
                $"reason={(paths.Length == 0 ? "no-paths" : "elevated")}");
            return;
        }

        var fileService = CurrentApp?.FileService;
        bool prepared = FileItemDragPackage.TryPreparePaths(
            args.Data,
            paths,
            candidatePaths => fileService?.GetStorageItems(candidatePaths) ?? [],
            list => list.Count == 1
                ? Path.GetFileNameWithoutExtension(list[0])
                : list.Count.ToString(),
            "search-recommend",
            out FileItemDragPackageResult result);
        if (!prepared)
        {
            args.Cancel = true;
            return;
        }

        // Launcher shortcuts always advertise Copy so dropping into a widget or
        // onto Desktop copies/references the shortcut instead of moving it out
        // of Start Menu or another widget; AppExecutionAlias stubs use Link.
        _hasEnteredPlatformDragLoop = true;
        BeginSearchOutboundVisualRefreshSession();
        if (FileItemDragPackage.ContainsAppExecutionAliasSource(result.SourcePaths))
        {
            args.AllowedOperations = DataPackageOperation.Link;
            args.Data.RequestedOperation = DataPackageOperation.Link;
        }
        else
        {
            args.AllowedOperations = WindowsCompatibilityService.IsWindows11OrLater
                ? FileItemDragPackage.ProtectedWin11Operations
                : DataPackageOperation.Copy;
            args.Data.RequestedOperation = DataPackageOperation.Copy;
        }

        args.DragUI.SetContentFromDataPackage();
        App.Log(
            $"[SearchDragOut] prepared source=recommend paths={result.SourcePaths.Count} " +
            $"native={result.UsesNativeShellDataObject} " +
            $"allowed={args.AllowedOperations}");
    }

    // Explorer performed the transfer (including async shell drops that return
    // DROPEFFECT_NONE upfront); register the expected Desktop arrival names so
    // desktop auto-organization does not immediately absorb them.
    private void SuppressSearchDragOutDesktopArrivals(
        bool shouldSuppressDesktop,
        string[] draggedPaths,
        string originTag)
    {
        if (!shouldSuppressDesktop || draggedPaths.Length == 0)
        {
            return;
        }

        DesktopOutboundDropCoordinator.SuppressDesktopArrivals(
            _settingsService,
            CurrentApp?.OrganizerService,
            draggedPaths,
            originTag);
    }

    private bool IsCursorOutsidePopupWindow() =>
        _hwnd != IntPtr.Zero &&
        Win32Helper.GetCursorPos(out var cursor) &&
        Win32Helper.GetWindowRect(_hwnd, out var rect) &&
        (cursor.X < rect.Left || cursor.X >= rect.Right ||
         cursor.Y < rect.Top || cursor.Y >= rect.Bottom);

    private static bool HasNonNetworkSourceDisappeared(IReadOnlyList<string> draggedPaths)
    {
        foreach (string path in draggedPaths)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(path))
            {
                continue;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunRecommendedDragOutAsync(
        UIElement dragSource,
        string[] draggedPaths,
        string originTag)
    {
        bool dropSucceeded = false;
        bool shouldAutoHide = false;
        try
        {
            await Task.Yield();
            // Launcher shortcuts and Start Menu entries always forbid Move so
            // dropping out copies/creates a link instead of relocating originals.
            dropSucceeded = await ExecuteElevatedSearchOutboundDragAsync(
                draggedPaths,
                forceCopyOnly: true,
                originTag);
            shouldAutoHide = dropSucceeded &&
                             !RecommendedAppsGrid.IsEditMode &&
                             !Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[SearchDragOut] elevated drag failed " +
                $"hresult=0x{ex.HResult:X8} type={ex.GetType().Name} " +
                $"source={originTag} item='{draggedPaths[0]}'");
        }
        finally
        {
            StopExternalDragFadeTimer(restoreOpacity: !shouldAutoHide);
            dragSource.ReleasePointerCaptures();
            _dragOutInProgress = false;
        }

        App.Log(
            $"[SearchDragOut] completed source={originTag} drop={dropSucceeded} items={draggedPaths.Length}");

        // Keep edit mode open so batch-organizing users can continue dragging
        // icons; in normal mode auto-collapse after drop unless Alt is held.
        if (shouldAutoHide)
        {
            HidePopup();
        }
    }

    private async Task<bool> ExecuteElevatedSearchOutboundDragAsync(
        string[] draggedPaths,
        bool forceCopyOnly,
        string originTag,
        bool useSecondaryDragButton = false)
    {
        if (draggedPaths.Any(DesktopOutboundDropCoordinator.MightTouchNetworkPath))
        {
            DesktopOutboundDropCoordinator.BoundedSourcePathProbeResult probe =
                await Task.Run(() => DesktopOutboundDropCoordinator.FilterExistingPathsWithBoundedNetworkProbe(
                    draggedPaths,
                    timeoutMs: 650));
            if (probe.TimedOut || probe.ExistingPaths.Length == 0)
            {
                App.Log(
                    $"[SearchDragOut] canceled elevated drag reason={(probe.TimedOut ? "network-source-timeout" : "no-existing-paths")} " +
                    $"items={draggedPaths.Length}");
                return false;
            }

            draggedPaths = probe.ExistingPaths;
        }

        bool hasAppAlias = FileItemDragPackage.ContainsAppExecutionAliasSource(draggedPaths);
        _hasEnteredPlatformDragLoop = true;

        if (!forceCopyOnly && !hasAppAlias && !useSecondaryDragButton)
        {
            DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: "search-drag-shift-tip",
                    Message: _localizationService.T("Search.DragOut.HoldShiftToMoveTip"),
                    Severity: DesktopToastSeverity.Info,
                    Priority: DesktopToastPriority.Routine,
                    DurationMs: 4500,
                    KeepVisibleDuringDrag: true),
                _settingsService);
        }

        try
        {
            return await RunElevatedSearchOutboundDragAsync(
                draggedPaths,
                forceCopyOnly,
                originTag,
                useSecondaryDragButton,
                hasAppAlias);
        }
        finally
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("search-drag-shift-tip");
        }
    }

    private enum SearchElevatedRightDropChoice
    {
        Cancel = 0,
        Copy = 1,
        Move = 2,
        Shortcut = 3,
    }

    private async Task<SearchElevatedRightDropChoice> ShowElevatedSearchRightDropChoiceAsync(
        int screenX,
        int screenY,
        bool forceCopyOnly,
        bool isRecycleBinTarget = false,
        bool hasAppAlias = false)
    {
        if (ResultsPanel.XamlRoot is null ||
            _hwnd == IntPtr.Zero ||
            !Win32Helper.IsWindowVisible(_hwnd))
        {
            return SearchElevatedRightDropChoice.Cancel;
        }

        StopExternalDragFadeTimer(restoreOpacity: true);
        var completion = new TaskCompletionSource<SearchElevatedRightDropChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var menu = new MenuFlyout();

        void AddChoice(string text, SearchElevatedRightDropChoice choice, bool isDefault = false)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                FontWeight = isDefault
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
            };
            item.Click += (_, _) =>
            {
                completion.TrySetResult(choice);
                menu.Hide();
            };
            menu.Items.Add(item);
        }

        if (isRecycleBinTarget)
        {
            AddChoice(
                _localizationService.T("Widget.DropOnRecycleBinHint"),
                SearchElevatedRightDropChoice.Move,
                isDefault: true);
        }
        else if (hasAppAlias)
        {
            AddChoice(
                _localizationService.T("Widget.CreateShortcut"),
                SearchElevatedRightDropChoice.Shortcut,
                isDefault: true);
        }
        else
        {
            AddChoice(
                _localizationService.T("Common.Copy"),
                SearchElevatedRightDropChoice.Copy,
                isDefault: true);
            if (!forceCopyOnly)
            {
                AddChoice(
                    _localizationService.T("Common.Move"),
                    SearchElevatedRightDropChoice.Move);
            }

            AddChoice(
                _localizationService.T("Widget.CreateShortcut"),
                SearchElevatedRightDropChoice.Shortcut);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        AddChoice(
            _localizationService.T("Common.Cancel"),
            SearchElevatedRightDropChoice.Cancel);

        _activeRightDropMenu = menu;
        try
        {
            var clientPt = new Win32Helper.POINT { X = screenX, Y = screenY };
            double scale = ResultsPanel.XamlRoot.RasterizationScale > 0
                ? ResultsPanel.XamlRoot.RasterizationScale
                : 1.0;
            Point localPoint;
            if (Win32Helper.ScreenToClient(_hwnd, ref clientPt))
            {
                Point rootLogical = new(clientPt.X / scale, clientPt.Y / scale);
                localPoint = RootGrid.TransformToVisual(ResultsPanel).TransformPoint(rootLogical);
            }
            else
            {
                localPoint = new Point(16, 16);
            }

            return await DropChoiceFlyoutHelper.ShowAndWaitAsync(
                menu,
                ResultsPanel,
                localPoint,
                completion,
                SearchElevatedRightDropChoice.Cancel,
                hostWindowHandle: _hwnd);
        }
        catch
        {
            return SearchElevatedRightDropChoice.Cancel;
        }
        finally
        {
            if (ReferenceEquals(_activeRightDropMenu, menu))
            {
                _activeRightDropMenu = null;
            }
        }
    }

    private async Task<bool> RunElevatedSearchOutboundDragAsync(
        string[] draggedPaths,
        bool forceCopyOnly,
        string originTag,
        bool useSecondaryDragButton = false,
        bool hasAppAlias = false)
    {
        var app = CurrentApp;
        using var ghost = new ElevatedItemDragGhostWindow();
        string title = draggedPaths.Length == 1
            ? Path.GetFileName(draggedPaths[0])
            : draggedPaths.Length.ToString();
        ghost.BeginSession(icon: null, fallbackGlyph: "\uE8A5", title, draggedPaths.Length);

        DeskBox.Controls.WidgetContents.FileSurfaceContent? hoveredPopover = null;
        ContentWidgetWindow? hoveredWidget = null;
        IntPtr lastOutboundHitHwnd = IntPtr.Zero;
        IntPtr lastOutboundRootHwnd = IntPtr.Zero;
        IntPtr cachedExplorerRootHwnd = IntPtr.Zero;
        IntPtr cachedExplorerTabHwnd = IntPtr.Zero;
        long cachedExplorerTickMs = 0;
        var cachedExplorerTarget = new ElevatedOutboundTargetInfo(
            ElevatedOutboundTargetKind.None,
            string.Empty,
            string.Empty);
        int releaseX = 0;
        int releaseY = 0;
        bool lastCtrlHeld = false;
        bool lastShiftHeld = false;
        bool lastAltHeld = false;
        bool canceledByEscape = false;

        try
        {
            while (true)
            {
                (bool dragButtonDown, bool abortButtonDown) =
                    Win32Helper.QueryLogicalDragButtonState(useSecondaryDragButton);
                if (abortButtonDown || Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Escape))
                {
                    canceledByEscape = true;
                    break;
                }

                if (!dragButtonDown)
                {
                    break;
                }

                EvaluateExternalDragFadeTick();
                if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
                {
                    await Task.Delay(16);
                    continue;
                }

                releaseX = cursor.X;
                releaseY = cursor.Y;
                lastCtrlHeld = Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Control);
                lastShiftHeld = Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Shift);
                lastAltHeld = Win32Helper.IsAsyncKeyPressed(Windows.System.VirtualKey.Menu);

                IntPtr hitHwnd = Win32Helper.WindowFromPoint(cursor);
                IntPtr rootHwnd = hitHwnd != IntPtr.Zero
                    ? Win32Helper.GetAncestor(hitHwnd, Win32Helper.GA_ROOT)
                    : IntPtr.Zero;
                if (rootHwnd == IntPtr.Zero)
                {
                    rootHwnd = hitHwnd;
                }

                if (rootHwnd == ghost.WindowHandle || rootHwnd == _hwnd)
                {
                    hoveredPopover?.LeaveCrossWidgetElevatedStackPopoverDrag();
                    hoveredPopover = null;
                    hoveredWidget?.LeaveElevatedPointerDrag();
                    hoveredWidget = null;
                    lastOutboundHitHwnd = IntPtr.Zero;
                    lastOutboundRootHwnd = IntPtr.Zero;
                    ghost.UpdateTracking(cursor.X, cursor.Y, string.Empty, canDrop: false);
                    await Task.Delay(16);
                    continue;
                }

                if (DeskBox.Controls.WidgetContents.FileSurfaceContent.TryGetActiveStackPopoverOwner(rootHwnd) is { } popoverOwner)
                {
                    if (!ReferenceEquals(hoveredPopover, popoverOwner))
                    {
                        hoveredPopover?.LeaveCrossWidgetElevatedStackPopoverDrag();
                        hoveredPopover = popoverOwner;
                    }

                    hoveredWidget?.LeaveElevatedPointerDrag();
                    hoveredWidget = null;
                    lastOutboundHitHwnd = IntPtr.Zero;
                    lastOutboundRootHwnd = IntPtr.Zero;

                    string caption = popoverOwner.ObserveCrossWidgetElevatedStackPopoverDrag(
                        cursor.X,
                        cursor.Y,
                        draggedPaths,
                        out bool canDrop);
                    ghost.UpdateTracking(cursor.X, cursor.Y, caption, canDrop);
                    await Task.Delay(16);
                    continue;
                }

                hoveredPopover?.LeaveCrossWidgetElevatedStackPopoverDrag();
                hoveredPopover = null;

                ContentWidgetWindow? widgetTarget = FindElevatedTargetWidgetWindow(rootHwnd);
                if (widgetTarget is not null)
                {
                    if (!ReferenceEquals(hoveredWidget, widgetTarget))
                    {
                        hoveredWidget?.LeaveElevatedPointerDrag();
                        hoveredWidget = widgetTarget;
                    }

                    lastOutboundHitHwnd = IntPtr.Zero;
                    lastOutboundRootHwnd = IntPtr.Zero;
                    string caption = widgetTarget.ObserveElevatedPointerDrag(
                        cursor.X,
                        cursor.Y,
                        draggedPaths,
                        _localizationService,
                        out bool canDrop,
                        forceCopyUnlessExplicit: true);
                    ghost.UpdateTracking(cursor.X, cursor.Y, caption, canDrop);
                    await Task.Delay(16);
                    continue;
                }

                hoveredWidget?.LeaveElevatedPointerDrag();
                hoveredWidget = null;

                bool immediatelyClassified = ElevatedShellOutboundDropInterop.TryClassifyImmediateOutboundTarget(
                    hitHwnd,
                    rootHwnd,
                    out IntPtr resolvedRoot,
                    out IntPtr tabKey,
                    out ElevatedOutboundTargetInfo immediate);
                if (!immediatelyClassified &&
                    resolvedRoot != IntPtr.Zero &&
                    resolvedRoot != _hwnd)
                {
                    long nowMs = Environment.TickCount64;
                    bool sameCachedTab =
                        cachedExplorerRootHwnd == resolvedRoot &&
                        cachedExplorerTabHwnd == tabKey &&
                        cachedExplorerTickMs != 0;
                    long cacheTtl = cachedExplorerTarget.Kind == ElevatedOutboundTargetKind.ExplorerFolder
                        ? 400
                        : 500;
                    if (sameCachedTab)
                    {
                        immediate = cachedExplorerTarget;
                    }

                    if (!sameCachedTab || nowMs - cachedExplorerTickMs >= cacheTtl)
                    {
                        ElevatedShellOutboundDropInterop.RequestExplorerWindowTargetProbe(
                            resolvedRoot,
                            tabKey,
                            (probedRoot, probedTab, resolvedInfo) =>
                            {
                                _ = DispatcherQueue.TryEnqueue(() =>
                                {
                                    cachedExplorerRootHwnd = probedRoot;
                                    cachedExplorerTabHwnd = probedTab;
                                    cachedExplorerTickMs = Environment.TickCount64;
                                    cachedExplorerTarget = resolvedInfo;
                                });
                            });
                    }
                }

                lastOutboundHitHwnd = hitHwnd;
                lastOutboundRootHwnd = resolvedRoot;

                bool canExternalDrop = immediate.Kind != ElevatedOutboundTargetKind.UnsupportedShellNamespace &&
                                       !(immediate.Kind == ElevatedOutboundTargetKind.RecycleBin && (forceCopyOnly || hasAppAlias)) &&
                                       resolvedRoot != IntPtr.Zero &&
                                       resolvedRoot != _hwnd;
                string externalCaption = FormatElevatedSearchOutboundCaption(
                    immediate,
                    forceCopyOnly,
                    lastShiftHeld,
                    lastAltHeld,
                    lastCtrlHeld,
                    hasAppAlias);
                ghost.UpdateTracking(cursor.X, cursor.Y, externalCaption, canExternalDrop);
                await Task.Delay(16);
            }

            ghost.Hide();
            if (canceledByEscape)
            {
                return false;
            }

            if (hoveredPopover is { } targetPopover)
            {
                hoveredPopover = null;
                bool moveRequested = lastShiftHeld && !forceCopyOnly;
                bool controlOverride = lastCtrlHeld || (!useSecondaryDragButton && !lastAltHeld && !moveRequested);
                return await targetPopover.AcceptCrossWidgetElevatedStackPopoverDropAsync(
                    draggedPaths,
                    releaseX,
                    releaseY,
                    controlOverride,
                    moveRequested,
                    lastAltHeld,
                    isSecondaryButtonDrag: useSecondaryDragButton);
            }

            if (hoveredWidget is { } targetWidget)
            {
                hoveredWidget = null;
                bool moveRequested = lastShiftHeld && !forceCopyOnly;
                bool controlOverride = lastCtrlHeld || (!useSecondaryDragButton && !lastAltHeld && !moveRequested);
                return await targetWidget.AcceptElevatedPointerDropAsync(
                    draggedPaths,
                    releaseX,
                    releaseY,
                    controlOverride,
                    moveRequested,
                    lastAltHeld,
                    isSecondaryButtonDrag: useSecondaryDragButton);
            }

            if (lastOutboundRootHwnd == IntPtr.Zero || lastOutboundRootHwnd == _hwnd)
            {
                return false;
            }

            bool allowDesktopRecycleBinHit =
                ElevatedShellOutboundDropInterop.ShouldAllowDesktopRecycleBinDrop(
                    lastCtrlHeld,
                    lastAltHeld);
            ElevatedOutboundTargetInfo resolvedTarget = await Task.Run(() =>
                ElevatedShellOutboundDropInterop.ResolveOutboundTarget(
                    lastOutboundHitHwnd,
                    lastOutboundRootHwnd,
                    explorerQueryTimeoutMs: 1500,
                    screenX: releaseX,
                    screenY: releaseY,
                    allowDesktopRecycleBinHit: allowDesktopRecycleBinHit));
            if (resolvedTarget.Kind == ElevatedOutboundTargetKind.RecycleBin)
            {
                if (forceCopyOnly || app?.FileService is not { } binFileService)
                {
                    return false;
                }

                Platform.SourceOriginSnapshot origin = Platform.PathOriginClassifier.ClassifySources(
                    draggedPaths,
                    Platform.PathOriginClassifier.DefaultDropProbeBudget);
                if (origin.HasAppExecutionAliasSource ||
                    origin.IsMoveProtectedSystemOrSharedSource)
                {
                    return false;
                }

                if (useSecondaryDragButton)
                {
                    SearchElevatedRightDropChoice binChoice = await ShowElevatedSearchRightDropChoiceAsync(
                        releaseX,
                        releaseY,
                        forceCopyOnly: false,
                        isRecycleBinTarget: true);
                    if (binChoice != SearchElevatedRightDropChoice.Move)
                    {
                        return false;
                    }
                }

                IReadOnlySet<string> deleted = await binFileService.DeleteEntriesWithShellAsync(
                    draggedPaths,
                    recycle: true,
                    ownerHandle: _hwnd);
                if (deleted.Count > 0)
                {
                    DesktopToastHudService.Shared.TryShow(
                        new DesktopToastRequest(
                            HintId: "search-outbound-recycle-bin-receipt",
                            Message: _localizationService.Format("Widget.MovedToRecycleBin", deleted.Count),
                            Severity: DesktopToastSeverity.Success,
                            Priority: DesktopToastPriority.ContextualGuide,
                            DurationMs: 4000),
                        _settingsService);
                    return true;
                }

                return false;
            }

            if (resolvedTarget.Kind is ElevatedOutboundTargetKind.Desktop or ElevatedOutboundTargetKind.ExplorerFolder)
            {
                string destinationFolder = resolvedTarget.DestinationFolderPath;
                if (string.IsNullOrWhiteSpace(destinationFolder))
                {
                    return false;
                }

                bool mightTouchNetwork =
                    DesktopOutboundDropCoordinator.MightTouchNetworkPath(destinationFolder) ||
                    draggedPaths.Any(DesktopOutboundDropCoordinator.MightTouchNetworkPath);
                bool destinationExists = mightTouchNetwork
                    ? await DesktopOutboundDropCoordinator.IsDestinationDirectoryReachableAsync(
                        destinationFolder,
                        800)
                    : Directory.Exists(destinationFolder);

                if (!destinationExists ||
                    Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(destinationFolder) ||
                    await Task.Run(() => Services.FileService.IsUnsafeDirectoryTransfer(draggedPaths, destinationFolder)))
                {
                    return false;
                }

                SearchElevatedRightDropChoice rightChoice = hasAppAlias
                    ? SearchElevatedRightDropChoice.Shortcut
                    : SearchElevatedRightDropChoice.Copy;
                if (useSecondaryDragButton)
                {
                    rightChoice = await ShowElevatedSearchRightDropChoiceAsync(
                        releaseX,
                        releaseY,
                        forceCopyOnly,
                        hasAppAlias: hasAppAlias);
                    if (rightChoice == SearchElevatedRightDropChoice.Cancel)
                    {
                        return false;
                    }
                }

                bool createShortcuts = hasAppAlias || (useSecondaryDragButton
                    ? rightChoice == SearchElevatedRightDropChoice.Shortcut
                    : (lastAltHeld || (lastCtrlHeld && lastShiftHeld)));
                bool isDesktopTarget =
                    resolvedTarget.Kind == ElevatedOutboundTargetKind.Desktop ||
                    DesktopOutboundDropCoordinator.IsUserDesktopPath(destinationFolder);
                if (createShortcuts)
                {
                    DeskBox.Services.DroppedFilePath[] shortcutSources = draggedPaths
                        .Select(static path => new DeskBox.Services.DroppedFilePath(
                            path,
                            Path.GetFileName(path),
                            ForceManagedCopy: false))
                        .ToArray();
                    IReadOnlyList<string> createdShortcuts =
                        await DeskBox.Controls.WidgetContents.FileSurfaceContent.CreateShortcutFilesAsync(
                            shortcutSources,
                            destinationFolder,
                            _localizationService.T("Widget.CreateShortcut"),
                            CancellationToken.None);
                    if (isDesktopTarget && createdShortcuts.Count > 0)
                    {
                        DesktopOutboundDropCoordinator.SuppressDesktopArrivals(
                            _settingsService,
                            app?.OrganizerService,
                            createdShortcuts,
                            originTag);
                        if (resolvedTarget.Kind == ElevatedOutboundTargetKind.Desktop)
                        {
                            _ = await Task.Run(() =>
                                ElevatedShellOutboundDropInterop.TryPositionDesktopItemsNearCursor(
                                    createdShortcuts.ToArray(),
                                    releaseX,
                                    releaseY));
                        }
                    }

                    return createdShortcuts.Count > 0;
                }

                bool move = useSecondaryDragButton
                    ? rightChoice == SearchElevatedRightDropChoice.Move && !forceCopyOnly
                    : lastShiftHeld && !lastCtrlHeld && !forceCopyOnly;
                if (isDesktopTarget)
                {
                    DesktopOutboundDropCoordinator.SuppressDesktopArrivals(
                        _settingsService,
                        app?.OrganizerService,
                        draggedPaths,
                        originTag);
                }

                if (app?.FileService is not { } fileService)
                {
                    return false;
                }

                IReadOnlyList<Services.FileService.FileTransferResult> results =
                    await fileService.TransferItemsWithResultAsync(
                        draggedPaths,
                        destinationFolder,
                        move,
                        progress: null,
                        CancellationToken.None,
                        useShellProgress: true,
                        ownerWindowHandle: _hwnd);
                string[] createdDestinations = results
                    .Select(static r => r.DestinationPath)
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (isDesktopTarget && createdDestinations.Length > 0)
                {
                    DesktopOutboundDropCoordinator.SuppressDesktopArrivals(
                        _settingsService,
                        app.OrganizerService,
                        createdDestinations,
                        originTag);
                    if (resolvedTarget.Kind == ElevatedOutboundTargetKind.Desktop)
                    {
                        _ = await Task.Run(() =>
                            ElevatedShellOutboundDropInterop.TryPositionDesktopItemsNearCursor(
                                createdDestinations,
                                releaseX,
                                releaseY));
                    }
                }

                if (move && results.Count > 0)
                {
                    await RecordSearchOutboundMoveUndoResultsAsync(
                        results,
                        destinationFolder);
                }

                return createdDestinations.Length > 0;
            }

            if (resolvedTarget.Kind == ElevatedOutboundTargetKind.ForeignWindow)
            {
                _ = ShellClipboardHelper.TrySetFileDropList(draggedPaths, cut: false);
                DesktopToastHudService.Shared.TryShow(
                    new DesktopToastRequest(
                        HintId: "search-elevated-uipi-tip",
                        Message: _localizationService.T("Search.DragOut.ElevatedThirdPartyHint"),
                        Severity: DesktopToastSeverity.Warning,
                        Priority: DesktopToastPriority.ContextualGuide,
                        DurationMs: 5000),
                    _settingsService);
                return false;
            }

            return false;
        }
        finally
        {
            hoveredPopover?.LeaveCrossWidgetElevatedStackPopoverDrag();
            hoveredWidget?.LeaveElevatedPointerDrag();
        }
    }

    private ContentWidgetWindow? FindElevatedTargetWidgetWindow(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero ||
            rootHwnd == _hwnd ||
            CurrentApp?.WidgetManager is not { } manager)
        {
            return null;
        }

        foreach (ContentWidgetWindow candidate in manager.ContentWidgets.Values)
        {
            if (candidate.WindowHandle == rootHwnd && candidate.CanObserveElevatedPointerDrag)
            {
                return candidate;
            }
        }

        return null;
    }

    private string FormatElevatedSearchOutboundCaption(
        ElevatedOutboundTargetInfo immediate,
        bool forceCopyOnly,
        bool shiftHeld,
        bool altHeld = false,
        bool ctrlHeld = false,
        bool hasAppAlias = false)
    {
        switch (immediate.Kind)
        {
            case ElevatedOutboundTargetKind.ForeignWindow:
                return _localizationService.T("Widget.ElevatedDragOut.BridgeClipboardCaption");
            case ElevatedOutboundTargetKind.UnsupportedShellNamespace:
                return _localizationService.T("Widget.ElevatedDragOut.UnsupportedShellTarget");
            case ElevatedOutboundTargetKind.RecycleBin:
                return (forceCopyOnly || hasAppAlias)
                    ? string.Empty
                    : _localizationService.T("Widget.DropOnRecycleBinHint");
            case ElevatedOutboundTargetKind.Desktop:
            case ElevatedOutboundTargetKind.ExplorerFolder:
            {
                string verb = (hasAppAlias || altHeld || (ctrlHeld && shiftHeld))
                    ? _localizationService.T("Widget.CreateShortcut")
                    : (!forceCopyOnly && shiftHeld
                        ? _localizationService.T("Common.Move")
                        : _localizationService.T("Common.Copy"));
                string targetName = immediate.Kind == ElevatedOutboundTargetKind.Desktop
                    ? _localizationService.T("Common.Desktop")
                    : immediate.DisplayName;
                return string.IsNullOrWhiteSpace(targetName)
                    ? verb
                    : $"{verb} → {targetName}";
            }
            default:
                return string.Empty;
        }
    }

    private async Task RecordSearchOutboundMoveUndoResultsAsync(
        IReadOnlyList<Services.FileService.FileTransferResult> results,
        string destinationFolder)
    {
        if (results.Count == 0 || CurrentApp?.OrganizerService is not { } organizerService)
        {
            return;
        }

        string searchTitle = _localizationService.T("Search.Title");
        OrganizationHistoryEntry? undoEntry = await organizerService.RecordCompletedMoveHistoryAsync(
            new WidgetConfig { Id = "global-search", Name = searchTitle },
            searchTitle,
            results);
        if (undoEntry is not { CanUndo: true, IsUndone: false })
        {
            return;
        }

        string folderName = string.IsNullOrWhiteSpace(destinationFolder)
            ? _localizationService.T("Common.Desktop")
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(destinationFolder));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = destinationFolder;
        }

        string undoId = undoEntry.Id;
        string message = _localizationService.Format("Widget.MovedToFolder", folderName, results.Count);
        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: "search-outbound-move-undo-receipt",
                Message: message,
                Severity: DesktopToastSeverity.Success,
                Priority: DesktopToastPriority.ContextualGuide,
                ActionButtonText: _localizationService.T("Widget.DragHud.Action.Undo"),
                OnActionAsync: () => organizerService.UndoAsync(undoId, IntPtr.Zero),
                DurationMs: 5000),
            _settingsService);
    }

    private void StartExternalDragFadeTimer()
    {
        // Release PopupShowStoryboard's HoldEnd animation lock on RootGrid.Opacity
        // and lock the local baseline at 1.0 so the fade timer can drive Opacity
        // down to 0.20 once the pointer leaves the popup bounds.
        PopupShowStoryboard.Stop();
        RootGrid.Opacity = 1.0;
        PopupScaleTransform.ScaleX = 1.0;
        PopupScaleTransform.ScaleY = 1.0;

        EndSearchOutboundVisualRefreshSession();
        _isExternalFileDragActive = true;
        _externalDragLeftPopupOnce = false;
        _hasEnteredPlatformDragLoop = false;
        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null)
        {
            return;
        }

        if (_externalDragFadeTimer is null)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.IsRepeating = true;
            timer.Interval = TimeSpan.FromMilliseconds(40);
            timer.Tick += ExternalDragFadeTimer_Tick;
            _externalDragFadeTimer = timer;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        _externalDragFadeTimer.Start();
    }

    private void ExternalDragFadeTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _ = sender;
        _ = args;
        EvaluateExternalDragFadeTick();
    }

    private void EvaluateExternalDragFadeTick()
    {
        if (!_isExternalFileDragActive || !IsPopupVisible || _isWindowClosed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (Win32Helper.GetCursorPos(out var cursor) &&
            Win32Helper.GetWindowRect(_hwnd, out var rect))
        {
            bool insidePopup = cursor.X >= rect.Left && cursor.X < rect.Right &&
                               cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
            if (!insidePopup)
            {
                _externalDragLeftPopupOnce = true;
            }

            // Once the drag has left the popup bounds, keep the popup faded and
            // drop-through (WS_EX_TRANSPARENT) for the rest of the drag session
            // so widgets positioned behind SearchPopupWindow remain visible and
            // directly accept drops without popup hit-test interference.
            bool passthrough = _externalDragLeftPopupOnce && _hasEnteredPlatformDragLoop;
            SetExternalDragWindowPassthrough(passthrough);

            double targetOpacity = (_externalDragLeftPopupOnce || !insidePopup) ? 0.20 : 1.0;
            if (Math.Abs(RootGrid.Opacity - targetOpacity) > 0.01)
            {
                RootGrid.Opacity = targetOpacity;
            }

            if (_hasEnteredPlatformDragLoop && _searchOutboundVisualRefreshSerial != 0)
            {
                EvaluateSearchOutboundVisualRefreshTick(insidePopup);
            }
        }
    }

    private void SetExternalDragWindowPassthrough(bool enabled)
    {
        if (_externalDragPassthroughApplied == enabled || _hwnd == IntPtr.Zero || _isWindowClosed)
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(_hwnd, Win32Helper.GWL_EXSTYLE);
        if (enabled)
        {
            if ((extendedStyle & Win32Helper.WS_EX_TRANSPARENT) == 0)
            {
                _ = Win32Helper.SetWindowLong(
                    _hwnd,
                    Win32Helper.GWL_EXSTYLE,
                    extendedStyle | Win32Helper.WS_EX_TRANSPARENT);
                _ = Win32Helper.SetWindowPos(
                    _hwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    Win32Helper.SWP_NOMOVE |
                    Win32Helper.SWP_NOSIZE |
                    Win32Helper.SWP_NOZORDER |
                    Win32Helper.SWP_NOACTIVATE |
                    Win32Helper.SWP_FRAMECHANGED);
            }

            _externalDragPassthroughApplied = true;
        }
        else
        {
            _ = Win32Helper.SetWindowLong(
                _hwnd,
                Win32Helper.GWL_EXSTYLE,
                extendedStyle & ~Win32Helper.WS_EX_TRANSPARENT);
            _ = Win32Helper.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                Win32Helper.SWP_NOMOVE |
                Win32Helper.SWP_NOSIZE |
                Win32Helper.SWP_NOZORDER |
                Win32Helper.SWP_NOACTIVATE |
                Win32Helper.SWP_FRAMECHANGED);
            _externalDragPassthroughApplied = false;
        }
    }

    private void BeginSearchOutboundVisualRefreshSession()
    {
        if (_searchOutboundVisualRefreshSerial != 0)
        {
            return;
        }

        _ = Services.OutboundDragVisualRefreshCoordinator.Shared.TryBeginDragSession(
            Environment.TickCount64,
            out _searchOutboundVisualRefreshSerial);
    }

    private void EndSearchOutboundVisualRefreshSession()
    {
        if (_searchOutboundVisualRefreshSerial == 0)
        {
            return;
        }

        long serial = _searchOutboundVisualRefreshSerial;
        _searchOutboundVisualRefreshSerial = 0;
        Services.OutboundDragVisualRefreshCoordinator.Shared.EndDragSession(serial);
    }

    private void EvaluateSearchOutboundVisualRefreshTick(bool insidePopup)
    {
        long serial = _searchOutboundVisualRefreshSerial;
        if (serial == 0)
        {
            return;
        }

        IntPtr exclusionHwnd = (_externalDragPassthroughApplied && insidePopup)
            ? IntPtr.Zero
            : _hwnd;
        Platform.OutboundCursorEnvironmentSnapshot env =
            Platform.DesktopToastHudInterop.SampleOutboundDragRefreshEnvironment(exclusionHwnd);
        Services.OutboundDragVisualRefreshDecision decision =
            Services.OutboundDragVisualRefreshCoordinator.Shared.EvaluateTick(
                serial,
                Environment.TickCount64,
                in env);

        switch (decision.Action)
        {
            case Services.OutboundDragVisualRefreshAction.MoveCursor:
                if (!Platform.DesktopToastHudInterop.TryMoveCursorAndPulseDragOver(decision.TargetPoint))
                {
                    Services.OutboundDragVisualRefreshDecision failDecision =
                        Services.OutboundDragVisualRefreshCoordinator.Shared.NotifyMoveFailed(serial);
                    if (failDecision.Action == Services.OutboundDragVisualRefreshAction.StopWatching)
                    {
                        _searchOutboundVisualRefreshSerial = 0;
                    }
                }
                else
                {
                    if (decision.IsReturnStep)
                    {
                        App.Log(
                            $"[SearchDragOut] stage=ColdStartVisualRefresh " +
                            $"pulse={decision.PulseNumber} complete={decision.CompletesWarmup}");
                    }

                    if (decision.CompletesWarmup)
                    {
                        _searchOutboundVisualRefreshSerial = 0;
                    }
                }

                break;

            case Services.OutboundDragVisualRefreshAction.StopWatching:
                _searchOutboundVisualRefreshSerial = 0;
                break;
        }
    }

    private void StopExternalDragFadeTimer(bool restoreOpacity)
    {
        EndSearchOutboundVisualRefreshSession();
        _isExternalFileDragActive = false;
        _externalDragLeftPopupOnce = false;
        _hasEnteredPlatformDragLoop = false;
        _externalDragFadeTimer?.Stop();
        SetExternalDragWindowPassthrough(false);
        if (restoreOpacity && IsPopupVisible && !_isWindowClosed)
        {
            RootGrid.Opacity = 1.0;
        }
    }

    private void DisposeExternalDragFadeTimer()
    {
        EndSearchOutboundVisualRefreshSession();
        _isExternalFileDragActive = false;
        _externalDragLeftPopupOnce = false;
        _hasEnteredPlatformDragLoop = false;
        SetExternalDragWindowPassthrough(false);
        if (_externalDragFadeTimer is not { } timer)
        {
            return;
        }

        _externalDragFadeTimer = null;
        timer.Tick -= ExternalDragFadeTimer_Tick;
        try
        {
            timer.Stop();
        }
        catch
        {
        }
        PerformanceLogger.RecordTransientUiTimerReleased();
    }
}
