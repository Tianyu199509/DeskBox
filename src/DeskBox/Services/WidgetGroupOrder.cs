namespace DeskBox.Services;

internal static class WidgetGroupOrder
{
    public static async Task<bool> MoveAndSaveAsync(
        IList<string> memberIds,
        string sourceWidgetId,
        string targetWidgetId,
        Func<Task<bool>> save)
    {
        string[] previousOrder = memberIds.ToArray();
        if (!MoveToTargetSlot(memberIds, sourceWidgetId, targetWidgetId))
        {
            return false;
        }

        bool saved = false;
        try
        {
            saved = await save();
            return saved;
        }
        finally
        {
            if (!saved)
            {
                memberIds.Clear();
                foreach (string memberId in previousOrder)
                {
                    memberIds.Add(memberId);
                }
            }
        }
    }

    /// <summary>
    /// Converts one native tab move into the original target-slot contract.
    /// Rejects interrupted drags and concurrent membership/order changes.
    /// </summary>
    public static bool TryResolveDragTarget(
        IReadOnlyList<string> originalOrder,
        IReadOnlyList<string> visualOrder,
        string sourceWidgetId,
        out string? targetWidgetId)
    {
        targetWidgetId = null;
        if (originalOrder.Count != visualOrder.Count ||
            originalOrder.Distinct(StringComparer.Ordinal).Count() != originalOrder.Count)
        {
            return false;
        }

        int targetIndex = -1;
        for (int index = 0; index < visualOrder.Count; index++)
        {
            if (visualOrder[index] == sourceWidgetId)
            {
                targetIndex = index;
                break;
            }
        }
        if (targetIndex < 0)
        {
            return false;
        }

        var expectedOrder = originalOrder.ToList();
        string targetId = originalOrder[targetIndex];
        if (!MoveToTargetSlot(expectedOrder, sourceWidgetId, targetId) ||
            !expectedOrder.SequenceEqual(visualOrder, StringComparer.Ordinal))
        {
            return false;
        }
        targetWidgetId = targetId;
        return true;
    }

    public static bool MoveToTargetSlot(
        IList<string> memberIds,
        string sourceWidgetId,
        string targetWidgetId)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        int sourceIndex = memberIds.IndexOf(sourceWidgetId);
        int targetIndex = memberIds.IndexOf(targetWidgetId);
        if (sourceIndex < 0 ||
            targetIndex < 0 ||
            sourceIndex == targetIndex)
        {
            return false;
        }

        memberIds.RemoveAt(sourceIndex);
        memberIds.Insert(
            Math.Clamp(targetIndex, 0, memberIds.Count),
            sourceWidgetId);
        return true;
    }
}
