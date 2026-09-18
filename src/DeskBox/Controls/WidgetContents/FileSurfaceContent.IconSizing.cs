using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private const int StandardMouseWheelDelta = 120;
    private int _iconSizeWheelAccumulator;

    private void ItemsView_IconSizePointerWheel(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_isDisposed ||
            !Win32Helper.IsKeyPressed(VirtualKey.Control) ||
            sender is not ListViewBase itemsView)
        {
            _iconSizeWheelAccumulator = 0;
            return;
        }

        e.Handled = true;
        int delta = e.GetCurrentPoint(itemsView).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (_iconSizeWheelAccumulator != 0 &&
            Math.Sign(_iconSizeWheelAccumulator) != Math.Sign(delta))
        {
            _iconSizeWheelAccumulator = 0;
        }

        _iconSizeWheelAccumulator += delta;
        if (Math.Abs(_iconSizeWheelAccumulator) < StandardMouseWheelDelta)
        {
            return;
        }

        int direction = Math.Sign(_iconSizeWheelAccumulator);
        _iconSizeWheelAccumulator %= StandardMouseWheelDelta;
        double next = FileWidgetIconSizePolicy.GetNext(
            ViewModel.EffectiveIconSize,
            direction);
        if (Math.Abs(next - ViewModel.EffectiveIconSize) < 0.01)
        {
            return;
        }

        FrameworkElement? anchorElement = FindItemElement(e.OriginalSource);
        WidgetItem? anchorItem = anchorElement?.DataContext as WidgetItem;
        double? anchorTop = TryGetElementTop(anchorElement, itemsView);
        if (!ViewModel.SetIconSizeOverride(next))
        {
            return;
        }

        ShowFeedback(new WidgetFeedbackRequest(
            $"{T("Settings.IconSize.Title")} {next:0}",
            WidgetFeedbackSeverity.Info,
            "file-icon-size"));
        RestoreWheelAnchorAfterLayout(itemsView, anchorItem, anchorTop);
    }

    private void RestoreWheelAnchorAfterLayout(
        ListViewBase itemsView,
        WidgetItem? anchorItem,
        double? previousTop)
    {
        if (anchorItem is null || previousTop is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed)
            {
                return;
            }

            itemsView.UpdateLayout();
            FrameworkElement? current =
                itemsView.ContainerFromItem(anchorItem) as FrameworkElement;
            double? currentTop = TryGetElementTop(current, itemsView);
            ScrollViewer? scrollViewer = FindVisualDescendant<ScrollViewer>(itemsView);
            if (currentTop is null || scrollViewer is null)
            {
                return;
            }

            double offset = Math.Max(
                0,
                scrollViewer.VerticalOffset + currentTop.Value - previousTop.Value);
            scrollViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset: offset,
                zoomFactor: null,
                disableAnimation: true);
        });
    }

    private static double? TryGetElementTop(
        FrameworkElement? element,
        UIElement relativeTo)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            Point point = element.TransformToVisual(relativeTo)
                .TransformPoint(new Point(0, 0));
            return point.Y;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
