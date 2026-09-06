using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetFrameClockHealthPolicyTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(360)]
    public void MatchingClockToleratesDispatchJitter(int refreshHz)
    {
        var health = new WidgetFrameClockHealthPolicy();
        for (int i = 0; i < 40; i++)
            Assert.False(health.RecordWait(1000d / refreshHz + 0.3, 1000d / refreshHz));
    }

    [Fact]
    public void SlowOutputClockCannotKeepCappingFasterParticipant()
    {
        var health = new WidgetFrameClockHealthPolicy();
        for (int i = 0; i < 7; i++) Assert.False(health.RecordWait(1000d / 60, 1000d / 144));
        Assert.True(health.RecordWait(1000d / 60, 1000d / 144));
    }

    [Fact]
    public void IsolatedStallsAndChangedDisplayBudgetDoNotPoisonTheClock()
    {
        var health = new WidgetFrameClockHealthPolicy();
        for (int i = 0; i < 7; i++) Assert.False(health.RecordWait(17, 7));
        Assert.False(health.RecordWait(17, 1000d / 60));
        for (int i = 0; i < 7; i++) Assert.False(health.RecordWait(17, 7));
    }
}
