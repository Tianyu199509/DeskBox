// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// 设备性能级别枚举
/// </summary>
public enum HardwarePerformanceLevel
{
    Low,      // 低性能（笔记本、集成显卡）
    Medium,   // 中等性能（普通台式机）
    High      // 高性能（游戏本、独显）
}

/// <summary>
/// 智能动画配置 - 根据硬件性能和场景自适应
/// </summary>
public sealed record AdaptiveAnimationConfig(
    int MaxFPS_HighPriority,
    int MaxFPS_Normal,
    double HighPriorityDurationMs,
    bool UseBatchGrouping,
    int BatchGroupDelayMs,
    bool EnableGPUTurboMode,
    string Reasoning
);

/// <summary>
/// 硬件性能检测与自适应动画控制器
/// </summary>
public sealed class HardwareAdaptiveAnimationService
{
    private const int MeasurementWindowMs = 500;
    private const int MinFrameCountForAccurateMeasurement = 30;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string>? _logger;
    private DateTime _lastMeasureTime;
    private int _measuredFrameCount;
    private double _measuredRenderDuration;
    private HardwarePerformanceLevel _cachedLevel;
    private bool _isMeasuring;

    public HardwarePerformanceLevel CurrentLevel => _cachedLevel;

    public HardwareAdaptiveAnimationService(DispatcherQueue dispatcherQueue, Action<string>? logger = null)
    {
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;
        
        // 默认初始化为中档性能
        _cachedLevel = HardwarePerformanceLevel.Medium;
        _lastMeasureTime = DateTime.MinValue;
    }

    /// <summary>
    /// 启动性能测量（简化版：不再使用 CompositionTarget）
    /// </summary>
    public void StartPerformanceMeasurement()
    {
        // 简化版：什么都不做
        _logger?.Invoke("[HardwareAdaptive] Measurement started (skipped)");
    }

    /// <summary>
    /// 停止性能测量并返回配置建议（简化版）
    /// </summary>
    public AdaptiveAnimationConfig StopAndGetConfiguration()
    {
        // 简化：直接基于 CPU 使用率判断，不依赖 CompositionTarget
        var cpuUsage = MeasureCpuUsage();
        
        if (cpuUsage < 0.15)
        {
            _cachedLevel = HardwarePerformanceLevel.High;
        }
        else if (cpuUsage < 0.4)
        {
            _cachedLevel = HardwarePerformanceLevel.Medium;
        }
        else
        {
            _cachedLevel = HardwarePerformanceLevel.Low;
        }
        
        var config = GenerateAdaptiveConfig(_cachedLevel);
        _logger?.Invoke($"[HardwareAdaptive] CPU={cpuUsage:P0}, Level: {_cachedLevel}");
        
        return config;
    }

    /// <summary>
    /// 性能测量回调 - 简化版：不需要 CompositionTarget
    /// </summary>
    private void OnMeasurementRenderingFrame(object sender, object e)
    {
        _measuredFrameCount++;
    }

    // 移除对 CompositionTarget 的引用，改用 Timer 测量

    /// <summary>
    /// 根据实测帧率判断硬件级别
    /// </summary>
    private HardwarePerformanceLevel DetermineHardwareLevel(double measuredFps)
    {
        // 在测量窗口内，如果 FPS >= 55 说明硬件能稳定跑满高帧率
        if (measuredFps >= 55 && _measuredFrameCount >= MinFrameCountForAccurateMeasurement)
        {
            return HardwarePerformanceLevel.High;
        }
        
        // 如果 FPS >= 40 说明性能还可以
        if (measuredFps >= 40)
        {
            return HardwarePerformanceLevel.Medium;
        }

        return HardwarePerformanceLevel.Low;
    }

    /// <summary>
    /// 生成自适应配置
    /// </summary>
    private AdaptiveAnimationConfig GenerateAdaptiveConfig(HardwarePerformanceLevel level)
    {
        return level switch
        {
            // 高性能：激进策略，充分利用 GPU
            HardwarePerformanceLevel.High => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: 120,     // 超高优先级
                MaxFPS_Normal: 60,             // 正常阶段也保持流畅
                HighPriorityDurationMs: 80.0,  // 延长高优先级时间
                UseBatchGrouping: true,        // 启用批次分组
                BatchGroupDelayMs: 3,          // 极短的组间延迟
                EnableGPUTurboMode: true,      // 开启 GPU Turbo 模式
                Reasoning: "High-end hardware detected: max performance mode enabled"
            ),

            // 中等性能：平衡策略
            HardwarePerformanceLevel.Medium => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: 120,      // 启动时仍然高帧率
                MaxFPS_Normal: 30,             // 之后降为适中帧率
                HighPriorityDurationMs: 50.0,  // 标准优先级持续时间
                UseBatchGrouping: true,        // 启用批次分组
                BatchGroupDelayMs: 5,          // 适中延迟
                EnableGPUTurboMode: false,     // 不使用 GPU Turbo
                Reasoning: "Balanced performance: good smoothness with moderate resource usage"
            ),

            // 低性能：节能策略
            HardwarePerformanceLevel.Low => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: 60,       // 降低高优先级帧率
                MaxFPS_Normal: 16,             // 进一步降低以保持流畅
                HighPriorityDurationMs: 30.0,  // 缩短高优先级时间
                UseBatchGrouping: true,        // 必须启用批次分组
                BatchGroupDelayMs: 10,         // 较长延迟避免卡顿
                EnableGPUTurboMode: false,     // 禁用 GPU Turbo
                Reasoning: "Low-end hardware detected: prioritize stability over frame rate"
            ),

            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }

    /// <summary>
    /// 基于已知硬件级别的配置生成（避免测量开销）
    /// </summary>
    public AdaptiveAnimationConfig GetConfigForKnownLevel(HardwarePerformanceLevel level)
    {
        return GenerateAdaptiveConfig(level);
    }

    private static double MeasureCpuUsage()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var totalCpuTime = process.TotalProcessorTime.TotalSeconds;
            var elapsedTime = Math.Max((DateTime.UtcNow - process.StartTime).TotalSeconds, 1.0); // Avoid division by zero
            
            return Math.Min(totalCpuTime / elapsedTime, 1.0);
        }
        catch
        {
            return 0.5; // Default to medium
        }
    }
}
