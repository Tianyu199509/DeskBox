using System.Numerics;
using DeskBox.Models;
using Microsoft.UI.Composition;

namespace DeskBox.Services;

internal enum WidgetAnimationTemplate
{
    TrayOpacity,
    TrayScale,
    CompactOpacity,
    CompactScale,
    CompactTranslation,
    GroupDropBreathing,
    CompactLiveTranslation,
    CompactLiveOpacity,
    EdgeGlow,
}

/// <summary>
/// Owns a fixed set of animation templates for one window/compositor. StartAnimation
/// takes a snapshot of a template, so updating it for another target does not change
/// an already running animation. Borrowed XAML visuals/compositors are never closed.
/// </summary>
internal sealed class WidgetCompositionResources : IDisposable
{
    private readonly Dictionary<WidgetAnimationTemplate, CompositionAnimation> _animations = [];
    private readonly Dictionary<(string Intensity, bool Showing), CompositionEasingFunction> _trayEasings = [];
    private CubicBezierEasingFunction? _breathingEasing;
    private bool _isDisposed;

    internal ScalarKeyFrameAnimation GetScalar(
        Compositor compositor,
        WidgetAnimationTemplate template)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_animations.TryGetValue(template, out CompositionAnimation? existing))
        {
            return (ScalarKeyFrameAnimation)existing;
        }

        ScalarKeyFrameAnimation animation = Track(compositor.CreateScalarKeyFrameAnimation());
        _animations.Add(template, animation);
        return animation;
    }

    internal Vector3KeyFrameAnimation GetVector3(
        Compositor compositor,
        WidgetAnimationTemplate template)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_animations.TryGetValue(template, out CompositionAnimation? existing))
        {
            return (Vector3KeyFrameAnimation)existing;
        }

        Vector3KeyFrameAnimation animation = Track(compositor.CreateVector3KeyFrameAnimation());
        _animations.Add(template, animation);
        return animation;
    }

    internal CompositionEasingFunction GetTrayEasing(
        Compositor compositor,
        string easingIntensity,
        bool isShowing)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        string intensity = WidgetAnimationSettings.NormalizeEasingIntensity(easingIntensity);
        // Normalization limits this dictionary to eight entries, independent of
        // how many times settings or the animation direction change.
        var key = (intensity, isShowing);
        if (_trayEasings.TryGetValue(key, out CompositionEasingFunction? easing))
        {
            return easing;
        }

        easing = Track(CreateTrayEasing(compositor, intensity, isShowing));
        _trayEasings.Add(key, easing);
        return easing;
    }

    internal CubicBezierEasingFunction GetBreathingEasing(Compositor compositor)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _breathingEasing ??= Track(compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
    }

    private static CompositionEasingFunction CreateTrayEasing(
        Compositor compositor, string intensity, bool isShowing)
    {
        if (intensity == SettingsService.WidgetAnimationEasingNone)
        {
            return compositor.CreateLinearEasingFunction();
        }

        if (isShowing)
        {
            return intensity switch
            {
                SettingsService.WidgetAnimationEasingLight => compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.9f), new Vector2(0.25f, 1.0f)),
                SettingsService.WidgetAnimationEasingStrong => compositor.CreateCubicBezierEasingFunction(new Vector2(0.05f, 1.1f), new Vector2(0.15f, 1.0f)),
                _ => compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.3f, 1.0f))
            };
        }

        return intensity switch
        {
            SettingsService.WidgetAnimationEasingLight => compositor.CreateCubicBezierEasingFunction(new Vector2(0.6f, 0.1f), new Vector2(0.9f, 0.3f)),
            SettingsService.WidgetAnimationEasingStrong => compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0.0f), new Vector2(0.95f, -0.1f)),
            _ => compositor.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0.0f), new Vector2(0.84f, 0.0f))
        };
    }

    internal static T Track<T>(T resource) where T : CompositionObject
    {
        PerformanceLogger.RecordOwnedCompositionResourceCreated();
        return resource;
    }

    internal static void Release(CompositionObject? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
            PerformanceLogger.RecordOwnedCompositionResourceReleased();
        }
        catch (Exception ex)
        {
            // Continue releasing other owned resources if a compositor has
            // already shut down. A failed release remains visible in diagnostics.
            App.LogVerbose($"[Composition] Owned resource release failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _isDisposed = true;
        foreach (CompositionAnimation animation in _animations.Values)
        {
            Release(animation);
        }
        _animations.Clear();
        foreach (CompositionEasingFunction easing in _trayEasings.Values)
        {
            Release(easing);
        }
        _trayEasings.Clear();
        Release(_breathingEasing);
        _breathingEasing = null;
    }
}
