namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host restore facade over the IPC pipe (batch 3c-2).
/// </summary>
public sealed class RemoteSettingsRestoreActions : ISettingsRestoreActions
{
    private readonly SettingsIpcClient _client;

    public RemoteSettingsRestoreActions(SettingsIpcClient client)
    {
        _client = client;
    }

    public async Task<DeskBoxRestorePreparation> PrepareRestoreAsync(string archivePath)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.RestorePrepare,
            archivePath);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DeskBoxRestorePreparation)
                is not { } preparation)
        {
            // Failures surface as exceptions, matching the in-process
            // service whose throws the window's catch blocks already handle.
            throw new InvalidOperationException(error ?? "restore preparation failed");
        }

        return preparation;
    }

    public Task<bool> ConfirmPendingFullRestoreAsync() =>
        RequestBoolAsync(SettingsIpcMethods.RestoreConfirm);

    public Task CancelPendingRestoreAsync() =>
        RequestVoidAsync(SettingsIpcMethods.RestoreCancel);

    public async Task<IReadOnlyList<DeskBoxBackupSnapshotInfo>> GetSnapshotInventoryAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.RestoreInventory);
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListDeskBoxBackupSnapshotInfo)
            is { } inventory
                ? inventory
                : [];
    }

    public async Task DeleteSnapshotAsync(string snapshotPath)
    {
        // Deep review R13-2 (D18 audit): destructive delete — a server-side
        // refusal must throw like the in-process service instead of being
        // swallowed by the void-request helper.
        (bool ok, _, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.RestoreDeleteSnapshot,
            snapshotPath);
        if (!ok)
        {
            throw new InvalidOperationException(error ?? "snapshot delete failed");
        }
    }

    public async Task<DeskBoxAttachmentHealthReport> ScanAttachmentHealthAsync()
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(SettingsIpcMethods.RestoreAttachmentHealth);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DeskBoxAttachmentHealthReport)
                is not { } report)
        {
            throw new InvalidOperationException(error ?? "attachment health scan failed");
        }

        return report;
    }

    private async Task<bool> RequestBoolAsync(string method)
    {
        (bool ok, _, _) = await _client.RequestAsync(method);
        return ok;
    }

    private Task RequestVoidAsync(string method, string? payload = null) =>
        SettingsIpcShell.RequestVoidAsync(_client, method, payload);
}
