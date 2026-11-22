using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    internal static (
        IReadOnlyList<GridLength> Columns,
        IReadOnlyList<GridLength> Rows,
        IReadOnlyList<(int Row, int Column, int RowSpan, int ColumnSpan)> Placements,
        bool HasStarRow) ComputeGridTrackAndPlacementPlan(
        CustomWidgetNodeSpec spec,
        string? resolvedCols,
        string? resolvedRows)
    {
        (IReadOnlyList<GridLength> cols, IReadOnlyList<GridLength> rows, IReadOnlyList<(int Row, int Column, int RowSpan, int ColumnSpan)> placements, _, bool hasStarRow) =
            ComputeGridTrackAndPlacementPlanCore(spec, resolvedCols, resolvedRows);
        return (cols, rows, placements, hasStarRow);
    }

    internal static (
        IReadOnlyList<GridLength> Columns,
        IReadOnlyList<GridLength> Rows,
        IReadOnlyList<(int Row, int Column, int RowSpan, int ColumnSpan)> Placements,
        IReadOnlyList<bool> OutOfRange,
        bool HasStarRow) ComputeGridTrackAndPlacementPlanCore(
        CustomWidgetNodeSpec spec,
        string? resolvedCols,
        string? resolvedRows)
    {
        string normalizedCols = CustomWidgetGridTrackNormalizer.NormalizeRuntimeTrackString(resolvedCols);
        string normalizedRows = CustomWidgetGridTrackNormalizer.NormalizeRuntimeTrackString(resolvedRows);

        var cols = new List<GridLength>();
        if (!string.IsNullOrWhiteSpace(normalizedCols) &&
            !normalizedCols.Contains("${", StringComparison.Ordinal))
        {
            foreach (string colToken in normalizedCols.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                cols.Add(ParseGridLength(colToken));
            }
        }

        var rows = new List<GridLength>();
        bool hasStarRow = false;
        if (!string.IsNullOrWhiteSpace(normalizedRows) &&
            !normalizedRows.Contains("${", StringComparison.Ordinal))
        {
            foreach (string rowToken in normalizedRows.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                GridLength rowLen = ParseGridLength(rowToken);
                if (rowLen.IsStar)
                {
                    hasStarRow = true;
                }
                rows.Add(rowLen);
            }
        }

        int colCount = cols.Count;
        int rowCount = rows.Count;
        bool hasFixedRows = !string.IsNullOrWhiteSpace(normalizedRows);

        var placements = new List<(int Row, int Column, int RowSpan, int ColumnSpan)>(spec.Children.Count);
        var outOfRange = new List<bool>(spec.Children.Count);

        if (colCount <= 0)
        {
            int maxRowExtent = rowCount;
            for (int idx = 0; idx < spec.Children.Count; idx++)
            {
                CustomWidgetNodeSpec child = spec.Children[idx];
                bool isChildOutOfRange = hasFixedRows && rowCount > 0 && child.Row >= rowCount;
                int effectiveCol = Math.Max(0, child.Column);
                int effectiveRow = hasFixedRows && rowCount > 0
                    ? Math.Clamp(child.Row, 0, rowCount - 1)
                    : Math.Max(0, child.Row);
                int effectiveColSpan = Math.Max(1, child.ColumnSpan);
                int rawRowSpan = Math.Max(1, child.RowSpan);
                int effectiveRowSpan = hasFixedRows && rowCount > 0
                    ? Math.Clamp(rawRowSpan, 1, Math.Max(1, rowCount - effectiveRow))
                    : rawRowSpan;
                placements.Add((effectiveRow, effectiveCol, effectiveRowSpan, effectiveColSpan));
                outOfRange.Add(isChildOutOfRange);
                maxRowExtent = Math.Max(maxRowExtent, effectiveRow + effectiveRowSpan);
            }

            if (!hasFixedRows && spec.Children.Count > 0)
            {
                while (rows.Count < maxRowExtent)
                {
                    rows.Add(GridLength.Auto);
                }
            }

            return (cols, rows, placements, outOfRange, hasStarRow);
        }

        // Two-pass 2D CSS Grid occupancy placement:
        // Pass 1 reserves explicitly positioned items (preserving intentional $when overlaps);
        // Pass 2 flows implicit items left-to-right, top-to-bottom around occupied cells.
        var placedArray = new (int Row, int Column, int RowSpan, int ColumnSpan)[spec.Children.Count];
        var outOfRangeArray = new bool[spec.Children.Count];
        var occupied = new List<bool[]>();

        void EnsureOccupiedRows(int requiredRows)
        {
            while (occupied.Count < requiredRows)
            {
                occupied.Add(new bool[colCount]);
            }
        }

        void MarkOccupied(int startRow, int startCol, int rowSpan, int colSpan)
        {
            EnsureOccupiedRows(startRow + rowSpan);
            for (int r = startRow; r < startRow + rowSpan; r++)
            {
                for (int c = startCol; c < startCol + colSpan && c < colCount; c++)
                {
                    occupied[r][c] = true;
                }
            }
        }

        bool IsSpanFree(int startRow, int startCol, int rowSpan, int colSpan)
        {
            if (startCol + colSpan > colCount)
            {
                return false;
            }

            for (int r = startRow; r < startRow + rowSpan; r++)
            {
                if (r >= occupied.Count)
                {
                    continue;
                }

                for (int c = startCol; c < startCol + colSpan; c++)
                {
                    if (occupied[r][c])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        static bool IsExplicitlyPlaced(CustomWidgetNodeSpec child) =>
            child.HasExplicitRow || child.HasExplicitColumn || child.Row > 0 || child.Column > 0;

        int maxExtent = rowCount;

        // Pass 1: Explicit row/column children
        for (int idx = 0; idx < spec.Children.Count; idx++)
        {
            CustomWidgetNodeSpec child = spec.Children[idx];
            if (!IsExplicitlyPlaced(child))
            {
                continue;
            }

            bool isChildOutOfRange = child.Column >= colCount ||
                                     (hasFixedRows && rowCount > 0 && child.Row >= rowCount);
            int effectiveCol = Math.Clamp(child.Column, 0, colCount - 1);
            int effectiveRow = hasFixedRows && rowCount > 0
                ? Math.Clamp(child.Row, 0, rowCount - 1)
                : Math.Max(0, child.Row);

            int rawColSpan = Math.Max(1, child.ColumnSpan);
            int effectiveColSpan = Math.Clamp(rawColSpan, 1, Math.Max(1, colCount - effectiveCol));
            int rawRowSpan = Math.Max(1, child.RowSpan);
            int effectiveRowSpan = hasFixedRows && rowCount > 0
                ? Math.Clamp(rawRowSpan, 1, Math.Max(1, rowCount - effectiveRow))
                : rawRowSpan;

            placedArray[idx] = (effectiveRow, effectiveCol, effectiveRowSpan, effectiveColSpan);
            outOfRangeArray[idx] = isChildOutOfRange;
            MarkOccupied(effectiveRow, effectiveCol, effectiveRowSpan, effectiveColSpan);
            maxExtent = Math.Max(maxExtent, effectiveRow + effectiveRowSpan);
        }

        // Pass 2: Implicit children flow via 2D cursor
        int cursorRow = 0;
        int cursorCol = 0;
        for (int idx = 0; idx < spec.Children.Count; idx++)
        {
            CustomWidgetNodeSpec child = spec.Children[idx];
            if (IsExplicitlyPlaced(child))
            {
                continue;
            }

            int desiredColSpan = Math.Clamp(Math.Max(1, child.ColumnSpan), 1, colCount);
            int desiredRowSpan = Math.Max(1, child.RowSpan);

            int placedRow = -1;
            int placedCol = -1;
            int maxSearchRow = hasFixedRows && rowCount > 0 ? rowCount - 1 : int.MaxValue;

            while (cursorRow <= maxSearchRow)
            {
                if (cursorCol + desiredColSpan > colCount)
                {
                    cursorRow++;
                    cursorCol = 0;
                    continue;
                }

                int effectiveSearchRowSpan = hasFixedRows && rowCount > 0
                    ? Math.Clamp(desiredRowSpan, 1, Math.Max(1, rowCount - cursorRow))
                    : desiredRowSpan;

                if (IsSpanFree(cursorRow, cursorCol, effectiveSearchRowSpan, desiredColSpan))
                {
                    placedRow = cursorRow;
                    placedCol = cursorCol;
                    break;
                }

                cursorCol++;
            }

            if (placedRow < 0)
            {
                // Fixed-rows overflow fallback: clamp onto last declared row
                placedRow = hasFixedRows && rowCount > 0 ? rowCount - 1 : 0;
                placedCol = idx % colCount;
                desiredColSpan = Math.Clamp(desiredColSpan, 1, Math.Max(1, colCount - placedCol));
            }

            int finalRowSpan = hasFixedRows && rowCount > 0
                ? Math.Clamp(desiredRowSpan, 1, Math.Max(1, rowCount - placedRow))
                : desiredRowSpan;

            placedArray[idx] = (placedRow, placedCol, finalRowSpan, desiredColSpan);
            outOfRangeArray[idx] = false;
            MarkOccupied(placedRow, placedCol, finalRowSpan, desiredColSpan);
            maxExtent = Math.Max(maxExtent, placedRow + finalRowSpan);

            cursorRow = placedRow;
            cursorCol = placedCol + desiredColSpan;
            if (cursorCol >= colCount)
            {
                cursorRow++;
                cursorCol = 0;
            }
        }

        if (!hasFixedRows && spec.Children.Count > 0)
        {
            while (rows.Count < maxExtent)
            {
                rows.Add(GridLength.Auto);
            }
        }

        placements.AddRange(placedArray);
        outOfRange.AddRange(outOfRangeArray);
        return (cols, rows, placements, outOfRange, hasStarRow);
    }

    private static void ApplyGridTrackDefinitionsAndPlacement(
        Grid grid,
        CustomWidgetNodeSpec spec,
        IReadOnlyList<UIElement> builtChildren,
        string? resolvedCols,
        string? resolvedRows,
        HashSet<int> collapsedOutOfRangeIndices,
        out bool hasStarRow)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        (IReadOnlyList<GridLength> cols, IReadOnlyList<GridLength> rows, IReadOnlyList<(int Row, int Column, int RowSpan, int ColumnSpan)> placements, IReadOnlyList<bool> outOfRange, bool starRow) =
            ComputeGridTrackAndPlacementPlanCore(spec, resolvedCols, resolvedRows);
        hasStarRow = starRow;

        foreach (GridLength width in cols)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        foreach (GridLength height in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        }

        int limit = Math.Min(placements.Count, builtChildren.Count);
        for (int idx = 0; idx < limit; idx++)
        {
            if (builtChildren[idx] is FrameworkElement fe)
            {
                (int row, int col, int rowSpan, int colSpan) = placements[idx];
                Grid.SetColumn(fe, col);
                Grid.SetRow(fe, row);
                Grid.SetColumnSpan(fe, colSpan);
                Grid.SetRowSpan(fe, rowSpan);

                bool isOutOfRange = idx < outOfRange.Count && outOfRange[idx];
                if (isOutOfRange)
                {
                    if (fe.Visibility == Visibility.Visible)
                    {
                        fe.Visibility = Visibility.Collapsed;
                        collapsedOutOfRangeIndices.Add(idx);
                    }
                }
                else if (collapsedOutOfRangeIndices.Remove(idx))
                {
                    fe.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private FrameworkElement BuildGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var grid = new Grid
        {
            Padding = isRoot
                ? ResolveEffectiveRootSafePadding(spec.Padding)
                : ParseThickness(spec.Padding),
            ColumnSpacing = spec.ColumnSpacing.HasValue
                ? Math.Max(0.0, spec.ColumnSpacing.Value)
                : ResolveEffectiveSpacing(spec, isHorizontal: true),
            RowSpacing = spec.RowSpacing.HasValue
                ? Math.Max(0.0, spec.RowSpacing.Value)
                : ResolveEffectiveSpacing(spec, isHorizontal: false)
        };

        string lastCols = !string.IsNullOrWhiteSpace(spec.Columns)
            ? CustomWidgetExpressionEvaluator.Interpolate(spec.Columns, ctx).Trim()
            : string.Empty;
        string lastRows = !string.IsNullOrWhiteSpace(spec.Rows)
            ? CustomWidgetExpressionEvaluator.Interpolate(spec.Rows, ctx).Trim()
            : string.Empty;
        int initialColCount = ComputeGridTrackAndPlacementPlanCore(spec, lastCols, lastRows).Columns.Count;

        var builtChildren = new List<UIElement>(spec.Children.Count);
        int savedParentGridColCount = _parentGridColCount;
        _parentGridColCount = initialColCount;
        try
        {
            for (int idx = 0; idx < spec.Children.Count; idx++)
            {
                UIElement childEl = BuildNode(spec.Children[idx], ctx);
                builtChildren.Add(childEl);
                grid.Children.Add(childEl);
            }
        }
        finally
        {
            _parentGridColCount = savedParentGridColCount;
        }

        var collapsedOutOfRangeIndices = new HashSet<int>();

        ApplyGridTrackDefinitionsAndPlacement(
            grid,
            spec,
            builtChildren,
            lastCols,
            lastRows,
            collapsedOutOfRangeIndices,
            out bool hasStarRow);

        bool hasDynamicCols = spec.Columns?.Contains("${", StringComparison.Ordinal) == true;
        bool hasDynamicRows = spec.Rows?.Contains("${", StringComparison.Ordinal) == true;
        if (hasDynamicCols || hasDynamicRows)
        {
            RegisterRetainedPatcher(evalCtx =>
            {
                string nextCols = !string.IsNullOrWhiteSpace(spec.Columns)
                    ? CustomWidgetExpressionEvaluator.Interpolate(spec.Columns, evalCtx).Trim()
                    : string.Empty;
                string nextRows = !string.IsNullOrWhiteSpace(spec.Rows)
                    ? CustomWidgetExpressionEvaluator.Interpolate(spec.Rows, evalCtx).Trim()
                    : string.Empty;
                if (!string.Equals(nextCols, lastCols, StringComparison.Ordinal) ||
                    !string.Equals(nextRows, lastRows, StringComparison.Ordinal))
                {
                    lastCols = nextCols;
                    lastRows = nextRows;
                    ApplyGridTrackDefinitionsAndPlacement(
                        grid,
                        spec,
                        builtChildren,
                        nextCols,
                        nextRows,
                        collapsedOutOfRangeIndices,
                        out _);
                    grid.InvalidateMeasure();
                }
            });
        }

        FrameworkElement styledGrid = ApplyContainerVisualOverrides(grid, spec, ctx);
        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(grid, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
            if (ContainsDynamicToken(spec.Tooltip))
            {
                RegisterRetainedPatcher(liveCtx =>
                    ToolTipService.SetToolTip(grid, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, liveCtx)));
            }
        }

        if ((spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover)) && !_isNodeSelectionEnabled)
        {
            grid.Background ??= GetOrCreateSolidBrush(Colors.Transparent);
            MarkInteractiveElement(grid);
            CustomWidgetEvaluationContext capturedCtx = ctx;
            RegisterRetainedPatcher(liveCtx => capturedCtx = liveCtx);
            grid.Tapped += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (_isNodeSelectionEnabled ||
                    e.Handled ||
                    IsInteractiveDescendant(e.OriginalSource as DependencyObject, grid))
                {
                    return;
                }

                e.Handled = true;
                await ExecuteInteractiveClickWithPopoverAsync(spec, capturedCtx, grid);
            });
        }

        if (isRoot && !hasStarRow)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(styledGrid)
            };
        }

        return styledGrid;
    }

    private FrameworkElement BuildScroll(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var inner = new StackPanel
        {
            Spacing = ResolveEffectiveSpacing(spec, isHorizontal: false),
            Padding = isRoot
                ? ResolveEffectiveRootSafePadding(spec.Padding)
                : ParseThickness(spec.Padding)
        };
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            inner.Children.Add(BuildNode(child, ctx));
        }

        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = WrapInShadowHostGrid(inner)
        };
        return ApplyContainerVisualOverrides(sv, spec, ctx);
    }

    internal void RegisterModuleStackInvalidationPatcher(
        string settingKey,
        string? defaultModules,
        string? initialRawModules,
        bool markRetainedRootForTest = false)
    {
        if (string.IsNullOrWhiteSpace(settingKey))
        {
            return;
        }

        string capturedRawModules = initialRawModules ?? string.Empty;
        void CheckModules(CustomWidgetEvaluationContext liveCtx)
        {
            string? liveRaw = liveCtx.Settings.TryGetValue(settingKey, out string? cur)
                ? cur
                : defaultModules;
            if (!string.Equals(liveRaw ?? string.Empty, capturedRawModules, StringComparison.Ordinal))
            {
                _hasRetainedRoot = false;
            }
        }

        if (markRetainedRootForTest)
        {
            RegisterRetainedPatcherForTest(CheckModules, markRetainedRoot: true);
        }
        else
        {
            RegisterRetainedPatcher(CheckModules);
        }
    }

    internal readonly record struct ModuleRowPlan(
        int StartIndex,
        bool IsPair,
        bool MatchesTrailingPin,
        GridLength LeftWidth,
        GridLength RightWidth);

    internal static bool CanPairModuleSpecs(CustomWidgetNodeSpec specA, CustomWidgetNodeSpec specB)
    {
        if (!specA.CompactPair || !specB.CompactPair)
        {
            return false;
        }

        string groupA = specA.PairGroup?.Trim() ?? string.Empty;
        string groupB = specB.PairGroup?.Trim() ?? string.Empty;
        return string.Equals(groupA, groupB, StringComparison.OrdinalIgnoreCase);
    }

    internal static GridLength ParseModulePairColumnLength(string? rawPairWidth)
    {
        if (string.IsNullOrWhiteSpace(rawPairWidth))
        {
            return new GridLength(1, GridUnitType.Star);
        }

        string token = rawPairWidth.Trim();
        if (string.Equals(token, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return GridLength.Auto;
        }

        if (token.EndsWith('*'))
        {
            string weightText = token[..^1].Trim();
            if (weightText.Length == 0)
            {
                return new GridLength(1, GridUnitType.Star);
            }

            if (double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out double starWeight) &&
                double.IsFinite(starWeight) &&
                starWeight > 0)
            {
                return new GridLength(starWeight, GridUnitType.Star);
            }
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
            double.IsFinite(px) &&
            px > 0)
        {
            return new GridLength(px, GridUnitType.Pixel);
        }

        return new GridLength(1, GridUnitType.Star);
    }

    internal static List<ModuleRowPlan> PlanModuleStackRows(
        IReadOnlyList<(string Id, CustomWidgetNodeSpec Node)> enabledNodes,
        string? rawPinTrailingGroup,
        bool isRoot,
        out bool shouldPinLastRowToFooter)
    {
        var rowPlans = new List<ModuleRowPlan>();
        string pinGroup = rawPinTrailingGroup?.Trim() ?? string.Empty;
        for (int i = 0; i < enabledNodes.Count; i++)
        {
            CustomWidgetNodeSpec specA = enabledNodes[i].Node;
            bool isPair = i + 1 < enabledNodes.Count && CanPairModuleSpecs(specA, enabledNodes[i + 1].Node);
            bool matchesPin = pinGroup.Length > 0 &&
                              string.Equals(specA.PairGroup?.Trim(), pinGroup, StringComparison.OrdinalIgnoreCase) &&
                              (!isPair || string.Equals(enabledNodes[i + 1].Node.PairGroup?.Trim(), pinGroup, StringComparison.OrdinalIgnoreCase));
            GridLength leftLen = isPair ? ParseModulePairColumnLength(specA.PairWidth) : new GridLength(1, GridUnitType.Star);
            GridLength rightLen = isPair ? ParseModulePairColumnLength(enabledNodes[i + 1].Node.PairWidth) : GridLength.Auto;
            rowPlans.Add(new ModuleRowPlan(i, isPair, matchesPin, leftLen, rightLen));
            if (isPair)
            {
                i++;
            }
        }

        shouldPinLastRowToFooter = isRoot && rowPlans.Count >= 2 && rowPlans[^1].MatchesTrailingPin;
        return rowPlans;
    }

    private sealed record DraggableModuleSlot(
        string ModuleId,
        int EnabledIndex,
        Grid HostGrid,
        Border DropIndicator);

    private FrameworkElement BuildModuleStack(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false,
        string? outerPaddingFallback = null)
    {
        string bindRaw = spec.Bind ?? "settings.modules";
        string settingKey = bindRaw.StartsWith("settings.", StringComparison.OrdinalIgnoreCase)
            ? bindRaw["settings.".Length..]
            : bindRaw.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase)
                ? bindRaw["$settings.".Length..]
                : bindRaw;

        CustomWidgetSettingDefinition? settingDef = _viewModel.Manifest
            .ResolveLocalizedSettings(ctx.CultureName)
            .FirstOrDefault(d => string.Equals(d.Key, settingKey, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<CustomWidgetOptionSpec> definedOptions = settingDef?.Options ?? [];

        string? rawModules = ctx.Settings.TryGetValue(settingKey, out string? saved)
            ? saved
            : settingDef?.DefaultValue;

        IReadOnlyList<CustomWidgetModuleEntry> entries =
            CustomWidgetModuleListHelper.ParseEntries(rawModules, definedOptions);

        var childById = new Dictionary<string, CustomWidgetNodeSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            if (!string.IsNullOrWhiteSpace(child.Id))
            {
                childById[child.Id] = child;
            }
        }

        var enabledNodes = new List<(string Id, CustomWidgetNodeSpec Node)>();
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetModuleEntry entry in entries)
        {
            if (!entry.Enabled || !childById.TryGetValue(entry.Id, out CustomWidgetNodeSpec? childSpec))
            {
                continue;
            }

            matchedIds.Add(entry.Id);
            enabledNodes.Add((entry.Id, childSpec));
        }

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            if (!string.IsNullOrWhiteSpace(child.Id) &&
                (matchedIds.Contains(child.Id) ||
                 definedOptions.Any(o => string.Equals(o.Value, child.Id, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            enabledNodes.Add((child.Id ?? string.Empty, child));
        }

        string? rawModulePadding = !string.IsNullOrWhiteSpace(spec.Padding) ? spec.Padding : outerPaddingFallback;
        Thickness effectivePadding = isRoot
            ? ResolveEffectiveRootSafePadding(rawModulePadding)
            : ParseThickness(rawModulePadding);
        double verticalSpacing = ResolveEffectiveSpacing(spec, isHorizontal: false);

        var container = new StackPanel
        {
            Spacing = verticalSpacing,
            Padding = effectivePadding,
            ChildrenTransitions = CreateRepositionTransitions()
        };
        UIElement dragCoordinateRoot = container;

        var slotRecords = new List<DraggableModuleSlot>(enabledNodes.Count);
        bool canDragReorder = !_isNodeSelectionEnabled &&
                              enabledNodes.Count > 1 &&
                              !string.IsNullOrWhiteSpace(settingKey);

        RegisterModuleStackInvalidationPatcher(settingKey, settingDef?.DefaultValue, rawModules);

        Brush accentBrush = ResolveSemanticBrush("accent");
        Color accentCol = accentBrush is SolidColorBrush ascb
            ? ascb.Color
            : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Brush dropHighlightFill = GetOrCreateSolidBrush(Color.FromArgb((byte)(IsDark ? 26 : 18), accentCol.R, accentCol.G, accentCol.B));

        Grid WrapModuleSlot(string moduleId, int enabledIndex, CustomWidgetNodeSpec nodeSpec, bool isPaired = false)
        {
            CustomWidgetEvaluationContext slotCtx = isPaired ? ctx.WithPairedSlot(true) : ctx;
            UIElement content;
            if (isPaired)
            {
                _buildingPairedSlotDepth++;
                try
                {
                    content = BuildNode(nodeSpec, slotCtx);
                }
                finally
                {
                    _buildingPairedSlotDepth--;
                }
            }
            else
            {
                content = BuildNode(nodeSpec, slotCtx);
            }

            var host = new Grid();
            host.Children.Add(content);
            if (!string.IsNullOrWhiteSpace(nodeSpec.When) && content is FrameworkElement contentFe)
            {
                host.Visibility = contentFe.Visibility;
                RegisterRetainedPatcher(_ => host.Visibility = contentFe.Visibility);
            }

            var dropIndicator = new Border
            {
                CornerRadius = CornerLarge,
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1.5),
                Background = dropHighlightFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            host.Children.Add(dropIndicator);

            if (canDragReorder && !string.IsNullOrWhiteSpace(moduleId))
            {
                Microsoft.UI.Xaml.Shapes.Path gripIcon = CreateSixDotGripIcon(ResolveForegroundBrush("muted"));
                gripIcon.RenderTransformOrigin = new Point(0.5, 0.5);
                gripIcon.RenderTransform = new RotateTransform { Angle = 90 };

                var handleBorder = new Border
                {
                    Width = 28,
                    Height = 12,
                    CornerRadius = new CornerRadius(6),
                    Background = GetOrCreateSolidBrush(Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0,
                    ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
                    Child = gripIcon
                };
                MarkInteractiveElement(handleBorder);
                TryApplyDragGripCursor(handleBorder);
                handleBorder.Tapped += static (_, e) => e.Handled = true;
                handleBorder.DoubleTapped += static (_, e) => e.Handled = true;
                ToolTipService.SetToolTip(
                    handleBorder,
                    Tr("CustomWidget.Module.DragHint", ctx.CultureName));

                bool isDragging = false;
                uint activePointerId = 0;
                Point pressPoint = default;
                bool movedEnough = false;
                int targetIndex = enabledIndex;

                host.PointerEntered += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Opacity = 0.78;
                    }
                };
                host.PointerExited += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Opacity = 0;
                    }
                };

                handleBorder.PointerEntered += (_, _) =>
                {
                    handleBorder.Background = GetBrush(
                        "SubtleFillColorSecondaryBrush",
                        IsDark ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
                    handleBorder.Opacity = 1;
                };
                handleBorder.PointerExited += (_, _) =>
                {
                    if (!isDragging)
                    {
                        handleBorder.Background = GetOrCreateSolidBrush(Colors.Transparent);
                    }
                };

                handleBorder.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(handleBorder).Properties.IsLeftButtonPressed)
                    {
                        return;
                    }

                    isDragging = true;
                    BeginInteractiveSession(handleBorder);
                    movedEnough = false;
                    targetIndex = enabledIndex;
                    activePointerId = e.Pointer.PointerId;
                    pressPoint = e.GetCurrentPoint(dragCoordinateRoot).Position;
                    handleBorder.CapturePointer(e.Pointer);
                    host.Opacity = 0.65;
                    e.Handled = true;
                };

                handleBorder.PointerMoved += (_, e) =>
                {
                    if (!isDragging || e.Pointer.PointerId != activePointerId)
                    {
                        return;
                    }

                    Point pt = e.GetCurrentPoint(dragCoordinateRoot).Position;
                    if (!movedEnough)
                    {
                        double dx = pt.X - pressPoint.X;
                        double dy = pt.Y - pressPoint.Y;
                        if ((dx * dx) + (dy * dy) < 16)
                        {
                            return;
                        }
                        movedEnough = true;
                    }

                    int bestSlot = enabledIndex;
                    double bestDistSq = double.MaxValue;
                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        if (slot.HostGrid.ActualWidth <= 0 || slot.HostGrid.ActualHeight <= 0 ||
                            slot.HostGrid.Visibility != Visibility.Visible)
                        {
                            continue;
                        }

                        Point origin = slot.HostGrid
                            .TransformToVisual(dragCoordinateRoot)
                            .TransformPoint(new Point(0, 0));
                        double cx = origin.X + (slot.HostGrid.ActualWidth * 0.5);
                        double cy = origin.Y + (slot.HostGrid.ActualHeight * 0.5);
                        double distSq = ((pt.X - cx) * (pt.X - cx)) + ((pt.Y - cy) * (pt.Y - cy));
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            bestSlot = slot.EnabledIndex;
                        }
                    }

                    targetIndex = bestSlot;
                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        slot.DropIndicator.Visibility =
                            slot.EnabledIndex == targetIndex && targetIndex != enabledIndex
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                    }

                    e.Handled = true;
                };

                async void FinishDrag(PointerRoutedEventArgs e, bool commit)
                {
                    if (!isDragging)
                    {
                        return;
                    }

                    isDragging = false;
                    handleBorder.ReleasePointerCapture(e.Pointer);
                    host.Opacity = 1;
                    handleBorder.Background = GetOrCreateSolidBrush(Colors.Transparent);
                    handleBorder.Opacity = 0;

                    foreach (DraggableModuleSlot slot in slotRecords)
                    {
                        slot.DropIndicator.Visibility = Visibility.Collapsed;
                    }

                    if (commit && movedEnough && targetIndex != enabledIndex)
                    {
                        _activeInteractions.Remove(handleBorder);
                        try
                        {
                            string? latestRaw = _viewModel.Settings.TryGetValue(settingKey, out string? cur)
                                ? cur
                                : rawModules;
                            string updated = CustomWidgetModuleListHelper.ReorderEnabledModule(
                                latestRaw,
                                definedOptions,
                                moduleId,
                                targetIndex);
                            await _viewModel.SetSettingValueAsync(settingKey, updated);
                        }
                        catch (Exception ex)
                        {
                            App.Log($"[CustomWidget] Module drag commit failed: {ex.Message}");
                        }
                        finally
                        {
                            NotifyIfAllInteractionsClosed();
                        }
                    }
                    else
                    {
                        EndInteractiveSession(handleBorder);
                    }
                }

                handleBorder.PointerReleased += (_, e) =>
                {
                    e.Handled = true;
                    FinishDrag(e, commit: true);
                };
                handleBorder.PointerCanceled += (_, e) => FinishDrag(e, commit: false);
                handleBorder.PointerCaptureLost += (_, e) => FinishDrag(e, commit: false);

                host.Children.Add(handleBorder);
            }

            slotRecords.Add(new DraggableModuleSlot(moduleId, enabledIndex, host, dropIndicator));
            return host;
        }

        FrameworkElement BuildVisualRow(ModuleRowPlan plan)
        {
            (string idA, CustomWidgetNodeSpec specA) = enabledNodes[plan.StartIndex];
            if (!plan.IsPair)
            {
                return WrapModuleSlot(idA, plan.StartIndex, specA, isPaired: false);
            }

            (string idB, CustomWidgetNodeSpec specB) = enabledNodes[plan.StartIndex + 1];
            var pairGrid = new Grid
            {
                ColumnSpacing = ResolveEffectiveSpacing(spec, isHorizontal: true)
            };
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = plan.LeftWidth });
            pairGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = plan.RightWidth });

            Grid leftSlot = WrapModuleSlot(idA, plan.StartIndex, specA, isPaired: true);
            Grid rightSlot = WrapModuleSlot(idB, plan.StartIndex + 1, specB, isPaired: true);
            Grid.SetColumn(leftSlot, 0);
            Grid.SetColumn(rightSlot, 1);
            pairGrid.Children.Add(leftSlot);
            pairGrid.Children.Add(rightSlot);
            return pairGrid;
        }

        List<ModuleRowPlan> rowPlans = PlanModuleStackRows(
            enabledNodes,
            spec.PinTrailingGroup,
            isRoot,
            out bool shouldPinFooter);

        if (shouldPinFooter)
        {
            container.Padding = new Thickness(
                effectivePadding.Left,
                effectivePadding.Top,
                effectivePadding.Right,
                0);

            for (int r = 0; r < rowPlans.Count - 1; r++)
            {
                container.Children.Add(BuildVisualRow(rowPlans[r]));
            }

            var upperScrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(container)
            };

            FrameworkElement pinnedRowElement = BuildVisualRow(rowPlans[^1]);
            var pinnedFooterHost = new Border
            {
                Padding = new Thickness(
                    effectivePadding.Left,
                    verticalSpacing,
                    effectivePadding.Right,
                    effectivePadding.Bottom),
                Child = pinnedRowElement
            };

            var pinnedRootGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            pinnedRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            pinnedRootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(upperScrollViewer, 0);
            Grid.SetRow(pinnedFooterHost, 1);
            pinnedRootGrid.Children.Add(upperScrollViewer);
            pinnedRootGrid.Children.Add(pinnedFooterHost);

            dragCoordinateRoot = pinnedRootGrid;
            return pinnedRootGrid;
        }

        for (int r = 0; r < rowPlans.Count; r++)
        {
            container.Children.Add(BuildVisualRow(rowPlans[r]));
        }

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(container)
            };
        }

        return container;
    }

    internal static bool TryResolveComputedRecords(
        string? rawBinding,
        CustomWidgetEvaluationContext ctx,
        out IReadOnlyList<CustomWidgetRecord> records)
    {
        records = [];
        if (string.IsNullOrWhiteSpace(rawBinding))
        {
            return false;
        }

        string trimmed = rawBinding.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
        {
            trimmed = trimmed[2..^1].Trim();
        }

        if (CustomWidgetExpressionEvaluator.TryResolveVirtualCollection(trimmed, ctx, out records))
        {
            return true;
        }

        if (trimmed.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
        {
            records = CustomWidgetScriptEngine.ProjectComputedCollection(trimmed, ctx.Computed);
            return true;
        }

        if (trimmed.StartsWith("$state.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("state.", StringComparison.OrdinalIgnoreCase))
        {
            records = CustomWidgetScriptEngine.ProjectComputedCollection(trimmed, ctx.StateJson);
            return true;
        }

        return false;
    }

    internal static bool IsDividedOrFlushListVariant(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return false;
        }

        ReadOnlySpan<char> v = variant.AsSpan().Trim();
        return v.Equals("divided", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("flush", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("plain", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDividedListVariant(string? variant) =>
        !string.IsNullOrWhiteSpace(variant) &&
        variant.AsSpan().Trim().Equals("divided", StringComparison.OrdinalIgnoreCase);

    private FrameworkElement BuildList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string resolvedVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, ctx);
        bool isFlatRows = IsDividedOrFlushListVariant(resolvedVariant);
        double defaultListSpacing = isFlatRows ? 0 : 4;

        if (!spec.AllowReorder)
        {
            var retainedStack = new StackPanel
            {
                Spacing = spec.HasExplicitSpacing
                    ? ResolveEffectiveSpacing(spec, isHorizontal: false, defaultSpacing: defaultListSpacing)
                    : defaultListSpacing,
                ChildrenTransitions = CreateRepositionTransitions()
            };

            return BuildRetainedCollectionView(
                spec,
                ctx,
                retainedStack,
                wrapInScrollViewer: spec.MaxLines <= 0 &&
                                    (spec.MaxItems <= 0 || spec.Height.HasValue || spec.MaxHeight.HasValue),
                new CustomWidgetEmptyStateSpec(
                    "\uE70F",
                    Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    string.Empty),
                (itemCtx, resolvedCol) => BuildListItemCard(spec, itemCtx, resolvedCol));
        }

        string rawItemsBinding = spec.Items ?? spec.Collection ?? "logs";
        int maxVisibleItems = spec.MaxLines > 0 ? spec.MaxLines : int.MaxValue;

        var itemsStack = new StackPanel
        {
            Spacing = spec.HasExplicitSpacing
                ? ResolveEffectiveSpacing(spec, isHorizontal: false, defaultSpacing: defaultListSpacing)
                : defaultListSpacing,
            ChildrenTransitions = CreateRepositionTransitions()
        };

        FrameworkElement emptyElement = BuildEmptyState(
            spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                "\uE70F",
                Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                string.Empty),
            ctx);

        FrameworkElement itemsHostElement = spec.MaxLines > 0
            ? itemsStack
            : new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(itemsStack)
            };

        var hostGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        hostGrid.Children.Add(emptyElement);
        hostGrid.Children.Add(itemsHostElement);

        var dragSlots = new List<DraggableListItemSlot>();
        var itemScopedPatchers = new List<(int RecordIndex, CustomWidgetRecord Record, List<Action<CustomWidgetEvaluationContext>> Patchers)>();
        string lastSignature = "\0";

        (List<(CustomWidgetRecord Record, int Index)> Filtered, string ResolvedCollectionName, string Signature) ResolveReorderItems(
            CustomWidgetEvaluationContext evalCtx)
        {
            string resolvedCollectionName = string.Empty;
            if (!TryResolveComputedRecords(rawItemsBinding, evalCtx, out IReadOnlyList<CustomWidgetRecord> allRecords))
            {
                string collectionName = CustomWidgetExpressionEvaluator
                    .Interpolate(rawItemsBinding, evalCtx)
                    .Trim();
                if (string.IsNullOrWhiteSpace(collectionName))
                {
                    collectionName = "logs";
                }

                if (collectionName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    collectionName = collectionName["collections.".Length..];
                    int dot = collectionName.IndexOf('.');
                    if (dot > 0) collectionName = collectionName[..dot];
                }

                resolvedCollectionName = collectionName;
                allRecords = evalCtx.Collections.TryGetValue(collectionName, out List<CustomWidgetRecord>? list)
                    ? list
                    : [];
            }
            else if (!string.IsNullOrWhiteSpace(spec.Collection) &&
                     !spec.Collection.Contains("${", StringComparison.Ordinal))
            {
                string explicitCol = spec.Collection.Trim();
                if (explicitCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    explicitCol = explicitCol["collections.".Length..];
                }
                resolvedCollectionName = explicitCol;
            }

            string? filterEqualsVal = !string.IsNullOrWhiteSpace(spec.FilterEquals)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.FilterEquals, evalCtx)
                : null;

            var filtered = new List<(CustomWidgetRecord Record, int Index)>();
            var sig = new System.Text.StringBuilder(resolvedCollectionName.Length + 32);
            sig.Append(resolvedCollectionName).Append('|');
            for (int i = 0; i < allRecords.Count && filtered.Count < maxVisibleItems; i++)
            {
                CustomWidgetRecord rec = allRecords[i];
                if (rec.IsDeleted)
                {
                    continue;
                }

                CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(rec, i);
                if (!string.IsNullOrWhiteSpace(spec.Filter) &&
                    !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, itemCtx))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(spec.ItemTemplate?.When) &&
                    !CustomWidgetExpressionEvaluator.EvaluateCondition(spec.ItemTemplate.When, itemCtx))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(spec.FilterField) && filterEqualsVal is not null)
                {
                    string fv = rec.GetField(spec.FilterField);
                    if (!string.Equals(fv, filterEqualsVal, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                filtered.Add((rec, i));
                sig.Append(!string.IsNullOrWhiteSpace(rec.Id) ? rec.Id : i.ToString(CultureInfo.InvariantCulture))
                   .Append('@')
                   .Append(i)
                   .Append(':')
                   .Append(rec.EffectiveUpdatedAtUtcTicks)
                   .Append(';');
            }

            return (filtered, resolvedCollectionName, sig.ToString());
        }

        void ReconcileReorderList(CustomWidgetEvaluationContext evalCtx)
        {
            (List<(CustomWidgetRecord Record, int Index)> filtered, string resolvedCollectionName, string nextSig) =
                ResolveReorderItems(evalCtx);

            if (string.Equals(nextSig, lastSignature, StringComparison.Ordinal))
            {
                foreach ((int recIdx, CustomWidgetRecord rec, List<Action<CustomWidgetEvaluationContext>> patchers) in itemScopedPatchers)
                {
                    if (patchers.Count > 0)
                    {
                        CustomWidgetEvaluationContext itemLiveCtx = evalCtx.WithItem(rec, recIdx);
                        foreach (Action<CustomWidgetEvaluationContext> p in patchers)
                        {
                            p(itemLiveCtx);
                        }
                    }
                }
                return;
            }

            // Defer structural rebuild if the user is actively dragging a slot.
            foreach (DraggableListItemSlot slot in dragSlots)
            {
                if (slot.DropIndicator.Visibility == Visibility.Visible)
                {
                    return;
                }
            }

            lastSignature = nextSig;
            foreach (UIElement oldChild in itemsStack.Children)
            {
                ForgetRetainedSubtree(oldChild);
            }
            itemsStack.Children.Clear();
            dragSlots.Clear();
            itemScopedPatchers.Clear();

            if (filtered.Count == 0)
            {
                emptyElement.Visibility = Visibility.Visible;
                itemsHostElement.Visibility = Visibility.Collapsed;
                return;
            }

            emptyElement.Visibility = Visibility.Collapsed;
            itemsHostElement.Visibility = Visibility.Visible;

            int visualIndex = 0;
            foreach ((CustomWidgetRecord record, int index) in filtered)
            {
                CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(record, index);
                int capturedVisualIndex = visualIndex++;
                UIElement wrappedItem = null!;
                List<Action<CustomWidgetEvaluationContext>> scoped = CaptureScopedPatchers(() =>
                {
                    FrameworkElement itemCard = BuildListItemCard(spec, itemCtx, resolvedCollectionName, capturedVisualIndex);
                    wrappedItem = WrapListItemForDragReorder(
                        itemCard,
                        index,
                        resolvedCollectionName,
                        itemsStack,
                        dragSlots,
                        spec,
                        evalCtx);
                });
                itemsStack.Children.Add(wrappedItem);
                itemScopedPatchers.Add((index, record, scoped));
            }
        }

        ReconcileReorderList(ctx);
        RegisterRetainedPatcher(ReconcileReorderList);
        return ApplyContainerVisualOverrides(hostGrid, spec, ctx);
    }

    private FrameworkElement BuildListItemCard(
        CustomWidgetNodeSpec listSpec,
        CustomWidgetEvaluationContext itemCtx,
        string resolvedCollectionName = "",
        int visualRowIndex = 0)
    {
        var itemGrid = new Grid();
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement body = listSpec.ItemTemplate is not null
            ? BuildNode(listSpec.ItemTemplate, itemCtx)
            : BuildDefaultListItemBody(listSpec, itemCtx);
        if (body is FrameworkElement bodyFe)
        {
            Grid.SetColumn(bodyFe, 0);
        }
        itemGrid.Children.Add(body);

        string resolvedListVariant = CustomWidgetExpressionEvaluator.Interpolate(listSpec.Variant, itemCtx);
        bool isFlatRow = IsDividedOrFlushListVariant(resolvedListVariant);
        bool isDividedRow = IsDividedListVariant(resolvedListVariant);

        FrameworkElement? rightMetaFadeTarget = null;
        if (isFlatRow &&
            listSpec.HoverActions.Count > 0 &&
            body is Grid bodyGrid &&
            bodyGrid.ColumnDefinitions.Count >= 3)
        {
            int lastColIdx = bodyGrid.ColumnDefinitions.Count - 1;
            foreach (UIElement child in bodyGrid.Children)
            {
                if (child is FrameworkElement colFe &&
                    Grid.GetColumn(colFe) == lastColIdx &&
                    colFe is not Button &&
                    !IsInteractiveElement(colFe))
                {
                    rightMetaFadeTarget = colFe;
                    if (FluentMotionHelper.AreAnimationsEnabled)
                    {
                        try
                        {
                            colFe.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(110) };
                        }
                        catch
                        {
                        }
                    }
                    break;
                }
            }
        }

        Border? hoverOverlay = null;
        if (listSpec.HoverActions.Count > 0)
        {
            var hoverPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (CustomWidgetNodeSpec actionNode in listSpec.HoverActions)
            {
                hoverPanel.Children.Add(BuildNode(actionNode, itemCtx));
            }

            Brush overlayBg;
            if (isFlatRow)
            {
                Color featherBase = IsDark
                    ? Color.FromArgb(245, 36, 34, 38)
                    : Color.FromArgb(245, 246, 247, 250);
                var featherBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };
                featherBrush.GradientStops.Add(new GradientStop
                {
                    Color = Color.FromArgb(0, featherBase.R, featherBase.G, featherBase.B),
                    Offset = 0.0
                });
                featherBrush.GradientStops.Add(new GradientStop
                {
                    Color = featherBase,
                    Offset = 0.24
                });
                featherBrush.GradientStops.Add(new GradientStop
                {
                    Color = featherBase,
                    Offset = 1.0
                });
                overlayBg = featherBrush;
            }
            else
            {
                overlayBg = GetBrush(
                    "SolidBackgroundFillColorSecondaryBrush",
                    IsDark ? Color.FromArgb(236, 36, 36, 40) : Color.FromArgb(242, 248, 248, 250));
            }

            hoverOverlay = new Border
            {
                CornerRadius = CornerSmall,
                Padding = isFlatRow ? new Thickness(22, 2, 2, 2) : new Thickness(4, 2, 4, 2),
                Background = overlayBg,
                BorderThickness = new Thickness(isFlatRow ? 0 : 1),
                BorderBrush = CardStrokeBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
                Child = hoverPanel
            };
            if (isFlatRow && FluentMotionHelper.AreAnimationsEnabled)
            {
                try
                {
                    hoverOverlay.Translation = new System.Numerics.Vector3(6f, 0f, 0f);
                    hoverOverlay.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(130) };
                }
                catch
                {
                }
            }
            Grid.SetColumn(hoverOverlay, 0);
            itemGrid.Children.Add(hoverOverlay);
        }

        Brush defaultBg = isFlatRow
            ? GetOrCreateSolidBrush(Colors.Transparent)
            : ResolveCardBackground("default");
        Brush hoverBg = SubtleHoverBrush;

        Thickness rowPadding = isFlatRow
            ? (!string.IsNullOrWhiteSpace(listSpec.ItemTemplate?.Padding)
                ? new Thickness(0)
                : new Thickness(2, 6, 2, 6))
            : new Thickness(10, 7, 10, 7);

        var cardBorder = new Border
        {
            CornerRadius = isDividedRow ? new CornerRadius(0) : CornerSmall,
            MinHeight = isFlatRow ? 32 : 40,
            Padding = rowPadding,
            Background = defaultBg,
            BorderThickness = isDividedRow
                ? (visualRowIndex > 0 ? new Thickness(0, 1, 0, 0) : new Thickness(0))
                : (isFlatRow ? new Thickness(0) : CardBorderThickness),
            BorderBrush = isDividedRow ? DividerBrush : CardStrokeBrush,
            Child = itemGrid
        };

        AttachListItemInlineEditor(
            listSpec,
            resolvedCollectionName,
            itemCtx.CurrentItem,
            cardBorder,
            itemGrid,
            body,
            bodyColumnIndex: 0);

        if (_isNodeSelectionEnabled)
        {
            if (hoverOverlay is not null)
            {
                hoverOverlay.Opacity = 0.85;
            }
        }
        else
        {
            AttachInteractiveSurfaceFeedback(cardBorder, hoverOverlay);
            bool isPointerOverCard = false;

            static bool HasAncestorOrSelf(DependencyObject? node, DependencyObject target)
            {
                while (node is not null)
                {
                    if (ReferenceEquals(node, target))
                    {
                        return true;
                    }
                    node = VisualTreeHelper.GetParent(node);
                }
                return false;
            }

            void UpdateHoverOverlayState()
            {
                bool hasFocusInside = cardBorder.XamlRoot is not null &&
                                      FocusManager.GetFocusedElement(cardBorder.XamlRoot) is DependencyObject focused &&
                                      HasAncestorOrSelf(focused, cardBorder);
                bool show = isPointerOverCard || hasFocusInside;
                cardBorder.Background = show ? hoverBg : defaultBg;
                if (rightMetaFadeTarget is not null)
                {
                    rightMetaFadeTarget.Opacity = show ? 0 : 1;
                }
                if (hoverOverlay is not null)
                {
                    hoverOverlay.IsHitTestVisible = show;
                    hoverOverlay.Opacity = show ? 1 : 0;
                    if (isFlatRow)
                    {
                        hoverOverlay.Translation = show
                            ? System.Numerics.Vector3.Zero
                            : new System.Numerics.Vector3(6f, 0f, 0f);
                    }
                }
            }

            cardBorder.PointerEntered += (_, _) =>
            {
                isPointerOverCard = true;
                UpdateHoverOverlayState();
            };
            cardBorder.PointerExited += (_, _) =>
            {
                isPointerOverCard = false;
                UpdateHoverOverlayState();
            };
            cardBorder.GotFocus += (_, _) => UpdateHoverOverlayState();
            cardBorder.LostFocus += (_, _) => UpdateHoverOverlayState();
        }

        return cardBorder;
    }

    private FrameworkElement BuildSegmented(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string ResolveSelectedValue(CustomWidgetEvaluationContext evalCtx)
        {
            string val = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx)
                : string.Empty;
            if (string.IsNullOrEmpty(val) && !string.IsNullOrWhiteSpace(spec.Value))
            {
                val = CustomWidgetExpressionEvaluator.Interpolate(spec.Value, evalCtx);
            }
            return val;
        }

        string currentVal = ResolveSelectedValue(ctx);
        var segmented = new ToolkitControls.Segmented
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        MarkInteractiveElement(segmented);

        string segStyle = string.Equals(spec.Variant, "pivot", StringComparison.OrdinalIgnoreCase) && Application.Current is not null
            ? SettingsService.WidgetTabStylePivot
            : SettingsService.WidgetTabStyleButton;
        WidgetSegmentedStyleHelper.Apply(segmented, segStyle);
        segmented.Loaded += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);
        segmented.ActualThemeChanged += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);

        CustomWidgetEvaluationContext capturedSegCtx = ctx;
        var segmentEntries = new List<(string Value, string RawLabel, string? RawIcon, ToolkitControls.SegmentedItem Item)>(spec.Options.Count);
        ToolkitControls.SegmentedItem? initialSelectedItem = null;

        void PopulateSegmentedItemVisuals(
            ToolkitControls.SegmentedItem segItem,
            string rawLabel,
            string? rawIcon,
            CustomWidgetEvaluationContext evalCtx)
        {
            string label = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, evalCtx);
            string? normIcon = ResolveNormalizedIcon(rawIcon, evalCtx);
            if (!string.IsNullOrWhiteSpace(normIcon) && CustomWidgetTemplates.IsSegoeFluentGlyph(normIcon))
            {
                string osGlyph = NormalizeFluentGlyphForOs(normIcon, WindowsCompatibilityService.IsWindows11OrLater);
                if (segItem.Icon is FontIcon existingFi)
                {
                    if (!string.Equals(existingFi.Glyph, osGlyph, StringComparison.Ordinal))
                    {
                        existingFi.Glyph = osGlyph;
                    }
                }
                else
                {
                    segItem.Icon = new FontIcon { Glyph = osGlyph, FontSize = 12 };
                }
                segItem.Content = string.IsNullOrWhiteSpace(label) ? null : label;
            }
            else if (!string.IsNullOrWhiteSpace(normIcon))
            {
                segItem.ClearValue(ToolkitControls.SegmentedItem.IconProperty);
                segItem.Content = string.IsNullOrWhiteSpace(label) ? normIcon : $"{normIcon} {label}";
            }
            else
            {
                segItem.ClearValue(ToolkitControls.SegmentedItem.IconProperty);
                segItem.Content = label;
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                AutomationProperties.SetName(segItem, label);
            }
        }

        foreach (CustomWidgetOptionSpec opt in spec.Options)
        {
            var segItem = new ToolkitControls.SegmentedItem
            {
                Tag = opt.Value,
                FontSize = CaptionFontSize
            };
            PopulateSegmentedItemVisuals(segItem, opt.Label, opt.Icon, ctx);
            segmented.Items.Add(segItem);
            segmentEntries.Add((opt.Value, opt.Label, opt.Icon, segItem));

            if (string.Equals(opt.Value, currentVal, StringComparison.Ordinal))
            {
                initialSelectedItem = segItem;
            }
        }

        bool suppressSelectionEvent = true;
        segmented.SelectedItem = initialSelectedItem;
        suppressSelectionEvent = false;

        segmented.SelectionChanged += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (suppressSelectionEvent ||
                segmented.SelectedItem is not ToolkitControls.SegmentedItem selected ||
                selected.Tag is not string targetValue)
            {
                return;
            }

            CustomWidgetEvaluationContext evCtx = capturedSegCtx.WithEventValue(targetValue);
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: "$value")
                ], evCtx);
            }
            if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
            }
        });

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedSegCtx = liveCtx;
            string liveVal = ResolveSelectedValue(liveCtx);
            ToolkitControls.SegmentedItem? matchedItem = null;
            foreach ((string optVal, string rawLbl, string? rawIco, ToolkitControls.SegmentedItem segItem) in segmentEntries)
            {
                if (string.Equals(optVal, liveVal, StringComparison.Ordinal))
                {
                    matchedItem = segItem;
                }
                if (ContainsDynamicToken(rawLbl) || ContainsDynamicToken(rawIco))
                {
                    PopulateSegmentedItemVisuals(segItem, rawLbl, rawIco, liveCtx);
                }
            }

            if (!ReferenceEquals(segmented.SelectedItem, matchedItem))
            {
                suppressSelectionEvent = true;
                try
                {
                    segmented.SelectedItem = matchedItem;
                }
                finally
                {
                    suppressSelectionEvent = false;
                }
            }
        });

        bool stretchSegmented = string.Equals(spec.HorizontalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        ScrollViewer segmentedScroll = WrapInHorizontalStripScrollViewer(segmented, stretchSegmented);
        return ApplyContainerVisualOverrides(segmentedScroll, spec, ctx);
    }

    private FrameworkElement BuildBadge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawText = spec.Text ?? spec.Value ?? string.Empty;
        string text = CustomWidgetExpressionEvaluator.Interpolate(rawText, ctx);
        string rawToneExpr = (!string.IsNullOrWhiteSpace(spec.Tone) && !string.Equals(spec.Tone, "default", StringComparison.OrdinalIgnoreCase))
            ? spec.Tone
            : (!string.IsNullOrWhiteSpace(spec.Color) ? spec.Color! : spec.Tone);
        string resolvedTone = CustomWidgetExpressionEvaluator.Interpolate(rawToneExpr, ctx);
        string resolvedVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, ctx);
        bool isPill = resolvedVariant.AsSpan().Trim().Equals("pill", StringComparison.OrdinalIgnoreCase) ||
                      resolvedVariant.AsSpan().Trim().Equals("status", StringComparison.OrdinalIgnoreCase) ||
                      resolvedVariant.AsSpan().Trim().Equals("outline", StringComparison.OrdinalIgnoreCase);

        (Brush bg, Brush fg) = ResolveBadgeColors(resolvedTone);

        var tb = new TextBlock
        {
            Text = text,
            FontSize = CaptionFontSize,
            FontWeight = TryResolveFontWeight(spec.FontWeight) ?? FontWeights.SemiBold,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        if (TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitBadgeFg) && explicitBadgeFg is not null)
        {
            tb.Foreground = explicitBadgeFg;
        }

        UIElement badgeChild = tb;
        FrameworkElement? iconEl = null;
        string? icon = ResolveNormalizedIcon(spec.Icon, ctx);
        if (!string.IsNullOrWhiteSpace(icon) || ContainsDynamicToken(spec.Icon))
        {
            var innerGrid = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            iconEl = CreateInlineIconElement(icon ?? string.Empty, 10.5, tb.Foreground);
            iconEl.Visibility = string.IsNullOrWhiteSpace(icon) ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(iconEl, 0);
            Grid.SetColumn(tb, 1);
            innerGrid.Children.Add(iconEl);
            innerGrid.Children.Add(tb);
            badgeChild = innerGrid;
        }

        var badgeBorder = new Border
        {
            CornerRadius = isPill ? new CornerRadius(11) : CornerSmall,
            Padding = !string.IsNullOrWhiteSpace(spec.Padding)
                ? ParseThickness(spec.Padding)
                : (isPill ? new Thickness(7, 2, 8, 2) : new Thickness(6, 2, 6, 2)),
            Background = bg,
            BorderThickness = isPill ? new Thickness(1) : new Thickness(0),
            BorderBrush = isPill ? ResolveBadgePillBorderBrush(resolvedTone) : null,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeChild
        };
        ApplyContainerVisualOverrides(badgeBorder, spec, ctx);

        if (ContainsDynamicToken(rawText) || ContainsDynamicToken(rawToneExpr) || ContainsDynamicToken(spec.Icon))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                tb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawText, liveCtx);
                if (ContainsDynamicToken(rawToneExpr))
                {
                    string liveTone = CustomWidgetExpressionEvaluator.Interpolate(rawToneExpr, liveCtx);
                    (Brush liveBg, Brush liveFg) = ResolveBadgeColors(liveTone);
                    badgeBorder.Background = liveBg;
                    if (isPill)
                    {
                        badgeBorder.BorderBrush = ResolveBadgePillBorderBrush(liveTone);
                    }
                    if (string.IsNullOrWhiteSpace(spec.TextColor))
                    {
                        tb.Foreground = liveFg;
                        if (iconEl is FontIcon fi)
                        {
                            fi.Foreground = liveFg;
                        }
                        else if (iconEl is TextBlock iconTb)
                        {
                            iconTb.Foreground = liveFg;
                        }
                    }
                }

                if (iconEl is not null && ContainsDynamicToken(spec.Icon))
                {
                    string? nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx);
                    iconEl.Visibility = string.IsNullOrWhiteSpace(nextIcon) ? Visibility.Collapsed : Visibility.Visible;
                    if (!string.IsNullOrWhiteSpace(nextIcon))
                    {
                        if (iconEl is FontIcon fi && CustomWidgetTemplates.IsSegoeFluentGlyph(nextIcon))
                        {
                            fi.Glyph = NormalizeFluentGlyphForOs(nextIcon, WindowsCompatibilityService.IsWindows11OrLater);
                        }
                        else if (iconEl is TextBlock iconTb)
                        {
                            iconTb.Text = nextIcon;
                        }
                    }
                }
            });
        }

        return badgeBorder;
    }

    internal static bool IsSegmentedProgressBarVariant(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return false;
        }

        ReadOnlySpan<char> v = variant.AsSpan().Trim();
        return v.Equals("segmented", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("pills", StringComparison.OrdinalIgnoreCase) ||
               v.Equals("ticks", StringComparison.OrdinalIgnoreCase);
    }

    internal static (bool IsIndeterminate, double ClampedValue, double MinHeight) ResolveStandardProgressBarState(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double maxVal = Math.Max(1, spec.Max);
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        double pct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        string rawIndeterminate = spec.Indeterminate ?? string.Empty;
        bool isIndeterminate = !string.IsNullOrWhiteSpace(rawIndeterminate) &&
                               CustomWidgetExpressionEvaluator.EvaluateCondition(rawIndeterminate, ctx);
        double minHeight = spec.Height.HasValue && spec.Height.Value > 0
            ? Math.Clamp(spec.Height.Value, 2.0, 24.0)
            : 4.0;
        return (isIndeterminate, Math.Clamp(pct, 0, maxVal), minHeight);
    }

    private FrameworkElement BuildProgressBar(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        double pct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double? warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double? danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx);
        double maxVal = Math.Max(1, spec.Max);

        Brush ResolveBarActiveBrush(double curPct, double? curWarn, double? curDanger, CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(spec.Color))
            {
                string interpolatedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx);
                if (TryParseRawColor(interpolatedColor, out Color rawBarCol))
                {
                    return GetOrCreateSolidBrush(rawBarCol);
                }

                return ResolveThresholdBrush(curPct, curWarn, curDanger, interpolatedColor);
            }

            return ResolveThresholdBrush(curPct, curWarn, curDanger, spec.Color);
        }

        string resolvedVariant = CustomWidgetExpressionEvaluator.Interpolate(spec.Variant, ctx);
        Brush trackBrush = GetOrCreateSolidBrush(IsDark
            ? Color.FromArgb(26, 255, 255, 255)
            : Color.FromArgb(22, 15, 23, 42));

        if (IsSegmentedProgressBarVariant(resolvedVariant))
        {
            double pillHeight = spec.Height.HasValue && spec.Height.Value > 0
                ? Math.Clamp(spec.Height.Value, 6.0, 32.0)
                : 14.0;
            int explicitSegments = spec.TickStep > 0 ? Math.Clamp((int)Math.Round(spec.TickStep), 4, 80) : 0;

            var segmented = new CustomWidgetSegmentedBar
            {
                Minimum = 0,
                Maximum = maxVal,
                PillHeight = pillHeight,
                ExplicitSegments = explicitSegments,
                TrackBrush = trackBrush,
                ActiveBrush = ResolveBarActiveBrush(pct, warn, danger, ctx),
                Value = Math.Clamp(pct, 0, maxVal)
            };

            if (!string.IsNullOrWhiteSpace(spec.Items))
            {
                string? bindKey = !string.IsNullOrWhiteSpace(spec.Bind)
                    ? (spec.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? spec.Bind["state.".Length..]
                        : spec.Bind)
                    : null;
                Action<string?>? onHover = !string.IsNullOrWhiteSpace(bindKey)
                    ? hovered => _viewModel.SetStateValueWithoutRebuild(
                        bindKey!,
                        hovered ?? string.Empty,
                        isTransientInteractiveScrub: true,
                        notifyLiveBindings: true)
                    : null;

                void SyncBuckets(CustomWidgetEvaluationContext evalCtx)
                {
                    if (!TryResolveComputedRecords(spec.Items, evalCtx, out IReadOnlyList<CustomWidgetRecord> records) ||
                        records.Count == 0)
                    {
                        segmented.SetBuckets(null);
                        return;
                    }

                    var list = new List<(Brush Fill, bool IsActive, string? Tooltip, string? HoverSummary)>(records.Count);
                    for (int i = 0; i < records.Count; i++)
                    {
                        CustomWidgetRecord rec = records[i];
                        string tone = rec.GetField("tone");
                        if (string.IsNullOrWhiteSpace(tone))
                        {
                            tone = rec.GetField("color");
                        }

                        bool isActive = !string.IsNullOrWhiteSpace(tone) &&
                                        !string.Equals(tone, "subtle", StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(tone, "idle", StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(tone, "none", StringComparison.OrdinalIgnoreCase);

                        Brush fill = !isActive
                            ? trackBrush
                            : TryParseRawColor(tone, out Color rawCol)
                                ? GetOrCreateSolidBrush(rawCol)
                                : ResolveThresholdBrush(100, null, null, tone);

                        string tooltip = rec.GetField("tooltip");
                        string summary = rec.GetField("summary");
                        if (string.IsNullOrWhiteSpace(tooltip))
                        {
                            tooltip = !string.IsNullOrWhiteSpace(summary) ? summary : rec.GetField("title");
                        }
                        if (string.IsNullOrWhiteSpace(summary))
                        {
                            summary = tooltip;
                        }

                        list.Add((fill, isActive, tooltip, summary));
                    }

                    segmented.SetBuckets(list, onHover);
                }

                SyncBuckets(ctx);
                RegisterRetainedPatcher(SyncBuckets);
                return segmented;
            }

            if (ContainsDynamicToken(rawProgress) ||
                ContainsDynamicToken(spec.Color) ||
                ContainsDynamicToken(spec.WarningThresholdExpression) ||
                ContainsDynamicToken(spec.DangerThresholdExpression))
            {
                RegisterRetainedPatcher(liveCtx =>
                {
                    double livePct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
                    double? liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
                    double? liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx);
                    segmented.ActiveBrush = ResolveBarActiveBrush(livePct, liveWarn, liveDanger, liveCtx);
                    segmented.Value = Math.Clamp(livePct, 0, maxVal);
                });
            }

            return segmented;
        }

        string rawIndeterminate = spec.Indeterminate ?? string.Empty;
        (bool isIndeterminate, double clampedVal, double barMinHeight) = ResolveStandardProgressBarState(spec, ctx);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = maxVal,
            Value = clampedVal,
            IsIndeterminate = isIndeterminate,
            Foreground = ResolveBarActiveBrush(pct, warn, danger, ctx),
            Background = trackBrush,
            CornerRadius = new CornerRadius(3),
            MinWidth = 0,
            MinHeight = barMinHeight
        };

        if (ContainsDynamicToken(rawProgress) ||
            ContainsDynamicToken(rawIndeterminate) ||
            ContainsDynamicToken(spec.Color) ||
            ContainsDynamicToken(spec.WarningThresholdExpression) ||
            ContainsDynamicToken(spec.DangerThresholdExpression))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                double livePct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
                (bool liveIndeterminate, double liveClampedVal, _) = ResolveStandardProgressBarState(spec, liveCtx);
                double? liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
                double? liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx);
                bar.IsIndeterminate = liveIndeterminate;
                bar.Value = liveClampedVal;
                bar.Foreground = ResolveBarActiveBrush(livePct, liveWarn, liveDanger, liveCtx);
            });
        }

        return bar;
    }

    private FrameworkElement BuildWrap(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var wrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = Math.Max(0, ResolveEffectiveSpacing(spec, isHorizontal: true)),
            VerticalSpacing = Math.Max(0, ResolveEffectiveSpacing(spec, isHorizontal: false))
        };

        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            wrap.Children.Add(BuildNode(child, ctx));
        }

        Thickness pad = isRoot
            ? ResolveEffectiveRootSafePadding(spec.Padding)
            : ParseThickness(spec.Padding);
        FrameworkElement result = pad.Left > 0 || pad.Top > 0 || pad.Right > 0 || pad.Bottom > 0
            ? new Border
            {
                Padding = pad,
                Child = wrap
            }
            : wrap;
        result = ApplyContainerVisualOverrides(result, spec, ctx);

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(result)
            };
        }

        return result;
    }

    private static FrameworkElement BuildSpacer(CustomWidgetNodeSpec spec)
    {
        var spacer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (spec.Width is { } w && w > 0)
        {
            spacer.Width = w;
        }
        if (spec.Height is { } h && h > 0)
        {
            spacer.Height = h;
        }
        else if (spec.Size is { } s && s > 0)
        {
            spacer.Height = s;
            spacer.Width = s;
        }

        return spacer;
    }

    private FrameworkElement BuildToggle(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawChecked = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);
        bool isOn = string.Equals(rawChecked, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(rawChecked, "1", StringComparison.Ordinal);

        string label = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        string accessibleName = !string.IsNullOrWhiteSpace(label)
            ? label
            : (!string.IsNullOrWhiteSpace(bindKey) ? bindKey : "Toggle");

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            MinWidth = 0,
            OnContent = null,
            OffContent = null,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, accessibleName);
        MarkInteractiveElement(toggle);

        bool suppressPatchEvent = false;
        bool userEventInFlight = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        toggle.Toggled += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (suppressPatchEvent || userEventInFlight)
            {
                return;
            }

            userEventInFlight = true;
            try
            {
                string nextVal = toggle.IsOn ? "true" : "false";
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(nextVal);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: "$value")
                    ], evCtx);
                }

                IReadOnlyList<CustomWidgetActionSpec> followUp = spec.OnChange.Count > 0 ? spec.OnChange : spec.OnClick;
                if (followUp.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(followUp, evCtx);
                }
            }
            finally
            {
                userEventInFlight = false;
            }
        });

        TextBlock? labelBlock = null;
        if (!string.IsNullOrWhiteSpace(label))
        {
            labelBlock = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveForegroundBrush(spec.Color)
            };
            ApplyTypography(labelBlock, string.IsNullOrWhiteSpace(spec.Style) ? "body" : spec.Style);
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            string liveRaw = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            bool liveOn = string.Equals(liveRaw, "true", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(liveRaw, "1", StringComparison.Ordinal);
            if (!userEventInFlight && toggle.IsOn != liveOn)
            {
                suppressPatchEvent = true;
                try
                {
                    toggle.IsOn = liveOn;
                }
                finally
                {
                    suppressPatchEvent = false;
                }
            }
            if (labelBlock is not null && ContainsDynamicToken(spec.Title ?? spec.Text))
            {
                labelBlock.Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, liveCtx);
            }
        });

        if (labelBlock is null)
        {
            return toggle;
        }

        var row = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(toggle, 1);

        row.Children.Add(labelBlock);
        row.Children.Add(toggle);
        return row;
    }

    private FrameworkElement BuildProgressRing(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double size = Math.Clamp(spec.Size ?? spec.Width ?? spec.Height ?? 20, 12, 120);
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        string rawIndeterminate = spec.Indeterminate ?? string.Empty;

        bool isIndeterminate = !string.IsNullOrWhiteSpace(rawIndeterminate)
            ? CustomWidgetExpressionEvaluator.EvaluateCondition(rawIndeterminate, ctx)
            : (string.IsNullOrWhiteSpace(spec.Progress) && string.IsNullOrWhiteSpace(spec.Value));
        double pct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;

        var ring = new ProgressRing
        {
            Width = size,
            Height = size,
            Minimum = 0,
            Maximum = Math.Max(1, spec.Max),
            IsIndeterminate = isIndeterminate,
            IsActive = isIndeterminate || pct > 0,
            Value = Math.Clamp(pct, 0, Math.Max(1, spec.Max)),
            Foreground = ResolveAnyColorBrush(
                string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                    ? "accent"
                    : spec.Color,
                ctx,
                "accent"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (ContainsDynamicToken(rawProgress) || ContainsDynamicToken(rawIndeterminate) || ContainsDynamicToken(spec.Color))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                bool liveIndeterminate = !string.IsNullOrWhiteSpace(rawIndeterminate)
                    ? CustomWidgetExpressionEvaluator.EvaluateCondition(rawIndeterminate, liveCtx)
                    : isIndeterminate;
                double livePct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
                ring.IsIndeterminate = liveIndeterminate;
                ring.IsActive = liveIndeterminate || livePct > 0;
                ring.Value = Math.Clamp(livePct, 0, Math.Max(1, spec.Max));
                if (ContainsDynamicToken(spec.Color))
                {
                    ring.Foreground = ResolveAnyColorBrush(spec.Color, liveCtx, "accent");
                }
            });
        }

        return ring;
    }

    internal static string FormatStatValueWithUnit(string rawValue, string? unit)
    {
        string trimmedVal = rawValue?.Trim() ?? string.Empty;
        string trimmedUnit = unit?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedUnit) || string.IsNullOrEmpty(trimmedVal))
        {
            return trimmedVal;
        }

        if (trimmedVal.EndsWith(trimmedUnit, StringComparison.OrdinalIgnoreCase))
        {
            return trimmedVal;
        }

        return trimmedUnit.Length <= 2 && !char.IsLetter(trimmedUnit[0])
            ? $"{trimmedVal}{trimmedUnit}"
            : $"{trimmedVal} {trimmedUnit}";
    }

    private FrameworkElement BuildStatCard(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;
        string rawProgress = spec.Progress ?? rawValue;
        string baseTone = spec.Color;

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };

        string initialUnit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);
        var valueTb = new TextBlock
        {
            Text = FormatStatValueWithUnit(CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx), initialUnit),
            FontFamily = DisplayFontFamily,
            FontSize = MetricFontSize,
            FontWeight = FontWeights.SemiBold,
            TextLineBounds = TextLineBounds.Tight,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(valueTb);

        var subtitleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(subtitleTb);

        double initialPct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double? initialWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double? initialDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx);

        double sparklineHeight = Math.Round(22.0 * EffectiveAppearance.DensityMultiplier);
        var sparklineHost = new Grid
        {
            Height = sparklineHeight,
            Margin = new Thickness(0, 4, 0, 2),
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        var areaPolygon = new Polygon
        {
            Fill = CreateSparklineAreaBrush(initialPct, initialWarn, initialDanger, baseTone),
            Stretch = Stretch.None
        };
        var linePolyline = new Polyline
        {
            Stroke = ResolveThresholdBrush(initialPct, initialWarn, initialDanger, baseTone),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None
        };
        sparklineHost.Children.Add(areaPolygon);
        sparklineHost.Children.Add(linePolyline);

        void RenderSparklinePoints(IReadOnlyList<double> history, double currentPct, CustomWidgetEvaluationContext evalCtx)
        {
            double width = sparklineHost.ActualWidth > 10 ? sparklineHost.ActualWidth : 120;
            double height = Math.Max(14, sparklineHeight - 2);
            double? warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, evalCtx);
            double? danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, evalCtx);
            linePolyline.Stroke = ResolveThresholdBrush(currentPct, warn, danger, baseTone);
            areaPolygon.Fill = CreateSparklineAreaBrush(currentPct, warn, danger, baseTone);

            PopulateSparklineGeometry(history, currentPct, width, height, linePolyline, areaPolygon);
        }

        IReadOnlyList<double> initialHistory = CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, ctx);
        RenderSparklinePoints(initialHistory, initialPct, ctx);
        sparklineHost.SizeChanged += (_, _) =>
        {
            CustomWidgetEvaluationContext latestCtx = _viewModel.CreateEvaluationContext();
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, latestCtx) ?? 0;
            RenderSparklinePoints(
                CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, latestCtx),
                pctNow,
                latestCtx);
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            string liveUnit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, liveCtx);
            string nextFormattedVal = FormatStatValueWithUnit(CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx), liveUnit);
            if (!string.Equals(valueTb.Text, nextFormattedVal, StringComparison.Ordinal))
            {
                valueTb.Text = nextFormattedVal;
                if (spec.AnimateChange)
                {
                    TryAnimateTextChange(valueTb);
                }
            }
            subtitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
            RenderSparklinePoints(
                CustomWidgetExpressionEvaluator.ResolveHistory(spec.Sparkline, liveCtx),
                pctNow,
                liveCtx);
        });

        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(titleTb);
        stack.Children.Add(valueTb);
        stack.Children.Add(sparklineHost);
        stack.Children.Add(subtitleTb);

        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = ScaleDefaultThickness(10, 8, 10, 8, EffectiveAppearance.DensityMultiplier),
            Background = ResolveCardBackground("default"),
            BorderThickness = CardBorderThickness,
            BorderBrush = CardStrokeBrush,
            Child = stack
        };
    }

    private FrameworkElement BuildProgressRow(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;
        string rawProgress = spec.Progress ?? "0";

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary")
        };
        var valueTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default")
        };
        SetTabularNumerals(valueTb);
        Grid.SetColumn(titleTb, 0);
        Grid.SetColumn(valueTb, 1);
        headerGrid.Children.Add(titleTb);
        headerGrid.Children.Add(valueTb);

        double initialPct = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? 0;
        double? initialWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double? initialDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx);
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(initialPct, 0, 100),
            Foreground = ResolveThresholdBrush(initialPct, initialWarn, initialDanger, spec.Color),
            MinHeight = 4
        };

        var subtitleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("muted"),
            Visibility = string.IsNullOrWhiteSpace(rawSubtitle) ? Visibility.Collapsed : Visibility.Visible
        };
        SetTabularNumerals(subtitleTb);

        RegisterRetainedPatcher(liveCtx =>
        {
            titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            valueTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx);
            subtitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
            double pctNow = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? 0;
            double? liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
            double? liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx);
            progressBar.Value = Math.Clamp(pctNow, 0, 100);
            progressBar.Foreground = ResolveThresholdBrush(pctNow, liveWarn, liveDanger, spec.Color);
        });

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(progressBar);
        stack.Children.Add(subtitleTb);
        var rowHost = new Grid();
        rowHost.Children.Add(stack);
        return rowHost;
    }

    private FrameworkElement BuildKeyValueRow(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawTitle = spec.Title ?? string.Empty;
        string rawValue = spec.Value ?? string.Empty;
        bool hasValueSlot = !string.IsNullOrWhiteSpace(rawValue);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (hasValueSlot)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        bool isBodyStrong = string.Equals(spec.Style, "bodyStrong", StringComparison.OrdinalIgnoreCase);
        bool isSemiBold = isBodyStrong ||
            string.Equals(spec.FontWeight, "semiBold", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(spec.FontWeight, "bold", StringComparison.OrdinalIgnoreCase);
        string titleColorRole = !string.IsNullOrWhiteSpace(spec.Color) &&
                                !string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
            ? spec.Color
            : (isBodyStrong ? "default" : "secondary");

        string? resolvedRowIcon = ResolveNormalizedIcon(spec.Icon, ctx);
        bool hasDynamicRowIcon = ContainsDynamicToken(spec.Icon);
        FrameworkElement? rowIconEl = null;
        if (!string.IsNullOrWhiteSpace(resolvedRowIcon) || hasDynamicRowIcon)
        {
            rowIconEl = CreateInlineIconElement(
                string.IsNullOrWhiteSpace(resolvedRowIcon) ? "\uE71D" : resolvedRowIcon,
                13,
                ResolveForegroundBrush(isBodyStrong ? "secondary" : "muted"));
            rowIconEl.VerticalAlignment = VerticalAlignment.Center;
            rowIconEl.Visibility = string.IsNullOrWhiteSpace(resolvedRowIcon) ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(rowIconEl, 0);
            grid.Children.Add(rowIconEl);
        }

        var titleTb = new TextBlock
        {
            Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
            FontSize = isBodyStrong ? BodyFontSize : CaptionFontSize,
            FontWeight = isSemiBold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ResolveForegroundBrush(titleColorRole),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 1);
        grid.Children.Add(titleTb);

        TextBlock? valueTb = null;
        if (hasValueSlot)
        {
            valueTb = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, ctx),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("default"),
                VerticalAlignment = VerticalAlignment.Center
            };
            SetTabularNumerals(valueTb);
            Grid.SetColumn(valueTb, 2);
            grid.Children.Add(valueTb);
        }

        if (ContainsDynamicToken(rawValue) || ContainsDynamicToken(rawTitle) || hasDynamicRowIcon)
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
                if (valueTb is not null)
                {
                    valueTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawValue, liveCtx);
                }
                if (rowIconEl is not null && hasDynamicRowIcon)
                {
                    string? nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx);
                    rowIconEl.Visibility = string.IsNullOrWhiteSpace(nextIcon) ? Visibility.Collapsed : Visibility.Visible;
                    if (!string.IsNullOrWhiteSpace(nextIcon))
                    {
                        UpdateInlineIconGlyph(rowIconEl, nextIcon);
                    }
                }
            });
        }

        return grid;
    }

    private FrameworkElement BuildFileChip(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string fileName = CustomWidgetExpressionEvaluator.Interpolate(spec.FileName, ctx);
        string filePath = CustomWidgetExpressionEvaluator.Interpolate(spec.FilePath, ctx);
        string mode = CustomWidgetExpressionEvaluator.Interpolate(spec.StorageMode, ctx);

        if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(filePath))
        {
            fileName = System.IO.Path.GetFileName(filePath);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new Grid { Visibility = Visibility.Collapsed };
        }

        bool isCopy = string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new FontIcon
        {
            Glyph = isCopy ? "\uE723" : "\uE71B",
            FontSize = 11.5,
            Foreground = ResolveForegroundBrush("accent")
        });
        var chipTb = new TextBlock
        {
            Text = fileName,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(chipTb);

        var chipBtn = new Button
        {
            Content = row,
            CornerRadius = CornerSmall,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ResolveCardBackground("subtle"),
            BorderThickness = new Thickness(0)
        };

        string resolvedTooltipPath = _viewModel.Store.ResolveRuntimePath(filePath);
        ToolTipService.SetToolTip(chipBtn, string.IsNullOrWhiteSpace(resolvedTooltipPath) ? filePath : resolvedTooltipPath);

        chipBtn.Click += (_, _) => _viewModel.OpenFileWithDefaultApp(filePath);
        chipBtn.RightTapped += (_, e) =>
        {
            e.Handled = true;
            var flyout = new MenuFlyout();
            var openItem = new MenuFlyoutItem
            {
                Text = Tr("Context.Open"),
                Icon = new FontIcon { Glyph = "\uE8E5" }
            };
            openItem.Click += (_, _) => _viewModel.OpenFileWithDefaultApp(filePath);

            var revealItem = new MenuFlyoutItem
            {
                Text = Tr("Context.OpenFileLocation"),
                Icon = new FontIcon { Glyph = "\uEC50" }
            };
            revealItem.Click += (_, _) => _viewModel.RevealFileInExplorer(filePath);

            flyout.Items.Add(openItem);
            flyout.Items.Add(revealItem);
            ShowTrackedFlyoutAt(flyout, chipBtn);
        };

        return chipBtn;
    }

    private FrameworkElement BuildEmptyState(
        CustomWidgetEmptyStateSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var stack = new StackPanel
        {
            MaxWidth = 268,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        string emptyIcon = ResolveNormalizedIcon(spec.Icon, ctx) ?? "\uE70F";
        string resolvedTitle = !string.IsNullOrWhiteSpace(spec.Title)
            ? CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(resolvedTitle))
        {
            var titleRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            FrameworkElement emptyIconElement = CreateInlineIconElement(
                emptyIcon,
                14,
                ResolveForegroundBrush("secondary"));
            emptyIconElement.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(emptyIconElement);

            titleRow.Children.Add(new TextBlock
            {
                Text = resolvedTitle,
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary"),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(titleRow);
        }
        else
        {
            FrameworkElement emptyIconElement = CreateInlineIconElement(
                emptyIcon,
                18,
                ResolveForegroundBrush("muted"));
            emptyIconElement.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(emptyIconElement);
        }

        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            });
        }

        return new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = stack
        };
    }

    internal static (string Primary, string Secondary) ResolveDefaultListItemTexts(
        CustomWidgetNodeSpec listSpec,
        CustomWidgetRecord? record)
    {
        string primary = string.Empty;
        if (record is not null)
        {
            if (!string.IsNullOrWhiteSpace(listSpec.TitleField))
            {
                primary = record.GetField(listSpec.TitleField!);
            }

            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = record.GetField("text");
            }

            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = record.GetField("title");
            }
        }

        string secondary = record is not null && !string.IsNullOrWhiteSpace(listSpec.SubtitleField)
            ? record.GetField(listSpec.SubtitleField!)
            : string.Empty;
        return (primary, secondary);
    }

    private FrameworkElement BuildDefaultListItemBody(
        CustomWidgetNodeSpec listSpec,
        CustomWidgetEvaluationContext itemCtx)
    {
        (string primary, string secondary) = ResolveDefaultListItemTexts(listSpec, itemCtx.CurrentItem);

        var titleTb = new TextBlock
        {
            Text = primary,
            Foreground = ResolveForegroundBrush("default"),
            TextWrapping = TextWrapping.Wrap
        };
        if (string.IsNullOrWhiteSpace(secondary) && string.IsNullOrWhiteSpace(listSpec.SubtitleField))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                (string p, _) = ResolveDefaultListItemTexts(listSpec, liveCtx.CurrentItem);
                titleTb.Text = p;
            });
            return titleTb;
        }

        var secondaryTb = new TextBlock
        {
            Text = secondary,
            FontSize = 11.5,
            Foreground = ResolveForegroundBrush("secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            (string p, string s) = ResolveDefaultListItemTexts(listSpec, liveCtx.CurrentItem);
            titleTb.Text = p;
            secondaryTb.Text = s;
            secondaryTb.Visibility = string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
        });

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(titleTb);
        stack.Children.Add(secondaryTb);
        return stack;
    }
}

public sealed partial class CustomWidgetWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        bool firstInLine = true;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            if (desired.Width <= 0 && desired.Height <= 0)
            {
                continue;
            }

            double neededWidth = firstInLine ? desired.Width : lineWidth + HorizontalSpacing + desired.Width;
            if (!firstInLine && neededWidth > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = neededWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
                firstInLine = false;
            }
        }

        if (!firstInLine)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += (totalHeight > 0 ? VerticalSpacing : 0) + lineHeight;
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = Math.Max(0, finalSize.Width);
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        bool firstInLine = true;

        foreach (UIElement child in Children)
        {
            Size desired = child.DesiredSize;
            if (desired.Width <= 0 && desired.Height <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (!firstInLine && x + HorizontalSpacing + desired.Width > maxWidth)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
                firstInLine = true;
            }

            if (!firstInLine)
            {
                x += HorizontalSpacing;
            }

            double childWidth = Math.Min(desired.Width, Math.Max(0, maxWidth - x));
            child.Arrange(new Rect(x, y, childWidth, desired.Height));
            x += childWidth;
            lineHeight = Math.Max(lineHeight, desired.Height);
            firstInLine = false;
        }

        return finalSize;
    }
}
