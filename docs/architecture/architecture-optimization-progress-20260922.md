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
