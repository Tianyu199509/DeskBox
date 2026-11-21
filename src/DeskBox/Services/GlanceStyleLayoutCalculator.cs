using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure size-adaptation rules for the Glance styles: which style actually
/// renders at a given size and how much detail each one can afford. Mirrors
/// the widget-family idea (S/M/L) without exposing size knobs to the user.
/// </summary>
internal static class GlanceStyleLayoutCalculator
{
    /// <summary>The month grid needs at least this much room; below it the week strip renders.</summary>
    internal const double MonthCalendarMinimumWidth = 236;
    internal const double MonthCalendarMinimumHeight = 200;

    public static GlanceStyle ResolveEffectiveStyle(
        GlanceStyle style,
        double availableWidth,
        double availableHeight)
    {
        if (style == GlanceStyle.MonthCalendar &&
            (availableWidth < MonthCalendarMinimumWidth || availableHeight < MonthCalendarMinimumHeight))
        {
            return GlanceStyle.WeekStrip;
        }

        return Enum.IsDefined(style) ? style : GlanceStyle.PhotoClock;
    }

    public static double CalculateTodayCardDayFontSize(
        double availableWidth,
        double availableHeight,
        double scale)
    {
        double size = Math.Clamp(
            Math.Min(availableWidth * 0.30, availableHeight * 0.36),
            44,
            112);
        return Math.Round(size * NormalizeScale(scale) * 2) / 2;
    }

    /// <summary>How many agenda rows the today card can show (0 hides the list).</summary>
    public static int CalculateTodayCardAgendaCapacity(double availableWidth, double availableHeight)
    {
        if (availableWidth < 200 || availableHeight < 210)
        {
            return 0;
        }

        if (availableHeight < 260)
        {
            return 1;
        }

        if (availableHeight < 320)
        {
            return 2;
        }

        return availableHeight < 400 ? 3 : 5;
    }

    /// <summary>The week strip adds a one-line agenda/countdown when tall enough.</summary>
    public static bool WeekStripShowsAgendaLine(double availableHeight) => availableHeight >= 190;

    /// <summary>The week strip shows traditional-calendar text under the day numbers when roomy.</summary>
    public static bool WeekStripShowsSecondaryText(double availableWidth, double availableHeight) =>
        availableWidth >= 300 && availableHeight >= 150;

    public static double CalculateWeekStripTimeFontSize(
        double availableWidth,
        double availableHeight,
        double scale,
        bool includeSeconds = false)
    {
        double widthFactor = includeSeconds ? 0.088 : 0.12;
        double minSize = includeSeconds ? 20 : 24;
        double maxSize = includeSeconds ? 38 : 48;
        double size = Math.Clamp(
            Math.Min(availableWidth * widthFactor, availableHeight * 0.2),
            minSize,
            maxSize);
        return Math.Round(size * NormalizeScale(scale) * 2) / 2;
    }

    private static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, 0.75, 1.35) : 1;
}
