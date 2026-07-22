using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the global search feature: hotkey, display mode, scopes and
/// recommendations. Reads and writes settings directly through the shared SettingsService.
/// </summary>
public sealed partial class SearchSettingsSection : UserControl
{
    private bool _isLoading;
    private bool _isRecordingHotkey;

    public SearchSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private SettingsService Settings => App.Current.SettingsService;
    private LocalizationService Localization => App.Current.LocalizationService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSettings();
    }

    /// <summary>
    /// Re-reads settings and updates the controls. Called when the section becomes visible.
    /// </summary>
    public void RefreshFromSettings()
    {
        _isLoading = true;
        try
        {
            var settings = Settings.Settings;
            SearchHotkeyToggle.IsOn = settings.SearchHotkeyEnabled;
            SearchDeskBoxContentToggle.IsOn = settings.SearchIncludeDeskBoxContent;
            SearchSystemIndexToggle.IsOn = settings.SearchIncludeSystemIndex;
            SearchCustomIndexerToggle.IsOn = settings.SearchCustomIndexerEnabled;
            SearchRecommendationsToggle.IsOn = settings.SearchShowRecommendations;
            SearchDefaultTabComboBox.SelectedItem = SearchDefaultTabComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.SearchDefaultTab,
                    StringComparison.OrdinalIgnoreCase));
            SearchMaxResultsComboBox.SelectedItem = SearchMaxResultsComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.SearchMaxResults.ToString(),
                    StringComparison.Ordinal));
        }
        finally
        {
            _isLoading = false;
        }

        RefreshHotkeyControls();
        RefreshIndexStatus();
    }

    private void SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        bool enabled = SearchHotkeyToggle.IsOn;
        Settings.Settings.SearchHotkeyEnabled = enabled;
        Settings.SaveDebounced();
        App.Current.SearchHotkeyService?.SetEnabled(enabled);
        RefreshHotkeyControls();
    }

    private void SearchScopeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var settings = Settings.Settings;
        settings.SearchIncludeDeskBoxContent = SearchDeskBoxContentToggle.IsOn;
        settings.SearchIncludeSystemIndex = SearchSystemIndexToggle.IsOn;
        settings.SearchCustomIndexerEnabled = SearchCustomIndexerToggle.IsOn;
        Settings.SaveDebounced();
        App.Current.SearchEngineService?.SetCustomIndexingEnabled(settings.SearchCustomIndexerEnabled);
        RefreshIndexStatus();
    }

    private void SearchRecommendationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Settings.SearchShowRecommendations = SearchRecommendationsToggle.IsOn;
        Settings.SaveDebounced();
    }

    private void SearchDefaultTabComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchDefaultTabComboBox.SelectedItem is not ComboBoxItem { Tag: string tabId })
        {
            return;
        }

        Settings.Settings.SearchDefaultTab = tabId;
        Settings.SaveDebounced();
    }

    private void SearchMaxResultsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchMaxResultsComboBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, out int maxResults))
        {
            return;
        }

        Settings.Settings.SearchMaxResults = maxResults;
        Settings.SaveDebounced();
    }

    private void ClearSearchActivityButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.SearchHistoryService?.ClearAllHistory();
    }

    private void RefreshIndexStatus()
    {
        if (!Settings.Settings.SearchCustomIndexerEnabled)
        {
            SearchIndexStatusText.Text = Localization.T("Settings.Search.Index.Status.Disabled");
            return;
        }

        var engine = App.Current.SearchEngineService;
        SearchIndexStatusText.Text = engine is { IsCustomIndexing: true }
            ? Localization.T("Settings.Search.Index.Status.Scanning")
            : Localization.Format("Settings.Search.Index.Status.Ready", engine?.IndexedItemCount ?? 0);
    }

    // ─── Hotkey capture ───────────────────────────────────────────────

    private void SearchHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        SearchHotkeyCaptureButton.Content = Localization.T("Settings.Search.Hotkey.Recording");
        SearchHotkeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private void SearchHotkeyCaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new GlobalHotkeyGesture(GetPressedModifiers(), (int)e.Key);
        ApplyGesture(gesture);
        e.Handled = true;
    }

    private void SearchHotkeyCaptureButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            EndHotkeyRecording();
        }
    }

    private void ResetSearchHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Settings;
        settings.SearchHotkeyModifiers = (int)HotkeyModifierKeys.Alt;
        settings.SearchHotkeyKey = 0x44; // Alt+D default
        Settings.SaveDebounced();
        App.Current.SearchHotkeyService?.RefreshRegistration();
        RefreshHotkeyControls();
    }

    private void ApplyGesture(GlobalHotkeyGesture gesture)
    {
        EndHotkeyRecording();

        var hotkeyService = App.Current.SearchHotkeyService;
        if (hotkeyService is null)
        {
            return;
        }

        if (!hotkeyService.TryApplyGesture(gesture))
        {
            SearchHotkeyStatusText.Text = Localization.T("Settings.Search.Hotkey.Status.Failed");
            return;
        }

        RefreshHotkeyControls();
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        RefreshHotkeyControls();
    }

    private void RefreshHotkeyControls()
    {
        var settings = Settings.Settings;
        var gesture = GlobalHotkeyService.NormalizeGesture(settings.SearchHotkeyModifiers, settings.SearchHotkeyKey);

        if (!_isRecordingHotkey)
        {
            SearchHotkeyCaptureButton.Content = GlobalHotkeyService.FormatGesture(gesture, Localization);
        }

        SearchHotkeyStatusText.Text = settings.SearchHotkeyEnabled
            ? Localization.T("Settings.Search.Hotkey.Status.Active")
            : Localization.T("Settings.Search.Hotkey.Status.Disabled");
    }

    private static HotkeyModifierKeys GetPressedModifiers()
    {
        var modifiers = HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Windows.System.VirtualKey key)
    {
        return key is
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows;
    }

}
