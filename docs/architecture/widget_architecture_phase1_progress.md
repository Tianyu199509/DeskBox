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

当前测试数：`106/106`。

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

### 暂不建议直接做

- 不建议直接迁移文件格子内容区为 `FileWidgetContent`。
- 不建议抽 `BaseWidgetWindow`。
- 不建议让 `WidgetSessionManager` 接管层级恢复。
- 不建议引入天气/Todo 等新功能入口。

## 16. 后续施工护栏

每次只动一个目标：

- 不改 `WidgetViewModel`。
- 不改 `FileService`。
- 不改 OLE 拖放兼容。
- 不改 IME / TextBox 焦点处理。
- 不改托盘动画算法。
- 不删除旧控件访问路径，先用 forwarding properties 过渡。

如果出现文件拖拽、中文重命名、F7 层级异常，优先 revert 当前阶段 commit，而不是继续在多个阶段上叠修。
