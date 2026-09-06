# DeskBox AOT 阶段 4D-4A 完成与复盘报告

- 日期：2026-08-21
- 范围：`ExplorerShellLaunchService` 的 Explorer 托管环境启动；不包含快速访问操作
- 技术选择：完整 Rust 粗粒度边界；普通 JIT 保留 C# oracle，Native AOT 只编译 Rust 路径
- 证据等级：源码与 ABI 契约、Rust 测试、x64 托管测试、Native AOT 编译与产物审计；
  显式 Rust 真实打开矩阵和 AOT 应用启动仍待人工验证

## 1. 完成结论

4D-4A 已完成实现和 AOT 结构审计。原 C# `Shell.Application` dynamic 链没有被删除，而是
完整放入 `#if !DESKBOX_NATIVE_AOT`，继续作为普通 JIT 默认行为基准。显式 Rust JIT 和
Native AOT 使用新增的 `deskbox_explorer_shell_launch_v1`。

本批采用完整 Rust 操作而非生成式 C# COM，是因为这条链依赖 `IDispatch` Automation，
.NET COM 源生成器面向 IUnknown 型接口。用 C# 重建 DISPIDs、参数逆序、VARIANT/BSTR 与多层
接口转换，会比调用 `windows-rs` 已生成的强类型 Shell 接口更大、更脆弱。该判断只适用于
这类单向、同步、无长期对象跨界的边界；4D-3 的高频反向拖放回调仍正确地保留在 C#。

## 2. 保持不变的产品行为

1. 首选运行中的 Explorer 桌面对象执行 `ShellExecute`，保留子进程继承 Explorer 环境的目的；
2. path、工作目录、`open` verb 和 `SW_SHOWNORMAL` 语义不变，参数仍为空；
3. Explorer 路径失败后仍由 `Win32Helper.OpenFileOrChooseApp` 调用本地
   `Process.Start(UseShellExecute=true)`；
4. 本地启动返回 1155 时仍调用带真实 owner 的 `SHOpenWithDialog`；
5. Rust 显式模式失败不回退到 C# oracle，避免差异和部署错误被静默掩盖；产品级本地
   ShellExecute/Open With 回退不受影响。

## 3. 实现与 ABI

Rust 依次使用 `IShellDispatch`、`IShellWindows`、`IWebBrowser`、
`IShellFolderViewDual` 和 `IShellDispatch2`。COM 初始化、对象创建、Windows 集合、桌面、
document、Application 和 Execute 七个阶段都有独立 HRESULT 与位标记。所有 COM 对象、
`BSTR` 和 `VARIANT` 都在一次调用内释放，不跨 ABI 暴露。

模块 ABI 版本仍为 2；新增能力位 `1 << 6` 后完整掩码为 127，发布必需导出从 7 个增至 8 个。
请求/结果结构版本均为 1，x64 尺寸分别为 96/88 字节。详细布局、输入约束和生命周期见
`explorer-shell-launch-native-abi-v1.md`。

## 4. 自动化与审计结果

- 新增 4D-4A 契约覆盖后端策略、AOT 源码隔离、ABI 布局、真实 DLL 能力/导出及审计门禁；
  旧实现因缺少新策略和类型而按预期无法通过编译；
- Rust 格式化、Clippy `-D warnings` 和 45/45 测试通过；
- AOT、Explorer、shortcut、音乐音量扩大定向测试最终 98/98 通过；
- 规范 x64 全量测试 2049/2049 通过；
- 配置 20 / schema 17 的首轮隔离 x64 AOT 审计通过，用时约 161.1 秒；
- 发布目录 39 个文件、87,349,901 字节；符号目录 3 个文件、190,992,384 字节；
- Rust 为 ABI 2、能力 127、八个必需导出，staging/publish SHA-256 一致；
- 4D-4A 目标文件警告为 0，Explorer 启动 `always-throw=0`，完整 `always-throw=0`；
- 未知警告代码为 0，JSON 默认反射关闭，审计前后源码指纹一致；
- 剩余原始警告为 IL2026 34、IL2072 2、IL2075 9、IL3050 62、WMC1506 6、
  WMC1510 1265，另有既有 C# 编译警告。

相较配置 19，Explorer 服务贡献的原始 IL2026 10、IL2072 2、IL3050 15 已全部消除；全局
对应计数由 44/4/77 降为 34/2/62。没有通过 suppress、宽泛 trimming root 或删除产品功能
隐藏告警。

文档落地后的第二次隔离 AOT 复审也通过：用时约 171.8 秒，发布目录 39 个文件、
87,349,389 字节，符号目录仍为 3 个文件、190,992,384 字节；源码在审计期间稳定，目标告警、
三类 `always-throw`、未知警告、ABI/能力/导出和同次哈希结论均与首轮一致。最终 DLL SHA-256
由 `.artifacts/aot-audit/win-x64/summary.json` 留档，不作为跨构建固定 ABI 值。

最终 `git diff --check` 没有空白错误。规范非平台 Debug 构建通过，0 个错误、30 个既有警告；
随后从 `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动唯一仓库实例。
PID 36260 响应正常，普通 JIT 默认策略在启动阶段加载的 `deskbox_native.dll` 数量为 0。

## 5. 完成后复盘

当前代码复盘未发现需要扩大 4D-4A 范围的遗漏：

1. Native AOT 编译单元中不再包含 `Type.GetTypeFromProgID`、`Activator.CreateInstance`、
   `dynamic` 或 `Marshal.ReleaseComObject` 的 Explorer 启动 oracle；
2. Rust 没有手写通用 `IDispatch::Invoke`，只使用类型库生成接口，调用链与旧实现逐步对应；
3. `S_OK`/`S_FALSE` 的 COM 初始化会配对撤销，`RPC_E_CHANGED_MODE` 不会撤销调用方 apartment；
4. 托管与 Rust 双侧都检查结构版本、长度、嵌入 NUL、reserved、状态一致性和未知阶段位；
5. 构建、发布和审计均要求同一个能力 127/八导出契约；
6. 既有 shortcut 与音乐音量 ABI/行为未改，4D-4B 的快速访问代码没有被修改；
7. 本地 ShellExecute 和 Open With 仍在产品编排层，不会因 Rust DLL 或 Explorer 暂时不可用
   而丢失原有恢复路径。

## 6. 开放项与下一阶段

4D-4A 尚有一项交互门槛：用 `DeskBoxRustNative=true` 构建并在进程启动前设置
`DESKBOX_EXPLORER_SHELL_BACKEND=rust`，人工验证已有文件、文件夹、URL、未知扩展名、缺失
目标和环境继承矩阵。这项验证涉及实际打开应用或系统对话框，未由自动化擅自执行。

下一开发批建议为 **4D-4B 快速访问固定状态与操作**，仍保持独立：先冻结
`ExplorerQuickAccessHelper` 的查询、`pintohome`、`unpinfromhome`、专用 STA 线程、重复操作和
本地化 verb 行为，再判断是否采用同样的完整 Rust 粗粒度边界。它比 4D-4A 复杂，因为同时有
查询与写操作、集合枚举、线程切换和 Explorer 状态一致性；不应复用本轮导出去暗中扩大语义。

在上述显式 Rust 人工矩阵通过前，不开始 4D-4B。AOT 主程序真实启动、安装升级和完整功能
矩阵仍属于阶段 5，而不是 4D-4A 的自动化完成条件。

## 7. 4D-4B 后续说明

4D-4B 已在后续批次完成：Quick Access 使用独立 Rust v1 导出，未扩展本报告的 Explorer
启动请求或结果结构。当前完整模块为 ABI 2、能力 255、九个必需导出；配置 21 / schema 18
确认 Explorer 启动、Quick Access 与完整 `always-throw` 均为 0。4D-4A 的历史测试数字与人工
打开矩阵边界保持不变，最新状态见 `aot-stage-4d-4b-report.md`。
