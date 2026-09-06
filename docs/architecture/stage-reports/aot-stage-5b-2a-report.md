# DeskBox Native AOT 阶段 5B-2A 完成与复盘报告

- 报告日期：2026-08-22
- 阶段范围：x64 Native AOT 进程中的 Explorer 托管启动与 Quick Access 只读查询真实边界
- 审计配置：profile 31 / summary schema 28
- 结论：5B-2A 已完成，可以开放 5B-2B；本报告不代表 Quick Access 状态变更、音乐、完整托管功能矩阵、安装升级或正式发布已经通过

## 1. 本阶段结果

5B-2A 已证明同一个受审计 NativeAOT 主程序能够通过实际产品入口完成以下两条 Rust 边界：

1. `ExplorerShellLaunchService` 在 Explorer 桌面进程中执行 `ShellExecute`，一次性命令文件实际生成预期标记，而不只是返回成功；
2. `ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync` 在独立 STA 中查询一次性目录，查询前后均为 `NotPinned`；同时直接读取原生调用明细，结果仍为 `NotPinned`。

本批没有执行 pin/unpin，也没有调用 Quick Access 的任何变更操作。普通 JIT 默认行为没有改变；详细原生结果重载只在 Rust 分支填充，原有调用方继续使用原签名和原回退语义。

## 2. 实现边界

### 2.1 Explorer 产品入口与实际效果

`ExplorerShellLaunchService.TryOpen` 保留原公开签名，并增加一个内部重载返回 `ExplorerShellLaunchNativeCallResult`。普通 JIT 的 C# oracle 仍返回原有布尔值和错误；NativeAOT/Rust 路径同时给冒烟入口提供 status、HRESULT 和 attempted phases。

冒烟夹具在隔离根创建 `explorer-launch-probe.cmd`。命令由产品服务交给 Explorer 执行，成功后在同一夹具目录写入 `explorer-launch-marker.txt`。最终验证同时要求：

- 产品服务成功；
- Rust 原生结果成功；
- attempted phases 非零；
- 标记文件真实存在，内容精确为 `explorer-shell-launch`。

因此本阶段没有把“FFI 返回 0”或“发布目录存在导出”误记为 Explorer 启动已经通过。

### 2.2 Quick Access 只读链路

对一次性目录依次执行：

1. 产品公共异步查询；
2. `QueryPinState` 原生明细查询；
3. Explorer 启动完成后的产品公共异步复查。

三次结果都必须为 `NotPinned`，两次产品查询不得返回错误。AOT runner 源码契约和 profile 31 审计门禁共同拒绝 `TryPinFolderToQuickAccess`、`TryUnpinFolderFromQuickAccess`、`QuickAccessNativeOperation.Pin` 与 `Unpin`。

### 2.3 隔离与结构化证据

新入口只在 `DESKBOX_NATIVE_AOT` 中编译，并且必须显式设置 `DESKBOX_AOT_SHELL_SMOKE=ExplorerQuickAccessReadOnly`。执行前要求当前数据根是显式 AOT preview 根，夹具只允许位于：

`<preview-root>\aot-shell-smoke\explorer-quick-access-read-only`

结果通过独立 source-generated `AotShellSmokeJsonContext` 原子写入 `result.json`，记录产品状态、原生状态、HRESULT、阶段掩码、EXE/Rust 哈希、模块句柄、ABI、能力和稳定步骤。

`run-aot-shell-smoke.ps1` 会清空 shortcut smoke 环境变量，避免两个入口同时运行；结束后恢复两个环境变量，并在外层 `finally` 中只按受审计 EXE 的完整路径停止 AOT 进程。

## 3. 真实 AOT 运行证据

| 项目 | 结果 |
| --- | --- |
| 场景 | `ExplorerQuickAccessReadOnly` |
| AOT PID | 29756；脚本结束后已不存在 |
| Explorer 后端 | `Rust` |
| Explorer status / HRESULT | `0` / `0x00000000` |
| Explorer attempted phases | `0x7F` |
| Explorer 实际效果 | 标记存在，内容为 `explorer-shell-launch` |
| Quick Access 后端 | `Rust` |
| 产品查询前 / 原生查询 / 产品查询后 | `NotPinned` / `NotPinned` / `NotPinned` |
| Quick Access status / HRESULT | `0` / `0x00000000` |
| Quick Access attempted phases | `0x3F` |
| 匹配项 / fallback | `false` / `false` |
| Rust 模块 | `Loaded`，句柄 `0x7FFD80A00000` |
| ABI / 能力 | 2 / 255 |

实际运行使用同次 profile 31 审计产物：

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `DeskBox.exe` | 39,532,544 | `42BE72835A6B7DA68FC0A07348B0E11B3908CDD0A710040893A1500D3D479ED2` |
| `DeskBox.Updater.exe` | 2,020,352 | `EFBBA4C8669728D8429CADE79141AC258CF4A873B06F0A37F4875F240911E5C9` |
| `deskbox_native.dll` | 146,944 | `D5F0966B62EED9A982A9675F3CC7D2FDA8F70E35C0C9790DC83FE60865E45CE7` |

## 4. 自动化与构建验证

| 项目 | 结果 |
| --- | --- |
| 5B-2A 新契约红灯 | 8/8 按预期失败 |
| 5B-2A 实施后契约 | 8/8 通过 |
| 5B-2A + JSON 固定清单 | 20/20 通过 |
| PowerShell 语法 | 通过 |
| NativeAOT 条件编译 | 0 错误；新增源文件最初的一处 CS8602 已修正 |
| Rust 单元测试 | 52/52 通过 |
| x64 全量测试 | 2149/2149 通过 |
| `git diff --check` | 通过；仅报告仓库既有 LF/CRLF 提示 |

新增 evidence context 使固定 JSON 清单从 17 个文件、50 处调用、15 个 context 所有者变为 18 个文件、51 处调用、16 个 context 所有者。第 51 处只编入 NativeAOT、只写隔离 preview 证据；51/51 仍显式使用 source-generated `JsonTypeInfo`，没有重新启用反射。

## 5. 最终 x64 AOT 审计

| 项目 | 结果 |
| --- | --- |
| 审计耗时 | 217,655 ms |
| 源码在审计期间 | 稳定；前后指纹均为 `609E294ACE3D72FA8F6CCBCBE0A093647D2FAD7F29F7E19564DF1E0BCBBFF088` |
| 发布目录 | 39 个文件，85,171,141 bytes |
| 符号目录 | 3 个文件 |
| 警告代码 | CS0108、CS0169、CS0414、CS8601、CS8602、WMC1510 |
| WMC1506 / WMC1510 | 0 / 1216 |
| IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 均为 0 |
| 完整 `always-throw` | 0 |
| 5B-2A 缺失 runner / launch / service / query / script | 均为 0 |
| Quick Access 非只读模式 / 不安全 preview 模式 / 目标源告警 | 均为 0 |
| Rust | ABI 2、能力 255、9 个必需导出，staging/publish 哈希一致 |

## 6. 正式数据与用户 Shell 状态

| 项目 | 场景前 | 场景后 |
| --- | ---: | ---: |
| 正式数据文件数 | 122 | 122 |
| 正式数据总字节数 | 303,204,886 | 303,204,886 |
| 元数据指纹 | `23E54BE3F0D82CDD5B6289B29CF1BA423446C388581942711B147BC7B9665CC0` | 相同 |
| 临时目录 Quick Access 状态 | `NotPinned` | `NotPinned` |

该正式数据快照与旧阶段报告不同，只表示阶段之间用户数据发生过范围外变化。本阶段只依据同一次运行开始和结束的对比，不把旧快照当作当前基线。

## 7. 完成后复盘

本轮实现和实测后未发现第三套 Explorer 启动实现、AOT 回退 C# dynamic COM、Quick Access 隐式变更操作或正式数据旁路。审计中发现并修正一处仅存在于新 AOT runner 的可空解引用分析警告；最终目标源告警为 0。

当前证据明确分为三层：

- 契约测试证明入口、只读边界、隔离和审计规则存在；
- profile 31 证明发布产物、分析告警、ABI、导出和哈希满足门禁；
- 真实 AOT 场景证明 Explorer 产生实际效果，Quick Access 查询在真实 Shell 环境中返回稳定结果。

5B-2A 没有验证 pin/unpin，也没有验证音乐或完整托管 UI；这些是后续范围，不是本阶段遗漏。

## 8. 下一阶段建议

下一批保持为 **5B-2B：Quick Access 临时目录 pin/unpin 的 AOT → Rust 真实边界验证**，复杂度为中等，不与音乐合并。建议边界如下：

1. 使用新的唯一 preview 目录，先通过产品查询确认初始状态为 `NotPinned`；
2. 通过产品公共入口执行 pin，轮询到 `Pinned`，并记录原生明细；
3. 通过产品公共入口执行 unpin，轮询恢复 `NotPinned`；
4. App 内 `finally` 必须执行补偿性 unpin；脚本在失败或超时后还要启动独立补偿检查，确认目录不再固定后才能清理夹具；
5. 正常、失败、超时和进程清理路径都必须记录最终状态；若补偿失败，应阻断阶段完成并给出明确人工清理路径；
6. 继续核对同次审计 EXE/Rust 哈希、非零模块句柄、ABI/能力、正式数据指纹和精确进程路径。

5B-2B 的主要复杂度不是 ABI 或代码量，而是会短暂改变用户的 Windows Shell 状态，必须把最终恢复视为硬门禁。完成后再进入音乐 getter/setter 的 AOT 实测；音乐 setter 同样需要先保存原值并在所有退出路径恢复。托管 UI 功能矩阵、安装升级/回滚和 CRT 决策继续后置，Rust `SearchCore` 仍是独立性能方向。
