# DeskBox 插件系统架构方案

> 版本：v1.0 调研版
> 日期：2026-06-18
> 状态：仅自己维护，暂不开放给用户

---

## 一、背景与目标

DeskBox 当前支持两种组件类型：`WidgetKind.File`（文件收纳/文件夹映射）和 `WidgetKind.Productivity`（已废弃的旧值，保留用于迁移）。用户希望扩展组件体系，支持计算器、待办、便签、剪贴板历史、日历提醒等完全不同形态的"插件组件"。

核心诉求：
- 自己维护插件代码，不需要开放给用户编写
- 不同插件的形态差异极大（有的要存数据，有的要读剪贴板，有的要系统通知）
- 保障 DeskBox 主程序的性能与稳定性（插件崩了不影响主程序）
- 为未来开放插件广场保留架构扩展空间

---

## 二、现有架构分析

### 2.1 WidgetConfig 模型

```csharp
public class WidgetConfig
{
    public string Id { get; set; }
    public string Name { get; set; }
    public double X, Y, Width, Height;
    public WidgetKind WidgetKind { get; set; } = WidgetKind.File;
    public ViewMode ViewMode { get; set; } = ViewMode.Icon;
    public bool IsVisible, IsDisabled, IsPositionLocked, IsSizeLocked;
    public string? MappedFolderPath;
    public bool FollowsDefaultStoragePath;
    public string? ManagedFolderName;
    public List<WidgetItemConfig> Items { get; set; }
}
```

### 2.2 WidgetKind 枚举

```csharp
public enum WidgetKind
{
    File,
    Productivity  // 旧值，保留用于迁移
}
```

### 2.3 关键约束

- `WidgetManager` 负责组件生命周期（创建、恢复、删除、迁移）
- `WidgetWindow` 是每个组件独立的 WinUI 窗口
- `WidgetViewModel` 负责数据和交互逻辑
- 设置文件 `AppSettings.Widgets` 序列化所有组件配置
- 当前所有 `WidgetManager` 中的逻辑都硬编码检查 `WidgetKind.File`

---

## 三、三轮迭代思考

### 第一轮：技术路线选择

有三种可选方案：

#### 方案 A：原生 .NET 插件（AssemblyLoadContext + MEF）

原理：每个插件是一个 .NET DLL，通过 `AssemblyLoadContext` 隔离加载，MEF 负责发现和组合。

优点：
- 性能最好，原生渲染
- 可以直接使用 WinUI 3 控件
- 和 DeskBox 现有架构最契合

缺点：
- `AssemblyLoadContext` 只隔离程序集，不隔离内存和线程。插件抛未捕获异常会影响主进程。
- 插件访问主程序对象没有天然边界，需要自己设计接口层。
- WinUI 3 的 XAML 资源在不同 ALC 中共享比较棘手，主题/本地化需要额外桥接。
- 未来开放给用户时，安全风险很高（DLL 可以做任何事）。
- 热更新困难，替换 DLL 需要重启进程。

适用场景：自己维护、对性能和原生感要求极高的组件。

#### 方案 B：WebView2 插件

原理：每个插件组件内部嵌一个 WebView2 控件，插件是 HTML/CSS/JS。

优点：
- 进程隔离：WebView2 运行在独立子进程中，崩了不影响 DeskBox 主进程。
- 安全边界天然存在：通过 JS SDK 控制插件能访问什么。
- 开发门槛低，前端技术栈。
- 热更新友好：替换 HTML 文件即可。
- 未来开放给用户时最适合。

缺点：
- 首次加载 WebView2 有冷启动开销（约 200-500ms），每个插件组件一个 WebView2 实例。
- 视觉上不是 100% 原生 WinUI 感（虽然 WebView2 可以用 WinUI 主题色 CSS 变量拟合）。
- 插件之间共享数据需要桥接层。
- 内存占用比原生高（每个 WebView2 约 30-80MB 额外）。

适用场景：需要隔离、未来开放、插件形态多样的场景。

#### 方案 C：混合方案（原生壳 + WebView2 内容区）

原理：插件组件的窗口壳（标题栏、拖拽、置顶、圆角）由 DeskBox 原生渲染，内容区根据插件类型选择原生 WinUI 控件或 WebView2。

优点：
- 原生壳保证体验一致（动画、毛玻璃、圆角）
- 简单插件（计算器、待办）用原生控件，性能好
- 复杂插件（剪贴板历史、Markdown 编辑器）用 WebView2，隔离好
- 灵活度最高

缺点：
- 实现复杂度最高，需要维护两条技术栈
- 插件清单需要声明渲染方式
- 测试矩阵翻倍

#### 第一轮结论

**推荐方案 C（混合方案），但分两步走：**

- **Step 1**：先用原生 .NET 组件方式做内置插件（计算器、待办、便签）。这些插件是你自己写的，不需要隔离，性能优先。
- **Step 2**：引入 WebView2 运行时，用于复杂插件或未来开放给用户的第三方插件。

这样你不会被锁死在某一条技术路线上。

---

### 第二轮：插件分类与权限模型

#### 4.1 插件类型分类

根据功能形态，插件可以分为四大类：

| 类型 | 代表插件 | 渲染方式 | 数据需求 | 系统权限 |
|------|----------|----------|----------|----------|
| **展示型** | 时钟、天气、系统信息 | 原生控件 | 只读配置 | 无特殊 |
| **交互型** | 计算器、待办、便签 | 原生控件 | 需持久化存储 | 无特殊 |
| **系统集成型** | 剪贴板历史、日历提醒、音量控制 | 原生控件 | 需持久化存储 | 剪贴板监听、日历读取、系统通知 |
| **富内容型** | Markdown 编辑器、画板、浏览器书签 | WebView2 | 需持久化存储 | 可能需要网络、文件读写 |

#### 4.2 插件清单设计（plugin.json）

```json
{
  "id": "deskbox.builtin.calculator",
  "name": "计算器",
  "version": "1.0.0",
  "author": "DeskBox",
  "description": "桌面计算器组件",
  "icon": "icon.png",
  "renderer": "native",
  "widgetDefaults": {
    "width": 240,
    "height": 320,
    "minWidth": 200,
    "minHeight": 280
  },
  "permissions": [],
  "entry": "DeskBox.Plugins.Calculator"
}
```

```json
{
  "id": "deskbox.builtin.clipboard",
  "name": "剪贴板历史",
  "version": "1.0.0",
  "author": "DeskBox",
  "description": "记录剪贴板历史，快速粘贴",
  "icon": "icon.png",
  "renderer": "native",
  "widgetDefaults": {
    "width": 320,
    "height": 450,
    "minWidth": 260,
    "minHeight": 300
  },
  "permissions": ["clipboard.read"],
  "entry": "DeskBox.Plugins.ClipboardHistory"
}
```

```json
{
  "id": "deskbox.builtin.markdown",
  "name": "Markdown 笔记",
  "version": "1.0.0",
  "author": "DeskBox",
  "description": "轻量 Markdown 编辑与预览",
  "icon": "icon.png",
  "renderer": "webview",
  "widgetDefaults": {
    "width": 400,
    "height": 500,
    "minWidth": 300,
    "minHeight": 350
  },
  "permissions": ["storage"],
  "entry": "index.html",
  "webviewConfig": {
    "allowNetwork": false,
    "allowDevTools": false
  }
}
```

#### 4.3 权限体系

| 权限标识 | 含义 | 风险等级 | 示例插件 |
|----------|------|----------|----------|
| `storage` | 读写插件自己的数据目录 | 低 | 所有需持久化的插件 |
| `clipboard.read` | 监听/读取系统剪贴板 | 中 | 剪贴板历史 |
| `clipboard.write` | 写入系统剪贴板 | 中 | 剪贴板历史、密码生成器 |
| `notification` | 发送 Windows 系统通知 | 低 | 日历提醒、待办到期 |
| `filesystem.read` | 读取用户指定的文件/目录 | 中 | 文件预览、Markdown 笔记 |
| `filesystem.write` | 写入用户指定的文件/目录 | 高 | 笔记导出 |
| `network` | 访问外部网络 | 高 | 天气、RSS、翻译 |
| `systeminfo` | 读取 CPU/内存/磁盘使用率 | 低 | 系统监控小组件 |

**权限模型原则：**
- 自己维护的插件：全部放行，不需要弹窗确认。
- 未来开放时：每个权限在插件清单中声明，安装时展示给用户，敏感权限（network、filesystem.write）需要确认。
- 权限粒度在 `plugin.json` 的 `permissions` 数组中声明。

#### 4.4 数据隔离设计

每个插件有独立的数据目录：

```
%APPDATA%/DeskBox/plugins/
  deskbox.builtin.calculator/
    config.json          # 插件配置（用户偏好设置）
    data.json            # 插件数据（待办列表、便签内容等）
  deskbox.builtin.todo/
    config.json
    data.json
  deskbox.builtin.clipboard/
    config.json
    data.json            # 剪贴板历史（可设上限自动清理）
    blobs/               # 大文本/图片单独存放
```

WebView2 插件额外需要：
```
%APPDATA%/DeskBox/plugins/
  deskbox.builtin.markdown/
    config.json
    data/
      notes/             # 笔记文件
    web/                 # 插件前端文件
      index.html
      app.js
      style.css
```

---

### 第三轮：性能、体验与安全的综合方案

#### 5.1 架构总览

```
┌─────────────────────────────────────────────────────────┐
│                    DeskBox 主进程                         │
│                                                          │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────┐ │
│  │ WidgetManager│  │ PluginRegistry│ │ PluginDataService│ │
│  │ (生命周期)   │  │ (插件注册表) │  │ (数据目录管理)    │ │
│  └──────┬──────┘  └──────┬──────┘  └────────┬─────────┘ │
│         │                │                   │           │
│  ┌──────┴────────────────┴───────────────────┴────────┐  │
│  │              WidgetWindow (壳，原生 WinUI)           │  │
│  │  ┌─────────────────────────────────────────────┐   │  │
│  │  │            内容区域                           │   │  │
│  │  │  ┌─────────────┐   ┌────────────────────┐   │   │  │
│  │  │  │ Native Panel│   │   WebView2 Panel   │   │   │  │
│  │  │  │ (原生 WinUI) │   │ (HTML/CSS/JS 插件) │   │   │  │
│  │  │  └─────────────┘   └────────────────────┘   │   │  │
│  │  └─────────────────────────────────────────────┘   │  │
│  └────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

#### 5.2 核心接口设计

```csharp
/// 插件必须实现的接口（原生插件）
public interface IDeskBoxPlugin
{
    string PluginId { get; }
    PluginManifest Manifest { get; }

    /// 创建插件的内容控件
    FrameworkElement CreateContent(PluginContext context);

    /// 插件获得焦点时调用
    void OnActivated();

    /// 插件失去焦点时调用
    void OnDeactivated();

    /// 释放资源
    void Dispose();
}

/// 插件运行时上下文，由 DeskBox 主程序注入
public class PluginContext
{
    /// 插件自己的数据目录
    public string DataDirectory { get; }

    /// 插件配置读写
    public IPluginStorage Storage { get; }

    /// 发送系统通知
    public INotificationService Notifications { get; }

    /// 剪贴板访问（需要 clipboard.read / clipboard.write 权限）
    public IClipboardService? Clipboard { get; }

    /// 主题色与当前主题
    public ThemeInfo CurrentTheme { get; }

    /// 本地化服务
    public ILocalizationService Localization { get; }
}
```

```csharp
/// 插件存储抽象
public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
    void Remove(string key);
    void Save();
}
```

```csharp
/// WebView2 插件的 JS SDK（注入到 WebView2 中）
/// 调用方式：window.DeskBox.storage.get("todos")
public class WebViewPluginBridge
{
    // 向 WebView 注入以下 JS API：
    // DeskBox.storage.get(key) -> Promise<any>
    // DeskBox.storage.set(key, value) -> Promise<void>
    // DeskBox.storage.remove(key) -> Promise<void>
    // DeskBox.theme.get() -> Promise<{mode, accentColor}>
    // DeskBox.clipboard.readText() -> Promise<string>  // 需权限
    // DeskBox.clipboard.writeText(text) -> Promise<void>  // 需权限
    // DeskBox.notify(title, body) -> Promise<void>  // 需权限
    // DeskBox.log.info(msg) / .warn(msg) / .error(msg)
}
```

#### 5.3 WidgetConfig 扩展

```csharp
public class WidgetConfig
{
    // ... 现有字段保持不变 ...

    /// 插件组件专用字段
    public string? PluginId { get; set; }

    /// 插件自定义配置（JSON 字符串）
    public string? PluginSettings { get; set; }
}

public enum WidgetKind
{
    File,
    Plugin         // 新增：插件组件
}
```

这样 WidgetConfig 的序列化向后兼容，旧配置文件不需要迁移。

#### 5.4 PluginRegistry（插件注册表）

```csharp
public sealed class PluginRegistry
{
    private readonly Dictionary<string, PluginManifest> _manifests = new();
    private readonly Dictionary<string, IDeskBoxPlugin> _instances = new();

    /// 启动时扫描内置插件目录 + 用户插件目录
    public void DiscoverPlugins();

    /// 根据 PluginId 创建插件实例
    public IDeskBoxPlugin? CreateInstance(string pluginId, PluginContext context);

    /// 获取所有已注册的插件清单
    public IReadOnlyList<PluginManifest> GetManifests();
}
```

插件目录结构：
```
DeskBox/
  plugins/
    calculator/
      plugin.json
      DeskBox.Plugins.Calculator.dll    # 原生插件
    todo/
      plugin.json
      DeskBox.Plugins.Todo.dll
    clipboard-history/
      plugin.json
      DeskBox.Plugins.ClipboardHistory.dll
    markdown/
      plugin.json
      web/
        index.html
        app.js
        style.css
```

#### 5.5 性能策略

| 问题 | 策略 |
|------|------|
| WebView2 冷启动 | 启动 DeskBox 时预初始化一个 WebView2 环境（共享），所有 WebView 插件复用同一环境，只是各自创建独立的 WebView2 控件 |
| 插件数量多时内存 | 只在组件可见时加载插件实例，隐藏超过 5 分钟的插件卸载到待机状态，下次显示时重新加载 |
| 插件数据读写频繁 | IPluginStorage 使用内存缓存 + 定时批量写入（debounce 500ms），不阻塞 UI |
| WebView2 渲染性能 | 限制 WebView2 插件的 GPU 加速策略；内容区域尺寸变化时使用 requestAnimationFrame 节流 |
| 首次扫描插件 | 插件清单用 `plugin.json` 而非反射扫描 DLL，只需读文件，毫秒级 |

#### 5.6 安全策略（当前阶段：自己维护）

当前阶段你不需要沙箱隔离，但仍建议：

1. **接口隔离**：所有插件通过 `PluginContext` 接口访问主程序能力，不直接引用 `App.Current` 或 `WidgetManager`。
2. **异常边界**：插件的 `CreateContent`、`OnActivated`、`OnDeactivated` 都包在 try-catch 中，异常只记录日志、不影响主程序。
3. **资源限制**：WebView2 插件配置 `CoreWebView2EnvironmentOptions` 限制子进程数量。
4. **数据备份**：插件数据目录统一在 `%APPDATA%/DeskBox/plugins/`，方便备份和清理。

#### 5.7 迁移路径（面向未来开放）

如果未来要开放给用户：

1. **签名验证**：插件 DLL 需要 Authenticode 签名，未签名的拒绝加载。
2. **沙箱加强**：WebView2 插件天然沙箱；原生插件改为 `AssemblyLoadContext` 隔离 + 接口白名单。
3. **权限确认**：安装插件时弹窗展示所需权限。
4. **插件广场**：远程 `manifest.json` 索引 + 下载 + 校验 + 更新。
5. **审核机制**：人工审核或自动扫描（静态分析 DLL API 调用）。

当前架构已经保留了这些扩展点（`plugin.json` 声明式清单、`PluginContext` 接口隔离、数据目录独立），迁移成本很低。

---

## 四、推荐的插件列表与实现顺序

### 第一批：纯原生、无特殊权限

| 插件 | 优先级 | 实现难度 | 说明 |
|------|--------|----------|------|
| **计算器** | P0 | 低 | 纯 UI，无数据持久化，最适合验证插件框架 |
| **待办清单** | P1 | 低 | 需要 storage 权限，验证数据持久化链路 |
| **便签** | P1 | 低 | 类似待办，富文本/纯文本存储 |

### 第二批：需要系统权限

| 插件 | 优先级 | 实现难度 | 说明 |
|------|--------|----------|------|
| **剪贴板历史** | P2 | 中 | 需要 clipboard.read 权限，Windows 消息监听 |
| **日历/提醒** | P2 | 中 | 需要 notification 权限，定时任务 |
| **快速启动** | P3 | 中 | 用户自定义应用/命令快捷方式 |

### 第三批：WebView2 插件

| 插件 | 优先级 | 实现难度 | 说明 |
|------|--------|----------|------|
| **Markdown 笔记** | P3 | 高 | 需要引入 WebView2 运行时，验证 WebView 插件链路 |
| **画板/白板** | P3 | 高 | WebView2 + Canvas |
| **RSS 阅读** | P4 | 高 | 需要 network 权限 |

---

## 五、文件结构规划

```
src/DeskBox/
  Plugins/
    IPlugin.cs                    # 插件核心接口
    PluginManifest.cs             # plugin.json 反序列化模型
    PluginContext.cs              # 运行时上下文
    PluginRegistry.cs             # 发现、注册、实例化
    PluginDataService.cs          # 数据目录 + 存储抽象
    PluginWidgetHost.cs           # 插件组件的窗口内容宿主
    WebViewPluginHost.cs          # WebView2 插件的特殊宿主
    WebViewPluginBridge.cs        # JS SDK 注入
    Permissions/
      PermissionSet.cs            # 权限集合
      PermissionChecker.cs        # 运行时权限校验

  BuiltinPlugins/
    Calculator/
      CalculatorPlugin.cs
      CalculatorControl.xaml
      plugin.json
    Todo/
      TodoPlugin.cs
      TodoControl.xaml
      plugin.json
    StickyNote/
      StickyNotePlugin.cs
      StickyNoteControl.xaml
      plugin.json
    ClipboardHistory/
      ClipboardHistoryPlugin.cs
      ClipboardHistoryControl.xaml
      plugin.json

plugins/                          # 插件运行时目录（构建产物复制）
  calculator/
    plugin.json
  todo/
    plugin.json
  ...
```

---

## 六、风险与注意事项

| 风险 | 说明 | 缓解措施 |
|------|------|----------|
| WidgetWindow 改造成本 | 当前 WidgetWindow 硬编码了文件组件的全部逻辑，需要重构出内容区域插槽 | 抽取内容区为 `ContentPresenter`，文件组件和插件组件共享壳 |
| WebView2 运行时体积 | WebView2 Runtime 约 150MB，如果内嵌会增大发布体积 | 使用 Evergreen 模式，依赖系统已安装的 WebView2 Runtime，首次运行提示用户安装 |
| 插件崩溃影响 | 原生插件在同一进程，未捕获异常可能崩溃整个应用 | 全局异常处理 + 接口调用全部 try-catch；WebView2 插件天然隔离 |
| 设置文件膨胀 | 插件配置全部存入 AppSettings 会让 JSON 变大 | 插件配置独立存储在各自的 `config.json`，AppSettings 只存 `PluginId` + `PluginSettings`（压缩 JSON 字符串） |
| 多插件同时运行的内存 | 每个 WebView2 实例额外占用 30-80MB | 限制同时活跃的 WebView2 插件数量；不可见超过 5 分钟的自动释放 |

---

## 七、总结

| 维度 | 推荐方案 |
|------|----------|
| 技术路线 | 混合方案：原生 .NET 组件（简单插件）+ WebView2（复杂/富内容插件） |
| 插件清单 | `plugin.json` 声明式，包含渲染方式、权限、入口点、默认尺寸 |
| 权限模型 | 声明式权限，当前阶段自动放行，保留未来用户确认弹窗接口 |
| 数据隔离 | 每个插件独立 `%APPDATA%/DeskBox/plugins/{id}/` 目录 |
| 性能策略 | 延迟加载 + 内存缓存 + WebView2 环境共享 + 不可见自动释放 |
| 安全策略 | 接口隔离 + 异常边界 + 插件目录限制，未来扩展签名验证和审核 |
| 实现顺序 | 计算器 → 待办/便签 → 剪贴板/日历 → Markdown/WebView2 插件 |
| 与现有架构的兼容 | `WidgetKind.Plugin` 新枚举值 + `WidgetConfig.PluginId` 字段，向后兼容 |
