using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Adds optional festival decorations to calendar days without coupling the
/// month grid to a particular account, sync engine, or event provider.
/// </summary>
internal sealed class GlanceFestivalService
{
    private readonly GlanceTraditionalCalendarService _traditionalCalendarService = new();

    public GlanceCalendarMonth Apply(
        GlanceCalendarMonth month,
        bool showChineseFestivals,
        GlanceTraditionalCalendarMode configuredMode,
        CultureInfo culture)
    {
        GlanceTraditionalCalendarMode mode = _traditionalCalendarService.ResolveMode(
            configuredMode,
            culture.Name);
        if (!showChineseFestivals || mode != GlanceTraditionalCalendarMode.ChineseLunar)
        {
            return month with
            {
                Days = month.Days
                    .Select(day => day with { FestivalText = string.Empty })
                    .ToList()
            };
        }

        bool useTraditional = LocalizationService.IsTraditionalChineseCulture(culture.Name);
        return month with
        {
            Days = month.Days
                .Select(day => day with
                {
                    FestivalText = GetChineseFestival(day.Date, useTraditional)
                })
                .ToList()
        };
    }

    internal string GetChineseFestival(DateOnly date, bool useTraditional = false)
    {
        try
        {
            if (date.Month == 4 && date.Day == GetQingmingDay(date.Year))
            {
                return Localize("清明", useTraditional);
            }

            ChineseFestivalDate value = GetChineseDate(date);
            if (!value.IsLeapMonth)
            {
                string fixedFestival = (value.Month, value.Day) switch
                {
                    (1, 1) => "春节",
                    (1, 15) => "元宵",
                    (5, 5) => "端午",
                    (7, 7) => "七夕",
                    (7, 15) => "中元",
                    (8, 15) => "中秋",
                    (9, 9) => "重阳",
                    (12, 8) => "腊八",
                    _ => string.Empty
                };
                if (!string.IsNullOrEmpty(fixedFestival))
                {
                    return Localize(fixedFestival, useTraditional);
                }
            }

            // Lunar December can contain 29 or 30 days. The day immediately
            // before a non-leap first day of the first month is always 除夕.
            ChineseFestivalDate next = GetChineseDate(date.AddDays(1));
            return !next.IsLeapMonth && next.Month == 1 && next.Day == 1
                ? Localize("除夕", useTraditional)
                : string.Empty;
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static string Localize(string value, bool useTraditional) =>
        useTraditional ? ChineseTextConverter.ToTraditional(value) : value;

    private static ChineseFestivalDate GetChineseDate(DateOnly date)
    {
        var calendar = new ChineseLunisolarCalendar();
        DateTime value = date.ToDateTime(TimeOnly.MinValue);
        int year = calendar.GetYear(value);
        int calendarMonth = calendar.GetMonth(value);
        int leapMonth = calendar.GetLeapMonth(year);
        bool isLeapMonth = leapMonth > 0 && calendarMonth == leapMonth;
        int month = leapMonth > 0 && calendarMonth >= leapMonth
            ? calendarMonth - 1
            : calendarMonth;
        return new ChineseFestivalDate(
            month,
            calendar.GetDayOfMonth(value),
            isLeapMonth);
    }

    private static int GetQingmingDay(int year)
    {
        int shortYear = year % 100;
        double centuryConstant = year < 2000 ? 5.59 : 4.81;
        return (int)Math.Floor(shortYear * 0.2422 + centuryConstant) - (shortYear / 4);
    }

    private readonly record struct ChineseFestivalDate(
        int Month,
        int Day,
        bool IsLeapMonth);
}
