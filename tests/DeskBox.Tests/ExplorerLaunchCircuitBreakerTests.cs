using DeskBox.Helpers;

namespace DeskBox.Tests;

/// <summary>
/// The Explorer-hosted launch breaker (#455): RPC-class failures must stop
/// the cross-process COM storm against a wedged shell until Explorer
/// restarts, and nothing else — user cancellations, plain errors, or the
/// direct AOT smoke calls — may trip it or be gated by it.
/// </summary>
public sealed class ExplorerLaunchCircuitBreakerTests
{
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcCallFailed = unchecked((int)0x800706BE);
    private const int Win32Cancelled = unchecked((int)0x8007007A);
    private const int UnexpectedFailure = unchecked((int)0x8000FFFF);
    private const int AccessDenied = unchecked((int)0x80070005);

    // The state's default open cooldown (five minutes), in the injected
    // clock's millisecond unit.
    private const long DefaultOpenCooldownMs = 300_000;

    [Fact]
    public void RpcClassFailureCodes_AreTheOnlyTripSignatures()
    {
        Assert.True(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(RpcServerUnavailable));
        Assert.True(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(RpcCallFailed));
        Assert.True(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(unchecked((int)0x800706A4)));
        Assert.True(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(unchecked((int)0x80070707)));

        Assert.False(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(Win32Cancelled));
        Assert.False(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(UnexpectedFailure));
        Assert.False(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(AccessDenied));
        Assert.False(ExplorerLaunchCircuitBreaker.IsRpcClassFailure(0));
    }

    [Fact]
    public void SingleRpcFailure_OpensTheBreakerImmediately()
    {
        ExplorerLaunchCircuitBreaker.Reset();

        ExplorerLaunchCircuitBreaker.RecordFailure(RpcCallFailed);

        Assert.True(ExplorerLaunchCircuitBreaker.ShouldBypassExplorerHostedLaunch());
    }

    [Fact]
    public void CancelledAndNonRpcFailures_DoNotOpenTheBreaker()
    {
        ExplorerLaunchCircuitBreaker.Reset();

        ExplorerLaunchCircuitBreaker.RecordFailure(Win32Cancelled);
        ExplorerLaunchCircuitBreaker.RecordFailure(UnexpectedFailure);
        ExplorerLaunchCircuitBreaker.RecordFailure(AccessDenied);

        Assert.False(ExplorerLaunchCircuitBreaker.ShouldBypassExplorerHostedLaunch());
    }

    [Fact]
    public void RecordSuccess_ClearsAccumulatedRpcFailures()
    {
        var state = new ExplorerLaunchCircuitBreakerState(2, () => 4242);

        state.RecordFailure(RpcCallFailed);
        Assert.False(state.ShouldBypass());

        state.RecordSuccess();
        state.RecordFailure(RpcCallFailed);
        Assert.False(state.ShouldBypass());

        state.RecordFailure(RpcCallFailed);
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void ShellRestart_ClosesTheBreakerAndItCanReopen()
    {
        uint shellProcessId = 4242;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => shellProcessId);

        state.RecordFailure(RpcCallFailed);
        Assert.True(state.ShouldBypass());

        shellProcessId = 9090;
        Assert.False(state.ShouldBypass());

        state.RecordFailure(RpcCallFailed);
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void MissingShellWindow_KeepsTheBreakerOpen()
    {
        var state = new ExplorerLaunchCircuitBreakerState(1, () => 0);

        state.RecordFailure(RpcCallFailed);
        state.RecordFailure(RpcCallFailed);

        Assert.True(state.ShouldBypass());
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void ConcurrentRecording_RemainsConsistent()
    {
        var state = new ExplorerLaunchCircuitBreakerState(64, () => 4242);

        Parallel.For(
            0,
            2_000,
            _ =>
            {
                state.RecordFailure(RpcCallFailed);
                state.RecordSuccess();
                state.ShouldBypass();
            });

        state.RecordSuccess();
        state.RecordFailure(RpcCallFailed);
        Assert.False(state.ShouldBypass());
    }

    [Fact]
    public void OpenFileOrChooseApp_ConsultsTheBreakerBeforeTheExplorerLaunch()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Platform/Win32Helper.cs"));
        string method = Slice(
            source,
            "public static bool OpenFileOrChooseApp",
            "internal static string ResolveShellLaunchDirectory");

        int bypass = method.IndexOf(
            "ExplorerLaunchCircuitBreaker.ShouldBypassExplorerHostedLaunch()",
            StringComparison.Ordinal);
        int explorerLaunch = method.IndexOf(
            "ExplorerShellLaunchService.TryOpen",
            StringComparison.Ordinal);

        Assert.True(bypass >= 0, "Missing breaker consult.");
        Assert.True(
            explorerLaunch > bypass,
            "The breaker must be consulted before the Explorer-hosted launch.");
        Assert.Contains(
            "ExplorerLaunchCircuitBreaker.RecordFailure(explorerLaunchHResult)",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExplorerLaunchCircuitBreaker.RecordSuccess()",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "[OpenFile] Explorer-hosted launch bypassed (circuit open, explorer restarted or RPC failures)",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AotShellSmoke_DirectExplorerLaunch_IsNotGatedByTheBreaker()
    {
        string smoke = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/App.AotShellSmoke.cs"));

        Assert.Contains("ExplorerShellLaunchService.TryOpen(", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("ExplorerLaunchCircuitBreaker", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenBreaker_KeepsBypassingWhileTheCooldownHasNotElapsed()
    {
        long now = 1_000_000;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => 4242, () => now);

        state.RecordFailure(RpcCallFailed);

        Assert.True(state.ShouldBypass());
        now += 60_000;
        Assert.True(state.ShouldBypass());
        now += DefaultOpenCooldownMs - 60_000 - 1;
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void ElapsedCooldown_GrantsExactlyOneHalfOpenProbe()
    {
        long now = 1_000_000;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => 4242, () => now);

        state.RecordFailure(RpcCallFailed);
        now += DefaultOpenCooldownMs - 1;
        Assert.True(state.ShouldBypass());

        now += 1;
        Assert.False(state.ShouldBypass());
        Assert.True(state.ShouldBypass());
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void HalfOpenProbe_RpcFailure_RestartsTheCooldown()
    {
        long now = 1_000_000;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => 4242, () => now);

        state.RecordFailure(RpcCallFailed);
        now += DefaultOpenCooldownMs;
        Assert.False(state.ShouldBypass());

        state.RecordFailure(RpcCallFailed);

        now += DefaultOpenCooldownMs - 1;
        Assert.True(state.ShouldBypass());
        now += 1;
        Assert.False(state.ShouldBypass());
    }

    [Fact]
    public void HalfOpenProbe_Success_FullyClosesTheBreaker()
    {
        long now = 1_000_000;
        uint shellProcessId = 4242;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => shellProcessId, () => now);

        state.RecordFailure(RpcCallFailed);
        now += DefaultOpenCooldownMs;
        Assert.False(state.ShouldBypass());

        state.RecordSuccess();

        // Closed again, not merely probing: no further bypass however far
        // the clock advances, and the failure count starts from zero.
        now += 10_000_000;
        Assert.False(state.ShouldBypass());
        state.RecordFailure(RpcCallFailed);
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void ShellRestartDuringCooldown_ClosesTheBreakerImmediately()
    {
        long now = 1_000_000;
        uint shellProcessId = 4242;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => shellProcessId, () => now);

        state.RecordFailure(RpcCallFailed);
        now += 1;
        shellProcessId = 9090;
        Assert.False(state.ShouldBypass());

        // Fully closed: a fresh RPC failure opens a brand-new cooldown
        // window even though the clock has barely moved.
        state.RecordFailure(RpcCallFailed);
        Assert.True(state.ShouldBypass());
    }

    [Fact]
    public void HalfOpenProbe_NonRpcFailure_ConsumesProbeAndRestartsCooldown()
    {
        long now = 1_000_000;
        var state = new ExplorerLaunchCircuitBreakerState(1, () => 4242, () => now);

        state.RecordFailure(RpcCallFailed);
        now += DefaultOpenCooldownMs;
        Assert.False(state.ShouldBypass());

        state.RecordFailure(AccessDenied);

        Assert.True(state.ShouldBypass());
        now += DefaultOpenCooldownMs - 1;
        Assert.True(state.ShouldBypass());
        now += 1;
        Assert.False(state.ShouldBypass());
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }
}
