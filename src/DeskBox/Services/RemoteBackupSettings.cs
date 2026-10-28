namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host backup-settings surface over the IPC pipe (batch 3c-3a):
/// stands in for the main process's BackupSettingsCoordinator, which needs
/// the local backup and cloud services that exist only in the main process.
/// </summary>
public sealed class RemoteBackupSettings : IBackupSettings
{
    private readonly SettingsIpcClient _client;

    public RemoteBackupSettings(SettingsIpcClient client)
    {
        _client = client;
        _client.EventReceived += OnEventReceived;
    }

    public event Action<BackupUploadNotification>? UploadCompleted;

    private void OnEventReceived(string method, string? payload)
    {
        if (method == SettingsIpcMethods.BackupUploadCompleted &&
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupUploadNotification)
                is { } notification)
        {
            UploadCompleted?.Invoke(notification);
        }
    }

    public BackupSettingsSnapshot Read()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.BackupSettingsRead)
            .GetAwaiter().GetResult();
        return SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.BackupSettingsSnapshot)
            ?? new BackupSettingsSnapshot(
                false, 0, 0, string.Empty, string.Empty, string.Empty, false,
                string.Empty, string.Empty, string.Empty, string.Empty,
                false, false, false, 0, 0, 0, 0, 0,
                new BackupEndpoint(string.Empty, string.Empty, string.Empty, string.Empty),
                HasEndpoint: false);
    }

    public void Update(BackupSettingsChange change) =>
        _client.RequestAsync(
                SettingsIpcMethods.BackupSettingsUpdate,
                SettingsIpcPayload.Encode(change, SettingsIpcJsonContext.Default.BackupSettingsChange))
            .GetAwaiter().GetResult();

    public bool IsValidLocalDirectory(string path, out string? rejectionReasonKey)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.BackupSettingsIsValidLocalDir,
                path)
            .GetAwaiter().GetResult();
        SettingsIpcLocalDirCheck check =
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcLocalDirCheck)
            ?? new SettingsIpcLocalDirCheck(false, null);
        rejectionReasonKey = check.RejectionReasonKey;
        return check.Valid;
    }

    public Task SaveAsync() =>
        RequestVoidAsync(SettingsIpcMethods.BackupSettingsSave);
    public Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken cancellationToken) =>
        RequestBoolAsync(
            SettingsIpcMethods.BackupSettingsHasCredential,
            SettingsIpcPayload.Encode(endpoint, SettingsIpcJsonContext.Default.BackupEndpoint),
            cancellationToken);

    public async Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken cancellationToken)
    {
        // Deep review R13-2 (D18 audit): the main-side credential write
        // throws on vault failures and the server converts that to ok=false
        // — discarding it (RequestVoidAsync) reported "password saved" on a
        // failed write. Throwing restores the in-process semantics the
        // view-model's catch blocks were written against.
        await RequireOkAsync(
            SettingsIpcMethods.BackupSettingsSaveCredential,
            SettingsIpcPayload.Encode(
                new SettingsIpcCredential(endpoint, secret, null),
                SettingsIpcJsonContext.Default.SettingsIpcCredential),
            cancellationToken);
    }

    public async Task ProbeAsync(BackupEndpoint endpoint, string? secretOverride, CancellationToken cancellationToken)
    {
        // Same contract as SaveCredentialAsync: a failed probe is a user-
        // visible failure, not a silent success.
        await RequireOkAsync(
            SettingsIpcMethods.BackupSettingsProbe,
            SettingsIpcPayload.Encode(
                new SettingsIpcCredential(endpoint, null, secretOverride),
                SettingsIpcJsonContext.Default.SettingsIpcCredential),
            cancellationToken);
    }

    private async Task RequireOkAsync(string method, string payload, CancellationToken cancellationToken)
    {
        (bool ok, _, string? error) = await _client.RequestAsync(method, payload, cancellationToken);
        if (!ok)
        {
            throw new InvalidOperationException(error ?? $"{method} failed");
        }
    }

    public async Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(
        BackupEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.BackupSettingsList,
            SettingsIpcPayload.Encode(endpoint, SettingsIpcJsonContext.Default.BackupEndpoint),
            cancellationToken);
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListBackupRemoteSnapshot) ?? []
            : [];
    }

    private async Task<bool> RequestBoolAsync(
        string method,
        string payload,
        CancellationToken cancellationToken)
    {
        (bool ok, string? result, _) = await _client.RequestAsync(method, payload, cancellationToken);
        return ok && SettingsIpcPayload.DecodeBoolean(result);
    }

    private Task RequestVoidAsync(string method, string? payload = null, CancellationToken cancellationToken = default) =>
        SettingsIpcShell.RequestVoidAsync(_client, method, payload, cancellationToken);
}
