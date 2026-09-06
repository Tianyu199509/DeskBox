# DeskBox Native AOT 阶段 5B-4B2B2B2 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT Todo 托管附件导入、真实附件卡片投影、跨进程重载、显式附件删除、物理文件清理、任务删除与空状态复核
- 平台：x64 / `win-x64`
- 结论：5B-4B2B2B2 已完成；下一阶段建议拆为 5B-4B2C1 Glance 本地图片与偏好持久化。本报告不代表 Todo 提醒/重复任务、Glance 在线图片、天气网络/定位、完整 OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B2B2B2 在既有 managed UI runner 中增加 `TodoAttachmentsPersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 经真实 Todo 详情入口创建固定任务，再经 `AddAttachmentPathAsync(..., copyToManagedStorageOverride: true)` 导入 owned 文本文件；
2. `VerifyDelete` 在新进程中重载任务、附件元数据、受管副本和实际附件 DataTemplate，经普通删除处理器共用的产品方法删除附件，再删除任务；
3. `Postflight` 在第三个新进程中确认 schema 3 store、ViewModel、Todo surface、附件卡片和受管附件文件清单均为空。

矩阵同时证明：

- 夹具和托管副本 SHA-256 完全一致，托管路径严格位于固定 Widget 的附件目录；
- 非空附件同时存在于 store、强类型 ViewModel 和实际实现的 `AttachmentTileStrip` DataTemplate；
- 实际卡片的 DataContext、文件名、图标、打开按钮 AutomationName 和删除按钮均已实现；
- 第二个进程重载后仍能观察到同一附件 ID、文件名、storage mode、物理文件和实际 UI；
- `DeleteAttachmentAsync` 先保存零附件元数据，再删除物理受管文件；删除后保留一项零附件任务作为中间证据；
- 任务随后删除，第三个进程再次确认零任务、零附件元数据、零 UI 项和零物理文件；
- 三个进程均经应用正常关闭路径退出，正式数据目录前后指纹一致，owned preview 根最终清理。

本阶段没有扩展 Rust 产品边界。`File.Copy` 使用操作系统流式复制，`Get-FileHash` 也以流式方式读取；当前路径不把完整附件读入托管大对象堆。将这一层改为 Rust 不会形成明确的常驻内存收益，反而会增加路径、错误、取消和 FFI 生命周期。Rust 仍保留在已经更适合完整原生边界的 shortcut、Core Audio、Explorer Shell 和 Quick Access。

## 2. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotTodoAttachmentsPersistenceSmoke.cs` | 三阶段调度、固定期望、附件导入/重载/删除断言与结构化证据 |
| `TodoWidgetContent.AotAttachmentsPersistenceSmoke.cs` | 经真实详情创建任务、调用产品受管导入路径、等待实际卡片并经共享删除处理器删除 |
| `AttachmentTileStrip.AotSmoke.cs` | 从真实 `ItemsControl.ContainerFromIndex` 和视觉树读取 DataContext、文件名、图标、按钮与 AutomationName |
| `App.AotTodoPersistenceSmoke.cs` | 共享 Todo 状态证据增加附件元数据、物理文件清单和实际卡片字段 |
| `TodoItemViewModel.cs` / `TodoWidgetContent.xaml` | 保留强类型附件集合，只在 object-valued `ItemsSource` ABI 边界增加可刷新的 `object[] AttachmentItemsSource` |
| `TodoWidgetContent.Attachments.cs` | 将普通删除事件的产品调用提取为可等待方法，普通 UI 行为不变 |
| `WidgetManager.AotTodoPersistenceSmoke.cs` | 增加第三个固定 Todo fixture，并继续拒绝任意 Widget 范围 |
| `run-aot-managed-ui-smoke.ps1` | 三进程执行、哈希/路径/物理删除/状态等值、精确进程、日志、正式数据和 owned 清理门禁 |
| `publish-aot-audit.ps1` | profile 42 / schema 39 的源码、产品路径、真实卡片、绑定、runner、禁止范围、警告与产物门禁 |
| `AotStage5B4B2B2B2ContractTests.cs` | 21 条场景、产品路径、集合投影、取证、清理、Rust 边界和审计契约 |

## 3. 真实 AOT 运行发现并修复的问题

### 3.1 Todo 附件集合需要与步骤相同的 WinRT UI 边界投影

Todo 业务层继续保留：

```text
ObservableCollection<TodoAttachmentViewModel> Attachments
```

实际 NativeAOT 非空场景沿用此前 Quick Capture 和 Todo 步骤已经验证的 ABI 结论，只在 `ItemsSource` 边界提供：

```text
object[] AttachmentItemsSource => Attachments.Cast<object>().ToArray()
```

附件增删时由既有 `RefreshDetailProperties` 同步通知该属性。持久化模型、强类型业务集合和普通 JIT 行为没有改变；`AttachmentTileStrip` 继续使用已有 typed `x:Bind`，也没有新增第四个 generated-bindable provider。

### 3.2 共享宿主没有把新附件场景识别为 Todo fixture

第一次真实运行在进入附件操作前失败。通用 tray/widget 取证只把核心 Todo 和 Todo 步骤识别为 Todo 场景，新附件场景因此错误选择 File fixture ID，随后被“恰好两个 owned Widget”严格断言拒绝。

修复后共享宿主显式识别 `TodoAttachmentsPersistenceRestart`，并选择 `aot-5b4b2b2b2-todo-attachments`。数量门禁没有放宽，仍要求一个固定主 Widget 和一个固定 Search 对照 Widget；新增契约和 AOT 审计冻结这一场景路由。

### 3.3 PowerShell 单元素集合在严格模式下被展开

附件删除已经在产品、store 和物理文件层完成，外层 postflight 校验随后因 `$managedFilesAfterDelete.Count` 失败。原因是 PowerShell 将 `if` 表达式返回的单层数组再次枚举，空或单元素结果不能稳定保留数组形状。

最终把整个条件枚举包在外层 `@(...)` 中，使零、单个和多个文件始终得到数组。该修复没有放宽“必须零文件”的门禁；修复后附件矩阵连续两轮通过。

## 4. 结构化证据

最终成功证据位于：

```text
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/verify-delete-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/final-todo.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-attachments-persistence-restart/DeskBox.log
```

最终连续复跑中的一轮实测：

- `session.json` 记录三个互不相同的 PID，且三个进程均自然退出；
- 夹具和托管副本 SHA-256 均为 `75367A050EA3B06AEBB234ECD645904B5DE0070B747D44D71DDAC32D67F4FE7D`；
- mutate 后任务数 1、附件数 1、物理文件数 1，真实卡片文件名为 `todo-managed-attachment.txt`；
- `InitialAttachmentUiProjected=true`、`RestartAttachmentUiProjected=true`、`ManagedAttachmentDeleted=true`；
- 显式附件删除后任务仍为 1，但附件元数据、卡片和物理文件均为 0；
- task delete 后和 postflight 前后任务数、附件数、UI 项和物理文件数均为 0；
- `RuntimeFailureLogCount=0`、残留受审计 AOT 进程数为 0、`PreviewRootCleaned=true`；
- `session.json` 中正式数据目录的前后指纹相等。

同一最终 AOT 产物还重新通过 `TodoPersistenceRestart` 和 `TodoStepsPersistenceRestart`，两者均为三个不同进程、3/3 正常退出。

## 5. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2B2B2 契约 | 21/21 通过 |
| 全部 AOT 相关测试 | 308/308 通过 |
| x64 .NET 全量测试 | 2295/2295 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | `publish-aot-audit.ps1`、`run-aot-managed-ui-smoke.ps1`、`start-aot-preview.ps1` 全部通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 42 / 39 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 83.5 MiB / 182.0 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| 本阶段缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| generated-bindable 类型 / evidence JSON 调用 | 3 / 1 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish SHA-256 一致 |
| Todo 附件三进程矩阵 | 连续两轮通过，每轮 3/3 正常退出 |
| Todo 核心 / 步骤回归矩阵 | 两项均重新通过，每项 3/3 正常退出 |

`git diff --check` 没有空白错误，只报告工作树已有的 LF/CRLF 转换提示。

## 6. 复盘与遗漏检查

本阶段定义的导入、SHA-256、受管路径、store、ViewModel、真实 DataTemplate、跨进程重载、显式附件删除、物理删除、任务删除和空状态 postflight 均有对应证据，没有发现阻断阶段完成的遗漏。

仍需明确区分以下未覆盖边界：

1. 当前矩阵使用文本文件，证明文件类型卡片；图片缩略图解码、视频/PDF图标和超大附件未在本轮运行。
2. 链接附件不拥有物理文件，删除语义与托管附件不同，本轮没有覆盖。
3. `DeleteItemAsync`、批量删除和清空已完成任务时会保留附件文件，以便当前内存中的 Undo 快照仍可恢复；但 Undo 被覆盖或 dismiss 后没有对应的受管文件垃圾回收。这是既有产品生命周期设计缺口，不能通过“删除任务时立即删文件”简单修补，否则会破坏 Undo。
4. `DeleteAttachmentAsync` 先持久化移除元数据，再尝试删除文件；如果文件被占用或权限拒绝，当前会记录日志但仍返回成功，可能留下无法再由 UI 定位的孤立文件。失败补偿与健康扫描应在发布前形成独立设计和测试。
5. 本轮程序化取证证明实际附件卡片已实现及产品删除处理器路径，但不替代鼠标 hover 后删除按钮显隐、物理点击、键盘焦点、触控、动画和视觉样式人工验收。

上述第 3、4 项应加入发布前债务清单，但不建议夹在 Glance AOT 场景中顺手修改；它们涉及 Undo 与文件恢复策略，需要独立方案。

## 7. 下一阶段建议

下一阶段建议把原 5B-4B2C 拆成两个顺序门，先做 **5B-4B2C1 Glance 本地图片与偏好持久化**：

1. 固定一个 Glance Widget、一个 Search 对照 Widget 和 owned 小型图片夹具；
2. 只走本地文件来源，不访问网络、不使用在线缓存，也不触发 FolderPicker；
3. 经与普通设置入口共用的产品方法写入本地图片路径和一组可逆偏好；
4. 核对 per-widget `GlanceWidgetStore`、`GlanceWidgetViewModel`、真实图片元素、时间/日期可见性和布局字段；
5. 第二个新 AOT 进程重载相同状态，再恢复基线；第三个进程验证 postflight；
6. 继续保持正式数据指纹、精确进程、正常退出、日志和 owned 根清理门禁。

天气改为后续 **5B-4B2C2**。天气 Widget 初始化会立即进入定位或 HTTP 获取，不能与纯本地 Glance 图片混成同一失败面；先只验证手动城市和展示设置，真实定位、网络源切换和刷新仍留给 OS/网络矩阵。

5B-4B2C1 不建议使用 Rust。Glance 偏好文档很小，本地图片枚举也不是当前明确的托管常驻内存热点，实际解码主要由 WinUI 图像栈承担。下一项具备明显潜在内存收益的 Rust 候选仍是 300k 条目级 `SearchCore`，应在阶段 5 的 AOT 功能矩阵稳定后用 Private Bytes、查询 P50/P95 和双后端结果一致性决定是否切换。
