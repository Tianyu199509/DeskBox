using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopLayerBeddingPolicyTests
{
    [Fact]
    public void IsBeddedAtDesktopLayer_AcceptsDeskBoxAndShellTail()
    {
        Assert.True(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]));
    }

    [Fact]
    public void IsBeddedAtDesktopLayer_AcceptsEmptyWalk()
    {
        // Walking below the widget hit the band bottom immediately.
        Assert.True(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(
            Array.Empty<DesktopLayerBeddingWindowKind>()));
    }

    [Fact]
    public void IsBeddedAtDesktopLayer_IgnoresInvisibleMinimizedCloakedWindows()
    {
        // Minimized (iconic) windows rest at the band bottom by design, and
        // cloaked/suspended or hidden windows occupy Z slots without any
        // visual meaning — none of them witness a leak.
        Assert.True(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(
        [
            DesktopLayerBeddingWindowKind.IgnoredWindow,
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.IgnoredWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]));
    }

    [Fact]
    public void IsBeddedAtDesktopLayer_RejectsForeignAppWindowBelow()
    {
        // 289/447/468 leak state: a visible foreign application window sits
        // between the widget and the shell desktop host.
        Assert.False(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]));
    }

    [Fact]
    public void IsBeddedAtDesktopLayer_RejectsForeignWindowEvenAtWalkEnd()
    {
        Assert.False(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(
        [
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
        ]));
    }

    [Fact]
    public void FindLeakWitness_ReturnsFirstNonDeskBoxNonShellObservation()
    {
        DesktopLayerBeddingWindowKind? witness = DesktopLayerBeddingPolicy.FindLeakWitness(
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]);

        Assert.Equal(DesktopLayerBeddingWindowKind.ForeignAppWindow, witness);
    }

    [Fact]
    public void FindLeakWitness_SkipsIgnoredWindows()
    {
        // Minimized/cloaked/hidden windows rest below the widget by design;
        // they never witness a leak.
        DesktopLayerBeddingWindowKind? witness = DesktopLayerBeddingPolicy.FindLeakWitness(
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.IgnoredWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]);

        Assert.Null(witness);
    }

    [Fact]
    public void FindLeakWitness_ReturnsNullWhenBedded()
    {
        DesktopLayerBeddingWindowKind? witness = DesktopLayerBeddingPolicy.FindLeakWitness(
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ]);

        Assert.Null(witness);
    }
}
