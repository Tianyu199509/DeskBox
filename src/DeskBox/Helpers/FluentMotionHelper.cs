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

    public static void AnimateStageFocus(
        UIElement? element,
        float scale,
        float offsetX,
        float offsetY,
        float opacity,
        int durationMs = 380)
    {
        if (element is null)
        {
            return;
        }

        if (!AreAnimationsEnabled)
        {
            element.Opacity = opacity;
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
            }

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.3f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
            scaleAnim.Duration = duration;

            Vector3KeyFrameAnimation translateAnim = compositor.CreateVector3KeyFrameAnimation();
            translateAnim.InsertKeyFrame(1f, new Vector3(offsetX, offsetY, 0f), ease);
            translateAnim.Duration = duration;

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, opacity, ease);
            opacityAnim.Duration = duration;

            visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            visual.StartAnimation("Translation", translateAnim);
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
        }
        catch
        {
            element.Opacity = opacity;
        }
    }

    public static void PlayConduitPulse(
        UIElement? element,
        float fromX,
        float toX,
        int durationMs = 760)
    {
        if (element is null)
        {
            return;
        }

        if (!AreAnimationsEnabled)
        {
            element.Opacity = 0.85;
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.9f),
                new Vector2(0.25f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation moveAnim = compositor.CreateVector3KeyFrameAnimation();
            moveAnim.InsertKeyFrame(0f, new Vector3(fromX, 0f, 0f));
            moveAnim.InsertKeyFrame(1f, new Vector3(toX, 0f, 0f), ease);
            moveAnim.Duration = duration;

            ScalarKeyFrameAnimation fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.InsertKeyFrame(0f, 0f);
            fadeAnim.InsertKeyFrame(0.22f, 1f, ease);
            fadeAnim.InsertKeyFrame(0.78f, 1f, ease);
            fadeAnim.InsertKeyFrame(1f, 0.18f, ease);
            fadeAnim.Duration = duration;

            visual.StartAnimation("Translation", moveAnim);
            visual.StartAnimation(nameof(Visual.Opacity), fadeAnim);
        }
        catch
        {
        }
    }

    public static void AnimateCardElevation(
        UIElement? element,
        float offsetY,
        float scale = 1f,
        int durationMs = 160)
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

            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
            }

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.3f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation translateAnim = compositor.CreateVector3KeyFrameAnimation();
            translateAnim.InsertKeyFrame(1f, new Vector3(0f, offsetY, 0f), ease);
            translateAnim.Duration = duration;

            Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
            scaleAnim.Duration = duration;

            visual.StartAnimation("Translation", translateAnim);
            visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
        }
        catch
        {
        }
    }
}
