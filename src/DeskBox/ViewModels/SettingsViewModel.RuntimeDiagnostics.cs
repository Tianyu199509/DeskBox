using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Runtime-health diagnostics, batch 49 form: the snapshot computation and
/// the resync flow (App-level external state recovery) stay on the shell;
/// the localized summary/detail lines are pushed into the backup settings
/// editor (<see cref="Features.Backup.BackupSettingsViewModel"/>) which
/// owns the compatibility-diagnostics section's XAML binding surface.
/// </summary>
public partial class SettingsViewModel
{
    public void RefreshRuntimeDiagnostics()
    {
        AppRuntimeHealthSnapshot? snapshot = _appActions.GetRuntimeHealthSnapshot();
        if (snapshot is null)
        {
            _backupSettings.SetRuntimeHealth(
                _localizationService.T("Settings.RuntimeHealth.Unavailable"),
                string.Empty);
            return;
        }

        bool everythingEnabled = _settingsService.Settings.SearchEverythingEnabled;
        bool everythingChecking = everythingEnabled &&
            snapshot.EverythingState == EverythingConnectionState.Checking;
        bool everythingNeedsAttention = everythingEnabled &&
            snapshot.EverythingState != EverythingConnectionState.Connected &&
            !everythingChecking;

        string summaryText = everythingChecking
            ? _localizationService.T("Settings.RuntimeHealth.Summary.Scanning")
            : everythingNeedsAttention ||
              snapshot.OfflineFolderCount > 0 ||
              snapshot.DegradedFolderCount > 0 ||
              snapshot.AccessDeniedFolderCount > 0
                ? _localizationService.T("Settings.RuntimeHealth.Summary.Degraded")
                : _localizationService.T("Settings.RuntimeHealth.Summary.Healthy");

        string lastLifecycle = snapshot.LastLifecycleEventAt is { } lifecycleAt
            ? lifecycleAt.ToLocalTime().ToString("g")
            : _localizationService.T("Settings.RuntimeHealth.Never");
        string everythingStatus = !everythingEnabled
            ? _localizationService.T("Settings.Search.Everything.Status.NotConfirmed")
            : snapshot.EverythingState switch
            {
                EverythingConnectionState.Checking =>
                    _localizationService.T("Settings.Search.Everything.Status.Checking"),
                EverythingConnectionState.NotInstalled =>
                    _localizationService.T("Settings.Search.Everything.Status.NotInstalled"),
                EverythingConnectionState.NotRunning =>
                    _localizationService.T("Settings.Search.Everything.Status.NotRunning"),
                EverythingConnectionState.PermissionMismatch =>
                    _localizationService.T("Settings.Search.Everything.Status.PermissionMismatch"),
                EverythingConnectionState.IpcUnavailable =>
                    _localizationService.T("Settings.Search.Everything.Status.IpcUnavailable"),
                EverythingConnectionState.SdkUnavailable =>
                    _localizationService.T("Settings.Search.Everything.Status.SdkUnavailable"),
                EverythingConnectionState.Connected => _localizationService.Format(
                    "Settings.Search.Everything.Status.Connected",
                    snapshot.EverythingVersion ??
                    _localizationService.T("Settings.Search.Everything.VersionUnknown")),
                EverythingConnectionState.Error =>
                    _localizationService.T("Settings.Search.Everything.Status.Error"),
                _ => _localizationService.T("Settings.Search.Everything.Status.Unknown")
            };

        string detailText = _localizationService.Format(
            "Settings.RuntimeHealth.Detail",
            snapshot.LifecycleEventCount,
            lastLifecycle,
            everythingStatus,
            snapshot.OfflineFolderCount,
            snapshot.DegradedFolderCount,
            snapshot.AccessDeniedFolderCount,
            string.IsNullOrWhiteSpace(snapshot.LastLifecycleReason)
                ? _localizationService.T("Settings.RuntimeHealth.Never")
                : snapshot.LastLifecycleReason);

        _backupSettings.SetRuntimeHealth(summaryText, detailText);
    }

    public async Task ResyncRuntimeStateAsync()
    {
        if (!_backupSettings.CanResyncRuntime)
        {
            return;
        }

        _backupSettings.SetRuntimeResyncBusy(true);
        try
        {
            await _appActions.ForceExternalStateRecoveryAsync();

            RefreshRuntimeDiagnostics();
        }
        finally
        {
            _backupSettings.SetRuntimeResyncBusy(false);
        }
    }
}
