using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Boundary rules for widget slide-out animations on multi-monitor setups.
/// Sliding an HWND fully past the current monitor's work-area edge lands it on
/// the adjacent display, where it stays fully visible until the animation
/// completes. The travel distance itself is never clamped — the slide keeps
/// its original speed and reach — but when an adjacent display is detected
/// beyond the slide edge, opacity is driven per frame from the window's
/// actual position: it stays fully opaque while the subject is inside its own
/// monitor and fades across the span in which the subject crosses the
/// boundary, so the adjacent display only ever sees a dissolving sliver.
/// </summary>
public static class WidgetSlideBoundaryPolicy
{
    private const double AdjacencyTolerancePx = 8.0;

    public readonly record struct CrossingFadeDecision(
        double StartOffset,
        double EndOffset,
        bool UseCrossingFade);

    /// <summary>
    /// Resolves the displacement span over which the crossing fade runs.
    /// <paramref name="farEdge"/> is the subject edge that leads when sliding
    /// toward <paramref name="workAreaEdge"/> (right edge for a rightward
    /// slide); <paramref name="nearEdge"/> is the trailing edge. Fading starts
    /// when the leading edge touches the boundary and completes by the time
    /// the trailing edge clears it, clamped by <paramref name="unconfinedTravel"/>
    /// so the fade always finishes before the animation ends.
    /// </summary>
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

    /// <summary>
    /// True when another display's outer bounds touch the current monitor's
    /// boundary on the slide side with at least some perpendicular overlap.
    /// Uses outer (physical) bounds: two abutting monitors share that boundary
    /// even when a taskbar pulls their work areas apart.
    /// </summary>
    public static bool HasAdjacentDisplayBeyondEdge(
        RectInt32 currentOuterBounds,
        string slideDirection)
    {
        int currentLeft = currentOuterBounds.X;
        int currentTop = currentOuterBounds.Y;
        int currentRight = currentOuterBounds.X + currentOuterBounds.Width;
        int currentBottom = currentOuterBounds.Y + currentOuterBounds.Height;

        try
        {
            // Index through the IReadOnlyList projection (IVectorView.GetAt):
            // foreach would take CsWinRT's enumerator path, which QIs the
            // vector for IIterable<DisplayArea> and throws
            // InvalidCastException.
            IReadOnlyList<DisplayArea> areas = DisplayArea.FindAll();
            for (int index = 0; index < areas.Count; index++)
            {
                RectInt32 bounds = areas[index].OuterBounds;
                if (bounds.X == currentOuterBounds.X &&
                    bounds.Y == currentOuterBounds.Y &&
                    bounds.Width == currentOuterBounds.Width &&
                    bounds.Height == currentOuterBounds.Height)
                {
                    continue;
                }

                int boundsLeft = bounds.X;
                int boundsTop = bounds.Y;
                int boundsRight = bounds.X + bounds.Width;
                int boundsBottom = bounds.Y + bounds.Height;

                bool overlapsVertically =
                    boundsBottom > currentTop &&
                    boundsTop < currentBottom;
                bool overlapsHorizontally =
                    boundsRight > currentLeft &&
                    boundsLeft < currentRight;

                bool adjacent = slideDirection switch
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

                if (adjacent)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            // A failed topology probe must never break the animation pipeline;
            // fall back to the unconfined slide (no adjacent display).
            App.Log($"[SlideBoundary] Display topology probe failed: {ex.Message}");
        }

        return false;
    }
}
