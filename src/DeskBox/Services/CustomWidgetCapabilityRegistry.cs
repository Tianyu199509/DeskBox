using System.Text;
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
                "capsuleTitle", "capsuleSummary", "capsuleBadge"
            ],
            ["$countdown"] =
            [
                "hero.title", "hero.days", "hero.subtitle", "hero.badge", "hero.isToday",
                "year.percent", "yearPercent", "year.remainingDays",
                "month.percent", "monthPercent", "month.remainingDays", "count",
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
            ["$clipboard"] = ["text", "preview", "length", "lines", "kind", "kindLabel", "hasText"],
            ["$drop"] = ["name", "path", "ext", "sizeText", "mode", "text"],
            ["$transform"] = ["status", "op", "sourcePath", "outputPath", "outputName", "sizeText", "resultText", "error"]
        };

    public static CustomWidgetCapabilitySpec? GetById(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id.Trim(), out CustomWidgetCapabilitySpec? spec)
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

            if (!ById.TryGetValue(id.Trim(), out CustomWidgetCapabilitySpec? spec))
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
        var sb = new StringBuilder(3072);
        sb.AppendLine("## DeskBox Native Capability & Component Directory (Schema v1)");
        sb.AppendLine("- Layout & Containers: `Stack` (vertical|horizontal), `Grid` (`columns`, `rows`, `column`, `row`; auto-flows multi-row items when `rows`/`row` omitted), `Tabs` (multi-page container: `bind`, `children` where each tab has `title`, `value`, `icon`), `Wrap`, `Scroll`, `Card` (`tone`: default|subtle|accent|warning|danger, max 2 nested levels), `Expander`, `Divider`, `Spacer`, `Text` (`style`: caption|body|bodyStrong|subtitle|title|hero|mono), `Image` (`src`, `height`, `stretch`, `cornerRadius`), `Badge`, `KeyValueRow`, `EmptyState`.");
        sb.AppendLine("- Interactive Controls: `Input` (`bind`, `onSubmit`), `TextArea`, `Select` (`options`), `Segmented` (`options`), `CheckBox` (`bind`/`value`, `onToggle`), `Toggle` (`bind`/`value`, `onChange`), `Slider` (`value`/`bind`, `min`, `max`, `step`, `unit`, `onChange` supporting `${$value}`), `NumberBox`, `Button` (`variant`: standard|accent|subtle|danger, `onClick`), `IconButton` (`icon` Segoe Fluent `\\uE700`..`\\uF8FF` or Emoji, `size`: 24|28, `onClick`), `ControlTile` (Win11 quick-settings tile: `icon`, `title`, `subtitle`, `value`/`bind`, `onClick`), `DropZone` (`title`, `subtitle`, `format`, `onDrop`).");
        sb.AppendLine("- Data Visualization & Domain Components: `RingGauge` (labeled circular KPI gauge: `progress`/`value`, `max`, `title`, `subtitle`, `unit`, `size`, `color`), `BarList` (ranked horizontal proportion bars: `collection`/`items`, `titleField`, `valueField`, `subtitleField`, `unit`, `maxItems`, `color`), `CalendarGrid` (`bind`, `collection`, `dateField`), `Sparkline`, `HeatmapGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`, `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`.");
        sb.AppendLine("- Built-in Host `sources`: `weather` (`$weather.*`, `forecast`), `calendar` (`$calendar.*`), `clock` (`$clock.*` + `tz('<zoneId>', '<format>')`), `systemMetrics` (`$sys.*`), `storageDrives` (`$drives.*` + `drives` collection), `deviceState` (Volume/Brightness/AudioDevice/Wi-Fi/Bluetooth/Media/KeepAwake/RecycleBin, `$device.*`), `clipboard` (`$clipboard.*`), `aiMonitor` (`$ai.*`), `countdown` (`$countdown.*`), `pomodoro` (`$pomodoro.*`), `httpJson` (supports `headers` with `${$secrets.<key>}`), `rss`.");
        sb.AppendLine("- Built-in Host `actions`: `state.set|toggle|increment` (supports event payload `${$value}`), `collection.prepend|append|update|remove|clear|exportMarkdown`, `text.transform` (pure in-memory `jsonPretty|jsonMinify|urlEncode|urlDecode|base64Encode|base64Decode|timestampToDate|dateToTimestamp|uuid|upper|lower|trim|wordCount|sha256|md5`, `$transform.*`), `launcher.open` (safe URI schemes `vscode://`, `obsidian://`, `ms-screenclip:`, `https://`, known folders `downloads|desktop|documents`, or local paths), `ai.run`, `file.transform` (PNG/JPG/WEBP/BMP/ICO + CSV/JSON/MD/SHA256), `file.pick|open|reveal`, `system.control` (`volume.set|step|muteToggle`, `brightness.set|step`, `audio.output`, `media.playPause|next|previous`, `power.keepAwakeToggle|lock`, `recycleBin.empty`, `radio.wifiToggle|bluetoothToggle`), `system.openSetting`, `reminder.notify`, `toast.show`, `source.refresh`, `clipboard.copy`, `url.open`, `pomodoro.*`, `countdown.*`.");
        sb.AppendLine("- Layout & Interaction Standards:");
        sb.AppendLine("  1. **No Duplicate Inner Header**: Every widget window ALREADY renders a host chrome header (`manifest.icon` + `manifest.name` + collapse/context menu). NEVER place a static widget-title header bar at the top of `view`; place utility `IconButton` actions inline on functional section headers or inside `Tabs`.");
        sb.AppendLine("  2. **Safe Root Viewport Inset & Bento Consistency**: Always set safe outer viewport padding on root `view` (`\"padding\": \"12,10,12,12\"`, NEVER `\"0\"` or omitted) so content never bleeds flush against the widget shell border. Keep sibling sections visually uniform (use sibling `Card` (`tone: \"subtle\"`) blocks OR clean `Divider` sections, max 2 `Card` levels). Calibrate `defaultSize` (`width`, `height`) to fit all initial sections without clipping; use `Tabs` when packing 3+ distinct panels.");
        sb.AppendLine("  3. **Direct Live Controls**: `Slider`, `Toggle`, and `Segmented` trigger `onChange` immediately (`${$value}` holds the live control value). NEVER add a redundant \"Apply / Sync\" button beside a live `Slider` or `Toggle`. For system sliders, bind `value: \"${$device.volume}\"` directly with `onChange: { \"type\": \"system.control\", \"targetId\": \"volume.set\", \"value\": \"${$value}\" }`.");
        sb.AppendLine("- Anti-Facade Honesty Rule: NEVER invent fake buttons/switches that only mutate unread dummy `state` strings when a requested feature exceeds DeskBox's sandbox (e.g. screen recording, video FFmpeg transcoding, reading private chat databases). Respond with `\"kind\": \"clarify\"` explaining the boundary and offering 2 supported DeskBox alternatives.");
        return sb.ToString().TrimEnd();
    }

    private static readonly Regex InlineCapabilityMentionRegex = new(
        @"@([A-Za-z0-9][A-Za-z0-9-]*)",
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
            string rawId = match.Groups[1].Value;
            if (ById.TryGetValue(rawId, out CustomWidgetCapabilitySpec? spec) && seen.Add(spec.Id))
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
        @"(\$sys|\$weather|\$calendar|\$device|\$pomodoro|\$pomo|\$countdown|\$ai|\$time|\$clock|\$drives|\$clipboard|\$drop|\$transform)\.([A-Za-z0-9_.]+)",
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
            return subPath is "daysLeft" or "days" or "title" or "subtitle";
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
                           or "fileName" or "size" or "result" or "text" or "hash";
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
        CollectActionPrimitives(node.OnClick, actions);
        CollectActionPrimitives(node.OnSubmit, actions);
        CollectActionPrimitives(node.OnChange, actions);
        CollectActionPrimitives(node.OnDropActions, actions);
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
    public static string GenerateApiReferenceMarkdown(bool isChinese = true)
    {
        var sb = new StringBuilder(24576);
        if (isChinese)
        {
            sb.AppendLine("# DeskBox 自定义组件与 API 参考手册 (`Schema v1`)");
            sb.AppendLine();
            sb.AppendLine("> 本文档由 DeskBox 运行时注册表自动生成。在共创工作台中可通过输入 `@模块ID` 或点击 **「能力模块」** 将指定规范注入对话上下文；亦可在外部编辑器中直接修改 `manifest.json`（保存后 200ms 自动增量校验并热重载）。");
            sb.AppendLine();
            sb.AppendLine("## 架构与布局规范");
            sb.AppendLine("- **禁止重复顶部标题栏**：宿主窗口外框已提供标准标题栏（显示 `manifest.icon`、`manifest.name`、胶囊折叠及右键菜单）。`view` 根节点内严禁重复绘制静态组件标题栏，局部刷新或操作按钮应内嵌于对应业务分区标题行或 `Tabs` 页签旁。");
            sb.AppendLine("- **Bento 卡片层级一致性**：同级功能分区应保持统一的容器层级（统一采用 `Card` (`tone: \"subtle\"`) 或统一采用 `Divider` 分隔），`Card` 最大嵌套深度不超过 2 层。`Grid` 指定 `columns` 后若省略 `rows` 与子项 `row`，渲染引擎将按列数自动折行排布。");
            sb.AppendLine("- **即时响应交互模型**：`Slider`、`Toggle`、`Segmented` 等控件通过 `onChange` 实时生效（事件载荷通过 `${$value}` 读取），无需在滑块或开关旁额外放置“应用/同步”确认按钮。");
        }
        else
        {
            sb.AppendLine("# DeskBox Custom Widget Capability & API Reference (`Schema v1`)");
            sb.AppendLine();
            sb.AppendLine("> Generated from the DeskBox runtime capability registry. Reference `@module-id` or use **Capabilities** in Studio to attach exact contracts to the prompt context, or edit `manifest.json` directly for 200ms hot-reload.");
            sb.AppendLine();
            sb.AppendLine("## Architecture & Layout Conventions");
            sb.AppendLine("- **No Duplicate Inner Header**: The host window chrome already renders the widget icon, title, collapse toggle, and context menu. Do not place a duplicate static header bar at the top of `view`.");
            sb.AppendLine("- **Consistent Bento Hierarchy**: Keep sibling functional sections visually uniform (`Card` with `tone: \"subtle\"` or `Divider`-separated rows; max 2 nested `Card` levels). Multi-item `Grid` containers auto-flow into implicit rows when `rows`/`row` are omitted.");
            sb.AppendLine("- **Direct Live Controls**: `Slider`, `Toggle`, and `Segmented` dispatch `onChange` immediately via `${$value}`; avoid redundant confirmation buttons next to live controls.");
        }

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
            sb.Append("## ").AppendLine(GetCategoryDisplayName(cat, isChinese));
            foreach (CustomWidgetCapabilitySpec spec in All.Where(c => c.Category == cat))
            {
                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.GetTitle(isChinese))
                  .Append(" (`@").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.GetSummary(isChinese));
                if (spec.RequiredCapabilityIds.Count > 0)
                {
                    sb.Append(isChinese ? "- **关联依赖模块**：" : "- **Required modules**: ")
                      .AppendLine(string.Join(", ", spec.RequiredCapabilityIds.Select(static d => $"`{d}`")));
                }

                if (spec.Members.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(isChinese
                        ? "| 接口签名 / 变量 / 属性 | 类别 | 规范说明 |"
                        : "| Signature / Token / Property | Kind | Specification |");
                    sb.AppendLine("| :--- | :--- | :--- |");
                    foreach (CustomWidgetApiMemberDoc m in spec.Members)
                    {
                        sb.Append("| `").Append(m.Name).Append("` | ").Append(m.Kind).Append(" | ")
                          .Append(isChinese ? m.DescriptionZh : m.DescriptionEn).AppendLine(" |");
                    }
                }

                if (!string.IsNullOrWhiteSpace(spec.GoldenSnippetJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(spec.GoldenSnippetJson.Trim());
                    sb.AppendLine("```");
                }
            }
        }

        sb.AppendLine();
        if (isChinese)
        {
            sb.AppendLine("## 表达式语法与上下文变量规范");
            sb.AppendLine("- **插值与运算符**：使用 `${...}` 包裹表达式；支持 `??`（空值合并）、`? :`（三元条件）、`==` / `!=` / `>` / `>=` / `<` / `<=`、`&&` / `||` / `!` 及数值四则运算。");
            sb.AppendLine("- **交互事件载荷 `${$value}`**：在 `Slider`、`Toggle`、`Segmented`、`Select`、`CheckBox`、`NumberBox` 的 `onChange` / `onToggle` 动作中，`${$value}` 表示控件变更后的即时值。");
            sb.AppendLine("- **多时区时间函数 `tz('<zoneId>', '<format>')`**：支持 IANA 时区（如 `Asia/Shanghai`、`America/New_York`、`Europe/London`、`Asia/Tokyo`）、固定偏移（如 `UTC+8`、`UTC-5`）及标准格式化字符串（如 `HH:mm:ss`、`MM-dd HH:mm`）。");
        }
        else
        {
            sb.AppendLine("## Expression Syntax & Context Variables");
            sb.AppendLine("- **Operators**: Wrap expressions in `${...}`; supports null-coalescing `??`, ternary `? :`, comparisons `== != > >= < <=`, logical `&& || !`, and numeric arithmetic.");
            sb.AppendLine("- **Event Payload `${$value}`**: Inside `onChange` / `onToggle` of `Slider`, `Toggle`, `Segmented`, `Select`, `CheckBox`, and `NumberBox`, `${$value}` resolves to the control's new value.");
            sb.AppendLine("- **Timezone Function `tz('<zoneId>', '<format>')`**: Supports IANA/Windows timezone IDs (`Asia/Shanghai`, `America/New_York`, `Europe/London`, `Asia/Tokyo`), UTC offsets (`UTC+8`, `UTC-5`), and standard format patterns (`HH:mm:ss`).");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string GetCategoryDisplayName(CustomWidgetCapabilityCategory category, bool isChinese) =>
        category switch
        {
            CustomWidgetCapabilityCategory.DataSource => isChinese ? "数据源" : "Data Sources",
            CustomWidgetCapabilityCategory.Component => isChinese ? "界面组件" : "UI Components",
            CustomWidgetCapabilityCategory.Action => isChinese ? "动作指令" : "Actions",
            CustomWidgetCapabilityCategory.ConfigAndSecurity => isChinese ? "配置与凭据" : "Config & Security",
            _ => string.Empty
        };

    private static IReadOnlyList<CustomWidgetCapabilitySpec> BuildRegistry() =>
    [
        // ──────────────── 1. DATA SOURCES ────────────────
        new(
            Id: "weather",
            Emoji: "🌤️",
            Glyph: "\uE753",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "天气预报",
            TitleEn: "Weather Forecast",
            SummaryZh: "内置免密钥气象服务，提供实时温湿度、风速、紫外线与未来 7 天预报。",
            SummaryEn: "Zero-key weather service providing live conditions, UV/wind metrics, and a 7-day forecast.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["weather"],
            TriggerTokenPrefixes: ["$weather."],
            TriggerKeywords: ["气象", "天气", "气温", "温度", "下雨", "晴天", "阴晴", "空气", "预报", "穿衣", "weather", "forecast", "temperature", "humidity", "rain", "uv"],
            Members:
            [
                new("{ \"type\": \"weather\", \"targetCollection\": \"forecast\" }", "Source", "在 sources 中声明内置天气源；省略 city 时自动跟随默认城市", "Declares built-in weather source; inherits host city when omitted"),
                new("${$weather.city} / ${$weather.tempText} / ${$weather.condition}", "Token", "城市名、实时温度（如 22°C）、天气状况文案", "City name, formatted temperature (22°C), and condition"),
                new("${$weather.icon} / ${$weather.rangeText} / ${$weather.feelsLikeText}", "Token", "天气图标、当日高低温度区间、体感温度", "Weather icon, daily temperature range, and feels-like text"),
                new("${$weather.humidity} / ${$weather.wind} / ${$weather.precipProb} / ${$weather.uv}", "Token", "湿度、风速、降水概率、紫外线指数", "Humidity, wind speed, precipitation probability, and UV index"),
                new("$item.weekday / $item.icon / $item.low / $item.high", "Collection", "forecast 集合中的未来 7 天逐日预报字段", "7-day forecast item fields in collections.forecast")
            ],
            CompactContractMarkdown: """
                - Prefer built-in `"sources": [{ "type": "weather", "city": "${$settings.city}", "targetCollection": "forecast", "intervalMs": 900000 }]` over external weather URLs (zero API key, cached, global).
                - Declare `"collections": { "forecast": { "maxItems": 7 } }` when rendering multi-day forecasts.
                - Exact `$weather.*` tokens: `${$weather.city}`, `${$weather.temp}`, `${$weather.tempText}`, `${$weather.feelsLikeText}`, `${$weather.high}`, `${$weather.low}`, `${$weather.rangeText}`, `${$weather.condition}`, `${$weather.icon}`, `${$weather.humidity}`, `${$weather.wind}`, `${$weather.precipProb}`, `${$weather.uv}`, `${$weather.sunrise}`, `${$weather.sunset}`, `${$weather.updatedAt}`.
                - Each item in `forecast` exposes: `${$item.weekday}`, `${$item.shortDate}`, `${$item.icon}`, `${$item.condition}`, `${$item.low}`, `${$item.high}`, `${$item.range}`, `${$item.precip}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "weather", "city": "${$settings.city}", "targetCollection": "forecast" }],
                  "collections": { "forecast": { "maxItems": 7 } },
                  "view": {
                    "type": "Card",
                    "tone": "subtle",
                    "children": [
                      { "type": "Text", "text": "${$weather.icon} ${$weather.city} · ${$weather.tempText}", "style": "title" },
                      { "type": "Text", "text": "${$weather.condition}  ${$weather.rangeText} · 湿度 ${$weather.humidity}", "style": "caption", "color": "secondary" }
                    ]
                  }
                }
                """),

        new(
            Id: "system-metrics",
            Emoji: "📊",
            Glyph: "\uE9D9",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "硬件监控",
            TitleEn: "Hardware Monitor",
            SummaryZh: "实时采集 CPU、内存、显卡显存、磁盘、网速、电池与高占用进程。",
            SummaryEn: "Low-overhead telemetry for CPU, RAM, GPU/VRAM, Disk, Network speed, Battery, and Top Processes.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["StatCard", "GpuCard", "ProcessList"],
            TriggerSourceOrActionTypes: ["systemMetrics"],
            TriggerTokenPrefixes: ["$sys."],
            TriggerKeywords: ["遥测", "硬件", "系统监控", "cpu", "内存", "ram", "显卡", "gpu", "显存", "vram", "网速", "磁盘", "硬盘", "电池", "电量", "进程", "开机时间", "hardware", "system monitor", "memory", "disk", "network speed", "battery", "process"],
            Members:
            [
                new("{ \"type\": \"systemMetrics\", \"intervalMs\": 2000 }", "Source", "在 sources 中启用硬件采样（隐藏或折叠时自动暂停）", "Subscribes to live hardware telemetry"),
                new("${$sys.cpu.percent} / ${$sys.memory.percent} / ${$sys.memory.summaryText}", "Token", "CPU 使用率、内存使用率、已用容量摘要", "CPU %, RAM %, and formatted memory summary"),
                new("${$sys.gpu.name} / ${$sys.gpu.percent} / ${$sys.gpu.vramSummary}", "Token", "显卡名称、核心占用率、显存用量", "GPU name, core utilization %, and VRAM summary"),
                new("${$sys.net.downText} / ${$sys.net.upText} / ${$sys.net.summaryText}", "Token", "实时下载网速、上传网速、合并网速文本", "Live download/upload network speed"),
                new("${$sys.disk.percent} / ${$sys.disk.freeGb} / ${$sys.battery.statusText} / ${$sys.uptime.text}", "Token", "系统盘占用、剩余容量、电源状态与开机时长", "Primary disk %, free GB, battery status, and uptime"),
                new("StatCard / GpuCard / ProcessList", "Component", "带趋势线的硬件卡片、显卡双栏卡与进程排行列表", "Built-in KPI card, GPU card, and top-process list")
            ],
            CompactContractMarkdown: """
                - MUST declare `"sources": [{ "type": "systemMetrics", "intervalMs": 2000 }]` whenever using `$sys.*`, `StatCard`, `GpuCard`, or `ProcessList`.
                - Exact `$sys.*` tokens: `${$sys.cpu.percent}`, `${$sys.memory.percent}`, `${$sys.memory.usedGb}`, `${$sys.memory.totalGb}`, `${$sys.memory.summaryText}`, `${$sys.memory.compactText}`, `${$sys.gpu.name}`, `${$sys.gpu.percent}`, `${$sys.gpu.vramPercent}`, `${$sys.gpu.vramSummary}`, `${$sys.disk.name}`, `${$sys.disk.percent}`, `${$sys.disk.freeGb}`, `${$sys.disk.summary}`, `${$sys.net.downText}`, `${$sys.net.upText}`, `${$sys.net.summaryText}`, `${$sys.battery.percent}`, `${$sys.battery.statusText}`, `${$sys.uptime.text}`.
                - Built-in composite hardware nodes:
                  - `{ "type": "StatCard", "title": "CPU", "value": "${$sys.cpu.percent}", "unit": "%", "progress": "${$sys.cpu.percent}", "sparkline": "cpu", "color": "accent" }` (`sparkline`: `"cpu"` | `"memory"` | `"gpu"` | `"net"`).
                  - `{ "type": "GpuCard" }`.
                  - `{ "type": "ProcessList", "maxItems": 5, "mode": "memory" }` (`mode`: `"memory"` | `"cpu"`).
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "systemMetrics", "intervalMs": 2000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      {
                        "type": "Grid",
                        "columns": "*,*",
                        "spacing": 8,
                        "children": [
                          { "type": "StatCard", "title": "CPU", "value": "${$sys.cpu.percent}", "unit": "%", "progress": "${$sys.cpu.percent}", "sparkline": "cpu", "color": "accent" },
                          { "type": "StatCard", "title": "RAM", "value": "${$sys.memory.percent}", "unit": "%", "subtitle": "${$sys.memory.compactText}", "progress": "${$sys.memory.percent}", "sparkline": "memory", "color": "cyan" }
                        ]
                      },
                      { "type": "GpuCard" }
                    ]
                  }
                }
                """),

        new(
            Id: "storage-drives",
            Emoji: "💾",
            Glyph: "\uEDA2",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "磁盘空间",
            TitleEn: "Storage Drives",
            SummaryZh: "列出本机各磁盘分区的盘符、卷标、使用率与剩余空间。",
            SummaryEn: "Lists all mounted local drives with volume label, used percentage, and free/total GB.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["storageDrives"],
            TriggerTokenPrefixes: ["$drives."],
            TriggerKeywords: ["磁盘分区", "多盘符", "c盘", "d盘", "硬盘空间", "存储空间", "容量监控", "剩余空间", "drives", "partitions", "disk space", "storage volumes"],
            Members:
            [
                new("{ \"type\": \"storageDrives\", \"targetCollection\": \"drives\" }", "Source", "定期扫描本机磁盘分区并写入 drives 集合", "Polls mounted local volumes into targetCollection"),
                new("${$drives.count} / ${$drives.summary} / ${$drives.primaryPercent}", "Token", "分区总数、多盘符占用摘要、系统盘使用率", "Mounted drive count, summary string, and primary drive %"),
                new("$item.name / $item.label / $item.percent / $item.freeGb / $item.summary", "Collection", "drives 集合中每个分区的盘符、卷标、百分比与容量摘要", "Per-drive fields in collections.drives")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "storageDrives", "targetCollection": "drives", "intervalMs": 15000 }]` and `"collections": { "drives": { "maxItems": 16 } }`.
                - Summary tokens: `${$drives.count}`, `${$drives.summary}`, `${$drives.primaryPercent}`, `${$drives.primaryFreeGb}`, `${$drives.primaryTotalGb}`.
                - Each record in `collections.drives` exposes: `${$item.name}` (`"C:"`), `${$item.label}`, `${$item.title}` (`"C: (System)"`), `${$item.format}` (`"NTFS"`), `${$item.percent}` (`0`..`100`), `${$item.usedGb}`, `${$item.freeGb}`, `${$item.totalGb}`, `${$item.summary}` (`"128.4 / 512.0 GB"`).
                - Pair directly with `BarList` (`"collection": "drives", "titleField": "title", "valueField": "percent", "subtitleField": "summary", "unit": "%"`) for a clean multi-drive capacity view.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "storageDrives", "targetCollection": "drives", "intervalMs": 15000 }],
                  "collections": { "drives": { "maxItems": 16 } },
                  "view": {
                    "type": "Card",
                    "tone": "subtle",
                    "children": [
                      {
                        "type": "BarList",
                        "collection": "drives",
                        "titleField": "title",
                        "valueField": "percent",
                        "subtitleField": "summary",
                        "unit": "%",
                        "color": "accent"
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "world-clock",
            Emoji: "🌍",
            Glyph: "\uE823",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "世界时钟",
            TitleEn: "World Clock",
            SummaryZh: "提供秒级刷新的多时区转换函数 `tz()`、UTC 时间与 Unix 时间戳。",
            SummaryEn: "Provides 1-second live UTC/Unix clock tokens and the `tz('<zoneId>', '<format>')` timezone function.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["clock"],
            TriggerTokenPrefixes: ["$clock.", "tz("],
            TriggerKeywords: ["全球", "世界时钟", "多时区", "时区", "伦敦时间", "纽约时间", "东京时间", "美股时间", "utc", "unix时间戳", "world clock", "timezone", "est", "pst", "gmt"],
            Members:
            [
                new("{ \"type\": \"clock\", \"intervalMs\": 1000 }", "Source", "启用实时时钟刷新（支持秒级或分钟级）", "Enables live clock tick updates"),
                new("${tz('America/New_York', 'HH:mm')} / ${tz('Europe/London', 'HH:mm:ss')}", "Function", "按 IANA 时区或固定偏移（如 UTC+9）格式化时间", "Formats current time in any IANA timezone or UTC offset"),
                new("${$clock.fullTime} / ${$clock.utcTime} / ${$clock.localOffset} / ${$clock.unixSeconds}", "Token", "本地时间、UTC 时间、时区偏移与 Unix 秒级时间戳", "Local time, UTC time, UTC offset, and Unix seconds")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "clock", "intervalMs": 1000 }]` when displaying live clocks or multi-timezone panels.
                - Built-in expression function `tz('<zoneId>', '<format>')`:
                  - Supported `<zoneId>`: IANA IDs (`"Asia/Shanghai"`, `"Asia/Tokyo"`, `"Europe/London"`, `"Europe/Berlin"`, `"America/New_York"`, `"America/Los_Angeles"`, `"Australia/Sydney"`, `"Asia/Singapore"`), `"UTC"`, `"local"`, or fixed offsets (`"UTC+8"`, `"UTC-5"`).
                  - Supported `<format>`: standard .NET time patterns (`"HH:mm"`, `"HH:mm:ss"`, `"MM-dd HH:mm"`, `"ddd HH:mm"`).
                - Exact `$clock.*` tokens: `${$clock.fullTime}`, `${$clock.seconds}`, `${$clock.utcTime}`, `${$clock.utcFullTime}`, `${$clock.utcDate}`, `${$clock.localOffset}`, `${$clock.unixSeconds}`, `${$clock.iso}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "clock", "intervalMs": 1000 }],
                  "view": {
                    "type": "Grid",
                    "columns": "*,*",
                    "spacing": 8,
                    "children": [
                      { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "北京 (UTC+8)", "style": "caption", "color": "secondary" }, { "type": "Text", "text": "${tz('Asia/Shanghai', 'HH:mm:ss')}", "style": "title" }] },
                      { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "纽约 (EST/EDT)", "style": "caption", "color": "secondary" }, { "type": "Text", "text": "${tz('America/New_York', 'HH:mm:ss')}", "style": "title" }] },
                      { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "伦敦 (GMT/BST)", "style": "caption", "color": "secondary" }, { "type": "Text", "text": "${tz('Europe/London', 'HH:mm:ss')}", "style": "title" }] },
                      { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "东京 (JST)", "style": "caption", "color": "secondary" }, { "type": "Text", "text": "${tz('Asia/Tokyo', 'HH:mm:ss')}", "style": "title" }] }
                    ]
                  }
                }
                """),

        new(
            Id: "ai-monitor",
            Emoji: "🤖",
            Glyph: "\uE950",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "AI 用量监控",
            TitleEn: "AI Quota Monitor",
            SummaryZh: "统计本机 Claude Code / Codex Token 消耗，并查询主流模型平台账户余额。",
            SummaryEn: "Aggregates local Claude Code / Codex token logs and queries cloud provider balances.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["AiFocusCard", "AiRadarList", "AiTokensCard", "AiTrendCard"],
            TriggerSourceOrActionTypes: ["aiMonitor", "ai.refresh", "ai.selectProvider"],
            TriggerTokenPrefixes: ["$ai."],
            TriggerKeywords: ["配额", "额度", "余额", "token消耗", "账单", "开销", "用量", "claude额度", "codex用量", "deepseek余额", "openrouter余额", "quota", "token usage", "billing", "wallet"],
            Members:
            [
                new("{ \"type\": \"aiMonitor\", \"intervalMs\": 60000 }", "Source", "启用本地日志统计与服务商配额查询", "Enables local token log parser and balance polling"),
                new("${$ai.todayTokens} / ${$ai.todayCost} / ${$ai.monthTokens} / ${$ai.monthCost}", "Token", "今日与本月累计 Token 用量及估算开销", "Daily and monthly token counts and estimated cost"),
                new("${$ai.focus.name} / ${$ai.focus.value} / ${$ai.focus.subtext}", "Token", "当前焦点服务商名称、余额数值与状态说明", "Focused provider name, balance value, and subtitle"),
                new("AiFocusCard / AiTokensCard / AiRadarList / AiTrendCard", "Component", "内置焦点配额卡、Token 统计卡、多平台列表与趋势卡", "Built-in quota card, token summary, radar list, and trend card")
            ],
            CompactContractMarkdown: """
                - For monitoring Claude Code, Codex, DeepSeek, GLM, Kimi, or OpenRouter usage/balance, MUST declare `"sources": [{ "type": "aiMonitor", "intervalMs": 60000 }]`.
                - Native composite nodes: `{ "type": "AiFocusCard" }`, `{ "type": "AiRadarList" }`, `{ "type": "AiTokensCard" }`, `{ "type": "AiTrendCard" }`.
                - Exact `$ai.*` tokens: `${$ai.todayTokens}`, `${$ai.todayCost}`, `${$ai.monthTokens}`, `${$ai.monthCost}`, `${$ai.cacheHitPercent}`, `${$ai.focus.name}`, `${$ai.focus.value}`, `${$ai.focus.subtext}`, `${$ai.capsuleTitle}`, `${$ai.capsuleSummary}`, `${$ai.capsuleBadge}`.
                - Actions: `{ "type": "ai.refresh" }`, `{ "type": "ai.selectProvider", "value": "deepseek" }`.
                - For custom OpenAI-compatible API gateways (e.g. SiliconFlow / OneAPI), pair `secret-vault` (`"type": "secret"`) with `httpJson` `headers`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "aiMonitor", "intervalMs": 60000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      { "type": "AiFocusCard" },
                      { "type": "AiTokensCard" },
                      { "type": "AiRadarList" }
                    ]
                  }
                }
                """),

        new(
            Id: "calendar",
            Emoji: "🗓️",
            Glyph: "\uE787",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "日历与节气",
            TitleEn: "Lunar & Calendar",
            SummaryZh: "提供公历日期、农历干支纪年、二十四节气与法定传统节日。",
            SummaryEn: "Built-in Gregorian date, Chinese Lunar Ganzhi, solar terms, and festival tokens.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["calendar"],
            TriggerTokenPrefixes: ["$calendar."],
            TriggerKeywords: ["公历", "历法", "农历", "阴历", "节气", "节日", "干支", "黄历", "几月几号", "星期几", "lunar", "festival", "holiday", "solar term"],
            Members:
            [
                new("${$calendar.date} / ${$calendar.monthTitle} / ${$calendar.weekday}", "Token", "当日日期 (yyyy-MM-dd)、年月标题、星期名称", "Current ISO date, month header, and weekday"),
                new("${$calendar.lunar} / ${$calendar.lunarTitle} / ${$calendar.festival}", "Token", "农历月日、干支纪年、当日节气或节日", "Lunar day, Ganzhi year/month, and festival/solar-term"),
                new("${$calendar.subtitle} / ${$calendar.weekOfYear}", "Token", "智能副标题（优先显示节日，否则显示农历）与周次", "Smart subtitle (festival or lunar) and ISO week number")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "calendar", "intervalMs": 60000 }]` when displaying live date/lunar/festival information.
                - Exact `$calendar.*` tokens: `${$calendar.date}` (`yyyy-MM-dd`), `${$calendar.year}`, `${$calendar.month}`, `${$calendar.day}`, `${$calendar.weekday}`, `${$calendar.weekdayShort}`, `${$calendar.monthTitle}`, `${$calendar.lunar}`, `${$calendar.lunarTitle}`, `${$calendar.festival}`, `${$calendar.subtitle}`, `${$calendar.weekOfYear}`, `${$calendar.daysInMonth}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "calendar", "intervalMs": 60000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 2,
                    "children": [
                      { "type": "Text", "text": "${$calendar.monthTitle} · ${$calendar.weekday}", "style": "subtitle" },
                      { "type": "Text", "text": "${$calendar.lunarTitle} ${$calendar.festival}", "style": "caption", "color": "accent" }
                    ]
                  }
                }
                """),

        new(
            Id: "countdown",
            Emoji: "🎂",
            Glyph: "\uEB51",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "纪念日与倒数日",
            TitleEn: "Countdowns",
            SummaryZh: "支持公历与农历生日、纪念日循环倒数，到期自动发送系统通知。",
            SummaryEn: "Solar & Lunar recurring birthday/anniversary countdown engine with Windows Toast reminders.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["CountdownHero", "CountdownList", "TimeProgressCard"],
            TriggerSourceOrActionTypes: ["countdown", "countdown.pin", "countdown.delete"],
            TriggerTokenPrefixes: ["$countdown."],
            TriggerKeywords: ["生日", "纪念日", "倒数日", "倒计时", "周年", "发薪日", "过年", "春节", "距离", "天数", "birthday", "anniversary", "countdown", "payday", "days until"],
            Members:
            [
                new("{ \"type\": \"countdown\", \"intervalMs\": 60000 }", "Source", "启用公农历循环倒数计算与每日到期提醒", "Enables recurring countdown calculation and daily reminders"),
                new("CountdownHero / CountdownList / TimeProgressCard", "Component", "置顶倒数大卡、带新增编辑弹窗的列表、年月进度卡", "Pinned hero card, interactive countdown list, and progress card"),
                new("${$countdown.hero.title} / ${$countdown.hero.days} / ${$countdown.hero.subtitle}", "Token", "置顶事件标题、剩余天数、目标日期说明", "Pinned event title, remaining days, and date subtitle")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "countdown", "intervalMs": 60000 }]` and `"collections": { "events": { "maxItems": 100 } }`.
                - Native interactive components: `{ "type": "CountdownHero" }`, `{ "type": "CountdownList" }` (includes built-in solar/lunar add & edit flyout), `{ "type": "TimeProgressCard" }`.
                - Event record fields in `collections.events`: `title`, `emoji` (`"🎂"`), `date` (`"yyyy-MM-dd"`), `calendar` (`"solar"` | `"lunar"`), `repeat` (`"yearly"` | `"monthly"` | `"weekly"` | `"none"`), `pinned` (`"true"` | `"false"`).
                - Exact `$countdown.*` tokens: `${$countdown.hero.title}`, `${$countdown.hero.days}`, `${$countdown.hero.subtitle}`, `${$countdown.hero.badge}`, `${$countdown.capsuleTitle}`, `${$countdown.capsuleSummary}`, `${$countdown.capsuleBadge}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "countdown", "intervalMs": 60000 }],
                  "collections": { "events": { "maxItems": 100 } },
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      { "type": "CountdownHero" },
                      { "type": "CountdownList" }
                    ]
                  }
                }
                """),

        new(
            Id: "pomodoro",
            Emoji: "🍅",
            Glyph: "\uE916",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "番茄专注",
            TitleEn: "Pomodoro Timer",
            SummaryZh: "无漂移专注、短休、长休计时状态机，支持完成提醒与今日统计。",
            SummaryEn: "Drift-free Focus / Break timer with system notifications and daily session stats.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["PomodoroCard"],
            TriggerSourceOrActionTypes: ["pomodoro", "pomodoro.toggle", "pomodoro.reset", "pomodoro.skip"],
            TriggerTokenPrefixes: ["$pomodoro."],
            TriggerKeywords: ["番茄钟", "专注", "计时器", "工作法", "休息提醒", "pomodoro", "focus timer"],
            Members:
            [
                new("{ \"type\": \"pomodoro\", \"intervalMs\": 1000 }", "Source", "启用番茄计时状态机秒级同步", "Enables 1-second Pomodoro timer sync"),
                new("PomodoroCard", "Component", "自带环形倒计时、阶段标签与启停按钮的标准专注卡片", "Complete native Pomodoro card with ring gauge and controls"),
                new("pomodoro.toggle / pomodoro.reset / pomodoro.skip / pomodoro.extend", "Action", "启动或暂停、重置阶段、跳过阶段、延长时长", "Start/pause, reset, skip phase, or extend session")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "pomodoro", "intervalMs": 1000 }]`.
                - Render `{ "type": "PomodoroCard" }` or build custom layouts using `${$pomodoro.remainingText}`, `${$pomodoro.phaseLabel}`, `${$pomodoro.isRunning}`, `${$pomodoro.progressPercent}`, `${$pomodoro.completedToday}` with actions `pomodoro.toggle`, `pomodoro.reset`, `pomodoro.skip`, `pomodoro.extend`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "pomodoro", "intervalMs": 1000 }],
                  "view": { "type": "PomodoroCard" }
                }
                """),

        new(
            Id: "device-state",
            Emoji: "🎛️",
            Glyph: "\uE9E9",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "设备与媒体状态",
            TitleEn: "Device & Media State",
            SummaryZh: "读取系统音量、亮度、Wi-Fi/蓝牙、正在播放曲目封面及常亮状态。",
            SummaryEn: "Reads live volume, brightness, Wi-Fi/Bluetooth, SMTC media track/cover, and Keep-Awake state.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["deviceState"],
            TriggerTokenPrefixes: ["$device."],
            TriggerKeywords: ["音量", "静音", "亮度", "音频设备", "wifi", "无线网", "蓝牙", "常亮", "防休眠", "防锁屏", "回收站", "正在播放", "专辑封面", "控制中心", "volume", "brightness", "bluetooth", "keep awake", "recycle bin", "media", "control center"],
            Members:
            [
                new("{ \"type\": \"deviceState\", \"intervalMs\": 3000 }", "Source", "订阅本机设备与全局媒体播放状态", "Subscribes to live device & SMTC media state"),
                new("${$device.volume} / ${$device.muted} / ${$device.audioDeviceName}", "Token", "主音量百分比 (0-100)、静音状态、当前音频输出设备", "Master volume 0-100, mute flag, and audio output device"),
                new("${$device.brightness} / ${$device.brightnessText}", "Token", "屏幕亮度百分比 (0-100) 与状态文案", "Display brightness 0-100 and status text"),
                new("${$device.wifiEnabled} / ${$device.bluetoothEnabled} / ${$device.keepAwake}", "Token", "Wi-Fi、蓝牙与防休眠屏幕常亮的开关状态", "Wi-Fi, Bluetooth, and Keep-Awake toggle states"),
                new("${$device.mediaTitle} / ${$device.mediaArtist} / ${$device.mediaToggleIcon} / ${$device.mediaCover}", "Token", "当前播放曲目、歌手、动态播放/暂停图标及封面图路径", "SMTC media title, artist, play/pause glyph, and cover path")
            ],
            CompactContractMarkdown: """
                - MUST declare `"sources": [{ "type": "deviceState", "intervalMs": 3000 }]` whenever reading `$device.*` tokens.
                - Exact `$device.*` tokens:
                  - Audio & Display: `${$device.volume}` (`0`..`100`), `${$device.volumeText}`, `${$device.muted}`, `${$device.audioDeviceName}`, `${$device.brightness}` (`0`..`100`), `${$device.brightnessText}`.
                  - Radios & Power: `${$device.wifiEnabled}`, `${$device.wifiStatus}`, `${$device.bluetoothEnabled}`, `${$device.bluetoothStatus}`, `${$device.keepAwake}`, `${$device.keepAwakeStatus}`, `${$device.darkTheme}`, `${$device.themeStatus}`, `${$device.recycleBinCount}`, `${$device.recycleBinStatus}`.
                  - Global Media (SMTC): `${$device.mediaPlaying}`, `${$device.mediaStatus}`, `${$device.mediaToggleIcon}` (`\uE769` when playing, `\uE768` when paused — bind directly to `IconButton.icon`!), `${$device.mediaCover}` (bind to `Image.src`), `${$device.mediaTitle}`, `${$device.mediaArtist}`, `${$device.mediaSummary}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "deviceState", "intervalMs": 3000 }],
                  "view": {
                    "type": "Card",
                    "tone": "subtle",
                    "children": [
                      {
                        "type": "Grid",
                        "columns": "Auto,*,Auto,Auto,Auto",
                        "spacing": 8,
                        "children": [
                          { "type": "Image", "column": 0, "src": "${$device.mediaCover}", "width": 36, "height": 36, "cornerRadius": 6, "visible": "${$device.mediaCover != ''}" },
                          { "type": "Stack", "column": 1, "spacing": 2, "children": [{ "type": "Text", "text": "${$device.mediaTitle}", "style": "bodyStrong", "maxLines": 1 }, { "type": "Text", "text": "${$device.mediaArtist != '' ? $device.mediaArtist : $device.mediaStatus}", "style": "caption", "color": "secondary", "maxLines": 1 }] },
                          { "type": "IconButton", "column": 2, "icon": "\uE892", "size": 28, "onClick": { "type": "system.control", "targetId": "media.previous" } },
                          { "type": "IconButton", "column": 3, "icon": "${$device.mediaToggleIcon}", "size": 28, "variant": "accent", "onClick": { "type": "system.control", "targetId": "media.playPause" } },
                          { "type": "IconButton", "column": 4, "icon": "\uE893", "size": 28, "onClick": { "type": "system.control", "targetId": "media.next" } }
                        ]
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "http-and-rss-feeds",
            Emoji: "🌐",
            Glyph: "\uE774",
            Category: CustomWidgetCapabilityCategory.DataSource,
            TitleZh: "网络接口与 RSS",
            TitleEn: "HTTP JSON & RSS",
            SummaryZh: "拉取远程 JSON 接口或 RSS/Atom 订阅流，内置离线缓存与鉴权请求头。",
            SummaryEn: "Fetches REST JSON & RSS/Atom feeds with disk caching and optional auth headers.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["httpJson", "rss", "source.refresh", "refreshSource"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["远程", "热榜", "热搜", "新闻", "资讯", "订阅", "rss", "接口", "api", "行情", "汇率", "币价", "知乎", "少数派", "v2ex", "hacker news", "github", "news", "feed", "trending", "price"],
            Members:
            [
                new("{ \"type\": \"rss\", \"url\": \"https://...\", \"targetCollection\": \"items\" }", "Source", "解析 RSS / Atom 订阅并自动提取 title, summary, link, pubDate", "Parses RSS/Atom feeds into title, summary, link, pubDate"),
                new("{ \"type\": \"httpJson\", \"url\": \"...\", \"itemsPath\": \"$.data\" }", "Source", "请求 JSON 接口，通过 stateMap 提取单值或 itemsPath 映射列表", "Fetches JSON APIs with scalar stateMap or array itemsPath/fieldMap"),
                new("{ \"type\": \"source.refresh\" }", "Action", "立即手动刷新数据源", "Immediately refreshes data sources")
            ],
            CompactContractMarkdown: """
                - `rss` source auto-populates `targetCollection` (default `"items"`) with `${$item.title}`, `${$item.summary}`, `${$item.link}`, `${$item.pubDate}`.
                - `httpJson` source maps scalars via `"stateMap": { "price": "$.bitcoin.usd" }` and arrays via `"targetCollection": "items", "itemsPath": "$.stories", "fieldMap": { "title": "title", "url": "url" }`. Optional `"headers": { "Authorization": "Bearer ${$secrets.apiKey}" }`.
                - **Verified Zero-Key Public Endpoints (Prefer Over Unverified URLs)**:
                  - CN Tech/News RSS: 少数派 `"https://sspai.com/feed"`, IT之家 `"https://www.ithome.com/rss/"`, 机核 `"https://www.gcores.com/rss"`, 阮一峰周刊 `"https://www.ruanyifeng.com/blog/atom.xml"`.
                  - CN Live JSON: 知乎日报 `"https://news-at.zhihu.com/api/4/news/latest"` (`itemsPath: "$.stories"`, `fieldMap: { "title": "title", "url": "url", "hint": "hint" }`); V2EX 热议 `"https://www.v2ex.com/api/topics/hot.json"` (`itemsPath: "$"`, `fieldMap: { "title": "title", "url": "url", "replies": "replies" }`).
                  - Global Feeds: Hacker News JSON `"https://hn.algolia.com/api/v1/search?tags=front_page"` (`itemsPath: "$.hits"`, `fieldMap: { "title": "title", "url": "url", "points": "points" }`); HN RSS `"https://hnrss.org/frontpage"`; FX Rates `"https://open.er-api.com/v6/latest/USD"` (`stateMap: { "cny": "$.rates.CNY", "eur": "$.rates.EUR", "jpy": "$.rates.JPY" }`); Crypto `"https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,ethereum&vs_currencies=usd"`.
                - Runtime auto-sets `state.sourceStatus` (`"ok"`|`"loading"`|`"error"`), `state.sourceError`, `state.lastUpdated`. Place refresh `IconButton` (`icon: "\\uE72C"`, `onClick: { "type": "source.refresh" }`) inline on a functional filter/status row (do NOT build a duplicate top widget title bar).
                """,
            GoldenSnippetJson: """
                {
                  "collections": { "items": { "maxItems": 20 } },
                  "sources": [
                    {
                      "id": "zhihu",
                      "type": "httpJson",
                      "url": "https://news-at.zhihu.com/api/4/news/latest",
                      "intervalMs": 600000,
                      "targetCollection": "items",
                      "itemsPath": "$.stories",
                      "fieldMap": { "title": "title", "url": "url", "sub": "hint" }
                    }
                  ],
                  "view": {
                    "type": "List",
                    "items": "items",
                    "itemTemplate": {
                      "type": "Button",
                      "variant": "subtle",
                      "text": "${$item.rank}. ${$item.title}",
                      "onClick": { "type": "url.open", "url": "${$item.url}" }
                    }
                  }
                }
                """),

        // ──────────────── 2. COMPOSITE & CORE UI COMPONENTS ────────────────
        new(
            Id: "visual-containers",
            Emoji: "🗂️",
            Glyph: "\uE8A9",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "分页、图片与仪表",
            TitleEn: "Tabs, Image & Gauges",
            SummaryZh: "提供多页签容器 `Tabs`、圆角图片 `Image`、环形仪表 `RingGauge` 与条形图 `BarList`。",
            SummaryEn: "Provides stateful `Tabs`, rounded `Image`, `RingGauge`, and proportional `BarList` charts.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["Tabs", "Image", "RingGauge", "BarList"],
            TriggerSourceOrActionTypes: [],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["标签页", "分页", "多面板", "切换面板", "图片", "封面", "图标", "仪表盘", "圆环图", "环形进度", "条形图", "柱状图", "占比", "排行图", "tabs", "image", "ringgauge", "gauge", "barlist", "bar chart"],
            Members:
            [
                new("Tabs", "Component", "`bind: \"state.tab\"`, 按选中页签展示对应 `children`，避免单页过高", "Segmented tab container rendering only the active tab"),
                new("Image", "Component", "`src` 支持本地文件、`${$device.mediaCover}` 或 `https://`，可设圆角与裁剪", "Renders local attachments, album art, or HTTPS images"),
                new("RingGauge / BarList", "Component", "带中心大字数值的环形仪表，以及绑定集合的横向占比排行条", "Circular KPI gauge and ranked horizontal proportion bars")
            ],
            CompactContractMarkdown: """
                - Use `Tabs` whenever a widget combines 3+ functional panels so initial height stays compact and scannable:
                  `{ "type": "Tabs", "bind": "state.activeTab", "children": [{ "title": "系统", "value": "sys", "icon": "\\uE9D9", "children": [ ... ] }, { "title": "媒体", "value": "media", "icon": "\\uE8D6", "children": [ ... ] }] }` (declare `"activeTab": "sys"` in root `"state"`).
                - `Image`: `{ "type": "Image", "src": "${$device.mediaCover}", "width": 44, "height": 44, "stretch": "uniformToFill", "cornerRadius": 8 }` (`stretch`: `"uniform"` | `"uniformToFill"` | `"fill"`).
                - `RingGauge`: `{ "type": "RingGauge", "progress": "${$sys.cpu.percent}", "max": 100, "title": "CPU", "subtitle": "核心负载", "unit": "%", "size": 76, "color": "accent" }`.
                - `BarList`: `{ "type": "BarList", "collection": "items", "titleField": "title", "valueField": "value", "subtitleField": "sub", "unit": "%", "maxItems": 6, "color": "cyan" }`.
                """,
            GoldenSnippetJson: """
                {
                  "state": { "tab": "gauge" },
                  "sources": [{ "type": "systemMetrics", "intervalMs": 2000 }],
                  "view": {
                    "type": "Tabs",
                    "bind": "state.tab",
                    "children": [
                      {
                        "title": "核心负载",
                        "value": "gauge",
                        "icon": "\uE9D9",
                        "children": [
                          {
                            "type": "Grid",
                            "columns": "*,*",
                            "spacing": 8,
                            "children": [
                              { "type": "RingGauge", "progress": "${$sys.cpu.percent}", "title": "CPU", "unit": "%", "color": "accent" },
                              { "type": "RingGauge", "progress": "${$sys.memory.percent}", "title": "RAM", "subtitle": "${$sys.memory.compactText}", "unit": "%", "color": "cyan" }
                            ]
                          }
                        ]
                      },
                      {
                        "title": "高占进程",
                        "value": "proc",
                        "icon": "\uE9F5",
                        "children": [
                          { "type": "ProcessList", "maxItems": 5, "mode": "memory" }
                        ]
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "calendar-grid",
            Emoji: "📅",
            Glyph: "\uE787",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "月历网格",
            TitleEn: "Month Calendar Grid",
            SummaryZh: "交互式月视图日历，自带月份切换、农历节日副标与日程标记点。",
            SummaryEn: "Interactive 7x6 month matrix with lunar subtitles, date selection, and event dots.",
            RequiredCapabilityIds: ["calendar"],
            TriggerNodeTypes: ["CalendarGrid"],
            TriggerSourceOrActionTypes: [],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["日历", "桌面日历", "月历", "月视图", "日程表", "排班", "calendar", "month grid", "schedule"],
            Members:
            [
                new("{ \"type\": \"CalendarGrid\", \"bind\": \"state.selectedDate\" }", "Component", "点击日期自动更新绑定的 state 字段 (yyyy-MM-dd)", "Interactive month grid updating bound state key on click"),
                new("collection / dateField", "Property", "关联数据集合，有记录的日期底部自动显示圆点", "Highlights days that have matching records in the collection")
            ],
            CompactContractMarkdown: """
                - Do NOT construct a 42-cell calendar manually from raw `Grid`/`Stack` nodes; always use `{ "type": "CalendarGrid", "bind": "state.selectedDate", "collection": "events", "dateField": "date" }`.
                - Clicking a day updates `state.selectedDate` (`"yyyy-MM-dd"`), and `CalendarGrid` includes built-in month navigation and Lunar/Festival subtitles.
                - Pair `CalendarGrid` with a filtered `List` (`"items": "events", "filter": "${state.selectedDate == '' || $item.date == state.selectedDate}"`) and an inline `Input` row below for a complete schedule widget.
                """,
            GoldenSnippetJson: """
                {
                  "state": { "selectedDate": "", "draft": "" },
                  "collections": { "events": { "maxItems": 200 } },
                  "sources": [{ "type": "calendar", "intervalMs": 60000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      { "type": "CalendarGrid", "bind": "state.selectedDate", "collection": "events", "dateField": "date" },
                      {
                        "type": "List",
                        "items": "events",
                        "filter": "${state.selectedDate == '' || $item.date == state.selectedDate}",
                        "emptyState": { "icon": "\uE787", "title": "当日暂无日程", "subtitle": "点击上方日期可切换查看" },
                        "itemTemplate": { "type": "Text", "text": "• ${$item.title} (${$item.date})" }
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "drop-zone",
            Emoji: "📥",
            Glyph: "\uE896",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "文件拖放区",
            TitleEn: "File Drop Zone",
            SummaryZh: "支持桌面文件直接拖入或点击选文件，逐个触发 `onDrop` 处理动作。",
            SummaryEn: "File input surface supporting desktop drag-and-drop and click-to-browse picker.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["DropZone"],
            TriggerSourceOrActionTypes: ["file.pick"],
            TriggerTokenPrefixes: ["$drop."],
            TriggerKeywords: ["拖拽", "拖入", "上传", "选择文件", "文件篮", "格式转换", "图片压缩", "dropzone", "drag and drop", "file picker", "converter"],
            Members:
            [
                new("{ \"type\": \"DropZone\", \"title\": \"拖入文件\", \"onDrop\": [...] }", "Component", "带拖放高亮反馈的文件面板，选定文件后执行 onDrop", "File drop & browse surface executing onDrop per file"),
                new("${$drop.name} / ${$drop.path} / ${$drop.ext} / ${$drop.sizeText} / ${$drop.text}", "Token", "文件名、完整路径、扩展名、体积与前 8KB 文本内容", "File name, full path, extension, size, and first 8KB text")
            ],
            CompactContractMarkdown: """
                - Render `{ "type": "DropZone", "icon": "\\uE896", "title": "拖入文件或点击选择", "subtitle": "支持 PNG / JPG / CSV / JSON", "onDrop": [ ...actions... ] }` for file conversion, checksum, or shelf workflows.
                - Both desktop drag-and-drop and clicking `DropZone` populate `${$drop.name}`, `${$drop.path}`, `${$drop.ext}`, `${$drop.sizeText}`, `${$drop.text}` and invoke `onDrop`.
                """,
            GoldenSnippetJson: """
                {
                  "type": "DropZone",
                  "icon": "\uE8B9",
                  "title": "拖入图片或点击选择文件",
                  "subtitle": "本地转换为目标格式，源文件保持不变",
                  "onDrop": [
                    {
                      "type": "file.transform",
                      "path": "${$drop.path}",
                      "format": "${state.targetFormat}",
                      "then": [
                        {
                          "type": "collection.prepend",
                          "collection": "history",
                          "item": { "name": "${$transform.outputName}", "path": "${$transform.outputPath}", "size": "${$transform.sizeText}" }
                        }
                      ]
                    }
                  ]
                }
                """),

        new(
            Id: "control-tile",
            Emoji: "🎚️",
            Glyph: "\uE701",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "快捷控制磁贴",
            TitleEn: "Quick Control Tile",
            SummaryZh: "Windows 11 控制中心风格开关磁贴，支持图标、状态副标与高亮激活态。",
            SummaryEn: "Windows 11 Quick Settings tile with icon, title, live subtitle, and active accent fill.",
            RequiredCapabilityIds: ["device-state", "system-control"],
            TriggerNodeTypes: ["ControlTile"],
            TriggerSourceOrActionTypes: [],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["磁贴", "控制中心", "快捷开关", "开关面板", "wifi开关", "蓝牙开关", "control tile", "quick settings", "control center"],
            Members:
            [
                new("{ \"type\": \"ControlTile\", \"title\": \"Wi-Fi\", \"value\": \"${$device.wifiEnabled}\" }", "Component", "当 value 为 true 时高亮主题色，点击执行 onClick", "Displays active accent fill when value is true; dispatches onClick")
            ],
            CompactContractMarkdown: """
                - `{ "type": "ControlTile", "icon": "\\uE701", "title": "Wi-Fi", "subtitle": "${$device.wifiStatus}", "value": "${$device.wifiEnabled}", "onClick": { "type": "system.control", "targetId": "radio.wifiToggle" } }`.
                - Place multiple `ControlTile` items inside `{ "type": "Grid", "columns": "*,*", "spacing": 8 }` — `Grid` automatically flows 4 or 6 tiles across multiple rows even when `rows`/`row` are omitted.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "deviceState", "intervalMs": 3000 }],
                  "view": {
                    "type": "Grid",
                    "columns": "*,*",
                    "spacing": 8,
                    "children": [
                      { "type": "ControlTile", "icon": "\uE701", "title": "Wi-Fi", "subtitle": "${$device.wifiStatus}", "value": "${$device.wifiEnabled}", "onClick": { "type": "system.control", "targetId": "radio.wifiToggle" } },
                      { "type": "ControlTile", "icon": "\uE702", "title": "蓝牙", "subtitle": "${$device.bluetoothStatus}", "value": "${$device.bluetoothEnabled}", "onClick": { "type": "system.control", "targetId": "radio.bluetoothToggle" } },
                      { "type": "ControlTile", "icon": "\uEA80", "title": "屏幕常亮", "subtitle": "${$device.keepAwakeStatus}", "value": "${$device.keepAwake}", "onClick": { "type": "system.control", "targetId": "power.keepAwakeToggle" } },
                      { "type": "ControlTile", "icon": "\uE74D", "title": "回收站", "subtitle": "${$device.recycleBinStatus}", "value": "${$device.recycleBinCount != '0'}", "onClick": { "type": "system.control", "targetId": "recycleBin.empty" } }
                    ]
                  }
                }
                """),

        new(
            Id: "micro-charts",
            Emoji: "📈",
            Glyph: "\uE9D2",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "趋势线与热力图",
            TitleEn: "Sparkline & Heatmap",
            SummaryZh: "提供平滑面积折线图、打卡热力矩阵与语义配色进度条。",
            SummaryEn: "Vector area sparklines, daily activity heatmaps, and progress indicators.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["Sparkline", "HeatmapGrid", "ProgressBar", "ProgressRing", "ProgressRow"],
            TriggerSourceOrActionTypes: [],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["折线图", "图表", "趋势", "曲线", "热力图", "打卡", "习惯", "连续", "进度条", "sparkline", "heatmap", "habit", "chart", "streak", "progress"],
            Members:
            [
                new("Sparkline", "Component", "绑定 `collection` 与 `valueField` 绘制平滑趋势折线图", "Smooth vector trend line & area bound to a collection"),
                new("HeatmapGrid", "Component", "按日期聚合最近 `days` 天记录并渲染打卡热力图", "Aggregates daily records into an intensity heatmap"),
                new("ProgressBar / ProgressRing / ProgressRow", "Component", "线性进度条、环形进度指示器与复合进度行", "Theme-aware linear and radial progress indicators")
            ],
            CompactContractMarkdown: """
                - `Sparkline`: `{ "type": "Sparkline", "title": "趋势", "collection": "logs", "valueField": "amount", "unit": "ml", "height": 40, "color": "accent", "showArea": true, "showLatest": true }`.
                - `HeatmapGrid`: `{ "type": "HeatmapGrid", "collection": "logs", "valueField": "score", "days": 28, "color": "success" }`.
                - `ProgressBar` / `ProgressRing`: `{ "type": "ProgressBar", "progress": "${state.current}", "max": 100, "color": "cyan" }`. Always use semantic colors (`accent`, `success`, `emerald`, `cyan`, `violet`, `warning`, `danger`).
                """,
            GoldenSnippetJson: """
                {
                  "type": "HeatmapGrid",
                  "title": "近 28 天记录热力分布",
                  "collection": "checkins",
                  "valueField": "score",
                  "days": 28,
                  "color": "success"
                }
                """),

        new(
            Id: "forms-inputs",
            Emoji: "⌨️",
            Glyph: "\uE70F",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "表单与交互控件",
            TitleEn: "Inputs & Controls",
            SummaryZh: "提供输入框、下拉选择、分段切换、复选框、开关与双向实时滑块。",
            SummaryEn: "Text inputs, selects, segmented pills, checkboxes, toggles, and two-way sliders.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["Input", "TextArea", "Select", "Segmented", "CheckBox", "Toggle", "Slider", "NumberBox", "IconButton"],
            TriggerSourceOrActionTypes: ["state.set", "state.toggle", "state.increment"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["输入", "表单", "下拉", "选择", "复选", "滑块", "计数器", "按钮", "备注", "input", "form", "select", "slider", "stepper", "counter"],
            Members:
            [
                new("Input / TextArea", "Component", "绑定 `bind: \"state.draft\"`，回车触发 `onSubmit`", "Two-way state-bound text input dispatching `onSubmit`"),
                new("Select / Segmented", "Component", "下拉框或分段标签，绑定 `bind` 与 `options`", "Dropdown or segmented pill selector"),
                new("Slider / Toggle / NumberBox", "Component", "变更时即时派发 `onChange`（通过 `${$value}` 读取新值）", "Dispatches `onChange` immediately with `${$value}`")
            ],
            CompactContractMarkdown: """
                - Declare every bound `state.<key>` in root `"state"`.
                - `Input`: `{ "type": "Input", "bind": "state.draft", "placeholder": "输入内容…", "onSubmit": { ... } }`. Pair in `Grid` (`columns: "*,Auto"`) with `IconButton` (`icon: "\\uE710"`, `variant: "accent"`).
                - `Select` / `Segmented`: `{ "type": "Segmented", "bind": "state.tab", "options": [{ "value": "all", "label": "全部" }, { "value": "open", "label": "未完成" }] }`.
                - `Slider` (two modes):
                  1. Persisted state: `{ "type": "Slider", "title": "目标阈值", "bind": "state.goal", "min": 0, "max": 100, "step": 5, "unit": "%" }`.
                  2. Live two-way hardware control (NO extra submit button): `{ "type": "Slider", "title": "主音量", "value": "${$device.volume}", "min": 0, "max": 100, "step": 2, "unit": "%", "onChange": { "type": "system.control", "targetId": "volume.set", "value": "${$value}" } }`.
                """,
            GoldenSnippetJson: """
                {
                  "type": "Grid",
                  "columns": "*,Auto",
                  "spacing": 6,
                  "children": [
                    { "type": "Input", "column": 0, "bind": "state.draft", "placeholder": "输入新条目…" },
                    { "type": "IconButton", "column": 1, "icon": "\uE710", "variant": "accent", "onClick": { "type": "collection.prepend", "guard": "${state.draft != ''}", "collection": "items", "item": { "title": "${state.draft}" }, "then": [{ "type": "state.set", "key": "draft", "value": "" }] } }
                  ]
                }
                """),

        new(
            Id: "collections-lists",
            Emoji: "📋",
            Glyph: "\uE8FD",
            Category: CustomWidgetCapabilityCategory.Component,
            TitleZh: "数据列表",
            TitleEn: "Data Lists",
            SummaryZh: "本地持久化记录列表，支持条件筛选、行悬停按钮与导出 Markdown。",
            SummaryEn: "Persisted record collections with filtering, row hover actions, and Markdown export.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: ["List", "FileChip", "EmptyState"],
            TriggerSourceOrActionTypes: ["collection.prepend", "collection.append", "collection.update", "collection.remove", "collection.clear", "collection.exportMarkdown"],
            TriggerTokenPrefixes: ["collections.", "$item."],
            TriggerKeywords: ["持久化", "集合", "列表", "清单", "待办", "任务", "筛选", "分类", "记录", "书签", "速记", "导出", "list", "todo", "checklist", "filter", "collection", "bookmark"],
            Members:
            [
                new("collections: { \"items\": { \"maxItems\": 200 } }", "Schema", "在根节点声明持久化数据集合与上限", "Declares persistent collection schema in manifest root"),
                new("${$item.id} / ${$item.rank} / ${$item.timeText} / ${$item.dateText}", "Token", "列表项内置唯一 ID、序号与创建时间", "Built-in item ID, 1-based rank, and timestamp tokens"),
                new("collection.prepend / collection.update / collection.remove / collection.clear", "Action", "新增、更新、删除单条记录或清空集合", "Collection add, update, remove, and clear actions")
            ],
            CompactContractMarkdown: """
                - Always declare root `"collections": { "items": { "maxItems": 200, "sort": "newestFirst" } }`.
                - `List`: `{ "type": "List", "items": "items", "filter": "${state.tab == 'all' || $item.done != 'true'}", "emptyState": { "icon": "\\uE73E", "title": "暂无记录" }, "itemTemplate": { ... }, "hoverActions": [{ "type": "IconButton", "icon": "\\uE74D", "size": 24, "variant": "danger", "onClick": { "type": "collection.remove", "collection": "items", "targetId": "${$item.id}" } }] }`.
                - Item tokens: `${$item.id}`, `${$item.rank}`, `${$item.index}`, `${$item.timeText}`, `${$item.dateText}`, `${$item.isToday}`. Summary tokens: `${collections.items.count}`, `${collections.items.todayCount}`, `${collections.items.latest.<field>}`.
                """,
            GoldenSnippetJson: """
                {
                  "type": "List",
                  "items": "items",
                  "itemTemplate": {
                    "type": "CheckBox",
                    "value": "${$item.done}",
                    "text": "${$item.title}",
                    "strikethrough": true,
                    "onToggle": { "type": "collection.update", "collection": "items", "targetId": "${$item.id}", "item": { "done": "${$item.done == 'true' ? 'false' : 'true'}" } }
                  }
                }
                """),

        // ──────────────── 3. ACTIONS & SANDBOXED LOCAL TOOLS ────────────────
        new(
            Id: "display-and-audio",
            Emoji: "🔆",
            Glyph: "\uE706",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "音量、亮度与媒体",
            TitleEn: "Volume, Brightness & Media",
            SummaryZh: "双向滑块实时调节系统音量与屏幕亮度（支持笔记本与外接显示器），并控制媒体播放与输出设备。",
            SummaryEn: "Two-way live sliders for master volume and display brightness, plus media transport and audio output switching.",
            RequiredCapabilityIds: ["device-state"],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["system.control"],
            TriggerTokenPrefixes: ["$device.brightness", "$device.media", "$device.audioDevice"],
            TriggerKeywords: ["亮度", "屏幕亮度", "调亮度", "音量滑块", "输出设备", "耳机切换", "扬声器", "音乐控制", "切歌", "专辑封面", "brightness", "audio output", "speaker", "media player"],
            Members:
            [
                new("brightness.set / brightness.step", "Command", "配合 `${$value}` 实时调节屏幕亮度百分比 (0-100)", "Sets display brightness (0-100) via LCD/DDC-CI using `${$value}`"),
                new("volume.set / volume.muteToggle / audio.output", "Command", "实时调节主音量 (0-100)、切换静音或打开输出设备面板", "Adjusts master volume, toggles mute, or opens audio output selector"),
                new("media.playPause / media.previous / media.next", "Command", "控制媒体播放；播放/暂停图标绑定 `${$device.mediaToggleIcon}`", "Controls active SMTC media session; bind icon to `${$device.mediaToggleIcon}`")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "deviceState", "intervalMs": 3000 }]`.
                - **Two-Way Live Sliders (Zero Dummy State & Zero Redundant Buttons)**:
                  - Volume: `{ "type": "Slider", "title": "音量", "value": "${$device.volume}", "min": 0, "max": 100, "step": 2, "unit": "%", "onChange": { "type": "system.control", "targetId": "volume.set", "value": "${$value}" } }`.
                  - Brightness: `{ "type": "Slider", "title": "亮度", "value": "${$device.brightness}", "min": 0, "max": 100, "step": 5, "unit": "%", "onChange": { "type": "system.control", "targetId": "brightness.set", "value": "${$value}" } }`.
                - Audio device label & quick switcher: display `${$device.audioDeviceName}` and invoke `{ "type": "system.control", "targetId": "audio.output" }`.
                - Media transport icons (always use valid Segoe Fluent glyphs, NEVER BMP symbols inside `FontIcon`): Previous `\uE892`, Play/Pause `${$device.mediaToggleIcon}`, Next `\uE893`, Album cover `${$device.mediaCover}`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "deviceState", "intervalMs": 3000 }],
                  "view": {
                    "type": "Card",
                    "tone": "subtle",
                    "children": [
                      { "type": "Slider", "title": "系统音量", "value": "${$device.volume}", "min": 0, "max": 100, "step": 2, "unit": "%", "onChange": { "type": "system.control", "targetId": "volume.set", "value": "${$value}" } },
                      { "type": "Slider", "title": "屏幕亮度", "value": "${$device.brightness}", "min": 0, "max": 100, "step": 5, "unit": "%", "onChange": { "type": "system.control", "targetId": "brightness.set", "value": "${$value}" } },
                      {
                        "type": "KeyValueRow",
                        "label": "音频输出",
                        "value": "${$device.audioDeviceName}"
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "clipboard-workspace",
            Emoji: "📋",
            Glyph: "\uE77F",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "剪贴板与文本转换",
            TitleEn: "Clipboard & Text Transform",
            SummaryZh: "识别剪贴板内容类型（JSON、链接、时间戳等），并提供 15 种本地纯内存编码、格式化与哈希算子。",
            SummaryEn: "Inspects live clipboard text and runs 15 local in-memory formatting, encoding, timestamp, and hash operations.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["clipboard", "text.transform", "clipboard.copy"],
            TriggerTokenPrefixes: ["$clipboard."],
            TriggerKeywords: ["剪贴板", "文本转换", "json美化", "json压缩", "url编码", "url解码", "时间戳转换", "uuid生成", "字数统计", "大小写转换", "编码解码", "开发者工具", "clipboard", "text transform", "urlencode", "base64", "uuid", "timestamp", "word count"],
            Members:
            [
                new("sources: [{ \"type\": \"clipboard\", \"intervalMs\": 2000 }]", "Source", "订阅剪贴板纯文本快照并自动识别内容特征", "Subscribes to system clipboard plain-text metadata"),
                new("${$clipboard.preview} / ${$clipboard.kindLabel} / ${$clipboard.length} / ${$clipboard.lines}", "Token", "单行预览、类型标签（JSON / 链接 / 时间戳）、字符数与行数", "Single-line preview, content type label, char length, and line count"),
                new("{ \"type\": \"text.transform\", \"format\": \"jsonPretty\", \"key\": \"output\", \"mode\": \"copy\" }", "Action", "转换文本并写入 `state.<key>`；`mode: \"copy\"` 可同步复制结果", "Transforms text in-memory into `state.<key>` with optional clipboard copy"),
                new("jsonPretty / jsonMinify / urlEncode / base64Encode / timestampToDate / uuid / sha256", "Enum", "内置 15 种免联网文本转换算子（含对应解码、大小写、字数统计与 MD5）", "15 built-in deterministic text transform formats")
            ],
            CompactContractMarkdown: """
                - Optional `"sources": [{ "type": "clipboard", "intervalMs": 2000 }]` exposes `${$clipboard.text}`, `${$clipboard.preview}`, `${$clipboard.length}`, `${$clipboard.lines}`, `${$clipboard.kind}` (`"json"`|`"url"`|`"timestamp"`|`"hexColor"`|`"number"`|`"text"`|`"empty"`), `${$clipboard.kindLabel}`, `${$clipboard.hasText}`.
                - Action `text.transform`:
                  `{ "type": "text.transform", "format": "jsonPretty", "text": "${state.rawText}", "key": "resultText", "mode": "copy" }`
                  - If `"text"` is omitted and `clipboard` source is active, transforms current clipboard text directly.
                  - Writes output to `state.<key>` (plus `state.transformOutput` and `${$transform.resultText}`); `"mode": "copy"` also copies the result to the system clipboard.
                  - Supported `format` operations: `"jsonPretty"`, `"jsonMinify"`, `"urlEncode"`, `"urlDecode"`, `"base64Encode"`, `"base64Decode"`, `"timestampToDate"`, `"dateToTimestamp"`, `"uuid"`, `"upper"`, `"lower"`, `"trim"`, `"wordCount"`, `"sha256"`, `"md5"`.
                """,
            GoldenSnippetJson: """
                {
                  "state": { "input": "", "output": "" },
                  "sources": [{ "type": "clipboard", "intervalMs": 2000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      {
                        "type": "Card",
                        "tone": "subtle",
                        "children": [
                          { "type": "KeyValueRow", "label": "当前剪贴板 (${$clipboard.kindLabel})", "value": "${$clipboard.length} 字符" },
                          { "type": "Text", "text": "${$clipboard.preview}", "style": "caption", "color": "secondary", "maxLines": 2 }
                        ]
                      },
                      { "type": "TextArea", "bind": "state.input", "placeholder": "输入待处理文本（留空则直接处理剪贴板内容）…" },
                      {
                        "type": "Grid",
                        "columns": "*,*,*",
                        "spacing": 6,
                        "children": [
                          { "type": "Button", "text": "JSON 格式化", "onClick": { "type": "text.transform", "format": "jsonPretty", "text": "${state.input != '' ? state.input : $clipboard.text}", "key": "output", "mode": "copy" } },
                          { "type": "Button", "text": "Base64 编码", "onClick": { "type": "text.transform", "format": "base64Encode", "text": "${state.input != '' ? state.input : $clipboard.text}", "key": "output", "mode": "copy" } },
                          { "type": "Button", "text": "时间戳转换", "onClick": { "type": "text.transform", "format": "timestampToDate", "text": "${state.input != '' ? state.input : $clipboard.text}", "key": "output", "mode": "copy" } }
                        ]
                      },
                      { "type": "Text", "text": "${state.output}", "style": "mono", "maxLines": 6, "visible": "${state.output != ''}" }
                    ]
                  }
                }
                """),

        new(
            Id: "quick-launcher",
            Emoji: "🚀",
            Glyph: "\uE756",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "快捷启动器",
            TitleEn: "Quick Launcher",
            SummaryZh: "一键打开常用系统文件夹（下载、桌面、文档）、开发笔记工具协议（VS Code、Obsidian、截图）及本地路径。",
            SummaryEn: "Opens standard user folders, whitelisted app URI protocols, and local files/folders safely.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["launcher.open"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["启动器", "快捷启动", "工作台", "打开文件夹", "下载目录", "桌面目录", "打开vscode", "obsidian", "截图", "计算器", "导航面板", "launcher", "quick launch", "open folder", "shortcut"],
            Members:
            [
                new("downloads / desktop / documents / pictures / userprofile", "Folder", "通过 `targetId` 打开常用系统用户目录", "Opens standard user folders via `targetId`"),
                new("vscode:// / cursor:// / obsidian:// / ms-screenclip: / ms-calculator:", "URI", "调起受信任桌面应用、屏幕截图或系统小工具协议", "Invokes whitelisted app and Windows utility URI schemes"),
                new("{ \"type\": \"launcher.open\", \"path\": \"D:/Projects\" }", "Action", "打开本地文件夹或文件（社区导入包首次运行带授权确认）", "Opens an existing local folder or file with consent protection")
            ],
            CompactContractMarkdown: """
                - Use `{ "type": "launcher.open", "targetId": "<target>" }` (or `"path"` / `"url"`) for quick-launch docks and workspace hubs:
                  - Known folders: `"downloads"`, `"desktop"`, `"documents"`, `"pictures"`, `"music"`, `"videos"`, `"userprofile"`.
                  - Built-in Windows utilities: `"ms-screenclip:"` (screen snip), `"ms-calculator:"`, `"ms-clock:"`, `"ms-paint:"`, `"ms-todo:"`.
                  - Developer & knowledge URIs: `"vscode://file/..."`, `"cursor://..."`, `"obsidian://open"`, `"notion://..."`, `"https://..."`.
                  - Local paths: existing file or folder path (never executes arbitrary shell/cmd scripts).
                """,
            GoldenSnippetJson: """
                {
                  "view": {
                    "type": "Grid",
                    "columns": "*,*",
                    "spacing": 8,
                    "children": [
                      { "type": "Button", "text": "下载目录", "icon": "\uE896", "onClick": { "type": "launcher.open", "targetId": "downloads" } },
                      { "type": "Button", "text": "文档目录", "icon": "\uE8A5", "onClick": { "type": "launcher.open", "targetId": "documents" } },
                      { "type": "Button", "text": "屏幕截图", "icon": "\uE70F", "onClick": { "type": "launcher.open", "targetId": "ms-screenclip:" } },
                      { "type": "Button", "text": "计算器", "icon": "\uE8EF", "onClick": { "type": "launcher.open", "targetId": "ms-calculator:" } }
                    ]
                  }
                }
                """),

        new(
            Id: "file-transform",
            Emoji: "🔄",
            Glyph: "\uE8AB",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "文件格式转换",
            TitleEn: "File Converter",
            SummaryZh: "本机离线转换图片格式与尺寸（PNG/JPG/WebP/ICO）、互转 CSV 与 JSON、计算文件哈希校验值。",
            SummaryEn: "Offline local image converter (PNG/JPG/WebP/ICO + resize) and CSV/JSON/Markdown/SHA256 file processor.",
            RequiredCapabilityIds: ["drop-zone"],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["file.transform", "file.pick"],
            TriggerTokenPrefixes: ["$transform."],
            TriggerKeywords: ["转换", "格式转换", "转webp", "转png", "转jpg", "转ico", "图标生成", "图片压缩", "缩放", "csv转json", "json转csv", "json格式化", "哈希", "校验", "sha256", "md5", "base64", "convert", "image format", "resize", "hash", "checksum"],
            Members:
            [
                new("{ \"type\": \"file.transform\", \"path\": \"${$drop.path}\", \"format\": \"webp\" }", "Action", "转换文件生成新副本，原文件保持不变，可在 `then` 中接续处理", "Non-destructively converts input file and populates `$transform.*`"),
                new("png / jpg / webp / ico / csvToJson / jsonToCsv / mdToHtml / sha256", "Enum", "支持主流图片格式缩放互转、结构化数据转换与哈希校验", "Supported image, structured data, and checksum formats"),
                new("${$transform.outputPath} / ${$transform.outputName} / ${$transform.sizeText} / ${$transform.resultText}", "Token", "输出路径、新文件名、文件大小与转换结果或哈希值", "Output path, file name, formatted size, and result/hash text")
            ],
            CompactContractMarkdown: """
                - Pair `DropZone` with `{ "type": "file.transform", "path": "${$drop.path}", "format": "${state.format}", "value": "${state.maxWidth}", "then": [ ... ] }`.
                - Supported `format` values ONLY:
                  - Images (WinRT native): `"png"`, `"jpg"`, `"webp"`, `"bmp"`, `"tiff"`, `"ico"` (optional `"value"` max width px e.g. `"1024"`).
                  - Data/Checksum: `"csvToJson"`, `"jsonToCsv"`, `"jsonPretty"`, `"jsonMinify"`, `"mdToHtml"`, `"mdToTxt"`, `"base64Encode"`, `"base64Decode"`, `"sha256"`, `"md5"`.
                  - Video/Audio/Office/PDF transcoding is NOT supported in the sandbox.
                - Inside `"then"`, read `${$transform.outputName}`, `${$transform.outputPath}`, `${$transform.sizeText}`, `${$transform.resultText}`.
                """,
            GoldenSnippetJson: """
                {
                  "state": { "format": "webp" },
                  "collections": { "outputs": { "maxItems": 30 } },
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      {
                        "type": "Segmented",
                        "bind": "state.format",
                        "options": [
                          { "value": "png", "label": "PNG" },
                          { "value": "jpg", "label": "JPG" },
                          { "value": "webp", "label": "WebP" },
                          { "value": "ico", "label": "ICO" }
                        ]
                      },
                      {
                        "type": "DropZone",
                        "icon": "\uE8B9",
                        "title": "拖入图片或点击选择",
                        "subtitle": "自动转为 ${state.format} 并保存在原目录",
                        "onDrop": [
                          {
                            "type": "file.transform",
                            "path": "${$drop.path}",
                            "format": "${state.format}",
                            "then": [
                              { "type": "collection.prepend", "collection": "outputs", "item": { "name": "${$transform.outputName}", "path": "${$transform.outputPath}" } }
                            ]
                          }
                        ]
                      },
                      {
                        "type": "List",
                        "items": "outputs",
                        "itemTemplate": { "type": "FileChip", "fileName": "${$item.name}", "filePath": "${$item.path}" }
                      }
                    ]
                  }
                }
                """),

        new(
            Id: "system-control",
            Emoji: "⚙️",
            Glyph: "\uE713",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "系统开关与设置",
            TitleEn: "System Toggles & Settings",
            SummaryZh: "切换 Wi-Fi、蓝牙、屏幕常亮防休眠，执行锁屏、清空回收站或直达 Windows 系统设置页。",
            SummaryEn: "Toggles Wi-Fi, Bluetooth, Keep-Awake, locks workstation, empties Recycle Bin, or opens Windows Settings pages.",
            RequiredCapabilityIds: ["device-state"],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["system.control", "system.openSetting"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["开关wifi", "开关蓝牙", "调音量", "静音", "调亮度", "锁屏", "清空回收站", "防休眠", "屏幕常亮", "切歌", "播放暂停", "系统设置", "任务管理器", "control center", "toggle wifi", "toggle bluetooth", "lock screen", "empty recycle bin", "keep awake"],
            Members:
            [
                new("radio.wifiToggle / radio.bluetoothToggle", "Command", "切换 Wi-Fi 或蓝牙开关；受限设备自动跳转对应系统设置页", "Toggles Wi-Fi or Bluetooth radio with ms-settings fallback"),
                new("power.keepAwakeToggle / power.lock / recycleBin.empty", "Command", "切换屏幕常亮、锁定电脑或清空系统回收站", "Toggles Keep-Awake, locks PC, or empties Recycle Bin"),
                new("{ \"type\": \"system.openSetting\", \"targetId\": \"display\" }", "Action", "跳转系统设置：`wifi`, `bluetooth`, `sound`, `display`, `nightlight`, `power`, `storage`, `windowsupdate`, `taskmgr`", "Opens whitelisted Windows Settings page or Task Manager")
            ],
            CompactContractMarkdown: """
                - Declare `"sources": [{ "type": "deviceState", "intervalMs": 3000 }]` so UI reflects live hardware state.
                - `system.control` (`"targetId"` commands):
                  - Audio & Display: `"volume.set"` (`"value": "${$value}"`), `"volume.step"` (`"value": "10"` or `"-10"`), `"volume.muteToggle"`, `"audio.output"`, `"brightness.set"` (`"value": "${$value}"`), `"brightness.step"`.
                  - Radios: `"radio.wifiToggle"`, `"radio.bluetoothToggle"` (auto-falls back to `ms-settings:` if restricted).
                  - Media: `"media.playPause"`, `"media.next"`, `"media.previous"`.
                  - Power & Desktop: `"power.keepAwakeToggle"`, `"power.lock"`, `"recycleBin.empty"`.
                - `system.openSetting` (`"targetId"`): `"wifi"`, `"network"`, `"bluetooth"`, `"hotspot"`, `"sound"`, `"volume"`, `"display"`, `"nightlight"`, `"power"`, `"battery"`, `"storage"`, `"clipboard"`, `"notifications"`, `"personalization"`, `"colors"`, `"apps"`, `"windowsupdate"`, `"taskmgr"`.
                """,
            GoldenSnippetJson: """
                {
                  "sources": [{ "type": "deviceState", "intervalMs": 3000 }],
                  "view": {
                    "type": "Stack",
                    "spacing": 8,
                    "children": [
                      {
                        "type": "Grid",
                        "columns": "*,*",
                        "spacing": 8,
                        "children": [
                          { "type": "ControlTile", "icon": "\uE701", "title": "Wi-Fi", "subtitle": "${$device.wifiStatus}", "value": "${$device.wifiEnabled}", "onClick": { "type": "system.control", "targetId": "radio.wifiToggle" } },
                          { "type": "ControlTile", "icon": "\uEA80", "title": "屏幕常亮", "subtitle": "${$device.keepAwakeStatus}", "value": "${$device.keepAwake}", "onClick": { "type": "system.control", "targetId": "power.keepAwakeToggle" } }
                        ]
                      },
                      { "type": "Slider", "title": "系统音量", "value": "${$device.volume}", "min": 0, "max": 100, "step": 2, "unit": "%", "onChange": { "type": "system.control", "targetId": "volume.set", "value": "${$value}" } }
                    ]
                  }
                }
                """),

        new(
            Id: "ai-run",
            Emoji: "✨",
            Glyph: "\uE945",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "AI 文本处理",
            TitleEn: "In-Widget AI Task",
            SummaryZh: "在格子内调用 AI 完成摘要提炼、待办拆解、翻译或润色，并将结果自动写入状态或列表。",
            SummaryEn: "Runs structured AI prompts inside the widget and maps JSON fields into state or collections.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["ai.run"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["智能提炼", "总结", "摘要", "拆解", "翻译", "润色", "分类", "会议纪要", "周报", "ai处理", "summarize", "translate", "extract", "polish", "ai.run"],
            Members:
            [
                new("{ \"type\": \"ai.run\", \"prompt\": \"...\", \"collection\": \"items\", \"fieldMap\": {...} }", "Action", "执行单并发 AI 任务，自动更新 `state.aiStatus` 并在失败时保留输入内容", "Runs single-flight structured AI task with status tracking and input preservation"),
                new("${state.aiStatus} / ${state.aiError} / ${state.aiUpdatedAt}", "Token", "运行状态 (`idle` | `running` | `error`)、错误提示与最近完成时间", "Execution status, error message, and last completion time")
            ],
            CompactContractMarkdown: """
                - `{ "type": "ai.run", "guard": "${state.draft != ''}", "prompt": "拆解以下待办事项：\n${state.draft}", "stateMap": { "summary": "summary" }, "collection": "items", "mode": "prepend", "itemsPath": "items", "fieldMap": { "title": "title" }, "then": [{ "type": "state.set", "key": "draft", "value": "" }] }`.
                - Auto-manages `state.aiStatus` (`"idle"`|`"running"`|`"error"`), `state.aiError`, `state.aiUpdatedAt`. Clear `draft` inside `"then"` so failed requests preserve user input.
                """,
            GoldenSnippetJson: """
                {
                  "type": "Button",
                  "text": "拆解待办事项",
                  "icon": "\uE945",
                  "variant": "accent",
                  "onClick": {
                    "type": "ai.run",
                    "guard": "${state.draft != ''}",
                    "prompt": "将以下内容拆解为可执行待办事项(items: title)：\n${state.draft}",
                    "collection": "tasks",
                    "itemsPath": "items",
                    "fieldMap": { "title": "title" },
                    "then": [{ "type": "state.set", "key": "draft", "value": "" }]
                  }
                }
                """),

        new(
            Id: "notifications-reminders",
            Emoji: "🔔",
            Glyph: "\uEA8F",
            Category: CustomWidgetCapabilityCategory.Action,
            TitleZh: "通知与轻提示",
            TitleEn: "Notifications & Toasts",
            SummaryZh: "发送 Windows 11 系统右下角弹窗提醒，或在当前格子内展示轻量操作反馈提示。",
            SummaryEn: "Sends Windows 11 Action Center toast notifications or subtle in-widget feedback banners.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["reminder.notify", "toast.show"],
            TriggerTokenPrefixes: [],
            TriggerKeywords: ["提醒", "弹窗", "系统通知", "喝水提醒", "久坐提醒", "闹钟", "notification", "reminder", "toast", "alert"],
            Members:
            [
                new("{ \"type\": \"reminder.notify\", \"text\": \"标题\", \"message\": \"内容\" }", "Action", "发送原生 Windows 系统通知弹窗", "Sends a native Windows 11 Action Center notification"),
                new("{ \"type\": \"toast.show\", \"message\": \"已保存\" }", "Action", "在当前格子内显示非打扰轻提示条", "Shows a lightweight in-widget feedback banner")
            ],
            CompactContractMarkdown: """
                - Use `{ "type": "reminder.notify", "text": "标题", "message": "提醒内容" }` to send a Windows 11 system notification.
                - Use `{ "type": "toast.show", "message": "已保存" }` for subtle in-widget confirmation banners.
                """,
            GoldenSnippetJson: """
                {
                  "type": "Button",
                  "text": "发送测试提醒",
                  "icon": "\uEA8F",
                  "onClick": { "type": "reminder.notify", "text": "定时提醒", "message": "预设提醒时间已到。" }
                }
                """),

        // ──────────────── 4. CONFIG & SECURITY ────────────────
        new(
            Id: "secret-vault",
            Emoji: "🔐",
            Glyph: "\uE72E",
            Category: CustomWidgetCapabilityCategory.ConfigAndSecurity,
            TitleZh: "API 密钥凭据",
            TitleEn: "Encrypted API Secrets",
            SummaryZh: "使用系统级加密安全保存个人 API Key 或 Token，导出分享 `.dbbox` 时自动剥离不外泄。",
            SummaryEn: "Stores API keys encrypted via Windows DPAPI; automatically excluded when exporting `.dbbox` packages.",
            RequiredCapabilityIds: ["http-and-rss-feeds"],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: ["secret"],
            TriggerTokenPrefixes: ["$secrets."],
            TriggerKeywords: ["密钥", "token", "apikey", "api key", "bearer", "authorization", "鉴权", "认证", "私密", "硅基流动", "siliconflow", "github", "notion", "homeassistant", "聚合网关", "secret"],
            Members:
            [
                new("settings: [{ \"key\": \"apiKey\", \"type\": \"secret\", \"label\": \"API Key\" }]", "Setting", "声明加密凭据项；未填写时自动展示配置引导", "Declares a DPAPI-encrypted secret setting with setup prompt"),
                new("headers: { \"Authorization\": \"Bearer ${$secrets.apiKey}\" }", "Header", "在同源 `httpJson` 请求头中安全注入密钥", "Injects secret into same-origin `httpJson` request headers")
            ],
            CompactContractMarkdown: """
                - NEVER hardcode API keys or tokens in `manifest.json`; declare `"settings": [{ "key": "apiKey", "type": "secret", "label": "API Key" }]`.
                - Reference in `httpJson` via `"headers": { "Authorization": "Bearer ${$secrets.apiKey}" }`.
                - Stored encrypted via Windows DPAPI outside `manifest.json`/`store.json` (never included in `.dbbox` exports).
                """,
            GoldenSnippetJson: """
                {
                  "settings": [
                    { "key": "apiKey", "type": "secret", "label": "SiliconFlow API Key" }
                  ],
                  "sources": [
                    {
                      "id": "balance",
                      "type": "httpJson",
                      "url": "https://api.siliconflow.cn/v1/user/info",
                      "headers": { "Authorization": "Bearer ${$secrets.apiKey}" },
                      "intervalMs": 300000,
                      "stateMap": { "balance": "$.data.totalBalance", "name": "$.data.name" }
                    }
                  ],
                  "view": {
                    "type": "Card",
                    "tone": "subtle",
                    "children": [
                      { "type": "Text", "text": "账户可用余额：¥ ${state.balance ?? '--'}", "style": "title" }
                    ]
                  }
                }
                """),

        new(
            Id: "widget-settings",
            Emoji: "🛠️",
            Glyph: "\uE713",
            Category: CustomWidgetCapabilityCategory.ConfigAndSecurity,
            TitleZh: "右键设置项",
            TitleEn: "Widget Settings",
            SummaryZh: "为格子添加右键「组件设置」面板，让用户无需改代码即可修改城市、刷新频率或显示偏好。",
            SummaryEn: "Adds user-configurable choice, toggle, text, and numeric options to the widget context menu.",
            RequiredCapabilityIds: [],
            TriggerNodeTypes: [],
            TriggerSourceOrActionTypes: [],
            TriggerTokenPrefixes: ["$settings."],
            TriggerKeywords: ["设置项", "配置项", "右键设置", "自定义城市", "切换源", "settings", "preferences", "configurable"],
            Members:
            [
                new("{ \"key\": \"city\", \"type\": \"text\", \"label\": \"城市\", \"default\": \"北京\" }", "Setting", "支持 `choice`、`toggle`、`text`、`number`、`secret` 五种设置类型", "Supports choice, toggle, text, number, and secret setting types"),
                new("${$settings.city}", "Token", "在视图或数据源中动态读取用户当前设置值", "Reads the user's configured setting value in views or sources")
            ],
            CompactContractMarkdown: """
                - Declare in root `"settings": [{ "key": "city", "type": "text", "label": "城市", "default": "上海" }]` (types: `"choice"`, `"toggle"`, `"text"`, `"number"`, `"secret"`).
                - Read anywhere in `view` or `sources` via `${$settings.city}`.
                """,
            GoldenSnippetJson: """
                {
                  "settings": [
                    {
                      "key": "unit",
                      "type": "choice",
                      "label": "显示密度",
                      "default": "compact",
                      "options": [
                        { "value": "compact", "label": "紧凑视图" },
                        { "value": "detail", "label": "详细视图" }
                      ]
                    }
                  ]
                }
                """)
    ];
}
