# DeskBox 待办格子 V2 — 最终设计方案

## 一、设计哲学

> 桌面任务中心（Task Hub），不是待办列表。

DeskBox 的待办格子不是 Microsoft To Do 的替代品。它是一个**桌面入口**——用户看到的第一个东西，操作的最后一个东西。它的核心价值不是"管理任务"，而是"连接任务与桌面资源"。

因此 V2 的设计原则是：

1. **零跳转** — 所有操作在格子内完成，不进入任何详情页
2. **渐进式复杂度** — 默认极简，需要时展开，不需要时不占空间
3. **上下文优先** — 任务不是孤立的文字，它可以关联文件、图片、链接
4. **原生优先** — 能用 WinUI 3 原生组件实现的，不自己造轮子

---

## 二、当前架构分析

### 2.1 数据模型现状

```
TodoItem
├── Id, Text, IsCompleted, IsImportant
├── ColorMarker, DueDate, Recurrence
├── ReminderOffsetMinutes, SnoozedUntil
├── SortOrder, CreatedAt, UpdatedAt
├── CompletedAt, RecurrenceSeriesId, GeneratedNextItemId
└── ReminderLastNotifiedAt, ReminderDismissedForDueDate, SnoozeLastNotifiedAt
```

**缺失**：子步骤（Steps）、备注（Notes）、附件（Attachments）。

### 2.2 排序逻辑现状

`CompareVisibleItems` 排序优先级：
1. 未完成 → 已完成
2. DueDate（早的在前）
3. SortOrder（手动顺序）
4. UpdatedAt（新的在前）

**问题**：`IsImportant` 不参与排序，星标只是一个视觉标记，不影响位置。

### 2.3 交互入口现状

| 操作 | 入口 |
|------|------|
| 新建 | 顶部 TextBox + Enter |
| 编辑文本 | 双击 → 全屏覆盖层 `WidgetInlineEditor` |
| 完成 | 点击 Checkbox |
| 星标 | 悬浮按钮 / 右键菜单 |
| 颜色 | 右键菜单 → 子菜单 |
| 日期 | 右键菜单 → 子菜单 → Custom 弹出 `CalendarDatePicker` 覆盖层 |
| 提醒 | 右键菜单 → 子菜单 |
| 贪睡 | 右键菜单 → 子菜单 |
| 重复 | 右键菜单 → 子菜单 |
| 删除 | 悬浮按钮 / 右键菜单 |
| 拖拽排序 | ListView `CanReorderItems` |
| 框选复制 | 自定义 `PointerPressed/Moved/Released` |
| 外部文本拖入 | `RootGrid_Drop` 接受文本 |

**核心问题**：除了完成和删除，所有高级操作都藏在右键菜单里。用户需要 3 次点击才能设置一个日期。编辑文本需要打开一个全屏覆盖层，割裂感很强。

---

## 三、V2 整体结构

### 3.1 布局结构

```
┌─────────────────────────────────────────┐
│  [输入标题........................] ⭐ ⏰ │  ← 新建行（带快捷操作）
├─────────────────────────────────────────┤
│  [全部] [今天] [重要] [已完成]    ●●●●  │  ← 筛选行（不变）
├─────────────────────────────────────────┤
│  ○ 完成登录功能              ⭐        │  ← 收起态 item
│  ○ 优化图片缓存             ⭐ 🔴      │
│  ● ◇已完成的设计稿                     │  ← 已完成 item
│                                         │
│  ○ 重新设计 DeskBox    ← 展开态 item   │  ← 展开态（同一 ListView）
│    ┊ ⭐重要  ⏰明天  🔁每周  🎨红色    │  ← 元数据行
│    ┊ ─────────────────────────────    │
│    ┊ 📝 备注...                       │  ← 备注（折叠）
│    ┊ ─────────────────────────────    │
│    ┊ ☑ 确定布局                       │  ← 子步骤
│    ┊ ☐ 实现 XAML                      │
│    ┊ ＋ 添加步骤                      │
│    ┊ ─────────────────────────────    │
│    ┊ 📎 2                             │  ← 附件（折叠）
│    ┊ ─────────────────────────────    │
│    ┊ 🗑 删除              ✓ 标记完成   │  ← 操作行
├─────────────────────────────────────────┤
│  还有 3 件事                    清除已完成│  ← 底部（不变）
└─────────────────────────────────────────┘
```

### 3.2 关键设计决策

| 决策 | 选择 | 原因 |
|------|------|------|
| 卡片展开方式 | `Visibility` 绑定 + `IsExpanded` 状态 | WinUI 3 的 `Expander` 控件不适合 ListView 内部使用（与虚拟化冲突），自定义 `Visibility` 绑定更可控 |
| 展开动画 | `AddDeleteThemeTransition` + `ImplicitAnimation` | 原生 `ThemeTransition` 处理高度变化，Composition `ImplicitAnimation` 处理其他 item 的位移 |
| 标题编辑 | `TextBox` 替换 `TextBlock`，`LostFocus` 自动保存 | 不需要覆盖层，不需要保存按钮，最轻量 |
| 日期/提醒/重复选择 | `MenuFlyout` + `CalendarDatePicker` in `Flyout` | 原生组件，不需要全屏覆盖层 |
| 子步骤排序 | `ListView` + `CanReorderItems` | 原生拖拽排序，和主列表一致 |
| 完成动画 | `AnimatedIcon` + `ImplicitAnimation` | 原生 `AnimatedIcon` 做勾选动画，Composition 做位移动画 |

---

## 四、逐项交互设计

### 4.1 新建待办 — 快捷操作栏

**当前**：TextBox + Enter，要设置日期/重要等需打开覆盖层。

**V2**：

```
┌─────────────────────────────────────┐
│ 输入标题...                    ⭐ ⏰ │
└─────────────────────────────────────┘
```

- TextBox 右侧始终显示 `⭐` 和 `⏰` 两个按钮（灰显，有值时高亮）
- `⭐` 点击 → 切换重要状态（影响新建后的排序位置）
- `⏰` 点击 → `MenuFlyout` 弹出日期预设（今天/明天/本周五/自定义/清除）
  - 自定义 → `CalendarDatePicker` in `Flyout`（不是全屏覆盖层）
- Enter → 用当前 TextBox 文本 + 快捷操作设置的属性创建任务
- 创建后清空 TextBox 和快捷操作状态，焦点留在 TextBox 方便连续输入
- `⏰` 设置后图标变为实心 + 显示日期文本（如"明天"）

**为什么只放 ⭐ 和 ⏰**：这两个是新建时最高频设置的属性。`🔁` 重复和 `🎨` 颜色使用频率低，展开后可以在卡片内设置。`📎` 附件依赖 Phase 2 数据模型。

**WinUI 3 原生组件**：`TextBox` + `Button` + `MenuFlyout` + `CalendarDatePicker`。全部原生。

**数据层**：ViewModel 新增 `DraftImportant` 和 `DraftDueDate` 两个临时属性，`AddInputAsync` 读取它们并应用到新 item，然后重置。

---

### 4.2 卡片展开 — 核心交互

**当前**：双击打开 `WidgetInlineEditor` 全屏覆盖层。

**V2**：单击 item 文本区域 → 行内展开。

**展开/收起规则**：

- 点击 item 的文本区域（不是 checkbox、不是星标按钮）→ 切换 `IsExpanded`
- 同一时间最多一个 item 展开（展开 A 时自动收起 B）
- 点击另一个 item 的文本 → 先收起当前展开的，再展开新的
- 点击格子内的空白区域 → 收起当前展开的
- 按 `Escape` → 收起当前展开的

**展开后的内容结构**（自上而下）：

```
○ [标题文本 — 可点击编辑]          ⭐    ← 标题行（checkbox + 标题 + 星标）
  ⭐重要  ⏰明天 18:00  🔁每周  🎨红色    ← 元数据行（横向排列，可点击修改）
  ─────────────────────────────────────  ← 分隔线
  📝 备注...                              ← 备注（默认折叠，有内容时显示首行预览）
  ─────────────────────────────────────
  ☑ 确定布局                              ← 子步骤列表
  ☐ 实现 XAML
  ＋ 添加步骤
  ─────────────────────────────────────
  📎 2 个附件                              ← 附件（默认折叠）
  ─────────────────────────────────────
  🗑 删除                    ✓ 标记完成    ← 操作行
```

**收起态保持不变**：checkbox + 颜色点 + 标题 + 日期状态 + 悬浮操作（星标/删除）。

**WinUI 3 原生组件**：
- 展开区域用一个 `Grid` 或 `StackPanel`，`Visibility="{Binding IsExpanded, Converter={StaticResource BoolToVisibilityConverter}}"`
- `ListViewItem` 自动适应高度，`ScrollViewer` 自动滚动以确保展开内容可见
- 动画：在 `ListView.ItemContainerStyle` 里加 `Transitions`，包含 `AddDeleteThemeTransition` 和 `ContentThemeTransition`，这样高度变化时有原生过渡效果

**关于 `Expander` 控件**：WinUI 3 有原生 `Expander`，但它设计用于设置页面的手风琴布局，不适合 `ListView` 内部使用——它会干扰 `ListView` 的虚拟化和容器回收。自定义 `Visibility` 绑定是更正确的做法，Windows 设置 App 的可展开列表项也是这样实现的。

---

### 4.3 标题内联编辑

**当前**：双击 → 全屏覆盖层 → Save/Cancel。

**V2**：在展开态下，点击标题文本 → 原地变为 `TextBox`。

**流程**：
1. 展开态下，标题是 `TextBlock`
2. 点击标题 → `TextBlock` 隐藏，`TextBox` 出现，选中文本
3. Enter → 保存，变回 `TextBlock`
4. `LostFocus`（点击其他地方）→ 保存，变回 `TextBlock`
5. Escape → 取消，恢复原文本，变回 `TextBlock`
6. 空文本 → 不保存，恢复原文本

**无 Save 按钮、无 Cancel 按钮、无覆盖层。**

**WinUI 3 原生组件**：`TextBlock` + `TextBox` + `Visibility` 绑定。`TextBox.LostFocus` 事件做自动保存。

**实现**：`TodoItemViewModel` 已有 `IsEditing` / `EditText` / `TextVisibility` / `EditVisibility` 属性。只需在 XAML 里把编辑模式的 `TextBox` 从覆盖层移到展开区域内。

---

### 4.4 元数据行

展开后标题下方显示一行元数据标签，每个标签可点击修改：

**⭐ 重要**：
- 点击 → 切换 `IsImportant`
- 已启用时高亮（Accent 色）
- 影响排序（见 4.10）

**⏰ 日期**：
- 点击 → `MenuFlyout`：今天 / 明天 / 本周五 / 下周一 / 自定义 / 清除
- 自定义 → `CalendarDatePicker` in `Flyout` + `TimePickerFlyout` 选时间
- 有值时显示自然语言文本（"明天 18:00" / "7月20日"）
- 逾期时文字变橙色

**🔁 重复**：
- 点击 → `MenuFlyout`：不重复 / 每天 / 每周 / 每月 / 工作日
- 有值时显示模式名（"每周"）
- 无日期时灰显不可点

**🎨 颜色**：
- 点击 → `MenuFlyout`：无 / 🔴🟠🟡🟢🔵🟣
- 有值时显示对应颜色圆点

**🔔 提醒**：
- 只在有日期时显示
- 点击 → `MenuFlyout`：默认提醒 / 关闭 / 到期时 / 1小时前 / 1天前
- 有值时显示铃铛图标 + 偏移文本

**每个标签的交互统一**：点击 → `MenuFlyout` → 选择 → 立即生效（无需确认）。这比当前的右键菜单 → 子菜单路径短了 1-2 步。

**WinUI 3 原生组件**：`Button` + `MenuFlyout` + `MenuFlyoutSubItem` + `CalendarDatePicker` + `TimePickerFlyout`。全部原生，且已在当前代码中使用。

---

### 4.5 子步骤（Checklist）

**当前**：无。

**V2 数据模型**：

```csharp
public sealed class TodoStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
}
```

`TodoItem` 新增：`public List<TodoStep> Steps { get; set; } = [];`

**UI**：

展开卡片内，分隔线下方：

```
☑ 确定布局
☐ 实现 XAML
☐ 测试交互
＋ 添加步骤
```

- 每个步骤：`CheckBox` + `TextBlock`（完成的步骤有删除线）
- 底部有一个 `TextBox`（placeholder："添加步骤"），Enter 添加并清空，焦点留在 `TextBox`
- 拖拽排序：`ListView` + `CanReorderItems="True"`（和主列表一致）
- 点击步骤文本 → 内联编辑（同标题编辑逻辑）
- 步骤右侧悬浮显示删除按钮

**步骤进度**：如果步骤数 > 0，在收起态的 item 上显示进度（如 "2/3"），用小字号在日期状态旁。

**WinUI 3 原生组件**：`ListView` + `CheckBox` + `TextBox` + `CanReorderItems`。全部原生。

**数据迁移**：旧数据 `Steps = []`，无影响。`TodoWidgetData.Version` 从 2 升到 3。

---

### 4.6 备注

**当前**：无。

**V2 数据模型**：`TodoItem` 新增 `public string? Notes { get; set; }`

**UI**：

展开卡片内，子步骤上方：

- **无备注**：显示一个 "＋ 添加备注" 按钮
- **有备注**：显示一行预览（首行文本，省略号截断），点击展开
- **展开后**：`TextBox` with `AcceptsReturn="True"`，`TextWrapping="Wrap"`
- `LostFocus` → 自动保存
- Escape → 取消

**WinUI 3 原生组件**：`TextBox` + `Visibility` 绑定。原生。

---

### 4.7 附件

**当前**：无。

**V2 数据模型**：

```csharp
public sealed class TodoAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "file"; // image, pdf, file, folder, url
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`TodoItem` 新增：`public List<TodoAttachment> Attachments { get; set; } = [];`

**UI**：

展开卡片内，子步骤下方：

- **无附件**：显示 "＋ 添加附件" 按钮（打开文件选择器 `FileOpenPicker`）
- **有附件**：显示 "📎 3" 按钮，点击展开
- **展开后**：横向滚动的缩略图列表
  - 图片：显示缩略图（`BitmapImage`）
  - PDF/文件：显示文件类型图标 + 文件名
  - URL：显示 🔗 + 域名
- 点击附件 → 用默认程序打开（`Win32Helper.OpenFile`）
- 附件右上角悬浮 × 删除按钮

**WinUI 3 原生组件**：`ItemsControl` + `WrapPanel`（或 `StackPanel` Horizontal）+ `BitmapImage`。`FileOpenPicker` 原生。缩略图加载用现有的 `FileService.GetIconAsync`。

**数据迁移**：旧数据 `Attachments = []`，无影响。

---

### 4.8 日期选择交互

**当前**：右键 → `MenuFlyoutSubItem` → Today/Tomorrow/ThisWeek/Custom → Custom 打开全屏 `CalendarDatePicker` 覆盖层。

**V2**：

在元数据行点击 `⏰` → `MenuFlyout`：

```
📅 今天
📅 明天
📅 本周五
📅 下周一
─────────
📅 选择日期...  → CalendarDatePicker Flyout
─────────
✕ 清除
```

- 预设项直接设置，`MenuFlyout` 自动关闭
- "选择日期" → 不关闭 `MenuFlyout`，在下方展开 `CalendarDatePicker` + 时间选择按钮
  - 或者更简单：关闭 `MenuFlyout`，在元数据行下方显示一个 `CalendarDatePicker` + `TimePickerFlyout` 的内联面板
- 选择后立即生效，无需确认

**为什么不用全屏覆盖层**：当前的全屏 `CustomDueDateOverlay` 在格子很小的时候会遮挡整个视图，体验不好。内联面板或 `Flyout` 更轻量。

**WinUI 3 原生组件**：`MenuFlyout` + `CalendarDatePicker` + `TimePickerFlyout`。`Flyout` 可以作为 `CalendarDatePicker` 的容器。

---

### 4.9 提醒交互

**当前**：右键 → `MenuFlyoutSubItem` → 列表选择。

**V2**：

在元数据行点击 `🔔`（仅有日期时显示）→ `MenuFlyout`：

```
🔔 默认提醒（1小时前）
✕ 关闭提醒
─────────
⏰ 到期时提醒
⏰ 1小时前
⏰ 1天前
⏰ 30分钟前
```

选择后立即生效。贪睡功能保留在右键菜单中（低频操作）。

**WinUI 3 原生组件**：`MenuFlyout`。原生。

---

### 4.10 星标与排序

**当前**：`IsImportant` 不参与排序。

**V2 排序逻辑**（修改 `CompareVisibleItems`）：

```
1. 未完成 → 已完成（不变）
2. [新增] 未完成中：重要 → 不重要
3. DueDate（不变）
4. SortOrder（不变）
5. UpdatedAt（不变）
```

即：重要的未完成任务浮动到列表顶部，在所有未完成项目中优先显示。

**副作用考虑**：用户手动拖拽排序后，如果星标状态改变，item 会跳到顶部或回到正常区域。这是预期行为——星标就是"置顶"的意思。

如果用户不希望星标影响排序（只是收藏），可以在设置里加一个开关 `TodoStarAffectsSort`（默认开）。但 V2 第一版先不做这个开关，直接让星标影响排序。

**改动量**：`CompareVisibleItems` 加 3 行代码。

---

### 4.11 完成动画

**当前**：`ContentOpacity` 从 1 变 0.62，`TextDecorations` 设为删除线。瞬间生效。

**V2 动画序列**：

1. **T=0ms**：用户点击 checkbox
2. **T=0~300ms**：`AnimatedIcon` 播放勾选动画（WinUI 3 原生 `AnimatedIcon` 支持 checkbox 的 `Checked` → `Unchecked` 过渡动画）
3. **T=0~300ms**：文字删除线动画——用一个 `Line` overlay 从左到右扫描（`XAML RenderTransform` + `DoubleAnimation`）
4. **T=0~300ms**：文字 `Opacity` 从 1 渐变到 0.5
5. **T=300~500ms**：等待（让用户看到完成效果）
6. **T=500ms**：调用 `SetCompletedAsync` → `RefreshVisibleItems` → item 在 `VisibleItems` 中从活跃区移到完成区
7. **T=500~800ms**：`ListView` 的 `AddDeleteThemeTransition` 处理 item 在旧位置消失、新位置出现的过渡

**关于"滑动到底部"**：

真正的"item 从位置 A 滑到位置 B"动画需要 Composition API 的 `ImplicitAnimation`。具体做法：

- 在 `ListView` 的 `ContainerContentChanging` 事件中，为每个 `ListViewItem` 的 `Visual` 设置 `ImplicitAnimation`，监听 `Offset` 属性变化
- 当 `RefreshVisibleItems` 重新排序后，`ListView` 会重新定位 item，`ImplicitAnimation` 会自动播放位移动画

这样 item 不是"消失再出现"，而是"平滑滑动"到新位置。

**WinUI 3 原生组件**：`AnimatedIcon`（原生）+ `DoubleAnimation`（原生 Storyboard）+ `ImplicitAnimation`（Composition API，需要 `Microsoft.UI.Composition`）。

**复杂度**：中等。`AnimatedIcon` 和 `DoubleAnimation` 是标准用法。`ImplicitAnimation` 需要在 `ContainerContentChanging` 中设置，约 30 行代码。

**取消完成（反勾选）**：反向播放动画——删除线消失、Opacity 恢复、item 滑回活跃区。

---

### 4.12 多状态空界面

**当前**：单一空状态。

**V2 三种状态**：

| 状态 | 触发条件 | 图标 | 标题 | 副标题 |
|------|---------|------|------|--------|
| **空列表** | `Items.Count == 0` | 📋 | 今天没有待办 | 点击上方创建第一个任务 |
| **全部完成** | `Items.Count > 0 && ActiveCount == 0` | 🎉 | 今天完成了全部任务 | 真不错！ |
| **有剩余** | `ActiveCount > 0` | （不显示空状态） | — | — |

**视觉差异**：
- 全部完成时：空状态区域背景使用极淡的 Accent 色叠加（Opacity 0.04），营造"庆祝"氛围
- 空列表时：中性背景

**交互**：
- 空列表状态下，点击"创建第一个任务"→ 聚焦顶部 TextBox
- 全部完成状态下，不显示可点击操作（纯展示）

**WinUI 3 原生组件**：`Grid` + `Visibility` 绑定 + `ThemeResource` 颜色。原生。

---

### 4.13 Context 上下文创建

**当前**：`RootGrid_Drop` 只接受文本（`StandardDataFormats.Text`），显式排除 `StorageItems`。

**V2**：扩展 Drop 处理，根据拖入内容类型自动创建任务并关联附件。

**类型识别与处理**：

| 拖入内容 | 识别方式 | 创建的任务 | 附件处理 |
|---------|---------|-----------|---------|
| **文本** | `StandardDataFormats.Text` | 标题 = 文本 | 无 |
| **URL** | 文本以 `http://` 或 `https://` 开头 | 标题 = URL 或网页标题 | `TodoAttachment.Type = "url"` |
| **图片文件** | `StorageItems` + 扩展名是 .png/.jpg/.jpeg/.bmp/.webp | 标题 = "查看 {文件名}" | `TodoAttachment.Type = "image"`，存路径 |
| **PDF 文件** | `StorageItems` + 扩展名是 .pdf | 标题 = "阅读 {文件名}" | `TodoAttachment.Type = "pdf"` |
| **文件夹** | `StorageItems` + `StorageFolder` | 标题 = "整理 {文件夹名}" | `TodoAttachment.Type = "folder"` |
| **其他文件** | `StorageItems` + 其他 | 标题 = "处理 {文件名}" | `TodoAttachment.Type = "file"` |

**DragOver 处理**：
- 当前：只接受文本
- V2：接受文本 + `StorageItems`
- `DragUIOverride` 显示对应提示（"创建任务：阅读 xxx.pdf"）

**Drop 处理流程**：
1. 检查 `DataView.Contains(StorageItems)`
2. 如果是 → 获取 `StorageItems` → 对每个 item 识别类型 → 创建任务 + 附件
3. 如果不是 → 检查文本 → 判断是否 URL → 创建任务
4. 显示 Undo Toast（"已创建任务：阅读 xxx.pdf" → 撤销）

**冲突考虑**：
- 待办格子和文件格子是不同的 `WidgetKind`，用户拖到待办格子时，文件格子不会接收
- 但如果用户想拖文件到文件格子，不小心拖到了旁边的待办格子 → 会创建任务而不是收纳文件。这个行为差异需要在 `DragOver` 时通过 `DragUIOverride.Caption` 明确提示

**WinUI 3 原生组件**：`DragEventArgs` + `DataPackageView` + `StorageFile`/`StorageFolder`。`DataView.Contains(StandardDataFormats.StorageItems)` 原生支持。

**依赖**：需要 Phase 2 的 `TodoAttachment` 数据模型。

---

### 4.14 筛选与颜色标记

**当前**：`Segmented`（全部/今天/重要/已完成）+ 颜色筛选圆点行。

**V2**：保持不变。这是已经做好的部分，不需要重新设计。

唯一调整：当展开一个 item 后，如果切换筛选器，先收起展开的 item。

---

### 4.15 右键菜单

**当前**：10+ 个菜单项（编辑/复制/完成/重要/颜色/日期/提醒/贪睡/重复/删除），部分有子菜单。

**V2**：精简为低频操作的快捷入口：

```
✏️ 编辑文本
📋 复制
─────────
🗑 删除
```

- **编辑文本**：等同于点击展开 → 点击标题（快捷方式）
- **复制**：保持不变（含批量复制逻辑）
- **删除**：保持不变

所有其他操作（完成/重要/颜色/日期/提醒/贪睡/重复）都在展开卡片的元数据行中直接操作，不再需要右键菜单。

**贪睡**：保留在右键菜单中，因为贪睡是低频但紧急的操作，在提醒弹出时可能需要快速访问。

```
✏️ 编辑文本
📋 复制
💤 贪睡 ▶
─────────
🗑 删除
```

**WinUI 3 原生组件**：`MenuFlyout` + `MenuFlyoutSubItem`。原生。

---

## 五、数据模型变更总览

### 5.1 新增模型

```csharp
public sealed class TodoStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
}

public sealed class TodoAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "file"; // image, pdf, file, folder, url
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 5.2 TodoItem 新增字段

```csharp
public List<TodoStep> Steps { get; set; } = [];
public string? Notes { get; set; }
public List<TodoAttachment> Attachments { get; set; } = [];
```

### 5.3 TodoWidgetData 版本升级

```csharp
public int Version { get; set; } = 3; // 从 2 升到 3
```

**迁移逻辑**：加载 V2 数据时，如果 `Version < 3`，为每个 item 补充 `Steps = []`, `Notes = null`, `Attachments = []`。JSON 反序列化会自动处理缺失字段（C# 的 default），所以实际上不需要显式迁移代码。

### 5.4 TodoItemViewModel 新增属性

```csharp
public bool IsExpanded { get; set; }  // 展开状态
public ObservableCollection<TodoStepViewModel> Steps { get; }  // 子步骤
public string? Notes { get; set; }  // 备注
public ObservableCollection<TodoAttachmentViewModel> Attachments { get; }  // 附件
public string StepProgressText => $"{CompletedStepCount}/{TotalStepCount}";
public bool HasSteps => Steps.Count > 0;
public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
public bool HasAttachments => Attachments.Count > 0;
```

---

## 六、实施阶段规划

### Phase 1 — 交互重构（不改数据模型）

**目标**：把右键菜单操作提升到卡片展开内联操作。

| 序号 | 任务 | 改动范围 | 复杂度 |
|------|------|---------|--------|
| 1.1 | `IsExpanded` 状态 + 展开区域 XAML | `TodoItemViewModel` + `TodoWidgetContent.xaml` | 中 |
| 1.2 | 单击展开/收起 + 同时只展开一个 | `TodoWidgetContent.xaml.cs` | 低 |
| 1.3 | 元数据行（⭐⏰🔁🎨🔔） | `TodoWidgetContent.xaml` | 中 |
| 1.4 | 日期 MenuFlyout（替代覆盖层） | `TodoWidgetContent.xaml.cs` | 低 |
| 1.5 | 提醒/重复 MenuFlyout | `TodoWidgetContent.xaml.cs` | 低 |
| 1.6 | 标题内联编辑（替代覆盖层） | `TodoWidgetContent.xaml` + `.cs` | 低 |
| 1.7 | 新建快捷操作栏（⭐⏰） | `TodoWidgetContent.xaml` + `TodoWidgetViewModel.cs` | 低 |
| 1.8 | 星标影响排序 | `TodoWidgetViewModel.cs` | 极低 |
| 1.9 | 多状态空界面 | `TodoWidgetViewModel.cs` + `TodoWidgetContent.xaml` | 低 |
| 1.10 | 完成动画（AnimatedIcon + ImplicitAnimation） | `TodoWidgetContent.xaml` + `.cs` | 中高 |
| 1.11 | 右键菜单精简 | `TodoWidgetContent.xaml.cs` | 极低 |

**Phase 1 不涉及数据模型变更**，风险低，用户体感变化最大。

### Phase 2 — 数据模型扩展

| 序号 | 任务 | 改动范围 | 复杂度 |
|------|------|---------|--------|
| 2.1 | `TodoStep` 模型 + `TodoStepViewModel` | `Models/` + `ViewModels/` | 低 |
| 2.2 | 子步骤 UI（ListView + CheckBox + 拖拽排序） | `TodoWidgetContent.xaml` + `.cs` | 中 |
| 2.3 | 步骤进度在收起态显示 | `TodoWidgetContent.xaml` | 低 |
| 2.4 | `Notes` 字段 + 折叠备注 UI | `Models/` + `TodoWidgetContent.xaml` | 低 |
| 2.5 | `TodoAttachment` 模型 + `TodoAttachmentViewModel` | `Models/` + `ViewModels/` | 低 |
| 2.6 | 附件 UI（缩略图 grid + 打开 + 删除） | `TodoWidgetContent.xaml` + `.cs` | 中 |
| 2.7 | `FileOpenPicker` 添加附件 | `TodoWidgetContent.xaml.cs` | 低 |
| 2.8 | 数据迁移（Version 2→3） | `TodoWidgetStore.cs` | 极低 |

### Phase 3 — Context 上下文创建

| 序号 | 任务 | 改动范围 | 复杂度 |
|------|------|---------|--------|
| 3.1 | 扩展 `RootGrid_DragOver` 接受 `StorageItems` | `TodoWidgetContent.xaml.cs` | 低 |
| 3.2 | 类型识别 + 自动生成标题 | `TodoWidgetContent.xaml.cs` | 中 |
| 3.3 | 自动关联附件 | `TodoWidgetContent.xaml.cs` | 低 |
| 3.4 | URL 拖入识别 | `TodoWidgetContent.xaml.cs` | 中 |
| 3.5 | `DragUIOverride` 提示文案 | `TodoWidgetContent.xaml.cs` | 低 |
| 3.6 | 批量拖入处理 | `TodoWidgetContent.xaml.cs` | 中 |

---

## 七、WinUI 3 原生组件使用清单

| 组件 | 用途 | 是否原生支持 |
|------|------|------------|
| `ListView` + `CanReorderItems` | 主列表 + 子步骤列表拖拽排序 | ✅ 原生 |
| `MenuFlyout` + `MenuFlyoutSubItem` | 日期/提醒/重复/颜色/贪睡选择 | ✅ 原生 |
| `CalendarDatePicker` | 自定义日期选择 | ✅ 原生 |
| `TimePickerFlyout` | 时间选择 | ✅ 原生 |
| `TextBox` + `LostFocus` | 标题/备注内联编辑 | ✅ 原生 |
| `CheckBox` | 子步骤完成 | ✅ 原生 |
| `Button` + `FontIcon` | 所有操作按钮 | ✅ 原生 |
| `Visibility` 绑定 | 展开/收起、折叠/展开备注/附件 | ✅ 原生 |
| `AddDeleteThemeTransition` | 展开/收起高度动画 | ✅ 原生 |
| `ContentThemeTransition` | 内容切换动画 | ✅ 原生 |
| `AnimatedIcon` | Checkbox 勾选动画 | ✅ 原生 |
| `DoubleAnimation` (Storyboard) | 删除线扫描动画 | ✅ 原生 |
| `FileOpenPicker` | 添加附件 | ✅ 原生 |
| `BitmapImage` | 附件缩略图 | ✅ 原生 |
| `ImplicitAnimation` (Composition) | 完成后平滑滑动到新位置 | ⚠️ 需要 Composition API |
| `Flyout` | CalendarDatePicker 的容器 | ✅ 原生 |

**结论**：除了完成动画的"平滑滑动"需要 Composition API（`ImplicitAnimation`），其他所有交互都可以用 WinUI 3 原生组件实现。Composition API 也是 WinUI 3 的标准能力，不是 hack。

---

## 八、需要特别注意的设计细节

### 8.1 展开态与 ListView 虚拟化

`ListView` 默认开启虚拟化，展开一个 item 会增加其高度。当用户滚动时，虚拟化可能回收展开的 item 容器。需要确保：

- `IsExpanded` 状态存在 ViewModel 中（不在 UI 上），容器回收后重建时能恢复
- 同一时间只展开一个 item（减少虚拟化复杂度）
- 展开后 `ListView.ScrollIntoView` 确保展开内容可见

### 8.2 内联编辑与 IME

当前代码有 IME（输入法）兼容处理。内联编辑从覆盖层移到列表项内后，需要确保：

- `TextBox` 获得焦点时 IME 能正确弹出
- IME 组词过程中不会触发 `LostFocus` 保存
- Escape 取消编辑时不干扰 IME

### 8.3 拖拽冲突

待办格子已有多套拖拽逻辑：
- `ListView.CanReorderItems` — 拖拽排序
- `ListView.DragItemsStarting` — 拖出复制
- `RootGrid.Drop` — 外部文本拖入
- `PointerPressed/Moved/Released` — 框选

V2 新增 `StorageItems` 拖入后，需要确保：
- 内部排序拖拽不触发 Context 创建
- 框选不干扰拖入
- `DragOver` 时区分内部拖拽（`_draggedTodoItemId` 非空）和外部拖拽

### 8.4 附件文件失效

附件只存路径，文件可能被移动/删除。处理策略：

- 展开附件时检查 `File.Exists(path)`
- 文件不存在时：缩略图灰显 + "文件已移除" 标签
- 不自动删除附件记录（用户可能只是临时移动了文件）

### 8.5 完成动画与重复任务

重复任务完成后会生成下一个待办。动画需要处理：

- 当前 item 标记完成 → 播放完成动画 → 滑到完成区
- 新生成的下一个 item 出现在活跃区顶部（带 `AddDeleteThemeTransition` 淡入）
- 这两个动画同时播放，视觉上"旧任务完成 → 新任务出现"

### 8.6 格子尺寸适配

DeskBox 的格子可以调整大小。展开一个 item 后，如果格子很小：

- 展开内容可能超出格子可视区域 → `ScrollViewer` 自动处理垂直滚动
- 元数据行可能太窄 → 横向滚动或换行（优先换行，用 `WrapPanel` 语义）
- 子步骤列表有最大高度限制（如 200px），超出后内部滚动

---

## 九、与用户原始方案的差异

| 用户方案 | 我的调整 | 原因 |
|---------|---------|------|
| 新建时显示 `⏰ 📎 ⭐ 🔁` | 只显示 `⭐ ⏰` | `📎` 依赖 Phase 2，`🔁` 低频，展开后再设置 |
| 卡片展开后显示 `📝 备注` 在步骤上方 | 保持这个顺序 | 备注是解释"为什么做"，步骤是"怎么做"，逻辑上备注在前 |
| 附件展开显示缩略图列表 | 用横向滚动而非 grid | 格子宽度有限，横向滚动更省空间 |
| 日期选项"周五" | 改为"本周五" + "下周一" | 更明确，避免歧义 |
| 完成后"缩小→滑到底部" | 改为 `ImplicitAnimation` 平滑滑动 | 缩小动画在列表中会导致其他 item 跳动，滑动更平滑 |
| 星标 = 排序 | 保持，但加说明 | 星标影响排序是预期行为，但需要在 UI 上让用户感知到这个因果关系 |
| 右键菜单保留所有操作 | 精简到 3 项 + 贪睡 | 大部分操作移到展开卡片内，右键菜单只保留低频/紧急操作 |
| "不要保存按钮" | 完全采纳 | 所有编辑（标题/备注/步骤）都是 `LostFocus` 或 `Enter` 自动保存 |
| Task Hub 定位 | 完全采纳 | 这是产品方向，不是 UI 细节 |

---

## 十、总结

这个方案的核心是：

1. **卡片展开**取代覆盖层编辑 — 所有操作在列表内完成
2. **元数据行**取代右键菜单 — 日期/提醒/重复/颜色一目了然，一键修改
3. **子步骤 + 备注 + 附件** — 从纯文本待办升级为富任务
4. **Context 创建** — 拖文件/图片/URL 自动生成带附件的任务，这是 DeskBox 的差异化护城河
5. **完成动画** — 用 `AnimatedIcon` + `ImplicitAnimation` 实现"勾选→划线→滑动"的流畅体验
6. **星标排序** — 星标 = 置顶，不是空收藏

所有交互都可以用 WinUI 3 原生组件实现，只有完成动画的平滑滑动需要 Composition API（也是 WinUI 3 标准能力）。

分三个 Phase 实施，Phase 1 不改数据模型风险最低，Phase 3 是差异化核心。
