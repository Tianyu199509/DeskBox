using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Deterministic, zero-reflection AOT-safe manifest self-healer that automatically wires
/// missing native <c>sources</c>, <c>collections</c>, and initial <c>state</c> keys when an LLM
/// or hand-written manifest uses built-in tokens/components without declaring their backing declarations.
/// </summary>
public static class CustomWidgetManifestAutoFixer
{
    private static readonly Regex StateRefRegex = new(@"\bstate\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> RuntimeManagedStateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiStatus", "aiError", "aiUpdatedAt",
        "networkStatus", "networkError",
        "sourceStatus", "sourceError", "sourceUpdatedAt", "lastUpdated",
        "feedTitle", "feedCount", "topHeadline",
        "transformStatus", "transformOutput", "transformError",
        "httpStatus", "httpOk", "httpError", "httpStatusCode", "httpUpdatedAt",
        "fileText", "fileLineCount", "fileContent", "fileStatus", "fileError", "fileWriteStatus", "fileWriteError",
        "commandStatus", "commandError", "commandExitCode", "commandStdout", "commandStderr"
    };

    private static bool IsRuntimeManagedStateKey(string key) =>
        RuntimeManagedStateKeys.Contains(key) ||
        key.StartsWith("sourceStatus.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("fileText.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("fileLineCount.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("commandStdout.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("commandStderr.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("commandExitCode.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("__tableSort.", StringComparison.OrdinalIgnoreCase);

    public static string AutoWireMissingCapabilities(string rawManifestJson)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson))
        {
            return rawManifestJson;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawManifestJson);
        if (parsed.Manifest is not { } manifest)
        {
            return rawManifestJson;
        }

        var existingSources = new HashSet<string>(
            manifest.Sources.Select(static s => s.Type),
            StringComparer.OrdinalIgnoreCase);
        var missingSources = new List<(string Type, int? IntervalMs)>();

        void RequireSource(string type, int? intervalMs = null)
        {
            if (existingSources.Add(type))
            {
                missingSources.Add((type, intervalMs));
            }
        }

        var usedNodeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedActionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedCollections = new HashSet<string>(StringComparer.Ordinal);
        var referencedStateKeys = new HashSet<string>(StringComparer.Ordinal);

        CollectAstReferences(
            manifest.View,
            usedNodeTypes,
            usedActionTypes,
            referencedCollections,
            referencedStateKeys);

        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            CollectAstReferences(
                pop,
                usedNodeTypes,
                usedActionTypes,
                referencedCollections,
                referencedStateKeys);
        }

        if (manifest.OnDrop is not null)
        {
            CollectActionReferences(manifest.OnDrop.Actions, usedActionTypes, referencedCollections, referencedStateKeys);
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (!string.IsNullOrWhiteSpace(src.TargetCollection) && !src.TargetCollection.Contains('{'))
            {
                referencedCollections.Add(src.TargetCollection);
            }
        }

        // 1. Native Sources auto-detection from tokens, composite components, and actions
        if (rawManifestJson.Contains("$sys.", StringComparison.OrdinalIgnoreCase) ||
            usedNodeTypes.Contains("GpuCard") ||
            usedNodeTypes.Contains("ProcessList"))
        {
            RequireSource("systemMetrics", 2000);
        }

        if (rawManifestJson.Contains("$weather.", StringComparison.OrdinalIgnoreCase))
        {
            RequireSource("weather", 900_000);
            if (rawManifestJson.Contains("\"forecast\"", StringComparison.Ordinal) ||
                rawManifestJson.Contains("collections.forecast", StringComparison.Ordinal))
            {
                referencedCollections.Add("forecast");
            }
        }

        if (rawManifestJson.Contains("$calendar.", StringComparison.OrdinalIgnoreCase) ||
            usedNodeTypes.Contains("CalendarGrid"))
        {
            RequireSource("calendar", 60_000);
        }

        if (rawManifestJson.Contains("$device.", StringComparison.OrdinalIgnoreCase) ||
            usedActionTypes.Contains("system.control"))
        {
            RequireSource("deviceState", 3000);
        }

        if (rawManifestJson.Contains("$clipboard.", StringComparison.OrdinalIgnoreCase))
        {
            RequireSource("clipboard", 2000);
        }

        if (rawManifestJson.Contains("$drives.", StringComparison.OrdinalIgnoreCase) ||
            existingSources.Contains("storageDrives"))
        {
            RequireSource("storageDrives", 15_000);
            if (rawManifestJson.Contains("\"drives\"", StringComparison.Ordinal) ||
                rawManifestJson.Contains("collections.drives", StringComparison.Ordinal))
            {
                referencedCollections.Add("drives");
            }
        }

        if (rawManifestJson.Contains("$music.", StringComparison.OrdinalIgnoreCase))
        {
            RequireSource("music", 1000);
        }

        if (rawManifestJson.Contains("$todo.", StringComparison.OrdinalIgnoreCase) ||
            existingSources.Contains("todo"))
        {
            RequireSource("todo", 30_000);
            if (rawManifestJson.Contains("\"todos\"", StringComparison.Ordinal) ||
                rawManifestJson.Contains("collections.todos", StringComparison.Ordinal))
            {
                referencedCollections.Add("todos");
            }
        }

        if (rawManifestJson.Contains("$clock.", StringComparison.OrdinalIgnoreCase) ||
            rawManifestJson.Contains("tz(", StringComparison.OrdinalIgnoreCase))
        {
            RequireSource("clock", 1000);
        }

        if (rawManifestJson.Contains("$ai.", StringComparison.OrdinalIgnoreCase) ||
            usedNodeTypes.Contains("AiFocusCard") ||
            usedNodeTypes.Contains("AiRadarList") ||
            usedNodeTypes.Contains("AiTokensCard") ||
            usedNodeTypes.Contains("AiTrendCard") ||
            usedActionTypes.Contains("ai.refresh") ||
            usedActionTypes.Contains("ai.selectProvider"))
        {
            RequireSource("aiMonitor", 60_000);
        }

        if (rawManifestJson.Contains("$pomodoro.", StringComparison.OrdinalIgnoreCase) ||
            usedNodeTypes.Contains("PomodoroCard") ||
            usedActionTypes.Any(static a => a.StartsWith("pomodoro.", StringComparison.OrdinalIgnoreCase)))
        {
            RequireSource("pomodoro", 1000);
        }

        if (rawManifestJson.Contains("$countdown.", StringComparison.OrdinalIgnoreCase) ||
            usedNodeTypes.Contains("CountdownHero") ||
            usedNodeTypes.Contains("CountdownList") ||
            usedNodeTypes.Contains("TimeProgressCard") ||
            usedActionTypes.Any(static a => a.StartsWith("countdown.", StringComparison.OrdinalIgnoreCase)))
        {
            RequireSource("countdown", 60_000);
            referencedCollections.Add("events");
        }

        foreach (Match m in StateRefRegex.Matches(rawManifestJson))
        {
            string key = m.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(key) && !IsRuntimeManagedStateKey(key))
            {
                referencedStateKeys.Add(key);
            }
        }

        List<string> missingCollections = referencedCollections
            .Where(c => !string.IsNullOrWhiteSpace(c) && !manifest.Collections.ContainsKey(c))
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToList();

        List<string> missingStateKeys = referencedStateKeys
            .Where(k => !string.IsNullOrWhiteSpace(k) && !IsRuntimeManagedStateKey(k) && !manifest.State.ContainsKey(k))
            .OrderBy(static k => k, StringComparer.Ordinal)
            .ToList();

        string? rootPaddingRaw = manifest.View.Padding?.Trim();
        bool needsRootPadding = string.IsNullOrWhiteSpace(rootPaddingRaw);

        if (missingSources.Count == 0 &&
            missingCollections.Count == 0 &&
            missingStateKeys.Count == 0 &&
            !needsRootPadding)
        {
            return rawManifestJson;
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
                return rawManifestJson;
            }

            bool hasSourcesProp = doc.RootElement.TryGetProperty("sources", out JsonElement existingSourcesEl) &&
                                  existingSourcesEl.ValueKind == JsonValueKind.Array;
            bool hasCollectionsProp = doc.RootElement.TryGetProperty("collections", out JsonElement existingColsEl) &&
                                      existingColsEl.ValueKind == JsonValueKind.Object;
            bool hasStateProp = doc.RootElement.TryGetProperty("state", out JsonElement existingStateEl) &&
                                existingStateEl.ValueKind == JsonValueKind.Object;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                writer.WriteStartObject();
                bool wroteState = false;
                bool wroteCollections = false;
                bool wroteSources = false;

                void EnsureInjectedBeforeView()
                {
                    if (!wroteState && !hasStateProp && missingStateKeys.Count > 0)
                    {
                        wroteState = true;
                        writer.WritePropertyName("state");
                        writer.WriteStartObject();
                        foreach (string sk in missingStateKeys)
                        {
                            writer.WriteString(sk, string.Empty);
                        }
                        writer.WriteEndObject();
                    }

                    if (!wroteCollections && !hasCollectionsProp && missingCollections.Count > 0)
                    {
                        wroteCollections = true;
                        writer.WritePropertyName("collections");
                        writer.WriteStartObject();
                        foreach (string col in missingCollections)
                        {
                            writer.WritePropertyName(col);
                            writer.WriteStartObject();
                            writer.WriteNumber("maxItems", 200);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                    }

                    if (!wroteSources && !hasSourcesProp && missingSources.Count > 0)
                    {
                        wroteSources = true;
                        writer.WritePropertyName("sources");
                        writer.WriteStartArray();
                        foreach ((string srcType, int? interval) in missingSources)
                        {
                            WriteAutoSourceObject(writer, srcType, interval);
                        }
                        writer.WriteEndArray();
                    }
                }

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "view", StringComparison.Ordinal) ||
                        string.Equals(prop.Name, "root", StringComparison.Ordinal) ||
                        string.Equals(prop.Name, "capsule", StringComparison.Ordinal))
                    {
                        EnsureInjectedBeforeView();
                    }

                    if (string.Equals(prop.Name, "state", StringComparison.Ordinal) &&
                        hasStateProp &&
                        missingStateKeys.Count > 0)
                    {
                        wroteState = true;
                        writer.WritePropertyName("state");
                        writer.WriteStartObject();
                        foreach (JsonProperty sp in existingStateEl.EnumerateObject())
                        {
                            sp.WriteTo(writer);
                        }
                        foreach (string sk in missingStateKeys)
                        {
                            writer.WriteString(sk, string.Empty);
                        }
                        writer.WriteEndObject();
                        continue;
                    }

                    if (string.Equals(prop.Name, "collections", StringComparison.Ordinal) &&
                        hasCollectionsProp &&
                        missingCollections.Count > 0)
                    {
                        wroteCollections = true;
                        writer.WritePropertyName("collections");
                        writer.WriteStartObject();
                        foreach (JsonProperty cp in existingColsEl.EnumerateObject())
                        {
                            cp.WriteTo(writer);
                        }
                        foreach (string col in missingCollections)
                        {
                            writer.WritePropertyName(col);
                            writer.WriteStartObject();
                            writer.WriteNumber("maxItems", 200);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject();
                        continue;
                    }

                    if (string.Equals(prop.Name, "sources", StringComparison.Ordinal) &&
                        hasSourcesProp &&
                        missingSources.Count > 0)
                    {
                        wroteSources = true;
                        writer.WritePropertyName("sources");
                        writer.WriteStartArray();
                        foreach (JsonElement se in existingSourcesEl.EnumerateArray())
                        {
                            se.WriteTo(writer);
                        }
                        foreach ((string srcType, int? interval) in missingSources)
                        {
                            WriteAutoSourceObject(writer, srcType, interval);
                        }
                        writer.WriteEndArray();
                        continue;
                    }

                    if (needsRootPadding &&
                        (string.Equals(prop.Name, "view", StringComparison.Ordinal) ||
                         string.Equals(prop.Name, "root", StringComparison.Ordinal)) &&
                        prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName(prop.Name);
                        writer.WriteStartObject();
                        bool wrotePad = false;
                        foreach (JsonProperty vp in prop.Value.EnumerateObject())
                        {
                            if (string.Equals(vp.Name, "padding", StringComparison.Ordinal))
                            {
                                writer.WriteString("padding", "12,10,12,12");
                                wrotePad = true;
                                continue;
                            }

                            if (!wrotePad && string.Equals(vp.Name, "children", StringComparison.Ordinal))
                            {
                                writer.WriteString("padding", "12,10,12,12");
                                wrotePad = true;
                            }

                            vp.WriteTo(writer);
                        }

                        if (!wrotePad)
                        {
                            writer.WriteString("padding", "12,10,12,12");
                        }

                        writer.WriteEndObject();
                        continue;
                    }

                    prop.WriteTo(writer);
                }

                EnsureInjectedBeforeView();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return rawManifestJson;
        }
    }

    private static void WriteAutoSourceObject(Utf8JsonWriter writer, string srcType, int? intervalMs)
    {
        writer.WriteStartObject();
        writer.WriteString("type", srcType);
        if (string.Equals(srcType, "todo", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteString("targetCollection", "todos");
        }
        if (intervalMs is > 0)
        {
            writer.WriteNumber("intervalMs", intervalMs.Value);
        }
        writer.WriteEndObject();
    }

    private static void CollectAstReferences(
        CustomWidgetNodeSpec? node,
        HashSet<string> nodeTypes,
        HashSet<string> actionTypes,
        HashSet<string> collections,
        HashSet<string> stateKeys)
    {
        if (node is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Type))
        {
            nodeTypes.Add(node.Type);
        }

        if (!string.IsNullOrWhiteSpace(node.Bind) &&
            !node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase) &&
            !node.Bind.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
        {
            string key = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                ? node.Bind["state.".Length..]
                : node.Bind;
            if (!string.IsNullOrWhiteSpace(key) && !key.Contains('{') && !IsRuntimeManagedStateKey(key))
            {
                stateKeys.Add(key);
            }
        }

        if ((string.Equals(node.Type, "List", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "Table", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "DonutChart", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "Chart", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "BarList", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "Sparkline", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "HeatmapGrid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "CalendarGrid", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(node.Collection ?? node.Items))
        {
            string rawCol = (node.Collection ?? node.Items)!.Trim();
            if (rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
            {
                rawCol = rawCol["collections.".Length..];
            }
            if (!string.IsNullOrWhiteSpace(rawCol) && !rawCol.Contains('{') && !rawCol.Contains('$'))
            {
                collections.Add(rawCol);
            }
        }

        if (string.Equals(node.Type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(node.SuggestionsSource))
        {
            string rawSug = node.SuggestionsSource.Trim();
            if (rawSug.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
            {
                rawSug = rawSug["collections.".Length..];
            }
            if (!string.IsNullOrWhiteSpace(rawSug) && !rawSug.Contains('{') && !rawSug.Contains('$'))
            {
                collections.Add(rawSug);
            }
        }

        CollectActionReferences(node.OnClick, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnSubmit, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnChange, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnDropActions, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnRowClick, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnItemClick, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnReorder, actionTypes, collections, stateKeys);
        CollectActionReferences(node.OnInlineCommit, actionTypes, collections, stateKeys);

        CollectAstReferences(node.ItemTemplate, nodeTypes, actionTypes, collections, stateKeys);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectAstReferences(h, nodeTypes, actionTypes, collections, stateKeys);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectAstReferences(c, nodeTypes, actionTypes, collections, stateKeys);
        }
    }

    private static void CollectActionReferences(
        IReadOnlyList<CustomWidgetActionSpec>? actions,
        HashSet<string> actionTypes,
        HashSet<string> collections,
        HashSet<string> stateKeys)
    {
        if (actions is null)
        {
            return;
        }

        foreach (CustomWidgetActionSpec act in actions)
        {
            if (!string.IsNullOrWhiteSpace(act.Type))
            {
                actionTypes.Add(act.Type);
            }

            if (!string.IsNullOrWhiteSpace(act.Collection) && !act.Collection.Contains('{'))
            {
                collections.Add(act.Collection);
            }

            if ((act.Type.StartsWith("state.", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(act.Type, "text.transform", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(act.Type, "http.request", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(act.Type, "command.run", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(act.Key))
            {
                string key = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? act.Key["state.".Length..]
                    : act.Key;
                if (!string.IsNullOrWhiteSpace(key) && !key.Contains('{') && !IsRuntimeManagedStateKey(key))
                {
                    stateKeys.Add(key);
                }
            }

            if (act.StateMap is not null)
            {
                foreach (string mappedKey in act.StateMap.Keys)
                {
                    string key = mappedKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? mappedKey["state.".Length..]
                        : mappedKey;
                    if (!string.IsNullOrWhiteSpace(key) && !key.Contains('{') && !IsRuntimeManagedStateKey(key))
                    {
                        stateKeys.Add(key);
                    }
                }
            }

            CollectActionReferences(act.Then, actionTypes, collections, stateKeys);
        }
    }
}
