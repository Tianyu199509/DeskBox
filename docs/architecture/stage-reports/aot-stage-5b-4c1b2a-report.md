# DeskBox AOT 阶段 5B-4C1B2A 完成报告

- 日期：2026-08-22
- 状态：5B-4C1B2A 已完成到定义的 x64 Native AOT 实际运行边界
- 审计配置：profile 48 / schema 45
- 本阶段范围：owned Shell move/progress 调用链、真实 owner HWND、部分完成、取消语义、延迟返回、跨进程恢复、失败补偿

## 1. 本阶段结论

本阶段已经闭环 File Widget 的“移回桌面”产品路径。真实单选和多选菜单仍通过 `FileItemMenuBuilder`、`FileSurfaceContent`、`WidgetViewModel`、`OrganizerService` 和 `FileService` 进入 `SHFileOperationW`，不建立测试专用产品入口。

此前这条链虽然选择了 Shell progress 路径，但 File Widget 的窗口句柄没有穿过 ViewModel 和 Organizer，`SHFileOperationW` 最终收到的 owner 为 0。当前菜单动作以及拖出失败后的桌面回退都会把 `_hostWindowHandle` 一路传至 `SHFILEOPSTRUCT.hwnd`。两次实际矩阵捕获的四次调用 owner 均与真实 File Widget HWND 相同且非 0。

本阶段没有扩展 Rust。Shell move 是现有 Win32/Shell 边界，数据量主要位于文件系统和 Shell 进程，不存在本轮可量化的大型托管常驻或复制内存热点。把该路径改写成 Rust 不能直接降低 DeskBox 常驻内存，反而会增加新的 ABI、Shell 行为差分和恢复面。生产 Rust 模块因此继续保持 ABI 2、能力 511 和十个必需导出。

## 2. 实现范围

### 2.1 产品调用链

产品代码完成以下调整：

1. `FileSurfaceContent` 在菜单动作和桌面拖出回退中传入真实 `_hostWindowHandle`；
2. `WidgetViewModel.MoveItemBackToDesktopAsync` 与 `MoveItemsBackToDesktopAsync` 接受并继续传递 owner；
3. `OrganizerService` 把 owner 传给 `FileService.ExecuteTransferPlanAsync`；
4. `FileService.ExecuteShellMovePlanAsync` 保留原有 `SHFileOperationW`、15 秒生产恢复探针、完成结果扫描和晚到任务观察逻辑；
5. AOT fixture 的 desktop provider 只在精确场景、精确 phase、32 位小写 run ID 和隔离 preview root 同时成立时返回 owned `desktop-root`。普通 JIT、其他 AOT 场景和生产启动仍使用系统桌面路径。

### 2.2 AOT 分支矩阵

`Mutate` 进程通过四次真实菜单 Automation Invoke 顺序覆盖：

| 模式 | 计划数 | 完成数 | 菜单反馈 | FileService 结果 | 证据性质 |
| --- | ---: | ---: | --- | --- | --- |
| Real | 1 | 1 | Success | Returned | 实际调用 `SHFileOperationW` |
| Partial | 2 | 1 | Success | Returned | 严格 owned 的 AOT-only `AnyOperationsAborted` 等价分支 |
| Cancel | 1 | 0 | Info | Returned | 严格 owned 的 AOT-only 取消等价分支 |
| Late | 1 | 1 | Success | RecoveredPending | 文件已完成，产品先返回，受观察任务随后返回 |

部分完成后只把真正满足“目标存在且源不存在”的一项写入历史；取消分支产生 0 项历史；最终四条历史按最新优先顺序记录的 item count 为 `1, 0, 1, 1`。延迟分支还独立证明产品返回时 Shell task 尚未返回，随后在退出前完成受观察返回。

### 2.3 三进程和补偿

正常矩阵使用三个全新的 AOT 进程：

1. `Mutate` 执行四种菜单路径并持久化磁盘和历史状态；
2. `VerifyRestore` 在新进程先验证部分/取消/延迟结果和历史重新载入，再由 harness 把三个已移动文件按原路径恢复并清空 owned 历史；
3. `Postflight` 在第三个新进程验证完整基线、内容 SHA-256 和空历史。

任一正常阶段在安全闭环前失败时，runner 会再启动独立 `Compensate` AOT 进程。补偿只处理 run ID 对应的五个精确 owned 文件；若源和目标同时存在，只在内容哈希相同时删除 owned 目标副本；无法证明身份时保留 preview/recovery 根并报错。

## 3. 实际运行结果

完整矩阵连续运行两轮，run ID 分别为：

- `6b47e36dbef74d8a885eb8fdbb772031`
- `e44567d412e24a099acb80cd135d93e6`

两轮结果一致：

- 每轮 3/3 个不同 AOT 进程自然退出；
- 每轮四次菜单均经真实 `MenuFlyoutItemAutomationPeer` / `IInvokeProvider` 进入产品事件链；
- 第二轮真实 owner HWND 为 `13765490`，四次 Shell 调用均使用该 HWND；
- 调用模式为 `Real, Partial, Cancel, Late`；
- FileService 结果为 `Returned, Returned, Returned, RecoveredPending`；
- 完成数为 `1, 1, 0, 1`；
- 菜单反馈为 `Success, Success, Info, Success`；
- mutation 后历史 item count 为 `1, 0, 1, 1`；
- VerifyRestore 和 Postflight 的路径、长度及 SHA-256 与初始基线一致；
- 正式数据目录前后指纹均为 `66B2723312E2FA671895765D5C6DFC92C9719F801FD4C3EBA405174DC5E86886`；
- 运行错误日志为 0；
- 两轮 preview root 和 `-Recovery` sibling 均已验证 ownership 后清理；
- 审计发布目录没有残留运行中的 `DeskBox.exe`。

最新结构化证据位于 `.artifacts/aot-managed-ui-smoke/win-x64/shell-move-persistence-restart-e44567d412e24a099acb80cd135d93e6/`。这是本地审计产物，不作为仓库源文件。

## 4. AOT 发布审计

profile 48 / schema 45 的完整 x64 发布审计通过：

- 发布文件 39 个；
- 符号文件 3 个；
- 发布目录约 85.2 MiB；
- WMC1506 为 0；
- WMC1510 为 1211；
- `always-throw` 为 0；
- 原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- Rust ABI 2、能力 511、十个必需导出，staging/publish DLL 哈希一致；
- 新阶段源码没有新增 AOT 分析警告；
- Properties、Picker、physical drag/drop、`IFileOperation` 和新 Rust ABI 均未进入本阶段。

第一次 AOT 编译发现一个集合表达式缺少目标类型、菜单 probe 缺少 `WidgetStackItem` 命名空间，以及 owned desktop provider 的可空返回警告；修正后完整发布通过。第一次完整发布后的静态门禁还发现 launcher 版本文本仍冻结为 47/44，以及禁用词把 `DESKBOX_NATIVE_AOT` 误判为 Rust API；这两项属于审计脚本问题，修正后 profile 48 / schema 45 通过。

## 5. 证据边界

本阶段已经证明：

- 真实 File Widget 单选/多选菜单和 Automation Invoke；
- 真实产品 `SHFileOperationW` 正常移动；
- 真实 owner HWND 到 Shell API 的完整传递；
- FileService 对部分完成、0 完成和晚到返回的结果扫描、反馈、历史持久化及恢复；
- 独立预览根、生产数据指纹、三进程重启、内容哈希、补偿和清理。

本阶段没有声称证明：

- 人工鼠标点击或物理拖放；
- 大文件操作时系统 Shell 进度窗口在屏幕上的视觉表现；
- 用户在真实 Shell 对话框中手动按下取消；
- 系统 Properties 窗口的目标、owner 和关闭行为；
- File/Folder Picker 交互。

Partial、Cancel 和 Late 是 AOT-only、精确 owned、默认不可达的确定性分支，用来验证 DeskBox 在 Shell 返回这些结果时的产品行为；其中只有 Real 分支是本轮实际操作系统 Shell 调用。该区分保留在结构化证据和 runner 断言中，不能把确定性分支表述为物理用户操作证据。

## 6. 回归与复盘

最终回归结果在本报告收口时记录：

- 5B-4C1B2A 新合同：11/11；
- 全部 AOT 相关测试：383/383；
- x64 全量测试：2378/2378；
- Rust workspace：57/57；
- `cargo fmt --check`、`cargo clippy --all-targets -- -D warnings`、PowerShell 解析和 `git diff --check` 均通过；
- 规范 Debug 构建通过，只有既有警告。

代码复盘没有发现未受保护的测试后门。AOT-only hook 必须同时满足场景、phase、run ID、preview root、owned 双根、精确文件名、精确 source/destination 和非 0 owner；任何选择形状或路径不匹配都会拒绝执行。生产探针仍为 15 秒，只有精确 Late fixture 使用 150 ms；Rust ABI、JSON 产品格式和系统桌面默认解析均未改变。

## 7. 下一阶段建议

下一阶段建议进入 **5B-4C1B2B：系统 Properties 菜单与 owner**，不要和 Picker 或物理拖放合并。

该阶段应从真实 File Widget 单选菜单进入现有 Properties 产品路径，验证精确目标路径、非 0 owner、窗口出现和可控关闭，并保留“自动化证据”和“目标 Windows 人工视觉确认”的边界。它不发生文件内容变更，复杂度和恢复风险低于本阶段，适合作为下一个独立合并单元。

Properties 完成后再进入 **5B-4C1C：Picker 与物理拖放**。C1C 涉及系统交互、真实 pointer/drag event ordering 和人工验证，复杂度明显更高，应继续单独处理。本阶段没有出现需要改用 Rust 的常驻内存热点，因此下一阶段仍不扩展 Rust。
