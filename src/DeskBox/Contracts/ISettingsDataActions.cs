namespace DeskBox.Contracts;

using DeskBox.Services;

/// <summary>
/// Settings-side surface for Quick Capture data management (counts, clears,
/// image-cache upkeep), which lives in the main process once the settings UI
/// runs in its own process. The image-cache members return null when the
/// service is unavailable so the editor can show its unavailable line.
/// </summary>
public interface ISettingsDataActions
{
    /// <summary>Counts of live records and recent items.</summary>
    Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync();

    /// <summary>
    /// Clears all data and replays the first-run guide on the widget.
    /// </summary>
    Task ClearQuickCaptureDataAsync();

    Task ClearQuickCaptureRecentAsync();

    Task<QuickCaptureImageCacheCleanupResult> ClearUnusedQuickCaptureImageCacheAsync();

    Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync();
}
