using Microsoft.UI.Dispatching;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Owns opt-in memory diagnostics and the UI-thread responsiveness watchdog.
/// Extracted from App.xaml.cs to reduce God Class complexity.
/// </summary>
public sealed class AppDiagnosticsService : IDisposable
{
    private DispatcherQueueTimer? _memoryDiagnosticTimer;
    private System.Threading.Timer? _uiWatchdogTimer;
    private volatile bool _uiHeartbeatReceived;
    private int _watchdogMissCount;
    private int _lifecycleEventCount;
    private DateTimeOffset? _lastLifecycleEventAt;
    private string _lastLifecycleReason = string.Empty;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isDisposed;

    public AppDiagnosticsService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Starts all diagnostic loops. Called once during app startup.
    /// </summary>
    public void StartAll()
    {
        ScheduleMemoryDiagnostics();
        if (PerformanceLogger.IsEnabled || App.IsVerboseLoggingEnabled)
        {
            StartUiThreadWatchdog();
        }
        else
        {
            App.Log("[Watchdog] Disabled; enable performance or verbose logging to opt in");
        }
    }

    public int LifecycleEventCount => Volatile.Read(ref _lifecycleEventCount);
    public DateTimeOffset? LastLifecycleEventAt => _lastLifecycleEventAt;
    public string LastLifecycleReason => _lastLifecycleReason;

    public AppRuntimeHealthSnapshot GetRuntimeHealthSnapshot(
        EverythingSearchService? everythingSearch,
        IEnumerable<FolderWatcherHealthSnapshot>? folderWatchers = null)
    {
        var folderHealth = folderWatchers?.ToList() ?? [];
        EverythingConnectionSnapshot? everything = everythingSearch?.CurrentSnapshot;
        return new AppRuntimeHealthSnapshot(
            LifecycleEventCount,
            LastLifecycleEventAt,
            LastLifecycleReason,
            SearchWatcherCount: 0,
            SearchWatcherRecoveryCount: 0,
            LastSearchWatcherRecoveryTime: null,
            IndexedEntryCount: 0,
            IsSearchScanning: everything?.State == EverythingConnectionState.Checking,
            LastSearchScanTime: null,
            FailedSearchWatcherCount: 0,
            OfflineSearchRootCount: 0,
            PartialSearchRootCount: 0,
            SearchScanCapacityLimited: false,
            IsUsnIndexAvailable: false,
            IsUsnIndexScanning: false,
            IsUsnIndexIncrementalSyncing: false,
            OfflineFolderCount: folderHealth.Count(item => item.Status == FolderWatcherHealth.Unavailable),
            DegradedFolderCount: folderHealth.Count(item => item.Status == FolderWatcherHealth.Degraded),
            AccessDeniedFolderCount: folderHealth.Count(item => item.Status == FolderWatcherHealth.AccessDenied),
            EverythingState: everything?.State,
            EverythingVersion: everything?.Version);
    }

    /// <summary>
    /// Records a lifecycle recovery signal alongside the regular watchdog
    /// diagnostics so field logs show whether external-state recovery ran.
    /// </summary>
    public void RecordLifecycleEvent(string reason)
    {
        int count = Interlocked.Increment(ref _lifecycleEventCount);
        _lastLifecycleEventAt = DateTimeOffset.Now;
        _lastLifecycleReason = reason;
        App.Log($"[Diagnostics] Lifecycle recovery #{count} reason={reason}");
    }

    /// <summary>
    /// When perf logging is enabled, samples memory and cache statistics
    /// every 30 seconds so long-running memory trends can be observed.
    /// </summary>
    private void ScheduleMemoryDiagnostics()
    {
        if (!PerformanceLogger.IsEnabled)
        {
            return;
        }

        _memoryDiagnosticTimer = _dispatcherQueue.CreateTimer();
        _memoryDiagnosticTimer.Interval = TimeSpan.FromSeconds(30);
        _memoryDiagnosticTimer.IsRepeating = true;
        _memoryDiagnosticTimer.Tick += MemoryDiagnosticTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        _memoryDiagnosticTimer.Start();
        App.Log("[Perf] Memory diagnostics timer started (30s interval)");
    }

    private static void MemoryDiagnosticTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        PerformanceLogger.SampleMemory();
    }

    /// <summary>
    /// Starts a watchdog that detects when the UI thread is unresponsive.
    /// A background timer sets a heartbeat flag every 15 seconds; the UI thread
    /// clears it via DispatcherQueue.TryEnqueue. If the flag is still set on
    /// the next tick, the UI thread was blocked for more than 15 seconds.
    /// The interval trades wake-up cost against forensic granularity: the
    /// watchdog is diagnostic-only and no recovery action depends on it.
    /// </summary>
    private void StartUiThreadWatchdog()
    {
        _uiHeartbeatReceived = true;

        _uiWatchdogTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (!_uiHeartbeatReceived)
                {
                    int missCount = Interlocked.Increment(ref _watchdogMissCount);
                    int handleCount = 0;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetCurrentProcess();
                        handleCount = proc.HandleCount;
                    }
                    catch { }

                    App.Log($"[Watchdog] UI thread unresponsive (miss #{missCount}), " +
                        $"handles={handleCount}, " +
                        $"gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}");

                    if (missCount == 5)
                    {
                        // A blocked UI thread cannot be repaired by forcing a collection.
                        // In particular, waiting for WinUI/COM finalizers while the UI STA is
                        // unresponsive can make the original stall harder to recover from.
                        // Keep the watchdog diagnostic-only and let the runtime manage GC.
                        App.Log("[Watchdog] 5 consecutive misses — diagnostics only; forced GC suppressed");
                    }
                }
                else
                {
                    if (Interlocked.Exchange(ref _watchdogMissCount, 0) > 0)
                    {
                        App.Log("[Watchdog] UI thread recovered");
                    }
                }

                _uiHeartbeatReceived = false;
                _dispatcherQueue?.TryEnqueue(() => _uiHeartbeatReceived = true);
            }
            catch (Exception ex)
            {
                App.Log($"[Watchdog] Error: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        App.Log("[Watchdog] UI thread watchdog started (15s interval)");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_memoryDiagnosticTimer is not null)
        {
            _memoryDiagnosticTimer.Stop();
            _memoryDiagnosticTimer.Tick -= MemoryDiagnosticTimer_Tick;
            _memoryDiagnosticTimer = null;
            PerformanceLogger.RecordTransientUiTimerReleased();
        }
        _uiWatchdogTimer?.Dispose();
        _uiWatchdogTimer = null;
    }
}

public sealed record AppRuntimeHealthSnapshot(
    int LifecycleEventCount,
    DateTimeOffset? LastLifecycleEventAt,
    string LastLifecycleReason,
    int SearchWatcherCount,
    int SearchWatcherRecoveryCount,
    DateTime? LastSearchWatcherRecoveryTime,
    int IndexedEntryCount,
    bool IsSearchScanning,
    DateTime? LastSearchScanTime,
    int FailedSearchWatcherCount = 0,
    int OfflineSearchRootCount = 0,
    int PartialSearchRootCount = 0,
    bool SearchScanCapacityLimited = false,
    bool IsUsnIndexAvailable = false,
    bool IsUsnIndexScanning = false,
    bool IsUsnIndexIncrementalSyncing = false,
    int OfflineFolderCount = 0,
    int DegradedFolderCount = 0,
    int AccessDeniedFolderCount = 0,
    EverythingConnectionState? EverythingState = null,
    string? EverythingVersion = null);
