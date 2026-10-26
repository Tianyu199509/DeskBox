# 160 深审第三轮 — 前两轮真未覆盖代码域账本（G1-G4，2026-10-26）

- 承接：第一轮（R13，设置拆分链+1.5.9 功能域）、第二轮（R15，九个 F 域）。
- 本轮范围=**覆盖普查后确认的真未覆盖部分**（全仓 848 个 .cs 文件 28 万行对照前两轮探员精读清单逐一排除）：
  - G1 窗口层六件：WidgetWindowBase.Collapse（4877 行=全仓第二大文件，折叠/紧凑引擎）/Bounds/Interaction + ContentWidgetWindow 三 partial（NativeDragDrop 1884/主文件/Commands）
  - G2 Rust 原生层（native/，9k 行）+ Helpers 代理五件：IconHelper（2953）/ShellThumbnailProxy/ShellContextMenuProxy/ShortcutNativeBackend/ShortcutHelper/WidgetTextShadow
  - G3 widget 层服务与控件：WidgetLayerService/WidgetTrayAnimationController/WidgetTopologyLayoutService/FolderWatcherService/GlanceImageService/MusicVolumeService/FileService.TransferProgress + WidgetGroupTitleSwitcher 三 partial（3243 行组切换器）/MarkdownDocumentView/MarkdownSourceEditor/QuickCaptureSurfaceContent（3269）
  - G4 WidgetViewModel 族四件：Stacks（2587）/Operations/SortingAndWatchers/ItemHydration
- 红线不变：零推送零发版；每批门=dev-restart+全量测试+quickcheck。

## G1：窗口层六件（探员完成，P1 修于 eb2b821a）

**P1-1（已修）**：组拖入回退释放路径 async void 无兜底（B 族遗留缺口，姊妹路径全设防唯它裸奔——前置面异常直上 dispatcher=进程崩溃）→ 补 catch；顺手 CacheGroupFileDropAsync 世代失配早退自清标志（脆弱间接链显式化）。折叠状态机判定健全（世代/令牌纪律贯穿、看门狗有界、无世代漏洞）；与 WidgetShell 呈现层无同一关切双写；事件订阅全对称零泄漏。**P2 登记**：`_compactState` 十态枚举=补丁残骸（20 写 1 读，建议显式化意图 bool 后退场）；Interaction 拖拽/缩放结束路径成对复制 ~120 行×2（未来改动必须双改）；EndCompactInteraction 同态分支重复视觉复位（Smart 展开窗每次交互零收益复位）；死码 GetMusicCompactProgress 已删+死条件已简化；四个 Compact*Requested async void 无 catch（低概率，随 P1 同族登记）。修一半扫描三批全完整。

## G2：Rust 原生层+代理六件（探员完成，P2×2 修于 eb2b821a）

**总判定：全仓质量最高区域之一**——ABI 边界纪律严格（先验信封/错误码三轴/COM 守卫全平衡/SAFETY 注释全覆盖）；panic 面除 unreachable! 外零风险；thumbnail-proxy 协议/超时/崩溃面完备；两个 Proxy spawn/挂死/重启链闭环；WidgetTextShadow=1.5.6 全新 Composition 实现（旧管理器 1.5.1 整删，无残留）。**已修**：quick_access unreachable! 改防御性返回（panic=abort 下 validate/dispatch 漂移=宿主无声死亡地雷）；C ABI 头补 MUSIC_VOLUME_MAX_INPUT_CHARS 镜像（三侧镜像一侧缺失）。**登记债**：图标提取最后一层回退+SHObjectProperties 属性页=Shell 代码隔离边界两个剩余缺口（明知保留、触达条件写明，迁 proxy 或白名单化待拍板）；quick_access/recycle_bin 60 行路径工具三件套重复（下次动这两个文件时顺手）；batch worker 客户端消失后多做一个提取（观察级不改）。

## G3+G4：widget 服务+VM 族（探员完成，P1+P2×4 修于 eb2b821a）

**P1-1（已修）**：拓扑字典写点进闸只做了四分之一（R15-2 修一半实锤——MarkSurfaceEntryAuthoritative 进了闸，但 ActivateCurrentTopology 两处+CaptureCurrentTopologyLayout 仍裸写，同一次组拖拽提交里第 3 步闸外改第 4 步闸内改同一字典）→ 三处统一进 MutateGraphUnderLock。**P2-1（已修）**：托管传输取消路径丢弃条目级完成回执（跨卷目录窗口的 [目录回执] 被批级重包覆盖=恢复/历史少一件）→ 取消 catch 补合并。**P2-3（已修）**：QC 内容面三处 async void 设防。**P2-5（已修）**：托盘动画公共入口异常逸出补 catch。死码三处删。**登记**：组面被排除在故意摆放仲裁外（需补组摆放时钟或决策记录）；托盘动画 Animate/BeginSharedAnimate 120 行镜像（抽共享核待后续）；MusicVolumeService C# 后端 RCW 计数不平衡（仅非 AOT 路径，GC 兜底）；Markdown 渲染递归无深度防护/重渲染空白窗；切换器三 partial 判状态所有权清晰零泄漏；MarkdownSourceEditor 撤销/大文档面判优。**正向**：WidgetLayerService/FolderWatcherService 状态机审读正确（P3 锁纪律/边角登记）。

## 待 Simon 拍板（累积）

1. G2 Shell 代码隔离边界两个剩余缺口（in-process 图标回退链+SHObjectProperties 属性页）：迁 proxy（彻底，成本高）vs 白名单化/登记维持——探员建议登记在案即可。
2. G3 组面摆放仲裁（补 WidgetGroupConfig 摆放时钟 vs 决策记录「组面按拓扑各自记忆属有意」）。
3. （前两轮遗留）批量全部打开上限（F6）、拖出测量闭环（F3）、R14 冷路径 27 处、⑤位置持久化。

## 债登记（累积）

（空）
