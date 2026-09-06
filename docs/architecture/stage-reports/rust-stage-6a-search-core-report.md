# Rust 阶段 6A SearchCore 实施与审计报告

- 日期：2026-08-23
- 结论：阶段 6A 自动化范围完成，产品默认路径保持不变
- 外部待验：Todo 真实 Windows 通知点击仍保留为发布前人工门，不阻塞搜索代码推进

## 1. 本轮完成内容

本轮没有扩展已冻结的 `deskbox_native.dll`，新增独立 workspace crate
`native/deskbox-search-core` 和 `deskbox_search_core.dll`。实现范围一次覆盖：

- ABI v1 create/add-batch/seal/query/copy/stats/destroy 生命周期；
- caller-owned packed UTF-16 批量导入与结果复制；
- 目录去重、连续 UTF-16 arenas 和固定宽度 entry；
- 与当前 C# 一致的相关度分支和有界 Top-N；
- `CancellationToken` 到 Rust `AtomicBool` 的协作取消；
- 构建期/封存后 tracked capacity；
- 显式绝对路径 C# 所有者；
- Debug 构建、ABI/导出校验和测试输出复制；
- Rust 单元、C# 真实后端差异、内存与静态隔离门禁。

没有编辑当前改动较多的 `SearchIndexService.cs`，也没有加入环境变量、默认加载或 AOT 打包。因此
搜索失败时不会因为这个实验模块改变现有产品行为，已完成的 AOT profile 56 / schema 53、生产 Rust
ABI 2 / 能力 511 / 十导出也无需重做。

## 2. 审计中发现并修正的问题

首轮用 Windows `CompareStringOrdinal` 复现 `OrdinalIgnoreCase`。常见英文、中文与 Ä 样本通过，扩展
矩阵在 `ς/σ` 上发现 C# 返回两条、Rust 只返回一条。该差异来自两套 ordinal casing 语义，不能用
“Windows API”名义忽略。

.NET 当前实现会按全球化后端进入自身的 ordinal casing 路径；其 ICU 路径并不等同于直接调用
`CompareStringOrdinal`。实现依据见 [.NET Ordinal 源码](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/System/Globalization/Ordinal.cs)、
[OrdinalCasing ICU 源码](https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Globalization/OrdinalCasing.Icu.cs.html)
以及记录两者差异的 [.NET runtime issue #30960](https://github.com/dotnet/runtime/issues/30960)。

最终改为 Windows ICU `u_toupper` 的单 scalar 映射，并显式阻止非 ASCII 映射为 ASCII；surrogate
pair 按 scalar 比较。C# 加载前再用 final sigma、Deseret、Kelvin、dotless-i、sharp-s 五组 canary
确认当前 .NET 全球化模式。如果语义不兼容，加载明确失败。修正后扩展差异矩阵全部通过。

这个问题也调整了后续策略：阶段 6 不能只比较文件数、速度和常见关键词，必须保留 Unicode、扩展名
分支、排序与取消的行为门禁后才能切换默认后端。

## 3. 内存证据

20,000 条共享同一长目录、目录大小写交替的样本中：

| 指标 | 结果 |
|---|---:|
| 原生 entry 数 | 20,000 |
| 去重目录数 | 1 |
| 封存后构建 lookup | 0 bytes |
| Rust tracked capacity | 1,480,124 bytes |
| 托管完整路径 UTF-16 字符载荷下限 | 3,320,000 bytes |
| Rust / 托管字符载荷下限 | 44.58% |

托管下限没有计算字典、entry、字符串头和 GC 对齐，所以数据足以支持继续推进 Rust SearchCore；它
还不能直接宣称 DeskBox 进程工作集下降 55.42%。下一阶段必须用隔离进程和 Release DLL测量
Private Bytes、Working Set、构建峰值、空闲后驻留以及查询 P50/P95。

## 4. 已执行验证

- `cargo fmt --check`：通过；
- `cargo clippy --all-targets --locked -- -D warnings`：通过；
- Rust SearchCore 单元：6/6；
- Rust workspace 全量：63/63；
- SearchCore C# 定向：8/8；
- x64 C# 全量：2482/2482；
- Debug 与 Release DLL ABI：1；
- 必需导出：10/10；
- C# 当前索引与 Rust 真实 DLL 差异矩阵：名称、目录、文件夹标志、UTC 时间、相关度、顺序均一致；
- Unicode 边界：Greek sigma、Deseret、Kelvin、dotless-i、sharp-s 及中英文混合通过；
- 产品隔离：`SearchIndexService` 未接入、DeskBox.csproj 未打包 SearchCore、AOT publish 中不存在
  `deskbox_search_core.dll`；
- x64 Native AOT：profile 56 / schema 53，39 个发布文件、88.9 MiB，3 个符号文件、203.7 MiB，
  `WMC1510=1211`、`always-throw=0`、受审计源码稳定；
- 生产 Rust：ABI 2、能力 511、原十个必需导出及发布哈希门禁保持不变。

- canonical Debug：0 error、24 个既有 warning，已从规范 Debug 输出重启并确认仓库内恰好一个实例；
- 最终 `git diff --check`：通过。

## 5. 下一阶段建议

下一阶段建议定为 **6B：隔离 300k 基准 + DBIX 直载原型**，一次完成以下内容：

1. 在独立进程分别建立当前 C# 和 Rust 10k/100k/300k 索引，记录构建峰值、封存驻留、Private
   Bytes、Working Set、查询 P50/P95、宽查询 Top-N 和取消延迟；
2. 让 Rust 直接读取或流式接收现有 DBIX，不先恢复完整托管字典，避免“为了验证省内存而双份常驻”；
3. 增加坏文件、版本不兼容、取消和原子替换失败的显式 fallback/rebuild；
4. 只有当 300k 结果与顺序一致、内存收益明确且故障可恢复时，才增加显式产品 opt-in；默认切换继续
   留到增量 watcher/update 和最近文件/常用目录能力完成之后。

复杂度为高。主要难点不是 FFI，而是避免双索引峰值、保持 watcher 增量一致性，并把持久化损坏回退
做成不会导致“搜索结果为空”的可恢复路径。Rust 在这里有明显内存价值，适合继续完整原生边界；
WinUI 展示、搜索弹窗生命周期和排序筛选仍留在 C#。
