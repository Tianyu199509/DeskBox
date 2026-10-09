# 多屏行为规格 v2 — 开发问题清单（待 Simon 审）

> 开发过程中跳过的、无法自行决定的问题按编号记录于此；已自行决定但与规格文档有偏差的项记录在实现报告（完成后另发）。每条含：背景、候选方案、临时采用的走向、建议。

## Q1（阶段 1，已按 A 实现待追认）整理建格漏斗的 Transaction 依赖注入
- 背景：5.8 要求整理建格走 ExplicitBounds 漏斗，但 `DesktopOrganizationTransaction` 无 WidgetManager 字段，且 `PlannedBounds` 是 double 矩形（漏斗要 int）。
- 候选：A. 构造函数加可选 `widgetManager`（3 处调用方：Coordinator/App 启动恢复传入；OrganizerService Undo 未传保持旧行为）；B. 把建格搬出 Transaction。
- 采用：A（最小侵入；Undo 路径不建新格，null 即旧行为）。**建议**：后续在 OrganizerService 里确认 Undo 确实不会 CreatesWidget，若会则补传。

## Q2（阶段 1，已实现待追认）CreateWidgetOfKindAsync 默认分支未持久化归属
- 背景：规格 5.8 表列了 `CreateWidgetOfKindAsync` 默认分支（注册格）接漏斗，但该分支构造的 config 直接交给 `CreateRegisteredWidgetFromConfigAsync`，**从不加入 Settings.Widgets**（窗口生命周期内不落盘，与文件格路径不同）。
- 候选：A. 按规格在该分支调 ApplyNewWidgetPlacement（只设几何+归属，不改变"不落盘"现状）；B. 顺手把它改成持久化（超范围）。
- 采用：A。**建议**：这个"创建不落盘"本身像是历史行为，值得单独开一条 issue 确认是否有意。

## Q3（阶段 2，已按折中实现待追认）临时档案的落盘隔离
- 背景：规格 5.6 要求首次出现的组合档案"只存在内存中"、10 秒或用户提交后才落盘。实现里 settings.WidgetTopologyLayouts 字典本身就是持久化对象（SaveDebounced 直接写 widget-layout.json）。
- 候选：A. 折中——临时档案照常进字典但**不参与 LRU 淘汰**，10 秒存活或用户放置提交（CaptureCurrentSurface）即转正（已实现）；B. 完整隔离——把临时档案放 WTLS 实例字典、转正后才写入 settings（需要改 widget-layout 存储层的保存协议与回读合并）。
- 采用：A。**建议**：真机观察瞬态拓扑（DP 休眠 1024×768）是否真的落盘且挤占真实档案；若观察到再上 B。

## Q4（阶段 2.5，未实现）外部移动采纳（B7）
- 背景：Win+Shift+方向键/第三方窗口管理器移动格子时，`OnAppWindowChanged` 在非拖拽/缩放时直接 return，配置不更新，下次恢复弹回。
- 规格 5.5 标注"可选，需真机验证"，误判率未知（系统在拔屏时自动挪窗必须排除）。
- 建议决策：真机上用 Spy++ 观察外部移动的 WM_WINDOWPOSCHANGED 特征（有无 SWP_NOACTIVATE/来源进程）后再定；若不做，用户用 Win+Shift+方向移动格子后按 F7 会弹回——可接受的已知限制。

（开发进行中持续追加）
