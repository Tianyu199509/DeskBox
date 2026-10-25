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

    // Fire-and-forget (batch R11 audit): the restore flow's finally block
    // runs on the UI thread; a blocking roundtrip there freezes the window
    // right after the result dialog, and a timeout throws out of the
    // finally. The response is not read; main deletes the directory
    // regardless of when the host exits (pipe FIFO keeps it ahead of any
    // subsequent shutdown command).
    public void DeleteTemporaryDownload(string? directory) =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.CloudDeleteTemporaryDownload,
            directory,
            nameof(DeleteTemporaryDownload));

    private Task NotifyWithoutBlockingAsync(string method, string? payload, string caller) =>
        SettingsIpcShell.NotifyWithoutBlockingAsync(_client, method, payload, caller);

    private Task RequestVoidAsync(
        string method,
        string? payload = null,
        CancellationToken cancellationToken = default) =>
        SettingsIpcShell.RequestVoidAsync(_client, method, payload, cancellationToken);
}
