# DeskBox Rust SearchCore 阶段 6D 收口报告

- 日期：2026-08-23
- 范围：Direct x64 SearchCore 长会话、运行期恢复、AOT 真实搜索界面、整机内存复测与默认启用决策
- 结论：阶段 6D 通过；Direct x64 模块构建默认启用 Rust；Store/ARM64 继续关闭
- 下一阶段：阶段 7 ARM64 与 Store 分发验证

## 1. 本阶段结论

SearchCore 已从“可手动打开的预览”进入 Direct x64 的条件默认后端。该决定不只依赖一次内存样本，
而是同时满足了结果一致性、长会话 mutation、watcher 恢复、原生运行期故障回退、真实 Native AOT
搜索界面和三轮完整格子进程内存门禁。

迁移边界没有扩大到 WinUI 或整个搜索业务。C# 继续负责 watcher、扫描、reconciliation、搜索组合、
应用结果、DeskBox 内容、筛选、排序和视觉层；Rust 只负责高常驻成本的文件索引数据、查询、投影、
增量 mutation 与 DBIX 保存。这是当前复杂度和内存收益最合适的分界。

## 2. 工作区保护

开始 6D 前已创建本地快照：

- 文件：`D:\project\wingezi-backups\DeskBox-stage6c-before-6d-20260823T101741Z.zip`
- 文件数：1,101
- 大小：23,288,969 bytes
- SHA-256：`7FB79E84600F38F94A32FBCD6322B1435D1BDD98A804A7B35DCCABA774E26157`

快照只用于本地恢复，没有提交、推送或合并工作区中的其他变更。

## 3. 运行期故障恢复

`SearchIndexService` 现在覆盖 resident Rust owner 的全部可恢复操作：

| 操作 | 失败后的产品行为 |
| --- | --- |
| query | 销毁原生 owner，恢复 managed DBIX，重新执行查询 |
| recent/frequent projection | 恢复 managed snapshot，不把故障呈现为空推荐 |
| save | 保留最后有效 DBIX，恢复 managed owner |
| idle unload | 离开 residency gate 后恢复，避免锁内死锁 |
| upsert | 恢复 managed 后重试同一 mutation |
| exact/tree remove | 恢复 managed 后重试删除 |
| scan reconciliation | 保留 snapshot，安排非破坏性 watcher recovery |

一次可恢复故障会设置会话级 quarantine。本会话后续 load 不再反复激活同一个有问题的 Rust 模块；
只有用户显式重新配置索引后端时才清除 quarantine 并重试。当前 recovery count 和 fallback reason
同时进入诊断及 AOT 结构化证据。

故障注入使用已处置的原生句柄，覆盖 query、projection、save、idle unload、upsert、精确删除、树删除
和 reconciliation。所有分支都验证了“只有一个 resident owner”以及非空、可继续使用的 managed
结果，而非仅断言捕获异常。

## 4. 长会话、watcher 与压缩

自动 soak 包含：

- 64 个 live 文件和 65 轮更新，产生超过 4,096 个 tombstone；
- 精确删除、重命名、目录树删除、live-only 保存压缩和 owner 替换；
- idle unload 后重新加载，并比较完整 exact live set；
- 真实 `FileSystemWatcher` create、rename 和 tree delete；
- 模拟 `InternalBufferOverflowException`，随后执行 root recovery；
- 在恢复前故意让索引缺失一个仍存在的文件，证明 reconciliation 实际补回内容。

watcher/overflow/compaction 组合测试除首轮外又连续重复三轮，均为 2/2；完整 SearchIndexService 定向
矩阵为 31/31。

## 5. 真实 Native AOT 搜索界面

新增 `SearchCorePreviewReadOnly` 场景。runner 在隔离 data root 中创建真实文件和 DBIX v1，但不再写入
`searchRustIndexerPreviewEnabled=true`；因此它验证的是受审计 Direct x64 AOT 二进制的编译默认策略。

场景已经证明：

- `IsDynamicCodeSupported=false`；
- Rust SearchCore 实际活跃、fallback 为空、单 resident owner、runtime recovery count 为 0；
- 搜索结果包含 owned DBIX 文件 `Open Settings stage6d-rust-aot.txt`；
- “打开设置”应用动作仍存在；
- All、FilesAndFolders、Apps、Images、Documents、DeskBox 共 6 次筛选转换；
- Name、Size、Date、Type 各点击两次，共 8 次升降序转换；
- 最后回到 All / Relevance / ascending 基线；
- 生产数据目录指纹不变，运行日志没有未处理异常，AOT 进程已停止。

结果文件：

- `.artifacts/aot-managed-ui-smoke/win-x64/preview-root/aot-managed-ui-smoke/search-core-preview-read-only/result.json`
- `.artifacts/aot-managed-ui-smoke/win-x64/session.json`

## 6. 完整格子内存复测

最终有效运行 ID 为 `20260823T110602Z-c1e9b134`。测试使用 208,021 条 DBIX entry、16,994 个目录、
11 个启用 Widget；managed/Rust 各运行三次，每次稳定 8 秒并采集 10 个样本。测量期间源设置和 DBIX
指纹均未改变。

| 指标 | Managed 三次 | Rust 三次 | 真正中位数 | 降幅 |
| --- | --- | --- | --- | --- |
| Private Bytes | 270.89 / 269.47 / 269.73 MiB | 242.59 / 235.08 / 234.22 MiB | 269.73 → 235.08 MiB | **12.85%** |
| Working Set | 388.30 / 386.73 / 388.00 MiB | 361.68 / 348.16 / 351.67 MiB | 388.00 → 351.67 MiB | **9.36%** |

测量脚本同时修正了三样本中位数计算：使用 `Floor(count / 2)`，避免 PowerShell 把 1.5 四舍五入后
错误选择最大值。证据位于：

- `.artifacts/search-core/stage-6d-product/20260823T110602Z-c1e9b134/product-memory.json`
- `.artifacts/search-core/stage-6d-product/20260823T110602Z-c1e9b134/product-memory.md`

这些是当前机器、当前完整格子和当前数据的整进程结果，不应外推成所有设备的固定比例；它们与
100k/300k 隔离索引结构数据共同支持默认决策。

## 7. 默认启用与兼容边界

默认策略由构建条件决定：

| 构建组合 | SearchCore DLL | 编译默认值 |
| --- | --- | --- |
| Direct x64 / 无显式 RID 的规范 x64 开发构建 | 包含 | Rust `true` |
| Store x64 | 不包含 | Managed `false` |
| Direct ARM64 / win-arm64 | 不包含 | Managed `false` |

MSBuild 实际求值已分别确认以上三种组合。实现使用 `DESKBOX_SEARCH_CORE_DEFAULT`，只有 Direct、
SearchCore 模块启用且不是 ARM64/win-arm64 时才定义。设置文件中已经存在的布尔值仍按用户选择反序列化，
因此升级不会把明确保存的 `false` 改回 `true`。新默认也不自动打开“自定义文件索引器”主开关；它只在
该功能启用时选择更低内存的 resident owner。

设置页文案已从“预览”更新为“Rust 索引后端”，并明确 Direct x64 默认与启动/运行期 managed fallback。

## 8. 验证状态

阶段内已完成：

- SearchIndexService + 6C/6D 契约定向测试：41/41；
- SearchIndexService 完整定向矩阵：31/31；
- watcher/overflow/compaction 重复矩阵：连续三轮 2/2；
- Stage 6D/AOT 契约：30/30；
- Native AOT SearchCore UI：真实运行通过；
- 三轮完整格子内存：有效且源指纹不变；
- Direct x64 / Store x64 / Direct ARM64 MSBuild 属性矩阵：符合预期。

最终 Rust workspace、x64 全量测试、profile 58/schema 55 AOT 重审计、最终 AOT UI 复跑和规范 Debug
重启在本报告收尾时执行；最终数字以本轮交付说明为准。

## 9. 没有遗漏但仍属外部边界的项目

以下项目没有被 SearchCore 6D 冒充为完成：

- ARM64 Rust 标准库/链接工具、PE 架构、真实 ARM64 设备功能和内存；
- Store x64/ARM64 MSIX 内容、签名/WACK、安装升级和缺失模块 fallback；
- 真实设备上的超长时间内存遥测与不同磁盘规模；
- Todo 通知中心真实点击、冷启动和不同目标设备的通知投递差异；
- 真人 Explorer 物理拖放、物理标准/Win+Space 键盘与设置/引导录制器门禁；
- 真实天气网络/定位、在线 Glance 图片等外部服务证据。

## 10. 下一阶段

下一阶段调整为阶段 7，并拆成三个可独立验收的批次：

1. **7A ARM64 构建与静态分发边界**：补齐固定 Rust 工具链的 `aarch64-pc-windows-msvc` 标准库和
   MSVC ARM64 链接组件，构建两个原生 DLL，核对 PE、导出、依赖、PDB 和 fail-fast 组合。复杂度中高。
2. **7B ARM64 真实设备产品门禁**：在 ARM64 Windows 设备运行搜索结果、mutation/watcher、idle、
   AOT UI 和完整格子内存矩阵，再决定 ARM64 是否默认 Rust。复杂度高，依赖设备。
3. **7C Store 分发与升级**：分别核对 x64/ARM64 MSIX 中的模块策略、in-place upgrade、用户设置保留、
   模块缺失/损坏 fallback、WACK 与 Store flight。复杂度高，包含外部发布环境。

不建议下一轮继续重写 WinUI 或把 watcher 状态机迁到 Rust。当前剩余风险集中在架构、打包和目标设备，
应先把阶段 7A 的工具链与静态产物门禁封闭。若本机缺少 ARM64 组件，再向用户给出精确下载文件名和路径要求。

按原始“选择性 Rust + 主程序 Native AOT + 可发布分发验证”目标加权估算，6D 收口后约完成 **92%**，
剩余约 **8%**。这个比例按风险和验收边界估算，不按代码行数；剩余部分虽然数量少，但目标设备和 Store
验证的外部依赖较强。
