namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host cloud-restore actions over the IPC pipe (batch 3c-3a).
/// </summary>
public sealed class RemoteBackupRestoreActions : IBackupRestoreActions
{
    private readonly SettingsIpcClient _client;

    public RemoteBackupRestoreActions(SettingsIpcClient client)
    {
        _client = client;
    }

    public Task StopAsync() =>
        RequestVoidAsync(SettingsIpcMethods.CloudStop);

    public bool IsCurrentEndpoint(BackupEndpoint endpoint)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.CloudIsCurrentEndpoint,
                SettingsIpcPayload.Encode(endpoint, SettingsIpcJsonContext.Default.BackupEndpoint))
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    public Task DeleteSnapshotAsync(
        BackupEndpoint endpoint,
        string name,
        CancellationToken cancellationToken = default) =>
        RequestVoidAsync(
            SettingsIpcMethods.CloudDeleteSnapshot,
            SettingsIpcPayload.Encode(
                new SettingsIpcEndpointName(endpoint, name),
                SettingsIpcJsonContext.Default.SettingsIpcEndpointName),
            cancellationToken);

    public async Task<string> DownloadSnapshotAsync(
        BackupEndpoint endpoint,
        string name,
        string directory,
        CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.CloudDownloadSnapshot,
            SettingsIpcPayload.Encode(
                new SettingsIpcEndpointName(endpoint, name, directory),
                SettingsIpcJsonContext.Default.SettingsIpcEndpointName),
            cancellationToken);
        if (!ok || string.IsNullOrEmpty(payload))
        {
            throw new InvalidOperationException(error ?? "download failed");
        }

        return payload;
    }

    public async Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(
        string archivePath,
        CloudBackupDomain scope,
        CancellationToken cancellationToken = default)
    {
        (bool ok, string? payload, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.CloudPrepareScopedRestore,
            SettingsIpcPayload.Encode(
                new SettingsIpcScopedRestore(archivePath, (int)scope),
                SettingsIpcJsonContext.Default.SettingsIpcScopedRestore),
            cancellationToken);
        if (!ok ||
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DeskBoxRestorePreparation)
                is not { } preparation)
        {
            throw new InvalidOperationException(error ?? "scoped restore preparation failed");
        }

        return preparation;
    }

    public Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default) =>
        RequestVoidAsync(SettingsIpcMethods.CloudCancelPendingRestore, cancellationToken: cancellationToken);

    public async Task<bool> SetPendingRestoreItemReplaceModeAsync(
        bool replace,
        CancellationToken cancellationToken = default)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.CloudSetReplaceMode,
            SettingsIpcPayload.EncodeBoolean(replace),
            cancellationToken);
        return ok;
    }

    public AppRelaunchScheduleResult ScheduleRelaunch()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.CloudScheduleRelaunch)
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.AppRelaunchScheduleResult)
            is { } schedule
                ? schedule
                : new AppRelaunchScheduleResult(false, "schedule request failed");
    }

    public Task ShutdownForRestartAsync() =>
        RequestVoidAsync(SettingsIpcMethods.CloudShutdownForRestart);

    public void DeleteTemporaryDownload(string? directory) =>
        RequestVoidAsync(SettingsIpcMethods.CloudDeleteTemporaryDownload, directory)
            .GetAwaiter().GetResult();

    private async Task RequestVoidAsync(
        string method,
        string? payload = null,
        CancellationToken cancellationToken = default)
    {
        await _client.RequestAsync(method, payload, cancellationToken);
    }
}
