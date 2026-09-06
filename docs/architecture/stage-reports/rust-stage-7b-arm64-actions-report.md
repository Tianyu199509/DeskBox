# DeskBox Rust / Native AOT 阶段 7B ARM64 GitHub Actions 运行报告

- 日期：2026-08-23
- 范围：原生 ARM64 Windows 托管机、两个 Rust DLL 的运行时 ABI、SearchCore 产品绑定、静态 CRT 最终配置
- 结论：ARM64 原生运行门禁通过；Direct ARM64 可默认启用 Rust SearchCore
- 证据级别：GitHub 托管 ARM64 运行证据，不等同于实体用户设备或交互式桌面验收
- 下一阶段：7C1 Direct / Store x64 与 ARM64 安装、升级和包内容矩阵

> 2026-08-24 状态更新：7C1 自动化分发矩阵已在原生 x64/ARM64 GitHub runner 通过；剩余签名、
> WACK、合并 Store 上传包、安装/覆盖升级和 package flight 已转入 7C2。

## 1. 结论

项目没有可用的实体 ARM64 设备，因此 7B 将可自动化的架构运行门禁迁移到 GitHub Actions 的
`windows-11-vs2026-arm` 原生 ARM64 Windows runner。该 runner 中 OS、PowerShell、.NET 测试宿主和
Rust host 均实际运行在 ARM64，不是 x64 主机上的交叉编译，也没有通过模拟返回值替代 DLL 加载。

两次隔离分支运行分别完成基线和最终生产配置验证。首轮证明动态 CRT 构建可在 ARM64 进程中加载；
第二轮把生产默认切换为静态 CRT，并要求运行门禁拒绝任何残留 `VCRUNTIME140.dll` 导入。两个 Rust
模块均保持冻结 ABI：`deskbox_native.dll` 为 ABI 2、能力 511、10 个导出，
`deskbox_search_core.dll` 为 ABI 3、14 个导出，machine 均为 `0xAA64`。

SearchCore 的 ARM64 测试实际创建索引、加入 Unicode 路径、seal 并查询结果，同时覆盖产品加载器与
原生后端测试。最终 Direct ARM64 构建因此与 Direct x64 一样默认定义
`DESKBOX_SEARCH_CORE_DEFAULT`；已经保存的用户设置继续优先，Store 构建仍使用 managed 后端。

## 2. 工作区与远端隔离

开始前创建的本地快照：

- `D:\project\wingezi-backups\DeskBox-stage7a-before-7b-actions-20260823T132349Z.zip`
- 文件数：40,258
- 大小：951,711,046 bytes
- SHA-256：`23B8C76DE91B4EB7CBD0B95600346B15A0997D471C9B26E93F423F3DF94A56AA`

远端 `main` 不包含当前大批 Rust/AOT 主线，因此 Actions 使用独立工作树
`D:\project\wingezi-actions-stage7b` 和分支 `codex/stage7b-arm64-actions`。推送前审计 399 个暂存路径，
排除了 `bin`、`obj`、`.artifacts`、安装包、归档、证书和超过 5 MiB 的文件；新增行没有凭据值。
该分支只用于验证，没有合并到 `main`。

## 3. 实现

### 3.1 双宿主工具链

`rust-arm64-msvc-environment.ps1` 不再假定调用进程为 x64。在 ARM64 Windows 上优先选择
`Hostarm64` MSVC 和 ARM64 Windows SDK 工具，缺少原生 host 工具时才回退到可用的 x64 host；在 x64
主机上继续使用既有 Hostx64 交叉编译路径。构建结果同时记录 OS、进程和目标架构，只有 host 与 target
一致时才允许执行运行时 ABI probe。

### 3.2 产品加载器

此前 `ShortcutNativeBackend` 与 `SearchCoreNativeBackend` 即使收到正确 ARM64 DLL，也会先被产品层的
x64-only 判断拒绝。该限制已改为只接受 x64 或 ARM64，并继续拒绝其他架构。这个修复是 7B 的实际产品
遗漏，不是测试放宽。

### 3.3 运行门禁

`run-arm64-stage-7b-runtime.ps1` 会：

1. 要求 OS 与 PowerShell 进程均为 ARM64；
2. 要求固定 `rustc 1.96.0` 且 host 为 `aarch64-pc-windows-msvc`；
3. 构建并实际加载两个静态 CRT ARM64 DLL；
4. 核对 ABI、能力、导出、PE machine、CRT 导入和 runtime probe；
5. 运行 `Arm64NativeRuntimeGateTests` 与 `SearchCoreNativeBackendTests`；
6. 输出带 runner、commit、哈希、TRX 计数和证据边界的 JSON。

本地 x64 负向执行也已确认：脚本在构建目标代码前失败，并写出失败证据，避免把交叉编译误记为原生
运行成功。

## 4. GitHub Actions 证据

工作流：`.github/workflows/arm64-runtime.yml`

| 项目 | 基线运行 | 最终静态 CRT 运行 |
| --- | --- | --- |
| 分支 | `codex/stage7b-arm64-actions` | `codex/stage7b-arm64-actions` |
| commit | `908bab38c4d8eb64a154f6e7dfe1c0d7955ba176` | `0b3b67a26d4795c840ba8db1fa06d8d9f45592bd` |
| run | [32644378767](https://github.com/Tianyu199509/DeskBox/actions/runs/32644378767) | [32645299871](https://github.com/Tianyu199509/DeskBox/actions/runs/32645299871) |
| runner | `win11-vs2026-arm64` | `win11-vs2026-arm64` |
| OS / process | ARM64 / ARM64 | ARM64 / ARM64 |
| .NET | 10.0.303 | 10.0.303 |
| Rust | 1.96.0 ARM64 host | 1.96.0 ARM64 host |
| Stage 7B | 11/11，通过 | 11/11，通过 |
| CRT A/B | 11/11 静态产品测试，通过 | 11/11 静态产品测试，通过 |

首轮结构化证据：

- `evidence/32644378767/arm64-stage7b-runtime-evidence/arm64-stage7b-runtime-evidence.json`
- `evidence/32644378767/arm64-stage7c0-crt-evidence/rust-crt-stage7c0-evidence.json`
- `status=passed`
- `targetArchitectureRuntimeExecuted=true`
- `physicalUserDeviceExecuted=false`
- `interactiveDesktopExecuted=false`

工作流在任何步骤失败时仍会保留可下载 JSON/TRX，验收不只依赖绿色状态图标。

最终生产配置证据：

- `evidence/32645299871/arm64-stage7b-runtime-evidence/arm64-stage7b-runtime-evidence.json`
- `evidence/32645299871/arm64-stage7c0-crt-evidence/rust-crt-stage7c0-evidence.json`
- Stage 7B 的 `native.crtLinkage` 与 `searchCore.crtLinkage` 均为 `Static`；两个 runtime probe 均执行；
- Stage 7C0 的两个静态 DLL `vcRuntimeImports` 均为空；静态产品测试 11/11；
- 两个 JSON 均为 `status=passed`，commit 与 Actions head SHA 均为 `0b3b67a2...`。

## 5. 已关闭与未关闭的边界

已关闭：

- ARM64 Rust host、MSVC/SDK 环境和真实目标进程；
- 两个 ARM64 DLL 的实际装载、ABI、能力、导出和 PE 架构；
- ARM64 SearchCore 的基本建索引、Unicode 查询和产品绑定；
- 静态 CRT DLL 在 ARM64 进程中的加载及产品测试；
- Direct ARM64 SearchCore 的编译默认值。

仍未由 GitHub 托管 runner 关闭：

- ARM64 Native AOT DeskBox 的交互式窗口、托盘、正常退出和系统 UI；
- 文件/应用结果的真人筛选与名称、大小、日期、类型双向排序；
- watcher、Explorer、通知、热键等真实用户输入或外部应用交互；
- 11 个 Widget 全显示且视觉不变时的 ARM64 整进程多轮内存；
- 实体 ARM64 设备上的 GPU、驱动、休眠恢复和设备差异。

因此本阶段足以开放 ARM64 后端默认值与分发开发，但不能写成“ARM64 实体设备全部验收完成”。没有实体
设备时，这些项目保留为发布前外部证据；以后拿到设备可直接复用本报告的相同 commit 和矩阵补测。

## 6. 下一阶段

下一阶段调整为 **7C1 Direct / Store x64 与 ARM64 分发矩阵**，复杂度高。优先顺序：

1. 以静态 CRT 最终配置重新完成 x64 与 ARM64 Native AOT publish 审计；
2. 构建 Direct x64/ARM64 安装器，核对文件清单、架构、PDB 排除、Windows App Runtime 检测与安装；
3. 构建 Store x64/ARM64 MSIX/upload，核对框架依赖和 Rust 模块策略；
4. 做无卸载覆盖升级、设置与数据保留、退出/重启、回滚边界；
5. 在可用环境执行签名、WACK 和小范围 package flight。

GitHub Actions ARM64 后端门禁应保留为后续每次 Rust ABI、工具链、CRT 或 ARM64 默认策略变化的回归项。
