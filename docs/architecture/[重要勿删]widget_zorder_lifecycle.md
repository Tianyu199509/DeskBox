# DeskBox 格子层级（Z-Order）生命周期与排查手册

> 文档性质：技术实现手册 + 故障复盘指南。
> 适用场景：F7 / 托盘唤起格子后出现的层级类问题（压屏、不回落、闪烁、不收起等）。
> 关联文档：`docs/architecture/widget_layer_workspace_plan.md`（产品规则口径）、`docs/architecture/current_architecture.md`（整体架构）。
> 最后更新：2026-08-12（按现行代码校正回落策略、组恢复与空闲排序规则）。

---

## 1. 系统目标与两种层级模式

格子（widget）本质上是一组**无边框 Win32 窗口**，需要在两个状态之间切换：

| 状态 | 含义 | Z-order 位置 |
|---|---|---|
| 桌面静置（DesktopResting） | 常态逻辑状态；物理位置由本次回落目标决定 | 外部前台窗口之后，或桌面图标附近 / 普通层级带底部 |
| 唤起（RaisedSession） | F7/托盘唤起，临时浮到最前 | **普通层级带顶部（非持久 TopMost）** |
| 交互中（InteractionActive） | 用户正在拖拽/重命名/开菜单 | 同唤起，且阻止自动回落 |
| 隐藏（Hidden） | 不可见 | — |

层级模式（`Settings.WidgetLayerMode`）：

1. **动态层级（默认）**：本文档主要描述的模式。F7 唤起时浮起，交互结束/点击外部后回落。
2. **桌面固定层（DesktopPinned，实验）**：格子 attach 到 WorkerW 桌面容器，所有"置顶/回落"操作都改为桌面图标层内的兄弟排序。**注意：几乎所有 Z-order 入口函数都有 `UsesDesktopPinnedMode()` 分支，修改任何一条路径时必须两种模式都过一遍。**

现行代码还把“逻辑状态”和“物理落点”分开处理。`DesktopResting` 只表示格子已经退出临时唤起/交互状态，不再等同于“绝对底层”。动态模式回落时由 `RelativeLayerRestorePolicy` 选择物理落点：

| 回落判定 | 物理落点 |
|---|---|
| 外部应用获得前台 | 整组格子紧随该前台窗口之后，保留格子内部顺序 |
| 前台是 DeskBox 自身 | 保持当前全局层级，只整理格子内部顺序 |
| 前台缺失或为桌面壳 | 回到桌面层 / 普通层级带底部 |
| DesktopPinned 模式 | 回到 Explorer 桌面 Owner 内，仅做桌面兄弟排序 |

格子窗口自 2026-10 起只有一种宿主类型（随记 `QuickCaptureWidgetWindow` 已并入 `ContentWidgetWindow` 分部，见 `ContentWidgetWindow.QuickCapture.cs`），唤起/回落路径不再有平行宿主实现；漂移面收窄为 `WidgetWindowBase` ↔ `ContentWidgetWindow` 派生层：

| 宿主类 | 文件 | 用于 |
|---|---|---|
| `ContentWidgetWindow`（继承 `WidgetWindowBase`） | `src/DeskBox/Views/ContentWidgetWindow.*.cs` | 文件、Todo、音乐、天气、搜索、随记、监控等全部格子 |

---

## 2. 核心机制：唤起不靠"持久置顶"

**这是理解整个系统的钥匙。** 唤起时格子**不是**持久 TopMost 窗口，而是通过一个 Win32 技巧浮到普通层级带顶部：

```
SetWindowPos(hwnd, HWND_TOPMOST,   ..., SWP_NOACTIVATE | SWP_SHOWWINDOW);
SetWindowPos(hwnd, HWND_NOTOPMOST, ..., SWP_NOACTIVATE | SWP_SHOWWINDOW);
```

先设为 TOPMOST 再立刻取消，窗口会停留在**普通（非 TopMost）层级带的最顶部**。效果上"浮在所有普通窗口之上"，但不占用 TopMost 属性——这样其他窗口被激活时可以正常盖过它。

实现位置：`src/DeskBox/Helpers/Win32Helper.cs` 的 `BringWindowTemporarilyToFront()`（约 556 行）。

> **推论**：格子压屏问题几乎都不是"置顶没清除"，而是"**回落（restore）没有被触发**"或"**回落了但没有视觉效果**"。排查时不要先找谁设了 TopMost，先找回落信号为什么没响。

### 持久置顶只作为瞬态存在（单窗口原语）

单窗口瞬态浮起（`BringWindowTemporarilyToFront`，先 TOPMOST 再 NOTOPMOST）仍是合法原语，效果上"浮在普通窗口之上"但不占用 TopMost 属性。**组抬升已不再访问 topmost 带**：`BringGroupTemporarilyToFront` 用单次相对排序把整组插到普通带顶（`WidgetRaiseBandContractTests.GroupRaise_NeverVisitsTheTopmostBand` 钉死），早先文档描述的"批量唤起短暂全员持久 TopMost 再逐个清除"序列已不存在。快速预览模式是唯一驻留 topmost 带的会话（`HoldGroupTopMostWithoutActivation`，释放只走每窗隐藏动画完成路径）。

---

## 3. F7 唤起全流程

### 3.1 热键入口

`src/DeskBox/Services/GlobalHotkeyService.cs`

- 主路径：`RegisterHotKey` + 窗口子类化（`SetWindowSubclass`）接 `WM_HOTKEY`。
- 兜底路径：`WH_KEYBOARD_LL` 低级键盘钩子（`KeyboardHookProc`）。**当前台窗口是提权进程时，UIPI 会拦截 WM_HOTKEY 投递，只有钩子路径能收到 F7**——这就是"热键时而灵时而不灵"的来源，不是注册失败。
- 两条路径都去重（`_hookGestureIsDown` / `_isInvoking`），最终调 `App.Tray.cs` 的 `ToggleTrayWidgetsAsync()`。

### 3.2 Toggle 决策（三态）

`src/DeskBox/Services/WidgetManager.cs` 的 `ShouldHideWidgetsForTrayToggle()`（231 行）：

| 条件 | 决策 | 日志标记 |
|---|---|---|
| `_widgetsRaisedFromTray == true`（唤起态中） | **hide** | `reason=raised-session` |
| 无可见格子 | **raise** | `reason=no-visible-windows` |
| 前台是 DeskBox / 桌面壳（Progman/WorkerW）/ 任务栏 | **hide** | `reason=foreground-local` |
| 格子可见但被埋 + 前台是外部窗口 | **raise** | `reason=visible-widgets-behind` |

> 注意最后一行是**有意设计**（2026-07-24 用户确认保留）：F7 可以把被其他窗口埋住的格子重新捞上来。所以"唤起 → 点外部回落 → 再按 F7"是重新浮起而不是隐藏，属预期行为。

### 3.3 唤起执行序列

`src/DeskBox/Services/WidgetManager.TrayAnimation.cs` 的 `RaiseWidgetsFromTrayAsync`（约 40-130 行），顺序固定、相互依赖，**调整顺序前务必读完整个函数**：

1. `_isTogglingWidgetsDesktopLayer = true`（finally 复位，防重入）。
2. 逐格子 `PrepareWidgetForBatchShowAsync`（异步，可能首次创建窗口）。
3. 隐藏中的格子 `ShowPreparedRaisedFromTray()`；已可见的格子 `EnsureRaisedFromTrayTopMost()`（现实现无 `_isAtDesktopLayer` 短路，每窗 `BringToFront`+`HoldTemporaryTopMost` 后由第 6 步的组操作统一终裁）。
4. 记录 `_foregroundAtRaiseTime = GetForegroundWindow()`；设置 `_suppressTrayLayerRestoreUntilUtc = now + 160ms`（防止唤起动画期间的瞬时事件误触发回落）。
5. `SetWidgetsRaisedFromTray(true)` 进入唤起态。
6. `QueueTrayRaiseTopMostConfirmation` → `BringGroupTemporarilyToFront`（瞬态置顶再清除，见 §2）。
7. **`StartTrayLayerRestoreMonitor`：启动 200ms 恢复监视器 + 50ms 鼠标边沿采样器（见 §4）。**
8. `ActivateLastRaisedWindow` → 两个宿主类的 `ActivateRaisedFromTrayBatch()`：`base.Activate()` + `SetForegroundWindow(hwnd)`。
   - **`SetForegroundWindow` 经常失败**（Windows 前台锁：只有"收到最后一次输入事件"的进程才能抢前台；热键经异步队列 + 窗口准备耗时后，输入事件归属可能已不是 DeskBox；前台是提权进程时 UIPI 也会拒绝）。**返回值必须检查并记日志**（2026-07-24 起已加，日志前缀 `[ZOrder] ... SetForegroundWindow FAILED`）。失败是合法的，系统必须能在"DeskBox 从未获得前台"的情况下正确回落。

---

## 4. 回落（Restore）信号体系

**唤起态期间，单窗口的所有自救路径都被显式禁用**（`WidgetWindowBase.Interaction.cs` 与 `ContentWidgetWindow.WindowInteraction.cs` 的 Deactivated/安全定时器路径都检查 `WidgetsRaisedFromTray: true` 后跳过）。唯一生效的回落路径是管理器侧的 **200ms 恢复监视器**：

`src/DeskBox/Services/WidgetManager.ZOrder.cs` 的 `TrayLayerRestoreTimer_Tick` → `TryRestoreRaisedWidgetsAfterInteraction`（约 93-152 行）。

### 4.1 监视器的四道闸（任一不满足则跳过本 tick）

1. `_isTogglingWidgetsDesktopLayer`：toggle 进行中。
2. `IsWidgetInteractionActive`：交互深度 > 0（拖拽/重命名/菜单/对话框）。**泄漏会永久堵死回落，见 §6 坑 #4。**
3. `_suppressTrayLayerRestoreUntilUtc`：唤起后 160ms 抑制窗。
4. 前台判断：前台是 DeskBox → 保持唤起并标记 `_hasDeskBoxForegroundSinceRaise`；前台是任务栏 → 保持唤起。

### 4.2 触发回落的三条信号

| 信号 | 可靠性 | 说明 |
|---|---|---|
| **DeskBox 曾拿前台，后离开** | 高 | 激活成功时的主路径。点任何外部窗口即触发。 |
| **前台窗口发生变化**（≠ `_foregroundAtRaiseTime`） | 高 | 激活失败时的主路径。要求用户点了**不同的**窗口。 |
| **鼠标按下边沿**（50ms 采样器） | 高（2026-07-24 修复后） | 激活失败 + 用户点回**同一个**已激活窗口时的兜底。 |

### 4.3 鼠标边沿采样器（方案 B，2026-07-24 引入）

`WidgetManager.ZOrder.cs` 的 `TrayMouseSamplerTimer_Tick`：

- 50ms 轮询 `Win32Helper.IsAnyMouseButtonDown()`（`GetAsyncKeyState` **高位**，全局物理状态，与目标进程是否提权无关）。
- 检测 up→down 跳变，**在按下瞬间**判断光标不在 DeskBox/任务栏上 → 置 `_outsideMousePressObserved = true`。
- 200ms 监视器消费该标志触发回落。
- 启动时预充当前按键状态（`_lastMouseButtonsDown = IsAnyMouseButtonDown()`），防止用户按住触发热键的那次点击被误判为新按下。

> **历史教训（坑 #1）**：旧实现用 `GetAsyncKeyState & 0x0001` 低位（"自上次查询以来是否按下"）。低位只对**派发到本线程消息队列**的输入可靠，点击其他进程窗口时经常不置位——这就是"点同一窗口 1 次不回落"的根因。代码注释里早就写了 *"GetAsyncKeyState (which only sees presses posted to our own thread)"*，但仍被用作唯一兜底信号。**检测跨进程点击，只能用高位轮询 + 自己记边沿，或 WH_MOUSE_LL 钩子。**

### 4.4 回落执行（现行代码）

`WidgetManager.RestoreRaisedWidgetsToDesktopLayer` 先让各宿主执行 `ForceRestoreDesktopLayerFromManager()`，清理临时置顶、交互计时器及逻辑状态；随后调用 `WidgetLayerService.RestoreGroupPreservingForeground()`，把所有可见格子作为一个连续 Z-order 组恢复。

组恢复只读取一次当前前台根窗口，并按 `RelativeLayerRestorePolicy` 选择落点：外部页面对应 `BehindForeground`，DeskBox 前台对应 `PreservePeerOrder`，桌面壳或无有效前台对应 `DesktopBottom`。外部页面分支以该页面为固定边界，使用 `BeginDeferWindowPos` / `DeferWindowPos` 一次排列整个格子组；失败时再以相同边界逐格子 `SetWindowPos` 兜底。

`NormalizeIdleWidgetZOrder()` 只能整理格子之间的顺序。它以当前最高格子的上一窗口作为全局边界，不得调用 `MoveToDesktopBottom()`、`SetWindowToBottom()` 或重新绑定 Owner。否则前面刚建立的“前台页面 > 格子组 > 更早页面”会在恢复末尾或 120ms 延迟回调中被覆盖成“所有页面 > 格子组”。

> **关键约束**：回落必须同时完成“退出临时唤起状态”和“建立新的全局 Z-order 边界”。单窗口可以负责清理自身状态；多个格子的全局位置只能由管理器按组确定。

---

## 5. 涉及文件速查表

| 文件 | 职责 |
|---|---|
| `src/DeskBox/Services/GlobalHotkeyService.cs` | F7 注册（RegisterHotKey + WH_KEYBOARD_LL 双路径）、去重、触发 |
| `src/DeskBox/App.Tray.cs` | `ToggleTrayWidgetsAsync`（551 行）：toggle 总入口 |
| `src/DeskBox/Services/WidgetManager.cs` | `ShouldHideWidgetsForTrayToggle`（231）、`RestoreRaisedWidgetsToDesktopLayer`（1141）、`IsWidgetInteractionActive`（112） |
| `src/DeskBox/Services/WidgetManager.TrayAnimation.cs` | `RaiseWidgetsFromTrayAsync` 唤起序列、`_foregroundAtRaiseTime`、抑制窗、`ActivateLastRaisedWindow` |
| `src/DeskBox/Services/WidgetManager.ZOrder.cs` | **恢复监视器（200ms）+ 鼠标采样器（50ms）+ 交互泄漏看门狗**，前台/任务栏/桌面壳判定 |
| `src/DeskBox/Services/WidgetLayerService.cs` | Z-order 原语：`BringWindowTemporarilyToFront`、`BringGroupTemporarilyToFront`、`RestoreGroupPreservingForeground`、相对前台组排序、DesktopPinned attach/detach |
| `src/DeskBox/Services/WidgetSessionManager.cs` | 会话状态机 + 交互深度计数（`BeginInteraction`/`EndInteraction`/`ForceResetInteractions`） |
| `src/DeskBox/Helpers/Win32Helper.cs` | `BringWindowTemporarilyToFront`（556）、`SetWindowTopMost`（575）、`ClearWindowTopMost`（590）、`IsAnyMouseButtonDown`（约 344）、`GetAsyncKeyState` 封装 |
| `src/DeskBox/Views/WidgetWindowBase.Interaction.cs` | 基类版本同上 + `ShouldDeferDesktopLayerRestore`（94） |
| `src/DeskBox/Views/WidgetWindowBase.Collapse.cs` | `RaiseForExpandedState`（1811）：胶囊展开时的层级处理（含"物理浮起但状态已回落"的兼容分支） |
| `src/DeskBox/Views/ContentWidgetWindow.xaml.cs` | 全部格子（含随记、监控）的统一宿主；实现唤起/回落/激活和内容切换 |
| `src/DeskBox/App.xaml.cs` | `IsDeskBoxWindow`（562）：按 PID + 已知窗口根判定，**范围宽（本进程所有窗口）** |

---

## 6. 踩坑清单（按危害排序）

### 坑 #1：用 `GetAsyncKeyState` 低位检测跨进程点击 —— 不可靠
- 低位（`& 0x0001`）语义是"自**本线程**上次查询以来是否按下"，对其他进程窗口收到的点击经常不置位。
- **正确做法**：高位（`& 0x8000`）全局物理状态 + 50ms 轮询 + 自记 up→down 边沿（已实现，见 §4.3）；或 `WH_MOUSE_LL` 全局钩子（兜底升级路径，暂未启用）。
- 采样间隔必须小于典型点击按下时长（50-150ms），200ms 轮询会漏快速点击。

### 坑 #2：把状态回落等同于 `HWND_BOTTOM` —— 会破坏页面相对层级
- 点击外部页面后，Windows 会把该页面提升到普通层级带顶部；格子此时自然应成为紧随其后的连续组。
- 如果恢复末尾或延迟空闲排序再调用 `MoveToDesktopBottom()`，格子会从“新前台页面之后”直接掉到所有普通页面之后。
- `HWND_BOTTOM` 只允许用于桌面壳/无有效前台等明确的桌面回落场景。外部页面场景必须使用该前台根窗口作为 `hWndInsertAfter` 边界。

### 坑 #3：`SetForegroundWindow` 静默失败 —— 必须检查返回值
- Windows 前台锁（foreground lock）规则：只有"收到最后一次输入事件"的进程等少数情况能抢前台。热键 → 异步队列 → 窗口准备/动画耗时后，输入归属可能已丢失；前台是提权进程时 UIPI 直接拒绝。
- 失败时格子仍浮起但永远拿不到前台，`_hasDeskBoxForegroundSinceRaise` 永不成立，回落完全依赖"前台变化"或"鼠标边沿"两条信号。
- 三个 `ActivateRaisedFromTrayBatch` 已实现返回值日志（`[ZOrder] ... SetForegroundWindow FAILED`），复盘先看这条。

### 坑 #4：`BeginInteractionLayer`/`ReleaseInteractionLayer` 配对泄漏 —— 会永久堵死回落
- 交互深度计数在 `WidgetSessionManager._interactionDepth`，> 0 时监视器每 tick 跳过。
- 清零只发生在 `MarkDesktopResting`/`MarkHidden`——而这俩又依赖回落先发生，**泄漏即死锁**。
- 看门狗 **2026-10-17 起真实存在**（`WidgetManager.ZOrder.cs` 的 `InteractionLeakWatchdog_Tick`，此前文档声称的 v1.3.3 实现从未落地）：交互深度 > 0 持续 10s（固定层模式 20s）且前台既非 DeskBox、非任务栏、也无代理原生菜单回合 → `ForceResetInteractions` 强制清零 + 排一次普通整理。真实交互若前台在 DeskBox/任务栏/原生菜单上则计时器不断重置，不误伤。
- **新增任何 Begin 调用点时**：确认所有退出路径（异常、取消、窗口中途隐藏、flyout 轻 dismiss）都有配对 End。

### 坑 #5：修改一条路径，忘了另一类宿主 / 另一种层级模式
- 两种宿主（QuickCaptureWidgetWindow / ContentWidgetWindow）有平行实现；`UsesDesktopPinnedMode()` 分支遍布所有 Z-order 原语。改动后四象限（2 宿主 × 2 模式）都要过。

### 坑 #6：瞬态置顶函数被误当持久置顶用
- `BringWindowTemporarilyToFront`（先 TOPMOST 再 NOTOPMOST）只保证"浮到普通带顶部"，调用返回后窗口**不是** TopMost。`StartTopMostSafetyTimer` 一进来就查 `IsWindowTopMost`，不是持久置顶会直接退出——安全网对瞬态浮起无效，别指望它兜底。
- `BringGroupTemporarilyToFront` 内部的"全员 TopMost → 逐个清除"序列必须保持原子（同步执行），往中间插入 await 会留下持久置顶残留。2026-10-14 回调隔离批起，推迟只发生在**调用点**（标题栏按下场景把整个 `ActivateAllVisibleWidgetsFromTitle` 调用推迟一帧）；函数内部的三连（排序 + `BringWindowToFront` + `SetForegroundWindow`）永远同帧，reassert 路径（`ApplyRaisedGroupReassertOrder`）因此不分帧——在函数体内加任何推迟都是回归。

### 坑 #10（新）：回调帧内的窗口手术与"迟到激活抢前台"
2026-10-14 回调隔离批（拖拽卡死族）之后的新边界，总则在 file_drag 契约 §9.23：
- DesktopPinned 家族的窗口手术（激活、落床、rebed）一律经 `DeferDesktopPinnedActivation`/`DeferDesktopPinnedRestore` 推迟出回调帧；`WM_MOUSEACTIVATE` 的**返回值判定与 `_desktopPinnedNativeSuppressionTimestamp` 盖章保持同步**（routed 事件靠它识别同一次点击），只有手术出帧。
- 落床 restore 与 rebed 必须**同一落地单元**（#574 同周期不变式；`WidgetZOrderRestoreContractTests.DesktopPinnedActivation_RebedsSiblingsInTheSameCycle` 钉着）。
- 迟到激活守卫：入队时快照 `GetForegroundWindow`，落地时前台"既非本 HWND 亦非快照值"即放弃——方法体内的 `foreground != HWnd` 复查挡不住用户在推迟窗口内把前台给了别人。
- 托盘左键 toggle 入口的 `Task.Yield` 使抬升决策输入（`_foregroundAtRaiseTime`、§3.3 的 +160ms 抑制窗与回落锚点）在 yield 后**重采样**；采样语义="抬升决策时刻"而非"点击时刻"。

### 坑 #7：监视器的抑制窗/代际（generation）被意外延长或失效
- `_suppressTrayLayerRestoreUntilUtc` 目前只有唤起时 +160ms 一处赋值。新增赋值点要克制——它直接推迟回落。
- `_trayRaiseBatchGeneration` 用于让过期异步回调失效（每次唤起/确认/回落都自增）。写新的延迟回调时记得捕获当前代际并在回调里比对，现行参考实现=`QueueTrayRaiseBandRecheck`（2026-10-17 起，带前台/拖拽/会话守卫的一次性带完整性复检；v1.2.9 已删除的三参延迟确认重载不再是参考）。

### 坑 #8：`IsDeskBoxWindow` 按进程判定，范围很宽
- 本进程**所有**窗口（搜索弹窗、设置、托盘隐藏窗口）都算 DeskBox 窗口。前台判断时，DeskBox 自家任何窗口拿到前台都会被视为"用户还在用格子"而保持唤起。新增顶级窗口类型时意识到这一点。

### 坑 #9：raised 会话期间的窗口侧回落请求是"内联挂起"，不是调度
- 旧版 `RequestRestoreRaisedWidgetsToDesktopLayer`（返回 true、只打日志 "held until=next-toggle"）已于 2026-10-17 删除：这是 v1.2.9 拍板的"raised 会话存活期间窗口自身回落挂起"语义，不是遗忘。现行形态=四个原调用点（`CompleteTitleRename`、flyout Closed、`QueueRestoreDesktopLayerIfForegroundLeavesDeskBox`、`ReleaseInteractionLayer`）内联 `App.Current.WidgetManager is { WidgetsRaisedFromTray: true }` 判定。以为"请求一下就会回落"仍会落空——真正的回落永远走监视器。

---

## 7. 排查手册：遇到层级问题怎么看

### 7.1 先分类症状

| 症状 | 大概率原因 | 首查 |
|---|---|---|
| 唤起后点外部窗口，格子**从不**回落 | 回落信号全失效（坑 #1/#4）或监视器没启动 | 日志搜 `[TrayBatch] RaisedStateMonitor started` 是否出现 |
| 点**不同**窗口能回落，点**同一**窗口不回落 | 激活失败 + 鼠标检测失效（坑 #1/#3） | 搜 `SetForegroundWindow FAILED` |
| 点击外部页面后格子直接掉到所有页面后面 | 回落后又被空闲排序绝对置底（坑 #2） | 搜 `Group restore ... disposition=BehindForeground` 后是否又出现 `SetWindowToBottom` |
| 交互过一次格子后永远压屏 | 交互深度泄漏（坑 #4） | 搜 `Interaction watchdog` |
| 只有前台是提权应用时出问题 | UIPI：热键走钩子兜底、激活必失败 | 同第 2 行 |
| F7 时灵时不灵 | 同上（钩子路径在干活，主路径被 UIPI 拦） | `GlobalHotkeyService` 日志 `source=hook/registered` |

### 7.2 关键日志标记（App.Log / App.LogVerbose）

```
[GlobalHotkey] Triggered source=registered|hook     热键触发及路径
[TrayBatch] Raise requested / completed             唤起开始与结束
[TrayBatch] RaisedStateMonitor started/stopped      监视器生命周期
[TrayBatch] RaisedState released reason=...         回落触发及依据
        -foreground-changed      前台变化（可靠）
        -outside-click           鼠标边沿采样（50ms 采样器）
        -deskbox-leave           DeskBox 曾有前台后离开（可靠）
[TrayBatch] ToggleDecision=hide|raise reason=...    F7 决策依据
[ZOrder] ... SetForegroundWindow FAILED             激活失败（坑 #3）
[ZOrder] Group restore ... disposition=...          整组回落使用的前台锚点和策略
[ZOrder] Window order applied reason=...             组排序边界和执行结果
[TrayBatch] Interaction watchdog ...                交互泄漏看门狗（坑 #4）
[WidgetSession] changed/kept ...                    会话状态机迁移
```

### 7.3 标准复现路径（回归用）

1. 在窗口 X 中按 F7 → 格子浮起。
2. 点击**另一个**窗口 Y → 格子应立即被 Y 盖住（前台变化路径）。
3. 再按 F7 → 格子重新浮起（`visible-widgets-behind` 特性，预期行为）。
4. 点击**同一个**已激活窗口 1 次 → 格子应被盖住（鼠标边沿路径；修复前这里会卡住）。
5. 再按 F7 → 格子正常收起。
6. 提权应用（如管理员终端）在前台时重复 1-5，行为应一致（高位采样对提权进程同样有效）。
7. 依次打开页面 A、B、C；在 C 上按 F7，再点击 C：预期顺序为 `C > 格子组 > B > A`，格子不得落到 A 之后。
8. 重复第 7 步并点击 B：预期顺序为 `B > 格子组 > C > A`；再等待超过 120ms，顺序不得被空闲整理改变。

---

## 8. 本次修复（2026-07-24）变更清单

> 本节保留历史记录。2026-08-12 的现行实现已用 `RelativeLayerRestorePolicy` 与 `RestoreGroupPreservingForeground` 替代旧的“无条件 `BringWindowToFront(foreground)`”回落方式，实际排查应以 §1、§4.4 和当前代码为准。

| 方案 | 文件 | 变更 |
|---|---|---|
| A 恢复要看得见 | `Services/WidgetLayerService.cs` | `ClearTopMostPreservingForeground` 去掉 `wasTopMost` 门控，无条件 `BringWindowToFront(foreground)` |
| B 检测可靠化 | `Helpers/Win32Helper.cs` | 新增 `IsAnyMouseButtonDown()`（高位物理状态） |
| B | `Services/WidgetManager.ZOrder.cs` | 新增 50ms 采样定时器（边沿检测 + 按下瞬间位置过滤）；200ms 监视器改消费 `_outsideMousePressObserved`；`StopTrayLayerRestoreMonitor` 同步停采样器 |
| B | `Services/WidgetManager.TrayAnimation.cs` | 删除为旧低位机制预充的 `HasMouseButtonActivity()` 调用 |
| D 可观测 | 两个宿主的 `ActivateRaisedFromTrayBatch` | 检查 `SetForegroundWindow` 返回值并记日志 |
| D 看门狗 | `Services/WidgetSessionManager.cs` | 新增 `ForceResetInteractions` |
| D 看门狗 | `Services/WidgetManager.ZOrder.cs` | `RunInteractionLeakWatchdog`：泄漏 >10s 且无 DeskBox 前台 → 强制清零 |
| C toggle 语义 | — | **不改**：`visible-widgets-behind → raise` 特性经用户确认保留 |

---

## 9. 2026-08-12 相对页面层级修复

| 文件 | 变更 |
|---|---|
| `Services/WidgetLayerService.cs` | 新增 `RestoreGroupPreservingForeground`；用同一个前台根窗口作为整组边界，并复用批量 peer 排序原语 |
| `Services/WidgetManager.cs` | F7 回落先统一清理宿主状态，再以单次组操作确定全局 Z-order；取消宿主排队的过期空闲整理 |
| `Services/WidgetManager.ZOrder.cs` | `NormalizeIdleWidgetZOrder` 改为 peer-only，不再对每个格子调用 `MoveToDesktopBottom` |
| `tests/DeskBox.Tests/WidgetZOrderRestoreContractTests.cs` | 锁定“外部页面用组恢复”和“空闲整理不得绝对置底”两项契约 |

预期层级示例：初始页面为 `C > B > A`，F7 后为 `格子组 > C > B > A`；用户点击 B 后必须变为 `B > 格子组 > C > A`，后续空闲排序只能改变格子组内部顺序。

---

## 10. 2026-10-17 层级撕裂专项批（右下角单格滞顶）

症状：偶尔只有一个格子（规范格序 `IdleWidgetZOrderPolicy` 的第一名=最下最右格）停在 z-order 顶部、其余全部沉底。根因=多条单窗抬升路径留下残留 + 自愈系统对撕裂态结构性失明（`IsPeerOrderAlreadyApplied` 只看同伴相对序，撕裂是它的不动点）+ 展开租约双态失步 + 交互泄漏无看门狗。五路调查+四路对抗评审定案后按序落地：

| # | 文件 | 变更 |
|---|---|---|
| ① | `WidgetManager.ZOrder.cs`（NormalizeIdleWidgetZOrder） | 新增 band 完整性检查（非 cohesion、非 pinned、非拖拽时，`IsForeignWindowAboveRaisedPeers` 判同伴间穿插外来窗=撕裂）→ 用 `RestoreGroupPreservingForeground` 相对回落治愈；cohesion-exit 后追加一次普通扫描跟进 |
| ② | `WidgetManager.ZOrder.cs` / `WidgetWindowBase.Collapse.cs` / `ContentWidgetWindow.xaml.cs` | 展开租约双态收口：新 `TearDownExpandedWidgetLayer`（代际守卫、无回落副作用）供 `EndExpandedWidgetLayerLease` 回缴；显示路径 `ReconcileStaleExpandedWidgetLayerLease` 清"隐藏时展开→再显示"的窗口侧残留 |
| ③ | `ContentWidgetWindow.WindowInteraction.cs` | 动态模式原生激活（身体点击）在 Activated 非 Deactivated 分支翻 `IsAtDesktopLayer=false`，堵"回落漏斗④"（守卫 `!IsAtDesktopLayer` 永不通过、悬浮无人接管） |
| ④ | `WidgetManager.TrayAnimation.cs` | `QueueTrayRaiseBandRecheck`：唤起确认后 80ms 一次性带完整性复检（守卫=代际+会话+DeskBox 前台+拖拽门+shell 豁免），撕裂则整组重抬——v1.2.9 删除的多次确认的安全复活形态 |
| ⑤ | `WidgetManager.ZOrder.cs` / `WidgetSessionManager.cs` 等 | 交互泄漏看门狗首次真实实现（见坑 #4）；`RequestRestoreRaisedWidgetsToDesktopLayer` 伪回落删除、四调用点内联会话判定；真死代码清理（`QueueRequestedLayerRestoreCheck`/`ForceRestoreRaisedWidgetsToDesktopLayer`/`BringAllVisibleWidgetsToFront`/三参 `QueueTrayRaiseTopMostConfirmation`）；`IsPhysicallyAtDesktopBottom` 归一到统一分类器语义（跳过最小化/cloaked 窗） |

契约测试同步：`WidgetExpandedLayerTeardownContractTests`（新）、`WidgetZOrderRestoreContractTests`（扩充：原生激活翻转、内联挂起、延迟复检守卫、band 修复相对出口、看门狗豁免）。日志新标记：`[ZOrder] Idle band torn`、`[ZOrder] Raised band split repaired by delayed recheck`、`[ZOrder] Expanded lease torn down`（verbose）、`[ZOrder] Stale expanded lease residue cleared`（verbose）、`[TrayBatch] Interaction watchdog reset`。

### 10.1 修复后对抗复审增补批（2026-10-18，含 issue #553）

六批落地后经三轮对抗筛查修订四处（行号以当时代码为准）：

| # | 文件 | 变更 |
|---|---|---|
| #553 | `WidgetLayerService.cs`（BringGroupTemporarilyToFront 尾部） | 尾部同步 `SetForegroundWindow` 在输入队列排干期死锁过 UI 线程（用户 dump 实锚：RIP=NtUserSetForegroundWindow）。加"前台已属本带（句柄在前台或前台根 ∈ handles）即跳过"守卫——三条热路径（标题点击/托盘激活/延迟复检）的尾部激活本就冗余，跳过即消除；真需前台转移的罕见路径保留同步。不采纳 issue 提议的 Task.Run 后台化（会丢失同输入事件的前台授予，重开 F7 分带族） |
| ④复活 | `WidgetManager.TrayAnimation.cs` | `QueueTrayRaiseBandRecheck` 原排队点带 `_isTogglingWidgetsDesktopLayer` 守卫——动画唤起的 toggling 窗≥137ms 必然吞掉 80ms 复检（主打场景从未生效）。排队移到 `await WaitForIdleAsync()` 之后（快速预览仍跳过），守卫随之不再是自毁式 |
| 自激环 | `WidgetManager.ZOrder.cs`（sweep）+ `WidgetWindowBase.Interaction.cs`（ClearTopMostOnly） | sweep 治愈后自验证撕裂是否消失，只对"真治愈"才 `-band-repaired` 重排（PreservePeerOrder 无位移分支不再每 120ms 自续命刷屏，info 日志另加 60s 限频）；`ClearTopMostOnly` 改为按处置结果置标志——PreservePeerOrder（无位移）保持原值，不再把物理浮着的窗标成"已回床"致盲 Deactivated 漏斗 |
| 看门狗 epoch | `WidgetSessionManager.cs` | `EndInteraction` 在强清后 2s 宽限窗内、深度将归零的 End 视为陈旧吞掉（日志 `stale end swallowed post force-reset`），防陈旧 End 清零强清后新开的交互 |

### 10.2 三轮复审修复批（2026-10-18，T1/T2/T3+P3）

| # | 文件 | 变更 |
|---|---|---|
| T1 | `WidgetManager.ZOrder.cs`（sweep） | heal 前预判前台归属：DeskBox 持前台（`IsDeskBoxForegroundWindow`）→ 一律走 deferred 分支不做 heal（日志 `Idle band tear deferred; DeskBox owns foreground`）。依据：PreservePeerOrder 分支唯一"治愈"形态=把床底兄弟抬到 z 最高滞留格=无人负责回落的无标志 peer-lift；该状态下现行"治愈"即 bug 本身，跳过零损失，修复延迟到前台离开后的 BehindForeground |
| T2 | `WidgetSessionManager.cs` + 看门狗 tick | 吞 End 记 `_lastSwallowedEndUtc`+`_beginSinceLastSwallow` 保护标记；新 `ReconcilePostResetDepth(now, foregroundConsistent)`（深度==1+吞后无 Begin+≥3s+前台非 DeskBox 系→清零）由看门狗 tick 在泄漏阈值判定前调用并排队一次整理——治"幻影深度卡 1"（成对 Begin/End 永不归零、最坏可超 22s）；吞 End 日志经构造第二委托升 info |
| T3 | `WidgetManager.cs`（RefreshVisibleWidgetDesktopLayers） | `_isTogglingWidgetsDesktopLayer` 期间延迟重试（250ms 步进至动画结束）而非内联执行——防唤起动画中途被逐窗强回落拆带；该路径是 QR→动态切换下已可见窗唯一即时铺层者，故延迟永不丢弃 |
| P3 | `ZOrder.cs`/测试 | 归一化 reason 在 `QueueIdleWidgetZOrderNormalization` 入口截断 96 字符（防链式后缀无界膨胀）；`NoDisplacementRestore` 钉改行尾无关的顺序断言（原多行字面量在 CRLF 检出上永不匹配=空钉）；格式修复 |

契约钉随批加固：F1 接线钉（`ForceRestoreDesktopLayerFromManager→EndExpandedWidgetLayerLease` 先于强制回落）、撕裂门先于治愈出口的顺序钉、④守卫极性钉（`!IsForegroundDeskBoxWindow()`）、看门狗豁免刷新先于 elapsed 的顺序钉、内联挂起计数钉（每表面各≥2）、#553 跳过先于激活的顺序钉。

---

## 11. 2026-10-31 三模式体验细化与第一阶段阴影重排止闪批

1. **动态层级（`Dynamic`）点击抬升范围可选 + 桌面单击解耦**：
   - 新增设置 `DynamicClickRaiseScope`（`Single` 默认仅抬升当前格子 / `Group` 点击格子任意区域整组一起浮出）。`Single` 模式点击标题栏或内容区前预先解除 `SHELLDLL_DefView` Owner 绑定（`DetachSingleDynamicWidgetForInteraction`），并在激活后将仍静置的同伴沉回桌面层（`RebedDynamicPassiveSiblingsAfterSingleActivation`）；`Group` 模式在点击内容区激活时通过 `BringGroupToNormalBandWithoutActivation` 将同伴提升至普通带紧随当前激活格子，离开 DeskBox 时由 `_temporaryRaiseLease` 一次性原子回落整组（DeskBox 仍持有前台期间不再提前清空租约）。
   - 通过 `WM_WINDOWPOSCHANGING` 授权深度标记（`Win32Helper.IsAuthorizedZOrderChangeInProgress`）+ `ShouldSuppressUnsolicitedZOrderChange` 拦截系统因点选/框选桌面普通图标而连带拉起 `SHELLDLL_DefView` Owner 带的非授权 Z-order 抬升（第 0 帧即追加 `SWP_NOZORDER`）；真正的“单击桌面空白处”由 `DesktopDoubleClickActivationService`（排除框选拖拽）单独回调 `PromoteWidgetsFromDynamicBlankDesktopClick` 抬升整组。
2. **桌面固定层（`DesktopPinned`）防串层与未获焦点选中态弱化**：
   - 同样由 `WM_WINDOWPOSCHANGING` 同步拦截非授权 Owner 带串层；`MoveToDesktopBottom` 改为返回 `bool`（已落底快速短路返回 `false`），消除 `MA_NOACTIVATE` 点击后无意义的 120ms 延迟整理。
   - 当格子宿主未持有 Win32 键盘焦点（`FileSurfaceContent.IsSurfaceWindowKeyboardFocused() == false`）时，`FileItemSurfaceStyleCache` 隐藏选中描边环并降级为低透明度柔和底色，`SelectedNameExpansionPolicy` 同时禁用长文件名展开浮层与慢速二次点击重命名，避免误导用户在外部前台窗口内按快捷键。
3. **快捷唤起层（`QuickReveal`）支持从背后窗口拖入内容**：
   - 采样器在 `QuickReveal` 会话下切为 16ms 步进，经 `QuickRevealOutsideInteractionState` 跟踪外部按下/拖拽：外部按下期间挂起前台离开收起（`ShouldHoldForegroundDismiss`）；未超 `SM_CXDRAG`/`SM_CYDRAG` 松手按外部点击收起；跨阈值拖入 DeskBox 格子松手保持面板打开，拖到外部松手则收起。
4. **第一阶段阴影重排止闪（100% 保留原生圆角、Mica/Acrylic 与原生 DWM 阴影）**：
   - `RestoreGroupToDesktopBottomAtomically` 将整组回落桌面底层合并为单次 `DeferWindowPos` 原子提交（以最低外部窗口为锚点直接写入目标同伴顺序，消除旧版 $N$ 次 `SetWindowToBottom` + 120ms 二次重排的双阶段跳变）；
   - `ClearTopMostOnly` 与 `RestoreDesktopPinnedBottomState` 在回落同帧执行 `ApplyImmediateRestingPeerOrderAfterRestore`；
   - `NormalizeIdleWidgetZOrder` 新增 `IdleWidgetZOrderPolicy.HasAnyShadowOverlapRisk`（88px 物理阴影半径检测），互不靠近的独立格子直接跳过同伴重排。
