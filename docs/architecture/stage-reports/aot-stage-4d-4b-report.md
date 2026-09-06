# DeskBox Native AOT 阶段 4D-4B 完成报告

- 日期：2026-08-21
- 范围：`ExplorerQuickAccessHelper` 查询、固定和取消固定
- 状态：代码、自动化、x64 AOT 产物审计已完成；写操作人工矩阵待执行

## 1. 阶段结论

4D-4B 已把 Native AOT 可达的 Quick Access ProgID/dynamic 链替换为完整 Rust 粗粒度边界。
这条链路适合由 Rust 完整持有一次调用中的 Shell Automation 对象，继续在 C# 中重建通用
`IDispatch::Invoke`、DISPID 和 VARIANT 参数栈会扩大改动面。实现仍保持双后端：普通 JIT
默认使用原 C# oracle，显式 Rust JIT 和 Native AOT 使用 Rust；Rust 失败时不静默回退 C#。

本批没有修改 Explorer 托管启动、shortcut、音乐音量、搜索、XAML 或托盘功能，也没有把
Quick Access 写操作加入自动化。AOT 主程序尚未启动。

## 2. 冻结并保留的行为

1. `ExplorerQuickAccessHelper` 的公开同步 API 与后台 STA 异步包装保持不变；
2. 查询仍返回 `Unknown`、`NotPinned`、`Pinned` 三态；目录不存在返回 `NotPinned`；
3. 属性读取只接受旧实现等价的 bool、整数和可解析字符串，属性或类型异常返回成功的
   `Unknown`，不会错误映射成 `NotPinned`；
4. 固定前仍由 C# 规范化并创建目录，Rust 只执行父目录 `NameSpace`、`ParseName` 和
   `pintohome`；
5. 取消固定前仍先查询状态，`NotPinned` 直接成功；匹配项目执行 `unpinfromhome`，未匹配时
   保留父目录回退；匹配项 verb 失败时不改走回退；
6. 路径比较保持忽略大小写和末尾目录分隔符；单个坏项目不会中止整个查询；
7. COM 初始化、接口、集合、项目、BSTR 和 VARIANT 均在同一 Rust 调用内释放，不跨线程或
   ABI 保存。

完整行为和布局见 `quick-access-native-abi-v1.md`。

## 3. 实现与 ABI

- 新 Rust 模块：`native/deskbox-native/src/quick_access.rs`；
- 新托管后端：`src/DeskBox/Helpers/QuickAccessNativeBackend.cs`；
- 新导出：`deskbox_quick_access_v1`；
- 模块 ABI：2；结构版本：1；
- 新能力位：`1 << 7`；完整能力掩码：255；
- 当前发布必需导出：9 个；
- x64 请求/结果尺寸：96/112 字节；
- AOT 使用 `DESKBOX_NATIVE_AOT` 完整排除 C# ProgID/dynamic oracle；
- JIT 可用 `DESKBOX_QUICK_ACCESS_BACKEND=rust` 显式选择 Rust，失败不回退。

Rust 使用 `IShellDispatch`、`Folder`、`FolderItems`、`FolderItem`、`FolderItem2` 的强类型投影，
没有手写通用 Automation 分派器，也没有和 4D-4A 共用操作导出来暗中扩大旧 ABI。

## 4. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧代码红线契约 | 3/3 按预期失败 |
| `cargo fmt --check` | 通过 |
| Clippy `-D warnings` | 通过 |
| Rust 单元测试 | 52/52 |
| 4D-4B 契约测试 | 12/12 |
| AOT/Explorer/shortcut/music 扩大定向测试 | 89/89 |
| DeskBox x64 全量测试 | 2061/2061 |
| x64 AOT 审计 | 配置 21 / schema 18，通过 |

首轮完整 AOT 审计用时约 147.4 秒，产生 39 个发布文件和 3 个分离 PDB；发布文件约
84,952,205 字节，符号约 181,178,368 字节。实际 DLL 返回 ABI 2、能力 255 和 9 个必需
导出，staging/publish SHA-256 一致。两个 Quick Access 目标文件告警、Quick Access
`always-throw` 和完整 `always-throw` 均为 0；原始 IL2026、IL2072、IL3050 均为 0。

剩余原始分析项只包括托盘 IL2075 9、WMC1506 6、WMC1510 1265，以及普通 C# 编译警告；
它们不属于 Quick Access 边界。

真实发布 DLL 还完成一次只读系统查询，结果为返回码 0、status 0、成功 1、命中 1、
`Pinned`、阶段 `0x7F`、HRESULT 0，请求/结果尺寸为 96/112。该探针没有调用 pin 或 unpin。

## 5. 完成后审计

代码与文档复盘未发现需要扩大 4D-4B 的遗漏：

1. AOT 编译单元已不包含 Quick Access 的 `Type.GetTypeFromProgID`、
   `Activator.CreateInstance`、dynamic 或 `Marshal.ReleaseComObject`；
2. 普通 JIT 默认后端与公开调用链未变，显式 Rust/AOT 失败不会执行另一套后端；
3. 托管与 Rust 双侧校验尺寸、版本、操作、字符串长度、嵌入 NUL、reserved、状态、布尔值、
   pin state 和阶段位；
4. 构建脚本、头文件、托管加载器、Rust 模块和 AOT 审计统一要求能力 255 与九个导出；
5. 4D-4A、音乐音量和 shortcut 的结构及能力位没有被修改；
6. 自动化只读取真实系统状态，没有改变用户 Explorer 配置。

## 6. 尚未由自动化证明的边界

以下操作会改变用户 Quick Access 状态，保留为明确人工矩阵：

- 固定未固定目录、重复固定；
- 取消固定、重复取消固定；
- Explorer 窗口刷新或重启后的状态一致性；
- 无权限、Shell namespace 暂时不可用及 verb 失败后的错误与恢复；
- 显式 Rust JIT 与既有 C# oracle 的可见行为差分。

这些项目不阻止 4D-4B 的结构/AOT 审计完成，但在阶段 5 AOT 预览前必须补齐。

## 7. 下一阶段建议

下一阶段建议为 **4D-5 托盘反射收口**，不使用 Rust，并拆成两个小步骤：

1. **4D-5A 托盘 identity 强类型化，复杂度低。** 当前 H.NotifyIcon.WinUI 已公开
   `TaskbarIcon.TrayIcon`，其 `WindowHandle` 和 `Id` 也是公开成员，可直接替换三层反射并保留
   图标矩形、菜单定位和 fallback；
2. **4D-5B 第二窗口 flyout，复杂度中等。** `ContextMenuFlyout` 仍是第三方私有属性。先以
   契约和 Debug 人工测试冻结右键菜单位置、主题和 presenter 样式，再选择公开事件/API 重构
   或最窄的兼容保留。不能为整个第三方程序集增加宽泛 trimming root。

4D-5 完成后，4D 的 COM/dynamic/trimming 数据流批次即可收口，再进入 4E 的 XAML 页面分批。
阶段 5 才启动 AOT 主程序并执行完整功能、安装升级和回滚矩阵。

## 8. 后续状态

上述 4D-5 已于同日按公开 API 路线完成：托盘 identity 使用公开强类型属性，SecondWindow
菜单使用公开打开事件和 WinUI 视觉树，不再访问私有 `ContextMenuFlyout`。配置 22 /
schema 19 已将 IL2075 从 9 清零；Rust 模块仍为 ABI 2、能力 255 和九个必需导出。

完成后对 XAML 告警的重新分组把下一批调整为 4E-0：先将搜索历史 6 条不可变
`OneWay x:Bind` 改为 `OneTime`，单独清零 WMC1506，再开始 WMC1510 页面 pilot。完整证据见
`aot-stage-4d-5-report.md`。
