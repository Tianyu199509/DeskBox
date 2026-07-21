using System.Diagnostics;
using System.Numerics;
using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
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

    private PointInt32? _targetPosition;
    private double? _offsetOverrideX;
    private double? _offsetOverrideY;
    private Microsoft.UI.Composition.Visual? _cachedRootVisual;
    private bool _isWindowCloakedForTrayShow;
    private double _preparedOffsetX;
    private double _preparedOffsetY;
    private float _preparedOpacity = RestingOpacity;
    private float _preparedScale = RestingScale;
    private EventHandler<object>? _contentReadyRenderingHandler;
    private int _contentReadyFrameCount;
    private long _contentReadyGeneration;
    private Action? _contentReadyAction;

    // ── 方案 A: 智能帧率节流控制 ──
    // 目标：减少不必要的渲染帧，降低系统开销
    private const int MaxFPS_HighPriority = 120;   // 高优先级阶段 FPS（前 50ms）
    private const int MaxFPS_Normal = 24;          // 正常阶段 FPS
    private const double HighPriorityDurationMs = 50.0;  // 高优先级持续时长 (ms)
    
    private DateTime _lastRenderTime = DateTime.MaxValue;
    private TimeSpan _elapsedSinceStart;
    private int _targetFPS = MaxFPS_HighPriority;
    private double _minFrameIntervalMs = 8.33;     // 120fps 的帧间隔
    private DateTime _animationStartTime;
    // ── 方案 A 控制字段结束 ──

    private bool _isRendering;
    private Stopwatch? _renderStopwatch;
    private double _renderDurationMs;
    private double _renderFromOffsetX;
    private double _renderFromOffsetY;
    private double _renderToOffsetX;
    private double _renderToOffsetY;
    private bool _renderIsShowing;
    private long _renderGeneration;
    private string _renderEasingIntensity = string.Empty;
    private Action? _renderCompleted;
    private Microsoft.UI.Composition.Compositor? _cachedCompositor;

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

    public bool IsPositionTransitionActive => _targetPosition.HasValue;

    public long NextGeneration()
    {
        return ++Generation;
    }

    public void SetOffsetOverride(double? offsetX, double? offsetY)
    {
        _offsetOverrideX = offsetX;
        _offsetOverrideY = offsetY;
    }

    public void CloakWindowForTrayShow()
    {
        if (_isWindowCloakedForTrayShow)
        {
            return;
        }

        // Disable DWM's built-in show/hide transition so it doesn't
        // interfere with our custom animation.
        int forceDisabled = 1;
        Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref forceDisabled,
            sizeof(int));

        int cloaked = 1;
        int result = Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_CLOAK,
            ref cloaked,
            sizeof(int));
        if (result == 0)
        {
            _isWindowCloakedForTrayShow = true;
        }
        else
        {
            _log($"CloakWindow failed hresult=0x{result:X8}");
        }
    }

    public void RevealWindowForTrayShow()
    {
        if (!_isWindowCloakedForTrayShow)
        {
            return;
        }

        int cloaked = 0;
        int result = Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_CLOAK,
            ref cloaked,
            sizeof(int));
        if (result == 0)
        {
            _isWindowCloakedForTrayShow = false;
        }
        else
        {
            _log($"RevealWindow failed hresult=0x{result:X8}");
        }
    }

    private void RestoreDwmTransitions()
    {
        int forceDisabled = 0;
        Win32Helper.DwmSetWindowAttribute(
            _windowHandle,
            Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref forceDisabled,
            sizeof(int));
    }

    public void PlayAfterContentReady(Action action)
    {
        CancelContentReadyCallback();
        _contentReadyGeneration = Generation;
        _contentReadyFrameCount = 0;
        _contentReadyAction = action;
        _contentReadyRenderingHandler = OnContentReadyRenderingFrame;
        CompositionTarget.Rendering += _contentReadyRenderingHandler;
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
                RestingOpacity, RestingOpacity,
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
        _preparedOffsetX = offsetX;
        _preparedOffsetY = offsetY;
        _preparedOpacity = opacity;
        _preparedScale = scale;
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
        visual.Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
    }

    public void PrepareHiddenState()
    {
        // PrepareTrayShowAnimation already established the effect-specific start state.
        // Do not replace it with an empty XAML surface after the native window is shown.
        if (_targetPosition.HasValue)
        {
            ApplyWindowOffset(_preparedOffsetX, _preparedOffsetY);
            var v = GetCachedRootVisual();
            StopVisualAnimations(v);
            v.Opacity = Math.Clamp(_preparedOpacity, 0.0f, 1.0f);
            v.CenterPoint = GetVisualCenterPoint();
            v.Scale = new Vector3(_preparedScale, _preparedScale, 1.0f);
            return;
        }

        _rootElement.Opacity = 1;
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);
        visual.Offset = Vector3.Zero;
        visual.Opacity = RestingOpacity;
        visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        visual.Opacity = SoftOpacity;
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
        if (_targetPosition is null)
        {
            PrepareVisualState(fromOffsetX, fromOffsetY, fromOpacity, fromScale);
        }
        else
        {
            ApplyWindowOffset(fromOffsetX, fromOffsetY);
        }

        // Ensure visual is ready and any previous animations are stopped.
        var visual = GetCachedRootVisual();
        StopVisualAnimations(visual);

        if (durationMs <= 1)
        {
            // Ensure final visual state is applied for instant transitions.
            visual.Opacity = toOpacity;
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
            CompleteAnimation(toOffsetX, toOffsetY, isShowing, generation, completed);
            return;
        }

        // ── Opacity & Scale: Composition KeyFrame animations (GPU-driven) ──
        var compositor = GetCachedCompositor(visual);
        var easing = CreateEasingFunction(compositor, easingIntensity, isShowing);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        // Opacity animation
        if (Math.Abs(fromOpacity - toOpacity) > 0.001f)
        {
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Duration = duration;
            opacityAnim.InsertKeyFrame(0, fromOpacity);
            opacityAnim.InsertKeyFrame(1, toOpacity, easing);
            visual.Opacity = fromOpacity;
            visual.StartAnimation("Opacity", opacityAnim);
        }
        else
        {
            visual.Opacity = toOpacity;
        }

        // Scale animation
        if (Math.Abs(fromScale - toScale) > 0.001f)
        {
            visual.CenterPoint = GetVisualCenterPoint();
            var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1));
            scaleAnim.InsertKeyFrame(1, new Vector3(toScale, toScale, 1), easing);
            visual.Scale = new Vector3(fromScale, fromScale, 1);
            visual.StartAnimation("Scale", scaleAnim);
        }
        else
        {
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Scale = new Vector3(toScale, toScale, 1);
        }

        // ── Window offset: still CPU-driven via AppWindow.Move() ──
        // Only the position needs CompositionTarget.Rendering; opacity/scale
        // are now GPU-driven and don't need per-frame CPU updates.
        _renderFromOffsetX = fromOffsetX;
        _renderFromOffsetY = fromOffsetY;
        _renderToOffsetX = toOffsetX;
        _renderToOffsetY = toOffsetY;
        _renderDurationMs = durationMs;
        _renderIsShowing = isShowing;
        _renderGeneration = generation;
        _renderCompleted = completed;
        _renderStopwatch = Stopwatch.StartNew();
        _isRendering = true;
        _animationStartTime = DateTime.UtcNow;
        
        // Use the same easing for window position interpolation.
        _renderEasingIntensity = easingIntensity;

        CompositionTarget.Rendering -= OnRenderingFrame;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    private void OnRenderingFrame(object sender, object e)
    {
        if (!_isRendering || _renderGeneration != Generation)
        {
            StopRendering();
            return;
        }

        var stopwatch = _renderStopwatch;
        if (stopwatch is null)
        {
            StopRendering();
            return;
        }

        // ── 方案 A: 智能帧率节流控制 ──
        // 策略：动画初期（前 100ms）用 60fps，之后降为 30fps
        // 理由：人眼对启动阶段的流畅度更敏感，后续帧差异不明显
        _elapsedSinceStart = stopwatch.Elapsed;
        _targetFPS = _elapsedSinceStart.TotalMilliseconds < HighPriorityDurationMs 
            ? MaxFPS_HighPriority 
            : MaxFPS_Normal;
        
        _minFrameIntervalMs = 1000.0 / _targetFPS;
        
        // 计算距离上次渲染经过的时间
        var timeSinceLastRender = stopwatch.Elapsed - new TimeSpan(_lastRenderTime.Ticks - _animationStartTime.Ticks);
        
        // 如果未到达目标帧率的最小间隔，跳过此帧
        if (timeSinceLastRender.TotalMilliseconds < _minFrameIntervalMs)
        {
            return;  // 跳过此帧，减少系统开销
        }
        
        // 更新最后一次渲染时间
        _lastRenderTime = DateTime.UtcNow;
        // ── 方案 A 控制结束 ──

        double rawProgress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / _renderDurationMs, 0.0, 1.0);
        double easedProgress = WidgetAnimationSettings.Ease(rawProgress, _renderEasingIntensity, _renderIsShowing);
        double currentOffsetX = Lerp(_renderFromOffsetX, _renderToOffsetX, easedProgress);
        double currentOffsetY = Lerp(_renderFromOffsetY, _renderToOffsetY, easedProgress);

        // Only move the window — opacity/scale are GPU-driven by Composition animations.
        ApplyWindowOffset(currentOffsetX, currentOffsetY);

        if (rawProgress < 1.0)
        {
            return;
        }

        StopRendering();

        CompleteAnimation(
            _renderToOffsetX,
            _renderToOffsetY,
            _renderIsShowing,
            _renderGeneration,
            _renderCompleted);
    }

    private void StopRendering()
    {
        if (!_isRendering)
        {
            return;
        }

        _isRendering = false;
        _renderStopwatch = null;
        CompositionTarget.Rendering -= OnRenderingFrame;
        
        // ── 方案 A: 重置帧率节流状态 ──
        _lastRenderTime = DateTime.MinValue;
        _elapsedSinceStart = TimeSpan.Zero;
        _targetFPS = MaxFPS_HighPriority;  // 重置为默认值
        _minFrameIntervalMs = 1000.0 / _targetFPS;
        // ── 方案 A 状态重置结束 ──
    }

    public void Stop()
    {
        CancelContentReadyCallback();
        StopRendering();
        RestoreDwmTransitions();

        if (_cachedRootVisual is { } visual)
        {
            try
            {
                StopVisualAnimations(visual);
            }
            catch
            {
                // The Composition Visual may be invalid if the window is
                // being torn down. Swallow to avoid stowed WinRT exceptions.
            }
        }
    }

    private void OnContentReadyRenderingFrame(object? sender, object e)
    {
        if (_contentReadyGeneration != Generation)
        {
            CancelContentReadyCallback();
            return;
        }

        // The first frame commits the newly shown XAML surface while the HWND
        // is still outside the work area. Start moving on the following frame.
        if (++_contentReadyFrameCount < 2)
        {
            return;
        }

        Action? action = _contentReadyAction;
        CancelContentReadyCallback();
        action?.Invoke();
    }

    private void CancelContentReadyCallback()
    {
        if (_contentReadyRenderingHandler is not null)
        {
            CompositionTarget.Rendering -= _contentReadyRenderingHandler;
            _contentReadyRenderingHandler = null;
        }

        _contentReadyAction = null;
        _contentReadyFrameCount = 0;
    }

    public void StopAndRestoreWindowPosition()
    {
        Stop();
        RestoreWindowPosition();
    }

    public void RestoreVisualState()
    {
        try
        {
            _rootElement.Opacity = 1;
            var visual = GetCachedRootVisual();
            StopVisualAnimations(visual);
            visual.CenterPoint = GetVisualCenterPoint();
            visual.Offset = Vector3.Zero;
            visual.Opacity = RestingOpacity;
            visual.Scale = new Vector3(RestingScale, RestingScale, 1.0f);
        }
        catch
        {
            // The Composition Visual may be invalid if the window is
            // being torn down. Swallow to avoid stowed WinRT exceptions.
        }
    }

    public void RestoreWindowPosition()
    {
        if (_targetPosition is { } target)
        {
            IsApplyingBounds = true;
            try
            {
                MoveNativeWindow(target);
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
        bool isShowing,
        long generation,
        Action completed)
    {
        if (generation != Generation)
        {
            return;
        }

        ApplyWindowOffset(finalOffsetX, finalOffsetY);
        SetOffsetOverride(null, null);
        RestoreDwmTransitions();
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
            MoveNativeWindow(nextPosition);
        }
        finally
        {
            IsApplyingBounds = false;
        }
    }

    private void MoveNativeWindow(PointInt32 position)
    {
        // Direct P/Invoke SetWindowPos — bypasses AppWindow.Move() WinRT
        // marshalling overhead for lower per-frame latency.
        Win32Helper.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            position.X,
            position.Y,
            0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
    }

    private Microsoft.UI.Composition.Compositor GetCachedCompositor(Microsoft.UI.Composition.Visual visual)
    {
        return _cachedCompositor ??= visual.Compositor;
    }

    private static Microsoft.UI.Composition.CompositionEasingFunction CreateEasingFunction(
        Microsoft.UI.Composition.Compositor compositor,
        string easingIntensity,
        bool isShowing)
    {
        string intensity = WidgetAnimationSettings.NormalizeEasingIntensity(easingIntensity);
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
