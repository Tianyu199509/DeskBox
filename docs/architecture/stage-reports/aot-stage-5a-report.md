# DeskBox Native AOT 阶段 5A 完成与复盘报告

- 报告日期：2026-08-21
- 阶段范围：x64 AOT 预览数据隔离、受审计产物启动、首次存活、单实例、托盘退出与重启
- 审计配置：profile 29 / summary schema 26
- 结论：5A 已完成，可以开放 5B-1；本报告不代表 AOT 已达到安装包或正式发布标准

## 1. 本阶段结果

阶段 5A 已把“能够生成 AOT 文件”推进到“能够在正式数据之外实际运行受审计的 AOT 主程序”。最终确认：

- AOT 主程序可在独立数据根首次启动并持续响应；
- 第二个 AOT 进程会退出并把激活转发给已有主实例；
- AOT 主实例可退出、重新启动，并继续读取同一隔离数据根；
- 托盘菜单可以显示，自动化调用真实“退出”菜单项后应用执行正常关闭链路；
- 正式 `%LOCALAPPDATA%\DeskBox` 在全部 AOT 运行前后保持相同元数据指纹；
- AOT 产物中的 `DeskBox.exe` 与 `deskbox_native.dll` 均和本次审计摘要中的哈希一致；
- 启动所需的 `DeskBox.pri`、Assets、WinUI/XAML 窗口、托盘、通知、拖放目标和默认 Widget 均已在实际 AOT 进程中建立。

本阶段没有继续清理 Binding，也没有开始 Rust `SearchCore`，普通 JIT 的正式发布路径保持不变。

## 2. 实现边界

### 2.1 AOT 专用数据根

`DeskBoxDataPathService` 现在按编译形态选择显式数据根：

| 构建形态 | 可读取的覆盖变量 | 未设置变量时 |
| --- | --- | --- |
| Debug | `DESKBOX_DEV_DATA_ROOT` | 正式默认路径 |
| Native AOT | `DESKBOX_AOT_PREVIEW_DATA_ROOT` | 正式默认路径 |
| 普通 Release JIT | 无 | 正式默认路径 |

这项设计没有把 preview 路径永久改成产品默认值。AOT 双击启动仍会遵循产品默认路径，因此内部预览必须通过受控脚本启动；未来正式切换为 AOT 时也不需要撤销数据目录语义。

### 2.2 受审计启动脚本

`scripts/start-aot-preview.ps1` 在启动前执行以下门禁：

1. 只接受最新 profile 29 / schema 26 的稳定 x64 Release 审计摘要；
2. 核对 AOT EXE 和 Rust DLL 的实际 SHA-256，并要求 Rust staging/publish 哈希一致；
3. 拒绝正式数据根及其父子重叠路径，也拒绝数据根与审计发布目录重叠；
4. 只停止可执行文件完整路径等于本次受审计 AOT EXE 的进程，不扫描或终止其他 DeskBox 构建；
5. 只在子进程启动期间设置 AOT 数据根，并清除可能继承的 Debug 数据根，随后恢复调用方环境；
6. 支持验证现有实例激活，并把产物、数据根、进程和正式目录指纹写入独立 `session.json`。

默认 preview 根按仓库路径生成，因此不同工作树不会默认共用测试数据。

## 3. 自动化与构建验证

| 项目 | 结果 |
| --- | --- |
| 5A 新契约红灯 | 9/9 按预期失败 |
| 5A 实施后契约 | 9/9 通过 |
| 5A + 数据路径隔离组合 | 20/20 通过 |
| x64 全量测试 | 2133/2133 通过 |
| 标准 Debug 构建 | 0 错误；24 条既有警告 |
| profile 28 旧摘要 | 启动前拒绝；未创建 AOT 进程 |
| 正式数据根作为 preview 根 | 启动前拒绝；未创建 AOT 进程 |
| PowerShell 语法 | Windows PowerShell 5.1 与 PowerShell 7 均通过 |

5A 契约覆盖数据根的编译期分支、profile/schema、正式路径拒绝、EXE/Rust 哈希、环境变量恢复、精确进程匹配、现有实例激活、正式目录指纹和项目/审计阶段标识。

## 4. 最终 x64 AOT 审计

| 项目 | 结果 |
| --- | --- |
| 审计耗时 | 222,175 ms |
| 源码在审计期间 | 稳定 |
| 发布目录 | 39 个文件，84,997,573 bytes |
| 符号目录 | 3 个文件，181,366,784 bytes |
| 警告代码 | CS0108、CS0169、CS0414、CS8601、CS8602、WMC1510 |
| WMC1506 / WMC1510 | 0 / 1216 |
| IL2026 / IL2050 / IL2072 / IL2075 / IL3050 | 均为 0 |
| 完整 `always-throw` | 0 |
| 5A 缺失模式 / 不安全模式 / 目标警告 | 均为 0 |
| Rust | ABI 2、能力 255、9 个必需导出，staging/publish 一致 |

关键文件：

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `DeskBox.exe` | 39,358,976 | `14FC0E323264801B8D7A1C474A8E071596FAE0034E2B18CB69D461CFAB977F2C` |
| `DeskBox.Updater.exe` | 2,020,352 | `B2DD1AE2F956171F3805532495C0EBFAE41DF3F0EA79EC124D4CEB34D9ACE101` |
| `deskbox_native.dll` | 146,944 | `E5D131FF19B07360D9252ED47E0FBE49A804C9B67B428A907E1559F2A61BADC5` |

资源文件的实际名称是 `DeskBox.pri`，不是通用示例中常见的 `resources.pri`。本次已按真实产物核对 `DeskBox.pri` 和 Assets。

## 5. 真实 AOT 运行证据

最终隔离根为：

`C:\Users\simon\AppData\Local\DeskBox-AotPreview\wingezi-B073278E-stage5a-final`

受审计可执行文件为：

`D:\project\wingezi\.artifacts\aot-audit\win-x64\publish\DeskBox.exe`

| 场景 | 结果 |
| --- | --- |
| 干净根首次启动 | PID 17420 持续运行并响应 |
| 单实例 | 第二进程 PID 41444 退出，主实例 PID 17420 保持运行 |
| 重启 | 新主实例 PID 33276 正常启动并读取同一隔离根 |
| 不与 Debug 共存的启动 | PID 41624；F7 注册、托盘宿主、通知、拖放目标和默认 Widget 均成功，日志无 error 1409 |
| 托盘正常退出 | PID 31388 显示真实 SecondWindow 菜单；UI Automation 调用“退出”后进程正常结束，日志记录 `ExitApplication invoked`、服务释放和热键注销 |

启动日志还确认 `OnLaunched completed successfully`、原生通知注册、OLE `IDropTarget` 注册、UI watchdog 和显示区域监视器启动。可见 WinUI/XAML Widget 窗口与托盘菜单均由实际 AOT 进程创建。

当 Debug 与 AOT 同时启动时，二者会竞争全局 F7，AOT 日志会出现预期的 error 1409；停止 Debug 后的独立 AOT 运行已确认 F7 正常注册。这不是 AOT 本身的启动缺陷，也说明后续人工矩阵不应让两种构建同时占用相同全局热键。

### 正式数据未被改写

正式根运行前后均为：

| 项目 | 运行前 | 运行后 |
| --- | ---: | ---: |
| 文件数 | 122 | 122 |
| 总字节数 | 303,016,768 | 303,016,768 |
| 元数据指纹 | `254EB84254707C6A61158F5038C59ED65A9B120157C320C0EBEA1FF0430EF7F0` | 相同 |

指纹算法为相对路径大写、长度和 UTC 最后写入 ticks，使用 ordinal 排序后计算 SHA-256。它用于证明本轮没有增删文件或改变这些元数据，不等于逐字节内容哈希。

## 6. 完成后的代码复盘

### 6.1 已核对的数据路径

全仓库现有 26 处 `DeskBoxDataPathService.Current` 使用点。应用自有设置、日志、搜索索引、Quick Capture、Todo、Glance、诊断、备份和恢复路径均经该服务取得根目录。

其余直接出现的 `LocalApplicationData` 用途不是应用数据旁路：

- `DragDropPermissionService` 检查已安装 EXE 的 AppCompat 项；
- `IconHelper` 搜索常见应用安装位置；
- `%UserProfile%\DeskBox` 是用户可管理的文件收纳目录或搜索根，不是应用元数据目录。

本轮没有发现会在 AOT 启动时绕过隔离根写入正式应用数据的代码路径。

### 6.2 审计中发现并修正的问题

最初的目录指纹使用 `Sort-Object FullName`，PowerShell 5.1 与 PowerShell 7 的文化排序可能产生不同结果。现已改为 `StringComparer.Ordinal`，并把算法版本写入 session；两个 PowerShell 宿主对同一正式目录得到相同哈希。对应契约与 AOT 审计门禁也已补齐。

### 6.3 尚未完成的范围

5A 只证明基础运行和数据隔离，不代表以下项目已经通过：

- 四个 Rust 产品边界在 AOT 进程中的完整行为；
- 设置、搜索、Quick Capture、Todo、天气、音乐、文件组件等完整功能矩阵；
- 多语言、主题、DPI、多屏和各类真实拖放交互的系统化回归；
- 安装、原位升级、更新器、卸载、回滚、签名、WACK 和受支持 Windows 版本；
- Rust DLL 的 `VCRUNTIME140.dll` 部署策略；
- 性能、内存和安装体积的 JIT/AOT 对照。

这些是阶段 5 后续批次和发布门槛，不是 5A 的遗漏实现。

## 7. 下一阶段建议

下一批调整为 **5B-1：shortcut AOT → Rust 真实边界冒烟**，复杂度为中等，范围保持单一：

1. 在隔离临时目录创建、读取和覆盖 `.lnk`；
2. 验证无 UI Resolve、损坏快捷方式、取消、修复和删除语义；
3. 需要 UI 的操作传入真实 owner HWND；
4. 确认 AOT 进程实际加载本次受审计的 `deskbox_native.dll`，结果与既有 JIT/C# oracle 冻结语义一致；
5. 全程不读取或修改正式 DeskBox 数据，也不改变用户 Quick Access 或系统音量。

之所以先做 shortcut，是因为它已经有最完整的 Rust ABI、差分和人工验证基础，能够最小成本证明“托管 AOT 调用 Rust”这一层，而 5A 启动本身未必触发 Rust DLL 加载。该批通过后，再依次开放 Explorer 启动与 Quick Access、音乐 getter/setter、完整托管 UI/功能矩阵，最后处理安装升级、回滚和 CRT 决策。

当前不建议恢复大规模 Binding 清理，也不建议先开始 Rust `SearchCore`。前者不是继续验证可运行 AOT 的最短路径，后者是独立的性能优化方向，不应阻塞主程序 AOT 的功能验收。
