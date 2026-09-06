# DeskBox AOT 阶段 4D-3A 完成与复盘报告

- 日期：2026-08-21
- 范围：OLE `IDataObject`/`IStream` 数据读取侧 AOT 化；不修改 `IDropTarget` 注册与回调侧
- 证据等级：源码契约、原生 vtable 行为测试、完整 x64 测试、Native AOT 编译与产物审计；真实拖放交互和 AOT 产物启动尚未执行

## 1. 完成结论

4D-3A 已完成到自动化与 AOT 产物审计边界，代码复盘未发现范围内遗漏。旧实现中的
`COMIDataObject`、`Marshal.GetObjectForIUnknown`、`Marshal.ReleaseComObject` 和内置
`IStream` RCW 已全部从 `NativeDropTarget` 数据读取链删除。

新实现没有增加 Rust ABI，也没有把拖放事件、窗口生命周期或文件导入迁移到原生 DLL。
OLE 回调传入的 `IDataObject*` 只在回调期间同步借用，读取层直接调用三个固定 ABI 槽：

- `IDataObject::GetData`：vtable slot 3；
- `IDataObject::QueryGetData`：vtable slot 5；
- `ISequentialStream::Read`（由 `IStream` 继承）：vtable slot 3。

这种边界不创建或缓存 RCW，也不跨回调保存 COM 指针。相比为只使用两个 `IDataObject` 方法
和一个流方法创建完整对象投影，窄 vtable 读取层更直接；4D-3B 的托管对象反向暴露仍使用
源生成 COM，不把两种方向强行统一成同一种实现。

## 2. 实现核对

### 数据对象

新增 `NativeOleDataObject`，持有回调期借用指针并只提供 `GetData`、`QueryGetData`。调用仍使用
原来的 `FORMATETC` 值：

- 普通文件：`CF_HDROP`、`DVASPECT_CONTENT`、`lindex=-1`、`TYMED_HGLOBAL`；
- 虚拟文件描述：`FileGroupDescriptorW`、`TYMED_HGLOBAL`；
- 虚拟文件内容：按原顺序先尝试 `TYMED_ISTREAM`，失败后尝试 `TYMED_HGLOBAL`。

`NativeFormatEtc` 和 `NativeStorageMedium` 采用顺序布局。x64 ABI 契约确认前者大小 32 字节、
字段偏移 0/8/16/20/24，后者大小 24 字节、字段偏移 0/8/16。

### COM 流

新增 `NativeComStreamReader`，使用 81,920 字节缓冲区循环调用 `Read`：

- 负 HRESULT 通过 `Marshal.ThrowExceptionForHR` 保持异常语义；
- `S_FALSE` 会先写入本次有效字节，再结束读取；
- `S_OK` 但读取 0 字节时结束，避免异常来源造成死循环；
- 返回字节数超过提供缓冲区时拒绝继续，防止越界数据进入托管写入。

流指针仍由外层 `STGMEDIUM` 持有，文件写入完成后继续由既有
`ReleaseStgMedium` 路径释放。普通 HGLOBAL 文件、虚拟文件名清理、缺失扩展名推断、临时目录
删除以及 `DropEvent`/UI 调度均未改变。

## 3. 测试与 AOT 审计

- 新增 4 条结构契约；旧实现上 3 条按预期失败，冻结“注册侧留给 4D-3B”的 1 条通过；
- 新增 7 条 vtable/ABI 行为测试，覆盖结构布局、GetData、QueryGetData、分块流读取、
  `S_FALSE`、失败 HRESULT、越界字节数和空指针；
- 拖放、虚拟文件、4D/AOT 与音乐音量扩大定向测试 64/64 通过；
- 规范 x64 全量测试 2027/2027 通过；
- PowerShell 审计脚本语法解析通过；
- 配置 18 / schema 15 的隔离 x64 AOT 审计通过，用时约 122.9 秒；
- 发布目录 39 个文件、约 83.3 MiB；符号目录 3 个 PDB、约 182.2 MiB；
- 4D-3A 旧 RCW 源码匹配为 0，新读取层 AOT 告警为 0，`NativeDropTarget` 非预期告警为 0；
- 剩余两条 `NativeDropTarget` 告警均为编译器/ILC 对
  `RegisterDragDrop(IDropTarget)` 同一 IL2050 来源的重复输出；
- 完整 `always-throw=0`，未知警告为 0，JSON 默认反射关闭；
- Rust 保持 ABI 2、能力 63、七个导出，staging/publish 哈希一致；
- 审计前后源码指纹一致。
- 规范 Debug 构建通过，0 个错误、30 个既有警告；随后启动唯一仓库实例，路径为
  `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`，进程响应正常；默认 JIT
  策略下没有加载 `deskbox_native.dll`。

原始警告计数保持为 IL2026 44、IL2050 2、IL2072 4、IL2075 9、IL3050 77、WMC1506 6、
WMC1510 1265，另有既有 C# 编译告警。4D-3A 没有通过抑制或宽泛 trimming root 隐藏告警。

静态清单现为 20 个 `[ComImport]`、94 个 `[DllImport]` 和 75 个 `[LibraryImport]`。
`ComImport` 比 4D-2 后少 1 个，来自已删除的 `COMIDataObject`；`IDropTarget` 仍保留到 4D-3B。

## 4. 复盘与验证边界

代码复盘确认以下生命周期关系成立：

1. `IDataObject*` 由 OLE 调用方保证在 `DragEnter`/`Drop` 回调期间有效，读取层不 AddRef、
   不 Release，也不把指针保存到异步任务；
2. `IStream*` 在对应 `STGMEDIUM` 释放前同步读取，随后仍由 `ReleaseStgMedium` 统一释放；
3. 提取出的普通路径或临时路径在 COM 回调结束前复制为托管字符串，异步导入不持有 COM 状态；
4. `IDropTarget` 的注册、注销、CCW 和窗口销毁顺序本轮完全未改。

自动化 vtable 测试能证明 ABI 槽位、参数和返回处理，但不能代替真实 OLE 来源。进入 4D-3B
前应在当前 Debug 实例完成以下人工矩阵：

1. 从 Explorer 拖入普通文件和文件夹，确认拖过反馈、放下导入和源文件保留正常；
2. 从浏览器拖入虚拟图片或文件，确认 `FileContents` 能落盘，缺失扩展名仍能识别；
3. 拖入后移出窗口不放下，确认高亮/状态清除；
4. 关闭并重新打开文件组件窗口后再次拖入，确认注册生命周期没有受到读取侧改动影响；
5. 若本机可用，再验证微信或其他原生 OLE 来源。

AOT 主程序仍未启动，因此本轮只证明源码、ABI、测试与 AOT 编译产物满足 4D-3A 契约，
不证明裁剪后的真实 OLE 回调已经运行。

## 5. 下一阶段

人工矩阵通过后，下一开发批为 **4D-3B `IDropTarget` 注册与回调侧**：

1. 用 `[GeneratedComInterface]` 定义 `IDropTarget`，用 `[GeneratedComClass]` 暴露托管实现；
2. `RegisterDragDrop` 改为显式接口指针和源生成 P/Invoke，不再让 P/Invoke 进行内置 COM marshalling；
3. 明确 `StrategyBasedComWrappers` 返回指针、`RegisterDragDrop` 持有引用、
   `RevokeDragDrop` 与本地引用释放的顺序；
4. 保持现有 `DragEnter`/`DragOver`/`DragLeave`/`Drop` 事件和 effect 策略；
5. 将 IL2050 从 2 次原始输出降为 0，并重新执行真实拖放矩阵。

4D-3B 复杂度中高、生命周期风险高，继续使用 C# 源生成 COM，不扩展 Rust。4D-3B 完成并
通过人工拖放后，再进入 4D-4 `Shell.Application` dynamic 的静态 Shell API/Rust 边界评审。

## 6. 后续状态

4D-3A 前置人工矩阵随后已确认通过，4D-3B 也已完成实现与自动化/AOT 审计。传统
`[ComImport] IDropTarget`、`[ComVisible]` 内置 CCW 和接口参数 `DllImport` 已替换为
`[GeneratedComInterface]`、`[GeneratedComClass]`、显式接口指针和两个 `[LibraryImport]`。
配置 19 / schema 16 将 IL2050 从 2 降为 0，规范 x64 全量测试 2036/2036 通过。

4D-3B 后仍需重新执行真实拖放矩阵；最新实现、引用生命周期和下一阶段拆分以
`aot-stage-4d-3b-report.md` 与总路线为准。
