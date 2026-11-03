using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetDroppedCollectionRisk(
    string OldCollectionKey,
    int LiveRecordCount,
    string? SuggestedNewCollectionKey);

public sealed record CustomWidgetDataSafetyReport(
    IReadOnlyList<CustomWidgetDroppedCollectionRisk> DroppedCollections,
    IReadOnlyList<string> DroppedNonEmptyStateKeys)
{
    public bool HasDataLossRisk => DroppedCollections.Count > 0 || DroppedNonEmptyStateKeys.Count > 0;
}

public sealed record CustomWidgetStructuralDiff(
    IReadOnlyList<string> AddedNodeTypes,
    IReadOnlyList<string> RemovedNodeTypes,
    IReadOnlyList<string> AddedCollections,
    IReadOnlyList<string> RemovedCollections,
    IReadOnlyList<string> AddedStateKeys,
    IReadOnlyList<string> RemovedStateKeys,
    IReadOnlyList<string> DroppedLocales,
    CustomWidgetSizeSpec? OldSize,
    CustomWidgetSizeSpec? NewSize)
{
    public bool SizeChanged =>
        OldSize is not null && NewSize is not null &&
        (OldSize.Width != NewSize.Width || OldSize.Height != NewSize.Height);

    public bool HasUnexpectedRegression =>
        DroppedLocales.Count > 0 || RemovedCollections.Count > 0;
}

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
public static class CustomWidgetHealthAnalyzer
{
    private static readonly HashSet<string> EphemeralRuntimeStateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiStatus", "aiError", "aiUpdatedAt",
        "networkStatus", "networkError",
        "sourceStatus", "sourceError", "sourceUpdatedAt", "lastUpdated",
        "feedTitle", "feedCount", "topHeadline",
        "transformStatus", "transformOutput", "transformError"
    };

    public static CustomWidgetDataSafetyReport EvaluateStoreDataSafety(
        CustomWidgetManifest? previousManifest,
        CustomWidgetStorePayload? liveStore,
        CustomWidgetManifest candidateManifest)
    {
        ArgumentNullException.ThrowIfNull(candidateManifest);
        if (liveStore is null)
        {
            return new([], []);
        }

        var candidateCollections = new HashSet<string>(candidateManifest.Collections.Keys, StringComparer.Ordinal);
        CollectReferencedCollectionNames(candidateManifest.View, candidateCollections);

        var oldDeclaredCollections = previousManifest is not null
            ? new HashSet<string>(previousManifest.Collections.Keys, StringComparer.Ordinal)
            : new HashSet<string>(liveStore.Collections.Keys, StringComparer.Ordinal);

        List<string> newlyIntroducedCollections = candidateCollections
            .Where(c => !oldDeclaredCollections.Contains(c))
            .ToList();

        var droppedCollections = new List<CustomWidgetDroppedCollectionRisk>();
        foreach ((string colKey, List<CustomWidgetRecord> records) in liveStore.Collections)
        {
            int activeCount = records.Count(r => !r.IsDeleted);
            if (activeCount <= 0)
            {
                continue;
            }

            if (!candidateCollections.Contains(colKey))
            {
                string? suggestedTarget = newlyIntroducedCollections.Count == 1
                    ? newlyIntroducedCollections[0]
                    : null;
                droppedCollections.Add(new CustomWidgetDroppedCollectionRisk(colKey, activeCount, suggestedTarget));
            }
        }

        var candidateStateKeys = new HashSet<string>(candidateManifest.State.Keys, StringComparer.Ordinal);
        CollectBoundOrWrittenStateKeys(candidateManifest, candidateStateKeys);

        var droppedStateKeys = new List<string>();
        foreach ((string stateKey, string val) in liveStore.State)
        {
            if (string.IsNullOrWhiteSpace(val) || EphemeralRuntimeStateKeys.Contains(stateKey))
            {
                continue;
            }

            // Only flag if previous manifest declared/used it and candidate completely dropped it
            bool wasInOld = previousManifest is null ||
                            previousManifest.State.ContainsKey(stateKey);
            if (wasInOld && !candidateStateKeys.Contains(stateKey))
            {
                droppedStateKeys.Add(stateKey);
            }
        }

        return new CustomWidgetDataSafetyReport(droppedCollections, droppedStateKeys);
    }

    public static async Task MigrateRenamedCollectionsAsync(
        CustomWidgetDataStore store,
        IReadOnlyList<(string OldCollectionKey, string NewCollectionKey)> renames)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (renames.Count == 0)
        {
            return;
        }

        CustomWidgetStorePayload payload = await store.LoadAsync();
        bool changed = false;
        foreach ((string oldKey, string newKey) in renames)
        {
            if (string.IsNullOrWhiteSpace(oldKey) ||
                string.IsNullOrWhiteSpace(newKey) ||
                string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (payload.Collections.TryGetValue(oldKey, out List<CustomWidgetRecord>? existingRecords) &&
                existingRecords.Count > 0)
            {
                if (!payload.Collections.TryGetValue(newKey, out List<CustomWidgetRecord>? dest))
                {
                    dest = [];
                    payload.Collections[newKey] = dest;
                }

                var existingIds = new HashSet<string>(dest.Select(r => r.Id), StringComparer.Ordinal);
                foreach (CustomWidgetRecord rec in existingRecords)
                {
                    if (existingIds.Add(rec.Id))
                    {
                        dest.Add(rec);
                    }
                }

                payload.Collections.Remove(oldKey);
                changed = true;
            }
        }

        if (changed)
        {
            await store.SaveAsync(payload);
        }
    }

    public static CustomWidgetStructuralDiff ComputeStructuralDiff(
        CustomWidgetManifest? previous,
        CustomWidgetManifest current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            var initialNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNodeTypes(current.View, initialNodes);
            return new CustomWidgetStructuralDiff(
                AddedNodeTypes: initialNodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                RemovedNodeTypes: [],
                AddedCollections: current.Collections.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                RemovedCollections: [],
                AddedStateKeys: current.State.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                RemovedStateKeys: [],
                DroppedLocales: [],
                OldSize: null,
                NewSize: current.DefaultSize);
        }

        var oldNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNodeTypes(previous.View, oldNodes);
        CollectNodeTypes(current.View, newNodes);

        List<string> addedNodes = newNodes.Except(oldNodes, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        List<string> removedNodes = oldNodes.Except(newNodes, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        List<string> addedCols = current.Collections.Keys.Except(previous.Collections.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();
        List<string> removedCols = previous.Collections.Keys.Except(current.Collections.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();

        List<string> addedState = current.State.Keys.Except(previous.State.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();
        List<string> removedState = previous.State.Keys.Except(current.State.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();

        List<string> droppedLocales = previous.I18n.Keys
            .Where(loc => !current.I18n.ContainsKey(loc))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CustomWidgetStructuralDiff(
            AddedNodeTypes: addedNodes,
            RemovedNodeTypes: removedNodes,
            AddedCollections: addedCols,
            RemovedCollections: removedCols,
            AddedStateKeys: addedState,
            RemovedStateKeys: removedState,
            DroppedLocales: droppedLocales,
            OldSize: previous.DefaultSize,
            NewSize: current.DefaultSize);
    }

    public static IReadOnlyList<string> FormatDiffPills(
        CustomWidgetStructuralDiff diff,
        bool isChinese)
    {
        var pills = new List<string>(4);
        if (diff.AddedNodeTypes.Count > 0)
        {
            string joined = string.Join(", ", diff.AddedNodeTypes.Take(3));
            pills.Add($"+{joined}");
        }

        if (diff.AddedCollections.Count > 0)
        {
            pills.Add(isChinese
                ? $"集合 +{string.Join(",", diff.AddedCollections)}"
                : $"col +{string.Join(",", diff.AddedCollections)}");
        }

        if (diff.SizeChanged && diff.NewSize is not null)
        {
            pills.Add($"{diff.NewSize.Width}×{diff.NewSize.Height}");
        }

        if (diff.DroppedLocales.Count > 0)
        {
            pills.Add(isChinese
                ? $"⚠ 缺少多语言 {string.Join(",", diff.DroppedLocales)}"
                : $"⚠ Missing i18n {string.Join(",", diff.DroppedLocales)}");
        }

        if (diff.RemovedCollections.Count > 0)
        {
            pills.Add(isChinese
                ? $"⚠ 移除集合 {string.Join(",", diff.RemovedCollections)}"
                : $"⚠ Removed col {string.Join(",", diff.RemovedCollections)}");
        }

        return pills;
    }

    public static CustomWidgetHealthReport AnalyzeHealth(
        string rawManifestJson,
        CustomWidgetManifest manifest,
        CustomWidgetStorePayload? storePayload,
        double measuredWidth = 0,
        double measuredHeight = 0,
        bool isChinese = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<CustomWidgetHealthIssue>();

        int targetW = manifest.DefaultSize.Width;
        int targetH = manifest.DefaultSize.Height;
        // Account for ~38px widget title chrome inside frame
        double availableBodyHeight = Math.Max(100, targetH - 38);
        int verticalOverflow = 0;

        bool hasRootScrollableList = HasRootFlexibleListOrScroll(manifest.View);
        if (measuredHeight > 0 && !hasRootScrollableList && measuredHeight > availableBodyHeight + 24)
        {
            verticalOverflow = (int)Math.Round(measuredHeight - availableBodyHeight);
            issues.Add(new CustomWidgetHealthIssue(
                Code: "layout.vertical_overflow",
                Message: isChinese
                    ? $"内容高度超出窗口约 {verticalOverflow}px，部分控件可能被截断"
                    : $"Content exceeds widget height by ~{verticalOverflow}px",
                SuggestedPrompt: isChinese
                    ? $"当前内容纵向溢出了约 {verticalOverflow}px，请精简垂直间距、把次要区域收纳进 Expander 或适当调高 defaultSize.height"
                    : $"Content overflows vertically by ~{verticalOverflow}px; tighten vertical spacing, tuck secondary sections into an Expander, or increase defaultSize.height"));
        }

        if (TryDetectDuplicateInnerHeader(manifest, isChinese, out CustomWidgetHealthIssue? dupHeaderIssue) &&
            dupHeaderIssue is not null)
        {
            issues.Add(dupHeaderIssue);
        }

        int maxCardDepth = ComputeMaxCardNestingDepth(manifest.View, 0);
        if (maxCardDepth >= 3)
        {
            issues.Add(new CustomWidgetHealthIssue(
                Code: "visual.card_nesting",
                Message: isChinese
                    ? $"检测到 {maxCardDepth} 层 Card 边框多层嵌套，紧凑桌面布局下会压缩有效内容区"
                    : $"Detected {maxCardDepth} nested Card borders, crowding compact desktop layout",
                SuggestedPrompt: isChinese
                    ? "当前存在多层 Card 边框嵌套，请移除多余的内层 Card，改用轻量 Stack 间距或 Divider 分隔"
                    : "Flatten nested Card borders and use clean Stack spacing or subtle Dividers instead"));
        }

        var hardcodedHex = new List<string>();
        CollectHardcodedContrastHazardColors(manifest.View, hardcodedHex);
        if (hardcodedHex.Count > 0)
        {
            string sample = hardcodedHex[0];
            issues.Add(new CustomWidgetHealthIssue(
                Code: "theme.hardcoded_hex",
                Message: isChinese
                    ? $"使用了写死色值 ({sample})，切换亮/暗色主题时可能看不清"
                    : $"Hardcoded color ({sample}) may lose contrast across Light/Dark themes",
                SuggestedPrompt: isChinese
                    ? $"请把写死的颜色 ({sample}) 替换为语义色（default / secondary / accent / success / warning / danger）以兼容亮暗双主题"
                    : $"Replace hardcoded hex color ({sample}) with semantic theme colors (default / secondary / accent / success / warning / danger)"));
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

            var incompleteLocales = manifest.I18n
                .Where(kvp => kvp.Value.Count < allKeys.Count)
                .Select(kvp => kvp.Key)
                .ToList();
            if (incompleteLocales.Count > 0)
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "i18n.incomplete",
                    Message: isChinese
                        ? $"多语言词条不齐（{string.Join(", ", incompleteLocales)} 缺少部分翻译键）"
                        : $"Incomplete i18n keys in: {string.Join(", ", incompleteLocales)}",
                    SuggestedPrompt: isChinese
                        ? $"请补齐 i18n 中 {string.Join(", ", incompleteLocales)} 缺失的翻译键，确保所有语种键集合一致"
                        : $"Complete missing i18n translation keys for {string.Join(", ", incompleteLocales)}"));
            }
        }

        foreach (CustomWidgetHealthIssue deadIssue in DetectDeadControlsAndUnreadMutations(manifest, isChinese).Take(2))
        {
            issues.Add(deadIssue);
        }

        if (!string.IsNullOrWhiteSpace(rawManifestJson))
        {
            CustomWidgetParseResult hintCheck = CustomWidgetManifestParser.ValidateWithRuntimeHints(rawManifestJson);
            foreach (CustomWidgetValidationDiagnostic diag in hintCheck.Diagnostics.Take(2))
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "manifest.hint",
                    Message: diag.Message,
                    SuggestedPrompt: isChinese
                        ? $"请修复校验提示：{diag.Path} — {diag.Message}"
                        : $"Fix validation diagnostic at {diag.Path}: {diag.Message}"));
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
                if (!EphemeralRuntimeStateKeys.Contains(k))
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
                runtimeNotes.Add($"sourceError=\"{srcErr}\"");
            }
            foreach ((string sk, string sv) in storePayload.State)
            {
                if (sk.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(sv))
                {
                    runtimeNotes.Add($"{sk}=\"{sv}\"");
                }
            }
            if (storePayload.State.TryGetValue("aiError", out string? aiErr) &&
                !string.IsNullOrWhiteSpace(aiErr))
            {
                runtimeNotes.Add($"aiError=\"{aiErr}\"");
            }

            if (runtimeNotes.Count > 0)
            {
                sb.Append(" Runtime diagnostics: ").Append(string.Join(", ", runtimeNotes)).Append('.');
            }
        }

        return sb.ToString().Trim();
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

    private static bool HasRootFlexibleListOrScroll(CustomWidgetNodeSpec root)
    {
        if (string.Equals(root.Type, "Scroll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "List", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(root.Type, "Stack", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase))
        {
            return root.Children.Any(c =>
                string.Equals(c.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "Scroll", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool TryDetectDuplicateInnerHeader(
        CustomWidgetManifest manifest,
        bool isChinese,
        out CustomWidgetHealthIssue? issue)
    {
        issue = null;
        string normalizedWidgetTitle = NormalizeHeaderComparableText(manifest.ResolveLocalizedText(manifest.Name));
        if (string.IsNullOrWhiteSpace(normalizedWidgetTitle))
        {
            normalizedWidgetTitle = NormalizeHeaderComparableText(manifest.Name);
        }

        if (string.IsNullOrWhiteSpace(normalizedWidgetTitle))
        {
            return false;
        }

        CustomWidgetNodeSpec root = manifest.View;
        if (root.Children.Count == 0)
        {
            return false;
        }

        CustomWidgetNodeSpec first = root.Children[0];
        string? candidateRaw = null;

        if (string.Equals(first.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(first.Style, "title", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "subtitle", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "bodyStrong", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "metric", StringComparison.OrdinalIgnoreCase)))
        {
            candidateRaw = first.Text ?? first.Title;
        }
        else if ((string.Equals(first.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(first.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(first.Type, "Card", StringComparison.OrdinalIgnoreCase)) &&
                 first.Children.Count > 0)
        {
            CustomWidgetNodeSpec? firstText = first.Children.FirstOrDefault(static c =>
                string.Equals(c.Type, "Text", StringComparison.OrdinalIgnoreCase));
            if (firstText is not null)
            {
                candidateRaw = firstText.Text ?? firstText.Title;
            }
        }

        if (string.IsNullOrWhiteSpace(candidateRaw))
        {
            return false;
        }

        string resolvedCandidate = manifest.ResolveLocalizedText(candidateRaw);
        string normalizedCandidate = NormalizeHeaderComparableText(resolvedCandidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) ||
            !string.Equals(normalizedCandidate, normalizedWidgetTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        issue = new CustomWidgetHealthIssue(
            Code: "layout.duplicate_header",
            Message: isChinese
                ? "内容区顶部重复放置了与组件同名的标题栏（宿主外框已自带图标与标题栏）"
                : "Top of view duplicates the widget title bar already provided by the host frame",
            SuggestedPrompt: isChinese
                ? "宿主外框顶部已自带组件图标与标题栏，请移除 view 顶部重复的组件标题栏，把操作按钮精简并入首个功能区块"
                : "Remove the duplicate widget title header at the top of view since the host frame already renders the title bar");
        return true;
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

    private static void CollectHardcodedContrastHazardColors(CustomWidgetNodeSpec? node, List<string> results)
    {
        if (node is null)
        {
            return;
        }

        CheckHexHazard(node.Color, results);
        CheckHexHazard(node.Tone, results);

        CollectHardcodedContrastHazardColors(node.ItemTemplate, results);
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectHardcodedContrastHazardColors(c, results);
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

    private static void CollectNodeTypes(CustomWidgetNodeSpec? node, HashSet<string> types)
    {
        if (node is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Type))
        {
            types.Add(node.Type);
        }

        CollectNodeTypes(node.ItemTemplate, types);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectNodeTypes(h, types);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectNodeTypes(c, types);
        }
    }

    private static void CollectReferencedCollectionNames(CustomWidgetNodeSpec? node, HashSet<string> collections)
    {
        if (node is null)
        {
            return;
        }

        string? rawCol = node.Collection ?? node.Items;
        if (!string.IsNullOrWhiteSpace(rawCol) && !rawCol.StartsWith("${", StringComparison.Ordinal))
        {
            string clean = rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                ? rawCol["collections.".Length..]
                : rawCol;
            if (!string.IsNullOrWhiteSpace(clean))
            {
                collections.Add(clean);
            }
        }

        CollectActionCollections(node.OnClick, collections);
        CollectActionCollections(node.OnSubmit, collections);
        CollectActionCollections(node.OnChange, collections);
        CollectReferencedCollectionNames(node.ItemTemplate, collections);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectReferencedCollectionNames(h, collections);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectReferencedCollectionNames(c, collections);
        }
    }

    private static void CollectActionCollections(IReadOnlyList<CustomWidgetActionSpec>? actions, HashSet<string> collections)
    {
        if (actions is null)
        {
            return;
        }

        foreach (CustomWidgetActionSpec act in actions)
        {
            if (!string.IsNullOrWhiteSpace(act.Collection))
            {
                collections.Add(act.Collection);
            }
            CollectActionCollections(act.Then, collections);
        }
    }

    private static void CollectBoundOrWrittenStateKeys(CustomWidgetManifest manifest, HashSet<string> keys)
    {
        void VisitActions(IReadOnlyList<CustomWidgetActionSpec>? actions)
        {
            if (actions is null)
            {
                return;
            }

            foreach (CustomWidgetActionSpec act in actions)
            {
                if (!string.IsNullOrWhiteSpace(act.Key))
                {
                    string k = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? act.Key["state.".Length..]
                        : act.Key;
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        keys.Add(k);
                    }
                }
                if (act.StateMap is not null)
                {
                    foreach (string sk in act.StateMap.Keys)
                    {
                        string k = sk.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                            ? sk["state.".Length..]
                            : sk;
                        if (!string.IsNullOrWhiteSpace(k))
                        {
                            keys.Add(k);
                        }
                    }
                }
                VisitActions(act.Then);
            }
        }

        void VisitNode(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(node.Bind) &&
                !node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
            {
                string k = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? node.Bind["state.".Length..]
                    : node.Bind;
                if (!string.IsNullOrWhiteSpace(k))
                {
                    keys.Add(k);
                }
            }

            VisitActions(node.OnClick);
            VisitActions(node.OnSubmit);
            VisitActions(node.OnChange);
            VisitActions(node.OnDropActions);
            VisitNode(node.ItemTemplate);
            foreach (CustomWidgetNodeSpec h in node.HoverActions)
            {
                VisitNode(h);
            }
            foreach (CustomWidgetNodeSpec c in node.Children)
            {
                VisitNode(c);
            }
        }

        VisitNode(manifest.View);
        VisitActions(manifest.OnDrop?.Actions);
    }

    private static readonly Regex StateTokenReadRegex = new(
        @"\bstate\.([A-Za-z0-9_]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Detects non-functional "facade" controls (buttons/tiles without actions, or actions mutating
    /// dummy state keys that no UI node, source, or follow-up action ever reads).
    /// </summary>
    public static IReadOnlyList<CustomWidgetHealthIssue> DetectDeadControlsAndUnreadMutations(
        CustomWidgetManifest manifest,
        bool isChinese = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<CustomWidgetHealthIssue>();
        var readOrBoundKeys = new HashSet<string>(StringComparer.Ordinal);
        var mutatedKeys = new List<(string Key, string Path)>();

        void RecordExprReads(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            foreach (Match m in StateTokenReadRegex.Matches(expr))
            {
                readOrBoundKeys.Add(m.Groups[1].Value);
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
                RecordExprReads(act.Value);
                RecordExprReads(act.TargetId);
                RecordExprReads(act.Text);
                RecordExprReads(act.Message);
                RecordExprReads(act.Url);
                RecordExprReads(act.Path);
                RecordExprReads(act.Guard);
                RecordExprReads(act.Prompt);
                if (act.ItemFields is not null)
                {
                    foreach (string v in act.ItemFields.Values)
                    {
                        RecordExprReads(v);
                    }
                }

                if ((string.Equals(act.Type, "state.set", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "state.toggle", StringComparison.OrdinalIgnoreCase)) &&
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

                ScanActions(act.Then, $"{actPath}.then");
            }
        }

        void ScanNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null)
            {
                return;
            }

            if ((string.Equals(node.Type, "Button", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "IconButton", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "ControlTile", StringComparison.OrdinalIgnoreCase)) &&
                node.OnClick.Count == 0)
            {
                string label = node.Title ?? node.Text ?? node.Tooltip ?? node.Type;
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "interaction.dead_button",
                    Message: isChinese
                        ? $"按钮「{label}」未绑定任何 onClick 动作，点击不会产生效果"
                        : $"'{label}' ({node.Type}) has no onClick actions",
                    SuggestedPrompt: isChinese
                        ? $"请为按钮「{label}」({path}) 补齐真实的 onClick 动作（或改用非按钮展示组件，不要留空壳按钮）"
                        : $"Wire real onClick actions to '{label}' ({path}) or replace the dead button"));
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

            RecordExprReads(node.When);
            RecordExprReads(node.Text);
            RecordExprReads(node.Title);
            RecordExprReads(node.Subtitle);
            RecordExprReads(node.Value);
            RecordExprReads(node.Icon);
            RecordExprReads(node.Src);
            RecordExprReads(node.Color);
            RecordExprReads(node.Tone);
            RecordExprReads(node.Progress);
            RecordExprReads(node.Indeterminate);
            RecordExprReads(node.Filter);
            RecordExprReads(node.FilterEquals);
            RecordExprReads(node.Collection);
            RecordExprReads(node.Items);
            RecordExprReads(node.Sparkline);
            RecordExprReads(node.Unit);
            RecordExprReads(node.FileName);
            RecordExprReads(node.FilePath);

            ScanActions(node.OnClick, $"{path}.onClick");
            ScanActions(node.OnSubmit, $"{path}.onSubmit");
            ScanActions(node.OnChange, $"{path}.onChange");
            ScanActions(node.OnDropActions, $"{path}.onDrop");

            ScanNode(node.ItemTemplate, $"{path}.itemTemplate");
            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                ScanNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                ScanNode(node.Children[i], $"{path}.children[{i}]");
            }
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            RecordExprReads(src.Url);
            RecordExprReads(src.City);
            if (src.Headers is not null)
            {
                foreach (string hv in src.Headers.Values)
                {
                    RecordExprReads(hv);
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
        }

        ScanNode(manifest.View, "$.view");
        ScanActions(manifest.OnDrop?.Actions, "$.onDrop.actions");

        var reportedUnread = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, string actPath) in mutatedKeys)
        {
            if (!readOrBoundKeys.Contains(key) && reportedUnread.Add(key))
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "interaction.unread_state",
                    Message: isChinese
                        ? $"检测到假交互：动作修改了 state.{key}，但没有任何界面或数据源读取它"
                        : $"Facade action mutates 'state.{key}' ({actPath}), which is never read anywhere",
                    SuggestedPrompt: isChinese
                        ? $"动作 {actPath} 修改了 state.{key} 但界面并未读取它，请改用真实的系统动作（如 system.control / file.transform / url.open）或将该状态绑定到界面逻辑"
                        : $"Action at {actPath} mutates unread 'state.{key}'; wire it to a real native action (system.control / file.transform / url.open) or bind UI to state.{key}"));
            }
        }

        return issues;
    }
}
