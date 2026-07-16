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
    public Color GetCurrentAccentColor() => _currentAccentColor;

    public bool SuppressAppearanceNotifications { get; set; }
    public bool DeferAppearancePersistence { get; set; }

    public void CommitAppearanceChanges()
    {
        _settingsService.NotifyAppearancePreviewNow();
        // The preview notification has already updated every widget with the
        // final slider value. Persist without broadcasting the same appearance
        // pass a second time through SettingsChanged.
        _settingsService.SaveDebounced(notifySubscribers: false);
        App.ScheduleLightMemoryCleanup();
    }

    public void SetCustomAccentColor(Color color)
    {
        _themeService.SetCustomAccentColor(color);

        if (UseSystemAccentColor)
        {
            _useSystemAccentColor = false;
            OnPropertyChanged(nameof(UseSystemAccentColor));
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
        }

        RefreshAccentPreview();
    }

    public void UpdateManagedStorageRootPath(string path)
    {
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(path);
        ManagedStorageRootPath = normalizedPath;
        _settingsService.Settings.DefaultManagedStorageRootPath = normalizedPath;
        _settingsService.SaveDebounced();
        _ = RefreshQuickAccessStateAsync(showBusy: true);
    }

    public async Task RestoreDefaultPreferencesAsync()
    {
        _isRestoringDefaults = true;
        _isApplyingSettingsSnapshot = true;
        SuppressAppearanceNotifications = true;
        DeferAppearancePersistence = false;

        try
        {
            SettingsService.ApplyDefaultPreferences(_settingsService.Settings);
            IconHelper.ClearAllThumbnailCaches();
            SelectedTheme = ThemeSystem;
            SelectedTrayIconStyle = TrayIconStyleColorful;
            UseSystemAccentColor = true;
            DefaultWidth = SettingsService.DefaultWidgetWidth;
            DefaultHeight = SettingsService.DefaultWidgetHeight;
            SelectedWidgetCornerPreference = CornerRound;
            SelectedWidgetMaterialType = MaterialMica;
            WidgetMaterialIntensity = SettingsService.DefaultWidgetMaterialIntensity;
            SelectedWidgetBorderColorMode = BorderColorNeutral;
            SelectedWidgetBorderStyle = BorderThin;
            SelectedWidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade;
            SelectedWidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedStandard;
            SelectedWidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionRight;
            SelectedWidgetAnimationEasingIntensity = SettingsService.WidgetAnimationEasingStandard;
            SelectedAnimationPreset = AnimationPresetStandard;
            SelectedDisplayWidgetChromeMode = SettingsService.WidgetChromeModeOverlay;
            SelectedInteractiveWidgetChromeMode = SettingsService.WidgetChromeModeStandard;
            SelectedWidgetTitleIconMode = SettingsService.WidgetTitleIconModeColor;
            SelectedWidgetLayerMode = SettingsService.WidgetLayerModeDynamic;
            WidgetOpacity = SettingsService.DefaultWidgetOpacity;
            IconSize = SettingsService.DefaultIconSize;
            TextSize = SettingsService.DefaultTextSize;
            LayoutDensityScale = SettingsService.DefaultLayoutDensityScale;
            HorizontalSpacingScale = SettingsService.DefaultHorizontalSpacingScale;
            VerticalSpacingScale = SettingsService.DefaultVerticalSpacingScale;
            FileNameWidthScale = SettingsService.DefaultFileNameWidthScale;
            SelectedLayoutDensity = SettingsService.LayoutDensityStandard;
            ShowFileExtensions = false;
            ShowImageFilesAsIcons = false;
            HideShortcutExtensionWhenShowingFileExtensions = true;
            ShowHoverButtons = true;
            ApplyHoverButtonActionSelection(SettingsService.DefaultWidgetHoverButtonActions);
            AutoCheckForUpdates = true;
            QuickCaptureClipboardEnabled = false;
            QuickCaptureImageClipboardEnabled = false;
            QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
            QuickCaptureShowCreatedTime = true;
            SelectedAttachmentStorageMode = SettingsService.AttachmentStorageModeLink;
            SelectedQuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
            SelectedQuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
            TodoShowCompletedTasks = true;
            TodoShowFooterStats = false;
            TodoShowClearCompletedButton = true;
            TodoConfirmBeforeDelete = false;
            TodoReminderEnabled = true;
            SelectedTodoReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
            MusicUseArtworkBackdrop = true;
            MusicEnableCoverHoverMotion = true;
            SelectedMusicDisplayMode = SettingsService.MusicDisplayModeAuto;
WeatherAutoLocation = true;
WeatherCityName = string.Empty;
SelectedWeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
SelectedWeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
SelectedWeatherDefaultView = SettingsService.WeatherDefaultViewToday;
SelectedWeatherSkin = SettingsService.WeatherSkinStandard;
WeatherShowForecast = true;
WeatherShowSunrise = true;
WeatherShowUvIndex = true;
WeatherShowPrecipitation = true;
WeatherShowHumidity = true;
WeatherShowWind = true;
WeatherShowPressure = false;
SelectedWeatherRefreshInterval = 60;
            SelectedTodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
            SelectedTodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
            SelectedTodoTabStyle = SettingsService.WidgetTabStyleButton;
            DoubleClickToOpen = true;
            ResizeSnapEnabled = true;
            GlobalHotkeyEnabled = SettingsService.DefaultGlobalHotkeyEnabled;
            HideShortcutArrowOverlay = true;
            ShowListItemDetails = false;
            ShowFileItemPathTooltips = true;

            ResetWidgetChromeOverrides(_settingsService.Settings, _widgetContentFactory, WidgetChromeCategory.Display);
            ResetWidgetChromeOverrides(_settingsService.Settings, _widgetContentFactory, WidgetChromeCategory.Interactive);
            App.Current?.ResizeGuideOverlay.IsSnapEnabled = true;

            RefreshAccentPreview();
            RefreshNumberInputs();
            RefreshSelectionProperties();
            OnPropertyChanged(nameof(CanEditCustomAccent));
            OnPropertyChanged(nameof(AccentColorDescription));
            App.Current?.GlobalHotkeyService?.RefreshRegistration();
            App.Current?.UpdateTrayIcon();
            RefreshGlobalHotkeyState();
            RefreshLocalizedProperties();
            _themeService.RefreshAppearance();
            await _settingsService.SaveAsync();
            App.Current?.QuickCaptureClipboardService?.Refresh();
            _settingsService.NotifyAppearancePreviewNow();
        }
        finally
        {
            SuppressAppearanceNotifications = false;
            _isApplyingSettingsSnapshot = false;
            _isRestoringDefaults = false;
        }
    }
}
