namespace DeskBox.Services;

/// <summary>
/// Elastic per-display layout (spec D11 / 5.2). A display's reference layout
/// is warped onto the current effective work area one axis at a time: the
/// axis is cut into widget-covered segments and blank segments; short blanks
/// (screen margins and gaps up to <see cref="SpacingThresholdDips"/>) keep
/// their DIP length, while widgets and large open areas absorb the change.
/// Growing scales widgets and open areas together; shrinking first
/// compresses open areas and only then widgets (down to
/// <see cref="MinimumShrinkScale"/>, a preferred floor that yields only when
/// even it cannot fit the axis). The mapping is monotone, so order and
/// non-overlap of the reference layout survive, and aligned edges stay
/// aligned. Pure functions only: no Win32, no settings.
/// </summary>
internal static class WidgetElasticLayout
{
    internal const double SpacingThresholdDips = 64;
    internal const double MinimumShrinkScale = 0.5;
    internal const double FixedContentMaxGrowScale = 1.5;
    internal const double IdentityToleranceDips = 0.5;

    /// <summary>
    /// Absolute size floor for geometry produced by the elastic projection
    /// (window creation, physical caches, seeding). Distinct from the
    /// interactive-resize floor: extreme shrink may legitimately project a
    /// widget below the preferred minimum, and re-clamping it there would
    /// push the widget over its neighbours.
    /// </summary>
    internal const double AbsoluteMinWidgetSizeDips = 20;

    /// <summary>
    /// The per-axis minimum length handed to the projection clamp so the
    /// clamp never fires on a widget the plan itself placed: the effective
    /// per-item scale (including the fixed-content grow cap, conservative
    /// for non-fixed items — a smaller floor only removes clamping) times
    /// the narrowest authored item on the axis, capped at the preferred
    /// minimum.
    /// </summary>
    internal static double AdaptiveMinimumLength(
        double preferredMinimum,
        double widgetScale,
        double minAuthoredLength)
    {
        double effectiveScale = widgetScale > FixedContentMaxGrowScale
            ? FixedContentMaxGrowScale
            : widgetScale;
        return Math.Min(preferredMinimum, effectiveScale * minAuthoredLength);
    }

    internal static ElasticAxisPlan PlanAxis(
        double referenceLength,
        double targetLength,
        IEnumerable<(double Start, double End)> intervals,
        bool allowGrow = true)
    {
        double reference = Math.Max(1, double.IsFinite(referenceLength) ? referenceLength : 1);
        double target = Math.Max(1, double.IsFinite(targetLength) ? targetLength : 1);
        if (Math.Abs(target - reference) <= IdentityToleranceDips ||
            (!allowGrow && target > reference))
        {
            return ElasticAxisPlan.Identity(reference, target);
        }

        List<ElasticSegment> segments = BuildSegments(reference, intervals);
        double covered = segments.Where(segment => segment.Kind == ElasticSegmentKind.Covered).Sum(segment => segment.ReferenceLength);
        double open = segments.Where(segment => segment.Kind == ElasticSegmentKind.Open).Sum(segment => segment.ReferenceLength);
        double spacing = segments.Where(segment => segment.Kind == ElasticSegmentKind.Spacing).Sum(segment => segment.ReferenceLength);
        double widgetScale = target > reference
            ? Grow(segments, target, covered, open, spacing)
            : Shrink(segments, reference - target, covered, spacing);
        return new ElasticAxisPlan(reference, target, segments, widgetScale);
    }

    private static List<ElasticSegment> BuildSegments(
        double length,
        IEnumerable<(double Start, double End)> intervals)
    {
        List<(double Start, double End)> merged = [];
        foreach ((double start, double end) in intervals
                     .Where(interval => double.IsFinite(interval.Start) && double.IsFinite(interval.End))
                     .Select(interval => (Math.Clamp(interval.Start, 0, length), Math.Clamp(interval.End, 0, length)))
                     .Where(interval => interval.Item2 - interval.Item1 > 0)
                     .OrderBy(interval => interval.Item1))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        List<ElasticSegment> segments = [];
        double position = 0;
        foreach ((double start, double end) in merged)
        {
            AddBlank(segments, position, start);
            segments.Add(new ElasticSegment(start, end - start, ElasticSegmentKind.Covered));
            position = end;
        }

        AddBlank(segments, position, length);
        return segments;
    }

    private static void AddBlank(List<ElasticSegment> segments, double start, double end)
    {
        double blank = end - start;
        if (blank <= 0)
        {
            return;
        }

        segments.Add(new ElasticSegment(
            start,
            blank,
            blank <= SpacingThresholdDips ? ElasticSegmentKind.Spacing : ElasticSegmentKind.Open));
    }

    private static double Grow(
        List<ElasticSegment> segments,
        double target,
        double covered,
        double open,
        double spacing)
    {
        if (covered + open <= 0)
        {
            double uniform = target / Math.Max(1, spacing);
            segments.ForEach(segment => segment.TargetLength = segment.ReferenceLength * uniform);
            return uniform;
        }

        double scale = Math.Max(0, (target - spacing) / (covered + open));
        foreach (ElasticSegment segment in segments)
        {
            segment.TargetLength = segment.Kind == ElasticSegmentKind.Spacing
                ? segment.ReferenceLength
                : segment.ReferenceLength * scale;
        }

        return scale;
    }

    private static double Shrink(
        List<ElasticSegment> segments,
        double reduction,
        double covered,
        double spacing)
    {
        segments.ForEach(segment => segment.TargetLength = segment.ReferenceLength);
        List<ElasticSegment> openSegments = segments
            .Where(segment => segment.Kind == ElasticSegmentKind.Open)
            .ToList();

        double surplus = openSegments.Sum(segment => segment.ReferenceLength - SpacingThresholdDips);
        if (reduction <= surplus)
        {
            double fraction = reduction / surplus;
            openSegments.ForEach(segment => segment.TargetLength =
                segment.ReferenceLength - (segment.ReferenceLength - SpacingThresholdDips) * fraction);
            return 1;
        }

        openSegments.ForEach(segment => segment.TargetLength = SpacingThresholdDips);
        double remaining = reduction - Math.Max(0, surplus);

        double widgetScale = 1;
        if (covered > 0)
        {
            widgetScale = Math.Max(MinimumShrinkScale, (covered - remaining) / covered);
            foreach (ElasticSegment segment in segments.Where(segment => segment.Kind == ElasticSegmentKind.Covered))
            {
                segment.TargetLength = segment.ReferenceLength * widgetScale;
            }

            remaining -= covered * (1 - widgetScale);
        }

        remaining = Compress(openSegments, remaining);
        remaining = Compress(
            segments.Where(segment => segment.Kind == ElasticSegmentKind.Spacing).ToList(),
            remaining);
        if (remaining > 0 && covered > 0)
        {
            // All blank space is already gone and the preferred floor still
            // does not fit: keep shrinking covered segments until the plan
            // exactly fills the axis so realized rectangles cannot overlap
            // (spec 5.2 step 4).
            widgetScale = Math.Max(0.01, (covered * widgetScale - remaining) / covered);
            foreach (ElasticSegment segment in segments.Where(
                         segment => segment.Kind == ElasticSegmentKind.Covered))
            {
                segment.TargetLength = segment.ReferenceLength * widgetScale;
            }
        }

        return widgetScale;
    }

    private static double Compress(List<ElasticSegment> segments, double remaining)
    {
        double available = segments.Sum(segment => segment.TargetLength);
        if (remaining <= 0 || available <= 0)
        {
            return Math.Max(0, remaining);
        }

        double cut = Math.Min(remaining, available);
        double keep = (available - cut) / available;
        segments.ForEach(segment => segment.TargetLength *= keep);
        return remaining - cut;
    }
}

internal enum ElasticSegmentKind
{
    Covered,
    Spacing,
    Open
}

internal sealed class ElasticSegment(double referenceStart, double referenceLength, ElasticSegmentKind kind)
{
    public double ReferenceStart { get; } = referenceStart;

    public double ReferenceLength { get; } = referenceLength;

    public ElasticSegmentKind Kind { get; } = kind;

    public double TargetStart { get; set; }

    public double TargetLength { get; set; }
}

internal sealed class ElasticAxisPlan
{
    private readonly IReadOnlyList<ElasticSegment> _segments;
    private readonly double _targetEnd;

    internal ElasticAxisPlan(
        double referenceLength,
        double targetLength,
        IReadOnlyList<ElasticSegment> segments,
        double widgetScale)
    {
        ReferenceLength = referenceLength;
        TargetLength = targetLength;
        _segments = segments;
        WidgetScale = widgetScale > 0 && double.IsFinite(widgetScale) ? widgetScale : 1;
        double position = 0;
        foreach (ElasticSegment segment in segments)
        {
            segment.TargetStart = position;
            position += segment.TargetLength;
        }

        _targetEnd = position;
    }

    public double ReferenceLength { get; }

    public double TargetLength { get; }

    public double WidgetScale { get; }

    public bool IsIdentity => _segments.Count == 0;

    internal static ElasticAxisPlan Identity(double referenceLength, double targetLength) =>
        new(referenceLength, targetLength, [], 1);

    public double Map(double value)
    {
        if (IsIdentity)
        {
            return value;
        }

        if (value <= 0)
        {
            return value * WidgetScale;
        }

        foreach (ElasticSegment segment in _segments)
        {
            if (value <= segment.ReferenceStart + segment.ReferenceLength)
            {
                double offset = value - segment.ReferenceStart;
                return segment.TargetStart + offset * (segment.TargetLength / segment.ReferenceLength);
            }
        }

        return _targetEnd + (value - ReferenceLength) * WidgetScale;
    }

    public double Unmap(double value)
    {
        if (IsIdentity)
        {
            return value;
        }

        if (value <= 0)
        {
            return value / WidgetScale;
        }

        foreach (ElasticSegment segment in _segments)
        {
            if (value <= segment.TargetStart + segment.TargetLength)
            {
                return segment.TargetLength <= 0
                    ? segment.ReferenceStart
                    : segment.ReferenceStart +
                      (value - segment.TargetStart) * (segment.ReferenceLength / segment.TargetLength);
            }
        }

        return ReferenceLength + (value - _targetEnd) / WidgetScale;
    }

    public (double Start, double End) MapItem(double start, double end, bool anchorFar, double maxGrowScale)
    {
        if (IsIdentity)
        {
            return (start, end);
        }

        double mappedStart = Map(start);
        double mappedEnd = Map(end);
        double length = mappedEnd - mappedStart;
        double referenceLength = end - start;
        if (referenceLength > 0 && length > referenceLength * maxGrowScale)
        {
            length = referenceLength * maxGrowScale;
        }

        return anchorFar
            ? (mappedEnd - length, mappedEnd)
            : (mappedStart, mappedStart + length);
    }

    public (double Start, double End) UnmapItem(double start, double end, bool anchorFar, double maxGrowScale)
    {
        if (IsIdentity)
        {
            return (start, end);
        }

        double itemScale = WidgetScale > 1 ? Math.Min(WidgetScale, maxGrowScale) : WidgetScale;
        double length = (end - start) / itemScale;
        double edge = Unmap(anchorFar ? end : start);
        return anchorFar ? (edge - length, edge) : (edge, edge + length);
    }
}

internal readonly record struct ElasticRect(double Left, double Top, double Right, double Bottom);

internal sealed class ElasticDisplayPlan
{
    private ElasticDisplayPlan(ElasticAxisPlan x, ElasticAxisPlan y, double adaptiveMinimumWidth, double adaptiveMinimumHeight)
    {
        X = x;
        Y = y;
        AdaptiveMinimumWidth = adaptiveMinimumWidth;
        AdaptiveMinimumHeight = adaptiveMinimumHeight;
    }

    public ElasticAxisPlan X { get; }

    public ElasticAxisPlan Y { get; }

    /// <summary>
    /// Minimum projected width/height for the projection clamp on this
    /// display: at or below every item's mapped length, so clamping never
    /// inflates a widget past its neighbour (spec 5.2 rule 4, no-overlap
    /// under any scale).
    /// </summary>
    public double AdaptiveMinimumWidth { get; }

    public double AdaptiveMinimumHeight { get; }

    public double FrameWidth => X.ReferenceLength;

    public double FrameHeight => Y.ReferenceLength;

    public double TargetWidth => X.TargetLength;

    public double TargetHeight => Y.TargetLength;

    public static ElasticDisplayPlan Create(
        double frameWidth,
        double frameHeight,
        double targetWidth,
        double targetHeight,
        IReadOnlyCollection<ElasticRect> items,
        double preferredMinimumLength = 50,
        bool allowGrow = true)
    {
        ElasticAxisPlan x = WidgetElasticLayout.PlanAxis(
            frameWidth, targetWidth, items.Select(item => (item.Left, item.Right)), allowGrow);
        ElasticAxisPlan y = WidgetElasticLayout.PlanAxis(
            frameHeight, targetHeight, items.Select(item => (item.Top, item.Bottom)), allowGrow);
        double minAuthoredWidth = items.Count == 0
            ? double.PositiveInfinity
            : items.Min(item => item.Right - item.Left);
        double minAuthoredHeight = items.Count == 0
            ? double.PositiveInfinity
            : items.Min(item => item.Bottom - item.Top);
        return new(
            x,
            y,
            WidgetElasticLayout.AdaptiveMinimumLength(preferredMinimumLength, x.WidgetScale, minAuthoredWidth),
            WidgetElasticLayout.AdaptiveMinimumLength(preferredMinimumLength, y.WidgetScale, minAuthoredHeight));
    }
}
