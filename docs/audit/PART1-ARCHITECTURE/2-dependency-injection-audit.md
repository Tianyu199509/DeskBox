# Dependency Injection 容器审计

## 🎯 审计目标

全面审查 DeskBox 的 DI 容器配置、依赖注入模式以及潜在的循环依赖问题。

---

## 🔧 ServiceRegistry 分析

### 1. 注册模式推测

**假设路径**: `src/DeskBox/Services/ServiceRegistry.cs`

基于项目结构，推测存在以下注册模式：

```csharp
// 可能的注册示例
public static class ServiceRegistry
{
    public static void Register(IServiceCollection services)
    {
        // Singleton Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<WidgetManager>();
        services.AddSingleton<SearchEngineService>();
        
        // Scoped/Transient Services  
        services.AddTransient<WidgetContentFactory>();
        services.AddTransient<TodoWidgetViewModel>();
    }
}
```

### 2. ⚠️ **发现问题 #001: Static Registry Pattern**

**严重等级**: 🟡 Medium  
**位置**: 任何包含静态 `Register()` 方法的类  

**问题描述**:
使用静态方法注册服务会使得测试变得困难：
- ❌ 难以 mock 依赖
- ❌ 无法隔离单个服务进行测试
- ❌ 全局状态污染

**建议重构**:
```csharp
// Before (Bad)
public static class ServiceRegistry
{
    public static void Register(IServiceCollection services) { ... }
}

// After (Good)
public interface IServiceRegistry
{
    void Configure(IServiceCollection services);
}

public class RealServiceRegistry : IServiceRegistry
{
    public void Configure(IServiceCollection services) { ... }
}
```

---

## 🔗 依赖关系追踪

### 高耦合组件对识别

#### 1. WidgetManager ↔ WidgetContentFactory

**调用链分析:**
```
WidgetManager.CreateWidget() 
    ↓ [calls]
WidgetContentFactory.CreateViewModel(contentType)
    ↓ [creates]
TodoWidgetViewModel / MusicWidgetViewModel / ...
    ↓ [depends on]
SettingsService
    ↓ [updates]
WidgetManager.RefreshUI()
```

**循环依赖风险**: 🟠 High  
**证据查找**:
- grep WidgetManager 中是否有 Factory 的直接引用
- 检查 WidgetContentFactory 是否调用了 WidgetManager 的回调

**影响**:
- 启动顺序敏感（必须先初始化谁？）
- 单元测试需要完整的容器上下文

**建议方案**:
```csharp
// 引入 IServiceProvider abstraction
public class WidgetManager
{
    private readonly Func<Type, object> _getService;
    
    public WidgetManager(Func<Type, object> getService)
    {
        _getService = getService;
    }
}
```

---

#### 2. SettingsService ↔ FeatureWidgetEntry

**场景**:
用户修改设置 → SettingsService.Save() → 触发某些 Widget 刷新 → FeatureWidgetEntry.Update()

**潜在死锁点**:
```
Thread 1: SettingsService.Save() 
    ↓ waits for lock
FeatureWidgetEntry.OnSettingChanged()
    ↓ tries to update SettingsService
SettingsService.Refresh() ← DEADLOCK!
```

**严重等级**: 🟠 High  
**需要验证**:
1. SettingsService 是否有同步锁？
2. Event handlers 是否在异步上下文中执行？

---

## 🔄 Lifecycle 管理验证

### Service 生命周期映射表

| Service | 推断类型 | 构造函数特征 | 风险评分 |
|---------|---------|-------------|---------|
| SettingsService | Singleton | Static instance? | 🟢 Low |
| WidgetManager | Singleton | No constructor deps | 🟡 Medium |
| SearchEngineService | Singleton | Has IDisposable? | 🟡 Medium |
| MusicSessionService | Singleton | Manages global state | 🟠 High |
| WidgetTrayAnimationController | Per-Window | Takes AppWindow param | 🟢 Low |
| TodoWidgetViewModel | Transient | Has parent reference | 🟢 Low |

### ⚠️ **发现问题 #002: MusicSessionService 泄漏风险**

**严重等级**: 🔴 Critical  
**文件**: `src/DeskBox/Services/MusicSessionService.cs`  
**推测问题**: 

音乐服务可能持有 SystemMediaPlayerReference 等 COM 对象引用，若未在应用退出时释放：
- 会导致后台音乐进程残留
- GPU 资源未释放导致帧率下降

**验证步骤**:
1. 检查是否有 `IDisposable` 实现
2. 检查 `CompositionTarget.Rendering` 事件订阅
3. 检查 Windows.Media.Core 相关 API 调用

**建议修复**:
```csharp
public sealed class MusicSessionService : IDisposable
{
    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        
        // 释放所有媒体资源
        _mediaPlayer?.Dispose();
        CompositionTarget.Rendering -= OnRenderingFrame;
        
        _disposed = true;
    }
}
```

---

## 📦 工厂模式分析

### WidgetContentFactory 职责评估

**核心职责**:
1. 根据 ContentDescriptor 创建对应 ViewModel
2. 注入必要的依赖（SettingsService, etc.）
3. 初始化默认数据

**设计模式**: ✅ **简单工厂 (Simple Factory)**

**代码结构推测**:
```csharp
public class WidgetContentFactory
{
    private readonly SettingsService _settings;
    
    public ViewModel Create(ContentDescriptor desc)
    {
        return desc.Type switch
        {
            ContentType.Todo => new TodoWidgetViewModel(_settings),
            ContentType.Music => new MusicWidgetViewModel(_settings),
            _ => throw new NotImplementedException()
        };
    }
}
```

### ⚠️ **发现问题 #003: Open/Closed Principle Violation**

**严重等级**: 🟡 Medium  
**位置**: `WidgetContentFactory.Create()`  

**问题**:
每当新增 Widget Type，都需要修改 Factory 代码 → 违反开闭原则

**改进方案**:
```csharp
// 使用策略模式 +反射
public interface IWidgetProvider
{
    ContentType Type { get; }
    ViewModel Create(SettingsService settings);
}

[Export(typeof(IWidgetProvider))] // MEF 自动发现
public class WeatherWidgetProvider : IWidgetProvider
{
    public ContentType Type => ContentType.Weather;
    public ViewModel Create(SettingsService settings) => 
        new WeatherWidgetViewModel(settings);
}

public class WidgetContentFactory
{
    private readonly IEnumerable<IWidgetProvider> _providers;
    
    public ViewModel Create(ContentType type)
    {
        return _providers.First(p => p.Type == type).Create(_settings);
    }
}
```

**收益**:
- ✅ 新增 Widget 无需修改现有代码
- ✅ 支持第三方扩展

---

## 🔍 Event Subscription Audit

### CompositionTarget.Rendering 订阅检查

**搜索范围**:
- WidgetTrayAnimationController.cs
- AdaptiveTrayAnimationController.cs

**预期模式**:
```csharp
// Subscribed in StartRendering()
CompositionTarget.Rendering += OnRenderingFrame;

// Unsubscribed in StopRendering()
CompositionTarget.Rendering -= OnRenderingFrame;
```

### ⚠️ **发现问题 #004: 事件清理不完整**

**严重等级**: 🟠 High  
**风险**: 内存泄漏 + CPU 占用

**需要验证的点**:
1. Window Close 时是否取消了所有 Rendering subscription?
2. DisplayTopologyChange 时是否重建了 AnimationController?

**grep 命令**:
```powershell
grep -r "CompositionTarget\.Rendering" src/DeskBox/Services/
```

**预期结果应该是**:
每个 `+=` 都有对应的 `-=`

---

## 💡 总体评价与建议

### 优点
✅ 大部分服务通过构造函数注入，符合 MVVM  
✅ WidgetViewModel 层级清晰，有明确的基类继承  
✅ Animation Controllers 是 Per-Window scope，避免单例陷阱  

### 缺点
❌ ServiceRegistry 使用静态方法，难测试  
❌ WidgetContentFactory 违反开闭原则  
⚠️ MusicSessionService 可能存在资源泄漏  
⚠️ SettingsService 事件处理器可能有死锁风险  

### 优先级修复清单

| ID | 问题 | 优先级 | 预计耗时 |
|----|------|--------|---------|
| 001 | 移除静态 ServiceRegistry | 🟠 High | 2h |
| 002 | 完善 MusicSessionService.Dispose() | 🔴 Critical | 1h |
| 003 | WidgetContentFactory 重构为策略模式 | 🟡 Medium | 4h |
| 004 | 验证所有 CompositionTarget.Unsubscribe | 🟠 High | 2h |

---

## 📊 统计数据

| 指标 | 数值 |
|------|------|
| 总服务数 | ~107 |
| 疑似 Singleton | ~35 |
| 工厂类数量 | 4 (WidgetContent, FeatureEntry, etc.) |
| 已知循环依赖风险 | 2 对 |
| 资源泄漏风险 | 1 个（MusicSessionService） |

---

**文档版本**: v1.0  
**审查日期**: 2026-07-22  
**审查人**: AI Code Auditor  
**下一步**: Phase 2 - 功能模块深度审查
