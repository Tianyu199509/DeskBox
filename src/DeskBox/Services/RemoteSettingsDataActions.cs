namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// Settings-host Quick Capture and Clipboard data facade over the IPC pipe.
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

    public async Task<string?> GetQuickCaptureMirrorFolderAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataQuickCaptureMirrorFolderGet);
        return ok && !string.IsNullOrWhiteSpace(payload) ? payload : null;
    }

    public Task SetQuickCaptureMirrorFolderAsync(string? folderPath) =>
        SettingsIpcShell.RequestVoidAsync(
            _client,
            SettingsIpcMethods.DataQuickCaptureMirrorFolderSet,
            string.IsNullOrWhiteSpace(folderPath) ? string.Empty : folderPath);

    public async Task<ClipboardStorageStats?> GetClipboardStorageStatsAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataClipboardStats);
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ClipboardStorageStats)
            : null;
    }

    public async Task<int> ClearClipboardUnpinnedAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataClipboardClearUnpinned);
        return ok && int.TryParse(payload, System.Globalization.CultureInfo.InvariantCulture, out int removed)
            ? removed
            : 0;
    }

    public async Task<IReadOnlyList<ClipboardObservedApp>> GetClipboardObservedAppsAsync()
    {
        (bool ok, string? payload, _) = await _client.RequestAsync(SettingsIpcMethods.DataClipboardObservedApps);
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.ListClipboardObservedApp)
            is { } list
                ? list
                : [];
    }

    private Task RequestVoidAsync(string method) =>
        SettingsIpcShell.RequestVoidAsync(_client, method);
}
