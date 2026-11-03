using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure sizing rules for the Glance month calendar. The grid draws only the
/// weeks the displayed month actually occupies (4–6) instead of the fixed six
/// rows inherited from the native CalendarView, and degrades through three
/// tiers as the widget shrinks: clock outside the panel, clock inside the
/// panel header, calendar only.
/// </summary>
internal static class GlanceCalendarLayoutCalculator
{
    private const double CompactCalendarThreshold = 320;
    private const double MinimalCalendarThreshold = 260;

    /// <summary>Header row (month + navigation) plus weekday labels and padding.</summary>
    private const double GridChromeHeight = 58;
    /// <summary>Additional strip that hosts the clock inside a compact panel.</summary>
    private const double CompactClockStripHeight = 46;
    private const double MinimumDayHeight = 20;
    private const double MaximumDayHeight = 42;

    public const int DefaultWeekRows = 6;

    public static bool IsCompact(double availableHeight) =>
        availableHeight < CompactCalendarThreshold;

    /// <summary>
    /// Below this height even the compact clock strip no longer fits; the panel
    /// shows the calendar grid alone.
    /// </summary>
    public static bool IsMinimal(double availableHeight) =>
        availableHeight < MinimalCalendarThreshold;

    public static double CalculatePanelHeight(
        double availableHeight,
        bool isCompact,
        bool hasTraditionalCalendar,
        int weekRows = DefaultWeekRows)
    {
        _ = hasTraditionalCalendar;
        int rows = NormalizeRows(weekRows);
        bool showClockStrip = isCompact && !IsMinimal(availableHeight);
        double chrome = GetChromeHeight(isCompact, availableHeight);

        double available = isCompact
            // Compact calendars keep the clock inside the material surface and
            // may use almost all of the widget height.
            ? availableHeight - (showClockStrip ? 40 : 30)
            // Above the compact breakpoint the clock sits outside the surface.
            : availableHeight - 102;
        double minimum = chrome + rows * (showClockStrip ? 22 : MinimumDayHeight);
        double maximum = chrome + rows * MaximumDayHeight;
        return Math.Round(Math.Clamp(available, minimum, Math.Max(minimum, maximum)));
    }

    public static double CalculateDayHeight(
        double panelHeight,
        bool isCompact,
        bool hasTraditionalCalendar,
        int weekRows = DefaultWeekRows,
        double availableHeight = double.PositiveInfinity)
    {
        _ = hasTraditionalCalendar;
        int rows = NormalizeRows(weekRows);
        double chrome = GetChromeHeight(isCompact, availableHeight);
        return Math.Clamp(
            (panelHeight - chrome) / rows,
            MinimumDayHeight,
            MaximumDayHeight);
    }

    public static bool ShouldShowTraditionalDetails(
        double panelWidth,
        double dayHeight,
        bool isCompact,
        bool hasTraditionalCalendar) =>
        hasTraditionalCalendar &&
        panelWidth >= 260 &&
        dayHeight >= (isCompact ? 30 : 28);

    /// <summary>
    /// Number of week rows the month occupies in a grid that starts on the
    /// culture's first day of week (4 for a February starting on that day, 6
    /// for long months that start late in the week).
    /// </summary>
    public static int CountWeekRows(IReadOnlyList<GlanceCalendarDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        int firstCurrentIndex = -1;
        int lastCurrentIndex = -1;
        for (int index = 0; index < days.Count; index++)
        {
            if (!days[index].IsCurrentMonth)
            {
                continue;
            }

            if (firstCurrentIndex < 0)
            {
                firstCurrentIndex = index;
            }

            lastCurrentIndex = index;
        }

        if (firstCurrentIndex < 0)
        {
            return DefaultWeekRows;
        }

        return NormalizeRows(lastCurrentIndex / 7 - firstCurrentIndex / 7 + 1);
    }

    /// <summary>Index of the first grid row that contains a current-month day.</summary>
    public static int GetFirstWeekRow(IReadOnlyList<GlanceCalendarDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        for (int index = 0; index < days.Count; index++)
        {
            if (days[index].IsCurrentMonth)
            {
                return index / 7;
            }
        }

        return 0;
    }

    private static double GetChromeHeight(bool isCompact, double availableHeight) =>
        isCompact && !IsMinimal(availableHeight)
            ? GridChromeHeight + CompactClockStripHeight
            : GridChromeHeight;

    private static int NormalizeRows(int rows) => Math.Clamp(rows, 4, 6);
}
