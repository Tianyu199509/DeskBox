using DeskBox.Helpers;
using DeskBox.Models;
using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Features.Search;
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
/// recommendations. The injected editor owns settings operations and visit cancellation.
/// </summary>
public sealed partial class SearchSettingsSection : UserControl
{
    private bool _isLoading;
    private bool _isRecordingSearchHotkey;
    private SearchSettingsViewModel? _viewModel;
    private LocalizationService _localization = null!;
    private nint _ownerWindow;
    private bool _hostActive;
    private bool _observingModel;

    public SearchSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private LocalizationService Localization => _localization;

    public void Configure(SearchSettingsViewModel viewModel, LocalizationService localization, nint ownerWindow)
    {
        _viewModel = viewModel;
        _localization = localization;
        _ownerWindow = ownerWindow;
        RefreshFromSettings();
    }

    public void SetActive(bool active)
    {
        _hostActive = active;
        SynchronizeActivity();
    }

    private void SynchronizeActivity()
    {
        if (_viewModel is null) return;
        bool active = _hostActive && IsLoaded;
        if (_observingModel != active)
        {
            if (active) _viewModel.PropertyChanged += OnEditorChanged;
            else _viewModel.PropertyChanged -= OnEditorChanged;
            _observingModel = active;
        }
        if (active)
        {
            _viewModel.Activate();
            RefreshFromSettings();
        }
        else
        {
            _isRecordingSearchHotkey = false;
            _viewModel.Deactivate();
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hostActive && IsLoaded) RenderEditor();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SynchronizeActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnEditorChanged;
        _observingModel = false;
        _isRecordingSearchHotkey = false;
        _viewModel.Deactivate();
    }

    /// <summary>
    /// Re-reads settings and updates the controls. Called when the section becomes visible.
    /// </summary>
    public void RefreshFromSettings()
    {
        if (_viewModel is null) return;
        _viewModel.RefreshState();
        RenderEditor();
    }

    private void RenderEditor()
    {
        if (_viewModel is null) return;
        _isLoading = true;
        try
        {
            SearchPreferences settings = _viewModel.State.Preferences;
            EverythingConsentCheckBox.IsChecked = settings.EverythingEnabled;
            EverythingAdvancedSyntaxToggle.IsOn =
                settings.AdvancedSyntax;
            EverythingAdvancedSyntaxToggle.IsEnabled = settings.EverythingEnabled;
            SearchDeskBoxContentToggle.IsOn = settings.IncludeDeskBoxContent;
            SearchRecommendationsToggle.IsOn = settings.ShowRecommendations;
            SearchDefaultTabComboBox.SelectedItem = SearchDefaultTabComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.DefaultTab,
                    StringComparison.OrdinalIgnoreCase));
            SearchIconAnimationComboBox.SelectedItem = SearchIconAnimationComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.IconAnimation.ToString(),
                    StringComparison.Ordinal));
            RefreshSearchHotkeyControls();
            UpdateEverythingDashboard(_viewModel.Connection);
            bool canOperate = _viewModel.State.FeatureEnabled && !_viewModel.IsBusy;
            EverythingDetectButton.IsEnabled = canOperate;
            EverythingBrowseButton.IsEnabled = canOperate;
            EverythingLaunchButton.IsEnabled = canOperate;
        }
        finally
        {
            _isLoading = false;
        }

    }

    private void SearchScopeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(IncludeDeskBoxContent: SearchDeskBoxContentToggle.IsOn));
    }

    private void SearchRecommendationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(ShowRecommendations: SearchRecommendationsToggle.IsOn));
    }

    private void SearchDefaultTabComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchDefaultTabComboBox.SelectedItem is not ComboBoxItem { Tag: string tabId })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(DefaultTab: tabId));
    }

    private void SearchIconAnimationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchIconAnimationComboBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, out int style))
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(IconAnimation: style));
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

        bool enabled = _viewModel!.State.Preferences.EverythingEnabled;
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

        if (!_viewModel.State.FeatureEnabled)
        {
            EverythingStatusInfoBar.Severity = InfoBarSeverity.Informational;
            EverythingStatusInfoBar.Message = Localization.T("Settings.Search.Index.Status.Disabled");
        }
        else if (_viewModel.Failure != SearchSettingsFailure.None)
        {
            EverythingStatusInfoBar.Severity = InfoBarSeverity.Error;
            EverythingStatusInfoBar.Message = Localization.T(
                _viewModel.Failure == SearchSettingsFailure.InvalidExecutable
                    ? "Settings.Search.Everything.Status.InvalidExecutable"
                    : "Settings.Search.Everything.Status.Error");
        }
    }

    private void EverythingConsentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        bool enabled = EverythingConsentCheckBox.IsChecked == true;
        _viewModel?.UpdatePreferences(new(EverythingEnabled: enabled));
    }

    private void EverythingAdvancedSyntaxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(AdvancedSyntax: EverythingAdvancedSyntaxToggle.IsOn));
    }

    private async void EverythingDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } editor) await editor.DetectAutomaticallyAsync();
    }

    private async void EverythingLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } editor) await editor.LaunchEverythingAsync();
    }

    private async void EverythingBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { IsActive: true } editor || _ownerWindow == 0) return;
        CancellationToken visit = editor.VisitToken;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, _ownerWindow);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null || visit.IsCancellationRequested) return;
            await editor.SelectExecutableAsync(file.Path);
        }
        catch (Exception ex)
        {
            if (!visit.IsCancellationRequested) editor.ReportViewError(ex);
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
        if (_viewModel is null) return;
        SearchHotkeyState hotkey = _viewModel.State.Hotkey;
        bool hotkeyAvailable = _viewModel.State.FeatureEnabled && hotkey.Available;

        SearchHotkeyExpander.IsEnabled = hotkeyAvailable;
        SearchHotkeyToggle.IsOn = hotkey.Enabled && hotkeyAvailable;

        if (!_isRecordingSearchHotkey)
        {
            SearchHotkeyCaptureButton.Content = hotkey.DisplayText;
        }

        SearchHotkeyPresetAltSpaceButton.IsChecked =
            hotkey.Gesture.Equals(SearchSettingsViewModel.AltSpaceGesture);

        SearchHotkeyStatusText.Text = !hotkeyAvailable
            ? Localization.T("Settings.Search.Hotkey.Status.Disabled")
            : _viewModel.HotkeyError ?? Localization.T(
                !hotkey.Enabled ? "Settings.Search.Hotkey.Status.Disabled" :
                hotkey.Registered ? "Settings.Search.Hotkey.Status.Active" : "Settings.Search.Hotkey.Status.Failed");
    }

    private void SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.SetHotkeyEnabled(SearchHotkeyToggle.IsOn);
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
        e.Handled = true;
        await ApplySearchHotkeyGestureAsync(gesture);
    }

    private async void SearchHotkeyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not ToggleButton { Tag: "AltSpace" })
        {
            return;
        }

        await ApplySearchHotkeyGestureAsync(SearchSettingsViewModel.AltSpaceGesture);
    }

    private async Task ApplySearchHotkeyGestureAsync(GlobalHotkeyGesture gesture)
    {
        if (_viewModel is not { IsActive: true } editor) return;
        CancellationToken visit = editor.VisitToken;
        try
        {
            if (editor.RequiresReservedHotkeyConfirmation(gesture) &&
                !await ConfirmSearchReservedHotkeyOverrideAsync())
            {
                if (!visit.IsCancellationRequested) RenderEditor();
                return;
            }
            if (!visit.IsCancellationRequested) editor.ApplyHotkey(gesture);
        }
        catch (Exception ex)
        {
            if (!visit.IsCancellationRequested) editor.ReportViewError(ex);
        }
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
            Title = _viewModel!.ReservedHotkeyDisplayText,
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
        _viewModel?.ResetHotkey();
    }

    private void EndSearchHotkeyRecording()
    {
        _isRecordingSearchHotkey = false;
        RenderEditor();
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
