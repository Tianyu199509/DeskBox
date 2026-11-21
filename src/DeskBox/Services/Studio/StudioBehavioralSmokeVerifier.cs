using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Fast (&lt; 3 ms) in-memory behavioral smoke verifier for DeskBox Studio Agent 2.0:
/// catches silent runtime defects that pass structural schema validation — such as
/// 100%-blank <c>itemTemplate</c> renders against sample records, dead <c>Input</c> bindings with zero
/// downstream consumers, and tautological/broken <c>List.filter</c> expressions.
/// </summary>
public static partial class StudioBehavioralSmokeVerifier
{
    private const string NoMatchProbeQuery = "__deskbox_nomatch_probe_991__";
    private const int MaxDryRunHandlers = 16;

    [GeneratedRegex(@"\bstate\.([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StateKeyRefRegex();

    [GeneratedRegex(@"^\s*state\.([A-Za-z_][A-Za-z0-9_]*)\s*===?\s*['""]([^'""]+)['""]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleStateEqualityRegex();

    public static IReadOnlyList<CustomWidgetValidationDiagnostic> Verify(
        CustomWidgetManifest? manifest,
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? sampleDataJson,
        string? cultureName = null)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifestJson))
        {
            return [];
        }

        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetManifest localizedManifest = manifest.WithLocales(locales);
        IReadOnlyDictionary<string, string> i18nDict = localizedManifest.ResolveI18nDictionary(cultureName);

        var settingsDict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition s in localizedManifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(s.Key))
            {
                settingsDict[s.Key] = s.DefaultValue ?? string.Empty;
            }
        }

        CustomWidgetSampleDataHydrator.TryHydrate(
            localizedManifest,
            sampleDataJson,
            logicJs: null,
            out Dictionary<string, string> stateDict,
            out Dictionary<string, JsonElement> stateJsonDict,
            out Dictionary<string, List<CustomWidgetRecord>> sampleCollections,
            out _,
            synthesizeFallbacks: false);

        var baseCtx = new CustomWidgetEvaluationContext
        {
            I18n = i18nDict,
            Settings = settingsDict,
            State = stateDict,
            StateJson = stateJsonDict,
            Collections = sampleCollections,
            CultureName = cultureName
        };

        var collectionNodes = new List<CustomWidgetNodeSpec>();
        var inputNodes = new List<CustomWidgetNodeSpec>();

        foreach (CustomWidgetNodeSpec node in localizedManifest.EnumerateAllNodes())
        {
            if (string.Equals(node.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase))
            {
                collectionNodes.Add(node);
            }
            else if (string.Equals(node.Type, "Input", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.Type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase))
            {
                inputNodes.Add(node);
            }
        }

        // 1. Blank itemTemplate check & broken List filter check
        foreach (CustomWidgetNodeSpec colNode in collectionNodes)
        {
            string? colName = ResolveCollectionName(colNode);
            if (string.IsNullOrWhiteSpace(colName) ||
                !sampleCollections.TryGetValue(colName, out List<CustomWidgetRecord>? rows) ||
                rows.Count == 0)
            {
                continue;
            }

            CustomWidgetRecord firstRow = rows[0];
            if (colNode.ItemTemplate is not null)
            {
                var textNodes = new List<CustomWidgetNodeSpec>();
                foreach (CustomWidgetNodeSpec candidate in colNode.ItemTemplate.EnumerateAllNodes())
                {
                    if (IsTextBearingNodeType(candidate.Type))
                    {
                        textNodes.Add(candidate);
                    }
                }
                if (textNodes.Count > 0)
                {
                    CustomWidgetEvaluationContext itemCtx = baseCtx.WithItem(firstRow, 0);
                    bool anyNonBlank = false;
                    bool anyDynamicOrMissing = false;

                    foreach (CustomWidgetNodeSpec tn in textNodes)
                    {
                        string rawTemplate = tn.Text ?? tn.Title ?? tn.Subtitle ?? tn.Value ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(rawTemplate) && tn.Spans.Count == 0)
                        {
                            anyDynamicOrMissing = true;
                            continue;
                        }

                        if (rawTemplate.Contains("${", StringComparison.Ordinal))
                        {
                            anyDynamicOrMissing = true;
                        }

                        string rendered = CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, itemCtx);
                        if (!string.IsNullOrWhiteSpace(rendered))
                        {
                            anyNonBlank = true;
                            break;
                        }

                        foreach (CustomWidgetTextSpanSpec span in tn.Spans)
                        {
                            if (!string.IsNullOrWhiteSpace(CustomWidgetExpressionEvaluator.Interpolate(span.Text, itemCtx)))
                            {
                                anyNonBlank = true;
                                break;
                            }
                        }

                        if (anyNonBlank)
                        {
                            break;
                        }
                    }

                    if (anyDynamicOrMissing && !anyNonBlank)
                    {
                        string availableFields = firstRow.FieldsJson.Count > 0
                            ? string.Join(", ", firstRow.FieldsJson.Keys.Take(8))
                            : "(none)";
                        diagnostics.Add(new CustomWidgetValidationDiagnostic(
                            $"{colNode.NodePath}.itemTemplate",
                            $"itemTemplate rendered 100% blank text on sample record #0 of collection '{colName}' (available fields: {availableFields}). Ensure Text/Badge nodes set \"text\": \"${{$item.<field>}}\" (not \"bind\") and match sampleData / source fieldMap keys.",
                            IsError: true,
                            Code: "BehavioralBlankItemTemplate"));
                    }
                }
            }

            // Probe List.filter or itemTemplate.when referencing state.<key>
            string? filterExpr = !string.IsNullOrWhiteSpace(colNode.Filter)
                ? colNode.Filter
                : colNode.ItemTemplate?.When;
            if (!string.IsNullOrWhiteSpace(filterExpr))
            {
                Match stateRef = StateKeyRefRegex().Match(filterExpr);
                if (stateRef.Success)
                {
                    string filterStateKey = stateRef.Groups[1].Value;
                    bool isBoundToTextInput = inputNodes.Any(inp =>
                        string.Equals(ExtractLeafStateKey(inp.Bind), filterStateKey, StringComparison.Ordinal));
                    if (isBoundToTextInput)
                    {
                        var probeState = new Dictionary<string, string>(stateDict, StringComparer.Ordinal)
                        {
                            [filterStateKey] = NoMatchProbeQuery
                        };
                        var probeStateJson = new Dictionary<string, JsonElement>(stateJsonDict, StringComparer.Ordinal)
                        {
                            [filterStateKey] = CustomWidgetScriptEngine.CreateStringJsonElement(NoMatchProbeQuery)
                        };
                        CustomWidgetEvaluationContext probeCtx = baseCtx with
                        {
                            State = probeState,
                            StateJson = probeStateJson
                        };

                        bool allStillMatch = true;
                        int checkedRows = Math.Min(rows.Count, 3);
                        for (int r = 0; r < checkedRows; r++)
                        {
                            if (!CustomWidgetExpressionEvaluator.EvaluateCondition(filterExpr, probeCtx.WithItem(rows[r], r)))
                            {
                                allStillMatch = false;
                                break;
                            }
                        }

                        if (allStillMatch)
                        {
                            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                                $"{colNode.NodePath}.filter",
                                $"Filter expression '{filterExpr}' stayed true for all sample rows even when state.{filterStateKey}='{NoMatchProbeQuery}'. Use \"filter\": \"!state.{filterStateKey} || ($item.<field> || '').toLowerCase().includes(state.{filterStateKey}.toLowerCase())\" directly on the {colNode.Type} node.",
                                IsError: true,
                                Code: "BehavioralTautologicalListFilter"));
                        }
                    }
                }
            }
        }

        // 2. Dead reactive Input binding check (Input binds state.<k>, has no onSubmit/onChange, widget has a List, and nothing else reads <k>)
        if (collectionNodes.Count > 0 && inputNodes.Count > 0)
        {
            foreach (CustomWidgetNodeSpec inp in inputNodes)
            {
                if (inp.OnSubmit.Count > 0 || inp.OnChange.Count > 0)
                {
                    continue;
                }

                string? leafKey = ExtractLeafStateKey(inp.Bind);
                if (string.IsNullOrWhiteSpace(leafKey))
                {
                    continue;
                }

                if (!HasDownstreamConsumer(leafKey, inp, localizedManifest, logicJs))
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        inp.NodePath,
                        $"Input '{inp.Id ?? inp.NodePath}' binds 'state.{leafKey}', but no List.filter, $when, text expression, source URL, or logic.js function reads 'state.{leafKey}' — typing in this input has no effect. Add \"filter\": \"!state.{leafKey} || ($item.<titleField> || '').toLowerCase().includes(state.{leafKey}.toLowerCase())\" on the List or wire onSubmit/onChange.",
                        IsError: true,
                        Code: "BehavioralDeadInputBinding"));
                }
            }
        }

        // 3. Multi-state reachability audit (detects dead tabs/views guarded by unreachable state.<k> == 'literal')
        CheckUnreachableStateBranches(localizedManifest, stateDict, logicJs, diagnostics);

        // 4. Duplicate sibling interactive button audit (detects forgot-to-delete duplicate buttons in same container)
        CheckDuplicateSiblingButtons(localizedManifest, baseCtx, diagnostics);

        // 5. QuickJS clickable-handler dry-run + Single-Source-of-Truth ctx.dispatch schema validation + form-popover open audit
        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            RunScriptHandlerSmokeCheck(
                localizedManifest,
                logicJs,
                baseCtx,
                stateDict,
                stateJsonDict,
                sampleCollections,
                diagnostics);
        }

        return diagnostics;
    }

    private static void CheckUnreachableStateBranches(
        CustomWidgetManifest manifest,
        IReadOnlyDictionary<string, string> initialState,
        string? logicJs,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        var switcherOptionsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (node.Options.Count == 0)
            {
                continue;
            }

            if (!(string.Equals(node.Type, "Tabs", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(node.Type, "SelectorBar", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(node.Type, "Segmented", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(node.Type, "Select", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(node.Type, "RadioGroup", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string? leafKey = ExtractLeafStateKey(node.Bind);
            if (string.IsNullOrWhiteSpace(leafKey))
            {
                continue;
            }

            if (!switcherOptionsByKey.TryGetValue(leafKey, out HashSet<string>? optSet))
            {
                optSet = new HashSet<string>(StringComparer.Ordinal);
                switcherOptionsByKey[leafKey] = optSet;
            }

            foreach (CustomWidgetOptionSpec opt in node.Options)
            {
                if (!string.IsNullOrWhiteSpace(opt.Value))
                {
                    optSet.Add(opt.Value.Trim());
                }
            }
        }

        if (switcherOptionsByKey.Count == 0)
        {
            return;
        }

        var staticSetValuesByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dynamicWrittenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomWidgetActionSpec action in manifest.EnumerateAllActions())
        {
            CollectStateSetTargets(action, staticSetValuesByKey, dynamicWrittenKeys);
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (string.IsNullOrWhiteSpace(node.When))
            {
                continue;
            }

            string trimmedWhen = node.When.Trim();
            if (trimmedWhen.StartsWith("${", StringComparison.Ordinal) && trimmedWhen.EndsWith('}'))
            {
                trimmedWhen = trimmedWhen[2..^1].Trim();
            }

            Match m = SimpleStateEqualityRegex().Match(trimmedWhen);
            if (!m.Success)
            {
                continue;
            }

            string key = m.Groups[1].Value;
            string expectedVal = m.Groups[2].Value;
            if (!switcherOptionsByKey.TryGetValue(key, out HashSet<string>? switcherOpts) ||
                dynamicWrittenKeys.Contains(key))
            {
                continue;
            }

            if (switcherOpts.Contains(expectedVal))
            {
                continue;
            }

            if (initialState.TryGetValue(key, out string? initVal) &&
                string.Equals(initVal?.Trim(), expectedVal, StringComparison.Ordinal))
            {
                continue;
            }

            if (staticSetValuesByKey.TryGetValue(key, out HashSet<string>? setVals) &&
                setVals.Contains(expectedVal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(logicJs) &&
                (logicJs.Contains($"'{expectedVal}'", StringComparison.Ordinal) ||
                 logicJs.Contains($"\"{expectedVal}\"", StringComparison.Ordinal)))
            {
                continue;
            }

            string available = string.Join(", ", switcherOpts.OrderBy(static s => s, StringComparer.Ordinal));
            diagnostics.Add(new CustomWidgetValidationDiagnostic(
                $"{node.NodePath}.when",
                $"Node '{node.Id ?? node.NodePath}' checks \"state.{key} == '{expectedVal}'\", but '{expectedVal}' is unreachable from initial state, switcher options ({available}), state.set actions, or logic.js. Add '{expectedVal}' to the switcher options or remove the dead branch.",
                IsError: true,
                Code: "BehavioralUnreachableStateBranch"));
        }
    }

    private static void CollectStateSetTargets(
        CustomWidgetActionSpec action,
        Dictionary<string, HashSet<string>> staticValues,
        HashSet<string> dynamicKeys)
    {
        if (string.Equals(action.Type, "state.set", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(action.Key))
        {
            string key = ExtractLeafStateKey(action.Key) ?? action.Key.Trim();
            string rawVal = action.Value ?? string.Empty;
            if (rawVal.Contains("${", StringComparison.Ordinal))
            {
                dynamicKeys.Add(key);
            }
            else
            {
                if (!staticValues.TryGetValue(key, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    staticValues[key] = set;
                }

                set.Add(rawVal.Trim());
            }
        }

        if (action.Then is { Count: > 0 })
        {
            foreach (CustomWidgetActionSpec next in action.Then)
            {
                CollectStateSetTargets(next, staticValues, dynamicKeys);
            }
        }
    }

    private static void CheckDuplicateSiblingButtons(
        CustomWidgetManifest manifest,
        CustomWidgetEvaluationContext baseCtx,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        foreach (CustomWidgetNodeSpec container in manifest.EnumerateAllNodes())
        {
            if (container.Children.Count < 2)
            {
                continue;
            }

            var buttonsByLabel = new Dictionary<string, List<CustomWidgetNodeSpec>>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomWidgetNodeSpec child in container.Children)
            {
                if (!(string.Equals(child.Type, "Button", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(child.Type, "IconButton", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string rawLabel = (child.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(rawLabel) || rawLabel.Contains("${", StringComparison.Ordinal))
                {
                    continue;
                }

                string groupKey = $"{child.Type}:{rawLabel}";
                if (!buttonsByLabel.TryGetValue(groupKey, out List<CustomWidgetNodeSpec>? list))
                {
                    list = [];
                    buttonsByLabel[groupKey] = list;
                }

                list.Add(child);
            }

            foreach (List<CustomWidgetNodeSpec> siblings in buttonsByLabel.Values)
            {
                if (siblings.Count < 2)
                {
                    continue;
                }

                bool foundSimultaneousPair = false;
                CustomWidgetNodeSpec? dupNode = null;
                for (int i = 0; i < siblings.Count && !foundSimultaneousPair; i++)
                {
                    for (int j = i + 1; j < siblings.Count; j++)
                    {
                        string w1 = (siblings[i].When ?? string.Empty).Trim();
                        string w2 = (siblings[j].When ?? string.Empty).Trim();
                        bool sameGuard = string.Equals(w1, w2, StringComparison.Ordinal);
                        bool bothVisibleInitially =
                            (string.IsNullOrEmpty(w1) || CustomWidgetExpressionEvaluator.EvaluateCondition(w1, baseCtx)) &&
                            (string.IsNullOrEmpty(w2) || CustomWidgetExpressionEvaluator.EvaluateCondition(w2, baseCtx));
                        if (sameGuard || bothVisibleInitially)
                        {
                            foundSimultaneousPair = true;
                            dupNode = siblings[j];
                            break;
                        }
                    }
                }

                if (foundSimultaneousPair && dupNode is not null)
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        dupNode.NodePath,
                        $"Duplicate sibling {dupNode.Type} with identical label '{dupNode.Text?.Trim()}' inside container '{container.Id ?? container.NodePath}'. Remove the obsolete duplicate button or guard mutually exclusive variants with 'when'.",
                        IsError: true,
                        Code: "BehavioralDuplicateSiblingButton"));
                }
            }
        }
    }

    private sealed record DryRunInvocation(
        string FunctionName,
        CustomWidgetActionSpec Action,
        CustomWidgetNodeSpec OwnerNode,
        IReadOnlyList<CustomWidgetActionSpec> OwnerActionList,
        CustomWidgetRecord? SampleItem,
        bool IsOnChange,
        bool IsReachableInitially,
        string? EnclosingPopoverId);

    private static void RunScriptHandlerSmokeCheck(
        CustomWidgetManifest manifest,
        string logicJs,
        CustomWidgetEvaluationContext baseCtx,
        Dictionary<string, string> initialState,
        Dictionary<string, JsonElement> initialStateJson,
        Dictionary<string, List<CustomWidgetRecord>> initialCollections,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        CustomWidgetScriptCompileResult compileRes = CustomWidgetScriptEngine.Compile(logicJs);
        if (!compileRes.IsSuccess || compileRes.Compiled is null)
        {
            return;
        }

        CustomWidgetCompiledScript compiled = compileRes.Compiled;
        {
            var workingState = new Dictionary<string, string>(initialState, StringComparer.Ordinal);
            var workingStateJson = new Dictionary<string, JsonElement>(initialStateJson, StringComparer.Ordinal);
            var workingCollections = CloneCollections(initialCollections);
            var workingComputed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            // Seed realistic computed & state values by running onInit once if exported.
            if (compiled.ExportedFunctions.Contains("onInit"))
            {
                var initInput = new CustomWidgetScriptExecutionInput
                {
                    State = workingState,
                    StateJson = workingStateJson,
                    Collections = workingCollections,
                    Settings = baseCtx.Settings,
                    Computed = workingComputed,
                    CultureName = baseCtx.CultureName,
                    CallKind = CustomWidgetScriptCallKind.Lifecycle,
                    IsPreview = true,
                    AllowCopy = false,
                    TimeoutMs = 30
                };
                CustomWidgetScriptExecutionResult initRes = CustomWidgetScriptEngine.Execute(
                    compiled,
                    "onInit",
                    initInput,
                    requireExport: false);
                if (initRes.IsSuccess && initRes.Invoked)
                {
                    MergeScriptResult(initRes, workingState, workingStateJson, workingCollections, workingComputed);
                    if (!string.IsNullOrWhiteSpace(initRes.DispatchedActionsRawJson))
                    {
                        CustomWidgetActionSchemaValidator.ValidateRawDispatchedActionsJson(
                            initRes.DispatchedActionsRawJson,
                            "onInit",
                            manifest,
                            compiled.ExportedFunctions,
                            diagnostics);
                    }
                }
            }

            CustomWidgetEvaluationContext hydratedCtx = baseCtx with
            {
                State = workingState,
                StateJson = workingStateJson,
                Collections = workingCollections,
                Computed = workingComputed
            };

            // Map each declared popover to its exclusive editable form state keys (keys not also bound in main view)
            var mainViewEditableKeys = new HashSet<string>(StringComparer.Ordinal);
            CollectEditableStateKeys(manifest.View, mainViewEditableKeys);
            var formKeysByPopoverId = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
            {
                string? popId = pop.PopoverId ?? pop.Id;
                if (string.IsNullOrWhiteSpace(popId))
                {
                    continue;
                }

                var keys = new HashSet<string>(StringComparer.Ordinal);
                CollectEditableStateKeys(pop, keys);
                keys.ExceptWith(mainViewEditableKeys);
                if (keys.Count >= 2)
                {
                    formKeysByPopoverId[popId] = keys;
                }
            }

            var invocations = new List<DryRunInvocation>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            CollectScriptInvocations(
                manifest.View,
                hydratedCtx,
                workingCollections,
                workingComputed,
                parentReachable: true,
                inheritedItem: null,
                insideItemTemplate: false,
                enclosingPopoverId: null,
                invocations,
                seenKeys);

            foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
            {
                string? popId = pop.PopoverId ?? pop.Id;
                CollectScriptInvocations(
                    pop,
                    hydratedCtx,
                    workingCollections,
                    workingComputed,
                    parentReachable: true,
                    inheritedItem: null,
                    insideItemTemplate: false,
                    enclosingPopoverId: popId,
                    invocations,
                    seenKeys);
            }

            foreach (DryRunInvocation inv in invocations)
            {
                if (!compiled.ExportedFunctions.Contains(inv.FunctionName))
                {
                    continue;
                }

                CustomWidgetEvaluationContext callCtx = inv.SampleItem is not null
                    ? hydratedCtx.WithItem(inv.SampleItem, 0)
                    : hydratedCtx;
                string? resolvedArg = !string.IsNullOrWhiteSpace(inv.Action.Arg)
                    ? CustomWidgetExpressionEvaluator.Interpolate(inv.Action.Arg, callCtx)
                    : null;
                Dictionary<string, string>? resolvedArgs = null;
                if (inv.Action.Args is { Count: > 0 })
                {
                    resolvedArgs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach ((string ak, string av) in inv.Action.Args)
                    {
                        resolvedArgs[ak] = CustomWidgetExpressionEvaluator.Interpolate(av, callCtx);
                    }
                }

                var execInput = new CustomWidgetScriptExecutionInput
                {
                    State = new Dictionary<string, string>(workingState, StringComparer.Ordinal),
                    StateJson = new Dictionary<string, JsonElement>(workingStateJson, StringComparer.Ordinal),
                    Collections = CloneCollections(workingCollections),
                    Settings = hydratedCtx.Settings,
                    Computed = workingComputed,
                    CultureName = hydratedCtx.CultureName,
                    Args = resolvedArgs,
                    Event = new CustomWidgetScriptEventPayload(
                        Arg: resolvedArg ?? string.Empty,
                        Value: inv.IsOnChange ? "probe" : string.Empty,
                        Item: inv.SampleItem,
                        Args: resolvedArgs),
                    CallKind = CustomWidgetScriptCallKind.UiEvent,
                    IsPreview = true,
                    AllowCopy = false,
                    TimeoutMs = 25
                };

                CustomWidgetScriptExecutionResult res = CustomWidgetScriptEngine.Execute(
                    compiled,
                    inv.FunctionName,
                    execInput,
                    requireExport: false);

                if (!res.IsSuccess)
                {
                    if (inv.IsReachableInitially && res.Status != CustomWidgetScriptStatus.ExportNotFound)
                    {
                        string linePart = res.ErrorLine is > 0 ? $" (line {res.ErrorLine})" : string.Empty;
                        diagnostics.Add(new CustomWidgetValidationDiagnostic(
                            $"logic.js:{inv.FunctionName}()",
                            $"Dry-run of interactive handler '{inv.FunctionName}' (wired on {inv.OwnerNode.NodePath}) threw a runtime error{linePart}: {res.ErrorMessage}",
                            IsError: true,
                            Code: "BehavioralScriptHandlerRuntimeError"));
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(res.DispatchedActionsRawJson))
                {
                    CustomWidgetActionSchemaValidator.ValidateRawDispatchedActionsJson(
                        res.DispatchedActionsRawJson,
                        inv.FunctionName,
                        manifest,
                        compiled.ExportedFunctions,
                        diagnostics);
                }

                // Check 6: Form-edit handler outside popover that populates >= 2 popover form keys without opening the popover
                if (inv.EnclosingPopoverId is null &&
                    string.IsNullOrWhiteSpace(inv.OwnerNode.Popover) &&
                    formKeysByPopoverId.Count > 0 &&
                    (res.StateSet.Count >= 2 || res.StateSetJson.Count >= 2))
                {
                    bool opensAnyPopover =
                        ActionsOpenPopover(inv.OwnerActionList) ||
                        ActionsOpenPopover(res.DispatchedActions);
                    if (!opensAnyPopover)
                    {
                        var mutatedKeys = new HashSet<string>(StringComparer.Ordinal);
                        bool hasNonEmptyValue = false;
                        foreach ((string mk, string mv) in res.StateSet)
                        {
                            mutatedKeys.Add(mk);
                            if (!string.IsNullOrWhiteSpace(mv))
                            {
                                hasNonEmptyValue = true;
                            }
                        }
                        foreach ((string mk, JsonElement mj) in res.StateSetJson)
                        {
                            mutatedKeys.Add(mk);
                            if (mj.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mj.GetString()))
                            {
                                hasNonEmptyValue = true;
                            }
                            else if (mj.ValueKind is JsonValueKind.Number or JsonValueKind.True)
                            {
                                hasNonEmptyValue = true;
                            }
                        }

                        if (hasNonEmptyValue)
                        {
                            foreach ((string popId, HashSet<string> popFormKeys) in formKeysByPopoverId)
                            {
                                int overlap = 0;
                                foreach (string k in mutatedKeys)
                                {
                                    if (popFormKeys.Contains(k))
                                    {
                                        overlap++;
                                    }
                                }

                                if (overlap >= 2)
                                {
                                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                                        $"logic.js:{inv.FunctionName}()",
                                        $"Handler '{inv.FunctionName}' (wired on {inv.OwnerNode.NodePath}) populates {overlap} form fields bound inside popover '{popId}', but neither the button nor logic.js dispatches '{{ \"type\": \"popover.open\", \"popoverId\": \"{popId}\" }}'. Users clicking this button will never see the editor popover open.",
                                        IsError: true,
                                        Code: "BehavioralFormEditMissingPopoverOpen"));
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static void CollectScriptInvocations(
        CustomWidgetNodeSpec? node,
        CustomWidgetEvaluationContext ctx,
        Dictionary<string, List<CustomWidgetRecord>> collections,
        Dictionary<string, JsonElement> computed,
        bool parentReachable,
        CustomWidgetRecord? inheritedItem,
        bool insideItemTemplate,
        string? enclosingPopoverId,
        List<DryRunInvocation> output,
        HashSet<string> seenKeys)
    {
        if (node is null || output.Count >= MaxDryRunHandlers)
        {
            return;
        }

        bool selfReachable = parentReachable;
        if (selfReachable && !string.IsNullOrWhiteSpace(node.When))
        {
            CustomWidgetEvaluationContext condCtx = inheritedItem is not null ? ctx.WithItem(inheritedItem, 0) : ctx;
            selfReachable = CustomWidgetExpressionEvaluator.EvaluateCondition(node.When, condCtx);
        }

        void EnqueueActionList(IReadOnlyList<CustomWidgetActionSpec> actions, bool isOnChange)
        {
            if (actions.Count == 0 || output.Count >= MaxDryRunHandlers)
            {
                return;
            }

            if (insideItemTemplate && inheritedItem is null)
            {
                return;
            }

            foreach (CustomWidgetActionSpec act in actions)
            {
                if (output.Count >= MaxDryRunHandlers)
                {
                    break;
                }

                if (string.Equals(act.Type, "script.run", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(act.Fn))
                {
                    string fn = act.Fn.Trim();
                    string dedup = $"{fn}|{act.Arg ?? string.Empty}|{inheritedItem is not null}|{enclosingPopoverId ?? string.Empty}|{isOnChange}";
                    if (seenKeys.Add(dedup))
                    {
                        output.Add(new DryRunInvocation(
                            fn,
                            act,
                            node,
                            actions,
                            inheritedItem,
                            isOnChange,
                            selfReachable,
                            enclosingPopoverId));
                    }
                }
            }
        }

        EnqueueActionList(node.OnClick, isOnChange: false);
        EnqueueActionList(node.OnSubmit, isOnChange: false);
        EnqueueActionList(node.OnChange, isOnChange: true);
        EnqueueActionList(node.OnRowClick, isOnChange: false);
        EnqueueActionList(node.OnItemClick, isOnChange: false);
        foreach (CustomWidgetMenuItemSpec mi in node.MenuItems)
        {
            if (mi.OnClick is { Count: > 0 })
            {
                EnqueueActionList(mi.OnClick, isOnChange: false);
            }
        }

        if (node.ItemTemplate is not null)
        {
            CustomWidgetRecord? sampleItem = ResolveSampleRecordForNode(node, collections, computed);
            CollectScriptInvocations(
                node.ItemTemplate,
                ctx,
                collections,
                computed,
                selfReachable,
                sampleItem,
                insideItemTemplate: true,
                enclosingPopoverId,
                output,
                seenKeys);
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            CollectScriptInvocations(
                child,
                ctx,
                collections,
                computed,
                selfReachable,
                inheritedItem,
                insideItemTemplate,
                enclosingPopoverId,
                output,
                seenKeys);
        }

        foreach (CustomWidgetNodeSpec hover in node.HoverActions)
        {
            CollectScriptInvocations(
                hover,
                ctx,
                collections,
                computed,
                selfReachable,
                inheritedItem,
                insideItemTemplate,
                enclosingPopoverId,
                output,
                seenKeys);
        }

        if (node.Pane is not null)
        {
            CollectScriptInvocations(
                node.Pane,
                ctx,
                collections,
                computed,
                selfReachable,
                inheritedItem,
                insideItemTemplate,
                enclosingPopoverId,
                output,
                seenKeys);
        }

        if (node.ActionButton is not null)
        {
            CollectScriptInvocations(
                node.ActionButton,
                ctx,
                collections,
                computed,
                selfReachable,
                inheritedItem,
                insideItemTemplate,
                enclosingPopoverId,
                output,
                seenKeys);
        }
    }

    private static CustomWidgetRecord? ResolveSampleRecordForNode(
        CustomWidgetNodeSpec colNode,
        Dictionary<string, List<CustomWidgetRecord>> collections,
        Dictionary<string, JsonElement> computed)
    {
        string? colName = ResolveCollectionName(colNode);
        if (!string.IsNullOrWhiteSpace(colName) &&
            collections.TryGetValue(colName, out List<CustomWidgetRecord>? rows) &&
            rows.Count > 0)
        {
            return rows[0];
        }

        string? rawItems = colNode.Items ?? colNode.Collection;
        if (!string.IsNullOrWhiteSpace(rawItems))
        {
            string s = rawItems.Trim();
            if (s.StartsWith("${", StringComparison.Ordinal) && s.EndsWith('}'))
            {
                s = s[2..^1].Trim();
            }

            if (s.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase))
            {
                string key = s["$computed.".Length..].Trim();
                if (computed.TryGetValue(key, out JsonElement arrEl) &&
                    arrEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement itemEl in arrEl.EnumerateArray())
                    {
                        if (itemEl.ValueKind == JsonValueKind.Object)
                        {
                            var rec = new CustomWidgetRecord { Id = "sample-0" };
                            foreach (JsonProperty prop in itemEl.EnumerateObject())
                            {
                                rec.FieldsJson[prop.Name] = prop.Value.Clone();
                                rec.Fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                                    ? (prop.Value.GetString() ?? string.Empty)
                                    : prop.Value.GetRawText();
                                if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase) &&
                                    prop.Value.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                                {
                                    rec.Id = prop.Value.GetString()!;
                                }
                            }
                            return rec;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static void CollectEditableStateKeys(CustomWidgetNodeSpec? node, HashSet<string> keys)
    {
        if (node is null)
        {
            return;
        }

        foreach (CustomWidgetNodeSpec n in node.EnumerateAllNodes())
        {
            if (string.Equals(n.Type, "Input", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "TextArea", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "Select", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "DatePicker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "TimePicker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "NumberBox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "Toggle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "CheckBox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "ColorPicker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "Segmented", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n.Type, "RadioGroup", StringComparison.OrdinalIgnoreCase))
            {
                string? leaf = ExtractLeafStateKey(n.Bind);
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    keys.Add(leaf);
                }
            }
        }
    }

    private static bool ActionsOpenPopover(IReadOnlyList<CustomWidgetActionSpec>? actions)
    {
        if (actions is null || actions.Count == 0)
        {
            return false;
        }

        foreach (CustomWidgetActionSpec a in actions)
        {
            if (string.Equals(a.Type, "popover.open", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Type, "popover.toggle", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ActionsOpenPopover(a.Then))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, List<CustomWidgetRecord>> CloneCollections(
        Dictionary<string, List<CustomWidgetRecord>> source)
    {
        var copy = new Dictionary<string, List<CustomWidgetRecord>>(source.Count, StringComparer.Ordinal);
        foreach ((string k, List<CustomWidgetRecord> list) in source)
        {
            copy[k] = new List<CustomWidgetRecord>(list);
        }

        return copy;
    }

    private static void MergeScriptResult(
        CustomWidgetScriptExecutionResult res,
        Dictionary<string, string> state,
        Dictionary<string, JsonElement> stateJson,
        Dictionary<string, List<CustomWidgetRecord>> collections,
        Dictionary<string, JsonElement> computed)
    {
        foreach (string removed in res.StateRemoved)
        {
            state.Remove(removed);
            stateJson.Remove(removed);
        }

        foreach ((string k, JsonElement el) in res.StateSetJson)
        {
            stateJson[k] = el.Clone();
            state[k] = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.GetRawText();
        }

        foreach ((string k, string v) in res.StateSet)
        {
            if (!res.StateSetJson.ContainsKey(k))
            {
                state[k] = v;
                stateJson[k] = CustomWidgetScriptEngine.CreateStringJsonElement(v);
            }
        }

        foreach ((string col, List<CustomWidgetRecord> items) in res.DirtyCollections)
        {
            collections[col] = new List<CustomWidgetRecord>(items);
        }

        if (res.Computed.Count > 0)
        {
            computed.Clear();
            foreach ((string ck, JsonElement cv) in res.Computed)
            {
                computed[ck] = cv.Clone();
            }
        }
    }

    private static bool HasDownstreamConsumer(
        string stateKey,
        CustomWidgetNodeSpec sourceInputNode,
        CustomWidgetManifest manifest,
        string? logicJs)
    {
        if (!string.IsNullOrWhiteSpace(logicJs) &&
            logicJs.Contains(stateKey, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (ContainsStateToken(src.Url, stateKey) || ContainsStateToken(src.Path, stateKey))
            {
                return true;
            }
        }

        if (manifest.Capsule is not null &&
            (ContainsStateToken(manifest.Capsule.Title, stateKey) ||
             ContainsStateToken(manifest.Capsule.Summary, stateKey) ||
             ContainsStateToken(manifest.Capsule.Badge, stateKey)))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (NodeConsumesStateKey(node, sourceInputNode, stateKey))
            {
                return true;
            }
        }

        return ActionsConsumeStateKey(manifest.EnumerateAllActions(), stateKey);
    }

    private static bool NodeConsumesStateKey(
        CustomWidgetNodeSpec node,
        CustomWidgetNodeSpec sourceInputNode,
        string stateKey)
    {
        bool isSelfInput = ReferenceEquals(node, sourceInputNode);
        if (!isSelfInput && ContainsStateToken(node.Bind, stateKey))
        {
            return true;
        }

        if (ContainsStateToken(node.When, stateKey) ||
            ContainsStateToken(node.Filter, stateKey) ||
            ContainsStateToken(node.FilterEquals, stateKey) ||
            ContainsStateToken(node.Text, stateKey) ||
            ContainsStateToken(node.Title, stateKey) ||
            ContainsStateToken(node.Subtitle, stateKey) ||
            ContainsStateToken(node.Value, stateKey) ||
            ContainsStateToken(node.Items, stateKey) ||
            node.Spans.Any(s => ContainsStateToken(s.Text, stateKey)))
        {
            return true;
        }

        return ActionsConsumeStateKey(node.EnumerateAllActions(), stateKey);
    }

    private static bool ActionsConsumeStateKey(IEnumerable<CustomWidgetActionSpec> actions, string stateKey)
    {
        foreach (CustomWidgetActionSpec a in actions)
        {
            if (ContainsStateToken(a.Value, stateKey) ||
                ContainsStateToken(a.Arg, stateKey) ||
                ContainsStateToken(a.Url, stateKey) ||
                ContainsStateToken(a.Text, stateKey) ||
                ContainsStateToken(a.Guard, stateKey) ||
                (a.ItemFields is not null && a.ItemFields.Values.Any(v => ContainsStateToken(v, stateKey))) ||
                (a.Args is not null && a.Args.Values.Any(v => ContainsStateToken(v, stateKey))) ||
                (a.Then is not null && ActionsConsumeStateKey(a.Then, stateKey)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsStateToken(string? expr, string stateKey) =>
        !string.IsNullOrWhiteSpace(expr) &&
        (expr.Contains($"state.{stateKey}", StringComparison.Ordinal) ||
         string.Equals(expr.Trim(), stateKey, StringComparison.Ordinal));

    private static bool IsTextBearingNodeType(string? nodeType) =>
        string.Equals(nodeType, "Text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "Badge", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "Markdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "CodeBlock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "RichText", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(nodeType, "StatCard", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveCollectionName(CustomWidgetNodeSpec node)
    {
        string? raw = !string.IsNullOrWhiteSpace(node.Collection) ? node.Collection : node.Items;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string s = raw.Trim();
        if (s.StartsWith("${", StringComparison.Ordinal) && s.EndsWith('}'))
        {
            s = s[2..^1].Trim();
        }
        if (s.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
        {
            s = s[12..].Trim();
        }

        return !string.IsNullOrWhiteSpace(s) && !s.Contains('.', StringComparison.Ordinal) && !s.Contains('[', StringComparison.Ordinal)
            ? s
            : null;
    }

    private static string? ExtractLeafStateKey(string? rawBind)
    {
        if (string.IsNullOrWhiteSpace(rawBind))
        {
            return null;
        }

        string s = rawBind.Trim();
        if (s.StartsWith("${", StringComparison.Ordinal) && s.EndsWith('}'))
        {
            s = s[2..^1].Trim();
        }
        if (s.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            s = s[6..].Trim();
        }
        else if (s.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(s) && !s.Contains('.', StringComparison.Ordinal) ? s : null;
    }
}
