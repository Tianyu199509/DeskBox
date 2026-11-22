using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Projects the mainland-China statutory holiday table
/// (<see cref="CustomWidgetHolidaySchedule"/>) onto Glance calendar days and
/// builds the "next holiday" countdown. The table only describes the PRC
/// arrangement, so it applies to Simplified-Chinese cultures only.
/// </summary>
public static class GlanceHolidayMarkService
{
    public static bool IsApplicable(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        string name = culture.Name;
        return name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase);
    }

    public static GlanceHolidayMark GetMark(DateOnly date) =>
        CustomWidgetHolidaySchedule.GetDayType(date) switch
        {
            CustomWidgetHolidayDayType.StatutoryHoliday => GlanceHolidayMark.Off,
            CustomWidgetHolidayDayType.SwapWorkday => GlanceHolidayMark.Work,
            _ => GlanceHolidayMark.None
        };

    public static GlanceCalendarMonth Apply(
        GlanceCalendarMonth month,
        bool enabled,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(month);
        if (!enabled || !IsApplicable(culture))
        {
            return month;
        }

        var days = new GlanceCalendarDay[month.Days.Count];
        for (int index = 0; index < days.Length; index++)
        {
            GlanceCalendarDay day = month.Days[index];
            GlanceHolidayMark mark = GetMark(day.Date);
            string holidayName = mark == GlanceHolidayMark.Off &&
                CustomWidgetHolidaySchedule.TryGetHolidayAt(day.Date, out CustomWidgetHolidayEntry entry)
                    ? CustomWidgetHolidaySchedule.GetLocalizedName(entry, culture.Name)
                    : string.Empty;
            days[index] = day with
            {
                HolidayMark = mark,
                HolidayName = holidayName
            };
        }

        return month with { Days = days };
    }

    /// <summary>
    /// Countdown state relative to <paramref name="today"/>: inside a holiday
    /// block, or the number of days until the next one. Null beyond the
    /// table's coverage.
    /// </summary>
    public static GlanceHolidayCountdown? GetCountdown(DateOnly today, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!CustomWidgetHolidaySchedule.TryGetNextHoliday(today, out CustomWidgetHolidayEntry entry))
        {
            return null;
        }

        string name = CustomWidgetHolidaySchedule.GetLocalizedName(entry, culture.Name);
        if (today >= entry.Start)
        {
            return new GlanceHolidayCountdown(name, 0, today.DayNumber - entry.Start.DayNumber + 1);
        }

        return new GlanceHolidayCountdown(name, entry.Start.DayNumber - today.DayNumber, 0);
    }
}

/// <param name="HolidayName">Localized holiday name.</param>
/// <param name="DaysUntil">Days until the block starts; zero while inside it.</param>
/// <param name="DayOfBreak">1-based day inside the block; zero before it starts.</param>
public sealed record GlanceHolidayCountdown(string HolidayName, int DaysUntil, int DayOfBreak)
{
    public bool IsInProgress => DayOfBreak > 0;
}
