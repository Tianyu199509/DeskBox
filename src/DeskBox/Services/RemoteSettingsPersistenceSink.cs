namespace DeskBox.Services;

using System.Text.Json;

/// <summary>
/// Settings-host persistence sink (batch 3b): ships every committed snapshot
/// to the main process over the settings IPC pipe instead of writing disk.
/// Cross-process by design — the caller holds the per-directory commit gate
/// while saving, and the main-side apply path takes the same gate in its own
/// process (see <see cref="ISettingsPersistenceSink"/> remarks).
/// </summary>
public sealed class RemoteSettingsPersistenceSink : ISettingsPersistenceSink
{
    private readonly SettingsIpcClient _client;

    public RemoteSettingsPersistenceSink(SettingsIpcClient client)
    {
        _client = client;
    }

    public async Task<bool> SaveAsync(SettingsService owner, SettingsPersistenceCommit commit)
    {
        string json = JsonSerializer.Serialize(
            commit.Snapshot,
            SettingsJsonContext.Default.AppSettings);
        string payload = commit.CommitId + "\n" + json;
        (bool ok, _, string? error) = await _client.RequestAsync(
            SettingsIpcMethods.SettingsCommit,
            payload).ConfigureAwait(false);
        if (!ok)
        {
            App.Log($"[SettingsIpc] Remote commit {commit.CommitId} refused: {error ?? "unknown"}");
        }

        return ok;
    }
}

/// <summary>
/// Main-side handlers for the settings-host pipe methods (batch 3b wires
/// ping and settings-commit; batch 3d extends the dispatch table).
/// </summary>
public static class SettingsIpcDispatch
{
    /// <summary>
    /// Applies a settings-commit payload ("commitId\nsettingsJson") to the
    /// main settings service.
    /// </summary>
    public static async Task<(bool Ok, string? Payload, string? Error)> SettingsCommitAsync(
        SettingsService service,
        string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return (false, null, "empty payload");
        }

        int separator = payload.IndexOf('\n');
        if (separator <= 0 || !long.TryParse(payload.AsSpan(0, separator), out long commitId))
        {
            return (false, null, "malformed payload");
        }

        string json = payload[(separator + 1)..];
        bool applied = await service.ApplyExternalSnapshotAsync(json);
        return applied
            ? (true, commitId.ToString(), null)
            : (false, null, "apply failed");
    }
}
