using DeskBox.Services;

namespace DeskBox.Models;

/// <summary>
/// Frozen layout decision for the shell title bar during a compact bounds
/// transition. The title bar is laid out once at the transition's stable
/// width (the expanded target while expanding, the expanded origin while
/// collapsing) and revealed or clipped by the growing window instead of
/// re-wrapping at every intermediate width.
/// </summary>
public readonly record struct WidgetTitleBarTransitionFreeze(
    bool ShouldFreeze,
    double Width,
    bool AlignRight);

public static class WidgetTitleBarTransitionFreezePolicy
{
    /// <summary>
    /// Overlay chrome has no title bar, and a hidden or zero-sized title has
    /// nothing worth freezing. Right-anchored expansions keep the title's
    /// fixed edge against the window edge that does not move.
    /// </summary>
    public static WidgetTitleBarTransitionFreeze Evaluate(
        bool isOverlayChromeMode,
        bool isTitleBarVisible,
        double stableWindowWidth,
        WidgetCompactExpansionAnchor expansionAnchor)
    {
        bool alignRight = expansionAnchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom;
        bool shouldFreeze = !isOverlayChromeMode &&
            isTitleBarVisible &&
            double.IsFinite(stableWindowWidth) &&
            stableWindowWidth > 0;
        return new WidgetTitleBarTransitionFreeze(
            shouldFreeze,
            shouldFreeze ? stableWindowWidth : 0,
            alignRight);
    }
}
