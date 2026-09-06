# DeskBox Native AOT 阶段 4D-5 完成报告

- 日期：2026-08-21
- 范围：托盘 identity 与 SecondWindow 菜单 presenter 的反射收口
- 状态：代码、自动化和 x64 AOT 产物审计已完成；Debug 可见菜单人工确认待执行

## 1. 阶段结论

4D-5 已删除 `App.Tray.cs` 中全部托盘私有成员反射，没有引入 Rust、第三方包升级、
`DynamicDependency`、trimming root 或警告抑制。

托盘 identity 直接使用 `TaskbarIcon.TrayIcon.WindowHandle/Id`。第二窗口菜单不再读取私有
`ContextMenuFlyout`，而是通过公开 `SecondWindowContextMenuOpened` 事件、已知菜单项的
`Loaded` 事件以及 WinUI 公开视觉树定位实际 `MenuFlyoutPresenter` 和所属 `Popup`。原有
SecondWindow 模式、托盘图标矩形定位、fallback、无滚动样式及窗口根约束设置均保留。

因此 4D 的 COM、dynamic 与 trimming 数据流批次已经收口。当前 Native AOT 原始日志中的
IL2026、IL2050、IL2072、IL2075、IL3050 和 always-throw 均为 0，下一阶段可以进入 4E 的
XAML bindable 分批治理。

## 2. 方案核对

项目解析到的 `H.NotifyIcon.WinUI` 为 `2.5.0-beta.1`，NuGet manifest 对应提交
`63747ab3d87178d793ab7a23ea7dcda3db2804de`。程序集公开契约确认：

1. `TaskbarIcon.TrayIcon` 是公开属性，类型为 `H.NotifyIcon.Core.TrayIcon`；
2. `TrayIcon.WindowHandle` 和 `TrayIcon.Id` 是公开属性；
3. `TaskbarIcon.SecondWindowContextMenuOpened` 是公开事件；
4. 第二窗口的实际 `ContextMenuFlyout` 仍是库私有实现，不应由产品代码反射访问。

对应版本源码会把原 flyout 的 item 移入第二窗口 flyout，并在其打开时触发公开事件：
[H.NotifyIcon 2.5.0-beta.1 SecondWindow 源码](https://github.com/HavenDV/H.NotifyIcon/blob/63747ab3d87178d793ab7a23ea7dcda3db2804de/src/libs/H.NotifyIcon.Shared/TaskbarIcon.ContextMenu.WinUI.SecondWindow.cs)。

最终采用公开视觉树方案，而不是升级依赖或将托盘逻辑迁入 Rust。托盘菜单仍是 WinUI 对象，
Rust 不能减少其 UI 生命周期复杂度；升级 H.NotifyIcon 则会把依赖变化和 AOT 修复混在同一批。

## 3. 实现与行为边界

### 3.1 强类型 identity

`TryGetTrayIconIdentity` 现在直接读取公开属性，用于既有 `Shell_NotifyIconGetRect` 调用。以下
行为没有改变：

- 优先使用真实托盘图标矩形计算菜单锚点；
- 图标矩形不可用时继续使用光标/工作区 fallback；
- `ShowContextMenu(point)` 及右键命令调用链不变。

### 3.2 SecondWindow presenter

实现用菜单中长期存在的“整理桌面”项作为视觉树锚点：

- `Loaded` 在库预热或实际创建视觉树时首先尝试应用设置；
- `SecondWindowContextMenuOpened` 在真实打开时再次尝试，覆盖首次预热尚未得到 Popup 的情况；
- 沿公开父视觉树找到 `MenuFlyoutPresenter`；
- 禁用垂直滚动与滚动条，并移除默认 `MaxHeight`；
- 从 `GetOpenPopupsForXamlRoot` 中定位包含该 presenter 的 Popup，设置
  `ShouldConstrainToRootBounds=false`。

普通原始 `MenuFlyout` 的 presenter style 仍保留，第二窗口的实际 presenter 再通过公开视觉树
同步。应用启动时仍调用 `ForceCreate(enablesEfficiencyMode: false)`，没有重新打开曾影响初始化
顺序的进程 Efficiency Mode 路径。

## 4. 审计与验证证据

| 验证项 | 结果 |
| --- | --- |
| 旧实现红线契约 | 4 失败 / 2 通过，符合预期 |
| 4D-5 契约测试 | 6/6 |
| DeskBox x64 全量测试 | 2067/2067 |
| `git diff --check` | 通过，仅有仓库既有换行提示 |
| x64 AOT 审计 | 配置 22 / schema 19，通过 |

隔离 AOT 审计用时约 163.2 秒，源码指纹前后一致；产生 39 个发布文件，共
84,952,205 字节，以及 3 个分离 PDB，共 181,170,176 字节。Rust DLL 仍为 ABI 2、能力掩码
255、九个必需导出，staging/publish SHA-256 一致；本阶段没有修改 Rust 源码、ABI 或路由。

审计门禁结果：

- 4D-5 旧反射模式命中：0；
- 4D-5 必需公开调用模式缺失：0；
- `App.Tray.cs` AOT 告警：0；
- 非预期告警代码：0；
- 完整 always-throw：0；
- IL2026、IL2050、IL2072、IL2075、IL3050：全部 0。

当前仅剩普通 C# 编译警告，以及 XAML 编译器的 WMC1506 6 条、WMC1510 1265 条。AOT 主程序
仍未启动，本阶段没有把产物检查描述为 AOT 运行通过。

## 5. 完成后复盘

代码、依赖契约、测试与发布摘要交叉检查后，没有发现需要扩大 4D-5 的遗漏：

1. `App.Tray.cs` 不再包含 `BindingFlags`、`GetProperty("TrayIcon")`、
   `GetProperty("WindowHandle")`、`GetProperty("Id")` 或
   `GetProperty("ContextMenuFlyout")`；
2. 公开事件在首次视觉树同步失败时仍提供实际打开期重试；
3. 两条路径最终修改的是库真正显示的第二窗口 presenter，而不是只修改已经搬空的原 flyout；
4. 菜单 positioning、fallback、SecondWindow 模式、左键命令和托盘创建 QoS 策略均未改变；
5. 审计脚本已将 IL2026、IL2072、IL2075、IL3050 从允许集合移除，后续重新出现会直接失败；
6. 没有为了通过裁剪分析新增宽泛保留规则。

自动化不能证明右键菜单在用户当前 DPI、任务栏位置和主题下的最终可见效果。Debug 运行后仍需
人工确认一次：菜单位置正常、全部项目可见且无滚动条、主题一致、左右键行为不变。该项是 UI
验收边界，不影响本批源码和 AOT 分析收口的结论。

## 6. 下一阶段调整

下一阶段建议从 **4E-0 搜索历史 WMC1506 收口** 开始，复杂度低，不使用 Rust：

1. `SearchWidgetContent.xaml` 的 6 条 WMC1506 都来自 `SearchHistoryEntry.Query/DeleteLabel` 的
   `OneWay x:Bind`；
2. 两个属性均为 `required init`，每次历史刷新都会清空并重新创建条目，条目存活期间不变；
3. 因此应把 6 处 binding 精确改为 `OneTime`，无需让 DTO 实现通知接口，也不改变历史刷新机制；
4. 增加契约，要求 WMC1506 从 6 降到 0，WMC1510 仍保持 1265，不在同一批开始大规模 XAML
   bindable 改造。

4E-0 完成并复盘后，再选择一个小型 WMC1510 页面作 4E-1 pilot。当前 1265 条分布在 25 个
XAML 文件，最大三组是 `SettingsWindow.xaml` 321、`QuickCaptureWidgetWindow.xaml` 137 和
`TodoWidgetContent.xaml` 135；不建议先从 321 条的设置主窗一次性展开。应先审计 1 到 8 条的
叶子控件，冻结 DataContext、运行时替换和 converter 行为，再确定是生成 bindable 属性还是
局部 `x:Bind`。

## 7. 后续状态

4E-0 已按上述范围完成：6 处搜索历史 `OneWay x:Bind` 改为 `OneTime`，WMC1506 6→0，
WMC1510 保持 1265。profile 23 / schema 20、4E-0 契约 6/6、x64 全量 2073/2073 和
隔离 AOT 审计均通过。下一批调整为 4E-1 `PinStateIcon` 两条自引用 Foreground compiled
binding pilot，完整证据见 `aot-stage-4e-0-report.md`。
