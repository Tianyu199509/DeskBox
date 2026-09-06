# DeskBox Native AOT 阶段 4E-2 完成报告

- 日期：2026-08-21
- 范围：两个自有属性叶子控件中的 15 条 WMC1510
- 状态：代码、生成代码、契约、x64 全量测试和 AOT 产物审计已完成；Debug 可见行为待人工确认

## 1. 阶段结论

4E-2 已按审计后的完整范围完成：

- `MusicTransportIcon.xaml`：7 条 `Foreground` 自引用 Binding 改为 OneWay typed `x:Bind`；
- `WidgetInlineEditor.xaml`：标题、字体、按钮文本等 7 条自身属性 Binding 改为 OneWay typed
  `x:Bind`，文本 Binding 改为保留 `UpdateSourceTrigger=PropertyChanged` 的 TwoWay typed `x:Bind`。

本批共移除 15 条传统 Binding。WMC1510 从 1258 精确降至 1243，目标两个 XAML 的 AOT
告警均为 0；WMC1506、全部 IL2xxx/IL3xxx 和完整 `always-throw` 继续为 0。全仓库传统
`{Binding}` 从 1,342 降至 1,327，`{x:Bind}` 从 35 增至 50，使用传统 Binding 的 XAML
文件从 20 个降至 18 个。

本批没有修改控件 code-behind、调用方、ViewModel、Rust 源码、ABI、COM 边界或 JIT/AOT
后端策略，也没有启动 AOT 主程序。

## 2. TwoWay 与动态刷新证据

本轮没有以“能编译”代替绑定生命周期验证。Debug 生成代码确认：

1. `WidgetInlineEditor` 的 `Title`、`Text`、`CancelText`、`SaveText` 和三个字体属性均通过
   `RegisterPropertyChangedCallback` 订阅对应 DependencyProperty；调用方运行时赋值仍会更新 UI；
2. 文本框额外对 `TextBox.TextProperty` 注册目标到源回调，每次变化都会执行
   `dataRoot.Text = obj3.Text`，因此 Quick Capture 与 Todo 保存时读取的控件 `Text` 仍是即时值；
3. 保存、取消、关闭、键盘事件和 `FocusEditor` 没有改动；Quick Capture 与 Todo 仍分别通过
   `QuickCaptureInlineEditor.Text`、`TodoInlineEditor.Text` 保存，并在关闭时清空该属性；
4. `MusicTransportIcon.Foreground` 注册一个 DependencyProperty 回调，一次同步更新 7 个 Shape；
5. `KindProperty`、`OnKindChanged` 和 `ApplyKind` 仍负责 Previous/Play/Pause/Next 四种图形切换。

因此本轮不需要增加手工 `TextChanged` 或前景色同步事件，也没有形成两套互相竞争的刷新路径。

## 3. 审计门禁

AOT 审计升级为 profile 25 / schema 22，新增以下硬门禁：

1. 两个目标文件中的 8 类旧 Binding 模式必须为 0；
2. 8 类 typed `x:Bind` 的精确数量合计必须为 15；
3. 两个控件的 DependencyProperty、图形切换、保存/取消和键盘事件入口必须保留；
4. `App.xaml` 两个 Style Setter 与 `ContentWidgetWindow` 运行时 DataContext 三个延期绑定必须保留；
5. 两个目标 XAML 的 AOT 告警必须为 0；
6. 当前 WMC1510 必须精确为 1243；
7. 非预期警告代码与完整 `always-throw` 必须为 0。

4E-1 的 1258 由“当前精确值”调整为历史上限门禁：后续阶段允许继续减少，但不允许回升到
1258 以上；4E-2 的 1243 是当前精确门禁。这样保留上一阶段的回归保护，同时避免两个互相
矛盾的全局精确计数。

## 4. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 6 失败 / 3 通过，符合预期 |
| 4E-2 契约 | 9/9 |
| AOT/4D/4E 扩大定向契约 | 102/102 |
| DeskBox x64 全量测试 | 2090/2090 |
| canonical Debug 构建 | 0 错误 |
| PowerShell 语法解析 | 0 错误 |
| x64 AOT 审计 | profile 25 / schema 22，通过 |

第一次全量测试为 2089/2090。唯一失败是既有 `MusicWidgetContentLayoutTests` 仍断言图标中的
7 条旧 Binding 文本；更新为新 typed `x:Bind` 契约后，全量 2090/2090 通过，没有产品行为失败。

最终 AOT 审计用时 369,631 毫秒，源码指纹前后一致；产生 39 个发布文件，共 84,969,901
字节，以及 3 个分离 PDB，共 181,260,288 字节。Rust DLL 保持 ABI 2、能力掩码 255、九个
必需导出，staging/publish SHA-256 一致。

最终分析结果：

- WMC1506：0；
- WMC1510：1243，分布在 19 个 XAML 文件；
- 4E-2 旧 Binding、缺失 compiled binding、缺失行为、延期范围变化和目标 XAML 告警：0；
- 非预期告警：0；
- 完整 `always-throw`：0。

## 5. 完成后复盘

代码、生成代码、调用方、测试和 AOT 摘要交叉检查后，本阶段发现并处理了唯一遗漏的旧布局
测试，没有发现范围内的产品代码遗漏：

1. 15 条目标 WMC1510 全部消失，其他文件计数没有意外变化；
2. TwoWay 文本由生成的 `TextProperty` 回调即时写回，不需要行为补丁；
3. 音乐四种图形、编辑器三个事件和两个真实保存调用链都由契约冻结；
4. 所有 4D、4E-0、4E-1、Rust ABI、导出、哈希和源码稳定性门禁继续通过；
5. 没有使用 bindable attribute、`x:SuppressXamlTrimWarnings` 或宽泛 trimming root 掩盖告警。

自动化仍不能代替真实 UI。Debug 实例中需要人工确认：

- Music Widget 与胶囊模式的 Previous/Play/Pause/Next 图标、禁用态、前景色和主题切换；
- Quick Capture 与 Todo 编辑器的标题、字体、按钮文案和语言切换；
- 连续输入后立即保存，以及取消、关闭、Escape、Enter/Ctrl+Enter 的原有语义。

## 6. 下一阶段审计结论

下一阶段建议定为 **4E-3 typed DataTemplate 小批次**，合并两个低复杂度目标：

1. `AttachmentTileStrip.xaml` 的 7 条绑定：DataTemplate 实际类型固定为
   `TodoAttachmentViewModel`。`Thumbnail` 及两个 Visibility 属性有 `ObservableObject` 通知，
   DisplayName/Glyph 在条目存活期不变；加入 `x:DataType` 后可保持全部 OneWay 语义。必须冻结
   Loaded/DataContextChanged 缩略图加载、打开/移除事件和悬停/焦点删除按钮行为；
2. `SearchPopupWindow.xaml` 的 7 条绑定：分别属于 `SearchTabItem`、`SearchResultItem` 和
   `SearchRecommendationItem` 三类 DataTemplate。Tab Count 有 `INotifyPropertyChanged`；应用
   Icon 虽不通知，但现有 `ElementPrepared` 与 `RefreshRecommendedAppIcons` 会显式刷新，必须
   原样保留。实现时应让 Tab Count 使用 OneWay，Glyph/DisplayName、Icon/AppDisplayName 和两条
   Title 使用 OneTime；四个模板实例使用明确 `x:DataType`，无需改业务代码。

两者同批目标为 WMC1510 1243→1229，共移除 14 条。复杂度为低到低中，风险集中在模板类型声明、
缩略图异步通知和搜索应用图标懒加载，适合用生成代码和现有手工刷新链分别验证。

本次审计明确不把以下项目混入 4E-3：

- `FileWidgetSettingsSection.xaml` 的 5 条依赖父窗口运行时继承 DataContext，并包含两个 TwoWay
  attached-property 绑定，需要先设计 typed ViewModel 桥接和 DataContextChanged 刷新；
- `SearchResultRowControl.xaml` 的 8 条直接依赖控件运行时 DataContext，图标和文件元数据还由
  code-behind 手工补刷新，不能只替换表达式；
- `App.xaml` 的两个 Style Setter 和 `ContentWidgetWindow.xaml` 的一个运行时 DataContext 绑定
  继续按原门禁延期。

4E-3 本轮只完成审计和范围冻结，尚未开始开发。
