# DeskBox Native AOT 阶段 5B-4B2B1 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT Quick Capture 内容 store、真实详情保存、托管附件、跨进程重载与删除后复核
- 平台：x64 / `win-x64`
- 结论：5B-4B2B1 已完成；下一阶段调整为 5B-4B2B2A Todo 核心任务与备注持久化，Todo 步骤和托管附件单独留到 5B-4B2B2B。本报告不代表 Quick Capture 图片/剪贴板入口、Todo、Glance、天气、OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B2B1 在现有 managed UI runner 中增加 `QuickCapturePersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从空 store 打开真实 Quick Capture 新建详情，完成 pending-save flush、真实 600 ms 自动保存和托管附件导入，然后正常退出；
2. `VerifyDelete` 在新进程中重载记录、详情和附件，再完成一次显式 pending-save flush，随后经产品路径删除附件和记录并正常退出；
3. `Postflight` 在第三个新进程中确认 store、列表、详情状态和托管附件目录仍为空，然后正常退出。

真实矩阵证明：

- 新记录经 `OpenNewDetailAsync`、详情编辑器、dirty revision 和 `FlushPendingDetailSaveAsync` 保存，不是直接构造 JSON；
- 已存在记录的正文修改经 `ScheduleDetailAutoSave` 启动生产环境的 600 ms debounce，并通过保存 revision 前进确认定时保存真实发生；
- 普通文本附件使用 `ForceManagedCopy=true` 进入现有托管附件产品路径，夹具和托管副本 SHA-256 完全一致；
- 第二个进程同时从 store、ViewModel、真实 Quick Capture surface 和物理文件系统核对重载结果；
- 显式 flush 将正文从 `AOT Quick Capture real 600 ms auto-save edit` 更新为 `AOT Quick Capture explicit restart flush edit`；
- `DeleteAttachmentAsync` 后记录仍存在但附件集合和物理托管文件均归零；`DeleteQuickCaptureItemAsync` 后记录、详情和列表均归零；
- 第三个进程确认最终 schema 4 store 的 `items` 和 `recentItems` 均为空，托管文件数仍为 0；
- 三个进程均由应用正常关闭路径退出，没有依靠外层脚本强制结束；
- 最终 owned preview 根已清理，正式 `%LOCALAPPDATA%\DeskBox` 前后指纹一致，运行日志失败数为 0。

本阶段没有改写或扩展 Rust 产品边界。Quick Capture 的 UI、ViewModel、JSON 内容模型和附件生命周期继续保留在 C#；这部分与 WinUI 状态和用户内容事务紧密耦合，没有迁移 Rust 的收益依据。

## 2. 范围与安全边界

外层入口为：

```text
scripts/run-aot-managed-ui-smoke.ps1 -Scenario QuickCapturePersistenceRestart
```

应用内场景和阶段变量为：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=QuickCapturePersistenceRestart
DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE=Mutate|VerifyDelete|Postflight
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界如下：

- DataRoot 必须位于专属 artifact 根下，并带脚本创建且仓库路径匹配的所有权标记；
- 只使用 ID 为 `aot-5b4a-search` 和 `aot-5b4b2b1-quick-capture` 的两个固定 Widget；
- Search Widget 是未修改对照项，Quick Capture Widget 是唯一内容变更目标；
- 只停止可执行文件完整路径等于受审计 AOT 产物的进程；
- 每个阶段都要求 NativeAOT、正确 EXE、正确 DataRoot、正确 PID、真实 XamlRoot 和独立结构化结果；
- 附件夹具位于 evidence archive，导入目标只能位于 owned preview 根的 Quick Capture 托管附件目录；
- 正式数据目录在矩阵前后比较确定性指纹、文件数和字节数；
- 只有确认三个进程均已退出且证据已归档后，才删除带所有权标记的 preview 根；
- 本轮不写 Todo、Glance 或天气内容 store，不创建或删除 Widget；
- 本轮不触发剪贴板监听、图片捕获、Picker、Shell、拖放、快捷键、媒体 setter、网络或定位。

## 3. 三进程状态矩阵

| 阶段 | 进程启动时 | 产品路径操作 | 退出前状态 |
| --- | --- | --- | --- |
| `Mutate` | schema 4 空 store、0 个托管文件 | 新建详情并显式 flush；修改已存在详情并等待 600 ms 自动保存；导入托管文本附件 | 1 条 Markdown 记录、1 个托管附件、1 个物理文件 |
| `VerifyDelete` | 重载上述完整状态 | 修改详情并显式 flush；删除附件；删除记录 | 0 条记录、0 个附件、0 个物理文件 |
| `Postflight` | 重载删除后状态 | 只读核对 store、surface、详情和附件目录 | 保持全空 |

外层脚本执行以下关键比较：

```text
Mutate.after == VerifyDelete.before
VerifyDelete.after == Postflight.before
Postflight.before == Postflight.after
fixture SHA-256 == managed attachment SHA-256
```

比较范围包含 store schema、记录 ID/正文/格式/来源、附件 ID/路径/显示名/存储模式/存在性、托管文件相对路径、surface 初始化与 XamlRoot、列表数量、详情选择、编辑/dirty 状态和 pending attachment 数量。它不是只读取 `quick-capture.json` 的单个字段。

## 4. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotQuickCapturePersistenceSmoke.cs` | 新场景三阶段调度、状态证据模型、固定正文与正常关闭 |
| `QuickCaptureSurfaceContent.AotPersistenceSmoke.cs` | 经真实详情 UI、pending flush、600 ms 自动保存、附件添加/删除和记录删除路径执行矩阵 |
| `WidgetManager.AotQuickCapturePersistenceSmoke.cs` | 获取固定 Quick Capture 的真实 content window、surface、可见性、HWND 和 XamlRoot |
| `App.AotManagedUiSmoke.cs` | 注册新场景并继续复用唯一 source-generated 结果写入入口 |
| `run-aot-managed-ui-smoke.ps1` | 建立 owned 根、启动三个新 AOT 进程、跨进程等值比较、哈希/日志/正式数据/清理门禁和证据归档 |
| `start-aot-preview.ps1` | 接受 profile 39 / schema 36，并记录应用内正常早退是否完成 |
| `publish-aot-audit.ps1` | profile 39 / schema 36 的 B2B1 源码、runner、surface、manager、禁止范围、JSON、告警、Rust 与产物门禁 |
| `AotStage5B4B2B1ContractTests.cs` | 本阶段 14 条产品路径、三进程、证据、清理、禁止范围和审计契约 |

runner 没有直接调用自动保存 timer 的 tick handler。它先通过生产方法安排 debounce，再等待 `_detailSavedRevision` 追上目标 revision，因此能够区分“仅修改了内存文本”和“生产自动保存链已完成”。显式 flush 则单独覆盖 debounce 尚未到期时的退出/切换保护语义。

## 5. 真实 AOT 运行发现并修复的问题

第一次实际运行到非空附件列表时，`DetailAttachmentStrip.ItemsSource` 接收 typed `IReadOnlyList<TodoAttachmentViewModel>`，NativeAOT/WinRT 投影返回 `E_INVALIDARG`。静态契约、普通 .NET 测试和空集合运行都没有提前暴露这一点。

最终只在 UI 边界改为：

```text
attachments.Cast<object>().ToArray()
```

业务层仍保留 typed attachment collection，附件模型、保存格式、删除语义和普通 JIT 默认路径均未改变。这个修复与 5B-4A/4B1 已确认的 managed collection 投影规律一致，但门禁只冻结 Quick Capture 实际失败的这一处，没有扩大为全局 Binding 改造。

## 6. 结构化证据

成功运行归档：

```text
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/verify-delete-result.json
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/final-quick-capture.json
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/quick-capture-attachment.txt
.artifacts/aot-managed-ui-smoke/win-x64/quick-capture-persistence-restart/DeskBox.log
```

本次实测数据：

- PID：`29980`、`34188`、`27156`，三个 PID 均不同；
- `ProcessCount=3`、`NaturalExitCount=3`、`PreviewRootCleaned=true`、`Running=false`；
- mutate 的 `PendingSaveFlushed=true`、`AutoSaveObserved=true`；
- fixture 与托管副本 SHA-256 均为 `CB7637BFD92D64354A3D7C758237BCBDC93892CCF0F1574E0A16C617D99E5DB3`；
- 删除附件后托管文件数为 0，删除记录后 item 数为 0；
- postflight 前后 item 数与托管文件数均为 0；
- `RuntimeFailureLogCount=0`，同一受审计 EXE 的残留进程数为 0；
- 正式数据指纹前后一致。

结果序列化继续复用 `AotManagedUiSmokeJsonContext`，应用内仍只有一次 `JsonSerializer.Serialize` 调用。产品 JSON 固定清单保持 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者，Quick Capture store 仍为 schema 4。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2B1 契约 | 14/14 通过 |
| 全部 AOT 阶段契约 | 222/222 通过 |
| x64 .NET 全量测试 | 2242/2242 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | 相关脚本全部通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 39 / 36 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 82.9 MiB / 179.8 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| 非预期告警 | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| Quick Capture 三进程矩阵 | 创建、pending flush、600 ms 自动保存、托管附件、重载、显式 flush、附件删除、记录删除、postflight 全部通过；3/3 正常退出 |

上述 UI 项是实际运行受审计 `DeskBox.exe` 的程序化产品路径证据，不是仅靠源码扫描。它仍不替代用户对物理键鼠输入、视觉层级、焦点、动画、IME 和目标系统差异的人工验收。

## 8. 复盘与遗漏检查

本轮完成后再次核对规划、代码、脚本、结构化结果、日志和现有测试，结论如下：

1. 新建记录、已有记录、自动保存和显式 flush 是四个不同状态边界，矩阵已分别覆盖，没有用直接 store 写入替代 UI 保存链。
2. 自动保存按 revision 观察生产 debounce 完成，没有直接调用 timer tick 或仅等待固定时间后假定成功。
3. 重载同时核对 store、ViewModel、真实 surface 和物理附件，不把单层 JSON 读取当作完整 UI 证据。
4. 附件删除和记录删除分步留证，能够确认物理文件是在附件产品删除路径中消失，而非随 preview 根最终清理才消失。
5. 三个阶段都使用同一受审计 EXE，但分别为全新 PID；3/3 均正常退出，最终没有 AOT 进程残留。
6. 证据在删除 owned preview 根前完成归档；正式数据根前后指纹一致。
7. 新增源文件、runner、launcher、项目编译项和审计门禁均进入 profile 39；B2B1 缺失模式、禁止范围、目标源码告警和非预期告警均为 0。
8. 新场景仍使用唯一 source-generated writer；产品 JSON 清单、Rust ABI、能力、导出和普通 JIT 默认后端没有变化。
9. 实际运行暴露的附件投影问题已以单点 `object[]` UI 边界修复，并由新契约和真实 AOT 重跑共同验证。
10. 最终 schema 4 store 明确为 `items=[]`、`recentItems=[]`，不是只看 UI 空列表。

当前没有发现阻断 5B-4B2B1 完成的代码遗漏。

## 9. 尚未证明的边界

- Quick Capture 的剪贴板监听、图片剪贴板、图片缓存、recent/pin/search、软删除恢复、导出和大内容边界；
- Quick Capture 的物理键鼠/触控编辑、IME、焦点切换、动画和附件可视样式；
- Todo 的创建、备注保存、完成状态、步骤、附件、重启恢复和删除清理；
- Glance 数据、owned 图片、轮播状态和重启恢复；
- 天气手动城市与展示设置的重启恢复，以及定位/网络刷新行为；
- Widget 创建、删除、禁用、可见性和多 Widget 关系变更；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体 UI 交互；
- 安装、覆盖升级、自动更新、卸载、回滚与 CRT 部署决策；
- ARM64、Store、WACK、签名和真实目标设备矩阵。

## 10. 下一阶段调整

原路线图把 Todo 内容 store 作为单个 5B-4B2B2 批次。重新核对实现后，建议再拆成两个顺序门：

1. **5B-4B2B2A Todo 核心任务与备注持久化**：下一轮开放。使用固定 Todo Widget，经真实 surface/ViewModel 路径验证最小任务创建、标题修改、非重复任务完成状态、详情 Markdown 备注的 600 ms 自动保存与退出前显式保存、全新 AOT 进程重载、任务删除和空 store postflight。复杂度中高。
2. **5B-4B2B2B Todo 步骤与托管附件生命周期**：2B2A 通过后开放。验证步骤创建/修改/完成/删除、托管附件导入/哈希/重载/显式删除和物理文件清理。复杂度高。

拆分依据：

- Todo 备注有独立的 600 ms timer、`SemaphoreSlim` save gate、selection-change flush 和失败重试状态；
- 完成操作还包含重复任务生成分支，2B2A 应固定为无 recurrence 的普通任务，避免把提醒/复发一并带入；
- 步骤和附件各有独立集合及保存链，附件还增加物理文件一致性与 NativeAOT collection 投影风险；
- `DeleteItemAsync` 带撤销快照且不直接承担托管附件物理删除，2B2B 的清理验证应先走明确的 `DeleteAttachmentAsync`，再删除任务，不能把两种语义混为一项；
- 两个小矩阵可复用本轮已稳定的三进程、owned root、哈希、日志和自然退出框架，不需要扩展 Rust。

因此下一步只实施 5B-4B2B2A。2B2A 完成后再复盘是否需要调整 2B2B；Glance/天气仍留在 5B-4B2C，OS 交互留在 5B-4C，安装升级与发布矩阵继续后置。
