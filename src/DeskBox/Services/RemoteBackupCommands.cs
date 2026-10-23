namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host backup commands over the IPC pipe (batch 3c-3a).
/// </summary>
public sealed class RemoteBackupCommands : IBackupCommands
{
    private readonly SettingsIpcClient _client;

    public RemoteBackupCommands(SettingsIpcClient client)
    {
        _client = client;
    }

    public bool IsStopping
    {
        get
        {
            (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.BackupIsStopping)
                .GetAwaiter().GetResult();
            return ok && SettingsIpcPayload.DecodeBoolean(payload);
        }
    }

    public async Task<LocalBackupResult> CreateSnapshotNowAsync(CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.BackupCreateSnapshotNow,
            cancellationToken: cancellationToken);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.LocalBackupResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "snapshot failed");
        }

        return result;
    }

    public async Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.BackupExport,
            destinationDirectory,
            cancellationToken);
        if (!ok || string.IsNullOrEmpty(payload))
        {
            throw new InvalidOperationException(error ?? "export failed");
        }

        return payload;
    }

    public async Task<CloudBackupRunResult> UploadNowAsync(CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.BackupUploadNow,
            cancellationToken: cancellationToken);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.CloudBackupRunResult)
                is not { } result)
        {
            throw new InvalidOperationException(error ?? "upload failed");
        }

        return result;
    }
}
