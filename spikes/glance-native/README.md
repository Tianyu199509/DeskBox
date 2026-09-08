# Glance NativeAOT 原生模块试点

这是 [官方功能包执行计划](../../docs/architecture/official-widget-packages-plan.md) 批次 B 的可运行证据。独立 NativeAOT 宿主通过 C ABI 加载独立 NativeAOT DLL，DLL 读取外置 XAML、创建 WinUI 控件并提供绑定对象。宿主没有引用包项目，也没有编译 Glance 的业务实现。

第一轮（v1/v2 小切片）证明机制存在；第二轮（2026-09-08 晚）补齐真实 Glance 切片、生命周期、多包并存与待办编辑/持久化切片。

## 已验证

2026-09-08，Windows x64、.NET SDK 10.0.303、Microsoft.WindowsAppSDK 2.4.0。

### 第一轮：同一宿主，两版包（机制证明）

脚本只发布一次宿主，随后分别发布两版 DLL，并启动使用相同宿主文件的新进程。v1 链接生产代码 `GlanceCalendarLayoutCalculator.cs`；v2 在输出目录中复制同一计算器，仅将紧凑模式阈值从 320 改为 360。生产计算器没有改动。

v2 的结论不变：宿主 `RuntimeFeature.IsDynamicCodeSupported`=false，输入高度 340 的业务计算值与实际 CalendarView 高度从 244 变为 268，标题绑定随包版本更新。

### 第二轮：真实 Glance 切片（v3 包）

`glance-real.xaml` 从生产 `GlanceWidgetContent.xaml` 移植（日项模板、玻璃面板、紧凑表头、CalendarView 及其资源覆写逐字保留；图片层/沉浸布局/照片操作栏属于本切片未包含的图片服务）。业务代码**链接生产源码**而非重写：布局计算器、日历契约模型、本地月视图源、传统历法服务、节日服务、日装饰记录、ChineseTextConverter（仅 `App.LogVerbose`（诊断日志）与 `IsTraditionalChineseCulture`（纯函数原样拷贝）两处宿主面以 `ProductionSeams.cs` 过渡，批次 C 定契约）。

固定 2026-09、zh-CN、农历模式下实测：42/42 天有农历文本，`2026-09-25 中秋` 出现在节日列表，标题为"丙午年 七月廿七"（真实传统历法服务输出），43 个日项被装饰（42 格 + 1 次回收重实例化），CalendarView 实际高度 306，模块加载+建视图 ~44ms。

### 第二轮：生命周期、多包并存、待办编辑/持久化

- **销毁重建**（lifecycle 场景）：同一进程内创建→卸载（Unloaded 事件确认）→再创建，第二个视图正常渲染（业务值 244 + 标题绑定恢复）。
- **多包并存**（multi-package 场景）：同一宿主进程同时加载 v2（简单切片）与 v3（真实切片）两个原生 DLL，两个模块句柄不同、两个视图同时渲染（v2 高度 268 + v3 农历标题），宿主哈希不变。
- **待办编辑/持久化**（todo-package 场景，`TodoPackage/`）：宿主通过投影设置包内 TextBox 文本、直接构造 `ButtonAutomationPeer` 触发真实点击路径，包代码将条目写入包目录 `todo-items.json`；销毁视图后重建，条目从文件恢复（1→1，内容一致）。**注意：数据写入包目录是 spike 简化，不是契约**——与 B1 的只读内容寻址安装目录/更新换目录模型冲突（更新后数据悬空、卸载误删、ReadOnly 无法写入），批次 C 必须拆分 PackageRoot（只读）/PackageDataRoot（可写）/InstanceDataRoot（实例级），激活 ABI 传 packageRoot+dataRoot+widgetInstanceId 三参。

### 第三轮：编译 XAML / PRI / WinRT 激活（钉住的负结论）

第三轮（2026-09-08 深夜）用可复现探针回答了三个悬而未决的问题，全部为**当前不可行**，已作为回归断言钉进脚本（`compiled-xaml-pinned-negative` 场景；若未来 Windows App SDK 行为翻转，断言会失败并强制重评契约）：

1. **编译 XAML（XBF）在动态加载的 AOT DLL 中无法定位。** 包项目为 `RealGlanceControl.xaml` 正常走完 XAML 编译（生成 XBF + XamlMetaDataProvider），但运行时 `Application.LoadComponent`（`ComponentResourceLocation.Nested`，`ms-appx:///DeskBox.Glance.NativePackage/...`）抛 XamlParseException。判别实验：**无任何包内自定义类型的最小编译控件同样失败**——问题在 XBF 定位层而非类型解析。把 XBF 按 ms-appx 布局拷到宿主目录子路径也不行（已试）。宿主自己的 XBF 无磁盘文件且 exe 内无明文名，说明解包应用的 XBF 解析内嵌在宿主 exe 的资源体系里，动态 DLL 借不到。**运行时文本 XAML + 预计算可绑定属性（第二轮）仍是已验证路径。** 若未来必须编译 XAML：`Application.ResourceManagerRequested` 提供自定义 `IResourceManager` 是候选逃逸口（未实验）。
2. **本试点的 AOT DLL 产物不导出 `DllGetActivationFactory`。** 当前 C# NativeAOT 动态类库配置中，raw C 导出（`UnmanagedCallersOnly`）是唯一**已验证可工作**的 ABI；C#/WinRT 本身仍支持组件激活/registration-free activation 等机制，本试点未验证，不构成平台定律。
3. **独立 PRI 不随类库发布产生，MrtCore 无文件级加载。** `PRIResource`（resw）进了编译但发布输出没有 `.pri` 文件；MrtCore `ResourceManager` 两个构造路径均抛 COMException（找不到元素）；UWP 遗留 `LoadPriFiles` 因无文件可载而跳过。**包本地化需要自带字符串文件机制（或批次 C 重新决策布局）。**

包体积变化：v1-v3 ≈ 6.18MB/个（编译控件+PRIResource 加入后，此前 4.55MB），todo 包 4.26MB。

### 第四轮：完整 Glance 切片（图片/交互/设置/持久化）

第四轮（2026-09-08 深夜）补齐完整 Glance 代表性切片（`full-glance.xaml` + `FullGlanceView.cs`）：背景图片 A/B 交叉淡入轮播（包目录 `backgrounds/` 本地图，3s 定时器，Unloaded 停表）、生产结构操作栏（暂停/下一张，真实 Click 处理器）、代码构建右键 MenuFlyout（运行时 XAML 无法接线处理器）、XAML 名字域内设置面板（节日/传统历法 ToggleSwitch）经程序化 `IsOn` 驱动同一 Toggled 路径重建月份、设置持久化到 `glance-settings.json`。实测链：定时轮播推进（索引 1）→ automation peer 真实点击下一张（索引回绕 0，2 张图模运算）→ 关节日开关（festivalDayCount 42 格→0，月份数据实时重建）→ 销毁重建（设置保留、轮播重置）。在线图片源（Bing/在线目录）不在本切片范围，归批次 D 的生产 GlanceImageService 迁移。

### 第五轮：批次 C 综合 probe（宿主控件/工具包/三根 ABI/主题 token）

第五轮（2026-09-08 末，`InteractionPackage/` + 宿主 `HostBadge`）回答了外部二次审计的最高价值问题：

1. **宿主自定义控件与 toolkit 控件都能在包运行时文本 XAML 中解析并实例化**——`<host:HostBadge />`（宿主编译 UserControl）与 `<toolkit:Segmented>`（CommunityToolkit，经宿主 XamlTypeInfo 提供器）双双通过。判别实验：宿主代码直接 `new HostBadge()` 曾同样失败（"Cannot locate resource ms-appx:///HostBadge.xaml"）→ 问题不在包 XAML，在宿主发布布局。
2. **宿主 Page XBF 必须随宿主安装目录落盘**：publish 不携带 Page XBF（App.xaml 的 ApplicationDefinition 走内嵌，Page 不走），`ms-appx` 文件级解析要求 XBF 在 exe 旁。脚本已显式拷贝——**产品宿主的安装布局必须包含 Page XBF，这是批次 C 契约条目**。
3. **三根 ABI 落地**：`interaction_get_abi_version/activate(packageRoot,dataRoot,instanceId)/create_widget(widgetId)/destroy_widget/shutdown`；数据只写 DataRoot（断言验证包目录零写入），重建后从 DataRoot 恢复。
4. **主题 token 注入可用**：宿主写 `theme-tokens.json` 进 DataRoot，包激活时映射为本地资源字典（解析期默认值 + 代码替换的双层模式；运行时文本 XAML 的 ThemeResource 在解析期已定值，动态重着色需另行设计）。
5. **事件接线**：包代码 FindName 订阅，计数入 summary；**注意 toolkit Segmented 的 SelectionChanged 需订阅控件自身事件**（经基类 `Selector` 订阅接不到——`selectionChanges=0` 实测记录），迁移时按控件类型逐个确认事件归属。
6. 事件计数 2、destroy 调用 1、真实点击添加 1→重建恢复 1，全部断言通过。

两轮所有场景的断言（业务值、实际布局、绑定、退出码、截图、宿主哈希一致）由 `scripts/spike/run-glance-native.ps1` 自动判定；本地证据在 `.artifacts/glance-native/runs/<timestamp>-x64/summary.json`，截图在各 `result-*/view.png`。二进制和运行证据不入库。

## 复现

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/spike/run-glance-native.ps1
```

需要当前仓库的 .NET、MSVC、Windows App SDK 构建环境。项目放在 `spikes/`，不进入应用的项目引用或发布流水线。正常和 AOT 两份依赖锁均保留。脚本不会清空历史运行目录。参数 `-Platform ARM64 -BuildOnly` 可用于 ARM64 编译验证；本轮仅执行了 x64。

## 发现的集成契约（批次 C 输入）

1. **运行时解析的包 XAML 不能使用包内自定义类型（转换器）。** `XamlReader.Load` 找不到 `using:包命名空间` 下的转换器类型——原生 DLL 没有自己的 XAML 元数据提供器，宿主的提供器也不认识包的命名空间。解法：可绑定模型直接预计算 `Visibility`/`FontWeight`（本试点的 `RealGlanceDayDecoration`），模板绑定形状保持不变。
2. **WinUI 控件主题字典的 ThemeResource key 到不了运行时解析的包 XAML。** 宿主 `Application.Resources` 合并 `XamlControlsResources` 也不行（`TextFillColorPrimaryBrush` 等解析失败）；只有平台级 key（如 `ApplicationPageBackgroundThemeBrush`）可达。解法：包 XAML 资源自含（本试点内联 Fluent 暗色近似值，单主题；正式包需要主题感知设计）。
3. **宿主需要正常生成的 WinUI XAML 元数据及 App 资源初始化**（第一轮结论，`0xC000027B` 消失于补 App.xaml 后）。
4. **CalendarView 日项在进树后才实例化**：任何"日项渲染完成"的证据必须在 Loaded 之后延迟采样（本试点用 600ms DispatcherQueueTimer 重写摘要）。
5. **AutomationPeer 懒创建**：宿主侧 `FromElement` 可能返回 null；直接 `new ButtonAutomationPeer(owner)` 后 `Invoke()` 走同一条点击路径。
6. **运行时 XAML 的 FindName 结果通过 C#/WinRT `As<T>()` 显式投影后访问**；直接 CLR 强制转换在该 AOT 场景失败（第一轮结论，沿用）。
7. **生产源码可以链接复用**：纯计算服务（传统历法/节日/布局/转换器）原文件链接进包即可工作；宿主面（日志、本地化大类）需要明确接缝——批次 C 要把它们定义为真实契约，而不是试点里的 no-op/拷贝。

## 尚未验证

- 真实**物理键盘/IME 输入**与焦点迁移（当前输入通过投影与 automation peer 模拟）。
- Glance 完整功能（图片/天气/设置/右键菜单）与其余五功能迁移。
- ARM64 设备运行（脚本支持 `-Platform ARM64 -BuildOnly` 编译验证，未执行）、冷启动/工作集/多包规模化的系统测量（当前仅记录模块加载+建视图耗时与 DLL 体积）。
- 叠放/合并/胶囊容器中的内容迁移、1.5.0 升级与 Store 渠道。
- 编译 XAML 需求若回归：`Application.ResourceManagerRequested` 自定义 `IResourceManager` 逃逸口未实验。

宿主命令行仅接受显式开发包目录，这里没有接入产品安装、签名、授权或实例恢复。官方原生包执行在进程内，必须作为可信代码管理。试点通过允许继续完善正式边界，不表示批次 B 或六功能迁移已经完成。
