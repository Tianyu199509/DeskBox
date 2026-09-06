# DeskBox Rust / Native AOT 阶段 7A ARM64 静态分发报告

- 日期：2026-08-23
- 范围：固定 ARM64 工具链、两个 Rust DLL、Direct ARM64 Native AOT 交叉发布、PE/导出/依赖/PDB/MSBuild 分发边界
- 结论：阶段 7A 通过；ARM64 交叉编译与静态分发边界完成，真实 ARM64 设备运行仍归阶段 7B
- 下一阶段：7B ARM64 真实设备产品与内存门禁

## 1. 阶段结论

固定 Rust 1.96.0 工具链现同时声明并安装 `x86_64-pc-windows-msvc` 与
`aarch64-pc-windows-msvc`。Visual Studio Build Tools 已补齐
`Microsoft.VisualStudio.Component.VC.Tools.ARM64`，实际使用 x64 host 的 MSVC 14.44.35207 ARM64
linker 和 Windows SDK 10.0.26100.0 ARM64 UCRT/UM 库。

`deskbox_native.dll` 与 `deskbox_search_core.dll` 均已从当前源码交叉构建为 ARM64 PE32+：前者保持
ABI 2、能力掩码 511 和 10 个导出，后者保持 ABI 3 和 14 个导出。Direct ARM64 Native AOT 的
`DeskBox.exe`、`DeskBox.Updater.exe` 及两个 Rust DLL 也全部为 PE machine `0xAA64`。两个 DLL 的
isolated staging 与 publish SHA-256 分别完全一致，PDB 已与发布目录分离。

本阶段没有在 x64 主机加载或执行 ARM64 DLL，也没有把 ARM64 EXE 的成功生成解释成真机可运行。
结构化摘要固定记录 `evidenceLevel=cross-compiled-static-only`、`targetDeviceExecuted=false` 和
`runtimeAbiProbeExecuted=false`。因此 7A 只关闭工具链、架构和静态分发风险，运行期 ABI、系统 API、
UI、搜索一致性及内存仍必须在 7B 的真实 ARM64 Windows 设备验证。

## 2. 工具链与可复现输入

| 项目 | 固定值 / 实际值 |
| --- | --- |
| Rust | `rustc 1.96.0 (ac68faa20 2026-05-25)` |
| Rust host | `1.96.0-x86_64-pc-windows-msvc` |
| Rust ARM64 target | `aarch64-pc-windows-msvc` |
| ARM64 rust-std 压缩包 SHA-256 | `53C11671DBD91E634B92E304EB8163B2A38658C42E0152122709E2129298D754` |
| MSVC ARM64 linker | `VC/Tools/MSVC/14.44.35207/bin/Hostx64/arm64/link.exe` |
| Windows SDK | `10.0.26100.0` ARM64 UCRT/UM，x64-hosted SDK tools |
| .NET SDK | `10.0.303` |
| NativeAOT package | `Microsoft.DotNet.ILCompiler 10.0.11` |

rust-std 压缩包哈希已与 Rust 官方 2026-05-28 分发校验值比对。`rust-toolchain.toml` 同时固定编译器、
rustfmt、Clippy 和两个 Windows MSVC target；没有使用浮动 stable 或未固定版本替代。

本阶段开始前的本地备份为：

- `D:\project\wingezi-backups\DeskBox-stage6d-before-7a-20260823T113303Z.zip`
- SHA-256：`F6E78390CA991EA7F95B5CDD4821066115B024F2F6643B3888F0251D8FF88DB8`

## 3. 实现边界

### 3.1 双架构 Rust 构建

`build-rust-native.ps1` 与 `build-rust-search-core.ps1` 现在只接受 `x64` 或 `ARM64`，并分别映射到固定
target triple。构建前会验证目标标准库，ARM64 构建还会验证 VS ARM64 component、最高已安装 MSVC
ARM64 linker、同版本 VC library、SDK ARM64 UCRT/UM、SDK include 和 x64-hosted tools。

`rust-arm64-msvc-environment.ps1` 的共享实现为 x64/ARM64 Cargo 和 NativeAOT 同时设置进程级 linker、`PATH`、`LIB`、
`INCLUDE`、VC/SDK 版本及 target architecture，并在完成后恢复调用方环境。NativeAOT 使用
`IlcUseEnvironmentalTools=true`，主程序和 Updater 复用同一次已校验环境，避免子发布再次依赖易变的
Visual Studio 注册状态。

收尾 x64 审计实际发现同机并存的 MSVC 14.42 linker 与 14.44 x64 `LIBCMT.lib` 被自动探测交叉选中，
导致 `LNK1104`。共享环境现在只接受 linker、`libcmt.lib` 与 include 同版本且完整的候选，再从中选择
最高版本；x64 与 ARM64 都实际选中 14.44.35207，进入/退出环境的恢复探针及最终两架构 AOT 审计均通过。

### 3.2 PE 与导出门禁

`native-pe-contract.ps1` 不加载目标 DLL，而是直接解析 DOS/PE signature、PE32+ optional header、COFF
machine、section/RVA 和 export name table。它拒绝：

- x64/ARM64 machine 不匹配；
- 非 PE32+、损坏或越界 RVA；
- RVA 落入 section 的未初始化范围；
- 缺失或大小写不精确的 ABI 导出；
- 异常大的导出表。

x64 构建继续执行真实 `LoadLibraryExW`/`GetProcAddress`/ABI probe，并叠加静态 PE 校验；ARM64 交叉构建
只执行静态 PE、导出和当前源码冻结常量校验，明确不在 x64 主机执行目标代码。

### 3.3 MSBuild 与产品默认策略

Native AOT 只接受以下完整组合：

| Platform | RuntimeIdentifier | 结果 |
| --- | --- | --- |
| `x64` | `win-x64` | 允许 |
| `ARM64` | `win-arm64` | 允许 |
| `x64` | `win-arm64` | 提前拒绝 |
| `ARM64` | `win-x64` | 提前拒绝 |

Direct ARM64 构建现在会构建并打包 SearchCore DLL，但 `DESKBOX_SEARCH_CORE_DEFAULT` 仍只对 Direct x64
定义。也就是说 ARM64 模块已经可供 7B 显式启用验证，新安装的产品默认仍使用 managed owner；Store
x64/ARM64 继续不构建两个 Rust 模块，直到 7C 单独审计 MSIX 内容和 fallback。

Direct 安装脚本已排除 `deskbox_search_core.pdb`，防止符号随产品安装。当前 ARM64 普通 JIT 安装脚本
仍排除 AOT-only 的 `deskbox_native.dll`，但会通过通配规则包含 SearchCore DLL；最终 AOT 安装输入和模块
策略属于 7C，不能用本阶段的裸 publish 目录代替安装包验收。

## 4. ARM64 产物证据

结构化摘要：

- `.artifacts/aot-arm64-static-audit/win-arm64/summary.json`
- profile：`1`
- schema：`1`
- 源指纹：审计前后相同；dirty working tree 被记录但没有在审计期间变化
- publish：40 个文件，`95228725` bytes（90.82 MiB）
- symbols：4 个 PDB，`214147072` bytes（204.23 MiB）
- 允许且实际出现的警告代码：`CS0108`、`CS0169`、`CS0414`、`CS8601`、`CS8602`、`WMC1510`
- 非允许警告代码：0

| 文件 | PE | 大小 | SHA-256 |
| --- | --- | ---: | --- |
| `DeskBox.exe` | `0xAA64` | 48,854,528 | `8568F7F493917208B032D53B0ED716327E38CB7CACBA5BDFEE42FD1B632DE781` |
| `DeskBox.Updater.exe` | `0xAA64` | 2,057,728 | `2B8E0BC6C70C421A0F512E465BC5AA5F2F8766E783BAC2955C233462F56E4D2F` |
| `deskbox_native.dll` | `0xAA64` | 150,016 | `2C465236204F4B5A7F43B7AE02E7F7FA614B9ED4D4E9994366B0811DE40D0352` |
| `deskbox_search_core.dll` | `0xAA64` | 160,768 | `2B125636E2D75717FB7CD85E9CE3361EF209A7127C7741A7230F5E2DA3699029` |

`deskbox_native.dll` 的 staging/publish 哈希均为 `2C46...0352`；SearchCore 的 staging/publish 哈希均为
`2B12...9029`。publish 根中每个模块恰好一份，四个 PDB 均移到独立 symbols 根，publish 中没有 PDB、
`DeskBox.dll`、deps/runtimeconfig、CoreCLR、JIT、hostfxr 或 hostpolicy 文件。

## 5. PE 依赖清单与分发风险

四个 PE 共出现 25 个直接导入。主程序与 Updater 只导入 Windows 系统库和 UCRT API set；两个 Rust
DLL 还直接导入以下关键项：

- `deskbox_native.dll`：`combase.dll`、`ole32.dll`、`oleaut32.dll`、`propsys.dll`、
  `vcruntime140.dll` 等；
- `deskbox_search_core.dll`：`bcryptprimitives.dll`、`icuuc.dll`、`vcruntime140.dll` 等。

`VCRUNTIME140.dll` 在当前开发机存在不等于所有受支持 Windows 设备都具备。7C 在生成可发布安装包前必须
二选一并在干净环境验证：

1. 固定 Rust 静态 CRT，重新核对 x64/ARM64 DLL 大小、导入、内存和许可/更新边界；
2. 保持动态 CRT，由 x64/ARM64 安装器检测并安装匹配的 Microsoft Visual C++ Redistributable。

本阶段只完成依赖库存，不提前选择会影响两种架构和安装体积的方案。Windows App Runtime 与 .NET
runtime-download 安装流程也不等同于 Visual C++ Runtime 的已验证保障。

## 6. 回归结果

当前已完成：

- Stage 7A / AOT / installer 定向契约：43/43；
- x64 全量 .NET 测试：2520/2520；
- Rust workspace：74/74；
- `cargo fmt --check`：通过；
- `cargo clippy --workspace --all-targets --locked -- -D warnings`：通过；
- `git diff --check`：通过；
- x64 两个 DLL 的实际运行时 ABI probe 与静态 PE 校验：通过；
- ARM64 两个 DLL 独立 Release 交叉构建：通过；
- Direct ARM64 Native AOT 主程序、Updater、双 DLL 静态分发审计：通过。

最终 x64 AOT 审计和 ARM64 静态审计均在源码指纹稳定的独立运行中通过；完整测试、Rust 质量门禁、
`git diff --check` 与规范 Debug 重启以本阶段交付说明中的最终复核为准。

## 7. 未完成边界

7A 没有覆盖：

- ARM64 真机加载两个 DLL、ABI/capability、应用启动和正常退出；
- SearchCore 的查询、筛选、八次双向排序、watcher mutation/reconciliation、DBIX 重启与故障回退；
- 11 个完整 Widget 全显示时的 ARM64 managed/Rust Private Bytes、Working Set 和响应延迟；
- ARM64 是否默认启用 Rust SearchCore；
- Direct/Store x64/ARM64 安装包、签名、WACK、升级、设置保留和 CRT 分发；
- 不同目标设备的 Todo 通知投递差异、真人 Explorer 拖放和物理热键等既有外部证据。

这些项目没有被交叉编译成功所替代。

## 8. 下一阶段建议

下一阶段开放 **7B ARM64 真实设备产品门禁**，复杂度高，依赖 ARM64 Windows 设备。建议一个较大批次内
完成：

1. 在隔离数据根启动本阶段 ARM64 AOT 产物，验证唯一进程、正常退出、双 DLL 实际加载、ABI 2/3 与
   `deskbox_native` 能力 511；
2. 在 SearchCore 默认关闭的前提下先跑 managed 基线，再显式启用 Rust，完成真实文件/应用结果、筛选、
   名称/大小/日期/类型各双向排序；
3. 覆盖 watcher create/rename/delete、树移动、overflow/reconciliation、idle unload/reload、DBIX 重启、
   DLL/DBIX 故障回退和生产数据不变；
4. 在全部格子显示且视觉不变的同一设备上重复 managed/Rust 内存矩阵，再决定 ARM64 默认值；
5. 形成可交给 7C 的 ARM64 runtime 与依赖结论，不在 7B 制作或发布正式安装包。

按原始“选择性 Rust + 主程序 Native AOT + 可发布分发验证”目标加权估算，7A 收口后约完成 **94%**，
剩余约 **6%**。剩余代码量不大，但 ARM64 真机与 Store/安装升级是发布级外部门禁，不能按文件数量低估。

## 9. 参考资料

- [Rust Windows MSVC targets](https://doc.rust-lang.org/rustc/platform-support/windows-msvc.html)
- [.NET Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Visual Studio component IDs](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-build-tools)
- [Rust MSVC CRT linkage](https://doc.rust-lang.org/stable/reference/linkage.html)
