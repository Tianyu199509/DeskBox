# DeskBox

简体中文 | [English](README.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4.svg)](#环境要求)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](#构建)

DeskBox 是一个基于 WinUI 3 的 Windows 11 桌面整理工具。它用轻量桌面格子帮你收纳文件、映射文件夹、记录待办、随手记点东西，也可以在桌面上控制音乐。DeskBox 不会替换 Windows 桌面，只是在原生桌面之上补一层更好整理、更好访问、更容易临时唤起的能力。

![DeskBox 产品封面](docs/images/brand/product-cover-zh-cn-1280x720.png)

## 下载

可以在 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases) 下载最新版安装包。

当前版本：1.2.6

- [DeskBox_Setup_1.2.6_x64.exe](https://github.com/Tianyu199509/DeskBox/releases/download/v1.2.6/DeskBox_Setup_1.2.6_x64.exe)

安装器会检测 .NET 10 Runtime x64 和 Windows App Runtime 2.2 x64。若目标电脑缺少运行时依赖，安装流程可以联网下载并安装。

## 最新更新

- 待办提醒进一步完善：支持每条任务独立提醒、稍后提醒、重启后恢复提醒状态，并可在系统通知里直接完成或延后任务。
- 重复待办体验更清爽：完成后的重复历史会折叠在当前任务下方，不再把列表堆得越来越长。
- 随记切换更顺滑：优化 Tab 切换时的列表刷新、空状态布局、内容过渡动画和剪贴板默认联动，减少卡顿和错位。
- 待办和随记的多选复制、框选、拖拽文本和 Esc 取消选择继续补齐，日常批量整理更顺手。

完整更新记录见 [CHANGELOG.md](CHANGELOG.md)。

## 为什么做这个产品

Windows 桌面已经陪大家用了很多年，也是很多人每天最常用的地方。但它也很容易变乱：临时文件、截图、下载内容、待处理事项，最后都堆在一起。DeskBox 想做的是帮桌面多一层克制的整理能力，而不是把桌面变成另一个复杂系统。Windows 桌面仍然是 Windows 桌面，文件仍然是普通文件，格子只是帮你把它们收纳、映射、查看和临时唤起。

我也很喜欢 WinUI 的原生质感，所以 DeskBox 后续会一直尽量按 Windows 原生设计和交互规范做下去：WinUI 3 控件、Windows App SDK、DWM 圆角、亚克力质感、托盘优先的工作流。能用原生能力时会优先用原生能力，不会为了一个很小的效果随便引入很重的第三方库。安装包偏大主要是因为 WinUI / Windows App SDK 这套运行环境本身比较完整，不是 DeskBox 想做成一个臃肿的大而全工具。

## 功能

- **收纳格子**：创建真实文件夹支撑的桌面格子，用于整理文件。
- **文件夹映射**：把已有文件夹展示为桌面格子，不改变原文件位置。
- **待办格子**：支持快速输入、全屏编辑、自定义结束时间和原生化行内编辑。
- **随记**：用可选的本地功能格子保存常用文本、链接、截图和最近复制内容。
- **音乐格子**：支持播放控制、播放模式切换、系统音量调整和自适应频谱样式，可跟随封面氛围取色。
- **拖入后收纳**：拖入收纳格子的文件默认移动到对应的真实收纳文件夹。
- **托盘管理**：新建格子、映射文件夹、显示或隐藏全部格子、临时置顶、打开收纳目录、打开设置、开机自启和退出。
- **全局快捷键**：可用快捷键快速显示、隐藏或唤起格子。
- **原生文件操作**：拖入、拖出、粘贴、剪切、重命名、删除、打开、在资源管理器中显示和键盘快捷键。
- **外观调节**：支持主题、透明度、DWM 圆角、图标大小、文字大小、间距、文件名宽度、标题样式、列表详情和封面氛围背景。
- **收纳目录维护**：调整默认收纳路径、固定到快速访问、恢复孤立收纳文件夹，并在影响用户文件前确认操作。

## 截图

### 桌面总览

| 浅色主题 | 深色主题 |
| --- | --- |
| ![DeskBox 浅色桌面总览](docs/images/screenshots/zh-cn/desktop-light.png) | ![DeskBox 深色桌面总览](docs/images/screenshots/zh-cn/desktop-dark.png) |

### 核心格子

| 文件格子 | 待办格子 |
| --- | --- |
| ![DeskBox 文件格子列表视图](docs/images/screenshots/zh-cn/file-widget-list.png) | ![DeskBox 待办格子](docs/images/screenshots/zh-cn/todo-widget.png) |
| 随记格子 | 音乐格子 |
| ![DeskBox 随记格子](docs/images/screenshots/zh-cn/quick-capture-widget.png) | ![DeskBox 音乐格子](docs/images/screenshots/zh-cn/music-widget.png) |

### 设置页

| 常规 | 外观 |
| --- | --- |
| ![DeskBox 常规设置](docs/images/screenshots/zh-cn/settings-general-1-2.png) | ![DeskBox 外观设置](docs/images/screenshots/zh-cn/settings-appearance-1-2.png) |
| 文件格子 | 功能格子 |
| ![DeskBox 文件格子设置](docs/images/screenshots/zh-cn/settings-file-widgets-1-2.png) | ![DeskBox 功能格子设置](docs/images/screenshots/zh-cn/settings-feature-widgets-1-2.png) |

### 品牌动效

<p align="center">
  <img src="docs/motion/deskbox-motion-01-layer-assemble.svg" width="120" alt="DeskBox logo layer assembly animation" />
</p>

## 环境要求

- Windows 11。
- .NET 10 Runtime x64。
- Windows App Runtime 2.2 x64。

当前项目主要在 Windows 11 下测试。Windows 10 或其他系统版本尚未完整验证。

开发环境需要 .NET 10 SDK。推荐使用安装了 Windows App SDK 工作负载的 Visual Studio。

## 安装和卸载

安装器基于 Inno Setup 构建，默认安装到当前用户目录。覆盖安装会保留现有应用设置、格子配置和收纳目录内容；旧版如果安装在 Program Files，安装器会自动迁移，避免 DeskBox 以管理员权限运行后影响资源管理器拖拽。

开机自启会静默启动到托盘。如果 DeskBox 已经运行，登录时再次启动的实例会直接退出，不会弹出设置页面。

卸载时安装器会先停止正在运行的 DeskBox，并让你选择是否删除 `%LocalAppData%\DeskBox` 下的本地应用数据。收纳目录中的用户文件不会被静默删除；当清理可能影响用户文件时，会先提示确认。

## 构建

还原并构建：

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

运行测试：

```powershell
dotnet test .\DeskBox.sln --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

启动 Debug 应用：

```powershell
.\scripts\start-debug.ps1
```

生成 Release x64 输出和安装包：

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
& 'C:\Program Files\Inno Setup 7\ISCC.exe' .\installer\DeskBox.iss
```

安装包输出：

```text
Output\DeskBox_Setup_1.2.6_x64.exe
```

## 项目结构

```text
src\DeskBox                 WinUI 3 应用源码
tests\DeskBox.Tests         核心服务测试
installer                   Inno Setup 安装脚本
docs\images                 README 和发布截图资源
docs\motion                 品牌动效方案与 SVG 资源
docs\releases               GitHub Releases 发布文案
```

## 数据位置

- 应用设置保存在 `%LocalAppData%\DeskBox\data`。
- 默认收纳路径为 `%UserProfile%\DeskBox`。
- `bin`、`obj`、`Output`、`artifacts` 和 `TestResults` 等生成目录已被 Git 忽略。

## 反馈

DeskBox 仍处于早期公开版本。如果 Win10/Win11 遇到文件拖不进格子的问题，请先尝试"设置 -> 拖拽异常诊断 -> 一键修复"。如果仍有问题，可以扫码关注应用"关于"页里的公众号留言，或在 GitHub 提交 [Issue](https://github.com/Tianyu199509/DeskBox/issues)。

## 作者

- 开发者：Tianyu Zhu
- 开源仓库：<https://github.com/Tianyu199509/DeskBox>

## 开源协议

DeskBox 现在使用 [GPL-3.0-only](LICENSE) 授权。

此前已经以 MIT License 发布的 DeskBox 旧版本，仍然按 MIT License 授权。本次协议变更不追溯旧版本；详情见 [LICENSE_CHANGE.md](LICENSE_CHANGE.md)。
