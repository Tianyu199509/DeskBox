using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan ScrollBarAutoHideDelay =
        TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _scrollBarHideTimer = new()
    {
        Interval = ScrollBarAutoHideDelay
    };

    private void RegisterScrollBarActivityTracking(ListViewBase itemsView)
    {
        itemsView.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(ItemsView_ScrollBarActivity),
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ItemsView_ScrollBarActivity),
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(ItemsView_ScrollBarActivity),
            handledEventsToo: true);
        itemsView.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(ItemsView_IconSizePointerWheel),
            handledEventsToo: true);

        if (!_scrollBarHideTimer.IsEnabled)
        {
            _scrollBarHideTimer.Tick -= ScrollBarHideTimer_Tick;
            _scrollBarHideTimer.Tick += ScrollBarHideTimer_Tick;
        }

        if (!ViewModel.ScrollBarsEnabled)
        {
            // Setting off: registration pins the whole surface to Hidden.
            HideSurfaceScrollBars();
        }
    }

    private void ItemsView_ScrollBarActivity(
        object sender,
        PointerRoutedEventArgs e)
    {
        ShowScrollBarTemporarily(sender as ListViewBase);
    }

    private void ShowScrollBarTemporarily(ListViewBase? itemsView)
    {
        if (_isDisposed || !IsLoaded)
        {
            return;
        }

        if (!ViewModel.ScrollBarsEnabled)
        {
            // Setting off: never switch to Auto and never arm the hide timer.
            StopScrollBarHideTimer();
            HideSurfaceScrollBars();
            return;
        }

        SetVerticalScrollBarVisibility(
            itemsView ?? GetActiveItemsView(),
            ScrollBarVisibility.Auto);
        _scrollBarHideTimer.Stop();
        _scrollBarHideTimer.Start();
    }

    private void ScrollBarHideTimer_Tick(object? sender, object e)
    {
        HideInactiveScrollBars();
    }

    private void HideInactiveScrollBars()
    {
        StopScrollBarHideTimer();
        HideSurfaceScrollBars();
    }

    private void HideSurfaceScrollBars()
    {
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

    /// <summary>
    /// Overflow policy for surfaces whose bar follows the content size instead
    /// of pointer activity (the stack popover). With the setting off the bar
    /// stays Hidden so no path can flip it back to Auto.
    /// </summary>
    private ScrollBarVisibility ResolveOverflowScrollBarVisibility(bool hasOverflow)
    {
        if (!ViewModel.ScrollBarsEnabled)
        {
            return ScrollBarVisibility.Hidden;
        }

        return hasOverflow
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
    }

    private void DisposeScrollBarActivityTracking()
    {
        StopScrollBarHideTimer();
        _scrollBarHideTimer.Tick -= ScrollBarHideTimer_Tick;
    }

    private static void SetVerticalScrollBarVisibility(
        ListViewBase itemsView,
        ScrollBarVisibility visibility)
    {
        itemsView.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            visibility);
    }
}
