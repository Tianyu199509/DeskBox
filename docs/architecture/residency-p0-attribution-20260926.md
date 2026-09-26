# 驻留线 P0 全应用内存归因（2026-09-26）

按 `widget-group-residency-roadmap-20260918.md` §0-0 的裁决规则执行：**若组成员树占稳态私有内存 <10%，只做 P1+P2，跳过 Cold 档**。

## 测量协议

- 构建：framework **Release**（x64，managed-jit 运行时）。注意非 AOT：AOT 零售基线绝对值更小（~115MB 级），缓存树增量同量级，10% 裁决方向不受影响（下注）。
- 工具：现有 `PerformanceLogger` 采样（`DESKBOX_PERF_LOG=1`，30s 节拍），字段直接取 `privateMB` / `cachedGroupContents` / `windows` / `loadedWidgets` / `materializedContentByKind`。
- 场景（隔离数据根，9 个真实文件格子，每个 1 文件）：
  - **A 基线**：3 组 × 3 成员，冷启动后不切换 → `cachedGroupContents=0`。
  - **B 切换扫**：同进程逐一切换全部 9 成员（物化后进缓存）→ 采样。
  - 同进程差分消除窗口数/基线漂移，是组缓存树最干净的归因。
- GC 纪律：采样取多拍稳态最小值；GC 推迟只会让树显得更大，故"延迟态仍 <10%"为保守安全裁决（roadmap §0-5 精神）。

## 数据

| 场景 | privateMB（稳态） | cachedGroupContents | windows | loadedWidgets | materialized File |
|---|---|---|---|---|---|
| A：3 组冷启动不切换 | 160.1 / 161.0 / 162.9 / 163.1 | **0** | 4 | 4 | 4 |
| B：切换扫过后 | 165.6 / 166.2 / 166.4 | **3** | 4 | 4 | 7 |

背景清理/静默工作集裁剪正常运转（可见后台态 WS 296→6.7MB；`performanceMode=ResourceSaver cacheBudget=Small`）。

## 裁决

- **差分 ≈ +3~5MB / 每棵缓存树 ≈ 1~1.7MB；占稳态私有内存 ≈ 2~3%**。
- Small 预算实际封顶 3 棵缓存树（切换 9 成员后 materialized=7、cached=3）；即便预算放开到理论上限 6 棵，外推 ≈ 6~10MB ≈ 4~6%，仍 <10%。
- **结论：命中停止线——跳过 Cold 档（P3 / View Eviction 不做），只保留 P1+P2 方向。**

## 两个意外发现（比裁决本身更有价值）

1. **冷启动 `cachedGroupContents=0`**：非活动成员的内容在启动时根本不物化，只有切换过的成员才进缓存。原分析中"可见组缓存成员永不被释放"的前提在当前代码已不构成内存问题——缓存是**按需物化 + 预算封顶**的，不是常驻放大器。
2. **P2（Warm TTL/压力触发）的价值随之降级**：树不是大头，TTL 驱逐主要收益变为 CPU/订阅冻结而非内存。建议 P2 降为低优先级，等待真实用户内存反馈再决定是否立项；P1（适配器统一）中的 **P1-c 仍按 roadmap 无条件承诺执行**（它的价值是拓展性/生命周期一致性，不是内存）。

## 工艺记录（如实）

- framework Release 构建不认 `DESKBOX_DEV_DATA_ROOT`（`DeskBoxDataPathService.ResolveConfiguredRoot` 为 `#if DEBUG` 门控）。测量用临时解除门控的本地构建完成，**临时改动已全部还原，工作树与 PR #430 一致**；期间一次误判导致短暂怀疑真实 profile 被写——经快照比对确认为虚惊（真实 profile 0 格子、内容一致、仅 mtime 变化），零数据影响。
- 采样原始日志保留在 `C:/Users/simon/AppData/Local/DeskBox-Dev/residency-p0-20260926-groups/DeskBox.log`。
