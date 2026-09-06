# DeskBox AOT 阶段 5B-4C1C2A 完成报告

- 日期：2026-08-23
- 状态：5B-4C1C2A 已完成到 x64 Native AOT 自动化实际运行边界
- 审计配置：profile 51 / schema 48
- 本阶段范围：OLE `IDropTarget` 生成式 COM/HDROP、拖入进入/离开清理、复制/移动意图冻结、大文件进度层、三进程重载与恢复
- 明确保留：真人从 Explorer 以物理鼠标拖放及视觉验收，归入 5B-4C1C2B

## 1. 结论

5B-4C1C2A 已闭环 NativeAOT 下的产品 OLE/native drop 自动化边界。受审计 AOT 进程通过 `[GeneratedComClass]` 生成的真实 CCW 和 `IDropTarget` vtable，接收测试夹具构造的 `CF_HDROP` data-object vtable，依次执行 `DragEnter`、`DragOver`、`DragLeave` 或 `Drop`，再进入 File Widget 的实际导入、进度、持久化、重载和恢复路径。

产品侧补齐了两个行为缺口。第一，原生 `DragEnter/DragOver/DragLeave` 只负责观察 pointer 和清理已存在的文件夹、文件堆及 surface 拖放状态，不从窗口层创建新的高亮，因此指针离开格子或窗口时不再依赖单一 WinUI routed `DragLeave`。第二，复制或移动意图在 OLE `Drop` 回调期按 key state 和允许 effect 冻结，并随路径一起排入 UI 线程；异步导入不会在回调结束后重新读取 Ctrl 状态。

本阶段没有扩展 Rust。OLE 回调、generated CCW、WinUI 命中测试、高亮和进度卡都是高频反向回调及 UI 生命周期边界；现有实现没有测得可粗粒度隔离的托管内存热点。迁移到 Rust 会增加 COM/FFI 回调和对象生命周期复杂度，不能证明可降低内存。生产 Rust 模块继续保持 ABI 2、能力 511 和十个必需导出。

## 2. 产品实现边界

`NativeDropTarget` 继续采用 C# source-generated COM，注册真实 `IDropTarget`。本阶段对产品路径的调整包括：

1. `DragEnterEvent` 和 `DragOverEvent` 传递屏幕坐标，`DragLeaveEvent` 显式结束 surface 拖放状态；
2. `DropEvent` 除路径、坐标和临时文件标记外，携带回调期冻结的 `copyWhenMapped`；
3. File Widget 将屏幕坐标映射到真实 surface，只清除越界或无文件数据时的旧高亮，不由原生窗口回调创建文件夹高亮；
4. `Drop` 返回前只完成路径提取、effect 决策和 UI 队列投递，文件复制或移动在回调释放后异步执行；
5. `WM_DROPFILES` 仍保留原有兼容路径，因其没有 OLE key/effect 快照，继续使用既有默认导入决策；
6. 进度卡保持独立高层，`Canvas.ZIndex=1000`、`Translation.Z=64`，背景为 `AcrylicBrush`。

普通 JIT 仍走同一产品注册和导入逻辑。NativeAOT-only 代码只负责构造 owned 夹具、调用 generated CCW vtable、收集证据和执行恢复，不替换产品 drop target 或文件操作实现。

## 3. NativeAOT 自动化证据

最终成功 run ID 为 `93da51cb94dc4db7a8a1a67d4511bcdf`，使用三个全新 AOT 进程：

| 阶段 | PID | 作用 |
| --- | ---: | --- |
| Mutate | 1488 | generated CCW/HDROP、离开清理、复制/移动、进度及持久化 |
| VerifyRestore | 18728 | 新进程重载变更并恢复 owned 基线 |
| Postflight | 40732 | 再次重载确认基线和无残留状态 |

Mutate 进程的主要结果：

- `SourceKind=ProgrammaticGeneratedCcwHDrop`，`PhysicalExplorerMouseVerified=false`；
- drop target 已真实注册，`DragEnter`、`DragOver`、`DragLeave`、`Drop` HRESULT 均按场景为 0；
- 将 pointer 移到真实 surface 外部后，文件夹视觉由 `DropTarget` 恢复为 `Normal`；显式 `DragLeave` 也完成同样清理；
- Ctrl 场景冻结为 copy，384 MiB 文件和文件夹源仍存在，目标副本长度与 SHA-256 一致；
- 无 Ctrl 场景冻结为 move，文件和文件夹源消失，目标内容长度与 SHA-256 一致；
- 两次 OLE callback 都先返回，再观察到产品导入 busy/progress，避免长文件操作占用 OLE 回调；
- 大文件导入在 165 ms 处取证为 determinate 进度，值约 `39.71%`，文本为 `40%`，描述包含 `152.5 MB / 384 MB`；
- 进度卡当时可见，`ZIndex=1000`、`TranslationZ=64`，背景类型为 `Microsoft.UI.Xaml.Media.AcrylicBrush`；
- 三个进程均通过应用正常退出路径自然结束，preview/recovery owned 根在 marker 复核后清理。

正式数据运行前后均为 122 个文件、305,728,603 bytes，元数据指纹保持：

`238CC52BF8B938CC47CBDA12509CA3C63BEFC8353EC8642039046A3FA5DD41DB`

受审计 AOT EXE SHA-256 为：

`DDD74DFDE51A18CBE2C406D9DE03FBFFEF04788E98102A4F03F4B5511E9A1B70`

成功证据归档保留在 `.artifacts/aot-managed-ui-smoke/win-x64/native-drop-runs/93da51cb94dc4db7a8a1a67d4511bcdf/`。

## 4. 实际运行发现并修正的遗漏

本阶段不是只靠静态测试完成。连续实际 AOT 运行先后发现并修正：

1. 独立预览启动器仍要求上一版 profile/schema，已同步为 51/48；
2. 共享 managed UI runner 未把 NativeDrop 场景纳入 primary File Widget ID 路由，导致无法定位目标 surface；
3. 初版视觉探针读取 ViewModel 状态而非真实 `Border` 外观，已改为核对边框厚度与非透明 brush；
4. NativeDrop 场景未进入共享自然退出分支，已加入统一 shutdown 条件；
5. 一条历史测试仍禁止原生 `DragLeaveEvent` 订阅，与本阶段的 surface 清理契约冲突，已迁移为允许 Enter/Over/Leave 清理但继续禁止窗口级高亮。

上述失败运行的 owned preview/recovery 根按故障取证规则保留，没有在本轮擅自删除。

## 5. AOT 发布审计与回归

profile 51 / schema 48 的标准 x64 发布审计通过：

- 发布目录 39 个文件、约 86.1 MiB，符号目录 3 个文件、约 192.9 MiB；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 为 0；
- 原始 IL2026、IL2050、IL2072、IL2075、IL3050 均为 0；
- Rust ABI 2、能力 511、十个导出以及 staging/publish DLL 哈希门禁通过；
- 5B-4C1C2A 新增契约 10/10；全部 AOT 相关测试 417/417；
- 规范 x64 全量测试 2412/2412；Rust workspace 57/57；
- `cargo fmt --check`、Clippy `-D warnings` 和相关 PowerShell 脚本解析通过。

项目标准审计允许仓库已经精确冻结的 C# 编译器警告和 WMC1510 基线。额外的 `-RequireCleanAnalysis` 会因这些已知基线失败，因此它不是本阶段完成结论的依据，也没有被写成“全项目零警告”。

## 6. 证据边界

本阶段已经证明：受审计 NativeAOT 产物中的 generated COM CCW 可被真实 vtable 调用；`CF_HDROP` 路径提取、effect 冻结、leave/outside 清理、回调释放、产品复制/移动、可见进度层、跨进程重载、恢复、正式数据保护和 owned 根清理均成立。

本阶段没有证明真人从 Explorer 按住鼠标移动时的系统 drag image、指针事件时序、不同 DPI/显示器下的命中、Ctrl 实际按键变化、窗口遮挡以及人眼看到的层级和毛玻璃效果。程序化 HDROP 和 generated CCW 证据不能替代这些物理交互，因此 `PhysicalExplorerMouseVerified` 明确为 `false`。

## 7. 下一阶段调整

下一阶段只开放 **5B-4C1C2B：真实 Explorer 物理鼠标与视觉验收**，复杂度为中等；现有实现和自动化已经稳定，主要风险转为操作系统输入、drag image、DPI 和视觉合成。如果物理矩阵发现产品缺陷，再进行窄修复并重跑 C1C2A 门禁。

人工矩阵建议使用唯一 owned 测试目录和可丢弃文件：

1. 分别拖入小文件、文件夹和至少 384 MiB 文件，核对结果、数量、长度和 SHA-256；
2. 对映射目录分别验证 Ctrl copy 与无 Ctrl move，确认源和目标状态符合意图；
3. 悬停文件夹触发高亮后，不松开鼠标移出格子，再移出整个 Widget，确认高亮和其他拖放状态均立即清除；
4. 大文件拖入时确认只有一份系统拖动图标，进度卡始终在其上方，背景为既定毛玻璃，完成后卡片收起；
5. 验证从 File Widget 向 Explorer 的外部拖出、取消和失败路径，不留下高亮或临时内容；
6. 至少覆盖 100% 与一个非 100% 缩放；如设备具备多显示器，再验证跨显示器进入、离开和 drop；
7. 保存屏幕录像或关键截图、产品日志和文件哈希，分别标记自动证据与人工视觉结论。

C1C2B 通过后，5B-4C1C 才能整体关闭。随后建议进入 5B-4C2 的快捷键/输入钩子 AOT 实际矩阵，再评估媒体 UI、真实 Weather 网络/定位、Quick Capture 全局剪贴板/图片、安装升级、ARM64/Store 和性能型 Rust 候选；这些范围不应与 C1C2B 同批混合。
