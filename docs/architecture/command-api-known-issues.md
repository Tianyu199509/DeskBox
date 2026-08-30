# Command API — 当前问题清单（Known Issues & Blockers）

> 状态快照：2026-08-30。配套文档：`command-api-v1.md`（协议契约）、
> `command-api-change-log.md`（开发记录全文）。
> 问题按优先级排列；每条含现象、诊断、已试措施、下一步。状态同步记录于 `command-api-change-log.md` §R3。

---

## P0-1（已解决 2026-08-30）STJ 源生成器整体未运行，主程序无法编译

**根因（已确认并修复）**：新增 `WeatherHandlers.cs` 中声明的 JSON 上下文类名
`WeatherJsonContext` 与 `WeatherService.cs` 里**既有的同名 partial 类冲突**。
两个同名 partial 合并后，STJ 生成器为重复的 hintName
（`WeatherJsonContext.Boolean.g.cs` 等）抛 ArgumentException（以 warning CS8785
呈现），随后**放弃整个程序集的全部源生成输出**——160 个 CS0534 全部为连锁症状。
与火绒无关（实时防护提示出现于生成器异常路径，属巧合）。
**修复**：新文件中的类改名 `WeatherCommandJsonContext`，一行改名即恢复编译。
**防御措施**：已在 change-log §9 维护指南中固化"新上下文类名必须全仓库唯一"
检查项。

**现象**
- `dotnet build src/DeskBox -c Debug -p:Platform=x64` 报 **160 个 CS0534**：
  "X 不实现 JsonSerializerContext 抽象成员
  （GeneratedSerializerOptions.get / GetTypeInfo(Type)）"。
- 受影响范围是**全部** `JsonSerializerContext`（83 个声明 × 2 条错误），
  包括与本次改动无关、此前一直正常生成的（如 `AppUpdateService.AppUpdateJsonContext`）。
- XAML 生成器正常（obj 下有 37 个 XAML `.g.cs`），**STJ 生成器零输出**。

**诊断时间线**
1. 触发点：本轮新增 `MusicHandlers.cs` 时曾在内插原始字符串上出现 CS9007
   （`$$"""..."""` 中 JSON 的连续 `}}}` 超出 `$` 数量允许）。已改写为常规内插
   字符串并逐字节核验合法（`$"...{{..}}...{method}..."`）。
2. 修复后错误收敛为纯 CS0534——**源代码层面已无任何语法错误**
   （160 条错误全部 CS0534，无 CS1xxx/CS9xxx）。
3. Roslyn 行为：存在语法/语义错误时源生成器会被跳过，从而产生连锁 CS0534——
   但当前已无此类错误，连锁却依旧存在。

**已尝试措施（全部无效）**
- `dotnet clean` + `--no-incremental`
- 物理删除 `src/DeskBox/obj`、`src/DeskBox/bin`（及 Protocol/Cli 的 obj）
- `dotnet build-server shutdown`（关闭 MSBuild 与 VB/C# 编译器服务）
- `-p:UseSharedCompilation=false`（禁用共享编译进程）
- 全量错误码分布核查（确认无隐藏语法错误）、SYSLIB 生成器诊断核查（无输出）

**结论假设（按可能性排序）**
1. **杀软拦截**：火绒（Huorong）行为监控此前已确认拦截过测试宿主进程；
   Roslyn 源生成器运行在独立的 IsolatedAnalyzer 进程中，疑似被同一机制
   终止或其输出文件被拦截，且失败表现为静默（无 CS8785 生成器异常诊断）。
2. 生成器对某个新输入崩溃但诊断被构建输出过滤（未观察到 SYSLIB）。
3. 环境/资源问题（C 盘可用约 7 GB，未证实相关）。

**恢复步骤（按序执行）**
1. 为火绒添加信任/排除：`F:\DeskBox\工作\`、`C:\Users\Administrator\.dotnet\`、
   `%TEMP%`（或临时退出火绒实时防护）；
2. `dotnet build-server shutdown` 后重新构建；
3. 若仍失败：`dotnet build -bl:msbuild.binlog`，用 MSBuild Structured Log
   Viewer 检查 csc 的 analyzer 诊断与生成器异常；
4. 二分定位：临时将 5 个新 handler 文件
   （Music/Weather/Glance/Group/SearchQuery + SettingsSet/Organize）
   移出编译，确认是否某个具体文件触发生成器崩溃；
5. 恢复编译后，从 change-log §R3.3 继续（CLI/MCP 接线未完成），
   并按 §7 矩阵补真机 e2e（music 需外部播放器在播、weather 需网络、
   glance 需先 `widgets/create glance`）。

**影响范围**
- 仅 `src/DeskBox`（WinUI 主程序）编译失败；
- `DeskBox.Protocol`、`DeskBox.Cli` 可独立编译（CLI 已验证成功）；
- 测试工程因引用主程序而连带失败。

---

## P1-1 测试宿主进程被杀软终止（间歇性）

**现象**：`dotnet test` 全量运行偶发"测试运行已中止"（此前一次 1994/1994 后中止）。
**诊断**：火绒行为监控将 testhost.x64.exe 误判（用户已确认）。
**规避**：重跑即可完成（后续多次全量均完成）；或给火绒加排除项。
**状态**：环境性，非代码问题。

## P1-2 FileService `.url` 枚举测试在本机稳定失败

**现象**：`FileServiceTests.EnumerateDirectoryAsync_RecognizesInternetShortcutAndHidesExtension`
断言"集合非空"失败（枚举结果为空）。
**范围**：与 Command API 无关（合并前的 1.4.5 快照上同样失败）；
推测与本地 Shell/`.url` 解析环境有关。
**状态**：环境性；远程 CI 环境可复验。全量回归按 3017/3018 计。

## P1-3 磁盘空间紧张（C 盘）

**现象**：C 盘可用约 7 GB（99% 使用）。NuGet 全局缓存在 C 盘（4 GB）。
**风险**：restore/构建临时文件可能失败（已出现过一次"磁盘空间不足"告警）。
**缓解**：定期 `dotnet nuget locals all --list` 清理，或将 NUGET_PACKAGES 迁至 F 盘。

---

## P2-1 已知功能限制（设计取舍，非 BUG）

| 项 | 说明 |
|---|---|
| `music/play`、`music/pause` 未提供 | VM 仅有 `TogglePlayPauseAsync`，无独立方法（SMTC 语义）；如需拆分须先给 VM 加公开 Play/Pause 包装 |
| `music/seek` 未提供 | `CommitSeekAsync` 有 `_isSeeking` 私有门槛，需先给 VM 增加公开 `SeekToAsync(TimeSpan)` |
| `weather/refresh` 未单独提供 | `weather/get` 的 `forceRefresh:true` 已覆盖强制取数语义 |
| organize/planId 为进程内缓存 | 仅本 DeskBox 会话、10 分钟内可 apply；跨会话需重新 plan |
| `quickcapture/delete` 无恢复 | 服务层有 `RestoreDeletedItemAsync` 但需要快照对象，RPC 难序列化；文档已标注"永久删除" |
| feature-widget 单实例 | todo/glance/music/weather/search 重复 create 返回现有 id + `created:false` |
| 搜索依赖 Everything | 文件搜索需 Everything 运行（voidtools.com）；DeskBox 内容（随记/待办/标题）不受影响；搜索功能需在设置中启用 |

## P2-2 尚未暴露的域（按价值排序，均已完成可行性勘察）

1. 音乐 SMTC 会话枚举/切换（`SelectSessionAsync` 已存在）
2. `weather/search-city`（地理编码查询，未暴露为独立命令）
3. Glance 在线图集刷新（`RefreshOnlineImagesAsync` 需包装公开方法）
4. 分组导航样式/轮播设置（`SetWidgetGroupNavigationStyleAsync` 等）
5. `settings/set` 扩展（performanceMode 需 PerformanceSettingsPolicy 联动重写明细）
6. 更新检查/下载（只读安全；**安装与恢复类永不暴露**）

## P2-3 文档与实现的状态差（阻塞恢复后需处理）

- change-log §R3.3 所列 CLI/MCP 接线未完成（music/weather/glance 的 11 个命令
  已在服务端注册并计入 schema，但 CommandRouter/HelpPrinter/McpServer 尚未扩充）；
- `command-api-v1.md` 命令表尚未包含本批新命令（恢复编译验证后统一更新）。

---

## 历史问题（已修复，详见 change-log §6）

A1 管道 ACL 与 CurrentUserOnly 互斥 · A2 `jsonrpc` 大小写 · A3 可空 JsonElement ·
A4 try 外调用绕过错误映射 · B1 todo 数据源割裂 · B2 功能格子 show 自关 ·
B3 mcp 未接线 · B4 CLI edit 解析 · B5 文化排序脆弱 · B6 单实例 create 报错。
