using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Owns background diagnostics: periodic memory sampling, forced GC cleanup,
/// and the UI-thread responsiveness watchdog.
/// Extracted from App.xaml.cs to reduce God Class complexity.
/// </summary>
public sealed class AppDiagnosticsService : IDisposable
{
    private DispatcherQueueTimer? _memoryDiagnosticTimer;
    private CancellationTokenSource? _periodicGcCts;
    private System.Threading.Timer? _uiWatchdogTimer;
    private volatile bool _uiHeartbeatReceived;
    private int _watchdogMissCount;
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
        SchedulePeriodicMemoryCleanup();
        StartUiThreadWatchdog();
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
        _memoryDiagnosticTimer.Tick += (_, _) => PerformanceLogger.SampleMemory();
        _memoryDiagnosticTimer.Start();
        App.Log("[Perf] Memory diagnostics timer started (30s interval)");
    }

    /// <summary>
    /// Starts a background loop that periodically triggers GC to release
    /// native Composition/COM resources that the WinUI framework holds via
    /// finalizable wrappers.
    /// </summary>
    private void SchedulePeriodicMemoryCleanup()
    {
        _periodicGcCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _periodicGcCts.Token);

                while (!_periodicGcCts.Token.IsCancellationRequested)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                    Helpers.Win32Helper.TrimCurrentProcessWorkingSet();

                    if (PerformanceLogger.IsEnabled)
                    {
                        PerformanceLogger.SampleMemory();
                        App.Log("[Perf] Periodic GC cleanup completed");
                    }

                    await Task.Delay(TimeSpan.FromMinutes(2), _periodicGcCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                App.Log($"[Perf] Periodic GC cleanup error: {ex}");
            }
        });
        App.Log("[Perf] Periodic memory cleanup scheduled (2 min interval)");
    }

    /// <summary>
    /// Starts a watchdog that detects when the UI thread is unresponsive.
    /// A background timer sets a heartbeat flag every 4 seconds; the UI thread
    /// clears it via DispatcherQueue.TryEnqueue. If the flag is still set on
    /// the next tick, the UI thread was blocked for more than 4 seconds.
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
                        App.Log("[Watchdog] 5 consecutive misses — forcing GC.Collect");
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
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
        }, null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4));

        App.Log("[Watchdog] UI thread watchdog started (4s interval)");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _memoryDiagnosticTimer?.Stop();
        _memoryDiagnosticTimer = null;
        _periodicGcCts?.Cancel();
        _periodicGcCts?.Dispose();
        _periodicGcCts = null;
        _uiWatchdogTimer?.Dispose();
        _uiWatchdogTimer = null;
    }
}
