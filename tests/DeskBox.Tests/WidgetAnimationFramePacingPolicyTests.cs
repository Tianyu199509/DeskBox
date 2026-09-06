using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetAnimationFramePacingPolicyTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    [InlineData(360)]
    public void NativeCadence_DoesNotLoseEveryOtherFrameToDispatchJitter(int refreshHz)
    {
        double budget = 1000d / refreshHz;
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, budget);

        for (int frame = 0; frame < 120; frame++)
        {
            double timestamp = frame * budget + (frame % 2 == 0 ? 0.05 : -0.05);
            Assert.True(policy.ShouldSubmit(timestamp, budget));
            policy.RecordSubmission(timestamp, 0.5);
        }
        Assert.Equal(budget, policy.TargetIntervalMilliseconds, precision: 6);
    }

    [Fact]
    public void SlowerSharedDisplay_KeepsItsAverageCadenceWithoutAnIntegerFrameDivider()
    {
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, 1000d / 60);
        int submissions = 0;
        for (int tick = 0; tick < 144; tick++)
        {
            double timestamp = tick * 1000d / 144;
            if (policy.ShouldSubmit(timestamp, 1000d / 60))
            {
                policy.RecordSubmission(timestamp, 0.5);
                submissions++;
            }
        }
        Assert.InRange(submissions, 59, 61);
    }

    [Fact]
    public void AlreadySlowTicks_AreNotDividedAgainAfterCostAdaptation()
    {
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, 1000d / 144);
        for (int tick = 0; tick < 60; tick++)
        {
            double timestamp = tick * 1000d / 60;
            Assert.True(policy.ShouldSubmit(timestamp, 1000d / 144));
            policy.RecordSubmission(timestamp, 8);
        }
        Assert.InRange(policy.TargetIntervalMilliseconds, 11, 12);
    }

    [Fact]
    public void Overload_RecoversToNativeCadenceWhenWorkBecomesCheap()
    {
        const double budget = 1000d / 144;
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, budget);
        for (int frame = 0; frame < 8; frame++)
        {
            policy.RecordSubmission(frame * 40, 10);
        }
        Assert.True(policy.TargetIntervalMilliseconds > budget * 2);

        for (int frame = 8; frame < 48; frame++)
        {
            policy.RecordSubmission(frame * 40, 0.5);
        }
        Assert.Equal(budget, policy.TargetIntervalMilliseconds, precision: 6);
    }

    [Fact]
    public void OneExpensiveOwner_DoesNotThrottleAnotherOwnerOrTheNextAnimation()
    {
        var busy = new WidgetAnimationFramePacingPolicy();
        var light = new WidgetAnimationFramePacingPolicy();
        busy.Reset(0, 1000d / 144);
        light.Reset(0, 1000d / 144);
        for (int frame = 0; frame < 8; frame++)
        {
            busy.RecordSubmission(frame * 40, 12);
        }

        Assert.True(busy.TargetIntervalMilliseconds > light.TargetIntervalMilliseconds);
        busy.Reset(500, 1000d / 144);
        Assert.Equal(light.TargetIntervalMilliseconds, busy.TargetIntervalMilliseconds);
    }

    [Fact]
    public void RefreshModeChange_ReconsidersBudgetAndForcedTerminalFrameAlwaysSubmits()
    {
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, 1000d / 60);
        policy.RecordSubmission(0, 0.5);
        _ = policy.ShouldSubmit(1, 1000d / 240);
        Assert.Equal(1000d / 240, policy.TargetIntervalMilliseconds, precision: 6);
        Assert.True(policy.ShouldSubmit(1, 1000d / 240, force: true));
    }

    [Fact]
    public void ClockStall_DiscardsMissedDeadlinesInsteadOfBurstingToCatchUp()
    {
        var policy = new WidgetAnimationFramePacingPolicy();
        policy.Reset(0, 1000d / 144);
        policy.RecordSubmission(0, 0.5);
        Assert.True(policy.ShouldSubmit(200, 1000d / 144));
        policy.RecordSubmission(200, 0.5);
        Assert.False(policy.ShouldSubmit(201, 1000d / 144));
        Assert.True(policy.ShouldSubmit(207, 1000d / 144));
    }
}
