# Glance NativeAOT 原生模块试点

这是 [官方功能包执行计划](../../docs/architecture/official-widget-packages-plan.md) 批次 B 的第一项可运行证据。独立 NativeAOT 宿主通过 C ABI 加载独立 NativeAOT DLL，DLL 读取外置 XAML、创建 WinUI 控件并提供绑定对象。宿主没有引用包项目，也没有编译 Glance 的业务实现。

## 已验证

2026-09-08，Windows x64、.NET SDK 10.0.303、Microsoft.WindowsAppSDK 2.4.0。

脚本只发布一次宿主，随后分别发布两版 DLL，并启动两个使用相同宿主文件的新进程。v1 链接生产代码 `GlanceCalendarLayoutCalculator.cs`；v2 在本次输出目录中复制同一计算器，仅将紧凑模式阈值从 320 改为 360。生产计算器没有改动。

| 证据 | v1 | v2 |
| --- | --- | --- |
| 宿主 `RuntimeFeature.IsDynamicCodeSupported` | false | false |
| 模块版本 | 1 | 2 |
| 输入高度 340 的业务计算值 | 244 | 268 |
| 实际 CalendarView 高度 | 244 | 268 |
| 原生模块对象绑定的标题 | Glance native package v1 | Glance native package v2 |
| 原生 DLL 大小 | 3,911,168 字节 | 3,911,168 字节 |

两次宿主 SHA-256 均为 `FFADB7F0377D2A54653E5FD9991BA2757288EF7142EF0C0C5127B2FF7EA43A61`。

v1 DLL SHA-256 为 `1838EDE00D38355FB25E42185EEB5E7CBB6083A3A716C54754083C49C25EE407`。

v2 DLL SHA-256 为 `BAD30302C26B0AE3A6CBCBD2017F1035B086C2EB40165DF812A310F2A0D2CD31`。

本地原始证据在 `.artifacts/glance-native/runs/20260908-161708-028-x64/summary.json`，两份 `result-v*/view.png` 已检查。二进制和运行证据不入库；重新运行会产生新的独立输出目录，哈希可能因构建而变化。

## 复现

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/spike/run-glance-native.ps1
```

需要当前仓库的 .NET、MSVC、Windows App SDK 构建环境。项目放在 `spikes/`，不进入应用的项目引用或发布流水线。正常和 AOT 两份依赖锁均保留。脚本不会清空历史运行目录。

脚本检查两版包的业务结果、实际布局、标题绑定、进程退出码、截图存在以及宿主哈希一致。模块加载后保留到进程退出，不调用 FreeLibrary。参数 `-Platform ARM64 -BuildOnly` 可用于后续 ARM64 编译验证；本轮仅执行了 x64。

## 发现的集成要求

- 宿主需要正常生成的 WinUI XAML 元数据及 App 资源初始化。最初仅以 C# 创建 XamlControlsResources 时在初始化处出现 `0xC000027B`；添加 App.xaml 后消失。
- 在 OnLaunched 创建内容和窗口。运行时 XAML 的 FindName 结果通过 C#/WinRT `As<T>()` 显式投影后访问；直接 CLR 强制转换在这次 AOT 场景中失败。
- 模块返回 IInspectable 的拥有引用，宿主创建投影后释放该 ABI 引用。宿主与模块不直接交换普通托管对象；绑定对象使用 C#/WinRT 生成的可绑定属性实现。
- 相同 Windows App SDK 版本下，本机可以加载、显示并跨 ABI 绑定。不同版本组合仍需单独验证。

## 尚未验证

该试点只使用生产 Glance 的布局计算器，界面为小型 CalendarView/TextBox 切片。完整 Glance 的日期样式、农历、图片、设置与业务服务尚未迁出主程序，待办编辑和持久化切片也尚未实现。

外置 XAML 是文本资源，尚未证明包内编译 XAML/XBF、PRI、本地化、自定义 WinRT 类型激活与依赖分发。输入框显示不代表物理键盘/IME、焦点迁移已经通过。销毁重建、叠放/合并迁移、多包同时运行、工作集及冷启动测量、ARM64 设备、1.5.0 升级与 Store 渠道均待验收。

宿主命令行仅接受显式开发包目录，这里没有接入产品安装、签名、授权或实例恢复。官方原生包执行在进程内，必须作为可信代码管理。试点通过允许继续完善正式边界，不表示批次 B 或六功能迁移已经完成。
