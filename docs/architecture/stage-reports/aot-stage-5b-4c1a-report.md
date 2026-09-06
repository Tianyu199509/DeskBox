# DeskBox Native AOT 阶段 5B-4C1A 完成与复盘报告

- 审计日期：2026-08-22
- 范围：x64 NativeAOT、owned 本地文件树、真实 File Widget、目录导航、Name 升序、watcher、copy/move/rename、重名失败、三进程重载/恢复/postflight
- 平台：x64 / `win-x64`
- 结论：5B-4C1A 已完成。本结论不包含产品回收站、Shell 进度/系统对话框、Picker、物理拖放、快捷键、媒体 UI、真实 Weather 网络/定位、安装升级、ARM64、Store 或正式发布验证

## 1. 本阶段结论

5B-4C1A 在既有 managed UI runner 中增加 `LocalFileSurfacePersistenceRestart`，由同一份受审计 NativeAOT 产物依次启动三个全新的 DeskBox 进程：

1. `Mutate` 从固定 owned 文件树开始，进入并返回子目录，经产品 ViewModel 完成 copy、move、rename 和重名失败，再从产品外直接创建 owned 文件以刺激真实 watcher；
2. `VerifyRestore` 在新进程中确认变更后的磁盘、ViewModel 和真实 UI 全部重载，再由 harness 在精确 owned 根内恢复固定文件树，并等待产品 watcher 恢复表面；
3. `Postflight` 在第三个新进程中确认恢复后的文件树、File Widget 和真实 DataTemplate 前后保持一致。

最终矩阵证明：

- 三个 PID 互不相同，均由应用正常关闭路径自然退出；
- 三次启动使用同一受审计 EXE 及同一 SHA-256；
- File Widget 的 HWND、XamlRoot、DataContext、可见状态和非空活动视图真实存在；
- 基线 2 个条目、变更后 5 个条目均按活动列表原始顺序满足 Name 升序，`nested` 为文件夹，其余为文件；
- 每个条目都有已实现容器、真实 `FileItemSurface`、匹配 DataContext 和 `ItemNameText` 投影；
- 目录进入后导航栏可见且子文件真实投影，返回映射根后导航栏折叠；
- copy 保留源并生成同内容目标，move 删除源并生成同内容目标，rename 同步更新磁盘与条目路径；
- 重名 rename 抛出 `System.IO.IOException`，两个冲突文件及条目状态均保持不变；
- 外部 owned 文件创建和 harness 清理均只通过真实 watcher 进入 UI，没有调用手工 refresh；
- runner 与应用分别递归枚举 owned 文件，并逐文件比较相对路径、长度和 SHA-256；
- `Mutate.after == VerifyRestore.before`，恢复状态与 `Postflight` 前后逐字段一致；
- 运行时失败日志和延期路径日志均为 0，正式数据目录前后指纹一致，证据归档后 preview 根已清理。

本阶段没有扩展 Rust 产品边界。文件复制、移动和哈希受磁盘 I/O 主导，现有实现按流/逐项处理；WinUI、FileSystemWatcher 和 DataTemplate 生命周期也不能由 Rust 简化。生产 Rust 模块继续保持 ABI 2、能力 255 和九个必需导出。只有后续大目录/大文件基准证明枚举、哈希或路径规划存在显著托管内存峰值时，才把有测量证据的计算段收成粗粒度 Rust 边界。

## 2. 工作区保护

本轮继续在已有大规模脏工作区上窄范围开发，并沿用开始本阶段前建立的本地恢复检查点。实施期间没有执行 commit、push、pull、merge、rebase、stash、reset，也没有删除或合并无关用户变更。runner 只递归清理带专用 ownership marker、解析后位于 `.artifacts/aot-managed-ui-smoke/win-x64` 内且不等于 evidence 根本身的 preview 目录。

## 3. 实现结构

| 文件 | 职责 |
| --- | --- |
| `AotLocalFileSurfaceFixture.cs` | NativeAOT-only 精确场景/phase/Widget ID 门禁及 owned 路径解析 |
| `WidgetManager.AotLocalFileSurfaceSmoke.cs` | 从真实 File Widget session 获取产品 surface、ViewModel、HWND 和 XamlRoot |
| `FileSurfaceContent.AotLocalFileSmoke.cs` | 等待活动视图稳定，核对原始 UI 顺序、容器、DataContext、文件名投影和导航状态 |
| `WidgetItem.AotBindableProperties.cs` | 为非空文件条目提供 6 个精确 NativeAOT DataTemplate 属性 |
| `WidgetViewModel.AotBindableProperties.cs` | 为 File Widget 根布局提供 23 个精确 NativeAOT 绑定属性 |
| `FileItemSurface.AotBindableProperties.cs` | 为控件自身 6 个 ElementName 计算属性提供精确 NativeAOT 属性表 |
| `App.AotLocalFilePersistenceSmoke.cs` | 三阶段产品操作、磁盘 SHA-256、状态期望及结构化证据 |
| `App.AotManagedUiSmoke.cs` | 场景/phase 路由、source-generated JSON evidence 和正常关闭 |
| `run-aot-managed-ui-smoke.ps1` | 固定树播种、三进程等值、独立磁盘哈希、PID/EXE、日志、正式数据和 owned 清理门禁 |
| `publish-aot-audit.ps1` | profile 46 / schema 43 的源码、绑定、runner、延期范围、警告和 Rust 边界契约 |
| `AotStage5B4C1AContractTests.cs` | 12 条场景、夹具、产品操作、真实 surface、生成属性、runner、范围和审计契约 |

## 4. 设计边界

### 4.1 夹具不能成为普通产品路径

`AotLocalFileSurfaceFixture` 整体位于 `#if DESKBOX_NATIVE_AOT` 中。AOT 内还必须同时满足精确场景 `LocalFileSurfacePersistenceRestart`、三个允许 phase 之一以及固定 Widget ID `aot-5b4c1a-file`。路径必须位于隔离数据根下的 `fixtures/local-file-surface`，缺少目录或越过 owned 根立即失败。普通 JIT 二进制不包含该夹具。

### 4.2 产品操作与 harness 清理分开

Mutate 中的导航、copy、move、rename 和冲突失败全部调用现有 `WidgetViewModel` 产品入口。watcher 创建刺激来自 `File.WriteAllTextAsync`，故意不调用产品 refresh。

VerifyRestore 中的移动回源和删除变更文件属于 harness-owned 清理，只用于把固定树恢复为可重复基线，不能描述为“产品删除/回收站已验证”。产品回收站另设后续阶段。

### 4.3 三层证据不能相互替代

应用进程记录 File Widget 的 ViewModel、活动 `ListViewBase`、每个已实现容器、真实 `FileItemSurface`、DataContext、投影文字和递归磁盘哈希。外层 runner 在每个进程自然退出后再次独立枚举并哈希磁盘，再比较两个证据源。静态契约、普通 x64 测试和成功 AOT 发布均不能替代这条实际运行链。

### 4.4 隐藏控件只记录可观察状态

映射根的导航栏处于折叠状态。折叠控件内部可能保留上次展开时的 Text，也可能因新进程从未展开而为空；该缓存文本不是产品持久化状态。探针只在导航栏可见时记录真实文字，在折叠时规范为空，同时仍严格要求子目录阶段的导航栏可见且文字非空。

## 5. 实施与复盘中发现的问题

### 5.1 隐藏扩展名使真实 UI 文本与磁盘名不同

首次实际运行的磁盘树、HWND、XamlRoot、ViewModel 数量和容器均正确，但 runner 预期 `baseline.txt`，真实用户设置固定为 `showFileExtensions=false`，因此 UI 正确显示 `baseline`。最终把 UI 期望改为无扩展名，磁盘证据仍保留完整 `.txt` 相对路径和 SHA-256，没有混用两个层级。

### 5.2 折叠导航栏缓存文字不是跨进程状态

第一次完整三进程比较中，Mutate 曾进入并返回子目录，折叠后的 TextBlock 仍缓存 `widget-root`；新进程从映射根启动时同一折叠 TextBlock 尚未求值，文字为空。路径、导航可用性、可见性和文件树全部一致。最终按 4.4 的可观察语义规范证据，没有删除真实导航断言。

### 5.3 日志门禁发现 `FileItemSurface` NativeAOT 属性表遗漏

功能和恢复断言曾全部完成，但最终日志门禁捕获 `FileItemSurface` 的 `IconLayoutVisibility`、`ListLayoutVisibility`、`SurfaceHorizontalAlignment`、`SurfaceMaxWidth`、`SurfaceMargin` 和 `SurfacePadding` 六项 `ICustomProperty` `NotSupportedException`。新增一个只在 NativeAOT 编译、只列出这六项的生成属性提供器；最终运行时失败日志为 0。该问题属于 WinUI 绑定元数据，Rust 无法简化或提供这类 XAML 属性表。

### 5.4 完成后审计补齐真实排序和类型门禁

初版探针为集合比较先排序实际名称，因而只能证明成员相同，不能证明真实 UI 顺序；文件/文件夹类型也主要由导航间接证明。最终改为按活动列表原始顺序核对“文件夹优先、同类按 Name 升序”的产品规则，并显式要求只有 `nested` 是文件夹。最终运行还发现契约曾误写为纯 Name 升序，导致真实的 `nested, baseline` 被拒绝；修正的是测试期望，产品排序没有改动。该修正收紧了既定 C1A 范围，没有扩展到新的产品功能。

## 6. 结构化证据

最终成功证据位于：

```text
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/session.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/mutate-result.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/verify-restore-result.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/postflight-result.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/final-settings.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/final-fixture
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/disk-states.json
.artifacts/aot-managed-ui-smoke/win-x64/local-file-surface-persistence-restart/DeskBox.log
```

关键实测形状：

- 三个不同 PID，3/3 自然退出，三次 EXE 哈希相同；
- 真实 File Widget surface 为 `380 x 421` logical px；
- 基线 UI 为 `nested, baseline`，变更 UI 为 `nested, baseline, copied-renamed, move-source, watcher-created`，符合文件夹优先、同类 Name 升序；
- 基线磁盘 4 个文件，变更磁盘 6 个文件，所有文件长度大于 0 且 SHA-256 为 64 位十六进制；
- `RuntimeFailureLogCount=0`、`DeferredPathLogCount=0`；
- 正式数据目录前后指纹、文件数和总字节一致；
- `PreviewRootCleaned=true`，结束后受审计 AOT 进程数为 0。

PID、HWND、EXE 哈希和正式数据指纹属于本次本机运行证据，不应被当作跨机器固定常量。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4C1A 定向契约 | 12/12 通过 |
| 全部 AOT 相关测试 | 360/360 通过 |
| x64 .NET 全量测试 | 2355/2355 通过 |
| Rust workspace | 54/54 通过，其中生产 crate 52、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | runner、审计、启动器解析通过 |
| 审计 profile / schema | 46 / 43 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布目录 / 符号目录 | 84.4 MiB / 185.7 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| C1A 缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| JSON source-generated 清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| Rust ABI / 能力 / 必需导出 | 2 / 255 / 9，staging 与 publish SHA-256 一致 |
| File 三进程矩阵 | 通过，3/3 正常退出，失败日志 0，延期路径日志 0 |

`-RequireCleanAnalysis` 仍会因仓库已有的 CS0108、CS0169、CS0414、CS8601、CS8602 和精确冻结的 WMC1510 返回非零；`unexpectedWarningCodes` 为 0，C1A 自身源码告警为 0。这组结果不能描述为“全仓库零警告”。

## 8. 完成后审计与剩余边界

对文档、实现和最终证据再次复盘后，C1A 定义内的 owned 固定树、真实 File Widget、文件/文件夹类型、Name 升序、目录进入/返回、外部 watcher、copy/move/rename、冲突失败、磁盘/ViewModel/UI 三层一致、三进程重载、恢复、postflight、日志、正式数据保护和清理均有对应门禁；没有发现仍阻断 C1A 完成的遗漏。

仍需明确以下边界：

1. 没有执行产品 `DeleteItemsAsync` 或 `SHFileOperation` 回收站路径，harness 删除不能替代产品删除证据；
2. 没有进入 `useShellProgress:true`、Shell 系统进度/属性对话框、取消、延迟返回或补偿路径；
3. 没有打开/点击 File Widget 菜单，也没有验证系统 Properties 窗口的 owner；
4. 没有调用 FileOpenPicker、剪贴板 StorageItems、OLE/native drop 或物理 Explorer 拖放；
5. 本轮只验证固定小文件和单层子目录，不是大目录、大文件、跨卷、网络盘、符号链接/junction、权限、断连设备或长路径性能证明；
6. 自动化证明结构与文本，不替代图标视觉质量、动画、截断、DPI 和多显示器人工验收；
7. Quick Capture 图片/剪贴板扩展、Todo 提醒/重复、附件 Undo/孤立文件回收、Weather 网络/定位、安装升级、CRT、ARM64/Store 和 Rust `SearchCore` 仍未完成。

## 9. 下一阶段调整

下一阶段建议从原先宽泛的 C1B 再拆一层，先开放 **5B-4C1B1：owned 回收站删除、精确恢复与 File Widget 菜单路由**，暂不把 Shell 进度和系统 Properties 对话框合入同一轮。

建议的 C1B1 范围：

1. 为每轮生成全局唯一的 owned 文件/目录和可校验内容，删除前记录正式回收站相关状态，不清空或枚举处理无关用户项目；
2. 从真实 File Widget 菜单/产品 `DeleteItemsAsync` 进入回收站路径，核对磁盘消失、watcher/UI 移除、成功反馈和新进程稳定状态；
3. 只恢复本轮唯一标记的项目，逐项核对原路径、内容 SHA-256 和最终回收站残留为 0；任何失败都必须在 `finally` 或独立补偿进程恢复；
4. 同轮覆盖内部单选/多选菜单的创建、启用状态和动作路由，但系统 Properties 对话框只做后续独立矩阵；
5. 继续使用正式数据指纹、精确 EXE、自然退出、日志和 owned 清理门禁。

拆分原因是回收站属于全局 Shell 状态，虽然操作可恢复，但比 C1A 的 preview 内文件树风险更高；Shell 进度还涉及 owner HWND、系统 UI、取消、延迟返回和跨卷补偿，复杂度更高。先把可唯一定位、可恢复的回收站生命周期闭环，再进入 **5B-4C1B2：Shell move/progress、取消/延迟返回与 Properties owner**，之后才进入 **5B-4C1C：Picker 与真实拖放**。

C1B1 实施时正好触发了上述条件：产品删除继续保留窄 `SHFileOperationW` P/Invoke，而精确恢复若放在 C# 需要重建一整组继承复杂的 Shell Automation COM 接口，因此最终采用一个完整粗粒度 Rust 查询/恢复边界。该调整没有改写产品删除，也没有把 WinUI 或 ViewModel 迁到 Rust；完整结果见 `aot-stage-5b-4c1b1-report.md`。
