using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public static partial class CustomWidgetHealthAnalyzer
{
    internal static bool HasRootFlexibleListOrScroll(
        CustomWidgetNodeSpec root,
        int depth = 0,
        CustomWidgetManifest? manifest = null)
    {
        if (depth > 6)
        {
            return false;
        }

        if (string.Equals(root.Type, "Scroll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "List", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Table", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(root.Type, "Tabs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "FlipView", StringComparison.OrdinalIgnoreCase))
        {
            return root.Children.Count > 0 &&
                   root.Children.All(child => HasRootFlexibleListOrScroll(child, depth + 1, manifest));
        }

        if (string.Equals(root.Type, "ModuleStack", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<CustomWidgetNodeSpec> enabledModules = ResolveDefaultEnabledModules(root, manifest);
            if (depth <= 1 &&
                !string.IsNullOrWhiteSpace(root.PinTrailingGroup) &&
                enabledModules.Count >= 2 &&
                string.Equals(
                    enabledModules[^1].PairGroup?.Trim(),
                    root.PinTrailingGroup.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (CustomWidgetNodeSpec child in enabledModules)
            {
                if (HasRootFlexibleListOrScroll(child, depth + 1, manifest))
                {
                    return true;
                }
            }

            return false;
        }

        // Recurse only through transparent layout containers (explicitly excluding Card so nested static Cards still trigger overflow).
        if (string.IsNullOrWhiteSpace(root.Type) ||
            string.Equals(root.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Box", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Tab", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "TabItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Section", StringComparison.OrdinalIgnoreCase))
        {
            foreach (CustomWidgetNodeSpec child in root.Children)
            {
                if (HasRootFlexibleListOrScroll(child, depth + 1, manifest))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<CustomWidgetNodeSpec> ResolveDefaultEnabledModules(
        CustomWidgetNodeSpec moduleStack,
        CustomWidgetManifest? manifest)
    {
        if (manifest is null || moduleStack.Children.Count == 0)
        {
            return moduleStack.Children;
        }

        string rawBind = moduleStack.Bind?.Trim() ?? string.Empty;
        if (rawBind.StartsWith("${", StringComparison.Ordinal) && rawBind.EndsWith('}'))
        {
            rawBind = rawBind[2..^1].Trim();
        }
        if (rawBind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
        {
            rawBind = rawBind["$settings.".Length..].Trim();
        }
        else if (rawBind.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
        {
            rawBind = rawBind["settings.".Length..].Trim();
        }

        CustomWidgetSettingDefinition? setting = null;
        if (!string.IsNullOrEmpty(rawBind))
        {
            setting = manifest.Settings.FirstOrDefault(s =>
                string.Equals(s.Key, rawBind, StringComparison.Ordinal));
        }

        setting ??= manifest.Settings.FirstOrDefault(static s =>
            string.Equals(s.Type, "moduleList", StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            return moduleStack.Children;
        }

        var enabled = new List<CustomWidgetNodeSpec>(moduleStack.Children.Count);
        foreach (CustomWidgetNodeSpec child in moduleStack.Children)
        {
            if (string.IsNullOrWhiteSpace(child.Id) ||
                CustomWidgetModuleListHelper.IsModuleEnabled(setting.DefaultValue, child.Id, setting.Options))
            {
                enabled.Add(child);
            }
        }

        return enabled;
    }

    public static double EstimateManifestLayoutHeight(
        CustomWidgetManifest manifest,
        out int overflowPx,
        out string? overflowNodePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        double availableBodyHeight = Math.Max(100, manifest.DefaultSize.Height - 36);
        double rootAvailableWidth = Math.Max(120, manifest.DefaultSize.Width - 28);
        double estimatedHeight = EstimateNodeHeightRecursive(
            manifest.View,
            rootAvailableWidth,
            parentGridColCount: 0,
            out string? deepestContributionPath,
            manifest);

        if (!HasRootFlexibleListOrScroll(manifest.View, depth: 0, manifest: manifest) && estimatedHeight > availableBodyHeight)
        {
            overflowPx = Math.Max(0, (int)Math.Ceiling(estimatedHeight - availableBodyHeight));
            overflowNodePath = deepestContributionPath ?? manifest.View.NodePath;
        }
        else
        {
            overflowPx = 0;
            overflowNodePath = null;
        }

        return Math.Round(estimatedHeight, 1);
    }

    private static double EstimatePopoverHeight(CustomWidgetNodeSpec popover)
    {
        double popWidth = popover.Width is > 100 and <= 600 ? popover.Width.Value : 320;
        return EstimateNodeHeightRecursive(popover, popWidth, parentGridColCount: 0, out _);
    }

    private static double EstimateNodeHeightRecursive(
        CustomWidgetNodeSpec? node,
        double availableWidth,
        int parentGridColCount,
        out string? tallestPath,
        CustomWidgetManifest? manifest = null)
    {
        tallestPath = node?.NodePath;
        if (node is null)
        {
            return 0;
        }

        if (node.Height is > 0 and < 4000)
        {
            return node.Height.Value;
        }

        TryParseEdgeThickness(node.Padding, out double padL, out double padT, out double padR, out double padB);
        TryParseEdgeThickness(node.Margin, out _, out double marT, out _, out double marB);
        double marginV = marT + marB;
        string type = node.Type?.Trim() ?? "Stack";

        if (string.Equals(type, "Popover", StringComparison.OrdinalIgnoreCase))
        {
            // Popovers render in floating surfaces, never contributing to main-view body height.
            return 0;
        }

        if (string.Equals(type, "Text", StringComparison.OrdinalIgnoreCase))
        {
            double lineH = node.Style.ToLowerInvariant() switch
            {
                "hero" => 38,
                "metric" => 30,
                "title" => 24,
                "subtitle" => 20,
                "caption" => 15,
                _ => 17
            };
            if (node.FontSize is >= 10 and <= 72)
            {
                lineH = Math.Round(node.FontSize.Value * 1.24);
            }

            if (!node.WrapText || node.MaxLines == 1)
            {
                return lineH + padT + padB + marginV;
            }

            string sample = node.Text ?? string.Empty;
            if (string.IsNullOrEmpty(sample))
            {
                return lineH + padT + padB + marginV;
            }

            double avgCharW = ContainsCjkCharacters(sample) ? (lineH * 0.68) : 7.5;
            double innerW = Math.Max(32, availableWidth - padL - padR);
            int estimatedLines = Math.Clamp((int)Math.Ceiling((sample.Length * avgCharW) / innerW), 1, node.MaxLines > 0 ? node.MaxLines : 4);
            // Dynamic binding strings like "${state.foo}" overestimate length; cap unless literal multi-line text.
            if (sample.Contains("${", StringComparison.Ordinal))
            {
                estimatedLines = Math.Min(estimatedLines, 1);
            }

            return (estimatedLines * lineH) + padT + padB + marginV;
        }

        if (string.Equals(type, "Button", StringComparison.OrdinalIgnoreCase))
        {
            double buttonH = node.Padding is not null
                ? Math.Clamp(18 + padT + padB, 22, 40)
                : 34;
            return buttonH + marginV;
        }

        if (string.Equals(type, "Slider", StringComparison.OrdinalIgnoreCase))
        {
            return 26 + marginV;
        }

        if (string.Equals(type, "Toggle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "AutoSuggestInput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "NumberBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TextInput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "NumberInput", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Select", StringComparison.OrdinalIgnoreCase))
        {
            return 32 + marginV;
        }

        if (string.Equals(type, "IconButton", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "MenuButton", StringComparison.OrdinalIgnoreCase))
        {
            double iconSize = node.Size is > 0 and <= 64 ? node.Size.Value : 32;
            return iconSize + marginV;
        }

        if (string.Equals(type, "Badge", StringComparison.OrdinalIgnoreCase))
        {
            return 20 + marginV;
        }

        if (string.Equals(type, "Progress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "ProgressBar", StringComparison.OrdinalIgnoreCase))
        {
            return 14 + marginV;
        }

        if (string.Equals(type, "Divider", StringComparison.OrdinalIgnoreCase))
        {
            return 9 + marginV;
        }

        if (string.Equals(type, "Spacer", StringComparison.OrdinalIgnoreCase))
        {
            return (node.Size is > 0 ? node.Size.Value : 10) + marginV;
        }

        if (string.Equals(type, "RingGauge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "RadialGauge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Gauge", StringComparison.OrdinalIgnoreCase))
        {
            return (node.Size is > 0 ? node.Size.Value : 88) + marginV;
        }

        if (string.Equals(type, "BarList", StringComparison.OrdinalIgnoreCase))
        {
            if (node.MaxHeight is > 0 and < 2000)
            {
                return node.MaxHeight.Value + padT + padB + marginV;
            }

            int rows = Math.Clamp(node.MaxItems > 0 ? node.MaxItems : 4, 1, 8);
            double barRowsH = (rows * 36.0) + (Math.Max(0, rows - 1) * 6.0);
            return barRowsH + padT + padB + marginV;
        }

        if (string.Equals(type, "RecycleBin", StringComparison.OrdinalIgnoreCase))
        {
            double binH = Math.Max(36, node.MinHeight ?? 36);
            return binH + marginV;
        }

        if (string.Equals(type, "Sparkline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Heatmap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Canvas", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Image", StringComparison.OrdinalIgnoreCase))
        {
            double visualH = node.MaxHeight ?? node.MinHeight ?? node.Size ?? 64;
            return visualH + padT + padB + marginV;
        }

        if (string.Equals(type, "Tabs", StringComparison.OrdinalIgnoreCase))
        {
            if (node.Children.Count == 0)
            {
                return marginV;
            }

            bool hasNonFlexibleTab = node.Children.Any(t => !HasRootFlexibleListOrScroll(t, 0, manifest));
            IEnumerable<CustomWidgetNodeSpec> tabsToMeasure = hasNonFlexibleTab
                ? node.Children.Where(t => !HasRootFlexibleListOrScroll(t, 0, manifest))
                : node.Children;

            double maxPane = 0;
            string? maxPanePath = node.NodePath;
            foreach (CustomWidgetNodeSpec tab in tabsToMeasure)
            {
                double tabH = EstimateNodeHeightRecursive(tab, availableWidth - padL - padR, 0, out string? candidatePath, manifest);
                if (tabH > maxPane)
                {
                    maxPane = tabH;
                    maxPanePath = candidatePath ?? tab.NodePath;
                }
            }

            tallestPath = maxPanePath;
            return 36 + maxPane + padT + padB + marginV;
        }

        if (string.Equals(type, "Tab", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TabItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Scroll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Section", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(type, "Scroll", StringComparison.OrdinalIgnoreCase) &&
                node.MaxHeight is > 0 and < 2000)
            {
                return node.MaxHeight.Value + marginV;
            }

            return EstimateVerticalChildrenHeight(node.Children, node.Spacing, availableWidth - padL - padR, 0, out tallestPath, manifest)
                + padT + padB + marginV;
        }

        if (string.Equals(type, "ModuleStack", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<CustomWidgetNodeSpec> enabledModules = ResolveDefaultEnabledModules(node, manifest);
            double modInnerW = Math.Max(40, availableWidth - padL - padR);
            double modGap = Math.Max(0, node.Spacing);
            double modTotal = 0;
            double modMaxGroup = 0;
            int modGroups = 0;
            int idx = 0;
            while (idx < enabledModules.Count)
            {
                if (enabledModules[idx].CompactPair &&
                    idx + 1 < enabledModules.Count &&
                    enabledModules[idx + 1].CompactPair &&
                    string.Equals(
                        enabledModules[idx].PairGroup?.Trim() ?? string.Empty,
                        enabledModules[idx + 1].PairGroup?.Trim() ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase))
                {
                    double pairColW = Math.Max(40, (modInnerW - modGap) * 0.5);
                    double leftH = EstimateNodeHeightRecursive(enabledModules[idx], pairColW, 2, out string? leftPath, manifest);
                    double rightH = EstimateNodeHeightRecursive(enabledModules[idx + 1], pairColW, 2, out string? rightPath, manifest);
                    double pairH = Math.Max(leftH, rightH);
                    modTotal += pairH;
                    modGroups++;
                    if (pairH > modMaxGroup)
                    {
                        modMaxGroup = pairH;
                        tallestPath = leftH >= rightH ? (leftPath ?? enabledModules[idx].NodePath) : (rightPath ?? enabledModules[idx + 1].NodePath);
                    }
                    idx += 2;
                    continue;
                }

                double singleH = EstimateNodeHeightRecursive(enabledModules[idx], modInnerW, 0, out string? singlePath, manifest);
                modTotal += singleH;
                modGroups++;
                if (singleH > modMaxGroup)
                {
                    modMaxGroup = singleH;
                    tallestPath = singlePath ?? enabledModules[idx].NodePath;
                }
                idx++;
            }

            if (modGroups > 1)
            {
                modTotal += (modGroups - 1) * modGap;
            }

            return modTotal + padT + padB + marginV;
        }

        if (string.Equals(type, "List", StringComparison.OrdinalIgnoreCase))
        {
            if (node.MaxHeight is > 0 and < 2000)
            {
                return node.MaxHeight.Value + marginV;
            }

            double itemH = node.ItemTemplate is not null
                ? EstimateNodeHeightRecursive(node.ItemTemplate, Math.Max(80, availableWidth - padL - padR), 0, out _, manifest)
                : 40;
            return Math.Min(3, Math.Max(1, node.MaxItems > 0 ? node.MaxItems : 3)) * Math.Max(34, itemH + 4) + marginV;
        }

        if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase))
        {
            return EstimateGridNodeHeight(node, availableWidth, out tallestPath, manifest) + marginV;
        }

        if (string.Equals(type, "Expander", StringComparison.OrdinalIgnoreCase))
        {
            if (!node.DefaultExpanded)
            {
                return 38 + marginV;
            }

            double bodyH = EstimateVerticalChildrenHeight(node.Children, node.Spacing, Math.Max(80, availableWidth - 20), 0, out tallestPath, manifest);
            return 38 + bodyH + marginV;
        }

        if (string.Equals(type, "Card", StringComparison.OrdinalIgnoreCase))
        {
            double defaultPadH = parentGridColCount >= 4 ? 12 : 24;
            double defaultPadV = parentGridColCount >= 4 ? 8 : 20;
            double effPadL = node.Padding is null ? defaultPadH * 0.5 : padL;
            double effPadR = node.Padding is null ? defaultPadH * 0.5 : padR;
            double effPadT = node.Padding is null ? defaultPadV * 0.5 : padT;
            double effPadB = node.Padding is null ? defaultPadV * 0.5 : padB;
            double innerW = Math.Max(40, availableWidth - effPadL - effPadR - 2);
            bool isHoriz = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
            double childrenH = isHoriz
                ? EstimateHorizontalChildrenHeight(node.Children, node.Spacing, innerW, out tallestPath, manifest)
                : EstimateVerticalChildrenHeight(node.Children, node.Spacing, innerW, 0, out tallestPath, manifest);
            return Math.Max(node.MinHeight ?? 0, childrenH + effPadT + effPadB + 2) + marginV;
        }

        if (string.Equals(type, "ZStack", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Overlay", StringComparison.OrdinalIgnoreCase))
        {
            if (node.Height is > 0 and < 2000)
            {
                return node.Height.Value + padT + padB + marginV;
            }

            double maxLayer = 0;
            foreach (CustomWidgetNodeSpec layer in node.Children)
            {
                if (IsTransientAlertWhenExpression(layer.When))
                {
                    continue;
                }

                double lh = EstimateNodeHeightRecursive(layer, Math.Max(40, availableWidth - padL - padR), parentGridColCount, out string? lPath, manifest);
                if (lh > maxLayer)
                {
                    maxLayer = lh;
                    tallestPath = lPath ?? layer.NodePath;
                }
            }

            return Math.Max(node.MinHeight ?? 0, maxLayer + padT + padB) + marginV;
        }

        // Stack / SplitView / unknown container
        double containerInnerW = Math.Max(40, availableWidth - padL - padR);
        bool horizontal = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
        double body = horizontal
            ? EstimateHorizontalChildrenHeight(node.Children, node.Spacing, containerInnerW, out tallestPath, manifest)
            : EstimateVerticalChildrenHeight(node.Children, node.Spacing, containerInnerW, 0, out tallestPath, manifest);
        if (string.Equals(type, "SplitView", StringComparison.OrdinalIgnoreCase) && node.Pane is not null)
        {
            double paneH = EstimateNodeHeightRecursive(node.Pane, Math.Max(80, node.PaneWidth), 0, out _, manifest);
            body = Math.Max(body, paneH);
        }

        return body + padT + padB + marginV;
    }

    private static double EstimateHorizontalChildrenHeight(
        IReadOnlyList<CustomWidgetNodeSpec> children,
        double spacing,
        double availableWidth,
        out string? tallestPath,
        CustomWidgetManifest? manifest = null)
    {
        tallestPath = null;
        if (children.Count == 0)
        {
            return 0;
        }

        double totalSpacing = Math.Max(0, children.Count - 1) * Math.Max(0, spacing);
        double perChildWidth = Math.Max(32, (availableWidth - totalSpacing) / children.Count);
        double maxH = 0;
        foreach (CustomWidgetNodeSpec child in children)
        {
            double h = EstimateNodeHeightRecursive(child, child.Width ?? perChildWidth, 0, out string? childPath, manifest);
            if (h > maxH)
            {
                maxH = h;
                tallestPath = childPath ?? child.NodePath;
            }
        }

        return maxH;
    }

    private static double EstimateVerticalChildrenHeight(
        IReadOnlyList<CustomWidgetNodeSpec> children,
        double spacing,
        double availableWidth,
        int parentGridColCount,
        out string? tallestPath,
        CustomWidgetManifest? manifest = null)
    {
        tallestPath = null;
        if (children.Count == 0)
        {
            return 0;
        }

        double total = 0;
        double maxGroupH = 0;
        int renderedGroups = 0;
        double effectiveSpacing = Math.Max(0, spacing);

        int i = 0;
        while (i < children.Count)
        {
            if (IsTransientAlertWhenExpression(children[i].When))
            {
                i++;
                continue;
            }

            // Group consecutive mutually exclusive $when siblings so only the tallest branch counts.
            if (TryGetExclusiveWhenStem(children[i].When, out string? stem))
            {
                double branchMax = 0;
                string? branchMaxPath = children[i].NodePath;
                int j = i;
                while (j < children.Count &&
                       TryGetExclusiveWhenStem(children[j].When, out string? nextStem) &&
                       AreMutuallyExclusiveWhenStemsCompatible(stem, nextStem))
                {
                    double ch = EstimateNodeHeightRecursive(children[j], availableWidth, parentGridColCount, out string? cPath, manifest);
                    if (ch > branchMax)
                    {
                        branchMax = ch;
                        branchMaxPath = cPath ?? children[j].NodePath;
                    }
                    j++;
                }

                total += branchMax;
                renderedGroups++;
                if (branchMax > maxGroupH)
                {
                    maxGroupH = branchMax;
                    tallestPath = branchMaxPath;
                }
                i = j;
                continue;
            }

            double childH = EstimateNodeHeightRecursive(children[i], availableWidth, parentGridColCount, out string? path, manifest);
            total += childH;
            renderedGroups++;
            if (childH > maxGroupH)
            {
                maxGroupH = childH;
                tallestPath = path ?? children[i].NodePath;
            }
            i++;
        }

        if (renderedGroups > 1)
        {
            total += (renderedGroups - 1) * effectiveSpacing;
        }

        return total;
    }

    private static bool IsTransientAlertWhenExpression(string? whenExpr)
    {
        if (string.IsNullOrWhiteSpace(whenExpr))
        {
            return false;
        }

        return whenExpr.Contains("sedentaryIsDue == 'true'", StringComparison.Ordinal) ||
               whenExpr.Contains("sedentaryIsDue == \"true\"", StringComparison.Ordinal) ||
               whenExpr.Contains("sourceStatus == 'error'", StringComparison.Ordinal) ||
               whenExpr.Contains("aiStatus == 'error'", StringComparison.Ordinal) ||
               whenExpr.Contains("hasError == 'true'", StringComparison.Ordinal);
    }

    private static bool AreMutuallyExclusiveWhenStemsCompatible(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return false;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        // Partitioned compound guards such as "!$pomodoro.isDue && ..." followed by "$pomodoro.isDue"
        // share the primary guard variable (first segment in pipe-joined variable list).
        int firstPipe = first.IndexOf('|');
        int secondPipe = second.IndexOf('|');
        ReadOnlySpan<char> firstPrimary = firstPipe >= 0 ? first.AsSpan(0, firstPipe) : first.AsSpan();
        ReadOnlySpan<char> secondPrimary = secondPipe >= 0 ? second.AsSpan(0, secondPipe) : second.AsSpan();
        return firstPrimary.SequenceEqual(secondPrimary);
    }

    private static bool TryGetExclusiveWhenStem(string? whenExpr, out string? stem)
    {
        stem = null;
        if (string.IsNullOrWhiteSpace(whenExpr))
        {
            return false;
        }

        string expr = whenExpr.Trim();
        if (expr.StartsWith("${", StringComparison.Ordinal) && expr.EndsWith('}') && expr.Length > 3)
        {
            expr = expr[2..^1].Trim();
        }

        if (expr.Length == 0)
        {
            return false;
        }

        if (expr.Contains("&&", StringComparison.Ordinal) ||
            expr.Contains("||", StringComparison.Ordinal))
        {
            // Extract ordered guard variables so De Morgan complements ("!a && !b" vs "a || b")
            // and partitioned guards ("!due && !run" vs "!due && run" vs "due") group cleanly.
            var tokens = new List<string>(4);
            int pos = 0;
            while (pos < expr.Length)
            {
                char ch = expr[pos];
                if (ch == '$' || char.IsLetter(ch) || ch == '_')
                {
                    int start = pos;
                    pos++;
                    while (pos < expr.Length && (char.IsLetterOrDigit(expr[pos]) || expr[pos] is '.' or '_' or '$'))
                    {
                        pos++;
                    }

                    string ident = expr[start..pos];
                    if (ident is not ("true" or "false" or "null") &&
                        !tokens.Contains(ident, StringComparer.Ordinal))
                    {
                        tokens.Add(ident);
                    }
                }
                else if (ch is '\'' or '"')
                {
                    char quote = ch;
                    pos++;
                    while (pos < expr.Length && expr[pos] != quote)
                    {
                        if (expr[pos] == '\\' && pos + 1 < expr.Length)
                        {
                            pos++;
                        }
                        pos++;
                    }
                    if (pos < expr.Length)
                    {
                        pos++;
                    }
                }
                else
                {
                    pos++;
                }
            }

            if (tokens.Count == 0)
            {
                return false;
            }

            string primary = tokens[0];
            tokens.Sort(StringComparer.Ordinal);
            stem = tokens.Count == 1
                ? primary
                : $"{primary}|{string.Join(',', tokens)}";
            return true;
        }

        ReadOnlySpan<string> ops = ["===", "!==", "==", "!=", ">=", "<=", ">", "<"];
        foreach (string op in ops)
        {
            int idx = expr.IndexOf(op, StringComparison.Ordinal);
            if (idx > 0)
            {
                stem = expr[..idx].Trim();
                return stem.Length > 0;
            }
        }

        if (expr.StartsWith('!'))
        {
            stem = expr[1..].Trim();
            return stem.Length > 0;
        }

        stem = expr;
        return stem.Length > 0;
    }

    private static double EstimateGridNodeHeight(
        CustomWidgetNodeSpec grid,
        double availableWidth,
        out string? tallestPath,
        CustomWidgetManifest? manifest = null)
    {
        tallestPath = grid.NodePath;
        TryParseEdgeThickness(grid.Padding, out double padL, out double padT, out double padR, out double padB);
        double innerWidth = Math.Max(40, availableWidth - padL - padR);
        double gap = Math.Max(0, grid.Spacing);

        List<double> colWidths = ComputeGridColumnWidths(grid.Columns, innerWidth, gap);
        int colCount = Math.Max(1, colWidths.Count);

        if (string.Equals(grid.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase))
        {
            int sampleRows = colCount >= 3 ? 1 : (colCount == 2 ? 2 : 3);
            double cellW = colWidths[0];
            double itemH = grid.ItemTemplate is not null
                ? EstimateNodeHeightRecursive(grid.ItemTemplate, cellW, colCount, out _, manifest)
                : 54;
            return (sampleRows * itemH) + (Math.Max(0, sampleRows - 1) * gap) + padT + padB;
        }

        if (grid.Children.Count == 0)
        {
            return padT + padB;
        }

        // 2D occupancy grid matching CustomWidgetNativeRenderer placement rules.
        var occupied = new HashSet<(int r, int c)>();
        var rowHeights = new Dictionary<int, double>();
        int autoCursorRow = 0;
        int autoCursorCol = 0;
        double maxContribution = 0;

        foreach (CustomWidgetNodeSpec child in grid.Children)
        {
            int colSpan = Math.Clamp(child.ColumnSpan, 1, colCount);
            int rowSpan = Math.Max(1, child.RowSpan);
            int r;
            int c;

            if (child.HasExplicitColumn || child.HasExplicitRow)
            {
                c = Math.Clamp(child.Column, 0, Math.Max(0, colCount - colSpan));
                r = Math.Max(0, child.Row);
            }
            else
            {
                while (true)
                {
                    if (autoCursorCol + colSpan > colCount)
                    {
                        autoCursorCol = 0;
                        autoCursorRow++;
                    }

                    bool fits = true;
                    for (int rr = autoCursorRow; rr < autoCursorRow + rowSpan && fits; rr++)
                    {
                        for (int cc = autoCursorCol; cc < autoCursorCol + colSpan; cc++)
                        {
                            if (occupied.Contains((rr, cc)))
                            {
                                fits = false;
                                break;
                            }
                        }
                    }

                    if (fits)
                    {
                        r = autoCursorRow;
                        c = autoCursorCol;
                        autoCursorCol += colSpan;
                        break;
                    }

                    autoCursorCol++;
                }
            }

            for (int rr = r; rr < r + rowSpan; rr++)
            {
                for (int cc = c; cc < Math.Min(colCount, c + colSpan); cc++)
                {
                    occupied.Add((rr, cc));
                }
            }

            double spanWidth = 0;
            for (int cc = c; cc < Math.Min(colCount, c + colSpan); cc++)
            {
                spanWidth += colWidths[cc];
            }
            spanWidth += Math.Max(0, colSpan - 1) * gap;

            double childH = EstimateNodeHeightRecursive(child, spanWidth, colCount, out string? childPath, manifest);
            double perRowH = childH / rowSpan;
            for (int rr = r; rr < r + rowSpan; rr++)
            {
                if (!rowHeights.TryGetValue(rr, out double existingH) || perRowH > existingH)
                {
                    rowHeights[rr] = perRowH;
                }
            }

            if (childH > maxContribution)
            {
                maxContribution = childH;
                tallestPath = childPath ?? child.NodePath;
            }
        }

        if (rowHeights.Count == 0)
        {
            return padT + padB;
        }

        double totalRowsH = rowHeights.Values.Sum();
        double rowGap = Math.Max(0, rowHeights.Count - 1) * gap;
        return totalRowsH + rowGap + padT + padB;
    }

    private static List<double> ComputeGridColumnWidths(string? rawColumns, double innerWidth, double gap)
    {
        if (string.IsNullOrWhiteSpace(rawColumns) ||
            !CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs(rawColumns, out string normalized))
        {
            return [innerWidth];
        }

        string[] tokens = normalized.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [innerWidth];
        }

        double totalGap = Math.Max(0, tokens.Length - 1) * gap;
        double distributable = Math.Max(20, innerWidth - totalGap);
        double fixedSum = 0;
        double starSum = 0;

        foreach (string tok in tokens)
        {
            if (tok == "*")
            {
                starSum += 1;
            }
            else if (tok.EndsWith('*') && double.TryParse(tok[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
            {
                starSum += w;
            }
            else if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
            {
                fixedSum += px;
            }
            else
            {
                starSum += 1;
            }
        }

        double rem = Math.Max(0, distributable - fixedSum);
        var widths = new List<double>(tokens.Length);
        foreach (string tok in tokens)
        {
            if (tok == "*")
            {
                widths.Add(starSum > 0 ? rem / starSum : distributable / tokens.Length);
            }
            else if (tok.EndsWith('*') && double.TryParse(tok[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
            {
                widths.Add(starSum > 0 ? (rem * w) / starSum : distributable / tokens.Length);
            }
            else if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double px))
            {
                widths.Add(px);
            }
            else
            {
                widths.Add(starSum > 0 ? rem / starSum : distributable / tokens.Length);
            }
        }

        return widths;
    }

    private static void AnalyzeCjkColumnSqueeze(
        CustomWidgetManifest manifest,
        string? cultureName,
        List<CustomWidgetHealthIssue> issues)
    {
        var squeezed = new List<(string NodePath, double NetWidth)>();
        double rootWidth = Math.Max(120, manifest.DefaultSize.Width - 28);
        WalkCjkColumnSqueeze(manifest.View, rootWidth, parentGridColCount: 0, manifest.I18n, squeezed);

        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            double popWidth = pop.Width is > 100 and <= 600 ? pop.Width.Value : 320;
            WalkCjkColumnSqueeze(pop, popWidth, parentGridColCount: 0, manifest.I18n, squeezed);
        }

        if (squeezed.Count == 0)
        {
            return;
        }

        (string samplePath, double minWidth) = squeezed.OrderBy(s => s.NetWidth).First();
        bool isZh = !string.IsNullOrWhiteSpace(cultureName) &&
            cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        issues.Add(new CustomWidgetHealthIssue(
            Code: "layout.cjk_column_squeeze",
            Message: isZh
                ? $"节点 {samplePath} 的可用文字净宽仅 {Math.Round(minWidth)}px（< 28px），中文将被严重折行或截断。"
                : $"Node {samplePath} has only {Math.Round(minWidth)}px net text width (< 28px), severely wrapping or truncating CJK labels.",
            SuggestedPrompt: isZh
                ? $"请减少 {samplePath} 所在多列网格的列数（如 6 列→3 列）、增大 defaultSize.width 或压缩卡片/按钮横向内边距，使每个文本槽位净宽 ≥ 28px。"
                : $"Reduce column count around {samplePath}, widen defaultSize.width, or tighten horizontal padding so every CJK label has at least 28px net text width."));
    }

    private static void WalkCjkColumnSqueeze(
        CustomWidgetNodeSpec? node,
        double availableWidth,
        int parentGridColCount,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> i18n,
        List<(string NodePath, double NetWidth)> squeezed)
    {
        if (node is null)
        {
            return;
        }

        double nodeWidth = node.Width is > 0 and < 4000 ? node.Width.Value : availableWidth;
        TryParseEdgeThickness(node.Padding, out double padL, out _, out double padR, out _);
        string type = node.Type?.Trim() ?? "Stack";

        if (string.Equals(type, "Card", StringComparison.OrdinalIgnoreCase))
        {
            double cardInset = node.Padding is null
                ? (parentGridColCount >= 4 ? 14 : 26)
                : (padL + padR + 2);
            double innerW = Math.Max(0, nodeWidth - cardInset);
            bool isHoriz = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
            if (isHoriz && node.Children.Count > 1)
            {
                double gap = Math.Max(0, node.Spacing) * (node.Children.Count - 1);
                double childW = Math.Max(0, (innerW - gap) / node.Children.Count);
                foreach (CustomWidgetNodeSpec ch in node.Children)
                {
                    WalkCjkColumnSqueeze(ch, childW, 0, i18n, squeezed);
                }
            }
            else
            {
                foreach (CustomWidgetNodeSpec ch in node.Children)
                {
                    WalkCjkColumnSqueeze(ch, innerW, 0, i18n, squeezed);
                }
            }
            return;
        }

        if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase))
        {
            double innerW = Math.Max(0, nodeWidth - padL - padR);
            double gap = Math.Max(0, node.Spacing);
            List<double> colWidths = ComputeGridColumnWidths(node.Columns, innerW, gap);
            int colCount = Math.Max(1, colWidths.Count);

            if (gridHasTemplate(node, out CustomWidgetNodeSpec? tmpl))
            {
                WalkCjkColumnSqueeze(tmpl, colWidths[0], colCount, i18n, squeezed);
                return;
            }

            int cursorCol = 0;
            foreach (CustomWidgetNodeSpec ch in node.Children)
            {
                int colSpan = Math.Clamp(ch.ColumnSpan, 1, colCount);
                int c = ch.HasExplicitColumn
                    ? Math.Clamp(ch.Column, 0, Math.Max(0, colCount - colSpan))
                    : (cursorCol + colSpan > colCount ? 0 : cursorCol);
                if (!ch.HasExplicitColumn)
                {
                    cursorCol = (c + colSpan >= colCount) ? 0 : (c + colSpan);
                }

                double spanW = 0;
                for (int cc = c; cc < Math.Min(colCount, c + colSpan); cc++)
                {
                    spanW += colWidths[cc];
                }
                spanW += Math.Max(0, colSpan - 1) * gap;
                WalkCjkColumnSqueeze(ch, spanW, colCount, i18n, squeezed);
            }
            return;
        }

        if (string.Equals(type, "Stack", StringComparison.OrdinalIgnoreCase))
        {
            double innerW = Math.Max(0, nodeWidth - padL - padR);
            bool horizontal = string.Equals(node.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase);
            if (horizontal && node.Children.Count > 1)
            {
                double gap = Math.Max(0, node.Spacing) * (node.Children.Count - 1);
                double perChild = Math.Max(0, (innerW - gap) / node.Children.Count);
                foreach (CustomWidgetNodeSpec ch in node.Children)
                {
                    WalkCjkColumnSqueeze(ch, ch.Width ?? perChild, 0, i18n, squeezed);
                }
            }
            else
            {
                foreach (CustomWidgetNodeSpec ch in node.Children)
                {
                    WalkCjkColumnSqueeze(ch, ch.Width ?? innerW, 0, i18n, squeezed);
                }
            }
            return;
        }

        if (string.Equals(type, "Text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Button", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Badge", StringComparison.OrdinalIgnoreCase))
        {
            string? textCandidate = node.Text ?? node.Title ?? node.Value;
            if (HasCjkLabelContent(textCandidate, i18n))
            {
                double ownInset = string.Equals(type, "Text", StringComparison.OrdinalIgnoreCase)
                    ? (padL + padR)
                    : Math.Max(12, padL + padR);
                double netW = nodeWidth - ownInset;
                if (netW < 28)
                {
                    squeezed.Add((node.NodePath, netW));
                }
            }
        }

        if (node.Children.Count > 0)
        {
            double childW = Math.Max(0, nodeWidth - padL - padR);
            foreach (CustomWidgetNodeSpec ch in node.Children)
            {
                WalkCjkColumnSqueeze(ch, childW, 0, i18n, squeezed);
            }
        }

        if (node.ItemTemplate is not null)
        {
            WalkCjkColumnSqueeze(node.ItemTemplate, Math.Max(0, nodeWidth - padL - padR), 0, i18n, squeezed);
        }

        static bool gridHasTemplate(CustomWidgetNodeSpec g, out CustomWidgetNodeSpec? template)
        {
            if (string.Equals(g.Type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) && g.ItemTemplate is not null)
            {
                template = g.ItemTemplate;
                return true;
            }
            template = null;
            return false;
        }
    }

    private static bool HasCjkLabelContent(
        string? raw,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> i18n)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (ContainsCjkCharacters(raw))
        {
            return true;
        }

        const string i18nPrefix = "${i18n.";
        int idx = raw.IndexOf(i18nPrefix, StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            int end = raw.IndexOf('}', idx + i18nPrefix.Length);
            if (end > idx)
            {
                string key = raw[(idx + i18nPrefix.Length)..end].Trim();
                foreach ((string lang, IReadOnlyDictionary<string, string> dict) in i18n)
                {
                    if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                        dict.TryGetValue(key, out string? translated) &&
                        ContainsCjkCharacters(translated))
                    {
                        return true;
                    }
                }
            }
            idx = end > 0 ? raw.IndexOf(i18nPrefix, end, StringComparison.OrdinalIgnoreCase) : -1;
        }

        return false;
    }

    private static bool ContainsCjkCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if ((c >= '\u4E00' && c <= '\u9FFF') ||
                (c >= '\u3400' && c <= '\u4DBF') ||
                (c >= '\u3040' && c <= '\u30FF') ||
                (c >= '\uAC00' && c <= '\uD7AF'))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Frozen.FrozenSet<string> EmptyCheckContainerTypes =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "Stack", "Grid", "ZStack", "Wrap", "Scroll", "Tabs", "FlipView", "SplitView", "ItemsGrid", "List"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static void AnalyzeEmptyContainers(
        CustomWidgetManifest manifest,
        string? cultureName,
        List<CustomWidgetHealthIssue> issues)
    {
        var findings = new List<(string NodePath, string ReasonZh, string ReasonEn)>();
        bool isZh = !string.IsNullOrWhiteSpace(cultureName) &&
            cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        static bool HasDynamicDataBinding(CustomWidgetNodeSpec node) =>
            !string.IsNullOrWhiteSpace(node.Items) ||
            !string.IsNullOrWhiteSpace(node.Collection) ||
            node.ItemTemplate is not null;

        void Walk(CustomWidgetNodeSpec? node, string fallbackPath, bool isRootView, bool isTabOrFlipPage)
        {
            if (node is null || findings.Count >= 2)
            {
                return;
            }

            string nodePath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : fallbackPath;
            string type = node.Type?.Trim() ?? "Stack";
            bool hasDynamicBinding = HasDynamicDataBinding(node);
            bool hasChildren = node.Children.Count > 0;

            if (isRootView &&
                EmptyCheckContainerTypes.Contains(type) &&
                !hasChildren &&
                !hasDynamicBinding)
            {
                findings.Add((
                    nodePath,
                    $"根节点 {nodePath} ({type}) 当前没有任何子节点或 items 绑定，渲染后为空白。",
                    $"Root container {nodePath} ({type}) has 0 children and no items binding, rendering blank."));
            }
            else if (isTabOrFlipPage &&
                     EmptyCheckContainerTypes.Contains(type) &&
                     !hasChildren &&
                     !hasDynamicBinding)
            {
                findings.Add((
                    nodePath,
                    $"分页容器 {nodePath} ({type}) 内没有任何子节点或数据模板，切到该标签页将看到空白。",
                    $"Tab/FlipView page {nodePath} ({type}) has 0 children and no itemTemplate/items binding, rendering blank when selected."));
            }
            else if (string.Equals(type, "Scroll", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasChildren)
                {
                    findings.Add((
                        nodePath,
                        $"滚动容器 {nodePath} (Scroll) 没有子节点，内容区域为空。",
                        $"Scroll container {nodePath} has 0 children."));
                }
                else if (node.Children.Count == 1 &&
                         node.Children[0] is { } onlyChild &&
                         (string.Equals(onlyChild.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(onlyChild.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(onlyChild.Type, "Wrap", StringComparison.OrdinalIgnoreCase)) &&
                         onlyChild.Children.Count == 0 &&
                         !HasDynamicDataBinding(onlyChild) &&
                         onlyChild.Height is null)
                {
                    string childPath = !string.IsNullOrWhiteSpace(onlyChild.NodePath) ? onlyChild.NodePath : $"{nodePath}.children[0]";
                    findings.Add((
                        childPath,
                        $"Scroll 内的容器 {childPath} ({onlyChild.Type}) 没有任何子节点，区域将完全空白。",
                        $"Container {childPath} ({onlyChild.Type}) inside Scroll has 0 children and will render empty."));
                }
            }
            else if (string.Equals(type, "Grid", StringComparison.OrdinalIgnoreCase) &&
                     (!string.IsNullOrWhiteSpace(node.Columns) || !string.IsNullOrWhiteSpace(node.Rows)) &&
                     !hasChildren &&
                     node.ItemTemplate is null)
            {
                findings.Add((
                    nodePath,
                    $"网格容器 {nodePath} (Grid) 声明了行列轨道但 children 为空，无法渲染任何单元格（动态列表/矩阵请改用 ItemsGrid + items + itemTemplate）。",
                    $"Grid {nodePath} declares columns/rows tracks but has 0 children (for dynamic items/matrices, use ItemsGrid + items + itemTemplate)."));
            }
            else if ((string.Equals(type, "ItemsGrid", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(type, "List", StringComparison.OrdinalIgnoreCase)) &&
                     node.ItemTemplate is null &&
                     !hasChildren)
            {
                findings.Add((
                    nodePath,
                    $"{type} {nodePath} 缺少 itemTemplate 且 children 为空。",
                    $"{type} {nodePath} is missing itemTemplate and has 0 children."));
            }
            else if (string.Equals(type, "Card", StringComparison.OrdinalIgnoreCase) &&
                     !hasChildren &&
                     !hasDynamicBinding &&
                     string.IsNullOrWhiteSpace(node.Title) &&
                     string.IsNullOrWhiteSpace(node.Subtitle) &&
                     string.IsNullOrWhiteSpace(node.Text) &&
                     string.IsNullOrWhiteSpace(node.Value) &&
                     string.IsNullOrWhiteSpace(node.Bind))
            {
                findings.Add((
                    nodePath,
                    $"卡片容器 {nodePath} (Card) 没有任何子节点或文本内容，渲染后为空白卡片（注：弹窗必须定义在顶层 popovers 并通过触发节点上的 \"popover\": \"<id>\" 绑定，不要把子节点写在无效内联包装里）。",
                    $"Card container {nodePath} has 0 children and no text/title content, rendering as an empty card shell."));
            }

            bool childIsTabOrFlipPage = string.Equals(type, "Tabs", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(type, "FlipView", StringComparison.OrdinalIgnoreCase);

            Walk(node.ItemTemplate, $"{nodePath}.itemTemplate", isRootView: false, isTabOrFlipPage: false);
            Walk(node.ActionButton, $"{nodePath}.actionButton", isRootView: false, isTabOrFlipPage: false);
            Walk(node.Pane, $"{nodePath}.pane", isRootView: false, isTabOrFlipPage: false);

            for (int i = 0; i < node.Children.Count; i++)
            {
                Walk(node.Children[i], $"{nodePath}.children[{i}]", isRootView: false, isTabOrFlipPage: childIsTabOrFlipPage);
            }
        }

        Walk(manifest.View, "$.view", isRootView: true, isTabOrFlipPage: false);

        for (int i = 0; i < manifest.Popovers.Count && findings.Count < 2; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popPath = !string.IsNullOrWhiteSpace(pop.NodePath) ? pop.NodePath : $"$.popovers[{i}]";
            if (pop.Children.Count == 0 && !HasDynamicDataBinding(pop))
            {
                findings.Add((
                    popPath,
                    $"弹窗 {popPath} (id={pop.PopoverId ?? pop.Id}) 没有子节点，打开后为空白弹层。",
                    $"Root popover {popPath} (id={pop.PopoverId ?? pop.Id}) has 0 children."));
            }
            else
            {
                Walk(pop, popPath, isRootView: false, isTabOrFlipPage: false);
            }
        }

        foreach ((string nodePath, string reasonZh, string reasonEn) in findings)
        {
            issues.Add(new CustomWidgetHealthIssue(
                Code: "layout.empty_container",
                Message: isZh ? reasonZh : reasonEn,
                SuggestedPrompt: isZh
                    ? $"请为 {nodePath} 补全真实可渲染的子组件；若是动态数量的数据/课表/矩阵格子，请改用 ItemsGrid（`columns` + `items: \"${{$computed.cells}}\"` + `itemTemplate`）。"
                    : $"Populate {nodePath} with real child nodes, or use ItemsGrid (`columns` + `items: \"${{$computed.cells}}\"` + `itemTemplate`) for dynamic data/matrix cells."));
        }

        AnalyzeUnconditionalLoadingText(manifest.View, "$.view", hasConditionalAncestor: false, isZh, issues);
    }

    private static void AnalyzeUnconditionalLoadingText(
        CustomWidgetNodeSpec? node,
        string fallbackPath,
        bool hasConditionalAncestor,
        bool isZh,
        List<CustomWidgetHealthIssue> issues)
    {
        if (node is null)
        {
            return;
        }

        string nodePath = !string.IsNullOrWhiteSpace(node.NodePath) ? node.NodePath : fallbackPath;
        bool curHasCondition = hasConditionalAncestor || !string.IsNullOrWhiteSpace(node.When);
        if (!curHasCondition &&
            string.Equals(node.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(node.Text) &&
            !node.Text.Contains("${", StringComparison.Ordinal))
        {
            string trimmed = node.Text.Trim();
            if (trimmed is "加载中..." or "加载中…" or "正在加载..." or "正在加载…" or "Loading..." or "Loading…")
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "view.unconditional_loading_text",
                    Message: isZh
                        ? $"文本节点 {nodePath} 硬编码了静态「{trimmed}」且没有任何 `when` 条件或 `${{...}}` 数据绑定，加载完成后仍会一直显示。"
                        : $"Text node {nodePath} renders static '{trimmed}' without a `when` guard or `${{...}}` binding, remaining permanently visible.",
                    SuggestedPrompt: isZh
                        ? $"请将 {nodePath} 的静态「{trimmed}」改为真实动态表达式（如 `${{computed.summary}}`），或添加 `when` 条件（如 `${{state.loading == true}}`）。"
                        : $"Bind {nodePath} to a dynamic `${{...}}` expression or guard it with a `when` condition."));
                return;
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            AnalyzeUnconditionalLoadingText(node.Children[i], $"{nodePath}.children[{i}]", curHasCondition, isZh, issues);
        }
    }

    private static void TryParseEdgeThickness(
        string? raw,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double u))
        {
            left = top = right = bottom = u;
        }
        else if (parts.Length == 2 &&
                 double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                 double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            left = right = h;
            top = bottom = v;
        }
        else if (parts.Length == 4 &&
                 double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
                 double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                 double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
                 double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            left = l;
            top = t;
            right = r;
            bottom = b;
        }
    }
}
