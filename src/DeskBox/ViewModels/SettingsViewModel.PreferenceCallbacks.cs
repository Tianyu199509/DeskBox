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
    partial void OnAutoStartChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        ApplyAutoStartOperationResult(StartupService.SetEnabled(value));
    }

    public void RefreshAutoStartState()
    {
        ApplyAutoStartState(StartupService.GetState());
    }

    partial void OnSelectedAutoStartModeChanged(string value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot ||
            StartupService.Current is not DirectStartupService directStartup ||
            !Enum.TryParse(value, out StartupMode mode))
            return;
        ApplyAutoStartOperationResult(directStartup.SetMode(mode));
    }

    private void ApplyAutoStartOperationResult(StartupOperationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            App.Log(
                $"[Settings] Startup state={result.State}: {result.ErrorMessage}");
        }

        _autoStartUsedFallback = result.UsedFallback;
        _autoStartOperationFailed = !result.UsedFallback &&
            (result.State is StartupRegistrationState.BlockedOrFailed or StartupRegistrationState.PathMismatch ||
                !string.IsNullOrWhiteSpace(result.ErrorMessage));
        // A failed mode switch may have preserved the old working entry.
        // Keep the toggle truthful while showing the operation warning separately.
        ApplyAutoStartState(_autoStartOperationFailed ? StartupService.GetState() : result.State);
    }

    private void ApplyAutoStartState(StartupRegistrationState state)
    {
        _autoStartState = state;
        bool effectiveValue = state is
            StartupRegistrationState.Enabled or
            StartupRegistrationState.Pending;
        bool wasApplyingSettingsSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            AutoStart = effectiveValue;
            if (StartupService.Current is DirectStartupService directStartup)
                SelectedAutoStartMode = directStartup.Mode.ToString();
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSettingsSnapshot;
        }

        OnPropertyChanged(nameof(AutoStartStatusText));
        OnPropertyChanged(nameof(AutoStartStatusVisibility));
        OnPropertyChanged(nameof(AutoStartSystemSettingsVisibility));
        OnPropertyChanged(nameof(AutoStartModeVisibility));

        // The coordinator skips unchanged values, mirroring the page's old
        // read-before-write guard.
        _interactionSettings.SetAutoStart(effectiveValue);
    }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetAutoCheckForUpdates(value);
    }

    partial void OnDoubleClickToOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedFileOpenMethod));
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetDoubleClickToOpen(value);
    }

    partial void OnFileItemSystemContextMenuEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetFileItemSystemContextMenuEnabled(value);
        if (value)
        {
            // Warm the native context-menu server so the first right-click in
            // a widget does not pay the cold handler-loading cost.
            ShellContextMenuProxy.Prewarm();
        }
    }

    partial void OnResizeSnapEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetResizeSnapEnabled(value);

        // Sync to the live overlay service
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.IsSnapEnabled = value;
        }
    }

    partial void OnWidgetSnapSpacingChanged(double value)
    {
        double normalized = SettingsService.NormalizeWidgetSnapSpacing(value);
        if (!double.IsFinite(value) || Math.Abs(value - normalized) > 0.0001)
        {
            WidgetSnapSpacing = normalized;
            return;
        }

        OnPropertyChanged(nameof(WidgetSnapSpacingText));

        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetWidgetSnapSpacing(normalized);
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.SnapSpacingDips = normalized;
        }
    }

    partial void OnKeepWidgetsVisibleOnShowDesktopChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedShowDesktopBehavior));
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _interactionSettings.SetKeepWidgetsVisibleOnShowDesktop(value);
        App.Current?.WidgetManager?.RefreshVisibleWidgetDesktopLayers(
            "settings-show-desktop-visibility");
    }

    public string SelectedFileOpenMethod
    {
        get => DoubleClickToOpen ? FileOpenMethodDoubleClick : FileOpenMethodSingleClick;
        set => DoubleClickToOpen = !string.Equals(
            value,
            FileOpenMethodSingleClick,
            StringComparison.Ordinal);
    }

    public IReadOnlyList<SettingsOption> AvailableFileOpenMethodOptions =>
        WrapOptions(
        [
            new(FileOpenMethodSingleClick, _localizationService.T("Settings.OpenMethod.SingleClick")),
            new(FileOpenMethodDoubleClick, _localizationService.T("Settings.OpenMethod.DoubleClick"))
        ]);

    public string SelectedShowDesktopBehavior
    {
        get => KeepWidgetsVisibleOnShowDesktop
            ? ShowDesktopBehaviorKeepVisible
            : ShowDesktopBehaviorHideWithWindows;
        set => KeepWidgetsVisibleOnShowDesktop = !string.Equals(
            value,
            ShowDesktopBehaviorHideWithWindows,
            StringComparison.Ordinal);
    }

    public IReadOnlyList<SettingsOption> AvailableShowDesktopBehaviorOptions =>
        WrapOptions(
        [
            new(ShowDesktopBehaviorKeepVisible, _localizationService.T("Settings.ShowDesktopBehavior.KeepVisible")),
            new(ShowDesktopBehaviorHideWithWindows, _localizationService.T("Settings.ShowDesktopBehavior.HideWithWindows"))
        ]);

    partial void OnDefaultWidthChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultWidthInput));
            return;
        }

        var update = _appearanceSettings.UpdateDefaultWidgetWidth(value);
        if (!update.Committed)
        {
            DefaultWidth = update.Value;
            return;
        }

        OnPropertyChanged(nameof(DefaultWidthInput));
    }

    partial void OnDefaultHeightChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultHeightInput));
            return;
        }

        var update = _appearanceSettings.UpdateDefaultWidgetHeight(value);
        if (!update.Committed)
        {
            DefaultHeight = update.Value;
            return;
        }

        OnPropertyChanged(nameof(DefaultHeightInput));
    }

    partial void OnHideShortcutArrowOverlayChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _fileDisplaySettings.SetHideShortcutArrowOverlay(value);
    }

    partial void OnShowImageFilesAsIconsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _fileDisplaySettings.SetShowImageFilesAsIcons(value);
    }

    partial void OnShowHoverButtonsChanged(bool value)
    {
        OnPropertyChanged(nameof(HoverButtonActionsSummaryText));
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetShowHoverButtons(value);
    }

    partial void OnShowHoverActionLockPositionChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockPosition, value);
    }

    partial void OnShowHoverActionLockSizeChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockSize, value);
    }

    partial void OnShowHoverActionAddChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionAdd, value);
    }

    partial void OnShowHoverActionMoreChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionMore, value);
    }

    partial void OnShowHoverActionDeleteChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionDelete, value);
    }

    partial void OnShowListItemDetailsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _fileDisplaySettings.SetShowListItemDetails(value);
    }

    partial void OnShowFileItemPathTooltipsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _fileDisplaySettings.SetShowFileItemPathTooltips(value);
    }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _fileDisplaySettings.SetShowFileExtensions(value);
    }

    partial void OnHideShortcutExtensionWhenShowingFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _fileDisplaySettings.SetHideShortcutExtensionWhenShowingFileExtensions(value);
    }

    partial void OnIdleWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetIdleWorkingSetTrimEnabled(value);
    }

    partial void OnImmediateHiddenWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetImmediateHiddenWorkingSetTrimEnabled(value);
    }

    partial void OnQuiescenceWorkingSetTrimEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.Performance.QuiescenceWorkingSetTrimEnabled = value;
        _settingsService.SaveDebounced();
    }
}
