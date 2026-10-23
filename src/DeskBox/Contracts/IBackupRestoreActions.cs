namespace DeskBox.Contracts;

using DeskBox.Services;

/// <summary>
/// The cloud-backup page's destructive-action surface (batch 3c-3a): the
/// staged restore transaction, remote snapshot operations, and shutdown
/// coordination consumed by the settings window. Extracted from the concrete
/// <see cref="BackupRestoreActions"/> so a settings host in its own process
/// gets a remote implementation with identical semantics.
/// </summary>
public interface IBackupRestoreActions
{
    Task StopAsync();

    bool IsCurrentEndpoint(BackupEndpoint endpoint);

    Task DeleteSnapshotAsync(
        BackupEndpoint endpoint,
        string name,
        CancellationToken cancellationToken = default);

    Task<string> DownloadSnapshotAsync(
        BackupEndpoint endpoint,
        string name,
        string directory,
        CancellationToken cancellationToken = default);

    Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(
        string archivePath,
        CloudBackupDomain scope,
        CancellationToken cancellationToken = default);

    Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default);

    Task<bool> SetPendingRestoreItemReplaceModeAsync(
        bool replace,
        CancellationToken cancellationToken = default);

    AppRelaunchScheduleResult ScheduleRelaunch();

    Task ShutdownForRestartAsync();

    void DeleteTemporaryDownload(string? directory);
}
