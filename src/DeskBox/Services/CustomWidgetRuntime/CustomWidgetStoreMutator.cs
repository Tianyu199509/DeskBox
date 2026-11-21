using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Shared mutation engine for external/agent Custom Widget data pushes across both live
/// <see cref="ViewModels.CustomWidgetViewModel"/> instances and offline <see cref="CustomWidgetStorePayload"/> files.
/// </summary>
internal static class CustomWidgetStoreMutator
{
    internal static bool ApplyExternalDataPushToPayload(
        CustomWidgetStorePayload payload,
        CustomWidgetManifest? manifest,
        IReadOnlyDictionary<string, string>? stateUpdates,
        string? collectionName = null,
        string? collectionMode = null,
        IReadOnlyList<Dictionary<string, string>>? records = null,
        IReadOnlyDictionary<string, JsonElement>? stateJsonUpdates = null,
        IReadOnlyList<Dictionary<string, JsonElement>>? jsonRecords = null,
        string? idField = null,
        int? maxItemsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return ApplyExternalDataPush(
            setStructuredState: (key, jsonVal) => payload.StateJson[key] = jsonVal.Clone(),
            setFlatState: (key, strVal) => payload.State[key] = strVal,
            collections: payload.Collections,
            manifest: manifest,
            stateUpdates: stateUpdates,
            collectionName: collectionName,
            collectionMode: collectionMode,
            records: records,
            stateJsonUpdates: stateJsonUpdates,
            jsonRecords: jsonRecords,
            idField: idField,
            maxItemsOverride: maxItemsOverride);
    }

    internal static bool ApplyExternalDataPush(
        Action<string, JsonElement> setStructuredState,
        Action<string, string> setFlatState,
        IDictionary<string, List<CustomWidgetRecord>> collections,
        CustomWidgetManifest? manifest,
        IReadOnlyDictionary<string, string>? stateUpdates,
        string? collectionName = null,
        string? collectionMode = null,
        IReadOnlyList<Dictionary<string, string>>? records = null,
        IReadOnlyDictionary<string, JsonElement>? stateJsonUpdates = null,
        IReadOnlyList<Dictionary<string, JsonElement>>? jsonRecords = null,
        string? idField = null,
        int? maxItemsOverride = null)
    {
        bool changed = false;

        if (stateJsonUpdates is { Count: > 0 })
        {
            foreach ((string rawKey, JsonElement jsonVal) in stateJsonUpdates)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || jsonVal.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }

                string key = NormalizeStateKey(rawKey);
                if (key.Length == 0)
                {
                    continue;
                }

                setStructuredState(key, jsonVal);
                changed = true;
            }
        }
        else if (stateUpdates is { Count: > 0 })
        {
            foreach ((string rawKey, string val) in stateUpdates)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    continue;
                }

                string key = NormalizeStateKey(rawKey);
                if (key.Length == 0)
                {
                    continue;
                }

                if (CustomWidgetScriptEngine.TryParseStructuredJsonElement(val, out JsonElement parsedJson))
                {
                    setStructuredState(key, parsedJson);
                }
                else
                {
                    setFlatState(key, val ?? string.Empty);
                }

                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return changed;
        }

        string colName = collectionName.Trim();
        if (!collections.TryGetValue(colName, out List<CustomWidgetRecord>? list))
        {
            list = [];
            collections[colName] = list;
        }

        string mode = (collectionMode ?? "prepend").Trim().ToLowerInvariant();
        if (mode is "clear" or "replace")
        {
            if (list.Count > 0)
            {
                list.Clear();
                changed = true;
            }
        }

        if (mode == "clear")
        {
            return changed;
        }

        string resolvedIdField = string.IsNullOrWhiteSpace(idField) ? "id" : idField.Trim();
        int effectiveMaxItems = ResolveEffectiveMaxItems(manifest, colName, maxItemsOverride);

        if (jsonRecords is { Count: > 0 })
        {
            for (int i = 0; i < jsonRecords.Count; i++)
            {
                Dictionary<string, JsonElement> fields = jsonRecords[i];
                string? explicitId = ExtractJsonRecordId(fields, resolvedIdField);

                if (mode == "upsert" && explicitId is not null)
                {
                    CustomWidgetRecord? existing = FindMatchingRecord(list, resolvedIdField, explicitId);
                    if (existing is not null)
                    {
                        foreach ((string fk, JsonElement fv) in fields)
                        {
                            if (!string.Equals(fk, "id", StringComparison.Ordinal) &&
                                fv.ValueKind != JsonValueKind.Undefined)
                            {
                                existing.FieldsJson[fk] = fv.Clone();
                            }
                        }

                        existing.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                        changed = true;
                        continue;
                    }
                }

                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                var rec = new CustomWidgetRecord
                {
                    Id = explicitId ?? Guid.NewGuid().ToString("N")[..10],
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks
                };
                foreach ((string fk, JsonElement fv) in fields)
                {
                    if (!string.Equals(fk, "id", StringComparison.Ordinal) &&
                        fv.ValueKind != JsonValueKind.Undefined)
                    {
                        rec.FieldsJson[fk] = fv.Clone();
                    }
                }

                InsertRecord(list, rec, mode, i);
                changed = true;
            }

            TrimToMaxItems(list, effectiveMaxItems);
        }
        else if (records is { Count: > 0 })
        {
            for (int i = 0; i < records.Count; i++)
            {
                Dictionary<string, string> fields = records[i];
                string? explicitId = ExtractStringRecordId(fields, resolvedIdField);

                if (mode == "upsert" && explicitId is not null)
                {
                    CustomWidgetRecord? existing = FindMatchingRecord(list, resolvedIdField, explicitId);
                    if (existing is not null)
                    {
                        foreach ((string fk, string fv) in fields)
                        {
                            if (!string.Equals(fk, "id", StringComparison.Ordinal))
                            {
                                existing.Fields[fk] = fv ?? string.Empty;
                            }
                        }

                        existing.UpdatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                        changed = true;
                        continue;
                    }
                }

                long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                var rec = new CustomWidgetRecord
                {
                    Id = explicitId ?? Guid.NewGuid().ToString("N")[..10],
                    CreatedAtUtcTicks = nowTicks,
                    UpdatedAtUtcTicks = nowTicks
                };
                foreach ((string fk, string fv) in fields)
                {
                    if (!string.Equals(fk, "id", StringComparison.Ordinal))
                    {
                        rec.Fields[fk] = fv ?? string.Empty;
                    }
                }

                InsertRecord(list, rec, mode, i);
                changed = true;
            }

            TrimToMaxItems(list, effectiveMaxItems);
        }

        return changed;
    }

    internal static string NormalizeStateKey(string rawKey)
    {
        string trimmed = rawKey.Trim();
        if (trimmed.StartsWith("state.", StringComparison.Ordinal))
        {
            return trimmed["state.".Length..];
        }

        if (trimmed.StartsWith("$state.", StringComparison.Ordinal))
        {
            return trimmed["$state.".Length..];
        }

        return trimmed;
    }

    private static int ResolveEffectiveMaxItems(
        CustomWidgetManifest? manifest,
        string collectionName,
        int? maxItemsOverride)
    {
        if (maxItemsOverride is > 0)
        {
            return Math.Clamp(maxItemsOverride.Value, 1, 500);
        }

        if (manifest is not null &&
            manifest.Collections.TryGetValue(collectionName, out CustomWidgetCollectionSchema? schema))
        {
            return Math.Clamp(schema.MaxItems, 1, 500);
        }

        return 200;
    }

    private static string? ExtractJsonRecordId(
        IReadOnlyDictionary<string, JsonElement> fields,
        string idField)
    {
        if (fields.TryGetValue(idField, out JsonElement idEl) &&
            idEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            string candidate = CustomWidgetScriptEngine.JsonElementToDisplayString(idEl).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        if (!string.Equals(idField, "id", StringComparison.Ordinal) &&
            fields.TryGetValue("id", out JsonElement fallbackEl) &&
            fallbackEl.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            string fallback = CustomWidgetScriptEngine.JsonElementToDisplayString(fallbackEl).Trim();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    private static string? ExtractStringRecordId(
        IReadOnlyDictionary<string, string> fields,
        string idField)
    {
        if (fields.TryGetValue(idField, out string? idVal) && !string.IsNullOrWhiteSpace(idVal))
        {
            return idVal.Trim();
        }

        if (!string.Equals(idField, "id", StringComparison.Ordinal) &&
            fields.TryGetValue("id", out string? fallbackVal) &&
            !string.IsNullOrWhiteSpace(fallbackVal))
        {
            return fallbackVal.Trim();
        }

        return null;
    }

    private static CustomWidgetRecord? FindMatchingRecord(
        List<CustomWidgetRecord> list,
        string idField,
        string targetId)
    {
        bool checkCustomField = !string.Equals(idField, "id", StringComparison.Ordinal);
        for (int i = 0; i < list.Count; i++)
        {
            CustomWidgetRecord candidate = list[i];
            if (string.Equals(candidate.Id, targetId, StringComparison.Ordinal))
            {
                return candidate;
            }

            if (checkCustomField &&
                string.Equals(candidate.GetField(idField), targetId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void InsertRecord(
        List<CustomWidgetRecord> list,
        CustomWidgetRecord record,
        string mode,
        int batchIndex)
    {
        if (mode is "append" or "replace")
        {
            list.Add(record);
        }
        else if (mode == "upsert")
        {
            list.Insert(0, record);
        }
        else
        {
            list.Insert(batchIndex < list.Count ? batchIndex : list.Count, record);
        }
    }

    private static void TrimToMaxItems(List<CustomWidgetRecord> list, int maxItems)
    {
        if (list.Count > maxItems)
        {
            list.RemoveRange(maxItems, list.Count - maxItems);
        }
    }
}
