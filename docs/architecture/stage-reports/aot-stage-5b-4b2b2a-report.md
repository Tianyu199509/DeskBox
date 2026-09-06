# DeskBox Native AOT 阶段 5B-4B2B2A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT Todo 核心任务、标题、详情备注、完成状态、跨进程重载与删除后复核
- 平台：x64 / `win-x64`
- 结论：5B-4B2B2A 已完成；原定 5B-4B2B2B 再拆为 5B-4B2B2B1 Todo 步骤持久化和 5B-4B2B2B2 Todo 托管附件生命周期。本报告不代表 Todo 步骤/附件/提醒/重复任务、Glance、天气、OS 交互、安装升级、ARM64、Store 或正式发布已经通过

## 1. 本阶段结论

5B-4B2B2A 在现有 managed UI runner 中增加 `TodoPersistenceRestart`，使用同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从空 Todo store 打开真实详情新建入口，保存任务并修改标题；随后通过产品 600 ms timer 自动保存 Markdown 备注，将普通非重复任务设为已完成，然后正常退出；
2. `VerifyDelete` 在新进程中重载任务、标题、备注和完成状态，再通过显式备注保存路径更新正文，将任务恢复为未完成，最后经产品路径删除任务并正常退出；
3. `Postflight` 在第三个新进程中确认 schema 3 store、ViewModel、真实 Todo surface 和详情选择均为空，然后正常退出。

真实矩阵证明：

- 任务经 `OpenAddEditorAsync`、`DetailTitleTextBox` 和 `FinalizeDetailAsync` 创建，不是直接构造模型或 JSON；
- 标题修改经 `SaveDetailEditorsAsync` 保存，并在新进程中同时从 store、ViewModel 和详情 UI 重载；
- 备注写入 `MarkdownSourceEditor.Text`，通过与人工编辑事件共用的 `ScheduleNotesAutoSave` 启动原有 600 ms `DispatcherTimer`；runner 不直接调用 timer tick；
- 自动保存完成同时要求 timer 停止、`SemaphoreSlim` save gate 释放、编辑项 ID 保持、原始备注前进、编辑器文本和 Item 备注一致；
- 第二个进程通过 `SaveActiveNotesAsync(keepEditing: false)` 覆盖显式保存，并把备注更新为另一份固定文本；
- 完成状态通过 `SetCompletedWithFeedbackAsync` 执行 `false → true → 跨进程重载 true → false`，并同时核对 `CompletedAt` 的存在与清除；
- 删除经 `DeleteItemAsync` 完成；第二阶段退出前和第三阶段启动后均确认 item 数为 0；
- 三个进程均由应用正常关闭路径退出，没有依靠外层脚本强制结束；
- 最终 owned preview 根已清理，正式 `%LOCALAPPDATA%\DeskBox` 前后指纹一致，运行日志失败数为 0。

本阶段没有改写或扩展 Rust 产品边界。Todo 的 UI、ViewModel、JSON store、保存门和状态机继续保留在 C#。它们与 WinUI 编辑状态和用户数据事务紧密耦合，迁移 Rust 会增加跨语言状态同步，并不比现有实现简单。

## 2. 范围与安全边界

外层入口为：

```text
scripts/run-aot-managed-ui-smoke.ps1 -Scenario TodoPersistenceRestart
```

应用内场景和阶段变量为：

```text
DESKBOX_AOT_MANAGED_UI_SMOKE=TodoPersistenceRestart
DESKBOX_AOT_MANAGED_UI_TODO_PHASE=Mutate|VerifyDelete|Postflight
.artifacts/aot-managed-ui-smoke/win-x64/preview-root
.deskbox-aot-managed-ui-owned.json
```

安全边界如下：

- DataRoot 必须位于专属 artifact 根下，并带脚本创建且仓库路径匹配的所有权标记；
- 只使用 ID 为 `aot-5b4a-search` 和 `aot-5b4b2b2a-todo` 的两个固定 Widget；
- Search Widget 是未修改对照项，Todo Widget 是唯一内容变更目标；
- 只停止可执行文件完整路径等于受审计 AOT 产物的进程；
- 每个阶段都要求 NativeAOT、正确 EXE、正确 DataRoot、正确 PID、真实 HWND、XamlRoot 和独立结构化结果；
- 正式数据目录在矩阵前后比较确定性指纹、文件数和字节数；
- 只有确认三个进程均已退出且证据已归档后，才删除带所有权标记的 preview 根；
- 本轮不写 Quick Capture、Glance 或天气内容 store，不创建或删除 Widget；
- 本轮固定普通无 recurrence 任务，不进入提醒、日期、重复任务或自动生成下一任务分支；
- 本轮不创建步骤、不导入附件，也不触发 Picker、Shell、拖放、快捷键、媒体 setter、网络或定位。

## 3. 三进程状态矩阵

| 阶段 | 进程启动时 | 产品路径操作 | 退出前状态 |
| --- | --- | --- | --- |
| `Mutate` | schema 3 空 store | 新建任务、修改标题、等待备注 600 ms 自动保存、设为已完成 | 1 个普通任务；固定标题和自动保存备注；`IsCompleted=true`、`CompletedAt` 存在 |
| `VerifyDelete` | 重载上述完整状态 | 显式保存另一份备注、恢复未完成、删除任务 | 0 个任务；详情和列表均为空 |
| `Postflight` | 重载删除后状态 | 只读核对 store、surface 和详情 | 保持全空 |

外层脚本执行以下关键比较：

```text
Mutate.after == VerifyDelete.before
VerifyDelete.after == Postflight.before
Postflight.before == Postflight.after
```

比较范围包含 store schema 与存在性、任务 ID/标题/备注/完成状态/完成时间、重要标记、日期、重复规则、步骤数、附件数、提醒偏移、排序和时间戳，以及 surface 初始化、XamlRoot、列表数量、可见数量、详情选择、编辑状态、timer 和 save gate。它不是只读取 `todo.json` 的单个字段。

## 4. 实现结构

| 文件 | 职责 |
| --- | --- |
| `App.AotTodoPersistenceSmoke.cs` | 新场景三阶段调度、真实 store/UI 状态取证、固定文本、范围断言与正常关闭 |
| `TodoWidgetContent.AotPersistenceSmoke.cs` | 经真实详情 UI、标题保存、600 ms 备注自动保存、显式保存、完成切换和任务删除路径执行矩阵 |
| `WidgetManager.AotTodoPersistenceSmoke.cs` | 获取固定 Todo 的真实 content window、adapter、surface、可见性、HWND 和 XamlRoot |
| `TodoViewModels.AotBindableProperties.cs` | 仅在 NativeAOT 中为本阶段实际 DataContext 类型生成属性访问；步骤和附件类型继续延期 |
| `TodoWidgetContent.DetailNotesAndSteps.cs` | 提取 `ScheduleNotesAutoSave`，使真实编辑事件与 AOT 场景共用 timer 安排入口 |
| `App.AotManagedUiSmoke.cs` | 注册 Todo 场景并继续复用唯一 source-generated 结果写入入口 |
| `run-aot-managed-ui-smoke.ps1` | 建立 owned 根、启动三个新 AOT 进程、跨进程等值比较、日志/正式数据/清理门禁和证据归档 |
| `start-aot-preview.ps1` | 接受 profile 40 / schema 37，并记录应用内正常早退是否完成 |
| `publish-aot-audit.ps1` | profile 40 / schema 37 的 B2B2A 源码、runner、surface、产品路径、manager、禁止范围、JSON、告警、Rust 与产物门禁 |
| `AotStage5B4B2B2AContractTests.cs` | 本阶段 15 条产品路径、绑定投影、三进程、证据、清理、禁止范围和审计契约 |

`ScheduleNotesAutoSave` 只提取原 `EditorTextChanged` handler 已有的 timer 安排代码。普通用户输入仍经原事件进入同一方法；AOT 场景写入控件公开 `Text` 属性后调用该入口，再等待真实 600 ms timer 和产品保存方法完成。没有新增旁路 store 写入。

## 5. 真实 AOT 运行发现并修复的问题

### 5.1 Todo DataContext 缺少 AOT 属性提供器

第一次真实运行时，运行日志出现 `ICustomProperty support used by XAML binding` 的 `NotSupportedException`，目标类型为 `TodoWidgetViewModel`。静态编译能够完成，但详情 XAML 在 AOT 运行时无法解析 `DetailBackText`、`DetailSaveText`、`TextSize` 等绑定属性。

最终增加 NativeAOT-only 生成绑定声明：

```text
TodoWidgetViewModel
TodoItemViewModel
```

本轮没有提前加入 `TodoStepViewModel` 或 `TodoAttachmentViewModel`。这两类只有在非空步骤和附件集合进入下一阶段 UI 时才需要验证，先添加会把未经实际运行证明的范围混入当前完成门禁。

### 5.2 自动化注入使用了控件内部 TextBox

初版场景直接设置 `DetailNotesEditor.SourceTextBox.Text`。AOT 运行中 timer 能启动并停止，但 `MarkdownSourceEditor.Text` 没有得到预期文本，最终保存的仍是旧值。问题位于场景注入方式，没有证据表明用户物理键入或 Todo store 本身失败。

最终改为写入 `MarkdownSourceEditor` 的公开 `Text` 数据契约，并通过 `ScheduleNotesAutoSave` 安排原 timer。再次发布后，自动保存备注、Item 状态和 store 文件同时前进，三进程矩阵完整通过。

## 6. 结构化证据

成功运行归档：

```text
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/verify-delete-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/final-todo.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/todo-persistence-restart/DeskBox.log
```

本次实测数据：

- PID：`34220`、`30844`、`2856`，三个 PID 均不同；
- `ProcessCount=3`、`NaturalExitCount=3`、`PreviewRootCleaned=true`、`Running=false`；
- mutate 的 `AutoSaveObserved=true`，标题为 `AOT Todo persisted edited title`，备注为 `AOT Todo real 600 ms auto-save notes`；
- mutate 退出时 `IsCompleted=true` 且 `CompletedAt` 存在；
- verify-delete 的 `ExplicitNotesSaved=true`、`CompletionRoundTripObserved=true`，显式保存备注为 `AOT Todo explicit restart save notes`；
- 删除后和 postflight 的 item 数均为 0；最终 `todo.json` 为 schema 3、`items=[]`；
- `RuntimeFailureLogCount=0`，同一受审计 EXE 的残留进程数为 0；
- 正式数据指纹前后一致。

结果序列化继续复用 `AotManagedUiSmokeJsonContext`，应用内仍只有一次 `JsonSerializer.Serialize` 调用。产品 JSON 固定清单保持 23 个文件、58/58 处 source-generated 调用和 21 个 context 所有者，Todo store 仍为 schema 3。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4B2B2A 契约 | 15/15 通过 |
| 全部 AOT 相关测试 | 270/270 通过 |
| x64 .NET 全量测试 | 2257/2257 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | `publish-aot-audit.ps1`、`run-aot-managed-ui-smoke.ps1`、`start-aot-preview.ps1` 全部通过解析 |
| JSON 固定清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| 审计 profile / schema | 40 / 37 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布 / PDB 体积 | 83.2 MiB / 180.8 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| 非预期告警 | 0 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9 |
| Rust staging / publish 哈希 | 一致 |
| 审计期间源码稳定 | `true` |
| Todo 三进程矩阵 | 创建、标题修改、600 ms 备注自动保存、完成、重载、显式备注保存、恢复未完成、删除、postflight 全部通过；3/3 正常退出 |

上述 UI 项是实际运行受审计 `DeskBox.exe` 的程序化产品路径证据，不是仅靠源码扫描。它仍不替代用户对物理键鼠输入、视觉层级、焦点、动画、IME 和目标系统差异的人工验收。

## 8. 复盘与遗漏检查

本轮完成后再次核对规划、代码、脚本、结构化结果、日志和现有测试，结论如下：

1. 新建任务和修改已有任务是两个不同保存边界，已分别通过 `FinalizeDetailAsync` 与 `SaveDetailEditorsAsync` 覆盖。
2. 自动保存与显式保存是两个不同备注边界；前者等待 timer、save gate、编辑器、Item 和 store 一致，后者单独走退出编辑保存。
3. 完成与恢复未完成跨越两个进程，并同时核对 `IsCompleted` 与 `CompletedAt`，没有只看复选框状态。
4. 重载同时核对 store、ViewModel、真实 surface 和详情选择，不把单层 JSON 读取当作完整 UI 证据。
5. 三个阶段使用同一受审计 EXE，但分别为全新 PID；3/3 均正常退出，最终没有 AOT 进程残留。
6. 证据在删除 owned preview 根前完成归档；正式数据根前后指纹一致。
7. Todo 核心的两个实际 DataContext 已增加 AOT 生成属性提供器；步骤和附件类型没有越界提前加入。
8. 新增源文件、runner、launcher、项目编译项和审计门禁均进入 profile 40；B2B2A 缺失模式、禁止范围、目标源码告警和非预期告警均为 0。
9. 新场景仍使用唯一 source-generated writer；产品 JSON 清单、Rust ABI、能力、导出和普通 JIT 默认后端没有变化。
10. 最终 schema 3 store 明确为 `items=[]`，第二进程删除后的空状态又由第三个新进程复核。

当前没有发现阻断 5B-4B2B2A 完成的代码遗漏。

## 9. 尚未证明的边界

- Todo 步骤的创建、修改、完成、删除、非空 UI 投影和跨进程恢复；
- Todo 链接附件、托管附件、图片缩略图、哈希、非空 UI 投影、物理删除和目录清理；
- Todo 日期、提醒、稍后提醒、重复任务、下一任务生成、排序、筛选、拖放和撤销；
- Todo 的物理键鼠/触控编辑、IME、焦点切换、动画和视觉样式；
- Quick Capture 的剪贴板监听、图片剪贴板、图片缓存、recent/pin/search、软删除恢复、导出和大内容边界；
- Glance 数据、owned 图片、轮播状态和重启恢复；
- 天气手动城市与展示设置的重启恢复，以及定位/网络刷新行为；
- Widget 创建、删除、禁用、可见性和多 Widget 关系变更；
- 文件拖放、复制移动、跨卷、回收站、上下文菜单、快捷键、Picker、Shell 和媒体 UI 交互；
- 安装、覆盖升级、自动更新、卸载、回滚与 CRT 部署决策；
- ARM64、Store、WACK、签名和真实目标设备矩阵。

## 10. 下一阶段调整

原计划把 Todo 步骤和托管附件合为 5B-4B2B2B。代码复盘后，建议再拆成两个顺序门：

1. **5B-4B2B2B1 Todo 步骤持久化**：下一轮开放。固定普通任务，经真实详情 surface 验证步骤创建、步骤文本修改、完成/恢复、跨进程重载、步骤删除、任务删除和空 store postflight。该批需要实际验证非空 `ObservableCollection<TodoStepViewModel>` 的 AOT UI 投影，并只在真实需要时为 `TodoStepViewModel` 增加生成绑定。复杂度中等。
2. **5B-4B2B2B2 Todo 托管附件生命周期**：B2B2B1 通过后开放。使用 owned 文件夹具和 `AddAttachmentPathAsync(..., copyToManagedStorageOverride: true)`，验证导入、SHA-256、非空附件 UI、跨进程重载、`DeleteAttachmentAsync`、物理文件删除、任务删除和空目录 postflight。该批可能需要处理 `TodoAttachmentViewModel` 或 typed `ItemsSource` 的 AOT 投影，复杂度高。

不合并的依据：

- 步骤是 Todo store 内的纯集合状态，不依赖文件系统、Picker、Shell 或哈希；
- 附件同时包含元数据、受管副本、物理文件和清理顺序，失败后还要区分“store 已删但文件未删”和“文件已删但 UI 未刷新”；
- 步骤模板当前使用无 `x:DataType` 的运行时 Binding，附件控件使用 typed `x:Bind`，两者的 AOT 投影风险不同；
- 本阶段只证明 `TodoWidgetViewModel` 和 `TodoItemViewModel`。把 Step 与 Attachment 的生成绑定和集合修复同时加入，会失去按实际失败收窄边界的能力；
- 两个矩阵可以复用现有三进程、owned root、日志、正式数据指纹和自然退出框架，不需要扩展 Rust。

因此下一步建议只实施 5B-4B2B2B1。步骤完成并复盘后，再开放 5B-4B2B2B2；随后进入 5B-4B2C Glance/天气。OS 交互、安装升级、CRT、ARM64/Store 与 Rust `SearchCore` 继续后置。
