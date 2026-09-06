# Rust 阶段 6C SearchCore 产品预览与内存报告

- 日期：2026-08-23
- 结论：6C 代码、隔离基准、全格子产品内存和 x64 AOT 打包审计完成
- 产品状态：Direct x64 可显式预览，默认关闭；单会话只保留一个 resident 索引 owner
- 下一阶段：6D 长会话、故障恢复与默认启用门禁
- 外部待验：Todo 通知中心真实点击/投递的目标设备差异仍是发布前外部证据

## 1. 本轮完成内容

SearchCore 独立 ABI 从 2 升到 3，并把 6B 的只读实验模块接入真实产品生命周期：

- 事务化 upsert、精确删除、树删除和按扫描代次清理；
- recent files 与 frequent folders 原生有界投影；
- live-only DBIX v1 原子保存、目录 ID 压缩和 tombstone 阈值重开；
- watcher、bounded pending changes、overflow recovery 与 reconciliation 继续由 C# 调度；
- 扫描 mutation 以 8,192 条有界批次提交，重复/容量边界会递归缩小批次；
- Rust 与 managed owner 二选一，Rust 活跃时不保留完整路径字典；
- DLL、ABI、导出、DBIX 或时间语义失败回到托管加载/重建，不返回伪成功空结果；
- 设置页增加默认关闭的 Rust 预览开关、当前后端状态和 fallback 原因；
- Direct x64 普通输出与 AOT publish 打包 DLL，Store/ARM64 明确排除。

复盘还修正了两项边界问题：旧 DBIX 中非 MinValue 的 Unspecified 时间在托管保存前规范为 Local；产品
内存脚本按实际 camelCase JSON 名 `searchRustIndexerPreviewEnabled` 写入隔离设置，并对后端日志、源数据
指纹和可执行路径做精确断言，避免“脚本声称 Rust、应用实际仍走 managed”的假数据。

## 2. 单 owner 与 fallback

产品启动或 idle reload 时先读取当前显式设置。Rust 请求成功后，原生句柄成为唯一 resident owner，
托管 `_index` 和目录池保持为空；请求失败则释放原生候选并完整读取托管 DBIX。新建/重建索引同样只
创建当前设置对应的 owner。

查询、recent/frequent、watcher upsert/delete、目录树删除、扫描 stale reconciliation、保存和 idle
unload/reload 都已路由到当前 owner。Rust 写盘后按 tombstone 阈值重开紧凑句柄。当前预览不会在
一次 query/project/save 的运行期异常后热切换到 managed；这项需要用故障注入证明状态恢复，留给 6D，
因此本轮没有把开关默认打开。

## 3. ABI v3 隔离进程结果

6C 继续使用与 6B 相同的独立 x64 Release 子进程、同一 DBIX、六组查询签名和取消门禁。v3 为 mutation
增加 revision/generation/tombstone 元数据，结构比只读 v2 稍大，但 100k/300k 仍保持明确收益。

| Entries | Managed resident private | Rust resident private | 降低 | Managed peak private | Rust peak private | 降低 |
|---:|---:|---:|---:|---:|---:|---:|
| 10,000 | 11.51 MiB | 0.07 MiB | 99.4% | 12.93 MiB | 4.75 MiB | 63.2% |
| 100,000 | 34.80 MiB | 6.68 MiB | 80.8% | 37.56 MiB | 11.63 MiB | 69.0% |
| 300,000 | 85.38 MiB | 22.07 MiB | 74.2% | 86.55 MiB | 26.45 MiB | 69.4% |

10k 的 resident 差值只有约 70 KiB，甚至低于 Rust 自报的 0.74 MiB tracked capacity，说明基线页、
allocator reserve 和 GC 对小样本的噪声足以淹没结构值；该档不用于默认启用判断。100k/300k 量级与
原生 tracked capacity 一致，才是结构结论的主要证据。

| Entries | Managed load | Rust load | Managed query P95 | Rust query P95 |
|---:|---:|---:|---:|---:|
| 10,000 | 22.22 ms | 3.89 ms | 4.567 ms | 0.763 ms |
| 100,000 | 72.77 ms | 13.13 ms | 37.584 ms | 9.531 ms |
| 300,000 | 223.43 ms | 32.91 ms | 31.349 ms | 20.635 ms |

三档六组结果签名全部一致，取消均被观察，DBIX 直载后的 build lookup 均为 0。300k Rust query P50
17.328 ms 与 managed 17.160 ms 接近，但 P95 和加载明显更低；当前结论是减少 resident 结构和尾延迟，
并非所有单次查询都按同一比例加速。

## 4. 真实 DeskBox 全格子内存

产品测量使用规范 Debug 可执行文件和正式数据的只读隔离副本。两个后端各运行两次，每个进程等待
8 秒稳定并取 10 个样本。副本包含 11 个已启用 Widget、207,925 个索引 entry、16,992 个目录，DBIX
为 8,890,425 bytes。脚本逐次确认 managed 日志为 `compact cache`、Rust 日志为
`Rust SearchCore preview backend`，并在每次结束后精确终止隔离进程。

| 指标 | Managed 中位数 | Rust 中位数 | 整体进程降低 |
|---|---:|---:|---:|
| Private Bytes | 269.23 MiB | 236.86 MiB | **12.02%** |
| Working Set | 387.36 MiB | 355.76 MiB | **8.16%** |

这是当前最接近用户“全部格子都显示且视觉不变”场景的证据：UI、图片、WinUI compositor、CLR 与其他
Widget 均相同，只切换索引 owner，视觉效果和 Widget 数量不变。32.37 MiB Private Bytes 与
31.60 MiB Working Set 的下降是整个 DeskBox 进程结果，不能与隔离索引的 74.2% 直接相加或互换。

该轮是同机两次重复测量，不是跨设备统计；它证明当前机器和当前完整数据上的收益，不代表所有目录
分布、显卡驱动或 Windows 版本的固定百分比。正式 settings 与 DBIX 指纹在测量前后不变。

## 5. x64 Native AOT 与发布边界

审计升级为 profile 57 / schema 54。受审计 Direct x64 AOT publish 包含唯一根目录
`deskbox_search_core.dll`，ABI 3、14 个导出、x64 PE、staging/publish 哈希一致，PDB 只在符号目录。
发布产物为 40 个文件、89.18 MiB；符号为 4 个文件、205.1 MiB；`WMC1510=1211`，完整
`always-throw=0`，审计期间源码稳定。

SearchCore 被打包不等于默认使用：AOT 产品仍读取同一个默认关闭设置，并保留 managed fallback。
Store 与 ARM64 项目条件明确排除 SearchCore，不能用 x64 静态审计替代阶段 7 的真实架构和包验证。

## 6. 已执行验证

- Rust SearchCore mutation/projection/save 单元：17/17；
- Rust workspace：74/74；
- `cargo fmt --all -- --check`：通过；
- `cargo clippy --workspace --all-targets --locked -- -D warnings`：通过；
- SearchCore/AOT publish/4D1B/6C/文档组合定向测试：46/46；
- x64 全量：2500/2500；
- x64 Native AOT publish audit：profile 57 / schema 54，通过；
- 10k/100k/300k 隔离结果签名、取消和 build lookup 门禁：全部通过；
- 全格子两轮 managed/Rust 产品内存：后端、路径、正式数据指纹和精确清理门禁全部通过。

## 7. 未完成边界与下一阶段

下一阶段定为 **6D：SearchCore 预览 soak、故障恢复与默认决策门禁**，复杂度中高，建议继续作为一个
较大的批次：

1. watcher create/change/rename/delete、目录树移动、overflow 和 reconciliation 长时间 churn；
2. query/project/save、DLL/DBIX 损坏和重开失败的故障注入，确保无空结果并能恢复可用 owner；
3. tombstone 增长、压缩、idle unload/reload、重启恢复和内存回落；
4. 多轮真实全格子 Release/AOT Private Bytes、Working Set 与搜索 P50/P95；
5. 人工核对真实搜索的文件/应用结果、筛选，以及名称/大小/日期/类型双向排序；
6. 全部门禁通过后再决定 Direct x64 是否默认启用，未通过则继续保持显式预览。

6D 不扩大到 WinUI 重写。完成默认决策后再进入阶段 7 ARM64 与 Store，复杂度高，需要 ARM64 Rust
工具链、真实设备、PE/依赖审计、MSIX 打包和升级验证。

Todo 通知中心真实点击、冷启动和不同目标设备投递仍保留为独立外部验收项；受控 activation 与本轮
搜索证据都不能替代该人工/目标设备证据。
