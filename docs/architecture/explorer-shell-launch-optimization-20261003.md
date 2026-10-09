# Explorer 委派启动链优化：评估与实测结论（2026-10-03）

> 状态：**终稿——三个方案评估完毕，保留方案一（现状），不引入缓存或常驻线程。**
> 方案二曾在工作区实现，实测后已撤销（备份在 `C:\temp\shell-chain-probe\scheme2-backup`）。
> 数据来源：`DESKBOX_PERF_LOG=1` 真机分段计时（临时插桩，用后已还原）、
> 仓库外独立探针（`C:\temp\shell-chain-probe`，windows-rs 0.62.2 与产品同版本）、
> 外部点击/进程/窗口三时间点观测器（`launch-watch`）。

## TL;DR

- **原始归因不成立**。初版文档把热路径 ~90ms 几乎全记在"每次重建 COM 链"上；分段计时后，
  真实链重建只有 **4–8ms**（Rust 正式路径），**~92% 的时间是 `ShellExecute` 在 Explorer
  进程内同步执行**——这部分桌面双击同样要付，任何客户端侧缓存/常驻线程都省不掉。
- **方案一（现状，每次全链重建）**：保留。DeskBox 相对桌面的固定额外开销实测只有 **~7–15ms**。
- **方案二（GIT 缓存）**：已撤销。初版"GIT 条目必然随注册线程作废"的实验设计有缺陷
  （见 §3），但即使机制可行，实测收益也只有每次点击 ~6–12ms，不值得引入缓存状态机。
- **方案三（常驻启动套间线程）**：不采纳。COM 推理本身成立，但它只能省链重建那一段；
  预估的"~10–20ms 热路径"实际为 70–460ms（`ShellExecute` 主导），同时引入串行化、
  UAC 阻塞、忙超时退回可能重复启动、陈旧对象等结构性复杂度。
- **2.7s / 20s 级慢打开的真相**：都发生在 `ShellExecute` 返回之后或目标进程内部
  （网易云冷启动恢复，进程已创建但窗口 20s 才出现），与 DeskBox 的 COM 链无关。

---

## 1. 问题本身

盒子内打开项目的管线（`FileService.OpenItemAsync` → `Win32Helper.OpenFileOrChooseApp` →
`ExplorerShellLaunchService`）为了让孩子进程继承 Explorer 的新鲜用户环境，把 `ShellExecute`
**委托给 Explorer 桌面进程执行**（BrandonLive 2008 文档化的 COM 链，Firefox 同款）：

```
CoCreateInstance(Shell.Application) → Windows() → FindWindowSW(桌面窗口)
→ Document → Application → CoAllowSetForegroundWindow → IShellDispatch2.ShellExecute
```

其中 5~6 步是跨进程 COM 调用，全部串行排在 Explorer 那条单线程桌面 STA 上。

**初版文档的测量错误**：当时把 `FileService.OpenItem.ShellDispatch` 的整段计时
（包含链重建 + 前台授权 + `ShellExecute` 全程）记成了"COM 链重建"，
得出"热路径 ~90ms 全是链、首开 ~2.7s 全是链"的结论。分段计时后的真实分布见 §5。

---

## 2. 方案一：现状（每次全链重建，main 分支在跑的代码）

**机制**：每次打开，在一条新建的短命 STA 线程（`BoundedStaOperationRunner`，并发 2、
队列上限 6、排队超时 2s）上完成整条链的获取和执行，用完线程即毁。

**优点**：
- 天然满足一切 COM 套间规则（对象、注册、调用同套间同生命周期）。
- 无任何缓存状态，无陈旧问题，无失效路径；Explorer 重启后自愈。
- 实测固定成本只有 ~6–12ms/次（见 §5），低于可感知阈值。

**缺点**：仍有每次点击的个位数毫秒链重建成本，以及 `ShellExecute` 的固有同步等待
（任何方案都消不掉）。

**结论：保留，作为最终方案。**

---

## 3. 方案二：GIT 缓存（已实现后撤销）

**机制**：把链尾 `IShellDispatch2` 注册进 COM 全局接口表（GIT），后续点击在短命 STA 上
`GetInterfaceFromGlobal` 取代理直接调用，跳过链重建；配 PID 钉扎、陈旧重试、预热、
kill-switch（`DESKBOX_EXPLORER_LAUNCH_CACHE=0`）。

**初版证伪实验的局限（更正）**：最初 .NET 探针在短命 STA 上注册、线程退出后取用得到
`0x80070057`，文档据此断言"GIT 路线必然不成立"。后续 Rust 探针（无 RCW）证明这个结论
下早了：

- **常驻 MTA（`CoIncrementMTAUsage` 保活）注册 GIT，短命 STA 消费者取用：8/8 成功**。
  "封送包跟随最后取用 apartment"不是 COM 的真实行为。
- `RoGetAgileReference` 在注册 STA 退出后依然可用（3/3）。
- 初版实验被污染的原因：每个消费者线程都 `Activator.CreateInstance` 新建 GIT 对象，
  踩中 .NET RCW 身份缓存问题——是 .NET 互操作陷阱，不是 GIT 语义。

**撤销的真实理由不是"不可能"，而是"不值得"**：
- 链重建实测 4–8ms，缓存命中只能省下这一段；`ShellExecute` 70–460ms 原样保留。
- 成本却是完整的一套：GIT/AgileReference 生命周期、PID 钉扎陈旧判定、跨后端
  （C# 调试 + Rust 正式）双份实现、熔断/回退/观测的一致性维护。
- 审查遗留缺陷也真实存在：命中分支丢 catch-all 致 `TypeInitializationException` 穿透、
  陈旧错误码集过窄（漏 `0x80010108`/`0x800401FD`）等。

**若未来需要**：更窄的做法是持有不依赖套间的引用
（`AgileReference<IShellDispatch2>`，或常驻 MTA 里的 GIT 条目），每次打开在当时的
`BoundedStaOperationRunner` 短命线程上取回（探针实测取回 ~0.6ms、调用 ~0.1ms），
不需要常驻线程、消息泵、忙超时退回，并发语义与现状一致。

---

## 4. 方案三：常驻启动套间线程（不采纳）

**机制**（原设计）：创建与进程同寿命的 STA 线程 + 消息泵，在其上常驻持有
`IShellDispatch2`；每次打开把请求投递给该线程原地执行；忙超时退回 per-call 全链；
陈旧则重建对象内联重试。

**COM 推理成立**（对象与调用同套间同寿命，整类跨套间问题不存在），
**但工程上不成立**：

- **收益上限太低**：只能省链重建 4–8ms（冷启动 12ms）。原预估"热路径 ~10–20ms"
  建立错误归因上；实测 `ShellExecute` 单发 70–460ms（网易云 70–85ms，
  Axure 280–460ms），在 Explorer 内同步执行，常驻线程原样要付。
- **串行化新风险**：所有派发挤在一条线程上，UAC 模态期间整条通道阻塞。
- **忙超时退回可能重复启动**：`ShellExecute` 是已执行才返回的调用，已取走的请求
  再退回方案一会打开两次；只能安全退回"还没被取走"的请求，语义比文档原设想复杂。
- **陈旧重试错误码必须极窄**：只对确定未送达的码重试
  （`0x80010108`、`0x800401FD`、`0x800706BA`、`0x800706BF`）；
  `RPC_S_CALL_FAILED` 类"可能已执行"的错误叠加本地回退，最坏打开三次。
- **前台授权前提需复核**：桌面钉住模式下小组件带 `WS_EX_NOACTIVATE`，DeskBox 一般
  不是前台进程，授权依赖"最后一次输入事件归属"规则——队列等待越久越容易失效。

**结论**：不采纳。如果未来实测证明链成本在 Explorer 繁忙时被显著放大，优先选择
§3 末尾的窄化方案（AgileReference / 常驻 MTA + GIT），而不是常驻线程。

---

## 5. 实测数据（分段计时，2026-10-03）

### 5.1 独立探针（`C:\temp\shell-chain-probe`，Rust / windows-rs 0.62.2）

| 测量项 | 结果 |
|---|---|
| 全新 COM 链获取（每次新建 STA，即方案一） | 首次 ~21ms；热路径 p50 **8.8ms**（最大头是 `Windows()` 激活 ShellWindows ~3.5ms） |
| 常驻对象上普通跨进程调用 | **~0.06ms**（方案三的真实单次开销） |
| 常驻对象 `ShellExecute(cmd /c exit)` | **9–19ms**，Explorer 内同步执行完才返回 |
| MTA 常驻 + 短命 STA 取回（AgileReference/GIT） | 取回 ~0.6ms，调用 ~0.1ms |
| C# `dynamic` 同款链（控制台 Debug） | 首次 ~149ms；热路径 ~2.5ms |

### 5.2 DeskBox 真机分段计时（Debug 构建 + 临时插桩，`DESKBOX_PERF_LOG=1`）

Rust 后端（正式版同款路径，缓存关闭 = 纯方案一），单位 ms：

| 次序 | 目标 | 总计 | COM 链 | ShellExecute |
|---|---|---|---|---|
| 1（冷） | 网易云 | 232.0 | 11.6 | **215.7** |
| 2 | 网易云 | 93.5 | 7.9 | 84.8 |
| 3 | 网易云 | 77.4 | 6.5 | 70.3 |
| 4 | 网易云 | 75.8 | 5.7 | 69.3 |
| 5 | Axure | 468.8 | 5.6 | 462.4 |
| 6 | Axure | 325.2 | 7.6 | 316.6 |

C# `dynamic` 后端（仅 Debug 存在，正式版 AOT 不走）：

| 次序 | 目标 | 总计 | COM 链 | ShellExecute |
|---|---|---|---|---|
| 1（冷） | 网易云 | 232.2 | **127.4**¹ | 98.4 |
| 2 | 网易云 | 74.8 | 4.1 | 70.1 |
| 3 | 网易云 | 88.1 | 5.7 | 82.0 |
| 4 | 网易云 | 113.6 | 42.5² | 70.2 |
| 5 | Axure | 301.7 | 4.9 | 296.4 |
| 6 | Axure | 285.8 | 3.7 | 280.9 |

¹ `dynamic` 首次绑定/JIT 一次性成本，仅 Debug C# 路径存在。
² 偶发的对象创建停顿（`CoCreateInstance` 一段 39.5ms），非常态。

公共部分：点击→开始派发 0.5–4.5ms；`CoAllowSetForegroundWindow` 与前台转移均 <1ms。

**读法**：
- 热路径 ~92% 是 `ShellExecute`；链每次 4–8ms。DeskBox 相对桌面的固定额外开销
  ≈ 链 6–12ms + 自身簿记 1–2ms，合计 **~7–15ms**。
- 首开 232ms 而非 2.7s：两条后端都没复现原报告的 2.7s；C# 首开里 127ms 是
  `dynamic` 首绑，Rust 首开链仅 11.6ms，余下为首次执行该快捷方式本身偏慢
  （`ShellExecute` 216ms vs 后续 ~70ms）——桌面双击同样要付。
- 原始 2.7s 样本的日志已被性能日志轮转覆盖，无法回查；最可能是当次目标程序
  首次启动或 Explorer 繁忙的偶发，而非结构性成本。

### 5.3 网易云真冷启动（点击/进程创建/首窗三时间点观测）

前置：网易云完全退出；DeskBox 重启一次；另有一个 20:19 残留的
`cloudmusic.exe` 渲染子进程（无主窗口，~300MB）仍在。

| 次序 | 入口 | 点击→进程创建 | 进程创建→首窗 | 点击→首窗 |
|---|---|---|---|---|
| 1 | 盒子（DeskBox 重启后首开） | 227ms | **20,237ms** | **20,463ms** |
| 2 | 桌面 | 71ms | 1,611ms | 1,681ms |
| 3 | 盒子 | ≤106ms（`ShellExecute` 于 106ms 返回） | ≈1,410ms | 1,516ms |
| 4 | 桌面 | 110ms | 1,378ms | 1,487ms |

- 20 秒样本里 DeskBox 在点击后 227ms 就完成了进程创建；**20,237ms 全部发生在
  网易云进程内部**（首个窗口出现之前）。残留渲染进程在第 1 次启动后消失，
  之后盒子和桌面都稳定在 ~1.5s。
- 结论：极端慢启动是应用自身的冷启动/恢复行为（很可能与残留的渲染进程
  单实例协商/超时有关），不是 DeskBox 委派链路的成本。遇到类似慢启动先查
  目标进程残留（`Get-Process <name>`）。

---

## 6. 最终决策

**保留方案一**（每次全链重建，短命 STA，`BoundedStaOperationRunner`）：

1. COM 语义最保守、零缓存状态、Explorer 重启天然自愈；
2. 实测固定开销 ~7–15ms/次，低于可感知阈值；
3. 瓶颈在 `ShellExecute` 同步执行与目标程序自身启动——任何客户端侧结构优化
   （缓存、常驻线程）都不触及这两段；
4. 若未来在 Explorer 高负载下实测链成本显著放大，按 §3 窄化方案走
   （AgileReference / 常驻 MTA + GIT，每次在短命线程取回 ~0.6ms），
   仍不需要常驻线程与消息泵。

## 7. 本次评估顺带修复的两个诊断问题

- **`queueWaitMs` 名不副实**（`FileService.OpenItem.cs`）：原字段在 `await RunAsync`
  完成后才读表，实际记录的是"排队+执行"总时长。现改为 runner 内部实测的
  `_workers` 等待时长（`StaOperationResult.QueueWait`），日志同时保留
  `queueWaitMs`（真实排队）与 `runTotalMs`（整个 RunAsync 时长）。
- **隐藏小组件的 warmup 空转轮询**（`WidgetWindowBase.Collapse.cs`）：
  warmup 重试循环在窗口隐藏时仍每 ~320ms 醒一次并写
  `CompactExpansionWarmupDeferred`（观测期占性能日志 ~75%，导致 ~1 小时轮转、
  覆盖掉了原始 2.7s 证据）。现改为：不可见时记一行 `action=stop-until-shown`
  直接退出（显示路径本就会 `QueueCompactExpansionWarmup(urgent: true)` 重新武装），
  可见但被瞬时条件阻塞时日志按签名去重。`WidgetCompactWarmupPolicy`
  新增 `ShouldKeepWaiting` 承载该决策并有单测覆盖。
