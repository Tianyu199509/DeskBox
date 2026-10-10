# 05 · 多语言全量走查（12 语言，含全部子窗口/弹窗，2026-10-01）

> 方法：node 脚本键集/占位符逐键比对 + 全部 `T()/svc:Localized.*` 引用反向核对（2200+ 处）+ 21 类用户可见表面逐一判定 + 术语计数走查。关键结论已主会话二次复核。

## 1. 机械一致性：完美

- 12 文件均为 **2677 键**；键集合逐语言与 en-US **完全一致**；文件内重复键 0；空值 0；占位符 `{n}` 逐键比对 **0 漂移**。
- `LocalizationResourceContractTests` 实际严格度：键集合相等✓、值非空白✓、占位符**去重集合**相等✓。两个盲点：(a) 不查占位符顺序与次数（`{0} {0}` 与 `{0}` 等价通过）；(b) **不检测同文件重复键**（反序列化后键覆盖前键、静默）。当前实测无重复，但测试不设防。
- 缺失键（代码引用但 JSON 没有）= **0**。动态拼装键（前缀+枚举，17 个族）全部组合齐全。

## 2. 死键：445 / 2677（16.6%）★复核抽查实锤

JSON 有、全仓（src/tests，含 12 语言）零引用。按族：

| 数量 | 键族 | 来源 |
|---|---|---|
| 55 | `Settings.Dialog.Migrate*` 全套（已抽查 `Settings.Dialog.MigrateTitle`＝0 引用实锤） | 迁移对话框被 SafeMigration* 取代 |
| 36 | `Settings.Search.*` 旧搜索设置 | 搜索设置重构 |
| 25 | `DesktopOrganization.Preview.*` | 整理预览重写 |
| 38 | `Settings.Todo.*` + `Settings.Capsule.*` 各 19 | 设置分区重构 |
| 18 | `Settings.QuickCapture.*` | 同上 |
| 12 | `Settings.Group.*` 旧分组头；12 | 动画旧效果值（None/Slide*/ScaleSlide——新 UI 只用 7 值） |
| 11+11 | `Search.Tab.*`；`Settings.WidgetGroups.*` | 搜索弹窗/格子组重写 |
| ~44 | `Widget.*` 各单键族 | 文件格子文案重写 |
| 5+5 | `Tray.TooltipNormal/ShowAll/...`（现用 Tooltip/TooltipRaised）；`Glance.*` | 托盘精简/一瞥改版 |
| 10 | `Settings.Weather.Show*.Description` 等 | 天气改版 |
| 其余 | `Language.Chinese`（硬编码简体中文绕过）、`Window.{Onboarding,Widget}.Title`、`Settings.Title` 等单点 | 各时期重命名 |

影响：不损运行，但 12×445 行冗余持续污染翻译交付与搜索目录生成。**建议整族删除**（删除时注意 LocalizationResourceContractTests 只要求一致，删键安全）。

## 3. 未翻译（P1）：SafeMigration 迁移对话框 46 键 × 9 语言全英文 ★复核实锤

- `Settings.Dialog.SafeMigration*` 46 键：de-DE、ja-JP 等 9 个非中文语言 **46/46 与 en-US 逐字相同**（已 node 复核：de/ja identical-to-en=46，zh-CN=0 全译）。
- 这是收纳路径迁移（文件搬移）的确认/进度/错误对话框——数据安全流程的文案，德法日等用户全程看英文。
- 同样 9–10 语言未译：`Settings.Performance.IdleWorkingSetTrim.*`、`Settings.Feature.{ListTextSize,ContentTextSize}.*`。
- 整体未译率（与 en-US 同值占比）：de 6.2% > fr 5.5% > pt 4.8% > es 3.9% > 其余 ~3%；zh 1.1%（多为 F7/Ctrl 等语言无关串）。

## 4. 硬编码未本地化：纪律极高，4 个真实问题点

全仓 `(Text|Content|Header|Title|...) = "字面量"` 0 命中；XAML 字面 Text 仅 14 处（emoji/F7/色值/扩展名示例，有意）。窗口 Title 字面量均为设计期占位，运行时 T() 覆盖。真实问题：

1. **一瞥（Glance）农历节日名硬编码中文**：`Services/GlanceFestivalService.cs:53-81`（春节/清明/端午…10 个），`Localize()` 仅简→繁转换——**非中文用户看到中文节日名**。
2. `MarkdownSourceEditor.xaml.cs:799` 用 `T("Common.More", "More")`——键不在 JSON，全语言回退英文 "More"（非裸键但英文）。
3. 强调色色板 tooltip 为十六进制字面量（`AppearanceSettingsSection.xaml:78-93`，技术值可接受，可选优化）。
4. 数字型拼接（`+{n}`、`X / Y`、`{value}%`）语言无关，可接受。

## 5. 术语与质量

**zh-CN（基准，质量最好）**：widget=格子 326 vs 小组件 3（两处例外应改：`Settings.Performance.IdleWorkingSetTrim.Description`、`Settings.Dialog.AboutMe*` 自述可容忍）；随记 79 vs 随记 0 ✅（一处"速记"应改：`Settings.CloudBackup.QuickCaptureData.Description`）；托盘/快捷键统一 ✅。

**zh-TW（问题最多）**：
- widget **系统性分裂**：小工具 188 次 vs 格子 124 键（不同批次混翻，如"收纳格子↔收納小工具"）→ **需产品定名后批量统一（P1）**；
- 快捷键：快速鍵 31 vs 快捷鍵 6（6 键应统一为"快速鍵"）；
- 托盘：系統匣 3 vs 工作列(通知區域) 3。

**品牌**：bn-BD 61 键、hi-IN 70 键把 DeskBox 转写为当地文字，同语言内两种写法并存，与商店名可能不一致 → 建议统一保留 "DeskBox"。

**zh-CN 标点**：`Onboarding.*` 12 个键用半角标点（如"找不到图标?展开…"，已抽查实锤）——全文件其余 2600+ 键均全角，批次遗漏 → 应改全角。

**占位符误用**：0；乱码/TODO 残留：0；长度异常（>3×en）：17 处（de/fr 的 Onboarding 状态串等，设置卡内无截断风险，P3 观察）。

## 6. 子窗口/弹窗覆盖矩阵（21 类表面）

| 表面 | 机制 | 判定 |
|---|---|---|
| 设置主窗+9 partials（427 处 Localized） | svc:Localized+T() | 完整 |
| 设置分区×6（Appearance/Capsule/DesktopOrg/FileWidget/Glance/Search，151 处） | 同上 | 完整 |
| Onboarding 窗 | Localized 34+T() | 完整（zh-CN 标点问题见 §5） |
| 内容格子宿主/随记宽视图（10 partials）/搜索弹窗（65 处 T()）/发布说明窗/整理桌面窗/整理放弃对话框 | T() | 完整 |
| **收纳迁移对话框**（SafeMigration* 46 键） | T() | 机制完整；**9 语言英文（P1）** |
| 叠放弹层/行内重命名/分离预览窗 | 无 UI 铬文本 | N/A（完整） |
| 托盘菜单+tooltip（7 项+Format）/跳转列表（4 项）/原生通知（待办提醒/更新） | T()/Format() | 完整 |
| 设置内 ContentDialog×14 文件 | T()（含按钮文本） | 完整 |
| 格子右键/胶囊/锁定/前景/收起确认/一瞥菜单（7 个 MenuBuilder） | T()/预本地化 options | 完整 |
| 文件格子内容/叠放 | T()+Localized 5 处 | 完整 |
| 待办/随记/音乐/天气/一瞥内容 | 绑定 T() 派生属性 | 完整（**节日名例外**） |
| 首次引导内容 | T() | 完整且 12 语言已译 |
| Markdown 编辑器/查看器/行内编辑 | T(key,fallback)/绑定 | `Common.More` 英文回退 |
| 商店资源 resw（AppDisplayName/Description） | resw | 完整（契约测试覆盖） |

## 7. 修复优先级清单

**P1（用户可见错误语言/含义）**
1. SafeMigration* 46 键 × 9 语言补译（数据安全流程文案）。
2. zh-TW widget 术语分裂统一（定名"格子"或"小工具"后批量替换）。

**P2（死键/漂移/未翻译）**
3. 删 445 死键（按 §2 表整族删）。
4. zh-CN Onboarding 12 键半角→全角。
5. zh-CN："速记"→"随记"×1、"小组件"→"格子"×1。
6. zh-TW：快捷鍵→快速鍵×6；系統匣 vs 工作列通知區域 统一。
7. IdleWorkingSetTrim / Feature.{List,Content}TextSize 补 9 语言。
8. bn/hi 品牌统一保留 "DeskBox"（61+70 键）。
9. `Common.More` 补入 12 JSON。

**P3（打磨）**
10. Glance 节日名各语言译名或拉丁转写。
11. 长度异常 17 处复核；色板 tooltip 可选本地化。
12. 契约测试补强：占位符有序+计次比对；同文件重复键检测；（配合死键清理）增加引用存在性检查防再生。

---

### 与其他审查文档的交叉引用
- 死键清理会改变 12 个 Strings 文件 → `LocalizationResourceContractTests` 只要求一致性，删键安全；但若同时清理 XAML，注意 `AotStage4D1B` Localized 使用计数与 `update-settings-search-catalog.ps1` 重生成。
- SafeMigration 补译属于文案新增，无冻结计数影响。
