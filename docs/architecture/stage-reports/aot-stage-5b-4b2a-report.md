# DeskBox Native AOT 阶段 5B-4B2A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT 设置与固定 Widget 拓扑写入、正常退出、跨进程重载和基线恢复
- 平台：x64 / `win-x64`
- 结论：5B-4B2A 已完成；下一批调整为 5B-4B2B1 Quick Capture 内容 store，Todo 单独留到 5B-4B2B2。本报告不代表 Quick Capture/Todo/Glance/天气内容、OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B2A 在既有 managed UI runner 中增加 `SettingsWidgetPersistenceRestart`，使用同一受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从固定基线写入设置与 File Widget 变更，显式 flush 后正常退出；
2. `VerifyRestore` 在新进程中逐字段确认变更已重载，再恢复原始基线，显式 flush 后正常退出；
3. `Postflight` 在第三个新进程中确认恢复结果仍然成立，保持基线不变并正常退出。

真实矩阵证明：

- 四类设置通过真实 `SettingsViewModel` 路径完成 `false / 2 / 11.5 / Colorful` → `true / 1 / 12.5 / White` → 原值恢复；
- 固定 File Widget 的标题、Icon/List 视图、位置锁、尺寸锁和真实 HWND 边界完成写入、重载和恢复；
- 实测物理边界为 `80,80,300,360` → `104,100,340,392` → `80,80,300,360`；
- `AppSettings`、Settings ViewModel、Widget 配置、File Widget ViewModel 和已加载窗口状态在每个阶段均一致；
- 固定 Search Widget 保持不变，用于发现非目标拓扑漂移；
- 三个进程均由应用正常关闭路径退出，没有依靠外层脚本强制结束；
- 最终 owned preview 根已清理，正式 `%LOCALAPPDATA%\DeskBox` 前后指纹一致，运行日志失败数为 0。

本阶段没有改写或扩展 Rust 产品边界。普通 JIT 仍默认使用原 C# shortcut、音乐音量、Explorer 启动和 Quick Access 实现；NativeAOT 编译期继续使用已经冻结的 Rust 粗粒度边界。

## 2. 范围与安全边界

外层入口为：

```text
scripts/run-aot-managed-ui-smoke.ps1 -Scenario SettingsWidgetPersistenceRestart
```

应用内场景和阶段变量为：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=SettingsWidgetPersistenceRestart
DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE=Mutate|VerifyRestore|Postflight
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界如下：

- DataRoot 必须位于专属 artifact 根下，并带脚本创建且仓库路径匹配的所有权标记；
- 只使用 ID 为 `aot-5b4a-file` 与 `aot-5b4a-search` 的两个固定 Widget；
- 只停止可执行文件完整路径等于受审计 AOT 产物的进程；
- 每个阶段都要求 NativeAOT、正确 EXE、正确 DataRoot、正确 PID 和独立结构化结果；
- 本轮不创建或删除 Widget，不写 Quick Capture、Todo、Glance 或天气内容 store；
- 本轮不触发 Picker、Shell、拖放、快捷键、媒体 setter、网络、定位或第三方应用操作；
- 正式数据目录在矩阵前后比较确定性指纹、文件数和字节数；
- 只有确认三个进程均已退出且证据已归档后，才删除带所有权标记的 preview 根；
- `BasicReadOnly` 与 `DeepSettingsReadOnly` 保留为独立回归场景。

## 3. 三进程状态矩阵

| 阶段 | 进程启动时 | 阶段操作 | flush 后状态 |
| --- | --- | --- | --- |
| `Mutate` | 固定基线 | 修改四类设置；修改 File Widget 标题、视图、锁定和边界 | 完整变更态 |
| `VerifyRestore` | 完整变更态 | 逐字段确认跨进程重载；恢复设置与 File Widget | 固定基线 |
| `Postflight` | 固定基线 | 只读确认恢复结果，执行显式 flush | 固定基线 |

外层脚本执行四组完整等值比较：

```text
Mutate.after == VerifyRestore.before
Mutate.before == VerifyRestore.after
VerifyRestore.after == Postflight.before
Postflight.before == Postflight.after
```

比较对象包含设置文件值、Settings ViewModel 值、两个 Widget 的全部配置字段、File Widget ViewModel 字段、窗口加载/可见/XamlRoot 状态和实际 HWND 边界。它不是只比较 `settings.json` 中的少数字段。

## 4. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotManagedUiSmoke.cs` | 新场景与三阶段调度、真实 Settings ViewModel 修改、显式 flush、结构化证据及正常关闭 |
| `WidgetManager.AotPersistenceSmoke.cs` | 固定 File Widget 的标题、视图、锁定、边界修改与基线恢复；采集配置/ViewModel/已加载 host 证据 |
| `WidgetWindowBase.AotPersistenceSmoke.cs` | 获取真实 HWND 物理边界，在最近显示器工作区内夹取安全边界，并复用产品锚点与配置持久化路径 |
| `run-aot-managed-ui-smoke.ps1` | 建立 owned 根、启动三个新 AOT 进程、逐字段跨进程核对、日志/正式数据/清理门禁和证据归档 |
| `start-aot-preview.ps1` | 支持预期的应用内正常早退，并准确记录会话是否仍在运行 |
| `publish-aot-audit.ps1` | profile 38 / schema 35 的源码、阶段、flush、退出、范围、JSON、警告、Rust 与产物门禁 |
| `AotStage5B4B2AContractTests.cs` | 本阶段 12 条安全、产品路径、三进程、证据、禁止范围和审计契约 |

标题修改没有调用会同步改名快捷方式的 `RenameWidgetAsync`，避免把本轮持久化验证扩张到 Shell 副作用；它更新现有 File Widget ViewModel/config，并通过 `SettingsService.UpdateWidget` 进入通用设置保存链。视图切换、位置锁和尺寸锁分别复用现有产品操作；窗口边界复用现有锚点捕获与 `UpdateConfigBoundsFromPhysical(... persist: true)`。

临时基线边界只存放在 owned File Widget 的 metadata 中，恢复时删除。第三进程明确确认该临时 metadata 不再存在。

## 5. 结构化证据

成功运行归档：

```text
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/verify-restore-result.json
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/settings-widget-persistence-restart/DeskBox.log
```

本次实测使用三个不同 PID，`ProcessCount=3`、`NaturalExitCount=3`、`PreviewRootCleaned=true`、`Running=false`、`RuntimeFailureLogCount=0`。外层脚本还确认矩阵结束后没有同一受审计 EXE 的残留进程。

结果序列化继续复用 `AotManagedUiSmokeJsonContext`，应用内仍只有一次 `JsonSerializer.Serialize` 调用。产品 JSON 固定清单保持 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者，持久化格式没有变化。

## 6. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2A 契约 | 12/12 通过 |
| 全部 AOT 阶段契约 | 208/208 通过 |
| x64 .NET 全量测试 | 2228/2228 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | 相关脚本全部通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 38 / 35 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 82.7 MiB / 179.0 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| 三进程持久化矩阵 | 写入、重载、恢复、再次重载均通过；3/3 正常退出 |
| `BasicReadOnly` 回归 | 2 个表面、6 个主设置分区、12 套语言、6 次筛选、8 次排序，运行日志失败数 0 |
| `DeepSettingsReadOnly` 回归 | 24 个页面、非空搜索、breadcrumb 返回、非空集合投影，运行日志失败数 0 |

上述 UI 项是实际运行受审计 `DeskBox.exe` 的自动化证据，不是仅靠源码扫描。它仍不替代用户对视觉、焦点、键鼠手感、动画和目标系统差异的人工验收。

## 7. 复盘与遗漏检查

本轮完成后再次核对规划、代码、脚本、结构化结果、日志和现有测试，结论如下：

1. 只在同一进程中写入再读取不能证明持久化，现已使用三个不同 PID 分别完成写入、重载恢复和 postflight。
2. 只读取配置对象不能证明窗口已应用，现同时核对配置、ViewModel、host、XamlRoot 和真实 HWND 边界。
3. 只等待固定时长不能证明正常退出，launcher 与 runner 现按精确 EXE 路径等待进程自然消失，并拒绝残留进程。
4. 只恢复内存值可能留下测试 metadata，第三进程已确认恢复后的基线及 metadata 清理结果。
5. 新场景沿用了 5B-4A/4B1 的两个固定 Widget；Search Widget 作为未修改对照项进入全字段比较。
6. `BasicReadOnly` 和 `DeepSettingsReadOnly` 已用同一 profile 38 产物重新实际运行，未被新 runner 分支破坏。
7. 新增源文件和脚本已进入 profile 38 门禁；目标源码 AOT 告警、禁止内容 store/OS 范围模式和非预期告警均为 0。
8. 本轮没有修改 Rust ABI、能力、导出、产品 JSON 格式或普通 JIT 默认后端。

当前没有发现阻断 5B-4B2A 完成的代码遗漏。

## 8. 尚未证明的边界

- Quick Capture 的真实 UI 编辑、600 ms 自动保存、显式 pending-save flush、图片/普通附件、重启恢复和删除清理；
- Todo 的创建、分组/筛选、完成状态、步骤、附件、重启恢复和删除清理；
- Glance 数据、owned 图片、轮播状态和重启恢复；
- 天气手动城市与展示设置的重启恢复，以及定位/网络刷新行为；
- Widget 创建、删除、禁用、可见性和多 Widget 关系变更；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体 UI 交互；
- 安装、覆盖升级、自动更新、卸载、回滚与 CRT 部署决策；
- ARM64、Store、WACK、签名和真实目标设备矩阵。

## 9. 下一阶段调整

原 5B-4B2B 把 Quick Capture 与 Todo 合并。代码复盘后不再把它们视为可同时实施的低复杂度项：

- `QuickCaptureService` 本身约 1900 行，还跨越主记录、最近项、图片缓存、受管附件、软删除/恢复和独立的详情自动保存；
- Todo 使用每个 Widget 独立 store，并包含创建、排序、筛选、完成/复发、步骤、详情和附件等另一套状态机；
- 两者只共享底层 JSON/附件设施，不共享足以让一次跨进程故障快速定位的上层保存链。

因此 5B-4B2B 调整为两个顺序门：

1. **5B-4B2B1 Quick Capture 内容 store**：下一轮开放。使用固定 Quick Capture Widget 和 owned 文本/附件夹具，经真实 UI/ViewModel 路径验证最小记录创建、详情自动保存与显式 flush、受管附件、全新 AOT 进程重载、删除及文件清理。复杂度高。
2. **5B-4B2B2 Todo 内容 store**：B2B1 通过后开放。使用固定 Todo Widget 验证最小任务、分组或筛选所需字段、完成状态、步骤、受管附件、重启恢复和删除清理。复杂度高。

推荐下一步只实施 5B-4B2B1。通用 `SettingsService`、Widget 配置和三进程框架已经由 2A 证明，B2B1 可以把失败范围集中在 Quick Capture 自身的 store、自动保存和附件生命周期。该批不需要扩展 Rust；这些逻辑与 WinUI 状态、JSON 内容模型和文件生命周期紧密耦合，保留在 C# 更直接。
