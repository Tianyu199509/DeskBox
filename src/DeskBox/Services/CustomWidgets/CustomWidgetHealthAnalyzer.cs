using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetHealthIssue(
    string Code,
    string Message,
    string? SuggestedPrompt,
    bool IsWarning = true);

public sealed record CustomWidgetHealthReport(
    int TargetWidth,
    int TargetHeight,
    double MeasuredWidth,
    double MeasuredHeight,
    int VerticalOverflowPx,
    int MaxCardNestingDepth,
    IReadOnlyList<CustomWidgetHealthIssue> Issues,
    string PrivacySafeDataShapeSummary)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// Host-enforced safety gates and deterministic layout/theme/reference telemetry for Studio co-creation:
/// 1) P0 User Data Safety Gate (blocks accidental loss of populated <c>store.json</c> collections/state);
/// 2) Structural AST Diff between revisions (detects silent drift or dropped i18n locales);
/// 3) Live Health &amp; Privacy-Safe Data Shape Telemetry injected into subsequent AI turns.
/// </summary>
public static partial class CustomWidgetHealthAnalyzer
{
    public static CustomWidgetHealthReport AnalyzeHealth(
        string rawManifestJson,
        CustomWidgetManifest manifest,
        CustomWidgetStorePayload? storePayload,
        double measuredWidth = 0,
        double measuredHeight = 0,
        string? cultureName = null,
        string? logicJs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<CustomWidgetHealthIssue>();

        int targetW = manifest.DefaultSize.Width;
        int targetH = manifest.DefaultSize.Height;
        // Account for ~38px widget title chrome inside frame
        double availableBodyHeight = Math.Max(100, targetH - 36);
        int verticalOverflow = 0;

        bool hasRootScrollableList = HasRootFlexibleListOrScroll(manifest.View, depth: 0, manifest: manifest);
        double estimatedHeight = EstimateManifestLayoutHeight(manifest, out int staticOverflowPx, out string? overflowNodePath);
        if (!hasRootScrollableList)
        {
            if (measuredHeight > 0 && measuredHeight > availableBodyHeight + 24)
            {
                verticalOverflow = (int)Math.Round(measuredHeight - availableBodyHeight);
            }
            else if (measuredHeight <= 0 && staticOverflowPx > 24)
            {
                verticalOverflow = staticOverflowPx;
            }

            if (verticalOverflow > 0)
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "layout.vertical_overflow",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.VerticalOverflowMessageFormat",
                        verticalOverflow),
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.VerticalOverflowAdviceFormat",
                        verticalOverflow)));
            }
        }

        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            if (HasRootFlexibleListOrScroll(pop))
            {
                continue;
            }

            double popHeight = EstimatePopoverHeight(pop);
            if (popHeight > 420)
            {
                int popOverflow = (int)Math.Ceiling(popHeight - 420);
                bool isZh = !string.IsNullOrWhiteSpace(cultureName) &&
                    cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "layout.popover_overflow",
                    Message: isZh
                        ? $"弹出层 {pop.NodePath} 内容预估高度（{Math.Round(popHeight)}px）超出 420px 弹层上限约 {popOverflow}px。"
                        : $"Popover {pop.NodePath} estimated height ({Math.Round(popHeight)}px) exceeds 420px limit by ~{popOverflow}px.",
                    SuggestedPrompt: isZh
                        ? $"请为 {pop.NodePath} 增加 Scroll 容器或精简内容高度，确保弹出层内容在 420px 内。"
                        : $"Wrap {pop.NodePath} content in a Scroll container or tighten items to stay within 420px."));
            }
        }

        AnalyzeCjkColumnSqueeze(manifest, cultureName, issues);

        if (TryDetectDuplicateInnerHeader(manifest, cultureName, out CustomWidgetHealthIssue? dupHeaderIssue) &&
            dupHeaderIssue is not null)
        {
            issues.Add(dupHeaderIssue);
        }

        int maxCardDepth = ComputeMaxCardNestingDepth(manifest.View, 0);
        if (maxCardDepth >= 3)
        {
            issues.Add(new CustomWidgetHealthIssue(
                Code: "visual.card_nesting",
                Message: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.CardNestingMessageFormat",
                    maxCardDepth),
                SuggestedPrompt: LocalizationService.TranslateForCulture(
                    cultureName,
                    "CustomWidget.Health.CardNestingAdvice")));
        }

        var hardcodedHex = new List<string>();
        CollectHardcodedContrastHazardColors(manifest.View, hardcodedHex);
        if (hardcodedHex.Count > 0)
        {
            string sample = hardcodedHex[0];
            issues.Add(new CustomWidgetHealthIssue(
                Code: "theme.hardcoded_hex",
                Message: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.HardcodedHexMessageFormat",
                    sample),
                SuggestedPrompt: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.HardcodedHexAdviceFormat",
                    sample)));
        }

        if (manifest.I18n.Count >= 2)
        {
            var allKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dict in manifest.I18n.Values)
            {
                foreach (string k in dict.Keys)
                {
                    allKeys.Add(k);
                }
            }

            bool isForkWidget = manifest.Id.StartsWith("user.fork-", StringComparison.OrdinalIgnoreCase) ||
                                !string.IsNullOrWhiteSpace(manifest.ForkedFromId);
            string activeCulture = !string.IsNullOrWhiteSpace(cultureName)
                ? cultureName
                : CultureInfo.CurrentUICulture.Name;
            string activeLang = activeCulture.Split('-')[0];

            var incompleteLocales = manifest.I18n
                .Where(kvp =>
                {
                    if (kvp.Value.Count >= allKeys.Count)
                    {
                        return false;
                    }

                    if (!isForkWidget)
                    {
                        return true;
                    }

                    return string.Equals(kvp.Key, activeCulture, StringComparison.OrdinalIgnoreCase) ||
                           kvp.Key.StartsWith(activeLang + "-", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(kvp.Key, activeLang, StringComparison.OrdinalIgnoreCase);
                })
                .Select(kvp => kvp.Key)
                .ToList();
            if (incompleteLocales.Count > 0)
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "i18n.incomplete",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.IncompleteI18nMessageFormat",
                        string.Join(", ", incompleteLocales)),
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.IncompleteI18nAdviceFormat",
                        string.Join(", ", incompleteLocales))));
            }
        }

        foreach (CustomWidgetHealthIssue deadIssue in DetectDeadControlsAndUnreadMutations(manifest, cultureName, logicJs).Take(6))
        {
            issues.Add(deadIssue);
        }

        AnalyzeEmptyContainers(manifest, cultureName, issues);

        if (TryDetectHardcodedParameterCandidate(manifest, cultureName, out CustomWidgetHealthIssue? settingsHint) &&
            settingsHint is not null)
        {
            issues.Add(settingsHint);
        }

        if (!string.IsNullOrWhiteSpace(rawManifestJson))
        {
            CustomWidgetParseResult hintCheck = CustomWidgetManifestParser.ValidateWithRuntimeHints(
                rawManifestJson,
                logicJs: logicJs,
                localeMap: manifest.I18n);
            foreach (CustomWidgetValidationDiagnostic diag in hintCheck.Diagnostics.Take(2))
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "manifest.hint",
                    Message: diag.Message,
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HintAdviceFormat",
                        diag.Path,
                        diag.Message)));
            }
        }

        string dataShape = BuildPrivacySafeDataShapeSummary(manifest, storePayload);
        return new CustomWidgetHealthReport(
            TargetWidth: targetW,
            TargetHeight: targetH,
            MeasuredWidth: Math.Round(measuredWidth, 1),
            MeasuredHeight: Math.Round(measuredHeight, 1),
            VerticalOverflowPx: verticalOverflow,
            MaxCardNestingDepth: maxCardDepth,
            Issues: issues,
            PrivacySafeDataShapeSummary: dataShape);
    }

    public static string BuildPrivacySafeDataShapeSummary(
        CustomWidgetManifest manifest,
        CustomWidgetStorePayload? storePayload)
    {
        var sb = new StringBuilder();
        sb.Append($"Widget DefaultSize: {manifest.DefaultSize.Width}x{manifest.DefaultSize.Height}. ");

        var stateKeys = new HashSet<string>(manifest.State.Keys, StringComparer.Ordinal);
        if (storePayload is not null)
        {
            foreach (string k in storePayload.State.Keys)
            {
                if (!IsEphemeralRuntimeStateKey(k))
                {
                    stateKeys.Add(k);
                }
            }
        }

        if (stateKeys.Count > 0)
        {
            sb.Append("State keys: [").Append(string.Join(", ", stateKeys.OrderBy(x => x))).Append("]. ");
        }

        var allColNames = new HashSet<string>(manifest.Collections.Keys, StringComparer.Ordinal);
        if (storePayload is not null)
        {
            foreach (string k in storePayload.Collections.Keys)
            {
                allColNames.Add(k);
            }
        }

        if (allColNames.Count > 0)
        {
            sb.Append("Collections: ");
            var parts = new List<string>();
            foreach (string col in allColNames.OrderBy(x => x))
            {
                int count = 0;
                var fieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (storePayload is not null &&
                    storePayload.Collections.TryGetValue(col, out List<CustomWidgetRecord>? recs))
                {
                    foreach (CustomWidgetRecord r in recs)
                    {
                        if (r.IsDeleted)
                        {
                            continue;
                        }
                        count++;
                        foreach (string fk in r.Fields.Keys)
                        {
                            fieldNames.Add(fk);
                        }
                    }
                }

                string fieldsStr = fieldNames.Count > 0
                    ? $"fields=[{string.Join(", ", fieldNames.OrderBy(x => x))}]"
                    : "fields=[]";
                parts.Add($"{col}(count={count}, {fieldsStr})");
            }
            sb.Append(string.Join("; ", parts)).Append('.');
        }

        if (storePayload is not null)
        {
            var runtimeNotes = new List<string>();
            if (storePayload.State.TryGetValue("sourceStatus", out string? srcStatus) &&
                !string.IsNullOrWhiteSpace(srcStatus))
            {
                runtimeNotes.Add($"sourceStatus={srcStatus}");
            }
            if (storePayload.State.TryGetValue("sourceError", out string? srcErr) &&
                !string.IsNullOrWhiteSpace(srcErr))
            {
                runtimeNotes.Add($"sourceError=<external_api_sample>{SanitizeTelemetrySample(srcErr)}</external_api_sample>");
            }
            foreach ((string sk, string sv) in storePayload.State)
            {
                if (sk.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(sv))
                {
                    runtimeNotes.Add($"{sk}=<external_api_sample>{SanitizeTelemetrySample(sv)}</external_api_sample>");
                }
            }
            if (storePayload.State.TryGetValue("aiError", out string? aiErr) &&
                !string.IsNullOrWhiteSpace(aiErr))
            {
                runtimeNotes.Add($"aiError=<external_api_sample>{SanitizeTelemetrySample(aiErr)}</external_api_sample>");
            }

            if (runtimeNotes.Count > 0)
            {
                sb.Append(" Runtime diagnostics: ").Append(string.Join(", ", runtimeNotes)).Append('.');
            }
        }

        return sb.ToString().Trim();
    }

    private static string SanitizeTelemetrySample(string raw)
    {
        string trimmed = raw.Replace("\r", " ", StringComparison.Ordinal)
                            .Replace("\n", " ", StringComparison.Ordinal)
                            .Replace("</external_api_sample>", "[/external_api_sample]", StringComparison.OrdinalIgnoreCase)
                            .Trim();
        return trimmed.Length > 240 ? trimmed[..240] + "…" : trimmed;
    }

    public static string FormatTelemetryPromptBlock(CustomWidgetHealthReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Live Host Telemetry & Data Shape (Privacy-Safe)");
        sb.Append("- ").AppendLine(report.PrivacySafeDataShapeSummary);
        if (report.MeasuredHeight > 0)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "- Rendered Content Height: {0:0}px inside {1}x{2} frame{3}.",
                report.MeasuredHeight,
                report.TargetWidth,
                report.TargetHeight,
                report.VerticalOverflowPx > 0 ? $" (VERTICAL OVERFLOW +{report.VerticalOverflowPx}px — make layout more compact!)" : " (fits cleanly)");
            sb.AppendLine();
        }

        if (report.Issues.Count > 0)
        {
            sb.AppendLine("- Detected Health Warnings to keep in mind:");
            foreach (CustomWidgetHealthIssue issue in report.Issues)
            {
                sb.Append("  * [").Append(issue.Code).Append("] ").AppendLine(issue.Message);
            }
        }

        return sb.ToString().Trim();
    }

    private static bool TryDetectDuplicateInnerHeader(
        CustomWidgetManifest manifest,
        string? cultureName,
        out CustomWidgetHealthIssue? issue)
    {
        issue = null;
        string resolvedWidgetTitle = manifest.ResolveLocalizedText(manifest.Name);
        if (string.IsNullOrWhiteSpace(resolvedWidgetTitle))
        {
            resolvedWidgetTitle = manifest.Name;
        }

        string normalizedWidgetTitle = NormalizeHeaderComparableText(resolvedWidgetTitle);
        if (string.IsNullOrWhiteSpace(normalizedWidgetTitle))
        {
            return false;
        }

        string normalizedPrimarySegment = ExtractPrimaryTitlePrefixComparableText(resolvedWidgetTitle);

        CustomWidgetNodeSpec root = manifest.View;
        if (root.Children.Count == 0)
        {
            return false;
        }

        string? candidateRaw = TryExtractLeadingHeaderText(root.Children[0], maxContainerDepth: 2);
        if (string.IsNullOrWhiteSpace(candidateRaw))
        {
            return false;
        }

        string resolvedCandidate = manifest.ResolveLocalizedText(candidateRaw);
        string normalizedCandidate = NormalizeHeaderComparableText(resolvedCandidate);
        bool matchesFull = !string.IsNullOrWhiteSpace(normalizedCandidate) &&
                           string.Equals(normalizedCandidate, normalizedWidgetTitle, StringComparison.OrdinalIgnoreCase);
        bool matchesPrimaryPrefix = !matchesFull &&
                                    normalizedCandidate.Length >= 3 &&
                                    !string.IsNullOrWhiteSpace(normalizedPrimarySegment) &&
                                    string.Equals(normalizedCandidate, normalizedPrimarySegment, StringComparison.OrdinalIgnoreCase);
        if (!matchesFull && !matchesPrimaryPrefix)
        {
            return false;
        }

        issue = new CustomWidgetHealthIssue(
            Code: "layout.duplicate_header",
            Message: LocalizationService.TranslateForCulture(
                cultureName,
                "CustomWidget.Health.DupHeaderMessage"),
            SuggestedPrompt: LocalizationService.TranslateForCulture(
                cultureName,
                "CustomWidget.Health.DupHeaderAdvice"));
        return true;
    }

    private static string? TryExtractLeadingHeaderText(CustomWidgetNodeSpec node, int maxContainerDepth)
    {
        if (string.Equals(node.Type, "Text", StringComparison.OrdinalIgnoreCase))
        {
            if (maxContainerDepth < 2 ||
                string.Equals(node.Style, "title", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Style, "subtitle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Style, "bodyStrong", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Style, "metric", StringComparison.OrdinalIgnoreCase))
            {
                return node.Text ?? node.Title;
            }

            return null;
        }

        if (string.Equals(node.Type, "RichText", StringComparison.OrdinalIgnoreCase) &&
            node.Spans.Count > 0)
        {
            string? firstSpanText = node.Spans[0].Text;
            if (!string.IsNullOrWhiteSpace(firstSpanText) &&
                !firstSpanText.Contains("${", StringComparison.Ordinal))
            {
                return firstSpanText;
            }

            return null;
        }

        if (maxContainerDepth > 0 &&
            (string.Equals(node.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "Card", StringComparison.OrdinalIgnoreCase)) &&
            node.Children.Count > 0)
        {
            foreach (CustomWidgetNodeSpec child in node.Children)
            {
                if (string.Equals(child.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(child.Type, "RichText", StringComparison.OrdinalIgnoreCase))
                {
                    return TryExtractLeadingHeaderText(child, maxContainerDepth - 1);
                }
            }

            return TryExtractLeadingHeaderText(node.Children[0], maxContainerDepth - 1);
        }

        return null;
    }

    private static string ExtractPrimaryTitlePrefixComparableText(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        int sepIdx = rawTitle.IndexOfAny(['·', '•', '—', '–', '-', '|', '/', '：', ':']);
        return sepIdx > 0 ? NormalizeHeaderComparableText(rawTitle[..sepIdx]) : string.Empty;
    }

    private static string NormalizeHeaderComparableText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    private static int ComputeMaxCardNestingDepth(CustomWidgetNodeSpec? node, int currentCardDepth)
    {
        if (node is null)
        {
            return currentCardDepth;
        }

        int nextDepth = string.Equals(node.Type, "Card", StringComparison.OrdinalIgnoreCase)
            ? currentCardDepth + 1
            : currentCardDepth;
        int max = nextDepth;

        max = Math.Max(max, ComputeMaxCardNestingDepth(node.ItemTemplate, nextDepth));
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            max = Math.Max(max, ComputeMaxCardNestingDepth(c, nextDepth));
        }
        return max;
    }

    private static void CollectHardcodedContrastHazardColors(
        CustomWidgetNodeSpec? node,
        List<string> results)
    {
        if (node is null)
        {
            return;
        }

        foreach (CustomWidgetNodeSpec cur in node.EnumerateAllNodes())
        {
            CheckHexHazard(cur.Color, results);
            CheckHexHazard(cur.Tone, results);
            CheckHexHazard(cur.TextColor, results);

            if (IsLayoutContainerNodeType(cur.Type))
            {
                CheckRawSurfaceOverrideHazard(cur.Background, results);
                if (cur.LinearGradient is { Stops.Count: > 0 })
                {
                    foreach (CustomWidgetGradientStopSpec stop in cur.LinearGradient.Stops)
                    {
                        CheckRawSurfaceOverrideHazard(stop.Color, results);
                    }
                }
                if (cur.RadialGradient is { Stops.Count: > 0 })
                {
                    foreach (CustomWidgetGradientStopSpec stop in cur.RadialGradient.Stops)
                    {
                        CheckRawSurfaceOverrideHazard(stop.Color, results);
                    }
                }
            }
        }
    }

    private static bool IsLayoutContainerNodeType(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return false;
        }

        return nodeType.Trim().ToLowerInvariant() switch
        {
            "stack" or "grid" or "card" or "scroll" or "wrap" or
            "zstack" or "itemsgrid" or "splitview" or "flipview" or "modulestack" => true,
            _ => false
        };
    }

    private static void CheckRawSurfaceOverrideHazard(string? raw, List<string> results)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (trimmed.StartsWith('#') ||
            trimmed.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
        {
            if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(trimmed);
            }
        }
    }

    private static void CheckHexHazard(string? raw, List<string> results)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string trimmed = raw.Trim();
        if (!trimmed.StartsWith('#'))
        {
            return;
        }

        string hex = trimmed[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }
        else if (hex.Length == 8)
        {
            if (int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int alpha) && alpha < 90)
            {
                return; // Low-alpha tint is safe across themes
            }
            hex = hex[2..];
        }

        if (hex.Length == 6 &&
            int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) &&
            int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) &&
            int.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
        {
            double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            // Near-white (> 0.85) or near-black (< 0.18) hardcoded hex breaks in Light or Dark mode
            if (luminance > 0.85 || luminance < 0.18)
            {
                if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(trimmed);
                }
            }
        }
    }

    [GeneratedRegex(@"\bstate\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StateTokenReadRegex { get; }

    [GeneratedRegex(@"\$computed\.([A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ComputedTokenReadRegex { get; }

    private static readonly System.Collections.Frozen.FrozenSet<string> ValidSystemControlCommands =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "volume.set", "volume.step", "volume.mutetoggle", "volume.mute",
            "brightness.set", "brightness.step",
            "audio.output", "audio.settings", "sound.output",
            "media.playpause", "media.toggle", "media.next", "media.previous", "media.prev",
            "power.keepawaketoggle", "power.caffeine", "power.keepawakeon", "power.keepawakeoff",
            "power.lock", "lock",
            "recyclebin.empty",
            "radio.wifitoggle", "wifi.toggle",
            "radio.bluetoothtoggle", "bluetooth.toggle",
            "theme.toggle"
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Detects "interactive facade" patterns, invalid script/state bindings, invalid
    /// <c>system.control</c> commands, and raw container surface overrides.
    /// </summary>
    public static IReadOnlyList<CustomWidgetHealthIssue> DetectDeadControlsAndUnreadMutations(
        CustomWidgetManifest manifest,
        string? cultureName = null,
        string? logicJs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        bool isZh = !string.IsNullOrWhiteSpace(cultureName) &&
                    cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var issues = new List<CustomWidgetHealthIssue>();
        var readOrBoundKeys = new HashSet<string>(StringComparer.Ordinal);
        var uiReadStateKeys = new HashSet<string>(StringComparer.Ordinal);
        var uiReadComputedKeys = new HashSet<string>(StringComparer.Ordinal);
        var mutatedKeys = new List<(string Key, string Path)>();
        var containerSurfaceOverrides = new List<string>();
        bool reportedInvalidSystemControl = false;

        var filterReferencedStateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomWidgetNodeSpec n in manifest.EnumerateAllNodes())
        {
            if (!string.IsNullOrWhiteSpace(n.Filter))
            {
                foreach (Match fm in StateTokenReadRegex.Matches(n.Filter))
                {
                    if (!string.IsNullOrWhiteSpace(fm.Groups[1].Value))
                    {
                        filterReferencedStateKeys.Add(fm.Groups[1].Value);
                    }
                }
            }
        }

        bool IsFilterOrSearchInputNode(CustomWidgetNodeSpec input)
        {
            string rawBind = input.Bind ?? string.Empty;
            string leafBind = rawBind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                ? rawBind["state.".Length..]
                : rawBind;
            if (!string.IsNullOrWhiteSpace(leafBind) && filterReferencedStateKeys.Contains(leafBind))
            {
                return true;
            }

            static bool HasSearchHint(string? s) =>
                !string.IsNullOrWhiteSpace(s) &&
                (s.Contains("keyword", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("搜索", StringComparison.Ordinal) ||
                 s.Contains("筛选", StringComparison.Ordinal) ||
                 s.Contains("过滤", StringComparison.Ordinal) ||
                 s.Contains("查找", StringComparison.Ordinal));

            return HasSearchHint(leafBind) || HasSearchHint(input.Id) || HasSearchHint(input.Placeholder);
        }

        void RecordExprReads(string? expr, bool isUiRead = false)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            foreach (Match m in StateTokenReadRegex.Matches(expr))
            {
                string key = m.Groups[1].Value;
                readOrBoundKeys.Add(key);
                if (isUiRead)
                {
                    uiReadStateKeys.Add(key);
                }
            }

            if (isUiRead)
            {
                foreach (Match cm in ComputedTokenReadRegex.Matches(expr))
                {
                    string key = cm.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        uiReadComputedKeys.Add(key);
                    }
                }
            }
        }

        void ScanActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                RecordExprReads(act.Key);
                RecordExprReads(act.Value);
                RecordExprReads(act.TargetId);
                RecordExprReads(act.Text);
                RecordExprReads(act.Message);
                RecordExprReads(act.Url);
                RecordExprReads(act.Path);
                RecordExprReads(act.Guard);
                RecordExprReads(act.Prompt);
                RecordExprReads(act.Arg);
                RecordExprReads(act.Body);
                RecordExprReads(act.WorkingDirectory);
                RecordExprReads(act.PopoverId);
                RecordExprReads(act.FromIndex);
                RecordExprReads(act.ToIndex);
                if (act.ItemFields is not null)
                {
                    foreach (string v in act.ItemFields.Values)
                    {
                        RecordExprReads(v);
                    }
                }
                if (act.Headers is not null)
                {
                    foreach (string hv in act.Headers.Values)
                    {
                        RecordExprReads(hv);
                    }
                }
                if (act.Args is not null)
                {
                    foreach (string av in act.Args.Values)
                    {
                        RecordExprReads(av);
                    }
                }
                if (act.CommandArgs is not null)
                {
                    foreach (string ca in act.CommandArgs)
                    {
                        RecordExprReads(ca);
                    }
                }

                if ((string.Equals(act.Type, "state.set", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "state.toggle", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "state.increment", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(act.Key))
                {
                    string normKey = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? act.Key["state.".Length..]
                        : act.Key;
                    if (!string.IsNullOrWhiteSpace(normKey) &&
                        !normKey.StartsWith("pomo.", StringComparison.OrdinalIgnoreCase) &&
                        !normKey.StartsWith("countdown.", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(normKey, "currentTask", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(normKey, "selectedProviderId", StringComparison.OrdinalIgnoreCase))
                    {
                        mutatedKeys.Add((normKey, actPath));
                    }
                }

                if (string.Equals(act.Type, "system.control", StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = !string.IsNullOrWhiteSpace(act.Key)
                        ? act.Key.Trim()
                        : !string.IsNullOrWhiteSpace(act.TargetId)
                            ? act.TargetId.Trim()
                            : (act.Value ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(cmd) &&
                        !cmd.Contains("${", StringComparison.Ordinal) &&
                        !ValidSystemControlCommands.Contains(cmd) &&
                        !CustomWidgetSystemControlService.IsSupportedSettingTarget(cmd) &&
                        !reportedInvalidSystemControl)
                    {
                        reportedInvalidSystemControl = true;
                        issues.Add(new CustomWidgetHealthIssue(
                            Code: "interaction.invalid_system_control",
                            Message: isZh
                                ? $"动作 {actPath} 使用了不支持的 system.control 指令 '{cmd}'。"
                                : $"Action {actPath} uses unsupported system.control target '{cmd}'.",
                            SuggestedPrompt: isZh
                                ? $"请将无效的 system.control 目标 '{cmd}' 替换为合法的 `system.control`（如 `volume.set`、`media.playPause`、`power.keepAwakeToggle`、`radio.wifiToggle`）或改用 `system.openSetting`（仅限 `taskmgr`、`storage`、`display`、`sound`、`wifi`、`bluetooth`、`power`、`windowsupdate`）。"
                                : $"Replace invalid system.control target '{cmd}' with a supported `system.control` command (`volume.set`, `media.playPause`, `power.keepAwakeToggle`, `radio.wifiToggle`) or `system.openSetting` (`taskmgr`, `storage`, `display`, `sound`, `wifi`, `bluetooth`, `power`, `windowsupdate`)."));
                    }
                }

                ScanActions(act.Then, $"{actPath}.then");
            }
        }

        void ScanMenuItems(IReadOnlyList<CustomWidgetMenuItemSpec>? menuItems, string basePath)
        {
            if (menuItems is null)
            {
                return;
            }

            for (int i = 0; i < menuItems.Count; i++)
            {
                CustomWidgetMenuItemSpec mi = menuItems[i];
                string itemPath = $"{basePath}[{i}]";
                RecordExprReads(mi.Label, isUiRead: true);
                RecordExprReads(mi.When, isUiRead: true);
                RecordExprReads(mi.Value, isUiRead: true);
                ScanActions(mi.OnClick, $"{itemPath}.onClick");
                ScanMenuItems(mi.Children, $"{itemPath}.children");
            }
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            RecordExprReads(src.Url);
            RecordExprReads(src.City);
            RecordExprReads(src.Path);
            RecordExprReads(src.Command);
            RecordExprReads(src.WorkingDirectory);
            if (src.Headers is not null)
            {
                foreach (string hv in src.Headers.Values)
                {
                    RecordExprReads(hv);
                }
            }
            if (src.Arguments is not null)
            {
                foreach (string ca in src.Arguments)
                {
                    RecordExprReads(ca);
                }
            }
        }

        if (manifest.Capsule is { } cap)
        {
            RecordExprReads(cap.Title);
            RecordExprReads(cap.Summary);
            RecordExprReads(cap.Badge);
            RecordExprReads(cap.Progress);
            RecordExprReads(cap.BadgeIsWarning);
            ScanActions(cap.PrimaryAction?.OnClick, "$.capsule.primaryAction.onClick");
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            foreach (Match jsMatch in JsCtxStateReadRegex.Matches(logicJs))
            {
                string jsKey = jsMatch.Groups[1].Success ? jsMatch.Groups[1].Value : jsMatch.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(jsKey))
                {
                    readOrBoundKeys.Add(jsKey);
                }
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            string path = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : "$.view";

            if (IsLayoutContainerNodeType(node.Type))
            {
                CheckRawSurfaceOverrideHazard(node.Background, containerSurfaceOverrides);
                if (node.LinearGradient is { Stops.Count: > 0 })
                {
                    foreach (CustomWidgetGradientStopSpec stop in node.LinearGradient.Stops)
                    {
                        CheckRawSurfaceOverrideHazard(stop.Color, containerSurfaceOverrides);
                    }
                }
            }

            if ((string.Equals(node.Type, "Button", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "IconButton", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "ControlTile", StringComparison.OrdinalIgnoreCase)) &&
                node.OnClick.Count == 0 &&
                string.IsNullOrWhiteSpace(node.Popover))
            {
                string label = node.Title ?? node.Text ?? node.Tooltip ?? node.Type;
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "interaction.dead_button",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.DeadButtonMessageFormat",
                        label,
                        node.Type),
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.DeadButtonAdviceFormat",
                        label,
                        path)));
            }

            bool isHorizontalComposerRow =
                string.Equals(node.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(node.Type, "Stack", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase));
            if (isHorizontalComposerRow && node.Children.Count >= 2)
            {
                CustomWidgetNodeSpec? unsubmittedInput = node.Children.FirstOrDefault(static c =>
                    (string.Equals(c.Type, "Input", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Type, "TextInput", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(c.Bind) &&
                    c.OnSubmit.Count == 0);
                CustomWidgetNodeSpec? popoverOnlyBtn = node.Children.FirstOrDefault(static c =>
                    (string.Equals(c.Type, "Button", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(c.Type, "IconButton", StringComparison.OrdinalIgnoreCase)) &&
                    (!string.IsNullOrWhiteSpace(c.Popover) ||
                     (c.OnClick.Count > 0 && c.OnClick.All(static a =>
                         a.Type.StartsWith("popover.", StringComparison.OrdinalIgnoreCase) &&
                         (a.Then is null || a.Then.Count == 0)))));
                if (unsubmittedInput is not null &&
                    popoverOnlyBtn is not null &&
                    !IsFilterOrSearchInputNode(unsubmittedInput))
                {
                    string btnLabel = popoverOnlyBtn.Title ?? popoverOnlyBtn.Text ?? popoverOnlyBtn.Tooltip ?? popoverOnlyBtn.Type;
                    issues.Add(new CustomWidgetHealthIssue(
                        Code: "interaction.inline_input_opens_popover",
                        Message: isZh
                            ? $"交互栏 `{path}` 中的输入框未绑定提交动作（onSubmit），而右侧按钮「{btnLabel}」仅打开弹窗（popover），导致填写后仍需二次录入。"
                            : $"Composer bar `{path}` pairs an Input (without onSubmit) with button '{btnLabel}' that only opens a Popover, causing a two-step entry loop.",
                        SuggestedPrompt: isZh
                            ? $"重构 `{path}` 的录入流：若保留内联快捷输入框，请让 `Input.onSubmit` 与右侧按钮 `onClick` 直接提交写入集合或调用 `script.run` 保存，不要再弹出填写弹窗；若需要多字段表单，请移除内联 `Input`，仅保留一个按钮打开完整表单弹窗。"
                            : $"Fix composer bar `{path}`: if keeping an inline Input, wire BOTH `Input.onSubmit` and the button `onClick` to commit directly (via `collection.*` or `script.run`); if multiple fields are required, remove the inline Input and use a single button to open the form Popover."));
                }
            }

            if (!string.IsNullOrWhiteSpace(node.Bind) &&
                !node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
            {
                string normBind = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? node.Bind["state.".Length..]
                    : node.Bind;
                if (!string.IsNullOrWhiteSpace(normBind))
                {
                    readOrBoundKeys.Add(normBind);
                }
            }

            RecordExprReads(node.When, isUiRead: true);
            RecordExprReads(node.Text, isUiRead: true);
            RecordExprReads(node.Title, isUiRead: true);
            RecordExprReads(node.Subtitle, isUiRead: true);
            RecordExprReads(node.Value, isUiRead: true);
            RecordExprReads(node.Icon, isUiRead: true);
            RecordExprReads(node.Src, isUiRead: true);
            RecordExprReads(node.Color, isUiRead: true);
            RecordExprReads(node.Tone, isUiRead: true);
            RecordExprReads(node.TextColor, isUiRead: true);
            RecordExprReads(node.Background, isUiRead: true);
            RecordExprReads(node.BorderColor, isUiRead: true);
            RecordExprReads(node.CenterValue, isUiRead: true);
            RecordExprReads(node.CenterTitle, isUiRead: true);
            RecordExprReads(node.ElementsSource, isUiRead: true);
            RecordExprReads(node.SuggestionsSource, isUiRead: true);
            RecordExprReads(node.Progress, isUiRead: true);
            RecordExprReads(node.Indeterminate, isUiRead: true);
            RecordExprReads(node.Filter, isUiRead: true);
            RecordExprReads(node.FilterEquals, isUiRead: true);
            RecordExprReads(node.Collection, isUiRead: true);
            RecordExprReads(node.Items, isUiRead: true);
            RecordExprReads(node.Sparkline, isUiRead: true);
            RecordExprReads(node.Unit, isUiRead: true);
            RecordExprReads(node.FileName, isUiRead: true);
            RecordExprReads(node.FilePath, isUiRead: true);

            foreach (CustomWidgetTableColumnSpec col in node.TableColumns)
            {
                RecordExprReads(col.Header);
                RecordExprReads(col.Field);
                RecordExprReads(col.Color);
            }
            foreach (CustomWidgetChartSeriesSpec s in node.Series)
            {
                RecordExprReads(s.Name);
                RecordExprReads(s.ValueField);
                RecordExprReads(s.Color);
            }
            foreach (CustomWidgetCanvasElementSpec el in node.Elements)
            {
                RecordExprReads(el.X);
                RecordExprReads(el.Y);
                RecordExprReads(el.Width);
                RecordExprReads(el.Height);
                RecordExprReads(el.Radius);
                RecordExprReads(el.Rx);
                RecordExprReads(el.Ry);
                RecordExprReads(el.X2);
                RecordExprReads(el.Y2);
                RecordExprReads(el.Points);
                RecordExprReads(el.Data);
                RecordExprReads(el.Text);
                RecordExprReads(el.Fill);
                RecordExprReads(el.Stroke);
            }
            for (int i = 0; i < node.Spans.Count; i++)
            {
                RecordExprReads(node.Spans[i].Text, isUiRead: true);
                RecordExprReads(node.Spans[i].Color, isUiRead: true);
                RecordExprReads(node.Spans[i].Href, isUiRead: true);
                ScanActions(node.Spans[i].OnClick, $"{path}.spans[{i}].onClick");
            }

            ScanMenuItems(node.MenuItems, $"{path}.menuItems");
            ScanMenuItems(node.ContextMenu, $"{path}.contextMenu");
            ScanActions(node.OnClick, $"{path}.onClick");
            ScanActions(node.OnSubmit, $"{path}.onSubmit");
            ScanActions(node.OnChange, $"{path}.onChange");
            ScanActions(node.OnDropActions, $"{path}.onDrop");
            ScanActions(node.OnRowClick, $"{path}.onRowClick");
            ScanActions(node.OnItemClick, $"{path}.onItemClick");
            ScanActions(node.OnReorder, $"{path}.onReorder");
            ScanActions(node.OnInlineCommit, $"{path}.onInlineCommit");
            ScanActions(node.OnPointerDown, $"{path}.onPointerDown");
            ScanActions(node.OnPointerMove, $"{path}.onPointerMove");
            ScanActions(node.OnPointerUp, $"{path}.onPointerUp");
        }

        ScanActions(manifest.OnDrop?.Actions, "$.onDrop.actions");

        bool skipUnreadCheckBecauseExternalScriptNotLoaded =
            manifest.Script is not null && string.IsNullOrWhiteSpace(logicJs);

        var reportedUnread = new HashSet<string>(StringComparer.Ordinal);
        if (!skipUnreadCheckBecauseExternalScriptNotLoaded)
        {
            foreach ((string key, string actPath) in mutatedKeys)
            {
                if (!readOrBoundKeys.Contains(key) && reportedUnread.Add(key))
                {
                    issues.Add(new CustomWidgetHealthIssue(
                        Code: "interaction.unread_state",
                        Message: LocalizationService.FormatForCulture(
                            cultureName,
                            "CustomWidget.Health.UnreadStateMessageFormat",
                            key,
                            actPath),
                        SuggestedPrompt: LocalizationService.FormatForCulture(
                            cultureName,
                            "CustomWidget.Health.UnreadStateAdviceFormat",
                            key,
                            actPath)));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            if (JsCtxSourcesRegex.IsMatch(logicJs))
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "script.invalid_ctx_sources",
                    Message: isZh
                        ? "logic.js 访问了不存在的 ctx.sources 对象，会导致脚本读取全部失效或报错。"
                        : "logic.js references non-existent ctx.sources, which breaks script telemetry reads.",
                    SuggestedPrompt: isZh
                        ? "请移除 logic.js 中的 ctx.sources：系统监控与设备状态请直接使用 `ctx.env.sys.*`（如 `cpuPercent`、`memoryPercent`、`gpuPercent`、`diskPercent`、`netDown`、`netUp`）和 `ctx.env.device.*`（如 `volume`、`brightness`、`wifiEnabled`），或在 view 中直接绑定声明式令牌 `${$sys.cpu.percent}`。"
                        : "Remove ctx.sources from logic.js: read telemetry from `ctx.env.sys.*` (`cpuPercent`, `memoryPercent`, `gpuPercent`, `diskPercent`, `netDown`, `netUp`) and `ctx.env.device.*`, or bind declarative `${$sys.cpu.percent}` directly in view without logic.js."));
            }

            var computedWrites = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match cw in JsCtxComputedWriteRegex.Matches(logicJs))
            {
                string key = cw.Groups[1].Success ? cw.Groups[1].Value : cw.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    computedWrites.Add(key);
                }
            }

            var stateWrites = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match sw in JsCtxStateWriteRegex.Matches(logicJs))
            {
                string key = sw.Groups[1].Success ? sw.Groups[1].Value : sw.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    stateWrites.Add(key);
                }
            }

            var actionMutatedSet = new HashSet<string>(mutatedKeys.Select(static k => k.Key), StringComparer.Ordinal);
            List<string> stateUiComputedJsMismatches = uiReadStateKeys
                .Where(k => computedWrites.Contains(k) && !stateWrites.Contains(k) && !actionMutatedSet.Contains(k))
                .ToList();
            List<string> computedUiStateJsMismatches = uiReadComputedKeys
                .Where(k => stateWrites.Contains(k) && !computedWrites.Contains(k))
                .ToList();
            List<string> mismatchedKeys = stateUiComputedJsMismatches
                .Concat(computedUiStateJsMismatches)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (mismatchedKeys.Count > 0)
            {
                bool isComputedUiReadingStateJs = stateUiComputedJsMismatches.Count == 0;
                string firstKey = mismatchedKeys[0];
                string sampleKeys = string.Join(", ", mismatchedKeys.Take(3));
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "script.state_computed_mismatch",
                    Message: isZh
                        ? (isComputedUiReadingStateJs
                            ? $"视图绑定了 $computed.{firstKey}，但 logic.js 写入的是 ctx.state.{firstKey}（两者命名空间不同，界面将永远显示初始值）。"
                            : $"视图绑定了 state.{firstKey}，但 logic.js 写入的是 ctx.computed.{firstKey}（两者命名空间不同，界面将永远显示初始值）。")
                        : (isComputedUiReadingStateJs
                            ? $"View binds $computed.{firstKey}, while logic.js writes ctx.state.{firstKey} (different namespaces; UI will stay stuck at initial value)."
                            : $"View binds state.{firstKey}, while logic.js writes ctx.computed.{firstKey} (different namespaces; UI will stay stuck at initial value)."),
                    SuggestedPrompt: isZh
                        ? $"修复绑定命名空间不一致（{sampleKeys}）：`ctx.computed.<key>` 对应视图 `${{$computed.<key>}}`，`ctx.state.<key>` 对应视图 `${{state.<key>}}`；若显示系统监控数据，更推荐直接绑定 `${{$sys.cpu.percent}}` / `${{$sys.gpu.percent}}` / `${{$sys.memory.percent}}` 而无需写冗余脚本。"
                        : $"Fix namespace mismatch ({sampleKeys}): bind `${{$computed.<key>}}` when logic.js writes `ctx.computed.<key>`, or `${{state.<key>}}` when logic.js writes `ctx.state.<key>`; for system telemetry prefer direct `${{$sys.cpu.percent}}` / `${{$sys.gpu.percent}}` / `${{$sys.memory.percent}}` bindings."));
            }
        }

        if (containerSurfaceOverrides.Count > 0)
        {
            issues.Add(new CustomWidgetHealthIssue(
                Code: "style.container_surface_override",
                Message: isZh
                    ? $"布局容器设置了自定义背景色/渐变（{containerSurfaceOverrides[0]}），会遮挡宿主云母/亚克力材质。"
                    : $"Layout container specifies custom background/gradient ({containerSurfaceOverrides[0]}), which occludes host Mica/Acrylic glass.",
                SuggestedPrompt: isZh
                    ? $"请移除 Stack/Grid/Card 容器上的自定义 `background`（如 {containerSurfaceOverrides[0]}）、`linearGradient`、`cornerRadius`、中性十六进制 `textColor`（如 #E2E8F0）及自定义 `fontSize`；改用宿主默认玻璃底板、`Card.tone`（如 `subtle`/`card`）、语义 `Text.style`（`caption`/`body`/`subtitle`/`metric`）与语义 `color`（`default`/`secondary`/`accent`）。"
                    : $"Remove custom `background` ({containerSurfaceOverrides[0]}), `linearGradient`, `cornerRadius`, neutral hex `textColor`, and raw `fontSize` on Stack/Grid/Card; use host Mica/Acrylic surface, `Card.tone`, semantic `Text.style`, and semantic `color`."));
        }

        return issues;
    }

    private static bool TryDetectHardcodedParameterCandidate(
        CustomWidgetManifest manifest,
        string? cultureName,
        out CustomWidgetHealthIssue? issue)
    {
        issue = null;
        if (manifest.Settings.Count > 0 || manifest.Sources.Count == 0)
        {
            return false;
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (string.Equals(src.Type, "weather", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(src.City) &&
                !src.City.Contains("settings.", StringComparison.OrdinalIgnoreCase))
            {
                issue = new CustomWidgetHealthIssue(
                    Code: "settings.hardcoded_parameter",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingMessageFormat",
                        "weather.city",
                        src.City.Trim()),
                    SuggestedPrompt: LocalizationService.TranslateForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingAdvice"),
                    IsWarning: false);
                return true;
            }

            if ((string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(src.Url) &&
                !src.Url.Contains("settings.", StringComparison.OrdinalIgnoreCase))
            {
                string sourceLabel = !string.IsNullOrWhiteSpace(src.Id) ? src.Id : src.Type;
                issue = new CustomWidgetHealthIssue(
                    Code: "settings.hardcoded_parameter",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingMessageFormat",
                        $"{sourceLabel}.url",
                        src.Url.Trim()),
                    SuggestedPrompt: LocalizationService.TranslateForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingAdvice"),
                    IsWarning: false);
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\bctx\.state(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[['""]([^'""]+)['""]\])", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxStateReadRegex { get; }

    [GeneratedRegex(@"\bctx\.sources\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxSourcesRegex { get; }

    [GeneratedRegex(@"\bctx\.computed(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[['""]([^'""]+)['""]\])\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxComputedWriteRegex { get; }

    [GeneratedRegex(@"\bctx\.state(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[['""]([^'""]+)['""]\])\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex JsCtxStateWriteRegex { get; }
}
