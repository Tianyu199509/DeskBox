using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskBox.Models;

/// <summary>
/// Normalizes Grid track definitions so bare column counts (<c>5</c> -> <c>*,*,*,*,*</c>),
/// CSS grid tokens (<c>1fr</c>, <c>repeat(4, 1fr)</c>, <c>120px</c>), and JSON track arrays
/// resolve consistently across parser validation, dry-run validation, and WinUI rendering.
/// </summary>
internal static partial class CustomWidgetGridTrackNormalizer
{
    [GeneratedRegex(@"\brepeat\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatPrefixRegex();

    public static bool TryNormalizeTrackSpecs(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        string trimmed = raw.Trim();
        if (trimmed.Contains("${", StringComparison.Ordinal))
        {
            normalized = trimmed;
            return true;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int bareCount) &&
            bareCount >= 1 &&
            bareCount <= 16)
        {
            normalized = string.Join(",", Enumerable.Repeat("*", bareCount));
            return true;
        }

        if (!TryExpandRepeatCalls(trimmed, out string expanded))
        {
            return false;
        }

        if (!TrySplitTopLevelTrackTokens(expanded, out List<string> rawTokens) ||
            rawTokens.Count == 0 ||
            rawTokens.Count > 24)
        {
            return false;
        }

        var result = new List<string>(rawTokens.Count);
        foreach (string rawToken in rawTokens)
        {
            if (!TryNormalizeSingleTrackToken(rawToken, out string normToken))
            {
                return false;
            }
            result.Add(normToken);
        }

        normalized = string.Join(",", result);
        return true;
    }

    private static bool TryExpandRepeatCalls(string input, out string expanded)
    {
        expanded = input;
        int searchStart = 0;
        while (searchStart < expanded.Length)
        {
            Match match = RepeatPrefixRegex().Match(expanded, searchStart);
            if (!match.Success)
            {
                break;
            }

            int openParenIdx = match.Index + match.Length - 1;
            int depth = 1;
            int closeParenIdx = -1;
            int topLevelCommaIdx = -1;

            for (int i = openParenIdx + 1; i < expanded.Length; i++)
            {
                char ch = expanded[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeParenIdx = i;
                        break;
                    }
                }
                else if (ch == ',' && depth == 1 && topLevelCommaIdx < 0)
                {
                    topLevelCommaIdx = i;
                }
            }

            if (closeParenIdx < 0 || topLevelCommaIdx < 0)
            {
                return false;
            }

            string countStr = expanded[(openParenIdx + 1)..topLevelCommaIdx].Trim();
            string unitStr = expanded[(topLevelCommaIdx + 1)..closeParenIdx].Trim();

            if (!int.TryParse(countStr, NumberStyles.None, CultureInfo.InvariantCulture, out int repCount) ||
                repCount < 1 ||
                repCount > 16 ||
                !TryNormalizeSingleTrackToken(unitStr, out string normalizedUnit))
            {
                return false;
            }

            string replacement = string.Join(",", Enumerable.Repeat(normalizedUnit, repCount));
            expanded = string.Concat(
                expanded.AsSpan(0, match.Index),
                replacement,
                expanded.AsSpan(closeParenIdx + 1));
            searchStart = match.Index + replacement.Length;
        }

        return true;
    }

    private static bool TrySplitTopLevelTrackTokens(string input, out List<string> tokens)
    {
        tokens = [];
        int depth = 0;
        int tokenStart = -1;

        for (int i = 0; i < input.Length; i++)
        {
            char ch = input[i];
            if (ch == '(')
            {
                if (tokenStart < 0)
                {
                    tokenStart = i;
                }
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
            else if (depth == 0 && (ch == ',' || ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n'))
            {
                if (tokenStart >= 0)
                {
                    string part = input[tokenStart..i].Trim();
                    if (part.Length > 0)
                    {
                        tokens.Add(part);
                    }
                    tokenStart = -1;
                }
            }
            else if (tokenStart < 0 && !char.IsWhiteSpace(ch))
            {
                tokenStart = i;
            }
        }

        if (depth != 0)
        {
            return false;
        }

        if (tokenStart >= 0)
        {
            string tail = input[tokenStart..].Trim();
            if (tail.Length > 0)
            {
                tokens.Add(tail);
            }
        }

        return true;
    }

    internal static bool TryNormalizeSingleTrackToken(string? rawToken, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        string token = rawToken.Trim();
        if (token.StartsWith("minmax(", StringComparison.OrdinalIgnoreCase) &&
            token.EndsWith(')'))
        {
            string inner = token[7..^1].Trim();
            int commaIdx = FindTopLevelCommaIndex(inner);
            if (commaIdx > 0)
            {
                string minPart = inner[..commaIdx].Trim();
                string maxPart = inner[(commaIdx + 1)..].Trim();
                if (IsValidMinMaxLowerBound(minPart) &&
                    TryNormalizeSingleTrackToken(maxPart, out string maxNormalized))
                {
                    normalized = maxNormalized;
                    return true;
                }
            }

            return false;
        }

        if (string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Auto";
            return true;
        }

        if (string.Equals(token, "*", StringComparison.Ordinal) ||
            string.Equals(token, "1*", StringComparison.Ordinal) ||
            string.Equals(token, "1fr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "fr", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "*";
            return true;
        }

        if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase))
        {
            string numStr = token[..^2].Trim();
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double weight) &&
                double.IsFinite(weight) &&
                weight > 0 &&
                weight <= 100)
            {
                normalized = Math.Abs(weight - 1.0) < 0.0001
                    ? "*"
                    : $"{weight.ToString("0.##", CultureInfo.InvariantCulture)}*";
                return true;
            }
            return false;
        }

        if (token.EndsWith("*", StringComparison.Ordinal))
        {
            string numStr = token[..^1].Trim();
            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double weight) &&
                double.IsFinite(weight) &&
                weight > 0 &&
                weight <= 100)
            {
                normalized = Math.Abs(weight - 1.0) < 0.0001
                    ? "*"
                    : $"{weight.ToString("0.##", CultureInfo.InvariantCulture)}*";
                return true;
            }
            return false;
        }

        string pxStr = token.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            ? token[..^2].Trim()
            : token;
        if (double.TryParse(pxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
            double.IsFinite(px) &&
            px > 0 &&
            px <= 4000)
        {
            normalized = px.ToString("0.##", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static int FindTopLevelCommaIndex(string inner)
    {
        int depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char ch = inner[i];
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return -1;
                }
            }
            else if (ch == ',' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsValidMinMaxLowerBound(string minPart)
    {
        if (string.IsNullOrWhiteSpace(minPart))
        {
            return false;
        }

        if (string.Equals(minPart, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(minPart, "min-content", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(minPart, "max-content", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string pxStr = minPart.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            ? minPart[..^2].Trim()
            : minPart;
        return double.TryParse(pxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
               double.IsFinite(px) &&
               px >= 0 &&
               px <= 4000;
    }

    public static string? TryExtractGridTrackProperty(JsonElement el, string propertyName, params ReadOnlySpan<string> fallbackProperties)
    {
        static string? ExtractElement(JsonElement prop)
        {
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (JsonElement item in prop.EnumerateArray())
                {
                    string? part = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Number => item.GetRawText(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        parts.Add(part.Trim());
                    }
                }
                if (parts.Count > 0)
                {
                    return string.Join(",", parts);
                }
            }
            else if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined)
            {
                string? scalar = prop.ValueKind == JsonValueKind.Number
                    ? prop.GetRawText()
                    : (prop.ValueKind == JsonValueKind.String ? prop.GetString() : null);
                if (!string.IsNullOrWhiteSpace(scalar))
                {
                    return scalar;
                }
            }

            return null;
        }

        if (el.TryGetProperty(propertyName, out JsonElement prop))
        {
            string? primary = ExtractElement(prop);
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }
        }

        foreach (string fallbackName in fallbackProperties)
        {
            if (!string.IsNullOrEmpty(fallbackName) &&
                el.TryGetProperty(fallbackName, out JsonElement fallbackEl))
            {
                string? fallback = ExtractElement(fallbackEl);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return fallback;
                }
            }
        }

        return null;
    }

    public static string NormalizeRuntimeTrackString(string? evaluated)
    {
        if (string.IsNullOrWhiteSpace(evaluated))
        {
            return string.Empty;
        }

        return TryNormalizeTrackSpecs(evaluated, out string normalized)
            ? normalized
            : evaluated.Trim();
    }
}


/// <summary>
/// AOT-safe JSON metadata stamper for <c>manifest.json</c> provenance and versioning fields.
/// Preserves all existing JSON properties while updating or inserting provenance keys.
/// </summary>
public static class CustomWidgetManifestMetadataStamper
{
    public static string StampProvenance(
        string rawManifestJson,
        string origin,
        bool isCustomized,
        string? forkedFromId = null,
        string? forkedFromVersion = null,
        string? updatedAtUtc = null,
        string? createdAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return rawManifestJson;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
        }
        catch
        {
            return rawManifestJson;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return rawManifestJson;
            }

            string normalizedOrigin = CustomWidgetOriginKinds.Normalize(origin);
            string nowIso = updatedAtUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            string? effectiveForkedId = forkedFromId;
            string? effectiveForkedVersion = forkedFromVersion;
            string? existingCreatedAt = string.IsNullOrWhiteSpace(createdAtUtc) ? null : createdAtUtc.Trim();

            if (doc.RootElement.TryGetProperty("forkedFromId", out JsonElement existingForkId) &&
                existingForkId.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(effectiveForkedId))
            {
                effectiveForkedId = existingForkId.GetString();
            }

            if (doc.RootElement.TryGetProperty("forkedFromVersion", out JsonElement existingForkVer) &&
                existingForkVer.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(effectiveForkedVersion))
            {
                effectiveForkedVersion = existingForkVer.GetString();
            }

            if (doc.RootElement.TryGetProperty("createdAtUtc", out JsonElement existingCreated) &&
                existingCreated.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(existingCreated.GetString()))
            {
                existingCreatedAt = existingCreated.GetString();
            }

            var managedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "origin",
                "isCustomized",
                "forkedFromId",
                "forkedFromVersion",
                "createdAtUtc",
                "updatedAtUtc"
            };

            var buffer = new ArrayBufferWriter<byte>(rawManifestJson.Length + 256);
            using (var writer = new Utf8JsonWriter(
                       buffer,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                bool injectedMetadata = false;

                void WriteMetadataBlock()
                {
                    if (injectedMetadata)
                    {
                        return;
                    }

                    injectedMetadata = true;
                    writer.WriteString("origin", normalizedOrigin);
                    writer.WriteBoolean("isCustomized", isCustomized);
                    if (!string.IsNullOrWhiteSpace(effectiveForkedId))
                    {
                        writer.WriteString("forkedFromId", effectiveForkedId);
                    }
                    if (!string.IsNullOrWhiteSpace(effectiveForkedVersion))
                    {
                        writer.WriteString("forkedFromVersion", effectiveForkedVersion);
                    }
                    writer.WriteString("createdAtUtc", existingCreatedAt ?? nowIso);
                    writer.WriteString("updatedAtUtc", nowIso);
                }

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (managedKeys.Contains(prop.Name))
                    {
                        continue;
                    }

                    prop.WriteTo(writer);

                    if (!injectedMetadata &&
                        (string.Equals(prop.Name, "icon", StringComparison.Ordinal) ||
                         string.Equals(prop.Name, "description", StringComparison.Ordinal)))
                    {
                        WriteMetadataBlock();
                    }
                }

                WriteMetadataBlock();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    public static bool ShouldAllowAutomaticOfficialUpgrade(string rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return true;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                rawManifestJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("isCustomized", out JsonElement customizedEl))
            {
                if (customizedEl.ValueKind == JsonValueKind.True ||
                    (customizedEl.ValueKind == JsonValueKind.String &&
                     string.Equals(customizedEl.GetString(), "true", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            if (!doc.RootElement.TryGetProperty("origin", out JsonElement originEl) ||
                originEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? origin = originEl.GetString()?.Trim();
            return string.Equals(origin, CustomWidgetOriginKinds.Official, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record CustomWidgetModuleEntry(
    string Id,
    string Label,
    bool Enabled,
    string? Icon = null);

public static class CustomWidgetModuleListHelper
{
    public static IReadOnlyList<CustomWidgetModuleEntry> ParseEntries(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions)
    {
        if (definedOptions.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return [];
            }

            var adHoc = new List<CustomWidgetModuleEntry>();
            foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (string id, bool? explicitState) = SplitToken(rawToken);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    adHoc.Add(new CustomWidgetModuleEntry(id, id, explicitState ?? true));
                }
            }
            return adHoc;
        }

        var optionMap = new Dictionary<string, CustomWidgetOptionSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetOptionSpec opt in definedOptions)
        {
            if (!string.IsNullOrWhiteSpace(opt.Value))
            {
                optionMap[opt.Value] = opt;
            }
        }

        var entries = new List<CustomWidgetModuleEntry>(definedOptions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasAnyToken = false;
        bool usesExplicitFlags = false;

        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (string id, bool? explicitState) = SplitToken(rawToken);
                if (explicitState.HasValue)
                {
                    usesExplicitFlags = true;
                }

                if (string.IsNullOrWhiteSpace(id) ||
                    !optionMap.TryGetValue(id, out CustomWidgetOptionSpec? opt) ||
                    !seen.Add(opt.Value))
                {
                    continue;
                }

                hasAnyToken = true;
                entries.Add(new CustomWidgetModuleEntry(
                    opt.Value,
                    opt.Label,
                    explicitState ?? true,
                    opt.Icon));
            }
        }

        foreach (CustomWidgetOptionSpec opt in definedOptions)
        {
            if (string.IsNullOrWhiteSpace(opt.Value) || !seen.Add(opt.Value))
            {
                continue;
            }

            bool defaultEnabled = !hasAnyToken || usesExplicitFlags;
            entries.Add(new CustomWidgetModuleEntry(opt.Value, opt.Label, defaultEnabled, opt.Icon));
        }

        if (entries.Count > 0 && entries.All(static e => !e.Enabled))
        {
            entries[0] = entries[0] with { Enabled = true };
        }

        return entries;
    }

    public static string SerializeEntries(IEnumerable<CustomWidgetModuleEntry> entries) =>
        string.Join(',', entries.Select(static e => $"{e.Id}:{(e.Enabled ? "1" : "0")}"));

    public static bool IsModuleEnabled(
        string? rawValue,
        string moduleId,
        IReadOnlyList<CustomWidgetOptionSpec>? definedOptions = null)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            return false;
        }

        if (definedOptions is { Count: > 0 })
        {
            IReadOnlyList<CustomWidgetModuleEntry> parsed = ParseEntries(rawValue, definedOptions);
            CustomWidgetModuleEntry? match = parsed.FirstOrDefault(
                e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            return match?.Enabled ?? true;
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        bool usesExplicitFlags = false;
        foreach (string rawToken in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (string id, bool? explicitState) = SplitToken(rawToken);
            if (explicitState.HasValue)
            {
                usesExplicitFlags = true;
            }
            if (string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase))
            {
                return explicitState ?? true;
            }
        }

        return usesExplicitFlags;
    }

    public static string SetModuleEnabled(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string moduleId,
        bool enabled)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        int idx = list.FindIndex(e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return SerializeEntries(list);
        }

        if (!enabled && list.Count(static e => e.Enabled) <= 1 && list[idx].Enabled)
        {
            // Keep at least one module visible on the widget.
            return SerializeEntries(list);
        }

        list[idx] = list[idx] with { Enabled = enabled };
        return SerializeEntries(list);
    }

    public static string MoveModule(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string moduleId,
        int delta)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        int idx = list.FindIndex(e => string.Equals(e.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || delta == 0)
        {
            return SerializeEntries(list);
        }

        int targetIdx = Math.Clamp(idx + delta, 0, list.Count - 1);
        if (targetIdx == idx)
        {
            return SerializeEntries(list);
        }

        CustomWidgetModuleEntry item = list[idx];
        list.RemoveAt(idx);
        list.Insert(targetIdx, item);
        return SerializeEntries(list);
    }

    public static string ReorderEnabledModule(
        string? rawValue,
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions,
        string draggedModuleId,
        int targetEnabledIndex)
    {
        var list = ParseEntries(rawValue, definedOptions).ToList();
        var enabledIndices = new List<int>();
        int draggedEnabledSlot = -1;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Enabled)
            {
                if (string.Equals(list[i].Id, draggedModuleId, StringComparison.OrdinalIgnoreCase))
                {
                    draggedEnabledSlot = enabledIndices.Count;
                }
                enabledIndices.Add(i);
            }
        }

        if (draggedEnabledSlot < 0 || enabledIndices.Count <= 1)
        {
            return SerializeEntries(list);
        }

        int clampedSlot = Math.Clamp(targetEnabledIndex, 0, enabledIndices.Count - 1);
        if (clampedSlot == draggedEnabledSlot)
        {
            return SerializeEntries(list);
        }

        var enabledItems = enabledIndices.Select(idx => list[idx]).ToList();
        CustomWidgetModuleEntry draggedItem = enabledItems[draggedEnabledSlot];
        enabledItems.RemoveAt(draggedEnabledSlot);
        enabledItems.Insert(clampedSlot, draggedItem);

        for (int s = 0; s < enabledIndices.Count; s++)
        {
            list[enabledIndices[s]] = enabledItems[s];
        }

        return SerializeEntries(list);
    }

    private static (string Id, bool? Enabled) SplitToken(string rawToken)
    {
        int colon = rawToken.IndexOf(':');
        if (colon < 0)
        {
            return (rawToken.Trim(), null);
        }

        string id = rawToken[..colon].Trim();
        string flag = rawToken[(colon + 1)..].Trim();
        bool enabled = !string.Equals(flag, "0", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(flag, "off", StringComparison.OrdinalIgnoreCase);
        return (id, enabled);
    }
}
