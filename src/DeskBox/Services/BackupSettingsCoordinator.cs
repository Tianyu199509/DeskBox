using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>Adapts the persisted backup slice and borrowed backup services to the settings editor.</summary>
public sealed class BackupSettingsCoordinator : IBackupSettings, IDisposable
{
    private readonly SettingsService _settings;
    private readonly DeskBoxDataBackupService _local;
    private readonly CloudBackupService _cloud;
    private readonly BackupRestoreActions _restoreActions;
    private readonly Action _refreshRuntimeOptions;
    private bool _disposed;

    internal BackupSettingsCoordinator(SettingsService settings, DeskBoxDataBackupService local,
        CloudBackupService cloud, BackupRestoreActions restoreActions, Action refreshRuntimeOptions)
    {
        _settings = settings;
        _local = local;
        _cloud = cloud;
        _restoreActions = restoreActions;
        _refreshRuntimeOptions = refreshRuntimeOptions;
        _cloud.BackupRunCompleted += OnUploadCompleted;
    }

    public event Action<BackupUploadNotification>? UploadCompleted;

    public BackupSettingsSnapshot Read()
    {
        AppSettings settings = _settings.Settings;
        CloudBackupSettingsSlice cloud = settings.CloudBackup;
        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(settings);
        AutomaticBackupDirectoryStatus local = _local.GetAutomaticBackupDirectoryStatus();
        return new(
            settings.Backup.AutomaticBackupEnabled,
            DataBackupSettingsPolicy.NormalizeIntervalMinutes(settings.Backup.AutomaticBackupIntervalMinutes),
            DataBackupSettingsPolicy.NormalizeRetentionCount(settings.Backup.AutomaticBackupRetentionCount),
            DataBackupSettingsPolicy.NormalizeCustomDirectory(settings.Backup.AutomaticBackupDirectory) ?? string.Empty,
            local.EffectiveDirectory,
            local.IsCustomDirectoryActive ? local.EffectiveDirectory :
                Path.GetDirectoryName(_local.AutomaticSnapshotDirectory) ?? _local.AutomaticSnapshotDirectory,
            local.ConfiguredDirectory is not null &&
            (!local.IsCustomDirectoryActive || _local.LastAutomaticSnapshotFallbackMessage is not null),
            cloud.CloudBackupProvider is CloudBackupSettingsPolicy.ProviderWebDav
                ? CloudBackupSettingsPolicy.ProviderWebDav : CloudBackupSettingsPolicy.ProviderNone,
            cloud.CloudBackupServerUrl ?? string.Empty,
            cloud.CloudBackupRemotePath ?? string.Empty,
            cloud.CloudBackupUsername ?? string.Empty,
            cloud.CloudBackupTodoDataEnabled,
            cloud.CloudBackupQuickCaptureDataEnabled,
            cloud.CloudBackupWidgetStyleEnabled,
            CloudBackupSettingsPolicy.NormalizeIntervalMinutes(cloud.CloudBackupIntervalMinutes),
            CloudBackupSettingsPolicy.NormalizeRetentionCount(cloud.CloudBackupRetentionCount),
            cloud.CloudBackupLastSuccessUtcTicks, cloud.CloudBackupLastFailureUtcTicks,
            cloud.CloudBackupLastUnverifiedUtcTicks,
            options.Endpoint, options.HasEndpoint);
    }

    public void Update(BackupSettingsChange change)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppSettings settings = _settings.Settings;
        CloudBackupSettingsSlice cloud = settings.CloudBackup;
        bool changed = false;
        if (change.LocalEnabled is { } localEnabled && settings.Backup.AutomaticBackupEnabled != localEnabled)
            { settings.Backup.AutomaticBackupEnabled = localEnabled; changed = true; }
        if (change.LocalIntervalMinutes is { } localInterval)
        {
            int value = DataBackupSettingsPolicy.NormalizeIntervalMinutes(localInterval);
            if (settings.Backup.AutomaticBackupIntervalMinutes != value)
                { settings.Backup.AutomaticBackupIntervalMinutes = value; changed = true; }
        }
        if (change.LocalRetentionCount is { } localRetention)
        {
            int value = DataBackupSettingsPolicy.NormalizeRetentionCount(localRetention);
            if (settings.Backup.AutomaticBackupRetentionCount != value)
                { settings.Backup.AutomaticBackupRetentionCount = value; changed = true; }
        }
        if (change.LocalDirectory is { } localDirectory)
        {
            string value = DataBackupSettingsPolicy.NormalizeCustomDirectory(localDirectory) ?? string.Empty;
            if (settings.Backup.AutomaticBackupDirectory != value)
                { settings.Backup.AutomaticBackupDirectory = value; changed = true; }
        }
        if (change.CloudProvider is { } provider)
        {
            string value = provider is CloudBackupSettingsPolicy.ProviderWebDav
                ? CloudBackupSettingsPolicy.ProviderWebDav : CloudBackupSettingsPolicy.ProviderNone;
            if (cloud.CloudBackupProvider != value) { cloud.CloudBackupProvider = value; changed = true; }
        }
        if (change.CloudServerUrl is { } url && cloud.CloudBackupServerUrl != url)
            { cloud.CloudBackupServerUrl = url; changed = true; }
        if (change.CloudRemotePath is { } path && cloud.CloudBackupRemotePath != path)
            { cloud.CloudBackupRemotePath = path; changed = true; }
        if (change.CloudUsername is { } username && cloud.CloudBackupUsername != username)
            { cloud.CloudBackupUsername = username; changed = true; }
        if (change.CloudTodoEnabled is { } todo && cloud.CloudBackupTodoDataEnabled != todo)
            { cloud.CloudBackupTodoDataEnabled = todo; changed = true; }
        if (change.CloudQuickCaptureEnabled is { } quick && cloud.CloudBackupQuickCaptureDataEnabled != quick)
            { cloud.CloudBackupQuickCaptureDataEnabled = quick; changed = true; }
        if (change.CloudWidgetStyleEnabled is { } style && cloud.CloudBackupWidgetStyleEnabled != style)
            { cloud.CloudBackupWidgetStyleEnabled = style; changed = true; }
        if (change.CloudIntervalMinutes is { } interval)
        {
            int value = CloudBackupSettingsPolicy.NormalizeIntervalMinutes(interval);
            if (cloud.CloudBackupIntervalMinutes != value) { cloud.CloudBackupIntervalMinutes = value; changed = true; }
        }
        if (change.CloudRetentionCount is { } retention)
        {
            int value = CloudBackupSettingsPolicy.NormalizeRetentionCount(retention);
            if (cloud.CloudBackupRetentionCount != value) { cloud.CloudBackupRetentionCount = value; changed = true; }
        }
        if (!changed) return;
        _settings.SaveDebounced();
        _refreshRuntimeOptions();
    }

    public bool IsValidLocalDirectory(string path, out string? rejectionReasonKey) =>
        _local.IsValidCustomAutomaticBackupDirectory(path, out rejectionReasonKey);

    public Task SaveAsync() => _settings.SaveAsync();

    private CloudBackupOptions CurrentOptions(BackupEndpoint endpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(_settings.Settings);
        if (options.Endpoint != endpoint) throw new OperationCanceledException("The backup endpoint changed.");
        return options;
    }

    public Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken cancellationToken) =>
        _cloud.HasCredentialAsync(CurrentOptions(endpoint), cancellationToken);

    public Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken cancellationToken) =>
        _cloud.SaveCredentialAsync(CurrentOptions(endpoint), secret, cancellationToken);

    public Task ProbeAsync(BackupEndpoint endpoint, string? secretOverride, CancellationToken cancellationToken) =>
        _cloud.ProbeConnectionAsync(CurrentOptions(endpoint), secretOverride, cancellationToken);

    /// <summary>
    /// Remote inventory routed through <see cref="BackupRestoreActions"/>:
    /// same endpoint capture and shutdown freeze as delete/download, then
    /// mapped to the picker's snapshot rows.
    /// </summary>
    public async Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(
        BackupEndpoint endpoint, CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudBackupRemoteEntry> entries = await _restoreActions.ListSnapshotsAsync(
            endpoint, cancellationToken);
        return entries.Select(entry => new BackupRemoteSnapshot(entry.Name, entry.Length,
            CloudBackupService.ParseSnapshotTimestamp(entry.Name))).ToArray();
    }

    private void OnUploadCompleted(CloudBackupRunCompletedInfo info)
    {
        if (!_disposed && info.Endpoint is { } endpoint)
            UploadCompleted?.Invoke(new(endpoint, info.Uploaded, info.RemoteFilePath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cloud.BackupRunCompleted -= OnUploadCompleted;
        UploadCompleted = null;
    }
}
