using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetCompactInteractionPolicyTests
{
    [Fact]
    public void CanHoverExpand_OnlyAllowsUnblockedSmartContentHover()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsPointerInside = true,
            IsExpansionZoneActive = true
        };

        Assert.True(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Click,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsExpansionZoneActive = false }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverMoveHandle = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerOverActions = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanHoverExpand(
            WidgetCollapseBehavior.Smart,
            snapshot with { SuppressHoverExpansion = true }));
    }

    [Fact]
    public void CanAutoCollapse_RequiresPointerAndInteractionToBeClear()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.True(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPointerInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { InteractionDepth = 1 }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsDropInside = true }));
        Assert.False(WidgetCompactInteractionPolicy.CanAutoCollapse(
            WidgetCollapseBehavior.Smart,
            snapshot with { IsPinned = true }));
    }

    [Theory]
    [InlineData(true, false, false, "Glance")]
    [InlineData(false, false, false, "Peek")]
    [InlineData(false, true, false, "Pinned")]
    [InlineData(false, false, true, "Open")]
    public void ResolveViewState_MapsSmartInteractionToFourUserStates(
        bool collapsed,
        bool pinned,
        bool interacting,
        string expected)
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = collapsed,
            IsPinned = pinned,
            InteractionDepth = interacting ? 1 : 0
        };

        Assert.Equal(
            Enum.Parse<WidgetCompactViewState>(expected),
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Smart,
                snapshot));
    }

    [Fact]
    public void ResolveViewState_ClickModeUsesOpenState()
    {
        WidgetCompactInteractionSnapshot snapshot = CollapsedSnapshot() with
        {
            IsCollapsed = false
        };

        Assert.Equal(
            WidgetCompactViewState.Open,
            WidgetCompactInteractionPolicy.ResolveViewState(
                WidgetCollapseBehavior.Click,
                snapshot));
    }

    private static WidgetCompactInteractionSnapshot CollapsedSnapshot() => new(
        IsCollapsed: true,
        IsPinned: false,
        IsPointerInside: false,
        IsExpansionZoneActive: false,
        IsPointerOverMoveHandle: false,
        IsPointerOverActions: false,
        IsDropInside: false,
        IsBoundsInteractionActive: false,
        InteractionDepth: 0,
        IsDragging: false,
        IsResizing: false,
        HasBlockingSurface: false,
        SuppressHoverExpansion: false);
}
