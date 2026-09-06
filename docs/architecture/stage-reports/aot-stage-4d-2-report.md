# DeskBox AOT 阶段 4D-2 完成与复盘报告

- 日期：2026-08-21
- 范围：删除未使用的 `FileOperationHelper`，冻结真实 `FileService` 文件操作路径，升级 AOT 审计门禁
- 证据等级：全仓库引用审计、自动化测试、x64 Native AOT 编译与产物审计；未启动 AOT 产物

## 1. 完成结论

4D-2 已完成，范围内没有发现遗漏。本批删除了 223 行未被产品或测试调用的传统
`IFileOperation`/`IShellItem` COM helper，没有修改实际文件操作实现，也没有增加 Rust ABI。

配置 17 / schema 14 的隔离 x64 AOT 审计确认：已删除源码没有重新出现，相关
`FileOperationHelper`、`IFileOperation` 和 `IShellItem` 告警为 0。原始 IL2050 从 4 降为 2；
剩余两次输出是编译器与 ILC 对 `NativeDropTarget.RegisterDragDrop` 同一来源的重复报告。

本批继续采用“按边界选技术”的原则。没有调用者的代码应直接删除；为它建立 Rust C ABI、
加载器、JIT oracle 和差分测试只会增加维护面，不会产生产品收益。

## 2. 代码核对

删除前的全仓库扫描确认，`FileOperationHelper`、`DeleteToRecycleBin` 和
`MoveItemsWithProgress` 在该文件之外均为零引用。实际产品文件操作仍由
`src/DeskBox/Services/FileService.cs` 承担：

- `ExecuteShellMovePlanAsync` 负责 Shell 移动计划和异步等待；
- `DeleteEntryToRecycleBin` 负责回收站删除；
- `MoveEntriesWithShellProgress` 负责带 Shell 进度 UI 的移动；
- `SHFileOperation` 仍是上述真实路径使用的 Win32 入口。

4D-2 没有修改 `FileService.cs`。新增契约同时冻结真实入口仍存在，并扫描 `src/DeskBox`
全部非 `bin`/`obj` C# 源码，拒绝重新引入 `FileOperationHelper` 引用。

删除后的静态互操作清单为：

| 项目 | 4D-1B 后 | 4D-2 后 | 变化 |
| --- | ---: | ---: | ---: |
| `[ComImport]` | 23 / 8 个文件 | 21 / 7 个文件 | -2 / -1 个文件 |
| `[DllImport]` | 96 | 94 | -2 |
| `[LibraryImport]` | 75 | 75 | 0 |

## 3. 验证结果

- 新增 3 条 4D-2 契约，旧实现上 3/3 按预期失败；
- AOT/4D 配置相关测试 42/42 通过；
- `FileService`、桌面整理与 4D-2 扩大定向测试 53/53 通过；
- 规范 x64 全量测试 2016/2016 通过；
- PowerShell 审计脚本通过语法解析；
- 配置 17 / schema 14 的隔离 x64 AOT 审计通过，用时约 151.5 秒；
- 发布目录包含 39 个文件、约 83.3 MiB；符号目录包含 3 个 PDB、约 182.2 MiB；
- 4D-1A、4D-1B 目标文件告警均为 0，4D-2 已删除文件与文件操作告警门禁均为 0；
- 未知告警为 0，完整 `always-throw` 为 0，MVVMTK0045 与 CsWinRT1028 均为 0；
- JSON 默认反射仍为关闭；Rust 保持 ABI 2、能力 63、七个导出，staging/publish 哈希一致；
- 审计前后源码指纹一致。
- 规范 Debug 构建通过，0 个错误、30 个既有警告；随后启动唯一仓库实例，路径为
  `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程响应正常；默认 JIT
  策略下没有加载 `deskbox_native.dll`。

当前原始警告计数为 IL2026 44、IL2050 2、IL2072 4、IL2075 9、IL3050 77、WMC1506 6、
WMC1510 1265，另有既有 C# 编译告警。原始次数包含重复分析通道；4D-2 的完成判断以目标
告警为 0、警告代码集合不扩张和 `always-throw=0` 为准。

本批删除的是零调用源码，因此没有新增产品交互矩阵。文件服务相关自动化已通过；AOT 主程序
仍未启动，不能据此宣称原生发布版本已经可运行。

## 4. 完成后复盘

### OLE 拖放不能只修一条 IL2050

`NativeDropTarget.cs` 是 714 行真实产品链路。它不仅把 `IDropTarget` 传给
`RegisterDragDrop`，还包含：

- Explorer/浏览器到托管事件的 `DragEnter`、`DragOver`、`DragLeave` 和 `Drop` 回调；
- `IDataObject` 的 `CF_HDROP`、`FileGroupDescriptorW`、`FileContents` 读取；
- `TYMED_HGLOBAL`、`TYMED_ISTREAM`、`STGMEDIUM` 的释放和虚拟文件落盘；
- 临时目录清理、窗口注册/注销和 UI 线程转发。

当前 IL2050 只点名 `RegisterDragDrop` 的 COM 接口参数，但代码还使用
`Marshal.GetObjectForIUnknown`、`Marshal.ReleaseComObject` 和内置 `IStream` RCW。只把 P/Invoke
参数改为 `nint` 会隐藏表面告警，却不会形成完整的 AOT 兼容边界。

.NET 当前的 COM 源生成器支持 `IUnknown` 接口、托管对象暴露给 COM 和原生对象投影到托管，
适合 `IDropTarget`、`IDataObject` 这类接口；它不支持 `IDispatch` 接口。对应官方说明见
[ComWrappers source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)
和 [COM interop in .NET](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/cominterop)。

### Rust 判断

OLE 拖放本轮不建议完整迁移 Rust。Rust 若完整持有 `IDropTarget`，仍要设计高频反向回调、
字符串/路径数组所有权、窗口销毁竞态、STA 生命周期和临时文件交接；现有 C# 事件、文件导入和
UI 调度反而会被拆成跨 ABI 状态机。C# 源生成 COM 可以替换互操作层，同时保留已有业务逻辑。

`ExplorerQuickAccessHelper` 和 `ExplorerShellLaunchService` 的情况不同。它们通过
`Shell.Application` 的 `IDispatch`/`dynamic` 主动调用 Shell，边界只有查询、固定 verb 操作和
启动结果，且当前 COM 源生成器明确不支持 `IDispatch`。4D-4 应先比较静态 Shell API 与完整
Rust 原生边界；若 Rust 能完整持有 Automation 对象并只返回 POD 状态和错误文本，它会比在
C# 中重建运行时 Binder 更符合“完整原生边界更简单时使用 Rust”的原则。

## 5. 下一阶段

下一开发阶段建议进入 **4D-3 OLE NativeDropTarget AOT 化**，但拆成两个可独立审计的小批：

1. **4D-3A 数据对象读取侧**：迁移 `IDataObject`/`IStream` 的内置 RCW 与
   `Marshal.GetObjectForIUnknown`，使用源生成 COM 或必要的窄范围显式 vtable 调用；保持拖放
   事件、效果策略、虚拟文件命名和临时目录逻辑不变。
2. **4D-3B 注册与回调侧**：将 `IDropTarget` 改为源生成 COM callable wrapper，
   `RegisterDragDrop` 改为显式接口指针和源生成 P/Invoke，冻结 AddRef/Release、注册、注销和
   窗口销毁顺序；要求 IL2050 清零。

4D-3 整体复杂度为高、行为风险为高，明显高于 4D-2。自动化至少要覆盖四种 effect、空数据、
普通文件和虚拟文件解析边界；完成实现后还必须人工验证 Explorer 文件/文件夹拖入、浏览器
虚拟文件、应用内拖过后离开、窗口关闭后重新打开，以及已知依赖原生 OLE 的第三方来源。

4D-3 完成后再进入 **4D-4 Shell `dynamic`**。4D-4 同样是高复杂度，但边界较粗，完整 Rust
候选价值高于 4D-3。托盘库反射保留为 4D-5，XAML `WMC1510` 保留为 4E，不与 COM 批次合并。

## 6. 后续状态

4D-3A 数据读取侧现已完成：`COMIDataObject`、`Marshal.GetObjectForIUnknown`、
`Marshal.ReleaseComObject` 和内置 `IStream` RCW 已替换为三个固定 vtable 槽的回调期借用
读取层。x64 全量测试 2027/2027 和配置 18 / schema 15 隔离 AOT 审计通过，新读取层告警为
0，IL2050 仍只来自留给 4D-3B 的 `RegisterDragDrop`。

开始 4D-3B 前应先完成 Explorer、浏览器虚拟文件、拖入离开和窗口重开人工矩阵。最新结论
以 `aot-stage-4d-3a-report.md` 和总路线为准。

后续人工矩阵已确认通过，4D-3B 也已完成源生成 `IDropTarget` CCW 与显式注册指针。配置 19 /
schema 16 将 IL2050 清零，规范 x64 全量测试 2036/2036 通过。最新结论以
`aot-stage-4d-3b-report.md` 和总路线为准。
