# DeskBox 格子组成员驻留（Residency）长期方案 —— 最终评估与决策记录

- 日期：2026-09-18（同日第四轮对码修订，见 §10）
- 状态：方案定稿，未立项实施（排在云同步/数据分层之后；P0 归因可提前插队，且**P0 结果决定后续投入规模**）
- 前置文档：
  - `C:\Users\simon\Desktop\2\格子组优化方向.md`（外部诊断稿）
  - `C:\Users\simon\Desktop\2\格子组优化方案2.md`（View Eviction 分阶段方案稿，本方案在其基础上修订）
  - `docs/architecture/module-boundary-roadmap-20260918.md`（#397 已合并，立法方法论沿用）
- 评估方法：三轮全量对码（17+22+14 项主张逐条核对到 file:line）+ 四路并行外部调研（微软官方与一手 issue、WinUI 3 内存机制、跨框架先例、同类产品）。所有外部结论均标注来源链接，见 §9。

---

## 0. 结论

0. **这条线对"降内存"是二阶杠杆，投入规模由 P0 归因决定。** 同日 AOT 曲线（`deskbox-aot-curve-samples.csv`：单个文件格子、无任何组）私有内存 128MB → 490MB plateau；组成员缓存的规模上限只是 组数 × (1~2) 棵虚拟化非活动树，量级估在个位数到几十 MB。P0 必须扩大为**全应用内存归因**（组成员树 / IconHelper 三缓存真实 native 尺寸 / File 投影 / 每窗口固定成本），若组成员树占稳态私有内存 <10%，只做 P1+P2，跳过 Cold 档。
1. **不推翻格子组架构。** 一组一窗（SurfaceId / 单 HWND / 切换事务 / 隐藏组纯 identity 切换）被四路证据一致支持：微软自家产品、WinUI 3 每窗口固定成本数据、开源同类产品、跨框架共识。推翻方向（每成员一窗口、单树共享）都有实证表明更差。
2. **选定方案 = 一种内容模式 + 三档成员状态：**
   - **北极星**：所有 widget kind 收敛为 adapter 持有 ViewModel/运行时、View 是可丢弃叶子。adapter 本身就是驻留单元；Warm/Cold 是**同一缓存字典里每个条目的状态位**，不是两级缓存。
   - **Active**（唯一完整树 + 全速运行）
   - **Warm**（树保留、运行时挂起 + TTL/内存压力降级）——**语义上就是现有缓存**，缺的是时间触发
   - **Cold**（`adapter.ReleaseView()`：树释放，VM/运行时随 adapter 存活，激活时重建）——**新建**，即方案2 的 View Eviction
3. **对方案2 的修订**：
   - 现有缓存 ≈ 业界 Warm 档，方案应是"补 Cold 档 + 给缓存加触发器"，而不是"用 View Eviction 替换缓存"；
   - 图片/缩略图是**进程级共享预算缓存**，组的内存增量主要在 XAML 树本身 → **内存收益必须走 Cold 档**，冻结层只省 CPU/订阅；
   - File/QuickCapture 的 `View => this`（控件即视图）使 lazy-getter 契约对两个最重对象不适用，必须先 adapter 化；
   - File 冷档的前置是 **adapter 化让 VM 归 adapter 所有**（今天 VM 随 content Dispose 一起死，逐出=重扫描+重建 watcher，正是 1.3.6 缓存要避免的回归）。adapter 化后不再需要 manager 级 VM 注册表（第四轮修订，见 §10）。
4. **Cold 档有两个硬前置，缺一不可**：① 每种 kind 的 transient state 必须完整到"丢树后可无损重建"（今天 File/Todo 都不存滚动位置、导航栈、展开堆叠）；② 意图预热（tab hover / Ctrl+Tab 相邻成员）在用户点下之前物化视图。没有这两条，Cold 档 = 1.3.6 之前的体验回归。
5. **实施前置 P0 测量协议必须包含强制 GC 分支**：WinUI 3 的 GC 会刻意推迟回收 XAML 对象（设计内行为），只看"清缓存后 Private Bytes"会得到假阴性。**同一事实的另一面：做完 Cold 档，任务管理器稳态数字可能不明显下降**，收益口径是"长跑非单调增长 + 内存压力时有东西可放"。

---

## 1. 现状（对码后的精确模型）

### 1.1 保留项（评估为正确设计，不动）

| 机制 | 证据 |
|---|---|
| 组模型：稳定 SurfaceId + ActiveMemberId + 普通成员 WidgetConfig，上限 8 | `Models/WidgetGroupConfig.cs:10-24`；`Services/WidgetGroupSettings.cs:12` |
| Surface 注册表：一个 SurfaceId 一个活跃 host，成员 id 是别名 | `Services/WidgetSurfaceRegistry.cs:8,50-51` |
| 合并后 retire 非活动成员旧窗口 | `Services/WidgetManager.Groups.cs:886-894` |
| 隐藏组切换 = 纯 identity，不建窗不建内容 | `WidgetManager.Groups.cs:1023-1035` |
| 切换事务 prepare/commit/rollback + 900ms 首帧超时回滚 | `Controls/WidgetShellContentHost.cs:91,180,427`；`WidgetManager.Groups.cs:20-21,1149-1155` |
| 切换时 transient state 捕获/恢复（按 widget id 存于 manager，**可跨 content 销毁存活**） | `WidgetManager.Groups.cs:32,1190-1232,2734-2738` |

### 1.2 成员缓存现状

- 入口：切换出 → `CompleteTransition` 先完成 presenter 事务，再 `TryRetainContent` → `ContentWidgetWindow.TryRetainGroupContent`（`WidgetShellContentHost.cs:384-425`；`Views/ContentWidgetWindow.ContentSwitching.cs:111-135`）。retain 前调用 `OnWindowVisibilityChanged(false)` 与 `OnWindowLongHidden()`（`:130`）。
- 门槛：仅 `IWidgetGroupContentCacheable`（`Contracts/IWidgetContent.cs:76`）。实现者三个：**Todo（adapter）、File（FileSurfaceContent 自身）、QuickCapture（自身）**；Search/Weather/Glance/Music 不缓存，切换出即 Dispose（`WidgetShellContentHost.cs:406-417` `DisposeContentOnce`）。
- 容量：**条数制**，Small/Balanced→1、Large→2，默认 ResourceSaver+Small=1（`Services/PerformanceSettingsPolicy.cs:131-140`；`PerformanceSettingsPolicyTests.cs:187-199` 钉死）。LRU 按插入序，逐出即整体 Dispose（`ContentSwitching.cs:145-157`）。
- 复用：`TakeCachedGroupContent` 命中则**完全跳过初始化**——不重启 watcher、不重扫目录（`WidgetShellContentHost.cs:121-128` 走 `IsReadyForReuse`/`PrepareForReuse`），陈旧度由 30s 磁盘对账兜底（`FileSurfaceContent.xaml.cs:587-639`；`Services/FileSurfaceRefreshPolicy.cs:5-16`）。

### 1.3 生命周期语义现状与缺口

**核心缺口：成员 inactive ≠ 窗口 hidden，两者清理路径完全不同。**

- 可见组窗口内：缓存成员的完整树**永远不会被任何清理路径释放**。`ReleaseLongHiddenContentResources` 有 `if (Visible || IsClosing) return`（`ContentSwitching.cs:179`）；后台清理仅在**全部 widget 隐藏**时武装（`App.xaml.cs:4013-4056` `CanArmBackgroundMemoryCleanup`）；可见空闲维护只裁进程级缓存到半容量、不碰成员树（`App.xaml.cs:3127-3267`）。
- `OnWindowLongHidden()` 全仓库**只有一个实现**（Todo，只退订一个 `CompositionTarget.Rendering` 处理器，`TodoWidgetContentAdapter.cs:117-123`；`TodoWidgetContent.xaml.cs:408-424`）。其余全是空默认（`IWidgetContent.cs:43`）。
- **File 被契约测试明令禁止实现 `OnWindowLongHidden`**：`tests/DeskBox.Tests/WidgetVisualActivityContractTests.cs:351` `Assert.DoesNotContain("OnWindowLongHidden()", fileSurface)`。1.4.6 立的法（"long-hidden 不得弃视图"）与"成员级冻结"是两个正交生命周期轴，新契约必须用新方法名，不能复用 `OnWindowLongHidden`。
- 已存在的运行时挂起 seam：切换出时 `OnWindowVisibilityChanged(false)` → `FileSurfaceContent` 调 `ViewModel.SuspendBackgroundActivity()`，**watcher 保活、事件合并**（`FileSurfaceContent.xaml.cs:563-585`；`ViewModels/WidgetViewModel.cs:459-470` 注释原文）。→ 现有缓存成员实际上已处于"树活+运行时挂起"状态。

### 1.4 File 的所有权链（为什么缓存长这样）

- `FileSurfaceContent` 构造时自建 `WidgetViewModel`，无 manager 级存活（`Services/WidgetManager.SurfaceContent.cs:46-55`；`FileSurfaceContent.xaml.cs:206-212`）。
- Dispose 连带 `ViewModel.Dispose()` → watcher 释放、文件夹投影（`Items`）消失（`FileSurfaceContent.xaml.cs:5249-5291`；`WidgetViewModel.cs:429-457`）→ **重激活 = 全量重扫 + 重建 watcher**。这就是 1.3.6 引入整内容缓存的原因（CHANGELOG 1.3.6："避免反复销毁重建 file grids、watchers、icon work"）。
- 结论：**File 冷档（放树保运行时）的真正前置是 VM 所有权迁移**，让投影/watcher 与视图解耦存活。

### 1.5 既有基础设施盘点（新方案能直接借用的）

| 设施 | 事实 | 对新方案的用途 |
|---|---|---|
| IconHelper 三缓存（icon 字节 200 条/32MB、解码位图 160 条/48MB、缩略图 128 条/32MB 基线，Small=50%/Large=150% 缩放，LRU+字节双预算） | `Helpers/IconHelper.cs:15-24,1013-1054` | 图片已是进程级预算制——**组成员共享**，不需要按成员重复管理 |
| per-path / per-scope 清理原语 | `IconHelper.cs:691`（ClearIconCache(path)）、`:1060`（ClearCacheScope） | 若将来要按成员清图，原语已存在 |
| MemorySample（30s 节拍，DESKBOX_PERF_LOG=1）：privateMB/gcHeapMB/thumbCacheMB/decodedBitmapMB/**cachedGroupContents** 等 | `Services/PerformanceLogger.cs:290-421`；`AppDiagnosticsService.cs:93-111` | P0 测量直接用 |
| 强制 GC 器（两次 max-gen + WaitForPendingFinalizers，120s 冷却） | `Services/MemoryReclaimer.cs:46,90-103` | P0 归因协议用；注意契约测试禁止 GC.Collect 出现在 App.xaml.cs（`WidgetVisualActivityContractTests.cs:35-80`），必须走 MemoryReclaimer |
| transient state 字典（按 widget id，跨销毁存活，回滚清理） | `WidgetManager.Groups.cs:32,2418-2462` | Cold 档状态管道已有一半 |
| generation/epoch 守卫（File `_itemHydrationGeneration`、QC `_detailImageLoadVersion`、host `_contentVersion` 等全仓库成体系） | `WidgetViewModel.ItemHydration.cs:305+` 等 | 树重建后的 stale-callback 防护模式现成 |
| 性能设置 UI（CacheBudget 下拉已存在） | `Views/SettingsWindow.xaml:2390-2401` | 字节预算挂同一设置，不加新 UI |
| 切换延迟测量 | **不存在**（切换文件零 PerformanceLogger 引用；`[WidgetGroup] Switched` 日志不带 ms） | P0 需新建；插桩点：切换入口 `Groups.cs:925/1076`、prepare 后 `:1107`、首帧后 `:1155`、settle `:1252` |
| AOT 内存曲线采样器与协议 | `scripts/measure-aot-memory-curve.ps1` / `summarize-aot-memory-curve.ps1` / `aot-memory-curve-protocol.md`；今日数据 `deskbox-aot-curve-samples.csv`（baseline 128MB → batch4 plateau 490MB，**单 File 格子无组**） | P0 全应用归因直接复用采样器；该曲线是"组成员树不是大头"的第一手证据 |
| transient state 现状覆盖面 | Todo：草稿三项（`TodoWidgetContentAdapter.cs:175-181`）；File：选中路径+剪切路径（`FileSurfaceContent.xaml.cs:535-544`）；QC：输入/搜索/视图/焦点/详情 id。**均不含滚动位置**；File 不含子目录导航栈、展开堆叠 | Warm 档不需要（树没丢）；**Cold 档硬前置**，见 §4.7 |
| tab hover 基础设施 | `WidgetGroupTitleSwitcher.Tabs.cs:222-230`（`PointerEntered` → `BeginTabHoverSwitch`，已有 200ms 拖拽 dwell 计时器） | Cold 成员意图预热的挂点，见 §4.8 |
| adapter 型 kind 的结构共性 | Todo/Search/Weather/Glance/Music 五个 adapter 各约 150 行，`_view is XContent c ? c.Foo() : noop` 转发占 80%；File/QC 是"控件即内容"另一种模式 | 基类/共享 helper 的共性已充分暴露，不必再等三个试点（修订 R8） |

---

## 2. 候选方案评估

### A. 维持现状（条数缓存，永续 Warm）
- 优点：零改动；容量已收紧到 1-2。
- 缺点：可见组内非活动树永驻；条数预算不分 Todo 和大目录 File 的重量差；无内存压力响应（全应用无系统内存压力监听，对码确认）。
- 结论：**否**。但有价值的发现——现状已约等于业界 Warm 档语义，问题只在"没有下限（Cold）也没有触发器"。

### B. 方案2 原版（直接 View Eviction：retain content + release View）
- 优点：方向与全行业共识一致；Todo 试点前提全部对码成立；`CompleteTransition` 的 retain seam 安全性对码确认（presenter 提交在前，回滚发生在 retain 之前，天然不冲突）。
- 缺点（最终对码轮新发现）：① 把现有缓存当"要被替换的东西"，实际它就是 Warm 档，应保留并补触发器；② File/QC `View => this` 不适用 lazy 置空契约；③ 未识别 File VM 所有权问题；④ 预设验收（P95<150ms）没有测量设施（不存在切换延迟指标）。
- 结论：**吸收为主体（Cold 档），但按本文档修订。**

### C. 一种内容模式 + 三档状态（**选定**；第三轮稿名"两级缓存 + 三档状态"，§10 修订）
- 描述见 §4。adapter 为驻留单元；Warm=现状语义+分钟级 TTL/压力触发；Cold=`adapter.ReleaseView()`；树按条数+时间逐出，字节预算只留图片缓存。
- 优点：改动面最小（现有缓存字典、切换事务、transient 管道全部保留）；与 VS Code / ViewPager2 / SnowDesktop / Edge 的成熟形态同构；每个 PR 独立可回滚；统一模式本身是拓展性投资，与内存收益解耦。
- 结论：**选定**，投入规模由 P0 归因决定（§0-0）。

### D. 推翻组模型 → 每成员一窗口
- 证据反对：WinUI 3 每窗口带 GDI 重定向位面，实测 1080p **7.91MB/窗**、4K ~32MB，分配在 dwm 侧且 profiling 看不见（[microsoft-ui-xaml#11523](https://github.com/microsoft/microsoft-ui-xaml/issues/11523)，开放）；连开 50 子窗全关内存不回落（[#9063](https://github.com/microsoft/microsoft-ui-xaml/issues/9063)，开放）；窗口事件 GIT 泄漏修复 [PR#11734](https://github.com/microsoft/microsoft-ui-xaml/pull/11734) 2026-09-04 才合入、**至今无已发布 WASDK 包含**；微软 Photos 团队 2024 年从多进程 IPC 退回单进程（[官方博客](https://blogs.windows.com/windowsdeveloper/2024/06/03/microsoft-photos-migrating-from-uwp-to-windows-app-sdk/)）。
- 结论：**否**。

### E. 单棵共享树 + DataContext/DataTemplate 交换
- 证据反对：Avalonia TabControl 就是这个模型（单 ContentPresenter、模板只实例化一次、切 tab 换 DataContext），被官方标记为 **bug**（状态跨 tab 串，[AvaloniaUI#14529](https://github.com/AvaloniaUI/Avalonia/issues/14529)，开放）；仅当成员 UI 完全同构且无本地控件状态时才安全——格子成员各有滚动/选中/草稿，必串。
- 结论：**否**。

### F. 声明式重建路线（视图 = 状态的纯函数，微软 Widgets/Dev Home 形态）
- 微软 Widgets Board = provider 进程外 + Adaptive Cards JSON → 宿主渲染，Activate/Deactivate 定义"不可见即不更新"（[官方文档](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers)）；Dev Home Dashboard 离开页面即 `PinnedWidgets.Clear()` 整树拆掉、回来从状态全量重建（归档仓库源码级确认）。
- 评估：这是长期北极星（"视图可随时从状态重建、无增量隐藏状态"），方案2 §10 的"非视觉逻辑禁止读 View"审计正是它的第一步。但 DeskBox 的 File 格子（拖拽/叠放/右键原生菜单）远比 Adaptive Cards 交互重，整体声明化不现实。
- 结论：**不立项，作为设计原则吸收**。

### G. 进程隔离（重 UI/第三方进子进程）
- SnowDesktop 把设置 UI 和 shell 操作放短命子进程；Flow Launcher 托管插件进程内、Python/Node 进程外；Ferdium 每 service 独立 renderer。
- 结论：**否（现阶段）**。DeskBox 无第三方内容进程化需求；设置窗复用已解决泄漏。记档备查。

---

## 3. 外部证据汇总（方案选择的依据）

### 3.1 微软官方立场

1. [Optimize XAML loading](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-xaml-loading)（2026-03 更新）**点名本场景**："delay the creation of non-visible content such as **a secondary tab in a tab-like UI**"，推荐 `x:Load`，未加载态每元素约 600 字节。
2. [`Frame.NavigationCacheMode`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.navigation.navigationcachemode?view=windows-app-sdk-2.0) 三档语义 = 微软内置有界内容缓存原型：Disabled（默认，每次重建）/ Enabled（缓存但超 `CacheSize` 逐出）/ Required（永驻自担后果）。本方案的三档状态机与之同构。
3. Dev Home / Widgets Board：视图可随时丢弃、状态外置（§2.F）。
4. 每窗口固定成本与窗口泄漏史（§2.D）→ 窗口复用与"一组一窗"继续正确。

### 3.2 WinUI 3 平台硬事实（可行性边界）

1. **断开子树 ≠ 内存回落（设计内行为）**：GC 看不见 XAML 对象的 native 尺寸、回收扫描贵，故刻意推迟（MS 工程师原话，[#2190](https://github.com/microsoft/microsoft-ui-xaml/issues/2190)）；实测 Gallery 500MB 在内存压力下掉到 75MB（[#9044](https://github.com/microsoft/microsoft-ui-xaml/issues/9044)）。**对策：P0 归因必须走 MemoryReclaimer 强制 GC 分支；验收口径 = "非单调增长"。**
2. **同栈开放 bug 登记**（设计须规避）：[#11001](https://github.com/microsoft/microsoft-ui-xaml/issues/11001)（WASDK 1.8 + Build 26200，**与 DeskBox 完全同环境**，页面开关内存增长）；[#10981](https://github.com/microsoft/microsoft-ui-xaml/issues/10981)（**.NET 10 回归**：x:Bind + 高频 Dispatcher 泄 `ManagedObjectWrapperHolder`——DeskBox 正是 net10.0+AOT）；[#11742](https://github.com/microsoft/microsoft-ui-xaml/issues/11742)（**解码中销毁子树泄 async action**——逐出前必须等 `BitmapImage` 解码收尾或断源）；[#10488](https://github.com/microsoft-ui-xaml/issues/10488)（ItemsRepeater 滚动不释放）。
3. **Loaded/Unloaded 事件不可作生命周期开关**（乱序/丢失，[#1900](https://github.com/microsoft/microsoft-ui-xaml/issues/1900)，2020 年至今开放）→ 宿主显式调用（本方案的设计）正确。
4. WASDK ≥1.3 后窗口内子树逐出"机制上安全"（TabView/模板泄漏已修，[#3597](https://github.com/microsoft-ui-xaml/issues/3597)），但非承诺——需终结器打点 + 强制 GC 的"切换 N 次不增长"自动化验证。XAML 树重建耗时**无公开 ms 基准**，只能 P0 自测。

### 3.3 跨框架共识与参数（可直接抄的数字）

| 系统 | 机制 | 可抄的点 |
|---|---|---|
| Android ViewPager2 `FragmentStateAdapter`（[源码](https://android.googlesource.com/platform/frameworks/support/+/androidx-main/viewpager2/viewpager2/src/main/java/androidx/viewpager2/adapter/FragmentStateAdapter.java)） | 实例+视图一起毁、只留 SavedState | **`GRACE_WINDOW_TIME_MS = 10_000` 宽限窗**；非当前页生命周期压级 |
| VS Code（[modelService 源码](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/services/modelService.ts)） | 模型引用计数，与编辑器控件分离 | **已关闭模型 undo 缓存 20MB 字节上限** + 按时间戳逐出最旧 + **恢复时 SHA1 校验** |
| Edge（[sleep before discard](https://blogs.windows.com/msedgedev/2022/12/06/sleeping-tabs-edge-105-sleep-before-discarding)） | 先冻结后丢弃 | 冻结/丢弃**分级**而非二值 |
| Vue [KeepAlive](https://vuejs.org/guide/built-ins/keep-alive.html) | 实例缓存 + `max` LRU | 实例级缓存**必须设上界+LRU**是生态共识 |
| React 19.2 [`<Activity>`](https://react.dev/reference/react/Activity) | 保留 DOM、销毁 Effects | "拆副作用而非拆树"的对照路线（Web 上 DOM 便宜） |
| iOS viewDidUnload（iOS 6 废除） | 废除**系统自动**卸视图 | 教训：驱逐时机必须宿主可控、可预测 |
| Chrome Memory Saver | 杀渲染进程、留会话状态 | "仅冷状态"极端端点的参照 |

**共识判定**：①状态/运行时比视图活得久——无例外；②非活动视图要驱逐且驱逐必须有界——除 iOS（被动）和 React（保 DOM 毁 Effects）外全部；③两次历史反转都不是反回"永久保留"。

### 3.4 同类产品

| 产品 | 模型 | 教训 |
|---|---|---|
| Files（WinUI 3 标签页） | 全部 tab 保活、关闭才销毁（每 tab 自持 Frame） | **反面教材**：每 tab 10-15MB、关 tab 不回落的投诉从 2020 延续至今（[#2065](https://github.com/files-community/Files/issues/2065)/[#18567](https://github.com/files-community/Files/issues/18567)） |
| Rubick（uTools 开源克隆） | 主窗口 + 插件视图切换即销毁 | 曾有 maxLen=4 LRU 视图池，**后来注释掉退回纯销毁**——小缓存是可选优化 |
| SnowDesktop（[性能文档](https://github.com/FreeFallingSnow/SnowDesktop)） | 隐藏组件：10s 宽限 + **16MiB 字节预算** + 按隐藏时间 LRU；只回收贵的（像素表面），保留便宜的（所有权/滚动/脚本运行时） | 与本方案最接近的成熟形态 |
| Wavebox / Ferdium | 闲置 N 分钟**或内存压力**双触发休眠 | **keep-awake 白名单**（音乐/通知类不许睡）是十年迭代的必要配套 |
| Rainmeter | 每 skin 一窗口全常活 | 前提是每窗口极廉价（原生绘制）——WinUI 组件达不到 |

---

## 4. 选定方案：一种内容模式 + 三档成员驻留

### 4.0 北极星：一种内容模式

```
IWidgetContent（adapter，驻留单元）
  ├─ ViewModel / 运行时（watcher、订阅、投影）—— 生命 = adapter 生命
  ├─ _view：可丢弃叶子（ReleaseView / MaterializeView）
  └─ 可选契约（Responsive / Resize / Viewport / Feedback / TransientState / ...）
     在共享基类里按 `_view is T` 转发一次；view-scoped 调用在 Cold 时天然 no-op，
     VM-scoped 调用（ApplyAppearance / Refresh / Capture）不经 View
```

- 五个现有 adapter 迁到基类是机械改动；File/QC 从"控件即内容"改为"adapter + 控件叶子"是这条线最大的一刀，但**无论 P0 归因结果如何都值得做**：它同时是拓展性（新 kind = VM + View + 几十行子类，residency 自动获得）与兼容性（所有 kind 走同一套生命周期转发）的基础。
- adapter 化之后，"两级缓存"退化为**一个字典 + 每条目的状态位**（`_view` 是否为 null）；`_cachedGroupContents` 持有 adapter 即持有 VM，不需要 manager 级 VM 注册表。

### 4.1 状态机

```
切换出（CompleteTransition 内，现有 seam 不变）
   │
   ▼
Warm：树保留（= 现有缓存语义）
   │   运行时挂起（watcher 合并、水合停止 —— SuspendBackgroundActivity 已做）
   │
   ├─ TTL 到期（默认分钟级 3~5 min；回切宽限 10s 内命中则零成本）
   ├─ Private Bytes 超阈值（复用现有 240/260MB 阈值思路，MemoryCleanupPolicy）
   ├─ 窗口整体隐藏（现有 ReleaseLongHiddenContentResources 路径，可从 Dispose 改为 ReleaseView）
   │   —— 以上三条均经 IsVisibleIdleCandidate 门控，用户交互中不降级
   ▼
Cold：adapter.ReleaseView()（树释放，VM/运行时随 adapter 保留）
   │   - 统一为 _view = null；view-scoped 订阅/异步在 ReleaseView 内取消解绑
   │   - 解码中的 BitmapImage 先断 Source（R3）
   │
   ├─ 意图预热：tab hover / Ctrl+Tab 相邻成员 → 后台 MaterializeView（§4.8）
   ▼
回切：MaterializeView + RestoreTransientState（含滚动/导航栈，§4.7）+ 30s 磁盘对账兜底（File 已有）
```

- 驱动方式：**宿主显式调用**，事件（Unloaded）只作辅助（§3.2-3）。
- 与旧立法的关系：`OnWindowLongHidden` 轴（窗口级、禁止弃视图）**原样保留**；新增成员驻留轴用新契约名（`IWidgetViewResidency`：`ReleaseView()` / `MaterializeView()` / `IsViewMaterialized`），不动旧测试所钉行为（`WidgetVisualActivityContractTests.cs:327-381` 无需推翻，只需新增）。
- **TTL 为何不能是 30-60s**：桌面小组件的使用是间歇式（看一眼 Todo → 切去文件格子干活 → 几分钟后切回）。秒级 TTL 让绝大多数回切命中 Cold → 整树重建 → 150ms loading 指示器，正是 1.3.6 缓存要消灭的体验。激进回收只由内存压力与窗口隐藏两条触发器驱动。

### 4.2 各档语义

| 资源 | Active | Warm | Cold |
|---|---|---|---|
| WidgetConfig/持久数据 | 保留 | 保留 | 保留 |
| transient state | 在视图/VM | 保留 | **捕获进 `_widgetGroupTransientStates`（现有字典）** |
| ViewModel/运行时 | 活跃 | 挂起（watcher 保活合并） | 保留（随 adapter 存活；File 需 adapter 化后达成） |
| XAML 树 | 唯一完整 | 保留（≤预算条数） | **释放** |
| 解码图/缩略图 | 共享预算缓存 | 共享预算缓存（现有全局 LRU 照常裁剪） | 同左（无需按成员管理） |
| View-scoped 订阅/异步 | 活 | 停（generation 守卫已有） | 取消+解绑（ReleaseView 责任） |

### 4.3 预算与逐出

- **树不做字节估算。** WinUI 3 上"元素计数×系数"没有可靠系数（native 侧尺寸 GC 看不见，§3.2-1），估错预算失效、估对也是巧合。Warm 树用**条数 + 时间**（time-LRU by last-active）；字节预算只保留在已有的图片共享缓存上（`IconHelper` 三缓存，不动）。
- `PerformanceCacheBudget`（Small/Balanced/Large，设置 UI 已存在）继续作为总开关：Small→warm 1 + 较短分钟级 TTL；Large→warm 2 + 较长 TTL。**warm=0 不作为任何档的默认**（默认 ResourceSaver+Small 今天即 1；降到 0 = 回到 1.3.6 之前）。条数语义测试（`PerformanceSettingsPolicyTests.cs:187-199`）随立法步同步修订为"条数 + TTL"。
- Cold 条目不占 warm 条数；Cold 条目本身按 last-active 时间 LRU，上限 = 组成员上限 8（VM 很轻，主要是防泄漏边界）。
- **四种非缓存 kind（Search/Weather/Glance/Music）的"Cold 档"是 UX 项，不是内存项。** 它们今天切换出全 Dispose（含 VM，如 `MusicWidgetContentAdapter.cs:148-168`）；给它们"保留 VM、释放视图"比今天**保留得更多**，价值是回切即显（不重拉天气、不重连 GSMTC）。单独作为可选 UX 评估（§6 P4），Music keep-awake 白名单等有用户反馈再做。

### 4.4 与现有代码的集成点（全部对码确认）

| Seam | 位置 | 动作 |
|---|---|---|
| 降级触发点 | `ContentWidgetWindow.ContentSwitching.cs:111-135`（TryRetainGroupContent） | retain 时记录 last-active 时间戳；新增 TTL 检查入口；`ReleaseLongHiddenContentResources`（`:177-188`）从 Dispose 改为 ReleaseView |
| 释放安全点 | `WidgetShellContentHost.cs:384-425`（CompleteTransition：presenter 提交→retain） | 回滚发生在 retain 之前，天然安全（已对码） |
| Cold 重建 | `TakeCachedGroupContent`（`ContentSwitching.cs:97-109`）命中 Cold adapter → `ContentWidgetWindowFactory.cs:59-89` 直接复用该 adapter → `WidgetShellContentHost.cs:121-128` 走 `IsReadyForReuse`/`PrepareForReuse` | adapter 化后 Cold 与 Warm 复用**同一条**路径，只多一步 `MaterializeView`；`PreviewWidgetGroupTransientState`（`Groups.cs:1089-1091`）现有预恢复管道原样使用 |
| 意图预热 | `WidgetGroupTitleSwitcher.Tabs.cs:222-230`（tab `PointerEntered`） | hover 到 Cold 成员 tab → 请求宿主 `MaterializeView`（低优先级 dispatcher 任务，可被切换请求抢占）；Ctrl+Tab 预热相邻成员 |
| 首帧等待 | `Groups.cs:1149-1155`（900ms） | Cold 重建走同一路径，超时回滚语义不变 |
| 冻结 seam | `OnWindowVisibilityChanged(false)` → `SuspendBackgroundActivity`（已调用） | Warm 档运行时挂起**已存在**，无需新做 |
| 图片 | 共享预算缓存（`IconHelper.cs:1013-1054`） | 不动；仅登记 §3.2-2 的解码窗口风险 |
| 强制 GC | `MemoryReclaimer.cs`（现有） | P0 与压力触发用；禁止进 App.xaml.cs（契约测试钉死） |

### 4.5 各 widget 适配表

| Widget | 现状 | P1 统一模式 | Cold 档额外改动 | 风险 |
|---|---|---|---|---|
| Todo | adapter + lazy view + transient（`TodoWidgetContentAdapter.cs:64-79,175-193`） | 迁基类（机械） | ReleaseView（退订 FeedbackRequested + ReleaseTransientRenderingSubscriptions + _view=null）；修 4 处业务方法读 View（`:197/:212/:219/:228`）；transient 补滚动位置 + 详情面板状态 | 低（试点） |
| Search | 不缓存、无 lazy 风险（业务用 `_view` 字段） | 迁基类；**修事件 add/remove 读 View**（`SearchWidgetContentAdapter.cs:55/62`——冷 widget 被订阅即物化整树，最隐蔽形态；改为 adapter 自持事件、物化时转接） | 可选 UX 项 | 低 |
| Weather / Glance | 不缓存，业务用 `_view` 字段 | 迁基类；Glance 注意 `_imageLoadVersion` 守卫已有 | 可选 UX 项 | 低 |
| Music | 切换出全 Dispose | 迁基类 | 可选 UX 项（保留 VM 监控不断）；keep-awake 白名单等用户反馈 | 低 |
| QuickCapture | content 即视图（`View => this`，`:166`），VM 归 content | **adapter 化**：VM 上提到 adapter，`QuickCaptureSurfaceContent` 退为叶子控件 | ReleaseView；transient 已较完整（输入/搜索/视图/焦点/详情 id），补列表滚动位置 | 中 |
| File | content 即视图 + VM 归 content 所有（`FileSurfaceContent.xaml.cs:206-212`） | **最大一刀，但只有一刀**：adapter 化让 `WidgetViewModel` 归 adapter；`FileSurfaceContent` 退为叶子，构造时接收 VM；`SetHostWindowHandle` / `ConfirmExtensionChangeHandler` 等 host 回调改挂 adapter；`IsReadyForReuse`/`PrepareForReuse` 上提。**不做 manager 级 VM 注册表** | ReleaseView（先断 BitmapImage Source、取消 hydration generation）；transient 补滚动位置、子目录导航栈、展开堆叠 key；沿用方案2 §17 "重建禁止全量重扫"验收 | 高（单独 PR 系列；收益也最大） |

### 4.6 需要新立法的契约（先立法后迁代码，沿用 #397 方法论）

1. 新增 residency 契约测试（参照 `ModuleBoundaryContractTests.cs:23-67` 的 exact-violation-manifest 模式）：
   - settled 组 surface：完整成员树 ≤ 1；transition 期 ≤ 2；
   - `ReleaseView` 不得 Dispose runtime；`Dispose` 必须 terminal；
   - Cold 成员的非视觉操作不得隐式物化 View（allowlist：只有真 UI action 与 MaterializeView 允许）；
   - 每种实现 `IWidgetViewResidency` 的 kind 必须同时实现 `IWidgetTransientStateContent`，且通过 §4.7 往返测试。
2. 修订：`PerformanceSettingsPolicyTests.CacheBudget_ControlsInactiveGroupContentRetention`（条数→条数 + TTL 语义；**断言任何档 warm ≥ 1**）。
3. 保留不动：`WidgetVisualActivityContractTests.LongHiddenMaintenance_*`（窗口轴立法）、`WidgetShellContentHostTests` 切换事务全组、`WidgetShellContentHostAppearanceTests`（外观对 warm/cold 成员的刷新路径——Cold 成员激活时需走"首次外观"路径，`ReplacementWithSameId_StillGetsItsFirstAppearance` 已覆盖同型场景）。
4. 新增：一种内容模式契约——所有 `IWidgetContent` 实现必须派生自共享 adapter 基类（或注册在显式 allowlist：`ExistingWidgetContent` / `PlaceholderWidgetContent` 等非成员内容）；`View => this` 出现在任何 `IWidgetContent` 实现中即违规。

### 4.7 Cold 硬前置 ①：transient state 完整性

Warm 档树没丢，transient state 只需覆盖"切换事务期间会被重置的东西"（今天的覆盖面就是按这个口径长的）。Cold 档丢树，**所有只活在视图里的用户可感知状态**都要进 transient state，否则回切就是回归。每种 kind 一条往返契约测试：`Capture → ReleaseView → MaterializeView → Restore` 后与释放前等价。

| Kind | 今天已存 | Cold 必须补 |
|---|---|---|
| Todo | 草稿文本/重要/截止 | 列表滚动位置；详情面板是否打开及所选项 id；master/detail 分栏宽度（若未持久化） |
| File | 选中路径、剪切路径 | 滚动位置（按首个可见项路径记，不按像素，重建后布局可能变）；子目录导航栈（`Navigation.cs`）；展开的堆叠 key；`RenderedItems` 窗口预算（避免回切只渲染 30 项再增长） |
| QC | 输入/搜索/视图/焦点/详情 id/编辑态/草稿 | 列表滚动位置 |

滚动位置一律用**锚点项**（首个可见项的稳定 id/path）而非像素偏移，重建后调 `ScrollIntoView(anchor, Leading)`。

### 4.8 Cold 硬前置 ②：意图预热

Cold→Active 的重建延迟不能只靠"P95<150ms 否则优化重建"这条验收线兜底。用户点下 tab 之前通常有可观察意图：

- **hover 预热**：`WidgetGroupTitleSwitcher` tab `PointerEntered`（`Tabs.cs:222`）已有挂点。hover 到 Cold 成员 → 向宿主提交低优先级 `MaterializeView` 请求；`PointerExited` 不取消（物化了就留在 Warm，自然走 TTL）。
- **Ctrl+Tab 预热**：按下时预热 forward 方向相邻成员；连按时以最新目标为准。
- 预热任务必须可被真正的切换请求抢占：如果切换到达时预热尚未完成，切换直接接管同一个 adapter，不重复物化。
- P0 插桩要能区分"Cold 命中且已预热"与"Cold 命中未预热"，Go/No-Go 加一条**预热命中率**。

---

## 5. 风险登记（每条附对策）

| # | 风险 | 来源 | 对策 |
|---|---|---|---|
| R1 | 逐出后内存不回落（GC 推迟回收 XAML，设计内） | [#2190](https://github.com/microsoft/microsoft-ui-xaml/issues/2190)/[#9044](https://github.com/microsoft/microsoft-ui-xaml/issues/9044) | P0/验收走 MemoryReclaimer 强制 GC 分支；口径=非单调增长 |
| R2 | 同栈开放泄漏（#11001 同环境 / #10981 .NET10 / #10488 ItemsRepeater） | §3.2-2 | 每档落地配"切换 N 次私有字节不增"自动化测试（终结器打点+强制 GC）；升级 WASDK 时回归 |
| R3 | 解码中销毁子树泄 async action | [#11742](https://github.com/microsoft/microsoft-ui-xaml/issues/11742)（修复未发版） | ReleaseView 协议：先等 `ImageOpened/Failed` 收尾或断 Source |
| R4 | 窗口泄漏修复未发版 | [PR#11734](https://github.com/microsoft/microsoft-ui-xaml/pull/11734)（>2.5.1 才有） | 继续窗口复用；升 WASDK 2.6+ 后重评 |
| R5 | Loaded/Unloaded 乱序 | [#1900](https://github.com/microsoft/microsoft-ui-xaml/issues/1900) | 全部宿主显式调用（已定） |
| R6 | 冷切换延迟超标 | 无公开基准 | Go/No-Go：未预热 Cold 命中 P95 < 150ms（150ms=现有 loading 显示延迟 `Groups.cs:1268`，900ms=回滚线）+ 预热命中率；超标先加长 TTL / 加强预热，再优化重建，不许回退全量缓存 |
| R7 | File adapter 化引入行为回归 | §1.4 | 单独 PR 系列；对账/watcher 契约测试先行（`File_ChangesWhileCold_AppearAfterRematerialize` 等，方案2 §32 清单沿用）；host 回调（`SetHostWindowHandle`、拖拽会话、原生菜单）逐一对码迁移 |
| R8 | 为未来造框架 | 方案2 §34 教训 | 接口只留 `IWidgetViewResidency` 一个；adapter 基类允许（共性已由五个现成 adapter 暴露，§1.5），但基类只做生命周期转发，不做业务抽象 |
| R9 | Cold 回切丢用户可感知状态（滚动/导航栈/展开堆叠） | §4.7 对码：今天 File/Todo transient 均不含滚动 | 每 kind 往返契约测试先行；未通过者不得实现 `IWidgetViewResidency`（§4.6-1） |
| R10 | TTL 过短把 Cold 变成常态，回到 1.3.6 之前的体验 | §4.1 | TTL 分钟级默认；warm ≥ 1 立法；P0 记录真实回切间隔分布再定参 |
| R11 | 这条线做完，任务管理器数字不明显下降，投入被质疑 | §3.2-1 GC 推迟回收 | P0 先做全应用归因，投入规模跟着归因走（§0-0）；对外口径统一为"非单调增长 + 压力时可放" |

---

## 6. 实施路线（第四轮修订版 P 系列；每 PR 独立可测/可并/可回滚）

旧 G 系列 → 新 P 系列映射：G0→P0（扩大）、G1→P1-a、G5+G6①②→P1-b/c（去掉 VM 注册表）、G2→P2、G3+G4+G6③→P3、G7→P4。

| 阶段 | 内容 | 行为变化 | 决策门 |
|---|---|---|---|
| **P0 归因 + 不变量** | ① 切换延迟四点插桩（入口/prepare 后/首帧后/settle，用 `PerformanceLogger.Measure`），并区分 Warm 命中 / Cold 已预热 / Cold 未预热 / 全新；② per-kind materialized view count 进 MemorySample；③ **全应用内存归因实验**：复用 `measure-aot-memory-curve.ps1`，对照组为 组成员树（1/2/4/8 成员 × Todo/File/混合 × Small/Large，清缓存对照 + MemoryReclaimer 强制 GC 分支）、IconHelper 三缓存真实 native 尺寸（估算 vs Private Bytes 差值）、File 投影（2500 项 VM 不带树）、每窗口固定成本（隐藏组 vs 可见组）；④ 记录真实回切间隔分布（定 TTL 用） | 无 | **组成员树占稳态私有内存 <10% → 跳过 P3，只做 P1+P2**；≥10% → 全线 |
| **P1-a 立法** | §4.6 契约（residency + 一种内容模式 allowlist ratchet + 往返测试骨架）；修订容量语义测试（条数 + TTL，warm ≥ 1） | 无 | — |
| **P1-b 统一模式（adapter 型）** | 共享 adapter 基类；迁 Todo/Search/Weather/Glance/Music（机械）；修 Search 事件访问器（`SearchWidgetContentAdapter.cs:55/62`） | 无 | — |
| **P1-c 统一模式（content 型）** | QC adapter 化；File adapter 化（VM 归 adapter，`FileSurfaceContent` 退为叶子，host 回调迁移）。**无论 P0 结果如何都做**——这是拓展性与兼容性的基础，不是内存项 | 无（结构重排） | 通过现有 `WidgetShellContentHostTests` 全组 + File 拖拽/菜单/对账契约 |
| **P2 Warm 触发器** | count-LRU → time-LRU；分钟级 TTL；`IsVisibleIdleCandidate` 门控；Private Bytes 阈值触发；`ReleaseLongHiddenContentResources` 改走 ReleaseView（adapter 化后可用） | 逐出时机（低风险） | — |
| **P3 Cold** | `IWidgetViewResidency` 在基类实现一次；**先补 §4.7 transient state + 往返测试**；Todo 试点 → File；§4.8 预热（hover + Ctrl+Tab） | Todo/File 冷切换走重建 | **Go/No-Go**：功能/事务零回归 + 未预热 Cold P95<150ms + 预热命中率 + inactive View=0 + 强制 GC 下 Private Bytes 非单调增长 + old view 可回收（终结器打点） |
| **P4 定参 + 可选 UX** | 按 P0/P3 数据定 warm 容量/TTL；四种非缓存 kind 的"保留 VM"作为可选 UX 项逐个评估（Music keep-awake 等用户反馈）；移除 whole-view retention 假设 | 小 | — |

**明确不做**：树字节估算预算；manager 级 VM 注册表；warm=0 作为任何档默认；30-60s TTL；Music keep-awake 白名单（除非 P4 有用户反馈）。

排期：主体排在**云同步/数据分层之后**（模块边界路线图定序：App 生命线→数据三域最急）；P0 无行为变化、与任何线零冲突，可提前插队；P1-b/c 与云同步线无文件冲突，可并行。

---

## 7. 与既有结论/路线图的关系

- AOT 曲线 PLATEAU ~490MB 的增量已定罪为 native 有界缓存（缩略图/位图）——那是 cut/restore、大批量文件场景，与本方案的组成员树是**两个不同持有者**；P0 的 M(n) 曲线 + 清缓存对照正是判别器，两边不互斥。**但要注意量级**：三缓存字节预算基线 32+48+32=112MB（Large 168MB），而曲线增量 ~360MB——估算口径与真实 native 尺寸之间的差值本身就是 P0 归因要回答的问题，且很可能比组成员树大一个数量级。
- 本方案不改变模块边界路线图的依赖方向（Features→Contracts←Platform）；成员驻留契约属于 Widgets/Features 内部资源 ownership，与 §38"运行时 ownership"定位一致。P1 的"一种内容模式"是 `widget_contribution_seam.md` 所述贡献 seam 的自然收口：新 kind 的接入面 = VM + 叶子 View + adapter 子类。
- 1.4.6 的窗口轴立法（long-hidden 不弃视图）与新的成员轴正交共存，不改写历史行为承诺。

## 8. 对两份原始文档的勘误表

| 原文主张 | 对码结果 |
|---|---|
| 方向.md：缓存容量"即使比 8 小"、暗示接近成员数 | 实际 Small/Balanced=1、Large=2（`PerformanceSettingsPolicy.cs:131-140`） |
| 方向.md：性能策略释放是 1.4.9 | CHANGELOG:432，实为 **1.4.6** |
| 方向.md："统一缓存容量设置" | 仅 group cache 走 CacheBudget；icon/缩略图/位图各有独立基线+同预算缩放 |
| 方案2 §30：snapshot 不得成为 ownership | 当前切换路径根本未用 snapshot（live crossfade 用真实 outgoing view，snapshot API 全仓库无调用），该节空转 |
| 方案2 §5：lazy View getter 模型适用于试点外的推广 | File/QC `View => this`，契约对二者结构性不适用（本方案 §4.5） |
| 方案2 隐含：现有缓存要被 View Eviction 取代 | 现有缓存≈业界 Warm 档，应保留并补 Cold/触发器（本方案 §0-2） |
| 两文档均未识别 | File VM 所有权是冷档前置（§1.4）；GC 延迟回收导致测量假阴性（§3.2-1）；File 被立法禁止 OnWindowLongHidden（§1.3） |
| 本文档第三轮稿（§10 修订前） | 见 §10 |

## 9. 来源索引

**微软官方**：[Optimize XAML loading](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-xaml-loading) · [NavigationCacheMode](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.navigation.navigationcachemode?view=windows-app-sdk-2.0) · [Widget providers overview](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers) · [microsoft/devhome（归档源码）](https://github.com/microsoft/devhome) · [Photos 迁移博客](https://blogs.windows.com/windowsdeveloper/2024/06/03/microsoft-photos-migrating-from-uwp-to-windows-app-sdk/)

**WinUI 3 内存**：[xaml-object-lifetime 设计文档](https://github.com/microsoft/microsoft-ui-xaml/blob/main/docs/design-notes/xaml-object-lifetime.md) · [#2190](https://github.com/microsoft/microsoft-ui-xaml/issues/2190) · [#9044](https://github.com/microsoft/microsoft-ui-xaml/issues/9044) · [#7282](https://github.com/microsoft/microsoft-ui-xaml/issues/7282)/[PR#11734](https://github.com/microsoft/microsoft-ui-xaml/pull/11734) · [#11523](https://github.com/microsoft/microsoft-ui-xaml/issues/11523) · [#9063](https://github.com/microsoft/microsoft-ui-xaml/issues/9063) · [#11001](https://github.com/microsoft/microsoft-ui-xaml/issues/11001) · [#10981](https://github.com/microsoft/microsoft-ui-xaml/issues/10981) · [#11742](https://github.com/microsoft/microsoft-ui-xaml/issues/11742) · [#10488](https://github.com/microsoft/microsoft-ui-xaml/issues/10488) · [#1900](https://github.com/microsoft/microsoft-ui-xaml/issues/1900) · [#3597](https://github.com/microsoft/microsoft-ui-xaml/issues/3597) · [Win2D RefCycles](https://microsoft.github.io/Win2D/WinUI3/html/RefCycles.htm) · [Optimize animations/media/images](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-animations-and-media)

**跨框架**：[FragmentStateAdapter 源码](https://android.googlesource.com/platform/frameworks/support/+/androidx-main/viewpager2/viewpager2/src/main/java/androidx/viewpager2/adapter/FragmentStateAdapter.java) · [VS Code modelService 源码](https://github.com/microsoft/vscode/blob/main/src/vs/editor/common/services/modelService.ts) · [Edge sleep-before-discard](https://blogs.windows.com/msedgedev/2022/12/06/sleeping-tabs-edge-105-sleep-before-discarding) · [Vue KeepAlive](https://vuejs.org/guide/built-ins/keep-alive.html) · [React Activity](https://react.dev/reference/react/Activity) · [viewDidUnload 废除](https://developer.apple.com/documentation/uikit/uiviewcontroller/viewdidunload) · [Avalonia#14529](https://github.com/AvaloniaUI/Avalonia/issues/14529) · [Qt Loader](https://doc.qt.io/qt-6/qml-qtquick-loader.html) · [Chrome tab discarding](https://developer.chrome.com/blog/tab-discarding)

**同类产品**：[Files #2065](https://github.com/files-community/Files/issues/2065)/[#18567](https://github.com/files-community/Files/issues/18567) · [Rainmeter 源码](https://github.com/rainmeter/rainmeter) · [SnowDesktop 性能文档](https://github.com/FreeFallingSnow/SnowDesktop) · [Rubick 源码](https://github.com/rubickCenter/rubick) · [Wavebox Sleep](https://hub.wavebox.io/sleep-performance) · [Ferdium 源码](https://github.com/ferdium/ferdium-app)

---

## 10. 第四轮对码修订记录（2026-09-18）

第三轮稿的事实层（§1 全部 file:line、§2-3 外部证据）复核无误，本轮不改事实，只改**方案取舍**。触发本轮的三个新输入：① 同日 AOT 曲线数据（单 File 格子无组，128→490MB）；② 对 transient state 覆盖面与 tab hover 基础设施的补充对码；③ 对五个 adapter 结构共性的横向对码。

| # | 第三轮稿主张 | 本轮修订 | 依据 |
|---|---|---|---|
| 1 | 决策门"若 G0 显示只贡献个位数 MB，整条线停在 G0"放在 §6 排期末尾 | 提到 §0-0 作为第一条结论；P0 扩大为全应用内存归因；<10% 则跳过 Cold | AOT 曲线量级 vs 组成员树量级（§0-0、§7） |
| 2 | 两级缓存（Warm 字典 + Cold 状态/VM 由 manager 持有） | 一个字典 + 每条目状态位；adapter 即驻留单元 | adapter 化后 `_cachedGroupContents` 持有 adapter = 持有 VM（§4.0） |
| 3 | File 冷档前置 = manager 级 VM 注册表（G6①）→ adapter 化（G6②）→ Cold（G6③） | 去掉 VM 注册表；只做 adapter 化（P1-c）→ Cold（P3） | 少一层所有权转移、少一个与 `_contentWidgets`/`_fileWidgets` 对齐的字典（§4.5 File 行） |
| 4 | TTL 默认 ~30-60s | 分钟级 3~5 min；秒级回收只由内存压力/窗口隐藏触发 | 间歇式使用模式；秒级 TTL = 1.3.6 之前体验（§4.1） |
| 5 | Small 档 warm 容量 0-1 | warm ≥ 1 立法；warm=0 不作为任何档默认 | 默认 ResourceSaver+Small 今天即 1（`PerformanceSettingsPolicy.cs:71,136`）（§4.3） |
| 6 | warm+cold 合计按字节估算逐出（树按元素计数×系数） | 树不做字节估算，用条数 + 时间；字节预算只留图片缓存 | WinUI 3 native 尺寸不可估（§3.2-1）（§4.3） |
| 7 | 未识别 | **Cold 硬前置 ①**：transient state 完整性（滚动/导航栈/展开堆叠）+ 每 kind 往返契约测试 | `TodoWidgetContentAdapter.cs:175-181`、`FileSurfaceContent.xaml.cs:535-544` 均不含滚动（§4.7、R9） |
| 8 | 未识别 | **Cold 硬前置 ②**：意图预热（tab hover / Ctrl+Tab 相邻） | `WidgetGroupTitleSwitcher.Tabs.cs:222-230` 已有挂点（§4.8） |
| 9 | Search/Weather/Glance/Music Cold 列为 G4 内存路线 | 改为可选 UX 项（P4）；Music keep-awake 等用户反馈 | 四种今天全 Dispose，"保留 VM"比今天保留得更多（§4.3） |
| 10 | R8：三个 widget 验证共性前不加层 | 允许 adapter 基类，仅做生命周期转发 | 五个 adapter 各约 150 行、80% 同构转发（§1.5） |
| 11 | 收益口径未对外说明 | R11：稳态数字可能不明显下降，口径 = 非单调增长 + 压力时可放 | §3.2-1 同一事实的另一面 |
| 12 | G 系列 8 步 | P 系列 5 阶段 + 明确不做清单；P1-c 与归因结果解耦 | §6 |

**对 Simon 三个目标的对应**：降内存 → P0 归因决定投入、P2/P3 是手段；不卡顿 → TTL 分钟级 + warm ≥ 1 + 预热 + 状态完整性四条护栏；拓展性与格子兼容性 → P1 一种内容模式（与内存收益无关也做）。
