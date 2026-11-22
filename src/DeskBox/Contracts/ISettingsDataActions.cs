namespace DeskBox.Contracts;

using DeskBox.Models;
using DeskBox.Services;

/// <summary>
/// Settings-side surface for Quick Capture and Clipboard data management
/// (counts, clears, cache upkeep), which lives in the main process once the
/// settings UI runs in its own process.
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

    Task<string?> GetQuickCaptureMirrorFolderAsync();

    Task SetQuickCaptureMirrorFolderAsync(string? folderPath);

    Task<ClipboardStorageStats?> GetClipboardStorageStatsAsync();

    Task<int> ClearClipboardUnpinnedAsync();

    Task<IReadOnlyList<ClipboardObservedApp>> GetClipboardObservedAppsAsync();
}
