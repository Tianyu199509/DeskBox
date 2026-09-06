// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct WidgetCompactAnimationFrameSummary(
    int RefreshRateHz,
    int FrameCount,
    int EstimatedDroppedFrames,
    double MaximumFrameIntervalMilliseconds,
    double ElapsedMilliseconds,
    int BoundsUpdateCount = 0,
    double MaximumBoundsUpdateIntervalMilliseconds = 0,
    double MaximumSubmissionWorkMilliseconds = 0)
{
    public double FrameBudgetMilliseconds => 1000d / Math.Max(1, RefreshRateHz);
}

/// <summary>
/// Small allocation-free tracker for diagnosing capsule animation cadence.
/// Timestamps are Stopwatch ticks so the policy can be covered by unit tests.
/// </summary>
public sealed class WidgetCompactAnimationFrameTracker
{
    private readonly long _startedTimestamp;
    private long _lastFrameTimestamp;
    private double _maximumFrameIntervalMilliseconds;
    private int _frameCount;
    private int _estimatedDroppedFrames;
    private long _lastBoundsUpdateTimestamp;
    private int _boundsUpdateCount;
    private double _maximumBoundsUpdateIntervalMilliseconds;
    private double _maximumSubmissionWorkMilliseconds;

    public WidgetCompactAnimationFrameTracker(long startedTimestamp, int refreshRateHz)
    {
        _startedTimestamp = startedTimestamp;
        _lastFrameTimestamp = startedTimestamp;
        _lastBoundsUpdateTimestamp = startedTimestamp;
        RefreshRateHz = WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, refreshRateHz));
    }

    public int RefreshRateHz { get; }

    public void RecordFrame(long timestamp)
    {
        RecordTick(timestamp, 1000d / RefreshRateHz);
    }

    /// <summary>UI dispatch cadence only, not a presented-frame measurement.</summary>
    public void RecordTick(long timestamp, double frameBudgetMilliseconds)
    {
        if (timestamp <= _lastFrameTimestamp)
        {
            return;
        }

        double intervalMs = Stopwatch.GetElapsedTime(_lastFrameTimestamp, timestamp).TotalMilliseconds;
        _lastFrameTimestamp = timestamp;
        _frameCount++;
        _maximumFrameIntervalMilliseconds = Math.Max(_maximumFrameIntervalMilliseconds, intervalMs);

        double frameBudgetMs = double.IsFinite(frameBudgetMilliseconds) && frameBudgetMilliseconds > 0
            ? frameBudgetMilliseconds
            : 1000d / RefreshRateHz;
        if (intervalMs > frameBudgetMs * 1.5)
        {
            _estimatedDroppedFrames += Math.Max(1, (int)Math.Round(intervalMs / frameBudgetMs) - 1);
        }
    }

    /// <summary>Called after a changed native bounds update has been committed.</summary>
    public void RecordBoundsUpdate(long timestamp, double workMilliseconds)
    {
        if (timestamp < _lastBoundsUpdateTimestamp)
        {
            return;
        }

        _maximumBoundsUpdateIntervalMilliseconds = Math.Max(
            _maximumBoundsUpdateIntervalMilliseconds,
            Stopwatch.GetElapsedTime(_lastBoundsUpdateTimestamp, timestamp).TotalMilliseconds);
        _lastBoundsUpdateTimestamp = timestamp;
        _boundsUpdateCount++;
        if (double.IsFinite(workMilliseconds) && workMilliseconds >= 0)
        {
            _maximumSubmissionWorkMilliseconds = Math.Max(
                _maximumSubmissionWorkMilliseconds, workMilliseconds);
        }
    }

    public WidgetCompactAnimationFrameSummary Complete(long timestamp)
    {
        long completedTimestamp = Math.Max(timestamp, _startedTimestamp);
        return new WidgetCompactAnimationFrameSummary(
            RefreshRateHz,
            _frameCount,
            _estimatedDroppedFrames,
            _maximumFrameIntervalMilliseconds,
            Stopwatch.GetElapsedTime(_startedTimestamp, completedTimestamp).TotalMilliseconds,
            _boundsUpdateCount,
            _maximumBoundsUpdateIntervalMilliseconds,
            _maximumSubmissionWorkMilliseconds);
    }
}
