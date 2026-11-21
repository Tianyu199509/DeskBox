using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
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
using Windows.Foundation;
using Windows.UI;
using WinUIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private readonly Dictionary<string, TeachingTip> _teachingTipsById = new(StringComparer.OrdinalIgnoreCase);

    private void ResetRichControlsState()
    {
        foreach (TeachingTip tip in _teachingTipsById.Values)
        {
            try { tip.IsOpen = false; } catch { }
        }
        _teachingTipsById.Clear();
    }

    private void ForgetTeachingTip(TeachingTip tip)
    {
        try { tip.IsOpen = false; tip.Target = null; } catch { }
        if (_teachingTipsById.Count == 0) return;

        List<string>? keysToRemove = null;
        foreach ((string key, TeachingTip registered) in _teachingTipsById)
        {
            if (ReferenceEquals(registered, tip))
            {
                (keysToRemove ??= []).Add(key);
            }
        }

        if (keysToRemove is not null)
        {
            foreach (string key in keysToRemove) _teachingTipsById.Remove(key);
        }
    }

    private void DetachTeachingTipTargets(FrameworkElement detachedElement)
    {
        if (_teachingTipsById.Count == 0) return;
        foreach (TeachingTip existingTip in _teachingTipsById.Values)
        {
            try
            {
                if (ReferenceEquals(existingTip.Target, detachedElement))
                {
                    existingTip.IsOpen = false;
                    existingTip.Target = null;
                }
            }
            catch { }
        }
    }

    internal void HandleTeachingTipCommand(string mode, string tipId)
    {
        if (string.IsNullOrWhiteSpace(tipId))
        {
            return;
        }

        string id = tipId.Trim();
        string normalizedMode = (mode ?? "toggle").Trim().ToLowerInvariant();
        if (string.Equals(normalizedMode, "close", StringComparison.Ordinal) &&
            (string.Equals(id, "*", StringComparison.Ordinal) || string.Equals(id, "all", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (TeachingTip t in _teachingTipsById.Values)
            {
                t.IsOpen = false;
            }
            return;
        }

        if (!_teachingTipsById.TryGetValue(id, out TeachingTip? tip))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(tip.Tag as string))
        {
            FrameworkElement? resolvedTarget = TryResolveElementById(tip.Tag as string);
            if (resolvedTarget is not null && !ReferenceEquals(tip.Target, resolvedTarget))
            {
                tip.Target = resolvedTarget;
            }
        }

        tip.IsOpen = normalizedMode switch
        {
            "open" => true,
            "close" => false,
            _ => !tip.IsOpen
        };
    }

    // =========================================================================
    // ZStack (Overlay Container) & ItemsGrid (Responsive Uniform Grid)
    // =========================================================================

    private FrameworkElement BuildZStack(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        var overlayGrid = new Grid
        {
            Padding = isRoot
                ? ResolveEffectiveRootSafePadding(spec.Padding)
                : ParseThickness(spec.Padding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        for (int i = 0; i < spec.Children.Count; i++)
        {
            UIElement child = BuildNode(spec.Children[i], ctx);
            Canvas.SetZIndex(child, i);
            overlayGrid.Children.Add(child);
        }

        FrameworkElement built = ApplyContainerVisualOverrides(overlayGrid, spec, ctx);
        return isRoot ? WrapRootContentInViewportScrollViewer(built, spec) : built;
    }

    internal static int ResolveItemsGridExplicitColumns(string? rawColumns, CustomWidgetEvaluationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(rawColumns))
        {
            return 0;
        }

        string resolved = CustomWidgetExpressionEvaluator.Interpolate(rawColumns, ctx).Trim();
        if (resolved.Length == 0 || resolved.Contains("${", StringComparison.Ordinal))
        {
            return 0;
        }

        if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intCols) &&
            intCols >= 1)
        {
            return Math.Clamp(intCols, 1, 16);
        }

        if (double.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblCols) &&
            double.IsFinite(dblCols) &&
            dblCols >= 1.0 &&
            Math.Abs(dblCols - Math.Round(dblCols)) < 0.001)
        {
            return Math.Clamp((int)Math.Round(dblCols), 1, 16);
        }

        return 0;
    }

    private FrameworkElement BuildItemsGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        int explicitCols = ResolveItemsGridExplicitColumns(spec.Columns, ctx);

        var gridPanel = new CustomWidgetUniformGridPanel
        {
            Columns = explicitCols,
            MinItemWidth = Math.Clamp(spec.MinItemWidth is > 0 ? spec.MinItemWidth.Value : 108.0, 36.0, 420.0),
            Spacing = Math.Max(0, spec.Spacing)
        };

        bool hasDynamicCols = spec.Columns?.Contains("${", StringComparison.Ordinal) == true;
        FrameworkElement resultElement;
        bool hasDataSource = !string.IsNullOrWhiteSpace(spec.Items) || !string.IsNullOrWhiteSpace(spec.Collection);
        if (!hasDataSource)
        {
            gridPanel.ChildrenTransitions = CreateRepositionTransitions();
            int savedParentGridCols = _parentGridColCount;
            _parentGridColCount = explicitCols;
            try
            {
                foreach (CustomWidgetNodeSpec child in spec.Children)
                {
                    gridPanel.Children.Add(BuildNode(child, ctx));
                }
            }
            finally
            {
                _parentGridColCount = savedParentGridCols;
            }

            Thickness pad = ParseThickness(spec.Padding);
            FrameworkElement host = pad.Left > 0 || pad.Top > 0 || pad.Right > 0 || pad.Bottom > 0
                ? new Border { Padding = pad, Child = gridPanel }
                : gridPanel;
            resultElement = ApplyContainerVisualOverrides(host, spec, ctx);
        }
        else
        {
            gridPanel.ChildrenTransitions = CreateRepositionTransitions();

            bool wrapScroll = spec.MaxLines <= 0 &&
                              (spec.MaxItems <= 0 || spec.Height.HasValue || spec.MaxHeight.HasValue);
            resultElement = BuildRetainedCollectionView(
                spec,
                ctx,
                gridPanel,
                wrapInScrollViewer: wrapScroll,
                defaultEmptySpec: new CustomWidgetEmptyStateSpec(
                    "\uF0E2",
                    Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    string.Empty),
                itemBuilder: (itemCtx, colName) =>
                {
                    int savedParentGridCols = _parentGridColCount;
                    _parentGridColCount = explicitCols;
                    try
                    {
                        return spec.ItemTemplate is not null
                            ? BuildNode(spec.ItemTemplate, itemCtx)
                            : BuildListItemCard(spec, itemCtx, colName);
                    }
                    finally
                    {
                        _parentGridColCount = savedParentGridCols;
                    }
                });
        }

        if (hasDynamicCols)
        {
            RegisterRetainedPatcher(evalCtx =>
            {
                int nextCols = ResolveItemsGridExplicitColumns(spec.Columns, evalCtx);
                if (gridPanel.Columns != nextCols)
                {
                    gridPanel.Columns = nextCols;
                    gridPanel.InvalidateMeasure();
                }
            });
        }

        return resultElement;
    }

    // =========================================================================
    // SelectorBar, FlipView (+ PipsPager), SplitView
    // =========================================================================

    private FrameworkElement BuildSelectorBar(
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

        string initialVal = ResolveSelectedValue(ctx);
        var selectorBar = new SelectorBar
        {
            HorizontalAlignment = HorizontalAlignment.Left
        };
        MarkInteractiveElement(selectorBar);

        var itemsByValue = new List<(string Value, SelectorBarItem Item, string RawLabel, string? RawIcon)>(spec.Options.Count);
        bool hasDynamicOptionContent = false;
        SelectorBarItem? initialItem = null;
        foreach (CustomWidgetOptionSpec opt in spec.Options)
        {
            if (ContainsDynamicToken(opt.Label) || ContainsDynamicToken(opt.Icon))
            {
                hasDynamicOptionContent = true;
            }

            var barItem = new SelectorBarItem
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(opt.Label, ctx),
                Tag = opt.Value,
                MinHeight = 28,
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = CaptionFontSize
            };
            string? iconGlyph = ResolveNormalizedIcon(opt.Icon, ctx);
            if (!string.IsNullOrWhiteSpace(iconGlyph) && CustomWidgetTemplates.IsSegoeFluentGlyph(iconGlyph))
            {
                barItem.Icon = new FontIcon
                {
                    Glyph = NormalizeFluentGlyphForOs(iconGlyph, WindowsCompatibilityService.IsWindows11OrLater),
                    FontSize = 13
                };
            }

            if (string.Equals(opt.Value, initialVal, StringComparison.OrdinalIgnoreCase))
            {
                initialItem = barItem;
            }
            selectorBar.Items.Add(barItem);
            itemsByValue.Add((opt.Value, barItem, opt.Label, opt.Icon));
        }

        bool suppressPatchSelection = true;
        bool userSelectionInFlight = false;
        if (initialItem is not null)
        {
            selectorBar.SelectedItem = initialItem;
        }
        else if (selectorBar.Items.Count > 0)
        {
            selectorBar.SelectedItem = selectorBar.Items[0];
        }
        suppressPatchSelection = false;

        selectorBar.SelectionChanged += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (suppressPatchSelection || userSelectionInFlight || _isNodeSelectionEnabled || selectorBar.SelectedItem is not SelectorBarItem selected)
            {
                return;
            }

            userSelectionInFlight = true;
            try
            {
                string nextVal = selected.Tag as string ?? string.Empty;
                CustomWidgetEvaluationContext evCtx = _viewModel.CreateEvaluationContext().WithEventValue(nextVal);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    string actionType = ResolveBindSetActionType(bindKey);
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec(actionType, Key: bindKey, Value: "$value")
                    ], evCtx);
                }
                if (spec.OnChange.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
                }
            }
            finally
            {
                userSelectionInFlight = false;
            }
        });

        if (hasDynamicOptionContent || !string.IsNullOrWhiteSpace(bindKey) || ContainsDynamicToken(spec.Value))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                foreach ((string _, SelectorBarItem item, string rawLabel, string? rawIcon) in itemsByValue)
                {
                    if (ContainsDynamicToken(rawLabel))
                    {
                        string nextText = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                        if (!string.Equals(item.Text, nextText, StringComparison.Ordinal))
                        {
                            item.Text = nextText;
                        }
                    }

                    if (ContainsDynamicToken(rawIcon))
                    {
                        string? nextGlyph = ResolveNormalizedIcon(rawIcon, liveCtx);
                        if (!string.IsNullOrWhiteSpace(nextGlyph) && CustomWidgetTemplates.IsSegoeFluentGlyph(nextGlyph))
                        {
                            string normalized = NormalizeFluentGlyphForOs(nextGlyph, WindowsCompatibilityService.IsWindows11OrLater);
                            if (item.Icon is FontIcon fi)
                            {
                                if (!string.Equals(fi.Glyph, normalized, StringComparison.Ordinal))
                                {
                                    fi.Glyph = normalized;
                                }
                            }
                            else
                            {
                                item.Icon = new FontIcon
                                {
                                    Glyph = normalized,
                                    FontSize = 13
                                };
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(bindKey) || ContainsDynamicToken(spec.Value))
                {
                    string liveVal = ResolveSelectedValue(liveCtx);
                    foreach ((string val, SelectorBarItem item, string _, string? _) in itemsByValue)
                    {
                        if (string.Equals(val, liveVal, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!userSelectionInFlight && !ReferenceEquals(selectorBar.SelectedItem, item))
                            {
                                suppressPatchSelection = true;
                                try
                                {
                                    selectorBar.SelectedItem = item;
                                }
                                finally
                                {
                                    suppressPatchSelection = false;
                                }
                            }
                            break;
                        }
                    }
                }
            });
        }

        bool stretchSelectorBar = string.Equals(spec.HorizontalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        ScrollViewer selectorScroll = WrapInHorizontalStripScrollViewer(selectorBar, stretchSelectorBar);
        return ApplyContainerVisualOverrides(selectorScroll, spec, ctx);
    }

    private FrameworkElement BuildFlipView(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var flipView = new FlipView
        {
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            MinHeight = spec.MinHeight ?? 96,
            Height = spec.Height ?? double.NaN,
            CornerRadius = new CornerRadius(Math.Max(0, spec.CornerRadius ?? RadiusMedium))
        };

        var slideValues = new List<string>();
        var slideItemPatchers = new List<(int RecordIndex, CustomWidgetRecord Record, List<Action<CustomWidgetEvaluationContext>> Patchers)>();
        bool hasDataSource = !string.IsNullOrWhiteSpace(spec.Items) || !string.IsNullOrWhiteSpace(spec.Collection);
        int limit = spec.MaxItems > 0 ? spec.MaxItems : 24;
        string lastSlideSignature = "\0";

        string BuildDataSourceSignature(IReadOnlyList<CustomWidgetRecord> records, string colName)
        {
            var sb = new System.Text.StringBuilder(colName.Length + 32);
            sb.Append(colName).Append('|');
            int added = 0;
            for (int i = 0; i < records.Count && added < limit; i++)
            {
                CustomWidgetRecord rec = records[i];
                if (rec.IsDeleted) continue;
                sb.Append(!string.IsNullOrWhiteSpace(rec.Id) ? rec.Id : i.ToString(CultureInfo.InvariantCulture))
                  .Append(':')
                  .Append(rec.EffectiveUpdatedAtUtcTicks)
                  .Append(';');
                added++;
            }
            return sb.ToString();
        }

        void RebuildDataSlides(CustomWidgetEvaluationContext evalCtx)
        {
            foreach (object? oldItem in flipView.Items)
            {
                if (oldItem is DependencyObject oldDep)
                {
                    ForgetRetainedSubtree(oldDep);
                }
            }
            flipView.Items.Clear();
            slideValues.Clear();
            slideItemPatchers.Clear();

            IReadOnlyList<CustomWidgetRecord> records = ResolveNodeCollectionRecords(spec, evalCtx, out string colName);
            lastSlideSignature = BuildDataSourceSignature(records, colName);
            int added = 0;
            for (int i = 0; i < records.Count && added < limit; i++)
            {
                CustomWidgetRecord rec = records[i];
                if (rec.IsDeleted) continue;
                CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(rec, i);
                UIElement slide = null!;
                List<Action<CustomWidgetEvaluationContext>> itemPatchers = CaptureScopedPatchers(() =>
                {
                    slide = spec.ItemTemplate is not null
                        ? BuildNode(spec.ItemTemplate, itemCtx)
                        : BuildListItemCard(spec, itemCtx, colName);
                });
                flipView.Items.Add(slide);
                slideValues.Add(!string.IsNullOrWhiteSpace(rec.Id) ? rec.Id : i.ToString(CultureInfo.InvariantCulture));
                slideItemPatchers.Add((i, rec, itemPatchers));
                added++;
            }
        }

        if (hasDataSource)
        {
            RebuildDataSlides(ctx);
        }
        else
        {
            for (int i = 0; i < spec.Children.Count; i++)
            {
                CustomWidgetNodeSpec child = spec.Children[i];
                flipView.Items.Add(BuildNode(child, ctx));
                slideValues.Add(!string.IsNullOrWhiteSpace(child.Id) ? child.Id! : i.ToString(CultureInfo.InvariantCulture));
            }

            if (flipView.Items.Count == 0)
            {
                return BuildEmptyState(
                    spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                        "\uEB9F",
                        Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                        string.Empty),
                    ctx);
            }
        }

        string bindKey = spec.Bind ?? string.Empty;
        int ResolveBoundIndex(CustomWidgetEvaluationContext evalCtx)
        {
            if (flipView.Items.Count == 0 || string.IsNullOrWhiteSpace(bindKey))
            {
                return 0;
            }
            string raw = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx).Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                idx >= 0 && idx < flipView.Items.Count)
            {
                return idx;
            }
            int found = slideValues.FindIndex(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
            return found >= 0 ? found : 0;
        }

        bool syncing = true;
        if (flipView.Items.Count > 0)
        {
            flipView.SelectedIndex = ResolveBoundIndex(ctx);
        }
        syncing = false;

        PipsPager? pager = null;
        if (spec.ShowPager && (hasDataSource || flipView.Items.Count > 1))
        {
            pager = new PipsPager
            {
                NumberOfPages = Math.Max(1, flipView.Items.Count),
                SelectedPageIndex = Math.Max(0, flipView.SelectedIndex),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = flipView.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed
            };
            pager.SelectedIndexChanged += (_, _) =>
            {
                int newIdx = pager.SelectedPageIndex;
                if (!syncing && newIdx >= 0 && newIdx < flipView.Items.Count &&
                    flipView.SelectedIndex != newIdx)
                {
                    flipView.SelectedIndex = newIdx;
                }
            };
        }

        flipView.SelectionChanged += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (syncing || _isNodeSelectionEnabled || flipView.SelectedIndex < 0 || flipView.SelectedIndex >= slideValues.Count)
            {
                return;
            }

            syncing = true;
            try
            {
                if (pager is not null)
                {
                    pager.SelectedPageIndex = flipView.SelectedIndex;
                }

                string chosen = slideValues[flipView.SelectedIndex];
                CustomWidgetEvaluationContext evCtx = _viewModel.CreateEvaluationContext().WithEventValue(chosen);
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
            }
            finally
            {
                syncing = false;
            }
        });

        FrameworkElement activeContent = pager is null
            ? flipView
            : new StackPanel { Spacing = 4, Children = { flipView, pager } };

        FrameworkElement? emptyElement = null;
        FrameworkElement hostElement = activeContent;
        if (hasDataSource)
        {
            emptyElement = BuildEmptyState(
                spec.EmptyState ?? new CustomWidgetEmptyStateSpec(
                    "\uEB9F",
                    Tr("CustomWidget.Renderer.EmptyList", ctx.CultureName),
                    string.Empty),
                ctx);
            bool hasInitialSlides = flipView.Items.Count > 0;
            emptyElement.Visibility = hasInitialSlides ? Visibility.Collapsed : Visibility.Visible;
            activeContent.Visibility = hasInitialSlides ? Visibility.Visible : Visibility.Collapsed;

            var hostGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            hostGrid.Children.Add(emptyElement);
            hostGrid.Children.Add(activeContent);
            hostElement = hostGrid;
        }

        if (hasDataSource || !string.IsNullOrWhiteSpace(bindKey))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (hasDataSource)
                {
                    IReadOnlyList<CustomWidgetRecord> liveRecords = ResolveNodeCollectionRecords(spec, liveCtx, out string liveColName);
                    string nextSig = BuildDataSourceSignature(liveRecords, liveColName);
                    if (!string.Equals(nextSig, lastSlideSignature, StringComparison.Ordinal))
                    {
                        int prevSelected = Math.Max(0, flipView.SelectedIndex);
                        syncing = true;
                        try
                        {
                            RebuildDataSlides(liveCtx);
                            bool hasSlides = flipView.Items.Count > 0;
                            if (emptyElement is not null)
                            {
                                emptyElement.Visibility = hasSlides ? Visibility.Collapsed : Visibility.Visible;
                            }
                            activeContent.Visibility = hasSlides ? Visibility.Visible : Visibility.Collapsed;
                            if (hasSlides)
                            {
                                int targetIdx = !string.IsNullOrWhiteSpace(bindKey)
                                    ? ResolveBoundIndex(liveCtx)
                                    : Math.Min(prevSelected, flipView.Items.Count - 1);
                                flipView.SelectedIndex = targetIdx;
                            }
                            if (pager is not null)
                            {
                                pager.NumberOfPages = Math.Max(1, flipView.Items.Count);
                                pager.SelectedPageIndex = Math.Max(0, flipView.SelectedIndex);
                                pager.Visibility = flipView.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                            }
                        }
                        finally
                        {
                            syncing = false;
                        }
                        return;
                    }

                    foreach ((int recIdx, CustomWidgetRecord rec, List<Action<CustomWidgetEvaluationContext>> patchers) in slideItemPatchers)
                    {
                        if (patchers.Count > 0)
                        {
                            CustomWidgetEvaluationContext itemLiveCtx = liveCtx.WithItem(rec, recIdx);
                            foreach (Action<CustomWidgetEvaluationContext> p in patchers)
                            {
                                p(itemLiveCtx);
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(bindKey) && flipView.Items.Count > 0)
                {
                    int nextIdx = ResolveBoundIndex(liveCtx);
                    if (flipView.SelectedIndex != nextIdx && nextIdx >= 0 && nextIdx < flipView.Items.Count)
                    {
                        syncing = true;
                        try
                        {
                            flipView.SelectedIndex = nextIdx;
                            if (pager is not null)
                            {
                                pager.SelectedPageIndex = nextIdx;
                            }
                        }
                        finally
                        {
                            syncing = false;
                        }
                    }
                }
            });
        }

        return ApplyContainerVisualOverrides(hostElement, spec, ctx);
    }

    private FrameworkElement BuildSplitView(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        string bindKey = spec.Bind ?? string.Empty;
        bool ResolvePaneOpen(CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string raw = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return CustomWidgetExpressionEvaluator.IsTruthy(raw);
                }
            }
            if (!string.IsNullOrWhiteSpace(spec.IsOpen))
            {
                return CustomWidgetExpressionEvaluator.EvaluateCondition(spec.IsOpen, evalCtx);
            }
            return spec.DefaultExpanded;
        }

        SplitViewDisplayMode displayMode = (spec.DisplayMode ?? "compactInline").Trim().ToLowerInvariant() switch
        {
            "inline" => SplitViewDisplayMode.Inline,
            "overlay" => SplitViewDisplayMode.Overlay,
            "compactoverlay" => SplitViewDisplayMode.CompactOverlay,
            _ => SplitViewDisplayMode.CompactInline
        };

        SplitViewPanePlacement placement = string.Equals(spec.PanePlacement, "right", StringComparison.OrdinalIgnoreCase)
            ? SplitViewPanePlacement.Right
            : SplitViewPanePlacement.Left;

        UIElement? paneElement = spec.Pane is not null ? BuildNode(spec.Pane, ctx) : null;
        int contentStart = 0;
        if (paneElement is null && spec.Children.Count >= 2)
        {
            paneElement = BuildNode(spec.Children[0], ctx);
            contentStart = 1;
        }

        UIElement bodyElement;
        if (spec.Children.Count - contentStart == 1)
        {
            bodyElement = BuildNode(spec.Children[contentStart], ctx);
        }
        else
        {
            var contentStack = new StackPanel
            {
                Spacing = ResolveEffectiveSpacing(spec, isHorizontal: false),
                Padding = isRoot
                    ? ResolveEffectiveRootSafePadding(spec.Padding)
                    : ParseThickness(spec.Padding)
            };
            for (int i = contentStart; i < spec.Children.Count; i++)
            {
                contentStack.Children.Add(BuildNode(spec.Children[i], ctx));
            }
            bodyElement = contentStack;
        }

        var splitView = new SplitView
        {
            DisplayMode = displayMode,
            PanePlacement = placement,
            OpenPaneLength = Math.Clamp(spec.PaneWidth > 0 ? spec.PaneWidth : 176.0, 72.0, 360.0),
            CompactPaneLength = Math.Clamp(spec.CompactPaneWidth > 0 ? spec.CompactPaneWidth : 42.0, 28.0, 96.0),
            IsPaneOpen = ResolvePaneOpen(ctx),
            PaneBackground = ResolveCardBackground("subtle"),
            Pane = paneElement,
            Content = bodyElement
        };

        bool syncing = false;
        splitView.PaneClosed += (_, _) =>
        {
            if (!syncing && !string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, "false");
            }
        };
        splitView.PaneOpened += (_, _) =>
        {
            if (!syncing && !string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, "true");
            }
        };

        if (!string.IsNullOrWhiteSpace(bindKey) || !string.IsNullOrWhiteSpace(spec.IsOpen))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                bool nextOpen = ResolvePaneOpen(liveCtx);
                if (splitView.IsPaneOpen != nextOpen)
                {
                    syncing = true;
                    try
                    {
                        splitView.IsPaneOpen = nextOpen;
                    }
                    finally
                    {
                        syncing = false;
                    }
                }
            });
        }

        FrameworkElement built = ApplyContainerVisualOverrides(splitView, spec, ctx);
        return isRoot ? WrapRootContentInViewportScrollViewer(built, spec) : built;
    }

    // =========================================================================
    // SplitButton, DropDownButton & Universal ContextMenu / MenuFlyout
    // =========================================================================

    internal static bool HasDynamicMenuTokens(IReadOnlyList<CustomWidgetMenuItemSpec> specs)
    {
        foreach (CustomWidgetMenuItemSpec itemSpec in specs)
        {
            if (ContainsDynamicToken(itemSpec.Label) ||
                ContainsDynamicToken(itemSpec.Icon) ||
                (itemSpec.Children is { Count: > 0 } && HasDynamicMenuTokens(itemSpec.Children)))
            {
                return true;
            }
        }

        return false;
    }

    private MenuFlyout BuildDeclarativeMenuFlyout(
        IReadOnlyList<CustomWidgetMenuItemSpec> items,
        CustomWidgetEvaluationContext ctx)
    {
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };
        TrackInteractiveFlyout(flyout);

        void PopulateItems(IList<MenuFlyoutItemBase> targetList, IReadOnlyList<CustomWidgetMenuItemSpec> specs, CustomWidgetEvaluationContext evalCtx)
        {
            foreach (CustomWidgetMenuItemSpec itemSpec in specs)
            {
                if (itemSpec.IsSeparator ||
                    string.Equals(itemSpec.Label, "-", StringComparison.Ordinal) ||
                    string.Equals(itemSpec.Label, "---", StringComparison.Ordinal))
                {
                    targetList.Add(new MenuFlyoutSeparator());
                    continue;
                }

                string label = CustomWidgetExpressionEvaluator.Interpolate(itemSpec.Label, evalCtx);
                string? glyph = ResolveNormalizedIcon(itemSpec.Icon, evalCtx);
                IconElement? iconEl = !string.IsNullOrWhiteSpace(glyph) && CustomWidgetTemplates.IsSegoeFluentGlyph(glyph)
                    ? new FontIcon { Glyph = NormalizeFluentGlyphForOs(glyph, WindowsCompatibilityService.IsWindows11OrLater) }
                    : null;

                if (itemSpec.Children is { Count: > 0 })
                {
                    var subMenu = new MenuFlyoutSubItem
                    {
                        Text = label,
                        Icon = iconEl
                    };
                    PopulateItems(subMenu.Items, itemSpec.Children, evalCtx);
                    targetList.Add(subMenu);
                    continue;
                }

                var menuItem = new MenuFlyoutItem
                {
                    Text = label,
                    Icon = iconEl
                };
                if (itemSpec.OnClick is { Count: > 0 })
                {
                    IReadOnlyList<CustomWidgetActionSpec> actions = itemSpec.OnClick;
                    string? itemVal = itemSpec.Value;
                    menuItem.Click += (_, _) => FireAndForgetSafeUi(async () =>
                    {
                        CustomWidgetEvaluationContext clickCtx = _viewModel.CreateEvaluationContext();
                        if (evalCtx.CurrentItem is not null)
                        {
                            clickCtx = clickCtx.WithItem(evalCtx.CurrentItem, evalCtx.CurrentIndex);
                        }
                        if (!string.IsNullOrWhiteSpace(itemVal))
                        {
                            clickCtx = clickCtx.WithEventValue(CustomWidgetExpressionEvaluator.Interpolate(itemVal, clickCtx));
                        }
                        await _viewModel.ExecuteActionsAsync(actions, clickCtx);
                    });
                }
                targetList.Add(menuItem);
            }
        }

        PopulateItems(flyout.Items, items, ctx);
        if (HasDynamicMenuTokens(items))
        {
            flyout.Opening += (_, _) =>
            {
                CustomWidgetEvaluationContext liveCtx = _viewModel.CreateEvaluationContext();
                if (ctx.CurrentItem is not null)
                {
                    liveCtx = liveCtx.WithItem(ctx.CurrentItem, ctx.CurrentIndex);
                }

                flyout.Items.Clear();
                PopulateItems(flyout.Items, items, liveCtx);
            };
        }

        return flyout;
    }

    private void AttachNodeContextMenu(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (spec.ContextMenu.Count == 0 || _isNodeSelectionEnabled)
        {
            return;
        }

        MarkInteractiveElement(element);
        element.RightTapped += (_, e) =>
        {
            if (_isNodeSelectionEnabled || e.Handled)
            {
                return;
            }

            e.Handled = true;
            CustomWidgetEvaluationContext liveCtx = _viewModel.CreateEvaluationContext();
            if (ctx.CurrentItem is not null)
            {
                liveCtx = liveCtx.WithItem(ctx.CurrentItem, ctx.CurrentIndex);
            }
            MenuFlyout flyout = BuildDeclarativeMenuFlyout(spec.ContextMenu, liveCtx);
            ShowTrackedFlyoutAt(flyout, element);
        };
    }

    private UIElement BuildCommandButtonContent(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        out TextBlock? liveLabelTb,
        out FrameworkElement? liveIconEl)
    {
        string rawLabel = spec.Text ?? spec.Title ?? string.Empty;
        string label = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, ctx);
        string? icon = ResolveNormalizedIcon(spec.Icon, ctx);
        bool hasDynamicIcon = ContainsDynamicToken(spec.Icon);
        bool mayHaveIcon = !string.IsNullOrWhiteSpace(icon) || hasDynamicIcon;
        bool stretchContent = string.Equals(spec.HorizontalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        liveLabelTb = null;
        liveIconEl = null;

        if (mayHaveIcon && (!string.IsNullOrWhiteSpace(label) || ContainsDynamicToken(rawLabel)))
        {
            var row = new Grid
            {
                ColumnSpacing = 6,
                HorizontalAlignment = stretchContent ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FrameworkElement iconEl = CreateInlineIconElement(string.IsNullOrWhiteSpace(icon) ? "\uE710" : icon, 13);
            iconEl.Visibility = string.IsNullOrWhiteSpace(icon) ? Visibility.Collapsed : Visibility.Visible;
            liveIconEl = iconEl;
            Grid.SetColumn(iconEl, 0);
            row.Children.Add(iconEl);

            var tb = new TextBlock
            {
                Text = label,
                FontSize = CaptionFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            liveLabelTb = tb;
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            return row;
        }

        if (mayHaveIcon)
        {
            FrameworkElement iconEl = CreateInlineIconElement(string.IsNullOrWhiteSpace(icon) ? "\uE710" : icon, 13);
            iconEl.Visibility = string.IsNullOrWhiteSpace(icon) ? Visibility.Collapsed : Visibility.Visible;
            liveIconEl = iconEl;
            return iconEl;
        }

        var onlyTb = new TextBlock
        {
            Text = label,
            FontSize = CaptionFontSize,
            HorizontalAlignment = stretchContent ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        liveLabelTb = onlyTb;
        return onlyTb;
    }

    private FrameworkElement BuildSplitButton(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawLabel = spec.Text ?? spec.Title ?? string.Empty;
        UIElement content = BuildCommandButtonContent(spec, ctx, out TextBlock? labelTb, out FrameworkElement? iconEl);
        var splitBtn = new SplitButton
        {
            Content = content,
            CornerRadius = CornerSmall,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (spec.MenuItems.Count > 0)
        {
            splitBtn.Flyout = BuildDeclarativeMenuFlyout(spec.MenuItems, ctx);
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(splitBtn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        if (spec.OnClick.Count > 0)
        {
            splitBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                CustomWidgetEvaluationContext clickCtx = _viewModel.CreateEvaluationContext();
                if (ctx.CurrentItem is not null)
                {
                    clickCtx = clickCtx.WithItem(ctx.CurrentItem, ctx.CurrentIndex);
                }
                await _viewModel.ExecuteActionsAsync(spec.OnClick, clickCtx);
            });
        }

        bool hasDynamicLabel = labelTb is not null && !string.IsNullOrWhiteSpace(rawLabel) && rawLabel.Contains('$');
        bool hasDynamicIcon = iconEl is not null && ContainsDynamicToken(spec.Icon);
        if (hasDynamicLabel || hasDynamicIcon)
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (hasDynamicLabel)
                {
                    labelTb!.Text = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                }
                if (hasDynamicIcon)
                {
                    string? nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx);
                    iconEl!.Visibility = string.IsNullOrWhiteSpace(nextIcon) ? Visibility.Collapsed : Visibility.Visible;
                    if (!string.IsNullOrWhiteSpace(nextIcon))
                    {
                        UpdateInlineIconGlyph(iconEl, nextIcon);
                    }
                }
            });
        }

        return splitBtn;
    }

    private FrameworkElement BuildDropDownButton(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawLabel = spec.Text ?? spec.Title ?? string.Empty;
        UIElement content = BuildCommandButtonContent(spec, ctx, out TextBlock? labelTb, out FrameworkElement? iconEl);
        bool stretch = string.Equals(spec.HorizontalAlignment, "stretch", StringComparison.OrdinalIgnoreCase);
        var dropBtn = new DropDownButton
        {
            Content = content,
            CornerRadius = CornerSmall,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (stretch)
        {
            dropBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
            dropBtn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }

        if (spec.MenuItems.Count > 0)
        {
            dropBtn.Flyout = BuildDeclarativeMenuFlyout(spec.MenuItems, ctx);
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(dropBtn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        bool hasDynamicLabel = labelTb is not null && !string.IsNullOrWhiteSpace(rawLabel) && rawLabel.Contains('$');
        bool hasDynamicIcon = iconEl is not null && ContainsDynamicToken(spec.Icon);
        if (hasDynamicLabel || hasDynamicIcon)
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (hasDynamicLabel)
                {
                    labelTb!.Text = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                }
                if (hasDynamicIcon)
                {
                    string? nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx);
                    iconEl!.Visibility = string.IsNullOrWhiteSpace(nextIcon) ? Visibility.Collapsed : Visibility.Visible;
                    if (!string.IsNullOrWhiteSpace(nextIcon))
                    {
                        UpdateInlineIconGlyph(iconEl, nextIcon);
                    }
                }
            });
        }

        return dropBtn;
    }

    // =========================================================================
    // InfoBadge, InfoBar & TeachingTip
    // =========================================================================

    private FrameworkElement BuildInfoBadge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawVal = spec.Value ?? spec.Text ?? string.Empty;
        var badge = new InfoBadge
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        void ApplyBadgeState(CustomWidgetEvaluationContext evalCtx)
        {
            string resolved = CustomWidgetExpressionEvaluator.Interpolate(rawVal, evalCtx).Trim();
            string? iconGlyph = ResolveNormalizedIcon(spec.Icon, evalCtx);
            if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numVal))
            {
                badge.Value = Math.Max(0, numVal);
                badge.IconSource = null;
            }
            else if (!string.IsNullOrWhiteSpace(iconGlyph) && CustomWidgetTemplates.IsSegoeFluentGlyph(iconGlyph))
            {
                badge.Value = -1;
                badge.IconSource = new FontIconSource
                {
                    Glyph = NormalizeFluentGlyphForOs(iconGlyph, WindowsCompatibilityService.IsWindows11OrLater)
                };
            }
            else
            {
                badge.Value = -1;
                badge.IconSource = null;
            }

            ReadOnlySpan<char> sev = CustomWidgetExpressionEvaluator
                .Interpolate(spec.Severity ?? spec.Tone, evalCtx)
                .AsSpan()
                .Trim();
            if (sev.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                sev.Equals("emerald", StringComparison.OrdinalIgnoreCase))
            {
                badge.Background = ResolveSemanticBrush("success");
            }
            else if (sev.Equals("caution", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("coral", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("orange", StringComparison.OrdinalIgnoreCase))
            {
                badge.Background = ResolveSemanticBrush("warning");
            }
            else if (sev.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("danger", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                badge.Background = ResolveSemanticBrush("danger");
            }
            else if (sev.Equals("informational", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("secondary", StringComparison.OrdinalIgnoreCase) ||
                     sev.Equals("muted", StringComparison.OrdinalIgnoreCase))
            {
                badge.Background = ResolveForegroundBrush("secondary");
            }
            else
            {
                badge.Background = ResolveSemanticBrush("accent");
            }
        }

        ApplyBadgeState(ctx);
        RegisterRetainedPatcher(ApplyBadgeState);
        return badge;
    }

    private FrameworkElement BuildInfoBar(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        static InfoBarSeverity ResolveSeverity(string? raw)
        {
            ReadOnlySpan<char> s = (raw ?? "informational").AsSpan().Trim();
            if (s.Equals("success", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("emerald", StringComparison.OrdinalIgnoreCase))
            {
                return InfoBarSeverity.Success;
            }
            if (s.Equals("warning", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("caution", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("coral", StringComparison.OrdinalIgnoreCase))
            {
                return InfoBarSeverity.Warning;
            }
            if (s.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("danger", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("critical", StringComparison.OrdinalIgnoreCase))
            {
                return InfoBarSeverity.Error;
            }
            return InfoBarSeverity.Informational;
        }

        string bindKey = spec.Bind ?? string.Empty;
        bool ResolveIsOpen(CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string raw = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx);
                return string.IsNullOrWhiteSpace(raw) || CustomWidgetExpressionEvaluator.IsTruthy(raw);
            }
            if (!string.IsNullOrWhiteSpace(spec.IsOpen))
            {
                return CustomWidgetExpressionEvaluator.EvaluateCondition(spec.IsOpen, evalCtx);
            }
            return true;
        }

        var infoBar = new InfoBar
        {
            Title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
            Message = CustomWidgetExpressionEvaluator.Interpolate(spec.Text ?? spec.Subtitle, ctx),
            Severity = ResolveSeverity(CustomWidgetExpressionEvaluator.Interpolate(spec.Severity ?? spec.Tone, ctx)),
            IsClosable = spec.IsClosable,
            IsOpen = ResolveIsOpen(ctx),
            CornerRadius = CornerMedium
        };

        if (spec.ActionButton is not null)
        {
            UIElement actionEl = BuildNode(spec.ActionButton, ctx);
            if (actionEl is ButtonBase btnBase)
            {
                infoBar.ActionButton = btnBase;
            }
        }

        if (spec.Children.Count > 0)
        {
            var body = new StackPanel { Spacing = spec.Spacing, Margin = new Thickness(0, 4, 0, 6) };
            foreach (CustomWidgetNodeSpec child in spec.Children)
            {
                body.Children.Add(BuildNode(child, ctx));
            }
            infoBar.Content = body;
        }

        bool syncing = false;
        infoBar.Closed += (_, _) =>
        {
            if (!syncing && !string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, "false");
            }
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            infoBar.Title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, liveCtx);
            infoBar.Message = CustomWidgetExpressionEvaluator.Interpolate(spec.Text ?? spec.Subtitle, liveCtx);
            infoBar.Severity = ResolveSeverity(CustomWidgetExpressionEvaluator.Interpolate(spec.Severity ?? spec.Tone, liveCtx));
            bool nextOpen = ResolveIsOpen(liveCtx);
            if (infoBar.IsOpen != nextOpen)
            {
                syncing = true;
                try
                {
                    infoBar.IsOpen = nextOpen;
                }
                finally
                {
                    syncing = false;
                }
            }
        });

        return infoBar;
    }

    private FrameworkElement BuildTeachingTip(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string id = (!string.IsNullOrWhiteSpace(spec.Id) ? spec.Id : spec.PopoverId)?.Trim()
                    ?? $"tip-{_teachingTipsById.Count + 1}";

        var tip = new TeachingTip
        {
            Title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
            Subtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle ?? spec.Text, ctx),
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(_themeScope),
            IsLightDismissEnabled = true,
            ShouldConstrainToRootBounds = false,
            Tag = spec.Target
        };

        string? glyph = ResolveNormalizedIcon(spec.Icon, ctx);
        if (!string.IsNullOrWhiteSpace(glyph) && CustomWidgetTemplates.IsSegoeFluentGlyph(glyph))
        {
            tip.IconSource = new FontIconSource
            {
                Glyph = NormalizeFluentGlyphForOs(glyph, WindowsCompatibilityService.IsWindows11OrLater)
            };
        }

        if (!string.IsNullOrWhiteSpace(spec.CloseButtonText))
        {
            tip.CloseButtonContent = CustomWidgetExpressionEvaluator.Interpolate(spec.CloseButtonText, ctx);
        }

        if (spec.ActionButton is not null)
        {
            tip.ActionButtonContent = CustomWidgetExpressionEvaluator.Interpolate(
                spec.ActionButton.Text ?? spec.ActionButton.Title,
                ctx);
            if (spec.ActionButton.OnClick.Count > 0)
            {
                IReadOnlyList<CustomWidgetActionSpec> actions = spec.ActionButton.OnClick;
                tip.ActionButtonClick += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    tip.IsOpen = false;
                    await _viewModel.ExecuteActionsAsync(actions, _viewModel.CreateEvaluationContext());
                });
            }
        }

        if (spec.Children.Count > 0)
        {
            var contentStack = new StackPanel { Spacing = spec.Spacing };
            foreach (CustomWidgetNodeSpec child in spec.Children)
            {
                contentStack.Children.Add(BuildNode(child, ctx));
            }
            tip.Content = contentStack;
        }

        if (!string.IsNullOrWhiteSpace(spec.Target) && TryResolveElementById(spec.Target) is { } targetEl)
        {
            tip.Target = targetEl;
        }

        string bindKey = spec.Bind ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(bindKey))
        {
            tip.Closed += (_, _) => _viewModel.SetStateValueWithoutRebuild(bindKey, "false");
        }

        _teachingTipsById[id] = tip;

        var host = new Grid
        {
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };
        host.Resources[id] = tip;
        host.Children.Add(tip);

        RegisterRetainedPatcher(liveCtx =>
        {
            tip.Title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, liveCtx);
            tip.Subtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle ?? spec.Text, liveCtx);
            if (!string.IsNullOrWhiteSpace(spec.Target) && tip.Target is null &&
                TryResolveElementById(spec.Target) is { } resolvedTarget)
            {
                tip.Target = resolvedTarget;
            }

            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                bool shouldOpen = CustomWidgetExpressionEvaluator.IsTruthy(
                    CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx));
                if (tip.IsOpen != shouldOpen)
                {
                    tip.IsOpen = shouldOpen;
                }
            }
            else if (!string.IsNullOrWhiteSpace(spec.IsOpen))
            {
                bool shouldOpen = CustomWidgetExpressionEvaluator.EvaluateCondition(spec.IsOpen, liveCtx);
                if (tip.IsOpen != shouldOpen)
                {
                    tip.IsOpen = shouldOpen;
                }
            }
        });

        return host;
    }

    // =========================================================================
    // RichText (Multi-Span Inline Typography) & CodeBlock (Monospace / Log Tail)
    // =========================================================================

    internal readonly record struct ResolvedRichTextRunSpec(
        string Text,
        string Color,
        bool Bold,
        string FontWeight,
        bool Italic,
        bool Strikethrough,
        bool Monospace);

    internal static IReadOnlyList<ResolvedRichTextRunSpec> ResolveComputedRichTextRuns(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        if (!string.IsNullOrWhiteSpace(spec.Items) &&
            TryResolveComputedRecords(spec.Items, ctx, out IReadOnlyList<CustomWidgetRecord> records) &&
            records.Count > 0)
        {
            var result = new List<ResolvedRichTextRunSpec>(records.Count);
            foreach (CustomWidgetRecord rec in records)
            {
                string segText = rec.GetField("text");
                if (string.IsNullOrEmpty(segText))
                {
                    continue;
                }

                result.Add(new ResolvedRichTextRunSpec(
                    Text: segText,
                    Color: rec.GetField("color"),
                    Bold: CustomWidgetExpressionEvaluator.IsTruthy(rec.GetField("bold")),
                    FontWeight: rec.GetField("fontWeight"),
                    Italic: CustomWidgetExpressionEvaluator.IsTruthy(rec.GetField("italic")),
                    Strikethrough: CustomWidgetExpressionEvaluator.IsTruthy(rec.GetField("strikethrough")),
                    Monospace: CustomWidgetExpressionEvaluator.IsTruthy(rec.GetField("monospace"))));
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        string fallback = CustomWidgetExpressionEvaluator.Interpolate(spec.Text ?? spec.Value ?? string.Empty, ctx);
        return string.IsNullOrEmpty(fallback)
            ? []
            : [new ResolvedRichTextRunSpec(fallback, string.Empty, false, string.Empty, false, false, false)];
    }

    private FrameworkElement BuildRichText(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var richBlock = new TextBlock
        {
            IsTextSelectionEnabled = !_isNodeSelectionEnabled,
            TextWrapping = spec.WrapText ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = spec.FontSize is > 0 ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value) : BodyFontSize,
            Foreground = ResolveSafeTextColorBrush(CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx), "default")
        };

        if (spec.MaxLines > 0)
        {
            richBlock.MaxLines = spec.MaxLines;
        }

        if (!string.IsNullOrWhiteSpace(spec.TextAlignment))
        {
            ReadOnlySpan<char> ta = spec.TextAlignment.AsSpan().Trim();
            richBlock.TextAlignment = ta.Equals("center", StringComparison.OrdinalIgnoreCase)
                ? TextAlignment.Center
                : (ta.Equals("right", StringComparison.OrdinalIgnoreCase) || ta.Equals("end", StringComparison.OrdinalIgnoreCase))
                    ? TextAlignment.Right
                    : ta.Equals("justify", StringComparison.OrdinalIgnoreCase)
                        ? TextAlignment.Justify
                        : TextAlignment.Left;
        }

        void ApplyRunStyling(
            Run run,
            double? fontSize,
            bool monospace,
            string? fontFamily,
            bool bold,
            string? fontWeight,
            bool italic,
            bool strikethrough,
            string? color,
            CustomWidgetEvaluationContext evalCtx)
        {
            if (fontSize is > 0)
            {
                run.FontSize = SnapLegacyFontSizeToHostTier(fontSize.Value);
            }
            if (monospace)
            {
                run.FontFamily = MonoFontFamily;
            }
            else if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                run.FontFamily = new FontFamily(fontFamily.Trim());
            }
            if (bold || TryResolveFontWeight(fontWeight) is { Weight: >= 600 })
            {
                run.FontWeight = TryResolveFontWeight(fontWeight) ?? FontWeights.SemiBold;
            }
            if (italic)
            {
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }
            if (strikethrough)
            {
                run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            }
            if (!string.IsNullOrWhiteSpace(color))
            {
                run.Foreground = ResolveAnyColorBrush(color, evalCtx, "default");
            }
        }

        if (spec.Spans.Count == 0 && !string.IsNullOrWhiteSpace(spec.Items))
        {
            void ApplyComputedSpans(CustomWidgetEvaluationContext evalCtx)
            {
                richBlock.Inlines.Clear();
                foreach (ResolvedRichTextRunSpec runSpec in ResolveComputedRichTextRuns(spec, evalCtx))
                {
                    var run = new Run { Text = runSpec.Text };
                    ApplyRunStyling(
                        run,
                        fontSize: null,
                        monospace: runSpec.Monospace,
                        fontFamily: null,
                        bold: runSpec.Bold,
                        fontWeight: runSpec.FontWeight,
                        italic: runSpec.Italic,
                        strikethrough: runSpec.Strikethrough,
                        color: runSpec.Color,
                        evalCtx: evalCtx);
                    richBlock.Inlines.Add(run);
                }
            }

            ApplyComputedSpans(ctx);
            RegisterRetainedPatcher(ApplyComputedSpans);
            return richBlock;
        }

        if (spec.Spans.Count == 0)
        {
            string rawFallback = spec.Text ?? spec.Value ?? string.Empty;
            var singleRun = new Run
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(rawFallback, ctx)
            };
            richBlock.Inlines.Add(singleRun);
            RegisterRetainedPatcher(liveCtx =>
            {
                string next = CustomWidgetExpressionEvaluator.Interpolate(rawFallback, liveCtx);
                if (!string.Equals(singleRun.Text, next, StringComparison.Ordinal))
                {
                    singleRun.Text = next;
                }
            });
            return richBlock;
        }

        var spanUpdaters = new List<Action<CustomWidgetEvaluationContext>>(spec.Spans.Count);
        foreach (CustomWidgetTextSpanSpec spanSpec in spec.Spans)
        {
            var run = new Run
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spanSpec.Text, ctx)
            };
            ApplyRunStyling(
                run,
                spanSpec.FontSize,
                spanSpec.Monospace,
                spanSpec.FontFamily,
                spanSpec.Bold,
                spanSpec.FontWeight,
                spanSpec.Italic,
                spanSpec.Strikethrough,
                spanSpec.Color,
                ctx);

            Inline topInline = run;
            if (spanSpec.Underline)
            {
                var u = new Underline();
                u.Inlines.Add(run);
                topInline = u;
            }

            if (!string.IsNullOrWhiteSpace(spanSpec.Href) || spanSpec.OnClick is { Count: > 0 })
            {
                var link = new Hyperlink();
                link.Inlines.Add(topInline);
                string? rawHref = spanSpec.Href;
                IReadOnlyList<CustomWidgetActionSpec>? clickActions = spanSpec.OnClick;
                link.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    CustomWidgetEvaluationContext clickCtx = _viewModel.CreateEvaluationContext();
                    if (clickActions is { Count: > 0 })
                    {
                        await _viewModel.ExecuteActionsAsync(clickActions, clickCtx);
                    }
                    else if (!string.IsNullOrWhiteSpace(rawHref))
                    {
                        await _viewModel.ExecuteActionsAsync(
                        [
                            new CustomWidgetActionSpec("url.open", Url: rawHref)
                        ], clickCtx);
                    }
                });
                topInline = link;
            }

            richBlock.Inlines.Add(topInline);

            CustomWidgetTextSpanSpec capturedSpan = spanSpec;
            spanUpdaters.Add(liveCtx =>
            {
                string nextText = CustomWidgetExpressionEvaluator.Interpolate(capturedSpan.Text, liveCtx);
                if (!string.Equals(run.Text, nextText, StringComparison.Ordinal))
                {
                    run.Text = nextText;
                }
                if (!string.IsNullOrWhiteSpace(capturedSpan.Color) && capturedSpan.Color.Contains('$'))
                {
                    run.Foreground = ResolveAnyColorBrush(capturedSpan.Color, liveCtx, "default");
                }
            });
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            foreach (Action<CustomWidgetEvaluationContext> update in spanUpdaters)
            {
                update(liveCtx);
            }
        });

        return richBlock;
    }

    private FrameworkElement BuildCodeBlock(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawTemplate = spec.Text ?? spec.Value ?? string.Empty;

        string ResolveCodeText(CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string bound = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx);
                if (!string.IsNullOrEmpty(bound) || string.IsNullOrEmpty(rawTemplate))
                {
                    return bound;
                }
            }
            return CustomWidgetExpressionEvaluator.Interpolate(rawTemplate, evalCtx);
        }

        Brush ResolveCurrentCodeBrush(CustomWidgetEvaluationContext evalCtx) =>
            TryResolveExplicitTextBrush(spec, evalCtx, out Brush? explicitFg) && explicitFg is not null
                ? explicitFg
                : ResolveForegroundBrush("default");

        string currentRawCode = ResolveCodeText(ctx);
        string currentLangHint = CustomWidgetExpressionEvaluator.Interpolate(spec.Language, ctx);
        string currentColorHint = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);
        bool showLinedGutter = string.Equals(spec.Variant, "lined", StringComparison.OrdinalIgnoreCase);

        var codeTb = new TextBlock
        {
            FontFamily = MonoFontFamily,
            FontSize = spec.FontSize is > 0
                ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value)
                : Math.Max(10.0, BodyFontSize - 0.5),
            Foreground = ResolveCurrentCodeBrush(ctx),
            IsTextSelectionEnabled = !_isNodeSelectionEnabled,
            TextWrapping = spec.WrapText ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FlowDirection = FlowDirection.LeftToRight
        };
        WidgetTextShadow.SetCast(codeTb, false);
        PopulateHighlightedCodeInlines(
            codeTb,
            currentRawCode,
            currentLangHint,
            IsDark,
            ResolveCurrentCodeBrush(ctx),
            ResolveForegroundBrush("muted"),
            GetOrCreateSolidBrush);

        TextBlock? gutterTb = null;
        UIElement scrollContent = codeTb;
        if (showLinedGutter)
        {
            gutterTb = new TextBlock
            {
                Text = FormatCodeLineNumbers(currentRawCode),
                FontFamily = MonoFontFamily,
                FontSize = codeTb.FontSize,
                Foreground = ResolveForegroundBrush("muted"),
                TextAlignment = TextAlignment.Right,
                IsTextSelectionEnabled = false,
                FlowDirection = FlowDirection.LeftToRight
            };
            WidgetTextShadow.SetCast(gutterTb, false);

            var gutterBorder = new Border
            {
                Padding = new Thickness(8, 8, 8, 8),
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = gutterTb
            };

            codeTb.Margin = new Thickness(10, 8, 10, 8);
            var codeGrid = new Grid
            {
                Background = spec.OnClick.Count > 0 ? GetOrCreateSolidBrush(Colors.Transparent) : null
            };
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            codeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(gutterBorder, 0);
            Grid.SetColumn(codeTb, 1);
            codeGrid.Children.Add(gutterBorder);
            codeGrid.Children.Add(codeTb);
            scrollContent = codeGrid;
        }

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = spec.WrapText ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = spec.MaxHeight ?? 180,
            Padding = showLinedGutter ? new Thickness(0) : new Thickness(10, 8, 10, 8),
            Background = spec.OnClick.Count > 0 ? GetOrCreateSolidBrush(Colors.Transparent) : null,
            Content = scrollContent
        };
        if (spec.Height.HasValue)
        {
            scrollViewer.Height = spec.Height.Value;
        }

        var layoutGrid = new Grid();
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        CustomWidgetEvaluationContext capturedClickCtx = ctx;
        TextBlock? langHeaderTb = null;
        string rawHeaderSpec = spec.Language ?? spec.Title ?? string.Empty;
        string lang = CustomWidgetExpressionEvaluator.Interpolate(rawHeaderSpec, ctx).Trim();
        bool showLangHeader = !string.IsNullOrWhiteSpace(lang) &&
                              !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase);
        if (showLangHeader || spec.ShowCopyButton)
        {
            var header = new Grid
            {
                Padding = new Thickness(10, 4, 6, 4),
                BorderBrush = DividerBrush,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (showLangHeader)
            {
                langHeaderTb = new TextBlock
                {
                    Text = lang,
                    FontFamily = MonoFallbackFontFamily,
                    FontSize = 10.5,
                    Foreground = ResolveForegroundBrush("muted"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                WidgetTextShadow.SetCast(langHeaderTb, false);
                header.Children.Add(langHeaderTb);
            }

            if (spec.ShowCopyButton)
            {
                Button copyBtn = CreateSubtleIconButton(
                    "\uE8C8",
                    size: 22,
                    iconSize: 11,
                    tooltip: Tr("CustomWidget.Renderer.FileDrop.CopyTooltip", ctx.CultureName));
                Action triggerCopyMorph = AttachCopyMorphFeedback(
                    copyBtn,
                    copyBtn.Content as FrameworkElement,
                    null,
                    null,
                    null,
                    static () => "\uE8C8",
                    ResolveForegroundBrush("secondary"));
                copyBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("clipboard.copy", Text: currentRawCode)
                    ], capturedClickCtx);
                    triggerCopyMorph();
                });
                Grid.SetColumn(copyBtn, 1);
                header.Children.Add(copyBtn);
            }

            Grid.SetRow(header, 0);
            layoutGrid.Children.Add(header);
        }

        Grid.SetRow(scrollViewer, 1);
        layoutGrid.Children.Add(scrollViewer);

        var border = new Border
        {
            CornerRadius = CornerMedium,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(44, 10, 14, 22) : Color.FromArgb(18, 15, 23, 42)),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Child = layoutGrid
        };

        if (spec.OnClick.Count > 0 && !_isNodeSelectionEnabled)
        {
            MarkInteractiveElement(scrollViewer);
            scrollViewer.Tapped += (_, e) =>
            {
                if (_isNodeSelectionEnabled || e.Handled || !string.IsNullOrEmpty(codeTb.SelectedText))
                {
                    return;
                }

                e.Handled = true;
                FireAndForgetSafeUi(() => ExecuteInteractiveClickWithPopoverAsync(spec, capturedClickCtx, scrollViewer));
            };
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedClickCtx = liveCtx;
            if (langHeaderTb is not null)
            {
                string nextHdr = CustomWidgetExpressionEvaluator.Interpolate(rawHeaderSpec, liveCtx).Trim();
                if (!string.Equals(langHeaderTb.Text, nextHdr, StringComparison.Ordinal))
                {
                    langHeaderTb.Text = nextHdr;
                }
            }
            string nextCode = ResolveCodeText(liveCtx);
            string nextLangHint = CustomWidgetExpressionEvaluator.Interpolate(spec.Language, liveCtx);
            string nextColorHint = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, liveCtx);
            bool codeChanged = !string.Equals(currentRawCode, nextCode, StringComparison.Ordinal);

            if (codeChanged ||
                !string.Equals(currentLangHint, nextLangHint, StringComparison.Ordinal) ||
                !string.Equals(currentColorHint, nextColorHint, StringComparison.Ordinal))
            {
                currentRawCode = nextCode;
                currentLangHint = nextLangHint;
                currentColorHint = nextColorHint;
                PopulateHighlightedCodeInlines(
                    codeTb,
                    nextCode,
                    nextLangHint,
                    IsDark,
                    ResolveCurrentCodeBrush(liveCtx),
                    ResolveForegroundBrush("muted"),
                    GetOrCreateSolidBrush);

                if (gutterTb is not null && codeChanged)
                {
                    gutterTb.Text = FormatCodeLineNumbers(nextCode);
                }

                if (codeChanged && spec.AutoScrollTail)
                {
                    scrollViewer.UpdateLayout();
                    scrollViewer.ChangeView(null, scrollViewer.ScrollableHeight, null, disableAnimation: false);
                }

                if (codeChanged && border.IsLoaded)
                {
                    PlayCodeRefreshFeedback(codeTb, border, DividerBrush);
                }
            }
        });

        return ApplyContainerVisualOverrides(border, spec, ctx);
    }

    // =========================================================================
    // RadialGauge (Precision Sweep Dial with Optional Ticks & Gradient Stroke)
    // =========================================================================

    private FrameworkElement BuildRadialGauge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double defaultSize = Math.Round(76.0 * EffectiveAppearance.DensityMultiplier);
        double size = Math.Clamp(spec.Size ?? spec.Width ?? spec.Height ?? defaultSize, 44, 160);
        double strokeThickness = Math.Clamp(spec.GaugeThickness > 0 ? spec.GaugeThickness : Math.Round(size * 0.065), 3, 20);
        double radius = spec.ShowTicks
            ? Math.Max(16, (size - strokeThickness - 4) * 0.5)
            : Math.Max(14, (size - strokeThickness) * 0.5);
        double cx = size * 0.5;
        double cy = size * 0.5;

        bool isFullCircle = string.Equals(spec.Style, "full", StringComparison.OrdinalIgnoreCase) ||
                            spec.SweepAngle >= 359.5;
        double startDeg = isFullCircle && Math.Abs(spec.StartAngle - (-135.0)) < 0.01 ? -90.0 : spec.StartAngle;
        double maxSweepDeg = Math.Clamp(isFullCircle && Math.Abs(spec.SweepAngle - 270.0) < 0.01 ? 359.9 : spec.SweepAngle, 30.0, 359.95);

        double min = spec.Min;
        double max = spec.Max > min ? spec.Max : min + 100;
        double step = spec.Step > 0 ? spec.Step : (max - min <= 2.0 ? 0.01 : 1.0);
        string bindKey = spec.Bind ?? string.Empty;
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        string ResolveProgress(CustomWidgetEvaluationContext evalCtx)
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string bound = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, evalCtx);
                if (!string.IsNullOrEmpty(bound) || string.IsNullOrEmpty(rawProgress))
                {
                    return bound;
                }
            }
            return rawProgress;
        }
        string rawDisplayText = !string.IsNullOrWhiteSpace(spec.Text)
            ? spec.Text!
            : (!string.IsNullOrWhiteSpace(spec.Value) ? spec.Value! : rawProgress);
        string rawTitle = spec.Title ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;

        static Geometry CreateGaugeArcGeometry(double centerX, double centerY, double r, double startAngleDeg, double sweepAngleDeg)
        {
            if (sweepAngleDeg >= 359.9)
            {
                return new EllipseGeometry
                {
                    Center = new Point(centerX, centerY),
                    RadiusX = r,
                    RadiusY = r
                };
            }

            var geom = new PathGeometry();
            double clamped = Math.Clamp(sweepAngleDeg, 0, 359.9);
            if (clamped <= 0.2)
            {
                return geom;
            }

            double sRad = startAngleDeg * (Math.PI / 180.0);
            double eRad = (startAngleDeg + clamped) * (Math.PI / 180.0);
            var fig = new PathFigure
            {
                StartPoint = new Point(centerX + r * Math.Cos(sRad), centerY + r * Math.Sin(sRad)),
                IsClosed = false,
                IsFilled = false
            };
            fig.Segments.Add(new ArcSegment
            {
                Point = new Point(centerX + r * Math.Cos(eRad), centerY + r * Math.Sin(eRad)),
                Size = new Size(r, r),
                IsLargeArc = clamped > 180.0,
                SweepDirection = SweepDirection.Clockwise
            });
            geom.Figures.Add(fig);
            return geom;
        }

        Brush ResolveActiveStroke(double val, CustomWidgetEvaluationContext evalCtx)
        {
            if (spec.LinearGradient is { Stops.Count: > 0 })
            {
                return BuildLinearGradientBrush(spec.LinearGradient, evalCtx);
            }

            string resolvedColor = CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx).Trim();
            if (TryParseRawColor(resolvedColor, out Color explicitCol))
            {
                return GetOrCreateSolidBrush(explicitCol);
            }

            double? warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, evalCtx);
            double? danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, evalCtx);
            return ResolveThresholdBrush(val, warn, danger, string.IsNullOrWhiteSpace(resolvedColor) ? "accent" : resolvedColor);
        }

        var dialStage = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = string.Equals(spec.Dir, "rtl", StringComparison.OrdinalIgnoreCase) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };

        var trackPath = new WinUIPath
        {
            Width = size,
            Height = size,
            Data = CreateGaugeArcGeometry(cx, cy, radius, startDeg, maxSweepDeg),
            Stroke = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0)),
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        dialStage.Children.Add(trackPath);

        if (spec.ShowTicks)
        {
            var ticksGeom = new PathGeometry();
            int tickCount = spec.TickStep > 0
                ? Math.Clamp((int)Math.Round((max - min) / spec.TickStep), 1, 60)
                : 12;
            double innerTickR = Math.Max(6, radius - strokeThickness * 1.45);
            double outerTickR = Math.Max(innerTickR + 3, radius - strokeThickness * 0.85);
            for (int t = 0; t <= tickCount; t++)
            {
                if (isFullCircle && t == tickCount) break;
                double ang = (startDeg + (maxSweepDeg * t / tickCount)) * (Math.PI / 180.0);
                var fig = new PathFigure
                {
                    StartPoint = new Point(cx + innerTickR * Math.Cos(ang), cy + innerTickR * Math.Sin(ang)),
                    IsClosed = false,
                    IsFilled = false
                };
                fig.Segments.Add(new LineSegment
                {
                    Point = new Point(cx + outerTickR * Math.Cos(ang), cy + outerTickR * Math.Sin(ang))
                });
                ticksGeom.Figures.Add(fig);
            }

            dialStage.Children.Add(new WinUIPath
            {
                Width = size,
                Height = size,
                Data = ticksGeom,
                Stroke = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0)),
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        double initialVal = CustomWidgetExpressionEvaluator.EvaluateDouble(ResolveProgress(ctx), ctx) ?? min;
        double initialNorm = Math.Clamp((initialVal - min) / (max - min), 0.0, 1.0);

        var activePath = new WinUIPath
        {
            Width = size,
            Height = size,
            Stroke = ResolveActiveStroke(initialVal, ctx),
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        UpdateArcPathInPlace(activePath, cx, cy, radius, startDeg, maxSweepDeg * initialNorm, allowFullCircleEllipse: true);
        dialStage.Children.Add(activePath);

        string FormatCenterText(double numericVal, CustomWidgetEvaluationContext evalCtx)
        {
            string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, evalCtx);
            if (!string.IsNullOrWhiteSpace(spec.Text))
            {
                return CustomWidgetExpressionEvaluator.Interpolate(spec.Text, evalCtx);
            }
            if (!string.IsNullOrWhiteSpace(spec.Value))
            {
                string interp = CustomWidgetExpressionEvaluator.Interpolate(spec.Value, evalCtx);
                if (!double.TryParse(interp, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    return $"{interp}{unit}";
                }
            }
            return $"{Math.Round(numericVal).ToString(CultureInfo.InvariantCulture)}{unit}";
        }

        var centerStack = new StackPanel
        {
            Spacing = 2,
            MaxWidth = Math.Max(40, (radius - strokeThickness) * 1.78),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        TextBlock? topTitleTb = null;
        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            topTitleTb = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx),
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                FontWeight = FontWeights.Medium,
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            centerStack.Children.Add(topTitleTb);
        }

        double centerValueFontSize = spec.FontSize is > 0
            ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value)
            : Math.Clamp(Math.Round(size * 0.18), CaptionFontSize, HeroFontSize);
        var valueTb = new TextBlock
        {
            Text = FormatCenterText(initialVal, ctx),
            FontFamily = DisplayFontFamily,
            FontSize = centerValueFontSize,
            FontWeight = FontWeights.SemiBold,
            TextLineBounds = TextLineBounds.Tight,
            Foreground = TryResolveExplicitTextBrush(spec, ctx, out Brush? explicitFg) && explicitFg is not null
                ? explicitFg
                : ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetTabularNumerals(valueTb);
        centerStack.Children.Add(valueTb);

        TextBlock? subTb = null;
        if (!string.IsNullOrWhiteSpace(rawSubtitle))
        {
            subTb = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx),
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            SetTabularNumerals(subTb);
            centerStack.Children.Add(subTb);
        }

        if (spec.Children.Count > 0)
        {
            var overlayInner = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (CustomWidgetNodeSpec child in spec.Children)
            {
                overlayInner.Children.Add(BuildNode(child, ctx));
            }
            dialStage.Children.Add(overlayInner);
        }
        else
        {
            dialStage.Children.Add(centerStack);
        }

        string FormatGaugeNumber(double val) =>
            step >= 1.0
                ? Math.Round(val).ToString(CultureInfo.InvariantCulture)
                : Math.Round(val, 2).ToString("0.##", CultureInfo.InvariantCulture);

        bool isUserDragging = false;
        double currentGaugeSweep = maxSweepDeg * initialNorm;
        int[] gaugeSweepAnimVersion = [0];

        void ApplyGaugeVisual(double val, CustomWidgetEvaluationContext evalCtx)
        {
            double norm = Math.Clamp((val - min) / (max - min), 0.0, 1.0);
            double targetSweep = maxSweepDeg * norm;
            if (isUserDragging)
            {
                gaugeSweepAnimVersion[0]++;
                currentGaugeSweep = targetSweep;
                UpdateArcPathInPlace(activePath, cx, cy, radius, startDeg, targetSweep, allowFullCircleEllipse: true);
            }
            else
            {
                AnimateGaugeSweep(
                    activePath,
                    ref currentGaugeSweep,
                    targetSweep,
                    cx,
                    cy,
                    radius,
                    startDeg,
                    gaugeSweepAnimVersion,
                    allowFullCircleEllipse: true);
            }
            activePath.Stroke = ResolveActiveStroke(val, evalCtx);

            string nextCenter = FormatCenterText(val, evalCtx);
            if (!string.Equals(valueTb.Text, nextCenter, StringComparison.Ordinal))
            {
                valueTb.Text = nextCenter;
                if (spec.AnimateChange)
                {
                    TryAnimateTextChange(valueTb);
                }
            }
        }

        double dragValue = initialVal;
        string lastCommittedFormatted = FormatGaugeNumber(initialVal);
        CustomWidgetEvaluationContext capturedCtx = ctx;

        if (spec.IsInteractive && !_isNodeSelectionEnabled &&
            (!string.IsNullOrWhiteSpace(bindKey) || spec.OnChange.Count > 0))
        {
            // A bare Grid only hit-tests through its children; the transparent fill makes the whole dial draggable.
            dialStage.Background = GetOrCreateSolidBrush(Colors.Transparent);
            MarkInteractiveElement(dialStage);

            double ResolveDragValue(Point pt)
            {
                double dx = pt.X - cx;
                double dy = pt.Y - cy;
                if (Math.Abs(dx) < 1.0 && Math.Abs(dy) < 1.0)
                {
                    return dragValue;
                }

                double rel = (Math.Atan2(dy, dx) * (180.0 / Math.PI)) - startDeg;
                rel %= 360.0;
                if (rel < 0)
                {
                    rel += 360.0;
                }
                if (rel > maxSweepDeg)
                {
                    rel = rel - maxSweepDeg > (360.0 - maxSweepDeg) * 0.5 ? 0.0 : maxSweepDeg;
                }
                double rawDrag = min + (Math.Clamp(rel / maxSweepDeg, 0.0, 1.0) * (max - min));
                double snapped = min + (Math.Round((rawDrag - min) / step) * step);
                return Math.Clamp(snapped, min, max);
            }

            void ApplyDrag(Point pt)
            {
                dragValue = ResolveDragValue(pt);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    _viewModel.SetStateValueWithoutRebuild(bindKey, FormatGaugeNumber(dragValue));
                    capturedCtx = _viewModel.CreateEvaluationContext();
                }
                ApplyGaugeVisual(dragValue, capturedCtx);
            }

            async Task CommitGaugeValueAsync()
            {
                string formatted = FormatGaugeNumber(dragValue);
                if (string.Equals(formatted, lastCommittedFormatted, StringComparison.Ordinal))
                {
                    capturedCtx = _viewModel.CreateEvaluationContext();
                    ApplyGaugeVisual(dragValue, capturedCtx);
                    return;
                }
                lastCommittedFormatted = formatted;

                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(formatted);
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
                capturedCtx = _viewModel.CreateEvaluationContext();
                ApplyGaugeVisual(dragValue, capturedCtx);
            }

            dialStage.PointerPressed += (_, e) =>
            {
                if (_isNodeSelectionEnabled || e.Handled ||
                    !e.GetCurrentPoint(dialStage).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                isUserDragging = true;
                BeginInteractiveSession(dialStage);
                dialStage.CapturePointer(e.Pointer);
                ApplyDrag(e.GetCurrentPoint(dialStage).Position);
                e.Handled = true;
            };
            dialStage.PointerMoved += (_, e) =>
            {
                if (isUserDragging)
                {
                    ApplyDrag(e.GetCurrentPoint(dialStage).Position);
                }
            };
            dialStage.PointerReleased += (_, e) =>
            {
                if (!isUserDragging)
                {
                    return;
                }

                isUserDragging = false;
                dialStage.ReleasePointerCapture(e.Pointer);
                EndInteractiveSession(dialStage);
                e.Handled = true;
            };
            dialStage.PointerCanceled += (_, e) =>
            {
                isUserDragging = false;
                dialStage.ReleasePointerCapture(e.Pointer);
                EndInteractiveSession(dialStage);
            };
            dialStage.PointerCaptureLost += (_, _) =>
            {
                isUserDragging = false;
                EndInteractiveSession(dialStage);
                FireAndForgetSafeUi(CommitGaugeValueAsync);
            };
            dialStage.Unloaded += (_, _) =>
            {
                isUserDragging = false;
                EndInteractiveSession(dialStage);
            };
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (!isUserDragging && !_activeInteractions.Contains(dialStage))
            {
                double liveVal = CustomWidgetExpressionEvaluator.EvaluateDouble(ResolveProgress(liveCtx), liveCtx) ?? min;
                ApplyGaugeVisual(liveVal, liveCtx);
                dragValue = liveVal;
                lastCommittedFormatted = FormatGaugeNumber(liveVal);
            }

            if (topTitleTb is not null)
            {
                topTitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            }
            if (subTb is not null)
            {
                subTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
            }
        });

        return dialStage;
    }
}

public sealed partial class CustomWidgetUniformGridPanel : Panel
{
    public int Columns { get; set; }
    public double MinItemWidth { get; set; } = 108;
    public double Spacing { get; set; } = 8;

    private int ResolveColumns(double availableWidth)
    {
        if (Columns > 0)
        {
            return Columns;
        }

        if (double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return Math.Max(1, Math.Min(3, Children.Count));
        }

        double minW = Math.Max(24, MinItemWidth);
        return Math.Max(1, (int)Math.Floor((availableWidth + Spacing) / (minW + Spacing)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int visibleCount = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                visibleCount++;
            }
        }

        if (visibleCount == 0)
        {
            return new Size(0, 0);
        }

        int cols = ResolveColumns(availableSize.Width);
        double finiteWidth = double.IsInfinity(availableSize.Width)
            ? cols * MinItemWidth + Math.Max(0, cols - 1) * Spacing
            : Math.Max(0, availableSize.Width);
        double cellWidth = Math.Max(0, (finiteWidth - Math.Max(0, cols - 1) * Spacing) / cols);

        double totalHeight = 0;
        double rowMaxHeight = 0;
        int colIdx = 0;
        int rowIdx = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowMaxHeight = Math.Max(rowMaxHeight, child.DesiredSize.Height);
            colIdx++;
            if (colIdx >= cols)
            {
                totalHeight += (rowIdx > 0 ? Spacing : 0) + rowMaxHeight;
                rowMaxHeight = 0;
                colIdx = 0;
                rowIdx++;
            }
        }

        if (colIdx > 0)
        {
            totalHeight += (rowIdx > 0 ? Spacing : 0) + rowMaxHeight;
        }

        return new Size(finiteWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = ResolveColumns(finalSize.Width);
        double cellWidth = Math.Max(0, (finalSize.Width - Math.Max(0, cols - 1) * Spacing) / cols);

        var rowElements = new List<UIElement>(cols);
        double y = 0;
        double rowHeight = 0;

        void FlushRow()
        {
            for (int i = 0; i < rowElements.Count; i++)
            {
                double x = i * (cellWidth + Spacing);
                rowElements[i].Arrange(new Rect(x, y, cellWidth, rowHeight));
            }
            y += rowHeight + Spacing;
            rowHeight = 0;
            rowElements.Clear();
        }

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            rowElements.Add(child);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (rowElements.Count >= cols)
            {
                FlushRow();
            }
        }

        if (rowElements.Count > 0)
        {
            FlushRow();
        }

        return finalSize;
    }
}
