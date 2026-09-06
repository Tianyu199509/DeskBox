# DeskBox Rust 阶段 7C0 CRT 与分发依赖决策报告

- 日期：2026-08-23
- 范围：两个 Rust DLL、x64/ARM64、动态/静态 MSVC CRT、PE 导入、体积、隔离进程内存与产品测试
- 结论：生产默认采用静态 CRT
- 分发结论：Direct 不为 Rust 额外安装 VC++ Redistributable；Store 不为 Rust 额外声明 VCLibs 依赖
- 下一阶段：7C1 安装包、MSIX、升级、签名、WACK 与 flight

> 2026-08-24 状态更新：7C1 的双架构 AOT publish、Direct 安装器、Store MSIX/appxsym/msixupload 与
> 包内容审计已通过。签名、WACK、安装/覆盖升级、合并双架构上传包和 package flight 转入 7C2。

## 1. 决策

`deskbox_native.dll` 与 `deskbox_search_core.dll` 在 x64 和 ARM64 均改为静态链接 MSVC CRT。动态版本
在两种架构的两个 DLL 中都直接导入 `VCRUNTIME140.dll`；静态版本均不再导入 `VCRUNTIME140.dll`、
`MSVCP*.dll` 或其他 VC runtime 模块。ABI、能力、导出和产品行为没有变化。

静态方案的代价是两个 DLL 合计增加约 0.18 MiB 文件体积和 0.18 至 0.20 MiB PE image size。这个增量
远低于为两种架构增加运行库检测、下载安装、Store 依赖和故障诊断的复杂度，也不会改变 SearchCore
索引所有权带来的主要整进程内存收益。因此生产默认由 `Dynamic` 改为 `Static`，动态模式只保留给 A/B
审计和诊断。

## 2. A/B 结果

### 2.1 x64 原生运行

证据：`.artifacts/rust-crt-stage7c0-local/rust-crt-stage7c0-evidence.json`

| 指标 | Dynamic | Static | Static - Dynamic |
| --- | ---: | ---: | ---: |
| 两 DLL 文件大小 | 318,976 B | 506,368 B | +187,392 B |
| 两 DLL SizeOfImage | 352,256 B | 540,672 B | +188,416 B |
| VC runtime 导入 | `VCRUNTIME140.dll` | 无 | 移除 |
| runtime ABI probe | 两个均执行 | 两个均执行 | 均通过 |
| 静态方案产品测试 | 不适用 | 11/11 | 通过 |

### 2.2 ARM64 原生运行

证据：GitHub Actions [32644378767](https://github.com/Tianyu199509/DeskBox/actions/runs/32644378767)
下载产物中的 `rust-crt-stage7c0-evidence.json`。

| 指标 | Dynamic | Static | Static - Dynamic |
| --- | ---: | ---: | ---: |
| 两 DLL 文件大小 | 312,320 B | 503,296 B | +190,976 B |
| 两 DLL SizeOfImage | 339,968 B | 544,768 B | +204,800 B |
| VC runtime 导入 | `VCRUNTIME140.dll` | 无 | 移除 |
| runtime ABI probe | 两个均执行 | 两个均执行 | 均通过 |
| 静态方案产品测试 | 不适用 | 11/11 | 通过 |

首轮 ARM64 Stage 7B 动态构建也通过 11/11；最终生产配置又在
[32645299871](https://github.com/Tianyu199509/DeskBox/actions/runs/32645299871) 中显式要求静态 CRT、无 VC
runtime 导入并重复运行同一 ARM64 产品门禁。

## 3. 内存数据解释

A/B 脚本在独立测试宿主中只测“加载两个 DLL 并调用 ABI”前后的 Private Bytes 与 Working Set。x64
三轮样本出现负 Private delta，ARM64 首轮动态样本也出现明显负异常值，说明进程启动、GC、页面回收和
共享页会覆盖不足 1 MiB 的模块差异。该数据适合发现数量级回归，不适合把几十 KiB 的中位数差解释成
DeskBox 常驻内存收益或损失。

可稳定比较的是 PE 数据：静态版本只增加约 0.2 MiB image，两 DLL 仍按需分页；SearchCore 在 11 个
Widget 全显示场景中已经证明的整进程 Private Bytes -12.85%、Working Set -9.36% 收益不由 CRT 选择
产生，也不会被这一级别的模块增量抵消。后续整机内存继续以真实 DeskBox 进程和同一数据集多轮中位数
为准，不使用本次微基准替代。

## 4. 实现门禁

- 两个 Rust build script 的默认 `CrtLinkage` 为 `Static`；
- `DeskBox.csproj` 与测试项目的 `DeskBoxRustCrtLinkage` 默认值为 `Static`；
- dynamic/static 使用不同 intermediate 与 Cargo target 目录，避免增量产物串用；
- 构建通过 `CARGO_ENCODED_RUSTFLAGS` 显式设置 `+crt-static` 或 `-crt-static`，完成后恢复调用方环境；
- PE parser 读取 import descriptor 和 `SizeOfImage`，静态模式发现任何 VC runtime 导入立即失败；
- ARM64 Stage 7B runner 强制传入 Static，且同时检查结果字段和空 `VcRuntimeImports`；
- A/B 脚本继续构建两种 linkage，防止生产默认切换后失去对照能力。

## 5. Direct 与 Store 结论

静态 CRT 只解决 Rust DLL 自身的 MSVC runtime 依赖：

- Direct x64/ARM64 安装器不需要因为 Rust 新增 `vc_redist` 检测、下载或安装；
- Store 包不需要因为 Rust 新增 `Microsoft.VCLibs` 依赖；
- Windows App SDK framework/runtime 仍按 DeskBox 既有 framework-dependent 策略单独处理；
- .NET Native AOT、Windows App Runtime、签名、证书和系统 API 兼容性仍需 7C1 安装包验证；
- 不能把“Rust 无 VC runtime 导入”扩大解释为“整个安装包没有任何框架依赖”。

## 6. 剩余门禁

7C0 关闭的是 CRT 选择，不是完整发布验收。仍需：

- x64/ARM64 最终 Native AOT publish 目录和 PE/依赖复审；
- Direct 安装器实际编译、安装、首次启动、卸载和无卸载覆盖升级；
- Store MSIX/upload 的文件清单、架构、框架依赖、签名和 WACK；
- 1.4.x 既有数据的设置、Widget、Quick Capture、Todo、索引与诊断日志保留；
- package flight 中的真实升级、退出/重启和目标系统验证。

## 7. 参考资料

- [Rust MSVC targets](https://doc.rust-lang.org/rustc/platform-support/windows-msvc.html)
- [Rust CRT linkage](https://doc.rust-lang.org/stable/reference/linkage.html)
- [Microsoft Visual C++ runtime redistribution](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170)
- [C runtime packages for Desktop Bridge](https://learn.microsoft.com/en-us/troubleshoot/developer/visualstudio/cpp/libraries/c-runtime-packages-desktop-bridge)
