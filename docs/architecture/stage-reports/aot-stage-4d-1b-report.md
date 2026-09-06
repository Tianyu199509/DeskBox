# DeskBox AOT 阶段 4D-1B 完成与复盘报告

- 日期：2026-08-21
- 范围：Quick Capture 固定异常诊断、`Localized` 强类型映射、AOT 零告警门禁
- 证据等级：源码清单、自动化测试、x64 Native AOT 编译与产物审计；未启动 AOT 产物

## 1. 完成结论

4D-1B 已完成，范围内没有发现遗漏。本批删除了两条应用内反射链，没有修改 COM、Rust ABI、
JSON、XAML Binding 或正常业务流程。AOT 审计中的两个目标文件告警为 0，原始 IL2075 计数
由 13 降为 9。

本批使用强类型 C#，不使用 Rust。异常字段读取和 WinUI 控件属性赋值都不是原生边界；为它们
增加 Rust 调用会扩大 ABI 和错误面，无法简化生命周期。

## 2. 实现内容

### Quick Capture 初始化失败诊断

旧实现通过 `GetProperties()` 和 `GetValue()` 枚举异常对象的所有公开属性。当前改为固定记录：

- 异常完整类型名；
- 十六进制 HRESULT；
- Message；
- InnerException；
- StackTrace。

日志前缀和捕获后重新抛出行为不变，不吞掉 XAML 初始化失败。固定字段避免 trimming 对任意
运行时异常类型公共属性的保留要求。

### Localized 强类型映射

Header/Description 不再使用 `target.GetType().GetProperty(...)`。源码 XAML 清单确认并冻结为：

| 控件 | HeaderKey | DescriptionKey |
| --- | ---: | ---: |
| `SettingsCard` | 152 | 122 |
| `SettingsExpander` | 19 | 6 |
| `TextBox` | 2 | 0 |

总计 301 个 attached-property 标记。当前实现对三类控件直接设置公开属性；未来出现未映射
类型时保持不抛异常并输出 Debug 诊断，同时源码清单测试会先失败，要求显式评审映射。

## 3. 验证结果

- 新增 3 条 4D-1B 契约，旧实现上 3/3 按预期失败；
- AOT/4D 相关定向测试 39/39 通过；
- Quick Capture、Localization 和 4D-1B 扩大定向测试 205/205 通过；
- 规范 x64 全量测试 2013/2013 通过；
- PowerShell 审计脚本通过语法解析；
- 配置 16 / schema 13 的隔离 x64 AOT 审计通过：39 个发布文件、约 83.3 MiB，3 个分离
  PDB、约 182.2 MiB；
- 4D-1A 与 4D-1B 目标文件警告均为 0，未知警告为 0，完整 `always-throw` 为 0；
- JSON 默认反射仍为关闭；Rust 保持 ABI 2、能力 63、七个导出，staging/publish 哈希一致；
- 审计前后源码指纹一致。

剩余原始警告计数为 IL2026 44、IL2050 4、IL2072 4、IL2075 9、IL3050 77、WMC1506 6、
WMC1510 1265。原始次数包含重复分析通道，批次完成以目标文件零告警、警告代码集合不扩张
和 `always-throw=0` 为准。

## 4. 人工验证边界

自动化无法替代设置页真实语言切换。规范 Debug 实例启动后仍需确认：

1. 普通 `SettingsCard` 的 Header/Description 随语言切换刷新；
2. `SettingsExpander` 展开项的 Header/Description 同步刷新；
3. 文件堆自定义规则中的“名称”和“扩展名”两个 TextBox Header 同步刷新。

Quick Capture 的固定诊断只在 XAML 初始化失败时触发，不应为了验证日志主动制造产品启动
失败；源码契约与 AOT 目标文件门禁是本阶段对该失败路径的验证边界。

## 5. 完成后复盘与下一阶段调整

复盘发现此前把 4D-2 规划成 `IFileOperation` Rust 边界的前提不成立：

- `FileOperationHelper.cs` 是已跟踪源码，但 `FileOperationHelper`、`DeleteToRecycleBin` 和
  `MoveItemsWithProgress` 在该文件之外没有任何产品或测试引用；
- 实际产品的回收站删除、Shell 进度移动、超时等待和完成判断位于 `FileService`，使用另一套
  `SHFileOperation`/托管实现；
- 当前两个 IL2050 只来自未使用 helper 的 `CoCreateInstance` 和
  `SHCreateItemFromParsingName` COM marshalling。

因此下一开发批调整为 **4D-2 死代码删除**，不再扩展 Rust：

1. 先增加零调用与真实 `FileService` 入口契约；
2. 删除 `FileOperationHelper.cs`，不修改实际文件操作路径；
3. 要求隔离 AOT 审计中对应两处 IL2050 消失；
4. 执行文件服务定向测试、x64 全量测试和隔离 AOT 审计。

该批实现复杂度低、行为风险低。为没有调用者的 helper 建立 Rust ABI、加载器、JIT oracle 和
差分矩阵会增加维护成本，不能产生产品收益。OLE DropTarget 仍包含 Shell 到托管的反向回调、
数据对象和窗口生命周期，不与 4D-2 合并；后续 `Shell.Application` dynamic 才需要重新判断
是否适合完整 Rust 原生边界。

## 6. 后续状态

4D-2 已按上述调整完成：删除 `FileOperationHelper.cs`，没有修改 `FileService`；规范 x64
全量测试 2016/2016 和配置 17 / schema 14 隔离 AOT 审计通过。对应两处
`IFileOperation`/`IShellItem` IL2050 已消失，原始 IL2050 从 4 降为 2，Rust ABI 仍为 2。

下一阶段改为 4D-3 OLE `NativeDropTarget`。复盘确认不能只修改 `RegisterDragDrop` 参数；还要
迁移 `IDataObject`、`IStream` 和 `Marshal.GetObjectForIUnknown`。当前计划先做数据对象读取侧
4D-3A，再做源生成 `IDropTarget` 注册/回调侧 4D-3B。4D-3A 现已完成自动化与配置 18 /
schema 15 AOT 审计，开始 4D-3B 前仍需真实拖放人工矩阵；最新结论以
`aot-stage-4d-3a-report.md` 和总路线为准。

后续人工矩阵已确认通过，4D-3B 也已完成。配置 19 / schema 16 确认生成式 COM 注册边界
零告警并将 IL2050 清零，x64 全量测试 2036/2036 通过。最新结论以
`aot-stage-4d-3b-report.md` 和总路线为准。
