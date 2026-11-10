using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal const double ElevatedDragThresholdDip = 6.0;
    private const double AutoScrollEdgeZoneDip = 36.0;
    private const double AutoScrollMinStepDip = 2.0;
    private const double AutoScrollMaxStepDip = 18.0;

    private enum ElevatedDragTargetRoute
    {
        None,
        SameSurfaceStackHeaderReorder,
        SameSurfaceItemsReorder,
        SameSurfaceJoinStack,
        SameSurfaceDetachFromStack,
        SameSurfaceFolderDrop,
        SameSurfaceRecycleBinDrop,
        SameSurfaceShortcutLaunch,
        StackPopoverInternalReorder,
        StackPopoverJoinFromSurface,
        OtherWidgetStackPopover,
        OtherWidgetWindow,
        OutboundExternal,
        Blocked,
    }

    private sealed class ElevatedPointerDragCandidate
    {
        public required uint PointerId { get; init; }

        public required WidgetItem PressedItem { get; init; }

        public required bool IsStackHeader { get; init; }

        public required bool FromStackPopover { get; init; }

        public required string? SourceStackKey { get; init; }

        public required UIElement CaptureHost { get; init; }

        public required ListViewBase OriginView { get; init; }

        public required Windows.Foundation.Point StartPointOnHost { get; init; }

        public required WidgetItem[] PrePressSelection { get; init; }
    }

    private sealed class ElevatedPointerDragSession
    {
        public required string SessionId { get; init; }

        public required uint PointerId { get; init; }

        public required bool IsStackHeader { get; init; }

        public required string? StackReorderKey { get; init; }

        public required bool FromStackPopover { get; init; }

        public required string? SourceStackKey { get; init; }

        public required WidgetItem[] DraggedItems { get; init; }

        public required string[] SourcePaths { get; init; }

        public required UIElement CaptureHost { get; init; }

        public required ListViewBase OriginView { get; init; }

        public required bool RestoreOriginItemClickEnabled { get; init; }

        public bool PointerCaptured { get; set; }

        public bool DetachedContinueByPolling { get; set; }

        public int LastScreenX { get; set; }

        public int LastScreenY { get; set; }

        public bool HasLastScreenPoint { get; set; }

        public bool LastCtrlDown { get; set; }

        public bool LastShiftDown { get; set; }

        public bool LastAltDown { get; set; }

        public int MissingButtonTicks { get; set; }

        public ElevatedDragTargetRoute Route { get; set; }

        public WidgetStackItem? TargetStack { get; set; }

        public WidgetItem? TargetChildItem { get; set; }

        public ContentWidgetWindow? HoveredOtherWindow { get; set; }

        public FileSurfaceContent? HoveredOtherStackPopoverOwner { get; set; }

        public IntPtr LastOutboundHitHwnd { get; set; }

        public IntPtr LastOutboundRootHwnd { get; set; }

        public IntPtr CachedOutboundRootHwnd { get; set; }

        public IntPtr CachedOutboundTabHwnd { get; set; }

        public long CachedOutboundResolvedTickMs { get; set; }

        public ElevatedOutboundTargetInfo CachedOutboundTarget { get; set; }
    }

    private PointerEventHandler? _elevatedRootPointerMovedHandler;
    private PointerEventHandler? _elevatedRootPointerReleasedHandler;
    private PointerEventHandler? _elevatedRootPointerCanceledHandler;
    private PointerEventHandler? _elevatedRootPointerCaptureLostHandler;
    private ElevatedPointerDragCandidate? _elevatedDragCandidate;
    private ElevatedPointerDragSession? _elevatedDragSession;
    private ElevatedItemDragGhostWindow? _elevatedDragGhostWindow;
    private DispatcherTimer? _elevatedDragWatchTimer;

    internal static bool ShouldCrossElevatedDragThreshold(
        Windows.Foundation.Point start,
        Windows.Foundation.Point current) =>
        FileItemSelectionGeometry.GetDragDistance(start, current) >=
        ElevatedDragThresholdDip;

    internal static double ComputeAutoScrollDelta(
        double pointerOffset,
        double viewportLength,
        double currentScrollOffset,
        double scrollableLength)
    {
        if (viewportLength <= 1 || scrollableLength <= 0.5)
        {
            return 0;
        }

        double edgeZone = Math.Min(AutoScrollEdgeZoneDip, viewportLength * 0.25);
        if (edgeZone <= 1)
        {
            return 0;
        }

        if (pointerOffset < edgeZone && currentScrollOffset > 0.5)
        {
            double ratio = Math.Clamp((edgeZone - pointerOffset) / edgeZone, 0.0, 1.0);
            double step = AutoScrollMinStepDip +
                          ((AutoScrollMaxStepDip - AutoScrollMinStepDip) * ratio * ratio);
            return -Math.Min(step, currentScrollOffset);
        }

        double trailingStart = viewportLength - edgeZone;
        if (pointerOffset > trailingStart &&
            currentScrollOffset < scrollableLength - 0.5)
        {
            double ratio = Math.Clamp(
                (pointerOffset - trailingStart) / edgeZone,
                0.0,
                1.0);
            double step = AutoScrollMinStepDip +
                          ((AutoScrollMaxStepDip - AutoScrollMinStepDip) * ratio * ratio);
            return Math.Min(step, scrollableLength - currentScrollOffset);
        }

        return 0;
    }

    private void InitializeElevatedPointerDrag()
    {
        if (!IsCurrentProcessElevatedCached() ||
            _elevatedRootPointerMovedHandler is not null)
        {
            return;
        }

        _elevatedRootPointerMovedHandler = Root_ElevatedPointerMoved;
        _elevatedRootPointerReleasedHandler = Root_ElevatedPointerReleased;
        _elevatedRootPointerCanceledHandler = Root_ElevatedPointerCanceled;
        _elevatedRootPointerCaptureLostHandler = Root_ElevatedPointerCaptureLost;

        Root.AddHandler(
            UIElement.PointerMovedEvent,
            _elevatedRootPointerMovedHandler,
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerReleasedEvent,
            _elevatedRootPointerReleasedHandler,
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerCanceledEvent,
            _elevatedRootPointerCanceledHandler,
            handledEventsToo: true);
        Root.AddHandler(
            UIElement.PointerCaptureLostEvent,
            _elevatedRootPointerCaptureLostHandler,
            handledEventsToo: true);
    }

    private void DisposeElevatedPointerDrag()
    {
        CancelElevatedPointerDrag();
        StopElevatedDragWatchTimer(dispose: true);
        _elevatedDragGhostWindow?.Dispose();
        _elevatedDragGhostWindow = null;

        if (_elevatedRootPointerMovedHandler is not null)
        {
            Root.RemoveHandler(
                UIElement.PointerMovedEvent,
                _elevatedRootPointerMovedHandler);
            _elevatedRootPointerMovedHandler = null;
        }

        if (_elevatedRootPointerReleasedHandler is not null)
        {
            Root.RemoveHandler(
                UIElement.PointerReleasedEvent,
                _elevatedRootPointerReleasedHandler);
            _elevatedRootPointerReleasedHandler = null;
        }

        if (_elevatedRootPointerCanceledHandler is not null)
        {
            Root.RemoveHandler(
                UIElement.PointerCanceledEvent,
                _elevatedRootPointerCanceledHandler);
            _elevatedRootPointerCanceledHandler = null;
        }

        if (_elevatedRootPointerCaptureLostHandler is not null)
        {
            Root.RemoveHandler(
                UIElement.PointerCaptureLostEvent,
                _elevatedRootPointerCaptureLostHandler);
            _elevatedRootPointerCaptureLostHandler = null;
        }
    }

    private void TryArmElevatedItemPointerDrag(
        object sender,
        WidgetItem item,
        PointerRoutedEventArgs e)
    {
        if (!IsCurrentProcessElevatedCached() ||
            _isDisposed ||
            _isImportBusy ||
            _elevatedDragSession is not null ||
            IsStackPopoverItemRenameEditing ||
            _stackPopoverItemRenameCommitInProgress ||
            item is WidgetStackItem)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (FileItemSelectionGeometry.HasAncestor<ScrollBar>(source) ||
             FileItemSelectionGeometry.HasAncestor<ButtonBase>(source) ||
             FileItemSelectionGeometry.HasAncestor<TextBox>(source)))
        {
            return;
        }

        if (FileItemSurface.TryGetInteractiveBorder(sender) is { } border &&
            FileItemSurface.FindOwner(border) is { IsListMode: true } surface &&
            !surface.IsPointWithinListPrimaryContent(
                e.GetCurrentPoint(surface).Position))
        {
            // List-row blank remainder belongs to marquee selection, matching
            // Explorer and Items_DragStarting.
            return;
        }

        bool fromStackPopover = IsHostedInStackPopover(sender);
        UIElement captureHost = fromStackPopover &&
            _stackPopoverSelectionHost is not null
            ? _stackPopoverSelectionHost
            : Root;
        ListViewBase originView = fromStackPopover &&
            _stackPopoverItemsView is not null
            ? _stackPopoverItemsView
            : GetMainSurfaceItemsView();

        PointerPoint pointOnHost = e.GetCurrentPoint(captureHost);
        bool isPrimaryContact =
            pointOnHost.Properties.IsLeftButtonPressed ||
            (pointOnHost.PointerDeviceType is
                PointerDeviceType.Touch or PointerDeviceType.Pen &&
             pointOnHost.IsInContact);
        if (!isPrimaryContact)
        {
            return;
        }

        WidgetItem[] prePressSelection = originView.SelectedItems.Contains(item)
            ? originView.SelectedItems
                .OfType<WidgetItem>()
                .Where(selected => selected is not WidgetStackItem)
                .Distinct()
                .ToArray()
            : [];

        _elevatedDragCandidate = new ElevatedPointerDragCandidate
        {
            PointerId = e.Pointer.PointerId,
            PressedItem = item,
            IsStackHeader = false,
            FromStackPopover = fromStackPopover,
            SourceStackKey = fromStackPopover ? _stackPopoverKey : null,
            CaptureHost = captureHost,
            OriginView = originView,
            StartPointOnHost = pointOnHost.Position,
            PrePressSelection = prePressSelection,
        };
    }

    private void TryArmElevatedStackPointerDrag(
        Border border,
        WidgetStackItem stack,
        PointerRoutedEventArgs e)
    {
        if (!IsCurrentProcessElevatedCached() ||
            _isDisposed ||
            _isImportBusy ||
            _elevatedDragSession is not null ||
            stack.IsExpanded)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (FileItemSelectionGeometry.HasAncestor<ScrollBar>(source) ||
             FileItemSelectionGeometry.HasAncestor<ButtonBase>(source) ||
             FileItemSelectionGeometry.HasAncestor<TextBox>(source)))
        {
            return;
        }

        PointerPoint pointOnRoot = e.GetCurrentPoint(Root);
        if (!pointOnRoot.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _elevatedDragCandidate = new ElevatedPointerDragCandidate
        {
            PointerId = e.Pointer.PointerId,
            PressedItem = stack,
            IsStackHeader = true,
            FromStackPopover = false,
            SourceStackKey = null,
            CaptureHost = Root,
            OriginView = GetMainSurfaceItemsView(),
            StartPointOnHost = pointOnRoot.Position,
            PrePressSelection = [],
        };
    }

    private void DisarmElevatedPointerDragCandidate(uint pointerId)
    {
        if (_elevatedDragCandidate is { } candidate &&
            candidate.PointerId == pointerId)
        {
            _elevatedDragCandidate = null;
        }
    }

    private void Root_ElevatedPointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        _ = TryHandleElevatedPointerMoved(e);
    }

    private void Root_ElevatedPointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _ = TryHandleElevatedPointerReleased(e);
    }

    private void Root_ElevatedPointerCanceled(
        object sender,
        PointerRoutedEventArgs e)
    {
        HandleElevatedPointerCaptureLost(e.Pointer.PointerId);
    }

    private void Root_ElevatedPointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        HandleElevatedPointerCaptureLost(e.Pointer.PointerId);
    }

    private bool TryHandleElevatedPointerMoved(PointerRoutedEventArgs e)
    {
        if (_elevatedDragSession is { } activeSession)
        {
            if (e.Pointer.PointerId != activeSession.PointerId)
            {
                return false;
            }

            if (Win32Helper.IsKeyPressed(VirtualKey.Escape))
            {
                CancelElevatedPointerDrag();
                e.Handled = true;
                return true;
            }

            UpdateActiveElevatedPointerDragFromCursor();
            e.Handled = true;
            return true;
        }

        if (_elevatedDragCandidate is not { } candidate ||
            e.Pointer.PointerId != candidate.PointerId)
        {
            return false;
        }

        PointerPoint currentPoint = e.GetCurrentPoint(candidate.CaptureHost);
        bool stillInContact =
            currentPoint.Properties.IsLeftButtonPressed ||
            (currentPoint.PointerDeviceType is
                PointerDeviceType.Touch or PointerDeviceType.Pen &&
             currentPoint.IsInContact);
        if (!stillInContact)
        {
            _elevatedDragCandidate = null;
            return false;
        }

        if (!ShouldCrossElevatedDragThreshold(
                candidate.StartPointOnHost,
                currentPoint.Position))
        {
            return false;
        }

        _elevatedDragCandidate = null;
        if (!TryStartElevatedPointerDragSession(candidate, e.Pointer))
        {
            return false;
        }

        UpdateActiveElevatedPointerDragFromCursor();
        e.Handled = true;
        return true;
    }

    private bool TryHandleElevatedPointerReleased(PointerRoutedEventArgs e)
    {
        DisarmElevatedPointerDragCandidate(e.Pointer.PointerId);

        if (_elevatedDragSession is not { } activeSession ||
            e.Pointer.PointerId != activeSession.PointerId)
        {
            return false;
        }

        if (Win32Helper.IsKeyPressed(VirtualKey.Escape))
        {
            CancelElevatedPointerDrag();
            e.Handled = true;
            return true;
        }

        CompleteElevatedPointerDragOnRelease(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void HandleElevatedPointerCaptureLost(uint pointerId)
    {
        DisarmElevatedPointerDragCandidate(pointerId);
        if (_elevatedDragSession is { } activeSession &&
            activeSession.PointerId == pointerId)
        {
            if (!_isDisposed &&
                !IsLoaded &&
                Win32Helper.IsAnyMouseButtonDown())
            {
                // Tab switch inside a Widget Group detaches CaptureHost while
                // the user still holds the mouse button. Hand off tracking and
                // release detection to _elevatedDragWatchTimer instead of
                // aborting the drag mid-gesture.
                activeSession.PointerCaptured = false;
                activeSession.DetachedContinueByPolling = true;
                return;
            }

            CancelElevatedPointerDrag();
        }
    }

    private bool TryStartElevatedPointerDragSession(
        ElevatedPointerDragCandidate candidate,
        Pointer pointer)
    {
        if (_isDisposed ||
            _isImportBusy ||
            (candidate.FromStackPopover &&
             (IsStackPopoverItemRenameEditing || _stackPopoverItemRenameCommitInProgress)))
        {
            return false;
        }

        CancelPendingSlowClickRename();
        HideAllNameExpansions();
        ResetBoxSelectionState();
        ClearInternalLaunchHover();
        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        ClearStackMemberDropTarget();
        PersistSurfaceReorder();
        HideStackPopoverReorderIndicator();

        string sessionId = Guid.NewGuid().ToString("N");
        WidgetItem[] draggedItems;
        string[] sourcePaths;
        string? stackReorderKey = null;
        ImageSource? previewIcon;
        string fallbackGlyph;
        string previewTitle;
        int previewCount;

        if (candidate.IsStackHeader &&
            candidate.PressedItem is WidgetStackItem stack)
        {
            if (stack.Members.Any(member => GetTransferState(member).BlocksMutation))
            {
                WidgetItem busyMember = stack.Members.First(member =>
                    GetTransferState(member).BlocksMutation);
                ShowTransferBlockedFeedback(GetTransferState(busyMember));
                return false;
            }

            _stackPointerDragStarted = true;
            _stackInputActivation.CancelPointer();
            draggedItems = [stack];
            sourcePaths = [];
            stackReorderKey = stack.StackKey;
            previewIcon = stack.Members.FirstOrDefault()?.Icon ?? stack.Icon;
            fallbackGlyph = "\uE8F1";
            previewTitle = stack.Name;
            previewCount = Math.Max(1, stack.Members.Count);
        }
        else
        {
            IReadOnlyList<WidgetItem> pointerSelection =
                candidate.PrePressSelection.Length > 1
                    ? candidate.PrePressSelection
                    : candidate.OriginView.SelectedItems
                        .OfType<WidgetItem>()
                        .Where(selected => selected is not WidgetStackItem)
                        .Distinct()
                        .ToArray();
            IReadOnlyList<WidgetItem> resolved =
                FileItemDragPackage.ResolveDraggedItems(
                    [candidate.PressedItem],
                    pointerSelection);
            draggedItems = resolved
                .Where(item =>
                    item is not WidgetStackItem &&
                    !string.IsNullOrWhiteSpace(item.Path) &&
                    (File.Exists(item.Path) || Directory.Exists(item.Path)))
                .ToArray();
            if (draggedItems.Length == 0 ||
                TryBlockTransferMutation(draggedItems))
            {
                return false;
            }

            sourcePaths = draggedItems
                .Select(item => Path.GetFullPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourcePaths.Length == 0)
            {
                return false;
            }

            if (HasActiveTransferSource(sourcePaths))
            {
                ShowTransferBlockedFeedback(
                    _fileService.TransferSessions.GetState(sourcePaths[0]));
                return false;
            }

            // Ensure the dragged item(s) reflect in the ListView selection when
            // dragging an unselected item, matching standard Explorer drag.
            if (!candidate.OriginView.SelectedItems.Contains(draggedItems[0]))
            {
                candidate.OriginView.SelectedItems.Clear();
                foreach (WidgetItem dragged in draggedItems)
                {
                    candidate.OriginView.SelectedItems.Add(dragged);
                }

                UpdateSelectionCommandBar();
                RefreshItemSelectionVisuals();
            }

            previewIcon = draggedItems[0].Icon;
            fallbackGlyph = draggedItems[0].FallbackGlyph;
            previewTitle = draggedItems.Length == 1
                ? draggedItems[0].Name
                : _localizationService.Format(
                    "Widget.SelectionCount",
                    draggedItems.Length);
            if (string.Equals(previewTitle, "Widget.SelectionCount", StringComparison.Ordinal))
            {
                previewTitle = draggedItems[0].Name;
            }

            previewCount = draggedItems.Length;
        }

        bool restoreItemClick = candidate.OriginView.IsItemClickEnabled;
        candidate.OriginView.IsItemClickEnabled = false;
        bool captured = candidate.CaptureHost.CapturePointer(pointer);
        if (!captured)
        {
            candidate.OriginView.IsItemClickEnabled = restoreItemClick;
            return false;
        }

        if (candidate.FromStackPopover)
        {
            _stackPopoverDragActive = true;
            StartStackPopoverOutboundDragPassthroughWatch();
        }

        _pendingPointerDragItems = [];
        App.Current?.WidgetManager?.BeginWidgetInteraction("elevated-pointer-drag");
        if (sourcePaths.Length > 0)
        {
            ActiveDeskBoxDragRegistry.Begin(
                sessionId,
                WidgetId,
                sourcePaths,
                candidate.FromStackPopover);
            bool isProtectedCopyOnly = FileItemDragPackage.IsProtectedCopyOnlySource(sourcePaths);
            DataPackageOperation allowedOperations = FileItemDragPackage.ResolveDragOutAllowedOperations(
                _settingsService.Settings.FileWidget.ManagedDragOutAction,
                sourcePaths: sourcePaths,
                isProtectedCopyOnly: isProtectedCopyOnly);
            MaybeShowDragOutModifierTip(allowedOperations);
        }

        _elevatedDragSession = new ElevatedPointerDragSession
        {
            SessionId = sessionId,
            PointerId = candidate.PointerId,
            IsStackHeader = candidate.IsStackHeader,
            StackReorderKey = stackReorderKey,
            FromStackPopover = candidate.FromStackPopover,
            SourceStackKey = candidate.SourceStackKey,
            DraggedItems = draggedItems,
            SourcePaths = sourcePaths,
            CaptureHost = candidate.CaptureHost,
            OriginView = candidate.OriginView,
            RestoreOriginItemClickEnabled = restoreItemClick,
            PointerCaptured = true,
            LastCtrlDown = Win32Helper.IsKeyPressed(VirtualKey.Control),
            LastShiftDown = Win32Helper.IsKeyPressed(VirtualKey.Shift),
            LastAltDown = Win32Helper.IsKeyPressed(VirtualKey.Menu),
        };

        EnsureElevatedDragGhostWindow().BeginSession(
            previewIcon,
            fallbackGlyph,
            previewTitle,
            previewCount);
        StartElevatedDragWatchTimer();

        App.Log(
            $"[DragProtocol] stage=ElevatedPointerDragStarted widget={WidgetId} " +
            $"session={FormatDragSessionId(sessionId)} " +
            $"kind={(candidate.IsStackHeader ? "stack" : "file")} " +
            $"popover={candidate.FromStackPopover} paths={sourcePaths.Length}");
        return true;
    }

    private ElevatedItemDragGhostWindow EnsureElevatedDragGhostWindow()
    {
        _elevatedDragGhostWindow ??= new ElevatedItemDragGhostWindow();
        return _elevatedDragGhostWindow;
    }

    private void StartElevatedDragWatchTimer()
    {
        if (_elevatedDragWatchTimer is null)
        {
            _elevatedDragWatchTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(32),
            };
            _elevatedDragWatchTimer.Tick += ElevatedDragWatchTimer_Tick;
        }

        if (!_elevatedDragWatchTimer.IsEnabled)
        {
            _elevatedDragWatchTimer.Start();
        }
    }

    private void StopElevatedDragWatchTimer(bool dispose = false)
    {
        if (_elevatedDragWatchTimer is null)
        {
            return;
        }

        _elevatedDragWatchTimer.Stop();
        if (dispose)
        {
            _elevatedDragWatchTimer.Tick -= ElevatedDragWatchTimer_Tick;
            _elevatedDragWatchTimer = null;
        }
    }

    private void ElevatedDragWatchTimer_Tick(object? sender, object e)
    {
        if (_elevatedDragSession is not { } session)
        {
            StopElevatedDragWatchTimer();
            return;
        }

        if (_isDisposed || Win32Helper.IsKeyPressed(VirtualKey.Escape))
        {
            CancelElevatedPointerDrag();
            return;
        }

        if (!Win32Helper.IsAnyMouseButtonDown())
        {
            if (session.DetachedContinueByPolling)
            {
                CompleteElevatedPointerDragOnRelease(pointer: null);
                return;
            }

            session.MissingButtonTicks++;
            if (session.MissingButtonTicks >= 2)
            {
                // Safety net if an OS modal switch swallowed PointerReleased
                // without raising PointerCaptureLost: cancel cleanly rather
                // than leaving a stranded ghost or mutating unexpectedly.
                CancelElevatedPointerDrag();
                return;
            }
        }
        else
        {
            session.MissingButtonTicks = 0;
        }

        bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        bool alt = Win32Helper.IsKeyPressed(VirtualKey.Menu);
        bool modifiersChanged =
            ctrl != session.LastCtrlDown ||
            shift != session.LastShiftDown ||
            alt != session.LastAltDown;
        session.LastCtrlDown = ctrl;
        session.LastShiftDown = shift;
        session.LastAltDown = alt;

        bool cursorMovedWhileDetached = false;
        if (session.DetachedContinueByPolling &&
            Win32Helper.GetCursorPos(out Win32Helper.POINT polledCursor) &&
            (!session.HasLastScreenPoint ||
             polledCursor.X != session.LastScreenX ||
             polledCursor.Y != session.LastScreenY))
        {
            cursorMovedWhileDetached = true;
        }

        bool scrolled = TryStepElevatedDragAutoScroll(session);
        if (modifiersChanged || scrolled || cursorMovedWhileDetached)
        {
            UpdateActiveElevatedPointerDragFromCursor();
        }
    }

    private bool TryStepElevatedDragAutoScroll(ElevatedPointerDragSession session)
    {
        if (!session.HasLastScreenPoint)
        {
            return false;
        }

        int screenX = session.LastScreenX;
        int screenY = session.LastScreenY;

        if (_stackPopoverHostWindow is { IsVisible: true } popoverHost &&
            _stackPopoverItemsView is { } popoverView &&
            TryGetScreenPointInElement(
                popoverView,
                popoverHost.WindowHandle,
                screenX,
                screenY,
                out Windows.Foundation.Point popoverPoint))
        {
            return TryAutoScrollListView(popoverView, popoverPoint);
        }

        ListViewBase mainView = GetMainSurfaceItemsView();
        if (_hostWindowHandle != IntPtr.Zero &&
            TryGetScreenPointInElement(
                mainView,
                _hostWindowHandle,
                screenX,
                screenY,
                out Windows.Foundation.Point mainPoint))
        {
            return TryAutoScrollListView(mainView, mainPoint);
        }

        return false;
    }

    private bool TryAutoScrollListView(
        ListViewBase view,
        Windows.Foundation.Point pointInView)
    {
        if (GetRenderWindowScrollViewer(view) is not { } scrollViewer)
        {
            return false;
        }

        double deltaY = ComputeAutoScrollDelta(
            pointInView.Y,
            scrollViewer.ViewportHeight,
            scrollViewer.VerticalOffset,
            scrollViewer.ScrollableHeight);
        double deltaX = ComputeAutoScrollDelta(
            pointInView.X,
            scrollViewer.ViewportWidth,
            scrollViewer.HorizontalOffset,
            scrollViewer.ScrollableWidth);
        if (Math.Abs(deltaY) < 0.1 && Math.Abs(deltaX) < 0.1)
        {
            return false;
        }

        double? targetHorizontal = Math.Abs(deltaX) >= 0.1
            ? Math.Clamp(
                scrollViewer.HorizontalOffset + deltaX,
                0,
                scrollViewer.ScrollableWidth)
            : null;
        double? targetVertical = Math.Abs(deltaY) >= 0.1
            ? Math.Clamp(
                scrollViewer.VerticalOffset + deltaY,
                0,
                scrollViewer.ScrollableHeight)
            : null;
        return scrollViewer.ChangeView(
            targetHorizontal,
            targetVertical,
            zoomFactor: null,
            disableAnimation: true);
    }

    private void UpdateActiveElevatedPointerDragFromCursor()
    {
        if (_elevatedDragSession is not { } session ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            return;
        }

        session.LastScreenX = cursor.X;
        session.LastScreenY = cursor.Y;
        session.HasLastScreenPoint = true;

        string caption = EvaluateElevatedPointerDragTarget(
            session,
            cursor.X,
            cursor.Y,
            out bool canDrop);
        EnsureElevatedDragGhostWindow().UpdateTracking(
            cursor.X,
            cursor.Y,
            caption,
            canDrop);
    }

    private string EvaluateElevatedPointerDragTarget(
        ElevatedPointerDragSession session,
        int screenX,
        int screenY,
        out bool canDrop)
    {
        canDrop = false;
        session.Route = ElevatedDragTargetRoute.None;
        session.TargetStack = null;
        session.TargetChildItem = null;

        IntPtr hitHwnd = Win32Helper.WindowFromPoint(new Win32Helper.POINT
        {
            X = screenX,
            Y = screenY,
        });
        IntPtr rootHwnd = hitHwnd != IntPtr.Zero
            ? Win32Helper.GetAncestor(hitHwnd, Win32Helper.GA_ROOT)
            : IntPtr.Zero;
        if (rootHwnd == IntPtr.Zero)
        {
            rootHwnd = hitHwnd;
        }

        // 1. Open stack popover window owned by this surface.
        if (_stackPopoverHostWindow is { IsVisible: true } popoverHost &&
            rootHwnd == popoverHost.WindowHandle)
        {
            ClearHoveredOtherStackPopover(session);
            ClearHoveredOtherWindow(session);
            ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: true);
            return EvaluateStackPopoverHover(session, popoverHost, screenX, screenY, out canDrop);
        }

        HideStackPopoverReorderIndicator();

        // 1b. Open stack popover window owned by ANOTHER file widget.
        if (!session.IsStackHeader &&
            session.SourcePaths.Length > 0 &&
            TryGetActiveStackPopoverOwner(rootHwnd) is { } otherPopoverOwner &&
            !ReferenceEquals(otherPopoverOwner, this))
        {
            ClearHoveredOtherWindow(session);
            ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
            if (!ReferenceEquals(session.HoveredOtherStackPopoverOwner, otherPopoverOwner))
            {
                ClearHoveredOtherStackPopover(session);
                session.HoveredOtherStackPopoverOwner = otherPopoverOwner;
            }

            string popoverCaption = otherPopoverOwner.ObserveCrossWidgetElevatedStackPopoverDrag(
                screenX,
                screenY,
                session.SourcePaths,
                out canDrop);
            session.Route = canDrop
                ? ElevatedDragTargetRoute.OtherWidgetStackPopover
                : ElevatedDragTargetRoute.Blocked;
            return popoverCaption;
        }

        ClearHoveredOtherStackPopover(session);

        // 2. This widget's main surface (only while still hosted/loaded).
        if (IsLoaded &&
            _hostWindowHandle != IntPtr.Zero &&
            rootHwnd == _hostWindowHandle &&
            TryGetScreenPointInElement(
                Root,
                _hostWindowHandle,
                screenX,
                screenY,
                out _))
        {
            ClearHoveredOtherWindow(session);
            return EvaluateSameSurfaceHover(session, screenX, screenY, out canDrop);
        }

        ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);

        // 3. Another open DeskBox ContentWidgetWindow — or our own host window's
        // inactive group title switcher outside Root (only file drags, never a
        // collapsed stack header).
        if (!session.IsStackHeader &&
            session.SourcePaths.Length > 0 &&
            FindTargetContentWidgetWindow(rootHwnd, screenX, screenY) is { } otherWindow)
        {
            if (!ReferenceEquals(session.HoveredOtherWindow, otherWindow))
            {
                ClearHoveredOtherWindow(session);
                session.HoveredOtherWindow = otherWindow;
            }

            string otherCaption = otherWindow.ObserveElevatedPointerDrag(
                screenX,
                screenY,
                session.SourcePaths,
                _localizationService,
                out canDrop);
            session.Route = canDrop
                ? ElevatedDragTargetRoute.OtherWidgetWindow
                : ElevatedDragTargetRoute.Blocked;
            return otherCaption;
        }

        ClearHoveredOtherWindow(session);

        // 4. Non-DeskBox window (Desktop, File Explorer, or third-party app).
        if (!session.IsStackHeader &&
            session.SourcePaths.Length > 0 &&
            rootHwnd != IntPtr.Zero &&
            App.Current?.IsDeskBoxWindow(rootHwnd) != true)
        {
            session.LastOutboundHitHwnd = hitHwnd;
            session.LastOutboundRootHwnd = rootHwnd;
            string outboundCaption = ObserveElevatedOutboundTarget(
                session,
                hitHwnd,
                rootHwnd,
                screenX,
                screenY,
                session.SourcePaths,
                out canDrop);
            session.Route = canDrop
                ? ElevatedDragTargetRoute.OutboundExternal
                : ElevatedDragTargetRoute.Blocked;
            return outboundCaption;
        }

        session.Route = ElevatedDragTargetRoute.Blocked;
        return string.Empty;
    }

    private string EvaluateStackPopoverHover(
        ElevatedPointerDragSession session,
        StackPopoverHostWindow popoverHost,
        int screenX,
        int screenY,
        out bool canDrop)
    {
        canDrop = false;
        if (session.IsStackHeader ||
            _stackPopoverItemsView is not { } popoverView ||
            _stackPopoverKey is not { Length: > 0 } popoverKey ||
            ViewModel.FindStackByKey(popoverKey) is not { } openStack)
        {
            session.Route = ElevatedDragTargetRoute.Blocked;
            HideStackPopoverReorderIndicator();
            return string.Empty;
        }

        if (!session.IsStackHeader &&
            TryResolveStackPopoverChildIconDropTarget(
                screenX,
                screenY,
                out Border? childBorder,
                out WidgetItem? childTarget) &&
            childBorder is not null &&
            childTarget is not null &&
            !session.SourcePaths.Contains(
                childTarget.Path,
                StringComparer.OrdinalIgnoreCase))
        {
            ItemDropBehavior childBehavior = ItemDropBehaviorPolicy.Resolve(childTarget);
            if (childBehavior == ItemDropBehavior.FolderImport &&
                ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                    childTarget,
                    out string childFolderPath))
            {
                HideStackPopoverReorderIndicator();
                ClearLaunchDropTarget();
                if (HasTransferConflict(session.SourcePaths, childFolderPath) ||
                    IsUnsafeFolderDrop(session.SourcePaths, childFolderPath))
                {
                    ClearFolderDropTarget();
                    session.Route = ElevatedDragTargetRoute.Blocked;
                    return T("Widget.CannotMoveToFolder");
                }

                if (AreAllSourcesAlreadyInDestinationLexically(
                        session.SourcePaths,
                        childFolderPath))
                {
                    ClearFolderDropTarget();
                    session.Route = ElevatedDragTargetRoute.Blocked;
                    return T("Widget.DragCaption.CurrentWidget");
                }

                FileDropIntent folderIntent = ResolveElevatedInternalFolderIntent(
                    session.SourcePaths,
                    childFolderPath);
                if (folderIntent == FileDropIntent.None)
                {
                    ClearFolderDropTarget();
                    session.Route = ElevatedDragTargetRoute.Blocked;
                    return string.Empty;
                }

                SetFolderDropTarget(childBorder);
                session.Route = ElevatedDragTargetRoute.SameSurfaceFolderDrop;
                session.TargetChildItem = childTarget;
                canDrop = true;
                return FormatDropCaption(folderIntent, childTarget.Name);
            }

            if (childBehavior == ItemDropBehavior.RecycleBinDelete)
            {
                HideStackPopoverReorderIndicator();
                ClearLaunchDropTarget();
                SetFolderDropTarget(childBorder);
                session.Route = ElevatedDragTargetRoute.SameSurfaceRecycleBinDrop;
                session.TargetChildItem = childTarget;
                canDrop = true;
                return T("Widget.DropOnRecycleBinHint");
            }

            if (childBehavior == ItemDropBehavior.Launch &&
                !session.FromStackPopover)
            {
                HideStackPopoverReorderIndicator();
                ClearFolderDropTarget();
                SetLaunchDropTarget(childBorder);
                session.Route = ElevatedDragTargetRoute.SameSurfaceShortcutLaunch;
                session.TargetChildItem = childTarget;
                canDrop = true;
                return _localizationService.Format(
                    "Widget.DropOnShortcutOpenWith",
                    childTarget.Name);
            }
        }

        ClearFolderDropTarget();
        ClearLaunchDropTarget();

        if (session.FromStackPopover &&
            string.Equals(session.SourceStackKey, popoverKey, StringComparison.Ordinal))
        {
            if (TryGetScreenPointInElement(
                    popoverView,
                    popoverHost.WindowHandle,
                    screenX,
                    screenY,
                    out Windows.Foundation.Point pointInPopover))
            {
                _stackPopoverReorderInsertionIndex = ReorderDropIndexCalculator.Compute(
                    popoverView,
                    pointInPopover,
                    _stackPopoverReorderInsertionIndex);
                UpdateStackPopoverReorderIndicator(
                    popoverView,
                    pointInPopover,
                    _stackPopoverReorderInsertionIndex);
                session.Route = ElevatedDragTargetRoute.StackPopoverInternalReorder;
                session.TargetStack = openStack;
                canDrop = true;
                return T("Widget.DragCaption.Reorder");
            }

            HideStackPopoverReorderIndicator();
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

        // Dragging from main surface into an open stack popover joins that stack.
        if (TryResolveSameWidgetStackJoinItems(session.SourcePaths, openStack, out _))
        {
            if (TryGetScreenPointInElement(
                    popoverView,
                    popoverHost.WindowHandle,
                    screenX,
                    screenY,
                    out Windows.Foundation.Point externalPoint) &&
                popoverView.Items.Count > 0 &&
                ReorderDropIndexCalculator.IsPointerOverRealizedItem(
                    popoverView,
                    externalPoint))
            {
                _stackPopoverReorderInsertionIndex = ReorderDropIndexCalculator.Compute(
                    popoverView,
                    externalPoint,
                    _stackPopoverReorderInsertionIndex);
                if (_stackPopoverReorderInsertionIndex >= 0 &&
                    _stackPopoverReorderInsertionIndex < popoverView.Items.Count)
                {
                    UpdateStackPopoverReorderIndicator(
                        popoverView,
                        externalPoint,
                        _stackPopoverReorderInsertionIndex);
                }
                else
                {
                    HideStackPopoverReorderIndicator();
                }
            }
            else
            {
                HideStackPopoverReorderIndicator();
            }

            session.Route = ElevatedDragTargetRoute.StackPopoverJoinFromSurface;
            session.TargetStack = openStack;
            canDrop = true;
            return _localizationService.Format(
                "Widget.Stack.DragCaption.Add",
                openStack.Name);
        }

        HideStackPopoverReorderIndicator();
        session.Route = ElevatedDragTargetRoute.Blocked;
        return string.Empty;
    }

    private string EvaluateSameSurfaceHover(
        ElevatedPointerDragSession session,
        int screenX,
        int screenY,
        out bool canDrop)
    {
        canDrop = false;
        ListViewBase mainView = GetMainSurfaceItemsView();

        // Collapsed stack header reorder on main surface.
        if (session.IsStackHeader &&
            session.StackReorderKey is { Length: > 0 } stackKey)
        {
            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            ClearStackMemberDropTarget();
            if (!TryGetScreenPointInElement(
                    mainView,
                    _hostWindowHandle,
                    screenX,
                    screenY,
                    out Windows.Foundation.Point stackPoint))
            {
                PersistSurfaceReorder();
                session.Route = ElevatedDragTargetRoute.Blocked;
                return string.Empty;
            }

            BeginOrUpdateElevatedStackHeaderReorderPreview(stackKey, stackPoint);
            session.Route = ElevatedDragTargetRoute.SameSurfaceStackHeaderReorder;
            canDrop = true;
            return T("Widget.DragCaption.Reorder");
        }

        // Member dragged out of an open stack popover onto the parent widget.
        if (session.FromStackPopover)
        {
            PersistSurfaceReorder();

            if (FindHitStackSurfaceBorder(screenX, screenY, out WidgetStackItem? hitStack)
                is { } stackBorder &&
                hitStack is not null)
            {
                ClearFolderDropTarget();
                ClearLaunchDropTarget();
                if (string.Equals(
                        hitStack.StackKey,
                        session.SourceStackKey,
                        StringComparison.Ordinal))
                {
                    ClearStackMemberDropTarget();
                    session.Route = ElevatedDragTargetRoute.Blocked;
                    return T("Widget.DragCaption.CurrentWidget");
                }

                if (TryResolveSameWidgetStackJoinItems(
                        session.SourcePaths,
                        hitStack,
                        out _))
                {
                    SetStackMemberDropTarget(stackBorder);
                    ObserveStackTileSpringLoad(
                        hitStack,
                        isStackHeader: session.IsStackHeader,
                        sourceStackKey: session.SourceStackKey);
                    session.Route = ElevatedDragTargetRoute.SameSurfaceJoinStack;
                    session.TargetStack = hitStack;
                    canDrop = true;
                    return _localizationService.Format(
                        "Widget.Stack.DragCaption.Add",
                        hitStack.Name);
                }
            }

            CancelStackSpringLoadTimer();
            ClearStackMemberDropTarget();
            if (TryEvaluateSameSurfaceIconTargetHover(
                    session,
                    screenX,
                    screenY,
                    out canDrop,
                    out string iconCaption))
            {
                return iconCaption;
            }

            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            if (!string.IsNullOrWhiteSpace(ViewModel.CurrentFolderPath) &&
                !AreAllSourcesAlreadyInDestinationLexically(
                    session.SourcePaths,
                    ViewModel.CurrentFolderPath))
            {
                return EvaluateSameSurfaceCrossFolderAfterSpringBack(
                    session,
                    out canDrop);
            }

            if (TryGetScreenPointInElement(
                    mainView,
                    _hostWindowHandle,
                    screenX,
                    screenY,
                    out Windows.Foundation.Point detachPoint))
            {
                UpdateExternalDropPreview(session.SourcePaths, detachPoint);
            }

            session.Route = ElevatedDragTargetRoute.SameSurfaceDetachFromStack;
            canDrop = true;
            return T("Widget.Stack.RemoveItem");
        }

        // Main-surface file item(s) dragged over a collapsed stack tile.
        if (FindHitStackSurfaceBorder(screenX, screenY, out WidgetStackItem? targetStack)
            is { } targetStackBorder &&
            targetStack is not null &&
            TryResolveSameWidgetStackJoinItems(
                session.SourcePaths,
                targetStack,
                out _))
        {
            PersistSurfaceReorder();
            SetStackMemberDropTarget(targetStackBorder);
            ObserveStackTileSpringLoad(
                targetStack,
                isStackHeader: session.IsStackHeader,
                sourceStackKey: session.SourceStackKey);
            session.Route = ElevatedDragTargetRoute.SameSurfaceJoinStack;
            session.TargetStack = targetStack;
            canDrop = true;
            return _localizationService.Format(
                "Widget.Stack.DragCaption.Add",
                targetStack.Name);
        }

        CancelStackSpringLoadTimer();
        ClearStackMemberDropTarget();

        // Main-surface file item(s) dragged directly over another item's ICON
        // (folder import, recycle-bin delete, or application shortcut launch).
        if (TryEvaluateSameSurfaceIconTargetHover(
                session,
                screenX,
                screenY,
                out canDrop,
                out string mainIconCaption))
        {
            return mainIconCaption;
        }

        ClearFolderDropTarget();
        ClearLaunchDropTarget();

        if (TryResolveFolderNavigationDropTarget(screenX, screenY, out bool overNavUp) is { } navFolder)
        {
            PersistSurfaceReorder();
            if (overNavUp)
            {
                ObserveFolderNavigationSpringBack(hoveringUpButton: true);
            }
            else
            {
                _folderNavigationSpringBackTimer?.Stop();
                SetHighlightedFolderNavigationButton(FolderNavigationRootButton);
            }

            if (HasTransferConflict(session.SourcePaths, navFolder.Path) ||
                IsUnsafeFolderDrop(session.SourcePaths, navFolder.Path) ||
                AreAllSourcesAlreadyInDestinationLexically(session.SourcePaths, navFolder.Path))
            {
                session.Route = ElevatedDragTargetRoute.Blocked;
                return T("Widget.CannotMoveToFolder");
            }

            session.Route = ElevatedDragTargetRoute.SameSurfaceFolderDrop;
            session.TargetChildItem = navFolder;
            canDrop = true;
            FileDropIntent navIntent = Win32Helper.IsKeyPressed(VirtualKey.Menu)
                ? FileDropIntent.Shortcut
                : Win32Helper.IsKeyPressed(VirtualKey.Control)
                    ? FileDropIntent.Copy
                    : FileDropIntent.Move;
            return FormatDropCaption(navIntent, navFolder.Name);
        }

        CancelFolderNavigationSpringBack();

        if (!string.IsNullOrWhiteSpace(ViewModel.CurrentFolderPath) &&
            !AreAllSourcesAlreadyInDestinationLexically(
                session.SourcePaths,
                ViewModel.CurrentFolderPath))
        {
            PersistSurfaceReorder();
            return EvaluateSameSurfaceCrossFolderAfterSpringBack(
                session,
                out canDrop);
        }

        if (!TryGetScreenPointInElement(
                mainView,
                _hostWindowHandle,
                screenX,
                screenY,
                out Windows.Foundation.Point pointInMainView))
        {
            PersistSurfaceReorder();
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

        if (!BeginOrUpdateElevatedFileItemsReorderPreview(
                session.SourcePaths,
                pointInMainView))
        {
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

        session.Route = ElevatedDragTargetRoute.SameSurfaceItemsReorder;
        canDrop = true;
        return T("Widget.DragCaption.Reorder");
    }

    private string EvaluateSameSurfaceCrossFolderAfterSpringBack(
        ElevatedPointerDragSession session,
        out bool canDrop)
    {
        canDrop = false;
        string destinationFolder = ViewModel.CurrentFolderPath!;
        if (HasTransferConflict(session.SourcePaths, destinationFolder) ||
            IsUnsafeFolderDrop(session.SourcePaths, destinationFolder))
        {
            session.Route = ElevatedDragTargetRoute.Blocked;
            return T("Widget.CannotMoveToFolder");
        }

        FileDropIntent folderIntent = ResolveElevatedInternalFolderIntent(
            session.SourcePaths,
            destinationFolder);
        if (folderIntent == FileDropIntent.None)
        {
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

        string folderDisplayName = string.IsNullOrWhiteSpace(ViewModel.CurrentFolderDisplayName)
            ? Path.GetFileName(destinationFolder.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            : ViewModel.CurrentFolderDisplayName;
        session.Route = ElevatedDragTargetRoute.SameSurfaceFolderDrop;
        session.TargetChildItem = new WidgetItem
        {
            Name = folderDisplayName,
            Path = destinationFolder,
            IsFolder = true
        };
        canDrop = true;
        return FormatDropCaption(folderIntent, folderDisplayName);
    }

    private bool TryEvaluateSameSurfaceIconTargetHover(
        ElevatedPointerDragSession session,
        int screenX,
        int screenY,
        out bool canDrop,
        out string caption)
    {
        canDrop = false;
        caption = string.Empty;
        if (FindHitMainSurfaceItemBorderOverIcon(
                screenX,
                screenY,
                out WidgetItem? iconHitItem) is not { } iconBorder ||
            iconHitItem is null ||
            session.SourcePaths.Contains(
                iconHitItem.Path,
                StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        ItemDropBehavior behavior = ItemDropBehaviorPolicy.Resolve(iconHitItem);
        if (behavior == ItemDropBehavior.FolderImport &&
            ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                iconHitItem,
                out string iconFolderDestination))
        {
            PersistSurfaceReorder();
            ClearLaunchDropTarget();
            if (HasTransferConflict(session.SourcePaths, iconFolderDestination) ||
                IsUnsafeFolderDrop(session.SourcePaths, iconFolderDestination))
            {
                ClearFolderDropTarget();
                session.Route = ElevatedDragTargetRoute.Blocked;
                caption = T("Widget.CannotMoveToFolder");
                return true;
            }

            if (AreAllSourcesAlreadyInDestinationLexically(
                    session.SourcePaths,
                    iconFolderDestination))
            {
                ClearFolderDropTarget();
                session.Route = ElevatedDragTargetRoute.Blocked;
                caption = T("Widget.DragCaption.CurrentWidget");
                return true;
            }

            FileDropIntent folderIntent = ResolveElevatedInternalFolderIntent(
                session.SourcePaths,
                iconFolderDestination);
            if (folderIntent == FileDropIntent.None)
            {
                ClearFolderDropTarget();
                session.Route = ElevatedDragTargetRoute.Blocked;
                return true;
            }

            SetFolderDropTarget(iconBorder);
            session.Route = ElevatedDragTargetRoute.SameSurfaceFolderDrop;
            session.TargetChildItem = iconHitItem;
            canDrop = true;
            caption = FormatDropCaption(folderIntent, iconHitItem.Name);
            return true;
        }

        if (behavior == ItemDropBehavior.RecycleBinDelete)
        {
            PersistSurfaceReorder();
            ClearLaunchDropTarget();
            SetFolderDropTarget(iconBorder);
            session.Route = ElevatedDragTargetRoute.SameSurfaceRecycleBinDrop;
            session.TargetChildItem = iconHitItem;
            canDrop = true;
            caption = T("Widget.DropOnRecycleBinHint");
            return true;
        }

        if (behavior == ItemDropBehavior.Launch &&
            ShortcutLaunchPolicy.EvaluateInternalDrag(
                isDeskBoxFileDrag: true,
                isStackPopoverMemberDrag: session.FromStackPopover,
                session.SourcePaths,
                iconHitItem.Path,
                targetHostedInStackPopover: false) == ShortcutLaunchDecision.Launch)
        {
            PersistSurfaceReorder();
            ClearFolderDropTarget();
            SetLaunchDropTarget(iconBorder);
            session.Route = ElevatedDragTargetRoute.SameSurfaceShortcutLaunch;
            session.TargetChildItem = iconHitItem;
            canDrop = true;
            caption = _localizationService.Format(
                "Widget.DropOnShortcutOpenWith",
                iconHitItem.Name);
            return true;
        }

        return false;
    }

    private void BeginOrUpdateElevatedStackHeaderReorderPreview(
        string stackKey,
        Windows.Foundation.Point position)
    {
        if (!string.Equals(
                _surfaceReorderStackKey,
                stackKey,
                StringComparison.Ordinal))
        {
            _surfaceReorderDraggedItem = null;
            _surfaceReorderDraggedItems = [];
            _surfaceReorderPathSet = null;
            _surfaceReorderLastView = null;
        }

        _isSurfaceReorderDragActive = true;
        HideAllNameExpansions();
        _surfaceReorderStackKey = stackKey;
        _surfaceReorderPaths = [];
        UpdateSurfaceReorderPreview(position);
    }

    private bool BeginOrUpdateElevatedFileItemsReorderPreview(
        string[] paths,
        Windows.Foundation.Point position)
    {
        if (paths.Length == 0)
        {
            return false;
        }

        _surfaceReorderPathSet ??= paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WidgetItem[] draggedItems = _surfaceReorderDraggedItems.Length > 0
            ? _surfaceReorderDraggedItems
            : ResolveSurfaceReorderItems();
        if (draggedItems.Length == 0)
        {
            return false;
        }

        if (!_isSurfaceReorderDragActive)
        {
            if (ViewModel.UsesStackProjection)
            {
                if (!draggedItems.All(ViewModel.PrepareVisibleItemReorder))
                {
                    return false;
                }
            }
            else if (ViewModel.EffectiveSortMode != WidgetSortMode.Manual)
            {
                ViewModel.SetSortMode(WidgetSortMode.Manual);
            }

            _isSurfaceReorderDragActive = true;
            HideAllNameExpansions();
            _surfaceReorderPaths = paths;
            _surfaceReorderDraggedItem = draggedItems[0];
            _surfaceReorderDraggedItems = draggedItems;
        }

        UpdateSurfaceReorderPreview(position);
        return true;
    }

    private Border? FindHitStackSurfaceBorder(
        int screenX,
        int screenY,
        out WidgetStackItem? stack)
    {
        foreach (Border border in EnumerateRealizedStackSurfaces())
        {
            if (border.DataContext is WidgetStackItem { IsExpanded: false } candidate &&
                TryGetScreenPointInElement(
                    border,
                    _hostWindowHandle,
                    screenX,
                    screenY,
                    out _))
            {
                stack = candidate;
                return border;
            }
        }

        stack = null;
        return null;
    }

    private Border? FindHitMainSurfaceItemBorderOverIcon(
        int screenX,
        int screenY,
        out WidgetItem? item)
    {
        foreach (Border border in _itemSurfaces)
        {
            if (IsHostedInStackPopover(border) ||
                !TryGetScreenPointInElement(
                    border,
                    _hostWindowHandle,
                    screenX,
                    screenY,
                    out _))
            {
                continue;
            }

            FileItemSurface? owner = FileItemSurface.FindOwner(border);
            WidgetItem? candidate = owner?.DataContext as WidgetItem ??
                border.DataContext as WidgetItem;
            if (candidate is null or WidgetStackItem ||
                owner?.IconHitTestElement is not { } iconHost ||
                !TryGetScreenPointInElement(
                    iconHost,
                    _hostWindowHandle,
                    screenX,
                    screenY,
                    out Windows.Foundation.Point pointOnIcon))
            {
                continue;
            }

            if (ShortcutLaunchPolicy.IsPointInsideRectWithSlack(
                    0,
                    0,
                    iconHost.ActualWidth,
                    iconHost.ActualHeight,
                    LaunchIconSlackPixels,
                    pointOnIcon.X,
                    pointOnIcon.Y))
            {
                item = candidate;
                return border;
            }
        }

        item = null;
        return null;
    }

    private bool TryResolveSameWidgetStackJoinItems(
        IReadOnlyList<string> sourcePaths,
        WidgetStackItem targetStack,
        out WidgetItem[] items)
    {
        items = [];
        if (sourcePaths.Count == 0)
        {
            return false;
        }

        HashSet<string> targetMemberPaths = targetStack.Members
            .Where(member => !string.IsNullOrWhiteSpace(member.Path))
            .Select(member => Path.GetFullPath(member.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> requestedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        items = ViewModel.Items
            .Where(candidate =>
                candidate is not WidgetStackItem &&
                !string.IsNullOrWhiteSpace(candidate.Path) &&
                requestedPaths.Contains(Path.GetFullPath(candidate.Path)) &&
                !targetMemberPaths.Contains(Path.GetFullPath(candidate.Path)))
            .ToArray();
        return items.Length > 0;
    }

    private FileDropIntent ResolveElevatedInternalFolderIntent(
        IReadOnlyList<string> sourcePaths,
        string destinationFolderPath,
        bool? controlDown = null,
        bool? shiftDown = null,
        bool? altDown = null)
    {
        bool sameVolume = PathOriginClassifier.AreAllOnSameVolume(
            sourcePaths,
            destinationFolderPath,
            TimeSpan.FromMilliseconds(80));
        string action = _settingsService.Settings.FileWidget.ManagedDropAction;
        bool followWindows = string.Equals(
            action,
            SettingsService.ManagedDropActionFollowWindows,
            StringComparison.Ordinal);
        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            sourcePaths,
            TimeSpan.FromMilliseconds(80));
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: controlDown ?? Win32Helper.IsKeyPressed(VirtualKey.Control),
            ShiftDown: shiftDown ?? Win32Helper.IsKeyPressed(VirtualKey.Shift),
            AltDown: altDown ?? Win32Helper.IsKeyPressed(VirtualKey.Menu),
            AllowedEffects: NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link,
            NoOperationMetadata: false,
            DefaultMove: string.Equals(
                action,
                SettingsService.ManagedDropActionMove,
                StringComparison.Ordinal),
            FollowWindows: followWindows,
            SameVolume: sameVolume,
            CanLink: true,
            SourceOrigin: origin)).Intent;
    }

    internal static FileDropIntent ResolveElevatedOutboundDropIntent(
        string? managedDragOutAction,
        bool sameVolume,
        bool controlDown,
        bool shiftDown,
        bool altDown,
        IEnumerable<string>? sourcePaths = null)
    {
        string[] materializedPaths = sourcePaths?
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray() ?? [];
        SourceOriginSnapshot origin = materializedPaths.Length > 0
            ? PathOriginClassifier.ClassifySources(materializedPaths, TimeSpan.FromMilliseconds(80))
            : SourceOriginSnapshot.Empty;
        bool protectedCopyOnly =
            DriveMediaInterop.IsProtectedDragOutSource(materializedPaths) ||
            origin.IsMoveProtectedSystemOrSharedSource;
        bool followWindows =
            string.IsNullOrWhiteSpace(managedDragOutAction) ||
            string.Equals(
                managedDragOutAction,
                SettingsService.ManagedDragOutActionFollowWindows,
                StringComparison.Ordinal);
        bool defaultMove = !protectedCopyOnly &&
            string.Equals(
                managedDragOutAction,
                SettingsService.ManagedDragOutActionMove,
                StringComparison.Ordinal);
        return FileDropIntentPolicy.ResolveMappedTransfer(
            hasMappedFolder: true,
            forceCopy: false,
            controlDown: controlDown,
            shiftDown: shiftDown,
            defaultMove: defaultMove,
            canCopy: true,
            canMove: !protectedCopyOnly,
            altDown: altDown,
            followWindows: followWindows,
            sameVolume: sameVolume,
            protectDefaultMoveAsCopy: origin.IsProtectedCopyByDefault,
            canLink: true,
            sourceOrigin: origin,
            domain: TransferDomain.Outbound);
    }

    private ContentWidgetWindow? FindTargetContentWidgetWindow(
        IntPtr rootHwnd,
        int screenX,
        int screenY)
    {
        if (rootHwnd == IntPtr.Zero ||
            App.Current?.WidgetManager is not { } manager)
        {
            return null;
        }

        bool isOwnHostWindow = IsLoaded && rootHwnd == _hostWindowHandle;
        foreach (ContentWidgetWindow candidate in manager.ContentWidgets.Values)
        {
            if (candidate.WindowHandle != rootHwnd ||
                !candidate.CanObserveElevatedPointerDrag)
            {
                continue;
            }

            if (isOwnHostWindow ||
                string.Equals(candidate.CurrentContent?.WidgetId, WidgetId, StringComparison.Ordinal))
            {
                if (candidate.HitTestInactiveGroupTitleMemberId(screenX, screenY) is not null)
                {
                    return candidate;
                }

                continue;
            }

            return candidate;
        }

        return null;
    }

    private ListViewBase GetMainSurfaceItemsView() =>
        ViewModel.IconViewVisibility == Visibility.Visible
            ? ItemsGrid
            : ItemsList;

    private static void ClearHoveredOtherWindow(ElevatedPointerDragSession session)
    {
        if (session.HoveredOtherWindow is { } previous)
        {
            session.HoveredOtherWindow = null;
            previous.LeaveElevatedPointerDrag();
        }
    }

    private static void ClearHoveredOtherStackPopover(ElevatedPointerDragSession session)
    {
        if (session.HoveredOtherStackPopoverOwner is { } previous)
        {
            session.HoveredOtherStackPopoverOwner = null;
            previous.LeaveCrossWidgetElevatedStackPopoverDrag();
        }
    }

    private void ClearSameSurfaceHoverVisuals(bool keepStackPopoverIndicator)
    {
        CancelStackSpringLoadTimer();
        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        ClearStackMemberDropTarget();
        PersistSurfaceReorder();
        if (!keepStackPopoverIndicator)
        {
            HideStackPopoverReorderIndicator();
        }
    }

    private void CancelElevatedPointerDrag()
    {
        _elevatedDragCandidate = null;
        if (_elevatedDragSession is not { } session)
        {
            return;
        }

        _elevatedDragSession = null;
        StopElevatedDragWatchTimer();
        _elevatedDragGhostWindow?.Hide();
        ClearHoveredOtherStackPopover(session);
        ClearHoveredOtherWindow(session);
        ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
        if (session.FromStackPopover)
        {
            CompleteStackPopoverDrag();
        }
        else if (_stackPopoverOpenedBySpringLoad && _stackPopoverPopupOpen)
        {
            HideStackPopoverForReuse(restoreOwnerForeground: false);
        }

        ReleaseElevatedSessionResources(session, releasePointer: null);

        App.Log(
            $"[DragProtocol] stage=ElevatedPointerDragCancelled widget={WidgetId} " +
            $"session={FormatDragSessionId(session.SessionId)}");
    }

    private void CompleteElevatedPointerDragOnRelease(Pointer? pointer)
    {
        if (_elevatedDragSession is not { } session)
        {
            return;
        }

        session.LastCtrlDown = Win32Helper.IsKeyPressed(VirtualKey.Control);
        session.LastShiftDown = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        session.LastAltDown = Win32Helper.IsKeyPressed(VirtualKey.Menu);
        if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            session.LastScreenX = cursor.X;
            session.LastScreenY = cursor.Y;
            session.HasLastScreenPoint = true;
            _ = EvaluateElevatedPointerDragTarget(
                session,
                cursor.X,
                cursor.Y,
                out _);
        }

        _elevatedDragSession = null;
        StopElevatedDragWatchTimer();
        _elevatedDragGhostWindow?.Hide();

        int releaseX = session.LastScreenX;
        int releaseY = session.LastScreenY;
        ElevatedDragTargetRoute route = session.Route;

        // Release capture and hide ghost BEFORE running commit mutations or
        // async Shell dialogs so no modal window can ever contend with capture.
        if (session.PointerCaptured)
        {
            session.PointerCaptured = false;
            if (pointer is not null)
            {
                try
                {
                    session.CaptureHost.ReleasePointerCapture(pointer);
                }
                catch
                {
                    // Window or element may have detached concurrently.
                }
            }
        }

        try
        {
            switch (route)
            {
                case ElevatedDragTargetRoute.SameSurfaceStackHeaderReorder:
                case ElevatedDragTargetRoute.SameSurfaceItemsReorder:
                    CommitElevatedSameSurfaceReorder(releaseX, releaseY);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.SameSurfaceJoinStack:
                    CommitElevatedSameSurfaceJoinStack(session);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.SameSurfaceDetachFromStack:
                    CommitElevatedSameSurfaceDetachFromStack(session, releaseX, releaseY);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.StackPopoverInternalReorder:
                    CommitElevatedStackPopoverInternalReorder(session, releaseX, releaseY);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.StackPopoverJoinFromSurface:
                    CommitElevatedStackPopoverJoinFromSurface(session, releaseX, releaseY);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.SameSurfaceShortcutLaunch:
                    CommitElevatedSameSurfaceShortcutLaunch(session);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;

                case ElevatedDragTargetRoute.SameSurfaceFolderDrop:
                case ElevatedDragTargetRoute.SameSurfaceRecycleBinDrop:
                case ElevatedDragTargetRoute.OtherWidgetStackPopover:
                case ElevatedDragTargetRoute.OtherWidgetWindow:
                case ElevatedDragTargetRoute.OutboundExternal:
                    QueueElevatedAsyncDropCommit(session, route, releaseX, releaseY);
                    break;

                default:
                    ClearHoveredOtherStackPopover(session);
                    ClearHoveredOtherWindow(session);
                    ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;
            }
        }
        catch (Exception ex)
        {
            ClearHoveredOtherStackPopover(session);
            ClearHoveredOtherWindow(session);
            ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
            FinalizeElevatedSessionAfterSyncCommit(session);
            App.Log(
                $"[WidgetSurface] Elevated pointer drag completion failed " +
                $"id={WidgetId}: {ex}");
        }
    }

    private void CommitElevatedSameSurfaceReorder(int releaseX, int releaseY)
    {
        ListViewBase mainView = GetMainSurfaceItemsView();
        if (TryGetScreenPointInElement(
                mainView,
                _hostWindowHandle,
                releaseX,
                releaseY,
                out Windows.Foundation.Point position))
        {
            CommitSurfaceReorder(position);
        }
        else if (_surfaceReorderHasLastPosition)
        {
            CommitSurfaceReorder(_surfaceReorderLastPosition);
        }
        else
        {
            PersistSurfaceReorder();
        }
    }

    private void CommitElevatedSameSurfaceJoinStack(ElevatedPointerDragSession session)
    {
        ClearStackMemberDropTarget();
        PersistSurfaceReorder();
        if (session.TargetStack is not { } stack ||
            !TryResolveSameWidgetStackJoinItems(
                session.SourcePaths,
                stack,
                out WidgetItem[] items))
        {
            return;
        }

        string targetStackKey = stack.StackKey;
        string[] targetAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        bool added = false;
        string resolvedStackKey = targetStackKey;
        if (session.FromStackPopover)
        {
            ApplyStackProjectionChange(() =>
                added = ViewModel.AddItemsToStack(
                    targetStackKey,
                    items,
                    out resolvedStackKey));
        }
        else
        {
            added = ViewModel.AddItemsToStack(
                targetStackKey,
                items,
                out resolvedStackKey);
        }

        if (added)
        {
            if (string.Equals(_stackPopoverKey, targetStackKey, StringComparison.Ordinal))
            {
                _stackPopoverKey = resolvedStackKey;
            }
            targetStackKey = resolvedStackKey;
            ClearSelection();
            QueueStackPopoverReconciliation(targetStackKey, targetAnchors);
            if (session.FromStackPopover)
            {
                CloseStackPopover();
            }
        }
    }

    private void CommitElevatedSameSurfaceDetachFromStack(
        ElevatedPointerDragSession session,
        int releaseX,
        int releaseY)
    {
        ListViewBase mainView = GetMainSurfaceItemsView();
        int detachInsertionIndex = TryGetScreenPointInElement(
                mainView,
                _hostWindowHandle,
                releaseX,
                releaseY,
                out Windows.Foundation.Point releasePoint)
            ? ReorderDropIndexCalculator.Compute(
                mainView,
                releasePoint,
                _externalDropInsertionIndex)
            : _externalDropInsertionIndex;
        ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
        ResetExternalDropPreview();
        if (session.SourceStackKey is not { Length: > 0 } sourceStackKey ||
            !TryGetStackPopoverDragItems(
                sourceStackKey,
                session.SourcePaths,
                out WidgetStackItem sourceStack,
                out WidgetItem[] detachItems))
        {
            return;
        }

        bool removed = false;
        ApplyStackProjectionChange(() =>
        {
            removed = ViewModel.RemoveItemsFromStack(
                sourceStack.StackKey,
                detachItems);
            if (removed && detachInsertionIndex >= 0)
            {
                for (int i = 0; i < detachItems.Length; i++)
                {
                    ViewModel.MoveVisibleItemForReorder(
                        detachItems[i],
                        detachInsertionIndex + i);
                }
            }
        });
        if (removed)
        {
            CloseStackPopover();
        }
    }

    private void CommitElevatedStackPopoverInternalReorder(
        ElevatedPointerDragSession session,
        int releaseX,
        int releaseY)
    {
        if (_stackPopoverItemsView is not { } view ||
            _stackPopoverHostWindow is not { IsVisible: true } host ||
            session.SourceStackKey is not { Length: > 0 } stackKey ||
            !TryGetStackPopoverDragItems(
                stackKey,
                session.SourcePaths,
                out WidgetStackItem sourceStack,
                out WidgetItem[] items))
        {
            HideStackPopoverReorderIndicator();
            return;
        }

        int insertionIndex = TryGetScreenPointInElement(
                view,
                host.WindowHandle,
                releaseX,
                releaseY,
                out Windows.Foundation.Point position)
            ? ResolveStackPopoverMemberInsertionIndex(view, position)
            : ResolveStackPopoverMemberInsertionIndex(
                view,
                _stackPopoverReorderInsertionIndex);
        bool reordered = ReorderStackPopoverMembers(
            sourceStack,
            items,
            insertionIndex);
        HideStackPopoverReorderIndicator();
        if (!reordered)
        {
            QueueStackPopoverReconciliation();
        }
    }

    private void CommitElevatedStackPopoverJoinFromSurface(
        ElevatedPointerDragSession session,
        int releaseX,
        int releaseY)
    {
        int? preferredMemberIndex = null;
        if (_stackPopoverItemsView is { } popoverView &&
            _stackPopoverHostWindow is { IsVisible: true } host &&
            _stackPopoverReorderInsertionIndex >= 0 &&
            _stackPopoverReorderInsertionIndex < popoverView.Items.Count &&
            TryGetScreenPointInElement(
                popoverView,
                host.WindowHandle,
                releaseX,
                releaseY,
                out Windows.Foundation.Point position))
        {
            preferredMemberIndex = ResolveStackPopoverMemberInsertionIndex(
                popoverView,
                position);
        }

        HideStackPopoverReorderIndicator();
        if (session.TargetStack is not { } stack ||
            !TryResolveSameWidgetStackJoinItems(
                session.SourcePaths,
                stack,
                out WidgetItem[] items))
        {
            return;
        }

        string targetStackKey = stack.StackKey;
        string[] targetAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        bool added = ViewModel.AddItemsToStack(
            targetStackKey,
            items,
            out string resolvedStackKey);
        if (added)
        {
            if (string.Equals(_stackPopoverKey, targetStackKey, StringComparison.Ordinal))
            {
                _stackPopoverKey = resolvedStackKey;
            }
            targetStackKey = resolvedStackKey;
            if (preferredMemberIndex is { } memberIndex)
            {
                ApplyImportedStackMemberInsertion(
                    ViewModel.FindStackByKey(targetStackKey) ?? stack,
                    items,
                    memberIndex);
            }

            ClearSelection();
            QueueStackPopoverReconciliation(targetStackKey, targetAnchors);
        }
    }

    private void CommitElevatedSameSurfaceShortcutLaunch(
        ElevatedPointerDragSession session)
    {
        ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
        if (session.TargetChildItem is not { Path.Length: > 0 } shortcut ||
            session.SourcePaths.Length == 0)
        {
            return;
        }

        bool launched = ShortcutFileLauncher.TryLaunchWithFiles(
            shortcut.Path,
            session.SourcePaths);
        MarkNativeLaunchConsumed();
        if (!launched)
        {
            ShowShortcutLaunchRefusedFeedback(shortcut.Name);
        }
    }

    private void QueueElevatedAsyncDropCommit(
        ElevatedPointerDragSession session,
        ElevatedDragTargetRoute route,
        int releaseX,
        int releaseY)
    {
        // Clear same-surface hover chrome immediately while keeping
        // ActiveDeskBoxDragRegistry alive across the async transfer so target
        // widgets recognize our own DeskBox-sourced move semantics.
        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        ClearStackMemberDropTarget();
        PersistSurfaceReorder();
        HideStackPopoverReorderIndicator();

        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await ExecuteElevatedAsyncDropCommitAsync(
                        session,
                        route,
                        releaseX,
                        releaseY);
                }
                catch (Exception ex)
                {
                    App.Log(
                        $"[WidgetSurface] Elevated async drop failed " +
                        $"id={WidgetId} route={route}: {ex}");
                }
                finally
                {
                    ClearHoveredOtherWindow(session);
                    ReleaseElevatedSessionResources(session, releasePointer: null);
                }
            }))
        {
            ClearHoveredOtherWindow(session);
            ReleaseElevatedSessionResources(session, releasePointer: null);
        }
    }

    private async Task ExecuteElevatedAsyncDropCommitAsync(
        ElevatedPointerDragSession session,
        ElevatedDragTargetRoute route,
        int releaseX,
        int releaseY)
    {
        if (_isDisposed || session.SourcePaths.Length == 0)
        {
            return;
        }

        switch (route)
        {
            case ElevatedDragTargetRoute.SameSurfaceFolderDrop:
                if (session.TargetChildItem is { } folder &&
                    ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                        folder,
                        out string folderDestination))
                {
                    FileDropIntent intent = ResolveElevatedInternalFolderIntent(
                        session.SourcePaths,
                        folderDestination,
                        session.LastCtrlDown,
                        session.LastShiftDown,
                        session.LastAltDown);
                    if (intent != FileDropIntent.None)
                    {
                        DroppedFilePath[] dropped = session.SourcePaths
                            .Select(path => new DroppedFilePath(
                                path,
                                Path.GetFileName(path),
                                ForceManagedCopy: false))
                            .ToArray();
                        bool imported = await ImportNativeDroppedFilesIntoFolderAsync(
                            dropped,
                            folder,
                            move: intent == FileDropIntent.Move,
                            intentOverride: intent == FileDropIntent.Shortcut
                                ? FileDropIntent.Shortcut
                                : null);
                        if (imported && intent == FileDropIntent.Move)
                        {
                            await ReconcileElevatedMovedOutPathsAsync(
                                session.SourcePaths,
                                session.FromStackPopover);
                        }
                    }
                }
                break;

            case ElevatedDragTargetRoute.SameSurfaceRecycleBinDrop:
                if (session.TargetChildItem is { } binTile)
                {
                    await DeleteDroppedFilesForBinAsync(
                        session.SourcePaths,
                        binTile.Path);
                    await ReconcileElevatedMovedOutPathsAsync(
                        session.SourcePaths,
                        session.FromStackPopover);
                }
                break;

            case ElevatedDragTargetRoute.OtherWidgetStackPopover:
                if (session.HoveredOtherStackPopoverOwner is { } popoverOwner)
                {
                    session.HoveredOtherStackPopoverOwner = null;
                    bool popoverAccepted =
                        await popoverOwner.AcceptCrossWidgetElevatedStackPopoverDropAsync(
                            session.SourcePaths,
                            releaseX,
                            releaseY,
                            session.LastCtrlDown,
                            session.LastShiftDown,
                            session.LastAltDown);
                    if (popoverAccepted)
                    {
                        await ReconcileElevatedMovedOutPathsAsync(
                            session.SourcePaths,
                            session.FromStackPopover);
                    }
                }
                break;

            case ElevatedDragTargetRoute.OtherWidgetWindow:
                if (session.HoveredOtherWindow is { } targetWindow)
                {
                    session.HoveredOtherWindow = null;
                    bool accepted = await targetWindow.AcceptElevatedPointerDropAsync(
                        session.SourcePaths,
                        releaseX,
                        releaseY,
                        session.LastCtrlDown,
                        session.LastShiftDown,
                        session.LastAltDown);
                    if (accepted)
                    {
                        await ReconcileElevatedMovedOutPathsAsync(
                            session.SourcePaths,
                            session.FromStackPopover);
                    }
                }
                break;

            case ElevatedDragTargetRoute.OutboundExternal:
                bool outboundMoved = await ExecuteElevatedOutboundDropAsync(
                    session,
                    releaseX,
                    releaseY);
                if (outboundMoved)
                {
                    await ReconcileElevatedMovedOutPathsAsync(
                        session.SourcePaths,
                        session.FromStackPopover);
                }
                break;
        }
    }

    private async Task ReconcileElevatedMovedOutPathsAsync(
        IReadOnlyCollection<string> candidatePaths,
        bool fromStackPopover)
    {
        if (_isDisposed || candidatePaths.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> missingPaths =
            await ViewModel.GetConfirmedMissingPathsAsync(candidatePaths);
        string[] stillMissing = missingPaths
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .ToArray();
        if (stillMissing.Length > 0)
        {
            await ViewModel.HandleItemsMovedOutAsync(stillMissing);
            await ViewModel.RefreshFolderContentsAsync();
            UpdateEmptyState();
            if (fromStackPopover)
            {
                QueueStackPopoverReconciliation();
            }
        }
    }

    private void FinalizeElevatedSessionAfterSyncCommit(
        ElevatedPointerDragSession session)
    {
        ReleaseElevatedSessionResources(session, releasePointer: null);
        App.Log(
            $"[DragProtocol] stage=ElevatedPointerDragCompleted widget={WidgetId} " +
            $"session={FormatDragSessionId(session.SessionId)} route={session.Route}");
    }

    private void ReleaseElevatedSessionResources(
        ElevatedPointerDragSession session,
        Pointer? releasePointer)
    {
        if (session.PointerCaptured && releasePointer is not null)
        {
            session.PointerCaptured = false;
            try
            {
                session.CaptureHost.ReleasePointerCapture(releasePointer);
            }
            catch
            {
            }
        }

        if (session.SourcePaths.Length > 0)
        {
            ActiveDeskBoxDragRegistry.End(session.SessionId);
        }

        ResetDragPayloadCache();
        _pressedStack = null;
        _stackPointerDragStarted = false;
        _stackInputActivation.CancelPointer();

        if (session.FromStackPopover)
        {
            CompleteStackPopoverDrag();
        }

        if (session.RestoreOriginItemClickEnabled)
        {
            ListViewBase viewToRestore = session.OriginView;
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_isDisposed)
                    {
                        viewToRestore.IsItemClickEnabled = true;
                    }
                }))
            {
                viewToRestore.IsItemClickEnabled = true;
            }
        }

        App.Current?.WidgetManager?.EndWidgetInteraction(
            "elevated-pointer-drag-completed");
        NotifyOutboundDragCompletedIfIdle();
    }

    private string ObserveElevatedOutboundTarget(
        ElevatedPointerDragSession session,
        IntPtr hitHwnd,
        IntPtr rootHwnd,
        int screenX,
        int screenY,
        IReadOnlyList<string> sourcePaths,
        out bool canDrop)
    {
        _ = screenX;
        _ = screenY;
        canDrop = false;

        ElevatedOutboundTargetInfo target = ResolveCachedOutboundTarget(
            session,
            hitHwnd,
            rootHwnd);
        switch (target.Kind)
        {
            case ElevatedOutboundTargetKind.Desktop:
            case ElevatedOutboundTargetKind.ExplorerFolder:
            {
                string destinationFolder = target.DestinationFolderPath;
                if (string.IsNullOrWhiteSpace(destinationFolder))
                {
                    return string.Empty;
                }

                if (HasTransferConflict(sourcePaths, destinationFolder) ||
                    IsUnsafeFolderDrop(sourcePaths, destinationFolder))
                {
                    return T("Widget.CannotMoveToFolder");
                }

                if (AreAllSourcesAlreadyInDestinationLexically(
                        sourcePaths,
                        destinationFolder))
                {
                    return T("Widget.DragCaption.CurrentWidget");
                }

                bool sameVolume = FileDropIntentPolicy.AreAllOnSameVolume(
                    sourcePaths,
                    destinationFolder);
                FileDropIntent intent = ResolveElevatedOutboundDropIntent(
                    _settingsService.Settings.FileWidget.ManagedDragOutAction,
                    sameVolume,
                    Win32Helper.IsKeyPressed(VirtualKey.Control),
                    Win32Helper.IsKeyPressed(VirtualKey.Shift),
                    Win32Helper.IsKeyPressed(VirtualKey.Menu),
                    sourcePaths);
                if (intent == FileDropIntent.None)
                {
                    return string.Empty;
                }

                canDrop = true;
                return FormatDropCaption(intent, GetOutboundTargetDisplayName(target));
            }

            case ElevatedOutboundTargetKind.UnsupportedShellNamespace:
                return T("Widget.ElevatedDragOut.UnsupportedShellTarget");

            case ElevatedOutboundTargetKind.ForeignWindow:
                canDrop = true;
                return T("Widget.ElevatedDragOut.BridgeClipboardCaption");

            default:
                return string.Empty;
        }
    }

    private const long OutboundPositiveCacheTtlMs = 400;
    private const long OutboundNegativeCacheTtlMs = 500;

    private ElevatedOutboundTargetInfo ResolveCachedOutboundTarget(
        ElevatedPointerDragSession session,
        IntPtr hitHwnd,
        IntPtr rootHwnd)
    {
        if (ElevatedShellOutboundDropInterop.TryClassifyImmediateOutboundTarget(
                hitHwnd,
                rootHwnd,
                out IntPtr resolvedRootHwnd,
                out IntPtr tabKey,
                out ElevatedOutboundTargetInfo immediate))
        {
            session.CachedOutboundRootHwnd = resolvedRootHwnd;
            session.CachedOutboundTabHwnd = IntPtr.Zero;
            session.CachedOutboundResolvedTickMs = Environment.TickCount64;
            session.CachedOutboundTarget = immediate;
            return immediate;
        }

        long nowMs = Environment.TickCount64;
        bool hasSameTabCache =
            session.CachedOutboundRootHwnd == resolvedRootHwnd &&
            session.CachedOutboundTabHwnd == tabKey &&
            session.CachedOutboundResolvedTickMs != 0;
        if (hasSameTabCache)
        {
            long ttlMs = session.CachedOutboundTarget.Kind == ElevatedOutboundTargetKind.ExplorerFolder
                ? OutboundPositiveCacheTtlMs
                : OutboundNegativeCacheTtlMs;
            if (nowMs - session.CachedOutboundResolvedTickMs < ttlMs)
            {
                return session.CachedOutboundTarget;
            }
        }

        ElevatedShellOutboundDropInterop.RequestExplorerWindowTargetProbe(
            resolvedRootHwnd,
            tabKey,
            (probedRoot, probedTab, resolved) =>
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isDisposed || !ReferenceEquals(_elevatedDragSession, session))
                    {
                        return;
                    }

                    session.CachedOutboundRootHwnd = probedRoot;
                    session.CachedOutboundTabHwnd = probedTab;
                    session.CachedOutboundResolvedTickMs = Environment.TickCount64;
                    session.CachedOutboundTarget = resolved;

                    if (session.LastOutboundRootHwnd == probedRoot)
                    {
                        UpdateActiveElevatedPointerDragFromCursor();
                    }
                });
            });

        return hasSameTabCache
            ? session.CachedOutboundTarget
            : new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.None,
                string.Empty,
                string.Empty);
    }

    private string GetOutboundTargetDisplayName(ElevatedOutboundTargetInfo target)
    {
        if (target.Kind == ElevatedOutboundTargetKind.Desktop ||
            IsUserDesktopFolder(target.DestinationFolderPath))
        {
            string localizedDesktop = T("Common.Desktop");
            if (!string.IsNullOrWhiteSpace(localizedDesktop) &&
                !string.Equals(localizedDesktop, "Common.Desktop", StringComparison.Ordinal))
            {
                return localizedDesktop;
            }
        }

        return !string.IsNullOrWhiteSpace(target.DisplayName)
            ? target.DisplayName
            : target.DestinationFolderPath;
    }

    private static bool IsUserDesktopFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        try
        {
            string desktopPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktopPath))
            {
                return false;
            }

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(desktopPath)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ExecuteElevatedOutboundDropAsync(
        ElevatedPointerDragSession session,
        int screenX,
        int screenY)
    {
        string[] validPaths = session.SourcePaths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (validPaths.Length == 0 || session.LastOutboundRootHwnd == IntPtr.Zero)
        {
            return false;
        }

        ElevatedOutboundTargetInfo target = await Task.Run(() =>
            ElevatedShellOutboundDropInterop.ResolveOutboundTarget(
                session.LastOutboundHitHwnd,
                session.LastOutboundRootHwnd,
                explorerQueryTimeoutMs: 1500));
        if (target.Kind == ElevatedOutboundTargetKind.UnsupportedShellNamespace &&
            session.CachedOutboundRootHwnd == session.LastOutboundRootHwnd &&
            session.CachedOutboundTarget.Kind == ElevatedOutboundTargetKind.ExplorerFolder)
        {
            target = session.CachedOutboundTarget;
        }

        switch (target.Kind)
        {
            case ElevatedOutboundTargetKind.Desktop:
            case ElevatedOutboundTargetKind.ExplorerFolder:
                return await ExecuteElevatedShellFolderDropAsync(
                    session,
                    target,
                    validPaths,
                    screenX,
                    screenY);

            case ElevatedOutboundTargetKind.ForeignWindow:
                await StageElevatedOutboundClipboardAsync(validPaths);
                return false;

            case ElevatedOutboundTargetKind.UnsupportedShellNamespace:
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.ElevatedDragOut.UnsupportedShellTarget"),
                    WidgetFeedbackSeverity.Warning,
                    "elevated-outbound-unsupported-shell"));
                return false;

            default:
                return false;
        }
    }

    private async Task<bool> ExecuteElevatedShellFolderDropAsync(
        ElevatedPointerDragSession session,
        ElevatedOutboundTargetInfo target,
        string[] validPaths,
        int screenX,
        int screenY)
    {
        string destinationFolder = target.DestinationFolderPath;
        if (string.IsNullOrWhiteSpace(destinationFolder) ||
            !Directory.Exists(destinationFolder))
        {
            return false;
        }

        if (HasTransferConflict(validPaths, destinationFolder))
        {
            FileTransferPathState destState =
                _fileService.TransferSessions.GetState(destinationFolder);
            ShowTransferBlockedFeedback(
                destState.IsActive
                    ? destState
                    : _fileService.TransferSessions.GetState(validPaths[0]));
            return false;
        }

        if (IsUnsafeFolderDrop(validPaths, destinationFolder) ||
            await Task.Run(() => FileService.IsUnsafeDirectoryTransfer(
                validPaths,
                destinationFolder)))
        {
            ShowFeedback(new WidgetFeedbackRequest(
                T("Widget.CannotMoveToFolder"),
                WidgetFeedbackSeverity.Warning,
                "elevated-outbound-unsafe-folder"));
            return false;
        }

        if (await AreAllSourcesAlreadyInDestinationResolvedAsync(
                validPaths,
                destinationFolder))
        {
            ShowSameDirectoryDropFeedback();
            return false;
        }

        bool sameVolume = FileDropIntentPolicy.AreAllOnSameVolume(
            validPaths,
            destinationFolder);
        FileDropIntent intent = ResolveElevatedOutboundDropIntent(
            _settingsService.Settings.FileWidget.ManagedDragOutAction,
            sameVolume,
            session.LastCtrlDown,
            session.LastShiftDown,
            session.LastAltDown,
            validPaths);
        if (intent == FileDropIntent.None)
        {
            return false;
        }

        bool isDesktop = target.Kind == ElevatedOutboundTargetKind.Desktop ||
            IsUserDesktopFolder(destinationFolder);
        bool move = intent == FileDropIntent.Move;
        bool createShortcuts = intent == FileDropIntent.Shortcut;

        if (isDesktop)
        {
            SuppressDesktopDragOutArrivals(validPaths);
        }

        BeginTrackedImport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        App.Log(
            $"[DragProtocol] stage=ElevatedOutboundTransferStart widget={WidgetId} " +
            $"kind={target.Kind} target='{destinationFolder}' " +
            $"intent={intent} count={validPaths.Length}");
        try
        {
            EnsureTrackedImportStarted();
            IProgress<FileService.FileTransferProgress> progress =
                new CallbackProgress<FileService.FileTransferProgress>(
                    ReportImportProgress);
            var results = new List<FileService.FileTransferResult>();

            if (createShortcuts)
            {
                DroppedFilePath[] shortcutSources = validPaths
                    .Select(path => new DroppedFilePath(
                        path,
                        Path.GetFileName(path),
                        ForceManagedCopy: false))
                    .ToArray();
                IReadOnlyList<string> createdShortcuts =
                    await CreateShortcutFilesAsync(
                        shortcutSources,
                        destinationFolder,
                        T("Widget.CreateShortcut"),
                        ActiveImportCancellationToken);
                if (isDesktop && createdShortcuts.Count > 0)
                {
                    SuppressDesktopDragOutArrivals(createdShortcuts);
                }

                results.AddRange(createdShortcuts.Select(path =>
                    new FileService.FileTransferResult(path, path)));
            }
            else
            {
                results.AddRange(await _fileService.TransferItemsWithResultAsync(
                    validPaths,
                    destinationFolder,
                    move,
                    progress,
                    ActiveImportCancellationToken,
                    useShellProgress: true,
                    ownerWindowHandle: _hostWindowHandle));
            }

            if (move)
            {
                _cutClipboardPaths = [];
                ApplyCutState();
                (string SourcePath, string DestinationPath)[] movedPairs = results
                    .Where(static item =>
                        !string.IsNullOrWhiteSpace(item.SourcePath) &&
                        !string.IsNullOrWhiteSpace(item.DestinationPath))
                    .Select(static item => (item.SourcePath, item.DestinationPath))
                    .ToArray();
                if (movedPairs.Length > 0)
                {
                    _ = await Task.Run(() => ShortcutHelper.RebaseMovedShortcuts(movedPairs));
                }
            }

            string[] createdDestinations = results
                .Select(item => item.DestinationPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (isDesktop && createdDestinations.Length > 0)
            {
                SuppressDesktopDragOutArrivals(createdDestinations);
                _ = await Task.Run(() =>
                    ElevatedShellOutboundDropInterop.TryPositionDesktopItemsNearCursor(
                        createdDestinations,
                        screenX,
                        screenY));
            }

            if (results.Count > 0)
            {
                string displayName = GetOutboundTargetDisplayName(target);
                ShowFeedback(new WidgetFeedbackRequest(
                    _localizationService.Format(
                        move
                            ? "Widget.MovedToFolder"
                            : "Widget.CopiedToFolder",
                        displayName,
                        results.Count),
                    WidgetFeedbackSeverity.Success,
                    move
                        ? "elevated-outbound-move"
                        : "elevated-outbound-copy"));
            }

            await CompleteTrackedImportAsync(ImportCompletionState.Completed);
            App.Log(
                $"[DragProtocol] stage=ElevatedOutboundTransferCompleted widget={WidgetId} " +
                $"kind={target.Kind} count={results.Count} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return move && results.Count > 0;
        }
        catch (OperationCanceledException)
        {
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(ImportCompletionState.Canceled);
            }

            App.Log(
                $"[DragProtocol] stage=ElevatedOutboundTransferCanceled widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return move;
        }
        catch (Exception ex)
        {
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(ImportCompletionState.Failed);
            }

            App.Log(
                $"[WidgetSurface] Elevated outbound folder drop failed id={WidgetId} " +
                $"target='{destinationFolder}': {ex}");
            ShowFeedback(new WidgetFeedbackRequest(
                DescribeImportFailure(
                    ex,
                    validPaths.Length,
                    validPaths.Length == 1 ? validPaths[0] : null),
                WidgetFeedbackSeverity.Error,
                "elevated-outbound-error"));
            return move &&
                ex is FileService.IFileTransferWithCompletedResults
                {
                    CompletedResults.Count: > 0,
                };
        }
        finally
        {
            if (_activeImportCancellation is not null)
            {
                CancelAndResetTrackedImport();
            }
        }
    }

    private async Task StageElevatedOutboundClipboardAsync(string[] validPaths)
    {
        if (validPaths.Length == 0)
        {
            return;
        }

        string clipboardText = string.Join(Environment.NewLine, validPaths);
        DeskBoxClipboardWriteScope.MarkWrite(
            text: clipboardText,
            paths: validPaths);
        bool shellClipboardSet =
            ShellClipboardHelper.TrySetFileDropList(validPaths, cut: false);
        if (!shellClipboardSet)
        {
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            IReadOnlyList<IStorageItem> storageItems =
                await _fileService.GetStorageItemsAsync(validPaths);
            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems);
            }
            else
            {
                package.SetText(clipboardText);
            }

            package.Properties["DeskBoxSourceWidgetId"] = WidgetId;
            package.Properties["DeskBoxSourcePaths"] = validPaths;
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        _cutClipboardPaths = [];
        ApplyCutState();
        ShowFeedback(new WidgetFeedbackRequest(
            _localizationService.Format(
                "Widget.ElevatedDragOut.BridgeClipboardReady",
                validPaths.Length),
            WidgetFeedbackSeverity.Info,
            "elevated-outbound-clipboard-bridge"));
        App.Log(
            $"[DragProtocol] stage=ElevatedOutboundClipboardStaged widget={WidgetId} " +
            $"count={validPaths.Length} shell={shellClipboardSet}");
    }
}
