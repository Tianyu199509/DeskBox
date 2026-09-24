using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public int[] AvailableAutomaticBackupIntervals { get; } =
        DataBackupSettingsPolicy.SupportedIntervalMinutes;

    public int[] AvailableAutomaticBackupRetentionCounts { get; } =
        DataBackupSettingsPolicy.SupportedRetentionCounts;

    private string[]? _cachedAutomaticBackupIntervalDisplayNames;

    public string[] AvailableAutomaticBackupIntervalDisplayNames =>
        _cachedAutomaticBackupIntervalDisplayNames ??=
            AvailableAutomaticBackupIntervals.Select(GetAutomaticBackupIntervalDisplayName).ToArray();

    private string[]? _cachedAutomaticBackupRetentionDisplayNames;

    public string[] AvailableAutomaticBackupRetentionDisplayNames =>
        _cachedAutomaticBackupRetentionDisplayNames ??=
            AvailableAutomaticBackupRetentionCounts.Select(GetAutomaticBackupRetentionDisplayName).ToArray();

    private string GetAutomaticBackupIntervalDisplayName(int minutes) => minutes switch
    {
        5 or 30 => _localizationService.Format("Settings.DataBackup.Interval.Minutes", minutes),
        60 => _localizationService.T("Settings.DataBackup.Interval.Hour"),
        720 => _localizationService.Format("Settings.DataBackup.Interval.Hours", 12),
        1440 => _localizationService.T("Settings.DataBackup.Interval.Day"),
        7200 => _localizationService.Format("Settings.DataBackup.Interval.Days", 5),
        _ => minutes.ToString()
    };

    private string GetAutomaticBackupRetentionDisplayName(int count) =>
        _localizationService.Format("Settings.DataBackup.Retention.Count", count);

    [ObservableProperty]
    public partial bool AutomaticBackupEnabled { get; set; } = DataBackupSettingsPolicy.DefaultEnabled;

    partial void OnAutomaticBackupEnabledChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _backupSettings.Update(new(LocalEnabled: value));
    }

    private int _selectedAutomaticBackupIntervalMinutes = DataBackupSettingsPolicy.DefaultIntervalMinutes;

    public int SelectedAutomaticBackupIntervalMinutes
    {
        get => _selectedAutomaticBackupIntervalMinutes;
        set
        {
            int normalized = DataBackupSettingsPolicy.NormalizeIntervalMinutes(value);
            if (!SetProperty(ref _selectedAutomaticBackupIntervalMinutes, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(LocalIntervalMinutes: normalized));
        }
    }

    private int _selectedAutomaticBackupRetentionCount = DataBackupSettingsPolicy.DefaultRetentionCount;

    public int SelectedAutomaticBackupRetentionCount
    {
        get => _selectedAutomaticBackupRetentionCount;
        set
        {
            int normalized = DataBackupSettingsPolicy.NormalizeRetentionCount(value);
            if (!SetProperty(ref _selectedAutomaticBackupRetentionCount, normalized))
            {
                return;
            }

            if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
            {
                return;
            }

            _backupSettings.Update(new(LocalRetentionCount: normalized));
        }
    }

    private string _automaticBackupDirectory = string.Empty;

    /// <summary>Configured custom snapshot folder; empty means the default recovery folder.</summary>
    public string AutomaticBackupDirectory
    {
        get => _automaticBackupDirectory;
        private set => SetProperty(ref _automaticBackupDirectory, value);
    }

    /// <summary>Folder shown in the UI: the configured folder, or the effective default one.</summary>
    public string AutomaticBackupDirectoryDisplayText =>
        _automaticBackupDirectory.Length > 0
            ? _automaticBackupDirectory
            : _backupSettings.State.EffectiveLocalDirectory;

    private string _automaticBackupFallbackWarningText = string.Empty;

    public string AutomaticBackupFallbackWarningText => _automaticBackupFallbackWarningText;

    public Visibility AutomaticBackupFallbackWarningVisibility =>
        string.IsNullOrEmpty(_automaticBackupFallbackWarningText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public void UpdateAutomaticBackupDirectory(string path)
    {
        string normalizedDirectory =
            DataBackupSettingsPolicy.NormalizeCustomDirectory(path) ?? string.Empty;
        _backupSettings.Update(new(LocalDirectory: normalizedDirectory));
        RefreshAutomaticBackupStatus();
    }

    /// <summary>
    /// Re-reads the effective snapshot folder from the backup service and
    /// updates the fallback warning shown in the backup settings section.
    /// </summary>
    public void RefreshAutomaticBackupStatus()
    {
        _backupSettings.RefreshState();
        OnPropertyChanged(nameof(AutomaticBackupDirectoryDisplayText));
        BackupSettingsSnapshot state = _backupSettings.State;
        _automaticBackupFallbackWarningText = state.LocalDirectoryFallback
            ? _localizationService.Format(
                "Settings.DataBackup.AutomaticBackupDirectory.FallbackWarning",
                state.EffectiveLocalDirectory)
            : string.Empty;
        OnPropertyChanged(nameof(AutomaticBackupFallbackWarningText));
        OnPropertyChanged(nameof(AutomaticBackupFallbackWarningVisibility));
    }

    public bool IsValidAutomaticBackupDirectory(string path, out string? rejectionReasonKey) =>
        _backupSettings.IsValidLocalDirectory(path, out rejectionReasonKey);
}
