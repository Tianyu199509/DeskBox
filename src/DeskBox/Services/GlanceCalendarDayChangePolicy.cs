namespace DeskBox.Services;

public readonly record struct GlanceCalendarDayChangeDecision(
    bool CalendarRefreshRequired,
    DateOnly DisplayedMonth)
{
    public bool DisplayedMonthChanged(DateOnly currentMonth) =>
        CalendarRefreshRequired && DisplayedMonth != currentMonth;
}

/// <summary>
/// Pure cross-day decision for the glance calendar. Shared by the visible
/// midnight tick and the hidden-across-midnight re-show path so both refresh
/// the same way. The date arguments are the fake-clock seam for unit tests.
/// </summary>
public static class GlanceCalendarDayChangePolicy
{
    public static GlanceCalendarDayChangeDecision Evaluate(
        DateOnly previousDate,
        DateOnly currentDate,
        DateOnly displayedMonth)
    {
        if (previousDate == currentDate)
        {
            return new GlanceCalendarDayChangeDecision(false, displayedMonth);
        }

        DateOnly previousMonth = new(previousDate.Year, previousDate.Month, 1);
        DateOnly currentMonth = new(currentDate.Year, currentDate.Month, 1);
        // Follow the calendar into the new month only when the user was
        // browsing the month the day change left; any other browsed month is
        // user intent and must not be reset by the tick.
        if (displayedMonth == previousMonth && previousMonth != currentMonth)
        {
            return new GlanceCalendarDayChangeDecision(true, currentMonth);
        }

        return new GlanceCalendarDayChangeDecision(true, displayedMonth);
    }
}
