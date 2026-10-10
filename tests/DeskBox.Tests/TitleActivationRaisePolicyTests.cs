using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TitleActivationRaisePolicyTests
{
    [Fact]
    public void ShouldSkipRepeatRaise_AcceptsLiveRaiseOfSameWindowInWindow()
    {
        Assert.True(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: true,
            sameActiveWindow: true,
            elapsedSincePreviousRaise: TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void ShouldSkipRepeatRaise_RejectsWhenNoRaiseIsTracked()
    {
        // The previous raise already restored; the Z-order state legitimately
        // changed since, so a new press must raise for real.
        Assert.False(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: false,
            sameActiveWindow: true,
            elapsedSincePreviousRaise: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void ShouldSkipRepeatRaise_RejectsDifferentActiveWindow()
    {
        Assert.False(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: true,
            sameActiveWindow: false,
            elapsedSincePreviousRaise: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void ShouldSkipRepeatRaise_RejectsAfterSuppressWindowElapses()
    {
        Assert.False(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: true,
            sameActiveWindow: true,
            elapsedSincePreviousRaise: TitleActivationRaisePolicy.RepeatRaiseSuppressWindow +
                TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ShouldSkipRepeatRaise_RejectsAtWindowBoundary()
    {
        // Half-open window [0, 2s): exactly at the boundary the previous
        // raise is stale and a new press must raise for real.
        Assert.False(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: true,
            sameActiveWindow: true,
            elapsedSincePreviousRaise: TitleActivationRaisePolicy.RepeatRaiseSuppressWindow));
    }

    [Fact]
    public void ShouldSkipRepeatRaise_RejectsNegativeElapsed()
    {
        // Clock skew / timestamp reset must never produce a permanent skip.
        Assert.False(TitleActivationRaisePolicy.ShouldSkipRepeatRaise(
            previousRaiseStillTracked: true,
            sameActiveWindow: true,
            elapsedSincePreviousRaise: TimeSpan.FromMilliseconds(-50)));
    }
}
