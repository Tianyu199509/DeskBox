# 多屏显隐作用范围 + 每屏显隐动画 设计定稿

- 日期：2026-10-10
- 状态：**设计定稿，未实施**（四路 agent 调研 + 两轮交互斟酌 + Simon 拍板完毕，等开工指令）
- 关联：`multiscreen-behavior-spec.md`（v2）——**实施时须同步修订其第 10 节**（"唤起跟随鼠标所在屏"现为非目标条款，本设计推翻该决策）
- 决策人：Simon

## 1. 背景与目标

多屏用户希望：
1. 快捷键显隐格子时可选"所有显示器一起"（现状）或"仅鼠标所在的显示器"。
2. 不同显示器可配置不同的显隐动画（效果、方向先行）。

## 2. 已拍板的决策（8 项）

| # | 决策 | 内容 |
|---|---|---|
| 1 | 作用域入口范围 | 开关只覆盖**快捷键 + 桌面空白双击**；**托盘图标始终全量**（混合态点托盘=全部显示，再点=全部隐藏） |
| 2 | 并行 | A 屏动画进行中按 B 屏热键**并行执行**，两屏动画互不打断（不接受排队丢弃） |
| 3 | 每屏动画字段 | v1 只放开**效果 + 滑动方向**；速度/缓动/交错留 v2 |
| 4 | 胶囊口径 | 分屏显隐**连胶囊一起**（胶囊随所在屏显隐） |
| 5 | 重启语义 | 分屏隐藏**不跨重启**（与现状一致，重启全可见） |
| 6 | 功能 A 标题 | **快捷键与双击的作用范围** |
| 7 | 功能 B 卡片名 | 直接复用现有词条"**显示与隐藏动画**"（`Settings.Group.Animation.Title`，不加屏号后缀） |
| 8 | 托盘 tooltip | 做**三态**：已全部唤起 / **部分显示器已唤起** / 已隐藏 |

## 3. 功能 A：快捷键与双击的作用范围

### 3.1 设置 UI

- 位置：设置 → 常规 → 显示器与格子 → "多显示器" Expander 内（断开行为卡下方新增一张卡）。
- 控件：ComboBox 两档 + 右侧 ？ InfoTip。
- **单屏时该卡隐藏**（注意："多显示器" Expander 本体单屏时是可见的，`DisconnectBehaviorCombo` 无门控——新卡自带 `Visibility` 门控，不动 Expander）。
- 持久化：`WidgetLayoutSettingsSlice`（widget-layout.json，设备域），键风格仿 `WidgetNewPlacementTarget` 常量对。默认值=所有显示器一起（存量行为不变）。

### 3.2 文案（中文定稿，12 语言同步）

- 标题：`快捷键与双击的作用范围`
- 描述：`按快捷键或双击桌面空白处时，控制哪些显示器上的格子显示或隐藏。`
- 选项 1（默认）：`所有显示器一起`
- 选项 2：`鼠标所在的显示器`（与同页"新格子出现在"选项词汇严格一致）
- InfoTip 内容：
  - 每块屏幕各自记忆自己的显示/隐藏状态，可以一块显示、一块隐藏
  - 托盘图标不受影响：始终控制全部显示器（混合态时点击=全部显示，再点=全部隐藏）
  - 胶囊随所在屏幕一起显示或隐藏

### 3.3 入口语义表（实施依据）

| 入口 | 分屏模式下行为 | 依据 |
|---|---|---|
| 全局热键 | 仅鼠标屏（需在钩子/回调处取光标屏后入队） | 拍板 1 |
| 桌面空白双击 | 仅双击所在屏（坐标天然可得） | 拍板 1 |
| 托盘图标左键 | 全量；混合态=全部显示→再点全部隐藏 | 拍板 1 |
| Jump List / 搜索 action-command / Onboarding / 通知激活 / 单实例裸激活 / 层模式切换 / 静默启动 | 全量（不在覆盖列表） | 拍板 1 |
| Quick Reveal 失焦收起 | 只收"被唤起的屏"（flyout 语义保留、对象收窄） | 调研拍板 3 |
| 目标屏判定口径 | 表面**实际所在屏**（最大交集面积，spec 5.4 自实现），不用 BoundScreenId | 调研拍板 11 |
| 重启 | 全部可见（不持久化分屏隐藏） | 拍板 5 |

## 4. 功能 B：每屏"显示与隐藏动画"

### 4.1 设置 UI

- 位置：显示器与格子页，**选中屏详情卡下方**，跟随排列预览选中的屏联动（Header 值实时显示生效摘要）。
- 形态：`SettingsExpander`（复用词条"显示与隐藏动画"）：
  - 折叠态值：`跟随全局（右滑淡入）` / `自定义 · 上滑淡入`（括号内全局值实时取）
  - 展开内容：`使用自定义动画` ToggleSwitch + `效果` ComboBox + `滑动方向` ComboBox + `修改全局动画 →` 深链（跳外观动画页）
  - 开关关闭即删除 override 条目（数据回落全局），ComboBox 置灰并回显全局值
- 联动门控复用外观页逻辑：无位移效果（如淡入淡出）时方向 ComboBox 置灰。
- **单屏时整卡隐藏**（与 ScreenDetailCard 同 `Count > 1` 门控）。
- 离线屏不可编辑，override 静默保留、重插回自动生效（与 BoundScreenId 同生命周期哲学）。

### 4.2 数据结构

```jsonc
// widget-layout.json（设备域，不进云同步投影）
"DisplayAnimationOverrides": {          // 键控字典，normalize 封顶（建议 8 条）
  "<显示器StableId>": {                  // Win32Helper.ResolveStableMonitorId（PnP 设备接口 id）
    "Effect": "SlideFade",               // 全部可空；null=跟随全局
    "SlideDirection": "Up"
    // v2 扩展位：Speed / EasingIntensity / Stagger（结构已留，UI v1 不放）
  }
}
```

- 标识键：复用 StableId 体系；退化 id（`\\.\DISPLAYn` / `unknown-display`）永不参与匹配（`WidgetScreenCatalog.cs:98-116` 现成规则）；"跟随全局"=删除条目，字典天然不膨胀。
- 运行时解析顺序：窗口 → 自己屏 StableId → override 字典 → 回落全局（settings.json 的 `WidgetShell.WidgetAnimation*`）。

### 4.3 执行侧改造

- **v1（效果+方向）：执行侧零改造**——窗口级 profile 生成点（`WidgetWindowBase.Interaction.cs:848` `GetTrayAnimationProfile`）按窗口所在屏解析 override；`ApplyTrayAnimationGroupOffset`（`WidgetManager.TrayAnimation.cs:436-443`）组循环内按组取效果/方向。
- **v2（速度+缓动+交错）**：`WidgetTrayBatchAnimationDriver` 把 duration/easing 下沉 entry 级（约 80-150 行，单时钟单事务不变量保持；`WidgetTrayBatchAnimationEntry` 加字段 + 插值改 entry 级值 + `totalMs = max(entry)`）。约束：`isShowing` 必须全批一致（操作语义）；窗口 profile duration 与 driver durationMs 两处读取必须同源。

## 5. 执行侧核心改造（功能 A 批次，五个 P0）

按屏显隐要把以下单例机制按屏化：

1. **`_widgetsRaisedFromTray` 单值位**（约 30 个消费点）升级为按屏会话集合（每屏 `{Raised, Generation, ForegroundAtRaise, SuppressUntilUtc}`）。消费点分三类：决策/组显示分支/窗口自救 → 按窗口所在屏查询；托盘 tooltip/诊断 → 聚合三态（决策 8）；pinned 床垫 → 保持全局。
2. **恢复监视器三件套**（`_trayLayerRestoreTimer` / `_trayRaiseBatchGeneration` / `_foregroundAtRaiseTime`）按屏实例化，B 屏 raise 不再作废 A 屏的 topmost 确认与回落信号。
3. **请求队列奇偶折叠修复**（`TrayToggleRequestQueue.cs:140-167`）：分屏后 A+B toggle 同批会被 `(length & 1)` 折叠成 no-op——折叠逻辑必须按屏分列。
4. **busy 闸按屏 + 取消按屏**：`_trayVisibilityOperationGate` / `_isTogglingWidgetsDesktopLayer` 按屏 gate（拍板 2 并行的前提）；`CancelActiveTrayAnimationsAndRestorePositions` 只取消目标屏的动画。
5. **Quick Reveal 全量 dismiss/hold 收窄 + 跨屏激活修复**：`QueueQuickRevealDismiss` 只收唤起屏；`ActivateIdleHighestWindow` 只在目标屏的窗口里取 idle-highest；`HoldGroupTopMostWithoutActivation` 对象集按屏。

其他确认安全项（只需对象集收窄）：拓扑重排/拔屏迁移、Z 序 peer 排序（已按屏分组）、组驻留缓存、显示桌面联动、整理流程、内存清理。胶囊跟随所在屏（拍板 4）。

## 6. 实施切分

| 批次 | 内容 | 风险 |
|---|---|---|
| B v1 | 每屏动画（效果+方向）：数据结构 + 显示器页 UI + 解析注入 | 低（执行侧零改造） |
| A | 作用域开关 + 按屏会话五个 P0 改造 + tooltip 三态 + spec 第 10 节修订 | 高（核心显隐链路） |
| B v2 | 速度/缓动 entry 级下沉（若 A 已做并行则顺手） | 中 |

## 7. 验证面

- 真机矩阵补充：双屏分别 toggle、A raised 时 B 屏点击外部、A 动画中 B toggle（并行）、quick-reveal 分屏 dismiss、pinned 分屏、拔屏宽限中分屏唤起（A 屏格子被迁移的感知场景）、胶囊随屏显隐。
- 契约测试同步：`SettingsSliceOwnershipContractTests`（`DisplaySettingsSection.xaml.cs=14` 门面计数）、`SettingsSearchCatalog`（脚本重新生成）、12 语言、AotDeepSmoke section 标签、`Windows10WidgetMotionContractTests` 源码字符串断言（B v2 时）。
- 日志补 `monitor=` 维度（对齐 spec 9.4）。
