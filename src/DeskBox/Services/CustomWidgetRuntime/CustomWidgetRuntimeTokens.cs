using System.Collections.Frozen;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Lightweight runtime built-in namespace token validator decoupled from the heavy
/// <see cref="CustomWidgetCapabilityRegistry"/> authoring JSON so normal desktop widget loading
/// and manifest validation never materialize the full Studio capability registry.
/// </summary>
public static partial class CustomWidgetRuntimeTokens
{
    [GeneratedRegex(
        @"(\$sys|\$weather|\$calendar|\$device|\$music|\$todo|\$pomodoro|\$pomo|\$countdown|\$ai|\$time|\$clock|\$drives|\$clipboard|\$drop|\$transform|\$slot)\.([A-Za-z0-9_.]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex BuiltInNamespaceTokenRegex();

    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ValidNamespaceTokens =
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
                "uptime.text", "status.label", "status.value", "topProcesses", "searchShortcut"
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
                "recycleBinCount", "recycleBinStatus", "searchShortcut"
            ],
            ["$pomodoro"] =
            [
                "remainingText", "timeText", "phaseLabel", "phase", "phasePill", "headerPill",
                "dialSubtext", "phaseHint", "glyph", "primaryActionText",
                "isRunning", "isPaused", "totalSeconds", "remainingSeconds", "progressPercent",
                "cycle", "cycleBadge", "todayCompleted", "completedToday", "todayMinutes", "todayDurationText",
                "dailyGoal", "goalPercent", "goalSummary", "streakDays", "streakBadge", "todayDate",
                "capsuleTitle", "capsuleSummary", "capsuleBadge",
                "mode", "rhythmMode", "isAway", "isAwaySuspended", "isDue", "satMinutes",
                "intervalMin", "sedentaryIntervalMin", "stoodCount", "longestSitMin",
                "microAction", "healthSummary", "confirmActionText", "snoozeActionText",
                "tipX", "tipY", "sedentaryIsRunning", "sedentaryIsDue", "sedentaryIsAway",
                "sedentaryProgressPercent", "sedentarySatMinutes", "pomoIsRunning", "pomoTimeText"
            ],
            ["$countdown"] =
            [
                "hasHero", "hero.id", "hero.emoji", "hero.title", "hero.days", "hero.statusWord",
                "hero.subtitle", "hero.badge", "hero.deltaText", "hero.progress", "hero.tone", "hero.isToday", "hero.milestone",
                "hero.rawDate", "hero.kind", "hero.category", "hero.recurrence", "hero.repeat", "hero.calendar",
                "hero.weekendRule", "hero.isPinned", "hero.isLunar", "hero.isLunarLeap",
                "hero.lunarYear", "hero.lunarMonth", "hero.lunarDay",
                "year.percent", "yearPercent", "year.remainingDays", "year.title", "year.summary",
                "month.percent", "monthPercent", "month.remainingDays", "month.title", "month.summary", "count",
                "week.percent", "week.dayIndex", "week.title", "week.summary",
                "work.percent", "work.phase", "work.summary", "work.range", "work.title",
                "hasHoliday", "holiday.title", "holiday.days", "holiday.subtitle", "holiday.inProgress",
                "items", "secondaryItems", "progressBars",
                "capsuleTitle", "capsuleSummary", "capsuleGlyph", "capsuleBadge", "capsuleIsWarning", "capsuleProgress"
            ],
            ["$ai"] =
            [
                "todayTokens", "todayCost", "monthTokens", "monthCost", "cacheHitPercent",
                "activeCount", "errorCount", "attentionCount", "statusTone",
                "hasActiveProviders", "hasLocalCliData", "hasTodayActivity",
                "activeDays28", "daily28Summary",
                "walletCurrency", "walletTargetBalance", "walletTargetSummary",
                "focus.id", "focus.name", "focus.shortName", "focus.monogram", "focus.color",
                "focus.value", "focus.subtext", "focus.badge", "focus.progress", "focus.tone",
                "focus.isWindowQuota", "focus.hasSecondary", "focus.secondaryLabel", "focus.secondaryValue",
                "focus.secondarySubtext", "focus.secondaryProgress", "focus.error",
                "providers", "dailyTrend", "daily28",
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
            ["$clipboard"] = ["text", "preview", "length", "lines", "kind", "kindLabel", "hasText", "hasImage"],
            ["$drop"] = ["name", "path", "ext", "sizeBytes", "sizeText", "modifiedIso", "mode", "text", "count"],
            ["$transform"] = ["status", "op", "sourcePath", "outputPath", "outputName", "sizeText", "resultText", "error", "jwtState"],
            ["$slot"] = ["paired"]
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FrozenSet<string>> ValidNamespaceTokenSets =
        ValidNamespaceTokens.ToFrozenDictionary(
            static kv => kv.Key,
            static kv => kv.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> EphemeralStateKeys =
        FrozenSet.ToFrozenSet(
        [
            "aiStatus", "aiError", "aiUpdatedAt",
            "networkStatus", "networkError",
            "sourceStatus", "sourceError", "sourceUpdatedAt", "lastUpdated",
            "feedTitle", "feedCount", "topHeadline",
            "transformStatus", "transformOutput", "transformError",
            "httpStatus", "httpOk", "httpError", "httpStatusCode", "httpUpdatedAt",
            "fileText", "fileLineCount", "fileContent", "fileStatus", "fileError", "fileWriteStatus", "fileWriteError",
            "commandStatus", "commandError", "commandExitCode", "commandStdout", "commandStderr",
            "folderItemCount"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsEphemeralStateKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        (EphemeralStateKeys.Contains(key) ||
         key.StartsWith("sourceStatus.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("fileText.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("fileLineCount.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("commandStdout.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("commandStderr.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("commandExitCode.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("folderItemCount.", StringComparison.OrdinalIgnoreCase) ||
         key.StartsWith("__tableSort.", StringComparison.OrdinalIgnoreCase));

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

        foreach (Match match in BuiltInNamespaceTokenRegex().Matches(expression))
        {
            string rawNs = match.Groups[1].Value;
            string ns = string.Equals(rawNs, "$pomo", StringComparison.OrdinalIgnoreCase) ? "$pomodoro" : rawNs;
            string subPath = match.Groups[2].Value.TrimEnd('.');
            if (!ValidNamespaceTokens.TryGetValue(ns, out IReadOnlyList<string>? validList) ||
                !ValidNamespaceTokenSets.TryGetValue(ns, out FrozenSet<string>? validSet))
            {
                continue;
            }

            if (IsTokenSupportedOrAliased(ns, subPath, validSet))
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

    private static bool IsTokenSupportedOrAliased(string ns, string subPath, FrozenSet<string> validSet)
    {
        if (validSet.Contains(subPath))
        {
            return true;
        }

        if (string.Equals(ns, "$sys", StringComparison.OrdinalIgnoreCase))
        {
            if (subPath.StartsWith("ram.", StringComparison.OrdinalIgnoreCase))
            {
                string mapped = "memory." + subPath["ram.".Length..];
                if (validSet.Contains(mapped))
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
            return subPath is "title" or "traditional" or "traditionalTitle" or "ganzhi" or "holiday"
                           or "lunarDate" or "holidayName";
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
}
