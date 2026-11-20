using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Hydrates widget state and collection records from preview <c>sampleData</c> JSON using
/// exact runtime string-coercion semantics, with deterministic 2-record fallback synthesis
/// when declared collections have no sample items yet.
/// </summary>
public static partial class CustomWidgetSampleDataHydrator
{
    [GeneratedRegex(@"(?:\$item|(?<![A-Za-z0-9_.])item)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ItemFieldAccessRegex();

    private static readonly System.Collections.Frozen.FrozenSet<string> ReservedItemMetaFields =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "id", "createdAt", "updatedAt", "createdAtUtcTicks"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryHydrate(
        CustomWidgetManifest manifest,
        string? sampleDataJson,
        out Dictionary<string, string> stateStrings,
        out Dictionary<string, JsonElement> stateJson,
        out Dictionary<string, List<CustomWidgetRecord>> collections,
        out bool usedSynthesizedFallback) =>
        TryHydrate(
            manifest,
            sampleDataJson,
            null,
            out stateStrings,
            out stateJson,
            out collections,
            out usedSynthesizedFallback);

    public static bool TryHydrate(
        CustomWidgetManifest manifest,
        string? sampleDataJson,
        string? logicJs,
        out Dictionary<string, string> stateStrings,
        out Dictionary<string, JsonElement> stateJson,
        out Dictionary<string, List<CustomWidgetRecord>> collections,
        out bool usedSynthesizedFallback,
        bool synthesizeFallbacks = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        stateStrings = new Dictionary<string, string>(manifest.State, StringComparer.Ordinal);
        stateJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string k, JsonElement el) in manifest.StateJson)
        {
            stateJson[k] = el.Clone();
            stateStrings[k] = ViewModels.CustomWidgetViewModel.JsonScalarToString(el);
        }

        collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        foreach (string colName in manifest.Collections.Keys)
        {
            collections[colName] = [];
        }

        bool hydratedExplicit = false;
        usedSynthesizedFallback = false;

        if (!string.IsNullOrWhiteSpace(sampleDataJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(
                    sampleDataJson,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("state", out JsonElement stateObj) &&
                        stateObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty prop in stateObj.EnumerateObject())
                        {
                            if (!string.IsNullOrEmpty(prop.Name))
                            {
                                stateJson[prop.Name] = prop.Value.Clone();
                                stateStrings[prop.Name] = ViewModels.CustomWidgetViewModel.JsonScalarToString(prop.Value);
                                hydratedExplicit = true;
                            }
                        }
                    }

                    JsonElement colsRoot = root.TryGetProperty("collections", out JsonElement colsObj) &&
                                           colsObj.ValueKind == JsonValueKind.Object
                        ? colsObj
                        : root;

                    foreach (JsonProperty colProp in colsRoot.EnumerateObject())
                    {
                        if (string.Equals(colProp.Name, "state", StringComparison.Ordinal) ||
                            colProp.Value.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var records = new List<CustomWidgetRecord>();
                        int idx = 0;
                        foreach (JsonElement itemEl in colProp.Value.EnumerateArray())
                        {
                            if (itemEl.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            DateTimeOffset createdUtc = DateTimeOffset.UtcNow.AddDays(-idx);
                            var rec = new CustomWidgetRecord
                            {
                                Id = $"sample-{idx + 1}"
                            };
                            foreach (JsonProperty fp in itemEl.EnumerateObject())
                            {
                                if (!string.IsNullOrEmpty(fp.Name) && fp.Value.ValueKind != JsonValueKind.Undefined)
                                {
                                    rec.FieldsJson[fp.Name] = fp.Value.Clone();
                                }

                                string strVal = ViewModels.CustomWidgetViewModel.JsonScalarToString(fp.Value);
                                if (IsSampleDateFieldName(fp.Name) &&
                                    DateTimeOffset.TryParse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedDate))
                                {
                                    createdUtc = parsedDate.ToUniversalTime();
                                }
                            }

                            rec.CreatedAtUtcTicks = createdUtc.UtcTicks;
                            rec.UpdatedAtUtcTicks = createdUtc.UtcTicks;
                            records.Add(rec);
                            idx++;
                        }

                        if (records.Count > 0)
                        {
                            collections[colProp.Name] = records;
                            hydratedExplicit = true;
                        }
                    }
                }
            }
            catch
            {
                // Keep defaults when sampleDataJson is malformed
            }
        }

        if (hydratedExplicit)
        {
            RebaseStaleSampleRecordDatesInPlace(collections);
        }

        // Synthesize deterministic 2-record fallback for declared collections left empty
        if (synthesizeFallbacks && manifest.Collections.Count > 0)
        {
            List<string> discoveredFields = ExtractReferencedItemFields(manifest, logicJs);
            foreach (string colName in manifest.Collections.Keys)
            {
                if (collections.TryGetValue(colName, out List<CustomWidgetRecord>? existing) &&
                    existing.Count > 0)
                {
                    continue;
                }

                usedSynthesizedFallback = true;
                var synth = new List<CustomWidgetRecord>(2);
                for (int r = 0; r < 2; r++)
                {
                    DateTimeOffset ts = DateTimeOffset.UtcNow.AddDays(-r);
                    var rec = new CustomWidgetRecord
                    {
                        Id = $"sample-{r + 1}",
                        CreatedAtUtcTicks = ts.UtcTicks,
                        UpdatedAtUtcTicks = ts.UtcTicks
                    };
                    rec.Fields["title"] = $"Sample {r + 1}";
                    rec.Fields["name"] = $"Sample {r + 1}";
                    foreach (string fn in discoveredFields)
                    {
                        rec.FieldsJson[fn] = InferSyntheticFieldJsonValue(fn, r);
                    }

                    synth.Add(rec);
                }

                collections[colName] = synth;
            }
        }

        return hydratedExplicit || usedSynthesizedFallback;
    }

    private static List<string> ExtractReferencedItemFields(CustomWidgetManifest manifest, string? logicJs)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);

        void ScanText(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return;
            }

            foreach (Match m in ItemFieldAccessRegex().Matches(s))
            {
                string field = m.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(field) && !ReservedItemMetaFields.Contains(field))
                {
                    fields.Add(field);
                }
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            ScanText(node.Text);
            ScanText(node.Title);
            ScanText(node.Subtitle);
            ScanText(node.Value);
            ScanText(node.Progress);
            ScanText(node.Tone);
            ScanText(node.When);
            ScanText(node.TitleField);
            ScanText(node.ValueField);
            ScanText(node.SubtitleField);
            if (!string.IsNullOrWhiteSpace(node.TitleField)) fields.Add(node.TitleField);
            if (!string.IsNullOrWhiteSpace(node.ValueField)) fields.Add(node.ValueField);
            if (!string.IsNullOrWhiteSpace(node.SubtitleField)) fields.Add(node.SubtitleField);
            foreach (CustomWidgetTextSpanSpec span in node.Spans)
            {
                ScanText(span.Text);
            }
        }

        ScanText(logicJs);
        return fields.ToList();
    }

    private static JsonElement InferSyntheticFieldJsonValue(string fieldName, int rowIndex)
    {
        string lower = fieldName.ToLowerInvariant();
        if (lower.StartsWith("is", StringComparison.Ordinal) ||
            lower.StartsWith("has", StringComparison.Ordinal) ||
            lower is "done" or "completed" or "checked" or "active" or "enabled" or "pinned" or "starred")
        {
            return CustomWidgetScriptEngine.CreateBooleanJsonElement(rowIndex == 0);
        }

        if (lower is "tags" or "labels" or "items")
        {
            using JsonDocument arrDoc = JsonDocument.Parse(rowIndex == 0 ? "[\"general\"]" : "[\"work\"]");
            return arrDoc.RootElement.Clone();
        }

        if (lower is "history" or "series" or "points")
        {
            using JsonDocument numArrDoc = JsonDocument.Parse("[12, 18, 24]");
            return numArrDoc.RootElement.Clone();
        }

        if (lower.Contains("date", StringComparison.Ordinal))
        {
            return CustomWidgetScriptEngine.CreateStringJsonElement(
                DateTimeOffset.Now.AddDays(-rowIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (lower.Contains("amount", StringComparison.Ordinal) ||
            lower.Contains("price", StringComparison.Ordinal) ||
            lower.Contains("cost", StringComparison.Ordinal) ||
            lower.Contains("score", StringComparison.Ordinal) ||
            lower.Contains("count", StringComparison.Ordinal) ||
            lower.Contains("qty", StringComparison.Ordinal) ||
            lower.Contains("total", StringComparison.Ordinal) ||
            lower.Contains("minutes", StringComparison.Ordinal) ||
            lower.Contains("hours", StringComparison.Ordinal) ||
            lower.Contains("duration", StringComparison.Ordinal) ||
            lower.Contains("progress", StringComparison.Ordinal) ||
            lower.Contains("percent", StringComparison.Ordinal) ||
            lower.Contains("value", StringComparison.Ordinal) ||
            lower.Contains("num", StringComparison.Ordinal) ||
            lower.Contains("weekday", StringComparison.Ordinal) ||
            lower.Contains("period", StringComparison.Ordinal))
        {
            return CustomWidgetScriptEngine.CreateNumberJsonElement(rowIndex == 0 ? 12.5 : 24.0);
        }

        return CustomWidgetScriptEngine.CreateStringJsonElement($"Sample {rowIndex + 1}");
    }

    internal static bool IsSampleDateFieldName(string fieldName) =>
        string.Equals(fieldName, "date", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "dateIso", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "createdAt", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "day", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "timestamp", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "dueDate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fieldName, "startDate", StringComparison.OrdinalIgnoreCase);

    internal static void RebaseStaleSampleRecordDatesInPlace(
        IDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset? nowOverride = null)
    {
        if (collections.Count == 0)
        {
            return;
        }

        DateTimeOffset nowLocal = (nowOverride ?? DateTimeOffset.Now).ToLocalTime();
        DateTimeOffset? maxExplicitDate = null;
        DateTimeOffset? maxCreatedDate = null;

        foreach (List<CustomWidgetRecord> records in collections.Values)
        {
            foreach (CustomWidgetRecord rec in records)
            {
                bool recHasExplicitField = false;
                foreach ((string fk, string fv) in rec.Fields)
                {
                    if (IsSampleDateFieldName(fk) &&
                        TryParseSampleDateValue(fv, nowLocal.Offset, out DateTimeOffset parsed, out _))
                    {
                        recHasExplicitField = true;
                        if (maxExplicitDate is null || parsed.Date > maxExplicitDate.Value.Date)
                        {
                            maxExplicitDate = parsed;
                        }
                    }
                }

                if (!recHasExplicitField && rec.CreatedAtUtcTicks > 0)
                {
                    DateTimeOffset createdLocal = new DateTimeOffset(rec.CreatedAtUtcTicks, TimeSpan.Zero).ToOffset(nowLocal.Offset);
                    if (maxCreatedDate is null || createdLocal.Date > maxCreatedDate.Value.Date)
                    {
                        maxCreatedDate = createdLocal;
                    }
                }
            }
        }

        DateTimeOffset? maxObservedDate = maxExplicitDate ?? maxCreatedDate;
        if (maxObservedDate is null)
        {
            return;
        }

        double ageDays = (nowLocal.Date - maxObservedDate.Value.Date).TotalDays;
        if (ageDays <= 25.0)
        {
            return;
        }

        TimeSpan shift = nowLocal.Date - maxObservedDate.Value.Date;
        foreach (List<CustomWidgetRecord> records in collections.Values)
        {
            foreach (CustomWidgetRecord rec in records)
            {
                bool shiftedField = false;
                foreach (string fk in rec.Fields.Keys.ToArray())
                {
                    if (!IsSampleDateFieldName(fk))
                    {
                        continue;
                    }

                    string rawVal = rec.Fields[fk];
                    if (TryParseSampleDateValue(rawVal, nowLocal.Offset, out DateTimeOffset parsed, out bool isDateOnly))
                    {
                        DateTimeOffset shifted = parsed.Add(shift);
                        rec.Fields[fk] = isDateOnly
                            ? shifted.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                            : shifted.ToString("O", CultureInfo.InvariantCulture);
                        shiftedField = true;
                    }
                }

                if (rec.CreatedAtUtcTicks > 0)
                {
                    DateTimeOffset createdUtc = new DateTimeOffset(rec.CreatedAtUtcTicks, TimeSpan.Zero);
                    double createdAgeDays = (nowLocal.Date - createdUtc.ToOffset(nowLocal.Offset).Date).TotalDays;
                    if (shiftedField || createdAgeDays > 25.0)
                    {
                        long shiftedTicks = createdUtc.Add(shift).UtcTicks;
                        rec.CreatedAtUtcTicks = shiftedTicks;
                        rec.UpdatedAtUtcTicks = shiftedTicks;
                    }
                }
            }
        }
    }

    private static bool TryParseSampleDateValue(
        string? rawValue,
        TimeSpan localOffset,
        out DateTimeOffset parsed,
        out bool isDateOnly)
    {
        parsed = default;
        isDateOnly = false;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string trimmed = rawValue.Trim();
        if (trimmed.Length == 10 &&
            DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateOnly))
        {
            parsed = new DateTimeOffset(dateOnly, localOffset);
            isDateOnly = true;
            return true;
        }

        if (trimmed.Length >= 8 &&
            DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset dto))
        {
            parsed = dto.ToOffset(localOffset);
            isDateOnly = false;
            return true;
        }

        return false;
    }
}
