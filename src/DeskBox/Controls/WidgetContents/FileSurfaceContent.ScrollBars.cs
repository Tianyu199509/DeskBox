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
