# DeskBox 内存优化长期方案

> 版本：2026-08-27 v2（含扩展复审增补，见 §8）· 基于 main @ v1.4.5-rc1 的全量代码勘察
> 性质：分析与方案文档，未含任何代码改动。所有结论均附 `文件:行号` 可回查。
> 注：勘察后 main 已有新提交（25a45eb 恢复弹窗材质系统等），行号可能小幅漂移，结论与方案不变；§8.1 中"被推迟的簇"现仅剩显示拓扑快照簇。
>
> **状态快照（2026-09-06）**：勘察基线为 v1.4.5-rc1，此后已随 1.4.9 落地的部分——设置窗口复用修复（开关设置反复开关 +10MB/次的泄漏）、空闲深度回收冷却后不重试的调度修复、空闲工作集修剪（UI 开关默认开启）、Direct 通道 Native AOT 化完成。材质切换的跨族增长已定性为平台层行为，不随应用层回收。内存评估以 Release/Native AOT 基线为准（稳态私有约 115MB），Debug 构建仅作诊断用。未列入本段的工作项仍处于方案状态，动工前需按当前代码重新勘察。

## 0. 背景与目标

用户报告的典型累积场景（例举，非穷尽）：

| # | 场景 | 表象 |
|---|---|---|
| S1 | 格子组连续滚动切换 | 内存持续上升 |
| S2 | 胶囊重复展开/收起 | 内存持续上升 |
| S3 | 叠放（stack）文件夹模式展开/收起 | 内存持续上升 |
| S4 | 频繁切换材质/语言/主题色 | 内存持续上升 |

共同特征：**若没有超时释放命中，内存只升不降**。本文档回答三个问题：
为什么升（链路）、为什么降不下来（释放断点）、怎么长期治（方案）。

目标验收形态（详见 §5）：
- 每场景 20 次循环后 Private 净增量收敛到阈值内；
- 停止交互空闲 60 秒后内存可测回落；
- 8 小时长稳不出现持续爬坡；
- 有自动化回归门禁防退化。

---

## 1. 内存架构现状全景

### 1.1 资源持有者五层模型

| 层 | 生命周期 | 代表资源 | 位置 |
|---|---|---|---|
| L1 进程级 | 进程全程 | 静态缓存：图标三缓存、shellKind、shortcut 元数据、GlanceWidgetStore、语言字典×12、FileMetaService | IconHelper.cs:48-83、FileService.cs:61、ShortcutHelper.cs:19、GlanceWidgetStore.cs:27、LocalizationService.cs:299 |
| L2 会话级 | widget/窗口存活期 | content 实例+XAML 整树、ViewModel、组内容缓存（容量1-2）、surface 切换 gate | ContentWidgetWindow.ContentSwitching.cs:38-40、WidgetSurfaceSwitchGatePool.cs:10 |
| L3 交互级 | 一次交互窗口 | 叠放弹窗 30s 缓存树、胶囊动画注册、组切换请求、backdrop controller | StackPopover.cs:22、WidgetCompactAnimationCoordinator.cs、WidgetWindowBase.Backdrop.cs:320 |
| L4 瞬态 | 单次操作 | CTS、Storyboard、Descriptor/Plan、每次 new 的 brush/UISettings | 各切换/动画路径 |
| L5 原生级 | 到 Stop/Dispose 为止 | compositor KeyFrame 动画、WinRT RCW（UISettings/AccessibilitySettings）、acrylic/mica controller | StackAnimations.cs:273-303 等 |

### 1.2 现有清理调度五条路径与"真实释放内容"

| 路径 | 触发 | 实际释放 | 空转成分 |
|---|---|---|---|
| 可见空闲维护 | 5s 轮询 + 连续 idle（Balanced 10min）+ cooldown | Localized 剪枝、FileMetaService.Clear、IconHelper 缓存减半 | GC 有门槛（堆≥96MB 等，MemoryCleanupPolicy.cs:71-103）；trim 需 WS≥240 且 Private≥260 |
| 后台 soft（全隐藏 30s） | 隐藏/设置关闭布防，代际取消 | 同上三项但图标缓存**全清** + 阻塞 GC | 无 trim |
| 后台 deep（隐藏 5min） | soft 之后 | `ReleaseLongHiddenWidgetResources` → 组缓存全 Dispose | **对 5/7 内容类型零释放**（见 R2） |
| 轻清理（2s 防抖） | 设置/引导/搜索关闭 | 仅当 heavy 条件成立才压缩 GC + LOH + HeapSetInformation | **无 heavy 条件时零释放** |
| 隐藏工作集裁剪 | 仅 ResourceSaver/Custom | 无托管引用释放 | **Balanced（默认）档为 CleanupNever，永不排程** |

调度代码集中在 `App.xaml.cs:2597-3428`；策略纯函数在 `MemoryCleanupPolicy.cs`、`PerformanceSettingsPolicy.cs`。

### 1.3 设计良好、可直接当模板的现有实现

| 模板 | 位置 | 模式 |
|---|---|---|
| FileItemSurfaceStyleCache | FileItemSurfaceStyleCache.cs:23-125 | 9 个 brush 实例常驻，只改 Color 不换实例，热路径零分配 |
| GetOrUpdateSolidColorBrush | WidgetWindowBase.Backdrop.cs:256-278 | 按颜色查表复用 |
| AccentResourceScope | AccentResourceScope.cs:13-41 | 5 个 brush 复用仅改色 |
| _edgeGlowPulseAnimation 字段缓存 | WidgetShell.xaml.cs:3047-3073 | compositor 动画对象字段级复用 |
| 叠放弹窗 30s 缓存 + 整树释放 | StackPopover.cs:1582-1846 | 交互级缓存的完整生命周期范本 |
| InactiveBackdropControllerRetention | WidgetWindowBase.Backdrop.cs:320-386 | 非活动 controller 定时释放 |
| IconHelper 三缓存 | IconHelper.cs:763-851 | LRU + 字节/条数双上限 + 档位预算 + idle 逐出 |

---

## 2. 四大场景逐链路剖析

### S1 格子组连续滚动

调用链：`WidgetWindowBase.Grouping.cs:343` → `WidgetManager.Groups.cs:1076-1261`。

**每次切换的创建清单：**
1. `ContentWidgetWindowFactory` + `WidgetContentFactory`（含 7 个 provider + 字典）**每次重建** —— Groups.cs:1037-1038。纯 churn，无状态可复用。
2. 缓存未命中时按成员类型创建整棵内容：QuickCapture = XAML 树+VM；Todo = Store+VM+Adapter；Glance/Music/Weather/Search = **全新 VM（Music 还 new MusicSessionService）**。
3. 切换动画 Storyboard + ~8 DoubleAnimation + 2 CompositeTransform（WidgetShell.xaml.cs:1225-1373，完成时释放，配对良好）。
4. 请求级 CTS、Descriptor、Plan 等短命对象。

**旧内容的去向（断点所在）：**
- `CompleteTransition`（WidgetShellContentHost.cs:369-390）→ 可缓存者进 `_cachedGroupContents`，**容量 Small=1 / Balanced=1 / Large=2**（PerformanceSettingsPolicy.cs:111-120），FIFO 逐出并 Dispose；
- **Glance/Music/Weather/Search 未实现 `IWidgetGroupContentCacheable` → 不可缓存 → 直接 Dispose 整树**。
- 连续滚动 3+ 成员时：非缓存成员反复"整树创建→销毁"，XAML 树的创建涉及大量 native 分配，GC 回收滞后于创建速率 → **工作集阶梯上升**；可见态 GC 门槛（堆≥96MB 且分配增量≥32MB）在此之前不触发 → 用户观察到"越滚越高"。
- 窗口隐藏后的 deep 清理会 Dispose 组缓存——但需要"全部隐藏 + 5 分钟"。

**结论：无泄漏，是"高分配速率 × 高释放门槛"的净累积。**

### S2 胶囊重复展开/收起

入口 `SetCollapsedState`（WidgetWindowBase.Collapse.cs:2577-2785）。

**每循环分配：** WidgetCompactAnimationFrameTracker、watchdog 一次性 timer（OwnedOneShotDispatcherTimer，Dispose 时解绑 Tick——Collapse.cs:4009-4054 有明确防钉死注释）、6-9 个 KeyFrame 动画（每个 13-25 帧，WidgetShell.xaml.cs:1960-2000）、warmup CTS、视觉树预排版 Queue。

**释放配对：** 协调器注册 Dispose、CompositionTarget.Rendering 退订、9 个元素 StopAnimation——全部平衡（WidgetCompactAnimationCoordinator.cs:277-304）。

**结论：同 S1，配对完好但分配密集；收起态保留整棵 Shell XAML 树与内容（有意设计）；与 R3 叠加时（隐藏后）无 trim 兜底。**

### S3 叠放展开/收起

- 投影重建：`RebuildStackDisplayItems`（WidgetViewModel.Stacks.cs:1266-1345）每次展开/收起**全量重算**分组+排序+投影（多个临时 List/HashSet/Dictionary），每 stack 发 **16 个 PropertyChanged**（WidgetStackItem.cs:162-179）触发绑定重评估。
- WidgetStackItem 本身按 key 复用（不新建）；stale key 有清理（:1333-1342）。
- 动画：`StartStackElementAnimation`（StackAnimations.cs:251-309）**每成员每方向新建 4 个 compositor 对象**（1 easing + 3 keyframe），不复用；成员越多分配越大。
- 弹窗模式：30s 缓存树 + 成员引用窗口（已修复并有验证报告）；`UpdateStackPopoverAppearance` 每次外观变化 new 3+ brush（StackPopover.cs:1281-1308）。
- WindowsCompatibilityService.AreAnimationsEnabled 在该热路径**每次 new UISettings()**（WindowsCompatibilityService.cs:98-100），不走 2 秒缓存（:127-164 只缓存合成值）。

**结论：稳态无泄漏；峰值来自投影重算 churn + compositor 对象 + brush 分配。**

### S4 材质/语言/主题色频繁切换

**双通道扇出：** 材质切换同时走 `RequestAppearancePreview()`（66ms 防抖 → AppearancePreviewChanged）**和** `SaveDebounced(notify:true)` → SettingsChanged 全扇出（SettingsViewModel.AppearanceOptions.cs:170-197）。一次切换触达每个 widget **3-4 条通知路径**。

**每次切换的分配点（热路径 new 清单）：**
| 位置 | 分配 |
|---|---|
| FileSurfaceContent.xaml.cs:347-358 | 2-3 个 SolidColorBrush（ReorderInsertionLine/ImportProgressBar/ImportStateIcon） |
| StackPopover.cs:1281-1308 | 3+ brush + CreateStackPopoverSurfaceBrush |
| WidgetShell.xaml.cs:856-877 | 3 个 brush（组拖放预览） |
| WidgetShell.xaml.cs:767 | 导航栏 Border 子元素全重建 |
| WidgetShell.xaml.cs:2999 | CompactPausedDim.Background new brush |
| WidgetShell.xaml.cs:4794-4810 | 按钮背景/边框 new brush |
| WidgetWindowBase.Backdrop.cs:303-307 | 每次 new AccessibilitySettings + UISettings |
| WindowsCompatibilityService.cs:98-125 | 每次 new UISettings / AccessibilitySettings（多处热路径调用） |

**语言切换特有：** 12 个 locale 字典静态常驻（不重建，好）；但 `LanguageChanged` → 每个 WidgetViewModel `RebuildStackDisplayItems()` **全量投影重算 + 全部 stack 本地化字符串重分配**（WidgetViewModel.LayoutAndSettings.cs:234-248）。

**主题切换特有：** RequestedTheme 赋值触发整棵 XAML 树主题资源重解析（框架行为）；Backdrop 签名比对避免 controller 重建（好）。

**订阅配对：** SettingsChanged 16 处、AppearanceChanged、LanguageChanged 全部 +=/-= 成对（窗口 Closed 集中解绑，ContentWidgetWindow.xaml.cs:1235-1272）——无订阅泄漏。

**结论：无泄漏；峰值来自扇出放大 + 重复 brush/设置对象分配 + 语言切换的全量重算。**

---

## 3. 根因归纳（架构级，7 条）

| # | 根因 | 证据 | 影响场景 |
|---|---|---|---|
| R1 | **释放不对称**：资源创建是即时的、每交互一次；释放依赖全局启发式（WS≥240MB、连续空闲 10min、隐藏 5min、堆≥96MB） | MemoryCleanupPolicy.cs:71-115、App.xaml.cs:2658-2833 | S1-S4 全部 |
| R2 | **隐藏≠释放**：`OnWindowLongHidden` 仅 2/7 内容实现（File、Todo）；Glance（含 `_compactBackground` BitmapImage）/Weather/Music/Search/QuickCapture 隐藏后整树+数据永不释放 | IWidgetContent.cs:42、GlanceWidgetContentAdapter.cs:66-104 | 常驻基线 |
| R3 | **默认档永久盲区**：Balanced 的 HiddenIdleWorkingSetTrimDelaySeconds=CleanupNever（PerformanceSettingsPolicy.cs:189 一带，已人工核实），而可见态 trim 要求 HasVisibleWidgets——**全隐藏时无任何 trim 路径**；"BestVisual"档名被归一为 Balanced，名义第 4 档不存在 | PerformanceSettingsPolicy.cs:75-88 | 全部 |
| R4 | **无预算资源**：`s_shellKindCache`（4096 FIFO，Clear 无调用方，FileService.cs:704-734）、`s_storedMetadataCache`（512 满清，ShortcutHelper.cs:143-153）、`GlanceWidgetStore.WidgetStores`（仅删 widget 时移除）、`WeatherCacheStore.s_pathGates` | 见左 | 常驻 |
| R5 | **热路径分配与重复构建**：工厂每次重建（Groups.cs:1037）、约 10 处 brush 每次 new、UISettings/AccessibilitySettings 每次 new、compositor 动画对象不复用、投影全量重算 | §2 各表 | S1/S3/S4 |
| R6 | **设置变更双通道扇出**：Preview + SettingsChanged 叠加，触达每个 widget 3-4 条路径，放大 R5 的每路径分配 | SettingsViewModel.AppearanceOptions.cs:170-197 | S4 |
| R7 | **无自动化内存回归门禁**：PerformanceLogger 有采样无断言，退化不可见 | PerformanceLogger.cs:180-232 | 工程性 |

**用户主诉"没有超时释放就一直涨"的机制解释：** R5 造成持续分配 → R1/R2/R3 使释放门槛形同虚设（可见交互期永不满足空闲、隐藏期 Balanced 无 trim、非缓存内容无隐藏释放）→ GC 只回收不可达对象且可见态 GC 有门槛 → native/compositor 分配与托管 LOH 累积 → 工作集阶梯上升。

---

## 4. 长期方案（交互峰值优先）

### Phase 0 · 测量基建（先行，1-2 天）

目的：先有标尺再动手，并为 Phase 4 门禁打地基。

1. `scripts/measure-scenario-memory.ps1`：按场景驱动并采样（WS/Private/GC 堆/句柄，2s 间隔）：
   - `--scenario group-scroll --cycles 20`
   - `--scenario capsule-toggle --cycles 20`
   - `--scenario stack-toggle --cycles 20`
   - `--scenario appearance-switch --cycles 20`
   - 输出 CSV + 摘要（起点/峰值/终点/空闲 60s 后回落值）。
2. UI 驱动复用既有输入注入方案（SendInput 绝对坐标）。
3. **降级策略（用户要求）**：真实 UI 驱动**连续失败超过 5 轮即停止驱动**，降级为"手动操作 + 自动采样"模式（脚本只采样，提示操作者执行动作），并在结果中标注 `degraded=true`。
4. 基线落盘 `docs/baselines/memory-scenarios.json`，字段含 commit、场景、四值。

### Phase 1 · 交互峰值削减（最高优先，对应 R5/R6）

按"改动小→大"排序，每项独立可验证：

| 项 | 内容 | 模板/参照 | 预期收益 |
|---|---|---|---|
| P1.1 | WindowsCompatibilityService 的 UISettings/AccessibilitySettings 缓存为静态单例 + 订阅系统变更事件失效 | — | 消除 S3/S4 热路径 RCW churn |
| P1.2 | brush 复用推广：§2-S4 清单约 10 处改为查表复用/仅改色 | FileItemSurfaceStyleCache、GetOrUpdateSolidColorBrush、AccentResourceScope | S4 每次切换分配骤降 |
| P1.3 | 组切换工厂复用：ContentWidgetWindowFactory/WidgetContentFactory 提升为 WidgetManager 字段（无状态，线程安全按需加锁） | — | S1 每切换省 7 provider+字典重建 |
| P1.4 | ~~compositor 动画对象缓存：StackAnimations 每成员 4 对象改为 per-surface 缓存池~~ **已否决（2026-08-27 用户决定：动画串场风险影响体验，不做）** | — | S3 的 compositor 分配保留为已接受成本 |
| P1.5 | 设置扇出合并：AppearancePreviewChanged 与 SettingsChanged 收敛为单一 dirty-flags 通道（AppearanceDimension/LocalizationDimension/…），订阅方按维度过滤；材质预览不再走 SettingsChanged 全扇出 | — | S4 触达路径 3-4 → 1 |
| P1.6 | ~~语言切换增量化：stack 名称/summary 本地化走按需重算~~ **已否决（2026-08-27 用户决定：重显短暂旧语言风险，不做）** | — | 语言切换保持全量重算 |
| P1.7 | 不可缓存成员改造：Glance/Music/Weather/Search 实现 `IWidgetGroupContentCacheable`（PrepareForReuse 释放重资源如 MusicSessionService 订阅、Glance 位图），使组滚动走缓存而非整树重建；容量策略按"成员类型×档位"细化 | TodoWidgetContentAdapter.cs:21、QuickCaptureSurfaceContent.xaml.cs:38 | S1 整树创建销毁 → 复用 |
| P1.8 | 投影增量化：RebuildStackDisplayItems 在"仅展开态变化"时跳过分组重算，只调整展开成员的插入/移除 | ReconcileStackPopoverItems（StackPopover.cs:1508-1549）为同构范本 | S3 CPU+GC 双降 |

### Phase 2 · 常驻内存与生命周期契约（对应 R1/R2/R3/R4）

| 项 | 内容 |
|---|---|
| P2.1 | ~~7 类内容全部实现 `OnWindowLongHidden`~~ **已否决（2026-08-27 用户决定：重显加载占位符影响体验，不做）——R2（隐藏≠释放）从此为已接受的设计取舍：换取隐藏 widget 重显瞬时完成。常驻基线相应维持现状，仅靠 P2.2 裁剪兜底** |
| P2.2 | Balanced 档启用低频隐藏 trim 兜底（如 30min 或 WS 超阈值触发），消除 CleanupNever 盲区；同时决定 BestVisual 档的真实语义或删除该档名 |
| P2.3 | 无预算资源纳入管理：s_shellKindCache 接入 idle 清理调用方、s_storedMetadataCache 改 LRU、GlanceWidgetStore 增加上限与禁用即逐出 |
| P2.4 | IWidgetContent 契约扩展（可选）：`ReleaseTransientResources()` 与 `EstimateResourceCost()`，为 Phase 3 台账提供数据。**注：随 P2.1 否决，ReleaseTransientResources 仅适用于无重显感知的资源（如诊断快照），范围大幅收窄** |

### Phase 3 · 统一资源台账（ResourceBudgetRegistry）

单一静态注册中心，每类缓存注册：键类型、容量上限、字节上限、逐出回调、当前占用。
- 现有散落的 Evict/Trim/ReleaseIdleCaches 调用点统一走 Registry；
- PerformanceLogger 的 MemorySample 从 Registry 拉全量（今天 `cachedGroupContents` 等是手工拼的）；
- 未来新增缓存必须注册（code review 检查项 + 契约测试扫描静态集合）。

### Phase 4 · 门禁固化

1. CI/手动回归任务跑 Phase 0 脚本，与 `docs/baselines/memory-scenarios.json` 断言：
   - 20 循环 Private 净增量 ≤ 基线 × 1.15 + 10MB；
   - 空闲 60s 回落 ≥ 峰值增量的 50%；
2. 降级模式同样记录（标注 degraded），不阻塞但出报告；
3. 每次发布前人工跑一次四场景，摘要进发布记录。

---

## 5. 验收标准

| 指标 | 目标 |
|---|---|
| S1-S4 各 20 循环 | Private 净增量 ≤ 15MB（现值以 Phase 0 基线为准回填） |
| 循环后空闲 60s | WS 回落 ≥ 交互期增量的 50% |
| 全部隐藏 + 5min（Balanced） | deep 释放后 WS 有可测下降（P2.1/2.2 生效判据） |
| 8h 长稳（混合场景脚本） | Private 无持续单调爬坡（斜率阈值） |
| 回归门禁 | 基线断言通过或降级记录完整 |

## 6. 风险与回滚

- 每个 P 项独立提交、独立验证（跑对应场景脚本 + 全量测试 2832 项），可单独 revert；
- P1.7（成员缓存化）风险最高（WinUI 内容复用的状态残留），放最后并配 20 循环功能自测清单；
- P1.5（扇出合并）注意保留"预览即时生效"体验，仅消除重复路径；
- 所有 compositor 复用必须保证动画参数重置完整，防串场；
- 方案不改变用户可见行为与视觉参数。

## 7. 附录：问题点索引（file:line 速查）

- 工厂重建：WidgetManager.Groups.cs:1037-1038
- 不可缓存成员：GlanceWidgetContentAdapter.cs / MusicWidgetContentAdapter.cs / Weather / Search（未实现 IWidgetGroupContentCacheable）
- 组缓存容量：PerformanceSettingsPolicy.cs:111-120
- Balanced 隐藏 trim 禁用：PerformanceSettingsPolicy.cs Resolve() Balanced 分支（CleanupNever）
- OnWindowLongHidden 缺口：IWidgetContent.cs:42（默认空实现）；仅 FileSurfaceContent.xaml.cs:670、TodoWidgetContentAdapter.cs:117 实现
- Glance 隐藏位图：GlanceWidgetContentAdapter.cs:66-104
- s_shellKindCache：FileService.cs:61,704-734
- s_storedMetadataCache：ShortcutHelper.cs:19-23,143-153
- GlanceWidgetStore.WidgetStores：GlanceWidgetStore.cs:27
- compositor 动画不复用：FileSurfaceContent.StackAnimations.cs:273-303
- 投影全量重算：WidgetViewModel.Stacks.cs:1266-1345
- 16×PropertyChanged：WidgetStackItem.cs:162-179
- 双通道扇出：SettingsViewModel.AppearanceOptions.cs:170-197
- brush 热路径 new 清单：FileSurfaceContent.xaml.cs:347-358；StackPopover.cs:1281-1308；WidgetShell.xaml.cs:767,856-877,2999,4794-4810；WidgetWindowBase.Backdrop.cs:303-307
- UISettings 每次 new：WindowsCompatibilityService.cs:98-125
- 可见态 GC/trim 门槛：MemoryCleanupPolicy.cs:71-115
- 清理调度中枢：App.xaml.cs:2597-3428

---

## 8. 扩展复审（v2 增补）

对 §1-§7 的一次红队式复审结果：新识别 6 类延伸场景、5 项方案遗漏、4 个已验证排除的疑点。

### 8.1 延伸场景（用户例举之外的累积/压力源）

| # | 场景 | 链路与风险 | 处置 |
|---|---|---|---|
| S5 | 显示拓扑事件风暴（热插拔/DPI/主屏切换） | DisplayAreaWatcherService → Coordinator → 全 widget 重排与布局重建；事件抖动（一次插拔产生多个中间态事件）会成倍放大 churn。注意：备份分支 `backup/wip-memory-runtime-lifecycle-20260827` 中被推迟的"显示拓扑快照簇"正是针对此区域的未完成工作 | P0 基线补 `--scenario display-storm`（模拟 WM_DISPLAYCHANGE）；风暴去抖列为 P1.9 |
| S6 | Explorer 崩溃重启恢复 | AppLifecycleRecoveryWatcher 在 Explorer restart 时重新注册热键/显示器/Everything 连接——恢复循环中的重注册若旧对象未先释放会累积 RCW | 检查项（P4.1 门禁外的代码审查清单） |
| S7 | 文件系统事件风暴 | 大目录复制/移动 → FolderWatcher 洪峰 → FileSurface 反复刷新（`_folderRefreshGate` 串行化但每轮全量重枚举+图标重水合） | P0 基线补 `--scenario fs-storm`；洪峰合并退避列为 P1.10 候选 |
| S8 | 搜索弹窗高频开关 | 隐藏 shell 保留 `TransientWindowReleaseDelaySeconds`（Balanced 10min）+ 每次 Everything 查询的结果集/图标 + FileMetaService 64 条缓存 | 纳入 P0 采样；弹窗关闭时结果集清空属 Search 内容自身 Dispose 路径，不依赖已否决的 P2.1 |
| S9 | 用户数据增长 | Todo/QuickCapture 集合随数据线性增长（quick-capture.json 已 59KB）——非交互泄漏，属容量问题 | 定为**非目标**，但要求进 Phase 3 台账可观测（EstimateResourceCost） |
| S10 | 托盘批量动画长跑 | WidgetTrayBatchAnimationDriver 的 entries 循环 Start/Stop（已确认清空+退订平衡） | 已排除，无需动作 |

### 8.2 方案遗漏增补

| # | 增补项 | 归属 | 内容 |
|---|---|---|---|
| P0.5 | 测量增强 | Phase 0 | ① 采样同时跑 `dotnet-counters monitor -p <pid>` 拆分托管堆/native（Private − managed ≈ native+映像）；② 把 `DeskBox.ThumbnailProxy.exe` 子进程一并采样；③ 用 PerformanceLogger 的内存快照（MemorySample）与应用日志时间线对齐，定位分配来源 |
| P1.9 | 拓扑风暴去抖 | Phase 1 | DisplayAreaWatcher 事件合并窗口（如 500ms 只应用最终拓扑），避免中间态全量重排 |
| P1.10 | 文件洪峰退避 | Phase 1（候选） | watcher 事件指数退避 + 首帧延迟合并，削峰重枚举次数 |
| P2.5 | 启动足迹 | Phase 2 | 实测启动 Private 基线 ≈314MB（2026-08-27 实测，空闲稳态 WS≈60MB/Private≈314MB）。评估：① 12 个 locale 字典改"当前+en-US 回退"惰性加载（其余切换时加载）；② 启动即全量创建的 widget 内容评估按可见性惰性；③ 明确维持工作站并发 GC（已确认未开 Server GC——**不要开**，Server GC 会显著抬高基线） |
| P2.6 | pending-heavy 卡死防御 | Phase 2 | `s_pendingHeavyMemoryCleanup` 标记在 TryEnqueue 失败时的自愈（超时自动复位），消除 agent 勘察指出的理论阻塞点（App.xaml.cs:3296 一带） |
| P2.7 | ~~隐藏窗口虚拟化（可选）~~ **已否决（2026-08-27 用户决定：重显 1-2 秒重建不可接受，不做；ResourceSaver 档亦不启用）** | Phase 2/3 | — |
| P4.3 | 契约测试固化 | Phase 4 | 按仓库既有契约测试文化（如 FileSurfaceParityContractTests 扫描源码的模式）新增：① ~~所有 IWidgetContent 实现必须实现 OnWindowLongHidden~~（随 P2.1 否决而作废）；② 所有静态缓存集合必须注册 ResourceBudgetRegistry；③ OwnedOneShotDispatcherTimer 创建/释放计数配对（已有 RecordTransientUiTimerCreated/Released 钩子） |
| — | QuickCapture 详情图解码 | 检查项 | `DetailImageDecodePixelWidth = 1200`（QuickCaptureSurfaceContent.xaml.cs:43）对详情窗偏大（1200²×4 ≈ 5.7MB/张）；确认列表缩略图与详情图解码分离，必要时降到 800 |

### 8.3 已验证排除的疑点（复审中核查，非问题）

1. **可见空闲 GC 不卡 UI**：`GC.Collect` 在 `Task.Run` 线程池线程执行（App.xaml.cs:2731-2741，blocking 但非 UI 线程）。
2. **Glance 轮播解码受控**：按控件尺寸动态计算 `DecodePixelWidth`，且有 decode 刷新判定（GlanceWidgetContent.xaml.cs:823-878）。
3. **音乐封面解码受控**：`CoverDecodePixelWidth = 192` 硬上限（MusicWidgetViewModel.MediaInfo.cs:232）。
4. **缩略图代理进程无状态**：`deskbox-thumbnail-proxy/src/main.rs` 无静态缓存，每请求分配 Vec 后释放；进程内存有界（P0.5 仍纳入采样以实证）。

### 8.4 对 §1-§7 的校准

- §2-S4"语言切换 12 字典常驻（好）"：定性不变，但常驻成本未量化——列入 P0.5 基线测量项，用数字决定是否做 P2.5①。
- §5 验收标准表补充：S5（display-storm）与 S8（search-toggle）的门禁占位，阈值待 P0 基线回填。
- §6 风险表补充：P2.7 窗口虚拟化与用户对"重现速度"的预期强相关，实施前需单独确认；P4.3 契约测试对 Native AOT 构建时间的影响需评估（扫描型测试增加构建产物读取）。
