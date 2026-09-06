# DeskBox AOT 阶段 5B-4C3B1 完成报告

- 日期：2026-08-23
- 状态：5B-4C3B1 已完成到 x64 Native AOT 真实通知展示、跨进程历史恢复、精确清理和注销边界
- 审计配置：profile 54 / schema 51
- 本阶段范围：Todo 原生通知注册、产品 payload、单项/聚合展示、历史枚举、逐条精确删除、注销与新进程 postflight
- 明确保留：通知 activation 动作路由、运行中/冷启动/第二进程转发、Todo surface 定位和真人点击，归入 5B-4C3B2

## 1. 结论

5B-4C3B1 已闭环 Todo 原生通知从产品构造到系统通知中心清理的生命周期。三个使用同一受审计 AOT 可执行文件和同一 owned preview root 的全新进程，依次完成真实展示与 payload 检查、跨进程历史恢复与逐条删除、第三进程无残留复核。

本阶段直接复用产品通知构造逻辑，没有在 fixture 中复制一份近似 XML。单项通知包含 Complete、Snooze 和四个稍后提醒选项；聚合通知不包含动作。两条通知分别使用 run ID 派生的唯一 tag，并共享唯一 group；清理只调用精确的 tag/group 删除接口，没有使用 `RemoveAllAsync` 或整组删除。

最终结果证明：

1. `AppNotificationManager` 能在受审计 x64 AOT 产物中注册、展示、枚举、删除和注销；
2. 单项与聚合通知均由真实产品 composition 生成并进入系统通知历史；
3. 单项 payload 含两个动作、一个 selection input 和 `10m`、`30m`、`1h`、`tomorrow` 四个唯一选项；
4. 聚合 payload 不含 action 或 selection input；
5. 第二个新进程能重新读取前一进程留下的两条通知，并分别按 tag/group 删除；
6. 第三个新进程重新注册后历史计数仍为 0；
7. 三次进程均正常退出，正式数据指纹不变，preview root 已清理；
8. 全程没有 native notification activation，也没有托盘 fallback。

## 2. 实现边界

产品侧增加了可复用的 Todo 通知构造入口，普通产品调用仍保留既有 Dispatcher、日志和托盘 fallback。AOT fixture 只调用该产品入口，并传入测试专用 tag/group；它没有改写 Todo reminder 的业务规则。

`NativeAppNotificationService` 增加了以下窄边界：

- 注册状态查询；
- `GetAllAsync()` 历史快照；
- `RemoveByTagAndGroupAsync(tag, group)` 精确删除；
- 显式 `Unregister()` 与 `Dispose()` 收口。

NativeAOT-only 场景只接受 `RealDisplayAndCleanup`、三个固定 phase、32 位 `Guid N` run ID 和显式隔离的开发数据根。每个 phase 都记录 EXE 路径与 SHA-256、PID、注册/注销状态、通知前后计数、tag/group、真实 XML payload、验证步骤和正常关闭状态。结构化结果使用独立 source-generated JSON context。

## 3. 三进程真实证据

成功 run ID 为 `bac6a8ac20a84571a8fc7f97aa2e0206`：

| 阶段 | PID | 通知计数 | 结果 |
| --- | ---: | ---: | --- |
| ShowAndInspect | 30212 | 0 → 2 | 展示单项与聚合通知，枚举真实历史并核对 payload |
| Cleanup | 18136 | 2 → 0 | 新进程重新读取两条通知，分别按 tag/group 精确删除 |
| Postflight | 24204 | 0 → 0 | 第三个新进程确认无残留 |

三个 PID 全部不同，三次均自然退出，使用的 EXE SHA-256 均为：

`67AB7E281B9FA84EB6C5FAF401F0A483B3B66132AD107B7BB60BC2DC9DBD1056`

正式数据运行前后均为 122 个文件、306,477,348 bytes，指纹保持：

`1EBAA2AAFE8339FC0D51BFE8255BEB2B7D91ACEDFCED2BDDC4CEEF8F234D2975`

成功 evidence 归档在：

`.artifacts/aot-todo-notification-smoke/win-x64/notification-runs/bac6a8ac20a84571a8fc7f97aa2e0206/`

最近一次成功索引为 `.artifacts/aot-todo-notification-smoke/win-x64/notification-session.json`。索引固定记录 `realSystemNotificationsShown=2`、`exactTagGroupCleanup=true`、`activationObserved=false` 和 `previewRootCleaned=true`。

## 4. 实际 payload 差异与失败补偿

首轮 run `377815a923994ac5858a0735f08202b8` 已真实展示并枚举两条通知，但 fixture 最初假设参数使用 `&` 分隔，并假设四个 selection 按固定顺序输出。当前 Windows App SDK 实际生成的 XML 使用 `;` 分隔参数，selection 顺序为 `10m`、`1h`、`30m`、`tomorrow`。

该轮在失败路径中立即按两个精确 tag/group 执行应用内补偿，结果记录 `compensationSucceeded=true`；随后独立 cleanup 进程再次确认历史计数为 0。因此首次失败没有留下系统通知或 owned 数据残留。

修正只作用于验证边界：fixture 参数解析同时接受 `&` 和 `;`，四个 selection 按唯一集合核对，不依赖 SDK 的序列化顺序。产品 activation 解析器目前仍只按 `&` 拆分，这不会影响本阶段的展示和清理，但会影响下一阶段动作路由，因此已列为 5B-4C3B2A 的第一项产品修正和真实回归门禁。

## 5. AOT 审计与回归

profile 54 / schema 51 的标准 x64 发布审计通过：

- 审计用时 282,902 ms，审计期间源码指纹稳定；
- 发布目录 39 个文件、92,515,797 bytes；符号目录 3 个文件、210,604,032 bytes；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 和原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- C3B1 缺失场景、缺失产品、缺失 runner、禁止范围、目标源警告均为 0；
- Rust ABI 2、能力 511、十个导出和 staging/publish 哈希门禁通过；
- 6 条 C3B1 阶段契约通过，全部 AOT 相关测试 435/435；
- 规范 x64 全量测试 2432/2432，Rust workspace 57/57；
- `cargo fmt --check`、Clippy `-D warnings`、相关 PowerShell 脚本解析和 `git diff --check` 通过；
- JSON 固定清单更新为 26 个文件、61/61 处 source-generated 调用和 24 个 context 所有者。

首轮全量回归的 17 个失败均为旧阶段契约仍断言 profile 53 / schema 50 或 `stage 5B-4C3A`。只同步当前状态断言后，全量 2432/2432 通过；没有修改产品行为来规避测试。

## 6. Rust 决策

本阶段不扩展 Rust。这里的主要边界是 Windows App SDK 的通知注册、WinRT 异步历史 API、XML composition 和应用生命周期；数据量固定为两条通知，迁移到 Rust 不会带来可量化的内存收益，反而需要新增 WinRT 通知 ABI、异步结果和 XML 错误映射。

继续使用 C#/WinRT 比跨语言重建该边界更简单，也符合“明显完整 Rust 更简单时才迁移”的既定原则。生产 Rust 模块保持 ABI 2、能力 511 和十个导出。

## 7. 复盘与下一阶段调整

C3B1 预定范围已经全部覆盖，没有展示、历史恢复、精确删除、注销或 postflight 遗漏。下一阶段调整为两个独立子批次：

1. **5B-4C3B2A：activation grammar 与确定性动作路由。** 先让产品参数解析兼容系统实际使用的 `;`，并保留对 `&` 的兼容；再在隔离 store、固定 clock 和受控 activation 输入下覆盖通知正文打开、Complete、Snooze 10/30/60 分钟与 tomorrow，验证参数拒绝、幂等、持久化和运行中实例刷新。该批不要求真人点击，也不启动第二个真实应用实例。复杂度中高。
2. **5B-4C3B2B：真实 Windows activation 与单实例转发。** 使用真实通知点击/按钮覆盖应用运行中、冷启动和第二进程转发，验证 Todo surface 打开、目标定位、状态重载与进程唯一性。真人点击与自动触发证据仍分开记录。复杂度高。

这样拆分后，先把本轮已经观察到的真实 payload grammar 和业务状态路由确定下来，再处理 Windows activation 与单实例这两个外部生命周期边界。C1C2B、C2B 的物理输入人工门继续保留为发布前条件，不阻塞 C3B2A。
