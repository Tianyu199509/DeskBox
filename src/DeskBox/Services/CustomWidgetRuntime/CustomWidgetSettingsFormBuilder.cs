using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace DeskBox.Services;

public sealed class CustomWidgetSettingsFormOptions
{
    public required LocalizationService Localization { get; init; }
    public string? CultureName { get; init; }
    public IntPtr OwnerWindowHandle { get; init; }
    public DependencyObject? ThemeScope { get; init; }
    public required Func<string, string, Task> CommitSettingAsync { get; init; }
    public Func<string, string>? ReadSecret { get; init; }
    public Func<string, string, Task>? CommitSecretAsync { get; init; }
    public Action<Action<IReadOnlyDictionary<string, string>>>? RegisterExternalSync { get; init; }
    public Action<bool>? SetModalPickerActive { get; init; }
}

/// <summary>
/// Single shared builder for Custom Widget setting controls across both the Settings Window
/// (<see cref="SettingsCard"/> + drag-reorder module card) and Studio's quick-settings flyout,
/// preventing UI drift when new setting types or interaction guards are introduced.
/// </summary>
public static class CustomWidgetSettingsFormBuilder
{
    private static readonly string[] NumericKeyHints =
    [
        "count", "top", "max", "min", "limit", "goal", "target", "balance", "wallet",
        "minute", "hour", "second", "sec", "interval", "duration",
        "step", "threshold", "days", "cups", "size", "refresh", "rate"
    ];

    private static void FireAndForgetSafeUi(Func<Task> action)
    {
        _ = RunSafeUiAsync(action);
    }

    private static async Task RunSafeUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CustomWidgetSettingsFormBuilder] UI handler error: {ex.Message}");
        }
    }

    public static bool IsNumericParameterDefinition(
        CustomWidgetSettingDefinition def,
        out double defaultNumericValue)
    {
        ArgumentNullException.ThrowIfNull(def);

        bool parsedDefault = double.TryParse(
            def.DefaultValue,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out defaultNumericValue);

        if (string.Equals(def.Type, "number", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "int", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "integer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase))
        {
            if (!parsedDefault)
            {
                defaultNumericValue = 0;
            }
            return true;
        }

        if (!parsedDefault)
        {
            return false;
        }

        string key = def.Key ?? string.Empty;
        foreach (string hint in NumericKeyHints)
        {
            if (key.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string ResolveSettingIconGlyph(CustomWidgetSettingDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8D7";
        }
        if (string.Equals(def.Type, "folder", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Type, "directory", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8B7";
        }
        if (string.Equals(def.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8A5";
        }

        string key = def.Key ?? string.Empty;
        if (key.Contains("sort", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8CB";
        }
        if (key.Contains("count", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("top", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8FD";
        }
        if (key.Contains("interval", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("time", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("duration", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("minute", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("focus", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE823";
        }
        if (key.Contains("break", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("rest", StringComparison.OrdinalIgnoreCase))
        {
            return "\uEC32";
        }
        if (key.Contains("goal", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("target", StringComparison.OrdinalIgnoreCase))
        {
            return key.Contains("wallet", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("balance", StringComparison.OrdinalIgnoreCase)
                ? "\uE8C7"
                : "\uE734";
        }
        if (key.Contains("wallet", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("balance", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8C7";
        }
        if (key.Contains("currency", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE774";
        }
        if (key.Contains("flow", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("cycle", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE895";
        }
        if (key.Contains("notify", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("remind", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("sound", StringComparison.OrdinalIgnoreCase))
        {
            return "\uEA8F";
        }
        if (key.Contains("drive", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("folder", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8B7";
        }
        if (key.Contains("threshold", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("alert", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE945";
        }

        return string.Equals(def.Type, "toggle", StringComparison.OrdinalIgnoreCase)
            ? "\uE713"
            : "\uE8A5";
    }

    public static SettingsCard BuildSettingsCard(
        CustomWidgetSettingDefinition def,
        string currentVal,
        CustomWidgetSettingsFormOptions options)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(options);

        string culture = ResolveCulture(options);
        var card = new SettingsCard
        {
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Header = def.Label,
            HeaderIcon = new FontIcon { Glyph = ResolveSettingIconGlyph(def) }
        };

        if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
        {
            card.Description = LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.Secret.SettingsDpapiNote");
            card.Content = BuildSecretControl(def, options, compactFlyoutLayout: false);
            return card;
        }

        if (!string.IsNullOrWhiteSpace(def.Description))
        {
            card.Description = def.Description;
        }

        if (string.Equals(def.Type, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            card.Content = BuildToggleSwitch(def, currentVal, options);
            return card;
        }

        if (def.Options.Count > 0)
        {
            card.Content = BuildOptionsControl(def, currentVal, options, compactFlyoutLayout: false);
            return card;
        }

        if (IsNumericParameterDefinition(def, out double defaultNumericValue))
        {
            card.Content = BuildNumberControl(def, currentVal, defaultNumericValue, options, compactFlyoutLayout: false);
            return card;
        }

        card.Content = BuildTextOrPathControl(def, currentVal, options, compactFlyoutLayout: false);
        return card;
    }

    public static FrameworkElement BuildCompactFlyoutEntry(
        CustomWidgetSettingDefinition def,
        string currentVal,
        CustomWidgetSettingsFormOptions options)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(options);

        if (string.Equals(def.Type, "moduleList", StringComparison.OrdinalIgnoreCase) && def.Options.Count > 0)
        {
            return BuildCompactModuleListSection(def, currentVal, options);
        }

        if (string.Equals(def.Type, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = def.Label,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });

            ToggleSwitch toggle = BuildToggleSwitch(def, currentVal, options);
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            return row;
        }

        if (def.Options.Count > 0)
        {
            return BuildOptionsControl(def, currentVal, options, compactFlyoutLayout: true);
        }

        if (IsNumericParameterDefinition(def, out double defaultNumericValue))
        {
            return BuildNumberControl(def, currentVal, defaultNumericValue, options, compactFlyoutLayout: true);
        }

        if (string.Equals(def.Type, "secret", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSecretControl(def, options, compactFlyoutLayout: true);
        }

        return BuildTextOrPathControl(def, currentVal, options, compactFlyoutLayout: true);
    }

    public static Border BuildModuleReorderGroupCard(
        CustomWidgetSettingDefinition def,
        string initialRaw,
        CustomWidgetSettingsFormOptions options,
        bool canDragReorderModules,
        Style? groupStyle = null,
        Style? titleTextStyle = null,
        Style? descriptionTextStyle = null)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(options);

        string settingKey = def.Key;
        string liveRaw = initialRaw;
        LocalizationService loc = options.Localization;

        var groupBorder = new Border();
        if (groupStyle is not null)
        {
            groupBorder.Style = groupStyle;
        }

        var groupStack = new StackPanel();

        var headerGrid = new Grid
        {
            MinHeight = 62,
            Padding = new Thickness(16, 12, 16, 12),
            ColumnSpacing = 20
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        headerGrid.Children.Add(new FontIcon
        {
            Glyph = "\uE8FD",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        var headerTextStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleTb = new TextBlock { Text = def.Label };
        if (titleTextStyle is not null)
        {
            titleTb.Style = titleTextStyle;
        }
        headerTextStack.Children.Add(titleTb);

        var descTb = new TextBlock { Text = loc.T("CustomWidget.Native.Detail.ModuleDescription") };
        if (descriptionTextStyle is not null)
        {
            descTb.Style = descriptionTextStyle;
        }
        headerTextStack.Children.Add(descTb);
        Grid.SetColumn(headerTextStack, 1);
        headerGrid.Children.Add(headerTextStack);

        var countBadge = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        if (descriptionTextStyle is not null)
        {
            countBadge.Style = descriptionTextStyle;
        }
        Grid.SetColumn(countBadge, 2);
        headerGrid.Children.Add(countBadge);
        groupStack.Children.Add(headerGrid);

        groupStack.Children.Add(new Border
        {
            Height = 1,
            Background = ResolveScopedBrush(
                "DividerStrokeColorDefaultBrush",
                options.ThemeScope,
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Opacity = 0.72
        });

        var itemContainerStyle = new Style(typeof(ListViewItem));
        if (Application.Current?.Resources is { } appRes &&
            appRes.TryGetValue("DefaultListViewItemStyle", out object? baseObj) &&
            baseObj is Style baseItemStyle)
        {
            itemContainerStyle.BasedOn = baseItemStyle;
        }
        itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemContainerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 44d));
        itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 2, 16, 2)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 1, 4, 1)));

        bool allowDragReorder = canDragReorderModules && !DragDropPermissionService.IsCurrentProcessElevatedCached();
        var listView = new ListView
        {
            AllowDrop = allowDragReorder,
            CanDragItems = allowDragReorder,
            CanReorderItems = allowDragReorder,
            ReorderMode = allowDragReorder ? ListViewReorderMode.Enabled : ListViewReorderMode.Disabled,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 4, 0, 4),
            ItemContainerStyle = itemContainerStyle
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);

        var rowRefs = new Dictionary<string, (TextBlock Label, ToggleSwitch Switch)>(StringComparer.Ordinal);
        bool suppressSwitchEvents = false;

        void RefreshRowVisualStates()
        {
            IReadOnlyList<CustomWidgetModuleEntry> parsed =
                CustomWidgetModuleListHelper.ParseEntries(liveRaw, def.Options);
            int enabledCount = parsed.Count(static e => e.Enabled);
            countBadge.Text = loc.Format(
                "CustomWidget.Native.Detail.ModuleEnabledCount",
                enabledCount,
                parsed.Count);

            suppressSwitchEvents = true;
            try
            {
                foreach (CustomWidgetModuleEntry entry in parsed)
                {
                    if (rowRefs.TryGetValue(entry.Id, out var pair))
                    {
                        pair.Label.Opacity = entry.Enabled ? 1.0 : 0.56;
                        if (pair.Switch.IsOn != entry.Enabled)
                        {
                            pair.Switch.IsOn = entry.Enabled;
                        }
                        pair.Switch.IsEnabled = !entry.Enabled || enabledCount > 1;
                    }
                }
            }
            finally
            {
                suppressSwitchEvents = false;
            }
        }

        void PopulateListViewItems()
        {
            listView.Items.Clear();
            rowRefs.Clear();

            IReadOnlyList<CustomWidgetModuleEntry> entries =
                CustomWidgetModuleListHelper.ParseEntries(liveRaw, def.Options);
            int enabledCount = entries.Count(static e => e.Enabled);
            countBadge.Text = loc.Format(
                "CustomWidget.Native.Detail.ModuleEnabledCount",
                enabledCount,
                entries.Count);

            foreach (CustomWidgetModuleEntry entry in entries)
            {
                string moduleId = entry.Id;

                var rowGrid = new Grid
                {
                    ColumnSpacing = 14,
                    Background = new SolidColorBrush(Colors.Transparent)
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Brush gripBrush = ResolveScopedBrush(
                    "TextFillColorSecondaryBrush",
                    options.ThemeScope,
                    Color.FromArgb(0xB0, 0xA0, 0xA0, 0xA0));
                var gripHost = new Border
                {
                    Width = 18,
                    Height = 20,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.CreateSixDotGripIcon(gripBrush)
                };
                ToolTipService.SetToolTip(gripHost, loc.T("CustomWidget.Module.DragHint"));

                rowGrid.PointerEntered += (_, _) => gripHost.Opacity = 0.95;
                rowGrid.PointerExited += (_, _) => gripHost.Opacity = 0.55;

                var labelTb = new TextBlock
                {
                    Text = entry.Label,
                    FontSize = 14,
                    Opacity = entry.Enabled ? 1.0 : 0.56,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var sw = new ToggleSwitch
                {
                    MinWidth = 0,
                    OnContent = string.Empty,
                    OffContent = string.Empty,
                    IsOn = entry.Enabled,
                    IsEnabled = !entry.Enabled || enabledCount > 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(sw, entry.Label);

                sw.Toggled += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    if (suppressSwitchEvents)
                    {
                        return;
                    }

                    liveRaw = CustomWidgetModuleListHelper.SetModuleEnabled(
                        liveRaw,
                        def.Options,
                        moduleId,
                        sw.IsOn);
                    RefreshRowVisualStates();
                    await options.CommitSettingAsync(settingKey, liveRaw);
                });

                Grid.SetColumn(gripHost, 0);
                Grid.SetColumn(labelTb, 1);
                Grid.SetColumn(sw, 2);
                rowGrid.Children.Add(gripHost);
                rowGrid.Children.Add(labelTb);
                rowGrid.Children.Add(sw);

                rowRefs[moduleId] = (labelTb, sw);

                var lvi = new ListViewItem
                {
                    Tag = moduleId,
                    Content = rowGrid
                };
                AutomationProperties.SetName(lvi, entry.Label);
                listView.Items.Add(lvi);
            }
        }

        listView.DragItemsCompleted += (_, _) => FireAndForgetSafeUi(async () =>
        {
            IReadOnlyList<CustomWidgetModuleEntry> currentEntries =
                CustomWidgetModuleListHelper.ParseEntries(liveRaw, def.Options);
            var enabledMap = currentEntries.ToDictionary(
                static e => e.Id,
                static e => e.Enabled,
                StringComparer.OrdinalIgnoreCase);

            var reorderedTokens = new List<string>(listView.Items.Count);
            for (int i = 0; i < listView.Items.Count; i++)
            {
                if (listView.Items[i] is ListViewItem { Tag: string id })
                {
                    bool isOn = !enabledMap.TryGetValue(id, out bool en) || en;
                    reorderedTokens.Add(isOn ? id : "-" + id);
                }
            }

            string nextRaw = CustomWidgetModuleListHelper.SerializeEntries(
                CustomWidgetModuleListHelper.ParseEntries(
                    string.Join(",", reorderedTokens),
                    def.Options));

            if (!string.Equals(nextRaw, liveRaw, StringComparison.Ordinal))
            {
                liveRaw = nextRaw;
                RefreshRowVisualStates();
                await options.CommitSettingAsync(settingKey, liveRaw);
            }
        });

        PopulateListViewItems();

        options.RegisterExternalSync?.Invoke(latestSettings =>
        {
            string synced = latestSettings.TryGetValue(settingKey, out string? v)
                ? v
                : def.DefaultValue;
            if (!string.Equals(synced, liveRaw, StringComparison.Ordinal))
            {
                liveRaw = synced;
                PopulateListViewItems();
            }
        });

        groupStack.Children.Add(listView);
        groupBorder.Child = groupStack;
        return groupBorder;
    }

    private static FrameworkElement BuildCompactModuleListSection(
        CustomWidgetSettingDefinition def,
        string initialRaw,
        CustomWidgetSettingsFormOptions options)
    {
        string settingKey = def.Key;
        string liveRaw = initialRaw;
        LocalizationService loc = options.Localization;

        var modSection = new StackPanel { Spacing = 6 };
        modSection.Children.Add(new TextBlock
        {
            Text = def.Label,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold
        });

        var modRowsHost = new StackPanel { Spacing = 4 };

        void RenderModuleRows()
        {
            modRowsHost.Children.Clear();
            IReadOnlyList<CustomWidgetModuleEntry> entries =
                CustomWidgetModuleListHelper.ParseEntries(liveRaw, def.Options);
            int enabledCount = entries.Count(static e => e.Enabled);

            for (int idx = 0; idx < entries.Count; idx++)
            {
                int capturedIdx = idx;
                CustomWidgetModuleEntry entry = entries[idx];

                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 6, 4),
                    Background = ResolveScopedBrush(
                        "SubtleFillColorTertiaryBrush",
                        options.ThemeScope,
                        Color.FromArgb(0x10, 0x80, 0x80, 0x80)),
                    BorderBrush = ResolveScopedBrush(
                        "CardStrokeColorDefaultBrush",
                        options.ThemeScope,
                        Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1)
                };

                var rowGrid = new Grid { ColumnSpacing = 6 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    Content = entry.Label,
                    IsChecked = entry.Enabled,
                    IsEnabled = !entry.Enabled || enabledCount > 1,
                    MinWidth = 0,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                cb.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    bool nextEnabled = cb.IsChecked == true;
                    liveRaw = CustomWidgetModuleListHelper.SetModuleEnabled(
                        liveRaw,
                        def.Options,
                        entry.Id,
                        nextEnabled);
                    await options.CommitSettingAsync(settingKey, liveRaw);
                    RenderModuleRows();
                });
                rowGrid.Children.Add(cb);

                async Task MoveModuleAsync(int delta)
                {
                    int targetIdx = capturedIdx + delta;
                    if (targetIdx < 0 || targetIdx >= entries.Count)
                    {
                        return;
                    }

                    var tokens = entries
                        .Select(static e => e.Enabled ? e.Id : "-" + e.Id)
                        .ToList();
                    (tokens[capturedIdx], tokens[targetIdx]) = (tokens[targetIdx], tokens[capturedIdx]);
                    liveRaw = CustomWidgetModuleListHelper.SerializeEntries(
                        CustomWidgetModuleListHelper.ParseEntries(
                            string.Join(",", tokens),
                            def.Options));
                    await options.CommitSettingAsync(settingKey, liveRaw);
                    RenderModuleRows();
                }

                var upBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    IsEnabled = capturedIdx > 0,
                    Content = new FontIcon { Glyph = "\uE70E", FontSize = 10 }
                };
                ToolTipService.SetToolTip(upBtn, loc.T("CustomWidget.Workshop.Stage.SettingsMoveUp"));
                upBtn.Click += (_, _) => FireAndForgetSafeUi(() => MoveModuleAsync(-1));
                Grid.SetColumn(upBtn, 1);
                rowGrid.Children.Add(upBtn);

                var downBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    IsEnabled = capturedIdx < entries.Count - 1,
                    Content = new FontIcon { Glyph = "\uE70D", FontSize = 10 }
                };
                ToolTipService.SetToolTip(downBtn, loc.T("CustomWidget.Workshop.Stage.SettingsMoveDown"));
                downBtn.Click += (_, _) => FireAndForgetSafeUi(() => MoveModuleAsync(1));
                Grid.SetColumn(downBtn, 2);
                rowGrid.Children.Add(downBtn);

                rowBorder.Child = rowGrid;
                modRowsHost.Children.Add(rowBorder);
            }
        }

        RenderModuleRows();
        modSection.Children.Add(modRowsHost);
        return modSection;
    }

    private static ToggleSwitch BuildToggleSwitch(
        CustomWidgetSettingDefinition def,
        string currentVal,
        CustomWidgetSettingsFormOptions options)
    {
        string settingKey = def.Key;
        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = string.Empty,
            OffContent = string.Empty,
            IsOn = CustomWidgetExpressionEvaluator.IsTruthy(currentVal),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, def.Label);

        bool suppress = false;
        toggle.Toggled += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (suppress)
            {
                return;
            }

            suppress = true;
            try
            {
                await options.CommitSettingAsync(settingKey, toggle.IsOn ? "true" : "false");
            }
            finally
            {
                suppress = false;
            }
        });

        options.RegisterExternalSync?.Invoke(latestSettings =>
        {
            string synced = latestSettings.TryGetValue(settingKey, out string? v)
                ? v
                : def.DefaultValue;
            bool wantOn = CustomWidgetExpressionEvaluator.IsTruthy(synced);
            if (toggle.IsOn != wantOn)
            {
                suppress = true;
                toggle.IsOn = wantOn;
                suppress = false;
            }
        });

        return toggle;
    }

    private static FrameworkElement BuildOptionsControl(
        CustomWidgetSettingDefinition def,
        string currentVal,
        CustomWidgetSettingsFormOptions options,
        bool compactFlyoutLayout)
    {
        string settingKey = def.Key;
        string culture = ResolveCulture(options);
        IReadOnlyList<CustomWidgetOptionSpec> effectiveOptions =
            CustomWidgetContextMenuBuilder.ResolveEffectiveSettingOptions(def, culture);

        bool useInlineSegmented = !compactFlyoutLayout &&
            effectiveOptions.Count is >= 2 and <= 3 &&
            effectiveOptions.All(static o => !string.IsNullOrWhiteSpace(o.Label) && o.Label.Length <= 10);

        if (useInlineSegmented)
        {
            var segmented = new Segmented
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(segmented, def.Label);
            WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);
            segmented.Loaded += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);
            segmented.ActualThemeChanged += (_, _) => WidgetSegmentedStyleHelper.ApplyNeutralPointerStates(segmented);

            SegmentedItem? selectedSegment = null;
            foreach (CustomWidgetOptionSpec opt in effectiveOptions)
            {
                var segItem = new SegmentedItem
                {
                    Padding = new Thickness(12, 4, 12, 4),
                    Tag = opt.Value,
                    Content = new TextBlock
                    {
                        Text = opt.Label,
                        FontSize = 12.5
                    }
                };
                segmented.Items.Add(segItem);
                if (string.Equals(opt.Value, currentVal, StringComparison.OrdinalIgnoreCase))
                {
                    selectedSegment = segItem;
                }
            }

            segmented.SelectedItem = selectedSegment ?? segmented.Items.FirstOrDefault();
            bool suppress = false;
            segmented.SelectionChanged += (_, _) => FireAndForgetSafeUi(async () =>
            {
                if (suppress || segmented.SelectedItem is not SegmentedItem { Tag: string val })
                {
                    return;
                }

                suppress = true;
                try
                {
                    await options.CommitSettingAsync(settingKey, val);
                }
                finally
                {
                    suppress = false;
                }
            });

            options.RegisterExternalSync?.Invoke(latestSettings =>
            {
                string synced = latestSettings.TryGetValue(settingKey, out string? v)
                    ? v
                    : def.DefaultValue;
                SegmentedItem? match = segmented.Items
                    .OfType<SegmentedItem>()
                    .FirstOrDefault(i => i.Tag is string t && string.Equals(t, synced, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !ReferenceEquals(segmented.SelectedItem, match))
                {
                    suppress = true;
                    segmented.SelectedItem = match;
                    suppress = false;
                }
            });

            return segmented;
        }

        var combo = new ComboBox
        {
            Header = compactFlyoutLayout ? def.Label : null,
            MinWidth = compactFlyoutLayout ? 0 : 188,
            HorizontalAlignment = compactFlyoutLayout ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(combo, def.Label);

        ComboBoxItem? selectedOption = null;
        foreach (CustomWidgetOptionSpec opt in effectiveOptions)
        {
            var item = new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(opt.Label) ? opt.Value : opt.Label,
                Tag = opt.Value
            };
            combo.Items.Add(item);
            if (string.Equals(opt.Value, currentVal, StringComparison.OrdinalIgnoreCase))
            {
                selectedOption = item;
            }
        }

        combo.SelectedItem = selectedOption ?? combo.Items.FirstOrDefault();
        bool suppressCombo = false;
        combo.SelectionChanged += (_, _) => FireAndForgetSafeUi(async () =>
        {
            if (suppressCombo || combo.SelectedItem is not ComboBoxItem { Tag: string val })
            {
                return;
            }

            suppressCombo = true;
            try
            {
                await options.CommitSettingAsync(settingKey, val);
            }
            finally
            {
                suppressCombo = false;
            }
        });

        options.RegisterExternalSync?.Invoke(latestSettings =>
        {
            string synced = latestSettings.TryGetValue(settingKey, out string? v)
                ? v
                : def.DefaultValue;
            ComboBoxItem? match = combo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is string t && string.Equals(t, synced, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !ReferenceEquals(combo.SelectedItem, match))
            {
                suppressCombo = true;
                combo.SelectedItem = match;
                suppressCombo = false;
            }
        });

        return combo;
    }

    private static NumberBox BuildNumberControl(
        CustomWidgetSettingDefinition def,
        string currentVal,
        double defaultNumericValue,
        CustomWidgetSettingsFormOptions options,
        bool compactFlyoutLayout)
    {
        string settingKey = def.Key;
        double initialNumber = double.TryParse(
            currentVal,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsedCurrent)
            ? parsedCurrent
            : defaultNumericValue;

        bool isIntegerStep = Math.Abs(defaultNumericValue - Math.Truncate(defaultNumericValue)) < 1e-6 &&
                             Math.Abs(initialNumber - Math.Truncate(initialNumber)) < 1e-6 &&
                             !string.Equals(def.Type, "double", StringComparison.OrdinalIgnoreCase);

        bool isWalletBalanceStep = settingKey.Contains("wallet", StringComparison.OrdinalIgnoreCase) ||
                                   settingKey.Contains("balance", StringComparison.OrdinalIgnoreCase);

        var numberBox = new NumberBox
        {
            Header = compactFlyoutLayout ? def.Label : null,
            MinWidth = compactFlyoutLayout ? 0 : 152,
            HorizontalAlignment = compactFlyoutLayout ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(6),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = isWalletBalanceStep ? 10 : (isIntegerStep ? 1 : 0.5),
            LargeChange = isWalletBalanceStep ? 50 : (isIntegerStep ? (compactFlyoutLayout ? 10 : 5) : 2),
            Minimum = isWalletBalanceStep ? 1 : (defaultNumericValue >= 0 ? 0 : -1_000_000),
            Maximum = 1_000_000,
            Value = initialNumber,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(numberBox, def.Label);

        string FormatNumericValue(double val) =>
            isIntegerStep
                ? ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture)
                : val.ToString("0.##", CultureInfo.InvariantCulture);

        string lastCommitted = FormatNumericValue(initialNumber);
        bool suppress = false;
        numberBox.ValueChanged += (_, args) => FireAndForgetSafeUi(async () =>
        {
            if (suppress)
            {
                return;
            }

            if (double.IsNaN(args.NewValue))
            {
                suppress = true;
                numberBox.Value = double.IsNaN(args.OldValue) ? defaultNumericValue : args.OldValue;
                suppress = false;
                return;
            }

            string formatted = FormatNumericValue(args.NewValue);
            if (!string.Equals(formatted, lastCommitted, StringComparison.Ordinal))
            {
                lastCommitted = formatted;
                await options.CommitSettingAsync(settingKey, formatted);
            }
        });

        options.RegisterExternalSync?.Invoke(latestSettings =>
        {
            string synced = latestSettings.TryGetValue(settingKey, out string? v)
                ? v
                : def.DefaultValue;
            if (double.TryParse(synced, NumberStyles.Float, CultureInfo.InvariantCulture, out double syncedNum))
            {
                string formatted = FormatNumericValue(syncedNum);
                if (!string.Equals(formatted, lastCommitted, StringComparison.Ordinal))
                {
                    lastCommitted = formatted;
                    suppress = true;
                    numberBox.Value = syncedNum;
                    suppress = false;
                }
            }
        });

        return numberBox;
    }

    private static FrameworkElement BuildSecretControl(
        CustomWidgetSettingDefinition def,
        CustomWidgetSettingsFormOptions options,
        bool compactFlyoutLayout)
    {
        string settingKey = def.Key;
        string culture = ResolveCulture(options);
        LocalizationService loc = options.Localization;
        string existingSecret = options.ReadSecret?.Invoke(settingKey) ?? string.Empty;

        var pwdBox = new PasswordBox
        {
            Width = compactFlyoutLayout ? double.NaN : 200,
            HorizontalAlignment = compactFlyoutLayout ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(6),
            PlaceholderText = !string.IsNullOrWhiteSpace(existingSecret)
                ? CustomWidgetSecretStore.MaskSecret(existingSecret)
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.SettingsFieldPlaceholder"),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(pwdBox, def.Label);

        var saveBtn = new Button
        {
            Content = loc.T("Common.Save"),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center
        };
        var clearBtn = new Button
        {
            Content = LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.SettingsClear"),
            CornerRadius = new CornerRadius(6),
            Visibility = !string.IsNullOrWhiteSpace(existingSecret) ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };

        async Task CommitSecretAsync(string rawSecret)
        {
            if (options.CommitSecretAsync is not null)
            {
                await options.CommitSecretAsync(settingKey, rawSecret);
            }

            string latest = options.ReadSecret?.Invoke(settingKey) ?? rawSecret;
            pwdBox.Password = string.Empty;
            pwdBox.PlaceholderText = !string.IsNullOrWhiteSpace(latest)
                ? CustomWidgetSecretStore.MaskSecret(latest)
                : LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.SettingsFieldPlaceholder");
            clearBtn.Visibility = !string.IsNullOrWhiteSpace(latest) ? Visibility.Visible : Visibility.Collapsed;
        }

        saveBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
        {
            string typed = pwdBox.Password.Trim();
            if (!string.IsNullOrEmpty(typed) || compactFlyoutLayout)
            {
                await CommitSecretAsync(typed);
            }
        });
        pwdBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && (!string.IsNullOrWhiteSpace(pwdBox.Password) || compactFlyoutLayout))
            {
                e.Handled = true;
                FireAndForgetSafeUi(() => CommitSecretAsync(pwdBox.Password.Trim()));
            }
        };
        clearBtn.Click += (_, _) => FireAndForgetSafeUi(() => CommitSecretAsync(string.Empty));

        if (!compactFlyoutLayout)
        {
            var secretRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            secretRow.Children.Add(pwdBox);
            secretRow.Children.Add(saveBtn);
            secretRow.Children.Add(clearBtn);
            return secretRow;
        }

        var secStack = new StackPanel { Spacing = 4 };
        secStack.Children.Add(new TextBlock
        {
            Text = def.Label,
            FontSize = 12.5
        });

        var pwdRow = new Grid { ColumnSpacing = 6 };
        pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pwdRow.Children.Add(pwdBox);
        Grid.SetColumn(saveBtn, 1);
        pwdRow.Children.Add(saveBtn);
        Grid.SetColumn(clearBtn, 2);
        pwdRow.Children.Add(clearBtn);
        secStack.Children.Add(pwdRow);

        secStack.Children.Add(new TextBlock
        {
            Text = LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.SettingsDpapiNote"),
            FontSize = 11,
            Foreground = ResolveScopedBrush(
                "TextFillColorSecondaryBrush",
                options.ThemeScope,
                Color.FromArgb(0xB0, 0x80, 0x80, 0x80)),
            TextWrapping = TextWrapping.Wrap
        });
        return secStack;
    }

    private static FrameworkElement BuildTextOrPathControl(
        CustomWidgetSettingDefinition def,
        string currentVal,
        CustomWidgetSettingsFormOptions options,
        bool compactFlyoutLayout)
    {
        string settingKey = def.Key;
        LocalizationService loc = options.Localization;
        IntPtr hwnd = options.OwnerWindowHandle;

        bool isMultiline = CustomWidgetOnboardingHelper.IsMultilineTextSetting(def, currentVal);
        bool isMultilineMediaOrPathList = isMultiline &&
            CustomWidgetOnboardingHelper.IsMultilineMediaOrPathListSetting(def, currentVal);
        (bool isPathLike, bool isFilePathLike) = CustomWidgetOnboardingHelper.ClassifyPathSetting(def, currentVal);

        var inputBox = new TextBox
        {
            Header = compactFlyoutLayout && !isPathLike && !isMultilineMediaOrPathList ? def.Label : null,
            Width = compactFlyoutLayout ? double.NaN : (isMultiline ? 300 : (isPathLike ? 240 : 220)),
            HorizontalAlignment = compactFlyoutLayout ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(6),
            Text = currentVal,
            PlaceholderText = def.Placeholder ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            AcceptsReturn = isMultiline,
            TextWrapping = isMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = isMultiline ? (compactFlyoutLayout ? 84 : 80) : 0,
            MaxHeight = isMultiline ? 156 : double.PositiveInfinity
        };
        if (isMultiline)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(inputBox, ScrollBarVisibility.Auto);
        }
        if (isPathLike)
        {
            inputBox.FlowDirection = FlowDirection.LeftToRight;
        }
        AutomationProperties.SetName(inputBox, def.Label);

        string lastCommitted = currentVal;
        async Task CommitInputAsync()
        {
            string val = inputBox.Text.Trim();
            if (!string.Equals(val, lastCommitted, StringComparison.Ordinal) || compactFlyoutLayout)
            {
                lastCommitted = val;
                await options.CommitSettingAsync(settingKey, val);
            }
        }

        inputBox.LostFocus += (_, _) => FireAndForgetSafeUi(CommitInputAsync);
        inputBox.KeyDown += (_, e) =>
        {
            if (ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                if (isMultiline)
                {
                    CoreVirtualKeyStates ctrlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
                    if (!ctrlState.HasFlag(CoreVirtualKeyStates.Down))
                    {
                        return;
                    }
                }

                e.Handled = true;
                FireAndForgetSafeUi(CommitInputAsync);
            }
        };

        options.RegisterExternalSync?.Invoke(latestSettings =>
        {
            string synced = latestSettings.TryGetValue(settingKey, out string? v)
                ? v
                : def.DefaultValue;
            if (!string.Equals(inputBox.Text, synced, StringComparison.Ordinal))
            {
                lastCommitted = synced;
                inputBox.Text = synced;
            }
        });

        if (isMultilineMediaOrPathList && hwnd != IntPtr.Zero)
        {
            var addFilesBtn = new Button
            {
                Height = compactFlyoutLayout ? 26 : 28,
                Padding = compactFlyoutLayout ? new Thickness(8, 0, 8, 0) : new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(compactFlyoutLayout ? 5 : 6),
                FontSize = compactFlyoutLayout ? 11.5 : 12,
                Content = "+ " + loc.T("CustomWidget.Onboarding.BrowseFileButton")
            };
            var addFolderBtn = new Button
            {
                Height = compactFlyoutLayout ? 26 : 28,
                Padding = compactFlyoutLayout ? new Thickness(8, 0, 8, 0) : new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(compactFlyoutLayout ? 5 : 6),
                FontSize = compactFlyoutLayout ? 11.5 : 12,
                Content = "+ " + loc.T("CustomWidget.Onboarding.BrowseFolderButton")
            };

            addFilesBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                addFilesBtn.IsEnabled = false;
                options.SetModalPickerActive?.Invoke(true);
                try
                {
                    IReadOnlyList<string> picked = await CustomWidgetOnboardingHelper.PickMultipleFilesForSettingAsync(hwnd, def);
                    if (picked.Count > 0)
                    {
                        inputBox.Text = CustomWidgetOnboardingHelper.AppendLinesToMultilineSetting(inputBox.Text, picked);
                        await CommitInputAsync();
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidgetSettingsForm] Multi-file picker failed: {ex.Message}");
                }
                finally
                {
                    options.SetModalPickerActive?.Invoke(false);
                    addFilesBtn.IsEnabled = true;
                }
            });

            addFolderBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                addFolderBtn.IsEnabled = false;
                options.SetModalPickerActive?.Invoke(true);
                try
                {
                    string? pickedFolder = await FolderPickerService.PickFolderAsync(hwnd, "widget-setting");
                    if (!string.IsNullOrWhiteSpace(pickedFolder))
                    {
                        inputBox.Text = CustomWidgetOnboardingHelper.AppendLinesToMultilineSetting(inputBox.Text, [pickedFolder]);
                        await CommitInputAsync();
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidgetSettingsForm] Folder append picker failed: {ex.Message}");
                }
                finally
                {
                    options.SetModalPickerActive?.Invoke(false);
                    addFolderBtn.IsEnabled = true;
                }
            });

            if (compactFlyoutLayout)
            {
                var listGroup = new StackPanel { Spacing = 6 };
                var headerRow = new Grid { ColumnSpacing = 6 };
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                headerRow.Children.Add(new TextBlock
                {
                    Text = def.Label,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(addFilesBtn, 1);
                headerRow.Children.Add(addFilesBtn);
                Grid.SetColumn(addFolderBtn, 2);
                headerRow.Children.Add(addFolderBtn);

                listGroup.Children.Add(headerRow);
                listGroup.Children.Add(inputBox);
                return listGroup;
            }

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btnRow.Children.Add(addFilesBtn);
            btnRow.Children.Add(addFolderBtn);

            var multiStack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            multiStack.Children.Add(inputBox);
            multiStack.Children.Add(btnRow);
            return multiStack;
        }

        if (isPathLike && hwnd != IntPtr.Zero)
        {
            var browseBtn = new Button
            {
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center,
                Content = loc.T(isFilePathLike
                    ? "CustomWidget.Onboarding.BrowseFileButton"
                    : "CustomWidget.Onboarding.BrowseFolderButton")
            };
            browseBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
            {
                browseBtn.IsEnabled = false;
                options.SetModalPickerActive?.Invoke(true);
                try
                {
                    string? pickedPath = await CustomWidgetOnboardingHelper.PickPathForSettingAsync(
                        hwnd,
                        def,
                        isFilePathLike,
                        inputBox.Text);
                    if (!string.IsNullOrWhiteSpace(pickedPath))
                    {
                        inputBox.Text = pickedPath;
                        await CommitInputAsync();
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidgetSettingsForm] Path picker failed: {ex.Message}");
                }
                finally
                {
                    options.SetModalPickerActive?.Invoke(false);
                    browseBtn.IsEnabled = true;
                }
            });

            if (compactFlyoutLayout)
            {
                var pathGroup = new StackPanel { Spacing = 4 };
                pathGroup.Children.Add(new TextBlock
                {
                    Text = def.Label,
                    FontSize = 12.5
                });

                var compactPathRow = new Grid { ColumnSpacing = 6 };
                compactPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                compactPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                compactPathRow.Children.Add(inputBox);
                Grid.SetColumn(browseBtn, 1);
                compactPathRow.Children.Add(browseBtn);
                pathGroup.Children.Add(compactPathRow);
                return pathGroup;
            }

            var pathRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            pathRow.Children.Add(inputBox);
            pathRow.Children.Add(browseBtn);
            return pathRow;
        }

        return inputBox;
    }

    private static string ResolveCulture(CustomWidgetSettingsFormOptions options) =>
        !string.IsNullOrWhiteSpace(options.CultureName)
            ? options.CultureName
            : options.Localization.CurrentCultureName;

    private static Brush ResolveScopedBrush(string key, DependencyObject? scope, Color fallback)
    {
        try
        {
            if (NeutralInteractionBrush.ResolveThemedResource(key, scope) is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
            // Headless unit-test fallback when Application.Current.Resources is uninitialized.
        }

        return new SolidColorBrush(fallback);
    }
}
