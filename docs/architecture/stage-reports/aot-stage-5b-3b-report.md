# DeskBox Native AOT 阶段 5B-3B 完成与复盘报告

- 审计日期：2026-08-22
- 范围：系统主音量 setter、应用内恢复、强制终止后的独立新进程恢复
- 平台：x64 / `win-x64`
- 结论：5B-3B 已完成，可以开放 5B-3C 可控媒体 session getter/setter；本报告不代表匹配 session、session setter、完整托管 UI 功能矩阵、安装升级、CRT 决策、ARM64 或正式发布已经通过

## 1. 本阶段结论

5B-3B 已使用最终受审计的 Native AOT 产物实际证明：

1. 系统主音量写入经过产品 `MusicVolumeService.TrySetSystemMasterVolumeAsync` 进入 Rust Core Audio 边界；
2. 每次变更前，原系统音量与探测值都会先原子写入稳定恢复意图并立即 source-generated 回读；
3. 正常流程完成“原值 → 探测值 → 原值”；
4. 写入后主动异常时，App 外层 `finally` 恢复原值；
5. 写入后强制终止时，独立新 AOT 进程读取同一恢复意图，先观察到遗留探测值，再通过产品 setter 恢复原值；
6. 只有直接 Rust getter 确认恢复值在 `0.005` 容差内，恢复意图才会删除；
7. 六阶段结束后恢复意图不存在、系统音量等于原值、正式数据指纹不变、受审计进程为 0。

本阶段没有调用 session setter，也没有为了制造 session 而控制用户的第三方播放器。匹配 session getter 与 session setter 保留到 5B-3C。

## 2. 实现边界

新增 `App.AotMusicVolumeMutationSmoke.cs`，只在 `DESKBOX_NATIVE_AOT` 中编译，并且只有显式设置 `DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE` 才会运行。支持四种应用侧场景：

| 场景 | 行为 |
| --- | --- |
| `ChangeRestore` | 持久化原值，写入小幅探测值，直接原生复查，由 App `finally` 恢复 |
| `ChangeThenFail` | 复查探测值后主动抛出固定异常，由 App `finally` 恢复 |
| `ChangeThenAwaitExternalRecovery` | 复查探测值并写出 `AwaitingExternalRecovery`，等待脚本强制结束进程 |
| `RecoverOriginal` | 新进程读取稳定恢复意图，通过产品 setter 恢复并在直接原生复查后删除意图；无意图时只读确认当前值 |

系统变更只使用：

```csharp
await new MusicVolumeService().TrySetSystemMasterVolumeAsync(volume)
```

runner 中没有 `MusicVolumeNativeBackend.SetSystemVolume`、`TrySetSessionVolumeAsync` 或 `MusicVolumeNativeBackend.SetSessionVolume`。直接原生边界只调用 `GetSystemVolume()`，用于确认默认 endpoint、status、HRESULT、attempted phases 和最终数值。

探测值规则为：原值不高于 `0.85` 时增加 `0.05`，否则减少 `0.05`；结果必须位于开区间 `(0,1)`，并且与原值至少相差 `0.04`。本机实际从约 37% 临时变为约 42%，没有触碰静音标志。

## 3. 恢复安全设计

恢复意图使用稳定路径：

```text
%LOCALAPPDATA%\DeskBox-AotPreview\wingezi-B073278E-stage5b3b-music-volume-mutation\
  aot-music-volume-mutation-smoke\recovery-intent.json
```

意图包含 schema、创建时间、preview 根、原值、探测值、来源 PID 和来源 EXE。关键顺序由测试和 AOT 审计同时冻结：

1. 直接原生 getter 读取健康原值；
2. 选择安全探测值；
3. 以 `.tmp` + 覆盖移动方式原子写入恢复意图；
4. 使用 source-generated context 立即回读并核对根、schema 和数值；
5. 才允许调用产品系统 setter；
6. 恢复时再次调用产品 setter；
7. 直接原生 getter 验证原值；
8. 最后删除恢复意图。

如果意图损坏、根不匹配、恢复 setter 失败或 getter 未回到容差内，runner 会保留意图。外层脚本无论主流程是否失败都会尝试独立 postflight；如果 postflight 仍失败，错误会给出恢复意图路径和可读取的原始系统音量，不会静默宣称完成。

五个 AOT smoke 脚本现在都会保存、清空并恢复全部五个 opt-in，避免调用进程残留环境让多个 runner 同时进入。音乐变更脚本还隔离 `DESKBOX_MUSIC_VOLUME_BACKEND`。

## 4. 真实 AOT 六阶段结果

最终绑定到受审计产物的六阶段结果如下：

| 阶段 | PID | 状态 | 关键结果 |
| --- | ---: | --- | --- |
| preflight | `35108` | `Completed` | 无恢复意图；读取原值 `0.370000004768372` |
| in-process-failure | `6532` | 预期 `Failed` | 写入 `0.420000004768372`，固定异常后 App 恢复到原值，意图删除 |
| forced-termination | `28468` | `AwaitingExternalRecovery` | 写入并确认探测值，意图保留，进程被精确强制结束 |
| recovery | `32144` | `Completed` | 新进程找到意图，先观察 `0.420000016689301`，恢复到原值，意图删除 |
| main | `24280` | `Completed` | 正常完成原值 → 探测值 → 原值 |
| postflight | `41632` | `Completed` | 无恢复意图；最终仍为 `0.370000004768372` |

关键状态汇总：

| 项目 | 实际结果 |
| --- | --- |
| 动态代码 | `false` |
| 后端 | `Rust` |
| 原始系统音量 | `0.370000004768372` |
| 计划探测值 | `0.420000004768372` |
| 强制终止后新进程读数 | `0.420000016689301` |
| 恢复后 / 正常后 / postflight | 均为 `0.370000004768372` |
| Rust system getter phases | 每次健康读取均覆盖 `0x0F` |
| 恢复意图最终存在 | `false` |
| 受审计 AOT 进程最终数量 | `0` |
| 清理结果 | `true` |

结构化外层证据写入：

```text
.artifacts\aot-music-volume-mutation-smoke\win-x64\session.json
```

并保留六个按阶段复制的 result JSON。

## 5. AOT 产物与审计

最终 profile 34 / schema 31 审计结果：

| 项目 | 结果 |
| --- | --- |
| 审计耗时 | 200,166 ms |
| 发布文件 | 39 |
| 发布体积 | 85,566,917 bytes |
| 分离 PDB | 3 / 183,398,400 bytes |
| WMC1506 | 0 |
| WMC1510 | 1216 |
| 完整 `always-throw` | 0 |
| 5B-3B 缺失 runner / launch / product / script 模式 | 0 / 0 / 0 / 0 |
| 5B-3B 直接 native setter/session / 非 preview 不安全模式 | 0 / 0 |
| 恢复顺序 | `true` |
| 5B-3B 目标源警告 | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 源码审计期间稳定 | `true` |

最终受审计并实际运行的文件哈希：

- `DeskBox.exe`：`46851FAA9A2C9C89B040973940653CA5D76FACE70E5ED72CD8C3A22876AD25A8`
- `deskbox_native.dll`：`933DB2AECE4E8547519E4D4502C7EE9487BBC788D739571B1315841670A7BD21`

哈希只证明本次审计内部 staging/publish 一致，以及运行证据与同次 summary 一致；没有宣称跨次 Native AOT 构建字节级可复现。

正式 `%LOCALAPPDATA%\DeskBox` 的确定性元数据指纹在完整六阶段前后均为：

```text
DF25E91EF8BB726C3BDFA46B9C0CF08EEBA93F186A4B26E4045AE58321F3F976
```

## 6. 自动化验证

| 验证 | 结果 |
| --- | --- |
| 5B-3B 新契约红灯 | 实现前 0/10，按预期失败 |
| 5B-3B 新契约 | 10/10 通过 |
| AOT / JSON / 音量组合定向 | 34/34 通过 |
| JSON 固定清单 | 21 个文件、55/55 处 source-generated 调用、19 个 context 所有者 |
| PowerShell 语法 | 五个 smoke、audit 和 launcher 均通过解析 |
| Rust fmt | 通过 |
| Rust Clippy | 通过，使用 `-D warnings` |
| Rust 单元测试 | 52/52 通过 |
| x64 全量测试 | 2180/2180 通过 |
| 真实 AOT 六阶段系统音量矩阵 | 通过 |

## 7. 实施复盘与遗漏检查

实施和最终复盘发现并处理了两项阶段内问题：

1. 第一次 NativeAOT 预审计发现 `MusicVolumeNativeCallResult latest = default` 产生新 runner 自身的 `CS8600`。改为在必执行的 `do` 循环中赋值后，最终审计的新阶段目标警告为 0。
2. 第一次实际脚本执行在“无恢复意图”的 preflight 使用了恢复后步骤名进行断言，因而在任何写入前失败。检查确认当时系统音量仍为 `0.370000004768372`、恢复意图不存在、受审计进程为 0；修正无意图步骤集合后，连续两次完整六阶段矩阵通过，最后一次绑定到最终审计产物。

最终再次核对代码和证据后确认：

1. 主 runner 的第一处系统 setter 文本和执行路径都位于恢复意图持久化与回读之后；
2. 主 runner 没有直接 native setter，也没有 session setter；
3. 主动异常场景能够在异步方法未返回 intent 的情况下，从稳定文件重新加载 intent 并在 App `finally` 恢复；
4. 强制终止场景的 result 明确停在 `AwaitingExternalRecovery`，恢复由不同 PID 完成；
5. 恢复进程实际观察到探测值，排除了“进程在 setter 前就被杀死、恢复只是空操作”的假阳性；
6. 无意图 preflight/postflight 与有意图恢复使用不同必需步骤，均检查健康 endpoint；
7. result 和 recovery intent 共用一个泛型 source-generated 原子写 helper，并有独立 source-generated 读取；没有引入反射 JSON；
8. 每一阶段都核对 PID、EXE/Rust 路径与哈希、ABI、能力、动态代码状态和正式数据指纹；
9. 恢复文件和受审计 AOT 进程最终均不存在；
10. 普通 JIT 仍默认使用原 C# 音量后端，产品服务与 Rust crate 本轮没有改动。

当前没有发现阻断 5B-3B 完成的遗漏。未覆盖项是匹配媒体 session getter 和 session setter，它们需要稳定、可控、可恢复的测试音频 session。

## 8. 下一阶段建议

下一批建议为 **5B-3C：可控媒体 session 的匹配 getter 与 session setter**，复杂度中高。

建议固定以下边界：

1. 使用测试控制的音频 session，提供稳定进程身份、显示名和可确认的 session 音量；
2. 不操作用户现有播放器，也不依赖当时恰好存在的第三方 session；
3. 先证明产品和原生 snapshot 都匹配到同一 session，再开放 session setter；
4. 写入前持久化原 session 音量，直接原生 getter 只负责明细复查；
5. 覆盖正常恢复、应用内异常恢复、强制终止和独立新进程恢复；
6. 测试 session 消失时必须有明确的清理/结束契约，不能把“session 不存在”误判为恢复成功；
7. 继续要求同次哈希、正式数据指纹和精确进程清理。

系统主音量 setter 已完成，不在 5B-3C 重复。5B-3C 完成后，建议进入托管 UI 功能矩阵；安装/覆盖升级/回滚、CRT 决策、ARM64 和 Rust `SearchCore` 继续后置。本轮不开始 5B-3C。
