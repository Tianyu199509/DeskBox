namespace DeskBox.Models;

public readonly record struct WidgetGroupPositionRailSlot(
    int MemberIndex,
    bool IsActive);

/// <summary>
/// Pure decision rules shared by mouse, touch, pen, precision touchpad and
/// keyboard navigation. Keeping these rules free of XAML makes the gesture
/// boundary and keyboard wrap behavior directly testable.
/// </summary>
public static class WidgetGroupNavigationInteractionPolicy
{
    public const double DirectionLockDistance = 7;
    public const double GestureCommitDistance = 56;
    public const double GestureCommitVelocity = 520;
    public const double WheelStep = 120;
    public static readonly TimeSpan WheelGestureQuietPeriod =
        TimeSpan.FromMilliseconds(220);

    public static string ResolveEffectiveStyle(
        string? requestedStyle,
        int memberCount,
        double availableWidth)
    {
        string requested = WidgetGroupNavigationStyles.Normalize(
            requestedStyle,
            allowFollowDefault: false);
        if (requested != WidgetGroupNavigationStyles.Auto)
        {
            return requested;
        }

        return memberCount <= 3 && availableWidth >= 240
            ? WidgetGroupNavigationStyles.Tabs
            : WidgetGroupNavigationStyles.Stack;
    }

    public static bool ShouldLockVertical(double deltaX, double deltaY)
    {
        return Math.Abs(deltaY) >= DirectionLockDistance &&
               Math.Abs(deltaY) > Math.Abs(deltaX) * 1.2;
    }

    public static double ApplyEdgeDamping(
        double deltaY,
        int activeIndex,
        int memberCount)
    {
        bool beyondStart = activeIndex == 0 && deltaY > 0;
        bool beyondEnd = activeIndex == memberCount - 1 && deltaY < 0;
        return beyondStart || beyondEnd ? deltaY * 0.35 : deltaY;
    }

    public static bool ShouldCommitGesture(
        bool cancelled,
        bool directionLocked,
        double deltaY,
        TimeSpan elapsed)
    {
        double seconds = Math.Max(0.001, elapsed.TotalSeconds);
        double velocity = deltaY / seconds;
        return !cancelled &&
               directionLocked &&
               (Math.Abs(deltaY) >= GestureCommitDistance ||
                Math.Abs(velocity) >= GestureCommitVelocity);
    }

    public static bool TryResolveRelativeTarget(
        int activeIndex,
        int memberCount,
        int delta,
        out int targetIndex,
        bool wrap = false)
    {
        targetIndex = -1;
        if (delta == 0 ||
            memberCount <= 0 ||
            activeIndex < 0 ||
            activeIndex >= memberCount)
        {
            return false;
        }

        targetIndex = activeIndex + Math.Sign(delta);
        if (targetIndex >= 0 && targetIndex < memberCount)
        {
            return true;
        }

        if (!wrap)
        {
            return false;
        }

        targetIndex = targetIndex < 0 ? memberCount - 1 : 0;
        return true;
    }

    public static bool TryConsumeWheelStep(
        ref double accumulator,
        double wheelDelta,
        out int direction)
    {
        // Precision touchpads can emit a small counter-delta when inertia
        // settles or the user reverses direction. Start a fresh gesture when
        // the sign changes so stale input cannot swallow the new intent.
        if (accumulator != 0 &&
            Math.Sign(accumulator) != Math.Sign(wheelDelta))
        {
            accumulator = 0;
        }

        accumulator += wheelDelta;
        if (Math.Abs(accumulator) < WheelStep)
        {
            direction = 0;
            return false;
        }

        direction = accumulator < 0 ? 1 : -1;
        accumulator = 0;
        return true;
    }

    /// <summary>
    /// Observes effective wheel input and identifies gesture boundaries. A
    /// same-direction burst remains one gesture until input has been quiet for
    /// long enough; reversing direction always starts a new explicit gesture.
    /// </summary>
    public static bool ObserveWheelGesture(
        ref DateTimeOffset lastObservedAt,
        ref int lastObservedDirection,
        DateTimeOffset observedAt,
        int direction)
    {
        if (direction is not (-1 or 1))
        {
            return false;
        }

        TimeSpan sinceObserved = observedAt - lastObservedAt;
        bool startsNewGesture = lastObservedAt == default ||
                                direction != lastObservedDirection ||
                                sinceObserved < TimeSpan.Zero ||
                                sinceObserved >= WheelGestureQuietPeriod;
        lastObservedAt = observedAt;
        lastObservedDirection = direction;
        return startsNewGesture;
    }

    /// <summary>
    /// Commits at most one page step for a continuous wheel gesture, regardless
    /// of the number or magnitude of same-direction deltas in that gesture.
    /// </summary>
    public static bool TryConsumeWheelGestureStep(
        ref double accumulator,
        ref DateTimeOffset lastObservedAt,
        ref int lastObservedDirection,
        ref bool gestureCommitted,
        double wheelDelta,
        DateTimeOffset observedAt,
        out int direction)
    {
        direction = 0;
        if (wheelDelta == 0)
        {
            return false;
        }

        int inputDirection = wheelDelta < 0 ? 1 : -1;
        if (ObserveWheelGesture(
                ref lastObservedAt,
                ref lastObservedDirection,
                observedAt,
                inputDirection))
        {
            accumulator = 0;
            gestureCommitted = false;
        }

        if (gestureCommitted ||
            !TryConsumeWheelStep(
                ref accumulator,
                wheelDelta,
                out direction))
        {
            direction = 0;
            return false;
        }

        gestureCommitted = true;
        return true;
    }

    /// <summary>
    /// Resolves the compact title-bar position rail. Two- and three-member
    /// groups map one-to-one; larger groups expose a rolling three-slot window
    /// so the active member is at the leading edge, center or trailing edge.
    /// </summary>
    public static IReadOnlyList<WidgetGroupPositionRailSlot>
        ResolvePositionRailSlots(int activeIndex, int memberCount)
    {
        if (memberCount < 2)
        {
            return Array.Empty<WidgetGroupPositionRailSlot>();
        }

        int resolvedActiveIndex = Math.Clamp(
            activeIndex,
            0,
            memberCount - 1);
        int visibleCount = Math.Min(3, memberCount);
        int startIndex = memberCount <= visibleCount
            ? 0
            : Math.Clamp(
                resolvedActiveIndex - 1,
                0,
                memberCount - visibleCount);
        var slots = new WidgetGroupPositionRailSlot[visibleCount];
        for (int slotIndex = 0; slotIndex < visibleCount; slotIndex++)
        {
            int memberIndex = startIndex + slotIndex;
            slots[slotIndex] = new WidgetGroupPositionRailSlot(
                memberIndex,
                memberIndex == resolvedActiveIndex);
        }

        return slots;
    }
}
