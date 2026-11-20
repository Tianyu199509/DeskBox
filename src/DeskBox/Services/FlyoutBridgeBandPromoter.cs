using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// The stack popover lives in the TOPMOST band (StackPopoverHostWindow pins
/// it at construction), but WinUI shows every FlyoutBase in its own
/// "Microsoft.UI.Content.PopupWindowSiteBridge" window that stays in the
/// normal band — owned by the widget window, not the popover. A menu opened
/// from the popover therefore renders beneath it and cannot be clicked
/// (live-captured 2026-10-21: the bridge pair is an ~80px icon-strip window
/// plus a ~300px row-area window, and owner-following z transactions on the
/// widget can additionally strip the popover's own topmost bit, which made
/// the failure look intermittent between old and new stacks). While a
/// popover-hosted flyout is open, every visible bridge of this process is
/// lifted into the topmost band and dropped back when the menu closes. A
/// short poll covers submenu bridges, which get their own window and no
/// Opened hook the host could attach to.
/// </summary>
internal sealed class FlyoutBridgeBandPromoter : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly HashSet<IntPtr> _promoted = [];

    private FlyoutBridgeBandPromoter(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => PromoteNewBridges();
    }

    public static FlyoutBridgeBandPromoter? TryBegin()
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return null;
        }

        var promoter = new FlyoutBridgeBandPromoter(queue);
        promoter.PromoteNewBridges();
        promoter._timer.Start();
        return promoter;
    }

    private void PromoteNewBridges()
    {
        // Pause topmost bridge promotion while an OLE or pointer drag is active so
        // popup bridges never paint over Explorer's SysDragImage / drag ghost.
        if (ActiveDeskBoxDragRegistry.HasActiveDrag ||
            ActiveDeskBoxDragRegistry.HasActivePointerDragSession)
        {
            return;
        }

        foreach (IntPtr bridge in Win32Helper.GetVisiblePopupBridgeWindows())
        {
            if (!Win32Helper.IsWindowTopMost(bridge) && _promoted.Add(bridge))
            {
                Win32Helper.SetWindowTopMost(bridge, showWindow: false);
            }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (IntPtr bridge in _promoted)
        {
            if (Win32Helper.IsWindow(bridge))
            {
                Win32Helper.ClearWindowTopMost(bridge);
            }
        }

        _promoted.Clear();
    }
}
