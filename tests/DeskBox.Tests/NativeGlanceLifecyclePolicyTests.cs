extern alias GlancePkg;

namespace DeskBox.Tests;

using Policy = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceLifecyclePolicy;

/// <summary>
/// The energy policy behind the native glance lifecycle (audit rounds
/// 18-19): hidden/long-hidden stops everything, compact and user pause stop
/// only the image rotation, and the clock re-arms on minute boundaries.
/// Pure logic, so these run the real package code.
/// </summary>
public class NativeGlanceLifecyclePolicyTests
{
    [Fact]
    public void VisibleAndIdleRunsEverything()
    {
        Policy.Activity activity = Policy.Compute(
            visible: true, longHidden: false, collapsed: false,
            paused: false, rotationConfigured: true, multipleImages: true);
        Assert.True(activity.ClockRunning);
        Assert.True(activity.RotationRunning);
    }

    [Fact]
    public void HiddenStopsEverything()
    {
        Policy.Activity activity = Policy.Compute(
            visible: false, longHidden: false, collapsed: false,
            paused: false, rotationConfigured: true, multipleImages: true);
        Assert.False(activity.ClockRunning);
        Assert.False(activity.RotationRunning);
    }

    [Fact]
    public void LongHiddenStopsEverythingButClockSurvivesReveal()
    {
        Policy.Activity hidden = Policy.Compute(
            visible: true, longHidden: true, collapsed: false,
            paused: false, rotationConfigured: true, multipleImages: true);
        Assert.False(hidden.ClockRunning);
        Assert.False(hidden.RotationRunning);

        // The controller clears the long-hidden latch on reveal.
        Policy.Activity revealed = Policy.Compute(
            visible: true, longHidden: false, collapsed: false,
            paused: false, rotationConfigured: true, multipleImages: true);
        Assert.True(revealed.ClockRunning);
    }

    [Theory]
    [InlineData(true)]  // compact
    [InlineData(false)] // user pause
    public void CompactAndPauseStopRotationOnly(bool collapsed)
    {
        Policy.Activity activity = Policy.Compute(
            visible: true, longHidden: false, collapsed: collapsed,
            paused: !collapsed, rotationConfigured: true, multipleImages: true);
        Assert.True(activity.ClockRunning);
        Assert.False(activity.RotationRunning);
    }

    [Fact]
    public void RotationRequiresConfigurationAndImages()
    {
        Policy.Activity noInterval = Policy.Compute(
            visible: true, longHidden: false, collapsed: false,
            paused: false, rotationConfigured: false, multipleImages: true);
        Assert.True(noInterval.ClockRunning);
        Assert.False(noInterval.RotationRunning);

        Policy.Activity singleImage = Policy.Compute(
            visible: true, longHidden: false, collapsed: false,
            paused: false, rotationConfigured: true, multipleImages: false);
        Assert.True(singleImage.ClockRunning);
        Assert.False(singleImage.RotationRunning);
    }

    [Fact]
    public void ClockDelayLandsJustPastTheNextMinuteBoundary()
    {
        // 30.5s into the minute -> ~29.55s remaining + 50ms guard.
        TimeSpan delay = Policy.DelayToNextMinute(new DateTime(2026, 9, 9, 12, 0, 30).AddMilliseconds(500));
        Assert.InRange(delay.TotalMilliseconds, 29_500, 29_650);

        // Right at a boundary second=0 the NEXT boundary is a full minute
        // away (+50ms guard) - matching the built-in's (60 - Second) math.
        TimeSpan boundary = Policy.DelayToNextMinute(new DateTime(2026, 9, 9, 12, 1, 0));
        Assert.InRange(boundary.TotalMilliseconds, 60_000, 60_150);
    }
}
