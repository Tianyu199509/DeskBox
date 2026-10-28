# DeskBox 内存占用归因与优化方案（2026-10-27）

## 结论速览

**占用大头不是缓存、不是托管泄漏，而是「WinAppSDK/WinUI 底座 + 每格子一个独立 HWND/XAML 内容树」的 native 内存。**

- 全部图片/图标缓存在 12 格子真实配置下合计 **< 2MB**，可以排除。
- 托管堆两次 gcdump 差分仅 +1.5MB（29.6→31.1MB），无泄漏。
- **折叠（胶囊）态几乎不省内存**：12 格子全折叠 270.8MB vs 展开 288.3MB，只差 17.5MB（6%）。这是当前代码里最值得修的点。
- 用户日常的 300+MB（Debug 构建 411-452MB Private）中，约 **120-160MB 是 managed-jit 运行时开销**；AOT 零售同配置为 288MB Private。

## 测量方法

- 构建：`.artifacts/aot-retail` 的 Native AOT 零售产物，复制到 `%TEMP%\deskbox-aot-run` 运行（仓库路径下的进程会被 repo-owned 清理误杀）。
- 数据根：`%TEMP%\deskbox-memattr-data`（复制自生产 `%LOCALAPPDATA%\DeskBox` 的 12 格子真实配置），经 `DESKBOX_AOT_PREVIEW_DATA_ROOT` 隔离。
- 口径：`DESKBOX_PERF_LOG=1` 的应用内 `[Perf] MemorySample` + `Get-Process` 采样，启动后 ~50s 稳态读数。**Private（提交）为主口径**，WS 受工作集裁剪影响仅参考。
- 对照变量：同一构建、同一数据根，仅改 `widget-layout.json` 的 `layout.widgets`（注意不是顶层 `widgets`——顶层不存在该键）。
- 测量陷阱记录：
  1. `SettingsService.cs` 规范化会把所有非 Glance 格子的 `IsDisabled` 强制改回 `false`，靠禁用做对照无效；需**清空 widgets 列表 + 写入 deletedWidgetIds 墓碑**。
  2. 测试进程会在启动它的 shell 退出时被作业对象回收，启动/等待/采样必须在同一次调用内完成。

## 归因数据（AOT 零售，ResourceSaver + Small cacheBudget）

| 场景 | windows | WS(MB) | Private(MB) | 托管堆(MB) | private−GcHeap(MB) |
|---|---|---|---|---|---|
| **0 格子（地板值）** | 0 | 175.9 | **96.1** | 10.2 | 89.8 |
| 1 个 File 格子（另 2 个组占位窗） | 3 | 270.9 | 188.9 | 23.6 | 162.8 |
| 5 个 File 格子 | 5 | 297.5 | 217.3 | 27.2 | 191.2 |
| **9 格子全开（真实混合配置）** | 9 | 371.8 | **288.3** | 41.5 | 256.9 |
| 12 格子全折叠（胶囊态） | 9 | 352.5 | 270.8 | 35.8 | 241.8 |

派生结论：

| 成本项 | 数值 | 说明 |
|---|---|---|
| 平台底座（0 窗口） | **~96MB Private** | WinAppSDK/WinUI 初始化、托盘、热键、SettingsIpc、显示监听等；native ≈ 90MB |
| 每格子窗口边际成本 | **~15-20MB Private/窗口** | 5→9 窗口实测 (288.3−217.3)/4 ≈ 17.8MB；含 HWND、XAML 内容树、DirectComposition/D2D 图面 |
| 托管堆 | 10→42MB | 随格子数缓慢增长，健康 |
| 图标/位图/缩略图缓存 | **< 2MB** | iconCache 55 条、decodedBitmap 0.8MB、thumbCache 0——预算上限 32/48/32MB 实际远未触及 |
| Debug(JIT) 运行时溢价 | ~120-160MB | 用户 Debug 实例 Private 411-452MB vs AOT 同档 ~289MB |

组成员缓存树此前 P0 归因已证实只占稳态 2-3%（`residency-p0-attribution-20260926.md`），本次结论与之互补：**问题在每窗口固定成本与内容树驻留，不在缓存。**

## 代码问题判定

1. **【主因】折叠/胶囊态不释放内容树**（`WidgetShell.xaml.cs` / `WidgetWindowBase.Collapse.cs`）
   `SetCollapsed` 只切换 chrome 层，`ShellContentPresenter.Visibility = Collapsed` —— 仅隐藏不卸载，整棵 XAML 内容树 + ViewModel + 图面全量驻留。实测 12 格子全折叠只比展开省 17.5MB。
2. **【放大器】长隐藏回收被 `Visible` 门槛锁死**（`ContentWidgetWindow.ContentSwitching.cs:210`）
   `ReleaseLongHiddenContentResources()` 首行 `if (Visible || IsClosing) return 0;`。胶囊态窗口仍 `Visible=true`，该回收路径**永远不会触发**；且该方法刻意保留 `CurrentContent` 只清缓存组员（注释：避免下次展开重建）。结果：隐藏/折叠格子的 XAML 树无任何释放路径。
3. **【架构固有】一组一窗、一格子一 HWND**——多格子架构（保持不变）的必然成本。WinUI 3 每窗口 1080p GDI 重定向图面约 7.9MB（roadmap #11523），外加每棵内容树。折叠时 HWND bounds 会缩到胶囊尺寸（图面随之缩小），这解释了折叠残留的 ~17.5MB 降幅。
4. **【轻微】File 格子整目录物化**：5 个 File 格子 90 个 FileItemSurface 全量驻留；`RenderedItems` 渲染窗口阈值 300 起作用，用户目录均 <300 项故全部物化。单条目成本低，不是大头。
5. **【行为怪异，非内存问题】** `SettingsService.cs` 规范化强制 `IsDisabled=false`（非 Glance）——「禁用」语义实际只对 Glance 生效，属产品语义问题，建议另行澄清。
6. **【观察项】** 长会话 Private 缓慢爬升（Debug 90min 337→452MB）而托管堆平稳，是 native 侧缓慢增长（D2D/XAML/碎片），未见泄漏形态；54→63 线程波动属线程池正常范围。

## 优化方案（保持多格子 widget 架构不变）

### P0-A：胶囊/折叠态卸载内容树（治本核心，预期收益最大）

- 新增「内容休眠」状态，与「窗口可见」解耦：折叠为胶囊（或组内非活动成员超时）后，卸载 `ShellContentPresenter` 内容树、释放 content ViewModel 与图面，只保留 HWND + 壳 chrome + 胶囊 UI。
- 展开时重建（预算 100-300ms 一次性重建，可复用现有 `CompactWarmup` 通道做预热）。
- **预期**：12 格子全折叠从 270MB → 约 130-160MB；日常「常驻几个胶囊」场景每格子驻留从 ~18MB 降到 ~5-8MB（壳 + HWND）。

### P0-B：补全长隐藏释放路径

- 修 `ReleaseLongHiddenContentResources` 的 `Visible` 门槛：胶囊可见但内容隐藏时也应允许释放 `CurrentContent`（现在刻意保活）。
- 与现有 `hiddenCacheCleanupDelaySeconds` 节拍对齐：隐藏 N 分钟后整树释放，重建走冷路径。
- **预期**：深夜/长时间不用时 Private 可回落到底座 + 少量壳；工作集裁减从「必开」降为「可选锦上添花」。

### P1-A：每窗口固定成本瘦身

- 压缩每格子 XAML 树深度：chrome 元素（把手、边框、按钮层）用 `x:DeferLoadStrategy`/按需加载；共享 Style/DWM 资源已共享的部分继续保持。
- 评估长隐藏格子直接**销毁 HWND**（roadmap 的 Cold 档）：彻底消除 7.9MB 图面 + 窗口过程开销，唤起时重建窗口。作为 P0-A 的激进版，可做成设置项（如「深度休眠」）。

### P1-B：File 格子渲染窗口下移

- `RenderedItems` 初始 30 / 增长块 200 的机制保持；对 <300 项目录也启用窗口化（如 60 项），减少一次性 90 个 ItemSurface 类的全量物化。
- 图标解码维持现有预算与 LRU（实测使用率极低，无需扩）。

### P2：长会话 native 爬升治理

- 在 `MemorySample` 已有字段上加趋势告警（`privateMinusGcHeapEstimateMB` 连续上涨超阈值时触发一次全量重建或提示重启）。
- 继续用 gcdump 差分巡检托管侧；native 侧若持续膨胀，再定向排查 D2D/DWrite 位面缓存。

### 不建议做的事

- ❌ 把多格子合并成单 widget（违反基础需求）。
- ❌ 大幅削减 IconHelper 三缓存预算（实测使用 <2MB，削减无收益）。
- ❌ 强制 GC 作为常规手段（托管堆只占 10-42MB，收益极小）。
- ⭕ 工作集裁减保留默认关闭即可——P0 落地后它只剩装饰性收益。

## 预期总账（日常「几个胶囊常驻」场景）

| 状态 | 现状 (AOT) | P0 落地后（估） |
|---|---|---|
| 9-12 格子全展开 | ~288MB | ~250-270MB（P1 树瘦身） |
| 9-12 格子全折叠 | ~271MB | **~130-160MB** |
| 1-2 格子展开 + 其余折叠 | ~250MB | **~150-180MB** |
| Debug 构建同上 | +120-160MB | 建议日常用 AOT 零售构建 |

核心判断：**代码没有严重 bug 或泄漏，但「折叠不释放、隐藏回收被 Visible 锁死」两个设计决策叠加，让多格子架构的每窗口成本在用户最常见的胶囊常驻场景下全额驻留**——这是 300MB 体感的直接来源。P0-A/B 落地即可治本，且完全保持多格子架构。
