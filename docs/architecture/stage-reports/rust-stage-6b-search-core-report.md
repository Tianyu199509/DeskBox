# Rust 阶段 6B SearchCore DBIX 直载与隔离基准报告

- 日期：2026-08-23
- 结论：6B 自动化范围完成，300k 数据下 Rust 索引驻留私有内存增量下降 79.5%
- 产品状态：默认搜索后端保持 C#，没有双索引常驻
- 外部待验：Todo 真人通知点击仍是发布前人工门，不阻塞搜索主线

## 1. 本轮完成内容

SearchCore 独立 ABI 从 1 升到 2，保留全部 v1 操作并新增 DBIX 直载。Rust 按现有 DBIX v1 格式直接
把目录和文件名写入紧凑 arenas，不恢复托管完整路径字典。实现同时覆盖：

- 128 MiB 文件、300k entry、字符串和 arena 上限；
- magic/version、UTF-8、目录 ID、截断、尾随数据和空索引验证；
- Local/UTC `DateTime.ToBinary()` 排序语义；
- waitable event 协作取消；
- 失败不返回半成品句柄；
- C# 显式绝对路径所有者和 rebuild/fallback 错误分类；
- 已打开句柄不受随后源文件损坏或替换影响；
- 独立 Release C#/Rust 基准进程和结果签名门禁。

没有编辑 `SearchIndexService` 的产品所有权，也没有将 DLL 加入 `DeskBox.csproj` 或 AOT publish。

## 2. 基准方法

基准工具为独立 `net10.0-windows` x64 Release 控制台。每种规模生成同一份确定性 DBIX，再分别启动
全新的 managed/Rust 子进程：

- baseline：加载前强制完整 GC 后的 Private Bytes / Working Set；
- resident：加载后完整 GC 的进程值；
- peak：后台每 5 ms 采样加载和查询期间的进程值；
- query：`report`、`project`、`文档`、`σigma`、`2026`、`a` 六组，每组预热后共 30 个样本；
- result gate：完整路径、类型、UTC ticks、分数和顺序生成 SHA-256，C#/Rust 必须逐组相同；
- cancellation：查询任务进入后立即取消，记录取消是否被观察和返回延迟。

表中的 private/working 值是各自子进程相对 baseline 的增量，不是整个 DeskBox 工作集。

## 3. 实测结果

| Entries | C# resident private | Rust resident private | 降低 | C# peak private | Rust peak private | 降低 |
|---:|---:|---:|---:|---:|---:|---:|
| 10,000 | 11.50 MiB | 0.61 MiB | 94.7% | 12.81 MiB | 5.52 MiB | 56.9% |
| 100,000 | 35.45 MiB | 5.63 MiB | 84.1% | 37.50 MiB | 10.88 MiB | 71.0% |
| 300,000 | 85.79 MiB | 17.55 MiB | **79.5%** | 86.27 MiB | 22.21 MiB | **74.3%** |

300k resident Working Set 增量为 C# 88.12 MiB、Rust 19.19 MiB；peak Working Set 增量为 C#
92.59 MiB、Rust 26.14 MiB。Rust 自报 tracked capacity 为 17.36 MiB，封存后的 build lookup 为 0，
与进程驻留增量量级一致；C# 子进程的 managed heap 为 64.67 MiB。

| Entries | C# load | Rust load | C# query P50 | Rust query P50 | C# query P95 | Rust query P95 |
|---:|---:|---:|---:|---:|---:|---:|
| 10,000 | 22.00 ms | 3.76 ms | 3.282 ms | 0.599 ms | 4.220 ms | 0.736 ms |
| 100,000 | 83.37 ms | 12.91 ms | 6.562 ms | 5.413 ms | 33.514 ms | 7.149 ms |
| 300,000 | 226.51 ms | 31.17 ms | 17.468 ms | 16.138 ms | 34.436 ms | 18.052 ms |

300k 加载约为 C# 的 13.8%，聚合查询 P95 下降约 47.6%。所有三档、六组查询的结果签名完全一致。

取消均被观察。300k 托管取消返回为 2.487 ms，Rust 为 4.067 ms；Rust 较慢，但仍远低于后续建议的
20 ms 产品门槛。这里测的是取消发出到调用返回，不代表 UI 输入到取消发出的调度延迟。

## 4. 结论边界

这些数据已经足以确认 SearchCore 是明显适合 Rust 的内存热点，继续使用完整原生数据边界比在 C#
字典上做零碎微优化更合理。但它还不能直接宣称 DeskBox 总工作集下降 68 MiB，原因包括：

- 当前是确定性合成 DBIX，不是用户实际目录分布；
- 每档内存是一次隔离进程测量，查询延迟有 30 个样本但内存不是多轮置信区间；
- 私有内存会受 CLR、CRT heap 预留和系统页驻留影响；
- 真正产品切换还需要 watcher 增量、recent/frequent 和故障恢复，因此现在启用会导致能力缺失。

正确结论是：结构收益已经被进程级证据确认，下一步应完成产品能力，而不是继续证明 Rust 是否值得。

## 5. 已执行验证

- Rust DBIX/搜索单元：12/12；
- Rust workspace 全量：69/69；
- SearchCore C# 真实 DLL 与契约：14/14；
- x64 C# 全量：2488/2488；
- Release benchmark project：0 warning、0 error；
- 10k/100k/300k 六组查询签名：全部一致；
- 三档 DBIX direct load：build lookup 均为 0；
- `cargo fmt --all -- --check` 与 `cargo clippy --workspace --all-targets --locked -- -D warnings`：通过；
- x64 Native AOT：profile 56 / schema 53，源码稳定，39 个发布文件、88.9 MiB，`WMC1510=1211`、
  `always-throw=0`；
- 生产 Rust：ABI 2、能力 511、十个必需导出不变，AOT publish 中不存在
  `deskbox_search_core.dll`。

canonical Debug 构建完成（24 warning、0 error），并从规范路径启动 PID 37780；仓库内仅此一个
`DeskBox.exe`，路径核对一致，Debug 输出中不存在 `deskbox_search_core.dll`。`git diff --check` 通过。

## 6. 下一阶段建议

下一阶段建议定为 **6C：增量所有权 + recent/frequent + 显式产品预览**，作为一个较大批次完成：

1. ABI v3 增加事务化 upsert/remove batch，失败不提交半批状态；
2. 增加 recent files 与 frequent folders 有界原生投影；
3. watcher、bounded pending changes、overflow reconciliation 和 DBIX 保存继续由 C# 调度；
4. `SearchIndexService` 在一次会话内只能选择 managed 或 Rust 一个 resident owner，禁止双索引常驻；
5. 增加显式设置型 preview opt-in，DLL/DBIX/版本/时间语义失败时回到可用的 C# rebuild，不允许返回
   “成功但结果为空”；
6. 用真实应用进程测量打开全部格子前后的 Private Bytes、Working Set、搜索 P50/P95 和卸载恢复。

复杂度为高。6C 通过后才适合讨论默认启用；ARM64 与 Store 仍属于后续阶段 7。
