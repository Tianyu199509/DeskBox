using System.Diagnostics;

namespace DeskBox.Services;

/// <summary>
/// Four-point timeline of one in-place group member switch (residency
/// roadmap P0): entry → content prepared → first presented frame → settled.
/// Timestamps are <see cref="Stopwatch"/> ticks so the manager can stamp
/// them for free on the hot path; formatting happens once at the settle log.
/// </summary>
internal sealed class WidgetGroupSwitchTimeline
{
    internal const string SourceCached = "cached";
    internal const string SourceFresh = "fresh";

    private readonly long _startedTimestamp;
    private long _preparedTimestamp;
    private long _firstFrameTimestamp;
    private long _settledTimestamp;

    internal WidgetGroupSwitchTimeline(string contentSource, long startedTimestamp)
    {
        ContentSource = contentSource;
        _startedTimestamp = startedTimestamp;
    }

    internal string ContentSource { get; }

    internal void MarkPrepared(long timestamp) => _preparedTimestamp = timestamp;

    internal void MarkFirstFrame(long timestamp) => _firstFrameTimestamp = timestamp;

    internal void MarkSettled(long timestamp) => _settledTimestamp = timestamp;

    /// <summary>
    /// Key/value fragment appended to the existing settle log line. A phase
    /// that never ran (hidden group skips the first-frame wait; a member
    /// switched to for the first time has no inactivity interval) prints
    /// "-" rather than 0 so the two cases stay distinguishable in the log.
    /// </summary>
    internal string Describe(TimeSpan? sinceLastActive)
    {
        long end = _settledTimestamp != 0 ? _settledTimestamp : Stopwatch.GetTimestamp();
        string prepare = FormatSpan(_startedTimestamp, _preparedTimestamp);
        string firstFrame = FormatSpan(_preparedTimestamp, _firstFrameTimestamp);
        string settle = FormatSpan(
            _firstFrameTimestamp != 0 ? _firstFrameTimestamp : _preparedTimestamp,
            end);
        string total = FormatSpan(_startedTimestamp, end);
        string inactivity = sinceLastActive is { } interval
            ? ((long)interval.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "-";
        return $"source={ContentSource} prepareMs={prepare} firstFrameMs={firstFrame} " +
               $"settleMs={settle} totalMs={total} sinceLastActiveMs={inactivity}";
    }

    private static string FormatSpan(long from, long to)
    {
        if (from == 0 || to == 0 || to < from)
        {
            return "-";
        }

        return ((long)Stopwatch.GetElapsedTime(from, to).TotalMilliseconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
