# DeskBox 全功能性能梳理（2026-09-07）

> 只读审计，未改任何代码。五个并行方向：动画帧率 / 文件与 Shell 热路径 / 内存 / 后台服务与定时器 / UI 线程与大数据渲染。行号基于当前 main（1.5.0 后）。

---

## 一、帧率 / 高刷屏卡顿（120–165Hz 观感）

共享帧钟体系（WidgetCompactAnimationCoordinator + CompositorClockBoostCoordinator + pacing/health 策略）覆盖了主路径，以下是绕过点：

### F1【高】4 处错误的 `EnableDependentAnimation = true`（复核确认：共 5 处，其中 4 处为纯 Opacity，1 处是 F2 的 Width）
全部核实无误：
- `Controls/WidgetFeedbackPresenter.xaml.cs:208`（AddAnimation 通用助手，调用点 163-164 动画 `Opacity` 与 `TranslateTransform.Y`——两者均可独立渲染，182 再用一次）
- `Views/SearchPopupWindow.xaml.cs:4140`（ResultsPanel Opacity）
- `Views/SettingsWindow.Navigation.cs:315`（搜索高亮 Opacity，650ms）
- `Controls/WidgetGroupTitleSwitcher.Interaction.cs:989`（TitleInteractionChrome Opacity）
- **修法**：删掉这些 `= true`，或改 `GetElementVisual().StartAnimation` ScalarKeyFrame。一行级改动，收益最大。

### F2【高】分组标题切换动画 Width 每帧布局
`WidgetGroupTitleSwitcher.Interaction.cs:828-845` 动画 `FrameworkElement.Width`（复核确认 :836 `EnableDependentAnimation=true`，此处 Width 确实必须 dependent——问题在"选了逐帧布局的方案"而非标志本身），每帧一次完整 measure/arrange，120Hz 上即每 8ms 布局一次，并阻塞共享帧钟回调。
- **修法**：改 `Clip.Rect` / Size 的 compositor 动画，或固定宽容器 ScaleX + 裁剪；文本切换改 crossfade + translation。

### F3【高】AdaptiveTrayAnimationController CPU 模式私有 Rendering 循环，完全绕过共享帧钟
`Services/AdaptiveTrayAnimationController.cs:304-306, 407-419, 568`：自订阅 `CompositionTarget.Rendering`、显式禁用节流、每帧 SetWindowPos；不持 boost 租约、不受帧跳过/降级保护。**且目前无产品代码调用（休眠代码）**。
- **修法**：删除，或接入共享帧钟后再启用；GPU 分支完成回调改 `CompositionAnimation.Completed`。

### F4【高】分组分离拖拽预览：16ms 轮询 + Task.Delay 阶跃淡出（硬编码 ~60Hz）
- `Views/WidgetWindowBase.Grouping.cs:510-545`（Task.Run + `Task.Delay(16)` 轮询 → SetWindowPos）
- `Views/WidgetDetachPlacementPreviewWindow.cs:215-231`（4 步 28ms 手写淡出）
高刷屏上预览 ~60Hz 跟随鼠标，滞后抖动；Task.Delay 精度受 15.6ms 定时器粒度影响，低配机上漂到 30Hz。
- **修法**：跟随改由源窗口 PointerMoved（经共享帧钟 paceToDisplay）驱动；淡出改 compositor Opacity 动画。

### F5【中】Win11 Acrylic/Mica 在逐帧 bounds 动画期间从不降级（复核确认，但注意这是设计取舍的一部分）
`WidgetWindowBase.Backdrop.cs:210-242` 的 `SimplifyBackdropForInteraction` 仅对 Win10 legacy acrylic 生效（复核确认门条件 `LegacyAccentBackdropActive && UsesLegacyWindowAcrylic`）；且注释明确"frosted glass by default——降级只在近期帧超支（或省资源模式）后才跑"，即连 Win10 也是"先卡后降"。Win11 SystemBackdrop 路径完全无降级机制。
- **修法（需尊重既有设计意图）**：不建议无条件降级（与 frosted-glass-first 的既有决策冲突）；建议把 InteractionBackdropSimplificationPolicy 的超支触发同样接到 Win11 SystemBackdrop 路径——帧预算真超支时临时切 `_solidColorBackdrop`（已有实现），动画结束恢复。

### F6【中】刷新率配置只在构造时采样
`AdaptiveTrayAnimationController.cs:377-381`、`HardwareAdaptiveAnimationService.cs:178-206`：TargetRefreshRate/baseFPS 冻结；跨不同刷新率显示器拖动或动态降挡时帧预算失配。改用 `WidgetAnimationDisplayTiming` 活值。

### F7【低】bounds-transition 注册无并发上限 + Win10 回退 timer 不对齐 vblank
`WidgetCompactAnimationCoordinator.cs:24, 228-242`：`MaximumConcurrentBoundsTransitions = int.MaxValue`，Win10 无 DwmFlush 回退 timer 固定间隔漂移 1-2ms。给并发上限硬件相关值、bounds 类注册也走 pacing。

### 确认无问题
托盘滑入/滑出（共享时钟 + DeferWindowPos 批量提交）、插入指示呼吸脉冲（compositor ScalarKeyFrame）、跑马灯/唱片旋转、标题拖动 paceToDisplay、分组协调移动批量 DeferWindowPos、各处帧计数型等待（等待非动画，cancel 路径齐全）。

---

## 二、文件 / Shell 热路径（含 Rust 判断）

| 排名 | 热点 | Rust 是否值得 |
|---|---|---|
| 1 | junction 逐段解析 `TryResolveExistingPathForTraversal` | **值得** |
| 2 | 缩略图代理进程化（per-item CreateProcess） | **部分值得**（改造现有 native 组件） |
| 3 | 目录枚举 per-entry 3-8 次冗余 stat | 不值得，C# 批量化 |
| 4 | 传输引擎 | 不值得（IO-bound，已委托 Shell） |
| 5 | Everything 搜索 | 不值得（IPC-bound） |

### H1【高】junction 解析 = 全应用最大隐性 CPU/syscall 热点
`Services/FileService.PathResolution.cs:18-183`：每路径逐段 `File.GetAttributes`，遇 reparse point 再 `ResolveLinkTarget` 递归。**22 处调用点**（枚举、快照、图标、计数、FolderWatcher、搜索枚举等），无缓存（刻意，防 swap-during-drag 读到陈旧物理路径——正确性取舍，保留语义）。
- **最优解（Rust 值得）**：在现有 `native/deskbox-native` 模块（shortcut.rs / recycle_bin.rs 同款导出模式，ShortcutNativeBackend 加载基建现成）追加 `resolve_traversal_path`：NtOpenFile + FSCTL_GET_REPARSE_POINT 一次调用完成逐段+循环检测，保持"每次真实解析"语义。
- 折中：短 TTL（~5s）解析缓存 + 传输前强制重解析（现有行为），拿 90% 收益。

### H2【高】目录枚举：每条目 3-8 次冗余文件系统 stat
- `FileService.cs:197-216`：entry 串行 await，无并行。
- `FileService.cs:525-535` + `File.cs:505,558`：每条目 File.Exists + Directory.Exists + GetAttributes + 快照再 stat = 3-5 次。
- `FileService.cs:219-297`：刷新路径 = 2 次全目录枚举 + 每条目 ~6-8 stat。
- `FileService.cs:800-811`：CountVisibleChildren 每文件夹再全枚举一次。
- **修法（C#，不值得 Rust）**：一次 `FindFirstFileW`/`FileSystemEntry` 拿全属性；entry 处理并行化。决定 widget 打开/刷新首帧延迟，随条目数线性放大。

### H3【中】缩略图/右键菜单代理：每请求一个进程
`ShellThumbnailProxy.cs:119-140`：每个非媒体缩略图 = 冷启动一个 `DeskBox.ThumbnailProxy.exe`（CreateProcess + COM + 管道），并发上限 2；50 个 PDF 目录 = 50 次进程冷启动。进程外隔离（防第三方 Shell handler DLL 崩溃进主进程）是硬约束，不能放弃。
- **修法**：代理从"每请求一进程"改**常驻 worker 进程**（stdin 队列/named pipe 批量请求）——现有 native 组件的协议升级，非重写。或先走进程内 `SHGetImageList`/IThumbnailCache 快路径兜底。

### H4【中】其他
- `FileService.cs:1016,1033,1076`：`GetAwaiter().GetResult()` 同步阻塞 WinRT storage API（见 UI 节 U2）。
- `FileService.cs:1673-1719`：legacy shell move 逐项 SHFileOperation；完成验证每项 4 次 stat。统一到 modern `IFileOperation` 批处理。
- `DesktopOrganizationTransaction.cs:100-112`：journal `Items.First` O(n) 查找（整体 O(n²)）+ 每项完成同步落盘。建字典 + debounce 批量落盘。
- `SearchEngineService.cs:246-336`：空态推荐递归枚举全部开始菜单 .lnk，40 项上限不限制枚举量；枚举中达上限即停。
- `EverythingSearchService.cs:368-503`：native 查询被全局信号量串行，无"最新优先"丢弃；gate 前检查排队即可。
- `FolderWatcherService.cs:887`：每 FS 事件一次跨线程 TryEnqueue 到 UI 线程（高 churn 目录排队压力）；改 volatile flag + UI timer 合并。**BoundedPathChangeBuffer 疑似死代码**（仅测试引用），接线或删除。
- `FileService.cs:676-703`：快捷方式目标串行最坏 N×1.5s。
- Rust 已有成功先例可复用：shortcut.rs、recycle_bin.rs（AOT 下 ShortcutNativeBackend 默认启用）。

---

## 三、内存占用

### M1【重要】IconHelper 三层缓存组合预算 112–168MB + XAML 解码面双重驻留
`Helpers/IconHelper.cs:15-24, 262, 332`：Balanced 档 icon bytes 32MB + decoded bitmaps 48MB + thumbnails 32MB = **112MB 名义预算**（Large ×150% = 168MB）；icon 路径与 thumbnail 路径 key 不同，同一文件可驻留两份 BitmapImage；条目移除后解码面仍由 XAML 图像缓存持有，MemoryReclaimer 120s 冷却期间不回收。这是 214MB native heap 最可疑的托管侧对应物。
- **修法**：默认档降为 Small 或三缓存纳入单一全局预算；同文件两条路径共享条目；缓存收缩与 GC 解耦。

### M2【中】媒体缩略图回退整文件读入内存
`IconHelper.cs:524`：`File.ReadAllBytesAsync` + 解码，80MB RAW 瞬时驻留 80MB 托管 + 解码面，并发 2 可叠加。
- **修法**：改 `BitmapDecoder.CreateAsync(stream)` + `BitmapTransform` 直接 WIC 层缩放解码（GlanceImagePaletteService 已有同款模式）。

### M3【中】SearchPopupWindow 隐藏后整窗常驻
`SearchPopupWindow.xaml.cs:426-431`（刻意 warm 复用，估 5-15MB）。
- **修法**：复用 HiddenWorkingSetTrimTracker 的隐藏会话信号，隐藏超 ~10 分钟走 `Close()` + 既有懒重建路径。

### M4【中】MemoryReclaimer 只看托管堆，native 不可见
`MemoryReclaimer.cs:141-150`：`HeapSizeBytes` 仅托管堆；对 native heap 的清理效果不可归因。
- **修法**：加 `PROCESS_MEMORY_COUNTERS.PrivateUsage` 探针（日志已有 [Memory] 行可扩展）。

### M5【低】
- `DesktopOrganizationPreviewCard.xaml.cs:334`：每瓦片预建 Flyout——改点击时惰性 `ShowAt`（339 行已有同款惰性模式）。
- `TodoWidgetStore.cs:248-249`：`NormalizeRecurrenceSeriesIds` O(n²)——先建索引。
- QuickCapture `_data.Items` 无上限（RecentItems 限 100）——考虑上限/分页。
- `IconHelper.cs:467-482` + `ShellThumbnailProxy.cs:467-482`：每图标全像素 alpha 扫描 + 载荷双份拷贝——Thumbnail 模式跳过扫描或采样。
- 确认管理良好（结案）：GlanceImageService（18×4 张/单张≤18MB LRU 磁盘缓存）、PaletteService（40×40 采样）、SnapshotCache（像素预算 LRU）、SharedBrushCache、StyleCache、Attachment 流式落盘、Onboarding/ReleaseNotes/组织窗口生命周期、NativeDropImageManager。

---

## 四、后台服务 / 空闲 CPU

### B1【高】DisplayAreaWatcherService 2 秒常驻轮询
`Services/DisplayAreaWatcherService.cs:18, 44-46`：每 2s 完整 `EnumDisplayMonitors` + 字符串拼接排序比较。**全库无任何 `WM_DISPLAYCHANGE` 处理**（grep 零命中），而 `RefreshNow()` 本就证明事件路径存在。
- **修法**：隐藏消息窗口收 `WM_DISPLAYCHANGE`/`WM_DPICHANGED`，轮询整个去掉；过渡期至少降到 10s。这是最大的固定空闲唤醒。

### B2【中】MusicWidget 500ms 进度 timer
`MusicWidgetViewModel.cs:19-20, 114-118`：展开态每 500ms 查会话状态。
- **修法**：本地外推（位置+速率累加），2-5s 校准一次。

### B3【中】TodoReminderService 30s 全量 Load
`TodoReminderService.cs:16, 85-88`：每 30s 反序列化全部事项 JSON。改增量（store 文件变化已可被 FolderWatcher 观察）或"下次到期时间"一次性定时。

### B4【中】低级钩子（系统级每次鼠标/键盘进回调）
- `ReservedHotkeyHookService.cs:290-294, 363-459`：WH_KEYBOARD_LL，opt-in 才装（默认 RegisterHotKey 零成本，设计好）。可再优化：钩子回调先读 vkCode int 再决定是否 PtrToStructure；锁改 volatile 快照。
- `DesktopDoubleClickActivationService.cs:266-270, 332-364`：WH_MOUSE_LL 常驻；每次桌面左键触发跨进程 hit test（VirtualAllocEx + LVM_HITTEST 发 Explorer）。加 `WindowFromPoint`+类名廉价预过滤短路。

### B5【低】
- `AppDiagnosticsService.cs:167`：4s UI 看门狗无条件双唤醒——放宽到 15-30s 或诊断模式启用。
- `WidgetManager.ZOrder.cs:730-736`：托盘展开期 50ms 鼠标采样——改临时 WinEvent/钩子。
- `App.xaml.cs:2368`：1 分钟备份检查 timer——改绝对时间一次性调度。
- `App.xaml.cs:961-1021` 启动路径：`SyncStorageFolderEntries`/`ManagedStorageDesktopShortcutService.SyncAsync` 等磁盘 IO 可后移到首窗显示后懒执行。
- 确认良好：FolderWatcher（FSW+防抖+指数退避）、自动整理 watcher（设置开才启）、天气（退避+缓存）、更新检查（一次性）、ShellUiForegroundMonitor（限 10s 自退）。

---

## 五、UI 线程 / 大数据渲染长帧

### U1【P0】文件网格框选：每次鼠标移动全清全加 + 全量视觉刷新（复核确认，复杂度修正为 O(n·树深)）
- `FileSurfaceContent.SelectionAndMenus.cs:100-130`：每 pointer-move `ApplySelectionPreview`。
- `SelectionAndMenus.cs:207-245`（复核确认）：每 move `SelectedItems.Clear()` + 按 `Items` 顺序全量重加（每项一条 selection 通知）；命中集合本身用 `HashSet` 计算——**原报告"SelectedItems.Contains O(n²)"不成立，予以撤回**。
- `FileSurfaceContent.ItemVisuals.cs:1712-1738`（复核确认）：每次刷新遍历全部 `activeView.Items`，每项 `ContainerFromItem` + `FindDescendantByTag`（视觉树下行递归）+ `FindOwner`（树上行）。每次 move 的真实成本 = O(n) 次树查找 + O(选中数) 条通知；125Hz 鼠标 + 几百文件时仍是主要掉帧源，但量级从 O(n²) 下修为 O(n·树深)。
- `FileSurfaceContent.xaml.cs:4456-4510`：ctrl+click 选中变化走同一全量刷新路径。
- **修法**：差分更新 SelectedItems（只处理进入/离开矩形的项）；只遍历 `_selectionHits`（已缓存 Item/Border）；SelectedItems 转 HashSet；16ms 合帧节流。

### U2【P0】UI 线程 sync-over-async（复核确认，频率修正：每次拖拽启动一次，非每指针事件）
- `FileService.cs:1016, 1033-1034, 1076`：`GetFileFromPathAsync(...).AsTask().GetAwaiter().GetResult()`——复核确认三处，且 `TryGetStorageFile` 的失败回退路径里还有第 4 处（parent folder 查找，:1032-1033 两条连用）。
- 入口 `GetStorageItems`（:868-901）同步串行；唯一生产调用点是 `FileSurfaceContent.xaml.cs:1022` 拖拽启动（DragStarting）时的 `FileItemDragPackage.TryPrepare` —— 即**UI 线程上、每次拖拽启动执行，多选 N 个文件 = N 次同步阻塞**。WinRT storage API 通常在线程池完成所以一般不死锁，但每个文件一次 UI 线程停顿在多选拖拽时可感知。
- `StoreStartupService.cs:123`：`StartupTask.GetAsync().GetAwaiter().GetResult()`（启动路径）。
- **修法**：全链路 async 化（同文件 `TryGetStorageFolderAsync` :1070 已是正确范例）。

### U3【P1】整理预览：每次勾选全量销毁重建几百张卡
`DesktopOrganizationTaskView.Presentation.cs:41-97`：勾选/取消勾选/include-selected/源切换都 `ReleasePreviewCards` 全清重建（每 tile = Button+StackPanel+Image+FontIcon+TextBlock）+ 异步重新解码图标。
- **修法**：按 `SourceBucketId` 复用卡片，只对勾选态/增删项做差分（类比 SearchResultCollectionReconciler）。

### U4【P1】LayoutContext PropertyChanged 风暴
`FileItemSurface.xaml.cs:407-440`：ViewModel 任意属性变化 → 一口气 raise 8 个 PropertyChanged × n 个 tile × 布局失效。
- **修法**：按 `e.PropertyName` 过滤，只投影真正相关的那几个；批量更新合并通知。

### U5【P2】
- `MarkdownDocumentView.cs:326-361`：Markdig Parse + Render 在 UI 线程同步（有去抖，无每键重渲，但长文档首帧是一次同步全量）——Parse 移 `Task.Run`。
- `WidgetTextShadowManager.cs:81-134`：resize 期间每次 LayoutUpdated 排一次全树 TextBlock 枚举 + TransformToVisual——reconcile 加 ~100ms 最小间隔。
- `SearchResultCollectionReconciler.cs:97, 126-140`：最坏 O(n²) Move 查找 + 每 Move 一条独立 CollectionChanged——引用字典 + 布局批处理（当前页小，风险低）。
- `WidgetGroupTitleSwitcher.xaml.cs:556-577`：位置导轨每次重建 new accent 画刷——用 SharedBrushCache。

### 确认健康
搜索管线（35ms debounce + 异步 + 虚拟化 + 行高缓存 + 增量 reconcile + 懒元数据）、内容切换 Prepare/Commit 事务模型、Stack 弹层防闪、Settings 延迟分区（带 [SettingsPerf] 埋点）、Markdown 每键不重渲、行内图片 DecodePixelWidth=960、StyleCache 指针路径零分配。

---

## 六、总优先级建议（跨维度 Top 10）

| # | 项 | 维度 | 性价比 |
|---|---|---|---|
| 1 | F1 删 4 处 `EnableDependentAnimation=true` | 帧率 | 一行级，收益最大 |
| 2 | U1 框选逐 move 全清全加 + 全量树刷新（差分化） | UI | 中等改动，文件网格跟手性质变 |
| 3 | U2 UI 线程 sync-over-async 清零 | UI | 小改动，消死锁风险 |
| 4 | F2 标题切换 Width 动画改 compositor | 帧率 | 中等 |
| 5 | H2 目录枚举一次拿全属性 + 并行 | 热路径 | 中等，首帧延迟线性收益 |
| 6 | M1 IconHelper 缓存预算收敛 | 内存 | 直接压 native heap 峰值 |
| 7 | B1 DisplayAreaWatcher 改 WM_DISPLAYCHANGE | 空闲CPU | 小改动，去掉最大常驻轮询 |
| 8 | F5 Win11 Acrylic 交互期降级 | 帧率 | 小改动 |
| 9 | H1 junction 解析 Rust 化 | 热路径 | ~1 天量级，进现有 native 模块 |
| 10 | H3 缩略图代理常驻化 | 热路径 | 协议升级，缩略图墙吞吐质变 |

**Rust 总结**：值得做的只有两件——junction 解析器（进 `deskbox-native`，shortcut/recycle_bin 已验证该模式）和缩略图代理常驻 worker 化（改造现有 native 组件）。传输引擎、目录枚举、Everything、FolderWatcher、备份都不值得：IO/IPC-bound，瓶颈在内核或外部服务，C# 批量化即可。
