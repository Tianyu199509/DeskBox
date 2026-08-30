# Command API 实施记录（改动全清单）

状态：随 1.4.9 开发推进持续更新。
关联契约：[command-api-v1.md](command-api-v1.md)。
本文记录命令 API 从立项到当前的全部改动、修复的缺陷、验证结论与遗留项，供回溯与后续开发参考。

---

## 1. 总览

| 阶段 | 内容 | 提交 |
|---|---|---|
| 立项 | 命令 API 立项方案（传输/协议/安全/分期） | — |
| M1 | Protocol 项目 + 管道服务器 + CLI（9 命令）+ MCP 服务器 | `bbafa46` |
| 设置面板 | Settings → 常规新增"命令 API"分节（12 语言） | `6695fdc` |
| MCP 接线修复 | `deskbox mcp` 子命令入口缺失修复 + stdio 全链验证 | `8d90034` |
| 上游合并 | 1.4.6 定稿基座合并（快照→同步→增量重放三步法） | `dfd860d`、`361bb2c`、`4c698c0`（备份分支） |
| 契约计数同步 | Localized 使用计数 168/137/334 | `3299489` |
| 控制面扩展 | 10 智能体盘点后新增 15 个命令（待办/随记/文件/生命周期） | `6ddeabb` |
| 可选项推进 | 搜索/分组/整理两段式/设置写入（+7 命令，共 31）+ 质量优化 | 本次 |

## 2. 新增项目与文件

### 2.1 新项目

- `src/DeskBox.Protocol/`（7 文件）：协议版本/错误码/能力位、JSON-RPC 信封、帧编解码、schema 记录、JSON source-gen 上下文。零依赖、零反射（NativeAOT 安全）。
- `src/DeskBox.Cli/`（7 文件）：管道客户端（重试/超时/退出码）、命令路由、人类/JSON 双格式化、帮助文本、`DeskBoxInstanceScope`（与主程序一致的管道名解析）、**MCP stdio 服务器**（2024-11-05 协议）。

### 2.2 主程序内新增

- `src/DeskBox/Services/CommandApi/`：
  - `CommandRegistration.cs`（注册元数据/线程亲和/参数工具）、`CommandRegistry.cs`、`CommandDispatcher.cs`（策略门禁/错误信封/UI 忙超时）、`PipeRpcServer.cs`（ACL/帧循环/审计）
  - `Handlers/`：Server（ping/info/schema）、SettingsGet、SettingsSet、QuickCapture（list/add/pin/update/delete）、Todo（list/add/set-completed/edit/set-due/delete/clear-completed）、Widgets（list/create/remove/show/hide/rename）、Files（list/add）、Search（query）、Groups（merge/dissolve）、Organize（plan/apply/undo）
- `src/DeskBox/App.CommandApi.cs`：命令 API 组合根（31 个命令注册、resolver、启停）
- 设置面板：`Views/SettingsSections/CommandApiSettingsSection.xaml(.cs)` + SettingsWindow 三处接线 + 12 语言字符串（14 key × 12 文件）
- 冒烟脚本：`scripts/run-commandapi-smoke.ps1`
- 测试：`tests/DeskBox.Tests/CommandApi/`（协议/分发器/管道集成 3 套）

### 2.3 既有文件修改点（合并时需重点保护的增量）

| 文件 | 改动 |
|---|---|
| `DeskBox.sln` | 挂入 Protocol（GUID 7C4E1A2B-…）/Cli（8D5F2B3C-…） |
| `src/DeskBox/DeskBox.csproj` | +ProjectReference → DeskBox.Protocol |
| `src/DeskBox/App.xaml.cs` | OnLaunched 末尾 `StartCommandApi()`；`ShutdownApplicationAsync` 开头 `StopCommandApi()` |
| `src/DeskBox/Models/AppSettings.cs` | +`EnableCommandApi`(默认 true)、`CommandApiReadOnly`、`AllowDestructiveCommands` |
| `src/DeskBox/Services/SettingsService.cs` | `ApplyDefaultPreferences` 加入三开关重置（API=true/其余=false） |
| `src/DeskBox/Helpers/Win32Helper.cs` | `GetWindowTitle`/`GetWindowClassName` 由 private → public |
| `src/DeskBox/Services/WidgetManager.cs` | 命令 API facade：`TryGetFileWidgetViewModel`、`TryGetTodoWidgetViewModel`、`GetWidgetConfigSnapshot`（含诊断日志） |
| `tests/DeskBox.Tests/DeskBox.Tests.csproj` | +ProjectReference → DeskBox.Cli |
| 契约测试 | JSON 序列化冻结基线（29 文件/65 调用/41 上下文属主，expected 改为代码内 Ordinal 排序）；AotStage4D1B 本地化计数；8 个 Stage5B4 文本契约同步 |

## 3. 命令面（31 个）

- **服务器**：`server/ping`、`server/info`、`server/schema`
- **设置**：`settings/get`（白名单只读）、`settings/set`（theme/language 白名单，经 ThemeService/LocalizationService 即时生效）
- **随记**（无头，自动刷 UI）：`quickcapture/list`、`add`、`pin`、`update`、`delete`
- **待办**（UI 线程经 ViewModel）：`todo/list`（无头）、`add`、`set-completed`、`edit`、`set-due`、`delete`、`clear-completed`
- **文件格子**（UI 线程）：`files/list`、`files/add`（move/copy 语义，走受管文件夹导入管线与整理历史）
- **格子生命周期**（UI 线程）：`widgets/list`（v2：id/kind/name/坐标/映射路径）、`create`（单实例返回现有 id + `created:false`）、`show`（功能格子自动启用会话开关）、`hide`、`rename`、`remove`（破坏性门禁；文件夹内容始终保留）
- **分组**（WidgetManager 自封送）：`groups/merge`、`groups/dissolve`
- **搜索**（UI 线程惰性初始化）：`search/query`（Everything 文件 + DeskBox 内容）
- **桌面整理**：`organize/plan`（无头预览，planId 缓存 10 分钟）、`organize/apply`（UI 线程执行，返回 historyId）、`organize/undo`（按 historyId 回滚）

## 4. 设计要点（为什么这样做）

1. **待办全部走 ViewModel**：Todo 无文件监视器，store 直写对已打开格子不可见；且 set-completed 的循环下一期生成、撤销栈都在 ViewModel。曾实测混用（add 直写 store + 其余走 VM）导致 done 找不到刚加的项——已统一。
2. **随记全部走服务**：QuickCaptureService 信号量串行 + Changed 事件自动刷 UI，无头即正确。
3. **单实例守卫**：`widgets/create todo` 在已存在时返回现有 id + `created:false`，不再报"no new widget id"。
4. **破坏性双门禁**：`widgets/remove` 需 CLI `--yes` 且服务端 `AllowDestructiveCommands=true`，两者缺一不可；受管文件夹内容永不删除。
5. **排序确定性**：基线契约的 expected/actual 统一 `Order(StringComparer.Ordinal)`，消除文化排序脆弱性（曾三次踩坑）。

## 5. 修复的缺陷（按发现顺序）

| # | 缺陷 | 根因 | 修复 |
|---|---|---|---|
| 1 | 管道服务器永远无法创建 | .NET 禁止 `PipeOptions.CurrentUserOnly` 与自定义 `PipeSecurity` 同时使用 | 去掉 CurrentUserOnly，ACL 覆盖同威胁模型 |
| 2 | `jsonRpc` 违反 JSON-RPC 规范 | camelCase 命名策略作用于 `JsonRpc` 属性 | `[JsonPropertyName("jsonrpc")]` |
| 3 | 缺省 arguments 的请求序列化崩溃 | `default(JsonElement)`（Undefined）序列化抛异常 | 信封字段改 `JsonElement?` + WhenWritingNull |
| 4 | 分发器/客户端同步抛出绕过错误映射 | handler 调用/连接在 try 块之外 | 移入 try；连接失败映射 CliException |
| 5 | `deskbox mcp` 报未知命令 | McpServer 类未接线 | Program.cs 分发分支 |
| 6 | uptime 序列化为小数致 CLI 解析崩 | double 进 int 解析 | 契约字段改 int + 格式器容错 |
| 7 | 功能格子 show 后立即自关 | 会话启用开关未置位 | show 对功能格子路由 `CreateWidgetOfKindAsync` |
| 8 | CLI `todo edit` 文本拼接 itemId | 参数切片偏移 | 顺序解析重写 |
| 9 | todo add 与 set-completed 数据源割裂 | add 直写 store、其余走 VM | add 统一走 ViewModel（UiThread） |
| 10 | search/query 永远报引擎不可用 | `EnsureSearchServicesForUserAction` 强制 UI 线程 | handler 改 UiThread 亲和 |

## 6. 验证记录（全部真实执行）

- **测试**：CommandApi 专项 22/22；基线与契约族 258/258；全量 3,017/3,018（唯一失败为 `FileServiceTests.EnumerateDirectoryAsync_RecognizesInternetShortcutAndHidesExtension`，本机 Shell 环境性失败，合并前即存在，远程 CI 可复验）。
- **真机端到端**（dev 数据根隔离 + 真实 DeskBox 进程）：
  - 服务器三命令、schema 自发现（31 命令）
  - 设置面板：入口/分节渲染、主题 set→get 回读一致
  - 待办：建格子→add→done→edit→set-due→delete→clear-completed→list 全链
  - 随记：add→pin→update→delete 全链
  - 文件：`files add --copy/--move`→受管存储→`files/list` 核对
  - 生命周期：create→show（功能格子自动启用）→hide→rename→remove 门禁（CLI 与服务端双重拒绝）
  - 单实例：重复 create 返回现有 id + `created:false`
  - 整理：plan（72 项 3 类预览，零移动）→ apply（全移动+自动建 3 格子+historyId）→ undo（桌面与格子完整还原）
  - MCP：initialize / tools/list（含全部工具与 inputSchema）/ tools/call 全链
  - 审计日志：每命令一行（方法/客户端 PID/结果/耗时）

## 7. 遗留与后续（按优先级）

1. `music/*`（SMTC 控外部播放器）、`weather/refresh`、`glance/*`——中等价值，下轮。
2. `organize/plan` 目前仅进程内缓存（10 分钟 TTL、跨进程不可用）——如需跨会话执行需持久化计划。
3. `settings/set` 白名单仅 theme/language；性能模式需联动 Policy，后续按需扩。
4. CLI 进安装器/PATH、设置面板搜索索引、CLI NativeAOT 发布。
5. FileService `.url` 测试的本机环境性失败待远程 CI 复核。
