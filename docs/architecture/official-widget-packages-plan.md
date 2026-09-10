# 官方功能包首发执行计划

2026-09-08，用户确认并授权执行。产品目标是 Weather、Todo、Music、Glance、Search、QuickCapture 按需安装并独立更新。宿主保留文件格子、窗口、桌面层级、胶囊、合并和叠放。

本文件为当前执行依据，替代 [历史路线](pluginization-roadmap.md) 中的“零功能拆分默认策略”“Declarative-only 首发”“必须跨三次公开发布”和“冻结宿主 schema 10”。历史技术分析和已经合入的基础设施继续保留；冲突时以本文件为准。

用户仍使用 1.5.0，这批改造尚未发布。核对起点是本地 `527f63d8`（B2a 执行器，PR #267）；1.5.0 的配置 schema 为 9，开发分支为 10。PR 完成、自动测试通过、NativeAOT 试点通过、真实设备交互和正式渠道升级分别记录，不相互替代。

## 首发判据

1. 未安装某功能包，宿主仍能正常启动；该功能实现无需编进宿主。
2. 安装包后可以创建、恢复和使用对应格子。包 ID、贡献 ID、实例 ID 分别持久化。
3. 固定同一份 AOT 宿主二进制，仅更新包 v1→v2，至少改变一处业务行为和一处可见 UI。原有数据和布局保留。
4. 缺包、损坏、不兼容或启动失败时保留实例、叠放关系与数据，显示可恢复占位。不得归一化为文件格子。
5. 首发允许下载后重启 DeskBox 生效。独立更新不要求热卸载；禁用/停止实例与二进制卸载分别处理。
6. 1.5.0 用户升级后原有功能离线可用，必要时使用随过渡版提供的兼容官方包。新装按需下载与旧用户升级保留分别验收。
7. Direct、Store 分别验证。x64、ARM64、数据迁移、备份恢复和真实输入/拖放/合并/叠放检查均明确记录。

## 实施顺序

| 批次 | 工作 | 退出条件 |
| --- | --- | --- |
| A 包底座 | 修复安装快照、损坏重装、注册表故障、发布者绑定授权、预算、语义模型和网络失败处理；用成熟库替代自研 Ed25519 | 回归场景有行为测试，应用构建与测试通过 |
| B 真实功能试点 | 优先 Glance；评估 NativeAOT 原生 DLL / WinRT 边界，并补待办编辑/持久化代表性切片 | 同一宿主、独立两版包、真实 UI 与业务变化、AOT 和渠道证据 |
| C 正式契约 | 按 B 的结果确定接口、资源装载、版本握手、包/数据版本和恢复规则 | 最小契约可测、兼容范围明确，不提前承诺所有运行时 |
| D 六功能迁移 | 逐个迁移旧功能，补通用实例恢复与生命周期 | 每个功能对照 1.5.0 行为验收，包缺失不会损坏布局 |
| E 商店及发布 | 浏览、安装、更新、禁用、卸载、故障恢复及渠道升级 | 全部首发判据完成后进入正式发布 |

声明式卡片继续复用，但六模板不是旧六功能的替代实现。WASM/Process 社区插件、AI/MCP、多发布者账号及收费后置。其原型保留，不纳入官方功能包首发完成度。

## 发布列车与 PR 纪律

- 本轮官方功能包改造的下一稳定公开版本目标为 **1.6.0**。功能交付、数据迁移和渠道行为均发生变化，不把这批改造作为 1.5.1 补丁版本发布。
- **1.5.1 保留给 1.5.0 维护线**。若 1.5.0 出现必须提前交付的用户故障，从 `v1.5.0` 建立 `release/1.5.x`，只移植必要修复和对应验证，不带入未完成的功能包改造。
- 开发期继续保留 1.5.0 版本号；进入候选冻结后再统一更新应用、MSIX、安装器、文档和更新清单版本。候选可使用 GitHub prerelease/CI 产物验证，未满足首发判据前不创建稳定 tag。
- 一个可独立验收的纵向批次使用一个短期分支和一个 Draft PR。审计、复评、修正和补测试继续进入同一 PR，退出条件满足后一次合并；只有前一 PR 已合并或后续修复可独立发布时才另开 PR。
- PR 合并后删除 head branch。需要长期保留的未合并快照改用 annotated tag；普通 `backup/*`、`archive/*` 不长期充当活跃分支。
- 稳定候选必须来自干净且固定的源码提交，并重新完成 x64 全量测试、当前提交的 NativeAOT 审计、ARM64 构建审计、Direct/Store 分渠道检查、1.5.0 覆盖升级与回滚、离线启动以及真实输入/拖放/合并/叠放验收。旧提交或 dirty 工作树的通过记录只作开发证据。

## 原生试点边界

.NET 不支持 NativeAOT 运行时加载普通托管程序集，但支持原生共享 DLL。C#/WinRT 支持组件 AOT 发布；这只证明候选路线存在，不证明 DeskBox 的复杂 WinUI 内容已经可动态分发。[.NET 官方原生库说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries)、[C#/WinRT AOT 支持](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)

试点必须检查 XAML/XBF、PRI/本地化资源、WinRT 激活、绑定、UI 线程、销毁重建、内容在叠放/合并中的迁移和 Windows App SDK 兼容性。普通 ProjectReference 成功不能作为动态分发证据。[WinUI 组件支持矩阵](https://github.com/microsoft/CsWinRT/blob/master/docs/authoring.md#windows-app-sdk-applications)

IWidgetContent 可继续作为宿主内部接口。两个独立 AOT 二进制之间不得直接交换普通托管 WidgetConfig/Task 对象，需 C/WinRT ABI、明确内存所有权和回调/释放约定。官方原生包为进程内可信代码，不提供第三方权限沙箱。

NativeAOT DLL 不支持 FreeLibrary 卸载，首发按重启应用新版本设计；测量每包包体、冷启动、激活、工作集及多包并存开销。若试点失败，记录具体阻塞后调整渲染契约/组件实现，不批量迁移六功能。

## 安装、信任与恢复

- 先在安装根内建立有预算的唯一快照，再验证实际提交内容；使用唯一版本目录和原子注册表切换，不覆盖上一可用目录。
- 内容哈希路径和 ReadOnly 属性只防误写，不构成对同一 OS 用户的权限隔离。执行前重新验证实际包；未来原生激活必须把验证与打开模块的生命周期绑定。
- 注册表存在但损坏时拒绝新安装覆盖，不当作首次安装。授权同时绑定包和发布者，旧的无发布者授权格式不能自动继承。
- Store 安装额外验证宿主配置的可信发布者；仓库公开开发密钥只用于显式测试/Development。正式发布密钥、撤销和轮换记录必须在上线前配置，不把有效自签名当作官方身份。
- 包元数据约束版本、哈希、架构和宿主兼容范围。**Windows App SDK 版本对齐**：包引用的 WinAppSDK 不得超过宿主（跨大版本无 ABI 保证），宿主兼容范围必须涵盖 WinAppSDK 版本并实测"旧宿主拒载新包"。下载托管可使用 GitHub，可信更新索引与密钥治理独立建设，可参考 [TUF](https://theupdateframework.github.io/specification/latest/)。
- 包二进制、用户数据、实例设置、授权和缓存的存储/备份边界分别定义。卸载清理所有包版本，文件被占用时如实报告未完成，不删除用户业务数据。
- 二进制回退必须配套兼容数据或一致快照，不能只切换目录后承诺恢复。

## 从 1.5.0 升级

继续分 PR、分测试步骤，最终允许一次公开版本升级。宿主 schema 按实际语义演进，内部已写入 schema 10 的资料也须处理；保留已支持的更旧升级链和历史备份导入。

迁移前完整快照，复制迁移、校验后提交，中断可重试。功能包分别声明设置/数据版本。降级 1.5.0 的恢复以实际验证的快照恢复路径为准，不承诺旧版能直接读取新结构。

Store 路径在批次 B 前期就核对，不能等发布前才验证。动态代码、依赖披露及实际提交类型需对应 [当前 Store 政策](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#102-security)。MSIX optional code package 的 related set/许可要求及旧 UWP 示例不能直接推导当前 NativeAOT 方案可行。

### Store 政策核对结论（2026-09-08，政策版本 7.19 原文）

- **10.1.5**：产品在初始下载后获取"发布者自己的其他产品"，前提是那些产品**也经 Microsoft Store 分发且获取经 Store 完成**。→ **Store 版宿主的应用内功能包下载不得从 GitHub 等外部源拉取原生 DLL**。
- **10.2.2**：动态包含代码不是一刀切禁止，禁止的是"以与描述不一致的方式执行"。→ Store listing 必须明确描述功能包/插件能力；功能包内容不得超出描述范围。
- **10.2.9**：直链安装器不能是 downloader stub（对我们的 Inno 安装器无影响，它是完整独立安装器）。
- **10.2.7**：干净卸载——现有三清卸载（文件+注册表+授权）符合。

**渠道决策**：Direct/GitHub 渠道（现行安装器+release）不受上述约束，功能包按 B1 管线从 GitHub 索引下载；**Store 渠道的功能包交付=捆绑进主包或 Store add-ons，不做 Store 版外部下载**。因此批次 C 的原生包格式必须同时支撑两种交付形态（同一包内容，两种分发通道），批次 E 按渠道分别验收。

### 批次 C 输入·第十一轮审计吸收（2026-09-09，逐条对码核实）

**已修的产品/代码缺陷**：①试点内容未实现 IDisposable——宿主只 Dispose `IDisposable`（WidgetManager:1598/2099），导致 `widget_destroy`/`shutdown` 从未被调用；已实现 Dispose→DestroyWidget（不触发 shutdown，防误伤其他实例）。②试点每 Widget 重新 Activate 覆盖包静态会话——已加宿主侧会话缓存（每包根一次激活）。③FullGlance 每次 Rebuild 重复订阅 CalendarViewDayItemChanging（叠处理器+旧状态捕获）——已改单订阅+可变状态持有者。④脚本 XBF 拷贝路径硬编码 x64——已改平台感知。

**C1（下一批，运行时契约收口，全部真运行测试而非源码扫描）**：
- ABI 重塑：`activate(packageRoot, packageDataRoot, hostApi*)` 去掉 instanceId（包生命周期）；`create_widget(contributionId, instanceId, instanceDataRoot, out widgetHandle, out view)`（补贡献身份+实例数据根+包侧不透明句柄）；`destroy_widget(widgetHandle)`；`shutdown()`。
- `NativePackageSession/RuntimeManager`：PackageId+ContentHash→会话（module/roots/liveInstances[]）；最后实例销毁才 shutdown；模块仍不 FreeLibrary。
- **加载器只接受 `VerifiedInstalledPackage`**（B1 管线绑定：验证→不可变根→LoadLibrary，禁止任意目录直载）。
- **PackageDataRoot 绝不由安装目录名推导**（叶子名=内容哈希，更新即漂移）：`data/packages/<publisherFingerprint>/<packageId>/`，实例 `instances/<instanceId>/`。
- 必测：同包双实例数据隔离、销毁 A 后 B 继续工作、关窗恰好销毁一次、v1→v2 更新后数据根不变、包更新不丢数据。

**C2（宿主 UI/主题/输入）**：真实 `WidgetTitleIcon` 替代 HostBadge；Segmented 订阅自身事件验证完整交互契约；主题走 HostApi GetTheme/ThemeChanged（非数据根 JSON；合并而非替换资源字典）；本地化 zh-CN/en-US 切换实测；Drag/Drop/KeyDown/IME/焦点；**Host UI Kit 不直接暴露 CommunityToolkit**（版本耦合风险，宿主包一层 DeskBox 控件）。

**批次 D 迁移顺序（审计建议，已采纳）**：Glance→Music→Search→Weather→Todo→QuickCapture（最重交互最后）。

**措辞收窄**：XBF 结论限定为"当前 NativeAOT 动态 DLL+运行时文本 XAML 路线中，宿主编译 Page 的 XBF 须保持 ms-appx 可解析"；Direct/Store 渠道分别实测，非平台定律。FullGlance 设置仍写包目录=已知 spike 债务（README 已标注反模式，C1 随三根落地一并清理）。



### 批次 C 输入·第十轮审计吸收（2026-09-08，逐条对码核实）

- **P0 存储三根拆分**：PackageRoot（只读，二进制/XAML/资源）／PackageDataRoot（可写，包级持久数据）／InstanceDataRoot（可写，实例级数据）；激活 ABI 从单一 `directory` 改为 `packageRoot + dataRoot + widgetInstanceId`。Spike 把数据写进包目录是被否决的反模式（与只读内容寻址安装/更新换目录/卸载清理直接冲突）。
- **宿主类型/主题/本地化契约**：先做 QuickCapture/Todo 综合 probe（它们比 Glance 难：CommunityToolkit 控件、拖放、KeyDown、宿主自定义控件 `WidgetTitleIcon`、局部转换器——均已对码核实）；宿主元数据提供器覆盖宿主类型（84 处命中）→"包文本 XAML 能否解析宿主自定义控件/工具包控件"是最高价值未验证项。主题走 HostThemeContract token（host→package 激活时注入，package 映射为本地 ResourceDictionary——本地字典解析已被 spike 证明可行）；本地化走包内 strings/*.json+展示层预本地化，不用 x:Uid/PRI。
- **事件接线约定**：运行时 XAML 无 code-behind，XAML 只描述结构、包代码 FindName 显式订阅；迁移成本必须在 probe 中量化（复杂视图的接线数量）。
- **正式 ABI 收敛**：统一小面（get_abi_version/activate/widget_create/widget_destroy/package_shutdown）+ HostApi 函数表回调；钉死引用计数/销毁时机/缓存/线程要求。
- **内存硬数据**：Host only / +1 / +3 / +6 包加载，及"全部视图销毁但 DLL 常驻"的长期占用（Private WS/Commit/mapped image），批次 D 前必须有数。
- **批次 D 退出条件补充**：迁移完成后源码归包所有，宿主不再编译同一 .cs（禁止长期 dual ownership）。

## 执行记录



- 2026-09-08：用户授权按复评建议开始实施，工作区干净；创建本地分支 `codex/official-widget-packages`，从 `527f63d8` 开始。
- 已建立本执行计划，历史路线保留并指向此处。
- 批次 A：包底座代码已实现。安装先做有预算的唯一快照，验证后以唯一版本目录和原子注册表提交；损坏重装、锁定文件卸载失败、损坏注册表阻断、发布者绑定授权、宿主兼容范围、结构化 payload 和网络取消/超时均有回归覆盖。自研 Ed25519 已换为 `ed25519-dalek 2.2.0 verify_strict`，Debug/AOT 均包含原生验签模块及许可证。原生 ABI 仍为 2，能力掩码 1023、11 个导出；审计配置更新为 62。
- 自动验证：插件专项 115/115，全量 x64 测试 3541/3541；原生验签 FFI 测试 2/2；四份现有 Node 插件样本全部通过更新后的校验器。主应用完整 AOT 审计输出位于 `.artifacts/aot-audit/win-x64/summary.json`，与真实设备交互和渠道验收分别记录。
- 批次 B：第一项 x64 ABI/UI 探针通过。相同 AOT 宿主哈希，替换独立 DLL 后，实际 CalendarView 高度和业务结果均由 244 变为 268，标题绑定同步更新；详见 [可复现实验及边界](../../spikes/glance-native/README.md)。完整 Glance、待办切片、资源/生命周期/渠道仍待完成，B 尚未验收。
- 批次 B 第二轮（2026-09-08 晚）：真实 Glance 切片（生产 XAML 移植 + 生产服务源码链接：本地月源/传统历法/节日/布局/装饰记录；固定 2026-09 实测 42/42 农历文本、中秋节日、真实传统历法标题）；同一进程销毁重建；双 DLL 单进程并存渲染；待办编辑/持久化切片（automation peer 真实点击路径 + 包目录 JSON 持久化 + 重建恢复）。六场景断言全绿，证据与批次 C 契约发现（包 XAML 无转换器、ThemeResource 需资源自含、元数据聚合待设计）见 [试点 README](../../spikes/glance-native/README.md)。尚未验收项：编译 XBF/PRI/本地化/自定义 WinRT 激活、物理输入/IME、完整 Glance 与其余五功能、ARM64 运行、系统化测量、叠放合并容器、渠道。
- 批次 B 第三轮（2026-09-08 深夜）：编译 XAML/PRI/WinRT 激活三问全部实测为**当前不可行**，已钉进回归断言（XBF 在动态 AOT DLL 中无法定位——无类型最简控件同样失败，问题在定位层；AOT DLL 不导出 DllGetActivationFactory——C 导出是唯一 ABI；类库发布不产独立 PRI 且 MrtCore 无文件级加载——包本地化需自带字符串机制）。**运行时文本 XAML + 预计算可绑定属性确认为正式渲染路径**；`Application.ResourceManagerRequested` 记为未来逃逸口（未实验）。七场景全绿。
- 批次 B 第四轮（2026-09-08 深夜）：完整 Glance 代表切片全绿（八场景）——背景轮播+操作栏真实点击+右键菜单+设置面板（程序化 Toggled 驱动月份数据实时重建）+设置持久化+销毁重建恢复；Store 政策结论（10.1.5/10.2.2 原文）与 WinAppSDK 版本对齐要求写入本计划。在线图片源归批次 D。
- 批次 C 综合 probe（2026-09-08 末，九场景全绿）：**宿主自定义控件与 toolkit 控件均可在包运行时文本 XAML 中解析并实例化**（HostBadge+Segmented 实测）；前提=**宿主 Page XBF 必须随安装目录落盘**（publish 不带，ms-appx 文件级解析需要，进契约）；三根 ABI（activate(packageRoot,dataRoot,instanceId)/create/destroy/shutdown）+数据零写包目录断言+主题 token 注入（解析期默认+代码替换双层）落地；注意 toolkit Segmented SelectionChanged 需订阅控件自身事件。宿主 UI Kit 复用路线打通，批次 C 最大不确定项消除。
- 真实宿主接线（2026-09-08 末）：**开发试点已接入产品 DeskBox**——`Services/Plugins/NativeWidgetPackageLoader.cs`（统一五导出 ABI：get_abi_version/activate 三根/create/destroy/shutdown；模块进程常驻）+ `NativeWidgetPilot.cs`（IWidgetContent 包装），接缝在 WidgetContentFactory（基础设施层，棘轮要求：试点文件不带功能 token、不进功能命名空间），`DESKBOX_DEV_NATIVE_GLANCE` 环境变量门控默认关，任何失败回落内置 Glance。spike 包新增同名统一导出。**真机实测**：Debug 宿主日志 `[NativePackage] pilot package active: glance-dev (instance <id>)`，ABI 版本握手+三根 activate+widget 创建全链路通，3545/3545 全绿（+4 试点测试：路径校验/数据根/工厂接缝回退/JSON 基线与功能 token 防线）。B1 安装管线与加载器的验证绑定（"验证与打开模块生命周期绑定"）仍留批次 C。
- **C1 运行时契约落地（2026-09-09，九 spike 场景全绿+真机冒烟）**：ABI v2——`activate(packageRoot, packageDataRoot, HostApi*)`（包生命周期，无实例参）+ `create_widget(contributionId, instanceId, instanceDataRoot, out widgetHandle, out view)`（贡献身份+实例数据根+包侧不透明句柄）+ `destroy_widget(widgetHandle)` + `shutdown()`；HostApi v1 函数表（log 回调实测 6 次往返）。产品侧 `NativeWidgetRuntimeManager`（每身份一会话恰激活一次、实例租约、末实例销毁→shutdown）+`NativePackageIdentity`（**数据根键=publisher指纹/packageId/instances/instanceId，与安装路径解耦——单测钉住 v1/v2 换路径数据根不变**）+`TryCreateFromVerified(VerifiedPluginPackage, installDirectory, ...)` 绑定入口（EntryMain 为原生标记；runtime 类型 schema 扩展随包格式冻结）。真运行矩阵全过：双实例数据隔离（A=["条目甲"]/B=["条目乙"]）+销毁 A 后 B 继续工作+包根零写入+激活恰 1/关停恰 1/创建 2/余 0。开发试点整体迁移到会话管理器。
- **C1.1 运行时加固（2026-09-09，第十二轮审计吸收）**：①试点内容 IDisposable 回归修复（#281 意外丢失接口）+类型测试钉住（宿主只 Dispose `is IDisposable`）；②ABI 引用恰一次释放（finally 单点）+partial-failure 回滚（包侧句柄销毁+ABI 释放）；③销毁状态机=包返回 0 才移出活集+租约标记已释放（失败可重试、不误触发 shutdown）；④**InstanceStorageKey=SHA256 哈希路径**（持久化实例 ID 是输入，绝不直接当路径片段——traversal/保留名/尾点全免疫）；⑤**模块身份+=ContentHash**（数据身份=publisher/packageId 稳定不变；已加载包的同 ID 不同哈希=NeedRestart 拒载，与"更新重启生效"策略一致）；⑥**HostApi 加 Size/Version 头**（后续 GetTheme/ThemeChanged 不再抬整包 ABI）；⑦**包代码永不于管理器锁内调用**（锁只做查找/预约/提交，activate/create/destroy/shutdown 全锁外——为 C2 回调防重入死锁）；⑧**NativeInstalledPackageHandle 结构性配对**（record+安装根只能由 PackageManager.TryCreateNativeHandle 组装，runtime==native 门在注册表加载校验处天然 fail-closed 直至包格式冻结；产品路径入口改用句柄+ProductEntryModuleFileName，开发路径保留常量）。当前表述校正：B1→原生运行时=**适配器接缝已建立**（非闭环）。待包格式冻结：schema runtime:native（仅官方可信发布者）、EntryMain 进完整性清单。剩余审计项（产品级 close→destroy→shutdown 集成测试待真机自动化、内存矩阵等）随 C2/冻结批次。
- **包格式冻结·runtime:native 落地（2026-09-09）**：schema v0 加 `runtime:native`（官方可信首方进程内全信任 NativeAOT DLL；仅官方发布者可声明，第三方 native 安装期拒）；entry.main 描述覆盖 native（DLL 文件名，须完整性清单+包路径文法）；验证器接纳 native 枚举+native 必签名（未签名 native 永远无效）；注册表加载校验同步。产品入口 `TryCreateFromInstalled` 不再天然 fail-closed——native 包从此可走完整 B1 安装→验证→激活链路。发布者白名单/正式密钥仍留批次 E（签名验证已闭环，白名单是授权层）。
- **ABI v4：versioned 事件结构（2026-09-09，第十七轮审计吸收）**：ABI v3 增补记录——#293 加 `deskbox_widget_event(handle, kind, width, height, flags)` 第六导出（12 个 typed 事件全转发）、#295 加固（widget_event 必需导出/try-catch 全函数/dev 与生产 Manager 拆分/事件 13-14-15 Responsive 拆分）。第十七轮审计对码核实发现 v3 两端漂移：**宿主已发 13-15 而包端仍只收 1..12**（胶囊 Responsive 事件全被 E_INVALIDARG 拒）、`0x80070510` 注释写成 ERROR_INVALID_HANDLE（实为 ERROR_BLOCKED_BY_POLICY=1296，E_HANDLE 应为 0x80070006）、Destroy 对 unknown handle 静默成功、Shutdown 带活实例仍返回成功、dev 安装链路断（bootstrap 用默认 Store policy → isDevelopment=false → 环境变量门永不命中 → 只能走 raw DLL）。**因插件化未发过版本（用户停 1.5.0，无中间态兼容负担），直接升 ABI v4 重做事件通道**：`deskbox_widget_event(handle, DeskBoxWidgetEventV1*)`——Size/Version 头 + Kind/Flags/Width/Height + 4×Reserved 追加位（两端布局由 `NativeWidgetLifecycleAbiTests` 源码对照钉死）；包端常量补 13-15 且范围检查从常量表推导（不再有魔法数）；Destroy unknown→E_HANDLE、Shutdown 活实例>0→E_UNEXPECTED（宿主侧记录 shutdown 状态）；新增生命周期映射测试（真 marshaling 走反向 P/Invoke stub 断言全部 15 个事件+flags/尺寸载荷+InitializeAsync 零事件）+ 两端事件表数值对照测试；CI 补 `src/DeskBox.GlancePackage` 编译步骤（宿主不引用包项目，此前包编译坏了 CI 照绿）。
- **Dev 链路修复 + Release 收口（2026-09-09，第十七轮审计吸收续）**：审计核实 dev 烟测链路自 #295 起实际断裂——`RunDevBootstrap` 用默认 **Store policy** 安装 dev 包（`PluginPackageManager` 注册记录 `isDevelopment=false`），激活端为 dev 记录设计的 `DESKBOX_ALLOW_UNTRUSTED_NATIVE_DEV=1` 门永远不命中，空信任集的生产 Manager 又必拒该记录 → dev 烟测从未走过 B1 安装链、一直在静默退化为 raw DLL。修复：bootstrap 显式传 `PluginPackageVerificationPolicy.Development`（记录标记 dev → 环境变量门生效 → 走完整验证+身份绑定的已安装路径）。收口：`TryCreate` 的 raw directory fallback 与 `CreateDevelopmentDescriptor`（及其 dev 常量）全部收进 `#if DESKBOX_NATIVE_DEV_PILOT`——**Release 二进制不再存在任何绕过 manifest/签名/B1 安装的加载入口**；`TryGetDevelopmentPackageRoot` 保留无条件编译（纯路径校验、被单测消费、不加载模块——新增源码棘轮钉住它永不长出加载逻辑）。`NativeWidgetDevPathContractTests` 四条源码棘轮：fallback 在 pilot 守卫内/bootstrap 传 Development policy/描述符创建 pilot 门控/路径校验器保持零加载。
- **D3 Phase 2 收官：源码所有权全量转移（2026-09-09）**：最后一个链接文件 `GlanceTraditionalCalendarService`（367 行）复制归包（8/8 全部包所有，宿主 Compile Include 清零、HostSourceRoot 属性移除）；`Seams.cs` 删除——两个过渡桩替换为包自有能力：`CultureRules.IsTraditionalChineseCulture`（真实区域规则，原桩逻辑迁入）+ `PackageLogger`（activate 时接线 HostApi Log 回调——原 `App.LogVerbose` 桩是静默丢弃，包内诊断日志现在真到宿主日志）。包与宿主副本自此允许分叉，宿主副本保留为 oracle 直到行为对照等价后删除。
- **D3 产品迁移第一刀：真实 locale/当前月/响应式视口（2026-09-09）**：包端开始消费 HostApi v2 config 通道——`HostConfig`（activate 时存 GetConfigJson 指针，两次调用契约取 `{"locale","accent"}`，坏载荷/未知 locale 静默降级 null→回退宿主 CurrentUICulture）；渲染管线三处固定值全部去除：**PinnedYear/Month(2026-9)→当前月**、**Culture(zh-CN 固定)→宿主 locale**、日期格式 "M月d日"→文化感知 "M" 标准模式；尺寸参数化（默认 440×560 直到首个 ViewportChanged）+ **ViewportChanged(事件 9) 接防抖重建**（120ms 单发 timer，重跑月数据+面板尺寸+presentation，宿主连续 resize 只在尺寸稳定后重建一次）；右键菜单走 `PackageStrings`（strings/{locale}.json 精确匹配→en-US→内联回退，Glance 三键 menuNextBackground/menuPauseRotation/menuSettings 双语就位——包拿不到宿主 PRI，本地化必须随包走，批次 B/C 结论）。`NativeGlanceHostConfigContractTests` 四条源码棘轮钉死"不回固定值"。SetConfigChangedHandler 推送侧仍未接（宿主端只有注册日志，实际推送等主题批次一起做）。accent 已解析未消费（留主题批次）。
- **D3 产品迁移第二刀：legacy 数据迁移（2026-09-09）**：宿主在首次原生创建前把内置 Glance 的存储文件（`data/glance/widgets/<id>.json`）**逐字节拷贝**进包实例数据根（`NativeWidgetDataMigration.TryMigrate`，幂等、包数据优先、best-effort 不阻塞创建；文件名不带功能 token 是棘轮要求——Glance 冻结清单 19 文件不动）——不经 JsonSerializer（冻结 JSON 基线零扰动），真实机器上存在 v7 老文件故包端读取器全字段容缺省。包端新增 `GlanceDataFile`（JsonDocument/Utf8JsonWriter 手读手写 camelCase+字符串枚举子集），`GlanceViewBuilder` 改由迁移数据驱动：**TraditionalCalendarMode≠None**（替代固定开关）、**ShowChineseFestivals**、**RotationIntervalMinutes**（真实轮播间隔，替代固定 3 秒；钳位 0.1min~1440min）、**RandomOrder**（加载时洗牌）、**背景源**（LocalFiles=LocalImagePaths / LocalFolder=目录枚举 / Online与Bing=暂回退包内 backgrounds 并记日志——网络能力后续批次）、**ImageFit**（Fill/Fit→Stretch）、**ShowPhotoControls**（隐藏操作按钮）；设置改动写回 `glance-data.json`（包自有格式），暂停/图片进度拆到 `glance-state.json`（卸载时保存）。**尚未接的 GlanceWidgetData 字段**（显示元素开关/Layout 四模式/Transition/Readability/透明度/材质/在线图源）留给后续 parity 批次。测试 +4（逐字节一致/幂等包数据优先/缺存储零副作用/包端无 JsonSerializer 棘轮）。
- **D3 数据所有权加固（2026-09-09，第十八轮审计吸收·六项 P0 全修）**：审计核实"数据 copy ≠ 数据 ownership"，修复批以用户数据完整性为先：①**无损 round-trip**——`GlanceDataFile` 保留原始文档（JsonElement.Clone），Save 只替换包拥有的 9 个属性、其余逐字保留、缺失的 owned 字段追加（未接入字段/未来未知字段在每次写入后存活）；②**迁移语义反转=pre-cutover 宿主权威**——取消"target 存在即永久迁移"，每次原生创建都从宿主 store 重新同步（失败的原生创建不再留下 stale snapshot，宿主侧设置变更总能到达下个原生会话；正式 cutover marker 留待所有权切换批）；③**候选链继承恢复能力**——primary→primary.bak→legacy `glance/glance.json`→其 .bak，逐个验证为合法 JSON 对象再拷贝（撕写主文件不再把包钉死在默认值；v7 等老文件/整数枚举照读——`TryEnum` 接受字符串与整数双格式，对齐仓库 golden"写名读数"语义）；④**原子写**——`PackageFileStore`（temp→Replace 留 .bak；读=primary→.bak）用于 glance-data.json 与 glance-state.json；⑤**RotationIntervalMinutes≤0=禁用**（不再钳成 6 秒轮播）；⑥**真实 TraditionalCalendarMode 贯通管线**（Auto 经 ResolveMode 解析；Hebrew/JapaneseEra 等不再塌缩成农历；开关关闭再开恢复用户原历法而非覆盖）。附带：HostApi 表 Version/Size 先验证再取指针（Activate 否则 E_INVALIDARG）；Shutdown 清空 _hostLog/HostConfig/PackageLogger static（防下次 activate 见旧回调）；宿主 GetConfigJson 改用 **DeskBox 自选语言**（LocalizationService.CurrentCultureName，非 OS CurrentUICulture）且每次调用现算；图片加载对齐内置语义（LocalFiles 保用户顺序、扩展名补 jpeg/webp/bmp、枚举失败降级不砸 widget、无图时显式渐变空态）。**测试升级：tests 直引 GlancePackage（extern alias 消同名 DeskBox.Models 歧义）+golden 数据测试 5 条**（无损 round-trip/整数枚举/v7 容缺省/撕写主文件回 .bak/坏文件降级 null），迁移测试重写为宿主权威语义 +5 场景。**遗留到后续批**：时钟冻结/生命周期能耗（需包侧 Controller）、设置 UI 写包 store（cutover 步）、data/plugins 备份排除、instance 删除清理、Shutdown failure→Faulted、manifest nativeAbi。
- **D3 数据所有权收口（2026-09-09，第十九轮审计吸收·四项核心全修）**：审计指出 #303 留下"宿主权威但原生仍直写副本"的假保存语义等四项，全部修复：
  ①**HostApi v3 写回通道**——表新增 `SetInstanceConfigJson(instanceId, json)`；宿主侧 `InstanceConfigPatch`（JsonDocument 严格类型解析，坏载荷在碰权威前拒绝，接受字符串/整数枚举、空串清 Folder）经 `GlanceWidgetStore.UpdateAsync` 提交（fire-and-forget 不阻塞 UI、Changed 事件让内置实时刷新）；包端开关全部走 write-through，**无通道/失败则回退开关**（read-only 语义，杜绝"看起来保存成功下次被覆盖"）；本地 glance-data.json 降级为缓存。
  ②**version 不再归 partial writer**——原文档有 version 则逐字保留（v7 保持 7、未来宿主 v11 不被降级），仅新建文件时落 CurrentVersion；正式 cutover 时再区分 legacy schema version 与包自有 schema。
  ③**PackageFileStore 全量对齐 ResilientJsonStore**——GUID temp+Replace(ignoreMetadataErrors)+1175 重试梯（50/150ms）+就地回退（WriteThrough+读回验证）；读取=primary→坏文件隔离（.corrupt- 时间戳保留）→备份→**恢复 primary**（防止恢复链退化为好主+坏备）；宿主迁移同步写入同款弹性 Replace（同步移植——UI 线程上不能 GetAwaiter().GetResult 等 WinUI SyncContext 续体）。
  ④**迁移候选项语义验证**——结构合法≠数据合法：owned 字段类型全验（bool/number/string-array/null），`{"rotationIntervalMinutes":"abc"}` 这类内置反序列化会拒的文件现在正确落到 .bak。
  附带（审计 §12/§13）：布局计算三处硬编码 `true` 改传真实 `hasTraditional`（None 模式不再预留次行空间）；`showSecondary` 真正消费——日项次行按响应式空间门控（对齐内置 `ShowCalendarTraditionalDetails`）。
  测试：patch 4 条（权威提交/类型拒绝/整数枚举/空串清 Folder）+迁移类型腐蚀场景+golden version 断言反转。
- **D3 生命周期 Controller 批（2026-09-09，第十八/十九轮审计 §16-17 落地）**：包端实例状态从 ViewBuilder 闭包收进 **`GlanceWidgetController`**（view/settings/runtime/images/三个 timer/生命周期/写回/Dispose），`GlanceWidgetHandle` 降级为 ABI 事件薄路由（**探针视觉残留清除**——Visibility 的 opacity 0.9、Compact 的 MaxHeight 260 不复存在）：①**时钟**：可见时按 `GlanceLifecyclePolicy.DelayToNextMinute` 单发到下一分钟边界（对齐内置 60-Second 算法+50ms 守卫）刷新时间/日期/星期；tick 里比对前后一分钟日期，**跨午夜自动刷新月历**（修时钟冻结）；②**能耗**：`GlanceLifecyclePolicy` 纯逻辑核（镜像内置 UpdateTimers 条件）决定 clock/rotation 运行——hidden/longHidden 全停、compact 与用户暂停只停轮播、轮播还需间隔>0+图≥2；reveal 时立即刷新时间文本（不显示隐藏期快照）；③**RefreshRequested(1) 首次消费**：刷新时钟+重建月份；④**destroy 显式 Dispose**（停三表+保存 runtime state），Unloaded 降级为带 disposed 守卫的兜底；⑤ViewBuilder 瘦身为静态构建助手。未接事件（2/3/4/6/10/11-15）注释标注归属批次（主题推送/性能策略需 config 通道扩展）。测试 +7（policy 矩阵 6+时钟边界纯函数；真包代码经 extern alias 直跑）。
- **D3 边界 correctness 收口（2026-09-09，第二十轮审计吸收·八项全修）**：审计在 #304/#305 找到两个回归+一批收严项，全部修复：
  ①**Destroy 事务恢复**——原 `_handles.Remove` 在 `Dispose`（含磁盘写、可抛）之前，失败即造成"包侧句柄已删/宿主 lease 仍活"的永久分歧（破坏 #281/#295 建立的销毁契约）。修复=`GlanceRuntimeState.TrySave`（total no-throw）+ destroy 改 **TryGetValue→Dispose（不可抛）→Remove** 顺序。
  ②**Unloaded→Dispose 删除**——宿主组切换会 reparent+可回滚 outgoing 视图，Unloaded 永久 Dispose 会让回滚回来的视图计时器全死。改为 `Unloaded→StopVisualResources()`（停三表）/`Loaded→EnsureCurrentDate+UpdateTimers`；真正的 teardown 只允许 deskbox_widget_destroy；暂停态改在 TogglePause 时即存。
  ③**写回最小 patch**——原 BuildOwnedPatch 发全部 9 个 owned 字段=用包缓存旧值覆盖宿主并发修改（典型 lost update：宿主设置页改 RandomOrder，原生关一下节日就被旧 false 盖掉）。修复=开关只发**被改的单个字段**（BuildOwnedPatch(params onlyFields)）。
  ④**写回语义改 accepted-not-committed**——回调返回 0 只表示"已受理"，UpdateAsync fire-and-forget 在宿主 dispatcher 上执行（阻塞 UI 会死锁）；异步持久化失败由宿主日志观测（CommitToAuthorityAsync try/catch），包端只把同步拒绝视为回滚条件；丢失的异步提交经下次 create 权威同步自愈。真正的 ack 等推送通道批次。
  ⑤**迁移校验 localImagePaths 条件反转修复**——原 `!=Array || All(string)` 会把 `"localImagePaths":"not-array"` 判合法（审计抓到的明确 bug）；改严格 `==Array && All(string)`。
  ⑥**迁移校验补三个枚举字段**（traditionalCalendarMode/backgroundSource/imageFit：名字可解析或整数在定义域内），`"backgroundSource":{}` 现在正确落 .bak。
  ⑦**Patch 枚举字符串收严**——`Enum.TryParse` 会把 `"999"` 这类数字字符串解析成未定义枚举值，补 `Enum.IsDefined`。
  ⑧**隐藏跨午夜/跨月修复**——新增 `EnsureCurrentDate`（_renderedDate 缓存）：reveal/Loaded/RefreshRequested/时钟 tick 统一走它，隐藏跨天后重显自动重建月历（运行中的分钟 tick 检测不到停摆期间的跨日）。
  测试 +5（未定义枚举拒绝/迁移错型落 .bak/最小 patch 恰一字段/TrySave 失败不抛/destroy 顺序+Unloaded 语义源码契约）。#306 Pomodoro 外部 PR 关闭符合架构方向（正是要消除的"加功能改宿主十几处"路径，未来可作官方包示例）。
- **PackageBindingRegistry 抽象落地（2026-09-09，audit-20 §18/§19"最大架构 gate"）**：Glance 专属代码全部撤出通用插件层，第二个官方包从此是"一次注册"而非"插件层新分支"：①`DeskBox.Contracts.ILegacyInstanceMigration`（宿主内部端口——DataFileName/ResolveLegacyContent/TryApplyPatch；落 Contracts 是既有 stage-3a 模式，feature 层被棘轮禁止 using Services.Plugins，Contracts 在允许清单内）；②`PackageBindingRegistry`（WidgetKind→binding：PackageId/ContributionId/Migration）+ `PackageInstanceRegistry`（instanceId→packageId 活跃归属表，create 注册/destroy 注销，供写回路由）；③`NativeWidgetDataMigration` 去功能化为通用弹性同步器（适配器解析+验证字节，infra 只做 1175 链写入）；④`NativeWidgetPilot` binding 驱动（删 deskbox.glance 硬编码）；⑤HostApi `SetInstanceConfigJson` 通用路由（实例归属→binding→adapter.TryApplyPatch，桥内零 feature 类型）；⑥`GlanceInstanceMigration`+`GlanceInstanceConfigPatch`（原 NativeInstanceConfigPatch.cs 撤出 Plugins）落 Services 根 glance 领地（冻结计数 19→20 deliberate——`GlanceWidgetStore.GetSafeWidgetFileName` 同池复用）；⑦App 启动一次注册。新源码棘轮：pilot/loader 不得含 feature 类型或包 id 字面量。附带收益：写回回调现在拒绝未注册实例（归属表校验），为 session context 留了路由骨架。
- **HostApi v4：session context + config 真推送（2026-09-10，audit-20 §31/§23 落地）**：①**归属闭环**——HostApi 表尾部追加 `Context`（append-only 保前缀兼容，CurrentVersion 3→4）：宿主每会话注册 opaque id（纯注册表 id，非真实指针，伪造/未知 id 解析为 null），包端回传；`SetInstanceConfigJson` 签名追加 context 并校验"实例归属=调用会话"（一个包不能再补丁另一个包的实例）；`SetConfigChangedHandler(context, handler)` 订阅同挂 context，shutdown 解绑；②**config 真推送**——包端 activate 时注册 `OnConfigChanged` 回调；宿主 `LocalizationService.LanguageChanged → PushConfigChanged()` 逐会话触发（包异常 contained）；包端经 DispatcherQueue 重读 locale→重配 strings→逐控制器 `ApplyConfigChange`（文化/月数据/presentation/菜单文本原地切换，无需重建格子）。工厂 gate 同步去 Glance 化（`PackageBindingRegistry.TryGetByKind` 决定是否尝试原生路径——工厂不再点名 feature）。3599/3599（+4：context 注册表行为/归属校验棘轮/包端订阅棘轮）。
- **R1 Runtime/Data Contract Repair（2026-09-10，第二十一轮审计落地）**：audit-21 的六项全修——①**runtime destroy 不再删 InstanceDataRoot**（生命周期 1/2/3 分离：runtime destroy 只释放 handle+归属；persistent data 只归逻辑删除 `NativeInstanceDataLifecycle.DeleteAsync` 所有，按 publisher 扫描+幂等+best-effort）；②逻辑删除挂接 WidgetManager 两处（RemoveWidgetAsync glance 分支+FeatureWidgets duplicate 清理，均通过 `PackageBindingRegistry.TryGetByKind` 泛型解析，不硬编码 feature）；③**Shutdown 去 early-return**——`DetachSession` 在 try/catch 后 always 执行（修正常路径 context+handler 泄漏）；返回 bool；④**Faulted/RestartRequired**——shutdown 失败→`FaultedPackages[identity]=reason`→`TryCreateInstance` 拒绝再激活（进程生命周期内清除仅靠重启）；⑤**HostApi v4 = 首个冻结基线**（v1–v3 内部实验不做兼容目标；v4 起 slot+签名 immutable，新能力追加新 slot）；⑥行为测试 +8（DeleteAsync 跨 publisher 幂等/DetachSession 移除 handler+context/Faulted 标记+拒绝再激活/DestroyWidget 无 Directory.Delete 源码棘轮）。
- 批次 C–E：C1/C2 完成。六功能仍保留在现有应用中；商店界面和升级迁移未在本批次提前切换。
