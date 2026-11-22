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
    public static bool ShouldFoldManifest(string? manifestJson, string? targetNodePath = null, AiContextBudget? contextBudget = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return false;
        }

        int baseCharThreshold = contextBudget?.ManifestFoldCharThreshold ?? ManifestFoldCharThreshold;
        int baseLineThreshold = contextBudget?.ManifestFoldLineThreshold ?? ManifestFoldLineThreshold;
        int charThreshold = !string.IsNullOrWhiteSpace(targetNodePath)
            ? Math.Min(10_000, baseCharThreshold)
            : baseCharThreshold;
        int lineThreshold = !string.IsNullOrWhiteSpace(targetNodePath)
            ? Math.Min(260, baseLineThreshold)
            : baseLineThreshold;
        return manifestJson.Length > charThreshold || CountLines(manifestJson) > lineThreshold;
    }

    public static bool ShouldFoldScript(string? logicJs, AiContextBudget? contextBudget = null)
    {
        if (string.IsNullOrWhiteSpace(logicJs))
        {
            return false;
        }

        int charThreshold = contextBudget?.ScriptFoldCharThreshold ?? ScriptFoldCharThreshold;
        int lineThreshold = contextBudget?.ScriptFoldLineThreshold ?? ScriptFoldLineThreshold;
        return logicJs.Length > charThreshold || CountLines(logicJs) > lineThreshold;
    }

    public static bool IsWorkspaceFolded(string? manifestJson, string? logicJs, string? targetNodePath = null, AiContextBudget? contextBudget = null) =>
        ShouldFoldManifest(manifestJson, targetNodePath, contextBudget) || ShouldFoldScript(logicJs, contextBudget);

    public static string BuildManifestPromptSection(
        string? manifestJson,
        bool isChinese,
        string? targetNodePath = null,
        string? userInstruction = null,
        bool forceExpanded = false,
        AiContextBudget? contextBudget = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        string trimmed = manifestJson.Trim();
        if (forceExpanded || !ShouldFoldManifest(trimmed, targetNodePath, contextBudget))
        {
            string heading = isChinese
                ? "## 当前 `manifest.json`（唯一基准真值）"
                : "## Current `manifest.json` (Source of Truth)";
            return $"{heading}\n```json\n{trimmed}\n```";
        }

        JsonObject? root = ParseManifestObject(trimmed);
        if (root is null)
        {
            string heading = isChinese
                ? "## 当前 `manifest.json`（唯一基准真值）"
                : "## Current `manifest.json` (Source of Truth)";
            return $"{heading}\n```json\n{trimmed}\n```";
        }

        int lineCount = CountLines(trimmed);
        var sb = new StringBuilder(2048);
        sb.AppendLine(isChinese
            ? $"## 当前 `manifest.json`（唯一基准真值 — 已折叠 {lineCount} 行 / {trimmed.Length} 字符大纲）"
            : $"## Current `manifest.json` (Source of Truth — Folded {lineCount} lines / {trimmed.Length} chars)");
        sb.AppendLine(isChinese
            ? "> 为节省上下文并支持局部精确编辑，大组件已自动折叠为架构大纲；请优先使用 `edit_manifest` / `read_workspace`，避免全量重生成。"
            : "> Large manifest automatically folded into a structural skeleton. Use `read_workspace` to inspect details and `edit_manifest` for targeted edits.");

        AppendManifestOverviewSkeleton(sb, root);

        string? cleanSelector = ExtractCleanNodeSelector(targetNodePath);
        if (!string.IsNullOrWhiteSpace(cleanSelector) &&
            TryResolveNode(root, cleanSelector, out JsonNode? targetNode, out _, out _, out string resolvedPath, out _) &&
            targetNode is not null)
        {
            sb.AppendLine();
            sb.AppendLine(isChinese
                ? $"### 自动展开的目标聚焦子树 (`{resolvedPath}`)"
                : $"### Auto-Expanded Target Node Subtree (`{resolvedPath}`)");
            JsonNode pruned = PruneNodeForDisplay(targetNode, maxDepth: 3);
            sb.AppendLine("```json");
            sb.AppendLine(SerializeNode(pruned, indented: true));
            sb.AppendLine("```");

            if (TryFindEnclosingContainer(root, resolvedPath, out string enclosingPath, out JsonObject? enclosingObj) &&
                enclosingObj is not null)
            {
                sb.AppendLine();
                sb.AppendLine(isChinese
                    ? $"### 所属父容器结构 (`{enclosingPath}`)"
                    : $"### Enclosing Container Structure (`{enclosingPath}`)");
                JsonNode prunedParent = PruneNodeForDisplay(enclosingObj, maxDepth: 1);
                sb.AppendLine("```json");
                sb.AppendLine(SerializeNode(prunedParent, indented: true));
                sb.AppendLine("```");
            }
        }
        else if (!string.IsNullOrWhiteSpace(userInstruction) &&
                 root["elements"] is JsonObject elemsObj &&
                 TryFindBestMatchingElementByInstruction(elemsObj, userInstruction, out string matchedPath, out JsonNode? matchedNode) &&
                 matchedNode is not null)
        {
            sb.AppendLine();
            sb.AppendLine(isChinese
                ? $"### 自动展开的高相关候选元素 (`{matchedPath}`)"
                : $"### Auto-Expanded Relevant Candidate Element (`{matchedPath}`)");
            JsonNode pruned = PruneNodeForDisplay(matchedNode, maxDepth: 3);
            sb.AppendLine("```json");
            sb.AppendLine(SerializeNode(pruned, indented: true));
            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildScriptPromptSection(
        string? logicJs,
        bool isChinese,
        string? targetNodePath = null,
        string? userInstruction = null,
        bool forceExpanded = false,
        string? manifestJson = null,
        AiContextBudget? contextBudget = null)
    {
        if (string.IsNullOrWhiteSpace(logicJs))
        {
            return string.Empty;
        }

        string trimmed = logicJs.Trim();
        if (forceExpanded || !ShouldFoldScript(trimmed, contextBudget))
        {
            string heading = isChinese
                ? "## 当前 `logic.js`（唯一基准真值 —— 未修改脚本时无需重发；修改请用 `patch_script`）"
                : "## Current `logic.js` (Source of Truth — use `patch_script` for edits)";
            return $"{heading}\n```js\n{trimmed}\n```";
        }

        string normalized = NormalizeNewlines(trimmed);
        string[] lines = normalized.Split('\n');
        List<ScriptSymbolSpan> symbols = IndexScriptSymbols(lines);

        var sb = new StringBuilder(1600);
        sb.AppendLine(isChinese
            ? $"## 当前 `logic.js`（已折叠 {lines.Length} 行 / {trimmed.Length} 字符符号索引）"
            : $"## Current `logic.js` (Folded Symbol Index — {lines.Length} lines / {trimmed.Length} chars)");
        sb.AppendLine(isChinese
            ? "> 请用 `read_workspace(section=\"script\", symbol=...)` 读取函数源码，并用 `patch_script` 提供精确 SEARCH/REPLACE 块修改。"
            : "> Use `read_workspace(section=\"script\", symbol=...)` to inspect a function body and `patch_script` with SEARCH/REPLACE hunks to edit.");

        if (symbols.Count > 0)
        {
            sb.AppendLine("Functions / Symbols:");
            foreach (ScriptSymbolSpan sym in symbols.Take(32))
            {
                sb.Append("- `").Append(sym.Name).Append("` (L").Append(sym.StartLine).Append("-L").Append(sym.EndLine).AppendLine(")");
            }
        }

        List<ScriptSymbolSpan> focusSymbols = PickRelevantSymbols(
            symbols,
            lines,
            targetNodePath,
            userInstruction,
            manifestJson);
        int remainingBudget = 90;
        foreach (ScriptSymbolSpan focusSym in focusSymbols)
        {
            if (remainingBudget <= 0)
            {
                break;
            }

            sb.AppendLine();
            sb.AppendLine($"### Focused Symbol: `{focusSym.Name}` (L{focusSym.StartLine}-L{focusSym.EndLine})");
            sb.AppendLine("```js");
            int start = Math.Max(1, focusSym.StartLine);
            int maxSpan = focusSymbols.Count > 1 ? Math.Min(remainingBudget, 65) : 70;
            int end = Math.Min(lines.Length, Math.Min(focusSym.EndLine, start + maxSpan - 1));
            for (int l = start; l <= end; l++)
            {
                sb.Append("L").Append(l).Append(": ").AppendLine(lines[l - 1]);
            }
            sb.AppendLine("```");
            remainingBudget -= (end - start + 1);
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildLocalizedRepairFeedback(
        IReadOnlyList<CustomWidgetValidationDiagnostic> diagnostics,
        string? manifestJson,
        string? logicJs,
        IReadOnlyList<string>? preFlightNotes = null,
        string? extraSummary = null,
        bool isChinese = false)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine(isChinese
            ? "### 校验失败（已提供局部出错片段与修复定位）"
            : "### Validation Failed (Localized Failure Diagnostics)");

        if (!string.IsNullOrWhiteSpace(extraSummary))
        {
            sb.AppendLine(extraSummary.Trim());
        }

        JsonObject? root = ParseManifestObject(manifestJson);
        string[]? scriptLines = string.IsNullOrWhiteSpace(logicJs)
            ? null
            : NormalizeNewlines(logicJs).Split('\n');

        List<CustomWidgetValidationDiagnostic> errors = diagnostics
            .Where(static d => d.IsError)
            .Take(4)
            .ToList();

        if (errors.Count == 0 && diagnostics.Count > 0)
        {
            errors = diagnostics.Take(4).ToList();
        }

        var combinedDiagText = new StringBuilder();
        foreach (CustomWidgetValidationDiagnostic diag in errors)
        {
            sb.Append("- [").Append(diag.Path).Append("] ").AppendLine(diag.Message);
            combinedDiagText.Append(diag.Path).Append(' ').Append(diag.Message).Append(' ');

            int? scriptLine = TryExtractScriptDiagnosticLine(diag.Path, diag.Message);
            if (scriptLine.HasValue && scriptLines is { Length: > 0 })
            {
                AppendScriptWindow(sb, scriptLines, scriptLine.Value, radius: 12);
                continue;
            }

            if (root is not null && !string.IsNullOrWhiteSpace(diag.Path))
            {
                if (TryResolveNearestSubtree(root, diag.Path, out string resolvedPath, out JsonNode? resolvedNode) &&
                    resolvedNode is not null)
                {
                    JsonNode pruned = PruneNodeForDisplay(resolvedNode, maxDepth: 3);
                    string snippet = SerializeNodeWithLineCap(pruned, maxLines: 60);
                    sb.AppendLine($"  Nearest manifest subtree (`{resolvedPath}`):");
                    sb.AppendLine("```json");
                    sb.AppendLine(snippet);
                    sb.AppendLine("```");
                }
            }
        }

        if (preFlightNotes is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(isChinese ? "### 预检问题" : "### Pre-Flight Issues");
            foreach (string note in preFlightNotes.Take(4))
            {
                sb.AppendLine(note);
                combinedDiagText.Append(note).Append(' ');
            }
        }

        IReadOnlyList<CustomWidgetCapabilitySpec> backfill =
            CustomWidgetCapabilityRegistry.FindBackfillCapabilitiesForRepair(manifestJson, errors)
                .Take(2)
                .ToList();
        if (backfill.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock([], backfill));
        }

        if (!string.IsNullOrWhiteSpace(manifestJson))
        {
            string renderTruth = BuildCompiledRenderTruthSummary(
                manifestJson,
                logicJs,
                locales: null,
                sampleDataJson: null,
                isChinese: isChinese);
            if (!string.IsNullOrWhiteSpace(renderTruth))
            {
                sb.AppendLine();
                sb.AppendLine(renderTruth);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string BuildCompiledRenderTruthSummary(
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales = null,
        string? sampleDataJson = null,
        bool isChinese = false)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        if (parsed.Manifest is null)
        {
            return string.Empty;
        }

        CustomWidgetManifest manifest = parsed.Manifest;
        CustomWidgetManifest effectiveManifest = locales is { IsEmpty: false }
            ? manifest.WithLocales(locales)
            : manifest;
        IReadOnlyDictionary<string, string> i18nTable = effectiveManifest.ResolveI18nDictionary(isChinese ? "zh-CN" : "en-US");

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition def in manifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(def.Key) &&
                !string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                settings[def.Key] = def.DefaultValue ?? string.Empty;
            }
        }

        CustomWidgetSampleDataHydrator.TryHydrate(
            manifest,
            sampleDataJson,
            logicJs,
            out Dictionary<string, string> state,
            out Dictionary<string, JsonElement> stateJson,
            out Dictionary<string, List<CustomWidgetRecord>> collections,
            out bool usedSynthFallback);

        var computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string scriptStatusLine = isChinese ? "脚本：无 (纯声明式)" : "Script: none (pure declarative)";

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            CustomWidgetScriptCompileResult compileRes = CustomWidgetScriptEngine.Compile(logicJs);
            if (!compileRes.IsSuccess || compileRes.Compiled is null)
            {
                scriptStatusLine = isChinese
                    ? $"脚本编译失败 (L{compileRes.ErrorLine ?? 0}): {compileRes.ErrorMessage}"
                    : $"Script compile FAILED (L{compileRes.ErrorLine ?? 0}): {compileRes.ErrorMessage}";
            }
            else if (!compileRes.Compiled.ExportedFunctions.Contains("onInit"))
            {
                scriptStatusLine = isChinese
                    ? $"脚本已编译 (exports: {string.Join(", ", compileRes.Compiled.ExportedFunctions)}; 无 onInit)"
                    : $"Script compiled (exports: {string.Join(", ", compileRes.Compiled.ExportedFunctions)}; no onInit)";
            }
            else
            {
                CustomWidgetScriptExecutionResult dryRun = CustomWidgetScriptEngine.Execute(
                    compileRes.Compiled,
                    "onInit",
                    new CustomWidgetScriptExecutionInput
                    {
                        State = state,
                        StateJson = stateJson,
                        Settings = settings,
                        Collections = collections,
                        Computed = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                        I18n = i18nTable,
                        CultureName = isChinese ? "zh-CN" : "en-US",
                        CallKind = CustomWidgetScriptCallKind.Lifecycle,
                        TimeoutMs = 80
                    });

                if (!dryRun.IsSuccess)
                {
                    scriptStatusLine = isChinese
                        ? $"onInit 冷启动干跑失败 (L{dryRun.ErrorLine ?? 0}): {dryRun.ErrorMessage}"
                        : $"onInit dry-run FAILED (L{dryRun.ErrorLine ?? 0}): {dryRun.ErrorMessage}";
                }
                else
                {
                    foreach (string rk in dryRun.StateRemoved)
                    {
                        state.Remove(rk);
                        stateJson.Remove(rk);
                    }
                    foreach ((string sk, string sv) in dryRun.StateSet)
                    {
                        state[sk] = sv;
                    }
                    foreach ((string sk, JsonElement sEl) in dryRun.StateSetJson)
                    {
                        stateJson[sk] = sEl.Clone();
                    }
                    foreach ((string ck, JsonElement cEl) in dryRun.Computed)
                    {
                        computed[ck] = cEl.Clone();
                    }

                    var computedSummaryParts = new List<string>(8);
                    foreach ((string ck, JsonElement cEl) in computed.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Take(8))
                    {
                        string valDesc = cEl.ValueKind switch
                        {
                            JsonValueKind.Array => $"array[{cEl.GetArrayLength()}]",
                            JsonValueKind.Object => "object",
                            JsonValueKind.String => FormatShortRenderTruthString(cEl.GetString()),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            JsonValueKind.Number => cEl.GetRawText(),
                            _ => "null"
                        };
                        computedSummaryParts.Add($"{ck}={valDesc}");
                    }

                    string computedText = computedSummaryParts.Count > 0
                        ? string.Join(", ", computedSummaryParts)
                        : "(empty)";
                    scriptStatusLine = isChinese
                        ? $"onInit 干跑通过 -> $computed: {computedText}"
                        : $"onInit dry-run ok -> $computed: {computedText}";
                }
            }
        }

        int totalRenderedNodes = 0;
        var emptyContainerNotes = new List<string>(4);
        var dataBoundNotes = new List<string>(4);
        var tabPageNotes = new List<string>(4);

        static string ResolveShortLabel(CustomWidgetNodeSpec n, IReadOnlyDictionary<string, string> i18n, string fallback)
        {
            string? raw = !string.IsNullOrWhiteSpace(n.Title) ? n.Title : n.Text;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            string resolved = raw.Trim();
            if (resolved.StartsWith("${$i18n.", StringComparison.OrdinalIgnoreCase) &&
                resolved.EndsWith('}') &&
                resolved.Length > 8)
            {
                string key = resolved[8..^1];
                if (i18n.TryGetValue(key, out string? locVal) && !string.IsNullOrWhiteSpace(locVal))
                {
                    resolved = locVal;
                }
            }

            return resolved.Length > 18 ? resolved[..18] + "…" : resolved;
        }

        int ResolveBoundItemCount(CustomWidgetNodeSpec node)
        {
            if (!string.IsNullOrWhiteSpace(node.Items))
            {
                string binding = node.Items.Trim();
                if (binding.Contains("computed.", StringComparison.OrdinalIgnoreCase) ||
                    binding.Contains("state.", StringComparison.OrdinalIgnoreCase))
                {
                    return CustomWidgetScriptEngine.ProjectComputedCollection(binding, computed, stateJson).Count;
                }
            }

            string? colName = node.Collection?.Trim();
            if (string.IsNullOrWhiteSpace(colName) &&
                !string.IsNullOrWhiteSpace(node.Items) &&
                node.Items.Contains("collections.", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = node.Items.Trim();
                if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
                {
                    trimmed = trimmed[2..^1].Trim();
                }
                if (trimmed.StartsWith("$collections.", StringComparison.OrdinalIgnoreCase))
                {
                    colName = trimmed["$collections.".Length..];
                }
                else if (trimmed.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    colName = trimmed["collections.".Length..];
                }
            }

            if (!string.IsNullOrWhiteSpace(colName) &&
                collections.TryGetValue(colName, out List<CustomWidgetRecord>? recs))
            {
                return recs.Count;
            }

            return -1;
        }

        void InspectNode(CustomWidgetNodeSpec? node, string fallbackPath, int depth)
        {
            if (node is null)
            {
                return;
            }

            totalRenderedNodes++;
            string nodePath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : fallbackPath;
            string nodeRef = !string.IsNullOrWhiteSpace(node.Id) ? $"{nodePath}#{node.Id.Trim()}" : nodePath;
            string type = node.Type?.Trim() ?? "Stack";
            bool hasDynamic = !string.IsNullOrWhiteSpace(node.Items) ||
                              !string.IsNullOrWhiteSpace(node.Collection) ||
                              node.ItemTemplate is not null;

            if (string.Equals(type, "Tabs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "FlipView", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < node.Children.Count && tabPageNotes.Count < 5; i++)
                {
                    CustomWidgetNodeSpec page = node.Children[i];
                    string pageLabel = ResolveShortLabel(page, i18nTable, $"#{i}");
                    bool pageEmptyContainer = (string.Equals(page.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(page.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(page.Type, "Scroll", StringComparison.OrdinalIgnoreCase)) &&
                                              page.Children.Count == 0 &&
                                              string.IsNullOrWhiteSpace(page.Items) &&
                                              string.IsNullOrWhiteSpace(page.Collection) &&
                                              page.ItemTemplate is null;
                    string note = pageEmptyContainer
                        ? $"[{i}:\"{pageLabel}\" -> {page.Type}(0 children ⚠️ EMPTY)]"
                        : $"[{i}:\"{pageLabel}\" -> {page.Type}(children={page.Children.Count})]";
                    tabPageNotes.Add(note);
                }
            }

            if (depth == 0 && node.Children.Count == 0 && !hasDynamic &&
                (string.Equals(type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "Scroll", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "Tabs", StringComparison.OrdinalIgnoreCase)))
            {
                emptyContainerNotes.Add($"{nodeRef} ({type}: 0 children)");
            }
            else if (string.Equals(type, "Scroll", StringComparison.OrdinalIgnoreCase) &&
                     (node.Children.Count == 0 ||
                      (node.Children.Count == 1 &&
                       node.Children[0].Children.Count == 0 &&
                       string.IsNullOrWhiteSpace(node.Children[0].Items) &&
                       string.IsNullOrWhiteSpace(node.Children[0].Collection) &&
                       node.Children[0].ItemTemplate is null)))
            {
                emptyContainerNotes.Add($"{nodeRef} (Scroll -> 0 children ⚠️ EMPTY)");
            }
            else if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) &&
                     (!string.IsNullOrWhiteSpace(node.Columns) || !string.IsNullOrWhiteSpace(node.Rows)) &&
                     node.Children.Count == 0 &&
                     node.ItemTemplate is null)
            {
                emptyContainerNotes.Add($"{nodeRef} (Grid columns=\"{node.Columns}\" -> 0 rendered children ⚠️ EMPTY)");
            }

            if (string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "List", StringComparison.OrdinalIgnoreCase))
            {
                int count = ResolveBoundItemCount(node);
                if (count >= 0 && dataBoundNotes.Count < 4)
                {
                    string srcExpr = !string.IsNullOrWhiteSpace(node.Items)
                        ? node.Items.Trim()
                        : $"collections.{node.Collection}";
                    string emptyFlag = count == 0 && node.Children.Count == 0 ? " ⚠️ 0 items" : string.Empty;
                    dataBoundNotes.Add($"{type}@{nodeRef} ({srcExpr} -> {count} item(s){emptyFlag})");
                }
            }

            InspectNode(node.ItemTemplate, $"{nodePath}.itemTemplate", depth + 1);
            InspectNode(node.ActionButton, $"{nodePath}.actionButton", depth + 1);
            InspectNode(node.Pane, $"{nodePath}.pane", depth + 1);
            for (int i = 0; i < node.Children.Count; i++)
            {
                InspectNode(node.Children[i], $"{nodePath}.children[{i}]", depth + 1);
            }
        }

        string rootFallbackPath = !string.IsNullOrWhiteSpace(manifest.View.Id)
            ? $"#{manifest.View.Id.Trim()}"
            : "$";
        InspectNode(manifest.View, rootFallbackPath, 0);

        var sb = new StringBuilder(512);
        sb.AppendLine(isChinese
            ? "### 编译后渲染真值快照 (Compiled Render-Truth)"
            : "### Compiled Render-Truth Snapshot");
        sb.AppendLine($"- View tree: `{manifest.View.Type}` root (topChildren={manifest.View.Children.Count}, totalNodes={totalRenderedNodes})");
        if (tabPageNotes.Count > 0)
        {
            sb.AppendLine($"- Tabs/Flip pages: {string.Join(", ", tabPageNotes)}");
        }
        if (emptyContainerNotes.Count > 0)
        {
            sb.AppendLine($"- ⚠️ Empty containers rendered blank: {string.Join("; ", emptyContainerNotes.Take(4))}");
        }
        if (dataBoundNotes.Count > 0)
        {
            sb.AppendLine($"- Data-bound lists/grids: {string.Join("; ", dataBoundNotes)}");
        }

        if (manifest.Popovers.Count > 0)
        {
            string popList = string.Join(", ", manifest.Popovers.Select(static p =>
                $"{(!string.IsNullOrWhiteSpace(p.PopoverId) ? p.PopoverId : p.Id)}(children={p.Children.Count})"));
            sb.AppendLine($"- Registered root popovers ({manifest.Popovers.Count}): {popList}");
        }
        else
        {
            sb.AppendLine("- Registered root popovers (0): (none)");
        }

        if (manifest.Collections.Count > 0)
        {
            string colSummary = string.Join(", ", manifest.Collections.Keys.Select(k =>
                $"{k}={(collections.TryGetValue(k, out List<CustomWidgetRecord>? list) ? list.Count : 0)}"));
            string modeNote = usedSynthFallback ? "sample-synthesized" : "sample-hydrated";
            sb.AppendLine($"- Collections ({modeNote}): {colSummary}");
        }

        sb.AppendLine($"- {scriptStatusLine}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatShortRenderTruthString(string? val)
    {
        if (string.IsNullOrEmpty(val))
        {
            return "\"\"";
        }

        string singleLine = val.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length > 24 ? $"\"{singleLine[..24]}…\"" : $"\"{singleLine}\"";
    }


    private string ReadOverviewSection()
    {
        var sb = new StringBuilder(1600);
        if (_manifestRoot is null)
        {
            sb.AppendLine("[Workspace Overview] No manifest.json loaded yet.");
        }
        else
        {
            AppendManifestOverviewSkeleton(sb, _manifestRoot);
        }

        if (!string.IsNullOrWhiteSpace(_logicJs))
        {
            string[] lines = NormalizeNewlines(_logicJs).Split('\n');
            List<ScriptSymbolSpan> syms = IndexScriptSymbols(lines);
            sb.AppendLine();
            sb.AppendLine($"[logic.js] {lines.Length} lines, {syms.Count} top-level symbol(s):");
            foreach (ScriptSymbolSpan sym in syms.Take(24))
            {
                sb.Append("  - ").Append(sym.Name).Append(" (L").Append(sym.StartLine).Append("-L").Append(sym.EndLine).AppendLine(")");
            }
        }

        if (_locales is { HasAnyLocale: true })
        {
            sb.AppendLine();
            sb.AppendLine($"[locales] {string.Join(", ", _locales.Locales.Keys)}");
        }

        if (_manifestRoot is not null)
        {
            string truth = BuildCompiledRenderTruthSummary(
                GetManifestJson(indented: false),
                _logicJs,
                _locales,
                ExtractedInlineSampleDataJson,
                isChinese: false);
            if (!string.IsNullOrWhiteSpace(truth))
            {
                sb.AppendLine();
                sb.AppendLine(truth);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private string ReadNodeSection(string? nodePath, int maxDepth)
    {
        if (_manifestRoot is null)
        {
            return "Workspace does not contain a manifest.json yet.";
        }

        string defaultPath = _manifestRoot["root"]?.GetValue<string?>() is { Length: > 0 } rootElemId
            ? $"#{rootElemId.Trim()}"
            : (_manifestRoot["elements"] is JsonObject ? "$.elements" : "$");
        string effectivePath = ExtractCleanNodeSelector(nodePath)
            ?? (string.IsNullOrWhiteSpace(nodePath) ? defaultPath : nodePath.Trim());
        if (!TryResolveNode(_manifestRoot, effectivePath, out JsonNode? target, out string lastValidPath, out JsonNode? lastValidNode, out string resolvedPath, out string failureReason) ||
            target is null)
        {
            return FormatReadPathFailure(effectivePath, lastValidPath, lastValidNode, failureReason);
        }

        JsonNode pruned = PruneNodeForDisplay(target, maxDepth);
        var sb = new StringBuilder(1024);
        sb.AppendLine($"[read_workspace node: `{resolvedPath}` (maxDepth={maxDepth})]");
        sb.AppendLine("```json");
        sb.AppendLine(SerializeNode(pruned, indented: true));
        sb.Append("```");
        return ClampOutput(sb.ToString(), 6000);
    }

    private string ReadManifestSection(string? sectionPath, int maxDepth, int? startLine, int? endLine)
    {
        if (_manifestRoot is null)
        {
            return "Workspace does not contain a manifest.json yet.";
        }

        if (startLine.HasValue || endLine.HasValue)
        {
            string formatted = SerializeNode(_manifestRoot, indented: true);
            string[] lines = NormalizeNewlines(formatted).Split('\n');
            return FormatLineSlice("manifest.json", lines, startLine ?? 1, endLine ?? Math.Min(lines.Length, (startLine ?? 1) + 119));
        }

        if (!string.IsNullOrWhiteSpace(sectionPath))
        {
            return ReadNodeSection(sectionPath, maxDepth);
        }

        return ClampOutput(SerializeNode(_manifestRoot, indented: true), 6000);
    }

    private string ReadScriptSection(string? symbol, int? startLine, int? endLine)
    {
        if (string.IsNullOrWhiteSpace(_logicJs))
        {
            return "Workspace does not contain logic.js.";
        }

        string[] lines = NormalizeNewlines(_logicJs).Split('\n');
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            List<ScriptSymbolSpan> syms = IndexScriptSymbols(lines);
            ScriptSymbolSpan? match = syms.FirstOrDefault(s => string.Equals(s.Name, symbol.Trim(), StringComparison.Ordinal))
                ?? syms.FirstOrDefault(s => s.Name.Contains(symbol.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                int from = Math.Max(1, match.StartLine - 2);
                int to = Math.Min(lines.Length, Math.Min(match.EndLine + 2, from + 159));
                return FormatLineSlice($"logic.js `{match.Name}`", lines, from, to);
            }

            // Fallback: substring search
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    int from = Math.Max(1, i + 1 - 6);
                    int to = Math.Min(lines.Length, i + 1 + 24);
                    return FormatLineSlice($"logic.js match '{symbol}'", lines, from, to);
                }
            }

            return $"Symbol '{symbol}' not found in logic.js. Available symbols: {string.Join(", ", syms.Select(static s => s.Name))}";
        }

        if (startLine.HasValue || endLine.HasValue)
        {
            int from = Math.Max(1, startLine ?? 1);
            int to = Math.Min(lines.Length, endLine ?? Math.Min(lines.Length, from + 119));
            return FormatLineSlice("logic.js", lines, from, to);
        }

        if (lines.Length <= 140)
        {
            return FormatLineSlice("logic.js", lines, 1, lines.Length);
        }

        List<ScriptSymbolSpan> index = IndexScriptSymbols(lines);
        var sb = new StringBuilder(1200);
        sb.AppendLine($"[logic.js] {lines.Length} lines. Symbols:");
        foreach (ScriptSymbolSpan sym in index)
        {
            sb.Append("- `").Append(sym.Name).Append("` (L").Append(sym.StartLine).Append("-L").Append(sym.EndLine).AppendLine(")");
        }
        sb.AppendLine();
        sb.AppendLine(FormatLineSlice("logic.js (head)", lines, 1, Math.Min(50, lines.Length)));
        return ClampOutput(sb.ToString(), 6000);
    }

    private string ReadLocalesSection(string? filter)
    {
        if (_locales is not { HasAnyLocale: true })
        {
            return "Workspace does not contain external locale dictionaries.";
        }

        var sb = new StringBuilder(1024);
        sb.AppendLine("[locales]");
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> lang in _locales.Locales.OrderBy(static k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Language `{lang.Key}` ({lang.Value.Count} keys):");
            IEnumerable<KeyValuePair<string, string>> entries = lang.Value;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string f = filter.Trim();
                entries = entries.Where(kv =>
                    kv.Key.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    kv.Value.Contains(f, StringComparison.OrdinalIgnoreCase));
            }

            foreach (KeyValuePair<string, string> entry in entries.Take(40))
            {
                sb.Append("  \"").Append(entry.Key).Append("\": \"").Append(entry.Value).AppendLine("\"");
            }
        }
        return ClampOutput(sb.ToString(), 5000);
    }

    private static void AppendManifestOverviewSkeleton(StringBuilder sb, JsonObject root)
    {
        string id = root["id"]?.GetValue<string?>() ?? string.Empty;
        string name = root["name"]?.GetValue<string?>() ?? string.Empty;
        string? size = root["defaultSize"] is JsonObject ds
            ? $"{ds["width"]?.ToString() ?? "?"}x{ds["height"]?.ToString() ?? "?"}"
            : null;
        sb.AppendLine($"Widget: id=`{id}`, name=`{name}`{(size is not null ? $", defaultSize={size}" : string.Empty)}");

        if (root["capabilities"] is JsonArray caps && caps.Count > 0)
        {
            sb.AppendLine($"Capabilities: [{string.Join(", ", caps.Select(static c => c?.ToString() ?? string.Empty))}]");
        }

        if (root["settings"] is JsonArray settings && settings.Count > 0)
        {
            sb.AppendLine($"Settings ({settings.Count}): {string.Join(", ", settings.OfType<JsonObject>().Select(static s => $"{s["key"]}({s["type"]})"))}");
        }

        if (root["state"] is JsonObject stateObj && stateObj.Count > 0)
        {
            sb.AppendLine($"State keys ({stateObj.Count}): {string.Join(", ", stateObj.Select(static kv => kv.Key).Take(20))}");
        }

        if (root["collections"] is JsonObject colObj && colObj.Count > 0)
        {
            sb.AppendLine($"Collections ({colObj.Count}): {string.Join(", ", colObj.Select(static kv => kv.Key))}");
        }
        else if (root["collections"] is JsonArray colArr && colArr.Count > 0)
        {
            sb.AppendLine($"Collections ({colArr.Count}): {string.Join(", ", colArr.Select(static c => c is JsonObject co ? (co["name"] ?? co["id"])?.ToString() ?? "?" : c?.ToString() ?? "?"))}");
        }

        if (root["sources"] is JsonArray srcArr && srcArr.Count > 0)
        {
            sb.AppendLine("Sources:");
            foreach (JsonObject src in srcArr.OfType<JsonObject>())
            {
                sb.Append("  - ").Append(src["id"]?.ToString() ?? "?")
                  .Append(" [type=").Append(src["type"]?.ToString() ?? "?")
                  .Append(", intervalMs=").Append(src["intervalMs"]?.ToString() ?? src["intervalSeconds"]?.ToString() ?? "?")
                  .AppendLine("]");
            }
        }

        if (root["actions"] is JsonObject actObj && actObj.Count > 0)
        {
            sb.AppendLine($"Actions: {string.Join(", ", actObj.Select(static kv => kv.Key))}");
        }

        if (root["popovers"] is JsonObject popObj && popObj.Count > 0)
        {
            sb.AppendLine($"Popovers ({popObj.Count}):");
            foreach (KeyValuePair<string, JsonNode?> kv in popObj)
            {
                string popPath = $"$.popovers.{kv.Key}";
                string? popTitle = (kv.Value as JsonObject)?["title"]?.GetValue<string?>();
                string? popRootId = (kv.Value as JsonObject)?["root"]?.GetValue<string?>();
                sb.Append("  - ").Append(kv.Key).Append(" [").Append(popPath).Append(']');
                if (!string.IsNullOrWhiteSpace(popRootId))
                {
                    sb.Append(" root=#").Append(popRootId.Trim());
                }
                if (!string.IsNullOrWhiteSpace(popTitle))
                {
                    sb.Append(" title=\"").Append(TruncateInline(popTitle, 28)).Append('"');
                }
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(popRootId) &&
                    root["elements"] is JsonObject popElemsMap)
                {
                    int popLines = 0;
                    AppendFlatGraphOutline(sb, popElemsMap, popRootId.Trim(), depth: 1, maxDepth: 3, maxLines: 24, ref popLines, new HashSet<string>(StringComparer.Ordinal));
                }
            }
        }

        if (root["elements"] is JsonObject flatElems &&
            root["root"]?.GetValue<string?>() is { Length: > 0 } flatRootId)
        {
            sb.AppendLine($"Elements graph outline (root=#{flatRootId.Trim()}, total={flatElems.Count}):");
            int emittedLines = 0;
            AppendFlatGraphOutline(sb, flatElems, flatRootId.Trim(), depth: 0, maxDepth: 4, maxLines: 80, ref emittedLines, new HashSet<string>(StringComparer.Ordinal));
        }
    }

    private static void AppendFlatGraphOutline(
        StringBuilder sb,
        JsonObject elements,
        string elemId,
        int depth,
        int maxDepth,
        int maxLines,
        ref int emittedLines,
        HashSet<string> stack)
    {
        if (emittedLines >= maxLines || depth > maxDepth || !stack.Add(elemId))
        {
            return;
        }

        try
        {
            if (elements[elemId] is not JsonObject node)
            {
                return;
            }

            string indent = new(' ', Math.Min(depth, 6) * 2);
            string type = node["type"]?.GetValue<string?>()?.Trim() ?? "Node";
            sb.Append(indent).Append('#').Append(elemId).Append(" [").Append(type).Append(']');
            string? text = node["text"]?.GetValue<string?>() ?? node["title"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(" \"").Append(TruncateInline(text, 32)).Append('"');
            }
            string? bind = node["bind"]?.GetValue<string?>()?.Trim();
            if (!string.IsNullOrWhiteSpace(bind))
            {
                sb.Append(" bind=").Append(TruncateInline(bind, 24));
            }
            string? col = (node["collection"] ?? node["items"])?.GetValue<string?>()?.Trim();
            if (!string.IsNullOrWhiteSpace(col))
            {
                sb.Append(" col=").Append(TruncateInline(col, 24));
            }
            string? filter = node["filter"]?.GetValue<string?>()?.Trim();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                sb.Append(" filter=").Append(TruncateInline(filter, 32));
            }
            string? when = (node["$when"] ?? node["when"])?.GetValue<string?>()?.Trim();
            if (!string.IsNullOrWhiteSpace(when))
            {
                sb.Append(" when=").Append(TruncateInline(when, 32));
            }
            if (node["onClick"] is not null)
            {
                sb.Append(" onClick");
            }
            sb.AppendLine();
            emittedLines++;

            if (node["children"] is JsonArray childIds)
            {
                foreach (JsonNode? c in childIds)
                {
                    if (emittedLines >= maxLines) break;
                    if (c is JsonValue cv && cv.TryGetValue<string>(out string? cid) && !string.IsNullOrWhiteSpace(cid))
                    {
                        AppendFlatGraphOutline(sb, elements, cid.Trim(), depth + 1, maxDepth, maxLines, ref emittedLines, stack);
                    }
                }
            }

            foreach (string refKey in (ReadOnlySpan<string>)["itemTemplate", "pane", "actionButton"])
            {
                if (emittedLines >= maxLines) break;
                if (node[refKey] is JsonValue rv && rv.TryGetValue<string>(out string? refId) && !string.IsNullOrWhiteSpace(refId))
                {
                    AppendFlatGraphOutline(sb, elements, refId.Trim(), depth + 1, maxDepth, maxLines, ref emittedLines, stack);
                }
            }

            if (node["hoverActions"] is JsonArray hoverIds)
            {
                foreach (JsonNode? h in hoverIds)
                {
                    if (emittedLines >= maxLines) break;
                    if (h is JsonValue hv && hv.TryGetValue<string>(out string? hid) && !string.IsNullOrWhiteSpace(hid))
                    {
                        AppendFlatGraphOutline(sb, elements, hid.Trim(), depth + 1, maxDepth, maxLines, ref emittedLines, stack);
                    }
                }
            }
        }
        finally
        {
            stack.Remove(elemId);
        }
    }

    public static string BuildCompactViewOutline(string? manifestJson, int maxLines = 28)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || ParseManifestObject(manifestJson) is not JsonObject root)
        {
            return string.Empty;
        }

        if (root["elements"] is not JsonObject flatElems ||
            root["root"]?.GetValue<string?>() is not { Length: > 0 } flatRootId)
        {
            return string.Empty;
        }

        var flatSb = new StringBuilder(512);
        int flatLines = 0;
        AppendFlatGraphOutline(flatSb, flatElems, flatRootId.Trim(), depth: 0, maxDepth: 4, maxLines: maxLines, ref flatLines, new HashSet<string>(StringComparer.Ordinal));
        if (flatLines < maxLines && root["popovers"] is JsonObject flatPopMap)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in flatPopMap)
            {
                if (flatLines >= maxLines) break;
                if (kv.Value is JsonObject po && po["root"]?.GetValue<string?>() is { Length: > 0 } pr)
                {
                    flatSb.Append("[popover:").Append(kv.Key).AppendLine("]");
                    flatLines++;
                    AppendFlatGraphOutline(flatSb, flatElems, pr.Trim(), depth: 1, maxDepth: 3, maxLines: maxLines, ref flatLines, new HashSet<string>(StringComparer.Ordinal));
                }
            }
        }
        return flatSb.ToString().TrimEnd();
    }

    private static bool TryFindBestMatchingElementByInstruction(
        JsonObject elementsObj,
        string instruction,
        out string bestPath,
        out JsonNode? bestNode)
    {
        string[] keywords = instruction
            .Split([' ', '\t', '\r', '\n', ',', '.', '，', '。', '`', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(static w => w.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        bestPath = "$.elements";
        bestNode = null;
        int bestScore = 0;

        foreach (KeyValuePair<string, JsonNode?> kv in elementsObj)
        {
            if (kv.Value is not JsonObject cur)
            {
                continue;
            }

            string elemId = kv.Key;
            string text = cur["text"]?.GetValue<string?>() ?? cur["title"]?.GetValue<string?>() ?? string.Empty;
            string type = cur["type"]?.GetValue<string?>() ?? string.Empty;
            int score = 0;
            foreach (string kw in keywords)
            {
                if (elemId.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (type.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPath = $"$.elements.{elemId}";
                bestNode = cur;
            }
        }

        return bestScore > 0 && bestNode is not null;
    }

    private static bool TryResolveNearestSubtree(
        JsonObject root,
        string diagPath,
        out string resolvedPath,
        out JsonNode? resolvedNode)
    {
        resolvedPath = "$";
        resolvedNode = null;
        if (TryResolveNode(root, diagPath, out JsonNode? exact, out _, out _, out string exactPath, out _) && exact is not null)
        {
            resolvedPath = exactPath;
            resolvedNode = exact;
            return true;
        }

        if (TryResolveNode(root, diagPath, out _, out string lastValidPath, out JsonNode? lastValidNode, out _, out _) &&
            lastValidNode is not null)
        {
            resolvedPath = string.IsNullOrWhiteSpace(lastValidPath) ? "$" : lastValidPath;
            resolvedNode = lastValidNode;
            return true;
        }

        return false;
    }

    private static JsonNode PruneNodeForDisplay(JsonNode node, int maxDepth, int currentDepth = 0)
    {
        if (node is JsonValue)
        {
            return node.DeepClone();
        }

        if (node is JsonArray arr)
        {
            if (currentDepth >= maxDepth)
            {
                return JsonValue.Create($"[... {arr.Count} item(s) folded]")!;
            }

            var clonedArr = new JsonArray();
            int limit = Math.Min(arr.Count, 12);
            for (int i = 0; i < limit; i++)
            {
                clonedArr.Add(arr[i] is null ? null : PruneNodeForDisplay(arr[i]!, maxDepth, currentDepth + 1));
            }
            if (arr.Count > limit)
            {
                clonedArr.Add((JsonNode?)JsonValue.Create($"[... +{arr.Count - limit} more item(s)]"));
            }
            return clonedArr;
        }

        if (node is JsonObject obj)
        {
            var clonedObj = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Value is null)
                {
                    continue;
                }

                if (currentDepth >= maxDepth && ChildContainerKeys.Contains(kv.Key))
                {
                    int childCount = kv.Value is JsonArray cArr ? cArr.Count : 1;
                    clonedObj[kv.Key] = JsonValue.Create($"[... {childCount} node(s) folded]");
                    continue;
                }

                clonedObj[kv.Key] = PruneNodeForDisplay(kv.Value, maxDepth, currentDepth + 1);
            }
            return clonedObj;
        }

        return node.DeepClone();
    }

    private static string SerializeNodeWithLineCap(JsonNode node, int maxLines)
    {
        string raw = SerializeNode(node, indented: true);
        string[] lines = NormalizeNewlines(raw).Split('\n');
        if (lines.Length <= maxLines)
        {
            return raw;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < maxLines; i++)
        {
            sb.AppendLine(lines[i]);
        }
        sb.Append($"... (+{lines.Length - maxLines} more lines)");
        return sb.ToString();
    }

    private static int? TryExtractScriptDiagnosticLine(string path, string message)
    {
        bool looksLikeScript =
            path.Contains("logic.js", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("$.script", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("script", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Jint", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeScript)
        {
            return null;
        }

        Match m = DiagnosticScriptLineRegex().Match(message);
        if (!m.Success)
        {
            m = DiagnosticScriptLineRegex().Match(path);
        }

        if (!m.Success)
        {
            return null;
        }

        string numText = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return int.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line) && line > 0
            ? line
            : null;
    }

    private static void AppendScriptWindow(StringBuilder sb, string[] lines, int centerLine, int radius)
    {
        int start = Math.Max(1, centerLine - radius);
        int end = Math.Min(lines.Length, centerLine + radius);
        sb.AppendLine($"  logic.js lines L{start}-L{end}:");
        sb.AppendLine("```js");
        for (int l = start; l <= end; l++)
        {
            sb.Append(l == centerLine ? ">> L" : "   L")
              .Append(l)
              .Append(": ")
              .AppendLine(lines[l - 1]);
        }
        sb.AppendLine("```");
    }


    private sealed record ScriptSymbolSpan(string Name, int StartLine, int EndLine);

    private static List<ScriptSymbolSpan> IndexScriptSymbols(string[] lines)
    {
        var result = new List<ScriptSymbolSpan>();
        int braceDepth = 0;
        string? activeName = null;
        int activeStart = 0;
        int activeEntryDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (activeName is null && braceDepth == 0)
            {
                Match fnMatch = JsFunctionDeclarationRegex().Match(line);
                if (fnMatch.Success)
                {
                    activeName = fnMatch.Groups[1].Value;
                    activeStart = i + 1;
                    activeEntryDepth = 0;
                }
                else
                {
                    Match constMatch = JsConstFunctionRegex().Match(line);
                    if (constMatch.Success)
                    {
                        activeName = constMatch.Groups[1].Value;
                        activeStart = i + 1;
                        activeEntryDepth = 0;
                    }
                }
            }

            foreach (char ch in line)
            {
                if (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth = Math.Max(0, braceDepth - 1);
            }

            if (activeName is not null && braceDepth <= activeEntryDepth && (line.Contains('}') || i + 1 == lines.Length))
            {
                result.Add(new ScriptSymbolSpan(activeName, activeStart, i + 1));
                activeName = null;
            }
        }

        if (activeName is not null)
        {
            result.Add(new ScriptSymbolSpan(activeName, activeStart, lines.Length));
        }

        return result;
    }


    private static List<ScriptSymbolSpan> PickRelevantSymbols(
        List<ScriptSymbolSpan> symbols,
        string[] lines,
        string? targetNodePath,
        string? userInstruction,
        string? manifestJson)
    {
        var selected = new List<ScriptSymbolSpan>(2);
        var selectedNames = new HashSet<string>(StringComparer.Ordinal);
        if (symbols.Count == 0)
        {
            return selected;
        }

        void AddUnique(ScriptSymbolSpan? sym)
        {
            if (sym is not null && selected.Count < 2 && selectedNames.Add(sym.Name))
            {
                selected.Add(sym);
            }
        }

        if (!string.IsNullOrWhiteSpace(targetNodePath))
        {
            foreach (ScriptSymbolSpan sym in symbols)
            {
                if (ContainsIdentifierToken(targetNodePath, sym.Name))
                {
                    AddUnique(sym);
                }
            }
        }

        var linkedScriptFns = new List<string>();
        var linkedComputedKeys = new List<string>();
        string? cleanSelector = ExtractCleanNodeSelector(targetNodePath);
        if (!string.IsNullOrWhiteSpace(cleanSelector) &&
            !string.IsNullOrWhiteSpace(manifestJson) &&
            ParseManifestObject(manifestJson) is JsonObject manifestRoot &&
            TryResolveNode(manifestRoot, cleanSelector, out JsonNode? targetNode, out _, out _, out _, out _) &&
            targetNode is not null)
        {
            string nodeSnippet = targetNode.ToJsonString();
            foreach (Match m in ComputedTokenRefRegex.Matches(nodeSnippet))
            {
                string key = m.Groups[1].Value;
                if (!linkedComputedKeys.Contains(key, StringComparer.Ordinal))
                {
                    linkedComputedKeys.Add(key);
                }
            }

            foreach (Match m in ManifestFnPropRegex.Matches(nodeSnippet))
            {
                string fn = m.Groups[1].Value;
                if (!linkedScriptFns.Contains(fn, StringComparer.Ordinal))
                {
                    linkedScriptFns.Add(fn);
                }
            }
        }

        foreach (string fnName in linkedScriptFns)
        {
            ScriptSymbolSpan? fnSym = symbols.FirstOrDefault(s => string.Equals(s.Name, fnName, StringComparison.Ordinal));
            AddUnique(fnSym);
        }

        List<string> bindingIds = ExtractScriptBindingIdentifiers(targetNodePath, userInstruction);
        foreach (string key in linkedComputedKeys)
        {
            if (!bindingIds.Contains(key, StringComparer.Ordinal))
            {
                bindingIds.Add(key);
            }
        }

        if (bindingIds.Count > 0 && lines.Length > 0 && selected.Count < 2)
        {
            var ranked = new List<(ScriptSymbolSpan Sym, int Score)>();
            foreach (ScriptSymbolSpan sym in symbols)
            {
                if (selectedNames.Contains(sym.Name))
                {
                    continue;
                }

                int start = Math.Clamp(sym.StartLine - 1, 0, lines.Length - 1);
                int end = Math.Clamp(sym.EndLine - 1, start, lines.Length - 1);
                int score = 0;
                foreach (string id in bindingIds)
                {
                    bool matchedInBody = false;
                    bool matchedAssignment = false;
                    for (int l = start; l <= end; l++)
                    {
                        if (ContainsIdentifierToken(lines[l], id))
                        {
                            matchedInBody = true;
                            if (lines[l].Contains($"{id}:", StringComparison.Ordinal) ||
                                lines[l].Contains("setComputed", StringComparison.Ordinal))
                            {
                                matchedAssignment = true;
                            }
                        }
                    }

                    if (matchedInBody)
                    {
                        score += matchedAssignment ? 3 : 1;
                    }
                }

                if (score > 0)
                {
                    ranked.Add((sym, score));
                }
            }

            foreach ((ScriptSymbolSpan sym, _) in ranked.OrderByDescending(static r => r.Score))
            {
                AddUnique(sym);
                if (selected.Count >= 2)
                {
                    break;
                }
            }
        }

        if (selected.Count < 2 && !string.IsNullOrWhiteSpace(userInstruction))
        {
            foreach (ScriptSymbolSpan sym in symbols)
            {
                if (ContainsIdentifierToken(userInstruction, sym.Name))
                {
                    AddUnique(sym);
                }
            }
        }

        if (selected.Count == 0)
        {
            AddUnique(
                symbols.FirstOrDefault(static s => string.Equals(s.Name, "onInit", StringComparison.OrdinalIgnoreCase))
                ?? symbols[0]);
        }

        return selected;
    }

    private static List<string> ExtractScriptBindingIdentifiers(string? targetNodePath, string? userInstruction)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddId(string candidate)
        {
            string trimmed = candidate.Trim();
            if (trimmed.Length >= 2 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        void ScanText(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            foreach (Match m in ComputedTokenRefRegex.Matches(source))
            {
                if (m.Groups.Count > 1)
                {
                    AddId(m.Groups[1].Value);
                }
            }

            int idx = source.IndexOf("linked script bindings:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                string tail = source[(idx + "linked script bindings:".Length)..];
                int closeParen = tail.IndexOf(')');
                if (closeParen >= 0)
                {
                    tail = tail[..closeParen];
                }

                foreach (Match tm in IdentifierWordRegex.Matches(tail))
                {
                    string tok = tm.Groups[1].Value;
                    if (!string.Equals(tok, "computed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(tok, "state", StringComparison.OrdinalIgnoreCase))
                    {
                        AddId(tok);
                    }
                }
            }
        }

        ScanText(targetNodePath);
        ScanText(userInstruction);
        return result;
    }

    private static bool ContainsIdentifierToken(string text, string identifier)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        int idx = 0;
        while ((idx = text.IndexOf(identifier, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = idx == 0 || !(char.IsLetterOrDigit(text[idx - 1]) || text[idx - 1] == '_');
            int after = idx + identifier.Length;
            bool rightOk = after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
            if (leftOk && rightOk)
            {
                return true;
            }

            idx += identifier.Length;
        }

        return false;
    }

    private static string FormatLineSlice(string label, string[] lines, int startLine, int endLine)
    {
        int s = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        int e = Math.Clamp(endLine, s, Math.Max(1, lines.Length));
        var sb = new StringBuilder();
        sb.AppendLine($"[{label} L{s}-L{e} / {lines.Length}]");
        sb.AppendLine("```js");
        for (int i = s; i <= e; i++)
        {
            sb.Append("L").Append(i).Append(": ").AppendLine(lines[i - 1]);
        }
        sb.Append("```");
        return sb.ToString();
    }

    internal static JsonNode? ParsePermissiveJsonNode(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(rawJson, PermissiveDocOptions);
        return ConvertJsonElementToNode(doc.RootElement);
    }

    internal static JsonNode? ConvertJsonElementToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new JsonObject(PermissiveNodeOptions);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    // Match standard JS JSON.parse last-write-wins semantics so duplicate keys emitted by LLMs never throw ArgumentException.
                    obj[prop.Name] = ConvertJsonElementToNode(prop.Value);
                }
                return obj;
            }
            case JsonValueKind.Array:
            {
                var arr = new JsonArray(PermissiveNodeOptions);
                foreach (JsonElement item in element.EnumerateArray())
                {
                    arr.Add(ConvertJsonElementToNode(item));
                }
                return arr;
            }
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create(element.Clone(), PermissiveNodeOptions);
            default:
                return null;
        }
    }

    private static JsonObject? ParseManifestObject(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return ParsePermissiveJsonNode(rawJson) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeNode(JsonNode node, bool indented)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, indented ? IndentedWriterOptions : CompactWriterOptions))
        {
            node.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }
        return lines;
    }

    internal static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string TruncateInline(string text, int maxLen) =>
        text.Length <= maxLen ? text : string.Concat(text.AsSpan(0, maxLen), "...");

    private static string ClampOutput(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "\n... [truncated]");

    private static string FormatReadPathFailure(
        string requestedPath,
        string lastValidPath,
        JsonNode? lastValidNode,
        string failureReason)
    {
        var sb = new StringBuilder(512);
        sb.Append($"read_workspace failed on path '{requestedPath}': {failureReason}");
        if (!string.IsNullOrWhiteSpace(lastValidPath))
        {
            sb.Append($" Nearest valid path: '{lastValidPath}'.");
        }

        if (lastValidNode is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Nearest node (`{lastValidPath}`):");
            sb.AppendLine("```json");
            sb.AppendLine(SerializeNodeWithLineCap(PruneNodeForDisplay(lastValidNode, maxDepth: 2), maxLines: 40));
            sb.Append("```");
        }

        return sb.ToString();
    }

    private enum SelectorTokenKind
    {
        Property,
        ArrayIndex,
        ArrayPredicate,
        GlobalId
    }

    private sealed record SelectorToken(
        SelectorTokenKind Kind,
        string? Name = null,
        int Index = -1,
        string? Value = null);

    private static bool TryTokenizeSelector(string rawPath, out List<SelectorToken> tokens)
    {
        tokens = [];
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string s = rawPath.Trim();
        int i = 0;
        if (s.StartsWith("$", StringComparison.Ordinal))
        {
            i = 1;
        }
        else if (s.StartsWith("#", StringComparison.Ordinal))
        {
            int end = 1;
            while (end < s.Length && s[end] != '.' && s[end] != '[')
            {
                end++;
            }

            string id = s[1..end].Trim();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            tokens.Add(new SelectorToken(SelectorTokenKind.GlobalId, Value: id));
            i = end;
        }
        else if (s.StartsWith("@id=", StringComparison.OrdinalIgnoreCase))
        {
            int end = 4;
            while (end < s.Length && s[end] != '.' && s[end] != '[')
            {
                end++;
            }

            string id = Unquote(s[4..end].Trim());
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            tokens.Add(new SelectorToken(SelectorTokenKind.GlobalId, Value: id));
            i = end;
        }

        while (i < s.Length)
        {
            char c = s[i];
            if (c == '.')
            {
                i++;
                if (i >= s.Length)
                {
                    return false;
                }

                if (s[i] == '#')
                {
                    int end = i + 1;
                    while (end < s.Length && s[end] != '.' && s[end] != '[')
                    {
                        end++;
                    }
                    string id = s[(i + 1)..end].Trim();
                    if (string.IsNullOrEmpty(id))
                    {
                        return false;
                    }
                    tokens.Add(new SelectorToken(SelectorTokenKind.GlobalId, Value: id));
                    i = end;
                    continue;
                }

                int start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[')
                {
                    i++;
                }

                string prop = s[start..i].Trim();
                if (string.IsNullOrEmpty(prop))
                {
                    return false;
                }

                tokens.Add(new SelectorToken(SelectorTokenKind.Property, Name: prop));
            }
            else if (c == '[')
            {
                int close = FindClosingBracket(s, i);
                if (close < 0)
                {
                    return false;
                }

                string inside = s[(i + 1)..close].Trim();
                if (string.IsNullOrEmpty(inside))
                {
                    return false;
                }

                if (int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                {
                    tokens.Add(new SelectorToken(SelectorTokenKind.ArrayIndex, Index: idx));
                }
                else if (inside.StartsWith("'", StringComparison.Ordinal) || inside.StartsWith("\"", StringComparison.Ordinal))
                {
                    tokens.Add(new SelectorToken(SelectorTokenKind.Property, Name: Unquote(inside)));
                }
                else
                {
                    int eq = inside.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = inside[..eq].Trim();
                        if (key.StartsWith("?@.", StringComparison.Ordinal)) key = key[3..];
                        else if (key.StartsWith("@.", StringComparison.Ordinal)) key = key[2..];
                        else if (key.StartsWith("@", StringComparison.Ordinal)) key = key[1..];

                        string valPart = inside[(eq + 1)..].Trim();
                        if (valPart.StartsWith("=", StringComparison.Ordinal))
                        {
                            valPart = valPart[1..].Trim();
                        }

                        tokens.Add(new SelectorToken(SelectorTokenKind.ArrayPredicate, Name: key, Value: Unquote(valPart)));
                    }
                    else
                    {
                        tokens.Add(new SelectorToken(SelectorTokenKind.Property, Name: Unquote(inside)));
                    }
                }

                i = close + 1;
            }
            else
            {
                int start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[')
                {
                    i++;
                }

                string prop = s[start..i].Trim();
                if (string.IsNullOrEmpty(prop))
                {
                    return false;
                }

                tokens.Add(new SelectorToken(SelectorTokenKind.Property, Name: prop));
            }
        }

        return true;
    }

    private static int FindClosingBracket(string text, int openIndex)
    {
        char quote = '\0';
        for (int i = openIndex + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                }
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == ']')
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool TryResolveNode(
        JsonObject root,
        string path,
        out JsonNode? target,
        out string lastValidPath,
        out JsonNode? lastValidNode,
        out string resolvedPath,
        out string failureReason)
    {
        return TryResolveNodeWithParent(
            root,
            path,
            out target,
            out _,
            out _,
            out _,
            out lastValidPath,
            out lastValidNode,
            out failureReason,
            out resolvedPath);
    }

    private static bool TryResolveNodeWithParent(
        JsonObject root,
        string path,
        out JsonNode? target,
        out JsonObject? parentObj,
        out string? parentProp,
        out JsonArray? parentArr,
        out string lastValidPath,
        out JsonNode? lastValidNode,
        out string failureReason,
        out string resolvedPath)
    {
        target = root;
        parentObj = null;
        parentProp = null;
        parentArr = null;
        lastValidPath = "$";
        lastValidNode = root;
        resolvedPath = "$";
        failureReason = string.Empty;

        if (!TryTokenizeSelector(path, out List<SelectorToken> tokens))
        {
            target = null;
            failureReason = "Invalid selector syntax.";
            return false;
        }

        JsonNode? current = root;
        var currentPath = new StringBuilder("$");

        for (int i = 0; i < tokens.Count; i++)
        {
            SelectorToken token = tokens[i];
            if (token.Kind == SelectorTokenKind.GlobalId)
            {
                if (TryFindNodeById(root, token.Value!, out JsonNode? found, out JsonObject? pObj, out string? pProp, out JsonArray? pArr, out string foundPath))
                {
                    current = found;
                    parentObj = pObj;
                    parentProp = pProp;
                    parentArr = pArr;
                    currentPath.Clear().Append(foundPath);
                    lastValidPath = foundPath;
                    lastValidNode = current;
                    continue;
                }

                target = null;
                failureReason = $"No element with id '{token.Value}' was found in manifest.json.";
                return false;
            }

            if (token.Kind == SelectorTokenKind.Property)
            {
                string prop = token.Name!;
                if (current is not JsonObject obj)
                {
                    target = null;
                    failureReason = $"Expected JSON object at '{currentPath}' before accessing property '.{prop}'.";
                    return false;
                }

                if (!TryGetObjectPropertyExactOrFolded(obj, prop, out string actualProp, out JsonNode? next))
                {
                    target = null;
                    string availableKeys = string.Join(", ", obj.Select(static kv => kv.Key).Take(12));
                    failureReason = $"Property '{prop}' does not exist on '{currentPath}'. Available keys: [{availableKeys}].";
                    return false;
                }

                parentObj = obj;
                parentProp = actualProp;
                parentArr = null;
                current = next;
                currentPath.Append('.').Append(actualProp);
                lastValidPath = currentPath.ToString();
                lastValidNode = current;
                continue;
            }

            if (token.Kind == SelectorTokenKind.ArrayIndex)
            {
                if (current is not JsonArray arr)
                {
                    target = null;
                    failureReason = $"Expected JSON array at '{currentPath}' before index [{token.Index}].";
                    return false;
                }

                int idx = token.Index < 0 ? arr.Count + token.Index : token.Index;
                if (idx < 0 || idx >= arr.Count)
                {
                    target = null;
                    failureReason = $"Array index [{token.Index}] out of bounds at '{currentPath}' (length={arr.Count}).";
                    return false;
                }

                parentObj = null;
                parentProp = null;
                parentArr = arr;
                current = arr[idx];
                currentPath.Append('[').Append(idx).Append(']');
                lastValidPath = currentPath.ToString();
                lastValidNode = current;
                continue;
            }

            if (token.Kind == SelectorTokenKind.ArrayPredicate)
            {
                if (current is not JsonArray arr)
                {
                    target = null;
                    failureReason = $"Expected JSON array at '{currentPath}' for predicate [{token.Name}={token.Value}].";
                    return false;
                }

                int foundIdx = -1;
                for (int j = 0; j < arr.Count; j++)
                {
                    if (arr[j] is JsonObject itemObj &&
                        TryGetObjectPropertyExactOrFolded(itemObj, token.Name!, out _, out JsonNode? valNode) &&
                        NodeScalarEquals(valNode, token.Value!))
                    {
                        foundIdx = j;
                        break;
                    }
                }

                if (foundIdx < 0)
                {
                    target = null;
                    failureReason = $"No element in '{currentPath}' matched predicate [{token.Name}='{token.Value}'] (length={arr.Count}).";
                    return false;
                }

                parentObj = null;
                parentProp = null;
                parentArr = arr;
                current = arr[foundIdx];
                currentPath.Append('[').Append(foundIdx).Append(']');
                lastValidPath = currentPath.ToString();
                lastValidNode = current;
            }
        }

        target = current;
        resolvedPath = currentPath.ToString();
        return true;
    }

    private static bool TryGetObjectPropertyExactOrFolded(
        JsonObject obj,
        string requestedKey,
        out string actualKey,
        out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(requestedKey, out value))
        {
            actualKey = requestedKey;
            return true;
        }

        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, requestedKey, StringComparison.OrdinalIgnoreCase))
            {
                actualKey = kv.Key;
                value = kv.Value;
                return true;
            }
        }

        actualKey = requestedKey;
        value = null;
        return false;
    }

    private static bool NodeScalarEquals(JsonNode? node, string expected)
    {
        if (node is null)
        {
            return false;
        }

        if (node is JsonValue val)
        {
            if (val.TryGetValue(out string? s))
            {
                return string.Equals(s, expected, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(val.ToJsonString().Trim('"'), expected, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryFindNodeById(
        JsonNode root,
        string targetId,
        out JsonNode? found,
        out JsonObject? parentObj,
        out string? parentProp,
        out JsonArray? parentArr,
        out string foundPath)
    {
        // Canonical Flat Element Graph O(1) element lookup in root["elements"][targetId]
        if (root is JsonObject rootObj &&
            rootObj["elements"] is JsonObject elementsMap &&
            TryGetObjectPropertyExactOrFolded(elementsMap, targetId, out string matchedElemKey, out JsonNode? elemNode) &&
            elemNode is not null)
        {
            found = elemNode;
            parentObj = elementsMap;
            parentProp = matchedElemKey;
            parentArr = null;
            foundPath = $"$.elements.{matchedElemKey}";
            return true;
        }

        found = null;
        parentObj = null;
        parentProp = null;
        parentArr = null;
        foundPath = "$";
        return false;
    }

    [GeneratedRegex(@"\$computed\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ComputedTokenRefRegex { get; }

    [GeneratedRegex(@"""fn""\s*:\s*""([A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestFnPropRegex { get; }

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierWordRegex { get; }
}
