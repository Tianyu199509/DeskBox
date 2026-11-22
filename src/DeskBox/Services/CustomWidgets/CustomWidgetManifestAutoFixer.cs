using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Deterministic, zero-reflection AOT-safe manifest self-healer that automatically wires
/// missing native <c>sources</c>, <c>collections</c>, and writable/bound <c>state</c> keys from the parsed AST.
/// Read-only <c>state.*</c> expression references are intentionally NOT auto-seeded so typos surface in validation.
/// </summary>
public static partial class CustomWidgetManifestAutoFixer
{
    private enum InferredStateDefaultKind
    {
        String,
        Boolean,
        Number
    }

    private static readonly FrozenSet<string> BooleanBindNodeTypes =
        FrozenSet.ToFrozenSet(["Toggle", "CheckBox"], StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> NumericBindNodeTypes =
        FrozenSet.ToFrozenSet(["Slider", "NumberBox", "Rating", "RadialGauge", "RingGauge"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bcollections\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionExprRefRegex();

    [GeneratedRegex(@"\btz\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TzFuncExprRegex();

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
        var expressionReferencedCollections = new HashSet<string>(StringComparer.Ordinal);
        var writableStateDefaults = new Dictionary<string, InferredStateDefaultKind>(StringComparer.Ordinal);
        var usedTokenNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool usesTzFunction = false;

        void RecordWritableState(string? rawKey, InferredStateDefaultKind kind)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return;
            }

            string key = rawKey.Trim();
            if (key.StartsWith("$state.", StringComparison.OrdinalIgnoreCase))
            {
                key = key["$state.".Length..];
            }
            else if (key.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
            {
                key = key["state.".Length..];
            }

            if (string.IsNullOrWhiteSpace(key) ||
                key.StartsWith('$') ||
                key.Contains('{') ||
                CustomWidgetRuntimeTokens.IsEphemeralStateKey(key))
            {
                return;
            }

            if (!writableStateDefaults.TryGetValue(key, out InferredStateDefaultKind existingKind) ||
                existingKind == InferredStateDefaultKind.String)
            {
                writableStateDefaults[key] = kind;
            }
        }

        void ScanExpressionForCapabilities(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            if (expr.Contains('$'))
            {
                if (expr.Contains("$sys.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$sys");
                if (expr.Contains("$weather.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$weather");
                if (expr.Contains("$calendar.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$calendar");
                if (expr.Contains("$device.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$device");
                if (expr.Contains("$clipboard.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$clipboard");
                if (expr.Contains("$drives.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$drives");
                if (expr.Contains("$music.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$music");
                if (expr.Contains("$todo.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$todo");
                if (expr.Contains("$clock.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$clock");
                if (expr.Contains("$ai.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$ai");
                if (expr.Contains("$pomodoro.", StringComparison.OrdinalIgnoreCase) ||
                    expr.Contains("$pomo.", StringComparison.OrdinalIgnoreCase))
                {
                    usedTokenNamespaces.Add("$pomodoro");
                }
                if (expr.Contains("$countdown.", StringComparison.OrdinalIgnoreCase)) usedTokenNamespaces.Add("$countdown");
            }

            if (!usesTzFunction &&
                expr.Contains("tz", StringComparison.OrdinalIgnoreCase) &&
                (expr.Contains("${", StringComparison.Ordinal) || expr.StartsWith("tz(", StringComparison.OrdinalIgnoreCase)) &&
                TzFuncExprRegex().IsMatch(expr))
            {
                usesTzFunction = true;
            }

            if (expr.Contains("collections.", StringComparison.Ordinal))
            {
                foreach (Match cm in CollectionExprRefRegex().Matches(expr))
                {
                    expressionReferencedCollections.Add(cm.Groups[1].Value);
                }
            }
        }

        CollectAstReferences(
            manifest.View,
            usedNodeTypes,
            usedActionTypes,
            referencedCollections,
            RecordWritableState,
            ScanExpressionForCapabilities);

        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            CollectAstReferences(
                pop,
                usedNodeTypes,
                usedActionTypes,
                referencedCollections,
                RecordWritableState,
                ScanExpressionForCapabilities);
        }

        if (manifest.Capsule is { } cap)
        {
            ScanExpressionForCapabilities(cap.Title);
            ScanExpressionForCapabilities(cap.Summary);
            ScanExpressionForCapabilities(cap.Glyph);
            ScanExpressionForCapabilities(cap.Badge);
            ScanExpressionForCapabilities(cap.BadgeIsWarning);
            ScanExpressionForCapabilities(cap.Progress);
            if (cap.PrimaryAction is { } pa)
            {
                ScanExpressionForCapabilities(pa.Icon);
                ScanExpressionForCapabilities(pa.Tooltip);
                CollectActionReferences(pa.OnClick, usedActionTypes, referencedCollections, RecordWritableState, ScanExpressionForCapabilities);
            }
        }

        if (manifest.OnDrop is not null)
        {
            ScanExpressionForCapabilities(manifest.OnDrop.Mode);
            CollectActionReferences(manifest.OnDrop.Actions, usedActionTypes, referencedCollections, RecordWritableState, ScanExpressionForCapabilities);
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            ScanExpressionForCapabilities(src.Url);
            ScanExpressionForCapabilities(src.City);
            ScanExpressionForCapabilities(src.Path);
            ScanExpressionForCapabilities(src.Command);
            ScanExpressionForCapabilities(src.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(src.TargetCollection) && !src.TargetCollection.Contains('{'))
            {
                referencedCollections.Add(src.TargetCollection);
            }
        }

        if (usedTokenNamespaces.Contains("$sys"))
        {
            RequireSource("systemMetrics", 2000);
        }

        if (usedTokenNamespaces.Contains("$weather"))
        {
            RequireSource("weather", 900_000);
            if (referencedCollections.Contains("forecast") || expressionReferencedCollections.Contains("forecast"))
            {
                referencedCollections.Add("forecast");
            }
        }

        if (usedTokenNamespaces.Contains("$calendar") || usedNodeTypes.Contains("CalendarGrid"))
        {
            RequireSource("calendar", 60_000);
        }

        if (usedTokenNamespaces.Contains("$device") || usedActionTypes.Contains("system.control"))
        {
            RequireSource("deviceState", 3000);
        }

        if (usedTokenNamespaces.Contains("$clipboard"))
        {
            RequireSource("clipboard", 2000);
        }

        if (usedTokenNamespaces.Contains("$drives") || existingSources.Contains("storageDrives"))
        {
            RequireSource("storageDrives", 15_000);
            if (referencedCollections.Contains("drives") || expressionReferencedCollections.Contains("drives"))
            {
                referencedCollections.Add("drives");
            }
        }

        if (usedTokenNamespaces.Contains("$music"))
        {
            RequireSource("music", 1000);
        }

        if (usedTokenNamespaces.Contains("$todo") || existingSources.Contains("todo"))
        {
            RequireSource("todo", 30_000);
            if (referencedCollections.Contains("todos") || expressionReferencedCollections.Contains("todos"))
            {
                referencedCollections.Add("todos");
            }
        }

        if (usedTokenNamespaces.Contains("$clock") || usesTzFunction)
        {
            RequireSource("clock", 1000);
        }

        if (usedTokenNamespaces.Contains("$ai") ||
            usedActionTypes.Contains("ai.refresh") ||
            usedActionTypes.Contains("ai.selectProvider"))
        {
            RequireSource("aiMonitor", 60_000);
        }

        if (usedTokenNamespaces.Contains("$pomodoro") ||
            usedActionTypes.Any(static a => a.StartsWith("pomodoro.", StringComparison.OrdinalIgnoreCase)))
        {
            RequireSource("pomodoro", 1000);
        }

        if (usedTokenNamespaces.Contains("$countdown") ||
            usedActionTypes.Any(static a => a.StartsWith("countdown.", StringComparison.OrdinalIgnoreCase)))
        {
            RequireSource("countdown", 60_000);
            referencedCollections.Add("events");
        }

        List<string> missingCollections = referencedCollections
            .Where(c => !string.IsNullOrWhiteSpace(c) && !manifest.Collections.ContainsKey(c))
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToList();

        List<(string Key, InferredStateDefaultKind Kind)> missingStateKeys = writableStateDefaults
            .Where(kv => !manifest.State.ContainsKey(kv.Key))
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => (kv.Key, kv.Value))
            .ToList();

        if (missingSources.Count == 0 &&
            missingCollections.Count == 0 &&
            missingStateKeys.Count == 0)
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

            bool hasSourcesProp = doc.RootElement.TryGetProperty("sources", out JsonElement existingSourcesEl);
            bool hasCollectionsProp = doc.RootElement.TryGetProperty("collections", out JsonElement existingColsEl);
            bool hasStateProp = doc.RootElement.TryGetProperty("state", out JsonElement existingStateEl);

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
                        foreach ((string sk, InferredStateDefaultKind skKind) in missingStateKeys)
                        {
                            WriteInferredStateDefault(writer, sk, skKind);
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
                        if (existingStateEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty sp in existingStateEl.EnumerateObject())
                            {
                                sp.WriteTo(writer);
                            }
                        }
                        foreach ((string sk, InferredStateDefaultKind skKind) in missingStateKeys)
                        {
                            WriteInferredStateDefault(writer, sk, skKind);
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
                        if (existingColsEl.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty cp in existingColsEl.EnumerateObject())
                            {
                                cp.WriteTo(writer);
                            }
                        }
                        else if (existingColsEl.ValueKind == JsonValueKind.Array)
                        {
                            var emittedExistingCols = new HashSet<string>(StringComparer.Ordinal);
                            foreach (JsonElement item in existingColsEl.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object)
                                {
                                    string? existingColName = null;
                                    if (item.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                    {
                                        existingColName = nameEl.GetString();
                                    }
                                    else if (item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                                    {
                                        existingColName = idEl.GetString();
                                    }

                                    if (!string.IsNullOrWhiteSpace(existingColName) && emittedExistingCols.Add(existingColName))
                                    {
                                        writer.WritePropertyName(existingColName);
                                        writer.WriteStartObject();
                                        bool wroteAnyMeta = false;
                                        foreach (JsonProperty colProp in item.EnumerateObject())
                                        {
                                            if (string.Equals(colProp.Name, "name", StringComparison.Ordinal) ||
                                                string.Equals(colProp.Name, "id", StringComparison.Ordinal))
                                            {
                                                continue;
                                            }
                                            colProp.WriteTo(writer);
                                            wroteAnyMeta = true;
                                        }
                                        if (!wroteAnyMeta)
                                        {
                                            writer.WriteNumber("maxItems", 200);
                                        }
                                        writer.WriteEndObject();
                                    }
                                }
                                else if (item.ValueKind == JsonValueKind.String)
                                {
                                    string? existingColName = item.GetString();
                                    if (!string.IsNullOrWhiteSpace(existingColName) && emittedExistingCols.Add(existingColName))
                                    {
                                        writer.WritePropertyName(existingColName);
                                        writer.WriteStartObject();
                                        writer.WriteNumber("maxItems", 200);
                                        writer.WriteEndObject();
                                    }
                                }
                            }
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
                        if (existingSourcesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement se in existingSourcesEl.EnumerateArray())
                            {
                                se.WriteTo(writer);
                            }
                        }
                        foreach ((string srcType, int? interval) in missingSources)
                        {
                            WriteAutoSourceObject(writer, srcType, interval);
                        }
                        writer.WriteEndArray();
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

    private static void WriteInferredStateDefault(
        Utf8JsonWriter writer,
        string key,
        InferredStateDefaultKind kind)
    {
        switch (kind)
        {
            case InferredStateDefaultKind.Boolean:
                writer.WriteBoolean(key, false);
                break;
            case InferredStateDefaultKind.Number:
                writer.WriteNumber(key, 0);
                break;
            default:
                writer.WriteString(key, string.Empty);
                break;
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
        else if (string.Equals(srcType, "weather", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteString("targetCollection", "forecast");
        }
        else if (string.Equals(srcType, "storageDrives", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteString("targetCollection", "drives");
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
        Action<string?, InferredStateDefaultKind> recordWritableState,
        Action<string?> scanExpr)
    {
        if (node is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Type))
        {
            nodeTypes.Add(node.Type);
        }

        scanExpr(node.When);
        scanExpr(node.Text);
        scanExpr(node.Title);
        scanExpr(node.Subtitle);
        scanExpr(node.Value);
        scanExpr(node.Progress);
        scanExpr(node.Tooltip);
        scanExpr(node.Placeholder);
        scanExpr(node.Src);
        scanExpr(node.Poster);
        scanExpr(node.Icon);
        scanExpr(node.Filter);
        scanExpr(node.Items);
        scanExpr(node.Collection);
        scanExpr(node.SuggestionsSource);
        scanExpr(node.Background);
        scanExpr(node.TextColor);
        scanExpr(node.BorderColor);
        scanExpr(node.OpacityExpression);
        if (node.EmptyState is { } es)
        {
            scanExpr(es.Title);
            scanExpr(es.Subtitle);
        }
        foreach (CustomWidgetTextSpanSpec span in node.Spans)
        {
            scanExpr(span.Text);
        }
        foreach (CustomWidgetOptionSpec opt in node.Options)
        {
            scanExpr(opt.Label);
        }
        foreach (CustomWidgetTableColumnSpec col in node.TableColumns)
        {
            scanExpr(col.Header);
        }
        foreach (CustomWidgetCanvasElementSpec ce in node.Elements)
        {
            scanExpr(ce.Text);
            scanExpr(ce.Data);
            scanExpr(ce.Points);
            scanExpr(ce.SweepAngle);
            scanExpr(ce.EndAngle);
            scanExpr(ce.RotationExpression);
        }

        if (!string.IsNullOrWhiteSpace(node.Bind))
        {
            string rawBind = node.Bind.Trim();
            if (!rawBind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase) &&
                !rawBind.StartsWith("settings.", StringComparison.OrdinalIgnoreCase) &&
                !rawBind.StartsWith("$item.", StringComparison.OrdinalIgnoreCase) &&
                !rawBind.StartsWith("item.", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rawBind, "$item", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rawBind, "$index", StringComparison.OrdinalIgnoreCase))
            {
                InferredStateDefaultKind bindKind = BooleanBindNodeTypes.Contains(node.Type ?? string.Empty)
                    ? InferredStateDefaultKind.Boolean
                    : NumericBindNodeTypes.Contains(node.Type ?? string.Empty)
                        ? InferredStateDefaultKind.Number
                        : InferredStateDefaultKind.String;
                recordWritableState(rawBind, bindKind);
            }
        }

        if ((string.Equals(node.Type, "List", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, "FlipView", StringComparison.OrdinalIgnoreCase) ||
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

        CollectActionReferences(node.OnClick, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnSubmit, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnChange, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnDropActions, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnRowClick, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnItemClick, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnReorder, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnInlineCommit, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnPointerDown, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnPointerMove, actionTypes, collections, recordWritableState, scanExpr);
        CollectActionReferences(node.OnPointerUp, actionTypes, collections, recordWritableState, scanExpr);
        foreach (CustomWidgetMenuItemSpec menuItem in node.MenuItems)
        {
            scanExpr(menuItem.Label);
            CollectActionReferences(menuItem.OnClick, actionTypes, collections, recordWritableState, scanExpr);
        }
        foreach (CustomWidgetMenuItemSpec ctxItem in node.ContextMenu)
        {
            scanExpr(ctxItem.Label);
            CollectActionReferences(ctxItem.OnClick, actionTypes, collections, recordWritableState, scanExpr);
        }

        CollectAstReferences(node.ItemTemplate, nodeTypes, actionTypes, collections, recordWritableState, scanExpr);
        CollectAstReferences(node.ActionButton, nodeTypes, actionTypes, collections, recordWritableState, scanExpr);
        CollectAstReferences(node.Pane, nodeTypes, actionTypes, collections, recordWritableState, scanExpr);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectAstReferences(h, nodeTypes, actionTypes, collections, recordWritableState, scanExpr);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectAstReferences(c, nodeTypes, actionTypes, collections, recordWritableState, scanExpr);
        }
    }

    private static void CollectActionReferences(
        IReadOnlyList<CustomWidgetActionSpec>? actions,
        HashSet<string> actionTypes,
        HashSet<string> collections,
        Action<string?, InferredStateDefaultKind> recordWritableState,
        Action<string?> scanExpr)
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

            scanExpr(act.Value);
            scanExpr(act.Text);
            scanExpr(act.Message);
            scanExpr(act.Title);
            scanExpr(act.Url);
            scanExpr(act.Path);
            scanExpr(act.Prompt);
            scanExpr(act.Guard);
            scanExpr(act.Arg);
            scanExpr(act.Body);

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
                InferredStateDefaultKind inferred = InferredStateDefaultKind.String;
                if (string.Equals(act.Type, "state.toggle", StringComparison.OrdinalIgnoreCase))
                {
                    inferred = InferredStateDefaultKind.Boolean;
                }
                else if (string.Equals(act.Type, "state.increment", StringComparison.OrdinalIgnoreCase))
                {
                    inferred = InferredStateDefaultKind.Number;
                }
                else if (string.Equals(act.Type, "state.set", StringComparison.OrdinalIgnoreCase))
                {
                    if (act.RawJsonValue is { ValueKind: JsonValueKind.True or JsonValueKind.False })
                    {
                        inferred = InferredStateDefaultKind.Boolean;
                    }
                    else if (act.RawJsonValue is { ValueKind: JsonValueKind.Number })
                    {
                        inferred = InferredStateDefaultKind.Number;
                    }
                }

                recordWritableState(act.Key, inferred);
            }

            if (act.StateMap is not null)
            {
                foreach (string mappedKey in act.StateMap.Keys)
                {
                    recordWritableState(mappedKey, InferredStateDefaultKind.String);
                }
            }

            CollectActionReferences(act.Then, actionTypes, collections, recordWritableState, scanExpr);
        }
    }
}
