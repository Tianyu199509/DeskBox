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
        bool quickCaptureEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.QuickCapture);
        bool quickCaptureClipboardEnabled = settings.QuickCaptureClipboardEnabled;
        bool quickCaptureImageClipboardEnabled = settings.QuickCaptureImageClipboardEnabled;
        int quickCaptureRecentLimit = QuickCaptureService.NormalizeRecentLimit(settings.QuickCaptureRecentLimit);
        bool quickCaptureShowCreatedTime = settings.QuickCaptureShowCreatedTime;
        string quickCaptureDefaultView = NormalizeQuickCaptureDefaultView(settings.QuickCaptureDefaultView);
        string quickCaptureTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.QuickCaptureTabStyle);
        bool todoEnabled = FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Todo);
        bool todoShowCompletedTasks = settings.TodoShowCompletedTasks;
        bool todoShowFooterStats = settings.TodoShowFooterStats;
        bool todoShowClearCompletedButton = settings.TodoShowClearCompletedButton;
        bool todoConfirmBeforeDelete = settings.TodoConfirmBeforeDelete;
        bool todoReminderEnabled = settings.TodoReminderEnabled;
        bool musicUseArtworkBackdrop = settings.MusicUseArtworkBackdrop;
        bool musicEnableCoverHoverMotion = settings.MusicEnableCoverHoverMotion;
        string musicDisplayMode = SettingsService.NormalizeMusicDisplayMode(settings.MusicDisplayMode);
        bool showImageFilesAsIcons = settings.ShowImageFilesAsIcons;
        bool showFileItemPathTooltips = settings.ShowFileItemPathTooltips;
        bool showHoverButtons = settings.ShowHoverButtons;
        string hoverButtonActions = SettingsService.NormalizeWidgetHoverButtonActions(settings.WidgetHoverButtonActions);
        bool autoCheckForUpdates = settings.AutoCheckForUpdates;
        string displayWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.DisplayWidgetChromeMode, WidgetChromeMode.Overlay);
        string interactiveWidgetChromeMode = NormalizeWidgetChromeModeSetting(settings.InteractiveWidgetChromeMode, WidgetChromeMode.Standard);
        string widgetTitleIconMode = NormalizeWidgetTitleIconModeSetting(settings.WidgetTitleIconMode);
        string widgetLayerMode = SettingsService.NormalizeWidgetLayerModeSetting(settings.WidgetLayerMode);
        bool widgetCapsuleModeEnabled = settings.WidgetCapsuleModeEnabled;
        string widgetCollapseBehavior = SettingsService.NormalizeWidgetCollapseBehavior(settings.WidgetCollapseBehavior) == SettingsService.WidgetCollapseBehaviorSmart
            ? SettingsService.WidgetCollapseBehaviorSmart
            : SettingsService.WidgetCollapseBehaviorClick;
        string widgetCollapsedStyle = SettingsService.NormalizeWidgetCollapsedStyle(settings.WidgetCollapsedStyle);
        string widgetCompactAnimationEffect = SettingsService.NormalizeWidgetCompactAnimationEffect(settings.WidgetCompactAnimationEffect);
        int widgetCompactAnimationDurationMs = SettingsService.NormalizeWidgetCompactAnimationDurationMs(settings.WidgetCompactAnimationDurationMs);
        int widgetCompactExpandDelayMs = SettingsService.NormalizeWidgetCompactExpandDelayMs(settings.WidgetCompactExpandDelayMs);
        int widgetCompactCollapseDelayMs = SettingsService.NormalizeWidgetCompactCollapseDelayMs(settings.WidgetCompactCollapseDelayMs);
        string widgetCompactMediaCornerMode = SettingsService.NormalizeWidgetCompactMediaCornerMode(settings.WidgetCompactMediaCornerMode);
        string todoNewTaskPosition = NormalizeTodoNewTaskPosition(settings.TodoNewTaskPosition);
        string todoDefaultFilter = NormalizeTodoDefaultFilter(settings.TodoDefaultFilter);
        string todoTabStyle = SettingsService.NormalizeWidgetTabStyle(settings.TodoTabStyle);
        int todoReminderOffsetMinutes = SettingsService.NormalizeTodoReminderOffsetMinutes(settings.TodoDefaultReminderOffsetMinutes);
        string managedStorageRootPath = SettingsService.NormalizeManagedStorageRootPath(settings.DefaultManagedStorageRootPath);

        _isApplyingSettingsSnapshot = true;
        try
        {
            if (WidgetCapsuleModeEnabled != widgetCapsuleModeEnabled)
            {
                WidgetCapsuleModeEnabled = widgetCapsuleModeEnabled;
            }

            if (!string.Equals(ManagedStorageRootPath, managedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageRootPath = managedStorageRootPath;
                _ = RefreshQuickAccessStateAsync();
            }

            if (QuickCaptureEnabled != quickCaptureEnabled)
            {
                QuickCaptureEnabled = quickCaptureEnabled;
            }

            if (QuickCaptureClipboardEnabled != quickCaptureClipboardEnabled)
            {
                QuickCaptureClipboardEnabled = quickCaptureClipboardEnabled;
            }

            if (QuickCaptureImageClipboardEnabled != quickCaptureImageClipboardEnabled)
            {
                QuickCaptureImageClipboardEnabled = quickCaptureImageClipboardEnabled;
            }

            if (QuickCaptureRecentLimit != quickCaptureRecentLimit)
            {
                QuickCaptureRecentLimit = quickCaptureRecentLimit;
            }

            if (QuickCaptureShowCreatedTime != quickCaptureShowCreatedTime)
            {
                QuickCaptureShowCreatedTime = quickCaptureShowCreatedTime;
            }

            if (!string.Equals(SelectedQuickCaptureDefaultView, quickCaptureDefaultView, StringComparison.Ordinal))
            {
                SelectedQuickCaptureDefaultView = quickCaptureDefaultView;
            }

            if (!string.Equals(SelectedQuickCaptureTabStyle, quickCaptureTabStyle, StringComparison.Ordinal))
            {
                SelectedQuickCaptureTabStyle = quickCaptureTabStyle;
            }

            if (TodoEnabled != todoEnabled)
            {
                TodoEnabled = todoEnabled;
            }

            if (TodoShowCompletedTasks != todoShowCompletedTasks)
            {
                TodoShowCompletedTasks = todoShowCompletedTasks;
            }

            if (TodoShowFooterStats != todoShowFooterStats)
            {
                TodoShowFooterStats = todoShowFooterStats;
            }

            if (TodoShowClearCompletedButton != todoShowClearCompletedButton)
            {
                TodoShowClearCompletedButton = todoShowClearCompletedButton;
            }

            if (TodoConfirmBeforeDelete != todoConfirmBeforeDelete)
            {
                TodoConfirmBeforeDelete = todoConfirmBeforeDelete;
            }

            if (TodoReminderEnabled != todoReminderEnabled)
            {
                TodoReminderEnabled = todoReminderEnabled;
            }

            if (MusicUseArtworkBackdrop != musicUseArtworkBackdrop)
            {
                MusicUseArtworkBackdrop = musicUseArtworkBackdrop;
            }

            if (MusicEnableCoverHoverMotion != musicEnableCoverHoverMotion)
            {
                MusicEnableCoverHoverMotion = musicEnableCoverHoverMotion;
            }

            if (!string.Equals(SelectedMusicDisplayMode, musicDisplayMode, StringComparison.Ordinal))
            {
                SelectedMusicDisplayMode = musicDisplayMode;
            }

            if (ShowImageFilesAsIcons != showImageFilesAsIcons)
            {
                ShowImageFilesAsIcons = showImageFilesAsIcons;
            }

            if (ShowFileItemPathTooltips != showFileItemPathTooltips)
            {
                ShowFileItemPathTooltips = showFileItemPathTooltips;
            }

            if (ShowHoverButtons != showHoverButtons)
            {
                ShowHoverButtons = showHoverButtons;
            }

            ApplyHoverButtonActionSelection(hoverButtonActions);

            if (AutoCheckForUpdates != autoCheckForUpdates)
            {
                AutoCheckForUpdates = autoCheckForUpdates;
            }

            if (!string.Equals(SelectedDisplayWidgetChromeMode, displayWidgetChromeMode, StringComparison.Ordinal))
            {
                SelectedDisplayWidgetChromeMode = displayWidgetChromeMode;
            }

            if (!string.Equals(SelectedInteractiveWidgetChromeMode, interactiveWidgetChromeMode, StringComparison.Ordinal))
            {
                SelectedInteractiveWidgetChromeMode = interactiveWidgetChromeMode;
            }

            if (!string.Equals(SelectedWidgetTitleIconMode, widgetTitleIconMode, StringComparison.Ordinal))
            {
                SelectedWidgetTitleIconMode = widgetTitleIconMode;
            }

            if (!string.Equals(SelectedWidgetLayerMode, widgetLayerMode, StringComparison.Ordinal))
            {
                SelectedWidgetLayerMode = widgetLayerMode;
            }

            if (!string.Equals(SelectedWidgetCollapseBehavior, widgetCollapseBehavior, StringComparison.Ordinal))
            {
                SelectedWidgetCollapseBehavior = widgetCollapseBehavior;
            }

            if (!string.Equals(SelectedWidgetCollapsedStyle, widgetCollapsedStyle, StringComparison.Ordinal))
            {
                SelectedWidgetCollapsedStyle = widgetCollapsedStyle;
            }

            if (!string.Equals(SelectedWidgetCompactAnimationEffect, widgetCompactAnimationEffect, StringComparison.Ordinal))
            {
                SelectedWidgetCompactAnimationEffect = widgetCompactAnimationEffect;
            }

            if (Math.Abs(WidgetCompactAnimationDurationMs - widgetCompactAnimationDurationMs) > 0.01)
            {
                WidgetCompactAnimationDurationMs = widgetCompactAnimationDurationMs;
            }

            if (Math.Abs(WidgetCompactExpandDelayMs - widgetCompactExpandDelayMs) > 0.01)
            {
                WidgetCompactExpandDelayMs = widgetCompactExpandDelayMs;
            }

            if (Math.Abs(WidgetCompactCollapseDelayMs - widgetCompactCollapseDelayMs) > 0.01)
            {
                WidgetCompactCollapseDelayMs = widgetCompactCollapseDelayMs;
            }

            if (!string.Equals(SelectedWidgetCompactMediaCornerMode, widgetCompactMediaCornerMode, StringComparison.Ordinal))
            {
                SelectedWidgetCompactMediaCornerMode = widgetCompactMediaCornerMode;
            }

            if (!string.Equals(SelectedTodoNewTaskPosition, todoNewTaskPosition, StringComparison.Ordinal))
            {
                SelectedTodoNewTaskPosition = todoNewTaskPosition;
            }

            if (!string.Equals(SelectedTodoDefaultFilter, todoDefaultFilter, StringComparison.Ordinal))
            {
                SelectedTodoDefaultFilter = todoDefaultFilter;
            }

            if (!string.Equals(SelectedTodoTabStyle, todoTabStyle, StringComparison.Ordinal))
            {
                SelectedTodoTabStyle = todoTabStyle;
            }

            if (SelectedTodoReminderOffsetMinutes != todoReminderOffsetMinutes)
            {
                SelectedTodoReminderOffsetMinutes = todoReminderOffsetMinutes;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = false;
        }

        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
        OnPropertyChanged(nameof(SelectedTodoTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesIndex));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeIndex));
        NotifyHoverButtonActionPropertiesChanged();
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshLocalizedProperties()
    {
        RefreshSelectionProperties();
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
OnPropertyChanged(nameof(WeatherCitySearchPlaceholder));
OnPropertyChanged(nameof(WeatherCityNoResultsText));
RefreshWeatherCityPopularCities();
        RefreshQuickCaptureClipboardDiagnostics();
    }

    private void RefreshSelectionProperties()
    {
        _cachedThemeDisplayNames = null;
        _cachedTrayIconStyleDisplayNames = null;
        _cachedLanguageDisplayNames = null;
        _cachedWidgetCornerPreferenceDisplayNames = null;
        _cachedWidgetMaterialTypeDisplayNames = null;
        _cachedWidgetBorderColorModeDisplayNames = null;
        _cachedWidgetBorderStyleDisplayNames = null;
        _cachedWidgetCollapseBehaviorDisplayNames = null;
        _cachedWidgetCollapsedStyleDisplayNames = null;
        _cachedWidgetCompactAnimationEffectDisplayNames = null;
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
        OnPropertyChanged(nameof(AvailableThemeDisplayNames));
        OnPropertyChanged(nameof(AvailableTrayIconStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableLanguageDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCornerPreferenceDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetMaterialTypeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetBorderColorModeDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetBorderStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCollapseBehaviorDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCollapsedStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCompactAnimationEffectDisplayNames));
        OnPropertyChanged(nameof(AvailableWidgetCompactMediaCornerDisplayNames));
        OnPropertyChanged(nameof(AvailableLayoutDensityDisplayNames));
        OnPropertyChanged(nameof(AvailableAnimationPresetDisplayNames));
        OnPropertyChanged(nameof(IsOpacitySliderEnabled));
        OnPropertyChanged(nameof(WidgetOpacityVisibility));
        OnPropertyChanged(nameof(MaterialIntensityVisibility));
        OnPropertyChanged(nameof(IsWidgetBorderStyleEnabled));
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
        OnPropertyChanged(nameof(SelectedAttachmentStorageModeIndex));
        OnPropertyChanged(nameof(AvailableTodoDefaultFilterDisplayNames));
        OnPropertyChanged(nameof(AvailableTodoTabStyleDisplayNames));
        OnPropertyChanged(nameof(AvailableTodoReminderOffsetDisplayNames));
        OnPropertyChanged(nameof(AvailableMusicDisplayModeDisplayNames));
        OnPropertyChanged(nameof(SelectedThemeText));
        OnPropertyChanged(nameof(SelectedThemeIndex));
        OnPropertyChanged(nameof(SelectedTrayIconStyleText));
        OnPropertyChanged(nameof(SelectedTrayIconStyleIndex));
        OnPropertyChanged(nameof(SelectedLanguageText));
        OnPropertyChanged(nameof(SelectedLanguageIndex));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceText));
        OnPropertyChanged(nameof(SelectedWidgetCornerPreferenceIndex));
        OnPropertyChanged(nameof(SelectedWidgetMaterialTypeText));
        OnPropertyChanged(nameof(SelectedWidgetMaterialTypeIndex));
        OnPropertyChanged(nameof(SelectedWidgetBorderColorModeText));
        OnPropertyChanged(nameof(SelectedWidgetBorderColorModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetBorderStyleText));
        OnPropertyChanged(nameof(SelectedWidgetBorderStyleIndex));
        OnPropertyChanged(nameof(SelectedWidgetCollapseBehaviorText));
        OnPropertyChanged(nameof(SelectedWidgetCollapseBehaviorIndex));
        OnPropertyChanged(nameof(IsSmartWidgetCollapseBehavior));
        OnPropertyChanged(nameof(SelectedWidgetCollapsedStyleText));
        OnPropertyChanged(nameof(SelectedWidgetCollapsedStyleIndex));
        OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectText));
        OnPropertyChanged(nameof(SelectedWidgetCompactAnimationEffectIndex));
        OnPropertyChanged(nameof(SelectedWidgetCompactMediaCornerText));
        OnPropertyChanged(nameof(SelectedWidgetCompactMediaCornerIndex));
        OnPropertyChanged(nameof(SelectedLayoutDensityText));
        OnPropertyChanged(nameof(SelectedLayoutDensityIndex));
        OnPropertyChanged(nameof(SelectedAnimationPresetText));
        OnPropertyChanged(nameof(SelectedAnimationPresetIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEffectIndex));
        OnPropertyChanged(nameof(IsDirectionEnabled));
        OnPropertyChanged(nameof(IsEasingEnabled));
        OnPropertyChanged(nameof(IsSpeedEnabled));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSpeedIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationSlideDirectionIndex));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityText));
        OnPropertyChanged(nameof(SelectedWidgetAnimationEasingIntensityIndex));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedDisplayWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeText));
        OnPropertyChanged(nameof(SelectedInteractiveWidgetChromeModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeText));
        OnPropertyChanged(nameof(SelectedWidgetTitleIconModeIndex));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeText));
        OnPropertyChanged(nameof(SelectedWidgetLayerModeIndex));
        NotifyHoverButtonActionPropertiesChanged();
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
        OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
        OnPropertyChanged(nameof(SelectedTodoTabStyleText));
        OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesIndex));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
        OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
        OnPropertyChanged(nameof(AvailableWeatherTemperatureUnitDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherTemperatureUnitIndex));
        OnPropertyChanged(nameof(AvailableWeatherWindSpeedUnitDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherWindSpeedUnitIndex));
        OnPropertyChanged(nameof(AvailableWeatherDefaultViewDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherDefaultViewIndex));
        OnPropertyChanged(nameof(AvailableWeatherSkinDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherSkinIndex));
        OnPropertyChanged(nameof(AvailableWeatherRefreshIntervalDisplayNames));
        OnPropertyChanged(nameof(SelectedWeatherRefreshIntervalIndex));
    }
}
