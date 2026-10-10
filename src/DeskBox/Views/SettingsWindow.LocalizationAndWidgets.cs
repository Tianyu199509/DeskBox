using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using CommunityToolkit.WinUI.Controls;
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
        if (e.PropertyName == nameof(SettingsViewModel.FeatureWidgetEntries))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(RefreshFeatureWidgetList);
                return;
            }

            RefreshFeatureWidgetList();
            return;
        }

        if (e.PropertyName == nameof(SettingsViewModel.ManagedStorageRootPath))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(RefreshManagedStoragePathWarning);
                return;
            }

            RefreshManagedStoragePathWarning();
        }

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
        if (_settingsSectionElements.TryGetValue("SearchSettings", out FrameworkElement? search) &&
            search is DeskBox.Views.SettingsSections.SearchSettingsSection searchSection)
        {
            searchSection.RefreshFromSettings();
        }
        RefreshManagedStoragePathWarning();
        RefreshManagedStorageDesktopShortcutState();
        if (TryGetSectionRoute(_currentSettingsSection, out SettingsSectionRoute? route))
        {
            UpdateBreadcrumb(route);
        }
        if (string.Equals(_currentSettingsSection, "ManagedStorage", StringComparison.Ordinal))
        {
            RefreshManagedStorageFolderList();
        }
        if (string.Equals(_currentSettingsSection, "BackupRestoreSettings", StringComparison.Ordinal))
        {
            _ = RefreshBackupSnapshotInventoryAsync();
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
                    FeatureWidgetList.Children.Add(row.Card);
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
        var card = new SettingsCard
        {
            Tag = entry.SettingsSectionTag,
            IsClickEnabled = entry.HasSettingsPage && !string.IsNullOrWhiteSpace(entry.SettingsSectionTag),
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        if (card.IsClickEnabled)
        {
            card.Click += FeatureWidgetSettingsButton_Click;
        }

        // WidgetTitleIcon (colorful kind icon) is a UserControl and cannot
        // fill the native HeaderIcon slot, so the header carries it inline
        // next to the title instead — this row is built in code, so the
        // header never goes through Localized.HeaderKey.
        var titleIcon = new WidgetTitleIcon
        {
            IconKind = WidgetTitleIconKindNames.FromWidgetKind(entry.Kind),
            Mode = WidgetTitleIconModeNames.Color,
            IconSize = 16,
            Glyph = entry.Glyph,
            LabelText = entry.Title,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = entry.Title,
            Style = (Style)SettingsRoot.Resources["SettingTitleTextStyle"]
        };
        // The description composes inside the header so it left-aligns with
        // the title; the card's native Description slot starts under the icon.
        var descriptionText = new TextBlock
        {
            Text = entry.DisplayDescription,
            Style = (Style)SettingsRoot.Resources["SettingDescriptionTextStyle"]
        };
        var textPanel = new StackPanel();
        textPanel.Children.Add(titleText);
        textPanel.Children.Add(descriptionText);

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        headerPanel.Children.Add(titleIcon);
        headerPanel.Children.Add(textPanel);
        card.Header = headerPanel;
        // The composed header is not a string, so the card's automation peer
        // cannot derive a name from it; expose the title explicitly.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, entry.Title);

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        Button? resetButton = null;
        if (FeatureWidgetSettings.IsFeatureWidget(entry.Kind))
        {
            resetButton = new Button
            {
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)SettingsRoot.Resources["IconActionButtonStyle"],
                Tag = entry.Kind,
                Content = new FontIcon
                {
                    Glyph = "\uE72C",
                    FontSize = 16,
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"]
                }
            };
            ToolTipService.SetToolTip(resetButton, _localizationService.T("Settings.FeatureWidgets.ResetTooltip"));
            resetButton.Click += FeatureWidgetResetButton_Click;
            contentPanel.Children.Add(resetButton);
        }

        ToggleSwitch? toggle = null;
        if (entry.ShowToggle)
        {
            toggle = new ToggleSwitch
            {
                MinWidth = 0,
                IsOn = entry.IsEnabled,
                IsEnabled = entry.CanToggle,
                Tag = entry.Kind
            };
            ClearToggleSwitchContent(toggle);
            // Same accessible-name contract as the XAML toggles: the card
            // name does not propagate to the content switch.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, entry.Title);
            toggle.Toggled += FeatureWidgetToggle_Toggled;
            contentPanel.Children.Add(toggle);
        }

        if (contentPanel.Children.Count > 0)
        {
            card.Content = contentPanel;
        }

        return new FeatureWidgetRowElements(
            card,
            titleIcon,
            titleText,
            descriptionText,
            resetButton,
            toggle,
            entry.HasSettingsPage,
            FeatureWidgetSettings.IsFeatureWidget(entry.Kind),
            entry.ShowToggle);
    }

    private void UpdateFeatureWidgetRow(FeatureWidgetRowElements row, FeatureWidgetEntry entry)
    {
        row.TitleText.Text = entry.Title;
        row.DescriptionText.Text = entry.DisplayDescription;
        row.Card.Tag = entry.SettingsSectionTag;
        row.TitleIcon.Glyph = entry.Glyph;
        row.TitleIcon.IconKind = WidgetTitleIconKindNames.FromWidgetKind(entry.Kind);
        row.TitleIcon.LabelText = entry.Title;

        if (row.ResetButton is not null)
        {
            row.ResetButton.Tag = entry.Kind;
            ToolTipService.SetToolTip(row.ResetButton, _localizationService.T("Settings.FeatureWidgets.ResetTooltip"));
        }

        if (row.Toggle is not null)
        {
            row.Toggle.Tag = entry.Kind;
            row.Toggle.IsOn = entry.IsEnabled;
            row.Toggle.IsEnabled = entry.CanToggle;
            ClearToggleSwitchContent(row.Toggle);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Toggle, entry.Title);
        }
    }

    private void ClearFeatureWidgetRows()
    {
        foreach (var row in _featureWidgetRows.Values)
        {
            row.Card.Click -= FeatureWidgetSettingsButton_Click;

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
        FeatureWidgetList?.Children.Clear();
    }

    private void FeatureWidgetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is SettingsCard { Tag: string sectionTag })
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
