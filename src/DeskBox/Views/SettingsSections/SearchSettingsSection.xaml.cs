using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the global search feature: hotkey, display mode, scopes and
/// recommendations. Reads and writes settings directly through the shared SettingsService.
/// </summary>
public sealed partial class SearchSettingsSection : UserControl
{
    private bool _isLoading;
    private bool _isRecordingSearchHotkey;
    private EverythingSearchService? _observedEverythingProvider;
    private CancellationTokenSource? _everythingRefreshCts;

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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _everythingRefreshCts?.Cancel();
        _everythingRefreshCts?.Dispose();
        _everythingRefreshCts = null;
        ObserveEverythingProvider(null);
    }

    private void ObserveEverythingProvider(EverythingSearchService? provider)
    {
        if (ReferenceEquals(_observedEverythingProvider, provider))
        {
            return;
        }

        if (_observedEverythingProvider is not null)
        {
            _observedEverythingProvider.ConnectionChanged -= OnEverythingConnectionChanged;
        }

        _observedEverythingProvider = provider;
        if (_observedEverythingProvider is not null)
        {
            _observedEverythingProvider.ConnectionChanged += OnEverythingConnectionChanged;
        }
    }

    private EverythingSearchService? EnsureEverythingProviderForUserAction()
    {
        var engine = App.Current.EnsureSearchServicesForUserAction();
        EverythingSearchService? provider = engine?.EverythingProvider;
        ObserveEverythingProvider(provider);
        return provider;
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
            EverythingConsentCheckBox.IsChecked = settings.SearchEverythingEnabled;
            EverythingAdvancedSyntaxToggle.IsOn =
                settings.SearchEverythingAdvancedSyntaxEnabled;
            EverythingAdvancedSyntaxToggle.IsEnabled = settings.SearchEverythingEnabled;
            SearchDeskBoxContentToggle.IsOn = settings.SearchIncludeDeskBoxContent;
            SearchRecommendationsToggle.IsOn = settings.SearchShowRecommendations;
            SearchDefaultTabComboBox.SelectedItem = SearchDefaultTabComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.SearchDefaultTab,
                    StringComparison.OrdinalIgnoreCase));
            SearchIconAnimationComboBox.SelectedItem = SearchIconAnimationComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.SearchAppIconAnimation.ToString(),
                    StringComparison.Ordinal));
            RefreshSearchHotkeyControls();
        }
        finally
        {
            _isLoading = false;
        }

        EverythingSearchService? provider = EnsureEverythingProviderForUserAction();
        if (provider is not null)
        {
            UpdateEverythingDashboard(provider.CurrentSnapshot);
            if (IsLoaded && Visibility == Visibility.Visible)
            {
                QueueEverythingRefresh();
            }
        }
    }

    private void SearchScopeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var settings = Settings.Settings;
        settings.SearchIncludeDeskBoxContent = SearchDeskBoxContentToggle.IsOn;
        Settings.SaveDebounced();
        App.Current.SearchEngineService?.SetDeskBoxContentSearchEnabled(
            settings.SearchIncludeDeskBoxContent);
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

    private void QueueEverythingRefresh()
    {
        EverythingSearchService? provider = EnsureEverythingProviderForUserAction();
        if (provider is null)
        {
            return;
        }

        _everythingRefreshCts?.Cancel();
        _everythingRefreshCts?.Dispose();
        _everythingRefreshCts = new CancellationTokenSource();
        _ = RefreshEverythingStatusAsync(provider, _everythingRefreshCts.Token);
    }

    private async Task RefreshEverythingStatusAsync(
        EverythingSearchService provider,
        CancellationToken cancellationToken)
    {
        try
        {
            EverythingConnectionSnapshot snapshot = await provider.RefreshConnectionAsync(
                allowIpcProbe: Settings.Settings.SearchEverythingEnabled,
                cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                _ = DispatcherQueue.TryEnqueue(() => UpdateEverythingDashboard(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer detection or the settings window closing.
        }
        catch (Exception ex)
        {
            App.Log($"[Everything] Settings refresh failed: {ex.Message}");
        }
    }

    private void OnEverythingConnectionChanged(EverythingConnectionSnapshot snapshot)
    {
        _ = DispatcherQueue.TryEnqueue(() => UpdateEverythingDashboard(snapshot));
    }

    private async void EverythingAboutButton_Click(object sender, RoutedEventArgs e)
{
    var dialog = new ContentDialog
    {
        Title = Localization.T("Settings.Search.Everything.About.Title"),
        CloseButtonText = Localization.T("Settings.Dialog.SupportClose"),
        DefaultButton = ContentDialogButton.Close,
        XamlRoot = XamlRoot
    };
    var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
    body.Children.Add(new TextBlock
    {
        Text = Localization.T("Settings.Search.Everything.About.P1"),
        TextWrapping = TextWrapping.Wrap
    });
    body.Children.Add(new TextBlock
    {
        Text = Localization.T("Settings.Search.Everything.SharingNotice"),
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.8
    });
    body.Children.Add(new TextBlock
    {
        Text = Localization.T("Settings.Search.Everything.About.P3"),
        TextWrapping = TextWrapping.Wrap
    });
    var siteLink = new HyperlinkButton
    {
        NavigateUri = new Uri("https://www.voidtools.com/"),
        Content = new TextBlock
        {
            Text = Localization.T("Settings.Search.Everything.Download"),
            TextWrapping = TextWrapping.Wrap
        },
        Padding = new Thickness(0)
    };
    body.Children.Add(siteLink);
    dialog.Content = body;
    await dialog.ShowAsync();
}

private void UpdateEverythingDashboard(EverythingConnectionSnapshot snapshot)
    {
        EverythingStatusInfoBar.Title =
            Localization.T("Settings.Search.Everything.StatusTitle");
        EverythingStatusInfoBar.Severity = snapshot.State switch
        {
            EverythingConnectionState.Connected => InfoBarSeverity.Success,
            EverythingConnectionState.Checking or EverythingConnectionState.NotConfirmed =>
                InfoBarSeverity.Informational,
            EverythingConnectionState.NotInstalled or EverythingConnectionState.NotRunning =>
                InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error
        };
        EverythingStatusInfoBar.Message = snapshot.State switch
        {
            EverythingConnectionState.Unknown =>
                Localization.T("Settings.Search.Everything.Status.Unknown"),
            EverythingConnectionState.Checking =>
                Localization.T("Settings.Search.Everything.Status.Checking"),
            EverythingConnectionState.NotConfirmed =>
                Localization.T("Settings.Search.Everything.Status.NotConfirmed"),
            EverythingConnectionState.NotInstalled =>
                Localization.T("Settings.Search.Everything.Status.NotInstalled"),
            EverythingConnectionState.NotRunning =>
                Localization.T("Settings.Search.Everything.Status.NotRunning"),
            EverythingConnectionState.PermissionMismatch =>
                Localization.T("Settings.Search.Everything.Status.PermissionMismatch"),
            EverythingConnectionState.IpcUnavailable =>
                Localization.T("Settings.Search.Everything.Status.IpcUnavailable"),
            EverythingConnectionState.SdkUnavailable =>
                Localization.T("Settings.Search.Everything.Status.SdkUnavailable"),
            EverythingConnectionState.Connected => Localization.Format(
                "Settings.Search.Everything.Status.Connected",
                snapshot.Version ?? Localization.T("Settings.Search.Everything.VersionUnknown")),
            _ => Localization.T("Settings.Search.Everything.Status.Error")
        };

        EverythingPathText.Text = string.IsNullOrWhiteSpace(snapshot.ExecutablePath)
            ? Localization.T("Settings.Search.Everything.Path.NotFound")
            : Localization.Format(
                snapshot.UsesManualPath
                    ? "Settings.Search.Everything.Path.Manual"
                    : "Settings.Search.Everything.Path.Detected",
                snapshot.ExecutablePath);

        EverythingDownloadLink.Visibility =
            snapshot.State == EverythingConnectionState.NotInstalled
                ? Visibility.Visible
                : Visibility.Collapsed;

        bool enabled = Settings.Settings.SearchEverythingEnabled;
        EverythingAdvancedSyntaxToggle.IsEnabled = enabled;
        EverythingLaunchButton.Visibility =
            !string.IsNullOrWhiteSpace(snapshot.ExecutablePath) && !snapshot.IsRunning
                ? Visibility.Visible
                : Visibility.Collapsed;
        EverythingDownloadButton.Visibility =
            snapshot.State == EverythingConnectionState.NotInstalled
                ? Visibility.Visible
                : Visibility.Collapsed;
        EverythingHelpButton.Visibility = snapshot.State is
            EverythingConnectionState.PermissionMismatch or
            EverythingConnectionState.IpcUnavailable
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void EverythingConsentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        bool enabled = EverythingConsentCheckBox.IsChecked == true;
        Settings.Settings.SearchEverythingEnabled = enabled;
        Settings.SaveDebounced();
        EverythingAdvancedSyntaxToggle.IsEnabled = enabled;
        QueueEverythingRefresh();
    }

    private void EverythingAdvancedSyntaxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        Settings.Settings.SearchEverythingAdvancedSyntaxEnabled =
            EverythingAdvancedSyntaxToggle.IsOn;
        Settings.SaveDebounced();
    }

    private async void EverythingDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureEverythingProviderForUserAction() is not { } provider)
        {
            return;
        }

        EverythingDetectButton.IsEnabled = false;
        try
        {
            await provider.UseAutomaticDetectionAsync();
        }
        finally
        {
            EverythingDetectButton.IsEnabled = true;
        }
    }

    private async void EverythingLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureEverythingProviderForUserAction() is not { } provider)
        {
            return;
        }

        EverythingLaunchButton.IsEnabled = false;
        try
        {
            _ = await provider.LaunchEverythingAsync();
        }
        finally
        {
            EverythingLaunchButton.IsEnabled = true;
        }
    }

    private async void EverythingBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.SettingsWindowInstance is not { } settingsWindow ||
            EnsureEverythingProviderForUserAction() is not { } provider)
        {
            return;
        }

        nint owner = WindowNative.GetWindowHandle(settingsWindow);
        if (owner == 0)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, owner);
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        if (!await provider.SetExecutablePathAsync(file.Path))
        {
            EverythingStatusInfoBar.Severity = InfoBarSeverity.Error;
            EverythingStatusInfoBar.Message =
                Localization.T("Settings.Search.Everything.Status.InvalidExecutable");
        }
    }

    private async void EverythingDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        _ = await Launcher.LaunchUriAsync(new Uri("https://www.voidtools.com/downloads/"));
    }

    private async void EverythingHelpButton_Click(object sender, RoutedEventArgs e)
    {
        _ = await Launcher.LaunchUriAsync(new Uri(
            "https://www.voidtools.com/support/everything/installing-everything/"));
    }

    private void RefreshSearchHotkeyControls()
    {
        var settings = Settings.Settings;
        var gesture = GlobalHotkeyService.NormalizeGesture(
            settings.SearchHotkeyModifiers,
            settings.SearchHotkeyKey);
        bool searchWidgetEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Search);
        bool hotkeyAvailable = searchWidgetEnabled && App.Current.SearchHotkeyService is not null;

        SearchHotkeyExpander.IsEnabled = hotkeyAvailable;
        SearchHotkeyToggle.IsOn = settings.SearchHotkeyEnabled && hotkeyAvailable;

        if (!_isRecordingSearchHotkey)
        {
            SearchHotkeyCaptureButton.Content = GlobalHotkeyService.FormatGesture(gesture, Localization);
        }

        SearchHotkeyPresetAltSpaceButton.IsChecked =
            gesture.Equals(SearchHotkeyService.AltSpaceGesture);

        SearchHotkeyStatusText.Text = settings.SearchHotkeyEnabled && hotkeyAvailable
            ? Localization.T("Settings.Search.Hotkey.Status.Active")
            : Localization.T("Settings.Search.Hotkey.Status.Disabled");
    }

    private void SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        if (App.Current.SearchHotkeyService is not { } service)
        {
            RefreshSearchHotkeyControls();
            return;
        }

        service.SetEnabled(SearchHotkeyToggle.IsOn);
        RefreshSearchHotkeyControls();
    }

    private void SearchHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingSearchHotkey = true;
        SearchHotkeyCaptureButton.Content = Localization.T("Settings.Search.Hotkey.Recording");
        SearchHotkeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private async void SearchHotkeyCaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingSearchHotkey)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndSearchHotkeyRecording();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new GlobalHotkeyGesture(GetPressedHotkeyModifiers(), (int)e.Key);
        EndSearchHotkeyRecording();
        await ApplySearchHotkeyGestureAsync(gesture);
        e.Handled = true;
    }

    private async void SearchHotkeyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not ToggleButton { Tag: "AltSpace" })
        {
            return;
        }

        await ApplySearchHotkeyGestureAsync(SearchHotkeyService.AltSpaceGesture);
    }

    private async Task ApplySearchHotkeyGestureAsync(GlobalHotkeyGesture gesture)
    {
        if (App.Current.SearchHotkeyService is not { } service)
        {
            RefreshSearchHotkeyControls();
            return;
        }

        var settings = Settings.Settings;
        var current = GlobalHotkeyService.NormalizeGesture(
            settings.SearchHotkeyModifiers,
            settings.SearchHotkeyKey);
        if (gesture.Equals(SearchHotkeyService.AltSpaceGesture) &&
            !gesture.Equals(current) &&
            !await ConfirmSearchReservedHotkeyOverrideAsync())
        {
            RefreshSearchHotkeyControls();
            return;
        }

        if (!service.TryApplyGesture(gesture, out string? error))
        {
            SearchHotkeyStatusText.Text = error ??
                Localization.T("Settings.Search.Hotkey.Status.Failed");
            return;
        }

        RefreshSearchHotkeyControls();
    }

    private async Task<bool> ConfirmSearchReservedHotkeyOverrideAsync()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = GlobalHotkeyService.FormatGesture(
                SearchHotkeyService.AltSpaceGesture,
                Localization),
            PrimaryButtonText = Localization.T("Common.Enable"),
            CloseButtonText = Localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = Localization.T("Settings.GlobalHotkey.AltSpaceWarning"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SearchHotkeyCaptureButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingSearchHotkey)
        {
            EndSearchHotkeyRecording();
        }
    }

    private void ResetSearchHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Settings;
        settings.SearchHotkeyModifiers = (int)HotkeyModifierKeys.Alt;
        settings.SearchHotkeyKey = 0x44; // Alt+D default
        Settings.SaveDebounced();
        App.Current.SearchHotkeyService?.RefreshRegistration();
        RefreshSearchHotkeyControls();
    }

    private void EndSearchHotkeyRecording()
    {
        _isRecordingSearchHotkey = false;
        RefreshSearchHotkeyControls();
    }

    private static HotkeyModifierKeys GetPressedHotkeyModifiers()
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
