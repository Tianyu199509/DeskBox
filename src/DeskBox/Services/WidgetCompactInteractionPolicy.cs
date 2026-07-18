namespace DeskBox.Services;

internal enum WidgetCompactViewState
{
    Glance,
    Peek,
    Open,
    Pinned
}

internal readonly record struct WidgetCompactInteractionSnapshot(
    bool IsCollapsed,
    bool IsPinned,
    bool IsPointerInside,
    bool IsExpansionZoneActive,
    bool IsPointerOverMoveHandle,
    bool IsPointerOverActions,
    bool IsDropInside,
    bool IsBoundsInteractionActive,
    int InteractionDepth,
    bool IsDragging,
    bool IsResizing,
    bool HasBlockingSurface,
    bool SuppressHoverExpansion)
{
    public bool HasActiveInteraction =>
        IsDropInside ||
        IsBoundsInteractionActive ||
        InteractionDepth > 0 ||
        IsDragging ||
        IsResizing ||
        HasBlockingSurface;
}

internal static class WidgetCompactInteractionPolicy
{
    public static bool CanHoverExpand(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        return behavior == WidgetCollapseBehavior.Smart &&
            snapshot.IsCollapsed &&
            snapshot.IsPointerInside &&
            snapshot.IsExpansionZoneActive &&
            !snapshot.IsPointerOverMoveHandle &&
            !snapshot.IsPointerOverActions &&
            !snapshot.IsDropInside &&
            !snapshot.HasActiveInteraction &&
            !snapshot.SuppressHoverExpansion;
    }

    public static bool CanAutoCollapse(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        return behavior == WidgetCollapseBehavior.Smart &&
            !snapshot.IsCollapsed &&
            !snapshot.IsPinned &&
            !snapshot.IsPointerInside &&
            !snapshot.HasActiveInteraction;
    }

    public static WidgetCompactViewState ResolveViewState(
        WidgetCollapseBehavior behavior,
        WidgetCompactInteractionSnapshot snapshot)
    {
        if (snapshot.IsCollapsed)
        {
            return WidgetCompactViewState.Glance;
        }

        if (snapshot.IsPinned)
        {
            return WidgetCompactViewState.Pinned;
        }

        if (behavior != WidgetCollapseBehavior.Smart || snapshot.HasActiveInteraction)
        {
            return WidgetCompactViewState.Open;
        }

        return WidgetCompactViewState.Peek;
    }
}
