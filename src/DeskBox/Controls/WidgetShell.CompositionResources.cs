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
            StopCompactCompositionTransitionAnimations(force: true);
            StopGroupDropPreviewBreathing();
        }
        finally
        {
            try { StopParticles(); }
            finally { _compositionResources.Dispose(); }
        }
    }

    private void ReleaseParticleAnimation(CompactParticleAnimationState particle)
    {
        CompositionScopedBatch? batch = particle.Batch;
        Vector3KeyFrameAnimation? animation = particle.Animation;
        particle.Batch = null;
        particle.Animation = null;
        try
        {
            if (batch is not null)
            {
                batch.Completed -= ParticleAnimationBatch_Completed;
            }
            if (animation is not null)
            {
                ElementCompositionPreview.GetElementVisual(particle.Shape)
                    .StopAnimation("Translation");
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Composition] Particle detach failed during cleanup: {ex.Message}");
        }
        finally
        {
            WidgetCompositionResources.Release(batch);
            WidgetCompositionResources.Release(animation);
        }
    }
}
