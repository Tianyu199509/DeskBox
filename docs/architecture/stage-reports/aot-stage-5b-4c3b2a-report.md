# DeskBox AOT 阶段 5B-4C3B2A 完成与复盘报告

- 日期：2026-08-23
- 状态：已完成 x64 Native AOT 下的 activation grammar 与确定性 Todo 动作路由
- 审计配置：profile 55 / schema 52
- 本阶段范围：通知参数解析、正文打开路由、Complete、四种 Snooze、旧版 `snooze10`、非法输入拒绝、跨进程持久化与清理
- 明确保留：真实 Windows 通知点击、冷启动 activation、第二实例转发、Todo surface 实际定位/刷新和人工点击证据

## 1. 结论

5B-4C3B2A 已把上一阶段真实 payload 暴露的参数语法问题修正到产品代码，并把 Todo 通知动作收敛为一个可复用、可注入时钟和时区的确定性路由边界。

产品解析器现在同时接受 Windows App SDK 实际生成的 `;` 和既有 `&` 分隔格式。正文点击、Complete、Snooze `10m`、`30m`、`1h`、`tomorrow` 与旧版 `snooze10` 都进入同一个路由器；缺失 selection、未知 selection、未知 action、缺失目标和非 Todo 来源均明确拒绝，不再把异常输入静默降级为 10 分钟。

同一受审计 AOT 可执行文件连续启动三个全新进程，完成 18 条首次路由、2 条重启路由和清空后的 postflight。三个 PID 不同、EXE SHA-256 一致、相邻进程的 store 长度和哈希连续，正式 DeskBox 数据目录指纹不变，owned preview root 已清理。

本阶段没有弹出系统通知，也没有模拟真实 Windows activation。它证明的是产品参数语法、业务动作和进程间持久化，不把受控输入描述成真人点击或外部生命周期证据。

## 2. 开发前备份

开始本阶段前已对巨大脏工作区建立独立本地备份：

- 目录：`D:/project/wingezi-local-backups/wingezi-20260823-093750-pre-5b-4c3b2a/`
- ZIP：`D:/project/wingezi-local-backups/wingezi-20260823-093750-pre-5b-4c3b2a.zip`
- ZIP SHA-256：`DE01DE631BBBCAE2701319CFBC063314521F9FAD74832B52F47279E838A642E4`
- 备份时状态：326 个 Git 状态项，其中 124 个 tracked change、202 个 untracked entry
- 内容：1057 个文件、37,963,259 bytes，另含 all-refs Git bundle、工作区 patch、状态清单和 10 个选定证据文件
- 校验：状态在备份前后稳定，ZIP 可完整列举，bundle 校验通过

仓库根原有 `备份.zip` 保持原样，没有覆盖、移动或删除。

## 3. 产品实现

### 3.1 参数语法

`ParseNotificationArguments` 使用 `&` 和 `;` 两个分隔符，同时保留 URI unescape、大小写不敏感 key 和旧来源识别逻辑。该修正直接对应 5B-4C3B1 从真实系统 XML 中观察到的分号格式。

### 3.2 单一路由边界

新增 `TodoNotificationActivationRouter`，路由结果显式记录 disposition、成功状态、目标、动作、Snooze selection、计算期限以及三个 callback 是否请求。产品 `App` 只负责把真实服务和 UI callback 接入路由器，不再分别维护 Complete 与 Snooze 两套私有动作实现。

动作规则如下：

| 输入 | 结果 |
| --- | --- |
| Todo 来源、无 action | 请求打开目标；`view=today` 保留 Today 偏好；不修改 store |
| `action=complete` | 调用产品 `TodoReminderService.CompleteAsync`，成功后请求刷新 |
| `action=snooze` + `10m/30m/1h` | 使用注入时钟计算相对期限，调用 `SnoozeUntilAsync`，请求刷新与确认 |
| `action=snooze` + `tomorrow` | 按注入本地时区计算次日 09:00 |
| `action=snooze10` | 兼容旧 payload，等价于 `10m` |
| 缺失/未知 selection | `RejectedUnsupportedSnooze`，不修改 store、不触发 callback |
| 未知 action | `RejectedUnsupportedAction`，不修改 store |
| Complete/Snooze 缺少目标 | `RejectedMissingTarget`，不修改 store |
| 非 Todo 来源 | `NotTodoReminder`，不修改 store |

相对时间和 tomorrow 都使用注入的 `DateTimeOffset`/`TimeZoneInfo`，避免测试依赖当前机器时钟，也避免把 tomorrow 简化为固定 24 小时。

## 4. Native AOT 三进程证据

成功 run ID 为 `2520cacfa69c4024b7210bc8629330dd`：

| 阶段 | PID | 路由数 | item 数变化 | 结果 |
| --- | ---: | ---: | ---: | --- |
| RouteAndPersist | 36404 | 18 | 0 → 7 | 两种 grammar、正文打开、Complete、四种 Snooze、旧动作和五种拒绝全部通过 |
| VerifyAndClear | 3268 | 2 | 7 → 0 | 新进程重载全部状态，正文打开仍无修改，非法 selection 仍无修改，随后清空 |
| Postflight | 3460 | 0 | 0 → 0 | 第三个新进程确认清空状态稳定 |

首次 18 条路由由以下矩阵组成：

1. 分号正文打开和 ampersand 正文打开各 1 条；
2. Complete 首次与重复各 1 条；
3. `10m`、`30m`、`1h`、`tomorrow` 各执行首次和重复，共 8 条；
4. 旧版 `snooze10` 1 条；
5. 缺失 selection、未知 selection、未知 action、缺失目标、非 Todo 来源共 5 条。

固定时钟为 2026-08-25 08:15:00 +08:00，四种期限精确为 08:25、08:45、09:15 和次日 09:00。重复 Complete/Snooze 在固定时钟下保持 store 哈希不变。

三个进程使用的 EXE SHA-256 均为：

`81E1B18E5F5D27CF7AD7495486D4E294C46F5C43A68A6E7CEC85F97BE71CE49F`

正式数据运行前后均为 122 个文件、306,549,395 bytes，指纹保持：

`7304FC950C38D9E882C78AB470989B6DAE154B75D1F270897B7372185182B6B4`

成功证据归档在：

`.artifacts/aot-todo-notification-activation-smoke/win-x64/activation-runs/2520cacfa69c4024b7210bc8629330dd/`

最近一次成功索引为 `.artifacts/aot-todo-notification-activation-smoke/win-x64/activation-session.json`。

## 5. AOT 审计与回归

profile 55 / schema 52 的标准 x64 发布审计通过：

- 审计用时 264,732 ms，审计期间源码指纹稳定；
- 发布目录 39 个文件、92,790,741 bytes；符号目录 3 个文件、211,775,488 bytes；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 和原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- C3B2A 缺失场景、缺失产品、缺失 runner、禁止范围和目标源警告均为 0；
- C3B2A fixture 只有 1 处 source-generated JSON serialize；
- Rust ABI 2、能力 511、十个导出和 staging/publish 哈希门禁通过；
- 9 条路由单元测试和 6 条 C3B2A 阶段契约通过；
- 全部 AOT 相关测试 441/441，规范 x64 全量测试 2447/2447；
- Rust workspace 57/57，`cargo fmt --check` 和 Clippy `-D warnings` 通过；
- JSON 固定清单更新为 27 个文件、62/62 处 source-generated 调用和 25 个 context 所有者。

首次全量回归的 7 个失败均为历史静态契约仍断言上一轮 JSON 清单，或 C3B1 测试没有把新 router 纳入常量扫描。同步当前清单与源码所有者后，2447/2447 通过；没有修改产品行为来规避测试。

## 6. Rust 决策

本阶段不扩展 Rust。动作矩阵只有少量字典参数和最多 7 个 Todo item，不存在可量化的托管内存热点；主要复杂度来自 Windows activation 参数、现有 C# Todo 服务、UI callback 与应用生命周期。迁移 Rust 会引入字典/user-input ABI、时间与时区映射、异步持久化和 callback 反向调用，复杂度明显高于保留 C#。

这与既定原则一致：完整 Rust 能显著简化边界或降低可测内存占用时直接采用 Rust；对 WinRT/UI 生命周期和小状态路由不为迁移而迁移。生产 Rust 模块保持 ABI 2、能力 511 和十个导出。

## 7. 复盘、遗漏边界与下一阶段调整

C3B2A 预定的 grammar、确定性动作、拒绝、幂等、持久化、清空和 postflight 均已覆盖。审计后确认有三项不能被本阶段证据扩大解释：

1. fixture 直接调用产品解析器和路由器，没有触发真实 `AppNotificationActivatedEventArgs`；
2. callback 已证明正文打开、刷新和确认请求正确，但没有实际创建 Todo surface、定位 item 或观察 UI 刷新；
3. 当前第二实例转发只把 activation arguments 写入 pending 文件，`UserInput` 没有写入；运行中主实例读取后传入空字典，因此真实 Snooze 按钮经第二实例转发会丢失 selection。该缺口属于下一阶段，不应在 C3B2A 内用受控输入掩盖。

基于该复盘，原 5B-4C3B2B 建议再拆为两个子批次：

1. **5B-4C3B2B1：类型化 activation envelope 与单实例转发。** 先把 arguments、UserInput 和必要来源信息作为 source-generated JSON envelope 原子写入/读取；覆盖运行中主实例、冷启动恢复、第二实例信号、重复/损坏 envelope、消费后删除和唯一进程，继续用受控 activation，不声称真人点击。复杂度中高。
2. **5B-4C3B2B2：真实 Windows 通知 activation 与 Todo surface。** 使用真实临时通知分别点击正文、Complete 和 Snooze，覆盖运行中、冷启动和第二实例转发，观察唯一进程、目标 Widget/item 定位、实际 UI 刷新与 store 状态；自动证据和真人点击证据分开记录。复杂度高。

下一项建议先做 5B-4C3B2B1。这样先修复已经从代码审计中确认的 UserInput 丢失，再把真实通知点击和 UI 生命周期建立在稳定的转发协议上。C1C2B 与 C2B 的物理输入门仍作为发布前条件，不阻塞该独立代码批次。
