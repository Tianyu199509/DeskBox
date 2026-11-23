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
        SameSurfaceCreateManualStack,
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

        public required PointerDeviceType DeviceType { get; init; }

        public bool IsSecondaryButtonDrag { get; init; }

        public required WidgetItem[] PrePressSelection { get; init; }
    }

    private sealed class ElevatedPointerDragSession
    {
        public required string SessionId { get; init; }

        public required uint PointerId { get; init; }

        public required PointerDeviceType DeviceType { get; init; }

        public bool IsSecondaryButtonDrag { get; init; }

        public bool PointerDragRegistered { get; set; }
        public bool ResourcesReleased { get; set; }

        public required bool IsStackHeader { get; init; }

        public required string? StackReorderKey { get; init; }

        public required bool FromStackPopover { get; init; }

        public required string? SourceStackKey { get; init; }

        public required WidgetItem[] DraggedItems { get; init; }

        public WidgetItem[] DraggedDisplayUnits { get; init; } = [];

        public required string[] SourcePaths { get; init; }

        public required UIElement CaptureHost { get; init; }

        public WeakReference<FrameworkElement>? HostWindowRootAnchor { get; init; }

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
    private int _activeAsyncOutboundCommits;
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

        if (IsBlockedPointerDragOrigin(e))
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
        bool isSecondaryMouseContact =
            !isPrimaryContact &&
            pointOnHost.PointerDeviceType is
                PointerDeviceType.Mouse or PointerDeviceType.Touchpad &&
            pointOnHost.Properties.IsRightButtonPressed;
        if (!isPrimaryContact && !isSecondaryMouseContact)
        {
            return;
        }

        WidgetItem[] prePressSelection = originView.SelectedItems.Contains(item)
            ? originView.SelectedItems
                .OfType<WidgetItem>()
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
            DeviceType = pointOnHost.PointerDeviceType,
            IsSecondaryButtonDrag = isSecondaryMouseContact,
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

        if (IsBlockedPointerDragOrigin(e))
        {
            return;
        }

        PointerPoint pointOnRoot = e.GetCurrentPoint(Root);
        bool isPrimaryContact =
            pointOnRoot.Properties.IsLeftButtonPressed ||
            (pointOnRoot.PointerDeviceType is
                PointerDeviceType.Touch or PointerDeviceType.Pen &&
             pointOnRoot.IsInContact);
        bool isSecondaryMouseContact =
            !isPrimaryContact &&
            pointOnRoot.PointerDeviceType is
                PointerDeviceType.Mouse or PointerDeviceType.Touchpad &&
            pointOnRoot.Properties.IsRightButtonPressed;
        if (!isPrimaryContact && !isSecondaryMouseContact)
        {
            return;
        }

        ListViewBase originView = GetMainSurfaceItemsView();
        WidgetItem[] prePressSelection = originView.SelectedItems.Contains(stack)
            ? originView.SelectedItems
                .OfType<WidgetItem>()
                .Distinct()
                .ToArray()
            : [];

        _elevatedDragCandidate = new ElevatedPointerDragCandidate
        {
            PointerId = e.Pointer.PointerId,
            PressedItem = stack,
            IsStackHeader = true,
            FromStackPopover = false,
            SourceStackKey = null,
            CaptureHost = Root,
            OriginView = originView,
            StartPointOnHost = pointOnRoot.Position,
            DeviceType = pointOnRoot.PointerDeviceType,
            IsSecondaryButtonDrag = isSecondaryMouseContact,
            PrePressSelection = prePressSelection,
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

            if (Win32Helper.IsAsyncKeyPressed(VirtualKey.Escape))
            {
                CancelElevatedPointerDrag();
                e.Handled = true;
                return true;
            }

            if (activeSession.DeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen &&
                !e.GetCurrentPoint(null).IsInContact)
            {
                CompleteElevatedPointerDragOnRelease(e.Pointer, e);
                e.Handled = true;
                return true;
            }

            UpdateActiveElevatedPointerDragFromCursor(e);
            e.Handled = true;
            return true;
        }

        if (_elevatedDragCandidate is not { } candidate ||
            e.Pointer.PointerId != candidate.PointerId)
        {
            return false;
        }

        PointerPoint currentPoint = e.GetCurrentPoint(candidate.CaptureHost);
        bool stillInContact = candidate.IsSecondaryButtonDrag
            ? currentPoint.Properties.IsRightButtonPressed
            : currentPoint.Properties.IsLeftButtonPressed ||
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

        UpdateActiveElevatedPointerDragFromCursor(e);
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

        if (Win32Helper.IsAsyncKeyPressed(VirtualKey.Escape))
        {
            CancelElevatedPointerDrag();
            e.Handled = true;
            return true;
        }

        CompleteElevatedPointerDragOnRelease(e.Pointer, e);
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
                IsSessionDragButtonDown(activeSession))
            {
                // Tab switch inside a Widget Group detaches CaptureHost while
                // the user still holds the pointer contact/button. Hand off tracking
                // and release detection to _elevatedDragWatchTimer instead of
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
        WidgetItem[] draggedDisplayUnits;
        string[] sourcePaths;
        string? stackReorderKey = null;
        ImageSource? previewIcon;
        string fallbackGlyph;
        string previewTitle;
        int previewCount;

        IReadOnlyList<WidgetItem> pointerSelection =
            candidate.PrePressSelection.Length > 1
                ? candidate.PrePressSelection
                : candidate.OriginView.SelectedItems
                    .OfType<WidgetItem>()
                    .Distinct()
                    .ToArray();
        IReadOnlyList<WidgetItem> resolvedUnits =
            FileItemDragPackage.ResolveDraggedItems(
                [candidate.PressedItem],
                pointerSelection);
        bool singleCollapsedStackLeftDrag =
            candidate.IsStackHeader &&
            candidate.PressedItem is WidgetStackItem &&
            resolvedUnits.Count <= 1 &&
            !candidate.IsSecondaryButtonDrag;
        bool effectiveIsStackHeader = singleCollapsedStackLeftDrag;

        if (singleCollapsedStackLeftDrag &&
            candidate.PressedItem is WidgetStackItem stack)
        {
            if (stack.Members.Any(member => GetTransferState(member).BlocksMutation))
            {
                WidgetItem busyMember = stack.Members.First(member =>
                    GetTransferState(member).BlocksMutation);
                ShowTransferBlockedFeedback(GetTransferState(busyMember));
                return false;
            }

            var seenStackPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            draggedItems = stack.Members
                .Where(item =>
                    item is not WidgetStackItem &&
                    !string.IsNullOrWhiteSpace(item.Path) &&
                    (DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(item.Path) ||
                     File.Exists(item.Path) ||
                     Directory.Exists(item.Path)) &&
                    seenStackPaths.Add(Path.GetFullPath(item.Path)))
                .ToArray();
            sourcePaths = draggedItems
                .Select(item => Path.GetFullPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sourcePaths.Length > 0 && HasActiveTransferSource(sourcePaths))
            {
                ShowTransferBlockedFeedback(
                    _fileService.TransferSessions.GetState(sourcePaths[0]));
                return false;
            }

            _stackPointerDragStarted = true;
            _stackInputActivation.CancelPointer();
            draggedDisplayUnits = [stack];
            stackReorderKey = stack.StackKey;
            previewIcon = stack.Members.FirstOrDefault()?.Icon ?? stack.Icon;
            fallbackGlyph = "\uE8F1";
            previewTitle = stack.Name;
            previewCount = Math.Max(1, stack.Members.Count);
        }
        else
        {
            draggedDisplayUnits = resolvedUnits.Count > 0
                ? resolvedUnits.ToArray()
                : [candidate.PressedItem];
            WidgetItem[] expandedCandidates = resolvedUnits
                .SelectMany(item => item is WidgetStackItem stackItem
                    ? (IEnumerable<WidgetItem>)stackItem.Members
                    : [item])
                .Where(static item => item is not WidgetStackItem)
                .ToArray();
            if (expandedCandidates.Any(member => GetTransferState(member).BlocksMutation))
            {
                WidgetItem busyMember = expandedCandidates.First(member =>
                    GetTransferState(member).BlocksMutation);
                ShowTransferBlockedFeedback(GetTransferState(busyMember));
                return false;
            }

            var seenDraggedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            draggedItems = expandedCandidates
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Path) &&
                    (DesktopOutboundDropCoordinator.IsFastNetworkCandidatePath(item.Path) ||
                     File.Exists(item.Path) ||
                     Directory.Exists(item.Path)) &&
                    seenDraggedPaths.Add(Path.GetFullPath(item.Path)))
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

            if (candidate.PressedItem is WidgetStackItem)
            {
                _stackPointerDragStarted = true;
                _stackInputActivation.CancelPointer();
            }

            // Ensure the dragged unit(s) reflect in the ListView selection when
            // dragging an unselected item, matching standard Explorer drag.
            if (!candidate.OriginView.SelectedItems.Contains(candidate.PressedItem))
            {
                candidate.OriginView.SelectedItems.Clear();
                foreach (WidgetItem draggedUnit in draggedDisplayUnits)
                {
                    if (candidate.OriginView.Items.Contains(draggedUnit))
                    {
                        candidate.OriginView.SelectedItems.Add(draggedUnit);
                    }
                }

                UpdateSelectionCommandBar();
                RefreshItemSelectionVisuals();
            }

            if (draggedDisplayUnits.Length == 1 &&
                draggedDisplayUnits[0] is WidgetStackItem singleRightStack)
            {
                previewIcon = singleRightStack.Members.FirstOrDefault()?.Icon ?? singleRightStack.Icon;
                fallbackGlyph = "\uE8F1";
                previewTitle = singleRightStack.Name;
                previewCount = Math.Max(1, singleRightStack.Members.Count);
            }
            else
            {
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
        }

        bool restoreItemClick = candidate.OriginView.IsItemClickEnabled;
        candidate.OriginView.IsItemClickEnabled = false;
        bool captured = candidate.CaptureHost.CapturePointer(pointer);
        if (!captured)
        {
            candidate.OriginView.IsItemClickEnabled = restoreItemClick;
            return false;
        }

        if (candidate.IsSecondaryButtonDrag)
        {
            _suppressNextRightTap = true;
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
            DataPackageOperation allowedOperations = isProtectedCopyOnly
                ? DataPackageOperation.Copy
                : DataPackageOperation.Copy |
                  DataPackageOperation.Move |
                  DataPackageOperation.Link;
            ArmOutboundModifierTipWatch(allowedOperations);
        }

        ActiveDeskBoxDragRegistry.BeginPointerDragSession();
        _elevatedDragSession = new ElevatedPointerDragSession
        {
            SessionId = sessionId,
            PointerId = candidate.PointerId,
            DeviceType = candidate.DeviceType,
            IsSecondaryButtonDrag = candidate.IsSecondaryButtonDrag,
            PointerDragRegistered = true,
            IsStackHeader = effectiveIsStackHeader,
            StackReorderKey = stackReorderKey,
            FromStackPopover = candidate.FromStackPopover,
            SourceStackKey = candidate.SourceStackKey,
            DraggedItems = draggedItems,
            DraggedDisplayUnits = draggedDisplayUnits,
            SourcePaths = sourcePaths,
            CaptureHost = candidate.CaptureHost,
            HostWindowRootAnchor = Root.XamlRoot?.Content as FrameworkElement is { } hostRoot
                ? new WeakReference<FrameworkElement>(hostRoot)
                : null,
            OriginView = candidate.OriginView,
            RestoreOriginItemClickEnabled = restoreItemClick,
            PointerCaptured = true,
            LastCtrlDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Control),
            LastShiftDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Shift),
            LastAltDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu),
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
            $"kind={(effectiveIsStackHeader ? "stack" : "file")} " +
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

        if (_isDisposed || Win32Helper.IsAsyncKeyPressed(VirtualKey.Escape))
        {
            CancelElevatedPointerDrag();
            return;
        }

        if (session.DeviceType is PointerDeviceType.Mouse or PointerDeviceType.Touchpad ||
            session.DetachedContinueByPolling)
        {
            if (!IsSessionDragButtonDown(session))
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
        }
        else
        {
            session.MissingButtonTicks = 0;
        }

        bool ctrl = Win32Helper.IsAsyncKeyPressed(VirtualKey.Control);
        bool shift = Win32Helper.IsAsyncKeyPressed(VirtualKey.Shift);
        bool alt = Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu);
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

    private static bool IsSessionDragButtonDown(ElevatedPointerDragSession session) =>
        session.IsSecondaryButtonDrag
            ? Win32Helper.IsSecondaryMouseButtonDown()
            : Win32Helper.IsPrimaryMouseButtonDown();

    private bool TryResolveElevatedDragScreenPoint(
        ElevatedPointerDragSession session,
        PointerRoutedEventArgs? pointerArgs,
        out Win32Helper.POINT cursor)
    {
        IntPtr referenceHwnd = ResolveElevatedDragReferenceHwnd(session.FromStackPopover);
        if (pointerArgs is not null &&
            session.DeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen &&
            Win32Helper.TryGetPointerScreenPoint(
                referenceHwnd,
                session.CaptureHost,
                pointerArgs,
                out cursor))
        {
            return true;
        }

        if (pointerArgs is null &&
            session.DeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen &&
            session.HasLastScreenPoint)
        {
            cursor = new Win32Helper.POINT
            {
                X = session.LastScreenX,
                Y = session.LastScreenY,
            };
            return true;
        }

        return Win32Helper.GetCursorPos(out cursor) ||
            (pointerArgs is not null &&
             Win32Helper.TryGetPointerScreenPoint(
                 referenceHwnd,
                 session.CaptureHost,
                 pointerArgs,
                 out cursor));
    }

    private IntPtr ResolveElevatedDragReferenceHwnd(bool fromStackPopover)
    {
        if (fromStackPopover)
        {
            IntPtr popoverHwnd = ActiveStackPopoverWindowHandle;
            if (popoverHwnd != IntPtr.Zero)
            {
                return popoverHwnd;
            }
        }

        return _hostWindowHandle;
    }

    private void UpdateActiveElevatedPointerDragFromCursor(PointerRoutedEventArgs? pointerArgs = null)
    {
        if (_elevatedDragSession is not { } session ||
            !TryResolveElevatedDragScreenPoint(
                session,
                pointerArgs,
                out Win32Helper.POINT cursor))
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

        // Empty payloads have no right-drag file actions; regular and collapsed
        // stack file drags permit folder, bin, launch, cross-widget, and external
        // drop targets and show the right-drag choice menu on release.
        if (session.IsSecondaryButtonDrag &&
            (rootHwnd == IntPtr.Zero ||
             session.SourcePaths.Length == 0 ||
             (session.IsStackHeader && session.SourcePaths.Length == 0)))
        {
            ClearHoveredOtherStackPopover(session);
            ClearHoveredOtherWindow(session);
            ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
            HideStackPopoverReorderIndicator();
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
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
        if (session.SourcePaths.Length > 0 &&
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
        // inactive group title switcher outside Root.
        if (session.SourcePaths.Length > 0 &&
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
        if (session.SourcePaths.Length > 0 &&
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
                if (!string.IsNullOrWhiteSpace(session.StackReorderKey))
                {
                    ClearFolderDropTarget();
                    session.Route = ElevatedDragTargetRoute.Blocked;
                    return string.Empty;
                }

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

        if (session.IsSecondaryButtonDrag)
        {
            HideStackPopoverReorderIndicator();
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

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

        // Member dragged out of an open stack popover onto the parent widget.
        if (session.FromStackPopover)
        {
            PersistSurfaceReorder();

            if (!session.IsSecondaryButtonDrag &&
                FindHitStackSurfaceBorder(screenX, screenY, out WidgetStackItem? hitStack)
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

            if (session.IsSecondaryButtonDrag)
            {
                session.Route = ElevatedDragTargetRoute.Blocked;
                return string.Empty;
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

        // Main-surface file item(s) or collapsed stack tile dragged over another collapsed stack tile.
        if (!session.IsSecondaryButtonDrag &&
            FindHitStackSurfaceBorder(screenX, screenY, out WidgetStackItem? targetStack)
            is { } targetStackBorder &&
            targetStack is not null &&
            (!session.IsStackHeader ||
             !string.Equals(targetStack.StackKey, session.StackReorderKey, StringComparison.Ordinal)) &&
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

            FileDropIntent navIntent = ResolveElevatedInternalFolderIntent(
                session.SourcePaths,
                navFolder.Path);
            if (navIntent == FileDropIntent.None)
            {
                session.Route = ElevatedDragTargetRoute.Blocked;
                return string.Empty;
            }

            session.Route = ElevatedDragTargetRoute.SameSurfaceFolderDrop;
            session.TargetChildItem = navFolder;
            canDrop = true;
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

        if (session.IsSecondaryButtonDrag)
        {
            PersistSurfaceReorder();
            ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
            session.Route = ElevatedDragTargetRoute.Blocked;
            return string.Empty;
        }

        // Collapsed stack header reorder on main surface (checked after join/folder/launch/nav
        // targets so dragging a stack tile over those targets still triggers drop actions).
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
                pointInMainView,
                session.DraggedDisplayUnits))
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
            if (!string.IsNullOrWhiteSpace(session.StackReorderKey))
            {
                ClearFolderDropTarget();
                session.Route = ElevatedDragTargetRoute.Blocked;
                return true;
            }

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

        if (behavior == ItemDropBehavior.None &&
            !session.IsStackHeader &&
            !session.FromStackPopover &&
            ViewModel.UsesStackProjection &&
            iconHitItem is not WidgetStackItem &&
            !iconHitItem.IsFolder &&
            !iconHitItem.IsStackChild &&
            session.DraggedItems.Any(static item =>
                item is not WidgetStackItem &&
                !item.IsFolder &&
                !string.IsNullOrWhiteSpace(item.Path)))
        {
            PersistSurfaceReorder();
            ClearFolderDropTarget();
            ClearLaunchDropTarget();
            SetStackMemberDropTarget(iconBorder);
            session.Route = ElevatedDragTargetRoute.SameSurfaceCreateManualStack;
            session.TargetChildItem = iconHitItem;
            canDrop = true;
            caption = _localizationService.Format(
                "Widget.Stack.DragCaption.CreateWith",
                iconHitItem.Name);
            return true;
        }

        return false;
    }

    private void BeginOrUpdateElevatedStackHeaderReorderPreview(
        string stackKey,
        Windows.Foundation.Point position)
    {
        ViewModel.BeginInteractiveLayoutUndoBatch();
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
        Windows.Foundation.Point position,
        WidgetItem[]? draggedDisplayUnits = null)
    {
        WidgetItem[] explicitUnits = draggedDisplayUnits ?? [];
        if (paths.Length == 0 && explicitUnits.Length == 0)
        {
            return false;
        }

        _surfaceReorderPathSet ??= paths.Length > 0
            ? paths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        WidgetItem[] draggedItems = _surfaceReorderDraggedItems.Length > 0
            ? _surfaceReorderDraggedItems
            : explicitUnits.Length > 0
                ? explicitUnits
                : ResolveSurfaceReorderItems();
        if (draggedItems.Length == 0)
        {
            return false;
        }

        if (!_isSurfaceReorderDragActive)
        {
            ViewModel.BeginInteractiveLayoutUndoBatch();
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
        bool canSourceMove = !DriveMediaInterop.IsProtectedDragOutSource(sourcePaths) &&
            !origin.IsMoveProtectedSystemOrSharedSource;
        uint allowedEffects = canSourceMove
            ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
            : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;
        return TransferIntentEngine.Evaluate(new TransferIntentRequest(
            Domain: TransferDomain.InternalDeskBox,
            TargetKind: TransferTargetKind.DirectorySurface,
            HasFileData: true,
            HasVirtualFileData: false,
            HasShellApplicationOrNamespaceData: false,
            ForceCopy: false,
            HasMappedFolder: true,
            ControlDown: controlDown ?? Win32Helper.IsAsyncKeyPressed(VirtualKey.Control),
            ShiftDown: shiftDown ?? Win32Helper.IsAsyncKeyPressed(VirtualKey.Shift),
            AltDown: altDown ?? Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu),
            AllowedEffects: allowedEffects,
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
        IEnumerable<string>? sourcePaths = null,
        bool isDesktopTarget = true)
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
        bool configuredMove = string.Equals(
            managedDragOutAction,
            SettingsService.ManagedDragOutActionMove,
            StringComparison.Ordinal);
        // Mirror TargetAwareDropEffectPolicy: Move crosses drives only for Desktop;
        // ordinary Explorer folders (which may be USB sticks or NAS shares) follow
        // Windows native same-volume Move / cross-volume Copy unless Shift is held.
        bool followWindows =
            string.IsNullOrWhiteSpace(managedDragOutAction) ||
            string.Equals(
                managedDragOutAction,
                SettingsService.ManagedDragOutActionFollowWindows,
                StringComparison.Ordinal) ||
            (configuredMove && !isDesktopTarget);
        bool defaultMove = !protectedCopyOnly &&
            configuredMove &&
            isDesktopTarget;
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
        CancelFolderNavigationSpringBack();
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
        ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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
        DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");

        App.Log(
            $"[DragProtocol] stage=ElevatedPointerDragCancelled widget={WidgetId} " +
            $"session={FormatDragSessionId(session.SessionId)}");
    }

    private static void ReleasePhysicalPointerDragLock(ElevatedPointerDragSession session)
    {
        if (!session.PointerDragRegistered)
        {
            return;
        }

        session.PointerDragRegistered = false;
        ActiveDeskBoxDragRegistry.EndPointerDragSession();
    }

    private void CompleteElevatedPointerDragOnRelease(
        Pointer? pointer,
        PointerRoutedEventArgs? pointerArgs = null)
    {
        if (_elevatedDragSession is not { } session)
        {
            return;
        }

        ReleasePhysicalPointerDragLock(session);
        session.LastCtrlDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Control);
        session.LastShiftDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Shift);
        session.LastAltDown = Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu);
        if (TryResolveElevatedDragScreenPoint(
                session,
                pointerArgs,
                out Win32Helper.POINT cursor))
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

                case ElevatedDragTargetRoute.SameSurfaceCreateManualStack:
                    CommitElevatedSameSurfaceCreateManualStack(session);
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
                    if (session.IsSecondaryButtonDrag)
                    {
                        QueueElevatedAsyncDropCommit(session, route, releaseX, releaseY);
                    }
                    else
                    {
                        CommitElevatedSameSurfaceShortcutLaunch(session);
                        FinalizeElevatedSessionAfterSyncCommit(session);
                    }
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
                    ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
                    ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
                    FinalizeElevatedSessionAfterSyncCommit(session);
                    break;
            }
        }
        catch (Exception ex)
        {
            ClearHoveredOtherStackPopover(session);
            ClearHoveredOtherWindow(session);
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
            return;
        }

        string targetStackKey = stack.StackKey;
        string[] targetAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        ViewModel.BeginInteractiveLayoutUndoBatch();
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
            ViewModel.CommitInteractiveLayoutUndoBatch();
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
        else
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
        }
    }

    private void CommitElevatedSameSurfaceCreateManualStack(ElevatedPointerDragSession session)
    {
        ClearStackMemberDropTarget();
        PersistSurfaceReorder();
        if (session.TargetChildItem is not { } targetItem ||
            targetItem is WidgetStackItem ||
            targetItem.IsFolder ||
            targetItem.IsStackChild)
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
            return;
        }

        WidgetItem[] looseDragged = session.DraggedItems
            .Where(item =>
                item is not WidgetStackItem &&
                !item.IsFolder &&
                !string.IsNullOrWhiteSpace(item.Path) &&
                !string.Equals(item.Path, targetItem.Path, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (looseDragged.Length == 0)
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
            return;
        }

        ViewModel.BeginInteractiveLayoutUndoBatch();
        bool created = ViewModel.CreateManualStack(
            [targetItem, .. looseDragged],
            anchorTargetItem: targetItem);
        if (created)
        {
            ClearSelection();
            ViewModel.CommitInteractiveLayoutUndoBatch();
        }
        else
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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

        ViewModel.BeginInteractiveLayoutUndoBatch();
        bool removed = DetachStackItemsToSurfaceIndex(
            sourceStack.StackKey,
            detachItems,
            detachInsertionIndex);
        if (removed)
        {
            ViewModel.CommitInteractiveLayoutUndoBatch();
            CloseStackPopover();
        }
        else
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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
        ViewModel.BeginInteractiveLayoutUndoBatch();
        bool reordered = ReorderStackPopoverMembers(
            sourceStack,
            items,
            insertionIndex);
        HideStackPopoverReorderIndicator();
        if (reordered)
        {
            ViewModel.CommitInteractiveLayoutUndoBatch();
        }
        else
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
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
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
            return;
        }

        string targetStackKey = stack.StackKey;
        string[] targetAnchors = stack.Members
            .Select(member => member.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        ViewModel.BeginInteractiveLayoutUndoBatch();
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

            ViewModel.CommitInteractiveLayoutUndoBatch();
            ClearSelection();
            QueueStackPopoverReconciliation(targetStackKey, targetAnchors);
        }
        else
        {
            ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
        }
    }

    private void CommitElevatedSameSurfaceShortcutLaunch(
        ElevatedPointerDragSession session)
    {
        ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
        ClearSameSurfaceHoverVisuals(keepStackPopoverIndicator: false);
        if (session.TargetChildItem is not { Path.Length: > 0 } shortcut ||
            session.SourcePaths.Length == 0)
        {
            return;
        }

        MarkNativeLaunchConsumed();
        if (!ShortcutFileLauncher.CanLaunchWithFiles(shortcut.Path, session.SourcePaths))
        {
            ShowShortcutLaunchRefusedFeedback(shortcut.Name);
            return;
        }

        string shortcutPath = shortcut.Path;
        string shortcutName = shortcut.Name;
        string[] sourcePaths = session.SourcePaths;
        UiDispatch.PostAfterCallback(async () =>
        {
            if (_isDisposed)
            {
                return;
            }

            ShellLaunchOutcome launchOutcome = await ShortcutFileLauncher.LaunchWithFilesAsync(
                shortcutPath,
                sourcePaths,
                _hostWindowHandle,
                _lifetimeCancellation.Token);
            if (launchOutcome == ShellLaunchOutcome.Failed && !_isDisposed)
            {
                ShowShortcutLaunchRefusedFeedback(shortcutName);
            }
        });
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
        CancelFolderNavigationSpringBack();
        ClearFolderDropTarget();
        ClearLaunchDropTarget();
        ClearStackMemberDropTarget();
        ViewModel.RevertAndClearInteractiveLayoutUndoBatch();
        PersistSurfaceReorder();
        HideStackPopoverReorderIndicator();

        bool TryEnqueueTracked(Func<Task> work)
        {
            Interlocked.Increment(ref _activeAsyncOutboundCommits);
            if (DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await work();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeAsyncOutboundCommits);
                        NotifyOutboundDragCompletedIfIdle();
                    }
                }))
            {
                return true;
            }

            Interlocked.Decrement(ref _activeAsyncOutboundCommits);
            return false;
        }

        if (!TryEnqueueTracked(async () =>
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
            case ElevatedDragTargetRoute.SameSurfaceShortcutLaunch:
                if (session.TargetChildItem is { Path.Length: > 0 } launchShortcut)
                {
                    (FrameworkElement launchTarget, IntPtr launchHwnd, bool launchInPopover) =
                        ResolveRightButtonMenuPlacement(
                            releaseX,
                            releaseY,
                            session.HostWindowRootAnchor);
                    string currentDestination = TransferIntentEngine.ResolveProspectiveManagedDestination(
                        ViewModel.CurrentFolderPath,
                        ViewModel.MappedFolderPath,
                        _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
                    bool canOfferFolderChoices = !string.IsNullOrWhiteSpace(currentDestination) &&
                        !AreAllSourcesAlreadyInDestinationLexically(session.SourcePaths, currentDestination);
                    SourceOriginSnapshot launchOrigin = PathOriginClassifier.ClassifySources(
                        session.SourcePaths,
                        PathOriginClassifier.DefaultDropProbeBudget);
                    bool canSourceMove = !DriveMediaInterop.IsProtectedDragOutSource(session.SourcePaths) &&
                        !launchOrigin.IsMoveProtectedSystemOrSharedSource;
                    uint launchAllowedEffects = canSourceMove
                        ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
                        : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;
                    StackPopoverRightDropChoice launchChoice = await ShowRightButtonDropMenuAsync(
                        launchTarget,
                        launchHwnd,
                        releaseX,
                        releaseY,
                        session.SourcePaths,
                        launchShortcut,
                        isRecycleBinTarget: false,
                        allowLaunchShortcut: true,
                        hasTargetFolder: canOfferFolderChoices,
                        destinationFolder: currentDestination,
                        isDeskBoxOwnedSource: true,
                        containsTemporaryFiles: false,
                        allItemsTemporary: false,
                        sourceCanCopy: canOfferFolderChoices,
                        sourceCanMove: canOfferFolderChoices && canSourceMove,
                        allowedEffects: launchAllowedEffects,
                        trackPopoverContextMenu: launchInPopover);
                    if (launchChoice == StackPopoverRightDropChoice.OpenWithApp)
                    {
                        CommitElevatedSameSurfaceShortcutLaunch(session);
                    }
                    else if (launchChoice is StackPopoverRightDropChoice.Copy
                        or StackPopoverRightDropChoice.Move
                        or StackPopoverRightDropChoice.Shortcut)
                    {
                        FileDropIntent forcedLaunchFolderIntent = launchChoice switch
                        {
                            StackPopoverRightDropChoice.Move => FileDropIntent.Move,
                            StackPopoverRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                            _ => FileDropIntent.Copy,
                        };
                        await ImportNativeDroppedFilesAsync(
                            session.SourcePaths,
                            containsTemporaryFiles: false,
                            copyWhenMapped: launchChoice != StackPopoverRightDropChoice.Move,
                            targetItem: null,
                            forcedIntent: forcedLaunchFolderIntent,
                            screenX: releaseX,
                            screenY: releaseY,
                            allowedEffects: launchAllowedEffects);
                        if (launchChoice == StackPopoverRightDropChoice.Move)
                        {
                            await ReconcileElevatedMovedOutPathsAsync(
                                session.SourcePaths,
                                session.FromStackPopover);
                        }
                    }
                }
                break;

            case ElevatedDragTargetRoute.SameSurfaceFolderDrop:
                if (session.TargetChildItem is { } folder &&
                    ItemDropBehaviorPolicy.TryGetFolderDropDestinationPath(
                        folder,
                        out string folderDestination))
                {
                    FileDropIntent intent;
                    if (session.IsSecondaryButtonDrag)
                    {
                        (FrameworkElement folderTarget, IntPtr folderHwnd, bool folderInPopover) =
                            ResolveRightButtonMenuPlacement(
                                releaseX,
                                releaseY,
                                session.HostWindowRootAnchor);
                        SourceOriginSnapshot folderOrigin = PathOriginClassifier.ClassifySources(
                            session.SourcePaths,
                            PathOriginClassifier.DefaultDropProbeBudget);
                        bool canSourceMove = !DriveMediaInterop.IsProtectedDragOutSource(session.SourcePaths) &&
                            !folderOrigin.IsMoveProtectedSystemOrSharedSource;
                        uint folderAllowedEffects = canSourceMove
                            ? NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Move | NativeDropEffectPolicy.Link
                            : NativeDropEffectPolicy.Copy | NativeDropEffectPolicy.Link;
                        StackPopoverRightDropChoice folderChoice = await ShowRightButtonDropMenuAsync(
                            folderTarget,
                            folderHwnd,
                            releaseX,
                            releaseY,
                            session.SourcePaths,
                            folder,
                            isRecycleBinTarget: false,
                            allowLaunchShortcut: false,
                            hasTargetFolder: true,
                            destinationFolder: folderDestination,
                            isDeskBoxOwnedSource: true,
                            containsTemporaryFiles: false,
                            allItemsTemporary: false,
                            sourceCanCopy: true,
                            sourceCanMove: canSourceMove,
                            allowedEffects: folderAllowedEffects,
                            trackPopoverContextMenu: folderInPopover);
                        intent = folderChoice switch
                        {
                            StackPopoverRightDropChoice.Copy => FileDropIntent.Copy,
                            StackPopoverRightDropChoice.Move => FileDropIntent.Move,
                            StackPopoverRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                            _ => FileDropIntent.None,
                        };
                    }
                    else
                    {
                        intent = ResolveElevatedInternalFolderIntent(
                            session.SourcePaths,
                            folderDestination,
                            session.LastCtrlDown,
                            session.LastShiftDown,
                            session.LastAltDown);
                    }

                    if (intent != FileDropIntent.None)
                    {
                        DroppedFilePath[] dropped = session.SourcePaths
                            .Select(path => new DroppedFilePath(
                                path,
                                Path.GetFileName(path),
                                ForceManagedCopy: false))
                            .ToArray();
                        FolderImportOutcome folderOutcome =
                            await ImportNativeDroppedFilesIntoFolderAsync(
                                dropped,
                                folder,
                                move: intent == FileDropIntent.Move,
                                intentOverride: intent == FileDropIntent.Shortcut
                                    ? FileDropIntent.Shortcut
                                    : null);
                        if (folderOutcome.Imported && intent == FileDropIntent.Move)
                        {
                            await ReconcileElevatedMovedOutPathsAsync(
                                session.SourcePaths,
                                session.FromStackPopover);
                        }

                        if (folderOutcome.CompletedCount > 0 &&
                            !folderOutcome.ShowedDedicatedWarning &&
                            intent is FileDropIntent.Move or FileDropIntent.Copy)
                        {
                            // Receipt-only (deterministic flags): the internal
                            // source short-circuits the protection branches and
                            // no persisted once-only quota is consumed here.
                            TryShowSubfolderDropHudReceipt(
                                intent,
                                session.SourcePaths,
                                isDeskBoxOwnedSource: true,
                                moved: intent == FileDropIntent.Move,
                                folderOutcome.CompletedCount,
                                folderOutcome.UndoHistoryId,
                                sourceWidgetId: WidgetId,
                                folderDestination,
                                containsTemporaryFiles: false,
                                hadExplicitModifierOrChoice:
                                    session.IsSecondaryButtonDrag ||
                                    session.LastCtrlDown ||
                                    session.LastShiftDown ||
                                    session.LastAltDown,
                                autoProtectedExternalCopy: false,
                                fallbackMessage: intent == FileDropIntent.Copy
                                    ? _localizationService.Format(
                                        "Widget.CopiedToFolder",
                                        folder.Name,
                                        folderOutcome.CompletedCount)
                                    : null);
                        }
                    }
                }
                break;

            case ElevatedDragTargetRoute.SameSurfaceRecycleBinDrop:
                if (session.TargetChildItem is { } binTile)
                {
                    if (session.IsSecondaryButtonDrag)
                    {
                        (FrameworkElement binTarget, IntPtr binHwnd, bool binInPopover) =
                            ResolveRightButtonMenuPlacement(
                                releaseX,
                                releaseY,
                                session.HostWindowRootAnchor);
                        StackPopoverRightDropChoice binChoice = await ShowRightButtonDropMenuAsync(
                            binTarget,
                            binHwnd,
                            releaseX,
                            releaseY,
                            session.SourcePaths,
                            binTile,
                            isRecycleBinTarget: true,
                            allowLaunchShortcut: false,
                            hasTargetFolder: false,
                            destinationFolder: null,
                            isDeskBoxOwnedSource: true,
                            containsTemporaryFiles: false,
                            allItemsTemporary: false,
                            sourceCanCopy: false,
                            sourceCanMove: true,
                            allowedEffects: NativeDropEffectPolicy.Move,
                            trackPopoverContextMenu: binInPopover);
                        if (binChoice != StackPopoverRightDropChoice.Move)
                        {
                            break;
                        }
                    }

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
                            session.LastAltDown,
                            isSecondaryButtonDrag: session.IsSecondaryButtonDrag);
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
                        session.LastAltDown,
                        isSecondaryButtonDrag: session.IsSecondaryButtonDrag);
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

    private (FrameworkElement Target, IntPtr WindowHandle, bool InStackPopover)
        ResolveRightButtonMenuPlacement(
            int screenX,
            int screenY,
            WeakReference<FrameworkElement>? fallbackHostAnchor = null)
    {
        if (_stackPopoverPopupOpen &&
            _stackPopoverHostWindow is { IsVisible: true, WindowHandle: not 0 } popHost &&
            _stackPopoverSurface is { XamlRoot: not null } popSurface)
        {
            IntPtr hitHwnd = Win32Helper.WindowFromPoint(new Win32Helper.POINT
            {
                X = screenX,
                Y = screenY,
            });
            IntPtr rootHwnd = hitHwnd != IntPtr.Zero
                ? Win32Helper.GetAncestor(hitHwnd, Win32Helper.GA_ROOT)
                : IntPtr.Zero;
            if (rootHwnd == popHost.WindowHandle)
            {
                return (popSurface, popHost.WindowHandle, true);
            }
        }

        if ((!Root.IsLoaded || Root.XamlRoot is null) &&
            fallbackHostAnchor is not null &&
            fallbackHostAnchor.TryGetTarget(out FrameworkElement? hostAnchor) &&
            hostAnchor is { IsLoaded: true, XamlRoot: not null })
        {
            return (hostAnchor, _hostWindowHandle, false);
        }

        return (Root, _hostWindowHandle, false);
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

        if (session.ResourcesReleased)
        {
            return;
        }

        session.ResourcesReleased = true;
        StopOutboundModifierTipWatch();
        ReleasePhysicalPointerDragLock(session);

        if (session.SourcePaths.Length > 0)
        {
            ActiveDeskBoxDragRegistry.End(session.SessionId);
        }

        if (session.Route != ElevatedDragTargetRoute.OutboundExternal)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
        }

        ResetDragPayloadCache();
        _pressedStack = null;
        _stackPointerDragStarted = false;
        _stackInputActivation.CancelPointer();

        if (session.FromStackPopover)
        {
            CompleteStackPopoverDrag();
        }

        if (session.IsSecondaryButtonDrag)
        {
            if (!DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (!_isDisposed)
                        {
                            _suppressNextRightTap = false;
                        }
                    }))
            {
                _suppressNextRightTap = false;
            }
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
                    Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(destinationFolder) ||
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
                    Win32Helper.IsAsyncKeyPressed(VirtualKey.Control),
                    Win32Helper.IsAsyncKeyPressed(VirtualKey.Shift),
                    Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu),
                    sourcePaths,
                    isDesktopTarget: target.Kind == ElevatedOutboundTargetKind.Desktop);
                if (intent == FileDropIntent.None)
                {
                    return string.Empty;
                }

                canDrop = true;
                return FormatDropCaption(intent, GetOutboundTargetDisplayName(target));
            }

            case ElevatedOutboundTargetKind.RecycleBin:
            {
                if (FileItemDragPackage.IsProtectedCopyOnlySource(sourcePaths) ||
                    PathOriginClassifier.ClassifySources(
                        sourcePaths,
                        TimeSpan.FromMilliseconds(80)).HasAppExecutionAliasSource)
                {
                    return T("Widget.CannotMoveToFolder");
                }

                canDrop = true;
                return T("Widget.DropOnRecycleBinHint");
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
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            string userDesktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(userDesktop) &&
                string.Equals(
                    normalized,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDesktop)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string publicDesktop = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonDesktopDirectory);
            return !string.IsNullOrWhiteSpace(publicDesktop) &&
                string.Equals(
                    normalized,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(publicDesktop)),
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
        string[] candidatePaths = session.SourcePaths;
        if (candidatePaths.Any(DesktopOutboundDropCoordinator.MightTouchNetworkPath))
        {
            DesktopOutboundDropCoordinator.BoundedSourcePathProbeResult probe =
                await Task.Run(() => DesktopOutboundDropCoordinator.FilterExistingPathsWithBoundedNetworkProbe(
                    candidatePaths,
                    timeoutMs: 650));
            if (probe.TimedOut)
            {
                DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
                return false;
            }

            candidatePaths = probe.ExistingPaths;
        }
        else
        {
            candidatePaths = candidatePaths
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    (File.Exists(path) || Directory.Exists(path)))
                .ToArray();
        }

        string[] validPaths = candidatePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (validPaths.Length == 0 || session.LastOutboundRootHwnd == IntPtr.Zero)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            return false;
        }

        bool allowDesktopRecycleBinHit =
            ElevatedShellOutboundDropInterop.ShouldAllowDesktopRecycleBinDrop(
                Win32Helper.IsAsyncKeyPressed(VirtualKey.Control),
                Win32Helper.IsAsyncKeyPressed(VirtualKey.Menu));
        ElevatedOutboundTargetInfo target = await Task.Run(() =>
            ElevatedShellOutboundDropInterop.ResolveOutboundTarget(
                session.LastOutboundHitHwnd,
                session.LastOutboundRootHwnd,
                explorerQueryTimeoutMs: 1500,
                screenX: screenX,
                screenY: screenY,
                allowDesktopRecycleBinHit: allowDesktopRecycleBinHit));
        IntPtr currentTabHwnd = ElevatedShellOutboundDropInterop.ResolveTargetTabWindow(
            session.LastOutboundHitHwnd,
            session.LastOutboundRootHwnd);
        if (target.Kind == ElevatedOutboundTargetKind.UnsupportedShellNamespace &&
            ElevatedShellOutboundDropInterop.ShouldReuseCachedExplorerFolderTarget(
                session.CachedOutboundRootHwnd,
                session.CachedOutboundTabHwnd,
                session.CachedOutboundResolvedTickMs,
                session.CachedOutboundTarget.Kind,
                session.LastOutboundRootHwnd,
                currentTabHwnd,
                Environment.TickCount64))
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

            case ElevatedOutboundTargetKind.RecycleBin:
                return await ExecuteElevatedRecycleBinDropAsync(
                    session,
                    validPaths,
                    screenX,
                    screenY);

            case ElevatedOutboundTargetKind.ForeignWindow:
                DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
                await StageElevatedOutboundClipboardAsync(validPaths);
                return false;

            case ElevatedOutboundTargetKind.UnsupportedShellNamespace:
                DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
                ShowFeedback(new WidgetFeedbackRequest(
                    T("Widget.ElevatedDragOut.UnsupportedShellTarget"),
                    WidgetFeedbackSeverity.Warning,
                    "elevated-outbound-unsupported-shell"));
                return false;

            default:
                DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
                return false;
        }
    }

    private async Task<bool> ExecuteElevatedRecycleBinDropAsync(
        ElevatedPointerDragSession session,
        string[] validPaths,
        int screenX,
        int screenY)
    {
        DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
        if (validPaths.Length == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(session.StackReorderKey))
        {
            ShowCollapsedStackRecycleBinBlockedFeedback();
            return false;
        }

        SourceOriginSnapshot origin = PathOriginClassifier.ClassifySources(
            validPaths,
            PathOriginClassifier.DefaultDropProbeBudget);
        if (FileItemDragPackage.IsProtectedCopyOnlySource(validPaths) ||
            origin.HasAppExecutionAliasSource ||
            origin.IsMoveProtectedSystemOrSharedSource)
        {
            return false;
        }

        if (session.IsSecondaryButtonDrag)
        {
            (FrameworkElement binTarget, IntPtr binHwnd, bool binInPopover) =
                ResolveRightButtonMenuPlacement(
                    screenX,
                    screenY,
                    session.HostWindowRootAnchor);
            StackPopoverRightDropChoice binChoice = await ShowRightButtonDropMenuAsync(
                binTarget,
                binHwnd,
                screenX,
                screenY,
                validPaths,
                targetItem: null,
                isRecycleBinTarget: true,
                allowLaunchShortcut: false,
                hasTargetFolder: false,
                destinationFolder: null,
                isDeskBoxOwnedSource: true,
                containsTemporaryFiles: false,
                allItemsTemporary: false,
                sourceCanCopy: false,
                sourceCanMove: true,
                allowedEffects: NativeDropEffectPolicy.Move,
                trackPopoverContextMenu: binInPopover);
            if (binChoice != StackPopoverRightDropChoice.Move)
            {
                return false;
            }
        }

        await DeleteDroppedFilesForBinAsync(validPaths, string.Empty);
        await ReconcileElevatedMovedOutPathsAsync(validPaths, session.FromStackPopover);
        return true;
    }

    private async Task<bool> ExecuteElevatedShellFolderDropAsync(
        ElevatedPointerDragSession session,
        ElevatedOutboundTargetInfo target,
        string[] validPaths,
        int screenX,
        int screenY)
    {
        string destinationFolder = target.DestinationFolderPath;
        bool destinationReachable = !string.IsNullOrWhiteSpace(destinationFolder) &&
            (DesktopOutboundDropCoordinator.MightTouchNetworkPath(destinationFolder)
                ? await DesktopOutboundDropCoordinator.IsDestinationDirectoryReachableAsync(destinationFolder, 800)
                : Directory.Exists(destinationFolder));
        if (!destinationReachable)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            return false;
        }

        if (HasTransferConflict(validPaths, destinationFolder))
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            FileTransferPathState destState =
                _fileService.TransferSessions.GetState(destinationFolder);
            ShowTransferBlockedFeedback(
                destState.IsActive
                    ? destState
                    : _fileService.TransferSessions.GetState(validPaths[0]));
            return false;
        }

        if (Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(destinationFolder))
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            ShowStoreVirtualizedDestinationBlockedFeedback();
            return false;
        }

        if (IsUnsafeFolderDrop(validPaths, destinationFolder) ||
            await Task.Run(() => FileService.IsUnsafeDirectoryTransfer(
                validPaths,
                destinationFolder)))
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
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
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            ShowSameDirectoryDropFeedback();
            return false;
        }

        bool isDesktop = target.Kind == ElevatedOutboundTargetKind.Desktop ||
            IsUserDesktopFolder(destinationFolder);
        bool sameVolume = FileDropIntentPolicy.AreAllOnSameVolume(
            validPaths,
            destinationFolder);
        FileDropIntent intent;
        if (session.IsSecondaryButtonDrag)
        {
            (FrameworkElement placementTarget, IntPtr placementHwnd, _) =
                ResolveRightButtonMenuPlacement(
                    screenX,
                    screenY,
                    session.HostWindowRootAnchor);
            bool hasAppAlias = PathOriginClassifier
                .ClassifySources(validPaths, PathOriginClassifier.DefaultDropProbeBudget)
                .HasAppExecutionAliasSource;
            bool isProtectedCopyOnly =
                hasAppAlias ||
                FileItemDragPackage.IsProtectedCopyOnlySource(validPaths);
            StackPopoverRightDropChoice rightChoice = await ShowRightButtonDropMenuAsync(
                placementTarget,
                placementHwnd,
                screenX,
                screenY,
                validPaths,
                targetItem: null,
                isRecycleBinTarget: false,
                allowLaunchShortcut: false,
                hasTargetFolder: true,
                destinationFolder: destinationFolder,
                isDeskBoxOwnedSource: true,
                containsTemporaryFiles: false,
                allItemsTemporary: false,
                sourceCanCopy: !hasAppAlias,
                sourceCanMove: !isProtectedCopyOnly,
                allowedEffects: NativeDropEffectPolicy.Copy |
                    NativeDropEffectPolicy.Move |
                    NativeDropEffectPolicy.Link);
            intent = rightChoice switch
            {
                StackPopoverRightDropChoice.Copy => FileDropIntent.Copy,
                StackPopoverRightDropChoice.Move => FileDropIntent.Move,
                StackPopoverRightDropChoice.Shortcut => FileDropIntent.Shortcut,
                _ => FileDropIntent.None,
            };
        }
        else
        {
            intent = ResolveElevatedOutboundDropIntent(
                _settingsService.Settings.FileWidget.ManagedDragOutAction,
                sameVolume,
                session.LastCtrlDown,
                session.LastShiftDown,
                session.LastAltDown,
                validPaths,
                isDesktopTarget: isDesktop);
        }

        if (intent == FileDropIntent.None)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            return false;
        }

        bool move = intent == FileDropIntent.Move;
        bool createShortcuts = intent == FileDropIntent.Shortcut;

        if (isDesktop)
        {
            if (createShortcuts)
            {
                string[] prospectiveShortcuts = validPaths
                    .Select(path => Path.Combine(
                        destinationFolder,
                        GetShortcutDisplayName(path, Directory.Exists(path))))
                    .ToArray();
                SuppressDesktopDragOutArrivals(prospectiveShortcuts);
            }
            else
            {
                SuppressDesktopDragOutArrivals(validPaths);
            }
        }

        BeginTrackedImport();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyDictionary<string, HashSet<string>?> preExistingMergeSnapshots =
            new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        App.Log(
            $"[DragProtocol] stage=ElevatedOutboundTransferStart widget={WidgetId} " +
            $"kind={target.Kind} target='{destinationFolder}' " +
            $"intent={intent} count={validPaths.Length}");
        try
        {
            EnsureTrackedImportStarted();
            if (move)
            {
                preExistingMergeSnapshots = await Task.Run(
                    () => OrganizerService.SnapshotPreExistingMergeDestinations(
                        validPaths,
                        destinationFolder),
                    ActiveImportCancellationToken);
            }

            IProgress<FileService.FileTransferProgress> progress =
                new CallbackProgress<FileService.FileTransferProgress>(
                    ReportImportProgress);
            var results = new List<FileService.FileTransferResult>();
            int userSkippedCount = 0;

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
                userSkippedCount += AppendTransferResults(results, await _fileService.TransferItemsWithResultAsync(
                    validPaths,
                    destinationFolder,
                    move,
                    progress,
                    ActiveImportCancellationToken,
                    useShellProgress: true,
                    ownerWindowHandle: _hostWindowHandle));
            }

            OrganizationHistoryEntry? undoEntry = null;
            if (move)
            {
                _cutClipboardPaths = [];
                ApplyCutState();
                if (results.Count > 0)
                {
                    undoEntry = await ViewModel.RecordSubfolderMoveUndoAsync(
                        results,
                        preExistingMergeSnapshots,
                        sourceWidgetId: WidgetId);
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
            }

            if (target.Kind == ElevatedOutboundTargetKind.Desktop &&
                createdDestinations.Length > 0)
            {
                _ = await Task.Run(() =>
                    ElevatedShellOutboundDropInterop.TryPositionDesktopItemsNearCursor(
                        createdDestinations,
                        screenX,
                        screenY));
            }

            if (!ShowSkippedTransferFeedback(userSkippedCount) && results.Count > 0)
            {
                string displayName = GetOutboundTargetDisplayName(target);
                string feedbackMessage = createShortcuts
                    ? _localizationService.Format(
                        "Widget.ShortcutsCreatedInFolder",
                        displayName,
                        results.Count)
                    : _localizationService.Format(
                        move
                            ? "Widget.MovedToFolder"
                            : "Widget.CopiedToFolder",
                        displayName,
                        results.Count);
                string feedbackId = createShortcuts
                    ? "elevated-outbound-shortcut"
                    : move
                        ? "elevated-outbound-move"
                        : "elevated-outbound-copy";
                ShowFeedback(new WidgetFeedbackRequest(
                    feedbackMessage,
                    WidgetFeedbackSeverity.Success,
                    feedbackId));

                if (createShortcuts)
                {
                    DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
                }
                else
                {
                    DataPackageOperation dropOp = move
                        ? DataPackageOperation.Move
                        : DataPackageOperation.Copy;
                    DataPackageOperation elevatedAllowedOps =
                        FileItemDragPackage.IsProtectedCopyOnlySource(validPaths)
                            ? DataPackageOperation.Copy
                            : DataPackageOperation.Copy |
                              DataPackageOperation.Move |
                              DataPackageOperation.Link;
                    var releaseSnapshot = new DropReleaseSnapshot(
                        DropTargetWindowKind.ExplorerFileView,
                        EscapePressed: false,
                        CursorPoint: new Win32Helper.POINT { X = screenX, Y = screenY },
                        IsDesktopShellView: target.Kind == ElevatedOutboundTargetKind.Desktop);
                    bool showedGuide = TryShowContextualDragOutGuide(
                        dropOp,
                        _settingsService.Settings.FileWidget.ManagedDragOutAction,
                        WindowsCompatibilityService.IsWindows11OrLater,
                        releaseSnapshot,
                        validPaths,
                        handledAsStackMembership: false,
                        hasStorageItems: true);
                    if (!showedGuide &&
                        _settingsService.Settings.FileWidget.DragOutResultHintEnabled &&
                        move &&
                        undoEntry is { CanUndo: true, IsUndone: false })
                    {
                        string undoHistoryId = undoEntry.Id;
                        DesktopToastHudService.Shared.TryShow(
                            new DesktopToastRequest(
                                HintId: "elevated-outbound-move-undo-receipt",
                                Message: feedbackMessage,
                                Severity: DesktopToastSeverity.Success,
                                Priority: DesktopToastPriority.ContextualGuide,
                                ActionButtonText: T("Widget.DragHud.Action.Undo"),
                                OnActionAsync: () => ExecuteUndoByHistoryIdAsync(undoHistoryId),
                                OnDismissAsync: DisableDragOutResultHintAsync,
                                DurationMs: 5000),
                            _settingsService);
                    }
                    else if (!showedGuide)
                    {
                        MaybeShowDragOutResultHint(
                            dropOp,
                            elevatedAllowedOps,
                            move ? ExternalDragObservation.Full : ExternalDragObservation.Brief,
                            handledAsStackMembership: false,
                            DropTargetWindowKind.ExplorerFileView);
                    }
                }
            }

            await CompleteTrackedImportAsync(ImportCompletionState.Completed);
            App.Log(
                $"[DragProtocol] stage=ElevatedOutboundTransferCompleted widget={WidgetId} " +
                $"kind={target.Kind} count={results.Count} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return move && results.Count > 0;
        }
        catch (FileService.FileTransferCanceledException canceledEx)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            await ReconcilePartialElevatedOutboundResultsAsync(
                canceledEx.CompletedResults,
                move,
                isDesktop,
                preExistingMergeSnapshots);
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(ImportCompletionState.Canceled);
            }

            App.Log(
                $"[DragProtocol] stage=ElevatedOutboundTransferCanceled widget={WidgetId} " +
                $"completed={canceledEx.CompletedResults.Count} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return move && canceledEx.CompletedResults.Count > 0;
        }
        catch (OperationCanceledException)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            await RefreshAfterInterruptedFolderImportAsync();
            if (_activeImportCancellation is not null)
            {
                await CompleteTrackedImportAsync(ImportCompletionState.Canceled);
            }

            App.Log(
                $"[DragProtocol] stage=ElevatedOutboundTransferCanceled widget={WidgetId} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return false;
        }
        catch (Exception ex)
        {
            DesktopToastHudService.Shared.HideIfMatchingHint("drag-out-modifier-tip");
            if (ex is FileService.IFileTransferWithCompletedResults
                {
                    CompletedResults: { Count: > 0 } partialResults,
                })
            {
                await ReconcilePartialElevatedOutboundResultsAsync(
                    partialResults,
                    move,
                    isDesktop,
                    preExistingMergeSnapshots);
            }

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

    private async Task ReconcilePartialElevatedOutboundResultsAsync(
        IReadOnlyList<FileService.FileTransferResult> completedResults,
        bool move,
        bool isDesktop,
        IReadOnlyDictionary<string, HashSet<string>?> preExistingMergeSnapshots)
    {
        if (completedResults.Count == 0)
        {
            return;
        }

        string[] destinations = completedResults
            .Select(static item => item.DestinationPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (isDesktop && destinations.Length > 0)
        {
            SuppressDesktopDragOutArrivals(destinations);
        }

        if (!move)
        {
            return;
        }

        await ViewModel.RecordSubfolderMoveUndoAsync(
            completedResults,
            preExistingMergeSnapshots,
            sourceWidgetId: WidgetId);
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
