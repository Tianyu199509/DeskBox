using System.Diagnostics;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactPerformancePolicyTests
{
    [Fact]
    public void WarmupSchedule_PrioritizesColdBuiltInSurfaces()
    {
        int quickCapture = WidgetCompactWarmupSchedulePolicy
            .GetInitialDelayMilliseconds(WidgetKind.QuickCapture);
        int weather = WidgetCompactWarmupSchedulePolicy
            .GetInitialDelayMilliseconds(WidgetKind.Weather);
        int file = WidgetCompactWarmupSchedulePolicy
            .GetInitialDelayMilliseconds(WidgetKind.File);

        Assert.True(quickCapture < weather);
        Assert.True(weather < file);
        Assert.InRange(quickCapture, 150, 300);
        Assert.InRange(file, 350, 600);
    }

    [Theory]
    [InlineData(0u, 60)]
    [InlineData(1u, 60)]
    [InlineData(23u, 60)]
    [InlineData(24u, 24)]
    [InlineData(60u, 60)]
    [InlineData(144u, 144)]
    [InlineData(240u, 240)]
    [InlineData(1001u, 60)]
    public void RefreshRate_NormalizesDriverValues(uint reported, int expected)
    {
        Assert.Equal(expected, WidgetDisplayRefreshRatePolicy.Normalize(reported));
    }

    [Fact]
    public void Readiness_WaitsOnlyUntilTheBoundedDeadline()
    {
        Assert.Equal(
            WidgetCompactExpansionReadinessDecision.WaitForWarmup,
            WidgetCompactExpansionReadinessPolicy.Decide(
                isReady: false,
                deadlineElapsed: false));
        Assert.Equal(
            WidgetCompactExpansionReadinessDecision.ExpandWithLiveLayoutFallback,
            WidgetCompactExpansionReadinessPolicy.Decide(
                isReady: false,
                deadlineElapsed: true));
        Assert.Equal(
            WidgetCompactExpansionReadinessDecision.ExpandNow,
            WidgetCompactExpansionReadinessPolicy.Decide(
                isReady: true,
                deadlineElapsed: false));
    }

    [Fact]
    public void LayerRestore_WaitsForCollapsedSurfaceToCommit()
    {
        Assert.Equal(
            WidgetCompactLayerRestoreDecision.WaitForFrameCommit,
            DecideLayerRestore(committedFrames: 1));
        Assert.Equal(
            WidgetCompactLayerRestoreDecision.Restore,
            DecideLayerRestore(
                committedFrames: WidgetCompactLayerRestorePolicy.RequiredCommittedFrames));
    }

    [Fact]
    public void LayerRestore_CancelsAStaleCollapseGeneration()
    {
        Assert.Equal(
            WidgetCompactLayerRestoreDecision.Cancel,
            WidgetCompactLayerRestorePolicy.Decide(
                isClosing: false,
                collapseInitialized: true,
                targetCollapsed: false,
                transitionActive: false,
                activeGeneration: 8,
                restoreGeneration: 7,
                committedFrames: 2,
                deadlineElapsed: false));
    }

    [Fact]
    public void LayerRestore_FallbackCannotCutThroughAnActiveTransition()
    {
        Assert.Equal(
            WidgetCompactLayerRestoreDecision.WaitForFrameCommit,
            WidgetCompactLayerRestorePolicy.Decide(
                isClosing: false,
                collapseInitialized: true,
                targetCollapsed: true,
                transitionActive: true,
                activeGeneration: 9,
                restoreGeneration: 9,
                committedFrames: 0,
                deadlineElapsed: true));
    }

    [Fact]
    public void FrameTracker_UsesHighRefreshFrameBudget()
    {
        const int refreshRateHz = 144;
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetCompactAnimationFrameTracker(started, refreshRateHz);

        long timestamp = started;
        for (int frame = 0; frame < 12; frame++)
        {
            timestamp += MillisecondsToStopwatchTicks(1000d / refreshRateHz);
            tracker.RecordFrame(timestamp);
        }

        WidgetCompactAnimationFrameSummary result = tracker.Complete(timestamp);

        Assert.Equal(refreshRateHz, result.RefreshRateHz);
        Assert.Equal(12, result.FrameCount);
        Assert.Equal(0, result.EstimatedDroppedFrames);
        Assert.InRange(result.FrameBudgetMilliseconds, 6.9, 7.0);
    }

    [Fact]
    public void FrameTracker_ReportsLongUiThreadStall()
    {
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetCompactAnimationFrameTracker(started, 60);

        long first = started + MillisecondsToStopwatchTicks(16.7);
        tracker.RecordFrame(first);
        long stalled = first + MillisecondsToStopwatchTicks(67);
        tracker.RecordFrame(stalled);

        WidgetCompactAnimationFrameSummary result = tracker.Complete(stalled);

        Assert.True(result.EstimatedDroppedFrames >= 3);
        Assert.True(result.MaximumFrameIntervalMilliseconds >= 66);
    }

    [Fact]
    public void FrameTracker_DistinguishesClockTicksFromChangedNativeBoundsSubmissions()
    {
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetCompactAnimationFrameTracker(started, 144);
        long timestamp = started;
        for (int tick = 1; tick <= 12; tick++)
        {
            timestamp = started + MillisecondsToStopwatchTicks(tick * 1000d / 144);
            tracker.RecordTick(timestamp, 1000d / 144);
            if (tick % 3 == 0)
            {
                tracker.RecordBoundsUpdate(timestamp, workMilliseconds: 2.5);
            }
        }

        WidgetCompactAnimationFrameSummary summary = tracker.Complete(timestamp);
        Assert.Equal(12, summary.FrameCount);
        Assert.Equal(4, summary.BoundsUpdateCount);
        Assert.Equal(0, summary.EstimatedDroppedFrames);
        Assert.InRange(summary.MaximumBoundsUpdateIntervalMilliseconds, 20.8, 20.9);
        Assert.Equal(2.5, summary.MaximumSubmissionWorkMilliseconds);
    }

    [Fact]
    public void FrameTracker_UsesCurrentBudgetAfterDisplayModeChanges()
    {
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetCompactAnimationFrameTracker(started, 144);
        long timestamp = started + MillisecondsToStopwatchTicks(1000d / 60);
        tracker.RecordTick(timestamp, 1000d / 60);
        Assert.Equal(0, tracker.Complete(timestamp).EstimatedDroppedFrames);
    }

    [Fact]
    public void TrayFrameTracker_SeparatesMixedRefreshRateBudgets()
    {
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetTrayAnimationFrameTracker(
            started,
            [60, 144, 144]);

        long timestamp = started;
        for (int frame = 0; frame < 12; frame++)
        {
            timestamp += MillisecondsToStopwatchTicks(1000d / 144d);
            tracker.RecordFrame(timestamp);
        }

        IReadOnlyList<WidgetTrayAnimationFrameSummary> results =
            tracker.Complete(timestamp);

        WidgetTrayAnimationFrameSummary sixtyHz = Assert.Single(
            results,
            result => result.RefreshRateHz == 60);
        WidgetTrayAnimationFrameSummary oneFortyFourHz = Assert.Single(
            results,
            result => result.RefreshRateHz == 144);
        Assert.Equal(1, sixtyHz.ParticipantCount);
        Assert.Equal(2, oneFortyFourHz.ParticipantCount);
        Assert.Equal(0, sixtyHz.EstimatedDroppedFrames);
        Assert.Equal(0, oneFortyFourHz.EstimatedDroppedFrames);
    }

    [Fact]
    public void TrayFrameTracker_DetectsHighRefreshDropsIndependently()
    {
        long started = Stopwatch.GetTimestamp();
        var tracker = new WidgetTrayAnimationFrameTracker(started, [60, 144]);

        long timestamp = started;
        for (int frame = 0; frame < 6; frame++)
        {
            timestamp += MillisecondsToStopwatchTicks(1000d / 60d);
            tracker.RecordFrame(timestamp);
        }

        IReadOnlyList<WidgetTrayAnimationFrameSummary> results =
            tracker.Complete(timestamp);

        Assert.Equal(
            0,
            Assert.Single(results, result => result.RefreshRateHz == 60)
                .EstimatedDroppedFrames);
        Assert.True(
            Assert.Single(results, result => result.RefreshRateHz == 144)
                .EstimatedDroppedFrames > 0);
    }

    [Fact]
    public void ClockBoostLease_RemainsEnabledUntilTheLastAnimationStops()
    {
        var states = new List<bool>();
        var leases = new ReferenceCountedToggleLeasePool(states.Add);

        IDisposable first = leases.Acquire();
        IDisposable second = leases.Acquire();

        Assert.Equal([true], states);
        Assert.Equal(2, leases.ActiveLeaseCount);

        first.Dispose();
        first.Dispose();
        Assert.Equal([true], states);
        Assert.Equal(1, leases.ActiveLeaseCount);

        second.Dispose();
        Assert.Equal([true, false], states);
        Assert.Equal(0, leases.ActiveLeaseCount);
    }

    private static long MillisecondsToStopwatchTicks(double milliseconds) =>
        Math.Max(1, (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d));

    private static WidgetCompactLayerRestoreDecision DecideLayerRestore(
        int committedFrames)
    {
        return WidgetCompactLayerRestorePolicy.Decide(
            isClosing: false,
            collapseInitialized: true,
            targetCollapsed: true,
            transitionActive: false,
            activeGeneration: 4,
            restoreGeneration: 4,
            committedFrames,
            deadlineElapsed: false);
    }
}
