using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskBox.Services;

public sealed record WidgetTrayAnimationProfile(
    double ShowOffsetX,
    double ShowOffsetY,
    double HideOffsetX,
    double HideOffsetY,
    float ShowStartOpacity,
    float HideEndOpacity,
    float ShowStartScale,
    float HideEndScale,
    int DurationMs,
    bool IsEnabled);

public sealed class WidgetTrayAnimationController
{
    public const float RestingOpacity = 1.0f;
    public const float SoftOpacity = 0.0f;
    public const float RestingScale = 1.0f;
    public const float SoftScale = 0.985f;

    private const double MinWidgetSlideOffset = 1.0;
    private const double OffscreenSlidePadding = 16.0;

    private readonly AppWindow _appWindow;
    private readonly FrameworkElement _rootElement;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IntPtr _windowHandle;
    private readonly Func<Windows.Foundation.Rect> _getAnimationBounds;
    private readonly Action<string> _log;

    private DispatcherQueueTimer? _timer;
    private PointInt32? _targetPosition;
    private double? _offsetOverrideX;
    private double? _offsetOverrideY;
    private Microsoft.UI.Composition.Visual? _cachedRootVisual;

    public WidgetTrayAnimationController(
        AppWindow appWindow,
        FrameworkElement rootElement,
        DispatcherQueue dispatcherQueue,
        IntPtr windowHandle,
        Func<Windows.Foundation.Rect> getAnimationBounds,
        Action<string> log)
    {
        _appWindow = appWindow;
        _rootElement = rootElement;
        _dispatcherQueue = dispatcherQueue;
        _windowHandle = windowHandle;
        _getAnimationBounds = getAnimationBounds;
        _log = log;
    }

    public long Generation { get; private set; }

    public bool IsApplyingBounds { get; private set; }

    public long NextGeneration()
    {
        return ++Generation;
    }

    public void SetOffsetOverride(double? offsetX, double? offsetY)
    {
        _offsetOverrideX = offsetX;
        _offsetOverrideY = offsetY;
    }

    public WidgetTrayAnimationProfile CreateProfile(WidgetAnimationOptions options)
    {
        string effect = options.Effect;
        int durationMs = options.DurationMs;
        var slideOffsets = GetOffscreenSlideOffsets();
        var (dirX, dirY) = WidgetAnimationSettings.GetDirectionalOffset(options.SlideDirection, slideOffsets);

        return effect switch
        {
            SettingsService.WidgetAnimationEffectNone => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                1, false),
            SettingsService.WidgetAnimationEffectFade => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeft => new WidgetTrayAnimationProfile(
                -slideOffsets.Left, 0, -slideOffsets.Left, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUp => new WidgetTrayAnimationProfile(
                0, -slideOffsets.Up, 0, -slideOffsets.Up,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDown => new WidgetTrayAnimationProfile(
                0, slideOffsets.Down, 0, slideOffsets.Down,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectScaleFade => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                SoftScale, SoftScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRight => new WidgetTrayAnimationProfile(
                slideOffsets.Right, 0, slideOffsets.Right, 0,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectZoom => new WidgetTrayAnimationProfile(
                0, 0, 0, 0,
                SoftOpacity, SoftOpacity,
                0.5f, 0.5f,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideUpFade => new WidgetTrayAnimationProfile(
                0, -slideOffsets.Up, 0, -slideOffsets.Up,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideDownFade => new WidgetTrayAnimationProfile(
                0, slideOffsets.Down, 0, slideOffsets.Down,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideLeftFade => new WidgetTrayAnimationProfile(
                -slideOffsets.Left, 0, -slideOffsets.Left, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideRightFade => new WidgetTrayAnimationProfile(
                slideOffsets.Right, 0, slideOffsets.Right, 0,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectSlideFade => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            SettingsService.WidgetAnimationEffectScaleSlide => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                SoftOpacity, SoftOpacity,
                RestingScale, RestingScale,
                durationMs, true),
            _ => new WidgetTrayAnimationProfile(
                dirX, dirY, dirX, dirY,
                RestingOpacity, RestingOpacity,
                RestingScale, RestingScale,
                durationMs, true)
        };
    }

    public void PrepareVisualState(double offsetX, double offsetY, float opacity, float scale)
    {
        var bounds = _getAnimationBounds();
        _targetPosition = new PointInt32(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y));
        ApplyWindowOffset(offsetX, offsetY);

        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        visual.CenterPoint = GetVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(scale, scale, 1.0f);
        ApplyOpacity(opacity);
    }

    public void PrepareHiddenState()
    {
        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        visual.Offset = Vector3.Zero;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        ApplyOpacity(SoftOpacity);
    }

    public void Animate(
        double fromOffsetX,
        double fromOffsetY,
        double toOffsetX,
        double toOffsetY,
        float fromOpacity,
        float toOpacity,
        float fromScale,
        float toScale,
        int durationMs,
        bool isShowing,
        long generation,
        string easingIntensity,
        Action completed)
    {
        _log(
            $"AnimateStart mode={(isShowing ? "show" : "hide")} gen={generation} durationMs={durationMs} " +
            $"windowOffset=({fromOffsetX:F0},{fromOffsetY:F0})->({toOffsetX:F0},{toOffsetY:F0}) " +
            $"windowOpacity={fromOpacity:F2}->{toOpacity:F2}");
        Stop();
        PrepareVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);

        if (durationMs <= 1)
        {
            CompleteAnimation(toOffsetX, toOffsetY, toOpacity, toScale, isShowing, generation, completed);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer = timer;

        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_timer, timer) || generation != Generation)
            {
                timer.Stop();
                return;
            }

            double rawProgress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0.0, 1.0);
            double easedProgress = WidgetAnimationSettings.Ease(rawProgress, easingIntensity, isShowing);
            double currentOffsetX = Lerp(fromOffsetX, toOffsetX, easedProgress);
            double currentOffsetY = Lerp(fromOffsetY, toOffsetY, easedProgress);
            float currentOpacity = (float)Lerp(fromOpacity, toOpacity, easedProgress);
            float currentScale = (float)Lerp(fromScale, toScale, easedProgress);

            ApplyWindowOffset(currentOffsetX, currentOffsetY);
            ApplyOpacity(currentOpacity);
            ApplyScale(currentScale);

            if (rawProgress < 1.0)
            {
                return;
            }

            timer.Stop();
            if (ReferenceEquals(_timer, timer))
            {
                _timer = null;
            }

            CompleteAnimation(toOffsetX, toOffsetY, toOpacity, toScale, isShowing, generation, completed);
        };

        timer.Start();
    }

    public void Stop()
    {
        if (_timer is { } timer)
        {
            timer.Stop();
            _timer = null;
        }

        if (_cachedRootVisual is { } visual)
        {
            StopVisualAnimations(visual);
        }
    }

    public void RestoreVisualState()
    {
        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        visual.CenterPoint = GetVisualCenterPoint();
        visual.Offset = Vector3.Zero;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        RestoreOpacity();
    }

    public void RestoreWindowPosition()
    {
        if (_targetPosition is { } target)
        {
            IsApplyingBounds = true;
            try
            {
                _appWindow.Move(target);
            }
            finally
            {
                IsApplyingBounds = false;
            }
        }

        _targetPosition = null;
    }

    private void CompleteAnimation(
        double finalOffsetX,
        double finalOffsetY,
        float finalOpacity,
        float finalScale,
        bool isShowing,
        long generation,
        Action completed)
    {
        if (generation != Generation)
        {
            return;
        }

        ApplyWindowOffset(finalOffsetX, finalOffsetY);
        ApplyOpacity(finalOpacity);
        ApplyScale(finalScale);
        SetOffsetOverride(null, null);
        _log($"AnimateCompleted mode={(isShowing ? "show" : "hide")} gen={generation}");
        completed();
    }

    private void ApplyWindowOffset(double offsetX, double offsetY)
    {
        var bounds = _getAnimationBounds();
        var target = _targetPosition ?? new PointInt32(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y));
        var nextPosition = new PointInt32(
            target.X + (int)Math.Round(offsetX),
            target.Y + (int)Math.Round(offsetY));

        IsApplyingBounds = true;
        try
        {
            _appWindow.Move(nextPosition);
        }
        finally
        {
            IsApplyingBounds = false;
        }
    }

    private void ApplyOpacity(float opacity)
    {
        opacity = Math.Clamp(opacity, 0.0f, 1.0f);
        var visual = GetCachedRootVisual();
        visual.Opacity = opacity;
    }

    private void RestoreOpacity()
    {
        var visual = GetCachedRootVisual();
        visual.Opacity = RestingOpacity;
    }

    private void ApplyScale(float scale)
    {
        scale = Math.Clamp(scale, 0.0f, 1.0f);
        var visual = GetCachedRootVisual();
        visual.CenterPoint = GetVisualCenterPoint();
        visual.Scale = new Vector3(scale, scale, 1.0f);
    }

    private (double Left, double Right, double Up, double Down) GetOffscreenSlideOffsets()
    {
        if (_offsetOverrideX.HasValue || _offsetOverrideY.HasValue)
        {
            double horizontal = Math.Abs(_offsetOverrideX.GetValueOrDefault());
            double vertical = Math.Abs(_offsetOverrideY.GetValueOrDefault());
            return (
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                horizontal > 0 ? horizontal : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset,
                vertical > 0 ? vertical : MinWidgetSlideOffset);
        }

        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        var bounds = _getAnimationBounds();
        double x = bounds.X;
        double y = bounds.Y;
        double width = Math.Max(MinWidgetSlideOffset, bounds.Width);
        double height = Math.Max(MinWidgetSlideOffset, bounds.Height);

        double left = Math.Max(MinWidgetSlideOffset, (x + width) - workArea.X + OffscreenSlidePadding);
        double right = Math.Max(MinWidgetSlideOffset, (workArea.X + workArea.Width) - x + OffscreenSlidePadding);
        double up = Math.Max(MinWidgetSlideOffset, (y + height) - workArea.Y + OffscreenSlidePadding);
        double down = Math.Max(MinWidgetSlideOffset, (workArea.Y + workArea.Height) - y + OffscreenSlidePadding);
        return (left, right, up, down);
    }

    private Microsoft.UI.Composition.Visual GetCachedRootVisual()
    {
        return _cachedRootVisual ??= ElementCompositionPreview.GetElementVisual(_rootElement);
    }

    private Vector3 GetVisualCenterPoint()
    {
        return new Vector3(
            (float)Math.Max(0, _rootElement.ActualWidth / 2),
            (float)Math.Max(0, _rootElement.ActualHeight / 2),
            0);
    }

    private static void StopVisualAnimations(Microsoft.UI.Composition.Visual visual)
    {
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }
}
