using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class HookHealthWatchdogTests
{
    private static HookWatchdogPolicy Policy => HookWatchdogPolicy.Default;

    private sealed class FakeHookTarget : IHookHealthProbeTarget
    {
        public bool Wanted { get; set; }
        public bool ConfirmedDead { get; set; }
        public long CallbackTicks { get; set; }
        public bool ProbeResult { get; set; } = true;
        public int ProbeCalls;
        public int RecoverCalls;

        public string ProbeName => "fake";
        public bool HookProbeWanted => Wanted;
        public bool HookConfirmedDead => ConfirmedDead;
        public long LastHookCallbackTicks => CallbackTicks;

        public Task<bool> ProbeHookAliveAsync(int echoWaitMilliseconds)
        {
            ProbeCalls++;
            return Task.FromResult(ProbeResult);
        }

        public void RecoverHook() => RecoverCalls++;
    }

    private sealed class FakeMaintenanceTarget : IHookRegistrationMaintenanceTarget
    {
        public bool Wanted { get; set; } = true;
        public bool Healthy { get; set; } = true;
        public int HeartbeatCalls;

        public string MaintenanceName => "fake-chord";
        public bool RegistrationMaintenanceWanted => Wanted;
        public bool RegistrationHealthy => Healthy;

        public void RunRegistrationHeartbeat() => HeartbeatCalls++;
    }

    private static HookHealthWatchdog CreateWatchdog(FakeHookTarget target)
    {
        // A null dispatcher makes the watchdog invoke recovery inline, which
        // keeps the decision logic synchronous and deterministic in tests.
        var watchdog = new HookHealthWatchdog(dispatcherQueue: null);
        watchdog.Watch(() => target);
        return watchdog;
    }

    private static HookHealthWatchdog CreateMaintenanceWatchdog(
        FakeMaintenanceTarget target,
        HookWatchdogPolicy? policy = null)
    {
        var watchdog = new HookHealthWatchdog(
            dispatcherQueue: null,
            log: _ => { },
            policy: policy);
        watchdog.WatchMaintenance(() => target);
        return watchdog;
    }

    private static Task RunCycleAsync(HookHealthWatchdog watchdog, long nowTicks)
    {
        return watchdog.RunCycleOnceAsync(
            inputKnown: true,
            lastInputTick: unchecked((uint)(nowTicks - 5_000)),
            nowTicks: nowTicks);
    }

    [Fact]
    public void PolicyDefaults_EncodeTheFeedback445Fix()
    {
        // Field data (feedback 445/450/502, 1.5.5): 60s silence + 15s tick +
        // flat 5-minute recovery cooldown produced minute-plus dead windows.
        // The defaults compress active-use detection to ~25s and rescue a
        // re-dead hook after 90s, escalating only on rapid re-death.
        Assert.Equal(TimeSpan.FromSeconds(5), Policy.TickInterval);
        Assert.Equal(10_000u, Policy.RecentInputWindowMs);
        Assert.Equal(20_000u, Policy.CallbackSilentThresholdMs);
        Assert.Equal(1_200, Policy.CanaryEchoWaitMs);
        Assert.Equal(180_000, Policy.ProbeSuccessCooldownMs);
        Assert.Equal(90_000, Policy.RecoveryCooldownBaseMs);
        Assert.Equal(480_000, Policy.RecoveryCooldownMaxMs);
        Assert.Equal(120_000, Policy.RapidRedeathWindowMs);
        Assert.Equal(90_000, Policy.MaintenanceIntervalMs);
        Assert.Equal(480_000, Policy.MaintenanceMaxIntervalMs);
        Assert.Equal(30_000, Policy.MaintenanceEvaluationGraceMs);
    }

    [Fact]
    public async Task TargetNotWanted_SkipsProbeAndRecovery()
    {
        var target = new FakeHookTarget { Wanted = false, ConfirmedDead = true };
        using var watchdog = CreateWatchdog(target);

        await watchdog.RunCycleOnceAsync(
            inputKnown: true, lastInputTick: 0, nowTicks: 0);

        Assert.Equal(0, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public async Task ConfirmedDeadTarget_RecoversWithoutCanary()
    {
        var target = new FakeHookTarget { Wanted = true, ConfirmedDead = true };
        using var watchdog = CreateWatchdog(target);

        await watchdog.RunCycleOnceAsync(
            inputKnown: true, lastInputTick: 0, nowTicks: 0);

        Assert.Equal(0, target.ProbeCalls);
        Assert.Equal(1, target.RecoverCalls);
    }

    [Fact]
    public async Task DivergentTargetWithFailedCanary_Recovers()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = false,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);

        Assert.Equal(1, target.ProbeCalls);
        Assert.Equal(1, target.RecoverCalls);
    }

    [Fact]
    public async Task DivergentTargetWithEchoedCanary_StaysRegistered()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = true,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);

        Assert.Equal(1, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public async Task SuccessfulCanary_LeavesTargetAloneUntilCooldownElapses()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = true,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);
        Assert.Equal(1, target.ProbeCalls);

        // Still divergent (input flowing, callback silent) — this is the
        // mouse-only-session steady state, and a just-echoed canary bought
        // quiet time, so the target is not re-probed every cycle.
        long midCycle = now + 90_000;
        await RunCycleAsync(watchdog, midCycle);
        Assert.Equal(1, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);

        long afterCooldown = now + Policy.ProbeSuccessCooldownMs + 1;
        await RunCycleAsync(watchdog, afterCooldown);
        Assert.Equal(2, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public async Task SilentCallbackWithoutRecentInput_SkipsCanary()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
        };
        using var watchdog = CreateWatchdog(target);

        // Input last seen outside the recent window — nobody is typing, so a
        // quiet hook is expected rather than suspicious.
        await watchdog.RunCycleOnceAsync(
            inputKnown: true,
            lastInputTick: unchecked((uint)(now - Policy.RecentInputWindowMs - 1)),
            nowTicks: now);

        Assert.Equal(0, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public async Task RecentlyActiveCallback_SkipsCanary()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs / 2,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);

        Assert.Equal(0, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public async Task RecoveryCooldown_PreventsRepeatedRecovery()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = false,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);
        long midCycle = now + 60_000;
        target.CallbackTicks = midCycle - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, midCycle);

        Assert.Equal(1, target.ProbeCalls);
        Assert.Equal(1, target.RecoverCalls);

        // Once the cooldown elapses a still-dead hook is retried.
        long lateCycle = now + Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = lateCycle - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, lateCycle);

        Assert.Equal(2, target.ProbeCalls);
        Assert.Equal(2, target.RecoverCalls);
    }

    [Fact]
    public async Task RapidRedeath_EscalatesRecoveryCooldownThenResets()
    {
        // A hook that dies again within the rapid re-death window (starved
        // process) escalates the cooldown; a death outside the window
        // (aggressive environment, feedback 445's 15-45 minute cadence) does
        // not and keeps the 90-second rescue.
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = false,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);
        Assert.Equal(1, target.RecoverCalls);

        // Death 90s after recovery: inside the 120s rapid window, but past
        // the base cooldown — recover and escalate.
        long secondDeath = now + Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = secondDeath - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, secondDeath);
        Assert.Equal(2, target.RecoverCalls);

        // Escalated cooldown (2x base) blocks the next attempt at +90s.
        long blocked = secondDeath + Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = blocked - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, blocked);
        Assert.Equal(2, target.RecoverCalls);

        // ...and allows it once the escalated window elapses. That death is
        // outside the rapid window, so the backoff resets to base.
        long thirdDeath = secondDeath + 2 * Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = thirdDeath - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, thirdDeath);
        Assert.Equal(3, target.RecoverCalls);

        long fourthDeath = thirdDeath + Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = fourthDeath - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, fourthDeath);
        Assert.Equal(4, target.RecoverCalls);
    }

    [Fact]
    public async Task SlowRedeath_NeverEscalatesRecoveryCooldown()
    {
        // Feedback 445's environment kills the hook every 15-45 minutes;
        // those deaths fall outside the rapid re-death window and must keep
        // the base cooldown (the old flat 5-minute cooldown was the bug).
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = false,
        };
        using var watchdog = CreateWatchdog(target);

        await RunCycleAsync(watchdog, now);
        Assert.Equal(1, target.RecoverCalls);

        long secondDeath = now + Policy.RapidRedeathWindowMs + 1;
        target.CallbackTicks = secondDeath - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, secondDeath);
        Assert.Equal(2, target.RecoverCalls);

        // Still on the base cooldown, so the next death is rescued after the
        // base interval, not an escalated one.
        long thirdDeath = secondDeath + Policy.RecoveryCooldownBaseMs + 1;
        target.CallbackTicks = thirdDeath - Policy.CallbackSilentThresholdMs - 1;
        await RunCycleAsync(watchdog, thirdDeath);
        Assert.Equal(3, target.RecoverCalls);
    }

    [Fact]
    public async Task UnknownInputTick_SkipsDivergenceProbing()
    {
        long now = 1_000_000;
        var target = new FakeHookTarget
        {
            Wanted = true,
            CallbackTicks = now - Policy.CallbackSilentThresholdMs - 1,
            ProbeResult = false,
        };
        using var watchdog = CreateWatchdog(target);

        await watchdog.RunCycleOnceAsync(
            inputKnown: false, lastInputTick: 0, nowTicks: now);

        Assert.Equal(0, target.ProbeCalls);
        Assert.Equal(0, target.RecoverCalls);
    }

    [Fact]
    public void IsDivergent_HandlesTickCountWraparound()
    {
        // The 32-bit GetTickCount domain wrapped: nowTicks sits just past
        // 2^32 while the last input tick landed 5s before the wrap.
        long now = (long)uint.MaxValue + 101;
        uint lastInputTick = uint.MaxValue - 4_999;

        bool divergent = HookHealthWatchdog.IsDivergent(
            lastInputTick,
            lastCallbackTicks: now - Policy.CallbackSilentThresholdMs - 1,
            nowTicks: now,
            recentInputWindowMs: Policy.RecentInputWindowMs,
            callbackSilentThresholdMs: Policy.CallbackSilentThresholdMs);

        Assert.True(divergent);
    }

    [Fact]
    public void IsDivergent_NeverFiredCallbackCountsAsSilent()
    {
        bool divergent = HookHealthWatchdog.IsDivergent(
            lastInputTick: 999_999,
            lastCallbackTicks: 0,
            nowTicks: 1_000_000,
            recentInputWindowMs: Policy.RecentInputWindowMs,
            callbackSilentThresholdMs: Policy.CallbackSilentThresholdMs);

        Assert.True(divergent);
    }

    [Fact]
    public async Task ChordHeartbeat_IssuesAtIntervalWhileWanted()
    {
        var target = new FakeMaintenanceTarget();
        var policy = new HookWatchdogPolicy
        {
            MaintenanceIntervalMs = 1_000,
            MaintenanceMaxIntervalMs = 4_000,
            MaintenanceEvaluationGraceMs = 500,
        };
        using var watchdog = CreateMaintenanceWatchdog(target, policy);

        await watchdog.RunCycleOnceAsync(true, 0, 1_000);
        Assert.Equal(1, target.HeartbeatCalls);

        // In flight: judged after the grace window, not re-issued.
        await watchdog.RunCycleOnceAsync(true, 0, 1_400);
        Assert.Equal(1, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 1_501);
        Assert.Equal(1, target.HeartbeatCalls);

        // Interval elapsed since the attempt: heartbeat re-issued.
        await watchdog.RunCycleOnceAsync(true, 0, 2_001);
        Assert.Equal(2, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 2_502);
        await watchdog.RunCycleOnceAsync(true, 0, 3_002);
        Assert.Equal(3, target.HeartbeatCalls);
    }

    [Fact]
    public async Task ChordHeartbeat_FailureBacksOffAndResetsOnSuccess()
    {
        var target = new FakeMaintenanceTarget { Healthy = false };
        var policy = new HookWatchdogPolicy
        {
            MaintenanceIntervalMs = 1_000,
            MaintenanceMaxIntervalMs = 4_000,
            MaintenanceEvaluationGraceMs = 500,
        };
        using var watchdog = CreateMaintenanceWatchdog(target, policy);

        await watchdog.RunCycleOnceAsync(true, 0, 1_000);
        Assert.Equal(1, target.HeartbeatCalls);

        // Evaluation marks the failure; the next attempt waits 2x base.
        await watchdog.RunCycleOnceAsync(true, 0, 1_501);
        await watchdog.RunCycleOnceAsync(true, 0, 2_999);
        Assert.Equal(1, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 3_001);
        Assert.Equal(2, target.HeartbeatCalls);

        // Second failure escalates to the 4s cap.
        await watchdog.RunCycleOnceAsync(true, 0, 3_502);
        await watchdog.RunCycleOnceAsync(true, 0, 6_999);
        Assert.Equal(2, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 7_002);
        Assert.Equal(3, target.HeartbeatCalls);

        // A healthy evaluation resets the backoff to the base interval.
        target.Healthy = true;
        await watchdog.RunCycleOnceAsync(true, 0, 7_503);
        await watchdog.RunCycleOnceAsync(true, 0, 8_001);
        Assert.Equal(3, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 8_002);
        Assert.Equal(4, target.HeartbeatCalls);
    }

    [Fact]
    public async Task ChordHeartbeat_SkippedWhileNotWantedAndReissuesAfter()
    {
        var target = new FakeMaintenanceTarget();
        var policy = new HookWatchdogPolicy
        {
            MaintenanceIntervalMs = 1_000,
            MaintenanceMaxIntervalMs = 4_000,
            MaintenanceEvaluationGraceMs = 500,
        };
        using var watchdog = CreateMaintenanceWatchdog(target, policy);

        // Suspended (recording) targets never get a heartbeat.
        target.Wanted = false;
        await watchdog.RunCycleOnceAsync(true, 0, 1_000);
        await watchdog.RunCycleOnceAsync(true, 0, 2_000);
        Assert.Equal(0, target.HeartbeatCalls);

        // An attempt issued right before suspension is not evaluated either.
        target.Wanted = true;
        await watchdog.RunCycleOnceAsync(true, 0, 3_000);
        Assert.Equal(1, target.HeartbeatCalls);
        target.Wanted = false;
        target.Healthy = false;
        await watchdog.RunCycleOnceAsync(true, 0, 3_501);
        await watchdog.RunCycleOnceAsync(true, 0, 4_000);
        Assert.Equal(1, target.HeartbeatCalls);

        // Once wanted again the cadence resumes from the last attempt.
        target.Wanted = true;
        await watchdog.RunCycleOnceAsync(true, 0, 3_999);
        Assert.Equal(1, target.HeartbeatCalls);
        await watchdog.RunCycleOnceAsync(true, 0, 4_001);
        Assert.Equal(2, target.HeartbeatCalls);
    }
}
