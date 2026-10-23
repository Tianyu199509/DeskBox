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
            ApplySettingsSnapshot();
            IconHelper.ClearAllThumbnailCaches();

            // ApplySettingsSnapshot above already re-projected the
            // restored snap state onto the interaction editor.
            _appActions.SetSnapEnabled(_interactionSettings.SnapEnabled);

            _hotkeyActions.RefreshRegistration();
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
