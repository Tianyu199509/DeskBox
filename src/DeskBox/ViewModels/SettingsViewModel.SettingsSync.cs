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
    }

    private void OnSettingsChanged()
    {
        if (App.UiDispatcherQueue is { } dispatcherQueue && !dispatcherQueue.HasThreadAccess)
        {
            dispatcherQueue.TryEnqueue(OnSettingsChanged);
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
            SelectedTheme = settings.Theme is ThemeLight or ThemeDark ? settings.Theme : ThemeSystem;
            SelectedTrayIconStyle = settings.TrayIconStyle is TrayIconStyleColorful or TrayIconStyleBlack or TrayIconStyleWhite
                ? settings.TrayIconStyle
                : TrayIconStyleSystem;
            SelectedLanguage = LocalizationService.NormalizeLanguageSetting(settings.Language);
            UseSystemAccentColor = !string.Equals(
                settings.AccentColorMode,
                ThemeService.AccentModeCustom,
                StringComparison.OrdinalIgnoreCase);

            AutoCheckForUpdates = settings.AutoCheckForUpdates;
            DoubleClickToOpen = settings.DoubleClickToOpen;
            DefaultWidth = settings.DefaultWidgetWidth;
            DefaultHeight = settings.DefaultWidgetHeight;
            HideShortcutArrowOverlay = settings.HideShortcutArrowOverlay;
            ShowImageFilesAsIcons = settings.ShowImageFilesAsIcons;
            ShowHoverButtons = settings.ShowHoverButtons;
            ResizeSnapEnabled = settings.ResizeSnapEnabled;
            ShowListItemDetails = settings.ShowListItemDetails;
            ShowFileItemPathTooltips = settings.ShowFileItemPathTooltips;
            ApplyHoverButtonActionSelection(settings.WidgetHoverButtonActions);

            WidgetOpacity = settings.WidgetOpacity;
            WidgetMaterialIntensity = settings.WidgetMaterialIntensity;
            SelectedWidgetCornerPreference = settings.WidgetCornerPreference is CornerDefault or CornerSquare or CornerSmall or CornerRound
                ? settings.WidgetCornerPreference
                : CornerSmall;
            SelectedWidgetMaterialType = settings.WidgetMaterialType is MaterialMica or MaterialMicaAlt or MaterialAcrylic or MaterialAcrylicBase or MaterialSolid
                ? settings.WidgetMaterialType
                : MaterialAcrylic;
            SelectedWidgetBorderColorMode = settings.WidgetBorderColorMode is BorderColorNeutral or BorderColorAccent or BorderColorNone
                ? settings.WidgetBorderColorMode
                : BorderColorNeutral;
            SelectedWidgetBorderStyle = settings.WidgetBorderStyle is BorderThin or BorderMedium or BorderThick
                ? settings.WidgetBorderStyle
                : BorderThin;

            WidgetCapsuleModeEnabled = settings.WidgetCapsuleModeEnabled;
            SelectedWidgetCompactWidthMode = SettingsService.NormalizeWidgetCompactWidthMode(
                settings.WidgetCompactWidthMode);
            SelectedWidgetCapsuleArrangementMode = SettingsService.NormalizeWidgetCapsuleArrangementMode(
                settings.WidgetCapsuleArrangementMode);
            WidgetCapsuleBarSpacing = SettingsService.NormalizeWidgetCapsuleBarSpacing(
                settings.WidgetCapsuleBarSpacing);
            SelectedWidgetCapsuleBarPlacement = SettingsService.NormalizeWidgetCapsuleBarPlacement(
                settings.WidgetCapsuleBarPlacement);
            SelectedWidgetCapsuleBarDirection = SettingsService.NormalizeWidgetCapsuleBarDirection(
                settings.WidgetCapsuleBarDirection);
            WidgetCompactHideSensitiveContent = settings.WidgetCompactHideSensitiveContent;
            SelectedWidgetCollapseBehavior = SettingsService.NormalizeWidgetCollapseBehavior(settings.WidgetCollapseBehavior) == SettingsService.WidgetCollapseBehaviorSmart
                ? SettingsService.WidgetCollapseBehaviorSmart
                : SettingsService.WidgetCollapseBehaviorClick;
            SelectedWidgetCompactContentMode = SettingsService.NormalizeWidgetCompactContentMode(settings.WidgetCompactContentMode);
            SelectedWidgetCompactAnimationEffect = SettingsService.NormalizeWidgetCompactAnimationEffect(settings.WidgetCompactAnimationEffect);
            WidgetCompactAnimationDurationMs = SettingsService.NormalizeWidgetCompactAnimationDurationMs(settings.WidgetCompactAnimationDurationMs);
            WidgetCompactExpandDelayMs = SettingsService.NormalizeWidgetCompactExpandDelayMs(settings.WidgetCompactExpandDelayMs);
            WidgetCompactCollapseDelayMs = SettingsService.NormalizeWidgetCompactCollapseDelayMs(settings.WidgetCompactCollapseDelayMs);
            SelectedWidgetCompactHoverResponse = SettingsService.ResolveWidgetCompactHoverResponse(
                settings.WidgetCompactExpandDelayMs,
                settings.WidgetCompactCollapseDelayMs);
            SelectedWidgetCompactMediaCornerMode = SettingsService.NormalizeWidgetCompactMediaCornerMode(settings.WidgetCompactMediaCornerMode);

            SelectedWidgetAnimationEffect = NormalizeWidgetAnimationEffect(settings.WidgetAnimationEffect);
            SelectedWidgetAnimationSpeed = NormalizeWidgetAnimationSpeed(settings.WidgetAnimationSpeed);
            SelectedWidgetAnimationSlideDirection = NormalizeWidgetAnimationSlideDirection(settings.WidgetAnimationSlideDirection);
            SelectedWidgetAnimationEasingIntensity = NormalizeWidgetAnimationEasingIntensity(settings.WidgetAnimationEasingIntensity);
            SelectedAnimationPreset = ResolveAnimationPreset();
            SelectedDisplayWidgetChromeMode = NormalizeWidgetChromeModeSetting(
                settings.DisplayWidgetChromeMode,
                WidgetChromeMode.Overlay);
            SelectedInteractiveWidgetChromeMode = NormalizeWidgetChromeModeSetting(
                settings.InteractiveWidgetChromeMode,
                WidgetChromeMode.Standard);
            SelectedWidgetTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
            SelectedWidgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);

            IconSize = settings.IconSize;
            TextSize = settings.TextSize;
            LayoutDensityScale = settings.LayoutDensityScale;
            HorizontalSpacingScale = settings.HorizontalSpacingScale;
            VerticalSpacingScale = settings.VerticalSpacingScale;
            FileNameWidthScale = settings.FileNameWidthScale;
            SelectedLayoutDensity = SettingsService.ResolveLayoutDensityPreset(settings);
            ShowFileExtensions = settings.ShowFileExtensions;
            HideShortcutExtensionWhenShowingFileExtensions = settings.HideShortcutExtensionWhenShowingFileExtensions;

            ApplyContentEditorSettingsSnapshot(settings);
            ApplyFileStackSettingsSnapshot(settings);

            QuickCaptureEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture);
            QuickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
            QuickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
            QuickCaptureRecentLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
            QuickCaptureShowCreatedTime = settings.QuickCaptureShowCreatedTime;
            SelectedAttachmentStorageMode = SettingsService.NormalizeAttachmentStorageMode(settings.AttachmentStorageMode);
            SelectedManagedDropAction = settings.ManagedDropAction == SettingsService.ManagedDropActionMove
                ? SettingsService.ManagedDropActionMove
                : SettingsService.ManagedDropActionCopy;
            SelectedQuickCaptureDefaultView = NormalizeQuickCaptureDefaultView(settings.QuickCaptureDefaultView);
            SelectedQuickCaptureTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
            QuickCaptureShowTabBar = settings.QuickCaptureShowTabBar;
            QuickCaptureShowRecordsTab = settings.QuickCaptureShowRecordsTab;
            QuickCaptureShowPinnedTab = settings.QuickCaptureShowPinnedTab;
            QuickCaptureShowRecentTab = settings.QuickCaptureShowRecentTab;

            TodoEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
            TodoShowTabBar = settings.TodoShowTabBar;
            TodoShowAllTab = settings.TodoShowAllTab;
            TodoShowActiveTab = settings.TodoShowActiveTab;
            TodoShowTodayTab = settings.TodoShowTodayTab;
            TodoShowThisWeekTab = settings.TodoShowThisWeekTab;
            TodoShowThisMonthTab = settings.TodoShowThisMonthTab;
            TodoShowImportantTab = settings.TodoShowImportantTab;
            TodoShowCompletedTab = settings.TodoShowCompletedTab;
            TodoShowCompletedTasks = settings.TodoShowCompletedTasks;
            TodoShowFooterStats = settings.TodoShowFooterStats;
            TodoShowClearCompletedButton = settings.TodoShowClearCompletedButton;
            TodoConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;
            TodoReminderEnabled = settings.TodoReminderEnabled;
            SelectedTodoNewTaskPosition = NormalizeTodoNewTaskPosition(settings.TodoNewTaskPosition);
            SelectedTodoDefaultFilter = NormalizeTodoDefaultFilter(settings.TodoDefaultFilter);
            SelectedTodoTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.TodoTabStyle);
            SelectedTodoReminderOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(
                settings.TodoDefaultReminderOffsetMinutes);

            MusicUseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
            MusicEnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
            SelectedMusicDisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.MusicDisplayMode);

            WeatherAutoLocation = settings.WeatherAutoLocation;
            WeatherCityName = settings.WeatherCityName;
            WeatherCitySearchText = settings.WeatherCityName;
            SelectedWeatherTemperatureUnit = settings.WeatherTemperatureUnit == SettingsService.WeatherTemperatureUnitFahrenheit
                ? SettingsService.WeatherTemperatureUnitFahrenheit
                : SettingsService.WeatherTemperatureUnitCelsius;
            SelectedWeatherWindSpeedUnit = settings.WeatherWindSpeedUnit is SettingsService.WeatherWindSpeedUnitMs or SettingsService.WeatherWindSpeedUnitMph
                ? settings.WeatherWindSpeedUnit
                : SettingsService.WeatherWindSpeedUnitKmh;
            SelectedWeatherDefaultView = settings.WeatherDefaultView == SettingsService.WeatherDefaultViewWeek
                ? SettingsService.WeatherDefaultViewWeek
                : SettingsService.WeatherDefaultViewToday;
            SelectedWeatherSkin = settings.WeatherSkin == SettingsService.WeatherSkinRich
                ? SettingsService.WeatherSkinRich
                : SettingsService.WeatherSkinStandard;
            WeatherShowForecast = settings.WeatherShowForecast;
            WeatherShowSunrise = settings.WeatherShowSunrise;
            WeatherShowUvIndex = settings.WeatherShowUvIndex;
            WeatherShowPrecipitation = settings.WeatherShowPrecipitation;
            WeatherShowHumidity = settings.WeatherShowHumidity;
            WeatherShowWind = settings.WeatherShowWind;
            WeatherShowPressure = settings.WeatherShowPressure;
            SelectedWeatherRefreshInterval = Math.Clamp(
                settings.WeatherRefreshIntervalMinutes,
                SettingsService.WeatherRefreshMinMinutes,
                SettingsService.WeatherRefreshMaxMinutes);

            ManagedStorageRootPath = SettingsService.NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);
            GlobalHotkeyEnabled = settings.GlobalHotkeyEnabled;
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
            _isRestoringDefaults = wasRestoringDefaults;
        }

        RefreshNumberInputs();
        RefreshSelectionProperties(refreshLocalizedOptions: false);
        RefreshGlobalHotkeyState();
        OnPropertyChanged(nameof(CanEditCustomAccent));
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(WeatherCityNameVisibility));
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        NotifyCapsuleOverridePropertiesChanged();
        RefreshQuickCaptureClipboardDiagnostics();
        _ = RefreshQuickAccessStateAsync();
    }

    private void RefreshLocalizedProperties()
    {
        RefreshSelectionProperties(refreshLocalizedOptions: true);
        OnPropertyChanged(nameof(AccentColorDescription));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(DistributionChannelText));
        OnPropertyChanged(nameof(AboutDeveloperText));
        OnPropertyChanged(nameof(OfficialWebsiteDisplayText));
        OnPropertyChanged(nameof(OpenSourceRepositoryDisplayText));
        OnPropertyChanged(nameof(UpdateDownloadActionText));
        OnPropertyChanged(nameof(DonationCardVisibility));
        OnPropertyChanged(nameof(DonationWechatImageSource));
        OnPropertyChanged(nameof(DonationAlipayImageSource));
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
        OnPropertyChanged(nameof(QuickAccessStatusText));
        OnPropertyChanged(nameof(PinQuickAccessButtonText));
        OnPropertyChanged(nameof(PinQuickAccessToolTipText));
        OnPropertyChanged(nameof(GlobalHotkeyDescription));
        OnPropertyChanged(nameof(GlobalHotkeyText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusText));
        OnPropertyChanged(nameof(GlobalHotkeyStatusKind));
        OnPropertyChanged(nameof(CanShowGlobalHotkeyWarning));
        NotifyDragDropPermissionPropertiesChanged();
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        NotifyCapsuleOverridePropertiesChanged();
OnPropertyChanged(nameof(WeatherCitySearchPlaceholder));
OnPropertyChanged(nameof(WeatherCityNoResultsText));
RefreshWeatherCityPopularCities();
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshSelectionProperties(bool refreshLocalizedOptions)
    {
        // Replacing localized option arrays during an ordinary settings sync makes
        // WinUI reset every bound ComboBox.SelectedIndex to -1.
        if (refreshLocalizedOptions)
        {
            RefreshFileStackSelectionProperties();
            _cachedThemeDisplayNames = null;
            _cachedTrayIconStyleDisplayNames = null;
            _cachedLanguageDisplayNames = null;
            _cachedWidgetCornerPreferenceDisplayNames = null;
            _cachedWidgetMaterialTypeDisplayNames = null;
            _cachedWidgetBorderColorModeDisplayNames = null;
            _cachedWidgetBorderStyleDisplayNames = null;
            _cachedWidgetCollapseBehaviorDisplayNames = null;
            _cachedWidgetCompactContentModeDisplayNames = null;
            _cachedWidgetCompactWidthModeDisplayNames = null;
            _cachedWidgetCapsuleArrangementDisplayNames = null;
            _cachedWidgetCapsuleBarPlacementDisplayNames = null;
            _cachedWidgetCapsuleBarDirectionDisplayNames = null;
            _cachedWidgetCompactAnimationEffectDisplayNames = null;
            _cachedWidgetCompactHoverResponseDisplayNames = null;
            _cachedWidgetCompactMediaCornerDisplayNames = null;
            _cachedLayoutDensityDisplayNames = null;
            _cachedAnimationPresetDisplayNames = null;
            _cachedWidgetAnimationEffectDisplayNames = null;
            _cachedWidgetAnimationSpeedDisplayNames = null;
            _cachedWidgetAnimationSlideDirectionDisplayNames = null;
            _cachedWidgetAnimationEasingIntensityDisplayNames = null;
            _cachedDisplayWidgetChromeModeDisplayNames = null;
            _cachedInteractiveWidgetChromeModeDisplayNames = null;
            _cachedWidgetTitleIconModeDisplayNames = null;
            _cachedWidgetLayerModeDisplayNames = null;
            _cachedQuickCaptureDefaultViewDisplayNames = null;
            _cachedQuickCaptureTabStyleDisplayNames = null;
            _cachedTodoNewTaskPositionDisplayNames = null;
            _cachedAttachmentStorageModeDisplayNames = null;
            _cachedTodoDefaultFilterDisplayNames = null;
            _cachedTodoTabStyleDisplayNames = null;
            _cachedTodoReminderOffsetDisplayNames = null;
            _cachedMusicDisplayModeDisplayNames = null;
            _cachedWeatherTempUnitDisplayNames = null;
            _cachedWeatherWindUnitDisplayNames = null;
            _cachedWeatherDefaultViewDisplayNames = null;
            _cachedWeatherSkinDisplayNames = null;
            _cachedWeatherRefreshIntervalDisplayNames = null;
            _cachedManagedDropActionDisplayNames = null;
            OnPropertyChanged(nameof(AvailableThemeDisplayNames));
            OnPropertyChanged(nameof(AvailableTrayIconStyleDisplayNames));
            OnPropertyChanged(nameof(AvailableLanguageDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCornerPreferenceDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetMaterialTypeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetBorderColorModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetBorderStyleDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCollapseBehaviorDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCompactWidthModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCompactContentModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCapsuleArrangementDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCapsuleBarPlacementDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCapsuleBarDirectionDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCompactAnimationEffectDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCompactHoverResponseDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetCompactMediaCornerDisplayNames));
            OnPropertyChanged(nameof(AvailableLayoutDensityDisplayNames));
            OnPropertyChanged(nameof(AvailableAnimationPresetDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetAnimationEffectDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetAnimationSpeedDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetAnimationSlideDirectionDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetAnimationEasingIntensityDisplayNames));
            OnPropertyChanged(nameof(AvailableDisplayWidgetChromeModeDisplayNames));
            OnPropertyChanged(nameof(AvailableInteractiveWidgetChromeModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetTitleIconModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWidgetLayerModeDisplayNames));
            OnPropertyChanged(nameof(AvailableQuickCaptureDefaultViewDisplayNames));
            OnPropertyChanged(nameof(AvailableQuickCaptureTabStyleDisplayNames));
            OnPropertyChanged(nameof(AvailableTodoNewTaskPositionDisplayNames));
            OnPropertyChanged(nameof(AvailableAttachmentStorageModeDisplayNames));
            OnPropertyChanged(nameof(AvailableManagedDropActionDisplayNames));
            OnPropertyChanged(nameof(AvailableTodoDefaultFilterDisplayNames));
            OnPropertyChanged(nameof(AvailableTodoTabStyleDisplayNames));
            OnPropertyChanged(nameof(AvailableTodoReminderOffsetDisplayNames));
            OnPropertyChanged(nameof(AvailableMusicDisplayModeDisplayNames));
            OnPropertyChanged(nameof(AvailableWeatherTemperatureUnitDisplayNames));
            OnPropertyChanged(nameof(AvailableWeatherWindSpeedUnitDisplayNames));
            OnPropertyChanged(nameof(AvailableWeatherDefaultViewDisplayNames));
            OnPropertyChanged(nameof(AvailableWeatherSkinDisplayNames));
            OnPropertyChanged(nameof(AvailableWeatherRefreshIntervalDisplayNames));
            RefreshContentEditorLocalizedProperties();
            NotifySelectionOptionsChanged();
        }

        OnPropertyChanged(nameof(IsOpacitySliderEnabled));
        OnPropertyChanged(nameof(WidgetOpacityVisibility));
        OnPropertyChanged(nameof(MaterialIntensityVisibility));
        OnPropertyChanged(nameof(IsWidgetBorderStyleEnabled));
        OnPropertyChanged(nameof(SelectedThemeText));
        OnPropertyChanged(nameof(SelectedTrayIconStyleText));
        OnPropertyChanged(nameof(SelectedLanguageText));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceText));
        OnPropertyChanged(nameof(SelectedWidgetMaterialTypeText));
        OnPropertyChanged(nameof(SelectedWidgetBorderColorModeText));
        OnPropertyChanged(nameof(SelectedWidgetBorderStyleText));
        OnPropertyChanged(nameof(SelectedWidgetCollapseBehaviorText));
        OnPropertyChanged(nameof(SelectedWidgetCompactWidthModeText));
        OnPropertyChanged(nameof(IsSmartWidgetCollapseBehavior));
        OnPropertyChanged(nameof(IsSmartWidgetCollapseBehaviorSelected));
        OnPropertyChanged(nameof(CapsuleHoverResponseEntryVisibility));
        OnPropertyChanged(nameof(CanOpenWidgetCompactHoverResponseDetails));
        OnPropertyChanged(nameof(CanOpenWidgetCompactAnimationDetails));
        OnPropertyChanged(nameof(SelectedWidgetCapsuleArrangementText));
        OnPropertyChanged(nameof(IsWidgetCapsuleBarSelected));
        OnPropertyChanged(nameof(IsWidgetCapsuleBarEnabled));
        OnPropertyChanged(nameof(IsWidgetCapsuleBarSpacingEnabled));
        OnPropertyChanged(nameof(CapsuleArrangementEntryVisibility));
        OnPropertyChanged(nameof(WidgetCapsuleBarSpacingText));
        OnPropertyChanged(nameof(SelectedWidgetCapsuleBarPlacementText));
        OnPropertyChanged(nameof(SelectedWidgetCapsuleBarDirectionText));
        OnPropertyChanged(nameof(CapsuleArrangementDetailsSummaryText));
        OnPropertyChanged(nameof(SelectedWidgetCompactContentModeText));
        OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectText));
        OnPropertyChanged(nameof(IsWidgetCompactAnimationCustom));
        OnPropertyChanged(nameof(WidgetCompactAnimationCustomVisibility));
        OnPropertyChanged(nameof(SelectedWidgetCompactHoverResponseText));
        OnPropertyChanged(nameof(IsWidgetCompactHoverResponseCustom));
        OnPropertyChanged(nameof(WidgetCompactHoverResponseCustomVisibility));
        OnPropertyChanged(nameof(SelectedWidgetCompactMediaCornerText));
        OnPropertyChanged(nameof(SelectedLayoutDensityText));
        OnPropertyChanged(nameof(SelectedAnimationPresetText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
        OnPropertyChanged(nameof(IsDirectionEnabled));
        OnPropertyChanged(nameof(IsEasingEnabled));
        OnPropertyChanged(nameof(IsSpeedEnabled));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityText));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
        NotifyHoverButtonActionPropertiesChanged();
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        OnPropertyChanged(nameof(SelectedTodoTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
    }
}
