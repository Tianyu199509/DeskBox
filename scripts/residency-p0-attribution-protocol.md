# Residency P0 归因实验协议（2026-09-18）

上游：`docs/architecture/widget-group-residency-roadmap-20260918.md` §6 P0。

## 目的

回答一个决策门问题：**组成员非活动树占稳态私有内存的比例是多少？** <10% → 跳过 P3 Cold 档，只做 P1 + P2；≥10% → 全线。

顺带产出三份定参数据：切换延迟四点分布（按 cached / fresh 分开）、真实回切间隔分布（定 TTL）、各持有者的边际斜率（组成员树 / 图片缓存 / File 投影 / 每窗口固定成本）。

## 插桩（已落地，任何构建都带）

- `[WidgetGroup] Switched ... source=cached|fresh prepareMs= firstFrameMs= settleMs= totalMs= sinceLastActiveMs=`
  — 每次组内切换一行；`-` 表示该阶段未发生（隐藏组不等首帧；成员首次激活无回切间隔）。
- `[Perf] MemorySample ... cachedGroupContents= materializedContentByKind=File=2;Todo=1 cachedContentByKind=Todo=1 ...`
  — 需要 `DESKBOX_PERF_LOG=1`，30s 一行。`materialized` = 活动树 + 非活动缓存树；`cached` = 其中非活动的。
- `[Memory] Deep finalizer cleanup completed ... privateBeforeMB= privateAfterMB= reclaimPrivateBeforeMB= reclaimPrivateAfterMB=`
  — 强制 GC 分支（MemoryReclaimer 两次 max-gen + finalizer），只在**全部格子隐藏**满 `HiddenCacheCleanupDelaySeconds`（默认 30s）后触发。

## 隔离

与 AOT 曲线协议相同：`DESKBOX_AOT_PREVIEW_DATA_ROOT` 指向独立数据根，复用 `measure-aot-memory-curve.ps1` 采样器（`-CsvPath` 换成本实验的 CSV）。**加 `DESKBOX_PERF_LOG=1`**。

```powershell
$env:DESKBOX_PERF_LOG = '1'
./scripts/measure-aot-memory-curve.ps1 -Phase <phase> -Minutes <n> [-Attach] `
    -CsvPath D:\project\wingezi\deskbox-residency-p0-samples.csv `
    -DataRoot "$env:LOCALAPPDATA\DeskBox-ResidencyP0"
```

Debug 构建也可以跑（口径一致即可，但 Release/AOT 更接近用户数字；两者别混在一个 CSV 里）。

## 阶段（Simon 手动操作；每阶段末尾**做一次强制 GC 分支**：全部收进托盘 → 等到 `Deep finalizer cleanup completed` 行出现 → 再展开，让下一阶段起点干净）

### A. 组成员 M(n) 曲线（主实验）

固定性能档 **Large**（warm 容量 2，让缓存真正装满），准备一个含 ~500 文件的文件夹。

| 阶段名 | 操作 | 读什么 |
|---|---|---|
| `m1-file` | 1 个文件格子（500 文件），不建组，静置 3 min | 基线 M(1) |
| `m2-file-todo` | 把一个 Todo 格子合并进来成 2 成员组；切换 3 次让 Todo 进缓存；静置 3 min | ΔM = 一棵 Todo 非活动树 |
| `m3-file-file` | 再合并第二个文件格子（同一 500 文件夹）；切换让两棵 File 树都物化；静置 3 min | ΔM = 一棵 File 非活动树 |
| `m3-cleared` | **对照**：把性能档改为 Small（warm=1）→ 切换一次触发逐出 → 强制 GC 分支 → 静置 3 min | 逐出一棵树后回落多少（GC 推迟回收的真实幅度） |
| `m8-mixed` | 8 成员组（File/Todo/QC/Weather/Search/Glance/Music 混合）；轮切每个成员 2 遍；静置 3 min | 上限场景；`materializedContentByKind` 应稳定在 warm 容量 + 1 |

判读：`cachedContentByKind` 每变化 1 时 `privateMB` 的阶跃 = 那一种 kind 一棵非活动树的成本。**用强制 GC 后的静置值算**，不用逐出瞬间的值。

### B. 对照组（判别"大头在哪"）

| 阶段名 | 操作 | 读什么 |
|---|---|---|
| `ctl-icons` | 单文件格子，500 → 2500 文件（拖入 2000 个）；静置到 hydration 完成 | `thumbCacheMB` / `decodedBitmapMB` 估算值 vs `privateMB` 实际增量的差 = 估算口径的 native 欠账 |
| `ctl-projection` | 同一 2500 文件格子，切到另一个成员让它进缓存 → 再改 Small 逐出它（树没了、VM 也没了）→ 强制 GC | 这一步回落 = 树 + 投影；与 A 里 File 树的 ΔM 相减 ≈ 投影成本 |
| `ctl-window` | 新建 3 个空 Todo 格子（不建组，3 个窗口）→ 静置 → 全隐藏 → 强制 GC → 展开 | 每窗口固定成本（含 DWM 重定向面，profiling 看不见，只能靠 Private Bytes 差） |

### C. 切换延迟与回切间隔（被动收集）

日常使用一天，不做操作。汇总脚本从 `DeskBox.log` 抽 `Switched` 行：
- `totalMs` 按 `source` 分组的 P50/P95 → Go/No-Go 基线（cached 是 Warm 命中的体验下限，fresh 是今天 Cold 的近似）；
- `sinceLastActiveMs` 分布 → TTL 定参：取 P80 作为"不该过期"的下界。

## 判读

```powershell
./scripts/summarize-residency-p0.ps1 -LogPath "$env:LOCALAPPDATA\DeskBox-ResidencyP0\DeskBox.log" `
    -CsvPath D:\project\wingezi\deskbox-residency-p0-samples.csv
```

输出：① 每阶段 privateMB 起/峰/末 + 强制 GC 后的值；② `materializedContentByKind` 变化点与对应的 privateMB 阶跃；③ Switched 延迟四点 P50/P95 by source；④ sinceLastActive 分布 P50/P80/P95。

**决策门算法**：`组成员非活动树成本 = Σ(cached 树数 × 该 kind 的 ΔM)`，除以 `m8-mixed` 强制 GC 后静置 privateMB。<10% 停 P3。

## 收尾

实验数据根可整目录删除；CSV/日志摘要留工作区（随 residency 文档一起处置，不提交原始 CSV）。
