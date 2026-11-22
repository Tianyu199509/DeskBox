using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioStageInspectorBar
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioStageInspectorBar(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private Border? _stageInspectorBarBorder;
    private string? _inspectorActiveNodePath;
    private string? _inspectorActivePropKey;
    private TextBox? _inspectorTextBox;
    private ComboBox? _inspectorStyleCombo;
    private ComboBox? _inspectorToneCombo;
    private NumberBox? _inspectorSpacingBox;
    private bool _suppressInspectorControlEvents;

    internal Border? StageInspectorBarBorder { get => _stageInspectorBarBorder; set => _stageInspectorBarBorder = value; }
    internal string? InspectorActiveNodePath { get => _inspectorActiveNodePath; set => _inspectorActiveNodePath = value; }
    internal string? InspectorActivePropKey { get => _inspectorActivePropKey; set => _inspectorActivePropKey = value; }
    internal TextBox? InspectorTextBox { get => _inspectorTextBox; set => _inspectorTextBox = value; }
    internal ComboBox? InspectorStyleCombo { get => _inspectorStyleCombo; set => _inspectorStyleCombo = value; }
    internal ComboBox? InspectorToneCombo { get => _inspectorToneCombo; set => _inspectorToneCombo = value; }
    internal NumberBox? InspectorSpacingBox { get => _inspectorSpacingBox; set => _inspectorSpacingBox = value; }

    internal void RefreshStageInspectorBar()
    {
        if (_stageInspectorBarBorder is null)
        {
            return;
        }

        if (!_session.IsInspectorModeEnabled ||
            _session.IsInPlaceGenerating ||
            _session.IsCapsulePreviewMode ||
            _session.SelectedTargetNode is not { } picked ||
            string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            _inspectorActiveNodePath = null;
            _inspectorActivePropKey = null;
            _inspectorTextBox = null;
            _inspectorStyleCombo = null;
            _inspectorToneCombo = null;
            _inspectorSpacingBox = null;
            _stageInspectorBarBorder.Child = null;
            _stageInspectorBarBorder.Visibility = Visibility.Collapsed;
            return;
        }

        CustomWidgetManifest? manifest = _shell.StageViewport.ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
        CustomWidgetNodeSpec? spec = StudioManifestInspector.FindManifestNodeByPath(manifest, picked.NodePath);
        if (spec is null ||
            StudioWorkspaceEngine.ParsePermissiveJsonNode(_session.CurrentManifestJson!) is not JsonObject rootObj ||
            !StudioManifestInspector.TryResolveJsonNodeByPath(rootObj, picked.NodePath, out JsonObject? targetObj) ||
            targetObj is null)
        {
            _inspectorActiveNodePath = null;
            _inspectorActivePropKey = null;
            _stageInspectorBarBorder.Child = null;
            _stageInspectorBarBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // Preserve focus & cursor while user is actively typing/nudging inside the bar
        bool hasFocusedInput =
            (_inspectorTextBox?.FocusState ?? FocusState.Unfocused) != FocusState.Unfocused ||
            (_inspectorSpacingBox?.FocusState ?? FocusState.Unfocused) != FocusState.Unfocused ||
            (_inspectorStyleCombo?.FocusState ?? FocusState.Unfocused) != FocusState.Unfocused ||
            (_inspectorToneCombo?.FocusState ?? FocusState.Unfocused) != FocusState.Unfocused;
        if (string.Equals(_inspectorActiveNodePath, picked.NodePath, StringComparison.Ordinal) &&
            _stageInspectorBarBorder.Visibility == Visibility.Visible &&
            _stageInspectorBarBorder.Child is not null &&
            hasFocusedInput)
        {
            return;
        }

        ElementTheme stageTheme = _shell.StageViewport.StageBorder?.RequestedTheme == ElementTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;
        _stageInspectorBarBorder.RequestedTheme = stageTheme;

        var wrap = new CustomWidgetWrapPanel
        {
            HorizontalSpacing = 6,
            VerticalSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        string nodeTag = CustomWidgetNativeRenderer.FormatPickedNodeTag(spec.Type, picked.DisplayLabel, _loc);
        wrap.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Background = StudioUiPrimitives.StageThemeBrush(_shell, "SubtleFillColorSecondaryBrush", stageTheme),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = nodeTag,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = StudioUiPrimitives.StageThemeBrush(_shell, "AccentTextFillColorPrimaryBrush", stageTheme),
                MaxWidth = 148,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        });

        bool hasParent = StudioManifestInspector.TryGetParentNodePath(rootObj, picked.NodePath, out string parentPath);
        var upBtn = new Button
        {
            Height = 26,
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            IsEnabled = hasParent,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE74A", _loc.T("CustomWidget.Workshop.Inspector.ParentNode"))
        };
        if (hasParent)
        {
            string capturedParent = parentPath;
            upBtn.Click += (_, _) =>
            {
                CustomWidgetManifest? curM = _shell.StageViewport.ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
                CustomWidgetNodeSpec? parentSpec = StudioManifestInspector.FindManifestNodeByPath(curM, capturedParent);
                if (parentSpec is null)
                {
                    return;
                }

                string parentLabel = !string.IsNullOrWhiteSpace(parentSpec.Title) ? parentSpec.Title
                    : (!string.IsNullOrWhiteSpace(parentSpec.Text) ? parentSpec.Text : parentSpec.Type);
                _inspectorActiveNodePath = null;
                _shell.StageViewport.OnPreviewNodePicked(new CustomWidgetPickedNode(capturedParent, parentSpec.Type, parentLabel));
            };
        }
        wrap.Children.Add(upBtn);

        _suppressInspectorControlEvents = true;
        try
        {
            _inspectorTextBox = null;
            _inspectorStyleCombo = null;
            _inspectorToneCombo = null;
            _inspectorSpacingBox = null;
            _inspectorActivePropKey = null;

            if (TryResolveInspectorTextBinding(spec, targetObj, out string propKey, out string rawPropVal))
            {
                _inspectorActivePropKey = propKey;
                string capturedPath = picked.NodePath;
                if (StudioManifestInspector.TryExtractPureI18nKey(rawPropVal, out string i18nKey))
                {
                    string normCulture = CustomWidgetLocaleCatalog.NormalizeLocaleFileCode(_session.EffectivePreviewCultureName);
                    string localizedText = rawPropVal;
                    if (_session.CurrentLocales is { HasAnyLocale: true } cat)
                    {
                        if (cat.Locales.TryGetValue(normCulture, out IReadOnlyDictionary<string, string>? activeDict) &&
                            activeDict.TryGetValue(i18nKey, out string? foundActive))
                        {
                            localizedText = foundActive;
                        }
                        else
                        {
                            foreach (string fb in new[] { "en-US", "zh-CN" })
                            {
                                if (cat.Locales.TryGetValue(fb, out IReadOnlyDictionary<string, string>? fbDict) &&
                                    fbDict.TryGetValue(i18nKey, out string? fbVal) &&
                                    fbVal is not null)
                                {
                                    localizedText = fbVal;
                                    break;
                                }
                            }
                        }
                    }

                    string capturedI18nKey = i18nKey;
                    _inspectorTextBox = new TextBox
                    {
                        MinWidth = 136,
                        MaxWidth = 220,
                        FontSize = 12,
                        PlaceholderText = _loc.T("CustomWidget.Workshop.Inspector.TextPlaceholder"),
                        Text = localizedText,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ToolTipService.SetToolTip(_inspectorTextBox, $"${{$i18n.{capturedI18nKey}}} ({normCulture})");
                    _inspectorTextBox.TextChanged += (s, _) =>
                    {
                        if (_suppressInspectorControlEvents || s is not TextBox tb)
                        {
                            return;
                        }

                        _shell.ApplyZeroTokenNodeEditInPlace(
                            capturedPath,
                            jsonProperty: null,
                            nextPropertyValue: null,
                            i18nKeyToUpdate: capturedI18nKey,
                            i18nValue: tb.Text,
                            coalesceGroupKey: $"i18n:{capturedI18nKey}",
                            changeSummary: _loc.Format("CustomWidget.Workshop.Inspector.EditSummaryFormat", propKey));
                    };
                    wrap.Children.Add(_inspectorTextBox);
                }
                else if (rawPropVal.Contains("${", StringComparison.Ordinal))
                {
                    wrap.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(7, 3, 7, 3),
                        Background = StudioUiPrimitives.StageThemeBrush(_shell, "SubtleFillColorTertiaryBrush", stageTheme),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = rawPropVal,
                            FontSize = 11,
                            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                            MaxWidth = 150,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    });
                    var aiBtn = new Button
                    {
                        Height = 26,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(5),
                        FontSize = 11.5,
                        Content = _loc.T("CustomWidget.Workshop.Inspector.AskAiEdit")
                    };
                    aiBtn.Click += (_, _) => _shell.ChatPanel.RefineTextBox?.Focus(FocusState.Programmatic);
                    wrap.Children.Add(aiBtn);
                }
                else
                {
                    string capturedProp = propKey;
                    _inspectorTextBox = new TextBox
                    {
                        MinWidth = 136,
                        MaxWidth = 220,
                        FontSize = 12,
                        PlaceholderText = _loc.T("CustomWidget.Workshop.Inspector.TextPlaceholder"),
                        Text = rawPropVal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    _inspectorTextBox.TextChanged += (s, _) =>
                    {
                        if (_suppressInspectorControlEvents || s is not TextBox tb)
                        {
                            return;
                        }

                        _shell.ApplyZeroTokenNodeEditInPlace(
                            capturedPath,
                            jsonProperty: capturedProp,
                            nextPropertyValue: JsonValue.Create(tb.Text ?? string.Empty),
                            coalesceGroupKey: $"node:{capturedPath}:{capturedProp}",
                            changeSummary: _loc.Format("CustomWidget.Workshop.Inspector.EditSummaryFormat", capturedProp));
                    };
                    wrap.Children.Add(_inspectorTextBox);
                }
            }

            if (string.Equals(spec.Type, "Text", StringComparison.OrdinalIgnoreCase))
            {
                string[] styles = ["caption", "body", "bodyStrong", "subtitle", "title", "hero", "mono"];
                string currentStyle = targetObj.TryGetPropertyValue("style", out JsonNode? styleNode) &&
                                      styleNode is JsonValue sv &&
                                      sv.TryGetValue(out string? strStyle) &&
                                      !string.IsNullOrWhiteSpace(strStyle)
                    ? strStyle.Trim()
                    : (string.IsNullOrWhiteSpace(spec.Style) ? "body" : spec.Style);
                _inspectorStyleCombo = new ComboBox
                {
                    MinWidth = 96,
                    FontSize = 11.5,
                    ItemsSource = styles,
                    SelectedItem = styles.FirstOrDefault(s => string.Equals(s, currentStyle, StringComparison.OrdinalIgnoreCase)) ?? "body",
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(_inspectorStyleCombo, _loc.T("CustomWidget.Workshop.Inspector.StyleTip"));
                string capturedPath = picked.NodePath;
                _inspectorStyleCombo.SelectionChanged += (s, _) =>
                {
                    if (_suppressInspectorControlEvents || s is not ComboBox cb || cb.SelectedItem is not string chosen)
                    {
                        return;
                    }

                    _shell.ApplyZeroTokenNodeEditInPlace(
                        capturedPath,
                        jsonProperty: "style",
                        nextPropertyValue: JsonValue.Create(chosen),
                        coalesceGroupKey: $"node:{capturedPath}:style",
                        changeSummary: _loc.Format("CustomWidget.Workshop.Inspector.EditSummaryFormat", $"style={chosen}"));
                };
                wrap.Children.Add(_inspectorStyleCombo);
            }

            if (string.Equals(spec.Type, "Card", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Type, "StatCard", StringComparison.OrdinalIgnoreCase))
            {
                string[] tones = ["default", "subtle", "accent", "success", "warning", "danger", "emerald", "cyan", "violet", "coral"];
                string currentTone = targetObj.TryGetPropertyValue("tone", out JsonNode? toneNode) &&
                                     toneNode is JsonValue tv &&
                                     tv.TryGetValue(out string? strTone) &&
                                     !string.IsNullOrWhiteSpace(strTone)
                    ? strTone.Trim()
                    : (string.IsNullOrWhiteSpace(spec.Tone) ? "default" : spec.Tone);
                _inspectorToneCombo = new ComboBox
                {
                    MinWidth = 92,
                    FontSize = 11.5,
                    ItemsSource = tones,
                    SelectedItem = tones.FirstOrDefault(t => string.Equals(t, currentTone, StringComparison.OrdinalIgnoreCase)) ?? "default",
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(_inspectorToneCombo, _loc.T("CustomWidget.Workshop.Inspector.ToneTip"));
                string capturedPath = picked.NodePath;
                _inspectorToneCombo.SelectionChanged += (s, _) =>
                {
                    if (_suppressInspectorControlEvents || s is not ComboBox cb || cb.SelectedItem is not string chosen)
                    {
                        return;
                    }

                    _shell.ApplyZeroTokenNodeEditInPlace(
                        capturedPath,
                        jsonProperty: "tone",
                        nextPropertyValue: JsonValue.Create(chosen),
                        coalesceGroupKey: $"node:{capturedPath}:tone",
                        changeSummary: _loc.Format("CustomWidget.Workshop.Inspector.EditSummaryFormat", $"tone={chosen}"));
                };
                wrap.Children.Add(_inspectorToneCombo);
            }

            if (string.Equals(spec.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Type, "Wrap", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec.Type, "Card", StringComparison.OrdinalIgnoreCase))
            {
                double currentSpacing = spec.Spacing;
                if (targetObj.TryGetPropertyValue("spacing", out JsonNode? spacingNode) &&
                    spacingNode is JsonValue spVal &&
                    spVal.TryGetValue(out double spNum))
                {
                    currentSpacing = spNum;
                }

                _inspectorSpacingBox = new NumberBox
                {
                    Width = 96,
                    Minimum = 0,
                    Maximum = 32,
                    SmallChange = 2,
                    LargeChange = 4,
                    Value = Math.Clamp(currentSpacing, 0, 32),
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(_inspectorSpacingBox, _loc.T("CustomWidget.Workshop.Inspector.SpacingTip"));
                string capturedPath = picked.NodePath;
                _inspectorSpacingBox.ValueChanged += (s, args) =>
                {
                    if (_suppressInspectorControlEvents || double.IsNaN(args.NewValue))
                    {
                        return;
                    }

                    int clamped = Math.Clamp((int)Math.Round(args.NewValue), 0, 32);
                    _shell.ApplyZeroTokenNodeEditInPlace(
                        capturedPath,
                        jsonProperty: "spacing",
                        nextPropertyValue: JsonValue.Create(clamped),
                        coalesceGroupKey: $"node:{capturedPath}:spacing",
                        changeSummary: _loc.Format("CustomWidget.Workshop.Inspector.EditSummaryFormat", $"spacing={clamped}"));
                };
                wrap.Children.Add(_inspectorSpacingBox);
            }

            var closeInspectorBtn = new Button
            {
                Height = 26,
                Width = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Content = new FontIcon { Glyph = "\uE711", FontSize = 10.5 },
                VerticalAlignment = VerticalAlignment.Center
            };
            closeInspectorBtn.Click += (_, _) =>
            {
                _session.SelectedTargetNode = null;
                _inspectorActiveNodePath = null;
                if (_shell.StageViewport.PreviewContent is not null)
                {
                    _shell.StageViewport.PreviewContent.SelectedNodePath = null;
                }
                _shell.ChatPanel.RefreshTargetNodePillChrome();
            };
            wrap.Children.Add(closeInspectorBtn);

            _inspectorActiveNodePath = picked.NodePath;
            _stageInspectorBarBorder.Child = wrap;
            _stageInspectorBarBorder.Visibility = Visibility.Visible;
        }
        finally
        {
            _suppressInspectorControlEvents = false;
        }
    }

    private static bool TryResolveInspectorTextBinding(
        CustomWidgetNodeSpec spec,
        JsonObject targetObj,
        out string propKey,
        out string value)
    {
        propKey = string.Empty;
        value = string.Empty;

        string[] order = string.Equals(spec.Type, "Button", StringComparison.OrdinalIgnoreCase)
            ? ["label", "text", "title"]
            : (string.Equals(spec.Type, "Card", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(spec.Type, "StatCard", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(spec.Type, "Expander", StringComparison.OrdinalIgnoreCase))
                ? ["title", "label", "text"]
                : ["text", "label", "title"];

        foreach (string key in order)
        {
            if (targetObj.TryGetPropertyValue(key, out JsonNode? node) &&
                node is JsonValue jv &&
                jv.TryGetValue(out string? strVal))
            {
                propKey = key;
                value = strVal ?? string.Empty;
                return true;
            }
        }

        if (string.Equals(spec.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(spec.Type, "Badge", StringComparison.OrdinalIgnoreCase))
        {
            propKey = "text";
            value = spec.Text ?? string.Empty;
            return true;
        }

        if (string.Equals(spec.Type, "Button", StringComparison.OrdinalIgnoreCase))
        {
            propKey = "label";
            value = spec.Title ?? spec.Text ?? string.Empty;
            return true;
        }

        return false;
    }

    internal void ClearPreviewElementRefs()
    {
        _stageInspectorBarBorder = null;
        _inspectorActiveNodePath = null;
        _inspectorActivePropKey = null;
        _inspectorTextBox = null;
        _inspectorStyleCombo = null;
        _inspectorToneCombo = null;
        _inspectorSpacingBox = null;
    }
}
