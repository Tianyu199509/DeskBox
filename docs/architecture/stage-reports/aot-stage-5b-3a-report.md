# DeskBox Native AOT 阶段 5B-3A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：音乐音量系统主音量 getter、session snapshot 无写入验证
- 平台：x64 / `win-x64`
- 结论：5B-3A 已完成，可以开放 5B-3B 系统主音量 setter 与故障恢复；本报告不代表匹配 session、任何音量 setter、完整托管 UI 功能矩阵、安装升级、CRT 决策、ARM64 或正式发布已经通过

## 1. 本阶段结论

5B-3A 已使用最终受审计的 Native AOT 产物实际证明：

1. `MusicVolumeService.GetSystemMasterVolumeAsync()` 在 AOT 中经过产品服务进入 Rust Core Audio 边界；
2. `MusicVolumeService.GetVolumeAsync()` 在固定探测身份下完成系统音量和无匹配 session snapshot；
3. 直接原生 getter 返回了真实 status、HRESULT、attempted phases、match kind 和数值，产品回退值 `0` 不能单独构成成功；
4. 场景前后系统主音量一致，没有调用系统或 session setter；
5. EXE、Rust DLL、ABI、能力、模块句柄、正式数据指纹和进程清理均通过同次证据核对。

本机运行时没有匹配到播放器 session。因此本阶段确认的是 `HasSessionVolume=false` 的 snapshot 契约；匹配 session getter 与 session setter 都没有被宣称为已通过，继续留到需要可控媒体 session 的 5B-3C。

## 2. 实现边界

新增 `App.AotMusicVolumeReadSmoke.cs`，只在 `DESKBOX_NATIVE_AOT` 中编译，并且只有显式设置 `DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE=SystemAndSnapshotReadOnly` 才会运行。入口位于 `OnLaunched completed successfully` 之后，普通 JIT 启动行为没有变化。

runner 依次执行：

1. `MusicVolumeNativeBackend.GetSystemVolume()`，建立真实默认 endpoint 与原生系统读取前值；
2. 产品 `GetSystemMasterVolumeAsync()`；
3. 产品 `GetVolumeAsync()`；
4. `MusicVolumeNativeBackend.GetSnapshot()`，记录原生 session 枚举和匹配明细；
5. 再次执行原生系统 getter，确认前后值没有变化。

runner 源码、契约测试和 AOT 审计都禁止 `TrySetSystemMasterVolumeAsync`、`TrySetSessionVolumeAsync`、`MusicVolumeNativeBackend.SetSystemVolume` 与 `SetSessionVolume`。本批没有修改 Rust crate、音乐 ABI、产品服务语义或普通 JIT 的 C# 默认后端。

结构化结果只写入显式 preview 根：

```text
%LOCALAPPDATA%\DeskBox-AotPreview\wingezi-B073278E-stage5b3a-system-and-snapshot-read-only\
  aot-music-volume-read-smoke\system-and-snapshot-read-only\result.json
```

外层脚本另把核验证据写到：

```text
.artifacts\aot-music-volume-read-smoke\win-x64\session.json
```

## 3. 真实 AOT 运行结果

最终成功场景的关键值如下：

| 项目 | 实际结果 |
| --- | --- |
| 场景 | `SystemAndSnapshotReadOnly` |
| AOT PID | `34948`，脚本结束后已退出 |
| 动态代码 | `false` |
| 后端 | `Rust` |
| 产品系统 getter | `0.370000004768372` |
| 产品 snapshot 系统音量 | `0.370000004768372` |
| 原生系统读取前值 | `0.370000004768372` |
| 原生 snapshot 系统音量 | `0.370000004768372` |
| 原生系统读取后值 | `0.370000004768372` |
| 产品 / 原生 session | `HasSessionVolume=false` / `matchKind=0` |
| 原生 snapshot status / operation HRESULT | `0` / `0x00000000` |
| 原生 snapshot attempted phases | `0x1F` |
| create / device / system HRESULT | `0x00000000` / `0x00000000` / `0x00000000` |
| session HRESULT | `0x00000001 (S_FALSE)` |
| COM HRESULT | `0x80010106 (RPC_E_CHANGED_MODE)`，按既有 STA apartment 复用契约接受 |
| 成功步骤 | 25 个必需步骤全部记录 |

前后五个系统音量读数完全一致，低于 `0.005` 容差门槛。`0x1F` 表示 COM 初始化、枚举器创建、默认设备、系统音量和 session 枚举均已尝试；没有匹配 session，因此没有设置 session-volume phase，也没有把 session 的 `0` 值误判为有效匹配。

第一次运行已经完成应用侧 AOT 读取，但外层 Windows PowerShell 5.1 不支持 `Double.IsFinite`，证据校验因此失败。脚本随后改为兼容的 `Double.IsNaN` / `Double.IsInfinity` 判断并重新运行成功。这是脚本兼容问题，不是 Rust、Core Audio 或产品 getter 失败；最终审计使用的是修正后的脚本。

## 4. AOT 产物与审计

最终 profile 33 / schema 30 审计结果：

| 项目 | 结果 |
| --- | --- |
| 发布文件 | 39 |
| 发布体积 | 85,428,677 bytes |
| 分离 PDB | 3 |
| WMC1506 | 0 |
| WMC1510 | 1216 |
| 完整 `always-throw` | 0 |
| 5B-3A 缺失 runner / launch / product / script 模式 | 0 / 0 / 0 / 0 |
| 5B-3A setter / 非 preview 不安全模式 | 0 / 0 |
| 5B-3A 目标源警告 | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 源码审计期间稳定 | `true` |

受审计文件哈希：

- `DeskBox.exe`：`EE2D589A4E7A29DB11D8D7EEFAFE37EA7370DEE20028A9E9AF502C728F681B9B`
- `deskbox_native.dll`：`16777CE343576C449832D61A4F7E6F5A350BCA9AFA43E83EF71F5784782FFD98`

这两个值与第一次审计构建不同；当前没有宣称跨次构建字节级可复现。门禁要求的是每一次审计内部 Rust staging/publish 哈希一致，以及运行时 EXE/Rust 哈希与所选同次 `summary.json` 一致；最终真实运行已绑定到上列最终哈希。

正式 `%LOCALAPPDATA%\DeskBox` 的确定性元数据指纹在场景前后均为：

```text
EAA4B8C68677573BDFF00A0DF428FB9A363C0F9CB7D1AE80990CA79C0FA7951F
```

脚本结束后没有受审计 AOT `DeskBox.exe` 遗留。

## 5. 自动化验证

| 验证 | 结果 |
| --- | --- |
| 5B-3A 新契约红灯 | 主实现前 0/9；复盘补充跨 runner 隔离前 0/1，均按预期失败 |
| 5B-3A 新契约 | 10/10 通过 |
| AOT / JSON 组合定向 | 194/194 通过 |
| JSON 固定清单 | 20 个文件、53/53 处 source-generated 调用、18 个 context 所有者 |
| Rust fmt / Clippy | 通过，Clippy 使用 `-D warnings` |
| Rust 单元测试 | 52/52 通过 |
| x64 全量测试 | 2170/2170 通过 |
| PowerShell 语法解析 | runner、audit、launcher 全部通过 |
| 真实 AOT 音量只读场景 | 通过 |

## 6. 复盘与遗漏检查

本轮复盘确认：

1. 产品服务在原生失败时会返回 `0`，所以 runner 额外要求直接原生 Success、endpoint/system HRESULT 和阶段掩码；没有把产品 `0` 当成功证据；
2. Rust snapshot 即使内部系统或 session 子读取失败也可能保持整体成功语义，所以门禁分别要求 `DeviceHResult`、`SystemHResult` 和 `SessionHResult` 非失败；
3. `RPC_E_CHANGED_MODE` 是在已有 STA 中复用 COM 的既有契约，不能错误当作 endpoint 失败；
4. session 是否存在会随环境变化，结果明确记录 `SessionMatchObserved`，无匹配时不宣称匹配分支通过；
5. 四个现有 AOT smoke 脚本都保存、清空并恢复全部四个 smoke opt-in；音乐脚本还隔离音乐后端变量，避免调用进程残留环境导致多个 runner 串入；
6. 结果 PID 必须等于当前 preview 的 primary PID，EXE/Rust 路径与哈希必须等于同次审计；
7. runner 与脚本都检查系统音量前后容差，源码审计同时禁止四个 setter；
8. 正式数据指纹保持一致，脚本 `finally` 按完整 EXE 路径清理进程。

当前没有发现阻断 5B-3A 完成的遗漏。唯一明确未覆盖的是“存在匹配媒体 session”分支；该分支需要可控 session，不能通过任意修改用户第三方播放器来制造。

## 7. 下一阶段建议

下一批建议为 **5B-3B：系统主音量 setter 与故障恢复**，复杂度中高，不与 session setter 合并。

建议固定以下门槛：

1. 任何写入前把原系统音量写入隔离、可供新进程读取的恢复证据；
2. 只通过产品 `TrySetSystemMasterVolumeAsync` 写入，直接 Rust getter 仅负责读取复查；
3. 探测值只做小幅、非静音变化，并避开 `0` / `1` 极值；
4. 正常流程验证“原值 → 探测值 → 原值”，App `finally` 无条件恢复；
5. 增加写入后主动异常和写入后强制终止；后一种必须由独立新 AOT 进程读取恢复证据并恢复；
6. 最终 postflight 必须确认产品与原生读数都在容差内回到原值；未恢复时脚本失败并给出明确人工恢复值；
7. 继续核对同次哈希、正式数据指纹和精确进程清理。

5B-3C 再使用可控媒体 session 补齐匹配 session getter 与 session setter。托管 UI 功能矩阵、安装/覆盖升级/回滚、CRT 决策、ARM64 和 Rust `SearchCore` 继续后置。本轮不开始 5B-3B。
