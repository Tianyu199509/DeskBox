# 多屏功能 v2 — UI/交互/文案自查审查报告（2026-10-08）

> 对象：阶段 1-4 全部落地的多屏功能（设置页、右键菜单、识别闪屏、文案）。两路代理精读代码+文案，对照 WinUI 3 / Windows 11 设置规范与用户直觉。按严重性分级；每条含位置与建议修法。
>
> **✅ 2026-10-08 修复批次已完成**（3 个 agent 并行：服务层/UI 层/文案+测试）：高 5/中 8/低 8/文案 8 **全部修复**，4865/4865 两轮全绿，DeskBox 已从规范路径重启。抽查确认：H1 友好名快照字段+显示链、H2 无占位符键、H3 令牌版本失效（PlacementVersion 比对）+ShowStatus 清按钮、H4 RequestCompactState 接口驱动活窗口、H5 DisplayTopologyChanged 事件订阅均已落地；文案抽查「显示器 {0} · {1}」「全部移到此显示器」「鼠标指针所在的显示器」生效、死键已删。修法明细见实现报告附录。

## 一、高严重性（功能缺陷或用户可见的硬伤）

| # | 问题 | 位置 | 说明与建议 |
|---|---|---|---|
| H1 | **离线归属显示原始设备路径（机器码上屏）** | 菜单 `WidgetScreenMenuBuilder.cs:109-117`；设置页行下拉 `DisplaySettingsSection.xaml.cs:678`、摘要 `:747`、"新格子出现在"离线项 `:542` | `{0}` 填的是 `BoundScreenId` 原始值——形如 `\\?\DISPLAY#DELD0E6#4&...#{e6f07b5f-...}` 的设备接口路径。菜单和下拉里会出现一长串机器码，且可能撑破 ComboBox。**建议**：绑定变更时持久化友好名快照（如 `BoundScreenLabel`），离线时显示它；没有快照时用固定文案"之前选择的显示器（未连接）"。 |
| H2 | **退化 ID 回退文案把 `{0}` 字面量上屏** | `WidgetScreenMenuBuilder.cs:113-115` | 回退逻辑取 `T("HomeOffline").Split('·')[0]`——这是未格式化的模板串。en-US 切出 "Home display {0} (disconnected)"，zh-CN 切出整句模板，**`{0}` 直接显示给用户**。建议新增无占位符专用键。 |
| H3 | **撤销按钮残留 + 撤销令牌永不过期** | `DisplaySettingsSection.xaml.cs:447-478, 820-826`；token 字段 `:389` | ① `ShowStatus` 从不清空 InfoBar.ActionButton——撤销后"已撤销"上仍挂着禁用按钮，之后任何状态提示（如移动失败）都带着旧撤销按钮复现，可点击旧 token。② 规格 5.9 要求令牌在"下一次任何放置提交"时失效——现在永不失效：用户移动全部→手动调整→点旧撤销=静默回滚掉中间调整。**建议**：ShowStatus 按语义清空 ActionButton；ApplyScreenBindingAsync/拖拽提交处清空令牌。 |
| H4 | **断开收起策略不驱动真实窗口（功能缺口）** | `WidgetManager.MoveAll.cs:250-297` | `SetSurfaceCollapsed` 只写 `config.IsCollapsed` 标志+重定位窗口。但运行时收起判定依赖 `IsCompactBoundsStateActive` 运行时字段（`Collapse.cs:184-185`），真正的 `SetCollapsedState`（`Collapse.cs:2772`）是 private 且 `IDesktopWidgetWindow` 未暴露收起 API。**结果：设置"收起为胶囊"后，断屏时实际窗口不会收起**，标志只在窗口重建后生效；"回家自动展开"同样不作用于活窗口。**建议**：接口加 `RequestCompactState(bool, bool)` 转调 SetCollapsedState 非持久化路径。 |
| H5 | **设置页打开时拔插屏不刷新（S27 未实现）** | `DisplaySettingsSection.xaml.cs` 全文件 | Refresh 仅在 Loaded/主题变化/页面导航/内部操作时触发；App 的 `DisplaysChanged` 没有通知到本分区。热插屏后预览、下拉、徽标全是旧拓扑。**建议**：App 暴露 DisplaysChanged，分区订阅（Unloaded 解绑）。 |

## 二、中严重性（规范不符或直觉混乱）

| # | 问题 | 位置 | 说明与建议 |
|---|---|---|---|
| M1 | **FollowPrimary 双重选中态** | `WidgetScreenMenuBuilder.cs:39-79` | 跟随主显示器的格子位于主屏时：radio"显示器 1"打勾 + toggle"始终在主显示器"打勾同时出现，两种选中语义叠在一起。**建议**：FollowPrimary 时 radio 不打勾（isActual 加 `mode==Pinned &&`）。 |
| M2 | **同一块屏两处叫法不同** | 菜单 `:41-44` 用分辨率；设置页用友好名 | 菜单显示"显示器 1 · 2560×1440"，设置页显示"显示器 1 · DELL U2720Q"。统一用友好名（回退分辨率）。 |
| M3 | **图标语义不符** | `.xaml:74`、`.xaml.cs:692`、`WidgetScreenMenuBuilder.cs:62` | ① "新格子出现在"用 E7ED（静音铃铛 RingerSilent）→ 建议 E710（添加）；② 每行格子用 E8A5（文档）→ 建议 E8A9（地图层）或格子自带图标；③ "始终在主显示器" toggle 与父级同用 E7F4（显示器）→ 易读成"切换屏幕"，建议换 E1E2（固定）类。已核实官方码位表。 |
| M4 | **识别闪屏字号换算错误** | `DisplayIdentifyOverlayWindow.cs:47` | `numeralHeight * 72/96` 是 px→pt 换算，但 WinUI FontSize 单位是 DIP。100% 缩放下数字比设计小 25%，150% 下小约 45%。**建议**：除以该屏 `EffectiveDpiScale`。 |
| M5 | **已选中项重复点击非空操作** | `WidgetScreenMenuBuilder.cs:130` | radio 已选中再点击仍走 applyBinding——ApplyScreenBindingAsync 会 EndRemovalGrace+重捕获几何+重标权威，可能覆盖原存储布局。**建议**：菜单/下拉侧先比对 mode+boundId 相同则跳过。 |
| M6 | **卡片间距偏离全局** | `.xaml:21` | `Spacing="12"` 覆盖了 SectionPanelStyle 的 4，与设置页其它分区及 Win11 紧凑分组不一致。 |
| M7 | **识别闪屏无障碍缺失** | `DisplayIdentifyOverlayWindow.cs:37-48` | 不透明全屏遮罩对讲述人完全不可见。建议 TextBlock 设 `AutomationProperties.Name="显示器 N"`。 |
| M8 | **固定蓝 #005CE6 非标准 token** | 同上 `:25` | Win11 识别闪屏实际跟随 accent；固定色可接受但建议改 accent 或官方基色 #005FB8；时长 2.4s 建议对齐 Windows 的 ~3s。 |

## 三、低严重性（打磨项）

- L1 暗色主题下 accent 边缘对比（阈值 0.45）可能翻车，建议 0.35-0.55 区间加边框强化（预览选中卡）。
- L2 `AccentBrush()` 死代码（`.xaml.cs:25`）；死键 `Settings.Displays.Widgets.Offline`（代码零引用，但 H1 修复后可能反过来需要它）。
- L3 PreviewHint 硬编码 FontSize=12/Opacity=0.7，应改 Caption 样式 + TextFillColorSecondaryBrush。
- L4 同页 ComboBox 宽度不一（220 vs 280），Win11 同页统一约 240。
- L5 确认框"其中 0 个当前在其他显示器上"建议为零时省略。
- L6 "多显示器" expander 头部的 Disconnect.Description 读起来像描述整组而非子项，建议下移到子卡。
- L7 组窗口匹配三重 FindByMember 嵌套写法脆弱（`MoveAll.cs:278-295`）。
- L8 菜单 toggle 图标与父级重复；"移到显示器"子菜单含跟随开关，可考虑"在哪个显示器上显示"。

## 四、文案审查（12 语言，zh-CN 为主）

**术语对齐 Win11**：
- P1 `MonitorFormat`「屏幕 {0} · {1}」——全家族其余键均用"显示器"，Win11 也叫"显示器 1"。改为「显示器 {0} · {1}」（en-US 已是 Display，中英不对应）。
- P2 `PreviewHint`「选择一个屏幕以查看它的设置」→「选择一个显示器以查看其设置」。
- P3 `NewWidgets.Cursor`「鼠标所在的显示器」——Win11 称"鼠标指针"。改「鼠标指针所在的显示器」（Offline 键同步）。
- ✓ 主显示器/识别/内置显示器与 Win11 一致。

**句式层级**：✓ 设置描述无句号、对话框正文有句号、标题全角问号——用对了。仅 `Description`"在哪块显示器"口语化，建议「查看显示器排列，并选择格子的显示位置」。

**标点**：P4 `Widgets.Offline` 全角"）"与"·"之间无空格，其余键均两侧空格——统一。

**一致性**：
- P5 把/将混用（按钮用"把"、正文用"将"）。Win11 正式文案惯用"将"；建议按钮/菜单保留动词式，正文统一"将"。
- P6 `MoveAll` 按钮口语把字句「把所有格子移到这里」→ Win11 动词式「全部移到此显示器」，ConfirmTitle 相应调整。

**en-US**：P7 Offline/HomeOffline 的 "returns when reconnected" 主语歧义（像显示器自己回来）→ "widgets return when reconnected"；P8 NewWidgets.Offline 与 Cursor 的 pointer 简称不一致，统一 "the mouse pointer"。

**缺失键**：✓ 代码引用的 29 个键全部存在，无缺失。反向发现死键 1 个（见 L2）。

## 五、修复优先级建议

1. **立即修**（发布阻断级）：H1+H2（机器码/占位符上屏——用户第一眼就会看到）、H4（断开收起根本不工作——承诺的功能）、H3（撤销数据丢失风险）。
2. **尽快修**：H5、M1、M2、M4、M5。
3. **打磨批**：M3/M6/M7/M8 + L 系 + 文案 P1-P8（可与 P 系一次 12 语言脚本批处理）。
