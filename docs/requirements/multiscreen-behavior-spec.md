# 多屏行为规格 v2：格子归属屏 + 按显示器组合记忆布局（2026-10-07 定案，已实现）

> **状态**：方案已定案（2026-10-07 Simon 批准第 1 节全部决策），**已实现（阶段 1-4 完成，随 1.5.6 发布）**。2026-10-08 按三路代码审查完成一轮修订（修正 2 处硬错误、1 处 API 语义依赖、补 6 项规则缺口与 4 个场景，明细见附录 E）；2026-10-09 发版前终审再修三处（MoveAll 撤销令牌重校准、重连自动展开条件、手动展开/收起清除断开标志）并将 6.3 文案表中的 ComboBox 长句改为短标签。真机验收矩阵见《multiscreen-behavior-spec-真机验证清单.md》。
> **读者**：负责实现的开发 agent。本文自包含：现状代码事实、已验证缺陷、目标行为、数据模型、算法、UI、文案、迁移、分阶段任务、测试与验收全部在此。
> **取代关系**：本文**取代** `docs/requirements/widget-screen-binding.md` 中的 R0-D1（"格子主屏幕"双层开关）、R0 中"拖拽/捕获不改变绑定模式"、R1 语义、R2 解析链、R3.1 菜单、R3.2 中的按钮文案、R4（"Unbound 零迁移 / 保留 ShouldFollowPrimaryMonitor"）。该文档的 **XAML 资源红线（R0）、测试与 AOT 红线（R5）、预览区视觉终案（R8，含 accent 选中态）仍然有效**，实现 UI 时必须遵守。本文 v1（2026-10-04 三路追踪版）的勘误见附录 B。
> **代码行号**：文中 `文件:行号` 为 2026-10-07 HEAD 的近似位置，开发中会漂移，以函数名为准。

---

## 0. 开发 agent 必读

### 0.1 一句话目标

格子"放在哪块显示器就属于哪块显示器"；每种"连接了哪些显示器"的组合各记一套摆放；显示器暂时不在时格子临时去别处、回来自动归位；只改分辨率/缩放/主显示器/排列时格子不跳屏。

### 0.2 阅读顺序

1. 第 1 节决策（不可再讨论，直接执行）。
2. 第 3 节目标行为场景表（= 验收标准）。
3. 第 5 节算法（实现依据）。
4. 第 8 节分阶段任务（按阶段提 PR，每阶段独立可合并）。
5. 第 9 节测试。

### 0.3 仓库开发约束（来自 `AGENTS.md`，必须遵守）

- 改完应用代码后：先停止本仓库路径下运行中的 `DeskBox.exe`，再构建，再从 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动新实例，并确认运行的是该路径。
- 测试命令（**必须带 x64**，不要先用 AnyCPU 跑）：
  `dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj --no-restore --verbosity:minimal -p:Platform=x64`
- 在 Windows 上执行命令请用 PowerShell（本机 bash 为不可用的 WSL）。
- 提交信息只用 `Simon <1047078635@qq.com>` 身份，**禁止** `Co-Authored-By`、`Generated with` 等任何工具署名（有 commit-msg 钩子与 CI 检查）。新克隆需 `git config core.hooksPath .githooks`。
- 不要删除/覆盖与本任务无关的用户改动和发布产物。

### 0.4 术语表（代码 ↔ 用户文案）

| 本文术语 | 含义 | 代码中的现有表示 | 用户可见文案 |
|---|---|---|---|
| 表面（surface） | 一个独立的桌面窗口单位：独立格子，或一个格子组（组成员共享一个表面） | `WidgetConfig` / `WidgetGroupConfig`；表面 ID 见 `WidgetTopologyLayoutService.ResolveGroupSurfaceId` | 格子 |
| 归属屏（home display） | 用户最近一次**主动**把该表面放上去的显示器 | `ScreenBindingMode == Pinned` + `BoundScreenId`（语义重定义，见 4.1） | "显示器 N"（菜单里打勾项） |
| 跟随主显示器 | 始终显示在 Windows 主显示器 | `ScreenBindingMode == FollowPrimary` | 始终在主显示器 |
| 归属未知 | 没有可用的稳定身份可记（ID 退化/远程会话/老数据） | `ScreenBindingMode == Unbound` | 不展示（菜单打勾当前所在屏） |
| 显示器组合（set） | 当前连接的显示器身份集合 | 现为 `WidgetTopologyLayoutService` 拓扑 key（v3，粒度更细） | 不展示 |
| 档案（profile） | 某个显示器组合下每个表面的摆放 | `WidgetTopologyLayoutProfile` | 不展示 |
| 档案条目（entry） | 档案中某个表面的摆放 | `WidgetSurfaceLayoutProfile` | 不展示 |
| 权威条目 | 由用户亲手摆放（或在归属屏上被捕获）写入的条目 | 新增字段，见 4.2 | 不展示 |
| 回退态 | 表面有归属屏但归属屏不在线，临时显示在别的屏 | 新增解析结果标志，见 5.1 | "原显示器 N（未连接）" |
| 主显示器 | Windows 主显示器（primary monitor） | `IsPrimary` | 主显示器（**不再使用"主屏""格子主屏幕"**） |

---

## 1. 决策记录（2026-10-07 Simon 批准）

| # | 决策 | 结论 |
|---|---|---|
| D1 | 用户心智模型 | **放哪属于哪**。拖动、菜单"移到显示器"、在某屏新建，都会把该屏设为归属屏。UI 不再出现"自动 / 固定"。保留"始终在主显示器"开关。 |
| D2 | 硬固定（拖动也不改归属） | **不提供**。防误拖由现有"锁定位置"承担。 |
| D3 | 显示器断开时默认行为 | **默认：把它的格子移到其他显示器**（保持可用）。可选：**收起为胶囊，重新连接后恢复**（对应 Windows"断开显示器时最小化窗口"）。 |
| D4 | 首次出现的新显示器组合如何播种 | **启发式**：原来在外接屏上的格子去另一块外接屏（按方位/尺寸匹配），原来在主显示器上的去主显示器。只用于该组合**首次**播种，之后以该组合自己的档案为准。 |
| D5 | 档案 key 粒度 | **只按显示器身份集合**。分辨率、缩放、主显示器、排列、任务栏都不再产生新档案。接受失去"横屏/竖屏各一套布局"。 |
| D6 | 新格子位置 | 设置项"新格子出现在"：**默认"鼠标所在的显示器"**，可选"主显示器"或指定某块显示器。新格子的归属屏 = 实际创建所在的屏（不再隐式"固定"成全局默认屏）。 |
| D7 | 批量操作 | "所有格子固定到此屏"改为一次性动作**"把所有格子移到这里"**：确认框说明数量，完成后 InfoBar 提供**撤销**。不再是永久锁定。 |
| D8 | 拓扑响应节奏 | 显示器消失给**宽限期**；锁屏/显示器关闭/全屏独占应用运行期间**只记录不应用**；启动时等显示器到齐；首次出现的组合存活一段时间才落盘。 |
| D9 | "根据显示器连接记住格子位置"开关 | **不提供**，行为常开。 |
| D10 | 术语 | 本功能区统一用"显示器"；Windows 主显示器统一叫"主显示器"；删除"格子主屏幕""主屏""固定到此屏"等说法。 |

---

## 2. 现状（代码事实）

### 2.1 现有三层结构

1. **绑定意图**：`WidgetConfig.ScreenBindingMode`（`Unbound`/`FollowPrimary`/`Pinned`）+ `BoundScreenId`，组表面存于 `WidgetGroupConfig` 并镜像到成员。写入点：`WidgetManager.ApplyScreenBindingAsync`（约 `WidgetManager.cs:1671`）、`PinAllWidgetSurfacesToScreenAsync`（约 `:1744`）、首次运行新建（约 `:1001-1024`）。
2. **拓扑档案**：`Services/WidgetTopologyLayoutService.cs`。key = SHA256（每块屏的 稳定ID;IsPrimary;DPI;显示器矩形）（`CreateTopologySignature` 约 `:215`），最多 12 套 LRU（`MaximumRetainedProfiles`）。激活入口 `Activate`（约 `:70`），由 `WidgetManager.RestoreWidgetPositionsAsync`（约 `WidgetManager.cs:1597`）与启动时 `RestoreWidgetsAsync`（约 `:841`）调用。
3. **运行时解析**：`WidgetPositioningService.SelectWorkAreaCore`（约 `:322`）：Pinned → FollowPrimary → **WasPrimary 智能跟随** → 稳定 ID → 设备名 → 工作区 key → 兜底。胶囊位置由 `WidgetCompactBoundsCalculator.Resolve`（约 `:55`）用胶囊自己的显示器字段再走一遍同一解析。
4. **拓扑变化协调**：`DisplayTopologyTransitionCoordinator`（180ms 观测间隔 × 连续 2 次相同签名即应用；失败最多重试 8 次）。信号来源：`DisplayAreaWatcherService`、各窗口 `WidgetDisplayChangeWatcher`、`AppLifecycleRecoveryWatcher`（已注册 WTS 会话通知与 `GUID_CONSOLE_DISPLAY_STATE`）。
5. **稳定 ID**：`Win32Helper.ResolveStableMonitorId`（约 `Win32Helper.cs:2552`），取 `EnumDisplayDevices(EDD_GET_DEVICE_INTERFACE_NAME)` 的 DeviceID（设备接口路径），退化时为 `\\.\DISPLAYn` 或 `unknown-display`（这两种被视为不稳定，从不参与匹配）。

### 2.2 已验证缺陷（2026-10-07 用临时单测在 HEAD 上实测，测试已删除；第 9 节要求把它们改写为回归测试）

| 编号 | 缺陷 | 复现 | 代码位置 |
|---|---|---|---|
| B1 | **两层解析优先级矛盾**。档案层把稳定 ID 排在 WasPrimary 之前，并在迁移时把 `PositionMonitorWasPrimary` 改写为目标屏的 IsPrimary；运行时层把 WasPrimary 智能跟随排在稳定 ID 之前。 | 双屏 A(主)/B，Unbound 格子在 A 且 WasPrimary=true、稳定 ID=A。主显示器切到 B：档案层结果=留在 A；同一配置直接交给运行时解析=跳到 B。实际表现：拓扑变化约 360ms 内若走非档案路径（如 F7 唤起），格子先跳到 B 再被协调器拉回 A。 | `WidgetPositioningService.cs` 约 `:354-373`；`WidgetTopologyLayoutService.cs` `SelectTargetMonitor` 约 `:712-736`、`MapToTopology` 约 `:445-448` |
| B2 | **分辨率/缩放/主显示器/排列都进 key，切回旧设置会复活旧布局**。 | 单屏 1080p 激活 → 改 2K（新 key，播种）→ 用户把格子边距改为 700/500 → 改回 1080p：格子边距回到 100/80（旧 1080p 档案），2K 下的修改丢失。 | `CreateTopologySignature` 约 `:215-231` |
| B3 | **新组合逐表面从"上一个激活档案"播种（签名完全相同的历史档案优先），回退态会传染**。 | 接坞 {L,E}，格子在 E → 拔坞 {L}（格子回退到 L）→ 接坞并多接一台电视 {L,E,TV}：格子留在 L，不回 E。 | `Activate` 约 `:83-127`（sourceProfile=上一个 ActiveKey；`FindCompatibleProfile` 签名匹配优先）、`SeedProfile` 约 `:312-330` |
| B4 | **胶囊位置有独立的显示器身份且迁移不回填**。主体迁移到 L 后，胶囊的 `PositionMonitorStableId` 仍为 E；老数据该字段为 null 时走设备名链，锁屏重编号可跳屏。 | 格子+胶囊在 E，拔掉 E：主体 stable=L，胶囊 stable=E、设备名=DISPLAY1。 | `MapToTopology` 约 `:452-460`（回填了设备名/key/WasPrimary，未回填稳定 ID）；`WidgetCompactBoundsCalculator.Resolve` 约 `:79-102` |
| B5 | **同 key 重投影有损**：工作区变化（任务栏改变等）时，`ReprojectSurfaces` 把夹取后的尺寸/边距写回档案；跨屏迁移时 `ClampLogicalSize` 把夹取结果存为意图。 | 代码审查 | `Activate` 约 `:134-141`；`MapToTopology` 约 `:435-444` |
| B6 | **"用户放置"入口分散，归属不随用户移动更新**。拖动结束只写位置不改绑定；Pinned 格子被拖到别屏后，任何恢复/唤起/重启都会弹回。入口有：标题栏拖动结束、拖动中丢失捕获、Ctrl 多选联动移动、胶囊条拖动跨屏、组拖动。 | 代码审查 | `WidgetWindowBase.Interaction.cs` `EndWindowDragCore` 约 `:399-462`、`DragPointerCaptureLostCore` 约 `:687-740`；`WidgetWindowBase.CoordinatedMove.cs` `CompleteCoordinatedMoveParticipation` 约 `:60-94`；`WidgetManager.CapsuleArrangement.cs` 约 `:250-300`、`ReanchorCapsuleBarToWorkArea` 约 `:881`；`WidgetManager.Groups.cs` `CompleteWidgetGroupDragAsync` 约 `:2219` |
| B7 | **外部移动被忽略**：系统快捷键（如 Win+Shift+方向键，格子取得前台时）或第三方窗口管理器移动格子，配置不更新，下次恢复弹回。 | 代码审查：非拖动/非调整大小时直接 return | `WidgetWindowBase.Bounds.cs` `OnAppWindowChanged` 约 `:644-651` |
| B8 | **归属判定用窗口中心点**，与系统语义（最大交集面积，`MonitorFromRect`，也是窗口 DPI 的归属依据）不一致。 | 代码审查 | `CapturePositionAnchor` 约 `WidgetWindowBase.Bounds.cs:529-532` |
| B9 | **新格子落点**：只有首次运行文件格使用"默认屏/鼠标所在屏"，且默认屏会**隐式 Pinned**；其余创建路径都用默认 `X=100,Y=100`（`ResetFeatureWidgetConfig` 显式写 100,100），经 `NormalizeWidgetBounds` 落到**包含 (100,100) 的显示器**（通常为主显示器左上）。 | 代码审查 | `WidgetManager.cs` `CreateManagedWidgetCoreAsync` 约 `:980-1061`；其余路径见 5.8 |
| B10 | **拓扑响应过急、无门控**：2×180ms 即应用；DP 显示器休眠被系统当作拔出（Rapid HPD）、锁屏期间多次重枚举、全屏游戏改分辨率、开机显示器晚到都会触发真实搬移和建档；协调器在交互中重试 8 次后放弃。 | 代码审查 | `DisplayTopologyTransitionCoordinator.cs` 约 `:11-19`、`:119-124` |
| B11 | **显示器只有编号和分辨率，没有名称**；编号为枚举顺序，可能与 Windows 设置里的编号不同。 | 代码审查 | `WidgetScreenCatalog.Capture` 约 `:28-58` |
| B12 | **术语冲突**："格子主屏幕""主屏""跟随 Windows 主屏"三词混用。 | 文案 | `Strings/*.json` 约 `:2750-2773` |

### 2.3 现状中正确且必须保留的行为

- 无显示器时拒绝建档、拒写（`Activate`/`CaptureCurrentSurface` 的 `Monitors.Count == 0` 与 activeKey 守卫）。
- 档案激活**永不改归属**（`ApplyToWidget`/`ApplyToGroup` 不写 `ScreenBindingMode`/`BoundScreenId`）——新方案里归属**只**在"用户放置提交"（5.4）中改变。
- 拓扑恢复期间不把物理坐标写回配置（`BeginDisplayTopologyTransition` + `TryRestoreBoundsForDisplayTopology(updateConfig:false)`，契约测试 `DisplayTopologyRestorationContractTests`）。
- 用户交互中延后恢复（`_sessionManager.IsInteractionActive`）。
- 同一物理屏仅改 DPI 时保持 DIP 尺寸与边距（`ratio = 1`）。
- 显示器全部消失时窗口不销毁；`NeedsInitialPlacement` 仅用于创建期无可用工作区。
- 隐藏窗口照常重定位（`allowHidden: true`），每条显示路径在显示前都会重新解析位置（`ShowLoadedWidgetWindow` 约 `WidgetManager.cs:1395`）。

---

## 3. 目标行为（用户可见规格 = 验收标准）

### 3.1 三条规则（对用户的全部解释）

1. 格子放在哪块显示器上，就属于那块显示器。
2. 每种"连接了哪些显示器"的组合，各自记住格子的摆放；显示器暂时不在时，格子临时去别的显示器（或按设置收起为胶囊），显示器回来后自动回到原位。
3. 可以让某个格子"始终在主显示器"。

### 3.2 场景表

记号：L=笔记本内屏，E/F=外接屏，TV=电视/投影。"归属"=归属屏。所有场景中**隐藏、收起、胶囊态、未创建窗口的懒加载格子**行为一致（未创建窗口的在创建时落到正确位置）。

| # | 前置 | 操作 | 期望结果 |
|---|---|---|---|
| S1 | {L,E}，W1 在 L、W2 在 E | 在 Windows 里把主显示器从 L 改为 E | 两个格子都不动（同一物理屏、同一位置）。"始终在主显示器"的格子移到 E。 |
| S2 | 同上 | 改 L 的分辨率或缩放，或在 Windows 中拖动显示器排列，再改回 | 格子留在原屏，按锚点保持相对位置；改回后与改之前**完全一致**。期间用户若挪了格子，改回后保留用户挪后的位置（不复活旧布局，修 B2）。 |
| S3 | {L,E}，W2 在 E（归属 E） | 拔掉 E（断开行为=默认"移到其他显示器"） | 宽限期（约 4 秒）后 W2 出现在 L 上：按 D4 启发式（E 不在 → 去 L），尺寸按两屏有效尺寸比例缩放；归属仍为 E。 |
| S4 | S3 之后 | 重新接上 E | W2 回到拔前在 E 上的位置与尺寸，与拔前完全一致。 |
| S5 | S3 之后（W2 临时在 L） | 在 L 上把 W2 拖到另一个位置，然后接回 E | 拖动只写 {L} 组合的档案，归属仍为 E；接回后 W2 回到 E 的原位。再次拔掉 E 时，W2 出现在用户上次在 L 上拖到的位置。 |
| S6 | {L,E}，W2 在 E | 把 W2 拖到 L 并松手 | 归属立即变为 L；之后拔插、重启、唤起都保持在 L。 |
| S7 | {L,E}，W 为"始终在主显示器"（L 为主） | 把 W 拖到 E | 拖动经过 E 时提示"松开后将不再跟随主显示器"；松手后 W 改为归属 E。 |
| S8 | 接坞 {L,E}，W2 在 E → 拔坞 {L} | 接坞的同时多接一台电视 {L,E,TV}（该组合首次出现） | W2 回到 E（从 W2 最近一次的权威条目播种，修 B3）；L 上的格子留在 L；电视上暂无格子。 |
| S9 | 家里 {L,E}，W2 在 E | 到公司接另一块外接屏 {L,F}（首次出现） | 按 D4 启发式：W2 出现在 F（外接→外接，方位/尺寸最接近者），L 上的格子留在 L；归属仍为 E。回家接 E 后 W2 回 E。 |
| S10 | {L,E} | 合盖仅用外接屏 {E}（或 Win+P"仅第二屏幕"） | L 上的格子按 D4 去 E（原来在主显示器上的去当前主显示器）；E 上的不动；开盖回到 {L,E} 时全部归位。 |
| S11 | {L,E} | DP 显示器休眠被系统当作拔出、数秒后又出现；或切换 KVM / 显示器输入源后很快切回 | 宽限期内恢复 → **格子完全不动**（修 B10）。 |
| S12 | 任意 | 锁屏或睡眠期间显示器多次重枚举，解锁/唤醒后拓扑与锁屏前相同 | 格子不动；锁屏期间不应用任何中间态。若最终拓扑不同，解锁后一次性应用。 |
| S13 | 任意 | 全屏独占游戏把分辨率临时改为 1280×720，退出后恢复 | 游戏运行期间不重排、不建档；退出后若拓扑与之前相同，格子不动。 |
| S14 | 开机时扩展坞/DisplayLink 显示器晚几秒才出现 | 启动 DeskBox | 不出现"先全部挤到 L、再搬回 E"的过程；最多等待约 5 秒后按最终组合摆放。 |
| S15 | 任意 | 复制屏模式（Win+P"复制"），投影分辨率为 1024×768 | 视为一个新组合（单逻辑屏），首次按 D4 播种；切回扩展时全部归位。 |
| S16 | 远程桌面登录 | 在远程会话中拖动格子 | 只写远程会话这个组合的档案；**不改归属屏**；回到本机物理屏时全部归位。 |
| S17 | 两块同型号显示器 | 交换两根线的接口 | 设备接口路径随接口变化 → 视为交换了身份（与 Windows 一致，可接受）。阶段 5 的 EDID 序列号匹配在序列号唯一时可识别并纠正。 |
| S18 | 显卡切换（独显直连）或驱动更新导致内屏设备接口路径改变 | 重启后 | 视为新组合，按 D4 播种（L 上的格子仍落在内屏）；阶段 5 的 EDID 匹配可让它沿用旧档案。 |
| S19 | {L,E} | 右键格子 → 移到显示器 → 显示器 2 | 格子移到 E（按比例映射到 E 上），归属=E。 |
| S20 | {L,E} | 设置页选中 E → 把所有格子移到这里 → 确认 | 所有表面（含隐藏、收起、组、"始终在主显示器"的格子）移到 E，归属=E；InfoBar 提供"撤销"，撤销后恢复每个表面原来的归属、模式与位置。 |
| S21 | 任意 | 从托盘/快捷键/菜单新建格子（设置="鼠标所在的显示器"） | 新格子出现在鼠标所在的显示器（右上对齐默认位置，与已有新格子级联错开），归属=该屏。 |
| S22 | 设置="指定显示器 E"，但 E 未连接 | 新建格子 | 回落到鼠标所在的显示器；设置项显示"E（未连接）"但保留选择。 |
| S23 | {L,E}，断开行为="收起为胶囊" | 拔掉 E | E 上可收起的格子在 L 上以胶囊出现（不占大面积）；接回 E 后自动展开并回到 E 原位。若用户在断开期间手动展开/收起过某个格子，则该格子不再自动展开。 |
| S24 | 格子被 Win+Shift+方向键或第三方窗口管理器移到另一块屏（阶段 2 可选项，见 5.5） | — | 视同用户拖动：归属更新为新屏，不被弹回。 |
| S25 | 设置文件恢复到另一台电脑 | 启动 | 归属屏 ID 在新机器上不存在 → 按回退与 D4 启发式摆放，不报错、不丢格子；在新机器上第一次拖动即确定新归属。 |
| S26 | 仅一块显示器 | 打开右键菜单 / 设置页 | 右键菜单不显示"移到显示器"；设置页隐藏"把所有格子移到这里"和"各格子所在显示器"分区（见 6.3）。 |
| S27 | 设置页「显示器与格子」打开 | 拔/插显示器 | 预览画布、屏信息卡、各格下拉、徽标立即刷新（分区订阅拓扑变化，见 6.3 实现约束；现状只在 Loaded/主题/自身操作时刷新，拔插后全部陈旧） |
| S28 | 「识别」overlay 正在显示 | 拔/插显示器 | overlay 立即全部关闭（或按新拓扑重建），不残留在已消失的屏上（现状 overlay 仅创建时定位） |
| S29 | 两块稳定 ID 退化且几何相同的屏（远控/虚拟屏混合） | 任意 | **已知限制**：geo token 相同、身份不可区分。有归属的表面按 HomeId 解析不受影响；无归属的保持 Unbound，"放哪属于哪"退化为"最后位置+兜底"；不崩溃、不串档。EDID 二级身份（阶段 5）在序列号唯一时部分缓解 |
| S30 | 「把所有格子移到这里」完成、撤销前 | 拔掉目标屏 | 撤销仍可用：按快照恢复全部归属/模式/条目，实际位置按当前拓扑解析（5.9） |

---

## 4. 数据模型

### 4.1 表面意图（`WidgetConfig` 与 `WidgetGroupConfig`）

**不改字段名与 JSON 值**（保证老版本可读、降级可用），只重定义语义：

| 字段 | 新语义 |
|---|---|
| `ScreenBindingMode = Pinned` + `BoundScreenId` | **有归属屏**。`BoundScreenId` = 归属屏的稳定 ID。迁移后绝大多数表面处于此态。 |
| `ScreenBindingMode = FollowPrimary` | 始终在主显示器。`BoundScreenId` 为 null（与现状一致；关闭"始终在主显示器"时归属屏取当时所在屏，见 5.10）。 |
| `ScreenBindingMode = Unbound` | 归属未知（仅过渡态）：当前所在屏的稳定 ID 退化、远程会话、或无法推断。下一次用户放置提交时获得归属。 |

新增字段（`[JsonIgnore(Condition = WhenWritingNull)]`）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `DisconnectCollapsedForScreenId` | `string?` | 仅用于 D3 可选行为：该表面因归属屏断开而被**系统**收起时记录归属屏 ID；归属屏回来时据此自动展开。用户手动展开/收起即清空。组表面记在 `WidgetGroupConfig`。 |

模型注释（`WidgetConfig.cs` 约 `:68-81`、`WidgetScreenBindingMode` 枚举注释、`WidgetGroupConfig.cs` 约 `:52-58`）需改写为上述语义。

### 4.2 档案条目（`WidgetSurfaceLayoutProfile`，`Models/WidgetTopologyLayoutModels.cs`）

新增：

| 字段 | 类型 | 说明 |
|---|---|---|
| `IsAuthoritative` | `bool?` | `true` = 用户亲手摆放写入，或在归属屏上被捕获；`false` = 播种/回退推导出的；`null` = 迁移前的老数据（迁移时按 7.1 赋值）。 |
| `AuthoredAtUtc` | `DateTimeOffset?` | 最近一次成为权威条目的时间。 |

规则：
- **只有** 5.4 的"用户放置提交"和 5.9/5.10 的显式动作会把条目设为权威并更新 `AuthoredAtUtc`。
- `CaptureAllSurfaces` / `CaptureCurrentSurface` / 播种 / 重投影**从不提升**权威标志；重写条目时**沿用旧条目的标志**（新条目默认 `false`）。例外：首次运行的初始捕获（`initialCapture`，无任何旧档案）写 `true`，因为那是用户现有布局。
- 档案条目只保存**相对显示器的意图**：显示器身份字段 + 锚点 + 边距（DIP）+ 尺寸（DIP）+ 胶囊锚点/边距。物理 X/Y 仅作"上次实际位置"缓存，不作为意图。
- 尺寸/边距的贴边夹取只在计算实际位置时做（5.2），**不得写回条目**（修 B5）。跨屏首次映射（播种）产生的比例缩放结果写入条目，但标记为非权威。

### 4.3 档案（`WidgetTopologyLayoutProfile`）

- 阶段 3 起 key 改为 **v4 = 显示器身份集合**（5.6）。`Monitors` 字段继续保存最近一次看到的每块屏元数据（位置、主显示器、分辨率、DPI、工作区），用于播种时的方位/尺寸启发式。
- 新增 `IsProvisional`（`bool`，不落盘时为 true）与 `FirstSeenAtUtc`（`DateTimeOffset`）：首次出现的组合先只存在内存中，满足 5.6 的条件后才落盘。
- LRU 上限保持 12。

### 4.4 胶囊位置（`WidgetCompactPlacement`）

- 一个表面**只属于一块显示器**。胶囊的 `PositionMonitorStableId/DeviceName/Key/WasPrimary` 一律视为**派生字段**：每次表面的显示器身份字段被写入（提交、映射、迁移）时同步为与表面相同的值（修 B4）。
- `WidgetCompactBoundsCalculator.Resolve` 解析胶囊位置时使用**表面**的显示器（不再用胶囊自己的身份字段决定屏），胶囊只贡献锚点和边距。

### 4.5 全局设置（`WidgetLayoutSettingsSlice` / `AppSettings`）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `WidgetNewPlacementTarget` | 新枚举 `WidgetNewPlacementTarget { CursorDisplay, MainDisplay, SpecificDisplay }`（字符串序列化，未知值降级为 `CursorDisplay`，仿 `WidgetScreenBindingModeJsonConverter`） | `CursorDisplay` | "新格子出现在" |
| `WidgetDefaultBoundScreenId` | 现有 `string?` | — | 保留，语义改为 `SpecificDisplay` 的目标屏 ID；不再导致新格子"固定"。 |
| `WidgetDisplayDisconnectBehavior` | 新枚举 `{ MoveToRemaining, CollapseToCapsule }` | `MoveToRemaining` | "显示器断开时" |

新字段需加入 `SettingsService` 的默认值保留表（约 `SettingsService.cs:379-383`，`WidgetDefaultBoundScreenId` 已是 `UserData`；两个新字段按偏好处理，参照同表其它偏好项的分类）。

### 4.6 显示器身份

- **主身份**：现有稳定 ID（设备接口路径），不变。退化值（`\\.\DISPLAYn`、`unknown-display`）永不参与匹配，也**不得**写成归属屏。
- **显示名称**（阶段 4）：通过 CCD API 取 `DISPLAYCONFIG_TARGET_DEVICE_NAME`（`DisplayConfigGetDeviceInfo`，type=`DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME`）的 `monitorFriendlyDeviceName`，用 `monitorDevicePath` 与稳定 ID 对应。`outputTechnology` 为内置（`DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL`）且名称为空时显示"内置显示器"；仍为空则显示分辨率。现有 `Platform/Win32Helper.DisplayTiming.cs` 已有 `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo`（取源名称）的 P/Invoke，可扩展。**不要**用 WinRT `DisplayMonitor`（AOT/裁剪风险，且 CCD 已够用）。
- **EDID 二级身份**（阶段 5，可选）：EDID 厂商+型号+序列号；序列号为 0/缺失时不生成。仅当当前在线显示器中**恰好一块**匹配、且该屏的主身份未被其它存储引用时，才把旧 ID 视为该屏（PowerToys FancyZones 0.60 因 EDID 序列号重复导致布局串屏后回退，见附录 A）。

`WidgetScreenInfo`（`WidgetScreenCatalog.cs`）增加 `FriendlyName`（阶段 4）。

---

## 5. 算法规格

### 5.1 统一显示器解析器（阶段 1，修 B1）

新建纯函数服务 `Services/DisplayPlacementResolver.cs`（无 Win32 调用，便于单测），**`WidgetPositioningService.SelectWorkAreaCore`、`WidgetTopologyLayoutService`（映射/播种）和 `WidgetCompactBoundsCalculator` 必须全部改用它**，不得各自保留优先级逻辑。

输入：
- 意图：`Mode`、`HomeId`（`BoundScreenId`，FollowPrimary 时忽略）。
- 条目的显示器引用：`StableId`、`DeviceName`、`WorkAreaKey`、`WasPrimary`、以及（若可得）条目被捕获时那块屏的几何（来自档案 `Monitors`；运行时可从 `WorkAreaKey` 解析）。
- 当前在线显示器列表（稳定 ID、设备名、显示器矩形、工作区、IsPrimary、DPI）。

输出：目标显示器 + `Reason`（`FollowPrimary | Home | Entry | Heuristic | Primary`）+ `IsFallback`（= 有 HomeId 且模式不是 FollowPrimary 且 Reason ≠ Home）。

优先级（**严格按此顺序**）：

1. `Mode == FollowPrimary` → 主显示器。
2. `HomeId` 非退化且在线 → 归属屏。
3. 条目的显示器在线：
   - 条目 `StableId` 非退化 → 只按稳定 ID 匹配；不在线则**直接进入第 4 步**（不再用设备名/工作区 key，避免 `\\.\DISPLAYn` 重编号后串屏）。
   - 条目 `StableId` 为空或退化（老数据）→ 依次按设备名、工作区 key 精确匹配。
4. 启发式（D4）：
   - a. 条目所在屏当时是主显示器（`WasPrimary == true` 或源几何 `IsPrimary`）→ 当前主显示器。
   - b. 否则在当前**非主**显示器中选：与主显示器的相对方位相同者优先（比较屏中心相对主显示器中心的主导轴方向：左/右/上/下）；再按尺寸（DIP 宽高）最接近；再按设备名相同。
   - c. 没有非主显示器 → 主显示器。
5. 主显示器（兜底；若没有主显示器标志则取第一块）。

删除旧的运行时"WasPrimary 智能跟随排在稳定 ID 之前"逻辑（`SelectPrimaryWorkAreaForSmartMode` / `ShouldFollowPrimaryMonitor` / `SavedMonitorLooksLikePrimary`，约 `WidgetPositioningService.cs:453-495`）。WasPrimary 只在第 4a 步作为"屏不在时去哪"的依据。

### 5.2 实际位置计算（realize）

`RealizeBounds(entry, display)`：锚点 + 边距（DIP）+ 尺寸（DIP）× 该屏 DPI → 物理矩形；尺寸夹到该屏工作区以内；`EnsureVisible`。**纯函数，不修改条目和配置中的意图字段**（`Width/Height/PositionAnchor/PositionMarginX/Y`）。物理 `X/Y` 可作为"上次实际位置"缓存写回配置。

阶段 3 需同时检查 `RestoreBoundsForCurrentTopology`（`updateConfig` 默认 true，约 `WidgetWindowBase.Bounds.cs:591-600`）一类路径：它们只允许更新物理 `X/Y` 缓存，不允许改写 `Width/Height`/锚点/边距。

### 5.3 激活与播种（阶段 1 改播种来源，阶段 3 改 key 与无损重投影）

激活某个组合时，对每个表面 s 求其条目：

```
entry = profile.Surfaces[s]（若存在）
if entry 存在:
    if s.Mode == FollowPrimary and entry 的屏 ≠ 当前主显示器: entry = Seed(s)
    elif s.HomeId 在线 and entry 的屏 ≠ HomeId:              entry = Seed(s)
    else: 使用 entry
else:
    entry = Seed(s)

Seed(s):
    A = s 在所有档案中的权威条目，按 AuthoredAtUtc 降序
    if s.Mode == FollowPrimary:
        src = A[0]（无则当前配置）；目标 = 主显示器
    elif s.HomeId 非空:
        src = A 中位于 HomeId 上的最新者；无则 A[0]；无则当前配置
        目标 = HomeId 在线 ? HomeId : DisplayPlacementResolver 第 4~5 步（以 src 所在屏为源屏）
    else（无归属）:
        if A 中存在其屏当前在线的条目: src = 其中最新者；目标 = 该屏
        else: src = A[0]（无则当前配置）；目标 = DisplayPlacementResolver 第 4~5 步
    目标 == src 所在屏 → 直接复制（ratio = 1）；否则按比例映射
    结果条目 IsAuthoritative = (目标 == src 所在屏 且 src 为权威)，否则 false
```

为什么有归属的表面在归属屏离线时以"归属屏上的权威条目"为源、而不是以"回退态中被拖过的条目"为源：回退态中的调整只属于那个组合（S5），不代表用户改变了格子属于外接屏的意图；对首次出现的新组合，应按 D4 从归属屏上的真实摆放出发做"外接→外接"映射（S9）。已存在的组合档案中的条目在归属屏离线时照常使用（S5"再次拔掉 E 时出现在上次拖到的位置"）。

**"条目屏"的判定**（上面伪码两处使用）：条目的 `PositionMonitorStableId` 经 Trim + 忽略大小写规范化后与目标屏稳定 ID 比较；条目稳定 ID 为空或退化（`\\.\DISPLAYn` / `unknown-display`）时视为"无法判定"，按 ≠ 处理（走 Seed 重建）。为避免全退化环境下"每次激活都重建、权威条目被 Seed 重写"，5.4 提交时必须把条目的 `PositionMonitorStableId` 强制写为落点屏的稳定 ID（落点屏退化时写 `HomeId`），保证有归属的表面条目屏始终可判定。

- "按比例映射"沿用现有 `MapToTopology` 的有效尺寸比例与锚点重算；同一物理屏 ratio = 1。
- 映射时必须同步胶囊显示器字段（4.4）。
- 阶段 1 先把 `Activate` 中"新档案整体从上一个激活档案播种"（`SeedProfile`、`EnsureMissingSurfaces`）替换为上面的逐表面 `Seed`；key 仍为 v3。阶段 3 把 key 改为 v4（5.6）。
- 激活**永不改**归属/模式（保持 `TopologyProfileActivation_RepositionsButNeverRebinds` 通过）。D3 的系统收起只改收起状态，不改归属。

### 5.4 用户放置提交 `CommitUserPlacement`（阶段 1，修 B6/B8）

新增统一入口（建议放在 `WidgetManager`，例如 `WidgetManager.ScreenHome.cs`）：

```
CommitUserPlacement(surface, finalPhysicalBounds, source)
  // source ∈ { Drag, CoordinatedMove, CapsuleBarDrag, GroupDrag, Resize, MenuMove, MoveAll, External, Create }
  display = 与 finalPhysicalBounds 交集面积最大的显示器（自实现：对捕获的每块屏计算 |bounds ∩ monitor| 面积取最大；并列时取交集占窗口面积比例大者，再并列取含窗口中心者；全部零交集 → GetFromPoint(中心, Nearest)。WinUI `DisplayArea.GetFromRect` 文档未定义跨屏矩形的裁决语义，不要依赖它做归属判定；Win32 `MonitorFromRect` 是"最大交集面积"的官方语义参照）
  1. 以 display 的工作区捕获锚点/边距/尺寸，写入配置（现有 CapturePositionAnchor + UpdateConfigBoundsFromPhysical 逻辑，
     但显示器判定改为最大交集面积）
  2. 写当前激活档案中该表面的条目：IsAuthoritative = true，AuthoredAtUtc = now
  3. 归属更新（远程会话 GetSystemMetrics(SM_REMOTESESSION) ≠ 0 时跳过本步）：
       if display 的稳定 ID 退化: 不改归属
       elif Mode == FollowPrimary:
           if display ≠ 主显示器: Mode = Pinned, HomeId = display
       elif HomeId 为空: Mode = Pinned, HomeId = display
       elif display ≠ HomeId and HomeId 在线: HomeId = display
       else（HomeId 不在线 = 回退态中的调整）: 不改归属
  4. 组表面：写 WidgetGroupConfig，并把 Mode/HomeId 镜像到所有成员（同现有 ApplyScreenBindingAsync 的镜像方式）
  5. 同步胶囊显示器字段（4.4）
  6. 若 DisconnectCollapsedForScreenId 非空且本次是用户操作：清空它
  7. 保存（SaveDebounced）；记录日志 [ScreenHome] surface=… from=… to=… source=…（仅归属变化时）
```

**必须接入的调用点**（全部在"用户确认移动结束"之后调用一次，不在每帧调用）：

| 入口 | 位置 |
|---|---|
| 标题栏拖动结束（`hasMoved`） | `WidgetWindowBase.Interaction.cs` `EndWindowDragCore` 约 `:429-435`（组宿主窗口也走这里——**组的移动在此检测组宿主并对组表面提交一次**，不要挂在 `CompleteWidgetGroupDragAsync`，那是合并建组路径） |
| 拖动中途丢失捕获（`hasMoved`） | 同文件 `DragPointerCaptureLostCore` 约 `:714-720` |
| Ctrl 多选联动移动：每个参与者 | `WidgetWindowBase.CoordinatedMove.cs` `CompleteCoordinatedMoveParticipation` 约 `:64-81` |
| 胶囊条拖动跨屏：条上每个胶囊 | `WidgetManager.CapsuleArrangement.cs` `CompleteCapsuleBarDrag` 约 `:365-395`（`:388` 写 CompactPlacement；重排逻辑 `ReanchorCapsuleBarToWorkArea` 约 `:284-363`） |
| 胶囊展开条整体拖动（hover 展开态随拖移动） | `MoveCapsuleBarFromExpandedWidget` 约 `:397` 起（调用点 `WidgetWindowBase.Collapse.cs:3661`）：拖动结束时对**条上每个胶囊**提交——只有被拖格子的 EndWindowDragCore 不够，兄弟胶囊是程序性落点 |
| 合并建组的新表面首次落位 | `WidgetManager.Groups.cs` `CompleteWidgetGroupDragAsync` 约 `:2219`（仅建组提交，非组移动） |
| 拖出（detach）落点 | `WidgetManager.Groups.cs` `PlaceDetachedMember` 约 `:2395` 的 `detachedPosition` 分支（用户拖放点=用户放置；现状该分支继承组归属，跨屏拖出后会被弹回——必须改为提交新归属。组解散的级联摆放约 `:1913` 是程序性的，不提交） |
| 调整大小结束 | `WidgetWindowBase.Interaction.cs` `CommitInteractiveResizeBounds` 约 `:592`（source=Resize；通常不跨屏，但规则一致） |
| 右键"移到显示器"/设置页单个格子改屏 | 5.10 |
| 把所有格子移到这里 | 5.9 |
| 新建 | 5.8 |
| 外部移动（阶段 2 可选） | 5.5 |

**不得调用**的路径：拓扑恢复、托盘显示/隐藏动画、胶囊自动收起/展开过程中的位置结算、`NormalizeWidgetBounds`、`PlacePendingInitialWidgets` 之外的程序化重排（自动胶囊排列除外：只有用户拖动胶囊条才算用户放置）。

`IsPositionLocked` 只阻止指针拖动；菜单与设置页的显式移动仍然允许。

拖动中的提示（S7）：拖动"始终在主显示器"的格子经过非主显示器时，用现有拖动引导层（`ResizeGuideOverlay`）显示一行提示（文案见 6.4）。若引导层不便扩展，可以不显示提示，行为不变。

### 5.5 外部移动采纳（阶段 2，可选，需真机验证）

在 `OnAppWindowChanged`（或 `WM_WINDOWPOSCHANGED` 子类）中检测：位置变化发生在 **非** `IsApplyingBounds`、非托盘动画、非拓扑过渡、非宽限期、非拖动/调整大小、且距离最近一次拓扑变化 ≥ 2 秒时 → 防抖 300ms → 若窗口所在显示器改变或位移超过 48 物理像素 → `CommitUserPlacement(source: External)`。系统在显示器移除时自动挪动窗口的情况必须被上述条件排除。若真机验证误判率高，保持现状（忽略外部移动），在此记录结论。

### 5.6 拓扑响应节奏与门控（阶段 2，修 B10）

在 `DisplayTopologyTransitionCoordinator` 中加入"应用门"（gate）。门关闭时只记录最新签名、**不调用** `restoreAction`、**不消耗重试次数**；门打开时自动 `RequestRestore("gate-open")`。

| 关门原因 | 判定 | 开门条件 |
|---|---|---|
| `SessionLocked` | WTS 锁屏/注销/远程断开（`AppLifecycleRecoveryWatcher` 已收到，约 `:142-147`，目前只写日志） | 解锁/登录/远程重连 |
| `DisplayOff` | `GUID_CONSOLE_DISPLAY_STATE` Data = 0（关闭）；现有分类器只识别"开"（`AppLifecycleRecoverySignalClassifier` 约 `:47-52`），需补"关" | Data = 1（开）；Data = 2（变暗）不算关 |
| `FullscreenApp` | `SHQueryUserNotificationState` 返回 `QUNS_BUSY(2)` / `QUNS_RUNNING_D3D_FULL_SCREEN(3)` / `QUNS_PRESENTATION_MODE(4)`；门关闭期间每 2 秒轮询一次 | 返回其它值 |
| `RemovalGrace` | 新组合是当前激活组合的**真子集**（只有显示器消失、没有新增）| 宽限到期（初值 4 秒）；或消失的屏回来（此时取消，什么都不做）；或用户唤起格子（F7/托盘显示）、开始拖动、在设置页执行移动 → 立即结束宽限并应用 |
| `StartupSettling` | 应用启动后，若当前组合是上次激活组合的真子集 | 缺失的屏到齐，或最长 5 秒 |
| `UserInteraction` | `_sessionManager.IsInteractionActive`（拖动/缩放/胶囊排列交互中） | 交互结束即开门。门关闭期间**只记录、不消耗重试预算**——现状长拖动可在 8×180ms 内耗尽协调器重试后放弃（`WidgetManager.cs:1604`、协调器 `:119-124`），此门正是"拖动立即结束宽限"与重试预算咬合的缺失环节 |

其它规则：
- **StartupSettling 必须挡住启动直通**：启动恢复走 `RestoreWidgetsAsync`（`WidgetManager.cs:841`）**直调** `ActivateCurrentTopology`，不经过协调器的 restoreAction——把门只做在协调器里拦不住它。实现须让该首次 Activate 同样受门约束（推荐把启动首次激活改经协调器，或在 `RestoreWidgetsAsync` 内先查门）。
- "真子集"按**显示器身份集合**比较（当前快照的稳定 ID 集合 vs 激活档案 `Monitors` 的稳定 ID 集合；退化 ID 按 `geo:` token 比较），不比较 v3 key——阶段 2 可能先于阶段 3 上线。激活档案的 `Monitors` 在拔屏宽限期内不会被覆盖（`CaptureCurrentSurface` 有 activeKey 守卫，`Activate` 仅在真正激活新档案时覆盖），子集判定数据可靠。
- 新增显示器：保留现有 2×180ms 稳定判定。
- **首次出现的组合**：创建为 `IsProvisional` 档案（内存中），满足任一条件才落盘：存活 ≥ 10 秒；或用户在其中做了放置提交。临时档案不参与 LRU 淘汰。
- 以上时长定义为具名常量；新增日志 `[DisplayTopology] gate closed/opened reason=…`、`grace start/cancel/expire elapsed=…ms`，用于真机校准。
- 阶段 3 key v4：`"v4-" + Hex(SHA256(join("|", sort(每块屏的 token))))[0..24]`，token = 规范化稳定 ID（大写、Trim）；稳定 ID 退化的屏用 `geo:{MonitorWidth}x{MonitorHeight}`。**不含**位置、主显示器、DPI、工作区。同 key 下这些元数据变化时只更新 `profile.Monitors` 与条目中的身份提示字段（`PositionMonitorDeviceName/Key/WasPrimary`），**不改写条目的尺寸/边距**（替换现有 `ReprojectSurfaces` 的有损逻辑），然后对所有窗口重新计算实际位置。两块稳定 ID 退化且几何相同的屏会得到相同 geo token（身份不可区分，见 S29 已知限制与 7.2 的空/退化 Monitors 处置）。

### 5.7 断开时收起为胶囊（阶段 4，D3 可选行为）

设置 = `CollapseToCapsule` 时，在激活新组合 S_new（旧组合 S_old）时：

- 候选表面：可见；非 FollowPrimary；其归属屏（无归属时取条目的屏）H ∈ S_old 且 H ∉ S_new；其有效收起行为允许收起（`EffectiveCollapseBehavior != Expanded`）；当前未收起。
- 动作：以**非持久化手动状态**收起（`WidgetWindowBase.Collapse.cs` 中的 `SetCollapsedState(true, persistManualState: false, ...)` 一类入口，与 `CollapseWidgetFromHost` 同源），设置 `DisconnectCollapsedForScreenId = H`。胶囊位置取 S_new 档案中该表面的条目（按 5.3 播种）。
- H 回来（激活的组合包含 H）且 `DisconnectCollapsedForScreenId == H` 且仍处于收起态 → 展开，清空标志。
- 用户在断开期间手动展开或收起该表面 → 清空标志（不再自动展开）。
- 有效收起行为为"始终展开"的表面按默认行为（移到其他显示器）处理。隐藏的表面不改可见性。组表面按组收起。

### 5.8 新格子放置漏斗（阶段 1，修 B9）

新增 `ApplyNewWidgetPlacement(WidgetConfig config, NewWidgetPlacementContext context)`，**在 `Settings.Widgets.Add(config)` 之前**调用。仅用于全新配置（重新启用已存在的功能格保持原位置）。

目标显示器：
1. `context.ExplicitBounds`（整理建格的计划矩形）→ 该矩形最大交集的显示器，位置用计划矩形；
2. `context.SourceSurfaceId`（从某个格子内部发起的新建）→ 该表面当前所在显示器；
3. 设置 `SpecificDisplay` 且该屏在线 → 该屏；`MainDisplay` → 主显示器；`CursorDisplay` 或指定屏不在线 → 鼠标所在显示器（`GetCursorPos` → `DisplayArea.GetFromPoint(..., Nearest)`）；
4. 无可用工作区 → 维持现有 `NeedsInitialPlacement = true` 机制。

位置：除 1 外，统一用 `InitialFileWidgetPlacementPolicy.CalculateRightAlignedBounds`（右上对齐，右 24 / 上 72 DIP），再按该屏上已有的同位置格子级联错开 24 DIP（最多 8 级，参照 `PlacePendingInitialWidgets` 约 `WidgetManager.cs:1861`）。注意：这会改变非首次运行新建格子的默认位置（原为主显示器左上 100,100），属于有意变更。

归属：`Mode = Pinned, HomeId = 目标屏`（目标屏 ID 退化或远程会话时保持 `Unbound`）。档案条目写为权威。

必须接入的创建路径：

| 路径 | 位置 |
|---|---|
| 新建文件格（含首次运行；删除原"默认屏 → 隐式 Pinned"分支，统一走漏斗） | `WidgetManager.cs` `CreateManagedWidgetCoreAsync` 约 `:980-1061` |
| 按类型新建（默认分支的注册格） | `CreateWidgetOfKindAsync` 约 `:1091-1100` |
| 映射文件夹格 | `CreateFolderWidgetAsync` 约 `:1108-1137` |
| 快速记录（仅新配置） | `WidgetManager.FeatureWidgets.cs` `CreateOrShowQuickCaptureWidgetAsync` 约 `:86-97` |
| 待办（新建） | `CreateTodoWidgetAsync` 约 `:147-158` |
| 单例功能格（天气/搜索/音乐等新建） | `CreateSingletonContentFeatureWidgetAsync` 约 `:393` 起 |
| 速览格（Glance） | `CreateGlanceWidgetAsync` 约 `:460` 起 |
| 重置功能格（新建默认配置） | `ResetFeatureWidgetAsync` / `CreateDefaultFeatureWidgetConfig` 约 `:1076-1205` |
| 恢复孤儿托管文件夹（批量，级联） | `WidgetManager.Storage.cs` `RestoreOrphanManagedStorageFoldersAsync` 约 `:125-169` |
| 桌面整理建格（`ExplicitBounds`） | `DesktopOrganizationTransaction.cs` `CreateCandidateWidgets` 约 `:445-475` |

不算创建：`SettingsService.UpdateWidget(s)Batch` 的补加、AOT 冒烟测试代码。另注：拆组拖出（`PlaceDetachedMember`）不建新配置、不走本漏斗，但它产生了新的独立表面身份——其首次落点按 5.4 的"拖出落点"入口提交归属。

### 5.9 把所有格子移到这里 + 撤销（阶段 4，D7）

`WidgetManager.MoveAllWidgetSurfacesToDisplayAsync(string displayId)`，替代 `PinAllWidgetSurfacesToScreenAsync`（约 `:1744`）：

1. 生成撤销快照：每个表面（独立格子、组表面、组成员）的 `Mode`、`BoundScreenId`、配置几何字段、当前激活档案中的条目（深拷贝）。
2. 对所有表面（含隐藏、收起、懒加载未建窗口、FollowPrimary）：`Mode = Pinned, HomeId = displayId`；条目按 5.3 Seed 的"HomeId 在线"分支映射到该屏；写为权威。
3. 对已加载窗口在 `BeginDisplayTopologyTransition/End` 包裹下重新计算实际位置（沿用现有 PinAll 的包裹方式）；返回 `(movedCount, undoToken)`。
4. `UndoMoveAllAsync(undoToken)`：恢复快照中的全部字段与条目，重新计算实际位置。撤销令牌在下一次任何放置提交或 InfoBar 关闭时失效。**撤销期间拓扑变化不失效令牌**：撤销按快照恢复归属/模式/条目，实际位置以当时拓扑重新解析（S30）。

确认框文案见 6.4（需统计"共 N 个、其中 M 个当前在其他显示器"）。

### 5.10 单个表面移到某屏 / 始终在主显示器（阶段 4）

替代 `ApplyScreenBindingAsync` 的对外用法（可保留该方法作内部实现）：

- `MoveSurfaceToDisplayAsync(widgetId, displayId)`：`Mode = Pinned, HomeId = displayId`；条目按 Seed"HomeId 在线"分支映射；权威；重新计算实际位置；组表面镜像到成员。
- `SetFollowPrimaryAsync(widgetId, bool on)`：on → `Mode = FollowPrimary, BoundScreenId = null`，映射到主显示器；off → `Mode = Pinned, HomeId = 当前所在显示器`（不移动）。
- 只有一块显示器时这些入口不出现（6.1/6.3）。

---

## 6. 交互与设置页

### 6.1 格子右键菜单（`WidgetScreenMenuBuilder.cs`，调用处 `ContentWidgetWindow.Commands.cs` 约 `:437`、`:957`）

只有一块显示器时整个子菜单不出现（保持现有 `TryCreate` 返回 null 的约定，null 不能加入 Items）。

```
移到显示器 ▸   ● 显示器 1 · 内置显示器 · 主显示器        （RadioMenuFlyoutItem，打勾 = 当前实际所在屏）
               ○ 显示器 2 · DELL U2720Q
               ─────────────
               ☐ 始终在主显示器                          （ToggleMenuFlyoutItem）
               原显示器 3 · LG 27UL850（未连接），重新连接后自动回到那里   （仅回退态显示，IsEnabled=false）
```

- 打勾项 = 当前实际所在屏（不是归属屏），使用户看到的永远与屏幕一致；回退态额外显示禁用的"原显示器"行。
- 选择某屏 → `MoveSurfaceToDisplayAsync`；切换"始终在主显示器" → `SetFollowPrimaryAsync`。
- 显示器标签格式：`显示器 {编号} · {名称}`，主显示器追加 ` · 主显示器`；名称不可得时用分辨率。阶段 4 之前仍用分辨率。
- 删除"自动"项。

### 6.2 拖动提示

见 5.4 末尾。仅对"始终在主显示器"的格子、仅在经过非主显示器时显示。

### 6.3 设置页「显示器与格子」（`Views/SettingsSections/DisplaySettingsSection.xaml(.cs)`）

保留：总览卡 + 识别按钮、排列预览（R8 终案视觉：accent 整卡选中、编号居中、徽标行）、InfoBar。修改后的页面结构：

```
[InfoBar：状态反馈；"把所有格子移到这里"成功后带「撤销」按钮]

显示器与格子                                           [识别]
查看显示器排列，选择格子显示在哪块显示器

[排列预览：卡片 = 大号编号；徽标行：「主显示器」（实底）、「新格子」（描边，仅当“新格子出现在”= 指定且为该屏）]

┌ 显示器 2 · DELL U2720Q · 主显示器                       （选中屏卡片，SettingsCard）
│ 2560×1440 · 缩放 125% · 当前有 5 个格子     [把所有格子移到这里]   （默认样式按钮；仅多屏时显示）
└

新格子出现在                                   [鼠标所在的显示器 ▾]  （SettingsCard + ComboBox）
从托盘、快捷键或菜单新建格子时使用                  选项：鼠标所在的显示器 / 主显示器 / 每块在线显示器 /
                                                  （若已指定的屏不在线）"显示器 N（未连接）"禁用项保持选中

▾ 多显示器                                                       （SettingsExpander，始终显示）
   显示器断开时                               [移到其他显示器 ▾]
   重新连接后，格子会自动回到原来的位置         选项：移到其他显示器 / 收起为胶囊

各格子所在显示器                                                （小节标题；仅多屏时显示）
   文件 · 工作                                 [显示器 2 · DELL U2720Q ▾]
   待办                                        [始终在主显示器 ▾]
   格子组（3 个格子） 成员：…                   [显示器 1 · 内置显示器 ▾]
   照片                                        [显示器 3（未连接）· 重新连接后自动回到那里 ▾]（禁用项保持选中）
```

- "当前有 N 个格子" = 当前实际显示在该屏上的表面数（含隐藏的按其解析结果计）。
- 每行下拉选项：每块在线显示器 + "始终在主显示器"；回退态表面额外显示禁用的"原显示器 N（未连接）"项并保持选中（沿用现有 offline 项做法）。选择即调用 5.10。
- 删除：`SetDefaultScreenButton`（设为格子主屏幕 / 清除默认）、`PinAllWidgetsButton`；新增 `MoveAllWidgetsButton`、`NewWidgetPlacementCombo`、`DisconnectBehaviorCombo`、`MultipleDisplaysExpander`（x:Name 仅为建议）。
- "把所有格子移到这里"：先弹 `ContentDialog` 确认（标题/正文/主按钮文案见 6.4，取消按钮复用现有通用"取消"文案键），确认后执行 5.9，成功后 InfoBar 显示完成文案 + "撤销"按钮。
- **拓扑变化期间页面自刷新（S27/S28）**：分区订阅 `DisplayAreaWatcherService.DisplaysChanged`（或 App 的拓扑恢复完成回调）→ `Refresh()`——现状分区只在 Loaded/主题/自身操作时刷新，拔插屏后预览与下拉全部陈旧。「识别」overlay 显示期间发生拓扑变化 → 立即关闭全部 overlay 窗口。
- 实现约束（来自 `widget-screen-binding.md` R0/R5，必须遵守）：分区在 LoadContent 期间元素未进树，**直接标记禁止 ThemeResource 画刷与无先例的 StaticResource 键**，主题画刷在 Loaded 后用 `Application.Current.Resources.TryGetValue` + 回退赋值；`SettingsWindow.xaml` 新增 x:Name 必须同步 `SectionElements.cs`；同步 AOT 契约冻结计数（x:Bind/SettingsCard）；不使用 `IReadOnlyList<T> => [...]` 集合表达式直接 x:Bind。

### 6.4 文案（12 语言：zh-CN、zh-TW、en-US、ja-JP、de-DE、fr-FR、es-ES、pt-BR、ru-RU、ar-SA、hi-IN、bn-BD，全部同步）

下表给出 zh-CN 与 en-US，其它语言由实现 agent 翻译（术语对齐各语言 Windows 设置的"主显示器 / Main display"译法）。

**修改**：

| 键 | zh-CN | en-US |
|---|---|---|
| `Widget.ScreenBinding.Title` | 移到显示器 | Move to display |
| `Widget.ScreenBinding.FollowPrimary` | 始终在主显示器 | Always on main display |
| `Widget.ScreenBinding.MonitorFormat` | 显示器 {0} · {1} | Display {0} · {1} |
| `Widget.ScreenBinding.PrimarySuffix` | ` · 主显示器` | ` · Main display` |
| `Settings.Displays.Description` | 查看显示器排列，选择格子显示在哪块显示器 | Review your display arrangement and choose where each widget appears |
| `Settings.Displays.PrimaryBadge` | 主显示器 | Main display |
| `Settings.Displays.DefaultBadge` | 新格子 | New widgets |
| `Settings.Displays.ScreenSummary` | {0} · 缩放 {1}% · 当前有 {2} 个格子 | {0} · {1}% scale · {2} widgets here |
| `Settings.Displays.Widgets.SectionTitle` | 各格子所在显示器 | Display for each widget |
| `Settings.Displays.Widgets.Offline` | {0}（未连接）· 重新连接后自动回到那里 | {0} (disconnected) · returns when reconnected |

**新增**：

| 键 | zh-CN | en-US |
|---|---|---|
| `Widget.ScreenBinding.HomeOffline` | 原显示器 {0}（未连接），重新连接后自动回到那里 | Home display {0} (disconnected) · returns when reconnected |
| `Widget.ScreenBinding.DragLeavesMain` | 松开后将不再跟随主显示器 | Release to stop following the main display |
| `Widget.ScreenBinding.BuiltInName` | 内置显示器 | Built-in display |
| `Settings.Displays.MoveAll` | 把所有格子移到这里 | Move all widgets here |
| `Settings.Displays.MoveAll.ConfirmTitle` | 把所有格子移到显示器 {0}？ | Move all widgets to display {0}? |
| `Settings.Displays.MoveAll.ConfirmBody` | 将移动 {0} 个格子，其中 {1} 个当前在其他显示器上。之后你仍可以把任意格子拖到别的显示器。 | {0} widgets will move, {1} of them from other displays. You can still drag any widget to another display afterwards. |
| `Settings.Displays.MoveAll.ConfirmButton` | 移动 | Move |
| `Settings.Displays.MoveAll.Done` | 已将 {0} 个格子移到显示器 {1} | Moved {0} widgets to display {1} |
| `Settings.Displays.MoveAll.Undo` | 撤销 | Undo |
| `Settings.Displays.MoveAll.Undone` | 已撤销 | Undone |
| `Settings.Displays.MoveAll.Failed` | 移动格子失败 | Couldn't move widgets |
| `Settings.Displays.NewWidgets.Header` | 新格子出现在 | Show new widgets on |
| `Settings.Displays.NewWidgets.Description` | 从托盘、快捷键或菜单新建格子时使用 | Used when you create a widget from the tray, a shortcut, or a menu |
| `Settings.Displays.NewWidgets.Cursor` | 鼠标所在的显示器 | Mouse pointer's display |
| `Settings.Displays.NewWidgets.Main` | 主显示器 | Main display |
| `Settings.Displays.NewWidgets.Offline` | {0}（未连接） | {0} (disconnected) |
| `Settings.Displays.MultipleDisplays.Header` | 多显示器 | Multiple displays |
| `Settings.Displays.Disconnect.Header` | 显示器断开时 | When a display is disconnected |
| `Settings.Displays.Disconnect.Description` | 重新连接后，格子会自动回到原来的位置 | Widgets return to where they were when it's reconnected |
| `Settings.Displays.Disconnect.Move` | 移到其他显示器 | Move to another display |
| `Settings.Displays.Disconnect.Collapse` | 收起为胶囊 | Collapse to capsules |

**删除**（确认全仓无引用后删除，12 语言同步）：`Widget.ScreenBinding.Auto`、`Settings.Displays.SetDefault`、`Settings.Displays.SetDefault.Done`、`Settings.Displays.SetDefault.Clear.Title`、`Settings.Displays.PinAll`、`Settings.Displays.PinAll.Done`、`Settings.Displays.PinAll.Failed`、`Settings.Displays.Widgets.Pinned`。

---

## 7. 迁移

### 7.1 Schema 11 → 12（阶段 1 随代码发布）

`SettingsMigrationService.cs`：`CurrentSchemaVersion` 由 11 改为 12（约 `:24`），新增 `Migration_11_To_12` 并在构造函数注册。迁移在反序列化副本上运行，**不得调用任何 Win32/显示 API**，只使用已持久化的数据。

1. **推断归属屏**（独立格子与组表面分别处理；组成员随后镜像组的结果）：
   - `Pinned` 且 `BoundScreenId` 非退化 → 不变。
   - `FollowPrimary` → 不变。
   - `Unbound` → 候选 ID 依次取：该表面在**显示器数量最多**的档案中的条目 `PositionMonitorStableId`（数量相同取 `LastUsedAtUtc` 最新）→ 配置的 `PositionMonitorStableId`；取到非退化值则 `Mode = Pinned, BoundScreenId = 该值`，否则保持 `Unbound`。（理由：缺屏时的位置多为回退态，显示器最全的组合下的位置最可能是用户主动选择的。）
2. **档案条目权威标志**：对每个档案的每个条目，`IsAuthoritative = (表面无归属) || (条目 PositionMonitorStableId 等于表面归属屏)`；`AuthoredAtUtc = 档案 LastUsedAtUtc`。
3. **胶囊显示器字段**：配置和每个档案条目中的 `CompactPlacement` 显示器字段同步为其表面的显示器字段（修 B4 存量数据）。
4. **新格子位置设置**：`WidgetDefaultBoundScreenId` 非空 → `WidgetNewPlacementTarget = SpecificDisplay`；否则 `CursorDisplay`。
5. `WidgetDisplayDisconnectBehavior = MoveToRemaining`。
6. `SettingsService.NormalizeScreenBindings`（约 `:1422`）保持"Pinned 无 ID → Unbound"的降级规则；另增加"Pinned 但 ID 退化 → Unbound""FollowPrimary → BoundScreenId 置 null"。

### 7.2 Schema 12 → 13（阶段 3 随 v4 key 发布）

1. 对每个档案用其持久化的 `Monitors` 计算 v4 key（5.6，纯计算）。
2. 同一 v4 key 的多个 v3 档案合并：以 `LastUsedAtUtc` 最新者为基础；对其缺失的表面，从同组其它档案按 `AuthoredAtUtc` 取最新的权威条目补入；其余丢弃。
3. `ActiveWidgetTopologyKey` 改为对应 v4 key。
4. 保留 `FindCompatibleProfile` 对 v1/v2 遗留 key 的懒迁移，但目标改为 v4。
5. **并列与缺失**：`LastUsedAtUtc` 相同或字段缺失时（老档案反序列化会初始化为"载入时刻"，排序实质随机）→ 以 v3 key 字典序作次级 tiebreak。
6. **空/全退化 `Monitors` 的 v3 档案**：其 v4 key 为空集合哈希，会互相撞 key——此类档案**不参与合并、直接丢弃**；`ActiveWidgetTopologyKey` 若指向被丢弃的档案 → 置 null（下次激活按新组合播种）。7.1 归属推断中"显示器数量最多"照计退化 ID 的 `Monitors`（屏数与身份是否退化无关）。

**降级兼容**：旧版本读取新文件时，`Pinned`/`FollowPrimary`/`BoundScreenId` 语义兼容；未知新字段被忽略；v4 key 档案会被旧版本的 `FindCompatibleProfile` 按签名懒迁移回 v3，可接受。

---

## 8. 分阶段任务（每阶段一个或多个 PR，阶段内任务可拆分；每个 PR 全量测试绿）

### 阶段 1：修行为缺陷（不改 UI 外观；可单独发布）

| 任务 | 内容 | 主要文件 |
|---|---|---|
| 1.1 | 统一解析器 `DisplayPlacementResolver`（5.1），替换三处优先级逻辑；删除运行时 WasPrimary 智能跟随 | `Services/DisplayPlacementResolver.cs`（新）、`WidgetPositioningService.cs`、`WidgetTopologyLayoutService.cs`、`WidgetCompactBoundsCalculator.cs` |
| 1.2 | 胶囊显示器字段派生化（4.4）；`MapToTopology` 回填胶囊稳定 ID | `WidgetTopologyLayoutService.cs`、`WidgetCompactBoundsCalculator.cs`、`WidgetWindowBase.Collapse.cs`（胶囊位置捕获处） |
| 1.3 | 档案条目新增 `IsAuthoritative/AuthoredAtUtc`；播种改为逐表面取最新权威条目（5.3）；捕获不提升标志 | `Models/WidgetTopologyLayoutModels.cs`、`WidgetTopologyLayoutService.cs` |
| 1.4 | `CommitUserPlacement`（5.4）及全部调用点；显示器判定改为最大交集面积 | `WidgetManager.ScreenHome.cs`（新）、`WidgetWindowBase.Interaction.cs`、`WidgetWindowBase.CoordinatedMove.cs`、`WidgetWindowBase.Bounds.cs`、`WidgetManager.CapsuleArrangement.cs`、`WidgetManager.Groups.cs` |
| 1.5 | 新格子放置漏斗（5.8）+ 设置字段 `WidgetNewPlacementTarget`（暂无 UI，按迁移值生效） | `WidgetManager*.cs`、`WidgetManager.Storage.cs`、`DesktopOrganizationTransaction.cs`、`InitialFileWidgetPlacementPolicy.cs`、`Models/WidgetLayoutSettingsSlice.cs`、`AppSettings.cs` |
| 1.6 | 迁移 11 → 12（7.1） | `SettingsMigrationService.cs`、`SettingsService.cs` |
| 1.7 | 现有菜单与设置页的最小适配：右键菜单和设置页每行下拉都去掉"自动"，打勾/选中改为当前实际所在屏；`ApplyScreenBindingAsync` 与 `PinAllWidgetSurfacesToScreenAsync` 结束时写入的档案条目标记为权威（二者语义已等同"设定归属屏"）。文案、确认框、撤销等留到阶段 4。 | `WidgetScreenMenuBuilder.cs`、`DisplaySettingsSection.xaml.cs`、`WidgetManager.cs` |

验收：第 9.1 节回归测试 R1–R5、R8–R12 通过；场景 S1、S6、S8、S19、S21、S25 真机通过。

### 阶段 2：拓扑响应节奏

| 任务 | 内容 | 主要文件 |
|---|---|---|
| 2.1 | 协调器应用门（5.6）：SessionLocked、DisplayOff、FullscreenApp | `DisplayTopologyTransitionCoordinator.cs`、`AppLifecycleRecoveryWatcher.cs`、`AppLifecycleRecoverySignalClassifier.cs`、`App.xaml.cs`、`Platform/Win32Helper*.cs`（`SHQueryUserNotificationState` P/Invoke；写法与现有 `Win32Helper` 一致——注意仓库内 `DllImport` 与 `LibraryImport` 并存，`DisplayTiming.cs` 现为 `DllImport`） |
| 2.2 | 移除宽限 RemovalGrace（含提前结束条件：唤起、拖动、设置页移动） | 同上 + `WidgetManager.cs`（唤起路径通知协调器） |
| 2.3 | 启动等待 StartupSettling | `App.xaml.cs`（Phase 3 恢复格子前后）、`WidgetManager.RestoreWidgetsAsync` |
| 2.4 | 临时档案（`IsProvisional`，10 秒或用户提交后落盘） | `WidgetTopologyLayoutService.cs`、`Models/WidgetTopologyLayoutModels.cs` |
| 2.5 | （可选）外部移动采纳（5.5），真机验证后决定是否启用 | `WidgetWindowBase.Bounds.cs` |

验收：R13–R16；S11–S14 真机通过（重点：DP 显示器休眠、KVM 切换、锁屏、全屏游戏、扩展坞冷启动）。

### 阶段 3：档案模型

| 任务 | 内容 |
|---|---|
| 3.1 | v4 key（5.6）；同 key 元数据变化时无损重投影（替换 `ReprojectSurfaces`） |
| 3.2 | 实际位置计算不写意图（5.2）；检查所有 `updateConfig: true` 的恢复路径 |
| 3.3 | 迁移 12 → 13（7.2） |

验收：R6、R7、R17；S2、S10、S15 真机通过。

### 阶段 4：界面

| 任务 | 内容 |
|---|---|
| 4.1 | 显示器友好名（4.6，CCD）+ `WidgetScreenInfo.FriendlyName`；菜单与设置页标签使用名称 |
| 4.2 | 右键菜单新结构（6.1）、拖动提示（6.2） |
| 4.3 | 设置页重构（6.3）：新格子出现在、多显示器扩展器、各格子所在显示器、把所有格子移到这里 + 确认 + 撤销 |
| 4.4 | 断开时收起为胶囊（5.7） |
| 4.5 | 12 语言文案（6.4），删除废弃键 |

验收：R18–R20；S20、S23、S26 真机通过；12 语言无缺键（现有字符串完整性测试通过）。

### 阶段 5：可选增强（单独排期）

- EDID 二级身份重新匹配（4.6，S17/S18）。
- 复制屏/演示时隐藏格子（对应 Windows"复制显示器时自动开启勿扰"）。
- 布局快照（手动保存/恢复，参照 Rainmeter Layouts、Fences 快照）。

---

## 9. 测试

### 9.1 新增单元测试（纯逻辑，`tests/DeskBox.Tests`）

建议新文件 `DisplayPlacementResolverTests.cs`、`WidgetScreenHomeTests.cs`、`WidgetTopologySeedingTests.cs`，以及扩展 `WidgetTopologyLayoutServiceTests.cs`、`DisplayTopologyTransitionCoordinatorTests.cs`、迁移测试。测试辅助可参照 `WidgetTopologyLayoutServiceTests` 中的 `Monitor(...)` / `CreateWidget()` 与 `WidgetTopologyLayoutService.CreateSnapshotForTest`、`WidgetPositioningService.ResolveBoundsForTestWithScreens`。

| # | 场景 | 期望 |
|---|---|---|
| R1 | 双屏 A(主)/B，Unbound 格子在 A、WasPrimary=true、稳定 ID=A；主显示器切到 B（A 移到 x=-1920），经档案激活 | 留在 A（x<0）（原 B1 探针 1，现已成立，锁定） |
| R2 | 同一配置直接交给运行时解析 | 也留在 A（原 B1 探针 2，现为跳到 B → 必须修正） |
| R3 | 解析器优先级矩阵：FollowPrimary / Home 在线 / Home 离线 + 条目在线 / 条目稳定 ID 不在线时不使用设备名 / 老数据仅设备名 / 启发式 4a、4b（方位、尺寸、设备名）、4c / 兜底 | 各自返回预期屏与 Reason、IsFallback |
| R4 | 接坞 {L,E} 格子在 E → {L} → {L,E,TV} | 回到 E（原 B3 探针，现为留在 L → 必须修正） |
| R5 | 格子+胶囊在 E，拔掉 E | 胶囊稳定 ID 与表面一致 = L（原 B4 探针，现为 E → 必须修正） |
| R6 | 1080p → 2K → 改边距 700/500 → 1080p（阶段 3） | 边距保持 700/500（原 B2 探针，现为回到 100/80 → 阶段 3 修正） |
| R7 | 同 key 下工作区缩小使尺寸被夹取，再恢复（阶段 3） | 条目尺寸/边距从未被改写，恢复后与原值相等 |
| R8 | 提交规则：Home 在线时拖到另一屏 → 改归属；Home 离线时拖动 → 不改归属；FollowPrimary 拖到非主屏 → 变 Pinned；Home 为空 → 获得归属；退化 ID → 不改；远程会话 → 不改 | 逐条断言 |
| R9 | 组拖动提交 | 组配置与全部成员 Mode/BoundScreenId 一致 |
| R10 | 档案激活永不改归属（保留现有 `TopologyProfileActivation_RepositionsButNeverRebinds`） | 通过 |
| R11 | 新格子漏斗：Cursor/Main/Specific/Specific 离线/ExplicitBounds/SourceSurface | 目标屏、右上对齐、级联、归属正确 |
| R12 | 迁移 11→12：Pinned 不变；Unbound 从"显示器最多的档案"推断归属；退化 ID 保持 Unbound；权威标志；胶囊字段同步；WidgetDefaultBoundScreenId → SpecificDisplay | 逐条断言 |
| R13 | 协调器门：门关时签名变化不触发 restore、不消耗重试；开门后触发一次 | 通过 |
| R14 | 移除宽限：宽限内屏回来 → 不触发 restore；到期 → 触发；唤起/拖动 → 立即触发 | 通过 |
| R15 | 显示电源分类器识别"关"（Data=0）、"开"（1），"变暗"（2）不关门 | 通过 |
| R16 | 临时档案：存活不足且无提交时不落盘、不参与 LRU；用户提交后落盘 | 通过 |
| R17 | 迁移 12→13：同 v4 key 的 v3 档案合并、ActiveKey 改写 | 通过 |
| R18 | 把所有格子移到这里 + 撤销：撤销后 Mode/BoundScreenId/几何/条目与之前逐字段相等 | 通过 |
| R19 | 断开收起：候选判定、标志写入、归属屏回来自动展开、用户手动操作后清除标志、"始终展开"行为的表面不收起 | 通过 |
| R20 | 菜单构建：单屏返回 null；打勾项=实际所在屏；回退态出现禁用"原显示器"项；无"自动"项 | 通过 |

**可测性前提**（实现时先做提取，否则下列用例无法在测试宿主纯逻辑运行）：R11 需注入 `GetCursorPos`；R13/R14 需把门/宽限抽成纯状态机并注入时钟（协调器本体用 `DispatcherQueueTimer`，现有 `DisplayTopologyTransitionCoordinatorTests` 只覆盖 StabilityTracker/CombineReasons 等静态件）；R16 需注入时钟（`CreateSnapshotForTest` + internal `Activate` 已经可用）；R18/R19/R20 落在 `WidgetManager`/窗口/UI 层，需分别抽出"快照-应用"纯服务、"断开收起候选判定"纯函数与"菜单状态"纯模型（`WidgetScreenMenuBuilder` 构造 WinUI 对象，测试宿主无 XAML 运行时；AOT 契约测试只是源码扫描，不覆盖此层）。

### 9.2 需要按新语义改写的现有测试

| 测试 | 原期望 | 新期望 |
|---|---|---|
| `WidgetPositioningServiceTests.ResolveBounds_FollowsCurrentPrimaryWhenCapturedOnPrimaryMonitor` | WasPrimary 格子跟随新主显示器 | 原屏（DISPLAY1）仍在线 → 留在原屏 |
| `WidgetPositioningServiceTests.ResolveBounds_TreatsLegacyOriginMonitorAsPrimaryForSmartMode` | "原点附近"启发式视为主显示器并跟随 | 该启发式已删除；按设备名/工作区 key 留在原屏 |
| `WidgetPositioningServiceTests.EnsureCurrentBoundsCoordinateVersion_MigratesLegacyPrimaryWidgetToCurrentPrimary` | 老坐标迁移时移到当前主显示器 | 按新解析器：原屏在线 → 留在原屏（断言相应更新） |
| `WidgetScreenBindingTests.UnboundChain_StableIdLayer_OutranksSwappedDeviceName` 等 | — | 逐个复核，仍符合 5.1 的保留，不符合的按 5.1 改写并在 PR 描述中列出 |
| `PinAll` 相关测试（如有） | 永久固定 | 改为 5.9 语义 |

### 9.3 真机验收清单（每阶段结束执行对应条目，记录结果到本文附录 D）

1. 笔记本 + 扩展坞外接屏：接坞/拔坞/合盖/开盖往返 5 次（S3、S4、S5、S10）。
2. 改主显示器、改分辨率、改缩放、拖动显示器排列、旋转竖屏再改回（S1、S2）。
3. DP 显示器自动休眠唤醒、按显示器电源键关开、切换显示器输入源（S11）。
4. 锁屏 10 分钟以上、睡眠唤醒（S12）。
5. 独占全屏游戏改分辨率（S13）。
6. DisplayLink 或雷电坞冷启动（S14）。
7. Win+P 复制/仅第二屏幕（S10、S15）。
8. 远程桌面登录后拖动，再回本机（S16）。
9. 两块同型号屏交换接口（S17，记录现象）。
10. 设置页全部入口 + 撤销 + 拔插屏时页面刷新 + 12 语言目测（S20、S22、S23、S26、S27、S28）。

### 9.4 日志

- `[DisplayTopology] gate closed/opened reason=…`、`grace start/cancel/expire elapsed=…ms`、`provisional profile key=… persisted reason=…`。
- `[ScreenHome] surface=… from=… to=… source=…`（归属变化时）。
- 激活时每个表面的解析 `Reason`（Verbose 级别）。
- 日志中只记录稳定 ID 与友好名，不记录其它个人信息。

---

## 10. 非目标

- 按 Windows 虚拟桌面分别记忆布局。
- 按屏幕方向（横/竖）分别记忆布局（D5 明确放弃）。
- "唤起跟随鼠标所在屏"（SummonFollowsCursor，沿用 `widget-screen-binding.md` R7 的非目标）。
- 在排列预览上拖动格子改屏（`widget-screen-binding.md` R3.2 二期项，仍不做）。
- 依赖 Windows App SDK 实验性的 `AppWindow` 位置保存 API（仅包装 `GetWindowPlacement`，不支持按显示器组合记忆）。

## 11. 风险与注意

- **AOT/裁剪**：新 P/Invoke 用与现有 `Win32Helper` 一致的写法；不引入 WinRT `DisplayMonitor`。
- **迁移纯净性**：迁移步骤不得访问显示 API；迁移失败时管线会整体回退（`SettingsMigrationPipeline` 的写时复制机制）。
- **线程**：解析、激活、提交都在 UI DispatcherQueue 上执行，与现有协调器一致。
- **Z 序**：移动窗口不得破坏 `docs/architecture/[重要勿删]widget_zorder_lifecycle.md` 的约定。注意现状并非"全部走 `ApplyWindowBounds`"——`WidgetWindowBase.Collapse.cs` 有 10+ 处 `MoveWindowWithoutPersisting` 直接 `SetWindowPos`（折叠/展开/胶囊动画），但均带 `SWP_NOZORDER|SWP_NOACTIVATE`。新代码保持同样的标志约定即可，不要求收敛到 `ApplyWindowBounds`。
- **组**：组表面是唯一的放置单位；任何写入都要镜像到成员，拆组继承组的 Mode/BoundScreenId（现有行为保留）。
- **性能**：一次激活中显示器列表只采集一次并传给解析器；不要在解析器内部调用 `Win32Helper.GetMonitorWorkAreaInfos`。
- **行为变更告知**：阶段 1 改变了两项可感知行为（主显示器切换不再带走"原来在主显示器上的"格子；新格子默认在鼠标所在屏右上），发布说明需写明。

---

## 附录 A：调研依据

| 来源 | 结论 | 本方案中的对应 |
|---|---|---|
| [Microsoft 支持：在 Windows 中使用多台显示器](https://support.microsoft.com/en-us/windows/hardware/display-graphics/how-to-use-multiple-monitors-in-windows) | Windows 11 提供"根据显示器连接记住窗口位置""断开显示器时最小化窗口"；重新接坞后窗口回到原处 | D3、D5、D9，S3/S4 |
| [winaero：断开显示器时最小化窗口](https://winaero.com/windows-11-dont-minimize-windows-when-monitor-is-disconnected/) | 两项默认开启；关闭最小化时窗口堆叠到剩余显示器 | D3 默认值的取舍 |
| [Win32：在多显示器上定位对象](https://learn.microsoft.com/en-us/windows/win32/gdi/positioning-objects-on-multiple-display-monitors) | 窗口跨屏时按"包含最大部分"的屏；右键菜单出现在点击处所在屏；对话框出现在所属窗口的屏 | B8、5.4（最大交集面积）、5.8（新格子出现在用户操作处） |
| [MonitorFromRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect) | 返回与矩形交集面积最大的显示器 | 5.4 |
| [DirectX 博客：Rapid Hot Plug Detect](https://devblogs.microsoft.com/directx/avoid-unexpected-app-rearrangement/) | DP 显示器休眠被当作拔出导致窗口重排；微软在系统层缓解 | B10、5.6 宽限期，S11 |
| [PowerToys #19240](https://github.com/microsoft/PowerToys/issues/19240)、[#19278](https://github.com/microsoft/PowerToys/issues/19278) | FancyZones 0.60 改用 EDID 序列号识别显示器，遇到序列号重复导致各屏布局串用 | 4.6：设备接口路径为主身份，EDID 仅在唯一时作二级匹配 |
| [PowerToys FancyZones 文档](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones) | "将新建窗口移到当前活动显示器" | D6 |
| [Stardock Fences 6 布局设置](https://support.stardock.com/space/SHC/2814444204/Fences%206%20%20UI%3A%20Layout)、[论坛：分辨率变化打乱图标](https://forums.stardock.com/533167/) | 按显示器配置分别保存布局；游戏改分辨率时出错 | D5（分辨率不进 key）、S13 |
| [Rainmeter 管理界面](https://docs.rainmeter.net/manual/user-interface/manage/) | 每个皮肤：主显示器 / 指定显示器 / "根据窗口位置自动选择"（选指定屏即取消自动）；全屏游戏模式；布局保存 | D1（放哪属于哪）、5.6 全屏门控、阶段 5 布局快照 |
| [DISPLAYCONFIG_TARGET_DEVICE_NAME](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name)、[DisplayConfigGetDeviceInfo 场景](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/displayconfiggetdeviceinfo-summary-and-scenarios) | 可取显示器友好名、EDID 厂商/型号、设备路径；Windows 显示控制面板用它取名称 | 4.6、B11 |

## 附录 B：v1（2026-10-04）结论勘误

| v1 条目 | 勘误 |
|---|---|
| 场景 1"捕获时在主屏的 Unbound 格子会跟随新主屏搬到屏2" | 不成立：有有效稳定 ID 时档案层把格子留在原屏并改写 WasPrimary；运行时解析才会跟随，两层矛盾（B1）。新方案：主显示器切换格子不动（S1）。 |
| P2-3"历史同签名档案复活属预期" | 不接受：分辨率/主显示器/排列进 key 导致旧布局复活（B2），v2 改为按显示器集合（D5）。 |
| P1-1 方案 A"拖动跨屏=更新归属" | 方向保留，但需增加"归属屏离线时拖动不改归属"，否则回退态下的随手拖动会让格子不再回原屏（5.4 第 3 步）。 |
| P1-2"新格子统一应用默认屏+继承 Pinned" | 改为 D6：只决定创建位置，归属=实际创建所在屏，不隐式固定到全局默认屏。 |
| P1-3"老胶囊数据 null 稳定 ID" | 范围更大：迁移后胶囊稳定 ID 会残留旧屏（B4）。v2 让胶囊不再拥有独立显示器身份（4.4）。 |
| P1-4"批量固定无重试+动画旁路" | 严重性高估：`IsHideAnimationRunning` 是托盘隐藏动画，结束后窗口隐藏，下次显示前一定重新解析位置；降为非问题。由 5.9 的新实现自然覆盖。 |
| P2-1"退化稳定 ID+主屏切换堆屏" | 由 5.1 第 3 步（条目稳定 ID 退化时才用设备名/工作区 key）与第 4 步启发式缓解。 |
| P2-2"瞬态拓扑占用档案额度" | 由 D5（分辨率不进 key）与 5.6 临时档案解决。 |
| P2-4"单屏调整不合并回双屏档案" | 保留此语义（每种组合各记一套，S5）。 |

## 附录 C：代码索引

| 领域 | 文件 |
|---|---|
| 模型 | `src/DeskBox/Models/WidgetConfig.cs`（绑定字段、`WidgetCompactPlacement`、`WidgetScreenBindingMode`）、`WidgetGroupConfig.cs`、`WidgetTopologyLayoutModels.cs`、`WidgetLayoutSettingsSlice.cs`、`AppSettings.cs` |
| 档案 | `src/DeskBox/Services/WidgetTopologyLayoutService.cs` |
| 运行时解析 | `src/DeskBox/Services/WidgetPositioningService.cs`、`WidgetCompactBoundsCalculator.cs` |
| 协调与信号 | `DisplayTopologyTransitionCoordinator.cs`、`DisplayAreaWatcherService.cs`、`WidgetDisplayChangeWatcher.cs`、`AppLifecycleRecoveryWatcher.cs`、`AppLifecycleRecoverySignalClassifier.cs`、`App.xaml.cs`（约 `:1104-1110` 创建协调器，`:1467`/`:1539` 请求恢复） |
| 管理器 | `WidgetManager.cs`（`RestoreWidgetPositionsAsync`、`ApplyScreenBindingAsync`、`PinAllWidgetSurfacesToScreenAsync`、`PlacePendingInitialWidgets`、`NormalizeWidgetBounds`、创建入口）、`WidgetManager.FeatureWidgets.cs`、`WidgetManager.Storage.cs`、`WidgetManager.Groups.cs`、`WidgetManager.CapsuleArrangement.cs`、`WidgetManager.CoordinatedMove.cs` |
| 窗口 | `src/DeskBox/Views/WidgetWindowBase.Bounds.cs`、`WidgetWindowBase.Interaction.cs`、`WidgetWindowBase.CoordinatedMove.cs`、`WidgetWindowBase.Collapse.cs`、`ContentWidgetWindow.Commands.cs` |
| 屏幕目录/菜单/设置页 | `WidgetScreenCatalog.cs`、`WidgetScreenMenuBuilder.cs`、`Views/SettingsSections/DisplaySettingsSection.xaml(.cs)`、`Views/DisplayIdentifyOverlayWindow.cs` |
| 平台 | `src/DeskBox/Platform/Win32Helper.cs`（`GetMonitorWorkAreaInfos`、`ResolveStableMonitorId`、`MonitorWorkAreaInfo`）、`Win32Helper.DisplayTiming.cs`（CCD P/Invoke） |
| 设置与迁移 | `SettingsService.cs`（`NormalizeScreenBindings`、`NormalizeWidgetTopologyLayouts`、默认值保留表）、`SettingsMigrationService.cs` |
| 新建策略 | `InitialFileWidgetPlacementPolicy.cs`、`DesktopOrganizationTransaction.cs` |
| 文案 | `src/DeskBox/Strings/*.json`（12 语言） |
| 现有测试 | `tests/DeskBox.Tests/WidgetTopologyLayoutServiceTests.cs`、`WidgetPositioningServiceTests.cs`、`WidgetScreenBindingTests.cs`、`DisplayTopologyRestorationContractTests.cs`、`DisplayTopologyTransitionCoordinatorTests.cs` |

## 附录 D：真机验收记录

（各阶段完成后由开发 agent 填写：日期、机器与显示器配置、场景编号、结果、日志摘录。）

## 附录 E：2026-10-08 审查修订记录

三路代码审查（B1-B12 逐条对码、算法/入口/迁移一致性、Windows 语义与外部依据核验）后的修订；**未改变 D1-D10 决策与整体架构**。审查同时确认了附录 B 对 v1 的勘误成立（B1 属实：运行时智能跟随在稳定 ID 之前，档案层相反且迁移改写 WasPrimary）。

| 类别 | 修订 |
|---|---|
| 硬错误修正 | ① 5.4 组拖动挂载点：`CompleteWidgetGroupDragAsync`(:2219) 是合并建组路径，组的移动改为在组宿主的 `EndWindowDragCore` 内提交；② 11 节 Z 序表述改为"保持 SWP_NOZORDER\|SWP_NOACTIVATE 约定"（现状 Collapse.cs 有 10+ 处 `MoveWindowWithoutPersisting` 直呼 SetWindowPos，均带安全标志）；③ 5.4 归属判定不再依赖 `DisplayArea.GetFromRect`（WinUI 文档未定义跨屏裁决语义），改为自实现最大交集面积（`MonitorFromRect` 为官方语义参照） |
| 规则补定义 | 5.3 新增"条目屏"判定（规范化比较；退化/为空视为无法判定按 ≠ 处理；5.4 提交时强制写条目稳定 ID，防全退化环境权威条目被 Seed 重写）；5.6 新增 `UserInteraction` 门行（门关时不消耗重试预算，修复长拖动耗尽 8 次重试的缺口）与"StartupSettling 必须挡 `RestoreWidgetsAsync:841` 启动直通"；7.2 新增 LastUsedAtUtc 并列 tiebreak、空/全退化 Monitors 档案丢弃与 ActiveKey 处置 |
| 入口补齐 | 5.4 新增两个用户放置入口：胶囊展开条整体拖动（`MoveCapsuleBarFromExpandedWidget`，条上每个胶囊都要提交）、拖出 detach 跨屏（`PlaceDetachedMember` 的 `detachedPosition` 分支，现状继承组归属会被弹回）；胶囊条持久化点修正为 `CompleteCapsuleBarDrag`(:365-395)；5.8 注明 detach 走 5.4 而非创建漏斗 |
| 场景补齐 | 新增 S27（设置页拓扑变化自刷新，分区需订阅 DisplaysChanged）、S28（识别 overlay 期间拓扑变化须关闭）、S29（双退化同尺寸屏=已知限制，geo token 撞车）、S30（MoveAll 撤销跨拓扑不失效令牌）；6.3 与 9.3 补对应约束/验收项 |
| 测试可测性 | 9.1 表后新增提取前提（R11 注入 GetCursorPos；R13/R14/R16 纯状态机+注入时钟；R18/R19/R20 需抽纯逻辑层——现状落点在 WidgetManager/窗口/UI 层，测试宿主无法直跑） |
| 措辞修正 | B3 改"逐表面播种+签名相同历史档案优先（sourceProfile=上一个 ActiveKey）"；B9 落点改"包含 (100,100) 的显示器"；任务 2.1 P/Invoke 风格描述（DllImport/LibraryImport 并存） |
| 确认无误项（未改） | B1/B2/B4-B8/B10-B12 逐条属实；9.2 三个测试名、`CalculateRightAlignedBounds`/`CollapseWidgetFromHost`/`persistManualState` 参数/`WidgetCollapseBehavior.Expanded`/`ShowLoadedWidgetWindow(:1395)`/`WidgetManager.CoordinatedMove.cs`/`Settings.Displays.SetDefault.Clear.Title` 键、Schema=11、5.8 创建路径清单（全仓 13 处 `.Widgets.Add` 全对上）、5.6 真子集判定数据可靠（宽限期激活档案 Monitors 不被覆盖）、外部依据 8 条中 7 条属实（复制模式单枚举为架构共识，建议 S15 真机验证） |
