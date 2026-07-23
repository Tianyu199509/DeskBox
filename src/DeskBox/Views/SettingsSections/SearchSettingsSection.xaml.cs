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
    private long _lastProgressRefreshMs;
    private long _lastStorageRefreshMs;
    private string _lastStorageText = string.Empty;

    public SearchSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private SettingsService Settings => App.Current.SettingsService;
    private LocalizationService Localization => App.Current.LocalizationService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSettings();
        // Subscribe to real-time index progress updates.
        var engine = App.Current.SearchEngineService;
        if (engine is not null)
        {
            engine.IndexProgressChanged += OnIndexProgressChanged;
            engine.IndexUpdated += OnIndexCompleted;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var engine = App.Current.SearchEngineService;
        if (engine is not null)
        {
            engine.IndexProgressChanged -= OnIndexProgressChanged;
            engine.IndexUpdated -= OnIndexCompleted;
        }
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
            SearchIconAnimationComboBox.SelectedItem = SearchIconAnimationComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.SearchAppIconAnimation.ToString(),
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
        UpdateDashboardVisibility();
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

    private void SearchIconAnimationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchIconAnimationComboBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, out int style))
        {
            return;
        }

        Settings.Settings.SearchAppIconAnimation = style;
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
            SearchIndexCountText.Text = string.Empty;
            SearchIndexStorageText.Text = string.Empty;
            HideProgressBar();
            IndexPauseResumeButton.IsEnabled = false;
            UpdateDashboardVisibility();
            return;
        }

        var engine = App.Current.SearchEngineService;
        bool isScanning = engine is { IsCustomIndexing: true };
        bool isPaused = engine is { IsIndexPaused: true };

        // Status text
        if (isPaused)
        {
            SearchIndexStatusText.Text = Localization.T("Settings.Search.Index.Status.Paused");
        }
        else if (isScanning)
        {
            SearchIndexStatusText.Text = Localization.T("Settings.Search.Index.Status.Scanning");
        }
        else
        {
            SearchIndexStatusText.Text = Localization.T("Settings.Search.Index.Status.Idle");
        }

        // Entry count
        int count = engine?.IndexedItemCount ?? 0;
        SearchIndexCountText.Text = Localization.Format("Settings.Search.Index.Status.Ready", count);

        // Progress bar visibility with fade-out animation
        bool showProgress = isScanning && !isPaused;
        if (showProgress)
        {
            IndexProgressBar.Opacity = 1;
            IndexProgressBar.Visibility = Visibility.Visible;
        }
        else
        {
            HideProgressBar();
        }

        // Storage info (throttled: refresh at most every 5 seconds to avoid disk I/O)
        RefreshStorageInfo(engine);

        // Pause/Resume button (always visible when indexer enabled, disabled when idle)
        IndexPauseResumeButton.IsEnabled = isScanning || isPaused;

        if (isPaused)
        {
            IndexPauseResumeIcon.Glyph = "\uE768"; // Play icon
            IndexPauseResumeLabel.Text = Localization.T("Settings.Search.Index.Resume");
        }
        else
        {
            IndexPauseResumeIcon.Glyph = "\uE769"; // Pause icon
            IndexPauseResumeLabel.Text = Localization.T("Settings.Search.Index.Pause");
        }

        UpdateDashboardVisibility();
    }

    private void RefreshStorageInfo(SearchEngineService? engine)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastStorageRefreshMs);
        // Throttle storage info to 5 seconds to avoid frequent disk I/O during indexing.
        if (now - last < 5000 && !string.IsNullOrEmpty(_lastStorageText))
        {
            SearchIndexStorageText.Text = _lastStorageText;
            return;
        }

        Interlocked.Exchange(ref _lastStorageRefreshMs, now);
        long bytes = engine?.GetIndexStorageBytes() ?? 0;
        string storageStr = FormatBytes(bytes);
        string lastScan = engine?.LastScanTime is { } time
            ? time.ToString("g")
            : Localization.T("Settings.Search.Index.LastScan.Never");
        _lastStorageText = Localization.Format("Settings.Search.Index.StorageInfo", storageStr, lastScan);
        SearchIndexStorageText.Text = _lastStorageText;
    }

    private void HideProgressBar()
    {
        if (IndexProgressBar.Visibility == Visibility.Collapsed)
        {
            return;
        }

        // Fade out animation for smooth transition
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(300)
        };
        storyboard.Children.Add(fadeOut);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOut, IndexProgressBar);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOut, "Opacity");
        storyboard.Completed += (_, _) =>
        {
            IndexProgressBar.Visibility = Visibility.Collapsed;
            IndexProgressBar.Opacity = 1; // Reset for next show
        };
        storyboard.Begin();
    }

    private void UpdateDashboardVisibility()
    {
        IndexDashboardCard.Visibility = Settings.Settings.SearchCustomIndexerEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnIndexProgressChanged(int count)
    {
        // Throttle: at most one full UI refresh per 500 ms to avoid flooding the
        // dispatcher when the index service reports progress every few entries.
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastProgressRefreshMs);
        if (now - last < 500 ||
            Interlocked.CompareExchange(ref _lastProgressRefreshMs, now, last) != last)
            return;

        _ = DispatcherQueue.TryEnqueue(RefreshIndexStatus);
    }

    private void OnIndexCompleted()
    {
        _ = DispatcherQueue.TryEnqueue(() => RefreshIndexStatus());
    }

    private void IndexPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        var engine = App.Current.SearchEngineService;
        if (engine is null)
        {
            return;
        }

        if (engine.IsIndexPaused)
        {
            engine.ResumeIndexing();
        }
        else
        {
            engine.PauseIndexing();
        }

        RefreshIndexStatus();
    }

    private void IndexRebuildButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.SearchEngineService?.RebuildIndex();
        RefreshIndexStatus();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{size:F1} {units[unitIndex]}";
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
