# DeskBox Native AOT 阶段 4E-3 完成报告

- 日期：2026-08-21
- 范围：`AttachmentTileStrip` 与 `SearchPopupWindow` 中 14 条 DataTemplate WMC1510
- 状态：代码、生成代码、契约、x64 全量测试和 AOT 产物审计已完成；Debug 可见行为待人工确认

## 1. 阶段结论

4E-3 已按冻结范围完成，共移除 14 条传统 Binding：

- `AttachmentTileStrip.xaml`：为 `TodoAttachmentViewModel` 模板增加 `x:DataType`，7 条绑定全部改为
  OneWay typed `x:Bind`；
- `SearchPopupWindow.xaml`：为 Tab、推荐应用、收藏和最近搜索四个模板增加明确类型。Tab 的 `Count`
  保持 OneWay；`Glyph`、`DisplayName`、`Icon`、`AppDisplayName` 和两条 `Title` 共 6 条使用
  OneTime typed `x:Bind`。

WMC1510 从 1243 精确降至 1229，两个目标 XAML 的 AOT 告警均为 0。全仓库传统 `{Binding}`
从 1,327 降至 1,313，`{x:Bind}` 从 50 增至 64，使用传统 Binding 的 XAML 文件从 18 个降至
16 个，`x:DataType` 声明从 1 个增至 6 个。

本批没有修改两个目标的 code-behind、ViewModel/DTO、Rust 源码、ABI、COM 边界或后端策略，
也没有启动 AOT 主程序。

## 2. 绑定生命周期与生成代码证据

本批按数据生命周期选择绑定模式，没有把所有模板机械改成 OneWay。

### 2.1 附件模板

`TodoAttachmentViewModel` 继续继承 `ObservableObject`。Debug 生成代码确认：

1. 模板数据根被强类型转换为 `TodoAttachmentViewModel`；
2. 生成绑定订阅该对象的 `INotifyPropertyChanged`，并为 `DisplayName`、`Glyph`、`Thumbnail`、
   `ThumbnailVisibility` 和 `FileIconVisibility` 生成独立更新分支；
3. `Thumbnail` setter 仍在图片加载成功后通知自身及两个可见性属性，因此占位图标会切换为缩略图；
4. `Loaded` 与 `DataContextChanged` 仍调用 `EnsureThumbnailAsync`，条目回收后仍可重试；
5. 打开、移除、悬停和键盘焦点删除按钮逻辑没有改动。

### 2.2 搜索模板

Debug 生成代码确认：

1. `SearchTabItem.Glyph/DisplayName` 只在模板初始化时读取；
2. `SearchTabItem.Count` 单独订阅 `PropertyChanged`，没有给不可变字段增加伪通知；
3. `SearchResultItem` 与两个 `SearchRecommendationItem` 模板没有生成属性变化监听器；
4. 推荐应用图标仍由 `OnRecommendedAppsElementPrepared` 和 `RefreshRecommendedAppIcons` 两处
   `image.Source = item.Icon` 显式回填，保留非 observable 的延迟图标加载；
5. 收藏和最近搜索刷新仍通过重建 `SearchRecommendationItem` 条目更新标题。

实施中的第一次 Debug 构建还发现，推荐应用的 `SearchResultItem` 类型声明曾误落到相邻的骨架
模板，导致真正包含 `Icon/AppDisplayName` 的模板没有类型根。修正模板位置后构建通过，并在契约中
增加了类型声明必须位于 `RecommendedAppsRepeater` 范围内的顺序检查，防止同类遗漏复发。

## 3. 审计门禁

AOT 审计升级为 profile 26 / schema 23，新增以下硬门禁：

1. 两个目标文件中的 13 类旧 Binding 模式必须为 0；
2. 11 类 typed `x:Bind` 模式的精确数量合计必须为 14；
3. 5 个 DataTemplate 实例必须保持 4 种明确数据类型；
4. 附件通知、缩略图加载、打开/移除和搜索延迟图标刷新链必须保留；
5. `App.xaml`、`ContentWidgetWindow`、`FileWidgetSettingsSection` 和
   `SearchResultRowControl` 共 16 条延期 Binding 必须保留；
6. 两个目标 XAML 的 AOT 告警必须为 0；
7. 当前 WMC1510 必须精确为 1229；
8. 非预期警告代码与完整 `always-throw` 必须为 0。

4E-2 的 1243 已调整为历史上限门禁，允许后续继续下降但不允许回升；4E-3 的 1229 是当前精确
门禁。

## 4. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 6 失败 / 5 通过，符合预期 |
| 4E-3 契约 | 11/11 |
| AOT/4D/4E 扩大定向契约 | 113/113 |
| DeskBox x64 全量测试 | 2101/2101 |
| canonical Debug 构建 | 0 错误 |
| PowerShell 语法解析 | 0 错误 |
| x64 AOT 审计 | profile 26 / schema 23，通过 |

最终 AOT 审计用时 242,439 毫秒，源码指纹前后一致；产生 39 个发布文件，共 84,982,981 字节，
以及 3 个分离 PDB，共 181,309,440 字节。`DeskBox.exe`、`DeskBox.Updater.exe` 和
`deskbox_native.dll` 均为 x64 PE。Rust DLL 保持 ABI 2、能力掩码 255、9 个必需导出，
staging/publish SHA-256 一致。

最终分析结果：

- WMC1506：0；
- WMC1510：1229，分布在 17 个 XAML 文件；
- 4E-3 旧 Binding、缺失 compiled binding、缺失类型、缺失行为、延期范围变化和目标源告警：0；
- 非预期告警：0；
- 完整 `always-throw`：0。

## 5. 完成后复盘

代码、生成代码、调用链、测试和 AOT 摘要交叉检查后，没有发现范围内仍未处理的产品绑定：

1. 14 条目标 WMC1510 全部消失，下降值与绑定改动数一致；
2. 附件异步缩略图由生成的通知监听器刷新，没有新增竞争性的手工同步路径；
3. 搜索 Tab 只有 Count 建立通知监听，6 条不可变/手工刷新字段没有错误使用 OneWay；
4. 推荐应用延迟图标的两处显式刷新、结果回收、收藏/最近搜索点击和现有排序筛选代码均未改动；
5. 既有附件契约中的两条旧 Binding 文本断言已同步更新；
6. 所有 4D、4E-0/1/2、Rust ABI、导出、哈希、JSON 反射关闭和源码稳定性门禁继续通过；
7. 没有使用 bindable attribute、`x:SuppressXamlTrimWarnings` 或宽泛 trimming root 掩盖告警。

自动化仍不能替代真实 UI。当前 Debug 实例中需要人工确认：

- Todo 与 Quick Capture 附件的图片缩略图、非图片 Glyph、标题、Tooltip、辅助功能名称、打开和移除；
- 附件删除按钮的鼠标悬停、移出、键盘焦点进入和离开；
- 搜索 Tab 的图标、名称和动态数量；
- 推荐应用首次及延迟加载图标、应用名、收藏/最近搜索标题与点击；
- 搜索结果筛选和排序继续保持原行为。

## 6. 下一阶段审计结论

下一阶段建议定为 **4E-4 `FileWidgetSettingsSection` typed ViewModel 桥接**，只处理该文件的
5 条 WMC1510，目标为 1229→1224，复杂度为中等。

当前不能直接把五条表达式替换成 `x:Bind`：`SettingsWindow` 在 `InitializeComponent()` 完成后才
执行 `SettingsRoot.DataContext = ViewModel`，子控件依赖运行时继承 DataContext；其中两个
`SettingsComboBox.Value` 还是 TwoWay attached-property。建议下一批采用以下窄范围设计：

1. 在 `FileWidgetSettingsSection` 增加类型为 `SettingsViewModel` 的 `ViewModel` DependencyProperty；
2. `SettingsWindow` 初始化后显式赋值，在关闭和 ViewModel dispose 前清空；
3. 摘要和两组选项使用 OneWay typed `x:Bind`；
4. 两个 `SettingsComboBox.Value` 保持 TwoWay typed `x:Bind`；
5. 生成代码必须同时证明子控件 `ViewModelProperty`、`SettingsViewModel.PropertyChanged` 和两个
   attached `ValueProperty` 的目标到源回调均存在；
6. 冻结语言切换后的选项重建、文件堆摘要、低优先级选中项同步、设置持久化和窗口关闭解绑。

`SearchResultRowControl` 的 8 条继续延期。它不仅依赖运行时 DataContext，还涉及 ItemsRepeater
回收、同一对象重新绑定、延迟 Icon/Size/Date 元数据和 `RefreshIconVisuals` 手工覆盖，复杂度为
中高，应在 4E-4 完成并验证桥接模式后作为独立 4E-5。`App.xaml` 的两个 Style Setter 和
`ContentWidgetWindow` 的一个运行时 DataContext 绑定也继续冻结。

上述 4E-4 已按该边界完成，结果与后续 4E-5 调整见 `aot-stage-4e-4-report.md`。
