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

        // User deliberately toggled the switch: update the baseline preference
        // so turning AutoStart OFF never triggers the ExternallyRemoved banner.
        _autoStartPreferenceAtLoad = value;
        _ = SetAutoStartEnabledAsync(value);
    }

    public void RefreshAutoStartState()
    {
        _ = RefreshAutoStartStateAsync();
    }

    internal async Task RefreshAutoStartStateAsync()
    {
        if (_isDisposed || Volatile.Read(ref _autoStartMutationInFlight) > 0)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref _autoStartOperationSequence);
        try
        {
            StartupOperationResult status = await _appActions.GetAutoStartStatusAsync();
            void Apply()
            {
                if (_isDisposed || Volatile.Read(ref _autoStartOperationSequence) != sequence)
                {
                    return;
                }

                _autoStartUsedFallback = status.UsedFallback;
                _autoStartOperationFailed = false;
                ApplyAutoStartState(status.State);
            }

            DeskBox.Platform.UiDispatch.RunOrDefer(Apply);
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Startup status refresh failed: {ex.Message}");
        }
    }

    internal async Task SetAutoStartEnabledAsync(bool value)
    {
        long sequence = Interlocked.Increment(ref _autoStartOperationSequence);
        Interlocked.Increment(ref _autoStartMutationInFlight);
        try
        {
            StartupOperationResult result = await _appActions.SetAutoStartEnabledAsync(value);
            StartupOperationResult? refreshedStatus = null;
            bool failed = IsAutoStartOperationFailure(result);
            if (failed)
            {
                try
                {
                    refreshedStatus = await _appActions.GetAutoStartStatusAsync();
                }
                catch
                {
                    // Fall back to the operation result when live status lookup fails.
                }
            }

            void Apply()
            {
                if (_isDisposed || Volatile.Read(ref _autoStartOperationSequence) != sequence)
                {
                    return;
                }

                ApplyAutoStartOperationResult(result, refreshedStatus);
            }

            DeskBox.Platform.UiDispatch.RunOrDefer(Apply);
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Startup update failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _autoStartMutationInFlight);
        }
    }

    private static bool IsAutoStartOperationFailure(StartupOperationResult result) =>
        !result.UsedFallback &&
        result.State != StartupRegistrationState.DisabledByUser &&
        result.State != StartupRegistrationState.DisabledByTaskScheduler &&
        (result.State is StartupRegistrationState.BlockedOrFailed or StartupRegistrationState.PathMismatch ||
            !string.IsNullOrWhiteSpace(result.ErrorMessage));

    private void ApplyAutoStartOperationResult(
        StartupOperationResult result,
        StartupOperationResult? refreshedStatus = null)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            App.Log(
                $"[Settings] Startup state={result.State}: {result.ErrorMessage}");
        }

        _autoStartUsedFallback = result.UsedFallback;
        _autoStartOperationFailed = IsAutoStartOperationFailure(result);
        StartupRegistrationState stateToDisplay = _autoStartOperationFailed && refreshedStatus is not null
            ? refreshedStatus.State
            : result.State;
        ApplyAutoStartState(stateToDisplay);
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
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSettingsSnapshot;
        }

        OnPropertyChanged(nameof(AutoStartStatusText));
        OnPropertyChanged(nameof(AutoStartStatusVisibility));
        OnPropertyChanged(nameof(AutoStartSystemSettingsVisibility));
    }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetAutoCheckForUpdates(value);
    }

    partial void OnShowReleaseNotesAfterUpdateChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetShowReleaseNotesAfterUpdate(value);
    }

    partial void OnAutoDownloadUpdatesChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetAutoDownloadUpdates(value);
    }

    partial void OnShowUpdateToastNotificationChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _interactionSettings.SetShowUpdateToastNotification(value);
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
        _appActions.SetSnapEnabled(value);
    }

    private void OnInteractionSnapSpacingUserChanged(double value)
    {
        _appActions.SetSnapSpacingDips(value);
    }

    private void OnInteractionShowDesktopBehaviorUserChanged()
    {
        _widgetActions.RefreshVisibleWidgetDesktopLayers(
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
