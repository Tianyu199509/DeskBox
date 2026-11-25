using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    internal bool IsExpandedCapsule =>
        _collapseInitialized &&
        EffectiveCollapseBehavior is WidgetCollapseBehavior.Click or WidgetCollapseBehavior.Smart &&
        !IsSettledCollapsedCapsule;

    private void SettleCollapseAnimationBeforeBoundsInteraction()
    {
        if (_isCollapseAnimationRendering)
        {
            StopCollapseAnimation(cancelShellTransition: true);
        }
    }

    private bool EvaluateCompactPointerOwnership(
        bool pointerInsideWindow,
        IntPtr pointerRootWindow,
        bool clearPendingHoverOnPeerOcclusion)
    {
        bool nativeRootCanReceivePointer = CanReceivePointerFromRootWindow(pointerRootWindow);
        bool isOccludedByPeerDeskBoxWindow =
            !nativeRootCanReceivePointer &&
            WidgetLayerService.IsPeerDeskBoxWindow(pointerRootWindow, HWnd);
        bool trusted = WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
            pointerInsideWindow,
            nativeRootCanReceivePointer,
            HasRecentCompactRoutedPointerAuthority(),
            isOccludedByPeerDeskBoxWindow);
        if (!trusted && clearPendingHoverOnPeerOcclusion && isOccludedByPeerDeskBoxWindow)
        {
            CancelTimer(ref _collapseHoverTimer);
            ClearCompactRoutedPointerAuthority();
        }

        return trusted;
    }

    private void GuardHiddenExpandedLayerRestore(out bool shouldAbortRestore)
    {
        shouldAbortRestore = !Visible || IsHideAnimationRunning || IsClosing;
        if (shouldAbortRestore)
        {
            ReleaseExpandedWidgetLayerLease("expanded-state-hidden");
        }
    }
}
