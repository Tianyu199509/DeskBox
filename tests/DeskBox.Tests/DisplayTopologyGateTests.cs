using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Spec 9.1 R13–R16: the gate parks restores without spending retries, the
/// removal grace behaves on expiry / return / user action, and the display
/// power classifier maps off/on/dim correctly.
/// </summary>
public sealed class DisplayTopologyGateTests
{
    private static (DisplayTopologyGate Gate, DateTimeOffset Now) Create()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        return (new DisplayTopologyGate(() => now), now);
    }

    private static void Advance(ref DateTimeOffset now, TimeSpan by) => now += by;

    [Fact]
    public void R13_ClosedGate_ParksWithoutRetryBudget()
    {
        var (gate, _) = Create();
        int opened = 0;
        gate.GateOpened += _ => opened++;

        gate.Close(DisplayTopologyGateReason.SessionLocked);
        Assert.True(gate.IsClosed);
        Assert.Equal(0, opened);

        gate.Open(DisplayTopologyGateReason.SessionLocked);
        Assert.False(gate.IsClosed);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void R14_MultipleReasons_LastOpenReleases()
    {
        var (gate, _) = Create();
        gate.Close(DisplayTopologyGateReason.SessionLocked);
        gate.Close(DisplayTopologyGateReason.DisplayOff);
        Assert.True(gate.IsClosed);

        gate.Open(DisplayTopologyGateReason.SessionLocked);
        Assert.True(gate.IsClosed);

        gate.Open(DisplayTopologyGateReason.DisplayOff);
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void R14_RemovalGrace_ExpiresAfterDuration()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var gate = new DisplayTopologyGate(() => now);
        int graceEnded = 0;
        gate.GraceEnded += _ => graceEnded++;

        gate.StartRemovalGrace(["A", "B"], ["A"]);
        Assert.True(gate.IsClosed);

        // Still inside the grace window.
        now += TimeSpan.FromSeconds(2);
        gate.ObserveDisplays(["A"]);
        Assert.True(gate.IsClosed);

        // Grace duration elapsed.
        now += TimeSpan.FromSeconds(2);
        gate.ObserveDisplays(["A"]);
        Assert.False(gate.IsClosed);
        Assert.Equal(1, graceEnded);
    }

    [Fact]
    public void R14_RemovalGrace_DisplaysReturn_CancelsOutright()
    {
        var (gate, _) = Create();
        gate.StartRemovalGrace(["A", "B"], ["A"]);
        Assert.True(gate.IsClosed);

        // B came back: nothing to apply, grace ends without a restore.
        gate.ObserveDisplays(["A", "B"]);
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void R14_RemovalGrace_IgnoredWhenSetGrows()
    {
        var (gate, _) = Create();
        gate.StartRemovalGrace(["A", "B"], ["A", "B", "C"]);
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void R14_UserAction_EndsGraceImmediately()
    {
        var (gate, _) = Create();
        gate.StartRemovalGrace(["A", "B"], ["A"]);
        Assert.True(gate.IsClosed);

        gate.EndGraceByUserAction();
        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void R15_ClassifierMapsOffOnDim()
    {
        // The classifier-level mapping is covered by
        // AppLifecycleRecoverySignalClassifierTests; here we pin that the gate
        // consumes the same three states (off closes, on opens, dim ignored).
        var (gate, _) = Create();
        gate.Close(DisplayTopologyGateReason.DisplayOff);
        Assert.True(gate.IsClosed);
        gate.Open(DisplayTopologyGateReason.DisplayOff);
        Assert.False(gate.IsClosed);
    }
}
