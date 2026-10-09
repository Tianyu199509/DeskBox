using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// Coalesces native per-window display messages and global topology polling into
/// one stable, generation-based restore operation.
/// </summary>
internal sealed class DisplayTopologyTransitionCoordinator : IDisposable
{
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(180);
    internal static readonly TimeSpan VerificationDelay = TimeSpan.FromMilliseconds(140);
    private const int MaxRestoreRetryCount = 8;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<string> _signatureProvider;
    private readonly Func<long, string, Task<bool>> _restoreAction;
    private readonly DisplayTopologyStabilityTracker _stabilityTracker = new(requiredObservations: 2);

    private long _generation;
    private string _pendingReasons = string.Empty;
    private int _restoreRetryCount;
    private bool _verificationPending;
    private bool _isExecuting;
    private bool _isDisposed;
    private string _lastBlockedReasons = string.Empty;

    /// <summary>
    /// Application gate (spec 5.6). While closed, restore requests are
    /// parked: the latest reasons are recorded, no observation runs, and no
    /// retry budget is spent. Opening the gate re-requests automatically.
    /// </summary>
    internal DisplayTopologyGate Gate { get; } = new();

    public DisplayTopologyTransitionCoordinator(
        DispatcherQueue dispatcherQueue,
        Func<string> signatureProvider,
        Func<long, string, Task<bool>> restoreAction)
    {
        _dispatcherQueue = dispatcherQueue;
        _signatureProvider = signatureProvider;
        _restoreAction = restoreAction;
        _timer = dispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += Timer_Tick;
        Gate.GateOpened += _ => OnGateOpened();
    }

    private void OnGateOpened()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(OnGateOpened);
            return;
        }

        if (_isDisposed || string.IsNullOrEmpty(_pendingReasons))
        {
            return;
        }

        App.Log("[DisplayTopology] gate opened; resuming parked restore");
        RequestRestore("gate-open");
    }

    public void RequestRestore(string reason)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => RequestRestore(reason));
            return;
        }

        _generation++;
        _pendingReasons = CombineReasons(_pendingReasons, reason);
        _restoreRetryCount = 0;
        _verificationPending = false;
        _stabilityTracker.Reset();
        if (Gate.IsClosed)
        {
            string reasons = string.Join("|", Gate.ClosedReasons);
            if (!string.Equals(reasons, _lastBlockedReasons, StringComparison.Ordinal))
            {
                App.Log($"[DisplayTopology] gate closed reason={reasons}; parking restore");
                _lastBlockedReasons = reasons;
            }

            return;
        }

        _lastBlockedReasons = string.Empty;
        Schedule(ObservationInterval);
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        _timer.Stop();
        if (_isDisposed)
        {
            return;
        }

        if (_isExecuting)
        {
            Schedule(ObservationInterval);
            return;
        }

        long generation = _generation;
        string signature = CaptureSignature();
        bool signatureChanged = !string.Equals(
            signature,
            _stabilityTracker.LastSignature,
            StringComparison.Ordinal);
        if (signatureChanged && _verificationPending)
        {
            // The topology moved again after the first apply. Treat the new
            // stable signature as a fresh apply before scheduling verification.
            _verificationPending = false;
        }

        if (!_stabilityTracker.Observe(signature))
        {
            Schedule(ObservationInterval);
            return;
        }

        bool isVerification = _verificationPending;
        bool completed = true;
        _isExecuting = true;
        try
        {
            completed = await _restoreAction(generation, _pendingReasons);
        }
        catch (Exception ex)
        {
            completed = false;
            App.Log($"[DisplayTopology] Restore generation={generation} failed: {ex}");
        }
        finally
        {
            _isExecuting = false;
        }

        // A newer native or polling signal owns the next pass. RequestRestore
        // has already reset the tracker and scheduled its observation timer.
        if (_isDisposed || generation != _generation)
        {
            return;
        }

        if (!completed && _restoreRetryCount < MaxRestoreRetryCount)
        {
            _restoreRetryCount++;
            Schedule(ObservationInterval);
            return;
        }

        if (!isVerification)
        {
            _verificationPending = true;
            Schedule(VerificationDelay);
            return;
        }

        App.Log(
            $"[DisplayTopology] Restore completed generation={generation} " +
            $"reasons={_pendingReasons} signature={signature}");
        _pendingReasons = string.Empty;
        _verificationPending = false;
        _restoreRetryCount = 0;
    }

    private string CaptureSignature()
    {
        try
        {
            return _signatureProvider() ?? string.Empty;
        }
        catch (Exception ex)
        {
            App.Log($"[DisplayTopology] Signature capture failed: {ex.Message}");
            return string.Empty;
        }
    }

    private void Schedule(TimeSpan delay)
    {
        if (_isDisposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    internal static string CombineReasons(string current, string? next)
    {
        string normalized = string.IsNullOrWhiteSpace(next) ? "unspecified" : next.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            return normalized;
        }

        return current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(normalized, StringComparer.Ordinal)
                ? current
                : current + "," + normalized;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}

internal sealed class DisplayTopologyStabilityTracker
{
    private readonly int _requiredObservations;
    private string? _lastSignature;
    private int _observationCount;

    public DisplayTopologyStabilityTracker(int requiredObservations)
    {
        _requiredObservations = Math.Max(1, requiredObservations);
    }

    public string? LastSignature => _lastSignature;

    public bool Observe(string? signature)
    {
        string normalized = signature ?? string.Empty;
        if (!string.Equals(normalized, _lastSignature, StringComparison.Ordinal))
        {
            _lastSignature = normalized;
            _observationCount = 1;
        }
        else
        {
            _observationCount++;
        }

        return _observationCount >= _requiredObservations;
    }

    public void Reset()
    {
        _lastSignature = null;
        _observationCount = 0;
    }
}
