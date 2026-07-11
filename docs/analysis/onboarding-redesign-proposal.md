# DeskBox 新用户引导流程重构方案（v2）

## 一、从零基础用户视角重新思考

### 1.1 用户画像

假设一个完全没接触过 DeskBox 的用户第一次打开这个产品：

- **不知道"格子"是什么**：这个词在 DeskBox 语境下有特定含义，但用户第一次看到没有概念
- **桌面很乱**：这就是他们下载 DeskBox 的原因，但他们不知道怎么用
- **担心文件安全**：万一这个软件把我的文件搞没了怎么办？
- **没有耐心看说明书**：想快点用起来，不想读长篇大论
- **可能在上传文件时找不到文件**：这是收纳机制的最大痛点

### 1.2 用户在引导中需要建立的心智模型

按时间顺序，用户需要在引导过程中逐步建立以下认知：

| 时间线 | 用户需要理解的 | 对应步骤 |
|--------|---------------|----------|
| 0-5 秒 | "哦，这个软件是在桌面上放几个小窗口来整理东西" | Step 1 |
| 5-30 秒 | "拖文件进去会移动到专用文件夹，固定到资源管理器后上传时能找到" | Step 2 |
| 30-50 秒 | "我可以选外观，这是我的风格" | Step 3 |
| 50-80 秒 | "除了文件格子，还能加待办、天气这些工具" | Step 4 |
| 80-100 秒 | "用 F7 或托盘可以随时显示隐藏，不会一直挡着" | Step 5 |
| 100-120 秒 | "好了，桌面上已经有一个空格子等我了，我去试试" | Step 6 |

### 1.3 关键设计决策

基于以上分析，做出以下决策：

1. **功能格子默认全部关闭**：不预设用户需要什么，让用户自己选。文件格子是核心，始终创建
2. **所有设置真实联动**：引导中的每一个开关、选择都直接调用 `ThemeService`、`GlobalHotkeyService`、`ExplorerQuickAccessHelper` 等真实服务，不是假预览
3. **右侧 Scene 完全重写**：不再使用现有的 mini widget 预览 + 卡片堆叠方式，改为每步有针对性的场景设计
4. **左右联动**：用户在左侧的设置操作会实时影响右侧的场景展示
5. **最后一步说明空格子**：明确告诉用户"桌面上已经有一个空的收纳格子等着你了"

---

## 二、新流程结构（6 步 + Logo 动画）

```
Logo 动画
  → Step 1 认识 DeskBox（理念 + 概念可视化）
  → Step 2 文件收纳与快速访问（核心概念 + 路径 + 固定）
  → Step 3 个性化外观（主题 + 主题色 + 材质）
  → Step 4 选择功能格子（4 个 Toggle，默认全关）
  → Step 5 日常使用（快捷键 + 自启）
  → Step 6 准备就绪（配置摘要 + 空格子引导）
```

---

## 三、各步骤详细设计

### Step 0：Logo 动画（保留不动）

三层色块组合动画 + 标题副标题淡入。保持现有实现。

---

### Step 1：认识 DeskBox

**目标**：5 秒内让用户理解"DeskBox = 桌面上的轻量整理层"

#### 左侧文案
- Eyebrow: "欢迎"
- Title: "在桌面上加一层轻量整理空间"
- Body: "DeskBox 不会替代你的 Windows 桌面。它把文件、待办和常用工具放进几个轻量格子里，需要时唤起，不需要时安静待在旁边。"
- Hint 1: "保留 Windows 原有桌面和文件操作方式"
- Hint 2: "格子可以自由拖动、调整大小"

#### 左侧交互
无设置项。2 个 Hint 要点。

#### 右侧 Scene 设计 — 完全重写

**场景概念**：桌面演变动画

**视觉元素**：
1. 一个模拟的桌面区域（圆角矩形背景，代表 Windows 桌面）
2. 桌面上散落 3-4 个杂乱的文件图标（不同类型：文档、图片、快捷方式）
3. 一个 DeskBox 格子从右侧滑入，覆盖在桌面上方

**动画流程**（入场后自动播放一次）：
1. 桌面区域淡入（0ms）
2. 杂乱文件图标依次出现，带有轻微旋转和错位，传达"混乱"感（200ms-800ms）
3. 一个格子窗口从右侧滑入，带柔和的阴影，落在桌面右侧（1000ms-1500ms）
4. 格子停稳后，1-2 个文件图标轻微弹跳向格子方向移动一点距离，暗示"可以拖进去"（1600ms-2200ms）

**设计意图**：用户看到的第一眼就理解"桌面上多了一个窗口，文件可以放进去"

**与现有实现的差异**：不再使用并排的多个 mini widget 预览 + badge 标签，改为一个有故事感的动画场景

---

### Step 2：文件收纳与快速访问

**目标**：讲清楚"文件拖进格子后去哪了"和"怎么找到它"

#### 左侧文案
- Eyebrow: "文件收纳"
- Title: "拖进格子的文件去哪了？"
- Body: "拖入收纳格子的文件会移动到 DeskBox 专用文件夹。把它固定到资源管理器边栏，以后在任何上传窗口都能快速找到。"
- Hint 1: "映射格子也会在专用文件夹下创建快捷方式"
- Hint 2: "路径和固定状态随时可在设置中调整"

#### 左侧交互 — 真实联动

##### 收纳路径
- 显示当前默认路径（如 `C:\Users\用户名\DeskBox`）
- "更改路径"按钮 → 打开文件夹选择器
- 路径变更时调用 `WidgetManager.UpdateDefaultManagedStorageRootAsync()` 执行真实迁移
- 迁移确认对话框复用现有逻辑

##### 固定到资源管理器快速访问
- ToggleSwitch，默认开启
- 开启时调用 `ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(path)`
- 关闭时调用 `ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(path)`
- 操作时显示加载状态
- 检测当前状态：`GetQuickAccessPinStateAsync(path)`

#### 右侧 Scene 设计 — 完全重写

**场景概念**：文件流向链路图（纵向流程）

**视觉元素**（从上到下排列）：

```
┌─────────────────────────────┐
│  ① 桌面区域                   │
│  📄 📷 🔗  (散落的文件)        │
└──────────┬──────────────────┘
           │  ↓ 拖入
┌──────────┴──────────────────┐
│  ② 收纳格子                   │
│  ┌─ 📁 收纳 ──────────┐      │
│  │  (空，等待文件)       │      │
│  └────────────────────┘      │
└──────────┬──────────────────┘
           │  ↓ 移动到
┌──────────┴──────────────────┐
│  ③ DeskBox 专用文件夹         │
│  📁 DeskBox                  │
│   ├ 📄 文档.docx              │
│   ├ 📷 截图.png               │
│   └ 🔗 项目文件夹 (快捷方式)   │
└──────────┬──────────────────┘
           │  ↓ 固定到边栏
┌──────────┴──────────────────┐
│  ④ 资源管理器边栏              │
│  ⭐ 快速访问                   │
│   📁 桌面                     │
│   📁 下载                     │
│   📁 DeskBox  ← 高亮(已固定)  │
└─────────────────────────────┘
```

**动画设计**：

1. **入场动画**：四个区块从上到下依次淡入 + 轻微下滑（类似现有 `PlaySceneEntrance`）
2. **文件流动画**（入场后自动播放一次）：
   - ① 中的一个文件图标（📄）从桌面区域滑出
   - 沿着箭头路径滑入 ② 格子区域
   - 继续滑入 ③ 文件夹区域，出现在文件列表中
   - 整个路径大约 2 秒，文件图标带有强调色拖尾
3. **边栏高亮**（与 Toggle 联动）：
   - 当左侧"固定到快速访问"Toggle 为 ON 时：④ 中的 "DeskBox" 项高亮显示（强调色背景 + 星标图标）
   - 当 Toggle 为 OFF 时：该项灰显或消失
   - **用户切换 Toggle 时，右侧实时响应**

**左右联动**：
- 用户切换"固定到快速访问"→ ④ 中的边栏项高亮/灰显变化
- 用户更改路径 → ③ 中的文件夹路径文字更新

**设计意图**：用户一眼看懂"文件从桌面 → 格子 → 文件夹 → 资源管理器边栏"的完整链路

---

### Step 3：个性化外观

**目标**：让用户选择主题、主题色、材质，实时预览

#### 左侧文案
- Eyebrow: "个性化"
- Title: "选择你喜欢的外观"
- Body: "格子会一直待在你的桌面上，选一个你喜欢的样子。以后可以随时在设置中调整。"

#### 左侧交互 — 真实联动

##### 主题选择
- 3 个卡片式选项：跟随系统 / 浅色 / 深色
- 选中时调用 `ThemeService.SetTheme("System" | "Light" | "Dark")`
- 引导窗口本身被 `ThemeService.TrackWindow()` 注册，主题切换会实时应用到整个引导窗口

##### 主题色选择
- 预设色板：8-10 个精选颜色圆点
- "自定义"按钮 → 打开颜色选择器
- 选中时调用 `ThemeService.SetCustomAccentColor(color)` 或 `ThemeService.SetAccentMode("System")`
- 强调色变化通过 `AppearanceChanged` 事件传播到整个窗口和右侧预览

##### 材质选择
- 3 个选项：Mica / 亚克力 / 纯色
- 选中时直接写入 `_settingsService.Settings.WidgetMaterialType` 并 `SaveDebounced()`
- 右侧预览区域反映材质变化

#### 右侧 Scene 设计 — 完全重写

**场景概念**：实时预览面板（不是缩略图，而是一个"准真实"的格子预览）

**视觉元素**：

一个迷你的格子窗口预览，包含：
1. 标题栏：图标（强调色）+ "我的文件" 标题 + 悬停按钮区域
2. 内容区：2-3 个文件项（图标 + 文件名），其中一项被选中（强调色高亮）
3. 背景材质效果：根据材质选择呈现不同的背景质感

**动画与联动设计**：

- **主题联动**：用户切换主题 → 整个引导窗口（包括右侧预览）的亮/暗模式实时切换。预览中的文字颜色、背景色、边框色全部跟随变化
- **强调色联动**：用户切换主题色 → 预览中的标题栏图标、选中项高亮、悬停按钮颜色全部实时变化。同时引导窗口中的强调色元素（进度点、按钮）也同步变化
- **材质联动**：用户切换材质 → 预览的背景效果变化：
  - Mica：呈现淡淡的桌面壁纸透出效果
  - Acrylic：呈现磨砂模糊效果
  - 纯色：呈现不透明的卡片背景色
- **入场动画**：预览面板从右侧滑入 + 淡入
- **微交互**：鼠标悬停在预览上时，悬停按钮区域淡入显示（模拟真实格子的 hover 行为）

**技术实现要点**：
- 预览面板不能直接用真实 WidgetWindow（太重），而是用 XAML 构建的模拟组件
- 颜色绑定到 `ThemeService.GetEffectiveAccentColor()` 和当前主题状态
- 材质效果用不同的 `Brush` 模拟（不需要真实的 Mica/Acrylic backdrop）
- 监听 `ThemeService.AppearanceChanged` 事件刷新预览

---

### Step 4：选择功能格子

**目标**：让用户选择开启哪些功能格子，引导完成后自动创建

#### 左侧文案
- Eyebrow: "功能格子"
- Title: "把常用的工具放在桌面"
- Body: "除了文件格子，你还可以开启这些轻量功能格子。选好后会自动创建到桌面上。"

#### 左侧交互 — 真实联动

4 张功能格子卡片，每张包含：图标 + 名称 + 一句话描述 + ToggleSwitch

| 格子 | 图标 | 描述 | 默认状态 |
|------|------|------|----------|
| **待办** | ✅ | 轻任务管理，支持截止日期和提醒 | **关闭** |
| **随记** | 📋 | 快速记录文本、链接，可自动捕获剪贴板 | **关闭** |
| **音乐** | 🎵 | 控制媒体播放，频谱动效和封面取色 | **关闭** |
| **天气** | 🌤️ | 实时天气和 7 天预报，自动定位 | **关闭** |

- 每张卡片的 Toggle 切换时调用 `FeatureWidgetSettings.SetEnabled(settings, kind, isOn)` + `SaveDebounced()`
- 开启时卡片高亮（强调色边框）
- 开启随记后展开子选项："自动记录剪贴板内容"（ToggleSwitch）
- 开启天气后展开子选项："自动定位我的位置"（ToggleSwitch）
- 文件格子不需要选择，始终默认创建

#### 右侧 Scene 设计 — 完全重写

**场景概念**：动态桌面布局（随着用户选择实时变化）

**视觉元素**：

一个模拟的桌面区域，上面已经有：
1. 一个文件格子预览（始终存在，位于左上）
2. 根据用户选择动态出现/消失的功能格子预览

**动画与联动设计**：

- **Toggle ON 动画**：对应的 mini widget preview 从桌面边缘滑入（带弹性缓动），停在一个合理的位置
- **Toggle OFF 动画**：对应的 mini widget preview 缩小 + 淡出消失
- **布局重排**：当多个功能格子开启时，预览会自动排列在文件格子周围（不需要复杂的布局算法，预设几个位置即可）
- **文件格子始终存在**：在场景中始终显示一个文件格子预览，传达"文件格子是一定会创建的"
- **音乐格子特殊处理**：如果开启音乐格子，预览中的频谱条做循环脉冲动效（类似现有 `PlayMusicBarsPulse`）
- **天气格子特殊处理**：如果开启天气格子，预览中显示一个简单的温度数字 + 天气图标

**技术实现要点**：
- 每个功能格子的 mini preview 复用现有的 `CreateMiniWidgetPreview` 等方法（但视觉风格重做）
- Toggle 切换时不需要重建整个场景，只需 add/remove 对应的 preview 元素
- 布局位置预设：文件格子固定在左上，功能格子根据开启顺序排列在右侧和下方

---

### Step 5：日常使用

**目标**：设置日常访问方式，让用户知道怎么唤起和收起格子

#### 左侧文案
- Eyebrow: "日常使用"
- Title: "随时唤起，随时收起"
- Body: "格子不会一直挡在你的面前。通过托盘图标或快捷键，你可以随时显示或隐藏所有格子。"

#### 左侧交互 — 真实联动

##### 全局快捷键
- ToggleSwitch，默认开启
- 切换时调用 `GlobalHotkeyService.SetEnabled(isOn)`
- 旁边显示当前快捷键的可视化按键（如 "F7"）

##### 开机自启
- ToggleSwitch，默认开启
- 切换时调用 `StartupService.SetEnabled(isOn)` + `settings.AutoStart = isOn` + `SaveDebounced()`

#### 右侧 Scene 设计 — 完全重写

**场景概念**：显示/隐藏演示动画

**视觉元素**：

1. 模拟桌面区域，上面有 2-3 个格子预览
2. 底部有一个模拟的任务栏，任务栏右侧有 DeskBox 托盘图标
3. 一个模拟的快捷键按键（F7 keycap）

**动画与联动设计**：

- **循环演示动画**（当快捷键 Toggle 为 ON 时）：
  1. F7 按键高亮闪烁（模拟按下）
  2. 桌面上的格子向下滑出 + 淡出（隐藏动画）
  3. 停顿 0.5 秒
  4. F7 按键再次高亮闪烁
  5. 格子从下方滑入 + 淡入（显示动画）
  6. 循环
- **Toggle OFF 时**：停止循环动画，格子保持可见静止状态，F7 按键灰显
- **托盘图标**：始终可见，带有微妙的光晕效果暗示"可以点击"
- **入场动画**：任务栏从底部滑入，托盘图标淡入，格子预览缩放淡入

**左右联动**：
- 快捷键 Toggle ON/OFF → 循环动画开始/停止
- 这让用户直观理解"按 F7 就能隐藏/显示格子"

---

### Step 6：准备就绪

**目标**：总结配置，告诉用户桌面上已有空格子，引导首次操作

#### 左侧文案
- Eyebrow: "就绪"
- Title: "你的桌面整理空间已就绪"
- Body: "桌面上已经为你创建了一个空的收纳格子。把文件拖进去试试吧。"

#### 左侧交互

**配置摘要**（只读展示，不设交互）：
- 文件收纳：路径 + 已固定/未固定
- 外观：主题 + 主题色色块
- 功能格子：已开启的列表（如"文件格子 + 待办"）
- 日常使用：快捷键状态 + 自启状态

**首次使用引导**（3 条要点）：
1. "把桌面文件拖进收纳格子，快速整理"
2. "上传文件时，从资源管理器左侧的快速访问找到 DeskBox 文件夹"
3. "右键托盘图标查看更多操作"

**按钮**：
- 主按钮："开始使用 DeskBox"（强调色）
- 次按钮："重新配置"（回到 Step 1）

#### 右侧 Scene 设计 — 完全重写

**场景概念**：桌面预览 + 空格子引导

**视觉元素**：

1. 一个干净的模拟桌面区域
2. 桌面上有一个空的收纳格子预览（标题栏 + 空内容区）
3. 空内容区中央有一个虚线框 + "拖入文件" 提示文字 + 拖拽图标
4. 如果用户在 Step 4 开启了功能格子，它们也排列在桌面上

**动画设计**：

- **空格子脉动动画**：虚线框边框做柔和的强调色脉冲（opacity 0.3 → 0.7 → 0.3），吸引用户注意
- **拖入暗示动画**：一个文件图标从桌面边缘缓缓飘向格子，到达格子边缘时淡出，循环播放（暗示"可以拖入"）
- **入场动画**：格子从中心缩放淡入，提示文字延迟出现

**完成行为**：
- 标记 `HasCompletedOnboarding = true`
- 保存所有设置
- 关闭引导窗口
- 桌面上已有自动创建的空收纳格子（在引导启动前已创建）
- 已开启的功能格子根据 `FeatureWidgetEnabledStates` 创建
- 格子以入场动画出现在桌面上

**技术实现说明**：
根据 `App.xaml.cs` 中的现有逻辑：
```csharp
// 先创建默认文件格子（如果没有的话）
if (SettingsService.Settings.Widgets.Count(...) == 0 && !IsStartupMode)
{
    await WidgetManager.CreateManagedWidgetAsync(...);
}
// 然后显示引导
ShowOnboarding();
```
所以引导窗口出现时，桌面上已经有一个空的文件格子了。功能格子的创建需要在引导完成时触发（或引导中的 Toggle 已经写入了设置，`RestoreWidgetsAsync` 会处理）。

---

## 四、右侧 Scene 完全重写 — 设计原则

### 4.1 为什么要完全重写

现有右侧 Scene 的问题：
1. **视觉风格杂乱**：mini widget 预览 + badge 标签 + connector 线条混在一起，信息层级不清晰
2. **动画与内容脱节**：入场动画虽然精致，但和左侧的设置操作没有任何联动
3. **信息密度不均**：有的步骤塞了 5-6 个元素，有的只有 2 个
4. **卡片风格不统一**：`CreateMiniWidgetPreview`、`CreateCompactTileCard`、`CreateSecurityCard`、`CreatePathCard` 等多种卡片样式混用

### 4.2 新设计原则

1. **一个场景一个核心信息**：每步的右侧只传达一个核心概念，不堆砌
2. **风格统一**：所有 Scene 元素使用统一的卡片样式、圆角、间距、配色
3. **左右联动优先**：右侧场景应该响应左侧的操作，而不只是被动展示
4. **动效有目的**：动画不是装饰，而是帮助理解概念
5. **适当留白**：不要把 320x250 的区域塞满，留出呼吸空间

### 4.3 统一视觉规范

| 元素 | 规格 |
|------|------|
| 桌面区域背景 | 圆角 8px，带 1px 边框 |
| 卡片背景 | 半透明，跟随主题 |
| 强调色元素 | 使用 `ThemeService.GetEffectiveAccentColor()` |
| 文字大小 | 标题 12px，描述 10.5px |
| 圆角 | 统一 8px |
| 间距 | 元素间 8-12px |
| 动画缓动 | CubicEase EaseOut，时长 300-500ms |

---

## 五、各步骤联动关系总览

| 步骤 | 左侧操作 | 右侧响应 |
|------|----------|----------|
| Step 1 | 无 | 桌面演变动画（自动播放一次） |
| Step 2 | 切换"固定到快速访问" | ④ 边栏项高亮/灰显 |
| Step 2 | 更改收纳路径 | ③ 文件夹路径文字更新 |
| Step 3 | 切换主题 | 整个窗口 + 预览面板亮/暗切换 |
| Step 3 | 切换主题色 | 预览中强调色元素 + 窗口强调色同步变化 |
| Step 3 | 切换材质 | 预览背景质感变化 |
| Step 4 | 开启功能格子 Toggle | 对应 mini preview 滑入桌面场景 |
| Step 4 | 关闭功能格子 Toggle | 对应 mini preview 淡出消失 |
| Step 5 | 切换快捷键 Toggle | 循环演示动画开始/停止 |
| Step 6 | 无 | 空格子脉动 + 拖入暗示动画 |

---

## 六、技术实现要点

### 6.1 代码结构

```csharp
private static readonly OnboardingStep[] Steps =
[
    new("Onboarding.Step1", window => window.BuildWelcomeOptions(), window => window.BuildWelcomeScene()),
    new("Onboarding.Step2", window => window.BuildStorageFlowOptions(), window => window.BuildStorageFlowScene()),
    new("Onboarding.Step3", window => window.BuildAppearanceOptions(), window => window.BuildAppearanceScene()),
    new("Onboarding.Step4", window => window.BuildFeatureWidgetOptions(), window => window.BuildFeatureWidgetScene()),
    new("Onboarding.Step5", window => window.BuildDailyAccessOptions(), window => window.BuildDailyAccessScene()),
    new("Onboarding.Step6", window => window.BuildReadyOptions(), window => window.BuildReadyScene())
];
```

### 6.2 真实联动实现

所有引导中的设置操作都直接调用对应 Service 的方法，不是假预览：

| 设置项 | 调用的真实 API |
|--------|---------------|
| 主题切换 | `ThemeService.SetTheme(theme)` |
| 主题色切换 | `ThemeService.SetCustomAccentColor(color)` / `ThemeService.SetAccentMode("System")` |
| 材质切换 | `settings.WidgetMaterialType = value; settingsService.SaveDebounced()` |
| 功能格子开关 | `FeatureWidgetSettings.SetEnabled(settings, kind, isOn); settingsService.SaveDebounced()` |
| 快捷键开关 | `GlobalHotkeyService.SetEnabled(isOn)` |
| 开机自启 | `StartupService.SetEnabled(isOn); settings.AutoStart = isOn; settingsService.SaveDebounced()` |
| 收纳路径 | `WidgetManager.UpdateDefaultManagedStorageRootAsync(path)` |
| 固定到快速访问 | `ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(path)` |

### 6.3 引导窗口注册到 ThemeService

引导窗口在创建时需要被 `ThemeService.TrackWindow()` 注册，这样主题切换才能实时应用：

```csharp
// App.xaml.cs 中的 ShowOnboarding() 方法已有此调用
ThemeService.TrackWindow(_onboardingWindow);
```

### 6.4 Scene 重建机制

每个步骤切换时：
1. `StopSceneAnimations()` — 停止上一步的所有动画
2. `DemoScene.Children.Clear()` — 清空所有元素
3. 调用新步骤的 `BuildScene` 方法 — 创建新场景
4. 如果需要联动，在左侧设置项的 `Changed` 事件中更新右侧元素

### 6.5 功能格子创建流程

引导完成后的格子创建：

1. **文件格子**：已在引导启动前由 `App.xaml.cs` 创建（`CreateManagedWidgetAsync`）
2. **功能格子**：引导中的 Toggle 已经通过 `FeatureWidgetSettings.SetEnabled` 写入了设置。引导窗口关闭后，如果需要立即创建，可以：
   - 方案 A：在 `OnboardingWindow.Closed` 事件中调用 `WidgetManager` 创建已启用的功能格子
   - 方案 B：引导中的 Toggle 切换时直接调用 `WidgetManager` 创建/销毁对应格子（实时生效）
   - **推荐方案 A**：引导结束时统一创建，避免引导过程中格子频繁创建销毁

### 6.6 本地化字符串

需要新增以下键值（中英文）：

```
// Step 1
Onboarding.Step1.Eyebrow / Title / Body / Hint1 / Hint2

// Step 2
Onboarding.Step2.Eyebrow / Title / Body / Hint1 / Hint2
Onboarding.Step2.PinTitle / PinDescription
Onboarding.Scene.DeskFile / DragInto / MoveTo / PinToSidebar / Pinned / MappedShortcut

// Step 3
Onboarding.Step3.Eyebrow / Title / Body / Hint1 / Hint2
Onboarding.Step3.ThemeSection / AccentSection / MaterialSection
Onboarding.Step3.ThemeSystem / ThemeLight / ThemeDark
Onboarding.Step3.MaterialMica / MaterialAcrylic / MaterialSolid
Onboarding.Step3.CustomColor

// Step 4
Onboarding.Step4.Eyebrow / Title / Body / Hint1 / Hint2
Onboarding.Step4.TodoTitle / TodoDescription
Onboarding.Step4.QuickCaptureTitle / QuickCaptureDescription
Onboarding.Step4.MusicTitle / MusicDescription
Onboarding.Step4.WeatherTitle / WeatherDescription
Onboarding.Step4.ClipboardSubOption / AutoLocationSubOption

// Step 5
Onboarding.Step5.Eyebrow / Title / Body / Hint1 / Hint2
Onboarding.Step5.HotkeyTitle / HotkeyDescription
Onboarding.Step5.StartupTitle / StartupDescription

// Step 6
Onboarding.Step6.Eyebrow / Title / Body / Hint1 / Hint2 / Hint3
Onboarding.Step6.SummaryStorage / SummaryAppearance / SummaryWidgets / SummaryDaily
Onboarding.Step6.Reconfigure / StartWithDeskBox
Onboarding.Step6.EmptyWidgetHint
```

---

## 七、流程对比

| 维度 | 当前流程 | 新流程 |
|------|----------|--------|
| 步骤数 | 5 步 | 6 步 |
| 纯信息步骤 | 2 步 | 1 步（Step 1，精简） |
| 可设置步骤 | 2 步 | 5 步（Step 2-6） |
| 文件收纳机制讲解 | 一句话带过 | Step 2 完整讲解 + 可视化 + 路径 + 固定 |
| 固定到资源管理器 | 无 | Step 2 引导开启 |
| 主题/外观设置 | 无 | Step 3 完整设置 |
| 功能格子启用 | 仅剪贴板开关 | Step 4 四个格子独立 Toggle |
| 功能格子默认状态 | Todo 默认开 | 全部默认关闭，用户自选 |
| 左右联动 | 无 | 每步都有联动 |
| 右侧 Scene 风格 | 杂乱多种卡片 | 统一风格 + 目的性动画 |
| 完成后桌面状态 | 默认配置 | 用户自定义配置 |
| 空格子引导 | 无 | Step 6 明确说明 |
| 设置真实性 | 部分假预览 | 全部真实联动 |
| 总结反馈 | 无 | Step 6 配置摘要 |

---

## 八、后续可选优化

1. **引导中的微交互**：Step 4 开启功能格子时，对应的 mini preview 有弹性入场动画
2. **引导后的首次使用提示**：引导结束后，在桌面上显示一个轻量提示气泡，引导用户尝试拖入文件
3. **渐进式功能发现**：如果用户在引导中没开启某些功能格子，可以在后续使用中通过托盘菜单的"发现新功能"入口引导开启
4. **Step 2 动画增强**：文件流动画可以做成循环播放，让用户有更多时间理解
5. **A/B 测试**：对比不同引导流程的用户留存和功能启用率
