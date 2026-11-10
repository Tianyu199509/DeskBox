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
                    _inspectorHoverTagText.Text = string.Equals(hit.DisplayLabel, hit.NodeType, StringComparison.OrdinalIgnoreCase)
                        ? hit.NodeType
                        : $"{hit.NodeType} · {hit.DisplayLabel}";
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
            VerticalAlignment = element.VerticalAlignment
        };

        if (_isNodeSelectionEnabled)
        {
            host.Background = GetOrCreateSolidBrush(Colors.Transparent);

            bool hasInspectableChildren =
                spec.Children.Count > 0 ||
                spec.ItemTemplate is not null ||
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

        string label = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        if (!string.IsNullOrWhiteSpace(label))
        {
            AutomationProperties.SetName(combo, label);
        }

        var optionValues = new List<string>(spec.Options.Count);
        int selectedIndex = -1;
        for (int i = 0; i < spec.Options.Count; i++)
        {
            CustomWidgetOptionSpec opt = spec.Options[i];
            optionValues.Add(opt.Value);
            combo.Items.Add(CustomWidgetExpressionEvaluator.Interpolate(opt.Label, ctx));
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

        combo.SelectionChanged += async (_, _) =>
        {
            if (isSyncing || combo.SelectedIndex < 0 || combo.SelectedIndex >= optionValues.Count)
            {
                return;
            }

            EndInteractiveSession(combo);
            string chosen = optionValues[combo.SelectedIndex];
            CustomWidgetEvaluationContext evCtx = ctx.WithEventValue(chosen);
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: bindKey, Value: "$value")
                ], evCtx);
            }

            if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
            }
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            return combo;
        }

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelTb = new TextBlock
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

        bool syncing = false;
        async void HandleCheckedChanged(bool nextValue)
        {
            if (syncing)
            {
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

                string boolStr = nextValue ? "true" : "false";
                CustomWidgetEvaluationContext evCtx = ctx.WithEventValue(boolStr);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: bindKey, Value: "$value")
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
            finally
            {
                syncing = false;
            }
        }

        checkBox.Checked += (_, _) => HandleCheckedChanged(true);
        checkBox.Unchecked += (_, _) => HandleCheckedChanged(false);
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

        bool isUserDragging = false;
        bool isProgrammaticSync = false;
        string lastCommittedFormatted = FormatNumber(current);

        async Task CommitSliderValueAsync(double targetVal)
        {
            string formattedNumber = FormatNumber(targetVal);
            if (string.Equals(formattedNumber, lastCommittedFormatted, StringComparison.Ordinal))
            {
                return;
            }
            lastCommittedFormatted = formattedNumber;

            CustomWidgetEvaluationContext evCtx = ctx.WithEventValue(formattedNumber);
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: bindKey, Value: "$value")
                ], evCtx);
            }
            if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
            }
        }

        slider.PointerPressed += (_, _) =>
        {
            isUserDragging = true;
            BeginInteractiveSession(slider);
        };
        slider.PointerCanceled += (_, _) =>
        {
            isUserDragging = false;
            EndInteractiveSession(slider);
        };
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

        slider.PointerCaptureLost += async (_, _) =>
        {
            isUserDragging = false;
            EndInteractiveSession(slider);
            await CommitSliderValueAsync(slider.Value);
        };

        slider.KeyUp += async (_, e) =>
        {
            if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
            {
                await CommitSliderValueAsync(slider.Value);
            }
        };

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

        if (string.IsNullOrWhiteSpace(bindKey) &&
            (ContainsSystemMetricsToken(spec.Value) || ContainsSystemMetricsToken(rawTitle)))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                if (titleTb is not null && ContainsSystemMetricsToken(rawTitle))
                {
                    titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
                }

                if (!isUserDragging && ContainsSystemMetricsToken(spec.Value))
                {
                    string liveRaw = CustomWidgetExpressionEvaluator.Interpolate(spec.Value, liveCtx);
                    if (double.TryParse(liveRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double liveVal))
                    {
                        double clamped = Math.Clamp(liveVal, min, max);
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
            });
        }

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

        bool syncing = false;
        box.ValueChanged += async (_, e) =>
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
                CustomWidgetEvaluationContext evCtx = ctx.WithEventValue(formatted);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("state.set", Key: bindKey, Value: "$value")
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
        };

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

        var textBox = new TextBox
        {
            AcceptsReturn = true,
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = spec.MinHeight ?? 68,
            MaxHeight = spec.MaxHeight ?? 180,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = CornerSmall,
            FontSize = BodyFontSize
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);
        textBox.Text = initialValue;

        textBox.GotFocus += (_, _) => BeginInteractiveSession(textBox);
        textBox.LostFocus += (_, _) => EndInteractiveSession(textBox);
        textBox.Unloaded += (_, _) => EndInteractiveSession(textBox);

        textBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, textBox.Text);
            }
        };

        if (spec.OnSubmit.Count > 0)
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            textBox.KeyDown += async (_, e) =>
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
            };
        }

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
        if (ContainsSystemMetricsToken(spec.Icon))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                UpdateInlineIconGlyph(iconEl, ResolveNormalizedIcon(spec.Icon, liveCtx) ?? "\uE710");
            });
        }
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

        if (spec.OnClick.Count > 0 || !string.IsNullOrWhiteSpace(spec.Popover))
        {
            CustomWidgetEvaluationContext capturedCtx = ctx;
            btn.Click += async (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(spec.Popover))
                {
                    string popId = CustomWidgetExpressionEvaluator.Interpolate(spec.Popover, capturedCtx).Trim();
                    HandlePopoverCommand("toggle", popId, btn);
                }

                if (spec.OnClick.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnClick, capturedCtx);
                }
            };
        }

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
            Glyph = isExpanded ? "\uE70E" : "\uE70D",
            FontSize = 11,
            Foreground = ResolveForegroundBrush("secondary"),
            VerticalAlignment = VerticalAlignment.Center
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

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
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

        headerBtn.Click += async (_, _) =>
        {
            if (_isNodeSelectionEnabled)
            {
                return;
            }

            isExpanded = !isExpanded;
            bodyStack.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = isExpanded ? "\uE70E" : "\uE70D";
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, isExpanded ? "true" : "false");
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: bindKey, Value: isExpanded ? "true" : "false")
                ], ctx);
            }
        };

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

    private FrameworkElement BuildSparkline(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double chartHeight = Math.Clamp(spec.Height ?? 36, 18, 140);
        string tone = string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
            ? "accent"
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);

        List<double> series = ResolveSparklineSeries(spec, ctx);
        double latest = series.Count > 0 ? series[^1] : 0;

        Brush strokeBrush = ResolveSemanticBrush(tone);
        Color strokeColor = strokeBrush is SolidColorBrush scb
            ? scb.Color
            : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Color topColor = Color.FromArgb((byte)(IsDark ? 44 : 28), strokeColor.R, strokeColor.G, strokeColor.B);

        if (!_sparklineBrushCache.TryGetValue(topColor, out LinearGradientBrush? areaBrush))
        {
            areaBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            areaBrush.GradientStops.Add(new GradientStop { Color = topColor, Offset = 0 });
            areaBrush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, topColor.R, topColor.G, topColor.B), Offset = 1 });
            _sparklineBrushCache[topColor] = areaBrush;
        }

        var canvasHost = new Grid
        {
            Height = chartHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var areaPoly = new Polygon
        {
            Fill = spec.ShowArea ? areaBrush : GetOrCreateSolidBrush(Colors.Transparent),
            Stretch = Stretch.None
        };
        var linePoly = new Polyline
        {
            Stroke = strokeBrush,
            StrokeThickness = 1.75,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None
        };
        canvasHost.Children.Add(areaPoly);
        canvasHost.Children.Add(linePoly);

        void RenderPoints(IReadOnlyList<double> pts)
        {
            double w = canvasHost.ActualWidth > 12 ? canvasHost.ActualWidth : 180;
            double h = Math.Max(14, chartHeight - 2);
            linePoly.Points.Clear();
            areaPoly.Points.Clear();

            if (pts.Count == 0)
            {
                return;
            }

            double minV = pts.Min();
            double maxV = pts.Max();
            double span = maxV - minV;
            if (span < 0.0001)
            {
                minV -= 1;
                span = 2;
            }

            int count = pts.Count;
            if (count == 1)
            {
                double y = h * 0.5;
                linePoly.Points.Add(new Point(0, y));
                linePoly.Points.Add(new Point(w, y));
                areaPoly.Points.Add(new Point(0, h));
                areaPoly.Points.Add(new Point(0, y));
                areaPoly.Points.Add(new Point(w, y));
                areaPoly.Points.Add(new Point(w, h));
                return;
            }

            areaPoly.Points.Add(new Point(0, h));
            for (int i = 0; i < count; i++)
            {
                double x = (double)i / (count - 1) * w;
                double norm = (pts[i] - minV) / span;
                double y = h - norm * (h - 4) - 2;
                var pt = new Point(x, y);
                linePoly.Points.Add(pt);
                areaPoly.Points.Add(pt);
            }
            areaPoly.Points.Add(new Point(w, h));
        }

        RenderPoints(series);
        canvasHost.SizeChanged += (_, _) => RenderPoints(ResolveSparklineSeries(spec, _viewModel.CreateEvaluationContext()));

        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);
        if (string.IsNullOrWhiteSpace(title) && !spec.ShowLatest)
        {
            return canvasHost;
        }

        var wrapper = new StackPanel { Spacing = 4 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(title))
        {
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        if (spec.ShowLatest && series.Count > 0)
        {
            var latestTb = new TextBlock
            {
                Text = $"{latest.ToString("0.##", CultureInfo.InvariantCulture)}{unit}",
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("default")
            };
            Grid.SetColumn(latestTb, 1);
            header.Children.Add(latestTb);
        }
        wrapper.Children.Add(header);
        wrapper.Children.Add(canvasHost);
        return wrapper;
    }

    private List<double> ResolveSparklineSeries(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        var result = new List<double>();
        string? colName = spec.Collection ?? spec.Items;
        if (!string.IsNullOrWhiteSpace(colName))
        {
            string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(colName, ctx).Trim();
            string cleanCol = resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                ? resolvedCol["collections.".Length..]
                : resolvedCol;
            if (!string.IsNullOrWhiteSpace(cleanCol) &&
                ctx.Collections.TryGetValue(cleanCol, out List<CustomWidgetRecord>? records) &&
                records is { Count: > 0 })
            {
                string valField = string.IsNullOrWhiteSpace(spec.ValueField) ? "value" : spec.ValueField!;
                // Records in newestFirst collections are reversed to chronological left->right order
                foreach (CustomWidgetRecord rec in Enumerable.Reverse(records).TakeLast(40))
                {
                    string raw = rec.GetField(valField);
                    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        result.Add(d);
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }
        }

        string? rawExpr = spec.Sparkline ?? spec.Value;
        if (!string.IsNullOrWhiteSpace(rawExpr))
        {
            IReadOnlyList<double> sysHist = CustomWidgetExpressionEvaluator.ResolveHistory(rawExpr, ctx);
            if (sysHist.Count > 0)
            {
                result.AddRange(sysHist);
                return result;
            }

            string interpolated = CustomWidgetExpressionEvaluator.Interpolate(rawExpr, ctx);
            foreach (string part in interpolated.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    result.Add(v);
                }
            }
        }

        return result;
    }

    private FrameworkElement BuildHeatmapGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        int days = Math.Clamp(spec.Days, 7, 112);
        string tone = string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
            ? "success"
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);

        Brush baseBrush = ResolveSemanticBrush(tone);
        Color baseColor = baseBrush is SolidColorBrush scb
            ? scb.Color
            : (IsDark ? Color.FromArgb(255, 52, 211, 153) : Color.FromArgb(255, 16, 185, 129));
        Brush emptyCellBrush = GetOrCreateSolidBrush(IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));

        var dayTotals = new Dictionary<DateOnly, double>();
        string? colName = spec.Collection ?? spec.Items;
        if (!string.IsNullOrWhiteSpace(colName))
        {
            string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(colName, ctx).Trim();
            string cleanCol = resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                ? resolvedCol["collections.".Length..]
                : resolvedCol;
            if (!string.IsNullOrWhiteSpace(cleanCol) &&
                ctx.Collections.TryGetValue(cleanCol, out List<CustomWidgetRecord>? records) &&
                records is not null)
            {
                string? dateField = spec.DateField;
                string? valField = spec.ValueField;
                foreach (CustomWidgetRecord rec in records)
                {
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

        double maxDayValue = dayTotals.Values.Count > 0 ? Math.Max(1.0, dayTotals.Values.Max()) : 1.0;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        double cellSize = spec.Size ?? (days <= 28 ? 14 : 11);

        var wrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 4,
            VerticalSpacing = 4
        };

        for (int offset = days - 1; offset >= 0; offset--)
        {
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

            var cell = new Border
            {
                Width = cellSize,
                Height = cellSize,
                CornerRadius = new CornerRadius(3),
                Background = fill
            };
            ToolTipService.SetToolTip(cell, val > 0
                ? $"{d:MM-dd}: {val.ToString("0.##", CultureInfo.InvariantCulture)}"
                : $"{d:MM-dd}: 0");
            wrap.Children.Add(cell);
        }

        string title = CustomWidgetExpressionEvaluator.Interpolate(spec.Title ?? spec.Text, ctx);
        if (string.IsNullOrWhiteSpace(title))
        {
            return wrap;
        }

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(wrap);
        return stack;
    }

    private FrameworkElement BuildCalendarGrid(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string culture = ctx.CultureName ?? _viewModel.ActiveCultureName;

        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int monthOffset = 0;
        if (ctx.State.TryGetValue("calendarMonthOffset", out string? rawOffset) &&
            int.TryParse(rawOffset, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOffset))
        {
            monthOffset = Math.Clamp(parsedOffset, -120, 120);
        }

        DateOnly visibleMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(monthOffset);
        string bindKey = string.IsNullOrWhiteSpace(spec.Bind) ? "selectedDate" : spec.Bind.Trim();
        string rawSelected = CustomWidgetExpressionEvaluator.EvaluateExpression(
            bindKey.StartsWith("state.", StringComparison.OrdinalIgnoreCase) ? bindKey : $"state.{bindKey}",
            ctx);
        DateOnly selectedDate = DateOnly.TryParse(rawSelected, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedSel)
            ? parsedSel
            : today;

        IReadOnlyList<CustomWidgetRecord>? eventRecords = null;
        string rawCol = CustomWidgetExpressionEvaluator.Interpolate(spec.Collection ?? spec.Items, ctx).Trim();
        if (rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
        {
            rawCol = rawCol["collections.".Length..];
        }
        if (!string.IsNullOrWhiteSpace(rawCol) &&
            ctx.Collections.TryGetValue(rawCol, out List<CustomWidgetRecord>? explicitList))
        {
            eventRecords = explicitList;
        }
        else if (string.IsNullOrWhiteSpace(rawCol) &&
                 ctx.Collections.TryGetValue("events", out List<CustomWidgetRecord>? defaultEvents))
        {
            eventRecords = defaultEvents;
        }

        CustomWidgetCalendarMonthView monthView = CustomWidgetCalendarEngine.BuildMonthView(
            visibleMonth,
            today,
            selectedDate,
            ctx.CultureName,
            eventRecords,
            spec.DateField);

        var root = new StackPanel { Spacing = 6 };

        // Header: Month Title + Lunar Subtitle + Prev / Today / Next
        var headerGrid = new Grid { ColumnSpacing = 6 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = monthView.MonthTitle,
            FontSize = SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(monthView.TraditionalTitle))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = monthView.TraditionalTitle,
                FontSize = Math.Max(10.5, CaptionFontSize - 1),
                Foreground = ResolveForegroundBrush("secondary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        headerGrid.Children.Add(titleStack);

        var navRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        Button prevBtn = CreateSubtleIconButton("\uE76B", size: 26, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.Calendar.PreviousMonth"));
        prevBtn.Click += async (_, _) =>
        {
            await _viewModel.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: (monthOffset - 1).ToString(CultureInfo.InvariantCulture))
            ], ctx);
        };
        navRow.Children.Add(prevBtn);

        if (monthOffset != 0 || selectedDate != today)
        {
            var todayBtn = new Button
            {
                Content = LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.Calendar.Today"),
                FontSize = Math.Max(10.5, CaptionFontSize - 1),
                Padding = new Thickness(7, 2, 7, 2),
                MinHeight = 24,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            StyleStandardCommandButton(todayBtn, CornerSmall);
            todayBtn.Click += async (_, _) =>
            {
                string todayIso = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: "0"),
                    new CustomWidgetActionSpec("state.set", Key: bindKey, Value: todayIso)
                ], ctx);
            };
            navRow.Children.Add(todayBtn);
        }

        Button nextBtn = CreateSubtleIconButton("\uE76C", size: 26, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.Calendar.NextMonth"));
        nextBtn.Click += async (_, _) =>
        {
            await _viewModel.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "calendarMonthOffset", Value: (monthOffset + 1).ToString(CultureInfo.InvariantCulture))
            ], ctx);
        };
        navRow.Children.Add(nextBtn);

        Grid.SetColumn(navRow, 1);
        headerGrid.Children.Add(navRow);
        root.Children.Add(headerGrid);

        // Weekday headers
        var weekGrid = new Grid { ColumnSpacing = 2 };
        for (int c = 0; c < 7; c++)
        {
            weekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string wd = c < monthView.WeekdayHeaders.Count ? monthView.WeekdayHeaders[c] : string.Empty;
            var wdTb = new TextBlock
            {
                Text = wd,
                FontSize = Math.Max(10.5, CaptionFontSize - 1),
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(wdTb, c);
            weekGrid.Children.Add(wdTb);
        }
        root.Children.Add(weekGrid);

        // 6x7 day cells
        var daysGrid = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
        for (int c = 0; c < 7; c++)
        {
            daysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        int rowCount = Math.Max(1, (monthView.Cells.Count + 6) / 7);
        for (int r = 0; r < rowCount; r++)
        {
            daysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Color accentColor = IsDark
            ? Color.FromArgb(255, 96, 165, 250)
            : Color.FromArgb(255, 37, 99, 235);
        Brush accentFill = GetBrush("AccentFillColorDefaultBrush", accentColor);
        Brush onAccentFg = GetBrush(
            "TextOnAccentFillColorPrimaryBrush",
            IsDark ? Color.FromArgb(255, 16, 18, 22) : Colors.White);
        Brush selectedFill = GetOrCreateSolidBrush(
            Color.FromArgb((byte)(IsDark ? 38 : 26), accentColor.R, accentColor.G, accentColor.B));
        Brush selectedBorder = GetOrCreateSolidBrush(
            Color.FromArgb(190, accentColor.R, accentColor.G, accentColor.B));
        Brush transparent = GetOrCreateSolidBrush(Colors.Transparent);

        for (int i = 0; i < monthView.Cells.Count; i++)
        {
            CustomWidgetCalendarCell cell = monthView.Cells[i];
            int col = i % 7;
            int row = i / 7;

            var cellStack = new StackPanel
            {
                Spacing = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dayTb = new TextBlock
            {
                Text = cell.DayText,
                FontSize = CaptionFontSize,
                FontWeight = cell.IsToday || cell.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = cell.IsToday
                    ? onAccentFg
                    : (cell.IsWeekend && cell.IsCurrentMonth
                        ? ResolveForegroundBrush("secondary")
                        : ResolveForegroundBrush("default")),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (cell.IsToday)
            {
                WidgetTextShadow.SetCast(dayTb, false);
            }
            cellStack.Children.Add(dayTb);

            if (!string.IsNullOrWhiteSpace(cell.SubtitleText))
            {
                var subTb = new TextBlock
                {
                    Text = cell.SubtitleText,
                    FontSize = 9.5,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.Clip,
                    Foreground = cell.IsToday
                        ? onAccentFg
                        : (cell.IsFestival
                            ? ResolveSemanticBrush("accent")
                            : ResolveForegroundBrush("secondary")),
                    Opacity = cell.IsToday ? 0.9 : 0.85,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                if (cell.IsToday)
                {
                    WidgetTextShadow.SetCast(subTb, false);
                }
                cellStack.Children.Add(subTb);
            }

            if (cell.EventCount > 0)
            {
                cellStack.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Margin = new Thickness(0, 1, 0, 0),
                    Fill = cell.IsToday ? onAccentFg : ResolveSemanticBrush("accent"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            var cellBtn = new Button
            {
                Padding = new Thickness(2, 3, 2, 3),
                MinWidth = 0,
                MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = CornerSmall,
                Opacity = cell.IsCurrentMonth ? 1.0 : 0.38,
                Background = cell.IsToday
                    ? accentFill
                    : (cell.IsSelected ? selectedFill : transparent),
                BorderBrush = cell.IsSelected && !cell.IsToday ? selectedBorder : transparent,
                BorderThickness = new Thickness(1),
                Content = cellStack
            };

            if (!cell.IsToday && !cell.IsSelected)
            {
                ApplyButtonStateOverrides(cellBtn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
            }

            if (cell.EventCount > 0 && !string.IsNullOrWhiteSpace(cell.FirstEventTitle))
            {
                string tip = cell.EventCount > 1
                    ? $"{cell.IsoDate} · {cell.FirstEventTitle} (+{cell.EventCount - 1})"
                    : $"{cell.IsoDate} · {cell.FirstEventTitle}";
                ToolTipService.SetToolTip(cellBtn, tip);
            }

            string targetIso = cell.IsoDate;
            int clickedMonthDelta = (cell.Date.Year - visibleMonth.Year) * 12 + (cell.Date.Month - visibleMonth.Month);
            cellBtn.Click += async (_, _) =>
            {
                var batch = new List<CustomWidgetActionSpec>
                {
                    new("state.set", Key: bindKey, Value: targetIso)
                };
                if (clickedMonthDelta != 0)
                {
                    batch.Add(new("state.set", Key: "calendarMonthOffset", Value: (monthOffset + clickedMonthDelta).ToString(CultureInfo.InvariantCulture)));
                }
                await _viewModel.ExecuteActionsAsync(batch, ctx);
                if (spec.OnChange.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnChange);
                }
                else if (spec.OnClick.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnClick);
                }
            };

            Grid.SetColumn(cellBtn, col);
            Grid.SetRow(cellBtn, row);
            daysGrid.Children.Add(cellBtn);
        }

        root.Children.Add(daysGrid);
        return root;
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

        innerStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = BodyFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            innerStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("secondary"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

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
                FontSize = Math.Max(10.5, CaptionFontSize - 1),
                Foreground = lastTr.Succeeded ? ResolveSemanticBrush("success") : ResolveSemanticBrush("warning"),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (lastTr.Succeeded && !string.IsNullOrWhiteSpace(lastTr.OutputPath))
            {
                string outPath = lastTr.OutputPath;
                Button revealBtn = CreateSubtleIconButton("\uE838", size: 22, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "Glance.Actions.ShowInExplorer"));
                revealBtn.Click += async (_, _) => await _viewModel.RevealFileInExplorerAsync(outPath);
                resultRow.Children.Add(revealBtn);
            }
            else if (lastTr.Succeeded && !string.IsNullOrWhiteSpace(lastTr.ResultText))
            {
                string copyVal = lastTr.ResultText;
                Button copyBtn = CreateSubtleIconButton("\uE8C8", size: 22, iconSize: 11, tooltip: LocalizationService.TranslateForCulture(culture, "CustomWidget.Renderer.FileDrop.CopyTooltip"));
                copyBtn.Click += async (_, _) =>
                {
                    await _viewModel.ExecuteActionsAsync(
                    [
                        new CustomWidgetActionSpec("clipboard.copy", Text: copyVal)
                    ], ctx);
                };
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

        zoneBorder.DragOver += (_, e) =>
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                DataPackageOperation negotiated = DeskBoxDragData.GetFileAssociationOperation(
                    e.DataView,
                    e.AllowedOperations);
                if (negotiated != DataPackageOperation.None)
                {
                    e.AcceptedOperation = negotiated;
                    zoneBorder.BorderBrush = dragBorder;
                    zoneBorder.Background = dragBg;
                    e.Handled = true;
                }
            }
        };
        zoneBorder.DragLeave += (_, _) =>
        {
            zoneBorder.BorderBrush = restBorder;
            zoneBorder.Background = restBg;
        };
        zoneBorder.Drop += async (_, e) =>
        {
            zoneBorder.BorderBrush = restBorder;
            zoneBorder.Background = restBg;
            if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                return;
            }

            DataPackageOperation allowedOperations = e.AllowedOperations;
            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                string[] paths = batch.Files
                    .Select(static f => f.Path)
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();
                if (paths.Length > 0)
                {
                    bool forceCopy = batch.Files.Any(static f => f.ForceManagedCopy);
                    bool handled = await _viewModel.HandleDropZoneFilesAsync(
                        paths,
                        spec.OnDropActions,
                        forceCopyToPortableStore: forceCopy,
                        nodeDropMode: spec.StorageMode);
                    e.AcceptedOperation = handled
                        ? DeskBoxDragData.GetFileAssociationOperation(e.DataView, allowedOperations)
                        : DataPackageOperation.None;
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
        };

        zoneBorder.Tapped += async (_, e) =>
        {
            if (IsInteractiveDescendant(e.OriginalSource as DependencyObject, zoneBorder))
            {
                return;
            }

            e.Handled = true;
            if (spec.OnDropActions.Count > 0 || _viewModel.Manifest.OnDrop is not null)
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
                await _viewModel.ExecuteActionsAsync(spec.OnClick, ctx);
            }
        };

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

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var subTb = new TextBlock
            {
                Text = subtitle,
                FontSize = Math.Max(10.5, CaptionFontSize - 0.5),
                Foreground = isActive ? onAccentFg : ResolveForegroundBrush("secondary"),
                Opacity = isActive ? 0.84 : 1.0,
                TextTrimming = TextTrimming.CharacterEllipsis
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

        if (isActive)
        {
            StyleAccentCommitButton(tileBtn);
            tileBtn.CornerRadius = CornerLarge;
        }
        else
        {
            StyleStandardCommandButton(tileBtn, CornerLarge);
        }

        if (!string.IsNullOrWhiteSpace(spec.Tooltip))
        {
            ToolTipService.SetToolTip(tileBtn, CustomWidgetExpressionEvaluator.Interpolate(spec.Tooltip, ctx));
        }

        tileBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(bindKey) &&
                !bindKey.StartsWith("$", StringComparison.Ordinal))
            {
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.toggle", Key: bindKey)
                ], ctx);
            }

            if (spec.OnClick.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnClick, ctx);
            }
            else if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, ctx);
            }
        };

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

        string stretchRaw = (spec.Stretch ?? spec.Style ?? "cover").Trim().ToLowerInvariant();
        Stretch stretch = stretchRaw switch
        {
            "contain" or "uniform" => Stretch.Uniform,
            "fill" => Stretch.Fill,
            _ => Stretch.UniformToFill
        };

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
        int loadGeneration = 0;

        double ResolveDecodeRasterScale() =>
            Math.Clamp(
                img.XamlRoot?.RasterizationScale
                    ?? (_themeScope as UIElement)?.XamlRoot?.RasterizationScale
                    ?? 2.0,
                1.0,
                4.0);

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

                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                if (explicitW is > 0)
                {
                    bitmap.DecodePixelWidth = Math.Clamp(
                        (int)Math.Ceiling(explicitW.Value * ResolveDecodeRasterScale()),
                        32,
                        1600);
                }
                await bitmap.SetSourceAsync(ras);

                if (generation == loadGeneration)
                {
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

        void ApplyImageSource(string candidate)
        {
            string trimmed = candidate.Trim();
            if (string.Equals(trimmed, appliedSrc, StringComparison.Ordinal))
            {
                return;
            }
            appliedSrc = trimmed;
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
                if (explicitW is > 0)
                {
                    bitmap.DecodePixelWidth = Math.Clamp(
                        (int)Math.Ceiling(explicitW.Value * ResolveDecodeRasterScale()),
                        32,
                        1600);
                }
                bitmap.UriSource = resolvedUri;
                img.Source = bitmap;
                img.Visibility = Visibility.Visible;
                fallbackHost.Visibility = Visibility.Collapsed;
            }
            catch
            {
                img.Source = null;
                img.Visibility = Visibility.Collapsed;
                fallbackHost.Visibility = Visibility.Visible;
            }
        }

        ApplyImageSource(initialSrc);

        if (ContainsSystemMetricsToken(rawSrc))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                ApplyImageSource(CustomWidgetExpressionEvaluator.Interpolate(rawSrc, liveCtx));
            });
        }

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
            container.Tapped += async (_, e) =>
            {
                e.Handled = true;
                await _viewModel.ExecuteActionsAsync(spec.OnClick, ctx);
            };
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
            string? tabIcon = ResolveNormalizedIcon(
                child.Icon ?? (i < spec.Options.Count ? spec.Options[i].Icon : null),
                ctx);

            UIElement btnContent;
            if (!string.IsNullOrWhiteSpace(tabIcon))
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(CreateInlineIconElement(tabIcon, 12));
                row.Children.Add(new TextBlock
                {
                    Text = tabLabel,
                    FontSize = CaptionFontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                btnContent = row;
            }
            else
            {
                btnContent = new TextBlock
                {
                    Text = tabLabel,
                    FontSize = CaptionFontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
            }

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

        for (int i = 0; i < tabButtons.Count; i++)
        {
            int capturedIdx = i;
            string capturedKey = tabKeys[i];
            tabButtons[i].Click += async (_, _) =>
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

                CustomWidgetEvaluationContext evCtx = ctx.WithEventValue(capturedKey);
                if (!string.IsNullOrWhiteSpace(bindKey))
                {
                    _viewModel.SetStateValueWithoutRebuild(bindKey, capturedKey);
                }
                if (spec.OnChange.Count > 0)
                {
                    await _viewModel.ExecuteActionsAsync(spec.OnChange, evCtx);
                }
            };
        }

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

        var rootStack = new StackPanel
        {
            Spacing = Math.Max(10, spec.Spacing),
            Padding = isRoot ? ResolveRootSafePadding(spec.Padding) : ParseThickness(spec.Padding)
        };
        rootStack.Children.Add(tabHeaderBorder);
        rootStack.Children.Add(panesHost);

        if (isRoot)
        {
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rootStack
            };
        }

        return rootStack;
    }

    private FrameworkElement BuildRingGauge(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        double size = Math.Clamp(spec.Size ?? spec.Width ?? spec.Height ?? 88, 52, 200);
        double strokeThickness = Math.Clamp(Math.Round(size * 0.10), 5, 12);
        double radius = Math.Max(14, (size - strokeThickness) * 0.5);
        double cx = size * 0.5;
        double cy = size * 0.5;

        double min = spec.Min;
        double max = spec.Max > min ? spec.Max : min + 100;
        string rawProgress = spec.Progress ?? spec.Value ?? "0";
        string rawTitle = spec.Title ?? spec.Text ?? string.Empty;
        string rawSubtitle = spec.Subtitle ?? string.Empty;
        string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);

        bool isFullCircle = string.Equals(spec.Style, "full", StringComparison.OrdinalIgnoreCase);
        double startDeg = isFullCircle ? -90.0 : 135.0;
        double maxSweepDeg = isFullCircle ? 359.9 : 270.0;

        static PathGeometry CreateArcGeometry(double centerX, double centerY, double r, double startAngleDeg, double sweepAngleDeg)
        {
            var geom = new PathGeometry();
            double clampedSweep = Math.Clamp(sweepAngleDeg, 0, 359.95);
            if (clampedSweep <= 0.15)
            {
                return geom;
            }

            double startRad = startAngleDeg * (Math.PI / 180.0);
            double endRad = (startAngleDeg + clampedSweep) * (Math.PI / 180.0);
            var startPt = new Point(centerX + r * Math.Cos(startRad), centerY + r * Math.Sin(startRad));
            var endPt = new Point(centerX + r * Math.Cos(endRad), centerY + r * Math.Sin(endRad));

            var fig = new PathFigure
            {
                StartPoint = startPt,
                IsClosed = false,
                IsFilled = false
            };
            fig.Segments.Add(new ArcSegment
            {
                Point = endPt,
                Size = new Size(r, r),
                RotationAngle = 0,
                IsLargeArc = clampedSweep > 180.0,
                SweepDirection = SweepDirection.Clockwise
            });
            geom.Figures.Add(fig);
            return geom;
        }

        double val = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, ctx) ?? min;
        double warn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, ctx);
        double danger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, ctx, Math.Max(warn + 10, 90));
        double norm = Math.Clamp((val - min) / (max - min), 0.0, 1.0);

        Brush trackStroke = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));

        var trackPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = CreateArcGeometry(cx, cy, radius, startDeg, maxSweepDeg),
            Stroke = trackStroke,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        var activePath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = CreateArcGeometry(cx, cy, radius, startDeg, maxSweepDeg * norm),
            Stroke = ResolveThresholdBrush(val, warn, danger, spec.Color),
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        string FormatGaugeVal(double v) =>
            $"{Math.Round(v).ToString(CultureInfo.InvariantCulture)}{unit}";

        var centerValueTb = new TextBlock
        {
            Text = FormatGaugeVal(val),
            FontFamily = DisplayFontFamily,
            FontSize = Math.Clamp(size * 0.20, 13, 24),
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("default"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetTabularNumerals(centerValueTb);

        var dialStage = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        dialStage.Children.Add(trackPath);
        dialStage.Children.Add(activePath);
        dialStage.Children.Add(centerValueTb);

        TextBlock? titleTb = null;
        TextBlock? subtitleTb = null;
        string resolvedTitle = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, ctx);
        string resolvedSub = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, ctx);

        if (!string.IsNullOrWhiteSpace(resolvedTitle))
        {
            titleTb = new TextBlock
            {
                Text = resolvedTitle,
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        if (!string.IsNullOrWhiteSpace(resolvedSub))
        {
            subtitleTb = new TextBlock
            {
                Text = resolvedSub,
                FontSize = Math.Max(10.5, CaptionFontSize - 0.5),
                Foreground = ResolveForegroundBrush("muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        if (ContainsSystemMetricsToken(rawProgress) ||
            ContainsSystemMetricsToken(rawTitle) ||
            ContainsSystemMetricsToken(rawSubtitle))
        {
            _liveMetricUpdaters.Add(liveCtx =>
            {
                double liveVal = CustomWidgetExpressionEvaluator.EvaluateDouble(rawProgress, liveCtx) ?? min;
                double liveWarn = ResolveEffectiveThreshold(spec.WarningThresholdExpression, spec.WarningThreshold, liveCtx);
                double liveDanger = ResolveEffectiveThreshold(spec.DangerThresholdExpression, spec.DangerThreshold, liveCtx, Math.Max(liveWarn + 10, 90));
                double liveNorm = Math.Clamp((liveVal - min) / (max - min), 0.0, 1.0);
                activePath.Data = CreateArcGeometry(cx, cy, radius, startDeg, maxSweepDeg * liveNorm);
                activePath.Stroke = ResolveThresholdBrush(liveVal, liveWarn, liveDanger, spec.Color);
                centerValueTb.Text = FormatGaugeVal(liveVal);

                if (titleTb is not null && ContainsSystemMetricsToken(rawTitle))
                {
                    titleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawTitle, liveCtx);
                }
                if (subtitleTb is not null && ContainsSystemMetricsToken(rawSubtitle))
                {
                    subtitleTb.Text = CustomWidgetExpressionEvaluator.Interpolate(rawSubtitle, liveCtx);
                }
            });
        }

        if (titleTb is null && subtitleTb is null)
        {
            return dialStage;
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
        List<CustomWidgetRecord> activeRecords;
        if (TryResolveComputedRecords(rawCol, ctx, out IReadOnlyList<CustomWidgetRecord> computedRecords))
        {
            activeRecords = computedRecords
                .Where(static r => !r.IsDeleted)
                .ToList();
        }
        else
        {
            string resolvedCol = CustomWidgetExpressionEvaluator.Interpolate(rawCol, ctx).Trim();
            if (resolvedCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
            {
                resolvedCol = resolvedCol["collections.".Length..];
            }

            ctx.Collections.TryGetValue(resolvedCol, out List<CustomWidgetRecord>? allRecords);
            activeRecords = allRecords?
                .Where(static r => !r.IsDeleted)
                .ToList() ?? [];
        }

        if (activeRecords.Count == 0)
        {
            if (spec.EmptyState is not null)
            {
                return BuildEmptyState(spec.EmptyState, ctx);
            }

            return new TextBlock
            {
                Text = LocalizationService.TranslateForCulture(
                    ctx.CultureName ?? _viewModel.ActiveCultureName,
                    "CustomWidget.Renderer.List.EmptyState"),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        int maxRows = spec.MaxLines > 0 ? Math.Clamp(spec.MaxLines, 1, 30) : 8;
        string titleField = string.IsNullOrWhiteSpace(spec.TitleField) ? "title" : spec.TitleField!;
        string valueField = string.IsNullOrWhiteSpace(spec.ValueField) ? "value" : spec.ValueField!;
        string? subtitleField = spec.SubtitleField;
        string unit = CustomWidgetExpressionEvaluator.Interpolate(spec.Unit, ctx);
        string tone = string.IsNullOrWhiteSpace(spec.Color) || string.Equals(spec.Color, "default", StringComparison.OrdinalIgnoreCase)
            ? "accent"
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Color, ctx);

        Brush barAccentBrush = ResolveSemanticBrush(tone);
        Color barAccentColor = barAccentBrush is SolidColorBrush scb
            ? scb.Color
            : (IsDark ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235));
        Brush barFillBrush = GetOrCreateSolidBrush(
            Color.FromArgb((byte)(IsDark ? 46 : 30), barAccentColor.R, barAccentColor.G, barAccentColor.B));
        Brush rowTrackBrush = GetOrCreateSolidBrush(
            IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));

        var parsedRows = new List<(CustomWidgetRecord Record, int Index, string Label, string Subtitle, string ValueDisplay, double NumValue)>(Math.Min(maxRows, activeRecords.Count));
        double computedMax = 0;

        for (int i = 0; i < activeRecords.Count && parsedRows.Count < maxRows; i++)
        {
            CustomWidgetRecord rec = activeRecords[i];
            string label = rec.GetField(titleField);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = rec.GetField("name");
            }
            if (string.IsNullOrWhiteSpace(label))
            {
                label = rec.GetField("label");
            }

            string rawValStr = rec.GetField(valueField);
            if (string.IsNullOrWhiteSpace(rawValStr))
            {
                rawValStr = rec.GetField("percent");
            }
            if (string.IsNullOrWhiteSpace(rawValStr))
            {
                rawValStr = rec.GetField("count");
            }

            double.TryParse(rawValStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double numVal);
            computedMax = Math.Max(computedMax, numVal);

            string sub = !string.IsNullOrWhiteSpace(subtitleField) ? rec.GetField(subtitleField!) : string.Empty;
            string displayVal = !string.IsNullOrWhiteSpace(rawValStr) ? $"{rawValStr}{unit}" : string.Empty;
            parsedRows.Add((rec, i, label, sub, displayVal, Math.Max(0, numVal)));
        }

        double effectiveMax = spec.Max > 0 && spec.Max != 100
            ? Math.Max(spec.Max, computedMax)
            : (computedMax > 0 ? computedMax : 100);

        var listStack = new StackPanel
        {
            Spacing = Math.Max(4, spec.Spacing > 0 && spec.Spacing != 8 ? spec.Spacing : 6)
        };

        foreach (var item in parsedRows)
        {
            double ratio = Math.Clamp(item.NumValue / effectiveMax, 0.0, 1.0);
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
                    Background = barFillBrush,
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
                    FontSize = Math.Max(10.0, CaptionFontSize - 1),
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

            if (spec.OnClick.Count > 0)
            {
                CustomWidgetEvaluationContext itemCtx = ctx.WithItem(item.Record, item.Index);
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
                ApplyButtonStateOverrides(rowBtn, SubtleHoverBrush, SubtlePressedBrush, transparent, transparent);
                rowBtn.Click += async (_, _) => await _viewModel.ExecuteActionsAsync(spec.OnClick, itemCtx);
                listStack.Children.Add(rowBtn);
            }
            else
            {
                listStack.Children.Add(barGrid);
            }
        }

        return listStack;
    }
}
