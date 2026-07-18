using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.FeatureWidgetEntries))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
            return;
        }

        RefreshFeatureWidgetList();
    }

    private void OnLanguageChanged()
    {
        RefreshLocalizedContent();
    }

    public void RefreshLocalizedContent()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshLocalizedContent);
            return;
        }

        if (_isClosed)
        {
            return;
        }

        ApplyLocalizedText();

        // After ItemsSource is replaced with new localized strings, the
        // ComboBox internally resets SelectedIndex to -1. The OneWay
        // SelectedIndex binding may not push the value back because the
        // binding engine still caches the old value. Defer to the next
        // frame so all binding updates have been processed, then force
        // each ComboBox to re-select from the ViewModel.
        DispatcherQueue.TryEnqueue(RefreshComboBoxSelections);
    }

    private void ApplyLocalizedText()
    {
        Title = _localizationService.T("Settings.WindowTitle");
        Localized.RefreshAll(_localizationService);
        RefreshSettingsSearchResults();
        ApplyToggleSwitchContentVisibility();
        RefreshFeatureWidgetList();
        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
        if (TryGetSectionRoute(_currentSettingsSection, out SettingsSectionRoute? route))
        {
            UpdateBreadcrumb(route);
        }
        if (string.Equals(_currentSettingsSection, "ManagedStorage", StringComparison.Ordinal))
        {
            RefreshManagedStorageFolderList();
        }
    }

    /// <summary>
    /// Forces every ComboBox in the settings tree to re-apply its
    /// SelectedIndex from the ViewModel.  After a language change the
    /// ItemsSource arrays are replaced; the ComboBox internally resets
    /// SelectedIndex to -1, and the OneWay binding may not push the
    /// value back because the engine still caches the old value.
    /// </summary>
    private void RefreshComboBoxSelections()
    {
        if (_isClosed)
        {
            return;
        }

        foreach (var combo in FindDescendants<ComboBox>(SettingsRoot))
        {
            if (combo.Tag is not string tag)
            {
                continue;
            }

            int index = tag switch
            {
                "Theme" => ViewModel.SelectedThemeIndex,
                "TrayIconStyle" => ViewModel.SelectedTrayIconStyleIndex,
                "Language" => ViewModel.SelectedLanguageIndex,
                "MusicDisplayMode" => ViewModel.SelectedMusicDisplayModeIndex,
                "FileStackGroupBy" => ViewModel.SelectedFileStackGroupByIndex,
                "FileStackThreshold" => ViewModel.SelectedFileStackThresholdIndex,
                "FileStackOrderBy" => ViewModel.SelectedFileStackOrderByIndex,
                "FileStackUnmatchedBehavior" => ViewModel.SelectedFileStackUnmatchedBehaviorIndex,
                "WidgetCorner" => ViewModel.SelectedWidgetCornerPreferenceIndex,
                "WidgetMaterial" => ViewModel.SelectedWidgetMaterialTypeIndex,
                "WidgetBorderColor" => ViewModel.SelectedWidgetBorderColorModeIndex,
                "WidgetBorder" => ViewModel.SelectedWidgetBorderStyleIndex,
                "WidgetCollapseBehavior" => ViewModel.SelectedWidgetCollapseBehaviorIndex,
                "WidgetCompactContentMode" => ViewModel.SelectedWidgetCompactContentModeIndex,
                "WidgetCompactAnimationEffect" => ViewModel.SelectedWidgetCompactAnimationEffectIndex,
                "WidgetCompactMediaCornerMode" => ViewModel.SelectedWidgetCompactMediaCornerIndex,
                "LayoutDensity" => ViewModel.SelectedLayoutDensityIndex,
                "AnimationPreset" => ViewModel.SelectedAnimationPresetIndex,
                "WidgetTitleIconMode" => ViewModel.SelectedWidgetTitleIconModeIndex,
                "WidgetAnimationEffect" => ViewModel.SelectedWidgetAnimationEffectIndex,
                "WidgetAnimationSpeed" => ViewModel.SelectedWidgetAnimationSpeedIndex,
                "WidgetAnimationSlideDirection" => ViewModel.SelectedWidgetAnimationSlideDirectionIndex,
                "WidgetAnimationEasingIntensity" => ViewModel.SelectedWidgetAnimationEasingIntensityIndex,
                "DisplayWidgetChromeMode" => ViewModel.SelectedDisplayWidgetChromeModeIndex,
                "InteractiveWidgetChromeMode" => ViewModel.SelectedInteractiveWidgetChromeModeIndex,
                "WidgetLayerMode" => ViewModel.SelectedWidgetLayerModeIndex,
                "QuickCaptureDefaultView" => ViewModel.SelectedQuickCaptureDefaultViewIndex,
                "QuickCaptureTabStyle" => ViewModel.SelectedQuickCaptureTabStyleIndex,
                "QuickCapturePreviewLines" => ViewModel.SelectedQuickCaptureItemPreviewLineCountIndex,
                "QuickCaptureEnterBehavior" => ViewModel.SelectedQuickCaptureEditorEnterBehaviorIndex,
                "TodoNewTaskPosition" => ViewModel.SelectedTodoNewTaskPositionIndex,
                "AttachmentStorageMode" => ViewModel.SelectedAttachmentStorageModeIndex,
                "TodoDefaultFilter" => ViewModel.SelectedTodoDefaultFilterIndex,
                "TodoTabStyle" => ViewModel.SelectedTodoTabStyleIndex,
                "TodoPreviewLines" => ViewModel.SelectedTodoItemPreviewLineCountIndex,
                "TodoEnterBehavior" => ViewModel.SelectedTodoEditorEnterBehaviorIndex,
                "TodoReminderOffset" => ViewModel.SelectedTodoReminderOffsetMinutesIndex,
                "WeatherTemperatureUnit" => ViewModel.SelectedWeatherTemperatureUnitIndex,
                "WeatherWindSpeedUnit" => ViewModel.SelectedWeatherWindSpeedUnitIndex,
                "WeatherDefaultView" => ViewModel.SelectedWeatherDefaultViewIndex,
                "WeatherSkin" => ViewModel.SelectedWeatherSkinIndex,
                "WeatherRefreshInterval" => ViewModel.SelectedWeatherRefreshIntervalIndex,
                _ => -1
            };

            if (index >= 0 && combo.SelectedIndex != index)
            {
                combo.SelectedIndex = index;
            }
        }
    }

    private void ApplyToggleSwitchContentVisibility()
    {
        foreach (var toggle in FindDescendants<ToggleSwitch>(SettingsRoot))
        {
            ClearToggleSwitchContent(toggle);
        }
    }

    private static void ClearToggleSwitchContent(ToggleSwitch toggle)
    {
        toggle.OnContent = string.Empty;
        toggle.OffContent = string.Empty;
    }

    private void RefreshFeatureWidgetList()
    {
        if (FeatureWidgetList is null)
        {
            return;
        }

        _isRefreshingFeatureWidgetList = true;
        try
        {
            var entries = ViewModel.FeatureWidgetEntries.ToArray();
            bool requiresRebuild = entries.Length != _featureWidgetRows.Count ||
                entries.Any(entry =>
                    !_featureWidgetRows.TryGetValue(entry.Kind, out var row) ||
                    row.HasSettingsPage != entry.HasSettingsPage ||
                    row.HasReset != FeatureWidgetSettings.IsFeatureWidget(entry.Kind) ||
                    row.HasToggle != entry.ShowToggle);

            if (requiresRebuild)
            {
                ClearFeatureWidgetRows();
                foreach (var entry in entries)
                {
                    var row = CreateFeatureWidgetRow(entry);
                    _featureWidgetRows[entry.Kind] = row;
                    FeatureWidgetList.Children.Add(row.Container);
                }
            }
            else
            {
                foreach (var entry in entries)
                {
                    UpdateFeatureWidgetRow(_featureWidgetRows[entry.Kind], entry);
                }
            }
        }
        finally
        {
            _isRefreshingFeatureWidgetList = false;
        }

        ApplyToggleSwitchContentVisibility();
    }

    private FeatureWidgetRowElements CreateFeatureWidgetRow(FeatureWidgetEntry entry)
    {
        var border = new Border
        {
            Style = (Style)SettingsRoot.Resources["SettingsGroupStyle"]
        };

        var root = new Grid
        {
            MinHeight = 70
        };

        Button? settingsButton = null;
        if (entry.HasSettingsPage && !string.IsNullOrWhiteSpace(entry.SettingsSectionTag))
        {
            settingsButton = new Button
            {
                Padding = new Thickness(0),
                Style = (Style)SettingsRoot.Resources["DrillDownRowStyle"],
                Tag = entry.SettingsSectionTag
            };
            settingsButton.Click += FeatureWidgetSettingsButton_Click;
            root.Children.Add(settingsButton);
        }

        var content = new Grid
        {
            Padding = new Thickness(12, 10, 10, 10),
            ColumnSpacing = 10
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = entry.Glyph,
            FontSize = 18,
            FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            Foreground = CreateFeatureWidgetIconBrush(),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        content.Children.Add(icon);

        var textPanel = new StackPanel
        {
            IsHitTestVisible = false,
            Style = (Style)SettingsRoot.Resources["SettingTextPanelStyle"]
        };
        var title = new TextBlock
        {
            Text = entry.Title,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        };
        textPanel.Children.Add(title);
        var description = new TextBlock
        {
            Text = entry.DisplayDescription,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        };
        textPanel.Children.Add(description);
        Grid.SetColumn(textPanel, 1);
        content.Children.Add(textPanel);

        Button? resetButton = null;
        if (FeatureWidgetSettings.IsFeatureWidget(entry.Kind))
        {
            resetButton = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)SettingsRoot.Resources["IconActionButtonStyle"],
                Tag = entry.Kind,
                Content = new FontIcon
                {
                    Glyph = "\uE72C",
                    FontSize = 13,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    Foreground = CreateFeatureWidgetIconBrush()
                }
            };
            ToolTipService.SetToolTip(resetButton, _localizationService.T("Settings.FeatureWidgets.ResetTooltip"));
            resetButton.Click += FeatureWidgetResetButton_Click;
            Canvas.SetZIndex(resetButton, 2);
            Grid.SetColumn(resetButton, 2);
            content.Children.Add(resetButton);
        }

        ToggleSwitch? toggle = null;
        if (entry.ShowToggle)
        {
            toggle = new ToggleSwitch
            {
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsOn = entry.IsEnabled,
                IsEnabled = entry.CanToggle,
                Tag = entry.Kind
            };
            ClearToggleSwitchContent(toggle);
            toggle.Toggled += FeatureWidgetToggle_Toggled;
            Canvas.SetZIndex(toggle, 2);
            Grid.SetColumn(toggle, 3);
            content.Children.Add(toggle);
        }

        FontIcon? arrow = null;
        if (entry.HasSettingsPage && !string.IsNullOrWhiteSpace(entry.SettingsSectionTag))
        {
            arrow = new FontIcon
            {
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                Glyph = "\uE974",
                Foreground = CreateFeatureWidgetIconBrush(),
                Margin = new Thickness(0, 0, -4, 0)
            };
            Grid.SetColumn(arrow, 4);
            content.Children.Add(arrow);
        }

        root.Children.Add(content);
        border.Child = root;
        return new FeatureWidgetRowElements(
            border,
            icon,
            title,
            description,
            settingsButton,
            resetButton,
            toggle,
            arrow,
            entry.HasSettingsPage,
            FeatureWidgetSettings.IsFeatureWidget(entry.Kind),
            entry.ShowToggle);
    }

    private void UpdateFeatureWidgetRow(FeatureWidgetRowElements row, FeatureWidgetEntry entry)
    {
        Brush iconBrush = CreateFeatureWidgetIconBrush();
        row.Icon.Glyph = entry.Glyph;
        row.Icon.Foreground = iconBrush;
        row.Title.Text = entry.Title;
        row.Description.Text = entry.DisplayDescription;

        if (row.SettingsButton is not null)
        {
            row.SettingsButton.Tag = entry.SettingsSectionTag;
        }

        if (row.ResetButton is not null)
        {
            row.ResetButton.Tag = entry.Kind;
            ToolTipService.SetToolTip(row.ResetButton, _localizationService.T("Settings.FeatureWidgets.ResetTooltip"));
            if (row.ResetButton.Content is FontIcon resetIcon)
            {
                resetIcon.Foreground = iconBrush;
            }
        }

        if (row.Toggle is not null)
        {
            row.Toggle.Tag = entry.Kind;
            row.Toggle.IsOn = entry.IsEnabled;
            row.Toggle.IsEnabled = entry.CanToggle;
            ClearToggleSwitchContent(row.Toggle);
        }

        if (row.Arrow is not null)
        {
            row.Arrow.Foreground = iconBrush;
        }
    }

    private void ClearFeatureWidgetRows()
    {
        foreach (var row in _featureWidgetRows.Values)
        {
            if (row.SettingsButton is not null)
            {
                row.SettingsButton.Click -= FeatureWidgetSettingsButton_Click;
            }

            if (row.ResetButton is not null)
            {
                row.ResetButton.Click -= FeatureWidgetResetButton_Click;
            }

            if (row.Toggle is not null)
            {
                row.Toggle.Toggled -= FeatureWidgetToggle_Toggled;
            }
        }

        _featureWidgetRows.Clear();
        FeatureWidgetList.Children.Clear();
    }

    private void FeatureWidgetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigateToSettingsSection(sectionTag);
        }
    }

    private async void FeatureWidgetResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WidgetKind kind } button)
        {
            if (!await ConfirmFeatureWidgetResetAsync(kind))
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                await ViewModel.ResetFeatureWidgetAsync(kind);
                RefreshFeatureWidgetList();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task<bool> ConfirmFeatureWidgetResetAsync(WidgetKind kind)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return false;
        }

        string titleKey = kind == WidgetKind.QuickCapture
            ? "QuickCapture.Name"
            : $"{kind}.Title";
        string widgetName = _localizationService.T(titleKey);
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.Format("Settings.FeatureWidgets.ResetDialogTitle", widgetName),
            PrimaryButtonText = _localizationService.T("Settings.FeatureWidgets.ResetConfirm"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.FeatureWidgets.ResetDialogBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void FeatureWidgetToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingFeatureWidgetList)
        {
            return;
        }

        if (sender is ToggleSwitch { Tag: WidgetKind kind } toggle)
        {
            ViewModel.SetWidgetEnabled(kind, toggle.IsOn);
            DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
        }
    }
}
