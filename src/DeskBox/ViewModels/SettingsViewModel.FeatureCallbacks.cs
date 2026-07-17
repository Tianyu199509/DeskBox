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
    partial void OnQuickCaptureEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            OnPropertyChanged(nameof(QuickCaptureStatusText));
            OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, value);
        if (!value)
        {
            ApplyQuickCaptureRecordingState(clipboardEnabled: false, imageEnabled: false);
            _settingsService.SaveDebounced();
            App.Current?.QuickCaptureClipboardService?.Refresh();
        }

        _ = SyncQuickCaptureEnabledAsync(value);
        OnPropertyChanged(nameof(QuickCaptureStatusText));
        OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
        RefreshQuickCaptureClipboardDiagnostics();
    }

    partial void OnQuickCaptureShowTabBarChanged(bool value) => PersistQuickCaptureTabSettings();
    partial void OnQuickCaptureShowRecordsTabChanged(bool value) => PersistQuickCaptureTabSettings();
    partial void OnQuickCaptureShowPinnedTabChanged(bool value) => PersistQuickCaptureTabSettings();
    partial void OnQuickCaptureShowRecentTabChanged(bool value) => PersistQuickCaptureTabSettings();

    private void PersistQuickCaptureTabSettings()
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.QuickCaptureShowTabBar = QuickCaptureShowTabBar;
        settings.QuickCaptureShowRecordsTab = QuickCaptureShowRecordsTab;
        settings.QuickCaptureShowPinnedTab = QuickCaptureShowPinnedTab;
        settings.QuickCaptureShowRecentTab = QuickCaptureShowRecentTab;
        if (!QuickCaptureShowRecordsTab && !QuickCaptureShowPinnedTab && !QuickCaptureShowRecentTab)
        {
            _isApplyingSettingsSnapshot = true;
            try
            {
                QuickCaptureShowRecordsTab = true;
            }
            finally
            {
                _isApplyingSettingsSnapshot = false;
            }

            settings.QuickCaptureShowRecordsTab = true;
        }

        if (!SettingsService.IsQuickCaptureTabVisible(settings, settings.QuickCaptureDefaultView))
        {
            SelectedQuickCaptureDefaultView = SettingsService.GetFirstVisibleQuickCaptureTab(settings);
        }

        _settingsService.SaveDebounced();
    }

    private async Task SyncQuickCaptureEnabledAsync(bool value)
    {
        try
        {
            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetQuickCaptureEnabledAsync(value, reveal: value);
                return;
            }

            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync Quick Capture enabled state: {ex}");
        }
        finally
        {
            App.Current?.QuickCaptureClipboardService?.Refresh();
            OnPropertyChanged(nameof(FeatureWidgetEntries));
            RefreshQuickCaptureClipboardDiagnostics();
        }
    }

    partial void OnTodoEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Todo, value);
        _ = SyncTodoEnabledAsync(value);
        OnPropertyChanged(nameof(FeatureWidgetEntries));
        App.Current?.TodoReminderService?.Refresh();
    }

    partial void OnTodoShowTabBarChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowAllTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowActiveTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowTodayTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowThisWeekTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowThisMonthTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowImportantTabChanged(bool value) => PersistTodoTabSettings();
    partial void OnTodoShowCompletedTabChanged(bool value) => PersistTodoTabSettings();

    private void PersistTodoTabSettings()
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.TodoShowTabBar = TodoShowTabBar;
        settings.TodoShowAllTab = TodoShowAllTab;
        settings.TodoShowActiveTab = TodoShowActiveTab;
        settings.TodoShowTodayTab = TodoShowTodayTab;
        settings.TodoShowThisWeekTab = TodoShowThisWeekTab;
        settings.TodoShowThisMonthTab = TodoShowThisMonthTab;
        settings.TodoShowImportantTab = TodoShowImportantTab;
        settings.TodoShowCompletedTab = TodoShowCompletedTab;
        if (!TodoShowAllTab && !TodoShowActiveTab && !TodoShowTodayTab &&
            !TodoShowThisWeekTab && !TodoShowThisMonthTab &&
            !TodoShowImportantTab && !TodoShowCompletedTab)
        {
            _isApplyingSettingsSnapshot = true;
            try
            {
                TodoShowAllTab = true;
            }
            finally
            {
                _isApplyingSettingsSnapshot = false;
            }

            settings.TodoShowAllTab = true;
        }

        if (!SettingsService.IsTodoTabVisible(settings, settings.TodoDefaultFilter))
        {
            SelectedTodoDefaultFilter = SettingsService.GetFirstVisibleTodoTab(settings);
        }

        _settingsService.SaveDebounced();
    }

    partial void OnTodoShowCompletedTasksChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowCompletedTasks = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoShowFooterStatsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowFooterStats = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoShowClearCompletedButtonChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoShowClearCompletedButton = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoConfirmBeforeDeleteChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoConfirmBeforeDelete = value;
        _settingsService.SaveDebounced();
    }

    partial void OnTodoReminderEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.TodoReminderEnabled = value;
        _settingsService.SaveDebounced();
        App.Current?.TodoReminderService?.Refresh();
        if (value && App.Current?.TodoReminderService is { } reminderService)
        {
            _ = reminderService.CheckNowAsync(DateTimeOffset.Now);
        }
    }

    partial void OnMusicUseArtworkBackdropChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.MusicUseArtworkBackdrop = value;
        _settingsService.SaveDebounced();
    }

    partial void OnMusicEnableCoverHoverMotionChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.MusicEnableCoverHoverMotion = value;
        _settingsService.SaveDebounced();
    }

    private async Task SyncTodoEnabledAsync(bool enabled)
    {
        try
        {
            if (App.Current?.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(WidgetKind.Todo, enabled, reveal: enabled);
                return;
            }

            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to sync Todo enabled state: {ex}");
        }
        finally
        {
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    partial void OnQuickCaptureClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        ApplyQuickCaptureRecordingState(
            clipboardEnabled: value,
            imageEnabled: value && QuickCaptureImageClipboardEnabled);

        if (value)
        {
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, true);
            bool shouldSyncQuickCapture = !QuickCaptureEnabled;
            if (shouldSyncQuickCapture)
            {
                _quickCaptureEnabled = true;
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            }

            _ = SyncQuickCaptureEnabledAsync(true);
        }
        else
        {
            App.Log("[QuickCaptureClipboard] Disabled from settings");
        }

        _settingsService.SaveDebounced();
        App.Current?.QuickCaptureClipboardService?.Refresh();
        if (value)
        {
            App.Current?.QuickCaptureClipboardService?.CaptureCurrent();
        }

        RefreshQuickCaptureClipboardDiagnostics();
    }

    partial void OnQuickCaptureImageClipboardEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        if (value)
        {
            ApplyQuickCaptureRecordingState(clipboardEnabled: true, imageEnabled: true);
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.QuickCapture, true);
            bool shouldSyncQuickCapture = !QuickCaptureEnabled;
            if (shouldSyncQuickCapture)
            {
                _quickCaptureEnabled = true;
                OnPropertyChanged(nameof(QuickCaptureEnabled));
                OnPropertyChanged(nameof(QuickCaptureStatusText));
                OnPropertyChanged(nameof(QuickCaptureDependencyStatusText));
            }

            _ = SyncQuickCaptureEnabledAsync(true);
        }
        else
        {
            ApplyQuickCaptureRecordingState(
                clipboardEnabled: QuickCaptureClipboardEnabled,
                imageEnabled: false);
        }

        _settingsService.SaveDebounced();
        App.Current?.QuickCaptureClipboardService?.Refresh();
        RefreshQuickCaptureClipboardDiagnostics();
        if (value)
        {
            App.Current?.QuickCaptureClipboardService?.CaptureCurrent();
        }
    }

    private void ApplyQuickCaptureRecordingState(bool clipboardEnabled, bool imageEnabled)
    {
        if (!clipboardEnabled)
        {
            imageEnabled = false;
        }

        _settingsService.Settings.QuickCaptureClipboardEnabled = clipboardEnabled;
        _settingsService.Settings.QuickCaptureImageClipboardEnabled = imageEnabled;

        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            if (QuickCaptureClipboardEnabled != clipboardEnabled)
            {
                QuickCaptureClipboardEnabled = clipboardEnabled;
            }

            if (QuickCaptureImageClipboardEnabled != imageEnabled)
            {
                QuickCaptureImageClipboardEnabled = imageEnabled;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }
    }

    partial void OnQuickCaptureRecentLimitChanged(int value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
            OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
            return;
        }

        int normalizedValue = QuickCaptureService.NormalizeRecentLimit(value);
        if (normalizedValue != value)
        {
            QuickCaptureRecentLimit = normalizedValue;
            return;
        }

        _settingsService.Settings.QuickCaptureRecentLimit = normalizedValue;
        _settingsService.SaveDebounced();
        _ = App.Current.QuickCaptureService.TrimRecentItemsAsync(normalizedValue);
        OnPropertyChanged(nameof(QuickCaptureRecentLimitText));
        OnPropertyChanged(nameof(QuickCaptureRecentLimitInput));
    }

    partial void OnQuickCaptureShowCreatedTimeChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.QuickCaptureShowCreatedTime = value;
        _settingsService.SaveDebounced();
    }
}
