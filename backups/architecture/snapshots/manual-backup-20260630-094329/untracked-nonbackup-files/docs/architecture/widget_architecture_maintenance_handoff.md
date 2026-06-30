# DeskBox 格子架构维护交接说明

日期：2026-06-30

本文档给后续维护者使用，目标是说明这段时间 DeskBox 格子架构已经改到了什么状态、用了哪些技术路线、后面新增格子应该怎么走，以及哪些地方需要谨慎。

这不是最终架构白皮书。它更像一份开发交接笔记：能让自己或其他开发者快速理解当前基础，不至于接手后又把统一能力拆散。

## 一句话结论

DeskBox 目前的方向是：**物理形态继续保留多窗口，逻辑能力逐步统一到 Shell、Content、Factory、Registry、Session 和 Manager 上。**

也就是说：

- 不急着做全屏透明 `DeskBoxLayerWindow`。
- 不把所有格子塞进一个跨屏大窗口。
- 优先继续利用 Windows 原生窗口、WinUI 原生控件、系统拖拽、DWM、DPI 和多显示器能力。
- DeskBox 自己负责把不同格子接入同一套外壳、生命周期、菜单、动画、快捷键、层级和创建入口。

这个方向对 Win11 质感更稳。未来 Todo、天气、标签、音乐、监控等格子，应该优先复用当前统一基础，而不是每个格子重新写一套窗口和菜单逻辑。

## 当前已经完成了什么

### 1. WidgetKind 已经扩展

位置：

- `src/DeskBox/Models/WidgetConfig.cs`

当前 `WidgetKind` 已经包含：

- `File`：文件/收纳/映射格子。
- `QuickCapture`：随记格子。
- `Weather`：预留，天气格子。
- `Todo`：已实现，作为第一个内容型格子。
- `Tags`：预留，标签格子。
- `Music`：预留，系统媒体控制格子。
- `SystemMonitor`：预留，系统监控格子。
- `Productivity`：历史兼容值，只用于迁移旧配置。

注意：新增枚举不代表用户可以创建。是否能创建，要看 `WidgetRegistry` 和 `WidgetContentDescriptor`。

### 2. WidgetRegistry 负责“能不能创建窗口”

位置：

- `src/DeskBox/Services/WidgetRegistry.cs`

它现在是格子类型的总开关，核心字段是：

- `CanCreateWindow`
- `IsImplemented`

当前状态：

- `File`：可以创建，已实现。
- `QuickCapture`：可以创建，已实现。
- `Todo`：可以创建，已实现。
- `Weather` / `Tags` / `Music` / `SystemMonitor`：已登记，但还不能创建窗口。

这个分层很重要：未来可以先把某个格子登记为“已知类型”，但暂时不开放创建入口，避免半成品出现在用户界面。

### 3. WidgetContentDescriptor 负责“内容元信息”

位置：

- `src/DeskBox/Services/WidgetContentDescriptor.cs`
- `src/DeskBox/Services/WidgetContentFactory.cs`

`WidgetContentDescriptor` 记录：

- `WidgetKind`
- 默认标题
- 默认图标 glyph
- 内容阶段：`Implemented` 或 `Placeholder`
- 是否出现在“新建格子”入口
- 是否可用：`Available` 或 `Planned`
- 状态文案多语言 key
- 创建入口文案多语言 key

这让“更多格子入口”“状态页”“托盘新建菜单”“右键新建菜单”可以从同一份描述生成。

后面新增格子时，不建议在多个菜单里手写入口，应该先补 descriptor，再由入口统一读取。

### 4. IWidgetContent 定义了内容格子的统一接口

位置：

- `src/DeskBox/Contracts/IWidgetContent.cs`

当前接口包含：

- `Config`
- `WidgetId`
- `WidgetKind`
- `View`
- `InitializeAsync()`
- `RefreshAsync()`
- `ApplyAppearance()`
- `OnActivated()`
- `OnDeactivated()`

原则：

- 窗口、层级、动画、DWM、位置、大小不属于 Content。
- Content 只负责业务内容：Todo 列表、天气数据、标签列表、监控数据等。
- 业务内容需要响应主题、刷新、激活、失活时，通过接口回调处理。

### 5. WidgetShell 是统一外壳

位置：

- `src/DeskBox/Controls/WidgetShell.xaml`
- `src/DeskBox/Controls/WidgetShell.xaml.cs`

它提供：

- 标题栏
- 标题图标
- 标题文本
- 更多按钮
- 关闭按钮
- 内容插槽 `ShellContent`
- 可替换标题栏 `TitleBarContent`
- 标题栏右键、拖动、按钮事件

当前 Shell 已经作为内容型格子的外壳基础。历史文件格子和随记格子还带有不少旧逻辑，后续应逐步向 Shell 收口，但不要一次性大改。

### 6. WidgetShellContentHost 管理 Content 生命周期

位置：

- `src/DeskBox/Controls/WidgetShellContentHost.cs`

它负责把 `IWidgetContent` 放进 `WidgetShell`，并统一调用：

- 初始化
- 刷新
- 应用外观
- 激活
- 失活

未来内容型格子都应该通过这个 Host 接入 Shell，避免窗口直接操作业务 UserControl。

### 7. ContentWidgetWindow 是内容型格子的轻量宿主

位置：

- `src/DeskBox/Views/ContentWidgetWindow.xaml`
- `src/DeskBox/Views/ContentWidgetWindow.xaml.cs`
- `src/DeskBox/Services/ContentWidgetWindowFactory.cs`

它目前用于非文件类功能格子，已经接入：

- `WidgetShell`
- `IWidgetContent`
- `IDesktopWidgetWindow`
- 基础窗口样式
- DWM / acrylic / 背景外观
- 位置和大小
- 拖动和缩放
- 托盘/F7 显隐路径
- 临时置顶和恢复桌面层
- 基础右键菜单：锁定位置、锁定大小、重命名、删除

Todo 就是当前第一个真实跑在 `ContentWidgetWindow` 上的格子。

### 8. Todo 已经成为第一个内容型格子

相关位置：

- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs`
- `src/DeskBox/ViewModels/TodoWidgetViewModel.cs`
- `src/DeskBox/ViewModels/TodoItemViewModel.cs`
- `src/DeskBox/Models/TodoWidgetData.cs`
- `src/DeskBox/Models/TodoItem.cs`
- `src/DeskBox/Services/TodoWidgetStore.cs`

当前 Todo 的意义不只是功能本身，而是验证了这条路线：

`WidgetKind` -> `WidgetRegistry` -> `WidgetContentDescriptor` -> `WidgetContentFactory` -> `IWidgetContent` -> `ContentWidgetWindow` -> `WidgetManager`

后面天气、标签、音乐、监控都应该尽量沿用这条路径。

### 9. 新建格子入口已经开始 descriptor 化

当前托盘和文件格子的创建入口已经开始由：

- `WidgetContentFactory.GetCreateEntryDescriptors()`

驱动。

这意味着未来开放某个格子的创建入口，应该优先检查：

- `WidgetRegistry` 是否允许创建窗口。
- `WidgetContentDescriptor.CanShowInCreateEntry` 是否为 `true`。
- 多语言 key 是否完整。
- `WidgetManager.CreateWidgetOfKindAsync(...)` 是否能路由到正确创建逻辑。

## 统一能力应该怎么理解

后续所有格子，不管是文件、随记、Todo、天气、标签、音乐还是监控，都应该尽量共享下面这些能力。

### 应该统一复用的能力

这些能力属于 DeskBox 的“外壳和运行时”，不应该在每个格子里各自实现：

- 标题栏外观、图标、标题文本、按钮尺寸。
- 标题多语言刷新。
- 右键菜单字体、菜单间距、菜单生命周期。
- 更多菜单入口。
- 锁定位置、锁定大小、重命名、删除。
- 窗口拖动、窗口缩放、最小尺寸。
- 窗口背景、透明度、毛玻璃、圆角、主题。
- F7 唤起。
- 托盘唤起。
- 启动恢复。
- 隐藏/显示。
- 临时置顶。
- 恢复桌面层。
- 多窗口整体显隐。
- 动画策略。
- 设置页里的“更多格子”入口。
- 格子类型状态展示。
- 日志和诊断。

### 每个格子自己实现的能力

这些能力属于业务内容，应该放在对应 Content / ViewModel / Store 中：

- Todo：任务列表、完成状态、排序、持久化。
- 天气：定位权限、城市选择、天气 API、刷新频率。
- 标签：标签索引、文件与标签关系、标签筛选。
- 音乐：Windows 系统媒体会话读取、播放/暂停/上一首/下一首。
- 监控：CPU、内存、网络采样与显示。
- 文件格子：文件拖拽、排序、映射目录、快捷方式、重命名、打开方式。
- 随记：剪贴板监听、固定、最近记录、文本编辑。

### 一个判断标准

新增功能时可以先问：

“这个能力是不是所有格子都应该一致？”

如果是，就放到 Shell / Window / Manager / Session / Setting 这类统一层。

“这个能力是不是只有某一种格子才需要？”

如果是，就放到对应 Content / ViewModel / Store。

## 当前技术路线

### 保留多窗口

当前没有走全屏透明桌面层方案。原因：

- 多窗口更符合 Windows 桌面生态。
- WinUI / DWM / AppWindow / 原生拖拽和 DPI 行为更可控。
- 不需要接管桌面空白点击、桌面右键、多屏穿透等高风险行为。
- 出问题时可以按单个窗口排查，不会全局炸掉。

远期如果要做全屏 Host，也应该先实验验证，不应作为当前主线。

### 统一 Shell，而不是统一大窗口

`WidgetShell` 是 UI 外壳统一点，解决的是：

- 每种格子的标题栏别长得不一样。
- 菜单、按钮、边距、字体、hover 状态尽量一致。
- 新功能格子不需要从零写窗口 UI。

它不解决：

- 所有窗口必须合并到一个窗口。
- 文件拖拽业务。
- Todo / 天气 / 标签等业务逻辑。

### 内容接口隔离业务

`IWidgetContent` 的意义是让窗口只知道“这里有一个内容”，不关心内容是 Todo 还是天气。

后续新增格子，尽量做到：

- 窗口只处理窗口。
- Shell 只处理外壳。
- Content 只处理业务 UI。
- ViewModel 只处理业务状态。
- Store 只处理业务持久化。

### Registry 控制开放节奏

`WidgetKind` 可以提前有枚举，descriptor 可以提前有规划，但真正开放给用户要通过 registry 和 descriptor 双重控制。

推荐做法：

1. 先加 kind 和 descriptor，但不开放创建。
2. 做 placeholder 或隐藏验证。
3. 做真实 Content。
4. 接入 `ContentWidgetWindow`。
5. 手动测试通过后，再开放创建入口。

### Session 先记录，再逐步接管

当前 `WidgetSessionManager` 已经存在，但还属于第一阶段：记录会话状态，辅助统一思路。

位置：

- `src/DeskBox/Services/WidgetSessionManager.cs`

它当前有状态：

- `DesktopResting`
- `RaisedSession`
- `InteractionActive`
- `Hidden`

注意：窗口层级和动画现在还没有完全交给 SessionManager。很多实际行为仍在 `WidgetManager`、`WidgetWindow`、`QuickCaptureWidgetWindow`、`ContentWidgetWindow` 中。

后续不要急着把所有层级逻辑一次性搬过去。正确节奏是：

1. 先把事件来源接齐。
2. 先让日志能看懂状态变化。
3. 再把某一条行为链路收口。
4. 每次只迁移一类行为，比如 F7、托盘、菜单、设置页、外部点击恢复。

## 当前关键文件地图

### 架构核心

- `src/DeskBox/Models/WidgetConfig.cs`
- `src/DeskBox/Contracts/IWidgetContent.cs`
- `src/DeskBox/Services/WidgetRegistry.cs`
- `src/DeskBox/Services/WidgetContentDescriptor.cs`
- `src/DeskBox/Services/WidgetContentFactory.cs`
- `src/DeskBox/Services/ContentWidgetWindowFactory.cs`
- `src/DeskBox/Services/WidgetSessionManager.cs`
- `src/DeskBox/Services/WidgetManager.cs`

### Shell 和内容宿主

- `src/DeskBox/Controls/WidgetShell.xaml`
- `src/DeskBox/Controls/WidgetShell.xaml.cs`
- `src/DeskBox/Controls/WidgetShellContentHost.cs`
- `src/DeskBox/Views/ContentWidgetWindow.xaml`
- `src/DeskBox/Views/ContentWidgetWindow.xaml.cs`

### 已接入的内容型格子

- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs`
- `src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs`
- `src/DeskBox/ViewModels/TodoWidgetViewModel.cs`
- `src/DeskBox/Services/TodoWidgetStore.cs`

### 旧主力窗口

- `src/DeskBox/Views/WidgetWindow.xaml`
- `src/DeskBox/Views/WidgetWindow.xaml.cs`
- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml`
- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs`

这两个窗口仍然包含大量成熟但复杂的历史逻辑。后续要逐步抽，不要为了“架构漂亮”一次性重写。

### 应用入口和托盘

- `src/DeskBox/App.xaml`
- `src/DeskBox/App.xaml.cs`

托盘菜单、全局快捷键、语言刷新、设置页打开、应用启动恢复都和这里有关。

## 新增一个功能格子的推荐步骤

下面以 `Weather`、`Tags`、`SystemMonitor` 这类功能格子为例。

### 第 1 步：确认 WidgetKind 已存在

检查：

- `src/DeskBox/Models/WidgetConfig.cs`

如果已经有枚举，不要重复加。当前天气、标签、音乐、监控都已预留。

### 第 2 步：补 descriptor

检查：

- `src/DeskBox/Services/WidgetContentFactory.cs`

需要确认：

- 默认标题是否合理。
- glyph 是否合适。
- `ContentStage` 是否从 `Placeholder` 改成 `Implemented`。
- `Availability` 是否从 `Planned` 改成 `Available`。
- 是否开放 `CanShowInCreateEntry`。
- 多语言 key 是否补齐。

注意：不要只在一个菜单里硬编码入口。

### 第 3 步：做 Content UI

建议路径：

- `src/DeskBox/Controls/WidgetContents/XxxWidgetContent.xaml`
- `src/DeskBox/Controls/WidgetContents/XxxWidgetContent.xaml.cs`
- `src/DeskBox/Controls/WidgetContents/XxxWidgetContentAdapter.cs`

Adapter 实现 `IWidgetContent`。

如果业务复杂，再加：

- `src/DeskBox/ViewModels/XxxWidgetViewModel.cs`
- `src/DeskBox/Services/XxxWidgetStore.cs`
- `src/DeskBox/Models/XxxWidgetData.cs`

### 第 4 步：接入 WidgetContentFactory

新增类似：

- `CreateXxxContent(...)`
- `CreateDetachedContent(...)` 分支
- `CanCreateDetachedContent(...)` 分支

### 第 5 步：接入 ContentWidgetWindowFactory

如果走标准内容型格子，通常不需要新建窗口类，只需要确保 `ContentWidgetWindowFactory` 能拿到对应 content 和 descriptor。

### 第 6 步：接入 WidgetRegistry

实现稳定前：

- `CanCreateWindow: false`
- `IsImplemented: false`

实现稳定后：

- `CanCreateWindow: true`
- `IsImplemented: true`

### 第 7 步：接入 WidgetManager 创建逻辑

检查：

- `WidgetManager.CreateWidgetOfKindAsync(...)`
- `WidgetManager.ShowWidgetAsync(...)`
- `WidgetManager.CreateRegisteredWidgetFromConfigAsync(...)`
- 内容型格子批量显隐路径

原则：新功能格子应该走 `ContentWidgetWindow`，不要再复制 `WidgetWindow`。

### 第 8 步：补多语言

需要检查所有语言资源：

- 创建入口
- 默认标题
- 设置页状态
- 空态文案
- 右键菜单新增项
- 错误提示

尤其注意标题多语言：用户自定义标题不应该被语言切换覆盖；默认标题可以随语言更新。

### 第 9 步：加测试

优先测试：

- descriptor 是否正确。
- registry 是否控制创建。
- factory 是否创建正确 content。
- 数据 store 是否不会丢数据。
- WidgetKind 序列化兼容。

### 第 10 步：手动验收

至少测：

- 新建格子。
- 重启恢复。
- F7 唤起和隐藏。
- 托盘唤起。
- 拖动窗口。
- 缩放窗口。
- 重命名。
- 锁定位置。
- 锁定大小。
- 删除格子。
- 语言切换。
- 主题切换。
- 透明度 / 毛玻璃。
- 设置页打开时层级是否正确。

## 后续功能建议顺序

### 1. 先收口 Todo v1

原因：Todo 已经跑通内容型格子链路，是最佳样板。

建议先补：

- Todo 的基础交互细节。
- 空态。
- 重启持久化验证。
- 标题默认文案和多语言。
- 键盘操作。
- 右键菜单是否需要 Todo 专属项。

Todo 做稳后，后面功能格子都有参考模板。

### 2. 再做系统监控

原因：第一版只做 CPU、内存、网络，相对封闭，不涉及账号、不涉及定位、不涉及第三方 API。

建议：

- 只做展示，不做过度复杂图表。
- 采样逻辑放 Service。
- UI 放 Content。
- 设置项先少量，例如刷新频率。

### 3. 再做天气

原因：天气会碰到定位权限、城市选择、API、网络失败、缓存、隐私说明。

第一版建议：

- 支持手动城市。
- 支持请求定位权限，但不要强依赖定位。
- 定位失败必须能继续手动选城市。
- API key 和接口错误要有兜底。

### 4. 再做标签

原因：标签系统会影响文件格子的右键菜单、索引、搜索、跨格子文件引用。

第一版边界建议保持：

- 只存 DeskBox 内部索引。
- 不写入文件本身。
- 不写 NTFS alternate stream。
- 不改文件属性。

### 5. 音乐控制靠后

原因：Windows 系统媒体会话有平台差异，不同播放器兼容性不一定一致。

第一版只接：

- 当前系统媒体会话。
- 播放 / 暂停。
- 上一首 / 下一首。
- 标题 / 艺术家 / 封面如果能稳定拿到再展示。

### 6. 格子合并最后做

原因：合并格子会牵涉窗口模型、子格子状态、顶部 tab、拖拽命中、悬停切换、配置结构和数据迁移。

第一版边界已经确认：

- 只支持同类文件格子合并。
- 不先支持 Todo + 文件混合。
- 不先支持天气 + 监控混合。

建议等 Shell、Content、Session 更稳之后再做。

## 高风险区域

这些地方之前反复修过，后续动代码要特别小心。

### 文件拖拽

涉及：

- Win10 / Win11 差异。
- UAC 是否开启。
- 进程完整性级别。
- `StorageItems` / `Shell IDList Array` / `Preferred DropEffect`。
- 外部拖入和格子内部拖动不是一回事。
- 快捷方式 `.lnk` 有特殊路径和排序问题。

不要只在本机 Win11 测一下就认为拖拽没问题。

### 重命名输入法

之前修过文件重命名无法切换中文输入法、Esc 保存而不是取消的问题。

后续改键盘事件、TextBox、Focus、PreviewKeyDown 时要回归：

- 中文输入。
- 输入法切换。
- Esc 取消。
- Enter 保存。
- 失焦保存。

### 层级和 F7

涉及：

- `PushToBottom`
- `HoldTemporaryTopMost`
- `RestoreDesktopLayer`
- 外部点击恢复
- 设置页激活
- 菜单打开和关闭
- 多窗口批量动画

这个区域不要分散到各个格子里各写一套。

### 菜单字体和间距

托盘菜单、格子右键、标题右键要尽量共享同一套样式。

涉及：

- `App.xaml` 的 `MenuFlyoutPresenter`
- `MenuFlyoutItem`
- `ToggleMenuFlyoutItem`
- `MenuFlyoutSubItem`
- 原生字体 fallback
- Win10 / Win11 字体差异

后续新增菜单项时，不要局部写死字体、字号和 padding。

### 设置页

设置页已经承载很多功能：

- 外观
- 快捷键
- 开机自启
- 托盘图标
- 拖拽诊断 / UAC 修复
- 随记设置
- 更多格子入口

新增格子设置时，优先考虑：

- 是否属于全局设置。
- 是否属于某个格子实例。
- 是否应该放进“更多格子”或对应格子的设置页。

不要把实例设置和全局设置混在一起。

### 安装器

涉及：

- 安装路径。
- 是否允许用户选择安装路径。
- 开机自启默认勾选。
- 卸载时是否保留数据。
- UAC / 管理员权限。
- 图标资源更新。
- 本地数据目录 `AppData\Local\DeskBox`。

安装器改动需要真实覆盖安装、卸载重装、保留数据、不保留数据都测。

## 构建和测试命令

常用命令：

```powershell
dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore
dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build
dotnet build .\DeskBox.sln -c Release -p:Platform=x64 --no-restore
```

当前最近一次完整验证的测试数量是：

```text
179/179
```

启动 Debug 版：

```powershell
Start-Process -FilePath "D:\project\wingezi\src\DeskBox\bin\x64\Debug\net8.0-windows10.0.22621.0\DeskBox.exe" -WorkingDirectory "D:\project\wingezi\src\DeskBox\bin\x64\Debug\net8.0-windows10.0.22621.0"
```

## 手动回归清单

每次动 Shell、Window、Manager、Session、菜单、设置后，至少测这些：

- 启动后文件格子、随记格子、Todo 格子都能恢复。
- 托盘右键菜单字体正常。
- 格子标题右键菜单字体正常。
- 格子内容右键菜单字体正常。
- F7 可以整体唤起。
- 再按 F7 行为符合预期。
- 托盘左键行为符合预期。
- 点击外部窗口后格子能恢复桌面层。
- 打开设置页时，设置页不会被格子压住。
- 新建文件格子。
- 新建 Todo 格子。
- 删除 Todo 格子。
- 重命名 Todo 格子。
- 锁定位置和锁定大小。
- 语言切换后菜单和标题刷新正常。
- 主题切换后背景、字体、图标正常。
- 文件格子外部拖入文件。
- 文件格子内部拖动排序。
- 快捷方式 `.lnk` 能拖动。
- 文件重命名可以输入中文。
- Esc 取消重命名。
- 随记最近内容可以滚动。
- 剪贴板记录重启后仍能按设置工作。

## 后续开发的推荐节奏

### 短期：先把当前基础变成稳定样板

建议先做：

1. 完成 Todo v1 的交互细节。
2. 给内容型格子补足测试。
3. 再检查 `ContentWidgetWindow` 和旧 `WidgetWindow` / `QuickCaptureWidgetWindow` 的能力差距。
4. 能收口到 Shell 的外观和菜单，继续小步收口。

### 中期：新增一个低风险功能格子

建议优先系统监控，而不是天气或合并格子。

原因：

- 不需要定位权限。
- 不需要外部账号。
- 不需要复杂文件索引。
- 能继续验证内容型格子路线。

### 中长期：再做天气、标签、音乐

这些功能都可以做，但要把边界控制住：

- 天气第一版别追求复杂城市搜索和完整预报。
- 标签第一版只做内部索引。
- 音乐第一版只做系统媒体会话，不单独适配每个播放器。

### 最后：再做格子合并

合并格子是高风险结构功能，不建议在当前阶段马上做。

如果要做，建议先单独写设计文档，确认：

- 数据结构怎么保存。
- 子格子 ID 怎么稳定。
- tab 怎么映射子格子。
- 悬停 100ms 切换如何避免误触。
- 同类文件格子合并后，映射格子和收纳格子是否允许混合。
- 拖拽到 tab、拖拽到内容区、拖拽离开窗口时怎么处理。

## 维护原则

1. 能用 WinUI / Windows 原生能力，就不要自绘一套复杂控件。
2. 能复用 Shell，就不要为新格子复制标题栏。
3. 能走 ContentWidgetWindow，就不要新建一个功能格子专用窗口。
4. 能由 descriptor 生成入口，就不要在多个菜单里硬编码。
5. 能通过 Registry 控制开放，就不要用临时 if 散落各处。
6. 每次只迁移一条行为链路。
7. 每次结构调整前做本地备份或 checkpoint。
8. 改完核心窗口逻辑必须手动测试，不要只看 build 通过。

## 当前基础的边界

当前基础已经能支持继续扩展功能格子，但还没有完全完成所有统一收口。

已经比较明确的：

- 内容型格子的创建链路已经跑通。
- Todo 可以作为后续功能格子的样板。
- 创建入口开始统一 descriptor 化。
- 内容接口和 Shell 已经存在。
- 内容型窗口已经接入生命周期和基础菜单。

仍需继续收口的：

- 文件格子和随记格子仍有不少历史窗口逻辑。
- SessionManager 还没有完全接管层级和动画。
- 动画策略仍主要在 `WidgetManager` 和具体窗口里。
- 设置页里的“更多格子”能力还可以继续和 descriptor / registry 对齐。
- 所有格子的菜单、标题、窗口状态还可以进一步统一。

所以后续开发时，不要把“已经有基础”理解成“可以随便开大功能”。当前最适合的方式还是小步推进：每做一个新格子或统一能力，都让它沉淀到这套基础里。
