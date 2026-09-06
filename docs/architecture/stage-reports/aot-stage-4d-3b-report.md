# DeskBox AOT 阶段 4D-3B 完成与复盘报告

- 日期：2026-08-21
- 范围：OLE `IDropTarget` 注册、CCW 与回调侧 AOT 化；不修改数据读取协议和文件导入业务
- 前置条件：4D-3A Explorer、浏览器虚拟文件、拖入离开和窗口重开人工矩阵已确认通过
- 证据等级：源码契约、真实源生成 CCW vtable 测试、引用计数测试、完整 x64 测试、
  Native AOT 编译与产物审计；4D-3B 后的真实拖放已开始复测并完成三项跟进修复，
  修复后的完整人工矩阵和 AOT 产物启动尚未执行

## 1. 完成结论

4D-3B 已完成到自动化与 AOT 产物审计边界。`NativeDropTarget` 不再声明传统
`[ComImport] IDropTarget`，不再依靠 `[ComVisible]` 和运行时生成的内置 CCW，也不再把
COM 接口类型直接交给 `DllImport` marshaller。

新增边界使用：

- `[GeneratedComInterface]` 定义 IUnknown 型 `INativeDropTarget`；
- `ComInterfaceOptions.ManagedObjectWrapper` 只生成托管对象向 COM 暴露所需的方向；
- `[GeneratedComClass]` 生成 `NativeDropTargetComObject` 的 CCW；
- `[LibraryImport]` 以 `nint` 调用 `RegisterDragDrop` 和 `RevokeDragDrop`；
- `ComInterfaceMarshaller<INativeDropTarget>` 显式获取和释放本地接口引用。

这一段继续使用 C# 源生成 COM。拖放需要 Shell 高频反向调用现有托管事件和 UI 状态，迁移
Rust 会引入一组双向回调和跨 ABI 生命周期，复杂度高于当前 128 行的单向 CCW 边界。

## 2. 注册与引用生命周期

注册顺序固定为：

1. `ConvertToUnmanaged` 获取一个明确的 `IDropTarget*` 本地引用；
2. 以该显式指针调用 `RegisterDragDrop`；
3. 无论注册成功或失败，都在 `finally` 中调用 `Free` 释放本地引用；
4. 注册成功时，OLE 已对接口调用 `AddRef`，因此本地引用释放后目标仍然有效；
5. 窗口注销时调用 `RevokeDragDrop`，由 OLE 对先前持有的接口调用 `Release`；
6. `NativeDropTarget` 在自身生命周期内继续强引用 `NativeDropTargetComObject`，后者反向持有
   owner，保证所有回调仍能访问同一事件和状态对象。

`Register()` 的幂等判断、失败日志和 `_registered` 更新位置保持不变；`Unregister()` 仍只在
成功注册状态下调用，仍保持原有的异常吞吐边界和幂等行为。

## 3. 回调行为核对

原来的 `DragEnter`、`DragOver`、`DragLeave`、`Drop` 业务体移动为
`NativeDropTarget.On*` 方法，生成的 COM 类只负责转发。以下顺序未改变：

- `DragEnter` 先探测虚拟文件与 `CF_HDROP`，再触发事件，最后计算反馈 effect；
- `DragOver` 先触发坐标事件，再基于已缓存的数据类型计算 effect；
- `DragLeave` 先清除两类数据状态，再触发离开事件；
- `Drop` 同步提取路径、清除状态、记录诊断，存在路径时触发 `DropEvent`，否则返回 none；
- 4D-3A 的 `IDataObject*` 借用、`STGMEDIUM` 释放、虚拟文件落盘和扩展名推断没有改动。

`POINTL` 的 Win64 布局仍为 8 字节，X/Y 偏移为 0/4。接口方法顺序对应 vtable slot 3 到 6。

## 4. 测试与 AOT 审计

- 新增 4 条 4D-3B 源码/配置契约；旧实现上 4/4 按预期失败；
- 新增 5 条运行时 CCW 测试，覆盖 `POINTL` 布局、IID 查询、slot 3-6、四类回调转发、
  effect 行为、空指针保护和引用持有顺序；
- 引用测试模拟 `RegisterDragDrop` 的额外 `AddRef`：释放本地 marshaller 引用后仍能调用
  `DragLeave`，最后再模拟 `RevokeDragDrop` 释放 OLE 引用；
- 4D-3A/3B 定向测试 13/13 通过；
- 规范 x64 全量测试 2036/2036 通过；
- PowerShell 审计脚本语法解析通过；
- 配置 19 / schema 16 的隔离 x64 AOT 审计通过，用时约 153.1 秒；
- 发布目录 39 个文件、约 83.3 MiB；符号目录 3 个 PDB、约 182.2 MiB；
- 4D-3B 传统注册模式匹配、缺失生成式 COM 模式、目标文件告警和 IL2050 均为 0；
- 4D-3A 旧 RCW 匹配、数据读取层告警和 `NativeDropTarget` 剩余告警也均为 0；
- 完整 `always-throw=0`、未知警告为 0、JSON 默认反射关闭；
- Rust 保持 ABI 2、能力 63、七个导出，staging/publish 哈希一致；
- 审计前后源码指纹一致。
- 规范非平台 Debug 构建通过，0 个错误、30 个既有警告；随后从
  `src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe` 启动唯一仓库实例；
  PID 32144 响应正常，普通 JIT 默认策略未加载 `deskbox_native.dll`；
- 启动日志确认新生成式 CCW 已通过真实 `RegisterDragDrop` 成功注册到 8 个恢复窗口的 HWND，
  没有注册失败日志。该证据证明真实 Windows 注册成功，但不代替实际拖入回调验证。

剩余原始分析警告为 IL2026 44、IL2072 4、IL2075 9、IL3050 77、WMC1506 6、
WMC1510 1265，另有既有 C# 编译警告。IL2050 已从 4D-3A 的 2 次原始输出降为 0，且本轮
没有增加抑制或宽泛 trimming root。

静态清单现为 19 个 `[ComImport]`（6 个文件）、92 个 `[DllImport]` 和 77 个
`[LibraryImport]`。相对 4D-3A，删除一个 `IDropTarget` ComImport 和两个注册 DllImport，
新增两个源生成 P/Invoke。

## 5. 完成后复盘

代码、生成 CCW 行为和 AOT 日志三层核对后，4D-3B 范围内未发现遗漏：

1. 注册函数不再接收托管接口，IL2050 不是通过改名或抑制隐藏，而是由显式指针边界消除；
2. `ConvertToUnmanaged` 返回的是请求的 `INativeDropTarget` 接口指针，不依赖“首接口恰好与
   IUnknown 地址相同”的实现细节；
3. 本地引用在注册成功与失败两条路径都会释放，OLE 引用由 Register/Revoke 配对；
4. 生成式接口仅开放 managed-object wrapper，不创建本轮不需要的 RCW 方向；
5. 数据读取和注册边界相互独立，后续修改一侧不会要求重做另一侧协议；
6. 本轮没有扩展 Rust ABI，也没有改变普通 JIT 与 AOT 的拖放实现分支。

自动化能证明 ABI、转发和引用计数模型，但不能替代 Windows OLE 对真实窗口的注册。因此，
进入 4D-4A 前仍需在当前 Debug 实例完成 Explorer 文件/文件夹、浏览器虚拟文件、拖入后
离开、窗口关闭重开，以及可用时的微信或其他原生 OLE 来源复测。AOT 主程序仍未启动。

## 6. 4D-3B 后人工复测跟进

人工复测发现的三项问题均位于既有 WinUI 文件表面，不在 4D-3A/3B 的 COM ABI 边界：

1. 从桌面拖入大文件后，根表面 `Root_Drop` 在整个文件传输期间仍持有系统 Drop deferral。
   DeskBox 已显示自身文件与底部进度时，Explorer 拖动图和标题仍未关闭，视觉上形成两层图标。
   当前实现会先完整物化 `DataPackageView`、缓存操作类型和来源，再完成 deferral，随后才执行
   长时间文件传输；临时虚拟文件批次仍由原有作用域持有和释放。
2. 文件夹和文件堆目标会把子项 `DragOver`/`DragLeave` 标记为已处理。快速跨越子项或直接移出
   内容区时，根表面的普通路由处理器可能没有机会清理旧目标。当前根表面以
   `handledEventsToo` 旁路观察两类事件，每次核对当前目标边界；离开整个表面时复用统一的
   `ClearDragSessionVisualState`，保留原有重排、Drop 和窗口级原生指针恢复路径。
3. 复制中的目标文件会被文件监控提前显示，原进度卡与文件项同在 `FileItemsViewport`，较低的
   Z 层不足以隔离文件项及其合成视觉。进度卡现移到 Root 的独立覆盖层，使用 ZIndex 1000 和
   Z 轴抬升，并改用 WinUI `SystemControlAcrylicElementBrush`、上沿圆角和细描边。

两项新增行为契约和一项增强的进度层级契约均已验证；其中两项行为契约在旧实现上 2/2 按预期
失败，修复后本批 3/3 通过。文件表面相关契约 21/21、x64 全量测试 2038/2038、规范非平台
Debug 构建均通过，0 个错误。当前唯一仓库实例来自
`src/DeskBox/bin/Debug/net10.0-windows10.0.22621.0/DeskBox.exe`。本批没有修改
`NativeDropTarget`、源生成 CCW、Rust ABI、AOT 分支或文件传输语义，因此不重开 4D-3B 的
COM/AOT 实现审计。

进入 4D-4A 前还需在当前实例确认：大文件拖入开始复制后系统拖动图立即消失；进度卡位于文件
项之上且毛玻璃外观正确；文件夹高亮在移到普通文件、空白区、标题区和窗口外时均立即清除；
随后完成原 4D-3B 的真实来源矩阵。

后续用户已确认本轮可进入下一步，因此该人工门槛关闭并开放 4D-4A。这里保留问题发现与修复
当时的证据顺序，不把后续 4D-4A 的自动化结果混入 4D-3B 数字。

## 7. 后续阶段跟进

原路线中的 4D-4 `Shell.Application dynamic` 应拆为两个阶段，不能一次迁移：

1. **4D-4A Explorer 托管环境启动**：只处理 `ExplorerShellLaunchService`。该服务不是普通
   `Process.Start` 的重复实现，它刻意通过正在运行的 Explorer 启动目标，使子进程继承当前
   Shell 环境；现有本地 `ShellExecuteEx` 仍只是失败回退。当前 AOT 日志在该文件产生
   IL2026 10、IL2072 2、IL3050 15 次原始输出。
2. **4D-4B 快速访问固定状态与操作**：单独处理 `ExplorerQuickAccessHelper` 的状态查询、
   `pintohome`、`unpinfromhome` 和专用 STA 线程。当前该文件产生 IL2026 34、IL2072 2、
   IL3050 55 次原始输出，行为面和人工矩阵明显大于 4D-4A。

两条链都依赖 `IDispatch` Automation，而 .NET COM 源生成器只支持 IUnknown 型接口；在 C#
中手写完整 `IDispatch`/VARIANT/BSTR 调用链会比本轮 CCW 边界复杂得多。因此 4D-4A 已按本节
建议采用“完整 Rust 粗粒度操作 + 普通 JIT C# oracle + AOT Rust 路径”，没有把 Explorer
环境语义退化为单纯 `Process.Start`。

4D-4A 当前已完成代码、ABI、自动化与配置 20 / schema 17 的 x64 AOT 产物审计：模块保持
ABI 2，能力变为 127，必需导出增至八个；该服务的 IL2026/IL2072/IL3050 目标输出和 Explorer
启动 `always-throw` 均为 0。其显式 Rust JIT 文件、文件夹、URL、未知扩展名与失败回退矩阵
仍是进入 4D-4B 前的人工门槛。完成报告见 `aot-stage-4d-4a-report.md`。

下一开发批仍为 4D-4B，且继续只处理快速访问状态查询、pin/unpin 和 STA 线程。它不复用
4D-4A 的导出去扩大语义，也不与托盘反射、XAML 或 AOT 运行阶段合并。

## 8. 参考资料

- [.NET COM interface source generation](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation)
- [RegisterDragDrop reference ownership](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-registerdragdrop)
- [RevokeDragDrop reference ownership](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-revokedragdrop)
