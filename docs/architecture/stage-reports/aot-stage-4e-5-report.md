# DeskBox Native AOT 阶段 4E-5 完成与复盘报告

- 日期：2026-08-21
- 范围：`SearchResultRowControl` 的 8 条搜索结果行 Binding
- AOT 审计：profile 28 / schema 25
- 结果：WMC1510 1224→1216，NativeAOT/Rust 结构审计通过

## 1. 范围与约束

4E-5 只处理搜索结果行中的 8 条运行时 Binding：

1. 行级 AutomationProperties.Name；
2. 备用 Glyph；
3. Shell Icon；
4. Title；
5. Subtitle；
6. TypeDisplay；
7. SizeDisplay；
8. DateDisplay。

本批不修改搜索结果集合、查询、筛选、排序、文件元数据服务、Rust ABI 或普通 JIT/AOT 后端
策略。`SearchResultItem` 继续是带 `required Kind/Title` 的普通 DTO，不增加
`INotifyPropertyChanged`。ItemsRepeater 回收、同对象再次 prepare、异步元数据返回、单选、
多选、列显隐和关闭解绑均作为冻结边界。

## 2. 编译器试验与最终决策

最初按规划试验了 public typed Item DependencyProperty，并由父 DataTemplate 用 pathless
OneWay `x:Bind` 传入当前条目。真实 XAML 构建给出两个明确结果：

1. public `SearchResultItem` XAML 属性会让 `XamlTypeInfo.g.cs` 生成
   `new SearchResultItem()`，随后因 `Kind` 和 `Title` 是 required 成员产生两个 CS9035；
2. 非 observable 条目上的 pathless OneWay 桥会产生 WMC1506。

因此没有删除 required 约束，也没有给 DTO 增加伪通知。最终采用内部强类型投影：

- 行控件保留 `internal SearchResultItem? Item`；
- 新增 `PrepareItem(SearchResultItem?)`，设置投影后调用生成的 `Bindings.Update()`；
- `OnResultsElementPrepared` 每次先按 `ItemsRepeater` 的准备索引从当前结果集合取得 Item，
  再执行既有 `RefreshIconVisuals()`；DataContext 只作为集合切换时的兜底；
- 8 条叶子使用 OneTime compiled `x:Bind`，由每次 prepare 显式整组刷新；
- DataContext 不被覆盖，既有命中测试、焦点和右键查找链继续使用真实继承值。

该方案比 object DependencyProperty 传输更窄，没有新增运行时 Binding，也能覆盖元素回收时
同一对象不触发 DataContextChanged 的情况。

## 3. 实现结果

`SearchResultRowControl.xaml` 的 8 条 `{Binding ...}` 已全部变为
`{x:Bind Item.*, Mode=OneTime}`，目标文件中的传统 Binding 为 0。

`OnResultsElementPrepared` 当前顺序为：

1. 按 `args.Index` 从 `CurrentResults` 取得条目并执行 `PrepareItem(preparedItem)`；
2. `RefreshIconVisuals()`；
3. 更新文件列可见性；
4. 必要时启动异步元数据补全；
5. 清理或恢复单选和多选视觉。

异步完成后仍只有在 `ReferenceEquals(row.Item, item)` 成立时刷新行，避免已回收给另一个条目的
行被旧任务覆盖。Icon、SizeDisplay 和 DateDisplay 继续由显式刷新回填；TypeDisplay 仍在结果
进入集合前由 ViewModel 写入。

后续实际运行验证发现，WinUI 的 `ElementPrepared` 可能早于 DataTemplate 根节点继承到
DataContext。若在该回调中直接读取 `row.DataContext`，搜索统计已有结果但 OneTime 绑定会被
一次性刷新为空。当前实现已改为使用回调提供的稳定索引取条目，并让选中态与行查找同时优先
使用内部 Item 投影，避免再次受同一时序影响。

## 4. 生成代码证据

Debug 生成的 `SearchResultRowControl.g.cs` 确认：

1. 生成 8 个目标 setter，Title 同时更新 Automation Name 和 TitleText；
2. `Update_` 进入 `Update_Item` 后一次读取 Title、TypeDisplay、SizeDisplay、DateDisplay、
   Subtitle、Icon 和 DisplayGlyph；
3. `Bindings.Update()` 会执行完整的 Item 叶子更新，不依赖 PropertyChanged；
4. `StopTracking()` 为空，没有隐藏的 DTO 事件订阅；
5. `XamlTypeInfo.g.cs` 不再包含 `SearchResultItem` activator。

这与当前生命周期一致：Item 切换由 ItemsRepeater 的每次 prepare 驱动，延迟字段由既有手工链
驱动，二者没有重复的长期订阅。

## 5. 审计门禁

`publish-aot-audit.ps1` 升级到 profile 28 / schema 25，并增加以下门禁：

1. 8 类旧 Binding 必须为 0；
2. 8 条 OneTime compiled binding 的路径和数量必须精确匹配；
3. internal Item、PrepareItem、Bindings.Update 和 ElementPrepared 准备模式必须存在；
4. public typed Item 或 Item DependencyProperty 必须为 0，避免恢复 required-member activator；
5. Item 准备必须先于 lazy metadata 视觉刷新；
6. 每次 prepare、异步引用一致性、单选、多选、DataContext 查找和列显隐行为必须保留；
7. SearchResultItem 的 required 成员与 lazy 字段必须保留，伪 observable 模式必须为 0；
8. 关闭时必须先解绑 ElementPrepared，再清空 ItemsSource，最后 dispose ViewModel；
9. `App.xaml` 两条 Style Setter 和 `ContentWidgetWindow` 一条运行时 DataContext Binding
   继续显式延期；
10. 目标源告警、非预期告警和完整 `always-throw` 必须为 0；
11. WMC1510 必须精确为 1216。

4E-4 的 1224 同时调整为历史上限门禁，允许本批继续下降但不允许回升。

## 6. 验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 5 失败 / 7 通过，符合预期 |
| 4E-5 契约 | 12/12 |
| 4E-3 至 4E-5 组合契约 | 34/34 |
| AOT/Rust 扩大定向契约 | 198/198 |
| DeskBox x64 全量测试 | 2124/2124 |
| canonical Debug 构建 | 0 错误，24 条既有警告 |
| PowerShell 语法解析 | 0 错误 |
| x64 AOT 审计 | profile 28 / schema 25，通过 |

最终隔离 AOT 审计用时 216,213 毫秒，源码指纹前后一致；产生 39 个发布文件，共
84,996,037 字节，以及 3 个分离 PDB，共 181,366,784 字节。三个本地产物均为 x64 PE：

- `DeskBox.exe`：39,357,440 字节；
- `DeskBox.Updater.exe`：2,020,352 字节；
- `deskbox_native.dll`：146,944 字节。

Rust DLL 保持 ABI 2、能力掩码 255、9 个必需导出，staging/publish SHA-256 一致。

最终分析结果：

- WMC1506：0；
- WMC1510：1216，分布在 15 个 XAML 文件；
- 4E-5 旧绑定、缺失 compiled binding、缺失内部桥接、错误刷新顺序、public Item 暴露、
  缺失行为、模型约束变化、伪 observable、DataContext 覆盖、错误生命周期、延期范围变化和
  目标源告警：0；
- 非预期告警：0；
- 完整 `always-throw`：0。

## 7. 剩余 WMC1510 分布

| 文件 | 数量 |
| --- | ---: |
| `App.xaml` | 2 |
| `FileItemSurface.xaml` | 36 |
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

## 8. 完成后复盘

代码、生成代码、调用链、契约和 AOT 摘要交叉检查后，没有发现 4E-5 范围内遗漏：

1. 下降值 8 与目标 Binding 数量一致，目标文件已从 WMC1510 分布中消失；
2. 每次 element prepare 都重设内部 Item 并整组刷新，覆盖新对象、回收对象和同对象重绑；
3. 异步任务返回仍检查条目引用，不会污染已回收行；
4. lazy Icon/Size/Date 的显式刷新仍存在，没有用 OneTime binding 代替它；
5. DataContext、单选、多选、焦点、右键查找、列显隐和关闭清理链没有被替换；
6. SearchResultItem 仍保留 required 约束且非 observable；
7. 没有修改 Rust 源码、ABI、能力、导出或 JIT/AOT 后端策略；
8. AOT 主程序仍未启动，4E 的结构清理与阶段 5 运行验证边界没有混淆。

自动化和生成代码不能替代可见 UI 验证。当前 Debug 实例仍需人工确认：输入能产生文件和应用
结果的关键词，切换筛选和排序，滚动使行发生回收，并核对标题、类型、大小、日期、图标、
单选和多选视觉没有串行或残留。

## 9. 下一阶段建议

下一阶段调整为 **5A：x64 AOT 隔离启动与基础存活验证**，停止继续做微型 Binding 批次。

当前 `DESKBOX_DEV_DATA_ROOT` 只在 DEBUG 编译中生效，不能直接保护 Release NativeAOT 产物。
因此 5A 应先完成一个窄范围的 AOT-preview 隔离入口，再启动本次审计产物：

1. 让 `DESKBOX_NATIVE_AOT` 构建在显式 opt-in 时支持独立 preview data root，默认路径继续保持
   `%LOCALAPPDATA%\DeskBox`；
2. 启动脚本必须拒绝 production data root，记录被启动 exe、summary、Rust DLL 哈希和数据根；
3. 首次只验证进程存活、日志无启动异常、PRI/XAML/资源加载、单实例、托盘、退出和重启；
4. 核对正式数据目录在运行前后没有变化；
5. 通过基础存活门后，再分批执行 shortcut、音乐音量、Explorer 启动、Quick Access、设置、
   搜索、Widget、文件操作、安装升级和回滚矩阵。

5A 不应顺便处理剩余 1216 条 WMC1510，也不应先扩展 Rust SearchCore。阶段 5 的首要问题是证明
当前裁剪后的 AOT 主程序能在隔离数据下真实启动和退出。

本报告完成时，阶段 5 尚未开始开发，也没有启动 AOT 产物。
