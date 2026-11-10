using System.Globalization;
using System.Text.Json;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private static readonly string[] DefaultColorPickerPresets =
    [
        "#3B82F6", "#10B981", "#8B5CF6", "#F59E0B", "#EF4444", "#06B6D4",
        "#EC4899", "#6366F1", "#14B8A6", "#F97316", "#64748B", "#0F172A"
    ];

    private FrameworkElement WrapOptionalInputHeader(
        FrameworkElement control,
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string label = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return control;
        }

        AutomationProperties.SetName(control, label);
        var stack = new StackPanel { Spacing = 4 };
        var labelTb = new TextBlock
        {
            Text = label,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveForegroundBrush("secondary"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ApplyRawTextTypographyOverrides(labelTb, spec, ctx);
        stack.Children.Add(labelTb);
        stack.Children.Add(control);
        return stack;
    }

    // =========================================================================
    // DatePicker & TimePicker (Module 1)
    // =========================================================================

    private FrameworkElement BuildDatePicker(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        var picker = new CalendarDatePicker
        {
            PlaceholderText = !string.IsNullOrWhiteSpace(spec.Placeholder)
                ? CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx)
                : "YYYY-MM-DD",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = CornerSmall
        };

        if (!string.IsNullOrWhiteSpace(rawVal) &&
            DateTimeOffset.TryParse(rawVal.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset initialDate))
        {
            picker.Date = initialDate;
        }

        picker.Opened += (_, _) => BeginInteractiveSession(picker);
        picker.Closed += (_, _) => EndInteractiveSession(picker);
        picker.Unloaded += (_, _) => EndInteractiveSession(picker);

        bool suppress = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        picker.DateChanged += async (_, e) =>
        {
            if (suppress || _isNodeSelectionEnabled)
            {
                return;
            }

            suppress = true;
            try
            {
                EndInteractiveSession(picker);
                string iso = e.NewDate.HasValue
                    ? e.NewDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : string.Empty;
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(iso);
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
                suppress = false;
            }
        };

        return WrapOptionalInputHeader(picker, spec, ctx);
    }

    private FrameworkElement BuildTimePicker(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        bool use12Hour = string.Equals(spec.Variant, "12h", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(spec.Variant, "12hour", StringComparison.OrdinalIgnoreCase);

        var picker = new TimePicker
        {
            ClockIdentifier = use12Hour ? "12HourClock" : "24HourClock",
            MinuteIncrement = Math.Clamp((int)Math.Round(spec.Step >= 1 ? spec.Step : 1), 1, 30),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = CornerSmall
        };

        if (!string.IsNullOrWhiteSpace(rawVal) && TryParseTimeSpanLoose(rawVal.Trim(), out TimeSpan ts))
        {
            picker.SelectedTime = ts;
        }

        picker.GotFocus += (_, _) => BeginInteractiveSession(picker);
        picker.LostFocus += (_, _) => EndInteractiveSession(picker);
        picker.Unloaded += (_, _) => EndInteractiveSession(picker);

        bool suppress = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        picker.SelectedTimeChanged += async (_, e) =>
        {
            if (suppress || _isNodeSelectionEnabled)
            {
                return;
            }

            suppress = true;
            try
            {
                EndInteractiveSession(picker);
                string formatted = e.NewTime.HasValue
                    ? $"{(int)e.NewTime.Value.TotalHours:00}:{e.NewTime.Value.Minutes:00}"
                    : string.Empty;
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(formatted);
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
                suppress = false;
            }
        };

        return WrapOptionalInputHeader(picker, spec, ctx);
    }

    private static bool TryParseTimeSpanLoose(string raw, out TimeSpan result)
    {
        if (TimeSpan.TryParseExact(raw, [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out result))
        {
            return true;
        }
        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out result);
    }

    // =========================================================================
    // AutoSuggestInput, ColorPicker, RadioGroup, Rating (Module 3)
    // =========================================================================

    private FrameworkElement BuildAutoSuggestInput(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string initialText = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        var box = new AutoSuggestBox
        {
            Text = initialText,
            PlaceholderText = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = CornerSmall,
            FontSize = spec.FontSize ?? BodyFontSize
        };

        List<string> ResolveCandidates(CustomWidgetEvaluationContext evalCtx)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? s)
            {
                if (!string.IsNullOrWhiteSpace(s))
                {
                    string trimmed = s.Trim();
                    if (seen.Add(trimmed))
                    {
                        candidates.Add(trimmed);
                    }
                }
            }

            foreach (string s in spec.Suggestions)
            {
                AddCandidate(CustomWidgetExpressionEvaluator.Interpolate(s, evalCtx));
            }

            foreach (CustomWidgetOptionSpec opt in spec.Options)
            {
                AddCandidate(!string.IsNullOrWhiteSpace(opt.Label)
                    ? CustomWidgetExpressionEvaluator.Interpolate(opt.Label, evalCtx)
                    : opt.Value);
            }

            string? dynSrc = spec.SuggestionsSource ?? spec.Items ?? spec.Collection;
            if (!string.IsNullOrWhiteSpace(dynSrc))
            {
                string trimmed = dynSrc.Trim();
                if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}'))
                {
                    trimmed = trimmed[2..^1].Trim();
                }

                if (trimmed.StartsWith("$computed.", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("computed.", StringComparison.OrdinalIgnoreCase))
                {
                    string compKey = trimmed.StartsWith('$') ? trimmed["$computed.".Length..] : trimmed["computed.".Length..];
                    string? rawJson = evalCtx.Computed.TryGetValue(compKey, out JsonElement compEl)
                        ? (compEl.ValueKind == JsonValueKind.String ? compEl.GetString() : compEl.GetRawText())
                        : null;
                    if (!string.IsNullOrWhiteSpace(rawJson) &&
                        rawJson.TrimStart().StartsWith('['))
                    {
                        try
                        {
                            using JsonDocument doc = JsonDocument.Parse(rawJson);
                            foreach (JsonElement item in doc.RootElement.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    AddCandidate(item.GetString());
                                }
                                else if (item.ValueKind == JsonValueKind.Object)
                                {
                                    string? label = item.TryGetProperty("label", out JsonElement lp) && lp.ValueKind == JsonValueKind.String
                                        ? lp.GetString()
                                        : item.TryGetProperty("title", out JsonElement tp) && tp.ValueKind == JsonValueKind.String
                                            ? tp.GetString()
                                            : item.TryGetProperty("name", out JsonElement np) && np.ValueKind == JsonValueKind.String
                                                ? np.GetString()
                                                : null;
                                    AddCandidate(label);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                else
                {
                    string colName = CustomWidgetExpressionEvaluator.Interpolate(trimmed, evalCtx).Trim();
                    if (colName.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                    {
                        colName = colName["collections.".Length..];
                    }
                    if (evalCtx.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? colList))
                    {
                        string tf = !string.IsNullOrWhiteSpace(spec.TitleField) ? spec.TitleField! : "title";
                        foreach (CustomWidgetRecord rec in colList)
                        {
                            string v = rec.GetField(tf);
                            if (string.IsNullOrWhiteSpace(v)) v = rec.GetField("text");
                            if (string.IsNullOrWhiteSpace(v)) v = rec.GetField("name");
                            AddCandidate(v);
                        }
                    }
                }
            }

            return candidates;
        }

        box.GotFocus += (_, _) => BeginInteractiveSession(box);
        box.LostFocus += (_, _) => EndInteractiveSession(box);
        box.Unloaded += (_, _) => EndInteractiveSession(box);

        CustomWidgetEvaluationContext capturedCtx = ctx;
        box.TextChanged += (_, e) =>
        {
            string text = box.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, text);
            }

            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                List<string> all = ResolveCandidates(_viewModel.CreateEvaluationContext());
                box.ItemsSource = string.IsNullOrWhiteSpace(text)
                    ? all.Take(12).ToArray()
                    : all.Where(item => item.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(12).ToArray();
            }
        };

        box.SuggestionChosen += async (_, e) =>
        {
            string chosen = e.SelectedItem?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, chosen);
            }
            EndInteractiveSession(box);
            if (spec.OnChange.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(spec.OnChange, capturedCtx.WithEventValue(chosen));
            }
        };

        box.QuerySubmitted += async (_, e) =>
        {
            if (ImeCompositionGuard.IsComposingOrWithinGrace())
            {
                return;
            }

            string submitted = e.ChosenSuggestion?.ToString() ?? e.QueryText ?? box.Text ?? string.Empty;
            CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(submitted);
            if (!string.IsNullOrWhiteSpace(bindKey))
            {
                _viewModel.SetStateValueWithoutRebuild(bindKey, submitted);
            }
            EndInteractiveSession(box);

            IReadOnlyList<CustomWidgetActionSpec> actions = spec.OnSubmit.Count > 0 ? spec.OnSubmit : spec.OnChange;
            if (actions.Count > 0)
            {
                await _viewModel.ExecuteActionsAsync(actions, evCtx);
            }
        };

        return WrapOptionalInputHeader(box, spec, ctx);
    }

    private FrameworkElement BuildColorPicker(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawInitial = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        bool enableAlpha = string.Equals(spec.Variant, "alpha", StringComparison.OrdinalIgnoreCase);
        Color currentColor = TryParseRawColor(rawInitial, out Color parsedInitial)
            ? parsedInitial
            : Color.FromArgb(255, 59, 130, 246);

        static string FormatColorHex(Color c, bool includeAlpha) =>
            includeAlpha && c.A < 255
                ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
                : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        string currentHex = FormatColorHex(currentColor, enableAlpha);

        var swatchDot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(currentColor),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };

        var hexTb = new TextBlock
        {
            Text = currentHex,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var btnContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnContent.Children.Add(swatchDot);
        btnContent.Children.Add(hexTb);

        var triggerBtn = new Button
        {
            Content = btnContent,
            Padding = new Thickness(8, 4, 10, 4),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        StyleStandardCommandButton(triggerBtn, CornerSmall);

        CustomWidgetEvaluationContext capturedCtx = ctx;
        triggerBtn.Click += (_, _) =>
        {
            if (_isNodeSelectionEnabled)
            {
                return;
            }

            Color committedColor = currentColor;
            Color draftColor = currentColor;
            bool committed = false;

            var flyout = new Flyout();
            var panel = new StackPanel
            {
                Width = 264,
                Spacing = 10
            };

            IReadOnlyList<string> presets = spec.Presets.Count > 0 ? spec.Presets : DefaultColorPickerPresets;
            var swatchesWrap = new CustomWidgetWrapPanel
            {
                HorizontalSpacing = 6,
                VerticalSpacing = 6
            };

            var winUiPicker = new ColorPicker
            {
                Color = currentColor,
                IsMoreButtonVisible = false,
                IsColorSliderVisible = true,
                IsColorChannelTextInputVisible = false,
                IsHexInputVisible = true,
                IsAlphaEnabled = enableAlpha,
                IsAlphaSliderVisible = enableAlpha,
                IsAlphaTextInputVisible = false
            };

            async Task CommitSelectedColorAsync(Color chosen)
            {
                committed = true;
                currentColor = chosen;
                committedColor = chosen;
                string nextHex = FormatColorHex(chosen, enableAlpha);
                swatchDot.Background = new SolidColorBrush(chosen);
                hexTb.Text = nextHex;

                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(nextHex);
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

            foreach (string rawPreset in presets.Take(18))
            {
                string resolvedPreset = CustomWidgetExpressionEvaluator.Interpolate(rawPreset, capturedCtx).Trim();
                if (!TryParseRawColor(resolvedPreset, out Color presetCol))
                {
                    continue;
                }

                var presetBtn = new Button
                {
                    Width = 22,
                    Height = 22,
                    MinWidth = 22,
                    MinHeight = 22,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(presetCol),
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(1)
                };
                ToolTipService.SetToolTip(presetBtn, FormatColorHex(presetCol, enableAlpha));
                Color capturedPreset = presetCol;
                presetBtn.Click += async (_, _) =>
                {
                    draftColor = capturedPreset;
                    winUiPicker.Color = capturedPreset;
                    flyout.Hide();
                    await CommitSelectedColorAsync(capturedPreset);
                };
                swatchesWrap.Children.Add(presetBtn);
            }

            if (swatchesWrap.Children.Count > 0)
            {
                panel.Children.Add(swatchesWrap);
            }

            winUiPicker.ColorChanged += (_, args) =>
            {
                draftColor = args.NewColor;
                swatchDot.Background = new SolidColorBrush(draftColor);
                hexTb.Text = FormatColorHex(draftColor, enableAlpha);
            };
            panel.Children.Add(winUiPicker);

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var applyBtn = new Button
            {
                Content = Tr("Common.Save", capturedCtx.CultureName),
                Padding = new Thickness(12, 4, 12, 4),
                MinHeight = 28,
                FontSize = CaptionFontSize
            };
            StyleAccentCommitButton(applyBtn);
            applyBtn.Click += async (_, _) =>
            {
                flyout.Hide();
                await CommitSelectedColorAsync(draftColor);
            };
            actionRow.Children.Add(applyBtn);
            panel.Children.Add(actionRow);

            flyout.Closed += async (_, _) =>
            {
                if (!committed && draftColor != committedColor)
                {
                    await CommitSelectedColorAsync(draftColor);
                }
            };

            flyout.Content = panel;
            ShowTrackedFlyoutAt(flyout, triggerBtn);
        };

        return WrapOptionalInputHeader(triggerBtn, spec, ctx);
    }

    private FrameworkElement BuildRadioGroup(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string currentVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        bool isHorizontal = string.Equals(spec.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(spec.Orientation, "row", StringComparison.OrdinalIgnoreCase);
        int maxCols = isHorizontal
            ? Math.Max(1, spec.Options.Count)
            : (!string.IsNullOrWhiteSpace(spec.Columns) &&
               int.TryParse(spec.Columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCols)
                ? Math.Clamp(parsedCols, 1, 8)
                : 1);

        var radios = new RadioButtons
        {
            MaxColumns = maxCols
        };

        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            radios.Header = new TextBlock
            {
                Text = CustomWidgetExpressionEvaluator.Interpolate(spec.Title, ctx),
                FontSize = CaptionFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveForegroundBrush("secondary")
            };
        }

        int selectedIndex = -1;
        for (int i = 0; i < spec.Options.Count; i++)
        {
            CustomWidgetOptionSpec opt = spec.Options[i];
            var rb = new RadioButton
            {
                Content = CustomWidgetExpressionEvaluator.Interpolate(opt.Label, ctx),
                Tag = opt.Value,
                MinWidth = 0,
                FontSize = spec.FontSize ?? BodyFontSize
            };
            if (string.Equals(opt.Value, currentVal, StringComparison.Ordinal))
            {
                selectedIndex = i;
            }
            radios.Items.Add(rb);
        }

        bool suppress = true;
        if (selectedIndex >= 0)
        {
            radios.SelectedIndex = selectedIndex;
        }
        suppress = false;

        CustomWidgetEvaluationContext capturedCtx = ctx;
        radios.SelectionChanged += async (_, _) =>
        {
            if (suppress || _isNodeSelectionEnabled)
            {
                return;
            }

            if (radios.SelectedItem is RadioButton selectedRb && selectedRb.Tag is string val)
            {
                suppress = true;
                try
                {
                    CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(val);
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
                    suppress = false;
                }
            }
        };

        return radios;
    }

    private FrameworkElement BuildRating(
        CustomWidgetNodeSpec spec,
        CustomWidgetEvaluationContext ctx)
    {
        string bindKey = spec.Bind ?? string.Empty;
        string rawVal = !string.IsNullOrWhiteSpace(bindKey)
            ? CustomWidgetExpressionEvaluator.EvaluateExpression(bindKey, ctx)
            : CustomWidgetExpressionEvaluator.Interpolate(spec.Value, ctx);

        int maxStars = Math.Clamp((int)Math.Round(spec.Max is > 0 and <= 12 ? spec.Max : 5), 1, 12);
        double initialRating = double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0
            ? Math.Clamp(r, 0, maxStars)
            : -1;

        var ratingControl = new RatingControl
        {
            MaxRating = maxStars,
            IsClearEnabled = true,
            IsReadOnly = _isNodeSelectionEnabled || (string.IsNullOrWhiteSpace(bindKey) && spec.OnChange.Count == 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Value = initialRating
        };

        if (!string.IsNullOrWhiteSpace(spec.Placeholder))
        {
            ratingControl.Caption = CustomWidgetExpressionEvaluator.Interpolate(spec.Placeholder, ctx);
        }

        bool suppress = false;
        CustomWidgetEvaluationContext capturedCtx = ctx;
        ratingControl.ValueChanged += async (_, _) =>
        {
            if (suppress || _isNodeSelectionEnabled)
            {
                return;
            }

            suppress = true;
            try
            {
                double val = ratingControl.Value < 0 ? 0 : ratingControl.Value;
                if (!spec.AllowHalfStar)
                {
                    val = Math.Round(val);
                }

                string formatted = val.ToString(spec.AllowHalfStar ? "0.#" : "0", CultureInfo.InvariantCulture);
                CustomWidgetEvaluationContext evCtx = capturedCtx.WithEventValue(formatted);
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
                suppress = false;
            }
        };

        return WrapOptionalInputHeader(ratingControl, spec, ctx);
    }

    // =========================================================================
    // List Drag-Reorder & Double-Click Inline Editing (Module 4)
    // =========================================================================

    private sealed record DraggableListItemSlot(
        int ActualRecordIndex,
        Grid HostGrid,
        Border DropIndicator);

    private void AttachListItemInlineEditor(
        CustomWidgetNodeSpec listSpec,
        string resolvedCollectionName,
        CustomWidgetRecord? record,
        Border cardBorder,
        Grid itemGrid,
        UIElement normalBodyElement,
        int bodyColumnIndex)
    {
        if (_isNodeSelectionEnabled ||
            string.IsNullOrWhiteSpace(listSpec.InlineEditField) ||
            string.IsNullOrWhiteSpace(resolvedCollectionName) ||
            record is null ||
            string.IsNullOrWhiteSpace(record.Id))
        {
            return;
        }

        string editField = listSpec.InlineEditField.Trim();
        string itemId = record.Id;
        bool isEditing = false;

        cardBorder.DoubleTapped += (_, e) =>
        {
            if (isEditing || IsInteractiveDescendant(e.OriginalSource as DependencyObject, cardBorder))
            {
                return;
            }

            e.Handled = true;
            isEditing = true;
            normalBodyElement.Visibility = Visibility.Collapsed;

            string initialText = record.GetField(editField);
            var editor = new TextBox
            {
                Text = initialText,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplyCompactInputStyle(editor);
            BeginInteractiveSession(editor);
            editor.Unloaded += (_, _) => EndInteractiveSession(editor);
            Grid.SetColumn(editor, bodyColumnIndex);
            itemGrid.Children.Add(editor);

            bool finished = false;
            async Task FinishEditAsync(bool save)
            {
                if (finished)
                {
                    return;
                }
                finished = true;
                isEditing = false;

                string nextText = editor.Text ?? string.Empty;
                itemGrid.Children.Remove(editor);
                normalBodyElement.Visibility = Visibility.Visible;

                if (save && !string.Equals(nextText, initialText, StringComparison.Ordinal))
                {
                    _activeInteractions.Remove(editor);
                    await _viewModel.CommitInlineItemEditAsync(
                        resolvedCollectionName,
                        itemId,
                        editField,
                        nextText,
                        listSpec.OnInlineCommit);
                    NotifyIfAllInteractionsClosed();
                }
                else
                {
                    EndInteractiveSession(editor);
                }
            }

            editor.KeyDown += async (_, ke) =>
            {
                if (ImeCompositionGuard.IsImeCompositionKeyEvent(ke))
                {
                    return;
                }

                if (ke.Key == VirtualKey.Enter)
                {
                    ke.Handled = true;
                    await FinishEditAsync(save: true);
                }
                else if (ke.Key == VirtualKey.Escape)
                {
                    ke.Handled = true;
                    await FinishEditAsync(save: false);
                }
            };

            editor.LostFocus += async (_, _) =>
            {
                await FinishEditAsync(save: true);
            };

            editor.Loaded += (_, _) =>
            {
                DeskBox.Views.WidgetWindowBase.TryActivateOwnerForKeyboardInput(editor);
                editor.Focus(FocusState.Programmatic);
                editor.SelectAll();
            };
        };
    }

    private UIElement WrapListItemForDragReorder(
        UIElement itemCard,
        int actualIndex,
        string resolvedCollectionName,
        StackPanel containerPanel,
        List<DraggableListItemSlot> slotRecords,
        CustomWidgetNodeSpec listSpec,
        CustomWidgetEvaluationContext ctx)
    {
        if (_isNodeSelectionEnabled ||
            !listSpec.AllowReorder ||
            string.IsNullOrWhiteSpace(resolvedCollectionName))
        {
            return itemCard;
        }

        var host = new Grid();
        var rowGrid = new Grid { ColumnSpacing = 4 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gripTb = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = ResolveForegroundBrush("muted"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        WidgetTextShadow.SetCast(gripTb, false);

        var gripHandle = new Border
        {
            Width = 18,
            MinHeight = 28,
            CornerRadius = CornerSmall,
            Background = GetOrCreateSolidBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.48,
            Child = gripTb
        };
        ToolTipService.SetToolTip(gripHandle, Tr("CustomWidget.Module.DragHint", ctx.CultureName));
        Grid.SetColumn(gripHandle, 0);
        rowGrid.Children.Add(gripHandle);

        if (itemCard is FrameworkElement cardFe)
        {
            Grid.SetColumn(cardFe, 1);
        }
        rowGrid.Children.Add(itemCard);
        host.Children.Add(rowGrid);

        var dropIndicator = new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = ResolveSemanticBrush("accent"),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, -2, 4, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        host.Children.Add(dropIndicator);

        bool isDragging = false;
        uint activePointerId = 0;
        Point pressPoint = default;
        bool movedEnough = false;
        int targetActualIndex = actualIndex;

        host.PointerEntered += (_, _) =>
        {
            if (!isDragging) gripHandle.Opacity = 0.92;
        };
        host.PointerExited += (_, _) =>
        {
            if (!isDragging) gripHandle.Opacity = 0.48;
        };

        gripHandle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(gripHandle).Properties.IsLeftButtonPressed)
            {
                return;
            }

            isDragging = true;
            BeginInteractiveSession(gripHandle);
            movedEnough = false;
            targetActualIndex = actualIndex;
            activePointerId = e.Pointer.PointerId;
            pressPoint = e.GetCurrentPoint(containerPanel).Position;
            gripHandle.CapturePointer(e.Pointer);
            host.Opacity = 0.65;
            e.Handled = true;
        };

        gripHandle.PointerMoved += (_, e) =>
        {
            if (!isDragging || e.Pointer.PointerId != activePointerId)
            {
                return;
            }

            Point pt = e.GetCurrentPoint(containerPanel).Position;
            if (!movedEnough)
            {
                double dx = pt.X - pressPoint.X;
                double dy = pt.Y - pressPoint.Y;
                if ((dx * dx) + (dy * dy) < 20)
                {
                    return;
                }
                movedEnough = true;
            }

            int bestIdx = actualIndex;
            double bestDist = double.MaxValue;
            foreach (DraggableListItemSlot slot in slotRecords)
            {
                if (slot.HostGrid.ActualHeight <= 0)
                {
                    continue;
                }

                Point origin = slot.HostGrid.TransformToVisual(containerPanel).TransformPoint(new Point(0, 0));
                double midY = origin.Y + (slot.HostGrid.ActualHeight * 0.5);
                double dist = Math.Abs(pt.Y - midY);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = slot.ActualRecordIndex;
                }
            }

            targetActualIndex = bestIdx;
            foreach (DraggableListItemSlot slot in slotRecords)
            {
                slot.DropIndicator.Visibility =
                    slot.ActualRecordIndex == targetActualIndex && targetActualIndex != actualIndex
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            e.Handled = true;
        };

        async void FinishReorder(PointerRoutedEventArgs e, bool commit)
        {
            if (!isDragging)
            {
                return;
            }

            isDragging = false;
            gripHandle.ReleasePointerCapture(e.Pointer);
            host.Opacity = 1.0;
            gripHandle.Opacity = 0.48;

            foreach (DraggableListItemSlot slot in slotRecords)
            {
                slot.DropIndicator.Visibility = Visibility.Collapsed;
            }

            if (commit && movedEnough && targetActualIndex != actualIndex)
            {
                _activeInteractions.Remove(gripHandle);
                await _viewModel.ReorderCollectionItemsAsync(
                    resolvedCollectionName,
                    actualIndex,
                    targetActualIndex,
                    listSpec.OnReorder);
                NotifyIfAllInteractionsClosed();
            }
            else
            {
                EndInteractiveSession(gripHandle);
            }
        }

        gripHandle.Unloaded += (_, _) => EndInteractiveSession(gripHandle);

        gripHandle.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            FinishReorder(e, commit: true);
        };
        gripHandle.PointerCanceled += (_, e) => FinishReorder(e, commit: false);
        gripHandle.PointerCaptureLost += (_, e) => FinishReorder(e, commit: false);

        slotRecords.Add(new DraggableListItemSlot(actualIndex, host, dropIndicator));
        return host;
    }
}
