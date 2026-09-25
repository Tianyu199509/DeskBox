using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupFailureProbeTests
{
    [Theory]
    [InlineData("reused-detach-create", "reused-detach-create", true, true)]
    [InlineData("reused-detach-first-frame", "reused-detach-create,reused-detach-first-frame", true, true)]
    [InlineData("reused-detach-create", "reused-detach-create", false, false)]
    [InlineData("reused-detach-create", "reused-detach-first-frame", true, false)]
    [InlineData("reused-detach-create", "reused-detach-create-extra", true, false)]
    [InlineData("merge-recovery-target-unavailable",
        "merge-recovery-target-unavailable,merge-recovery-promotion", true, true)]
    [InlineData("merge-recovery-rebuild", "merge-recovery-rebuild", false, false)]
    public void FaultStage_RequiresAnExactStageAndIsolatedDataRoot(
        string stage,
        string requested,
        bool isolated,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetGroupFailureProbe.IsRequested(stage, requested, isolated));
    }
}
