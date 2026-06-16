# DeskBox

中文 | [English](README.en.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4.svg)](#环境要求)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](#构建)

DeskBox 是一个基于 WinUI 3 的 Windows 11 桌面整理工具。它可以创建轻量桌面格子，用于收纳文件、映射文件夹，并通过托盘快速显示、隐藏或临时置顶这些格子，同时尽量保留 Windows 原生桌面的质感和使用习惯。

![DeskBox 产品封面](docs/images/DeskBox_产品封面_1280x720.png)

## 下载

可以在 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases) 下载最新版安装包。

当前版本：

- [DeskBox_Setup_1.0.3_x64.exe](https://github.com/Tianyu199509/DeskBox/releases/download/v1.0.3/DeskBox_Setup_1.0.3_x64.exe)

安装器会检测 .NET 8 Runtime x64 和 Windows App Runtime 2.1.3 x64。若目标电脑缺少运行时依赖，安装流程可以联网下载并安装。

## 1.0.3 更新

- 新增桌面格子显示/隐藏动画效果和动画速度设置，可在设置中选择滑动、淡入、缩放淡入或关闭动画。
- 优化动画底层实现，降低毛玻璃背景闪屏、黑底和图标二次动画的概率。
- 优化托盘左键逻辑：桌面格子被其他窗口遮挡时，点击托盘会稳定临时置顶，再次点击才隐藏。
- 优化托盘右键打开设置的体验，设置窗口会临时置顶展示，切到其他窗口后自动恢复普通层级。
- 优化新增组件、拖动组件、文件夹选择弹窗等场景的临时置顶行为。
- 增加性能日志辅助能力，便于后续定位卡顿和启动耗时问题。

完整更新记录见 [CHANGELOG.md](CHANGELOG.md)。

## 为什么做这个产品

很多桌面整理工具会接管桌面：替换原本的交互，重建一套文件入口，甚至让桌面变成另一个完整的管理容器。DeskBox 不想走这条路。它的目标是保留 Windows 原生桌面，只在文件整理这件事上补一层更轻的能力。

所以 DeskBox 选择了“移动式整理”的思路：桌面仍然是桌面，文件仍然是普通文件，格子只是帮你把文件移动、复制或映射到合适的位置。它不会试图成为新的桌面 Shell，也不会强迫你改变 Windows 原本的使用方式。

## 功能

- **收纳组件**：创建真实文件夹支撑的桌面格子，用于整理文件。
- **文件夹映射**：把已有文件夹展示为桌面格子，不改变原文件位置。
- **拖入后移动或复制**：可设置拖入收纳组件后的默认处理方式。
- **托盘管理**：新建组件、映射文件夹、显示/隐藏全部组件、临时置顶、打开设置、开机自启和退出。
- **原生文件操作**：拖入、拖出、粘贴、剪切、重命名、删除、打开、在资源管理器中显示和键盘快捷键。
- **外观调节**：支持主题、透明度、DWM 圆角、图标大小、文字大小、间距、文件名宽度和列表详情。
- **安全清理提示**：删除组件或清理收纳目录时明确提示影响范围，避免误删用户文件。
- **新用户引导**：首次启动时配置关键默认项，也可以在设置中重新打开。

## 截图

### 桌面格子

![DeskBox 浅色模式](docs/images/light-mode.png)

![DeskBox 深色模式](docs/images/dark-mode.png)

### 设置

![DeskBox 设置](docs/images/PixPin_2026-06-12_18-18-39.png)

### 新用户引导

![DeskBox 新用户引导](docs/images/PixPin_2026-06-12_18-20-17.png)

## 环境要求

- Windows 11。
- .NET 8 Runtime x64。
- Windows App Runtime 2.1.3 x64。

当前项目主要在 Windows 11 下测试。Windows 10 或其他系统版本尚未完整验证。

开发环境需要 .NET 8 SDK。推荐使用安装了 Windows App SDK 工作负载的 Visual Studio 2022。

## 安装和卸载

安装器基于 Inno Setup 构建。覆盖安装会保留现有应用设置、组件配置和收纳目录内容。

开机自启会静默启动到托盘。如果 DeskBox 已经运行，登录时再次启动的实例会直接退出，不会弹出设置页。

卸载时安装器会先停止正在运行的 DeskBox。收纳目录中的用户文件不会被静默删除；当清理可能影响用户文件时，会先提示确认。

## 构建

还原并构建：

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
```

运行测试：

```powershell
dotnet test .\DeskBox.sln --configuration Debug --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:minimal
```

生成 Release x64 输出和安装包：

```powershell
dotnet publish .\src\DeskBox\DeskBox.csproj --configuration Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false -o .\artifacts\publish\DeskBox\x64 -v:minimal
& 'C:\Program Files\Inno Setup 7\ISCC.exe' .\installer\DeskBox.iss
```

安装包输出：

```text
Output\DeskBox_Setup_1.0.3_x64.exe
```

## 项目结构

```text
src\DeskBox                 WinUI 3 应用源码
tests\DeskBox.Tests         核心服务测试
installer                   Inno Setup 安装脚本
docs\images                 README 和发布截图资源
```

## 数据位置

- 应用设置保存于 `%LocalAppData%\DeskBox\data`。
- 默认收纳路径为 `%UserProfile%\DeskBox`。
- `bin`、`obj`、`Output`、`artifacts` 和 `TestResults` 等生成目录已被 Git 忽略。

## 反馈

DeskBox 仍处于早期公开版本。如果你遇到文件拖拽、运行时依赖、窗口层级、卸载残留或不同 Windows 版本兼容性问题，欢迎通过 [Issues](https://github.com/Tianyu199509/DeskBox/issues) 提供复现路径。

## 作者

- 开发者：朱天雨
- 开源仓库：<https://github.com/Tianyu199509/DeskBox>

## 开源协议

本项目使用 [MIT License](LICENSE) 开源。
