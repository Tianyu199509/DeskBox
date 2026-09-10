# DeskBox 综合评估报告

- **评估日期**：2026-09-10
- **评估对象**：`Tianyu199509/DeskBox`（本地分支 `codex/boundary-correctness-closeout`）
- **评估维度**：代码质量、技术合理性、功能成熟度、竞品对比
- **数据来源**：本地仓库静态分析、实际测试执行、公开竞品资料

---

## 一、执行摘要

| 维度 | 评分 | 结论 |
| --- | --- | --- |
| 代码质量 | 7.0 / 10 | 测试全绿、规范收敛，但测试结构失衡、存在上帝对象 |
| 技术合理性 | 8.5 / 10 | AOT 与原生层决策正确且执行严谨，代价是复杂度高 |
| 功能成熟度 | 7.5 / 10 | 功能覆盖广、完成度高，但项目仅 3 个月历史 |
| 工程化与流程 | 9.0 / 10 | 显著超过同类个人项目，接近小团队水准 |
| 市场竞争力 | 8.0 / 10 | 差异化定位清晰，卡在 Fences 与挂件工具之间的空白带 |
| **综合** | **8.0 / 10** | **一个高完成度的年轻项目，架构债可控但需要主动收口** |

**一句话结论**：DeskBox 在 3 个月内由单人交付了通常需要小团队才能完成的体量与工程规范，技术选型有明确主见且执行到位。当前的主要风险不在于"代码写坏了"，而在于**高速迭代积累的结构债尚未进入偿还期**——如果维持当前节奏而不做收敛，6–12 个月后边际开发成本会显著上升。

---

## 二、基本面

| 指标 | 数值 |
| --- | --- |
| 首次提交 | 2026-06-10 |
| 首个发布版本 | 1.0.0（2026-06-11） |
| 当前版本 | 1.5.0（2026-09-06） |
| 累计版本数 | 49 |
| 累计提交 | 464 |
| 生产代码 | 680 个 C# 文件 / 245,472 行 + XAML 17,719 行 |
| 测试代码 | 341 个 C# 文件 / 66,638 行 |
| 测试/生产代码行数比 | 约 27% |
| GitHub Stars | 约 3.4k |
| 许可证 | GPL-3.0-only |
| 开发模式 | 单人开发，明确不接受外部 PR |

**关键背景**：从 0 到 24.5 万行仅 3 个月，8 月 5 日至 9 月 6 日一个月内发布 12 个版本。所有架构判断都必须放在这个前提下——**这是高速冲刺的产物，不是长期腐化的结果**。这两种情况的治疗方案完全不同。

---

## 三、代码质量（7.0 / 10）

### 3.1 测试：数量充足，结构失衡

**实测结果（2026-09-10 执行）**：

```
已通过! - 失败: 0，通过: 3624，已跳过: 0，总计: 3624，持续时间: 46 s
```

全绿。这是本次评估中最正面的数据点，也修正了此前基于 9 月 5 日旧日志（4 个失败）的印象——那 4 个失败已全部修复。

但数量背后有三个结构性问题：

| 问题 | 证据 | 影响 |
| --- | --- | --- |
| **无覆盖率门禁** | `DeskBox.Tests.csproj` 引用了 `coverlet.collector`，但仓库内不存在 `coverage.settings.xml` 或任何 `.runsettings`，未配置阈值 | 3624 这个数字无法换算成"覆盖了多少逻辑"，测试分布不可见 |
| **契约测试占比过高** | `*ContractTests.cs` 124 个文件（占 344 个测试文件的 36%），另有 `Aot*` 前缀 50 个，合计 **≥50%** 为契约/构建门禁测试 | 真正的业务逻辑测试密度被高估 |
| **零 Mock 基础设施** | `Moq` / `NSubstitute` / `new Mock` / `Stub<` 命中 **0 个文件**；`TestServices.cs` 仅 18 行 | 测试为重量级集成风格（直接 `new SettingsService()` + 临时目录），无隔离层 |

契约测试本身不是坏事——在 AOT 项目里它们防的是"反射被裁掉导致运行时崩溃"这类致命问题，价值很高。但当它们占到测试总量一半以上时，**"3624 个测试全绿"传达的安全感要打折**：它证明的是"结构没被破坏"，而非"行为是正确的"。

抽样对比印证了这一点：`AotStage7AContractTests.cs` 全篇是 `Assert.Contains("aarch64-pc-windows-msvc", toolchain)` 这类子串断言；而 `SettingsServiceTests.cs` L27-60 是真实的 Load/Save 往返行为测试。两类并存，后者占比偏低。

### 3.2 复杂度：集中在少数巨型文件

| 文件 | 行数 | 说明 |
| --- | --- | --- |
| `Services/SettingsService.cs` | 3,290 | 含 11 个 `Normalize*` 方法 |
| `Controls/WidgetShell.xaml.cs` | 5,369 | 拆出约 20 个 partial |
| `Controls/WidgetContents/FileSurfaceContent.xaml.cs` | 4,967 | 拆出约 20 个 partial |
| `App.xaml.cs` | 4,684 | — |
| `Views/WidgetWindowBase.Collapse.cs` | 4,463 | 8 个 partial 之一 |
| `Views/SearchPopupWindow.xaml.cs` | 4,432 | — |

**最长方法**：`SettingsService.NormalizePresentationSettings` 约 **561 行**；`NormalizeOrganizerSettings` 约 **429 行**；`Plugins/PluginPackageVerifier.ValidateStructure` 约 **594 行**。

partial 拆分是**物理切片而非职责分解**。把 `WidgetManager` 切成 21 个文件（`Groups` / `Memory` / `ZOrder` / `Surfaces` / `Storage`…）改善了阅读体验，但没有降低耦合——仍然是同一个类在同一个对象上操作。

### 3.3 异常处理：宽捕获比例偏高

| 指标 | 数值 |
| --- | --- |
| `catch` 总数 | 866 |
| `catch (Exception)` 宽捕获 | 669（**77.2%**） |
| 空 `catch {}` | 20 |

77% 的宽捕获在桌面 Shell 集成类软件中有其合理性——第三方 Shell 扩展、文件系统状态、Explorer 行为都可能抛出不可预期的异常，崩溃的代价远高于吞掉一个异常。**这不必然是缺陷**，但缺少区分度：无法从代码判断哪些是"刻意的防御性兜底"，哪些是"不知道会抛什么所以全接住"。

### 3.4 规范一致性

**正面信号**：

- **技术债注释极少**。真实 `TODO` 注释约 9 处，`FIXME` / `HACK` **为 0**。（注意：粗放统计会得出"3113 处"的错误结论——那是把 `AotTodoNotificationSmoke` 等 25 个文件名中的 "Todo" 误计为注释。）
- **死锁风险调用极低**。`.Result` / `.Wait()` / `GetAwaiter().GetResult()` 合计约 38 处（含部分同名属性噪音），在 24.5 万行规模下属优秀水平。
- **条件编译密度可控**。`#if` 143 条，其中 115 条绑定 `DESKBOX_NATIVE_AOT` / `DESKBOX_STORE` / `DEBUG` / `WINDOWS` 等构建常量，AOT 项目属正常范围。

**待改进项**：

- `async void` **269 处**。多数为 UI 事件处理器（如 `FileSurfaceContent.Root_Drop`，该方法同时长达 281 行），在 WinUI 事件模型中难以完全避免，但这些处理器**不可等待、不可测、异常无法被上层捕获**。
- `IDisposable` 实现 49 个类 / 66 个 `Dispose` 方法，其中**仅约 41%** 在 `Dispose` 体内执行了 `-=` 事件解订阅。在长生命周期的挂件窗口场景下存在事件泄漏风险。
- 编译警告：存在若干 `CS8602`（可能的空引用解引用）与 `CS8601`，集中在 `WidgetShell.xaml.cs`、`MusicWidgetContent.xaml.cs`、`DesktopOrganizationSettingsSection.xaml.cs`；另有 `CS0414`（字段赋值未使用）和 `CS0108`（成员隐藏缺 `new`）。数量不大，但未清零。

---

## 四、技术合理性（8.5 / 10）

### 4.1 Native AOT：正确且执行到位，但代价明确

配置确认（`src/DeskBox/DeskBox.csproj`）：

```xml
<PublishAot>true</PublishAot>
<SelfContained>true</SelfContained>
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
<Platforms>x64;ARM64</Platforms>
```

**为什么合理**：目标用户是"装个桌面小工具"的普通用户，要求额外下载 .NET 10 运行时是真实的安装摩擦。AOT 换来了启动速度、内存占用和单文件分发，与产品定位高度匹配。

**执行质量高**：

- 反射序列化默认关闭，27 个 `JsonSerializerContext` 全源生成——这是 AOT 项目中最容易做到一半的地方，DeskBox 做到了位。
- **53 份 AOT/Rust 分阶段报告**（`docs/architecture/stage-reports/`），记录从 `aot-stage-4d-1a` 到 `rust-stage-7c1` 的完整推进过程。这是本次评估中最能体现工程素养的证据——单人项目愿意为每次技术决策留下过程记录，极为罕见。
- 契约测试针对 XAML 编译期绑定、命名元素、冻结控件集等 AOT 高危点设防。

**代价**：AOT 约束反向塑造了整个架构——反射关闭导致大量契约测试、partial 拆分、手工 ABI 装载。这不是缺陷，但需要清醒认识：**每增加一个 AOT 约束，就增加一层测试与文档的负担**。当前这套体系运转良好，依赖的是作者一个人的完整记忆。

### 4.2 Rust 原生层：全项目最讲究的一段

`native/` workspace 含 3 个成员：`deskbox-native`（cdylib）、`deskbox-thumbnail-proxy`（进程外隔离 bin）、`deskbox-audio-session-fixture`（测试夹具），另有独立的 `deskbox-wasm-spike` 刻意不进主构建。

**装载方式值得肯定**：`Helpers/ShortcutNativeBackend.cs` 未使用裸 `DllImport`，而是手工 `LoadLibraryEx`——路径锁定 `AppContext.BaseDirectory`，flags 使用 `DllLoadDir | System32` 防 DLL 劫持——再通过 `GetProcAddress` 取 `delegate* unmanaged[Cdecl]`，并完成 `deskbox_native_abi_version`（=2）握手与 5 位能力掩码协商。

这是一段可以直接作为范例的代码。但覆盖面有限：全项目仍有 27 个文件含 `DllImport`，Rust 只吃下 shortcut / music_volume / quick_access / recycle_bin / explorer_shell_launch 五个能力域。

**值得商榷之处**：为一个桌面整理工具引入 Rust 工具链，`publish-aot-retail.ps1` 强依赖它（"shipping builds use for the shortcut, system volume, Quick Access, Recycle Bin, and Explorer Shell native paths"）。这提升了发布门槛——任何贡献者（以及未来的你）都必须维护 Rust 工具链才能完整发布。5 个能力域是否值得这个门槛，取决于这些路径的稳定性收益，建议定期复核。

### 4.3 插件化：安全层跑在运行时前面

`Services/Plugins/` 已有 15 个文件，其中 `PluginPackageVerifier.cs` 约 60KB、`PluginPackageManager.cs` 约 27KB，GrantStore / CapabilityGate / DeclarativeExecutor 均已成型，验签用 ed25519-dalek `verify_strict`。

但按 `docs/architecture/pluginization-roadmap.md` 的 0–8 阶段划分：**0 / 1 / 1.5 已落地，2.5 的 schema 已入库，阶段 3 Capability Broker 执行中，4–8 未开工**。`NativeWidgetPilot` 默认关闭（`#if DESKBOX_NATIVE_DEV_PILOT` + 环境变量），生产可信发布者集为空。

**判断**：安全与包格式的设计明显超前于运行时抽象落地。这在安全敏感场景下是**正确的顺序**（先想清楚不信任边界，再开放扩展），但需要警惕"安全层写完、插件生态始终没起来"的风险——那会让这 87KB 代码成为沉没成本。

### 4.4 持久化：设计扎实，但被"上帝文件"抵消

写路径设计完整：`_fileWriteLock` 串行 → 11 个 `Normalize*` → 源生成 `SerializeToUtf8Bytes` → `ResilientJsonStore.SaveAsync`（.bak 备份、`File.Replace` 两次重试规避 ERROR 1175、损坏隔离、4 态恢复枚举）。迁移链 `SettingsMigrationService` 已到 SchemaVersion 10。1.5.0 进一步加入自动快照（5 分钟~5 天可调，保留 3–30 份）。

**但**：`AppSettings` 有 **208 个属性**，所有挂件配置塞进 `AppSettings.Widgets`，落单个 `settings.json`。移动一个挂件就要重写 208 个属性 + 全部 `WidgetConfig`。per-kind store 只做了 4 个（Todo / QuickCapture / Glance / Music），Widgets 本体未分区。

写入粒度越大，冲突与损坏的面就越宽——这部分抵消了 `ResilientJsonStore` 的设计收益。

---

## 五、功能成熟度（7.5 / 10）

### 5.1 覆盖度：已超越"桌面整理"范畴

| 能力域 | 状态 |
| --- | --- |
| 文件格子 / 文件夹映射 | 成熟（图标与列表布局、叠放、规则排序、拖放、QuickLook 集成） |
| 桌面整理 | 成熟（1.5.0 重做为预览卡片，支持公共桌面、断点续传） |
| 桌面搜索 | 成熟（复用 Everything 索引，不再自建重复索引） |
| 天气 | 成熟（MSN Weather + Open-Meteo 回退，双皮肤） |
| 音乐控制 | 成熟（Windows 媒体会话 + 系统音量） |
| 时光（Glance） | 成熟（公历 + 农历 + 节日） |
| 待办 / 快捷捕捉 | 成熟（Markdown、附件、重复提醒） |
| 胶囊模式 | 成熟（悬停展开、隐私模式、胶囊条） |
| 多显示器布局记忆 | 成熟（按显示器拓扑分别存储） |
| 本地化 | 12 种语言，资源键与占位符覆盖一致 |
| 更新 / 备份 / 诊断 | 成熟（自动快照、隐私过滤诊断包） |
| 插件系统 | **试验中**，未对终端用户开放 |

功能广度已明显超出同类竞品，这是核心差异化所在。

### 5.2 稳定性：修复密度反映真实成熟度

1.5.0 单版本的修复条目约 20 条，且多为**边界场景**：
- junction / 符号链接环路检测
- 跨卷拖放后的 Shell 变更通知（含 OneDrive 桌面）
- Steam 库位于不可用网络驱动器时的误判
- 快捷方式健康检查对网络共享根目录的误报
- 原子替换失败时的降级写入

这类修复的深度说明产品在**真实用户环境**中被使用，且问题被认真跟进。这是成熟度最可靠的信号——比功能列表更能说明问题。

### 5.3 成熟度的主要制约：时间

从 1.0.0 到 1.5.0 仅 3 个月。这意味着：

- **没有经历完整的 Windows 大版本更新周期**。Windows 的半年通道更新是桌面 Shell 类软件的主要破坏源，DeskBox 尚未经历一次。
- **长期数据迁移验证不足**。SchemaVersion 已到 10，意味着 3 个月内做了 10 次 schema 变更。迁移代码本身有测试，但"用户从 1.0.0 一路升级到 1.5.0"的累积路径难以充分验证。
- **发布节奏偏快**。一个月 12 个版本意味着单个版本的验证窗口很短。1.5.0 中"实验性性能设置"这类措辞也说明部分功能尚在观察期。

---

## 六、竞品对比

### 6.1 竞品现状（2026-09）

| 产品 | 类型 | 价格 | 核心能力 | 资源占用 |
| --- | --- | --- | --- | --- |
| **Stardock Fences 6** | 商业闭源 | $29.99 一次性 / $9.99 每年 | 图标容器、自动整理规则、文件夹门户、桌面页面、标签页、Peek | <30 MB，<1% CPU |
| **Coodesker 酷呆桌面** | 国产商业（有免费版） | 免费可用 + 付费激活 | 自动分类、通配符规则、文件夹映射、多标签盒子、仅图标模式、双击隐藏 | 24 MB，约 0.02% CPU |
| **Nimi Places** | 免费 | $0 | 基础图标容器 | 轻量 |
| **Themia** | 商业（有免费层） | 免费层 + $19 Pro | 实时数据挂件（天气/日历/邮件/系统/股票） | 安装包 <10 MB |
| **Rainmeter** | 免费开源 | $0 | 无限定制，需自行配置 | 视皮肤而定 |

### 6.2 定位对比

| 维度 | DeskBox | Fences 6 | Coodesker | Themia |
| --- | --- | --- | --- | --- |
| 文件/文件夹整理 | 强 | 强 | 强 | 弱 |
| 自动整理规则 | 中（预览式） | **强（最成熟）** | 强（通配符） | 无 |
| 实时信息挂件 | **强** | 无 | 无 | **强** |
| 桌面搜索 | **强**（Everything） | 无 | 无 | 无 |
| 待办 / 笔记 | **强** | 无 | 无 | 部分 |
| 原生 Windows 观感 | **强**（WinUI 3 + Mica/Acrylic） | 强 | 中 | 中（Tauri） |
| 开源可审计 | **是**（GPL-3.0） | 否 | 否 | 否 |
| 本地优先 / 无账号 | **是** | 是 | 是 | 是 |
| 多显示器布局记忆 | **强** | 中 | 弱 | 中 |
| 成熟度 / 长期验证 | 弱（3 个月） | **强（10+ 年）** | 强（6 年） | 中 |

### 6.3 竞争位置判断

DeskBox 占据的是一个**真实存在的空白带**：

- **Fences 明确不做实时数据**。第三方评测直言："If your goal is primarily to have live information visible on the desktop — weather, calendar, upcoming tasks — Fences 6 does not address that need at all."，并建议用户**同时装 Fences + Themia** 两个软件来覆盖需求。
- **Themia 明确不做文件整理**。它"is not a shell extension and does not create containers for desktop shortcuts"。
- **Coodesker 功能最接近**，但同样没有挂件层，且不开源。

**"文件整理 + 桌面挂件"二合一，且开源、本地优先、WinUI 3 原生观感**——这四点的交集目前没有其他产品同时满足。这是 DeskBox 最坚实的护城河。

**主要短板**：

1. **自动整理规则弱于 Fences**。Fences 的规则引擎经过十年打磨，是它最被认可的功能；DeskBox 1.5.0 的整理是"预览 + 手动确认"模式，自动化程度低一档。
2. **无长期稳定性背书**。Fences 有十年口碑，DeskBox 有三个月。
3. **GPL-3.0 限制了商业化路径**。这对个人用户是纯利好，但意味着无法通过 OEM / 捆绑等渠道扩散，也解释了为何不接受外部 PR（需保持清晰的版权边界）。

---

## 七、风险与建议

### 7.1 风险矩阵

| 风险 | 等级 | 说明 |
| --- | --- | --- |
| 契约测试漂移 | **高** | roadmap §16.3 自述"契约测试只钉变量名不钉值"，9 月 5 日曾出现 4 个契约测试失败（现已修复）。无机制防复发。 |
| Glance dual ownership | **高** | 宿主 `Services/Glance*.cs` 与 `GlancePackage` 内同名类并存且已 diverge；宿主不引用该包，CI 只能防编译断裂，运行时漂移无覆盖。官方计划明令禁止长期如此。 |
| 关键人物依赖 | **高** | 单人开发 + 53 份 stage report 说明大量知识在个人记忆中；且明确不接受外部 PR，无接班路径。 |
| settings.json 上帝文件 | 中 | 208 属性单文件写入，损坏半径大，与 ResilientJsonStore 的容错设计相抵消。 |
| 未经历 Windows 大版本更新 | 中 | 桌面 Shell 类软件的主要破坏源，3 个月历史尚未遭遇。 |
| 无覆盖率门禁 | 中 | 3624 个测试无法换算为覆盖度，测试盲区不可见。 |
| 68 个远程分支 | 低 | `git branch -a` 已基本失去导航价值。 |

### 7.2 优先级建议

| 优先级 | 动作 | 理由 |
| --- | --- | --- |
| **P0** | 为契约测试补**值断言**，并将 `publish-aot-audit.ps1` 纳入 CI | 漂移已有实证（9/5 那 4 个失败），修复成本最低、收益最直接 |
| **P0** | 收口 Glance dual ownership，二选一删除另一份 | 每多一天 diverge 更严重，且已有明确的自定规则被违反 |
| **P1** | settings.json 按 widget 分区落盘 | 直接缩小数据损坏半径，与已有 per-kind store 模式一致 |
| **P1** | 引入覆盖率采集（不需设门禁，先获得可见性） | 识别真实测试盲区，判断 50% 契约测试的占比是否合理 |
| **P2** | `Services/` 按领域建子目录 | 纯移动操作，零风险，可渐进执行 |
| **P2** | 审计 66 个 `Dispose` 的事件解订阅 | 长生命周期挂件窗口的泄漏风险 |
| **P3** | 拆分 `WidgetManager` | 依赖 DI / 服务定位问题先解决，否则只是移动上帝类 |
| **P3** | 清理非活跃分支（打 tag 后删除） | 恢复仓库可导航性 |

**不建议当前进行**：大规模 DI 改造。372 处 `App.Current.*` 静态引用在单人项目中的收益低于改造风险，除非计划引入协作者。

---

## 八、总结

DeskBox 是一个**技术判断力明显强于其年龄**的项目。

它的优点不是"功能多"，而是**在关键决策点上几乎没有犯过错**：AOT 选对了且执行到位、Rust 装载写了防劫持、持久化做了容错迁移、挂件用接口族而非基类、文档留下了 53 份过程记录。这些选择组合起来，使一个 3 个月的项目拥有了通常 2–3 年项目才有的工程骨架。

它的债也不是"代码烂"，而是**高速冲刺未进入偿还期**：上帝对象、上帝文件、扁平服务层、测试结构失衡。这些问题在单人全栈掌控时不会立刻发作，但会随功能增长非线性放大。

**最紧迫的不是重构，是补机制**——契约测试的值断言和 CI 门禁。当前的质量依赖作者个人的完整记忆，一旦这层保障失效（需要协作、暂时离开、或 simply 半年后忘了细节），现有架构的容错空间会比看上去小。

竞争位置上，DeskBox 占据的空白带是真实的、有防御性的。建议下一步重心从"增加功能"适度转向"加固已有机能 + 提升自动化程度（尤其是规则引擎，这是与 Fences 的主要差距）"。

---

*本报告基于 2026-09-10 的仓库状态与公开竞品资料。测试数据为本地实际执行结果。竞品价格与特性可能变动，重大决策前建议复核。*
