using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;

namespace DeskBox.Controls.WidgetContents;

public sealed record CustomWidgetPickedNode(
    string NodePath,
    string NodeType,
    string DisplayLabel);

public sealed partial class CustomWidgetNativeRenderer
{
    internal static string GetFriendlyNodeTypeLabel(string? nodeType, LocalizationService? loc = null)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return string.Empty;
        }

        string trimmed = nodeType.Trim();
        (string Key, string Fallback)? mapped = trimmed.ToLowerInvariant() switch
        {
            "grid" => ("CustomWidget.Workshop.NodeType.Grid", "Grid"),
            "stack" => ("CustomWidget.Workshop.NodeType.Stack", "Stack"),
            "card" => ("CustomWidget.Workshop.NodeType.Card", "Card"),
            "text" => ("CustomWidget.Workshop.NodeType.Text", "Text"),
            "button" => ("CustomWidget.Workshop.NodeType.Button", "Button"),
            "icon" => ("CustomWidget.Workshop.NodeType.Icon", "Icon"),
            "badge" => ("CustomWidget.Workshop.NodeType.Badge", "Badge"),
            "tabs" => ("CustomWidget.Workshop.NodeType.Tabs", "Tabs"),
            "tab" => ("CustomWidget.Workshop.NodeType.Tab", "Tab"),
            "list" => ("CustomWidget.Workshop.NodeType.List", "List"),
            "itemsgrid" => ("CustomWidget.Workshop.NodeType.ItemsGrid", "ItemsGrid"),
            "scroll" => ("CustomWidget.Workshop.NodeType.Scroll", "Scroll"),
            "input" => ("CustomWidget.Workshop.NodeType.Input", "Input"),
            "toggle" => ("CustomWidget.Workshop.NodeType.Toggle", "Toggle"),
            "slider" => ("CustomWidget.Workshop.NodeType.Slider", "Slider"),
            "progress" => ("CustomWidget.Workshop.NodeType.Progress", "Progress"),
            "chart" => ("CustomWidget.Workshop.NodeType.Chart", "Chart"),
            "donutchart" => ("CustomWidget.Workshop.NodeType.DonutChart", "DonutChart"),
            "table" => ("CustomWidget.Workshop.NodeType.Table", "Table"),
            "heatmap" => ("CustomWidget.Workshop.NodeType.HeatMap", "HeatMap"),
            "barlist" => ("CustomWidget.Workshop.NodeType.BarList", "BarList"),
            "statcard" => ("CustomWidget.Workshop.NodeType.StatCard", "StatCard"),
            "radialgauge" => ("CustomWidget.Workshop.NodeType.RadialGauge", "RadialGauge"),
            "markdown" => ("CustomWidget.Workshop.NodeType.Markdown", "Markdown"),
            "image" => ("CustomWidget.Workshop.NodeType.Image", "Image"),
            "divider" => ("CustomWidget.Workshop.NodeType.Divider", "Divider"),
            "spacer" => ("CustomWidget.Workshop.NodeType.Spacer", "Spacer"),
            "canvas" => ("CustomWidget.Workshop.NodeType.Canvas", "Canvas"),
            "zstack" => ("CustomWidget.Workshop.NodeType.ZStack", "ZStack"),
            "dropzone" => ("CustomWidget.Workshop.NodeType.DropZone", "DropZone"),
            _ => null
        };

        if (mapped is not { Key: var key, Fallback: var fallback })
        {
            return trimmed;
        }

        string? resolved = loc is not null
            ? loc.T(key)
            : LocalizationService.TranslateForCulture(Localized.ActiveCultureName, key);
        return !string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, key, StringComparison.Ordinal)
            ? resolved
            : fallback;
    }

    internal static string FormatPickedNodeTag(string? nodeType, string? displayLabel, LocalizationService? loc = null)
    {
        string friendlyType = GetFriendlyNodeTypeLabel(nodeType, loc);
        string rawType = nodeType?.Trim() ?? string.Empty;
        string cleanLabel = displayLabel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanLabel) ||
            string.Equals(cleanLabel, rawType, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cleanLabel, friendlyType, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(friendlyType) ? friendlyType : rawType;
        }

        return string.IsNullOrWhiteSpace(friendlyType)
            ? cleanLabel
            : $"{friendlyType} · {cleanLabel}";
    }

    private sealed record InspectableNodeEntry(
        string NodePath,
        string NodeType,
        string DisplayLabel,
        bool IsRoot,
        Border HighlightBorder);

    private readonly Dictionary<DependencyObject, InspectableNodeEntry> _inspectableNodes = [];
    private bool _isNodeSelectionEnabled;
    private string? _selectedNodePath;
    private string? _hoveredNodePath;
    private Border? _inspectorHoverTagPill;
    private TextBlock? _inspectorHoverTagText;

    public bool IsNodeSelectionEnabled
    {
        get => _isNodeSelectionEnabled;
        set => _isNodeSelectionEnabled = value;
    }

    public string? SelectedNodePath
    {
        get => _selectedNodePath;
        set => _selectedNodePath = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public event Action<CustomWidgetPickedNode>? NodePicked;

    internal bool UpdateSelectedNodePath(string? nextPath)
    {
        string? normalized = string.IsNullOrWhiteSpace(nextPath) ? null : nextPath;
        _selectedNodePath = normalized;

        if (_inspectableNodes.Count > 0)
        {
            UpdateInspectionHighlights();
            return false;
        }

        return normalized is not null;
    }

    private void ResetInspectionState()
    {
        _inspectableNodes.Clear();
        _hoveredNodePath = null;
        _inspectorHoverTagPill = null;
        _inspectorHoverTagText = null;
    }

    private void AttachRootInspectionHandlers(Grid rootStack)
    {
        if (_inspectableNodes.Count > 0)
        {
            UpdateInspectionHighlights();
        }

        if (!_isNodeSelectionEnabled)
        {
            return;
        }

        Color accentColor = IsDark
            ? Color.FromArgb(245, 96, 165, 250)
            : Color.FromArgb(245, 37, 99, 235);
        _inspectorHoverTagText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetOrCreateSolidBrush(accentColor),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _inspectorHoverTagPill = new Border
        {
            CornerRadius = CornerSmall,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 6, 8, 6),
            MaxWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(236, 22, 26, 34) : Color.FromArgb(242, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = GetOrCreateSolidBrush(accentColor),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _inspectorHoverTagText
        };
        Canvas.SetZIndex(_inspectorHoverTagPill, 24);
        rootStack.Children.Add(_inspectorHoverTagPill);

        rootStack.PointerMoved += (_, e) =>
        {
            if (!_isNodeSelectionEnabled)
            {
                return;
            }

            InspectableNodeEntry? hit = ResolveDeepestInspectableNode(e.OriginalSource as DependencyObject);
            string? nextHover = hit?.NodePath;
            if (!string.Equals(_hoveredNodePath, nextHover, StringComparison.Ordinal))
            {
                _hoveredNodePath = nextHover;
                UpdateInspectionHighlights();
            }

            if (_inspectorHoverTagPill is not null && _inspectorHoverTagText is not null)
            {
                if (hit is null)
                {
                    _inspectorHoverTagPill.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _inspectorHoverTagText.Text = FormatPickedNodeTag(hit.NodeType, hit.DisplayLabel);
                    Point pt = e.GetCurrentPoint(rootStack).Position;
                    double rootHeight = rootStack.ActualHeight;
                    _inspectorHoverTagPill.VerticalAlignment =
                        rootHeight > 80 && pt.Y > rootHeight - 44
                            ? VerticalAlignment.Top
                            : VerticalAlignment.Bottom;
                    _inspectorHoverTagPill.Visibility = Visibility.Visible;
                }
            }
        };

        rootStack.PointerExited += (_, _) =>
        {
            if (_hoveredNodePath is not null)
            {
                _hoveredNodePath = null;
                UpdateInspectionHighlights();
            }

            if (_inspectorHoverTagPill is not null)
            {
                _inspectorHoverTagPill.Visibility = Visibility.Collapsed;
            }
        };

        rootStack.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) =>
            {
                if (!_isNodeSelectionEnabled ||
                    !e.GetCurrentPoint(rootStack).Properties.IsLeftButtonPressed)
                {
                    return;
                }

                InspectableNodeEntry? hit = ResolveDeepestInspectableNode(e.OriginalSource as DependencyObject);
                if (hit is null)
                {
                    return;
                }

                e.Handled = true;
                _selectedNodePath = hit.NodePath;
                UpdateInspectionHighlights();
                NodePicked?.Invoke(new CustomWidgetPickedNode(hit.NodePath, hit.NodeType, hit.DisplayLabel));
            }),
            handledEventsToo: true);
    }

    private InspectableNodeEntry? ResolveDeepestInspectableNode(DependencyObject? source)
    {
        InspectableNodeEntry? rootFallback = null;
        DependencyObject? current = source;
        while (current is not null)
        {
            if (_inspectableNodes.TryGetValue(current, out InspectableNodeEntry? entry))
            {
                if (!entry.IsRoot)
                {
                    return entry;
                }

                rootFallback ??= entry;
            }

            if (ReferenceEquals(current, _themeScope))
            {
                break;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                break;
            }
        }

        return rootFallback;
    }

    private void UpdateInspectionHighlights()
    {
        foreach (InspectableNodeEntry entry in _inspectableNodes.Values)
        {
            ApplyNodeHighlightVisual(entry);
        }
    }

    private void ApplyNodeHighlightVisual(InspectableNodeEntry entry)
    {
        bool isSelected = !string.IsNullOrWhiteSpace(_selectedNodePath) &&
                          string.Equals(_selectedNodePath, entry.NodePath, StringComparison.Ordinal);
        bool isHovered = _isNodeSelectionEnabled &&
                         !string.IsNullOrWhiteSpace(_hoveredNodePath) &&
                         string.Equals(_hoveredNodePath, entry.NodePath, StringComparison.Ordinal);

        if (!isSelected && !isHovered)
        {
            Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
            entry.HighlightBorder.BorderBrush = transparent;
            entry.HighlightBorder.Background = transparent;
            entry.HighlightBorder.BorderThickness = new Thickness(0);
            return;
        }

        Color accentColor = IsDark
            ? Color.FromArgb(245, 96, 165, 250)
            : Color.FromArgb(245, 37, 99, 235);

        if (isSelected)
        {
            entry.HighlightBorder.BorderBrush = GetOrCreateSolidBrush(accentColor);
            entry.HighlightBorder.BorderThickness = new Thickness(2);
            entry.HighlightBorder.Background = GetOrCreateSolidBrush(
                Color.FromArgb((byte)(IsDark ? 34 : 24), accentColor.R, accentColor.G, accentColor.B));
        }
        else
        {
            entry.HighlightBorder.BorderBrush = GetOrCreateSolidBrush(
                Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B));
            entry.HighlightBorder.BorderThickness = new Thickness(1.5);
            entry.HighlightBorder.Background = GetOrCreateSolidBrush(
                Color.FromArgb((byte)(IsDark ? 20 : 14), accentColor.R, accentColor.G, accentColor.B));
        }
    }

    private FrameworkElement WrapForNodeInspection(
        FrameworkElement element,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot)
    {
        bool isSelected = !string.IsNullOrWhiteSpace(_selectedNodePath) &&
                          string.Equals(_selectedNodePath, spec.NodePath, StringComparison.Ordinal);

        if (!_isNodeSelectionEnabled && !isSelected)
        {
            return element;
        }

        string summary = ResolveNodeInspectionSummary(spec, ctx);

        var highlightOverlay = new Border
        {
            CornerRadius = CornerSmall,
            Margin = isRoot ? new Thickness(0) : new Thickness(-2),
            IsHitTestVisible = false
        };

        var host = new Grid
        {
            HorizontalAlignment = element.HorizontalAlignment,
            VerticalAlignment = element.VerticalAlignment,
            Visibility = element.Visibility
        };
        if (!string.IsNullOrWhiteSpace(spec.When))
        {
            RegisterRetainedPatcher(_ => host.Visibility = element.Visibility);
        }

        if (_isNodeSelectionEnabled)
        {
            host.Background = GetOrCreateSolidBrush(Colors.Transparent);

            bool hasInspectableChildren =
                spec.Children.Count > 0 ||
                spec.ItemTemplate is not null ||
                spec.Pane is not null ||
                spec.ActionButton is not null ||
                spec.HoverActions.Count > 0;

            if (!hasInspectableChildren)
            {
                element.IsHitTestVisible = false;
            }
        }

        host.Children.Add(element);
        host.Children.Add(highlightOverlay);

        var entry = new InspectableNodeEntry(
            spec.NodePath,
            spec.Type,
            summary,
            isRoot,
            highlightOverlay);
        _inspectableNodes[host] = entry;
        ApplyNodeHighlightVisual(entry);

        return host;
    }

    internal static string ResolveNodeInspectionSummary(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string raw = spec.Title
                     ?? spec.Text
                     ?? spec.Placeholder
                     ?? spec.EmptyState?.Title
                     ?? spec.Bind
                     ?? spec.Collection
                     ?? spec.Items
                     ?? spec.Id
                     ?? string.Empty;
        string resolved = CustomWidgetExpressionEvaluator.Interpolate(raw, ctx).Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return spec.Type;
        }

        return resolved.Length > 24 ? resolved[..24] + "…" : resolved;
    }

    private FrameworkElement BuildSelect(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string currentVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        var combo = new ComboBox
        {
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            FontSize = CaptionFontSize,
            MinHeight = 30,
            CornerRadius = CornerSmall,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        string rawLabel = spec.Title ?? spec.Text ?? string.Empty;
        string label = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, ctx);
        if (!string.IsNullOrWhiteSpace(label))
        {
            AutomationProperties.SetName(combo, label);
        }

        var optionValues = new List<string>(spec.Options.Count);
        var optionLabels = new List<string>(spec.Options.Count);
        int selectedIndex = -1;
        for (int i = 0; i < spec.Options.Count; i++)
        {
            CustomWidgetOptionSpec opt = spec.Options[i];
            optionValues.Add(opt.Value);
            string resolvedOptLabel = CustomWidgetExpressionEvaluator.Interpolate(opt.Label, ctx);
            optionLabels.Add(resolvedOptLabel);
            combo.Items.Add(resolvedOptLabel);
            if (string.Equals(opt.Value, currentVal, StringComparison.Ordinal))
            {
                selectedIndex = i;
            }
        }

        bool isSyncing = true;
        if (selectedIndex >= 0)
        {
            combo.SelectedIndex = selectedIndex;
        }
        isSyncing = false;

        combo.DropDownOpened += (_, _) => BeginInteractiveSession(combo);
        combo.DropDownClosed += (_, _) => EndInteractiveSession(combo);
        combo.Unloaded += (_, _) => EndInteractiveSession(combo);
        MarkInteractiveElement(combo);

        TextBlock? labelTb = null;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        combo.SelectionChanged += (_, _) =>
        {
            if (isSyncing || combo.SelectedIndex < 0 || combo.SelectedIndex >= optionValues.Count)
            {
                return;
            }

            EndInteractiveSession(combo);
            string chosen = optionValues[combo.SelectedIndex];
            CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(chosen);
            FireAndForgetSafeUi(async () =>
            {
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
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (_activeInteractions.Contains(combo))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(spec.Placeholder))
            {
                string nextPlaceholder = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, liveCtx);
                if (!string.Equals(combo.PlaceholderText, nextPlaceholder, StringComparison.Ordinal))
                {
                    combo.PlaceholderText = nextPlaceholder;
                }
            }

            if (labelTb is not null)
            {
                string nextLabel = CustomWidgetExpressionEvaluator.Interpolate(rawLabel, liveCtx);
                if (!string.Equals(labelTb.Text, nextLabel, StringComparison.Ordinal))
                {
                    labelTb.Text = nextLabel;
                    AutomationProperties.SetName(combo, nextLabel);
                }
            }

            bool anyOptionLabelChanged = false;
            for (int i = 0; i < spec.Options.Count && i < combo.Items.Count; i++)
            {
                string nextOptLabel = CustomWidgetExpressionEvaluator.Interpolate(spec.Options[i].Label, liveCtx);
                if (!string.Equals(optionLabels[i], nextOptLabel, StringComparison.Ordinal))
                {
                    anyOptionLabelChanged = true;
                    break;
                }
            }

            string liveVal = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            int nextIdx = -1;
            for (int i = 0; i < optionValues.Count; i++)
            {
                if (string.Equals(optionValues[i], liveVal, StringComparison.Ordinal))
                {
                    nextIdx = i;
                    break;
                }
            }

            if (anyOptionLabelChanged || (nextIdx >= 0 && combo.SelectedIndex != nextIdx))
            {
                int targetIdx = nextIdx >= 0 ? nextIdx : combo.SelectedIndex;
                isSyncing = true;
                try
                {
                    if (anyOptionLabelChanged)
                    {
                        for (int i = 0; i < spec.Options.Count && i < combo.Items.Count; i++)
                        {
                            string nextOptLabel = CustomWidgetExpressionEvaluator.Interpolate(spec.Options[i].Label, liveCtx);
                            if (!string.Equals(optionLabels[i], nextOptLabel, StringComparison.Ordinal))
                            {
                                optionLabels[i] = nextOptLabel;
                                combo.Items[i] = nextOptLabel;
                            }
                        }
                    }

                    if (targetIdx >= 0 && combo.SelectedIndex != targetIdx)
                    {
                        combo.SelectedIndex = targetIdx;
                    }
                }
                finally
                {
                    isSyncing = false;
                }
            }
        });

        if (string.IsNullOrWhiteSpace(label))
        {
            return combo;
        }

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelTb = new TextBlock
        {
            Text = label,
            FontSize = BodyFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        combo.MinWidth = 110;
        Grid.SetColumn(combo, 1);
        row.Children.Add(labelTb);
        row.Children.Add(combo);
        return row;
    }

    private FrameworkElement BuildCheckBox(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawChecked = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);
        bool isChecked = string.Equals(rawChecked, "true", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(rawChecked, "1", StringComparison.Ordinal);

        string label = CustomWidgetExpressionEvaluator.Interpolate(spec.Text ?? spec.Title, ctx);
        var labelTb = new TextBlock
        {
            Text = label,
            FontSize = BodyFontSize,
            Foreground = isChecked && spec.Strikethrough
                ? ResolveForegroundBrush("secondary")
                : ResolveForegroundBrush("default"),
            TextDecorations = isChecked && spec.Strikethrough
                ? TextDecorations.Strikethrough
                : TextDecorations.None,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var checkBox = new CheckBox
        {
            IsChecked = isChecked,
            MinWidth = 0,
            MinHeight = 26,
            Padding = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = string.IsNullOrWhiteSpace(label) ? null : labelTb
        };
        AutomationProperties.SetName(checkBox, string.IsNullOrWhiteSpace(label) ? "CheckBox" : label);
        MarkInteractiveElement(checkBox);

        bool syncing = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        CancellationTokenSource? settleCts = null;
        bool userRevertedDuringSettle = false;
        bool unloadedDuringSettle = false;

        static (Border? RowBorder, TextBlock? RowTitleTb) FindListRowTargets(DependencyObject source)
        {
            DependencyObject? cur = VisualTreeHelper.GetParent(source);
            Grid? bodyGrid = null;
            Border? rowBorder = null;
            for (int depth = 0; cur is not null && depth < 6; depth++)
            {
                if (bodyGrid is null && cur is Grid g && g.ColumnDefinitions.Count >= 2)
                {
                    bodyGrid = g;
                }
                else if (cur is Border b && b.Child is Grid outerItemGrid && outerItemGrid.Children.Count > 0)
                {
                    rowBorder = b;
                    break;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }

            TextBlock? titleTb = null;
            if (bodyGrid is not null)
            {
                int starCol = -1;
                for (int c = 0; c < bodyGrid.ColumnDefinitions.Count; c++)
                {
                    if (bodyGrid.ColumnDefinitions[c].Width.IsStar)
                    {
                        starCol = c;
                        break;
                    }
                }

                foreach (UIElement child in bodyGrid.Children)
                {
                    if (child is FrameworkElement fe && (starCol < 0 || Grid.GetColumn(fe) == starCol))
                    {
                        if (fe is TextBlock tb)
                        {
                            titleTb = tb;
                            break;
                        }
                        if (fe is Panel p)
                        {
                            foreach (UIElement inner in p.Children)
                            {
                                if (inner is TextBlock innerTb && innerTb.Visibility == Visibility.Visible)
                                {
                                    titleTb = innerTb;
                                    break;
                                }
                            }
                            if (titleTb is not null) break;
                        }
                    }
                }
            }

            return (rowBorder, titleTb);
        }

        void CancelAndDisposeSettleCts()
        {
            if (settleCts is { } activeSettleCts)
            {
                settleCts = null;
                try { activeSettleCts.Cancel(); } catch { }
                try { activeSettleCts.Dispose(); } catch { }
            }
        }

        checkBox.Unloaded += (_, _) =>
        {
            if (settleCts is not null)
            {
                unloadedDuringSettle = true;
                CancelAndDisposeSettleCts();
            }
        };

        async void HandleCheckedChanged(bool nextValue)
        {
            if (syncing)
            {
                return;
            }

            if (!nextValue && settleCts is not null)
            {
                userRevertedDuringSettle = true;
                CancelAndDisposeSettleCts();
                (Border? rb, TextBlock? rt) = FindListRowTargets(checkBox);
                if (rb is not null) rb.Opacity = 1.0;
                if (rt is not null)
                {
                    rt.TextDecorations = TextDecorations.None;
                    rt.Foreground = ResolveForegroundBrush("default");
                }
                if (spec.Strikethrough)
                {
                    labelTb.TextDecorations = TextDecorations.None;
                    labelTb.Foreground = ResolveForegroundBrush("default");
                }
                return;
            }

            syncing = true;
            try
            {
                if (spec.Strikethrough)
                {
                    labelTb.TextDecorations = nextValue ? TextDecorations.Strikethrough : TextDecorations.None;
                    labelTb.Foreground = nextValue ? ResolveForegroundBrush("secondary") : ResolveForegroundBrush("default");
                }

                if (nextValue &&
                    spec.Strikethrough &&
                    checkBox.IsLoaded &&
                    checkBox.XamlRoot is not null &&
                    FluentMotionHelper.AreAnimationsEnabled)
                {
                    FluentMotionHelper.AnimateHoverScale(checkBox, 0.90f, durationMs: 85);
                    (Border? rowBorder, TextBlock? rowTitleTb) = FindListRowTargets(checkBox);
                    if (rowTitleTb is not null)
                    {
                        rowTitleTb.TextDecorations = TextDecorations.Strikethrough;
                        rowTitleTb.Foreground = ResolveForegroundBrush("secondary");
                    }
                    if (rowBorder is not null)
                    {
                        rowBorder.OpacityTransition ??= new Microsoft.UI.Xaml.ScalarTransition
                        {
                            Duration = TimeSpan.FromMilliseconds(160)
                        };
                        rowBorder.Opacity = 0.56;
                    }

                    userRevertedDuringSettle = false;
                    unloadedDuringSettle = false;
                    CancelAndDisposeSettleCts();
                    var currentSettleCts = new CancellationTokenSource();
                    settleCts = currentSettleCts;
                    CancellationToken token = currentSettleCts.Token;
                    syncing = false;
                    try
                    {
                        await Task.Delay(220, token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (userRevertedDuringSettle && !unloadedDuringSettle)
                        {
                            return;
                        }
                    }
                    finally
                    {
                        if (ReferenceEquals(settleCts, currentSettleCts))
                        {
                            settleCts = null;
                        }
                        try { currentSettleCts.Dispose(); } catch { }
                    }

                    syncing = true;
                    FluentMotionHelper.AnimateHoverScale(checkBox, 1.0f, durationMs: 95);
                    if (rowBorder is not null)
                    {
                        rowBorder.Opacity = 1.0;
                    }
                }

                string boolStr = nextValue ? "true" : "false";
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(boolStr);
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
                else if (spec.OnClick.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnClick, evCtx);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget] CheckBox state update failed: {ex.Message}");
            }
            finally
            {
                syncing = false;
            }
        }

        checkBox.Checked += (_, _) => HandleCheckedChanged(true);
        checkBox.Unchecked += (_, _) => HandleCheckedChanged(false);

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            string liveLabel = CustomWidgetExpressionEvaluator.Interpolate(spec.Text ?? spec.Title, liveCtx);
            if (!string.Equals(labelTb.Text, liveLabel, StringComparison.Ordinal))
            {
                labelTb.Text = liveLabel;
            }

            string liveCheckedRaw = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            bool liveChecked = string.Equals(liveCheckedRaw, "true", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(liveCheckedRaw, "1", StringComparison.Ordinal);

            if (checkBox.IsChecked != liveChecked)
            {
                syncing = true;
                try
                {
                    checkBox.IsChecked = liveChecked;
                    if (spec.Strikethrough)
                    {
                        labelTb.TextDecorations = liveChecked ? TextDecorations.Strikethrough : TextDecorations.None;
                        labelTb.Foreground = liveChecked ? ResolveForegroundBrush("secondary") : ResolveForegroundBrush("default");
                    }
                }
                finally
                {
                    syncing = false;
                }
            }
        });

        return checkBox;
    }

    private FrameworkElement BuildSlider(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        double min = spec.Min;
        double max = spec.Max > min ? spec.Max : min + 100;
        double step = spec.Step > 0 ? spec.Step : 1;
        double current = double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Clamp(parsed, min, max)
            : min;

        string rawTitle = spec.Title ?? spec.Text ?? string.Empty;
        string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);
        string title = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx);

        string FormatNumber(double v) =>
            step >= 1
                ? Math.Round(v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);

        string FormatDisplay(double v) => $"{FormatNumber(v)}{unit}";

        var valueTb = new TextBlock
        {
            Text = FormatDisplay(current),
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(valueTb);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = current,
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(slider, string.IsNullOrWhiteSpace(title) ? "Slider" : title);
        MarkInteractiveElement(slider);

        bool isUserDragging = false;
        bool isProgrammaticSync = false;
        string lastCommittedFormatted = FormatNumber(current);
        CustomWidgetEvaluationContext capturedCtx = ctx;

        async Task CommitSliderValueAsync(double targetVal)
        {
            string formattedNumber = FormatNumber(targetVal);
            if (string.Equals(formattedNumber, lastCommittedFormatted, StringComparison.Ordinal))
            {
                return;
            }
            lastCommittedFormatted = formattedNumber;

            CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(formattedNumber);
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

        slider.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) =>
            {
                isUserDragging = true;
                BeginInteractiveSession(slider);
            }),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler((_, _) =>
            {
                isUserDragging = false;
                EndInteractiveSession(slider);
            }),
            handledEventsToo: true);
        slider.Unloaded += (_, _) =>
        {
            isUserDragging = false;
            EndInteractiveSession(slider);
        };

        slider.ValueChanged += (_, e) =>
        {
            valueTb.Text = FormatDisplay(e.NewValue);
            if (isProgrammaticSync)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, FormatNumber(e.NewValue));
            }
        };

        async Task EndDragAndCommitAsync()
        {
            bool wasDragging = isUserDragging || _activeInteractions.Contains(slider);
            isUserDragging = false;
            EndInteractiveSession(slider);
            if (wasDragging)
            {
                await CommitSliderValueAsync(slider.Value);
            }
        }

        slider.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(async (_, _) => await EndDragAndCommitAsync()),
            handledEventsToo: true);
        slider.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(async (_, _) => await EndDragAndCommitAsync()),
            handledEventsToo: true);

        slider.KeyUp += (_, e) => FireAndForgetSafeUi(async () =>
        {
            if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
            {
                await CommitSliderValueAsync(slider.Value);
            }
        });

        TextBlock? titleTb = null;
        if (!string.IsNullOrWhiteSpace(title))
        {
            titleTb = new TextBlock
            {
                Text = title,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("default"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (titleTb is not null && ContainsDynamicToken(rawTitle))
            {
                titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
            }

            if (!isUserDragging && !_activeInteractions.Contains(slider))
            {
                string liveRaw = !string.IsNullOrWhiteSpace(bindKey)
                    ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                    : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
                if (double.TryParse(liveRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double liveVal))
                {
                    double clamped = Math.Clamp(liveVal, min, max);
                    if (Math.Abs(slider.Value - clamped) > 0.0001)
                    {
                        isProgrammaticSync = true;
                        try
                        {
                            slider.Value = clamped;
                            valueTb.Text = FormatDisplay(clamped);
                            lastCommittedFormatted = FormatNumber(clamped);
                        }
                        finally
                        {
                            isProgrammaticSync = false;
                        }
                    }
                }
            }
        });

        if (titleTb is null)
        {
            var inlineGrid = new Grid { ColumnSpacing = 8 };
            inlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inlineGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(valueTb, 1);
            inlineGrid.Children.Add(slider);
            inlineGrid.Children.Add(valueTb);
            return inlineGrid;
        }

        var stack = new StackPanel { Spacing = 2 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titleTb);
        Grid.SetColumn(valueTb, 1);
        header.Children.Add(valueTb);
        stack.Children.Add(header);
        stack.Children.Add(slider);
        return stack;
    }

    private FrameworkElement BuildNumberBox(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        double min = spec.Min;
        double max = spec.Max > min ? spec.Max : double.MaxValue;
        double step = spec.Step > 0 ? spec.Step : 1;
        double initial = double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Clamp(parsed, min, max)
            : min;

        var box = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 5,
            Value = initial,
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            CornerRadius = CornerSmall,
            FontSize = BodyFontSize,
            VerticalAlignment = VerticalAlignment.Center
        };

        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        AutomationProperties.SetName(box, string.IsNullOrWhiteSpace(title) ? "NumberBox" : title);

        box.GotFocus += (_, _) => BeginInteractiveSession(box);
        box.LostFocus += (_, _) => EndInteractiveSession(box);
        box.Unloaded += (_, _) => EndInteractiveSession(box);
        MarkInteractiveElement(box);

        bool syncing = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        box.ValueChanged += (_, e) => FireAndForgetSafeUi(async () =>
        {
            if (syncing || double.IsNaN(e.NewValue))
            {
                return;
            }

            syncing = true;
            try
            {
                EndInteractiveSession(box);
                string formatted = step >= 1
                    ? Math.Round(e.NewValue).ToString(CultureInfo.InvariantCulture)
                    : e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
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
            }
            finally
            {
                syncing = false;
            }
        });

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (_activeInteractions.Contains(box))
            {
                return;
            }

            string liveRaw = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            if (double.TryParse(liveRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double liveVal))
            {
                double clamped = Math.Clamp(liveVal, min, max);
                if (double.IsNaN(box.Value) || Math.Abs(box.Value - clamped) > 0.0001)
                {
                    syncing = true;
                    try
                    {
                        box.Value = clamped;
                    }
                    finally
                    {
                        syncing = false;
                    }
                }
            }
        });

        if (string.IsNullOrWhiteSpace(title))
        {
            return box;
        }

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        box.MinWidth = 104;
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        return row;
    }

    private FrameworkElement BuildTextArea(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string initialValue = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        bool isMonoEditor = string.Equals(spec.Style, "mono", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(spec.FontFamily, "mono", StringComparison.OrdinalIgnoreCase);
        var textBox = new TextBox
        {
            AcceptsReturn = true,
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = spec.MinHeight ?? 68,
            MaxHeight = spec.MaxHeight ?? 180,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = CornerSmall,
            FontSize = spec.FontSize is > 0
                ? SnapLegacyFontSizeToHostTier(spec.FontSize.Value)
                : (isMonoEditor ? Math.Max(10.5, BodyFontSize - 0.5) : BodyFontSize)
        };
        if (isMonoEditor)
        {
            textBox.FontFamily = MonoFontFamily;
        }
        if (ResolveExplicitFlowDirection(spec) is { } explicitDir)
        {
            textBox.FlowDirection = explicitDir;
        }
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);
        textBox.Text = initialValue;

        textBox.GotFocus += (_, _) => BeginInteractiveSession(textBox);
        textBox.LostFocus += (_, _) => EndInteractiveSession(textBox);
        textBox.Unloaded += (_, _) => EndInteractiveSession(textBox);
        MarkInteractiveElement(textBox);

        bool suppressSync = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        IReadOnlyList<CustomWidgetActionSpec> liveOnChangeActions = spec.OnChange.Count > 0
            ? FilterNonRedundantInputOnChangeActions(spec.OnChange, bindKey)
            : [];
        string lastCommittedText = textBox.Text ?? string.Empty;

        void CommitTextAreaValue(string currentText)
        {
            if (string.Equals(lastCommittedText, currentText, StringComparison.Ordinal))
            {
                return;
            }

            lastCommittedText = currentText;
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, currentText, notifyLiveBindings: true);
            }

            if (liveOnChangeActions.Count > 0)
            {
                _ = _viewModel.ExecuteActionsAsync(liveOnChangeActions, capturedCtx.WithEventValue(currentText));
            }
        }

        textBox.TextChanged += (_, _) =>
        {
            if (suppressSync)
            {
                return;
            }

            if (textBox.FocusState != FocusState.Unfocused)
            {
                BeginInteractiveSession(textBox);
            }

            string currentText = textBox.Text ?? string.Empty;
            if (DeskBox.Helpers.ImeCompositionGuard.IsActivelyComposing(textBox))
            {
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    _viewModel.SetStateValueWithoutRebuild(bindKey, currentText, notifyLiveBindings: false);
                }
                return;
            }

            CommitTextAreaValue(currentText);
        };
        textBox.TextCompositionEnded += (_, _) =>
        {
            if (suppressSync)
            {
                return;
            }
            CommitTextAreaValue(textBox.Text ?? string.Empty);
        };

        AttachWorkspaceEditorBehavior(textBox, spec);

        textBox.KeyDown += (_, e) =>
        {
            if (!e.Handled &&
                !DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e) &&
                e.Key == VirtualKey.Escape &&
                ClearTextInputFocus(textBox))
            {
                e.Handled = true;
            }
        };

        if (spec.OnSubmit.Count > 0)
        {
            textBox.KeyDown += (_, e) => FireAndForgetSafeUi(async () =>
            {
                if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
                {
                    return;
                }

                var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                bool ctrlDown = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (ctrlDown && e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    string submittedText = textBox.Text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(bindKey))
                    {
                        _viewModel.SetStateValueWithoutRebuild(bindKey, submittedText);
                    }
                    EndInteractiveSession(textBox);
                    await _viewModel.ExecuteActionsAsync(spec.OnSubmit, capturedCtx.WithEventValue(submittedText));
                }
            });
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (_activeInteractions.Contains(textBox) ||
                DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace())
            {
                return;
            }

            string liveVal = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx)
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
            if (!string.Equals(textBox.Text, liveVal, StringComparison.Ordinal))
            {
                suppressSync = true;
                try
                {
                    textBox.Text = liveVal;
                    lastCommittedText = liveVal;
                }
                finally
                {
                    suppressSync = false;
                }
            }
        });

        return textBox;
    }

    private FrameworkElement BuildIconButton(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string glyph = ResolveNormalizedIcon(spec.Icon, ctx)
            ?? CustomWidgetManifestParser.NormalizeGlyph(CustomWidgetExpressionEvaluator.Interpolate(spec.Text, ctx));
        if (string.IsNullOrWhiteSpace(glyph))
        {
            glyph = "\uE710";
        }

        double btnSize = spec.Size ?? (string.Equals(spec.Style, "sm", StringComparison.OrdinalIgnoreCase) ? 24 : 28);
        double iconFontSize = btnSize <= 24 ? 11.5 : 13.0;
        string? tooltip = string.IsNullOrWhiteSpace(spec.Tooltip)
            ? (!string.IsNullOrWhiteSpace(spec.Title) ? CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx) : null)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx);

        Brush? iconFg = string.Equals(spec.Variant, "danger", StringComparison.OrdinalIgnoreCase)
            ? ResolveForegroundBrush("danger")
            : string.Equals(spec.Variant, "accent", StringComparison.OrdinalIgnoreCase)
                ? GetBrush("TextOnAccentFillColorPrimaryBrush", IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White)
                : null;
        FrameworkElement iconEl = CreateInlineIconElement(glyph, iconFontSize, iconFg);
        var btn = new Button
        {
            Width = btnSize,
            Height = btnSize,
            MinWidth = btnSize,
            MinHeight = btnSize,
            Padding = new Thickness(0),
            CornerRadius = CornerSmall,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = iconEl
        };

        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            ToolTipService.SetToolTip(btn, tooltip);
            AutomationProperties.SetName(btn, tooltip);
        }

        if (string.Equals(spec.Variant, "accent", StringComparison.OrdinalIgnoreCase))
        {
            StyleAccentCommitButton(btn);
            btn.CornerRadius = CornerSmall;
        }
        else if (string.Equals(spec.Variant, "danger", StringComparison.OrdinalIgnoreCase))
        {
            Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
            btn.Background = transparent;
            btn.BorderBrush = transparent;
            btn.BorderThickness = new Thickness(0);
            if (iconEl is FontIcon fi)
            {
                fi.Foreground = ResolveForegroundBrush("danger");
            }
            ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        }
        else
        {
            Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
            btn.Background = transparent;
            btn.BorderBrush = transparent;
            btn.BorderThickness = new Thickness(0);
            ApplyButtonStateOverrides(btn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
        }

        ApplyRawButtonVisualOverrides(btn, labelTb: null, iconEl, spec, ctx);
        MarkInteractiveElement(btn);

        CustomWidgetEvaluationContext capturedCtx = ctx;
        string lastResolvedGlyph = glyph;
        Action? triggerCopyMorph = IsCopyActionNode(spec)
            ? AttachCopyMorphFeedback(
                btn,
                iconEl,
                null,
                null,
                null,
                () => ResolveNormalizedIcon(spec.Icon, capturedCtx) ?? glyph,
                iconFg)
            : null;
        if (spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover))
        {
            btn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                FluentMotionHelper.PlayGlyphMicroMotion(iconEl, lastResolvedGlyph);
                await ExecuteInteractiveClickWithPopoverAsync(spec, capturedCtx, btn);
                triggerCopyMorph?.Invoke();
            });
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (ContainsDynamicToken(spec.Icon) || ContainsDynamicToken(spec.Text))
            {
                string nextGlyph = ResolveNormalizedIcon(spec.Icon, liveCtx)
                    ?? CustomWidgetManifestParser.NormalizeGlyph(CustomWidgetExpressionEvaluator.Interpolate(spec.Text, liveCtx));
                string resolvedGlyph = string.IsNullOrWhiteSpace(nextGlyph) ? "\uE710" : nextGlyph;
                if (!string.Equals(resolvedGlyph, lastResolvedGlyph, StringComparison.Ordinal))
                {
                    lastResolvedGlyph = resolvedGlyph;
                    UpdateInlineIconGlyph(iconEl, resolvedGlyph);
                    FluentMotionHelper.PlayGlyphMorphTransition(iconEl);
                }
            }

            if (ContainsDynamicToken(spec.Tooltip) || ContainsDynamicToken(spec.Title))
            {
                string? nextTip = string.IsNullOrWhiteSpace(spec.Tooltip)
                    ? (!string.IsNullOrWhiteSpace(spec.Title) ? CustomWidgetExpressionEvaluator.Interpolate(spec.Title, liveCtx) : null)
                    : CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, liveCtx);
                if (!string.IsNullOrWhiteSpace(nextTip))
                {
                    ToolTipService.SetToolTip(btn, nextTip);
                }
            }
        });

        return btn;
    }

    private FrameworkElement BuildExpander(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        bool isExpanded = spec.DefaultExpanded;
        if (!string.IsNullOrWhiteSpace(bindKey))
        {
            string boundVal = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx);
            if (!string.IsNullOrWhiteSpace(boundVal))
            {
                isExpanded = string.Equals(boundVal, "true", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(boundVal, "1", StringComparison.Ordinal);
            }
        }

        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        string subtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx);

        var chevron = new FontIcon
        {
            Glyph = "\uE70D",
            FontSize = 11,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center,
            CenterPoint = new System.Numerics.Vector3(5.5f, 5.5f, 0f),
            Rotation = isExpanded ? 180f : 0f,
            RotationTransition = FluentMotionHelper.AreAnimationsEnabled
                ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) }
                : null
        };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string? expanderIcon = ResolveNormalizedIcon(spec.Icon, ctx);
        if (!string.IsNullOrWhiteSpace(expanderIcon))
        {
            UIElement iconEl = CreateInlineIconElement(expanderIcon, 13);
            if (iconEl is FrameworkElement iconFe)
            {
                iconFe.VerticalAlignment = VerticalAlignment.Center;
                iconFe.Margin = new Thickness(0, 0, 2, 0);
            }
            headerGrid.Children.Add(iconEl);
        }

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(titleText);
        TextBlock? subtitleText = null;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            subtitleText = new TextBlock
            {
                Text = subtitle,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(subtitleText);
        }
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        Grid.SetColumn(chevron, 2);
        headerGrid.Children.Add(chevron);

        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
        var headerBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 7, 10, 7),
            Background = transparent,
            BorderBrush = transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = CornerSmall,
            Content = headerGrid
        };
        AutomationProperties.SetName(headerBtn, string.IsNullOrWhiteSpace(title) ? "Expander" : title);
        ApplyButtonStateOverrides(headerBtn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);

        var bodyStack = new StackPanel
        {
            Spacing = spec.Spacing,
            Padding = new Thickness(10, 2, 10, 10),
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        foreach (CustomWidgetNodeSpec child in spec.Children)
        {
            bodyStack.Children.Add(BuildNode(child, ctx));
        }

        CustomWidgetEvaluationContext capturedCtx = ctx;
        headerBtn.Click += (_, _) =>
        {
            if (_isNodeSelectionEnabled)
            {
                return;
            }

            isExpanded = !isExpanded;
            bodyStack.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Rotation = isExpanded ? 180f : 0f;
            if (isExpanded)
            {
                FluentMotionHelper.PlayEntranceSlideAndFade(bodyStack, offsetY: -4f, durationMs: 140);
            }
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string nextVal = isExpanded ? "true" : "false";
                CustomWidgetEvaluationContext evCtx = capturedCtx;
                FireAndForgetSafeUi(() => _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec(ResolveBindSetActionType(bindKey), Key: bindKey, Value: nextVal)
                ], evCtx));
            }
        };

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            string liveTitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, liveCtx);
            if (!string.Equals(titleText.Text, liveTitle, StringComparison.Ordinal))
            {
                titleText.Text = liveTitle;
                AutomationProperties.SetName(headerBtn, string.IsNullOrWhiteSpace(liveTitle) ? "Expander" : liveTitle);
            }

            if (subtitleText is not null)
            {
                string liveSubtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, liveCtx);
                if (!string.Equals(subtitleText.Text, liveSubtitle, StringComparison.Ordinal))
                {
                    subtitleText.Text = liveSubtitle;
                }
            }

            if (string.IsNullOrWhiteSpace(bindKey))
            {
                return;
            }

            string boundVal = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx);
            if (string.IsNullOrWhiteSpace(boundVal))
            {
                return;
            }

            bool liveExpanded = string.Equals(boundVal, "true", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(boundVal, "1", StringComparison.Ordinal);
            if (liveExpanded != isExpanded)
            {
                isExpanded = liveExpanded;
                bodyStack.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
                chevron.Rotation = isExpanded ? 180f : 0f;
                if (isExpanded)
                {
                    FluentMotionHelper.PlayEntranceSlideAndFade(bodyStack, offsetY: -4f, durationMs: 140);
                }
            }
        });

        var outerStack = new StackPanel { Spacing = 0 };
        outerStack.Children.Add(headerBtn);
        outerStack.Children.Add(bodyStack);

        return new Border
        {
            CornerRadius = CornerMedium,
            Background = ResolveCardBackground(spec.Tone),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            Child = outerStack
        };
    }

    private FrameworkElement BuildHeatmapGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        int days = Math.Clamp(spec.Days, 7, 112);
        double cellSize = spec.Size ?? (days <= 28 ? 14 : 11);

        var wrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 4,
            VerticalSpacing = 4
        };

        var cells = new Border[days];
        for (int i = 0; i < days; i++)
        {
            var cell = new Border
            {
                Width = cellSize,
                Height = cellSize,
                CornerRadius = new CornerRadius(3)
            };
            cells[i] = cell;
            wrap.Children.Add(cell);
        }

        string? rawTitleSpec = spec.Title ?? spec.Text;
        TextBlock? titleBlock = null;
        FrameworkElement rootElement = wrap;
        if (!string.IsNullOrWhiteSpace(rawTitleSpec))
        {
            titleBlock = new TextBlock
            {
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var stack = new StackPanel { Spacing = 5 };
            stack.Children.Add(titleBlock);
            stack.Children.Add(wrap);
            rootElement = stack;
        }

        var dayTotals = new Dictionary<DateOnly, double>();
        void ApplyHeatmapState(CustomWidgetEvaluationContext evalCtx)
        {
            string tone = string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                ? "success"
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx);

            Brush baseBrush = ResolveSemanticBrush(tone);
            Color baseColor = baseBrush is SolidColorBrush scb
                ? scb.Color
                : (IsDark ? Color.FromArgb(255, 52, 211, 153) : Color.FromArgb(255, 16, 185, 129));
            Brush emptyCellBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));

            dayTotals.Clear();
            string? colName = spec.Collection ?? spec.Items;
            if (!string.IsNullOrWhiteSpace(colName))
            {
                string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(colName, evalCtx).Trim();
                string cleanCol = resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                    ? resolvedCol["collections.".Length..]
                    : resolvedCol;
                if (!string.IsNullOrWhiteSpace(cleanCol) &&
                    evalCtx.Collections.TryGetValue(cleanCol, out List<CustomWidgetRecord>? records) &&
                    records is not null)
                {
                    string? dateField = spec.DateField;
                    string? valField = spec.ValueField;
                    foreach (CustomWidgetRecord rec in records)
                    {
                        if (rec.IsDeleted)
                        {
                            continue;
                        }

                        DateOnly day = DateOnly.FromDateTime(rec.CreatedAtLocal.Date);
                        if (!string.IsNullOrWhiteSpace(dateField))
                        {
                            string rawDate = rec.GetField(dateField);
                            if (DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedDay))
                            {
                                day = parsedDay;
                            }
                            else if (DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
                            {
                                day = DateOnly.FromDateTime(dto.LocalDateTime.Date);
                            }
                        }

                        double amount = 1.0;
                        if (!string.IsNullOrWhiteSpace(valField))
                        {
                            string rawAmount = rec.GetField(valField);
                            if (double.TryParse(rawAmount, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedAmt))
                            {
                                amount = Math.Max(0, parsedAmt);
                            }
                        }

                        dayTotals[day] = dayTotals.GetValueOrDefault(day) + amount;
                    }
                }
            }

            double maxDayValue = 1.0;
            foreach (double v in dayTotals.Values)
            {
                if (v > maxDayValue)
                {
                    maxDayValue = v;
                }
            }

            DateOnly today = DateOnly.FromDateTime((evalCtx.ClockOverride ?? DateTimeOffset.Now).ToLocalTime().DateTime);
            for (int idx = 0; idx < days; idx++)
            {
                int offset = (days - 1) - idx;
                DateOnly d = today.AddDays(-offset);
                double val = dayTotals.GetValueOrDefault(d);
                Brush fill;
                if (val <= 0)
                {
                    fill = emptyCellBrush;
                }
                else
                {
                    double ratio = Math.Clamp(val / maxDayValue, 0.2, 1.0);
                    byte alpha = (byte)Math.Clamp((IsDark ? 70 : 55) + ratio * 175, 55, 240);
                    fill = GetOrCreateSolidBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
                }

                Border cell = cells[idx];
                if (!ReferenceEquals(cell.Background, fill))
                {
                    cell.Background = fill;
                }

                ToolTipService.SetToolTip(cell, val > 0
                    ? $"{d:MM-dd}: {val.ToString("0.##", CultureInfo.InvariantCulture)}"
                    : $"{d:MM-dd}: 0");
            }

            if (titleBlock is not null)
            {
                string nextTitle = CustomWidgetExpressionEvaluator.Interpolate(rawTitleSpec, evalCtx);
                titleBlock.Text = nextTitle;
                titleBlock.Visibility = string.IsNullOrWhiteSpace(nextTitle) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        ApplyHeatmapState(ctx);
        RegisterRetainedPatcher(ApplyHeatmapState);
        return rootElement;
    }

    private FrameworkElement BuildDropZone(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string culture = ctx.CultureName ?? _viewModel.ActiveCultureName;

        string icon = ResolveNormalizedIcon(spec.Icon, ctx) ?? "\uE896";
        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.FileDrop.Hint");
        }
        string subtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle ?? spec.Placeholder, ctx).Trim();

        Brush restBg = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0));
        Brush hoverBg = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        Brush restBorder = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(42, 0, 0, 0));
        Color accentColor = IsDark
            ? Color.FromArgb(230, 96, 165, 250)
            : Color.FromArgb(230, 37, 99, 235);
        Brush dragBorder = GetOrCreateSolidBrush(accentColor);
        Brush dragBg = GetOrCreateSolidBrush(
            Color.FromArgb((byte)(IsDark ? 34 : 22), accentColor.R, accentColor.G, accentColor.B));

        var innerStack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        FrameworkElement iconEl = CreateInlineIconElement(icon, 20);
        iconEl.HorizontalAlignment = HorizontalAlignment.Center;
        if (iconEl is FontIcon fi)
        {
            fi.Foreground = ResolveSemanticBrush("accent");
        }
        innerStack.Children.Add(iconEl);

        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        innerStack.Children.Add(titleTb);

        var subtitleTb = new TextBlock
        {
            Text = subtitle,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("secondary"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible
        };
        innerStack.Children.Add(subtitleTb);

        CustomWidgetEvaluationContext capturedCtx = ctx;
        if (_viewModel.LastTransformResult is { } lastTr)
        {
            var resultRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            string badgeText = lastTr.Succeeded
                ? (!string.IsNullOrWhiteSpace(lastTr.OutputName)
                    ? $"✓ {lastTr.OutputName} ({lastTr.SizeText})"
                    : $"✓ {lastTr.ResultText}")
                : $"⚠ {lastTr.ErrorMessage}";
            resultRow.Children.Add(new TextBlock
            {
                Text = badgeText.Length > 42 ? badgeText[..42] + "…" : badgeText,
                FontSize = Math.Max(9.5, CaptionFontSize - 1),
                Foreground = lastTr.Succeeded ? ResolveSemanticBrush("success") : ResolveSemanticBrush("warning"),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (lastTr.Succeeded && !string.IsNullOrWhiteSpace(lastTr.OutputPath))
            {
                string outPath = lastTr.OutputPath;
                Button revealBtn = CreateSubtleIconButton("\uE838", size: 22, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "Glance.Actions.ShowInExplorer"));
                revealBtn.Click += (_, _) => FireAndForgetSafeUi(() => _viewModel.RevealFileInExplorerAsync(outPath));
                resultRow.Children.Add(revealBtn);
            }
            else if (lastTr.Succeeded && !string.IsNullOrWhiteSpace(lastTr.ResultText))
            {
                string copyVal = lastTr.ResultText;
                Button copyBtn = CreateSubtleIconButton("\uE8C8", size: 22, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.FileDrop.CopyTooltip"));
                copyBtn.Click += (_, _) => FireAndForgetSafeUi(() => _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("clipboard.copy", Text: copyVal)
                ], capturedCtx));
                resultRow.Children.Add(copyBtn);
            }

            innerStack.Children.Add(resultRow);
        }

        var innerHost = new Grid();
        innerHost.Children.Add(innerStack);

        var zoneBorder = new Border
        {
            MinHeight = spec.MinHeight ?? 88,
            Padding = string.IsNullOrWhiteSpace(spec.Padding) ? new Thickness(14, 14, 14, 14) : ParseThickness(spec.Padding),
            CornerRadius = CornerLarge,
            Background = restBg,
            BorderBrush = restBorder,
            BorderThickness = new Thickness(1.5),
            AllowDrop = true,
            Child = innerHost
        };
        AutomationProperties.SetName(zoneBorder, title);

        zoneBorder.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(zoneBorder.BorderBrush, dragBorder))
            {
                zoneBorder.Background = hoverBg;
            }
        };
        zoneBorder.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(zoneBorder.BorderBrush, dragBorder))
            {
                zoneBorder.Background = restBg;
            }
        };

        string? effectiveAccept = !string.IsNullOrWhiteSpace(spec.Accept)
            ? CustomWidgetExpressionEvaluator.Interpolate(spec.Accept, ctx).Trim()
            : _viewModel.EffectiveDropAccept;

        zoneBorder.DragOver += (_, e) =>
        {
            if (DeskBoxDragData.CanAcceptCustomWidgetDrop(
                    e.DataView,
                    effectiveAccept,
                    e.AllowedOperations,
                    preferLinkForFiles: false,
                    out DataPackageOperation negotiated,
                    out string _))
            {
                e.AcceptedOperation = negotiated;
                zoneBorder.BorderBrush = dragBorder;
                zoneBorder.Background = dragBg;
                e.Handled = true;
            }
        };
        zoneBorder.DragLeave += (_, _) =>
        {
            zoneBorder.BorderBrush = restBorder;
            zoneBorder.Background = restBg;
        };
        zoneBorder.Drop += (_, e) => FireAndForgetSafeUi(async () =>
        {
            zoneBorder.BorderBrush = restBorder;
            zoneBorder.Background = restBg;
            if (!DeskBoxDragData.CanAcceptCustomWidgetDrop(
                    e.DataView,
                    effectiveAccept,
                    e.AllowedOperations,
                    preferLinkForFiles: false,
                    out DataPackageOperation expectedOp,
                    out string _))
            {
                return;
            }

            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                using CustomWidgetExtractedDropBatch? extracted =
                    await DeskBoxDragData.TryExtractCustomWidgetDropAsync(e.DataView, effectiveAccept);
                if (extracted is not null)
                {
                    bool handled = await _viewModel.HandleStructuredDropAsync(
                        extracted.Payload,
                        spec.OnDropActions,
                        nodeDropMode: spec.StorageMode,
                        nodeAcceptSpec: effectiveAccept);
                    e.AcceptedOperation = handled ? expectedOp : DataPackageOperation.None;
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget] DropZone drop failed: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        });

        zoneBorder.Tapped += (_, e) =>
        {
            if (IsInteractiveDescendant(e.OriginalSource as DependencyObject, zoneBorder))
            {
                return;
            }

            e.Handled = true;
            FireAndForgetSafeUi(async () =>
            {
                if (CustomWidgetDropAcceptHelper.AllowsFiles(effectiveAccept) &&
                    (spec.OnDropActions.Count > 0 || _viewModel.Manifest.OnDrop is not null))
                {
                    IntPtr hwnd = _viewModel.OwnerWindowHandleResolver?.Invoke() ?? IntPtr.Zero;
                    IReadOnlyList<string> picked = await FileOpenPickerService.PickFilesAsync(hwnd);
                    if (picked.Count > 0)
                    {
                        await _viewModel.HandleDropZoneFilesAsync(
                            picked,
                            spec.OnDropActions,
                            nodeDropMode: spec.StorageMode);
                    }
                }
                else if (spec.OnClick.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
                }
            });
        };

        string lastResolvedDropIcon = icon;
        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (ContainsDynamicToken(spec.Icon))
            {
                string nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx) ?? "\uE896";
                if (!string.Equals(nextIcon, lastResolvedDropIcon, StringComparison.Ordinal))
                {
                    lastResolvedDropIcon = nextIcon;
                    UpdateInlineIconGlyph(iconEl, nextIcon);
                }
            }

            string liveCulture = liveCtx.CultureName ?? _viewModel.ActiveCultureName;
            string liveTitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, liveCtx).Trim();
            if (string.IsNullOrWhiteSpace(liveTitle))
            {
                liveTitle = LocalizationService.TranslateForCulture(liveCulture, "CustomWidget.Renderer.FileDrop.Hint");
            }
            if (!string.Equals(titleTb.Text, liveTitle, StringComparison.Ordinal))
            {
                titleTb.Text = liveTitle;
                AutomationProperties.SetName(zoneBorder, liveTitle);
            }

            string liveSub = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle ?? spec.Placeholder, liveCtx).Trim();
            if (!string.Equals(subtitleTb.Text, liveSub, StringComparison.Ordinal))
            {
                subtitleTb.Text = liveSub;
                subtitleTb.Visibility = string.IsNullOrWhiteSpace(liveSub) ? Visibility.Collapsed : Visibility.Visible;
            }

            effectiveAccept = !string.IsNullOrWhiteSpace(spec.Accept)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.Accept, liveCtx).Trim()
                : _viewModel.EffectiveDropAccept;
        });

        return zoneBorder;
    }

    private FrameworkElement BuildControlTile(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        bool isActive = false;
        if (!string.IsNullOrWhiteSpace(bindKey))
        {
            string boundVal = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx);
            isActive = CustomWidgetExpressionEvaluator.IsTruthy(boundVal) ||
                       CustomWidgetExpressionEvaluator.EvaluateCondition(bindKey, ctx);
        }
        else if (!string.IsNullOrWhiteSpace(spec.Value))
        {
            isActive = CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Value, ctx) ||
                       CustomWidgetExpressionEvaluator.IsTruthy(CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx));
        }

        string icon = ResolveNormalizedIcon(spec.Icon, ctx) ?? "\uE713";
        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        string subtitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, ctx);

        Brush onAccentFg = GetBrush(
            "TextOnAccentFillColorPrimaryBrush",
            IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White);

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement iconEl = CreateInlineIconElement(icon, 16);
        iconEl.VerticalAlignment = VerticalAlignment.Center;
        if (iconEl is FontIcon fi)
        {
            fi.Foreground = isActive ? onAccentFg : ResolveForegroundBrush("default");
        }
        else if (iconEl is TextBlock iconTb && isActive)
        {
            iconTb.Foreground = onAccentFg;
            WidgetTextShadow.SetCast(iconTb, false);
        }
        Grid.SetColumn(iconEl, 0);
        grid.Children.Add(iconEl);

        var textStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleTb = new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = isActive ? onAccentFg : ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (isActive)
        {
            WidgetTextShadow.SetCast(titleTb, false);
        }
        textStack.Children.Add(titleTb);

        TextBlock? subTb = null;
        if (!string.IsNullOrWhiteSpace(spec.Subtitle))
        {
            subTb = new TextBlock
            {
                Text = subtitle,
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                Foreground = isActive ? onAccentFg : ResolveForegroundBrush("secondary"),
                Opacity = isActive ? 0.84 : 1.0,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible
            };
            if (isActive)
            {
                WidgetTextShadow.SetCast(subTb, false);
            }
            textStack.Children.Add(subTb);
        }

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        var tileBtn = new Button
        {
            MinHeight = spec.Height ?? 50,
            Padding = string.IsNullOrWhiteSpace(spec.Padding) ? new Thickness(12, 8, 12, 8) : ParseThickness(spec.Padding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = grid
        };
        AutomationProperties.SetName(tileBtn, string.IsNullOrWhiteSpace(title) ? "ControlTile" : title);
        MarkInteractiveElement(tileBtn);

        void ApplyTileVisualState(bool active)
        {
            if (active)
            {
                StyleAccentCommitButton(tileBtn);
                tileBtn.CornerRadius = CornerLarge;
            }
            else
            {
                StyleStandardCommandButton(tileBtn, CornerLarge);
            }

            if (iconEl is FontIcon fontIcon)
            {
                fontIcon.Foreground = active ? onAccentFg : ResolveForegroundBrush("default");
            }
            else if (iconEl is TextBlock iconTextBlock)
            {
                iconTextBlock.Foreground = active ? onAccentFg : ResolveForegroundBrush("default");
                WidgetTextShadow.SetCast(iconTextBlock, !active);
            }

            titleTb.Foreground = active ? onAccentFg : ResolveForegroundBrush("default");
            WidgetTextShadow.SetCast(titleTb, !active);
            if (subTb is not null)
            {
                subTb.Foreground = active ? onAccentFg : ResolveForegroundBrush("secondary");
                subTb.Opacity = active ? 0.84 : 1.0;
                WidgetTextShadow.SetCast(subTb, !active);
            }
        }

        ApplyTileVisualState(isActive);

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(tileBtn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        CustomWidgetEvaluationContext capturedCtx = ctx;
        tileBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (!string.IsNullOrWhiteSpace(bindKey) &&
                !bindKey.StartsWith("$", StringComparison.Ordinal))
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.toggle", Key: bindKey)
                ], capturedCtx);
            }

            if (spec.OnClick.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
            }
            else if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, capturedCtx);
            }
        });

        string lastResolvedTileIcon = icon;
        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (ContainsDynamicToken(spec.Icon))
            {
                string nextIcon = ResolveNormalizedIcon(spec.Icon, liveCtx) ?? "\uE713";
                if (!string.Equals(nextIcon, lastResolvedTileIcon, StringComparison.Ordinal))
                {
                    lastResolvedTileIcon = nextIcon;
                    UpdateInlineIconGlyph(iconEl, nextIcon);
                }
            }

            bool liveActive = false;
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                string boundVal = CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx);
                liveActive = CustomWidgetExpressionEvaluator.IsTruthy(boundVal) ||
                             CustomWidgetExpressionEvaluator.EvaluateCondition(bindKey, liveCtx);
            }
            else if (!string.IsNullOrWhiteSpace(spec.Value))
            {
                liveActive = CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Value, liveCtx) ||
                             CustomWidgetExpressionEvaluator.IsTruthy(CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx));
            }

            if (liveActive != isActive)
            {
                isActive = liveActive;
                ApplyTileVisualState(liveActive);
            }

            string liveTitle = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, liveCtx);
            if (!string.Equals(titleTb.Text, liveTitle, StringComparison.Ordinal))
            {
                titleTb.Text = liveTitle;
            }

            if (subTb is not null)
            {
                string liveSub = CustomWidgetExpressionEvaluator.Interpolate(spec.Subtitle, liveCtx);
                subTb.Text = liveSub;
                subTb.Visibility = string.IsNullOrWhiteSpace(liveSub) ? Visibility.Collapsed : Visibility.Visible;
            }
        });

        return tileBtn;
    }

    private FrameworkElement BuildImage(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawSrc = spec.Src ?? spec.FilePath ?? spec.Value ?? string.Empty;
        string initialSrc = CustomWidgetExpressionEvaluator.Interpolate(rawSrc, ctx).Trim();
        string fallbackGlyph = ResolveNormalizedIcon(spec.Icon, ctx) ?? "\uEB9F";

        double? explicitW = spec.Width ?? spec.Size;
        double? explicitH = spec.Height ?? spec.Size;
        double cornerRadius = Math.Clamp(spec.CornerRadius ?? 8, 0, 96);

        static Stretch ResolveImageStretch(string? rawStretchOrStyle, CustomWidgetEvaluationContext evalCtx)
        {
            string stretchRaw = CustomWidgetExpressionEvaluator.Interpolate(rawStretchOrStyle ?? "cover", evalCtx)
                .Trim()
                .ToLowerInvariant();
            return stretchRaw switch
            {
                "contain" or "uniform" => Stretch.Uniform,
                "fill" => Stretch.Fill,
                "none" => Stretch.None,
                _ => Stretch.UniformToFill
            };
        }

        string rawStretchSpec = spec.Stretch ?? spec.Style ?? "cover";
        Stretch stretch = ResolveImageStretch(rawStretchSpec, ctx);

        var fallbackIconEl = CreateInlineIconElement(
            fallbackGlyph,
            Math.Clamp((explicitH ?? 56) * 0.34, 16, 28),
            ResolveForegroundBrush("muted"));
        fallbackIconEl.HorizontalAlignment = HorizontalAlignment.Center;
        fallbackIconEl.VerticalAlignment = VerticalAlignment.Center;

        var fallbackHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = ResolveCardBackground("subtle")
        };
        fallbackHost.Children.Add(fallbackIconEl);

        var img = new Image
        {
            Stretch = stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var stage = new Grid();
        stage.Children.Add(fallbackHost);
        stage.Children.Add(img);

        img.ImageFailed += (_, _) =>
        {
            img.Visibility = Visibility.Collapsed;
            fallbackHost.Visibility = Visibility.Visible;
        };
        img.ImageOpened += (_, _) =>
        {
            fallbackHost.Visibility = Visibility.Collapsed;
            img.Visibility = Visibility.Visible;
        };

        string appliedSrc = "\0";
        double appliedScale = 0;
        int loadGeneration = 0;

        double ResolveDecodeRasterScale() =>
            Math.Clamp(
                img.XamlRoot?.RasterizationScale
                    ?? (_themeScope as UIElement)?.XamlRoot?.RasterizationScale
                    ?? Math.Max(1.0, DeskBox.Platform.Win32Helper.GetDpiForSystem() / 96.0),
                1.0,
                4.0);

        void ConfigureBitmapDecodeConstraint(Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap, double scale)
        {
            bitmap.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Physical;
            if (explicitW is > 0)
            {
                bitmap.DecodePixelWidth = Math.Clamp(
                    (int)Math.Ceiling(explicitW.Value * scale),
                    32,
                    1600);
            }
            else if (explicitH is > 0)
            {
                bitmap.DecodePixelHeight = Math.Clamp(
                    (int)Math.Ceiling(explicitH.Value * scale),
                    32,
                    1600);
            }
            else
            {
                bitmap.DecodePixelWidth = Math.Clamp(
                    (int)Math.Ceiling(640.0 * scale),
                    128,
                    1600);
            }
        }

        async Task LoadRemoteHttpsImageAsync(Uri httpsUri, int generation)
        {
            try
            {
                byte[]? imageBytes = await _viewModel.FetchValidatedRemoteImageBytesAsync(httpsUri);
                if (generation != loadGeneration || imageBytes is not { Length: > 0 })
                {
                    if (generation == loadGeneration)
                    {
                        img.Source = null;
                        img.Visibility = Visibility.Collapsed;
                        fallbackHost.Visibility = Visibility.Visible;
                    }
                    return;
                }

                using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await System.IO.WindowsRuntimeStreamExtensions.AsStreamForWrite(ras).WriteAsync(imageBytes);
                ras.Seek(0);

                double liveScale = ResolveDecodeRasterScale();
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                ConfigureBitmapDecodeConstraint(bitmap, liveScale);
                await bitmap.SetSourceAsync(ras);

                if (generation == loadGeneration)
                {
                    appliedScale = liveScale;
                    img.Source = bitmap;
                    img.Visibility = Visibility.Visible;
                    fallbackHost.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                if (generation == loadGeneration)
                {
                    img.Source = null;
                    img.Visibility = Visibility.Collapsed;
                    fallbackHost.Visibility = Visibility.Visible;
                }
            }
        }

        void ApplyImageSource(string candidate, bool forceScaleRefresh = false)
        {
            string trimmed = candidate.Trim();
            double currentScale = ResolveDecodeRasterScale();
            if (!forceScaleRefresh &&
                string.Equals(trimmed, appliedSrc, StringComparison.Ordinal) &&
                Math.Abs(currentScale - appliedScale) <= 0.01)
            {
                return;
            }
            appliedSrc = trimmed;
            appliedScale = currentScale;
            int currentGen = ++loadGeneration;

            Uri? resolvedUri = null;
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                string authorizedLocalPath = _viewModel.ResolveAuthorizedImagePath(trimmed);
                if (!string.IsNullOrWhiteSpace(authorizedLocalPath) && System.IO.File.Exists(authorizedLocalPath))
                {
                    Uri.TryCreate(System.IO.Path.GetFullPath(authorizedLocalPath), UriKind.Absolute, out resolvedUri);
                }
                else if (CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl(trimmed, out Uri? httpsUri, out _))
                {
                    resolvedUri = httpsUri;
                }
            }

            if (resolvedUri is null)
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
                fallbackHost.Visibility = Visibility.Visible;
                return;
            }

            if (string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
                fallbackHost.Visibility = Visibility.Visible;
                _ = LoadRemoteHttpsImageAsync(resolvedUri, currentGen);
                return;
            }

            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                ConfigureBitmapDecodeConstraint(bitmap, currentScale);
                if (resolvedUri.IsAbsoluteUri && resolvedUri.IsFile)
                {
                    img.Source = bitmap;
                    img.Visibility = Visibility.Visible;
                    fallbackHost.Visibility = Visibility.Collapsed;
                    WidgetImageStreamLoader.Fill(bitmap, resolvedUri.LocalPath, ok =>
                    {
                        if (!ok && currentGen == loadGeneration)
                        {
                            img.Source = null;
                            img.Visibility = Visibility.Collapsed;
                            fallbackHost.Visibility = Visibility.Visible;
                        }
                    });
                }
                else
                {
                    bitmap.UriSource = resolvedUri;
                    img.Source = bitmap;
                    img.Visibility = Visibility.Visible;
                    fallbackHost.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
                fallbackHost.Visibility = Visibility.Visible;
            }
        }

        Windows.Foundation.TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? xamlRootChangedHandler = null;
        XamlRoot? subscribedXamlRoot = null;

        void RefreshImageForScaleIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(appliedSrc) || appliedSrc == "\0")
            {
                return;
            }

            if (Math.Abs(ResolveDecodeRasterScale() - appliedScale) > 0.01)
            {
                ApplyImageSource(appliedSrc, forceScaleRefresh: true);
            }
        }

        img.Loaded += (_, _) =>
        {
            XamlRoot? root = img.XamlRoot;
            if (!ReferenceEquals(subscribedXamlRoot, root))
            {
                if (subscribedXamlRoot is not null && xamlRootChangedHandler is not null)
                {
                    subscribedXamlRoot.Changed -= xamlRootChangedHandler;
                }

                subscribedXamlRoot = root;
                if (root is not null)
                {
                    xamlRootChangedHandler ??= (_, _) => RefreshImageForScaleIfNeeded();
                    root.Changed += xamlRootChangedHandler;
                }
            }

            RefreshImageForScaleIfNeeded();
        };

        img.Unloaded += (_, _) =>
        {
            if (subscribedXamlRoot is not null && xamlRootChangedHandler is not null)
            {
                subscribedXamlRoot.Changed -= xamlRootChangedHandler;
            }

            subscribedXamlRoot = null;
        };

        ApplyImageSource(initialSrc);

        CustomWidgetEvaluationContext capturedCtx = ctx;
        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            if (ContainsDynamicToken(rawSrc))
            {
                ApplyImageSource(CustomWidgetExpressionEvaluator.Interpolate(rawSrc, liveCtx));
            }
            if (ContainsDynamicToken(rawStretchSpec))
            {
                img.Stretch = ResolveImageStretch(rawStretchSpec, liveCtx);
            }
        });

        var container = new Border
        {
            CornerRadius = new CornerRadius(cornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            Child = stage
        };

        if (explicitW is > 0)
        {
            container.Width = explicitW.Value;
        }
        if (explicitH is > 0)
        {
            container.Height = explicitH.Value;
        }
        else if (explicitW is null)
        {
            container.MaxHeight = spec.MaxHeight ?? 160;
            container.MinHeight = spec.MinHeight ?? 48;
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(container, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        if (spec.OnClick.Count > 0)
        {
            MarkInteractiveElement(container);
            container.Tapped += (_, e) => FireAndForgetSafeUi(async () =>
            {
                e.Handled = true;
                await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
            });
        }

        return container;
    }

    private FrameworkElement BuildTabs(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx,
        bool isRoot = false)
    {
        if (spec.Children.Count == 0)
        {
            return new Grid { Visibility = Visibility.Collapsed };
        }

        string bindKey = spec.Bind ?? string.Empty;
        string initialBound = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx).Trim()
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx).Trim();

        int activeIndex = 0;
        var tabKeys = new List<string>(spec.Children.Count);
        for (int i = 0; i < spec.Children.Count; i++)
        {
            CustomWidgetNodeSpec child = spec.Children[i];
            string key = !string.IsNullOrWhiteSpace(child.Id)
                ? child.Id!
                : !string.IsNullOrWhiteSpace(child.Value)
                    ? child.Value!
                    : i < spec.Options.Count && !string.IsNullOrWhiteSpace(spec.Options[i].Value)
                        ? spec.Options[i].Value
                        : i.ToString(CultureInfo.InvariantCulture);
            tabKeys.Add(key);

            if (!string.IsNullOrWhiteSpace(initialBound) &&
                (string.Equals(initialBound, key, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(initialBound, i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
            {
                activeIndex = i;
            }
        }

        var tabBarGrid = new Grid { ColumnSpacing = 2 };
        var tabButtons = new List<Button>(spec.Children.Count);
        var tabLabelBlocks = new List<(string RawLabel, TextBlock Tb)>(spec.Children.Count);
        var tabIconEntries = new List<(string RawIcon, FrameworkElement IconEl)>();
        var panesHost = new Grid();
        var panes = new List<UIElement>(spec.Children.Count);

        for (int i = 0; i < spec.Children.Count; i++)
        {
            tabBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            CustomWidgetNodeSpec child = spec.Children[i];

            string rawTabLabel = child.Title
                ?? (i < spec.Options.Count ? spec.Options[i].Label : null)
                ?? child.Text
                ?? $"Tab {i + 1}";
            string tabLabel = CustomWidgetExpressionEvaluator.Interpolate(rawTabLabel, ctx);
            string? rawTabIcon = child.Icon ?? (i < spec.Options.Count ? spec.Options[i].Icon : null);
            string? tabIcon = ResolveNormalizedIcon(rawTabIcon, ctx);
            bool hasDynamicTabIcon = ContainsDynamicToken(rawTabIcon);

            TextBlock labelTb;
            UIElement btnContent;
            if (!string.IsNullOrWhiteSpace(tabIcon) || hasDynamicTabIcon)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                FrameworkElement tabIconEl = CreateInlineIconElement(string.IsNullOrWhiteSpace(tabIcon) ? "\uE71D" : tabIcon, 12);
                tabIconEl.Visibility = string.IsNullOrWhiteSpace(tabIcon) ? Visibility.Collapsed : Visibility.Visible;
                row.Children.Add(tabIconEl);
                if (hasDynamicTabIcon && rawTabIcon is not null)
                {
                    tabIconEntries.Add((rawTabIcon, tabIconEl));
                }
                labelTb = new TextBlock
                {
                    Text = tabLabel,
                    FontSize = CaptionFontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                row.Children.Add(labelTb);
                btnContent = row;
            }
            else
            {
                labelTb = new TextBlock
                {
                    Text = tabLabel,
                    FontSize = CaptionFontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                btnContent = labelTb;
            }
            tabLabelBlocks.Add((rawTabLabel, labelTb));

            var tabBtn = new Button
            {
                Content = btnContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = CornerSmall,
                Height = 28,
                MinHeight = 28,
                MinWidth = 0,
                Padding = new Thickness(8, 0, 8, 0)
            };
            AutomationProperties.SetName(tabBtn, tabLabel);
            ApplySegmentedItemState(tabBtn, i == activeIndex);
            Grid.SetColumn(tabBtn, i);
            tabBarGrid.Children.Add(tabBtn);
            tabButtons.Add(tabBtn);

            UIElement builtPane = BuildNode(child, ctx);
            builtPane.Visibility = i == activeIndex ? Visibility.Visible : Visibility.Collapsed;
            panesHost.Children.Add(builtPane);
            panes.Add(builtPane);
        }

        CustomWidgetEvaluationContext capturedCtx = ctx;
        for (int i = 0; i < tabButtons.Count; i++)
        {
            int capturedIdx = i;
            string capturedKey = tabKeys[i];
            MarkInteractiveElement(tabButtons[i]);
            tabButtons[i].Click += (_, _) =>
            {
                if (_isNodeSelectionEnabled || activeIndex == capturedIdx)
                {
                    return;
                }
                activeIndex = capturedIdx;
                for (int j = 0; j < panes.Count; j++)
                {
                    panes[j].Visibility = j == activeIndex ? Visibility.Visible : Visibility.Collapsed;
                    ApplySegmentedItemState(tabButtons[j], j == activeIndex);
                }
                FluentMotionHelper.PlayEntranceSlideAndFade(panes[activeIndex], offsetY: 4f, durationMs: 140);

                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(capturedKey);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    _viewModel.SetStateValueWithoutRebuild(bindKey, capturedKey);
                }
                if (spec.OnChange.Count > 0)
                {
                    FireAndForgetSafeUi(() => _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx));
                }
            };
        }

        RegisterRetainedPatcher(liveCtx =>
        {
            capturedCtx = liveCtx;
            for (int t = 0; t < tabLabelBlocks.Count && t < tabButtons.Count; t++)
            {
                (string rawLbl, TextBlock tb) = tabLabelBlocks[t];
                string nextLbl = CustomWidgetExpressionEvaluator.Interpolate(rawLbl, liveCtx);
                if (!string.Equals(tb.Text, nextLbl, StringComparison.Ordinal))
                {
                    tb.Text = nextLbl;
                    AutomationProperties.SetName(tabButtons[t], nextLbl);
                }
            }

            foreach ((string rawIco, FrameworkElement iconEl) in tabIconEntries)
            {
                string? nextIco = ResolveNormalizedIcon(rawIco, liveCtx);
                iconEl.Visibility = string.IsNullOrWhiteSpace(nextIco) ? Visibility.Collapsed : Visibility.Visible;
                if (!string.IsNullOrWhiteSpace(nextIco))
                {
                    UpdateInlineIconGlyph(iconEl, nextIco);
                }
            }

            string liveBound = !string.IsNullOrWhiteSpace(bindKey)
                ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, liveCtx).Trim()
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx).Trim();
            if (string.IsNullOrWhiteSpace(liveBound))
            {
                return;
            }

            int nextIdx = -1;
            for (int k = 0; k < tabKeys.Count; k++)
            {
                if (string.Equals(liveBound, tabKeys[k], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(liveBound, k.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    nextIdx = k;
                    break;
                }
            }

            if (nextIdx >= 0 && nextIdx != activeIndex)
            {
                activeIndex = nextIdx;
                for (int j = 0; j < panes.Count; j++)
                {
                    panes[j].Visibility = j == activeIndex ? Visibility.Visible : Visibility.Collapsed;
                    ApplySegmentedItemState(tabButtons[j], j == activeIndex);
                }
                FluentMotionHelper.PlayEntranceSlideAndFade(panes[activeIndex], offsetY: 4f, durationMs: 140);
            }
        });

        var tabHeaderBorder = new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(2),
            Background = ResolveCardBackground("subtle"),
            BorderThickness = new Thickness(1),
            BorderBrush = DividerBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = tabBarGrid
        };

        bool hasAnyPaneContent = spec.Children.Any(c =>
            c.Children.Count > 0 ||
            !string.Equals(c.Type, "Tab", StringComparison.OrdinalIgnoreCase));

        var rootStack = new StackPanel
        {
            Spacing = spec.Spacing > 0
                ? ResolveEffectiveSpacing(spec, isHorizontal: false)
                : Math.Max(8, ResolveEffectiveSpacing(spec, isHorizontal: false, defaultSpacing: 8)),
            Padding = isRoot
                ? ResolveEffectiveRootSafePadding(spec.Padding)
                : ParseThickness(spec.Padding)
        };
        rootStack.Children.Add(tabHeaderBorder);
        if (hasAnyPaneContent)
        {
            rootStack.Children.Add(panesHost);
        }

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = WrapInShadowHostGrid(rootStack)
            };
        }

        return rootStack;
    }

    private FrameworkElement BuildRingGauge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double defaultDiameter = Math.Round(72.0 * EffectiveAppearance.DensityMultiplier);
        double size = Math.Clamp(spec.Size ?? spec.Width ?? spec.Height ?? defaultDiameter, 44, 150);
        bool isFullCircle = string.Equals(spec.Style, "full", StringComparison.OrdinalIgnoreCase);
        string rawTitle = spec.Title ?? spec.Text ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;

        CustomWidgetNodeSpec radialSpec = spec with
        {
            Size = size,
            GaugeThickness = Math.Clamp(Math.Round(size * 0.10), 5, 12),
            StartAngle = isFullCircle ? -90.0 : 135.0,
            SweepAngle = isFullCircle ? 359.9 : 270.0,
            ShowTicks = false,
            IsInteractive = false,
            FontSize = size >= 84 ? MetricFontSize : SubtitleFontSize,
            Title = null,
            Subtitle = null,
            Text = null,
            Progress = spec.Progress ?? spec.Value ?? "0",
            Value = null,
            Children = []
        };

        FrameworkElement dialStage = BuildRadialGauge(radialSpec, ctx);

        string resolvedTitle = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx);
        string resolvedSub = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx);
        TextBlock? titleTb = !string.IsNullOrWhiteSpace(rawTitle)
            ? new TextBlock
            {
                Text = resolvedTitle,
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = string.IsNullOrWhiteSpace(resolvedTitle) ? Visibility.Collapsed : Visibility.Visible
            }
            : null;

        TextBlock? subtitleTb = !string.IsNullOrWhiteSpace(rawSubtitle)
            ? new TextBlock
            {
                Text = resolvedSub,
                FontSize = Math.Max(9.5, CaptionFontSize - 0.5),
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = string.IsNullOrWhiteSpace(resolvedSub) ? Visibility.Collapsed : Visibility.Visible
            }
            : null;

        if (titleTb is null && subtitleTb is null)
        {
            return dialStage;
        }

        if (ContainsDynamicToken(rawTitle) || ContainsDynamicToken(rawSubtitle))
        {
            RegisterRetainedPatcher(liveCtx =>
            {
                if (titleTb is not null)
                {
                    string nextTitle = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
                    titleTb.Text = nextTitle;
                    titleTb.Visibility = string.IsNullOrWhiteSpace(nextTitle) ? Visibility.Collapsed : Visibility.Visible;
                }
                if (subtitleTb is not null)
                {
                    string nextSub = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
                    subtitleTb.Text = nextSub;
                    subtitleTb.Visibility = string.IsNullOrWhiteSpace(nextSub) ? Visibility.Collapsed : Visibility.Visible;
                }
            });
        }

        var wrapper = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        wrapper.Children.Add(dialStage);
        if (titleTb is not null)
        {
            wrapper.Children.Add(titleTb);
        }
        if (subtitleTb is not null)
        {
            wrapper.Children.Add(subtitleTb);
        }
        return wrapper;
    }

    private FrameworkElement BuildBarList(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string rawCol = (spec.Collection ?? spec.Items ?? string.Empty).Trim();
        int maxRows = spec.MaxLines > 0 ? Math.Clamp(spec.MaxLines, 1, 30) : 8;
        string titleField = string.IsNullOrWhiteSpace(spec.TitleField) ? "title" : spec.TitleField!;
        string valueField = string.IsNullOrWhiteSpace(spec.ValueField) ? "value" : spec.ValueField!;
        string? subtitleField = spec.SubtitleField;
        string? colorField = spec.ColorField;

        var host = new Grid();
        var listStack = new StackPanel
        {
            Spacing = Math.Max(4, spec.Spacing > 0 && spec.Spacing != 8 ? spec.Spacing : 6)
        };
        FrameworkElement emptyElement = spec.EmptyState is not null
            ? BuildEmptyState(spec.EmptyState, ctx)
            : new TextBlock
            {
                Text = LocalizationService.TranslateForCulture(
                    ctx.CultureName ?? _viewModel.ActiveCultureName,
                    "CustomWidget.Renderer.List.EmptyState"),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                Margin = new Thickness(0, 4, 0, 4)
            };
        host.Children.Add(emptyElement);
        host.Children.Add(listStack);

        string? lastSignature = null;
        void PopulateBarRows(CustomWidgetEvaluationContext evalCtx)
        {
            List<CustomWidgetRecord> activeRecords;
            if (TryResolveComputedRecords(rawCol, evalCtx, out IReadOnlyList<CustomWidgetRecord> computedRecords))
            {
                activeRecords = computedRecords
                    .Where(static r => !r.IsDeleted)
                    .ToList();
            }
            else
            {
                string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(rawCol, evalCtx).Trim();
                if (resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedCol = resolvedCol["collections.".Length..];
                }

                evalCtx.Collections.TryGetValue(resolvedCol, out List<CustomWidgetRecord>? allRecords);
                activeRecords = allRecords?
                    .Where(static r => !r.IsDeleted)
                    .ToList() ?? [];
            }

            if (!string.IsNullOrWhiteSpace(spec.Filter) && activeRecords.Count > 0)
            {
                var filteredRecords = new List<CustomWidgetRecord>(activeRecords.Count);
                for (int fi = 0; fi < activeRecords.Count; fi++)
                {
                    CustomWidgetRecord candidate = activeRecords[fi];
                    if (CustomWidgetExpressionEvaluator.EvaluateCondition(spec.Filter, evalCtx.WithItem(candidate, fi)))
                    {
                        filteredRecords.Add(candidate);
                    }
                }
                activeRecords = filteredRecords;
            }

            if (activeRecords.Count == 0)
            {
                if (lastSignature == "__empty__")
                {
                    return;
                }
                lastSignature = "__empty__";
                foreach (UIElement old in listStack.Children)
                {
                    ForgetRetainedSubtree(old);
                }
                listStack.Children.Clear();
                listStack.Visibility = Visibility.Collapsed;
                emptyElement.Visibility = Visibility.Visible;
                return;
            }

            string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, evalCtx);
            string tone = string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
                ? "accent"
                : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, evalCtx);

            var parsedRows = new List<(CustomWidgetRecord Record, int Index, string Label, string Subtitle, string ValueDisplay, double NumValue, string ItemColor)>(Math.Min(maxRows, activeRecords.Count));
            double computedMax = 0;
            var sig = new System.Text.StringBuilder(activeRecords.Count * 32);
            sig.Append(tone).Append('|').Append(unit).Append('|');

            for (int i = 0; i < activeRecords.Count && parsedRows.Count < maxRows; i++)
            {
                CustomWidgetRecord rec = activeRecords[i];
                string label = rec.GetField(titleField);
                if (string.IsNullOrWhiteSpace(label)) label = rec.GetField("name");
                if (string.IsNullOrWhiteSpace(label)) label = rec.GetField("label");

                string rawValStr = rec.GetField(valueField);
                if (string.IsNullOrWhiteSpace(rawValStr)) rawValStr = rec.GetField("percent");
                if (string.IsNullOrWhiteSpace(rawValStr)) rawValStr = rec.GetField("count");

                double.TryParse(rawValStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double numVal);
                computedMax = Math.Max(computedMax, numVal);

                string sub = !string.IsNullOrWhiteSpace(subtitleField) ? rec.GetField(subtitleField!) : string.Empty;
                string displayVal = !string.IsNullOrWhiteSpace(spec.Text)
                    ? CustomWidgetExpressionEvaluator.Interpolate(spec.Text, evalCtx.WithItem(rec, i))
                    : (!string.IsNullOrWhiteSpace(rawValStr) ? $"{rawValStr}{unit}" : string.Empty);
                string itemCol = !string.IsNullOrWhiteSpace(colorField)
                    ? rec.GetField(colorField!)
                    : (string.IsNullOrWhiteSpace(spec.Color) ? rec.GetField("color") : string.Empty);

                sig.Append(rec.Id).Append(':').Append(label).Append(':').Append(sub).Append(':')
                    .Append(displayVal).Append(':').Append(numVal.ToString("G7", CultureInfo.InvariantCulture))
                    .Append(':').Append(itemCol).Append(';');
                parsedRows.Add((rec, i, label, sub, displayVal, Math.Max(0, numVal), itemCol));
            }

            string nextSig = sig.ToString();
            if (string.Equals(lastSignature, nextSig, StringComparison.Ordinal))
            {
                return;
            }
            lastSignature = nextSig;

            emptyElement.Visibility = Visibility.Collapsed;
            listStack.Visibility = Visibility.Visible;
            foreach (UIElement old in listStack.Children)
            {
                ForgetRetainedSubtree(old);
            }
            listStack.Children.Clear();

            Brush defaultBarBrush = ResolveSemanticBrush(tone);
            Color defaultBarColor = defaultBarBrush is SolidColorBrush scb
                ? scb.Color
                : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
            Brush defaultFillBrush = GetOrCreateSolidBrush(
                Color.FromArgb((byte)(IsDark ? 46 : 30), defaultBarColor.R, defaultBarColor.G, defaultBarColor.B));
            Brush rowTrackBrush = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));

            double effectiveMax = spec.Max > 0 && spec.Max != 100
                ? Math.Max(spec.Max, computedMax)
                : (computedMax > 0 ? computedMax : 100);

            foreach (var item in parsedRows)
            {
                double ratio = Math.Clamp(item.NumValue / effectiveMax, 0.0, 1.0);
                Brush rowBarFill = defaultFillBrush;
                if (!string.IsNullOrWhiteSpace(item.ItemColor))
                {
                    Brush resolvedItemBrush = ResolveAnyColorBrush(item.ItemColor, evalCtx, tone);
                    if (resolvedItemBrush is SolidColorBrush iscb)
                    {
                        Color ic = iscb.Color;
                        rowBarFill = GetOrCreateSolidBrush(
                            Color.FromArgb((byte)(IsDark ? 52 : 34), ic.R, ic.G, ic.B));
                    }
                }

                var barGrid = new Grid
                {
                    MinHeight = 28,
                    CornerRadius = CornerSmall,
                    Background = rowTrackBrush
                };
                barGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(0.001, ratio), GridUnitType.Star)
                });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(Math.Max(0.001, 1.0 - ratio), GridUnitType.Star)
                });

                if (ratio > 0.005)
                {
                    var fillBorder = new Border
                    {
                        CornerRadius = CornerSmall,
                        Background = rowBarFill,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    Grid.SetColumn(fillBorder, 0);
                    barGrid.Children.Add(fillBorder);
                }

                var contentRow = new Grid
                {
                    Padding = new Thickness(10, 5, 10, 5),
                    ColumnSpacing = 8
                };
                contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var leftTextStack = new StackPanel
                {
                    Spacing = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                leftTextStack.Children.Add(new TextBlock
                {
                    Text = item.Label,
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveForegroundBrush("default"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (!string.IsNullOrWhiteSpace(item.Subtitle))
                {
                    leftTextStack.Children.Add(new TextBlock
                    {
                        Text = item.Subtitle,
                        FontSize = Math.Max(9.5, CaptionFontSize - 1),
                        Foreground = ResolveForegroundBrush("secondary"),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }
                Grid.SetColumn(leftTextStack, 0);
                contentRow.Children.Add(leftTextStack);

                var rightValTb = new TextBlock
                {
                    Text = item.ValueDisplay,
                    FontSize = CaptionFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResolveForegroundBrush("default"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                SetTabularNumerals(rightValTb);
                Grid.SetColumn(rightValTb, 1);
                contentRow.Children.Add(rightValTb);

                Grid.SetColumnSpan(contentRow, 2);
                barGrid.Children.Add(contentRow);

                if (spec.OnClick.Count > 0 || spec.OnItemClick.Count > 0)
                {
                    IReadOnlyList<CustomWidgetActionSpec> rowActions = spec.OnItemClick.Count > 0 ? spec.OnItemClick : spec.OnClick;
                    CustomWidgetEvaluationContext itemCtx = evalCtx.WithItem(item.Record, item.Index);
                    Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);
                    var rowBtn = new Button
                    {
                        Padding = new Thickness(0),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        CornerRadius = CornerSmall,
                        Background = transparent,
                        BorderBrush = transparent,
                        BorderThickness = new Thickness(0),
                        Content = barGrid
                    };
                    MarkInteractiveElement(rowBtn);
                    ApplyButtonStateOverrides(rowBtn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
                    rowBtn.Click += (_, _) => FireAndForgetSafeUi(() => _viewModel.ExecuteActionsAsync(rowActions, itemCtx));
                    listStack.Children.Add(rowBtn);
                }
                else
                {
                    listStack.Children.Add(barGrid);
                }
            }
        }

        PopulateBarRows(ctx);
        RegisterRetainedPatcher(PopulateBarRows);
        return host;
    }
}
