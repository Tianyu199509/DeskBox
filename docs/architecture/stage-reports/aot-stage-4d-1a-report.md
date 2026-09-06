# DeskBox AOT 阶段 4D-1A 完成与复盘报告

- 日期：2026-08-21
- 范围：三个低风险类型化入口、审计门禁和下一批边界
- 证据等级：代码审查、自动化测试、x64 Native AOT 编译与产物审计；未启动 AOT 产物

## 1. 完成结论

4D-1A 已完成，没有发现范围内遗漏。本批消除了三个目标文件中的 AOT 分析警告，未改变
功能入口、Rust ABI、COM 边界、JSON 格式或 XAML Binding。技术选择遵循按边界判断的原则：
纯托管类型信息直接使用强类型 C#；只有完整原生边界明显更简单、生命周期可以完全留在
单次调用内部时，才优先整体使用 Rust。

## 2. 实现内容

| 文件 | 旧实现 | 当前实现 | 行为边界 |
| --- | --- | --- | --- |
| `Win32Helper.cs` | `Marshal.SizeOf(Type)` | `Marshal.SizeOf<DispatcherQueueOptions>()` | 结构尺寸和值不变 |
| `MarkdownDocumentView.cs` | 按类型名和属性名反射 Markdig task-list | 公开 `TaskList.Checked` | checked、unchecked 和非任务列表返回语义不变 |
| `SearchPopupWindow.xaml.cs` | 匿名 `{ Title }` 与运行时属性反射 | 已有 `SearchRecommendationItem` | 收藏/历史标题、最多 8 条历史和点击应用查询不变 |

搜索推荐 DTO 同时写入 `Title` 与 `HistoryQuery`，并区分 `Favorite`、`History` 类型；点击处理
只接受这个明确模型。没有新建重复 DTO，也没有把这类简单托管映射扩展到 Rust。

## 3. 验证结果

- 新增 4 条 4D-1A 契约测试，先在旧实现上 4/4 按预期失败；
- 4D-1A、Markdown、AOT 与音乐音量相关定向测试 49/49 通过；
- 规范 x64 全量测试 2010/2010 通过；
- PowerShell 审计脚本通过语法解析；
- 配置 15 / schema 12 的隔离 x64 Native AOT 审计通过，发布目录 39 个文件、约
  83.3 MiB，symbols 目录 3 个 PDB、约 182.2 MiB；
- JSON 默认反射保持关闭；Rust 模块保持 ABI 2、能力 63、七个必需导出，staging 与 publish
  DLL 哈希一致；
- 4D-1A 三个目标文件的 AOT 分析警告为 0，未知警告代码为 0，完整 `always-throw` 为 0。

本轮审计的原始剩余计数为 IL2026 44、IL2050 4、IL2072 4、IL2075 13、IL3050 77、
WMC1506 6、WMC1510 1265。原始次数会受重复分析通道影响，不能把减少的条数直接当作唯一
完成证据；目标文件零警告和警告集合不扩张才是本批稳定门禁。

## 4. 复盘结果

源码回扫未发现三类旧模式残留。`MarkdownDocumentView` 的 `partial` 声明是工作树中已有的
其他修改，本批只增加 Markdig task-list 强类型访问，没有覆盖该修改。共享工作树中的其他
未提交内容与发布产物均未改动或清理。

仍需人工确认的直接交互包括搜索收藏/历史推荐点击，以及 Markdown 中未勾选/已勾选任务的
显示：推荐标题、点击后查询文本和结果、两种任务状态都应与原行为一致。DispatcherQueue
路径已由规范 Debug 实例成功启动覆盖。自动化和 AOT 产物审计不能替代前两项实际 UI 检查，
也不能证明 AOT 主程序已经可运行。

## 5. 下一阶段

下一开发批冻结为 **4D-1B 应用内反射收口**：

1. Quick Capture 的 XAML 初始化失败诊断改为固定异常字段，不再枚举异常对象的所有属性；
2. `Localized` 改为有限强类型映射。当前源码使用面为 152 个 `SettingsCard`、19 个
   `SettingsExpander` 和 2 个 `TextBox`；共 301 个 Header/Description attached-property
   标记，不存在第四种目标类型；
3. 新增源码清单门禁，防止以后增加新目标类型却未同步类型映射；
4. 执行 x64 全量测试、递增配置/schema 的隔离 AOT 审计，并人工验证设置页切换语言后
   card、expander 和两个 TextBox header 都会刷新。

4D-1B 不使用 Rust，因为它只处理异常文本和 WinUI 控件属性。其后进入 **4D-2
`IFileOperation` Rust 原生边界**：现有 API 只有回收站删除和批量移动两种同步粗粒度操作，
没有 progress sink 回调或 COM 对象跨 ABI，适合让 Rust 完整持有 COM 激活、Shell item 和
释放生命周期。OLE DropTarget 有反向回调、数据对象和窗口生命周期，应继续作为独立批次，
不能与 `IFileOperation` 一并强行 Rust 化。

后续状态：4D-1B 已按上述范围完成。完成后全仓库引用审计确认 `FileOperationHelper` 没有
调用者，因此 4D-2 已调整为删除死代码并随后完成，没有为这段未使用实现扩展 Rust。规范
x64 全量测试 2016/2016 和配置 17 / schema 14 隔离审计通过。随后 4D-3A 已完成 OLE
数据读取侧，x64 全量测试 2027/2027 和配置 18 / schema 15 审计通过；人工拖放矩阵通过后
再进入 4D-3B。当前结论以 `aot-stage-4d-3a-report.md` 和总路线为准。

后续 4D-3B 已完成，配置 19 / schema 16 将 OLE 注册侧 IL2050 清零，x64 全量测试
2036/2036 通过。当前结论以 `aot-stage-4d-3b-report.md` 和总路线为准。
