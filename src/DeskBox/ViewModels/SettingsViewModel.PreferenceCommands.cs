using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    // Host-side working state for the accent card. The XAML binding surface
    // lives on the appearance editor (section-level DataContext switch); the
    // shell keeps the accent-mode flag its theme-service write chain needs
    // and pushes the accent presentation onto the editor on every change.
    internal bool UseSystemAccentColor
    {
        get => _useSystemAccentColor;
        private set => SetProperty(ref _useSystemAccentColor, value);
    }

    public bool SuppressAppearanceNotifications { get; set; }
    public bool DeferAppearancePersistence { get; set; }

    public void CommitAppearanceChanges()
    {
        _settingsService.NotifyAppearancePreviewNow();
        // The preview notification has already updated every widget with the
        // final slider value. Persist without broadcasting the same appearance
        // pass a second time through SettingsChanged.
        _settingsService.SaveDebounced(notifySubscribers: false);
        _appActions.ScheduleLightMemoryCleanup();
    }

    public void SetCustomAccentColor(Color color)
    {
        _themeService.SetCustomAccentColor(color);

        if (UseSystemAccentColor)
        {
            UseSystemAccentColor = false;
        }

        RefreshAccentPreview();
    }

    public void UpdateManagedStorageRootPath(string path)
    {
        // The verified migration already committed both durable settings files.
        // This only refreshes presentation, without another settings write.
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(path);
        ManagedStorageRootPath = normalizedPath;
        _managedStorageSettings.UpdateRootPath(normalizedPath);
        _ = RefreshQuickAccessStateAsync(showBusy: true);
    }

    public async Task RestoreDefaultPreferencesAsync()
    {
        _isRestoringDefaults = true;
        SuppressAppearanceNotifications = true;
        DeferAppearancePersistence = false;

        try
        {
            SettingsService.ApplyDefaultPreferences(_settingsService.Settings);

            // The group-level defaults (nav style, title mode, wheel/hover
            // switch, switch animation, groups master switch) live in the
            // layout domain, which a snapshot commit strips as main-owned:
            // reset them through the dedicated command or the restore
            // silently kept the user's custom group presentation
            // (batch R9).
            _ = _widgetActions.ApplyGroupNavigationDefaults(new GroupNavigationDefaultsChange(
                NavigationStyle: WidgetGroupNavigationStyles.Tabs,
                TitleDisplayMode: WidgetGroupTitleDisplayModes.IconAndText,
                WheelSwitchEnabled: true,
                HoverSwitchEnabled: false,
                SwitchAnimationStyle: WidgetGroupSwitchAnimationStyles.Auto,
                GroupsEnabled: true));
            ApplySettingsSnapshot();
            IconHelper.ClearAllThumbnailCaches();

            // ApplySettingsSnapshot above already re-projected the
            // restored snap state onto the interaction editor.
            _appActions.SetSnapEnabled(_interactionSettings.SnapEnabled);

            _hotkeyActions.RefreshRegistration();
            // The reset flipped SearchHotkeyEnabled back to its default in
            // settings.json; re-run the live registration or the old
            // gesture kept triggering search until the next restart
            // (batch R9).
            _hotkeyActions.RefreshSearchHotkeyRegistration();
            _appActions.UpdateTrayIcon();
            RefreshGlobalHotkeyState();
            _themeService.RefreshAppearance();
            RefreshAccentPreview();
            await _settingsService.SaveAsync();
            _quickCaptureSettings.RefreshFromSettings();
            _settingsService.NotifyAppearancePreviewNow();
        }
        finally
        {
            SuppressAppearanceNotifications = false;
            _isRestoringDefaults = false;
        }
    }

}
