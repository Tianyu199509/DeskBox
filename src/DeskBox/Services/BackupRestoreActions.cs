using DeskBox.Contracts;

namespace DeskBox.Services;

/// <summary>Explicit boundary for destructive remote actions and the staged restore transaction.</summary>
public sealed class BackupRestoreActions
{
    private readonly SettingsService _settings;
    private readonly CloudBackupService _cloud;
    private readonly DeskBoxDataBackupService _local;
    private readonly Func<Task> _shutdownForRestart;

    internal BackupRestoreActions(SettingsService settings, CloudBackupService cloud,
        DeskBoxDataBackupService local, Func<Task> shutdownForRestart)
    {
        _settings = settings;
        _cloud = cloud;
        _local = local;
        _shutdownForRestart = shutdownForRestart;
    }

    public bool IsCurrentEndpoint(BackupEndpoint endpoint) =>
        CloudBackupSettingsPolicy.GetOptions(_settings.Settings).Endpoint == endpoint;

    private CloudBackupOptions CaptureCurrent(BackupEndpoint endpoint)
    {
        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(_settings.Settings);
        if (options.Endpoint != endpoint)
            throw new OperationCanceledException("The backup endpoint changed.");
        return options;
    }

    public Task DeleteSnapshotAsync(BackupEndpoint endpoint, string name,
        CancellationToken cancellationToken = default) =>
        _cloud.DeleteRemoteSnapshotAsync(CaptureCurrent(endpoint), name, cancellationToken);

    public Task<string> DownloadSnapshotAsync(BackupEndpoint endpoint, string name, string directory,
        CancellationToken cancellationToken = default) =>
        _cloud.DownloadSnapshotAsync(CaptureCurrent(endpoint), name, directory, cancellationToken);

    public Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(string archivePath,
        CloudBackupDomain scope, CancellationToken cancellationToken = default) =>
        _local.PrepareScopedRestoreAsync(archivePath, scope, cancellationToken);

    public Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default) =>
        _local.CancelPendingRestoreAsync(cancellationToken);

    public Task<bool> SetPendingRestoreItemReplaceModeAsync(bool replace,
        CancellationToken cancellationToken = default) =>
        _local.SetPendingRestoreItemReplaceModeAsync(replace, cancellationToken);

    public AppRelaunchScheduleResult ScheduleRelaunch() =>
        AppRelaunchService.ScheduleAfterCurrentProcessExit();

    public Task ShutdownForRestartAsync() => _shutdownForRestart();

    public void DeleteTemporaryDownload(string? directory)
    {
        if (directory is not null) CloudBackupService.TryDeleteDirectory(directory);
    }
}
