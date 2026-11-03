using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public enum CustomWidgetCapabilityCategory
{
    DataSource = 0,
    Component = 1,
    Action = 2,
    ConfigAndSecurity = 3
}

public sealed record CustomWidgetApiMemberDoc(
    string Name,
    string Kind,
    string DescriptionZh,
    string DescriptionEn);

public sealed record CustomWidgetCapabilitySpec(
    string Id,
    string Emoji,
    string Glyph,
    CustomWidgetCapabilityCategory Category,
    string TitleZh,
    string TitleEn,
    string SummaryZh,
    string SummaryEn,
    IReadOnlyList<string> RequiredCapabilityIds,
    IReadOnlyList<string> TriggerNodeTypes,
    IReadOnlyList<string> TriggerSourceOrActionTypes,
    IReadOnlyList<string> TriggerTokenPrefixes,
    IReadOnlyList<string> TriggerKeywords,
    IReadOnlyList<CustomWidgetApiMemberDoc> Members,
    string CompactContractMarkdown,
    string GoldenSnippetJson)
{
    public string GetTitle(bool isChinese) => isChinese ? TitleZh : TitleEn;
    public string GetSummary(bool isChinese) => isChinese ? SummaryZh : SummaryEn;

    /// <summary>
    /// Culture-aware title. Resolves <c>CustomWidget.Cap.{Id}.Title</c> from the shared
    /// localization catalog (13 locales); the in-code Zh/En pair stays as the fallback
    /// when the key is missing. Key alignment is pinned by CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    public string GetTitle(string? cultureName)
    {
#if DESKBOX_STORE
        if (string.Equals(Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
        {
            return CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? TitleZh : TitleEn;
        }
#endif
        string key = $"CustomWidget.Cap.{Id}.Title";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? TitleZh : TitleEn);
    }

    public string GetSummary(string? cultureName)
    {
#if DESKBOX_STORE
        if (string.Equals(Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
        {
            return CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? SummaryZh : SummaryEn;
        }
#endif
        string key = $"CustomWidget.Cap.{Id}.Summary";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? SummaryZh : SummaryEn);
    }

    public string GetMemberDescription(CustomWidgetApiMemberDoc member, string? cultureName)
    {
        string key = $"CustomWidget.Cap.{Id}.Member.{CustomWidgetCapabilityRegistry.SlugifyKey(member.Name)}.Desc";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? member.DescriptionZh : member.DescriptionEn);
    }
}

/// <summary>
/// Single Source of Truth (SSOT) capability &amp; API registry for DeskBox Custom Widgets.
/// Powers:
/// 1) 3-Tier anti-hallucination prompt assembly (Tier 0 directory + Tier 1 atomic contracts + Must-Use context pins);
/// 2) AST-driven auto-repair capability backfill &amp; <c>$namespace.*</c> Did-You-Mean validation;
/// 3) Studio interactive <c>@</c> / <c>+</c> Capability Selector and <c>API_REFERENCE.md</c> / interactive API Reference dialog;
/// 4) External Agent MCP <c>deskbox_get_capability_spec</c> lookup.
/// </summary>
public static class CustomWidgetCapabilityRegistry
{
    /// <summary>
    /// Deterministic key slug shared by code and tests: lowercase, non-alphanumeric runs
    /// collapse to '-', trim, truncate to 48 chars, trim again; empty becomes "x".
    /// Must stay byte-compatible with CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    public static string SlugifyKey(string raw)
    {
        string s = raw.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        bool pendingDash = false;
        foreach (char c in s)
        {
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (alnum)
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }
                pendingDash = false;
                sb.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        string slug = sb.Length == 0 ? "x" : sb.ToString();
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        return slug.Length == 0 ? "x" : slug;
    }

    public const int DefaultAutoSelectedCap = 5;

    public static IReadOnlyList<CustomWidgetCapabilitySpec> All { get; } = BuildRegistry();

    private static readonly IReadOnlyDictionary<string, CustomWidgetCapabilitySpec> ById =
        All.ToDictionary(static c => c.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ValidNamespaceTokens =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["$sys"] =
            [
                "cpu.percent", "cpu.percentExact", "cpu.history",
                "memory.percent", "memory.usedGb", "memory.totalGb", "memory.summaryText", "memory.compactText", "memory.history",
                "gpu.name", "gpu.percent", "gpu.vramUsedGb", "gpu.vramTotalGb", "gpu.vramPercent", "gpu.vramSummary", "gpu.vramCompact", "gpu.history",
                "disk.percent", "disk.freeGb", "disk.totalGb", "disk.name", "disk.summary",
                "net.downText", "net.upText", "net.summaryText", "net.history",
                "battery.percent", "battery.hasBattery", "battery.statusText",
                "uptime.text", "status.label", "status.value"
            ],
            ["$weather"] =
            [
                "city", "temp", "tempExact", "tempText", "feelsLike", "feelsLikeText",
                "high", "low", "rangeText", "condition", "icon", "glyph",
                "humidity", "humidityValue", "wind", "windSpeed", "precipProb",
                "uv", "sunrise", "sunset", "updatedAt", "status", "error"
            ],
            ["$calendar"] =
            [
                "date", "today", "year", "month", "monthPadded", "day", "dayPadded",
                "weekday", "weekdayShort", "monthTitle", "lunar", "lunarTitle",
                "festival", "subtitle", "daysInMonth", "weekOfYear", "selectedDate"
            ],
            ["$device"] =
            [
                "volume", "volumeText", "muted",
                "brightness", "brightnessText",
                "audioDevice", "audioDeviceName",
                "wifiEnabled", "wifiStatus", "bluetoothEnabled", "bluetoothStatus", "networkConnected",
                "keepAwake", "keepAwakeStatus", "darkTheme", "themeStatus",
                "mediaPlaying", "mediaStatus", "mediaToggleIcon", "mediaCover", "mediaTitle", "mediaArtist", "mediaSummary",
                "recycleBinCount", "recycleBinStatus"
            ],
            ["$pomodoro"] =
            [
                "remainingText", "timeText", "phaseLabel", "phase", "phasePill", "headerPill",
                "dialSubtext", "phaseHint", "glyph", "primaryActionText",
                "isRunning", "isPaused", "totalSeconds", "remainingSeconds", "progressPercent",
                "cycle", "cycleBadge", "todayCompleted", "completedToday", "todayMinutes", "todayDurationText",
                "dailyGoal", "goalPercent", "goalSummary", "streakDays", "streakBadge", "todayDate",
                "capsuleTitle", "capsuleSummary", "capsuleBadge",
                // v5.0 dual-rhythm (sedentary guardian) tokens.
                "mode", "rhythmMode", "isAway", "isAwaySuspended", "isDue", "satMinutes",
                "intervalMin", "sedentaryIntervalMin", "stoodCount", "longestSitMin",
                "microAction", "healthSummary", "confirmActionText", "snoozeActionText"
            ],
            ["$countdown"] =
            [
                "hero.title", "hero.days", "hero.subtitle", "hero.badge", "hero.isToday", "hero.milestone",
                "year.percent", "yearPercent", "year.remainingDays",
                "month.percent", "monthPercent", "month.remainingDays", "count",
                "week.percent", "week.dayIndex",
                "work.percent", "work.phase", "work.summary", "work.range", "work.title",
                "holiday.title", "holiday.days", "holiday.subtitle", "holiday.inProgress",
                "capsuleTitle", "capsuleSummary", "capsuleGlyph", "capsuleBadge", "capsuleIsWarning", "capsuleProgress"
            ],
            ["$ai"] =
            [
                "todayTokens", "todayCost", "monthTokens", "monthCost", "cacheHitPercent",
                "activeCount", "focus.name", "focus.value", "focus.subtext", "focus.badge",
                "capsuleTitle", "capsuleSummary", "capsuleBadge", "capsuleIsWarning", "capsuleProgress"
            ],
            ["$time"] = ["now", "time", "date", "monthDay", "weekday"],
            ["$clock"] =
            [
                "utc", "utcTime", "utcSeconds", "utcFullTime", "utcDate",
                "localOffset", "unixSeconds", "unixMs", "iso", "seconds", "fullTime"
            ],
            ["$drives"] = ["count", "summary", "primaryPercent", "primaryFreeGb", "primaryTotalGb"],
            ["$music"] =
            [
                "hasSession", "isPlaying", "status", "title", "artist", "album", "sourceApp", "coverPath",
                "positionSeconds", "durationSeconds", "positionText", "durationText", "progressPercent"
            ],
            ["$todo"] =
            [
                "totalCount", "pendingCount", "completedCount", "todayPendingCount", "todayCompletedCount",
                "overdueCount", "importantPendingCount", "completionPercent", "nextTaskTitle", "nextTaskDueText", "summary"
            ],
            ["$clipboard"] = ["text", "preview", "length", "lines", "kind", "kindLabel", "hasText"],
            ["$drop"] = ["name", "path", "ext", "sizeText", "mode", "text"],
            ["$transform"] = ["status", "op", "sourcePath", "outputPath", "outputName", "sizeText", "resultText", "error", "jwtState"]
        };

    private static string ResolveCapabilityAlias(string id) =>
        id.ToLowerInvariant() switch
        {
            "script" or "logic.js" or "logic-js" => "script-sandbox",
            "canvas" or "svg" => "vector-canvas",
            "popover" or "popovers" or "markdown" => "popover-and-markdown",
            "chart" or "charts" or "donutchart" or "table" => "data-charts-and-tables",
            "datepicker" or "timepicker" or "colorpicker" or "autosuggestinput" or "radiogroup" or "rating" or "styling" => "advanced-inputs-and-styling",
            "http.request" or "webhook" => "interactive-http",
#if DESKBOX_STORE
            "filetext" or "file.writetext" => "local-files-and-commands",
#else
            "filetext" or "file.writetext" or "command" or "command.run" => "local-files-and-commands",
#endif
            _ => id
        };

    public static CustomWidgetCapabilitySpec? GetById(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(ResolveCapabilityAlias(id.Trim()), out CustomWidgetCapabilitySpec? spec)
            ? spec
            : null;

    public static IReadOnlyList<CustomWidgetCapabilitySpec> ExpandWithDependencies(IEnumerable<string>? capabilityIds)
    {
        if (capabilityIds is null)
        {
            return [];
        }

        var result = new List<CustomWidgetCapabilitySpec>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !visited.Add(id))
            {
                return;
            }

            if (!ById.TryGetValue(ResolveCapabilityAlias(id.Trim()), out CustomWidgetCapabilitySpec? spec))
            {
                return;
            }

            foreach (string dep in spec.RequiredCapabilityIds)
            {
                Visit(dep);
            }

            result.Add(spec);
        }

        foreach (string rawId in capabilityIds)
        {
            Visit(rawId);
        }

        return result;
    }

    public static IReadOnlyList<CustomWidgetCapabilitySpec> Search(string? query, CustomWidgetCapabilityCategory? category = null)
    {
        IEnumerable<CustomWidgetCapabilitySpec> source = All;
        if (category is { } cat)
        {
            source = source.Where(c => c.Category == cat);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return source.ToList();
        }

        string q = query.Trim().ToLowerInvariant();
        return source
            .Select(spec => (
                Spec: spec,
                Score: ScoreQueryMatch(spec, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Spec)
            .ToList();
    }

    private static int ScoreQueryMatch(CustomWidgetCapabilitySpec spec, string q)
    {
        int score = 0;
        if (spec.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (spec.TitleZh.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 9;
        if (spec.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 9;
        if (spec.SummaryZh.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 5;
        if (spec.SummaryEn.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 5;
        if (spec.TriggerNodeTypes.Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 8;
        if (spec.TriggerSourceOrActionTypes.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 8;
        if (spec.TriggerTokenPrefixes.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 7;
        if (spec.TriggerKeywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 6;
        if (spec.Members.Any(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 4;
        return score;
    }

    /// <summary>
    /// Tier 0: Ultra-compact global directory + Layout &amp; Interaction Architecture Rules + Anti-Facade Honesty Rule.
    /// Always included so the LLM knows every native capability DeskBox provides and follows standardized UI conventions.
    /// </summary>
    public static string BuildTier0DirectoryPromptBlock(bool isChinese = false)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("## DeskBox Native Capability & Component Directory (Schema v1)");
        sb.AppendLine("- Layout, Overlays & Rich Content: `Stack` (vertical|horizontal), `ModuleStack` (`bind`: `\"settings.modules\"`, drag-reorderable/toggleable child sections each with `id`), `Grid` (`columns`, `rows`, `column`, `row`; auto-flows multi-row items when `rows`/`row` omitted), `Tabs` (multi-page container: `bind`, `children` where each tab has `title`, `value`, `icon`), `Wrap`, `Scroll`, `Card` (`tone`: default|subtle|accent|warning|danger, max 2 nested levels), `Expander`, `Popover` (plus root `\"popovers\": { \"<id>\": { \"width\", \"height\", \"placement\", \"view\" } }` & `Button.popover`), `Markdown` (GFM headings, tables, code blocks, interactive task list toggles via `bind`/`onChange`), `Divider`, `Spacer`, `Text` (`style`: caption|body|bodyStrong|subtitle|title|hero|mono), `Image` (`src`, `height`, `stretch`, `cornerRadius`), `Badge`, `KeyValueRow`, `EmptyState`.");
        sb.AppendLine("- Raw Styling (Any Container/Control): `background` (`#RGB`/`#ARGB`/`#RRGGBB`/`#AARRGGBB`/`rgba(...)` rendered faithfully with zero contrast tampering), `linearGradient` (`{ \"angle\": 135, \"stops\": [{ \"offset\": 0, \"color\": \"#...\" }] }`), `textColor`, `borderColor`, `borderWidth`, `cornerRadius`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontSize`, `fontWeight`, `fontFamily`, `opacity`.");
        sb.AppendLine("- Interactive Controls: `Input` (`bind`, `onSubmit`), `TextArea`, `AutoSuggestInput` (`bind`, `suggestions`/`suggestionsSource`, `onSubmit`, `onChange`), `Select` (`options`), `Segmented` (`options`), `RadioGroup` (`bind`, `options`, `orientation`, `onChange`), `CheckBox` (`bind`/`value`, `onToggle`), `Toggle` (`bind`/`value`, `onChange`), `Slider` (`value`/`bind`, `min`, `max`, `step`, `unit`, `onChange` supporting `${$value}`), `NumberBox`, `Rating` (`bind`/`value`, `max`, `allowHalfStar`, `onChange`), `DatePicker` (`bind`/`value` ISO `YYYY-MM-DD`, `onChange`), `TimePicker` (`bind`/`value` `HH:mm`, `onChange`), `ColorPicker` (`bind`/`value` hex color, `presets`, `onChange`), `Button` (`variant`: standard|accent|subtle|danger, `popover`, `onClick`), `IconButton` (`icon` Segoe Fluent `\\uE700`..`\\uF8FF` or Emoji, `size`: 24|28, `onClick`), `ControlTile` (Win11 quick-settings tile: `icon`, `title`, `subtitle`, `value`/`bind`, `onClick`), `List` (`items`, `allowReorder`, `onReorder`, `inlineEditField`, `onInlineCommit`, `itemTemplate`, `hoverActions`), `DropZone` (`title`, `subtitle`, `format`, `onDrop`).");
        sb.AppendLine("- Data Visualization, Vector Canvas & Domain Components: `Canvas` (pure native vector canvas: `viewBox`, static `elements` or dynamic `elementsSource`: `\"${$computed.shapes}\"` supporting `rect|circle|ellipse|line|polyline|polygon|path|text` with `rotation` and `onItemClick`/`onClick` passing `{ id, x, y }`), `Table` (`collection`/`items`, `columns`: `[{ key, header, width, align, format }]`, `sortable`, `striped`, `onRowClick`), `DonutChart` (`collection`/`items`, `titleField`, `valueField`, `colorField`, `centerTitle`, `centerValue`, `showLegend`), `Chart` (`chartKind`: `line|bar|area`, `collection`/`items`, `xField`, multi-`series`: `[{ field, label, color }]`, `showLegend`, `showGrid`), `RingGauge` (`progress`/`value`, `max`, `title`, `subtitle`, `unit`, `size`, `color`), `BarList` (`collection`/`items`, `titleField`, `valueField`, `subtitleField`, `unit`, `maxItems`, `color`), `CalendarGrid` (`bind`, `collection`, `dateField`), `Sparkline`, `HeatmapGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`.");
#if DESKBOX_STORE
        sb.AppendLine("- Built-in Host `sources`: `weather` (`$weather.*`, `forecast`), `calendar` (`$calendar.*`), `clock` (`$clock.*` + `tz('<zoneId>', '<format>')`), `systemMetrics` (`$sys.*`), `storageDrives` (`$drives.*` + `drives` collection), `deviceState` (Volume/Brightness/AudioDevice/Wi-Fi/Bluetooth/Media/KeepAwake/RecycleBin, `$device.*`), `music` (`$music.*`), `todo` (`$todo.*` + `todos` collection), `clipboard` (`$clipboard.*`), `aiMonitor` (`$ai.*`), `countdown` (`$countdown.*`), `pomodoro` (`$pomodoro.*`), `httpJson` (`>= 15s` polling, `headers` with `${$secrets.<key>}`, `https://` or LAN/localhost `http://`), `rss`, `fileText` (local UTF-8/Markdown/JSON file watcher -> `stateKey`). (Microsoft Store sandbox edition: `command` source is NOT supported.)");
        sb.AppendLine("- Built-in Host `actions`: `state.set|toggle|increment` (supports `${$value}`), `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle` (`popoverId`), `script.run` (`fn`, `arg`, structured `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch(actionOrArray)` queuing host actions from `logic.js`), `http.request` (`method`: GET|POST|PUT|PATCH|DELETE, `url`, `headers` with `${$secrets.*}`, `body`, `stateMap`, `collection`, `onHttpResponse(ctx, res)`), `file.writeText` (`path`, `text`, `append`), `text.transform` (`jsonPretty|jsonMinify|urlEncode|urlDecode|base64Encode|base64Decode|jwtDecode|timestampToDate|dateToTimestamp|uuid|sha256|md5|...`, `$transform.*`), `launcher.open`, `ai.run`, `file.transform`, `file.pick|open|reveal`, `system.control`, `system.openSetting`, `reminder.notify`, `toast.show`, `source.refresh`, `clipboard.copy`, `url.open`, `pomodoro.*`, `countdown.*`. (Microsoft Store sandbox edition: `command.run` is NOT supported.)");
#else
        sb.AppendLine("- Built-in Host `sources`: `weather` (`$weather.*`, `forecast`), `calendar` (`$calendar.*`), `clock` (`$clock.*` + `tz('<zoneId>', '<format>')`), `systemMetrics` (`$sys.*`), `storageDrives` (`$drives.*` + `drives` collection), `deviceState` (Volume/Brightness/AudioDevice/Wi-Fi/Bluetooth/Media/KeepAwake/RecycleBin, `$device.*`), `music` (`$music.*`), `todo` (`$todo.*` + `todos` collection), `clipboard` (`$clipboard.*`), `aiMonitor` (`$ai.*`), `countdown` (`$countdown.*`), `pomodoro` (`$pomodoro.*`), `httpJson` (`>= 15s` polling, `headers` with `${$secrets.<key>}`, `https://` or LAN/localhost `http://`), `rss`, `fileText` (local UTF-8/Markdown/JSON file watcher -> `stateKey`), `command` (direct build: local CLI stdout polling -> `stateKey`).");
        sb.AppendLine("- Built-in Host `actions`: `state.set|toggle|increment` (supports `${$value}`), `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle` (`popoverId`), `script.run` (`fn`, `arg`, structured `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch(actionOrArray)` queuing host actions from `logic.js`), `http.request` (`method`: GET|POST|PUT|PATCH|DELETE, `url`, `headers` with `${$secrets.*}`, `body`, `stateMap`, `collection`, `onHttpResponse(ctx, res)`), `file.writeText` (`path`, `text`, `append`), `command.run` (`executable`, `arguments`, `workingDirectory`, `onCommandResult(ctx, res)`), `text.transform` (`jsonPretty|jsonMinify|urlEncode|urlDecode|base64Encode|base64Decode|jwtDecode|timestampToDate|dateToTimestamp|uuid|sha256|md5|...`, `$transform.*`), `launcher.open`, `ai.run`, `file.transform`, `file.pick|open|reveal`, `system.control`, `system.openSetting`, `reminder.notify`, `toast.show`, `source.refresh`, `clipboard.copy`, `url.open`, `pomodoro.*`, `countdown.*`.");
#endif
        sb.AppendLine("- Layout & Interaction Standards:");
        sb.AppendLine("  1. **No Duplicate Inner Header**: Every widget window ALREADY renders a host chrome header (`manifest.icon` + `manifest.name` + collapse/context menu). NEVER place a static widget-title header bar at the top of `view`; place utility `IconButton` actions inline on functional section headers, inside `Popover`s, or inside `Tabs`.");
        sb.AppendLine("  2. **Safe Root Viewport Inset & Bento Consistency**: Always set safe outer viewport padding on root `view` (`\"padding\": \"12,10,12,12\"`, NEVER `\"0\"` or omitted) so content never bleeds flush against the widget shell border. Keep sibling sections visually uniform (max 2 `Card` levels). Calibrate `defaultSize` (`width`, `height`) to fit all initial sections without clipping; use `Tabs` or `Popover`s when packing multi-panel detail.");
        sb.AppendLine("  3. **Direct Live Controls**: `Slider`, `Toggle`, `Segmented`, `RadioGroup`, `Rating`, `DatePicker`, `TimePicker`, and `ColorPicker` trigger `onChange` immediately (`${$value}` holds the live control value). NEVER add a redundant \"Apply / Sync\" button beside a live control.");
        sb.AppendLine("  4. **High-Frequency Canvas vs. Low-Frequency `settings` Separation**: Keep the desktop canvas clean for daily glance & 1-click actions; expose low-frequency preferences and API tokens in root `\"settings\"` (`choice`, `toggle`, `number`, `text`, `secret`, `moduleList`) and ALWAYS bind every declared setting key via `${$settings.<key>}`, `settings.<key>`, or `${$secrets.<key>}`.");
        sb.AppendLine("  5. **First-Class `logic.js` + Native Rich Components (Never Downgrade Local Complexity)**: Simple widgets stay pure declarative `manifest.json`, while complex local calculators, state machines, mini-games, interactive charts, custom clocks/gauges, or multi-step workflows MUST pair `logic.js` (`export function onInit(ctx)` populating every `${$computed.<key>}`, `onTick(ctx)` when `tickIntervalMs > 0`, `ctx.args`, `ctx.event`, `ctx.dispatch(...)`) with `Canvas`, `Popover`, `Table`, `Chart`, `DonutChart`, `Markdown`, and raw styling to deliver a complete, un-compromised widget directly. Always include a high-density `\"capsule\"` block and match the user's language.");
        sb.AppendLine("- External API Clarification & Anti-Facade Rule: NEVER invent fake buttons/switches that only mutate unread dummy `state` strings. When a request depends on an unknown 3rd-party API, Webhook, or private user endpoint whose URL/auth/payload structure is not yet known, return `\"kind\": \"clarify\"` in Turn 1 to confirm the interface details with the user (or use `probeHttp` when a concrete URL is provided), then generate full `httpJson` / `http.request` code. Only decline with alternatives when a request truly exceeds OS/sandbox boundaries (e.g. kernel hooks, video FFmpeg transcoding).");
        return sb.ToString().TrimEnd();
    }

    private static readonly Regex InlineCapabilityMentionRegex = new(
        @"@([A-Za-z0-9][A-Za-z0-9.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ExtractMentionedCapabilityIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('@'))
        {
            return [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InlineCapabilityMentionRegex.Matches(text))
        {
            string rawId = match.Groups[1].Value.TrimEnd('.');
            if (ById.TryGetValue(ResolveCapabilityAlias(rawId), out CustomWidgetCapabilitySpec? spec) && seen.Add(spec.Id))
            {
                found.Add(spec.Id);
            }
        }

        return found;
    }

    public static (IReadOnlyList<CustomWidgetCapabilitySpec> PinnedSpecs, IReadOnlyList<CustomWidgetCapabilitySpec> AutoSpecs)
        SelectCapabilitiesForTurn(
            string? currentManifestJson,
            string? userPrompt,
            string? briefMarkdown = null,
            IEnumerable<string>? pinnedCapabilityIds = null,
            int maxAutoCaps = DefaultAutoSelectedCap)
    {
        var combinedPinIds = new List<string>();
        if (pinnedCapabilityIds is not null)
        {
            combinedPinIds.AddRange(pinnedCapabilityIds);
        }
        combinedPinIds.AddRange(ExtractMentionedCapabilityIds(userPrompt));

        IReadOnlyList<CustomWidgetCapabilitySpec> pinnedExpanded = ExpandWithDependencies(combinedPinIds);
        var excludedIds = new HashSet<string>(pinnedExpanded.Select(static p => p.Id), StringComparer.OrdinalIgnoreCase);

        var autoCandidates = new List<CustomWidgetCapabilitySpec>();
        int cap = Math.Clamp(maxAutoCaps, 2, 8);

        // 1. AST inspection of existing manifest
        if (!string.IsNullOrWhiteSpace(currentManifestJson))
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(currentManifestJson);
            if (parsed.Manifest is { } manifest)
            {
                foreach (CustomWidgetCapabilitySpec spec in MatchFromManifestAst(manifest))
                {
                    if (autoCandidates.Count >= Math.Max(2, cap - 2))
                    {
                        break;
                    }

                    if (excludedIds.Add(spec.Id))
                    {
                        autoCandidates.Add(spec);
                    }
                }
            }
        }

        // 2. Intent keyword scoring across userPrompt + brief
        string combined = $"{userPrompt ?? string.Empty}\n{briefMarkdown ?? string.Empty}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(combined))
        {
            var scored = All
                .Where(c => !excludedIds.Contains(c.Id))
                .Select(c => (
                    Spec: c,
                    Score: c.TriggerKeywords.Count(kw => combined.Contains(kw.ToLowerInvariant(), StringComparison.Ordinal)) * 3 +
                           c.TriggerNodeTypes.Count(n => combined.Contains(n.ToLowerInvariant(), StringComparison.Ordinal)) * 4 +
                           c.TriggerSourceOrActionTypes.Count(a => combined.Contains(a.ToLowerInvariant(), StringComparison.Ordinal)) * 4))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var hit in scored)
            {
                if (autoCandidates.Count >= cap)
                {
                    break;
                }

                foreach (CustomWidgetCapabilitySpec withDep in ExpandWithDependencies([hit.Spec.Id]))
                {
                    if (excludedIds.Add(withDep.Id))
                    {
                        autoCandidates.Add(withDep);
                    }
                }
            }
        }

        // 3. Default starter contracts when creating from scratch with generic prompt and zero pins
        if (pinnedExpanded.Count == 0 && autoCandidates.Count == 0)
        {
            foreach (string fallbackId in new[] { "forms-inputs", "collections-lists" })
            {
                if (ById.TryGetValue(fallbackId, out CustomWidgetCapabilitySpec? fb) && excludedIds.Add(fb.Id))
                {
                    autoCandidates.Add(fb);
                }
            }
        }

        return (pinnedExpanded, autoCandidates);
    }

    public static string FormatSelectedCapsulesPromptBlock(
        IReadOnlyList<CustomWidgetCapabilitySpec> pinnedSpecs,
        IReadOnlyList<CustomWidgetCapabilitySpec> autoSpecs)
    {
        var sb = new StringBuilder(4096);

        if (pinnedSpecs.Count > 0)
        {
            sb.AppendLine("## Explicitly Selected Capability Modules by User (MUST Integrate & Follow Exact Signatures)");
            foreach (CustomWidgetCapabilitySpec spec in pinnedSpecs)
            {
                sb.AppendLine();
                sb.Append("### [MUST-USE] ").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                  .Append(" (`").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.CompactContractMarkdown.Trim());
            }
        }

        if (autoSpecs.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("## Matched Atomic Capability Contracts (Follow Exact Signatures)");
            foreach (CustomWidgetCapabilitySpec spec in autoSpecs)
            {
                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                  .Append(" (`").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.CompactContractMarkdown.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// AST Backfill for Auto-Repair: inspects a broken/candidate JSON string and diagnostics to find
    /// any capability module the LLM attempted to use so the repair prompt receives its exact contract.
    /// </summary>
    public static IReadOnlyList<CustomWidgetCapabilitySpec> FindBackfillCapabilitiesForRepair(
        string? candidateManifestJson,
        IReadOnlyList<CustomWidgetValidationDiagnostic>? diagnostics)
    {
        var ids = new List<string>();
        string haystack = candidateManifestJson ?? string.Empty;
        if (diagnostics is not null && diagnostics.Count > 0)
        {
            haystack += "\n" + string.Join("\n", diagnostics.Select(static d => d.Message));
        }

        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        foreach (CustomWidgetCapabilitySpec spec in All)
        {
            bool matched =
                spec.TriggerNodeTypes.Any(t => haystack.Contains($"\"{t}\"", StringComparison.OrdinalIgnoreCase) || haystack.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                spec.TriggerSourceOrActionTypes.Any(a => haystack.Contains($"\"{a}\"", StringComparison.OrdinalIgnoreCase)) ||
                spec.TriggerTokenPrefixes.Any(p => haystack.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (matched)
            {
                ids.Add(spec.Id);
            }
        }

        return ExpandWithDependencies(ids);
    }

    private static readonly Regex BuiltInNamespaceTokenRegex = new(
        @"(\$sys|\$weather|\$calendar|\$device|\$music|\$todo|\$pomodoro|\$pomo|\$countdown|\$ai|\$time|\$clock|\$drives|\$clipboard|\$drop|\$transform)\.([A-Za-z0-9_.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validates built-in <c>$namespace.*</c> token paths inside expressions and emits actionable
    /// Did-You-Mean hints when an LLM hallucinates a non-existent token path.
    /// </summary>
    public static void ValidateBuiltInTokensInExpression(
        string? expression,
        string jsonPath,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(expression) || !expression.Contains('$'))
        {
            return;
        }

        foreach (Match match in BuiltInNamespaceTokenRegex.Matches(expression))
        {
            string rawNs = match.Groups[1].Value;
            string ns = string.Equals(rawNs, "$pomo", StringComparison.OrdinalIgnoreCase) ? "$pomodoro" : rawNs;
            string subPath = match.Groups[2].Value.TrimEnd('.');
            if (!ValidNamespaceTokens.TryGetValue(ns, out IReadOnlyList<string>? validList))
            {
                continue;
            }

            if (IsTokenSupportedOrAliased(ns, subPath, validList))
            {
                continue;
            }

            string topSegment = subPath.Contains('.') ? subPath[..subPath.IndexOf('.')] : subPath;
            List<string> suggestions = validList
                .Where(v => v.StartsWith(topSegment, StringComparison.OrdinalIgnoreCase) ||
                            v.Contains(topSegment, StringComparison.OrdinalIgnoreCase))
                .Take(6)
                .ToList();
            if (suggestions.Count == 0)
            {
                suggestions = validList.Take(8).ToList();
            }

            string validHint = string.Join(", ", suggestions.Select(s => $"{ns}.{s}"));
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                jsonPath,
                $"Unknown built-in token '{ns}.{subPath}'. Valid '{ns}.*' tokens include: {validHint}.",
                IsError: true,
                Code: "UnknownBuiltInToken"));
        }
    }

    private static bool IsTokenSupportedOrAliased(string ns, string subPath, IReadOnlyList<string> validList)
    {
        if (validList.Any(v => string.Equals(v, subPath, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.Equals(ns, "$sys", StringComparison.OrdinalIgnoreCase))
        {
            if (subPath.StartsWith("ram.", StringComparison.OrdinalIgnoreCase))
            {
                string mapped = "memory." + subPath["ram.".Length..];
                if (validList.Any(v => string.Equals(v, mapped, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return subPath is "disk.primaryPercent" or "disk.selectedPercent"
                           or "disk.primaryFreeGb" or "disk.selectedFreeGb"
                           or "disk.primaryTotalGb" or "disk.selectedTotalGb"
                           or "disk.primaryName" or "disk.selectedName"
                           or "disk.primarySummary" or "disk.selectedSummary";
        }

        if (string.Equals(ns, "$weather", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "location" or "name" or "temperature" or "apparentTemp"
                           or "maxTemp" or "tempMax" or "minTemp" or "tempMin" or "range"
                           or "summary" or "weather" or "weatherText" or "desc" or "emoji"
                           or "windText" or "precip" or "rainProb" or "uvIndex";
        }

        if (string.Equals(ns, "$calendar", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "title" or "traditional" or "traditionalTitle" or "ganzhi" or "holiday";
        }

        if (string.Equals(ns, "$device", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "volumePercent" or "volumeIcon" or "isMuted" or "brightnessPercent"
                           or "mediaCoverPath" or "wifi" or "bluetooth"
                           or "online" or "caffeine" or "isDark" or "recycleBinItems";
        }

        if (string.Equals(ns, "$pomodoro", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "remaining" or "time";
        }

        if (string.Equals(ns, "$countdown", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "daysLeft" or "days" or "title" or "subtitle" or "percent"
                       or "phase" or "summary" or "range" or "inProgress" or "dayIndex" or "milestone";
        }

        if (string.Equals(ns, "$ai", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "tokens" or "cost" or "balance" ||
                   subPath.StartsWith("provider.", StringComparison.OrdinalIgnoreCase) ||
                   subPath.StartsWith("local.", StringComparison.OrdinalIgnoreCase) ||
                   subPath.StartsWith("selected.", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(ns, "$clock", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "offset" or "unix";
        }

        if (string.Equals(ns, "$drives", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "percent" or "freeGb" or "totalGb";
        }

        if (string.Equals(ns, "$clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "content" or "chars" or "lineCount" or "type";
        }

        if (string.Equals(ns, "$drop", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "fileName" or "filePath" or "extension" or "storageMode" or "content";
        }

        if (string.Equals(ns, "$transform", StringComparison.OrdinalIgnoreCase))
        {
            return subPath is "operation" or "format" or "inputPath" or "path" or "name"
                           or "fileName" or "size" or "result" or "text" or "hash"
                           or "jwtStatus";
        }

        return false;
    }

    private static IReadOnlyList<CustomWidgetCapabilitySpec> MatchFromManifestAst(CustomWidgetManifest manifest)
    {
        var usedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSourcesAndActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (!string.IsNullOrWhiteSpace(src.Type))
            {
                usedSourcesAndActions.Add(src.Type);
            }
        }

        CollectNodePrimitives(manifest.View, usedNodes, usedSourcesAndActions);
        foreach (CustomWidgetNodeSpec popover in manifest.Popovers)
        {
            usedNodes.Add("Popover");
            CollectNodePrimitives(popover, usedNodes, usedSourcesAndActions);
        }
        if (manifest.Script is not null)
        {
            usedSourcesAndActions.Add("script");
        }
        if (manifest.Capsule?.PrimaryAction is not null)
        {
            CollectActionPrimitives(manifest.Capsule.PrimaryAction.OnClick, usedSourcesAndActions);
        }
        if (manifest.OnDrop is not null)
        {
            usedSourcesAndActions.Add("onDrop");
            CollectActionPrimitives(manifest.OnDrop.Actions, usedSourcesAndActions);
        }
        if (manifest.Settings.Any(static s => string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase)))
        {
            usedSourcesAndActions.Add("secret");
        }

        return All
            .Select(spec => (
                Spec: spec,
                Hits: spec.TriggerNodeTypes.Count(usedNodes.Contains) * 2 +
                      spec.TriggerSourceOrActionTypes.Count(usedSourcesAndActions.Contains) * 3))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Spec)
            .ToList();
    }

    private static void CollectNodePrimitives(
        CustomWidgetNodeSpec? node,
        HashSet<string> nodes,
        HashSet<string> actions)
    {
        if (node is null) return;
        if (!string.IsNullOrWhiteSpace(node.Type)) nodes.Add(node.Type);
        if (!string.IsNullOrWhiteSpace(node.Popover)) actions.Add("popover.open");
        if (!string.IsNullOrWhiteSpace(node.Background) || node.LinearGradient is not null) nodes.Add("RawStyling");
        CollectActionPrimitives(node.OnClick, actions);
        CollectActionPrimitives(node.OnSubmit, actions);
        CollectActionPrimitives(node.OnChange, actions);
        CollectActionPrimitives(node.OnDropActions, actions);
        CollectActionPrimitives(node.OnRowClick, actions);
        CollectActionPrimitives(node.OnItemClick, actions);
        CollectActionPrimitives(node.OnReorder, actions);
        CollectActionPrimitives(node.OnInlineCommit, actions);
        CollectNodePrimitives(node.ItemTemplate, nodes, actions);
        foreach (CustomWidgetNodeSpec h in node.HoverActions) CollectNodePrimitives(h, nodes, actions);
        foreach (CustomWidgetNodeSpec c in node.Children) CollectNodePrimitives(c, nodes, actions);
    }

    private static void CollectActionPrimitives(
        IReadOnlyList<CustomWidgetActionSpec>? list,
        HashSet<string> actions)
    {
        if (list is null) return;
        foreach (CustomWidgetActionSpec a in list)
        {
            if (!string.IsNullOrWhiteSpace(a.Type)) actions.Add(a.Type);
            CollectActionPrimitives(a.Then, actions);
        }
    }

    /// <summary>
    /// Generates the formal, standardized <c>API_REFERENCE.md</c> specification from the single source of truth.
    /// </summary>
    public static string GenerateApiReferenceMarkdown(bool isChinese) =>
        GenerateApiReferenceMarkdown(isChinese ? "zh-CN" : "en-US");

    /// <summary>
    /// Generates the formal, standardized <c>API_REFERENCE.md</c> specification from the single source of truth,
    /// localized for <paramref name="cultureName"/> via the shared Strings catalog (13 locales).
    /// </summary>
    public static string GenerateApiReferenceMarkdown(string? cultureName = null)
    {
        var sb = new StringBuilder(24576);
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.deskbox-custom-widget-capability-api-ref"));
        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.generated-from-the-deskbox-runtime-capab"));
        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.architecture-layout-conventions"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.no-duplicate-inner-header-the-host-windo"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.consistent-bento-hierarchy-keep-sib"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.direct-live-controls-slider-toggle-and-s"));

        CustomWidgetCapabilityCategory[] order =
        [
            CustomWidgetCapabilityCategory.DataSource,
            CustomWidgetCapabilityCategory.Component,
            CustomWidgetCapabilityCategory.Action,
            CustomWidgetCapabilityCategory.ConfigAndSecurity
        ];

        foreach (CustomWidgetCapabilityCategory cat in order)
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(GetCategoryDisplayName(cat, cultureName));
            foreach (CustomWidgetCapabilitySpec spec in All.Where(c => c.Category == cat))
            {
                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.GetTitle(cultureName))
                  .Append(" (`@").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.GetSummary(cultureName));
                if (spec.RequiredCapabilityIds.Count > 0)
                {
                    sb.Append(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.required-modules"))
                      .AppendLine(string.Join(", ", spec.RequiredCapabilityIds.Select(static d => $"`{d}`")));
                }

                if (spec.Members.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.signature-token-property-kind-specificat"));
                    sb.AppendLine("| :--- | :--- | :--- |");
                    foreach (CustomWidgetApiMemberDoc m in spec.Members)
                    {
                        sb.Append("| `").Append(m.Name).Append("` | ").Append(m.Kind).Append(" | ")
                          .Append(spec.GetMemberDescription(m, cultureName)).AppendLine(" |");
                    }
                }

                if (!string.IsNullOrWhiteSpace(spec.GoldenSnippetJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(LocalizeSnippetSample(spec.GoldenSnippetJson.Trim(), cultureName));
                    sb.AppendLine("```");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.expression-syntax-context-variables"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.operators-wrap-expressions-in-supports-n"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.event-payload-value-inside-onchange-onto"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.timezone-function-tz-zoneid-format-suppo"));

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string GetCategoryDisplayName(CustomWidgetCapabilityCategory category, bool isChinese) =>
        GetCategoryDisplayName(category, isChinese ? "zh-CN" : "en-US");

    public static string GetCategoryDisplayName(CustomWidgetCapabilityCategory category, string? cultureName) =>
        category switch
        {
            CustomWidgetCapabilityCategory.DataSource => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Sources"),
            CustomWidgetCapabilityCategory.Component => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Components"),
            CustomWidgetCapabilityCategory.Action => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Actions"),
            CustomWidgetCapabilityCategory.ConfigAndSecurity => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Security"),
            _ => string.Empty
        };

    /// <summary>
    /// Display-sample terms embedded in golden/reference snippets. Replaced at RENDER time only —
    /// the raw <c>GoldenSnippetJson</c> fixtures stay canonical (tests + agent contracts keep the zh text).
    /// Key list and zh terms are pinned by CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    private static readonly (string Zh, string Key)[] SnippetSampleTerms =
    [
        ("概览", "CustomWidget.ApiDocs.Sample.01"),
        ("区块 A", "CustomWidget.ApiDocs.Sample.02"),
        ("区块 B", "CustomWidget.ApiDocs.Sample.03"),
        ("主音量", "CustomWidget.ApiDocs.Sample.04"),
        ("内存", "CustomWidget.ApiDocs.Sample.05"),
        ("纽约 ${tz('America/New_York', 'HH:mm')} · 伦敦 ${tz('Europe/London', 'HH:mm')}", "CustomWidget.ApiDocs.Sample.06"),
        ("${$weather.condition}  ${$weather.rangeText} · 湿度 ${$weather.humidity}", "CustomWidget.ApiDocs.Sample.07"),
        ("北京 (UTC+8)", "CustomWidget.ApiDocs.Sample.08"),
        ("纽约 (EST/EDT)", "CustomWidget.ApiDocs.Sample.09"),
        ("伦敦 (GMT/BST)", "CustomWidget.ApiDocs.Sample.10"),
        ("东京 (JST)", "CustomWidget.ApiDocs.Sample.11"),
        ("核心负载", "CustomWidget.ApiDocs.Sample.12"),
        ("高占进程", "CustomWidget.ApiDocs.Sample.13"),
        ("当日暂无日程", "CustomWidget.ApiDocs.Sample.14"),
        ("点击上方日期可切换查看", "CustomWidget.ApiDocs.Sample.15"),
        ("拖入图片或点击选择文件", "CustomWidget.ApiDocs.Sample.16"),
        ("本地转换为目标格式，源文件保持不变", "CustomWidget.ApiDocs.Sample.17"),
        ("蓝牙", "CustomWidget.ApiDocs.Sample.18"),
        ("屏幕常亮", "CustomWidget.ApiDocs.Sample.19"),
        ("回收站", "CustomWidget.ApiDocs.Sample.20"),
        ("近 28 天记录热力分布", "CustomWidget.ApiDocs.Sample.21"),
        ("输入新条目…", "CustomWidget.ApiDocs.Sample.22"),
        ("系统音量", "CustomWidget.ApiDocs.Sample.23"),
        ("屏幕亮度", "CustomWidget.ApiDocs.Sample.24"),
        ("音频输出", "CustomWidget.ApiDocs.Sample.25"),
        ("当前剪贴板 (${$clipboard.kindLabel})", "CustomWidget.ApiDocs.Sample.26"),
        ("${$clipboard.length} 字符", "CustomWidget.ApiDocs.Sample.27"),
        ("输入待处理文本（留空则直接处理剪贴板内容）…", "CustomWidget.ApiDocs.Sample.28"),
        ("JSON 格式化", "CustomWidget.ApiDocs.Sample.29"),
        ("Base64 编码", "CustomWidget.ApiDocs.Sample.30"),
        ("时间戳转换", "CustomWidget.ApiDocs.Sample.31"),
        ("下载目录", "CustomWidget.ApiDocs.Sample.32"),
        ("文档目录", "CustomWidget.ApiDocs.Sample.33"),
        ("屏幕截图", "CustomWidget.ApiDocs.Sample.34"),
        ("计算器", "CustomWidget.ApiDocs.Sample.35"),
        ("拖入图片或点击选择", "CustomWidget.ApiDocs.Sample.36"),
        ("自动转为 ${state.format} 并保存在原目录", "CustomWidget.ApiDocs.Sample.37"),
        ("拆解待办事项", "CustomWidget.ApiDocs.Sample.38"),
        ("发送测试提醒", "CustomWidget.ApiDocs.Sample.39"),
        ("定时提醒", "CustomWidget.ApiDocs.Sample.40"),
        ("预设提醒时间已到。", "CustomWidget.ApiDocs.Sample.41"),
        ("账户可用余额：¥ ${state.balance ?? '--'}", "CustomWidget.ApiDocs.Sample.42"),
        ("显示密度", "CustomWidget.ApiDocs.Sample.43"),
        ("紧凑视图", "CustomWidget.ApiDocs.Sample.44"),
        ("详细视图", "CustomWidget.ApiDocs.Sample.45"),
        ("将以下内容拆解为可执行待办事项(items: title)：\\n${state.draft}", "CustomWidget.ApiDocs.Sample.46")
    ];

    /// <summary>Replaces zh display-sample labels inside a snippet with the active culture's rendering.</summary>
    public static string LocalizeSnippetSample(string snippet, string? cultureName)
    {
        if (string.IsNullOrEmpty(snippet))
        {
            return snippet;
        }

        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(
            (cultureName ?? LocalizationService.ResolveAmbientCultureName()));
        if (isChinese)
        {
            return snippet;
        }

        foreach ((string zh, string key) in SnippetSampleTerms.OrderByDescending(static t => t.Zh.Length))
        {
            string localized = LocalizationService.TranslateForCulture(cultureName, key);
            if (!string.IsNullOrEmpty(localized) && !string.Equals(localized, zh, StringComparison.Ordinal))
            {
                snippet = snippet.Replace(zh, localized, StringComparison.Ordinal);
            }
        }

        return snippet;
    }

    public const string EmbeddedRegistryResourceName = "DeskBox.Assets.CustomWidgets.capability-registry.json";

    private static IReadOnlyList<CustomWidgetCapabilitySpec> BuildRegistry()
    {
        using Stream stream = OpenEmbeddedRegistryStream();
        using JsonDocument doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("capabilities", out JsonElement capsEl) ||
            capsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Invalid capability-registry.json: missing 'capabilities' array.");
        }

        var list = new List<CustomWidgetCapabilitySpec>(capsEl.GetArrayLength());
        foreach (JsonElement item in capsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var spec = new CustomWidgetCapabilitySpec(
                Id: ReadReqString(item, "id"),
                Emoji: ReadReqString(item, "emoji"),
                Glyph: ReadReqString(item, "glyph"),
                Category: ParseCategory(ReadReqString(item, "category")),
                TitleZh: ReadReqString(item, "titleZh"),
                TitleEn: ReadReqString(item, "titleEn"),
                SummaryZh: ReadReqString(item, "summaryZh"),
                SummaryEn: ReadReqString(item, "summaryEn"),
                RequiredCapabilityIds: ReadStringList(item, "requiredCapabilityIds"),
                TriggerNodeTypes: ReadStringList(item, "triggerNodeTypes"),
                TriggerSourceOrActionTypes: ReadStringList(item, "triggerSourceOrActionTypes"),
                TriggerTokenPrefixes: ReadStringList(item, "triggerTokenPrefixes"),
                TriggerKeywords: ReadStringList(item, "triggerKeywords"),
                Members: ReadMemberDocs(item, "members"),
                CompactContractMarkdown: ReadReqString(item, "compactContractMarkdown"),
                GoldenSnippetJson: ReadReqString(item, "goldenSnippetJson"));

#if DESKBOX_STORE
            if (string.Equals(spec.Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
            {
                spec = spec with
                {
                    TitleZh = "本地文件读写",
                    TitleEn = "Local File Sync",
                    SummaryZh = "受格子级权限开关保护的本地 UTF-8/Markdown 文件监听与写入能力（商店版本不支持本地命令行进程调用）。",
                    SummaryEn = "Permission-gated local UTF-8/Markdown file watching and writing (CLI command execution is disabled in the Microsoft Store edition).",
                    TriggerSourceOrActionTypes = spec.TriggerSourceOrActionTypes
                        .Where(static t => !t.StartsWith("command", StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    TriggerKeywords = spec.TriggerKeywords
                        .Where(static k => k is not ("命令行" or "终端" or "脚本执行" or "进程" or "command.run" or "cli" or "powershell" or "git status" or "docker"))
                        .ToList(),
                    Members = spec.Members
                        .Where(static m => !m.Name.Contains("command", StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    CompactContractMarkdown =
                        "- Local File Sync (`LocalFileAccess` permission):\n" +
                        "  - Source: `{ \"id\": \"noteFile\", \"type\": \"fileText\", \"path\": \"${$settings.filePath}\", \"stateKey\": \"fileBody\", \"intervalMs\": 5000 }`\n" +
                        "  - Action: `{ \"type\": \"file.writeText\", \"path\": \"${$settings.filePath}\", \"text\": \"${state.fileBody}\", \"append\": false }`\n" +
                        "  - Note: `command` source and `command.run` action are disabled in the Microsoft Store edition."
                };
            }
#endif

            list.Add(spec);
        }

        return list;
    }

    private static Stream OpenEmbeddedRegistryStream()
    {
        Stream? embedded = typeof(CustomWidgetCapabilityRegistry).Assembly
            .GetManifestResourceStream(EmbeddedRegistryResourceName);
        if (embedded is not null)
        {
            return embedded;
        }

        string diskFallback = Path.Combine(AppContext.BaseDirectory, "Assets", "CustomWidgets", "capability-registry.json");
        if (File.Exists(diskFallback))
        {
            return File.OpenRead(diskFallback);
        }

        throw new InvalidOperationException(
            $"Embedded resource '{EmbeddedRegistryResourceName}' was not found in assembly '{typeof(CustomWidgetCapabilityRegistry).Assembly.FullName}'.");
    }

    private static CustomWidgetCapabilityCategory ParseCategory(string raw) =>
        raw switch
        {
            "DataSource" => CustomWidgetCapabilityCategory.DataSource,
            "Component" => CustomWidgetCapabilityCategory.Component,
            "Action" => CustomWidgetCapabilityCategory.Action,
            "ConfigAndSecurity" => CustomWidgetCapabilityCategory.ConfigAndSecurity,
            _ => throw new InvalidOperationException($"Unknown capability category '{raw}' in capability-registry.json.")
        };

    private static string ReadReqString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int len = arr.GetArrayLength();
        if (len == 0)
        {
            return [];
        }

        var list = new List<string>(len);
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetApiMemberDoc> ReadMemberDocs(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int len = arr.GetArrayLength();
        if (len == 0)
        {
            return [];
        }

        var list = new List<CustomWidgetApiMemberDoc>(len);
        foreach (JsonElement m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new CustomWidgetApiMemberDoc(
                Name: ReadReqString(m, "name"),
                Kind: ReadReqString(m, "kind"),
                DescriptionZh: ReadReqString(m, "descriptionZh"),
                DescriptionEn: ReadReqString(m, "descriptionEn")));
        }

        return list;
    }

    public static string BuildCompactCoreComponentIndex(bool isChinese = false) => isChinese
        ? """
          ## 核心组件与动作速查索引 (Schema v1)
          - 根字段: `schemaVersion: 1`, `id` (`custom.<slug>`), `name`, `icon` (Emoji), `defaultSize: { width, height }`, `settings`, `state`, `collections`, `sources`, `script`, `popovers`, `onDrop`, `capsule`, `view`.
          - 自由样式 (任意容器/控件): `background` (`#RGB`/`#ARGB`/`#RRGGBB`/`#AARRGGBB`/`rgba(...)` 原样渲染不做对比度篡改), `linearGradient` (`{ angle, stops: [{offset,color}] }`), `textColor`, `borderColor`, `borderWidth`, `cornerRadius`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontSize`, `fontWeight`, `fontFamily`, `opacity`.
          - 布局与富文本: `Stack` (`orientation`: vertical|horizontal, `spacing`, `padding`), `Grid` (`columns`, `rows`, `column`, `row`), `Wrap` (`spacing`), `Scroll`, `Card` (`tone`: default|subtle|accent|warning|danger, 最多嵌套 2 层), `Tabs` (`bind`, `tabs`/`children`), `Expander` (`title`, `subtitle`, `defaultExpanded`, `bind`, `children`), `Popover` (`popoverId`, `text`, `icon`), `Markdown` (`text`, `bind`, `onChange`), `ModuleStack` (`bind`: `settings.<moduleListKey>`, `children`), `Spacer`, `Divider`.
          - 交互控件: `Input` (`bind`, `placeholder`, `onSubmit`), `TextArea` (`bind`, `minHeight`, `maxHeight`, `onSubmit`), `AutoSuggestInput` (`bind`, `suggestions`/`suggestionsSource`, `onSubmit`, `onChange`), `Select` (`title`, `bind`, `options: [{value,label}]`, `onChange`), `RadioGroup` (`bind`, `options`, `orientation`, `onChange`), `CheckBox` (`bind`/`value`, `text`, `strikethrough`, `onToggle`), `Toggle` (`title`, `bind`, `onChange`), `Slider` (`title`, `bind`/`value`, `min`, `max`, `step`, `unit`, `onChange`), `NumberBox` (`title`, `bind`, `min`, `max`, `step`), `Rating` (`bind`/`value`, `max`, `allowHalfStar`, `onChange`), `DatePicker` (`bind`/`value`, `onChange`), `TimePicker` (`bind`/`value`, `onChange`), `ColorPicker` (`bind`/`value`, `presets`, `onChange`), `Segmented` (`bind`, `options`), `Button` (`text`, `icon`, `variant`: standard|accent|subtle|danger, `popover`, `onClick`), `IconButton` (`icon`, `size`: 24|28, `variant`: subtle|accent|danger, `tooltip`, `onClick`), `ControlTile`.
          - 视觉、矢量画布与图表: `Canvas` (`viewBox`, `elements`/`elementsSource`: `rect|circle|ellipse|line|polyline|polygon|path|text`, `onItemClick`), `Table` (`collection`/`items`, `columns: [{key,header,width,align,format}]`, `sortable`, `striped`, `onRowClick`), `DonutChart` (`collection`/`items`, `titleField`, `valueField`, `colorField`, `centerTitle`, `centerValue`), `Chart` (`chartKind`: line|bar|area, `collection`/`items`, `xField`, `series: [{field,label,color}]`), `Text` (`text`, `style`: caption|body|bodyStrong|subtitle|title|hero|mono, `color`: default|secondary|muted|accent|emerald|cyan|violet|success|warning|danger), `Badge` (`text`, `tone`), `List` (`items`, `filter`, `allowReorder`, `onReorder`, `inlineEditField`, `onInlineCommit`, `emptyState`, `itemTemplate`, `hoverActions`), `RingGauge`, `BarList`, `Sparkline`, `HeatmapGrid`, `CalendarGrid`, `ProgressBar`, `ProgressRing`, `KeyValueRow`, `ProgressRow`, `DropZone`, `FileChip`, `EmptyState`.
          """
#if DESKBOX_STORE
        + "\n- 领域卡片与数据源: `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`; 数据源含 `httpJson`, `rss`, `fileText` 等。\n- 常用动作: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request` (`method`, `url`, `headers`, `body`, `stateMap`, `onHttpResponse`), `file.writeText`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `timer.*`, `pomodoro.*`, `countdown.*`."
#else
        + "\n- 领域卡片与数据源: `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`; 数据源含 `httpJson`, `rss`, `fileText`, `command` 等。\n- 常用动作: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request` (`method`, `url`, `headers`, `body`, `stateMap`, `onHttpResponse`), `file.writeText`, `command.run` (`onCommandResult`), `ai.run`, `source.refresh`, `system.control`, `file.transform`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `timer.*`, `pomodoro.*`, `countdown.*`."
#endif
        : """
          ## Component Quick Index (All Supported Schema v1 Nodes)
          - Root Keys: `schemaVersion: 1`, `id` (`custom.<slug>`), `name`, `icon` (Emoji), `defaultSize: { width, height }`, `settings`, `state`, `collections`, `sources`, `script`, `popovers`, `onDrop`, `capsule`, `view`.
          - Raw Styling (Any Node): `background` (`#RGB`/`#ARGB`/`#RRGGBB`/`#AARRGGBB`/`rgba(...)` rendered faithfully with zero contrast tampering), `linearGradient` (`{ angle, stops: [{offset,color}] }`), `textColor`, `borderColor`, `borderWidth`, `cornerRadius`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontSize`, `fontWeight`, `fontFamily`, `opacity`.
          - Containers & Rich Text: `Stack` (`orientation`: vertical|horizontal, `spacing`, `padding`), `Grid` (`columns`, `rows`, `column`, `row`), `Wrap` (`spacing`), `Scroll`, `Card` (`tone`: default|subtle|accent|warning|danger, max 2 nested levels!), `Tabs` (`bind`, `tabs`/`children`), `Expander` (`title`, `subtitle`, `defaultExpanded`, `bind`, `children`), `Popover` (`popoverId`, `text`, `icon`), `Markdown` (`text`, `bind`, `onChange`), `ModuleStack` (`bind`: `settings.<moduleListKey>`, `children`), `Spacer`, `Divider`.
          - Inputs & Controls: `Input` (`bind`, `placeholder`, `onSubmit`), `TextArea` (`bind`, `minHeight`, `maxHeight`, `onSubmit`), `AutoSuggestInput` (`bind`, `suggestions`/`suggestionsSource`, `onSubmit`, `onChange`), `Select` (`title`, `bind`, `options: [{value,label}]`, `onChange`), `RadioGroup` (`bind`, `options`, `orientation`, `onChange`), `CheckBox` (`bind`/`value`, `text`, `strikethrough`, `onToggle`), `Toggle` (`title`, `bind`, `onChange`), `Slider` (`title`, `bind`/`value`, `min`, `max`, `step`, `unit`, `onChange`), `NumberBox` (`title`, `bind`, `min`, `max`, `step`), `Rating` (`bind`/`value`, `max`, `allowHalfStar`, `onChange`), `DatePicker` (`bind`/`value`, `onChange`), `TimePicker` (`bind`/`value`, `onChange`), `ColorPicker` (`bind`/`value`, `presets`, `onChange`), `Segmented` (`bind`, `options`), `Button` (`text`, `icon`, `variant`: standard|accent|subtle|danger, `popover`, `onClick`), `IconButton` (`icon`, `size`: 24|28, `variant`: subtle|accent|danger, `tooltip`, `onClick`), `ControlTile`.
          - Vector Canvas, Charts & Data: `Canvas` (`viewBox`, `elements`/`elementsSource`: `rect|circle|ellipse|line|polyline|polygon|path|text`, `onItemClick`), `Table` (`collection`/`items`, `columns: [{key,header,width,align,format}]`, `sortable`, `striped`, `onRowClick`), `DonutChart` (`collection`/`items`, `titleField`, `valueField`, `colorField`, `centerTitle`, `centerValue`), `Chart` (`chartKind`: line|bar|area, `collection`/`items`, `xField`, `series: [{field,label,color}]`), `Text` (`text`, `style`: caption|body|bodyStrong|subtitle|title|hero|mono, `color`: default|secondary|muted|accent|emerald|cyan|violet|success|warning|danger), `Badge` (`text`, `tone`), `List` (`items`, `filter`, `allowReorder`, `onReorder`, `inlineEditField`, `onInlineCommit`, `emptyState`, `itemTemplate`, `hoverActions`), `RingGauge`, `BarList`, `Sparkline`, `HeatmapGrid`, `CalendarGrid`, `ProgressBar`, `ProgressRing`, `KeyValueRow`, `ProgressRow`, `DropZone`, `FileChip`, `EmptyState`.
          """
#if DESKBOX_STORE
        + "\n- Domain Presets & Sources: `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`; sources include `httpJson`, `rss`, `fileText`, etc.\n- Actions: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request` (`method`, `url`, `headers`, `body`, `stateMap`, `onHttpResponse`), `file.writeText`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `timer.*`, `pomodoro.*`, `countdown.*`.";
#else
        + "\n- Domain Presets & Sources: `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`; sources include `httpJson`, `rss`, `fileText`, `command`, etc.\n- Actions: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request` (`method`, `url`, `headers`, `body`, `stateMap`, `onHttpResponse`), `file.writeText`, `command.run` (`onCommandResult`), `ai.run`, `source.refresh`, `system.control`, `file.transform`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `timer.*`, `pomodoro.*`, `countdown.*`.";
#endif
}
