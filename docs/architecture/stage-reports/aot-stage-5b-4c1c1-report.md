# DeskBox AOT 阶段 5B-4C1C1 完成报告

- 日期：2026-08-23
- 状态：5B-4C1C1 已完成到定义的 x64 Native AOT 实际运行边界
- 审计配置：profile 50 / schema 47
- 本阶段范围：现代文件选择器、精确 owner、选择/取消、文件与文件夹 `StorageItems`、隔离导入、跨进程重载与恢复

## 1. 结论

本阶段已经闭环 File Widget 的 Picker 与 Clipboard StorageItems 产品路径。文件选择改为 Windows App SDK 现代 `FileOpenPicker(WindowId)`，直接使用所属 File Widget 的 `_hostWindowHandle` 生成 `WindowId`；服务拒绝零句柄和失效句柄，不再依赖 `InitializeWithWindow`、前台窗口或祖先窗口推测。

剪贴板处理拆成“全局剪贴板传输”和“可复用 `DataPackageView` 解析”两层。普通产品入口仍从全局剪贴板取得内容，并保留 Shell file-drop 回退；本阶段 AOT probe 使用真实 `StorageFile`、`StorageFolder` 和 `DataPackage.SetStorageItems` 构造视图，再进入与产品相同的 `StorageItems` 解析、路径归一化、导入和进度路径。产品行为、持久化格式和已有 Shell 回退没有改变。

本阶段没有扩展 Rust。Picker、WinRT `DataPackageView` 和 WinUI surface 都是 UI/异步生命周期边界，没有出现可量化的大型托管常驻、复制缓冲热点或 AOT 硬阻断。把它们迁入 Rust 会扩大 FFI、回调和 owner 生命周期复杂度，不能证明可降低内存；生产模块继续保持 ABI 2、能力 511 和十个必需导出。

## 2. 实现边界

`FileOpenPickerService` 接受精确 owner HWND 和可选建议目录，验证窗口仍有效后调用 `GetWindowIdFromWindow`，再创建现代 picker。File Widget 的普通“添加文件”产品入口和 AOT probe 共用该服务及同一个导入方法，不存在仅供测试使用的替代 picker。

剪贴板入口保留以下语义：

1. 普通粘贴从 `Clipboard.GetContent` 读取全局数据，并优先解析 `StandardDataFormats.StorageItems`；
2. 只有普通入口允许继续尝试既有 Shell file-drop 回退；
3. AOT probe 对真实 `DataPackageView` 调用同一个解析方法，但显式关闭 Shell fallback，以便证据只归因于 `StorageItems`；
4. 文件和文件夹最终都转换为规范化路径，并进入 `ImportPathsWithTrackedProgressAsync`；
5. 取消 picker 时 surface 不变，选择和 StorageItems 导入后按产品排序语义持久化，随后在新进程中重载、恢复和 postflight。

## 3. 系统窗口观察与 runner 修正

runner 分别驱动一次取消和一次选择。应用在打开 picker 前冻结可见顶层窗口基线，只接受同一 AOT 进程中新出现的可见 `#32770`，并记录窗口 HWND、线程、进程、类名、标题、direct owner、root owner 和完整 owner chain。两次窗口的 owner chain 都包含精确 File Widget HWND。

首次实现只用 UI Automation 的根子项查找公共对话框，但当前系统不会把该窗口暴露为 DeskBox UIA 根的子元素。最终先用 Win32 枚举精确定位同进程可见 `#32770`，再由 `AutomationElement.FromHandle` 进入 UIA。当前 Windows 上文件名输入框和按钮也没有暴露可用的 Value/Invoke pattern，因此 runner 先尝试标准 UIA pattern，再对精确 AutomationId `1148`、`1`、`2` 的真实 Win32 控件使用 `WM_SETTEXT` 或 `BM_CLICK`，并把实际方法写入证据。

真实运行还发现 picker 关闭后 Windows 可能很快复用同一个 HWND。仅用 `IsWindow(hwnd)` 会把新窗口误认作旧窗口仍未销毁；最终销毁判断同时冻结并核对窗口线程、进程、direct owner 和类名，避免句柄复用造成假失败。

## 4. 实际运行结果

最终形态已有两个独立 run ID 的完整三进程矩阵通过：

| run ID | Mutate / VerifyRestore / Postflight PID | 取消对话框 HWND | 选择对话框 HWND |
| --- | --- | ---: | ---: |
| `36120dec1c4545ecae69244e49ce20e7` | `19288 / 41540 / 30476` | 3149142 | 11274982 |
| `53654387061841fdaec1cc73468a728b` | `24848 / 32040 / 4168` | 18682856 | 21172874 |

两轮共同结果：

- 两个系统窗口均为同一 DeskBox AOT 进程的真实可见 `#32770`，标题为“打开”；
- direct owner 与完整 owner chain 均指向精确 File Widget HWND；
- 取消使用真实控件 `BM_CLICK`，surface 与物理目录均无变化；
- 选择使用真实控件 `WM_SETTEXT` 后 `BM_CLICK`，选择文件进入产品导入路径；
- `StorageFile` 和 `StorageFolder` 经真实 `DataPackage.SetStorageItems` / `GetView` 进入同一产品解析器；
- 最终 surface 顺序为文件夹、picker 文件、StorageItems 文件，跨进程重载和 SHA-256 均通过；
- `GlobalClipboardUntouched=true`，三个 AOT 进程均自然退出，运行错误为 0；
- 正式数据指纹保持 `24BFA29EF40EE98433DE349D920B8B35E610655E6DB759C6D07EC7AB941624A3`；
- preview/recovery 根均在 ownership marker 复核后清理，成功证据归档保留在 `.artifacts/aot-managed-ui-smoke/win-x64/picker-clipboard-runs/`。

## 5. AOT 发布审计

profile 50 / schema 47 的完整 x64 发布审计通过：

- 发布目录 39 个文件、约 85.8 MiB，符号目录 3 个文件、约 191.8 MiB；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 为 0；
- 原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- C1C1 的现代 picker、精确 owner、StorageItems、真实系统窗口、三进程恢复、正式数据保护和安全清理门禁均通过；
- OLE/native drop、真实 Explorer 物理拖放、`IFileOperation` 和新 Rust ABI 均未进入本阶段；
- Rust ABI 2、能力 511、十个导出及 staging/publish DLL 哈希保持不变。

## 6. 证据边界

本阶段已经证明真实现代 picker、真实选择/取消、精确 owner、真实 `StorageFile`/`StorageFolder`、真实 `DataPackageView`、产品 StorageItems 解析与导入、跨进程持久化、恢复、自然退出和数据隔离。

自动化没有调用全局剪贴板的 `Clipboard.SetContent` 或 `Clipboard.GetContent`。这是有意的安全边界，避免覆盖用户当前剪贴板；因此本阶段证明的是 StorageItems 数据格式和产品解析/导入链，不把“系统会话全局剪贴板传输”宣称为已经自动化证明。该传输仍需在目标机器人工执行复制文件、复制文件夹、粘贴和原剪贴板恢复检查。

文件选择器由 Win32/UIA 驱动，不等于人工鼠标操作。当前证据也不能替代不同 Windows 版本、DPI、多显示器、焦点和视觉层级的人工确认。OLE/native drop、真实 Explorer 鼠标拖放、进入/离开刷新、文件夹高亮、外部拖出以及大文件进度层的物理与视觉行为明确留到 5B-4C1C2。

## 7. 回归与复盘

本阶段新增 12 条契约，定向运行 12/12 通过。最终回归为全部 AOT 相关测试 407/407、x64 全量 2402/2402 和 Rust workspace 57/57；`cargo fmt --check`、Clippy `-D warnings` 与四个阶段 PowerShell 脚本解析也全部通过。

代码复盘确认：普通 JIT 与 NativeAOT 使用同一现代 picker 服务和同一 StorageItems 解析器；AOT fixture 只在 `DESKBOX_NATIVE_AOT` 且精确场景、phase、run ID 和 owned 根成立时记录；全局剪贴板没有被测试场景修改；Rust 模块、JSON 产品格式和安装发布策略均未变化。

## 8. 下一阶段建议

下一阶段只开放 **5B-4C1C2：OLE/native drop 与真实 Explorer 物理拖放**，复杂度高于 C1C1，建议仍作为独立门完成：

1. 从真实 Explorer 以物理鼠标拖入文件、文件夹和大文件，核对 `DragEnter`、`DragOver`、`DragLeave`、`Drop` 与 OLE 回调顺序；
2. 验证移入文件夹后未松开即离开格子时，高亮必然清除，不依赖 routed `DragLeave` 单一事件；
3. 验证外部拖出、复制/移动意图、目标目录、失败与取消语义；
4. 验证大文件只出现一份拖动视觉，进度卡始终置顶且保持既定毛玻璃外观；
5. 自动日志、文件结果、真实 Explorer 操作和人工视觉分别记录，不用合成输入代替物理边界证据。

C1C2 继续保留当前 C# 源生成 COM/vtable 实现。只有测量显示具体托管缓冲、常驻集合或批量预处理导致显著内存占用，并且可以提炼成低频、粗粒度、无 UI 回调的边界时，才把对应计算或解析迁入 Rust；pointer/drag 状态机和 WinUI 高亮生命周期不迁入 Rust。
