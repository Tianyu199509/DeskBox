using System.Text;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetSkillCard(
    string Id,
    string Title,
    string FileName,
    IReadOnlyList<string> TriggerNodeTypes,
    IReadOnlyList<string> TriggerActionOrSourceTypes,
    IReadOnlyList<string> TriggerKeywords,
    string MarkdownContent);

/// <summary>
/// Modular WinUI 3 component &amp; UX pattern recipe catalog shared across built-in Direct API Studio,
/// Web AI clipboard prompts, and external coding agents (<c>references/*.md</c>).
/// Uses deterministic-first selection (existing AST primitives first, then user intent keywords).
/// </summary>
public static class CustomWidgetSkillCatalog
{
    public const int DefaultMaxSelectedCards = 3;

    public static IReadOnlyList<CustomWidgetSkillCard> AllCards { get; } =
    [
        new(
            Id: "forms-and-inputs",
            Title: "WinUI Native Forms, Compact Controls & Steppers",
            FileName: "forms-and-inputs.md",
            TriggerNodeTypes: ["Input", "TextArea", "Select", "CheckBox", "Slider", "NumberBox", "IconButton", "Toggle"],
            TriggerActionOrSourceTypes: ["state.set", "state.toggle", "state.increment"],
            TriggerKeywords:
            [
                "输入", "表单", "下拉", "选择", "复选", "勾选", "滑块", "滑动", "步进", "数字", "多行", "备注", "图标按钮", "计数器", "目标", "预算", "打分",
                "input", "form", "select", "dropdown", "combobox", "checkbox", "slider", "number", "stepper", "textarea", "multiline", "note", "counter", "goal", "iconbutton"
            ],
            MarkdownContent: """
                # Skill: WinUI Native Forms, Compact Controls & Steppers

                ## Component Signatures & Binding Rules
                - `Input` (Single-line): `{ "type": "Input", "bind": "state.draft", "placeholder": "...", "onSubmit": { ... } }`
                - `TextArea` (Multi-line, `Ctrl+Enter` submits): `{ "type": "TextArea", "bind": "state.note", "placeholder": "...", "minHeight": 72, "maxHeight": 150, "onSubmit": { ... } }`
                - `Select` (Fluent `ComboBox`): `{ "type": "Select", "title": "分类", "bind": "state.cat", "options": [{ "value": "work", "label": "工作" }, { "value": "life", "label": "生活" }] }`
                - `CheckBox` (Compact row): `{ "type": "CheckBox", "bind": "state.hideDone", "text": "隐藏已完成", "strikethrough": false }`
                - `Slider` (Live value + unit): `{ "type": "Slider", "title": "专注时长", "bind": "state.minutes", "min": 5, "max": 90, "step": 5, "unit": "m" }`
                - `NumberBox` (Inline spin buttons): `{ "type": "NumberBox", "title": "每次饮水", "bind": "state.stepMl", "min": 50, "max": 1000, "step": 50 }`
                - `IconButton` (24px/28px zero-clutter action): `{ "type": "IconButton", "icon": "\\uE710", "size": 28, "variant": "accent", "tooltip": "添加", "onClick": { ... } }` (`variant`: `"subtle"` | `"accent"` | `"danger"`)

                ## Layout Craft Rules
                1. **Inline Composer Bar**: Pair an `Input` (`column: 0` in `columns: "*,Auto"`) with an `IconButton` (`variant: "accent"`, `icon: "\\uE710"`) instead of a wide text button to save horizontal space.
                2. **Prefer `CheckBox` over `Toggle` inside dense lists**; reserve `Toggle` for top-level feature switches.
                3. **Declare every bound `state.<key>`** with a sensible initial string value in root `"state"`.

                ## Golden Snippet: Compact Entry Bar + Goal Slider
                ```json
                {
                  "type": "Stack",
                  "spacing": 8,
                  "children": [
                    {
                      "type": "Grid",
                      "columns": "*,Auto,Auto",
                      "spacing": 6,
                      "children": [
                        { "type": "Input", "column": 0, "bind": "state.draft", "placeholder": "添加新事项…" },
                        {
                          "type": "Select",
                          "column": 1,
                          "bind": "state.priority",
                          "options": [
                            { "value": "p1", "label": "高优" },
                            { "value": "p2", "label": "常规" }
                          ]
                        },
                        {
                          "type": "IconButton",
                          "column": 2,
                          "icon": "\uE710",
                          "variant": "accent",
                          "tooltip": "添加",
                          "onClick": {
                            "type": "collection.prepend",
                            "guard": "${state.draft != ''}",
                            "collection": "items",
                            "item": { "title": "${state.draft}", "priority": "${state.priority}", "done": "false" },
                            "then": [{ "type": "state.set", "key": "draft", "value": "" }]
                          }
                        }
                      ]
                    },
                    { "type": "Slider", "title": "每日目标", "bind": "state.dailyGoal", "min": 1, "max": 12, "step": 1, "unit": " 项" }
                  ]
                }
                ```
                """),

        new(
            Id: "metrics-and-microcharts",
            Title: "Micro-Charts, Habit Heatmaps & Progress Telemetry",
            FileName: "metrics-and-microcharts.md",
            TriggerNodeTypes: ["Sparkline", "HeatmapGrid", "ProgressBar", "ProgressRing", "StatCard", "ProgressRow", "Expander"],
            TriggerActionOrSourceTypes: ["systemMetrics", "aiMonitor"],
            TriggerKeywords:
            [
                "图表", "折线", "趋势", "曲线", "热力图", "打卡", "习惯", "坚持", "连续", "统计", "进度", "仪表", "监控", "绿格子",
                "chart", "sparkline", "trend", "graph", "heatmap", "habit", "streak", "checkin", "progress", "metric", "dashboard", "stats"
            ],
            MarkdownContent: """
                # Skill: Micro-Charts, Habit Heatmaps & Progress Telemetry

                ## Component Signatures
                - `Sparkline` (Vector trend line + soft gradient area):
                  - From collection: `{ "type": "Sparkline", "title": "近 14 次记录趋势", "collection": "logs", "valueField": "amount", "unit": "ml", "height": 40, "color": "accent", "showArea": true, "showLatest": true }`
                  - From comma string / state: `{ "type": "Sparkline", "sparkline": "${state.series}", "height": 34, "color": "emerald" }`
                - `HeatmapGrid` (GitHub-style daily intensity grid):
                  - `{ "type": "HeatmapGrid", "title": "近 28 天打卡热力", "collection": "checkins", "valueField": "score", "days": 28, "size": 13, "color": "success" }`
                  - Aggregates records in `collection` automatically by `CreatedAtLocal` (or optional `dateField` `"yyyy-MM-dd"`).
                - `ProgressBar` / `ProgressRing`: `{ "type": "ProgressBar", "progress": "${state.current}", "max": 2000, "color": "cyan" }`
                - `Expander` (Progressive disclosure for secondary charts/history):
                  - `{ "type": "Expander", "title": "趋势与统计", "defaultExpanded": false, "tone": "subtle", "children": [ ... ] }`

                ## Visual Craft Rules
                1. **Semantic Tones Only**: Use `"accent"`, `"success"` / `"emerald"`, `"cyan"`, `"violet"`, `"warning"`, or `"danger"` for `color` — NEVER hardcode hex colors that clash with Light/Dark themes.
                2. **Pair Hero KPI + Micro-Chart**: Put big number (`style: "Metric"` or `"Title"`) on top, `Sparkline` or `HeatmapGrid` directly beneath inside a single `Card` (`tone: "subtle"`). Avoid nesting `Card` inside `Card`.

                ## Golden Snippet: Habit Streak + 28-Day Heatmap
                ```json
                {
                  "type": "Card",
                  "tone": "subtle",
                  "padding": "12,10",
                  "children": [
                    {
                      "type": "Grid",
                      "columns": "*,Auto",
                      "children": [
                        {
                          "type": "Stack",
                          "spacing": 2,
                          "children": [
                            { "type": "Text", "text": "每日阅读打卡", "style": "BodyStrong" },
                            { "type": "Text", "text": "累计 ${collections.logs.count} 次记录", "style": "Caption", "color": "secondary" }
                          ]
                        },
                        {
                          "type": "Button",
                          "column": 1,
                          "text": "+1 打卡",
                          "variant": "accent",
                          "onClick": {
                            "type": "collection.prepend",
                            "collection": "logs",
                            "item": { "score": "1", "note": "已完成" }
                          }
                        }
                      ]
                    },
                    { "type": "HeatmapGrid", "collection": "logs", "valueField": "score", "days": 28, "color": "success" }
                  ]
                }
                ```
                """),

        new(
            Id: "collections-and-filtering",
            Title: "Persistent Lists, Filtering, Checklists & Item Actions",
            FileName: "collections-and-filtering.md",
            TriggerNodeTypes: ["List", "Segmented", "EmptyState"],
            TriggerActionOrSourceTypes: ["collection.prepend", "collection.append", "collection.update", "collection.remove", "collection.clear", "collection.exportMarkdown"],
            TriggerKeywords:
            [
                "列表", "清单", "待办", "任务", "筛选", "分类", "归档", "记录", "日志", "备忘", "书签", "速记", "删除", "导出",
                "list", "todo", "task", "checklist", "filter", "tab", "collection", "log", "bookmark", "snippet", "export"
            ],
            MarkdownContent: """
                # Skill: Persistent Lists, Filtering, Checklists & Item Actions

                ## Key Rules for `collections` & `List`
                1. **Always declare root `"collections"`**: e.g. `"collections": { "items": { "maxItems": 100, "sort": "newestFirst" } }`.
                2. **Built-in Record Fields**: Every item in `itemTemplate` / `hoverActions` automatically exposes `${$item.id}`, `${$item.timeText}`, `${$item.dateText}`, `${$item.isToday}`, plus custom fields (`${$item.title}`, `${$item.done}`, etc.).
                3. **Filtering**: Use `Segmented` (for 2–4 short tabs) or `Select` (for 4+ categories) bound to `state.filter`, then on `List` set `"filter": "${state.filter == 'all' || $item.status == state.filter}"`.
                4. **Row Actions**: Place compact `IconButton`s inside `hoverActions` (`icon: "\\uE74D"`, `variant: "danger"`, `size: 24`) so rows stay clean at rest and reveal actions on hover.
                5. **Root Flex Scrolling**: When `List` is a direct child of root vertical `Stack`, it automatically scrolls while keeping header/composer pinned! Do NOT set `maxLines` on the main scrolling `List` unless building a fixed preview summary.

                ## Golden Snippet: Filterable Checklist with Strikethrough & Hover Delete
                ```json
                {
                  "type": "List",
                  "items": "items",
                  "filter": "${state.tab == 'all' || (state.tab == 'open' && $item.done != 'true')}",
                  "emptyState": { "icon": "\uE73E", "title": "全部清空啦", "subtitle": "上方输入即可新增条目" },
                  "itemTemplate": {
                    "type": "CheckBox",
                    "value": "${$item.done}",
                    "text": "${$item.title}",
                    "strikethrough": true,
                    "onToggle": {
                      "type": "collection.update",
                      "collection": "items",
                      "targetId": "${$item.id}",
                      "item": { "done": "${$item.done == 'true' ? 'false' : 'true'}" }
                    }
                  },
                  "hoverActions": [
                    {
                      "type": "IconButton",
                      "icon": "\uE74D",
                      "size": 24,
                      "variant": "danger",
                      "tooltip": "删除",
                      "onClick": { "type": "collection.remove", "collection": "items", "targetId": "${$item.id}" }
                    }
                  ]
                }
                ```
                """),

        new(
            Id: "live-sources-and-drops",
            Title: "HTTP JSON / RSS Live Feeds, Timers & File Drop Zones",
            FileName: "live-sources-and-drops.md",
            TriggerNodeTypes: ["FileChip", "PomodoroCard", "CountdownHero", "CountdownList"],
            TriggerActionOrSourceTypes: ["httpJson", "rss", "source.refresh", "refreshSource", "file.open", "file.reveal", "pomodoro", "countdown"],
            TriggerKeywords:
            [
                "接口", "网络", "订阅", "新闻", "热榜", "天气", "汇率", "行情", "股价", "拖入", "拖拽", "文件", "番茄钟", "倒计时",
                "api", "http", "json", "rss", "feed", "news", "weather", "price", "drop", "file", "pomodoro", "countdown", "timer"
            ],
            MarkdownContent: """
                # Skill: HTTP JSON / RSS Live Feeds, Timers & File Drop Zones

                ## Live Network `sources` (`httpJson` / `rss`)
                - Declare in root `"sources"`:
                  ```json
                  {
                    "id": "feed",
                    "type": "httpJson",
                    "url": "https://api.example.com/data",
                    "intervalMs": 300000,
                    "stateMap": { "price": "$.data.amount", "updated": "$.data.time" },
                    "targetCollection": "entries",
                    "itemsPath": "$.data.list",
                    "fieldMap": { "title": "name", "url": "link" },
                    "maxItems": 20
                  }
                  ```
                - Runtime auto-populates `state.sourceStatus` (`"ok"` | `"loading"` | `"error"`), `state.sourceError`, `state.lastUpdated`.
                - Add a subtle header `IconButton` (`icon: "\\uE72C"`, `onClick: { "type": "source.refresh" }`) for manual refresh.

                ## Desktop File Drop (`onDrop`)
                - Root `"onDrop"`: `{ "accept": "files", "mode": "link", "actions": [ ... ] }`.
                - Drop context tokens: `${$drop.name}`, `${$drop.path}`, `${$drop.ext}`, `${$drop.storage}`, and `${$drop.text}` (safe first 8 KB UTF-8 plain text for `.txt`/`.md`/`.csv`/`.json`/`.log`/`.yml`/`.xml`).
                - Render saved file references with `{ "type": "FileChip", "fileName": "${item.name}", "filePath": "${item.path}" }`.
                """),

        new(
            Id: "ai-micro-workflows",
            Title: "Runtime AI Micro-Workflows (ai.run + Safe Text Extraction)",
            FileName: "ai-micro-workflows.md",
            TriggerNodeTypes: ["AiFocusCard", "AiRadarList", "AiTokensCard", "AiTrendCard"],
            TriggerActionOrSourceTypes: ["ai.run"],
            TriggerKeywords:
            [
                "智能", "提炼", "总结", "摘要", "拆解", "翻译", "润色", "分类", "萃取", "ai", "周报", "会议纪要",
                "summarize", "extract", "refine", "translate", "polish", "classify", "ai.run", "llm", "smart"
            ],
            MarkdownContent: """
                # Skill: Runtime AI Micro-Workflows (`ai.run` + `${$drop.text}`)

                ## How `"ai.run"` Works
                - Uses DeskBox's global `FastMicroTask` router (automatic non-thinking fast model, single-flight guard, 16 KB prompt cap).
                - Automatically manages `state.aiStatus` (`"idle"` | `"running"` | `"error"`), `state.aiError`, and `state.aiUpdatedAt`.
                - **Draft Safety Guarantee**: If API key is missing or request fails, `then` actions are skipped so user input in `state.draft` is NEVER lost! Always clear draft inside `"then": [{ "type": "state.set", "key": "draft", "value": "" }]`.

                ## Golden Snippet: AI Action + Non-Blocking Running Indicator
                ```json
                {
                  "type": "Stack",
                  "spacing": 6,
                  "children": [
                    {
                      "type": "TextArea",
                      "bind": "state.draft",
                      "placeholder": "粘贴零散想法或拖入 .md/.txt 文档，Ctrl+Enter 智能提炼…",
                      "minHeight": 64,
                      "onSubmit": {
                        "type": "ai.run",
                        "guard": "${state.draft != ''}",
                        "prompt": "提炼以下内容的核心一句话结论(summary)并拆解可执行事项(items: title, tag)：\n${state.draft}",
                        "stateMap": { "summary": "summary" },
                        "collection": "items",
                        "mode": "prepend",
                        "itemsPath": "items",
                        "fieldMap": { "title": "title", "tag": "tag" },
                        "maxItems": 8,
                        "then": [{ "type": "state.set", "key": "draft", "value": "" }]
                      }
                    },
                    {
                      "type": "Stack",
                      "visible": "${state.aiStatus == 'running'}",
                      "orientation": "horizontal",
                      "spacing": 8,
                      "children": [
                        { "type": "ProgressRing", "indeterminate": true, "size": 14 },
                        { "type": "Text", "text": "AI 正在提炼…", "style": "Caption", "color": "accent" }
                      ]
                    }
                  ]
                }
                ```
                """)
    ];

    public static string BuildCompactCoreComponentIndex() => """
        ## Component Quick Index (All Supported Schema v1 Nodes)
        - Containers: `Stack` (`orientation`: vertical|horizontal, `spacing`, `padding`), `Grid` (`columns`, `rows`, `column`, `row`), `Wrap` (`spacing`), `Scroll`, `Card` (`tone`: default|subtle|accent|warning|danger, max 2 nested levels!), `Expander` (`title`, `subtitle`, `defaultExpanded`, `bind`, `children`), `Spacer`, `Divider`.
        - Inputs & Controls: `Input` (`bind`, `placeholder`, `onSubmit`), `TextArea` (`bind`, `minHeight`, `maxHeight`, `onSubmit`), `Select` (`title`, `bind`, `options: [{value,label}]`, `onChange`), `CheckBox` (`bind`/`value`, `text`, `strikethrough`, `onToggle`), `Toggle` (`title`, `bind`, `onChange`), `Slider` (`title`, `bind`, `min`, `max`, `step`, `unit`), `NumberBox` (`title`, `bind`, `min`, `max`, `step`), `Segmented` (`bind`, `options`), `Button` (`text`, `icon`, `variant`: standard|accent|subtle|danger, `onClick`), `IconButton` (`icon`, `size`: 24|28, `variant`: subtle|accent|danger, `tooltip`, `onClick`).
        - Data & Visuals: `Text` (`text`, `style`: caption|body|bodyStrong|subtitle|title|hero|mono, `color`: default|secondary|muted|accent|emerald|cyan|violet|success|warning|danger), `Badge` (`text`, `tone`), `List` (`items`, `filter`, `emptyState`, `itemTemplate`, `hoverActions`), `Sparkline` (`collection`+`valueField` or `sparkline`, `height`, `color`, `showArea`, `showLatest`, `unit`), `HeatmapGrid` (`collection`, `valueField`, `dateField`, `days`, `color`), `ProgressBar` (`progress`, `max`, `color`), `ProgressRing` (`progress` or `indeterminate`, `size`), `KeyValueRow`, `ProgressRow`, `FileChip`, `EmptyState`.
        - Domain Presets: `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`.
        - Actions: `state.set`, `state.toggle`, `state.increment`, `collection.prepend`, `collection.append`, `collection.update`, `collection.remove`, `collection.clear`, `collection.exportMarkdown`, `ai.run`, `source.refresh`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `timer.*`, `pomodoro.*`, `countdown.*`.
        """;

    /// <summary>
    /// Deterministic-first skill card router:
    /// 1) Selects cards matching primitives already present in <paramref name="currentManifestJson"/>;
    /// 2) Fills remaining slots (up to <paramref name="maxCards"/>) by scoring <paramref name="userPrompt"/> + <paramref name="briefMarkdown"/>;
    /// 3) Falls back to core form + collection cards when creating from scratch with generic wording.
    /// </summary>
    public static IReadOnlyList<CustomWidgetSkillCard> SelectSkillsForTurn(
        string? currentManifestJson,
        string? userPrompt,
        string? briefMarkdown = null,
        int maxCards = DefaultMaxSelectedCards)
    {
        int cap = Math.Clamp(maxCards, 1, AllCards.Count);
        var selected = new List<CustomWidgetSkillCard>(cap);
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Deterministic AST scan of existing manifest
        if (!string.IsNullOrWhiteSpace(currentManifestJson))
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(currentManifestJson);
            if (parsed.Manifest is { } manifest)
            {
                var usedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedActionsAndSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectManifestPrimitives(manifest, usedNodes, usedActionsAndSources);

                var astMatches = AllCards
                    .Select(card => (
                        Card: card,
                        Hits: card.TriggerNodeTypes.Count(usedNodes.Contains) * 2 +
                              card.TriggerActionOrSourceTypes.Count(usedActionsAndSources.Contains) * 3))
                    .Where(x => x.Hits > 0)
                    .OrderByDescending(x => x.Hits)
                    .ToList();

                // Reserve at least 1 slot for new user intent when userPrompt is non-empty
                int astCap = !string.IsNullOrWhiteSpace(userPrompt) ? Math.Max(1, cap - 1) : cap;
                foreach (var match in astMatches)
                {
                    if (selected.Count >= astCap)
                    {
                        break;
                    }

                    if (selectedIds.Add(match.Card.Id))
                    {
                        selected.Add(match.Card);
                    }
                }
            }
        }

        // Pass 2: User intent + brief keyword scoring
        string combinedIntent = $"{userPrompt ?? string.Empty}\n{briefMarkdown ?? string.Empty}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(combinedIntent))
        {
            var intentMatches = AllCards
                .Where(c => !selectedIds.Contains(c.Id))
                .Select(card => (
                    Card: card,
                    Score: card.TriggerKeywords.Count(kw => combinedIntent.Contains(kw.ToLowerInvariant(), StringComparison.Ordinal))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var match in intentMatches)
            {
                if (selected.Count >= cap)
                {
                    break;
                }

                if (selectedIds.Add(match.Card.Id))
                {
                    selected.Add(match.Card);
                }
            }
        }

        // Pass 3: If we still have room and AST had extra matches, top up from AST
        if (selected.Count < cap && !string.IsNullOrWhiteSpace(currentManifestJson))
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(currentManifestJson);
            if (parsed.Manifest is { } manifest)
            {
                var usedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var usedActionsAndSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectManifestPrimitives(manifest, usedNodes, usedActionsAndSources);

                foreach (CustomWidgetSkillCard card in AllCards)
                {
                    if (selected.Count >= cap)
                    {
                        break;
                    }

                    bool hits = card.TriggerNodeTypes.Any(usedNodes.Contains) ||
                                card.TriggerActionOrSourceTypes.Any(usedActionsAndSources.Contains);
                    if (hits && selectedIds.Add(card.Id))
                    {
                        selected.Add(card);
                    }
                }
            }
        }

        // Pass 4: Generic scratch creation fallback
        if (selected.Count == 0)
        {
            selected.Add(AllCards[0]); // forms-and-inputs
            if (cap >= 2 && AllCards.Count > 2)
            {
                selected.Add(AllCards[2]); // collections-and-filtering
            }
        }

        return selected;
    }

    public static string FormatSelectedSkillsPromptBlock(IReadOnlyList<CustomWidgetSkillCard> cards)
    {
        if (cards.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Matched WinUI Component & Pattern Recipes (Follow These Patterns)");
        foreach (CustomWidgetSkillCard card in cards)
        {
            sb.AppendLine();
            sb.AppendLine(card.MarkdownContent.Trim());
        }
        return sb.ToString();
    }

    public static async Task WriteReferenceDocsAsync(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return;
        }

        string refsDir = Path.Combine(workspaceDirectory, "references");
        Directory.CreateDirectory(refsDir);

        foreach (CustomWidgetSkillCard card in AllCards)
        {
            string filePath = Path.Combine(refsDir, card.FileName);
            await File.WriteAllTextAsync(filePath, card.MarkdownContent.Trim() + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static void CollectManifestPrimitives(
        CustomWidgetManifest manifest,
        HashSet<string> usedNodes,
        HashSet<string> usedActionsAndSources)
    {
        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (!string.IsNullOrWhiteSpace(src.Type))
            {
                usedActionsAndSources.Add(src.Type);
            }
        }

        CollectActionTypes(manifest.OnDrop?.Actions, usedActionsAndSources);
        CollectNodePrimitives(manifest.View, usedNodes, usedActionsAndSources);
    }

    private static void CollectNodePrimitives(
        CustomWidgetNodeSpec? node,
        HashSet<string> usedNodes,
        HashSet<string> usedActionsAndSources)
    {
        if (node is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Type))
        {
            usedNodes.Add(node.Type);
        }

        CollectActionTypes(node.OnClick, usedActionsAndSources);
        CollectActionTypes(node.OnSubmit, usedActionsAndSources);
        CollectActionTypes(node.OnChange, usedActionsAndSources);
        CollectNodePrimitives(node.ItemTemplate, usedNodes, usedActionsAndSources);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectNodePrimitives(h, usedNodes, usedActionsAndSources);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectNodePrimitives(c, usedNodes, usedActionsAndSources);
        }
    }

    private static void CollectActionTypes(
        IReadOnlyList<CustomWidgetActionSpec>? actions,
        HashSet<string> usedActionsAndSources)
    {
        if (actions is null)
        {
            return;
        }

        foreach (CustomWidgetActionSpec act in actions)
        {
            if (!string.IsNullOrWhiteSpace(act.Type))
            {
                usedActionsAndSources.Add(act.Type);
            }
            CollectActionTypes(act.Then, usedActionsAndSources);
        }
    }
}
