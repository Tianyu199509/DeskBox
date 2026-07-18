using Windows.Graphics;

namespace DeskBox.Services;

public enum WidgetCompactExpansionAnchor
{
    LeftTop,
    RightTop,
    LeftBottom,
    RightBottom
}

public readonly record struct WidgetCompactExpansionLayout(
    WidgetCompactExpansionAnchor Anchor,
    PointInt32 Pivot,
    RectInt32 ExpandedBounds,
    SizeInt32 RequestedSize,
    bool IsSizeConstrained);

public static class WidgetCompactExpansionCalculator
{
    private static readonly WidgetCompactExpansionAnchor[] DefaultAnchorOrder =
    [
        WidgetCompactExpansionAnchor.LeftTop,
        WidgetCompactExpansionAnchor.RightTop,
        WidgetCompactExpansionAnchor.LeftBottom,
        WidgetCompactExpansionAnchor.RightBottom
    ];

    public static WidgetCompactExpansionLayout Resolve(
        RectInt32 compactBounds,
        SizeInt32 requestedSize,
        RectInt32 workArea,
        IReadOnlyList<WidgetCompactExpansionAnchor>? anchorOrder = null)
    {
        int originalRequestedWidth = Math.Max(1, requestedSize.Width);
        int originalRequestedHeight = Math.Max(1, requestedSize.Height);
        int requestedWidth = Math.Min(
            originalRequestedWidth,
            Math.Max(1, workArea.Width));
        int requestedHeight = Math.Min(
            originalRequestedHeight,
            Math.Max(1, workArea.Height));
        var normalizedRequestedSize = new SizeInt32(requestedWidth, requestedHeight);
        IReadOnlyList<WidgetCompactExpansionAnchor> candidates =
            anchorOrder is { Count: > 0 }
                ? anchorOrder.Distinct().ToArray()
                : DefaultAnchorOrder;

        Candidate? best = null;
        for (int index = 0; index < candidates.Count; index++)
        {
            Candidate candidate = CreateCandidate(
                compactBounds,
                normalizedRequestedSize,
                workArea,
                candidates[index],
                index);
            if (best is null || candidate.IsBetterThan(best.Value))
            {
                best = candidate;
            }
        }

        Candidate resolved = best ?? CreateCandidate(
            compactBounds,
            normalizedRequestedSize,
            workArea,
            WidgetCompactExpansionAnchor.LeftTop,
            0);
        return new WidgetCompactExpansionLayout(
            resolved.Anchor,
            resolved.Pivot,
            resolved.Bounds,
            new SizeInt32(originalRequestedWidth, originalRequestedHeight),
            resolved.Bounds.Width != originalRequestedWidth ||
                resolved.Bounds.Height != originalRequestedHeight);
    }

    public static RectInt32 CreateBoundsFromPivot(
        PointInt32 pivot,
        SizeInt32 size,
        WidgetCompactExpansionAnchor anchor)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        bool anchorsRight = anchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom;
        bool anchorsBottom = anchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        return new RectInt32(
            anchorsRight ? pivot.X - width : pivot.X,
            anchorsBottom ? pivot.Y - height : pivot.Y,
            width,
            height);
    }

    public static PointInt32 GetPivot(
        RectInt32 bounds,
        WidgetCompactExpansionAnchor anchor)
    {
        bool anchorsRight = anchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom;
        bool anchorsBottom = anchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        return new PointInt32(
            anchorsRight ? bounds.X + bounds.Width : bounds.X,
            anchorsBottom ? bounds.Y + bounds.Height : bounds.Y);
    }

    public static RectInt32 InterpolateAnchoredBounds(
        RectInt32 from,
        RectInt32 to,
        PointInt32 pivot,
        WidgetCompactExpansionAnchor anchor,
        double progress)
    {
        double value = Math.Clamp(progress, 0, 1);
        int width = Math.Max(1, Lerp(from.Width, to.Width, value));
        int height = Math.Max(1, Lerp(from.Height, to.Height, value));
        return CreateBoundsFromPivot(pivot, new SizeInt32(width, height), anchor);
    }

    public static WidgetCompactExpansionAnchor? FromPositionAnchor(string? anchor)
    {
        return anchor switch
        {
            WidgetPositionAnchors.LeftTop => WidgetCompactExpansionAnchor.LeftTop,
            WidgetPositionAnchors.RightTop => WidgetCompactExpansionAnchor.RightTop,
            WidgetPositionAnchors.LeftBottom => WidgetCompactExpansionAnchor.LeftBottom,
            WidgetPositionAnchors.RightBottom => WidgetCompactExpansionAnchor.RightBottom,
            _ => null
        };
    }

    public static WidgetCompactExpansionAnchor ResolveHorizontalResizeAnchor(
        WidgetCompactExpansionAnchor currentAnchor,
        string? resizeDirection)
    {
        bool anchorsBottom = currentAnchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        return resizeDirection switch
        {
            "Right" => anchorsBottom
                ? WidgetCompactExpansionAnchor.LeftBottom
                : WidgetCompactExpansionAnchor.LeftTop,
            "Left" => anchorsBottom
                ? WidgetCompactExpansionAnchor.RightBottom
                : WidgetCompactExpansionAnchor.RightTop,
            _ => currentAnchor
        };
    }

    private static Candidate CreateCandidate(
        RectInt32 compactBounds,
        SizeInt32 requestedSize,
        RectInt32 workArea,
        WidgetCompactExpansionAnchor anchor,
        int preferenceIndex)
    {
        PointInt32 pivot = GetPivot(compactBounds, anchor);
        int workRight = workArea.X + Math.Max(1, workArea.Width);
        int workBottom = workArea.Y + Math.Max(1, workArea.Height);
        bool anchorsRight = anchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom;
        bool anchorsBottom = anchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        int availableWidth = anchorsRight
            ? pivot.X - workArea.X
            : workRight - pivot.X;
        int availableHeight = anchorsBottom
            ? pivot.Y - workArea.Y
            : workBottom - pivot.Y;
        int width = Math.Max(1, Math.Min(requestedSize.Width, availableWidth));
        int height = Math.Max(1, Math.Min(requestedSize.Height, availableHeight));
        RectInt32 bounds = CreateBoundsFromPivot(
            pivot,
            new SizeInt32(width, height),
            anchor);
        bool fitsRequestedSize = width == requestedSize.Width && height == requestedSize.Height;
        long retainedArea = (long)width * height;
        return new Candidate(
            anchor,
            pivot,
            bounds,
            fitsRequestedSize,
            retainedArea,
            preferenceIndex);
    }

    private static int Lerp(int from, int to, double progress) =>
        (int)Math.Round(from + ((to - from) * progress));

    private readonly record struct Candidate(
        WidgetCompactExpansionAnchor Anchor,
        PointInt32 Pivot,
        RectInt32 Bounds,
        bool FitsRequestedSize,
        long RetainedArea,
        int PreferenceIndex)
    {
        public bool IsBetterThan(Candidate other)
        {
            if (FitsRequestedSize != other.FitsRequestedSize)
            {
                return FitsRequestedSize;
            }

            if (!FitsRequestedSize && RetainedArea != other.RetainedArea)
            {
                return RetainedArea > other.RetainedArea;
            }

            return PreferenceIndex < other.PreferenceIndex;
        }
    }
}
