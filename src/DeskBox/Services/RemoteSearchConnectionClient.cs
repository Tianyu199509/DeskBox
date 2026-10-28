namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host Everything connection client over the IPC pipe (batch 3d
/// follow-up): the Everything service lives only in the main process, so
/// the search section's connection card, detect/launch buttons, and manual
/// path picker all ride these commands. CurrentSnapshot is a cached read —
/// the cache warms at construction and updates on every command round-trip.
/// </summary>
public sealed class RemoteSearchConnectionClient : ISearchConnectionClient
{
    private readonly SettingsIpcClient _client;
    private EverythingConnectionSnapshot _snapshot = EverythingConnectionSnapshot.Unknown;

    public RemoteSearchConnectionClient(SettingsIpcClient client)
    {
        _client = client;
    }

    public event Action<EverythingConnectionSnapshot>? ConnectionChanged;

    public EverythingConnectionSnapshot CurrentSnapshot => _snapshot;

    public async Task<EverythingConnectionSnapshot> RefreshConnectionAsync(
        bool allowIpcProbe,
        CancellationToken cancellationToken)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.SearchEverythingRefresh,
            SettingsIpcPayload.Encode(
                new SettingsIpcEverythingRefresh(allowIpcProbe),
                SettingsIpcJsonContext.Default.SettingsIpcEverythingRefresh),
            cancellationToken);
        return UpdateFromPayload(ok, payload);
    }

    public async Task UseAutomaticDetectionAsync(CancellationToken cancellationToken)
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(
            SettingsIpcMethods.SearchEverythingAuto,
            cancellationToken: cancellationToken);
        UpdateFromPayload(ok, payload);
    }

    public async Task<bool> SetExecutablePathAsync(string path, CancellationToken cancellationToken)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.SearchEverythingSetPath,
            SettingsIpcPayload.Encode(
                new SettingsIpcEverythingPath(path),
                SettingsIpcJsonContext.Default.SettingsIpcEverythingPath),
            cancellationToken);
        await RefreshConnectionAsync(allowIpcProbe: false, cancellationToken);
        return ok;
    }

    public async Task<bool> LaunchEverythingAsync(CancellationToken cancellationToken)
    {
        (bool ok, _, _) = await _client.RequestAsync(
            SettingsIpcMethods.SearchEverythingLaunch,
            cancellationToken: cancellationToken);
        return ok;
    }

    /// <summary>Warm the cache once at construction (first synchronous read).</summary>
    public RemoteSearchConnectionClient Prime()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(SettingsIpcMethods.SearchEverythingState)
            .GetAwaiter().GetResult();
        UpdateFromPayload(ok, payload);
        return this;
    }

    private EverythingConnectionSnapshot UpdateFromPayload(bool ok, string? payload)
    {
        if (ok &&
            SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.EverythingConnectionSnapshot)
                is { } snapshot)
        {
            bool changed = _snapshot != snapshot;
            _snapshot = snapshot;
            if (changed)
            {
                ConnectionChanged?.Invoke(snapshot);
            }

            return snapshot;
        }

        return _snapshot;
    }
}
