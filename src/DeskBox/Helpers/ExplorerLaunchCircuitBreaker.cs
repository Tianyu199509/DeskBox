using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// Stops the Explorer-hosted launch path from being retried against a sick
/// shell (#455). Every explorer-hosted open performs several synchronous
/// cross-process COM calls on the Explorer desktop thread, so once Explorer
/// starts answering with RPC-class failures, each further click re-pays the
/// same storm and can drag Explorer into a hang or crash. The breaker opens
/// on RPC-class failures only and closes again when the shell process
/// identity changes — that is, Explorer actually restarted — or, once a
/// cooldown has elapsed, when a single released half-open probe launch
/// succeeds; a failing probe restarts the cooldown. Direct
/// <see cref="ExplorerShellLaunchService"/> callers such as the AOT shell
/// smoke diagnostics bypass the breaker by design: they must observe the real
/// Explorer path, not a locally recovered one.
/// </summary>
internal static class ExplorerLaunchCircuitBreaker
{
    // One RPC-class failure is already the signature of a wedged desktop
    // thread; a second attempt only re-pays the same cross-process hang.
    internal const int RpcFailureThreshold = 1;

    private static readonly ExplorerLaunchCircuitBreakerState State =
        new(RpcFailureThreshold, ProbeShellProcessId);

    internal static bool ShouldBypassExplorerHostedLaunch() =>
        State.ShouldBypass();

    internal static void RecordFailure(int hresult) =>
        State.RecordFailure(hresult);

    internal static void RecordSuccess() =>
        State.RecordSuccess();

    internal static void Reset() =>
        State.Reset();

    internal static bool IsRpcClassFailure(int hresult) =>
        ExplorerLaunchCircuitBreakerState.IsRpcClassFailure(hresult);

    private static uint ProbeShellProcessId()
    {
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return 0;
        }

        _ = Win32Helper.GetWindowThreadProcessId(shellWindow, out uint processId);
        return processId;
    }
}

/// <summary>
/// The breaker's decision state, separated from the Win32 shell probe so the
/// transitions can be exercised without a live desktop.
/// </summary>
internal sealed class ExplorerLaunchCircuitBreakerState
{
    // An HRESULT with FACILITY_WIN32 wrapping a Win32 RPC error (1700-1799)
    // covers RPC_S_SERVER_UNAVAILABLE (0x800706BA), RPC_S_CALL_FAILED
    // (0x800706BE) and friends: the transport died on the way to the shell.
    // User cancellations, missing associations and plain Win32 errors must
    // not read as a broken desktop.
    private const int FacilityWin32 = 7;
    private const int RpcWin32ErrorLow = 1700;
    private const int RpcWin32ErrorHigh = 1799;

    // How long an open breaker keeps bypassing before releasing a single
    // half-open probe launch to test whether the shell has recovered.
    private static readonly TimeSpan DefaultOpenCooldown = TimeSpan.FromMinutes(5);
    private static readonly Func<long> DefaultClock = static () => Environment.TickCount64;

    private readonly object _gate = new();
    private readonly int _rpcFailureThreshold;
    private readonly Func<uint> _probeShellProcessId;
    private readonly Func<long> _clock;
    private readonly TimeSpan _openCooldown;

    private int _consecutiveRpcFailures;
    private bool _open;
    private uint _shellProcessIdAtOpen;
    private long _openedAtTick;
    private bool _halfOpenProbeGranted;

    internal ExplorerLaunchCircuitBreakerState(
        int rpcFailureThreshold,
        Func<uint> probeShellProcessId,
        Func<long>? clock = null,
        TimeSpan? openCooldown = null)
    {
        _rpcFailureThreshold = rpcFailureThreshold;
        _probeShellProcessId = probeShellProcessId;
        _clock = clock ?? DefaultClock;
        _openCooldown = openCooldown ?? DefaultOpenCooldown;
    }

    internal static bool IsRpcClassFailure(int hresult)
    {
        int facility = (hresult >> 16) & 0x7FF;
        if (facility != FacilityWin32)
        {
            return false;
        }

        int win32Error = hresult & 0xFFFF;
        return win32Error is >= RpcWin32ErrorLow and <= RpcWin32ErrorHigh;
    }

    internal bool ShouldBypass()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return false;
            }

            uint currentShellProcessId = _probeShellProcessId();
            if (currentShellProcessId == 0)
            {
                // No shell window at all: keep the breaker open. A missing
                // desktop cannot host launches anyway, and the next probe
                // will notice a restarted shell.
                return true;
            }

            if (currentShellProcessId != _shellProcessIdAtOpen)
            {
                // The shell process changed since the breaker opened, so
                // Explorer was restarted: give the explorer-hosted path a
                // fresh chance. The identity reset outranks the cooldown.
                _consecutiveRpcFailures = 0;
                _open = false;
                _shellProcessIdAtOpen = 0;
                _openedAtTick = 0;
                _halfOpenProbeGranted = false;
                return false;
            }

            if (_halfOpenProbeGranted)
            {
                // A half-open probe launch is still outstanding: keep
                // bypassing until it resolves via RecordSuccess or
                // RecordFailure.
                return true;
            }

            long cooldownElapsedMs = _clock() - _openedAtTick;
            if (cooldownElapsedMs < _openCooldown.TotalMilliseconds)
            {
                // Still cooling down from the last RPC-class failure.
                return true;
            }

            // Cooldown elapsed: release exactly one real launch as the
            // half-open probe. No extra COM probing — the probe is the
            // launch itself; its outcome closes the breaker or restarts
            // the cooldown.
            _halfOpenProbeGranted = true;
        }

        App.Log(
            "[ExplorerLaunchCircuitBreaker] Cooldown elapsed; releasing one half-open probe launch.");
        return false;
    }

    internal void RecordFailure(int hresult)
    {
        lock (_gate)
        {
            if (!IsRpcClassFailure(hresult))
            {
                // Non-RPC failures say nothing about shell health and never
                // accumulate toward the threshold. One observed while a
                // half-open probe is outstanding still means the probe launch
                // happened and did not succeed: consume the probe and restart
                // the cooldown rather than letting a sick shell ping-pong
                // straight into further probes.
                if (_open && _halfOpenProbeGranted)
                {
                    _halfOpenProbeGranted = false;
                    _openedAtTick = _clock();
                }

                return;
            }

            _consecutiveRpcFailures++;
            if (!_open && _consecutiveRpcFailures >= _rpcFailureThreshold)
            {
                _open = true;
                _shellProcessIdAtOpen = _probeShellProcessId();
                _openedAtTick = _clock();
                _halfOpenProbeGranted = false;
            }
            else if (_open)
            {
                // Already open (cooling or half-open): the shell is still
                // sick, so restart the cooldown window from now.
                _openedAtTick = _clock();
                _halfOpenProbeGranted = false;
            }
        }
    }

    internal void RecordSuccess()
    {
        lock (_gate)
        {
            // A successful explorer-hosted launch — including a half-open
            // probe — is proof of a healthy shell: fully close the breaker.
            _consecutiveRpcFailures = 0;
            _open = false;
            _shellProcessIdAtOpen = 0;
            _openedAtTick = 0;
            _halfOpenProbeGranted = false;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _consecutiveRpcFailures = 0;
            _open = false;
            _shellProcessIdAtOpen = 0;
            _openedAtTick = 0;
            _halfOpenProbeGranted = false;
        }
    }
}
