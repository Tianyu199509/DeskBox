using DeskBox.Contracts;
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
    int CachedContentCount,
    int EvictedHostCount = 0);

public sealed partial class WidgetManager
{
    private static readonly HashSet<IntPtr> s_emptyMonitorSet = [];
    private IReadOnlySet<IntPtr> _occludedMonitors = s_emptyMonitorSet;
    private readonly HashSet<IntPtr> _occludedSuspendedWindowHandles = [];

    internal bool HasUnoccludedVisibleWidgets
    {
        get
        {
            foreach (IDesktopWidgetWindow window in GetLoadedDesktopWindows())
            {
                bool isVisible =
                    window.Visible ||
                    (window.WindowHandle != IntPtr.Zero &&
                     Win32Helper.IsWindowVisible(window.WindowHandle));
                if (!isVisible)
                {
                    continue;
                }

                bool isAtDesktopLayer =
                    !window.IsRaisedAboveDesktopLayer &&
                    !_widgetsRaisedFromTray &&
                    !WidgetLayerService.UsesQuickRevealMode();
                if (!isAtDesktopLayer || _occludedMonitors.Count == 0)
                {
                    return true;
                }

                IntPtr hMonitor = MonitorOcclusionTracker.GetMonitorFromWindow(window.WindowHandle);
                if (hMonitor == IntPtr.Zero || !_occludedMonitors.Contains(hMonitor))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal void ApplyMonitorOcclusionState(IReadOnlySet<IntPtr>? occludedMonitors = null)
    {
        if (!HasUiThreadAccess())
        {
            IReadOnlySet<IntPtr>? captured = occludedMonitors;
            UiDispatch.RunOrDefer(() => ApplyMonitorOcclusionState(captured));
            return;
        }

        if (occludedMonitors is not null)
        {
            _occludedMonitors = occludedMonitors;
        }

        if (App.IsShuttingDown)
        {
            _occludedSuspendedWindowHandles.Clear();
            return;
        }

        var liveWindowsByHandle = new Dictionary<IntPtr, ContentWidgetWindow>();
        foreach (ContentWidgetWindow window in GetLoadedDesktopWindows().OfType<ContentWidgetWindow>())
        {
            if (window.WindowHandle != IntPtr.Zero)
            {
                liveWindowsByHandle.TryAdd(window.WindowHandle, window);
            }
        }

        if (_occludedSuspendedWindowHandles.Count > 0)
        {
            _occludedSuspendedWindowHandles.RemoveWhere(
                handle => !liveWindowsByHandle.ContainsKey(handle));
        }

        foreach ((IntPtr handle, ContentWidgetWindow window) in liveWindowsByHandle)
        {
            bool isVisibleAtDesktopLayer =
                window.Visible &&
                !window.IsClosing &&
                !window.IsRaisedAboveDesktopLayer &&
                !_widgetsRaisedFromTray &&
                !WidgetLayerService.UsesQuickRevealMode();

            bool shouldSuspend =
                isVisibleAtDesktopLayer &&
                _occludedMonitors.Count > 0 &&
                _occludedMonitors.Contains(MonitorOcclusionTracker.GetMonitorFromWindow(handle));

            if (shouldSuspend)
            {
                if (_occludedSuspendedWindowHandles.Add(handle))
                {
                    try
                    {
                        window.CurrentContent?.OnWindowVisibilityChanged(false);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[Occlusion] Failed suspending occluded widget '{window.Config.Id}': {ex.Message}");
                    }
                }
            }
            else if (_occludedSuspendedWindowHandles.Remove(handle))
            {
                if (window.Visible &&
                    !window.IsClosing &&
                    Win32Helper.IsWindowVisible(handle))
                {
                    try
                    {
                        window.CurrentContent?.OnWindowVisibilityChanged(true);
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[Occlusion] Failed resuming unoccluded widget '{window.Config.Id}': {ex.Message}");
                    }
                }
            }
        }
    }

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
            if (window.CurrentContent is { } current &&
                (current is not DeskBox.Controls.WidgetContents.WidgetContentAdapterBase adapter ||
                 adapter.HasMaterializedView))
            {
                Increment(materialized, current.WidgetKind);
            }

            foreach (IWidgetContent cachedContent in window.CachedGroupContents)
            {
                if (cachedContent is not DeskBox.Controls.WidgetContents.WidgetContentAdapterBase cachedAdapter ||
                    cachedAdapter.HasMaterializedView)
                {
                    Increment(materialized, cachedContent.WidgetKind);
                }

                Increment(cached, cachedContent.WidgetKind);
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
            if (window.Visible ||
                (window.WindowHandle != IntPtr.Zero &&
                 Win32Helper.IsWindowVisible(window.WindowHandle)))
            {
                continue;
            }

            contentHostCount++;
            window.RunLongHiddenNoRebuildMaintenance();
        }

        if (contentHostCount > 0)
        {
            Views.WidgetWindowBase.TrimAllIdleSharedBackdropControllers();
        }

        return new LongHiddenWidgetMaintenanceResult(contentHostCount);
    }

    internal LongHiddenWidgetResourceReleaseResult
        ReleaseLongHiddenInactiveContent()
    {
        int contentHostCount = 0;
        int cachedContentCount = 0;
        foreach (var window in _contentWidgets.Values
                     .DistinctBy(window => window.WindowHandle)
                     .ToArray())
        {
            if (window.Visible ||
                (window.WindowHandle != IntPtr.Zero &&
                 Win32Helper.IsWindowVisible(window.WindowHandle)))
            {
                continue;
            }

            contentHostCount++;
            cachedContentCount += window.ReleaseLongHiddenContentResources();
        }

        int evictedHostCount = EvictExcessLongHiddenContentHosts();
        return new LongHiddenWidgetResourceReleaseResult(
            contentHostCount,
            cachedContentCount,
            evictedHostCount);
    }

    private int EvictExcessLongHiddenContentHosts()
    {
        if (App.IsShuttingDown ||
            _trayBatchAnimationDriver.IsRunning ||
            WidgetCompactAnimationCoordinator.HasActiveAnimations)
        {
            return 0;
        }

        IReadOnlyList<IDesktopWidgetWindow> loadedWindows = GetLoadedDesktopWindows();
        if (loadedWindows.Count <= ContentWidgetWindowPool.MinRetainedHiddenWindowCount)
        {
            return 0;
        }

        EffectivePerformanceSettings performance =
            PerformanceSettingsPolicy.Resolve(_settingsService.Settings);
        ContentWidgetWindow[] distinctHosts = _contentWidgets.Values
            .DistinctBy(window => window.WindowHandle)
            .ToArray();
        List<HiddenContentHostCandidate> candidates = new(distinctHosts.Length);
        foreach (ContentWidgetWindow host in distinctHosts)
        {
            string widgetId = host.Config.Id;
            bool isGroupMember = WidgetGroupSettings.FindByMember(
                _settingsService.Settings,
                widgetId) is not null;
            bool hasWatcherObligation =
                host.Config.WidgetKind == WidgetKind.File ||
                _fileWidgets.ContainsKey(widgetId) ||
                host.CurrentContent is DeskBox.Controls.WidgetContents.FileWidgetContentAdapter;
            bool isNativeVisible =
                host.WindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindowVisible(host.WindowHandle);
            candidates.Add(new HiddenContentHostCandidate(
                widgetId,
                host.Config.WidgetKind,
                host.Visible,
                isNativeVisible,
                host.IsClosing,
                host.IsBoundsTransitionActive || host.IsTitleBarDragTransitionActive,
                isGroupMember,
                hasWatcherObligation));
        }

        IReadOnlyList<string> selectedIds =
            ContentWidgetWindowPool.SelectExcessHiddenHostsToEvict(
                candidates,
                loadedWindows.Count,
                performance.HiddenCacheCleanupScope,
                performance.CacheBudget);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        int evicted = 0;
        foreach (string widgetId in selectedIds)
        {
            if (!_contentWidgets.TryGetValue(widgetId, out ContentWidgetWindow? window) ||
                window.Visible ||
                window.IsClosing ||
                GetLoadedDesktopWindows().Count <=
                    ContentWidgetWindowPool.MinRetainedHiddenWindowCount)
            {
                continue;
            }

            CaptureWidgetGroupTransientState(
                widgetId,
                window.CurrentContent as IWidgetTransientStateContent);
            _suppressClosedVisibilityPersistence.Add(widgetId);
            try
            {
                window.CloseWindow();
                evicted++;
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[Memory] Failed to close excess long-hidden host '{widgetId}': {ex.Message}");
            }
            finally
            {
                _suppressClosedVisibilityPersistence.Remove(widgetId);
            }
        }

        if (evicted > 0)
        {
            EnsureXamlLoopLifelineAction?.Invoke();
            App.Log(
                $"[Memory] Evicted excess long-hidden widget hosts count={evicted} " +
                $"remainingLoaded={GetLoadedDesktopWindows().Count}");
        }

        return evicted;
    }
}
