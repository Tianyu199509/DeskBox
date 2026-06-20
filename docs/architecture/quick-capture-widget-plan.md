# DeskBox Quick Capture Widget Plan

状态：方案稿  
创建日期：2026-06-20  
关联需求：`docs/requirements/quick-capture-widget.md`

## 1. 结论

“随手记 / Quick Capture Widget”适合 DeskBox 做，但必须作为可关闭的内置功能格子，而不是默认强推的新核心。

它对用户的真实价值不在于“又一个笔记软件”或“又一个剪贴板管理器”，而在于补上 DeskBox 当前文件格子之外的轻内容处理场景：

```text
刚复制的，看得见。
刚想到的，记得下。
常用的，固定住。
要发出去的，拿得走。
```

DeskBox 现有优势是桌面常驻、F7 临时唤起、跨窗口拖拽和轻量窗口层级控制。轻内容格子可以复用这套心智，让用户在微信、网页、表格、资源管理器之间工作时，把文本、链接、截图、常用话术暂存在一个看得见的位置。

不建议第一版直接实现完整需求稿里的“记录 / 固定 / 最近 / 图片 / 待办识别 / hover 标签 / 拖入拖出 / 剪贴板历史”全部能力。范围过大，会把 DeskBox 拉向复杂效率套件，增加性能、稳定性和交互成本。

推荐路线：

- 当前版本：只做手动记录和固定内容，验证“轻内容格子”是否真的被使用。
- 下一版本：在用户显式开启后，增加最近剪贴板记录。
- 未来版本：再做图片、拖出、轻待办和更丰富的规则识别。

## 2. 产品判断

### 2.1 值得做的原因

1. DeskBox 的现有场景已经不只是桌面整理，而是跨应用操作。

   用户用 F7 把文件格子临时抬到前面，本质是在解决“我正在其他窗口里，需要从 DeskBox 拿东西”的问题。轻内容也有同样需求：地址、发票信息、客服话术、链接、临时句子都经常需要被快速拿走。

2. Windows 原生工具没有完全覆盖这个体验。

   Windows 剪贴板 API 支持监听剪贴板变化，官方文档中 `Clipboard.ContentChanged` 用于跟踪剪贴板变化；但系统剪贴板历史不是桌面可视化工作区，也不围绕“固定、拖出、复用”设计。  
   参考：<https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.clipboard>

3. 这个功能能提高 DeskBox 的日常打开频率。

   文件收纳不是每分钟都发生，但复制文本、保存链接、复用话术是更高频的办公动作。做好后，DeskBox 会从“文件整理工具”变成“桌面轻内容操作台”。

### 2.2 不值得做的部分

第一阶段不值得做完整剪贴板管理器、完整待办、富文本笔记、标签系统、云同步、AI 整理。

这些功能很容易让用户产生新的期待：

- 你做了待办，用户会要提醒、日历、重复任务。
- 你做了图片，用户会要标注、压缩、编辑、OCR。
- 你做了剪贴板历史，用户会要搜索、黑名单、跨设备同步。
- 你做了固定内容，用户会要分类、标签、排序、导入导出。

这些不是不能做，而是不应该进入第一版。

### 2.3 隐私判断

DeskBox 是开源、本地运行、无联网需求的产品，所以这里不是“数据上传风险”。

真正需要处理的是用户感知风险：用户可能没有意识到剪贴板里有验证码、Token、客户资料、聊天截图、地址等敏感内容。即使只保存本地，也必须让用户明确知道“DeskBox 正在记录剪贴板”。

因此：

- 随手记功能格子必须有总开关。
- 自动记录剪贴板必须是二级开关，默认关闭。
- 手动输入和固定内容可以先做，不需要启用剪贴板监听。
- 用户关闭功能后，必须停止所有监听、计时器和后台处理。

## 3. 功能开关与性能原则

### 3.1 开关设计

建议增加两个层级的开关：

1. 功能总开关：`Enable Quick Capture Widget`

   位置：设置 -> 功能 / 格子类型，或设置 -> 格子布局下新增“功能格子”。  
   默认：关闭。  
   行为：关闭时不创建随手记格子、不恢复随手记窗口、不启动剪贴板监听服务。

2. 剪贴板记录开关：`Record recent clipboard items`

   位置：随手记设置页内。  
   默认：关闭。  
   行为：只有功能总开关开启，并且剪贴板记录开关开启时，才订阅剪贴板变化事件。

### 3.2 关闭时的性能要求

关闭随手记后必须满足：

- 不订阅 `Clipboard.ContentChanged`。
- 不生成图片缩略图。
- 不启动后台扫描任务。
- 不读取随手记数据文件，除非用户打开设置页查看。
- 不创建 QuickCapture WidgetWindow。
- F7 逻辑不遍历随手记内容。

### 3.3 开启时的性能约束

即使开启，也要限制成本：

- 最近剪贴板默认最多 30 条。
- 文本内容单条建议限制 20 KB，超过则截断预览但保留原文，或提示用户手动保存。
- 图片第一版不做；未来做图片时要限制原图保存大小和缩略图尺寸。
- 写盘采用 debounce，例如 300-800 ms 合并保存。
- UI 列表只渲染可见项，避免大量内容卡顿。

## 4. 当前版本开发方案

当前版本目标：验证用户是否愿意把轻内容放进 DeskBox，并频繁复制/固定。

### 4.1 当前版本范围

P0 只做：

- 新增一个随手记功能格子。
- 用户手动输入文本或链接。
- 内容保存到“记录”。
- 内容可以固定。
- 内容可以取消固定。
- 内容可以删除。
- 点击内容主体复制原文。
- 右键菜单包含复制、编辑、固定/取消固定、删除。
- 本地持久化。
- 设置里提供随手记总开关。

当前版本不做：

- 自动剪贴板历史。
- 图片剪贴板。
- 截图保存。
- 待办识别。
- 拖出图片。
- 链接标题自动抓取。
- 多视图 hover 自动切换。
- 工作包。

### 4.2 当前版本交互

推荐第一版只保留两个视图：

```text
[ 记录 ] [ 固定 ]
```

不做“最近”，因为最近剪贴板需要监听剪贴板，会带来开关、隐私感知、重复内容过滤和性能问题。先把手动记录闭环做好。

默认布局：

```text
随手记                                      ···
[ 记录 ] [ 固定 ]

[ 记点什么...                         + ]

内容列表
- 客户地址...
- https://example.com
- 发票抬头...
```

主动作：

- 输入框按 Enter 保存为记录。
- 单击内容主体复制。
- 悬停内容行显示复制、固定、删除。
- 右键显示完整菜单。
- 固定内容在“固定”视图展示。

空状态文案：

```text
输入一句话，之后可以快速复制。
```

### 4.3 当前版本设置

新增设置项：

- 启用随手记功能格子。
- 创建/显示随手记格子。
- 清空随手记数据。

不建议第一版把设置项做太多。功能不复杂时，设置也要克制。

### 4.4 当前版本验收标准

- 关闭随手记开关后，启动应用不会创建随手记窗口。
- 开启开关后，托盘菜单或设置页可以创建随手记格子。
- 输入文本后按 Enter，内容出现在记录列表。
- 点击内容项后，原文进入剪贴板。
- 固定一条内容后，它出现在固定视图。
- 删除内容后，重启应用不会恢复。
- F7 临时置顶时，随手记格子和文件格子行为一致。

## 5. 下一版本开发方案

下一版本目标：加入“最近复制内容”，但必须显式开启。

### 5.1 新增范围

- 最近视图。
- 剪贴板文本记录。
- 剪贴板链接识别。
- 最近内容上限，默认 30 条。
- 暂停记录。
- 清空最近。
- 保存最近内容到记录。
- 固定最近内容。

不做图片，图片放到后续版本。

### 5.2 剪贴板记录行为

剪贴板记录开启后：

- 使用 Windows 原生 `Clipboard.ContentChanged`。
- 只读取文本和 URL。
- 忽略空内容。
- 忽略和上一条完全相同的内容。
- 忽略 DeskBox 自己刚写入剪贴板的内容，避免用户点击复制后又生成一条记录。
- 最近内容只保存本地。

### 5.3 最近视图

布局：

```text
随手记                                      ···
[ 记录 12 ] [ 固定 5 ] [ 最近 8 ]

最近
- 刚复制的一段文字                  14:32
- https://example.com              14:20
```

动作：

- 点击复制。
- 保存到记录。
- 固定。
- 删除。
- 清空最近。

### 5.4 二次确认

首次打开剪贴板记录开关时，需要显示清楚的本地提示：

```text
DeskBox 会在本机记录最近复制的文字和链接，用于在随手记中快速找回。
数据不会上传。你可以随时暂停或清空。
```

这不是因为产品联网，而是因为用户需要知道剪贴板会被记录。

## 6. 未来版本路线

### 6.1 V3：图片与截图

目标：支持截图和复制图片的临时保存。

范围：

- 剪贴板图片记录开关。
- 图片缩略图。
- 复制图片。
- 删除图片。
- 清空图片缓存。

注意：

- 图片容易占空间，必须有大小限制。
- 缩略图异步生成。
- 原图建议保存到 `%LocalAppData%\DeskBox\data\quick-capture\images`。
- 元数据和图片文件分离。

### 6.2 V4：拖入拖出

目标：让随手记真正参与跨应用操作。

范围：

- 文本拖入创建记录。
- 链接拖入创建链接记录。
- 文本拖出为 Unicode Text。
- 链接拖出默认为 URL。
- 多选拖出合并文本。

注意：

- 不要让文件拖入随手记变成文件收纳。
- 文件继续交给文件格子。

### 6.3 V5：轻待办

目标：处理“很轻的一句话任务”，不做完整待办软件。

范围：

- 内容项可标记为待办。
- 待办可完成/取消完成。
- 简单规则提示“可能是待办”。

不做：

- 日历。
- 提醒。
- 重复任务。
- 多级项目。

### 6.4 V6：工作包

工作包比随手记更重，建议确认随手记被用户使用后再做。

工作包目标是组合“文件 + 文本 + 链接 + 检查项”，和文件上传场景强相关。它更接近 F7 内容工作台，不应该混进随手记第一阶段。

## 7. 技术架构

### 7.1 当前代码现状

当前代码中：

- `WidgetKind` 只有 `File`，`Productivity` 是旧值迁移用。
- `SettingsService.NormalizeWidgetContentSettings` 会移除旧的 `Productivity` 并把非 File 的 widget 改回 File。
- `WidgetManager.RestoreWidgetsAsync` 只恢复 `WidgetKind.File`。
- `WidgetViewModel` 和 `WidgetWindow` 都强绑定文件格子逻辑。

因此不能直接把随手记硬塞进现有 `WidgetViewModel`。更合理的是保留现有文件格子不动，新增一套轻内容 ViewModel / Service / Store。

### 7.2 推荐模块

新增模型：

```text
Models/QuickCaptureItem.cs
Models/QuickCaptureSettings.cs
Models/QuickCaptureStoreData.cs
```

新增服务：

```text
Services/QuickCaptureService.cs
Services/QuickCaptureStore.cs
Services/ClipboardCaptureService.cs
```

新增视图模型：

```text
ViewModels/QuickCaptureWidgetViewModel.cs
```

新增窗口：

```text
Views/QuickCaptureWidgetWindow.xaml
Views/QuickCaptureWidgetWindow.xaml.cs
```

如果暂时不想新增 XAML，也可以先用 code-behind 创建 UI，但长期建议 XAML 化，避免 `WidgetWindow.xaml.cs` 继续膨胀。

### 7.3 WidgetKind 扩展

新增：

```csharp
public enum WidgetKind
{
    File,
    QuickCapture,
    Productivity
}
```

同时修改：

- `SettingsService.NormalizeWidgetContentSettings`：保留 `QuickCapture`，不要改回 File。
- `WidgetManager.RestoreWidgetsAsync`：按 WidgetKind 分发创建。
- `ShowWidgetAsync`：允许显示 QuickCapture。
- 托盘菜单：新增“新建随手记”或“显示随手记”，但仅在功能开关开启时展示。

### 7.4 窗口外壳复用策略

短期：

- 新建 `QuickCaptureWidgetWindow`。
- 复制/复用必要的窗口层级、位置、F7 显隐、锁定大小、主题处理逻辑。
- 不复用文件拖拽和文件右键菜单逻辑。

中期：

- 抽出 `WidgetWindowShell` 或 `WidgetChromeController`。
- 文件格子和随手记共享窗口边框、标题栏、缩放、层级、动画。
- 内容区域由不同控件承载。

不建议当前就大规模重构 `WidgetWindow.xaml.cs`，风险太大。

### 7.5 数据模型

第一版可以使用：

```csharp
public sealed class QuickCaptureItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public QuickCaptureItemType Type { get; set; } = QuickCaptureItemType.Text;
    public string Body { get; set; } = string.Empty;
    public string? Url { get; set; }
    public bool IsPinned { get; set; }
    public bool IsDeleted { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum QuickCaptureItemType
{
    Text,
    Link,
    Image,
    Todo
}
```

第一版实际只用 `Text` 和 `Link`，保留枚举扩展空间。

### 7.6 存储方案

当前版本推荐 JSON：

```text
%LocalAppData%\DeskBox\data\quick-capture\quick-capture.json
```

原因：

- 数据量很小。
- 易调试。
- 开源用户容易理解和备份。
- 不需要新增依赖。

写入策略：

- 服务内维护内存列表。
- 修改后 debounce 保存。
- 保存时写临时文件，再原子替换，避免崩溃导致文件损坏。

未来版本如果加入大量剪贴板历史、图片索引、搜索，可以迁移到 SQLite。

### 7.7 剪贴板服务

`ClipboardCaptureService` 只在以下条件全部满足时启动：

- 功能总开关开启。
- 剪贴板记录开关开启。
- 应用已经初始化完成。

服务行为：

- 订阅 `Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged`。
- 读取 `Clipboard.GetContent()`。
- 优先处理 `StandardDataFormats.Text` 和 WebLink。
- DeskBox 主动复制内容时设置短时间 ignore token，避免自触发。
- 失败时记录日志，不打扰用户。

## 8. 三方库策略

### 8.1 当前版本不需要新增三方库

当前版本只做手动文本/链接记录时，不需要引入新库。

可使用现有能力：

- WinUI 3 控件。
- `Windows.ApplicationModel.DataTransfer.Clipboard`。
- `System.Text.Json`。
- 现有 `CommunityToolkit.Mvvm`。
- 现有窗口和主题服务。

### 8.2 剪贴板监听不需要第三方库

Windows 官方 Clipboard API 已支持剪贴板变化事件。  
参考：<https://learn.microsoft.com/en-us/windows/apps/develop/communication/copy-and-paste>

因此不要引入剪贴板管理器类库。剪贴板是系统级敏感能力，引入第三方库反而增加不可控行为和调试成本。

### 8.3 数据库存储的未来选择

当前版本用 JSON。

未来如果满足任一条件，可以考虑 SQLite：

- 最近内容超过几百条。
- 需要全文搜索。
- 需要图片元数据和清理策略。
- 需要更可靠的增量写入。

如果用 SQLite，优先考虑 `Microsoft.Data.Sqlite`。它是 Microsoft 维护的轻量 ADO.NET SQLite provider。  
参考：<https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/>

不建议第一版上 EF Core。EF Core 对这个场景偏重，会增加包体、迁移复杂度和调试成本。

### 8.4 图片缩略图不建议引入第三方库

未来图片支持优先使用 WinUI / WinRT / WIC 能力生成缩略图。除非遇到格式兼容问题，不建议引入 ImageSharp、SkiaSharp 等库。

原因：

- 包体增大。
- 原生 Windows 图片格式已经覆盖常见截图和剪贴板图片。
- DeskBox 不是图片编辑器。

### 8.5 搜索不建议第一版引入搜索库

第一版数据量小，直接内存过滤即可。

未来如果需要中文分词、模糊搜索、全文搜索，再考虑 SQLite FTS 或轻量索引。不要第一版引入 Lucene 这类重库。

## 9. UI 设计方案

### 9.1 视觉原则

- 和现有文件格子一致，不做新的品牌风格。
- 密度比便签高，比表格轻。
- 不做大卡片墙。
- 不使用大面积彩色标签。
- 主体是列表，每行一到两行预览。

### 9.2 当前版本布局

```text
┌────────────────────────────┐
│ 随手记                  ··· │
│ [记录] [固定]              │
│ ┌────────────────────────┐ │
│ │ 记点什么...            │ │
│ └────────────────────────┘ │
│                            │
│ 客户地址...             ⧉  │
│ 发票信息...             ★  │
│ https://example.com      ⧉  │
└────────────────────────────┘
```

### 9.3 交互原则

- 单击内容主体：复制。
- 双击内容主体：编辑。
- Enter：保存输入。
- Esc：取消编辑或清空输入焦点。
- Delete：删除选中项。
- 右键菜单：复制、编辑、固定/取消固定、删除。

### 9.4 设置入口

建议新增一个“功能格子”或“实验功能”分组：

```text
随手记
记录文本、链接和常用内容。
[启用]

剪贴板记录
启用后记录最近复制的文本和链接。
[关闭]
```

如果暂时不想新增设置分组，可以放在“格子布局”下面，但长期建议独立出来。因为随手记不是文件格子的布局设置，而是一个功能模块。

## 10. 和插件系统的关系

当前不建议把随手记做成插件。

原因：

- 它需要和 F7 唤起、窗口层级、托盘菜单、剪贴板、设置、持久化深度集成。
- 插件边界还没有稳定。
- 如果先按插件做，早期会把大量基础能力抽象化，开发慢且风险高。

推荐做法：

- 随手记先做内置功能格子。
- 开发过程中沉淀 `WidgetShell`、`WidgetContentHost`、`IWidgetContentViewModel` 等内部接口。
- 等内部接口稳定后，再考虑把计算器、待办、剪贴板、笔记等扩展成插件能力。

## 11. 风险与应对

### 11.1 产品膨胀

风险：用户希望它变成完整笔记、待办、剪贴板管理器。

应对：

- 第一版只做记录和固定。
- 文案避免“笔记本”“任务管理”。
- 命名强调“随手记”或“Quick Capture”。

### 11.2 性能占用

风险：用户不需要却创建窗口、监听剪贴板、读写数据。

应对：

- 总开关默认关闭。
- 剪贴板记录默认关闭。
- 关闭时停止服务。
- 最近条数限制。

### 11.3 UI 复杂

风险：三个视图、hover 标签、图片、待办一起上会显得混乱。

应对：

- 当前版本只做两个视图：记录、固定。
- 最近视图下一版再加。
- hover 自动切换暂缓，先用点击切换。

### 11.4 技术债

风险：继续往 `WidgetWindow.xaml.cs` 塞逻辑，文件变得更难维护。

应对：

- 随手记单独窗口和 ViewModel。
- 中期抽 `WidgetShell`。
- 不在文件格子的 ViewModel 中混入轻内容逻辑。

## 12. 推荐实施顺序

### Step 1：准备模型和设置

- 增加 `WidgetKind.QuickCapture`。
- 增加功能总开关。
- 增加 QuickCapture 数据模型。
- 增加 JSON store。

### Step 2：创建窗口和基础 UI

- 新增 `QuickCaptureWidgetWindow`。
- 新增 `QuickCaptureWidgetViewModel`。
- 支持记录/固定切换。
- 支持输入、保存、复制、删除、固定。

### Step 3：接入 WidgetManager

- 创建随手记格子。
- 恢复随手记格子。
- F7 临时置顶包含随手记。
- 托盘菜单显示/隐藏随手记。

### Step 4：设置与数据维护

- 设置页启用/禁用。
- 清空随手记数据。
- 关闭功能后停止恢复窗口。

### Step 5：验证使用价值

重点观察：

- 用户是否主动创建内容。
- 用户是否频繁点击复制。
- 固定内容是否被使用。
- 用户是否抱怨占空间或打扰。

只有这些指标成立，再进入剪贴板历史。

## 13. 最终建议

可以做，但第一版要非常克制。

推荐当前版本目标：

```text
一个可关闭的随手记格子。
只记录用户主动输入的文本和链接。
可以固定常用内容。
点击即可复制。
```

不要一开始就做：

```text
自动剪贴板历史、图片、待办识别、工作包、AI、复杂拖拽。
```

这个功能的成败不取决于功能数量，而取决于用户是否形成一个很短的肌肉记忆：

```text
想到一句话 -> 放进 DeskBox。
要用一句话 -> 从 DeskBox 点一下复制。
常用一句话 -> 固定在 DeskBox。
```

如果这个闭环成立，随手记值得继续做；如果这个闭环都不成立，后面的剪贴板、图片、工作包都不应该继续投入。
