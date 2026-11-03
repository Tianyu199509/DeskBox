using System.Numerics;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

/// <summary>
/// The left stage is one persistent illustration canvas. Each step owns a
/// single hero wrapper; switching steps runs a direction-aware Fluent
/// hand-off (the outgoing hero accelerates out, the incoming one decelerates
/// in from the navigation direction) while the shared halo glides behind the
/// new hero. Inside each hero a short, purposeful loop explains exactly one
/// idea — and wherever the right column offers a choice, the scene answers it
/// immediately (drop mode replays, real F7 toggles are mirrored, feature
/// switches pop their tiles). Every loop is fully stopped when leaving the
/// step, and reduced-motion users get a meaningful static end frame instead.
/// </summary>
public sealed partial class OnboardingWindow
{
    private const int HeroEnterMs = 520;
    private const int HeroEnterDelayMs = 70;
    private const int HeroExitMs = 170;
    private const float HeroTravel = 28f;
    private const int HaloGlideMs = 640;

    /// <summary>One full coincide → fan → permute → merge → snap cycle.</summary>
    private const int MarkAssemblyLoopMs = 7200;

    /// <summary>Lift → fly → land ×3 → badge → path rewrite → hold.</summary>
    private const int DropSceneLoopMs = 6000;

    /// <summary>Press → summon → drag into chat → press → dismiss.</summary>
    private const int SummonSceneLoopMs = 5200;

    /// <summary>Pointer → tray click → menu rise → highlight walk → close.</summary>
    private const int TraySceneLoopMs = 6400;

    private static readonly Vector2[] HaloOffsets =
    [
        new(0f, 0f),
        new(0f, 10f),
        new(0f, -34f),
        new(18f, -22f),
        new(0f, -6f),
    ];

    /// <summary>
    /// Bumped by every StopSceneLoops so one-shot callbacks (the scene
    /// entrance's halo hand-off) can tell their loop from a newer one.
    /// </summary>
    private int _sceneLoopGeneration;
    private int _sceneStep = -1;
    private bool _dropSceneShowsCopy;
    private bool _summonSceneMirrored;
    private Compositor? _sceneCompositor;

    private Compositor SceneCompositor =>
        _sceneCompositor ??= GetVisual(SceneHalo).Compositor;

    // ════════════════════════════════════════════════════════════
    //  Fluent motion curves
    // ════════════════════════════════════════════════════════════

    private CubicBezierEasingFunction Bezier(float x1, float y1, float x2, float y2) =>
        SceneCompositor.CreateCubicBezierEasingFunction(new Vector2(x1, y1), new Vector2(x2, y2));

    /// <summary>Entrances: fast start, long soft landing.</summary>
    private CubicBezierEasingFunction Decelerate() => Bezier(0.1f, 0.9f, 0.2f, 1f);

    /// <summary>Exits: leave quickly without a visible tail.</summary>
    private CubicBezierEasingFunction Accelerate() => Bezier(0.7f, 0f, 1f, 0.5f);

    /// <summary>State changes in place.</summary>
    private CubicBezierEasingFunction EasyEase() => Bezier(0.33f, 0f, 0.67f, 1f);

    /// <summary>WinUI point-to-point travel (connected motion).</summary>
    private CubicBezierEasingFunction PointToPoint() => Bezier(0.55f, 0.55f, 0f, 1f);

    private CubicBezierEasingFunction EaseOut() => Decelerate();

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static bool MotionEnabled => WindowsCompatibilityService.AreAnimationsEnabled;

    // ════════════════════════════════════════════════════════════
    //  Step hand-off
    // ════════════════════════════════════════════════════════════

    private UIElement GetSceneHero(int step) => step switch
    {
        0 => SceneMark,
        1 => SceneDropHero,
        2 => SceneSummonHero,
        3 => SceneTrayHero,
        _ => SceneFeatureTiles,
    };

    private void ApplySceneState(int step, bool animate) =>
        ApplySceneState(step, animate, direction: 1);

    private void ApplySceneState(int step, bool animate, int direction)
    {
        StopSceneLoops();
        bool motion = animate && MotionEnabled;
        int previous = _sceneStep;
        _sceneStep = step;

        for (int index = 0; index < StepCount; index++)
        {
            UIElement hero = GetSceneHero(index);
            if (index == step)
            {
                EnterHero(hero, motion, direction);
            }
            else if (index == previous && motion)
            {
                ExitHero(hero, direction);
            }
            else
            {
                Pose(hero, 0f, 0f, 1f, 0f);
            }
        }

        MoveHalo(step, motion);
        PrepareStepPose(step, finalFrame: !MotionEnabled);
    }

    private void EnterHero(UIElement hero, bool motion, int direction)
    {
        Visual visual = GetVisual(hero);
        StopVisual(visual);
        SetTranslation(visual, Vector3.Zero);
        visual.Scale = Vector3.One;
        visual.Opacity = 1f;
        if (!motion)
        {
            return;
        }

        Compositor compositor = visual.Compositor;
        TimeSpan delay = Ms(HeroEnterDelayMs);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = Ms(HeroEnterMs);
        translation.DelayTime = delay;
        translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        translation.InsertKeyFrame(0f, new Vector3(direction * HeroTravel, 0f, 0f));
        translation.InsertKeyFrame(1f, Vector3.Zero, Decelerate());

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = Ms(HeroEnterMs);
        scale.DelayTime = delay;
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        scale.InsertKeyFrame(0f, new Vector3(0.97f, 0.97f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, Decelerate());

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = Ms(300);
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, Decelerate());

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private void ExitHero(UIElement hero, int direction)
    {
        // Preempt directly without StopVisual so an interrupted entrance
        // accelerates out from its live frame instead of snapping to base 0.
        Visual visual = GetVisual(hero);
        Compositor compositor = visual.Compositor;

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = Ms(HeroExitMs);
        translation.InsertKeyFrame(1f, new Vector3(-direction * HeroTravel * 0.6f, 0f, 0f), Accelerate());

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = Ms(HeroExitMs);
        scale.InsertKeyFrame(1f, new Vector3(0.985f, 0.985f, 1f), Accelerate());

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = Ms(HeroExitMs - 20);
        opacity.InsertKeyFrame(1f, 0f, Accelerate());

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private void MoveHalo(int step, bool motion)
    {
        Vector2 offset = HaloOffsets[Math.Clamp(step, 0, HaloOffsets.Length - 1)];
        var target = new Vector3(offset, 0f);
        float scale = step == 0 ? 1f : 0.92f;
        float opacity = step == 0 ? 0.55f : 0.42f;
        Visual visual = GetVisual(SceneHalo);
        if (!motion)
        {
            StopVisual(visual);
            SetTranslation(visual, target);
            visual.Scale = new Vector3(scale, scale, 1f);
            visual.Opacity = opacity;
            return;
        }

        AnimateVisualTo(visual, target, scale, opacity, HaloGlideMs, PointToPoint());
    }

    private void PrepareStepPose(int step, bool finalFrame)
    {
        switch (step)
        {
            case 0:
                ResetMarkPose();
                break;
            case 1:
                PrepareDropPose(finalFrame);
                break;
            case 2:
                PrepareSummonPose(finalFrame);
                break;
            case 3:
                PrepareTrayPose(finalFrame);
                break;
            case 4:
                ApplyAllFeatureTileStates(animate: false);
                break;
        }
    }

    private void StartSceneLoops(int step)
    {
        if (!MotionEnabled)
        {
            return;
        }

        switch (step)
        {
            case 0:
                StartMarkAssemblyLoop();
                break;
            case 1:
                StartFileFlightLoop();
                break;
            case 2:
                StartKeycapPressLoop();
                break;
            case 3:
                StartTrayHaloLoop();
                break;
            case 4:
                StartFeatureFloatLoop();
                break;
        }
    }

    private void StopAllSceneAnimations()
    {
        StopSceneLoops();
        StopLoopAnimation(SceneHalo);
        for (int index = 0; index < StepCount; index++)
        {
            StopLoopAnimation(GetSceneHero(index));
        }
    }

    private void StopSceneLoops()
    {
        _sceneLoopGeneration++;
        foreach (UIElement element in EnumerateSceneLoopElements())
        {
            StopLoopAnimation(element);
        }
    }

    private IEnumerable<UIElement> EnumerateSceneLoopElements()
    {
        yield return SceneMarkGroup;
        yield return SceneMarkSlab0;
        yield return SceneMarkSlab1;
        yield return SceneMarkSlab2;

        yield return SceneFileToken;
        yield return SceneFileToken2;
        yield return SceneFileToken3;
        yield return SceneFileGhost1;
        yield return SceneFileGhost2;
        yield return SceneFileGhost3;
        yield return SceneFileClone1;
        yield return SceneFileClone2;
        yield return SceneFileClone3;
        yield return SceneDropSlot1;
        yield return SceneDropSlot2;
        yield return SceneDropSlot3;
        yield return SceneDropZone;
        yield return SceneDropBox;
        yield return SceneDropBoxHalo;
        yield return SceneMoveBadge;
        yield return SceneCopyBadge;
        yield return ScenePathChip;
        yield return ScenePathOldRow;
        yield return ScenePathStrike;
        yield return ScenePathNewRow;

        yield return SceneMiniWidgetA;
        yield return SceneMiniWidgetB;
        yield return SceneDragToken;
        yield return SceneAppDropHighlight;
        yield return SceneAppSentBubble;
        yield return SceneKeycap;
        yield return SceneKeycapHalo;

        yield return SceneTrayMenu;
        yield return SceneMenuHighlight;
        yield return SceneCursor;
        yield return SceneTrayHalo;
        foreach (UIElement row in SceneMenuRows.Children)
        {
            yield return row;
        }

        foreach (UIElement tile in SceneFeatureTiles.Children)
        {
            yield return tile;
        }
    }

    private static void StopLoopAnimation(UIElement element)
    {
        StopVisual(GetVisual(element));
    }

    // ════════════════════════════════════════════════════════════
    //  Visual primitives
    // ════════════════════════════════════════════════════════════

    private static void StopVisual(Visual visual)
    {
        visual.StopAnimation("Translation");
        visual.StopAnimation("RotationAngle");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Scale.X");
        visual.StopAnimation("Opacity");
    }

    /// <summary>
    /// Translation is driven exclusively through the composition property
    /// set (enabled by <see cref="GetVisual"/>), never the XAML facade, so
    /// static poses and keyframe loops always write the same channel.
    /// </summary>
    private static void SetTranslation(Visual visual, Vector3 translation) =>
        visual.Properties.InsertVector3("Translation", translation);

    private static void Pose(UIElement element, float x, float y, float scale, float opacity)
    {
        Visual visual = GetVisual(element);
        StopVisual(visual);
        SetTranslation(visual, new Vector3(x, y, 0f));
        visual.Scale = new Vector3(scale, scale, 1f);
        visual.Opacity = opacity;
    }

    private static void AnimateVisualTo(
        Visual visual,
        Vector3 translation,
        float scale,
        float opacity,
        int durationMs,
        CompositionEasingFunction easing,
        int delayMs = 0)
    {
        Compositor compositor = visual.Compositor;
        var translate = compositor.CreateVector3KeyFrameAnimation();
        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        var fade = compositor.CreateScalarKeyFrameAnimation();
        foreach (KeyFrameAnimation animation in new KeyFrameAnimation[] { translate, scaleAnimation, fade })
        {
            animation.Duration = Ms(durationMs);
            if (delayMs > 0)
            {
                animation.DelayTime = Ms(delayMs);
            }
        }

        translate.InsertKeyFrame(1f, translation, easing);
        scaleAnimation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
        fade.InsertKeyFrame(1f, opacity, easing);
        visual.StartAnimation("Translation", translate);
        visual.StartAnimation("Scale", scaleAnimation);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// Drives Translation/Scale/Opacity from one key table so the three
    /// channels stay phase-locked. <paramref name="center"/> overrides the
    /// default centered pivot (e.g. the pointer scales around its tip).
    /// </summary>
    private static void StartKeyLoop(
        UIElement element,
        TimeSpan duration,
        SceneKey[] keys,
        bool loop = true,
        Vector3? center = null)
    {
        Visual visual = GetVisual(element);
        if (center is { } pivot)
        {
            visual.CenterPoint = pivot;
        }

        Compositor compositor = visual.Compositor;
        var translation = compositor.CreateVector3KeyFrameAnimation();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        foreach (KeyFrameAnimation animation in new KeyFrameAnimation[] { translation, scale, opacity })
        {
            animation.Duration = duration;
            if (loop)
            {
                animation.IterationBehavior = AnimationIterationBehavior.Forever;
            }
            else
            {
                animation.IterationBehavior = AnimationIterationBehavior.Count;
                animation.IterationCount = 1;
            }
        }

        foreach (SceneKey key in keys)
        {
            var translationValue = new Vector3(key.Translation.X, key.Translation.Y, 0f);
            var scaleValue = new Vector3(key.Scale, key.Scale, 1f);
            if (key.Easing is null)
            {
                translation.InsertKeyFrame(key.Progress, translationValue);
                scale.InsertKeyFrame(key.Progress, scaleValue);
                opacity.InsertKeyFrame(key.Progress, key.Opacity);
            }
            else
            {
                translation.InsertKeyFrame(key.Progress, translationValue, key.Easing);
                scale.InsertKeyFrame(key.Progress, scaleValue, key.Easing);
                opacity.InsertKeyFrame(key.Progress, key.Opacity, key.Easing);
            }
        }

        visual.StartAnimation("Translation", translation);
        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
    }

    private readonly record struct SceneKey(
        float Progress,
        Vector2 Translation,
        float Scale = 1f,
        float Opacity = 1f,
        CompositionEasingFunction? Easing = null);

    /// <summary>
    /// Layout-space origin of <paramref name="element"/> inside
    /// <paramref name="root"/>. Sums ActualOffset up the visual tree, so it
    /// ignores composition translations and is stable while loops run.
    /// </summary>
    private static Vector2 LayoutOriginIn(FrameworkElement element, UIElement root)
    {
        Vector3 offset = Vector3.Zero;
        DependencyObject? current = element;
        while (current is UIElement uiElement && !ReferenceEquals(uiElement, root))
        {
            offset += uiElement.ActualOffset;
            current = VisualTreeHelper.GetParent(uiElement);
        }

        return new Vector2(offset.X, offset.Y);
    }

    private static Vector2 LayoutCenterIn(FrameworkElement element, UIElement root) =>
        LayoutOriginIn(element, root) +
        new Vector2((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f);

    // ── Step 1: the mark assembles — the three slabs coincide face-on as one
    // tile, fan apart along the logo diagonal, permute cyclically (each beat
    // moves every slab one slot; the front slab hops over the top, scaled
    // down and dimmed to read as passing behind), then merge back into the
    // stacked logo, breathe, and snap shut into the next cycle ──

    private void StartMarkAssemblyLoop()
    {
        var duration = Ms(MarkAssemblyLoopMs);
        var easeOut = Decelerate();
        var easeIn = Bezier(0.7f, 0f, 0.84f, 0f);
        var easeInOut = Bezier(0.65f, 0f, 0.35f, 1f);
        var arcEase = Bezier(0.45f, 0f, 0.25f, 1f);

        // Waypoints are the approved 240px-stage preview scaled to the 128px
        // mark. pitch = one (4,3)-viewBox-unit slab step so all slabs coincide
        // on the middle footprint; back/middle/front are the exploded slots
        // straddling the resting logo along its diagonal; arc is the hop over
        // the top. Fixed z-order by design — the "behind" pass is faked with
        // scale + dim because Composition cannot animate Canvas.ZIndex.
        var pitch = new Vector2(21.3f, 16f);
        var back = new Vector2(-20.3f, -14.9f);
        var middle = new Vector2(2.1f, -2.1f);
        var front = new Vector2(22.4f, 17.1f);
        var arc = new Vector2(1.1f, -34.1f);

        // Back slab: coincides, fans out first, hops over the top on beat 3.
        StartKeyLoop(SceneMarkSlab0, duration,
        [
            new(0.00f, pitch),
            new(0.12f, pitch, Easing: easeOut),
            new(0.26f, back, 1.02f),
            new(0.36f, back),
            new(0.46f, middle, Easing: easeInOut),
            new(0.56f, front, Easing: easeInOut),
            new(0.60f, arc, 0.94f, 0.85f, arcEase),
            new(0.64f, back, Easing: arcEase),
            new(0.68f, back),
            new(0.78f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, pitch, 0.99f, Easing: easeIn),
        ]);

        // Middle slab: hops over the top on beat 2.
        StartKeyLoop(SceneMarkSlab1, duration,
        [
            new(0.00f, Vector2.Zero),
            new(0.14f, Vector2.Zero, Easing: easeOut),
            new(0.28f, middle, 1.02f),
            new(0.36f, middle),
            new(0.46f, front, Easing: easeInOut),
            new(0.51f, arc, 0.94f, 0.85f, arcEase),
            new(0.56f, back, Easing: arcEase),
            new(0.64f, middle, Easing: easeInOut),
            new(0.70f, middle),
            new(0.80f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, Vector2.Zero),
        ]);

        // Front slab: hops over the top on beat 1.
        StartKeyLoop(SceneMarkSlab2, duration,
        [
            new(0.00f, -pitch),
            new(0.16f, -pitch, Easing: easeOut),
            new(0.30f, front, 1.02f),
            new(0.36f, front),
            new(0.41f, arc, 0.94f, 0.85f, arcEase),
            new(0.46f, back, Easing: arcEase),
            new(0.56f, middle, Easing: easeInOut),
            new(0.64f, front, Easing: easeInOut),
            new(0.72f, front),
            new(0.82f, Vector2.Zero, Easing: easeOut),
            new(0.92f, Vector2.Zero),
            new(1.00f, -pitch, 0.99f, Easing: easeIn),
        ]);

        StartMarkGroupLoop(duration, easeOut, easeInOut);
        StartMarkHaloPulse(duration);
    }

    /// <summary>
    /// Group-level motion on SceneMarkGroup only: a breathe at the coincided
    /// tile, a lift at the merge impact, a settle breathe on the finished
    /// logo, and a squash into the snap-shut.
    /// </summary>
    private void StartMarkGroupLoop(
        TimeSpan duration,
        CompositionEasingFunction easeOut,
        CompositionEasingFunction easeInOut)
    {
        var visual = GetVisual(SceneMarkGroup);
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = duration;
        translation.IterationBehavior = AnimationIterationBehavior.Forever;
        translation.InsertKeyFrame(0.00f, Vector3.Zero);
        translation.InsertKeyFrame(0.05f, new Vector3(0, -1.6f, 0), easeInOut);
        translation.InsertKeyFrame(0.10f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(0.64f, Vector3.Zero);
        translation.InsertKeyFrame(0.70f, new Vector3(0, -1.1f, 0), easeOut);
        translation.InsertKeyFrame(0.78f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(0.85f, new Vector3(0, -3.2f, 0), easeInOut);
        translation.InsertKeyFrame(0.92f, Vector3.Zero, easeInOut);
        translation.InsertKeyFrame(1.00f, Vector3.Zero);
        visual.StartAnimation("Translation", translation);

        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.InsertKeyFrame(0.00f, Vector3.One);
        scale.InsertKeyFrame(0.05f, new Vector3(1.012f, 1.012f, 1f), easeInOut);
        scale.InsertKeyFrame(0.10f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.64f, Vector3.One);
        scale.InsertKeyFrame(0.70f, new Vector3(1.02f, 1.02f, 1f), easeOut);
        scale.InsertKeyFrame(0.78f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.96f, new Vector3(0.99f, 0.99f, 1f));
        scale.InsertKeyFrame(1.00f, Vector3.One);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// The shared halo beats with the loop — one pulse per permutation hop,
    /// a bigger flash as the slabs merge into the logo. Also restarted by
    /// the scene entrance once its one-shot fade hands Opacity back.
    /// </summary>
    private void StartMarkHaloPulse(TimeSpan duration)
    {
        var visual = GetVisual(SceneHalo);
        var compositor = visual.Compositor;
        var easeInOut = Bezier(0.65f, 0f, 0.35f, 1f);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = duration;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        opacity.InsertKeyFrame(0.00f, 0.55f);
        opacity.InsertKeyFrame(0.41f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.46f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.51f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.56f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.61f, 0.78f, easeInOut);
        opacity.InsertKeyFrame(0.66f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(0.74f, 0.95f, easeInOut);
        opacity.InsertKeyFrame(0.82f, 0.55f, easeInOut);
        opacity.InsertKeyFrame(1.00f, 0.55f);
        visual.StartAnimation("Opacity", opacity);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.InsertKeyFrame(0.00f, Vector3.One);
        scale.InsertKeyFrame(0.41f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.46f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.51f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.56f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.61f, new Vector3(1.06f, 1.06f, 1f), easeInOut);
        scale.InsertKeyFrame(0.66f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(0.74f, new Vector3(1.12f, 1.12f, 1f), easeInOut);
        scale.InsertKeyFrame(0.82f, Vector3.One, easeInOut);
        scale.InsertKeyFrame(1.00f, Vector3.One);
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>
    /// Parks the mark slabs and group on the exact logo pose — the static
    /// fallback for instant scene applies and disabled animations.
    /// </summary>
    private void ResetMarkPose()
    {
        foreach (var element in new UIElement[]
                 { SceneMarkSlab0, SceneMarkSlab1, SceneMarkSlab2, SceneMarkGroup })
        {
            Pose(element, 0f, 0f, 1f, 1f);
        }
    }

    // ── Step 2: moving changes the path. Each desktop file lifts (a clone
    // takes off; in Move mode the original slot is left as a dashed hole),
    // flies point-to-point into the storage widget and pops into its slot.
    // Then the mode badge lands, the path card rises, the old path is struck
    // through and the new location slides in. Copy mode keeps the originals
    // in place and shows "+ copy" instead of a strike. ──

    private UIElement[] DropTokens => [SceneFileToken, SceneFileToken2, SceneFileToken3];
    private UIElement[] DropGhosts => [SceneFileGhost1, SceneFileGhost2, SceneFileGhost3];
    private FrameworkElement[] DropClones => [SceneFileClone1, SceneFileClone2, SceneFileClone3];
    private FrameworkElement[] DropSlots => [SceneDropSlot1, SceneDropSlot2, SceneDropSlot3];

    /// <summary>Switches the drop story between Move and Copy and replays it.</summary>
    private void SetDropSceneMode(bool copy, bool replay)
    {
        _dropSceneShowsCopy = copy;
        if (!replay || _sceneStep != 1)
        {
            return;
        }

        StopSceneLoops();
        PrepareDropPose(finalFrame: !MotionEnabled);
        if (MotionEnabled)
        {
            StartFileFlightLoop();
        }
    }

    private void PrepareDropPose(bool finalFrame)
    {
        bool copy = _dropSceneShowsCopy;
        bool moved = finalFrame && !copy;
        UIElement[] tokens = DropTokens;
        UIElement[] ghosts = DropGhosts;
        FrameworkElement[] clones = DropClones;
        FrameworkElement[] slots = DropSlots;
        for (int index = 0; index < 3; index++)
        {
            Pose(tokens[index], 0f, 0f, 1f, moved ? 0f : 1f);
            Pose(ghosts[index], 0f, 0f, 1f, moved ? 1f : 0f);
            Pose(clones[index], 0f, 0f, 1f, 0f);
            Pose(slots[index], 0f, 0f, finalFrame ? 1f : 0.6f, finalFrame ? 1f : 0f);
        }

        Pose(SceneDropZone, 0f, 0f, 1f, finalFrame ? 0f : 1f);
        Pose(SceneDropBox, 0f, 0f, 1f, 1f);
        Pose(SceneDropBoxHalo, 0f, 0f, 1f, 0f);
        Pose(SceneMoveBadge, 0f, finalFrame ? 0f : 4f, 1f, moved ? 1f : 0f);
        Pose(SceneCopyBadge, 0f, finalFrame ? 0f : 4f, 1f, finalFrame && copy ? 1f : 0f);
        Pose(ScenePathChip, 0f, finalFrame ? 0f : 10f, 1f, finalFrame ? 1f : 0f);
        Pose(ScenePathOldRow, 0f, 0f, 1f, moved ? 0.5f : 1f);
        Pose(ScenePathNewRow, 0f, finalFrame ? 0f : 6f, 1f, finalFrame ? 1f : 0f);

        Visual strike = GetVisual(ScenePathStrike);
        StopVisual(strike);
        strike.CenterPoint = new Vector3(0f, (float)ScenePathStrike.ActualHeight / 2f, 0f);
        strike.Scale = new Vector3(moved ? 1f : 0f, 1f, 1f);
        strike.Opacity = copy ? 0f : 1f;

        ScenePathNewGlyph.Glyph = copy ? "\uE710" : "\uE72A";
    }

    private void StartFileFlightLoop()
    {
        var duration = Ms(DropSceneLoopMs);
        bool copy = _dropSceneShowsCopy;
        var decelerate = Decelerate();
        var ease = EasyEase();
        var travel = PointToPoint();

        UIElement[] tokens = DropTokens;
        UIElement[] ghosts = DropGhosts;
        FrameworkElement[] clones = DropClones;
        FrameworkElement[] slots = DropSlots;
        var lands = new float[3];

        for (int index = 0; index < 3; index++)
        {
            float start = 0.10f + (0.11f * index);
            float land = start + 0.16f;
            lands[index] = land;

            Vector2 delta = LayoutCenterIn(slots[index], SceneDropHero) -
                LayoutCenterIn(clones[index], SceneDropHero);

            StartKeyLoop(clones[index], duration,
            [
                new(0f, Vector2.Zero, 1f, 0f),
                new(start, Vector2.Zero, 1f, 0f),
                new(start + 0.012f, Vector2.Zero, 1f, 1f),
                new(start + 0.05f, new Vector2(0f, -9f), 1.1f, 1f, decelerate),
                new(land, delta, 0.75f, 1f, travel),
                new(land + 0.012f, delta, 0.75f, 0f),
                new(1f, delta, 0.75f, 0f),
            ]);

            if (copy)
            {
                // The original acknowledges the copy and stays put.
                StartKeyLoop(tokens[index], duration,
                [
                    new(0f, Vector2.Zero),
                    new(start, Vector2.Zero),
                    new(start + 0.04f, Vector2.Zero, 0.96f, 1f, ease),
                    new(start + 0.10f, Vector2.Zero, 1f, 1f, ease),
                    new(1f, Vector2.Zero),
                ]);
                Pose(ghosts[index], 0f, 0f, 1f, 0f);
            }
            else
            {
                // The original leaves; a dashed hole marks the old location
                // until the next batch of files arrives on the desktop.
                StartKeyLoop(tokens[index], duration,
                [
                    new(0f, Vector2.Zero),
                    new(start, Vector2.Zero),
                    new(start + 0.012f, Vector2.Zero, 1f, 0f),
                    new(0.90f, new Vector2(0f, -10f), 1f, 0f),
                    new(0.98f, Vector2.Zero, 1f, 1f, decelerate),
                    new(1f, Vector2.Zero),
                ]);
                StartKeyLoop(ghosts[index], duration,
                [
                    new(0f, Vector2.Zero, 1f, 0f),
                    new(start + 0.02f, Vector2.Zero, 0.96f, 0f),
                    new(start + 0.09f, Vector2.Zero, 1f, 1f, decelerate),
                    new(0.86f, Vector2.Zero, 1f, 1f),
                    new(0.92f, Vector2.Zero, 1f, 0f, ease),
                    new(1f, Vector2.Zero, 1f, 0f),
                ]);
            }

            StartKeyLoop(slots[index], duration,
            [
                new(0f, Vector2.Zero, 0.6f, 0f),
                new(land - 0.004f, Vector2.Zero, 0.6f, 0f),
                new(land + 0.035f, Vector2.Zero, 1.08f, 1f, decelerate),
                new(land + 0.08f, Vector2.Zero, 1f, 1f, ease),
                new(0.86f, Vector2.Zero, 1f, 1f),
                new(0.92f, Vector2.Zero, 0.92f, 0f, ease),
                new(1f, Vector2.Zero, 0.6f, 0f),
            ]);
        }

        // The widget swallows each landing with a soft pulse.
        var boxKeys = new List<SceneKey> { new(0f, Vector2.Zero) };
        var haloKeys = new List<SceneKey> { new(0f, Vector2.Zero, 1f, 0f) };
        foreach (float land in lands)
        {
            boxKeys.Add(new(land, Vector2.Zero));
            boxKeys.Add(new(land + 0.02f, Vector2.Zero, 1.03f, 1f, decelerate));
            boxKeys.Add(new(land + 0.07f, Vector2.Zero, 1f, 1f, ease));
            haloKeys.Add(new(land, Vector2.Zero, 1f, 0f));
            haloKeys.Add(new(land + 0.02f, Vector2.Zero, 1f, 0.2f, decelerate));
            haloKeys.Add(new(land + 0.09f, Vector2.Zero, 1f, 0f, ease));
        }

        boxKeys.Add(new(1f, Vector2.Zero));
        haloKeys.Add(new(1f, Vector2.Zero, 1f, 0f));
        StartKeyLoop(SceneDropBox, duration, [.. boxKeys]);
        StartKeyLoop(SceneDropBoxHalo, duration, [.. haloKeys]);

        StartKeyLoop(SceneDropZone, duration,
        [
            new(0f, Vector2.Zero),
            new(lands[0], Vector2.Zero),
            new(lands[0] + 0.04f, Vector2.Zero, 1f, 0f, ease),
            new(0.88f, Vector2.Zero, 1f, 0f),
            new(0.95f, Vector2.Zero, 1f, 1f, ease),
            new(1f, Vector2.Zero),
        ]);

        float badgeAt = lands[2] + 0.03f;
        UIElement badge = copy ? SceneCopyBadge : SceneMoveBadge;
        Pose(copy ? SceneMoveBadge : SceneCopyBadge, 0f, 4f, 1f, 0f);
        StartKeyLoop(badge, duration,
        [
            new(0f, new Vector2(0f, 4f), 0.92f, 0f),
            new(badgeAt, new Vector2(0f, 4f), 0.92f, 0f),
            new(badgeAt + 0.06f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.86f, Vector2.Zero, 1f, 1f),
            new(0.92f, new Vector2(0f, 2f), 0.96f, 0f, ease),
            new(1f, new Vector2(0f, 4f), 0.92f, 0f),
        ]);

        float chipAt = badgeAt + 0.02f;
        StartKeyLoop(ScenePathChip, duration,
        [
            new(0f, new Vector2(0f, 10f), 1f, 0f),
            new(chipAt, new Vector2(0f, 10f), 1f, 0f),
            new(chipAt + 0.08f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.88f, Vector2.Zero, 1f, 1f),
            new(0.94f, new Vector2(0f, 6f), 1f, 0f, ease),
            new(1f, new Vector2(0f, 10f), 1f, 0f),
        ]);

        float newAt = chipAt + 0.14f;
        if (copy)
        {
            Pose(ScenePathOldRow, 0f, 0f, 1f, 1f);
        }
        else
        {
            StartKeyLoop(ScenePathOldRow, duration,
            [
                new(0f, Vector2.Zero),
                new(chipAt + 0.10f, Vector2.Zero),
                new(chipAt + 0.16f, Vector2.Zero, 1f, 0.5f, ease),
                new(0.94f, Vector2.Zero, 1f, 0.5f),
                new(1f, Vector2.Zero),
            ]);

            Visual strike = GetVisual(ScenePathStrike);
            strike.CenterPoint = new Vector3(0f, (float)ScenePathStrike.ActualHeight / 2f, 0f);
            strike.Opacity = 1f;
            var draw = strike.Compositor.CreateScalarKeyFrameAnimation();
            draw.Duration = duration;
            draw.IterationBehavior = AnimationIterationBehavior.Forever;
            draw.InsertKeyFrame(0f, 0f);
            draw.InsertKeyFrame(chipAt + 0.09f, 0f);
            draw.InsertKeyFrame(chipAt + 0.16f, 1f, decelerate);
            draw.InsertKeyFrame(0.94f, 1f);
            draw.InsertKeyFrame(1f, 0f);
            strike.StartAnimation("Scale.X", draw);
        }

        StartKeyLoop(ScenePathNewRow, duration,
        [
            new(0f, new Vector2(0f, 6f), 1f, 0f),
            new(newAt, new Vector2(0f, 6f), 1f, 0f),
            new(newAt + 0.08f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.94f, Vector2.Zero, 1f, 1f),
            new(1f, new Vector2(0f, 6f), 1f, 0f),
        ]);
    }

    // ── Step 3: summon over the app you are in. Press → the widgets rise
    // above a chat window → a file is dragged straight into the conversation
    // and lands as a sent bubble → press → the widgets step back. ──

    private void PrepareSummonPose(bool finalFrame)
    {
        _summonSceneMirrored = false;
        Pose(SceneMiniWidgetA, 0f, finalFrame ? 0f : 8f, finalFrame ? 1f : 0.96f, finalFrame ? 1f : 0f);
        Pose(SceneMiniWidgetB, 0f, finalFrame ? 0f : 8f, finalFrame ? 1f : 0.96f, finalFrame ? 1f : 0f);
        Vector2 from = DragTokenFrom();
        Pose(SceneDragToken, from.X, from.Y, 1f, 0f);
        Pose(SceneAppDropHighlight, 0f, 0f, 1f, 0f);
        Pose(SceneAppSentBubble, 0f, finalFrame ? 0f : 6f, finalFrame ? 1f : 0.96f, finalFrame ? 1f : 0f);
        Pose(SceneKeycap, 0f, 0f, 1f, 1f);
        Pose(SceneKeycapHalo, 0f, 0f, 0.9f, 0f);
    }

    private Vector2 DragTokenFrom() =>
        LayoutCenterIn(SceneMiniFileIcon, SceneSummonHero) - new Vector2(12f, 12f);

    private Vector2 DragTokenTo() =>
        LayoutOriginIn(SceneAppDropZone, SceneSummonHero) +
        new Vector2(74f, (float)SceneAppDropZone.ActualHeight / 2f) -
        new Vector2(12f, 12f);

    private void StartKeycapPressLoop()
    {
        var duration = Ms(SummonSceneLoopMs);
        var decelerate = Decelerate();
        var accelerate = Accelerate();
        var ease = EasyEase();
        var travel = PointToPoint();

        StartKeyLoop(SceneKeycap, duration,
        [
            new(0f, Vector2.Zero),
            new(0.06f, Vector2.Zero),
            new(0.085f, new Vector2(0f, 1.5f), 0.92f, 1f, decelerate),
            new(0.14f, Vector2.Zero, 1f, 1f, ease),
            new(0.70f, Vector2.Zero),
            new(0.725f, new Vector2(0f, 1.5f), 0.92f, 1f, decelerate),
            new(0.78f, Vector2.Zero, 1f, 1f, ease),
            new(1f, Vector2.Zero),
        ]);

        StartKeyLoop(SceneKeycapHalo, duration,
        [
            new(0f, Vector2.Zero, 0.9f, 0f),
            new(0.06f, Vector2.Zero, 0.9f, 0f),
            new(0.09f, Vector2.Zero, 1f, 0.32f, decelerate),
            new(0.30f, Vector2.Zero, 1.18f, 0f, ease),
            new(0.31f, Vector2.Zero, 0.9f, 0f),
            new(0.70f, Vector2.Zero, 0.9f, 0f),
            new(0.73f, Vector2.Zero, 1f, 0.32f, decelerate),
            new(0.94f, Vector2.Zero, 1.18f, 0f, ease),
            new(1f, Vector2.Zero, 0.9f, 0f),
        ]);

        StartKeyLoop(SceneMiniWidgetA, duration, BuildSummonWidgetKeys(0.10f, 0.74f, decelerate, accelerate));
        StartKeyLoop(SceneMiniWidgetB, duration, BuildSummonWidgetKeys(0.13f, 0.76f, decelerate, accelerate));

        Vector2 from = DragTokenFrom();
        Vector2 to = DragTokenTo();
        StartKeyLoop(SceneDragToken, duration,
        [
            new(0f, from, 1f, 0f),
            new(0.28f, from, 1f, 0f),
            new(0.29f, from, 1f, 1f),
            new(0.33f, from + new Vector2(0f, -8f), 1.12f, 1f, decelerate),
            new(0.48f, to, 1f, 1f, travel),
            new(0.51f, to, 0.7f, 0f, accelerate),
            new(1f, to, 0.7f, 0f),
        ]);

        StartKeyLoop(SceneAppDropHighlight, duration,
        [
            new(0f, Vector2.Zero, 1f, 0f),
            new(0.42f, Vector2.Zero, 1f, 0f),
            new(0.47f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.58f, Vector2.Zero, 1f, 1f),
            new(0.66f, Vector2.Zero, 1f, 0f, ease),
            new(1f, Vector2.Zero, 1f, 0f),
        ]);

        StartKeyLoop(SceneAppSentBubble, duration,
        [
            new(0f, new Vector2(0f, 6f), 0.96f, 0f),
            new(0.50f, new Vector2(0f, 6f), 0.96f, 0f),
            new(0.57f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.90f, Vector2.Zero, 1f, 1f),
            new(0.96f, new Vector2(0f, 4f), 1f, 0f, ease),
            new(1f, new Vector2(0f, 6f), 0.96f, 0f),
        ]);
    }

    private static SceneKey[] BuildSummonWidgetKeys(
        float enter,
        float exit,
        CompositionEasingFunction decelerate,
        CompositionEasingFunction accelerate) =>
    [
        new(0f, new Vector2(0f, 8f), 0.96f, 0f),
        new(enter, new Vector2(0f, 8f), 0.96f, 0f),
        new(enter + 0.10f, Vector2.Zero, 1f, 1f, decelerate),
        new(exit, Vector2.Zero, 1f, 1f),
        new(exit + 0.06f, new Vector2(0f, 6f), 0.97f, 0f, accelerate),
        new(1f, new Vector2(0f, 8f), 0.96f, 0f),
    ];

    /// <summary>
    /// Real toggles (F7, tray click, the Try button) take over the scene:
    /// the loop stops and the mini widgets follow the actual desktop state,
    /// with the keycap answering every press.
    /// </summary>
    private void MirrorSummonScene(bool widgetsVisible)
    {
        if (_sceneStep != 2)
        {
            return;
        }

        if (!_summonSceneMirrored)
        {
            _summonSceneMirrored = true;
            // Retire only the drag demo elements; the mini widgets and keycap
            // are preempted directly from their live frame below so they
            // never snap to their pre-loop base pose first.
            Vector2 from = DragTokenFrom();
            Pose(SceneDragToken, from.X, from.Y, 1f, 0f);
            Pose(SceneAppDropHighlight, 0f, 0f, 1f, 0f);
            Pose(SceneAppSentBubble, 0f, 6f, 0.96f, 0f);
        }

        float y = widgetsVisible ? 0f : 8f;
        float scale = widgetsVisible ? 1f : 0.96f;
        float opacity = widgetsVisible ? 1f : 0f;
        if (!MotionEnabled)
        {
            Pose(SceneMiniWidgetA, 0f, y, scale, opacity);
            Pose(SceneMiniWidgetB, 0f, y, scale, opacity);
            Pose(SceneKeycap, 0f, 0f, 1f, 1f);
            Pose(SceneKeycapHalo, 0f, 0f, 0.9f, 0f);
            return;
        }

        CompositionEasingFunction easing = widgetsVisible ? Decelerate() : Accelerate();
        int duration = widgetsVisible ? 360 : 200;
        AnimateVisualTo(GetVisual(SceneMiniWidgetA), new Vector3(0f, y, 0f), scale, opacity, duration, easing);
        AnimateVisualTo(GetVisual(SceneMiniWidgetB), new Vector3(0f, y, 0f), scale, opacity, duration, easing, widgetsVisible ? 40 : 0);

        var once = Ms(560);
        StartKeyLoop(SceneKeycap, once,
        [
            new(0f, Vector2.Zero),
            new(0.12f, new Vector2(0f, 1.5f), 0.9f, 1f, Decelerate()),
            new(0.5f, Vector2.Zero, 1f, 1f, EasyEase()),
            new(1f, Vector2.Zero),
        ], loop: false);
        StartKeyLoop(SceneKeycapHalo, once,
        [
            new(0f, Vector2.Zero, 0.9f, 0f),
            new(0.14f, Vector2.Zero, 1f, 0.34f, Decelerate()),
            new(1f, Vector2.Zero, 1.2f, 0f, EasyEase()),
        ], loop: false);
    }

    // ── Step 4: the pointer clicks the DeskBox tray icon (ripple), the menu
    // rises from the tray corner with a row stagger, and the highlight walks
    // the three entries that matter on day one: new widget → map a folder →
    // settings. ──

    private void PrepareTrayPose(bool finalFrame)
    {
        Pose(SceneTrayMenu, 0f, finalFrame ? 0f : 8f, finalFrame ? 1f : 0.96f, finalFrame ? 1f : 0f);
        SetTrayMenuPivot();
        foreach (UIElement row in SceneMenuRows.Children)
        {
            Pose(row, 0f, finalFrame ? 0f : 6f, 1f, finalFrame ? 1f : 0f);
        }

        SceneMenuHighlight.Height = Math.Max(1, SceneMenuRowMapping.ActualHeight);
        Pose(SceneMenuHighlight, 0f, MenuRowY(SceneMenuRowMapping), 1f, finalFrame ? 1f : 0f);
        Vector2 start = TrayPointerStart();
        Pose(SceneCursor, start.X, start.Y, 1f, 0f);
        GetVisual(SceneCursor).CenterPoint = Vector3.Zero;
        Pose(SceneTrayHalo, 0f, 0f, 0.8f, 0f);
    }

    private void SetTrayMenuPivot()
    {
        Visual menu = GetVisual(SceneTrayMenu);
        menu.CenterPoint = new Vector3(
            (float)SceneTrayMenu.ActualWidth * 0.85f,
            (float)SceneTrayMenu.ActualHeight,
            0f);
    }

    private static float MenuRowY(FrameworkElement row) => row.ActualOffset.Y;

    private Vector2 TrayIconPoint() =>
        LayoutCenterIn(SceneTrayIconHost, SceneTrayHero) + new Vector2(1f, 2f);

    private Vector2 TrayPointerStart() => TrayIconPoint() + new Vector2(-64f, -46f);

    private Vector2 MenuRowPoint(FrameworkElement row)
    {
        Vector2 origin = LayoutOriginIn(SceneMenuRows, SceneTrayHero);
        return origin + new Vector2(
            (float)SceneMenuRows.ActualWidth * 0.64f,
            MenuRowY(row) + ((float)row.ActualHeight * 0.55f));
    }

    private void StartTrayHaloLoop()
    {
        var duration = Ms(TraySceneLoopMs);
        var decelerate = Decelerate();
        var accelerate = Accelerate();
        var ease = EasyEase();
        var travel = PointToPoint();

        Vector2 start = TrayPointerStart();
        Vector2 tray = TrayIconPoint();
        Vector2 rowNew = MenuRowPoint(SceneMenuRowNewWidget);
        Vector2 rowMap = MenuRowPoint(SceneMenuRowMapping);
        Vector2 rowSettings = MenuRowPoint(SceneMenuRowSettings);

        StartKeyLoop(SceneCursor, duration,
        [
            new(0f, start, 1f, 0f),
            new(0.05f, start, 1f, 1f, ease),
            new(0.18f, tray, 1f, 1f, travel),
            new(0.195f, tray, 0.85f, 1f, decelerate),
            new(0.225f, tray, 1f, 1f, ease),
            new(0.35f, rowNew, 1f, 1f, travel),
            new(0.47f, rowNew, 1f, 1f),
            new(0.55f, rowMap, 1f, 1f, travel),
            new(0.66f, rowMap, 1f, 1f),
            new(0.74f, rowSettings, 1f, 1f, travel),
            new(0.85f, rowSettings, 1f, 1f),
            new(0.90f, rowSettings, 1f, 0f, ease),
            new(1f, start, 1f, 0f),
        ], center: Vector3.Zero);

        StartKeyLoop(SceneTrayHalo, duration,
        [
            new(0f, Vector2.Zero, 0.8f, 0f),
            new(0.19f, Vector2.Zero, 0.8f, 0f),
            new(0.21f, Vector2.Zero, 1f, 0.42f, decelerate),
            new(0.36f, Vector2.Zero, 1.3f, 0f, ease),
            new(1f, Vector2.Zero, 0.8f, 0f),
        ]);

        StartKeyLoop(SceneTrayMenu, duration,
        [
            new(0f, new Vector2(0f, 8f), 0.96f, 0f),
            new(0.22f, new Vector2(0f, 8f), 0.96f, 0f),
            new(0.31f, Vector2.Zero, 1f, 1f, decelerate),
            new(0.88f, Vector2.Zero, 1f, 1f),
            new(0.93f, new Vector2(0f, 4f), 0.98f, 0f, accelerate),
            new(1f, new Vector2(0f, 8f), 0.96f, 0f),
        ], center: new Vector3(
            (float)SceneTrayMenu.ActualWidth * 0.85f,
            (float)SceneTrayMenu.ActualHeight,
            0f));

        int rowIndex = 0;
        foreach (UIElement row in SceneMenuRows.Children)
        {
            float rowStart = 0.225f + (0.011f * rowIndex);
            StartKeyLoop(row, duration,
            [
                new(0f, new Vector2(0f, 6f), 1f, 0f),
                new(rowStart, new Vector2(0f, 6f), 1f, 0f),
                new(rowStart + 0.07f, Vector2.Zero, 1f, 1f, decelerate),
                new(0.93f, Vector2.Zero, 1f, 1f),
                new(1f, new Vector2(0f, 6f), 1f, 0f),
            ]);
            rowIndex++;
        }

        SceneMenuHighlight.Height = Math.Max(1, SceneMenuRowNewWidget.ActualHeight);
        float yNew = MenuRowY(SceneMenuRowNewWidget);
        float yMap = MenuRowY(SceneMenuRowMapping);
        float ySettings = MenuRowY(SceneMenuRowSettings);
        StartKeyLoop(SceneMenuHighlight, duration,
        [
            new(0f, new Vector2(0f, yNew), 1f, 0f),
            new(0.35f, new Vector2(0f, yNew), 1f, 0f),
            new(0.38f, new Vector2(0f, yNew), 1f, 1f, ease),
            new(0.47f, new Vector2(0f, yNew), 1f, 1f),
            new(0.55f, new Vector2(0f, yMap), 1f, 1f, travel),
            new(0.66f, new Vector2(0f, yMap), 1f, 1f),
            new(0.74f, new Vector2(0f, ySettings), 1f, 1f, travel),
            new(0.85f, new Vector2(0f, ySettings), 1f, 1f),
            new(0.89f, new Vector2(0f, ySettings), 1f, 0f, ease),
            new(1f, new Vector2(0f, yNew), 1f, 0f),
        ]);
    }

    // ── Step 5: tiles mirror the switches. Enabled tiles stand forward with
    // an accent check and idle-float gently; disabled ones recede. ──

    private (UIElement Tile, UIElement Check)? GetFeatureTile(WidgetKind kind) => kind switch
    {
        WidgetKind.Todo => (SceneTileTodo, SceneTileTodoCheck),
        WidgetKind.QuickCapture => (SceneTileQuickCapture, SceneTileQuickCaptureCheck),
        WidgetKind.Search => (SceneTileSearch, SceneTileSearchCheck),
        WidgetKind.Weather => (SceneTileWeather, SceneTileWeatherCheck),
        WidgetKind.Music => (SceneTileMusic, SceneTileMusicCheck),
        WidgetKind.Glance => (SceneTileGlance, SceneTileGlanceCheck),
        _ => null,
    };

    private static readonly WidgetKind[] SceneFeatureKinds =
    [
        WidgetKind.Todo,
        WidgetKind.QuickCapture,
        WidgetKind.Search,
        WidgetKind.Weather,
        WidgetKind.Music,
        WidgetKind.Glance,
    ];

    private void ApplyFeatureTileState(WidgetKind kind, bool enabled, bool animate, int delayMs = 0)
    {
        if (GetFeatureTile(kind) is not { } parts)
        {
            return;
        }

        float tileScale = enabled ? 1f : 0.92f;
        float tileOpacity = enabled ? 1f : 0.55f;
        float checkScale = enabled ? 1f : 0.4f;
        float checkOpacity = enabled ? 1f : 0f;
        Visual tile = GetVisual(parts.Tile);
        Visual check = GetVisual(parts.Check);
        if (!animate || !MotionEnabled)
        {
            tile.StopAnimation("Scale");
            tile.StopAnimation("Opacity");
            check.StopAnimation("Scale");
            check.StopAnimation("Opacity");
            check.StopAnimation("Translation");
            tile.Scale = new Vector3(tileScale, tileScale, 1f);
            tile.Opacity = tileOpacity;
            check.Scale = new Vector3(checkScale, checkScale, 1f);
            check.Opacity = checkOpacity;
            return;
        }

        Compositor compositor = tile.Compositor;
        if (enabled)
        {
            var pop = compositor.CreateVector3KeyFrameAnimation();
            pop.Duration = Ms(420);
            pop.DelayTime = Ms(delayMs);
            pop.InsertKeyFrame(0.5f, new Vector3(1.06f, 1.06f, 1f), Decelerate());
            pop.InsertKeyFrame(1f, Vector3.One, EasyEase());
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = Ms(220);
            fade.DelayTime = Ms(delayMs);
            fade.InsertKeyFrame(1f, 1f, Decelerate());
            tile.StartAnimation("Scale", pop);
            tile.StartAnimation("Opacity", fade);

            var checkPop = compositor.CreateVector3KeyFrameAnimation();
            checkPop.Duration = Ms(360);
            checkPop.DelayTime = Ms(delayMs + 90);
            checkPop.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            checkPop.InsertKeyFrame(0f, new Vector3(0.4f, 0.4f, 1f));
            checkPop.InsertKeyFrame(0.6f, new Vector3(1.12f, 1.12f, 1f), Decelerate());
            checkPop.InsertKeyFrame(1f, Vector3.One, EasyEase());
            var checkFade = compositor.CreateScalarKeyFrameAnimation();
            checkFade.Duration = Ms(180);
            checkFade.DelayTime = Ms(delayMs + 90);
            checkFade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            checkFade.InsertKeyFrame(0f, 0f);
            checkFade.InsertKeyFrame(1f, 1f, Decelerate());
            check.StartAnimation("Scale", checkPop);
            check.StartAnimation("Opacity", checkFade);
            return;
        }

        AnimateVisualScaleOpacity(tile, tileScale, tileOpacity, 240, EasyEase(), delayMs);
        AnimateVisualScaleOpacity(check, checkScale, checkOpacity, 150, Accelerate(), delayMs);
    }

    private static void AnimateVisualScaleOpacity(
        Visual visual,
        float scale,
        float opacity,
        int durationMs,
        CompositionEasingFunction easing,
        int delayMs)
    {
        var scaleAnimation = visual.Compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = Ms(durationMs);
        scaleAnimation.DelayTime = Ms(delayMs);
        scaleAnimation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = Ms(durationMs);
        fade.DelayTime = Ms(delayMs);
        fade.InsertKeyFrame(1f, opacity, easing);
        visual.StartAnimation("Scale", scaleAnimation);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// Staggered arrival of the tile grid when the step opens: every tile
    /// rises into its current state, 45 ms apart, reading left-to-right.
    /// </summary>
    private void PlayFeatureTilesEntrance(Func<WidgetKind, bool> isEnabled)
    {
        int index = 0;
        foreach (WidgetKind kind in SceneFeatureKinds)
        {
            bool enabled = isEnabled(kind);
            if (!MotionEnabled || GetFeatureTile(kind) is not { } parts)
            {
                ApplyFeatureTileState(kind, enabled, animate: false);
                index++;
                continue;
            }

            Visual tile = GetVisual(parts.Tile);
            tile.StopAnimation("Scale");
            tile.StopAnimation("Opacity");
            tile.Scale = new Vector3(0.86f, 0.86f, 1f);
            tile.Opacity = 0f;
            Visual check = GetVisual(parts.Check);
            check.StopAnimation("Scale");
            check.StopAnimation("Opacity");
            check.Scale = new Vector3(0.4f, 0.4f, 1f);
            check.Opacity = 0f;
            ApplyFeatureTileState(kind, enabled, animate: true, delayMs: HeroEnterDelayMs + (index * 45));
            index++;
        }
    }

    private void StartFeatureFloatLoop()
    {
        var durations = new[] { 3200, 3600, 4000 };
        var index = 0;
        foreach (var child in SceneFeatureTiles.Children)
        {
            StartFloat(child, durations[index % durations.Length], (index * 160) % 900);
            index++;
        }
    }

    private void StartFloat(UIElement element, int durationMs, int delayMs)
    {
        var visual = GetVisual(element);
        var ease = EasyEase();
        var rise = visual.Compositor.CreateVector3KeyFrameAnimation();
        rise.Duration = Ms(durationMs);
        rise.DelayTime = Ms(delayMs);
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        rise.IterationBehavior = AnimationIterationBehavior.Forever;
        rise.InsertKeyFrame(0f, Vector3.Zero);
        rise.InsertKeyFrame(0.5f, new Vector3(0, -2.5f, 0), ease);
        rise.InsertKeyFrame(1f, Vector3.Zero, ease);
        visual.StartAnimation("Translation", rise);
    }

    /// <summary>
    /// One-shot entrance on first paint: the halo fades in and the mark settles.
    /// </summary>
    private void PlaySceneEntrance()
    {
        if (!MotionEnabled || _stepIndex != 0)
        {
            return;
        }

        var haloVisual = GetVisual(SceneHalo);
        haloVisual.StopAnimation("Opacity");
        var haloFade = haloVisual.Compositor.CreateScalarKeyFrameAnimation();
        haloFade.Duration = Ms(700);
        haloFade.InsertKeyFrame(0f, 0f);
        haloFade.InsertKeyFrame(1f, 0.55f, Decelerate());

        // This one-shot fade owns SceneHalo.Opacity while it runs, cancelling
        // the assembly loop's pulse — hand the property back once it lands,
        // unless the scene already moved on (generation bump) or closed.
        int loopGeneration = _sceneLoopGeneration;
        var batch = haloVisual.Compositor.CreateScopedBatch(
            CompositionBatchTypes.Animation);
        haloVisual.StartAnimation("Opacity", haloFade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (!_isClosed &&
                _stepIndex == 0 &&
                _sceneLoopGeneration == loopGeneration)
            {
                StartMarkHaloPulse(Ms(MarkAssemblyLoopMs));
            }
        };

        var markVisual = GetVisual(SceneMark);
        markVisual.StopAnimation("Opacity");
        markVisual.StopAnimation("Scale");
        markVisual.StopAnimation("Translation");
        var markFade = markVisual.Compositor.CreateScalarKeyFrameAnimation();
        markFade.Duration = Ms(460);
        markFade.DelayTime = Ms(140);
        markFade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        markFade.InsertKeyFrame(0f, 0f);
        markFade.InsertKeyFrame(1f, 1f, Decelerate());
        markVisual.StartAnimation("Opacity", markFade);
        var markScale = markVisual.Compositor.CreateVector3KeyFrameAnimation();
        markScale.Duration = Ms(720);
        markScale.DelayTime = Ms(140);
        markScale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        markScale.InsertKeyFrame(0f, new Vector3(0.9f, 0.9f, 1f));
        markScale.InsertKeyFrame(1f, Vector3.One, Decelerate());
        markVisual.StartAnimation("Scale", markScale);
        var markRise = markVisual.Compositor.CreateVector3KeyFrameAnimation();
        markRise.Duration = Ms(720);
        markRise.DelayTime = Ms(140);
        markRise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        markRise.InsertKeyFrame(0f, new Vector3(0f, 14f, 0f));
        markRise.InsertKeyFrame(1f, Vector3.Zero, Decelerate());
        markVisual.StartAnimation("Translation", markRise);
    }
}
