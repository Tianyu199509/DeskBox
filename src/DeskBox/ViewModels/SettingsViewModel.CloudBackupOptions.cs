using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// One remote snapshot row in the cloud-backup restore list. Title/Details
/// are rendered through compiled {x:Bind} (AOT-safe); the generated
/// bindable metadata stays as a safety net for any future {Binding} use.
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CloudBackupRemoteSnapshotItem
{
    internal CloudBackupRemoteSnapshotItem(string name, string title, string details, BackupEndpoint endpoint)
    {
        Name = name;
        Title = title;
        Details = details;
        Endpoint = endpoint;
    }

    public string Name { get; }
    public string Title { get; }
    public string Details { get; }
    internal BackupEndpoint Endpoint { get; }
}

public partial class SettingsViewModel
{
    private string[] AvailableCloudBackupProviders =>
        [CloudBackupSettingsPolicy.ProviderNone, CloudBackupSettingsPolicy.ProviderWebDav];

    private string[] AvailableCloudBackupProviderDisplayNames =>
        [
            _localizationService.T("Settings.CloudBackup.Provider.Off"),
            _localizationService.T("Settings.CloudBackup.Provider.WebDav")
        ];

    public IReadOnlyList<SettingsOption> AvailableCloudBackupProviderOptions =>
        CreateSelectionOptions(AvailableCloudBackupProviders, AvailableCloudBackupProviderDisplayNames);

    private string _selectedCloudBackupProvider = CloudBackupSettingsPolicy.ProviderNone;

    public string SelectedCloudBackupProvider
    {
        get => _selectedCloudBackupProvider;
        set
        {
            string normalized = value is CloudBackupSettingsPolicy.ProviderWebDav
                ? CloudBackupSettingsPolicy.ProviderWebDav
                : CloudBackupSettingsPolicy.ProviderNone;
            if (!SetProperty(ref _selectedCloudBackupProvider, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(CloudBackupWebDavVisibility));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudProvider: normalized));
        }
    }

    /// <summary>WebDAV-only fields are hidden when the provider is off.</summary>
    public Visibility CloudBackupWebDavVisibility =>
        _selectedCloudBackupProvider == CloudBackupSettingsPolicy.ProviderWebDav
            ? Visibility.Visible
            : Visibility.Collapsed;

    private string _cloudBackupServerUrl = string.Empty;

    public string CloudBackupServerUrl
    {
        get => _cloudBackupServerUrl;
        set
        {
            if (!SetProperty(ref _cloudBackupServerUrl, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(CloudBackupHttpWarningVisibility));
            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudServerUrl: value ?? string.Empty));
        }
    }

    /// <summary>Plain-HTTP endpoints send Basic credentials on a cleartext channel.</summary>
    public string CloudBackupHttpWarningText =>
        _localizationService.T("Settings.CloudBackup.HttpWarning");

    public Visibility CloudBackupHttpWarningVisibility =>
        Uri.TryCreate(_cloudBackupServerUrl, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttp
            ? Visibility.Visible
            : Visibility.Collapsed;

    private string _cloudBackupRemotePath = "DeskBox/backups";

    public string CloudBackupRemotePath
    {
        get => _cloudBackupRemotePath;
        set
        {
            if (!SetProperty(ref _cloudBackupRemotePath, value ?? string.Empty))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudRemotePath: value ?? string.Empty));
        }
    }

    private string _cloudBackupUsername = string.Empty;

    public string CloudBackupUsername
    {
        get => _cloudBackupUsername;
        set
        {
            if (!SetProperty(ref _cloudBackupUsername, value ?? string.Empty))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudUsername: value ?? string.Empty));
        }
    }

    [ObservableProperty]
    public partial bool CloudBackupTodoDataEnabled { get; set; }

    partial void OnCloudBackupTodoDataEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _backupSettings.Update(new(CloudTodoEnabled: value));
    }

    [ObservableProperty]
    public partial bool CloudBackupQuickCaptureDataEnabled { get; set; }

    partial void OnCloudBackupQuickCaptureDataEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _backupSettings.Update(new(CloudQuickCaptureEnabled: value));
    }

    [ObservableProperty]
    public partial bool CloudBackupWidgetStyleEnabled { get; set; }

    partial void OnCloudBackupWidgetStyleEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _backupSettings.Update(new(CloudWidgetStyleEnabled: value));
    }

    public int[] AvailableCloudBackupIntervals { get; } =
        CloudBackupSettingsPolicy.SupportedIntervalMinutes;

    public int[] AvailableCloudBackupRetentionCounts { get; } =
        CloudBackupSettingsPolicy.SupportedRetentionCounts;

    private string[]? _cachedCloudBackupIntervalDisplayNames;

    public string[] AvailableCloudBackupIntervalDisplayNames =>
        _cachedCloudBackupIntervalDisplayNames ??=
            AvailableCloudBackupIntervals.Select(GetCloudBackupIntervalDisplayName).ToArray();

    private string[]? _cachedCloudBackupRetentionDisplayNames;

    public string[] AvailableCloudBackupRetentionDisplayNames =>
        _cachedCloudBackupRetentionDisplayNames ??=
            AvailableCloudBackupRetentionCounts.Select(GetCloudBackupRetentionDisplayName).ToArray();

    public IReadOnlyList<SettingsOption> AvailableCloudBackupIntervalOptions =>
        CreateSelectionOptions(AvailableCloudBackupIntervals, AvailableCloudBackupIntervalDisplayNames);

    public IReadOnlyList<SettingsOption> AvailableCloudBackupRetentionOptions =>
        CreateSelectionOptions(AvailableCloudBackupRetentionCounts, AvailableCloudBackupRetentionDisplayNames);

    private string GetCloudBackupIntervalDisplayName(int minutes) => minutes switch
    {
        60 => _localizationService.T("Settings.CloudBackup.Interval.Hour"),
        360 => _localizationService.Format("Settings.CloudBackup.Interval.Hours", 6),
        720 => _localizationService.Format("Settings.CloudBackup.Interval.Hours", 12),
        1440 => _localizationService.T("Settings.CloudBackup.Interval.Day"),
        10080 => _localizationService.Format("Settings.CloudBackup.Interval.Days", 7),
        _ => minutes.ToString()
    };

    private string GetCloudBackupRetentionDisplayName(int count) =>
        _localizationService.Format("Settings.CloudBackup.Retention.Count", count);

    private int _selectedCloudBackupIntervalMinutes = CloudBackupSettingsPolicy.DefaultIntervalMinutes;

    public int SelectedCloudBackupIntervalMinutes
    {
        get => _selectedCloudBackupIntervalMinutes;
        set
        {
            int normalized = CloudBackupSettingsPolicy.NormalizeIntervalMinutes(value);
            if (!SetProperty(ref _selectedCloudBackupIntervalMinutes, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudIntervalMinutes: normalized));
        }
    }

    private int _selectedCloudBackupRetentionCount = CloudBackupSettingsPolicy.DefaultRetentionCount;

    public int SelectedCloudBackupRetentionCount
    {
        get => _selectedCloudBackupRetentionCount;
        set
        {
            int normalized = CloudBackupSettingsPolicy.NormalizeRetentionCount(value);
            if (!SetProperty(ref _selectedCloudBackupRetentionCount, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(CloudRetentionCount: normalized));
        }
    }

    // The shell keeps established XAML names; the backup editor owns page reads.

    private bool _cloudBackupBusy;

    /// <summary>True while a test/backup/restore round-trip is in flight.</summary>
    public bool CloudBackupBusy
    {
        get => _cloudBackupBusy || _backupSettings.IsBusy;
        set
        {
            if (SetProperty(ref _cloudBackupBusy, value))
            {
                OnPropertyChanged(nameof(CloudBackupActionsEnabled));
            }
        }
    }

    /// <summary>Action buttons stay enabled only while no round-trip is in flight.</summary>
    public bool CloudBackupActionsEnabled => !CloudBackupBusy;

    private string _cloudBackupConnectionStatusText = string.Empty;

    public string CloudBackupConnectionStatusText
    {
        get => _cloudBackupConnectionStatusText;
        set
        {
            if (SetProperty(ref _cloudBackupConnectionStatusText, value))
            {
                OnPropertyChanged(nameof(CloudBackupConnectionStatusVisibility));
            }
        }
    }

    public Visibility CloudBackupConnectionStatusVisibility =>
        string.IsNullOrEmpty(_cloudBackupConnectionStatusText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string CloudBackupCredentialStatusText => _backupSettings.CredentialSaved
        ? _localizationService.T("Settings.CloudBackup.Password.Saved")
        : _localizationService.T("Settings.CloudBackup.Password.NotSaved");

    public bool CloudBackupCredentialSaved => _backupSettings.CredentialSaved;

    /// <summary>Last successful upload — plus the latest failure when it is newer, so a silently-broken scheduled backup can't hide behind a stale success.</summary>
    public string CloudBackupStatusText
    {
        get
        {
            long successTicks = _backupSettings.State.CloudLastSuccessUtcTicks;
            long failureTicks = _backupSettings.State.CloudLastFailureUtcTicks;
            string status = successTicks > 0
                ? _localizationService.Format(
                    "Settings.CloudBackup.LastSuccess",
                    new DateTimeOffset(successTicks, TimeSpan.Zero).ToLocalTime().ToString("g"))
                : _localizationService.T("Settings.CloudBackup.LastSuccess.Never");

            if (failureTicks > successTicks)
            {
                status += " · " + _localizationService.Format(
                    "Settings.CloudBackup.LastFailure",
                    new DateTimeOffset(failureTicks, TimeSpan.Zero).ToLocalTime().ToString("g"));
            }

            // An accepted-but-never-listed upload is stamped separately: it
            // is not a failure, yet the snapshot may never have landed —
            // show it alongside the success instead of hiding behind it.
            long unverifiedTicks = _backupSettings.State.CloudLastUnverifiedUtcTicks;
            if (unverifiedTicks > 0)
            {
                status += " · " + _localizationService.Format(
                    "Settings.CloudBackup.LastUnverified",
                    new DateTimeOffset(unverifiedTicks, TimeSpan.Zero).ToLocalTime().ToString("g"));
            }

            return status;
        }
    }

    public void RefreshCloudBackupStatus()
    {
        _backupSettings.RefreshState();
        OnPropertyChanged(nameof(CloudBackupStatusText));
    }

    /// <summary>Remote snapshot inventory for the restore list.</summary>
    public ObservableCollection<CloudBackupRemoteSnapshotItem> CloudBackupRemoteSnapshots { get; } = [];

    internal long CloudBackupEndpointGeneration => _backupSettings.EndpointGeneration;

    private void ApplyBackupSettingsMessage()
    {
        var message = _backupSettings.Message;
        CloudBackupConnectionStatusText = message.Kind switch
        {
            Features.Backup.BackupPageMessageKind.PasswordSaved =>
                _localizationService.T("Settings.CloudBackup.Password.Saved"),
            Features.Backup.BackupPageMessageKind.PasswordMissing =>
                _localizationService.T("Settings.CloudBackup.Password.NotSaved"),
            Features.Backup.BackupPageMessageKind.PasswordSaveFailed =>
                _localizationService.Format("Settings.CloudBackup.Password.SaveFailed", message.Error ?? string.Empty),
            Features.Backup.BackupPageMessageKind.ProbeSucceeded =>
                _localizationService.T("Settings.CloudBackup.TestConnection.Success"),
            Features.Backup.BackupPageMessageKind.ProbeFailed =>
                _localizationService.Format("Settings.CloudBackup.TestConnection.Failed", message.Error ?? string.Empty),
            Features.Backup.BackupPageMessageKind.ListFailed =>
                _localizationService.Format("Settings.CloudBackup.RefreshSnapshots.Failed", message.Error ?? string.Empty),
            Features.Backup.BackupPageMessageKind.NotYetVisible =>
                _localizationService.T("Settings.CloudBackup.SnapshotList.NotYetVisible"),
            _ => string.Empty
        };
    }

    private void SyncCloudBackupRemoteSnapshots()
    {
        CloudBackupRemoteSnapshots.Clear();
        BackupEndpoint endpoint = _backupSettings.Endpoint;
        foreach (BackupRemoteSnapshot item in _backupSettings.RemoteSnapshots)
        {
            string title = item.Name;
            string details = item.Name;
            if (item.CreatedAtUtc is { } createdUtc)
            {
                title = createdUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                string stem = item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? item.Name[..^4] : item.Name;
                string tail = stem.Split('-').Last();
                string device = tail.Length == 8 ? tail : item.Name;
                string size = item.Length is { } length ? $" · {FormatBytes(length)}" : string.Empty;
                details = _localizationService.Format("Settings.CloudBackup.SnapshotDetails", device, size);
            }
            CloudBackupRemoteSnapshots.Add(new(item.Name, title, details, endpoint));
        }
    }
}
