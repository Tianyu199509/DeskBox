using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace DeskBox.Helpers;

/// <summary>
/// Windows 11 Fluent 2 motion and micro-interaction helper built strictly on
/// NativeAOT-safe <see cref="Microsoft.UI.Composition"/> and built-in theme transitions.
/// Automatically honors Windows Accessibility (<see cref="UISettings.AnimationsEnabled"/>).
/// </summary>
public static class FluentMotionHelper
{
    public static bool AreAnimationsEnabled
    {
        get
        {
            try
            {
                return new UISettings().AnimationsEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    public static TransitionCollection? CreateListTransitions()
    {
        if (!AreAnimationsEnabled)
        {
            return null;
        }

        return
        [
            new AddDeleteThemeTransition(),
            new RepositionThemeTransition()
        ];
    }

    public static void PlayEntranceSlideAndFade(
        UIElement? element,
        float offsetX = 0f,
        float offsetY = 12f,
        int durationMs = 190)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, new Vector3(offsetX, offsetY, 0f));
            slide.InsertKeyFrame(1f, Vector3.Zero, decelerate);
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, decelerate);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation("Translation", slide);
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
        catch
        {
        }
    }

    public static void AttachSmoothBoundsImplicitAnimations(UIElement? element, int durationMs = 220)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector2KeyFrameAnimation sizeAnim = compositor.CreateVector2KeyFrameAnimation();
            sizeAnim.InsertExpressionKeyFrame(1f, "this.FinalValue", ease);
            sizeAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            sizeAnim.Target = nameof(Visual.Size);

            Vector3KeyFrameAnimation offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertExpressionKeyFrame(1f, "this.FinalValue", ease);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
            offsetAnim.Target = nameof(Visual.Offset);

            ImplicitAnimationCollection group = compositor.CreateImplicitAnimationCollection();
            group[nameof(Visual.Size)] = sizeAnim;
            group[nameof(Visual.Offset)] = offsetAnim;
            visual.ImplicitAnimations = group;
        }
        catch
        {
        }
    }

    public static void PlayDesktopArrivalPulse(UIElement? rootElement)
    {
        if (rootElement is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(rootElement);
            Compositor compositor = visual.Compositor;

            if (rootElement is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
            }

            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.965f, 0.965f, 1f));
            scale.InsertKeyFrame(0.65f, new Vector3(1.008f, 1.008f, 1f), decelerate);
            scale.InsertKeyFrame(1f, Vector3.One, decelerate);
            scale.Duration = TimeSpan.FromMilliseconds(260);

            visual.StartAnimation(nameof(Visual.Scale), scale);
        }
        catch
        {
        }
    }

    public static void AnimateHoverScale(UIElement? element, float targetScale, int durationMs = 150)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
            }

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f), ease);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation(nameof(Visual.Scale), anim);
        }
        catch
        {
        }
    }
}
