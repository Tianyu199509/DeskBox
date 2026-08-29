# DeskBox

**本地优先的 Windows 10/11 桌面整理工具：用格子管理文件、文件夹、时光、待办、随记、搜索、天气和音乐。**

简体中文 | [English](README.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![最新版本](https://img.shields.io/badge/release-1.4.8-2563EB.svg)](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.4.8)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](#环境要求)
[![x64 and ARM64](https://img.shields.io/badge/architecture-x64%20%7C%20ARM64-5C2D91.svg)](#下载)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

![DeskBox Windows 桌面整理工具，包含文件、待办、搜索、天气和音乐格子](docs/images/brand/readme-hero-1-3-7-dark-zh-cn.png)

DeskBox 基于 C#、WinUI 3 和 Windows App SDK 构建，在原生 Windows 桌面上增加一层轻量格子，但不会替换资源管理器，也不会改变文件原本的使用方式。你可以创建真实文件夹支撑的文件格子、映射已有文件夹、用时光格子保留日期与农历、记录待办与随记、搜索电脑内容、查看天气或控制当前音乐。格子既能保持展开，也能收起成胶囊，并可通过托盘或全局快捷键临时唤起。

## 桌面上的 Mica 与 Acrylic

DeskBox 使用贴近 Windows 原生体验的材质，同时保留普通桌面文件与文件夹原本的使用方式。

| Mica 云母 | Acrylic 亚克力 |
| --- | --- |
| ![DeskBox 中文界面的 Windows 11 云母材质桌面格子](docs/images/screenshots/zh-cn/云母材质.png) | ![DeskBox 中文界面的 Windows 11 亚克力材质桌面格子](docs/images/screenshots/zh-cn/亚克力材质.png) |

## DeskBox 概览

| | |
| --- | --- |
| **支持平台** | Windows 10/11，x64 与 ARM64 |
| **技术栈** | C#、WinUI 3、.NET 10 Native AOT、Windows App SDK 2.4、Rust 原生 Shell 层 |
| **数据方式** | 本地优先；文件、随记、待办、设置与布局保存在电脑上 |
| **界面语言** | 简体中文、繁體中文、English、日本語、Deutsch、Português do Brasil、हिन्दी、Español、Français、العربية、বাংলা、Русский |
| **开源协议** | GPL-3.0-only |

12 种可选语言使用一致的资源键和格式化占位符覆盖范围。

## 下载

当前线上稳定版为 DeskBox 1.4.8，可从 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases/tag/v1.4.8) 下载。

- [DeskBox 1.4.8 x64 安装包](https://github.com/Tianyu199509/DeskBox/releases/download/v1.4.8/DeskBox_Setup_1.4.8_x64.exe)，推荐大多数 Intel 和 AMD 电脑使用。
- [DeskBox 1.4.8 ARM64 安装包](https://github.com/Tianyu199509/DeskBox/releases/download/v1.4.8/DeskBox_Setup_1.4.8_arm64.exe)，推荐骁龙、Surface Pro X 等 Windows on ARM 电脑使用。

两个安装包都是 Native AOT 构建，不再下载、也不再需要单独的 .NET 10 运行时。安装程序会检查 Windows App Runtime 2.4，只有当前架构缺少它时才联网安装；运行时安装完成后，少数电脑可能需要重启。完全离线的电脑请先手动安装对应架构的 Windows App Runtime 2.4。

每个安装包都提供同名的 `.sha256` 校验文件。安装包目前尚未进行 Authenticode 签名，介意者请在运行前核对哈希。

> DeskBox 本体默认安装到当前用户目录。

## 核心功能

### 文件整理与文件夹格子

- 创建由普通文件夹支撑的收纳格子，或把已有文件夹直接映射到桌面，不改变原文件位置。
- 支持图标/列表布局、标题样式、详细信息和路径开关、手动或规则排序、显示密度设置；每个格子可单独覆盖全局图标大小，文件名可选单行、双行或隐藏，格子最小可调整到 50×50。
- 可直接调整项目顺序、把文件移动或复制到格子内的文件夹；新建文件夹时自动滚动到对应位置并进入名称输入，手动顺序可在重启后恢复。
- 叠放拆分为叠放总开关与自动归组开关；点击叠放可在格子布局内展开，或使用自适应、3×3、5×5 独立弹窗，弹窗与格子共用图标大小、密度、选中状态和 Ctrl+鼠标滚轮缩放，并始终保持在屏幕边缘以内。
- 支持文件和快捷方式拖入、拖出、复制、剪切、粘贴、重命名、删除与在资源管理器中显示；拖放可选择跟随 Windows 默认的复制/移动判断，正确处理跨磁盘和修饰键创建快捷方式，并使用原生拖放图像与目标说明。
- Windows Shell 复制和移动会显示逐项进度标记，并在传输期间保护来源、目标和接收文件夹，减少互相冲突的操作。
- 支持创建快捷方式、带二次确认与部分失败提示的永久删除、仅对选中目标生效的使用管理员身份打开，以及靠近鼠标位置弹出的“更多”系统菜单。
- 可从资源管理器、微信或浏览器拖入内容；浏览器中的远程图片与文件链接可以下载后导入。
- 已运行 [QuickLook](https://github.com/QL-Win/QuickLook) 时，可在格子中按空格预览支持的文件。

### 格子组与桌面整理

- 文件格子可以在不改变底层文件夹的情况下合并成组，并通过标题、鼠标滚轮或可循环的 Ctrl+Tab 快捷键切换成员。
- 支持安全拆出成员或解散格子组；组内与独立文件格子共用视图、设置、菜单、排序、拖放和 QuickLook 交互。
- 桌面整理会先按类别预览将要移动的内容，每类可选择新建文件夹或复用已有格子。
- 可选择把保留文件夹、大文件和快速批次之外的项目也纳入整理，并按访问被拒、文件占用、已变更、目标不可用或传输失败分别说明项目为何保留，而不是静默跳过。
- 可在下载、解压和同路径替换达到稳定状态后，自动整理新出现的桌面文件。

### 待办与随记

- 待办与随记使用响应式列表/详情布局，宽屏可双栏展示并调整列表宽度，窄屏会自动切换为单页浏览。
- 待办支持截止日期、提醒、重复、颜色标记、Markdown 备注、多附件、筛选与批量操作。
- 随记支持文本、链接、图片和文件，提供固定、纸张样式、Markdown 编辑与预览、附件删除和专注编辑。
- 附件可以关联原文件，也可以复制到 DeskBox 管理的数据目录。

### 桌面搜索

- 在一个搜索弹窗或搜索格子中查找文件、文件夹、应用、设置与随记、待办内容。
- 文件结果通过本机 IPC 读取 Everything 已有索引，并与 DeskBox 内容在同一窗口合并展示，DeskBox 不再维护重复的文件索引。
- 设置中可检测或启动 Everything、选择其程序位置、查看连接与权限状态、启用高级语法并过滤低价值的系统与缓存路径。Everything 不随 DeskBox 捆绑，需要单独安装。
- 支持结果筛选、可排序详细列、数量设置、历史、收藏和独立全局快捷键。
- 支持 Ctrl/Shift 多选、带边缘自动滚动的框选，以及对选中结果执行批量操作。
- 搜索结果按阶段增量返回；单个来源异常时会被隔离，不影响其他来源继续工作。
- 空闲时预热搜索弹窗外壳，点击搜索格子后优先显示并聚焦窗口，推荐内容和图标在后台恢复；长期隐藏的搜索窗口会释放界面树，关闭搜索功能则释放完整搜索运行资源。

### 时光、天气与音乐

- 时光格子常驻显示日期、星期、农历与节气节日，可自定义背景图片或轮播，并单独设置背景透明度。
- 天气格子可展示实时天气、逐小时和多日预报，默认使用 MSN 天气，失败时自动回退到 Open-Meteo。
- 天气提供跟随明暗模式的标准皮肤和按天气变化的高级皮肤，日/周视图会随格子尺寸响应式调整；启动时先使用仍然新鲜的缓存预报，刷新工作不占用交互路径。
- 音乐格子通过 Windows 媒体会话控制当前播放器，支持播放模式、进度和系统音量，也可在多个媒体会话间切换或跟随系统选定的来源。
- 音乐提供封面、控制、唱片与紧凑布局，并可选择跟随专辑封面的氛围色。

### 胶囊模式与原生交互

- 格子可收起为智能胶囊，支持点击切换或鼠标悬停自动展开；胶囊设置以“展开方式”为主控制项，并提供现成的悬停预设。
- 收起后可显示关键信息、简要摘要或仅图标与标题；待办和随记可隐藏敏感正文。
- 胶囊可以独立摆放，也可以组合成可整体移动、可排序的胶囊栏。
- 操作叠放弹窗、右键菜单、拖放、标题编辑或关闭确认时，悬停展开的胶囊和格子组会保持展开，直到交互结束且鼠标移出后才收起。
- 可通过托盘、F7、双击 Ctrl、Alt+Space、Win+Space、单独按一下 Win、自定义快捷键，或可选的双击桌面空白区域显示、隐藏全部格子；会改变 Windows 行为的组合在启用前会明确提示，修饰键组合和未完成按键不会误触发。
- “快捷唤起层”可临时把格子显示到其他窗口上方，不永久改变其桌面层级行为，并保留用于第一次操作的点击。
- 连续触发会串行处理，并可在显示器、DPI、睡眠唤醒和资源管理器变化后恢复。
- 支持云母/亚克力材质、透明度、边框、DWM 圆角、动画、标题栏、图标与文字大小；格子文字和单色控件可跟随主题，也可使用浅色、深色、自定义和单格子覆盖配色，并可选文字描边。

### 布局、显示器与性能

- DeskBox 为不同的显示器拓扑分别保存格子布局。重新接入用过的屏幕组合后，会恢复该组合对应的位置、尺寸、格子组表面和胶囊位置；热插拔、工作区和 DPI 变化会先等待稳定再恢复，切换期间暂停写入布局，避免临时坐标覆盖已保存的布局。
- 更换显示器或缩放比例变化时，会按可用工作区映射出限制在屏幕范围内的比例布局，不会把格子留在屏幕外。
- 按住 Ctrl 拖动格子标题，可把当前显示器上的可移动格子作为一个整体移动。吸附在移动和调整尺寸时都生效，可设置相邻格子间距，并保证贴近屏幕边缘时仍在可用工作区内。
- 设置 > 常规的“性能与资源”提供均衡、节省资源和自定义三种模式；自定义可分别控制格子隐藏后的缓存回收、可见闲置回收、临时窗口释放、图标/缩略图/解码图片缓存容量，以及文字跑马灯、唱片旋转、时光图片切换和胶囊光效等持续动画。
- 隐藏和非活动格子会按所选策略释放可重建的界面、解码图片、图标和缩略图；同时通过复用进程级 WinRT 设置对象、共享画刷、缓存窗口工厂和只更新变化条目，减少热路径上的重复分配。窗口动画会按当前显示器刷新率调整节拍，并为 Windows 10 增加帧节奏和背景材质保护。
- 开机会等待资源管理器桌面图标环境稳定后再挂接桌面层格子，避免干扰系统自身的图标位置恢复；收纳磁盘临时断开时格子保持完整，磁盘回来后自动恢复。

### 更新、备份与诊断

- 支持应用内检查更新，在独立界面阅读较长的更新日志；下载失败时可重试或前往官网继续下载。
- DeskBox 关闭后会显示安装界面；升级会复用并锁定原安装路径，避免生成第二份应用。
- 支持设置备份与恢复，并可导出经过隐私过滤的一键诊断包用于排查问题。
- 设置使用可恢复快照，退出时刷新待保存内容；保存失败会明确记录和提示，不再静默恢复默认配置。

## 1.4.8 更新亮点

- **更安全的收纳文件交接。** DeskBox 可以在桌面保留独立的 `DeskBox Files.lnk` 快捷方式，卸载时如果仍有收纳文件，安装程序会询问是否创建或保留这条入口。
- **Windows 10 圆角兼容。** Windows 10 的外框和胶囊媒体内图统一使用直角；Windows 11 继续跟随用户保存的圆角设置。
- **天气默认样式更简洁。** 新安装和恢复默认设置使用简洁的标准天气样式，丰富样式仍可手动选择。
- **搜索键盘操作更可靠。** 上下键会同步选中结果和高亮；按 Ctrl+Tab 切换搜索 Tab 后，方向键仍用于选择文件，不会只滚动列表。
- **搜索 Tab 更干净。** Tab 只保留文字，宽度按内容适配，文字两侧留出更舒适的间距，并使用更高的指示条。
- **Windows 集成更稳定。** 同时包含目录联接/符号链接访问、Shell 原生确认框、文件夹监视退避、虚拟显示器恢复和高 DPI 叠放布局修复。

## 1.4.7 更新亮点

- **更安全的“更多系统操作”。** 扩展 Windows Shell 菜单现在在独立辅助进程中运行，第三方 Shell 扩展异常不会连带结束 DeskBox。
- **高 DPI 叠放网格稳定。** 3×3 布局放入五个文件时，在分数 DPI 缩放下仍保持第一行 3 个、第二行 2 个，不再错误排成 2+2+1。
- **桌面层级切换更稳定。** Explorer 拖拽和激活状态变化期间，隐藏格子保持隐藏；展开胶囊仍保持正确的同层级顺序。
- **时光日历 AOT 绑定修复。** Direct Native AOT 构建保留日历日期装饰数据的绑定元数据。
- **修复严重的快捷方式误删。** 部分系统下把 `.lnk` 快捷方式在格子间拖动会连带删除原文件并进入回收站。修复后 DeskBox 会等文件真正搬运完成，再向系统确认这次操作的结果。
- **性能模式与资源控制。** 新增均衡、节省资源和自定义三种模式，可控制缓存保留、临时窗口释放和各项持续动画，隐藏与非活动界面更有计划地释放资源。
- **多显示器布局记忆。** 为不同屏幕组合分别保存布局，重新接入后恢复位置、尺寸、分组和胶囊；新显示器或缩放变化时获得限制在屏幕内的比例布局。
- **更多唤起方式。** 新增双击 Ctrl、Alt+Space、Win+Space、单独按 Win 和双击桌面空白区域，并提供可临时把格子提到其他窗口上方的快捷唤起层。
- **文件叠放 2.0。** 手动叠放与自动归组拆分为独立开关，叠放可在格子内展开或使用自适应、3×3、5×5 弹窗，并复用格子同款图标、密度与缩放控制。
- **Everything 文件搜索。** 直接读取 Everything 已有索引并与 DeskBox 内容合并，删除了重复的自建索引；Everything 需单独安装。
- **Native AOT 直发包。** GitHub 包不再需要单独的 .NET 10 运行时，Windows App Runtime 升级到 2.4。

完整内容见 [更新日志](CHANGELOG.md) 和 [1.4.8 发布说明](docs/releases/v1.4.8.md)。

## 当前界面

以下图片用于展示当前 DeskBox 的设置界面。

### 设置

| 常规 | 外观 |
| --- | --- |
| ![DeskBox 中文常规设置](docs/images/screenshots/zh-cn/常规.png) | ![DeskBox 中文外观设置](docs/images/screenshots/zh-cn/外观.png) |

| 胶囊模式 | 文件格子 |
| --- | --- |
| ![DeskBox 中文胶囊模式设置](docs/images/screenshots/zh-cn/胶囊模式.png) | ![DeskBox 中文文件格子设置](docs/images/screenshots/zh-cn/文件格子.png) |

| 功能格子 | 快捷与交互 |
| --- | --- |
| ![DeskBox 中文功能格子设置](docs/images/screenshots/zh-cn/功能格子.png) | ![DeskBox 中文快捷与交互设置](docs/images/screenshots/zh-cn/快捷与交互.png) |

## 本地数据与隐私

DeskBox 不要求注册账号，也不依赖云同步。格子配置、待办、随记、搜索历史、窗口布局和收纳文件都保存在本机。

以下功能会按使用意图联网：

- 天气数据来自 MSN 天气或 Open-Meteo。
- 更新检查访问 DeskBox 更新服务或 GitHub Releases。
- DeskBox 1.4.8 及后续 Full 安装包内置匹配架构的 Windows App Runtime；更早的直发安装器会在缺少运行时时联网下载。
- 从浏览器拖入远程链接时，只有确认导入的内容会被下载。

胶囊隐私选项只是在收起状态下隐藏部分文字，属于展示控制，并不等同于文件加密。

## 环境要求

- Windows 10 21H2（build 19044）或更高版本；Windows 11 22H2 或更高版本可获得完整视觉效果。
- 与安装包匹配的 x64 或 ARM64 处理器。
- Windows App Runtime 2.4。DeskBox 1.4.8 及后续 Full 安装包内置匹配架构的专用运行时，Native AOT 版本不再需要单独的 .NET 10 运行时。

Windows 10 会自动降级不受系统支持的材质、圆角和部分动画；文件同步、拖放与格子核心功能仍按兼容基线验证。

## 安装、更新与卸载

DeskBox 使用 Inno Setup 安装器，默认安装到当前用户目录。覆盖安装会保留应用设置、格子配置和收纳目录。旧版如果安装在 Program Files，安装器会进行迁移，以避免管理员权限进程影响资源管理器拖拽。

开机自启会静默启动到托盘。DeskBox 已运行时，再启动一个实例会直接退出，不会重复打开设置窗口。

开机自启使用当前用户的 Run 注册表项，因此 DeskBox 会出现在“Windows 设置 → 应用 → 启动”中；旧的计划任务注册会在安全的前提下自动迁移，在系统侧关闭 DeskBox 后应用内开关也会同步显示为关闭。

卸载时会明确提供“保留应用数据”和“彻底删除应用数据”两个选择。彻底删除会清理 `%LocalAppData%\DeskBox`、`%LocalAppData%\DeskBox-Recovery`、临时文件和 DeskBox 自己创建的注册信息；收纳路径中的用户文件始终保留。静默卸载默认保留应用数据，管理员只有显式传入 `/PURGEUSERDATA` 才会执行彻底清理。

## 常见问题

### DeskBox 会替换 Windows 桌面吗？

不会。Windows 资源管理器仍是桌面外壳，文件也仍是普通文件和文件夹。DeskBox 只是在现有桌面上增加独立管理的格子。

### DeskBox 把数据保存在哪里？

- 应用设置和格子数据：`%LocalAppData%\DeskBox\data`
- 新用户收纳目录：优先使用空间充足的非系统固定磁盘，例如 `D:\DeskBox\用户名`；没有合适磁盘时回退到 `%UserProfile%\DeskBox`

两类数据都可以通过 DeskBox 设置中的备份功能进行备份。

### 应该下载 x64 还是 ARM64？

绝大多数 Intel、AMD 电脑选择 x64；骁龙等原生 Windows on ARM 设备选择 ARM64。不确定时可在“Windows 设置 → 系统 → 系统信息 → 系统类型”中查看。

### 为什么安装时可能需要联网？

当前已发布的 1.4.7 及更早直发安装包可能会在缺少 Windows App Runtime 时联网下载。从 1.4.8 开始，标准命名的 x64、ARM64 Full 安装包会内置匹配架构的专用运行时，可在离线电脑上安装；Native AOT 版本不需要单独的 .NET 运行时。

### 关闭功能格子会删除内容吗？

不会。关闭功能会关闭对应界面并释放运行资源，但保存的数据和配置仍会保留，下次开启后继续使用。

## 从源码构建

开发需要 .NET 10 SDK 和 Windows 11 环境，推荐安装带 Windows App SDK 工作负载的 Visual Studio。发布时如果带上 `-p:DeskBoxRustNative=true`（正式版本使用），还需要 `rust-toolchain.toml` 指定的 Rust 工具链，用于编译快捷方式、系统音量、快速访问、回收站和资源管理器 Shell 相关的原生路径。

还原、测试并构建 x64 Debug 版本：

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64
dotnet test .\DeskBox.Tests\DeskBox.Tests.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

`scripts\publish-aot-retail.ps1` 是正式零售产物的权威入口，它会生成 Full Native AOT 载荷、内置 Windows App Runtime、编译对应架构的 Rust DLL、生成升级清单并校验产物：

```powershell
.\scripts\publish-aot-retail.ps1 -Platform x64
.\scripts\publish-aot-retail.ps1 -Platform ARM64
```

发布结果同时满足 .NET Native AOT 与 Windows App SDK 自包含要求。不要用裸 `dotnet publish` 替代这个脚本，安装器需要脚本生成的 `DeskBox.InstallManifest.txt` 才能安全清理旧载荷：

```powershell
ISCC.exe /DDeskBoxNativeAot=1 /DDeskBoxBundledRuntime=1 /DMyAppReleaseDir=..\.artifacts\aot-retail\win-x64\publish .\installer\DeskBox.iss
ISCC.exe /DDeskBoxNativeAot=1 /DDeskBoxBundledRuntime=1 /DMyAppReleaseDir=..\.artifacts\aot-retail\win-arm64\publish .\installer\DeskBox.arm64.iss
```

预期输出：

```text
Output\DeskBox_Setup_1.4.8_x64.exe
Output\DeskBox_Setup_1.4.8_arm64.exe
```

## 项目结构

```text
src\DeskBox                 WinUI 3 应用源码（格子外壳、服务、视图）
src\DeskBox.Updater         直发版更新辅助程序
native                      Rust 原生层、Shell ABI 与缩略图代理
tests\DeskBox.Tests         服务、策略与 AOT 契约测试
scripts                     构建、发布、审计与内存测量脚本
installer                   x64/ARM64 Inno Setup 脚本
docs\architecture           当前架构、原生 ABI 契约与 AOT 阶段记录
docs\user-guide             产品使用说明
docs\images                 README 与发布图片
docs\releases               版本发布文案和测试清单
.github\workflows           CI、ARM64 运行时与分发包审计
```

## 反馈与本地化

DeskBox 目前由个人独立开发和维护。为了保持架构一致性与后续版权边界，现阶段暂不接受外部 Pull Request；欢迎通过 [GitHub Issues](https://github.com/Tianyu199509/DeskBox/issues) 提交问题、功能建议、翻译和 UI/UX 反馈。

特别感谢 [@magisph](https://github.com/magisph) 提供巴西葡萄牙语本地化支持。

也可以访问 [deskbox.fun](https://deskbox.fun)，或通过应用“关于”页面中的联系方式反馈。

## 作者与协议

- 开发者：朱天雨
- 项目地址：<https://github.com/Tianyu199509/DeskBox>
- 开源协议：[GPL-3.0-only](LICENSE)

早期已按 MIT 协议发布的 DeskBox 版本继续保持原许可，协议变更不追溯历史版本；详情见 [LICENSE_CHANGE.md](LICENSE_CHANGE.md)。
