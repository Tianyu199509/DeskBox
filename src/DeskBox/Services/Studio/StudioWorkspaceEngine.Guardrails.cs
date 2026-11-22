using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;


public sealed partial class StudioWorkspaceEngine
{
    private static bool NormalizeLayoutNodeInPlace(
        JsonObject node,
        string elemId,
        JsonObject elementsMap,
        string currentPath,
        bool isRootView,
        int cardAncestorDepth,
        bool directParentIsSingleChildCard,
        string? lockedCanonicalPath,
        JsonObject? baselineRoot,
        out string? effectiveNodeType,
        out bool isPlainCardShell)
    {
        bool changed = false;

        if (node.TryGetPropertyValue("spacing", out JsonNode? spacingNode) &&
            TryReadNumericJsonValue(spacingNode, out double sp) &&
            sp < 0)
        {
            node["spacing"] = JsonValue.Create(0);
            changed = true;
        }

        if (node.TryGetPropertyValue("padding", out JsonNode? paddingNode) &&
            TryReadNumericJsonValue(paddingNode, out double pad) &&
            pad < 0)
        {
            node["padding"] = JsonValue.Create(0);
            changed = true;
        }

        if (node.TryGetPropertyValue("background", out JsonNode? bgNode) && bgNode is null)
        {
            node.Remove("background");
            changed = true;
        }

        string? nodeType = node["type"] is JsonValue typeVal && typeVal.TryGetValue(out string? parsedType)
            ? parsedType
            : null;

        if (node["child"] is JsonValue singleChildVal &&
            singleChildVal.TryGetValue(out string? singleChildId) &&
            !string.IsNullOrWhiteSpace(singleChildId) &&
            (node["children"] is not JsonArray existingChildrenArr || existingChildrenArr.Count == 0))
        {
            node.Remove("child");
            node["children"] = new JsonArray((JsonNode?)JsonValue.Create(singleChildId.Trim()));
            changed = true;
        }

        changed |= NormalizeCommonDialectPropertiesInPlace(node, elemId, elementsMap, ref nodeType, isRootView);

        if (TryNormalizeInlineActionRowToGrid(node, elementsMap, ref nodeType))
        {
            changed = true;
        }

        if (string.Equals(nodeType, "Grid", StringComparison.OrdinalIgnoreCase))
        {
            changed |= NormalizeGridTrackPropertyInPlace(node, "columns", "cols", "columnCount", "columnsCount", "gridTemplateColumns");
            changed |= NormalizeGridTrackPropertyInPlace(node, "rows", "rowsCount", "gridTemplateRows");
            changed |= NormalizeOneBasedGridIndicesInPlace(node, elementsMap);
        }
        else if (string.Equals(nodeType, "Tabs", StringComparison.OrdinalIgnoreCase) &&
                 node["tabs"] is JsonArray tabsAlias)
        {
            if (node["children"] is not JsonArray existingTabChildren || existingTabChildren.Count == 0)
            {
                node.Remove("tabs");
                node["children"] = tabsAlias;
                changed = true;
            }
        }

        changed |= NormalizeHostDesignSystemNodeInPlace(
            node,
            nodeType,
            currentPath,
            isRootView,
            cardAncestorDepth,
            directParentIsSingleChildCard,
            lockedCanonicalPath,
            baselineRoot,
            out effectiveNodeType,
            out isPlainCardShell);

        if (node["children"] is JsonArray children)
        {
            // Canonical flat element graph: children holds element ID strings (never rewrite IDs into Text/Divider objects).
            for (int i = children.Count - 1; i >= 0; i--)
            {
                if (children[i] is null)
                {
                    children.RemoveAt(i);
                    changed = true;
                }
                else if (children[i] is JsonValue childScalar &&
                         childScalar.TryGetValue(out string? rawChildId) &&
                         string.IsNullOrWhiteSpace(rawChildId))
                {
                    children.RemoveAt(i);
                    changed = true;
                }
            }
        }

        foreach (string actionKey in (ReadOnlySpan<string>)["onClick", "onSubmit", "onChange", "onDrop", "onRowClick", "onItemClick", "onReorder", "onInlineCommit"])
        {
            if (node.TryGetPropertyValue(actionKey, out JsonNode? actNode) && actNode is not null)
            {
                changed |= NormalizeActionTreeInPlace(actNode);
            }
        }

        foreach (string menuKey in (ReadOnlySpan<string>)["menuItems", "contextMenu", "actions"])
        {
            if (node[menuKey] is JsonArray menuArr)
            {
                for (int i = 0; i < menuArr.Count; i++)
                {
                    if (menuArr[i] is JsonObject menuItemObj)
                    {
                        changed |= NormalizeUnicodeGlyphPropertyInPlace(menuItemObj, "icon");
                        changed |= NormalizeUnicodeGlyphPropertyInPlace(menuItemObj, "glyph");
                        if (menuItemObj.TryGetPropertyValue("onClick", out JsonNode? mAct) &&
                            mAct is not null)
                        {
                            changed |= NormalizeActionTreeInPlace(mAct);
                        }
                    }
                }
            }
        }

        return changed;
    }

    private static bool NormalizeHostDesignSystemNodeInPlace(
        JsonObject node,
        string? nodeType,
        string currentPath,
        bool isRootView,
        int cardAncestorDepth,
        bool directParentIsSingleChildCard,
        string? lockedCanonicalPath,
        JsonObject? baselineRoot,
        out string? effectiveNodeType,
        out bool isPlainCardShell)
    {
        bool changed = false;
        effectiveNodeType = nodeType;
        isPlainCardShell = false;

        bool isExempt = IsExemptMicroIndicatorOrVisual(node, nodeType);
        bool isCard = string.Equals(nodeType, "Card", StringComparison.OrdinalIgnoreCase);
        bool isLayoutContainer = isRootView || isCard || IsLayoutContainerTypeName(nodeType);

        if (!isExempt && isCard)
        {
            if (node.Remove("cornerRadius")) changed = true;
            if (node.Remove("radius")) changed = true;
        }

        if (!isExempt && isLayoutContainer)
        {
            if (node.Remove("linearGradient")) changed = true;
            if (node.Remove("radialGradient")) changed = true;
            if (node.Remove("gradient")) changed = true;

            foreach (string bgKey in new[] { "background", "backgroundColor", "bg" })
            {
                if (!node.TryGetPropertyValue(bgKey, out JsonNode? rawBgNode) || rawBgNode is null)
                {
                    continue;
                }

                if (rawBgNode is JsonObject)
                {
                    node.Remove(bgKey);
                    changed = true;
                    continue;
                }

                if (rawBgNode is JsonValue bgVal && bgVal.TryGetValue(out string? rawBgStr))
                {
                    string trimmedBg = rawBgStr.Trim();
                    if (trimmedBg.Contains("${", StringComparison.Ordinal) ||
                        string.Equals(trimmedBg, "transparent", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (isCard && !isRootView && TryParseStaticRgbColor(trimmedBg, out byte r, out byte g, out byte b))
                    {
                        int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                        if (chroma > 48)
                        {
                            string existingTone = node["tone"]?.GetValue<string?>()?.Trim() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(existingTone) ||
                                string.Equals(existingTone, "default", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(existingTone, "subtle", StringComparison.OrdinalIgnoreCase))
                            {
                                node["tone"] = JsonValue.Create(MapRgbHueToCardTone(r, g, b));
                            }
                        }
                    }

                    node.Remove(bgKey);
                    changed = true;
                }
            }
        }

        if (string.Equals(nodeType, "Text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeType, "Icon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeType, "StatCard", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string colorAlias in new[] { "textColor", "foreground", "fg" })
            {
                if (!node.TryGetPropertyValue(colorAlias, out JsonNode? aliasNode))
                {
                    continue;
                }

                if (aliasNode is JsonValue aliasVal &&
                    aliasVal.TryGetValue(out string? aliasStr) &&
                    !string.IsNullOrWhiteSpace(aliasStr))
                {
                    string existingColor = node["color"] is JsonValue curColVal && curColVal.TryGetValue(out string? cStr)
                        ? cStr.Trim()
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(existingColor) ||
                        string.Equals(existingColor, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        node["color"] = JsonValue.Create(aliasStr.Trim());
                    }
                }

                node.Remove(colorAlias);
                changed = true;
            }

            bool isFocusedSubtree = !string.IsNullOrEmpty(lockedCanonicalPath) &&
                                    IsSameOrDescendantPath(currentPath, lockedCanonicalPath);
            if (!isFocusedSubtree &&
                node["color"] is JsonValue colVal &&
                colVal.TryGetValue(out string? rawColor) &&
                TryMapNeutralTextColorToSemanticToken(rawColor, out string semanticTextToken))
            {
                bool preserveExplicitUserColorEdit = false;
                if (baselineRoot is not null &&
                    DeskBox.Services.Studio.StudioManifestInspector.TryResolveJsonNodeByPath(baselineRoot, currentPath, out JsonObject? baseNode) &&
                    baseNode is not null)
                {
                    string? baseRawColor = baseNode["color"]?.GetValue<string?>()?.Trim()
                                           ?? baseNode["textColor"]?.GetValue<string?>()?.Trim();
                    if (!string.Equals(baseRawColor, rawColor.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        string baseSemantic = string.IsNullOrWhiteSpace(baseRawColor)
                            ? string.Empty
                            : (TryMapNeutralTextColorToSemanticToken(baseRawColor, out string mappedBase)
                                ? mappedBase
                                : baseRawColor);
                        if (string.IsNullOrEmpty(baseSemantic) ||
                            string.Equals(baseSemantic, semanticTextToken, StringComparison.OrdinalIgnoreCase))
                        {
                            preserveExplicitUserColorEdit = true;
                        }
                    }
                }

                if (!preserveExplicitUserColorEdit)
                {
                    node["color"] = JsonValue.Create(semanticTextToken);
                    changed = true;
                }
            }
        }

        if (isCard)
        {
            string rawTone = node["tone"]?.GetValue<string?>()?.Trim() ?? string.Empty;
            bool isNeutralTone =
                !rawTone.Contains("${", StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(rawTone) ||
                 string.Equals(rawTone, "default", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(rawTone, "subtle", StringComparison.OrdinalIgnoreCase));
            bool hasInteractiveTrigger =
                node.ContainsKey("onClick") ||
                node.ContainsKey("popover") ||
                node.ContainsKey("contextMenu");

            isPlainCardShell = isNeutralTone && !hasInteractiveTrigger;

            bool isProtectedTarget = !string.IsNullOrEmpty(lockedCanonicalPath) &&
                                     string.Equals(currentPath, lockedCanonicalPath, StringComparison.Ordinal);

            if (!isRootView &&
                !isProtectedTarget &&
                isPlainCardShell &&
                (cardAncestorDepth >= 2 || directParentIsSingleChildCard))
            {
                node["type"] = JsonValue.Create("Stack");
                effectiveNodeType = "Stack";
                if (!node.ContainsKey("padding") || node["padding"] is null)
                {
                    string fallbackPad = directParentIsSingleChildCard && cardAncestorDepth < 2
                        ? "0"
                        : "6,4,6,4";
                    node["padding"] = JsonValue.Create(fallbackPad);
                }

                changed = true;
            }
        }

        return changed;
    }

    private static bool IsExemptMicroIndicatorOrVisual(JsonObject node, string? nodeType)
    {
        if (!string.IsNullOrWhiteSpace(nodeType))
        {
            switch (nodeType.Trim().ToLowerInvariant())
            {
                case "canvas":
                case "image":
                case "badge":
                case "button":
                case "sparkline":
                case "progressring":
                case "barchart":
                case "heatmap":
                case "gauge":
                case "ringgauge":
                    return true;
            }
        }

        bool hasChildren =
            (node["children"] is JsonArray arr && arr.Count > 0) ||
            (node["itemTemplate"] is JsonValue itv && itv.TryGetValue(out string? its) && !string.IsNullOrWhiteSpace(its)) ||
            (node["content"] is JsonValue cv && cv.TryGetValue(out string? cs) && !string.IsNullOrWhiteSpace(cs)) ||
            (node["pane"] is JsonValue pv && pv.TryGetValue(out string? ps) && !string.IsNullOrWhiteSpace(ps));
        if (!hasChildren &&
            TryReadNumericJsonNode(node["width"], out double w) && w > 0 && w <= 32 &&
            TryReadNumericJsonNode(node["height"], out double h) && h > 0 && h <= 32)
        {
            return true;
        }

        return false;
    }

    private static bool IsLayoutContainerTypeName(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return false;
        }

        return nodeType.Trim().ToLowerInvariant() switch
        {
            "stack" or "column" or "row" or "grid" or "scroll" or "tabs" or
            "tabitem" or "tab" or "wrap" or "zstack" or "modulestack" or "box" or "itemsgrid" => true,
            _ => false
        };
    }

    private static bool TryReadNumericJsonNode(JsonNode? node, out double value)
    {
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue(out double d))
            {
                value = d;
                return true;
            }
            if (jv.TryGetValue(out int i))
            {
                value = i;
                return true;
            }
            if (jv.TryGetValue(out string? s) &&
                double.TryParse(s?.Trim().TrimEnd('p', 'x'), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                return true;
            }
        }

        value = 0;
        return false;
    }

    internal static bool TryMapNeutralTextColorToSemanticToken(string? rawColor, out string semanticToken)
    {
        semanticToken = string.Empty;
        if (string.IsNullOrWhiteSpace(rawColor) ||
            rawColor.Contains("${", StringComparison.Ordinal) ||
            !TryParseStaticRgbColor(rawColor.Trim(), out byte r, out byte g, out byte b))
        {
            return false;
        }

        int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        if (chroma > 48)
        {
            return false;
        }

        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        semanticToken = luminance switch
        {
            <= 0.16 or >= 0.92 => "default",
            (> 0.16 and <= 0.36) or (>= 0.72 and < 0.92) => "secondary",
            _ => "muted"
        };
        return true;
    }

    private static bool TryParseStaticRgbColor(string raw, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith('#'))
        {
            string hex = trimmed[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }
            else if (hex.Length == 8)
            {
                hex = hex[2..];
            }

            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
        }
        else if (trimmed.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
        {
            int open = trimmed.IndexOf('(');
            int close = trimmed.LastIndexOf(')');
            if (open >= 0 && close > open + 1)
            {
                string[] parts = trimmed[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 &&
                    byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r) &&
                    byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g) &&
                    byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string MapRgbHueToCardTone(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;
        if (delta <= 0.001)
        {
            return "default";
        }

        double hue;
        if (Math.Abs(max - rd) < 0.0001)
        {
            hue = 60.0 * (((gd - bd) / delta) % 6.0);
        }
        else if (Math.Abs(max - gd) < 0.0001)
        {
            hue = 60.0 * (((bd - rd) / delta) + 2.0);
        }
        else
        {
            hue = 60.0 * (((rd - gd) / delta) + 4.0);
        }

        if (hue < 0)
        {
            hue += 360.0;
        }

        return hue switch
        {
            >= 8 and < 34 => "coral",
            >= 34 and < 68 => "warning",
            >= 68 and < 168 => "emerald",
            >= 168 and < 205 => "cyan",
            >= 205 and < 258 => "accent",
            >= 258 and < 328 => "violet",
            _ => "danger"
        };
    }

    private static JsonObject? SynthesizeSafeCapsuleFallback(
        JsonObject manifestRoot,
        JsonObject elementsMap,
        string rootElemId)
    {
        string rawName = manifestRoot["name"]?.GetValue<string?>()?.Trim() ?? string.Empty;
        string title = !string.IsNullOrWhiteSpace(rawName) ? rawName : "Widget";
        string rawIcon = manifestRoot["icon"]?.GetValue<string?>()?.Trim() ?? string.Empty;
        string glyph = !string.IsNullOrWhiteSpace(rawIcon) ? rawIcon : "\uE71D";

        // Extract only already-present safe expressions from elements graph (excluding $item.* loop variables)
        string? safeSummary = TryFindFirstSafeCapsuleSummaryExpression(
            elementsMap,
            rootElemId,
            new HashSet<string>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            string desc = manifestRoot["description"]?.GetValue<string?>()?.Trim() ?? string.Empty;
            safeSummary = !string.IsNullOrWhiteSpace(desc) ? TruncateInline(desc, 24) : title;
        }

        return new JsonObject
        {
            ["glyph"] = JsonValue.Create(glyph),
            ["title"] = JsonValue.Create(title),
            ["summary"] = JsonValue.Create(safeSummary)
        };
    }

    private static string? TryFindFirstSafeCapsuleSummaryExpression(
        JsonObject elementsMap,
        string elemId,
        HashSet<string> visited,
        int depth = 0)
    {
        if (depth > 8 ||
            string.IsNullOrWhiteSpace(elemId) ||
            !visited.Add(elemId) ||
            elementsMap[elemId] is not JsonObject node)
        {
            return null;
        }

        foreach (string key in (ReadOnlySpan<string>)["text", "value", "label", "subtitle"])
        {
            if (node[key] is JsonValue jv && jv.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                string trimmed = s.Trim();
                if (trimmed.Contains("$item.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("item.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains(" item.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("{item.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("(item.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (trimmed.Contains("${", StringComparison.Ordinal))
                {
                    return TruncateInline(trimmed, 48);
                }
            }
        }

        if (node["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                if (child is JsonValue cv &&
                    cv.TryGetValue(out string? childId) &&
                    !string.IsNullOrWhiteSpace(childId) &&
                    TryFindFirstSafeCapsuleSummaryExpression(elementsMap, childId.Trim(), visited, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool NormalizeGridTrackPropertyInPlace(JsonObject node, string propertyName, params string[] fallbackPropertyNames)
    {
        bool changed = false;
        string? raw = null;
        bool hadFallback = false;

        if (!node.TryGetPropertyValue(propertyName, out JsonNode? propNode) || propNode is null)
        {
            foreach (string fallbackPropertyName in fallbackPropertyNames)
            {
                if (!string.IsNullOrEmpty(fallbackPropertyName) &&
                    node.TryGetPropertyValue(fallbackPropertyName, out JsonNode? fallbackNode) &&
                    fallbackNode is not null)
                {
                    propNode = fallbackNode;
                    hadFallback = true;
                    break;
                }
            }

            if (propNode is null)
            {
                return false;
            }
        }

        foreach (string alias in fallbackPropertyNames)
        {
            if (!string.IsNullOrEmpty(alias) &&
                !hadFallback &&
                node.ContainsKey(alias))
            {
                node.Remove(alias);
                changed = true;
            }
        }

        if (propNode is JsonArray arr)
        {
            var parts = new List<string>(arr.Count);
            foreach (JsonNode? item in arr)
            {
                string? token = item switch
                {
                    JsonValue jv when jv.TryGetValue(out string? s) => s,
                    JsonValue jv => jv.ToJsonString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(token))
                {
                    parts.Add(token.Trim());
                }
            }
            if (parts.Count > 0)
            {
                raw = string.Join(",", parts);
            }
        }
        else if (propNode is JsonValue val)
        {
            raw = val.TryGetValue(out string? strVal) ? strVal : val.ToJsonString();
        }

        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("${", StringComparison.Ordinal))
        {
            if (hadFallback && !string.IsNullOrWhiteSpace(raw))
            {
                node[propertyName] = JsonValue.Create(raw.Trim());
                foreach (string alias in fallbackPropertyNames)
                {
                    if (!string.IsNullOrEmpty(alias)) node.Remove(alias);
                }
                return true;
            }
            return false;
        }

        if (CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(raw, out string normalized) &&
            !string.IsNullOrWhiteSpace(normalized))
        {
            if (hadFallback ||
                propNode is not JsonValue ||
                !string.Equals(raw.Trim(), normalized, StringComparison.Ordinal))
            {
                node[propertyName] = JsonValue.Create(normalized);
                changed = true;
            }

            if (hadFallback)
            {
                foreach (string alias in fallbackPropertyNames)
                {
                    if (!string.IsNullOrEmpty(alias)) node.Remove(alias);
                }
                changed = true;
            }
        }

        return changed;
    }

    private static readonly FrozenSet<string> SubtreeGuardAncestorScalarProps = new[]
    {
        "spacing", "padding", "columns", "rows", "orientation", "width",
        "height", "maxHeight", "minWidth", "minHeight", "verticalAlignment",
        "horizontalAlignment", "columnSpacing", "rowSpacing", "wrap", "dir"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CollectFlatElementSubtreeIds(JsonObject elementsMap, string startElemId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(startElemId))
        {
            return ids;
        }

        var queue = new Queue<string>();
        queue.Enqueue(startElemId);
        while (queue.Count > 0)
        {
            string cur = queue.Dequeue();
            if (!ids.Add(cur) || elementsMap[cur] is not JsonObject elem)
            {
                continue;
            }

            if (elem["children"] is JsonArray childIds)
            {
                foreach (JsonNode? c in childIds)
                {
                    if (c is JsonValue cv && cv.TryGetValue(out string? cid) && !string.IsNullOrWhiteSpace(cid))
                    {
                        queue.Enqueue(cid.Trim());
                    }
                }
            }

            if (elem["hoverActions"] is JsonArray hoverIds)
            {
                foreach (JsonNode? h in hoverIds)
                {
                    if (h is JsonValue hv && hv.TryGetValue(out string? hid) && !string.IsNullOrWhiteSpace(hid))
                    {
                        queue.Enqueue(hid.Trim());
                    }
                }
            }

            foreach (string refKey in (ReadOnlySpan<string>)["child", "itemTemplate", "pane", "actionButton"])
            {
                if (elem[refKey] is JsonValue rv && rv.TryGetValue(out string? rid) && !string.IsNullOrWhiteSpace(rid))
                {
                    queue.Enqueue(rid.Trim());
                }
            }
        }

        return ids;
    }

    private bool EnforceSubtreeGuardOnFullReplace(
        JsonObject candidate,
        out string? rejectionReason,
        out string? graftNote)
    {
        rejectionReason = null;
        graftNote = null;

        if (string.IsNullOrEmpty(_lockedCanonicalPath) || _baselineManifestSnapshot is null)
        {
            return true;
        }

        if (!TryTokenizeSelector(_lockedCanonicalPath, out List<SelectorToken> lockTokens) ||
            lockTokens.Count < 2)
        {
            return true;
        }

        int preservedBranchCount = 0;

        // 1. Protect unrelated baseline popovers if lock is not wiping popovers
        preservedBranchCount += GraftBaselinePopoversIntoCandidate(_baselineManifestSnapshot, candidate, _lockedCanonicalPath);

        // 2. Walk structural spine down to the parent container of _lockedCanonicalPath
        JsonNode? baseCur = _baselineManifestSnapshot;
        JsonNode? candCur = candidate;

        for (int i = 0; i < lockTokens.Count - 1; i++)
        {
            SelectorToken tok = lockTokens[i];
            if (tok.Kind != SelectorTokenKind.Property)
            {
                return true;
            }

            string prop = tok.Name ?? string.Empty;
            if (baseCur is not JsonObject baseObj ||
                !TryGetObjectPropertyExactOrFolded(baseObj, prop, out _, out JsonNode? nextBase) ||
                candCur is not JsonObject candObj ||
                !TryGetObjectPropertyExactOrFolded(candObj, prop, out string actualCandKey, out JsonNode? nextCand) ||
                nextBase is null ||
                nextCand is null)
            {
                rejectionReason = $"Structural spine segment '.{prop}' leading to '{_lockedCanonicalPath}' was removed or incompatible in replacement manifest. Modify only '{_lockedCanonicalPath}' or use edit_manifest.";
                return false;
            }

            // Protect non-spine child containers on intermediate spine nodes
            string spinePropNext = lockTokens[i + 1].Kind == SelectorTokenKind.Property
                ? (lockTokens[i + 1].Name ?? string.Empty)
                : string.Empty;
            foreach (string protectedObjProp in (ReadOnlySpan<string>)["child", "itemTemplate", "pane", "actionButton"])
            {
                if (!string.Equals(protectedObjProp, spinePropNext, StringComparison.OrdinalIgnoreCase) &&
                    baseCur is JsonObject bParent &&
                    bParent.TryGetPropertyValue(protectedObjProp, out JsonNode? bSub) &&
                    bSub is not null &&
                    candCur is JsonObject cParent)
                {
                    cParent[protectedObjProp] = bSub.DeepClone();
                    preservedBranchCount++;
                }
            }

            baseCur = nextBase;
            candCur = nextCand;
            _ = actualCandKey;
        }

        // 3. Final segment: check final target or direct parent siblings
        SelectorToken finalTok = lockTokens[^1];
        if (finalTok.Kind == SelectorTokenKind.Property)
        {
            string finalProp = finalTok.Name ?? string.Empty;
            if (baseCur is JsonObject baseObj && candCur is JsonObject candObj)
            {
                if (!candObj.ContainsKey(finalProp))
                {
                    rejectionReason = $"Locked target property '.{finalProp}' at '{_lockedCanonicalPath}' was removed in replacement manifest.";
                    return false;
                }

                HashSet<string>? allowedFlatSubtreeIds =
                    lockTokens.Count == 2 &&
                    string.Equals(lockTokens[0].Name, "elements", StringComparison.Ordinal)
                        ? CollectFlatElementSubtreeIds(baseObj, finalProp)
                        : null;

                foreach (KeyValuePair<string, JsonNode?> kv in baseObj)
                {
                    if (allowedFlatSubtreeIds is not null)
                    {
                        if (!allowedFlatSubtreeIds.Contains(kv.Key) && kv.Value is JsonObject)
                        {
                            candObj[kv.Key] = kv.Value.DeepClone();
                            preservedBranchCount++;
                        }
                        continue;
                    }

                    if (!string.Equals(kv.Key, finalProp, StringComparison.OrdinalIgnoreCase) &&
                        !SubtreeGuardAncestorScalarProps.Contains(kv.Key) &&
                        kv.Value is JsonObject or JsonArray)
                    {
                        candObj[kv.Key] = kv.Value.DeepClone();
                        preservedBranchCount++;
                    }
                }
            }
        }

        if (preservedBranchCount > 0)
        {
            graftNote = $"Subtree Guard preserved {preservedBranchCount} untouched sibling branch(es)/popover(s) relative to '{_lockedCanonicalPath}'.";
        }

        return true;
    }

    private static int GraftBaselinePopoversIntoCandidate(
        JsonObject baselineRoot,
        JsonObject candidateRoot,
        string lockedCanonicalPath)
    {
        if (!baselineRoot.TryGetPropertyValue("popovers", out JsonNode? basePopsNode) ||
            basePopsNode is not JsonObject basePopMap)
        {
            return 0;
        }

        if (candidateRoot["popovers"] is not JsonObject candPopMap)
        {
            if (!lockedCanonicalPath.StartsWith("$.popovers.", StringComparison.Ordinal))
            {
                candidateRoot["popovers"] = basePopMap.DeepClone();
                return basePopMap.Count;
            }
            return 0;
        }

        int preserved = 0;
        foreach (KeyValuePair<string, JsonNode?> kv in basePopMap)
        {
            string entryPath = $"$.popovers.{kv.Key}";
            if (!IsSameOrDescendantPath(lockedCanonicalPath, entryPath))
            {
                candPopMap[kv.Key] = kv.Value?.DeepClone();
                preserved++;
            }
        }

        return preserved;
    }

    private static bool IsSameOrDescendantPath(string candidatePath, string prefixPath)
    {
        if (string.Equals(candidatePath, prefixPath, StringComparison.Ordinal))
        {
            return true;
        }

        return candidatePath.Length > prefixPath.Length &&
               candidatePath.StartsWith(prefixPath, StringComparison.Ordinal) &&
               (candidatePath[prefixPath.Length] == '.' || candidatePath[prefixPath.Length] == '[');
    }


    private static bool TryReadNumericJsonValue(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jv)
        {
            return false;
        }

        if (jv.TryGetValue(out double d))
        {
            value = d;
            return true;
        }

        if (jv.TryGetValue(out int i))
        {
            value = i;
            return true;
        }

        if (jv.TryGetValue(out long l))
        {
            value = l;
            return true;
        }

        if (jv.TryGetValue(out decimal dec))
        {
            value = (double)dec;
            return true;
        }

        if (jv.TryGetValue(out JsonElement el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double ed))
        {
            value = ed;
            return true;
        }

        return false;
    }

    private static bool TryFindEnclosingContainer(
        JsonObject root,
        string resolvedPath,
        out string enclosingPath,
        out JsonObject? enclosingObj)
    {
        enclosingPath = string.Empty;
        enclosingObj = null;

        // Flat element graph: for $.elements.<elemId> (or #<elemId>), find the referencing parent element inside elements.
        JsonObject? elementsMap = root["elements"] as JsonObject;
        if (elementsMap is not null)
        {
            string? targetElemId = null;
            if (resolvedPath.StartsWith("$.elements.", StringComparison.Ordinal))
            {
                string rest = resolvedPath["$.elements.".Length..];
                int dotIdx = rest.IndexOfAny(['.', '[']);
                targetElemId = dotIdx < 0 ? rest : rest[..dotIdx];
            }
            else if (resolvedPath.StartsWith('#'))
            {
                string rest = resolvedPath[1..];
                int dotIdx = rest.IndexOfAny(['.', '[']);
                targetElemId = dotIdx < 0 ? rest : rest[..dotIdx];
            }

            if (!string.IsNullOrWhiteSpace(targetElemId))
            {
                foreach (KeyValuePair<string, JsonNode?> kv in elementsMap)
                {
                    if (string.Equals(kv.Key, targetElemId, StringComparison.Ordinal) ||
                        kv.Value is not JsonObject candidateObj)
                    {
                        continue;
                    }

                    if (ElementReferencesChildId(candidateObj, targetElemId))
                    {
                        enclosingPath = $"$.elements.{kv.Key}";
                        enclosingObj = candidateObj;
                        return true;
                    }
                }

                return false;
            }
        }

        string cur = resolvedPath;
        for (int i = 0; i < 16; i++)
        {
            if (!TryTrimLastSelectorSegment(cur, out string parentPath))
            {
                break;
            }

            cur = parentPath;
            if (TryResolveNode(root, cur, out JsonNode? cand, out _, out _, out string resolvedParentPath, out _) &&
                cand is JsonObject obj &&
                !ReferenceEquals(obj, root) &&
                !ReferenceEquals(obj, elementsMap))
            {
                enclosingPath = resolvedParentPath;
                enclosingObj = obj;
                return true;
            }
        }

        return false;
    }

    private static bool ElementReferencesChildId(JsonObject parentObj, string childId)
    {
        if (parentObj["children"] is JsonArray childrenArr)
        {
            for (int i = 0; i < childrenArr.Count; i++)
            {
                if (childrenArr[i] is JsonValue v &&
                    v.TryGetValue(out string? id) &&
                    string.Equals(id?.Trim(), childId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        if (parentObj["hoverActions"] is JsonArray hoverArr)
        {
            for (int i = 0; i < hoverArr.Count; i++)
            {
                if (hoverArr[i] is JsonValue v &&
                    v.TryGetValue(out string? id) &&
                    string.Equals(id?.Trim(), childId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        foreach (string refKey in (ReadOnlySpan<string>)["itemTemplate", "pane", "actionButton", "child"])
        {
            if (parentObj[refKey] is JsonValue v &&
                v.TryGetValue(out string? id) &&
                string.Equals(id?.Trim(), childId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ResolveFlatChildElement(JsonObject elementsMap, JsonNode? childRef) =>
        childRef is JsonValue cv &&
        cv.TryGetValue(out string? cid) &&
        !string.IsNullOrWhiteSpace(cid) &&
        elementsMap[cid.Trim()] is JsonObject obj
            ? obj
            : null;

    private static bool TryNormalizeInlineActionRowToGrid(JsonObject node, JsonObject elementsMap, ref string? nodeType)
    {
        bool isHorizontalStack =
            string.Equals(nodeType, "Row", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(nodeType, "Stack", StringComparison.OrdinalIgnoreCase) &&
             node["orientation"] is JsonValue oriVal &&
             oriVal.TryGetValue(out string? oriStr) &&
             string.Equals(oriStr, "horizontal", StringComparison.OrdinalIgnoreCase));
        if (!isHorizontalStack ||
            node["children"] is not JsonArray children ||
            children.Count != 2 ||
            ResolveFlatChildElement(elementsMap, children[0]) is not JsonObject firstChild ||
            ResolveFlatChildElement(elementsMap, children[1]) is not JsonObject secondChild)
        {
            return false;
        }

        string? firstType = firstChild["type"] is JsonValue ftVal && ftVal.TryGetValue(out string? ftStr) ? ftStr : null;
        string? secondType = secondChild["type"] is JsonValue stVal && stVal.TryGetValue(out string? stStr) ? stStr : null;
        bool isStretchInput =
            string.Equals(firstType, "Input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstType, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstType, "Select", StringComparison.OrdinalIgnoreCase);
        bool hasExplicitWidth = firstChild.TryGetPropertyValue("width", out JsonNode? wNode) && wNode is not null;
        bool isActionButton =
            string.Equals(secondType, "Button", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secondType, "IconButton", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secondType, "SplitButton", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(secondType, "DropDownButton", StringComparison.OrdinalIgnoreCase);
        if (!isStretchInput || hasExplicitWidth || !isActionButton)
        {
            return false;
        }

        node["type"] = JsonValue.Create("Grid");
        node.Remove("orientation");
        node["columns"] = JsonValue.Create("*,Auto");
        if (node.TryGetPropertyValue("spacing", out JsonNode? spacingNode) && spacingNode is not null)
        {
            if (!node.ContainsKey("columnSpacing"))
            {
                node["columnSpacing"] = spacingNode.DeepClone();
            }
            node.Remove("spacing");
        }
        else if (!node.ContainsKey("columnSpacing"))
        {
            node["columnSpacing"] = JsonValue.Create(8);
        }

        firstChild["column"] = JsonValue.Create(0);
        secondChild["column"] = JsonValue.Create(1);
        nodeType = "Grid";
        return true;
    }

    private static bool AlignTabsInitialStateInPlace(JsonObject manifestRoot, JsonObject elementsMap)
    {
        bool changed = false;

        static string? ExtractFlatStateKey(JsonObject obj, string propName)
        {
            if (obj[propName] is not JsonValue val || !val.TryGetValue(out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
            {
                trimmed = trimmed[2..^1].Trim();
            }

            if (trimmed.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[6..].Trim();
            }
            else if (!string.Equals(propName, "bind", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('.', StringComparison.Ordinal)
                ? trimmed
                : null;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in elementsMap)
        {
            if (kv.Value is not JsonObject current ||
                current["type"] is not JsonValue typeVal ||
                !typeVal.TryGetValue(out string? t) ||
                !string.Equals(t, "Tabs", StringComparison.OrdinalIgnoreCase) ||
                current["children"] is not JsonArray tabChildren ||
                tabChildren.Count == 0)
            {
                continue;
            }

            string? bindKey = ExtractFlatStateKey(current, "bind")
                ?? ExtractFlatStateKey(current, "value")
                ?? ExtractFlatStateKey(current, "selected");
            if (string.IsNullOrWhiteSpace(bindKey))
            {
                continue;
            }

            var tabValues = new List<string>(tabChildren.Count);
            foreach (JsonNode? child in tabChildren)
            {
                if (ResolveFlatChildElement(elementsMap, child) is JsonObject tabItem &&
                    tabItem["value"] is JsonValue vv &&
                    vv.TryGetValue(out string? tabVal) &&
                    !string.IsNullOrWhiteSpace(tabVal))
                {
                    tabValues.Add(tabVal.Trim());
                }
            }

            if (tabValues.Count > 0)
            {
                if (manifestRoot["state"] is not JsonObject stateObj)
                {
                    stateObj = new JsonObject();
                    manifestRoot["state"] = stateObj;
                }

                string? existing = stateObj[bindKey] is JsonValue sv && sv.TryGetValue(out string? sStr)
                    ? sStr.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(existing) || !tabValues.Contains(existing, StringComparer.Ordinal))
                {
                    stateObj[bindKey] = JsonValue.Create(tabValues[0]);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool NormalizeSettingsInPlace(JsonObject manifestRoot)
    {
        if (!manifestRoot.TryGetPropertyValue("settings", out JsonNode? settingsNode) || settingsNode is null)
        {
            return false;
        }

        bool changed = false;
        if (settingsNode is JsonArray settingsArr)
        {
            for (int i = 0; i < settingsArr.Count; i++)
            {
                if (settingsArr[i] is JsonObject settingObj)
                {
                    changed |= NormalizeSingleSettingObjectInPlace(settingObj);
                }
            }
        }
        else if (settingsNode is JsonObject settingsMap)
        {
            JsonObject sourceProps = settingsMap.Count == 1 && settingsMap["properties"] is JsonObject schemaProps
                ? schemaProps
                : settingsMap;
            var convertedArr = new JsonArray();
            foreach (KeyValuePair<string, JsonNode?> kv in sourceProps)
            {
                if (string.Equals(kv.Key, "properties", StringComparison.Ordinal) && kv.Value is JsonObject innerProps && sourceProps != innerProps)
                {
                    foreach (KeyValuePair<string, JsonNode?> ikv in innerProps)
                    {
                        if (ikv.Value is JsonObject innerSetting && innerSetting.DeepClone() is JsonObject clonedInner)
                        {
                            if (!clonedInner.ContainsKey("key"))
                            {
                                clonedInner["key"] = JsonValue.Create(ikv.Key);
                            }
                            NormalizeSingleSettingObjectInPlace(clonedInner);
                            convertedArr.Add((JsonNode)clonedInner);
                        }
                    }
                    continue;
                }

                if (kv.Value is JsonObject settingObj && settingObj.DeepClone() is JsonObject cloned)
                {
                    if (!cloned.ContainsKey("key"))
                    {
                        cloned["key"] = JsonValue.Create(kv.Key);
                    }
                    NormalizeSingleSettingObjectInPlace(cloned);
                    convertedArr.Add((JsonNode)cloned);
                }
            }

            manifestRoot["settings"] = convertedArr;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeSingleSettingObjectInPlace(JsonObject settingObj)
    {
        bool changed = false;

        if (settingObj.TryGetPropertyValue("valueJson", out JsonNode? vjNode) && vjNode is null)
        {
            settingObj.Remove("valueJson");
            changed = true;
        }

        if (!settingObj.ContainsKey("label") &&
            settingObj.TryGetPropertyValue("title", out JsonNode? titleNode) &&
            titleNode is not null)
        {
            settingObj["label"] = titleNode.DeepClone();
            settingObj.Remove("title");
            changed = true;
        }

        if (!settingObj.ContainsKey("default") &&
            settingObj.TryGetPropertyValue("value", out JsonNode? valNode) &&
            valNode is not null)
        {
            settingObj["default"] = valNode.DeepClone();
            settingObj.Remove("value");
            changed = true;
        }

        string? type = settingObj["type"] is JsonValue tv && tv.TryGetValue(out string? tStr)
            ? tStr.Trim()
            : null;
        if (!string.IsNullOrEmpty(type))
        {
            string? canonicalSettingType = type.ToLowerInvariant() switch
            {
                "numberbox" or "int" or "integer" or "float" or "double" => "number",
                "toggleswitch" or "boolean" or "bool" or "switch" => "toggle",
                "combobox" or "dropdown" or "enum" => "select",
                "textbox" or "string" or "input" => "text",
                _ => null
            };
            if (canonicalSettingType is not null)
            {
                settingObj["type"] = JsonValue.Create(canonicalSettingType);
                type = canonicalSettingType;
                changed = true;
            }
        }

        if (!string.Equals(type, "moduleList", StringComparison.OrdinalIgnoreCase) ||
            settingObj["items"] is not JsonArray itemsArr)
        {
            return changed;
        }

        var optionsArr = new JsonArray();
        var defaultParts = new List<string>(itemsArr.Count);
        foreach (JsonNode? entry in itemsArr)
        {
            if (entry is not JsonObject itemObj)
            {
                continue;
            }

            string id = itemObj["id"] is JsonValue idv && idv.TryGetValue(out string? idStr)
                ? idStr.Trim()
                : (itemObj["value"] is JsonValue vv && vv.TryGetValue(out string? vStr) ? vStr.Trim() : string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            string label = itemObj["label"] is JsonValue lv && lv.TryGetValue(out string? lStr) && !string.IsNullOrEmpty(lStr)
                ? lStr
                : id;
            bool visible = true;
            if (itemObj["visible"] is JsonValue visVal && visVal.TryGetValue(out bool visBool))
            {
                visible = visBool;
            }
            else if (itemObj["enabled"] is JsonValue enVal && enVal.TryGetValue(out bool enBool))
            {
                visible = enBool;
            }
            else if (itemObj["defaultEnabled"] is JsonValue defEnVal && defEnVal.TryGetValue(out bool defEnBool))
            {
                visible = defEnBool;
            }

            optionsArr.Add((JsonNode)new JsonObject
            {
                ["value"] = JsonValue.Create(id),
                ["label"] = JsonValue.Create(label)
            });
            defaultParts.Add($"{id}:{(visible ? "1" : "0")}");
        }

        if (settingObj["options"] is not JsonArray existingOpts || existingOpts.Count == 0)
        {
            settingObj["options"] = optionsArr;
        }

        string existingDefault = settingObj["default"] is JsonValue dv && dv.TryGetValue(out string? dStr)
            ? dStr.Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(existingDefault) && defaultParts.Count > 0)
        {
            settingObj["default"] = JsonValue.Create(string.Join(",", defaultParts));
        }

        settingObj.Remove("items");
        return true;
    }

    private static bool NormalizeActionTreeInPlace(JsonNode? actionNode)
    {
        if (actionNode is null)
        {
            return false;
        }

        bool changed = false;
        if (actionNode is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                changed |= NormalizeActionTreeInPlace(arr[i]);
            }
            return changed;
        }

        if (actionNode is not JsonObject actObj)
        {
            return false;
        }

        if (actObj["type"] is JsonValue tv && tv.TryGetValue(out string? rawType) && !string.IsNullOrWhiteSpace(rawType))
        {
            string trimmed = rawType.Trim();
            string canonicalType = trimmed switch
            {
                "setState" => "state.set",
                "toggleState" => "state.toggle",
                "incrementState" => "state.increment",
                "refreshSource" => "source.refresh",
                "runScript" => "script.run",
                "openUrl" => "url.open",
                "copyText" => "clipboard.copy",
                "notify" => "reminder.notify",
                "toast" => "toast.show",
                "prependRecord" => "collection.prepend",
                "appendRecord" => "collection.append",
                "removeRecord" => "collection.remove",
                "updateRecord" => "collection.update",
                "clearCollection" => "collection.clear",
                _ => trimmed
            };

            if (!string.Equals(rawType, canonicalType, StringComparison.Ordinal))
            {
                actObj["type"] = JsonValue.Create(canonicalType);
                changed = true;
            }

            if (string.Equals(canonicalType, "collection.remove", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonicalType, "collection.update", StringComparison.OrdinalIgnoreCase))
            {
                bool hasTargetId = actObj.TryGetPropertyValue("targetId", out JsonNode? tidNode) && tidNode is not null;
                foreach (string targetAlias in new[] { "id", "target" })
                {
                    if (!actObj.TryGetPropertyValue(targetAlias, out JsonNode? aliasNode) || aliasNode is null)
                    {
                        continue;
                    }

                    if (!hasTargetId)
                    {
                        actObj["targetId"] = aliasNode.DeepClone();
                        hasTargetId = true;
                    }
                    actObj.Remove(targetAlias);
                    changed = true;
                }
            }
            else if (string.Equals(canonicalType, "source.refresh", StringComparison.OrdinalIgnoreCase) &&
                     actObj.TryGetPropertyValue("source", out JsonNode? srcAliasNode) &&
                     srcAliasNode is not null)
            {
                bool hasSourceTarget =
                    (actObj.TryGetPropertyValue("sourceId", out JsonNode? sidNode) && sidNode is not null) ||
                    (actObj.TryGetPropertyValue("targetId", out JsonNode? tidNode) && tidNode is not null);
                if (!hasSourceTarget)
                {
                    actObj["sourceId"] = srcAliasNode.DeepClone();
                }
                actObj.Remove("source");
                changed = true;
            }
        }

        if (actObj.TryGetPropertyValue("then", out JsonNode? thenNode) && thenNode is not null)
        {
            changed |= NormalizeActionTreeInPlace(thenNode);
        }

        return changed;
    }

    private static bool NormalizeCommonDialectPropertiesInPlace(
        JsonObject node,
        string elemId,
        JsonObject elementsMap,
        ref string? nodeType,
        bool isRootView)
    {
        bool changed = false;

        changed |= NormalizeUnicodeGlyphPropertyInPlace(node, "icon");
        changed |= NormalizeUnicodeGlyphPropertyInPlace(node, "glyph");
        if (node["emptyState"] is JsonObject emptyStateObj)
        {
            changed |= NormalizeUnicodeGlyphPropertyInPlace(emptyStateObj, "icon");
        }

        if (CustomWidgetManifestParser.IsReadOnlyDisplayNodeType(nodeType))
        {
            bool hasDisplayProp =
                node.ContainsKey("text") ||
                node.ContainsKey("code") ||
                node.ContainsKey("markdown") ||
                node.ContainsKey("content") ||
                node.ContainsKey("spans");
            if (!hasDisplayProp &&
                node.TryGetPropertyValue("bind", out JsonNode? bindAliasNode) &&
                bindAliasNode is JsonValue bindAliasVal &&
                bindAliasVal.TryGetValue(out string? rawBindStr) &&
                !string.IsNullOrWhiteSpace(rawBindStr))
            {
                string trimmedBind = rawBindStr.Trim();
                string targetProp = string.Equals(nodeType, "CodeBlock", StringComparison.OrdinalIgnoreCase)
                    ? "code"
                    : "text";
                node[targetProp] = JsonValue.Create(
                    trimmedBind.Contains("${", StringComparison.Ordinal)
                        ? trimmedBind
                        : $"${{{trimmedBind}}}");
                node.Remove("bind");
                changed = true;
            }
        }

        if (string.Equals(nodeType, "KeyValueRow", StringComparison.OrdinalIgnoreCase) &&
            !node.ContainsKey("title") &&
            !node.ContainsKey("label") &&
            !node.ContainsKey("header") &&
            node.TryGetPropertyValue("key", out JsonNode? kvKeyNode) &&
            kvKeyNode is JsonValue kvKeyVal &&
            kvKeyVal.TryGetValue(out string? kvKeyStr) &&
            !string.IsNullOrWhiteSpace(kvKeyStr))
        {
            node["label"] = JsonValue.Create(kvKeyStr);
            node.Remove("key");
            changed = true;
        }

        if (string.Equals(nodeType, "Text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nodeType, "StatCard", StringComparison.OrdinalIgnoreCase))
        {
            if (node.TryGetPropertyValue("mono", out JsonNode? monoNode))
            {
                if (monoNode is JsonValue monoVal &&
                    monoVal.TryGetValue(out bool isMono) &&
                    isMono &&
                    !node.ContainsKey("fontFamily"))
                {
                    node["fontFamily"] = JsonValue.Create("mono");
                }
                node.Remove("mono");
                changed = true;
            }

            if (node.TryGetPropertyValue("bodyStrong", out JsonNode? bsNode))
            {
                if (bsNode is JsonValue bsVal &&
                    bsVal.TryGetValue(out bool isBs) &&
                    isBs &&
                    !node.ContainsKey("style"))
                {
                    node["style"] = JsonValue.Create("bodyStrong");
                }
                node.Remove("bodyStrong");
                changed = true;
            }

            if (node.TryGetPropertyValue("bold", out JsonNode? boldNode))
            {
                if (boldNode is JsonValue boldVal &&
                    boldVal.TryGetValue(out bool isBold) &&
                    isBold &&
                    !node.ContainsKey("fontWeight") &&
                    !node.ContainsKey("style"))
                {
                    node["fontWeight"] = JsonValue.Create("semiBold");
                }
                node.Remove("bold");
                changed = true;
            }

            if (node.TryGetPropertyValue("weight", out JsonNode? weightNode))
            {
                if (weightNode is JsonValue weightVal &&
                    weightVal.TryGetValue(out string? weightStr) &&
                    !string.IsNullOrWhiteSpace(weightStr) &&
                    !node.ContainsKey("fontWeight"))
                {
                    node["fontWeight"] = JsonValue.Create(weightStr.Trim());
                }
                node.Remove("weight");
                changed = true;
            }
        }

        if (string.Equals(nodeType, "Stack", StringComparison.OrdinalIgnoreCase))
        {
            if (node.TryGetPropertyValue("horizontal", out JsonNode? horizNode))
            {
                if (horizNode is JsonValue hVal && hVal.TryGetValue(out bool isHoriz) && !node.ContainsKey("orientation"))
                {
                    node["orientation"] = JsonValue.Create(isHoriz ? "horizontal" : "vertical");
                }
                node.Remove("horizontal");
                changed = true;
            }

            if (node.TryGetPropertyValue("vertical", out JsonNode? vertNode))
            {
                if (vertNode is JsonValue vVal && vVal.TryGetValue(out bool isVert) && !node.ContainsKey("orientation"))
                {
                    node["orientation"] = JsonValue.Create(isVert ? "vertical" : "horizontal");
                }
                node.Remove("vertical");
                changed = true;
            }
        }

        if (string.Equals(nodeType, "Stack", StringComparison.OrdinalIgnoreCase) &&
            node["children"] is JsonArray stackChildren)
        {
            bool hasSpacer = false;
            bool allButtons = stackChildren.Count is >= 2 and <= 4;
            bool anyIconButton = false;
            for (int i = 0; i < stackChildren.Count; i++)
            {
                if (ResolveFlatChildElement(elementsMap, stackChildren[i]) is not JsonObject childObj)
                {
                    allButtons = false;
                    continue;
                }

                string? childType = childObj["type"] is JsonValue ctVal && ctVal.TryGetValue(out string? ctStr)
                    ? ctStr
                    : null;
                if (string.Equals(childType, "IconButton", StringComparison.OrdinalIgnoreCase))
                {
                    anyIconButton = true;
                }
                else if (!string.Equals(childType, "Button", StringComparison.OrdinalIgnoreCase))
                {
                    allButtons = false;
                }

                if (string.Equals(childType, "Spacer", StringComparison.OrdinalIgnoreCase))
                {
                    hasSpacer = true;
                    allButtons = false;
                    continue;
                }

                if (string.Equals(childType, "Stack", StringComparison.OrdinalIgnoreCase) &&
                    childObj.TryGetPropertyValue("flex", out JsonNode? flexNode) &&
                    flexNode is JsonValue flexVal &&
                    flexVal.TryGetValue(out double flexNum) &&
                    flexNum > 0 &&
                    (childObj["children"] is not JsonArray grandChildren || grandChildren.Count == 0) &&
                    !childObj.ContainsKey("width") &&
                    !childObj.ContainsKey("height") &&
                    !childObj.ContainsKey("onClick"))
                {
                    childObj["type"] = JsonValue.Create("Spacer");
                    childObj.Remove("flex");
                    hasSpacer = true;
                    changed = true;
                }
            }

            if (node.TryGetPropertyValue("justify", out JsonNode? justifyNode))
            {
                string? justifyStr = justifyNode is JsonValue jv && jv.TryGetValue(out string? js) ? js?.Trim() : null;
                if (string.Equals(justifyStr, "spaceBetween", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(justifyStr, "space-between", StringComparison.OrdinalIgnoreCase))
                {
                    if (!node.ContainsKey("orientation") && !node.ContainsKey("direction"))
                    {
                        node["orientation"] = JsonValue.Create("horizontal");
                    }
                    if (!hasSpacer && stackChildren.Count >= 2)
                    {
                        string spacerId = $"{elemId}_spacer";
                        int suffix = 1;
                        while (elementsMap.ContainsKey(spacerId))
                        {
                            suffix++;
                            spacerId = $"{elemId}_spacer_{suffix}";
                        }
                        elementsMap[spacerId] = new JsonObject { ["type"] = JsonValue.Create("Spacer") };
                        stackChildren.Insert(stackChildren.Count - 1, (JsonNode?)JsonValue.Create(spacerId));
                        hasSpacer = true;
                    }
                }
                node.Remove("justify");
                changed = true;
            }

            if (!isRootView &&
                allButtons &&
                anyIconButton &&
                !node.ContainsKey("orientation") &&
                !node.ContainsKey("direction"))
            {
                node["orientation"] = JsonValue.Create("horizontal");
                changed = true;
            }

            if (!isRootView &&
                hasSpacer &&
                stackChildren.Count >= 2 &&
                !node.ContainsKey("orientation") &&
                !node.ContainsKey("direction") &&
                node["align"] is JsonValue alignVal &&
                alignVal.TryGetValue(out string? alignStr) &&
                string.Equals(alignStr, "center", StringComparison.OrdinalIgnoreCase))
            {
                node["orientation"] = JsonValue.Create("horizontal");
                changed = true;
            }
        }

        return changed;
    }

    private static bool NormalizeOneBasedGridIndicesInPlace(JsonObject gridNode, JsonObject elementsMap)
    {
        if (gridNode["children"] is not JsonArray children || children.Count == 0)
        {
            return false;
        }

        static int CountStaticTracks(JsonObject grid, string propName)
        {
            if (!grid.TryGetPropertyValue(propName, out JsonNode? trackNode) ||
                trackNode is not JsonValue trackVal ||
                !trackVal.TryGetValue(out string? raw) ||
                string.IsNullOrWhiteSpace(raw) ||
                raw.Contains("${", StringComparison.Ordinal))
            {
                return 0;
            }

            string normalized = CustomWidgetGridTrackNormalizer.NormalizeRuntimeTrackString(raw);
            return string.IsNullOrWhiteSpace(normalized)
                ? 0
                : normalized.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }

        bool TryShiftAxis(JsonArray kids, string indexProp, string spanProp, int trackCount)
        {
            if (trackCount < 2)
            {
                return false;
            }

            int explicitCount = 0;
            int minIndex = int.MaxValue;
            int maxExtent = 0;
            foreach (JsonNode? child in kids)
            {
                if (ResolveFlatChildElement(elementsMap, child) is not JsonObject cObj ||
                    !cObj.TryGetPropertyValue(indexProp, out JsonNode? idxNode) ||
                    idxNode is not JsonValue idxVal ||
                    !idxVal.TryGetValue(out int idx))
                {
                    continue;
                }

                explicitCount++;
                if (idx < minIndex) minIndex = idx;
                int span = cObj.TryGetPropertyValue(spanProp, out JsonNode? spNode) &&
                           spNode is JsonValue spVal &&
                           spVal.TryGetValue(out int sp) &&
                           sp > 1
                    ? sp
                    : 1;
                int extent = idx + span - 1;
                if (extent > maxExtent) maxExtent = extent;
            }

            // Unambiguous CSS 1-based Grid placement: at least 2 placed children, none at 0, starts at 1, and reaches N (out-of-bounds in 0-based!)
            if (explicitCount < 2 || minIndex != 1 || maxExtent != trackCount)
            {
                return false;
            }

            foreach (JsonNode? child in kids)
            {
                if (ResolveFlatChildElement(elementsMap, child) is JsonObject cObj &&
                    cObj.TryGetPropertyValue(indexProp, out JsonNode? idxNode) &&
                    idxNode is JsonValue idxVal &&
                    idxVal.TryGetValue(out int idx) &&
                    idx >= 1)
                {
                    cObj[indexProp] = JsonValue.Create(idx - 1);
                }
            }

            return true;
        }

        bool changed = false;
        changed |= TryShiftAxis(children, "column", "columnSpan", CountStaticTracks(gridNode, "columns"));
        changed |= TryShiftAxis(children, "row", "rowSpan", CountStaticTracks(gridNode, "rows"));
        return changed;
    }

    internal static bool NormalizeUnicodeGlyphPropertyInPlace(JsonObject obj, string propertyName)
    {
        if (!obj.TryGetPropertyValue(propertyName, out JsonNode? propNode) ||
            propNode is not JsonValue propVal ||
            !propVal.TryGetValue(out string? raw) ||
            string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (!raw.Contains(@"\u", StringComparison.Ordinal) &&
            !raw.Contains(@"\U", StringComparison.Ordinal))
        {
            return false;
        }

        string unescaped = EscapedUnicodeHexRegex.Replace(
            raw,
            static m => ((char)ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToString());
        if (string.Equals(unescaped, raw, StringComparison.Ordinal))
        {
            return false;
        }

        obj[propertyName] = JsonValue.Create(unescaped);
        return true;
    }

    private static readonly string[] LifecycleExportNames =
    [
        "onInit", "onTick", "onSourceUpdated", "onSourceData", "onSettingsChange",
        "onHttpResponse", "onCommandResult", "onComplete", "onProgress", "onAiResult", "onDrop"
    ];

    [GeneratedRegex(@"(?:^|\n)\s*export\s+(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(")]
    private static partial Regex JsExportedFunctionRegex();

    [GeneratedRegex(@"(?:^|\n)\s*export\s+(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\b")]
    private static partial Regex JsExportedVariableRegex();

    [GeneratedRegex(@"(?:^|\n)\s*export\s*\{([^}]*)\}")]
    private static partial Regex JsNamedExportClauseRegex();

    public static string? PromoteMissingTopLevelScriptExports(string? manifestJson, string? logicJs)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(logicJs))
        {
            return logicJs;
        }

        JsonObject? root = ParseManifestObject(manifestJson);
        if (root is null)
        {
            return logicJs;
        }

        return PromoteMissingTopLevelScriptExports(root, logicJs, out _);
    }

    public static string? PromoteMissingTopLevelScriptExports(JsonObject? manifestRoot, string? logicJs, out bool changed)
    {
        changed = false;
        if (manifestRoot is null || string.IsNullOrWhiteSpace(logicJs))
        {
            return logicJs;
        }

        var eligibleNames = new HashSet<string>(LifecycleExportNames, StringComparer.Ordinal);
        CollectManifestScriptRunTargets(manifestRoot, eligibleNames);

        bool hadCrlf = logicJs.Contains("\r\n", StringComparison.Ordinal);
        string normalized = NormalizeNewlines(logicJs);

        var alreadyExported = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in JsExportedFunctionRegex().Matches(normalized))
        {
            alreadyExported.Add(m.Groups[1].Value);
        }
        foreach (Match m in JsExportedVariableRegex().Matches(normalized))
        {
            alreadyExported.Add(m.Groups[1].Value);
        }
        foreach (Match m in JsNamedExportClauseRegex().Matches(normalized))
        {
            foreach (string rawSpec in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int asIdx = rawSpec.IndexOf(" as ", StringComparison.Ordinal);
                if (asIdx > 0)
                {
                    alreadyExported.Add(rawSpec[..asIdx].Trim());
                    alreadyExported.Add(rawSpec[(asIdx + 4)..].Trim());
                }
                else
                {
                    alreadyExported.Add(rawSpec);
                }
            }
        }

        string[] lines = normalized.Split('\n');
        int braceDepth = 0;
        int parenDepth = 0;
        int bracketDepth = 0;
        char activeQuote = '\0';
        bool inBlockComment = false;
        Stack<int>? templateExprBraceDepths = null;
        bool anyPromoted = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && activeQuote == '\0' && !inBlockComment)
            {
                string trimmedStart = line.TrimStart();
                if (!trimmedStart.StartsWith("export ", StringComparison.Ordinal) &&
                    !trimmedStart.StartsWith("export\t", StringComparison.Ordinal))
                {
                    Match fnMatch = JsFunctionDeclarationRegex().Match(line);
                    if (!fnMatch.Success)
                    {
                        fnMatch = JsConstFunctionRegex().Match(line);
                    }

                    if (fnMatch.Success)
                    {
                        string fnName = fnMatch.Groups[1].Value;
                        if (eligibleNames.Contains(fnName) && alreadyExported.Add(fnName))
                        {
                            int indentLen = line.Length - trimmedStart.Length;
                            lines[i] = string.Concat(line.AsSpan(0, indentLen), "export ", line.AsSpan(indentLen));
                            anyPromoted = true;
                        }
                    }
                }
            }

            AdvanceJsLexicalState(
                line,
                ref braceDepth,
                ref parenDepth,
                ref bracketDepth,
                ref activeQuote,
                ref inBlockComment,
                ref templateExprBraceDepths);
        }

        if (!anyPromoted)
        {
            return logicJs;
        }

        string rewritten = string.Join('\n', lines);
        if (hadCrlf)
        {
            rewritten = rewritten.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        CustomWidgetScriptCompileResult rewrittenCompile = CustomWidgetScriptEngine.Compile(rewritten);
        if (rewrittenCompile.Status != CustomWidgetScriptStatus.EngineUnavailable && !rewrittenCompile.IsSuccess)
        {
            CustomWidgetScriptCompileResult origCompile = CustomWidgetScriptEngine.Compile(logicJs);
            if (origCompile.IsSuccess)
            {
                return logicJs;
            }
        }

        changed = true;
        return rewritten;
    }

    private static void CollectManifestScriptRunTargets(JsonNode? node, HashSet<string> targets)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue typeVal &&
                typeVal.TryGetValue(out string? typeStr) &&
                string.Equals(typeStr?.Trim(), "script.run", StringComparison.OrdinalIgnoreCase) &&
                obj["fn"] is JsonValue fnVal &&
                fnVal.TryGetValue(out string? fnStr) &&
                !string.IsNullOrWhiteSpace(fnStr))
            {
                targets.Add(fnStr.Trim());
            }

            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Value is JsonObject or JsonArray)
                {
                    CollectManifestScriptRunTargets(kv.Value, targets);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? child in arr)
            {
                if (child is JsonObject or JsonArray)
                {
                    CollectManifestScriptRunTargets(child, targets);
                }
            }
        }
    }

    private static void AdvanceJsLexicalState(
        string line,
        ref int braceDepth,
        ref int parenDepth,
        ref int bracketDepth,
        ref char activeQuote,
        ref bool inBlockComment,
        ref Stack<int>? templateExprBraceDepths)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (activeQuote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                    continue;
                }

                if (activeQuote == '`' && c == '$' && next == '{')
                {
                    templateExprBraceDepths ??= new Stack<int>(2);
                    templateExprBraceDepths.Push(braceDepth);
                    braceDepth++;
                    activeQuote = '\0';
                    i++;
                    continue;
                }

                if (c == activeQuote)
                {
                    activeQuote = '\0';
                }
                continue;
            }

            if (c == '/' && next == '/')
            {
                break;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                activeQuote = c;
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
            }
            else if (c == '}')
            {
                braceDepth = Math.Max(0, braceDepth - 1);
                if (templateExprBraceDepths is { Count: > 0 } && templateExprBraceDepths.Peek() == braceDepth)
                {
                    templateExprBraceDepths.Pop();
                    activeQuote = '`';
                }
            }
            else if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                parenDepth = Math.Max(0, parenDepth - 1);
            }
            else if (c == '[')
            {
                bracketDepth++;
            }
            else if (c == ']')
            {
                bracketDepth = Math.Max(0, bracketDepth - 1);
            }
        }

        if (activeQuote is '\'' or '"')
        {
            activeQuote = '\0';
        }
    }

    [GeneratedRegex(@"\\+u([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedUnicodeHexRegex { get; }
}
