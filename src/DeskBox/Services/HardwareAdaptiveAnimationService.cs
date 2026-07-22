// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Display;
using System.Runtime.InteropServices;

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
/// 智能动画配置 - 根据硬件性能、屏幕刷新率和场景自适应
/// </summary>
public sealed record AdaptiveAnimationConfig(
    int MaxFPS_HighPriority,
    int MaxFPS_Normal,
    double HighPriorityDurationMs,
    bool UseBatchGrouping,
    int BatchGroupDelayMs,
    bool EnableGPUTurboMode,
    string Reasoning,
    
    // 新增：屏幕相关配置
    int SourceRefreshRate,        // 源屏幕刷新率
    int TargetRefreshRate,        // 目标屏幕刷新率
    bool IsCrossScreenAnimation   // 是否跨屏动画
);

/// <summary>
/// 硬件性能检测与自适应动画控制器
/// </summary>
public sealed class HardwareAdaptiveAnimationService
{
    private const int MeasurementWindowMs = 500;
    private const int MinFrameCountForAccurateMeasurement = 30;
    
    // VRR 支持检测
    private bool _supportsVRR;
    
    // 性能阈值配置
    private const double CpuThresholdHigh = 0.15;
    private const double CpuThresholdMedium = 0.40;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string>? _logger;
    private DateTime _lastMeasureTime;
    private int _measuredFrameCount;
    private double _measuredRenderDuration;
    private HardwarePerformanceLevel _cachedLevel;
    private bool _isMeasuring;
    
    // 新增：屏幕感知相关
    private static readonly Dictionary<IntPtr, int> s_screenCache = new(); // windowHandle -> refreshRate
    private static readonly object s_cacheLock = new();
    private const int s_cacheExpirationMs = 5000; // 5 秒有效期

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
    /// 停止性能测量并返回配置建议（带屏幕感知）
    /// </summary>
    public AdaptiveAnimationConfig StopAndGetConfiguration(IntPtr? windowHandle = null)
    {
        // 1. 获取硬件级别
        var cpuUsage = MeasureCpuUsage();
        
        if (cpuUsage < CpuThresholdHigh)
        {
            _cachedLevel = HardwarePerformanceLevel.High;
        }
        else if (cpuUsage < CpuThresholdMedium)
        {
            _cachedLevel = HardwarePerformanceLevel.Medium;
        }
        else
        {
            _cachedLevel = HardwarePerformanceLevel.Low;
        }
        
        // 2. 获取屏幕信息
        int sourceRefreshRate = GetScreenRefreshRate(windowHandle);
        int targetRefreshRate = sourceRefreshRate; // 简化版：假设同屏
        bool isCrossScreen = false;
        
        // 3. VRR 检测
        _supportsVRR = CheckVRRSupport();
        
        // 4. 根据屏幕刷新率和硬件级别生成配置
        var config = GenerateAdaptiveConfig(_cachedLevel, sourceRefreshRate, targetRefreshRate, isCrossScreen);
        
        _logger?.Invoke($"[HardwareAdaptive] CPU={cpuUsage:P0}, Level={_cachedLevel}, " +
                       $"SourceFPS={sourceRefreshRate}Hz, TargetFPS={targetRefreshRate}Hz, VRR={_supportsVRR}");
        
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
    /// 生成自适应配置（带屏幕感知）
    /// </summary>
    private AdaptiveAnimationConfig GenerateAdaptiveConfig(HardwarePerformanceLevel level, 
                                                           int sourceRefreshRate,
                                                           int targetRefreshRate,
                                                           bool isCrossScreen)
    {
        // ⭐【强制模式】直接以显示器刷新率为目标，不做任何节流
        // 不管性能如何，确保动画帧率与显示器同步
        var baseFPS = sourceRefreshRate;  // 直接使用显示器刷新率
        
        if (isCrossScreen)
        {
            // 跨屏时取较高者
            baseFPS = Math.Max(sourceRefreshRate, targetRefreshRate);
        }
        
        return level switch
        {
            // ⭐【强制模式】所有硬件都使用显示器最大刷新率
            HardwarePerformanceLevel.High => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: baseFPS,           // 直接使用显示器刷新率
                MaxFPS_Normal: baseFPS,                 // 始终以满帧运行
                HighPriorityDurationMs: 999999.0,       // 无限时高优先级阶段
                UseBatchGrouping: true,                  // 启用批次分组
                BatchGroupDelayMs: 5,                    // 适中延迟保证流畅
                EnableGPUTurboMode: false,               // 禁用 GPU Turbo（不稳定）
                SourceRefreshRate: sourceRefreshRate,
                TargetRefreshRate: targetRefreshRate,
                IsCrossScreenAnimation: isCrossScreen,
                Reasoning: $"MAX_PERFORMANCE_MODE: RefreshRate={baseFPS}Hz, VRR={_supportsVRR}, no throttling applied"
            ),

            // ⭐【强制模式】中等性能也拉满
            HardwarePerformanceLevel.Medium => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: baseFPS,           // 直接使用显示器刷新率
                MaxFPS_Normal: baseFPS,                 // 始终满帧
                HighPriorityDurationMs: 999999.0,       // 无降级
                UseBatchGrouping: true,                  // 启用批次分组
                BatchGroupDelayMs: 5,                    // 适中延迟保证流畅
                EnableGPUTurboMode: false,               // 禁用 GPU Turbo
                SourceRefreshRate: sourceRefreshRate,
                TargetRefreshRate: targetRefreshRate,
                IsCrossScreenAnimation: isCrossScreen,
                Reasoning: $"MAX_PERFORMANCE_MODE: RefreshRate={baseFPS}Hz, no performance degradation"
            ),

            // ⭐【强制模式】低性能同样拉满
            HardwarePerformanceLevel.Low => new AdaptiveAnimationConfig(
                MaxFPS_HighPriority: baseFPS,           // 直接使用显示器刷新率
                MaxFPS_Normal: baseFPS,                 // 不降帧！
                HighPriorityDurationMs: 999999.0,       // 持续高帧率
                UseBatchGrouping: true,                  // 必须启用批次分组
                BatchGroupDelayMs: 8,                    // 稍大延迟防止卡顿
                EnableGPUTurboMode: false,               // 禁用 GPU Turbo
                SourceRefreshRate: sourceRefreshRate,
                TargetRefreshRate: targetRefreshRate,
                IsCrossScreenAnimation: isCrossScreen,
                Reasoning: $"MAX_PERFORMANCE_MODE: RefreshRate={baseFPS}Hz, stability prioritized but no FPS drop"
            ),

            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }

    /// <summary>
    /// 基于已知硬件级别的配置生成（避免测量开销）
    /// </summary>
    public AdaptiveAnimationConfig GetConfigForKnownLevel(HardwarePerformanceLevel level, int sourceRefreshRate = 60, int targetRefreshRate = 60, bool isCrossScreen = false)
    {
        return GenerateAdaptiveConfig(level, sourceRefreshRate, targetRefreshRate, isCrossScreen);
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
    
    /// <summary>
    /// 获取窗口所在屏幕的刷新率
    /// </summary>
    private int GetScreenRefreshRate(IntPtr? windowHandle)
    {
        if (windowHandle == null || windowHandle.Value == IntPtr.Zero)
            return 60; // 默认值
        
        try
        {
            // 简化版：暂时返回默认值
            // TODO: 后续使用 Windows.Graphics.Display 命名空间的 API 获取实际刷新率
            
            return 60; // TODO: 完善刷新率获取逻辑
        }
        catch
        {
            return 60; // 失败时返回默认值
        }
    }
    
    /// <summary>
    /// 检查 VRR 支持（G-Sync/FreeSync）
    /// </summary>
    private bool CheckVRRSupport()
    {
        try
        {
            // 通过注册表检查 NVIDIA G-Sync 或 AMD FreeSync 支持
            var registryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Technology");
            
            if (registryKey != null)
            {
                var technology = registryKey.GetValue(null)?.ToString();
                return technology?.Contains("NVIDIA") == true || 
                       technology?.Contains("AMD") == true;
            }
        }
        catch
        {
            // 忽略错误
        }
        
        return false;
    }
}
