using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DeskBox.Helpers;

internal static class DetailPageTransitionHelper
{
    private const int EnterDurationMs = 210;
    private const int ExitDurationMs = 150;
    private const float EnterOffsetY = 10f;
    private const float ExitOffsetY = 7f;

    public static void PlayEnter(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(EnterDurationMs);
        opacityAnimation.InsertKeyFrame(0f, 0.28f);
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);

        var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = TimeSpan.FromMilliseconds(EnterDurationMs);
        translationAnimation.InsertKeyFrame(0f, new Vector3(0, EnterOffsetY, 0));
        translationAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);

        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        visual.Opacity = 1f;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Translation", translationAnimation);
    }

    public static async Task PlayExitAsync(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");

        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f),
            new Vector2(0.6f, 1f));
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(ExitDurationMs);
        opacityAnimation.InsertKeyFrame(0f, 1f);
        opacityAnimation.InsertKeyFrame(1f, 0f, easing);

        var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = TimeSpan.FromMilliseconds(ExitDurationMs);
        translationAnimation.InsertKeyFrame(0f, Vector3.Zero);
        translationAnimation.InsertKeyFrame(1f, new Vector3(0, ExitOffsetY, 0), easing);

        element.Translation = new Vector3(0, ExitOffsetY, 0);
        visual.Opacity = 0f;
        visual.StartAnimation("Opacity", opacityAnimation);
        visual.StartAnimation("Translation", translationAnimation);

        await Task.Delay(ExitDurationMs);
    }

    public static void Reset(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        element.Opacity = 1;
        element.Translation = Vector3.Zero;
        visual.Opacity = 1f;
    }
}
