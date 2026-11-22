using System.Numerics;
using System.Runtime.CompilerServices;
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
    public static bool AreAnimationsEnabled =>
        DeskBox.Services.WindowsCompatibilityService.ShouldAnimate;

    private static void BindCenterPointExpression(Visual visual, Compositor compositor)
    {
        ExpressionAnimation centerExpr = compositor.CreateExpressionAnimation(
            "Vector3(this.Target.Size.X * 0.5f, this.Target.Size.Y * 0.5f, 0.0f)");
        visual.StartAnimation(nameof(Visual.CenterPoint), centerExpr);
    }

    private static void SyncElementCenterPoint(UIElement element)
    {
        if (element is not FrameworkElement fe)
        {
            return;
        }

        double width = fe.ActualWidth > 0 ? fe.ActualWidth : (double.IsFinite(fe.Width) && fe.Width > 0 ? fe.Width : 0);
        double height = fe.ActualHeight > 0 ? fe.ActualHeight : (double.IsFinite(fe.Height) && fe.Height > 0 ? fe.Height : 0);
        if (width > 0 && height > 0)
        {
            fe.CenterPoint = new Vector3((float)(width * 0.5), (float)(height * 0.5), 0f);
            return;
        }

        void OnFirstSizeChanged(object sender, SizeChangedEventArgs e)
        {
            fe.SizeChanged -= OnFirstSizeChanged;
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                fe.CenterPoint = new Vector3((float)(e.NewSize.Width * 0.5), (float)(e.NewSize.Height * 0.5), 0f);
            }
        }

        fe.SizeChanged += OnFirstSizeChanged;
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
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            float baseZ = element.Translation.Z;

            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = nameof(UIElement.Translation);
            slide.InsertKeyFrame(0f, new Vector3(offsetX, offsetY, baseZ));
            slide.InsertKeyFrame(1f, new Vector3(0f, 0f, baseZ), decelerate);
            slide.Duration = TimeSpan.FromMilliseconds(durationMs);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = nameof(UIElement.Opacity);
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, decelerate);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);

            element.Translation = new Vector3(0f, 0f, baseZ);
            element.Opacity = 1.0;
            element.StartAnimation(slide);
            element.StartAnimation(fade);
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
            BindCenterPointExpression(visual, compositor);

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
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            SyncElementCenterPoint(element);

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Target = nameof(UIElement.Scale);
            anim.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f), ease);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);

            element.StartAnimation(anim);
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
            element.Scale = new Vector3(scale, scale, 1f);
            element.Translation = new Vector3(offsetX, offsetY, element.Translation.Z);
            element.Opacity = opacity;
            return;
        }

        try
        {
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            SyncElementCenterPoint(element);
            float baseZ = element.Translation.Z;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.3f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Target = nameof(UIElement.Scale);
            scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
            scaleAnim.Duration = duration;

            Vector3KeyFrameAnimation translateAnim = compositor.CreateVector3KeyFrameAnimation();
            translateAnim.Target = nameof(UIElement.Translation);
            translateAnim.InsertKeyFrame(1f, new Vector3(offsetX, offsetY, baseZ), ease);
            translateAnim.Duration = duration;

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = nameof(UIElement.Opacity);
            opacityAnim.InsertKeyFrame(1f, opacity, ease);
            opacityAnim.Duration = duration;

            element.StartAnimation(scaleAnim);
            element.StartAnimation(translateAnim);
            element.StartAnimation(opacityAnim);
        }
        catch
        {
            element.Translation = new Vector3(offsetX, offsetY, element.Translation.Z);
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
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            float baseZ = element.Translation.Z;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.9f),
                new Vector2(0.25f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation moveAnim = compositor.CreateVector3KeyFrameAnimation();
            moveAnim.Target = nameof(UIElement.Translation);
            moveAnim.InsertKeyFrame(0f, new Vector3(fromX, 0f, baseZ));
            moveAnim.InsertKeyFrame(1f, new Vector3(toX, 0f, baseZ), ease);
            moveAnim.Duration = duration;

            ScalarKeyFrameAnimation fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.Target = nameof(UIElement.Opacity);
            fadeAnim.InsertKeyFrame(0f, 0f);
            fadeAnim.InsertKeyFrame(0.22f, 1f, ease);
            fadeAnim.InsertKeyFrame(0.78f, 1f, ease);
            fadeAnim.InsertKeyFrame(1f, 0.18f, ease);
            fadeAnim.Duration = duration;

            element.StartAnimation(moveAnim);
            element.StartAnimation(fadeAnim);
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
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            SyncElementCenterPoint(element);
            float baseZ = element.Translation.Z;

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.3f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation translateAnim = compositor.CreateVector3KeyFrameAnimation();
            translateAnim.Target = nameof(UIElement.Translation);
            translateAnim.InsertKeyFrame(1f, new Vector3(0f, offsetY, baseZ), ease);
            translateAnim.Duration = duration;

            Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Target = nameof(UIElement.Scale);
            scaleAnim.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
            scaleAnim.Duration = duration;

            element.StartAnimation(translateAnim);
            element.StartAnimation(scaleAnim);
        }
        catch
        {
        }
    }

    private sealed record SkeletonPulseRegistration(int DelayMs, float MinOpacity, float MaxOpacity, int PeriodMs);

    private static readonly ConditionalWeakTable<FrameworkElement, SkeletonPulseRegistration> s_skeletonPulseRegistrations = new();
    private static readonly RoutedEventHandler s_skeletonPulseUnloadedHandler = OnSkeletonPulseElementUnloaded;
    private static readonly RoutedEventHandler s_skeletonPulseLoadedHandler = OnSkeletonPulseElementLoaded;

    private static void OnSkeletonPulseElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            PauseSkeletonPulseVisual(fe);
        }
    }

    private static void OnSkeletonPulseElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe &&
            s_skeletonPulseRegistrations.TryGetValue(fe, out SkeletonPulseRegistration? reg))
        {
            StartSkeletonPulseVisual(fe, reg.DelayMs, reg.MinOpacity, reg.MaxOpacity, reg.PeriodMs);
        }
    }

    public static void StartSkeletonPulse(
        UIElement? element,
        int delayMs = 0,
        float minOpacity = 0.42f,
        float maxOpacity = 0.96f,
        int periodMs = 1350)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        if (element is FrameworkElement fe)
        {
            bool firstRegistration = !s_skeletonPulseRegistrations.TryGetValue(fe, out _);
            s_skeletonPulseRegistrations.AddOrUpdate(
                fe,
                new SkeletonPulseRegistration(delayMs, minOpacity, maxOpacity, periodMs));
            if (firstRegistration)
            {
                fe.Unloaded += s_skeletonPulseUnloadedHandler;
                fe.Loaded += s_skeletonPulseLoadedHandler;
            }
        }

        StartSkeletonPulseVisual(element, delayMs, minOpacity, maxOpacity, periodMs);
    }

    private static void StartSkeletonPulseVisual(
        UIElement element,
        int delayMs,
        float minOpacity,
        float maxOpacity,
        int periodMs)
    {
        if (!AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction easeInOut = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.42f, 0f),
                new Vector2(0.58f, 1f));

            ScalarKeyFrameAnimation pulse = compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0f, maxOpacity, easeInOut);
            pulse.InsertKeyFrame(0.5f, minOpacity, easeInOut);
            pulse.InsertKeyFrame(1f, maxOpacity, easeInOut);
            pulse.Duration = TimeSpan.FromMilliseconds(periodMs);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            if (delayMs > 0)
            {
                pulse.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            }

            visual.StartAnimation(nameof(Visual.Opacity), pulse);
        }
        catch
        {
        }
    }

    private static void PauseSkeletonPulseVisual(UIElement element)
    {
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1f;
        }
        catch
        {
        }
    }

    public static void StopSkeletonPulse(UIElement? element)
    {
        if (element is null)
        {
            return;
        }

        if (element is FrameworkElement fe && s_skeletonPulseRegistrations.Remove(fe))
        {
            fe.Unloaded -= s_skeletonPulseUnloadedHandler;
            fe.Loaded -= s_skeletonPulseLoadedHandler;
        }

        PauseSkeletonPulseVisual(element);
    }

    public static void PlaySubtleFadeIn(UIElement? element, int durationMs = 140)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, decelerate);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
        catch
        {
        }
    }

    public static void PlayViewportMorphIn(UIElement? element, int durationMs = 160)
    {
        if (element is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;
            BindCenterPointExpression(visual, compositor);

            CubicBezierEasingFunction decelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.955f, 0.955f, 1f));
            scale.InsertKeyFrame(1f, Vector3.One, decelerate);
            scale.Duration = duration;

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.15f);
            fade.InsertKeyFrame(1f, 1f, decelerate);
            fade.Duration = duration;

            visual.StartAnimation(nameof(Visual.Scale), scale);
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
        catch
        {
        }
    }

    public static async Task PlayExitSlideAndFadeAsync(
        UIElement? element,
        float offsetX = 0f,
        float offsetY = 10f,
        int durationMs = 130)
    {
        if (element is null || !AreAnimationsEnabled || element.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            float baseZ = element.Translation.Z;

            CubicBezierEasingFunction accelerate = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.4f, 0f),
                new Vector2(1f, 1f));

            TimeSpan duration = TimeSpan.FromMilliseconds(durationMs);

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = nameof(UIElement.Translation);
            slide.InsertKeyFrame(0f, new Vector3(0f, 0f, baseZ));
            slide.InsertKeyFrame(1f, new Vector3(offsetX, offsetY, baseZ), accelerate);
            slide.Duration = duration;

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = nameof(UIElement.Opacity);
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(1f, 0f, accelerate);
            fade.Duration = duration;

            element.StartAnimation(slide);
            element.StartAnimation(fade);

            await Task.Delay(durationMs);

            element.StopAnimation(slide);
            element.StopAnimation(fade);
            element.Translation = new Vector3(0f, 0f, baseZ);
            element.Opacity = 1.0;
        }
        catch
        {
        }
    }

    private static readonly ConditionalWeakTable<UIElement, string> s_canvasAmbientMotionModes = new();

    /// <summary>
    /// Plays a lightweight glyph micro-interaction matched to the button's action semantics:
    /// counterclockwise 360-degree spin for reset glyphs, horizontal push for skip/next glyphs,
    /// and a crisp completion pop for check/confirm glyphs.
    /// </summary>
    public static void PlayGlyphMicroMotion(UIElement? iconElement, string? glyph)
    {
        if (iconElement is null || !AreAnimationsEnabled || string.IsNullOrWhiteSpace(glyph))
        {
            return;
        }

        string normalized = glyph.Trim();
        try
        {
            if (normalized is "\uE72C" or "\uE777" or "\uE895")
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(iconElement);
                Compositor compositor = visual.Compositor;
                BindCenterPointExpression(visual, compositor);

                CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.22f, 1.0f),
                    new Vector2(0.36f, 1.0f));
                ScalarKeyFrameAnimation spin = compositor.CreateScalarKeyFrameAnimation();
                spin.InsertKeyFrame(0f, 0f);
                spin.InsertKeyFrame(1f, -360f, ease);
                spin.Duration = TimeSpan.FromMilliseconds(380);
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), spin);
            }
            else if (normalized is "\uE893" or "\uE72A" or "\uE8AC")
            {
                Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
                float baseZ = iconElement.Translation.Z;
                CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.2f, 0.0f),
                    new Vector2(0.0f, 1.0f));
                Vector3KeyFrameAnimation nudge = compositor.CreateVector3KeyFrameAnimation();
                nudge.Target = nameof(UIElement.Translation);
                nudge.InsertKeyFrame(0f, new Vector3(0f, 0f, baseZ));
                nudge.InsertKeyFrame(0.45f, new Vector3(3.5f, 0f, baseZ), ease);
                nudge.InsertKeyFrame(1f, new Vector3(0f, 0f, baseZ), ease);
                nudge.Duration = TimeSpan.FromMilliseconds(240);
                iconElement.StartAnimation(nudge);
            }
            else if (normalized is "\uE73E" or "\uE73A" or "\uE930" or "\uE8FB")
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(iconElement);
                Compositor compositor = visual.Compositor;
                BindCenterPointExpression(visual, compositor);

                CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.2f, 0.0f),
                    new Vector2(0.0f, 1.0f));
                Vector3KeyFrameAnimation pop = compositor.CreateVector3KeyFrameAnimation();
                pop.InsertKeyFrame(0f, new Vector3(0.80f, 0.80f, 1f));
                pop.InsertKeyFrame(0.55f, new Vector3(1.12f, 1.12f, 1f), ease);
                pop.InsertKeyFrame(1f, Vector3.One, ease);
                pop.Duration = TimeSpan.FromMilliseconds(240);
                visual.StartAnimation(nameof(Visual.Scale), pop);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Smoothly cross-fades and scales an inline glyph when its icon state changes (e.g. Play ↔ Pause).
    /// </summary>
    public static void PlayGlyphMorphTransition(UIElement? iconElement)
    {
        if (iconElement is null || !AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(iconElement);
            Compositor compositor = visual.Compositor;
            BindCenterPointExpression(visual, compositor);

            CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.0f),
                new Vector2(0.0f, 1.0f));
            TimeSpan duration = TimeSpan.FromMilliseconds(180);

            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.82f, 0.82f, 1f));
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = duration;

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.4f);
            fade.InsertKeyFrame(1f, 1.0f, ease);
            fade.Duration = duration;

            visual.StartAnimation(nameof(Visual.Scale), scale);
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
        catch
        {
        }
    }

    private static readonly ConditionalWeakTable<FrameworkElement, object> s_canvasAmbientUnloadGuards = new();
    private static readonly RoutedEventHandler s_canvasAmbientUnloadedHandler = OnCanvasAmbientElementUnloaded;

    private static void OnCanvasAmbientElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            StopCanvasAmbientMotion(fe);
        }
    }

    public static void StopCanvasAmbientMotion(FrameworkElement? element)
    {
        if (element is null)
        {
            return;
        }

        bool hadMode = s_canvasAmbientMotionModes.TryGetValue(element, out string? existingMode) &&
                       !string.IsNullOrEmpty(existingMode);
        s_canvasAmbientMotionModes.Remove(element);
        if (s_canvasAmbientUnloadGuards.Remove(element))
        {
            element.Unloaded -= s_canvasAmbientUnloadedHandler;
        }

        if (!hadMode)
        {
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            try { visual.StopAnimation(nameof(Visual.RotationAngleInDegrees)); } catch { }
            try { visual.StopAnimation(nameof(Visual.Opacity)); } catch { }
            visual.RotationAngleInDegrees = 0f;
            visual.Opacity = (float)Math.Clamp(element.Opacity, 0.0, 1.0);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Attaches or detaches low-frequency GPU ambient motion (<c>sway:</c> or <c>breath:</c> element id prefixes)
    /// on Canvas vector elements without restarting running animations on unchanged frames.
    /// </summary>
    public static void SyncCanvasAmbientMotion(
        FrameworkElement? element,
        string? elementId,
        double pivotX,
        double pivotY)
    {
        if (element is null)
        {
            return;
        }

        string trimmedId = elementId?.Trim() ?? string.Empty;
        string modeKey = string.Empty;
        if (AreAnimationsEnabled)
        {
            if (trimmedId.StartsWith("sway:", StringComparison.OrdinalIgnoreCase))
            {
                modeKey = $"sway:{pivotX:0.#},{pivotY:0.#}";
            }
            else if (trimmedId.StartsWith("breath:", StringComparison.OrdinalIgnoreCase))
            {
                modeKey = "breath";
            }
        }

        if (s_canvasAmbientMotionModes.TryGetValue(element, out string? currentMode) &&
            string.Equals(currentMode, modeKey, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrEmpty(modeKey))
        {
            StopCanvasAmbientMotion(element);
            return;
        }

        s_canvasAmbientMotionModes.AddOrUpdate(element, modeKey);
        if (s_canvasAmbientUnloadGuards.TryAdd(element, s_canvasAmbientUnloadedHandler))
        {
            element.Unloaded += s_canvasAmbientUnloadedHandler;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;

            if (modeKey.StartsWith("sway:", StringComparison.Ordinal))
            {
                double left = Canvas.GetLeft(element);
                double top = Canvas.GetTop(element);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;
                visual.CenterPoint = new Vector3((float)(pivotX - left), (float)(pivotY - top), 0f);

                CubicBezierEasingFunction sineEase = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.45f, 0.05f),
                    new Vector2(0.55f, 0.95f));
                ScalarKeyFrameAnimation sway = compositor.CreateScalarKeyFrameAnimation();
                sway.InsertKeyFrame(0f, -1.5f);
                sway.InsertKeyFrame(0.5f, 1.5f, sineEase);
                sway.InsertKeyFrame(1.0f, -1.5f, sineEase);
                sway.Duration = TimeSpan.FromMilliseconds(5000);
                sway.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), sway);
            }
            else if (string.Equals(modeKey, "breath", StringComparison.Ordinal))
            {
                CubicBezierEasingFunction sineEase = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.45f, 0.05f),
                    new Vector2(0.55f, 0.95f));
                ScalarKeyFrameAnimation breath = compositor.CreateScalarKeyFrameAnimation();
                breath.InsertKeyFrame(0f, 0.24f);
                breath.InsertKeyFrame(0.5f, 0.62f, sineEase);
                breath.InsertKeyFrame(1.0f, 0.24f, sineEase);
                breath.Duration = TimeSpan.FromMilliseconds(4500);
                breath.IterationBehavior = AnimationIterationBehavior.Forever;
                visual.StartAnimation(nameof(Visual.Opacity), breath);
            }
            else
            {
                try { visual.StopAnimation(nameof(Visual.RotationAngleInDegrees)); } catch { }
                try { visual.StopAnimation(nameof(Visual.Opacity)); } catch { }
                visual.RotationAngleInDegrees = 0f;
                visual.Opacity = (float)Math.Clamp(element.Opacity, 0.0, 1.0);
            }
        }
        catch
        {
        }
    }
}
