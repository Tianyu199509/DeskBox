using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Views;

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

    /// <summary>
    /// Residency P0 attribution: how many content trees exist per widget kind
    /// right now (active + inactive cached), and how many of those are the
    /// inactive cached ones. Formatted as "Kind=n;Kind=n" for the MemorySample
    /// line so the M(n) experiment can correlate tree counts with memory.
    /// </summary>
    internal (string Materialized, string Cached) DescribeContentResidencyByKind()
    {
        var materialized = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var cached = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (ContentWidgetWindow window in GetLoadedDesktopWindows().OfType<ContentWidgetWindow>())
        {
            if (window.CurrentContent is { } current)
            {
                Increment(materialized, current.WidgetKind);
            }

            foreach (WidgetKind kind in window.CachedGroupContentKinds)
            {
                Increment(materialized, kind);
                Increment(cached, kind);
            }
        }

        return (Format(materialized), Format(cached));

        static void Increment(SortedDictionary<string, int> counts, WidgetKind kind)
        {
            string key = kind.ToString();
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        static string Format(SortedDictionary<string, int> counts) =>
            counts.Count == 0
                ? "none"
                : string.Join(';', counts.Select(pair => $"{pair.Key}={pair.Value}"));
    }

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
