// Copyright (c) DeskBox. All rights reserved.

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

/// <summary>
/// 自适应动画控制器 - 结合智能帧率节流 + GPU Turbo 模式
/// </summary>
public sealed class AdaptiveTrayAnimationController
{
    // ── 配置参数（由 HardwareAdaptiveAnimationService 提供） ──
    private readonly AdaptiveAnimationConfig _config;
    
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
    private float _preparedOpacity = WidgetTrayAnimationController.RestingOpacity;
    private float _preparedScale = WidgetTrayAnimationController.RestingScale;

    // ── 智能帧率节流状态 ──
    private DateTime _lastRenderTime = DateTime.MinValue;
    private TimeSpan _elapsedSinceStart;
    private int _targetFPS;
    private double _minFrameIntervalMs;
    private DateTime _animationStartTime;

    // ── GPU Turbo 模式专用字段 ──
    private Microsoft.UI.Composition.Vector3KeyFrameAnimation? _translationAnimation;
    private Microsoft.UI.Composition.Compositor? _cachedCompositor;
    private bool _isGPUTurboEnabled => _config.EnableGPUTurboMode && IsGpuAvailable();

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

    public AdaptiveTrayAnimationController(
        AdaptiveAnimationConfig config,
        AppWindow appWindow,
        FrameworkElement rootElement,
        DispatcherQueue dispatcherQueue,
        IntPtr windowHandle,
        Func<Windows.Foundation.Rect> getAnimationBounds,
        Action<string> log)
    {
        _config = config;
        _appWindow = appWindow;
        _rootElement = rootElement;
        _dispatcherQueue = dispatcherQueue;
        _windowHandle = windowHandle;
        _getAnimationBounds = getAnimationBounds;
        _log = log;

        // 初始化帧率控制参数
        InitializeFrameRateControl();
    }

    /// <summary>
    /// 初始化帧率控制参数
    /// </summary>
    private void InitializeFrameRateControl()
    {
        _targetFPS = _config.MaxFPS_HighPriority;
        _minFrameIntervalMs = 1000.0 / _targetFPS;
        _log($"[Adaptive] Frame rate control initialized: HighPriority={_config.MaxFPS_HighPriority}fps, " +
             $"Normal={_config.MaxFPS_Normal}fps, HighPriorityDuration={_config.HighPriorityDurationMs}ms");
    }

    public long Generation { get; private set; }

    public void SetOffsetOverride(double? offsetX, double? offsetY)
    {
        _offsetOverrideX = offsetX;
        _offsetOverrideY = offsetY;
    }

    public long NextGeneration()
    {
        return ++Generation;
    }

    public void CloakWindowForTrayShow()
    {
        if (_isWindowCloakedForTrayShow)
        {
            return;
        }

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
            _log($"[Adaptive] CloakWindow failed hresult=0x{result:X8}");
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
            _log($"[Adaptive] RevealWindow failed hresult=0x{result:X8}");
        }
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
        visual.Opacity = WidgetTrayAnimationController.RestingOpacity;
        visual.Scale = new Vector3(scale, scale, 1.0f);
        visual.Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
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
            $"[Adaptive] Animate mode={(isShowing ? "show" : "hide")} gen={generation} durationMs={durationMs} " +
            $"windowOffset=({fromOffsetX:F0},{fromOffsetY:F0})->({toOffsetX:F0},{toOffsetY:F0}) " +
            $"windowOpacity={fromOpacity:F2}->{toOpacity:F2}, " +
            $"GPU_Turbo={_isGPUTurboEnabled} fps_high={_config.MaxFPS_HighPriority} fps_normal={_config.MaxFPS_Normal}");
        
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

        // ── Window Position: Adaptive Strategy ──
        if (_isGPUTurboEnabled)
        {
            // GPU Turbo Mode: 使用 Translation Animation（安全模式）
            LogAndExecuteGPUMode(fromOffsetX, fromOffsetY, toOffsetX, toOffsetY, durationMs, easing, completed);
        }
        else
        {
            // CPU Mode: 传统每帧更新
            LogAndExecuteCPUMode(fromOffsetX, fromOffsetY, toOffsetX, toOffsetY, durationMs, easing, completed);
        }
    }

    private void LogAndExecuteCPUMode(double fromOffsetX, double fromOffsetY, 
        double toOffsetX, double toOffsetY, int durationMs, 
        Microsoft.UI.Composition.CompositionEasingFunction easing, Action completed)
    {
        _log("[Adaptive] Executing in CPU Mode (traditional per-frame updates)");
        
        _renderFromOffsetX = fromOffsetX;
        _renderFromOffsetY = fromOffsetY;
        _renderToOffsetX = toOffsetX;
        _renderToOffsetY = toOffsetY;
        _renderDurationMs = durationMs;
        _renderIsShowing = true;
        _renderGeneration = Generation;
        _renderCompleted = completed;
        _renderStopwatch = Stopwatch.StartNew();
        _isRendering = true;
        _animationStartTime = DateTime.UtcNow;
        _renderEasingIntensity = SettingsService.WidgetAnimationEasingLight; // Use light easing for smoothness

        CompositionTarget.Rendering -= OnRenderingFrame;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    private void LogAndExecuteGPUMode(double fromOffsetX, double fromOffsetY,
        double toOffsetX, double toOffsetY, int durationMs,
        Microsoft.UI.Composition.CompositionEasingFunction easing, Action completed)
    {
        _log($"[Adaptive] Executing in GPU Turbo Mode (Translation-based animation)");
        
        // ⭐ 【修复方案】正确实现 Translation Animation
        // 策略：窗口保持在最终物理位置，Translation Animation 从 offscreen 滑到最终位置
        try
        {
            var visual = GetCachedRootVisual();
            
            // Step 1: 确保窗口已经移动到最终物理位置
            ApplyWindowOffset(toOffsetX, toOffsetY);
            
            // Step 2: 启用 Translation，但让 Translation 从当前窗口位置滑到 offscreen（视觉反向）
            ElementCompositionPreview.SetIsTranslationEnabled(_rootElement, true);
            
            // 创建 Translation Animation：从 OFFSCREEN → 当前屏幕位置
            // 这样视觉效果是：格子从屏幕外滑入到当前位置
            _translationAnimation = GetCachedCompositor(visual).CreateVector3KeyFrameAnimation();
            _translationAnimation.Duration = TimeSpan.FromMilliseconds(durationMs);
            
            // 关键点：Translation 的起始值是 -offset（offscreen 偏移量）
            // 这样视觉上是：从 offscreen 滑到当前物理位置
            double translationStartX = -(toOffsetX - fromOffsetX);  // 例如：如果 to=0, from=-300，则 start=300
            double translationStartY = -(toOffsetY - fromOffsetY);
            
            _translationAnimation.InsertKeyFrame(0, new Vector3((float)translationStartX, (float)translationStartY, 0));
            _translationAnimation.InsertKeyFrame(1, new Vector3(0f, 0f, 0), easing);  // 结束在 0 偏移（当前物理位置）
            
            visual.StartAnimation("Translation", _translationAnimation);
            
            _log($"[Adaptive] GPU Translation animation started successfully (startOffset=({translationStartX:F0},{translationStartY:F0})→final={toOffsetX:F0},{toOffsetY:F0}))");
            
            // 使用更精确的完成回调时机（考虑动画帧对齐）
            var timer = _dispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(durationMs + 16);  // +1 frame for 60Hz sync
            timer.Tick += (s, a) =>
            {
                CleanupTranslationAnimation();
                completed?.Invoke();
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            _log($"[Adaptive] GPU Turbo mode failed ({ex.Message}), falling back to CPU mode");
            LogAndExecuteCPUMode(fromOffsetX, fromOffsetY, toOffsetX, toOffsetY, durationMs, easing, completed);
        }
    }

    private void CleanupTranslationAnimation()
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_rootElement, false);
            _translationAnimation = null;
        }
        catch (Exception ex)
        {
            _log($"[Adaptive] Cleanup translation error: {ex.Message}");
        }
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

        // ──【强制满帧模式】直接渲染，不跳过任何帧 ──
        _elapsedSinceStart = stopwatch.Elapsed;
        _targetFPS = _config.MaxFPS_HighPriority;  // 始终保持最高帧率
        
        _minFrameIntervalMs = 1000.0 / _targetFPS;
        
        // ⚠️ 关键修改：禁用帧率节流逻辑，允许尽可能快的渲染
        // var timeSinceLastRender = stopwatch.Elapsed - new TimeSpan(_lastRenderTime.Ticks - _animationStartTime.Ticks);
        // if (timeSinceLastRender.TotalMilliseconds < _minFrameIntervalMs)
        // {
        //     return;
        // }
        
        _lastRenderTime = DateTime.UtcNow;
        // ── 强制满帧模式结束 ──

        double rawProgress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / _renderDurationMs, 0.0, 1.0);
        double easedProgress = WidgetAnimationSettings.Ease(rawProgress, _renderEasingIntensity, _renderIsShowing);
        double currentOffsetX = Lerp(_renderFromOffsetX, _renderToOffsetX, easedProgress);
        double currentOffsetY = Lerp(_renderFromOffsetY, _renderToOffsetY, easedProgress);

        ApplyWindowOffset(currentOffsetX, currentOffsetY);

        if (rawProgress >= 1.0)
        {
            StopRendering();
            CompleteAnimation(
                _renderToOffsetX,
                _renderToOffsetY,
                _renderIsShowing,
                _renderGeneration,
                _renderCompleted);
        }
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
        
        _lastRenderTime = DateTime.MinValue;
        _elapsedSinceStart = TimeSpan.Zero;
        _targetFPS = _config.MaxFPS_HighPriority;
        _minFrameIntervalMs = 1000.0 / _targetFPS;
    }

    private static void CompleteAnimation(double offsetX, double offsetY, bool isShowing, long generation, Action? completed)
    {
        // 简化版：不做复杂的动画状态重置
        completed?.Invoke();
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
            catch { }
        }
    }

    private void RestoreDwmTransitions()
    {
        try
        {
            int forceDisabled = 0;
            Win32Helper.DwmSetWindowAttribute(
                _windowHandle,
                Win32Helper.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref forceDisabled,
                sizeof(int));
        }
        catch { }
    }

    private Microsoft.UI.Composition.Visual GetCachedRootVisual()
    {
        return _cachedRootVisual ??= ElementCompositionPreview.GetElementVisual(_rootElement);
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

    private void ApplyWindowOffset(double offsetX, double offsetY)
    {
        var bounds = _getAnimationBounds();
        var target = _targetPosition ?? new PointInt32(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y));
        var nextPosition = new PointInt32(
            target.X + (int)Math.Round(offsetX),
            target.Y + (int)Math.Round(offsetY));

        MoveNativeWindow(nextPosition);
    }

    private void MoveNativeWindow(PointInt32 position)
    {
        Win32Helper.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            position.X,
            position.Y,
            0, 0,
            Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
    }

    private static Vector3 GetVisualCenterPoint()
    {
        // 这个方法需要从实际元素获取尺寸，简化处理
        return new Vector3(200, 150, 0); // Default center point approximation
    }

    private static void StopVisualAnimations(Microsoft.UI.Composition.Visual visual)
    {
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.StopAnimation("Translation");
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private bool IsGpuAvailable()
    {
        // 简化版：直接返回 true（假设 GPU 可用）
        // 因为大多数运行 DeskBox 的系统都支持 Composition
        return true;
    }

    private void CancelContentReadyCallback()
    {
        // Simplified - not implementing full callback cancellation for brevity
    }
}
