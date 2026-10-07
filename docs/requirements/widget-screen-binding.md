# 格子屏幕归属与显示器预览（2026-10-04 已实现，待真机验收）

> **状态**：批1-3 已全部落地（2026-10-04，未提交）：绑定模型+稳定 ID 解析链+右键菜单、设置页「显示器与格子」分区（预览画布/识别/批量操作）、12 语言、单测+契约同步，4784/4784 绿。
> **对标**：Windows 显示设置的"设为主显示器"开关 + 显示器排列预览。
> **实现索引**：`Services/WidgetScreenCatalog.cs`（屏幕目录）、`Services/WidgetScreenMenuBuilder.cs`（右键菜单）、`Views/SettingsSections/DisplaySettingsSection.xaml(.cs)`（设置页）、`Views/DisplayIdentifyOverlayWindow.cs`（识别闪屏）、解析链改造在 `Services/WidgetPositioningService.cs`（SelectWorkAreaCore）。

## R0. 已确认结论

- D1 开关粒度：**双层**——全局"格子主屏幕"默认（`WidgetDefaultBoundScreenId`，管新格子首放+批量固定入口）+ 每格子三档绑定（自动/跟随主屏/固定到屏）。
- D2 预览视觉：**Windows 设置风**——等比排列显示器卡片、编号徽标、主屏徽标、分辨率+缩放标注、选中强调色描边、默认屏角标。
- D3 识别按钮：**做**。每块物理屏闪约 2.5 秒大号编号（不透明纯色 overlay，规避透明顶层窗口渲染黑的坑）。
- D4 入口：**双入口**——格子右键菜单「所属屏幕」二级菜单（RadioMenuFlyoutItem 单选组，单屏时隐藏，null 必须条件添加否则整个右键菜单崩）+ 设置页三级页：常规 → 「显示器与格子」钻取行（新手引导上方）→ Displays 分区（2026-10-04 Simon 定案：不做顶层导航项）。
- 分区 XAML 资源红线（LoadContent 期间元素未进树）：**直接标记禁用 ThemeResource 画刷与无先例的 StaticResource 键**（ControlCornerRadius/CaptionTextBlockStyle 会解析失败）；主题画刷一律进树后（Loaded）用 `Application.Current.Resources` 赋值，且用 TryGetValue+回退——构造函数里硬索引同样会炸掉整个分区创建。
- 样式与交互遵循原生 WinUI 规范：RadioMenuFlyoutItem、SettingsCard、ThemeResource 主题资源、InfoBar 状态反馈、E7F4 TVMonitor 图标、Fluent 渐变导航图标。
- 语义补充定案：**绑定与拓扑档案正交**——档案激活只改位置、永不改绑定（ApplyToWidget/ApplyToGroup 不回写）；拖拽/捕获不改变绑定模式；组表面绑定存于组配置并镜像到成员；拆组继承绑定；屏不在时落入现有兜底链且绑定保留、屏回来即回位。

## D. 决策记录（2026-10-04 Simon 拍板：全部按建议执行，样式/交互要求原生标准，可慢慢做）

| # | 决策 | 结论 |
|---|---|---|
| D1 | 开关粒度 | 双层：全局"格子主屏幕"默认 + 每格子可单独改绑（已实现） |
| D2 | 预览视觉 | Windows 设置风（已实现） |
| D3 | "识别"闪屏按钮 | 要（已实现） |
| D4 | 入口 | 双入口：右键菜单 + 设置页（已实现） |

## R1. 概念与数据模型

镜像 Windows 的心智：Windows 里选中一块屏点"设为主显示器"；DeskBox 里选中一块屏点"设为格子主屏幕"。

```text
WidgetConfig 新增：
  ScreenBindingMode : Unbound(默认) | FollowPrimary | Pinned
  BoundScreenId     : string  // 稳定显示器 ID（EnumDisplayDevices DeviceId，
                               //  与 WidgetTopologyLayoutService 拓扑 key 同源）

全局设置新增：
  WidgetScreenDefaults : { Mode: Unbound|FollowPrimary|Pinned, ScreenId }
  // 只约束：新格子初始归属、用户显式点"把所有格子固定到此屏"时的批量目标
```

- `Unbound`：维持现状链路（存量配置零迁移、行为零变化）。
- `FollowPrimary`：现有隐式 `ShouldFollowPrimaryMonitor` 的显式化版本。
- `Pinned`：固定到指定物理屏，按稳定 ID 匹配；屏不在时落入现有兜底链（拓扑迁移/Nearest/EnsureVisible），**绑定保留**，屏重新接入即自动回位——这是反馈224"固定在某一个显示桌面"的正解。

## R2. 解析链（含 P1 修复，本批次必做）

`WidgetPositioningService.SelectWorkAreaCore` 匹配链调整：

```text
1. ScreenBindingMode == Pinned      → BoundScreenId 稳定 ID 匹配（新增，最高优先）
2. ScreenBindingMode == FollowPrimary→ 当前主屏 work area
3. Unbound（现状链，插入稳定 ID 层）：
   a. 稳定 ID（DeviceId）匹配     ← P1 修复：新增
   b. PositionMonitorDeviceName     ← 现第一优先，降级（\\.\DISPLAYn 序号锁屏重枚举会互换）
   c. work-area 签名 key
   d. 同尺寸最近几何
   e. GetFromPoint(Nearest) + EnsureVisible
```

稳定 ID 采集复用 `WidgetTopologyLayoutService.cs:861-906` 的既有机制；单格子的显示器身份持久化增加稳定 ID 字段（与拓扑档案共用采集器）。

**修复覆盖**：反馈224（锁屏跳屏）、62/85/102（模式切换/接拔漂移）、#61/#9 的"位置不一致"族。

## R3. 交互面

### R3.1 右键菜单（快捷改绑，无预览）

```text
所属屏幕 ▸   ● 自动（维持现状）
             ○ 跟随 Windows 主屏
             ○ 屏幕 1 · 2560×1440 · 主
             ○ 屏幕 2 · 1920×1200
             ○ 屏幕 3 · 1920×1200
```

当前绑定打勾；选项按 EnumDisplayDevices 枚举序列编号（与诊断包 displays.number 同源，保证与预览一致）。

### R3.2 设置页「显示器与格子」分区

```text
┌─ 显示器与格子 ──────────────────────────────────────┐
│  ┌────预览画布（等比缩放物理布局）────┐              │
│  │   ┌────┐      ┌──────┐           │   [识别]      │
│  │   │ 2  │      │ 1 主 │           │               │
│  │   │ ▣▣ │      │  ▣   │           │  屏幕 1        │
│  │   └────┘      └──────┘           │  2560×1440    │
│  │        ┌────┐                    │  缩放 100%     │
│  │        │ 3  │                    │  [设为格子主屏幕]│
│  │        └────┘                    │  [所有格子固定到此屏]│
│  └──────────────────────────────────┘               │
│  格子列表                                            │
│  ▣ 文件·工作      屏幕 1       [更改 ▾]             │
│  ▣ 待办           跟随主屏     [更改 ▾]             │
│  ▣ 音乐           自动         [更改 ▾]             │
└─────────────────────────────────────────────────────┘
```

预览画布规则：

- 显示器卡片按虚拟桌面并集等比缩放摆放；卡片含编号徽标、主屏"主"徽标、分辨率+缩放标注。
- 已绑定格子在其所属屏卡片上渲染为小色块（按持久化 bounds 相对位置缩放），按格子类型着色，hover 显示名称；`Unbound` 格子按当前实际所在屏渲染、半透明。
- 点击屏卡片 → 右侧面板展示信息 + 「设为格子主屏幕」（写全局默认，Windows 同款心智）+「所有格子固定到此屏」（批量改绑）。
- 点击格子色块 → 选中该格子，右侧出归属三选项。
- 「识别」按钮：每块物理屏闪 2-3 秒大号编号（顶层无边框 overlay 窗口，复用现有 overlay 能力）。
- 断开的屏：绑定不存在屏幕的格子归入"离线屏"灰卡区域，标注"屏重新接入后自动回位"。
- 复制模式：单卡片 + "复制模式：所有屏幕显示相同内容"提示。
- 拖拽改绑（把格子色块拖到另一块屏卡上）：二期增强，一期不做。

### R3.3 唤起联动（不改唤起通路）

绑定只作用于位置解析；热键/托盘/双击唤起仍走逐格子 `RestoreBoundsForCurrentTopology`，解析确定性变强后自然落在绑定屏——反馈395/400/279（唤起在旧屏/错屏）随 R2 一并修复，无需动 `WidgetManager.TrayAnimation`。

## R4. 兼容性

- 存量配置：`ScreenBindingMode` 缺省 `Unbound`，零迁移、行为零变化。
- `ShouldFollowPrimaryMonitor` 旧启发式（含"原点近 (0,0)"判定）保留在 `Unbound` 链内部，不静默迁移为 `FollowPrimary`，减少行为变量。
- 拓扑档案（WidgetTopologyLayoutService）与绑定正交共存：绑定决定"锚定哪块屏"，档案决定"该拓扑下布局长什么样"。

## R5. 测试与 AOT 红线

- `WidgetPositioningService` 绑定解析单测：Pinned 命中/屏缺失回退/屏恢复回位；FollowPrimary 主屏切换；Unbound 链回归。
- 拓扑变化契约测试：绑定格子经历 DISPLAYn 序号互换后仍解析到原物理屏（反馈224 场景回归锁）。
- 设置页 UI：12 语言 strings 同步；`SettingsWindow.xaml` 新 x:Name 必须补 `SectionElements.cs` 属性（CS0103 是唯一真错，XamlCompiler 红字是连锁噪音）；AOT 契约冻结计数（x:Bind/SettingsCard）同步更新。
- 预览画布：不使用 `IReadOnlyList<T> => [...]` 集合表达式直接 x:Bind（AOT 剪裁坑）；ItemsPanelTemplate 保持条件赋值惯例。
- 「识别」overlay 窗口注意多屏 DPI 下字体物理尺寸换算。

## R6. 批次与工作量

| 批 | 内容 | 估时 |
|---|---|---|
| 批1 | 绑定模型 + R2 解析链（含 P1 稳定 ID）+ 右键菜单 | ~1 天 |
| 批2 | 设置页预览画布 + 识别 + 批量操作 + 12 语言 | ~2-3 天 |
| 批3 | 测试 + 契约冻结计数 + 文档 | ~0.5-1 天 |

## R7. 非目标（本线不覆盖）

- 收起跨界 160px 动画取舍（审计 P3，独立拍板）。
- DPI 字体/尺寸观感（审计 P5，归外观线）。
- 布局档案管理 UI 的完整形态（本设计的预览画布是其第一步）。
- "唤起跟随鼠标屏"策略（SummonFollowsCursor，二期独立开关）。

## R8. 预览区与交互优化方案（2026-10-04 第二版，已实现待真机验收）

> 起因：功能验收通过（截图确认批量固定 12 格子生效），但预览区样式被 Simon 判"粗糙、没有 Win 的感觉"、操作"比较奇怪"。调研+对截图逐条分析后形成方案；三点决策（删色块/单屏隐藏预览/固定系统蓝选中）按推荐定案，当日重做完成（4784/4784 绿）。**其中"固定系统蓝选中"其后在第三轮返工中被推翻，终案=跟随用户 accent，见 R8.7。**
>
> **实现落点**（全部在 `Views/SettingsSections/DisplaySettingsSection.xaml(.cs)`）：预览=无外框 Viewbox（设计尺寸=物理像素并集，Uniform 缩放零手算）+ 默认样式 Button 卡片（不覆盖 Background，选中样式按 **R8.7 accent 终案**在 code-behind 应用），卡内=大号居中编号+主屏底部小字（Opacity 0.6）+"默认"右上角标，打开页面默认选中主屏；格子色块/坐标映射/格子详情页/RadioButtons/返回按钮全删，单屏保留紧凑预览；选中屏 SettingsCard（屏信息+设为默认[Accent]/全部固定+**清除默认**两态）；格子列表=每面一行动态 SettingsCard+**行内 ComboBox「显示在」**一步改绑，组行显示组绑定经代表成员应用，离线 pin 在下拉中显示禁用项不静默回落；PreviewHint 文案 12 语言更新为"选择一个屏幕以查看它的设置"。

### R8.1 现状问题清单（对照截图）

1. **颜色撞色**：屏幕卡片选中描边/编号徽标直接用系统强调色——Simon 机器 accent=红色，整片红；Windows 设置的显示器选中态是**固定系统蓝**（浅色 #0067C0 / 深色 #4CC2FF），不跟用户 accent。**（该判断已被 R8.7 终案推翻：最终保留跟随用户 accent。）**
2. **卡片信息排布不像 Win11**：小徽标置顶 + 卡内塞两行小字（分辨率+主屏+缩放）。Win11 显示设置是"**浅灰圆角矩形 + 大号编号居中 + 卡内无其他文字**"，分辨率等信息只在选中后的详情区出现。
3. **格子色块价值为负**：按持久化坐标映射画色块，真实用户格子成堆/成组 → 色块随机重叠、半侧空旷，既不精确也不好看。
4. **预览区带外框容器**：我加了带边框的盒子包住画布；Win11 的排列区直接铺在页面背景上，无外框。
5. **提示/徽标字级混乱**：底部提示行、默认角标、主屏小字各自为政；卡片间距偏密。
6. **交互两级跳转**：点屏幕卡→下方详情；点色块→格子详情替换列表 + 返回按钮——"操作奇怪"的直接来源。

### R8.2 调研结论：无现成原生组件

WinUI 3 / Windows App SDK **没有**显示排列（display arrangement）控件；Windows 设置该页用的是 SystemSettings 内部组件。社区复刻一致做法 = Canvas/ItemsControl 自绘 + 显示 API 取数。**结论：排列区必须自绘，但全部用"原生积木 + 默认样式"拼装，不自调画刷**（也根治本批四轮资源解析坑）。

### R8.3 组件选型表

| 场景 | 现状 | 改用 | 理由 |
|---|---|---|---|
| 屏幕卡片 | Button + 手刷调色板 | **默认样式 Button，不覆盖 Background/BorderBrush**；选中样式按 **R8.7 accent 终案**（整卡 accent 填充+对比度前景，code-behind 从实时系统 accent 应用） | 默认样式自带 Fluent hover/pressed/圆角/明暗主题，零手刷零踩坑 |
| 整体缩放 | 手动 scale 数学 | **Viewbox（Stretch=Uniform）** 包固定设计尺寸的排列 | 原生等比缩放，DPI/窗口宽度自适应免算 |
| 编号 | accent 底小徽标 | **卡内大号居中数字**（约卡高 0.35、SemiBold、TextFillColorPrimary） | Win11 同款；TextFillColor* 键已验证可达 |
| 主屏标识 | 卡内小字 | 卡内底部小字"主屏"（居中、TextFillColorSecondary） | Win11 同款位置 |
| 默认屏标识 | 右下角标 | 右上 **InfoBadge**（WinUI 原生徽标控件，NavigationView 同款）或"默认"细角标 | 原生徽标语义 |
| 格子位置示意 | 坐标映射色块 | **方案一：删除色块**；方案二：每屏卡内底部一行"格子条"（类型着色小矩形横排、溢出 +N，不做坐标映射永不重叠） | 见 R8.5 待拍板 |
| 屏幕操作 | 详情面板两按钮 | 选中屏后预览下方一张 SettingsCard：屏信息 + 「设为默认」（AccentButtonStyle 主按钮）+「全部固定到此屏」（次级） | Win11"选中→下方设置"流；主次按钮分层 |
| 每格子改绑 | 格子详情页 RadioButtons + 返回 | **格子列表每行内联 ComboBox（"显示在"）** | Windows 设置行内下拉惯例；一步改绑、零跳转、列表常驻 |
| 识别 | 实心按钮 | ~~预览区右上文字样式按钮~~ **实作为总览卡内的默认样式按钮**（实现简化，未做 Win11 文字链接式；如需对齐再返工） | 同款 |
| 状态反馈 | InfoBar | 保留 InfoBar（原生，Success 绿为系统行为） | — |
| 页面说明 | 底部提示行 | 预览区上方一行说明文字（"选择一个屏幕以查看和更改格子归属"式） | Win11 同款文案位 |

### R8.4 视觉规格（Win11 对齐）

- 卡片：中性浅灰填充（默认 Button Background 或 #E9E9E9/#1C1C1C 字面）、CornerRadius 8、卡内仅大号编号 + 主屏小字
- 选中：~~2px 系统蓝描边（#0067C0/#4CC2FF，固定值不跟 accent）~~ **按 R8.7 终案：整卡跟随用户 accent 填充（浅色取 Accent、深色取 AccentLight1），前景按亮度对比取色，hover/pressed 在 accent 族内混色**；未选中保持默认 Fluent 样式（无专门描边）
- 排列：Viewbox 居中；页面背景直铺无外框；上说明行、识别按钮（默认样式）
- 间距：卡片间距 8px、排列区上下留白 24px；格子列表行距沿用分区卡片节奏

### R8.5 决策记录（2026-10-04 Simon 拍板）

1. **格子示意**：✅ 方案一，删除色块。
2. **单屏时预览区**：✅ 初判隐藏 → **真机体验后推翻（同日）**：单屏保留紧凑预览（Viewbox MaxHeight 200 vs 多屏 280），与 Windows "单屏也画排列"一致；单屏同样默认选中主屏，设置卡照常可用。
3. 选中态描边：✅ 初判固定系统蓝，不跟 accent → **后续推翻：跟随用户 accent（R8.7 终案）**。
4. **徽标样式（真机反馈补定）**：主屏/默认标签初版过小且位置不规范 → 改为 Win10/11 同款**卡内底部徽标行**："主屏"=系统蓝实底圆角胶囊+白字（Win10 主显示器徽标先例）、"默认"=同色描边空心胶囊，两者并排居中；字号按排列并集高度取值（union×0.075，clamp 18-72 设计像素），保证紧凑/全尺寸两种预览下渲染字号稳定。**（配色其后随 R8.7 终案调整为 accent 族实底+对比度文字，形态从胶囊改为圆角矩形。）**

### R8.7 第三轮返工记录（2026-10-04，终案）

预览区选中态最终**跟随用户系统 accent**，不再使用固定系统蓝（初判因"Simon 机器 accent=红色撞色"而回避 accent，其后拍板接受 accent 为终案——撞色问题改由亮度对比的前景色解决，而不是绕开 accent）。终案口径：

- 选中卡：整卡 accent 填充——浅色主题取 `Accent`、深色主题取 `AccentLight1`；前景文字按背景亮度对比取色；hover/pressed 在 accent 族内 Blend 混色。实现见 `DisplaySettingsSection.xaml.cs` 的 `ResolveSelectionFillColor`/`ApplySelectionFill`（约 :200-231、:303-319），画刷全部 code-behind 从 `Application.Current.Resources` 取值（延迟创建分区 ThemeResource 红线）。
- 未选中卡：保持默认 Fluent 样式，无专门描边。
- 徽标：accent 族实底（`AccentLight2`）+ 对比度文字，圆角矩形（非胶囊）；选中卡上徽标前景反相。
- 单屏保留紧凑预览（与 R8.5.2 一致）；"设为默认"按钮两态（已是默认时可清除默认，2026-10-04 补）。
- 识别按钮：总览卡内默认样式按钮（未做 Win11 文字链接式，见 R8.3 表备注）。

### R8.6 已核实非问题

- 截图疑似"导航项重复"：代码侧核实 NavigationView 恰 8 项、Tag 唯一，无重复（识别误差或截图压缩伪影）；若真机仍见重复再报我。
- InfoBar Success 绿为系统原生样式，不改。

### R8.7 工作量

纯 UI 重做（预览区 + 列表行 + 详情卡），不动解析链/绑定/拓扑逻辑：**约 1 天**。
