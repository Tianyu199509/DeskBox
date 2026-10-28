namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// Settings-host Quick Capture data facade over the IPC pipe (batch 3c-1).
/// </summary>
public sealed class RemoteSettingsDataActions : ISettingsDataActions
{
    private readonly SettingsIpcClient _client;

    public RemoteSettingsDataActions(SettingsIpcClient client)
    {
        _client = client;
    }

    public async Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataQuickCaptureCounts);
        if (ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.SettingsIpcIntPair)
            is { } pair)
        {
            return (pair.A, pair.B);
        }

        return (0, 0);
    }

    public Task ClearQuickCaptureDataAsync() =>
        RequestVoidAsync(SettingsIpcMethods.DataQuickCaptureClear);

    public Task ClearQuickCaptureRecentAsync() =>
        RequestVoidAsync(SettingsIpcMethods.DataQuickCaptureClearRecent);

    public async Task<QuickCaptureImageCacheCleanupResult> ClearUnusedQuickCaptureImageCacheAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataQuickCaptureCacheCleanup);
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.QuickCaptureImageCacheCleanupResult)
            is { } result
                ? result
                : new QuickCaptureImageCacheCleanupResult(DeletedFileCount: 0, DeletedBytes: 0);
    }

    public async Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataQuickCaptureCacheInfo);
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.QuickCaptureImageCacheInfo)
            : null;
    }

    private Task RequestVoidAsync(string method) =>
        SettingsIpcShell.RequestVoidAsync(_client, method);
}
