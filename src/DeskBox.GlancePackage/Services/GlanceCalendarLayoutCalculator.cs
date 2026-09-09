namespace DeskBox.Services;

/// <summary>
/// Owned by DeskBox.GlancePackage since D3 Phase 2 (copied from
/// src/DeskBox/Services/GlanceCalendarLayoutCalculator.cs).
/// </summary>
internal static class GlanceCalendarLayoutCalculator
{
    private const double CompactCalendarThreshold = 320;

    public static bool IsCompact(double availableHeight) =>
        availableHeight < CompactCalendarThreshold;

    public static double CalculatePanelHeight(
        double availableHeight,
        bool isCompact,
        bool hasTraditionalCalendar)
    {
        _ = hasTraditionalCalendar;
        if (isCompact) return Math.Clamp(availableHeight - 40, 238, 268);
        return Math.Clamp(availableHeight - 102, 244, 310);
    }

    public static double CalculateDayHeight(
        double panelHeight,
        bool isCompact,
        bool hasTraditionalCalendar)
    {
        _ = hasTraditionalCalendar;
        double fixedContentHeight = isCompact ? 104 : 58;
        return Math.Clamp((panelHeight - fixedContentHeight) / 6, 24, 42);
    }

    public static bool ShouldShowTraditionalDetails(
        double panelWidth,
        double dayHeight,
        bool isCompact,
        bool hasTraditionalCalendar) =>
        hasTraditionalCalendar && !isCompact && panelWidth >= 280 && dayHeight >= 28;
}
