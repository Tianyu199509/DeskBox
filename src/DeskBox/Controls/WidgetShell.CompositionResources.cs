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
            GroupTitleSwitcher?.ReleaseVisualHandlers();
            StopCompactCompositionTransitionAnimations(force: true);
            StopGroupDropPreviewBreathing();
        }
        finally
        {
            try { StopCompactMarquee(); }
            finally { _compositionResources.Dispose(); }
        }
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
