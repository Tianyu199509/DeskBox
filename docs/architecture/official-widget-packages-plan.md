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
- 批次 C–E：尚未实施。六功能仍保留在现有应用中；正式包格式、商店界面和升级迁移未在本批次提前切换。
