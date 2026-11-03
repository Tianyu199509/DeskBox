using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Boundary and single-monitor containment rules for widget slide animations on
/// multi-monitor setups. Ensures neither short-throw slides (<c>SlideFade</c>)
/// nor bezel-drawer slides (<c>ScreenEdgeSlide</c>) ever push an HWND pixel
/// across a seam onto an adjacent monitor.
/// </summary>
public static class WidgetSlideBoundaryPolicy
{
    private const double AdjacencyTolerancePx = 8.0;
    private const double MinSlideTravelPx = 1.0;
    private const double TopologyCacheTtlMs = 500.0;

    internal readonly record struct CachedDisplayEntry(
        RectInt32 OuterBounds,
        RectInt32 WorkArea,
        AdjacentDisplaySides AdjacentSides);

    private static CachedDisplayEntry[]? s_cachedDisplays;
    private static long s_cachedDisplaysTimestamp;

    internal static bool TryResolveDisplayForPoint(
        PointInt32 point,
        out RectInt32 workArea,
        out RectInt32 outerBounds,
        out AdjacentDisplaySides adjacentSides)
    {
        CachedDisplayEntry[] entries = GetOrRefreshDisplaySnapshot();
        if (entries.Length > 0)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                RectInt32 bounds = entries[i].OuterBounds;
                if (point.X >= bounds.X &&
                    point.X < bounds.X + bounds.Width &&
                    point.Y >= bounds.Y &&
                    point.Y < bounds.Y + bounds.Height)
                {
                    workArea = entries[i].WorkArea;
                    outerBounds = bounds;
                    adjacentSides = entries[i].AdjacentSides;
                    return true;
                }
            }

            workArea = entries[0].WorkArea;
            outerBounds = entries[0].OuterBounds;
            adjacentSides = entries[0].AdjacentSides;
            return true;
        }

        workArea = default;
        outerBounds = default;
        adjacentSides = default;
        return false;
    }

    private static CachedDisplayEntry[] GetOrRefreshDisplaySnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        if (s_cachedDisplays is { Length: > 0 } cached &&
            s_cachedDisplaysTimestamp != 0 &&
            Stopwatch.GetElapsedTime(s_cachedDisplaysTimestamp, now).TotalMilliseconds < TopologyCacheTtlMs)
        {
            return cached;
        }

        try
        {
            IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
            int count = areas.Count;
            if (count <= 0)
            {
                return [];
            }

            var outerList = new RectInt32[count];
            var workList = new RectInt32[count];
            for (int i = 0; i < count; i++)
            {
                DisplayArea area = areas[i];
                outerList[i] = area.OuterBounds;
                workList[i] = area.WorkArea;
            }

            var snapshot = new CachedDisplayEntry[count];
            for (int i = 0; i < count; i++)
            {
                RectInt32 current = outerList[i];
                bool left = false;
                bool right = false;
                bool up = false;
                bool down = false;
                for (int j = 0; j < count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    RectInt32 other = outerList[j];
                    left |= IsAdjacentOnSide(current, other, SettingsService.WidgetAnimationSlideDirectionLeft);
                    right |= IsAdjacentOnSide(current, other, SettingsService.WidgetAnimationSlideDirectionRight);
                    up |= IsAdjacentOnSide(current, other, SettingsService.WidgetAnimationSlideDirectionUp);
                    down |= IsAdjacentOnSide(current, other, SettingsService.WidgetAnimationSlideDirectionDown);
                }

                snapshot[i] = new CachedDisplayEntry(
                    current,
                    workList[i],
                    new AdjacentDisplaySides(left, right, up, down));
            }

            s_cachedDisplays = snapshot;
            s_cachedDisplaysTimestamp = now;
            return snapshot;
        }
        catch (Exception ex)
        {
            App.Log($"[SlideBoundary] Display topology probe failed: {ex.Message}");
            return [];
        }
    }

    public readonly record struct CrossingFadeDecision(
        double StartOffset,
        double EndOffset,
        bool UseCrossingFade);

    public readonly record struct AdjacentDisplaySides(
        bool Left,
        bool Right,
        bool Up,
        bool Down)
    {
        public bool HasAdjacentOn(string direction) => direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => Left,
            SettingsService.WidgetAnimationSlideDirectionRight => Right,
            SettingsService.WidgetAnimationSlideDirectionUp => Up,
            SettingsService.WidgetAnimationSlideDirectionDown => Down,
            _ => false
        };
    }

    public readonly record struct SlideMotionPlan(
        string ResolvedDirection,
        double OffsetX,
        double OffsetY,
        bool UseShortFadeFallback);

    /// <summary>
    /// Resolves a short-throw drift offset (<c>SlideFade</c>) strictly contained
    /// within <paramref name="outerBounds"/> whenever an adjacent monitor touches
    /// the slide side, so 0 pixels ever cross onto the neighbor display.
    /// </summary>
    public static (double OffsetX, double OffsetY) ResolveShortSlideOffset(
        string direction,
        Windows.Foundation.Rect restingBounds,
        RectInt32 outerBounds,
        AdjacentDisplaySides adjacentSides,
        double desiredThrowPx)
    {
        string normalizedDirection = direction is
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight or
            SettingsService.WidgetAnimationSlideDirectionUp or
            SettingsService.WidgetAnimationSlideDirectionDown
                ? direction
                : SettingsService.WidgetAnimationSlideDirectionDown;

        double travel = Math.Max(0, desiredThrowPx);
        if (adjacentSides.HasAdjacentOn(normalizedDirection))
        {
            double distanceToSeam = GetDistanceToOuterEdge(
                normalizedDirection,
                restingBounds,
                outerBounds);
            travel = Math.Min(travel, distanceToSeam);
        }

        return ToDirectionalVector(normalizedDirection, travel);
    }

    /// <summary>
    /// Resolves a drawer-style bezel slide (<c>ScreenEdgeSlide</c>).
    /// When <paramref name="requestedDirection"/> is <c>"Auto"</c>, chooses the
    /// nearest physical outer bezel that has no adjacent display. If the chosen
    /// direction is blocked by an adjacent display (either forced by the user
    /// or on a fully surrounded center display), falls back to a seam-clamped
    /// short-throw fade inside the current monitor so 0 pixels cross the seam.
    /// </summary>
    public static SlideMotionPlan ResolveScreenEdgeSlide(
        string requestedDirection,
        Windows.Foundation.Rect restingBounds,
        RectInt32 workArea,
        RectInt32 outerBounds,
        AdjacentDisplaySides adjacentSides,
        double offscreenPaddingPx = 16.0,
        double fallbackShortThrowPx = 48.0)
    {
        ReadOnlySpan<string> candidates =
        [
            SettingsService.WidgetAnimationSlideDirectionLeft,
            SettingsService.WidgetAnimationSlideDirectionRight,
            SettingsService.WidgetAnimationSlideDirectionUp,
            SettingsService.WidgetAnimationSlideDirectionDown
        ];

        bool isAuto = requestedDirection is not (
            SettingsService.WidgetAnimationSlideDirectionLeft or
            SettingsService.WidgetAnimationSlideDirectionRight or
            SettingsService.WidgetAnimationSlideDirectionUp or
            SettingsService.WidgetAnimationSlideDirectionDown);

        if (isAuto)
        {
            string? bestBezelDirection = null;
            double bestBezelGap = double.MaxValue;
            string nearestAnyDirection = SettingsService.WidgetAnimationSlideDirectionDown;
            double nearestAnyGap = double.MaxValue;

            foreach (string candidate in candidates)
            {
                double gap = GetGapToWorkAreaEdge(candidate, restingBounds, workArea);
                if (gap < nearestAnyGap)
                {
                    nearestAnyGap = gap;
                    nearestAnyDirection = candidate;
                }

                if (!adjacentSides.HasAdjacentOn(candidate) && gap < bestBezelGap)
                {
                    bestBezelGap = gap;
                    bestBezelDirection = candidate;
                }
            }

            if (bestBezelDirection is not null)
            {
                double offscreenTravel = GetOffscreenBezelTravel(
                    bestBezelDirection,
                    restingBounds,
                    outerBounds,
                    offscreenPaddingPx);
                var (ox, oy) = ToDirectionalVector(bestBezelDirection, offscreenTravel);
                return new SlideMotionPlan(bestBezelDirection, ox, oy, UseShortFadeFallback: false);
            }

            var (fx, fy) = ResolveShortSlideOffset(
                nearestAnyDirection,
                restingBounds,
                outerBounds,
                adjacentSides,
                fallbackShortThrowPx);
            return new SlideMotionPlan(nearestAnyDirection, fx, fy, UseShortFadeFallback: true);
        }

        if (!adjacentSides.HasAdjacentOn(requestedDirection))
        {
            double offscreenTravel = GetOffscreenBezelTravel(
                requestedDirection,
                restingBounds,
                outerBounds,
                offscreenPaddingPx);
            var (ox, oy) = ToDirectionalVector(requestedDirection, offscreenTravel);
            return new SlideMotionPlan(requestedDirection, ox, oy, UseShortFadeFallback: false);
        }

        var (clampedX, clampedY) = ResolveShortSlideOffset(
            requestedDirection,
            restingBounds,
            outerBounds,
            adjacentSides,
            fallbackShortThrowPx);
        return new SlideMotionPlan(requestedDirection, clampedX, clampedY, UseShortFadeFallback: true);
    }

    public static AdjacentDisplaySides ProbeAdjacentDisplaySides(RectInt32 currentOuterBounds)
    {
        CachedDisplayEntry[] entries = GetOrRefreshDisplaySnapshot();
        for (int i = 0; i < entries.Length; i++)
        {
            RectInt32 bounds = entries[i].OuterBounds;
            if (bounds.X == currentOuterBounds.X &&
                bounds.Y == currentOuterBounds.Y &&
                bounds.Width == currentOuterBounds.Width &&
                bounds.Height == currentOuterBounds.Height)
            {
                return entries[i].AdjacentSides;
            }
        }

        bool left = false;
        bool right = false;
        bool up = false;
        bool down = false;
        for (int i = 0; i < entries.Length; i++)
        {
            RectInt32 bounds = entries[i].OuterBounds;
            left |= IsAdjacentOnSide(currentOuterBounds, bounds, SettingsService.WidgetAnimationSlideDirectionLeft);
            right |= IsAdjacentOnSide(currentOuterBounds, bounds, SettingsService.WidgetAnimationSlideDirectionRight);
            up |= IsAdjacentOnSide(currentOuterBounds, bounds, SettingsService.WidgetAnimationSlideDirectionUp);
            down |= IsAdjacentOnSide(currentOuterBounds, bounds, SettingsService.WidgetAnimationSlideDirectionDown);
        }

        return new AdjacentDisplaySides(left, right, up, down);
    }

    public static CrossingFadeDecision ResolveCrossingFade(
        double unconfinedTravel,
        double farEdge,
        double nearEdge,
        double workAreaEdge,
        bool hasAdjacentDisplay)
    {
        if (!hasAdjacentDisplay || unconfinedTravel <= 0)
        {
            return new CrossingFadeDecision(0, 0, UseCrossingFade: false);
        }

        double start = Math.Max(0, Math.Abs(workAreaEdge - farEdge));
        double end = Math.Max(start, Math.Abs(workAreaEdge - nearEdge));
        end = Math.Min(end, unconfinedTravel);
        start = Math.Min(start, end);

        return new CrossingFadeDecision(start, end, UseCrossingFade: true);
    }

    public static bool HasAdjacentDisplayBeyondEdge(
        RectInt32 currentOuterBounds,
        string slideDirection)
    {
        return ProbeAdjacentDisplaySides(currentOuterBounds).HasAdjacentOn(slideDirection);
    }

    public static bool IsAdjacentOnSide(
        RectInt32 currentOuterBounds,
        RectInt32 candidateOuterBounds,
        string slideDirection)
    {
        int currentLeft = currentOuterBounds.X;
        int currentTop = currentOuterBounds.Y;
        int currentRight = currentOuterBounds.X + currentOuterBounds.Width;
        int currentBottom = currentOuterBounds.Y + currentOuterBounds.Height;

        int boundsLeft = candidateOuterBounds.X;
        int boundsTop = candidateOuterBounds.Y;
        int boundsRight = candidateOuterBounds.X + candidateOuterBounds.Width;
        int boundsBottom = candidateOuterBounds.Y + candidateOuterBounds.Height;

        bool overlapsVertically =
            boundsBottom > currentTop &&
            boundsTop < currentBottom;
        bool overlapsHorizontally =
            boundsRight > currentLeft &&
            boundsLeft < currentRight;

        return slideDirection switch
        {
            SettingsService.WidgetAnimationSlideDirectionRight =>
                overlapsVertically &&
                Math.Abs(boundsLeft - currentRight) <= AdjacencyTolerancePx,
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                overlapsVertically &&
                Math.Abs(currentLeft - boundsRight) <= AdjacencyTolerancePx,
            SettingsService.WidgetAnimationSlideDirectionUp =>
                overlapsHorizontally &&
                Math.Abs(currentTop - boundsBottom) <= AdjacencyTolerancePx,
            SettingsService.WidgetAnimationSlideDirectionDown =>
                overlapsHorizontally &&
                Math.Abs(currentBottom - boundsTop) <= AdjacencyTolerancePx,
            _ => false
        };
    }

    private static double GetDistanceToOuterEdge(
        string direction,
        Windows.Foundation.Rect restingBounds,
        RectInt32 outerBounds)
    {
        double width = Math.Max(MinSlideTravelPx, restingBounds.Width);
        double height = Math.Max(MinSlideTravelPx, restingBounds.Height);
        return direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                Math.Max(0, restingBounds.X - outerBounds.X),
            SettingsService.WidgetAnimationSlideDirectionRight =>
                Math.Max(0, (outerBounds.X + outerBounds.Width) - (restingBounds.X + width)),
            SettingsService.WidgetAnimationSlideDirectionUp =>
                Math.Max(0, restingBounds.Y - outerBounds.Y),
            SettingsService.WidgetAnimationSlideDirectionDown =>
                Math.Max(0, (outerBounds.Y + outerBounds.Height) - (restingBounds.Y + height)),
            _ => 0
        };
    }

    private static double GetGapToWorkAreaEdge(
        string direction,
        Windows.Foundation.Rect restingBounds,
        RectInt32 workArea)
    {
        double width = Math.Max(MinSlideTravelPx, restingBounds.Width);
        double height = Math.Max(MinSlideTravelPx, restingBounds.Height);
        return direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                Math.Max(0, restingBounds.X - workArea.X),
            SettingsService.WidgetAnimationSlideDirectionRight =>
                Math.Max(0, (workArea.X + workArea.Width) - (restingBounds.X + width)),
            SettingsService.WidgetAnimationSlideDirectionUp =>
                Math.Max(0, restingBounds.Y - workArea.Y),
            SettingsService.WidgetAnimationSlideDirectionDown =>
                Math.Max(0, (workArea.Y + workArea.Height) - (restingBounds.Y + height)),
            _ => double.MaxValue
        };
    }

    private static double GetOffscreenBezelTravel(
        string direction,
        Windows.Foundation.Rect restingBounds,
        RectInt32 outerBounds,
        double offscreenPaddingPx)
    {
        double width = Math.Max(MinSlideTravelPx, restingBounds.Width);
        double height = Math.Max(MinSlideTravelPx, restingBounds.Height);
        double padding = Math.Max(0, offscreenPaddingPx);
        return direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft =>
                Math.Max(MinSlideTravelPx, (restingBounds.X + width) - outerBounds.X + padding),
            SettingsService.WidgetAnimationSlideDirectionRight =>
                Math.Max(MinSlideTravelPx, (outerBounds.X + outerBounds.Width) - restingBounds.X + padding),
            SettingsService.WidgetAnimationSlideDirectionUp =>
                Math.Max(MinSlideTravelPx, (restingBounds.Y + height) - outerBounds.Y + padding),
            SettingsService.WidgetAnimationSlideDirectionDown =>
                Math.Max(MinSlideTravelPx, (outerBounds.Y + outerBounds.Height) - restingBounds.Y + padding),
            _ => MinSlideTravelPx
        };
    }

    private static (double OffsetX, double OffsetY) ToDirectionalVector(
        string direction,
        double distance)
    {
        return direction switch
        {
            SettingsService.WidgetAnimationSlideDirectionLeft => (-distance, 0),
            SettingsService.WidgetAnimationSlideDirectionRight => (distance, 0),
            SettingsService.WidgetAnimationSlideDirectionUp => (0, -distance),
            _ => (0, distance)
        };
    }
}
