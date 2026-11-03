namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// In-process default of <see cref="ISettingsDataActions"/>: a thin wrapper
/// over the Quick Capture and Clipboard history services. Counting stays here
/// so a remote settings host never transfers the full store over the wire.
/// </summary>
public sealed class InProcessSettingsDataActions : ISettingsDataActions
{
    private readonly QuickCaptureService _quickCaptureService;
    private readonly LocalizationService _localizationService;
    private readonly Func<ClipboardHistoryService?>? _clipboardServiceProvider;

    public InProcessSettingsDataActions(
        QuickCaptureService quickCaptureService,
        LocalizationService localizationService,
        Func<ClipboardHistoryService?>? clipboardServiceProvider = null)
    {
        _quickCaptureService = quickCaptureService;
        _localizationService = localizationService;
        _clipboardServiceProvider = clipboardServiceProvider;
    }

    public Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync() =>
        _quickCaptureService.GetCountsAsync();

    public async Task ClearQuickCaptureDataAsync()
    {
        await _quickCaptureService.ClearAsync();
        await WidgetFirstRunGuideFactory.EnsureQuickCaptureGuideAsync(
            _quickCaptureService,
            _localizationService);
    }

    public Task ClearQuickCaptureRecentAsync() =>
        _quickCaptureService.ClearRecentAsync();

    public Task<QuickCaptureImageCacheCleanupResult> ClearUnusedQuickCaptureImageCacheAsync() =>
        _quickCaptureService.CleanupUnusedImageCacheAsync();

    public async Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync() =>
        await _quickCaptureService.GetImageCacheInfoAsync();

    public Task<string?> GetQuickCaptureMirrorFolderAsync() =>
        _quickCaptureService.GetMirrorFolderPathAsync();

    public Task SetQuickCaptureMirrorFolderAsync(string? folderPath) =>
        _quickCaptureService.SetMirrorFolderPathAsync(folderPath);

    public async Task<ClipboardStorageStats?> GetClipboardStorageStatsAsync()
    {
        ClipboardHistoryService? service = _clipboardServiceProvider?.Invoke();
        return service is not null
            ? await service.GetStorageStatsAsync()
            : await ClipboardStore.CalculateDiskStorageStatsAsync();
    }

    public async Task<int> ClearClipboardUnpinnedAsync()
    {
        ClipboardHistoryService? service = _clipboardServiceProvider?.Invoke();
        return service is not null
            ? await service.ClearUnpinnedAsync()
            : 0;
    }

    public async Task<IReadOnlyList<ClipboardObservedApp>> GetClipboardObservedAppsAsync()
    {
        ClipboardHistoryService? service = _clipboardServiceProvider?.Invoke();
        if (service is not null)
        {
            return await service.GetObservedAppsSnapshotAsync();
        }

        ClipboardStorageStats stats = await ClipboardStore.CalculateDiskStorageStatsAsync();
        return stats.ObservedApps;
    }
}
