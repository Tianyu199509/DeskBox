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
    CompactClipCornerRadius,
    LayerRaiseScale,
    GroupDropBreathing,
    CompactLiveTranslation,
    CompactLiveOpacity,
    EdgeGlow,
    DeferredContentRevealOpacity,
    DeferredContentRevealTranslation,
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
    private CubicBezierEasingFunction? _softOutEasing;
    private CompositionRoundedRectangleGeometry? _compactClipGeometry;
    private CompositionGeometricClip? _compactGeometricClip;
    private ExpressionAnimation? _compactClipSizeExpression;
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

    internal Vector2KeyFrameAnimation GetVector2(
        Compositor compositor,
        WidgetAnimationTemplate template)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_animations.TryGetValue(template, out CompositionAnimation? existing))
        {
            return (Vector2KeyFrameAnimation)existing;
        }

        Vector2KeyFrameAnimation animation = Track(compositor.CreateVector2KeyFrameAnimation());
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

    internal (CompositionRoundedRectangleGeometry Geometry, CompositionGeometricClip Clip, ExpressionAnimation SizeExpression) GetCompactClip(
        Compositor compositor)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _compactClipGeometry ??= Track(compositor.CreateRoundedRectangleGeometry());
        _compactGeometricClip ??= Track(compositor.CreateGeometricClip(_compactClipGeometry));
        _compactClipSizeExpression ??= Track(compositor.CreateExpressionAnimation("host.Size"));
        return (_compactClipGeometry, _compactGeometricClip, _compactClipSizeExpression);
    }

    internal CubicBezierEasingFunction GetSoftOutEasing(Compositor compositor)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return _softOutEasing ??= Track(compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.22f, 0.80f), new Vector2(0.28f, 1.0f)));
    }

    internal CompositionEasingFunction GetTrayEasing(
        Compositor compositor,
        string easingIntensity,
        bool isShowing)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        string intensity = string.Equals(
                easingIntensity,
                SettingsService.WidgetAnimationEasingNone,
                StringComparison.Ordinal)
            ? SettingsService.WidgetAnimationEasingNone
            : WidgetAnimationSettings.NormalizeEasingIntensity(easingIntensity);
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
            return intensity == SettingsService.WidgetAnimationEasingSpring
                ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.22f), new Vector2(0.36f, 1.0f))
                : compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));
        }

        return compositor.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0.0f), new Vector2(0.67f, 0.0f));
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
        Release(_softOutEasing);
        _softOutEasing = null;
        Release(_compactClipSizeExpression);
        _compactClipSizeExpression = null;
        Release(_compactGeometricClip);
        _compactGeometricClip = null;
        Release(_compactClipGeometry);
        _compactClipGeometry = null;
    }
}
