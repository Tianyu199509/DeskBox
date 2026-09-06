# DeskBox Native AOT 阶段 5B-4B2B2B1 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT Todo 步骤创建、文本修改、完成状态、跨进程重载、步骤删除与任务删除后复核
- 平台：x64 / `win-x64`
- 结论：5B-4B2B2B1 已完成；下一阶段调整为 5B-4B2B2B2 Todo 托管附件生命周期。本报告不代表 Todo 附件/提醒/重复任务、Glance、天气、OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B2B2B1 在现有 managed UI runner 中增加 `TodoStepsPersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从 schema 3 空 Todo store 经真实详情入口创建固定普通任务，通过详情步骤输入新增步骤，再经真实行控件完成文本修改和完成状态切换；
2. `VerifyDelete` 在新进程中重载任务和已完成步骤，从真实行 UI 将步骤恢复为未完成，再依次删除步骤和任务；
3. `Postflight` 在第三个新进程中确认 schema 3 store、ViewModel、真实 Todo surface、详情选择和步骤 UI 均为空。

真实矩阵证明：

- 任务经 `OpenAddEditorAsync`、`DetailTitleTextBox` 和 `FinalizeDetailAsync` 创建，没有直接构造模型或写 JSON；
- 步骤经 `DetailNewStepTextBox`、`AddDetailStepAsync` 和 `TodoWidgetViewModel.AddStepAsync` 创建；
- 文本修改、完成/恢复和删除均使用 DataTemplate 实际实现出的 TextBox、CheckBox 与删除按钮，并与普通事件处理器共用可等待的产品方法；
- 每个非空阶段同时核对 Todo store、强类型 ViewModel、实际 DataTemplate DataContext、行文本、复选框和完成态透明度；
- `true → 跨进程重载 true → false` 的完成状态往返已通过，步骤删除后先验证任务仍存在且步骤数为 0，再删除任务；
- 三个进程均由应用正常关闭路径退出，外层脚本没有用强制结束替代成功退出；
- 最终 owned preview 根已清理，正式 `%LOCALAPPDATA%\DeskBox` 前后指纹一致，运行失败日志和残留受审计 AOT 进程均为 0。

本阶段没有扩展 Rust 产品边界。Todo 步骤属于 WinUI DataTemplate、ViewModel 集合通知和 Todo store 的现有 C# 状态链；保持该链并只修正 AOT UI 投影，比建立跨语言步骤状态同步更简单且风险更小。

## 2. 范围与安全边界

外层入口为：

```text
scripts/run-aot-managed-ui-smoke.ps1 -Scenario TodoStepsPersistenceRestart
```

应用内场景和阶段变量为：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=TodoStepsPersistenceRestart
DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE=Mutate|VerifyDelete|Postflight
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界如下：

- DataRoot 必须位于专属 artifact 根下，并带脚本创建且仓库路径匹配的所有权标记；
- 只使用 ID 为 `aot-5b4a-search` 和 `aot-5b4b2b2b1-todo-steps` 的两个固定 Widget；
- Search Widget 是未修改对照项，Todo Widget 是唯一内容变更目标；
- 只停止可执行文件完整路径等于受审计 AOT 产物的进程；
- 每个阶段均验证 NativeAOT、正确 EXE、正确 DataRoot、独立 PID、真实 HWND、XamlRoot 和结构化结果；
- 正式数据目录在矩阵前后比较确定性指纹、文件数和字节数；
- 只有三个进程退出且证据归档完成后，才删除带所有权标记的 preview 根；
- 本轮不导入附件，不设置日期、提醒或 recurrence，不调用 Picker、Shell、拖放、快捷键、媒体 setter、网络或定位；
- 本轮不创建或删除 Widget，不写 Quick Capture、Glance 或天气内容 store。

## 3. 三进程状态矩阵

| 阶段 | 进程启动时 | 产品路径操作 | 退出前状态 |
| --- | --- | --- | --- |
| `Mutate` | schema 3 空 store | 创建普通任务；新增步骤；修改步骤文本；设为已完成 | 1 个任务、1 个已完成步骤；真实行文本、复选框和透明度一致 |
| `VerifyDelete` | 重载上述完整状态 | 将步骤恢复未完成；删除步骤；删除任务 | 先保留 1 个零步骤任务，再变为 0 个任务 |
| `Postflight` | 重载删除后状态 | 只读核对 store、surface、详情和步骤 UI | 保持全空 |

外层脚本执行以下关键比较：

```text
Mutate.after == VerifyDelete.before
VerifyDelete.after == Postflight.before
Postflight.before == Postflight.after
```

比较范围包含 store schema 与存在性、任务和步骤 ID、文本、完成状态、排序、核心延期字段、surface 初始化、XamlRoot、列表和详情状态，以及实际步骤行数量、DataContext、文本、复选框和透明度。步骤删除后的中间状态另行比较，确保任务删除没有掩盖步骤删除失败。

## 4. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotTodoStepsPersistenceSmoke.cs` | 三阶段调度、固定期望、store/ViewModel/UI 取证、步骤与零步骤断言 |
| `TodoWidgetContent.AotStepsPersistenceSmoke.cs` | 经真实详情输入和实际实现行执行创建、编辑、完成、恢复与删除 |
| `TodoWidgetContent.DetailNotesAndSteps.cs` | 将步骤文本、完成和删除事件的产品路径提取为可等待方法，普通事件行为保持不变 |
| `TodoWidgetContent.xaml` | 为步骤 ItemsControl 和实际行控件提供稳定名称，并使用 AOT 安全 UI 投影 |
| `TodoItemViewModel.cs` | 保留强类型 `Steps`，新增仅供 `ItemsSource` ABI 使用的 `object[] StepItemsSource` 并在增删时通知刷新 |
| `TodoStepViewModel.cs` / `TodoViewModels.AotBindableProperties.cs` | 仅为实际进入 DataTemplate 的步骤 DataContext 增加 NativeAOT 生成属性提供器 |
| `App.AotTodoPersistenceSmoke.cs` | 共享 Todo 状态证据，显式接收 widget ID，避免核心与步骤 fixture 串读 |
| `WidgetManager.AotTodoPersistenceSmoke.cs` | 只允许两个固定 Todo fixture，并获取真实窗口、adapter、surface、HWND 与 XamlRoot |
| `run-aot-managed-ui-smoke.ps1` | 三进程执行、跨阶段等值比较、精确进程/日志/正式数据/清理门禁和证据归档 |
| `publish-aot-audit.ps1` | profile 41 / schema 38 的源码、产品路径、UI 投影、绑定、runner、禁止范围、告警与产物门禁 |
| `AotStage5B4B2B2B1ContractTests.cs` | 本阶段 17 条场景、产品路径、投影、取证、清理、禁止范围和审计契约 |

## 5. 真实 AOT 运行发现并修复的问题

### 5.1 强类型步骤集合没有投影到 ItemsControl

第一次真实运行中，`AddStepAsync` 已将步骤写入 ViewModel 和 `todo.json`，但 `DetailStepsItemsControl.Items.Count` 持续为 0。静态契约和 AOT 编译均能通过，故障只出现在非空 `ObservableCollection<TodoStepViewModel>` 跨 WinRT object-valued `ItemsSource` 的运行时投影。

最终保留业务层的强类型集合：

```text
ObservableCollection<TodoStepViewModel> Steps
```

只在 UI 边界增加：

```text
object[] StepItemsSource => Steps.Cast<object>().ToArray()
```

步骤增删已有的 `RefreshDetailProperties` 同时通知 `StepItemsSource`，XAML 改为绑定该投影。Todo store 模型、JSON 格式和步骤业务 API 均未改变。修复后实际 DataTemplate 行、DataContext、文本、复选框和透明度全部可在 NativeAOT 中观察。

### 5.2 共享状态证据读取了上一阶段的 fixture store

第二次真实运行已完成新增、文本修改和完成状态 UI 投影，但随后的结构化断言从空列表取步骤。磁盘上的步骤 store 内容正确，原因是共享 `CaptureAotManagedUiTodoStateAsync` 仍硬编码读取上一阶段 `aot-5b4b2b2a-todo` 的 store。

最终让状态采集显式接收 widget ID。核心 Todo 矩阵传入旧 ID，步骤矩阵传入 `aot-5b4b2b2b1-todo-steps`。审计冻结该参数化约束，并用同一份最终 AOT 产物重新运行旧 `TodoPersistenceRestart`；旧矩阵与新矩阵均为 3/3 正常退出。

### 5.3 重启取证早于步骤行完成布局投影

一次最终复跑中，`VerifyDelete` 的 store、ViewModel 和详情选择均已正确恢复，但立即采集的步骤 UI 仍为 0 项。此前成功运行依赖了窗口布局恰好在取证前完成，存在时序不确定性。

最终在打开重载任务后调用 `WaitForAotTodoStepProjectionAsync`，复用既有 `WaitForAotTodoStepRowAsync` 的真实条件等待：步骤 ID、文本、完成状态、DataContext、TextBox、CheckBox 和透明度必须同时一致。该修复没有加入固定 sleep，也没有放宽后续快照断言；只有实际行完成投影后才允许建立 `VerifyDelete.before` 证据。

### 5.4 历史静态契约仍冻结旧 profile 和旧场景列表

扩大 AOT 测试首次运行时有四条历史契约仍要求 profile 40 或不包含 `TodoStepsPersistenceRestart` 的旧 `ValidateSet`。这些失败不属于产品运行问题。对应期望已更新到 profile 41 和当前完整场景列表，没有删除或放宽原有断言；全部 AOT 相关测试随后通过。

## 6. 结构化证据

成功运行归档：

```text
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/verify-delete-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/final-todo.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-steps-persistence-restart/DeskBox.log
```

本次实测数据：

- PID：`31024`、`37868`、`17952`，三个 PID 均不同；
- mutate 的 `InitialStepUiProjected=true`、`StepTextEditObserved=true`，步骤文本为 `AOT Todo persisted edited step`；
- mutate 退出时 store、ViewModel 和真实行均为 1 个已完成步骤，UI 透明度约为 `0.58`；
- verify-delete 启动时仍为已完成，`StepCompletionRoundTripObserved=true`，恢复后复选框为 false、透明度为 1；
- 步骤删除后任务仍存在且 `StepCount=0`，随后任务删除；postflight 前后 item 数均为 0；
- 三个阶段均 `NormalShutdownRequested=true`，3/3 自然退出；
- `RuntimeFailureLogCount=0`、残留 AOT 进程数为 0、`PreviewRootCleaned=true`；
- 正式数据目录前后指纹一致，最终归档 `todo.json` 为 schema 3、`items=[]`。

结果序列化继续复用 `AotManagedUiSmokeJsonContext`，应用内仍只有一次 source-generated `JsonSerializer.Serialize` 调用。产品 JSON 固定清单保持 23 个文件、58/58 处调用和 21 个 context 所有者。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2B2B1 契约 | 17/17 通过 |
| 全部 AOT 相关测试 | 287/287 通过 |
| x64 .NET 全量测试 | 2274/2274 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | `publish-aot-audit.ps1`、`run-aot-managed-ui-smoke.ps1`、`start-aot-preview.ps1` 全部通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 41 / 38 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 83.3 MiB / 181.4 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| 本阶段缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish 哈希一致 |
| Todo 步骤三进程矩阵 | 创建、文本修改、完成、重载、恢复未完成、步骤删除、任务删除、postflight 全部通过；3/3 正常退出 |
| Todo 核心回归矩阵 | `TodoPersistenceRestart` 再次通过；3/3 正常退出 |

上述 UI 项是实际运行受审计 `DeskBox.exe` 的程序化产品路径证据，不是仅靠源码扫描。它仍不替代用户对物理键鼠、触控、IME、焦点、动画、视觉层级和目标系统差异的人工验收。

## 8. 复盘与遗漏检查

1. 步骤创建使用真实详情输入和产品 AddStep 路径，没有直接写模型或 store。
2. 文本、完成和删除使用实际实现的 DataTemplate 行控件，普通事件与自动化共用同一产品方法。
3. 完成状态跨越两个进程，并核对 store、ViewModel、DataContext、复选框和透明度，不只观察单层 JSON。
4. 步骤删除和任务删除分开取证；零步骤任务中间态明确通过，任务删除没有掩盖步骤清理结果。
5. 非空步骤 UI 需要的 `TodoStepViewModel` 生成绑定已加入；未经验证的 `TodoAttachmentViewModel` 仍未提前加入。
6. 强类型步骤集合与持久化格式保持不变，`object[]` 只存在于 WinRT `ItemsSource` 边界。
7. 两个 Todo 场景使用各自固定 store ID，共享取证方法不再硬编码 fixture；旧核心矩阵已实际回归。
8. 重启后的步骤行使用实际状态条件等待，不再依赖详情打开与 ItemsControl 布局之间的偶然时序。
9. 三个阶段使用同一受审计 EXE 的三个不同 PID，3/3 正常退出，证据先归档后清理 owned 根。
10. 正式数据指纹、JSON 固定清单、Rust ABI/能力/导出和普通 JIT 的 Rust 后端策略均未变化。
11. profile 41 / schema 38 的 runner、surface、产品路径、集合投影、manager、禁止范围、警告和源码稳定门禁全部通过。

当前没有发现阻断 5B-4B2B2B1 完成的代码遗漏。

## 9. 尚未证明的边界

- Todo 链接附件、托管附件、图片缩略图、SHA-256、非空附件 UI、物理删除和目录清理；
- Todo 日期、提醒、稍后提醒、重复任务、下一任务生成、排序、筛选、拖放和撤销；
- Todo 的物理键鼠/触控编辑、IME、焦点切换、动画和视觉样式；
- Quick Capture 的剪贴板监听、图片剪贴板、图片缓存、recent/pin/search、软删除恢复、导出和大内容边界；
- Glance 数据、owned 图片、轮播状态和重启恢复；
- 天气手动城市与展示设置的重启恢复，以及定位/网络刷新行为；
- Widget 创建、删除、禁用、可见性和多 Widget 关系变更；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体 UI 交互；
- 安装、覆盖升级、自动更新、卸载、回滚与 CRT 部署决策；
- ARM64、Store、WACK、签名和真实目标设备矩阵。

## 10. 下一阶段建议

下一阶段建议实施 **5B-4B2B2B2 Todo 托管附件生命周期**，复杂度高于本阶段，仍保持单独顺序门：

1. 使用 owned 文本或图片文件夹具，经产品 `AddAttachmentPathAsync(..., copyToManagedStorageOverride: true)` 导入受管副本；
2. 核对元数据、SHA-256、受管路径、物理文件存在性和非空附件 UI；
3. 在第二个新 AOT 进程重载相同附件状态；
4. 显式走 `DeleteAttachmentAsync`，同时验证 UI/store 条目删除和物理受管文件清理；
5. 再删除任务，并由第三个进程确认空 store、空附件目录和无残留文件；
6. 仅在非空 UI 实际需要时为 `TodoAttachmentViewModel` 增加生成绑定或窄 `object[]` 投影。

`DeleteItemAsync` 当前不直接承担受管附件物理文件删除，因此测试顺序必须先删除附件、证明物理清理，再删除任务。该批不与 Glance/天气、提醒/重复任务、Picker/Shell 或 Rust `SearchCore` 合并。完成并复盘后，再决定进入 5B-4B2C Glance/天气还是先补其他独立 Todo 状态矩阵。
