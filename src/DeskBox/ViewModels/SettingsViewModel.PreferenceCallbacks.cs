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
        // First call runs before any overwrite: capture the persisted
        // preference so a vanished registration can be attributed to
        // external cleanup (see the field's comment).
        if (_autoStartPreferenceAtLoad is null)
        {
            _autoStartPreferenceAtLoad = AutoStart;
        }

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

    partial void OnSilentStartupChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetSilentStartup(value);
    }

    /// <summary>
    /// Host linkage for the interaction editor: the user toggled the
    /// file-item context menu (the editor persisted the value through the
    /// coordinator). Warm the native context-menu server so the first
    /// right-click in a widget does not pay the cold handler-loading cost.
    /// </summary>
    private void OnInteractionFileItemContextMenuUserChanged(bool value)
    {
        if (value)
        {
            ShellContextMenuProxy.Prewarm();
        }
    }

    // Host linkages for the interaction editor: the editor persists the
    // snap and show-desktop fields through the coordinator; the resize-guide
    // overlay sync and the visible-layer refresh stay on the shell because
    // they reach host services.
    private void OnInteractionSnapEnabledUserChanged(bool value)
    {
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.IsSnapEnabled = value;
        }
    }

    private void OnInteractionSnapSpacingUserChanged(double value)
    {
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.SnapSpacingDips = value;
        }
    }

    private void OnInteractionShowDesktopBehaviorUserChanged()
    {
        App.Current?.WidgetManager?.RefreshVisibleWidgetDesktopLayers(
            "settings-show-desktop-visibility");
    }

    partial void OnShowHoverButtonsChanged(bool value)
    {
        _interactionSettings.UpdateHoverButtonActionsSummary(BuildHoverButtonActionsSummary());
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

    // The three working-set trim switches render in the performance section;
    // their binding surface moved to the performance editor (batch 50) and
    // writes go through the interaction / performance coordinators from the
    // editor's change handlers.
}
