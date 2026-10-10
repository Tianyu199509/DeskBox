using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

/// <summary>
/// A service whose low-level hook should currently be listening. Windows
/// removes WH_KEYBOARD_LL/WH_MOUSE_LL hooks without any notification once a
/// starved callback misses enough deliveries (LowLevelHooksTimeout), so a
/// healthy-looking handle is not proof the hook is still wired in.
/// </summary>
internal interface IHookHealthProbeTarget
{
    /// <summary>Short diagnostic name for log lines.</summary>
    string ProbeName { get; }

    /// <summary>A low-level hook is expected to be listening right now.</summary>
    bool HookProbeWanted { get; }

    /// <summary>
    /// The hook thread/handle is gone — liveness probing is pointless, recover
    /// directly. Distinct from silent removal, which keeps IsActive true.
    /// </summary>
    bool HookConfirmedDead { get; }

    /// <summary>Environment.TickCount64 of the last delivered callback, or 0.</summary>
    long LastHookCallbackTicks { get; }

    /// <summary>
    /// Injects one tagged synthetic input event and waits for the hook to
    /// echo it. Returns false when nothing came back within the window.
    /// </summary>
    Task<bool> ProbeHookAliveAsync(int echoWaitMilliseconds);

    /// <summary>Re-registers the hook. Always invoked on the UI dispatcher.</summary>
    void RecoverHook();
}

/// <summary>
/// A RegisterHotKey-based registration the watchdog keeps alive with a
/// periodic unregister+register round-trip. winuser exposes no API to query
/// whether a hotkey is still registered (only RegisterHotKey and
/// UnregisterHotKey exist), and field reports (feedback 308/371) show chord
/// registrations silently stop delivering after session transitions, so the
/// only repair is to re-run the registration and let its return value report
/// conflicts. Implementations must skip the round-trip while input recording
/// suspends the registration.
/// </summary>
internal interface IHookRegistrationMaintenanceTarget
{
    /// <summary>Short diagnostic name for log lines.</summary>
    string MaintenanceName { get; }

    /// <summary>A chord registration is expected to be live right now.</summary>
    bool RegistrationMaintenanceWanted { get; }

    /// <summary>
    /// False when the most recent registration round-trip left no live
    /// registration. The watchdog evaluates this one cycle after issuing a
    /// heartbeat.
    /// </summary>
    bool RegistrationHealthy { get; }

    /// <summary>
    /// Re-runs the unregister+register round-trip. Always invoked on the UI
    /// dispatcher.
    /// </summary>
    void RunRegistrationHeartbeat();
}

/// <summary>
/// All timing knobs of <see cref="HookHealthWatchdog"/> in one place so the
/// decision logic can be exercised with synthetic clocks in tests. The
/// defaults encode the production values; every change here must justify
/// itself against the field data (feedback 445/450/502 diagnostics).
/// </summary>
internal sealed record HookWatchdogPolicy
{
    internal static HookWatchdogPolicy Default { get; } = new();

    /// <summary>Watchdog pass cadence; bounds detection latency.</summary>
    internal TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Divergence requires user input this recent (GetLastInputInfo): a
    /// silent hook is only suspicious while somebody is actively using the
    /// machine.
    /// </summary>
    internal uint RecentInputWindowMs { get; init; } = 10_000;

    /// <summary>
    /// Hook-callback silence (while input flows) that makes a target
    /// suspicious enough for a canary. Active-use dead window =
    /// this threshold plus one tick plus the canary echo wait.
    /// </summary>
    internal uint CallbackSilentThresholdMs { get; init; } = 20_000;

    /// <summary>
    /// How long a canary waits for its echo. LowLevelHooksTimeout is capped
    /// at 1000ms by the system (Windows 10 1709+), so 1200ms covers the worst
    /// legal callback latency.
    /// </summary>
    internal int CanaryEchoWaitMs { get; init; } = 1_200;

    /// <summary>
    /// After a canary echo, leave the target alone for this long: input
    /// flowing while a hook stays silent is the normal state of a
    /// keyboard-only (or mouse-only) session, and re-probing would feed
    /// synthetic input into the session once per window.
    /// </summary>
    internal long ProbeSuccessCooldownMs { get; init; } = 3 * 60_000;

    /// <summary>
    /// Minimum quiet time after a recovery before another recovery may run.
    /// Feedback 445/450 showed the old flat 5-minute cooldown itself became
    /// the dead window (the hook died again right after recovery and stayed
    /// dead for the whole cooldown), so the base is short and only escalates
    /// on rapid re-death.
    /// </summary>
    internal long RecoveryCooldownBaseMs { get; init; } = 90_000;

    /// <summary>Escalation ceiling for the recovery cooldown backoff.</summary>
    internal long RecoveryCooldownMaxMs { get; init; } = 8 * 60_000;

    /// <summary>
    /// A hook that dies again within this window after a recovery marks that
    /// recovery as a failure and escalates the cooldown — the signature of a
    /// starved process rather than an aggressive environment (feedback 445
    /// died every 15-45 minutes, which must NOT escalate).
    /// </summary>
    internal long RapidRedeathWindowMs { get; init; } = 2 * 60_000;

    /// <summary>Chord heartbeat cadence (RegisterHotKey round-trip).</summary>
    internal long MaintenanceIntervalMs { get; init; } = 90_000;

    /// <summary>Escalation ceiling for the heartbeat backoff.</summary>
    internal long MaintenanceMaxIntervalMs { get; init; } = 8 * 60_000;

    /// <summary>
    /// Grace before a heartbeat attempt is judged by the target's health —
    /// the round-trip runs asynchronously on the UI dispatcher and a busy UI
    /// thread may take a few ticks to get there.
    /// </summary>
    internal long MaintenanceEvaluationGraceMs { get; init; } = 30_000;
}

/// <summary>
/// Periodic health check for the low-level input hooks. Windows can silently
/// unhook a callback whose owning process is memory-trimmed or throttled for
/// too long, and nothing in the process notices until input is missed
/// permanently. Detection is two-staged so the check itself has no side
/// effects during idle: divergence (user input is flowing per
/// GetLastInputInfo while the hook's callback stays silent) is free to
/// observe; only a divergent target gets a single tagged canary injection as
/// confirmation, and only a failed echo triggers re-registration. A target
/// whose canary just echoed is left alone for a success cooldown: input
/// flowing while a keyboard hook stays silent is the normal state of a
/// mouse-only session, and re-probing it every cycle would feed synthetic
/// keystrokes into the focused app once per silence window.
/// RegisterHotKey chords have no hook to probe, so they are kept alive by a
/// separate heartbeat: a periodic unregister+register round-trip with
/// exponential backoff on failure to prevent registration storms.
/// </summary>
internal sealed class HookHealthWatchdog : IDisposable
{
    private readonly HookWatchdogPolicy _policy;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Action<string> _log;
    private readonly List<WatchSlot> _slots = new();
    private readonly List<MaintenanceSlot> _maintenanceSlots = new();
    private readonly object _slotsLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private bool _disposed;

    private sealed class WatchSlot
    {
        internal required Func<IHookHealthProbeTarget?> Resolve { get; init; }
        internal bool HasRecovered;
        internal long LastRecoveryTicks;
        internal long LastProbeSuccessTicks;
        internal int ConsecutiveRecoveryFailures;
    }

    private sealed class MaintenanceSlot
    {
        internal required Func<IHookRegistrationMaintenanceTarget?> Resolve { get; init; }
        internal long LastAttemptTicks;
        internal bool HasAttempted;
        internal int ConsecutiveFailures;
        internal bool AttemptInFlight;
    }

    internal HookHealthWatchdog(
        DispatcherQueue? dispatcherQueue,
        Action<string>? log = null,
        HookWatchdogPolicy? policy = null)
    {
        _policy = policy ?? HookWatchdogPolicy.Default;
        _dispatcherQueue = dispatcherQueue;
        _log = log ?? (_ => { });
        _loop = Task.Run(LoopAsync);
    }

    internal void Watch(Func<IHookHealthProbeTarget?> resolve)
    {
        lock (_slotsLock)
        {
            _slots.Add(new WatchSlot { Resolve = resolve });
        }
    }

    internal void WatchMaintenance(Func<IHookRegistrationMaintenanceTarget?> resolve)
    {
        lock (_slotsLock)
        {
            _maintenanceSlots.Add(new MaintenanceSlot { Resolve = resolve });
        }
    }

    /// <summary>
    /// Input is flowing (per GetLastInputInfo) but the hook callback has been
    /// silent long enough that real input must have crossed it. The tick
    /// counts are 32/64-bit GetTickCount-domain values; the subtraction is
    /// intentionally unchecked so a 49.7-day wraparound stays correct.
    /// </summary>
    internal static bool IsDivergent(
        uint lastInputTick,
        long lastCallbackTicks,
        long nowTicks,
        uint recentInputWindowMs,
        uint callbackSilentThresholdMs)
    {
        uint inputAgeMs = unchecked((uint)nowTicks - lastInputTick);
        long callbackSilentMs = nowTicks - lastCallbackTicks;
        return inputAgeMs <= recentInputWindowMs &&
               callbackSilentMs >= callbackSilentThresholdMs;
    }

    internal Task RunCycleOnceAsync()
    {
        bool inputKnown = Win32Helper.TryGetLastInputTickCount(out uint lastInputTick);
        return RunCycleOnceAsync(inputKnown, lastInputTick, Environment.TickCount64);
    }

    /// <summary>
    /// One watchdog pass with an externally supplied clock/input snapshot so
    /// the decision logic can be exercised without real user input.
    /// </summary>
    internal async Task RunCycleOnceAsync(bool inputKnown, uint lastInputTick, long nowTicks)
    {
        List<WatchSlot> slots;
        List<MaintenanceSlot> maintenanceSlots;
        lock (_slotsLock)
        {
            slots = new List<WatchSlot>(_slots);
            maintenanceSlots = new List<MaintenanceSlot>(_maintenanceSlots);
        }

        foreach (WatchSlot slot in slots)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            IHookHealthProbeTarget? target;
            try
            {
                target = slot.Resolve();
            }
            catch (Exception ex)
            {
                _log($"[HookWatchdog] Target resolution failed: {ex.Message}");
                continue;
            }

            if (target is null)
            {
                continue;
            }

            try
            {
                await CheckTargetAsync(slot, target, inputKnown, lastInputTick, nowTicks)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[HookWatchdog] Probe of {target.ProbeName} failed: {ex.Message}");
            }
        }

        RunMaintenanceSlots(maintenanceSlots, nowTicks);
    }

    private async Task CheckTargetAsync(
        WatchSlot slot,
        IHookHealthProbeTarget target,
        bool inputKnown,
        uint lastInputTick,
        long nowTicks)
    {
        if (!target.HookProbeWanted)
        {
            return;
        }

        // While the process is still starved a freshly re-registered hook just
        // accumulates new timeouts; bound the churn with a per-slot cooldown
        // that escalates only when recoveries keep dying within the rapid
        // re-death window (starved process) rather than an aggressive
        // environment (feedback 445: removals every 15-45 minutes).
        if (slot.HasRecovered &&
            nowTicks - slot.LastRecoveryTicks < CurrentRecoveryCooldownMs(slot))
        {
            return;
        }

        bool dead;
        if (target.HookConfirmedDead)
        {
            dead = true;
        }
        else if (
            !inputKnown ||
            !IsDivergent(
                lastInputTick,
                target.LastHookCallbackTicks,
                nowTicks,
                _policy.RecentInputWindowMs,
                _policy.CallbackSilentThresholdMs))
        {
            return;
        }
        else if (
            slot.LastProbeSuccessTicks != 0 &&
            nowTicks - slot.LastProbeSuccessTicks < _policy.ProbeSuccessCooldownMs)
        {
            return;
        }
        else
        {
            // Canary side effects (one synthetic keystroke/mouse nudge) are
            // user-visible in principle — always log them so field logs show
            // the probe cadence without flipping verbose flags.
            _log($"[HookWatchdog] {target.ProbeName} silent while input flows; issuing tagged canary");
            dead = !await target.ProbeHookAliveAsync(_policy.CanaryEchoWaitMs).ConfigureAwait(false);
        }

        if (!dead)
        {
            slot.LastProbeSuccessTicks = nowTicks;
            return;
        }

        bool rapidRedeath = slot.HasRecovered &&
            nowTicks - slot.LastRecoveryTicks < _policy.RapidRedeathWindowMs;
        slot.ConsecutiveRecoveryFailures = rapidRedeath
            ? slot.ConsecutiveRecoveryFailures + 1
            : 0;

        slot.HasRecovered = true;
        slot.LastRecoveryTicks = nowTicks;
        if (slot.ConsecutiveRecoveryFailures > 0)
        {
            _log(
                $"[HookWatchdog] {target.ProbeName} hook unresponsive; re-registering " +
                $"(died within {_policy.RapidRedeathWindowMs / 1000}s of recovery; " +
                $"cooldown escalated to {CurrentRecoveryCooldownMs(slot) / 1000}s)");
        }
        else
        {
            _log($"[HookWatchdog] {target.ProbeName} hook unresponsive; re-registering");
        }

        if (_disposed)
        {
            return;
        }

        // Without a dispatcher (unit tests, or a degenerate shutdown window)
        // run the recovery inline on the watchdog thread.
        if (_dispatcherQueue is null)
        {
            InvokeRecovery(target);
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                InvokeRecovery(target);
            }
        }))
        {
            _log(
                "[HookWatchdog] Recovery enqueue for " +
                $"{target.ProbeName} failed; dispatcher is gone");
        }
    }

    private void RunMaintenanceSlots(List<MaintenanceSlot> slots, long nowTicks)
    {
        foreach (MaintenanceSlot slot in slots)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            IHookRegistrationMaintenanceTarget? target;
            try
            {
                target = slot.Resolve();
            }
            catch (Exception ex)
            {
                _log($"[HookWatchdog] Maintenance target resolution failed: {ex.Message}");
                continue;
            }

            if (target is null)
            {
                continue;
            }

            try
            {
                CheckMaintenanceTarget(slot, target, nowTicks);
            }
            catch (Exception ex)
            {
                _log($"[HookWatchdog] Maintenance of {target.MaintenanceName} failed: {ex.Message}");
            }
        }
    }

    private void CheckMaintenanceTarget(
        MaintenanceSlot slot,
        IHookRegistrationMaintenanceTarget target,
        long nowTicks)
    {
        if (!target.RegistrationMaintenanceWanted)
        {
            // Suspended (recording) or disabled: there is no pending heartbeat
            // whose outcome to evaluate, but past failures stay remembered.
            slot.AttemptInFlight = false;
            return;
        }

        if (slot.AttemptInFlight)
        {
            // The round-trip runs asynchronously on the UI dispatcher; judge
            // it by the target's health only after the grace window.
            if (nowTicks - slot.LastAttemptTicks < _policy.MaintenanceEvaluationGraceMs)
            {
                return;
            }

            slot.AttemptInFlight = false;
            if (target.RegistrationHealthy)
            {
                if (slot.ConsecutiveFailures > 0)
                {
                    _log(
                        $"[HookWatchdog] {target.MaintenanceName} heartbeat restored " +
                        "registration; resetting backoff");
                }

                slot.ConsecutiveFailures = 0;
            }
            else
            {
                slot.ConsecutiveFailures++;
                _log(
                    $"[HookWatchdog] {target.MaintenanceName} heartbeat left registration " +
                    $"unhealthy; backing off (failures={slot.ConsecutiveFailures}, next in " +
                    $"{CurrentMaintenanceIntervalMs(slot.ConsecutiveFailures) / 1000}s)");
            }

            return;
        }

        if (slot.HasAttempted &&
            nowTicks - slot.LastAttemptTicks < CurrentMaintenanceIntervalMs(slot.ConsecutiveFailures))
        {
            return;
        }

        slot.HasAttempted = true;
        slot.LastAttemptTicks = nowTicks;
        slot.AttemptInFlight = true;
        _log($"[HookWatchdog] {target.MaintenanceName} issuing registration heartbeat");
        if (_disposed)
        {
            return;
        }

        // Without a dispatcher (unit tests, or a degenerate shutdown window)
        // run the heartbeat inline on the watchdog thread.
        if (_dispatcherQueue is null)
        {
            InvokeHeartbeat(target);
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                InvokeHeartbeat(target);
            }
        }))
        {
            slot.AttemptInFlight = false;
            _log(
                "[HookWatchdog] Heartbeat enqueue for " +
                $"{target.MaintenanceName} failed; dispatcher is gone");
        }
    }

    private long CurrentRecoveryCooldownMs(WatchSlot slot)
    {
        long cooldown = _policy.RecoveryCooldownBaseMs;
        for (int i = 0;
             i < slot.ConsecutiveRecoveryFailures && cooldown < _policy.RecoveryCooldownMaxMs;
             i++)
        {
            cooldown = Math.Min(cooldown * 2, _policy.RecoveryCooldownMaxMs);
        }

        return cooldown;
    }

    private long CurrentMaintenanceIntervalMs(int consecutiveFailures)
    {
        long interval = _policy.MaintenanceIntervalMs;
        for (int i = 0;
             i < consecutiveFailures && interval < _policy.MaintenanceMaxIntervalMs;
             i++)
        {
            interval = Math.Min(interval * 2, _policy.MaintenanceMaxIntervalMs);
        }

        return interval;
    }

    private void InvokeRecovery(IHookHealthProbeTarget target)
    {
        try
        {
            target.RecoverHook();
        }
        catch (Exception ex)
        {
            _log($"[HookWatchdog] Recovery of {target.ProbeName} failed: {ex.Message}");
        }
    }

    private void InvokeHeartbeat(IHookRegistrationMaintenanceTarget target)
    {
        try
        {
            target.RunRegistrationHeartbeat();
        }
        catch (Exception ex)
        {
            _log($"[HookWatchdog] Heartbeat of {target.MaintenanceName} failed: {ex.Message}");
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_policy.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await RunCycleOnceAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log($"[HookWatchdog] Cycle failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Cancel only — the loop may still be inside WaitForNextTickAsync and
        // touching the token; the CTS carries no OS resource to release early.
        _cts.Cancel();
    }
}
