using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the first-party Clipboard feature widget: capture types,
/// click/paste behavior, privacy &amp; secret protection, excluded apps, and cache upkeep.
/// </summary>
public sealed partial class ClipboardSettingsSection : UserControl
{
    private SettingsService? _settingsService;
    private LocalizationService? _localization;
    private ISettingsWidgetActions? _widgetActions;
    private ISettingsDataActions? _dataActions;
    private nint _ownerWindow;
    private bool _isLoading;
    private int _statsRefreshGeneration;
    private IReadOnlyList<ClipboardObservedApp> _observedApps = Array.Empty<ClipboardObservedApp>();

    public ClipboardSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                RebuildExcludedAppLists();
            }
        };
    }

    public void Configure(
        SettingsService settingsService,
        LocalizationService localizationService,
        ISettingsWidgetActions widgetActions,
        ISettingsDataActions dataActions,
        nint ownerWindow)
    {
        _settingsService = settingsService;
        _localization = localizationService;
        _widgetActions = widgetActions;
        _dataActions = dataActions;
        _ownerWindow = ownerWindow;
        RefreshFromSettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSettings();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _statsRefreshGeneration++;
    }

    public void RefreshFromSettings()
    {
        if (_settingsService is null || _localization is null || _widgetActions is null)
        {
            return;
        }

        _isLoading = true;
        try
        {
            ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
            EnableClipboardToggle.IsOn = _widgetActions.IsFeatureWidgetEnabled(WidgetKind.Clipboard);
            CaptureTextToggle.IsOn = slice.ClipboardCaptureText;
            CaptureImagesToggle.IsOn = slice.ClipboardCaptureImages;
            CaptureFilesToggle.IsOn = slice.ClipboardCaptureFiles;
            SelectByTag(MaxImageSizeComboBox, slice.ClipboardMaxImageSizeMb.ToString());
            SelectByTag(ClickBehaviorComboBox, slice.ClipboardClickBehavior);
            DefaultPlainTextToggle.IsOn = slice.ClipboardDefaultPastePlainText;
            MoveToTopToggle.IsOn = slice.ClipboardMoveToTopOnReuse;
            SelectByTag(CardDensityComboBox, slice.ClipboardCardDensity);
            ShowSourceAppToggle.IsOn = slice.ClipboardShowSourceApp;
            IgnorePrivacyFormatsToggle.IsOn = slice.ClipboardIgnoreSystemPrivacyFormats;
            SelectByTag(SecretProtectionComboBox, slice.ClipboardSensitiveProtectionMode);
            SelectByTag(MaxItemsComboBox, slice.ClipboardMaxItems.ToString());
            SelectByTag(RetentionDaysComboBox, slice.ClipboardRetentionDays.ToString());
            RebuildExcludedAppLists();
        }
        finally
        {
            _isLoading = false;
        }

        _ = RefreshRuntimeStatsAndObservedAppsAsync();
    }

    private async Task RefreshRuntimeStatsAndObservedAppsAsync()
    {
        if (_dataActions is null || _localization is null)
        {
            return;
        }

        int generation = ++_statsRefreshGeneration;
        try
        {
            ClipboardStorageStats? stats = await _dataActions.GetClipboardStorageStatsAsync();
            if (generation != _statsRefreshGeneration)
            {
                return;
            }

            if (stats is null)
            {
                StorageSummaryCard.Description = _localization.T("Settings.Clipboard.StorageSummary.Empty");
                ClearUnpinnedButton.IsEnabled = false;
                _observedApps = Array.Empty<ClipboardObservedApp>();
                RebuildExcludedAppLists();
                return;
            }

            int unpinnedCount = Math.Max(0, stats.ActiveItemCount - stats.PinnedItemCount);
            ClearUnpinnedButton.IsEnabled = unpinnedCount > 0;
            StorageSummaryCard.Description = stats.ActiveItemCount == 0 && stats.TotalCacheBytes == 0
                ? _localization.T("Settings.Clipboard.StorageSummary.Empty")
                : _localization.Format(
                    "Settings.Clipboard.StorageSummary.Format",
                    stats.ActiveItemCount,
                    stats.PinnedItemCount,
                    FormatBytes(stats.TotalCacheBytes));

            _observedApps = stats.ObservedApps ?? [];
            RebuildExcludedAppLists();
        }
        catch
        {
            if (generation == _statsRefreshGeneration)
            {
                StorageSummaryCard.Description = _localization.T("Settings.Clipboard.StorageSummary.Empty");
            }
        }
    }

    private void RebuildExcludedAppLists()
    {
        if (_settingsService is null || _localization is null)
        {
            return;
        }

        List<string> excluded = _settingsService.Settings.Clipboard.ClipboardExcludedApps
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();

        ExcludedAppsListPanel.Children.Clear();
        ExcludedAppsEmptyText.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludedAppsListPanel.Visibility = excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Microsoft.UI.Xaml.Media.Brush secondaryBrush =
            NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this) ??
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

        foreach (string entry in excluded)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string fileName = Path.GetFileName(entry);
            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(fileName) ? entry : fileName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(fileName) &&
                !string.Equals(fileName, entry, StringComparison.OrdinalIgnoreCase))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = entry,
                    FontSize = 11,
                    Foreground = secondaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var removeButton = new Button
            {
                Tag = entry,
                Style = (Style)Resources["IconActionButtonStyle"],
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }
            };
            ToolTipService.SetToolTip(removeButton, _localization.T("Common.Delete"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                removeButton,
                _localization.T("Common.Delete"));
            removeButton.Click += RemoveExcludedAppButton_Click;

            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(textStack);
            row.Children.Add(removeButton);
            ExcludedAppsListPanel.Children.Add(row);
        }

        ObservedAppsListPanel.Children.Clear();
        ObservedAppsEmptyText.Visibility = _observedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ObservedAppsListPanel.Visibility = _observedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (ClipboardObservedApp observed in _observedApps)
        {
            string exeName = Path.GetFileName(observed.AppPath);
            bool isChecked = excluded.Any(item =>
                string.Equals(item, observed.AppPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(exeName) &&
                 string.Equals(Path.GetFileName(item), exeName, StringComparison.OrdinalIgnoreCase)));

            string displayLabel = string.IsNullOrWhiteSpace(observed.AppName)
                ? (string.IsNullOrWhiteSpace(exeName) ? observed.AppPath : exeName)
                : (string.IsNullOrWhiteSpace(exeName) ||
                   string.Equals(observed.AppName, exeName, StringComparison.OrdinalIgnoreCase)
                    ? observed.AppName
                    : $"{observed.AppName} ({exeName})");

            var checkBox = new CheckBox
            {
                Content = displayLabel,
                Tag = observed.AppPath,
                IsChecked = isChecked,
                MinWidth = 0
            };
            ToolTipService.SetToolTip(checkBox, observed.AppPath);
            checkBox.Click += ObservedAppCheckBox_Click;
            ObservedAppsListPanel.Children.Add(checkBox);
        }
    }

    private async void EnableClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _widgetActions is null || _settingsService is null)
        {
            return;
        }

        bool desired = EnableClipboardToggle.IsOn;
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Clipboard, desired);
        try
        {
            bool applied = await _widgetActions.SetFeatureWidgetEnabled(WidgetKind.Clipboard, desired);
            if (applied != desired)
            {
                _isLoading = true;
                try
                {
                    FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Clipboard, applied);
                    EnableClipboardToggle.IsOn = applied;
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Toggle clipboard widget failed: {ex.Message}");
        }
    }

    private async void CaptureTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureText = CaptureTextToggle.IsOn);
    }

    private async void CaptureImagesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureImages = CaptureImagesToggle.IsOn);
    }

    private async void CaptureFilesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureFiles = CaptureFilesToggle.IsOn);
    }

    private async void MaxImageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || MaxImageSizeComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int mb))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardMaxImageSizeMb = mb);
    }

    private async void ClickBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || ClickBehaviorComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardClickBehavior = mode);
    }

    private async void DefaultPlainTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardDefaultPastePlainText = DefaultPlainTextToggle.IsOn);
    }

    private async void MoveToTopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardMoveToTopOnReuse = MoveToTopToggle.IsOn);
    }

    private async void CardDensityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || CardDensityComboBox.SelectedItem is not ComboBoxItem { Tag: string density })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardCardDensity = density);
    }

    private async void ShowSourceAppToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardShowSourceApp = ShowSourceAppToggle.IsOn);
    }

    private async void IgnorePrivacyFormatsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        await SaveSliceMutationAsync(slice => slice.ClipboardIgnoreSystemPrivacyFormats = IgnorePrivacyFormatsToggle.IsOn);
    }

    private async void SecretProtectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SecretProtectionComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardSensitiveProtectionMode = mode);
    }

    private async void BrowseExcludedAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService is null || _ownerWindow == 0)
        {
            return;
        }

        try
        {
            string? selectedPath = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                [".exe"],
                PickerLocationId.ComputerFolder);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            string normalized = selectedPath.Trim();
            await SaveSliceMutationAsync(slice =>
            {
                if (!slice.ClipboardExcludedApps.Any(existing =>
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    slice.ClipboardExcludedApps.Add(normalized);
                }
            });
            ExcludedAppsExpander.IsExpanded = true;
            RebuildExcludedAppLists();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Browse excluded app failed: {ex.Message}");
        }
    }

    private async void RemoveExcludedAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await SaveSliceMutationAsync(slice =>
        {
            slice.ClipboardExcludedApps.RemoveAll(existing =>
                string.Equals(existing, target, StringComparison.OrdinalIgnoreCase));
        });
        RebuildExcludedAppLists();
    }

    private async void ObservedAppCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string appPath } checkBox || string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        bool shouldExclude = checkBox.IsChecked == true;
        string exeName = Path.GetFileName(appPath);
        await SaveSliceMutationAsync(slice =>
        {
            if (shouldExclude)
            {
                if (!slice.ClipboardExcludedApps.Any(existing =>
                        string.Equals(existing, appPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(exeName) &&
                         string.Equals(Path.GetFileName(existing), exeName, StringComparison.OrdinalIgnoreCase))))
                {
                    slice.ClipboardExcludedApps.Add(appPath);
                }
            }
            else
            {
                slice.ClipboardExcludedApps.RemoveAll(existing =>
                    string.Equals(existing, appPath, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(exeName) &&
                     string.Equals(Path.GetFileName(existing), exeName, StringComparison.OrdinalIgnoreCase)));
            }
        });
        RebuildExcludedAppLists();
    }

    private async void MaxItemsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || MaxItemsComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int count))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardMaxItems = count);
    }

    private async void RetentionDaysComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || RetentionDaysComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int days))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardRetentionDays = days);
    }

    private void CancelClearUnpinnedButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUnpinnedConfirmFlyout.Hide();
    }

    private async void ConfirmClearUnpinnedButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUnpinnedConfirmFlyout.Hide();
        if (_dataActions is null)
        {
            return;
        }

        try
        {
            await _dataActions.ClearClipboardUnpinnedAsync();
            await RefreshRuntimeStatsAndObservedAppsAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Clear unpinned failed: {ex.Message}");
        }
    }

    private async Task SaveSliceMutationAsync(Action<ClipboardSettingsSlice> mutate)
    {
        if (_settingsService is null)
        {
            return;
        }

        try
        {
            _settingsService.MutateGraphUnderLock(() => mutate(_settingsService.Settings.Clipboard));
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Save slice mutation failed: {ex.Message}");
        }
    }

    private static void SelectByTag(ComboBox comboBox, string expectedTag)
    {
        ComboBoxItem? match = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            comboBox.SelectedItem = match;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return $"{kb:0.#} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return $"{mb:0.#} MB";
        }

        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }
}
