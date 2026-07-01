# DeskBox 格子标题样式与窗口外壳模式设计

日期：2026-06-30

本文档用于梳理“标准 / 紧凑 / 悬浮 / 隐藏”标题样式的设计边界和开发关联点。目标不是推翻现有组件化，而是在现有 `WidgetShell`、`ContentWidgetWindow`、`WidgetWindow`、`QuickCaptureWidgetWindow` 基础上，把“窗口公共能力”和“标题栏表现形式”进一步拆开。

## 背景结论

当前组件化方向是正确的，但 `WidgetShell` 现在把公共窗口能力和固定标题栏 UI 绑定得偏紧。

对文件格子、随记、待办来说，标题栏是高频操作入口：拖动、重命名、添加、更多菜单、删除、锁定位置等都依赖它。

对音乐、天气、系统监控这类偏展示型格子来说，固定 46px 标题栏会占用明显空间，且“更多/删除/标题”不是主要内容。它们更适合内容铺满，管理按钮以悬浮方式出现，甚至允许隐藏。

所以后续应引入“格子外壳模式”，而不是为音乐、天气、监控重新写专用窗口。

## 设计目标

- 所有格子继续共用透明度、毛玻璃、圆角、层级、动画、托盘/F7、默认宽高、主题、多语言、设置同步。
- 标题栏表现可以按格子类型和实例配置切换。
- 文件、随记、待办默认保持标准标题栏，但也支持用户改成悬浮或隐藏。
- 音乐、天气、系统监控默认使用更轻的标题样式，例如悬浮。
- 不把业务内容塞进 Shell，不把窗口管理逻辑塞进内容控件。
- 优先用 WinUI 原生控件和现有 `WidgetShell` 扩展，不引入第三方库。

## 术语建议

建议用 `WidgetChromeMode` 表示格子的窗口外壳模式。

可选值：

- `Standard`：标准标题栏
- `Compact`：紧凑标题栏
- `Overlay`：悬浮标题操作区
- `Hidden`：隐藏标题栏

这里使用 `Chrome` 是因为它描述的是窗口外壳，而不只是标题文字。这个命名比 `TitleBarMode` 更准确，因为它还影响按钮、拖动区域、分隔线、内容边距和右键入口。

## 四种模式定义

### Standard

标准模式，保持当前默认体验。

表现：

- 顶部固定标题栏。
- 行高继续由 `WidgetTitleBarMetrics` 计算，目前大约 46px。
- 显示标题图标、标题文字、右侧悬浮按钮。
- 显示标题栏底部分隔线。
- 内容从标题栏下方开始。

适合：

- 文件格子
- 随记
- 待办
- 需要经常管理内容的格子

风险低，当前就是这个方向。

### Compact

紧凑模式，减少标题栏高度，但保留标题栏结构。

表现：

- 顶部固定标题栏。
- 行高可降到 30-34px。
- 标题图标和标题文字缩小。
- 右侧按钮缩小。
- 分隔线可保留，也可变得更淡。
- 内容仍从标题栏下方开始。

适合：

- 待办
- 随记
- 文件格子的小尺寸场景
- 不想完全隐藏管理入口，但希望节省空间的用户

注意：

- 文件格子标题重命名、添加按钮、右键菜单仍需完整可用。
- 中文标题、长标题、英文长词都要测试是否挤压按钮。

### Overlay

悬浮模式，内容铺满，标题操作区覆盖在右上角。

表现：

- 不占用固定标题栏行高。
- 内容区域从窗口顶部开始。
- 右上角在鼠标移入时显示悬浮按钮组。
- 按钮组可包含：更多、关闭；文件格子可额外包含添加。
- 标题文字默认不显示，或者只在更多菜单/tooltip/辅助访问里保留。
- 不显示标题栏分隔线。

适合：

- 音乐
- 天气
- 系统监控
- 视觉卡片型格子
- 用户希望内容更沉浸的文件/随记/待办

关键点：

- 悬浮按钮不能遮挡核心内容。音乐可放右上角；天气和监控也类似。
- 对文件格子来说，悬浮按钮可能遮住第一行文件，需要内容顶部留安全内边距或让悬浮按钮有不透明底。
- 右键菜单要支持从内容区域打开，否则没有标题栏后用户很难找到设置。

### Hidden

隐藏模式，完全不显示标题栏和悬浮按钮。

表现：

- 内容完全铺满。
- 不显示标题、分隔线、右上角按钮。
- 管理入口必须通过替代方式提供。

适合：

- 高级用户
- 极简桌面展示
- 天气、监控这类只看内容的格子

风险最高：

- 用户可能不知道怎么移动、删除、打开设置。
- 文件格子隐藏后会影响添加、重命名、更多菜单入口。
- 必须保留至少一种管理入口，例如内容区右键菜单、托盘管理、设置页实例列表。

建议：

- 第一阶段实现 `Hidden` 时，不要真正移除全部管理能力。
- 内容区右键必须能打开通用菜单。
- 可考虑保留一个 1px 或透明热区，但这会让交互不直观，优先用右键菜单兜底。

## 设置模型建议

需要同时支持全局默认和单个格子覆盖。

### 全局设置

建议在 `AppSettings` 中新增：

```csharp
public string DisplayWidgetChromeMode { get; set; } = "Overlay";
public string InteractiveWidgetChromeMode { get; set; } = "Standard";
```

含义：

- `DisplayWidgetChromeMode`：展示型格子的默认标题样式，默认建议 `Overlay`。
- `InteractiveWidgetChromeMode`：交互型格子的默认标题样式，默认建议 `Standard`。

展示型格子建议包括：

- `Music`
- `Weather`
- `SystemMonitor`

交互型格子建议包括：

- `File`
- `QuickCapture`
- `Todo`
- `Tags`

说明：

- 用户提出“文件/随记/待办默认标准，但也要支持悬浮/隐藏”，所以不能只做“纯展示格子标题样式”一个全局项。
- 更稳妥的是提供两个全局默认：展示型默认、交互型默认。
- 设置文案可以简化，不必暴露“交互型/展示型”的工程词。

设置页文案建议：

- `展示型格子标题样式`：用于音乐、天气、系统监控等偏展示格子。
- `内容型格子标题样式`：用于文件、随记、待办等可编辑格子。

### 单个格子覆盖

建议使用 `WidgetConfig.Metadata` 存储实例覆盖：

```text
ChromeMode = Standard | Compact | Overlay | Hidden | System
```

含义：

- `System` 或空值：跟随全局默认。
- 其他值：该格子单独覆盖。

不建议第一阶段直接给 `WidgetConfig` 新增强类型字段，原因：

- 当前已有 `Metadata` 用于未来 widget-specific payload。
- 单实例标题样式属于 UI 配置，可以先用 metadata 降低迁移风险。
- 如果后面稳定，再迁移成强类型字段。

### Descriptor 默认值

建议在 `WidgetContentDescriptor` 或新建 `WidgetChromeDescriptor` 增加默认外壳建议。

示例：

```csharp
WidgetChromeCategory = Display | Interactive
DefaultChromeMode = Overlay | Standard
CanHideChrome = true
CanUseOverlayChrome = true
```

推荐默认：

| WidgetKind | 分类 | 默认模式 | 说明 |
| --- | --- | --- | --- |
| File | Interactive | Standard | 高频文件操作，标题栏仍重要 |
| QuickCapture | Interactive | Standard | 有 tabs、输入、设置入口 |
| Todo | Interactive | Standard | 有新增任务、筛选、设置入口 |
| Music | Display | Overlay | 内容视觉优先，按钮悬浮更合适 |
| Weather | Display | Overlay | 卡片展示优先 |
| SystemMonitor | Display | Overlay | 仪表盘展示优先 |
| Tags | Interactive | Standard | 需要筛选、右键、文件列表操作 |

## 解析优先级

建议统一实现一个解析服务或 helper：

```text
实例 Metadata.ChromeMode
-> descriptor 默认分类对应的全局设置
-> descriptor 默认模式
-> Standard
```

不要让每个窗口自己写一套判断。

建议命名：

- `WidgetChromeMode`
- `WidgetChromeModeResolver`
- `WidgetChromeSettings`

## 关联代码点

### AppSettings

文件：

- `src/DeskBox/Models/AppSettings.cs`
- `src/DeskBox/Services/SettingsService.cs`
- `src/DeskBox/ViewModels/SettingsViewModel.cs`
- `src/DeskBox/Views/SettingsWindow.xaml`
- `src/DeskBox/Views/SettingsWindow.xaml.cs`
- `src/DeskBox/Services/LocalizationService.cs`

需要处理：

- 新增全局标题样式设置。
- 默认值归一化。
- 恢复默认设置时重置。
- 设置变更后实时通知窗口刷新。
- 设置页 ComboBox 文案和索引绑定。

### WidgetConfig

文件：

- `src/DeskBox/Models/WidgetConfig.cs`

建议：

- 第一阶段使用 `Metadata["ChromeMode"]`。
- 不直接新增强类型字段。
- 后续稳定后可以迁移。

需要注意：

- 删除/重建格子时 metadata 是否保留。
- 默认标题多语言和自定义标题不应受 chrome mode 影响。

### WidgetContentDescriptor

文件：

- `src/DeskBox/Services/WidgetContentDescriptor.cs`
- `src/DeskBox/Services/WidgetContentFactory.cs`

需要补充：

- 格子是 `Display` 还是 `Interactive`。
- 默认 `ChromeMode`。
- 是否允许隐藏标题。
- 是否允许悬浮标题。

文档建议：

- Descriptor 只描述能力和默认策略。
- 具体解析仍放 resolver，不要在 descriptor 里读 settings。

### WidgetShell

文件：

- `src/DeskBox/Controls/WidgetShell.xaml`
- `src/DeskBox/Controls/WidgetShell.xaml.cs`

这是最关键的改造点。

当前状态：

- 固定两行：标题栏 46px + 内容。
- `TitleBarContent` 用于文件格子和随记等迁移场景。
- 右侧按钮随 `ShowHoverButtons` 做淡入淡出。

需要新增：

- `ChromeMode` 依赖属性。
- `TitleBarVisibility` 或内部根据 mode 切换。
- `DividerVisibility`。
- Overlay 操作按钮容器。
- Hidden 模式下关闭标题栏和按钮。
- 内容行布局根据 mode 调整。

可能的 XAML 结构：

```text
ShellRoot
├─ BackgroundPlate
├─ StandardTitleBarHost
├─ HeaderDivider
├─ ShellContentPresenter
└─ OverlayActionHost
```

Standard / Compact：

- 使用 `StandardTitleBarHost`。
- `ShellContentPresenter` 从标题栏下方开始。

Overlay / Hidden：

- `ShellContentPresenter` 占满窗口。
- `OverlayActionHost` 悬浮在右上角。

注意：

- 不建议通过给标题栏高度设为 0 勉强实现所有模式。
- Overlay 和 Hidden 的点击、右键、拖动语义不同，应显式处理。

### WidgetTitleBarMetrics

文件：

- `src/DeskBox/Services/WidgetTitleBarMetrics.cs`

当前用于统一标题栏高度、图标、按钮、padding。

需要扩展：

- 支持 `Standard` 和 `Compact` 两套 metrics。
- Overlay 按钮也可复用 action button size，但 padding/host 背景不同。
- Hidden 不需要标题 metrics。

建议：

```csharp
WidgetTitleBarMetricsCalculator.Create(width, mode, ...)
```

或者新增：

```csharp
WidgetChromeMetricsCalculator.Create(mode, width, ...)
```

### ContentWidgetWindow

文件：

- `src/DeskBox/Views/ContentWidgetWindow.xaml`
- `src/DeskBox/Views/ContentWidgetWindow.xaml.cs`

优先从这里落地。

原因：

- 音乐、天气、系统监控都会走这条路径。
- 当前音乐已经是 `ContentWidgetWindow`。
- 这里业务包袱比文件格子和随记少。

需要处理：

- 根据 resolver 设置 `ContentWidgetShell.ChromeMode`。
- `ApplyAppearancePreview()` 时重新应用。
- 设置变化后刷新 Shell mode。
- Overlay/Hidden 下窗口拖动区域不再只是标题栏。
- 右键菜单可以从内容区域打开。
- 关闭按钮逻辑保持通用：功能格子关闭就是关闭功能开关。

### WidgetWindow 文件格子

文件：

- `src/DeskBox/Views/WidgetWindow.xaml`
- `src/DeskBox/Views/WidgetWindow.xaml.cs`

风险较高。

当前特点：

- 已使用 `WidgetShell.TitleBarContent` 承载自定义标题栏。
- 标题栏里有添加、更多、删除。
- 标题双击重命名、中文 IME、Esc 取消等历史修复很多。
- 文件拖拽和框选也有复杂 pointer 逻辑。

要支持 Overlay/Hidden，需要特别处理：

- 添加按钮从固定标题栏迁到悬浮按钮。
- 更多/删除按钮迁到悬浮按钮。
- 标题右键菜单要迁移到内容右键或悬浮更多。
- 标题双击重命名在隐藏模式下入口在哪里。
- 文件格子的窗口拖动不能和内容框选、文件拖拽冲突。

建议：

- 第一阶段只让文件格子支持 `Standard` 和 `Compact`。
- 第二阶段再支持 `Overlay`。
- `Hidden` 对文件格子最后再开放，且需要设置页或右键菜单兜底。

### QuickCapture 随记

文件：

- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml`
- `src/DeskBox/Views/QuickCaptureWidgetWindow.xaml.cs`

风险中等。

当前特点：

- 使用 `WidgetShell`。
- 仍有专用窗口。
- 顶部有 tabs、搜索/编辑等内容逻辑。

要支持 Overlay/Hidden，需要特别处理：

- tabs 是否属于标题栏还是内容区。
- 如果隐藏标题栏，tabs 不能被误隐藏。
- 右上角更多/删除可以悬浮。
- 随记设置入口必须保留。

建议：

- 随记的 tabs 应视为内容的一部分，不应跟标题栏一起隐藏。
- 只隐藏窗口管理标题区，不隐藏随记内部 tabs。

### Todo

文件：

- `src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml`
- `src/DeskBox/Views/ContentWidgetWindow.xaml.cs`

Todo 走 `ContentWidgetWindow`，所以可跟随第一阶段一起支持。

注意：

- Add 输入框在内容内，不受标题栏隐藏影响。
- 设置入口可通过右键菜单或悬浮更多保留。
- 删除/关闭功能开关联动不应改变。

### Menus

关联：

- 标题栏右键菜单
- 悬浮更多菜单
- 内容区右键菜单
- 托盘菜单

需要统一：

- Overlay/Hidden 下，内容区右键应能打开通用菜单。
- 文件格子内容右键已有文件操作菜单，不能被通用菜单覆盖。
- 可考虑按点击区域区分：空白处右键打开通用菜单，文件项右键打开文件菜单。

风险：

- 文件格子右键菜单已经很复杂，不能简单全局拦截。
- 随记和 Todo 内容右键也有业务菜单。

### Drag / Move

隐藏标题栏后最大问题是：窗口怎么拖动？

候选方案：

1. Overlay 模式下，悬浮按钮区域旁保留拖动热区。
2. 内容空白区域可拖动。
3. 按住 Alt 拖动窗口。
4. 更多菜单中提供“进入移动模式”。

建议第一阶段：

- `ContentWidgetWindow` 的 Overlay/Hidden 支持内容空白区域拖动。
- 音乐、天气、监控内容交互少，空白拖动冲突低。
- 文件格子暂不启用内容拖动，避免和文件拖拽、框选冲突。

### Resize

窗口缩放边框当前在窗口层，和标题栏关系不大。

需要确认：

- Overlay/Hidden 不影响四边和四角 resize hit area。
- 悬浮按钮不要覆盖 resize corner。
- 小尺寸下按钮不要和右上角 resize 热区冲突。

### Accessibility / 可发现性

隐藏标题栏会降低可发现性。

必须保留：

- 右键菜单可打开更多操作。
- 设置页可修改回标准标题栏。
- 托盘或设置页能找到格子。
- 更多菜单里有“标题样式”入口或“打开设置”入口。

建议：

- 第一次切到 Hidden 时，给一次轻提示。
- 设置项描述里说明：隐藏后可通过右键菜单管理格子。

## 设置页设计建议

位置：

- 设置 → 外观

全局项：

- 展示型格子标题样式
- 内容型格子标题样式

可选项：

- 标准
- 紧凑
- 悬浮
- 隐藏

文案建议：

- 标准：显示完整标题栏和操作按钮。
- 紧凑：减少标题栏高度。
- 悬浮：内容铺满，操作按钮在鼠标移入时显示。
- 隐藏：隐藏标题栏和悬浮按钮，可通过右键菜单管理。

单个格子覆盖入口：

- 格子更多菜单 → 标题样式
- 可选：跟随全局、标准、紧凑、悬浮、隐藏

注意：

- 单格覆盖必须保存到 `WidgetConfig.Metadata`。
- 修改后立即刷新该窗口。
- 如果用户选隐藏，菜单本身关闭后仍要能通过内容右键找回来。

## 分阶段开发路线

### 阶段 1：模型和文档对齐

目标：

- 增加枚举、resolver、settings 字段。
- 不大改 UI。

内容：

- 新增 `WidgetChromeMode`。
- 新增 `WidgetChromeCategory`。
- 新增 `WidgetChromeModeResolver`。
- `WidgetContentDescriptor` 增加 chrome 默认信息。
- `AppSettings` 增加展示型和内容型默认标题样式。
- `SettingsService` 增加 normalize。
- 测试 resolver。

风险：

- 低。

### 阶段 2：ContentWidgetWindow 支持 Standard / Compact / Overlay / Hidden

目标：

- 让音乐、Todo 先跑通。
- 为天气、监控打基础。

内容：

- `WidgetShell` 增加 `ChromeMode`。
- `ContentWidgetWindow` 使用 resolver。
- Overlay 模式使用悬浮按钮。
- Hidden 模式内容区右键打开通用菜单。
- 设置变更实时刷新。

重点测试：

- Todo 标准/紧凑/悬浮/隐藏。
- 音乐标准/紧凑/悬浮/隐藏。
- 功能格子关闭开关联动。
- F7/托盘唤起动画。
- 右键菜单和设置入口。

风险：

- 中。

### 阶段 3：设置页全局配置

目标：

- 用户可以设置展示型和内容型默认标题样式。

内容：

- 外观页新增两个 ComboBox。
- 恢复默认设置同步。
- 多语言补齐。
- 设置变更后窗口实时应用。

风险：

- 低到中。

### 阶段 4：单个格子覆盖

目标：

- 每个格子可单独选择标题样式。

内容：

- 更多菜单增加“标题样式”子菜单。
- 写入 `WidgetConfig.Metadata["ChromeMode"]`。
- `WidgetManager` 保存配置。
- 变更后刷新当前窗口。

风险：

- 中。

### 阶段 5：文件格子和随记接入

目标：

- 文件、随记、待办默认标准，但支持悬浮/隐藏。

建议顺序：

1. 文件和随记先支持 `Compact`。
2. 再支持 `Overlay`。
3. 最后支持 `Hidden`。

原因：

- 文件格子有文件拖拽、框选、重命名、右键菜单。
- 随记有 tabs、编辑、最近记录、剪贴板逻辑。
- 这两个不要和 ContentWidgetWindow 同时大改。

风险：

- 高。

## 推荐优先级

最推荐先做：

1. `WidgetChromeMode` 模型。
2. `ContentWidgetWindow + WidgetShell` 支持 Overlay。
3. 音乐默认改为 Overlay。
4. 设置页加展示型格子标题样式。

暂缓：

- 文件格子 Hidden。
- 随记 Hidden。
- 内容区全局拖动。

原因：

- 音乐、天气、监控最需要这个能力，也最不容易和内容拖拽冲突。
- 文件/随记/待办虽然要支持，但需要更细的交互兜底。

## 测试清单

基础：

- 启动后窗口恢复正常。
- F7 显示/隐藏正常。
- 托盘唤起正常。
- 设置窗口不被格子盖住。
- 透明度、毛玻璃、圆角仍生效。
- 动画仍生效。

标题模式：

- Standard 显示完整标题栏。
- Compact 标题栏变矮且文字不挤。
- Overlay 内容铺满，鼠标移入显示按钮。
- Hidden 内容铺满，按钮不显示。
- 隐藏后可通过右键菜单恢复。

内容格子：

- 音乐播放控制可用。
- 音乐悬浮按钮不遮挡核心内容。
- Todo 新增、筛选、删除、设置入口可用。
- Todo 关闭按钮仍同步功能开关。

文件格子后续接入时：

- 外部拖入文件。
- 内部拖拽排序。
- `.lnk` 快捷方式拖动。
- 框选。
- 标题重命名中文输入法。
- Esc 取消重命名。
- 内容右键文件菜单不被通用菜单覆盖。

随记后续接入时：

- tabs 不被误隐藏。
- 最近内容可滚动。
- 双击编辑可用。
- 记录最近复制内容设置仍稳定。
- 顶部右键和内容右键语义清楚。

## 关键边界

不要做：

- 不要给音乐、天气、监控各写一个专用窗口。
- 不要让业务 Content 自己管理窗口层级、透明度、托盘动画。
- 不要把文件格子的复杂拖拽逻辑抽进通用 Shell。
- 不要第一阶段就强行让所有窗口都支持 Hidden。

应该做：

- 让 Shell 支持不同 chrome mode。
- 让 descriptor 声明默认策略。
- 让 resolver 统一决定最终模式。
- 让设置页负责全局默认。
- 让更多菜单负责单格覆盖。
- 先在 ContentWidgetWindow 验证，再迁移文件/随记。

## 最终判断

这次不是要否定前面的组件化，而是要让组件化更准确：

- `WidgetShell` 不应该等于“永远有 46px 标题栏”。
- `WidgetShell` 应该等于“统一窗口外壳能力”。
- 标准标题栏、紧凑标题栏、悬浮按钮、隐藏标题栏，都只是 Shell 的不同表现模式。

后续新增音乐、天气、系统监控时，应该优先复用这套外壳模式。这样既能保留 DeskBox 的统一行为，也能让不同类型格子获得适合自己的视觉密度。
