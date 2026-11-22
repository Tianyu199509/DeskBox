namespace DeskBox.Services;

/// <summary>
/// Token-compact structural archetypes and layout laws injected only on initial creation
/// or explicit layout/tab refactors (skipped on routine iterative refinement turns and Remix).
/// </summary>
public static class CustomWidgetArchetypeCatalog
{
    public static string SelectPromptBlueprintBlock(
        string? currentManifestJson,
        string? userMessage,
        bool isChinese)
    {
        bool isBlankCreation = string.IsNullOrWhiteSpace(currentManifestJson);
        bool isExplicitLayoutRefactor = !isBlankCreation && RequestsStructuralRebuild(userMessage);
        if (!isBlankCreation && !isExplicitLayoutRefactor)
        {
            return string.Empty;
        }

        return isChinese ? BuildChineseBlock() : BuildEnglishBlock();
    }

    private static bool RequestsStructuralRebuild(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return false;
        }

        string[] keywords =
        [
            "重构", "重做", "重排", "推翻", "重新布局", "改成标签页", "分页", "分栏",
            "课表", "课程表", "排班", "矩阵", "棋盘", "宫格",
            "rebuild", "redesign", "refactor layout", "switch to tabs", "split layout", "bento",
            "timetable", "schedule", "matrix"
        ];
        foreach (string keyword in keywords)
        {
            if (userMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildChineseBlock() =>
        """
        ## 原型蓝图与 340px 三层容纳铁律
        1. **选型六原型（选 1 个作为骨架，勿堆砌）**：
           - `glance-telemetry`（指标速览）：主数值 `Text(hero)` + `Progress`/`RingGauge` + 至多 1 个 `Sparkline`/2 列 KPI。
           - `interactive-dial-stage`（中心表盘交互）：居中 `RadialGauge`/`Canvas` + 模式 `Segmented` + 主操作行。
           - `capture-feed-popover`（采集列表 + 详情弹层）：顶部精简输入/搜索 + `List`/`ItemsGrid`（主列表≤3~4项高可见） + `Button.popover` 把复杂表单放进 `popovers`。
           - `workbench-utility`（工具台）：`Tabs`（≤3页）或紧凑参数区 + `Card` 结果区 + 执行/复制栏。
           - `multi-module-tabs`（模块化仪表盘）：`ModuleStack(bind: "settings.modules")` 或 `Tabs` 分区呈现。
           - `bento-launcher-grid`（快捷网格）：2~3 列 `Grid` + 语义 `Card.tone` 一键触发/跳转。
        2. **340px 三层容纳铁律**：① 全树最多 2 层 `Card` 嵌套（严禁 Card 套 Card 套 Card）；② 含中文的多列 `Grid`/`ItemsGrid` 在 340px 下至多 3 列，确保每列净字宽 ≥ 28px；③ 默认总高贴合 `defaultSize.height - 38`，超长区域用 Root `List` 或 `Scroll`；④ 复杂二级配置/编辑表单入 `popovers`，勿全摊在主画布；⑤ 严禁容器硬编码渐变/圆角/不透明背景。
        3. **原生高频数据优先**：涉及待办、剪贴板或农历节假日时优先直连原生 `sources`：`todo`（自动填充 `todos` 集合 + `$todo.totalCount|pendingCount|completedCount|completionPercent|nextTaskTitle|summary`，配合原生动作 `todo.add` / `todo.toggle` / `todo.remove` / `todo.cyclePriority` / `todo.clearCompleted` 直接双向同步宿主待办存储）、`clipboard`（`$clipboard.text|preview|length|lines` + `clipboard.copy|captureImage`）或 `calendar`（`$calendar.lunar|lunarTitle|festival|subtitle|weekday|today`），无需重复造空壳数据源。
        4. **二维网格/课表矩阵唯一正交范式**：严禁写 `"children": "${$computed.xxx}"`（所有容器 `children` 必须是元素 ID 字符串静态数组！），也严禁手写数十个重复格子节点。凡 $N \times M$ 课表、排班、日历、棋盘矩阵，统一用 `"type": "ItemsGrid"` + 整数列数 `"columns": 5`（若需超过 5 个含字列请将 `defaultSize.width` 设为 `420` 避免中文挤压）+ `"spacing": 4` + `"items": "${$computed.cells}"`（由 `logic.js` `onInit` 生成扁平化单元格对象数组）；在唯一 `"itemTemplate"` 内用紧凑 `"padding": "4,3,4,3"` 与互斥 `"when": "${$item.isEmpty == true}"` / `"when": "${$item.isEmpty != true}"` 切换空位 `+` 按钮与有课色块卡片，表单弹层定义在顶层 `"popovers"`（勿内联塞进 `elements` 子树），集合读写统一用 `ctx.collections.get('...')` / `append` / `update` / `remove`。
        """;

    private static string BuildEnglishBlock() =>
        """
        ## Archetype Blueprints & 340px 3-Tier Containment Law
        1. **Pick 1 Structural Archetype**:
           - `glance-telemetry`: `Text(hero)` + `Progress`/`RingGauge` + single `Sparkline` or 2-col KPI row.
           - `interactive-dial-stage`: Centered `RadialGauge`/`Canvas` + mode `Segmented` + primary action bar.
           - `capture-feed-popover`: Compact quick-add/filter + `List`/`ItemsGrid` + complex forms offloaded into `popovers` via `Button.popover`.
           - `workbench-utility`: `Tabs` (<=3 tabs) or compact input row + result `Card` + copy/run footer.
           - `multi-module-tabs`: `ModuleStack(bind: "settings.modules")` or `Tabs` modular sections.
           - `bento-launcher-grid`: 2–3 column `Grid` of semantic `Card.tone` tiles with 1-click actions.
        2. **340px Containment Law**: (a) Max 2 nested `Card` levels; (b) Keep text-bearing `Grid`/`ItemsGrid` <= 3 columns at 340px width (>= 28px net label width); (c) Keep static body within `defaultSize.height - 38`, or wrap dynamic feeds in `List`/`Scroll`; (d) Move secondary editors/details to `popovers`; (e) Never emit container backgrounds, gradients, or Card corner radii.
        3. **Native Personal Capabilities First**: For tasks, live clipboard, or lunar/holiday calendar data, prefer built-in `sources`: `todo` (populates the `todos` collection + `$todo.totalCount|pendingCount|completedCount|completionPercent|nextTaskTitle|summary`, mutated directly via `todo.add`, `todo.toggle`, `todo.remove`, `todo.cyclePriority`, and `todo.clearCompleted`), `clipboard` (`$clipboard.text|preview|length|lines` + `clipboard.copy|captureImage`), or `calendar` (`$calendar.lunar|lunarTitle|festival|subtitle|weekday|today`).
        4. **Canonical 2D Matrix / Timetable Pattern**: NEVER bind a template string to `"children"` (`"children"` MUST be a static JSON array of element ID strings) and NEVER hand-expand dozens of static grid cells. For any $N \times M$ timetable, roster, calendar, or board, use `"type": "ItemsGrid"` with integer `"columns": 5` (or widen `defaultSize.width` to `420` when >5 text columns are needed), `"spacing": 4`, `"items": "${$computed.cells}"` (flattened cell array generated in `logic.js` `onInit`), and a single `"itemTemplate"` (compact cell `"padding": "4,3,4,3"`) with mutually exclusive `"when": "${$item.isEmpty == true}"` / `"when": "${$item.isEmpty != true}"` branches for filled cards vs. empty `+` buttons. Declare `"popovers"` at manifest root (never inline inside `elements`) and mutate collections strictly via `ctx.collections.get(...)` / `append` / `update` / `remove`.
        """;
}
