# DeskBox AOT 阶段 5B-4C3A 完成报告

- 日期：2026-08-23
- 状态：5B-4C3A 已完成到 x64 Native AOT 五进程实际运行边界
- 审计配置：profile 53 / schema 50
- 本阶段范围：Todo 到期候选、提醒控制、稍后提醒、完成、重复任务生成、跨进程 dismissal、清理与 postflight
- 明确保留：真实 Windows 通知展示、通知中心清理、用户点击 activation、冷启动/既有实例转发与 Todo 窗口定位，归入 5B-4C3B

## 1. 结论

5B-4C3A 已闭环 Todo recurrence/reminder 中不依赖系统通知 UI 的确定性状态矩阵。五个使用同一受审计 AOT 产物、同一隔离 preview 根和同一 owned Todo store 的全新进程，依次完成种子与 snooze、snooze 到期与完成、下一次重复任务提醒、跨进程 dismissal 恢复、清空和 postflight。

本阶段直接执行现有 `TodoReminderService`、`TodoRecurrenceService`、`TodoWidgetStore` 和 `SettingsService`。生产服务代码不需要为测试改写；既有 internal 注入边界已经允许固定 clock、owned store factory、空 DispatcherQueue 和提醒回调。AOT fixture 只把候选通知记录为结构化 evidence，不调用 `ShowTodoReminderNotification`、`NativeAppNotificationService` 或托盘通知。

最终结果证明：

1. 默认 5 分钟偏移和单项 5 分钟偏移同时产生两个候选，且只调用一次聚合回调；
2. reminder-off、已完成和超过 1 分钟 grace 的过期任务均被跳过；
3. recurring 项 snooze 10 分钟后，9 分钟时不触发，新进程在 10 分钟时只触发一次；
4. 完成 recurring 项会生成次日 occurrence，并重置完成、提醒、dismissal 和 snooze 状态；
5. 下一 occurrence 在提醒点前 1 秒不触发，到点只触发一次；
6. 再次启动的新进程不会重复已持久化 dismissal；
7. product store 清空后，最后一个新进程重载为空，扫描不写回、不回调。

## 2. 实现边界

新增的 NativeAOT-only 场景只接受：

- `DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE=DeterministicStateMatrix`；
- 五个固定 phase；
- 32 位 `Guid N` run ID；
- 显式隔离的 Native AOT preview 根。

固定基准时钟为 `2026-08-24T01:00:00Z`。种子 store 包含 recurring、默认偏移、reminder-off、已完成和 stale overdue 五项。所有初始时间、ID、排序和 recurrence series 都由 fixture 固定；产品生成的下一 occurrence ID 保持真实随机语义，验证使用源项与生成项之间的关系，不伪造产品 ID。

每个 phase 都记录：

- 可执行文件路径、SHA-256、PID、preview/fixture/result 根；
- 固定时钟、检查返回计数、回调内容和显式步骤；
- Todo store 文件长度、SHA-256、版本和完整 item 状态；
- phase 前、中、后的 due、recurrence、completion、reminder、dismissal、snooze、series 和 generated-next 字段；
- `NotificationChannel=CapturedCallbackOnly`、`SystemNotificationAttempted=false` 和正常关闭请求。

结果使用独立 source-generated JSON context，完成与失败都原子写入并进入应用正常关闭路径。普通 JIT 不包含该 fixture。

## 3. 五进程实际证据

成功 run ID 为 `9f4a18f9d5aa4bc0ba9e7349a39ba61a`：

| 阶段 | PID | before/after item | CheckNow 返回 | callback | 显式步骤 |
| --- | ---: | ---: | --- | ---: | ---: |
| SeedAndSnooze | 36236 | 0 / 5 | 2, 0 | 1 | 9 |
| SnoozeAndComplete | 35904 | 5 / 6 | 0, 1, 0 | 1 | 11 |
| NextOccurrence | 37388 | 6 / 6 | 0, 1, 0 | 1 | 8 |
| Restore | 38936 | 6 / 0 | 0 | 0 | 6 |
| Postflight | 34000 | 0 / 0 | 0 | 0 | 5 |

五个 PID 全部不同，五次均自然退出，相邻 phase 的前后 store SHA-256 和长度连续一致。所有进程使用的 EXE SHA-256 相同：

`A0FA626BCA955721AF2AC9CF7D6AC040611AEA342E293C221763050AA6282AC7`

正式数据运行前后均为 122 个文件、306,209,852 bytes，指纹保持：

`C021FDC60EB4F5DA32628DF348F73C8A79D0FA6DB95B5D9390BB8B93A8B386E8`

成功 evidence 归档在：

`.artifacts/aot-todo-recurrence-reminder-smoke/win-x64/todo-runs/9f4a18f9d5aa4bc0ba9e7349a39ba61a/`

最近一次成功索引为 `.artifacts/aot-todo-recurrence-reminder-smoke/win-x64/todo-session.json`。owned preview 根在标记、仓库根和 data root 三项复核后删除，没有残留受审计 AOT 进程。

## 4. 通知证据边界

本阶段证明的是提醒候选与动作后的持久化状态，不是 Windows 通知展示或用户点击。

结构化结果固定记录：

- `notificationChannel=CapturedCallbackOnly`；
- `systemNotificationAttempted=false`；
- 日志不得包含 Native notification shown 或 Tray notification fallback shown；
- 主应用 preview settings 中 Todo 与 Todo reminder 均关闭，避免全局生产计时器进入真实通知路径。

因此 C3A 不声称通知出现在通知中心，不声称按钮、组合框或用户输入可用，也不声称冷启动或已有实例 activation 已通过。

## 5. AOT 审计与回归

profile 53 / schema 50 的标准 x64 发布审计通过：

- 发布目录 39 个文件、90,603,477 bytes；符号目录 3 个文件；
- 审计用时 231,688 ms，审计期间源码稳定；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 为 0；C3A 缺失场景、缺失产品、缺失 runner、禁止范围和目标源警告均为 0；
- Rust ABI 2、能力 511、十个导出和 staging/publish 哈希门禁通过；
- 新增 6 条 C3A 阶段契约，全部 AOT 相关测试 429/429；
- 规范 x64 全量测试 2426/2426；Rust workspace 57/57；
- `cargo fmt --check`、Clippy `-D warnings` 和相关 PowerShell 脚本解析通过；
- JSON 固定清单更新为 25 个文件、60/60 处 source-generated 调用和 23 个 context 所有者。

全量回归首次运行的 21 个失败均为旧阶段测试仍查找项目文本 `stage 5B-4C2A` 或旧 JSON 清单 `24/59/22`。同步当前状态契约后全量 2426/2426 通过；没有修改产品行为以迁就测试。

项目仍保留已冻结的普通 C# 编译器警告和 WMC1510 基线，本阶段没有把它们描述为新增问题或全项目零警告。

## 6. Rust 决策

本阶段不扩展 Rust。Todo fixture 的最大状态只有 6 项，核心工作是日期比较、少量集合遍历、WinUI 应用生命周期和 JSON store 持久化。把它迁到 Rust 不会产生可量化的内存收益，反而需要新增 Todo 模型、时间、回调、store 和错误映射 ABI。

现有 C# 产品服务已能以固定 clock 和 owned store 完整验证，继续保留 C# 比扩展跨语言边界更简单。生产 Rust 模块因此保持 ABI 2、能力 511 和十个导出。

## 7. 复盘与下一阶段调整

C3A 预定范围已经全部覆盖，没有遗漏需要回补到当前 fixture。下一阶段仍进入 **5B-4C3B**，但建议拆成两个可独立回滚的子批次：

1. **5B-4C3B1：Todo 原生通知注册、payload、展示与清理生命周期。** 使用唯一 tag/group 和 owned run ID，验证单项通知的 Complete/Snooze 按钮、四个 snooze 选项、聚合通知不带动作、注册/注销、展示成功、通知中心清理和托盘 fallback 不误触发。该批会真实显示临时 Windows 通知，开始前应先确认当前 Windows App SDK 的可控清理 API，并把残留通知清理列为硬门禁。复杂度中高。
2. **5B-4C3B2：真实 activation 与单实例转发。** 分别验证打开任务、Complete、Snooze 10/30/60 分钟与 tomorrow，覆盖应用运行中、冷启动和第二进程转发到既有实例；持久化 store、Todo surface 刷新和目标定位必须一致。自动化触发与真实用户点击证据分开，通知中心中的真人点击仍保留人工门。复杂度高。

这样拆分后，B1 的失败只涉及系统通知注册/展示/清理，B2 才承担 activation、单实例、UI 和状态修改，不会在一个 runner 中同时混入三类外部状态。
