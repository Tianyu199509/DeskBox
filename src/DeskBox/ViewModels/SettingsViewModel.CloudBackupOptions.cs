using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>One remote snapshot row in the cloud-backup restore list.</summary>
public sealed class CloudBackupRemoteSnapshotItem
{
    internal CloudBackupRemoteSnapshotItem(string name, string title, string details)
    {
        Name = name;
        Title = title;
        Details = details;
    }

    public string Name { get; }
    public string Title { get; }
    public string Details { get; }
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

            _settingsService.Settings.CloudBackup.CloudBackupProvider = normalized;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
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

            _settingsService.Settings.CloudBackup.CloudBackupServerUrl = value ?? string.Empty;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
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

            _settingsService.Settings.CloudBackup.CloudBackupRemotePath = value ?? string.Empty;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
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

            _settingsService.Settings.CloudBackup.CloudBackupUsername = value ?? string.Empty;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
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

        _settingsService.Settings.CloudBackup.CloudBackupTodoDataEnabled = value;
        _settingsService.SaveDebounced();
        PushCloudBackupOptionsToService();
    }

    [ObservableProperty]
    public partial bool CloudBackupQuickCaptureDataEnabled { get; set; }

    partial void OnCloudBackupQuickCaptureDataEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.CloudBackup.CloudBackupQuickCaptureDataEnabled = value;
        _settingsService.SaveDebounced();
        PushCloudBackupOptionsToService();
    }

    [ObservableProperty]
    public partial bool CloudBackupWidgetStyleEnabled { get; set; }

    partial void OnCloudBackupWidgetStyleEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.CloudBackup.CloudBackupWidgetStyleEnabled = value;
        _settingsService.SaveDebounced();
        PushCloudBackupOptionsToService();
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

            _settingsService.Settings.CloudBackup.CloudBackupIntervalMinutes = normalized;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
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

            _settingsService.Settings.CloudBackup.CloudBackupRetentionCount = normalized;
            _settingsService.SaveDebounced();
            PushCloudBackupOptionsToService();
        }
    }

    // ── Status surface (written by the code-behind actions) ────────────

    private bool _cloudBackupBusy;

    /// <summary>True while a test/backup/restore round-trip is in flight.</summary>
    public bool CloudBackupBusy
    {
        get => _cloudBackupBusy;
        set
        {
            if (SetProperty(ref _cloudBackupBusy, value))
            {
                OnPropertyChanged(nameof(CloudBackupActionsEnabled));
            }
        }
    }

    /// <summary>Action buttons stay enabled only while no round-trip is in flight.</summary>
    public bool CloudBackupActionsEnabled => !_cloudBackupBusy;

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

    private bool _cloudBackupCredentialSaved;

    public string CloudBackupCredentialStatusText => _cloudBackupCredentialSaved
        ? _localizationService.T("Settings.CloudBackup.Password.Saved")
        : _localizationService.T("Settings.CloudBackup.Password.NotSaved");

    public bool CloudBackupCredentialSaved
    {
        get => _cloudBackupCredentialSaved;
        set
        {
            if (SetProperty(ref _cloudBackupCredentialSaved, value))
            {
                OnPropertyChanged(nameof(CloudBackupCredentialStatusText));
            }
        }
    }

    /// <summary>Last successful upload, formatted for the status row.</summary>
    public string CloudBackupStatusText
    {
        get
        {
            long ticks = _settingsService.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks;
            return ticks > 0
                ? _localizationService.Format(
                    "Settings.CloudBackup.LastSuccess",
                    new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime().ToString("g"))
                : _localizationService.T("Settings.CloudBackup.LastSuccess.Never");
        }
    }

    public void RefreshCloudBackupStatus() => OnPropertyChanged(nameof(CloudBackupStatusText));

    /// <summary>Remote snapshot inventory for the restore list.</summary>
    public ObservableCollection<CloudBackupRemoteSnapshotItem> CloudBackupRemoteSnapshots { get; } = [];

    private void PushCloudBackupOptionsToService()
    {
        App.Current?.CloudBackupService.UpdateOptions(
            CloudBackupSettingsPolicy.GetOptions(_settingsService.Settings));
    }
}
