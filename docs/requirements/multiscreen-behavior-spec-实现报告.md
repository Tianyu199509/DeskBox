# 多屏行为规格 v2 — 实现报告（阶段 1-3 完成，2026-10-08）

> 本报告记录已完成阶段、与规格文档的全部偏差、跳过的问题（另见开发问题清单.md）。DeskBox 已从规范 Debug 路径重启验证，测试 **4865/4865 全绿**，未提交。阶段 4 已于同日完成（4.1 CCD 友好名 / 4.3 设置页重构+MoveAll 确认撤销 / 4.4 断开收起 / 4.5 十二语言）。

## 一、完成状态总览

| 阶段 | 状态 | 验收 |
|---|---|---|
| 阶段 1 修行为缺陷（1.1-1.7） | ✅ 完成 | R1-R5 新增回归全绿；S 真机待 Simon |
| 阶段 2 拓扑响应节奏（2.1-2.4） | ✅ 完成 | R13-R16 单测绿；S11-S14 真机待 Simon；2.5 外部移动采纳**未做**（规格标可选+需真机验证，记 Q4） |
| 阶段 3 档案模型（3.1-3.3） | ✅ 完成 | R6/R7 语义经改写测试锁定；S2/S10/S15 真机待 Simon |
| 阶段 4 界面（4.1-4.5） | ✅ 完成 | 4865/4865 绿；拖动提示 S7 未做（规格 6.2 允许省略）；真机矩阵待跑 |

## 二、新增文件

- `Services/DisplayPlacementResolver.cs` — 统一显示器解析器（规格 5.1 全矩阵）
- `Services/DisplayTopologyGate.cs` — 拓扑应用门（纯状态机+注入时钟）
- `Services/DisplayIdentityTokens.cs` — 身份集合 token（v4 key 与宽限判定共用）
- `Services/WidgetManager.ScreenHome.cs` — CommitUserPlacement + 归属更新 + 最大交集屏判定
- `Services/WidgetManager.NewWidgetPlacement.cs` — 新格子放置漏斗
- `tests/DisplayPlacementResolverTests.cs`（9）、`tests/DisplayTopologyGateTests.cs`（7）

## 三、关键行为变更（发布说明需写明）

1. **主显示器切换不再带走"原来在主显示器上的"格子**——WasPrimary 智能跟随已从运行时删除（9.2 三个测试按新语义改写）。
2. **非首次运行的新格子默认位置**从"主显示器左上 100,100"改为"『新格子出现在』设置的目标屏右上对齐+级联"（默认鼠标所在屏）。
3. **分辨率/缩放/主屏/排列变化不再各存一套布局**（v4 key 只按显示器身份集合）；DPI 往返保留用户编辑的 DIP 意图、物理位置按当前 DPI 重算。
4. 拖动跨屏即更新归属（"放哪属于哪"）；归属屏离线时的拖动不改归属（回退态保护）。
5. 锁屏/显示器关闭/全屏独占/拔屏 4 秒宽限期内不再瞬时重排格子。
6. Schema 11→12→13 两次迁移（归属推断/权威标志/胶囊字段同步；v3 档案重算 v4 key 并合并）。

## 四、与规格的偏差清单（全部偏差）

| # | 偏差 | 原因 |
|---|---|---|
| D-1 | 临时档案（IsProvisional）**未做落盘隔离**：仍写入 settings 内存字典并会被持久化，只实现"不参与 LRU 淘汰+10s 转正+用户提交转正" | 完整隔离需改 widget-layout 存储层；见问题清单 Q3 |
| D-2 | FullscreenApp 门用**常驻 2 秒轮询**（SHQueryUserNotificationState），而非规格的"仅门关闭期间轮询" | 实现简化，单次 P/Invoke 成本可忽略；行为等价 |
| D-3 | RemovalGrace 宽限时长、StartupSettling 5 秒、Provisional 10 秒均为代码常量，未做真机校准记录 | 真机验收阶段统一校准（规格 5.6 也要求真机校准） |
| D-4 | 5.4 的显示器判定规格为"最大交集面积"；实现为**最大交集+并列取主屏+零交集取中心最近**（规格并列规则未定义，我补了确定性的 tiebreak） | 规格未定义并列；与 Windows MonitorFromRect 语义一致性待真机核 |
| D-5 | R5 胶囊测试以 `CapturePlacement` 落盘字段为准（规格 4.4 的"Resolve 用表面身份"也已实现于 WidgetCompactBoundsCalculator） | 测试锚点选择 |
| D-6 | 阶段 4 全部 UI 项未做（见上表）；1.7 菜单已按 6.1 新结构实现但**无友好名**（显示 分辨率·主显示器，阶段 4.1 的 CCD 名称未接） | 阶段顺序+上下文预算 |
| D-7 | CreateSnapshotForTest/test 缝的 AvailableMonitorWorkArea 增加了 MonitorRect/DpiScale 分量（规格未提测试缝变化） | 5.1 启发式需要监视器矩形 |
| D-8 | `EndRemovalGraceByUserAction` 经 `WidgetManager` 属性委托注入（规格 5.6 只说"唤起/拖动/设置页移动→结束宽限"，未规定注入机制） | 模块边界契约（BusinessGlobalAccess 只减不增） |

## 五、修复的既有缺陷（对照规格 2.2）

B1 ✅（统一解析器+删智能跟随）｜B2 ✅（v4 key）｜B3 ✅（逐表面权威播种，R4 测试锁）｜B4 ✅（胶囊派生+迁移回填，R5 测试锁）｜B5 ✅（无损重投影+realize 只写物理缓存）｜B6 ✅（CommitUserPlacement 全入口）｜B7 ❌ 未做（外部移动采纳=2.5 可选，Q4）｜B8 ✅（最大交集判定）｜B9 ✅（漏斗+10 路径）｜B10 ✅（门+宽限+启动等待+临时档案）｜B11 ❌ 友好名未做（阶段 4.1）｜B12 部分（菜单术语已改"移到显示器/始终在主显示器"；设置页术语待阶段 4.5）。

## 六、验收缺口（下一步）

1. **真机矩阵**（9.3 十项清单：接坞往返/主屏分辨率缩放排列/DP 休眠/锁屏睡眠/全屏游戏/DisplayLink 冷启/Win+P/远程桌面/同型号换口/设置页全入口）。
2. 问题清单 Q1-Q4 待 Simon 审。
3. 拖动提示 S7（6.2）未做——规格允许引导层不便扩展时省略，行为不受影响。

## 七、测试与构建状态

- 全套 `dotnet test -p:Platform=x64`：**4865/4865 通过**（新增 16 个：Resolver 9 + Gate 7；改写 9 个既有测试按 v2 语义）。
- 契约冻结同步：Schema pin ×2（13）、迁移链测试 → Thirteen、门面 235/236、slice 13、SettingsWireKeys +widgetNewPlacementTarget、顺序基线同、FacadeAccessManifest（MigrationService 55 / SettingsService 611 / WTLS 23 / DisplayIdentityTokens 2 / NewWidgetPlacement 4 / ScreenHome 2）、BusinessGlobalAccess 无增长（注入委托替代 App.Current）。
- DeskBox 已从 `D:\project\wingezi\src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe` 重启，启动日志干净（0 异常）。


## 八、审查修复批次（2026-10-08 晚，3-Agent 并行）

审查报告（multiscreen-behavior-spec-审查报告.md）的 **5 高 + 8 中 + 8 低 + 8 文案 全部修复**，4865/4865 两轮全绿，已从规范路径重启。

| 项 | 修法 |
|---|---|
| H1 机器码上屏 | `WidgetConfig/WidgetGroupConfig` 新增 `BoundScreenLabel`（绑定时持久化友好名快照；ApplyScreenBindingAsync/CommitUserPlacement/组镜像四处写入）；菜单/设置页全部离线显示改用快照，无快照回退 H2 新键 |
| H2 `{0}` 字面量 | 新键 `Widget.ScreenBinding.HomeOfflineUnnamed`（无占位符），删除 Split('·') hack |
| H3 撤销残留/永不过期 | `ShowStatus` 每次清 ActionButton；WidgetManager 新增 `PlacementGeneration`（CommitUserPlacement/ApplyScreenBindingAsync 自增），令牌记录版本、Undo 前比对失配返回 false → InfoBar 显示"布局已变化，无法撤销" |
| H4 收起不驱动窗口 | `IDesktopWidgetWindow` 新增 `RequestCompactState(collapsed, animate)` → `WidgetWindowBase` 转调 `SetCollapsedState(persistManualState:false)`；断开收起/回家展开均驱动活窗口 |
| H5 拔插屏不刷新 | App 新增 `DisplayTopologyChanged` 事件（OnDisplaysChanged 经 UI 队列触发）；分区 Loaded 订阅/Unloaded 解绑 |
| M1-M8 | FollowPrimary radio 不打勾；菜单统一 DisplayName；E710/E8A9/E718 图标；识别闪屏字号按屏 DPI 换算；ApplyScreenBindingAsync 幂等守卫（含组镜像一致）；Spacing 继承全局；识别闪屏 AutomationProperties.Name；BadgeColor 改 accent（回退 #005FB8）+3 秒 |
| L1-L8 | 对比 0.35-0.55 加 1px 内描边；删死代码/死键；PreviewHint 走 TextFillColorSecondary（code 注入）；ComboBox 统一 240；"其中 0 个"省略句（新键 ConfirmBodyAllHere）；Disconnect 描述下移子卡；组窗口匹配重构；toggle 图标 |
| 文案 P1-P8 | MonitorFormat/PreviewHint/Cursor 统一"显示器/鼠标指针"（zh/zh-TW/ja）；MoveAll 动词式「全部移到此显示器」+ ConfirmTitle"将"体；Disconnect.Move「将其格子」；en HomeOffline 消歧义 + pointer 统一；死键 Offline 删除；净增 3 键删 1 键 ×12 语言（文本级替换保序） |
| 契约 | AotStage4D1B 176/22（L6 挪卡）；样式白名单 +boundScreenLabel；搜索目录 +3 条；FacadeAccess 无变化 |

修复期间插曲：`DesktopAutoOrganizationWatcherDelayTests(300)` 两轮失败，取证为负载 flake（stash 基线复跑绿+隔离绿+新分支在该路径不可达），非产品缺陷。
