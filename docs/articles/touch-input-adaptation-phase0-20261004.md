# DeskBox 触摸屏适配 · 第一步（Phase 0 基线实证）方案

- 日期：2026-10-04
- 状态：待 Simon 拍板执行
- 背景：用户反馈"鼠标键盘操作没问题，触摸屏上很多功能不能用"
- 关联：本文档为触摸适配线的立项底稿；Phase 0 不改任何产品行为代码

---

## 0. 结论摘要（先读这段）

三轮只读代码审计（2026-10-04，两轮输入事件面 + 一轮遗漏面）+ 微软文档/社区对照后，**第一步裁定为：Phase 0 触摸基线实证**——在一台触摸设备上用半天到一天跑完基线断言 B1–B12 与六阶段功能矩阵，并行向反馈用户发确认清单。

理由：静态审计自身出了错（见 §2.2，第一轮的核心根因被微软文档推翻），所有"推测失效"清单在实证前**不能指导修复**。直接开修的后果是：批 1 会花一天去替换 21 处根本没坏的左键门判断（白做 + 平白引入回归风险），而真正压垮触摸用户的 P0（软键盘不弹出，一切打字功能不可用）在原方案里排不进前两批。

Phase 0 产出：修正后的断点定案清单 + 批 1'–批 5' 范围定稿。成本：半天到一天，零产品代码改动。

---

## 1. 审计覆盖面回顾（三轮）

| 轮次 | 覆盖面 | 产出 |
|---|---|---|
| 第一轮：输入事件 | PointerPressed/Moved/Released/Entered/Exited、`IsLeftButtonPressed`（21 处）、`PointerDeviceType`、RightTapped（10 处）、PointerWheelChanged、DoubleTapped、XAML CanDrag 拖拽入口 | 14 条"推测完全不可用"清单 + 6 条"需真机确认" |
| 第二轮：功能可达性 | 悬停显隐 UI、无键盘入口盘点、小点击目标、原生右键菜单通路、滚动容器、触摸相关痕迹 | 入口断点（搜索/整理桌面）、hover-only 条目按钮清单、24–28px 命中目标清单 |
| 第三轮：遗漏面 | 软键盘（InputPane）、`GetCursorPos` 全景（31 处）、`GetAsyncKeyState`/VK 按键状态、`SetWindowsHookEx` 钩子（2 处）、WndProc 子类化（14 处）、拖入方向 grfKeyState、`IsPointerOver` 功能依赖、Holding、指针捕获、Flyout 锚点 | 新 P0（SIP 零处理）、修饰键交互全灭、组标签 WM_MOUSEWHEEL 子类化等 10 条新断点 |

## 2. 关键认知修正

### 2.1 被推翻的断言：`IsLeftButtonPressed` 触摸语义

第一轮审计断言"触摸 PointerPressed 时 `IsLeftButtonPressed` 恒为 false，21 处左键门全部死亡"。**微软文档证明这是错的**：

> [PointerPointProperties.IsLeftButtonPressed — Remarks](https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.pointerpointproperties.isleftbuttonpressed)（原文摘录）：
> Examples of primary action modes for various input devices:
> - **A touch pointer when it is in contact with the digitizer surface.**
> - A pen pointer when the pen tip is in contact with the digitizer surface and no modifying buttons… are pressed.
> - A mouse pointer when the left mouse button is pressed.

即触摸接触期间 `IsLeftButtonPressed == true`（与 `IsInContact` 同步）。仓库里的"正面样例"`FileSurfaceContent.ItemVisuals.cs:130-134`（`IsLeftButtonPressed || (Touch/Pen && IsInContact)`）实为冗余防御写法，两分支对触摸等价。

**影响**：第一轮 14 条"推测完全不可用"里约一半（窗口拖动入口、胶囊点按展开入口、框选、解组长按、todo 拖色、搜索结果拖出、单击收起）从"左键门挡死"降级为"入口应该能过，是否失效取决于其他因素（坐标源、hover 闸门、手势竞争）"。`IsLeftButtonPressed` 替换批**取消**，仅保留 B1 真机复核（WinUI 3 投影与 UWP 文档语义一致性——理论上是同一 WinRT 类型，预期通过，但 WinUI 3 手势栈有过行为差异前科，必须复核一次）。

教训（记档）：子代理的 API 行为断言未经文档/实证对照不得进入修复方案。本轮已在方案层完成对抗校验。

### 2.2 新坐实的 P0：软键盘（SIP）零处理

- 第三轮 grep 证实全仓库**零** `InputPane`/`InputPaneInterop`/触摸键盘处理；受影响输入框全景：搜索弹窗（`Views/SearchPopupWindow.xaml:324`）、快速捕获输入（`Controls/WidgetContents/QuickCaptureSurfaceContent.xaml:106`）、文件内联重命名（`FileSurfaceContent.xaml:800`）、通用内联编辑器（`WidgetInlineEditor.xaml:44`）、堆叠弹层改名窗（`Views/StackPopoverInlineRenameWindow.cs:38`）、Todo 标题/步骤三框（`TodoWidgetContent.xaml:670/798/834`）、Markdown 源编辑器、设置页搜索 + 云备份表单（约 10 处）。
- 理论上[触摸设计指引](https://learn.microsoft.com/en-us/windows/apps/design/input/touch-interactions)称"Windows app text input controls invoke the touch keyboard by default"，但 WinUI 3 desktop 没有 CoreWindow，触摸键盘不自动弹出是该形态的已知缺陷（对照：[键盘 API 迁移指南](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/guides/keyboard-events)、[microsoft-ui-xaml 讨论 #8874](https://github.com/microsoft/microsoft-ui-xaml/discussions/8874)）。
- 标准修法（社区 + 文档一致）：`InputPaneInterop.GetForWindow(hwnd)` 取 `InputPane`，在文本框获得焦点（`FocusState.Pointer`）时 `TryShow()`（[TryShow 文档](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.inputpane.tryshow)注明为 best-effort）。
- **"很多功能都不能用"的反馈与此高度吻合**：纯触摸用户点开搜索/快采/重命名，键盘不弹出，一切打字链路死亡。这是触摸用户最先撞到的墙。

### 2.3 长按冲突（社区证据支持，仍需实测表现）

触摸上"按住"同时是右键菜单（press-and-hold → RightTapped）与拖拽启动（short-press-and-drag → CanDrag）的手势来源，二者天然竞争。[microsoft-ui-xaml #1500 讨论](https://github.com/microsoft/microsoft-ui-xaml/issues/1500)确认了该冲突的真实性（"With touch input, the option to hold on a row and just move it"），常见结局是一方永远不触发。DeskBox 文件项同时挂 `CanDrag`（拖出/重排）与 `RightTapped`（原生 Shell 菜单）——触摸下谁赢必须实测（B3）。

### 2.4 无权威文档、只能实测的关键项：`GetCursorPos` 触摸行为

第三轮盘点出 31 处 `GetCursorPos`，核心消费者：窗口拖动/缩放（`WidgetWindowBase.Interaction.cs:253/283/488/511`、`SearchPopupWindow.xaml.cs:665/689`）、胶囊悬停展开权威闸门（`WidgetWindowBase.Collapse.cs:2240/2249`）、拖入有效性判定（`NativeDragDrop.cs:648`）、组拖拽全程（`WidgetManager.Groups.cs:2174/3187`）。Windows 触摸通常会把 legacy 光标位置合成到触点（服务非 pointer-aware 程序），但是否"按住拖动期间持续跟手指"因驱动/系统设置而异，且公开文档没有承诺。**若跟手指：窗口拖动/胶囊探针大概率半可用；若不跟：整条坐标源需要改造**。检索无权威结论，列为 B2 实证项。

## 3. 微软文档对照要点（已核原文）

| 文档 | 对本线的意义 |
|---|---|
| [PointerPointProperties.IsLeftButtonPressed](https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.pointerpointproperties.isleftbuttonpressed) | 触摸接触=主动作=true。推翻第一轮根因一（§2.1）。Pen：笔尖接触且无 barrel 键；barrel → `IsRightButtonPressed` |
| [Touch interactions 设计指引](https://learn.microsoft.com/en-us/windows/apps/design/input/touch-interactions) | 命中目标：**最小 40×40 epx**（视觉可以更小），宽 ≥120 epx 时高可 32 epx；**触摸优化 44×44 epx + 目标间 ≥4 epx 间距**。官方推荐两种策略："always touch optimized"或"基于设备信号切换"——后者即社区通行的"最后输入设备检测触摸模式"。手势惯例：tap=激活；**short-press-and-drag=移动对象**；**press-and-hold=上下文菜单**；swipe=快捷命令 |
| [Touch developer guide](https://learn.microsoft.com/en-us/windows/apps/develop/input/touch-developer-guide) | 静态手势事件（Tapped/DoubleTapped/RightTapped/Holding）在交互结束后才触发，与 manipulation/drag 的仲裁关系——解释 §2.3 冲突机制 |
| [InputPane.TryShow](https://learn.microsoft.com/en-us/uwp/api/windows.ui.viewmanagement.inputpane.tryshow) + [键盘迁移指南](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/migrate-to-windows-app-sdk/guides/keyboard-events) | WinUI 3 desktop 无 CoreWindow → SIP 行为异于 UWP；`InputPaneInterop.GetForWindow` 是唯一正道；`TryShow` 是 best-effort 语义 |
| 触摸键盘 reflow 指引（touch-interactions §Touch keyboard） | 键盘可占屏 50%；不得遮挡活动文本框，遮挡时滚动内容或移窗——DeskBox 多为贴桌面的浮窗，SIP 弹出后的窗口避让要做（批 1' 范围） |

## 4. 社区方案对照

| 来源 | 结论 / 可复用方案 |
|---|---|
| [microsoft-ui-xaml #8874](https://github.com/microsoft/microsoft-ui-xaml/discussions/8874)（2023-09） | WinUI 3 desktop 取 InputPane 必须 `InputPaneInterop.GetForWindow(hwnd)`；订阅 Showing/Hiding；`TryShow()/TryHide()`。#8874 给出标准代码形态 |
| [microsoft-ui-xaml #1500](https://github.com/microsoft/microsoft-ui-xaml/issues/1500) | hold/drag 手势冲突真实存在；workaround 三方向：容器关 CanDrag 改手动 `StartDragAsync`、按 `PointerDeviceType.Touch` 分流、用 `Holding` 事件替代 RightTapped 做触摸菜单。DeskBox 已有 `StartDragAsync` 手动路径先例（分组标题/搜索行），批 5' 可复用 |
| [#8434](https://github.com/microsoft/microsoft-ui-xaml/issues/8434) | UWP ListView 触摸交互未处理异常案例——触摸矩阵跑拖拽时留意同类崩溃（风险提醒，非断言） |
| WebView2 + WinUI 3 SIP 不弹案例（Stack Overflow） | 同形态问题的旁证：焦点进非 XAML 岛控件时 SIP 静默失败 |

## 5. Phase 0 定义

**Phase 0 = 触摸基线实证 + 用户反馈校准**，产出可执行的批次定稿。不做的事：不改任何产品行为代码、不提交修复、不先入为主按推测清单开修。

执行人：Simon 手动操作（遵循"一次一个阶段、明确指令 + 观察"的惯例），我负责矩阵设计、打点采样与结果归档。设备：任一触摸屏 Windows 设备（平板/二合一最佳，可覆盖"无键鼠纯触摸"场景）；如暂无设备，B1/B2 可先用 `InitializeTouchInjection` 合成触摸的探针工具替代（合成触摸带注入标志，与真实驱动仍有差异，最终以真机为准）。

### 5.1 基线断言清单（B1–B12）

| # | 断言 | 方法 | 对方案的影响 |
|---|---|---|---|
| B1 | 触摸按下时 `IsLeftButtonPressed == true`（WinUI 3 投影与文档一致） | 打点版 Debug：PointerPressed 处记设备类型 + `IsLeftButtonPressed` + `IsInContact`（一处样例打点即可，建议挂文件格子标题栏） | 通过 → 左键门替换批彻底取消；不通过 → 恢复原批 1（21 处替换为 `IsLeftButtonPressed \|\| (Touch/Pen && IsInContact)`） |
| B2 | 触摸按住拖动期间 `GetCursorPos` 持续跟手指；抬指后停在最后触点 | 打点版：窗口拖动路径同时记 `GetCursorPos` 与 `e.GetCurrentPoint` 屏幕坐标，触摸拖动 3 秒比对 | 跟随 → 窗口拖动/胶囊探针/拖入判定"半可用"，坐标源改造降级为打磨项；不跟 → 坐标源改造独立成批（本线最大单项工程） |
| B3 | 文件项长按：RightTapped（原生 Shell 菜单）与 CanDrag 拖拽谁触发 | 触摸：a) 按住不动 1.5s；b) 按住立刻拖动；c) 短按 | a 出菜单 + b 起拖 = 冲突可共存；只出一方 → 批 5' 启用手势分流（社区 #1500 三方案） |
| B4 | 触摸点各文本框，SIP 弹不弹 | 逐个输入框点按（搜索/快采/重命名/todo/设置页） | 任一不弹 → 批 1'（SIP 接线）成立；全弹 → 批 1' 降级为防回归 |
| B5 | 桌面空白触摸双击 → WH_MOUSE_LL 收到合成消息 → 唤出格子 | `DesktopDoubleClickActivationService.cs:405/413` 已放行 LLMHF_INJECTED；触摸双击桌面观察格子是否唤出 | 不唤出 → 静默启动的触摸用户只剩托盘入口，批 4' 加格子表面唤出 |
| B6 | 子类化 `WM_LBUTTONDOWN` 关原生菜单路径收不收触摸合成消息 | 触摸点格子内空白处，观察已开的原生 Shell 菜单是否关闭（`NativeDragDrop.cs:756`、`StackPopoverHostWindow.cs:344`） | 不关 → 原生菜单触摸下关不掉，批 5' 补 XAML 层关闭兜底 |
| B7 | 桌面钉住（NOACTIVATE）格子的触摸点按能否激活窗口/聚焦内嵌输入 | 触摸点钉住格子的重命名框 | 不聚焦 → 与 B4 叠加，钉住格子的触摸输入全灭，批 1' 需含激活策略调整 |
| B8 | 折叠胶囊触摸可达性：点按展开、胶囊操作按钮、移动 | 触摸：a) 点胶囊图标区；b) 按住胶囊；c) 尝试点胶囊上的按钮 | 综合判定 Smart 收起模式触摸可用面；`Collapse.cs` hover 闸门（GetCursorPos 权威）+ `ApplyCompactActionVisibility` 的 hover 门在 B2 结果下推演实际行为并核对 |
| B9 | 触摸按住分组 Tab 不放，dwell 悬停切换（`Tabs.cs:226`）是否误触发 | 按住任一 tab 2s | 误切 → 批 5' 给 dwell 加设备类型门 |
| B10 | 触摸 pan 文件格子内容区：滚动正常？滚动条出现？误触发拖拽？ | 单指滑动内容区 | 基线记录；拖拽误触率高 → 与 B3 合并处理 |
| B11 | 触摸双击文件正常打开（DoubleTapped 触摸阈值） | 触摸快速双击文件项 | 通过则记录；失败则批 5' 查触摸双击间隔 |
| B12 | 音乐进度条、音量滑条、天气横拖等自绘拖动件触摸表现 | 触摸按住拖动各一处 | 入口均为接触语义（第三轮判定 OK 倾向），复核 |

打点版说明：B1/B2 需要（参照 DESKBOX_PERF_LOG 先例加 `[Touch]` 日志行，临时诊断、不入库或以诊断开关形式留）；B3–B12 纯观察即可。打点构建走 Debug 常规路径，不触碰 AOT/发布链。

### 5.2 功能复现矩阵（六阶段，手动执行）

每项记录 PASS / FAIL / PARTIAL + 一句证据（录像或日志行）。顺序按"触摸用户最先撞墙"排：

**阶段 1 · 打字链路（预期重灾区）**
1. 触摸点搜索组件按钮唤出搜索 → 点搜索框 → SIP 是否弹出、能否输入（B4）
2. 快速捕获：点输入框 → SIP（B4）；已有条目长按 → 菜单；条目上的复制/置顶/删除按钮（hover-only）能否点到
3. 文件长按菜单 → 重命名 → 点内联编辑框 → SIP（B4）
4. Todo：新建/编辑标题/加步骤 → SIP（B4）；条目操作按钮（hover-only）可达性
5. 设置页：触摸导航 + 搜索框 + 云备份表单（B4 顺带覆盖大 UI 触摸命中）

**阶段 2 · 文件基本操作**
6. 单击选中/打开（视 DoubleClickToOpen 设置）、触摸双击打开（B11）
7. 长按文件 → XAML 菜单 / 原生 Shell 菜单（`FileItemSystemContextMenuEnabled` 两档都测）（B3a）
8. 内容区触摸 pan 滚动 + 滚动条表现（B10）
9. 堆叠点按展开/收起；长按堆叠菜单

**阶段 3 · 拖拽**
10. 文件项长按后拖动 → 拖到桌面/其他窗口（拖出）（B3b）
11. Explorer 触摸拖文件进格子（拖入，默认意图）
12. 格子内拖动重排；快采/todo 列表项拖动；tab 拖出重排（B3 泛化）

**阶段 4 · 窗口管理**
13. 触摸拖标题栏移动格子；拖 Overlay 格子的悬浮把手（168×14，B2 综合）
14. 触摸拖边框缩放格子；搜索窗拖动/缩放
15. 折叠胶囊全组操作（B8）：点按展开、按住、按钮区、长按右键菜单
16. 标题栏按钮（24×24）命中测试：关闭/更多/添加/锁定（纯命中，非功能）

**阶段 5 · 入口**
17. 静默启动（或手动隐藏）后：托盘左键、桌面空白触摸双击（B5）、热键——触摸可达入口盘点
18. 搜索唤出：无搜索组件时仅热键（已知硬断点，记录即可）
19. 整理桌面入口：托盘右键 → 触摸可达性

**阶段 6 · 杂项基线**
20. 天气/日历/Glance 基本点按；日历翻月按钮；音乐播放控制（B12）
21. Ctrl/Shift 依赖交互不可用确认（多选、Ctrl+滚轮缩放——纯记录，设计层批 3' 处理）
22. 长按已开的原生菜单外空白处关闭（B6）

### 5.3 反馈用户确认清单（并行发）

1. 设备形态：纯平板 / 二合一 / 带触摸笔记本？Windows 10 还是 11？平时是否接键鼠？
2. "不能用"的具体功能（给勾选清单，按阶段 1–6 分组）
3. 失效的具体表现分类：点了没反应 / 点了没键盘 / 按钮找不到 / 长按也没用？
4. 长按文件出过右键菜单吗？（校准 B3 实测方向）
5. 能否在触摸操作时段再导一份诊断包（校准日志）

---

## 6. Phase 0 输出物与验收

1. 矩阵结果表（22 项 + B1–B12 断言，PASS/FAIL/PARTIAL + 证据）
2. 修正后的触摸断点定案清单（取代三轮推测清单，作为后续批次唯一事实源）
3. 批 1'–5' 范围定稿（§7 映射表填实）
4. 用户反馈归档（若用户回复）

验收标准：B1/B2/B3/B4 四个关键断言全部有定论；矩阵六阶段全覆盖；无"推测"字样残留于定案清单。

## 7. 矩阵结果 → 后续批次映射（修正版）

| 批次 | 范围 | 触发条件 | 预估 |
|---|---|---|---|
| 批 1' | SIP 全接线：`InputPaneInterop` 服务 + 全部文本框 GotFocus→TryShow + SIP 弹出后的窗口避让（reflow 指引）；叠加 B7 钉住激活问题 | B4 任一 FAIL（预期成立） | 1–1.5 天 |
| 批 2' | hover 杀手：App 级"最后输入设备"检测 → 触摸模式；条目操作按钮（快采/todo/附件/Glance）、胶囊动作区、Overlay 把手在触摸模式常显或按住浮现；胶囊 hover 闸门补触摸路径 | B8 FAIL（预期成立，静态已证 hover-only 无替代） | 1–2 天 |
| 批 3' | 手势等价设计批：捏合缩放映射 IconSizing；长按进入多选模式（修饰键触摸等价，Android/iOS 惯例）；swipe 快捷命令评估 | 设计拍板项，Simon 决定做多大 | 0.5–2 天 |
| 批 4' | 入口与命中：托盘补搜索/整理项或格子表面入口；命中目标按 40/44 epx + 4 epx 间距在触摸模式扩展（视觉尺寸不变，扩热区） | 矩阵阶段 5 结果 | 0.5–1 天 |
| 批 5' | 实证失败项修复池：B2 坐标源改造（若不跟手指，本线最大工程，独立成批）/ B3 手势分流（StartDragAsync 手动路径 or Holding 菜单）/ B5 唤出入口 / B6 菜单关闭 / B9 dwell 门 / B11 双击阈值 | 逐条由矩阵结果点亮 | 按项计 |
| （条件批） | 左键门替换：`IsLeftButtonPressed → IsPrimaryPress` 21 处 | 仅当 B1 意外 FAIL | 1 天 |

红线提醒（进入批 1' 后适用）：XAML 改动先查契约测试冻结计数（x:Bind 计数等）；新增设置项过 12 语言；InputPane 互操作注意 AOT（P/Invoke 互操作常规，无反射风险）；触摸模式判定逻辑入 Contracts 层时同步 `WidgetCompactInteractionPolicy` 策略类。

## 8. 风险与未决

- 矩阵执行依赖触摸设备可得性；无设备时 B1/B2 可先合成触摸探针，但 B3–B12 的手势感受（长按时长、拖拽阈值）必须真机。
- 合成触摸（`InitializeTouchInjection`）与真实触摸驱动存在行为差异（注入标志、系统手势层），只作过渡手段。
- `TryShow` 是 best-effort：即使接线正确，个别系统状态（平板模式关闭、触摸键盘服务被禁）下仍不弹，批 1' 需留降级路径（提示 + 手动唤起指引）。
- WinUI 3 触摸栈存在上游 bug 可能（#8434 先例），矩阵中任何崩溃单独记录复现路径，勿并入功能 FAIL。

---

## 9. 本地实证记录（2026-10-04，无触摸硬件开发机）

**装备就绪（可复用）**：
- 打点版 Debug 构建已产出并在跑：三处 `TOUCH-PROBE` 标记的诊断打点（工作区临时改动，勿提交）——`ContentWidgetWindow.WindowInteraction.cs`（TitleBarPointerPressed：dev/left/contact，B1 采样）、`WidgetWindowBase.Interaction.cs` ContinueWindowDragCore（cursor vs ptrLocal 坐标对比，B2 采样）、`FileSurfaceContent.xaml.cs` Items_RightTapped（fired，B3 采样）。日志走 `App.Log`，过滤 `grep "\[Touch\]" %LOCALAPPDATA%\DeskBox\DeskBox.log`。
- 注入探针：`artifacts/touch-probe-20261004/touchprobe/`（list / status / tap / doubletap / hold / drag；双后端）。`list` 模式可枚举 DeskBox 全部顶层窗口坐标供注入选址（已验证可用，10 窗口全列出）。

**合成触摸注入结论：本机不可用（已排尽公开 API）**：
1. `InitializeTouchInjection` 返回成功，但 `InjectTouchInput` 对任意帧（DOWN/纯 MOVE/UP，含非零 `dwTime`、非零接触面积、INDIRECT 反馈模式、前置 MOVE 等全部变体）一律失败 `err=0`。
2. `CreateSyntheticPointerDevice(PT_TOUCH)` 返回 NULL——该 API 要求调用方 UIAccess 权限（普通未签名进程不可用）。
3. 根因证据：本机 `TabletInputService`（Touch Keyboard and Handwriting Panel Service）**不存在**（无触摸硬件系统未装触摸栈），仅 `hidserv` 在跑。会话环境已排除嫌疑（SESSIONNAME=Console，交互桌面 Session 1）。

**断言状态更新**（取代 §5.1 原始状态）：

| # | 状态 | 依据 |
|---|---|---|
| B1 | **按文档定案：touch contact → `IsLeftButtonPressed`=true**；真机复核降级为批 1' 验收时的顺手项 | 微软文档 Remarks 原文（§2.1）；本机无法注入，复核延后 |
| B4 | **按缺陷成立定案**（真机终验兜底） | 代码零 InputPane 处理（第三轮 grep 证据）+ WinUI 3 desktop 无 CoreWindow 的平台已知缺陷（§2.2），双重证据 |
| B2 | **待实证（唯一影响批次结构的重大未知）** | 本机不可注入；无公开文档承诺 GetCursorPos 触摸跟随行为 |
| B3/B5/B6/B8–B12 | 待实证：优先走反馈用户问卷（§5.3，用户设备有真触摸，长按/拖拽/SIP 均可直接口答），其次未来借真机 | 本机不可注入 |
| B7/B9/B10 | 同上（问卷覆盖 B7 的子项：钉住格子点输入框聚焦与否） | 同上 |

**下一步排序**（修订）：
1. 发 §5.3 用户确认清单（唯一当前可推进的实证通道；B3/B4 的答案直接决定批 1'/批 5' 结构）。
2. 批 1'（SIP 接线）可不等矩阵开工：B4 已双重证据定案，修法明确。
3. B2 留待任何一次真触摸机会（打点版与探针已备好，拿来即用：注入/触摸拖动格子标题栏 → `grep "\[Touch]"` 看 cursor 与 ptrLocal 是否同步）。

---

## 10. 用户反馈渠道拉取结果（2026-10-04）

**官网反馈（PostgreSQL 全量 409 条，含 closed）**：触摸相关 **0 条**（两轮关键词：触摸/触屏/触控/平板/手指/手写/touch/tablet/stylus/finger/surface + 兜底单字"触"/拖不动/手势，9 条命中全为"触发"类误命中）。

**GitHub issues（open + closed + 全状态搜索）**：触摸相关 **1 条**——[#497「触屏无法移动格子位置，也无法调整格子尺寸」](https://github.com/Tianyu199509/DeskBox/issues/497)：
- ha47i，2026-10-03，v1.5.5 / Win11 x64，open 未回复
- 原文："触屏无法移动格子以及调整格子尺寸，**其他部分暂未发现触屏适配问题**。既然项目使用winUI3，适配触屏应该不难，还请优化一下"

**交叉验证结论（用户实测面 × 代码分析）**：
1. "其他部分暂未发现问题"（点击/长按/双击正常）→ **B1 获得旁证**：左键门没挡触摸，与微软文档语义一致。三轮审计里 14 条"推测不可用"中的点击/框选/解组等项基本可判"实际可用"。
2. "无法移动格子/调整尺寸"恰好是 B2 怀疑区（`GetCursorPos` 坐标源 + 触摸手势抢 capture）→ **现象经真实用户确认**。失败机制两种候选（GetCursorPos 不跟手指 / capture 被手势抢），但对修复方案无影响（见下）。
3. 用户未报打字问题 → **B4（SIP）紧迫性下降**（不能证伪：他可能没用过输入功能），降级为修复批顺手验证项。

**B2 状态升级：无需 API 级实证**。正确修复 = 窗口拖动/缩放坐标源从 `GetCursorPos` 轮询改为指针事件坐标（`e.GetCurrentPoint` 屏幕换算），该方案在 GetCursorPos 无论跟不跟手指的两种行为下都正确——断言失去决策价值，直接进修复。

**批次结构再修订**：
- **批 1''（新，核心）= 窗口拖动/缩放坐标源改造**：`WidgetWindowBase.Interaction.cs` 拖动/缩放两链路 + `SearchPopupWindow` 同款，从 GetCursorPos 差值改为指针事件驱动；1 天；修完打测试包给 ha47i 验证闭环（打点版/探针保留作回归工具）。
- 批 1'（SIP）降为第二优先（等下一个输入类触摸反馈或问卷结果再开工）。
- 批 2'（hover 杀手）等问卷证据（用户未报胶囊/条目按钮问题——他可能没用 Smart 收起）。
- 触摸适配总策略定为**精准小批**：全渠道 409 反馈 + ~50 issues 仅 1 条触摸，长尾需求，不做大而全触摸模式，聚焦已证实的失效点。

**待 Simon 拍板**：是否回复 #497（内容建议：确认收到 + 已定位根因 + 下版本修复 + 届时请帮忙验证）。

---

## 11. 批 1'' 执行记录（2026-10-04，已实现未提交）

**改动（4 文件）**：
- `Platform/Win32Helper.cs`：新增 `ClientToScreen` P/Invoke 与 `TryGetPointerScreenPoint(hwnd, relativeTo, e, out POINT)`——客户区屏幕原点 + `GetCurrentPoint(relativeTo).Position × GetDpiScaleForWindow` 换算出指针事件的屏幕坐标。**每帧重读客户区原点**是正确性关键：窗口被我们移动后原点随之更新，指针屏幕坐标始终闭环，无需初始锚点校准。
- `Views/WidgetWindowBase.Interaction.cs`：拖动两切点（`BeginWindowDragCore`/`ContinueWindowDragCore`）与缩放两切点（`ResizeBorder_PointerPressedCore`/`MovedCore`）全部改用指针屏幕点；`GetCursorPos` 降级为 `XamlRoot` 未就绪时的 fallback。字段 `InitialCursorPt` 更名 `InitialPointerPt`（`WidgetWindowBase.cs`）。
- `Views/SearchPopupWindow.xaml.cs`：`TryBeginWindowInteraction` 签名 `(element, Pointer)` → `(element, PointerRoutedEventArgs)`，`WindowInteraction_PointerMoved` 同步换源。

**触摸正确性论证**：主路径完全不读鼠标光标；触摸指针的事件坐标 + `ClientToScreen` 与光标状态无关，故在 GetCursorPos 无论跟不跟手指的两种系统行为下均正确（B2 断言失去决策价值的正式落地）。

**鼠标回归验证（本机实测，双源对比法）**：DragSample 打点同时输出新源 `ptr` 与老源 `cursor`，注入鼠标拖动格子标题栏 42×55px——**逐帧完全相等**（`ptr=(1188,45) cursor=(1188,45)` 等 29 帧），窗口实际位移 (+42,+57)（差 2px 为吸附取整），格子已复位。证明新坐标源与老算法在鼠标下逐像素等价，零回归。

**验证过程的环境事实（复用价值）**：
- 桌面模式下格子沉底层，注入测试前需先显示桌面（探针 `mtap` 点任务栏 Show-Desktop 角，会最小化前台窗口）；`WindowFromPoint`（探针 `hit`）用于注入前选址自检。
- 探针已扩充鼠标注入能力：`mmove`/`mtap`/`mdrag`/`hit`/`mpos`（SendInput 绝对坐标 + 虚拟桌面归一化），与触摸命令并存——本机触摸栈缺失，鼠标注入是 UI 回归的主要注入手段。

**收尾待办**：① ~~测试套件 x64~~（**已通过：4775/4775，0 失败，1m30s**，2026-10-04）；② ha47i 验证用测试包（随下个版本或单独诊断包载体）；③ TOUCH-PROBE 打点三处去留——建议随修复批一并提交（低噪声、诊断价值高）或提交前剥离。

### 11.1 对抗回归补充（第二轮，2026-10-04）

**遗漏检查（胶囊/组形态的"移动格子"）**：
- 胶囊条移动/重排：位移链 = `ContinueWindowDragCore` delta → `ApplyTitleBarDragFrame` → `TryMoveCompactArrangement(frame.ProposedBounds)` → `TryMoveCapsuleBar`——**已被本批覆盖**，无需额外改动（`Collapse.cs:1943/1990` 的 GetCursorPos 是 hover 判定语义，非位移源）。
- 组跟随移动（coordinated move）：`UpdateCoordinatedMove(HWnd, frame.DeltaX, frame.DeltaY)` 吃的正是已修 delta——**已被覆盖**。
- **批 5' 遗留登记**（组高级交互仍读光标，触摸下可能失效，用户未报）：组落点合并判定 `IsGroupDragTargetUnderCursor`（`WidgetManager.Groups.cs:3187`）、组拖拽预览（`:2174`）、组员 detach 30ms 轮询（`WidgetWindowBase.Grouping.cs:392/451/526`）。
- 其余 GetCursorPos 消费点（托盘菜单锚点、原生菜单兜底、堆叠弹层回退、内拖提交判定、拖入判定、内存清理门控、ZOrder 采样）均为锚点/判定语义而非位移源；触摸下光标停旧位在多数场景仍判定正确，且有 #497"其他部分没问题"旁证，本轮不动。

**非触屏影响面**：
- 鼠标：双源逐帧相等两轮复验（29 帧 + 8 帧）+ 位移精确等量。
- **缩放链补测**：右缘注入 +27px → 宽度 280→307 精确等量，已复位。
- 触控板/笔：指针驱动光标，事件路径与鼠标同构，逻辑等价。
- **DPI 验证盲区（登记）**：本机 100%（AppliedDPI=96），scale=1 会掩盖乘法错误。公式已人工复核（物理像素 = DIP × RasterizationScale + ClientToScreen 客户区原点，与 `SearchPopupWindow.ApplyResizeDelta` 既有同模式代码一致）；非 100% DPI 与跨屏拖动（DPI 切换瞬间 scale 与 XAML 重排的时序差可能产生 1-2 帧自愈型跳变，锚点在物理屏幕系）留 ha47i 或多屏真机验证。
- **fallback 行为**：`XamlRoot` 未就绪时回退 `GetCursorPos` = 老行为；`ContinueWindowDragCore` 双源皆失败时 `e.Handled=true` 后跳帧（老代码会继续用 default(0,0) 算 delta 把窗口甩向左上角——新行为更安全，属顺带改进）。
- 打点卫生：DragSample 节流（每拖动会话 #0-2/#19/#39…），单次拖动 29 行 → 8 行。
