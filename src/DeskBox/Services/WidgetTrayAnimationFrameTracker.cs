// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct WidgetTrayAnimationFrameSummary(
    int RefreshRateHz,
    int ParticipantCount,
    int FrameCount,
    int EstimatedDroppedFrames,
    double MaximumFrameIntervalMilliseconds,
    double ElapsedMilliseconds)
{
    public double FrameBudgetMilliseconds => 1000d / Math.Max(1, RefreshRateHz);
    public int PositionSubmissionCount { get; init; }
    public int PositionSubmissionTickCount { get; init; }
    public double MaximumPositionSubmissionIntervalMilliseconds { get; init; }
}

/// <summary>
/// Measures one shared tray animation against every refresh-rate group taking
/// part in the batch. A 60 Hz and a 144 Hz display therefore receive separate
/// callback-budget results even though their HWND positions share one clock.
/// Native position submissions are counted separately. Neither counter is a
/// DWM Present or an observed scan-out frame count.
/// </summary>
public sealed class WidgetTrayAnimationFrameTracker
{
    private readonly List<RefreshRateGroup> _groups;

    public WidgetTrayAnimationFrameTracker(
        long startedTimestamp,
        IEnumerable<int> participantRefreshRates)
    {
        ArgumentNullException.ThrowIfNull(participantRefreshRates);

        List<int> normalizedRates = participantRefreshRates
            .Select(rate => WidgetDisplayRefreshRatePolicy.Normalize(
                (uint)Math.Max(0, rate)))
            .ToList();
        if (normalizedRates.Count == 0)
        {
            normalizedRates.Add(WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz);
        }

        _groups = normalizedRates
            .GroupBy(rate => rate)
            .OrderBy(group => group.Key)
            .Select(group => new RefreshRateGroup(
                group.Key,
                group.Count(),
                new WidgetCompactAnimationFrameTracker(startedTimestamp, group.Key)))
            .ToList();
    }

    public void RecordFrame(long timestamp)
    {
        foreach (RefreshRateGroup group in _groups)
        {
            group.Tracker.RecordFrame(timestamp);
        }
    }

    public void RecordPositionSubmission(long timestamp, int refreshRateHz)
    {
        int rate = WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, refreshRateHz));
        RefreshRateGroup? group = null;
        foreach (var candidate in _groups)
        {
            if (candidate.RefreshRateHz == rate)
            {
                group = candidate;
                break;
            }
        }
        if (group is null)
        {
            // A mode/monitor change may introduce a new budget during a run.
            group = new RefreshRateGroup(rate, 0,
                new WidgetCompactAnimationFrameTracker(timestamp, rate));
            _groups.Add(group);
        }
        group.RecordPositionSubmission(timestamp);
    }

    public IReadOnlyList<WidgetTrayAnimationFrameSummary> Complete(long timestamp)
    {
        return _groups
            .Select(group =>
            {
                WidgetCompactAnimationFrameSummary summary =
                    group.Tracker.Complete(timestamp);
                return new WidgetTrayAnimationFrameSummary(
                    summary.RefreshRateHz,
                    group.ParticipantCount,
                    summary.FrameCount,
                    summary.EstimatedDroppedFrames,
                    summary.MaximumFrameIntervalMilliseconds,
                    summary.ElapsedMilliseconds)
                {
                    PositionSubmissionCount = group.PositionSubmissionCount,
                    PositionSubmissionTickCount = group.PositionSubmissionTickCount,
                    MaximumPositionSubmissionIntervalMilliseconds = group.MaximumPositionSubmissionIntervalMilliseconds
                };
            })
            .ToList();
    }

    private sealed class RefreshRateGroup(
        int refreshRateHz,
        int participantCount,
        WidgetCompactAnimationFrameTracker tracker)
    {
        private long? _lastPositionSubmissionTimestamp;
        public int RefreshRateHz { get; } = refreshRateHz;
        public int ParticipantCount { get; } = participantCount;
        public WidgetCompactAnimationFrameTracker Tracker { get; } = tracker;
        public int PositionSubmissionCount { get; private set; }
        public int PositionSubmissionTickCount { get; private set; }
        public double MaximumPositionSubmissionIntervalMilliseconds { get; private set; }

        public void RecordPositionSubmission(long timestamp)
        {
            PositionSubmissionCount++;
            if (_lastPositionSubmissionTimestamp is { } previous)
            {
                if (timestamp <= previous)
                {
                    return;
                }
                MaximumPositionSubmissionIntervalMilliseconds = Math.Max(
                    MaximumPositionSubmissionIntervalMilliseconds,
                    Stopwatch.GetElapsedTime(previous, timestamp).TotalMilliseconds);
            }
            _lastPositionSubmissionTimestamp = timestamp;
            PositionSubmissionTickCount++;
        }
    }
}

internal static class WidgetTrayAnimationDiagnostics
{
    public static void Report(
        WidgetTrayAnimationFrameTracker? tracker,
        long completedTimestamp,
        bool isShowing,
        string outcome,
        string scope,
        Action<string> verboseLog)
    {
        if (tracker is null)
        {
            return;
        }

        foreach (WidgetTrayAnimationFrameSummary summary in tracker.Complete(completedTimestamp))
        {
            string details =
                $"scope={scope} mode={(isShowing ? "show" : "hide")} " +
                $"outcome={outcome} refreshHz={summary.RefreshRateHz} " +
                $"participantsAtStart={summary.ParticipantCount} cpuCallbacks={summary.FrameCount} " +
                $"estimatedCallbackGaps={summary.EstimatedDroppedFrames} " +
                $"maxCallbackMs={summary.MaximumFrameIntervalMilliseconds:F1} " +
                $"nativePositionCommits={summary.PositionSubmissionCount} " +
                $"nativeCommitTicks={summary.PositionSubmissionTickCount} " +
                $"maxNativeCommitIntervalMs={summary.MaximumPositionSubmissionIntervalMilliseconds:F1} " +
                $"budgetMs={summary.FrameBudgetMilliseconds:F1} " +
                $"elapsedMs={summary.ElapsedMilliseconds:F1}";
            PerformanceLogger.Mark("TrayAnimation", details);
            if (summary.EstimatedDroppedFrames > 0)
            {
                App.Log($"[TrayAnimation] Frame budget missed {details}");
            }
            else
            {
                verboseLog($"[TrayAnimation] {details}");
            }
        }
    }
}
