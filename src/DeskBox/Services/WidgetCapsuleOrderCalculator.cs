using Windows.Graphics;

namespace DeskBox.Services;

public static class WidgetCapsuleOrderCalculator
{
    public static IReadOnlyList<string> MoveToNearestSlot(
        IReadOnlyList<string> orderedIds,
        IReadOnlyList<RectInt32> slotBounds,
        string activeId,
        RectInt32 proposedBounds,
        string? direction)
    {
        int activeIndex = IndexOf(orderedIds, activeId);
        if (activeIndex < 0 || orderedIds.Count < 2)
        {
            return orderedIds.ToArray();
        }

        bool vertical = SettingsService.NormalizeWidgetCapsuleBarDirection(direction) ==
            SettingsService.WidgetCapsuleBarDirectionVertical;
        long proposedCenter = GetPrimaryCenter(proposedBounds, vertical);
        int nearestIndex = activeIndex;
        long nearestDistance = long.MaxValue;
        int slotCount = Math.Min(orderedIds.Count, slotBounds.Count);
        for (int index = 0; index < slotCount; index++)
        {
            RectInt32 slot = slotBounds[index];
            long distance = Math.Abs(proposedCenter - GetPrimaryCenter(slot, vertical));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = index;
            }
        }

        if (nearestIndex == activeIndex)
        {
            return orderedIds.ToArray();
        }

        var reordered = orderedIds.ToList();
        reordered.RemoveAt(activeIndex);
        reordered.Insert(nearestIndex, activeId);
        return reordered;
    }

    public static IReadOnlyList<string> MergeGroupOrder(
        IReadOnlyList<string> completeOrder,
        IReadOnlyList<string> groupOrder)
    {
        var groupIds = groupOrder.ToHashSet(StringComparer.Ordinal);
        var result = completeOrder.ToArray();
        int groupIndex = 0;
        for (int index = 0; index < result.Length && groupIndex < groupOrder.Count; index++)
        {
            if (groupIds.Contains(result[index]))
            {
                result[index] = groupOrder[groupIndex++];
            }
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static long GetPrimaryCenter(RectInt32 bounds, bool vertical) => vertical
        ? (long)bounds.Y * 2 + bounds.Height
        : (long)bounds.X * 2 + bounds.Width;
}
