using DeskBox.Platform;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskBox.Helpers;

internal readonly record struct StaOperationResult<T>(
    bool Started,
    T? Value = default,
    TimeSpan QueueWait = default);

/// <summary>
/// A watchdog declared the underlying native Shell operation dead after it
/// showed no progress for the whole inactivity window. The STA thread itself
/// could not be aborted (COM STA affinity: native Shell calls cannot be
/// safely aborted from another thread), so it was abandoned — it keeps
/// leaking until it ever returns, and the abandonment is counted and logged.
/// The failure is retryable: a fresh worker is built for the next request.
/// </summary>
internal sealed class StaOperationAbandonedException : TimeoutException
{
    internal StaOperationAbandonedException(
        TimeSpan idleDuration,
        TimeSpan inactivityTimeout,
        long processAbandonmentCount,
        string workerName)
        : base(
            $"The STA Shell operation '{workerName}' was abandoned after " +
            $"{(long)idleDuration.TotalMilliseconds}ms without Shell progress " +
            $"(threshold {(long)inactivityTimeout.TotalMilliseconds}ms; " +
            $"process abandonment #{processAbandonmentCount}). The worker " +
            $"thread is left to leak by design; retry the operation.")
    {
        IdleDuration = idleDuration;
        InactivityTimeout = inactivityTimeout;
        ProcessAbandonmentCount = processAbandonmentCount;
        WorkerName = workerName;
    }

    internal TimeSpan IdleDuration { get; }

    internal TimeSpan InactivityTimeout { get; }

    internal long ProcessAbandonmentCount { get; }

    internal string WorkerName { get; }
}

/// <summary>
/// Short-lived STA workers for synchronous Shell operations. Both the running
/// calls and the waiting queue are bounded. A cancelled caller must not release
/// a running call's slot: native Shell calls cannot be safely aborted. The
/// one sanctioned exception is an opted-in <see cref="StaOperationWatchdog"/>:
/// it declares the operation dead only after no Shell progress for the whole
/// inactivity window, then abandons the (unabortable) thread and releases the
/// slot so the next request gets a fresh worker.
/// </summary>
internal sealed class BoundedStaOperationRunner
{
    /// <summary>COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE.</summary>
    internal const int DefaultCoInitializationFlags = 0x2 | 0x4;
    private static readonly TimeSpan DefaultIdleWorkerKeepAlive = TimeSpan.FromSeconds(30);

    private static long s_abandonedOperations;

    private readonly SemaphoreSlim _workers;
    private readonly SemaphoreSlim _admission;
    private readonly TimeSpan _queueTimeout;
    private readonly TimeSpan _idleKeepAlive;
    private readonly int _maxConcurrency;
    private readonly string _threadName;
    private readonly int _coInitializationFlags;
    private readonly object _idleLock = new();
    private readonly List<WarmStaWorker> _idleWorkers = [];

    /// <summary>Process-wide count of watchdog-abandoned operations.</summary>
    internal static long TotalAbandonedOperations =>
        Volatile.Read(ref s_abandonedOperations);

    internal BoundedStaOperationRunner(
        int maxConcurrency,
        int maxQueued,
        TimeSpan queueTimeout,
        string threadName = "DeskBox File Open",
        int coInitializationFlags = DefaultCoInitializationFlags,
        TimeSpan? idleKeepAlive = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueued);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(queueTimeout, TimeSpan.Zero);
        _maxConcurrency = maxConcurrency;
        _workers = new(maxConcurrency, maxConcurrency);
        _admission = new(maxConcurrency + maxQueued, maxConcurrency + maxQueued);
        _queueTimeout = queueTimeout;
        _idleKeepAlive = idleKeepAlive ?? DefaultIdleWorkerKeepAlive;
        _threadName = threadName;
        _coInitializationFlags = coInitializationFlags;
    }

    internal async Task<StaOperationResult<T>> RunAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken,
        StaOperationWatchdog? watchdog = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_admission.Wait(0))
        {
            return new(false);
        }

        try
        {
            Stopwatch queueStopwatch = Stopwatch.StartNew();
            if (!await _workers.WaitAsync(_queueTimeout, cancellationToken).ConfigureAwait(false))
            {
                return new(false, QueueWait: queueStopwatch.Elapsed);
            }

            TimeSpan queueWait = queueStopwatch.Elapsed;
            try
            {
                // Do not WaitAsync(cancellationToken) here: the worker owns the
                // capacity until the underlying native operation really ends.
                // A watchdog abandonment is the only sanctioned release: it
                // fires when the operation has been silent for its whole
                // inactivity window, and frees the slot for a rebuilt worker.
                T value = watchdog is null
                    ? await RunOnStaAsync(operation, cancellationToken).ConfigureAwait(false)
                    : await RunOnStaWithWatchdogAsync(operation, cancellationToken, watchdog)
                        .ConfigureAwait(false);
                return new(true, value, queueWait);
            }
            finally
            {
                _workers.Release();
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    private async Task<T> RunOnStaWithWatchdogAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken,
        StaOperationWatchdog watchdog)
    {
        // The abandoned thread's late completion lands on an unreferenced
        // task; nothing observes it, so a zombie that eventually unwinds
        // cannot corrupt the caller that already received the failure.
        (Task<T> Completion, Thread Worker, WarmStaWorker PoolWorker) started = StartOnSta(
            () =>
            {
                watchdog.Pulse();
                return operation();
            },
            cancellationToken);
        while (true)
        {
            Task finished = await Task.WhenAny(
                started.Completion,
                Task.Delay(watchdog.PollInterval)).ConfigureAwait(false);
            if (ReferenceEquals(finished, started.Completion) ||
                started.Completion.IsCompleted)
            {
                return await started.Completion.ConfigureAwait(false);
            }

            if (!watchdog.ShouldAbandon(
                    watchdog.ReadClockMilliseconds(),
                    started.Worker,
                    out TimeSpan idleDuration))
            {
                continue;
            }

            started.PoolWorker.MarkAbandoned();
            long count = Interlocked.Increment(ref s_abandonedOperations);
            App.Log(
                $"[StaRunner] Watchdog abandoned worker={_threadName}: no Shell " +
                $"progress for {(long)idleDuration.TotalMilliseconds}ms " +
                $"(threshold={(long)watchdog.InactivityTimeout.TotalMilliseconds}ms). " +
                $"The STA thread cannot be aborted and now leaks by design; the " +
                $"worker slot is released for a fresh thread. " +
                $"processAbandonments={count}");
            throw new StaOperationAbandonedException(
                idleDuration,
                watchdog.InactivityTimeout,
                count,
                _threadName);
        }
    }

    private Task<T> RunOnStaAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        return StartOnSta(operation, cancellationToken).Completion;
    }

    private (Task<T> Completion, Thread Worker, WarmStaWorker PoolWorker) StartOnSta<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Exception?> workItem = initError =>
        {
            if (initError is not null)
            {
                completion.TrySetException(initError);
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                T result = operation();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        };

        try
        {
            WarmStaWorker worker = RentOrStartWorker(workItem);
            return (completion.Task, worker.Thread, worker);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            return (completion.Task, Thread.CurrentThread, WarmStaWorker.DetachedSentinel);
        }
    }

    private WarmStaWorker RentOrStartWorker(Action<Exception?> workItem)
    {
        if (_idleKeepAlive > TimeSpan.Zero)
        {
            lock (_idleLock)
            {
                while (_idleWorkers.Count > 0)
                {
                    int lastIndex = _idleWorkers.Count - 1;
                    WarmStaWorker idle = _idleWorkers[lastIndex];
                    _idleWorkers.RemoveAt(lastIndex);
                    idle.InIdlePool = false;
                    if (idle.TryAssignAndWake(workItem))
                    {
                        return idle;
                    }
                }
            }
        }

        return WarmStaWorker.CreateAndStart(this, workItem);
    }

    private bool TryParkIdleWorker(WarmStaWorker worker)
    {
        if (_idleKeepAlive <= TimeSpan.Zero || worker.IsAbandoned)
        {
            return false;
        }

        lock (_idleLock)
        {
            if (worker.IsAbandoned || _idleWorkers.Count >= _maxConcurrency)
            {
                return false;
            }

            worker.InIdlePool = true;
            _idleWorkers.Add(worker);
            return true;
        }
    }

    private bool TryEvictTimedOutWorker(WarmStaWorker worker)
    {
        lock (_idleLock)
        {
            if (!worker.InIdlePool)
            {
                return false;
            }

            _idleWorkers.Remove(worker);
            worker.InIdlePool = false;
            return true;
        }
    }

    private sealed class WarmStaWorker
    {
        internal static readonly WarmStaWorker DetachedSentinel = new();

        private readonly BoundedStaOperationRunner? _owner;
        private readonly AutoResetEvent? _wakeEvent;
        private Action<Exception?>? _pendingWork;
        private volatile bool _abandoned;

        private WarmStaWorker()
        {
            Thread = Thread.CurrentThread;
            _abandoned = true;
        }

        private WarmStaWorker(
            BoundedStaOperationRunner owner,
            Action<Exception?> initialWork)
        {
            _owner = owner;
            _pendingWork = initialWork;
            _wakeEvent = new AutoResetEvent(false);
            var thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = owner._threadName
            };
            thread.SetApartmentState(ApartmentState.STA);
            Thread = thread;
        }

        internal Thread Thread { get; }

        internal bool InIdlePool { get; set; }

        internal bool IsAbandoned => _abandoned;

        internal void MarkAbandoned() => _abandoned = true;

        internal static WarmStaWorker CreateAndStart(
            BoundedStaOperationRunner owner,
            Action<Exception?> initialWork)
        {
            var worker = new WarmStaWorker(owner, initialWork);
            worker.Thread.Start();
            return worker;
        }

        internal bool TryAssignAndWake(Action<Exception?> work)
        {
            if (_abandoned || _wakeEvent is null)
            {
                return false;
            }

            Interlocked.Exchange(ref _pendingWork, work);
            _wakeEvent.Set();
            return true;
        }

        private void RunLoop()
        {
            BoundedStaOperationRunner owner = _owner!;
            AutoResetEvent wakeEvent = _wakeEvent!;

            // Explicitly initialize COM even under Native AOT, where setting
            // the managed apartment flag alone is not enough. Keep the STA
            // apartment warm across pooled dispatches so ShellExecuteEx does
            // not pay cold CreateThread + CoInitializeEx/CoUninitialize churn.
            int hresult = Ole32NativeMethods.CoInitializeEx(
                IntPtr.Zero,
                (uint)owner._coInitializationFlags);
            if (hresult < 0)
            {
                Exception initException = Marshal.GetExceptionForHR(hresult) ??
                    new COMException("CoInitializeEx failed.", hresult);
                Interlocked.Exchange(ref _pendingWork, null)?.Invoke(initException);
                wakeEvent.Dispose();
                return;
            }

            try
            {
                while (true)
                {
                    Action<Exception?>? work = Interlocked.Exchange(ref _pendingWork, null);
                    work?.Invoke(null);

                    if (_abandoned || !owner.TryParkIdleWorker(this))
                    {
                        break;
                    }

                    bool signaled = wakeEvent.WaitOne(owner._idleKeepAlive);
                    if (signaled)
                    {
                        continue;
                    }

                    if (owner.TryEvictTimedOutWorker(this))
                    {
                        break;
                    }

                    // A caller popped and signaled this worker right as WaitOne
                    // timed out; drain the wake signal and execute _pendingWork.
                    _ = wakeEvent.WaitOne(0);
                }
            }
            finally
            {
                Ole32NativeMethods.CoUninitialize();
                wakeEvent.Dispose();
            }
        }
    }
}
