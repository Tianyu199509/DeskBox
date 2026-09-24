using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Features.Backup;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private readonly BackupSettingsViewModel _backupSettings;
    private BackupEndpoint? _projectedBackupEndpoint;

    private void OnBackupSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BackupSettingsViewModel.State))
        {
            if (_projectedBackupEndpoint is not null &&
                _projectedBackupEndpoint != _backupSettings.Endpoint)
                CloudBackupConnectionStatusText = string.Empty;
            SyncBackupSettingsFacade();
            OnPropertyChanged(nameof(CloudBackupStatusText));
            OnPropertyChanged(nameof(AutomaticBackupDirectoryDisplayText));
        }
        else if (e.PropertyName == nameof(BackupSettingsViewModel.CredentialSaved))
        {
            OnPropertyChanged(nameof(CloudBackupCredentialSaved));
            OnPropertyChanged(nameof(CloudBackupCredentialStatusText));
        }
        else if (e.PropertyName == nameof(BackupSettingsViewModel.RemoteSnapshots))
        {
            SyncCloudBackupRemoteSnapshots();
            if (_backupSettings.Message.Kind == BackupPageMessageKind.None)
                CloudBackupConnectionStatusText = string.Empty;
        }
        else if (e.PropertyName == nameof(BackupSettingsViewModel.Message))
        {
            ApplyBackupSettingsMessage();
        }
        else if (e.PropertyName == nameof(BackupSettingsViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(CloudBackupBusy));
            OnPropertyChanged(nameof(CloudBackupActionsEnabled));
        }
    }

    private void SyncBackupSettingsFacade()
    {
        BackupSettingsSnapshot state = _backupSettings.State;
        _projectedBackupEndpoint = state.Endpoint;
        bool wasApplying = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            AutomaticBackupEnabled = state.LocalEnabled;
            SetProperty(ref _selectedAutomaticBackupIntervalMinutes, state.LocalIntervalMinutes,
                nameof(SelectedAutomaticBackupIntervalMinutes));
            SetProperty(ref _selectedAutomaticBackupRetentionCount, state.LocalRetentionCount,
                nameof(SelectedAutomaticBackupRetentionCount));
            AutomaticBackupDirectory = state.LocalDirectory;
            SetProperty(ref _selectedCloudBackupProvider, state.CloudProvider,
                nameof(SelectedCloudBackupProvider));
            SetProperty(ref _cloudBackupServerUrl, state.CloudServerUrl,
                nameof(CloudBackupServerUrl));
            SetProperty(ref _cloudBackupRemotePath, state.CloudRemotePath,
                nameof(CloudBackupRemotePath));
            SetProperty(ref _cloudBackupUsername, state.CloudUsername,
                nameof(CloudBackupUsername));
            CloudBackupTodoDataEnabled = state.CloudTodoEnabled;
            CloudBackupQuickCaptureDataEnabled = state.CloudQuickCaptureEnabled;
            CloudBackupWidgetStyleEnabled = state.CloudWidgetStyleEnabled;
            SetProperty(ref _selectedCloudBackupIntervalMinutes, state.CloudIntervalMinutes,
                nameof(SelectedCloudBackupIntervalMinutes));
            SetProperty(ref _selectedCloudBackupRetentionCount, state.CloudRetentionCount,
                nameof(SelectedCloudBackupRetentionCount));
        }
        finally { _isApplyingSettingsSnapshot = wasApplying; }
        OnPropertyChanged(nameof(CloudBackupWebDavVisibility));
        OnPropertyChanged(nameof(CloudBackupHttpWarningVisibility));
    }
}
