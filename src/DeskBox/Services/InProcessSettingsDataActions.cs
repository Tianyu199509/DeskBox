namespace DeskBox.Services;

using DeskBox.Contracts;

/// <summary>
/// In-process default of <see cref="ISettingsDataActions"/>: a thin wrapper
/// over the Quick Capture service. Counting stays here so a remote settings
/// host never transfers the full store over the wire.
/// </summary>
public sealed class InProcessSettingsDataActions : ISettingsDataActions
{
    private readonly QuickCaptureService _quickCaptureService;
    private readonly LocalizationService _localizationService;

    public InProcessSettingsDataActions(
        QuickCaptureService quickCaptureService,
        LocalizationService localizationService)
    {
        _quickCaptureService = quickCaptureService;
        _localizationService = localizationService;
    }

    public async Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync()
    {
        var data = await _quickCaptureService.GetDataAsync();
        int activeCount = data.Items.Count(item => !item.IsDeleted);
        int recentCount = data.RecentItems.Count(item => !item.IsDeleted);
        return (activeCount, recentCount);
    }

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

    public Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync() =>
        _quickCaptureService.GetImageCacheInfoAsync();
}
