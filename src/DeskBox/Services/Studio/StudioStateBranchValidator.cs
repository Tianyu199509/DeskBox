using System.Buffers;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Validates root-view visibility invariants and static state-switcher branch coverage
/// across declarative and scripted custom widgets without false positives on filter bars.
/// </summary>
internal static partial class StudioStateBranchValidator
{
    [GeneratedRegex(@"^(?:\$\{\s*)?\$?state\.(?<key>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleStateBindRegex();

    private static readonly FrozenSet<string> SwitcherTypes = new[]
    {
        "Segmented",
        "SelectorBar",
        "RadioGroup"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BranchContainerOrSurfaceTypes = new[]
    {
        "Stack",
        "Grid",
        "Card",
        "Scroll",
        "ItemsGrid",
        "List",
        "BarList",
        "Table",
        "Chart",
        "DonutChart",
        "Sparkline",
        "HeatmapGrid",
        "Canvas",
        "FlipView",
        "EmptyState",
        "Markdown",
        "RichText",
        "CodeBlock",
        "SplitView",
        "Expander"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> PrimaryDataSurfaceTypes = new[]
    {
        "ItemsGrid",
        "List",
        "BarList",
        "Table",
        "Chart",
        "DonutChart",
        "HeatmapGrid",
        "Canvas",
        "FlipView"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static void Validate(
        CustomWidgetManifest manifest,
        CustomWidgetEvaluationContext evalCtx,
        List<CustomWidgetValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evalCtx);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!string.IsNullOrWhiteSpace(manifest.View.When))
        {
            return;
        }

        var switchers = new List<(CustomWidgetNodeSpec Node, string StateKey)>();
        CollectSwitchers(manifest.View, switchers);
        if (switchers.Count == 0)
        {
            return;
        }

        foreach ((CustomWidgetNodeSpec switcherNode, string stateKey) in switchers)
        {
            var branchNodes = new HashSet<CustomWidgetNodeSpec>(ReferenceEqualityComparer.Instance);
            CollectBranchCandidates(manifest.View, switcherNode, stateKey, branchNodes);
            if (branchNodes.Count == 0)
            {
                continue;
            }

            if (HasUnconditionalPrimaryDataSurface(manifest.View, stateKey, ancestorGuardedByStateKey: false) ||
                AnyExternalNodeUsesStateKeyAsFilterOrText(manifest.View, switcherNode, stateKey))
            {
                continue;
            }

            int deadOptionsEmitted = 0;
            foreach (CustomWidgetOptionSpec opt in switcherNode.Options)
            {
                CustomWidgetEvaluationContext simCtx = BuildSimulatedContext(evalCtx, stateKey, opt.Value);
                int visibleBranches = CountVisibleBranches(manifest.View, simCtx, branchNodes);
                if (visibleBranches == 0)
                {
                    diagnostics.Add(new CustomWidgetValidationDiagnostic(
                        switcherNode.NodePath,
                        $"Switcher option \"{opt.Value}\" ({opt.Label}) bound to \"state.{stateKey}\" leaves all conditional \"state.{stateKey}\" view branches hidden. Add a matching view branch (e.g. when: \"${{state.{stateKey} == '{opt.Value}'}}\") or remove the dead option.",
                        IsError: false,
                        Code: CustomWidgetStrictAuthoringRules.DeadStateSwitchBranch));
                    deadOptionsEmitted++;
                    if (deadOptionsEmitted >= 2)
                    {
                        break;
                    }
                }
            }
        }
    }

    private static void CollectSwitchers(
        CustomWidgetNodeSpec? node,
        List<(CustomWidgetNodeSpec Node, string StateKey)> results)
    {
        if (node is null)
        {
            return;
        }

        if (SwitcherTypes.Contains(node.Type) &&
            node.Options.Count is >= 2 and <= 8 &&
            node.Options.All(o => !string.IsNullOrWhiteSpace(o.Value) && !o.Value.Contains("${", StringComparison.Ordinal)) &&
            !node.OnChange.Any(a => a.Type.StartsWith("script.", StringComparison.OrdinalIgnoreCase)))
        {
            string? rawBind = !string.IsNullOrWhiteSpace(node.Bind) ? node.Bind : node.Value;
            if (!string.IsNullOrWhiteSpace(rawBind))
            {
                Match m = SimpleStateBindRegex().Match(rawBind.Trim());
                if (m.Success)
                {
                    results.Add((node, m.Groups["key"].Value));
                }
            }
        }

        if (node.Pane is not null)
        {
            CollectSwitchers(node.Pane, results);
        }

        if (node.ActionButton is not null)
        {
            CollectSwitchers(node.ActionButton, results);
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            CollectSwitchers(child, results);
        }
    }

    private static void CollectBranchCandidates(
        CustomWidgetNodeSpec? node,
        CustomWidgetNodeSpec switcherNode,
        string stateKey,
        HashSet<CustomWidgetNodeSpec> branchNodes)
    {
        if (node is null || ReferenceEquals(node, switcherNode))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.When) &&
            BranchContainerOrSurfaceTypes.Contains(node.Type) &&
            ContainsIdentifierRef(node.When, "state", stateKey) &&
            !ContainsComputedPrefix(node.When) &&
            !node.When.Contains("!=", StringComparison.Ordinal) &&
            !HasLeadingNegation(node.When))
        {
            branchNodes.Add(node);
        }

        if (node.Pane is not null)
        {
            CollectBranchCandidates(node.Pane, switcherNode, stateKey, branchNodes);
        }

        if (node.ActionButton is not null)
        {
            CollectBranchCandidates(node.ActionButton, switcherNode, stateKey, branchNodes);
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            CollectBranchCandidates(child, switcherNode, stateKey, branchNodes);
        }
    }

    private static bool HasUnconditionalPrimaryDataSurface(
        CustomWidgetNodeSpec? node,
        string stateKey,
        bool ancestorGuardedByStateKey)
    {
        if (node is null)
        {
            return false;
        }

        bool guarded = ancestorGuardedByStateKey ||
                       (!string.IsNullOrWhiteSpace(node.When) && ContainsIdentifierRef(node.When, "state", stateKey));

        if (!guarded && PrimaryDataSurfaceTypes.Contains(node.Type))
        {
            return true;
        }

        if (HasUnconditionalPrimaryDataSurface(node.Pane, stateKey, guarded) ||
            HasUnconditionalPrimaryDataSurface(node.ActionButton, stateKey, guarded))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (HasUnconditionalPrimaryDataSurface(child, stateKey, guarded))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyExternalNodeUsesStateKeyAsFilterOrText(
        CustomWidgetNodeSpec? node,
        CustomWidgetNodeSpec switcherNode,
        string stateKey)
    {
        if (node is null || ReferenceEquals(node, switcherNode))
        {
            return false;
        }

        if (ContainsIdentifierRef(node.Filter, "state", stateKey) ||
            ContainsIdentifierRef(node.FilterEquals, "state", stateKey) ||
            ContainsIdentifierRef(node.Collection, "state", stateKey) ||
            ContainsIdentifierRef(node.Items, "state", stateKey) ||
            ContainsIdentifierRef(node.Text, "state", stateKey) ||
            ContainsIdentifierRef(node.Value, "state", stateKey))
        {
            return true;
        }

        if (AnyExternalNodeUsesStateKeyAsFilterOrText(node.Pane, switcherNode, stateKey) ||
            AnyExternalNodeUsesStateKeyAsFilterOrText(node.ActionButton, switcherNode, stateKey) ||
            AnyExternalNodeUsesStateKeyAsFilterOrText(node.ItemTemplate, switcherNode, stateKey))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            if (AnyExternalNodeUsesStateKeyAsFilterOrText(child, switcherNode, stateKey))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountVisibleBranches(
        CustomWidgetNodeSpec? node,
        CustomWidgetEvaluationContext simCtx,
        HashSet<CustomWidgetNodeSpec> branchNodes)
    {
        if (node is null)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(node.When) &&
            !CustomWidgetExpressionEvaluator.EvaluateCondition(node.When, simCtx))
        {
            return 0;
        }

        int count = branchNodes.Contains(node) ? 1 : 0;
        count += CountVisibleBranches(node.Pane, simCtx, branchNodes);
        count += CountVisibleBranches(node.ActionButton, simCtx, branchNodes);
        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            count += CountVisibleBranches(child, simCtx, branchNodes);
        }

        return count;
    }

    private static CustomWidgetEvaluationContext BuildSimulatedContext(
        CustomWidgetEvaluationContext evalCtx,
        string stateKey,
        string optionValue)
    {
        var simState = new Dictionary<string, string>(evalCtx.State, StringComparer.Ordinal)
        {
            [stateKey] = optionValue
        };
        var simStateJson = new Dictionary<string, JsonElement>(evalCtx.StateJson, StringComparer.Ordinal)
        {
            [stateKey] = CreateTypedJsonElement(
                optionValue,
                evalCtx.StateJson.TryGetValue(stateKey, out JsonElement original) ? original.ValueKind : JsonValueKind.String)
        };
        return evalCtx with { State = simState, StateJson = simStateJson };
    }

    /// <summary>
    /// Writes the option value with the same JSON kind as the declared state default so that
    /// <c>state.tab == 1</c> / <c>state.on == true</c> branches compare like the runtime would.
    /// </summary>
    private static JsonElement CreateTypedJsonElement(string value, JsonValueKind declaredKind)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(16, value.Length + 4));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (declaredKind == JsonValueKind.Number &&
                double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) &&
                double.IsFinite(number))
            {
                writer.WriteNumberValue(number);
            }
            else if (declaredKind is JsonValueKind.True or JsonValueKind.False &&
                     bool.TryParse(value, out bool flag))
            {
                writer.WriteBooleanValue(flag);
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    private static bool ContainsIdentifierRef(string? expr, string rootPrefix, string key)
    {
        if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        string needle = $"{rootPrefix}.{key}";
        int idx = 0;
        while (idx < expr.Length)
        {
            int found = expr.IndexOf(needle, idx, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            int prefixStart = found > 0 && expr[found - 1] == '$' ? found - 1 : found;
            bool leftOk = prefixStart == 0 || !IsIdentifierChar(expr[prefixStart - 1]);
            int afterIdx = found + needle.Length;
            bool rightOk = afterIdx >= expr.Length || !IsIdentifierChar(expr[afterIdx]);
            if (leftOk && rightOk)
            {
                return true;
            }

            idx = found + 1;
        }

        return false;
    }

    private static bool ContainsComputedPrefix(string expr)
    {
        int idx = 0;
        while (idx < expr.Length)
        {
            int found = expr.IndexOf("computed.", idx, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            int prefixStart = found > 0 && expr[found - 1] == '$' ? found - 1 : found;
            if (prefixStart == 0 || !IsIdentifierChar(expr[prefixStart - 1]))
            {
                return true;
            }

            idx = found + 1;
        }

        return false;
    }

    private static bool HasLeadingNegation(string expr)
    {
        string trimmed = expr.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        return trimmed.StartsWith('!');
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
