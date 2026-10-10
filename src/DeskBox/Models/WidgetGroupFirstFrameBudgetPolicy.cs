namespace DeskBox.Models;

/// <summary>
/// Adaptive first-frame budget for widget-group surface transactions
/// (feedback 497: switching into a file-heavy group member timed out the old
/// fixed 900ms gate deterministically — five "first-frame wait timed out;
/// timeout-rollback" hits — because committing thousands of file tiles keeps
/// the UI thread busy past any fixed, content-blind deadline).
///
/// The gate still rolls back on timeout: its purpose is to prevent committing
/// the group identity onto a member that never presented a frame (a blank
/// flash). Only the budget becomes a function of how much content the target
/// member must render: the legacy 900ms floor for ordinary members, a
/// per-item allowance for file surfaces, and a gentle ceiling so a genuinely
/// dead surface cannot stall the switch gate unboundedly.
/// </summary>
public static class WidgetGroupFirstFrameBudgetPolicy
{
    /// <summary>
    /// Floor of the curve — the historical fixed gate. Ordinary members and
    /// empty file surfaces keep exactly the old behavior.
    /// </summary>
    public const int BaseMilliseconds = 900;

    /// <summary>
    /// Additional allowance per rendered item of the incoming member. Covers
    /// the XAML layout plus stack-projection work that scales with the item
    /// list, without which the floor would keep failing file-heavy members.
    /// </summary>
    public const int PerItemMilliseconds = 2;

    /// <summary>
    /// Ceiling of the curve. Bounds how long one switch may hold the surface
    /// switch gate when rendering never completes.
    /// </summary>
    public const int CapMilliseconds = 4000;

    /// <summary>
    /// Budget curve: <c>min(Cap, Base + PerItem × max(0, projectedItemCount))</c>
    /// with the item count pre-clamped so huge counts cannot overflow.
    /// </summary>
    public static TimeSpan ResolveBudget(int projectedItemCount)
    {
        long clampedItems = Math.Clamp(
            (long)projectedItemCount,
            0,
            (CapMilliseconds - BaseMilliseconds) / PerItemMilliseconds);
        long budget = BaseMilliseconds + PerItemMilliseconds * clampedItems;
        return TimeSpan.FromMilliseconds(Math.Min(budget, CapMilliseconds));
    }
}
