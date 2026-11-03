using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private enum InteractiveAutoScrollMode
    {
        None = 0,
        MainSurfaceDrag = 1,
        StackPopoverDrag = 2,
        MarqueeSelection = 3,
    }

    private static readonly TimeSpan ScrollBarAutoHideDelay =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InteractiveAutoScrollInterval =
        TimeSpan.FromMilliseconds(32);

    private readonly DispatcherTimer _scrollBarHideTimer = new()
    {
        Interval = ScrollBarAutoHideDelay
    };

    private readonly DispatcherTimer _interactiveAutoScrollTimer = new()
    {
        Interval = InteractiveAutoScrollInterval
    };

    private InteractiveAutoScrollMode _autoScrollMode;
    private ListViewBase? _autoScrollTargetView;
    private bool _autoScrollTimerWired;

    private PointerEventHandler? _scrollBarActivityPointerHandler;
    private PointerEventHandler? _iconSizeWheelPointerHandler;

    private void RegisterScrollBarActivityTracking(ListViewBase itemsView)
    {
        _scrollBarActivityPointerHandler ??= new PointerEventHandler(ItemsView_ScrollBarActivity);
        _iconSizeWheelPointerHandler ??= new PointerEventHandler(ItemsView_IconSizePointerWheel);
        itemsView.AddHandler(
            UIElement.PointerMovedEvent,
            _scrollBarActivityPointerHandler,
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerPressedEvent,
            _scrollBarActivityPointerHandler,
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _scrollBarActivityPointerHandler,
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _iconSizeWheelPointerHandler,
            handledEventsToo: true);

        if (!_scrollBarHideTimer.IsEnabled)
        {
            _scrollBarHideTimer.Tick -= ScrollBarHideTimer_Tick;
            _scrollBarHideTimer.Tick += ScrollBarHideTimer_Tick;
        }
    }

    private void UnregisterScrollBarActivityTracking(ListViewBase? itemsView)
    {
        if (itemsView is null)
        {
            return;
        }

        if (_scrollBarActivityPointerHandler is not null)
        {
            itemsView.RemoveHandler(
                UIElement.PointerMovedEvent,
                _scrollBarActivityPointerHandler);
            itemsView.RemoveHandler(
                UIElement.PointerPressedEvent,
                _scrollBarActivityPointerHandler);
            itemsView.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                _scrollBarActivityPointerHandler);
        }

        if (_iconSizeWheelPointerHandler is not null)
        {
            itemsView.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                _iconSizeWheelPointerHandler);
        }
    }

    private void ItemsView_ScrollBarActivity(
        object sender,
        PointerRoutedEventArgs e)
    {
        ShowScrollBarTemporarily(sender as ListViewBase);
    }

    private long _lastScrollBarActivityTick64;

    private void ShowScrollBarTemporarily(ListViewBase? itemsView)
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        ListViewBase target = itemsView ?? GetActiveItemsView();
        SetVerticalScrollBarVisibility(
            target,
            ScrollBarVisibility.Auto);
        long now = Environment.TickCount64;
        if (!_scrollBarHideTimer.IsEnabled ||
            now - _lastScrollBarActivityTick64 >= 250)
        {
            _lastScrollBarActivityTick64 = now;
            _scrollBarHideTimer.Stop();
            _scrollBarHideTimer.Start();
        }
    }

    private void ScrollBarHideTimer_Tick(object? sender, object e)
    {
        HideInactiveScrollBars();
    }

    private void HideInactiveScrollBars()
    {
        StopScrollBarHideTimer();
        SetVerticalScrollBarVisibility(ItemsGrid, ScrollBarVisibility.Hidden);
        SetVerticalScrollBarVisibility(ItemsList, ScrollBarVisibility.Hidden);
        if (_stackPopoverItemsView is { } stackPopoverItemsView)
        {
            SetVerticalScrollBarVisibility(
                stackPopoverItemsView,
                ScrollBarVisibility.Hidden);
        }
    }

    private void StopScrollBarHideTimer()
    {
        _scrollBarHideTimer.Stop();
    }

    internal static Windows.Foundation.Point ShiftMarqueeAnchorForScroll(
        Windows.Foundation.Point startPoint,
        double actualScrolledX,
        double actualScrolledY) =>
        new(startPoint.X - actualScrolledX, startPoint.Y - actualScrolledY);

    private void BeginSurfaceDragAutoScroll()
    {
        if (_isDisposed || !IsLoaded || _elevatedDragSession is not null)
        {
            return;
        }

        ListViewBase view = GetMainItemsView();
        ShowScrollBarTemporarily(view);
        if (GetRenderWindowScrollViewer(view) is { } scrollViewer &&
            (scrollViewer.ScrollableHeight > 0.5 || scrollViewer.ScrollableWidth > 0.5))
        {
            StartInteractiveAutoScroll(InteractiveAutoScrollMode.MainSurfaceDrag, view);
        }
        else
        {
            StopInteractiveAutoScroll(InteractiveAutoScrollMode.MainSurfaceDrag);
        }
    }

    private void BeginStackPopoverDragAutoScroll()
    {
        if (_isDisposed || !IsLoaded || _elevatedDragSession is not null ||
            _stackPopoverItemsView is not { } popoverView)
        {
            return;
        }

        ShowScrollBarTemporarily(popoverView);
        if (GetRenderWindowScrollViewer(popoverView) is { } scrollViewer &&
            (scrollViewer.ScrollableHeight > 0.5 || scrollViewer.ScrollableWidth > 0.5))
        {
            StartInteractiveAutoScroll(InteractiveAutoScrollMode.StackPopoverDrag, popoverView);
        }
        else
        {
            StopInteractiveAutoScroll(InteractiveAutoScrollMode.StackPopoverDrag);
        }
    }

    private void BeginMarqueeAutoScroll(ListViewBase listView)
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        ShowScrollBarTemporarily(listView);
        StartInteractiveAutoScroll(InteractiveAutoScrollMode.MarqueeSelection, listView);
    }

    private void StartInteractiveAutoScroll(
        InteractiveAutoScrollMode mode,
        ListViewBase targetView)
    {
        _autoScrollMode = mode;
        _autoScrollTargetView = targetView;
        if (!_autoScrollTimerWired)
        {
            _interactiveAutoScrollTimer.Tick += InteractiveAutoScrollTimer_Tick;
            _autoScrollTimerWired = true;
        }

        if (!_interactiveAutoScrollTimer.IsEnabled)
        {
            _interactiveAutoScrollTimer.Start();
        }
    }

    private void StopInteractiveAutoScroll(InteractiveAutoScrollMode? onlyIfMode = null)
    {
        if (onlyIfMode.HasValue && _autoScrollMode != onlyIfMode.Value)
        {
            return;
        }

        _autoScrollMode = InteractiveAutoScrollMode.None;
        _autoScrollTargetView = null;
        _interactiveAutoScrollTimer.Stop();
    }

    private void InteractiveAutoScrollTimer_Tick(object? sender, object e)
    {
        if (_isDisposed || !IsLoaded ||
            _autoScrollMode == InteractiveAutoScrollMode.None ||
            _autoScrollTargetView is not { } view ||
            !Win32Helper.IsAnyMouseButtonDown() ||
            !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            StopInteractiveAutoScroll();
            return;
        }

        switch (_autoScrollMode)
        {
            case InteractiveAutoScrollMode.MainSurfaceDrag:
            {
                if (_hostWindowHandle == IntPtr.Zero ||
                    !TryGetScreenPointInElement(view, _hostWindowHandle, cursor.X, cursor.Y, out Windows.Foundation.Point pt))
                {
                    return;
                }

                if (TryAutoScrollListView(view, pt))
                {
                    ShowScrollBarTemporarily(view);
                    if (_isSurfaceReorderDragActive)
                    {
                        _surfaceReorderHasLastPosition = false;
                        UpdateSurfaceReorderPreview(pt);
                    }
                    else if (_externalDropHasLastPosition)
                    {
                        _externalDropHasLastPosition = false;
                        UpdateExternalDropPreview(_externalDropPathHints, pt);
                    }
                }
                break;
            }

            case InteractiveAutoScrollMode.StackPopoverDrag:
            {
                if (_stackPopoverHostWindow is not { IsVisible: true } host ||
                    host.WindowHandle == IntPtr.Zero ||
                    !TryGetScreenPointInElement(view, host.WindowHandle, cursor.X, cursor.Y, out Windows.Foundation.Point pt))
                {
                    return;
                }

                if (TryAutoScrollListView(view, pt))
                {
                    ShowScrollBarTemporarily(view);
                    if (_stackPopoverReorderInsertionIndex >= 0)
                    {
                        _stackPopoverReorderInsertionIndex = ReorderDropIndexCalculator.Compute(
                            view,
                            pt,
                            _stackPopoverReorderInsertionIndex);
                        UpdateStackPopoverReorderIndicator(view, pt, _stackPopoverReorderInsertionIndex);
                    }
                }
                break;
            }

            case InteractiveAutoScrollMode.MarqueeSelection:
            {
                if (!_isBoxSelecting)
                {
                    StopInteractiveAutoScroll(InteractiveAutoScrollMode.MarqueeSelection);
                    return;
                }

                IntPtr hwnd = ReferenceEquals(view, _stackPopoverItemsView)
                    ? (_stackPopoverHostWindow?.WindowHandle ?? IntPtr.Zero)
                    : _hostWindowHandle;
                if (hwnd == IntPtr.Zero ||
                    GetRenderWindowScrollViewer(view) is not { } scrollViewer ||
                    !TryGetUnclampedScreenPointInElement(view, hwnd, cursor.X, cursor.Y, out Windows.Foundation.Point ptInView))
                {
                    return;
                }

                double beforeX = scrollViewer.HorizontalOffset;
                double beforeY = scrollViewer.VerticalOffset;
                double deltaY = ComputeAutoScrollDelta(
                    ptInView.Y,
                    scrollViewer.ViewportHeight,
                    beforeY,
                    scrollViewer.ScrollableHeight);
                double deltaX = ComputeAutoScrollDelta(
                    ptInView.X,
                    scrollViewer.ViewportWidth,
                    beforeX,
                    scrollViewer.ScrollableWidth);
                if (Math.Abs(deltaY) < 0.1 && Math.Abs(deltaX) < 0.1)
                {
                    return;
                }

                double targetX = Math.Clamp(beforeX + deltaX, 0, scrollViewer.ScrollableWidth);
                double targetY = Math.Clamp(beforeY + deltaY, 0, scrollViewer.ScrollableHeight);
                double actualDx = targetX - beforeX;
                double actualDy = targetY - beforeY;
                if (Math.Abs(actualDx) < 0.1 && Math.Abs(actualDy) < 0.1)
                {
                    return;
                }

                _ = scrollViewer.ChangeView(
                    Math.Abs(actualDx) >= 0.1 ? targetX : null,
                    Math.Abs(actualDy) >= 0.1 ? targetY : null,
                    zoomFactor: null,
                    disableAnimation: true);
                ShowScrollBarTemporarily(view);
                _selectionStartPoint = ShiftMarqueeAnchorForScroll(_selectionStartPoint, actualDx, actualDy);
                Canvas overlay = ResolveSelectionOverlay(view);
                if (TryGetUnclampedScreenPointInElement(overlay, hwnd, cursor.X, cursor.Y, out Windows.Foundation.Point ptInOverlay))
                {
                    _selectionCurrentPoint = new Windows.Foundation.Point(
                        Math.Clamp(ptInOverlay.X, 0, Math.Max(0, overlay.ActualWidth)),
                        Math.Clamp(ptInOverlay.Y, 0, Math.Max(0, overlay.ActualHeight)));
                }

                view.UpdateLayout();
                CacheSelectionHits(view, overlay);
                UpdateSelectionRectangle(view);
                ApplySelectionPreview(view);
                break;
            }
        }
    }

    private static bool TryGetUnclampedScreenPointInElement(
        FrameworkElement element,
        IntPtr windowHandle,
        int screenX,
        int screenY,
        out Windows.Foundation.Point point)
    {
        point = default;
        if (windowHandle == IntPtr.Zero ||
            element.Visibility != Visibility.Visible ||
            element.XamlRoot is null ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0 ||
            !Win32Helper.GetWindowRect(windowHandle, out Win32Helper.RECT windowBounds))
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point topLeft = element.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = element.XamlRoot.RasterizationScale;
            if (scale <= 0)
            {
                scale = 1;
            }

            double left = windowBounds.Left + (topLeft.X * scale);
            double top = windowBounds.Top + (topLeft.Y * scale);
            point = new Windows.Foundation.Point(
                (screenX - left) / scale,
                (screenY - top) / scale);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void DisposeScrollBarActivityTracking()
    {
        StopInteractiveAutoScroll();
        if (_autoScrollTimerWired)
        {
            _interactiveAutoScrollTimer.Tick -= InteractiveAutoScrollTimer_Tick;
            _autoScrollTimerWired = false;
        }

        StopScrollBarHideTimer();
        _scrollBarHideTimer.Tick -= ScrollBarHideTimer_Tick;
        UnregisterScrollBarActivityTracking(ItemsGrid);
        UnregisterScrollBarActivityTracking(ItemsList);
        UnregisterScrollBarActivityTracking(_stackPopoverItemsView);
        _scrollBarActivityPointerHandler = null;
        _iconSizeWheelPointerHandler = null;
    }

    private static void SetVerticalScrollBarVisibility(
        ListViewBase itemsView,
        ScrollBarVisibility visibility)
    {
        if (ScrollViewer.GetVerticalScrollBarVisibility(itemsView) != visibility)
        {
            itemsView.SetValue(
                ScrollViewer.VerticalScrollBarVisibilityProperty,
                visibility);
        }
    }
}
