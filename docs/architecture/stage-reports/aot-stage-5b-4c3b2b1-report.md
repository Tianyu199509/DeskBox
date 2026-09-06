# DeskBox AOT 阶段 5B-4C3B2B1 完成与复盘报告

- 日期：2026-08-23
- 状态：已完成；代码、定向测试、x64 Native AOT 审计、五进程运行矩阵、全量回归和 Rust 回归均通过
- 审计配置：profile 56 / schema 53
- 本阶段范围：类型化 activation envelope、完整 `UserInput` 单实例转发、服务就绪门控、冷启动恢复、重复/损坏/旧格式处理和真实第二进程转发
- 明确保留：真实 Windows 通知点击、Todo surface 实际打开/定位/刷新和人工点击证据

## 1. 结论

5B-4C3B2B1 修复了上一阶段代码审计确认的实际缺口。第二实例不再只写 activation arguments 文本，而是将 arguments、`UserInput`、信封 ID、创建时间和来源 PID 写入 source-generated JSON 信封。每次 activation 使用独立文件原子发布，多个按钮动作不会互相覆盖。

主实例的 activation wait 不再在 Todo 服务恢复前注册。启动期命名事件先保留信号，Todo reminder 和 native notification 服务就绪后，主线程先检查待处理信号、排空全部信封，再注册长期监听。无信号冷启动也会排空上次进程退出前留下的信封，因此不会把 `ServiceUnavailable` 当成已消费成功。

本阶段的 AOT 运行矩阵使用四个主进程和一个真实第二进程。第二进程实际进入产品 mutex、信封写入、事件唤醒和退出路径；它不是对路由器的直接调用。通知输入本身仍由严格的 NativeAOT-only fixture 提供，不把它描述成 Windows 通知中心真人点击。

## 2. 产品实现

### 2.1 类型化信封与 spool

`NativeNotificationActivationEnvelopeStore` 使用 schema 1，保存以下字段：

- `EnvelopeId`
- `CreatedAtUtc`
- `SourceProcessId`
- `Arguments`
- `UserInput`
- `IsLegacyArgumentsOnly`

信封使用独立的 camelCase source-generated `JsonSerializerContext`。写入先生成同目录临时文件，再以不覆盖方式移动到最终 `.json`；固定 ID 的重复信封返回 `Duplicate`。读取按时间前缀排序，将文件原子改名为 claim 后校验和消费；损坏、超限或 schema 不支持的信封返回 `Rejected` 并删除，正常队列继续排空。

末次代码复盘还发现并封闭了一个异常退出窗口：如果领取信封的进程在 claim 改名后被强制结束，下一次消费会识别 claim 中的 owner PID；只有确认该进程已经退出或 PID 已复用时才恢复原文件，活跃 owner 的 claim 不会被抢走。对应单元测试使用不存在的 owner PID 验证遗留 claim 可被恢复且只消费一次。

边界限制包括 64 KiB 信封、8 KiB arguments、16 个输入项，以及 key/value 长度限制。旧版 `pending-notification-activation.txt` 在新 spool 为空时迁移为 arguments-only 信封，保持旧 `snooze10` 等无需 `UserInput` 的兼容路径。

### 2.2 单实例与启动就绪

第二实例现在读取完整 `NativeAppNotificationActivation` 并存储类型化信封，随后只负责唤醒已有实例并退出。主实例消费后把 `envelope.Arguments` 和原始 `envelope.UserInput` 一起交给现有通知路由。

长期 activation listener 延后到 Todo reminder 与 native notification 服务创建以后注册。启动期间发生的命名事件由 auto-reset event 保存；主线程在服务就绪时先做一次非阻塞检查。没有事件的新进程仍执行恢复性排空，以覆盖前一主进程在写入后、消费前退出的情况。

排空按每批最多 128 条让出 UI；批末若 store、旧格式文件或可恢复 claim 仍有待处理项，会再次设置同一个 auto-reset event。启动期监听尚未注册时信号仍会保留，运行期则调度下一批，因此批次上限不会把第 129 条以后无限期留在 spool 中。

## 3. Native AOT 五进程矩阵

矩阵包含以下阶段：

| 阶段 | 进程角色 | 验证内容 |
| --- | --- | --- |
| SeedColdStart | 主进程 1 | 写入两项 Todo、一个有效 `30m` 信封、同 ID 重复信封和一个损坏信封 |
| ColdStartConsume | 主进程 2 | 无新外部信号冷启动，拒绝损坏信封，保留 `UserInput=30m` 并持久化精确 snooze |
| PrimaryAwait | 主进程 3 | 写入 Ready 证据并等待真实第二进程 |
| SecondaryForward | 第二进程 | 使用相同 preview root 启动，命中 mutex，写入 `UserInput=tomorrow`，唤醒主进程并退出 |
| Postflight | 主进程 4 | 新进程重载两个精确 snooze，确认 spool 为空并清理 fixture store |

runner 要求五个 PID 全部不同、四个主进程自然退出、第二进程在主进程保持存活时退出、全部结果和受审计 EXE 使用同一 SHA-256。正式 DeskBox 数据目录在运行前后以文件数、总字节和确定性元数据指纹核对。

fixture 固定时钟为 2026-08-25 08:15:00 +08:00。冷启动结果应为 08:45，第二实例 `tomorrow` 结果应为次日 09:00。AOT-only 分支抑制该 fixture 的 snooze 确认通知，系统通知展示与 Windows activation 均不在本阶段触发。

## 4. AOT 审计与回归

### 4.1 正式 x64 Native AOT 审计

`publish-aot-audit.ps1 -Platform x64` 在 profile 56 / schema 53 下通过：

- 审计耗时 258,943 ms，`sourceStableDuringAudit=true`；
- publish 39 个文件、92,945,877 bytes；symbols 3 个文件、212,373,504 bytes；
- `DeskBox.exe` SHA-256 为 `E5E4D95B9E386BADB69243280B975AFDD6DF25FB09638D2ABC0C87F25391E78C`；
- WMC1506=0、WMC1510=1211、完整 `always-throw`=0，原始 IL2026/IL2050/IL2072/IL2075/IL3050 均为 0；
- B2B1 的 scenario/product/runner 缺失模式、越界模式和目标源码 warning 均为 0；
- fixture 1 处、信封 store 2 处 source-generated JSON 调用；JSON 固定清单为 29 个文件、65/65 处调用和 27 个 context 所有者；
- Rust ABI 2、能力 511、十个导出保持不变。

### 4.2 五进程转发矩阵

成功 run ID 为 `731d469f4233482d84ae9a721350c1dd`，归档位于 `.artifacts/aot-todo-notification-forwarding-smoke/win-x64/forwarding-runs/731d469f4233482d84ae9a721350c1dd`。PID 为：

- SeedColdStart：29428；
- ColdStartConsume：40900；
- PrimaryAwait：26052；
- Postflight：42576；
- 真实第二实例：41684。

五个 PID 全部不同，四个主进程均自然退出，第二实例退出时主实例仍存活；全部进程使用同一受审计 EXE 哈希。Seed 留下一个有效项和一个损坏项，同 ID 第二次写入为 `Duplicate`。ColdStartConsume 拒绝 1 个损坏项、消费来源 PID 29428 的有效项，保留 `todoSnooze=30m`，精确写入 2026-08-25 08:45:00 +08:00。PrimaryAwait 消费来源 PID 41684 的真实第二实例信封，保留 `todoSnooze=tomorrow`，精确写入 2026-08-26 09:00:00 +08:00。Postflight 重载两项结果、确认 spool 为 0、清空 fixture store 并删除 owned preview root。

正式数据目录前后均为 122 个文件、306,937,429 bytes，指纹均为 `13A138D6C0BB41A2B4348708CB7E6B1FAE6D6F54DD08B4C61980DDD33DA29ACD`。`SystemNotificationAttempted=false`、`ExternalWindowsActivationAttempted=false`，因此证据边界没有扩大。

### 4.3 自动回归

- activation envelope store 与 JSON 定向测试：21/21；
- B2B1 阶段契约：6/6；
- 全部 AOT 相关测试：447/447；
- x64 全量测试：2462/2462；
- Rust workspace：57/57；`cargo fmt --all --check` 与 Clippy `-D warnings` 均通过；
- 23 个 PowerShell 脚本解析错误为 0，`git diff --check` 通过；仅输出工作区既有的 LF/CRLF 提示。
- 规范非平台 Debug 构建为 0 错误、24 个既有警告；最终仅运行一个仓库实例，PID 38860，路径为 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`。

## 5. Rust 决策

本阶段不扩展 Rust。这里的数据规模最多是少量短字符串和字典项，内存收益不可量化；复杂度集中在 WinUI 应用启动、Windows App SDK activation、命名 mutex/event 和 C# Todo 服务生命周期。把这段状态机跨到 Rust 会新增 JSON/字典 ABI、进程间错误映射和 UI callback 反向边界，不能降低整体复杂度。

## 6. 复盘与下一阶段

本阶段只把已经确认的转发协议和启动时序修稳，没有提前扩大到真实通知点击。后续调整为 **5B-4C3B2B2**：

1. 从真实 Windows 通知分别触发正文、Complete 和 Snooze；
2. 覆盖运行中主实例、冷启动与第二实例；
3. 验证唯一进程、真实 `AppNotificationActivatedEventArgs` 和 `UserInput`；
4. 观察 Todo surface 实际打开、目标 Widget/item 定位和动作后的可见刷新；
5. 自动证据与用户人工点击证据分开记录。

5B-4C3B2B2 复杂度高于本阶段，主要风险是通知中心和应用生命周期的外部时序，以及 UI surface 的可见状态取证。Rust 对这一层没有明显收益，继续使用 C#/WinRT 更合适。
