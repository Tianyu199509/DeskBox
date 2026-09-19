using System.Diagnostics;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupSwitchTimelineTests
{
    private static long Ticks(double milliseconds) =>
        (long)(milliseconds * Stopwatch.Frequency / 1000.0);

    [Fact]
    public void Describe_ReportsEveryPhaseInMilliseconds()
    {
        long start = Ticks(1000);
        var timeline = new WidgetGroupSwitchTimeline(WidgetGroupSwitchTimeline.SourceFresh, start);
        timeline.MarkPrepared(start + Ticks(120));
        timeline.MarkFirstFrame(start + Ticks(120 + 40));
        timeline.MarkSettled(start + Ticks(120 + 40 + 15));

        string described = timeline.Describe(TimeSpan.FromSeconds(42));

        Assert.Equal(
            "source=fresh prepareMs=120 firstFrameMs=40 settleMs=15 totalMs=175 sinceLastActiveMs=42000",
            described);
    }

    [Fact]
    public void Describe_MarksSkippedPhasesAndFirstActivationWithDash()
    {
        // Hidden-group switch: no first-frame wait; first ever activation of
        // this member: no inactivity interval.
        long start = Ticks(500);
        var timeline = new WidgetGroupSwitchTimeline(WidgetGroupSwitchTimeline.SourceCached, start);
        timeline.MarkPrepared(start + Ticks(5));
        timeline.MarkSettled(start + Ticks(5 + 7));

        string described = timeline.Describe(sinceLastActive: null);

        Assert.Equal(
            "source=cached prepareMs=5 firstFrameMs=- settleMs=7 totalMs=12 sinceLastActiveMs=-",
            described);
    }

    [Fact]
    public void Describe_BeforeSettle_UsesNowAsEndpointWithoutThrowing()
    {
        var timeline = new WidgetGroupSwitchTimeline(
            WidgetGroupSwitchTimeline.SourceFresh,
            Stopwatch.GetTimestamp());
        timeline.MarkPrepared(Stopwatch.GetTimestamp());

        string described = timeline.Describe(sinceLastActive: null);

        Assert.StartsWith("source=fresh prepareMs=", described, StringComparison.Ordinal);
        Assert.Contains(" firstFrameMs=- ", described, StringComparison.Ordinal);
        Assert.DoesNotContain("totalMs=-", described, StringComparison.Ordinal);
    }
}
