# DeskBox 模块化与插件平台架构方案

- 方案日期：2026-09-07；v1.1（外部评审 16 条+代码复盘）；v1.2（开源全景调研，§13）；v1.3（Protocol First 收敛，§14）；v1.4（第三轮评审 12 条收口=架构 Baseline）；**v1.5（复盘修订：阶段 2 选型 Music 试点/阶段 3 改写为三切点剥离/新增阶段 2.5 schema v0/Broker 落点定案/依赖边修正+护栏补洞清单，§16）**
- 代码基线：main `94d0e99f`（DeskBox 1.5.0）；执行进度：阶段 0/1/1.5 已落地（`6c7d2827`/`ee35bf32`，3376/3376 绿）
- 评审记录：见第 12 节（v1.0 → v1.1）、第 13 节（v1.1 → v1.2）、第 16 节（v1.5）
- 当前状态：**执行中**。阶段 2（批次 C 数据卫生 → Music 试点）待开工

---

## 1. 目标与非目标

**目标**

1. 主功能（文件格子 + 窗口/胶囊/合并/叠放体系）与辅助功能格子（Weather/Todo/Music/Glance/Search/QuickCapture）解耦，各自可独立演进。
2. 应用商店：功能按需下载安装，而不是全部打包进本体。
3. 能力 SDK：把 DeskBox 已有能力（Windows 文件/Shell 封装、格子/叠放/胶囊操作）以受控接口暴露给插件调用；支持"文件 ↔ 插件 ↔ 插件"的关联机制。
4. AI 能力：宿主提供模型调用代理（BYOK 或账号托管），插件与 AI 功能统一经能力层访问，密钥不落地到插件。
5. **（v1.1 升级为一等目标）AI 生成插件**：用户/AI 依据规范自动生成并验证插件——由此插件 CLI/Validator 从"副产品"升级为核心基础设施（见 7 阶段 6）。
6. SDK 是能力层的副产品，不单独立项。

**非目标（明确排除）**

- 不改写 WinUI/XAML 技术栈，不引入 Electron。
- 不放弃 NativeAOT（`PublishAot` + `PublishTrimmed` + 全量警告门禁保持不变）。
- 不做运行时 `Assembly.Load` 式进程内 .NET 动态加载（NativeAOT 不支持，见第 2 节）。

## 2. 硬约束（技术事实，已核实）

1. **NativeAOT 禁止动态程序集加载**：`Assembly.Load`/运行时代码生成均不可用（dotnet/runtime #117470）。**DeskBox 不做运行时 .NET 程序集加载**；对于第三方可执行代码，优先评估 **WASM 组件**（进程内沙箱）与**进程外 Runtime**两条路线（理论上还存在内嵌脚本引擎/DSL 解释器等路线，当前不评估）；声明式资源包独立于代码 Runtime。
2. **测试与审计体系按"文件路径 + 计数"冻结**：147 个路径式源码扫描契约测试（24 个文件含 52 处硬编码 `WidgetManager.*.cs` 路径）；`JsonSerializationBaselineContractTests` 冻结 28 文件/64 调用/26 context（**v1.5 起已多项目扫描**）；`AotStage4D1BContractTests` 冻结 XAML 计数 347 项；`scripts/publish-aot-audit.ps1` 含 227 处硬编码路径、`auditProfileVersion`（批次 B 起=59，动脚本须 bump 并同步全部钉版测试）、**31 处 WMC1510 等值断言（=863）+4 处 ceiling**。护栏改造已在阶段 0/批次 A/B 落地（SourceFile 重定位接线/多根扫描/audit 全管线重对齐），后续搬迁税按"每文件一条映射+触碰点同步"重估。另有 63 个测试文件直接断言 DeskBox.csproj 内容——"项目结构"本身是被测试冻结的一级接口。
3. **本地化资源经 `Assembly.GetExecutingAssembly()` 读取**（LocalizationService.cs:471；CitySearchService.cs:72 的 cities.json 同模式）：代码搬到子程序集后静默回落。本地化留宿主 + 资源加载器抽象。
4. **设置系统**：`AppSettings` 约 200 字段单一类、684 处直接读写、14 个 Normalize 双向全量执行；`SchemaVersion=9` + 10 级迁移链；备份服务强制含 settings.json。
5. **AOT 红线**：OneTime x:Bind 模式不改回；`SearchResultRowControl.Item` 保持 internal；`*.AotBindableProperties.cs` 必须随 ViewModel 同程序集；**`App.Aot*Smoke.cs`/`WidgetManager.Aot*Smoke.cs` 是宿主类 partial，物理上搬不走**——每个功能的 AOT 证据链天然横跨两个程序集（v1.1 新增：这是反对多程序集拆分的结构性论据）；每新程序集必须复制 retail smoke 排除 ItemGroup、提交双锁文件、加 IVT。
6. **分发管线**：Direct（Inno 整包）/Store（MSIX）双通道。**安装层今天没有任何按功能裁剪的基础**（.iss 无 [Components]，appxmanifest 无 OptionalPackage，全新安装功能开关默认全关）——物理拆程序集在分发层没有现成消费者（v1.1 核实）。
7. **（v1.2 修正）wasmtime-dotnet 维护现实**：v1.1 的"44.0.0 停在 2025-08"**有误**（那是 34.0.2 的日期）。实际：34.0.2（2025-08-05）→ 9.5 个月空窗 → **44.0.0（2026-05-23）**，commit 持续到 2026-08，已支持 .NET 10、新增 epoch deadline callback。即"慢而活着"，落后上游约 4 个大版本/3.5 个月，官方态度"认养但非重点"。**且经 v1.2 调研确认：当前评估的托管 .NET WASM 库（wasmtime-dotnet、Extism）均无 WIT/组件模型支持，给不了类型化插件 ABI；完整的 Component/WIT 宿主能力只有 Rust 宿主路径**（见 13.3）。

## 3. 现状与耦合点

**分层**：`DeskBox.sln` 仅 3 项目（主程序、Updater、Tests），所有功能/契约/XAML/设置页在 `src/DeskBox` 单程序集内。

**widget 体系**（现状抽象质量好，是拆分的接缝）：`IWidgetContent` 生命周期+能力接口族（public，**Step 1 已迁入 `src/DeskBox.Abstractions/Contracts/`**）；`IWidgetContentProvider`/`Context`（internal，Context 携带具体类型，留守宿主）；`WidgetContentFactory`/`WidgetRegistry`/`FeatureWidgetSettings` 三处重复注册；`WidgetManager` 约 1.2 万行 partial 群；`WidgetShellContentHost` 纯接口生命周期事务。

**主要耦合点**（难→易）：单体 csproj；`WidgetManager.FeatureWidgets.cs`（1392 行：Todo reminder 穿透、Glance 多实例、QuickCapture×File 落盘 140 行、App 级回调）；File 双注册表不变式；FileSurfaceContent 20 处 ambient 环；AppSettings 横向贯穿；ContentWidgetWindow 25+ 处 `is` 强转；App.xaml 单体字典；QuickCaptureWidgetWindow 6400 行死代码。

**现成接缝**：叠放算法不在 WidgetManager；胶囊排布 kind 无关；z-order/托盘动画/协同移动零 File 耦合；GlanceWidgetStore 独立存储（类注释明说四分类意图）；全部功能 ViewModel 已 public。

**（v1.1 新增）数据层现状**：

- `WidgetConfig.Metadata` 约 9 个 key 家族，全部单一写入者，磁盘上无实际冲突；但无中央注册表、双命名风格并存、`QuickCaptureMasterPaneWidth` 字面量两处重复声明、FileStack 4 个 key 存"JSON 套 string"双重序列化且解析失败静默容忍。
- per-feature 存储四分法（全局设置/实例状态/数据/缓存）**物理结构大部分已存在**（glance/widgets/{id}.json、todo per-widget、cache/ 目录、thumbnails/ 排除备份）；混乱点：settings.json 一文件混装、**weather-cache.json 与 cache/glance 目前被卷进备份**（ShouldIncludeInBackup 只排 tmp/thumbnails/exports）。
- **已知真实缺陷**：删除 Todo 格子后 `data/widgets/{id}/todo.json` 与附件**无人回收**（`WidgetRemoved` 事件零订阅者，仅 Glance 有 DeleteForWidgetAsync 清理钩子）。
- 密钥存储：全仓库零 DPAPI/ProtectedData 使用；唯一 key 是天气服务硬编码公共端点 key。为不存在的密钥建加密层属超前建设。

## 4. 外部架构调研结论

| 模式 | 核心机制 | 对 DeskBox 的启示 |
|---|---|---|
| Agent harness | 极小内核循环 + curated 工具 API 面 + 分档权限门控 | DeskBox 对应物 = Capability Broker |
| VS Code 扩展宿主 | 独立进程 + curated 命名空间 + activation events 惰性激活 | 激活事件设计参考；其"扩展共享一个 Node 进程无逐插件沙箱"是前车之鉴 |
| Zed WASM 插件 | wasm32-wasip2/WIT 组件 + Wasmtime 进程内沙箱 | 动态加载主路线；**WASI 0.3 已于 2026-06-11 正式发布**（原生 async、stream<T>，Wasmtime 43+ 支持）——定位注意（v1.4）：WASI 0.3 是 **WASM Adapter 的实现语义**，不反过来定义平台 Extension Model（平台自定 Task/Stream/Subscription/Cancellation/Resource 语义，各 Adapter 各自映射）；工具链 RC→final 的 pin 问题需在 spike 中验证 |
| Figma 双环境 | 逻辑沙箱 VM + UI 隔离 iframe + 纯字符串消息 | "WASM 逻辑插件 + 宿主渲染 UI + JSON 消息"原型 |
| Windows 11 小组件板 | 宿主全 WebView2 渲染 + adaptive 内容 | 宿主渲染模板的正确性佐证 + WebView2 共享单进程的教训 |
| MCP | tools/resources/prompts 协议，stdio 本地传输 | DeskBox 可当 server（外部 AI 操作格子）与 client；**定位为能力子集投影而非等价投影** |
| BYOK vs 托管 | 混合模式收敛 | 先 BYOK（DPAPI 存 key、经 `ai.*` 代理），托管等付费用户 |
| （v1.1）Wasmtime 资源限制 | **epoch interruption**（墙钟中断，~10% 开销，快于 fuel）、**fuel metering**（确定性计费，开销高）、**ResourceLimiter**（Store 级内存/表增长上限） | 推荐组合：epoch + ResourceLimiter 为主、fuel 仅需确定性计量时用——直接落实插件资源预算层 |

## 5. 目标架构：Capability Broker Harness（v1.1 修订）

```
┌────────────────────────────────────────────────────────┐
│  DeskBox Harness（核心进程，NativeAOT WinUI）            │
│                                                        │
│  宿主内核（不插件化，永远保留）                           │
│  ├─ 窗口/胶囊/合并/分组/z-order + 文件格子引擎           │
│  └─ 设置/本地化/更新/分发                                │
│                                                        │
│  Capability Broker（薄壳，防上帝类）                     │
│  ├─ 发现（Capability Registry）                         │
│  ├─ 权限（细粒度 permission + scope 判定）               │
│  ├─ 路由（Dispatch → 各 Provider）                      │
│  └─ 审计（调用日志/预算计量）                            │
│                                                        │
│  Capability Providers（实现各自独立）                    │
│  ├─ widgets.*  ├─ files.*   ├─ associations.*           │
│  ├─ storage.*  ├─ events.*  ├─ ui.*   └─ ai.*          │
│                                                        │
│  适配层：Extension Model（语言无关语义契约）的多种投影（v1.4 重分层）│
│  ├─ .NET Adapter（进程内官方功能）                       │
│  ├─ WIT Adapter（WASM 插件 → 组件模型）                  │
│  ├─ RPC Adapter（进程外插件 → JSON-RPC/stdio）           │
│  ├─ Declarative Adapter（资源包 → JSON Schema）          │
│  └─ MCP Adapter（对外子集：AI 可安全操作的能力）          │
└────────────────────────────────────────────────────────┘
```

**设计原则（v1.1 修订后共 9 条）**

1. **Broker 只做"发现→权限→路由→审计"四件事**，能力实现在各自 Provider 里——防止 Broker 长成新的 WidgetManager。
2. **一个语义模型，多种协议适配**：WIT/.NET/JSON-RPC/MCP 的 async、流、资源句柄、取消、生命周期语义不完全一致，各 Adapter 各自表达；MCP 明确定位为"AI 可安全操作的能力子集"，不是插件运行时 API。
3. **associations 是一级领域概念**（能力命名空间含 `associations.*`）。实现分两步：设计期即按一级概念建模；落地时第一版只做"类型化轻模型"（source/target/relationType），`ownerPlugin/permissions/state` 等字段等插件生态出现后再加。`WidgetConfig.Metadata` 只存实例级杂项设置（pane 宽度、view mode 类），**不做关系数据库**。
4. **权限 = 细粒度 capability + scope，三档信任只是 UX 映射**：底层是 `files.read(scope=目录)`、`network.fetch(scope=api.github.com)`、`ai.complete(budget=100/day)` 式声明；上层映射为自动允许/首次确认/逐次确认三档交互。
5. **插件数据四分类**：全局设置 / 实例状态 / 累积数据 / 缓存，外加密钥（密钥只进宿主安全存储，插件永不接触明文）。目录布局沿用现状并规范化：`plugins/{publisher.plugin}/settings.json|data/|instances/{id}.json|cache/`。现有备份边界缺陷（缓存入备份）与 Todo 孤儿数据在阶段 2 一并修。
6. **AI 是一个 capability**（`ai.*`），宿主实现，key 经 DPAPI 存储，BYOK→托管切换对插件零感知。
7. **插件生命周期 = 三条正交状态机（v1.4 拆分，取代 v1.1 的单链状态机）**——单一维度链条无法表达"包已启用但运行时未启动""包更新中旧运行时仍在跑""运行时在跑而格子 A 可见 B 隐藏"这些合法组合：
   - **Package**：NotInstalled → Installed → Enabled ⇄ Disabled；Updating；→ Quarantined
   - **Runtime**（进程/VM 实例）：Stopped → Starting → Running ⇄ Suspended → Stopping；Crashed（→按崩溃治理策略重启或升级 Quarantined）
   - **Widget Instance**：Created → Restored → Visible ⇄ Hidden → Disposed（对齐现有 WidgetConfig.IsVisible/IsDisabled + 窗口生命周期）
   现状映射：`SetFeatureWidgetEnabledState` 已是 Package 层 Enabled/Disabled 的事实实现（拆窗+Dispose 服务+惰性重建）；`IsAvailableForSession` 是激活闸门；`OnWindowLongHidden` 挂 Runtime Suspended / Instance Hidden。
8. **激活事件声明式化，且实例恢复与运行时激活彻底分离（v1.4 修正）**：onWidgetOpen/onStartupFinished/onSchedule/onCommand/onFileAssociation 等，manifest 声明→路由到现有触发源（全局热键/搜索热键/桌面双击钩子/剪贴板监听/定时器/托盘惰性建窗——全部已在生产运行）。**正确顺序是：恢复 Widget 实例元数据（位置/大小/胶囊关系/分组）→ 恢复窗口/壳/布局（含占位内容）→ 可见性判定 → 激活策略决定何时启动插件 Runtime → 加载真实内容**。实例恢复永远发生、不依赖激活事件；"未声明 onStartupFinished"只影响运行时何时被拉起，绝不能导致桌面重启后格子消失（占位内容机制 PlaceholderWidgetContent 现成）。
9. **资源预算第一天进运行时模型**（不急于全量暴露 UI）：WASM 侧 epoch interruption + ResourceLimiter（fuel 备用）；进程外侧沿用超时/kill-tree/熔断；网络用 not-before 门槛模式（WeatherRefreshBackoffPolicy 样板）；存储配额 = 写前目录检查；CPU 最难、最后做。

## 6. Runtime 矩阵（v1.4 重写：取代 v1.0-v1.3 的 A/B/C 方案对比，与 §14 收敛框架一致）

| 维度 | Declarative 资源包 | External Process | WASM |
|---|---|---|---|
| 运行任意代码 | 否 | 是 | 是 |
| 崩溃/内存/宿主损坏隔离 | 不适用 | 强 | 强 |
| **权限沙箱** | 最强（无代码可越权） | **默认无**（Full-Trust 进程可绕过 Broker 直呼 Win32/文件/注册表；除非附加 AppContainer/restricted token） | 强（线性内存边界 + 能力经 WIT 显式导入） |
| NativeAOT 兼容 | ✓ | ✓ | ✓（宿主选型见 13.3） |
| AI 生成难度 | 最低 | 中 | 中 |
| 每插件内存 | 最低 | 较高（每进程） | 低~中（进程内实例） |
| 开发门槛 | 最低 | 最低~中 | 中~高 |
| 定位 | 商店首发品类/默认 | 通用代码扩展（含官方重度功能） | 不受信任第三方代码 |

**结论：三条 Runtime 并存，Declarative 为默认品类；代码插件默认 Runtime（Process vs WASM）待阶段 3.5 三路 spike 实测拍板，不预设。** 关键推论：External Process 的权限声明对恶意插件只是**声明性约束**（Broker 边界拦截），不是强制沙箱——所以 Full-Trust 插件在商店走 L1 人工审核 + Windows 代码签名通道（§13.6），权限 UI 措辞不得让用户误以为"申请 network.fetch = 读不了文件"。UI 采用**原生模板制**（v1.1 收窄）：v1 只提供 Metric/List/Status/Gallery/ActionList/SimpleForm 六个宿主模板 + 数据绑定；不发明小型 XAML，模板不够再逐步开放 primitive。

## 7. 分阶段路线图（v1.1 修订）

| 阶段 | 内容 | 交付判据 |
|---|---|---|
| **0（前置）** | 测试/审计护栏改造：路径常量化、JSON 基线多项目扫描、audit restore 循环参数化、WMC1510 计数重校准机制化 | 单独 PR；此后结构变更有安全回归网 |
| **1** | Abstractions 变体 A（Contracts 6 文件 + WidgetConfig + WidgetFeedback + Descriptor + chrome 枚举，保 namespace 全 public，唯一切码处 GlanceWidgetData.cs） | 零引用改动，冻结测试全绿。**v1.1：这是唯一的"多程序集"试点**——用它验证多项目能否活过 AOT 门禁，观察 1-2 个版本的成本信号（锁文件冲突、audit 重校准频率、CI 时长），再决定是否拆 Feature 程序集 |
| **1.5（2026-09-07 已落地）** | **边界纪律（零拆分方案，ratchet 形态）**：新增 `ArchitectureContractTests`（4 测试）——①六功能文件清单冻结（Weather 16/Todo 36/Music 13/Glance 19/Search 20/QuickCapture 22，增删改名须有意更新快照）；②功能源 using 白名单冻结（7 个宿主命名空间，禁 `DeskBox.Views` 等新依赖）；③ambient `App.Current.WidgetManager` 棘轮（Glance 设置节 9 + QuickCapture VM 1，只许减）；④Abstractions 纯度（契约程序集禁依赖宿主命名空间，防环）。**执行决策：`Features/{Xxx}/` 物理目录搬迁推迟**——147 个路径式测试重校准的成本 vs 零运行时收益，棘轮测试已提供边界看守；真正搬迁时走 TestPaths 重定位映射按功能逐个做 | 六功能边界有测试看守，无程序集工程税 |
| **2** | 设置重组 + 数据卫生（v1.5 修订：**试点改 Music**——3 个 AppSettings 字段成本是 Weather 的零头；Weather 17 字段/803 行 VM partial 留给模式验证后做，若做必须拆 3 个 PR 跨 3 个发布=UI 搬迁/SchemaVersion 10 数据分区/字段清理）：①数据卫生先行——**Todo 孤儿修复挂两处**（RemoveWidgetAsync + ResetFeatureWidgetAsync 重复实例分支，只挂一处留漏）+ 可选存量孤儿目录清扫（PruneOrphanedManualStackMetadata 模式）；备份排除 cache/ 与 weather-cache.json（恢复后首屏天气回退定位流程，PR 里明说）；Metadata key 收敛=**宿主侧 const 别名聚合类**（勿进 Abstractions，文件名避开功能 token）。②Music per-kind store。③四个内联模板（Weather/Todo/Music/QuickCapture 的 SettingsWindow 内联 DataTemplate）抽 UserControl——新 section 禁止新增 ambient（棘轮只认减） | Music 设置走 per-kind store；三处数据卫生修复合入 |
| **2.5（v1.5 新增）** | **manifest/能力 JSON Schema v0 草案**：权限声明子集 + 六模板 payload + version/fallback 条款骨架——3.5 三腿、阶段 6 CLI validate、阶段 7 规范三方共同依赖，先于 spike 定稿（语义草案与 schema 同 PR） | schema v0 入库，spike 的"AI 生成成功率"有靶子 |
| **3** | Capability Broker v1（进程内）（v1.5 修订）：grids./files./storage./associations.(轻模型)/events. 接口 + 权限/scope 判定 + 审计；**接口命名空间=DeskBox.Contracts、物理落宿主 src/DeskBox/Contracts/（已搬空正好复用），不进 Abstractions**（保试点纯度）；FeatureWidgets.cs 改造=**三个切点剥离（~340 行/24%）**：Todo reminder 穿透→能力接口、QuickCapture 落盘→broker+事件、App 回调→事件；**前置：先把 5 处 FeatureWidgets 钉住测试接线 SourceFile（已完成于批次 A），搬迁时每文件加一条重定位映射**；per-plugin 数据根目录约定（plugins/{publisher.plugin}/...）归本阶段 storage.* 交付 | 一个官方功能完全经 broker 消费能力；Todo CRUD 能力面就位（阶段 5 依赖） |
| **3.5（v1.5：与阶段 3 并行；前置=阶段 2.5 schema v0，§11 旧排期表述以本行为准）** | **同一个 GitHub-Stats 插件实现三份实测对比**：① 声明式（六模板+manifest v0，预期半天）；② TS 外部进程（JSON-RPC over stdio，宿主进程治理复用 ThumbnailProxy 模式）；③ Rust/TS→WASM（**用独立 crate `native/deskbox-wasm-spike`，勿给 deskbox-native 加 wasmtime feature**——那会把 spike 依赖拖进 app 构建/audit/零售脚本；Rust 嵌 Wasmtime 组件模型 + wit-bindgen `deskbox:plugin` world + fuel/epoch/ResourceLimiter，**WASM 宿主第一候选=Rust**——.NET 侧 embedding 无组件模型是已核实事实，wasmtime-dotnet #324 挂 26 个月未动）。统一测：冷启动/内存/IPC 延迟/开发代码量/打包大小/调试体验/**AI 一次生成成功率**/升级兼容/权限强制/Crash 恢复。Extism（1 天 AOT 冒烟）与 wasmtime-dotnet（0.5 天，定位宿主内置信任脚本引擎）降为可选补充腿 | 实测数据表拍板"代码插件默认 Runtime"；协议/权限/manifest 的投入无论结果如何全部复用 |
| **4** | 插件运行时抽象 + 权限/生命周期/激活事件/资源预算的运行时落地（epoch+ResourceLimiter+熔断泛化） | 官方示例插件跑在完整生命周期+预算内 |
| **5** | **MCP server 作为 Broker 的第一个外部 Adapter**（v1.1 调序；v1.4 修正：MCP 工具清单**不硬编码具体功能**——核心域工具由宿主贡献（grids/files/search），Todo 等功能工具由各 Feature 经 **AI Tool Contribution**（`todo.list/create/complete`）注入 ContributionRegistry，MCP Adapter 只做聚合。这样将来 GitHub 插件能贡献 `github.getIssues` 而无需改宿主 MCP server，MCP 与插件体系形成闭环） | 外部 AI 客户端可列格子/读文件/搜索；Todo 工具由 Todo Feature 贡献 |
| **6（v1.1 升级）** | **插件 CLI/Validator**：`DeskBox.Cli.exe` 伴随程序（完全照抄 Updater 的无头 AOT exe 模式：Main+args+退出码，csproj 构建/复制 target 已模板化）；先做 init/validate/pack（纯文件操作，校验 manifest/权限/UI 模板 schema）；dev/test 待运行时就绪；install 复用 pending-command 文件+激活事件转发通道（现成 jump-list/通知封包同构模式，注意单向无回执限制） | AI 循环可跑：生成→validate→失败→修复→打包 |
| **7** | 商店（v1.2 修订；v1.4 措辞修正）：**首发品类=声明式资源包**（主题/布局/格子模板/快捷指令集——**无二进制 ABI，仅依赖版本化 JSON Schema**（Schema 1.x 长期向后兼容，2.x 经 version/fallback/migration 支持），Rainmeter 皮肤层长期为主体 + Seelen UI 同型），WASM/进程外插件为第二品类。manifest 完整版（含 v1.2 新增 `category: resource-pack\|wasm\|out-of-proc`；**Package ID ≠ Widget Type ID ≠ Instance ID**；实例 id 持久、会话 context 易变）+ **强制签名**（v1 仅自家密钥；HACS/Obsidian 缺的就是这个）+ 分级审核（L1 人工+版本 diff 复审 / L2 静态扫描与声明 capability 对账 / L3 未审核强警示）+ 更新时权限 diff 须重新确认 + Flow 式中心 manifest 仓库（PR+CI 聚合+VirusTotal 信号，单人可维护零后端）+ 下载管线（AppUpdateService 骨架）+ 包目录 `%LOCALAPPDATA%\DeskBox-Packages` + LKG 回滚 | 第一个资源包与第一个代码插件均可安装 |
| **8** | AI 层：`ai.*` BYOK（DPAPI）→ 账号托管后端（等付费用户） | 各自独立可发版 |
| SDK | 3-4 的副产品（capability 文档 + 绑定 + 模板） | 不单独立项 |

**（v1.1 保留）三版本数据节奏**：N 拆分版零迁移 → N+1 数据分区版（SchemaVersion 10 拷贝式迁移）→ N+2 清理版。插件数据 schema 迁移（plugin 声明 dataSchemaVersion/settingsSchemaVersion，宿主负责备份→迁移→验证→提交/回滚）作为 manifest 规范在阶段 7 定稿，机制镜像宿主迁移链。

## 8. 边界纪律与功能拆分（v1.1 重写）

**默认姿势（v1.1 修订，代码证据支持）：零功能程序集拆分。**

理由：①安装层无任何按功能分发的消费者（.iss/appxmanifest/默认开关全无基础）；②边界防腐已有更便宜的等价物——125 个源码扫描测试 + IVT 体系下，新增架构契约测试零新机制即获得物理拆分约 80% 的价值；③每程序集永久税 ×6（双锁文件、双脚本 restore 数组、csproj 复制段、audit 227 路径重校准 ×6）；④结构性不可能项：AOT smoke 宿主 partial 搬不走，AotBindableProperties 必须随 VM 走——证据链天然跨程序集；⑤真正需要独立打包的能力（商店动态下载）走 WASM/进程外，与官方功能是否同程序集**正交**。

**若未来出现真实分发层消费者**（如官方功能单独上架商店），届时按试点评估拆分：**Glance 是唯一试点候选**（依赖闭包 23 文件 7.8k 行，SystemFontCatalogService 专属可整体带走，设置节已独立 UserControl，AppSettings 仅 1 字段；三项判据全胜 Weather——后者另有内联设置模板 803 行 ViewModel partial、16 个 AppSettings 字段、cities.json 程序集耦合资源）。

**无论如何都要做的项目内解耦**（与是否拆程序集无关）：FeatureWidgets.cs 解体、per-kind store、设置节 UserControl 化、ambient 环收敛为回调/事件、双注册表合并。

## 9. 数据兼容性与升级迁移设计

（v1.0 内容全部保留，要点）代码轴与数据轴分离；唯一需迁移的是功能设置字段（Weather 17/QuickCapture 20/Todo 22/Music 3/Search 16）；三版本节奏 N 零迁移 / N+1 SchemaVersion 10 拷贝式迁移 / N+2 清理；迁移失败不炸启动且拷贝式可重跑；降级在 N+2 后丢功能设置可从 .bak 恢复；备份包清单扩展 per-kind 文件；WASM 插件字符串 id 与内置 WidgetKind 枚举序列化互不干扰。

v1.1 补充：associations 轻模型若落独立 `associations.json`，自动进备份（数据目录递归快照零改动），但注意 ResilientJsonStore 无跨文件事务——关联与 settings.json 各自落盘，崩溃时可能悬挂引用，写入顺序必须"先关联后删除实体"并配孤儿清扫（PruneOrphanedManualStackMetadata 模式）。

## 10. 风险清单（v1.1 更新）

1. **wasmtime-dotnet 维护节奏**（年级发布、落后上游）——双轨 spike 必须且 Rust 路线权重提高。
2. 声明式模板表现力决定商店上限；v1 六模板起步，按需求生长。
3. WebView2 与内存基调冲突；进阶通道必须共享单进程。
4. AI 托管后端运维负担；MCP+BYOK 先行。
5. 商店安全面；v1 仅自家签名。
6. Step 0 是一切前置。
7. （新增）**associations 独立文件的一致性风险**（无跨文件事务），用写入顺序+孤儿清扫对冲。
8. （新增）CLI 转发通道单向无回执——`plugin install` 的结果回报需要结果文件约定，AI 循环里 validate 必须是纯本地操作（无此依赖）。
9. （新增）启动急切建窗与"装而不活"的张力，靠激活事件声明解决，实现时注意 RestoreWidgetsAsync 的路径。

## 11. 待拍板决策点（v1.3 更新）

1. **方案主线**：v1.3 已收敛为 Protocol First + Runtime Pluggable（§14），WASM/进程外均为一等 Runtime——此项只需确认接受新框架。
2. **三路对比 spike 排期（v1.3 取代"四项 spike"）**：Step 1 后立刻做，同一插件三份实测，用数据拍板代码插件默认 Runtime。
3. **MCP 排序**：默认 Broker v1 后（对外 Adapter）；硬约束提前通道（独立进程+只读四服务+零 WidgetManager）保留。
4. **零功能程序集拆分默认姿势**：代码证据支持接受。
5. **AI 生成插件一等目标**：两个评审 AI 与方案均建议确认（machine-authorable/validatable 写入设计原则）——因你已表达"Skill 生成格子"意向，此项默认按"是"执行，除非你否决。

## 12. 评审记录：v1.0 → v1.1（外部评审 16 条的裁定）

| # | 评审意见 | 裁定 | 依据 |
|---|---|---|---|
| 1 | Broker 防上帝类（只做发现/权限/路由/审计） | **采纳** | 设计原则 1 |
| 2 | 语义模型而非等价投影；MCP 定位子集 | **采纳** | WASI 0.3（2026-06-11 正式发布，原生 async/stream）已核实 |
| 3 | associations 提升一级，不落 Metadata | **设计采纳、实现分期** | 代码复盘：通用关系模型现仅 3 个真实用例，ownerPlugin/permissions 无使用者；但 Metadata 卫生（key 注册表、双命名风格）与 Todo 孤儿数据是真实问题，立即修 |
| 4 | 细粒度权限+scope 替代三档信任 | **采纳** | 三档降为 UX 映射 |
| 5 | WASM 双轨 spike | **采纳并加强** | 网络核查：wasmtime-dotnet 年级发布节奏、落后上游——评审"持续更新/net10"表述不实，反而强化双轨必要性 |
| 6 | 模板制收窄 UI | **采纳** | 六模板起步 |
| 7 | 生命周期状态机+激活事件 | **采纳** | 代码复盘：全部为增量扩展（Enabled/Disabled 已是事实实现、触发源全在产），成本远低于评审暗示 |
| 8 | 插件数据四分类 | **采纳（新插件目录），存量暂不动** | 物理结构大半已存在；密钥层超前（现零居民）；顺带修备份含缓存缺陷 |
| 9 | 插件数据 schema 迁移 | **采纳为规范条款，实现随商店** | 镜像宿主迁移链 |
| 10 | 崩溃环检测+隔离+LKG | **采纳并纠偏** | 进程内 WASM trap 不杀宿主——真正需要的是挂起检测（epoch）、崩溃计数（熔断泛化）、进程外崩溃治理（已有）与 LKG（新建但小） |
| 11 | 资源预算 | **采纳** | epoch+ResourceLimiter+fuel 机制已核实；CPU 最后做 |
| 12 | 插件 CLI/Validator 是 AI 生态核心基建 | **采纳** | 代码复盘：DeskBox.Updater 无头 AOT exe 模式可直接照抄，validate/init/pack 零服务依赖 |
| 13 | MCP 推迟到 Broker v1 后 | **有条件采纳** | 只读四服务 MCP 的返工量其实很小（换内层调用点）；但绑 WidgetManager/变更操作/进程内宿主确会返工。默认采纳新排序，保留硬约束提前通道 |
| 14 | 每功能一程序集过于激进 | **采纳并走得更远** | 代码复盘：默认零拆分+架构契约测试（阶段 1.5）；Abstractions 是唯一程序集试点；Glance 为未来唯一候选 |
| 15 | manifest 完整化+三层 ID 分离 | **采纳** | 阶段 7 规范 |
| 16 | 路线图重排 | **采纳骨架，压缩合并** | 单人节奏：13 级压为 0-8+SDK；保留三版本数据节奏 |

**评审自身的错误/遗漏**（修订时一并补入）：wasmtime-dotnet 维护状况描述过于乐观；未意识到崩溃/隔离/预算在仓库已有大量资产（成本被高估）；未发现 AOT smoke 宿主 partial 的结构性限制（反而低估了拆程序集的难度）；未提及备份/缓存边界与 Todo 孤儿数据两个现存真问题；其引用的"Skill 生成格子"目标不在 v1.0 文档中，已提请拍板确认（决策点 5）。

## 13. 开源全景调研与 v1.2 修订（四路调研结论）

**调研面**：①同赛道开源产品（Flow Launcher、PowerToys Run/CmdPal、Rainmeter、Files、espanso、Seelen UI）；②进程外插件协议范式（Stream Deck SDK、LSP、Terraform go-plugin、Nushell、MCP 2026）；③WASM 插件基建（Extism、WASI 0.3 工具链、Zed、Spin/wasmCloud、wasmtime-dotnet）；④声明式 UI（Adaptive Cards）、权限模型（Tauri ACL）、社区分发安全（HACS/GNOME/Obsidian）。

**总裁定：方向零推翻，五处实质升级，一处事实自纠（§2.7）。** 微软自家 Command Palette（WinUI 3）选择的正是"进程外扩展 + 宿主模板化渲染（List/Detail/Form/Markdown/Grid 五种页面）+ 宿主侧扩展管理器"——与本方案逐条对应，是同栈最强背书；且微软正在 [#48707](https://github.com/microsoft/PowerToys/issues/48707)（2026-06-18 open，配套 7 个 PR 指向 0.102 里程碑、均未合并）提案"每扩展一个 Node 进程 + JSON-RPC 2.0 over stdio（LSP framing）+ discover/initialize/dispose/restart + hot reload + debugger attach"——即本方案 §13.4 协议骨架的路线正在被微软验证（是活跃提案，尚非现实）。四个有生态的产品共同点：宿主渲染 UI + 窄能力 API + manifest 发现 + 声明层与代码层分级；共同风险：进程内二进制耦合（v1.3 修正措辞：Flow #2426 为单个插件的 .NET 8 加载失败 feature request，#3643/#3617 与 runtime 无关——它证明的是"进程内 managed 插件受宿主 runtime 版本约束"，不足以证明"全生态断裂"；GNOME 45 ESM 断崖成立）。

### 13.1 升级一：插件分三类，资源包是商店首发品类

- **声明式资源包**（主题/布局/格子模板/快捷指令集）：**无二进制 ABI，仅依赖版本化 JSON Schema**（模板+绑定+动作本身就是 Data ABI/Schema Contract，须按 §13.5 的版本机制管理；Rainmeter 皮肤层长期为主体的先例；Seelen UI"插件即声明文件"同型；espanso 包模型）。权限注意（espanso 教训）：资源包若能绑定"打开 URL/运行命令/移动文件"动作，manifest 必须声明 permission 并经 Broker 校验，声明式 ≠ 无害。
- **WASM 组件插件**（第二品类）与**进程外功能包**（官方重度功能）维持 v1.1 定位。
- 安装器层功能可选=死路（PowerToys MSI 单 Feature `AllowAbsent="no"`）：按需安装只能应用内做，与 §2.6 一致。

### 13.2 升级二：权限模型借 Tauri ACL 的概念词汇，不兼容其文件格式（v1.3 修正）

permission（allow/deny 命令组，标识符 `plugin-name:permission-name`）→ scope（allow/deny 对象，**deny 永远压过 allow**，类型=目录 glob/URL 过滤/配额）→ capability（把 permission 授给某插件上下文）→ **default set**（安装时默认授予并明示）→ **Runtime Authority**（宿主在能力分发边界统一拦截：上下文→capability→命令→scope 注入，不通过则命令不执行）。**文件格式不抄 Tauri 的 TOML/window-webview 域模型**（那是它的历史包袱）：DeskBox manifest 与权限声明全部用 **JSON + JSON Schema**（C# source-gen 友好、AI 生成稳定、IDE 补全、Validator 好做、后续 JSON-RPC/WIT 映射顺）。补一条 Tauri 没有的：宿主维护**防御性默认 deny 集**（禁读宿主自身配置/凭据目录）。

### 13.3 升级三：WASM 引擎选型——Rust 宿主为主候选（证据链）

- **当前评估的托管 .NET WASM 库均无 WIT/组件模型**（v1.4 收窄措辞：结论限于已评估的托管库，不排除 P/Invoke 原生宿主等其他 embedding 形态）：wasmtime-dotnet 只有 core module 类（src 树无 Component*，跟踪 issue #324 挂 26 个月未动）；Extism 是自研 kernel ABI 且官方明确"等组件模型成熟再说"，WASI 停在 p1、无 async。类型化插件 ABI + 版本协商 + 资源限制三件套**只有 Rust 宿主路径给全**。
- Extism 细节：libextism 极活跃（wasmtime 48 已进 main），.NET SDK=纯 blittable P/Invoke（`IsAotCompatible=true`），AOT 风险极低——但天花板锁死 JSON 约定 ABI，只配当备选快车道；且 Extism.runtime.all NuGet 原生包停在 2025-11，需自管 extism.dll（有 msvc 构建）。
- **Zed 的插件 ABI 版本管理照抄机制、限定承诺窗口（v1.3 修正）**：crate 内按 API 版本分层快照 WIT（现已到 since_v0.8.0 共 10 层），semver 编码成 6 字节版本标记（自定义 `zed:api-version` section）嵌入 wasm，宿主解析后按版本选择接口层。但注意 Zed 自己也不是无限兼容：**Stable 渠道接口上限锁在 v0.6.0（灰度推进）、Nightly 才到 v0.8.0，另有 SUPPRESSED_EXTENSIONS 硬淘汰个别扩展**。DeskBox 的合理姿势：采用分层快照+版本标记机制，承诺窗口 = **Current Major + Previous Major（必要时加 LTS 标记）**，不承诺"老插件永远可跑"。
- 插件语言：商店首发只承诺 **Rust + TypeScript（wasi p2 基线）**；0.3 的 JS/Python/C# guest async 绑定仍在进行（async 插件暂时只有 Rust/Go 能写）；cargo-component 已休眠，Rust 用原生 `cargo build --target wasm32-wasip2` + wit-bindgen 0.61。

### 13.4 升级四：进程外协议 v1 骨架（五家收敛公因子 + 2026 教训）

spawn 时带外传引导信息（argv/env：管道句柄/一次性注册 token）→ `initialize` 握手（一次性交换：协议版本、双方实现信息、capabilities、宿主环境/locale）→ **能力协商宽容规则**（不认识的字段必须忽略，只许用协商过的能力；LSP 教科书路线）→ 版本双闸（安装期 manifest `hostApi min-max` + 运行期握手 protocolVersion，不匹配回结构化错误附 supported 列表）→ 消息循环（JSON-RPC、**Transport v1 = stdio + Content-Length(LSP) framing**——协议层不知道也不关心底层是 Windows 管道还是 Unix pipe；需要 shared memory/named pipe 时再加 Transport Adapter；id 关联+取消+超时第一天就有）→ 两段式关闭（`shutdown`(request)→`exit`(notification)，退出码入协议；宿主阶梯=优雅 2s→kill-tree）→ 崩溃治理（自动重启带上限→Quarantined）→ 多实例单进程 context 模型（一包一进程，实例=instanceId）。**预留 encoding 协商字段**（v1 只做 JSON——AI 生成与调试友好；Nushell 证明同 schema 换 msgpack 可行）。
**通道三分法（v1.3 澄清，取代 v1.2 的模糊表述）**：① **Capability Call**（插件→宿主，request/response，受权限+scope 控制，`grids.*`/`ai.*` 全在这类——插件调用宿主能力是被鼓励的正常流量）；② **Lifecycle/Event**（宿主→插件，typed event，只能来自白名单事件：onActivate/onWidgetVisible/onFileChanged/onTimer/onSettingsChanged 等——宿主主动调插件是事件体系的必需品，不受 MCP 教训限制）；③ **任意宿主函数 invoke**（禁止）。MCP 2026-07-28 弃用反向请求的教训只适用于第③类和"插件驱动宿主 UI"（elicitation 类），不能推广成"宿主不能调插件"。

### 13.5 升级五：声明式 UI——自研六模板为体，Adaptive Cards 为师

- **AC 的 WinUI3 渲染器技术上可行**（AdaptiveCards.Rendering.WinUI3 **2.2.4-beta**（2026-01-07）的 NuGet 版本说明明言"支持 C# projection 与 AOT / trimming-safe rendering"——以出货的包为证据，不依赖 PR 历史；稳定线停在 1.0.2/2023，要用 AOT 只能吃 beta 通道；Windows 小组件板自用在用；.NET WPF 渲染器是 Newtonsoft 反射系，AOT 死路勿碰）——但元素集封闭（XAML 线无自定义元素注册）、通用布局原语在 300px 小组件上布局质量不可控。**裁定维持自研六模板**（与 CmdPal 五种页面模板互为印证），从 AC/Block Kit 抄四件：**payload 顶层 version + 按版本固化 JSON Schema + 字段级 fallback**；**HostConfig**（宿主主题/间距/语义色与卡片数据分离）；**Action.Execute + Refresh 循环**（v1.4 的 execute+refresh 对小组件定期刷新很贴合）；**Builder 可视化工具**（Slack Block Kit Builder 式：左 JSON 右预览、模板链接即 JSON）。可选：后期加"AC 1.5 子集兼容渲染"适配层（Teams/Bot 卡片是现成内容供给）。
- **未知字段处理按层分流（v1.4 消除"严格"与"宽容"的表面矛盾）**：**RPC envelope** = 宽容（LSP/JSON-RPC 风格，未知字段忽略，利于前向扩展）；**manifest** = 严格（JSON Schema 校验，未知字段 Validator 报错/警告）；**UI payload** = 按 `schemaVersion` 选解析器，已知版本内部严格，未知元素类型走 `fallback`（有则降级呈现，无则丢弃该元素但不炸整卡）。
- 兼容纪律（GNOME 45 教训）：宿主升级承诺旧版本 payload 渲染兼容，防止用户被断崖逼向山寨版。

### 13.6 商店安全最小组合（v1.2 并入阶段 7，v1.3 细化签名与扫描定位）

无审核商店风险清单：typosquat/仿冒抢发、刷量、恶意更新注入（先干净后加毒）、进程内全权限、宿主升级断崖诱发山寨、作者账号失窃。**签名拆三种，不可混称（v1.3）**：① **完整性**（SHA256/contentHash，防传输篡改）；② **发布者签名**（自管 Ed25519 包签名，证明"确实由 publisher X 发布"，换钥=显式重新授权——声明式包与 WASM 包有这两种即够）；③ **Windows 可执行信任**（Authenticode/MSIX——Full-Trust 进程外 exe 插件**必须**走这个，DeskBox 自家包签名不能替代 Windows 代码签名；对齐 CmdPal 官方姿势：Gallery 只做目录+元数据+hash+权限+深链，Full-Trust 插件引导 Store/WinGet/可信发布者渠道）。审核管线：签名/身份 → manifest capability diff → 静态扫描 → 可复现构建/hash → 运行时沙箱/全信任分级 → **VirusTotal 仅作 CI 信号之一**（有误报、新样本可能 0 检出、不能验证权限与行为一致性——不是信任根）。加透明度（下载量/首版日期/版本史/审核级/更新时权限 diff 重新确认）与架构级隔离（声明式 UI 零代码进 UI 进程 + 能力调用全经 ACL 网关）。

### 13.7 v1.2 未采纳/降权项

- CmdPal 的 MSIX/AppExtension 发现 + COM ExeServer 注册：依赖打包身份与 COM 注册，与 DeskBox 单体 exe 便携双通道有张力；发现用文件目录+manifest，进程外走自有 IPC。
- Files 的 12 项目拆分：多人团队组织手段，佐证零拆分姿势（§8）。
- Rainmeter 皮肤内嵌逻辑（公式/bang 编程）：与 AI 生成/校验目标不符，声明层保持纯数据。
- MCP 2026 的无状态化（砍握手）：HTTP 多租户网关压力下的选择；单机长驻会话不跟。
- Stream Deck 的 DRM 打包、Elgato 商店全量人工重审流程：单人团队成本不匹配，用 L1/L2/L3 分级替代。
- （v1.3）"Rainmeter 99% 生态"表述删除（无可证实来源），改为"生态长期以声明式皮肤为主体"。

## 14. v1.3 收敛：Protocol First + Runtime Pluggable（第二轮外部评审的合并）

第二轮外部评审的核心主张成立并被采纳：**"Rust 是最合适的 WASM 宿主"与"WASM 应为第三方代码插件默认主线"是两个独立决策，不可合并推导**。CmdPal 自身（进程外 .NET/COM）与微软进行中的 Node+JSON-RPC 提案（#48707，7 个 PR @ 0.102 未合并）恰恰说明"外部进程也是被反复验证的成熟路线"。因此平台主线从"Capability Broker + WASM 主线/进程外辅线"修正为：

```
                 DeskBox（NativeAOT / WinUI 宿主）
                           │
              Platform Kernel（平台内核，Runtime 无关）
              ├─ Contribution Registry（贡献注册：Package≠Type≠Instance）
              ├─ Capability Broker + Permission Engine（Tauri 式词汇）
              ├─ Association Registry（轻模型起步）
              ├─ Activation / 三条正交 Lifecycle 状态机（§5.7）
              ├─ Package Manager（签名三分/审核分级/LKG 回滚）
              └─ Native Renderer（六模板 + HostConfig + Builder）
                           │
        DeskBox Extension Model（语言无关语义契约，长期稳定层：
        Package / Contribution / Lifecycle / Capability / State /
        Action / Event / Widget Model）
                           │
        适配层（Runtime Protocol，各自映射语义契约）：
        ├─ Declarative Adapter → JSON Schema（资源包）
        ├─ RPC Adapter → JSON-RPC 2.0 + stdio + LSP framing
        │   （initialize 握手/版本双闸/通道三分法，§13.4）
        ├─ WIT Adapter → 组件模型（WASM）
        ├─ .NET Adapter（进程内官方功能）
        └─ MCP Adapter（对外 AI 子集）
                           │
        ┌──────────────────┼──────────────────┐
   Declarative         Process             WASM
   Resource Pack       Runtime             Runtime
   （商店首发品类，      （TS/C# 候选，       （Rust Wasmtime
   AI 默认，最安全）     一等 Runtime）       第一候选宿主，
                                           一等 Runtime）
```

**分层纪律（v1.4 钉死）**：stdio+JSON-RPC 只是 **Process Runtime 的传输协议**，不是平台协议——WASM 走 WIT（进程内组件模型）、资源包只走 JSON Schema 校验、官方功能走 .NET 接口，三者都不经过 JSON-RPC；`initialize/版本/权限` 等公共条款定义在 Extension Model 层，各 Adapter 负责投影。语义映射示例：Extension Model 的 Task/Stream/Subscription/Cancellation → WIT Adapter 映射到 WASI 0.3 的 async func/stream<T>/future<T>，RPC Adapter 映射到 request/notification/cancellation，.NET Adapter 映射到 Task/IAsyncEnumerable。

**代码插件默认 Runtime 不预拍板**，由阶段 3.5 的三路对比 spike 实测决定（WASM 的先验优势——强沙箱/小包/内存效率/AI 可生成——作为假设带入检验，而非结论）。平台内核七件套（贡献/能力/权限/关联/生命周期/包管理/原生渲染）与协议规范无论 Runtime 结果如何全部复用——这是本轮最重要的结构结论。

**AI 可生成性升格为设计原则（待 Simon 最终确认）**：Extension System 应当 machine-authorable & machine-validatable——严格 schema、稳定 ID、确定性 CLI、声明式优先、良好诊断、预览、validator、权限推断成为核心要求；不等于所有插件都由 AI 从零生成（复杂 Full-Trust 插件仍面向专业开发者）。

**v1.3 决策表**：

| 决策 | 结论 |
|---|---|
| 宿主 NativeAOT / Assembly.Load 3P / 宿主原生 UI | 确定 / 否决 / 确定 |
| 平台主线 | **Extension Model**（语义契约层）+ Contribution + Capability——不是 WASM、不是 JSON-RPC；Runtime Protocol 是 Adapter 层（v1.4 分层钉死） |
| Declarative 首发 / Capability+Scope / Package≠Contribution≠Instance | 确定 / 确定 / 确定 |
| Tauri ACL | 借词汇，JSON 格式，不兼容 TOML |
| 一开始拆功能程序集 | 不做（零拆分默认姿势） |
| External Runtime / WASM Runtime | 都是正式一等 Runtime，无主辅之分 |
| **External Runtime 的隔离语义**（v1.4） | 崩溃/内存/宿主损坏隔离=强；**权限沙箱=默认无**（Full-Trust 进程可绕 Broker），商店对策=L1 审核+Windows 签名通道 |
| WASM 宿主 | Rust Wasmtime 第一候选（当前评估的托管 .NET 库均无组件模型） |
| 代码插件默认 Runtime | 等三路对比 spike 实测 |
| MCP | Broker 的对外 Adapter，不是 Runtime；工具清单经 AI Tool Contribution 注入，不硬编码具体功能 |
| AI 生成插件 | 一等设计目标（machine-authorable/validatable） |
| Adaptive Cards / CmdPal | 参考设计不绑定 / 当前最重要参考架构（Node+RPC 路线为活跃提案） |
| 插件 ABI 兼容承诺 | 分层快照+版本标记，窗口=Current+Previous Major，不做永久承诺 |
| 生命周期模型（v1.4） | Package / Runtime / Widget Instance **三条正交状态机**，不是单链 |
| 实例恢复 vs 运行时激活（v1.4） | 彻底分离：实例元数据/窗口/布局恢复永远发生，激活事件只决定 Runtime 何时拉起 |

### v1.4 收口清单（第三轮外部评审 12 条全部采纳）

P0：§6 重写为 Runtime 矩阵（删除 A→B→C 旧结论，与 §14 唯一化）；Extension Model 与 Runtime Protocol 分层（§14 图 + §5 适配层）；External Runtime"强沙箱"改为"强崩溃隔离、默认无权限沙箱"（§6）；生命周期拆三条正交状态机（§5.7）。P1：实例恢复与运行时激活分离（§5.8）；删除"动态化仅有两条路"绝对表述（§2.1）；WASI 0.3 降为 WIT Adapter 语义（§4/§14）；"零 ABI/永兼容"改为"无二进制 ABI+版本化 Schema"（§7/§13.1）；MCP 工具经 AI Tool Contribution 注入不硬编码 Todo（§7 阶段 5）；未知字段处理按层分流（§13.5）。P2：附录 wasmtime 日期修正；"任何 .NET embedding"收窄为"当前评估的托管库"（§2.7/§13.3）；AC 证据改引出货的 NuGet 2.2.4-beta（§13.5）。**裁定：架构调研到此为止，v1.4 为架构 Baseline，下一步进入 Phase 0（测试护栏改造）与 Phase 1（Abstractions），代码插件默认 Runtime 由三路 spike 实测回答。**

## 15. 商店运营与商业化扩展性（v1.3 补充：上传/下载/账号/收费）

### 15.1 开发者上架：两阶段演进，包格式不变

- **阶段一（零后端，Flow 式，单人可运营）**：`DeskBox.Cli`（init→validate→pack）产出签名包；**发布者身份=Ed25519 密钥指纹，无需账号**；向中心 manifest 仓库（`DeskBox-Gallery`）提 PR；CI 验证管线（schema 校验、**权限声明 vs 静态扫描对账**、hash、VirusTotal 信号）→ 合并聚合 `index.json` → CDN（GitHub Releases/jsDelivr）→ 应用内商店读索引。维护成本=审 PR + 偶尔 L1 人工复审。
- **阶段二（接账号后）**：Web 开发者门户上传，**服务端跑同一条验证管线**（CI 代码复用搬迁），发布者密钥绑定账号；包格式/签名/manifest 不含任何"必须联网"字段——**阶段一的包在阶段二原样有效**。

### 15.2 用户下载使用

应用内商店：浏览 → 详情页展示权限清单（来自 manifest，安装前可见）→ 安装=下载 zip → SHA256+发布者签名双重校验 → 解压 `%LOCALAPPDATA%\DeskBox-Packages\{id}\{version}` → Package Manager 注册 → 激活事件门控装载；上一版目录保留（LKG 指针回滚）。更新=权限 diff 重新确认+新版本目录+原子切换指针。卸载按数据四分类（cache 必删、用户数据询问）。**Full-Trust exe 插件例外**：v1 走 Gallery 深链 Store/WinGet/可信发布者（信任根=Windows 代码签名而非自家发布者签名）；账号体系落地后可升级直接托管。

### 15.3 账号体系：四个现成插槽 + 一条红线

① 发布者身份密钥制（账号无关先行），账号只加"归属与找回"；② `ai.*` 本就双后端（BYOK/托管），账号=托管后端前置，插件零改动；③ Secret vault（插件永不明文）容纳账号 token，与 BYOK key 同库（DPAPI）；④ 数据层按可同步性分层（GlanceWidgetStore 自述 sync 意图），资源包/布局/插件设置的云同步为后补服务。**红线：本地优先——账号永远可选，不登录可用全部本地功能。**

### 15.4 收费体系：钱不经过插件运行时

- 可收费对象：资源包/代码插件（买断/订阅）、`ai.*` 用量（credits）、官方功能订阅。**包本体是免费产物，entitlement 是纯服务端元数据**。
- 执行点插在 Package Manager/Broker（安装/激活/能力调用三档任选），插件运行时对付费无感知；离线用 entitlement 缓存 + license key 兑换（无账号买断路径）。
- 支付在门户对接 Paddle/Stripe/LemonSqueezy，应用内不碰卡数据。**通道分流**：Direct 可自有 commerce；Store（MSIX）通道站外购买受商店政策约束，按 `AppDistributionService` 先例分流。
- 模式参照：uTools 付费插件 / Raycast Pro 付费扩展 / Obsidian 本体收费+插件免费。

### 15.5 扩展性判据（为什么撑得住 / 什么会致命）

撑得住的四个早期决策：包=内容寻址签名产物（商业与包解耦）；发布者=密钥身份（先于账号）；Broker 唯一执行点（entitlement 可插任意档）；数据按可同步性分层（同步后补）。**致命反例**：无签名文件夹拖放式插件（无法收钱/追责）；身份绑死单一平台账号；`ai.*` 第一天就要求登录（破坏本地优先）；entitlement 检查埋进插件运行时（各插件自实现=灾难）。

## 16. v1.5 修订记录（2026-09-07 四路复盘后）

复盘结论：Step 1 本身零高危；发现的护栏漏洞与计划偏差在本批次（"批次 A 护栏补洞"）同步修复，计划调整如下。

### 16.1 护栏补洞（已落地，全部测试/脚本/文档层）

1. **SourceFile 接线**：5 处 FeatureWidgets 钉住测试（Onboarding/GlanceInstance/AotStage5B4C3B2B2A/IdleRuntime/MarkdownAndSplitter）改走 `TestPaths.SourceFile` + 采用率护栏（≥5 文件）——重定位映射从零调用者变为实际生效。
2. **棘轮堵漏三件**：①功能文件禁声明 `DeskBox.Views*` 命名空间（grandfather=Glance/Search 设置节+SearchPopupWindow 三文件，Step 2 迁移）；②禁 `using DeskBox;`/`using static DeskBox.*`/命名空间别名/全限定 `DeskBox.Views.`/`global::DeskBox.`（namespace 声明行与注释除外）；③目录发现改递归+去重，加"无未登记子目录"绊线（Views 允许 SettingsSections、Controls 允许 WidgetContents）。
3. **编译覆盖**：`dotnet build` 解决方案带 RID 被 SDK 拒绝（NETSDK1134）→ ci.yml 维持宿主 csproj 构建，改加"每个 src 项目必须被宿主/测试引用图覆盖"契约测试 + sln 成员绊线。
4. **发布禁令**：retail/7C1/store 审计三处禁令清单补 `DeskBox.Abstractions.dll`（JIT 产物泄漏检测）；audit 脚本同款补条目推迟到下次动它（需 58→59+46 测试文件同步）。
5. **多根化收尾**：AotStage4D1B（XAML 计数）与 AotStage4D2（全树扫）改走 `EnumerateProductionXamlFiles`/`EnumerateProductionSourceFiles`，消灭第二套枚举实现。
6. **杂项**：Abstractions csproj 补 Version 1.5.0 + IncludeSourceRevisionInInformationalVersion=false（发布证据哈希确定性；锁文件零漂移已验证）。

### 16.2 计划调整

1. **阶段 2 选型**：试点 Weather→**Music**（3 字段 vs Weather 的 17 字段+803 行 VM partial+15 消费文件+2 个搬不走的 AOT smoke 重写；§8/§9 的"Weather 16 字段"笔误统一为 17）。Weather 若做必须拆 3-PR/3-发布。
2. **阶段 3 改写**："解体"→"三个切点剥离（~340 行/24%）"；Broker 接口落 `DeskBox.Contracts` 命名空间+宿主 Contracts 目录；per-plugin 数据根约定归阶段 3；Todo CRUD 能力面进阶段 3 判据（阶段 5 依赖）。
3. **新增阶段 2.5**：manifest/能力 JSON Schema v0（3.5/6/7 三方共同前置）。
4. **依赖边修正**：spike 排期统一为"与阶段 3 并行、前置 schema v0"（取代 §11 旧表述）；Rust spike 用独立 crate。
5. **数据卫生细化**：Todo 孤儿挂两处+可选存量清扫；Metadata 注册表=宿主侧 const 别名聚合（勿进 Abstractions）。
6. **过时表述**（§2.2 等）：JSON 基线扫描已多项目化；"30-40% 工作量"基于阶段 0 之前状态，搬迁税现按"SourceFile 已接线+每文件一条映射"重估。

### 16.3 批次 B 补记（2026-09-07 audit 全管线重对齐，commits 6823325/54f02be0/2fa47fce）

本地冒烟六轮排障，audit 端到端全绿（profile 59/schema 55/源码全程稳定）：①主项目 packages.aot.lock.json 补 deskbox.abstractions 条目（仅 AOT 变体 restore 生成——新 csproj 变更后必须重跑两种 restore 并提交双锁，此纪律已写入 AGENTS.md）；②4E-4 桥契约重对齐惰性装载机制（0a496114）；③WMC1510 期望 1235→863×31 处（1.5.0 XAML 批次合法移除 372 个编译绑定；**retail 管线无数值断言故漂移漏网——动 XAML 后必须重跑 audit**）；④5B4B1 四处模式重对齐+可绑定属性计数 309→327；⑤profile 58→59 全仓 58 文件同步（含 smoke runner 小写变体链）。**结构性教训：契约测试只钉变量名不钉值是漂移根因**——批次 B 修的是结果不是机制，后续以"数值必同步测试"的宅例对冲（见 16.4 台账）。

### 16.4 延迟项台账（第二轮复盘入册）

| # | 事项 | 状态 |
|---|---|---|
| 1 | audit 禁令补 DeskBox.Abstractions.dll + WMC1510 抛错指路 + ceiling 收紧 | **profile-60 合并包执行中** |
| 2 | JIT（非 AOT）Store MSIX 中 Abstractions.dll 为合法 payload 的存在性断言 | 未做，非主路径，暂缓 |
| 3 | QuickCaptureWidgetWindow 死代码（6420 行，被 25 个测试钉住） | 排阶段 2（删=重校准 25 文件，与设置节同批） |
| 4 | 陈旧 worktree wingezi-glance-memory-20260905 + 分支 | 可删（0a496114 已在 main） |
| 5 | Features/ 物理搬迁 / grandfather 三文件迁移 | 维持推迟至阶段 2/3 |

**批次 D 前置决策（已定）**：MusicSettingsSection 命名空间落 `DeskBox.Controls.WidgetContents`（文件仍在 Views/SettingsSections/，命名空间≠目录是 C# 合法形态，且该命名空间已在功能 using 白名单内零扰动）；实现纪律=保持 `{Binding}` 不改 x:Bind（保 327 计数）、保 `x:Name="MusicSettingsSection"`（Slice 断言端点）、Music 文件计数 13→15 有意更新。

## 附录 A：关键证据文件索引

| 主题 | 文件 |
|---|---|
| 契约族 | `src/DeskBox.Abstractions/Contracts/IWidgetContent.cs`（Step 1 已迁入；Provider/Context 留守 `src/DeskBox/Services/IWidgetContentProvider.cs`） |
| Provider（internal + 具体类型 Context） | `src/DeskBox/Services/IWidgetContentProvider.cs` |
| WidgetManager 主体/partial 群 | `src/DeskBox/Services/WidgetManager.cs`（+20 partial） |
| 功能格启停/穿透点 | `src/DeskBox/Services/WidgetManager.FeatureWidgets.cs:224,257,290,673-815,1322-1338` |
| 设置持久化/迁移 | `src/DeskBox/Services/SettingsService.cs`、`SettingsMigrationService.cs`（CurrentSchemaVersion=9） |
| per-kind store 样板 | `src/DeskBox/Services/GlanceWidgetStore.cs` |
| 本地化/cities 资源耦合 | `src/DeskBox/Services/LocalizationService.cs:469-483`、`CitySearchService.cs:72` |
| JSON 冻结基线 | `tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs` |
| "不得引用"契约测试先例 | `tests/DeskBox.Tests/AotStage5B4B2AContractTests.cs:140-154` |
| retail smoke 隔离 | `tests/DeskBox.Tests/AotRetailIsolationContractTests.cs`、`DeskBox.csproj:76-79` |
| AOT 审计门禁/restore 数组 | `scripts/publish-aot-audit.ps1:283-304`、`publish-aot-retail.ps1:281-292` |
| 进程治理/熔断模板 | `src/DeskBox/Helpers/ShellThumbnailProxy.cs`（30s 熔断/kill-tree）、`Services/FolderWatcherService.cs`（指数退避+四态健康） |
| 无头 AOT exe 先例 | `src/DeskBox.Updater/Program.cs`（CLI 照抄对象） |
| UI-free 服务证据 | `Services/FileService.cs`（"Headless callers" 注释）、`SearchEngineService.cs`（全 ConfigureAwait(false)）、`TodoWidgetStore.cs` |
| 下载/回滚骨架 | `src/DeskBox/Services/AppUpdateService.cs`（.tmp+SHA256）、`ResilientJsonStore.cs`（.bak/.corrupt） |
| 备份边界 | `src/DeskBox/Services/DeskBoxDataBackupService.cs:1450-1459`（ShouldIncludeInBackup） |
| 卸载 DelTree 范围 | `installer/DeskBox.Uninstall.iss` |
| Metadata key 家族 | `Services/WidgetFileStackSettings.cs:24-32`、`WeatherWidgetViewModeSettings.cs:7` 等 |
| AOT XAML 防御先例 | `docs/architecture/rust-native-aot-roadmap.md`（4E 系列） |

## 附录 B：外部参考

- Native AOT 限制：learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/、github.com/dotnet/runtime/discussions/117470
- WASI 0.3（2026-06-11 正式发布）：bytecodealliance.org/articles/WASI-0.3、wasi.dev/releases/wasi-p3、wasi.dev/roadmap
- wasmtime-dotnet：github.com/bytecodealliance/wasmtime-dotnet、nuget.org/packages/Wasmtime（44.0.0=2026-05-23；34.0.2=2025-08-05）
- Wasmtime 资源限制：docs.wasmtime.dev/examples-interrupting-wasm-execution.html（epoch/fuel 对比）、docs.rs/wasmtime（ResourceLimiter）
- Zed 扩展：zed.dev/blog/zed-decoded-extensions、zed.dev/docs/extensions/developing-extensions
- Figma 插件：figma.com/blog/how-we-built-the-figma-plugin-system/
- VS Code 扩展宿主/激活事件：code.visualstudio.com/api/advanced-topics/extension-host、code.visualstudio.com/api/references/activation-events
- Windows Widget Providers：learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers
- MCP：modelcontextprotocol.io（2026-07-28 版文档）
- Harness 设计：anthropic.com/engineering/harness-design-long-running-apps、addyosmani.com/blog/agent-harness-engineering/

v1.2 新增（开源全景）：
- Command Palette 扩展模型：learn.microsoft.com/en-us/windows/powertoys/command-palette/extensibility-overview
- Flow Launcher 插件/商店：github.com/Flow-Launcher/docs（json-rpc.md、plugin.json.md）、github.com/Flow-Launcher/Flow.Launcher.PluginsManifest
- Rainmeter 插件 API/兼容：docs.rainmeter.net/manual/plugins/、docs.rainmeter.net/developers/plugin/cpp/api/
- espanso 包模型：espanso.org/docs/packages/basics/
- Seelen UI 插件指南：seelen.io/blog/seelen-ui-plugin-guidelines
- Stream Deck SDK：docs.elgato.com/streamdeck/sdk/（plugin、manifest、distribution）、maker-console 审核流程
- LSP 3.17：microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/
- go-plugin：github.com/hashicorp/go-plugin（server.go/client.go）
- Nushell 插件协议：nushell.sh/contributor-book/plugins.html
- MCP 2026-07-28 changelog（反向请求弃用改 MRTR）：modelcontextprotocol.io/specification/2026-07-28/changelog
- Extism：github.com/extism/extism（runtime/Cargo.toml）、github.com/extism/dotnet-sdk（LibExtism.cs、csproj IsAotCompatible）、issue #666、discussion #334
- WASI 0.3 发布公告：bytecodealliance.org/articles/WASI-0.3；wit-bindgen（crates.io）、componentize-js、jco releases、wasm32-wasip3 平台文档
- Zed 扩展版本管理：crates.io/crates/zed_extension_api（分层 WIT 快照 + 版本标记）
- wasmtime Config（fuel/epoch/ResourceLimiter）：docs.rs/wasmtime/latest/wasmtime/struct.Config.html；wasmtime releases（v48 RELEASES.md）
- Adaptive Cards WinUI3 渲染器：nuget.org/packages/AdaptiveCards.Rendering.WinUI3、github.com/microsoft/AdaptiveCards（source/uwp/winui3、schemas/README.md、host-config.json、commit #9287 AOT support）
- Slack Block Kit：api.slack.com/reference/block-kit/blocks、Block Kit Builder
- Tauri ACL：tauri.app/security/permissions/、/scope/、/capabilities/、/runtime-authority/
- 社区分发安全：extensions.gnome.org/about/、gjs.guide/extensions/upgrading/gnome-shell-45.html、forum.obsidian.md/t/25707、hacs.xyz
