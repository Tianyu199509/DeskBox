using System.Numerics;

namespace DeskBox.Services;

/// <summary>
/// Decides when Win10 legacy-acrylic widget windows may drop the DWM blur
/// while moving. Frosted glass stays on by default (including during motion);
/// the tint-only simplification only kicks in after recent coordinator ticks
/// keep missing the frame budget, or when the user opted into ResourceSaver.
/// The decision is re-evaluated at every interaction start, so a machine that
/// recovers goes back to full acrylic.
/// </summary>
internal static class InteractionBackdropSimplificationPolicy
{
    // Bitmask window of the coordinator's recent ticks. Roughly 37% of the
    // last 64 ticks missing their frame budget means the DWM blur resample is
    // very likely part of the overload.
    public const int MinimumRecentOverrunTicks = 24;

    public static bool ShouldSimplify(long recentOverrunMask, string? performanceMode)
    {
        if (string.Equals(
                performanceMode,
                PerformanceSettingsPolicy.ModeResourceSaver,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return BitOperations.PopCount((ulong)recentOverrunMask) >= MinimumRecentOverrunTicks;
    }
}
