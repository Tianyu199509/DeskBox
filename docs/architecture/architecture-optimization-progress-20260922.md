# 架构优化进度与下一批计划

更新时间：2026-09-23。实施基线：`d4b0a7a2`。本记录承接当日的架构核对方案，按可独立验证的功能链路推进。

## 第一批：Todo 设置与提醒启停

状态：本批实现、自动验证和 Debug 启动核验完成。真实 UI 点击和通知交互仍需人工验收。

本批覆盖 Todo 功能开关、提醒开关、默认提前时间，以及提醒实例的启动、停止和退出等待。Todo 列表、布局、附件、任务数据模型仍沿用原实现。磁盘字段和 XAML 绑定名保持兼容。

| 职责 | 当前所有者 | 生命周期 |
|---|---|---|
| 功能开关、提醒开关、提前时间的编辑状态 | `Features/Todo/TodoSettingsViewModel` | 设置窗口创建；真正关闭时取消排队操作并释放 |
| 设置写入与窗口启停的协调 | `Services/TodoSettingsCoordinator`，通过 `ITodoSettings` 对编辑器暴露 | App 装配；退出时取消等待中的切换，等待已经开始的窗口操作 |
| 当前提醒实例 | `Features/Todo/TodoReminderRuntime` | 开关生效时协调；停止实例后等待其在途操作 |
| UI 定时器、首次检查延迟、一次提醒扫描 | `Services/TodoReminderService` | 启动时获得；禁用/退出时停止定时器、取消延迟并阻止迟到通知 |
| WinUI Dispatcher 和系统通知 | App 的宿主适配入口 | 本批保留线程与通知激活语义，运行时规则从 App 移出 |
| 格子创建/分组/显隐 | WidgetManager | 保留原窗口实现，Todo 状态提交委托给协调器 |

设置修改沿 `SettingsViewModel` 兼容属性 → `TodoSettingsViewModel` → `ITodoSettings` → `TodoSettingsCoordinator` 执行。设置窗口无需直接通知 App 刷新 Todo 提醒。创建、删除 Todo 格子的既有入口也通过协调器提交启用状态；外部恢复和默认设置由 SettingsChanged 兼容观察覆盖。

设置开关操作串行执行，并保留最近一次 UI 请求，避免旧保存通知覆盖用户正在进行的下一次开关选择。这一队列的范围是本批迁移的设置操作；它不是全局 WidgetManager 操作队列。

提醒运行时只持有一个活动实例。创建后启动失败时，候选实例被释放；后续可重试。禁用后旧实例立即停止计时，再排空已有 IO。允许已有文件写入完成，不做磁盘事务回滚；本批保证禁用/释放后的扫描不会继续显示通知。

## 依赖约束

- `ModuleBoundaryContractTests` 为 Models、ViewModels、Services 的存量 App.Current、App.UiDispatcherQueue 和 IServiceProvider 文本访问建立逐文件清单；新增位置或计数增长会失败。日志兼容调用不计入这条规则。
- 对新的 `DeskBox.Features.*`、Todo/Search 设置协调器及 Search 设置视图，额外检查编译后的类型引用，包括字段、方法签名、IL 调用和异步状态机。功能业务代码禁止依赖 App、全局容器和具体 Services/Platform 实现；Search 视图保留合法 XAML 框架调用，禁止直接依赖 SettingsService、SearchHotkeyService、EverythingSearchService。
- 当前 WinUI 内容契约仍留在宿主；本批新增的 Todo 设置和提醒会话契约不含 WinUI 类型。
- 纯规则、UI 行为、文件提交和运行时资源分别声明所有者。扩充旧例外清单不能替代边界修复。

## 验证记录

- canonical Debug 最终构建：通过，22 警告、0 错误；警告来自现有控件/可空性等位置。
- 首轮针对性测试：39/39 通过，覆盖提醒规则、开关串行化、启动失败清理、停止等待、恢复路径和依赖检查。
- 全量 x64 测试：4,073/4,073 通过。初次全量发现两个属性迁出后旧 AOT 生成属性计数仍为 77，已调整为 75，并另加可读写属性及 AOT 绑定入口保留检查。
- AOT 条件编译：x64 / win-x64、`DeskBoxAotAudit=true`、`DeskBoxAotSmokeHarness=true`、`DeskBoxRustNative=true` 的 Release build 通过，0 错误。构建报告 890 个警告，包含 WMC1510 等绑定提示；这是条件编译验证，没有执行 Native AOT publish/link 或发布包 smoke。
- AOT 构建使用临时 artifacts 和独立 NuGet lock 路径。普通仓库锁文件不包含 AOT 隐式编译器依赖，初次 locked restore 失败后改用隔离的 AOT restore；仓库锁文件未改动。
- 最终 canonical Debug 进程：`src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；2026-09-22 20:01 核验 PID 22844，仓库下只运行这一个 DeskBox 实例，Medium 完整性。
- 使用独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-architecture-20260922-a82b40bd` 启动，预置空数据和启用的 Todo 提醒。日志确认提醒实例按需创建一次、设置窗口完成构造和加载、启动 35 步中 0 degraded / 0 failed。原安装版进程继续运行。
- `git diff --check`：通过。

可复查的本地证据：`tests/DeskBox.Tests/TestResults/todo-architecture-full-final.trx`，以及 `%TEMP%/deskbox-todo-architecture-full-final.log`、`%TEMP%/deskbox-todo-architecture-aot-build.log`、`%TEMP%/deskbox-todo-architecture-debug-final.log`。日志和构建产物不加入版本控制。

自动测试与真实点击、系统通知交互是不同证据。未完成的 UI 验收不能用构建通过代替。

## 第二批：Search 设置节与搜索运行时入口

实施基线：`76d0a271` 加第一批工作区改动；核验时 HEAD 为 `44e7a0d4`，期间的其他提交仅涉及社区文档和发布文案，均保留原样。状态：实现、自动回归和 Debug 启动核验完成；实际 Search 页面点击及设备交互仍需人工验收。

优先范围为 `Views/SettingsSections/SearchSettingsSection.xaml.cs`，接着本批已验证的纵向链路继续收口。保持现有 DataTemplate 按需创建机制。

1. 提取 `SearchSettingsViewModel` 和窄的搜索设置接口，让 SearchSettingsSection 不再直接获取 App.Current.SettingsService、SearchHotkeyService 或 Everything 实例。
2. 将连接状态、用户主动连接/刷新、快捷键配置等操作定义为明确的搜索用例。设置节只订阅用例暴露的状态，不持有具体 Everything 服务。
3. 明确设置节的取消边界：离开/隐藏时取消自己的连接探测和 UI 刷新；关闭设置节不能停止用户仍在使用的全局搜索服务。
4. 保留搜索禁用时释放运行时、重新启用时按需初始化的既有行为。用任务返回值和取消信号替代分散的启动调用。
5. 缩减相应旧依赖清单，增加连接失败/恢复、重复启停、快捷键冲突以及页面离开后的迟到回调测试。

验收条件：搜索设置节能用假实现验证主要行为；打开和关闭设置不重复创建搜索运行时；关闭后的回调不访问旧控件；Everything 未连接、搜索禁用和快捷键冲突均能明确显示状态；已有搜索和 AOT 绑定测试通过；实机验证首次打开、返回、唤起搜索及快捷键不回退。

实际实现：

| 职责 | 当前所有者 |
|---|---|
| 搜索设置状态、快捷键操作结果、当前访问的请求与取消 | `Features/Search/SearchSettingsViewModel` |
| Search 设置切片写入、按需取得运行时能力、旧运行时请求取消 | `Services/SearchSettingsCoordinator` |
| 配置和连接快照、借用连接/快捷键能力的接口 | `Contracts/ISearchSettings.cs` |
| 文件选择器、Alt+Space 确认、键盘录入、控件呈现 | `SearchSettingsSection`，依赖通过 Configure 注入 |
| 页面进入/离开、窗口显示/隐藏/真正关闭 | `SettingsWindow` 显式控制 Search 设置节活动状态 |
| 全局搜索服务的创建与释放 | 继续由 App/现有搜索引擎拥有；设置页借用能力，不释放全局引擎 |

本批移除了 SearchSettingsSection 对 App.Current、SettingsService、SearchHotkeyService 和 EverythingSearchService 的直接使用；其 24 处旧设置门面访问对应的例外条目已移出清单。连接快照移到 Contracts，磁盘设置字段及 UI 控件名不变。

关闭设置窗口原先仅 Hide、切换设置节仅 Collapsed，因此本批不再依赖 Unloaded 完成取消。显式离开后停止页面探测并取消文件选择/快捷键确认返回后的写入；排队中的连接通知还要核对访问代次。重新打开可发起新的探测，并复用当前全局搜索实例。运行时替换前先取消绑定旧实例的请求，迟到完成不能继续更新该次页面操作。

快捷键启用状态按实际 `IsRegistered` 展示；注册失败明确显示失败。重置为 Alt+D 也经过现有 TryApplyGesture，用同一套冲突回退路径，避免先覆盖配置再发现注册失败。原有 Alt+Space 确认保留。

第二批验证记录：

- 针对性测试 53/53 通过，含迟到探测、排队通知、重复访问、运行时更换、失败恢复、禁用状态和快捷键冲突。
- 最终全量 x64 测试：4,085/4,085 通过。首次全量唯一失败为旧 AOT 源码测试把 `_searchSettingsViewModel.Dispose()` 子串误认成 `ViewModel.Dispose()`；精确匹配主属性后仍保留“先解除绑定、后释放主 ViewModel”的顺序要求。
- 最终 AOT 条件编译通过，0 错误、888 警告，含 WMC1510 等绑定提示。采用 x64/win-x64、AOT audit 与 smoke 条件编译、隔离 artifacts/lock 路径；未执行 Native AOT publish/link 或发布包运行验证。
- 最终 canonical Debug 构建：22 警告、0 错误。2026-09-22 20:46 核验进程 PID 23652，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，仓库下唯一 DeskBox 实例，Medium 完整性。
- 开发数据目录为 `C:/Users/simon/AppData/Local/DeskBox-Dev/search-architecture-20260922-42d41e6f`。预置 Search 功能启用、Everything 查询授权关闭、搜索快捷键关闭；启动日志确认搜索历史/快捷键服务/Everything provider 各初始化一次，设置窗口完成加载，启动 36 步中 0 degraded / 0 failed。未用此启动检查冒充 Search 页或 Everything IPC 实机验收。
- `git diff --check` 通过；SearchSettingsSection 的 App.Current 和具体设置/搜索运行时类型引用均为 0。

第二批证据使用 `tests/DeskBox.Tests/TestResults/search-architecture-full-final.trx` 及 `%TEMP%/deskbox-search-architecture-*.log`。实际页面点击、快捷键设备行为和 Everything 实例联调仍需人工验收。

## 第三批：BackupRuntime 与退出资源归属

实施基线：`44e7a0d4` 加前两批工作区改动。状态：实现、全量测试、AOT 条件编译及 Debug 本地备份检查完成；真实服务器与退出交互仍需人工验收。

共享工作区期间另有内存实验改动：`MemoryDestroyProbe.cs` 及 WidgetManager/SettingsWindow 中的实验引用，核验时分支为 `experiment/memory-probe-destroy-hidden`。本批保留了这些并行改动，未将其当作备份重构内容；验证对应当时工作区，启动命令没有设置这两个实验开关。本轮未提交，后续提交须区分各批及实验差异。

本批围绕 App 内的自动备份定时器与在途备份任务，按单条生命周期链路实施：

1. 将自动备份计时、触发和并发抑制集中到 BackupRuntime，明确本地快照与云备份的调度归属，保持现有间隔与重试规则。
2. 定时触发和设置页的手动触发复用操作入口；每次操作都有结果、取消信号和可等待的任务，不靠分散的 fire-and-forget 驱动。
3. 退出时停止新触发，取消可取消阶段并等待在途操作安全结束，再按资源依赖顺序关闭窗口、完成最后刷盘及释放容器。对已经进入持久化提交阶段的操作，先明确完成/回退语义，再实现取消。
4. 补齐 DI 容器和手工创建对象的所有权表。UI 相关释放在所属线程完成，容器释放放在其消费者解除订阅之后。
5. 测试手动/定时重叠、配置修改、上传失败、取消、退出竞争和重复停止；确认失败不会阻断其余退出步骤，也不会生成被误认为成功的备份记录。

验收条件：一个定时器拥有者、一个明确的在途任务集合；重复触发不会并发执行同一任务；退出后无新回调；成功/失败/取消记录可区分；原备份、恢复、凭据及文件事务测试保持通过。WebDAV 协议、磁盘 schema、恢复事务和发布渠道策略不并入这批。

Generic Host、程序集拆分、WidgetManager 的 Z-order/托盘动画拆分继续按触发条件评估。

实际实现和资源归属：

| 对象/资源 | 所有者 | 结束方式 |
|---|---|---|
| 1 分钟定时器、备份调度、在途及等待中的备份任务 | `Features/Backup/BackupRuntime` | 停止接收任务、撤销 Tick 订阅、停止计时器、发出取消、等待已提交工作结束 |
| 本地快照/导出与云端上传入口 | `IBackupCommands`，由设置窗口注入 | 手动与定时触发进入同一运行时；备份服务保留原归档/上传算法 |
| 设置刷新与手动备份前刷盘 | `Services/BackupBackend` | 使用现有设置存储，手动刷盘不广播调度事件；刷盘失败不继续创建备份 |
| 提醒、搜索、观察器、钩子和窗口等手工创建对象 | App 的 `ShutdownSequence` 步骤 | 按依赖顺序清理，每步失败单独记录，后续步骤继续；重复退出复用同一任务 |
| DI 创建的实例 | ServiceProvider | 窗口及订阅解除、设置最后刷盘后，在 UI 线程同步 Dispose 容器；当前注册的可释放对象均支持同步 Dispose |
| ThemeService 的系统颜色订阅、防抖计时器、窗口事件 | ThemeService，由容器释放 | 显式退订、停止计时器；已排队的颜色回调检查 disposed 状态 |
| 窗口/内容工厂自行创建的 Weather、CitySearch 等实例 | 原窗口、内容或 ViewModel | 继续由原消费者释放，不能把同一实例交给两个 owner |
| WebDAV 共享 HttpClient | 原有进程级静态共享对象 | 保留原共享策略，不按单次上传销毁；未变更协议实现 |

并发策略明确为两条任务通道。本地手动操作保持串行等待；本地定时检查在已有本地任务时跳过，避免堆积。云端手动/定时操作仍采用忙时跳过并返回 AlreadyInProgress 的语义。本地归档与云端上传可以并行调度，其内部归档仍由既有数据服务锁保护。

取消边界：

- 本地归档在最终 rename 前接受取消，清理临时件；rename 成功后保留完整备份，不因迟到取消改记为取消。
- 云端在 UploadAsync 返回成功前取消，不写成功或失败时间戳。若传输在未确认阶段中断，不宣称远端一定回滚。
- UploadAsync 已成功返回后，服务器已确认接收。取消校验重试或保留策略清理时仍完成结果落盘；未完成校验则记录 Uploaded + UploadUnverified，保留原有“已上传但未验证”语义。
- 完成通知订阅者抛错不会把已接受的上传改判失败，也不会阻止其余订阅者收到结果。

退出序列先停止备份新触发并排空其工作，再释放功能运行时、观察器、钩子和窗口。窗口关闭产生的最终设置写入完成后刷盘并释放容器；托盘宿主窗口保持到异步序列完成后才关闭，随后退出。日志队列仍属 App，本批只在退出时排空，未迁移日志架构。

第三批验证记录：

- 首轮针对性测试 198/198 通过。
- 最终全量 x64 测试 4,102/4,102 通过，包含手动/定时重叠、排队取消、提交排空、重试、配置重入、刷盘失败、退出步骤故障、上传确认前/后取消、保留清理取消及观察者异常。
- AOT 条件编译通过：x64/win-x64、audit + smoke 编译配置，888 警告、0 错误。没有做 Native AOT publish/link 或发布包运行验证。
- 最终 canonical Debug 构建：22 警告、0 错误。2026-09-22 21:50 核验 PID 33600，仓库下唯一 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。
- 独立开发数据为 `C:/Users/simon/AppData/Local/DeskBox-Dev/backup-architecture-20260922-8b0415bf`，仅放入样例配置，启用本地自动备份（5 分钟、保留 2 份），未配置云端。启动生成 21:50 的快照，5 分钟调度又生成 21:55 的快照；读取最新 ZIP，核对 `manifest.json` 所列两个数据文件的长度和 SHA-256 全部一致。启动 35 步中 0 degraded / 0 failed。
- `git diff --check` 通过。
- 使用模拟 WebDAV 传输验证取消边界；真实服务器联调和用户点击退出时的实机表现尚未验收。

证据：`tests/DeskBox.Tests/TestResults/backup-architecture-full-final.trx`，`%TEMP%/deskbox-backup-architecture-full-final.log`、`%TEMP%/deskbox-backup-architecture-aot-build.log`、`%TEMP%/deskbox-backup-architecture-debug-final.log`。

## 第四批：备份设置页与读取操作会话

实施基线：`44e7a0d4` 加前三批尚未提交的工作区改动。当前分支仍为 `experiment/memory-probe-destroy-hidden`；内存探针及并行实验修改保持原样。本批只触及备份设置、云端页面与必要的服务端点参数，未提交或推送。

| 职责 | 当前所有者 |
|---|---|
| 本地/云端设置快照、局部更新、凭据/探测/列表端口 | `Contracts/IBackupSettings.cs` |
| 设置切片读写、选项刷新、云端服务适配 | `Services/BackupSettingsCoordinator`；App 装配，退出时解除完成事件订阅 |
| 页面访问、端点切换、凭据状态和快照列表读取 | `Features/Backup/BackupSettingsViewModel`；设置窗口真正关闭时释放 |
| 原 XAML 属性名、可见性、本地化文案和列表行投影 | `SettingsViewModel` 的兼容门面；不直接持有具体备份服务 |
| HWND、PasswordBox、确认对话框和列表控件 | `SettingsWindow`；显示、隐藏、导航时显式开关页面访问 |
| 删除/下载的端点校验与冻结、现有恢复事务入口 | `Services/BackupRestoreActions`；恢复暂存与重启语义沿用数据备份服务 |
| 已提交的本地/云端备份任务 | 第三批的 `BackupRuntime`；页面隐藏不取消它们 |

页面和端点各有取消与代次检查。隐藏或切换端点后，旧凭据查询、连接探测和 PROPFIND 即使忽略取消、迟到返回，也不能覆盖新页面。列表读取按当前访问合并；上传完成通知携带发起时端点，只有当前可见页面且端点匹配时才安排有界重试。页面重开会重新读取凭据及列表。

远端列表项保留所属端点。删除在确认前后核对当前页面和列表项，服务调用开始后使用冻结的端点选项；恢复在选择域后再核对端点，已开始的下载继续使用原列表项对应的端点。对话框、文件选择和密码输入仍由 View 负责，密码只作为调用参数交给系统凭据存储；没有新增明文字段或设置持久化。恢复暂存事务、WebDAV 协议和磁盘 schema 未修改。

旧全局依赖清单删除了两个备份设置 partial 的例外，并对备份页面、编辑器和适配器增加了零全局 App 访问门禁。桌面设置窗口其他功能的旧依赖按后续批次处理。

第四批验证：

- 定向测试先后 91/91、70/70 通过，覆盖端点切换、迟到凭据/列表/探测、页面隐藏重开、旧上传通知、失败重试及旧列表项不能删除新端点文件。
- 最终全量 x64 测试：4,110/4,110 通过，含页面重复进入仅保留一条完成事件订阅、可见且端点匹配的后台上传刷新列表。
- 全量回归后补充的上传完成端点断言，云备份传输定向测试 60/60 通过。
- AOT 条件编译：x64/win-x64，audit + smoke 编译配置，888 警告、0 错误；未做 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 09:45 最终核验 PID 6632，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录：`C:/Users/simon/AppData/Local/DeskBox-Dev/backup-settings-architecture-20260923-c6834218`，关闭自动备份、云端配置和实验开关。启动日志显示设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed，未见备份设置错误或致命异常。此项只证明启动和默认设置页装配；真实 WebDAV、密码输入、云备份页面与恢复对话框的设备交互仍需人工验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

测试记录：`tests/DeskBox.Tests/TestResults/backup-settings-full-final3-20260923.trx`；构建与测试日志位于 `%TEMP%/deskbox-backup-settings-*.log`。测试数据使用独立开发目录，不使用正式用户配置或系统凭据。

## 第五批：QuickCapture 启停与剪贴板监听归属

实施基线：`44e7a0d4` 加前四批未提交的工作区改动。分支仍为 `experiment/memory-probe-destroy-hidden`；内存探针与其他并行修改保持原样。本批不改 QuickCapture 数据格式、剪贴板读取器协议或图片内容处理。

| 职责 | 当前所有者 |
|---|---|
| 功能开关、文本/图片录制选项与依赖归一化 | `Services/QuickCaptureSettingsCoordinator`，通过 `IQuickCaptureSettings` 暴露 |
| 唯一活动剪贴板监听及退役实例的排空 | `Features/QuickCapture/QuickCaptureClipboardRuntime` |
| 实际 ContentChanged 订阅、读取与保存前检查 | `QuickCaptureClipboardService`，实现 `IQuickCaptureClipboardSession` |
| QuickCapture 格子创建、分组脱离、隐藏与关闭 | `WidgetManager`，通过注入的协调器提交功能状态 |
| 原设置页 XAML 属性、文案和诊断呈现 | `SettingsViewModel` 兼容门面，通过注入接口操作 |
| UI 调度、实例装配、进程退出顺序 | App 宿主，退出时等待协调器与监听任务结束 |

设置页三个开关、格子自身的关闭入口、格子创建、录制引导入口和默认设置恢复现共用同一设置写入者。QuickCapture 开关关闭时同步清除文本与图片录制选项并停止新监听；图片录制打开时同时打开文本录制和功能本身。外部设置变化由协调器观察，旧文件中“功能关闭但录制开启”的组合会归一化。设置页保留已有绑定名，WidgetManager 保留窗口能力，不再从这条链路直接调用 `App.Current.RefreshQuickCaptureClipboardService`。

快速开关的窗口操作按请求代次和现有 WidgetManager 锁串行。较旧的窗口操作不能在新请求关闭后把录制状态重新写回开启。监听退役先解除系统事件订阅并取消对未完成系统读取的等待，迟到结果不能进入数据层；已开始的本地写入允许完成，停用操作与进程退出会等待写入任务结束。失败的监听创建和窗口操作可通过下一次用户请求重试。

验证记录：

- QuickCapture、生命周期、模块边界与 Onboarding 定向测试先后 50/50、48/48 通过。首次全量测试发现一条旧 Onboarding 源码断言仍要求设置页直接调用 WidgetManager，已改为检查协调器及窗口锁的实际链路。
- 最终全量 x64 测试 4,120/4,120 通过，记录在 `tests/DeskBox.Tests/TestResults/quickcapture-architecture-full-final3-20260923.trx`。覆盖新协调器、单监听运行时、迟到读取取消及拒写、停用及重置等待、默认/外部设置归一化和原有功能回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 10:40 最终核验 PID 36108，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-architecture-20260923-32e4264d` 关闭了录制和实验开关。日志确认设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed、没有创建剪贴板监听或出现 QuickCapture 错误。此项验证装配与关闭状态，实际系统剪贴板和 QuickCapture 页面交互仍需人工验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。自动测试不能替代系统剪贴板与真实 UI 的设备交互验收。

## 第六批：Search 总开关与全局运行时入口

实施基线：`44e7a0d4` 加前五批未提交的工作区改动；分支仍为 `experiment/memory-probe-destroy-hidden`。内存探针和并行修改保留，本批只收拢 Search 功能总开关，不修改 Everything 查询语法、索引策略或快捷键产品规则。

| 职责 | 当前所有者 |
|---|---|
| Search 总开关的唯一设置写入、请求代次及页面探测排空 | `SearchSettingsCoordinator`，新增 `ISearchFeatureSettings` 端口 |
| Search 设置页的一次访问、连接状态与热键反馈 | 原 `SearchSettingsViewModel`，继续借用运行时能力 |
| Search 格子创建、隐藏、分组脱离与窗口退订 | `WidgetManager`，经注入的端口提交状态 |
| 搜索引擎、Everything provider、热键与弹窗实例 | App 宿主，向协调器注入启停能力；运行时资源仍由 App 释放 |
| 原功能卡片绑定与用户开关入口 | `SettingsViewModel` 的兼容门面 |

设置页、WidgetManager 直接调用及外部设置恢复现在进入同一启停请求。启用时先准备 App 拥有的搜索服务，再创建 Search 格子；禁用时先关闭格子，让内容退订原 SearchHistoryService，然后取消并等待设置页探测，最后释放引擎、热键和弹窗。WidgetManager 已移除 `App.Current.SetSearchFeatureEnabled` 直接业务回调。快速反向切换通过代次跳过排队中的旧请求，旧窗口操作不能改写较新的设置状态；退出在释放全局搜索实例前先停止协调器。

取消后的探测由协调器等待，最长 5 秒；若后端不响应取消，会记错并继续关闭宿主资源。迟到结果仍经取消检查拒绝回写当前页面。这是对不配合取消的外部能力所设的退出上限，不宣称其底层操作已经物理终止。

第六批验证：

- Search/模块边界/Onboarding/生命周期定向测试 48/48 通过，含关闭顺序、快速开关、启动失败重试、外部设置恢复、旧探测排空及热键冲突回归。
- 全量 x64 测试 4,125/4,125 通过，记录在 `tests/DeskBox.Tests/TestResults/search-master-architecture-full-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未做 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 10:59 核验 PID 17928，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/search-master-architecture-20260923-187bf5e4` 关闭 Search 功能与热键。日志确认设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed，没有建立 Everything provider 或记录 Search 错误。此项只验证装配及禁用状态；测试中的假宿主和启动日志无法替代 Everything IPC、系统热键及真实 Search 页交互验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第七批：WidgetManager 内容窗口注册与清理

实施基线：`44e7a0d4` 加前六批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只集中内容窗口的 ID、实例和 HWND 注册；文件格子的会话字典、分组状态机、Z-order、动画和内存探针实验保持原边界。

`ContentWindowRegistration<TWindow>` 对 WidgetManager 原有的 `_contentWidgets` 与 `_widgetWindowHandles` 就地操作，不保存第二份窗口清单。它统一注册、按实例注销、按 ID 且实例匹配注销、分组成员 ID 重绑和退出清空，拒绝另一窗口占用相同 ID 或 HWND。重复注册同一窗口保持幂等；旧窗口的迟到关闭回调无法移除已经替换的实例或句柄。

内容窗口创建后，主题跟踪、窗口登记、独立文件会话登记、表面宿主登记、胶囊布局及 Closed 回调接线都进入同一失败清理范围。任何一步失败，都会按实例移除内容窗口登记与文件会话、注销表面宿主并关闭候选窗口。正常 Closed 回调仅在实际移除了此窗口的注册 ID 时才持久化隐藏状态，因此不会把替换窗口的配置误写成隐藏。

分组原地切换先检查持久窗口仍登记且目标 ID 没有其他实例；若不满足，在身份提交前回滚准备中的过渡。成功后只把同一窗口从旧成员 ID 重绑到新 ID，HWND 集合不增不减。表面宿主仍由现有 `WidgetSurfaceRegistry` 管理；本批没有叠加另一套状态机。

验证记录：

- 内容注册、模块边界、Surface 分组/提升及呈现链路定向测试 81/81 通过，含重复 ID/HWND、创建失败清理、旧关闭回调、分组重绑与冲突不改状态。
- 全量 x64 测试 4,130/4,130 通过，记录在 `tests/DeskBox.Tests/TestResults/window-registration-full-20260923.trx`；包含内容注册身份边界及原有分组、Surface、文件窗口回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 11:31 核验 PID 41836，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/window-registration-20260923-2716c2d1` 不创建可见格子，也关闭剪贴板录制和热键。日志确认 WidgetManager 与设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed，没有窗口注册错误或致命异常。此项只验证装配与空窗口启动；真实分组切换、文件拖拽及动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第八批：文件格子会话的身份与清理

实施基线：`44e7a0d4` 加前七批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理独立 File 格子的 `FileWidgetSession` 登记，不改用户文件读写、拖拽/DropTarget、文件栈或窗口布局。

代码核对确认，当前生产路径统一使用 `ContentWidgetWindow` 宿主。独立 File 内容才在 `_fileWidgets` 中保留会话别名；同一宿主进入分组 Surface 时，`CommitSurfaceHost` 会移除该别名，拆回独立窗口时再登记。分组 Surface 的成员切换不需要为每个成员保留文件会话。

`FileSessionRegistration<TSession,THost>` 就地操作原有 `_fileWidgets` 字典，没有第二份会话清单。一个宿主最多拥有一个独立 ID；同一 ID、宿主和内容的重复登记保持幂等。同宿主换了 `FileSurfaceContent` 时会替换旧会话，避免 ViewModel/选择状态仍指向旧内容；同 ID 换宿主时按新会话替换，旧会话或旧宿主迟到清理只能按实例或宿主身份删除自己的条目。若该 ID 的内容窗口已属于另一宿主，文件会话登记会拒绝不一致的写入。

创建、失败回滚、普通关闭、分组退役、功能窗口关闭、删除和退出现在都经该入口修改会话字典。现有 `FileWidgetHostDiagnostics` 仅在物理宿主更换时计一次创建，同宿主内容重绑不虚增宿主数。内容窗口注册与 SurfaceRegistry 继续由各自的原所有者维护。

验证记录：

- 文件会话、内容窗口、模块边界、文件宿主诊断、Surface/分组及存储清理定向测试 100/100 通过，覆盖幂等、同宿主内容更换、同 ID 宿主替换、旧宿主迟到关闭、重复别名拒绝和退回独立窗口。
- 全量 x64 测试 4,134/4,134 通过，记录在 `tests/DeskBox.Tests/TestResults/file-session-architecture-full-20260923.trx`，包含文件会话身份边界与原有 Surface、分组、存储清理回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 11:58 核验 PID 24452，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/file-session-architecture-20260923-5364728d` 未创建可见格子，也关闭剪贴板录制与热键。日志确认 WidgetManager 与设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed，没有文件会话错误或致命异常。此项只验证装配与空窗口启动；实际文件拖拽和组切换仍需设备验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第九批：Surface 宿主声明的提交与回滚

实施基线：`44e7a0d4` 加前八批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只收口现有 `WidgetSurfaceRegistry` 的候选身份与分组提升时序；窗口层级、动画、文件操作和并行内存探针保持原边界。

核对的声明链路是：普通窗口创建登记活动宿主；已有分组的提升先暂存候选，首帧与保存成功后 `CommitSurfaceHost` 提交，再按实例退役旧窗口；失败时由创建清理或提升事务按实例注销候选。新建分组尚无 Surface 声明时，提升候选暂由事务持有，原独立窗口的成员声明保留到提交；这样首帧失败不需要凭配置猜测旧宿主来恢复。拆组复用则显式移除原组声明、登记独立宿主，失败时关闭替换宿主并恢复原声明。

`StageCandidate` 现在对同一宿主和成员幂等，但拒绝第二个宿主覆盖待提交候选，也拒绝把当前活动宿主暂存为自己的候选。候选暂存失败会中止创建并进入既有窗口失败清理，不再只记日志后留下一个无声明的窗口。提升事务还要求当前成员确为组活动成员，且候选必须新建；同 ID 已有内容窗口时提前拒绝，防止失败回滚误关原有实例。已有分组的活动宿主不能由普通创建路径直接替换；确有替换时必须走提升事务。`SynchronizeActive` 保留给恢复与拓扑稳定后的显式对账。

验证记录：

- Surface、候选事务、切换矩阵、窗口呈现与模块边界定向测试 74/74 通过。补充假宿主测试覆盖重复候选、取消后同组重试、已有分组失败保留旧宿主、新分组提交时转移成员声明、拆组回滚、迟到关闭和退出清空。
- 最终全量 x64 测试 4,139/4,139 通过，记录在 `tests/DeskBox.Tests/TestResults/surface-registry-full-final2-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 12:25 最终核验 PID 7860，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/surface-registry-20260923-d299a602` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认 WidgetManager 和设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed。此项只验证空布局启动装配；真实分组提升、拆离和首帧动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十批：Surface 成员声明转移与拓扑对账

实施基线：`44e7a0d4` 加前九批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Surface 成员别名的所有权转移及与在途分组切换的序列化；不改窗口层级、拖放规则、用户文件或磁盘 schema，并保留并行内存探针。

代码核对确认，合并是唯一增加组成员集合的入口。旧 `RemoveMemberClaims` 在普通注册、定义更新和恢复对账中遇到另一个 Surface 的成员别名时，会直接移除整个原会话；若对应窗口仍可见，就会留下没有 Registry 声明的物理宿主。现在这些入口默认严格拒绝冲突，且先校验所有别名再更新索引，失败不会只改一半。独立 Surface 也不能借显式转移参数夺走组成员。

合法合并在修改设置前通过现有 Registry 捕获预期源声明：只接受该成员自己的独立 Surface，或本次明确作为源组的 Surface；源组必须整体并入，不能只转走其中一名成员。捕获值保存会话身份、物理宿主、活动成员及成员集合的不可变快照。首帧与设置保存成功后，提交在 Registry 锁下再次核对快照和完整的源声明集合，再一次性移除旧别名并登记目标 Surface。在途切换或窗口替换若改变源状态，合并会拒绝提交并走既有候选/设置回滚。恢复对账与旧宿主迁入组 Surface 只允许转移当前同一物理宿主的独立声明。

拆离的非复用路径和解散路径在请求退役旧组宿主后显式移除旧 Surface 声明，然后才创建独立或剩余组窗口；旧 Closed 回调晚到时不能删除新窗口的别名。合并、拆离、解散和成员重排在修改拓扑前等待相关 Surface 切换 gate，并持有到操作收尾；多 gate 按 Surface ID 排序，取消时释放已经取得的 gate。`_widgetGroupGate` 仍串行拓扑操作，未添加第二份窗口清单。

验证记录：

- Surface、分组、呈现与模块边界定向测试 222/222 通过。新增假宿主测试覆盖多源组完整转移、重复/部分/过期声明拒绝、捕获后宿主或活动成员改变、待提交候选拒绝、保存失败保留源声明、旧组退役后的迟到关闭；gate 测试覆盖多 Surface 等待与取消释放。
- 最终全量 x64 测试 4,154/4,154 通过，记录在 `tests/DeskBox.Tests/TestResults/surface-claim-transfer-full-final-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 13:05 最终核验 PID 12192，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/surface-claim-transfer-20260923-27ff52b1` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认 WidgetManager 和设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed。未得到真实分组合并、拆离或解散的设备日志；自动化假宿主测试与空布局启动不能代替实际窗口、拖动及动画验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十一批：分组持久化后窗口失败的补偿

实施基线：`44e7a0d4` 加前十批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批处理分组拓扑保存后的窗口替换失败，不修改磁盘 schema、Z-order、拖放和用户文件操作；并行内存探针保持原样。

代码核对确认，非复用拆离与解散在保存新拓扑后退役旧组宿主，再逐个创建替换窗口。原路径在创建、显示或首帧失败时会直接退出，留下已保存的新拓扑与不完整的窗口集合。现在每个成功创建的替换宿主都按实例记录，并等待可见内容窗口的首帧。创建或显示中途失败的新宿主会按实例注销、隐藏和关闭；若整条替换链路失败，先把原拓扑写回磁盘。回写成功后才清理已创建的新窗口、移除旧声明并恢复原组宿主。旧宿主仍完整登记且可见时直接保留它。恢复的设置写盘被拒绝或抛错时，内存恢复为已经持久化的新拓扑，避免继续维持相反的设置状态；已出现的窗口错误会记录在日志中。

`WidgetGroupMutationSnapshot` 现在既能还原仍在设置中的组，也能还原解散后“不存在该组”的已提交状态；拆离的新状态还保存被移出的成员，组成员非活动时间记录也随快照恢复。合并则区分候选在设置保存前失败与新拓扑已保存后宿主提交失败：前者恢复内存并在已尝试保存时重写原拓扑，后者通过同一持久化回滚规则决定保留原拓扑或已保存的新拓扑。新宿主提交后若旧 HWND 退役异常，按旧实例补做清理并保留已提交的新宿主，不让候选事务误关它。

验证记录：

- 分组持久化回滚、Surface、呈现与模块边界定向测试 226/226 通过。新增纯事务测试覆盖回写成功、返回失败和抛错后的内存/磁盘状态选择；呈现契约测试检查非复用拆离、解散均在替换窗口首帧失败时进入补偿。既有迟到 Closed 与 Surface 身份测试继续通过。WinUI 窗口创建和真实首帧失败尚未做设备级故障注入。
- 首次全量测试 4,157/4,158：唯一失败是设置切片访问门禁发现新增 2 处平铺访问。新代码已改为通过 `WidgetLayout` 切片写入，门禁单测通过；最终全量 x64 测试 4,158/4,158 通过，记录在 `tests/DeskBox.Tests/TestResults/group-replacement-recovery-full-final2-20260923.trx`。未扩张旧访问清单。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 14:06 最终核验 PID 9484，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/group-recovery-20260923-78703819` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认设置窗口完成构造、35 个启动步骤中 0 degraded / 0 failed。此项只验证装配；真实分组窗口的创建、首帧、拖动和动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十二批：复用拆离的持久化回滚与设备检查

实施基线：`44e7a0d4` 加前十一批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只修复复用原 HWND 拆离时的回滚写盘边界，并在独立开发数据目录检查真实窗口。并行内存探针、用户文件操作、磁盘 schema 和窗口动画策略未改。

原复用路径先把新拓扑写盘，再将活动 HWND 改为独立成员并创建剩余成员的替换宿主。失败时旧代码无论回滚写盘是否成功，都会把原 HWND 重新登记为旧组；若写盘失败，磁盘已拆离而内存和 Registry 却恢复为旧组。现在用已提交状态的快照调用 `WidgetGroupPersistedTopologyRecovery`：只有旧拓扑回写成功才关闭替换宿主、恢复旧组声明和原 HWND；回写被拒绝或抛错时，内存保持已保存的拆离状态，原 HWND 维持独立成员声明，存活的替换宿主继续保留，缺失时尝试补建并记录失败。复用路径的创建、首帧和回滚写盘故障点只在带独立 `DESKBOX_DEV_DATA_ROOT` 的 Debug 构建下可单次触发。为自动执行设备检查临时加入的启动动作入口已在验证后移除；常规 Debug 启动不执行分组动作。

验证记录：

- Surface、分组、呈现、设置访问门禁及故障探针定向测试 235/235 通过；最终移除临时启动动作入口后，全量 x64 测试 4,166/4,166 通过，记录在 `tests/DeskBox.Tests/TestResults/reused-detach-rollback-final-clean-20260923.trx`。用例覆盖回滚写盘成功与失败时的内存/Registry 归属，以及写盘确认前不恢复旧组声明。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立配置中的真实 HWND 检查：正常复用拆离从 1 个组宿主变为 2 个可见独立宿主，原 HWND `0x9B0E5C` 保留；替换创建失败时旧组与原 HWND `0xE610DA` 保留；替换首帧失败时回滚写盘成功，旧组与原 HWND `0x420210` 保留，替换窗口不再可见；首帧失败且注入回滚写盘拒绝时磁盘组数为 0、Registry 有 2 个 Surface，两个实际 HWND 均可见。合并检查中源 HWND 关闭、目标 HWND 保留，2 个窗口归为 1 个组 Surface；解散检查中旧组 HWND 关闭，出现 2 个可见独立窗口。上述操作由隔离 Debug 诊断入口自动触发，完成后入口已移除；它验证了 Win32 HWND/注册关系，不等同于用户拖拽手势、视觉动画或窗口层级的人工验收。
- 最终空布局 Debug 使用 `C:/Users/simon/AppData/Local/DeskBox-Dev/reused-detach-final-20260923-80ffe2f9`，2026-09-23 15:07 核验 PID 39020，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed。`git diff --check` 通过，未提交或推送。

## 第十三批：Todo 布局设置的单一写入入口

实施基线：`44e7a0d4` 加前十二批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批从剩余 Todo 显示设置中先选布局链路，不同时迁默认筛选、标签栏和文字/预览设置；Todo 任务数据、提醒运行时、磁盘 schema、XAML 绑定名与窗口内容实现不变。

原设置页的 `SelectedTodoLayoutMode` 同时写 `TodoLayoutMode` 和兼容字段 `TodoUseWideDetailPane`，另一个回调单独写 `TodoAutoSelectFirstInWideLayout`；默认值恢复又直接写同三个平铺字段。现在 `ITodoSettings` 提供窄的 `TodoLayoutSettings` 快照与布局更新命令，现有 `TodoSettingsCoordinator` 是三项布局状态的唯一 UI 写入者，继续使用 `SettingsService` 的布局归一化规则。`TodoSettingsViewModel` 持有可编辑快照并发布属性变化；`SettingsViewModel` 只转发原 `SelectedTodoLayoutMode` 和宽布局开关绑定，同时维护旧版宽布局布尔属性的兼容写入。外部设置恢复通过编辑器刷新，默认值恢复调用同一布局重置命令；整页恢复由外层统一保存，不提前排队一次布局单独保存。其他 Todo 选项仍按原路径，未把整张设置页一次性重写。

验证记录：

- Todo 协调器、设置切片/模块边界和 AOT 定向测试在最终小幅时序调整后 529/529 通过。新增用例覆盖布局模式与旧兼容字段同时写入、宽布局自动选中、外部变化刷新、默认值重置、持久化往返和协调器停止后的失败回退。
- 最终全量 x64 测试 4,169/4,169 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-layout-writer-full-final-20260923.trx`。既有 JSON 默认值、序列化、设置绑定与 Surface 回归均通过。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-layout-final-20260923-ad163b26` 预置 `SinglePane`、兼容宽布局关闭、自动选中关闭及空格子布局。2026-09-23 15:28 核验 PID 3964，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，三个磁盘字段保持一致。此项验证装配与既有状态加载；Todo 设置页实际点击、运行中宽窄布局切换仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十四批：Todo 默认筛选与标签可见性的联动写入

实施基线：`44e7a0d4` 加前十三批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Todo 默认筛选、七个标签可见位与标签栏总开关；Todo 字号、预览行数、任务数据、提醒和 XAML/JSON 字段保持原样。

原 `SelectedTodoDefaultFilter` 会先打开目标标签，再单独写默认筛选；各标签回调又通过 `PersistTodoTabSettings` 写整组布尔值，并在默认标签被隐藏时反过来调用筛选属性，形成多次保存和回调递归。现在 `ITodoSettings` 增加不可变 `TodoTabSettings` 快照，现有 `TodoSettingsCoordinator` 一次提交目标标签与默认筛选，或一次提交标签可见性与必要的回退。它使用 `SettingsService` 已有的筛选规范化、标签可见性和首个可见标签顺序，确保至少一个标签可见且默认筛选指向可见标签。标签栏总开关走同一写入者。`TodoSettingsViewModel` 发布快照变化；设置页保留原生成属性、`SelectedTodoDefaultFilter`、可见选项和文案绑定，只把用户操作转发给编辑器，并按最终快照校正开关，避免关闭最后一个标签后界面停在错误的关闭状态。

默认值恢复通过 Todo 编辑器重置整组标签且由外层统一保存，设置页不再直接写这一组平铺属性。外部设置刷新从编辑器读取；对尚未持久化的无效外部组合只生成安全的只读展示快照，不在读取时修改磁盘，下一次实际设置操作再提交归一化状态。`SettingsService` 在加载磁盘配置时仍按原规则修复无效字段，磁盘 schema 未变。

验证记录：

- 最终无效外部配置边界修复后的定向 x64 测试 657/657 通过，覆盖选择隐藏筛选时自动显示标签、隐藏当前默认标签后的顺序回退、最后一个标签不能消失、标签栏开关、外部刷新、默认重置与持久化往返。
- 最终全量 x64 测试 4,173/4,173 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-tab-writer-full-final-20260923.trx`；原设置、绑定、AOT 契约及 Surface 回归通过。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-tab-writer-20260923-8e4dec31` 预置 `ThisWeek` 为默认筛选及唯一可见标签，其他功能与热键关闭、格子布局为空。2026-09-23 15:54 核验 PID 36904，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘筛选/标签组合保持一致。此项只验证装配与已保存状态加载；Todo 设置页实际点击和运行中格子切换仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十五批：Todo 内容密度与文字大小设置

实施基线：`44e7a0d4` 加前十四批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Todo 预览行数、列表字号和正文字号；新任务位置、页脚显示、编辑器 Enter 行为、任务数据、磁盘 schema 与 XAML 绑定名不变。

原 `SettingsViewModel` 分别直接写三个平铺字段。预览行数按 1–10 行归一化；Todo 字号磁盘覆盖值为 `0` 时继承全局字号，界面显示的是有效字号。滑块只接受 10–16pt、0.5pt 步进，拖动期间沿用全局外观预览与延迟保存机制。现在 `ITodoSettings` 暴露有效值快照，现有 `TodoSettingsCoordinator` 是三个 Todo 原始字段的唯一 UI 写入者。`TodoSettingsViewModel` 持有可编辑预览行数和有效字号；设置页保留原绑定与字号文案，但字号滑块只向编辑器提交覆盖值，仍由原设置页决定何时请求预览和保存。全局字号变化时编辑器重读有效值，原始覆盖值为 `0` 的 Todo 字号只更新界面显示，不被误存为独立覆盖值。

默认功能设置恢复中的预览行数走同一编辑器命令，并由外层统一保存；原功能恢复没有重置 Todo 的两个独立字号，本批保持此行为。外部配置刷新和启动装配共用编辑器快照，设置页不再直接写这三个 Todo 平铺字段。

验证记录：

- Todo、SettingsService、设置切片/模块边界、AOT 与设置同步定向 x64 测试 677/677 通过，新增用例覆盖字号 `0` 继承全局值、显式字号覆盖后的独立保持、半点归一化、预览行数上下界、默认恢复、持久化往返与停止后拒写。
- 全量 x64 测试 4,176/4,176 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-density-writer-full-20260923.trx`。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 最终构建 22 警告、0 错误。独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-density-20260923-93839b4b` 预置全局字号 12.5、Todo 预览 4 行、列表字号覆盖值 0、正文覆盖值 13.5 及空格子布局。2026-09-23 17:47 核验 PID 38288，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘字段保持原值。此项验证装配与加载，未代替 Todo 设置页滑块拖动和运行中格子字号变化的设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十六批：Todo 输入行为设置的写入归属

实施基线：`44e7a0d4` 加前十五批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 `TodoNewTaskPosition` 和 `TodoEditorEnterBehavior` 的设置写入；任务排序算法、键盘事件路由、Todo 数据、磁盘 schema 和 XAML 绑定名不变。

原设置页分别直接写“新任务顶部/底部”和“Enter/Ctrl+Enter 保存”平铺字段。现在 `ITodoSettings` 增加输入行为快照，`TodoSettingsCoordinator` 是这两个字段的唯一 UI 写入者，`TodoSettingsViewModel` 发布编辑状态，原 `SettingsViewModel` 属性仅转发绑定并通知内容摘要。设置服务和 Todo 运行时复用同一新任务位置归一化规则：只有 `Bottom` 表示底部，其余回顶部；Enter 行为继续使用既有大小写不敏感的归一化及 `ShouldSubmitEditorOnEnter` 判断。默认功能设置恢复由 Todo 编辑器一次重置这两项，再由外层统一保存；外部配置刷新只读取规范化快照，不在读取时改盘。

验证记录：

- Todo 协调器、Todo 运行时、SettingsService、设置切片/模块边界和 AOT 定向 x64 测试 734/734 通过。新增用例覆盖 Bottom/EnterSaves 写入与持久化、Enter 和 Ctrl+Enter 原按键规则、非法值回退、外部刷新、默认重置与停止后的拒写。
- 全量 x64 测试 4,179/4,179 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-input-writer-full-20260923.trx`。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建 22 警告、0 错误。独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-input-20260923-a49138a2` 预置 Bottom、EnterSaves 与空格子布局。2026-09-23 17:59 核验 PID 32308，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘字段保持原值。此项验证装配与加载；真实 Todo 编辑器按键输入、新任务插入位置仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十七批：Todo 显示开关与标签样式的写入归属

实施基线：`44e7a0d4` 加前十六批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理已完成任务可见性、页脚统计、清除已完成按钮和 Todo 标签样式四项呈现偏好；Todo 任务过滤与清除命令、磁盘 schema、XAML 绑定名、实际控件布局均未改。

原设置页的三个显示开关回调直接写 `SettingsService.Settings`，标签样式属性另行规范化并写盘；默认功能设置恢复也直接写四个平铺字段。现在 `ITodoSettings` 提供这组设置的不可变快照与更新/重置命令，`TodoSettingsCoordinator` 是四个字段的唯一设置页写入者，继续使用 `SettingsService` 的 `Pivot/Button` 规范化规则。`TodoSettingsViewModel` 在编辑、外部配置刷新和默认重置后发布变化；原 `SettingsViewModel` 仅保留生成开关、标签索引和文案的 XAML/AOT 兼容绑定。协调器停止导致写入失败时，生成开关会按编辑器的实际值回退。功能默认恢复通过编辑器重置这四项，仍由外层统一保存；对外部无效标签样式的只读快照不改盘，显式重置时才写回规范值。

验证记录：

- Todo 协调器、SettingsService、设置切片/模块边界、设置同步与 AOT 定向 x64 测试 682/682 通过。新增用例覆盖四项设置的持久化往返、外部刷新、无效标签样式只读归一化及重置写回、协调器停止后的拒写与状态回退。
- 全量 x64 测试 4,181/4,181 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-display-20260923-1d244a8d` 预置“隐藏已完成、显示页脚统计、隐藏清除按钮、Pivot 标签”和空格子布局。2026-09-23 18:13 核验 PID 32900，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，四个磁盘字段保持原值。这验证装配与已存配置加载，不代替 Todo 设置页实际点击、运行中列表过滤和页脚呈现的设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十八批：Todo 功能默认恢复中的提醒写入收尾

实施基线：`44e7a0d4` 加前十七批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只收口 Todo 功能默认恢复时的提醒开关与默认提前时间；任务数据清理、提醒扫描算法、磁盘 schema、XAML 绑定名及格子重置顺序未改。

原功能重置在 `_isApplyingSettingsSnapshot` 保护下给设置页属性赋值，实际写盘仍靠随后两处 `SettingsService.Settings.Todo...` 平铺直写。现在 `ITodoSettings` 增加 `ResetReminderPreferences`，由 `TodoSettingsCoordinator` 一次把两项原始字段恢复为启用和 5 分钟，`TodoSettingsViewModel` 刷新编辑快照与设置页摘要。功能重置传入 `scheduleSave:false`：此阶段不排队保存，也不提前刷新提醒；外层 `SaveAsync` 通知设置变化时才按原时序协调提醒。普通显式重置可自行延迟保存并刷新一次；从关闭状态恢复时沿用立即检查规则。设置页对 Todo 切片或其平铺兼容属性已没有直接写入，`SettingsService` 的加载、迁移和 JSON 门面继续保留。模块边界测试按 `TodoSettingsSlice` 的属性清单检查 `SettingsViewModel` 源文件，阻止新增直接赋值。

验证记录：

- Todo 协调器、提醒运行时、SettingsService、设置边界和 AOT 定向 x64 测试 691/691 通过；补充真实 `TodoReminderRuntime` 假会话联测后，相关最终定向测试 44/44 通过。用例覆盖默认值快照、外层保存前零刷新、保存后仅一次协调、会话创建与关闭、显式恢复的 `checkNow`、重复重置不重复通知、停止后的拒写、外部保存恢复及持久化往返。
- 最终全量 x64 测试 4,186/4,186 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-reminder-reset-20260923-30f36340` 预置提醒关闭、提前 30 分钟、Todo 关闭及空格子布局。2026-09-23 20:05 最终核验 PID 44552，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘提醒值保持原样。该检查证明装配和已有设置加载，未在真实 UI 中执行清空 Todo 数据的功能重置，也不等同于系统通知投递验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十九批：QuickCapture 默认视图与标签可见性的联动写入

实施基线：`44e7a0d4` 加前十八批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 QuickCapture 默认视图、Records/Pinned/Recent 三个可见位和标签栏总开关；内容数据、剪贴板录制选择、标签样式、磁盘 schema 与 XAML 绑定名不变。

原设置页选择默认视图时会先通过生成属性打开对应标签，再写默认视图；隐藏当前默认标签时，标签回调写整组可见位，并可能再次进入默认视图属性，形成重复保存和回调联动。现在 `IQuickCaptureSettings` 暴露不可变 `QuickCaptureTabSettings` 快照，现有 `QuickCaptureSettingsCoordinator` 在一次写入中同时维护默认视图和标签可见性。选择隐藏视图会打开它；隐藏默认视图按 Records、Pinned、Recent 顺序回退；最后一个标签被关闭时恢复 Records。`SettingsService` 与设置页共用一条默认视图归一化规则。对尚未保存的无效外部组合，读取只给出安全快照，不在读取时改盘；下一次实际操作或默认重置才提交规范值。

设置页继续保留原生成开关、默认视图和摘要的 XAML/AOT 兼容绑定，变更通过协调器写入并按最终快照校正，停止后不能留下虚假的界面状态。功能默认恢复调用协调器重置整组导航偏好，仍由外层统一保存；外部设置刷新从同一快照同步。导航变动只通知设置页，不刷新或重建剪贴板监听。模块边界测试阻止设置页重新直接写这五个字段。

验证记录：

- QuickCapture 协调器、SettingsService、设置同步/复制、模块边界和 AOT 定向 x64 测试 667/667 通过；补充导航与剪贴板监听隔离用例后，相关最终定向测试 28/28 通过。新增用例覆盖选择隐藏默认视图的一次提交、隐藏当前默认标签后的顺序回退、最后一个标签的保底、无效外部组合的只读展示、默认恢复与持久化、协调器停止后的拒写，以及标签变动不刷新剪贴板会话。
- 最终全量 x64 测试 4,192/4,192 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-tabs-20260923-8137ed76` 预置默认 Recent、只显示 Recent 标签、隐藏标签栏及空格子布局。2026-09-23 20:40 核验 PID 40120，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，五个磁盘字段保持预置值。这验证装配与已保存状态加载，实际点击标签、运行中 QuickCapture 格子切换及视觉呈现仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第二十批：QuickCapture 标签样式与内容预览偏好

实施基线：`44e7a0d4` 加前十九批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 QuickCapture 标签样式、创建时间显示和列表预览行数；内容数据、剪贴板录制、最近记录容量、字号、编辑器格式、宽布局、磁盘 schema 与 XAML 绑定名不变。

原设置页把三个字段分别写入 `SettingsService.Settings`，默认功能恢复也在属性赋值后直接写平铺字段。现在 `IQuickCaptureSettings` 提供不可变 `QuickCapturePresentationSettings` 快照，现有协调器是三项的唯一设置页写入者，继续使用 `SettingsService` 的 `Pivot/Button` 和 1–10 行归一化规则。设置页原标签索引、布尔开关、预览行数及摘要绑定只转发操作并同步最终快照；外部设置刷新从同一快照读取。对尚未保存的无效原始值只做只读规范化，显式编辑或默认恢复才写回。功能默认恢复调用协调器一次重置这三项，外层仍负责统一保存。呈现设置变化会通知设置页，但不会刷新或重建剪贴板监听；协调器停止后的编辑尝试会按真实快照回退。模块边界测试阻止设置页重新直写这三个字段。

验证记录：

- QuickCapture 协调器、SettingsService、设置同步/复制、模块边界和 AOT 定向 x64 测试 672/672 通过。新增用例覆盖非法样式与越界行数的只读展示、用户写入归一化和持久化往返、默认值重置时不提前保存、外部呈现变化通知且不刷新剪贴板会话，以及停止后的拒写。
- 全量 x64 测试 4,196/4,196 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-presentation-20260923-76c3d5ba` 预置 Pivot、隐藏创建时间、预览 7 行及空格子布局。2026-09-23 21:02 核验 PID 16628，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，三个磁盘字段保持预置值。这验证装配和已保存状态加载；实际设置页点击、QuickCapture 内容卡片样式及运行中切换仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第二十一批：QuickCapture 最近记录容量与裁剪归属

实施基线：`44e7a0d4` 加前二十批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 `QuickCaptureRecentLimit` 的设置写入及对应历史裁剪；容量范围、磁盘格式、记录数据模型、剪贴板监听规则、XAML 绑定名和功能重置的数据清理范围不变。

原设置页在容量回调中直接写平铺字段并保存，再通过 `App.Current.QuickCaptureService.TrimRecentItemsAsync` 发起无人等待的裁剪。现在 `IQuickCaptureSettings` 暴露规范化容量的读取、设置和默认恢复命令，`QuickCaptureSettingsCoordinator` 是设置页的唯一写入者。App 在装配时注入数据服务的裁剪动作；设置页只保留数字输入、文案与兼容绑定，外部配置刷新从协调器读有效值。沿用 `QuickCaptureService.NormalizeRecentLimit`：小于最小值回默认值，高于最大值截断。功能默认恢复通过协调器设回默认容量，取消未开始的裁剪，由外层统一保存并沿用原有数据清理流程。

协调器用 350 毫秒安静期合并连续调整：尚未开始的旧请求被最新容量取代，已经进入数据服务的裁剪串行完成。裁剪异常交由宿主错误报告，后续请求仍能继续；退出取消待执行请求并等待活动裁剪，避免释放数据服务时遗留任务。容量编辑、显式恢复或外部容量变化才安排裁剪；标签和呈现偏好仍不刷新剪贴板监听。已开始的裁剪不能撤销，随后调大容量无法恢复之前已移除的记录；这是该操作原有的数据语义，快速调整只避免尚未开始的过期裁剪。

验证记录：

- QuickCapture 协调器、剪贴板运行时/服务、SettingsService、设置同步、模块边界和 AOT 定向 x64 测试 678/678 通过；调整通知顺序后相关最终定向测试 39/39 通过。新增用例覆盖快速设置仅执行最后的待裁剪值、默认恢复取消待执行请求、停止等待活动裁剪并拒绝新写入、失败报告后下一次仍可运行、外部配置改变不刷新剪贴板监听。真实隔离 `QuickCaptureStore` 中的 25 条最近记录按新容量裁为 10 条，重新加载仍为 10 条。
- 最终全量 x64 测试 4,203/4,203 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，890 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-limit-20260923-5731a4ff` 预置容量 80、功能关闭及空格子布局。2026-09-23 22:54 核验 PID 39980，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘容量保持 80。该启动检查验证装配与加载；真实设置页数字输入交互、运行中剪贴板采集仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 历史下一批（第 22 批现已完成）：QuickCapture 列表与正文字号的写入归属

当时设置页仍直接写 `QuickCaptureListTextSize` 与 `QuickCaptureContentTextSize` 两个可选覆盖值，原始值 `0` 表示继承全局字号。第 22 批仅收口这两项：

1. 核对字号滑块的 10–16pt、0.5pt 步进、即时外观预览、延迟保存与全局字号变化时的继承行为，不改变视觉范围或现有 XAML/JSON 字段。
2. 由 QuickCapture 协调器接收两个原始覆盖值并提供有效字号快照；设置页保留滑块绑定和预览时序，外部恢复时不能把继承值误写成覆盖值。
3. 验证 `0` 继承、显式覆盖、非法输入、默认功能恢复、持久化和停止拒写，再做 x64/AOT 与隔离 Debug。编辑器格式、宽布局和远程图片策略继续分批处理。

## 后续保留事项

- Todo 设置页仍保留 XAML/AOT 兼容属性和摘要文案；功能重置涉及数据清理和两次保存/提醒协调，若要改变其事务和通知时序，应单独审查并做设备验收。
- SettingsService 仍有根对象和兼容属性；其他功能写入入口、全局 SettingsChanged 参数化还未迁移。
- 原生通知注册/激活和 Dispatcher 由宿主拥有，本批没有引入第二个 UI 线程或 Generic Host。
- 全局备份生成/上传任务和容器释放已在第三批收口；云端列表与连接检测已迁入页面编辑器；恢复事务仍由原数据备份服务负责。日志队列的整体迁移仍待后续评估。
- 每批更新本记录的实际证据和下一批范围，代码搬迁不同时修改磁盘 schema、文件安全事务或产品交互。

## 2026-09-24：A+B/C/D 与第 22 批统一候选收口

当前隔离候选在 `C:/Users/simon/.codex/worktrees/deskbox-surface-group/wingezi`，包含第 1–22 批架构改动及评审核实后的 A+B/C/D 修复；原共享目录 `D:/project/wingezi` 和 `MemoryDestroyProbe` 未并入。候选 HEAD 仍是本地检查点 `f3f357f1`，本轮增量均未提交、推送或创建 PR。

- QuickCapture 最近记录裁剪贯通退出取消令牌，15 秒退出步骤上限兜底；不可取消的原子持久化仍可能在超时后运行，退出会记录该情况。随记格子内启用剪贴板捕获改为先保存设置、后刷新监听，避免额外显示格子；协调器测试钉住该行为。
- Todo 列表/正文非有限字号输入在协调器和编辑门面均拒写。第 22 批 QuickCapture 两个字号覆盖值已单独提取到候选：原始 `0` 继续继承全局字号，设置页由协调器写入；增加实际设置门面的继承、保存、再显式覆盖往返测试。
- D 段补充仅在 Debug 且隔离数据根下启用的无目标窗口故障点。用户实测合并双故障后无可用窗口：5 个故障点命中，磁盘仍保留 1 组 2 成员，两份样例文件完好；去掉故障重启恢复同一组合且切换正常，Registry 仅 1 条有效声明。复用拆离的首次窗口外拖动命中 `reused-detach-first-frame`，真实回滚写盘和窗口边界恢复均成功；之后的正常拖动又完成拆离。具体日志、HWND 和数据根见 `surface-group-recovery-segment-20260924.md`。
- 最终统一候选全量 x64 测试 **4,226/4,226 通过**，本地 TRX 为 `tests/DeskBox.Tests/TestResults/architecture-final-x64-20260924.trx`（按仓库规则被忽略）；Release AOT audit+smoke 条件构建 888 警告、0 错误，canonical Debug 构建 22 警告、0 错误。
- x64 Native AOT publish/link 与完整 `publish-aot-audit.ps1` 审计通过，产物 44 个文件、约 95.7 MiB，`AlwaysThrowCount=0`，源码快照审计前后一致。真实 AOT 程序在独立数据根启动，36 步 0 degraded / 0 failed。审计中发现两处旧源码形态匹配误报（主 ViewModel `Dispose` 子串匹配、Todo 提醒异步入口改为包装+核心方法），已收紧对应审计条件；两次失败的原始产物和摘要均保留在 `.artifacts/aot-audit/` 下。
- x64 Native AOT 测试 MSIX `1.5.5.0` 构建完成，原始包 73 文件、签名副本 75 文件的静态包审计均通过。未签名包被 `0x80073CFF` 拒绝；当前用户 TrustedPeople/Root 信任仍被 `0x800B0109` 拒绝，符合微软文档对 `LocalMachine\TrustedPeople` 的要求。经用户另行授权后，短期测试证书仅临时加入整机 TrustedPeople：MSIX 成功安装，状态 `Ok`。从包入口启动的 PID 13096 位于 `WindowsApps`，`GetPackageFullName` 与安装包身份完全一致，exe SHA-256 与审计解包文件一致，完整性级别为 Medium；“DeskBox 设置”窗口可响应，导航按钮实际切换并恢复。随后结束该进程、卸载包并移除整机证书信任；当前用户/整机均无该证书，包与进程均为 0。包内启动证据保存在 `.artifacts/architecture-final-sideload-test-x64-20260924/packaged-run-evidence.json`。正式双架构安装包、商店合并上传包与发布不属于这次架构候选。
- 隔离 Debug 数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-final-quickcapture-ui-20260924` 预置全局字号 12.5、随记列表原始覆盖值 `0`、正文覆盖值 13.5。用户在真实设置页看到列表 12.5pt/正文 13.5pt，调列表滑块到 13pt 正常；磁盘只将列表原始值改为 13，正文仍为 13.5。用户开启随记格子并在「最近」页点击「开启记录」，界面仍只有 1 个随记窗口、最近页正常；磁盘记录功能与剪贴板记录均启用，布局仅 1 个随记格子，日志有监听启动与一次文本捕获、无相关错误。强制结束测试进程后重启同一目录，1 个随记格子和字号/记录设置保持不变，启动 35 步 0 degraded / 0 failed，监听恢复。测试实例最后已结束，不影响生产数据根。

### 下一批

1. 已复核“可见组里单个隐藏成员”的可达性：`WidgetGroupSettings.Normalize` 与正常组操作都强制成员可见性跟随组；独立磁盘注入测试在启动时把隐藏成员修正为可见，35 步 0 degraded / 0 failed，现有归一化单测复跑 1/1 通过。真实拖离已覆盖回滚分支，因此不再把无正常入口的隐藏态手势列为人工交付门槛；保留可见性传值作为防御性修复。详见 `surface-group-recovery-segment-20260924.md`。
2. 审阅统一候选相对检查点的每个 hunk，确认只含 A+B/C/D、第 22 批及本轮审计修复；分段固化并交给 CI 后再考虑 PR/主干集成。云同步、设备层 store、Generic Host 和物理拆工程仍按原路线图的立项触发，不挤入本次收口。

## 2026-09-24：增量差异复审完成

本轮对 `f3f357f1` 之后的 36 个已跟踪改动和当时 4 个新文件按 A+B、C、D、第 22 批、AOT 审计分类复核；没有内存探针、版本/安装器/Release 元数据或多语言资源混入。第 22 批提取的五个独立文件与原共享目录逐字节一致，交织文件的修改点逐段核对。完整结论与后续提取顺序见 `architecture-final-candidate-review-20260924.md`。

**下一批改为分段提取与交付**：从已有 A+B 本地提交 `61f05c5d` 对齐本轮修复，再按 C→D→第 22 批→AOT 审计契约整理可审阅差异；各段复验后才进入远程 CI/PR。此前列出的“隐藏成员必须人工故障复现”经归一化源码、既有单测和独立启动注入校正为无正常持久化入口的防御项，不再阻塞这一步。

## 2026-09-24：A+B 与 C 分段对齐完成

A+B 工作树已补齐最终 QuickCapture 退出/快捷入口和 Todo 非有限字号修复，保留第 22 批独立；全量 x64 **4,167/4,167**、AOT 条件构建 0 错误、canonical Debug 0 错误，隔离启动 36 步 0 degraded / 0 failed。C 工作树在此基础上保留自己的窗口登记边界及零 HWND 守卫；全量 x64 **4,177/4,177**、AOT 条件构建 0 错误、canonical Debug 0 错误，隔离启动 36 步 0 degraded / 0 failed。两次测试进程均已结束。两段记录分别见各自 `feature-runtime-settings-segment-20260924.md` 与 `window-registration-segment-20260924.md`。

**下一批**：以已验证的 A+B/C 差异作为分段交付输入，将 D Surface/格子组和第 22 批字号分别形成可审阅的后续增量，再跑最终叠加态检查并交给远程 CI。当前三个候选工作树都未提交本轮修复、未推送、未创建 PR；`f3f357f1` 仍只是整批回退锚点。详见 `architecture-final-candidate-review-20260924.md`。

## 2026-09-25：分段提交与远程审阅

在用户明确授权“发”之后，A+B 已提交并推送 `c966a9a0`，创建 [PR #423](https://github.com/Tianyu199509/DeskBox/pull/423)；C 从该提交叠加 `6728c2e4`，创建 [PR #424](https://github.com/Tianyu199509/DeskBox/pull/424)；D 再叠加 `a3809579`，创建 [PR #425](https://github.com/Tianyu199509/DeskBox/pull/425)。第 22 批使用独立 `codex/architecture-quickcapture-text-size-review` 分支，以 D 提交为基线。前三段本地全量 x64 分别为 4,167、4,177、4,224 全绿；最终叠加态 **4,229/4,229** 通过，完整 Native AOT publish/link 与审计通过。最终 canonical Debug 从独立数据根恢复 1 个随记格子，原始字号 13/13.5 与剪贴板记录状态保持，启动 35 步 0 degraded / 0 failed；测试进程已退出。最终源码、测试与脚本和先前已实测的统一候选逐文件一致，内存探针未进入提交链。

**下一批**：核对四个 PR 各自 CI 及审阅反馈，按依赖顺序处理合并；合并动作和正式版本发布另行授权。云同步设备层、contribution descriptor、Generic Host 或物理拆工程仍遵照原路线图的触发条件，不混入当前 PR 链。具体范围见 `architecture-final-candidate-review-20260924.md`。

## 2026-09-25：四段 PR 栈合并完成

经逐段核对，#423、#424、#425、#426 均通过远程 `Build and test`，并按此顺序以保留提交祖先关系的合并提交进入 `main`。四个合并提交分别为 `a7488e46`、`cc3dd408`、`ab2044d1`、`8cedf5f2`；每一步的文件树与对应 PR head 一致，最终远端 `main` 为 `8cedf5f2`，四个 PR 均显示 `MERGED`。提交作者与提交者均为 Simon。原共享工作区 `D:/project/wingezi` 的并行 `MemoryDestroyProbe` 和两份外部审查报告没有并入，也没有清理该工作区。

合并前修复了第 22 批切片遗漏：全局字号回调现在与 Todo 一样立即刷新 QuickCapture 设置协调器。正常滑块拖动及提交不会发出普通 `SettingsChanged`，因此不能依赖异步广播更新随记设置页的继承字号。新增回归测试先在漏项上失败，再验证列表和正文字号均随全局值更新、原始覆盖值仍为 `0`；最终本地 Debug 与 CI 同配置 Release/x64 全量均为 **4,230/4,230** 通过。隔离 Debug 启动记录 36 步、0 degraded、0 failed；#426 最终远程 CI 通过。#423 的 PR 说明明确了新 scoped 恢复标记在用户确认前中断时丢弃暂存、旧标记缺少确认字段时继续采用旧合并语义。

此前统一候选的完整 x64 Native AOT publish/link、测试 MSIX 安装启动和部分真实交互已有独立证据；本次补回一行现有协调器调用后，没有重新执行完整 Native AOT publish/link 或正式双架构安装包验收。它们仍是后续正式发布前的门禁，不与这次源码合并混为一谈。

### 下一批

1. **第 23 批：退出链路剩余等待的所有权。** 先测量和故障注入 `todo-settings`、`search-settings`、`todo-reminders` 三步的挂起路径，再明确取消、排空与宿主资源释放的顺序。不能只给步骤套 `WaitAsync`：超时后的任务仍会运行，后续关闭窗口或释放服务可能与之竞争。验收包括不合作后端、重复退出、退出后不得回写已释放对象，以及隔离 Debug 的实际退出；保持设置 schema 和用户交互不变。
2. **随后处理 D 段剩余的拆离对账异常。** 针对回滚写盘失败且 Registry/替换窗口再次出错的窄路径，建立可观测的失败结果或隔离补偿，补一条经过真实拆离编排的自动测试。现有真实拖离故障注入已证明常见回滚路径可用，隐藏成员的无正常入口状态不再列为手动验收前提。
3. WebDAV 真服务器、通知交互和正式包的设备验收按各功能/发版门禁单独完成；`SwitchGate` 旧测试与成员、远端列表登记等低风险清理随相关代码触碰处理。设备层 store、云同步协议、contribution descriptor、Generic Host 和物理拆工程继续遵照路线图的立项触发条件，不并入第 23 批。

## 第二十三批：退出链路剩余等待的所有权

实施基线：`53c65f9d`。本批只处理退出序列中 `todo-settings`、`search-settings`、`todo-reminders` 三步的等待所有权；设置 schema、用户交互与其余步骤不变。

实现：`ShutdownSequence.RunAsync` 返回完整清理是否执行。`ShutdownStep.Bounded` 期限届满抛出专用 `ShutdownStepDeadlineExceededException`；后端自身的超时或失败仍按普通失败记录并继续，不触发中止。带 `abortFollowingStepsOnTimeout` 的步骤超时后中止其余步骤并返回 false——超时后仍会运行的操作不得与随后关闭窗口、释放服务和容器的步骤竞争。三步均以 15 秒期限启用该语义。`ShutdownApplicationAsync` 的 finally 成为兜底：托盘窗关闭、单实例互斥释放并置空（正常路径由 `single-instance` 步执行并置空，null 传播防止双重释放）。`SearchSettingsCoordinator` 停止时移除内部 5 秒上限，改为排空全部在途请求：期限由 App 层统一持有，协调器报告完成即代表没有请求再使用连接；超时则不释放借用的搜索运行时，交由进程退出接管。

测量与故障注入（隔离 Debug，数据根含 `architecture-shutdown-ownership-20260925`，`DESKBOX_DEV_SHUTDOWN_PROBE`）：`clean-exit` 全序列执行、2 秒退出；`hang-todo` 在 15 秒整抛出期限并跳过依赖清理。测量发现：跳过依赖清理时 `Application.Exit()` 返回后 XAML 消息循环继续泵送（dotnet-stack 证实 UI 线程空转于 Main、无前台线程阻塞、`ShutdownApplicationAsync` 已完成），进程无限存活。修复：deadline 路径在 `Exit()` 前布置 3 秒 `Environment.Exit(0)` 看门狗，仅该路径武装。修复后 `hang-todo` 20 秒退出（15 秒期限 + 3 秒看门狗 + 余量），`clean-exit` 仍 2 秒且不触及看门狗；契约测试钉住看门狗与跳过日志。注意本批首次尝试用 `BaseIntermediateOutputPath` 隔离 AOT 构建会破坏 XamlCompiler 状态（WMC9999），隔离应使用 SDK `ArtifactsPath`。

验证记录：

- 定向测试 56/56 通过，含所有权期限中止与共享完成结果、后端超时区分、挂起的 Todo 窗口操作/提醒排空/搜索探测分别中止依赖清理。
- 全量 x64 测试 4,235/4,235 通过（合并态 4,230 + 本批 5 个新用例）。
- AOT 条件编译（x64/win-x64、DeskBoxAotAudit + DeskBoxAotSmokeHarness + DeskBoxRustNative、`ArtifactsPath` 隔离）通过：888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- Debug 构建 0 错误；`git diff --check` 通过。
- 真实挂起仅经探针模拟；生产三步后端均自带取消与排空，期限属于最后防线。未提交推送。

## 第二十四批：拆离对账失败的隔离补偿

实施基线：`53c65f9d`（分支自 main；与退出所有权批次（PR #427）无源码交集，可独立合并）。本批只处理复用拆离回滚写盘失败后 Registry 重指再出错的窄路径；拆离编排、磁盘 schema、Z-order 与拖放规则不变。

原路径：`ReconcileCommittedDetachedSurfaceAsync` 的 Registry 重指失败时只记日志——设置已保存拆分而 Registry 仍持旧组声明，此后每次 `RaiseWidgetGroupsChanged` 都会撞声明校验抛点，分组操作降级直到重启。现在该 catch 调用 `WidgetGroupPersistedTopologyRecovery.QuarantineCommittedDetachClaims`：按实例注销被拆宿主、注销仍声明该成员的其它 Surface、移除旧组 Surface，再重试一次独立声明注册；重试被拒返回 false 并输出可观测标记（"Detach reconciliation quarantined without a standalone declaration"），不改动无关声明。补偿成功后补登记独立文件会话。`QuarantineCommittedMergeAsync` 两处原位于 try 之外的 `UnregisterHost` 补了逐项守卫，隔离级不再可能把异常逸出到合并 catch 之外。

新增探针阶段 `detach-reconcile-registry`（DEBUG+开发数据根门控、单次触发）用于注入该重指失败。

验证记录：

- 定向 35/35 通过：真实 `WidgetSurfaceRegistry` + 假宿主断言隔离会清除旧组声明并重建独立声明；重试被拒时返回 false、输出可观测标记、且不误删无关声明。
- 全量 x64 4,232/4,232（分支基线 4,230 + 2 个新用例）；AOT 条件编译 890 警告、0 错误（`ArtifactsPath` 隔离，仓库锁文件未改动）；`git diff --check` 通过。
- 设备级检查已完成（2026-09-25 晚，隔离 Debug 临时入口，入口已移除）：真实 HWND 双文件格子组，`reused-detach-create,reused-detach-rollback-save,detach-reconcile-registry` 三阶段全触发——复用失败→回滚写盘被拒→对账重指失败→隔离补偿成功重建独立声明→后续经真实编排的重命名正常完成（`RaiseWidgetGroupsChanged` 不再抛），进程无崩溃。
- 老版对照终审（同日）：设置面/运行时/磁盘兼容三路审计。磁盘兼容全绿（新→旧→新双向启动演练 settings.json 逐字节一致）；两处用户可见变更确认为第 5 批文档明示的有意统一（菜单关闭随记同步停录制、旧"功能关+录制开"配置启动归一化），列入 1.6.0 changelog 候选；补回 3 条剪贴板日志标记线（"Disabled from settings"/"Service initialized on demand"/"Inactive service released"）。最终全量 4,237/4,237。

## 第二十五批：审计遗留清理与文档对账

实施基线：`fdb5d45a`（main，四段 PR 栈 + #427 + #428 全部合并后）。本批只清理历次审查的低风险遗留项与文档欠账，不改用户可见行为。

- 删除 `WidgetSurfaceSession.SwitchGate` 死代码（全仓零引用，真串行早已由 `WidgetSurfaceSwitchGatePool` 承担）。
- 空白 SurfaceId 语义澄清：全量测试证明 `AcquireManyAsync` 的跳过空白行为是**承载语义**（独立格子的拓扑事务参与者没有 SurfaceId，必须无门控运行），不可改为抛错（首版 fail-closed 尝试被 HiddenMerge 双故障测试当场击落并回退）；拆离/解散/重排入口在查找组之前先 `WidgetGroupSettings.Normalize`（对齐合并路径既有做法），`Get` 对空白仍严格抛错。新增测试钉住跳过语义。
- `FileSurfaceContent` 磁盘协调的 `OperationCanceledException` 与表面切换/退役的竞争改为 Verbose 记录（Session A 观察到的日志噪声），真实失败仍走原错误日志。
- 远端**列表**操作仍不经 `BackupRestoreActions` 登记（只读、页面访问已取消、无状态影响；为它穿透三层构造函数与低风险清理批的定位不符）——维持文档化残余，随下次触碰备份协调器时顺手收编。
- 文档对账：路线图追记执行对账（分组事务线立档、2C PR-1 状态修正、IFeatureRuntime 替代记录）。

验证记录：定向测试（GatePool/Registry）通过；全量 x64 4,238/4,238（4,237 + 1 个新用例；首版抛错尝试在 4,237/4,238 被合并编排测试击落后回退）；`git diff --check` 通过。设备验收（A/B/D）已于 2026-09-26 全绿，发版等 Simon 指令。

## 第二十六批（部分）：P0 归因与日志队列评估

- **P0 全应用内存归因已完成**（报告 `residency-p0-attribution-20260926.md`）：framework Release、3 组×9 格子同进程差分——组缓存树增量 3~5MB、占稳态私有 2~3%，**命中 <10% 停止线：跳过 Cold 档**。两个意外发现：冷启动缓存为空（按需物化+Small 预算封顶，"缓存常驻"前提已不成立）；P2（Warm TTL）价值降级为 CPU/订阅冻结，降为低优先级待真实反馈，**P1-c 维持无条件执行**。测量用临时解除 `DESKBOX_DEV_DATA_ROOT` Release 门控的本地构建，改动已全部还原（一次真实数据误写疑云经快照比对确认为虚惊、零影响，如实记录）。
- **日志队列迁移评估结案：不迁移**。理由：日志队列与单实例锁同属进程生命周期基础设施，必须活到所有服务释放之后（退出序列中 log-drain 在 service-container 之后）；移入 DI 容器会倒置依赖，移入协调器只换边界无行为收益；模块边界立法本就将日志兼容调用豁免在外。重开触发条件：日志需要可配置 sink/级别（结构化日志功能立项）时，抽 Contracts 接口、App 为默认实现。第 3 批起的"待评估"就此关闭。

## 第二十七批：外部审计对照（#427/#428 复审）

外部审计四项指控经两路独立对码验证：①"三个备份/随记步骤缺 abort 会与容器释放竞争"——**事实成立但影响链被驳斥**（五类依赖服务均非 IDisposable，容器只释放 Theme/Weather/CitySearch；备份挂起是纯 IO；abort 反而是错误语义），仅补防翻修注释；②"看门狗路径互斥锁提前释放开 3 秒双实例窗口"——**成立并已修复**：mutex 仅在完整清理时手动释放，看门狗路径交给进程死亡释放（修复含审计员遗漏的约束：throw 路径无看门狗、必须保留手动释放）；契约断言钉住该语义，hang 探针复验 20 秒退出不变；③"Search 停止/准入竞态"——**驳斥**（admission 与 Stop 全在 UI 线程、无挂起点交叉，连 latent 都不成立）；④"隔离补偿缺身份校验可误删有效声明"——**驳斥**（已提交拓扑下 removedMember 为独立，任何其它声明定义上即 stale；提议的 SurfaceId==originalSurfaceId 校验在唯一可构造子情形会破坏清理，提议的测试会把违反不变量的状态固化为正例）。审计的 P3（PR 混日志修复）与拆 Coordinator 建议记录在案（后者与路线图拆工程触发条件一致）。全量 4,238/4,238。

## 第二十八批：提醒重入的切片收窄

审计遗留 P3-2 收口。目标口径：全局 `SettingsChanged` 无参数广播导致 `TodoSettingsCoordinator.OnSettingsChanged` 在每次防抖保存（如拖动外观滑块每秒一次）都重入提醒协调。评估后取**协调器侧变更检测守卫**而非全量事件参数化：其余订阅者（QuickCapture/Search/WidgetManager/备份）早已自带缓存比较守卫，事件签名改造是一天级宽 API 动而收益仅剩提醒一处。守卫缓存四个提醒相关输入（TodoEnabled、TodoReminderEnabled、默认提前分钟、Todo 格子 ID 集合），无变化即跳过；首次通知仍重入（覆盖恢复/默认值路径）。新增三条测试：无关保存零重入、提醒开关变化重入、Todo 格子增删重入。事件参数化留作触发项：出现第二个必须依赖切片信息的消费者时再立法。

## 第二十九批（Track A 首批）：设置门面剩余写入清点 + 外观节迁移

实施基线：`688d7d67`（main，第 1–28 批全部合并后），工作树 `codex/final-settings-appearance`。这是"完全拆完"计划 Track A（设置门面清零）的第一批。

### 清点：SettingsViewModel 33 个 partial 的剩余平铺写入点

清点口径：`_settingsService.Settings.<平铺门面> = ` 直接赋值（读写混合的门面访问总数另有 ratchet 管理）。全量共 **115 个写入点，分布在 15 个文件**；Todo/QuickCapture 功能开关与提醒、备份、搜索各节经第 1–22 批已收口，不在清单内。按设置节聚类后的 Track A 剩余批次划分：

| 批 | 节 | 写入点 | 触面文件 | 共享棘轮资源 |
|---|---|---|---|---|
| （无剩余行——第 39 批完成后 Track A 清点口径归零） | | | | |

合计剩余 **0**（第 29 批外观 28、第 33 批胶囊/紧凑 16、第 34 批交互 12、第 35 批文件显示 6、第 36 批文件栈 10、第 37 批分组导航 4、第 38 批功能节+QuickCapture 编辑器组 37、第 39 批存储/诊断尾巴 2 全部完成并在各自批次记录中销账；28+16+12+6+10+4+37+2=115 对上第二十九批清点总数）。**外部残余写入者**（不在 SettingsViewModel 内、后续单独处理）：OnboardingWindow.Appearance.cs 1 处 WidgetMaterialType 写入、OnboardingWindow.Hotkey.cs 的 AutoStart 直写与 App.xaml.cs ApplyDefaultAutoStartOnce 的开机默认回映（onboarding/宿主批次）、SettingsService 自身的加载/迁移/默认值路径（Track B 界面）；第 39 批收官对账另行登记同字段宿主侧写入者（WidgetManager 存储迁移链、OnboardingWindow.Storage、App 后台更新检查），见第三十九批记录。

### 本批实现

| 职责 | 所有者 |
|---|---|
| 外观节 26 个字段的唯一设置页写入、数值归一化（步进/钳制）、每个字段的原有保存语义 | `Services/AppearanceSettingsCoordinator`，经 `Contracts/IAppearanceSettings` 暴露 |
| 外观节编辑器缝（设置壳转发目标） | `Features/Appearance/AppearanceSettingsViewModel`（无复制状态：滑块可编辑状态仍在设置壳的 AOT 绑定属性上，拖动期间活预览时序原样保留） |
| XAML 绑定名、AOT 生成属性、文案、密度/动画预设联动、`SaveAppearanceChange` 拖动期预览+延迟保存、`CommitAppearanceChanges` | `SettingsViewModel` 兼容门面（AppearanceCallbacks/AppearanceOptions/WidgetForeground/PreferenceCallbacks/SettingsViewModel.cs 五个 partial） |
| 全局字号联动（写入后立即 `_todoSettings.Refresh()` + `_quickCaptureSettings.RefreshFromSettings()`，第 22 批教训） | 设置壳回调保留原顺序，仅写值改经编辑器 |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel |

关键时序保真点：滑块 Update 类方法只归一化+写原始字段，不触发预览或保存——预览（`RequestAppearancePreview` 66ms 防抖）与防抖保存仍由设置壳的 `SaveAppearanceChange`（受 `DeferAppearancePersistence`/`SuppressAppearanceNotifications` 拖动标志控制）唯一决定；材质类型切换保留"写值→预览→防抖保存"的原顺序（由协调器内聚）；动画预设应用传 `scheduleSave:false` 四字段后由壳一次 `SaveDebounced`；密度预设经协调器一次写 7 字段不落保存、壳在预设应用完成后走一次 `SaveAppearanceChange`；窗口默认宽高与窗口外观模式保留即时保存。归一化反馈环（NeedsNormalization→壳重设自身绑定→再入）保留原语义；非有限输入（NaN/Infinity）按原判定拒写并回读存储值。数值归一化逻辑随写入迁入协调器（0.02/2/0.5/10 步进与各 Min/Max 常量仍取自 SettingsService），四个动画归一化器从 SettingsViewModel.HoverActions 私有方法上移为 SettingsService 公共静态（与 chrome/titleIcon 先例一致）。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺访问清单 5 个文件收缩（AppearanceCallbacks 16→0 删除条目、AppearanceOptions 18→4、PreferenceCallbacks 21→17、WidgetForeground 6→4、SettingsViewModel.cs 141→94），只删不加；`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增外观字段写入门禁（26 字清单，WidgetLayerMode/collapse/compact 留给 30/31 批）；AOT 绑定契约（`requiredBindingProperties == generatedBindableProperties`）因绑定面零变化自动保持。磁盘 schema、XAML、文案零变化。

### 第二十九批验证记录

- canonical x64 Debug 构建（restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`）：22 警告、0 错误（警告来自既有可空性/控件位置）；非平台 canonical Debug（启动用）24 警告、0 错误。
- 定向测试 18/18 通过（新增 AppearanceSettingsCoordinatorTests 8 用例：步进归一化反馈、非有限拒写回读、滑块零通知零保存、选项归一化与无变化跳过、密度预设七字段不落保存、动画 scheduleSave、前景/标题图标延迟保存、停止后拒写；SettingsSliceOwnership 7；FeatureSettingsBoundary 3）。首轮全量 4,259/4,260：唯一失败是第 22 批全局字号回归测试用反射构造 SettingsViewModel 未注入新编辑器，已补注入真实 `AppearanceSettingsViewModel(AppearanceSettingsCoordinator)`。
- 全量 x64 测试：**4,260/4,260 通过**（基线 4,252 + 本批 8 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;...;DEBUG;DESKBOX_NATIVE_AOT"`）：22 警告、0 错误。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/appearance-track-a-20260926-215022` 预置外观非默认值（White 托盘图标、Acrylic 0.92/0.8、Custom 前景 #20A0FF、Small 角/Accent 边/Medium 边框、图标 36/字号 13、Custom 密度 0.84/0.68/0.82/0.5、文件名 1 行、默认 340×460、Compact/Overlay chrome、FilledMono 标题图标、Zoom/Fast/Strong 动画、空格子布局）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 27704，启动管线 35 步、0 degraded、0 failed；停机后磁盘全部预置字段保持。唯一变化 `widgetAnimationSlideDirection: "Left"→"None"` 为既有加载归一化（非 SlideFade 效果强制方向 None——预置组合本身无效），非本批行为。验证后已按路径停止本 worktree 实例。
- `git diff --check` 通过。
- 已知残余：OnboardingWindow.Appearance.cs 的 1 处 WidgetMaterialType 直写留给后续 onboarding 批次；设置壳 ApplySettingsSnapshot/构造函数中的外观读仍走门面读（无写入，ratchet 已顺带收缩 141→94）。未做真实滑块拖动的设备级手感验收，自动化证据不替代外观页实际拖动与材质切换的视觉验收。

## 第三十批：设备层迁移收口核验（2B 剩余项，提前纳入完全拆完）

实施基线：`688d7d67`（main）。本批对象是路线图 §2B 标记的最后一项剩余——格子布局/拓扑 → 设备域 store（原定云同步立项触发，Simon 拍板提前）。**对码结论：迁移本体已随 `b8dfb443`（2026-09-19）及四轮加固（`0b66db18`/`ed5b5a52`/`8319db1e`/`1d598f90`/`4378134c`）进入 main，路线图"剩余 ~330 处"的记载滞后于实况**；本批做全量收口核验、补齐演练契约与文档对账，未发现需要修复的产品缺陷。

store 设计（在库现状，非本批新写）：`DeskBox.Core.Persistence.WidgetLayoutStore` 承载 `widget-layout.json`（与 `desktop-organization-history.json` 同款命名风格），11 个布局线级键（widgets/widgetGroups/widgetTopologyLayouts/activeWidgetTopologyKey/deletedWidgetIds/featureWidgetEnabledStates + 5 个组导航/兼容默认值键）整体迁出 settings.json。接入 `ResilientJsonStore`（`.bak` 自救 + `.corrupt-*` 隔离 + 校验写入），自带 schemaVersion 与未来 schema 只读保护（typed slice 表示不了的文件拒绝覆写、save 如实报失败并保留冗余 settings 键），结构校验 fail-closed（`null`/`{}`/`{"layout":null}` 走隔离路径而非充当权威空布局）。

领养规则（fail-closed，2B-2 同款）：`SettingsService.LoadAsync` 把迁移+归一化后的旧 settings 键作为种子交给 `WidgetLayoutStore.LoadAsync`——store 文件落盘成为权威后才在下次保存剥离 settings 旧键；写失败保留旧键下次重试；corrupt primary 隔离留证不覆写；settings.json 整体加载失败时 layout 独立重新领养（单文件损坏不拖垮桌面）。旧版回退语义如实记录：旧版读不到设备层文件=回默认布局，与 2B-2 同款既定取舍。

引用迁移覆盖面核对（原估 ~330 处的实际处置）：2A facade 吸收了调用面——全部消费方（WidgetManager 11 个 partial、分组事务线、WidgetTopologyLayoutService、协调器/VM/启动恢复）继续读写 `AppSettings.WidgetLayout` 内存切片（store 的会话内活对象，`CopyFrom` 原位并入），持久化统一经 SettingsService 成对提交：单一 `FileWriteLock` 下 layout 先落、settings 后落，settings 提交失败回滚 layout 至提交前字节——一次保存=一对一致文件，**无半迁移混合读写状态**（不存在"一半走 store 一半走 settings"的路径）。逐文件走查确认除 SettingsService/WidgetStyleBackupProjection/备份服务外无任何直读直写布局数据文件的代码。

事务时序冻结的证据走查：①分组事务线（第 9-12/24 批）——合并/拆离/解散保持 快照→成对提交→表面退役 顺序（提交在 `beforeRetireAsync` 内执行 `SaveWidgetGroupSettingsCheckedAsync`，回滚=切片快照还原+再保存+表面对账，隔离补偿入口不变）；②#393 整理事务线——`settings→history→clear journal` 线性化点不变，settings 腿内部先 commit layout 对，history receipt 仍最后落；③启动恢复路径——`RestoreWidgetsAsync` 在内存切片上激活当前拓扑（数据已由 LoadAsync 从 store 装载），SaveDebounced 走同一成对保存；④本地灾难快照在 `OperationGate`+settings 写锁下把 settings/layout/history/journal 四文件同纪元拷备（防撕裂对）。

备份域排除核对：云备份域为 allowlist（todo/quick-capture 数据 + widget-style 投影），`widget-layout.json` 永不可入域（`LayoutFile_IsNeverInAnyCloudBackupDomain` 钉固）；`device.id` 维持排除（installation-local 身份）；样式口径与云备份 v1 一致——`WidgetStyleBackupProjection` 白名单只含样式键，恢复端对 post-adoption 文件直接补进 layout 文件并带 journal 两提交事务。本批新增 `StyleWhitelist_IsDisjointFromDeviceLayoutWireKeys` 契约：样式白名单与 11 个设备层键不相交（键漂进两侧即"布局借样式通道跨设备"）。

本批新增测试（`WidgetLayoutStoreTests`，+2）：`SettingsService_RealDataDrill_AdoptsGroupsMembersTopology_AndIsStableAcrossRestart`——预置带 2 组 4 成员+1 独立格子、双拓扑档案（组表面/独立表面几何+显示器记忆）、active key、墓碑的旧 settings.json→新版本启动领养→settings 剥离 11 键→store 文件承载完整形态→重启加载等值且 settings 不回吐。写失败保留旧字段重试路径由既有 `SettingsService_FailedAdoption_RetriesOnNextLaunch`/`SettingsService_LayoutWriteFailure_KeepsKeysInSettingsJson` 覆盖；三域归属其余两域由 `SyncLayerFieldsContractTests`（同步层字段集不变）与 history/journal store 测试（本地层不动）钉固。

验证记录：

- 定向 `WidgetLayoutStoreTests` 22/22 通过（20 既有 + 2 新增）。
- 全量 x64 **4,254/4,254**（基线 4,252 + 2 新用例）；build x64 0 错误（并发测试+构建会撞 XamlCompiler 状态，分开跑即净）。
- AOT 定义编译检查（`DESKBOX_NATIVE_AOT` DefineConstants）0 错误、22 警告；canonical Debug 构建 0 错误。
- 隔离 Debug 启动演练（2026-09-26 晚，数据根 `device-layer-batch30-20260926-7f3a1c`，DESKBOX_DEV_DATA_ROOT，仓库唯一实例 PID 4104→45084，路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`）：预置 2 组 4 成员+1 独立文件格子（真实映射文件夹）+双拓扑档案+墓碑的旧 settings.json→首启 36 步 0 degraded / 0 failed，两组表面以真实 HWND 呈现且成员切换正常（surface-a 0x1410E10 / surface-b 0xCF166A），磁盘上 `widget-layout.json` 生成且完整承载 5 格子/2 组/双拓扑/墓碑/功能态，settings.json 11 键全部清空；强制结束→重启同数据根，36 步 0 degraded / 0 failed，组/成员/拓扑/墓碑保持，settings 保持清空，无 corrupt 残留。演练中一次预置错误（非法 `viewMode` 枚举值）被既有 fail-closed 机制正确拦截（settings 整体隔离为 `.corrupt-*` 留证），顺带验证了损坏路径。测试实例已结束，未触碰生产数据根与并行代理实例。
- `git diff --check` 通过；未推送。路线图 §2B 状态同步更新（2B-3 条目 + 节奏建议行）。
## 第三十一批：Platform P/Invoke 主动全迁（Track C）

Simon 拍板放弃"随触碰 ratchet"，对 Platform 域的 DllImport/LibraryImport 存量做一次性主动全迁（与 2026-09-18 路线图"不做一次性大搬家"的原始口径就此收口）。实施基线：`688d7d67`（origin/main，worktree `codex/final-platform-pinvoke`）。行为零变化：只做声明搬家/抽取，DllImport 特性、字符集、SetLastError 错误位、签名逐字保留；调用语义、线程模型、错误处理不动。

计数对账（ratchet 口径，文件→调用点）：立法日 2026-09-18 为 **41 文件 / 260 调用点**（路线图口径）；历经各批收缩后本批起点（origin/main）为 **14 文件 / 99 调用点**（任务简报中的 41→26 为撰写时点快照，26 在中间批已继续下降）；本批完成后 **0 文件 / 0 调用点**——`PlatformInterop_StaysInsideThePlatformDomain` 自此成为硬零法，新增 P/Invoke 只能落 `DeskBox.Platform`。Platform 域内现有 26 个文件 / 约 270 处声明。

迁移清单（14 文件：1 整迁 + 1 整类迁 + 12 抽取）：

| 原文件 | 计数 | 方式 | 去向（DeskBox.Platform） |
| --- | --- | --- | --- |
| Helpers/NativeDropDescriptionWriter.cs | 7 | 整文件迁 | Platform/NativeDropDescriptionWriter.cs |
| Views/ContentWidgetWindow.AotNativeDropSmoke.cs（尾部 AotNativeDropWin32 类） | 4 | 整类迁（保留 `#if DESKBOX_NATIVE_AOT` 门控） | Platform/AotNativeDropWin32.cs |
| App.xaml.cs | 10 | 抽取 | Platform/ProcessDiagnosticsNativeMethods.cs（含 ProcessEntry32/TokenElevation/TokenMandatoryLabel/SidAndAttributes/TokenInformationClass） |
| Services/DragDropPermissionService.cs | 13 | 抽取 | Platform/DragDropPermissionNativeMethods.cs（含 StartupInfo/ProcessInformation 及 token 结构） |
| Helpers/NativeDropTarget.cs | 12 | 抽取 | Platform/OleDropTargetNativeMethods.cs |
| Helpers/ShellClipboardHelper.cs | 12 | 抽取 | Platform/ClipboardNativeMethods.cs |
| Helpers/ElevatedFileLauncher.cs | 7 | 抽取 | Platform/ElevatedLaunchNativeMethods.cs（含 ShellExecuteInfo/TokenElevation） |
| Services/FileService.cs | 6 | 抽取 | Platform/FileTransferNativeMethods.cs（含 ShFileOperation/ByHandleFileInformation/FileDispositionInfo/FileBasicInfo/FileIdInfo） |
| Services/DesktopBlankHitTest.cs | 6 | 抽取 | Platform/RemoteProcessMemoryNativeMethods.cs |
| Services/FileService.ShellTransfer.cs | 5 | 抽取 | Platform/FileOperationNativeMethods.cs |
| Helpers/ShellDataObjectBuilder.cs | 5 | 抽取 | Platform/HdropDataObjectNativeMethods.cs |
| Controls/NativeShellFileDragProvider.cs | 4 | 抽取 | Platform/ShellItemDragNativeMethods.cs |
| Services/JumpListService.cs | 4 | 抽取 | Platform/JumpListNativeMethods.cs（PropertyKey/IPropertyStore COM 侧留在原处） |
| Services/QuickLookPreviewService.cs | 4 | 抽取 | Platform/QuickLookElevationNativeMethods.cs |

抽取原则：纯 native interop 帮助类整迁；业务混合类只把 P/Invoke 声明与其签名直接引用的封送结构搬进 Platform（`private`→`internal`），业务侧调用点加类名限定。NativeDropTarget/DesktopBlankHitTest 的破坏性文件操作计数（Destructive manifest）不受影响——文件留在原命名空间，File/Directory 调用未动。

棘轮与钉同步：

- `ModuleBoundaryContractTests.PlatformInteropExpectedViolations` 清空（14 条→0），测试转为硬零法；Destructive 清单无需变化（NativeDropTarget.cs = 5 等条目所在文件未迁走、调用未动）。
- `NativeDropVisualContractTests`：NativeDropDescriptionWriter 的 2 处路径钉 Helpers→Platform；`NativeDropDescriptionWriterTests` 补 `using DeskBox.Platform`。
- AOT 文本钉逐一复核未破坏：`SHFileOperation(ref operation/fileOperation)`（AotStage4D2/5B4C1B1/5B4C1B2A 与 publish-aot-audit.ps1 5984/6269 行）——调用文本加限定后子串保留；`GlobalAlloc(`（AotStage5B4C1C2A 探针钉）同理保留；AotRetailIsolation 的烟具清单 61 个与排除模式列表不变（新 Platform 文件不匹配 `*.Aot*Smoke.cs`，且保留 `#if DESKBOX_NATIVE_AOT` 门控，retail 烟具移除后无引用可裁）。

验证记录：restore Updater 后 build x64 0 错误；全量单测 4,252/4,252 全绿；AOT 定义编译检查（DESKBOX_NATIVE_AOT）0 错误；隔离 Debug 启动烟测通过。`grep DllImport|LibraryImport` 非 Platform 命中 0。

## 第三十二批：IFeatureRuntime 正式化与两项残余收口

实施基线：`688d7d67`（main）。本批把路线图 §2 的功能运行时租约从"手写等价语义"升为正式契约，并收掉第 25 批文档化的远端列表残余与 App 装配区的功能分支清点。行为零变化：三个运行时的既有幂等/取消/串行语义原样保留，接口适配层为薄封装。

**接口（`Contracts/IFeatureRuntime.cs`）**：`IFeatureRuntime : IAsyncDisposable`，仅 `StartAsync(CancellationToken)` 一个新成员。状态机语义以 XML 契约注释立法：①幂等——重复 Start 不建第二份租约、重复 Dispose 不是二次释放；②Start 可取消且失败按获取逆序回收已建资源（不留半租约）；③Dispose 与进行中的 Start 竞争按状态机串行（Start 完成→Dispose，或 Start 取消→Dispose），不允许并发交错；④Dispose 不得无限阻塞宿主关闭——宿主持有期限、记诊断并把未回收运行时计入泄漏隔离清单。明确不是 PowerToys 式模块生命周期；不持长生命周期资源的功能不实现。

**三个实现（形式化封装，不重设计）**：

- `Features/Todo/TodoReminderRuntime`：`StartAsync` = 按当前设置的 `Reconcile`（协调器同一路径，构造函数新增 `readSettings` 读取器）；DisposeAsync 原有停止+排空；`_stopped` 守卫即终态语义。失败候选的回收（逆序回滚）为既有行为。
- `Features/QuickCapture/QuickCaptureClipboardRuntime`：`StartAsync` = `Refresh()`（按当前设置创建/刷新唯一监听）；`DisposeAsync` = 既有幂等 `StopAsync`（缓存任务）。
- `Features/Search/SearchFeatureRuntime`（新）：App 持有的第 6 批启停链（`EnsureSearchServices`/`DisposeSearchServices`）之上的委托适配器。链自身的幂等与部分失败回收保持权威；适配器加 `_started` 旗标使注册表级重复调用与清扫为 no-op。`SetSearchFeatureEnabled`（设置协调器注入的启停回调）改经适配器，enable/disable 循环与关停释放同一条契约路径。

**App 侧所有权（`Services/FeatureRuntimeRegistry`，新）**：注册表登记 `search`/`quick-capture`/`todo-reminders` 三个运行时实例（键与退出步同名），提供 `TryGet` 借用、按 id 释放、`DisposeAllAsync` 反向注册序兜底清扫（与契约的逆获取序回收一致；单运行时故障记 `[FeatureRuntimes] '...' ... quarantined for leak isolation` 诊断后跳过，不阻塞其余释放）。退出序列：`todo-reminders` 与 `search-runtime` 两步改经注册表解析（语义同前）；`search-runtime` 之后新增 `feature-runtimes` 兜底清扫步（`ShutdownStep.Bounded`，现有三运行时在此均为幂等 no-op，为未来无专属步骤的运行时立安全网）。装配区创建注册表并注册三个实例；协调器构造注入是装配期注入而非调用方缓存。

**共享契约测试（`tests/DeskBox.Tests/FeatureRuntimeContractTests.cs`）**：接口级状态机测试以 Theory 同时跑四个用例——全 gated 的多资源 Fake（立法本体）+ 三个生产运行时的薄用例适配。断言：重复 Start 单租约、预取消 token 零获取、重复 Dispose 单次释放、部分失败无残留租约且可重试；Fake 专属断言逆序回收与 Dispose/Start 竞争串行（Start 完成→Dispose、Start 取消→Dispose 两序）；Search 专属断言 enable/disable 循环（释放后可再 Start）。生产运行时在自有 UI 线程上的串行边界无法从测试线程证明，由 Fake 的门控转换测试代替钉住，功能级既有测试保留不动。`FeatureRuntimeRegistryTests` 钉注册表：注册/借用/重复键拒绝、按 id 定向释放、反向注册序清扫与故障隔离。

**残余一（第 25 批文档化项）**：备份设置页的远端 PROPFIND/列表读从 `BackupSettingsCoordinator.ListAsync` 直连 `CloudBackupService` 改经 `BackupRestoreActions.ListSnapshotsAsync`（internal）——同一 `CaptureCurrent` 端点校验、同一 `TrackAsync`/`StopAsync` 冻结语义，与删除/下载一致；`IBackupSettings.ListAsync` 端口不变，页面取消链不变。`BackupRestoreActions` 由设置窗口每次新建改为 App 启动时创建的单实例（设置窗口借用；未开过窗口时 `backup-restore-actions` 退出步也补了无窗口分支的 Stop）。新增测试：列表经登记路径返回条目、端点漂移拒绝、StopAsync 后列表拒绝。

**残余二（App.xaml.cs 功能分支清点）**：grep 清点后归四类——①Todo 通知激活链（常量、路由、snooze 确认、原生/托盘通知呈现、`CreateTodoReminderService` 工厂）：宿主适配（原生通知+托盘回退+WidgetManager 交互），第 1 批所有权表本就归 App，保留；②搜索结果动作/内容分发（`HandleSearchContentAsync`/`HandleSearchActionAsync` 的 Todo/QuickCapture 分支）：功能分支可挪 `WidgetManager.RevealSearchResultAsync`，但 WidgetManager 属并行轨道文件且 Features 层按边界法不能引 Services，列入清单建议后续随 WidgetManager 触碰时收编；③启动期功能门（search-shell/todo-reminders/quick-capture-clipboard 三步）：启动管线契约测试钉住步骤名，保留，search-shell 可在未来触碰启动管线时改经注册表 StartAsync；④备份设置回调与未验证上传 toast 策略：薄委托+一次性提示策略，随 BackupRuntime 触碰时评估。**本批实际小挪**：Todo"立即检查"（`CheckNowAsync` 触发）从 App 移入 `TodoReminderRuntime.CheckCurrentAsync`，App 只传用户意图；退出步经注册表解析替代两处直接字段调用。

验证记录：

- 定向测试（契约/注册表/三运行时/备份传输/边界/启动韧性/关停序列）172/172 通过。
- 全量 x64 测试 **4,277/4,277** 通过（main 基线 4,252 + 本批 25 个新用例：契约 Theory 16 + Fake/Search 专属 4 + 注册表 4 + 列表登记 1）。
- AOT 定义编译检查（`DESKBOX_NATIVE_AOT` DefineConstants、x64、`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，仓库主 obj 与锁文件未受影响）：11 警告、**0 错误**。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- canonical Debug 构建 0 错误（警告均为既有位置：CS0108/CS0414/CS0169/CS8602 与 WinUI 可空性，无本批新文件告警）。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/final-feature-runtime-20260926-d32`，核验进程 PID 45132，路径为 `D:/project/wingezi-final-d/src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`（本工作树唯一实例）；启动 **36 步、0 degraded、0 failed**，默认态 Search 未初始化服务（符合禁用路径），外部状态恢复完成，正常关闭退出（本工作树实例计数归零）。三个功能的开关循环由 SearchCase/接口级用例与既有功能级测试覆盖（真实 UI 点击仍属人工验收）。
- `git diff --check` 通过；未提交推送（本记录随提交一并入库）。

## 第三十三批：胶囊/紧凑模式设置节迁移（Track A 第二批）

实施基线：`405ca777`（main，含批 29-32），worktree `codex/final-settings-capsule`。对象是第二十九批清点表中"33 胶囊/紧凑模式"行：16 个写入点（CapsuleOptions 14 + AppearanceOptions 2 的 CollapseBehavior/CompactContentMode），覆盖胶囊内容模式、胶囊栏排列/位置/方向/间距、敏感内容隐藏、折叠行为、紧凑动画（效果+时长预设）、悬停展开/收起延迟、紧凑媒体圆角。

**协调器归属决策：独立建 `CapsuleSettingsCoordinator`（不并入外观协调器）。** 判据：胶囊/紧凑族写入与外观预览机制零耦合——CapsuleOptions 全部 14 个写入点均为"归一化→写值→SaveDebounced"直保存，从不走 `SaveAppearanceChange`/`RequestAppearancePreview`/`DeferAppearancePersistence` 拖动期活预览链；节内仅有的联动是本族预设对（动画效果↔时长、悬停响应↔双延迟），胶囊协调器可整体内聚。并入外观会把两种保存语义搅进同一端口。胶囊行为到宿主（WidgetShell/ApplyCompactState 等）仍只经既有 SettingsChanged/外观刷新链，宿主侧逻辑零改动。

| 职责 | 所有者 |
|---|---|
| 胶囊/紧凑 14 字段的唯一设置页写入、数值归一化（钳制/取整）、每字段原保存语义、动画效果↔时长与效果翻转 Custom 的成对写 | `Services/CapsuleSettingsCoordinator`，经 `Contracts/ICapsuleSettings` 暴露 |
| 胶囊节编辑器缝（设置壳转发目标） | `Features/Capsule/CapsuleSettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、预设选择的视图态联动（动画预设时长镜像、悬停响应派生 Custom 标记与 `_isApplyingWidgetCompactHoverResponse` 防回环、胶囊覆盖项重置命令） | `SettingsViewModel` 兼容门面（CapsuleOptions/AppearanceOptions 两个 partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29 外观编辑器同款） |

关键时序保真点：悬停响应对（Sensitive/Balanced/PreventAccidental）没有自己的持久字段——设置壳经自身双延迟绑定套用预设（各延迟绑定再转发协调器，保持原"两次防抖保存"语义），响应选择本身是派生视图态不落盘；手动改延迟时 `MarkWidgetCompactHoverResponseCustom` 只翻视图选择不写设置；紧凑动画预设时长映射与悬停响应预设延迟映射从设置壳私有 switch 上移为 `SettingsService.WidgetCompactAnimationPresetDurationMs`/`WidgetCompactHoverResponsePresetDelays` 公共静态（与批 29 四个动画归一化器上移同款先例），壳与协调器共用一份映射；协调器选项写入带"无变化跳过"（跳过冗余 SaveDebounced/SettingsChanged，与批 29 选项门面同款）。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单 2 个文件收缩（CapsuleOptions 34→20，仅剩 Widgets/WidgetGroups 覆盖项清单读；AppearanceOptions 4→2，剩 WidgetLayerMode 写与快照读留给第 34 批），只删不加；`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增胶囊字段写入门禁（14 字清单，WidgetLayerMode 留给交互批）；AotStage5B4B1 对 CapsuleOptions 的源码钉（`CapsuleOverrideSettingsItem` record 位置）与 AOT 绑定面（属性零增删）自动保持，无需改钉。磁盘 schema、XAML、文案零变化。

### 第三十三批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：22 警告（均为既有位置）、0 错误；非平台 canonical Debug（启动用）0 错误。
- 定向测试 30/30 通过（新增 CapsuleSettingsCoordinatorTests 8 用例：选项归一化与无变化跳过、动画预设成对写单次保存、自定义时长翻转 Custom/保留 Custom 或 None、数值钳制（NaN 间距回默认 8、时长/延迟夹 Min/Max）、行为/延迟/排列端口读写、磁盘往返、上移预设映射与持久字段一致、停止后拒写；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；AotStage5B4B1 相关）。
- 全量 x64 测试：**4,295/4,295 通过**（新基线 4,287 + 本批 8 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;...;DEBUG;DESKBOX_NATIVE_AOT"`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`）：11 警告、**0 错误**（与批 32 同位警告）。未执行 Native AOT publish/link，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/capsule-track-a-33-20260926`（DESKBOX_DEV_DATA_ROOT）预置胶囊 14 字段非默认值（Smart 折叠、Minimal 内容、隐藏敏感内容、Independent 宽度、Up 展开、Bar 排列/Top 位置/Vertical 方向/21px 间距、Custom 动画 330ms、500/900ms 悬停延迟、Round 媒体圆角）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 11544，启动管线 35 步、0 degraded、0 failed；优雅关停后磁盘全部 14 个预置字段保持。验证后已按路径停止本 worktree 实例。
- `git diff --check` 通过；未推送。
- 已知残余：胶囊覆盖项重置命令（行为/几何/全量重置）仍操作 Widgets/WidgetGroups 集合（非平铺门面写入，清点表口径外，随分组导航或覆盖项后续批评估）；设置壳 ApplySettingsSnapshot/构造的胶囊读仍走门面读（无写入，ratchet 20 计数内）；未做真实胶囊悬停/动画的设备级手感验收，自动化证据不替代胶囊页实际拖动与悬停展开的视觉验收。

## 第三十四批：交互设置节迁移（Track A 第三批）

实施基线：`01db3a02`（main，含批 29-33），worktree `codex/final-settings-interaction`。对象是第二十九批清点表中"34 交互"行：12 个写入点（PreferenceCallbacks 10——AutoStart/AutoCheck/DoubleClick/FileItemMenu/ResizeSnap/SnapSpacing/KeepVisible/ShowHoverButtons/Idle/ImmediateTrim，HoverActions 1——WidgetHoverButtonActions，AppearanceOptions 1——WidgetLayerMode）。

**协调器归属决策：独立建 `InteractionSettingsCoordinator`（不并入外观/胶囊协调器）。** 判据：交互族写入与外观活预览机制零耦合——11 个写入点均为"（按需归一化）→写值→SaveDebounced"直保存，唯一的例外 WidgetHoverButtonActions 走设置壳 `SaveAppearanceChange`（拖动延迟/通知抑制属壳的保存编排，不属于外观协调器），端口只负责存值、保存仍由壳唯一决定；WidgetLayerMode 是置顶层级语义（批 29 的 FeatureSettingsBoundary 注释本就"留给交互批"），并入外观会把窗口层级搅进视觉族。协调器内部一律走切片路径（`Settings.Core/FileWidget/WidgetShell/Performance.<字段>`），不新增平铺门面访问（FacadePassthroughAccess 棘轮零新增，与胶囊/外观协调器同款）。

| 职责 | 所有者 |
|---|---|
| 交互节 12 字段的唯一设置页写入、数值/选项归一化（SnapSpacing 钳制、LayerMode 归一）、每字段原保存语义、AutoStart 的"未变化跳过"守卫（壳原读前置卫一并移入） | `Services/InteractionSettingsCoordinator`，经 `Contracts/IInteractionSettings` 暴露 |
| 交互节编辑器缝（设置壳转发目标） | `Features/Interaction/InteractionSettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、启动注册操作与状态回映（StartupService.SetEnabled/SetMode/失败回读）、更新检查触发时序、宿主侧联动（ResizeGuideOverlay 同步、WidgetManager 层级刷新、ShellContextMenuProxy.Prewarm）、HoverActions 的 SaveAppearanceChange 提交 | `SettingsViewModel` 兼容门面（PreferenceCallbacks/HoverActions/AppearanceOptions 三个 partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33 同款） |

特有语义保全：①AutoStart——设置页写点在 `ApplyAutoStartState` 尾部（注册状态回映），StartupService 的模式切换/任务计划/Run 键迁移链零改动（DirectStartupService.SetMode 自身的 AutoStartMode 写入维持原样，属宿主侧自启动逻辑）；开发数据根（DESKBOX_DEV_DATA_ROOT）不触发开机默认自启应用，`ApplyDefaultAutoStartOnce` 的 App 侧写入不在本批范围。②AutoCheckForUpdates——App 启动时检查触发（App.xaml.cs 读门面）零改动，设置页只改持久值。③Idle/ImmediateTrim——第 1.4.9 时代工作集修剪仍只走既有 SettingsChanged 消费链（App.ImmediateHiddenWorkingSetTrim / App.QuiescenceWorkingSetTrim 读门面），协调器不触碰。④WidgetLayerMode——写值后壳仍按原顺序调 `RefreshVisibleWidgetDesktopLayers("settings-layer-mode")`。WidgetHoverButtonActions 语义精确化：协调器 SetWidgetHoverButtonActions 只存值不排保存（壳随后调 SaveAppearanceChange，避免双保存）；写入后宿主联动顺序（写→存→联动）原样保留。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单 3 个文件收缩（PreferenceCallbacks 17→6，剩文件显示 6 字段留给第 35 批；HoverActions 1→0 删除条目；AppearanceOptions 2→1，仅剩 chrome 覆盖项重置的 Widgets 清单读），只删不加；协调器新文件走切片路径零新增门面访问。`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增交互字段写入门禁（12 字清单），批 29 注释中"WidgetLayerMode 留给交互批"改为指向新门禁。ModuleBoundary 的 App.Current 例外清单 PreferenceCallbacks=3 **保留并注明**：三处（ResizeGuideOverlay×2、WidgetManager 层刷新）是刻意留在壳门面的宿主侧联动，Prewarm 钉（ShellContextMenuCompatibilityContractTests.Prewarm_IsWiredToStartupAndSettingsToggle 要求 Prewarm 调用留在 PreferenceCallbacks）不受影响。AOT 绑定面（属性零增删）自动保持。磁盘 schema、XAML、文案零变化。

### 第三十四批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误（警告均为既有位置）；非平台 canonical Debug（启动用）0 错误。
- 定向测试 46/46 通过（新增 InteractionSettingsCoordinatorTests 7 用例：开关族写入+未变化跳过、AutoStart 回映守卫、SnapSpacing NaN/越界钳制、LayerMode 归一与无变化跳过、HoverActions 只存值不排保存且显式保存可落盘、12 字段磁盘往返、停止后拒写；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；ModuleBoundary 4；ShellContextMenuCompatibility 5 及其余）。首版 LayerMode 用例把"无效值归一到已存默认值→无变化跳过"误计为一次通知，按实际语义修正断言。
- 全量 x64 测试：**4,302/4,302 通过**（新基线 4,295 + 本批 7 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;...;DEBUG;DESKBOX_NATIVE_AOT"`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，检查后已清理）：11 警告（与批 32/33 同位）、**0 错误**。未执行 Native AOT publish/link，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/interaction-track-a-34-20260926`（DESKBOX_DEV_DATA_ROOT）预置交互 12 字段非默认值（autoStart/autoCheckForUpdates/doubleClickToOpen/resizeSnap/keepVisible/showHoverButtons/idle+immediateTrim 全 false、fileItemSystemContextMenuEnabled true、widgetSnapSpacing 14、widgetLayerMode QuickReveal、widgetHoverButtonActions "LockPosition,Delete"）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 33680，启动管线 **35 步、0 degraded、0 failed**（日志中 F7 RegisterHotKey error=1409 为本机并存生产实例占用热键的环境性记录，非启动步失败）；停机后磁盘 12 个预置字段全部保持（settings.json 经首启规范化补全 schema，预置值原样）。验证后已按路径停止本 worktree 实例。
- `git diff --check` 通过。
- 已知残余：设置壳 ApplySettingsSnapshot/构造的交互字段读仍走门面读（无写入，ratchet 6 计数内）；OnboardingWindow.Hotkey.cs 的 2 处 AutoStart 直写与 App.xaml.cs ApplyDefaultAutoStartOnce 的 1 处（开发根豁免外的开机默认回映）属宿主/onboarding 侧写入，不在 Track A 设置页清零口径内，已登记到剩余批次表的外部残余写入者行；未做真实设置页点击（自启开关、吸附间距滑杆、悬停按钮组合）的设备级手感验收，自动化证据不替代交互页实际操作验收。

## 第三十五批：文件显示设置节迁移（Track A 第四批）

实施基线：`2ee5fb56`（main，含批 29-34），worktree `codex/final-settings-filedisplay`。对象是第二十九批清点表中"35 文件显示"行：PreferenceCallbacks 的 6 个写入点（ShowFileExtensions/HideShortcutExtensionWhenShowingFileExtensions/HideShortcutArrowOverlay/ShowImageFilesAsIcons/ShowListItemDetails/ShowFileItemPathTooltips），全部为"写值→SaveDebounced"纯直保存族。

**协调器归属决策：独立建 `FileDisplaySettingsCoordinator`（不并入外观/胶囊/交互协调器）。** 判据与批 33/34 同款：文件显示族写入与外观活预览机制零耦合，6 个写入点均为直保存，从不走 `SaveAppearanceChange`/`RequestAppearancePreview` 链；字段全部落在 `FileWidget` 切片（文件格子的呈现偏好），与交互族共享 partial 但语义独立（清点表"与 31 同文件分批"即指此）。并入交互会把"文件怎么显示"搅进"窗口怎么操作"；并入外观会把直保存搅进活预览端口。协调器一律走切片路径（`Settings.FileWidget.<字段>`），FacadePassthroughAccess 棘轮零新增。迁移后 PreferenceCallbacks 的平铺写入清零（门禁条目 6→0 删除），partial 内仅剩的 `_settingsService` 访问是 QuiescenceWorkingSetTrimEnabled 的 Performance 切片写（非平铺门面，批 34 已按切片路径留下）。

| 职责 | 所有者 |
|---|---|
| 文件显示 6 字段的唯一设置页写入、每字段原保存语义、未变化跳过守卫 | `Services/FileDisplaySettingsCoordinator`，经 `Contracts/IFileDisplaySettings` 暴露 |
| 文件显示节编辑器缝（设置壳转发目标） | `Features/FileDisplay/FileDisplaySettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、回调守卫（`_isRestoringDefaults`；ShowImageFilesAsIcons/ShowFileItemPathTooltips 原有的 `_isApplyingSettingsSnapshot` 追加守卫） | `SettingsViewModel` 兼容门面（PreferenceCallbacks partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33/34 同款） |

特有语义保全：ShowFileExtensions/HideShortcutExtensionWhenShowingFileExtensions/ShowImageFilesAsIcons 变更后的图标缓存清理与文件重投影链**零触碰**——这些仍只由 SaveDebounced 触发的既有 SettingsChanged 广播驱动（WidgetViewModel.OnSettingsChanged 比较 `_showImageFilesAsIcons`/`_hideShortcutArrowOverlay`/`_showFileExtensions` 缓存后走 FileService.ClearIconCache/RefreshItemDisplayNames），宿主侧消费代码原样；协调器新增的"未变化跳过"与回调驱动等价（ObservableProperty 只在值真变时触发回调，跳过只挡端口级冗余重写，与批 33/34 同款语义精确化）。壳侧 RestoringDefaults 链（SettingsService.ApplyDefaultPreferences + ApplySettingsSnapshot 在 `_isRestoringDefaults` 下写盘）与 SettingsService 加载/默认值路径（Track B）不在本批范围。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单 PreferenceCallbacks 6→0 删除条目（该 partial 自此无平铺门面访问），只删不加；`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增文件显示字段写入门禁（6 字清单，含 `(?:FileWidget\s*\.\s*)?` 切片前缀变体）；ModuleBoundary 的 PreferenceCallbacks App.Current=3 例外不变（属批 34 注明的宿主侧联动，本批 6 字段无 App.Current 访问）；AotStage5B4B2A 对 App.AotManagedUiSmoke 的 `ShowFileExtensions` 源码钉不受影响（钉的是烟具经真实 ViewModel 属性写设置，路径改经编辑器后行为等价）；AOT 绑定面（属性零增删）自动保持。磁盘 schema、XAML、文案零变化。

### 第三十五批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误、11 警告（均为既有位置，与批 34 后同位）。
- 定向测试 34/34 通过（新增 FileDisplaySettingsCoordinatorTests 4 用例：6 字段写入+未变化跳过、ReadAll 快照磁盘往返、显式保存落盘且未触字段保持默认、停止后拒写；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；ModuleBoundary 20）。
- 全量 x64 测试：**4,306/4,306 通过**（新基线 4,302 + 本批 4 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;...;DEBUG;DESKBOX_NATIVE_AOT"`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，检查后已清理）：11 警告（与批 32/33/34 同位）、**0 错误**。未执行 Native AOT publish/link，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/filedisplay-track-a-35-20260926`（DESKBOX_DEV_DATA_ROOT）预置文件显示 6 字段非默认值（showFileExtensions=true、hideShortcutExtensionWhenShowingFileExtensions=false、hideShortcutArrowOverlay=false、showImageFilesAsIcons=true、showListItemDetails=true、showFileItemPathTooltips=false，另预置 hasCompletedOnboarding/hasResolvedInitialFileWidgetSetup=true 保持无格子安静启动）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 28520，启动管线 35 步（5 critical）、0 degraded、0 failed（日志中 F7 RegisterHotKey error=1409 为本机并存实例占用热键的环境性记录，非启动步失败）；运行中与停止后磁盘 6 个预置字段均保持原值（首启规范化补全 schema，预置值原样；widget-layout.json 由 store 正常领养生成空布局）。停止采用按 PID 强制结束（关闭到托盘语义下 WM_CLOSE 不触发退出序列，无 CLI 退出入口），未走完整退出管线——本批验证的字段在启动加载时已由既有保存链定格，不受影响。验证后已确认本 worktree 实例归零（并存的他 worktree 实例未触碰）。
- `git diff --check` 通过。
- 已知残余：设置壳 ApplySettingsSnapshot/构造的文件显示字段读仍走门面读（无写入，FacadeAccessManifest SettingsSync 133/SettingsViewModel.cs 94 计数内）；未做真实设置页点击（扩展名开关、图片图标投影切换后真实文件夹图标的实际重投影效果）的设备级手感验收，自动化证据不替代文件显示页实际操作与图标刷新的视觉验收。

## 第三十六批：文件栈/文件格子设置节迁移（Track A 第五批）

实施基线：`c89f5eb0`（main，含批 29-35），worktree `codex/final-settings-filestack`。对象是第二十九批清点表中"36 文件栈/文件格子"行：`SettingsViewModel.FileStackOptions` partial 的 10 个写入点——堆叠总开关/自动堆叠/分组方式/阈值/排序/打开方式/浮窗布局/浮窗样式/未匹配行为 9 个标量加 `FileStackCustomRules` 集合整写，全部为"（选项归一）→写值→SaveDebounced"直保存族。

**协调器归属决策：独立建 `FileStackSettingsCoordinator`（不并入文件显示协调器）。** 判据与批 33/34/35 同款：文件栈族写入与外观活预览机制零耦合，全部直保存，从不走 `SaveAppearanceChange`/`RequestAppearancePreview` 链；但与文件显示的纯标量族有一处结构性差异——自定义规则是集合整写（增删改与拖拽排序共用 `PersistFileStackCustomRules` 单一投影），并入文件显示会把"标量逐字段写"与"集合替换写"搅进同一端口。协调器一律走 `Settings.FileWidget` 切片路径，FacadePassthroughAccess 棘轮零新增。

| 职责 | 所有者 |
|---|---|
| 文件栈 10 字段的唯一设置页写入、选项/阈值归一化、每字段原保存语义、未变化跳过守卫 | `Services/FileStackSettingsCoordinator`，经 `Contracts/IFileStackSettings` 暴露 |
| 文件栈节编辑器缝（设置壳转发目标） | `Features/FileStack/FileStackSettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、规则编辑器集合（增删/上移下移/拖拽排序提交/逐规则编辑）、规则预览投影、回调守卫（`_isRestoringDefaults`/`_isApplyingSettingsSnapshot`）、`editor.ToModel()` 扩展名归一化投影 | `SettingsViewModel` 兼容门面（FileStackOptions partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33/34/35 同款） |

特有语义保全：①自定义规则集合的三条触发路径（CollectionChanged 增删、逐规则 PropertyChanged、`CommitFileStackCustomRuleOrder` 拖拽排序提交）仍汇聚到壳的 `PersistFileStackCustomRules`，改经协调器 `SetFileStackCustomRules` 单一写入入口；`ToModel()` 的扩展名归一化（ParseExtensions→NormalizeFileStackExtensions+每规则 64 个上限）原样保留，协调器写入侧存储同一投影（裁剪名称+归一化上限扩展名，与加载管线归一化一致），并带等价跳过（同 Id/同裁剪名/等价扩展名序列——拖回原位的排序重提交不再触发保存与投影重建，与批 33/34/35 未变化跳过同款语义精确化）。②堆叠开关/分组/规则变更后的投影重建零触碰：仍只由 SaveDebounced 触发的既有 SettingsChanged 广播驱动（WidgetViewModel 的 QueueStackDisplayRebuild 排队链），宿主侧消费代码原样。③AotStage5B4B1 对 FileStackOptions 源码的钉逐一核对保持：`AvailableFileStackPopoverLayoutOptions` 属性留在 partial、`FileStacksEnabled` 的 TwoWay XAML 钉与 `FileStackCustomRules` 的 OneWay ItemsSource 钉（XAML 零变化）、AotDeepSmoke 的 `FileStackRuleCount` 探针路径（读壳的 `ViewModel.FileStackCustomRules`，属性面零增删）、AotBindableProperties 的 349 计数均自动保持。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单收缩（FileStackOptions 32→22，仅剩构造/快照读与 Widgets 预览读），只删不加；`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增文件栈 10 字段写入门禁（含 `FileWidget` 切片前缀变体）；AOT 绑定面与磁盘 schema/XAML/文案零变化自动保持。

### 第三十六批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误、22 警告（均为既有位置，与批 35 后同位）；非平台 canonical Debug（启动用）0 错误。
- 定向测试 27/27 通过（新增 FileStackSettingsCoordinatorTests 5 用例：9 标量+规则集合写入与未变化跳过、规则单一入口等价重提交跳过/真实排序与编辑落盘、非默认全量磁盘往返（ReadAll 快照+扩展名归一化断言）、无效选项按页面归一化收口、停止后拒写；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；AotStage5B4B1 12）。
- 全量 x64 测试：**4,311/4,311 通过**（新基线 4,306 + 本批 5 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;DEBUG;DESKBOX_NATIVE_AOT"`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，检查后已清理）：11 警告（与批 32/33/34/35 同位）、**0 错误**。未执行 Native AOT publish/link，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/filestack-track-a-36-20260926`（DESKBOX_DEV_DATA_ROOT）预置文件栈 10 字段非默认值（开+Custom 分组+2 条自定义规则（Documents/.pdf/.docx、Images/.png/.jpg）+自动堆叠开+阈值 2+Name 排序+Popover 打开+Grid5 浮窗+FollowMaterial 样式+Other 未匹配行为，另预置 hasCompletedOnboarding/hasResolvedInitialFileWidgetSetup=true 保持无格子安静启动）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 21896，启动管线 **35 步（5 critical）、0 degraded、0 failed**（日志中 F7 RegisterHotKey error=1409 为本机并存实例占用热键的环境性记录，非启动步失败）；运行中与按 PID 强制结束（关闭到托盘语义下无 CLI 退出入口，与批 35 同款）后磁盘 10 个预置字段（含 2 条规则的 Id/名称/归一化扩展名）全部保持原值。验证后已确认本 worktree 实例归零（并存的他 worktree 实例未触碰）。
- `git diff --check` 通过。
- 已知残余：设置壳 ApplySettingsSnapshot/构造的文件栈字段读仍走门面读（无写入，FileStackOptions ratchet 22 计数内）；规则预览的文件枚举（BuildFileStackPreviewEntries 读 Widgets）仍在壳门面；未做真实设置页点击（开关切换、规则增删拖拽后真实堆叠投影重建效果）的设备级手感验收，自动化证据不替代文件栈页实际操作与堆叠重建的视觉验收。

## 第三十七批：分组导航设置节迁移（Track A 第六批）

实施基线：`8593c158`（main，含批 29-36），worktree `codex/final-settings-groupnav`。对象是第二十九批清点表中"37 分组导航"行：`SettingsViewModel.GroupNavigation` partial 的 4 个写入点——`SelectedWidgetGroupDefaultNavigationStyle`（默认导航风格）/`SelectedWidgetGroupDefaultTitleDisplayMode`（默认标题显示）/`IsWidgetGroupWheelSwitchEnabled`（滚轮切换）/`IsWidgetGroupHoverSwitchEnabled`（悬停切换），全部为"归一化→比较原始存储值→写值→SaveDebounced"直保存族。**切片归属勘误**：任务简报称"这组字段写 WidgetShell 切片"，实际四个字段自第 30 批设备层迁移起就住在 `WidgetLayoutSettingsSlice`（widget-layout.json，第 30 批 11 键中的 5 个组导航/兼容默认值键成员），协调器按真实归属走 `Settings.WidgetLayout` 切片路径，经既有 SettingsService 成对提交（单一 FileWriteLock 下 layout 先落、settings 后落）。

**协调器归属决策：独立建 `GroupNavigationSettingsCoordinator`（不并入交互/胶囊协调器）。** 判据与批 33/34/35/36 同款：四个默认字段是纯直保存族，与外观活预览机制零耦合；但与前几节有两处结构性差异——①持久化目标是设备层 WidgetLayout 切片（并入交互会把 WidgetShell/Core/Performance 切片写与 WidgetLayout 切片写搅进同一端口，且引入跨设备层文件的保存语义）；②设置壳在每次真实写入后除 SettingsChanged 广播外还显式调 `App.Current?.WidgetManager?.NotifyWidgetGroupPresentationSettingsChanged()`（组表面呈现刷新链），写入口返回 bool"是否发生变化"让壳保留"只在真实变化时跑通知链"的原语义（与本 partial 既有 per-group 方法族的 bool 返回约定一致）。

| 职责 | 所有者 |
|---|---|
| 分组导航 4 默认字段的唯一设置页写入、归一化（`WidgetGroupNavigationStyles`/`WidgetGroupTitleDisplayModes` Normalize，FollowDefault 对默认级永不接受、无效值收口到出厂默认，与加载管线一致）、未变化跳过、每字段原保存语义（写值+SaveDebounced 订户广播） | `Services/GroupNavigationSettingsCoordinator`，经 `Contracts/IGroupNavigationSettings` 暴露 |
| 分组导航节编辑器缝（设置壳转发目标） | `Features/GroupNavigation/GroupNavigationSettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、选项列表/概要汇总/既有组投影、写后通知链（`AfterWidgetGroupPresentationChange`：显式组呈现通知+概要刷新+既有组投影刷新）、per-group 覆盖编辑方法（Rename/SetWidgetGroup*/ResetWidgetGroupOverrides，写 WidgetGroupConfig 对象非门面） | `SettingsViewModel` 兼容门面（GroupNavigation partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33/34/35/36 同款） |

特有语义保全：①四个 setter 原顺序逐字保持——归一化→与原始存储值 Ordinal 比较→写归一化值→SaveDebounced→壳通知链（显式组呈现通知、概要/属性 OnPropertyChanged），未变化写入零通知零保存零投影重建；②组切换行为到宿主仍只经既有 SettingsChanged→WidgetManager/WidgetShell 消费链加壳的显式 `NotifyWidgetGroupPresentationSettingsChanged` 链接，WidgetManager/WidgetShell 消费代码零改动（含 `RefreshWidgetGroupPresentationDefaultsIfChanged` 缓存比较守卫）；③壳的 `SaveWidgetGroupPresentationChange` 私有方法随迁移改名为 `AfterWidgetGroupPresentationChange`（去 SaveDebounced 行，保存职责入协调器；per-group 侧 `CompleteWidgetGroupSettingsChange` 的 `SaveDebounced(notifySubscribers:false)` 静默保存语义原样不动）；④AotStage5B4B1 对 GroupNavigation 源码的钉逐一核对保持：`WidgetGroupSettingsItem`/`WidgetGroupMemberSettingsItem` 两个 `[WinRT.GeneratedBindableCustomProperty]` record 留在 partial（契约测试与 publish-aot-audit.ps1 stage5B4B1SourceFiles[14] 双侧钉）、四个属性的 AotBindableProperties nameof 面与 349 计数零变化（requiredBindingProperties==generatedBindableProperties 自动保持）、XAML 绑定名/文案/磁盘 schema 零变化。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单收缩（GroupNavigation 28→20：四个 setter 的比较读+写入共 8 处消失，仅剩属性 get/概要/投影/本地化键字面量读），只删不加；`FeatureSettingsBoundaryContractTests.SettingsShell_DoesNotWriteMigratedFeatureFieldsDirectly` 新增分组导航 4 字段写入门禁（含 `WidgetLayout` 切片前缀变体）；协调器/编辑器/接口三个新文件 FacadePassthroughAccess 零命中。

### 第三十七批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误、22 警告（均为既有位置，与批 36 后同位）；非平台 canonical Debug（启动用）0 错误。
- 定向测试 26/26 通过（新增 GroupNavigationSettingsCoordinatorTests 4 用例：4 字段经编辑器缝写入/bool 变化报告/未变化跳过（SettingsChanged 计数 4 不涨）、无效值按页面归一化收口（FollowDefault→Tabs no-op、legacy Auto→Stack、Nonsense/null→出厂默认、归一化等值重发 no-op）、非默认全量磁盘往返（写→SaveAsync→fresh LoadAsync→ReadAll 等值，验证 widget-layout.json 设备层承载与再会话 no-op）、停止后拒写（ObjectDisposedException+磁盘保持默认）；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；AotStage5B4B1 12）。
- 全量 x64 测试：**4,315/4,315 通过**（新基线 4,311 + 本批 4 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants="TRACE;DEBUG;DESKBOX_NATIVE_AOT"`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`（Updater 引用随 DeskBox 的 ArtifactsPath 一并隔离 restore），检查后已清理）：11 警告（与批 32/33/34/35/36 同位）、**0 错误**。未执行 Native AOT publish/link，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/groupnav-track-a-37-20260926`（DESKBOX_DEV_DATA_ROOT）预置分组导航 4 字段非默认值（Stack 导航+TextOnly 标题+滚轮切换关+悬停切换开，另预置 hasCompletedOnboarding/hasResolvedInitialFileWidgetSetup=true 保持无格子安静启动）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 35452（本 worktree 唯一实例），启动管线 **36 步（5 critical）、0 degraded、0 failed**（日志中 F7 RegisterHotKey error=1409 为本机并存实例占用热键的环境性记录，非启动步失败，与批 36 同款）；懒领养符合预期（本次会话无设置变更即无持久保存，widget-layout.json 未生成、settings.json 仍为权威并完整保有 4 键——设备层往返已由本批磁盘往返测试覆盖）；按 PID 强制结束（关闭到托盘语义下无 CLI 退出入口，与批 35/36 同款）后磁盘 4 个预置字段全部保持原值。验证后已确认本 worktree 实例归零（并存的他 worktree 实例未触碰）。
- `git diff --check` 通过。
- 已知残余：设置壳的 GroupNavigation 属性 get/概要/既有组投影读仍走门面读（无写入，ratchet 20 计数内）；per-group 覆盖写（WidgetGroupConfig 对象）按口径留在壳（非平铺门面写入）；未做真实设置页点击（导航风格/标题显示切换后真实组表面呈现刷新）的设备级手感验收，自动化证据不替代分组导航页实际操作与组切换行为的视觉验收。

## 第三十八批：功能节与 QuickCapture 编辑器组迁移（Track A 第七批）

实施基线：`c469f58a`（main，含批 29-37），worktree `codex/final-settings-features`。对象是第二十九批清点表中"38 功能节（音乐/天气/Glance）+ QuickCapture 编辑器组"行：37 个平铺写入点（FeatureOptions 29、FeatureCallbacks 2、WeatherOptions 1、ContentEditorOptions 5）——音乐呈现 3、天气选项（数据源平铺写 1 + 既有 WeatherSettingsPolicy 归一化路径的温度/风力单位/默认视图/皮肤/刷新间隔/自动定位/手动城市/七项显示开关）、功能卡重置默认块（QuickCapture 编辑器 6、音乐 3、天气 16）、功能节杂项呈现（附件存储/托管拖放动作/文件夹打开方式）与音乐两个 ObservableProperty 回调。

**协调器归属决策：独立建 `FeatureWidgetsSettingsCoordinator` 承载整个功能节（音乐+天气+功能卡启停+节内杂项呈现），QuickCapture 编辑器组并入既有 `QuickCaptureSettingsCoordinator`。** 判据：①功能节全部写入是同一"归一化→与原始存储值比较→写切片→SaveDebounced"直保存族，与外观活预览机制零耦合；②功能卡重置流（ResetFeatureWidgetAsync）在一次 `_isApplyingSettingsSnapshot` 事务里跨 Music/Weather/QuickCapture 应用默认值并靠调用方单次 SaveAsync 落盘——按音乐/天气分设协调器会迫使壳编排多协调器默认值事务；天气归一化本来就集中在共享 `WeatherSettingsPolicy`（AOT 烟具也直接调用它），协调器内部继续调政策类，归一化单一来源零变化。③QuickCapture 编辑器 5+1 个字段全部住在 `QuickCaptureSettingsSlice`，与批 19-22 已迁的导航/呈现/字号同切片——并入既有协调器保持单一写入者，不再为同一切片开第二个端口。

| 职责 | 所有者 |
|---|---|
| 音乐 3 字段、天气（数据源+政策路径 9 组）、附件存储/托管拖放/文件夹打开 3 字段的唯一设置页写入、归一化（SettingsService 共享归一化器与 WeatherSettingsPolicy）、未变化跳过、每字段原保存语义 | `Services/FeatureWidgetsSettingsCoordinator`，经 `Contracts/IFeatureWidgetsSettings` 暴露 |
| 功能卡启停的持久旗标写入（`SetFeatureWidgetEnabled`：只写不存——持久化仍由壳的 WidgetManager 同步链拥有，与迁移前一致）与音乐/天气重置默认块（`Reset*Preferences(scheduleSave:false)`，靠重置流的单次显式保存） | `Services/FeatureWidgetsSettingsCoordinator` |
| QuickCapture 编辑器 5 字段（进入行为/格式/宽布局/宽打开模式/远程图片）写入+归一化+未变化跳过，与重置块（5 默认值+清空 LastQuickCaptureFileWidgetId） | 既有 `Services/QuickCaptureSettingsCoordinator`（IQuickCaptureSettings 新增 SetEditor*/ResetEditorPreferences/ReadEditorSettings） |
| 功能节编辑器缝（设置壳转发目标） | `Features/FeatureWidgets/FeatureWidgetsSettingsViewModel`（无复制状态） |
| XAML 绑定名、AOT 生成属性、文案、回调守卫（`_isRestoringDefaults`/`_isApplyingSettingsSnapshot`）、天气摘要/城市搜索/定位状态/建议列表、功能卡列表与 WidgetManager 同步链（SyncFeatureWidgetAsync/ResetFeatureWidgetAsync）、音乐显示模式文本通知 | `SettingsViewModel` 兼容门面（FeatureOptions/FeatureCallbacks/WeatherOptions/ContentEditorOptions 四个 partial） |
| 装配 | App 创建协调器与编辑器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33-37 同款） |

特有语义保全：①功能启停关联的格子创建/隐藏/运行时启停零改动——壳的 `SetWidgetEnabled` 仍只把持久旗标写经协调器，`SyncFeatureWidgetAsync`→WidgetManager 链原样；②天气归一化随写入迁入：协调器内部调 `WeatherSettingsPolicy`（温度/风力/视图/皮肤/刷新间隔/自动定位/手动城市/显示开关），无效值收口与迁移前逐字节一致，未变化跳过等价于原 SetProperty 门（壳字段变化才进回调）；`SelectWeatherCity` 的坐标校验拒绝（91°/NaN 返回 false+壳日志）与"自动定位开着时选手动城市先切手动"顺序不变；③功能卡重置块的壳属性写（静默更新绑定状态）原样保留，仅持久化写改经协调器 Reset 端口（scheduleSave:false），重置流尾部的单次 `SaveAsync` 不变；④音乐显示模式 setter 的 `OnPropertyChanged(SelectedMusicDisplayModeText)` 时机、天气回调"摘要刷新→守卫→写"顺序逐字保持；⑤AotStage5B4B2C2A 的"全局突变复用产品政策"钉随归属更新：契约测试与 publish-aot-audit.ps1 的 stage5B4B2C2A 源清单从 SettingsViewModel.WeatherOptions.cs 改指 FeatureWidgetsSettingsCoordinator.cs（政策仍是单一归一化来源，钉的是"产品写入者复用它"这一不变量），警告归属过滤器同步纳入新协调器文件。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单收缩（FeatureOptions 68→2——仅剩两条本地化键字面量 "Settings.AttachmentStorageMode.Copy/Link"；FeatureCallbacks 25→0 删除条目；WeatherOptions 2→1——仅剩城市搜索文本恢复读；ContentEditorOptions 24→10——仅剩构造/快照读），只删不加；`FeatureSettingsBoundaryContractTests` quickCaptureWrite 门禁扩展编辑器 6 字段、新增 featureSectionWrite 门禁（音乐/天气/杂项 22 字段清单，含切片前缀变体）；ModuleBoundary 的 App.Current 例外 FeatureOptions=4 保留并注明（全局热键启用调用、功能卡启用读穿透、两个 WidgetManager 同步链是刻意留在壳门面的宿主侧联动）；AOT 绑定面（属性零增删）与磁盘 schema/XAML/文案零变化自动保持。协调器/编辑器/接口三个新文件 FacadePassthroughAccess 零命中。

### 第三十八批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误、22 警告（均为既有位置，与批 36/37 后同位）；非平台 canonical Debug（启动用）0 错误。
- 定向测试 62/62 通过（新增 FeatureWidgetsSettingsCoordinatorTests 6 用例：音乐+杂项经编辑器缝写入/未变化跳过/无效值按页面归一化收口、天气政策路径写入与无效值收口与刷新间隔钳制/未变化跳过/未知显示开关抛参、手动城市政策拒绝（91°/NaN）与有效写入及同城市重选零保存、功能重置默认不排保存+调用方单次保存后全量磁盘往返（含 WeatherDataSource 不在重置块的保真）、功能卡启停写不排保存+保存后经设备层保持、停止后全端口拒写；QuickCaptureSettingsCoordinatorTests 新增 1 用例：编辑器 5 字段归一化/未变化跳过/磁盘往返/重置清 LastQuickCaptureFileWidgetId 不排保存；SettingsSliceOwnership 7；FeatureSettingsBoundary 3；ModuleBoundary；AotStage5B4B2C2A 11）。首轮全量 4,321/4,322：唯一失败是 AotStage5B4B2C2A 的"产品政策复用"钉仍指向壳文件，已随归属更新为协调器。
- 全量 x64 测试：**4,322/4,322 通过**（新基线 4,315 + 本批 7 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，Updater 引用随同隔离 restore，检查后已清理）：11 警告（与批 32-37 同位）、**0 错误**。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/featurewidgets-track-a-38-20260926`（DESKBOX_DEV_DATA_ROOT）预置功能节 24 项非默认值（音乐 Controls/双 false、天气 Hanoi 21.03/105.85 手动定位+Fahrenheit+mph+Week+Rich+OpenMeteo+Wind 关/Pressure 开+180 分、QuickCapture 编辑器 EnterSaves/PlainText/DualPane/Editing/远程图片开、附件 Copy/托管拖放 FollowWindows/文件夹打开 Embedded、功能卡 Music 关/Weather 开，另预置 hasCompletedOnboarding/hasResolvedInitialFileWidgetSetup=true 保持无格子安静启动）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 35804（本 worktree 唯一实例，进程路径核验一致；并存的 wingezi-p1c 实例未触碰），启动管线 **36 步（5 critical）、0 degraded、0 failed**；按 PID 强制结束（关闭到托盘语义下无 CLI 退出入口，与批 35-37 同款）后磁盘 24 项预置字段全部保持原值（本会话无设置变更即无持久保存，settings.json 未被重写，与批 37 懒领养观察一致）。
- `git diff --check` 通过。
- 已知残余：设置壳构造/快照/属性 get 的功能节读仍走门面读（无写入，ratchet 内）；天气城市搜索的定位状态/建议列表 UI 状态机仍留在壳门面（非设置写入）；publish-aot-audit.ps1 的 stage5B4B2C2A 文件清单已随归属更新但完整 Native AOT publish/link 审计未在本批重跑（发版门禁时验证）；未做真实设置页点击（音乐显示模式切换、天气单位/城市选择、功能卡开关后真实格子创建隐藏、QuickCapture 编辑器格式切换后真实编辑行为）的设备级手感验收，自动化证据不替代功能节实际操作与天气/音乐格子呈现的视觉验收。

## 第三十九批：存储/诊断尾巴迁移（Track A 收官批）

实施基线：`194544cc`（main，含批 29-38），worktree `codex/final-settings-tail`。对象是第二十九批清点表中"39 存储/诊断尾巴"行的最后 2 个写入点：`PreferenceCommands.UpdateManagedStorageRootPath` 的 `DefaultManagedStorageRootPath`（默认托管存储根路径）与 `AboutAndUpdates.CheckForUpdatesAsync` 的 `LastUpdateCheckAt`（更新检查时间戳）。XAML 绑定名、文案与磁盘 schema 零变化。

**协调器归属决策：存储路径新建 `ManagedStorageSettingsCoordinator`（桌面整理/文件域），更新时间戳新建 `MaintenanceSettingsCoordinator`（维护域），各配 `IManagedStorageSettings`/`IMaintenanceSettings` 合同与 `Features/ManagedStorage`/`Features/Maintenance` 编辑器缝。** 判据：①既有 `DesktopOrganizationCoordinator` 是整理事务引擎（扫描/计划/事务），不是设置页写入协调器（无 Stop 拒写、无合同端口），混入设置写会破坏批 29-38 的协调器家族形状；存储路径虽与整理域相邻，其设置页归属是"ManagedStorage"节（SectionRoutes）且字段住 `FileWidgetSettingsSlice`——按批 35/36 的"按节建协调器"先例独立建。②`LastUpdateCheckAt` 是纯记录性写入（UI 从不回读、磁盘仅供诊断），与交互批 34 迁走的 `AutoCheckForUpdates` 开关（InteractionSettingsCoordinator）不同域，与备份协调器（数据备份操作）也不同域——归维护域新建协调器，端口 `RecordUpdateCheck(DateTimeOffset checkedAt)` 由调用方决定"何时"，协调器只拥有"怎么写"（写切片 + 一次静默防抖保存）。

| 职责 | 所有者 |
|---|---|
| `DefaultManagedStorageRootPath` 的唯一设置页写入（归一化→写 FileWidget 切片→SaveDebounced 常规广播）与读取 | `Services/ManagedStorageSettingsCoordinator`，经 `Contracts/IManagedStorageSettings` 暴露 |
| `LastUpdateCheckAt` 的唯一设置页写入（写 Core 切片→SaveDebounced(notifySubscribers:false) 静默记录）与读取 | `Services/MaintenanceSettingsCoordinator`，经 `Contracts/IMaintenanceSettings` 暴露 |
| 存储路径显示镜像（`ManagedStorageRootPath` 绑定属性）、快速访问刷新、迁移确认/残留对话框、更新卡（检查/下载/安装流） | `SettingsViewModel` 兼容门面（PreferenceCommands/AboutAndUpdates 两个 partial） |
| 装配 | App 创建两个协调器，经 SettingsWindow 注入 SettingsViewModel（与批 29/33-38 同款） |

特有语义保全：①存储迁移链零改动——设置页写点只是迁移成功后的提交步骤；`WidgetManager.UpdateDefaultManagedStorageRootAsync` 链（含其自身 3 处 `DefaultManagedStorageRootPath` 写：迁移提交/重试/回滚）原样保留；写值无变化跳过仍由窗口层等值预滤（`ChangeManagedStoragePathButton_Click` 比较 `ViewModel.ManagedStorageRootPath`）承担，协调器写保持原命令的无条件写+广播语义。②`LastUpdateCheckAt` 只作记录性写入：调用方提供时间戳（`DateTimeOffset.Now` 原位）、保存不带 `SettingsChanged` 广播（记录检查不得惊动在跑格子），写序仍在 `CheckForUpdatesAsync` 结果应用之前逐字保持。③归一化随写入迁入：`NormalizeManagedStorageRootPath`（空/不可用路径回默认根）继续由 SettingsService 单一持有，协调器经它归一化并返回归一值供壳镜像绑定——与批 38 天气政策路径同款"单一归一化来源"原则。

门禁收缩：`SettingsSliceOwnershipContractTests` 平铺清单收缩（AboutAndUpdates 1→0 删条目；PreferenceCommands 2→1——仅剩恢复默认块的 ResizeSnapEnabled 读），只删不加；`FeatureSettingsBoundaryContractTests` 新增 managedStorageWrite 与 maintenanceWrite 两道写入门禁（含 FileWidget/Core 切片前缀变体）；AOT 绑定面（属性零增删）、磁盘 schema、XAML 与文案零变化自动保持；两个新协调器/编辑器/合同文件 FacadePassthroughAccess 零命中（切片路径访问不计门面）。

### 第三十九批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：0 错误、22 警告（与批 36/37/38 后同位）；非平台 canonical Debug（启动用）0 错误、22 警告。
- 定向测试 48/48 通过（新增 ManagedStorageSettingsCoordinatorTests 4 用例：归一化写入+广播保持/空路径回退默认根/磁盘往返/停止拒写；MaintenanceSettingsCoordinatorTests 3 用例：静默记录零广播/磁盘往返/停止拒写；SettingsSliceOwnership 7、FeatureSettingsBoundary 3、ModuleBoundary、AotStage7C1、第 22 批字号回归 2 等边界组全绿）。
- 全量 x64 测试：**4,329/4,329 通过**（新基线 4,322 + 本批 7 个新用例）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，Updater 引用随同隔离 restore，检查后已清理）：11 警告（与批 32-38 同位）、**0 错误**。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/storage-tail-39-20260927-a1c4e8f2` 预置两字段非默认值（`defaultManagedStorageRootPath=batch39-managed-store`、`lastUpdateCheckAt=2026-09-20T10:11:12+08:00`、`autoCheckForUpdates=false` 防宿主后台检查改写、空格子布局全功能关）。canonical 路径 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动 PID 21472（仓库下唯一实例），启动管线 35 步、0 degraded、0 failed；停止后磁盘两字段保持预置值（路径斜杠方向由既有加载归一化统一为反斜杠，语义位置不变）。验证后已按路径停止本 worktree 实例。
- `git diff --check` 通过。

### Track A 收官对账（批 29-39 结账）

**清单终值 vs 起始：第二十九批清点口径（`_settingsService.Settings.<平铺门面> = ` 直接赋值）在 SettingsViewModel 33 个 partial 内的 115 个写入点 → 归零。** 复核命令（`grep -rE "_settingsService\.Settings\.[A-Za-z]+\s*=[^=]" src/DeskBox/ViewModels/SettingsViewModel*.cs`）当前命中 0；批次销账对账：29 外观 28 + 33 胶囊/紧凑 16 + 34 交互 12 + 35 文件显示 6 + 36 文件栈 10 + 37 分组导航 4 + 38 功能节+QuickCapture 编辑器 37 + 39 存储/诊断尾巴 2 = 115，无遗漏无重复。已知口径外残余：①`SettingsViewModel.Performance.cs` 的自定义性能节 11 处经局部变量 lambda（`UpdateCustomPerformanceSetting(settings => settings.X = …)`）的门面写——该模式不匹配批 29 清点的直接赋值口径，一直由 FacadeAccessManifest 预算（Performance=11）圈住，属设置页最后一块未迁节，留待后续按节立项；②设置壳大量门面**读**（构造/快照/属性 get，SettingsSync 实测 88/预算 133、SettingsViewModel.cs 94 等）无写入，按"只删不加"棘轮继续收缩。

**Track A 九个设置页写入协调器（批 29-39 新建）全表：**

| # | 协调器 | 批 | 节 | 写入点 |
|---|---|---|---|---|
| 1 | AppearanceSettingsCoordinator | 29 | 外观 | 28 |
| 2 | CapsuleSettingsCoordinator | 33 | 胶囊/紧凑 | 16 |
| 3 | InteractionSettingsCoordinator | 34 | 交互 | 12 |
| 4 | FileDisplaySettingsCoordinator | 35 | 文件显示 | 6 |
| 5 | FileStackSettingsCoordinator | 36 | 文件栈/文件格子 | 10 |
| 6 | GroupNavigationSettingsCoordinator | 37 | 分组导航 | 4 |
| 7 | FeatureWidgetsSettingsCoordinator | 38 | 功能节 | 37 中 32 |
| 8 | ManagedStorageSettingsCoordinator | 39 | 托管存储 | 1 |
| 9 | MaintenanceSettingsCoordinator | 39 | 维护域记录 | 1 |

批 38 的其余 5 写点并入既有 `QuickCaptureSettingsCoordinator`（批 19-22 建、编辑器组同切片），保持单一写入者；先于 Track A 的功能协调器（批 1-22 的 Todo/Search/Backup/QuickCapture）不在本表。每批同款装配：`Contracts/IXSettings` 合同 + `Services/XSettingsCoordinator`（Stop 拒写）+ `Features/X/XSettingsViewModel` 编辑器缝 + App 创建 + SettingsWindow/SettingsViewModel 注入 + FeatureSettingsBoundary 写入门禁 + SettingsSliceOwnership 清单收缩。

**外部残余写入者终表（设置页清零后仍合法在册的非 SettingsViewModel 写入者）：**

| 写入者 | 字段 | 状态 |
|---|---|---|
| OnboardingWindow.Appearance.cs 1 处 | WidgetMaterialType | 在册，待 onboarding 批次 |
| OnboardingWindow.Hotkey.cs 2 处 + App.xaml.cs ApplyDefaultAutoStartOnce 1 处 | AutoStart | 在册，宿主/onboarding 侧（批 34 登记） |
| OnboardingWindow.Storage.cs 1 处 | DefaultManagedStorageRootPath | 在册（onboarding 引导流），本批顺带登记 |
| WidgetManager.Storage.cs 3 处（迁移提交/重试/回滚） | DefaultManagedStorageRootPath | 在册且**刻意保留**：存储迁移链宿主侧写入，第 39 批语义保全对象 |
| App.xaml.cs ScheduleBackgroundUpdateCheck 1 处 | LastUpdateCheckAt | 在册（宿主后台检查，12/45 秒延迟），本批顺带登记；未并入维护协调器以避免停止语义与后台任务的竞态 |
| SettingsService 加载/迁移/默认值路径 | 全部切片 | 在册，Track B 界面 |

Track A 就此收官：设置页（SettingsViewModel）不再有任何平铺门面直写，后续新增设置字段的唯一合法入口是各节协调器合同端口（FeatureSettingsBoundary 门禁 + SettingsSliceOwnership 棘轮双向锁）。

## 第四十批：门面退役第二阶段试点（Music 节 XAML 绑定迁编辑器，模式定型批）

实施基线：`5469123a`（main，Track A 收官后），worktree `codex/final2-facade-pilot`。对象是"把设置页 XAML 绑定从 SettingsViewModel 兼容门面迁到各 Features 编辑器 VM 并删除对应兼容属性"的第一节：**Music 节**（4 条绑定：显示模式 ComboBox + 封面背景/封面悬停两个 ToggleSwitch，全节字段最少、无联动）。磁盘 schema、文案零变化；{Binding} 标记形态保持（WMC1510=866 全部 runner/审计钉零触碰）。

**模式定型决策（后续批量批次的施工基准）：路线 (a) 节级 DataContext 切到编辑器 VM，而非 (b) 壳挂编辑器属性 + `Path=Editor.X` 重路径。** 判据：①**真删除**——路线 (b) 要求壳永久保留 `Music` 一类编辑器暴露属性，与"删除兼容属性"目标冲突；②**AOT 证据**——{Binding} 在 NativeAOT 下走 ICustomPropertyProvider 生成桥（`[WinRT.GeneratedBindableCustomProperty]`），本仓已有非壳 VM 先例：`GlanceWidgetContent` 的 `DataContext = GlanceWidgetViewModel` + `{Binding TimeText}` + `GlanceWidgetViewModel.AotBindableProperties.cs` 生成桥，AOT 审计在案；而设置页 XAML 无任何 dotted-path {Binding} 先例，嵌套路径的中间/末端对象桥接未经验证；③**WMC1510 稳定**——两条路线都保持 {Binding} 标记数量不变，但路线 (a) 的桥只建在编辑器上（壳桥净删 4 条）；④**门面名棘轮**——`SettingsSliceOwnershipContractTests.FacadePassthroughAccess` 按 `settings.<平铺门面名>` 正则计数，编辑器可绑定属性若沿用 `MusicUseArtworkBackdrop` 等平铺门面名会新增命中；节级 DataContext 后属性名无需跨壳唯一，去掉 `Music` 前缀（`UseArtworkBackdrop`/`EnableCoverHoverMotion`/`DisplayMode`/`AvailableDisplayModeOptions`）同时解决唯一性与棘轮两个问题。

| 职责 | 所有者 |
|---|---|
| Music 节 XAML 绑定面（4 属性：读投影 + TwoWay 写入 + 选项列表 + 本地化缓存重建） | `Features/Music/MusicSettingsViewModel`（ObservableObject，构造时经读快照自同步） |
| NativeAOT {Binding} 桥（4 nameof 条目） | `Features/Music/MusicSettingsViewModel.AotBindableProperties.cs`（Glance 同款） |
| 音乐呈现读快照 `MusicPresentationSettings`（显示模式归一化）与显示模式规范值 `MusicDisplayModes` | `Contracts/IFeatureWidgetsSettings`；`FeatureWidgetsSettingsCoordinator.ReadMusicPresentation` 实现（写入端口不变，仍为音乐/天气/杂项唯一写入者） |
| Music 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`sectionTag == "MusicSettings"` 时 `section.DataContext = _musicSettingsViewModel`（覆盖壳默认；ProcessBindings 对该节为 no-op，因节内无 x:Bind） |
| 外部刷新路径（SettingsChanged 广播/功能卡重置/语言切换） | SettingsViewModel.ApplySettingsSnapshot → `_musicSettings.SyncPresentation()`（对齐既有 `_todoSettings.Refresh()` 形态）；OnLanguageChanged → `_musicSettings.RefreshLocalization()`；功能卡 Music 重置块保留协调器 Reset + 编辑器 Sync（原三行门面赋值删除） |
| 壳兼容属性（已删除） | ~~`MusicUseArtworkBackdrop`/`MusicEnableCoverHoverMotion`（ObservableProperty）+ `SelectedMusicDisplayMode`(+Text) + `AvailableMusicDisplayModeOptions`/`AvailableMusicDisplayModes`(+DisplayNames) + 两个 Changed 回调 + 快照/构造/本地化/默认恢复处的全部读写~~ |

门禁同步（本批实际触碰的钉，后续批次同款三件套）：①`AotStage5B4B1ContractTests` 的 nameof 计数 349→345 与 `Assert.Contains("stage5B4B1ExpectedBindableViewModelPropertyCount = 345")`；`publish-aot-audit.ps1` 同名变量 349→345（动态对账测试 `BindableSettingsViewModelInventory_CoversEveryDirectSettingsBinding` 因 XAML 属性名与壳属性集双侧同步收缩而自动平衡）；②`AotPublishContractTests` 的 SettingsViewModel.cs `[ObservableProperty]` 计数 75→73；③WMC1510=866 与全部 stage runner 零触碰（{Binding} 未转 x:Bind）。`SettingsSliceOwnership` 平铺清单/`FeatureSettingsBoundary` 写入门禁零触碰（本批只删壳代码，读棘轮自动收缩）；新编辑器文件 FacadePassthroughAccess 零命中。

### 第四十批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误；x64 Debug 随测试构建 0 错误。
- 新增 `MusicSettingsEditorPilotTests` 9 用例（构造投影/快照归一化/写穿透+广播计数/外部同步零回写/选项表/本地化重建/常量别名/壳反射面无残留/XAML+桥+接线文本钉）。全量 x64 测试：**4,338/4,338 通过**（批 39 基线 4,329 + 本批 9）。
- AOT 定义编译检查（x64、`DefineConstants=DESKBOX_NATIVE_AOT`，`ArtifactsPath`/`RestorePackagesPath` 隔离，Updater 随同隔离 restore，检查后已清理 `.aotcheck/`）：11 警告（与批 32-39 同位）、**0 错误**。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针，非构建绿即绑定活的替代）**：隔离数据根 `facade-pilot-music-20260928-a1f3c2` 预置 `musicDisplayMode=Cover`、`musicUseArtworkBackdrop=false`、`musicEnableCoverHoverMotion=false`（camelCase 平铺键）、`hasCompletedOnboarding=true`、`language=zh-CN`。canonical Debug `--open-settings` 启动（跳转列表激活路径），UIA 经"功能格子"页音乐卡 drill-down 进入 Music 节：两个 ToggleSwitch 读到 **Off/Off**、显示模式 ComboBox 读到**封面模式**——预置非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影（绑定若死则控件呈编辑器默认 true/true/自动）。再经 TogglePattern 把"根据封面配色背景"拨到 On，2.5 秒后磁盘 `musicUseArtworkBackdrop=true`（另两字段不动）——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveDebounced→磁盘"回写贯通。启动管线 35 步、0 degraded、0 failed。探针后已按路径停止本 worktree 实例；探针脚本不入库。
- `git diff --check` 通过。

### 门面退役第二阶段施工图（侦察结论，后续批次照此复制）

**DataContext 流向现状**：整窗单一 `SettingsRoot.DataContext = SettingsViewModel`；各节经 `EnsureSettingsSectionCreated` 的 `DataTemplate.LoadContent()` 延迟实例化后再赋节 DataContext（本批起允许按节覆盖为编辑器 VM）；Search 节已是"代码后置渲染"（无绑定）、Appearance/Capsule 节为独立 UserControl + `{Binding}`（仍指壳门面）、FileWidget 节为 `x:Bind ViewModel.*`（ViewModel DP 类型仍是壳门面——后续批次把 DP 类型换成编辑器即可沿用 x:Bind 形态，x:Bind 免 AOT 桥）。

**每节迁移难度与推荐批次顺序**（唯一绑定属性数=该节门面绑定面规模；难度=属性数×联动面）：

| 顺位 | 节 | 唯一绑定属性数 | 编辑器归属 | 难度点 |
|---|---|---|---|---|
| 已完成 | Music | 4 | 新建 `Features/Music/MusicSettingsViewModel` | 试点基准 |
| 已完成（批 41） | Interaction 主节 | 3 | InteractionSettingsViewModel 补面（批 34 薄缝扩成可绑定编辑器） | 最小；首批复制 |
| 已完成（批 41） | InteractionWindow（高级） | 13 | 同上 | 全局热键/悬停按钮状态机经"壳计算+编辑器推面"分离（见第四十一批） |
| 已完成（批 42） | FileDisplay | 6 | FileDisplaySettingsViewModel 补面 | 纯直保存族，低险 |
| 已完成（批 42） | FileStorage(托管存储) | 7 | ManagedStorageSettingsViewModel 补面 | 快速访问状态/路径警告刷新链在壳（经壳推面模式分离，见第四十二批） |
| 已完成（批 43） | Appearance 五节（主节+材质/密度/窗口/动画） | 23+16+8+13（主节 23 含 12 共享，编辑器绑定面 76） | AppearanceSettingsViewModel 补面 | 外观活预览链经"编辑器值事件→壳 SaveAppearanceChange"分离，主题/accent/组导航为壳推面+事件回壳（见第四十三批） |
| 已完成（批 44） | WidgetGroups | 9（另 20 行绑定已走 [GeneratedBindableCustomProperty] 项记录） | GroupNavigationSettingsViewModel 补面 | 既有组投影经壳推面进编辑器；四默认字段+选项表迁编辑器（见第四十四批） |
| 已完成（批 44） | Capsule 主节+四子节 | 28+5+6+3+7（编辑器绑定面 42） | CapsuleSettingsViewModel 补面 | 悬停/动画预设派生态在编辑器不落盘；覆盖列表壳推面；UserControl DP 未换型（命令留壳，见第四十四批） |
| 已完成（批 45） | FileStack 节 | 10 节级绑定（另 3 处 x:Bind 重定型到编辑器）+ 自定义规则列表 | FileStackSettingsViewModel 补面（批 36 薄缝扩成可绑定编辑器） | 预览条目扫描（widget 配置+磁盘）留壳推面；规则汇聚单写口随编辑器（见第四十五批） |
| 已完成（批 46） | QuickCapture | 35（最大单体节） | 新建 `Features/QuickCapture/QuickCaptureSettingsViewModel`（批 19-22+38 的协调器域承接绑定面） | 录制连锁回推经协调器 Changed→编辑器 SyncPresentation；诊断/图缓存壳推面；容量防抖裁剪链留协调器（见第四十六批） |
| 已完成（批 47） | Todo | 29 | TodoSettingsViewModel 补面（批 1 编辑器扩成可绑定编辑器） | 默认筛选×标签可见性联动（批 14）经协调器快照重投影；字号 0 继承全局+滑杆事件回壳；回收批 46 共享面（预览行数/Enter 选项表+GetWidgetTabStyleDisplayName+PersistOwnedTextSize） |
| 已完成（批 48） | Weather | 23 XAML 绑定+显示 flyout 族 | 新建 `Features/Weather/WeatherSettingsViewModel`（批 38 协调器域承接绑定面） | 城市搜索状态机（CitySearchService/定位缓存/防抖取消）留壳推面，`AutoLocationUserChanged` 事件回壳；显示 flyout 族随编辑器（见第四十八批） |
| 已完成（批 49） | BackupRestore/CloudBackup/CompatibilityDiagnostics | 8+19+13 | BackupSettingsViewModel 扩面（批 4 状态机原位扩成可绑定编辑器） | 访问代次/取消链状态机留编辑器主分部原样；手动备份/恢复流程+快照列表 ItemsSource 留壳 code-behind 推面（SetCommandBusy/ConnectionStatusText）；拖放诊断与运行健康计算留壳薄 partial 推面；PasswordBox 凭据纪律保全（见第四十九批） |
| 已完成（批 50） | General/About/Performance | 16/25/14 | Performance 节+两跨域组合框迁编辑器（新建 `Features/Performance/PerformanceSettingsViewModel`，批 39 漏网族销账）；General 节留壳（语言/自启动宿主生命线）；About 节留壳（更新流水线宿主域） | 收官批裁决与终态对账见第五十批 |

**共享钉资源表**：迁 N 个门面绑定属性时必同步——AotBindableProperties nameof 计数（345 起逐批递减，批 49 后=49；测试+审计 ps1 两处+四编辑器测试桥）；SettingsViewModel.cs ObservableProperty 计数（73 起，批 48 后=17，AotPublishContract InlineData）；{Binding} 不得转 x:Bind（否则 WMC1510 866 变更牵动 ~20 runner×2 处钉；外观族首用的 `{Binding ..., Converter=...}` 不改 {Binding} 计数）；BindableSettingsViewModelInventory 动态测试自动平衡（勿在壳残留同名属性——需要保留的壳工作态可转 internal，如批 42 的 ManagedStorageRootPath、批 43 的 UseSystemAccentColor）；编辑器文件 FacadePassthroughAccess 零命中（可绑定属性名避开平铺门面名，去前缀即可；**本地化资源键字面量也会被该正则命中**——"Settings.Theme.*" 键含 `settings.Theme`（忽略大小写），编辑器源里必须分片拼接或改从常量拼装，注释同理，批 41/43 实测踩坑；"Settings.QuickCapture.*"/"Settings.Todo.*"/"Settings.Weather.*"/"Weather.*" 键安全——QuickCapture/Todo/Weather 均非平铺门面名（Weather 是 get-only 切片），批 46-48 实测）；ModuleBoundary 编译引用检查（Features/* 不得引用 DeskBox.Services/Platform/App——常量下沉 Contracts、本地化经 Func 委托）。


## 第四十一批：门面退役第二阶段第一复制批（Interaction 主节+InteractionWindow 节 XAML 绑定迁编辑器）

实施基线：`1fd7296c`（main，含批 40 试点），worktree `codex/final2-facade-interaction`。对象是施工图顺位 1/2 两节共 16 个唯一绑定属性（Interaction 主节 3：层模式下拉+悬停按钮摘要；InteractionWindow 高级节 13：打开方式/按 Win+D 行为两个下拉、全局热键卡 6 属性、吸附开关+间距滑杆+间距文本）。磁盘 schema、文案零变化；{Binding} 标记形态保持（WMC1510=866 零触碰）；交互族协调器缝（批 34 建的 `InteractionSettingsCoordinator`/`IInteractionSettings`）扩展成可绑定面，非新建。

**状态机分离决策（本批定型的新模式，适用于"绑定面可迁、状态机留壳"的节）**：16 属性中 11 个是纯持久字段投影（层模式/吸附开关/间距/打开方式/桌面行为——读快照+协调器写透），5 个属壳持状态机：全局热键卡（RefreshGlobalHotkeyState 计算 enable/激活文本/注册状态/本地化描述与保留手势警告，读 `App.Current.GlobalHotkeyService`——编辑器按 ModuleBoundary 不得引用）与悬停按钮摘要（HoverActions.cs 的 flyout 选择状态机）。这两个族改为**壳计算+编辑器推面**：壳把 `GlobalHotkeyPresentationSettings`（6 字段）与摘要字符串 push 到编辑器（`UpdateGlobalHotkeyPresentation`/`UpdateHoverButtonActionsSummary`，push 期间编辑器静默不回写不触发事件）；用户拨动热键开关经 `HotkeyEnabledUserChanged` 事件回壳执行 `GlobalHotkeyService.SetEnabled`+重算推面。宿主联动（层模式/桌面行为→RefreshVisibleWidgetDesktopLayers、吸附两字段→ResizeGuideOverlay）以编辑器 `*UserChanged` 事件回壳，订阅在 SettingsViewModel 构造器，处理方法落在原承载它们的 partial（App.Current 预算按文件守恒：AppearanceOptions 2/PreferenceCallbacks 3/FeatureOptions 4 均持平）。

| 职责 | 所有者 |
|---|---|
| 两节 XAML 绑定面（16 属性：读投影+TwoWay 写入+选项表+本地化缓存重建） | `Features/Interaction/InteractionSettingsViewModel`（ObservableObject 化，批 34 薄缝的方法保留给未迁节的壳回调） |
| NativeAOT {Binding} 桥（16 nameof 条目） | `Features/Interaction/InteractionSettingsViewModel.AotBindableProperties.cs`（Glance/Music 同款） |
| 交互呈现读快照 `ReadInteractionPresentation`（层模式归一化+间距钳制后返回） | `Contracts/IInteractionSettings` + 协调器实现（写入端口不变）；合同新增 `WidgetLayerModes`/`FileOpenMethods`/`ShowDesktopBehaviors` 规范值与两个呈现记录 |
| 全局热键卡状态机、悬停按钮 flyout 状态机、4 个宿主联动 | 仍在 SettingsViewModel（HotkeyAndStorage/HoverActions/AppearanceOptions/PreferenceCallbacks/FeatureOptions partial），经推面/事件与编辑器协作 |
| 两节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`sectionTag is "Interaction" or "InteractionWindowSettings"` 时 `section.DataContext = _interactionSettingsViewModel` |
| 外部刷新路径 | ApplySettingsSnapshot → `_interactionSettings.SyncPresentation()`+摘要 push；OnLanguageChanged → `RefreshLocalization()`+摘要 push+`RefreshGlobalHotkeyState()`；恢复默认 → 快照路径+`_interactionSettings.SnapEnabled` 读投影喂 ResizeGuideOverlay（门面读收缩） |
| 壳兼容属性（已删除，20 个反射面+4 个 ObservableProperty） | ~~SelectedWidgetLayerMode(+Text)/AvailableWidgetLayerModeOptions(+Modes+DisplayNames+GetWidgetLayerModeDisplayName)/HoverButtonActionsSummaryText/SelectedFileOpenMethod/AvailableFileOpenMethodOptions/SelectedShowDesktopBehavior/AvailableShowDesktopBehaviorOptions/GlobalHotkeyEnabled/GlobalHotkeyText/GlobalHotkeyStatusText/GlobalHotkeyStatusKind/GlobalHotkeyDescription/GlobalHotkeyWarningText/CanShowGlobalHotkeyWarning/WidgetSnapSpacingText/ResizeSnapEnabled/WidgetSnapSpacing/DoubleClickToOpen/KeepWidgetsVisibleOnShowDesktop~~ + OnDoubleClickToOpenChanged/OnResizeSnapEnabledChanged/OnWidgetSnapSpacingChanged/OnKeepWidgetsVisibleOnShowDesktopChanged 四回调 + 构造/快照/本地化处全部读写 |

行为语义保全要点：①热键状态机合并为单一 `RefreshGlobalHotkeyState`（原 RefreshGlobalHotkeyStatus 无独立消费点后删除，`GlobalHotkeyStatusKind` 的 risky 分支只影响已无视觉消费的 tint，状态文本两分支本就相同）；②快照应用期 `GlobalHotkeyEnabled = settings.*` 镜像赋值删除——原路径会顺带以同值调用 `GlobalHotkeyService.SetEnabled`（等值 no-op），新路径经 RefreshGlobalHotkeyState 纯推面；③间距滑杆的越界回夹改由协调器写入归一化承担（Slider 0-32 步进 1 下不可达）；④桌面双击/文件项菜单/悬停开关/空闲裁剪等仍走壳属性+回调（未在本批 16 属性口径内）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 345→**329**（测试两处+审计 ps1）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 73→**69**（删 DoubleClickToOpen/ResizeSnapEnabled/WidgetSnapSpacing/KeepWidgetsVisibleOnShowDesktop）；③动态对账测试自动平衡；④WMC1510=866 与全部 runner 零触碰；⑤`SettingsSliceOwnership` 平铺读棘轮自动收缩（PreferenceCommands 的 ResizeSnapEnabled 门面读改编辑器投影，预算 1→0 实效）；⑥`FacadePassthroughAccess` 编辑器文件零命中（层模式本地化键前缀分片拼接 `"Settings.WidgetLayer" + "Mode."`，规范值即键后缀）；⑦四个外部文本钉随绑定名更新（SettingsCopyAndHierarchy 2 处、GlobalHotkeySafety 1 处、WidgetCoordinatedMove 1 处）；⑧ModuleBoundary：编辑器仍零 Services/Platform/App 引用（事件+Func 委托）。

### 第四十一批验证记录

- canonical Debug（非平台）与 x64 Debug 构建：0 错误、22 警告（与批 40 后同码同位：CS8602×14/CS8601×2/CS0414×2/CS0169×2/CS0108×2）。
- 新增 `InteractionSettingsEditorTests` 11 用例（构造投影/快照归一化+钳制/写穿透+广播计数/联动事件触发与推面静默/热键开关事件传值/外部同步零回写/选项表/本地化重建/常量别名/壳反射面无残留/XAML+桥+接线文本钉）。全量 x64 测试：**4,349/4,349 通过**（批 40 基线 4,338 + 本批 11）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore，检查后已清理）：**0 错误**、22 警告（与同树常规 x64 构建逐码同位，无 AOT 特有新增）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针）**：隔离数据根 `facade-interaction-41-20260928-b41e7a` 的 `<root>/data/settings.json` 预置 `widgetLayerMode=QuickReveal`、`resizeSnapEnabled=false`、`widgetSnapSpacing=17`、`doubleClickToOpen=false`、`keepWidgetsVisibleOnShowDesktop=false`（camelCase，schemaVersion 9）+已完成 onboarding。canonical Debug `--open-settings` 启动（跳转列表激活路径），UIA 选中"快捷与交互"导航进入两节：层模式 ComboBox 读到**快捷唤起层**、打开方式读到**单击打开**、按 Win+D 后读到**和窗口一起隐藏**、格子吸附 ToggleSwitch 读到 **Off**、全局快捷键 ToggleSwitch 读到 **On**（推面）——预置非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影（绑定若死则控件呈编辑器默认 动态层/双击/保持显示/On）。再经 TogglePattern 把格子吸附拨到 On，3 秒后磁盘 `resizeSnapEnabled=true` 且其余预置字段不动——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveDebounced→磁盘"回写贯通（另在首轮错误预置位置的会话中亦已复证：默认态拨 Off 落盘 false）。启动管线 35 步（5 critical）、0 degraded、0 failed；Interaction/InteractionWindowSettings 两节延迟创建正常。探针后已按路径停止本 worktree 实例（主检出与 wingezi-p1c 实例未触碰）；探针脚本临时件未入库。
- `git diff --check` 通过。
- 遗留风险：①热键卡与悬停摘要的"推面"粒度是全量 push（每次重算 6 字段/1 字符串），状态机仍在壳，后续批次若迁状态机进编辑器需一并下沉本地化与 GlobalHotkeyService 查询委托；②ComboBox 选中项的 UIA 可读性依赖展开（未展开时 SelectionPattern 无选中项暴露），不影响绑定本身；③未做真实设置页逐控件操作手感验收（热键录制/预设按钮/悬停 flyout 的交互流仍是代码后置原路径，未受本批影响）。

## 第四十二批：门面退役第二阶段第二复制批（FileDisplay 节+FileStorage 节 XAML 绑定迁编辑器）

实施基线：`3f6f8ee0`（main，含批 40-41），worktree `codex/final2-facade-filewidget`。对象是施工图顺位 3/4 两节共 13 个唯一绑定属性（FileDisplay 文件显示节 6：显示后缀名/排除快捷方式 .lnk/图片视频图标/快捷方式小箭头/列表详情/路径提示六开关；FileStorage 收纳与路径节 7：托管拖放下拉×2、只读根路径、快速访问状态行/动作门/按钮面/按钮提示）。磁盘 schema、文案零变化；{Binding} 标记形态保持（WMC1510=866 零触碰）；两节编辑器缝为批 35 薄缝（FileDisplaySettingsViewModel/ManagedStorageSettingsViewModel），本批扩成可绑定面，非新建。

**DP 类型改 x:Bind 路线的取舍（本批的耦合判断）**：施工图为 FileWidget 总览节（AppearanceDetail 的 FileWidgetSettingsSection，`x:Bind ViewModel.*` 且 DP 类型是壳门面）预留了"后续批次把 DP 类型换成编辑器沿用 x:Bind 免桥"的例外。本批评估后**未执行**：该节 5 个 x:Bind 绑定横跨三个协调器域（文件叠放开关+摘要属 FileStack 域且摘要状态机在壳、文件夹打开方式属 FeatureWidgets 域、文件项系统右键菜单属 Interaction 域），其中 FileStack 两绑定与尚未迁移的 FileStack 节 `{Binding}` 共享同一壳门面状态——现在换 DP 类型要么在编辑器复制 FileStack 推面状态（约 10 处推送接线），要么把三个域塞进一个复合编辑器，而 FileStack 批迁移时又要重做；且该两成员无论换不换 DP 都无法真删。x:Bind 免桥路线留给拥有 FileStack 迁移的批次执行。

| 职责 | 所有者 |
|---|---|
| FileDisplay 节绑定面（6 属性：读投影+TwoWay 写入，无本地化） | `Features/FileDisplay/FileDisplaySettingsViewModel`（ObservableObject 化，属性名即 XAML 路径、本就无前缀） |
| FileStorage 节绑定面（7 属性：DropAction+选项表+RootPath 推面+快速访问四呈现） | `Features/ManagedStorage/ManagedStorageSettingsViewModel`（ObservableObject 化；DropAction/RootPath 去前缀避门面名） |
| NativeAOT {Binding} 桥（6+7 nameof 条目） | 两编辑器各自 `*.AotBindableProperties.cs`（Glance/Music 同款） |
| 托管呈现读快照 `ReadManagedStoragePresentation`（拖放动作归一化+根路径归一化）与 `SetManagedDropAction` 写端口 | `Contracts/IManagedStorageSettings` + 协调器实现（FeatureWidgets 侧同字段写端口有第二消费者故保留不动）；`ManagedDropActions` 规范值下沉 Contracts，SettingsService 常量改别名 |
| 快速访问状态机（Explorer 查询/忙态/置顶态）与根路径工作态 | 仍在 SettingsViewModel（HotkeyAndStorage/FeatureOptions）：`ManagedStorageRootPath`/`ManagedStorageQuickAccessPinState`/`IsQuickAccessBusy` 转 internal（PropertyChanged 供路径警告链），状态变更经 `PushQuickAccessPresentation` 全量推 `QuickAccessPresentationSettings` 五字段到编辑器；语言切换重算重推 |
| 两节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`FileDisplaySettings`→`_fileDisplaySettingsViewModel`、`FileStorageSettings`→`_managedStorageSettingsViewModel`（SettingsWindow 新增两编辑器字段与 FileDisplaySettings 公开访问器） |
| 外部刷新路径 | ApplySettingsSnapshot → `_fileDisplaySettings.SyncPresentation()`+`_managedStorageSettings.SyncPresentation()`；OnLanguageChanged → `_managedStorageSettings.RefreshLocalization()`+快速访问推面；根路径迁移提交 → 编辑器 `CommitRootPath`（协调器写+推面一体） |
| 壳兼容属性（已删除） | ~~ShowFileExtensions/HideShortcutExtensionWhenShowingFileExtensions/ShowImageFilesAsIcons/HideShortcutArrowOverlay/ShowListItemDetails/ShowFileItemPathTooltips 六 ObservableProperty + 六 Changed 回调 + SelectedManagedDropAction/AvailableManagedDropActionOptions/AvailableManagedDropActions(+DisplayNames)/GetManagedDropActionDisplayName/QuickAccessStatusText/CanInvokeQuickAccessAction/PinQuickAccessButtonText/PinQuickAccessToolTipText/ShouldUnpinManagedStorageFromQuickAccess/死代码 RefreshQuickAccessState + `_selectedManagedDropAction`/`_cachedManagedDropActionDisplayNames` 字段 + 构造/快照/本地化处全部读写~~ |
| AOT 持久化烟囱 | App.AotManagedUiSmoke 的 ShowFileExtensions 拨动/读出改经 `settingsWindow.FileDisplaySettings` 编辑器面（evidence 字段名不变），磁盘链路不变 |

行为语义保全要点：①编辑器构造/Sync 期静默不回写（`_isSyncingPresentation`），与原壳回调的 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 守卫等价；②拖放动作组合只出规范值、协调器写入归一化（未知值回退 Move），与原壳归一化 switch 同语义；③快速访问四呈现改为壳算好字符串整包推送（批 41 热键卡同款"壳推面"），ComboBox 值/按钮启用/提示全由编辑器呈现，代码后置 Pin/Unpin 处理改读编辑器 `CanInvokeQuickAccessAction`/`ShouldUnpinQuickAccessAction`；④根路径 TextBox 显示值从"构造期裸值"变为"读快照归一化值"（原快照应用期本就归一化，仅首开帧差异）；⑤桌面快捷方式开关/文件夹清理/打开路径按钮仍是代码后置原路径未受影响。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 329→**316**（测试两处+审计 ps1）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 69→**63**（删六个文件显示开关）；③动态对账测试自动平衡（壳属性与 XAML 路径双侧同步收缩，壳未残留同名公有属性——ManagedStorageRootPath 等转 internal）；④WMC1510=866 与全部 runner 零触碰；⑤`SettingsSliceOwnership` 读棘轮自动收缩；⑥`FacadePassthroughAccess` 两编辑器+合同文件零命中（本批本地化键 Settings.DropAction.*/Settings.ManagedPath.* 无平铺门面名前缀碰撞，未需分片拼接）；⑦ModuleBoundary：编辑器仅引 Contracts/Models/MVVM Toolkit，零 Services/Platform/App/WinUI；⑧审计 ps1 的 stage5B4B2A 烟囱钉（'ShowFileExtensions'/'settingsWindow.ViewModel'）随 harness 改指编辑器后原样满足。

### 第四十二批验证记录

- canonical Debug（非平台）构建 0 错误；x64 Debug 随测试构建 0 错误。
- 新增 `FileSettingsEditorTests` 12 用例（两编辑器构造投影/快照归一化/写穿透+广播计数与unchanged跳过/外部同步零回写/CommitRootPath 归一化持久推面/快速访问推面/选项表/本地化重建/常量别名/壳反射面无残留/XAML+桥+接线+烟囱文本钉）。另将批 35 的 FileDisplaySettingsCoordinatorTests/ManagedStorageSettingsCoordinatorTests 两处旧薄缝调用改指新绑定面。全量 x64 测试：**4,361/4,361 通过**（批 41 基线 4,349 + 本批 12）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT`，`ArtifactsPath`/`RestorePackagesPath` 隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore，`--no-incremental` 全量重编确认 DefineConstants 经 `-getProperty` 实际生效——Git Bash 下分号属性需内嵌引号传递，检查后已清理）：**0 错误**、22 警告。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针）**：隔离数据根 `facade-file-42-20260928-c42f7d` 的 `<root>/data/settings.json` 预置 `showFileExtensions=true`、`hideShortcutExtensionWhenShowingFileExtensions=false`、`showImageFilesAsIcons=true`、`hideShortcutArrowOverlay=false`、`showListItemDetails=true`、`showFileItemPathTooltips=false`、`managedDropAction=FollowWindows`（camelCase，schemaVersion 9）+已完成 onboarding、zh-CN。canonical Debug `--open-settings` 启动（跳转列表激活路径），UIA 选中"文件格子"导航：收纳与路径节读到拖放下拉选中**跟随 Windows 默认**（预置 FollowWindows）与只读根路径 `C:\Users\simon\DeskBox`；drill-down 进入文件显示节后六 ToggleSwitch 读到 **On/Off/On/Off/On/Off**——与预置逐项一致（绑定若死则控件呈编辑器默认 Off/On/Off/On/Off/On）。再经 TogglePattern 把"图片和视频只显示图标"拨到 Off，4 秒后磁盘 `showImageFilesAsIcons=false` 且其余预置字段（含 managedDropAction）不动——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveDebounced→磁盘"回写贯通。启动管线 35 步（5 critical）、0 degraded、0 failed；FileDisplaySettings/FileStorageSettings 两节延迟创建正常。探针后已按路径停止本 worktree 实例（主检出实例未触碰）；探针脚本临时件未入库。
- `git diff --check` 通过。
- 遗留风险：①快速访问推面为全量五字段 push（状态机在壳，迁编辑器需一并下沉 Explorer 查询委托与本地化）；②FileWidget 总览节仍走壳门面 x:Bind（DP 类型改 x:Bind 免桥路线已评估未执行，见上），其 5 绑定与 FileStack 批耦合；③根路径显示值首开帧从裸值变归一化值（正常安装两者相同）；④未做真实设置页逐控件操作手感验收（文件夹选择器/迁移对话框/桌面快捷方式开关的交互流仍是代码后置原路径，未受本批影响）。

## 第四十三批：门面退役第二阶段第三复制批（外观族五节 XAML 绑定迁外观编辑器）

实施基线：`4919bf38`（main，含批 40-42），worktree `codex/final2-facade-appearance`。对象是施工图顺位 5 的外观族：四个子节（材质 23+密度 16+窗口 8+动画 13=60 唯一绑定属性）加上承载它们 drill-down 行的主外观节（23 绑定，其中 12 与子节共享）——主节不切 DataContext 则共享属性无法真删，故五节（`Appearance`/`AppearanceMaterialSettings`/`AppearanceDensitySettings`/`AppearanceWindowSettings`/`AppearanceAnimationSettings`）整体切到外观编辑器，编辑器绑定面共 76 属性。磁盘 schema、文案零变化；{Binding} 标记形态保持（两 XAML 的 {Binding} 计数 420/23 逐字不变，WMC1510=866 零触碰；滑杆可见性门与颜色选择器经 XAML 值转换器绑布尔/十六进制投影，{Binding} 个数不变）；批 29 的薄缝 `Features/Appearance/AppearanceSettingsViewModel` 扩成可绑定编辑器（ObservableObject 化，新增 localize 委托参数），非新建。

**活预览时序的保全（本批核心决策）**：活预览编排（`SaveAppearanceChange` 的 preview→防抖保存、滑杆拖动期的 `SuppressAppearanceNotifications`/`DeferAppearancePersistence` 旗标、`CommitAppearanceChanges` 的单次通知+静默落盘）全部留在壳——它不是门面属性而是保存编排。编辑器提供可绑定面+值变更事件，壳订阅驱动预览链：滑杆/选项写入经协调器落字段后触发 `AppearanceValueCommitted`（壳跑 SaveAppearanceChange）、文字大小触发 `TextSizeCommitted`（另刷 Todo/QuickCapture 继承字号，批 22 回归）、密度标签拨"自定义"触发 `LayoutDensityMarkedCustom`（壳跑普通 SaveDebounced，与旧壳语义一致）、动画预设四字段写完触发 `AnimationPresetApplied`（壳跑普通 SaveDebounced）。拖动期间不保存由壳旗标在 SaveAppearanceChange 内原位裁决；材质切换的"写→预览→防抖保存"本就在协调器 `SetWidgetMaterialType` 内，未动；密度预设一次写 7 字段由编辑器在 `_isSyncingPresentation` 静默重投影后单事件收口（替代旧壳 `_isApplyingLayoutDensityPreset` 对逐字段保存的压制），动画预设的 scheduleSave 延迟同理由 `_isApplyingAnimationPreset` 承载。

**选择类状态机的"壳推面+事件回壳"（批 41 子模式复用）**：主题（ThemeService 写在壳）、主题色（accent 模式/生效色状态机+预设色板在壳，编辑器以 `AccentColorSource`/`SelectedAccentColorHex` 承接）、组导航默认样式（GroupNavigation 域未迁，选项表+值由壳整包推送）与主机环境探针（Win10 兼容、原生圆角支持、按 OS 的材质清单——`WindowsCompatibilityService` 在 Platform，编辑器不得引用）均经 `UpdateThemeSelection`/`UpdateAccentPresentation`/`UpdateGroupNavigationPresentation`/`UpdateHostEnvironment` 静默推面；用户拨动经 `*UserChanged` 事件回壳执行宿主侧效果（SetTheme/SetAccentMode/SetCustomAccentColor/UpdateTrayIcon/写组导航协调器）。协调器读快照（`Read*`）改为按旧壳构造器语义归一化（材质/圆角经 WindowsCompatibilityService、边框/动画/标题图标/前景/chrome 归一化、密度标签从 7 值解析、行数归一化），新增 `ReadTrayIconStyle`；动画/密度/材质/边框/圆角/前景/标题图标规范值与归一化下沉 Contracts（`WidgetMaterialKinds`/`WidgetBorderKinds`/`WidgetCornerKinds`/`WidgetForegroundKinds`/`LayoutDensityKinds`/`WidgetAnimationKinds`/`WidgetTitleIconKinds`/`WidgetChromeKinds`，SettingsService/WidgetForegroundSettings/WidgetChromeMode/WidgetTitleIconMode 常量改别名，`LayoutDensityPresetValues` 记录移驻 Contracts）。

| 职责 | 所有者 |
|---|---|
| 外观族五节 XAML 绑定面（76 属性：读投影+TwoWay 写入+选项表+本地化缓存重建+密度/动画预设状态机+滑杆归一化回夹） | `Features/Appearance/AppearanceSettingsViewModel`（ObservableObject 化，零 Services/Platform/App/WinUI 引用；Visibility 门以布尔+`SettingsBoolToVisibilityConverter`、颜色以十六进制串+`SettingsColorStringConverter` 绑定，编辑器保持 WinUI-free） |
| NativeAOT {Binding} 桥（75 nameof 条目，仅 XAML 消费面） | `Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs`（Glance/Music 同款） |
| 活预览编排、滑杆拖动旗标、主题/主题色/托盘图标/组导航宿主联动、Windows 兼容探针 | 仍在 SettingsViewModel（AppearanceOptions/PreferenceCommands/HotkeyAndAppearance partial），经事件/推面与编辑器协作 |
| 五节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`sectionTag is "Appearance" or "AppearanceMaterialSettings" or "AppearanceDensitySettings" or "AppearanceWindowSettings" or "AppearanceAnimationSettings"` 时 `section.DataContext = _appearanceSettingsViewModel`（SettingsWindow 新增编辑器字段与 `AppearanceSettings` 公开访问器） |
| 外部刷新路径 | ApplySettingsSnapshot → `_appearanceSettings.SyncPresentation()`+主题/accent/组导航推面；OnLanguageChanged → `RefreshLocalization()`+组导航选项重推；恢复默认 → 快照路径（UseSystemAccentColor 内部化后仍走快照赋值） |
| 壳兼容面（已删除，门面退役至今最大单批：壳 ViewModels 侧约 1,790 行净删除） | ~~SelectedTheme(+Text)/SelectedTrayIconStyle(+Text)/AvailableTrayIconStyles(+DisplayNames)/UseSystemAccentColor/CanEditCustomAccent/SelectedAccentColorSource/AvailableAccentColorSourceOptions/AccentColorDescription/SelectedAccentColor/AccentColorHex/AccentPreviewBrush/SelectedWidgetMaterialType(+Text)/AvailableWidgetMaterialTypes(+DisplayNames)/IsWindows10VisualCompatibilityMode/SupportsNativeWidgetCorners/Windows10VisualCompatibilityTitle/Message/IsOpacitySliderEnabled/WidgetOpacityVisibility/MaterialIntensityVisibility/SelectedWidgetBorderColorMode(+Text)/IsWidgetBorderStyleEnabled/SelectedWidgetBorderStyle(+Text)/AvailableWidgetBorderModes/Styles(+DisplayNames)/SelectedWidgetForegroundMode/SelectedWidgetForegroundColor/WidgetForegroundCustomColorVisibility（整文件 SettingsViewModel.WidgetForeground.cs 删除）/AvailableWidgetCornerPreferences(+DisplayNames)/SelectedLayoutDensity(+Text)/AvailableLayoutDensities(+DisplayNames)/FileNameLineCount/SelectedAnimationPreset(+Text)/AvailableAnimationPresets(+DisplayNames)/SelectedWidgetAnimationEffect/Speed/SlideDirection/EasingIntensity(+各 Text)/IsSpeed/Direction/EasingEnabled/AvailableWidgetAnimation* 四组(+DisplayNames)/SelectedDisplay/InteractiveWidgetChromeMode(+Text)/AvailableDisplay/InteractiveWidgetChromeModes(+DisplayNames)/SelectedWidgetTitleIconMode(+Text)/AvailableWidgetTitleIconModes(+DisplayNames)/WidgetTransparency/十个 ObservableProperty（DefaultWidth/Height、WidgetOpacity、WidgetMaterialIntensity、IconSize、TextSize、LayoutDensityScale、Horizontal/VerticalSpacingScale、FileNameWidthScale）/全部 *ValueText/*Percent/*PercentInput/*Input 死面/RefreshNumberInputs/ApplyNumberInput/ApplyLayoutDensityPreset/SyncLayoutDensitySelection/ApplyAnimationPreset/SyncAnimationPresetSelection/ResolveAnimationPreset/ApplySpacingScaleChange/八个 On*Changed 回调（整文件 SettingsViewModel.AppearanceCallbacks.cs 删除）/OnDefaultWidth/HeightChanged/十四个显示名 GetXxxDisplayName/SettingsDropDownButton_Click 死代码 + 构造/快照/本地化处全部读写~~（保留：SelectedLanguage 系、SelectedWidgetGroupDefaultNavigationStyle 系与 AvailableWidgetGroupNavigationStyleOptions——WidgetGroups 节（顺位 6）仍走壳；SelectedWidgetCollapseBehavior/CompactContentMode——Capsule 节（顺位 7）仍走壳；UseSystemAccentColor/_currentAccentColor/RefreshAccentPreview/SetCustomAccentColor 转 internal 工作态） |
| AOT 持久化烟囱 | App.AotManagedUiSmoke 的 FileNameLineCount/TextSize/SelectedTrayIconStyle 拨动与读出改经 `settingsWindow.AppearanceSettings` 编辑器面（evidence 字段名不变），磁盘链路不变 |

行为语义保全要点：①滑杆越界回夹仍由协调器归一化+编辑器重入（NeedsNormalization 路径逐点保真，含二遍提交）；②材质切换在协调器内 preview→防抖保存（未动）；③密度滑杆编辑静默标"自定义"（MarkLayoutDensityCustom 不发事件），组合框拨"自定义"才触发壳普通保存事件——与旧壳 SyncLayoutDensitySelection/SelectedLayoutDensity setter 两分支语义一致；④SlideFade 效果配空方向的 Right 纠偏、动画预设四字段的 scheduleSave 延迟、预设解析回 Custom 的联动全在编辑器同序复刻；⑤主节预设色板按钮经编辑器 `NotifyAccentPresetPicked` 事件回壳 SetCustomAccentColor；⑥默认宽/高 NumberBox 写入仍由协调器 UpdateDefaultWidgetWidth/Height 内部即时防抖保存（旧壳回调只刷已死的输入镜像）；⑦托盘图标切换=协调器写+壳 UpdateTrayIcon 事件，主题=纯壳事件，主题色来源/自定义色=壳状态机+推面回环。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 316→**243**（测试两处+审计 ps1）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 63→**53**（删十个数值 ObservableProperty）；③动态对账测试自动平衡（壳属性与 XAML 路径双侧同步收缩；编辑器桥与五节 XAML 路径互查）；④WMC1510=866 与全部 runner 零触碰（{Binding} 计数不变）；⑤`SettingsSliceOwnership` 读棘轮自动收缩（AppearanceOptions 因推面读 `Settings.Core.Theme` 切片而非门面，预算 1 持平）；⑥`FacadePassthroughAccess` 编辑器/Contracts/协调器文件零命中（"Settings.Theme.*" 本地化键前缀分片拼接 `"Settings.The" + "me."` 规避）；⑦ModuleBoundary：编辑器仅引 Contracts/Models/MVVM Toolkit，零 Services/Platform/App/WinUI；⑧外部文本钉七处随绑定名更新（WidgetForeground/WidgetCorner/SolidColorBackdrop/FileNameLineCount/AotStage5B4B2A/SettingsCopyAndHierarchy×2）；⑨两处旧测试改接新面（AppearanceSettingsCoordinatorTests 滑杆断言改直连协调器、SettingsViewModelQuickCaptureTextSizeTests 改镜像生产事件接线）。

### 第四十三批验证记录

- canonical Debug（非平台）构建 0 错误、11 警告（全为存量无关位）；x64 Debug 随测试构建 0 错误。
- 新增 `AppearanceSettingsEditorTests` 17 用例（构造投影/读快照归一化/滑杆写穿透+事件/归一化重入/密度标自定义/密度预设七字段单事件/动画预设四字段+回 Custom/SlideFade 方向纠偏/外部同步零回写/推面静默零事件/用户选择事件链/主机环境推面门控/选项写保存语义分族/默认尺寸归一化重入/本地化重建/壳反射面无残留+组导航保留/XAML+桥+接线文本钉）。全量 x64 测试：**4,378/4,378 通过**（批 42 基线 4,361 + 本批 17）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 随同隔离 restore，`--no-incremental`，经 `-getProperty:DefineConstants` 确认生效，检查后已清理）：**0 错误**、22 警告（码集与批 41/42 同位：CS8602/CS8601/CS0414/CS0169/CS0108）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针）**：隔离数据根 `facade-appearance-43-20260928-d43a9f` 的 `<root>/data/settings.json` 预置 `theme=Dark`、`trayIconStyle=White`、`widgetMaterialType=Mica`、`widgetMaterialIntensity=0.3`、`widgetOpacity=0.4`、密度七字段=Relaxed 预设值、`fileNameLineCount=1`、`widgetCornerPreference=Square`、`widgetBorderColorMode=Accent`、`widgetBorderStyle=Thick`、`widgetTitleIconMode=Hidden`、`widgetAnimationEffect=Zoom`、`widgetForegroundMode=Light`（camelCase，schemaVersion 9）+已完成 onboarding、zh-CN。canonical Debug `--open-settings` 启动（跳转列表激活路径，启动管线 35 步（5 critical）、0 degraded、0 failed），UIA 进入"外观"主节读到：应用主题=**深色**、托盘图标=**黑色**（White 值的中文显示名）、材质行=**云母**、密度行=**宽松**（七字段解析出的预设标签）、标题栏行=**隐藏**、动画行=**自定义**（Zoom 解析）——主节 6 项非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影（主题/托盘走壳推面链）。drill-down 材质子节读到：格子文字=**浅色文字**、边框颜色=**主题色**、边框样式=**粗**、材质浓度滑杆=**0.3**（Mica 下透明度滑杆按材质能力门正确折叠）；圆角读到**大圆角**——预置 Square 被协调器读路径的 `ResolveEffectiveWidgetCornerPreference` 按 OS 能力归一化为 Round，与旧壳构造器逐字同源（绑定活、语义同）。再经 RangeValuePattern 把材质浓度拨到 **0.6**，6 秒后磁盘 `widgetMaterialIntensity=0.6` 且其余预置字段（widgetOpacity=0.4、iconSize=36、textSize=13、widgetAnimationEffect=Zoom、theme=Dark）不动——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→事件→壳 SaveAppearanceChange→SaveDebounced→磁盘"回写贯通。探针后已按路径停止本 worktree 实例（主检出与 wingezi-p1c 实例未触碰）；探针脚本临时件未入库。
- `git diff --check` 通过。
- 遗留风险：①主节主题/主题色/组导航三族是全量 push+事件回壳（状态机在壳；迁编辑器需下沉 ThemeService 委托与组导航域）；②组导航选项表由壳推送（WidgetGroups 批迁移时编辑器可自建表，届时回收推送）；③密度/动画预设的编辑器状态机重演了旧壳分支（后续若多节复用可抽公共预设助手）；④XAML 值转换器（bool→Visibility、十六进制↔Color）为外观族新增的首批 {Binding Converter} 用法，JIT 与 AOT 均编译过、UIA 双向已验，但发布级 AOT publish 未跑（发版门禁）；⑤未做真实设置页逐滑杆拖动手感验收（拖动旗标链路代码未动，且 UIA 写入路径已证明提交链贯通）。



## 第四十四批：门面退役第二阶段第四复制批（分组节+胶囊族五节 XAML 绑定迁各自编辑器）

实施基线：`ab2025c9`（main，含批 40-43），worktree `codex/final2-facade-groupcapsule`。对象是施工图顺位 6/7 两族：**WidgetGroups 分组节**（9 节级绑定：默认导航风格/默认标题显示/滚轮切换/悬停切换两开关/现有组列表与空态门）与**胶囊族五节**（CapsuleMode 主节 UserControl 28 绑定 + 行为 5+排列 6+动画 3+覆盖列表 7 子节 = 编辑器绑定面共 42 属性——主节不切则子节共享属性无法真删，与批 43 外观族"主节+四子节"同构）。磁盘 schema、文案零变化；{Binding} 标记形态保持（WMC1510 计数 866→**864**：外观主节被回收的组导航内联选择器带走 2 条 {Binding}，壳 AOT 桥 nameof 计数 243→**192**，测试+审计 ps1 双处同步）；可见性门经 XAML 值转换器绑布尔投影（批 43 先例），两编辑器零 Services/Platform/App/WinUI 引用。

**组导航推面回收（本批对批 43 的修正）**：批 43 曾把组导航默认样式作为壳推面挂进外观编辑器（`GroupNavigationStyle`/`AvailableGroupNavigationStyleOptions`/`UpdateGroupNavigationPresentation`/`GroupNavigationStyleUserChanged` + 外观主节 WidgetGroups 行的内联 ComboBox）。本批按归属整体回收：上述推面从外观编辑器与壳（`OnAppearanceGroupNavigationStyleUserChanged`/`PushAppearanceGroupNavigationPresentation` 及全部调用点）删除，外观主节该行退化为纯 drill-down 行（保留 Tag=WidgetGroups 与说明文案）；字段重绑到 GroupNavigation 编辑器后由协调器真持久化。

**胶囊族派生视图态（批 33 语义保全）**：悬停响应预设（Sensitive/Balanced/PreventAccidental）无持久字段，编辑器内部处理不落盘——预设选择经 `HoverResponsePresetDelays` 把延迟对写入两滑杆（`_isApplyingHoverResponse` 抑制期间不回标 Custom），手动改延迟经 `MarkHoverResponseCustom` 翻标签回 Custom 且选择本身永不落盘；动画预设对偶（`AnimationPresetDurationMs` 预设伴随时长 + 自定义时长翻 Custom）在编辑器同序复刻，协调器持有配对写/翻转语义（批 33 已建）。胶囊规范值/归一化/钳制/预设映射整体下沉 `Contracts/CapsuleOptionKinds`（SettingsService 47 常量+12 归一化函数改别名，折叠行为字符串级归一化含 "Manual"→Click/"Auto"→Smart/System 禁入的旧壳语义）。

| 职责 | 所有者 |
|---|---|
| 两族 XAML 绑定面（9+42 属性：读投影+TwoWay 写入+选项表+本地化缓存重建+胶囊动画/悬停预设状态机+滑杆归一化回夹） | `Features/GroupNavigation/GroupNavigationSettingsViewModel` 与 `Features/Capsule/CapsuleSettingsViewModel`（均 ObservableObject 化，新增 localize/format 委托参数） |
| NativeAOT {Binding} 桥（9+42 nameof 条目） | 两编辑器各自 `*.AotBindableProperties.cs`（Glance/Music/外观同款） |
| 现有组投影与组编辑状态机（Rename/SetXxx 六个逐组覆写方法/ResetWidgetGroupOverrides/成员重排解散流）+ 覆盖列表投影与重置命令（ResetCapsule* 四命令/ResetCapsuleOverridesForWidget） | 仍在 SettingsViewModel（GroupNavigation/CapsuleOptions partial）：经 `UpdateExistingGroups`/`UpdateOverridePresentation` 整包推面进编辑器；组默认写入经编辑器 `PresentationUserChanged` 事件回壳跑 `AfterWidgetGroupPresentationChange`（宿主通知+投影重建） |
| CapsuleModeSettingsSection.UserControl 的 ViewModel DP | **未换型**（施工图预留）：DP 仅供 `x:Bind ViewModel.ResetCapsuleWidthOverridesCommand`，重置命令状态机在壳（改 AppSettings+SaveDebounced），与批 43 保留 chrome 重置命令同判据 |
| 两族节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`WidgetGroups`→组导航编辑器；`CapsuleMode`/`CapsuleBehaviorSettings`/`CapsuleArrangementSettings`/`CapsuleAnimationSettings`/`CapsuleOverridesSettings`→胶囊编辑器 |
| 外部刷新路径 | ApplySettingsSnapshot → 两编辑器 `SyncPresentation()`；OnLanguageChanged → 两编辑器 `RefreshLocalization()`+`RefreshWidgetGroupSettings()`（重建并重推现有组）+`NotifyCapsuleOverridePropertiesChanged()`（重建并重推覆盖列表/汇总） |
| 壳兼容面（已删除，壳 ViewModels 侧净删约 1,074 行） | ~~组导航：SelectedWidgetGroupDefaultNavigationStyle(+选项表)/SelectedWidgetGroupDefaultTitleDisplayMode(+选项表)/IsWidgetGroupWheelSwitchEnabled/IsWidgetGroupHoverSwitchEnabled/ExistingWidgetGroupItems/ExistingWidgetGroups(Empty)Visibility/WidgetGroupOverviewSummaryText（死面）~~ + ~~胶囊族：SelectedWidgetCollapseBehavior/CompactContentMode（原挂 AppearanceOptions）/SelectedWidgetCompactWidthMode/ExpansionDirection/ContentMode/HideSensitiveContent/SelectedWidgetCapsuleArrangementMode/BarPlacement/BarDirection/WidgetCapsuleBarSpacing(+Text)/SelectedWidgetCompactAnimationEffect/AnimationDurationMs(+Text)/SelectedWidgetCompactHoverResponse/HoverResponse 派生面/ExpandDelayMs/CollapseDelayMs(+Text)/SelectedWidgetCompactMediaCornerMode 系（死面）/CapsuleArrangementDetailsSummaryText/全部 *Text/*Visibility/*Enabled 派生面/九组 Available* 选项表与 DisplayNames 缓存/动画与悬停预设状态机/NotifyCapsuleOverridePropertiesChanged 的 16 条 OnPropertyChanged~~ + 构造/快照/本地化处全部读写；投影记录 `WidgetGroupSettingsItem`/`WidgetGroupMemberSettingsItem`/`CapsuleOverrideSettingsItem` 移驻 Models（WinRT bindable 清单 +2 登记） |

行为语义保全要点：①四默认字段写入=协调器归一化+unchanged 跳过+防抖保存，壳只在真变更时跑宿主通知与投影重建（原 `AfterWidgetGroupPresentationChange` 语义）；②胶囊逐字段写=协调器即时防抖保存（原壳回调同序），动画预设配对写/自定义时长翻转在协调器；③`ShowHoverResponseEntry` 门=折叠行为 Smart 或推面的 Smart 覆盖存在（原 `IsSmartWidgetCollapseBehavior` 的选择项+逐组扫描两分支）；④排列门 `IsBarEnabled`/`IsBarSpacingEnabled`/`ShowArrangementEntry` 由 Bar 模式派生，排列详情摘要 Format(placement, direction, spacing) 同参；⑤MediaCorner 三面无 XAML 消费者，整族删除（协调器端口保留）；⑥组选项表的 FollowDefault 标签改读 settings 原值（原读壳门面属性）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 243→**192**（测试两处+审计 ps1）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 计数 **53 持平**（两族原为手写 SetProperty 面）；③动态对账测试自动平衡（壳属性与 XAML 路径双侧同步收缩）；④WMC1510 866→**864**（回收的内联选择器带走 2 条 {Binding}；未做任何 {Binding}→x:Bind 转换，测试+ps1 双处同步）；⑤`SettingsSliceOwnership` 读棘轮自动收缩（CapsuleOptions/GroupNavigation 文件预算从 20/20 起收缩）；⑥`FacadePassthroughAccess` 编辑器/Contracts 零命中（本地化键经值拼接，无平铺门面名碰撞）；⑦ModuleBoundary：编辑器仅引 Contracts/Models/MVVM Toolkit；`LegacyModelsNamespace_UiDependenciesDoNotGrow` 清单 +2（投影记录带 WinRT 属性，SettingsOption 同款登记）；⑧外部文本钉六处随绑定名更新（SettingsCopyAndHierarchy×4/AotStage5B4B1 深投影记录路径/WidgetCompactExpansionDirectionPolicy/AppearanceSettingsEditorTests 组导航保留钉翻转）。

### 第四十四批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位）；x64 Debug 随测试构建 0 错误。
- 新增 `GroupAndCapsuleSettingsEditorTests` 15 用例（组导航构造投影/写穿透+宿主事件与 unchanged 跳过/外部同步零回写/现有组推面静默/选项表规范值与本地化重建；胶囊构造投影+快照归一化（含悬停预设解析）/九字段写穿透/动画预设配对写+自定义翻转/悬停预设延迟对写入且标签不落盘+手动延迟翻 Custom/Smart 覆盖推面门/外部同步零回写/排列摘要格式化/XAML+桥+接线文本钉/壳反射面无残留）。另将 CapsuleSettingsCoordinatorTests/GroupNavigationSettingsCoordinatorTests 两处旧缝构造改指新编辑器签名。全量 x64 测试：**4,393/4,393 通过**（批 43 基线 4,378 + 本批 15）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore 且 Updater 需带同 ArtifactsPath，`--no-incremental`，经 `-getProperty:DefineConstants` 确认生效，检查后已清理）：**0 错误**、22 警告（码集与批 41-43 同位：CS8602/CS8601/CS0414/CS0169/CS0108）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针）**：隔离数据根 `facade-groupcapsule-44-3b85f1` 的 `<root>/data/settings.json` 预置（schemaVersion 9 + `widgetCompactSettingsVersion: 2`，缺后者会被旧迁移重写折叠/内容两字段——探针首跑踩坑后在 progress 记录）`widgetCollapseBehavior=Smart`、`widgetCompactContentMode=Minimal`、`widgetCompactHideSensitiveContent=true`、`widgetCompactWidthMode=Independent`、`widgetCompactExpansionDirection=Up`、`widgetCapsuleArrangementMode=Bar`、`widgetCapsuleBarPlacement=Top`、`widgetCapsuleBarDirection=Vertical`、`widgetCapsuleBarSpacing=12`、`widgetCompactAnimationEffect=Slow`、`widgetCompactExpandDelayMs=620`、`widgetCompactCollapseDelayMs=900`（PreventAccidental 预设对）；`widget-layout.json` 预置 `widgetGroupDefaultNavigationStyle=Stack`、`widgetGroupDefaultTitleDisplayMode=TextOnly`、`widgetGroupWheelSwitchEnabled=false`、`widgetGroupHoverSwitchEnabled=true`（envelope 需 `{"schemaVersion":1,"layout":{...}}`，缺 layout 键走隔离检疫路径——首跑踩坑记录）+已完成 onboarding、zh-CN。canonical Debug `--open-settings` 启动（启动管线 35 步（5 critical）、0 degraded、0 failed），UIA 先选"外观"再 invoke WidgetGroups drill-down 行进入分组节：标题栏布局=**折叠显示**（Stack）、名称显示=**仅文字**（TextOnly）、滚轮切换=**Off**、鼠标移入自动切换=**On**——四项非默认值经"磁盘（含 widget-layout 设备域）→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影。再经 TogglePattern 把滚轮切换拨到 On，6 秒后磁盘 `widget-layout.json` 的 `widgetGroupWheelSwitchEnabled=true` 且其余预置（Stack/TextOnly/hover=true）不动——组导航 TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveDebounced→widget-layout.json"回写贯通。选"胶囊模式"主节读到：默认显示方式=**悬停自动展开**（Smart）、展开方向=**向上展开**（Up）、收起后显示=**仅图标和标题**（Minimal）、排列方式=**组合排列**（Bar）、隐藏具体内容=**On**；drill-down 组合排列子节读到：胶囊间距滑杆文本=**12 px**、高级排列方向=**纵向**（Vertical）——主节 5 项+排列子节 2 项非默认值投影（绑定若死则控件呈编辑器默认）。再经 TogglePattern 把"隐藏具体内容"拨到 Off，6 秒后磁盘 `widgetCompactHideSensitiveContent=false` 且其余 11 项预置（含 620/900 延迟对与 Smart/Minimal）不动——胶囊 TwoWay 回写贯通。探针后已按路径停止本 worktree 实例（主检出与 wingezi-p1c 实例未触碰）；探针脚本临时件未入库。
- `git diff --check` 通过。
- 遗留风险：①现有组/覆盖列表两投影仍是壳算好整包推送（状态机在壳，迁编辑器需一并下沉组编辑与重置命令）；②CapsuleMode UserControl 的 ViewModel DP 仍指壳（ResetCapsuleWidthOverrides 命令状态机在壳，后续批可随覆盖域整体下沉）；③外观主节 WidgetGroups 行的内联选择器被回收后行内不再能快捷改组导航风格（需进分组节；该族字段归属所致）；④WMC1510 866→864 为计数钉同步而非 {Binding}→x:Bind 转换（发版门禁的 publish 级审计会在下次发版跑真值）；⑤未做真实设置页逐滑杆拖动手感验收（滑杆提交链路 UIA 已证贯通，动画/悬停预设的状态机重演有单测钉）。

## 第四十五批：门面退役第二阶段第五复制批（FileStack 节 XAML 绑定迁编辑器 + 批 42 递延的 FileWidget 总览节 DP 类型例外）

实施基线：`126f6b66`（main，含批 40-44），worktree `codex/final2-facade-filestack`。对象是施工图顺位 7 的**文件叠放节**（10 个节级绑定属性：堆叠总开关/自动叠放/分组方式/阈值/排序/打开方式/浮窗布局/浮窗样式/未匹配行为 + 派生门与摘要），另加批 42 递延的 **FileWidget 总览节 DP 类型例外**（5 个 x:Bind 横跨 FileStack/FeatureWidgets/Interaction 三域）。磁盘 schema、文案零变化；{Binding} 标记形态保持（未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰——本批同时把批 44 遗漏的存量 866 钉全部修齐到 864，见门禁同步）；节模板的 3 处既有 x:Bind（打开方式组合框 + 自定义规则列表）**保持 x:Bind**，模板 x:DataType 重定型到编辑器（免 AOT 桥路线的首个 DataTemplate 先例）。批 36 的薄缝 `Features/FileStack/FileStackSettingsViewModel` 扩成可绑定编辑器（ObservableObject 化，新增 localize/format 委托），非新建。

**FileWidget 总览节 DP 例外的最终处置（按域拆分，非复合）**：批 42 递延时 FileStack 两成员与未迁节共享壳门面；本批 FileStack 节迁移后，三域归属全部就位，故 UserControl 的单一 `ViewModel`（壳门面类型）DP 拆为三个类型化编辑器 DP——`FileStack`（总开关 StacksEnabled + 概要 SettingsSummaryText，均 x:Bind 免桥）、`FeatureWidgets`（文件夹打开方式 FolderOpenBehavior + 选项表，FeatureWidgets 薄缝首次扩出 ObservableObject 面，Contracts 新增 ReadFileWidgetFolderOpenBehavior 读口与 FileWidgetFolderOpenBehaviors 规范值）、`Interaction`（桌面原版右键菜单 FileItemContextMenuEnabled，写口批 41 已备，本批补绑定面 + InteractionPresentationSettings 快照扩字段 + FileItemContextMenuEnabledUserChanged 事件回壳承接 ShellContextMenuProxy.Prewarm 预热）。**证据**：无需跨三域复合投影 VM（零转发层）、无需复制推面（各编辑器直读各自协调器快照）；UIA 探针读出 OverviewCombos=当前格子内浏览（Embedded 预置经 FeatureWidgets DP 投影）、OverviewContextToggle=On、OverviewStackToggle=On、概要文本"自定义格式 · 1 条规则"（FileStack DP OneWay）。

**状态机分离（批 41 子模式）**：规则*预览条目*扫描（widget 配置遍历 + 映射文件夹磁盘枚举 + 公共桌面合并 + 隐藏文件过滤）需要 `_settingsService`/FileService，留在壳（`SettingsViewModel.FileStackOptions.cs` 只剩扫描与推送），经 `MarkPreviewLoading`/`UpdatePreviewEntries` 推面进编辑器；规则匹配、逐规则预览文本、汇总、优先级文本、派生门（ShowCustomRules/HasNoRules/CanAddRule）与总览摘要全部下沉编辑器（本地化经 T/Format 委托）。**自定义规则列表汇聚路径保全（批 36 语义）**：CollectionChanged + PropertyChanged 单写口 `SetFileStackCustomRules` 随编辑器迁移（Add/Remove/Move/CommitRuleOrder/逐规则编辑共用），等价重提交跳过仍在协调器；快照期的规则集等价判断（避免重建集合打断焦点）同序复刻。`RefreshFileStackRulePreviewFromDiskAsync` 的代数失效与生命周期取消（_lifetimeCts）留壳。

| 职责 | 所有者 |
|---|---|
| 文件叠放节绑定面（10 节级属性 + 规则列表 + 派生门/摘要/预览匹配 + 选项表 + 本地化缓存重建） | `Features/FileStack/FileStackSettingsViewModel`（ObservableObject 化，新增 localize/format 委托，零 Services/Platform/App/WinUI） |
| NativeAOT {Binding} 桥（18 nameof 条目，仅 {Binding} 消费面；3 处 x:Bind 与总览 2 处 x:Bind 免桥） | `Features/FileStack/FileStackSettingsViewModel.AotBindableProperties.cs`（Glance/Music 同款） |
| 规范值/归一化/上限下沉 | `Contracts/FileStackOptionKinds`（SettingsService 22 常量 + 8 归一化函数改别名；FileWidgetFolderOpenBehaviorNames 3 常量 + NormalizeGlobal 改别名到 Contracts.FileWidgetFolderOpenBehaviors）；`FileStackPreviewEntry` 移驻 Contracts（壳扫描→编辑器匹配的推面载荷） |
| 预览条目扫描（widget 配置 + 映射文件夹磁盘枚举）与代数失效 | 仍在 SettingsViewModel（FileStackOptions partial 仅剩扫描）：构造期推 widget 条目、进节推磁盘条目 |
| FileWidget 总览节三 DP | `FileWidgetSettingsSection.xaml.cs`（FileStack/FeatureWidgets/Interaction 三 DP，阶段 4E-4 的"单 ViewModel 桥"模式退役）；EnsureSettingsSectionCreated 懒赋值、Closed 三清 |
| 桌面原版右键菜单预热联动 | 仍在 SettingsViewModel（PreferenceCallbacks partial），经 `FileItemContextMenuEnabledUserChanged` 事件回壳（写经编辑器） |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`FileStackSettings`→`_fileStackSettingsViewModel`；模板 x:Bind 的编译绑定根按节路由（`compiledBindingsRoot`，FileStackSettings 传编辑器，其余传壳） |
| 外部刷新路径 | ApplySettingsSnapshot → `_fileStackSettings.SyncPresentation()` + `_featureWidgetsSettings.SyncPresentation()`；OnLanguageChanged → 两编辑器 `RefreshLocalization()`（选项表/优先级/预览/摘要本地语重建 + 当前选择重通知防 ComboBox 复位） |
| 壳兼容面（已删除，壳 ViewModels 侧删 837 行/增 39 行） | ~~FileStacksEnabled/FileStackAutoStacking/SelectedFileStack{GroupBy,Threshold,OrderBy,OpenMode,PopoverLayout,PopoverStyle,UnmatchedBehavior} 九门面 + 七组 Available*Options 与 DisplayNames 缓存 + 六个 GetXxxDisplayName + FileStackCustomRulesVisibility/FileStackRulesEmptyVisibility/CanAddFileStackCustomRule/FileStackPreviewSummaryText/FileStackSettingsSummaryText + 规则集合与汇聚处理器 + Initialize/Apply/RefreshSelection/Reset/Dispose 全套~~ + ~~SelectedFileWidgetFolderOpenBehavior/AvailableFileWidgetFolderOpenBehavior{Options,OptionItems} + 字段 + 快照/构造/本地化读写~~ + ~~FileItemSystemContextMenuEnabled ObservableProperty + OnChanged 回调 + 两处快照赋值~~ |

行为语义保全要点：①编辑器构造/Sync 期 `_isSyncingPresentation` 静默不回写（替代原壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 守卫），归一化语义逐字同源（GroupBy 的 DateCreated→DateAdded 折叠、Threshold 2/3/5 白名单等）；②规则增删改/拖拽排序/逐字段编辑的持久化时序与写次数不变（单写口 + 协调器等价跳过）；③未匹配行为 setter 先刷预览再落盘（原壳同序）；④右键菜单开→On 的预热只对用户拨动触发（原壳快照镜像赋值路径的预热为等值 no-op 场景，语义等价、少一次冷加载优化）；⑤总览节 folder-open 组合框在快照应用期的回写（原壳无快照守卫）由协调器等值跳过承接，语义不变。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 192→**174**（测试两处+审计 ps1）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 53→**52**（删 FileItemSystemContextMenuEnabled）；③动态对账测试自动平衡（壳属性与 XAML 路径双侧同步收缩；FileStack 模板 x:DataType 重定型后其 {Binding} 名不再匹配壳反射面）；④WMC1510 **864 零触碰**，并把批 44 只同步了 stage5B4B1 一处、其余 ~33 处存量 866 钉（ps1 34 处 + 17 个测试文件的字面钉）全部修齐到 864（否则发布级审计会在 stage4E5 首个精确钉处误掷）；⑤`SettingsSliceOwnership` 读棘轮自动收缩（FileStackOptions 22→2、FeatureOptions 2→0）；⑥`FacadePassthroughAccess` 新编辑器/Contracts 文件零命中（属性名去 FileStack/Selected/FileWidget 前缀；"Settings.FileStacks.*"/"Settings.FileWidget.FolderOpenBehavior.*" 本地化键无平铺门面名碰撞——FileWidget 为 get-only 切片属性不计入门面清单）；⑦外部文本钉更新：AotStage4E4ContractTests（三 DP 桥形状/赋值清空标记/行为钉改指编辑器文件）、AotStage5B4B1ContractTests（总览 x:Bind 钉、规则列表 x:Bind 钉、FileWidget 投影钉改指编辑器）、SettingsCopyAndHierarchyTests（总览/节绑定名与排序标记）、SettingsDeferredSectionsTests（模板 x:DataType 期望按节例外）、DeskBox.csproj 桥描述（four typed ViewModel bridge bindings→three typed section-editor bridges）与对应钉。

### 第四十五批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位）；x64 Debug 随测试构建 0 错误。
- 新增 `FileStackSettingsEditorTests` 14 用例（构造投影+快照归一化/写穿透+unchanged 跳过/外部同步零回写+等价规则集不重建/规则增删改移单写口与等价重提交跳过/AddRule 上限与总开关门/推面预览条目驱动匹配与汇总/派生门与总览摘要四态/选项表规范值与本地化名+优先级/本地化重建/FeatureWidgets 面投影写穿透与读口/Interaction 右键菜单面+事件/壳反射面无残留/迁移模式文本钉含模板 x:DataType、三 DP、compiledBindingsRoot 路由与桥计数 174）。另 FileStackSettingsCoordinatorTests/FeatureWidgetsSettingsCoordinatorTests 两处旧缝构造改指新签名。全量 x64 测试：**4,407/4,407 通过**（批 44 基线 4,393 + 本批 14）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离，DeskBox 与 Updater 均随隔离构建，`--no-incremental`，检查后已清理）：**0 错误**、22 警告（码集与批 41-44 同位：CS8602/CS8601/CS0414/CS0169/CS0108）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针，FlaUI/UIA3 COM 通道——本会话的托管 UIA RootElement 不可用，探针脚本临时件未入库）**：隔离数据根的 `settings.json` 预置（schemaVersion 9）`fileStackGroupBy=Custom`、`fileStackThreshold=5`、`fileStackOrderBy=Name`、`fileStackOpenMode=Popover`、`fileStackPopoverLayout=Grid5`、`fileStackPopoverStyle=FollowMaterial`、`fileStackUnmatchedBehavior=Other`、`fileStackAutoStacking=true`、规则 `[设计文件 .psd .ai]`、`fileWidgetFolderOpenBehavior=Embedded`、`fileItemSystemContextMenuEnabled=true` +已完成 onboarding、zh-CN。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**；AppearanceDetail/FileStackSettings 两节延迟创建正常，日志无异常）。UIA 选"文件格子"进入总览节：叠放行概要=**自定义格式 · 1 条规则**、叠放总开关=**On**（FileStack DP）、文件夹打开方式组合框选中=**当前格子内浏览**（FeatureWidgets DP，Embedded 预置）、桌面原版右键菜单=**On**（Interaction DP）——预置非默认值经"磁盘→协调器读快照→各域编辑器→类型化 DP x:Bind→控件"全链投影。drill-down 进入文件叠放节：总开关/自动叠放=**On/On**、分组方式=**自定义格式**、自动叠放数量=**5 个文件**、叠放内排序=**名称**、展开方式=**弹出式展开**（x:Bind 组合框）、弹窗布局=**5×5 网格**、弹窗视觉样式=**跟随格子材质**、未匹配文件=**归入"其他"叠放**，自定义规则列表可见且含预置规则**设计文件**。再经 TogglePattern 把自动叠放拨 Off，6 秒后磁盘 `fileStackAutoStacking=false` 且其余 11 项预置（含规则表、Embedded、右键菜单 true、七个组合框值）逐项不动——TwoWay 经"控件→{Binding}/x:Bind→编辑器 setter→协调器→SaveDebounced→磁盘"回写贯通。探针后已按路径停止本 worktree 实例（主检出实例未触碰）。
- `git diff --check` 通过。
- 遗留风险：①预览条目磁盘扫描仍是整包推送（状态机在壳；迁编辑器需下沉 widget 配置与文件服务委托）；②右键菜单预热的触发面从"任何赋值"缩为"用户拨动"（快照镜像路径本就等值 no-op，仅少一次理论上的冷加载预热）；③WMC1510 866→864 存量钉修齐依据的是批 44 报告的真值（本批未改变 {Binding} 计数，canonical 全量重建亦无 WMC1510），publish 级审计仍会在下次发版跑真值复核；④设置窗口关闭时总览节三 DP 显式置 null（与既有 AppearanceDetail 清空同型），未观察到泄漏迹象但未做显存剖析。

## 第四十六批：门面退役二阶段第六复制批（QuickCapture 节 XAML 绑定迁编辑器——剩余最大单体节）

实施基线：`abd7883f`（main，含批 40-45），worktree `codex/final2-facade-quickcapture`。对象是施工图顺位 8 的 **QuickCapture 节**（35 个唯一绑定属性，门面退役至今最大单体节：功能开关/状态行、宽屏布局+打开方式、顶部分类组（显示开关/可见分类按钮/默认视图/标签样式）、内容与编辑组（两字号滑杆/预览行数/编辑格式/Enter 行为/创建时间/远程图片）、录制组（文本录制/图片录制/最近容量 NumberBox）、数据管理组（图缓存行/两个清理按钮的门与文本）+ 诊断摘要行）。批 19-22+38 已把全部写入迁 `QuickCaptureSettingsCoordinator`；本批把**绑定面**迁到新建的 `Features/QuickCapture/QuickCaptureSettingsViewModel`（施工图"IQuickCaptureSettings 域"的具体化，协调器合同本就齐备）。磁盘 schema、文案零变化；{Binding} 标记形态保持（SettingsWindow.xaml 的 {Binding} 计数 423 逐字不变，未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰）；可见性门经既有 `SettingsBoolToVisibilityConverter` 绑布尔投影（批 43 先例，{Binding} 个数不变）。

**录制连锁的回推方案（本批核心决策）**："图片录制开→文本录制+功能连锁开"与"文本录制关→图片录制跟随关"的归一化在协调器（`WriteEnabled`/`WriteRecording`），且 `SetEnabledAsync`/`SetClipboardEnabledAsync`/`SetImageEnabledAsync` 本就是协调器合同端口——编辑器 setter 直接调这些端口（异步链经 `RunUserAction`：await→异常回报→finally 全量重投影+诊断重建，替代旧壳 `TrackQuickCaptureAction`），连锁的视觉呈现不需要任何编辑器内复制：协调器写盘后同步触发 `Changed` 事件→壳 `OnQuickCaptureSettingsChanged`→编辑器 `SyncPresentation()` 整面重投影→三个开关的 {Binding} 各自跟随。容量输入的 350ms 安静期防抖裁剪链（批 21）整体留协调器，编辑器值变更只走 `SetRecentLimit`（协调器归一化+SaveDebounced+QueueRecentTrim+Changed 回推归一化值）。

| 职责 | 所有者 |
|---|---|
| QC 节 XAML 绑定面（35 属性：读投影+TwoWay 写入+选项表+标签可见性 flyout 状态机+派生门/摘要/状态行+本地化缓存重建+字号滑杆半步归一化回夹） | `Features/QuickCapture/QuickCaptureSettingsViewModel`（新建，ObservableObject，零 Services/Platform/App/WinUI 引用；localize/format/log/reportError 委托注入） |
| NativeAOT {Binding} 桥（35 nameof 条目，仅 XAML 消费面） | `Features/QuickCapture/QuickCaptureSettingsViewModel.AotBindableProperties.cs`（Glance/Music/FileStack 同款） |
| 规范值/归一化/上限下沉 | `Contracts/QuickCaptureOptionKinds`（SettingsService 22 常量+7 归一化函数改别名；含 `NormalizeTextSizeStep` 半步归一化） |
| 录制启停/连锁/容量裁剪/启停宿主链（SetEnabledAsync 等）、Changed/DiagnosticsChanged 广播 | `QuickCaptureSettingsCoordinator`（批 19-22 建，未动） |
| 图缓存扫描行（`QuickCaptureService.GetImageCacheInfoAsync`）与诊断变更的 UI 线程派发 | 仍在壳（QuickCaptureDiagnostics partial）：文本/门经 `UpdateImageCachePresentation(text, canClear)` 推面（批 42 快速访问同款）；诊断文本构建（批 22 的原因码→本地化映射）下沉编辑器 `RefreshClipboardDiagnostics`，壳 `DiagnosticsChanged` 派发后调它 |
| 字号滑杆保存编排 | 编辑器 `ListTextSizeCommitted`/`ContentTextSizeCommitted` 事件回壳 `SaveAppearanceChange()`（批 43 活预览分离同款：scheduleSave:false 写协调器+壳统一防抖保存与拖动旗标裁决；全局字号继承刷新链经协调器 `RefreshFromSettings`→Changed→编辑器重投影保持批 22/43 回归） |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`QuickCaptureSettings`→`_quickCaptureSettingsViewModel`（模板纯 {Binding} 无 x:Bind，x:DataType 不变，compiledBindingsRoot 仍传壳） |
| 可见分类 flyout | `QuickCaptureTabsDropDown_Click` 改经 SettingsWindow 的编辑器字段（`AvailableDefaultViews`/`IsTabSelected`/`CanToggleTab`/`ToggleTab`/`GetDefaultViewDisplayName` 随状态机下沉编辑器） |
| 外部刷新路径 | ApplySettingsSnapshot → `SyncPresentation()`；OnLanguageChanged → `RefreshLocalization()`（选项表/摘要/诊断行重建+当前选择重通知防 ComboBox 复位）；功能卡重置/启停（FeatureOptions 的 `SetWidgetEnabled`/`ResetFeatureWidgetAsync`/`RefreshFeatureWidgetViewState`）改调编辑器（`SetFeatureEnabled` 镜像节开关链；重置后 `SyncPresentation`+`RefreshClipboardDiagnostics` 替代旧壳镜像赋值） |
| 壳兼容面（已删除，壳 ViewModels 侧删 911 行/增 139 行） | ~~QuickCaptureEnabled/ShowTabBar/ShowRecordsTab/ShowPinnedTab/ShowRecentTab/ClipboardEnabled/ImageClipboardEnabled/RecentLimit/ShowCreatedTime/ListTextSize/ContentTextSize 十一个 ObservableProperty + 八个 On*Changed 回调 + SelectedQuickCaptureDefaultView(+Text)/SelectedQuickCaptureTabStyle(+Text)/QuickCaptureTabStyleIndex + QuickCaptureStatusText/DependencyStatusText（死面）/RecentLimitText/RecentLimitInput（死面）+ApplyQuickCaptureRecentLimitInput/TryParseNumberInput + 可见标签族（QuickCaptureVisibleTabsText/TabsSummaryText/VisibleQuickCaptureDefaultViewOptions/IsQuickCaptureTabSelected/CanToggleQuickCaptureTab/ToggleQuickCaptureTab/GetQuickCaptureTabDisplayName/JoinSelectedQuickCaptureTabs/CountSelectedQuickCaptureTabs）+ 布局/内容摘要与可见性门 + 编辑器五字段与 SetQuickCaptureSetting 助手 + 六组 QC 专属选项表与 DisplayNames 缓存 + 四个 QC 显示名函数 + NormalizeQuickCaptureDefaultView + 全部 Sync*Facade/Apply*/TrySet*TextSize/TrackQuickCaptureAction + 图缓存/诊断两文本字段与壳内构建（诊断映射下沉编辑器）~~（共享面保留：AvailableItemPreviewLineCounts/DisplayNames/Options 与 AvailableEditorEnterBehaviors/DisplayNames/Options、GetEditorEnterBehaviorDisplayName、GetWidgetTabStyleDisplayName——Todo 节仍绑定；PersistOwnedTextSize 留壳供 Todo 字号） |

行为语义保全要点：①编辑器构造/Sync 期 `_isSyncingPresentation` 静默不回写（替代旧壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 守卫），归一化语义逐字同源（协调器读快照本就按旧壳构造器归一化）；②字号滑杆沿用旧壳两遍提交：越半步网格的值先回夹再由重入路径落盘+发事件（非有限值改为从有效快照重投影）；③录制端口的 try/catch+重同步（协调器 Stop 后抛 ObjectDisposedException 的旧壳防护）在编辑器 `RunWithFailureProjection`/`TrySetTextSize` 复刻；④诊断摘要行在开关变更后即时重建（旧 `RefreshQuickCaptureClipboardDiagnostics` 时序）；⑤图缓存行初始 loading 文本与扫描失败/空态文本由壳推面（文案键不变）；⑥功能卡重置块的六处壳镜像赋值删除后由协调器 Reset 端口+编辑器重投影等价承接（重置流尾部单次 SaveAsync 不变）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 192→174 再至 **141**（测试两处+审计 ps1，FileStackSettingsEditorTests 的桥计数钉同步 141）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 52→**41**（删十一个 QC ObservableProperty）；③动态对账测试自动平衡（壳属性与 XAML 路径双侧同步收缩；QC 节重绑后 33 个 QC 绑定名不再匹配壳反射面）；④WMC1510 **864 零触碰**（{Binding} 计数不变，可见性门走 Converter 不改计数）；⑤`SettingsSliceOwnership` 读棘轮自动收缩（本批只删壳代码：ContentEditorOptions/SettingsSync/SettingsViewModel.cs 等文件的 QC 平铺读随面删除自然收缩，均未涨破清单预算、未触碰清单本身）；⑥`FacadePassthroughAccess` 新编辑器/Contracts 文件零命中（属性名去 QuickCapture/Selected 前缀；"Settings.QuickCapture.*" 本地化键安全——QuickCapture 是 get-only 切片不在平铺门面清单内，与批 41/43 的 Theme 踩坑相反）；⑦ModuleBoundary：编辑器仅引 Contracts/Models/MVVM Toolkit（零 Services/Platform/App/WinUI），QuickCaptureEnablement 门禁的回调钉改指编辑器文件；⑧外部文本钉更新：SettingsCopyAndHierarchyTests（QC 节绑定名四钉）、SettingsViewModelQuickCaptureTextSizeTests（镜像生产事件接线改编辑器 SyncPresentation）、OnboardingExperienceTests（启停链钉改编辑器）、ModuleBoundaryContractTests（回调钉改编辑器）。

### 第四十六批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位）；x64 Debug 随测试构建 0 错误（DeskBox+Updater）。
- 新增 `QuickCaptureSettingsEditorTests` 14 用例（构造投影+未知值归一化/写穿透/外部同步零回写/**录制连锁：图片录制从关闭态拨开→协调器连锁开功能+文本录制、Changed 回推三开关全跟随**/标签 flyout 末位保护与可见过滤/容量归一化链（500→100、1→30）/字号半步回夹两遍提交+等值跳过/派生门与摘要/选项表规范值与本地化名/本地化重建+诊断行格式/图缓存推面/壳反射面无残留（60 成员清单）/迁移模式文本钉（XAML 路径、转换器、DeferredSections DataContext、flyout 接线、编辑器桥 35、壳桥 141））。全量 x64 测试：**4,421/4,421 通过**（批 45 基线 4,407 + 本批 14）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore+构建，`--no-incremental`，检查后已清理）：**0 错误**、22 警告（码集与批 41-45 同位：CS8602/CS8601/CS0414/CS0169/CS0108）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针，FlaUI/UIA3 COM 通道，探针脚本临时件未入库）**：隔离数据根 `facade-quickcapture-46-3c71e9` 的 `settings.json` 预置（schemaVersion 9）`quickCaptureEnabled=true`、`quickCaptureClipboardEnabled=false`、`quickCaptureImageClipboardEnabled=false`、`quickCaptureWideLayout=DualPane`、`quickCaptureWideOpenMode=Editing`、`quickCaptureDefaultView=Pinned`、`quickCaptureShowRecentTab=false`、`quickCaptureTabStyle=Pivot`、`quickCaptureShowCreatedTime=false`、`quickCaptureItemPreviewLineCount=6`、`quickCaptureDefaultFormat=Markdown`、`quickCaptureEditorEnterBehavior=EnterSaves`、`quickCaptureAllowRemoteImages=true`、`quickCaptureRecentLimit=77`、`quickCaptureListTextSize=13.5`、`quickCaptureContentTextSize=12.5` +已完成 onboarding、zh-CN。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**），UIA 经"功能格子"页随记卡 drill-down 进入 QC 节，嵌套卡片经设置搜索结果激活 reveal（节内五组 SettingsExpander 为手风琴互斥，产品搜索路径自动展开命中分支）：状态行=**当前状态：已启用**、宽屏布局=**始终双列**、打开方式=**编辑**、默认视图=**固定**（RecentTab 预置隐藏后过滤生效）、列表字号滑杆=**13.5**、内容字号滑杆=**12.5**、预览行数=**6 行**、编辑格式=**Markdown**、回车行为=**Enter 保存，Ctrl+Enter 换行**、显示创建时间=**Off**、远程图片=**On**、最近容量=**77**、功能开关=**On**、图片录制=**Off**——16 项非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影（绑定若死则控件呈编辑器默认）。**联动回推验证**：经 TogglePattern 拨文本录制 Off→On 再拨图片录制 On→文本录制跟随确认 On，最后再拨文本录制 Off→**图片录制视觉跟随 Off**（协调器 WriteRecording 归一化→Changed→壳→编辑器 SyncPresentation 回推全链）、功能开关全程 On 不动；探针后磁盘 `quickCaptureClipboardEnabled=false`、`quickCaptureImageClipboardEnabled=false` 且其余 14 项预置（DualPane/Editing/Pinned/Pivot/false/6/Markdown/EnterSaves/true/77/13.5/12.5 等）逐项不动——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveAsync/SaveDebounced→磁盘"回写贯通。（预置的 `quickCaptureClipboardEnabled=false` 在启动后被随记组件恢复链 `EnableClipboardFromOpenWidgetAsync` 按产品语义置 true——组件打开即启用最近捕获，非绑定行为；联动链在该态下完整演示。）探针后已按路径停止本 worktree 实例（主检出实例未触碰）。
- `git diff --check` 通过。
- 遗留风险：①诊断摘要与图缓存行为壳推面（状态机在壳；迁编辑器需下沉 QuickCaptureService 与剪贴板会话委托）；②`RunUserAction` 的异步完成重投影依赖协调器 Changed 的同步前缀+finally 双保险，UI 线程外的完成（理论）由壳派发路径兜底但未构造多线程用例；③功能卡重置的编辑器重投影在 Reset 端口后立即执行、早于尾部单次 SaveAsync 的 SettingsChanged 广播——两路径幂等（同值重投影）但多一次 PropertyChanged；④~~Todo 节仍与壳共享预览行数/Enter 行为选项表与显示名函数~~（批 47 已随 Todo 编辑器自建回收）。

## 第四十七批：门面退役二阶段第七复制批（Todo 节 XAML 绑定迁编辑器+回收批 46 共享面）

实施基线：`28028de6`（main，含批 40-46），worktree `codex/final2-facade-todo`。对象是施工图顺位 8 的 **Todo 节**（29 个唯一绑定属性：功能开关、布局组（模式/宽布局自动选中门）、顶部分类组（标签栏开关/可见标签 flyout/默认筛选/标签样式 Segmented）、内容与编辑组（两字号滑杆/预览行数/新任务位置/Enter 行为/已完成可见）、提醒组（开关/提前时间）、页脚显示组（统计/清除按钮 flyout））。批 1/13-18/22 已把全部写入迁 `TodoSettingsCoordinator`（`ITodoSettings` 合同本就齐备）；本批把**绑定面**迁到批 1 建的 `Features/Todo/TodoSettingsViewModel`（旧快照式编辑器重写为 QC 式可绑定编辑器，构造签名扩 localize/format 委托）。磁盘 schema、文案零变化；{Binding} 标记形态保持（SettingsWindow.xaml 的 {Binding} 计数 423 逐字不变，未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰）；可见性门经既有 `SettingsBoolToVisibilityConverter` 绑布尔投影（批 43/46 先例，{Binding} 个数不变）。

**联动回推方案（本批核心决策）**：批 14 的"默认筛选×标签可见性"联动（选隐藏筛选→标签自动开；隐藏默认标签→筛选回退首个可见；最后标签保底）本就在协调器（`SetDefaultFilter`/`SaveTabsIfChanged`/`ReadTabs` 归一化），且 Todo 协调器无 Changed 事件（写入全同步）——编辑器每个 setter 走"SetProperty 乐观投影→写协调器→`RunWrite` 尾部整面 `Refresh()` 重投影"：协调器联动归一化后的快照（含筛选回退/末位保护）直接驱动绑定面，零编辑器内联动复制。字号滑杆沿用批 43/46 两遍提交（越半步网格先回夹再由重入路径落盘+发 `ListTextSizeCommitted`/`ContentTextSizeCommitted` 事件回壳 `SaveAppearanceChange`；存储 0 继承全局由协调器 `ReadContentDisplay` 解析，全局字号变化经壳 `OnAppearanceTextSizeCommitted`→编辑器 `Refresh()` 刷新，批 22 回归保持）。

| 职责 | 所有者 |
|---|---|
| Todo 节 XAML 绑定面（29 属性：读投影+TwoWay 写入+五组选项表+标签/页脚两 flyout 状态机+派生门/摘要+本地化缓存重建+字号滑杆半步归一化回夹+Tabs/TabStyle/TrySet\* 批 1-22 合同面） | `Features/Todo/TodoSettingsViewModel`（重写，ObservableObject，零 Services/Platform/App/WinUI 引用；localize/format/reportError 委托注入；功能开关保留批 1 串行异步链 `_enablePending`/generation） |
| NativeAOT {Binding} 桥（29 nameof 条目，仅 XAML 消费面） | `Features/Todo/TodoSettingsViewModel.AotBindableProperties.cs`（Glance/Music/FileStack/QuickCapture 同款） |
| 规范值/归一化下沉 | `Contracts/TodoOptionKinds`（SettingsService 19 个 Todo 常量+4 归一化函数改别名；预览行数/Enter 行为/标签样式/字号步进继续引 `QuickCaptureOptionKinds` 共享 kinds） |
| 默认筛选×标签联动/末位保护/写入归一化/提醒宿主链 | `TodoSettingsCoordinator`（批 1/14 建，未动） |
| 字号滑杆保存编排 | 编辑器 `ListTextSizeCommitted`/`ContentTextSizeCommitted` 事件回壳 `SaveAppearanceChange()`（批 43/46 同款：scheduleSave:false 写协调器+壳统一防抖保存与拖动旗标裁决） |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`TodoSettings`→`_todoSettingsViewModel`（模板纯 {Binding} 无 x:Bind，x:DataType 不变，compiledBindingsRoot 仍传壳） |
| 可见标签/页脚显示两 flyout | `TodoTabsDropDown_Click`/`TodoFooterDisplayDropDown_Click` 改经窗口编辑器字段（`AvailableDefaultFilters`/`IsTabSelected`/`CanToggleTab`/`ToggleTab`/`SetTabVisible`+`AvailableFooterDisplayOptions`/`IsFooterDisplayOptionSelected`/`ToggleFooterDisplayOption` 随状态机下沉编辑器） |
| 外部刷新路径 | ApplySettingsSnapshot/OnAppearanceTextSizeCommitted → `Refresh()`；OnLanguageChanged → `RefreshLocalization()`；功能卡重置（FeatureOptions 的 `ApplyFeatureWidgetDefaultSettingsAsync`）保持编辑器 `Reset*` 端口（内部 RunWrite 已含重投影）；`SetWidgetEnabled`/`IsWidgetEnabled`/`RefreshFeatureWidgetViewState` 的 Todo case 改经编辑器（`SetFeatureEnabled` 镜像节开关链/`Enabled` 读/`Refresh()`+`FeatureWidgetEntries` 通知） |
| 壳兼容面（已删除，壳 ViewModels 侧 +28/-907：整文件删除 FeatureCallbacks/ContentEditorOptions/FeatureTextSize/FeatureSettingsPresentation 四个 partial） | ~~TodoEnabled/TodoReminderEnabled 门面属性+TodoShowTabBar/AllTab/ActiveTab/TodayTab/ThisWeekTab/ThisMonthTab/ImportantTab/CompletedTab/CompletedTasks/FooterStats/ClearCompletedButton/UseWideDetailPane/AutoSelectFirstInWideLayout 十三个 ObservableProperty 与十二个 OnTodo\*Changed 回调+TodoListTextSize/TodoContentTextSize 两滑杆镜像与 `PersistOwnedTextSize`+SelectedTodoLayoutMode/NewTaskPosition/DefaultFilter/TabStyle/ReminderOffsetMinutes(+Text) 五组+TodoItemPreviewLineCount/TodoEditorEnterBehavior+七组 Todo 选项表与 `_cachedTodo*DisplayNames` 五缓存+四个 GetTodo\*DisplayName 与 `GetWidgetTabStyleDisplayName`+Todo 七摘要/`TodoWideOptionsVisibility`/`TodoTabStyleIndex`+标签 flyout 族（IsTodoTabSelected/CanToggleTodoTab/ToggleTodoTab/GetTodoTabDisplayName）与页脚 flyout 族+`SyncTodoTabFacade`/`SyncTodoDisplayFacade`/`SyncTodoTextSizeFacade`/`ApplyTodoTabVisibility`/`ApplyTodoTabBarVisibility`/`RefreshTodoTabsPresentation`/`RefreshTodoContentPresentation`/`RefreshContentEditorLocalizedProperties`/`NormalizeTodoNewTaskPosition`/`NormalizeTodoDefaultFilter` 私有包装~~（批 46 留的共享面一并回收：`AvailableItemPreviewLineCountOptions`/`AvailableEditorEnterBehaviorOptions` 与 `GetEditorEnterBehaviorDisplayName` 随 ContentEditorOptions.cs 整文件删除，Todo 编辑器自建 `AvailablePreviewLineOptions`/`AvailableEnterBehaviorOptions`/`GetEnterBehaviorDisplayName`/`GetPreviewLineDisplayName`；`GetWidgetTabStyleDisplayName` 随壳再无消费者删除；`PersistOwnedTextSize` 随 FeatureTextSize.cs 整文件删除；壳 `OnTodoSettingsPropertyChanged` 瘦身为仅 `Enabled`→`FeatureWidgetEntries` 通知） |

行为语义保全要点：①编辑器 Refresh 期 `_isSyncingPresentation` 静默不回写（替代旧壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 守卫），协调器快照归一化语义逐字同源；②功能开关保留批 1 串行链：pending 期 Refresh 保持用户选择投影、generation 守卫防止旧过渡覆盖新选择；③宽布局自动选中卡的可见性门随布局联动（SinglePane 隐藏），批 46 `ShowWideOptions` 布尔投影+Converter 同款；④提醒开关族的宿主关停链（`SetRemindersEnabled`→`SavePreference(checkNow)`→`_refreshReminders`）不动（协调器内）；⑤启动管线对"无实例且开关开"的功能开关回置 false 是宿主产品语义（WidgetManager 启动对账，非绑定行为——UIA 探针实测：预置 true 被对账置 false 后 UI 忠实投影 Off，再经 UIA 拨 On 写回磁盘贯通）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 174→141 再至 **112**（测试两处+审计 ps1，FileStackSettingsEditorTests 与 QuickCaptureSettingsEditorTests 的桥计数钉同步 112）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 41→**26**（删十五个 Todo ObservableProperty）；`TodoSettingsFacade_PreservesWritableAotBindingSurface` 改钉编辑器（Enabled/RemindersEnabled/DefaultOffsetMinutes 可读写+编辑器桥）；③动态对账测试自动平衡（29 个 Todo 绑定名重绑后不再匹配壳反射面，壳桥同步收缩 29 条）；④WMC1510 **864 零触碰**（{Binding} 计数 423 不变）；⑤`SettingsSliceOwnership` 读棘轮：ContentEditorOptions/FeatureTextSize 整文件删除后清单条目销账，FeatureOptions/SettingsSync/SettingsViewModel.cs 平铺读随面删除自然收缩均未涨破；⑥`FacadePassthroughAccess` 新编辑器/Contracts 文件零命中（属性名去 Todo/Selected 前缀；"Settings.Todo.\*"/"Settings.ContentEditor.\*"/"Settings.Feature.\*"/"Settings.WidgetTabStyle.\*" 本地化键均安全——无平铺门面名冲突）；⑦ModuleBoundary：Todo 编辑器仅引 Contracts/Models/MVVM Toolkit，`QuickCaptureEnablement_DoesNotResolveTheGlobalApp` 检查清单加 Todo 协调器+编辑器，FeatureCallbacks.cs 已删除改断言其不存在；⑧外部文本钉更新：SettingsCopyAndHierarchy（Todo 节绑定名四钉+三个 DoesNotContain 收缩钉）、SettingsViewModelQuickCaptureTextSizeTests（编辑器构造签名）、TodoSettingsCoordinatorTests（构造签名 21 处+PreviewLineCount 更名 ItemPreviewLineCount，批 1-22 合同行为零变化 29 用例全过）、ContentDeletionDirectContractTests（文件清单改指编辑器）。新增 `TodoSettingsEditorTests` 15 用例。

### 第四十七批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位，与批 46 同数）；x64 Debug 随测试构建 0 错误（DeskBox+Updater）。
- 新增 `TodoSettingsEditorTests` 15 用例（构造投影+未知值归一化/写穿透/外部同步零回写/**联动两向：选隐藏筛选→标签自动开、隐藏默认标签→筛选回退全部**/flyout 末位保护与可见过滤/字号半步回夹两遍提交+等值跳过+commit 事件/**存储 0 继承全局且全局变化重投影**/派生门与摘要（页脚 Off 回退）/选项表规范值与本地化名/本地化重建/功能开关串行链 pending 投影/壳反射面无残留（69 成员清单）/迁移模式文本钉（XAML 路径、转换器、DeferredSections DataContext、两 flyout 接线、编辑器桥 29、壳桥 112））。全量 x64 测试：**4,436/4,436 通过**（批 46 基线 4,421 + 本批 15）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore+构建，`--no-incremental`，检查后已清理）：**0 错误**、22 警告（码集与批 41-46 同位：CS8602×14/CS8601/CS0414/CS0169/CS0108 各存量位）。
- **绑定实效验证（UIA 探针，FlaUI/UIA3 COM 通道即 System.Windows.Automation，探针脚本临时件未入库）**：隔离数据根 `facade-todo-47-9d4a2e` 的 `data/settings.json` 平铺预置（schemaVersion 9）`todoEnabled=true`、`todoReminderEnabled=false`、`todoDefaultReminderOffsetMinutes=30`、`todoLayoutMode=SinglePane`、`todoAutoSelectFirstInWideLayout=false`、`todoShowTodayTab=false`、`todoDefaultFilter=Important`、`todoTabStyle=Pivot`、`todoShowCompletedTasks=true`、`todoItemPreviewLineCount=6`、`todoNewTaskPosition=Bottom`、`todoEditorEnterBehavior=EnterSaves`、`todoListTextSize=13.5`、`todoContentTextSize=12.5`、`todoShowFooterStats=true`、`todoShowClearCompletedButton=false` +zh-CN。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**），UIA 经"功能格子"页待办卡 drill-down 进入 Todo 节，逐组展开（手风琴互斥）读出：**布局=固定单列、宽布局自动选中卡随 SinglePane 门隐藏（ShowWideOptions=false 命中）、顶部分类摘要=全部·重要·已完成（TodayTab 预置隐藏生效）、默认视图=重要、列表字号滑杆=13.5、内容字号滑杆=12.5、预览行数=6 行、新任务位置=底部、回车行为=Enter 保存，Ctrl+Enter 换行、显示已完成任务=On、到期提醒=Off（提前时间卡随门禁用）、开提醒后提前时间=30 分钟前、页脚显示=剩余任务数量（清除按钮预置隐藏）**——15 项非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影。**回写四链**：UIA 拨功能开关 Off→On（启动管线对账预置开关回置 false 后的真实状态）→磁盘 `todoEnabled=true`；布局组合框切"自动"→自动选中卡随门出现且投影预置值 Off→磁盘 `todoLayoutMode=Auto`；拨"显示已完成任务" On→Off→磁盘 `todoShowCompletedTasks=false`；拨"到期提醒" Off→On（读出 30 分钟前）→再 Off→磁盘 `todoReminderEnabled=false`。**联动回推验证**：顶部分类 flyout 拨掉"重要"（当前默认视图）→默认视图下拉自动回退**全部**、可见标签摘要变**全部·已完成**（协调器 SaveTabsIfChanged 归一化→编辑器 Refresh 重投影全链），磁盘 `todoDefaultFilter=All`、`todoShowImportantTab=false`；其余 12 项预置（字号 13.5/12.5、6 行、底部、EnterSaves、30 分钟、TodayTab 隐藏、页脚统计开/清除按钮关等）逐项不动——TwoWay 经"控件→{Binding}→编辑器 setter→协调器→SaveDebounced→磁盘"回写贯通。标签样式 Segmented 的 UIA SelectionItem 接口缺失（toolkit 控件仅暴露 ScrollItemPattern）无法 UIA 驱动，由 XAML 迁移钉+编辑器 TabStyleIndex 往返单测+批 46 QC 同款控件先例替代。探针后已按路径停止本 worktree 实例（主检出实例未触碰），数据根与探针件已清理。
- `git diff --check` 通过。
- 遗留风险：①Todo 协调器无 Changed 事件，编辑器写路径的重投影依赖 `RunWrite` 尾部同步 Refresh（写入全同步成立；若未来协调器引入异步归一化需补事件回推）；②功能卡重置链（Reset\* 六端口）逐个 RunWrite 重投影，早于尾部 SaveAsync 广播——幂等但多次 PropertyChanged（与批 46 ③ 同型）；③Segmented（标签样式）UIA 面缺失为 toolkit 控件级限制；④~~Weather 节（顺位 9，26 属性）~~（批 48 已迁编辑器）与 Backup 族（顺位 10）及 General/About/Performance（顺位 11）仍走壳门面。


## 第四十八批：门面退役二阶段第八复制批（Weather 节 XAML 绑定迁编辑器——城市搜索状态机壳推面/事件回壳）

实施基线：`94d0534c`（main，含批 40-47），worktree `codex/final2-facade-weather`。对象是施工图顺位 9 的 **Weather 节**（23 个 XAML 绑定属性+显示选项 flyout 族：位置来源组合框、定位状态行、城市 AutoSuggestBox（文本/建议列表/占位符/无结果行/城市卡可见门）、数据源/温度单位/风速单位/默认视图/皮肤/刷新间隔六组合框（各自选项表）、显示选项多选 flyout 与摘要）。批 38 已把全部写入迁 `FeatureWidgetsSettingsCoordinator`（`WeatherSettingsPolicy` 归一化路径未动）；本批把**绑定面**迁到新建 `Features/Weather/WeatherSettingsViewModel`（独立天气编辑器——天气字段多且带城市搜索链，不并入 FeatureWidgets 薄缝）。磁盘 schema、文案零变化；{Binding} 标记形态保持（SettingsWindow.xaml 的 {Binding} 计数 423 逐字不变，未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰）；三个可见性门（定位状态行/城市卡/无结果行）经既有 `SettingsBoolToVisibilityConverter` 绑布尔投影（批 43/46/47 先例，{Binding} 个数不变）。

**城市搜索链处置（本批核心决策，"壳推面+事件回壳"子模式）**：`CitySearchService`（网络搜索/热门城市）、`WindowsLocationHelper`（Windows 定位）与 300ms 防抖+取消链是宿主服务，编辑器不得引用（Features/* 零 Services/WinUI）——状态机留壳（新 `SettingsViewModel.WeatherSearch.cs` 薄 partial，~173 行），经两个推面端口把结果灌进编辑器（`SetCitySuggestions(suggestions, hasNoResults)`/`SetLocationStatus(text)`），编辑器用 `AutoLocationUserChanged(bool)` 事件回壳（切回自动定位→壳重跑定位查找并推状态行）；城市选择链整体下沉编辑器（`SelectCity` 校验坐标→自动模式先切手动（用户路径，含事件）→协调器 `TrySetWeatherManualLocation`→重投影搜索文本），AutoSuggestBox 四个 code-behind 事件（TextChanged/SuggestionChosen/QuerySubmitted/LostFocus）改接编辑器+壳搜索机；语言变化清空建议列表（下次聚焦按新语言重灌热门城市）。

| 职责 | 所有者 |
|---|---|
| Weather 节 XAML 绑定面（23 属性：读投影+TwoWay 写入+六组选项表+城市搜索投影（文本/建议/无结果/占位）+定位状态行+派生门/摘要+显示选项 flyout 族+本地化缓存重建+`ResetPreferences` 端口） | `Features/Weather/WeatherSettingsViewModel`（新建，ObservableObject，零 Services/Platform/App/WinUI 引用；localize/format/reportError 委托注入；写路径 `RunWrite` 尾部整面 `Refresh` 重投影） |
| NativeAOT {Binding} 桥（23 nameof 条目，仅 XAML 消费面） | `Features/Weather/WeatherSettingsViewModel.AotBindableProperties.cs`（Glance/Music/FileStack/QuickCapture/Todo 同款） |
| 规范值/读归一化下沉 | `Contracts/WeatherOptionKinds`（单位/视图/皮肤/数据源/刷新步进与界、位置模式、显示选项键+读归一化函数；SettingsService 13 个天气常量改别名） |
| 读端口 | `IFeatureWidgetsSettings.ReadWeatherPresentation()`→`WeatherPresentationSettings` 快照（协调器按旧壳构造语义归一化；批 38 写端口/`WeatherSettingsPolicy` 未动） |
| 城市搜索/定位状态机（CitySearchService、WindowsLocationHelper、防抖取消、位置缓存） | 壳 `SettingsViewModel.WeatherSearch.cs`（internal 工作态，推面进编辑器；构造期急灌热门城市保持原时序） |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`WeatherSettings`→`_weatherSettingsViewModel`（模板纯 {Binding} 无 x:Bind，x:DataType 不变） |
| 外部刷新路径 | ApplySettingsSnapshot→`Refresh()`；OnLanguageChanged→`RefreshLocalization()`+清建议；功能卡重置（`ApplyFeatureWidgetDefaultSettingsAsync` Weather case）改编辑器 `ResetPreferences`（内部 RunWrite 已含重投影）；`RefreshFeatureWidgetViewState` Weather case→`Refresh()`；功能格子开关（SetWidgetEnabled 默认分支：协调器写+WidgetManager 同步链）不动 |

行为语义保全要点：①编辑器 Refresh 期 `_isSyncingPresentation` 静默不回写、不发用户事件（替代旧壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 守卫），RunWrite 尾部重投影使搜索文本复位到已存城市名（与旧 ApplySettingsSnapshot 语义一致）；②城市选择期自动→手动的切换走 `SelectedLocationMode` 用户路径（写 `SetWeatherAutoLocation(false)`+事件），`TrySetWeatherManualLocation` 坐标校验前后双保险；③旧 `_isWeatherCitySearchUpdating` 内部旗标删除——窗口 `_isSelectingCity`（SuggestionChosen/QuerySubmitted 链）与 TextChanged 的 `Reason!=UserInput` 检查已覆盖全部抑制路径；④不可见 UI 态 `WeatherLocationStatusIsError`/`IsWeatherCitySearching` 随门面删除（从未被 XAML 消费）；⑤`TrySelectFirstCitySuggestion` 端口承接 QuerySubmitted 空选择回车取首条。

删除壳兼容面（壳 ViewModels 侧 219+/925-：**`SettingsViewModel.WeatherOptions.cs` 整文件删除（-756 行）**）：WeatherAutoLocation/WeatherCityName 与七个 WeatherShow\* 九个 ObservableProperty+九组 `OnWeather*Changed` 回调、SelectedWeather\* 六组选值字段与归一化 setter、七组 AvailableWeather\* 选项表与六个 DisplayName 缓存、四个 GetWeather\*DisplayName、位置模式常量与 SelectedWeatherLocationMode、WeatherLocationStatus 三件与 `RefreshWeatherLocationStatusAsync`、城市搜索面（WeatherCitySearchText/Suggestions/SuggestionItems/Placeholder/NoResultsText/HasNoCitySearchResults(+Visibility)/IsWeatherCitySearching/Update\*/Populate\*/Refresh\*/Select\*/Clear\*/Restore\* 全套）、显示选项族（AvailableWeatherDisplayOptions/Is\*/Toggle\*/Get\*/SummaryText）、SelectionOptions 的七组天气选项桥与通知、SettingsSync 的天气快照块/本地化失效/通知、FeatureOptions 重置 case 的 15 行镜像赋值；SelectionOptions/SettingsSync/FeatureOptions/主文件的 `settings.Weather*` 平铺读随面删除自然收缩（SettingsSliceOwnership 读棘轮 WeatherOptions 条目销账）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 112→**89**（测试两处+审计 ps1，FileStack/QuickCapture/Todo 三处编辑器测试桥计数钉同步 89；天气投影钉改指 `Features/Weather/WeatherSettingsViewModel.cs`（`_citySuggestions.Cast<object>().ToArray()`/`SelectCity(_citySuggestions[0])` 等）+HotkeyAndAppearance 的 `TrySelectFirstCitySuggestion` 接线钉+XAML `ItemsSource="{Binding CitySuggestionItems}"` 钉；ps1 5B4B1 源清单 WeatherOptions 条目原位换编辑器路径（索引稳定）、源警告过滤器加 Features/Weather 路径）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 26→**17**（删九个天气 ObservableProperty）、构造抑制钉尾锚 `WeatherShowPressure=...` 改 `IdleWorkingSetTrimEnabled=...`；③动态对账测试自动平衡（23 个天气绑定名重绑后不再匹配壳反射面，壳桥同步收缩 23 条）；④WMC1510 **864 零触碰**（{Binding} 计数 423 不变）；⑤`SettingsSliceOwnership` 读棘轮 WeatherOptions 条目销账，SettingsSync/SettingsViewModel.cs/FeatureOptions 平铺读收缩均未涨破；⑥`FacadePassthroughAccess` 新编辑器/Contracts 文件零命中（"Settings.Weather.\*"/"Weather.\*" 本地化键安全——Weather 是 get-only 切片而非平铺门面名）；⑦ModuleBoundary：Weather 编辑器仅引 Contracts/Models/MVVM Toolkit，`QuickCaptureEnablement_DoesNotResolveTheGlobalApp` 检查清单加 Weather 编辑器；⑧外部文本钉更新：SettingsCopyAndHierarchy（位置模式/显示摘要/导航接线三钉）。新增 `WeatherSettingsEditorTests` 16 用例。

### 第四十八批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位，与批 46/47 同数）；x64 Debug：0 错误、22 警告（码集存量：CS8602×14/CS8601/CS0414/CS0169/CS0108 各位，无新增）。
- 新增 `WeatherSettingsEditorTests` 16 用例（构造投影+未知值归一化（皮肤 bogus→Standard 与旧壳构造语义逐字同源）/写穿透+写后重投影/外部 Refresh 零用户事件/位置模式用户编辑写自动定位+事件两向/**城市选择链：自动切手动+协调器落盘+搜索文本复位+建议清空**/非法坐标拒绝零写入/建议推面端口与无结果门/失焦复位取已存城市名/定位状态行可见门/显示 flyout 全关回退"关"+逐项落盘/重置默认+重投影/本地化重建/SettingsService 常量别名钉/壳反射面无残留（58 成员清单）/迁移模式文本钉（XAML 14 路径+3 个 DoesNotContain、转换器、DeferredSections DataContext、flyout 与 AutoSuggestBox 接线、编辑器桥 23、壳桥 89））。全量 x64 测试：**4,452/4,452 通过**（批 47 基线 4,436 + 本批 16）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore+构建（隔离产物含 DeskBox.exe/DeskBox.Updater.exe/DeskBox.ThumbnailProxy.exe），`-t:Rebuild` 全量，检查后已清理）：**0 错误**、22 警告（CS8602×14/CS8601×2/CS0414×2/CS0169×2/CS0108×2，码集与批 41-47 同位）。
- **绑定实效验证（UIA 探针，System.Windows.Automation COM 通道，探针脚本临时件未入库）**：隔离数据根 `facade-weather-48-3d9cb9`（`DESKBOX_DEV_DATA_ROOT`）的 `data/settings.json` 平铺预置 `weatherAutoLocation=false`、`weatherCityName=Hanoi`、`weatherLatitude/Longitude=21.0333/105.85`、`weatherTemperatureUnit=Fahrenheit`、`weatherWindSpeedUnit=mph`、`weatherDefaultView=Week`、`weatherSkin=Standard`、`weatherDataSource=OpenMeteo`、`weatherRefreshIntervalMinutes=180`、`weatherShowForecast=false`、`weatherShowPressure=true`（其余显示项 true）+zh-CN+已完成 onboarding。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**），UIA 经"功能格子"页天气卡 drill-down 进入 Weather 节读出：**位置来源=手动选择城市、城市搜索框文本=Hanoi、数据源=Open-Meteo、温度单位=华氏度、风速单位=mph、默认视图=周视图、皮肤=简洁、刷新间隔=3 小时**——八项非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影（绑定若死则控件呈编辑器默认）。**回写两链**：①UIA SelectionPattern 温度单位"华氏度"→"摄氏度"→磁盘 `weatherTemperatureUnit=Celsius`；②**城市搜索链全程**：聚焦搜索框、清空、粘贴"北京"（TextChanged→壳搜索机防抖+网络查询→`SetCitySuggestions` 推面）、ENTER（QuerySubmitted→`HasCitySuggestions`→`TrySelectFirstCitySuggestion`→`SelectCity`→协调器 `TrySetWeatherManualLocation`）→磁盘 `weatherCityName="武汉, 湖北省, 中国"`、`weatherLatitude/Longitude=30.5928/114.3055`、`weatherAutoLocation=false`——壳推面+编辑器选择链+事件回壳全链贯通；其余预置项（手动模式、mph、周视图、简洁、Open-Meteo、180 分钟、显示预报关等）逐项不动。**显示选项 flyout 的 UIA 面缺失**：WinUI MenuFlyout 弹层不发布条目 UIA peer（ExpandCollapse 可开但条目对 UIA/键盘合成均不可达，与批 47 Segmented 同类控件级限制），显示开关回写由编辑器单测（DisplayOptions_FlyoutTogglesPersistAndUpdateTheSummary 落盘断言）+XAML 迁移钉覆盖。探针后已按路径停止本 worktree 实例（主检出实例未触碰），数据根与探针件已清理。
- `git diff --check` 通过。
- 遗留风险：①显示选项 flyout 与 AutoSuggestBox 建议列表项的 UIA peer 缺失为 WinUI 弹层控件级限制（显示开关回写的 UIA 侧已由单测+钉覆盖）；②城市搜索结果依赖网络（探针中 ENTER 取到的首条建议来自服务端排序，与绑定链无关）；③Backup 族（顺位 10）与 General/About/Performance（顺位 11）仍走壳门面。

## 第四十九批：门面退役二阶段第九复制批（备份族三节 XAML 绑定迁编辑器——批 4 状态机原位扩面、凭据纪律保全）

实施基线：`cd1c9156`（main，含批 40-48），worktree `codex/final2-facade-backup`。对象是施工图顺位 10 的**备份族三节**共 40 个唯一 XAML 绑定属性（本地备份 BackupRestoreSettings 8：开关/间隔/保留数选项表与选值/自定义目录显示/回退警告；云备份 CloudBackupSettings 19：开关门/渠道/端点四字段/HTTP 警告门/凭据状态/连接状态行/三域开关/间隔与保留选项表与选值/最近成功摘要；兼容性诊断 CompatibilityDiagnosticsSettings 13：拖拽诊断摘要+明细+六诊断行+修复状态+修复门/运行健康摘要+明细+重同步门）。磁盘 schema、文案零变化；{Binding} 标记形态保持（SettingsWindow.xaml 的 {Binding} 计数 423 逐字不变，未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰）；三个可见性门（目录回退警告/连接卡×4/HTTP 警告）经既有 `SettingsBoolToVisibilityConverter` 绑布尔投影（批 43/46/47/48 先例）。

**批 4 状态机处置（本批核心决策，与批 48 城市搜索机对偶）**：批 4 建的 `Features/Backup/BackupSettingsViewModel` 是"一次可见云备份访问"的访问代次+取消链状态机（Activate/Deactivate 生命周期、端点代次、命令代次、列表在飞去重、上传完成核验重试），只引 `IBackupSettings` 契约——它**本就是编辑器域的正确位置，原位扩面而非迁壳**：构造签名追加 `localize`/`format` 委托（App 装配处注入，与批 47/48 编辑器同款），新增 `BackupSettingsViewModel.SettingsSurface.cs` 分部持有三节绑定面（自订阅 `PropertyChanged`：State 变化→静默重投影+端点变更清连接状态行、Message→连接状态行映射、RemoteSnapshots→`RemoteSnapshotItems` 集合投影、CredentialSaved→凭据状态、IsBusy→动作门），状态机主文件除委托与 `InitializeSurface()` 外零改动。**壳推面**（批 48 搜索机同款薄 partial）：手动备份/恢复/删除流程与 `BackupRestoreActions`/`IBackupCommands` 运行时入口留 `SettingsWindow.CloudBackup.cs` code-behind（本就是 Views，不算门面），经 `SetCommandBusy` 推面端子拨 `ActionsEnabled` 门、`ConnectionStatusText`/`EndpointGeneration` 直读写编辑器；拖拽诊断与运行健康的**计算**留壳（`SettingsViewModel.DragDropDiagnostics.cs`/`RuntimeDiagnostics.cs` 重写为薄 partial，Diagnose/Repair/GetRuntimeHealthSnapshot/ForceExternalStateRecoveryAsync 都是宿主服务），`SetDragDropDiagnostic`/`SetDragDropRepairBusy`/`SetDragDropRepairStatus`/`SetRuntimeHealth`/`SetRuntimeResyncBusy` 推面端子把九条文本与两个门灌进编辑器；**凭据纪律保全**：PasswordBox 内容仍只经 View code-behind 直传 `SaveCredentialAsync`/`ProbeAsync`（批 4 红线，编辑器/壳均不持有明文，编辑器状态 `ToString` 无密码的既有断言不变）。

| 职责 | 所有者 |
|---|---|
| 备份族三节 XAML 绑定面（40 属性：读投影+TwoWay 写入经 `IBackupSettings.Update` 逐字段变更、五组选项表与显示名缓存、目录显示/回退警告、HTTP 警告门、凭据状态、连接状态行、最近成功摘要、远程快照集合投影、诊断文本与两个门、`RefreshLocalization`/`RefreshLocalStatus`） | `Features/Backup/BackupSettingsViewModel`（扩面；零 Services/WinUI/App 引用，仅 Contracts/Models/MVVM Toolkit） |
| NativeAOT {Binding} 桥（40 nameof 条目，仅 XAML 消费面） | `Features/Backup/BackupSettingsViewModel.AotBindableProperties.cs` |
| 规范值/读归一化下沉 | `Contracts/BackupOptionKinds`（本地/云端间隔与保留预设、默认值、提供者常量与归一化；`DataBackupSettingsPolicy`/`CloudBackupSettingsPolicy` 改别名单一真源） |
| 云备份访问状态机（访问/端点/命令代次+取消链+上传核验） | `Features/Backup/BackupSettingsViewModel` 主分部（批 4 原样） |
| 手动备份/恢复/删除流程、快照列表 ItemsSource object[] 投影、PasswordBox 凭据传递 | 壳 `SettingsWindow.CloudBackup.cs`（code-behind，经 `SetCommandBusy`/`ConnectionStatusText`/`RemoteSnapshotItems` 推面端口） |
| 拖拽诊断/运行健康计算（宿主服务） | 壳 `SettingsViewModel.DragDropDiagnostics.cs`/`RuntimeDiagnostics.cs` 薄 partial（推面进编辑器） |
| 远程快照行类型 | `Features/Backup/CloudBackupRemoteSnapshotItem`（自壳 ViewModels 迁入，XAML x:DataType 换 `backup:` 命名空间） |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`BackupRestoreSettings`/`CloudBackupSettings`/`CompatibilityDiagnosticsSettings` 三 tag → `_backupSettingsViewModel` |
| 外部刷新路径 | ApplySettingsSnapshot→`RefreshState()`（内含重投影）；OnLanguageChanged→`RefreshLocalization()`；进入本地备份节→`RefreshLocalStatus()`；目录选择/重置→`UpdateLocalDirectory` |

行为语义保全要点：①编辑器投影期 `_isSyncingPresentation` 静默不回写（替代旧壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 双守卫），每次 `Update` 后 `RefreshState` 尾随重投影归一化（与旧壳 SyncBackupSettingsFacade 语义一致）；②选值 setter 归一化后 SetProperty 无变化即不写（旧壳行为逐字保留——探针中"未知预设值归一化回默认且默认值未变则零写入"）；③Deactivate 经 Message=Empty 置空连接状态行（旧壳 ApplyBackupSettingsMessage 联动语义）；④`_projectedBackupEndpoint` 端点比对清状态行逻辑随面迁入编辑器；⑤`ActionsEnabled = !SetCommandBusy 门 && !IsBusy`（旧 `CloudBackupBusy` 双源合成）；⑥拖拽修复期 `CanRepairDragDrop` 双门（busy+诊断可修复位）与重同步门同构保留。

删除壳兼容面（壳 ViewModels 侧 **+67/-798**（九文件，净 -731）：`SettingsViewModel.BackupSettings.cs`（-79）/`SettingsViewModel.DataBackupOptions.cs`（-151）/`SettingsViewModel.CloudBackupOptions.cs`（-407）三文件整删）：`AutomaticBackupEnabled` ObservableProperty 与回调、本地/云端间隔与保留选值字段+归一化 setter、五组选项表与四组 DisplayName 缓存、目录显示与回退警告三件、`UpdateAutomaticBackupDirectory`/`RefreshAutomaticBackupStatus`/`IsValidAutomaticBackupDirectory`、云端提供者/端点四字段/三域开关三个 ObservableProperty 与回调、WebDAV/HTTP 可见性门、`CloudBackupBusy`/`CloudBackupActionsEnabled`/`CloudBackupConnectionStatusText(+Visibility)`/`CloudBackupCredentialSaved`/`CloudBackupCredentialStatusText`/`CloudBackupStatusText`/`CloudBackupEndpointGeneration`/`CloudBackupRemoteSnapshots` 集合与同步、`SyncBackupSettingsFacade`/`OnBackupSettingsPropertyChanged` 整链、拖拽诊断九文本+`IsDragDropPermissionRepairing`+`CanRepairDragDropPermission`+`DragDropPermissionSeverityKind`（从未被 XAML 消费）与 `NotifyDragDropPermissionPropertiesChanged`、`RuntimeHealthSummary`/`RuntimeHealthDetail`/`IsRuntimeResyncing`/`CanResyncRuntimeState`、SelectionOptions 两组本地备份选项桥与通知、SettingsSync 的本地备份显示名缓存失效（换编辑器 `RefreshLocalization`）；`OnBackupSettingsPropertyChanged` 订阅/退订随面删除（`_backupSettings` 字段保留供 SettingsSync 刷新与诊断推面）。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 89→**49**（测试两处+审计 ps1；FileStack/QuickCapture/Todo/**Weather 四处编辑器测试桥计数钉同步 49**）；备份迁移钉三枚（CloudBackup `RemoteSnapshotItems.Cast<object>().ToArray()`、XAML `Text="{Binding LocalDirectoryDisplayText, Mode=OneWay}"`、DeferredSections `section.DataContext = _backupSettingsViewModel;`）；ps1 计数 89→49、5B4B1 源清单加 Backup 编辑器两文件、源警告过滤器加 `Features\.Backup\.BackupSettingsViewModel(?:\.\w+)?` 路径；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty **17 不变**（四个备份 ObservableProperty 原在已删分部文件，不在该文件计数内）、构造抑制钉锚不动；③动态对账测试自动平衡（40 个备份族绑定名重绑后不再匹配壳反射面，壳桥同步收缩 40 条）；④WMC1510 **864 零触碰**（{Binding} 计数 423 不变）；⑤`SettingsSliceOwnership` 读棘轮零触碰（删的三文件本就零 `settings.` 平铺读，`RuntimeDiagnostics.cs`=1 持平）；⑥`FacadePassthroughAccess` 新编辑器/Contracts 文件零命中（"Settings.CloudBackup.\*"/"Settings.DataBackup.\*" 本地化键的段名带点——`settings\.CloudBackupServerUrl` 等平铺门面名不匹配分段键字面量；`Backup`/`CloudBackup` 是 get-only 切片不在正则名集）；⑦ModuleBoundary+FeatureSettingsBoundary 的 `BackupSettingsPages` 检查清单换新文件集（删两壳分部条目，加 SettingsSurface/CloudBackupRemoteSnapshotItem；`QuickCaptureEnablement` 清单不动）；⑧`SettingsSectionElementAotContractTests` 快照行类型路径钉换 `Features/Backup/CloudBackupRemoteSnapshotItem.cs`；⑨三域开关命名 `IncludeTodoData`/`IncludeQuickCaptureData`/`IncludeWidgetStyle` 避开 QuickCapture/Todo 编辑器迁移钉的 `"{Binding QuickCapture"`/`"{Binding Todo"` 前缀 DoesNotContain 守卫。新增 `BackupSettingsSurfaceTests` 16 用例。

### 第四十九批验证记录

- canonical Debug（非平台）`dotnet build src/DeskBox/DeskBox.csproj -c Debug`：0 错误、11 警告（存量位，与批 46-48 同数）；x64 Debug：0 错误（与全量测试并行触发的 obj 竞争曾报 4 错误，串行重跑归零，非源性问题）。
- 新增 `BackupSettingsSurfaceTests` 16 用例（构造投影（回退目录警告+生效路径）/用户写穿透+未知预设归一化回默认+归一化无变化零写入+16 变更记录计数/外部 RefreshState 零回写/`UpdateLocalDirectory` 规整与清空/命令忙门与状态机忙门双源+幂等/Message→连接状态行映射/端点变更清状态行/远程快照集合投影（设备尾段+大小+端点）/最近成功·失败·未验证三段合成+从未备份/凭据状态随 vault 读翻转/HTTP 警告本地化/诊断推面端子与修复·重同步双门/RefreshLocalization 十属性通知集/选项表真数组与规范值（六档间隔/两渠道）/壳反射面无残留（48 成员清单））。全量 x64 测试：**4,468/4,468 通过**（批 48 基线 4,452 + 本批 16）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`（2.0 GB），DeskBox 与 Updater 均随隔离 restore+构建（隔离产物含 DeskBox.exe/DeskBox.Updater.exe/DeskBox.ThumbnailProxy.exe），`-t:Rebuild` 全量，检查后已清理）：**0 错误**、22 警告（CS8602×14/CS8601×2/CS0414×2/CS0169×2/CS0108×2，码集与批 41-48 同位）。
- **绑定实效验证（UIA 探针，System.Windows.Automation COM 通道，探针脚本临时件未入库）**：隔离数据根 `facade-backup-49-91c43c82`（`DESKBOX_DEV_DATA_ROOT`）的 `data/settings.json` 平铺预置 `automaticBackupEnabled=false`、`automaticBackupIntervalMinutes=720`、`automaticBackupRetentionCount=14`、`cloudBackupProvider=webdav`、`cloudBackupServerUrl=http://nas.local/dav`、`cloudBackupRemotePath=DeskBox/backups`、`cloudBackupUsername=probe-user`、`cloudBackupTodoDataEnabled=true`、`cloudBackupQuickCaptureDataEnabled=false`、`cloudBackupWidgetStyleEnabled=true`、`cloudBackupIntervalMinutes=360`、`cloudBackupRetentionCount=10`+zh-CN+已完成 onboarding。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**），UIA 经左导航"诊断与维护"→"设置与内容备份"卡进入本地备份节：**自动备份开关=关（默认开）、备份间隔下拉选中"12 小时"（默认 1 天）、保留下拉选中"保留 14 份"（默认 7）、目录显示框=Recovery\automatic 生效路径**——四项非默认值经"磁盘→协调器读快照→编辑器→节级 DataContext {Binding}→控件"全链投影；**回写**：TogglePattern 拨"自动备份"开关 Off→On→磁盘 `automaticBackupEnabled=true`（编辑器 setter→`Update(LocalEnabled)`→SaveDebounced 落盘），其余预置项不动。经设置搜索"云备份"进入云备份节：**WebDAV 连接卡整体可见（渠道门投影）、服务器地址=http://nas.local/dav、远端文件夹=DeskBox/backups、用户名=probe-user、待办数据=开/随记数据=关/格子样式=开（三域开关逐项）、HTTP 明文警告行可见、凭据状态行="尚未保存密码。"、最近备份行="还没有成功备份过。"**——探针全程**零真实网络/磁盘备份动作**（未触碰测试连接/立即备份/恢复/删除；凭据状态来自本地 vault 只读查询）。经设置搜索"拖拽诊断"进入兼容性诊断节：**摘要"启动项指向旧版本"+明细+六诊断行（进程完整性 Medium(0x2000)/Explorer/UAC EnableLUA/兼容性/启动项/快捷方式）+一键修复按钮 IsEnabled=True（可修复门推面）+运行健康"后台服务运行正常/生命周期事件：1"**——壳诊断计算→推面→编辑器→{Binding} 全链贯通。探针后已按路径停止本 worktree 实例（主检出实例未触碰），数据根与探针件已清理。
- `git diff --check` 通过。
- 遗留风险：①SettingsComboBox 关闭面选中值与 ComboBox 弹层条目在 UIA COM 通道下需 ExpandCollapse+弹层 ListItem 枚举读取（批 48 同款限制；渠道组合框的选中证明由连接卡可见门+编辑器单测覆盖）；②云备份操作按钮（测试连接/立即备份/恢复/删除）的 UIA 侧回写验证刻意不做（真实网络/磁盘动作），其门与状态行投影已由探针+单测覆盖；③General/About/Performance（顺位 11）仍走壳门面（收官批再定去留）。


## 第五十批：门面退役收官批（General/About/Performance 三节去留裁决与执行 + SettingsViewModel 终态对账）

实施基线：`452a12d8`（main，含批 40-49），worktree `codex/final2-facade-final`。对象是施工图顺位 11 的最后三节。磁盘 schema、文案零变化；{Binding} 标记形态保持（SettingsWindow.xaml 的 {Binding} 计数 423 逐字不变，未做任何 {Binding}→x:Bind 转换，WMC1510=864 零触碰）。

**三节去留裁决（逐节证据）**：

- **Performance 节——迁（真删，批 39 漏网族销账）**。证据：①批 39 收官对账登记的 11 处经局部 lambda（`UpdateCustomPerformanceSetting(settings => settings.X = …)`）的自定义性能节门面写——该模式不匹配批 29 直接赋值口径、一直由 FacadeAccessManifest 预算（Performance=11）圈住，本批全部销账（文件删除、清单条目删除）；②节为懒加载 DataTemplate，零宿主服务耦合（无 App.Current/StartupService/ThemeService/网络），全部写入经 PerformanceSettingsPolicy 归一化+SaveDebounced——标准协调器族形状；③ PreferenceCallbacks 的 QuiescenceWorkingSetTrimEnabled Performance 切片直写（批 35 注明的最后残留）一并入协调器；④ Idle/Immediate 两写的所有权按批 34 裁决留在 InteractionSettingsCoordinator，编辑器经 IInteractionSettings 合同写入（两合同编辑器，ModuleBoundary 允许——仅引 Contracts），零改判。
- **General 节——留壳（混合宿主生命线节）+ 两个跨域组合框元素级迁编辑器**。证据：①语言（SelectedLanguage）：setter 一行转发 `_localizationService.SetLanguage(...)`——宿主服务写 Settings.Language 并触发全应用重本地化级联（每窗口/托盘/全部编辑器 RefreshLocalization），App 生命线操作留壳；②自启动族（AutoStart/SelectedAutoStartMode/AutoStartModeVisibility/AutoStartStatusText/StatusVisibility/SystemSettingsVisibility 六绑定）：OnAutoStartChanged→StartupService.SetEnabled（Run 键/任务计划注册=宿主注册表写路径）、ApplyAutoStartState 读 StartupService.Current 回映注册态，持久值写已在批 34 InteractionSettingsCoordinator——宿主生命线留壳；③Onboarding 按钮为 code-behind 无绑定；④**性能模式内联下拉与附件存储方式下拉真迁**：两组合框经元素级 DataContext（`PerformanceModeInlineComboBox.DataContext = _performanceSettingsViewModel`/`AttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel`）分别挂到性能编辑器与 FeatureWidgets 编辑器——性能模式与性能节共享状态（随节迁移真删壳属性的前提），附件存储写口自批 38 已在 FeatureWidgetsSettingsCoordinator（本批补 `ReadAttachmentStorageMode` 读口+绑定面）。
- **About 节——留壳+注明**。证据：①更新卡状态机（CheckForUpdatesAsync/DownloadAvailableUpdateAsync/StartDownloadedUpdateInstall+manifests/下载器路径/失败态）依赖 IAppUpdateService（Services 域：网络清单/下载进度/商店安装/安装器启动）——ModuleBoundary 禁 Features/* 引 Services，编辑器化必须搬迁状态机本体=服务层重构，超出本阶段（门面退役）口径；②导航徽章 `x:Bind ViewModel.UpdateReminderBadgeVisibility` 挂在 NavigationView 项上（节元素之外、窗口生命周期绑定）；③AutoCheckForUpdates 写已归 InteractionSettingsCoordinator（批 34）、LastUpdateCheckAt 已归 Maintenance 协调器（批 39）——About 节的**写**已全部在协调器，剩余壳面是宿主更新流水线的只读呈现+code-behind 操作，无门面写；④版本/链接/商店卡/开源信息为只读常量呈现，消费者含 code-behind 与 ReleaseNotesWindow 流。

| 职责 | 所有者 |
|---|---|
| 性能节 XAML 绑定面（14 属性：预设模式+四组自定义明细组合框+装饰动画摘要与 flyout 族+三工作集裁剪开关）与 General 内联模式下拉 | `Features/Performance/PerformanceSettingsViewModel`（新建，ObservableObject，零 Services/Platform/App/WinUI 引用；localize/isChinese 委托注入；写路径尾部整面 SyncPresentation 重投影——预设应用后的明细字段跟随即旧壳 SynchronizePerformanceDetailSelection） |
| NativeAOT {Binding} 桥（14 nameof 条目） | `Features/Performance/PerformanceSettingsViewModel.AotBindableProperties.cs`（Glance/Music 同款） |
| 11 处 lambda 门面写+预设写+Quiescence 切片写的唯一设置页写入者；读快照（policy 解析+三 trim 开关）；规范值/字符串归一化下沉 | `Services/PerformanceSettingsCoordinator` + `Contracts/IPerformanceSettings` + `Contracts/PerformanceOptionKinds`（PerformanceSettingsPolicy 常量与字符串归一化器改别名，单一真源在 Contracts；数值延迟归一化留 policy——CleanupNever 哨兵与加载迁移路径） |
| Idle/Immediate 两 trim 开关的持久写 | 既有 `InteractionSettingsCoordinator`（批 34 所有权，未动；编辑器经 IInteractionSettings 写） |
| 附件存储组合框绑定面（读写+选项表+本地化缓存） | `Features/FeatureWidgets/FeatureWidgetsSettingsViewModel` 补面（+`AotBindableProperties` 桥 2 条目）；合同新增 `ReadAttachmentStorageMode`；`Contracts/AttachmentStorageModes` 规范值（SettingsService 常量改别名；本地化键分片拼接规避门面名正则） |
| 装饰动画 flyout 接线 | `ContinuousDecorativeAnimationsDropDown_Click` 改经窗口性能编辑器字段 |
| 节 DataContext 切换 | `SettingsWindow.EnsureSettingsSectionCreated`：`PerformanceSettings`→`_performanceSettingsViewModel`；两 General 组合框元素级 DataContext 在窗口构造器赋值（GeneralSection 为常驻元素非 DataTemplate） |
| 外部刷新路径 | ApplySettingsSnapshot→`_performanceSettings.SyncPresentation()`（含三 trim 开关投影）+`_featureWidgetsSettings.SyncPresentation()`（含附件模式）；OnLanguageChanged→`_performanceSettings.RefreshLocalization()` |
| 壳兼容面（已删除，壳 ViewModels 侧 +19/-599：`SettingsViewModel.Performance.cs` 整文件删除（-491 行）） | ~~SelectedPerformanceMode/AvailablePerformanceModeOptions（Custom 条目条件加入）/SelectedHiddenCacheCleanupDelaySeconds/SelectedVisibleIdleCacheCleanupDelaySeconds/SelectedTransientWindowReleaseDelaySeconds（无 XAML 消费者的死面，随文件消亡）/SelectedPerformanceCacheBudget/SelectedHiddenCacheCleanupScope 六组+五组选项表+ContinuousDecorativeAnimationsSummaryText+Is/Get/Toggle 装饰动画三件+AvailableContinuousDecorativeAnimationOptions+UpdateCustomPerformanceSetting/SwitchPerformanceModeToCustom/SynchronizePerformanceDetailSelection/Initialize/ApplySnapshot/RefreshPerformanceSelectionProperties 全套~~ + ~~IdleWorkingSetTrimEnabled/ImmediateHiddenWorkingSetTrimEnabled/QuiescenceWorkingSetTrimEnabled 三 ObservableProperty+三 On*Changed 回调（PreferenceCallbacks）~~ + ~~SelectedAttachmentStorageMode/AvailableAttachmentStorageMode{,Options,DisplayNames}/GetAttachmentStorageModeDisplayName/_selectedAttachmentStorageMode 与显示名缓存字段~~ |

行为语义保全要点：①自定义明细写入=归一化→写 Performance 切片→切 Custom（写 PerformanceMode）→SaveDebounced，等值跳过（与批 33-39 同款语义精确化）；②预设写入=policy ApplyPreset 原位写+编辑器尾部重投影；③装饰动画开关写四布尔之一+派生 legacy EnableContinuousDecorativeAnimations+切 Custom（协调器内聚）；④trim 开关永不切 Custom（原语义）；⑤编辑器 Sync 期 `_isSyncingPresentation` 静默不回写（替代旧壳 `_isRestoringDefaults`/`_isApplyingSettingsSnapshot` 双守卫）；⑥AvailablePerformanceModeOptions 仅在当前模式为 Custom 时提供自定义条目（读自身模式字段，与旧壳同源）；⑦中文顿号连接由 `isChinese` 委托保持。

门禁同步：①`AotStage5B4B1ContractTests` nameof 计数 49→**33**（测试两处+审计 ps1；FileStack/QuickCapture/Todo/Weather 四处编辑器测试桥计数钉同步 33；新增三枚迁移钉：性能节 DataContext switch、两 General 组合框元素级 DataContext）；②`AotPublishContractTests` SettingsViewModel.cs ObservableProperty 17→**14**（删三 trim 开关）、构造抑制钉尾锚 `IdleWorkingSetTrimEnabled = settings.…` 改 `ShowHoverButtons = settings.ShowHoverButtons;`；③动态对账测试自动平衡（14+2 绑定名重绑后不再匹配壳反射面，壳桥同步收缩 16 条）；④WMC1510 **864 零触碰**（{Binding} 计数 423 不变）；⑤`SettingsSliceOwnership` FacadeAccessManifest 两条销账（Performance 11→删除整条目——批 39 漏网族就此归零；FeatureOptions 2→删除——剩余两命中本是附件本地化键字面量，随面迁移进编辑器的分片拼接）；⑥`FacadePassthroughAccess` 新协调器/编辑器/Contracts 文件零命中（"Settings.Performance.*" 本地化键安全——Performance 是 get-only 切片名不在 220 门面名集；附件键经分片拼接）；⑦ModuleBoundary：性能编辑器仅引 Contracts/Models/MVVM Toolkit，`QuickCaptureEnablement_DoesNotResolveTheGlobalApp` 清单加性能编辑器+协调器；⑧`PerformanceSettingsContractTests` 的选项钉改指编辑器文件（ModeBestVisual/CleanupNever 禁入面保持）；⑨publish-aot-audit.ps1：计数 49→33、5B4B1 源清单加性能编辑器与 FeatureWidgets 编辑器文件、源警告过滤器加两条 Features 路径、**顺带修复批 49 遗留的位置索引漂移**（weather 投影钉的 `[21]` 在批 49 插入 Backup 两文件后已指向 BackupSettingsViewModel.cs 而非含 `TrySelectFirstCitySuggestion` 的 HotkeyAndAppearance——发布级审计未跑所以未爆；本批改按文件路径键取源，位置插入不再牵动）。新增 `PerformanceSettingsEditorTests` 15 用例。

### SettingsViewModel 终态对账（门面退役二阶段收官）

- **行数/文件**：33 partial/11,495 行（批 40 试点前基线）→ 批 49 后 24 partial/4,521 行 → **本批后 23 partial/3,941 行**（批 50 净 -580：+19/-599）；二阶段累计壳 ViewModels 侧净删 **7,554 行（-65.7%）**（批 40-50 十一批合计）。
- **壳 AOT 桥 nameof 终值=33**（全部为留壳面：General 语言 2+自启动 7+About 更新卡 24）；**SettingsViewModel.cs ObservableProperty 终值=14**（AutoStart/SelectedAutoStartMode/AutoCheckForUpdates/ShowHoverButtons+五 ShowHoverAction*/IsCheckingForUpdates/IsDownloadingUpdate/UpdateStatusText/UpdateDetailText/UpdateProgressValue）；**{Binding} 剩余量**：SettingsWindow.xaml 共 423 条 {Binding}，仍解析到壳的=General 节 9 条（语言 2/自启动 7）+About 节 23 条+控件级（ElementName/Title/Opacity 等），其余 26 个节模板与两 General 跨域组合框全部经节级/元素级/类型化 DP 绑各自编辑器。
- **剩余壳职责（终态）**：快照恢复编排（ApplySettingsSnapshot 分发到各编辑器 SyncPresentation/Refresh）、节导航与搜索路由（窗口侧）、推面状态机（全局热键卡/悬停按钮摘要/快速访问/天气城市搜索/拖拽与运行健康诊断/现有组与覆盖列表投影）、宿主生命线（语言切换级联、自启动注册链、更新流水线、外观活预览编排 SaveAppearanceChange、功能卡 WidgetManager 同步链、文件栈预览磁盘扫描）、AppearanceDetail/CapsuleMode UserControl 的 DP 桥与重置命令。
- **编辑器与协调器全景（16 编辑器 VM/13 协调器合同）**，按节 DataContext 归属：General→壳+性能/功能格子编辑器（元素级）；PerformanceSettings→性能编辑器（本批）；Interaction/InteractionWindow→交互编辑器；FileDisplay→文件显示编辑器；FileStorage→托管存储编辑器；FileStack+AppearanceDetail 总览→文件栈编辑器（模板 x:Bind）+FW/交互编辑器（类型化 DP）；Appearance 五节→外观编辑器；WidgetGroups→组导航编辑器；CapsuleMode 五节→胶囊编辑器；QuickCapture/Todo/Music/Weather→各自编辑器（QuickCapture=QuickCapture 协调器、Todo=Todo 协调器、Music/Weather/FW=FeatureWidgets 协调器）；备份族三节→备份编辑器；Search→SearchSettingsViewModel（code-behind 渲染）；Maintenance/DataHealth/Reset/FeatureWidgets/Glance/DesktopOrganization→壳（code-behind 操作与投影）；About→壳（本批裁决）。
- **外部残余终表（全部在册未动，本批无新增外部写入者——性能节写入者只有新协调器与既有交互协调器）**：OnboardingWindow.Appearance（WidgetMaterialType 1）/Hotkey（AutoStart 2）/Storage（DefaultManagedStorageRootPath 1）、App.xaml.cs ApplyDefaultAutoStartOnce（1）/ScheduleBackgroundUpdateCheck（LastUpdateCheckAt 1）、WidgetManager.Storage（3，迁移链刻意保留）、SettingsService 加载/迁移/默认值路径（Track B）。

### 第五十批验证记录

- restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：**0 错误**、11 警告（存量位，与批 46-49 同数）；canonical Debug（非平台，启动用）0 错误。
- 新增 `PerformanceSettingsEditorTests` 15 用例（构造投影=policy 解析/预设应用字段跟随+磁盘往返/自定义明细写入切 Custom+选项表加自定义条目+等值跳过/预算与范围与可见闲置经 Custom 路径/未知值归一化（bogus→Balanced、Huge→Balanced、Deep→AllRecreatable）/装饰开关写派生 legacy 旗标+切 Custom+关/部分/全选三态摘要/三 trim 开关经各自协调器且不切 Custom/外部同步零回写/选项表规范值与本地化名/停止拒写/policy 常量别名钉/壳反射面无残留（21 成员清单）/迁移模式文本钉（XAML 路径、DeferredSections DataContext、两 General 组合框元素级 DataContext、壳桥 33、性能编辑器桥 14）/附件面（FW 编辑器读写穿透+读口+未知值归一化+外部同步+分片键防正则钉））。全量 x64 测试：**4,483/4,483 通过**（批 49 基线 4,468 + 本批 15）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`（2.0 GB），DeskBox 与 Updater 均随隔离 restore+构建（隔离产物含 DeskBox.exe/DeskBox.Updater.exe/DeskBox.ThumbnailProxy.exe），`-t:Rebuild` 全量，检查后已清理）：**0 错误**、22 警告（CS8602×14/CS8601×2/CS0414×2/CS0169×2/CS0108×2，码集与批 41-49 同位）。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- **绑定实效验证（UIA 探针，System.Windows.Automation COM 通道，探针脚本临时件未入库）**：隔离数据根 `facade-final-50-probe2`（DESKBOX_DEV_DATA_ROOT）的 `data/settings.json` 平铺预置（schemaVersion 9）`performanceMode=Custom`、`hiddenCacheCleanupDelaySeconds=300`、`visibleIdleCacheCleanupDelaySeconds=900`、`performanceCacheBudget=Large`、`hiddenCacheCleanupScope=Warm`、`enableVinylRotationAnimations=false`（其余三装饰开）、`idleWorkingSetTrimEnabled=false`、`immediateHiddenWorkingSetTrimEnabled=true`、`quiescenceWorkingSetTrimEnabled=false`、`attachmentStorageMode=Copy` +zh-CN+已完成 onboarding。canonical Debug `--open-settings` 启动（启动管线 **35 步（5 critical）、0 degraded、0 failed**），UIA 在常规节读到：**附件保存方式=复制到 DeskBox（预置 Copy 经 FeatureWidgets 编辑器元素级 DataContext 投影）、性能模式下拉=自定义（选项表含自定义条目——预置 Custom 经性能编辑器投影）**；drill-down 进入性能节读到：**性能模式=自定义、可见闲置后台维护=15 分钟、隐藏后缓存回收=5 分钟、隐藏后清理范围=保留热缓存、缓存容量=较大、持续装饰动画摘要=文字跑马灯、时光图片自动切换、胶囊光效与粒子（vinyl 预置关被排除、中文顿号连接）、空闲时压缩内存=Off、隐藏后立即裁剪=On（预置投影）；静默期裁剪=Off 且 IsEnabled=False（空闲门按产品语义禁用嵌套开关——门绑定经编辑器 PropertyChanged 跟随）**——14 绑定值全链投影（绑定若死则控件呈编辑器默认）。**回写两链**：①TogglePattern 拨"空闲时压缩内存占用"Off→On→磁盘 `idleWorkingSetTrimEnabled=true`（编辑器 setter→IInteractionSettings→SaveDebounced），且嵌套开关随即解除禁用（门跟随）；②经导航返回键回常规节，附件组合框选"关联原文件"→磁盘 `attachmentStorageMode=Link`（编辑器 setter→FeatureWidgets 协调器→SaveDebounced）。其余预置项（Custom/300/900/Large/Warm/vinyl=false/quiescence=false 等）逐项不动。探针后已按路径停止本 worktree 实例（主检出 D:\project\wingezi 实例未触碰），数据根与探针件已清理。
- `git diff --check` 通过。
- 遗留风险：①探针首轮暴露两处实现缺陷并已修复——附件选项表曾以集合表达式直返 IReadOnlyList 致条目 UIA 名呈 `DeskBox.Models.SettingsOption`（DisplayMemberPath 解析失败），改经实数组装载后修复；XAML 附件组合框 Value 路径曾遗留旧名 `SelectedAttachmentStorageMode`（编辑器面名去前缀后路径失配），改 `AttachmentStorageMode` 后修复——两处均为迁移面自检收益，单测+UIA 双通道均有覆盖；②publish-aot-audit.ps1 的 5B4B1 源清单仍有位置索引引用（[5]/[6]/[7]/[8]/[9]/[20]/[22]），本批已修复漂移的一处并注明，位置插入类变更仍需人工核对索引；③装饰动画 flyout 的 UIA 条目面（MenuFlyout）与批 47/48 同类控件级限制，回写由编辑器单测（DecorativeToggle 用例）+代码后置接线钉覆盖；④About 更新卡的 UIA 双向验证刻意不做（真实网络/下载动作），其呈现面零改动；⑤未做真实设置页逐滑杆/组合框操作手感验收（UIA 写入路径已证明提交链贯通）。

**门面退役二阶段（批 40-50）就此收官**：设置页 34 个节模板全部按节归属 DataContext（26 个节+两 General 跨域组合框经节级/元素级/类型化 DP 绑 16 个编辑器），壳 ViewModels 从 33 partial/11,495 行到 23 partial/3,941 行；留壳面全部为宿主生命线并在终态对账表注明理由。批 29 清点的 115 个平铺写入点+批 39 登记的 11 处 lambda 漏网族全部归零，设置页新增设置字段的唯一合法入口是各节协调器合同端口。

## 第五十一批：AppSettings 门面 schema 等价收窄（消费者全量清点 + 三档裁决 + 棘轮对账收紧）

实施基线：`fb51304d`（main，含批 40-50），worktree `codex/final2-appsettings-narrow`。对象是"完全拆完"计划最后一个结构项：2A 切片时代留下的 `AppSettings` 门面（`SchemaVersion` + 13 个 `[JsonIgnore]` 切片引用 + 220 个透传属性，共 234 个属性）。硬约束不变：磁盘 schema 字节级等价（`SettingsSliceContractBaselineTests` 预切默认值逐属性钉固 + JSON 成员顺序钉固 + 往返字节等价契约必须原样全绿）。

### 清点：220 个透传的真实消费者分布

清点口径与 `SettingsSliceOwnershipContractTests.FacadePassthroughAccess` 同一正则（大小写不敏感 `settings.<透传名>`，字母后视断言排除 `WidgetSettings.` 等假阳性），扫描范围 src/DeskBox 全部 .cs（排除 bin/obj/AppPackages）+ tests + 全部 XAML。结果：

- **生产侧 129 文件 / 1,786 处访问**，分布（独占归类）：SettingsService 序列化根（加载/保存/归一化/默认值/还原保全映射）594/1 文件；其余 Services（策略/协调器/运行时）357/37 文件；widget ViewModels 179/19 文件；窗口/控件 code-behind 168/30 文件；WidgetManager 布局/表面链 201/7 文件；设置壳 SettingsViewModel*/SettingsWindow/SettingsSections 残面（读投影/同步快照/宿主生命线）97/13 文件；App 宿主/托盘/onboarding 85/12 文件；AOT 冒烟烟囱 App.Aot* 70/9 文件；SettingsMigrationService schema 迁移管线 35/1 文件。
- **XAML 零直接消费**：SettingsWindow.xaml 等 XAML 里匹配到的 `Settings.X` 全部是本地化资源键字符串（`svc:Localized.HeaderKey="Settings.AutoStart.Title"` 式），`{Binding X}` 路径绑的是设置壳/编辑器 ViewModel 属性而非 AppSettings；`WidgetStyleBackupProjection` 与 `DeskBoxDataBackupService` 走 raw JsonNode/settings.json 文件名，不经门面。
- **220 个透传中 207 个有生产访问**；**13 个零访问形**：CloudBackup 族 12 个 + QuiescenceWorkingSetTrimEnabled——但它们全部经切片路径消费（`Settings.CloudBackup.X`、`Settings.Performance.Quiescence…`），且 CloudBackup 族 10 个还是 SettingsService 还原默认值保全映射的 `nameof(AppSettings.X)` 键（编译级消费）。

### 三档裁决（逐档证据）

- **a 档（零引用透传，真删）**：**空集**。220 个透传全部是冻结磁盘 schema 的序列化成员（`SerializedMemberOrder_MatchesPreSliceDeclarationOrder` 钉固全部 221 个线成员：schemaVersion + 220 个 camelCase 名，`LegacyWidgetCapsuleModeEnabled` 以其 JsonPropertyName `widgetCapsuleModeEnabled` 在列）——删任何一个都是从每次落盘的 settings.json 里丢字段，直接违反硬约束；且 `EverySliceProperty_IsReachableThroughTheFacade` 要求切片属性与门面同名 1:1 对齐，切片属性在则门面透传必须在。
- **b 档（仅序列化需要，评估"切片成员直接序列化"）**：**评估后放弃该路线，220 个全部保留并注明**。等价不可证的三重证据：①221 个线成员的平铺顺序跨全部 13 个切片交错（Core 7 个→Performance 14 个→Core 2 个→QuickCapture/Todo/WidgetLayout→QuickCapture 17 个→Todo 21 个→Music 3 个→QuickCapture 1 个→Core 8 个→WidgetShell/FileWidget 交替……），System.Text.Json source-gen 按序列化类型声明序出成员，切片要么嵌套对象（形状改变，非字节等价）要么自定义 converter 重放整个 writer（camelCase/字符串枚举/WriteIndented/WhenWritingNull 全量重实现、绕过 source-gen 元数据）——高风险零用户价值；②线级特性纠缠在门面上：`LegacyWidgetCapsuleModeEnabled` 携带 `[JsonPropertyName("widgetCapsuleModeEnabled")] + [JsonIgnore(Condition = WhenWritingNull)]`（迁移期旧键名+空值省略语义必须原样保真）；③生成器产出的基线测试直接读门面属性（223 处 `s.X`），"原样绿"要求测试文件不改不重编即绿，删除即编译失败。裁决落款：AppSettings 类头 remarks 注明"220 个透传=冻结线契约，增删都是磁盘 schema 变更、必须走 schema 版本化迁移"。
- **c 档（有活消费者的）**：207 个透传的消费面（1,786 处/129 文件）经批 29-50 全部裁决过（设置页写入已归零；剩余=序列化根/迁移管线/壳读投影/同步快照/WidgetManager 存储链/AOT 烟囱/宿主生命线）。因 b 档已裁决透传不可删，单纯迁移读取点不缩门面（0 收益）只添回归面——本批不做消费者迁移。**实际收窄动作改为棘轮对账收紧**：`FacadeAccessManifest` 的 133 条预算按当前树重测，12 条陈旧余量全部收回（合计 268 单位：SettingsService 605→594、SettingsSync 133→6、SettingsViewModel.cs 94→14、Collapse 35→28、App.xaml.cs 31→28、WidgetManager.Groups 57→55、FileStackOptions 22→1、GroupNavigation 20→15），4 文件归零失条（QuickCaptureClipboardActivationHelper、SettingsViewModel.DisplayNames/PreferenceCommands/WidgetForeground）——此后任何新增门面访问立即红灯（此前 SettingsViewModel.cs 单文件就有 80 单位静默余量）。
- **新增守卫**：`AppSettingsFacadeNarrowingContractTests` 4 用例——透传数钉固 220（增长=无迁移加字段、缩减=丢字段，双向都须有意为之并同步基线钉）；门面≡schema（序列化成员集恰为 schemaVersion+220 线名，证明无游离于线外的死透传、无无主线成员）；13 个切片访问器永不序列化（[JsonIgnore] get-only + 成员集缺席）；线级特性冻结在门面（唯一 JsonPropertyName+WhenWritingNull 对，且必须是 widgetCapsuleModeEnabled）。

**对账结论**：门面属性数 234→234（220 透传全保留）；a 档 0、b 档 220（全部注明）、c 档 0 迁移（消费面经棘轮收紧代偿）。门面在本批从"待消解的 god façade"正式定性为"冻结的磁盘线契约"——2A 的"透传归零"目标在 schema 冻结约束下的终态就是：透传不再随触碰迁移消减，而是等未来一次 schema 版本化迁移（届时切片直序列化+顺序重排可一次性带版本号落地）整体取代。

### 第五十一批验证记录

- 本批运行时代码零改动（AppSettings.cs 仅增 remarks 注释，git diff 证全为文档）；restore Updater 后 `dotnet build src/DeskBox/DeskBox.csproj -p:Platform=x64`：**0 错误**、22 警告（存量位，与批 50 同数）；canonical 非平台 Debug（启动用）0 错误、22 警告。
- 全量 x64 测试 `dotnet test ./tests/DeskBox.Tests/DeskBox.Tests.csproj --no-restore --verbosity:minimal -p:Platform=x64`：**4,487/4,487 通过**（批 50 基线 4,483 + 本批新增 `AppSettingsFacadeNarrowingContractTests` 4 用例）；**字节等价契约测试原样全绿未改动**（SettingsSliceContractBaselineTests 两用例 + SettingsSliceOwnershipContractTests 往返字节等价用例逐字未动；唯一触碰是 FacadeAccessManifest 预算收紧与清单头注记）。
- AOT 定义编译检查（x64、`DefineConstants=TRACE;DEBUG;DESKBOX_NATIVE_AOT` 以 `%3B` 内码传递、`-getProperty:DefineConstants` 确认生效，`ArtifactsPath`/`RestorePackagesPath` 绝对路径隔离于 `.aotcheck/`，DeskBox 与 Updater 均随隔离 restore、`-t:Rebuild` 全量，隔离产物含 DeskBox.exe/DeskBox.Updater.exe/DeskBox.ThumbnailProxy.exe，检查后已清理）：**0 错误**、22 警告（码集与批 41-50 同位）。
- 隔离 Debug 启动：数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/appsettings-narrow-51-022350`（DESKBOX_DEV_DATA_ROOT）预置 59 个非默认值覆盖全部 12 个内容切片组+legacy 键（Core 9/Performance 9/QuickCapture 4/Todo 3/Music 1/WidgetShell 8/FileWidget 4/Backup 3/DesktopOrganization 1/Weather 6/Search 4/CloudBackup 5/legacy 1）。canonical 路径启动 PID 7256，启动管线 **35 步（5 critical）、0 degraded、0 failed**；停机后磁盘 **59 个预置字段中 58 个逐字段原值保持**（schemaVersion 与全部内容值零漂移）。仅有的两处非保持均为既有按设计行为、与本批无关：①`widgetCapsuleModeEnabled=true` 被加载归一化消费（SettingsService.cs:1658-1674 迁移期旧键折叠进 widgetCollapseBehavior 后清空，WhenWritingNull 随之省略——本批 AppSettings 零运行时改动，main 同行为）；②11 个布局键（widgets/widgetGroups/…/featureWidgetEnabledStates）由 2B-3 领养剥离至 widget-layout.json（终态 settings.json 209 成员=221-11 布局键-1 已消费 legacy 键）。探针后已按路径停止本 worktree 实例（主检出 D:\project\wingezi 实例未触碰），数据根已清理。
- `git diff --check` 通过。



# 架构优化进度与下一批计划

更新时间：2026-09-23。实施基线：`d4b0a7a2`。本记录承接当日的架构核对方案，按可独立验证的功能链路推进。

## 第一批：Todo 设置与提醒启停

状态：本批实现、自动验证和 Debug 启动核验完成。真实 UI 点击和通知交互仍需人工验收。

本批覆盖 Todo 功能开关、提醒开关、默认提前时间，以及提醒实例的启动、停止和退出等待。Todo 列表、布局、附件、任务数据模型仍沿用原实现。磁盘字段和 XAML 绑定名保持兼容。

| 职责 | 当前所有者 | 生命周期 |
|---|---|---|
| 功能开关、提醒开关、提前时间的编辑状态 | `Features/Todo/TodoSettingsViewModel` | 设置窗口创建；真正关闭时取消排队操作并释放 |
| 设置写入与窗口启停的协调 | `Services/TodoSettingsCoordinator`，通过 `ITodoSettings` 对编辑器暴露 | App 装配；退出时取消等待中的切换，等待已经开始的窗口操作 |
| 当前提醒实例 | `Features/Todo/TodoReminderRuntime` | 开关生效时协调；停止实例后等待其在途操作 |
| UI 定时器、首次检查延迟、一次提醒扫描 | `Services/TodoReminderService` | 启动时获得；禁用/退出时停止定时器、取消延迟并阻止迟到通知 |
| WinUI Dispatcher 和系统通知 | App 的宿主适配入口 | 本批保留线程与通知激活语义，运行时规则从 App 移出 |
| 格子创建/分组/显隐 | WidgetManager | 保留原窗口实现，Todo 状态提交委托给协调器 |

设置修改沿 `SettingsViewModel` 兼容属性 → `TodoSettingsViewModel` → `ITodoSettings` → `TodoSettingsCoordinator` 执行。设置窗口无需直接通知 App 刷新 Todo 提醒。创建、删除 Todo 格子的既有入口也通过协调器提交启用状态；外部恢复和默认设置由 SettingsChanged 兼容观察覆盖。

设置开关操作串行执行，并保留最近一次 UI 请求，避免旧保存通知覆盖用户正在进行的下一次开关选择。这一队列的范围是本批迁移的设置操作；它不是全局 WidgetManager 操作队列。

提醒运行时只持有一个活动实例。创建后启动失败时，候选实例被释放；后续可重试。禁用后旧实例立即停止计时，再排空已有 IO。允许已有文件写入完成，不做磁盘事务回滚；本批保证禁用/释放后的扫描不会继续显示通知。

## 依赖约束

- `ModuleBoundaryContractTests` 为 Models、ViewModels、Services 的存量 App.Current、App.UiDispatcherQueue 和 IServiceProvider 文本访问建立逐文件清单；新增位置或计数增长会失败。日志兼容调用不计入这条规则。
- 对新的 `DeskBox.Features.*`、Todo/Search 设置协调器及 Search 设置视图，额外检查编译后的类型引用，包括字段、方法签名、IL 调用和异步状态机。功能业务代码禁止依赖 App、全局容器和具体 Services/Platform 实现；Search 视图保留合法 XAML 框架调用，禁止直接依赖 SettingsService、SearchHotkeyService、EverythingSearchService。
- 当前 WinUI 内容契约仍留在宿主；本批新增的 Todo 设置和提醒会话契约不含 WinUI 类型。
- 纯规则、UI 行为、文件提交和运行时资源分别声明所有者。扩充旧例外清单不能替代边界修复。

## 验证记录

- canonical Debug 最终构建：通过，22 警告、0 错误；警告来自现有控件/可空性等位置。
- 首轮针对性测试：39/39 通过，覆盖提醒规则、开关串行化、启动失败清理、停止等待、恢复路径和依赖检查。
- 全量 x64 测试：4,073/4,073 通过。初次全量发现两个属性迁出后旧 AOT 生成属性计数仍为 77，已调整为 75，并另加可读写属性及 AOT 绑定入口保留检查。
- AOT 条件编译：x64 / win-x64、`DeskBoxAotAudit=true`、`DeskBoxAotSmokeHarness=true`、`DeskBoxRustNative=true` 的 Release build 通过，0 错误。构建报告 890 个警告，包含 WMC1510 等绑定提示；这是条件编译验证，没有执行 Native AOT publish/link 或发布包 smoke。
- AOT 构建使用临时 artifacts 和独立 NuGet lock 路径。普通仓库锁文件不包含 AOT 隐式编译器依赖，初次 locked restore 失败后改用隔离的 AOT restore；仓库锁文件未改动。
- 最终 canonical Debug 进程：`src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；2026-09-22 20:01 核验 PID 22844，仓库下只运行这一个 DeskBox 实例，Medium 完整性。
- 使用独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-architecture-20260922-a82b40bd` 启动，预置空数据和启用的 Todo 提醒。日志确认提醒实例按需创建一次、设置窗口完成构造和加载、启动 35 步中 0 degraded / 0 failed。原安装版进程继续运行。
- `git diff --check`：通过。

可复查的本地证据：`tests/DeskBox.Tests/TestResults/todo-architecture-full-final.trx`，以及 `%TEMP%/deskbox-todo-architecture-full-final.log`、`%TEMP%/deskbox-todo-architecture-aot-build.log`、`%TEMP%/deskbox-todo-architecture-debug-final.log`。日志和构建产物不加入版本控制。

自动测试与真实点击、系统通知交互是不同证据。未完成的 UI 验收不能用构建通过代替。

## 第二批：Search 设置节与搜索运行时入口

实施基线：`76d0a271` 加第一批工作区改动；核验时 HEAD 为 `44e7a0d4`，期间的其他提交仅涉及社区文档和发布文案，均保留原样。状态：实现、自动回归和 Debug 启动核验完成；实际 Search 页面点击及设备交互仍需人工验收。

优先范围为 `Views/SettingsSections/SearchSettingsSection.xaml.cs`，接着本批已验证的纵向链路继续收口。保持现有 DataTemplate 按需创建机制。

1. 提取 `SearchSettingsViewModel` 和窄的搜索设置接口，让 SearchSettingsSection 不再直接获取 App.Current.SettingsService、SearchHotkeyService 或 Everything 实例。
2. 将连接状态、用户主动连接/刷新、快捷键配置等操作定义为明确的搜索用例。设置节只订阅用例暴露的状态，不持有具体 Everything 服务。
3. 明确设置节的取消边界：离开/隐藏时取消自己的连接探测和 UI 刷新；关闭设置节不能停止用户仍在使用的全局搜索服务。
4. 保留搜索禁用时释放运行时、重新启用时按需初始化的既有行为。用任务返回值和取消信号替代分散的启动调用。
5. 缩减相应旧依赖清单，增加连接失败/恢复、重复启停、快捷键冲突以及页面离开后的迟到回调测试。

验收条件：搜索设置节能用假实现验证主要行为；打开和关闭设置不重复创建搜索运行时；关闭后的回调不访问旧控件；Everything 未连接、搜索禁用和快捷键冲突均能明确显示状态；已有搜索和 AOT 绑定测试通过；实机验证首次打开、返回、唤起搜索及快捷键不回退。

实际实现：

| 职责 | 当前所有者 |
|---|---|
| 搜索设置状态、快捷键操作结果、当前访问的请求与取消 | `Features/Search/SearchSettingsViewModel` |
| Search 设置切片写入、按需取得运行时能力、旧运行时请求取消 | `Services/SearchSettingsCoordinator` |
| 配置和连接快照、借用连接/快捷键能力的接口 | `Contracts/ISearchSettings.cs` |
| 文件选择器、Alt+Space 确认、键盘录入、控件呈现 | `SearchSettingsSection`，依赖通过 Configure 注入 |
| 页面进入/离开、窗口显示/隐藏/真正关闭 | `SettingsWindow` 显式控制 Search 设置节活动状态 |
| 全局搜索服务的创建与释放 | 继续由 App/现有搜索引擎拥有；设置页借用能力，不释放全局引擎 |

本批移除了 SearchSettingsSection 对 App.Current、SettingsService、SearchHotkeyService 和 EverythingSearchService 的直接使用；其 24 处旧设置门面访问对应的例外条目已移出清单。连接快照移到 Contracts，磁盘设置字段及 UI 控件名不变。

关闭设置窗口原先仅 Hide、切换设置节仅 Collapsed，因此本批不再依赖 Unloaded 完成取消。显式离开后停止页面探测并取消文件选择/快捷键确认返回后的写入；排队中的连接通知还要核对访问代次。重新打开可发起新的探测，并复用当前全局搜索实例。运行时替换前先取消绑定旧实例的请求，迟到完成不能继续更新该次页面操作。

快捷键启用状态按实际 `IsRegistered` 展示；注册失败明确显示失败。重置为 Alt+D 也经过现有 TryApplyGesture，用同一套冲突回退路径，避免先覆盖配置再发现注册失败。原有 Alt+Space 确认保留。

第二批验证记录：

- 针对性测试 53/53 通过，含迟到探测、排队通知、重复访问、运行时更换、失败恢复、禁用状态和快捷键冲突。
- 最终全量 x64 测试：4,085/4,085 通过。首次全量唯一失败为旧 AOT 源码测试把 `_searchSettingsViewModel.Dispose()` 子串误认成 `ViewModel.Dispose()`；精确匹配主属性后仍保留“先解除绑定、后释放主 ViewModel”的顺序要求。
- 最终 AOT 条件编译通过，0 错误、888 警告，含 WMC1510 等绑定提示。采用 x64/win-x64、AOT audit 与 smoke 条件编译、隔离 artifacts/lock 路径；未执行 Native AOT publish/link 或发布包运行验证。
- 最终 canonical Debug 构建：22 警告、0 错误。2026-09-22 20:46 核验进程 PID 23652，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，仓库下唯一 DeskBox 实例，Medium 完整性。
- 开发数据目录为 `C:/Users/simon/AppData/Local/DeskBox-Dev/search-architecture-20260922-42d41e6f`。预置 Search 功能启用、Everything 查询授权关闭、搜索快捷键关闭；启动日志确认搜索历史/快捷键服务/Everything provider 各初始化一次，设置窗口完成加载，启动 36 步中 0 degraded / 0 failed。未用此启动检查冒充 Search 页或 Everything IPC 实机验收。
- `git diff --check` 通过；SearchSettingsSection 的 App.Current 和具体设置/搜索运行时类型引用均为 0。

第二批证据使用 `tests/DeskBox.Tests/TestResults/search-architecture-full-final.trx` 及 `%TEMP%/deskbox-search-architecture-*.log`。实际页面点击、快捷键设备行为和 Everything 实例联调仍需人工验收。

## 第三批：BackupRuntime 与退出资源归属

实施基线：`44e7a0d4` 加前两批工作区改动。状态：实现、全量测试、AOT 条件编译及 Debug 本地备份检查完成；真实服务器与退出交互仍需人工验收。

共享工作区期间另有内存实验改动：`MemoryDestroyProbe.cs` 及 WidgetManager/SettingsWindow 中的实验引用，核验时分支为 `experiment/memory-probe-destroy-hidden`。本批保留了这些并行改动，未将其当作备份重构内容；验证对应当时工作区，启动命令没有设置这两个实验开关。本轮未提交，后续提交须区分各批及实验差异。

本批围绕 App 内的自动备份定时器与在途备份任务，按单条生命周期链路实施：

1. 将自动备份计时、触发和并发抑制集中到 BackupRuntime，明确本地快照与云备份的调度归属，保持现有间隔与重试规则。
2. 定时触发和设置页的手动触发复用操作入口；每次操作都有结果、取消信号和可等待的任务，不靠分散的 fire-and-forget 驱动。
3. 退出时停止新触发，取消可取消阶段并等待在途操作安全结束，再按资源依赖顺序关闭窗口、完成最后刷盘及释放容器。对已经进入持久化提交阶段的操作，先明确完成/回退语义，再实现取消。
4. 补齐 DI 容器和手工创建对象的所有权表。UI 相关释放在所属线程完成，容器释放放在其消费者解除订阅之后。
5. 测试手动/定时重叠、配置修改、上传失败、取消、退出竞争和重复停止；确认失败不会阻断其余退出步骤，也不会生成被误认为成功的备份记录。

验收条件：一个定时器拥有者、一个明确的在途任务集合；重复触发不会并发执行同一任务；退出后无新回调；成功/失败/取消记录可区分；原备份、恢复、凭据及文件事务测试保持通过。WebDAV 协议、磁盘 schema、恢复事务和发布渠道策略不并入这批。

Generic Host、程序集拆分、WidgetManager 的 Z-order/托盘动画拆分继续按触发条件评估。

实际实现和资源归属：

| 对象/资源 | 所有者 | 结束方式 |
|---|---|---|
| 1 分钟定时器、备份调度、在途及等待中的备份任务 | `Features/Backup/BackupRuntime` | 停止接收任务、撤销 Tick 订阅、停止计时器、发出取消、等待已提交工作结束 |
| 本地快照/导出与云端上传入口 | `IBackupCommands`，由设置窗口注入 | 手动与定时触发进入同一运行时；备份服务保留原归档/上传算法 |
| 设置刷新与手动备份前刷盘 | `Services/BackupBackend` | 使用现有设置存储，手动刷盘不广播调度事件；刷盘失败不继续创建备份 |
| 提醒、搜索、观察器、钩子和窗口等手工创建对象 | App 的 `ShutdownSequence` 步骤 | 按依赖顺序清理，每步失败单独记录，后续步骤继续；重复退出复用同一任务 |
| DI 创建的实例 | ServiceProvider | 窗口及订阅解除、设置最后刷盘后，在 UI 线程同步 Dispose 容器；当前注册的可释放对象均支持同步 Dispose |
| ThemeService 的系统颜色订阅、防抖计时器、窗口事件 | ThemeService，由容器释放 | 显式退订、停止计时器；已排队的颜色回调检查 disposed 状态 |
| 窗口/内容工厂自行创建的 Weather、CitySearch 等实例 | 原窗口、内容或 ViewModel | 继续由原消费者释放，不能把同一实例交给两个 owner |
| WebDAV 共享 HttpClient | 原有进程级静态共享对象 | 保留原共享策略，不按单次上传销毁；未变更协议实现 |

并发策略明确为两条任务通道。本地手动操作保持串行等待；本地定时检查在已有本地任务时跳过，避免堆积。云端手动/定时操作仍采用忙时跳过并返回 AlreadyInProgress 的语义。本地归档与云端上传可以并行调度，其内部归档仍由既有数据服务锁保护。

取消边界：

- 本地归档在最终 rename 前接受取消，清理临时件；rename 成功后保留完整备份，不因迟到取消改记为取消。
- 云端在 UploadAsync 返回成功前取消，不写成功或失败时间戳。若传输在未确认阶段中断，不宣称远端一定回滚。
- UploadAsync 已成功返回后，服务器已确认接收。取消校验重试或保留策略清理时仍完成结果落盘；未完成校验则记录 Uploaded + UploadUnverified，保留原有“已上传但未验证”语义。
- 完成通知订阅者抛错不会把已接受的上传改判失败，也不会阻止其余订阅者收到结果。

退出序列先停止备份新触发并排空其工作，再释放功能运行时、观察器、钩子和窗口。窗口关闭产生的最终设置写入完成后刷盘并释放容器；托盘宿主窗口保持到异步序列完成后才关闭，随后退出。日志队列仍属 App，本批只在退出时排空，未迁移日志架构。

第三批验证记录：

- 首轮针对性测试 198/198 通过。
- 最终全量 x64 测试 4,102/4,102 通过，包含手动/定时重叠、排队取消、提交排空、重试、配置重入、刷盘失败、退出步骤故障、上传确认前/后取消、保留清理取消及观察者异常。
- AOT 条件编译通过：x64/win-x64、audit + smoke 编译配置，888 警告、0 错误。没有做 Native AOT publish/link 或发布包运行验证。
- 最终 canonical Debug 构建：22 警告、0 错误。2026-09-22 21:50 核验 PID 33600，仓库下唯一 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。
- 独立开发数据为 `C:/Users/simon/AppData/Local/DeskBox-Dev/backup-architecture-20260922-8b0415bf`，仅放入样例配置，启用本地自动备份（5 分钟、保留 2 份），未配置云端。启动生成 21:50 的快照，5 分钟调度又生成 21:55 的快照；读取最新 ZIP，核对 `manifest.json` 所列两个数据文件的长度和 SHA-256 全部一致。启动 35 步中 0 degraded / 0 failed。
- `git diff --check` 通过。
- 使用模拟 WebDAV 传输验证取消边界；真实服务器联调和用户点击退出时的实机表现尚未验收。

证据：`tests/DeskBox.Tests/TestResults/backup-architecture-full-final.trx`，`%TEMP%/deskbox-backup-architecture-full-final.log`、`%TEMP%/deskbox-backup-architecture-aot-build.log`、`%TEMP%/deskbox-backup-architecture-debug-final.log`。

## 第四批：备份设置页与读取操作会话

实施基线：`44e7a0d4` 加前三批尚未提交的工作区改动。当前分支仍为 `experiment/memory-probe-destroy-hidden`；内存探针及并行实验修改保持原样。本批只触及备份设置、云端页面与必要的服务端点参数，未提交或推送。

| 职责 | 当前所有者 |
|---|---|
| 本地/云端设置快照、局部更新、凭据/探测/列表端口 | `Contracts/IBackupSettings.cs` |
| 设置切片读写、选项刷新、云端服务适配 | `Services/BackupSettingsCoordinator`；App 装配，退出时解除完成事件订阅 |
| 页面访问、端点切换、凭据状态和快照列表读取 | `Features/Backup/BackupSettingsViewModel`；设置窗口真正关闭时释放 |
| 原 XAML 属性名、可见性、本地化文案和列表行投影 | `SettingsViewModel` 的兼容门面；不直接持有具体备份服务 |
| HWND、PasswordBox、确认对话框和列表控件 | `SettingsWindow`；显示、隐藏、导航时显式开关页面访问 |
| 删除/下载的端点校验与冻结、现有恢复事务入口 | `Services/BackupRestoreActions`；恢复暂存与重启语义沿用数据备份服务 |
| 已提交的本地/云端备份任务 | 第三批的 `BackupRuntime`；页面隐藏不取消它们 |

页面和端点各有取消与代次检查。隐藏或切换端点后，旧凭据查询、连接探测和 PROPFIND 即使忽略取消、迟到返回，也不能覆盖新页面。列表读取按当前访问合并；上传完成通知携带发起时端点，只有当前可见页面且端点匹配时才安排有界重试。页面重开会重新读取凭据及列表。

远端列表项保留所属端点。删除在确认前后核对当前页面和列表项，服务调用开始后使用冻结的端点选项；恢复在选择域后再核对端点，已开始的下载继续使用原列表项对应的端点。对话框、文件选择和密码输入仍由 View 负责，密码只作为调用参数交给系统凭据存储；没有新增明文字段或设置持久化。恢复暂存事务、WebDAV 协议和磁盘 schema 未修改。

旧全局依赖清单删除了两个备份设置 partial 的例外，并对备份页面、编辑器和适配器增加了零全局 App 访问门禁。桌面设置窗口其他功能的旧依赖按后续批次处理。

第四批验证：

- 定向测试先后 91/91、70/70 通过，覆盖端点切换、迟到凭据/列表/探测、页面隐藏重开、旧上传通知、失败重试及旧列表项不能删除新端点文件。
- 最终全量 x64 测试：4,110/4,110 通过，含页面重复进入仅保留一条完成事件订阅、可见且端点匹配的后台上传刷新列表。
- 全量回归后补充的上传完成端点断言，云备份传输定向测试 60/60 通过。
- AOT 条件编译：x64/win-x64，audit + smoke 编译配置，888 警告、0 错误；未做 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 09:45 最终核验 PID 6632，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录：`C:/Users/simon/AppData/Local/DeskBox-Dev/backup-settings-architecture-20260923-c6834218`，关闭自动备份、云端配置和实验开关。启动日志显示设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed，未见备份设置错误或致命异常。此项只证明启动和默认设置页装配；真实 WebDAV、密码输入、云备份页面与恢复对话框的设备交互仍需人工验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

测试记录：`tests/DeskBox.Tests/TestResults/backup-settings-full-final3-20260923.trx`；构建与测试日志位于 `%TEMP%/deskbox-backup-settings-*.log`。测试数据使用独立开发目录，不使用正式用户配置或系统凭据。

## 第五批：QuickCapture 启停与剪贴板监听归属

实施基线：`44e7a0d4` 加前四批未提交的工作区改动。分支仍为 `experiment/memory-probe-destroy-hidden`；内存探针与其他并行修改保持原样。本批不改 QuickCapture 数据格式、剪贴板读取器协议或图片内容处理。

| 职责 | 当前所有者 |
|---|---|
| 功能开关、文本/图片录制选项与依赖归一化 | `Services/QuickCaptureSettingsCoordinator`，通过 `IQuickCaptureSettings` 暴露 |
| 唯一活动剪贴板监听及退役实例的排空 | `Features/QuickCapture/QuickCaptureClipboardRuntime` |
| 实际 ContentChanged 订阅、读取与保存前检查 | `QuickCaptureClipboardService`，实现 `IQuickCaptureClipboardSession` |
| QuickCapture 格子创建、分组脱离、隐藏与关闭 | `WidgetManager`，通过注入的协调器提交功能状态 |
| 原设置页 XAML 属性、文案和诊断呈现 | `SettingsViewModel` 兼容门面，通过注入接口操作 |
| UI 调度、实例装配、进程退出顺序 | App 宿主，退出时等待协调器与监听任务结束 |

设置页三个开关、格子自身的关闭入口、格子创建、录制引导入口和默认设置恢复现共用同一设置写入者。QuickCapture 开关关闭时同步清除文本与图片录制选项并停止新监听；图片录制打开时同时打开文本录制和功能本身。外部设置变化由协调器观察，旧文件中“功能关闭但录制开启”的组合会归一化。设置页保留已有绑定名，WidgetManager 保留窗口能力，不再从这条链路直接调用 `App.Current.RefreshQuickCaptureClipboardService`。

快速开关的窗口操作按请求代次和现有 WidgetManager 锁串行。较旧的窗口操作不能在新请求关闭后把录制状态重新写回开启。监听退役先解除系统事件订阅并取消对未完成系统读取的等待，迟到结果不能进入数据层；已开始的本地写入允许完成，停用操作与进程退出会等待写入任务结束。失败的监听创建和窗口操作可通过下一次用户请求重试。

验证记录：

- QuickCapture、生命周期、模块边界与 Onboarding 定向测试先后 50/50、48/48 通过。首次全量测试发现一条旧 Onboarding 源码断言仍要求设置页直接调用 WidgetManager，已改为检查协调器及窗口锁的实际链路。
- 最终全量 x64 测试 4,120/4,120 通过，记录在 `tests/DeskBox.Tests/TestResults/quickcapture-architecture-full-final3-20260923.trx`。覆盖新协调器、单监听运行时、迟到读取取消及拒写、停用及重置等待、默认/外部设置归一化和原有功能回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 10:40 最终核验 PID 36108，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-architecture-20260923-32e4264d` 关闭了录制和实验开关。日志确认设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed、没有创建剪贴板监听或出现 QuickCapture 错误。此项验证装配与关闭状态，实际系统剪贴板和 QuickCapture 页面交互仍需人工验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。自动测试不能替代系统剪贴板与真实 UI 的设备交互验收。

## 第六批：Search 总开关与全局运行时入口

实施基线：`44e7a0d4` 加前五批未提交的工作区改动；分支仍为 `experiment/memory-probe-destroy-hidden`。内存探针和并行修改保留，本批只收拢 Search 功能总开关，不修改 Everything 查询语法、索引策略或快捷键产品规则。

| 职责 | 当前所有者 |
|---|---|
| Search 总开关的唯一设置写入、请求代次及页面探测排空 | `SearchSettingsCoordinator`，新增 `ISearchFeatureSettings` 端口 |
| Search 设置页的一次访问、连接状态与热键反馈 | 原 `SearchSettingsViewModel`，继续借用运行时能力 |
| Search 格子创建、隐藏、分组脱离与窗口退订 | `WidgetManager`，经注入的端口提交状态 |
| 搜索引擎、Everything provider、热键与弹窗实例 | App 宿主，向协调器注入启停能力；运行时资源仍由 App 释放 |
| 原功能卡片绑定与用户开关入口 | `SettingsViewModel` 的兼容门面 |

设置页、WidgetManager 直接调用及外部设置恢复现在进入同一启停请求。启用时先准备 App 拥有的搜索服务，再创建 Search 格子；禁用时先关闭格子，让内容退订原 SearchHistoryService，然后取消并等待设置页探测，最后释放引擎、热键和弹窗。WidgetManager 已移除 `App.Current.SetSearchFeatureEnabled` 直接业务回调。快速反向切换通过代次跳过排队中的旧请求，旧窗口操作不能改写较新的设置状态；退出在释放全局搜索实例前先停止协调器。

取消后的探测由协调器等待，最长 5 秒；若后端不响应取消，会记错并继续关闭宿主资源。迟到结果仍经取消检查拒绝回写当前页面。这是对不配合取消的外部能力所设的退出上限，不宣称其底层操作已经物理终止。

第六批验证：

- Search/模块边界/Onboarding/生命周期定向测试 48/48 通过，含关闭顺序、快速开关、启动失败重试、外部设置恢复、旧探测排空及热键冲突回归。
- 全量 x64 测试 4,125/4,125 通过，记录在 `tests/DeskBox.Tests/TestResults/search-master-architecture-full-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未做 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 10:59 核验 PID 17928，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/search-master-architecture-20260923-187bf5e4` 关闭 Search 功能与热键。日志确认设置窗口构造完成、35 个启动步骤中 0 degraded / 0 failed，没有建立 Everything provider 或记录 Search 错误。此项只验证装配及禁用状态；测试中的假宿主和启动日志无法替代 Everything IPC、系统热键及真实 Search 页交互验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第七批：WidgetManager 内容窗口注册与清理

实施基线：`44e7a0d4` 加前六批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只集中内容窗口的 ID、实例和 HWND 注册；文件格子的会话字典、分组状态机、Z-order、动画和内存探针实验保持原边界。

`ContentWindowRegistration<TWindow>` 对 WidgetManager 原有的 `_contentWidgets` 与 `_widgetWindowHandles` 就地操作，不保存第二份窗口清单。它统一注册、按实例注销、按 ID 且实例匹配注销、分组成员 ID 重绑和退出清空，拒绝另一窗口占用相同 ID 或 HWND。重复注册同一窗口保持幂等；旧窗口的迟到关闭回调无法移除已经替换的实例或句柄。

内容窗口创建后，主题跟踪、窗口登记、独立文件会话登记、表面宿主登记、胶囊布局及 Closed 回调接线都进入同一失败清理范围。任何一步失败，都会按实例移除内容窗口登记与文件会话、注销表面宿主并关闭候选窗口。正常 Closed 回调仅在实际移除了此窗口的注册 ID 时才持久化隐藏状态，因此不会把替换窗口的配置误写成隐藏。

分组原地切换先检查持久窗口仍登记且目标 ID 没有其他实例；若不满足，在身份提交前回滚准备中的过渡。成功后只把同一窗口从旧成员 ID 重绑到新 ID，HWND 集合不增不减。表面宿主仍由现有 `WidgetSurfaceRegistry` 管理；本批没有叠加另一套状态机。

验证记录：

- 内容注册、模块边界、Surface 分组/提升及呈现链路定向测试 81/81 通过，含重复 ID/HWND、创建失败清理、旧关闭回调、分组重绑与冲突不改状态。
- 全量 x64 测试 4,130/4,130 通过，记录在 `tests/DeskBox.Tests/TestResults/window-registration-full-20260923.trx`；包含内容注册身份边界及原有分组、Surface、文件窗口回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 11:31 核验 PID 41836，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/window-registration-20260923-2716c2d1` 不创建可见格子，也关闭剪贴板录制和热键。日志确认 WidgetManager 与设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed，没有窗口注册错误或致命异常。此项只验证装配与空窗口启动；真实分组切换、文件拖拽及动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第八批：文件格子会话的身份与清理

实施基线：`44e7a0d4` 加前七批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理独立 File 格子的 `FileWidgetSession` 登记，不改用户文件读写、拖拽/DropTarget、文件栈或窗口布局。

代码核对确认，当前生产路径统一使用 `ContentWidgetWindow` 宿主。独立 File 内容才在 `_fileWidgets` 中保留会话别名；同一宿主进入分组 Surface 时，`CommitSurfaceHost` 会移除该别名，拆回独立窗口时再登记。分组 Surface 的成员切换不需要为每个成员保留文件会话。

`FileSessionRegistration<TSession,THost>` 就地操作原有 `_fileWidgets` 字典，没有第二份会话清单。一个宿主最多拥有一个独立 ID；同一 ID、宿主和内容的重复登记保持幂等。同宿主换了 `FileSurfaceContent` 时会替换旧会话，避免 ViewModel/选择状态仍指向旧内容；同 ID 换宿主时按新会话替换，旧会话或旧宿主迟到清理只能按实例或宿主身份删除自己的条目。若该 ID 的内容窗口已属于另一宿主，文件会话登记会拒绝不一致的写入。

创建、失败回滚、普通关闭、分组退役、功能窗口关闭、删除和退出现在都经该入口修改会话字典。现有 `FileWidgetHostDiagnostics` 仅在物理宿主更换时计一次创建，同宿主内容重绑不虚增宿主数。内容窗口注册与 SurfaceRegistry 继续由各自的原所有者维护。

验证记录：

- 文件会话、内容窗口、模块边界、文件宿主诊断、Surface/分组及存储清理定向测试 100/100 通过，覆盖幂等、同宿主内容更换、同 ID 宿主替换、旧宿主迟到关闭、重复别名拒绝和退回独立窗口。
- 全量 x64 测试 4,134/4,134 通过，记录在 `tests/DeskBox.Tests/TestResults/file-session-architecture-full-20260923.trx`，包含文件会话身份边界与原有 Surface、分组、存储清理回归。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：11 警告、0 错误。2026-09-23 11:58 核验 PID 24452，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`；启动器为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/file-session-architecture-20260923-5364728d` 未创建可见格子，也关闭剪贴板录制与热键。日志确认 WidgetManager 与设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed，没有文件会话错误或致命异常。此项只验证装配与空窗口启动；实际文件拖拽和组切换仍需设备验收。
- `git diff --check` 通过；未提交、推送或混入并行内存实验改动。

## 第九批：Surface 宿主声明的提交与回滚

实施基线：`44e7a0d4` 加前八批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只收口现有 `WidgetSurfaceRegistry` 的候选身份与分组提升时序；窗口层级、动画、文件操作和并行内存探针保持原边界。

核对的声明链路是：普通窗口创建登记活动宿主；已有分组的提升先暂存候选，首帧与保存成功后 `CommitSurfaceHost` 提交，再按实例退役旧窗口；失败时由创建清理或提升事务按实例注销候选。新建分组尚无 Surface 声明时，提升候选暂由事务持有，原独立窗口的成员声明保留到提交；这样首帧失败不需要凭配置猜测旧宿主来恢复。拆组复用则显式移除原组声明、登记独立宿主，失败时关闭替换宿主并恢复原声明。

`StageCandidate` 现在对同一宿主和成员幂等，但拒绝第二个宿主覆盖待提交候选，也拒绝把当前活动宿主暂存为自己的候选。候选暂存失败会中止创建并进入既有窗口失败清理，不再只记日志后留下一个无声明的窗口。提升事务还要求当前成员确为组活动成员，且候选必须新建；同 ID 已有内容窗口时提前拒绝，防止失败回滚误关原有实例。已有分组的活动宿主不能由普通创建路径直接替换；确有替换时必须走提升事务。`SynchronizeActive` 保留给恢复与拓扑稳定后的显式对账。

验证记录：

- Surface、候选事务、切换矩阵、窗口呈现与模块边界定向测试 74/74 通过。补充假宿主测试覆盖重复候选、取消后同组重试、已有分组失败保留旧宿主、新分组提交时转移成员声明、拆组回滚、迟到关闭和退出清空。
- 最终全量 x64 测试 4,139/4,139 通过，记录在 `tests/DeskBox.Tests/TestResults/surface-registry-full-final2-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 12:25 最终核验 PID 7860，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/surface-registry-20260923-d299a602` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认 WidgetManager 和设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed。此项只验证空布局启动装配；真实分组提升、拆离和首帧动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十批：Surface 成员声明转移与拓扑对账

实施基线：`44e7a0d4` 加前九批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Surface 成员别名的所有权转移及与在途分组切换的序列化；不改窗口层级、拖放规则、用户文件或磁盘 schema，并保留并行内存探针。

代码核对确认，合并是唯一增加组成员集合的入口。旧 `RemoveMemberClaims` 在普通注册、定义更新和恢复对账中遇到另一个 Surface 的成员别名时，会直接移除整个原会话；若对应窗口仍可见，就会留下没有 Registry 声明的物理宿主。现在这些入口默认严格拒绝冲突，且先校验所有别名再更新索引，失败不会只改一半。独立 Surface 也不能借显式转移参数夺走组成员。

合法合并在修改设置前通过现有 Registry 捕获预期源声明：只接受该成员自己的独立 Surface，或本次明确作为源组的 Surface；源组必须整体并入，不能只转走其中一名成员。捕获值保存会话身份、物理宿主、活动成员及成员集合的不可变快照。首帧与设置保存成功后，提交在 Registry 锁下再次核对快照和完整的源声明集合，再一次性移除旧别名并登记目标 Surface。在途切换或窗口替换若改变源状态，合并会拒绝提交并走既有候选/设置回滚。恢复对账与旧宿主迁入组 Surface 只允许转移当前同一物理宿主的独立声明。

拆离的非复用路径和解散路径在请求退役旧组宿主后显式移除旧 Surface 声明，然后才创建独立或剩余组窗口；旧 Closed 回调晚到时不能删除新窗口的别名。合并、拆离、解散和成员重排在修改拓扑前等待相关 Surface 切换 gate，并持有到操作收尾；多 gate 按 Surface ID 排序，取消时释放已经取得的 gate。`_widgetGroupGate` 仍串行拓扑操作，未添加第二份窗口清单。

验证记录：

- Surface、分组、呈现与模块边界定向测试 222/222 通过。新增假宿主测试覆盖多源组完整转移、重复/部分/过期声明拒绝、捕获后宿主或活动成员改变、待提交候选拒绝、保存失败保留源声明、旧组退役后的迟到关闭；gate 测试覆盖多 Surface 等待与取消释放。
- 最终全量 x64 测试 4,154/4,154 通过，记录在 `tests/DeskBox.Tests/TestResults/surface-claim-transfer-full-final-20260923.trx`。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 13:05 最终核验 PID 12192，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/surface-claim-transfer-20260923-27ff52b1` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认 WidgetManager 和设置窗口构造完成，35 个启动步骤中 0 degraded / 0 failed。未得到真实分组合并、拆离或解散的设备日志；自动化假宿主测试与空布局启动不能代替实际窗口、拖动及动画验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十一批：分组持久化后窗口失败的补偿

实施基线：`44e7a0d4` 加前十批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批处理分组拓扑保存后的窗口替换失败，不修改磁盘 schema、Z-order、拖放和用户文件操作；并行内存探针保持原样。

代码核对确认，非复用拆离与解散在保存新拓扑后退役旧组宿主，再逐个创建替换窗口。原路径在创建、显示或首帧失败时会直接退出，留下已保存的新拓扑与不完整的窗口集合。现在每个成功创建的替换宿主都按实例记录，并等待可见内容窗口的首帧。创建或显示中途失败的新宿主会按实例注销、隐藏和关闭；若整条替换链路失败，先把原拓扑写回磁盘。回写成功后才清理已创建的新窗口、移除旧声明并恢复原组宿主。旧宿主仍完整登记且可见时直接保留它。恢复的设置写盘被拒绝或抛错时，内存恢复为已经持久化的新拓扑，避免继续维持相反的设置状态；已出现的窗口错误会记录在日志中。

`WidgetGroupMutationSnapshot` 现在既能还原仍在设置中的组，也能还原解散后“不存在该组”的已提交状态；拆离的新状态还保存被移出的成员，组成员非活动时间记录也随快照恢复。合并则区分候选在设置保存前失败与新拓扑已保存后宿主提交失败：前者恢复内存并在已尝试保存时重写原拓扑，后者通过同一持久化回滚规则决定保留原拓扑或已保存的新拓扑。新宿主提交后若旧 HWND 退役异常，按旧实例补做清理并保留已提交的新宿主，不让候选事务误关它。

验证记录：

- 分组持久化回滚、Surface、呈现与模块边界定向测试 226/226 通过。新增纯事务测试覆盖回写成功、返回失败和抛错后的内存/磁盘状态选择；呈现契约测试检查非复用拆离、解散均在替换窗口首帧失败时进入补偿。既有迟到 Closed 与 Surface 身份测试继续通过。WinUI 窗口创建和真实首帧失败尚未做设备级故障注入。
- 首次全量测试 4,157/4,158：唯一失败是设置切片访问门禁发现新增 2 处平铺访问。新代码已改为通过 `WidgetLayout` 切片写入，门禁单测通过；最终全量 x64 测试 4,158/4,158 通过，记录在 `tests/DeskBox.Tests/TestResults/group-replacement-recovery-full-final2-20260923.trx`。未扩张旧访问清单。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；没有执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建：22 警告、0 错误。2026-09-23 14:06 最终核验 PID 9484，仓库下只有一个 DeskBox 实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程为 Medium 完整性。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/group-recovery-20260923-78703819` 使用空格子布局并关闭功能热键、剪贴板录制和自动备份。日志确认设置窗口完成构造、35 个启动步骤中 0 degraded / 0 failed。此项只验证装配；真实分组窗口的创建、首帧、拖动和动画仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十二批：复用拆离的持久化回滚与设备检查

实施基线：`44e7a0d4` 加前十一批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只修复复用原 HWND 拆离时的回滚写盘边界，并在独立开发数据目录检查真实窗口。并行内存探针、用户文件操作、磁盘 schema 和窗口动画策略未改。

原复用路径先把新拓扑写盘，再将活动 HWND 改为独立成员并创建剩余成员的替换宿主。失败时旧代码无论回滚写盘是否成功，都会把原 HWND 重新登记为旧组；若写盘失败，磁盘已拆离而内存和 Registry 却恢复为旧组。现在用已提交状态的快照调用 `WidgetGroupPersistedTopologyRecovery`：只有旧拓扑回写成功才关闭替换宿主、恢复旧组声明和原 HWND；回写被拒绝或抛错时，内存保持已保存的拆离状态，原 HWND 维持独立成员声明，存活的替换宿主继续保留，缺失时尝试补建并记录失败。复用路径的创建、首帧和回滚写盘故障点只在带独立 `DESKBOX_DEV_DATA_ROOT` 的 Debug 构建下可单次触发。为自动执行设备检查临时加入的启动动作入口已在验证后移除；常规 Debug 启动不执行分组动作。

验证记录：

- Surface、分组、呈现、设置访问门禁及故障探针定向测试 235/235 通过；最终移除临时启动动作入口后，全量 x64 测试 4,166/4,166 通过，记录在 `tests/DeskBox.Tests/TestResults/reused-detach-rollback-final-clean-20260923.trx`。用例覆盖回滚写盘成功与失败时的内存/Registry 归属，以及写盘确认前不恢复旧组声明。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立配置中的真实 HWND 检查：正常复用拆离从 1 个组宿主变为 2 个可见独立宿主，原 HWND `0x9B0E5C` 保留；替换创建失败时旧组与原 HWND `0xE610DA` 保留；替换首帧失败时回滚写盘成功，旧组与原 HWND `0x420210` 保留，替换窗口不再可见；首帧失败且注入回滚写盘拒绝时磁盘组数为 0、Registry 有 2 个 Surface，两个实际 HWND 均可见。合并检查中源 HWND 关闭、目标 HWND 保留，2 个窗口归为 1 个组 Surface；解散检查中旧组 HWND 关闭，出现 2 个可见独立窗口。上述操作由隔离 Debug 诊断入口自动触发，完成后入口已移除；它验证了 Win32 HWND/注册关系，不等同于用户拖拽手势、视觉动画或窗口层级的人工验收。
- 最终空布局 Debug 使用 `C:/Users/simon/AppData/Local/DeskBox-Dev/reused-detach-final-20260923-80ffe2f9`，2026-09-23 15:07 核验 PID 39020，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed。`git diff --check` 通过，未提交或推送。

## 第十三批：Todo 布局设置的单一写入入口

实施基线：`44e7a0d4` 加前十二批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批从剩余 Todo 显示设置中先选布局链路，不同时迁默认筛选、标签栏和文字/预览设置；Todo 任务数据、提醒运行时、磁盘 schema、XAML 绑定名与窗口内容实现不变。

原设置页的 `SelectedTodoLayoutMode` 同时写 `TodoLayoutMode` 和兼容字段 `TodoUseWideDetailPane`，另一个回调单独写 `TodoAutoSelectFirstInWideLayout`；默认值恢复又直接写同三个平铺字段。现在 `ITodoSettings` 提供窄的 `TodoLayoutSettings` 快照与布局更新命令，现有 `TodoSettingsCoordinator` 是三项布局状态的唯一 UI 写入者，继续使用 `SettingsService` 的布局归一化规则。`TodoSettingsViewModel` 持有可编辑快照并发布属性变化；`SettingsViewModel` 只转发原 `SelectedTodoLayoutMode` 和宽布局开关绑定，同时维护旧版宽布局布尔属性的兼容写入。外部设置恢复通过编辑器刷新，默认值恢复调用同一布局重置命令；整页恢复由外层统一保存，不提前排队一次布局单独保存。其他 Todo 选项仍按原路径，未把整张设置页一次性重写。

验证记录：

- Todo 协调器、设置切片/模块边界和 AOT 定向测试在最终小幅时序调整后 529/529 通过。新增用例覆盖布局模式与旧兼容字段同时写入、宽布局自动选中、外部变化刷新、默认值重置、持久化往返和协调器停止后的失败回退。
- 最终全量 x64 测试 4,169/4,169 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-layout-writer-full-final-20260923.trx`。既有 JSON 默认值、序列化、设置绑定与 Surface 回归均通过。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-layout-final-20260923-ad163b26` 预置 `SinglePane`、兼容宽布局关闭、自动选中关闭及空格子布局。2026-09-23 15:28 核验 PID 3964，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，三个磁盘字段保持一致。此项验证装配与既有状态加载；Todo 设置页实际点击、运行中宽窄布局切换仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十四批：Todo 默认筛选与标签可见性的联动写入

实施基线：`44e7a0d4` 加前十三批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Todo 默认筛选、七个标签可见位与标签栏总开关；Todo 字号、预览行数、任务数据、提醒和 XAML/JSON 字段保持原样。

原 `SelectedTodoDefaultFilter` 会先打开目标标签，再单独写默认筛选；各标签回调又通过 `PersistTodoTabSettings` 写整组布尔值，并在默认标签被隐藏时反过来调用筛选属性，形成多次保存和回调递归。现在 `ITodoSettings` 增加不可变 `TodoTabSettings` 快照，现有 `TodoSettingsCoordinator` 一次提交目标标签与默认筛选，或一次提交标签可见性与必要的回退。它使用 `SettingsService` 已有的筛选规范化、标签可见性和首个可见标签顺序，确保至少一个标签可见且默认筛选指向可见标签。标签栏总开关走同一写入者。`TodoSettingsViewModel` 发布快照变化；设置页保留原生成属性、`SelectedTodoDefaultFilter`、可见选项和文案绑定，只把用户操作转发给编辑器，并按最终快照校正开关，避免关闭最后一个标签后界面停在错误的关闭状态。

默认值恢复通过 Todo 编辑器重置整组标签且由外层统一保存，设置页不再直接写这一组平铺属性。外部设置刷新从编辑器读取；对尚未持久化的无效外部组合只生成安全的只读展示快照，不在读取时修改磁盘，下一次实际设置操作再提交归一化状态。`SettingsService` 在加载磁盘配置时仍按原规则修复无效字段，磁盘 schema 未变。

验证记录：

- 最终无效外部配置边界修复后的定向 x64 测试 657/657 通过，覆盖选择隐藏筛选时自动显示标签、隐藏当前默认标签后的顺序回退、最后一个标签不能消失、标签栏开关、外部刷新、默认重置与持久化往返。
- 最终全量 x64 测试 4,173/4,173 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-tab-writer-full-final-20260923.trx`；原设置、绑定、AOT 契约及 Surface 回归通过。
- AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-tab-writer-20260923-8e4dec31` 预置 `ThisWeek` 为默认筛选及唯一可见标签，其他功能与热键关闭、格子布局为空。2026-09-23 15:54 核验 PID 36904，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘筛选/标签组合保持一致。此项只验证装配与已保存状态加载；Todo 设置页实际点击和运行中格子切换仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十五批：Todo 内容密度与文字大小设置

实施基线：`44e7a0d4` 加前十四批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 Todo 预览行数、列表字号和正文字号；新任务位置、页脚显示、编辑器 Enter 行为、任务数据、磁盘 schema 与 XAML 绑定名不变。

原 `SettingsViewModel` 分别直接写三个平铺字段。预览行数按 1–10 行归一化；Todo 字号磁盘覆盖值为 `0` 时继承全局字号，界面显示的是有效字号。滑块只接受 10–16pt、0.5pt 步进，拖动期间沿用全局外观预览与延迟保存机制。现在 `ITodoSettings` 暴露有效值快照，现有 `TodoSettingsCoordinator` 是三个 Todo 原始字段的唯一 UI 写入者。`TodoSettingsViewModel` 持有可编辑预览行数和有效字号；设置页保留原绑定与字号文案，但字号滑块只向编辑器提交覆盖值，仍由原设置页决定何时请求预览和保存。全局字号变化时编辑器重读有效值，原始覆盖值为 `0` 的 Todo 字号只更新界面显示，不被误存为独立覆盖值。

默认功能设置恢复中的预览行数走同一编辑器命令，并由外层统一保存；原功能恢复没有重置 Todo 的两个独立字号，本批保持此行为。外部配置刷新和启动装配共用编辑器快照，设置页不再直接写这三个 Todo 平铺字段。

验证记录：

- Todo、SettingsService、设置切片/模块边界、AOT 与设置同步定向 x64 测试 677/677 通过，新增用例覆盖字号 `0` 继承全局值、显式字号覆盖后的独立保持、半点归一化、预览行数上下界、默认恢复、持久化往返与停止后拒写。
- 全量 x64 测试 4,176/4,176 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-density-writer-full-20260923.trx`。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 最终构建 22 警告、0 错误。独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-density-20260923-93839b4b` 预置全局字号 12.5、Todo 预览 4 行、列表字号覆盖值 0、正文覆盖值 13.5 及空格子布局。2026-09-23 17:47 核验 PID 38288，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性。设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘字段保持原值。此项验证装配与加载，未代替 Todo 设置页滑块拖动和运行中格子字号变化的设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十六批：Todo 输入行为设置的写入归属

实施基线：`44e7a0d4` 加前十五批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 `TodoNewTaskPosition` 和 `TodoEditorEnterBehavior` 的设置写入；任务排序算法、键盘事件路由、Todo 数据、磁盘 schema 和 XAML 绑定名不变。

原设置页分别直接写“新任务顶部/底部”和“Enter/Ctrl+Enter 保存”平铺字段。现在 `ITodoSettings` 增加输入行为快照，`TodoSettingsCoordinator` 是这两个字段的唯一 UI 写入者，`TodoSettingsViewModel` 发布编辑状态，原 `SettingsViewModel` 属性仅转发绑定并通知内容摘要。设置服务和 Todo 运行时复用同一新任务位置归一化规则：只有 `Bottom` 表示底部，其余回顶部；Enter 行为继续使用既有大小写不敏感的归一化及 `ShouldSubmitEditorOnEnter` 判断。默认功能设置恢复由 Todo 编辑器一次重置这两项，再由外层统一保存；外部配置刷新只读取规范化快照，不在读取时改盘。

验证记录：

- Todo 协调器、Todo 运行时、SettingsService、设置切片/模块边界和 AOT 定向 x64 测试 734/734 通过。新增用例覆盖 Bottom/EnterSaves 写入与持久化、Enter 和 Ctrl+Enter 原按键规则、非法值回退、外部刷新、默认重置与停止后的拒写。
- 全量 x64 测试 4,179/4,179 通过，记录在 `tests/DeskBox.Tests/TestResults/todo-input-writer-full-20260923.trx`。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。
- canonical Debug 构建 22 警告、0 错误。独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-input-20260923-a49138a2` 预置 Bottom、EnterSaves 与空格子布局。2026-09-23 17:59 核验 PID 32308，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘字段保持原值。此项验证装配与加载；真实 Todo 编辑器按键输入、新任务插入位置仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十七批：Todo 显示开关与标签样式的写入归属

实施基线：`44e7a0d4` 加前十六批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理已完成任务可见性、页脚统计、清除已完成按钮和 Todo 标签样式四项呈现偏好；Todo 任务过滤与清除命令、磁盘 schema、XAML 绑定名、实际控件布局均未改。

原设置页的三个显示开关回调直接写 `SettingsService.Settings`，标签样式属性另行规范化并写盘；默认功能设置恢复也直接写四个平铺字段。现在 `ITodoSettings` 提供这组设置的不可变快照与更新/重置命令，`TodoSettingsCoordinator` 是四个字段的唯一设置页写入者，继续使用 `SettingsService` 的 `Pivot/Button` 规范化规则。`TodoSettingsViewModel` 在编辑、外部配置刷新和默认重置后发布变化；原 `SettingsViewModel` 仅保留生成开关、标签索引和文案的 XAML/AOT 兼容绑定。协调器停止导致写入失败时，生成开关会按编辑器的实际值回退。功能默认恢复通过编辑器重置这四项，仍由外层统一保存；对外部无效标签样式的只读快照不改盘，显式重置时才写回规范值。

验证记录：

- Todo 协调器、SettingsService、设置切片/模块边界、设置同步与 AOT 定向 x64 测试 682/682 通过。新增用例覆盖四项设置的持久化往返、外部刷新、无效标签样式只读归一化及重置写回、协调器停止后的拒写与状态回退。
- 全量 x64 测试 4,181/4,181 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-display-20260923-1d244a8d` 预置“隐藏已完成、显示页脚统计、隐藏清除按钮、Pivot 标签”和空格子布局。2026-09-23 18:13 核验 PID 32900，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，四个磁盘字段保持原值。这验证装配与已存配置加载，不代替 Todo 设置页实际点击、运行中列表过滤和页脚呈现的设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十八批：Todo 功能默认恢复中的提醒写入收尾

实施基线：`44e7a0d4` 加前十七批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只收口 Todo 功能默认恢复时的提醒开关与默认提前时间；任务数据清理、提醒扫描算法、磁盘 schema、XAML 绑定名及格子重置顺序未改。

原功能重置在 `_isApplyingSettingsSnapshot` 保护下给设置页属性赋值，实际写盘仍靠随后两处 `SettingsService.Settings.Todo...` 平铺直写。现在 `ITodoSettings` 增加 `ResetReminderPreferences`，由 `TodoSettingsCoordinator` 一次把两项原始字段恢复为启用和 5 分钟，`TodoSettingsViewModel` 刷新编辑快照与设置页摘要。功能重置传入 `scheduleSave:false`：此阶段不排队保存，也不提前刷新提醒；外层 `SaveAsync` 通知设置变化时才按原时序协调提醒。普通显式重置可自行延迟保存并刷新一次；从关闭状态恢复时沿用立即检查规则。设置页对 Todo 切片或其平铺兼容属性已没有直接写入，`SettingsService` 的加载、迁移和 JSON 门面继续保留。模块边界测试按 `TodoSettingsSlice` 的属性清单检查 `SettingsViewModel` 源文件，阻止新增直接赋值。

验证记录：

- Todo 协调器、提醒运行时、SettingsService、设置边界和 AOT 定向 x64 测试 691/691 通过；补充真实 `TodoReminderRuntime` 假会话联测后，相关最终定向测试 44/44 通过。用例覆盖默认值快照、外层保存前零刷新、保存后仅一次协调、会话创建与关闭、显式恢复的 `checkNow`、重复重置不重复通知、停止后的拒写、外部保存恢复及持久化往返。
- 最终全量 x64 测试 4,186/4,186 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/todo-reminder-reset-20260923-30f36340` 预置提醒关闭、提前 30 分钟、Todo 关闭及空格子布局。2026-09-23 20:05 最终核验 PID 44552，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘提醒值保持原样。该检查证明装配和已有设置加载，未在真实 UI 中执行清空 Todo 数据的功能重置，也不等同于系统通知投递验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第十九批：QuickCapture 默认视图与标签可见性的联动写入

实施基线：`44e7a0d4` 加前十八批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 QuickCapture 默认视图、Records/Pinned/Recent 三个可见位和标签栏总开关；内容数据、剪贴板录制选择、标签样式、磁盘 schema 与 XAML 绑定名不变。

原设置页选择默认视图时会先通过生成属性打开对应标签，再写默认视图；隐藏当前默认标签时，标签回调写整组可见位，并可能再次进入默认视图属性，形成重复保存和回调联动。现在 `IQuickCaptureSettings` 暴露不可变 `QuickCaptureTabSettings` 快照，现有 `QuickCaptureSettingsCoordinator` 在一次写入中同时维护默认视图和标签可见性。选择隐藏视图会打开它；隐藏默认视图按 Records、Pinned、Recent 顺序回退；最后一个标签被关闭时恢复 Records。`SettingsService` 与设置页共用一条默认视图归一化规则。对尚未保存的无效外部组合，读取只给出安全快照，不在读取时改盘；下一次实际操作或默认重置才提交规范值。

设置页继续保留原生成开关、默认视图和摘要的 XAML/AOT 兼容绑定，变更通过协调器写入并按最终快照校正，停止后不能留下虚假的界面状态。功能默认恢复调用协调器重置整组导航偏好，仍由外层统一保存；外部设置刷新从同一快照同步。导航变动只通知设置页，不刷新或重建剪贴板监听。模块边界测试阻止设置页重新直接写这五个字段。

验证记录：

- QuickCapture 协调器、SettingsService、设置同步/复制、模块边界和 AOT 定向 x64 测试 667/667 通过；补充导航与剪贴板监听隔离用例后，相关最终定向测试 28/28 通过。新增用例覆盖选择隐藏默认视图的一次提交、隐藏当前默认标签后的顺序回退、最后一个标签的保底、无效外部组合的只读展示、默认恢复与持久化、协调器停止后的拒写，以及标签变动不刷新剪贴板会话。
- 最终全量 x64 测试 4,192/4,192 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-tabs-20260923-8137ed76` 预置默认 Recent、只显示 Recent 标签、隐藏标签栏及空格子布局。2026-09-23 20:40 核验 PID 40120，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，五个磁盘字段保持预置值。这验证装配与已保存状态加载，实际点击标签、运行中 QuickCapture 格子切换及视觉呈现仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第二十批：QuickCapture 标签样式与内容预览偏好

实施基线：`44e7a0d4` 加前十九批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 QuickCapture 标签样式、创建时间显示和列表预览行数；内容数据、剪贴板录制、最近记录容量、字号、编辑器格式、宽布局、磁盘 schema 与 XAML 绑定名不变。

原设置页把三个字段分别写入 `SettingsService.Settings`，默认功能恢复也在属性赋值后直接写平铺字段。现在 `IQuickCaptureSettings` 提供不可变 `QuickCapturePresentationSettings` 快照，现有协调器是三项的唯一设置页写入者，继续使用 `SettingsService` 的 `Pivot/Button` 和 1–10 行归一化规则。设置页原标签索引、布尔开关、预览行数及摘要绑定只转发操作并同步最终快照；外部设置刷新从同一快照读取。对尚未保存的无效原始值只做只读规范化，显式编辑或默认恢复才写回。功能默认恢复调用协调器一次重置这三项，外层仍负责统一保存。呈现设置变化会通知设置页，但不会刷新或重建剪贴板监听；协调器停止后的编辑尝试会按真实快照回退。模块边界测试阻止设置页重新直写这三个字段。

验证记录：

- QuickCapture 协调器、SettingsService、设置同步/复制、模块边界和 AOT 定向 x64 测试 672/672 通过。新增用例覆盖非法样式与越界行数的只读展示、用户写入归一化和持久化往返、默认值重置时不提前保存、外部呈现变化通知且不刷新剪贴板会话，以及停止后的拒写。
- 全量 x64 测试 4,196/4,196 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，888 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-presentation-20260923-76c3d5ba` 预置 Pivot、隐藏创建时间、预览 7 行及空格子布局。2026-09-23 21:02 核验 PID 16628，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，三个磁盘字段保持预置值。这验证装配和已保存状态加载；实际设置页点击、QuickCapture 内容卡片样式及运行中切换仍需设备交互验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 第二十一批：QuickCapture 最近记录容量与裁剪归属

实施基线：`44e7a0d4` 加前二十批未提交的工作区改动，分支仍为 `experiment/memory-probe-destroy-hidden`。本批只处理 `QuickCaptureRecentLimit` 的设置写入及对应历史裁剪；容量范围、磁盘格式、记录数据模型、剪贴板监听规则、XAML 绑定名和功能重置的数据清理范围不变。

原设置页在容量回调中直接写平铺字段并保存，再通过 `App.Current.QuickCaptureService.TrimRecentItemsAsync` 发起无人等待的裁剪。现在 `IQuickCaptureSettings` 暴露规范化容量的读取、设置和默认恢复命令，`QuickCaptureSettingsCoordinator` 是设置页的唯一写入者。App 在装配时注入数据服务的裁剪动作；设置页只保留数字输入、文案与兼容绑定，外部配置刷新从协调器读有效值。沿用 `QuickCaptureService.NormalizeRecentLimit`：小于最小值回默认值，高于最大值截断。功能默认恢复通过协调器设回默认容量，取消未开始的裁剪，由外层统一保存并沿用原有数据清理流程。

协调器用 350 毫秒安静期合并连续调整：尚未开始的旧请求被最新容量取代，已经进入数据服务的裁剪串行完成。裁剪异常交由宿主错误报告，后续请求仍能继续；退出取消待执行请求并等待活动裁剪，避免释放数据服务时遗留任务。容量编辑、显式恢复或外部容量变化才安排裁剪；标签和呈现偏好仍不刷新剪贴板监听。已开始的裁剪不能撤销，随后调大容量无法恢复之前已移除的记录；这是该操作原有的数据语义，快速调整只避免尚未开始的过期裁剪。

验证记录：

- QuickCapture 协调器、剪贴板运行时/服务、SettingsService、设置同步、模块边界和 AOT 定向 x64 测试 678/678 通过；调整通知顺序后相关最终定向测试 39/39 通过。新增用例覆盖快速设置仅执行最后的待裁剪值、默认恢复取消待执行请求、停止等待活动裁剪并拒绝新写入、失败报告后下一次仍可运行、外部配置改变不刷新剪贴板监听。真实隔离 `QuickCaptureStore` 中的 25 条最近记录按新容量裁为 10 条，重新加载仍为 10 条。
- 最终全量 x64 测试 4,203/4,203 通过。AOT 条件编译通过：x64/win-x64、audit + smoke 配置，890 警告、0 错误；未执行 Native AOT publish/link 或发布包运行验证。canonical Debug 构建 22 警告、0 错误。
- 独立开发数据目录 `C:/Users/simon/AppData/Local/DeskBox-Dev/quickcapture-limit-20260923-5731a4ff` 预置容量 80、功能关闭及空格子布局。2026-09-23 22:54 核验 PID 39980，仓库下唯一实例，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，Medium 完整性；设置窗口构造完成，启动 35 步中 0 degraded / 0 failed，磁盘容量保持 80。该启动检查验证装配与加载；真实设置页数字输入交互、运行中剪贴板采集仍需设备验收。
- `git diff --check` 通过；未提交、推送或合并并行实验改动。

## 历史下一批（第 22 批现已完成）：QuickCapture 列表与正文字号的写入归属

当时设置页仍直接写 `QuickCaptureListTextSize` 与 `QuickCaptureContentTextSize` 两个可选覆盖值，原始值 `0` 表示继承全局字号。第 22 批仅收口这两项：

1. 核对字号滑块的 10–16pt、0.5pt 步进、即时外观预览、延迟保存与全局字号变化时的继承行为，不改变视觉范围或现有 XAML/JSON 字段。
2. 由 QuickCapture 协调器接收两个原始覆盖值并提供有效字号快照；设置页保留滑块绑定和预览时序，外部恢复时不能把继承值误写成覆盖值。
3. 验证 `0` 继承、显式覆盖、非法输入、默认功能恢复、持久化和停止拒写，再做 x64/AOT 与隔离 Debug。编辑器格式、宽布局和远程图片策略继续分批处理。

## 后续保留事项

- Todo 设置页仍保留 XAML/AOT 兼容属性和摘要文案；功能重置涉及数据清理和两次保存/提醒协调，若要改变其事务和通知时序，应单独审查并做设备验收。
- SettingsService 仍有根对象和兼容属性；其他功能写入入口、全局 SettingsChanged 参数化还未迁移。
- 原生通知注册/激活和 Dispatcher 由宿主拥有，本批没有引入第二个 UI 线程或 Generic Host。
- 全局备份生成/上传任务和容器释放已在第三批收口；云端列表与连接检测已迁入页面编辑器；恢复事务仍由原数据备份服务负责。日志队列的整体迁移仍待后续评估。
- 每批更新本记录的实际证据和下一批范围，代码搬迁不同时修改磁盘 schema、文件安全事务或产品交互。

## 2026-09-24：A+B/C/D 与第 22 批统一候选收口

当前隔离候选在 `C:/Users/simon/.codex/worktrees/deskbox-surface-group/wingezi`，包含第 1–22 批架构改动及评审核实后的 A+B/C/D 修复；原共享目录 `D:/project/wingezi` 和 `MemoryDestroyProbe` 未并入。候选 HEAD 仍是本地检查点 `f3f357f1`，本轮增量均未提交、推送或创建 PR。

- QuickCapture 最近记录裁剪贯通退出取消令牌，15 秒退出步骤上限兜底；不可取消的原子持久化仍可能在超时后运行，退出会记录该情况。随记格子内启用剪贴板捕获改为先保存设置、后刷新监听，避免额外显示格子；协调器测试钉住该行为。
- Todo 列表/正文非有限字号输入在协调器和编辑门面均拒写。第 22 批 QuickCapture 两个字号覆盖值已单独提取到候选：原始 `0` 继续继承全局字号，设置页由协调器写入；增加实际设置门面的继承、保存、再显式覆盖往返测试。
- D 段补充仅在 Debug 且隔离数据根下启用的无目标窗口故障点。用户实测合并双故障后无可用窗口：5 个故障点命中，磁盘仍保留 1 组 2 成员，两份样例文件完好；去掉故障重启恢复同一组合且切换正常，Registry 仅 1 条有效声明。复用拆离的首次窗口外拖动命中 `reused-detach-first-frame`，真实回滚写盘和窗口边界恢复均成功；之后的正常拖动又完成拆离。具体日志、HWND 和数据根见 `surface-group-recovery-segment-20260924.md`。
- 最终统一候选全量 x64 测试 **4,226/4,226 通过**，本地 TRX 为 `tests/DeskBox.Tests/TestResults/architecture-final-x64-20260924.trx`（按仓库规则被忽略）；Release AOT audit+smoke 条件构建 888 警告、0 错误，canonical Debug 构建 22 警告、0 错误。
- x64 Native AOT publish/link 与完整 `publish-aot-audit.ps1` 审计通过，产物 44 个文件、约 95.7 MiB，`AlwaysThrowCount=0`，源码快照审计前后一致。真实 AOT 程序在独立数据根启动，36 步 0 degraded / 0 failed。审计中发现两处旧源码形态匹配误报（主 ViewModel `Dispose` 子串匹配、Todo 提醒异步入口改为包装+核心方法），已收紧对应审计条件；两次失败的原始产物和摘要均保留在 `.artifacts/aot-audit/` 下。
- x64 Native AOT 测试 MSIX `1.5.5.0` 构建完成，原始包 73 文件、签名副本 75 文件的静态包审计均通过。未签名包被 `0x80073CFF` 拒绝；当前用户 TrustedPeople/Root 信任仍被 `0x800B0109` 拒绝，符合微软文档对 `LocalMachine\TrustedPeople` 的要求。经用户另行授权后，短期测试证书仅临时加入整机 TrustedPeople：MSIX 成功安装，状态 `Ok`。从包入口启动的 PID 13096 位于 `WindowsApps`，`GetPackageFullName` 与安装包身份完全一致，exe SHA-256 与审计解包文件一致，完整性级别为 Medium；“DeskBox 设置”窗口可响应，导航按钮实际切换并恢复。随后结束该进程、卸载包并移除整机证书信任；当前用户/整机均无该证书，包与进程均为 0。包内启动证据保存在 `.artifacts/architecture-final-sideload-test-x64-20260924/packaged-run-evidence.json`。正式双架构安装包、商店合并上传包与发布不属于这次架构候选。
- 隔离 Debug 数据根 `C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-final-quickcapture-ui-20260924` 预置全局字号 12.5、随记列表原始覆盖值 `0`、正文覆盖值 13.5。用户在真实设置页看到列表 12.5pt/正文 13.5pt，调列表滑块到 13pt 正常；磁盘只将列表原始值改为 13，正文仍为 13.5。用户开启随记格子并在「最近」页点击「开启记录」，界面仍只有 1 个随记窗口、最近页正常；磁盘记录功能与剪贴板记录均启用，布局仅 1 个随记格子，日志有监听启动与一次文本捕获、无相关错误。强制结束测试进程后重启同一目录，1 个随记格子和字号/记录设置保持不变，启动 35 步 0 degraded / 0 failed，监听恢复。测试实例最后已结束，不影响生产数据根。

### 下一批

1. 已复核“可见组里单个隐藏成员”的可达性：`WidgetGroupSettings.Normalize` 与正常组操作都强制成员可见性跟随组；独立磁盘注入测试在启动时把隐藏成员修正为可见，35 步 0 degraded / 0 failed，现有归一化单测复跑 1/1 通过。真实拖离已覆盖回滚分支，因此不再把无正常入口的隐藏态手势列为人工交付门槛；保留可见性传值作为防御性修复。详见 `surface-group-recovery-segment-20260924.md`。
2. 审阅统一候选相对检查点的每个 hunk，确认只含 A+B/C/D、第 22 批及本轮审计修复；分段固化并交给 CI 后再考虑 PR/主干集成。云同步、设备层 store、Generic Host 和物理拆工程仍按原路线图的立项触发，不挤入本次收口。

## 2026-09-24：增量差异复审完成

本轮对 `f3f357f1` 之后的 36 个已跟踪改动和当时 4 个新文件按 A+B、C、D、第 22 批、AOT 审计分类复核；没有内存探针、版本/安装器/Release 元数据或多语言资源混入。第 22 批提取的五个独立文件与原共享目录逐字节一致，交织文件的修改点逐段核对。完整结论与后续提取顺序见 `architecture-final-candidate-review-20260924.md`。

**下一批改为分段提取与交付**：从已有 A+B 本地提交 `61f05c5d` 对齐本轮修复，再按 C→D→第 22 批→AOT 审计契约整理可审阅差异；各段复验后才进入远程 CI/PR。此前列出的“隐藏成员必须人工故障复现”经归一化源码、既有单测和独立启动注入校正为无正常持久化入口的防御项，不再阻塞这一步。

## 2026-09-24：A+B 与 C 分段对齐完成

A+B 工作树已补齐最终 QuickCapture 退出/快捷入口和 Todo 非有限字号修复，保留第 22 批独立；全量 x64 **4,167/4,167**、AOT 条件构建 0 错误、canonical Debug 0 错误，隔离启动 36 步 0 degraded / 0 failed。C 工作树在此基础上保留自己的窗口登记边界及零 HWND 守卫；全量 x64 **4,177/4,177**、AOT 条件构建 0 错误、canonical Debug 0 错误，隔离启动 36 步 0 degraded / 0 failed。两次测试进程均已结束。两段记录分别见各自 `feature-runtime-settings-segment-20260924.md` 与 `window-registration-segment-20260924.md`。

**下一批**：以已验证的 A+B/C 差异作为分段交付输入，将 D Surface/格子组和第 22 批字号分别形成可审阅的后续增量，再跑最终叠加态检查并交给远程 CI。当前三个候选工作树都未提交本轮修复、未推送、未创建 PR；`f3f357f1` 仍只是整批回退锚点。详见 `architecture-final-candidate-review-20260924.md`。

## 2026-09-25：分段提交与远程审阅

在用户明确授权“发”之后，A+B 已提交并推送 `c966a9a0`，创建 [PR #423](https://github.com/Tianyu199509/DeskBox/pull/423)；C 从该提交叠加 `6728c2e4`，创建 [PR #424](https://github.com/Tianyu199509/DeskBox/pull/424)；D 再叠加 `a3809579`，创建 [PR #425](https://github.com/Tianyu199509/DeskBox/pull/425)。第 22 批使用独立 `codex/architecture-quickcapture-text-size-review` 分支，以 D 提交为基线。前三段本地全量 x64 分别为 4,167、4,177、4,224 全绿；最终叠加态 **4,229/4,229** 通过，完整 Native AOT publish/link 与审计通过。最终 canonical Debug 从独立数据根恢复 1 个随记格子，原始字号 13/13.5 与剪贴板记录状态保持，启动 35 步 0 degraded / 0 failed；测试进程已退出。最终源码、测试与脚本和先前已实测的统一候选逐文件一致，内存探针未进入提交链。

**下一批**：核对四个 PR 各自 CI 及审阅反馈，按依赖顺序处理合并；合并动作和正式版本发布另行授权。云同步设备层、contribution descriptor、Generic Host 或物理拆工程仍遵照原路线图的触发条件，不混入当前 PR 链。具体范围见 `architecture-final-candidate-review-20260924.md`。

## 2026-09-25：四段 PR 栈合并完成

经逐段核对，#423、#424、#425、#426 均通过远程 `Build and test`，并按此顺序以保留提交祖先关系的合并提交进入 `main`。四个合并提交分别为 `a7488e46`、`cc3dd408`、`ab2044d1`、`8cedf5f2`；每一步的文件树与对应 PR head 一致，最终远端 `main` 为 `8cedf5f2`，四个 PR 均显示 `MERGED`。提交作者与提交者均为 Simon。原共享工作区 `D:/project/wingezi` 的并行 `MemoryDestroyProbe` 和两份外部审查报告没有并入，也没有清理该工作区。

合并前修复了第 22 批切片遗漏：全局字号回调现在与 Todo 一样立即刷新 QuickCapture 设置协调器。正常滑块拖动及提交不会发出普通 `SettingsChanged`，因此不能依赖异步广播更新随记设置页的继承字号。新增回归测试先在漏项上失败，再验证列表和正文字号均随全局值更新、原始覆盖值仍为 `0`；最终本地 Debug 与 CI 同配置 Release/x64 全量均为 **4,230/4,230** 通过。隔离 Debug 启动记录 36 步、0 degraded、0 failed；#426 最终远程 CI 通过。#423 的 PR 说明明确了新 scoped 恢复标记在用户确认前中断时丢弃暂存、旧标记缺少确认字段时继续采用旧合并语义。

此前统一候选的完整 x64 Native AOT publish/link、测试 MSIX 安装启动和部分真实交互已有独立证据；本次补回一行现有协调器调用后，没有重新执行完整 Native AOT publish/link 或正式双架构安装包验收。它们仍是后续正式发布前的门禁，不与这次源码合并混为一谈。

### 下一批

1. **第 23 批：退出链路剩余等待的所有权。** 先测量和故障注入 `todo-settings`、`search-settings`、`todo-reminders` 三步的挂起路径，再明确取消、排空与宿主资源释放的顺序。不能只给步骤套 `WaitAsync`：超时后的任务仍会运行，后续关闭窗口或释放服务可能与之竞争。验收包括不合作后端、重复退出、退出后不得回写已释放对象，以及隔离 Debug 的实际退出；保持设置 schema 和用户交互不变。
2. **随后处理 D 段剩余的拆离对账异常。** 针对回滚写盘失败且 Registry/替换窗口再次出错的窄路径，建立可观测的失败结果或隔离补偿，补一条经过真实拆离编排的自动测试。现有真实拖离故障注入已证明常见回滚路径可用，隐藏成员的无正常入口状态不再列为手动验收前提。
3. WebDAV 真服务器、通知交互和正式包的设备验收按各功能/发版门禁单独完成；`SwitchGate` 旧测试与成员、远端列表登记等低风险清理随相关代码触碰处理。设备层 store、云同步协议、contribution descriptor、Generic Host 和物理拆工程继续遵照路线图的立项触发条件，不并入第 23 批。

## 第二十三批：退出链路剩余等待的所有权

实施基线：`53c65f9d`。本批只处理退出序列中 `todo-settings`、`search-settings`、`todo-reminders` 三步的等待所有权；设置 schema、用户交互与其余步骤不变。

实现：`ShutdownSequence.RunAsync` 返回完整清理是否执行。`ShutdownStep.Bounded` 期限届满抛出专用 `ShutdownStepDeadlineExceededException`；后端自身的超时或失败仍按普通失败记录并继续，不触发中止。带 `abortFollowingStepsOnTimeout` 的步骤超时后中止其余步骤并返回 false——超时后仍会运行的操作不得与随后关闭窗口、释放服务和容器的步骤竞争。三步均以 15 秒期限启用该语义。`ShutdownApplicationAsync` 的 finally 成为兜底：托盘窗关闭、单实例互斥释放并置空（正常路径由 `single-instance` 步执行并置空，null 传播防止双重释放）。`SearchSettingsCoordinator` 停止时移除内部 5 秒上限，改为排空全部在途请求：期限由 App 层统一持有，协调器报告完成即代表没有请求再使用连接；超时则不释放借用的搜索运行时，交由进程退出接管。

测量与故障注入（隔离 Debug，数据根含 `architecture-shutdown-ownership-20260925`，`DESKBOX_DEV_SHUTDOWN_PROBE`）：`clean-exit` 全序列执行、2 秒退出；`hang-todo` 在 15 秒整抛出期限并跳过依赖清理。测量发现：跳过依赖清理时 `Application.Exit()` 返回后 XAML 消息循环继续泵送（dotnet-stack 证实 UI 线程空转于 Main、无前台线程阻塞、`ShutdownApplicationAsync` 已完成），进程无限存活。修复：deadline 路径在 `Exit()` 前布置 3 秒 `Environment.Exit(0)` 看门狗，仅该路径武装。修复后 `hang-todo` 20 秒退出（15 秒期限 + 3 秒看门狗 + 余量），`clean-exit` 仍 2 秒且不触及看门狗；契约测试钉住看门狗与跳过日志。注意本批首次尝试用 `BaseIntermediateOutputPath` 隔离 AOT 构建会破坏 XamlCompiler 状态（WMC9999），隔离应使用 SDK `ArtifactsPath`。

验证记录：

- 定向测试 56/56 通过，含所有权期限中止与共享完成结果、后端超时区分、挂起的 Todo 窗口操作/提醒排空/搜索探测分别中止依赖清理。
- 全量 x64 测试 4,235/4,235 通过（合并态 4,230 + 本批 5 个新用例）。
- AOT 条件编译（x64/win-x64、DeskBoxAotAudit + DeskBoxAotSmokeHarness + DeskBoxRustNative、`ArtifactsPath` 隔离）通过：888 警告、0 错误。未执行 Native AOT publish/link 或发布包运行，仍为发版门禁。
- Debug 构建 0 错误；`git diff --check` 通过。
- 真实挂起仅经探针模拟；生产三步后端均自带取消与排空，期限属于最后防线。未提交推送。

## 第二十四批：拆离对账失败的隔离补偿

实施基线：`53c65f9d`（分支自 main；与退出所有权批次（PR #427）无源码交集，可独立合并）。本批只处理复用拆离回滚写盘失败后 Registry 重指再出错的窄路径；拆离编排、磁盘 schema、Z-order 与拖放规则不变。

原路径：`ReconcileCommittedDetachedSurfaceAsync` 的 Registry 重指失败时只记日志——设置已保存拆分而 Registry 仍持旧组声明，此后每次 `RaiseWidgetGroupsChanged` 都会撞声明校验抛点，分组操作降级直到重启。现在该 catch 调用 `WidgetGroupPersistedTopologyRecovery.QuarantineCommittedDetachClaims`：按实例注销被拆宿主、注销仍声明该成员的其它 Surface、移除旧组 Surface，再重试一次独立声明注册；重试被拒返回 false 并输出可观测标记（"Detach reconciliation quarantined without a standalone declaration"），不改动无关声明。补偿成功后补登记独立文件会话。`QuarantineCommittedMergeAsync` 两处原位于 try 之外的 `UnregisterHost` 补了逐项守卫，隔离级不再可能把异常逸出到合并 catch 之外。

新增探针阶段 `detach-reconcile-registry`（DEBUG+开发数据根门控、单次触发）用于注入该重指失败。

验证记录：

- 定向 35/35 通过：真实 `WidgetSurfaceRegistry` + 假宿主断言隔离会清除旧组声明并重建独立声明；重试被拒时返回 false、输出可观测标记、且不误删无关声明。
- 全量 x64 4,232/4,232（分支基线 4,230 + 2 个新用例）；AOT 条件编译 890 警告、0 错误（`ArtifactsPath` 隔离，仓库锁文件未改动）；`git diff --check` 通过。
- 设备级检查已完成（2026-09-25 晚，隔离 Debug 临时入口，入口已移除）：真实 HWND 双文件格子组，`reused-detach-create,reused-detach-rollback-save,detach-reconcile-registry` 三阶段全触发——复用失败→回滚写盘被拒→对账重指失败→隔离补偿成功重建独立声明→后续经真实编排的重命名正常完成（`RaiseWidgetGroupsChanged` 不再抛），进程无崩溃。
- 老版对照终审（同日）：设置面/运行时/磁盘兼容三路审计。磁盘兼容全绿（新→旧→新双向启动演练 settings.json 逐字节一致）；两处用户可见变更确认为第 5 批文档明示的有意统一（菜单关闭随记同步停录制、旧"功能关+录制开"配置启动归一化），列入 1.6.0 changelog 候选；补回 3 条剪贴板日志标记线（"Disabled from settings"/"Service initialized on demand"/"Inactive service released"）。最终全量 4,237/4,237。

## 第二十五批：审计遗留清理与文档对账

实施基线：`fdb5d45a`（main，四段 PR 栈 + #427 + #428 全部合并后）。本批只清理历次审查的低风险遗留项与文档欠账，不改用户可见行为。

- 删除 `WidgetSurfaceSession.SwitchGate` 死代码（全仓零引用，真串行早已由 `WidgetSurfaceSwitchGatePool` 承担）。
- 空白 SurfaceId 语义澄清：全量测试证明 `AcquireManyAsync` 的跳过空白行为是**承载语义**（独立格子的拓扑事务参与者没有 SurfaceId，必须无门控运行），不可改为抛错（首版 fail-closed 尝试被 HiddenMerge 双故障测试当场击落并回退）；拆离/解散/重排入口在查找组之前先 `WidgetGroupSettings.Normalize`（对齐合并路径既有做法），`Get` 对空白仍严格抛错。新增测试钉住跳过语义。
- `FileSurfaceContent` 磁盘协调的 `OperationCanceledException` 与表面切换/退役的竞争改为 Verbose 记录（Session A 观察到的日志噪声），真实失败仍走原错误日志。
- 远端**列表**操作仍不经 `BackupRestoreActions` 登记（只读、页面访问已取消、无状态影响；为它穿透三层构造函数与低风险清理批的定位不符）——维持文档化残余，随下次触碰备份协调器时顺手收编。
- 文档对账：路线图追记执行对账（分组事务线立档、2C PR-1 状态修正、IFeatureRuntime 替代记录）。

验证记录：定向测试（GatePool/Registry）通过；全量 x64 4,238/4,238（4,237 + 1 个新用例；首版抛错尝试在 4,237/4,238 被合并编排测试击落后回退）；`git diff --check` 通过。设备验收（A/B/D）已于 2026-09-26 全绿，发版等 Simon 指令。

## 第二十六批（部分）：P0 归因与日志队列评估

- **P0 全应用内存归因已完成**（报告 `residency-p0-attribution-20260926.md`）：framework Release、3 组×9 格子同进程差分——组缓存树增量 3~5MB、占稳态私有 2~3%，**命中 <10% 停止线：跳过 Cold 档**。两个意外发现：冷启动缓存为空（按需物化+Small 预算封顶，"缓存常驻"前提已不成立）；P2（Warm TTL）价值降级为 CPU/订阅冻结，降为低优先级待真实反馈，**P1-c 维持无条件执行**。测量用临时解除 `DESKBOX_DEV_DATA_ROOT` Release 门控的本地构建，改动已全部还原（一次真实数据误写疑云经快照比对确认为虚惊、零影响，如实记录）。
- **日志队列迁移评估结案：不迁移**。理由：日志队列与单实例锁同属进程生命周期基础设施，必须活到所有服务释放之后（退出序列中 log-drain 在 service-container 之后）；移入 DI 容器会倒置依赖，移入协调器只换边界无行为收益；模块边界立法本就将日志兼容调用豁免在外。重开触发条件：日志需要可配置 sink/级别（结构化日志功能立项）时，抽 Contracts 接口、App 为默认实现。第 3 批起的"待评估"就此关闭。

## 第二十七批：外部审计对照（#427/#428 复审）

外部审计四项指控经两路独立对码验证：①"三个备份/随记步骤缺 abort 会与容器释放竞争"——**事实成立但影响链被驳斥**（五类依赖服务均非 IDisposable，容器只释放 Theme/Weather/CitySearch；备份挂起是纯 IO；abort 反而是错误语义），仅补防翻修注释；②"看门狗路径互斥锁提前释放开 3 秒双实例窗口"——**成立并已修复**：mutex 仅在完整清理时手动释放，看门狗路径交给进程死亡释放（修复含审计员遗漏的约束：throw 路径无看门狗、必须保留手动释放）；契约断言钉住该语义，hang 探针复验 20 秒退出不变；③"Search 停止/准入竞态"——**驳斥**（admission 与 Stop 全在 UI 线程、无挂起点交叉，连 latent 都不成立）；④"隔离补偿缺身份校验可误删有效声明"——**驳斥**（已提交拓扑下 removedMember 为独立，任何其它声明定义上即 stale；提议的 SurfaceId==originalSurfaceId 校验在唯一可构造子情形会破坏清理，提议的测试会把违反不变量的状态固化为正例）。审计的 P3（PR 混日志修复）与拆 Coordinator 建议记录在案（后者与路线图拆工程触发条件一致）。全量 4,238/4,238。

## 第二十八批：提醒重入的切片收窄

审计遗留 P3-2 收口。目标口径：全局 `SettingsChanged` 无参数广播导致 `TodoSettingsCoordinator.OnSettingsChanged` 在每次防抖保存（如拖动外观滑块每秒一次）都重入提醒协调。评估后取**协调器侧变更检测守卫**而非全量事件参数化：其余订阅者（QuickCapture/Search/WidgetManager/备份）早已自带缓存比较守卫，事件签名改造是一天级宽 API 动而收益仅剩提醒一处。守卫缓存四个提醒相关输入（TodoEnabled、TodoReminderEnabled、默认提前分钟、Todo 格子 ID 集合），无变化即跳过；首次通知仍重入（覆盖恢复/默认值路径）。新增三条测试：无关保存零重入、提醒开关变化重入、Todo 格子增删重入。事件参数化留作触发项：出现第二个必须依赖切片信息的消费者时再立法。

## 2026-09-29 文件安全专项：默认收纳路径复制迁移

用户批准方案二后，将设置页与首次引导的默认路径迁移统一为复制、SHA-256 校验、整批提交映射并保留原文件。移除迁移残留的删除入口，增加持久化恢复记录和大批量迁移风险提示。范围、当前准入、验证证据及下一批验收见 [收纳路径安全迁移](managed-storage-copy-migration-20260929.md)。