namespace DeskBox.Contracts;

/// <summary>The non-secret identity of one remote backup folder.</summary>
public sealed record BackupEndpoint(string Provider, string ServerUrl, string RemotePath, string Username);

public sealed record BackupSettingsSnapshot(
    bool LocalEnabled, int LocalIntervalMinutes, int LocalRetentionCount, string LocalDirectory,
    string EffectiveLocalDirectory, string LocalOpenDirectory, bool LocalDirectoryFallback,
    string CloudProvider, string CloudServerUrl, string CloudRemotePath, string CloudUsername,
    bool CloudTodoEnabled, bool CloudQuickCaptureEnabled, bool CloudWidgetStyleEnabled,
    int CloudIntervalMinutes, int CloudRetentionCount,
    long CloudLastSuccessUtcTicks, long CloudLastFailureUtcTicks, long CloudLastUnverifiedUtcTicks,
    BackupEndpoint Endpoint, bool HasEndpoint);

/// <summary>Only non-null members are written, so independent controls never overwrite each other.</summary>
public sealed record BackupSettingsChange(
    bool? LocalEnabled = null, int? LocalIntervalMinutes = null, int? LocalRetentionCount = null,
    string? LocalDirectory = null, string? CloudProvider = null, string? CloudServerUrl = null,
    string? CloudRemotePath = null, string? CloudUsername = null,
    bool? CloudTodoEnabled = null, bool? CloudQuickCaptureEnabled = null,
    bool? CloudWidgetStyleEnabled = null, int? CloudIntervalMinutes = null,
    int? CloudRetentionCount = null);

public sealed record BackupRemoteSnapshot(string Name, long? Length, DateTimeOffset? CreatedAtUtc);
public sealed record BackupUploadNotification(BackupEndpoint Endpoint, bool Uploaded, string? RemoteFilePath);

/// <summary>Settings editing and page reads; submitted uploads remain owned by the app runtime.</summary>
public interface IBackupSettings
{
    BackupSettingsSnapshot Read();
    void Update(BackupSettingsChange change);
    bool IsValidLocalDirectory(string path, out string? rejectionReasonKey);
    Task SaveAsync();
    Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken cancellationToken);
    Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken cancellationToken);
    Task ProbeAsync(BackupEndpoint endpoint, string? secretOverride, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(BackupEndpoint endpoint, CancellationToken cancellationToken);
    event Action<BackupUploadNotification>? UploadCompleted;
}
