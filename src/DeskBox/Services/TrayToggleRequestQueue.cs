// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;

namespace DeskBox.Services;

internal sealed record TrayToggleQueueSnapshot(
    int PendingCount,
    bool WorkerRunning,
    long TotalRequests,
    long EffectiveToggles,
    long FoldedNoOpBatches,
    long SuppressedTrayIconDoubleClicks,
    string? LastSource,
    string? LastError);

/// <summary>
/// Serializes tray-toggle requests and folds a burst of toggles by parity.
/// A toggle is an intent, not a request to start another animation while a
/// previous animation is still being prepared. Folding the pending burst
/// prevents a rapid key repeat from creating an unbounded animation backlog.
/// </summary>
internal sealed class TrayToggleRequestQueue
{
    /// <summary>Source tag used by the tray icon's left-click command.</summary>
    internal const string TrayIconSource = "tray-icon";

    /// <summary>
    /// A second tray-icon click within this window of the previous one is a
    /// double-click: it completes without toggling instead of waiting for the
    /// queue and playing hide right after the reveal animation finished
    /// (feedback 237). The first click still toggles immediately.
    /// </summary>
    internal static readonly TimeSpan DefaultTrayIconDoubleClickWindow =
        TimeSpan.FromMilliseconds(350);

    private sealed record Request(string Source, TaskCompletionSource Completion);

    private readonly object _sync = new();
    private readonly Queue<Request> _pending = new();
    private readonly Func<string, Task> _toggleAsync;
    private readonly TimeSpan _trayIconDoubleClickWindow;
    private readonly Action? _onAcceptedWhileBusy;
    private bool _workerRunning;
    private long _totalRequests;
    private long _effectiveToggles;
    private long _foldedNoOpBatches;
    private long _suppressedTrayIconDoubleClicks;
    private long _lastTrayIconArrivalTimestamp;
    private string? _lastSource;
    private string? _lastError;

    public TrayToggleRequestQueue(
        Func<string, Task> toggleAsync,
        TimeSpan? trayIconDoubleClickWindow = null,
        Action? onAcceptedWhileBusy = null)
    {
        _toggleAsync = toggleAsync ?? throw new ArgumentNullException(nameof(toggleAsync));
        _trayIconDoubleClickWindow = trayIconDoubleClickWindow ?? DefaultTrayIconDoubleClickWindow;
        _onAcceptedWhileBusy = onAcceptedWhileBusy;
    }

    public Task EnqueueAsync(string source)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool startWorker;
        bool notifyBusy;
        int pendingCount;

        lock (_sync)
        {
            _totalRequests++;
            _lastSource = source;

            if (source == TrayIconSource && _trayIconDoubleClickWindow > TimeSpan.Zero)
            {
                long now = Stopwatch.GetTimestamp();
                bool isDoubleClick = _lastTrayIconArrivalTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_lastTrayIconArrivalTimestamp, now) <
                        _trayIconDoubleClickWindow;
                _lastTrayIconArrivalTimestamp = now;
                if (isDoubleClick)
                {
                    _suppressedTrayIconDoubleClicks++;
                    App.LogVerbose(
                        "[TrayToggle] suppressed tray-icon double-click click");
                    completion.TrySetResult();
                    return completion.Task;
                }
            }

            _pending.Enqueue(new Request(source, completion));
            pendingCount = _pending.Count;
            startWorker = !_workerRunning;
            notifyBusy = !startWorker && _onAcceptedWhileBusy is not null;
            _workerRunning = true;
        }

        App.LogVerbose(
            $"[TrayToggle] queued source={source} pending={pendingCount} " +
            $"startWorker={startWorker}");

        if (notifyBusy)
        {
            try
            {
                _onAcceptedWhileBusy!();
            }
            catch
            {
            }
        }

        if (startWorker)
        {
            _ = ProcessAsync();
        }

        return completion.Task;
    }

    public TrayToggleQueueSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new TrayToggleQueueSnapshot(
                _pending.Count,
                _workerRunning,
                _totalRequests,
                _effectiveToggles,
                _foldedNoOpBatches,
                _suppressedTrayIconDoubleClicks,
                _lastSource,
                _lastError);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Request[] batch;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _workerRunning = false;
                    return;
                }

                batch = _pending.ToArray();
                _pending.Clear();
            }

            // An even number of toggles returns to the same requested state.
            // Complete all requests after the batch has been accounted for so
            // callers never observe a request as completed while it is still
            // waiting in the queue.
            try
            {
                if ((batch.Length & 1) != 0)
                {
                    string source = batch[^1].Source;
                    lock (_sync)
                    {
                        _effectiveToggles++;
                        _lastSource = source;
                    }
                    App.LogVerbose(
                        $"[TrayToggle] processing source={source} batch={batch.Length} " +
                        $"effective=toggle");
                    await _toggleAsync(source);
                }
                else
                {
                    lock (_sync)
                    {
                        _foldedNoOpBatches++;
                    }
                    App.LogVerbose(
                        $"[TrayToggle] processing batch={batch.Length} effective=no-op");
                }

                foreach (Request request in batch)
                {
                    request.Completion.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError = ex.Message;
                }
                App.Log($"[TrayToggle] processing failed batch={batch.Length}: {ex}");
                foreach (Request request in batch)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
    }
}
