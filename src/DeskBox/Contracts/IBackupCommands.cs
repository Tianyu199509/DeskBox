namespace DeskBox.Contracts;

public enum BackupOutcome { Succeeded, Skipped, AlreadyRunning, Canceled, Failed }
public enum BackupWorkKind { Local, Cloud }
public sealed record LocalBackupResult(BackupOutcome Outcome, string? ArchivePath = null, string? Error = null);
public sealed record BackupCompletion(BackupWorkKind Kind, bool Scheduled, BackupOutcome Outcome, string? Error = null);

public sealed record CloudBackupRunResult(
    bool Uploaded,
    string? RemoteFilePath,
    int PrunedCount,
    bool NoCredential = false,
    bool RestorePending = false,
    bool NoScopeSelected = false,
    bool AlreadyInProgress = false,
    bool UploadUnverified = false,
    bool Failed = false)
{
    public BackupOutcome Outcome => Uploaded ? BackupOutcome.Succeeded :
        AlreadyInProgress ? BackupOutcome.AlreadyRunning : Failed ? BackupOutcome.Failed : BackupOutcome.Skipped;
    internal static readonly CloudBackupRunResult NotConfigured = new(false, null, 0);
    internal static readonly CloudBackupRunResult NoScope = new(false, null, 0, NoScopeSelected: true);
    internal static readonly CloudBackupRunResult MissingCredential = new(false, null, 0, NoCredential: true);
    internal static readonly CloudBackupRunResult PendingRestore = new(false, null, 0, RestorePending: true);
    internal static readonly CloudBackupRunResult InProgress = new(false, null, 0, AlreadyInProgress: true);
    internal static readonly CloudBackupRunResult NotDue = new(false, null, 0);
    internal static readonly CloudBackupRunResult Failure = new(false, null, 0, Failed: true);
}

public interface IBackupCommands
{
    bool IsStopping { get; }
    Task<LocalBackupResult> CreateSnapshotNowAsync(CancellationToken cancellationToken = default);
    Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken = default, bool includeClipboard = false);
    Task<CloudBackupRunResult> UploadNowAsync(CancellationToken cancellationToken = default);
}

public interface IBackupBackend
{
    bool LocalScheduleEnabled { get; }
    bool CloudScheduleEnabled { get; }

    /// <summary>
    /// Re-reads effective options from settings and pushes them to the local
    /// and cloud services. Returns whether the effective scheduling options
    /// actually changed, so callers can skip redundant schedule evaluations.
    /// </summary>
    bool RefreshOptions();
    Task PrepareManualBackupAsync(CancellationToken cancellationToken);
    Task<LocalBackupResult> CreateSnapshotAsync(bool force, CancellationToken cancellationToken);
    Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken, bool includeClipboard = false);
    Task<CloudBackupRunResult> UploadAsync(bool scheduled, CancellationToken cancellationToken);
}

/// <summary>UI-thread-owned timer. Only the backup runtime starts and stops it.</summary>
public interface IBackupTimer : IDisposable
{
    event Action? Tick;
    void Start();
    void Stop();
}
