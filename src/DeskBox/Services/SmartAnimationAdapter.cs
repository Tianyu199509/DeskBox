// Copyright (c) DeskBox. All rights reserved.

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
/// 智能动画适配器 - 简化版：只在硬件性能好时启用 GPU Turbo
/// 设计原则：稳定性第一，只在完全可靠的情况下才启用高级功能
/// </summary>
public sealed class SmartAnimationAdapter : IDisposable
{
    private readonly HardwareAdaptiveAnimationService _hardwareService;
    private readonly AdaptiveAnimationConfig _config;
    private readonly bool _useGpuTurbo;
    
    public bool IsGpuTurboActive => _useGpuTurbo && _config.EnableGPUTurboMode;

    public SmartAnimationAdapter(DispatcherQueue dispatcherQueue, Action<string>? logger = null)
    {
        _hardwareService = new HardwareAdaptiveAnimationService(dispatcherQueue, logger);
        
        // 启动性能测量
        _hardwareService.StartPerformanceMeasurement();
        
        // 获取自适应配置（基于 CPU 使用率）
        _config = _hardwareService.StopAndGetConfiguration();
        _useGpuTurbo = true; // 在当前版本中假设 GPU 始终可用
        
        logger?.Invoke($"[SmartAnimationAdapter] Initialized with config: {_config.Reasoning}");
    }

    /// <summary>
    /// 创建适配的动画控制器
    /// </summary>
    public WidgetTrayAnimationController CreateAnimationController(
        AppWindow appWindow,
        FrameworkElement rootElement,
        DispatcherQueue dispatcherQueue,
        IntPtr windowHandle,
        Func<Windows.Foundation.Rect> getAnimationBounds,
        Action<string> log)
    {
        // 注意：当前版本仍然使用原有的 WidgetTrayAnimationController
        // 未来可以替换为 AdaptiveTrayAnimationController 以获得更好的性能
        
        return new WidgetTrayAnimationController(
            appWindow,
            rootElement,
            dispatcherQueue,
            windowHandle,
            getAnimationBounds,
            log);
    }

    /// <summary>
    /// 获取当前硬件级别
    /// </summary>
    public HardwarePerformanceLevel GetCurrentHardwareLevel() => _hardwareService.CurrentLevel;

    /// <summary>
    /// 获取推荐的最大帧率
    /// </summary>
    public int RecommendedMaxFPS => 
        _config.HighPriorityDurationMs > 0 ? _config.MaxFPS_HighPriority : _config.MaxFPS_Normal;

    public void Dispose()
    {
        // 清理资源
    }
}
