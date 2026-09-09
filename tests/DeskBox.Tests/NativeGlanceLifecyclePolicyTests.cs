extern alias GlancePkg;

namespace DeskBox.Tests;

using Policy = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceLifecyclePolicy;
using Display = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceDisplayPolicy;
using ClockCadence = GlancePkg::DeskBox.GlancePackage.Rendering.ClockCadence;
using GlanceTimeFormatMode = GlancePkg::DeskBox.Models.GlanceTimeFormatMode;

/// <summary>
/// The energy policy behind the native glance lifecycle (audit rounds
/// 18-19): hidden/long-hidden stops everything, compact and user pause stop
/// only the image rotation, and the clock cadence (per-minute for a time
/// display, per-midnight for date/calendar-only, none otherwise) follows the
/// built-in UpdateClockTimer semantics. Pure logic running the real package
/// code.
/// </summary>
public class NativeGlanceLifecyclePolicyTests
{
    private static Policy.Activity Compute(
        bool visible = true, bool longHidden = false, bool collapsed = false,
        bool paused = false, ClockCadence cadence = ClockCadence.PerMinute,
        bool rotationConfigured = true, bool multipleImages = true) =>
        Policy.Compute(visible, longHidden, collapsed, paused, cadence, rotationConfigured, multipleImages);

    [Fact]
    public void VisibleAndIdleRunsEverything()
    {
        Policy.Activity activity = Compute();
        Assert.Equal(ClockCadence.PerMinute, activity.Cadence);
        Assert.True(activity.RotationRunning);
    }

    [Fact]
    public void HiddenStopsEverything()
    {
        Policy.Activity activity = Compute(visible: false);
        Assert.Equal(ClockCadence.None, activity.Cadence);
        Assert.False(activity.RotationRunning);
    }

    [Fact]
    public void LongHiddenStopsEverythingButClockSurvivesReveal()
    {
        Policy.Activity hidden = Compute(longHidden: true);
        Assert.Equal(ClockCadence.None, hidden.Cadence);
        Assert.False(hidden.RotationRunning);

        // The controller clears the long-hidden latch on reveal.
        Policy.Activity revealed = Compute();
        Assert.Equal(ClockCadence.PerMinute, revealed.Cadence);
    }

    [Fact]
    public void CompactAndPauseStopRotationOnly()
    {
        Policy.Activity compact = Compute(collapsed: true);
        Assert.Equal(ClockCadence.PerMinute, compact.Cadence);
        Assert.False(compact.RotationRunning);

        Policy.Activity paused = Compute(paused: true);
        Assert.Equal(ClockCadence.PerMinute, paused.Cadence);
        Assert.False(paused.RotationRunning);
    }

    [Fact]
    public void RotationRequiresConfigurationAndImages()
    {
        Policy.Activity noInterval = Compute(rotationConfigured: false);
        Assert.Equal(ClockCadence.PerMinute, noInterval.Cadence);
        Assert.False(noInterval.RotationRunning);

        Policy.Activity singleImage = Compute(multipleImages: false);
        Assert.Equal(ClockCadence.PerMinute, singleImage.Cadence);
        Assert.False(singleImage.RotationRunning);
    }

    [Fact]
    public void ClockDelayLandsJustPastTheNextMinuteBoundary()
    {
        // 30.5s into the minute -> ~29.55s remaining + 50ms guard.
        TimeSpan delay = Display.DelayToNextMinute(new DateTime(2026, 9, 9, 12, 0, 30).AddMilliseconds(500));
        Assert.InRange(delay.TotalMilliseconds, 29_500, 29_650);

        // Right at a boundary second=0 the NEXT boundary is a full minute
        // away (+50ms guard) - matching the built-in's (60 - Second) math.
        TimeSpan boundary = Display.DelayToNextMinute(new DateTime(2026, 9, 9, 12, 1, 0));
        Assert.InRange(boundary.TotalMilliseconds, 60_000, 60_150);
    }

    [Fact]
    public void MidnightCadenceDelaysToJustPastMidnight()
    {
        // Built-in parity: date/weekday/calendar-only widgets tick once per
        // day, 100 ms past the next midnight.
        DateTime now = new(2026, 9, 9, 23, 59, 0);
        TimeSpan delay = Display.DelayToBoundary(ClockCadence.PerMidnight, now);
        Assert.InRange(delay.TotalMinutes, 0.9, 1.1);
    }

    [Fact]
    public void ClockCadenceFollowsDisplaySettings()
    {
        var mode = GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.None;
        Assert.Equal(ClockCadence.None, Display.ComputeClockCadence(
            showTime: false, showDate: false, showWeekday: false, showCalendarEffective: false, traditionalMode: mode));
        Assert.Equal(ClockCadence.PerMinute, Display.ComputeClockCadence(
            showTime: true, showDate: false, showWeekday: false, showCalendarEffective: false, traditionalMode: mode));
        // Any date-bearing element without a clock ticks once per midnight.
        Assert.Equal(ClockCadence.PerMidnight, Display.ComputeClockCadence(
            showTime: false, showDate: true, showWeekday: false, showCalendarEffective: false, traditionalMode: mode));
        Assert.Equal(ClockCadence.PerMidnight, Display.ComputeClockCadence(
            showTime: false, showDate: false, showWeekday: false, showCalendarEffective: true, traditionalMode: mode));
        Assert.Equal(ClockCadence.PerMidnight, Display.ComputeClockCadence(
            showTime: false, showDate: false, showWeekday: false, showCalendarEffective: false,
            traditionalMode: GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode.Hebrew));
    }

    [Fact]
    public void ShowCalendarCarriesTheResponsiveFloor()
    {
        Assert.True(Display.ShowCalendarEffective(true, 440, 560));
        Assert.False(Display.ShowCalendarEffective(false, 440, 560));
        // Built-in floor: width >= 300 and height >= 280.
        Assert.False(Display.ShowCalendarEffective(true, 299, 560));
        Assert.False(Display.ShowCalendarEffective(true, 440, 279));
        Assert.True(Display.ShowCalendarEffective(true, 300, 280));
    }
}
