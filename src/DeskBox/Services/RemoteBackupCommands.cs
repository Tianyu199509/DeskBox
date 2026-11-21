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

    // Batch R11: every button guard and catch filter used to cost a full
    // synchronous pipe roundtrip on the UI thread — and one click evaluates
    // the guard two or three times. A short read-through cache serves the
    // burst from one roundtrip; "stop in progress" tolerates a 200ms-stale
    // answer, and each new click re-reads fresh.
    private long _isStoppingCacheTicks;
    private bool _isStoppingCacheValue;

    public bool IsStopping
    {
        get
        {
            long now = Environment.TickCount64;
            if (now - _isStoppingCacheTicks < 200)
            {
                return _isStoppingCacheValue;
            }

            (bool ok, string? payload, _) = SettingsIpcShell.RequestSyncSafe(_client, SettingsIpcMethods.BackupIsStopping);
            _isStoppingCacheTicks = now;
            _isStoppingCacheValue = ok && SettingsIpcPayload.DecodeBoolean(payload);
            return _isStoppingCacheValue;
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

    public async Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken = default, bool includeClipboard = false)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", destinationDirectory);
            writer.WriteBoolean("includeClipboard", includeClipboard);
            writer.WriteEndObject();
        }

        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.BackupExport,
            System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
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
