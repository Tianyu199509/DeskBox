using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
private void OnLanguageChanged()
{
    RefreshLocalizedProperties();
    _musicSettings.RefreshLocalization();
    _interactionSettings.RefreshLocalization();
    _interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());
    RefreshGlobalHotkeyState();
    // The managed-storage editor's option list re-localizes itself and
    // the quick-access card is re-pushed in the new language.
    _managedStorageSettings.RefreshLocalization();
    PushQuickAccessPresentation();
    // The appearance editor's option tables and value texts re-localize
    // themselves. The group-navigation and capsule editors rebuild their
    // option tables too; their pushed projections (existing groups, override
    // lists/summaries) are rebuilt by the notify calls below.
    _appearanceSettings.RefreshLocalization();
    _groupNavigationSettings.RefreshLocalization();
    _capsuleSettings.RefreshLocalization();
    RefreshWidgetGroupSettings();
    // The Quick Capture editor re-localizes its option tables, summaries and
    // the clipboard-diagnostics line (batch 46); the Todo editor rebuilds
    // its option tables, summaries and tab texts (batch 47); the Weather
    // editor rebuilds its option tables, summary, placeholder and no-results
    // text, and its suggestion list is dropped so the popular cities
    // repopulate in the new language (batch 48).
    _quickCaptureSettingsEditor.RefreshLocalization();
    _todoSettings.RefreshLocalization();
    _weatherSettings.RefreshLocalization();
    _weatherSettings.ClearCitySuggestions();
    // The performance editor's option tables and the decorative-animation
    // summary re-localize themselves (batch 50).
    _performanceSettings.RefreshLocalization();
}


    private void OnSettingsChanged()
    {
        if (!DeskBox.Platform.UiDispatch.HasAccess)
        {
            DeskBox.Platform.UiDispatch.RunOrDefer(ApplySettingsSnapshot);
            return;
        }

        ApplySettingsSnapshot();
    }

    private void ApplySettingsSnapshot()
    {
        var settings = _settingsService.Settings;
        bool wasRestoringDefaults = _isRestoringDefaults;

        _isApplyingSettingsSnapshot = true;
        _isRestoringDefaults = true;
        try
        {
            SelectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);
            AutoCheckForUpdates = settings.AutoCheckForUpdates;
            SilentStartup = settings.SilentStartup;
            ShowHoverButtons = settings.ShowHoverButtons;
            ApplyHoverButtonActionSelection(settings.WidgetHoverButtonActions);

            // The file-stack section (including its custom-rule collection)
            // and the file-widget overview's folder-open combo live on their
            // section editors now: re-project from the coordinator snapshots
            // instead of assigning shell facade properties.
            _fileStackSettings.SyncPresentation();
            _featureWidgetsSettings.SyncPresentation();

            // The Quick Capture section's whole presentation lives on its
            // editor (batch 46): re-project from the coordinator snapshots.
            _quickCaptureSettingsEditor.SyncPresentation();
            // The performance section's whole presentation (incl. the three
            // working-set trim switches) and the General section's
            // attachment-storage combo live on their editors now (batch 50):
            // re-project from the coordinator read snapshots.
            _performanceSettings.SyncPresentation();

            // The Todo section's whole presentation lives on its editor
            // (batch 47); the Weather section's whole presentation lives on
            // its editor (batch 48): re-project from the coordinator
            // snapshots instead of assigning shell facade properties.
            _todoSettings.Refresh();
            _weatherSettings.Refresh();

            // Appearance presentation (material, density, window chrome, animation,
            // foreground, tray icon style) lives on the appearance editor now;
            // the shell only re-projects the selections whose state machines
            // stay here (theme, accent mode/effective color, group-nav).
            _appearanceSettings.SyncPresentation();
            PushAppearanceThemeSelection();
            UseSystemAccentColor = !string.Equals(
                settings.AccentColorMode,
                ThemeService.AccentModeCustom,
                StringComparison.OrdinalIgnoreCase);
            PushAppearanceAccentPresentation();

            // The group-navigation defaults and the capsule family's whole
            // presentation live on their section editors now: re-project them
            // from the coordinator snapshots instead of assigning shell
            // facade properties.
            _groupNavigationSettings.SyncPresentation();
            _capsuleSettings.SyncPresentation();


            // Music presentation lives on the section editor now: refresh the
            // editor projection instead of assigning shell facade properties.
            _musicSettings.SyncPresentation();

            // Interaction presentation (layer mode, snap enable/spacing,
            // open method, show-desktop behavior) lives on the section
            // editor now; the pushed hover summary follows the flyout state
            // re-projection above.
            _interactionSettings.SyncPresentation();
            _interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());

            // The file-display and managed-storage section presentation
            // (batch 48 note: the weather snapshot block that used to live
            // here moved into the weather editor's Refresh above), the
            // file-display and managed-storage editors' presentation live on
            // their section editors now: refresh the editor projections
            // instead of assigning shell facade properties. The root-path
            // working state below still feeds the shell's picker /
            // migration / quick-access chains.
            _fileDisplaySettings.SyncPresentation();
            _managedStorageSettings.SyncPresentation();

            ManagedStorageRootPath = SettingsService.NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);
            _backupSettings.RefreshState();
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
            _isRestoringDefaults = wasRestoringDefaults;
        }

        RefreshSelectionProperties(refreshLocalizedOptions: false);
        RefreshGlobalHotkeyState();
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        NotifyCapsuleOverridePropertiesChanged();
        RefreshQuickCaptureClipboardDiagnostics();
        _ = RefreshQuickAccessStateAsync();
    }

    private void RefreshLocalizedProperties()
    {
        RefreshSelectionProperties(refreshLocalizedOptions: true);
        OnPropertyChanged(nameof(DistributionChannelText));
        OnPropertyChanged(nameof(OfficialWebsiteDisplayText));
        OnPropertyChanged(nameof(OpenSourceRepositoryDisplayText));
        OnPropertyChanged(nameof(UpdateDownloadActionText));
        OnPropertyChanged(nameof(StoreSupportCardVisibility));
        if (!IsCheckingForUpdates && !IsDownloadingUpdate)
        {
            if (_appUpdateService.LastCheckResult is not null)
            {
                ApplyCachedUpdateResult();
            }
            else
            {
                UpdateStatusText = _localizationService.T("Settings.Update.Status.Ready");
                UpdateDetailText = GetReadyUpdateDetailText();
            }
        }
        OnPropertyChanged(nameof(AutoStartStatusText));
        OnPropertyChanged(nameof(AvailableAutoStartModeOptions));
        // The global-hotkey card text lives on the interaction editor now;
        // the shell refreshes it through the editor push instead of shell
        // property notifications.
        // The drag-drop diagnostic texts re-localize through the editor
        // push (batch 49), not through shell property notifications.
        PushDragDropDiagnosticProjection();
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        NotifyCapsuleOverridePropertiesChanged();
        // The group-navigation editor rebuilds its option tables itself; the
        // existing-groups projection rebuild follows.
        RefreshWidgetGroupSettings();
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshSelectionProperties(bool refreshLocalizedOptions)
    {
        // Replacing localized option arrays during an ordinary settings sync makes
        // WinUI reset every bound ComboBox.SelectedIndex to -1.
        if (refreshLocalizedOptions)
        {
            // The file-stack option tables, rule priorities, preview texts
            // and the overview summary re-project in the new language
            // through the section editor (batch 45).
            _fileStackSettings.RefreshLocalization();
            _featureWidgetsSettings.RefreshLocalization();
            // The backup family's option tables and status lines live on the
            // backup editor now (batch 49); its localization refresh clears
            // the display-name caches and re-projects the pushed texts.
            _backupSettings.RefreshLocalization();
            _cachedLanguageDisplayNames = null;
            OnPropertyChanged(nameof(AvailableLanguageDisplayNames));
            NotifySelectionOptionsChanged();
        }

        OnPropertyChanged(nameof(SelectedLanguageText));
        NotifyHoverButtonActionPropertiesChanged();
        _interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());
    }
}
