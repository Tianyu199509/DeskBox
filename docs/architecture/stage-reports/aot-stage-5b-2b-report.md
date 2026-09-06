# DeskBox Native AOT 阶段 5B-2B 完成与复盘报告

- 审计日期：2026-08-22
- 范围：Quick Access 临时目录 pin/unpin、应用内失败补偿、进程终止后的独立补偿
- 平台：x64 / `win-x64`
- 结论：5B-2B 已完成，可以开放 5B-3A 音乐音量只读 getter；本报告不代表音乐 setter、完整托管 UI 功能矩阵、安装升级、CRT 决策、ARM64 或正式发布已经通过

## 1. 本阶段结论

5B-2B 已用最终受审计 Native AOT 产物实际证明以下边界：

1. 正常链路通过产品公共入口完成 `NotPinned → Pinned → NotPinned`，公共查询与直接原生只读查询一致；
2. 固定成功后主动抛出应用内异常，App 外层 `finally` 实际执行补偿性 unpin，最终公共与原生状态均为 `NotPinned`；
3. 固定成功后让 AOT 进程停在 `AwaitingExternalCompensation`，脚本按完整 EXE 路径强制结束该进程；
4. 新启动的独立 AOT 恢复进程先实际读到前一进程留下的 `Pinned`，随后通过产品 unpin 恢复，公共与原生最终状态均为 `NotPinned`；
5. 前置清理、恢复、正常主流程和最终 postflight 均使用同一稳定目标路径；测试完成后没有受审计 AOT 进程遗留，正式数据目录指纹未变化。

因此，本阶段不再只依赖正常路径或静态脚本判断。应用内异常和外部强制终止两种恢复链路都已在真实 AOT 进程中执行。

## 2. 实现边界

新增 `App.AotQuickAccessMutationSmoke.cs`，只在 `DESKBOX_NATIVE_AOT` 中编译，并通过显式环境变量 `DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE` 启用。入口仍位于 `OnLaunched completed successfully` 之后，不改变普通 JIT 启动行为。

夹具位于隔离 preview 根：

```text
<preview-root>\aot-quick-access-mutation-smoke\
  mutation-target\
  pin-unpin\result.json
  pin-then-fail\result.json
  pin-then-await-external-compensation\result.json
  compensate-unpin\result.json
```

`mutation-target` 是跨进程稳定路径。runner 只清理各场景证据目录，不删除该目标目录，避免进程被终止后 Quick Access 中留下一个后续进程无法定位的失效路径。

pin/unpin 只调用产品公共入口：

- `ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync`；
- `ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync`；
- `ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync`。

`QuickAccessNativeBackend.Invoke(QueryPinState, ...)` 只用于读取原生状态和诊断明细，没有绕过产品层直接执行原生 pin/unpin。

## 3. 补偿与进程安全

应用内 runner 的外层 `finally` 无条件调用补偿性 unpin，并轮询公共状态到 `NotPinned`，随后用原生查询复查。补偿失败会把结构化结果标记为 `Failed`，不会把场景判为完成。

`run-aot-quick-access-mutation-smoke.ps1` 按以下六段执行：

1. `preflight`：独立补偿进程确认初始 `NotPinned`；
2. `in-process-failure`：pin 后主动异常，验证 App `finally`；
3. `forced-termination`：pin 后进入等待点，由脚本精确结束 AOT EXE；
4. `recovery`：新进程必须先观察到 `Pinned`，再补偿到 `NotPinned`；
5. `main`：完成正常 `NotPinned → Pinned → NotPinned`；
6. `postflight`：无条件执行最终补偿和双重状态复查。

任一主阶段失败或超时后，脚本仍会进入 postflight。若 postflight 失败，脚本给出稳定目标的完整路径并阻断完成；不会只停止进程后继续报成功。每次调用都在 `finally` 中按受审计 EXE 完整路径清理进程，并隔离 shortcut、shell 和 mutation 三个 smoke 环境变量。

## 4. 契约与回归

| 检查 | 结果 |
| --- | ---: |
| 初始 5B-2B 红灯契约 | 9/9 按预期失败 |
| 首轮实现后 5B-2B 契约 | 9/9 通过 |
| 复盘新增故障路径红灯契约 | 2/2 按预期失败 |
| 最终 5B-2B 契约 | 11/11 通过 |
| JSON 固定清单 | 19 个文件、52/52 处调用、17 个 context 所有者、反射型重载 0 |
| Rust 单元测试 | 52/52 通过 |
| x64 全量测试 | 2160/2160 通过 |
| PowerShell 语法解析 | mutation runner 与 AOT audit 均为 0 错误 |
| `git diff --check` | 通过 |

全量回归曾出现 2156/2158：两个旧阶段契约要求项目错误说明继续保留 5B-1 和 5B-2A 的原文。最终项目说明同时保留历史门禁并追加 5B-2B，定向 25/25 后全量恢复通过。这是契约说明兼容问题，不是 Quick Access 行为失败。

## 5. 最终 AOT 审计

最终 `publish-aot-audit.ps1 -Platform x64` 结果：

| 项目 | 结果 |
| --- | --- |
| profile / schema | 32 / 29 |
| 审计用时 | 200,952 ms |
| 发布文件 / 分离 PDB | 39 / 3 |
| 警告代码 | CS0108、CS0169、CS0414、CS8601、CS8602、WMC1510 |
| WMC1506 / WMC1510 | 0 / 1216 |
| 完整 `always-throw` | 0 |
| 5B-2B 缺失 runner / script | 0 / 0 |
| 5B-2B 不安全模式 / 目标源码告警 | 0 / 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| source stable during audit | `true` |

最终实际运行产物：

- EXE SHA-256：`4DB12E7AEDDC6B2A48CD5665E25F72A7A800B84EDFAB509F9C96EDCF9436C320`；
- Rust DLL SHA-256：`15BFCF7D683DB37CB908E7E5571B9E11AB358F00F854707095A2812805BA7D11`；
- 模块句柄非零，ABI 为 2，能力掩码为 255；
- 正式数据指纹前后均为 `2103B1C1658CEBA52A5A1634C68635707170CDA8562C9C3E9F977C2536CC1391`。

## 6. 六段真实 AOT 结果

| 阶段 | PID | 初始状态 | 中间状态 | 最终状态 | 结果 |
| --- | ---: | --- | --- | --- | --- |
| preflight | 39500 | NotPinned | — | 公共/原生 NotPinned | 通过 |
| in-process-failure | 25700 | NotPinned | 公共/原生 Pinned，随后主动异常 | App `finally` 恢复公共/原生 NotPinned | 按预期失败并完成补偿 |
| forced-termination | 30884 | NotPinned | 公共/原生 Pinned | 进程停在等待点后被精确终止 | 按预期保留 Pinned 供恢复验证 |
| recovery | 36984 | Pinned | 产品 unpin | 公共/原生 NotPinned | 通过 |
| main | 30660 | NotPinned | 公共/原生 Pinned | 公共/原生 NotPinned，finally 再次确认 | 通过 |
| postflight | 9916 | NotPinned | — | 公共/原生 NotPinned | 通过 |

`forced-termination` 证据中的 `CleanupSucceeded=false` 是预期状态：该进程在进入自身 `finally` 前被强制结束。完成判定来自随后独立 `recovery` 进程先读取到 `Pinned`，再成功恢复为 `NotPinned`，不能把等待点本身误报为已清理。

## 7. 复盘与遗漏检查

复盘后已补齐以下原方案风险：

1. 只执行正常 pin/unpin 不能证明异常恢复，因此增加 pin 后主动异常；
2. 只验证 App `finally` 不能覆盖崩溃或强制结束，因此增加外部终止和新进程恢复；
3. 补偿目标不能位于会被场景初始化删除的目录，因此目标改为稳定兄弟目录；
4. 旧结果可能被误读，因此脚本清理旧结果并要求 PID 等于当前 preview session 的 primary PID；
5. 只看公共状态不足以证明 Rust 原生边界，因此 Pinned 和 NotPinned 均有直接原生只读复查；
6. 历史阶段的项目文字契约不能被新阶段覆盖，最终说明同时保留 5B-1、5B-2A 和 5B-2B。

当前没有发现阻断 5B-2B 完成的遗漏。目标目录仍保留在隔离 preview 根中，但已由 recovery、main 和 postflight 三次确认未固定；它不是正式用户数据，也没有 AOT 进程继续使用。

## 8. 下一阶段调整

下一批建议改为 **5B-3A：音乐音量只读 getter 的 AOT → Rust 真实边界验证**，复杂度为低到中，不直接把 getter 和 setter 合并。

建议边界：

1. 只通过产品 `MusicVolumeService.GetSystemMasterVolumeAsync` 与 `GetVolumeAsync` 读取，不调用 setter；
2. 直接原生调用只用于记录详细 status、HRESULT、attempted phases、session match kind 和数值；
3. 系统音量必须是有限值且位于 `[0,1]`；有匹配 session 时 session 音量也必须满足相同范围；
4. 继续核对同次 EXE/Rust 哈希、模块句柄、ABI/能力、正式数据指纹和精确进程清理；
5. 没有默认音频 endpoint 时应明确失败或记录环境阻断，不能把产品回退值 `0` 当成真实 Rust 成功。

原规划中的 setter 建议拆分：

- **5B-3B：系统主音量 setter**，复杂度中高。变更前必须把原值写到独立持久化证据，App `finally` 与独立新进程都要能恢复，并按容差确认最终值；
- **5B-3C：session 音量 setter**，复杂度高且依赖环境。应使用可控媒体会话并保存该会话原值，不能任意修改用户正在播放的第三方会话。

托管 UI 功能矩阵、安装/覆盖升级/回滚、CRT 决策、ARM64 和 Rust `SearchCore` 继续后置。本轮不开始 5B-3A。
