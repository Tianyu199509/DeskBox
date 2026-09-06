using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeskBox.Helpers;

namespace DeskBox.Services;

/// <summary>
/// Lightweight opt-in timing logs for performance baseline work.
/// Enable by setting DESKBOX_PERF_LOG=1 before launching DeskBox.
/// </summary>
public static partial class PerformanceLogger
{
    public const string EnabledEnvironmentVariable = "DESKBOX_PERF_LOG";

    private static readonly Lazy<bool> s_isEnabled = new(
        () => IsEnabledSetting(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable)));

    public static bool IsEnabled => s_isEnabled.Value;

    // ── Memory & resource diagnostics ───────────────────────────

    /// <summary>Working set (bytes) at the last sample.</summary>
    public static long LastWorkingSet { get; private set; }

    /// <summary>Private memory (bytes) at the last sample.</summary>
    public static long LastPrivateMemory { get; private set; }

    /// <summary>Managed heap size (bytes) at the last sample.</summary>
    public static long LastManagedHeap { get; private set; }

    public static long LastGcHeapSize { get; private set; }

    public static long LastGcCommittedBytes { get; private set; }

    public static long LastGcFragmentedBytes { get; private set; }

    public static long LastGcMemoryLoad { get; private set; }

    /// <summary>
    /// Private bytes minus the GC heap size. This is only a broad estimate of
    /// non-GC private memory; it is not a composition or graphics attribution.
    /// </summary>
    public static long LastPrivateMinusGcHeapEstimate { get; private set; }

    /// <summary>Handle count at the last sample.</summary>
    public static int LastHandleCount { get; private set; }

    /// <summary>Thumbnail cache entry count, updated by IconHelper.</summary>
    public static int ThumbnailCacheCount { get; set; }

    public static long ThumbnailEstimatedBytes { get; set; }

    /// <summary>Icon cache entry count, updated by IconHelper.</summary>
    public static int IconCacheCount { get; set; }

    public static int DecodedBitmapCacheCount { get; set; }

    public static long DecodedBitmapEstimatedBytes { get; set; }

    public static int QuickCaptureDetailImageDecodeCount =>
        Volatile.Read(ref s_quickCaptureDetailImageDecodeCount);

    public static long QuickCaptureDetailImageEstimatedBytes =>
        Math.Max(
            0,
            Interlocked.Read(
                ref s_quickCaptureDetailImageEstimatedBytes));

    public static int GlanceCompactBackgroundDecodeCount =>
        Volatile.Read(ref s_glanceCompactBackgroundDecodeCount);

    public static long GlanceCompactBackgroundEstimatedBytes =>
        Math.Max(
            0,
            Interlocked.Read(
                ref s_glanceCompactBackgroundEstimatedBytes));

    /// <summary>Music cover decode count since launch.</summary>
    public static int MusicCoverDecodeCount => Volatile.Read(ref s_musicCoverDecodeCount);

    /// <summary>Markdown visual-tree rebuild count since launch.</summary>
    public static int MarkdownRenderCount => Volatile.Read(ref s_markdownRenderCount);

    /// <summary>Markdown inline bitmap creation count since launch.</summary>
    public static int MarkdownInlineImageDecodeCount =>
        Volatile.Read(ref s_markdownInlineImageDecodeCount);

    /// <summary>Active music progress timer count.</summary>
    public static int ActiveMusicTimerCount { get; set; }

    /// <summary>Transient WinUI timers created since launch.</summary>
    public static int TransientUiTimerCreatedCount =>
        Volatile.Read(ref s_transientUiTimerCreatedCount);

    /// <summary>Transient WinUI timers fully stopped and detached since launch.</summary>
    public static int TransientUiTimerReleasedCount =>
        Volatile.Read(ref s_transientUiTimerReleasedCount);

    /// <summary>Transient WinUI timers that still own a Tick subscription.</summary>
    public static int ActiveTransientUiTimerCount =>
        Math.Max(0, TransientUiTimerCreatedCount - TransientUiTimerReleasedCount);

    /// <summary>Current music progress timer interval.</summary>
    public static int MusicProgressTimerIntervalMs { get; set; }

    private static int s_musicCoverDecodeCount;
    private static int s_markdownRenderCount;
    private static int s_markdownInlineImageDecodeCount;
    private static int s_transientUiTimerCreatedCount;
    private static int s_transientUiTimerReleasedCount;
    private static int s_quickCaptureDetailImageDecodeCount;
    private static long s_quickCaptureDetailImageEstimatedBytes;
    private static int s_glanceCompactBackgroundDecodeCount;
    private static long s_glanceCompactBackgroundEstimatedBytes;
    private static long s_thumbnailCacheLookupHitCount;
    private static long s_thumbnailCacheLookupMissCount;
    private static long s_thumbnailCacheLoadCount;
    private static long s_thumbnailCacheLoadFailureCount;
    private static long s_thumbnailCacheLoadDurationTicks;
    private static long s_thumbnailCacheMaxLoadDurationTicks;
    private static long s_decodedBitmapCacheLookupHitCount;
    private static long s_decodedBitmapCacheLookupMissCount;
    private static long s_decodedBitmapCacheLoadCount;
    private static long s_decodedBitmapCacheLoadFailureCount;
    private static long s_decodedBitmapCacheLoadDurationTicks;
    private static long s_decodedBitmapCacheMaxLoadDurationTicks;

    public static void RecordMusicCoverDecode()
    {
        Interlocked.Increment(ref s_musicCoverDecodeCount);
    }

    public static void RecordMarkdownRender()
    {
        Interlocked.Increment(ref s_markdownRenderCount);
    }

    public static void RecordMarkdownInlineImageDecode()
    {
        Interlocked.Increment(ref s_markdownInlineImageDecodeCount);
    }

    public static void RecordQuickCaptureDetailImageDecode()
    {
        Interlocked.Increment(ref s_quickCaptureDetailImageDecodeCount);
    }

    public static void AdjustQuickCaptureDetailImageEstimatedBytes(long delta)
    {
        Interlocked.Add(
            ref s_quickCaptureDetailImageEstimatedBytes,
            delta);
    }

    public static void RecordGlanceCompactBackgroundDecode()
    {
        Interlocked.Increment(ref s_glanceCompactBackgroundDecodeCount);
    }

    public static void AdjustGlanceCompactBackgroundEstimatedBytes(long delta)
    {
        Interlocked.Add(
            ref s_glanceCompactBackgroundEstimatedBytes,
            delta);
    }

    public static void RecordTransientUiTimerCreated()
    {
        Interlocked.Increment(ref s_transientUiTimerCreatedCount);
    }

    public static void RecordTransientUiTimerReleased()
    {
        Interlocked.Increment(ref s_transientUiTimerReleasedCount);
    }

    /// <summary>
    /// Adds one thumbnail-cache lookup to the in-memory aggregate. No log is
    /// emitted, and the counter is disabled unless performance logging was
    /// explicitly enabled for this process.
    /// </summary>
    internal static void RecordThumbnailCacheLookup(bool hit)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (hit)
        {
            Interlocked.Increment(ref s_thumbnailCacheLookupHitCount);
        }
        else
        {
            Interlocked.Increment(ref s_thumbnailCacheLookupMissCount);
        }
    }

    internal static void RecordThumbnailCacheLoad(
        TimeSpan elapsed,
        bool succeeded)
    {
        if (!IsEnabled)
        {
            return;
        }

        RecordCacheLoad(
            elapsed,
            succeeded,
            ref s_thumbnailCacheLoadCount,
            ref s_thumbnailCacheLoadFailureCount,
            ref s_thumbnailCacheLoadDurationTicks,
            ref s_thumbnailCacheMaxLoadDurationTicks);
    }

    /// <summary>
    /// Adds one decoded-icon-cache lookup to the in-memory aggregate. It does
    /// not write a per-lookup diagnostic line.
    /// </summary>
    internal static void RecordDecodedBitmapCacheLookup(bool hit)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (hit)
        {
            Interlocked.Increment(ref s_decodedBitmapCacheLookupHitCount);
        }
        else
        {
            Interlocked.Increment(ref s_decodedBitmapCacheLookupMissCount);
        }
    }

    internal static void RecordDecodedBitmapCacheLoad(
        TimeSpan elapsed,
        bool succeeded)
    {
        if (!IsEnabled)
        {
            return;
        }

        RecordCacheLoad(
            elapsed,
            succeeded,
            ref s_decodedBitmapCacheLoadCount,
            ref s_decodedBitmapCacheLoadFailureCount,
            ref s_decodedBitmapCacheLoadDurationTicks,
            ref s_decodedBitmapCacheMaxLoadDurationTicks);
    }

    private static readonly ConcurrentDictionary<string, int> s_windowCounts = new();

    /// <summary>
    /// Records a widget window as open (called by window constructors).
    /// </summary>
    public static void TrackWindowOpen(string windowKind)
    {
        if (!IsEnabled)
        {
            return;
        }

        s_windowCounts.AddOrUpdate(windowKind, 1, (_, c) => c + 1);
    }

    /// <summary>
    /// Records a widget window as closed.
    /// </summary>
    public static void TrackWindowClose(string windowKind)
    {
        if (!IsEnabled)
        {
            return;
        }

        s_windowCounts.AddOrUpdate(windowKind, 0, (_, c) => Math.Max(0, c - 1));
    }

    /// <summary>
    /// Samples the current process memory and handle usage and logs a
    /// diagnostic line.  Only runs when perf logging is enabled.
    /// </summary>
    public static void SampleMemory(string? reason = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            LastWorkingSet = proc.WorkingSet64;
            LastPrivateMemory = proc.PrivateMemorySize64;
            LastHandleCount = proc.HandleCount;
            LastManagedHeap = GC.GetTotalMemory(forceFullCollection: false);
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            LastGcHeapSize = gcInfo.HeapSizeBytes;
            LastGcCommittedBytes = gcInfo.TotalCommittedBytes;
            LastGcFragmentedBytes = gcInfo.FragmentedBytes;
            LastGcMemoryLoad = gcInfo.MemoryLoadBytes;
            LastPrivateMinusGcHeapEstimate = Math.Max(
                0,
                LastPrivateMemory - LastGcHeapSize);
            long lohSizeBytes = gcInfo.GenerationInfo.Length > 3
                ? gcInfo.GenerationInfo[3].SizeAfterBytes
                : 0;
            CachePerformanceSnapshot cachePerformance =
                CaptureCachePerformanceSnapshot();

            var app = App.Current;
            string everythingState = app.EverythingSearchService?.CurrentSnapshot.State.ToString()
                ?? "uninitialized";
            bool searchEnabled = FeatureWidgetSettings.IsEnabled(
                app.SettingsService.Settings,
                DeskBox.Models.WidgetKind.Search);
            int loadedWidgetCount = app.WidgetManager?.LoadedWidgetCount ?? 0;
            int visibleWidgetCount = app.WidgetManager?.VisibleWidgetCount ?? 0;
            int surfaceSwitchGateCount =
                app.WidgetManager?.SurfaceSwitchGateCount ?? 0;
            int activeFolderWatcherCount =
                app.WidgetManager?.ActiveFolderWatcherCount ?? 0;
            int cachedGroupContentCount =
                app.WidgetManager?.CachedGroupContentCount ?? 0;
            EffectivePerformanceSettings performance =
                PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings);
#if DESKBOX_NATIVE_AOT
            const string runtimeProfile = "native-aot";
#else
            string runtimeProfile = RuntimeFeature.IsDynamicCodeSupported
                ? "managed-jit"
                : "managed-aot";
#endif

            int windowCount = 0;
            foreach (var kv in s_windowCounts)
            {
                windowCount += kv.Value;
            }

            App.Log(
                $"[Perf] MemorySample " +
                $"pid={proc.Id} " +
                $"version={typeof(PerformanceLogger).Assembly.GetName().Version} " +
                $"runtime={runtimeProfile} " +
                $"performanceMode={performance.Mode} " +
                $"cacheBudget={IconHelper.CurrentPerformanceCacheBudget} " +
                $"workingSetMB={LastWorkingSet / (1024.0 * 1024):F1} " +
                $"privateMB={LastPrivateMemory / (1024.0 * 1024):F1} " +
                $"managedHeapMB={LastManagedHeap / (1024.0 * 1024):F1} " +
                $"gcHeapMB={LastGcHeapSize / (1024.0 * 1024):F1} " +
                $"gcCommittedMB={LastGcCommittedBytes / (1024.0 * 1024):F1} " +
                $"gcIndex={gcInfo.Index} " +
                $"ownedCompositionCreated={OwnedCompositionResourcesCreated} " +
                $"ownedCompositionReleased={OwnedCompositionResourcesReleased} " +
                $"ownedCompositionOutstanding={OwnedCompositionResourcesOutstanding} " +
                $"contentAppearanceApplied={ContentAppearanceApplied} " +
                $"contentAppearanceSkipped={ContentAppearanceSkipped} " +
                $"stackProjectionRebuilds={StackProjectionRebuilds} " +
                $"stackProjectionReuseSkips={StackProjectionReuseSkips} " +
                $"privateMinusGcHeapEstimateMB={LastPrivateMinusGcHeapEstimate / (1024.0 * 1024):F1} " +
                $"gcFragmentedMB={LastGcFragmentedBytes / (1024.0 * 1024):F1} " +
                $"lohMB={lohSizeBytes / (1024.0 * 1024):F1} " +
                $"gcMemoryLoadMB={LastGcMemoryLoad / (1024.0 * 1024):F1} " +
                $"handles={LastHandleCount} " +
                $"thumbCache={ThumbnailCacheCount} " +
                $"thumbCacheMB={ThumbnailEstimatedBytes / (1024.0 * 1024):F1} " +
                $"thumbLookupHits={cachePerformance.ThumbnailLookupHits} " +
                $"thumbLookupMisses={cachePerformance.ThumbnailLookupMisses} " +
                $"thumbHitRatePercent={CalculateHitRatePercent(cachePerformance.ThumbnailLookupHits, cachePerformance.ThumbnailLookupMisses):F1} " +
                $"thumbLoads={cachePerformance.ThumbnailLoads} " +
                $"thumbLoadFailures={cachePerformance.ThumbnailLoadFailures} " +
                $"thumbLoadAvgMs={CalculateAverageDurationMilliseconds(cachePerformance.ThumbnailLoadDurationTicks, cachePerformance.ThumbnailLoads):F1} " +
                $"thumbLoadMaxMs={TimeSpan.FromTicks(cachePerformance.ThumbnailMaxLoadDurationTicks).TotalMilliseconds:F1} " +
                $"iconCache={IconCacheCount} " +
                $"shellKindCache={FileService.ShellKindCacheEntryCount} " +
                $"shortcutMetadataCache={ShortcutHelper.StoredMetadataCacheEntryCount} " +
                $"glanceStores={GlanceWidgetStore.CachedWidgetStoreCount} " +
                $"weatherPathGates={WeatherCacheStore.PathGateCount} " +
                $"decodedBitmapCache={DecodedBitmapCacheCount} " +
                $"decodedBitmapMB={DecodedBitmapEstimatedBytes / (1024.0 * 1024):F1} " +
                $"decodedBitmapLookupHits={cachePerformance.DecodedBitmapLookupHits} " +
                $"decodedBitmapLookupMisses={cachePerformance.DecodedBitmapLookupMisses} " +
                $"decodedBitmapHitRatePercent={CalculateHitRatePercent(cachePerformance.DecodedBitmapLookupHits, cachePerformance.DecodedBitmapLookupMisses):F1} " +
                $"decodedBitmapLoads={cachePerformance.DecodedBitmapLoads} " +
                $"decodedBitmapLoadFailures={cachePerformance.DecodedBitmapLoadFailures} " +
                $"decodedBitmapLoadAvgMs={CalculateAverageDurationMilliseconds(cachePerformance.DecodedBitmapLoadDurationTicks, cachePerformance.DecodedBitmapLoads):F1} " +
                $"decodedBitmapLoadMaxMs={TimeSpan.FromTicks(cachePerformance.DecodedBitmapMaxLoadDurationTicks).TotalMilliseconds:F1} " +
                 $"quickCaptureDetailImageDecodes={QuickCaptureDetailImageDecodeCount} " +
                 $"quickCaptureDetailImageMB={QuickCaptureDetailImageEstimatedBytes / (1024.0 * 1024):F1} " +
                 $"glanceCompactBackgroundDecodes={GlanceCompactBackgroundDecodeCount} " +
                 $"glanceCompactBackgroundMB={GlanceCompactBackgroundEstimatedBytes / (1024.0 * 1024):F1} " +
                 $"musicCoverDecodes={MusicCoverDecodeCount} " +
                 $"markdownRenders={MarkdownRenderCount} " +
                 $"markdownImageDecodes={MarkdownInlineImageDecodeCount} " +
                 $"musicTimers={ActiveMusicTimerCount} " +
                 $"musicTimerIntervalMs={MusicProgressTimerIntervalMs} " +
                 $"transientUiTimers={ActiveTransientUiTimerCount} " +
                 $"transientUiTimersCreated={TransientUiTimerCreatedCount} " +
                 $"transientUiTimersReleased={TransientUiTimerReleasedCount} " +
                 $"windows={windowCount} " +
                $"loadedWidgets={loadedWidgetCount} " +
                 $"visibleWidgets={visibleWidgetCount} " +
                 $"surfaceSwitchGates={surfaceSwitchGateCount} " +
                 $"activeFolderWatchers={activeFolderWatcherCount} " +
                 $"cachedGroupContents={cachedGroupContentCount} " +
                  $"searchEnabled={searchEnabled} " +
                 $"everythingState={everythingState} " +
                 $"everythingConnected={app.IsEverythingSearchConnected} " +
                 $"searchPopupCreated={app.IsSearchPopupCreated} " +
                $"searchPopupVisible={app.IsSearchPopupVisible} " +
                $"searchMetaCache={app.SearchMetaCacheCount}" +
                (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" reason={reason}"));
        }
        catch
        {
            // Best-effort diagnostics — never crash the app.
        }
    }

    public static IDisposable Measure(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return EmptyScope.Instance;
        }

        return new Scope(operation, details);
    }

    public static void Mark(string operation, string? details = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        App.Log(FormatMessage(operation, null, details));
    }

    internal static bool IsEnabledSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalizedValue = value.Trim();
        return normalizedValue switch
        {
            "1" => true,
            _ when normalizedValue.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ when normalizedValue.Equals("enabled", StringComparison.OrdinalIgnoreCase) => true,
            _ => false
        };
    }

    internal static double CalculateHitRatePercent(long hits, long misses)
    {
        long lookupCount = Math.Max(0, hits) + Math.Max(0, misses);
        return lookupCount == 0
            ? 0
            : Math.Max(0, hits) * 100.0 / lookupCount;
    }

    internal static double CalculateAverageDurationMilliseconds(
        long totalDurationTicks,
        long sampleCount) =>
        sampleCount <= 0
            ? 0
            : TimeSpan.FromTicks(Math.Max(0, totalDurationTicks))
                .TotalMilliseconds / sampleCount;

    private static CachePerformanceSnapshot CaptureCachePerformanceSnapshot() =>
        new(
            Interlocked.Read(ref s_thumbnailCacheLookupHitCount),
            Interlocked.Read(ref s_thumbnailCacheLookupMissCount),
            Interlocked.Read(ref s_thumbnailCacheLoadCount),
            Interlocked.Read(ref s_thumbnailCacheLoadFailureCount),
            Interlocked.Read(ref s_thumbnailCacheLoadDurationTicks),
            Interlocked.Read(ref s_thumbnailCacheMaxLoadDurationTicks),
            Interlocked.Read(ref s_decodedBitmapCacheLookupHitCount),
            Interlocked.Read(ref s_decodedBitmapCacheLookupMissCount),
            Interlocked.Read(ref s_decodedBitmapCacheLoadCount),
            Interlocked.Read(ref s_decodedBitmapCacheLoadFailureCount),
            Interlocked.Read(ref s_decodedBitmapCacheLoadDurationTicks),
            Interlocked.Read(ref s_decodedBitmapCacheMaxLoadDurationTicks));

    private static void RecordCacheLoad(
        TimeSpan elapsed,
        bool succeeded,
        ref long loadCount,
        ref long failureCount,
        ref long totalDurationTicks,
        ref long maxDurationTicks)
    {
        long elapsedTicks = Math.Max(0, elapsed.Ticks);
        Interlocked.Increment(ref loadCount);
        if (!succeeded)
        {
            Interlocked.Increment(ref failureCount);
        }

        Interlocked.Add(ref totalDurationTicks, elapsedTicks);
        UpdateMaximum(ref maxDurationTicks, elapsedTicks);
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long observed = Interlocked.Read(ref target);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(
                ref target,
                value,
                observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private readonly record struct CachePerformanceSnapshot(
        long ThumbnailLookupHits,
        long ThumbnailLookupMisses,
        long ThumbnailLoads,
        long ThumbnailLoadFailures,
        long ThumbnailLoadDurationTicks,
        long ThumbnailMaxLoadDurationTicks,
        long DecodedBitmapLookupHits,
        long DecodedBitmapLookupMisses,
        long DecodedBitmapLoads,
        long DecodedBitmapLoadFailures,
        long DecodedBitmapLoadDurationTicks,
        long DecodedBitmapMaxLoadDurationTicks);

    private static string FormatMessage(string operation, double? elapsedMs, string? details)
    {
        string message = elapsedMs.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"[Perf] {operation} elapsedMs={elapsedMs.Value:F1}")
            : $"[Perf] {operation}";

        if (string.IsNullOrWhiteSpace(details))
        {
            return message;
        }

        return $"{message} {details.ReplaceLineEndings(" ")}";
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _operation;
        private readonly string? _details;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(string operation, string? details)
        {
            _operation = operation;
            _details = details;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            App.Log(FormatMessage(_operation, _stopwatch.Elapsed.TotalMilliseconds, _details));
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}
