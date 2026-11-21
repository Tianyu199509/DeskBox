using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace DeskBox.Controls;

public sealed partial class WidgetShell
{
    private readonly WidgetCompositionResources _compositionResources = new();

    // Permanent window teardown only. Brief hiding keeps templates warm.
    internal void ReleaseOwnedCompositionResources()
    {
        try
        {
            SuspendVisualActivity();
            ReleaseTitleBarClickCollapseTimer();
            ReleaseCompactMarqueeDelayTimer();
            StopDeferredExpandedContentRevealAnimation();
            GroupTitleSwitcher?.ReleaseVisualHandlers();
            StopCompactCompositionTransitionAnimations(force: true);
            GroupDropPreview?.Release();
            ClearAllEventSubscribers();
            Bindings?.StopTracking();
            DataContext = null;
        }
        finally
        {
            try { StopCompactMarquee(); }
            finally { _compositionResources.Dispose(); }
        }
    }

    private void ClearAllEventSubscribers()
    {
        AddRequested = null;
        QuickSettingsRequested = null;
        PositionLockRequested = null;
        SizeLockRequested = null;
        MoreRequested = null;
        CloseRequested = null;
        CollapseRequested = null;
        ExpandRequested = null;
        CompactBodyExpandRequested = null;
        CompactPreviousRequested = null;
        CompactPrimaryActionRequested = null;
        CompactPlayPauseRequested = null;
        CompactNextRequested = null;
        CompactPointerEntered = null;
        CompactPointerMoved = null;
        CompactPointerExited = null;
        CompactExpansionPointerEntered = null;
        CompactExpansionPointerExited = null;
        CompactPointerPressed = null;
        CompactActionPointerEntered = null;
        CompactActionPointerExited = null;
        CompactMoveHandlePointerEntered = null;
        CompactMoveHandlePointerExited = null;
        CompactDragEntered = null;
        CompactDragLeft = null;
        CompactDropCompleted = null;
        TitleDoubleTapped = null;
        TitleRightTapped = null;
        TitlePointerPressed = null;
        TitlePointerMoved = null;
        TitlePointerReleased = null;
        DragHandlePointerPressed = null;
        DragHandlePointerMoved = null;
        DragHandlePointerReleased = null;
        GroupMemberInvoked = null;
        GroupMemberRemoveRequested = null;
        GroupMemberDetachRequested = null;
        GroupMemberDetachDragStarted = null;
        GroupMemberDetachDragCompleted = null;
        GroupMemberReorderRequested = null;
        GroupDissolveRequested = null;
        GroupPickerOpened = null;
        GroupPickerClosed = null;
        HostedContentChanged = null;
        EffectiveMaterialCornerRadiusChanged = null;
        OverlayDragCueArmRequested = null;
    }

    /// <summary>
    /// Creates the shared compact-transition animation templates during the
    /// expansion warm-up so the first real transition does not pay WinRT
    /// animation-object creation inside its opening frames.
    /// </summary>
    internal void PrewarmCompactTransitionCompositionResources()
    {
        try
        {
            Compositor compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _ = _compositionResources.GetScalar(
                compositor, WidgetAnimationTemplate.CompactOpacity);
            _ = _compositionResources.GetVector3(
                compositor, WidgetAnimationTemplate.CompactScale);
            _ = _compositionResources.GetVector3(
                compositor, WidgetAnimationTemplate.CompactTranslation);
            _ = _compositionResources.GetVector2(
                compositor, WidgetAnimationTemplate.CompactClipCornerRadius);
            _ = _compositionResources.GetCompactClip(compositor);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Composition] Compact transition prewarm failed: {ex.Message}");
        }
    }
}
