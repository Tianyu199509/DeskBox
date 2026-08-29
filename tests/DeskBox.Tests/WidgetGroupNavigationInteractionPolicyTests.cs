using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetGroupNavigationInteractionPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Auto")]
    [InlineData("invalid")]
    public void RemovedAutoAndInvalidStyles_ResolveToStack(string? style)
    {
        Assert.Equal(
            WidgetGroupNavigationStyles.Stack,
            WidgetGroupNavigationInteractionPolicy.ResolveEffectiveStyle(
                style,
                memberCount: 3,
                availableWidth: 500));
    }

    [Theory]
    [InlineData(WidgetGroupNavigationStyles.Tabs)]
    [InlineData(WidgetGroupNavigationStyles.Stack)]
    public void ExplicitStyle_DoesNotChangeAtResponsiveBreakpoint(string style)
    {
        Assert.Equal(
            style,
            WidgetGroupNavigationInteractionPolicy.ResolveEffectiveStyle(
                style,
                memberCount: 8,
                availableWidth: 100));
    }

    [Theory]
    [InlineData(1, 6, false)]
    [InlineData(20, 20, false)]
    [InlineData(4, 7, true)]
    [InlineData(8, 10, true)]
    public void DirectionLock_RejectsShortOrHorizontalIntent(
        double deltaX,
        double deltaY,
        bool expected)
    {
        Assert.Equal(
            expected,
            WidgetGroupNavigationInteractionPolicy.ShouldLockVertical(
                deltaX,
                deltaY));
    }

    [Fact]
    public void Gesture_UsesDistanceOrVelocityAndHonorsCancellation()
    {
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.ShouldCommitGesture(
                cancelled: false,
                directionLocked: true,
                deltaY: 56,
                TimeSpan.FromSeconds(1)));
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.ShouldCommitGesture(
                cancelled: false,
                directionLocked: true,
                deltaY: 30,
                TimeSpan.FromMilliseconds(40)));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy.ShouldCommitGesture(
                cancelled: true,
                directionLocked: true,
                deltaY: 100,
                TimeSpan.FromMilliseconds(10)));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy.ShouldCommitGesture(
                cancelled: false,
                directionLocked: false,
                deltaY: 100,
                TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void RelativeNavigation_DoesNotWrapAtEitherEdge()
    {
        Assert.False(
            WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex: 0,
                memberCount: 3,
                delta: -1,
                out _));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex: 2,
                memberCount: 3,
                delta: 1,
                out _));
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex: 1,
                memberCount: 3,
                delta: 1,
                out int target));
        Assert.Equal(2, target);
    }

    [Fact]
    public void RelativeNavigation_CanWrapWhenKeyboardNavigationRequestsIt()
    {
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex: 2,
                memberCount: 3,
                delta: 1,
                out int nextTarget,
                wrap: true));
        Assert.Equal(0, nextTarget);

        Assert.True(
            WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex: 0,
                memberCount: 3,
                delta: -1,
                out int previousTarget,
                wrap: true));
        Assert.Equal(2, previousTarget);
    }

    [Fact]
    public void EdgeDamping_OnlyAppliesWhenMovingPastAnEnd()
    {
        Assert.Equal(
            35,
            WidgetGroupNavigationInteractionPolicy.ApplyEdgeDamping(
                100,
                activeIndex: 0,
                memberCount: 3));
        Assert.Equal(
            -35,
            WidgetGroupNavigationInteractionPolicy.ApplyEdgeDamping(
                -100,
                activeIndex: 2,
                memberCount: 3));
        Assert.Equal(
            100,
            WidgetGroupNavigationInteractionPolicy.ApplyEdgeDamping(
                100,
                activeIndex: 1,
                memberCount: 3));
    }

    [Fact]
    public void Wheel_AccumulatesUntilOneDeterministicStep()
    {
        double accumulator = 0;

        Assert.False(
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref accumulator,
                -40,
                out _));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref accumulator,
                -40,
                out _));
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref accumulator,
                -40,
                out int direction));

        Assert.Equal(1, direction);
        Assert.Equal(0, accumulator);
    }

    [Fact]
    public void Wheel_DirectionReversalStartsANewGesture()
    {
        double accumulator = -80;

        Assert.False(
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref accumulator,
                40,
                out _));
        Assert.Equal(40, accumulator);
        Assert.True(
            WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref accumulator,
                80,
                out int direction));

        Assert.Equal(-1, direction);
        Assert.Equal(0, accumulator);
    }

    [Fact]
    public void WheelGesture_ContinuousBurstRemainsOneGestureUntilQuiet()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset lastObservedAt = default;
        int lastObservedDirection = 0;

        Assert.True(
            WidgetGroupNavigationInteractionPolicy
                .ObserveWheelGesture(
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    startedAt,
                    direction: 1));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy
                .ObserveWheelGesture(
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    startedAt.AddMilliseconds(150),
                    direction: 1));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy
                .ObserveWheelGesture(
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    startedAt.AddMilliseconds(300),
                    direction: 1));
        Assert.Equal(startedAt.AddMilliseconds(300), lastObservedAt);

        Assert.True(
            WidgetGroupNavigationInteractionPolicy
                .ObserveWheelGesture(
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    startedAt.AddMilliseconds(520),
                    direction: 1));
    }

    [Fact]
    public void WheelGesture_LargeRepeatedDeltasCommitOnlyOnePageUntilQuiet()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        double accumulator = 0;
        DateTimeOffset lastObservedAt = default;
        int lastObservedDirection = 0;
        bool gestureCommitted = false;

        Assert.True(
            WidgetGroupNavigationInteractionPolicy
                .TryConsumeWheelGestureStep(
                    ref accumulator,
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    ref gestureCommitted,
                    wheelDelta: -480,
                    startedAt,
                    out int firstDirection));
        Assert.Equal(1, firstDirection);
        Assert.Equal(0, accumulator);

        Assert.False(
            WidgetGroupNavigationInteractionPolicy
                .TryConsumeWheelGestureStep(
                    ref accumulator,
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    ref gestureCommitted,
                    wheelDelta: -480,
                    startedAt.AddMilliseconds(150),
                    out _));
        Assert.False(
            WidgetGroupNavigationInteractionPolicy
                .TryConsumeWheelGestureStep(
                    ref accumulator,
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    ref gestureCommitted,
                    wheelDelta: -480,
                    startedAt.AddMilliseconds(300),
                    out _));

        Assert.True(
            WidgetGroupNavigationInteractionPolicy
                .TryConsumeWheelGestureStep(
                    ref accumulator,
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    ref gestureCommitted,
                    wheelDelta: -480,
                    startedAt.AddMilliseconds(520),
                    out int nextGestureDirection));
        Assert.Equal(1, nextGestureDirection);
    }

    [Fact]
    public void WheelGesture_UsesTheDocumentedQuietPeriod()
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(220),
            WidgetGroupNavigationInteractionPolicy
                .WheelGestureQuietPeriod);
    }

    [Fact]
    public void WheelGesture_DirectionReversalStartsANewGestureImmediately()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset lastObservedAt = startedAt;
        int lastObservedDirection = 1;

        Assert.True(
            WidgetGroupNavigationInteractionPolicy
                .ObserveWheelGesture(
                    ref lastObservedAt,
                    ref lastObservedDirection,
                    startedAt.AddMilliseconds(20),
                    direction: -1));
        Assert.Equal(-1, lastObservedDirection);
    }

    [Fact]
    public void PositionRail_MapsTwoAndThreeMembersOneToOne()
    {
        IReadOnlyList<WidgetGroupPositionRailSlot> two =
            WidgetGroupNavigationInteractionPolicy
                .ResolvePositionRailSlots(activeIndex: 1, memberCount: 2);
        IReadOnlyList<WidgetGroupPositionRailSlot> three =
            WidgetGroupNavigationInteractionPolicy
                .ResolvePositionRailSlots(activeIndex: 1, memberCount: 3);

        Assert.Equal([0, 1], two.Select(slot => slot.MemberIndex));
        Assert.False(two[0].IsActive);
        Assert.True(two[1].IsActive);
        Assert.Equal([0, 1, 2], three.Select(slot => slot.MemberIndex));
        Assert.True(three[1].IsActive);
    }

    [Theory]
    [InlineData(0, 8, 0, 0)]
    [InlineData(4, 8, 3, 1)]
    [InlineData(7, 8, 5, 2)]
    public void PositionRail_UsesAThreeSlotRollingWindow(
        int activeIndex,
        int memberCount,
        int expectedFirstIndex,
        int expectedActiveSlot)
    {
        IReadOnlyList<WidgetGroupPositionRailSlot> slots =
            WidgetGroupNavigationInteractionPolicy
                .ResolvePositionRailSlots(activeIndex, memberCount);

        Assert.Equal(3, slots.Count);
        Assert.Equal(expectedFirstIndex, slots[0].MemberIndex);
        Assert.Equal(
            expectedActiveSlot,
            slots.ToList().FindIndex(slot => slot.IsActive));
    }

    [Fact]
    public void PositionRail_HidesWhenThereIsNoGroup()
    {
        Assert.Empty(
            WidgetGroupNavigationInteractionPolicy
                .ResolvePositionRailSlots(activeIndex: 0, memberCount: 1));
    }
}
