namespace DeskBox.Tests;

using DeskBox.Models;
using DeskBox.Services;

/// <summary>
/// Deep review R13 (R12 debt ③): the download loop reports every 128KB
/// chunk; the throttler must collapse that stream without losing the first
/// report, integer-percent steps, or the final report — and without ever
/// moving the displayed progress backwards.
/// </summary>
public sealed class AppUpdateDownloadProgressThrottleTests
{
    [Fact]
    public void FirstReport_AlwaysPasses()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(0, 1_000_000), nowTicks: 1000));
    }

    [Fact]
    public void SamePercentWithinFloor_IsDropped()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(120_000, 1_000_000), nowTicks: 1000));
        // Both stay at 12% inside the 100ms floor.
        Assert.False(throttle.ShouldPush(new AppUpdateDownloadProgress(125_000, 1_000_000), nowTicks: 1050));
        Assert.False(throttle.ShouldPush(new AppUpdateDownloadProgress(129_999, 1_000_000), nowTicks: 1080));
    }

    [Fact]
    public void PercentStep_PassesWithinFloor()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(0, 1_000_000), nowTicks: 1000));
        // 0% -> 10%: a percent step passes even inside the 100ms floor.
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(100_000, 1_000_000), nowTicks: 1010));
    }

    [Fact]
    public void TimeFloor_CarriesSlowProgress()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(0, null), nowTicks: 1000));
        // Unknown total: only the time floor can carry updates.
        Assert.False(throttle.ShouldPush(new AppUpdateDownloadProgress(5_000, null), nowTicks: 1050));
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(130_000, null), nowTicks: 1120));
    }

    [Fact]
    public void FinalReport_AlwaysPasses()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(990_000, 1_000_000), nowTicks: 1000));
        // 99% -> 100% right after an accepted report: the final report
        // must still reach the UI so the progress bar completes.
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(1_000_000, 1_000_000), nowTicks: 1010));
    }

    [Fact]
    public void Progress_NeverMovesBackwards()
    {
        var throttle = new AppUpdateDownloadProgressThrottle();
        Assert.True(throttle.ShouldPush(new AppUpdateDownloadProgress(500_000, 1_000_000), nowTicks: 1000));
        // A late-arriving lower byte count (chunk reordering in the pipe
        // queue can never produce this, but the guard costs nothing) must
        // not be accepted as a percent step backwards.
        Assert.False(throttle.ShouldPush(new AppUpdateDownloadProgress(400_000, 1_000_000), nowTicks: 5000));
    }
}
