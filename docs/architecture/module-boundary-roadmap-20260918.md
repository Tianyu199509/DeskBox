# DeskBox 模块边界路线图：Modular Monolith 落地方案

- 日期：2026-09-18（同日经两轮复核 + 一轮独立审计修订）
- 状态：**评审用文档，未改任何代码**
- 输入：方向评审稿《方向.md》（PowerToys 架构辨析 + Modular Monolith 提案）、本仓 `widget_contribution_seam.md`（2026-09-11）、`startup-resilience-audit-20260915.md`、红队 DD 采纳对照、AOT 内存曲线实验（**已结案：PLATEAU**）、两轮独立复核 + 独立审计稿《方向审计.md》（已逐条核验吸收）
- 本文回答三件事：这个方向值不值得做、按 DeskBox 现状应该怎么改、每刀的验收判据与工作量
- 修订记录：第二轮——第 0 刀门禁已开、原第 3 刀降为可选、原第 4 刀升级为数据分层刀；第三轮——刀序按"事故驱动 > 假设驱动"重排，新增立法前置步；**第四轮（审计收敛）——依赖方向改 ports & adapters、数据分层拆 2A/2B/2C、软边界措辞修正、新增 IFeatureRuntime 资源租约、启动管线带 criticality 模型、Platform 改随触碰 ratchet 不设专刀**

---

## 0. 结论

**方向采纳，方案修正。** Modular Monolith + 集中文件安全内核 + 选择性进程外隔离，对 DeskBox 是正确的长期方向——不是因为它是什么新架构，而是因为它是**本仓已经在走的路的形式化**：contribution 缝文档、缩略图/右键菜单代理进程、FileService 文件安全内核、功能级资源释放，全部是同一方向的先遣。

但原提案需要四处修正才适配本仓：

1. **不拆程序集**。在单程序集内用命名空间 + `internal` + 契约测试强制边界；物理拆分留作后期可选项。
2. **不用生命周期式模块接口**（`Initialize/Activate/Suspend/Shutdown` 不映射格子语义）。采用 `widget_contribution_seam.md` 已定型的 **contribution descriptor + IWidgetContent** 形态。
3. **切割顺序按"事故驱动 > 假设驱动"重排**（见 §3.3/§4）：立法步先行，App 生命线第一刀、数据分层第二刀，contribution descriptor 与新格子立项绑定。
4. **"Domain 纯化"收敛为一件事**：把解码图像从 item 模型挪到有预算的图像服务——RISE 已结案为有界水位后，它降为可选的水位优化刀，不是抽象的"依赖洁癖"。

经《方向审计.md》收敛，再钉死四处（详见 §2/§3.1/§4）：

1. **Feature 依赖 Contracts，不依赖 Platform 具体实现**——否则"到处 P/Invoke"只会变成"到处调 Platform"，Windows 耦合照样扩散。Feature 说"要什么"，Platform 说"怎么做"。
2. **数据分层拆 2A/2B/2C**——代码 ownership、磁盘格式、云同步协议是三件事，塞进一刀违反"每刀行为零变化"原则。
3. **单程序集边界是 policy-enforced 软边界，不等于编译器边界**——同程序集内 `internal` 挡不住跨命名空间调用，执法靠 architecture tests；物理拆分是满足触发条件后的升级路径。
4. **新增 `IFeatureRuntime` 资源租约**——不引入 PowerToys 式模块生命周期，但"disable=release"必须有契约：只有持长生命周期资源的功能（Search/Music/未来 Sync）实现，disable → DisposeAsync → watcher/COM 订阅/timer/缓存归零。

一句话定性：**把它当所有权纪律执行，不当架构审美执行。** 每一刀都问"这刀让谁拥有了什么资源/生命周期"，不问"目录结构好不好看"。

**做的理由要换成自己的，不是"微软都这么分层"**。三条真实理由，按硬度排序：

1. **事故半径**：僵尸实例吞启动、功能失败升级成打不开、三个月三次内存事故——全是"没人对资源/生命周期负责"的同构事故。边界=半径。
2. **数据安全**：代码边界错了是难维护（可恢复），**数据边界错了是安全事故**（19.8MB 事故、误恢复风险窗口）。数据分层比代码分层紧急。
3. **AI 协作上下文**：本仓的实际开发模式是单人+AI（Codex PR、多轮 agent 审阅）。350KB god 文件对 AI 不是"难读"而是**上下文装不下、跨会话纪律丢失**——每次会话重建理解，回归藏得进大文件。模块边界=上下文局部性=生成与审阅质量的直接提升。这比"未来三个工程师"现实得多。

---

## 1. 现状盘点：先例覆盖约 70%，强制覆盖约 15%

> **口径说明**：下面表格量的是**先例覆盖**——方案的每个要素在现有代码里已能找到活体先例（约 70%）。按**强制覆盖**（边界测试实际执法的面）算约 15%：P/Invoke 0/260 已迁移、设备层 0/~330 引用、facade 207 个透传未减、`Features`/`Platform` 命名空间尚不存在（`FileSafety` 刚有第一个住户）。两个数字都对，但读者应按后者理解"还剩多少"。

| 方案要素 | 现状 | 证据 |
|---|---|---|
| Modular monolith | 已是单进程单程序集 | `src/DeskBox/DeskBox.csproj` |
| Rust 只做危险原语适配层 | 已成立 | `deskbox_native.dll` ABI 2 / 10 导出；全仓 P/Invoke 只在 `Helpers/` 与 `Services/` 内，Feature 无一处直接调 Rust |
| 危险第三方代码进程外隔离 | **已存在且比提案深** | `deskbox-thumbnail-proxy` 常驻 server：第三方右键菜单处理器全部加载进代理进程（`native_context_menu_hosting.md`：拒绝进程内宿主，"崩了整个 App 陪葬"） |
| 集中文件安全内核 | `FileService` 已收口 | 对象身份（VolumeSerialNumber + FILE_ID_128）+ done-is-done，全部 copy/move/delete 走一处 |
| 模块 contract 缝 | 已有雏形 | `Contracts/IWidgetContent` + `IWidgetContentProvider` + `WidgetContentDescriptor` + `WidgetContentFactory` |
| DI | 已就位 | `ServiceRegistry` + Microsoft.Extensions.DI |
| 功能级资源释放 | 局部已有 | "disabling Search releases the complete search runtime"——是手动契约，未成统一约定 |
| 增量迁移纪律 | 已是工作方式 | stage 制 AOT 审计、契约测试、每 PR 独立可验 |
| 插件化路线 | 已验证后放弃 | `archive/pluginization-20260911`（184 提交：声明式包、安全语义、DNS-pinned 验证）；产品决策：不做商店、不做第三方插件平台 |

**未做的 30%**：边界正式化。具体表现为——

- 新增一个 `WidgetKind` 要碰 **25 个文件、最多 34 处 kind 分支**（`widget_contribution_seam.md` 实测）；
- god 文件群：`App.xaml.cs` 195KB、`WidgetManager` 各 partial 合计 ~350KB、`SettingsService` 141KB、`FileService` 136KB、`WidgetViewModel` 各 partial 合计 ~250KB、`SearchPopupWindow.xaml.cs` 164KB；
- `AppSettings` 208 个属性平铺，~133 个属功能侧；
- `Models/` 下 6 个文件有 WinUI 依赖，最实的是 `WidgetItem` 持有 `BitmapImage`；
- P/Invoke 散布 ~40 个文件，无统一平台层。

---

## 2. 目标架构

单程序集内的逻辑分层（命名空间即边界，architecture tests 即执法）：

```text
DeskBox.App                  ← Composition Root：生命线（单实例/托盘/激活/恢复）+ 装配
  ├─ DeskBox.Features.*       ← FileWidget / Todo / QuickCapture / Search / Music / Weather / Glance
  │    每个功能聚合自己的 service + viewmodel + content + settings 切片
  │    **只依赖 Contracts——说"要什么"，不认平台具体实现**
  ├─ DeskBox.Widgets          ← 格子宿主公共层：WidgetManager / WidgetWindowBase / 胶囊/层级/拓扑
  │    （功能不拥有宿主机制，宿主不认识功能内部）
  ├─ DeskBox.Contracts        ← IWidgetContent / IFileOperations / IFileSystemPrimitives /
  │    IAudioSession / ISyncTransport / IFeatureRuntime ……功能与平台之间的端口
  ├─ DeskBox.FileSafety       ← 文件操作 policy：对象身份/done-is-done/事务/WAL/何时允许 delete
  │    只引用 IFileSystemPrimitives 契约，不碰 native
  ├─ DeskBox.Platform         ← 唯一知道 HWND/WorkerW/DWM/Shell/COM/P/Invoke 的层
  │    实现 Contracts（mechanism：open handle/FILE_ID_INFO/move 原语/回收站 API）
  └─ native/deskbox_native    ← 位置不变，仍只经 Platform 层调用
进程外：deskbox-thumbnail-proxy（已是常驻 server，缩略图 + 右键菜单处理器宿主）
```

**依赖方向（契约测试强制）**：

```text
Feature → Contracts（要什么）← Platform implements（怎么做）→ Rust/Win32
FileSafety → IFileSystemPrimitives → Platform → Rust
Features.* 之间只允许经 Contracts/
逆向引用 = 构建期契约测试失败
```

**契约为高价值面服务，不搞接口税**：`IFileOperations`/`IFileSystemPrimitives`/`IAudioSession`/`ISyncTransport` 值得立（可测缝、可换实现、FileSafety 边界所必需）；不给每个平台调用都造接口——那又走回过度工程。

**资源租约（`IFeatureRuntime`，可选契约）**：只给持长生命周期资源的功能实现——Search、Music、未来 Sync；Todo/Weather/QuickCapture 不需要。enable → `StartAsync` 建租约；disable → `DisposeAsync` → watcher/COM 订阅/timer/缓存**归零**。这把本仓已有的手动契约（"disabling Search releases the complete search runtime"）形式化为可测契约，但不引入 PowerToys 式 `Initialize/Activate/Suspend/Shutdown` 模块生命周期。**状态机语义（契约的一部分，不只是签名）**：`StartAsync`/`DisposeAsync` 必须幂等——重复调用是 no-op 而非二次建租约；`StartAsync` 接受 `CancellationToken`，partial-start 失败必须按获取逆序回收已建资源再抛出（不许半租约泄漏）；`DisposeAsync` 与进行中的 `StartAsync` 竞争时按状态机串行化（Start 完成→Dispose，或 Start 取消→Dispose），不允许并发交错；`DisposeAsync` 超时不阻塞宿主关闭——记诊断并把未回收资源计入泄漏隔离清单。runtime 实例的所有权在功能注册表（feature registry），不由调用方缓存。

**contribution descriptor（新格子 = 1 处声明 + 3 处实现）**：

- 1 处：公共描述符表加一行（显示名/本地化键/标题图标/设置节归属/默认启用态/多实例/紧凑与胶囊策略位/存储根名/搜索与引导参与位）；
- 3 处：`IWidgetContent` 实现 + provider、设置节 UserControl、12 文化表加键；
- 按 kind 逐条列策略的位置（紧凑隐私、预热计划等）**有意保留**——那是功能自己的策略表达，不是宿主公共表（seam 文档已定此原则）。

---

## 3. 为什么是这个形态（对原提案的四处修正的理由）

### 3.1 单程序集，不拆 csproj——但要认清这是软边界

- **边界性质要诚实**：命名空间 + `internal` + architecture tests = **policy-enforced 软边界**，不是 compiler-enforced——同一程序集内 `internal` 挡不住跨命名空间调用，执法靠契约测试。强度对当前阶段够用、成本近零，但措辞不许说"等效"；
- **暂不拆的理由是 ROI，不是"跨不过"**：61 个文件的 `App` partial 本身是结构债，不能反引为"所以不该拆"的架构约束；真实理由是 MSIX + Native AOT 单工程发布链已稳定，多工程只增加 restore/packaging 摩擦；
- **物理拆分的升级触发条件**（满足其一再评估）：architecture test 频繁被破坏；某层已被边界证明干净且独立构建/测试收益明显；开始多人并行 ownership。**"目录看起来漂亮"不是触发条件**——届时拆的是"已经干净的层"，成本最低。

### 3.2 模块契约形态取 seam 文档，不取生命周期接口

格子是窗口 surface：有胶囊折叠、桌面层级、拓扑恢复，没有"模块激活/挂起"语义。`IWidgetContent` + contribution descriptor 已在仓库里验证过形状，直接采用。

### 3.3 切割顺序按"事故驱动 > 假设驱动"重排（第三轮修订）

原提案 WindowOrchestrator 第一刀。本仓的等价物（WidgetLayerService/Positioning/Topology）已分散但**不痛**。对照"过去三个月哪种痛实际发生过"：僵尸实例、启动连坐、三次内存事故、文件安全审计——**全发生过**；而"加格子碰 25 文件"一次没发生（近 3 月零新增 kind）。所以：App 生命线第一（事故已发生且有未堵同类入口）、数据分层第二（数据边界错=事故 + 云同步前置）、contribution descriptor 与新格子立项绑定（假设兑现时做，新 kind 自己当验收）。

### 3.4 "Domain 纯化"收敛为图像出模型

`Models/` 全仓只有 6 个 WinUI 依赖文件。其中 5 个是 AOT bindable 桥（生产代码，动不得）或轻微类型泄漏；真正值得做的是 `WidgetItem.BitmapImage`——2500 item × 解码位图是 native 水位的主要构成项之一（RISE 已结案为有界水位，此项转入可选刀评估）。这不是依赖洁癖，是有收益的内存工程。

---

## 4. 切割计划（每刀行为零变化、独立可验、独立可回滚）

> 排序原则（第三轮修订）：**事故驱动 > 假设驱动**。回应已发生事故的刀排前，回应假设的刀等触发条件。

### 第 0 刀（前置门禁，非架构刀）——已结案：PLATEAU
AOT 内存曲线四轮批次峰值 171→399→480→491MB，批间增量 +228→+81→+11（+2.4%，远低于 15% 阈值），末段 7 分钟私有内存钉死 ~490.6MB——**有界水位而非泄漏**，托管堆全程平稳（~35MB）。1.5.4 门禁已开，后续刀次不再受"排查基线漂移"约束。残留事项：~490MB 水位偏高——但它是**绝对预算**不是单项成本（含 WinUI runtime/.NET/Rust/窗口等固定开销），真实单项成本要用边际斜率 `(M2500-M500)/2000` 测；是否值得再降归入可选刀（§4）评估。

### 立法步（先于一切物理搬移，最便宜的一刀）——**已落地 2026-09-18**
**先立法，后搬家**：物理重构之前，先用契约测试把目标边界"声明"出来。已实现为 `tests/DeskBox.Tests/ModuleBoundaryContractTests.cs`（7 个测试，全部通过）：

- **ratchet 精确清单法**（file→count 违规清单，条目只许缩或消失，新文件出现即红——与冻结计数、阶段守卫同构，比裸总数预算强：杜绝"删 A 违规补 B 违规"的替换攻击）：
  - `DllImport`/`LibraryImport` 只允许出现在 `DeskBox.Platform` 域——当前清单 **260 个调用点 / 41 文件**，新增 P/Invoke 落 Platform 即不受罚，落别处立刻红；
  - `File`/`Directory` 的 `Move`/`Delete`/`Copy`/`Replace` 只允许出现在 FileSafety / Core.Persistence / Platform 域——当前清单 **121 个调用点 / 47 文件**；
  - `DeskBox.Models` 命名空间内 UI 依赖文件（WinUI/WinRT/Graphics 引用）清单 **6 个**；
- **休眠硬零法**（命名空间落地即生效，无需改测试）：
  - `DeskBox.Features.X` 不得 `using DeskBox.Features.Y`（跨功能只许经 `Contracts/`）；
  - `DeskBox.Core.Models`/`FileSafety.Models`/`Sync.*` 不得出现 WinUI/WASDK 类型——**法按语义不按文件夹名**，`UI.Models`/`ViewModels` 不受限（AOT bindable 桥是合法生产代码）；
  - `DeskBox.Sync` 不得引用 `FileSafety`/`Features`/`Platform`（只经 Contracts 读写同步域）；
  - `DeskBox.FileSafety` 不得携带 P/Invoke（policy 经 `IFileSystemPrimitives` 契约调 mechanism）。

**机制照抄本仓已有的 ratchet 模式**：立法本身几十行测试、当天进 CI；之后每一刀物理搬移都在已立的法护网下进行——搬错立刻红，而不是三个月后才发现边界又烂了。**这把本仓的契约测试纪律从"守数字"升级成"守结构"**。

### 第 1 刀：启动管线化（事故驱动，排最前）——首阶段 ✅ 落地（1.5.4 之后）

- **已落地**（首阶段）：`Services/StartupPipeline.cs` 管线引擎（criticality/duration/result/diagnostic 记录 + `WriteSummary` 结构化报告）；`App.Startup.cs` 现有 ~30 个可选步原样委托进管线（名称/位置/日志格式不变），新增 `RunCriticalStartupStep*`/`RecordStartupDegradation`；`OnLaunched` 关键步显式化（WidgetManager 构造+AuxiliaryWindowProvider 装配为 critical，恢复/widgets 降级收编进报告）；`StartupPipelineTests` 8 个故障注入测试全绿。**已立法的边界测试+管线测试+启动韧性契约共 26 个测试全绿**。余下验收项（僵尸路径实跑复测、启动计时无回退）待实机/实测；
- **为什么第一**：僵尸事故是**已发生**的真实事故，审计 §2 还点出 3 个未堵的同类启动期入口；而"加格子碰 25 文件"是**尚未发生**的假设（近 3 个月零新增 kind）。事故驱动优先；
- **内容**：`App.xaml.cs` 拆为**启动管线**而非散落 try/catch——每个启动步带 `criticality/duration/result/diagnostic` 四元组（**duration 是测量不是强制 timeout**——已落地形态只记录耗时；`AppLifecycleRecoveryWatcher` 覆盖灾难性挂死）：**Critical**（单实例/设置迁移/窗口宿主/托盘）失败→退出且**保证不被模态对话框挂死**；**Optional**（诊断/备份/更新/JumpList/Onboarding/通知路由）失败→**降级模式**（记诊断、该功能缺席、app 照跑）。散落 try/catch 会从"故障扩散"摆向"故障静默"，管线化让降级是**可见的降级**。**若未来引入强制 timeout，先写死语义再实现**：步接受 `CancellationToken`；超时步**不得继续提交状态**（WhenAny 判超时后旧任务仍在后台跑 = 竞态源）；超时步的已获资源由管线按清理归属回收，不由步自决；
- **验收（语义验收，不以文件大小为目标）**：App 无功能特定分支、不直接操作 FileSafety 事务、不持有功能运行时资源；Optional 步注入失败不致死**且有诊断记录**；僵尸事故路径复测；启动计时无回退。`App.xaml.cs` 体积只作观察指标——28KB 带 12 个全局可变状态比 40KB 纯 composition root 更差；
- **工作量**：中大。面广但机械性强；
- **价值**："少个功能"不再升级成"打不开"；顺带缩短启动路径。

### 第 2 刀拆为 2A/2B/2C：数据分层按节奏推进（审计修订）

原一刀塞了**代码 ownership、磁盘格式、云同步协议**三件不同的事，违反本方案自己的"每刀行为零变化"原则。拆开：

**为什么数据分层比代码分层急**：代码边界错了是难维护（可恢复），数据边界错了是安全事故（19.8MB、误恢复窗口）。且这是云同步的唯一前置缝——切数据时就按同步命运分三域：

- **同步层**（可上云）：待办（`TodoWidgetStore` 数据文件）、随记（QuickCapture 内容库）、偏好白名单子集——**注意不是全部配置**：显示器拓扑、存储路径等是设备相关，同步会跨设备打架；
- **本地层**（绝不同步）：整理历史、撤销回执、恢复 WAL journal——单机事务数据，归 **FileSafety 域**所有；
- **设备层**（默认不同步，将来可选）：格子布局、拓扑记忆。

#### 第 2A 刀：Settings ownership 切片（不改磁盘 schema）——首阶段 ✅ 落地

- **已落地**（结构切片，零调用点迁移）：`AppSettings` 降为 facade——`SchemaVersion` + 12 个 `[JsonIgnore]` get-only 切片引用（`Core`/`Performance`/`QuickCapture`/`Todo`/`Music`/`WidgetShell`/`FileWidget`/`Backup`/`DesktopOrganization`/`WidgetLayout`/`Weather`/`Search`）+ 207 个**同声明序**透传属性（`JsonPropertyName`/`WhenWritingNull` 等线级特性留在门面上）。磁盘 schema 零变化：全量套件 **3747/3747 绿**，含"预切默认值逐属性钉固 + JSON 成员顺序钉固"的生成基线（`SettingsSliceContractBaselineTests`，由一次性生成器从原文件提取）和映射完备性/透传保真/往返字节等价契约（`SettingsSliceOwnershipContractTests`）。功能代码引用切片的迁移走 ratchet（同 Platform 原则：新代码用切片，存量随触碰迁）；
- **定位**：`AppSettings` 208 属性超级对象的**代码 ownership** 先切开——降为 facade，下分 General/Appearance/Todo/QuickCapture/Search/Device 等切片，**磁盘序列化格式不变**（仍写回同一 settings.json）；
- **为什么独立**：只动代码所有权，几乎零用户数据风险——这才符合"行为零变化"的第一刀；
- **硬约束：不物化 ≠ 不保留**——禁用模块的设置节不构建强类型对象，但 raw JSON 段必须 round-trip 透传，否则 disable→save→enable 会重置配置；
- **验收**：settings.json 往返字节级等价（含禁用节透传）；facade 引用 ratchet 生效（每文件 `AppSettings` 引用数只减不增）；facade 透传数随迁移下降（目标归零——否则 god object 变 god façade + 12 slice）。注：**2A 首阶段的收益是 ownership seam 不是对象图**——207 透传 + 12 slice 对象在迁移完成前对象数持平甚至微增，"缩小"只在 facade 消解后才成立；
- **工作量**：中。面广但格式零风险。

#### 第 2B 刀：本地持久化域分层 + 迁移（数据边界，最敏感一刀）

- **2B-1 同步层字段预埋 ✅ 已落地**（加性变更、不搬数据）：`TodoItem`/`QuickCaptureItem` 增加 `device_id`（`entity_id`=`Id`、`updated_at`=`UpdatedAt`、墓碑=`IsDeleted` 均已存在；`TodoItem` 补了 `IsDeleted` 槽位）。`Services/DeviceIdentity.cs` 在 `<dataDir>/device.id` 持久化 GUID，两个 store 的 `Normalize` 里 `item.DeviceId ??=` 回填——存量记录下次加载/保存时自动补齐，外来 device_id 保留不覆盖。`SyncLayerFieldsContractTests` 4 测试。旧版读新文件忽略未知成员，安全；
- **2B-2 本地层迁移 ✅ 已落地**（undo receipts → FileSafety 域）：新建 `DeskBox.FileSafety` 命名空间，`DesktopOrganizationHistoryStore` 是第一个住户（立法预设域名首次启用——新代码落正确命名空间的 ratchet 规则首次生效）。`desktop-organization-history.json` 在 `LoadAsync` 里对旧 `recentOrganizationHistory` 做 fail-closed 领养——文件成为权威后才清空 settings 列表，写失败则保留旧列表下次重试。**#393 时序冻结保持**：所有曾提交历史变更的 `SaveChecked/SaveAsync` 点位都成对加了 `OrganizationHistory` 保存（守卫规则——历史条目是 reconcile 的守卫，**新增先存、删除后存**：commit 是 history→settings，删除类是 settings→history），store 侧复刻 `SaveCheckedAsync` 语义。`DesktopOrganizationHistoryStoreTests` 9 测试，全量 **3759 绿**；
- **2B-2 复审修复（已落地）**：外部评审发现 receipt 持久化四处用了会抛的 `SaveAsync`（原 settings 路径是吞异常的 best-effort——文件已移动后落盘失败会误报失败/替换回滚原异常），全部改回 `SaveCheckedAsync` 并以源码 pin（`ProductionCode_PersistsReceiptsWithCheckedSaves`）封禁未检查调用点；HistoryStore 接入 `ResilientJsonStore`（`.bak`+`.corrupt-*` 隔离+校验写入），补齐相对 settings.json 时代的持久化保护差。**journal store（WAL 权威）同款 retrofit 已落地**：`DesktopOrganizationRecoveryStore` 接入 `ResilientJsonStore`——损坏 journal 自动 `.bak` 自救+隔离（旧代码直接抛），`Clear()` 连 `.bak` 一起删防"已清事务复活"，STA 同步 `Save` 经 `Task.Run` 隔离防 dispatcher 死锁。**crash-matrix 复审修正（已落地）**：①两文件提交线性化点翻正——`settings → history → clear journal`（history receipt 永远最后落，receipt 存在⇒两半都 durable；旧顺序 history 先落在 settings 失败+回滚写同败的关联故障下会留"假 commit evidence"→孤儿态；回滚段保持 history 先写——先撤 receipt 再还原 settings，两处 undo reconcile 同款翻正）；②`Clear()` 改 `.bak` 先删、primary 后删——中间 crash 只留 primary 终态，防 `.bak` 复活 abandon 前旧 WAL；`HasPendingJournal` 含 `.bak`；③备份对 settings/history/journal 三文件在 `OperationGate` 持锁下拷备——同一事务纪元，防 torn snapshot；④`DeviceIdentity.Id` 首发加锁防双 GUID 分裂；⑤备份 metadata 集合在 `OperationGate` 内用定死路径 `File.Exists` 解析——gate 外枚举会漏掉等待期间新建的 journal（settings@T1+history@T0+无 WAL 的撕裂态）；内部恢复件明确排除出备份，但 `attachments/` 路径段一律按**用户数据**直通——任何扩展名/sidecar 启发式都不得过滤它（托管附件保留用户原文件名，`config.json.bak`、`file.tmp` 都是合法附件名，按名字过滤即备份数据丢失）；非附件路径上的 `<store>.json.bak`/`<store>.json.corrupt-*` 才按 `ResilientJsonStore` sidecar 约定排除；`ResilientJsonStore` 对损坏 `.bak` 对称隔离——否则 corrupt bak-only journal 让 `HasPendingJournal` 永真而 `LoadAsync` 永 null，Execute 永久拒绝（死锁）；
- **2B-2 已知限制（记录在案）**：① `device.id` 已从备份排除（installation-local 身份而非用户数据）——恢复时 `DeviceIdentity.GetOrCreate` 自动重生成，跨机克隆已消除；同机恢复后旧记录的 device_id 指向"上一代设备"，属诚实语义。2C 立项时如需更强连续性再决策机器指纹绑定；② 新版迁移后回退旧版：旧版写进 settings.json 的履历史录在"文件即权威"规则下被丢弃——降级非受支持流，属既定取舍；
- **定位**：真正动磁盘——三域各落独立存储边界，待办/随记数据文件归位同步层；
- **硬约束（事务边界保护）**：`RecentOrganizationHistory` 持久化时序**冻结**——#393 六轮稳定的 SaveChecked 提交顺序、WAL finalize、cap 在 journal 清除后执行，**不许被迁移顺手搅动**；历史和 journal 划归 FileSafety 域，不参与"设置归功能"的划分逻辑；
- **同步层字段预埋**：记录带上 `entity_id`/`device_id`/`deleted`（墓碑）/`updated_at`——现在带几乎免费，事后补要动模型+迁移；
- **验收**：三域归属契约测试（同步层字段集可枚举、本地层无用户内容数据、设备层标记完整）；fail-closed 迁移规则贯穿；真实数据迁移 + 回滚演练；
- **工作量**：大。迁移敏感——沿用刚落地的 fail-closed 语义。

#### 第 2C 刀（云同步立项绑定）：Sync projection + 协议契约
- **定位**：同步是**投影**不是污染——`Local Todo → SyncRecord<Todo>` 经 projection 层上行，云协议不进入本地 domain model。**界限划明**：domain 记录携带身份 + 轻量 provenance（`Id`=entity_id、`DeviceId`=最后写入者、`UpdatedAt`、`IsDeleted` 墓碑——均有本地用途）；协议状态（`server_revision`/`base_revision`/`operation_id`/冲突元数据）**永远不进 domain**，放 sync store 按 entity_id 关联——那类字段只在 sync 存在时才有意义。架构只钉三个契约：`ISyncTransport`/`IAuthSession`/`SyncEnvelope`，后端选型（Supabase/Azure/自建 REST）是实现细节不写进架构；
- **最小同步契约**（不用 wall-clock LWW 当唯一真相）：`entity_id`/`server_revision`（服务端单调发放）/`base_revision`/`device_id`/`operation_id`/`deleted`——乐观并发检**真冲突**；UX 策略仍可选自动 LWW，但"发生了冲突"是已知事实，不是两台设备时钟互猜；
- **墓碑 GC 预设计**：服务端带 cursor/epoch——离线过久设备上线直接 full snapshot replace，不无限 replay，防 deleted item resurrection；
- **验收**：冲突注入测试（双设备离线改同条→服务端报 conflict）；超期设备全量替换路径演练；
- **数据量判断不变**：KB 级数据不需要 CRDT——revision 乐观并发 + 墓碑已足够。

**双渠道注记**：商店版 `%LocalAppData%` 写入被 MSIX VFS 重定向到 `Packages\[PFN]\LocalCache`，直装版直写——`DeskBoxDataPathService` 经 `SpecialFolder.LocalApplicationData` 已透明盖住这条分叉，同步引擎只走这层抽象即渠道无关。附带：商店版卸载即清 LocalCache，云同步对商店用户是数据安全加分项。

### 第 3 刀（与新格子立项绑定，不提前做）：Contribution descriptor 收敛
- **为什么降级为触发式**：它回应的"加格子碰 25 文件"是**尚未发生**的假设——近 3 个月零新增 kind（`Tags`/`SystemMonitor` 在枚举里占位但未实现）。等下一个新 kind 立项时做它，**让新格子本身当验收**："1+3 处落地"是不是真的，做一个格子立刻见分晓，比空转重构诚实；
- **内容**（= seam 文档 step 1+2）：三张注册表合一为单一描述符真源；`FeatureWidgets.cs` 34 处分支按能力归类改描述符能力位；随后第一个新 kind 按新缝落地；
- **设计护栏**：能力位超过 ~5–8 个时改 `Capabilities` 集合/策略对象，防 `SupportsX` 布尔汤；
- **验收**：契约测试钉住现有 kind 清单与能力矩阵；行为零变化；新 kind 全程不碰 `FeatureWidgets.cs`；
- **工作量**：中。纯搬运 + 分支语义改写 + 新格子首验；
- **价值**：新功能边际成本封顶——做的时候 ROI 立即兑现，而不是先付再等。

### 持续项（不设专刀）：Platform 收口 + Feature 命名空间收敛
- **方式**：自立法日起**新增 P/Invoke 必须落 `DeskBox.Platform`**；存量 ~40 处随功能触碰顺手迁（40→35→…→0），`Features.*` 命名空间与文件位置跟着自然开发收敛——**不做"一次性大搬家"PR**：diff 大、收益有限、制造 git history 噪声；
- **配套**：依赖方向契约测试入 CI（Feature→Contracts、Platform implements Contracts、FileSafety→`IFileSystemPrimitives`、Feature 间仅经 Contracts）；
- **验收**：ratchet 计数单调下降；`grep DllImport` 来源数只减不增；
- **价值**：边界由构建执法且不占发版节奏——bounded problem，不是专项重构。

### 可选刀：图像/图标数据出模型层
- **定位**：水位优化期权——把 ~490MB 的 native 水位再降一档；**不是正确性修复**；
- **内容**：`WidgetItem` 等模型去 `BitmapImage`，解码图像统一进有预算的图像服务（图标缓存、缩略图、Glance 图各自配额）；模型只留路径/键；
- **触发条件（二选一）**：真实用户反馈内存压力；或多格子大文件夹场景**边际斜率实测**超既定内存预算——先跑 `M(0)/M(500)/M(2500)` 三点测 `(M2500-M500)/2000`，别用 `490MB/2500` 当单项成本（固定开销占大头）；
- **验收**：图像服务命中率/驱逐/预算契约测试；批量导入 2500 项内存曲线复测；
- **工作量**：中。触碰 hydration 与 render window 热路径，要小心。

**每刀公共约束（第三轮新增）**：
1. **测试跟着切**：2632 个测试方法与源码同构归位——每刀落地时把对应测试迁入模块边界，否则三个月后维护成本从源码转移到测试套件（冻结计数、JSON 基线、阶段守卫已有"新增 X 同步 N 处"的坑）；
2. **FileOperationService 不单列刀**：`FileService` 已是那个内核，`IFileOperations`/`IFileSystemPrimitives` 契约随立法步和持续项顺手提出即可。

---

## 5. 对性能与内存的长期影响（诚实清单）

| 维度 | 影响 | 机制 |
|---|---|---|
| 内存——直接 | **中性为主，一处正收益** | 重排代码不省字节；省内存的是"谁负责释放"有了归口 + 图像出模型 |
| 内存——间接 | 正收益 | 功能级资源所有权（disable=release 经 `IFeatureRuntime` 租约成可测契约）；Settings 2A 切片在 facade 消解后削启动对象图（当前为 ownership seam） |
| 性能——调用 | 零成本 | 进程内模块 = 直接方法调用，无 IPC/序列化（对照 PowerToys 命名管道开销，一个不付） |
| 性能——启动 | 正收益 | App 功能阶段后移，首启路径变短 |
| 性能——缓存 | 正收益 | 模块各自有界缓存，替代全 app 隐形池 |
| 性能——纪律红线 | 须守住 | render window / hydration / watcher / mutation scope 热路径**不许插接口分发**；边界画在调用者一侧 |
| 可靠性 | 高价值 | 功能失败≠app 失败；僵尸事故同类缺口收敛 |
| 演进 | 期权价值 | UI 无关资产（文件安全内核/设置协议/恢复日志）在 UI 框架换代时可携带 |

**三个月的内存问题史印证同一个模式**：LOH 282MB 垃圾（无人拥有缓冲所有权）→ settings.json 19.8MB（无人拥有回执生命周期）→ AOT 内存曲线排查（定案有界水位，但排查过程难恰因资源所有权不清）。三个问题同构：**"没人对资源生命周期负责"**。这是该方案最硬的长期论据。

## 6. 对商店/打包的影响

**零影响，一包到底。** 模块是编译期边界不是部署单元：单 exe、单 MSIX、单商店条目、单次送审；新格子 = 代码 + 描述符一行 + 本地化键，随版本更新同包下发。先例已在：`deskbox-thumbnail-proxy.exe` 独立进程也装在同一 MSIX 内。只有走回已放弃的"独立可下载功能包"（MSIX optional package 或自建包系统）才会变，方案明确排除。

附加收益：模块边界 + 功能开关让新格子出问题时可单独禁用，降低整包回滚概率——对商店评分是保护项。

## 7. 失败模式（这么做会变错的五种走法）

1. **重构成框架**：为"未来 100 个模块"造抽象——pluginization 存档（184 提交）就是这条路的尸检报告。纪律：只支撑现有 7–10 个内部模块。
2. **边界画错**：教条"功能间零引用"会伤产品（统一搜索必须跨读）。解法：跨读合法但经 Contracts 接口。
3. **重构回潮**：排查已结案，但大件重构后应复跑一次内存曲线基线——若水位回升超阈值说明边界改动引入了新的持有路径。
4. **物理拆分当目标**：拆程序集是满足 §3.1 触发条件后的升级路径，不是目标——当前成本实在、收益近零。
5. **用文档过度工程架构**：三轮复核 + 一轮审计已收敛。继续设计是从"避免过度工程"变成**用文档过度工程**——下一步的学习来自立法步和第 1 刀进 release，不是第五版方案。

## 8. 与微软路线的对照（已核验）

- PowerToys 的 Runner/Module/IPC 是**产品形态逼出的架构，非微软推荐**——官方文档确认，方向评审稿判断准确；
- 微软官方推荐 = WinUI 3 + Windows App SDK（本仓 WASDK 2.4，已在推荐栈上）；
- 微软自有迁移方法论 = bounded problem 分阶段，反大爆炸重写（PowerToys WPF→WinUI3 skill、WASDK 迁移文档同口径）——与本计划的"每刀独立可发"同构；
- Native AOT：WASDK 1.6 起官方支持，保留；
- MVVM + MVVM Toolkit + DI：微软官方教程形态，本仓已在用。

## 9. 节奏建议（第四轮：审计版刀序）

```
立法步：✅ 已落地（ModuleBoundaryContractTests，7 测试全绿；精确清单 260/41 文件 + 121/47 文件 + 6 文件，条目只缩不增）
1.5.4：✅ 已发版（本地）
第 1 刀：首阶段 ✅ 已落地（StartupPipeline + 关键步显式化 + 8 故障注入测试；
         僵尸路径复测/计时无回退待实机验收），随 1.5.5/1.5.6 实跑观察
第 2A 刀：首阶段 ✅ 已落地（AppSettings facade + 12 切片 + 207 同序透传；
         3747 全绿，默认值/成员顺序双钉固；调用点迁移走 ratchet）
第 2B 刀：2B-1 字段预埋 ✅ + 2B-2 本地层迁移 ✅（DeskBox.FileSafety 首个住户＝历史 store；
         剩余: 设备层迁移[Widgets/WidgetGroups/拓扑→设备域 store]——触面最大（~330 处），
         与 sync 立项绑定执行：其唯一立项理由是同步前置，提前做即"假设驱动"）
云同步立项时：第 2C 刀 Sync projection + revision 协议契约
新格子立项时：第 3 刀 contribution descriptor（新 kind 自己当验收）
持续进行：Platform P/Invoke 随触碰迁移（ratchet，不设专刀）
触发才做：图像出模型（边际斜率实测超预算或用户内存反馈）
需要时：危险第三方组件 → helper 进程（thumbnail-proxy 先例）
```

**云同步注记**：Sticky Notes 的先例就是这条路——本地优先 → 蹭 OS roaming → roaming 被微软自己废弃 → 被迫自建。**账号+云从设计第一天就当自建，不蹭任何 OS 通道**（`RoamingSettings` Win11 已死）。协议侧最小契约见 §4 第 2C 刀（revision 乐观并发 + cursor/epoch 快照替换）；传输只钉 `ISyncTransport`/`IAuthSession`/`SyncEnvelope`，后端选型是实现细节不写进架构。引擎落地即 `Features/Sync` 普通模块，独立队列事件驱动，**绝不挂进 settings 防抖保存的热路径**。商店渠道：免费同步无合规增量；收费对 PC 非游戏应用可走 Microsoft IAP **或**合规第三方购买 API/第三方 recurring billing（Store Policy 10.8 允许；10.8.4 是购买信息披露义务，不是"IAP 强制"）。

**明示不解决的成本**：直装/商店双渠道维护（自启动×2、更新链×2、打包×2、审计×2）每月都在付，模块化对它零帮助——这是产品/运营决策项，不在本方案射程内。

**可选项**：功能使用遥测（哪个格子被创建、哪个 30 天零使用）可指导砍/加功能决策；但产品承诺 local-first，只能做**显式 opt-in**，不做则零损失。

原则照原提案，且与 PowerToys 自身实践一致：**work on bounded problems, not the entire codebase at once**。每个版本只切一个 ownership boundary，不暂停产品，不搞 `refactor/*` 大爆炸分支。

## 10. 云备份立项（WebDAV 首刀，2026-09-18 用户决策）

**立项性质**：云**备份**（单向快照+按需还原），不是云同步（双向合并）。用户自配置 WebDAV；将来官方账户云只是 `ISyncTransport` 第二个实现，接缝/UI/凭证不动。与 §4 第 2C 刀不冲突不替代——envelope 语义将来可升级 merge，`updated_at`/tombstone 预埋字段继续待命。

**v1 域开关（用户拍板，三个独立 toggle）**：

| 开关 | 内容 |
|---|---|
| 待办数据 | `data/widgets/*/todo.json` + 各 widget `attachments/`（托管附件保留原名，一律用户数据） |
| 随记数据 | `data/quick-capture/quick-capture.json` + `attachments/`（thumbnails/exports 仍排） |
| 格子样式 | `WidgetShellSettingsSlice` 全量 + `WidgetConfig` 逐字段**样式投影**（排 position/topology/monitor——跨设备打架，用户明确"样式同步/布局不同步"） |

**永不备份**：`device.id`、journal、history、`.bak`/`.corrupt-*`、`cache/`、`thumbnails/`、`settings.json` 本体、文件类格子指向的桌面文件内容（结构性白送——文件格子只存路径引用，内容从不进 data 目录）。

**明确不加（v1）**：通用偏好（语言/主题/天气城市等非样式非设备设置）——用户拍板最小面，提了再加。

**还原语义**：列远端快照 → 选一个 → 逐域显示元数据（来源设备/时间/记录数）→ 逐域确认覆盖。无合并、无冲突队列。

**凭证纪律**：Windows Credential Manager / DPAPI；settings.json 只存 provider/url/路径/开关，**密码一个字符不落盘**。

**远端布局**：`<用户路径>/DeskBox/backups/<时间戳>-<deviceid8>.zip`，保留最近 N 份（默认 5 可调）。

**PR 分解**：PR-1 域范围快照+样式投影+凭证封装（纯本地）；PR-2 WebDAV transport+编排；PR-3 设置 UI；PR-4+ 官方云/真同步升级（换 envelope 语义/transport 实现）。
