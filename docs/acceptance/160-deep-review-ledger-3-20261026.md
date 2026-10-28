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

## 拍板项复核（2026-10-26 复核轮，三路对抗复核探员+亲验）与执行结果

Simon 拍板「开始」后全部落地（批 R18-1/2/3 = 050dad21/ec978e6d/0d4a7830）：

### 已修（三批）
- **A-#24（050dad21）**：孤儿目录扫描 10 分钟 IPC 上界内联 UI 线程→池线程化。复核纠偏点：原登记把 27 处统称「冷路径」有事实偏差——B 类 2 处是暖路径（每次设置推送）、#24 违反自家 15s 有界契约。
- **A-#27（050dad21）**：folder-watcher-health 死线整删（宿主零发送；真实消费走 app.runtime-health 面直读主进程）。
- **A-#3（050dad21）**：GetRuntimeState 改推送喂缓存（构造 Prime+RegistrationChanged 失效+变更命令后刷新，R13-3 同款）。冻结棘轮 27→25。
- **E-1（ec978e6d）**：图标 in-process 回退 registry-only 化——复核修正了原方案（迁 proxy 不对，正解=USEFILEATTRIBUTES，WIC pinned 先例同构）；GetShellItemIconPngBytes 死码删（最后能绑 handler 的 PIDL 形态）。「第三方 Shell 代码不进 DeskBox 进程」从菜单单点原则升级为不变量。
- **C（0d4a7830）**：批量「打开全部」>15 确认门（13 语言键）+QC 落盘按需化。**复核证伪**：「数百内容窗」不成立（内容窗按 widget 实例单例复用）；真实危害=混合选区 N 次 Shell 连发。
- **D（0d4a7830）**：双计时器补全（nativeShellMs 搭车 DragProtocol）。**复核否决**了原探员方案（PointerPressed 预热=窗口双向无界+新鲜度绕回原点）；正确闭环=先测再拍板。

### 维持登记（措辞按复核修正后写入）
- **⑤设置窗位置持久化**：非缺陷（拆分前后行为一致，R12 已补会话内回屏钳制）；若做=仿搜索弹窗 SearchPopupCustom 四键+工作区交集校验先例（约 60 行）。纯产品偏好。
- **E-2 属性页**：v1.4.7 菜单迁移的**范围裁剪遗留**（非文档化取舍）——显式点击×稀有 handler×零现场案例，撑不起对话框宿主协议工程。复活条件=第一起属性页崩溃反馈。
- **F 组面摆放仲裁**：**结构性遗漏非有意设计**（`group is null` 从 7a1fa80a 第一天就在且无注释解释；1.5.9 用户承诺「过期快照不再覆盖用户最新摆放」为全称表述）。不修理由：①P4 半（兜底几何永不权威）已无歧视覆盖组面；②三复合条件+零现场反馈；③正确修法必须组级时钟字段，**勿用成员戳 max**（合并新成员会误清拓扑记忆）。复活条件=第一起组面跳变反馈或下次动 topology schema。
- **D 决策记录**：常走腿（本地 SSD 暖缓存约 1-10ms/次）预期结论「不重构」；唯一值得工程量的候选=broker 回退腿设界（超阈值路径数直接取消拖拽，.lnk 同款）。待 nativeShellMs 真实样本。

## 债登记（累积）

（空）
