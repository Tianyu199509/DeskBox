# DeskBox

简体中文 | [English](README.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D4.svg)](#环境要求)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](#构建)

DeskBox 是一个基于 WinUI 3 的 Windows 11 桌面整理工具。它用轻量桌面格子帮你收纳文件、映射文件夹，并通过托盘或全局快捷键快速显示、隐藏、临时置顶这些格子。DeskBox 不会替换 Windows 桌面，只是在原生桌面之上补一层更好整理和访问文件的能力。

![DeskBox 产品封面](docs/images/brand/product-cover-1280x720.png)

## 下载

可以在 [GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases) 下载最新版安装包。

当前版本：1.1.7

- [DeskBox_Setup_1.1.7_x64.exe](https://github.com/Tianyu199509/DeskBox/releases/download/v1.1.7/DeskBox_Setup_1.1.7_x64.exe)

安装器会检测 .NET 8 Runtime x64 和 Windows App Runtime 2.1.3 x64。若目标电脑缺少运行时依赖，安装流程可以联网下载并安装。

## 1.1.7 更新

- 修复全新安装后开关按钮样式不生效的问题。

## 1.1.6 更新

- 修复从托盘右键打开设置页面后一直置顶的问题。
- 优化格子 z-order 可靠性，缩短安全定时器。

## 1.1.5 更新

- 修复剪贴板监控重启后不生效的问题。
- 修复格子交互后可能卡在顶部的 z-order 问题。
- 统一设置界面所有开关样式。
- 精简圆角选项，移除"系统默认"。

## 1.1.4 更新

- 修复批量唤起格子后点击外部窗口时，部分格子仍留在浏览器上方的层级问题。
- 优化启动初始化，启动速度提升 2-3 秒。
- 为关键异步事件处理器添加错误处理，防止崩溃。

## 1.1.3 更新

- 优化格子动画性能，使用 GPU 加速透明度和原生贝塞尔缓动曲线。
- 精简动画设置，统一滑动方向下拉框和缓动强度控制。
- 修复文件格子和随记格子动画效果不一致的问题。
- 新增图片文件缩略图预览。
- 修复关闭"双击打开"后单击无法打开文件的问题。
- 修复右键点击文件时误触发单击打开的问题。
- 修复框选逻辑吞掉点击事件导致失效的问题。
- 移除"唤起后仅保留点击的格子"设置，所有格子统一显示和隐藏。

## 1.1.2 更新

- 修复收纳格子内快捷方式（`.lnk`）无法长按拖动的问题。

## 1.1.0 更新

- 新增拖拽异常诊断和一键修复。如果 Win10/Win11 遇到文件拖不进格子的问题，请先到设置中运行一键修复。
- 优化资源管理器拖拽兜底处理，收纳格子和映射格子会记录更完整的诊断信息，并兼容更多 Windows shell 拖拽格式。
- 修复格子内排序稳定性，使用更接近 Windows 的自然名称排序，新加入文件也会按当前排序方式插入。
- 优化随记文本操作：已保存文本双击进入随记内编辑，右键可选择“在记事本中编辑”。
- 新安装和恢复默认设置时，托盘图标默认改为彩色。
- 优化新用户引导，只在首次安装后自动弹出，之后可在设置中手动查看。
- 优化卸载体验，可选择是否同时删除本地应用数据。

完整更新记录见 [CHANGELOG.md](CHANGELOG.md)。GitHub Release 可使用 [docs/releases/v1.1.1.md](docs/releases/v1.1.1.md) 中的中英文发布文案。

## 为什么做这个产品

很多桌面整理工具会接管桌面：替换原本的交互，重建一套文件入口，甚至让桌面变成另一个完整容器。DeskBox 选择更轻的方式：Windows 桌面仍然是 Windows 桌面，文件仍然是普通文件，格子只是帮你把文件移动、复制或映射到更合适的位置。

## 功能

- **收纳格子**：创建真实文件夹支撑的桌面格子，用于整理文件。
- **文件夹映射**：把已有文件夹展示为桌面格子，不改变原文件位置。
- **随记**：用可选的本地功能格子保存常用文本、链接、截图和最近复制内容。
- **拖入后移动或复制**：可设置拖入收纳格子后的默认处理方式。
- **托盘管理**：新建格子、映射文件夹、显示或隐藏全部格子、临时置顶、打开收纳目录、打开设置、开机自启和退出。
- **全局快捷键**：可用快捷键快速显示、隐藏或唤起格子。
- **原生文件操作**：拖入、拖出、粘贴、剪切、重命名、删除、打开、在资源管理器中显示和键盘快捷键。
- **外观调节**：支持主题、透明度、DWM 圆角、图标大小、文字大小、间距、文件名宽度、列表详情和动画效果。
- **收纳目录维护**：调整默认收纳路径、固定到快速访问、恢复孤立收纳文件夹，并在影响用户文件前确认操作。
- **新用户引导**：首次启动时说明核心概念和关键默认项，也可以在设置中重新打开。

## 截图

### 桌面格子

![DeskBox 浅色格子](docs/images/screenshots/zh-cn/widget-light.png)

![DeskBox 深色格子](docs/images/screenshots/zh-cn/widget-dark.png)

### 设置

![DeskBox 常规设置](docs/images/screenshots/zh-cn/settings-general.png)

![DeskBox 收纳设置](docs/images/screenshots/zh-cn/settings-storage.png)

### 新用户引导

![DeskBox 新用户引导](docs/images/screenshots/zh-cn/onboarding-step-1.png)

### 品牌动效

<p align="center">
  <img src="docs/motion/deskbox-motion-01-layer-assemble.svg" width="120" alt="DeskBox logo layer assembly animation" />
</p>

## 环境要求

- Windows 11。
- .NET 8 Runtime x64。
- Windows App Runtime 2.1.3 x64。

当前项目主要在 Windows 11 下测试。Windows 10 或其他系统版本尚未完整验证。

开发环境需要 .NET 8 SDK。推荐使用安装了 Windows App SDK 工作负载的 Visual Studio 2022。

## 安装和卸载

安装器基于 Inno Setup 构建，默认安装到当前用户目录。覆盖安装会保留现有应用设置、格子配置和收纳目录内容；旧版如果安装在 Program Files，安装器会自动迁移，避免 DeskBox 以管理员权限运行后影响资源管理器拖拽。

开机自启会静默启动到托盘。如果 DeskBox 已经运行，登录时再次启动的实例会直接退出，不会弹出设置页面。

卸载时安装器会先停止正在运行的 DeskBox，并让你选择是否删除 `%LocalAppData%\DeskBox` 下的本地应用数据。收纳目录中的用户文件不会被静默删除；当清理可能影响用户文件时，会先提示确认。

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
Output\DeskBox_Setup_1.1.1_x64.exe
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

DeskBox 仍处于早期公开版本。如果 Win10/Win11 遇到文件拖不进格子的问题，请先尝试“设置 -> 拖拽异常诊断 -> 一键修复”。如果仍有问题，可以扫码关注应用“关于”页里的公众号留言，或在 GitHub 提交 [Issue](https://github.com/Tianyu199509/DeskBox/issues)。

## 作者

- 开发者：Tianyu Zhu
- 开源仓库：<https://github.com/Tianyu199509/DeskBox>

## 开源协议

本项目使用 [MIT License](LICENSE) 开源。
