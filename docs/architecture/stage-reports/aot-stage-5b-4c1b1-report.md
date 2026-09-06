# DeskBox Native AOT 阶段 5B-4C1B1 完成与复盘报告

- 审计日期：2026-08-22
- 范围：owned 回收站删除、唯一身份查询、精确恢复、File Widget 单选/多选菜单真实路由与三进程恢复
- 平台：x64 / `win-x64`
- 结论：5B-4C1B1 已完成。本结论不包含通用回收站管理、清空回收站、Shell move/progress、系统 Properties、Picker、剪贴板 StorageItems 或物理拖放

## 1. 本阶段结论

5B-4C1B1 在既有 managed UI runner 中增加 `RecycleBinMenuPersistenceRestart`，只操作每轮由 32 位小写十六进制 run ID 标识的三个 owned 项目。最终矩阵使用同一份受审计 NativeAOT 产物依次启动三个全新 DeskBox 进程：

1. `Mutate` 加载真实 File Widget，经真实单选和多选 `MenuFlyout` 的删除菜单项进入产品删除链；
2. `VerifyRestore` 在新进程中确认三个原路径均消失、回收站中各有且仅有一个精确匹配，再通过 Rust 边界逐项恢复并核对长度与 SHA-256；
3. `Postflight` 在第三个新进程中确认三个项目仍位于原路径、内容未变，回收站精确匹配均为 0。

最终证明了真实菜单、产品删除、跨进程观察、唯一匹配恢复、失败补偿、正式数据保护和 owned 根清理的闭环。没有增加面向用户的“恢复”功能，也没有把产品删除改写为 Rust。

## 2. Rust 与 C# 的边界选择

产品删除保持现有窄链路：

```text
FileItemMenuBuilder
  -> FileSurfaceContent.DeleteItemsAsync
  -> WidgetViewModel.DeleteItemsAsync
  -> FileService.DeleteEntryAsync(recycle: true)
  -> SHFileOperationW(FO_DELETE, FOF_ALLOWUNDO)
```

该路径是小数据量同步 P/Invoke，没有需要通过 Rust 降低的托管常驻内存，也没有 Native AOT COM 阻断。因此保留 C# 可以维持普通 JIT 和 AOT 的同一产品行为。

精确恢复需要 Shell 回收站 namespace、`FolderItem`/`FolderItem2`、`VARIANT`、来源目录属性及 `undelete` verb。若继续在 C# 中实现，需要重建一组带继承和自动化类型的 Shell COM 接口。本阶段按“完整 Rust 边界明显更简单时直接使用 Rust”的原则，增加单个粗粒度 `deskbox_recycle_bin_v1` 导出，只返回阶段 HRESULT、匹配数和恢复数。生产模块保持 ABI 2，能力从 255 扩为 511，必需导出从 9 个扩为 10 个。

## 3. 精确身份与安全门禁

恢复身份由删除前的完整父目录和项目名组成。Rust 使用 Shell namespace CSIDL 10 完整枚举回收站：

- 名称读取自 `FolderItem.Name`；
- 原目录读取自 `FolderItem2.ExtendedProperty("System.Recycle.DeletedFrom")`；
- 两侧路径均经 `GetFullPathNameW` 规范化，再以 ordinal ignore-case 比较；
- `QUERY` 返回完整枚举后的精确匹配数；
- `RESTORE` 只有在完整枚举结束且 `matched_count == 1` 时才调用 `InvokeVerb("undelete")`；
- 0 个匹配、多个匹配、同名候选属性读取失败或路径规范化失败都不会触发恢复。

边界不会解析私有 `$I`/`$R` 数据，不直接访问 `$Recycle.Bin`，不调用 `SHEmptyRecycleBin`，不处理非匹配项目。runner 同时拒绝正式数据根、重叠路径、已有 preview/recovery 根、非法 run ID 和缺少 ownership marker 的清理目标。

## 4. 实现结构

| 文件 | 职责 |
| --- | --- |
| `FileSurfaceContent.AotRecycleBinSmoke.cs` | 从真实单选/多选菜单创建路径查找删除项，以 `MenuFlyoutItemAutomationPeer` 调用真实动作并记录产品反馈 |
| `App.AotRecycleBinSmoke.cs` | 四阶段场景、产品删除结果、精确查询/恢复、文件长度与 SHA-256 证据 |
| `AotRecycleBinFixture.cs` | 精确场景、phase、run ID 与 owned 路径门禁 |
| `WidgetManager.AotLocalFileSurfaceSmoke.cs` | 复用真实 File Widget 宿主定位与 surface 等待 |
| `RecycleBinNativeBackend.cs` | ABI/能力/导出加载、输入校验和防御性结果验证 |
| `recycle_bin.rs` | Shell 回收站完整枚举、精确身份比较和唯一恢复 |
| `deskbox_native.h` | C ABI、能力位、结构尺寸、阶段位及导出声明 |
| `run-aot-managed-ui-smoke.ps1` | 唯一根、三进程矩阵、独立补偿、正式数据指纹和双 owned 根清理 |
| `publish-aot-audit.ps1` | profile 47 / schema 44 的源码、范围、ABI、恢复顺序和警告门禁 |
| `AotStage5B4C1B1ContractTests.cs` | 产品链、菜单、fixture、Rust/托管 ABI、runner、补偿和延期范围契约 |

详细 ABI 见 `recycle-bin-native-abi-v1.md`。

## 5. 实施与复盘中发现的问题

### 5.1 初版恢复在首个匹配后提前执行

首次代码复盘发现，Rust 初版在遇到第一个匹配项时就调用恢复并退出枚举。这能恢复正常唯一项，但无法在同一次调用中证明不存在第二个同身份项目。实现已改为保留第一个候选、完成全量枚举、确认匹配数严格为 1 后再调用 `undelete`。多个匹配现在返回 `E_UNEXPECTED`，且不会修改任何项目。

### 5.2 公共 C 头文件未同步新边界

Rust、托管加载器和构建脚本已经扩展到能力 511，但复盘发现 `deskbox_native.h` 尚缺少回收站结构、常量、尺寸断言和导出声明。现已补齐 80 字节请求、104 字节结果以及 C/C++ 静态断言，避免只由 Rust/C# 私有布局维持 ABI。

### 5.3 首次真实运行触发了补偿路径

首次真实 AOT 运行中，产品菜单删除已经成功，三个 owned 项目也都在回收站中各有一个精确匹配，但外层 PowerShell 对单元素结果直接访问 `.Count`，导致 validator 失败。runner 随后启动独立 `Compensate` AOT 进程，逐项完成 `Query 1 -> Restore 1/1`，最终三个原路径全部恢复且精确匹配残留均为 0。

修正内容包括把单元素结果显式数组化，以及把自动派生的 `-Recovery` sibling 纳入路径重叠检查、ownership marker、成功清理和 session 证据。此后两个全新 run ID 的完整三进程矩阵连续通过。

失败运行的 preview 与 recovery 诊断目录仍保留在 `.artifacts`，因为本次工具策略拒绝直接递归删除；目录内 owned 项目已经恢复，不代表回收站仍有遗留。后续成功运行的两组 preview/recovery 根均由 runner 自身验证后清理。

## 6. 实际 AOT 证据

最终稳定运行证据：

```text
.artifacts/aot-managed-ui-smoke/win-x64/recycle-bin-menu-persistence-restart-22c11de3c32f4641afb4cf57eef5e68c/session.json
```

关键实测值：

- 三个 PID 为 12936、42232、19612，互不相同且 3/3 自然退出；
- 三次 EXE SHA-256 均为 `A3553790D7C43962ACDD3D93C6D0E33ABC9184113240248829085137AD01EA33`；
- 单选菜单 13 项，删除项索引 12；多选菜单 9 项，删除项索引 8；两次均由真实 automation invoke 触发；
- 产品删除后，单文件、多选文件和含非空 payload 的文件夹在原路径消失，回收站精确匹配均为 1；
- 三次 Rust 恢复均为 `matched_count=1`、`restored_count=1`；
- Postflight 三项精确匹配均为 0，原路径、长度和 SHA-256 与删除前一致；
- 正式数据指纹前后一致，runtime failure 日志为 0；
- `PreviewRootCleaned=true`、`RecoveryRootCleaned=true`，结束后受审计 AOT 进程数为 0。

另一个全新 run ID `e083318578734ad695259ba383b73e8c` 也完成相同矩阵。PID、哈希和正式数据指纹属于本机本次产物证据，不应被当作跨机器固定常量。

## 7. 验证结果

| 验证 | 结果 |
| --- | --- |
| 5B-4C1B1 定向契约 | 12/12 通过 |
| 全部 AOT 相关测试 | 372/372 通过 |
| x64 .NET 全量测试 | 2367/2367 通过 |
| Rust workspace | 57/57 通过，其中生产 crate 55、测试夹具 2 |
| Rust fmt / Clippy | 通过；Clippy 使用 `-D warnings` |
| PowerShell 语法 | build、runner、审计、启动器解析通过 |
| 审计 profile / schema | 47 / 44 |
| 发布文件 / 分离 PDB | 39 / 3 |
| 发布目录 / 符号目录 | 84.8 MiB / 187.2 MiB |
| WMC1506 / WMC1510 | 0 / 1211 |
| 完整 `always-throw` | 0 |
| 原始 IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 全部 0 |
| C1B1 缺失模式 / 禁止范围 / 目标源码告警 | 0 / 0 / 0 |
| JSON source-generated 清单 | 23 个文件、58/58 处调用、21 个 context 所有者 |
| Rust ABI / 能力 / 必需导出 | 2 / 511 / 10，staging 与 publish SHA-256 一致 |
| Recycle Bin 三进程矩阵 | 连续两轮通过；每轮 3/3 正常退出、恢复 3/3、最终精确残留 0 |
| C1A 回归矩阵 | `LocalFileSurfacePersistenceRestart` 通过，3/3 正常退出 |

`-RequireCleanAnalysis` 仍会因仓库已有的 CS0108、CS0169、CS0414、CS8601、CS8602 和精确冻结的 WMC1510 返回非零；`unexpectedWarningCodes` 为 0，C1B1 自身源码告警为 0。这组结果不能描述为“全仓库零警告”。

## 8. 完成后审计与剩余边界

对文档、实现和真实运行证据再次复盘后，C1B1 定义内的真实单选/多选菜单、产品删除调用链、watcher/UI 移除、唯一身份、完整枚举、精确恢复、内容哈希、三进程重载、独立补偿、正式数据保护、日志和 owned 清理均有对应门禁；没有发现仍阻断 5B-4C1B1 完成的遗漏。

仍需明确以下边界：

1. Rust 导出是内部精确恢复与验证边界，不是面向用户的通用恢复功能；
2. 没有清空、遍历处理或修改任何非本轮 owned 回收站项目；
3. 没有进入 `useShellProgress:true` 的 Shell move、系统进度 UI、取消、延迟返回或跨进程补偿；
4. 没有打开系统 Properties 窗口，也未验证 `SHObjectProperties` owner；
5. 没有调用 Picker、剪贴板 StorageItems、OLE/native drop 或物理 Explorer 拖放；
6. 自动化未覆盖跨卷、网络盘、长路径、权限失败、超大文件/目录或用户在运行期间手工制造同身份回收站项；
7. Quick Capture 图片/剪贴板扩展、Todo 提醒/重复、附件 Undo/孤立文件回收、Weather 网络/定位、安装升级、CRT、ARM64/Store 和 Rust `SearchCore` 仍未完成。

## 9. 下一阶段调整

原 5B-4C1B2 同时包含有数据变更的 Shell move/progress 和只显示系统窗口的 Properties。完成 C1B1 后建议继续拆成两个独立验收批次：

1. **5B-4C1B2A：owned Shell move/progress、owner、取消、延迟返回与补偿。** 使用 owned 源/目标和可校验哈希，从真实产品导入路径进入 `useShellProgress:true`，分别证明正常完成、用户取消/部分完成、文件系统已完成但 `SHFileOperationW` 延迟返回、晚到任务观察、跨进程恢复和正式数据隔离。该批复杂度高，优先处理，因为它包含真实数据变更和现有异步恢复分支；不在没有实测收益的情况下改写为 Rust。
2. **5B-4C1B2B：系统 Properties 菜单与 owner。** 从真实 File Widget 菜单调用 `SHObjectProperties`，核对目标路径、非零且属于目标 Widget 的 owner、窗口出现/关闭和进程自然退出。该批数据风险低但系统 UI 自动化边界不同，单独验收更容易判断结果。

B2A 完成后再开放 B2B，随后进入 **5B-4C1C：Picker、剪贴板 StorageItems 与真实拖放**。如果 B2A 审计发现现有 `SHFileOperationW` 的取消、延迟返回或补偿需要在 C# 重建复杂 COM 状态机，再按本轮相同原则评估完整粗粒度 Rust/Shell 边界；没有这种证据时继续保留现有 C# 产品路径。
