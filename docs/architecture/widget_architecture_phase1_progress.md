# DeskBox Widget Architecture Phase 1 Progress

更新日期：2026-06-29  
当前分支：`codex/widget-architecture-phase1`  
基线版本：`v1.1.10` / `581ebb3`

## 1. 当前结论

第一阶段架构铺底已经完成到 `WidgetShell` + `IWidgetContent` + `WidgetSessionManager` 的低风险基础线。

当前仍然保持多窗口架构：每个格子仍然是独立 WinUI Window，没有引入全屏 `DeskBoxLayerWindow`，也没有接管 Windows 桌面层。

目前所有 Session 相关改动只做记录和日志，不改变 F7、托盘、置顶、恢复桌面层、拖拽、IME、窗口动画等核心行为。

## 2. 已完成提交

| Commit | 内容 | 状态 |
| --- | --- | --- |
| `78caf55` | A-D 架构铺底：全局菜单样式、`WidgetKind` 扩展、`WidgetRegistry`、随记接入 `WidgetShell` | 已手测 |
| `d1ec532` | D.2：新增 `IWidgetContent` 契约和 `ExistingWidgetContent` 适配器 | 已测试 |
| `40161ee` | F.1：新增 `WidgetSessionManager`，记录会话状态 | 已测试 |
| `4b4985f` | 菜单左右边距微调 | 已手测 |
| `c39433a` | F.2：随记菜单弹窗接入 Session 记录 | 已手测 |
| `51ce88b` | F.3：文件格子部分交互接入 Session 记录 | 已手测 |
| `7260af0` | E.1：文件格子外壳接入 `WidgetShell`，内容区和业务逻辑保持不变 | 已手测 |
| `ac62a44` | E.2：补充 `WidgetShell` 过渡 API 说明并更新迁移进度 | 已测试 |
| `42c935c` | E.3：标出文件格子内容区未来迁移边界，不移动业务 UI | 已测试 |
| `0b8adde` | G.1：新增只读 `WidgetWindowDiagnostics`，统一窗口日志和动画边界计算 | 已手测 |
| `3ac4ff7` | G.2：新增只读 `WidgetWindowIdentity` 上下文 | 已手测 |
| `81b7ba7` | G.3：`IDesktopWidgetWindow` 暴露只读 `Identity` | 已手测 |
| `a7822b1` | G.4：`WidgetManager` 部分日志使用统一 Host 身份 | 已手测 |

## 3. 已完成范围

### 3.1 全局 UI 资源

- 菜单字体统一为 `Microsoft YaHei UI, Segoe UI Variable, Segoe UI`。
- 托盘菜单、格子标题菜单、内容区菜单、随记菜单都走统一 WinUI `MenuFlyout` 样式。
- 菜单外层 padding 和菜单项 padding 已拆分，避免右键菜单左右边距过宽。

### 3.2 WidgetKind / 配置层

- `WidgetKind` 已预留未来类型：`Weather`、`Todo`、`Tags`、`Music`、`SystemMonitor`。
- `WidgetConfig.Metadata` 已加入，用于未来格子的轻量配置扩展。
- 未知 `WidgetKind` 会安全降级到 `File`。
- 未来已知但未实现的类型会保留配置，但不会创建窗口。

### 3.3 WidgetRegistry

- 新增 `WidgetRegistry`，集中声明哪些格子类型已知、已实现、可创建窗口。
- 当前只有 `File` 和 `QuickCapture` 可创建窗口。
- 未来格子类型不会散落在多个 `if WidgetKind == ...` 判断里。

### 3.4 WidgetShell

- 新增 `WidgetShell`。
- 随记窗口已经接入 `WidgetShell` 作为外壳试点。
- 随记窗口仍保留原来的 WindowHandle、AppWindow、DWM、动画、层级逻辑。
- 文件格子已经接入 `WidgetShell` 外壳。
- 文件格子使用自定义标题栏插槽保留原有标题编辑、添加按钮、更多按钮、关闭按钮和按钮动画。
- 文件格子内容区、拖拽、重命名、选择框、resize、迁移遮罩仍保留原窗口逻辑。
- 文件格子内容区已标出未来 `FileWidgetContent` 边界，但尚未迁移。
- 总方案已修正：阶段 E 不要求立即移动内容区 XAML，后续必须把内容区作为整体评估迁移。

### 3.5 IWidgetContent

- 新增 `IWidgetContent`。
- 新增 `ExistingWidgetContent`，用于后续渐进迁移已有内容。
- 当前没有把随记或文件格子的业务 UI 强行迁入独立内容控件。

### 3.6 WidgetSessionManager

- 新增状态：
  - `DesktopResting`
  - `RaisedSession`
  - `InteractionActive`
  - `Hidden`
- `WidgetManager` 暴露当前 Session 状态和交互状态。
- 已记录：
  - 托盘/F7 raised 状态
  - 隐藏状态
  - 随记菜单弹窗交互
  - 文件格子标题重命名、文件重命名、右键菜单、删除确认、文件选择器、resize 等交互
- 尚未接管窗口层级、鼠标 hook、topmost 确认、显示隐藏决策。

## 4. 明确未做

- 未做全屏 `DeskBoxLayerWindow`。
- 未做格子合并 / Tab。
- 未新增天气、Todo、标签、音乐、监控正式入口。
- 未迁移文件格子内容区 XAML。
- 未重写文件拖拽。
- 未改中文重命名 IME 逻辑。
- 未改文件排序规则。
- 未改安装器。
- 未删除旧逻辑 fallback。

## 5. 手测记录

最近一次用户确认：2026-06-29

已确认正常：

- 随记外观、记录/固定/最近 tab。
- 随记右键菜单。
- 托盘/F7 唤起和隐藏。
- 文件格子右键菜单。
- 文件格子中文重命名和 ESC 取消。
- 文件格子 resize。
- 添加文件 / 添加文件夹。
- 菜单左右边距调整后视觉正常。

自动验证：

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数：`114/114`。

## 6. 本地备份

单步 patch 备份位置：

- `backups/architecture/phase1-before-content-contract-20260629-182503.patch`
- `backups/architecture/phase1-d2-content-contract-20260629-182920.patch`
- `backups/architecture/phase1-f1-session-manager-20260629-183322.patch`
- `backups/architecture/menu-padding-tune-20260629-184504.patch`
- `backups/architecture/phase1-f2-session-interaction-quickcapture-20260629-184808.patch`
- `backups/architecture/phase1-f3-session-interaction-file-widget-20260629-185706.patch`

整段历史备份：

- `backups/architecture/snapshots/codex-widget-architecture-phase1-20260629-190017.bundle`
- `backups/architecture/snapshots/format-patch-phase1-20260629-190017/`

## 7. 当前风险

### 高风险

- 文件格子内容区 XAML 迁移。
- 文件拖入、拖出、格子内拖动、`.lnk` 快捷方式拖动。
- 中文重命名 IME。
- F7 / 托盘 raised session 的层级恢复逻辑。

### 中风险

- 把文件格子内容区抽成 `FileWidgetContent`。
- 文件格子右键菜单与 Session 进一步联动。
- 标题栏拖动接入 Session 记录。

### 低风险

- 文档更新。
- Registry 扩展但不开放入口。
- Session 日志增强但不改变行为。
- 只读窗口诊断 helper。
- 新功能格子内容控件的空壳验证。

## 8. E.1 验收记录

文件格子 Shell 外壳试点已完成并手测通过。

已验收：

- 收纳格子拖入文件正常。
- 映射格子拖入文件正常。
- 格子内文件拖动正常。
- `.lnk` 快捷方式拖动正常。
- 中文重命名正常。
- ESC 取消重命名不保存。
- 内容区右键菜单正常。
- 标题栏右键菜单正常。
- F7 / 托盘行为正常。

## 9. E.3 前置整理记录

已完成低风险边界整理：

- `WidgetWindow.xaml` 中的文件内容区根节点命名为 `FileWidgetContentHost`。
- 用注释明确未来 `FileWidgetContent` 应整体迁移的范围。
- 未移动 XAML。
- 未改变拖拽、重命名、选择框、toast、GridView/ListView、resize、F7 或层级逻辑。

后续如果抽 `FileWidgetContent`，应把以下内容作为一个整体迁移，而不是拆散迁移：

- 加载状态。
- 空状态。
- 图标视图和列表视图。
- 选择框 overlay。
- 文件重命名编辑框。
- 状态 toast。

## 10. G.1 路线复核记录

已复核 `WidgetWindow` 和 `QuickCaptureWidgetWindow` 的生命周期代码。

结论：

- 两个窗口确实存在重复的窗口身份、日志、bounds、动画、层级、DWM 代码。
- 但动画和层级逻辑混有不同 guard，例如文件格子的重命名、删除弹窗、内联弹窗，随记的编辑、清空、tab 等状态。
- 当前不适合直接抽 `WidgetWindowLayerController` 或 `WidgetWindowAnimationController`。
- G.1 只做 `WidgetWindowDiagnostics`：统一短 ID、托盘窗口日志前缀、只读 `AnimationBounds` 计算。
- `WidgetWindowDiagnostics` 不调用 Win32，不保存设置，不改变 visible/topmost/raised 状态。

### G.1 验收记录

用户已在 Debug 版手测通过：

- 托盘/F7 行为正常。
- 随记显示正常。
- 文件格子显示正常。
- 格子拖拽未发现异常。

## 11. 下一步建议

### 推荐下一步：G.2 只读窗口身份上下文

当前仍不建议马上抽 `FileWidgetContent`。文件内容区仍包含拖拽、选择框、重命名、空状态、toast、GridView/ListView 等大量耦合逻辑，直接迁移风险较高。

G.2 继续抽只读/纯计算逻辑，建议先统一窗口身份上下文：`WidgetId`、`WidgetKind`、`Name`、`WindowHandle`、`AnimationBounds`、日志显示名。暂不接管 AppWindow、DWM、topmost 或动画执行。

## 12. G.2 施工记录

已完成只读窗口身份上下文：

- 新增 `WidgetWindowIdentity`。
- `WidgetWindowDiagnostics.Identity` 暴露 `WidgetId`、`WidgetKind`、`Name`、`LogKind`、`ShortWidgetId`、`WindowHandle`、`AnimationBounds`。
- 新增 `DisplayName` 和 `LogDisplayName`，为后续统一窗口日志、调试面板、诊断页面做准备。
- 未修改 `IDesktopWidgetWindow`。
- 未修改 `WidgetManager` 批量显隐流程。
- 未接管 AppWindow、DWM、topmost、F7、托盘动画或拖拽逻辑。

### G.2 验收记录

用户已在 Debug 版手测通过：

- 托盘/F7 行为正常。
- 随记显示正常。
- 文件格子显示正常。
- 格子拖拽未发现异常。

## 13. G.3 施工记录

已补齐 Host 侧只读身份接口：

- `IDesktopWidgetWindow` 新增只读 `Identity`。
- `WidgetWindow.Identity` 返回 `_diagnostics.Identity`。
- `QuickCaptureWidgetWindow.Identity` 返回 `_diagnostics.Identity`。
- 当前 `WidgetManager` 仍继续使用原有 `WindowHandle`、`Visible`、`AnimationBounds` 流程。
- 未改变批量显隐、topmost、F7、托盘动画、拖拽或 IME 行为。

### G.3 验收记录

用户已在 Debug 版手测通过：

- 托盘/F7 行为正常。
- 随记显示正常。
- 文件格子显示正常。
- 格子拖拽未发现异常。

## 14. G.4 施工记录

已让 `WidgetManager` 的部分诊断日志使用统一 Host 身份：

- 新增内部日志格式化方法 `FormatHostWindow(IDesktopWidgetWindow window)`。
- 异常日志和 `Prepare useLoaded` 日志显示 `LogDisplayName`、`WidgetKind` 和 hwnd。
- `WidgetManager` 的显隐、批处理、topmost 确认、动画调用仍使用原有控制流。
- 未改变任何 `WindowHandle` 判断、Win32 调用、F7、托盘动画、拖拽或 IME 行为。

### G.4 验收记录

用户已在 Debug 版手测通过：

- 托盘/F7 行为正常。
- 随记显示正常。
- 文件格子显示正常。
- 格子拖拽未发现异常。

## 15. 当前阶段停止建议

建议暂时停在 G.4：

- `WidgetShell` 已覆盖随记和文件格子的外壳。
- `IWidgetContent` 契约和 `ExistingWidgetContent` 已存在。
- `WidgetSessionManager` 已记录会话状态，但尚未接管层级。
- `IDesktopWidgetWindow` 已具备统一只读身份。
- `WidgetManager` 已开始在日志层使用统一 Host 身份。

当前不建议继续推进 `WidgetWindowLayerController`、`WidgetWindowAnimationController` 或 `FileWidgetContent` 内容迁移。下一轮如果继续重构，优先做纯参数/只读类整理，例如 appearance 参数计算；如果要进入内容迁移，需要单独列验证矩阵。

阶段性摘要：

- `docs/architecture/widget_architecture_phase1_g4_checkpoint_summary.md`

## 16. 空壳内容试点

已增加内容层空壳试点，用于验证 `WidgetKind + IWidgetContent` 链路：

- 新增 `PlaceholderWidgetContent`。
- 新增 `WidgetContentFactory`。
- 支持为 `Weather`、`Todo`、`Tags`、`Music`、`SystemMonitor` 创建占位内容。
- `WidgetRegistry` 仍然不允许这些未来类型创建窗口。
- 未开放任何用户入口。
- 未写入用户设置。
- 未改变 `WidgetManager` 的窗口创建流程。

这个试点只验证内容契约，不代表功能格子已经可用。

### 暂不建议直接做

- 不建议直接迁移文件格子内容区为 `FileWidgetContent`。
- 不建议抽 `BaseWidgetWindow`。
- 不建议让 `WidgetSessionManager` 接管层级恢复。
- 不建议引入天气/Todo 等新功能入口。

## 17. 后续施工护栏

每次只动一个目标：

- 不改 `WidgetViewModel`。
- 不改 `FileService`。
- 不改 OLE 拖放兼容。
- 不改 IME / TextBox 焦点处理。
- 不改托盘动画算法。
- 不删除旧控件访问路径，先用 forwarding properties 过渡。

如果出现文件拖拽、中文重命名、F7 层级异常，优先 revert 当前阶段 commit，而不是继续在多个阶段上叠修。

## 18. H.1 内容层元信息铺底

已补充内容层只读元信息，用于给后续天气、Todo、标签、音乐、监控等内容控件提供统一描述：

- 新增 `WidgetContentDescriptor`。
- `WidgetContentFactory` 统一维护内容层默认标题、默认图标和是否存在占位内容。
- `PlaceholderWidgetContent` 改为消费 descriptor，不再自己维护图标映射。
- `WidgetRegistry` 仍然负责“是否可创建窗口”，descriptor 不接管窗口创建权限。
- 未来类型仍然只有占位内容能力，没有用户入口，也不能创建窗口。
- 未改变 `WidgetManager` 创建流程。
- 未写入用户设置。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### H.1 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`122/122`。

### 下一步建议

继续保持低风险路线。下一步可以做 `H.2`：补一层只读的内容能力查询 API，例如“该类型是否已有真实内容控件、是否只有占位内容、是否允许显示在创建入口”。这一步仍然不开放入口，先把未来功能格子的判断集中起来，避免后面天气/Todo/标签各自散落判断。

## 19. H.2 内容能力查询收口

已在内容层补充只读能力查询，用于后续创建入口和功能格子渐进开放：

- 新增 `WidgetContentStage`，当前分为 `Implemented` 和 `Placeholder`。
- `WidgetContentDescriptor` 增加 `ContentStage` 与 `CanShowInCreateEntry`。
- `WidgetContentFactory` 增加：
  - `GetDescriptors()`
  - `GetCreateEntryDescriptors()`
  - `HasImplementedContent(WidgetKind)`
  - `IsPlaceholderOnly(WidgetKind)`
  - `CanShowInCreateEntry(WidgetKind)`
- 当前只有 `File` 内容允许显示在普通创建入口。
- `QuickCapture` 是已实现内容，但仍不显示在普通创建入口，继续走现有随记开关/窗口流程。
- `Weather`、`Todo`、`Tags`、`Music`、`SystemMonitor` 仍是 placeholder-only，不能显示在创建入口，也不能创建窗口。
- 继续由 `WidgetRegistry` 决定窗口是否可创建，内容 descriptor 不接管窗口授权。
- 未接入设置页、托盘菜单、右键菜单或新建格子流程。
- 未改变 `WidgetManager` 创建流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### H.2 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`132/132`。

### 下一步建议

下一步可以做 `H.3`：把未来内容类型的“开发状态/展示状态”文案也纳入只读 descriptor，例如 `PreviewLabel` 或 `StatusText`，方便以后设置页、调试页、创建入口共用同一套状态说明。仍不建议现在开放真实入口。

## 20. H.3 内容展示状态元信息

已补充内容层展示状态元信息，用于后续设置页、调试页和创建入口共用状态说明：

- 新增 `WidgetContentAvailability`，当前分为 `Available` 和 `Planned`。
- `WidgetContentDescriptor` 增加：
  - `Availability`
  - `StatusLabelKey`
  - `StatusDescriptionKey`
- `WidgetContentFactory` 增加：
  - `IsAvailable(WidgetKind)`
  - `IsPlanned(WidgetKind)`
- 当前 `File` 与 `QuickCapture` 标记为 `Available`。
- `Weather`、`Todo`、`Tags`、`Music`、`SystemMonitor` 标记为 `Planned`。
- 状态说明只保存本地化 key，不直接写死中英文文案。
- 未修改 `LocalizationService` 字典。
- 未接入设置页、创建入口、托盘菜单或右键菜单。
- 未改变 `WidgetManager` 创建流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### H.3 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`133/133`。

### 下一步建议

内容层只读元信息已经足够支撑未来功能格子的入口规划。建议下一步不要继续堆 descriptor 字段，可以转向 `H.4`：补一个仅供测试/调试使用的 `WidgetKind` 一致性测试，确保 `WidgetRegistry` 与 `WidgetContentFactory` 对已知类型的覆盖一致。仍不接 UI。

## 21. H.4 WidgetKind 覆盖一致性测试

已补充测试层护栏，确保后续新增格子类型时不会只改一边：

- 新增 `WidgetKindCoverageTests`。
- 校验 `WidgetRegistry` 与 `WidgetContentFactory` 覆盖同一组 active `WidgetKind`。
- 校验旧迁移值 `Productivity` 不进入 active registry/content descriptor。
- 校验可创建窗口的类型必须同时是 implemented + available 内容。
- 校验 planned 类型仍是 placeholder-only 且不可创建窗口。
- 未修改生产代码。
- 未接入设置页、创建入口、托盘菜单或右键菜单。
- 未改变 `WidgetManager` 创建流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### H.4 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`137/137`。

### 下一步建议

H 线的内容元信息铺底可以在 H.4 后暂时收口。下一步建议回到路线文档复核，决定是进入“第一个真实功能格子原型前置设计”，还是继续做只读调试/诊断页面。暂不建议立刻开放天气/Todo 创建入口。

## 22. H 线收口复核

H 线已经完成内容层低风险铺底，可以暂时收口：

- `WidgetContentFactory` 已具备内容 descriptor、能力查询、状态查询。
- `PlaceholderWidgetContent` 已验证未来类型可以走 `IWidgetContent` 链路。
- `WidgetKindCoverageTests` 已改为从 `Enum.GetValues<WidgetKind>()` 自动推导 active kinds，只排除旧迁移值 `Productivity`，避免未来新增枚举后漏同步 registry/content。
- `WidgetRegistry` 仍是窗口创建授权来源。
- `WidgetContentFactory` 只描述内容能力，不创建窗口、不写设置、不开放 UI。
- 未来类型仍不进入设置页、托盘菜单、右键菜单或普通创建入口。

### 复核结论

当前没有发现需要立刻修复的架构方向问题。H 线的几个类职责边界基本清楚：

- `WidgetRegistry`：回答“这个 kind 是否已知、是否已实现、是否允许创建窗口”。
- `WidgetContentFactory`：回答“这个 kind 的内容元信息是什么、是否有占位内容、是否适合显示在创建入口”。
- `WidgetSessionManager`：当前只记录会话，不接管层级。
- `WidgetShell`：当前只做外壳，不拥有业务内容。

### 仍需注意的遗漏

- 主进度文档前半部分的测试数量历史值保留了当时记录，例如 `114/114`、`122/122`，不是当前最新总数；以后发阶段总结时需要统一刷新。
- `docs/architecture/widget_architecture_phase1_g4_checkpoint_summary.md` 仍停在 G4，没有包含 H 线内容；如果要把当前分支交给别人继续，应补一份 H 线 checkpoint summary。
- `PlaceholderWidgetContent` 仍有英文占位描述 `Content placeholder`；因为没有用户入口，暂不影响产品，但如果未来用于调试页，需要接入本地化。
- `WidgetContentFactory` 暂未真正接入创建 UI；这是有意保留，不算遗漏。
- 文件格子内容区、层级控制、动画控制、拖拽和 IME 仍不建议继续在本阶段动。

### 下一阶段建议

H 线后建议先做“第一个真实功能格子前置设计”，不要直接上 UI。推荐顺序：

1. `Todo`：本地数据、无网络和定位权限，适合验证真实 `IWidgetContent`。
2. `Weather`：需要定位权限、城市搜索、天气 API 和缓存，作为第二个更合适。
3. `SystemMonitor`：需要性能计数器和刷新节流，适合作为第三个验证实时刷新。

开始真实功能前，应先写独立设计文档，明确：

- 数据存储位置。
- `WidgetConfig.Metadata` 使用哪些 key。
- 是否需要独立 store 文件。
- 是否允许普通创建入口显示。
- 首轮验证矩阵。

## 23. Todo 格子前置设计

已新增第一个真实功能格子的前置设计文档：

- `docs/architecture/todo_widget_design.md`

设计结论：

- Todo 作为第一个真实功能格子。
- Todo 是独立格子，不放进随记作为主入口。
- v1 只做本地任务列表、增删改、完成状态、基础过滤。
- v1 不做提醒、重复任务、优先级、子任务、标签、同步、系统通知。
- Todo 数据不进入 `settings.json`，使用每个 widget 独立的 `todo.json`。
- 在 Todo 内容、store、测试完成前，`WidgetRegistry` 和 `WidgetContentFactory` 仍保持 Todo 不可创建、不显示入口。
- 真正启用 Todo 创建入口必须单独提交，并同步更新 registry/content descriptor 测试。

当前仍未修改生产代码，也未开放 Todo 入口。

## 24. Todo Store 第一小步

已开始 Todo 真实功能格子的第一小步，只做数据与存储层：

- 新增 `TodoItem`。
- 新增 `TodoWidgetData`。
- 新增 `TodoWidgetStore`。
- 新增 `TodoWidgetStoreTests`。
- Todo 数据路径为 `%LocalAppData%/DeskBox/data/widgets/{widgetId}/todo.json`。
- Store 支持按 widget 隔离数据。
- Store 会规范化版本、空文本、重复 ID、排序和缺失时间。
- Store 使用 camelCase JSON。
- Store 保存时使用临时文件替换，降低写坏风险。
- 暂未新增 `TodoWidgetViewModel`。
- 暂未新增 `TodoWidgetContent`。
- 未修改 `WidgetRegistry`。
- 未修改 `WidgetContentFactory`。
- 未开放 Todo 创建入口。
- 未接入设置页、托盘菜单、右键菜单或新建格子流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### Todo Store 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`144/144`。

### 下一步建议

下一步可以做 `TodoWidgetViewModel` 与 ViewModel 单元测试，继续不接 UI、不开放入口。

## 25. Todo ViewModel 第一小步

已完成 Todo 行为层第一小步：

- 新增 `TodoFilter`。
- 新增 `TodoItemViewModel`。
- 新增 `TodoWidgetViewModel`。
- 新增 `TodoWidgetViewModelTests`。
- 支持初始化加载。
- 支持新增任务，新增任务默认在顶部。
- 支持输入框新增并成功后清空输入。
- 支持编辑任务文本，空文本编辑会被拒绝。
- 支持完成/取消完成。
- 支持删除单个任务。
- 支持清理已完成任务。
- 支持 `All` / `Active` / `Completed` 过滤。
- 支持计数：总数、未完成数、已完成数。
- 所有操作通过 `TodoWidgetStore` 持久化。
- 暂未新增 `TodoWidgetContent`。
- 未修改 `WidgetRegistry`。
- 未修改 `WidgetContentFactory`。
- 未开放 Todo 创建入口。
- 未接入设置页、托盘菜单、右键菜单或新建格子流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### Todo ViewModel 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`153/153`。

### 下一步建议

下一步可以做 `TodoWidgetContent` UI 空接入，但仍不要开放 Todo 创建入口。先让 UI 控件可以绑定 ViewModel，再决定是否需要通用内容 host。

## 26. Todo UI 空接入

已新增 Todo 内容控件，但仍未开放入口：

- 新增 `TodoWidgetContent.xaml`。
- 新增 `TodoWidgetContent.xaml.cs`。
- Todo UI 使用 WinUI 原生控件：
  - `TextBox`
  - `Button`
  - `CheckBox`
  - `ListView`
- Todo UI 可绑定 `TodoWidgetViewModel`。
- 支持添加任务、切换过滤、完成/取消完成、删除任务、清理已完成任务。
- `TodoWidgetViewModel` 补充 UI 绑定所需的空状态和列表可见性属性。
- 补充 ViewModel 测试覆盖空状态和列表可见性。
- 暂未新增 `TodoWidgetContent : IWidgetContent` 适配器。
- 暂未接入 `WidgetContentFactory`。
- 暂未修改 `WidgetRegistry`。
- 未开放 Todo 创建入口。
- 未接入设置页、托盘菜单、右键菜单或新建格子流程。
- 未改变托盘/F7/层级/拖拽/IME/排序/安装器逻辑。

### Todo UI 验证记录

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试数量：`154/154`。

### 下一步建议

下一步建议做一个不开放入口的 `TodoWidgetContentAdapter` 或等价内容封装，让 Todo 真正实现 `IWidgetContent`，但仍不让 `WidgetRegistry` 创建 Todo 窗口。

## 27. 2026-06-30 当前进度对齐

本节是对当前代码状态的重新对齐。前面 1-26 节保留历史施工记录，其中部分“未开放 Todo”描述已经是当时状态，不代表当前最新状态。

当前最新状态：

- `Todo` 已经不只是 UI 空接入，已经作为第一个真实内容型格子接入 `ContentWidgetWindow`。
- `WidgetRegistry` 当前允许 `File`、`QuickCapture`、`Todo` 创建窗口。
- `Weather`、`Tags`、`Music`、`SystemMonitor` 仍然只是已知/计划类型，不开放窗口创建。
- `WidgetContentFactory` 当前可以为 `Todo` 创建真实 content，为未来类型创建 placeholder content。
- `ContentWidgetWindow` 已经承担 Todo 的窗口宿主角色。
- `AppSettings.TodoEnabled` 当前作为 Todo 的 durable 功能开关。
- 删除桌面 Todo 或点击 Todo 内容窗口关闭按钮后，会同步关闭 Todo 功能开关，避免 F7/托盘“显示全部”再次把 Todo 拉起来。
- 设置页的“功能格子”已经有 `FeatureWidgetEntry` 数据结构雏形，但 XAML 里随记/Todo 行仍然是手写。
- 设置页外观页已经开始把全局显示项前移：默认宽度、默认高度、背景透明度、文字大小、显示悬浮按钮属于全局共用项。
- “文件格子显示设置”保留文件格子专用项：图标大小、间距、文件名宽度、扩展名等。

最近验证：

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- 构建通过：0 warning / 0 error。
- Todo 同步修复后曾完整跑过测试：`179/179` 通过。

## 28. 当前短期路线

短期内不继续新增天气、标签、音乐、监控等新格子。当前阶段先把基础能力打稳。

推荐下一步顺序：

1. 通用功能格子开关模型。
2. 功能格子设置列表动态化。
3. `ContentWidgetWindow` 关闭语义通用化。
4. 全局外观上下文继续收口。
5. `WidgetContentFactory` provider 化或等价注册表化。
6. Todo 作为样板格子补测试和文档。

这一步的目标是：未来新增一个格子时，只需要实现 Content / ViewModel / Store / 业务设置，而不是继续改多个散落的 switch 和设置字段。

## 29. 当前可复用能力清单

后续所有格子应优先复用：

- `WidgetKind`：类型标识和配置序列化。
- `WidgetRegistry`：是否已知、是否实现、是否允许创建窗口。
- `WidgetContentDescriptor`：默认标题、图标、展示状态、创建入口能力。
- `WidgetContentFactory`：内容控件创建入口。
- `IWidgetContent`：内容控件和窗口宿主之间的契约。
- `ContentWidgetWindow`：非文件类内容格子的窗口宿主。
- `WidgetShell`：标题栏、更多按钮、关闭按钮、内容插槽。
- `WidgetSessionManager`：当前用于记录会话状态，未来逐步承接更多层级/交互判断。
- `WidgetManager`：创建、恢复、显隐、F7/托盘协调。
- 全局菜单样式：托盘菜单、标题菜单、内容菜单应保持统一字体和间距。

这些能力不应在每个新格子里复制。

## 30. 当前仍未完全通用的部分

下面这些点是下一轮基础收口的重点。

### 30.1 功能开关仍是单独字段

当前还有：

- `QuickCaptureEnabled`
- `TodoEnabled`

不要继续添加：

- `WeatherEnabled`
- `TagsEnabled`
- `MusicEnabled`
- `SystemMonitorEnabled`

新增格子前应优先做通用 feature widget enabled 状态。

### 30.2 WidgetManager 仍有功能格子 switch

`SetFeatureWidgetEnabledAsync(...)`、`CreateOrShowFeatureWidgetAsync(...)`、`IsFeatureWidgetEnabled(...)` 已经有通用名字，但内部仍主要处理 `QuickCapture` 和 `Todo`。

新增格子前应改成 descriptor/registry/provider 驱动。

### 30.3 WidgetContentFactory 仍有 Todo 专门分支

当前 `CreateDetachedContent(...)` 对 Todo 是真实分支，对未来类型是 placeholder 分支。

后续如果新格子增加，应考虑 provider 注册表，避免 factory switch 越来越长。

### 30.4 设置页功能格子列表仍有手写 XAML

`FeatureWidgetEntries` 已经存在，但 `SettingsWindow.xaml` 里随记/Todo 的功能格子入口仍然手写。

新增第三个功能格子前，应先改成数据驱动列表。

### 30.5 ContentWidgetWindow 关闭按钮仍有 Todo 特判

当前关闭按钮会特判 Todo 并关闭 Todo 功能开关。

后续应改为通用规则：如果 kind 是单例功能格子，关闭按钮表示关闭该功能；普通多实例内容窗口再按产品定义处理。

### 30.6 全局外观应用还需补齐

全局外观项包括：

- 文字大小。
- 背景透明度。
- 默认宽度。
- 默认高度。
- 显示悬浮按钮。
- 动画效果和动画速度。

这些应逐步形成统一 appearance context。文件格子专用的图标大小、间距、文件名宽度不要混入全局设置。

## 31. 未来新增格子的执行模板

等基础收口后，新增功能格子按以下顺序：

1. 确认 `WidgetKind`。
2. 补 descriptor：标题、图标、状态、多语言 key、是否单例、是否有设置页。
3. 接入通用功能开关模型。
4. 实现 `XxxWidgetContent`。
5. 实现 `XxxWidgetViewModel`。
6. 如需持久化，实现 `XxxWidgetStore` 和数据模型。
7. 用 Adapter 实现 `IWidgetContent`。
8. 在 factory/provider 中注册创建逻辑。
9. 在 registry 中从 planned 改成 implemented/creatable。
10. 接入设置页动态列表。
11. 补多语言。
12. 补测试。
13. 手动验收 F7、托盘、关闭、删除、重启恢复、设置同步、主题、透明度、文字大小。

各格子独立开发内容：

- Weather：定位权限、手动城市、天气 API、缓存、网络错误。
- Tags：内部索引、文件标签关系、右键打标签；不写入文件本身。
- Music：Windows 系统媒体会话和播放控制。
- SystemMonitor：CPU、内存、网络采样和刷新节流。
- Todo：任务列表、完成状态、本地持久化和 Todo 专属交互。

共用内容不应重复开发：

- 窗口宿主。
- 标题栏。
- 菜单字体/间距。
- 基础更多菜单。
- F7/托盘显隐。
- 层级恢复。
- 全局外观。
- 设置页功能格子入口。

## 32. 文件格子和功能格子的设置边界

全局外观页应放：

- 默认宽度。
- 默认高度。
- 背景透明度。
- 文字大小。
- 显示悬浮按钮。
- 动画效果。
- 动画速度。
- 主题、圆角、毛玻璃等所有格子共享项。

`文件格子显示设置` 应放：

- 图标大小。
- 横向间距。
- 纵向间距。
- 文件名宽度。
- 扩展名显示。
- 快捷方式扩展名隐藏。
- 列表详细信息。
- 其他只影响收纳/映射文件格子的显示细节。

不要把 Todo、天气、标签、音乐、监控的业务设置放进 `文件格子显示设置`。

## 33. 2026-06-30 动画执行与 Shell 标题栏收口

本轮继续做基础能力收口，不新增业务格子。

### 本地备份

已在重构前保存本地 patch：

- `backups/architecture/before-animation-controller-shell-title-20260630-151856.patch`

### 动画执行公共化

新增 `WidgetTrayAnimationController`，把托盘/F7 唤起相关动画里的重复执行逻辑集中到一个 helper：

- 根据 `WidgetAnimationSettings` 生成动画 profile。
- 统一处理显示/隐藏时的窗口位移。
- 统一处理透明度、缩放、计时器、缓动和结束状态。
- 统一记录 animation generation，避免旧动画回调覆盖新状态。
- 统一恢复窗口位置和 visual 状态。
- 支持 offset override，用于从托盘方向进入/离开等特殊场景。

当前已接入：

- `WidgetWindow`：文件格子。
- `QuickCaptureWidgetWindow`：随记格子。
- `ContentWidgetWindow`：Todo 以及后续内容型功能格子。

各窗口仍保留自己的业务收尾逻辑，例如：

- 文件格子的 item transition 恢复。
- 文件格子和随记的 backdrop 临时抑制/恢复。
- 内容型格子的窗口生命周期和关闭语义。

这次没有把所有窗口动画入口完全合并成一个基类，因为文件格子、随记和内容型窗口仍有不同的历史行为。当前做法是先统一“动画执行器”，保留窗口自己的产品语义，风险更低。

### WidgetShell 标题栏扩展点

`WidgetShell` 新增标题栏扩展能力，为后续继续迁移文件格子标题栏做准备：

- `ShowAddButton`：控制是否显示添加按钮。
- `IsTitleEditable`：控制标题是否支持双击编辑入口。
- `TitleEditorContent`：允许宿主提供标题编辑控件。
- `AddRequested`：统一添加按钮事件。
- `TitleDoubleTapped`：统一标题双击事件。
- 暴露 `AddActionButton`、`AddActionIcon`、`TitleEditorPresenterElement` 等元素，方便迁移期窗口继续复用现有逻辑。

当前文件格子仍通过 `TitleBarContent` 使用自定义标题栏。原因是文件格子的标题栏涉及：

- 标题重命名和中文输入法兼容。
- Esc 取消、Enter 保存、失焦保存等编辑语义。
- 右键菜单。
- 添加按钮。
- 拖动窗口。
- 旧事件链和布局细节。

这些区域之前多次修过，属于高风险区。本轮只补 Shell 能力，不强行深迁移。后续可以按按钮、标题文本、标题编辑三个小步骤逐步迁。

### 当前边界

已经统一：

- 三类窗口共享托盘动画执行逻辑。
- 内容型格子继续走 `ContentWidgetWindow + WidgetShell`。
- Shell 具备可选添加按钮和标题编辑扩展能力。

暂未统一：

- 文件格子的完整标题栏交互。
- 随记窗口内部大量历史业务逻辑。
- `WidgetManager` 中的整体 F7/托盘调度策略。
- `WidgetContentFactory` provider 化。

后续建议：

1. 先观察这轮动画公共化在文件格子、随记、Todo 上是否稳定。
2. 再小步迁移文件格子标题栏，不要一次替换整条标题栏。
3. 新增天气、标签、音乐、监控前，优先继续收口 provider/descriptor/设置入口。

### 本轮验证

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试结果：`184/184` 通过。

## 35. 2026-06-30 Provider 与功能格子 Handler 收口

本轮继续做基础分发收口，不改变用户可见行为。

### WidgetContentFactory provider 化

新增轻量内容 provider：

- `IWidgetContentProvider`
- `WidgetContentProviderContext`
- `TodoWidgetContentProvider`
- `PlaceholderWidgetContentProvider`

`WidgetContentFactory.CreateDetachedContent(...)` 不再使用 `switch` 判断 Todo / Weather / Tags / Music / SystemMonitor，而是通过 provider 注册表分发。

当前 provider 状态：

- `TodoWidgetContentProvider` 创建真实 Todo content / store / view model adapter。
- `PlaceholderWidgetContentProvider` 继续为 Weather / Tags / Music / SystemMonitor 创建 placeholder content。
- 外部接口保持兼容：`CreateTodoContent(...)`、`CreateDetachedContent(...)`、`CanCreateDetachedContent(...)` 仍可用。

### ContentWidgetWindowFactory 去 Todo 分支

`ContentWidgetWindowFactory.CreateContentWindowPlan(...)` 不再单独判断 `WidgetKind.Todo`，统一调用 `WidgetContentFactory.CreateDetachedContent(...)`，并把 `SettingsService` 传入 provider context。

这让后续新增内容型格子时，不需要再在窗口工厂里增加新的 kind 分支。

### WidgetManager 功能格子 handler 化

新增内部 `FeatureWidgetHandler` 注册表，用于收口功能格子的创建和启停分发。

当前 handler：

- QuickCapture：仍走专用 `QuickCaptureWidgetWindow` 创建和隐藏逻辑。
- Todo：仍走 `ContentWidgetWindow` 创建和关闭逻辑。

`CreateOrShowFeatureWidgetAsync(...)` 和 `SetFeatureWidgetEnabledAsync(...)` 不再直接写 QuickCapture/Todo switch，而是通过 handler 注册表分发。

### 文档状态修正

之前章节中这些内容已过期：

- “设置页功能格子列表仍有手写 XAML”：当前已经由 `FeatureWidgetEntries` 动态生成。
- “ContentWidgetWindow 关闭按钮仍有 Todo 特判”：当前已经改为 `FeatureWidgetSettings.IsFeatureWidget(...)` 通用规则。
- “WidgetContentFactory 仍有 Todo 专门分支”：当前已改为 provider 注册表。
- “WidgetManager 功能格子 API 仍有 switch”：当前核心创建/启停 API 已改为 handler 注册表。

仍需继续收口：

- `CreateWidgetOfKindAsync(...)` 和 `CreateRegisteredWidgetFromConfigAsync(...)` 仍有窗口类型分发逻辑，这是窗口宿主差异导致，暂时保留。
- QuickCapture 仍是专用窗口，Todo 是内容型窗口，二者不能强行合并。
- `FeatureWidgetSettings` 仍保留 legacy `QuickCaptureEnabled` / `TodoEnabled` 镜像字段，用于兼容旧设置。

验证：

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试结果：`185/185` 通过。

## 34. 2026-06-30 标题栏视觉规格统一

本轮继续收口标题栏视觉细节，不改变标题栏业务交互。

新增 `WidgetTitleBarMetrics` / `WidgetTitleBarMetricsCalculator`，统一计算：

- 标题图标大小。
- 标题文字大小。
- 右上角操作按钮宽高。
- 右上角操作按钮图标大小。
- 标题栏行高。
- 迁移期自定义标题栏内层 padding。

当前已接入：

- `WidgetWindow`：文件格子。
- `QuickCaptureWidgetWindow`：随记格子。
- `ContentWidgetWindow`：Todo 和后续内容型格子。

边界说明：

- 文件格子仍保留自定义标题栏和原有重命名、拖动、右键菜单逻辑。
- 文件格子自定义标题栏不再叠加内层 padding，避免与 `WidgetShell` 外层 padding 叠加造成左右间距过宽或按钮被压扁。
- 随记格子保留现有内层 padding，因为当前视觉是正常参照。
- Todo/内容型格子继续走 `WidgetShell` 默认标题栏。

验证：

- `dotnet build .\DeskBox.sln -c Debug -p:Platform=x64 --no-restore`
- `dotnet test .\DeskBox.sln -c Debug -p:Platform=x64 --no-build`

当前测试结果：`184/184` 通过。
