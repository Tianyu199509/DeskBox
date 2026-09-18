using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

internal readonly record struct WidgetMemoryVisibilitySnapshot(
    int LoadedWindowCount,
    int LogicalVisibleCount,
    int NativeVisibleCount)
{
    internal bool HasNativeVisibleWidgets => NativeVisibleCount > 0;
}

internal readonly record struct LongHiddenWidgetMaintenanceResult(
    int ContentHostCount);

internal readonly record struct LongHiddenWidgetResourceReleaseResult(
    int ContentHostCount,
    int CachedContentCount);

public sealed partial class WidgetManager
{
    internal Task WaitForTrayAnimationsIdleAsync() =>
        _trayBatchAnimationDriver.WaitForIdleAsync();

    internal int ActiveFolderWatcherCount =>
        GetFolderWatcherHealthSnapshots().Count(snapshot =>
            snapshot.NativeWatcherActive || snapshot.QueryWatcherActive);

    internal int CachedGroupContentCount => _contentWidgets.Values
        .DistinctBy(window => window.WindowHandle)
        .Sum(window => window.CachedGroupContentCount);

    internal LongHiddenWidgetMaintenanceResult
        RunLongHiddenNoRebuildMaintenance()
    {
        int contentHostCount = 0;
        foreach (var window in _contentWidgets.Values
                     .DistinctBy(window => window.WindowHandle))
        {
            if (window.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(window.WindowHandle))
            {
                continue;
            }

            contentHostCount++;
            window.RunLongHiddenNoRebuildMaintenance();
        }

        return new LongHiddenWidgetMaintenanceResult(contentHostCount);
    }

    internal LongHiddenWidgetResourceReleaseResult
        ReleaseLongHiddenInactiveContent()
    {
        int contentHostCount = 0;
        int cachedContentCount = 0;
        foreach (var window in _contentWidgets.Values
                     .DistinctBy(window => window.WindowHandle))
        {
            if (window.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(window.WindowHandle))
            {
                continue;
            }

            contentHostCount++;
            cachedContentCount += window.ReleaseLongHiddenContentResources();
        }

        return new LongHiddenWidgetResourceReleaseResult(
            contentHostCount,
            cachedContentCount);
    }

}
