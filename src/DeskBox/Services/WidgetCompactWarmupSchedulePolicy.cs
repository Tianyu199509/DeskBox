using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Keeps cold-start capsule realization away from the first application paint
/// while favoring the built-in surfaces that have the largest first-layout
/// cost. Pointer-driven work bypasses this schedule in WidgetWindowBase.
/// </summary>
internal static class WidgetCompactWarmupSchedulePolicy
{
    /// <summary>
    /// A hosted-content swap (group tab switch) invalidates the expansion
    /// warm-up, and the collapse that follows it usually means the user is
    /// about to hover right back in. Warming that collapse urgently (16 ms
    /// initial delay, High dispatcher priority, no application-idle gate)
    /// lets the new member's layout finish while the pointer travels back,
    /// instead of losing the dwell-window race to switch-settle animations
    /// on the shared frame clock and hitting the 320 ms live-layout
    /// fallback (2026-10-20 group tab-switch hover delay).
    /// </summary>
    public const double RecentHostedContentSwapMilliseconds = 10_000;

    public static bool ShouldWarmUrgentlyAfterHostedContentSwap(
        double millisecondsSinceSwap) =>
        millisecondsSinceSwap >= 0 &&
        millisecondsSinceSwap <= RecentHostedContentSwapMilliseconds;

    public static int GetInitialDelayMilliseconds(WidgetKind kind) => kind switch
    {
        WidgetKind.QuickCapture => 220,
        WidgetKind.Clipboard => 240,
        WidgetKind.Weather => 260,
        WidgetKind.Search => 300,
        WidgetKind.Music => 340,
        WidgetKind.Todo => 380,
        WidgetKind.File => 420,
        WidgetKind.SystemMonitor => 460,
        _ => 500
    };
}
