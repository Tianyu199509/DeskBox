namespace DeskBox.Services;

internal readonly record struct WidgetCompactWarmupSnapshot(
    bool IsCollapseInitialized,
    bool IsCollapsed,
    bool IsExpansionWarmed,
    bool IsClosing,
    bool IsAnimationActive,
    bool IsPointerOverWidget,
    bool HasActiveInteraction,
    bool IsWindowVisible,
    bool IsContentReady,
    bool IsApplicationIdle);

internal static class WidgetCompactWarmupPolicy
{
    public static bool IsExpansionReady(
        bool isWarmed,
        long warmedEpoch,
        long memoryCleanupEpoch)
    {
        return isWarmed && warmedEpoch == memoryCleanupEpoch;
    }

    public static bool CanRun(WidgetCompactWarmupSnapshot snapshot)
    {
        return snapshot.IsCollapseInitialized &&
            snapshot.IsCollapsed &&
            !snapshot.IsExpansionWarmed &&
            !snapshot.IsClosing &&
            !snapshot.IsAnimationActive &&
            !snapshot.IsPointerOverWidget &&
            !snapshot.HasActiveInteraction &&
            snapshot.IsWindowVisible &&
            snapshot.IsContentReady &&
            snapshot.IsApplicationIdle;
    }

    /// <summary>
    /// Whether a deferred warm-up run can still become runnable by waiting.
    /// Every other gate clears on its own (idle arrives, the pointer leaves,
    /// content finishes loading); a hidden window only becomes runnable
    /// through the show path, which re-arms an urgent run on its own, so
    /// retrying while hidden only burns a dispatcher timer and floods the
    /// performance log.
    /// </summary>
    public static bool ShouldKeepWaiting(WidgetCompactWarmupSnapshot snapshot)
    {
        return snapshot.IsWindowVisible;
    }
}
