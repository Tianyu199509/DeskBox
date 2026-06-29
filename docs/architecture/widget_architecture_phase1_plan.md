# DeskBox 第一阶段格子架构铺底重构方案

版本：草案 v2  
范围：第一阶段架构铺底，不做格子合并，不做全屏 DeskBoxLayerWindow，不新增天气/Todo/标签/音乐/监控正式功能。  
目标：保留 Windows 原生质感和多窗口稳定性，为后续功能格子扩展打地基。

## 0. 总结结论

第一阶段不追求“大一统重写”，而是做一次低风险、可回滚的结构整理：

1. 继续保留“每个格子一个独立 WinUI Window”的物理形态。
2. 把所有格子的共同外壳抽成统一 `WidgetShell`。
3. 把每种格子的内容抽成 `IWidgetContent` / `WidgetContent`。
4. 把窗口显隐、托盘、F7、临时置顶、恢复到底层逐步收口到统一会话管理。
5. 所有视觉和交互优先使用 WinUI 3 / Windows App SDK 原生控件，不自绘一套仿 Windows 控件。

第一阶段完成后，用户看到的行为应该基本不变；开发上应该变成“新增一个功能格子时，只写内容，不复制一套窗口和层级逻辑”。

## 1. 产品与技术原则

### 1.1 必须坚持

- 保留多窗口形态。每个桌面格子仍是独立窗口，继续享受 Windows 原生窗口边界、拖放、DPI、任务栏/桌面交互能力。
- 使用 WinUI 原生控件。菜单用 `MenuFlyout`，列表用 `ListView` / `GridView`，开关用 `ToggleSwitch`，选择用 `ComboBox`，数字输入用 `NumberBox`。
- 使用 Windows App SDK / AppWindow / DWM 能力处理窗口质感，不用全屏透明大窗口模拟桌面层。
- 第一阶段只做结构铺底，不新增复杂业务功能。
- 每一步都要可以单独构建、启动、回滚。
- 先迁移随记，再迁移普通文件格子。随记业务相对独立，适合作为 Shell 试点。
- 保持现有用户数据兼容，不改动已有 `settings.json` 的含义。

### 1.2 明确不做

- 不做全屏 DeskBoxLayerWindow。
- 不做跨屏透明 Widget Host。
- 不接管桌面空白区域点击、右键、拖放。
- 不做格子合并 / Tab 子格子功能。
- 不新增天气、标签、Todo、音乐、系统监控正式入口。
- 不把普通文件格子和随记强行揉成一个巨大 ViewModel。
- 不重写文件拖拽核心逻辑。
- 不改变安装器、更新机制、网站发布流程。

### 1.3 原生质感守则

每次修改 UI 时优先按以下顺序选择：

1. WinUI 原生控件默认样式。
2. 基于 WinUI 默认样式 `BasedOn` 做轻量 Setter。
3. 全局资源统一字体、间距、圆角、菜单样式。
4. 必要时使用 Fluent 图标或 `FontIcon`。
5. 最后才考虑自定义 `ControlTemplate`。

禁止为了短期效果直接自绘菜单、自绘 `ToggleSwitch`、自绘 `ComboBox`、自绘窗口阴影，或用 `WebView` 承载格子 UI。

## 2. 当前架构现状

当前核心类型：

- `WidgetConfig`：保存位置、大小、名称、显示状态、锁定状态、`WidgetKind`、文件格子数据。
- `WidgetKind`：当前有 `File`、`QuickCapture`、`Productivity`，其中 `Productivity` 是旧值迁移保留。
- `WidgetWindow`：普通收纳格子 / 映射格子的窗口，包含窗口管理、标题栏、内容区、拖拽、菜单、动画、层级等大量逻辑。
- `QuickCaptureWidgetWindow`：随记格子窗口，与 `WidgetWindow` 重复了很多窗口、标题栏、菜单、层级、动画逻辑。
- `WidgetManager`：当前同时管理文件格子和随记格子，已有 `IDesktopWidgetWindow` 接口，内部有 `_widgets` 和 `_quickCaptureWidgets` 两套字典。

主要问题不是“功能做不了”，而是每新增一种格子都会遇到重复窗口、重复标题栏、重复菜单、重复拖动缩放、重复托盘/F7、重复层级恢复等问题。如果不重构，后续新增天气、标签、Todo、音乐、系统监控时，会出现 5 到 7 套相似窗口代码，维护成本会快速上升。

## 3. 第一阶段目标架构

```text
WidgetHostWindow
  - 物理窗口
  - Win32 / AppWindow / DWM / DPI / 位置 / 显隐 / 层级
  - 不写具体业务 UI

WidgetShell
  - 通用外壳
  - 标题栏、图标、右侧按钮、右键菜单入口
  - 边框、背景、圆角、透明度、主题、字体、Toast
  - 内容插槽 ContentPresenter

IWidgetContent
  - 每种格子的内容契约
  - 文件格子内容
  - 随记内容
  - 未来天气 / Todo / 标签 / 音乐 / 系统监控内容

WidgetManager
  - 管理所有 WidgetConfig
  - 按 WidgetKind 创建对应内容
  - 创建窗口宿主
  - 接入会话管理

WidgetSessionManager
  - 后续逐步接管托盘/F7/临时置顶/恢复到底层
```

第一阶段不要求一次性删除 `WidgetWindow` 和 `QuickCaptureWidgetWindow`。建议采用“旁路搭桥”的方式：新增 Shell / Content 接口，先让新代码承载随记，再让普通文件格子逐步接入，旧窗口逻辑保留一段时间方便对比和回滚。

## 4. 建议目录

```text
src/DeskBox/Contracts
  IWidgetContent.cs
  IWidgetHostWindow.cs
  IWidgetShellHost.cs

src/DeskBox/Controls
  WidgetShell.xaml
  WidgetShell.xaml.cs
  WidgetTitleBar.xaml
  WidgetTitleBar.xaml.cs

src/DeskBox/Controls/WidgetContents
  FileWidgetContent.xaml
  FileWidgetContent.xaml.cs
  QuickCaptureWidgetContent.xaml
  QuickCaptureWidgetContent.xaml.cs

src/DeskBox/Services
  WidgetRegistry.cs
  WidgetSessionManager.cs
  WidgetWindowFactory.cs
```

第一阶段不必一次性全部创建，按阶段推进。

## 5. 核心接口设计

### 5.1 IWidgetContent

```csharp
public interface IWidgetContent
{
    WidgetConfig Config { get; }
    string WidgetId { get; }
    WidgetKind WidgetKind { get; }
    FrameworkElement View { get; }

    Task InitializeAsync();
    Task RefreshAsync();
    void ApplyAppearance();
    void OnActivated();
    void OnDeactivated();
}
```

`View` 返回具体 WinUI `UserControl`。`InitializeAsync` 用于加载文件、随记数据、未来天气数据等。`RefreshAsync` 用于菜单刷新、文件刷新、天气刷新等。`ApplyAppearance` 响应主题、透明度、字号、图标大小变化。`OnActivated` / `OnDeactivated` 只处理内容层需要知道的激活状态，不处理窗口层级。

### 5.2 IWidgetHostWindow

当前已有 `IDesktopWidgetWindow`，第一阶段先扩展或新建内部接口，不要马上重命名全项目。

```csharp
internal interface IWidgetHostWindow
{
    string WidgetId { get; }
    WidgetKind WidgetKind { get; }
    IntPtr WindowHandle { get; }
    bool Visible { get; }
    Windows.Foundation.Rect AnimationBounds { get; }

    void SetContent(IWidgetContent content);
    void ApplyAppearancePreview();

    void PrepareTrayShowAnimation();
    void ShowPreparedAtDesktopLayer(bool persistVisibility = true);
    void ShowPreparedRaisedFromTray(bool persistVisibility = true);
    void PlayTrayShowAnimation();
    bool PrepareTrayHideAnimation(bool persistVisibility = true);
    void PlayPreparedTrayHideAnimation();

    void ActivateRaisedFromTrayBatch();
    void EnsureRaisedFromTrayTopMost();
    void ForceRestoreDesktopLayerFromManager();
    void RestoreDesktopLayerFromManager();
    void HideWindow();
}
```

### 5.3 WidgetShell

Shell 只负责外壳，不直接调用 `WidgetManager.RemoveWidgetAsync()` 这类业务动作。建议暴露事件给窗口或管理器。

```csharp
public sealed partial class WidgetShell : UserControl
{
    public event EventHandler? TitlePointerPressed;
    public event EventHandler? CloseRequested;
    public event EventHandler? MoreRequested;
    public event EventHandler? RenameRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? ContentInteractionStarted;
    public event EventHandler? ContentInteractionEnded;
}
```

输入属性建议：

```csharp
public string Title { get; set; }
public IconElement? TitleIcon { get; set; }
public bool IsPositionLocked { get; set; }
public bool IsSizeLocked { get; set; }
public FrameworkElement? ContentView { get; set; }
```

## 6. WidgetKind 扩展策略

第一阶段可以定义未来枚举，但不开放 UI：

```csharp
public enum WidgetKind
{
    File,
    QuickCapture,
    Weather,
    Todo,
    Tags,
    Music,
    SystemMonitor,
    Productivity
}
```

注意：

- 新枚举可以先存在，但不要在设置页/托盘/菜单开放入口。
- `SettingsService.NormalizeSettings` 不能把新枚举误改回 `File`。
- 对不支持创建的 `WidgetKind`，`WidgetManager` 应记录日志并跳过，不应崩溃。

短期不为每个功能格子新增大量字段到 `WidgetConfig` 顶层。建议新增：

```csharp
public Dictionary<string, string> Metadata { get; set; } = [];
```

复杂数据放独立 store：

```text
%LocalAppData%/DeskBox/data/widgets/{widgetId}/
```

例如 `todo.json`、`tags-index.json`、`weather-cache.json`。

## 7. 分阶段施工计划

### 阶段 A：基线与全局 UI 规范

目标：先把所有格子共用的 UI 资源收口，降低后续新增组件漏样式的概率。

任务：

- 检查 `App.xaml` 全局资源。
- 统一菜单字体、菜单项字体、菜单子项字体。
- 统一通用按钮样式命名。
- 统一 `ToolTip`、`MenuFlyout`、`ToggleMenuFlyoutItem` 视觉。
- 不改具体业务逻辑。

验收：

- 托盘右键菜单字体正确。
- 普通格子标题右键菜单字体正确。
- 普通格子内容区右键菜单字体正确。
- 随记标题右键菜单字体正确。
- 随记记录右键菜单字体正确。
- 设置页自定义菜单字体正确。
- `dotnet build -c Release -p:Platform=x64 --no-restore` 通过。

### 阶段 B：WidgetKind 与配置层铺底

目标：让配置模型可以承载未来格子类型，但不开放新功能入口。

任务：

- 扩展 `WidgetKind`。
- 增加 `WidgetConfig.Metadata`。
- 调整 `SettingsService.NormalizeSettings`：允许未来内置类型存在，仍迁移旧 `Productivity`，未知值安全降级。
- 给 `WidgetManager` 增加 `IsSupportedWidgetKind` / `CanCreateWidgetKind`。
- 对未实现类型记录日志并跳过恢复。

验收：

- 老用户 settings 正常加载。
- 旧 `Productivity` 仍按当前规则迁移。
- 手动在配置里放一个未来 `WidgetKind.Weather`，应用启动不崩溃。
- 当前 `File` / `QuickCapture` 行为不变。

### 阶段 C：WidgetRegistry / WidgetFactory

目标：把 `WidgetManager` 中散落的 `File or QuickCapture` 判断逐步替换为注册表/工厂。

第一阶段只注册：

- `File`
- `QuickCapture`

新增类型时入口集中，不需要在多个地方重复加 `if`。

### 阶段 D：抽取 WidgetShell 试点

目标：先抽出通用外壳，但不急着迁移普通文件格子。

先迁移随记，因为随记是功能格子，和未来天气/Todo/监控更像。`QuickCaptureWidgetWindow` 仍负责 WindowHandle、AppWindow、DWM、PushToBottom / TopMost、动画、DPI / bounds；`WidgetShell` 负责标题栏、图标、按钮、菜单入口、背景、边框、圆角和内容插槽。

验收：

- 随记外观和迁移前一致。
- 随记拖动、缩放、关闭正常。
- 随记记录/固定/最近 tab 正常。
- 随记右键菜单正常。
- 随记 F7 / 托盘层级正常。
- 普通文件格子完全不受影响。

### 阶段 E：普通文件格子接入 WidgetShell

普通文件格子比随记复杂很多：文件拖入、拖出、中文重命名、选择框、多选、映射文件夹、收纳文件夹、排序、右键菜单、删除确认、拖回桌面等都在这一层，迁移要更谨慎。

第一轮只移动内容区 XAML，不移动核心代码逻辑。Win32 subclass、OLE 拖放兼容、IME 处理、PushToBottom / topmost、托盘动画、多显示器 bounds 都先留在 `WidgetWindow.xaml.cs`。

验收：

- 收纳格子拖入文件正常。
- 映射格子拖入/打开/刷新正常。
- 格子内文件拖动正常。
- 快捷方式 `.lnk` 拖动正常。
- 中文重命名正常。
- ESC 取消重命名不保存。
- 内容区右键菜单正常。
- 标题栏右键菜单正常。
- F7 / 托盘行为正常。

### 阶段 F：WidgetSessionManager 第一版

目标：先把会话状态定义出来，不要马上替换全部层级逻辑。

```csharp
public enum WidgetSessionState
{
    DesktopResting,
    RaisedSession,
    InteractionActive,
    Hidden
}
```

第一版只做记录和协调，不强行夺权：

- 记录当前是否 Raised。
- 记录当前是否有菜单/拖拽/编辑交互。
- 统一日志。
- 暴露查询方法给 `WidgetManager`。

第二版再逐步接管是否隐藏、是否恢复桌面层、何时安装/移除鼠标 hook、何时确认 topmost、何时保存可见性。

### 阶段 G：统一窗口生命周期

目标：让 `WidgetWindow` 和 `QuickCaptureWidgetWindow` 共享更多生命周期逻辑。

优先抽组合对象，不建议一开始做 `BaseWidgetWindow : Window`。

推荐：

```text
WidgetWindowChromeController
WidgetWindowAnimationController
WidgetWindowLayerController
```

## 8. 测试矩阵

每个阶段至少验证：

- 全新数据启动。
- 老数据覆盖启动。
- 有普通格子启动。
- 有随记格子启动。
- 没有任何格子启动。
- 托盘左键唤起/隐藏。
- F7 唤起/隐藏/恢复。
- 点击外部窗口后恢复桌面层。
- 收纳格子拖入文件。
- 映射格子拖入文件。
- 格子内拖动文件。
- `.lnk` 快捷方式拖动。
- 文件中文重命名。
- ESC 取消重命名。
- 删除文件。
- 内容区和标题栏右键菜单。
- 随记记录/固定/最近 tab。
- 随记文本添加、图片记录、剪贴板记录。
- 随记单击复制、双击编辑、右键菜单。
- 设置页打开、主题切换、字号/图标大小/透明度调整。
- 单屏 100%、单屏 150%、双屏同缩放、双屏不同缩放。

## 9. 风险清单

高风险：

- 文件拖拽。
- 中文重命名 IME。
- 托盘/F7 层级。
- 多窗口动画同步。
- 设置页和格子窗口层级关系。
- 随记剪贴板监听。

中风险：

- 菜单样式。
- 标题栏按钮。
- 锁定位置/大小。
- 主题切换。
- 透明度和背景。

低风险：

- 纯资源抽取。
- 枚举扩展但不开放入口。
- 日志增强。
- 文档更新。

## 10. 回滚策略

每个阶段都必须有独立 commit。每个 commit 必须满足：

- 可以构建。
- 可以启动。
- 当前核心功能可用。
- 不依赖后续 commit 才能运行。

如果某阶段出问题，优先 revert 当前阶段 commit，不要手工乱修多个阶段。

## 11. 执行护栏

每个开发批次只允许命中一个主目标：

- 只抽资源，不迁移窗口。
- 只扩展模型，不改 UI。
- 只加 Registry，不迁移 Shell。
- 只迁移随记 Shell，不碰文件格子拖拽。
- 只迁移文件格子内容外壳，不重写文件业务。
- 只记录 Session 状态，不夺取窗口层级控制权。

禁止顺手做这些事：

- 顺手改设置页信息架构。
- 顺手改安装包。
- 顺手改拖拽核心实现。
- 顺手改文件排序规则。
- 顺手改随记数据格式。
- 顺手改窗口动画算法。
- 顺手新增功能入口。
- 顺手删除旧逻辑 fallback。

## 12. 已确认的产品决策

这些决策作为后续功能格子的默认方向：

1. Todo 需要独立格子，不放进随记作为主入口。
2. 天气第一版需要申请定位权限，同时支持用户手动选择城市。
3. 标签系统只存在 DeskBox 内部索引，不写入文件本身。
4. 音乐格子只支持 Windows 系统媒体会话，不为单个播放器做私有适配。
5. 系统监控格子第一版只做 CPU / 内存 / 网络，GPU 延后。
6. 格子合并第一版只支持同类文件格子合并，不支持任意类型混合合并。

## 13. 后续功能接入顺序建议

第一阶段完成后，建议功能接入顺序：

1. 天气格子：独立内容，低耦合，适合验证新格子接入流程。
2. 系统监控基础格子：先做 CPU / 内存 / 网络，GPU 延后。
3. 音乐控制格子：验证系统媒体会话兼容。
4. 独立 Todo 格子：使用 `WidgetKind.Todo` 和独立 `TodoWidgetContent`。
5. 标签系统：需要文件索引和路径追踪，风险较高。
6. 格子合并 / Tab：等 Shell/Content 稳定后再做。

## 14. 开始实施前的最终检查

正式开始阶段 A 前，先确认：

1. 当前主分支可以 Release 构建。
2. 当前版本已完成 GitHub 备份。
3. 当前安装包和 exe 发版产物已保存。
4. 当前已知问题单独记录，不混入重构目标。
5. 重构分支单独创建，例如 `codex/widget-architecture-phase1`。
6. 每个阶段完成后先手测，再进入下一阶段。

如果其中任意一项不满足，不建议开始 Shell 或 Session 级别重构。
