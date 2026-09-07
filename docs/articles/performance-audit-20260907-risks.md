# DeskBox 性能优化改动风险评估（2026-09-07 第二轮）

> 对 `performance-audit-20260907.md` 全部发现项的交叉冲突面与改动风险深查。5 个并行子代理逐项追调用链、契约测试、既有设计取舍。结论分四档：**直接可做 / 需改方案后做 / 谨慎做 / 建议不做**。

---

## 总裁决表

| 原发现 | 修法 | 风险 | 裁决 |
|---|---|---|---|
| F1 DependentAnimation | 删标志 | 低 | **直接做**（零测试牵连，注意 FeedbackPresenter 助手含 Translate.Y 一并删） |
| F3+F6a 休眠帧控器 | 删除 | 低 | **直接做**；连带发现 SmartAnimationAdapter 也是活死码（构造期还跑性能测量拖累启动），建议一并删（WidgetWindowBase.cs:50/55/132-148），F6a 刷新率采样问题随之消失 |
| M4 PrivateUsage 探针 | 新增观测 | 低 | **直接做**（只观测不触发 GC，复用现有 [Memory] 日志格式） |
| M5 惰性 Flyout | 对齐 :339 模式 | 低-中 | **直接做** |
| B6 Watchdog 4s→10-15s | 放宽 | 低 | **可做，勿删**（查明是 hang 取证手段：miss#5 时抑制强制 GC 是历史教训；无恢复逻辑依赖它） |
| B8 Backup one-shot | 绝对时间调度 | 低 | **可做**（设置变更/手动备份/恢复时重算） |
| U3 整理卡片差分复用 | 键控 diff | 中低 | **可做**（设计红线不受影响——视觉全在 XAML/LayoutTargetCards；须给图标加载加 generation 守卫防张冠李戴） |
| P2 四小项（Markdown 后台 Parse/阴影节流/Reconciler 字典/SharedBrushCache） | 套既有模式 | 低 | **可做** |
| H2 批量枚举 | FindFirstFileW 单遍 | 中 | **做，但语义敏感点两处**（见下） |
| H1 junction Rust 化 | native 导出 | 中高 | **限点接入**：只切 FileService.cs:584（唯一每条目热点，每文件夹 2 次），其余 20 处保持托管门面内部分发 |
| U1 框选差分 | 差分 Add/Remove | 中高 | **需改方案**：`UpdateItemSurfaceVisuals` **不可**收窄到 `_selectionHits`（会回归 stack 投影 realize 已修 bug）；正确做法见下 |
| U2 去 sync-over-async | async 化 | 中 | **需改方案**：DragStarting 的"部分载荷即取消"语义是真正难点，不是线程模型 |
| F5 Win11 backdrop 降级 | 超支触发切换 | 高 | **谨慎做**：有材质泄漏史 + 恢复竞态；只针对 Acrylic，走 `_solidColorBackdrop` swap，绝不重建 controller |
| B1 DisplayArea 事件化 | 消息窗口 | 中 | **混合方案**：事件 + 30s 慢轮询兜底（纯 DPI/重排/RDP 不触发 WM_DISPLAYCHANGE；现有架构注释本就自认轮询是兜底） |
| B2 Music 500ms timer | 降频 | 低-中 | **可做**（外推逻辑已存在！只调间隔；须处理 seek/暂停基准重置，时钟改 TickCount64） |
| B3 Todo 变更驱动 | 增量调度 | 中 | **混合**：必须保留慢扫兜底 + 挂起恢复即查（宽限窗仅 1min，纯单 shot 会静默丢提醒） |
| B4 鼠标钩子预过滤 | 类名快速排除 | 低 | **可做**（重活留线程池；发现附带问题：第三方壁纸引擎窗口被误判为桌面空白） |
| B7 启动延迟 | 后移 IO | 中高 | **只动 App.xaml.cs:1020-1021**，不动 :972/:983（storage sync 参与恢复与通知时序） |
| F2 Width 动画 | compositor 化 | 中 | **可做**（选 Clip 不选 ScaleX——ScaleX 拉伸文本且 hit-test 不符；须同步 2 个源码契约测试） |
| F4 分离预览轮询 | 指针事件化 | 高 | **原方案不可行**：OLE 拖拽模态循环下收不到指针事件——这正是当初用轮询的原因。改 DispatcherQueueTimer + 帧钟节流 |
| M1 缓存预算收敛 | 降默认/共享池 | 中高 | **小步做**：双缓存解码尺寸不同不可直接共享条目；改默认值要走 5 接线点+12 语言+迁移 |
| M2 缩略图流式化 | BitmapDecoder | 中高 | **改用流喂 BitmapImage**，勿用 BitmapDecoder（EXIF 方向/GIF/BMP alpha 三处确定性回归） |
| M3 弹窗 idle 关闭 | 10min 超时关 | 高 | **建议不做**：代码注释明确 warm-keep 是为防热键首开延迟+材质闪变，且无预重建机制 |
| M7 QuickCapture 上限 | 截断 | — | **产品决策**，不随技术批次 |
| B5 钩子锁优化 | volatile | 高/收益微小 | **不做**：三字段跨锁 generation 一致性，opt-in 场景无竞争，20ns 无谓冒险 |
| H4 Everything latest-wins | 取消旧查询 | 中 | **只做探测 single-flight**；查询路径的串行是 DLL 单实例正确性前提，不可放宽 |
| H5 FolderWatcher flag 合并 | 合并 enqueue | 低-中 | **只做 TryEnqueue 失败兜底**；跨事件 flag 合并会破坏 partial 语义 |
| H6 journal debounce | 防抖落盘 | 中高 | **拒绝 debounce**：per-item 落盘是崩溃一致性回执，防抖会让恢复误回滚。可改 append-only 回执日志 |
| H7 开始菜单早停 | 40 项即停 | 低 | 可选：配额轮转（每 root ≤10 回填），企业大菜单需实测 |
| F7 bounds 并发上限 | 硬件相关 cap | 低-中 | 可做；被拒请求不得落 IsApplyingBounds=true（Prepare/Complete 配对契约） |
| B4' BoundedPathChangeBuffer | 接线或删 | — | 接线属行为变更需单独评估；倾向删除死代码 |
| H3 缩略图代理常驻化 | worker 进程 | 中高 | **先做折中**："N 个并发请求复用一个短命 worker 批处理"；常驻方案须带 watchdog/重启熔断/correlation id。右键菜单代理保持 one-shot |

---

## 关键修正（推翻/修正原报告的修法）

### 1. U1 框选：原修法的第二半会引入回归
`UpdateItemSurfaceVisuals` 走全量遍历是**刻意修复**（ItemVisuals.cs:1730 注释：集合投影 realize stack 成员不触发 template Loaded，必须从原生容器重枚举）。且 `_selectionHits` 只在框选启动时缓存一次——Ctrl+click / Ctrl+A / stack 展开 / cut 刷新路径根本没有 hits。
**正确做法**：差分只限 `ApplySelectionPreview`（`_isSynchronizingSelection` 守卫内的 toAdd/toRemove 差集）；视觉刷新新增"只刷本次变化容器"的私有路径（从 `e.AddedItems/e.RemovedItems` 经 ContainerFromItem 即时解析，天然覆盖新 realize）；`UpdateItemSurfaceVisuals` 公共方法保持不动；PointerReleased 同步 flush。同步改契约测试 `FileSurfaceParityContractTests.cs:1059`（源码字符串断言了 `SelectedItems.Clear()` 存在）。
选择顺序语义确认安全：所有消费方（剪贴/拖拽包/QuickLook）只做集合语义，无人依赖顺序。

### 2. F4 分离预览：指针事件方案根本不可行
拖拽是 `StartDragAsync` 的 OLE 模态循环，期间源窗口收不到 XAML 指针事件——**当初选轮询就是因此**。可行替代：DispatcherQueueTimer（模态循环会泵消息）+ `WidgetAnimationFramePacingPolicy` 节流 + 保留 Esc 键轮询。契约测试 `WidgetPresentationAndDetachContractTests.cs:102` 直接断言了 `Task.Delay(16, token)` 存在，必须同步改。PreviewWindow 是纯 Win32 分层窗口无合成器树，淡出只能帧时钟阶梯插值，另需补 ResourceSaver 直隐路径。

### 3. U2 拖拽 async 化：真正难点是"部分载荷即取消"
`FileItemDragPackage.cs:83-131` 注释明确：必须在 DragStarting 当下判断 .lnk/broker 阻断与"storageItems 数 != 源数 → 拒绝部分载荷回退 NativeShell attach"。改延迟 provider（`SetDataProvider`，QuickCaptureDragPackage.cs:115 有成熟范例）后 e.Cancel 不可用。
**方案**：启动时仍同步做纯路径预判（RequiresStorageBrokerBypass）+ 同步 attach Shell 保底；延迟 provider 成功时覆盖，失败时记日志并让 drop 对账放弃。委托签名改 Task 返回，连带 `_activeDragHasStorageItems` 赋值时机后移。

### 4. H2 批量枚举：两处语义雷
- 现状是**混合语义**：`File.GetAttributes` 对 junction 返回链接属性（不穿透），而 `Directory.Exists`/`FileInfo.Length` 穿透目标。若 find-data 属性统一口径，"junction 指向 hidden 目录"的过滤结果可能反转。需对照测试锁定。
- `EnumerateDirectoryForRefreshAsync` 的双快照是刻意竞态检测（:255 注释），不可当冗余删。
- 排序（OrderBy IsFolder + NaturalStringComparer）UI 依赖，并行化后必须按索引回填恢复原序。
- `ShouldDisplayEntry` 是公共契约（3 处外部消费），保留原实现，批量路径内部走新 `NativeFind`。

### 5. M2 缩略图：不要用 BitmapDecoder
`BitmapImage` 自动应用 EXIF 方向、支持 GIF 动画；`BitmapDecoder` 两者皆否（BMP alpha 还有黑底风险）。**改法**：保留 `BitmapImage` 终点，只把 `ReadAllBytesAsync` 换成 `FileRandomAccessStream` 喂 `SetSourceAsync`——同样避免全量驻留（DecodePixelWidth 已设，解码即下采样），语义零变化。

### 6. H6 journal：拒绝 debounce
per-item 落盘是崩溃一致性回执：每项 move 完成即落盘，进程中途崩溃时 Restore 只回滚未 Completed 项。防抖会让最后 N 个已完成项被误回滚/误保留。若要省 IO：改 append-only 回执日志（每项一行 append 更便宜），或 ≤50ms 微批 + 异常强制 flush，且保证崩溃瞬间 journal 的 Completed 集合在保守侧。

### 7. M3 弹窗 idle 关闭：与明确设计决策冲突，建议不做
SearchPopupWindow.xaml.cs:426-431 注释原文：重建会把内存节省转成可见输入延迟 + 材质闪变。且全库无 idle 预重建机制。若坚持做，必须"关窗 + 后台维护时不可见预重建"两段式 + 热键首开 P95 不劣化验收。默认：计入可接受常驻。

### 8. H1 junction Rust：限点接入而非全量切换
21 处调用中唯一每条目热点是 `FileService.cs:584`（CaptureEntrySnapshot，每文件夹子项一次，且内嵌 CountVisibleChildren 再触发一次 = 每文件夹 2 次）；其余为一次性（图标/元数据，下游有缓存）或每导航。**单一门面内按 backend 分发，调用方零改动**；只切 :584（可合并 :802）。保真清单：false=不可用契约、hop=64、环检测、非链接 reparse（云占位）直通、`TryResolvePathWithMissingSuffix` 内部依赖；失败回退托管而非静默 false；加托管 vs Rust parity 对照测试。

### 9. B3 Todo：纯变更驱动会静默丢提醒
`MissedReminderGrace` 仅 1 分钟，纯"下次到期"单 shot 在挂起/恢复（timer 停走）后必然漏。必须：慢扫兜底（30-60s）+ next-due 提前触发 + 写路径主动 CheckNow + 恢复即查。

### 10. B1 DisplayArea：轮询是架构自认的兜底
WidgetDisplayChangeWatcher 注释明言依赖"全局轮询兜底"。且纯 DPI 变化/显示器重排/主屏切换/RDP 不触发 WM_DISPLAYCHANGE（WM_DPICHANGED 只发顶层窗口）。混合方案：消息窗口收信号 → 调现有 `RefreshNow()`（签名比对天然幂等去重），轮询 2s→30s。

---

## 连带改动速查（实施时必须同步）

- **源码字符串契约测试**（改代码前先 grep 对应断言）：`FileSurfaceParityContractTests.cs:1059`（框选）、`WidgetPresentationAndDetachContractTests.cs:102`（分离预览）、`WidgetGroupTitleWheelContractTests.cs` / `WidgetGroupCompactPositionRailContractTests.cs`（标题切换）、`Windows10WidgetMotionContractTests.cs:299-310` / `Win10FramePacingAndBackdropPolicyTests.cs`（backdrop）。
- **设置接线红线**（M1 若改默认值）：AppSettings.cs:72 + PerformanceSettingsPolicy.cs:71 + SettingsViewModel.Performance.cs 5 点 + 12 语言 Strings + LocalizationResourceContractTests；存量显式保存过的用户不跟随新默认。
- **AOT/运行时契约**：TodoReminderServiceTests（30s 行为）、AotStage5B4C3*（音乐/备份）、IdleRuntimeLifecycleContractTests（watchdog）。
- **修法红线**：F5 降级期间绝不销毁/重建 MicaController/AcrylicController（泄漏史，Backdrop.cs:455/478 注释）；ApplyBackdropPreference 入口须清降级标志防恢复竞态。

---

## 推荐实施批次

1. **零风险批**：F1 删标志、F3+F6a 删 AdaptiveTrayAnimationController+SmartAnimationAdapter、M4 探针、M5 惰性 Flyout、B6 watchdog 放宽、B8 backup one-shot、P2 四小项。
2. **低风险批**：B2 Music 降频（外推已存在）、B4 鼠标预过滤、U3 卡片差分、B1 混合方案（事件+30s）、B3 混合方案（慢扫+next-due）。
3. **中风险批（需按上文修正方案）**：U1 框选差分（不动公共刷新方法+改契约测试）、U2 拖拽延迟 provider（保底 attach 语义）、H2 批量枚举（对照测试锁 junction 语义）、F2 Width 动画（Clip 方案+2 契约测试）、M2 流式喂 BitmapImage、M1 小步预算收敛、H1 junction Rust 限点接入、H3 短命 worker 批处理折中、B7 启动只后移 :1020-1021。
4. **高风险/缓做**：F5 Win11 降级（先 Acrylic only + 竞态防护 + 新契约断言）、F4 分离预览（DQT 方案）、F7 并发 cap、H5/H6 按修正案、M3 不做、B5 不做、M7 移交产品。
