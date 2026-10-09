using DeskBox.Helpers;
using DeskBox.Models;
using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Features.Search;
using DeskBox.Services;
using DeskBox.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the global search feature: hotkey, display mode, scopes and
/// recommendations. The injected editor owns settings operations and visit cancellation.
/// </summary>
public sealed partial class SearchSettingsSection : UserControl
{
    private bool _isLoading;
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
        // Local brush values do not follow theme swaps; re-pin when the
        // realized section's theme flips.
        ActualThemeChanged += (_, _) => ApplyElevatedNoticeSeverityBrushes();
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
            _viewModel.Deactivate();
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hostActive && IsLoaded) RenderEditor();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyElevatedNoticeSeverityBrushes();
        // Both notices keep the template-default title (14px semi-bold) with
        // the message sized down to the settings-card description spec; see
        // SettingsWindow.ApplyNoticeMessageTypography for why the template
        // text block must be retargeted from code-behind.
        SettingsWindow.ApplyNoticeMessageTypography(EverythingStatusInfoBar);
        SettingsWindow.ApplyNoticeMessageTypography(SearchHotkeyElevatedNoticeInfoBar);
        SynchronizeActivity();
    }

    /// <summary>
    /// The elevated-hotkey notice's ThemeResource severity brushes fail to
    /// resolve inside sections realized through DataTemplate.LoadContent
    /// (the known delayed-creation pitfall), leaving the bar painted like
    /// the card behind it with near-invisible text. Pin the informational
    /// severity brushes from the application resources so the notice keeps
    /// a distinct background and readable text in both themes.
    /// </summary>
    private void ApplyElevatedNoticeSeverityBrushes()
    {
        if (Application.Current.Resources.TryGetValue(
                "InfoBarInformationalSeverityBackgroundBrush", out object? background) &&
            background is Brush backgroundBrush)
        {
            SearchHotkeyElevatedNoticeInfoBar.Background = backgroundBrush;
        }
        if (Application.Current.Resources.TryGetValue(
                "InfoBarInformationalSeverityForegroundBrush", out object? foreground) &&
            foreground is Brush foregroundBrush)
        {
            SearchHotkeyElevatedNoticeInfoBar.Foreground = foregroundBrush;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnEditorChanged;
        _observingModel = false;
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
            string? executablePath = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                [".exe"],
                PickerLocationId.ComputerFolder);
            if (executablePath is null || visit.IsCancellationRequested) return;
            await editor.SelectExecutableAsync(executablePath);
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

        // Content-level gating on purpose: the expander itself stays enabled
        // so the notice about the unavailable hotkey can always be read;
        // only the controls that would mutate the hotkey go gray.
        SearchHotkeyToggle.IsEnabled = hotkeyAvailable;
        SearchHotkeyCaptureButton.IsEnabled = hotkeyAvailable;
        ResetSearchHotkeyButton.IsEnabled = hotkeyAvailable;
        SearchHotkeyToggle.IsOn = hotkey.Enabled && hotkeyAvailable;
        SearchHotkeyCaptureButton.Content = hotkey.DisplayText;

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

    private async void SearchHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || _viewModel is not { IsActive: true } editor)
        {
            return;
        }

        HotkeyRecorderDialog.Preset[] presets =
        [
            new(
                Localization.T("Settings.GlobalHotkey.Preset.AltSpace"),
                GlobalHotkeyActivation.FromChord(SearchSettingsViewModel.AltSpaceGesture))
        ];

        var dialog = new HotkeyRecorderDialog(
            XamlRoot,
            Localization,
            HotkeyRecorderDialog.Scope.Search,
            GlobalHotkeyActivation.FromChord(editor.State.Hotkey.Gesture),
            presets,
            activation => ApplySearchHotkeyFromRecorderAsync(editor, activation),
            GetSearchHotkeyRecorderConflict);
        await dialog.ShowAsync();
    }

    private Task<string?> ApplySearchHotkeyFromRecorderAsync(
        SearchSettingsViewModel editor,
        GlobalHotkeyActivation activation)
    {
        try
        {
            editor.ApplyHotkey(activation.Gesture);
            return Task.FromResult(editor.HotkeyError);
        }
        catch (Exception ex)
        {
            editor.ReportViewError(ex);
            return Task.FromResult<string?>(
                Localization.T("Settings.Search.Hotkey.Status.Failed"));
        }
    }

    private string? GetSearchHotkeyRecorderConflict(GlobalHotkeyGesture gesture)
    {
        return _viewModel is { IsActive: true } editor &&
            editor.IsHotkeyOwnedByMainHotkey(gesture)
                ? Localization.T("Settings.Search.Hotkey.Status.GlobalHotkeyConflict")
                : null;
    }

    private void ResetSearchHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ResetHotkey();
    }

}
