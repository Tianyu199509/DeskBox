# DeskBox Native AOT 阶段 4E-1 完成报告

- 日期：2026-08-21
- 范围：四个低风险叶子 XAML 中的 7 条 WMC1510
- 状态：代码、契约、x64 全量测试和 AOT 产物审计已完成；Debug 可见行为待人工确认

## 1. 阶段结论

4E-1 将四个生命周期明确的叶子绑定合并为一批处理：

- `PinStateIcon.xaml`：2 条 `Foreground` 自引用；
- `MarkdownSourceEditor.xaml`：3 条控件自身 DependencyProperty；
- `DesktopOrganizationTaskView.xaml`：1 条路径文本自身 Tooltip；
- `DesktopOrganizationSettingsSection.xaml`：1 条规则路径自身 Tooltip。

7 条传统 Binding 均改为 `Mode=OneWay` 的 typed `x:Bind`。WMC1510 从 1265 降至 1258，
WMC1506 继续为 0。四个目标 XAML 已不再包含传统 Binding；全仓库 `{Binding}` 从 1,349
降至 1,342，`{x:Bind}` 从 28 增至 35，使用传统 Binding 的 XAML 文件从 24 个降至 20 个。

本批没有修改 ViewModel、业务逻辑、依赖属性定义、Rust 模块、COM 边界或 AOT 运行策略。
AOT 主程序仍未启动。

## 2. 合并范围与延期项

这次没有按“告警少”机械扩大范围。以下三个同样只有少量告警的绑定被明确延期：

- `App.xaml` 中 `SegmentHeight/SegmentTextSize` 位于全局 Style Setter，不能直接套用页面
  code-behind 的 `x:Bind` 生命周期；
- `ContentWidgetWindow.xaml` 的 `DisplayName` 来自构造后才设置的运行时 DataContext 和私有
  `INotifyPropertyChanged` 类型，直接改为 `_titleViewModel` x:Bind 会涉及初始化顺序。

审计脚本冻结这三个旧绑定仍然存在，防止 4E-1 在未设计生命周期的情况下顺手改动。

## 3. 生命周期证据

四组绑定都保留 OneWay 动态更新，不是只满足编译器：

1. `PinStateIcon.Foreground` 是 `Control.ForegroundProperty`；生成代码通过
   `RegisterPropertyChangedCallback` 订阅，并同时更新两个 Path；
2. `MarkdownSourceEditor.EditorFontSize/IsReadOnly/PlaceholderText` 都是该控件自身的
   DependencyProperty；生成代码分别注册属性变化回调；
3. 两个路径 Tooltip 都从命名 TextBlock 的 `TextProperty` 取值；生成代码会在
   `StoragePathText.Text` 或 `RuleDetailPath.Text` 变化时同步 Tooltip；
4. `PinStateIcon.IsPinnedProperty`、Markdown 三个属性定义及两处路径赋值入口均由契约冻结，
   没有用手工复制值替代现有生命周期。

## 4. 审计门禁

AOT 审计升级为 profile 24 / schema 21，并增加以下硬门禁：

1. 6 种旧 Binding 模式在各自目标文件中必须为 0；
2. 6 种 typed x:Bind 模式的精确数量合计必须为 7；
3. 依赖属性和路径刷新入口必须保留；
4. Style Setter 与运行时 DataContext 三个延期绑定不得混入本批；
5. 四个目标 XAML 的 AOT 告警必须为 0；
6. WMC1510 必须精确为 1258，WMC1506 继续全局为 0；
7. 非预期警告代码与完整 `always-throw` 必须为 0。

## 5. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 6 失败 / 2 通过，符合预期 |
| 4E-1 契约 | 8/8 |
| 4E-0 + 4E-1 + AOT 发布契约 | 34/34 |
| 受影响既有契约 + 4E-1 | 11/11 |
| DeskBox x64 全量测试 | 2081/2081 |
| PowerShell 语法解析 | 0 错误 |
| x64 AOT 审计 | profile 24 / schema 21，通过 |

第一次全量测试为 2080/2081，唯一失败是既有 `ItemHoverActionContractTests` 仍断言
`PinStateIcon` 的旧 Binding 文本。该契约更新为 typed x:Bind 后，受影响定向 11/11、全量
2081/2081 通过；没有发现产品行为失败。

成功 AOT 审计用时约 331.0 秒，源码指纹前后一致；产生 39 个发布文件，共 84,962,141
字节，以及 3 个分离 PDB，共 181,227,520 字节。Rust DLL 保持 ABI 2、能力掩码 255、
九个必需导出，staging/publish SHA-256 一致。

最终告警代码只剩 `CS0108`、`CS0169`、`CS0414`、`CS8601`、`CS8602` 和 `WMC1510`：

- WMC1506：0；
- WMC1510：1258；
- 4E-1 旧 Binding、缺失 compiled binding、缺失生命周期或延期范围变化：0；
- 4E-1 目标 XAML 告警：0；
- 非预期告警：0；
- 完整 always-throw：0。

## 6. 完成后复盘

代码、生成代码、测试与 AOT 摘要交叉检查后，没有发现需要扩大 4E-1 的遗漏：

1. 产品差异只有 7 个 binding expression，没有修改 code-behind；
2. 生成代码确认 OneWay 订阅真实 DependencyProperty，不依赖反射属性路径；
3. 四个目标文件的 7 条 WMC1510 全部消失，其他页面计数没有意外变化；
4. WMC1506 和全部 4D/4E-0 门禁继续通过；
5. Rust ABI、能力、导出和普通 JIT 后端策略均未变化；
6. 没有使用 bindable attribute 或 `x:SuppressXamlTrimWarnings` 掩盖告警。

自动化没有代替真实 UI。Debug 启动后仍需人工确认：Quick Capture 置顶/取消置顶图标在主题和
前景色变化后仍正确；Markdown 源编辑器的字体、只读状态和占位文本会动态更新；桌面整理路径和
规则路径变化后 Tooltip 与当前文本一致。

## 7. 下一阶段建议

下一阶段建议为 **4E-2 两个自有 DependencyProperty 叶子控件批次**：

- `MusicTransportIcon.xaml`：7 条同型 `Foreground` 自引用，复杂度低；
- `WidgetInlineEditor.xaml`：8 条控件自身属性绑定，其中 7 条 OneWay、1 条 Text TwoWay，
  整体复杂度低到中。

若两者同批完成，目标为 WMC1510 1258→1243。`MusicTransportIcon` 可以直接复用本阶段已经
验证的 Foreground compiled-binding 模式；`WidgetInlineEditor` 需要额外冻结 TwoWay 文本的
即时回写、保存和取消语义。若 TwoWay x:Bind 不能保持 `UpdateSourceTrigger=PropertyChanged`
行为，则 4E-2 先完成音乐图标的 7 条，把编辑器拆为独立后续批次，不用手工事件掩盖语义差异。

## 8. 后续状态

4E-2 已按完整 15 条范围完成。生成代码确认 TwoWay Text 使用 `TextBox.TextProperty` 即时回写，
无需拆分或增加手工事件；profile 25 / schema 22 审计将 WMC1510 精确降至 1243，x64 全量
2090/2090 通过。后续范围与证据以 `aot-stage-4e-2-report.md` 为准。
