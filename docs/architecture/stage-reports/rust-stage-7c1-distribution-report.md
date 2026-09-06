# DeskBox Rust / Native AOT 阶段 7C1 双架构分发报告

- 日期：2026-08-24
- 范围：Direct/Inno 与 Store/MSIX 的 x64、ARM64 Native AOT 构建及包内容审计
- 结论：双架构自动化分发门禁通过；阶段 7C1 的可自动化部分完成
- 证据级别：GitHub 托管原生架构构建和包审计，不等同于签名、安装、覆盖升级、WACK 或实体设备验收
- 下一阶段：7C2 合并 Store 上传包、签名/WACK、x64 覆盖升级、package flight 与实体 ARM64 补测

## 1. 结论

Direct x64/ARM64 与 Store x64/ARM64 现在使用同一套固定的 Rust 1.96.0、Native AOT、静态 CRT 和包
内容规则。GitHub Actions 在原生 x64 与原生 ARM64 Windows runner 上分别完成最终 AOT publish、
Inno 安装器编译、Store MSIX/appxsym/msixupload 生成、拆包、PE/导出/依赖/哈希检查，再由独立 job
聚合两种架构的通过摘要。

该结果关闭的是“源码能否稳定生成正确的双架构分发产物”。本轮没有使用证书，没有安装生成的安装器
或 MSIX，没有覆盖现有用户安装，也没有运行 WACK 或 Partner Center package flight。因此不能把绿色
Actions 扩大解释成“发布验收全部完成”。

## 2. 本轮实现

### 2.1 Store Native AOT payload

Windows App SDK 的 Native AOT 打包目标能够选择 AOT `DeskBox.exe`，但自定义 Rust DLL 不会自动进入
MSIX，同时旧的 `DeskBox.deps.json` 与 `DeskBox.runtimeconfig.json` 仍可能被 package payload 快照。
项目现在在 `_ComputeAppxPackagePayload` 前执行专用 target：

- 从 `PackagingOutputs` 移除 deps/runtimeconfig；
- 明确把静态 CRT `deskbox_native.dll` 加入 Store package payload；
- 把 `DeskBox.pdb` 与 `deskbox_native.pdb` 放入 `.appxsym`，不放入 MSIX；
- 保持 Store SearchCore 为 managed，不打包 `deskbox_search_core.dll`；
- 继续排除 Direct Updater 与 Direct 素材。

Store 审计会拆开 MSIX，验证正式 Partner Center identity、处理器架构、
`Microsoft.WindowsAppRuntime.2` framework dependency、AOT EXE 无 CLR header、Rust ABI/导出/静态
CRT、publish 与包内哈希一致，以及严格的禁止文件清单。

### 2.2 Direct Native AOT 安装器

x64 与 ARM64 Inno 脚本新增显式 `DeskBoxNativeAot` 模式。该模式只跳过不再需要的 .NET Desktop
Runtime 检测，仍保留 Windows App Runtime 2.2 检测与安装。ARM64 安装器在 AOT 模式明确包含
`deskbox_native.dll`；两种架构均排除 PDB 和 managed runtime 元数据，并保留 Direct SearchCore。

编排脚本从 `.iss` 读取版本和基础文件名，通过 Inno 的输出文件名覆盖参数生成带 `NativeAot` 后缀的
安装器。GitHub runner 暴露的命令行编译器实际验证了输出名、发布目录和参数绑定；期间发现的三处问题
均固定为契约：字符串宏引号不能进入输出文件名、发布目录宏不能重复嵌套引号、PowerShell 子脚本必须
使用命名哈希表 splat 而不是位置数组。

### 2.3 GitHub Actions 分发矩阵

`.github/workflows/distribution-audit.yml` 使用：

- x64：`windows-2025-vs2026`；
- ARM64：`windows-11-vs2026-arm`；
- 固定 .NET SDK 10.0.303 与仓库 Rust 1.96.0；
- Inno Setup 与 Windows SDK MakeAppx；
- 每架构独立证据产物和失败时仍上传的部分证据；
- 只有两架构摘要均为 `passed` 时才生成跨架构 manifest。

## 3. GitHub Actions 证据

最终提交：`ebbb8ecf341db9068b3bbf71c7101fd9c19ff886`

最终分发运行：[32650821484](https://github.com/Tianyu199509/DeskBox/actions/runs/32650821484)

| 项目 | x64 | ARM64 |
| --- | --- | --- |
| runner | `windows-2025-vs2026` | `windows-11-vs2026-arm` |
| OS / process | x64 / x64 | ARM64 / ARM64 |
| Direct AOT EXE/Updater | `0x8664` | `0xAA64` |
| Rust CRT | Static，无 VC runtime 导入 | Static，无 VC runtime 导入 |
| Direct 安装器 | 通过 | 通过 |
| Store MSIX/appxsym/msixupload | 通过 | 通过 |
| package payload/hash audit | 通过 | 通过 |
| 物理用户设备 | 未执行 | 未执行 |

跨架构摘要保存在：

`evidence/32650821484/stage7c1-cross-architecture-summary/stage7c1-cross-architecture-summary.json`

摘要状态为 `passed`，最终 SHA-256：

| 产物 | x64 | ARM64 |
| --- | --- | --- |
| Direct installer | `50EBF5E35B68D283FAD0513EC38F99DA43514D5ECBB9433D036AB2CF6D9C72C0` | `37BD8805F4074ABD5DB8B6D01DB0754C29754D3CEDA92ECDBAF5309620D19F0A` |
| MSIX | `E642C0ED55D78C1E0912C6DE862468DC82CB48CA8C9BA53E40932350E867CDDD` | `C2C8DEC410FDDABEA8219F10FB179E1726FEB93C88CC3459E3B59662BF676CDB` |
| MSIXUpload | `1FB2E4228AFF3E3679F9B388EC805C5067356EAA1928128B93AC066E6448D408` | `999CF7968EA252DB3A822221A09BAA1F1BA9021FEF875BA71AE6F19BEEC764B6` |

配套 ARM64 原生 ABI/SearchCore/CRT 回归也在同一主线重复通过：
[32650821493](https://github.com/Tianyu199509/DeskBox/actions/runs/32650821493)。

## 4. 本机回归

- Stage 7A + 7C1 定向契约：12/12；
- Inno/日期确定性修复定向测试：2/2；
- DeskBox x64 全量：2535/2535；
- PowerShell 解析与 `git diff --check`：通过；
- canonical Debug 构建：0 error、24 个既有 warning；已从仓库规范路径重新启动并核对唯一进程；
- 本机 x64 Store Native AOT 拆包审计：71 个包内文件，AOT EXE 无 CLR header，Rust DLL 10 个导出，
  publish/package 哈希一致，`.appxsym` 含两个 PDB。

Todo 日历测试曾在周日深夜使用 `DateTimeOffset.Now.AddHours(1)`，跨到下周后产生空 ThisWeek 集合。
测试已改用当天中午，覆盖语义不变且不再依赖执行时刻。旧安装器契约也已从“ARM64 永不包含 Rust DLL”
更新为“ARM64 只在 Native AOT 模式包含 Rust DLL”。

## 5. 已关闭与未关闭的边界

已关闭：

- 双架构最终 Direct Native AOT publish、PE、Rust ABI/导出和静态 CRT；
- 双架构 Inno 安装器可编译及文件清单规则；
- 双架构 Store Native AOT MSIX、appxsym、单架构 msixupload；
- Store identity、framework dependency、payload、符号分离和 hash；
- GitHub 原生 ARM64 构建、Rust DLL 装载和 SearchCore 产品门禁。

仍未关闭：

- 沿用正式发布模型的 x64+ARM64 合并 `.msixbundle/.msixupload`；
- Authenticode/Store 证书链与时间戳；
- Windows App Certification Kit；
- Direct 安装、首次启动、不卸载覆盖升级、失败回滚和卸载；
- Store package flight 的不卸载升级、退出/重启和数据保留；
- 实体 ARM64 上的窗口、托盘、系统 UI、GPU/驱动、休眠恢复和全部 Widget 整进程内存。

## 6. 下一阶段建议

下一阶段定为 **7C2 发布外部证据与合并上传包**，复杂度中高，不再改写 Rust/WinUI 主体：

1. GitHub Actions 复用本轮两个已审计 MSIX，生成并拆包审计 x64+ARM64 `.msixbundle`，再封装与现有
   正式发布结构一致的 `.msixupload`；
2. 在可用的 Windows 环境执行 WACK，并把报告作为独立 artifact；
3. 在当前 x64 设备用隔离测试数据验证 Direct 首装、同 AppId 覆盖升级、退出/重启、卸载与数据保留；
4. 使用 Partner Center 小范围 package flight 验证 Store 1.4.x 不卸载升级；
5. ARM64 实体设备以后获得时补交互、系统集成和整进程内存，不阻塞上述可自动化工作。

原始“选择性 Rust + 主程序 Native AOT + 双架构分发门禁”目标按加权口径约完成 **98%**。剩余约 2%
主要是发布系统与实体设备证据，不是继续扩大 Rust 重构范围。

## 7. 参考资料

- [GitHub-hosted runner images](https://github.com/actions/runner-images/blob/main/README.md)
- [Windows ARM64 runner image inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows11-VS2026-Arm64-Readme.md)
- [Inno Setup command-line compiler](https://jrsoftware.org/ishelp/topic_compilercmdline.htm)
- [Windows App SDK packaged deployment](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-packaged-apps)
- [Windows App SDK CI guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/ci-for-winui3)
