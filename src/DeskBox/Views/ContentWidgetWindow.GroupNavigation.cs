using DeskBox.Controls;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    protected override void OnCompactVisualStateChanged(bool collapsed)
    {
        if (collapsed)
        {
            if (HWnd != IntPtr.Zero)
            {
                WidgetEditorPopoverWindow.ClosePopoversForOwner(HWnd);
            }

            App.Current?.WidgetManager?.CancelWidgetSurfaceSwitch(_config.Id);
        }

        UpdateResizeChromeForCurrentState();
    }

    protected override void OnCollapseBehaviorChanged(WidgetCollapseBehavior behavior)
    {
        UpdateResizeChromeForCurrentState();
    }

    internal void UpdateResizeChromeForCurrentState()
    {
        if (IsResizing || ResizeGrid is null)
        {
            return;
        }

        WidgetCompactResizeChromeMetrics metrics =
            WidgetCompactInteractionPolicy.ResolveResizeChrome(
                IsCompactBoundsStateActive,
                _config.IsSizeLocked,
                isCompactTransitionActive: false);

        ResizeTopRow.Height = new GridLength(metrics.TopBorderHeight);
        ResizeBottomRow.Height = new GridLength(metrics.BottomBorderHeight);
        ResizeLeftColumn.Width = new GridLength(metrics.HorizontalBorderWidth);
        ResizeRightColumn.Width = new GridLength(metrics.HorizontalBorderWidth);

        for (int i = 0; i < ResizeGrid.Children.Count; i++)
        {
            if (ResizeGrid.Children[i] is not FrameworkElement child ||
                child.Tag is not string tag)
            {
                continue;
            }

            bool isVerticalEdge = string.Equals(tag, "Top", StringComparison.Ordinal) ||
                string.Equals(tag, "Bottom", StringComparison.Ordinal);
            child.IsHitTestVisible = isVerticalEdge
                ? metrics.AllowVerticalHitTest
                : metrics.AllowHorizontalHitTest;
        }
    }

    internal void SetGroupMemberLoading(string? widgetId, bool isLoading)
    {
        ContentWidgetShell.SetGroupMemberLoading(widgetId, isLoading);
    }
}
