# DeskBox Native AOT 阶段 4E-4 完成与复盘报告

- 日期：2026-08-21
- 范围：`FileWidgetSettingsSection` typed ViewModel 桥接及 5 条 Binding
- AOT 审计：profile 27 / schema 24
- 结果：WMC1510 1229→1224，NativeAOT/Rust 结构审计通过

## 1. 范围与决策

4E-4 只处理 `FileWidgetSettingsSection.xaml` 中 5 条运行时 Binding：

1. 文件堆设置摘要；
2. 文件堆模式选项；
3. 文件堆模式 TwoWay 选择；
4. 文件夹打开方式选项；
5. 文件夹打开方式 TwoWay 选择。

子控件原来在 `SettingsWindow.InitializeComponent()` 完成后继承 `SettingsRoot.DataContext`。这类来源
不能直接按控件自身属性替换为 `x:Bind`，因此本批增加显式 `SettingsViewModel` DependencyProperty
桥接，不改写 SettingsViewModel、SettingsComboBox 或设置持久化逻辑。

## 2. 实现结果

`FileWidgetSettingsSection` 新增可空 `ViewModel` DependencyProperty。父窗口在
`SettingsRoot.DataContext = ViewModel` 后显式赋值，并在 `ViewModel.Dispose()` 前清空：

- 摘要和两组选项改为 3 条 OneWay typed `x:Bind`；
- 两个 `SettingsComboBox.Value` 保持 2 条 TwoWay typed `x:Bind`；
- 旧 `{Binding ...}` 在目标文件中降为 0；
- 语言切换、选项重建、文件堆摘要、选择归一化、低优先级选中项同步和设置保存仍使用既有链路。

第一次生成代码核对还发现，WinUI 已自动监听 `ViewModelProperty`。因此删除了最初加入的手动
`Bindings.Update()` 回调，避免依赖属性变化时重复刷新。

## 3. 生成代码证据

Debug 生成的 `FileWidgetSettingsSection.g.cs` 确认：

1. 对 `ViewModelProperty` 自动调用 `RegisterPropertyChangedCallback` 和
   `UnregisterPropertyChangedCallback`；
2. ViewModel 切换时先移除旧实例的 `PropertyChanged`，再订阅新实例；
3. 5 个叶子属性分别生成精确的 PropertyChanged 分支；
4. 两个 ComboBox 分别监听 `SettingsComboBox.ValueProperty`，并回写
   `SelectedFileStackMode` 与 `SelectedFileWidgetFolderOpenBehavior`；
5. ViewModel 为空时回写链带有空值保护；
6. 产品代码不包含额外的 `Bindings.Update()` 或手写 ViewModel 事件订阅。

这证明本批不是只消除编译器警告，原有 TwoWay 用户输入和关闭解绑语义也进入了编译绑定链。

## 4. 审计门禁

`publish-aot-audit.ps1` 升级到 profile 27 / schema 24，并增加以下门禁：

1. 5 类旧 Binding 必须为 0；
2. 5 条 typed `x:Bind` 的模式和数量必须精确匹配；
3. ViewModel DependencyProperty、父窗口赋值和清空模式必须存在；
4. 父窗口必须先建立根 DataContext，再赋值子桥接，并在 ViewModel dispose 前清空；
5. 冗余手动 Binding 刷新必须为 0；
6. ViewModel 通知、附加属性选择同步和设置持久化行为模式必须保留；
7. `App.xaml`、`ContentWidgetWindow` 和 `SearchResultRowControl` 的 11 条延期 Binding 必须保留；
8. 目标源告警、非预期告警和完整 `always-throw` 必须为 0；
9. WMC1510 必须精确为 1224。

4E-3 的 1229 同时调整为历史上限门禁，允许当前和后续批次继续下降，但不允许回升。

## 5. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 8 失败 / 3 通过，符合预期 |
| 4E-4 契约 | 11/11 |
| AOT/4D/4E 扩大定向契约 | 124/124 |
| DeskBox x64 全量测试 | 2112/2112 |
| canonical Debug 构建 | 0 错误，24 条既有警告 |
| PowerShell 语法解析 | 0 错误 |
| x64 AOT 审计 | profile 27 / schema 24，通过 |

最终隔离 AOT 审计用时 209,155 毫秒，源码指纹前后一致；产生 39 个发布文件，共
84,994,765 字节，以及 3 个分离 PDB，共 181,358,592 字节。三个本地产物均为 x64 PE：

- `DeskBox.exe`：39,355,904 字节；
- `DeskBox.Updater.exe`：2,020,352 字节；
- `deskbox_native.dll`：146,944 字节。

Rust DLL 保持 ABI 2、能力掩码 255、9 个必需导出，staging/publish SHA-256 一致。

最终分析结果：

- WMC1506：0；
- WMC1510：1224，分布在 16 个 XAML 文件；
- 4E-4 旧绑定、缺失 compiled binding、缺失桥接、错误生命周期顺序、冗余手动刷新、缺失行为、
  延期范围变化和目标源告警：0；
- 非预期告警：0；
- 完整 `always-throw`：0。

## 6. 剩余 WMC1510 分布

| 文件 | 数量 |
| --- | ---: |
| `App.xaml` | 2 |
| `FileItemSurface.xaml` | 36 |
| `SearchResultRowControl.xaml` | 8 |
| `FileSurfaceContent.xaml` | 92 |
| `GlanceWidgetContent.xaml` | 82 |
| `MusicWidgetContent.xaml` | 118 |
| `QuickCaptureSurfaceContent.xaml` | 82 |
| `TodoWidgetContent.xaml` | 135 |
| `WeatherWidgetContent.xaml` | 126 |
| `WidgetShell.xaml` | 15 |
| `ContentWidgetWindow.xaml` | 1 |
| `QuickCaptureWidgetWindow.xaml` | 137 |
| `AppearanceSettingsSection.xaml` | 25 |
| `CapsuleModeSettingsSection.xaml` | 33 |
| `GlanceWidgetSettingsSection.xaml` | 11 |
| `SettingsWindow.xaml` | 321 |

## 7. 完成后复盘

代码、生成代码、调用链、契约和 AOT 摘要交叉检查后，没有发现 4E-4 范围内遗漏：

1. 5 条下降值与目标 Binding 数量一致，目标文件从剩余 WMC1510 清单中消失；
2. 两个 TwoWay 回写均由生成代码监听真实附加 DependencyProperty；
3. 语言切换会重新通知两组选项，SettingsComboBox 仍在 ItemsSource 和 Value 变化后排队同步选择；
4. 文件堆摘要和选择通知仍覆盖启用状态、分组变化及本地化刷新；
5. 父窗口关闭时先清空桥接，再停止父绑定、清空 DataContext 并 dispose ViewModel；
6. 没有修改 Rust 源码、ABI、能力、导出或普通 JIT/AOT 后端策略。

自动化和生成代码不能替代可见 UI 验证，仍需人工确认：

- 设置页初次打开时两组选项和文件堆摘要正确；
- 修改文件堆模式后摘要、选择和重开后的持久化正确；
- 修改文件夹打开方式后选择和重开后的持久化正确；
- 切换语言后选项文本重建但选中值不丢失；
- 关闭并重开设置窗口后没有旧 ViewModel 回调或选择跳动。

## 8. 下一阶段建议

下一批建议保持为 4E-5 `SearchResultRowControl` typed Item 桥接，单独处理 8 条，目标为
WMC1510 1224→1216，复杂度中高：

1. 父级 `ResultsRepeater` DataTemplate 声明 `SearchResultItem` 类型，并显式把当前条目传给行控件；
2. 行控件增加 typed Item DependencyProperty，8 条内部绑定改为 compiled binding；
3. Title、Subtitle、DisplayGlyph 和 TypeDisplay 随 Item 切换更新；
4. Icon、SizeDisplay 和 DateDisplay 仍保留 `ElementPrepared`、异步完成后的引用一致性检查及
   `RefreshIconVisuals` 手工覆盖，不给 SearchResultItem 增加伪 PropertyChanged；
5. 先用真实 XAML 编译验证 public typed Item 是否触发 required-member activator；如果会触发，
   再采用 object 传输加控件内类型投影，不能为了消除 8 条警告重做搜索模型；
6. 冻结 ItemsRepeater 回收、同对象重新 prepare、单选/多选视觉、列显隐和关闭解绑。

`App.xaml` 的两个 Style Setter 与 `ContentWidgetWindow` 的一个运行时 DataContext 绑定继续冻结。
4E-5 完成后建议暂停微型 Binding 批次，进入阶段 5，首次启动隔离 AOT 主程序并执行真实功能矩阵。

4E-5 已在随后批次完成。真实 XAML 编译确认 public typed Item 会触发 required-member
activator，因此最终采用 internal typed Item 投影、每次 ElementPrepared 调用
`Bindings.Update()` 和 8 条 OneTime compiled binding；WMC1510 已降至 1216。完整记录见
[`aot-stage-4e-5-report.md`](aot-stage-4e-5-report.md)。当前下一步为阶段 5A 的 x64 AOT 隔离启动。
