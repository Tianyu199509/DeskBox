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
    public string SelectedTodoNewTaskPosition
    {
        get => _selectedTodoNewTaskPosition;
        set
        {
            if (!SetProperty(ref _selectedTodoNewTaskPosition, NormalizeTodoNewTaskPosition(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoNewTaskPosition = _selectedTodoNewTaskPosition;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
        }
    }

    public string SelectedTodoNewTaskPositionText => GetTodoNewTaskPositionDisplayName(SelectedTodoNewTaskPosition);
    public int SelectedTodoNewTaskPositionIndex => Array.IndexOf(AvailableTodoNewTaskPositions, _selectedTodoNewTaskPosition);

    public string SelectedAttachmentStorageMode
    {
        get => _selectedAttachmentStorageMode;
        set
        {
            string normalized = SettingsService.NormalizeAttachmentStorageMode(value);
            if (!SetProperty(ref _selectedAttachmentStorageMode, normalized))
            {
                return;
            }

            if (!_isRestoringDefaults && !_isApplyingSettingsSnapshot)
            {
                _settingsService.Settings.AttachmentStorageMode = normalized;
                _settingsService.SaveDebounced();
            }

            OnPropertyChanged(nameof(SelectedAttachmentStorageModeIndex));
        }
    }

    public int SelectedAttachmentStorageModeIndex => Array.IndexOf(
        AvailableAttachmentStorageModes,
        _selectedAttachmentStorageMode);

    public string SelectedQuickCaptureDefaultView
    {
        get => _selectedQuickCaptureDefaultView;
        set
        {
            if (!SetProperty(ref _selectedQuickCaptureDefaultView, NormalizeQuickCaptureDefaultView(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            EnsureQuickCaptureTabEnabled(_selectedQuickCaptureDefaultView);
            _settingsService.Settings.QuickCaptureDefaultView = _selectedQuickCaptureDefaultView;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
        }
    }

    public string SelectedQuickCaptureDefaultViewText => GetQuickCaptureDefaultViewDisplayName(SelectedQuickCaptureDefaultView);
    public int SelectedQuickCaptureDefaultViewIndex => Array.IndexOf(AvailableQuickCaptureDefaultViews, _selectedQuickCaptureDefaultView);

    public string SelectedQuickCaptureTabStyle
    {
        get => _selectedQuickCaptureTabStyle;
        set
        {
            if (!SetProperty(ref _selectedQuickCaptureTabStyle, SettingsService.NormalizeWidgetTabStyle(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.QuickCaptureTabStyle = _selectedQuickCaptureTabStyle;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
            OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
        }
    }

    public string SelectedQuickCaptureTabStyleText => GetWidgetTabStyleDisplayName(SelectedQuickCaptureTabStyle);
    public int SelectedQuickCaptureTabStyleIndex => Array.IndexOf(AvailableWidgetTabStyles, _selectedQuickCaptureTabStyle);

    public string SelectedTodoDefaultFilter
    {
        get => _selectedTodoDefaultFilter;
        set
        {
            if (!SetProperty(ref _selectedTodoDefaultFilter, NormalizeTodoDefaultFilter(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            EnsureTodoTabEnabled(_selectedTodoDefaultFilter);
            _settingsService.Settings.TodoDefaultFilter = _selectedTodoDefaultFilter;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
        }
    }

    public string SelectedTodoDefaultFilterText => GetTodoDefaultFilterDisplayName(SelectedTodoDefaultFilter);
    public int SelectedTodoDefaultFilterIndex => Array.IndexOf(AvailableTodoDefaultFilters, _selectedTodoDefaultFilter);

    private void EnsureQuickCaptureTabEnabled(string view)
    {
        switch (view)
        {
            case SettingsService.QuickCaptureDefaultViewPinned:
                QuickCaptureShowPinnedTab = true;
                break;
            case SettingsService.QuickCaptureDefaultViewRecent:
                QuickCaptureShowRecentTab = true;
                break;
            default:
                QuickCaptureShowRecordsTab = true;
                break;
        }
    }

    private void EnsureTodoTabEnabled(string filter)
    {
        switch (filter)
        {
            case SettingsService.TodoDefaultFilterActive:
                TodoShowActiveTab = true;
                break;
            case SettingsService.TodoDefaultFilterToday:
                TodoShowTodayTab = true;
                break;
            case SettingsService.TodoDefaultFilterThisWeek:
                TodoShowThisWeekTab = true;
                break;
            case SettingsService.TodoDefaultFilterThisMonth:
                TodoShowThisMonthTab = true;
                break;
            case SettingsService.TodoDefaultFilterImportant:
                TodoShowImportantTab = true;
                break;
            case SettingsService.TodoDefaultFilterCompleted:
                TodoShowCompletedTab = true;
                break;
            default:
                TodoShowAllTab = true;
                break;
        }
    }

    public string SelectedTodoTabStyle
    {
        get => _selectedTodoTabStyle;
        set
        {
            if (!SetProperty(ref _selectedTodoTabStyle, SettingsService.NormalizeWidgetTabStyle(value)))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoTabStyle = _selectedTodoTabStyle;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoTabStyleText));
            OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
        }
    }

    public string SelectedTodoTabStyleText => GetWidgetTabStyleDisplayName(SelectedTodoTabStyle);
    public int SelectedTodoTabStyleIndex => Array.IndexOf(AvailableWidgetTabStyles, _selectedTodoTabStyle);

    public int SelectedTodoReminderOffsetMinutes
    {
        get => _selectedTodoReminderOffsetMinutes;
        set
        {
            int normalizedValue = SettingsService.NormalizeTodoReminderOffsetMinutes(value);
            if (!SetProperty(ref _selectedTodoReminderOffsetMinutes, normalizedValue))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.TodoDefaultReminderOffsetMinutes = normalizedValue;
            _settingsService.SaveDebounced();
            OnPropertyChanged(nameof(SelectedTodoReminderOffsetMinutesText));
        }
    }

    public string SelectedTodoReminderOffsetMinutesText => GetTodoReminderOffsetDisplayName(SelectedTodoReminderOffsetMinutes);
    public int SelectedTodoReminderOffsetMinutesIndex => Array.IndexOf(AvailableTodoReminderOffsetMinutes, _selectedTodoReminderOffsetMinutes);

    public string SelectedMusicDisplayMode
    {
        get => _selectedMusicDisplayMode;
        set
        {
            string normalizedValue = SettingsService.NormalizeMusicDisplayMode(value);
            if (!SetProperty(ref _selectedMusicDisplayMode, normalizedValue))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedMusicDisplayModeText));
            OnPropertyChanged(nameof(SelectedMusicDisplayModeIndex));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _settingsService.Settings.MusicDisplayMode = normalizedValue;
            _settingsService.SaveDebounced();
        }
    }

    public string SelectedMusicDisplayModeText => GetMusicDisplayModeDisplayName(SelectedMusicDisplayMode);
    public int SelectedMusicDisplayModeIndex => Array.IndexOf(AvailableMusicDisplayModes, _selectedMusicDisplayMode);

    public string AccentColorHex
    {
        get => _accentColorHex;
        private set => SetProperty(ref _accentColorHex, value);
    }

    public Color SelectedAccentColor
    {
        get => _currentAccentColor;
        set
        {
            if (_currentAccentColor.Equals(value))
            {
                return;
            }

            SetCustomAccentColor(value);
        }
    }

    public string ManagedStorageRootPath
    {
        get => _managedStorageRootPath;
        private set => SetProperty(ref _managedStorageRootPath, value);
    }

    public QuickAccessPinState ManagedStorageQuickAccessPinState
    {
        get => _quickAccessPinState;
        private set
        {
            if (!SetProperty(ref _quickAccessPinState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(QuickAccessStatusText));
            OnPropertyChanged(nameof(PinQuickAccessButtonText));
            OnPropertyChanged(nameof(PinQuickAccessToolTipText));
            OnPropertyChanged(nameof(ShouldUnpinManagedStorageFromQuickAccess));
        }
    }

    public bool IsQuickAccessBusy
    {
        get => _isQuickAccessBusy;
        private set
        {
            if (!SetProperty(ref _isQuickAccessBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(QuickAccessStatusText));
            OnPropertyChanged(nameof(PinQuickAccessButtonText));
            OnPropertyChanged(nameof(PinQuickAccessToolTipText));
            OnPropertyChanged(nameof(CanInvokeQuickAccessAction));
        }
    }

    public bool CanInvokeQuickAccessAction => !IsQuickAccessBusy;

    public string QuickAccessStatusText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessStatusUpdating")
        : ManagedStorageQuickAccessPinState switch
    {
        QuickAccessPinState.Pinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusPinned"),
        QuickAccessPinState.NotPinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusNotPinned"),
        _ => _localizationService.T("Settings.ManagedPath.QuickAccessStatusUnknown")
    };

    public string PinQuickAccessButtonText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdating")
        : ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned
            ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccess")
            : _localizationService.T("Settings.ManagedPath.PinQuickAccess");

    public string PinQuickAccessToolTipText => IsQuickAccessBusy
        ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdatingTooltip")
        : ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned
            ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccessTooltip")
            : _localizationService.T("Settings.ManagedPath.PinQuickAccessTooltip");

    public bool ShouldUnpinManagedStorageFromQuickAccess => ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned;

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set
        {
            if (!SetProperty(ref _globalHotkeyEnabled, value))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            App.Current?.GlobalHotkeyService?.SetEnabled(value);
            RefreshGlobalHotkeyStatus();
        }
    }

    public string GlobalHotkeyText
    {
        get => _globalHotkeyText;
        private set => SetProperty(ref _globalHotkeyText, value);
    }

    public string GlobalHotkeyStatusText
    {
        get => _globalHotkeyStatusText;
        private set => SetProperty(ref _globalHotkeyStatusText, value);
    }

    public string GlobalHotkeyStatusKind
    {
        get => _globalHotkeyStatusKind;
        private set => SetProperty(ref _globalHotkeyStatusKind, value);
    }

    public string IconSizeValueText => $"{Math.Round(IconSize):0}px";
    public string WidgetOpacityValueText => $"{Math.Round(WidgetOpacity * 100):0}%";
    public string WidgetMaterialIntensityValueText => $"{Math.Round(WidgetMaterialIntensity * 100):0}%";
    public string TextSizeValueText => $"{TextSize:0.#}pt";
    public string LayoutDensityValueText => $"{Math.Round(LayoutDensityScale * 100):0}%";
    public string HorizontalSpacingValueText => $"{Math.Round(HorizontalSpacingScale * 100):0}%";
    public string VerticalSpacingValueText => $"{Math.Round(VerticalSpacingScale * 100):0}%";
    public string FileNameWidthValueText => $"{Math.Round(FileNameWidthScale * 100):0}%";
    public string DefaultWidthInput
    {
        get => FormatNumber(DefaultWidth, 0);
        set => ApplyNumberInput(value, () => DefaultWidth, next => DefaultWidth = next, SettingsService.MinWidgetWidth, 1200d, 0);
    }

    public string DefaultHeightInput
    {
        get => FormatNumber(DefaultHeight, 0);
        set => ApplyNumberInput(value, () => DefaultHeight, next => DefaultHeight = next, SettingsService.MinWidgetHeight, 1200d, 0);
    }

    public string WidgetOpacityPercentInput
    {
        get => FormatNumber(WidgetOpacityPercent, 0);
        set => ApplyNumberInput(value, () => WidgetOpacityPercent, next => WidgetOpacityPercent = next, 0d, 100d, 0);
    }

    public string IconSizeInput
    {
        get => FormatNumber(IconSize, 0);
        set => ApplyNumberInput(value, () => IconSize, next => IconSize = next, SettingsService.MinIconSize, SettingsService.MaxIconSize, 0);
    }

    public string TextSizeInput
    {
        get => FormatNumber(TextSize, 1);
        set => ApplyNumberInput(value, () => TextSize, next => TextSize = next, SettingsService.MinTextSize, SettingsService.MaxTextSize, 1);
    }

    public string LayoutDensityPercentInput
    {
        get => FormatNumber(LayoutDensityPercent, 0);
        set => ApplyNumberInput(value, () => LayoutDensityPercent, next => LayoutDensityPercent = next, 0d, 100d, 0);
    }

    public string HorizontalSpacingPercentInput
    {
        get => FormatNumber(HorizontalSpacingPercent, 0);
        set => ApplyNumberInput(value, () => HorizontalSpacingPercent, next => HorizontalSpacingPercent = next, 0d, 100d, 0);
    }

    public string VerticalSpacingPercentInput
    {
        get => FormatNumber(VerticalSpacingPercent, 0);
        set => ApplyNumberInput(value, () => VerticalSpacingPercent, next => VerticalSpacingPercent = next, 0d, 100d, 0);
    }

    public string FileNameWidthPercentInput
    {
        get => FormatNumber(FileNameWidthPercent, 0);
        set => ApplyNumberInput(value, () => FileNameWidthPercent, next => FileNameWidthPercent = next, 0d, 100d, 0);
    }

    public double WidgetOpacityPercent
    {
        get => Math.Round(WidgetOpacity * 100);
        set => WidgetOpacity = Math.Clamp(value / 100d, SettingsService.MinWidgetOpacity, SettingsService.MaxWidgetOpacity);
    }

    public double LayoutDensityPercent
    {
        get => Math.Round(LayoutDensityScale * 100);
        set => LayoutDensityScale = Math.Clamp(value / 100d, SettingsService.MinLayoutDensityScale, SettingsService.MaxLayoutDensityScale);
    }

    public double HorizontalSpacingPercent
    {
        get => Math.Round(HorizontalSpacingScale * 100);
        set => HorizontalSpacingScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public double VerticalSpacingPercent
    {
        get => Math.Round(VerticalSpacingScale * 100);
        set => VerticalSpacingScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public double FileNameWidthPercent
    {
        get => Math.Round(FileNameWidthScale * 100);
        set => FileNameWidthScale = Math.Clamp(value / 100d, SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
    }

    public string AccentColorDescription => UseSystemAccentColor
        ? _localizationService.T("Settings.Accent.SystemDescription")
        : _localizationService.T("Settings.Accent.CustomDescription");

    public string GlobalHotkeyDescription => _localizationService.T("Settings.GlobalHotkey.Description");
    public bool CanShowGlobalHotkeyWarning => GlobalHotkeyEnabled && GlobalHotkeyService.IsRiskyGesture(GetCurrentGlobalHotkeyGesture());
    public IEnumerable<FeatureWidgetEntry> FeatureWidgetEntries
    {
        get
        {
            var factory = new FeatureWidgetEntryFactory(
                _localizationService,
                new WidgetContentFactory(_localizationService),
                WidgetRegistry.Default,
                IsWidgetEnabled);
            return factory.CreateEntries();
        }
    }

    public bool IsWidgetEnabled(WidgetKind kind)
    {
        return App.Current?.WidgetManager?.IsFeatureWidgetEnabled(kind) ??
               FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);
    }

    public void SetWidgetEnabled(WidgetKind kind, bool enabled)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                QuickCaptureEnabled = enabled;
                return;
            case WidgetKind.Todo:
                TodoEnabled = enabled;
                return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
        _ = SyncFeatureWidgetAsync(kind, enabled);
    }

    public async Task ResetFeatureWidgetAsync(WidgetKind kind)
    {
        if (!FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        try
        {
            await ApplyFeatureWidgetDefaultSettingsAsync(kind);

            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.ResetFeatureWidgetAsync(kind);
            }
            else
            {
                await _settingsService.SaveAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to reset feature widget kind={kind}: {ex}");
        }
        finally
        {
            RefreshFeatureWidgetViewState(kind);
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    private async Task ApplyFeatureWidgetDefaultSettingsAsync(WidgetKind kind)
    {
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            switch (kind)
            {
                case WidgetKind.QuickCapture:
                    QuickCaptureClipboardEnabled = false;
                    QuickCaptureImageClipboardEnabled = false;
                    QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
                    QuickCaptureShowCreatedTime = true;
                    QuickCaptureItemPreviewLineCount = SettingsService.DefaultItemPreviewLineCount;
                    QuickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
                    SelectedQuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
                    SelectedQuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
                    QuickCaptureShowTabBar = true;
                    QuickCaptureShowRecordsTab = true;
                    QuickCaptureShowPinnedTab = true;
                    QuickCaptureShowRecentTab = true;
                    _settingsService.Settings.QuickCaptureClipboardEnabled = false;
                    _settingsService.Settings.QuickCaptureImageClipboardEnabled = false;
                    _settingsService.Settings.QuickCaptureRecentLimit = QuickCaptureService.DefaultRecentLimit;
                    _settingsService.Settings.QuickCaptureShowCreatedTime = true;
                    _settingsService.Settings.QuickCaptureItemPreviewLineCount = SettingsService.DefaultItemPreviewLineCount;
                    _settingsService.Settings.QuickCaptureEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
                    _settingsService.Settings.QuickCaptureDefaultView = SettingsService.QuickCaptureDefaultViewRecords;
                    _settingsService.Settings.QuickCaptureTabStyle = SettingsService.WidgetTabStyleButton;
                    _settingsService.Settings.QuickCaptureShowTabBar = true;
                    _settingsService.Settings.QuickCaptureShowRecordsTab = true;
                    _settingsService.Settings.QuickCaptureShowPinnedTab = true;
                    _settingsService.Settings.QuickCaptureShowRecentTab = true;
                    _settingsService.Settings.LastQuickCaptureFileWidgetId = string.Empty;
                    App.Current?.QuickCaptureClipboardService?.Refresh();
                    RefreshQuickCaptureClipboardDiagnostics();
                    break;
                case WidgetKind.Todo:
                    TodoShowCompletedTasks = true;
                    TodoItemPreviewLineCount = SettingsService.DefaultItemPreviewLineCount;
                    TodoEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
                    TodoShowFooterStats = false;
                    TodoShowClearCompletedButton = true;
                    TodoConfirmBeforeDelete = false;
                    TodoReminderEnabled = true;
                    SelectedTodoReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
                    SelectedTodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
                    SelectedTodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
                    SelectedTodoTabStyle = SettingsService.WidgetTabStyleButton;
                    TodoShowTabBar = true;
                    TodoShowAllTab = true;
                    TodoShowActiveTab = false;
                    TodoShowTodayTab = true;
                    TodoShowThisWeekTab = false;
                    TodoShowThisMonthTab = false;
                    TodoShowImportantTab = true;
                    TodoShowCompletedTab = true;
                    _settingsService.Settings.TodoShowCompletedTasks = true;
                    _settingsService.Settings.TodoItemPreviewLineCount = SettingsService.DefaultItemPreviewLineCount;
                    _settingsService.Settings.TodoEditorEnterBehavior = SettingsService.EditorEnterBehaviorCtrlEnterSaves;
                    _settingsService.Settings.TodoShowFooterStats = false;
                    _settingsService.Settings.TodoShowClearCompletedButton = true;
                    _settingsService.Settings.TodoConfirmBeforeDelete = false;
                    _settingsService.Settings.TodoReminderEnabled = true;
                    _settingsService.Settings.TodoDefaultReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
                    _settingsService.Settings.TodoNewTaskPosition = SettingsService.TodoNewTaskPositionTop;
                    _settingsService.Settings.TodoDefaultFilter = SettingsService.TodoDefaultFilterAll;
                    _settingsService.Settings.TodoTabStyle = SettingsService.WidgetTabStyleButton;
                    _settingsService.Settings.TodoShowTabBar = true;
                    _settingsService.Settings.TodoShowAllTab = true;
                    _settingsService.Settings.TodoShowActiveTab = false;
                    _settingsService.Settings.TodoShowTodayTab = true;
                    _settingsService.Settings.TodoShowThisWeekTab = false;
                    _settingsService.Settings.TodoShowThisMonthTab = false;
                    _settingsService.Settings.TodoShowImportantTab = true;
                    _settingsService.Settings.TodoShowCompletedTab = true;
                    break;
                case WidgetKind.Music:
                    MusicUseArtworkBackdrop = true;
                    MusicEnableCoverHoverMotion = true;
                    SelectedMusicDisplayMode = SettingsService.MusicDisplayModeAuto;
                    _settingsService.Settings.MusicUseArtworkBackdrop = true;
                    _settingsService.Settings.MusicEnableCoverHoverMotion = true;
                    _settingsService.Settings.MusicDisplayMode = SettingsService.MusicDisplayModeAuto;
                    break;
                case WidgetKind.Weather:
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

                    _settingsService.Settings.WeatherAutoLocation = true;
                    _settingsService.Settings.WeatherCityName = string.Empty;
                    _settingsService.Settings.WeatherLatitude = 0;
                    _settingsService.Settings.WeatherLongitude = 0;
                    _settingsService.Settings.WeatherTemperatureUnit = SettingsService.WeatherTemperatureUnitCelsius;
                    _settingsService.Settings.WeatherWindSpeedUnit = SettingsService.WeatherWindSpeedUnitKmh;
                    _settingsService.Settings.WeatherDefaultView = SettingsService.WeatherDefaultViewToday;
                    _settingsService.Settings.WeatherSkin = SettingsService.WeatherSkinStandard;
                    _settingsService.Settings.WeatherShowForecast = true;
                    _settingsService.Settings.WeatherShowSunrise = true;
                    _settingsService.Settings.WeatherShowUvIndex = true;
                    _settingsService.Settings.WeatherShowPrecipitation = true;
                    _settingsService.Settings.WeatherShowHumidity = true;
                    _settingsService.Settings.WeatherShowWind = true;
                    _settingsService.Settings.WeatherShowPressure = false;
                    _settingsService.Settings.WeatherRefreshIntervalMinutes = 60;
                    break;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }

        await _settingsService.SaveAsync();
    }

    private void RefreshFeatureWidgetViewState(WidgetKind kind)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
                OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
                OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
                OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewText));
                OnPropertyChanged(nameof(SelectedQuickCaptureDefaultViewIndex));
                OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleText));
                OnPropertyChanged(nameof(SelectedQuickCaptureTabStyleIndex));
                break;
            case WidgetKind.Todo:
                OnPropertyChanged(nameof(TodoEnabled));
                OnPropertyChanged(nameof(SelectedTodoNewTaskPositionText));
                OnPropertyChanged(nameof(SelectedTodoNewTaskPositionIndex));
                OnPropertyChanged(nameof(SelectedTodoDefaultFilterText));
                OnPropertyChanged(nameof(SelectedTodoDefaultFilterIndex));
                OnPropertyChanged(nameof(SelectedTodoTabStyleText));
                OnPropertyChanged(nameof(SelectedTodoTabStyleIndex));
                break;
            case WidgetKind.Music:
                break;
            case WidgetKind.Weather:
                OnPropertyChanged(nameof(SelectedWeatherDefaultViewIndex));
                OnPropertyChanged(nameof(SelectedWeatherSkinIndex));
                OnPropertyChanged(nameof(SelectedWeatherTemperatureUnitIndex));
                OnPropertyChanged(nameof(SelectedWeatherWindSpeedUnitIndex));
                OnPropertyChanged(nameof(SelectedWeatherRefreshIntervalIndex));
                break;
        }
    }

    private async Task SyncFeatureWidgetAsync(WidgetKind kind, bool enabled)
    {
        try
        {
            if (App.Current?.WidgetManager is not { } widgetManager)
            {
                await _settingsService.SaveAsync();
                return;
            }

            await widgetManager.SetFeatureWidgetEnabledAsync(kind, enabled, reveal: enabled);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync feature widget enabled state kind={kind}: {ex}");
        }
        finally
        {
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    public SolidColorBrush AccentPreviewBrush { get; } = new(AccentColorHelper.DefaultAccentColor);

    public string[] AvailableThemes { get; } = [ThemeSystem, ThemeLight, ThemeDark];
    public string[] AvailableThemeDisplayNames => _cachedThemeDisplayNames ??= AvailableThemes.Select(GetThemeDisplayName).ToArray();
    public string[] AvailableLanguages { get; } = [SettingsService.LanguageSystem, SettingsService.LanguageChinese, SettingsService.LanguageEnglish];
    public string[] AvailableLanguageDisplayNames => _cachedLanguageDisplayNames ??= AvailableLanguages.Select(_localizationService.GetLanguageDisplayName).ToArray();
    public string[] AvailableWidgetCornerPreferences { get; } =
        [CornerDefault, CornerSmall, CornerRound, CornerSquare];
    public string[] AvailableWidgetCornerPreferenceDisplayNames => _cachedWidgetCornerPreferenceDisplayNames ??= AvailableWidgetCornerPreferences.Select(GetCornerDisplayName).ToArray();

    public string[] AvailableWidgetMaterialTypes { get; } =
        [MaterialAcrylic, MaterialAcrylicBase, MaterialMica, MaterialMicaAlt, MaterialSolid];
    public string[] AvailableWidgetMaterialTypeDisplayNames => _cachedWidgetMaterialTypeDisplayNames ??= AvailableWidgetMaterialTypes.Select(GetMaterialTypeDisplayName).ToArray();

    public string[] AvailableWidgetBorderColorModes { get; } =
        [BorderColorNeutral, BorderColorAccent, BorderColorNone];
    public string[] AvailableWidgetBorderColorModeDisplayNames =>
        _cachedWidgetBorderColorModeDisplayNames ??=
            AvailableWidgetBorderColorModes.Select(GetBorderColorModeDisplayName).ToArray();

    public string[] AvailableWidgetBorderStyles { get; } = [BorderThin, BorderMedium, BorderThick];
    public string[] AvailableWidgetBorderStyleDisplayNames => _cachedWidgetBorderStyleDisplayNames ??= AvailableWidgetBorderStyles.Select(GetBorderStyleDisplayName).ToArray();

    public string[] AvailableWidgetCollapseBehaviors { get; } =
    [
        SettingsService.WidgetCollapseBehaviorClick,
        SettingsService.WidgetCollapseBehaviorSmart
    ];
    public string[] AvailableWidgetCollapseBehaviorDisplayNames =>
        _cachedWidgetCollapseBehaviorDisplayNames ??=
            AvailableWidgetCollapseBehaviors.Select(GetWidgetCollapseBehaviorDisplayName).ToArray();

    public string[] AvailableWidgetCompactContentModes { get; } =
    [
        SettingsService.WidgetCompactContentModeSmart,
        SettingsService.WidgetCompactContentModeSummary,
        SettingsService.WidgetCompactContentModeMinimal
    ];
    public string[] AvailableWidgetCompactContentModeDisplayNames =>
        _cachedWidgetCompactContentModeDisplayNames ??=
            AvailableWidgetCompactContentModes.Select(GetWidgetCompactContentModeDisplayName).ToArray();
    public string[] AvailableLayoutDensities { get; } =
    [
        SettingsService.LayoutDensityCompact,
        SettingsService.LayoutDensityStandard,
        SettingsService.LayoutDensityRelaxed,
        SettingsService.LayoutDensityCustom
    ];
    public string[] AvailableLayoutDensityDisplayNames =>
        _cachedLayoutDensityDisplayNames ??= AvailableLayoutDensities.Select(GetLayoutDensityDisplayName).ToArray();
    public string[] AvailableMusicDisplayModes { get; } =
    [
        SettingsService.MusicDisplayModeAuto,
        SettingsService.MusicDisplayModeCover,
        SettingsService.MusicDisplayModeControls
    ];
    public string[] AvailableMusicDisplayModeDisplayNames =>
        _cachedMusicDisplayModeDisplayNames ??= AvailableMusicDisplayModes.Select(GetMusicDisplayModeDisplayName).ToArray();
    public string[] AvailableAnimationPresets { get; } =
    [
        AnimationPresetNone,
        AnimationPresetGentle,
        AnimationPresetStandard,
        AnimationPresetEmphasized,
        AnimationPresetCustom
    ];
    public string[] AvailableAnimationPresetDisplayNames =>
        _cachedAnimationPresetDisplayNames ??= AvailableAnimationPresets.Select(GetAnimationPresetDisplayName).ToArray();
    public string[] AvailableWidgetAnimationEffects { get; } =
    [
        SettingsService.WidgetAnimationEffectSlideFade,
        SettingsService.WidgetAnimationEffectFade,
        SettingsService.WidgetAnimationEffectScaleFade,
        SettingsService.WidgetAnimationEffectZoom,
        SettingsService.WidgetAnimationEffectNone
    ];
    public string[] AvailableWidgetAnimationEffectDisplayNames => _cachedWidgetAnimationEffectDisplayNames ??= AvailableWidgetAnimationEffects.Select(GetWidgetAnimationEffectDisplayName).ToArray();
    public string[] AvailableWidgetAnimationSpeeds { get; } =
    [
        SettingsService.WidgetAnimationSpeedVeryFast,
        SettingsService.WidgetAnimationSpeedFast,
        SettingsService.WidgetAnimationSpeedStandard,
        SettingsService.WidgetAnimationSpeedRelaxed,
        SettingsService.WidgetAnimationSpeedSlow
    ];
    public string[] AvailableWidgetAnimationSpeedDisplayNames => _cachedWidgetAnimationSpeedDisplayNames ??= AvailableWidgetAnimationSpeeds.Select(GetWidgetAnimationSpeedDisplayName).ToArray();
    public string[] AvailableWidgetAnimationSlideDirections { get; } =
    [
        SettingsService.WidgetAnimationSlideDirectionLeft,
        SettingsService.WidgetAnimationSlideDirectionRight,
        SettingsService.WidgetAnimationSlideDirectionUp,
        SettingsService.WidgetAnimationSlideDirectionDown
    ];
    public string[] AvailableWidgetAnimationSlideDirectionDisplayNames => _cachedWidgetAnimationSlideDirectionDisplayNames ??= AvailableWidgetAnimationSlideDirections.Select(GetWidgetAnimationSlideDirectionDisplayName).ToArray();
    public string[] AvailableWidgetAnimationEasingIntensities { get; } =
    [
        SettingsService.WidgetAnimationEasingNone,
        SettingsService.WidgetAnimationEasingLight,
        SettingsService.WidgetAnimationEasingStandard,
        SettingsService.WidgetAnimationEasingStrong
    ];
    public string[] AvailableWidgetAnimationEasingIntensityDisplayNames => _cachedWidgetAnimationEasingIntensityDisplayNames ??= AvailableWidgetAnimationEasingIntensities.Select(GetWidgetAnimationEasingIntensityDisplayName).ToArray();

    public string[] AvailableDisplayWidgetChromeModes { get; } =
    [
        SettingsService.WidgetChromeModeStandard,
        SettingsService.WidgetChromeModeCompact,
        SettingsService.WidgetChromeModeOverlay,
        SettingsService.WidgetChromeModeHidden
    ];

    public string[] AvailableInteractiveWidgetChromeModes { get; } =
    [
        SettingsService.WidgetChromeModeStandard,
        SettingsService.WidgetChromeModeCompact,
        SettingsService.WidgetChromeModeOverlay,
        SettingsService.WidgetChromeModeHidden
    ];

    public string[] AvailableDisplayWidgetChromeModeDisplayNames => _cachedDisplayWidgetChromeModeDisplayNames ??= AvailableDisplayWidgetChromeModes.Select(GetWidgetChromeModeDisplayName).ToArray();
    public string[] AvailableInteractiveWidgetChromeModeDisplayNames => _cachedInteractiveWidgetChromeModeDisplayNames ??= AvailableInteractiveWidgetChromeModes.Select(GetWidgetChromeModeDisplayName).ToArray();

    public string[] AvailableWidgetTitleIconModes { get; } =
    [
        SettingsService.WidgetTitleIconModeFilledMono,
        SettingsService.WidgetTitleIconModeLineMono,
        SettingsService.WidgetTitleIconModeColor,
        SettingsService.WidgetTitleIconModeHidden,
        SettingsService.WidgetTitleIconModeTextLabel
    ];

    public string[] AvailableWidgetTitleIconModeDisplayNames => _cachedWidgetTitleIconModeDisplayNames ??= AvailableWidgetTitleIconModes.Select(GetWidgetTitleIconModeDisplayName).ToArray();

    public string[] AvailableWidgetLayerModes { get; } =
    [
        SettingsService.WidgetLayerModeDynamic,
        SettingsService.WidgetLayerModeDesktopPinned
    ];

    public string[] AvailableWidgetLayerModeDisplayNames => _cachedWidgetLayerModeDisplayNames ??= AvailableWidgetLayerModes.Select(GetWidgetLayerModeDisplayName).ToArray();

    public string[] AvailableQuickCaptureDefaultViews { get; } =
    [
        SettingsService.QuickCaptureDefaultViewRecords,
        SettingsService.QuickCaptureDefaultViewPinned,
        SettingsService.QuickCaptureDefaultViewRecent
    ];

    public string[] AvailableQuickCaptureDefaultViewDisplayNames => _cachedQuickCaptureDefaultViewDisplayNames ??= AvailableQuickCaptureDefaultViews.Select(GetQuickCaptureDefaultViewDisplayName).ToArray();

    public string[] AvailableWidgetTabStyles { get; } =
    [
        SettingsService.WidgetTabStylePivot,
        SettingsService.WidgetTabStyleButton
    ];

    public string[] AvailableQuickCaptureTabStyleDisplayNames => _cachedQuickCaptureTabStyleDisplayNames ??= AvailableWidgetTabStyles.Select(GetWidgetTabStyleDisplayName).ToArray();

    public string[] AvailableTodoNewTaskPositions { get; } =
    [
        SettingsService.TodoNewTaskPositionTop,
        SettingsService.TodoNewTaskPositionBottom
    ];

    public string[] AvailableTodoNewTaskPositionDisplayNames => _cachedTodoNewTaskPositionDisplayNames ??= AvailableTodoNewTaskPositions.Select(GetTodoNewTaskPositionDisplayName).ToArray();

    public string[] AvailableAttachmentStorageModes { get; } =
    [
        SettingsService.AttachmentStorageModeLink,
        SettingsService.AttachmentStorageModeCopy
    ];

    public string[] AvailableAttachmentStorageModeDisplayNames =>
        _cachedAttachmentStorageModeDisplayNames ??=
            AvailableAttachmentStorageModes.Select(GetAttachmentStorageModeDisplayName).ToArray();

    public string GetAttachmentStorageModeDisplayName(string storageMode)
    {
        return SettingsService.NormalizeAttachmentStorageMode(storageMode) == SettingsService.AttachmentStorageModeCopy
            ? _localizationService.T("Settings.AttachmentStorageMode.Copy")
            : _localizationService.T("Settings.AttachmentStorageMode.Link");
    }

    public string[] AvailableTodoDefaultFilters { get; } =
    [
        SettingsService.TodoDefaultFilterAll,
        SettingsService.TodoDefaultFilterActive,
        SettingsService.TodoDefaultFilterToday,
        SettingsService.TodoDefaultFilterThisWeek,
        SettingsService.TodoDefaultFilterThisMonth,
        SettingsService.TodoDefaultFilterImportant,
        SettingsService.TodoDefaultFilterCompleted
    ];

    public string[] AvailableTodoDefaultFilterDisplayNames => _cachedTodoDefaultFilterDisplayNames ??= AvailableTodoDefaultFilters.Select(GetTodoDefaultFilterDisplayName).ToArray();

    public string[] AvailableTodoTabStyleDisplayNames => _cachedTodoTabStyleDisplayNames ??= AvailableWidgetTabStyles.Select(GetWidgetTabStyleDisplayName).ToArray();

    public int[] AvailableTodoReminderOffsetMinutes { get; } =
    [
        0,
        5,
        10,
        15,
        30,
        60,
        1440
    ];

    public string[] AvailableTodoReminderOffsetDisplayNames => _cachedTodoReminderOffsetDisplayNames ??= AvailableTodoReminderOffsetMinutes.Select(GetTodoReminderOffsetDisplayName).ToArray();

// ─── Weather Settings Properties ──────────────────────────────
}
