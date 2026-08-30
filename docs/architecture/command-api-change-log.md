# Command API 变更全记录（Implementation & Change Log）

> 本文记录命令 API（CLI / MCP / 命名管道 JSON-RPC）从立项到当前状态的**全部改动**，
> 与 `command-api-v1.md`（协议契约）互补：契约文档描述"现在是什么"，
> 本文记录"怎么来的、为什么、改了哪些文件、验证了什么"。
> 状态：随 1.4.9 开发推进持续更新。

---

## 1. 背景与目标

让 AI 助手（Claude Desktop / Cursor / 任何具备终端能力的智能体）能够通过
CLI、MCP 或原始 JSON-RPC 安全地查看并操作运行中的 DeskBox。

三条通道，同一命令面：

| 通道 | 入口 | 适用场景 |
|---|---|---|
| CLI | `DeskBox.Cli.exe <verb>` | 人、脚本、有终端的 AI 智能体 |
| MCP | `DeskBox.Cli.exe mcp`（stdio） | Claude Desktop、Cursor 等 MCP 宿主 |
| 原始 API | 同用户命名管道上的 JSON-RPC 2.0 | 自定义集成 |

安全模型四层：同用户管道 ACL → 能力位协商 → 只读/破坏性双门禁 → 全量审计日志。

---

## 2. 合并与提交时间线

| 提交 | 内容 |
|---|---|
| `4c698c0` | 本地快照（1.4.5-dev 工作副本 + Command API 初版实现），分支 `pre-merge-snapshot` |
| `361bb2c` | sync：工作区与 origin/main（release 1.4.6）逐字节对齐 |
| `bbafa46` | feat：Command API 主体（Protocol/管道服务端/CLI/MCP），落在 1.4.6 基座 |
| `8d90034` | fix：`deskbox mcp` 子命令接线（McpServer 类已完成但入口未分发） |
| `6695fdc` | feat：设置面板"命令 API"分节（12 语言本地化） |
| `3299489` | fix：新分节引入的 Localized 冻结使用计数同步（168/137/334） |
| `dfd860d` | merge：上游 1.4.7 / 1.4.8-prep（4 个上游提交，仅 CHANGELOG 冲突） |
| `6ddeabb` | feat：AI 可控小部件面——待办/随记/文件/生命周期 15 个新命令 |
| `3299489`+ | fix：JSON 序列化冻结基线联动（29 文件/65 调用/41 上下文属主，Ordinal 排序） |
| `6ddeabb` 后 | 上游复查：无新增提交（origin/main = 7b60ce6 已含于本地） |

上游 1.4.7/1.4.8 的关键变更（已吸收）：Windows App SDK 2.4、`.lnk` 拖拽修复、
栈弹窗 DIP 修正、`WidgetConfig` 坐标改为 double、Search/Everything 引擎集成。

---

## 3. 新增文件清单

### 3.1 `src/DeskBox.Protocol/`（新项目，7 文件，零 NuGet 依赖）

| 文件 | 职责 |
|---|---|
| `CommandApiProtocol.cs` | 协议版本、管道名前缀、稳定错误码、能力位常量 |
| `CommandEnvelope.cs` | CommandRequest / CommandErrorPayload / CommandResult |
| `JsonRpcWire.cs` | JSON-RPC 2.0 请求/响应/错误对象（`jsonrpc` 名字被显式固定为小写） |
| `CommandSchema.cs` | CommandDescriptor / CommandApiSchema（schema 自发现契约） |
| `CommandApiJson.cs` | source-gen 序列化入口（全部 `JsonSerializerContext` 为 internal） |
| `CommandFrame.cs` | 4 字节小端长度前缀 + UTF-8 JSON 的帧编解码（上限 4 MiB） |

### 3.2 `src/DeskBox/Services/CommandApi/`（应用内宿主）

| 文件 | 职责 |
|---|---|
| `CommandRegistration.cs` | 注册记录、线程亲和枚举、`ICommandHandler`、`ICommandUiDispatcher`、`CommandArguments` 参数解析助手（含共享 `RequireWidgetId`） |
| `CommandRegistry.cs` | 命令注册表：查找、能力列表、schema 生成（schema 的唯一事实源） |
| `CommandDispatcher.cs` | 门禁流水线：协议版本 → 路由 → 只读 → 破坏性 → 校验 → 执行（UI 亲和 5 秒忙超时）；`CommandValidationException` / `CommandUiShutdownException` |
| `PipeRpcServer.cs` | 管道宿主：ACL（仅当前用户+SYSTEM）、30 秒空闲断开、逐调用审计 |
| `Handlers/ServerHandlers.cs` | `server/ping`、`server/info` |
| `Handlers/ServerSchemaHandler.cs` | `server/schema` |
| `Handlers/SettingsGetHandler.cs` | `settings/get`（显式白名单投影） |
| `Handlers/SettingsSetHandler.cs` | `settings/set`（仅 theme/language，必须经服务方法立即生效） |
| `Handlers/QuickCaptureHandlers.cs` | `quickcapture/list`、`quickcapture/add` |
| `Handlers/QuickCaptureMutationHandlers.cs` | `quickcapture/pin`、`update`、`delete`（全无头，Changed 事件自动刷 UI） |
| `Handlers/TodoHandlers.cs` | `todo/list`（store 直读）、`todo/add`（**UI 线程经 ViewModel**，与其它写命令同源） |
| `Handlers/TodoMutationHandlers.cs` | `todo/set-completed`、`edit`、`set-due`、`delete`、`clear-completed`（UI 线程经 ViewModel：保循环生成/撤销栈/即时刷新） |
| `Handlers/WidgetsListHandler.cs` | `widgets/list`（配置快照：id/kind/名称/矩形/映射路径，坐标 double） |
| `Handlers/WidgetLifecycleHandlers.cs` | `widgets/create`（HashSet 增量检测；单实例返回现有 id + `created:false`）、`remove`（破坏性；仅 RemoveWidgetOnly）、`show`（功能格子经 CreateWidgetOfKindAsync 启用会话开关）、`hide`、`rename` |
| `Handlers/FileWidgetHandlers.cs` | `files/list`（活动 Items 集合）、`files/add`（ImportPathsAsync 受管导入管线，支持 move/copy） |
| `Handlers/SearchQueryHandler.cs` | `search/query`（Everything + DeskBox 内容；UI 线程经 EnsureSearchServicesForUserAction 惰性初始化） |
| `Handlers/GroupHandlers.cs` | `groups/merge`、`groups/dissolve`（WidgetManager 组方法自带 UI 封送，可无头调用） |
| `Handlers/OrganizeHandlers.cs` | `organize/plan`（扫描+规划，不动文件）、`apply`（执行缓存计划：建目录/移动/自动建格）、`undo`（按 historyId 回滚）；进程内 plan 缓存（10 分钟 TTL，容量 8） |

### 3.3 `src/DeskBox.Cli/`（新项目，7 文件）

`Program.cs`（全局参数解析 + mcp 分发）、`PipeRpcClient.cs`（连接重试/超时映射退出码）、
`CommandRouter.cs`（31 个子命令 → RPC 方法 + 人类可读格式化）、`HelpPrinter.cs`、
`DeskBoxInstanceScope.cs`（管道名作用域解析，与主程序算法一致）、`McpServer.cs`
（stdio MCP 2024-11-05，14 个粗粒度工具）、`DeskBox.Cli.csproj`。

### 3.4 测试（`tests/DeskBox.Tests/CommandApi/`）

`CommandApiProtocolTests.cs`（帧编解码/camelCase 信封/schema 往返）、
`CommandDispatcherTests.cs`（全部门禁路径：版本不符、未知方法、只读、破坏性、
校验失败、UI 调度、UI 忙超时）、`PipeRpcServerIntegrationTests.cs`
（真实管道 + 真实 CLI 客户端往返、垃圾帧 parse_error、顺序多客户端）。

### 3.5 文档与脚本

- `docs/architecture/command-api-v1.md` —— 协议契约（本文档的姊妹篇）
- `scripts/run-commandapi-smoke.ps1` —— 端到端冒烟（证据落盘 `.artifacts`）
- `docs/architecture/command-api-change-log.md` —— 本文档

### 3.6 设置面板

`src/DeskBox/Views/SettingsSections/CommandApiSettingsSection.xaml(.cs)` ——
三开关（启用/只读/破坏性）+ 审计路径（按数据根动态解析）+ CLI 用法内联代码块。

---

## 4. 修改过的既有文件（合并后基线 1.4.8 上的增量）

| 文件 | 改动 |
|---|---|
| `src/DeskBox/DeskBox.csproj` | +ProjectReference DeskBox.Protocol |
| `DeskBox.sln` | +DeskBox.Protocol / +DeskBox.Cli（AnyCPU 映射） |
| `src/DeskBox/App.xaml.cs` | OnLaunched 挂 `StartCommandApi()`；ShutdownApplicationAsync 挂 `StopCommandApi()` |
| `src/DeskBox/App.CommandApi.cs`（新 partial） | 组合 31 个 handler、UI 调度桥、Todo/文件 ViewModel 解析器、整理协调器工厂 |
| `src/DeskBox/Models/AppSettings.cs` | +`EnableCommandApi`（默认 true）/`CommandApiReadOnly`/`AllowDestructiveCommands` |
| `src/DeskBox/Services/SettingsService.cs` | 三开关接入默认偏好重置策略（ApplyDefaultPreferences） |
| `src/DeskBox/Services/WidgetManager.cs` | +`TryGetFileWidgetViewModel` / `TryGetTodoWidgetViewModel` / `GetWidgetConfigSnapshot` 三个命令 API facade；Todo 解析失败时输出诊断日志 |
| `src/DeskBox/Helpers/Win32Helper.cs` | `GetWindowTitle`/`GetWindowClassName` 可见性 private→public |
| `src/DeskBox/Views/SettingsWindow.xaml` | +CommandApiSettingsSection 元素 + General 节入口卡片 |
| `src/DeskBox/Views/SettingsWindow.xaml.cs` | +SectionRoutes["CommandApiSettings"] |
| `src/DeskBox/Views/SettingsWindow.Navigation.cs` | +分节元素映射 + RefreshFromSettings 钩子 |
| `src/DeskBox/Strings/*.json`（12 语言） | +14 个 `Settings.CommandApi.*` 本地化键 |
| `tests/DeskBox.Tests/DeskBox.Tests.csproj` | +ProjectReference DeskBox.Cli |
| `tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs` | 基线 29 文件/65 调用/41 上下文属主；owners 排序改为 **Ordinal**（根治 culture 排序脆弱性） |
| `tests/DeskBox.Tests/AotStage4D1BContractTests.cs` | Localized 冻结使用计数 168/137/334 |
| `tests/DeskBox.Tests/AotStage5B4*.cs`（8 个） | 冻结数字文本契约同步 29/65/41 |
| `CHANGELOG.md` | 1.4.9 - Unreleased 双语条目 |

---

## 5. 设计决策（与理由）

1. **JSON-RPC over 命名管道**（而非 HTTP）：无端口/防火墙问题、ACL 精确限同用户、
   NativeAOT 友好、管道名复用 `InstanceScope` 天然隔离 dev/preview/retail 实例。
2. **协议语言镜像 native ABI**：版本字段 + 能力位 + 稳定错误码 + `hint` 自纠错字段——
   复用仓库已验证的契约心智模型。
3. **注册表即 schema**：`deskbox schema` 由 `CommandRegistry` 生成，文档永不漂移。
4. **写命令双路径原则**：能无头的服务（QuickCapture）走管道线程；涉及 UI 状态的
   （Todo/文件/生命周期）走 UI 线程 ViewModel，宁可多一次调度也要保证
   循环生成/撤销栈/即时刷新一致。**教训**：todo/add 最初走 store 直写，
   与走 ViewModel 的 set-completed 产生数据源割裂（详见 §6-B1），已统一。
5. **破坏性双层门禁**：CLI `--yes` + 服务端 `AllowDestructiveCommands` 开关；
   `widgets/remove` 只暴露 RemoveWidgetOnly（`DeleteManagedFolder` 物理删盘路径不暴露）。
6. **设置写入走服务方法**：`settings/set` 仅暴露 theme/language（经
   ThemeService/LocalizationService 立即生效）；路径类/自锁类键永久拒绝。

## 6. 验证过程发现并修复的 BUG

| # | BUG | 根因 | 修复 |
|---|---|---|---|
| A1 | 管道服务器永远无法创建 | .NET 禁止 `PipeOptions.CurrentUserOnly` 与自定义 `PipeSecurity` 同时使用，每次创建抛异常后静默重试 | 去掉 CurrentUserOnly（ACL 已覆盖同用户威胁模型） |
| A2 | JSON-RPC 规范违规 | camelCase 策略把 `jsonrpc` 序列化为 `jsonRpc` | `[JsonPropertyName("jsonrpc")]` 显式固定 |
| A3 | 省略 arguments 的请求序列化崩溃 | 默认 `JsonElement`（Undefined）写入即抛 | 改 `JsonElement?` + WhenWritingNull |
| A4 | 同步抛出绕过错误映射 | handler 调用与客户端连接发生在 try 块外 | 调用移入 try（分发器与 CLI 客户端同修） |
| B1 | **todo/add 后 set-completed 找不到该项** | add 走 store 直写，写命令走 ViewModel，两个数据源割裂 | todo/add 统一改走 ViewModel（UI 线程），整族同源 |
| B2 | `widgets/show` 对功能格子创建后立即自关 | 会话级启用开关（如 `todoEnabled:false`）未生效 | 功能格子 show 改经 `CreateWidgetOfKindAsync`（自带启用+单例语义） |
| B3 | `deskbox mcp` 报未知命令 | McpServer 类已实现但 Program.cs 未分发 | 入口接线 + stdio 全链路实测 |
| B4 | CLI `todo edit` 文本拼入 itemId | tokens 跳数错误 | 重写参数解析（按位置归类） |
| B5 | 基线契约三次误报 | `Order()` culture 感知排序与手工数组顺序不一致 | actual 排序固定 `Order(StringComparer.Ordinal)`（根治，与 CommandRegistry 能力排序一致） |
| B6 | 单实例 create 返回错误 | 二次 create todo 走"显示现有"分支，diff 检测不到新 id | 返回现有 id + `created:false`；file/folder 仍报错 |

## 7. 功能覆盖矩阵（当前 31 个命令）

| 域 | 命令 | 状态 |
|---|---|---|
| 服务器 | ping / info / schema | ✅ 全部实测 |
| 设置 | settings/get ✅、settings/set（theme/language）✅ | 实测（Dark 切换即生效） |
| 待办 | list ✅ / add ✅ / set-completed ✅ / edit ✅ / set-due ✅ / delete ✅ / clear-completed ✅ | 真机全链路 |
| 随记 | list ✅ / add ✅ / pin ✅ / update ✅ / delete ✅ | 真机全链路 |
| 文件格子 | files/list ✅、files/add（--move/--copy）✅ | 真机（受管存储落位验证） |
| 格子生命周期 | list ✅ / create ✅ / show ✅ / hide ✅ / rename ✅ / remove（门禁验证）| 真机 |
| 分组 | groups/merge ✅ / dissolve ✅ | 真机 |
| 桌面整理 | organize/plan ✅ / apply ✅（72 项移动+自动建格）/ undo ✅（完整还原）| 真机三段闭环 |
| 搜索 | search/query ✅（引擎未初始化时错误+hint 正确；成功路径依赖 Everything 运行）| 部分实测 |
| MCP | initialize / tools/list（14 工具）/ tools/call ✅ | stdio 实测 |

未暴露（有意）：音乐/天气/Glance 控制、更新安装、备份恢复、
`widgets/remove` 的物理删盘路径、设置中存储路径/自锁类键。

## 8. 已知问题与遗留

- **测试环境性失败 1 个**：`FileServiceTests.EnumerateDirectoryAsync_RecognizesInternetShortcut…`
  在本机 Shell 环境下稳定失败，与 Command API 无关（合并前即失败），远程 CI 可复验。
- **杀软误报**：火绒曾将测试宿主进程判为风险并中止（全量测试偶发 abort），重跑即可；
  CLI/管道正常。
- **遗留可选域**（按价值排序，均已完成可行性勘察）：音乐 SMTC 控制、
  天气刷新/城市切换、Glance 翻页/图集、`search/history`、分组导航样式设置、
  `settings/set` 扩展（performanceMode 需 Policy 联动）。
- **organize/plan 为进程内缓存**：planId 仅本会话、10 分钟内有效（跨会话需重新 plan）。

## 9. 维护指南

- 新增命令：实现 `ICommandHandler` → 在 `App.CommandApi.cs` 注册 →
  若新增 JSON 上下文/序列化调用，**必须**联动 `JsonSerializationBaselineContractTests`
  与 8 个 `AotStage5B4*` 文本契约（跑一次测试看实际值）→ 更新
  `command-api-v1.md` 命令表与 CLI/MCP 表面 → CHANGELOG 双语条目。
- 破坏性命令：注册时 `Destructive: true` 即自动纳入服务端门禁；CLI 侧需 `--yes`。
- schema 测试：`deskbox schema` 的输出由注册表生成，禁止手改 golden。

---

# 第 3 轮开发记录：音乐 / 天气 / Glance（进行中 → 构建阻塞）

## R3.1 侦察结论（3 个并行智能体，全部完成）

- **音乐**：DeskBox 无自带播放器，经 SMTC 控制外部播放器。控制面在
  `MusicWidgetViewModel`（`TogglePlayPauseAsync`:180 / `PreviousAsync`:201 /
  `NextAsync`:212 / `SetSystemVolumeAsync(double 0-1)`:307，**无独立 Play/Pause 方法**）。
  widgetId → `MusicWidgetContentAdapter.ViewModel`。状态读 Title/Artist/PlaybackState/IsPlaying。
- **天气**：`WeatherService` 全无头（静态 HttpClient 6s 超时，MSN 主源 + Open-Meteo 回退，
  缓存 `data/weather-cache.json`）；城市/坐标存全局 AppSettings
  （WeatherCityName/Latitude/Longitude）；换城链路 =
  CitySearchService.SearchAsync → WeatherSettingsPolicy.TrySetManualLocation(internal static)
  → SaveDebounced → SettingsChanged 自动刷 UI。
- **Glance**：`GlanceWidgetStore.ForWidget(id).LoadAsync()` 无头可读（Layout/Transition/
  LocalImagePaths/RotationIntervalMinutes/RandomOrder，**无 CurrentIndex/IsPaused——纯运行时态**）；
  Store.Changed 事件使无头写自动刷 UI；换图/暂停必须走 VM（UI 线程），
  widgetId → `GlanceWidgetContentAdapter.ViewModel`。

## R3.2 已完成的实施（代码已写入，编译被阻塞，见 R3.4）

| 文件 | 内容 |
|---|---|
| `WidgetManager.cs` | +`TryGetMusicWidgetViewModel` / `TryGetGlanceWidgetViewModel` facade |
| `Handlers/MusicHandlers.cs`（新） | `music/status`（SMTC 快照：曲名/艺术家/播放态/系统音量%）、`music/toggle|previous|next`（SMTC 播放控制）、`music/volume`（系统主音量 0-100，Core Audio 后端）。全 UI 线程经 ViewModel；ok 反映播放器确认结果 |
| `Handlers/WeatherHandlers.cs`（新） | `weather/get`（无头，设置坐标强制/缓存取数，MSN+回退）、`weather/set-city`（地理编码 → TrySetManualLocation → SaveDebounced，UI 线程，格子自动刷新） |
| `Handlers/GlanceHandlers.cs`（新） | `glance/get`（无头读 store：布局/过渡/本地图数/轮播设置）、`glance/next`、`glance/toggle-pause`（UI 线程 VM） |
| `Protocol/CommandApiProtocol.cs` | +能力位 music.read/music.write/weather.read/glance.read/glance.write |
| `App.CommandApi.cs` | +11 个 handler 注册（音乐 5、天气 2、Glance 3——weather 服务经 DI `Services.GetService<T>()`）+ 2 个 ViewModel 解析器 |

设计取舍记录：
- music/play、music/pause 未实现——VM 无独立方法，仅 `TogglePlayPauseAsync`（SMTC 语义）。
- music/seek 未实现——`CommitSeekAsync` 有 `_isSeeking` 私有门槛，需先给 VM 加公开
  `SeekToAsync`（记录为待办）。
- weather/refresh 未实现独立命令——`weather/get` 的 `forceRefresh:true` 已覆盖强制取数。

## R3.3 CLI / MCP 扩充（待接线）

规划中的 CLI 子命令：`music status|toggle|next|previous|volume <id>`、
`weather get [--force]`、`weather set-city <name>`、`glance get|next|toggle-pause <id>`；
MCP 新工具：`music_control`、`get_weather`、`glance_control`。
**尚未写入 CommandRouter/McpServer/HelpPrinter**（被 R3.4 阻塞，避免半接线状态堆积）。

## R3.4 当前阻塞：STJ 源生成器整体未运行（构建失败）

**现象**：全项目 160 个 CS0534（"X 不实现 JsonSerializerContext 抽象成员"），
覆盖**所有** `JsonSerializerContext`——包括此前一直正常生成的既有上下文
（AppUpdateJsonContext 等）。删除 obj/bin、`--no-incremental`、
`dotnet build-server shutdown`、`-p:UseSharedCompilation=false` 均无效。

**诊断已排除**：
- 源代码无语法错误（160 个错误全部为 CS0534，无任何 CS1xxx/CS9xxx）；
- 无 SYSLIB 源生成器诊断输出；
- XAML 生成器正常产出 .g.cs（37 个），**STJ 生成器零输出**。

**结论（待验证）**：STJ 源生成器进程被外部因素终止/拦截——时间点与火绒
（Huorong）行为监控拦截测试宿主进程同期，疑似同一拦截源作用于
Roslyn IsolatedAnalyzer/源生成器进程；亦不排除生成器对某个新输入崩溃但诊断被吞。

**恢复指引（下次会话按序执行）**：
1. 向火绒添加排除项：`F:\DeskBox\工作\`、`C:\Users\Administrator\.dotnet\`、
   `%TEMP%`（或临时退出火绒）；
2. `dotnet build-server shutdown` 后重建；
3. 若仍失败：用 `dotnet build -bl:msbuild.binlog` + MSBuild Structured Log Viewer
   查看生成器诊断；或临时将 `MusicHandlers.cs` 等 5 个新 handler 文件移出编译
   （确认是否某个具体文件触发生成器崩溃）；
4. 恢复后从 R3.3 的 CLI/MCP 接线继续，然后按 §7 矩阵补真机 e2e
   （music 需要外部播放器在播；weather 需网络；glance 需先 widgets/create glance）。

## R3.5 本轮验证状态

- 主程序构建：**阻塞**（R3.4）
- CLI 构建：通过（本轮未触及 CLI 新命令）
- e2e：未开始（依赖 R3.4 恢复）
