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

internal sealed class StudioStageDialogsAndFlyouts
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioStageDialogsAndFlyouts(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }

    internal async Task ShowWorkspaceCodeViewerDialogAsync()
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.ShellRootGrid.XamlRoot is null || string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
        {
            return;
        }

        _shell.IsDialogOpen = true;
        try
        {
            string manifestCode = _session.CurrentManifestJson ?? string.Empty;
            string? logicCode = _session.CurrentLogicJs;
            CustomWidgetLocaleCatalog? localeCatalog = _session.ActiveI18nWorkingCatalog ?? _session.CurrentLocales;

            var tabs = new List<(string FileName, string Content)>
            {
                ("manifest.json", manifestCode)
            };
            if (!string.IsNullOrWhiteSpace(logicCode))
            {
                tabs.Add(("logic.js", logicCode!));
            }

            var localeFiles = new List<(string Label, string Content)>();
            if (localeCatalog is { HasAnyLocale: true })
            {
                if (localeCatalog.Meta is { Hashes.Count: > 0 } meta)
                {
                    localeFiles.Add((
                        $"locales/{CustomWidgetLocaleCatalog.MetaFileName}",
                        CustomWidgetLocaleCatalog.FormatMetaJson(meta)));
                }

                foreach (CustomWidgetSupportedLocaleInfo info in CustomWidgetLocaleCatalog.SupportedLocales)
                {
                    if (localeCatalog.Locales.TryGetValue(info.Code, out IReadOnlyDictionary<string, string>? dict) &&
                        dict.Count > 0)
                    {
                        localeFiles.Add((
                            $"locales/{info.Code}.json  ({info.NativeName})",
                            CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(dict)));
                    }
                }

                foreach ((string code, IReadOnlyDictionary<string, string> dict) in localeCatalog.Locales)
                {
                    if (dict.Count == 0 ||
                        CustomWidgetLocaleCatalog.SupportedLocales.Any(s =>
                            string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    localeFiles.Add((
                        $"locales/{code}.json",
                        CustomWidgetLocaleCatalog.FormatLocaleDictionaryJson(dict)));
                }

                if (localeFiles.Count > 0)
                {
                    tabs.Add(("locales/", localeFiles[0].Content));
                }
            }

            const string liveStateTabTag = "$state";
            if (_shell.StageViewport.PreviewVm is { } liveVm)
            {
                tabs.Add((liveStateTabTag, FormatLiveRuntimeStateJson(liveVm)));
            }

            var rootStack = new StackPanel { Spacing = 10, MinWidth = 560 };
            var topBar = new Grid { ColumnSpacing = 8 };
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tabSegmented = new ToolkitControls.Segmented
            {
                SelectionMode = ListViewSelectionMode.Single,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            foreach ((string fileName, _) in tabs)
            {
                string displayLabel = string.Equals(fileName, liveStateTabTag, StringComparison.Ordinal)
                    ? _loc.T("CustomWidget.Workshop.StateInspector.TabTitle")
                    : fileName;
                tabSegmented.Items.Add(new ToolkitControls.SegmentedItem
                {
                    Tag = fileName,
                    Content = displayLabel,
                    FontSize = 12
                });
            }
            tabSegmented.SelectedIndex = 0;
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(tabSegmented);
            topBar.Children.Add(tabSegmented);

            var localeFileCombo = new ComboBox
            {
                MinWidth = 196,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                Visibility = Visibility.Collapsed
            };
            int defaultLocaleIdx = 0;
            string effectiveCulture = _session.EffectivePreviewCultureName;
            for (int i = 0; i < localeFiles.Count; i++)
            {
                localeFileCombo.Items.Add(new ComboBoxItem
                {
                    Content = localeFiles[i].Label,
                    Tag = localeFiles[i].Content
                });
                if (localeFiles[i].Label.StartsWith($"locales/{effectiveCulture}.json", StringComparison.OrdinalIgnoreCase))
                {
                    defaultLocaleIdx = i;
                }
            }
            if (localeFileCombo.Items.Count > 0)
            {
                localeFileCombo.SelectedIndex = defaultLocaleIdx;
            }
            Grid.SetColumn(localeFileCombo, 1);
            topBar.Children.Add(localeFileCombo);

            var codeBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                MinHeight = 360,
                MaxHeight = 460,
                Text = tabs[0].Content
            };
            ScrollViewer.SetVerticalScrollBarVisibility(codeBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(codeBox, ScrollBarVisibility.Auto);

            var rightActionButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            var resetStateBtn = new Button
            {
                Height = 30,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Visibility = Visibility.Collapsed,
                Content = StudioUiPrimitives.CreateSmallIconLabel("\uE72C", _loc.T("CustomWidget.Workshop.StateInspector.ResetState"))
            };
            rightActionButtons.Children.Add(resetStateBtn);

            var copyBtn = new Button
            {
                Height = 30,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Content = StudioUiPrimitives.CreateSmallIconLabel("\uE8C8", _loc.T("CustomWidget.Workshop.CodeViewer.CopyCurrent"))
            };
            copyBtn.Click += (_, _) => _shell.SetClipboardText(codeBox.Text ?? string.Empty);
            rightActionButtons.Children.Add(copyBtn);
            Grid.SetColumn(rightActionButtons, 2);
            topBar.Children.Add(rightActionButtons);

            void RefreshSelectedCodeText()
            {
                int idx = tabSegmented.SelectedIndex;
                if (idx < 0 || idx >= tabs.Count)
                {
                    return;
                }

                string selectedTag = tabs[idx].FileName;
                bool isLocalesTab = string.Equals(selectedTag, "locales/", StringComparison.Ordinal);
                bool isLiveStateTab = string.Equals(selectedTag, liveStateTabTag, StringComparison.Ordinal);
                localeFileCombo.Visibility = isLocalesTab && localeFiles.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                resetStateBtn.Visibility = isLiveStateTab && _shell.StageViewport.PreviewVm is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (isLocalesTab && localeFileCombo.SelectedItem is ComboBoxItem { Tag: string locJson })
                {
                    codeBox.Text = locJson;
                }
                else if (isLiveStateTab && _shell.StageViewport.PreviewVm is { } currentVm)
                {
                    codeBox.Text = FormatLiveRuntimeStateJson(currentVm);
                }
                else
                {
                    codeBox.Text = tabs[idx].Content;
                }
            }

            resetStateBtn.Click += (_, _) =>
            {
                if (_shell.StageViewport.PreviewVm is { } vm && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson))
                {
                    vm.ApplyPreviewBundle(
                        _session.CurrentManifestJson!,
                        _session.CurrentLogicJs,
                        _session.CurrentLocales,
                        preserveRuntimeData: false);
                    if (!_session.IsEditMode && !string.IsNullOrWhiteSpace(_session.PreviewSampleDataJson) && !_session.IsSampleDataMutedInPreview)
                    {
                        vm.ApplyPreviewSampleDataJson(_session.PreviewSampleDataJson, seedOnlyIfEmptyOrSample: false);
                    }
                    RefreshSelectedCodeText();
                }
            };

            tabSegmented.SelectionChanged += (_, _) => RefreshSelectedCodeText();
            localeFileCombo.SelectionChanged += (_, _) => RefreshSelectedCodeText();

            rootStack.Children.Add(topBar);
            rootStack.Children.Add(codeBox);

            var dialog = new ContentDialog
            {
                XamlRoot = _shell.ShellRootGrid.XamlRoot,
                RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                Title = _loc.T("CustomWidget.Workshop.CodeViewer.Title"),
                Content = rootStack,
                CloseButtonText = _loc.T("Common.Close"),
                DefaultButton = ContentDialogButton.Close
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            await dialog.ShowAsync();
        }
        finally
        {
            _shell.IsDialogOpen = false;
        }
    }

    private static string FormatLiveRuntimeStateJson(CustomWidgetViewModel vm)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions
               {
                   Indented = true,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
               }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("$state");
            foreach ((string k, JsonElement el) in vm.StateJson)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
            }
            foreach ((string k, string v) in vm.State)
            {
                if (!vm.StateJson.ContainsKey(k))
                {
                    writer.WriteString(k, v);
                }
            }
            writer.WriteEndObject();

            writer.WriteStartObject("$computed");
            foreach ((string k, JsonElement el) in vm.Computed)
            {
                if (el.ValueKind != JsonValueKind.Undefined)
                {
                    writer.WritePropertyName(k);
                    el.WriteTo(writer);
                }
            }
            writer.WriteEndObject();

            writer.WriteStartObject("collections");
            foreach ((string colName, List<CustomWidgetRecord> records) in vm.Collections)
            {
                writer.WriteStartArray(colName);
                foreach (CustomWidgetRecord record in records)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", record.Id);
                    foreach ((string fk, JsonElement fv) in record.FieldsJson)
                    {
                        if (!string.Equals(fk, "id", StringComparison.Ordinal) && fv.ValueKind != JsonValueKind.Undefined)
                        {
                            writer.WritePropertyName(fk);
                            fv.WriteTo(writer);
                        }
                    }
                    foreach ((string fk, string fv) in record.Fields)
                    {
                        if (!string.Equals(fk, "id", StringComparison.Ordinal) && !record.FieldsJson.ContainsKey(fk))
                        {
                            writer.WriteString(fk, fv);
                        }
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();

            writer.WriteStartObject("$settings");
            foreach ((string k, string v) in vm.Settings)
            {
                writer.WriteString(k, v);
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal Flyout BuildSizeQuickEditFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var stack = new StackPanel { Spacing = 10, Width = 248 };

        var presetsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        (string Key, int W, int H)[] presets =
        [
            ("CustomWidget.Workshop.CoCreate.SizeCompact", 340, 200),
            ("CustomWidget.Workshop.CoCreate.SizeStandard", 340, 340),
            ("CustomWidget.Workshop.CoCreate.SizeTall", 340, 460),
            ("CustomWidget.Workshop.CoCreate.SizeWide", 480, 340)
        ];

        var widthBox = new NumberBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.SizeWidth"),
            Minimum = 200,
            Maximum = 640,
            SmallChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var heightBox = new NumberBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.SizeHeight"),
            Minimum = 140,
            Maximum = 720,
            SmallChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };

        foreach ((string key, int w, int h) in presets)
        {
            int cw = w;
            int ch = h;
            Button pBtn = StudioUiPrimitives.CreateChipButton($"{_loc.T(key)} {cw}×{ch}");
            pBtn.Click += (_, _) =>
            {
                flyout.Hide();
                _shell.ApplyZeroTokenMetadataEdit(newSize: new CustomWidgetSizeSpec { Width = cw, Height = ch });
            };
            presetsRow.Children.Add(pBtn);
        }
        stack.Children.Add(presetsRow);

        var dimsGrid = new Grid { ColumnSpacing = 8 };
        dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dimsGrid.Children.Add(widthBox);
        Grid.SetColumn(heightBox, 1);
        dimsGrid.Children.Add(heightBox);
        stack.Children.Add(dimsGrid);

        var applyBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = StudioUiPrimitives.AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.CoCreate.ApplyQuickEdit")
        };
        applyBtn.Click += (_, _) =>
        {
            int w = double.IsNaN(widthBox.Value) ? 320 : Math.Clamp((int)Math.Round(widthBox.Value), 200, 640);
            int h = double.IsNaN(heightBox.Value) ? 260 : Math.Clamp((int)Math.Round(heightBox.Value), 140, 720);
            flyout.Hide();
            _shell.ApplyZeroTokenMetadataEdit(newSize: new CustomWidgetSizeSpec { Width = w, Height = h });
        };
        stack.Children.Add(applyBtn);

        BindFlyoutToStudioTheme(flyout, stack);
        flyout.Opening += (_, _) =>
        {
            CustomWidgetSizeSpec cur = CustomWidgetManifestParser.Parse(_session.CurrentManifestJson ?? string.Empty).Manifest?.DefaultSize
                ?? new CustomWidgetSizeSpec();
            widthBox.Value = cur.Width;
            heightBox.Value = cur.Height;
        };

        flyout.Content = stack;
        return flyout;
    }

    internal Flyout BuildSettingsPreviewFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var hostScroll = new ScrollViewer
        {
            MaxHeight = 440,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var rootStack = new StackPanel
        {
            Spacing = 12,
            Width = 308,
            Padding = new Thickness(2, 0, 4, 2)
        };
        hostScroll.Content = rootStack;

        void RebuildControls()
        {
            rootStack.Children.Clear();
            CustomWidgetViewModel? vm = _shell.StageViewport.PreviewVm;
            if (vm is null)
            {
                return;
            }

            IReadOnlyList<CustomWidgetSettingDefinition> localizedSettings =
                vm.Manifest.ResolveLocalizedSettings(_session.EffectivePreviewCultureName);
            if (localizedSettings.Count == 0)
            {
                return;
            }

            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Spacing = 2 };
            titleStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.Stage.SettingsFlyoutTitle"),
                Style = StudioUiPrimitives.TextStyle("BodyStrongTextBlockStyle")
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = _loc.T("CustomWidget.Workshop.Stage.SettingsFlyoutSubtitle"),
                Style = StudioUiPrimitives.TextStyle("CaptionTextBlockStyle"),
                Foreground = StudioUiPrimitives.ThemeBrush(_shell, "TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            headerGrid.Children.Add(titleStack);

            var resetBtn = new Button
            {
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(5),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Top,
                Content = _loc.T("CustomWidget.Workshop.Stage.SettingsResetDefaults")
            };
            resetBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(_shell, "StageSettingsResetDefaults", async () =>
            {
                _session.SessionSettingOverrides.Clear();
                _session.SessionSecretOverrides.Clear();
                _session.HasDirtySettingOverrides = true;
                _session.Committed = false;
                foreach (CustomWidgetSettingDefinition def in localizedSettings)
                {
                    if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
                    {
                        await vm.SetSecretValueAsync(def.Key, string.Empty);
                    }
                    else
                    {
                        await vm.SetSettingValueAsync(def.Key, def.DefaultValue);
                    }
                }

                RebuildControls();
            });
            Grid.SetColumn(resetBtn, 1);
            headerGrid.Children.Add(resetBtn);
            rootStack.Children.Add(headerGrid);

            rootStack.Children.Add(new Border
            {
                Height = 1,
                Background = StudioUiPrimitives.ThemeBrush(_shell, "DividerStrokeColorDefaultBrush")
            });

            var formOptions = new CustomWidgetSettingsFormOptions
            {
                Localization = _loc,
                CultureName = _session.EffectivePreviewCultureName,
                OwnerWindowHandle = _shell.WindowHandle,
                ThemeScope = rootStack,
                CommitSettingAsync = async (key, nextVal) =>
                {
                    _session.SessionSettingOverrides[key] = nextVal;
                    _session.HasDirtySettingOverrides = true;
                    _session.Committed = false;
                    await vm.SetSettingValueAsync(key, nextVal);
                },
                ReadSecret = key => vm.Secrets.TryGetValue(key, out string? sVal) ? sVal : string.Empty,
                CommitSecretAsync = async (key, nextSecret) =>
                {
                    _session.SessionSecretOverrides[key] = nextSecret;
                    _session.HasDirtySettingOverrides = true;
                    _session.Committed = false;
                    await vm.SetSecretValueAsync(key, nextSecret);
                }
            };

            foreach (CustomWidgetSettingDefinition def in localizedSettings)
            {
                string settingKey = def.Key;
                string currentVal = vm.Settings.TryGetValue(settingKey, out string? liveVal)
                    ? liveVal
                    : def.DefaultValue;

                rootStack.Children.Add(
                    CustomWidgetSettingsFormBuilder.BuildCompactFlyoutEntry(
                        def,
                        currentVal,
                        formOptions));
            }
        }

        BindFlyoutToStudioTheme(flyout, hostScroll);
        flyout.Opening += (_, _) => RebuildControls();
        flyout.Content = hostScroll;
        return flyout;
    }

    internal Flyout BuildHeaderQuickEditFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var stack = new StackPanel { Spacing = 10, Width = 268 };
        var quickEmojiHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };

        var emojiInput = new TextBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.EmojiLabel"),
            MaxLength = 32
        };
        var titleInput = new TextBox
        {
            Header = _loc.T("CustomWidget.Workshop.CoCreate.TitleLabel"),
            MaxLength = 60
        };
        var applyBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = StudioUiPrimitives.AccentButtonStyle(),
            Content = _loc.T("CustomWidget.Workshop.CoCreate.ApplyQuickEdit")
        };
        applyBtn.Click += (_, _) =>
        {
            flyout.Hide();
            _shell.ApplyZeroTokenMetadataEdit(
                newName: titleInput.Text,
                newIcon: emojiInput.Text);
        };

        stack.Children.Add(quickEmojiHost);
        stack.Children.Add(emojiInput);
        stack.Children.Add(titleInput);
        stack.Children.Add(applyBtn);

        BindFlyoutToStudioTheme(flyout, stack);
        flyout.Opening += (_, _) =>
        {
            CustomWidgetManifest? cur = _shell.StageViewport.ResolvePreviewManifestWithSidecar(_session.CurrentManifestJson);
            string curEmoji = _shell.PreviewTheme.ResolveDisplayEmoji(cur);
            emojiInput.Text = curEmoji;
            titleInput.Text = cur?.ResolveLocalizedName(_session.EffectivePreviewCultureName) ?? string.Empty;
            quickEmojiHost.Content = EmojiPickerFlyoutHelper.BuildCompactQuickEmojiStrip(
                StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                curEmoji,
                picked => emojiInput.Text = picked,
                maxCount: 14);
        };

        flyout.Content = stack;
        return flyout;
    }

    private void BindFlyoutToStudioTheme(Flyout flyout, FrameworkElement? contentRoot = null) =>
        StudioUiPrimitives.BindFlyoutToStudioTheme(_shell, flyout, contentRoot);
}
