using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

/// <summary>
/// Pure helper operations for inspecting Custom Widget manifests, node paths,
/// workspace files, template categories, and error/stage diagnostics in Studio.
/// </summary>
internal static class StudioManifestInspector
{
    internal static bool IsBuiltInOrCatalogTemplateId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        (CustomWidgetTemplates.MatchPresetOrNull(id) is not null ||
         id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase));

    internal static bool TryExtractPureI18nKey(string? raw, out string i18nKey)
    {
        i18nKey = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (!trimmed.StartsWith("${", StringComparison.Ordinal) || !trimmed.EndsWith('}'))
        {
            return false;
        }

        string inner = trimmed[2..^1].Trim();
        if (!inner.StartsWith("$i18n.", StringComparison.Ordinal) || inner.Length <= "$i18n.".Length)
        {
            return false;
        }

        string candidate = inner["$i18n.".Length..].Trim();
        foreach (char c in candidate)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-'))
            {
                return false;
            }
        }

        i18nKey = candidate;
        return i18nKey.Length > 0;
    }

    internal static bool TryGetParentNodePath(JsonObject rootObj, string? nodePath, out string parentPath)
    {
        parentPath = string.Empty;
        if (rootObj is null || string.IsNullOrWhiteSpace(nodePath))
        {
            return false;
        }

        string trimmed = nodePath.Trim();
        if (trimmed.StartsWith('#') && trimmed.Length > 1)
        {
            string targetId = trimmed[1..].Trim();
            if (targetId.Length == 0 || rootObj["elements"] is not JsonObject elementsObj)
            {
                return false;
            }

            foreach ((string candidateId, JsonNode? elemNode) in elementsObj)
            {
                if (elemNode is not JsonObject elemObj ||
                    string.Equals(candidateId, targetId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ElementDirectlyReferencesChild(elemObj, targetId))
                {
                    parentPath = "#" + candidateId;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool ElementDirectlyReferencesChild(JsonObject elemObj, string childId)
    {
        foreach (string singleSlot in new[] { "child", "itemTemplate", "pane", "actionButton" })
        {
            if (elemObj[singleSlot] is JsonValue sv &&
                sv.TryGetValue(out string? refId) &&
                string.Equals(refId?.Trim(), childId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (string arraySlot in new[] { "children", "hoverActions" })
        {
            if (elemObj[arraySlot] is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue av &&
                        av.TryGetValue(out string? refId) &&
                        string.Equals(refId?.Trim(), childId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static bool TryResolveJsonNodeByPath(JsonObject rootObj, string? nodePath, out JsonObject? targetObj)
    {
        targetObj = null;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return false;
        }

        string path = nodePath.Trim();
        if (path.StartsWith('#') && path.Length > 1)
        {
            string elemId = path[1..].Trim();
            if (elemId.Length > 0 &&
                rootObj["elements"] is JsonObject elementsObj &&
                elementsObj.TryGetPropertyValue(elemId, out JsonNode? elemNode) &&
                elemNode is JsonObject elemObj)
            {
                targetObj = elemObj;
                return true;
            }

            return false;
        }

        if (!path.StartsWith("$.", StringComparison.Ordinal))
        {
            return false;
        }

        JsonNode? cur = rootObj;
        int i = 2;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                continue;
            }

            if (path[i] == '[')
            {
                int close = path.IndexOf(']', i + 1);
                if (close < 0 || cur is not JsonArray arr)
                {
                    return false;
                }

                if (!int.TryParse(path.AsSpan(i + 1, close - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) ||
                    idx < 0 ||
                    idx >= arr.Count)
                {
                    return false;
                }

                cur = arr[idx];
                i = close + 1;
                continue;
            }

            int nextDelim = path.IndexOfAny(['.', '['], i);
            string prop = nextDelim < 0 ? path[i..] : path[i..nextDelim];
            if (cur is not JsonObject obj || !obj.TryGetPropertyValue(prop, out cur))
            {
                return false;
            }

            i = nextDelim < 0 ? path.Length : nextDelim;
        }

        targetObj = cur as JsonObject;
        return targetObj is not null;
    }

    internal static string? InspectNodeScriptSummary(string? manifestJson, string? nodePath)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        try
        {
            CustomWidgetManifest? manifest = CustomWidgetManifestParser.Parse(manifestJson).Manifest;
            if (manifest is null)
            {
                return null;
            }

            CustomWidgetNodeSpec? found = FindManifestNodeByPath(manifest, nodePath);
            if (found is null)
            {
                return null;
            }

            var fns = new List<string>();
            void CollectFns(IReadOnlyList<CustomWidgetActionSpec> actions)
            {
                foreach (CustomWidgetActionSpec a in actions)
                {
                    if (string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(a.Fn) &&
                        !fns.Contains(a.Fn.Trim(), StringComparer.Ordinal))
                    {
                        fns.Add(a.Fn.Trim());
                    }
                    if (a.Then is { Count: > 0 })
                    {
                        CollectFns(a.Then);
                    }
                }
            }

            CollectFns(found.OnClick);
            CollectFns(found.OnSubmit);
            CollectFns(found.OnChange);

            var computedKeys = new List<string>();
            void CollectComputed(string? expr)
            {
                if (string.IsNullOrWhiteSpace(expr))
                {
                    return;
                }
                int idx = expr.IndexOf("$computed.", StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int start = idx + "$computed.".Length;
                    int end = start;
                    while (end < expr.Length && (char.IsLetterOrDigit(expr[end]) || expr[end] == '_'))
                    {
                        end++;
                    }
                    if (end > start)
                    {
                        string key = expr[start..end];
                        if (!computedKeys.Contains(key, StringComparer.Ordinal))
                        {
                            computedKeys.Add(key);
                        }
                    }
                    idx = expr.IndexOf("$computed.", end, StringComparison.Ordinal);
                }
            }

            CollectComputed(found.Text);
            CollectComputed(found.Title);
            CollectComputed(found.Subtitle);
            CollectComputed(found.Value);
            CollectComputed(found.Items);
            CollectComputed(found.Collection);
            CollectComputed(found.Sparkline);
            CollectComputed(found.Columns);
            CollectComputed(found.Rows);

            var parts = new List<string>(2);
            if (fns.Count > 0)
            {
                parts.Add("⚡ " + string.Join(", ", fns));
            }
            if (computedKeys.Count > 0)
            {
                parts.Add("$computed." + string.Join(", $computed.", computedKeys.Take(2)));
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static CustomWidgetNodeSpec? FindManifestNodeByPath(CustomWidgetManifest? manifest, string? targetPath)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        string trimmed = targetPath.Trim();
        CustomWidgetNodeSpec? matched = null;
        _ = FindMatchingNodePath(
            manifest,
            node =>
            {
                if (string.Equals(node.NodePath, trimmed, StringComparison.Ordinal))
                {
                    matched = node;
                    return true;
                }

                return false;
            });
        return matched;
    }

    internal static string? FindMatchingNodePath(
        CustomWidgetManifest? manifest,
        Predicate<CustomWidgetNodeSpec> predicate)
    {
        if (manifest is null || predicate is null)
        {
            return null;
        }

        if (FindMatchingNodePath(manifest.View, "$.view", predicate) is { } viewMatch)
        {
            return viewMatch;
        }

        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popFallback = !string.IsNullOrWhiteSpace(pop.NodePath)
                ? pop.NodePath
                : (!string.IsNullOrWhiteSpace(pop.PopoverId) ? $"$.popovers.{pop.PopoverId}" : $"$.popovers[{i}]");
            if (FindMatchingNodePath(pop, popFallback, predicate) is { } popMatch)
            {
                return popMatch;
            }
        }

        return null;
    }

    internal static string? FindMatchingNodePath(
        CustomWidgetNodeSpec? node,
        string fallbackPath,
        Predicate<CustomWidgetNodeSpec> predicate)
    {
        if (node is null || predicate is null)
        {
            return null;
        }

        string currentPath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : fallbackPath;
        if (predicate(node))
        {
            return currentPath;
        }

        if (node.ItemTemplate is not null &&
            FindMatchingNodePath(node.ItemTemplate, $"{currentPath}.itemTemplate", predicate) is { } tplMatch)
        {
            return tplMatch;
        }

        if (node.Pane is not null &&
            FindMatchingNodePath(node.Pane, $"{currentPath}.pane", predicate) is { } paneMatch)
        {
            return paneMatch;
        }

        if (node.ActionButton is not null &&
            FindMatchingNodePath(node.ActionButton, $"{currentPath}.actionButton", predicate) is { } actionBtnMatch)
        {
            return actionBtnMatch;
        }

        for (int i = 0; i < node.HoverActions.Count; i++)
        {
            if (FindMatchingNodePath(node.HoverActions[i], $"{currentPath}.hoverActions[{i}]", predicate) is { } hoverMatch)
            {
                return hoverMatch;
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (FindMatchingNodePath(node.Children[i], $"{currentPath}.children[{i}]", predicate) is { } childMatch)
            {
                return childMatch;
            }
        }

        return null;
    }

    internal static string? FormatTargetNodePromptContext(string? manifestJson, string? nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        string? scriptSummary = InspectNodeScriptSummary(manifestJson, nodePath);
        return string.IsNullOrWhiteSpace(scriptSummary)
            ? nodePath
            : $"{nodePath} (linked script bindings: {scriptSummary})";
    }

    internal static string? FormatPickedNodeDisplayLabel(
        CustomWidgetPickedNode? picked,
        LocalizationService loc,
        string? manifestJson = null)
    {
        if (picked is null)
        {
            return null;
        }

        string baseLabel = CustomWidgetNativeRenderer.FormatPickedNodeTag(picked.NodeType, picked.DisplayLabel, loc);
        string? scriptSummary = InspectNodeScriptSummary(manifestJson, picked.NodePath);
        return string.IsNullOrWhiteSpace(scriptSummary)
            ? baseLabel
            : $"{baseLabel} ({scriptSummary})";
    }

    internal static string ClassifyDirectErrorMessageKey(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail))
        {
            return "CustomWidget.Workshop.Direct.Failed";
        }

        string lower = errorDetail.ToLowerInvariant();
        if (lower.Contains("401", StringComparison.Ordinal) ||
            lower.Contains("403", StringComparison.Ordinal) ||
            lower.Contains("unauthorized", StringComparison.Ordinal) ||
            lower.Contains("invalid_api_key", StringComparison.Ordinal) ||
            lower.Contains("invalid api key", StringComparison.Ordinal) ||
            lower.Contains("api key", StringComparison.Ordinal) ||
            lower.Contains("authentication", StringComparison.Ordinal) ||
            lower.Contains("密钥", StringComparison.Ordinal))
        {
            return "CustomWidget.Workshop.Direct.FailedAuth";
        }

        if (lower.Contains("429", StringComparison.Ordinal) ||
            lower.Contains("rate limit", StringComparison.Ordinal) ||
            lower.Contains("too many requests", StringComparison.Ordinal) ||
            lower.Contains("insufficient_quota", StringComparison.Ordinal) ||
            lower.Contains("insufficient_balance", StringComparison.Ordinal) ||
            lower.Contains("quota", StringComparison.Ordinal) ||
            lower.Contains("balance", StringComparison.Ordinal) ||
            lower.Contains("billing", StringComparison.Ordinal) ||
            lower.Contains("配额", StringComparison.Ordinal) ||
            lower.Contains("余额", StringComparison.Ordinal))
        {
            return "CustomWidget.Workshop.Direct.FailedQuota";
        }

        if (lower.Contains("timeout", StringComparison.Ordinal) ||
            lower.Contains("timed out", StringComparison.Ordinal) ||
            lower.Contains("taskcanceled", StringComparison.Ordinal) ||
            lower.Contains("connection", StringComparison.Ordinal) ||
            lower.Contains("network", StringComparison.Ordinal) ||
            lower.Contains("socket", StringComparison.Ordinal) ||
            lower.Contains("dns", StringComparison.Ordinal) ||
            lower.Contains("ssl", StringComparison.Ordinal) ||
            lower.Contains("unreachable", StringComparison.Ordinal) ||
            lower.Contains("超时", StringComparison.Ordinal) ||
            lower.Contains("网络", StringComparison.Ordinal))
        {
            return "CustomWidget.Workshop.Direct.FailedNetwork";
        }

        return "CustomWidget.Workshop.Direct.Failed";
    }

    internal static (bool Ok, bool Committed, string? WidgetId, string Error) ParseAgentStageResponse(string response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out JsonElement okEl) &&
                      okEl.ValueKind == JsonValueKind.True;
            bool committed = false;
            string? widgetId = null;
            if (ok &&
                root.TryGetProperty("result", out JsonElement resultEl) &&
                resultEl.ValueKind == JsonValueKind.Object)
            {
                committed = resultEl.TryGetProperty("committed", out JsonElement committedEl) &&
                            committedEl.ValueKind == JsonValueKind.True;
                if (resultEl.TryGetProperty("widgetId", out JsonElement widEl) &&
                    widEl.ValueKind == JsonValueKind.String)
                {
                    widgetId = widEl.GetString();
                }
            }
            string error = ok
                ? string.Empty
                : (root.TryGetProperty("error", out JsonElement errorEl) &&
                   errorEl.ValueKind == JsonValueKind.String
                    ? errorEl.GetString() ?? "Unknown error."
                    : "Unknown error.");
            return (ok, committed, widgetId, error);
        }
        catch (Exception ex)
        {
            return (false, false, null, ex.Message);
        }
    }

    internal static string? TryReadWidgetManifest(string widgetId, Action<string>? log = null)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            return File.Exists(store.ManifestPath)
                ? File.ReadAllText(store.ManifestPath, Encoding.UTF8)
                : null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CustomWidgetStudio] Read manifest failed: {ex.Message}");
            return null;
        }
    }

    internal static string? TryReadWidgetLogicJs(string widgetId, Action<string>? log = null)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            if (!File.Exists(store.LogicScriptPath))
            {
                return null;
            }

            string text = File.ReadAllText(store.LogicScriptPath, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CustomWidgetStudio] Read logic.js failed: {ex.Message}");
            return null;
        }
    }

    internal static CustomWidgetLocaleCatalog? TryReadWidgetLocales(string widgetId, Action<string>? log = null)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            CustomWidgetLocaleCatalog catalog = store.ReadLocaleCatalog();
            return catalog.HasAnyLocale ? catalog : null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[CustomWidgetStudio] Read locales failed: {ex.Message}");
            return null;
        }
    }

    internal static TemplateGalleryCategory InferRemoteTemplateCategory(string id, string? label)
    {
        string haystack = $"{id} {label}".ToLowerInvariant();
        if (haystack.Contains("ai", StringComparison.Ordinal) ||
            haystack.Contains("agent", StringComparison.Ordinal) ||
            haystack.Contains("llm", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Ai;
        }
        if (haystack.Contains("rss", StringComparison.Ordinal) ||
            haystack.Contains("news", StringComparison.Ordinal) ||
            haystack.Contains("monitor", StringComparison.Ordinal) ||
            haystack.Contains("weather", StringComparison.Ordinal) ||
            haystack.Contains("stock", StringComparison.Ordinal) ||
            haystack.Contains("hardware", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Live;
        }
        if (haystack.Contains("habit", StringComparison.Ordinal) ||
            haystack.Contains("water", StringComparison.Ordinal) ||
            haystack.Contains("check", StringComparison.Ordinal) ||
            haystack.Contains("todo", StringComparison.Ordinal) ||
            haystack.Contains("pomodoro", StringComparison.Ordinal))
        {
            return TemplateGalleryCategory.Habits;
        }

        return TemplateGalleryCategory.Tools;
    }

    internal static string StripTrailingCapabilitySuffix(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return rawTitle;
        }

        int openIdx = rawTitle.LastIndexOf(" (", StringComparison.Ordinal);
        if (openIdx > 0 && rawTitle.EndsWith(')'))
        {
            return rawTitle[..openIdx].Trim();
        }

        return rawTitle;
    }

    internal static string FormatCleanChannelLabel(string providerDisplayName, string shortModel)
    {
        if (string.IsNullOrWhiteSpace(providerDisplayName))
        {
            return shortModel;
        }

        string cleanProvider = providerDisplayName.Replace(" ", string.Empty, StringComparison.Ordinal);
        string cleanModel = shortModel.Replace("-", string.Empty, StringComparison.Ordinal)
                                      .Replace("_", string.Empty, StringComparison.Ordinal);
        if (cleanProvider.Length >= 3 &&
            cleanModel.StartsWith(cleanProvider, StringComparison.OrdinalIgnoreCase))
        {
            return shortModel;
        }

        return $"{providerDisplayName} · {shortModel}";
    }

    internal static string TrimLeadingVersionPrefix(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return "1.0.0";
        }

        string trimmed = rawVersion.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1]))
        {
            return trimmed[1..];
        }

        return trimmed;
    }

    internal static string TruncateTextElements(string? text, int maxTextElements)
    {
        if (string.IsNullOrEmpty(text) || maxTextElements <= 0)
        {
            return string.Empty;
        }

        var info = new StringInfo(text);
        if (info.LengthInTextElements <= maxTextElements)
        {
            return text;
        }

        return info.SubstringByTextElements(0, maxTextElements) + "…";
    }

    internal static string ResolveFallbackEmojiForManifest(CustomWidgetManifest? manifest)
    {
        if (manifest is not null)
        {
            if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
            {
                return manifest.Icon.Trim();
            }

            if (CustomWidgetTemplates.TryNormalizeKnownTemplateId(manifest.Id) is { } templateId)
            {
                return templateId switch
                {
                    CustomWidgetTemplates.SystemPulseTemplateId => "📊",
                    CustomWidgetTemplates.PomodoroTemplateId => "🍅",
                    CustomWidgetTemplates.CountdownTemplateId => "📅",
                    CustomWidgetTemplates.AiPulseTemplateId => "🧠",
                    CustomWidgetTemplates.WorldClockTemplateId => "🌍",
                    CustomWidgetTemplates.DevToolboxTemplateId => "🛠️",
                    CustomWidgetTemplates.AgentHudTemplateId => "🤖",
                    CustomWidgetTemplates.HydrationStarterId => "💧",
                    CustomWidgetTemplates.AiInboxStarterId => "⚡",
                    CustomWidgetTemplates.NewsRssStarterId => "📰",
                    CustomWidgetTemplates.FocusDialTemplateId => "🎧",
                    CustomWidgetTemplates.SnippetGridTemplateId => "⌨️",
                    CustomWidgetTemplates.WeatherDeskTemplateId => "🌤️",
                    CustomWidgetTemplates.TodoAgendaTemplateId => "✅",
                    _ => "\u2728"
                };
            }
        }

        return "\u2728";
    }

    internal static string ExtractLatestNonEmptySingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = text.AsSpan();
        int end = span.Length;
        while (end > 0)
        {
            while (end > 0 && (span[end - 1] == '\r' || span[end - 1] == '\n'))
            {
                end--;
            }

            if (end <= 0)
            {
                break;
            }

            int lineStart = end;
            while (lineStart > 0 && span[lineStart - 1] != '\n' && span[lineStart - 1] != '\r')
            {
                lineStart--;
            }

            ReadOnlySpan<char> line = span[lineStart..end].Trim();
            while (line.Length > 0 && (line[0] == '#' || line[0] == '*' || line[0] == '-' || line[0] == '>' || line[0] == ' '))
            {
                line = line[1..];
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                if (line.Length > 160)
                {
                    return string.Concat(line[..160], "…");
                }

                return line.ToString();
            }

            end = lineStart;
        }

        return string.Empty;
    }
}
