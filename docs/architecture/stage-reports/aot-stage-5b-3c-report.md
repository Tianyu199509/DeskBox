# DeskBox Native AOT 阶段 5B-3C 完成与复盘报告

- 审计日期：2026-08-22
- 范围：可控媒体 session 匹配 getter、session setter、应用内恢复、强制终止后的独立恢复
- 平台：x64 / `win-x64`
- 结论：5B-3C 已完成，可以进入 5B-4 托管 UI 功能矩阵；本报告不代表完整 UI、安装升级、CRT、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-3C 使用测试专用 Rust 静音音频夹具和最终受审计的 Native AOT 产物，实际证明：

1. 产品 `MusicVolumeService.GetVolumeAsync` 与直接 Rust snapshot 都能稳定匹配同一个受控 session，匹配类型固定为 process/display-name match（kind 4）；
2. session 音量写入只经过产品 `TrySetSessionVolumeAsync`，runner 不调用直接原生 setter，也不调用系统主音量 setter；
3. 每次变更前，原 session 音量、探测值、受控身份、夹具 PID 和系统主音量都先原子写入恢复意图并立即 source-generated 回读；
4. 正常流程完成 session `1.0 → 0.92 → 1.0`；
5. 写入后主动异常时，App 外层 `finally` 恢复原 session 音量；
6. 写入后强制终止时，独立新 AOT 进程先观察到约 `0.9200000167`，再通过产品 setter 恢复到 `1.0`；
7. 如果受控 session 已消失，恢复流程会保留恢复意图并报告 `session-disappeared-intent-preserved`，不会把“找不到 session”误判为恢复成功；
8. 六阶段结束后恢复意图不存在、受控夹具和受审计 AOT 进程均已清理、正式数据指纹不变，系统主音量前后保持 `0.370000004768372`。

本阶段没有控制用户播放器，也没有把测试夹具复制到产品发布目录。普通 JIT 仍默认使用既有 C# 音量实现；Native AOT 继续只保留 Rust Core Audio 产品边界。

## 2. 可控 Rust 音频夹具

新增工作区包 `native/deskbox-audio-session-fixture`。它只供 5B-3C 外层脚本使用：

- 生成 8 kHz、16-bit、mono 的 1 秒 PCM WAV，数据区全部为 0；
- 通过 `PlaySoundW` 的 `SND_FILENAME | SND_ASYNC | SND_LOOP | SND_NODEFAULT` 建立稳定 session；
- 可执行文件名和传给产品的 display name 均固定为 `deskbox-audio-session-fixture`；
- 接受绝对 `--wave`、`--ready`、`--stop` 和 `--parent-pid` 参数；
- 持有父脚本进程句柄，父进程退出时自动停止音频并退出；正常流程由 stop marker 收尾；
- 音频负载为静音，不产生可听声音，但仍提供真实 Core Audio session。

外层脚本在启动前拒绝任何同名既有进程，只跟踪自己创建且路径、PID 均精确匹配的夹具。无论进入主矩阵前还是矩阵执行中失败，清理都只作用于该精确实例。夹具二进制、路径、哈希、PID 和静音属性被记录到隔离证据中，但不属于九个生产 Rust ABI 导出。

## 3. 产品调用与身份边界

新增 `App.AotMusicVolumeSessionMutationSmoke.cs`，仅在 `DESKBOX_NATIVE_AOT` 中编译，并且只有显式设置以下变量才运行：

```text
DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE
DESKBOX_AOT_MUSIC_VOLUME_SESSION_FIXTURE_PID
```

runner 要求 preview 数据根、唯一同名进程和传入 PID 三者同时成立，否则返回 `RefusedNonPreviewRoot` 或 `RefusedUntrustedFixture`。它使用固定 identity：

```text
sourceAppUserModelId = DeskBox.Aot.Controlled.Session.Identity
sourceDisplayName   = deskbox-audio-session-fixture
expectedMatchKind   = 4
```

产品边界只有：

```csharp
await new MusicVolumeService().GetVolumeAsync(sourceAppUserModelId, sourceDisplayName)
await new MusicVolumeService().TrySetSessionVolumeAsync(sourceAppUserModelId, sourceDisplayName, volume)
```

`MusicVolumeNativeBackend.GetSnapshot` 只用于核对 status、HRESULT、`HasSessionVolume`、匹配类型、阶段掩码和数值。runner 中没有直接 session/system native setter；系统主音量在每个阶段都只读，并要求整个矩阵前后不变。

## 4. 恢复安全设计

稳定恢复意图位于隔离 preview 根：

```text
aot-music-volume-session-mutation-smoke/session-recovery-intent.json
```

意图包含 preview 根、固定身份、夹具 PID、来源 PID/EXE、初始系统主音量、原 session 音量和探测值。关键顺序由契约与 AOT 审计共同冻结：

1. 产品 getter 与直接 Rust snapshot 同时确认 kind 4 的健康 session；
2. 选取与原值相差 `0.08` 的安全探测值；
3. 以临时文件加覆盖移动方式原子写入恢复意图；
4. 使用独立 source-generated context 立即读取并核对全部身份和数值；
5. 才允许调用产品 session setter；
6. 恢复时重新确认同一夹具 PID 仍存活且匹配 session 仍存在；
7. 通过产品 setter 恢复，产品 getter 与直接 Rust getter 都验证原值，系统主音量也必须未变；
8. 最后删除恢复意图。

损坏意图、身份不符、夹具进程不符、session 消失、setter 失败或恢复读数不符都会保留意图。旧会话留下的意图不会被脚本删除或伪装为已恢复；没有能够证明的同一活跃夹具时，脚本会拒绝开始新矩阵。

## 5. 真实 AOT 六阶段结果

六阶段均绑定到同一次受审计 AOT/Rust 产物和同一个受控夹具：

| 阶段 | 场景 | 关键结果 |
| --- | --- | --- |
| preflight | `ReadMatchedSession` | 产品与 Rust getter 均匹配 kind 4；session 原值 `1.0` |
| in-process-failure | `ChangeThenFail` | 写入 `0.92`，固定异常后由 App `finally` 恢复为 `1.0` |
| forced-termination | `ChangeThenAwaitExternalRecovery` | 写入并确认 `0.92`，恢复意图保留，受审计进程被精确结束 |
| recovery | `RecoverOriginal` | 独立进程观察约 `0.9200000167`，恢复为 `1.0` 并删除意图 |
| main | `ChangeRestore` | 正常完成 `1.0 → 0.92 → 1.0` |
| postflight | `RecoverOriginal` | 独立最终恢复/确认；无遗留意图，session 为 `1.0` |

关键结果：

| 项目 | 结果 |
| --- | --- |
| 动态代码 | `false` |
| 产品后端 | `Rust` |
| session match kind | `4` |
| Rust snapshot phases | 健康读取覆盖 `0x3F` |
| 原始 / 探测 / 最终 session 音量 | `1.0 / 0.92 / 1.0` |
| 系统主音量前后 | `0.370000004768372 / 0.370000004768372` |
| 恢复意图最终存在 | `false` |
| AOT 与夹具清理 | `true / true` |
| 正式数据指纹前后 | 一致 |

结构化证据位于：

```text
.artifacts/aot-music-volume-session-mutation-smoke/win-x64/session.json
```

并保留六个阶段 result JSON。证据记录同次 `DeskBox.exe`、`deskbox_native.dll` 和夹具 EXE 的路径与 SHA-256；哈希用于绑定本次审计和运行，不宣称跨次 Native AOT 构建字节级可复现。

## 6. AOT 与自动化门禁

最终门禁基线为 profile 35 / schema 32：

| 验证 | 结果 |
| --- | --- |
| 5B-3C 新契约红灯 | 实现前 0/12，按预期失败 |
| 5B-3C 契约与 JSON 清单 | 13/13 通过 |
| JSON 固定清单 | 22 个文件、57/57 处 source-generated 调用、20 个 context 所有者 |
| PowerShell 语法 | 全部脚本通过解析 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| Rust 单元测试 | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| x64 全量测试 | 2192/2192 通过 |
| 发布文件 / 分离 PDB | 39 / 3 |
| WMC1506 / WMC1510 | 0 / 1216 |
| 完整 `always-throw` | 0 |
| 5B-3C 缺失 runner / launch / product / fixture / script 模式 | 0 / 0 / 0 / 0 / 0 |
| 不安全直接 setter / runner / 夹具脚本模式 | 0 / 0 / 0 |
| 持久化、setter、验证恢复、删除顺序 | 通过 |
| 5B-3C 目标源警告 | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| 真实 AOT session 六阶段矩阵 | 通过 |

## 7. 实施复盘与遗漏检查

实施和复盘发现并修正了以下阶段内遗漏：

1. 新 Rust 工作区包加入后，旧 AOT 发布契约仍冻结单成员 workspace；已改为同时登记生产 crate 与测试夹具，随后 x64 全量 2192/2192 通过。
2. 第一版 postflight 只执行 `ReadMatchedSession`。如果前一阶段异常留下合法恢复意图，只读场景会拒绝但不会补偿；现改为独立 `RecoverOriginal`，并移除“进入 postflight 时不得存在意图”的旧断言，只保留“恢复完成后意图必须不存在”的门禁，使最终阶段具备实际恢复能力。
3. 第一版外层脚本只逐阶段检查系统主音量；现增加整个矩阵 preflight/postflight 比较，任何跨阶段漂移都使任务失败。
4. 夹具在进入主 `try/finally` 前的 ready/path 校验失败时也必须清理；现增加启动期补偿，仅停止本脚本创建且 PID/path 精确匹配的实例。
5. 六个 AOT smoke 脚本原有 opt-in 隔离只覆盖五个 runner；现全部保存、清空并恢复新增的 session mutation opt-in，避免父环境残留导致多 runner 同时进入。

最终代码复核确认：

1. 产品 session setter 的调用顺序严格位于恢复意图持久化与回读之后；
2. 正常、主动异常、强制终止、独立恢复和最终 postflight 都使用同一受控身份；
3. session 消失明确保留意图，没有“对象不存在等于恢复成功”的假阳性；
4. runner 没有直接 native setter，也没有任何系统主音量 setter；
5. 夹具为静音、父进程绑定、非发布组件，不接管用户第三方播放器；
6. 六个 runner opt-in 互相隔离，正式数据根、AOT 进程和夹具进程都有独立清理门禁；
7. JSON 结果与恢复意图均显式使用 source-generated 类型信息，没有反射回退；
8. 普通 JIT 的产品行为和 Rust ABI 2/能力 255/九个导出均未改变。

当前没有发现阻断 5B-3C 完成的遗漏。

## 8. 下一阶段建议

下一批建议为 **5B-4：x64 Native AOT 托管 UI 功能矩阵**，总体复杂度高，但应拆成三个可单独验收的小批次：

1. **5B-4A 基础窗口与只读路径**：托盘、主界面、设置各分区、搜索打开/筛选/排序、语言和资源、Widget 恢复；优先验证，不预设重构。
2. **5B-4B 持久化与组件变更路径**：Widget 增删锁定、Quick Capture、Todo、Glance、天气、设置保存与重启恢复；使用隔离数据根并建立前后指纹。
3. **5B-4C OS 交互路径**：文件拖入拖出、复制移动/跨卷/回收站、上下文菜单、全局快捷键、FolderPicker、Quick Access/Explorer、音乐与媒体控制；自动化证据和用户人工 UI 证据分开记录。

这一阶段以真实 AOT 功能核对和发现问题后的窄修复为主，不先启动大范围 Rust 重构，也不恢复无边界的 XAML Binding 清理。安装/覆盖升级/回滚、CRT 选择、ARM64/Store 和 Rust `SearchCore` 继续后置。5B-4 完成后再进入安装升级闭环，避免把 UI 行为问题与安装器问题混在同一批定位。
