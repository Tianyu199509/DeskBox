namespace DeskBox.Services;

/// <summary>
/// Pure interaction rules shared by the search result pointer handlers and tests.
/// Keeping row hit-testing, range normalization, and edge scrolling here prevents
/// the visual layer from growing another set of subtly different selection rules.
/// </summary>
internal static class SearchResultSelectionPolicy
{
    internal static bool ShouldPreserveSelectionForDrag(
        bool itemIsSelected,
        int selectedItemCount,
        bool pointerIsOnDragHandle) =>
        itemIsSelected && selectedItemCount > 1 && pointerIsOnDragHandle;

    internal enum DragOutMoveDecision
    {
        // No armed candidate for this pointer (other pointer, no gesture, or
        // a drag already in flight): the move belongs to some other gesture.
        Ignore,

        // The candidate's button is no longer held: disarm instead of
        // starting a phantom drag on unpressured hover.
        Disarm,

        // Candidate armed, button held, threshold crossed: start the drag.
        Start
    }

    // Drag-out move gate for result rows. The button re-check exists because
    // a missed PointerReleased (released out-of-band, capture stolen) would
    // otherwise leave a stale candidate that the next unpressured hover move
    // converts into a drag start.
    internal static DragOutMoveDecision EvaluateDragOutMove(
        bool candidateArmed,
        bool samePointer,
        bool leftButtonPressed,
        double distanceUnits,
        double thresholdUnits = 8)
    {
        if (!candidateArmed || !samePointer)
        {
            return DragOutMoveDecision.Ignore;
        }

        if (!leftButtonPressed)
        {
            return DragOutMoveDecision.Disarm;
        }

        return distanceUnits >= thresholdUnits
            ? DragOutMoveDecision.Start
            : DragOutMoveDecision.Ignore;
    }

    internal static IReadOnlyList<T> ResolveDraggedItems<T>(
        T anchor,
        IReadOnlyList<T> selectedItems)
    {
        T[] distinctSelectedItems = selectedItems.Distinct().ToArray();
        return distinctSelectedItems.Length > 1 && distinctSelectedItems.Contains(anchor)
            ? distinctSelectedItems
            : [anchor];
    }

    internal static bool ShouldStartRubberBand(
        bool isLeftButtonPressed,
        bool isOverResultRow,
        bool isShiftPressed) =>
        isLeftButtonPressed && !isOverResultRow && !isShiftPressed;

    internal static (int Start, int End) GetRange(
        int anchorIndex,
        int targetIndex,
        int itemCount)
    {
        if (itemCount <= 0 ||
            anchorIndex < 0 || anchorIndex >= itemCount ||
            targetIndex < 0 || targetIndex >= itemCount)
        {
            return (-1, -1);
        }

        return (
            Math.Min(anchorIndex, targetIndex),
            Math.Max(anchorIndex, targetIndex));
    }

    internal static double GetAutoScrollDelta(
        double pointerY,
        double viewportHeight,
        double edgeSize = 32,
        double maximumDelta = 18)
    {
        if (viewportHeight <= 0 || edgeSize <= 0 || maximumDelta <= 0)
        {
            return 0;
        }

        double effectiveEdge = Math.Min(edgeSize, viewportHeight / 2);
        if (pointerY < effectiveEdge)
        {
            double intensity = Math.Clamp(
                (effectiveEdge - pointerY) / effectiveEdge,
                0,
                1);
            return -maximumDelta * intensity;
        }

        double lowerEdge = viewportHeight - effectiveEdge;
        if (pointerY > lowerEdge)
        {
            double intensity = Math.Clamp(
                (pointerY - lowerEdge) / effectiveEdge,
                0,
                1);
            return maximumDelta * intensity;
        }

        return 0;
    }
}
