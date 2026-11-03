using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetCalendarCell(
    DateOnly Date,
    string IsoDate,
    string DayText,
    string SubtitleText,
    bool IsCurrentMonth,
    bool IsToday,
    bool IsSelected,
    bool IsWeekend,
    bool IsFestival,
    int EventCount,
    string FirstEventTitle);

public sealed record CustomWidgetCalendarMonthView(
    DateOnly MonthFirstDay,
    string MonthTitle,
    string TraditionalTitle,
    IReadOnlyList<string> WeekdayHeaders,
    IReadOnlyList<CustomWidgetCalendarCell> Cells);

/// <summary>
/// Global solar + traditional calendar (Chinese Lunisolar, Japanese Era, Hijri, Hebrew, etc.)
/// &amp; festival engine for Custom Widgets (<c>$calendar.*</c> tokens and <c>CalendarGrid</c> component).
/// Reuses <see cref="LocalCalendarPresentationSource"/>, <see cref="GlanceTraditionalCalendarService"/>,
/// and <see cref="GlanceFestivalService"/>.
/// </summary>
public static class CustomWidgetCalendarEngine
{
    private static readonly LocalCalendarPresentationSource PresentationSource = new();
    private static readonly GlanceTraditionalCalendarService TraditionalService = new();
    private static readonly GlanceFestivalService FestivalService = new();

    public static string ResolveToken(
        string path,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? state = null,
        string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        CultureInfo culture = ResolveSafeCulture(cultureName);
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);

        // Build current month snapshot for today's traditional/festival info
        GlanceCalendarMonth rawMonth = PresentationSource.GetMonthAsync(today, culture).GetAwaiter().GetResult();
        GlanceCalendarMonth withTrad = TraditionalService.Apply(rawMonth, GlanceTraditionalCalendarMode.Auto, culture, today);
        GlanceCalendarMonth full = FestivalService.Apply(withTrad, showChineseFestivals: true, GlanceTraditionalCalendarMode.Auto, culture);

        GlanceCalendarDay? todayCell = full.Days.FirstOrDefault(d => d.Date == today);
        string lunarText = todayCell?.TraditionalText ?? string.Empty;
        string festivalText = ResolveSolarOrLunarFestival(today, todayCell?.FestivalText, culture.Name);
        string subtitle = !string.IsNullOrWhiteSpace(festivalText) ? festivalText : lunarText;

        DateOnly selected = today;
        if (state is not null &&
            state.TryGetValue("selectedDate", out string? selRaw) &&
            DateOnly.TryParse(selRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedSel))
        {
            selected = parsedSel;
        }

        return path.Trim() switch
        {
            "date" or "today" => today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "year" => today.Year.ToString(CultureInfo.InvariantCulture),
            "month" => today.Month.ToString(CultureInfo.InvariantCulture),
            "monthPadded" => today.Month.ToString("D2", CultureInfo.InvariantCulture),
            "day" => today.Day.ToString(CultureInfo.InvariantCulture),
            "dayPadded" => today.Day.ToString("D2", CultureInfo.InvariantCulture),
            "weekday" => today.ToDateTime(TimeOnly.MinValue).ToString("dddd", culture),
            "weekdayShort" => today.ToDateTime(TimeOnly.MinValue).ToString("ddd", culture),
            "monthTitle" or "title" => FormatMonthTitle(today, culture),
            "lunar" or "traditional" => lunarText,
            "lunarTitle" or "traditionalTitle" or "ganzhi" => full.TraditionalTitle,
            "festival" or "holiday" => festivalText,
            "subtitle" => subtitle,
            "daysInMonth" => DateTime.DaysInMonth(today.Year, today.Month).ToString(CultureInfo.InvariantCulture),
            "weekOfYear" => ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture),
            "selectedDate" => selected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    public static CustomWidgetCalendarMonthView BuildMonthView(
        DateOnly visibleMonth,
        DateOnly today,
        DateOnly selectedDate,
        string? cultureName = null,
        IReadOnlyList<CustomWidgetRecord>? eventsCollection = null,
        string? dateField = null)
    {
        CultureInfo culture = ResolveSafeCulture(cultureName);
        DateOnly firstOfMonth = new(visibleMonth.Year, visibleMonth.Month, 1);

        GlanceCalendarMonth rawMonth = PresentationSource.GetMonthAsync(firstOfMonth, culture).GetAwaiter().GetResult();
        GlanceCalendarMonth withTrad = TraditionalService.Apply(rawMonth, GlanceTraditionalCalendarMode.Auto, culture, today);
        GlanceCalendarMonth full = FestivalService.Apply(withTrad, showChineseFestivals: true, GlanceTraditionalCalendarMode.Auto, culture);

        Dictionary<DateOnly, (int Count, string FirstTitle)> eventMap = BuildEventDayLookup(
            full.Days,
            eventsCollection,
            dateField);

        var cells = new List<CustomWidgetCalendarCell>(full.Days.Count);
        foreach (GlanceCalendarDay day in full.Days)
        {
            string fest = ResolveSolarOrLunarFestival(day.Date, day.FestivalText, culture.Name);
            bool hasFest = !string.IsNullOrWhiteSpace(fest);
            string sub = hasFest ? fest : day.TraditionalText;
            bool isWeekend = day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            eventMap.TryGetValue(day.Date, out (int Count, string FirstTitle) evInfo);

            cells.Add(new CustomWidgetCalendarCell(
                Date: day.Date,
                IsoDate: day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DayText: day.DayText,
                SubtitleText: sub,
                IsCurrentMonth: day.IsCurrentMonth,
                IsToday: day.Date == today,
                IsSelected: day.Date == selectedDate,
                IsWeekend: isWeekend,
                IsFestival: hasFest,
                EventCount: evInfo.Count,
                FirstEventTitle: evInfo.FirstTitle ?? string.Empty));
        }

        return new CustomWidgetCalendarMonthView(
            MonthFirstDay: firstOfMonth,
            MonthTitle: FormatMonthTitle(firstOfMonth, culture),
            TraditionalTitle: full.TraditionalTitle,
            WeekdayHeaders: full.WeekdayHeaders,
            Cells: cells);
    }

    private static Dictionary<DateOnly, (int Count, string FirstTitle)> BuildEventDayLookup(
        IReadOnlyList<GlanceCalendarDay> visibleDays,
        IReadOnlyList<CustomWidgetRecord>? records,
        string? explicitDateField)
    {
        var map = new Dictionary<DateOnly, (int Count, string FirstTitle)>();
        if (records is null || records.Count == 0 || visibleDays.Count == 0)
        {
            return map;
        }

        DateOnly gridStart = visibleDays[0].Date;
        DateOnly gridEnd = visibleDays[^1].Date;

        foreach (CustomWidgetRecord rec in records)
        {
            if (rec.IsDeleted)
            {
                continue;
            }

            string title = rec.GetField("title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = rec.GetField("text");
            }

            string rawDate = !string.IsNullOrWhiteSpace(explicitDateField)
                ? rec.GetField(explicitDateField)
                : (!string.IsNullOrWhiteSpace(rec.GetField("date"))
                    ? rec.GetField("date")
                    : rec.GetField("rawDate"));

            string repeat = rec.GetField("repeat").Trim().ToLowerInvariant();
            string calType = rec.GetField("calendar").Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(rawDate))
            {
                if (string.Equals(calType, "lunar", StringComparison.OrdinalIgnoreCase) &&
                    DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly lunarSpec))
                {
                    // Match yearly lunar events across visible solar days
                    bool isLeap = string.Equals(rec.GetField("lunarLeap"), "true", StringComparison.OrdinalIgnoreCase);
                    for (int y = gridStart.Year - 1; y <= gridEnd.Year + 1; y++)
                    {
                        if (CustomWidgetCountdownEngine.TryLunarToSolar(y, lunarSpec.Month, lunarSpec.Day, isLeap, out DateOnly solarHit) &&
                            solarHit >= gridStart && solarHit <= gridEnd)
                        {
                            AddHit(map, solarHit, title);
                        }
                    }

                    continue;
                }

                if (DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
                {
                    if (repeat == "yearly")
                    {
                        for (int y = gridStart.Year; y <= gridEnd.Year; y++)
                        {
                            int safeDay = Math.Min(parsed.Day, DateTime.DaysInMonth(y, parsed.Month));
                            var candidate = new DateOnly(y, parsed.Month, safeDay);
                            if (candidate >= gridStart && candidate <= gridEnd)
                            {
                                AddHit(map, candidate, title);
                            }
                        }
                    }
                    else if (repeat == "monthly")
                    {
                        for (DateOnly d = gridStart; d <= gridEnd; d = d.AddDays(1))
                        {
                            int safeDay = Math.Min(parsed.Day, DateTime.DaysInMonth(d.Year, d.Month));
                            if (d.Day == safeDay)
                            {
                                AddHit(map, d, title);
                            }
                        }
                    }
                    else if (parsed >= gridStart && parsed <= gridEnd)
                    {
                        AddHit(map, parsed, title);
                    }

                    continue;
                }

                // Support "MM-dd" recurring shorthand (e.g. "10-24")
                if (rawDate.Length == 5 && rawDate[2] == '-' &&
                    int.TryParse(rawDate[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm) &&
                    int.TryParse(rawDate[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dd) &&
                    mm is >= 1 and <= 12 && dd is >= 1 and <= 31)
                {
                    for (int y = gridStart.Year; y <= gridEnd.Year; y++)
                    {
                        int safeDay = Math.Min(dd, DateTime.DaysInMonth(y, mm));
                        var candidate = new DateOnly(y, mm, safeDay);
                        if (candidate >= gridStart && candidate <= gridEnd)
                        {
                            AddHit(map, candidate, title);
                        }
                    }

                    continue;
                }
            }
            else if (string.IsNullOrWhiteSpace(explicitDateField))
            {
                // Fallback to record creation local date (e.g. habit check-ins / daily logs)
                DateOnly createdDate = DateOnly.FromDateTime(
                    new DateTimeOffset(rec.CreatedAtUtcTicks, TimeSpan.Zero).ToLocalTime().DateTime);
                if (createdDate >= gridStart && createdDate <= gridEnd)
                {
                    AddHit(map, createdDate, title);
                }
            }
        }

        return map;
    }

    private static void AddHit(Dictionary<DateOnly, (int Count, string FirstTitle)> map, DateOnly date, string title)
    {
        if (map.TryGetValue(date, out (int Count, string FirstTitle) cur))
        {
            map[date] = (cur.Count + 1, string.IsNullOrWhiteSpace(cur.FirstTitle) ? title : cur.FirstTitle);
        }
        else
        {
            map[date] = (1, title);
        }
    }

    private static string ResolveSolarOrLunarFestival(DateOnly date, string? lunarFestival, string cultureName)
    {
        if (!string.IsNullOrWhiteSpace(lunarFestival))
        {
            return lunarFestival;
        }

        bool isZh = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isZh)
        {
            bool isTrad = LocalizationService.IsTraditionalChineseCulture(cultureName);
            return (date.Month, date.Day) switch
            {
                (1, 1) => "元旦",
                (5, 1) => isTrad ? "勞動節" : "劳动节",
                (10, 1) => isTrad ? "國慶" : "国庆",
                (12, 25) => isTrad ? "聖誕" : "圣诞",
                _ => string.Empty
            };
        }

        return (date.Month, date.Day) switch
        {
            (1, 1) => "New Year",
            (12, 25) => "Christmas",
            _ => string.Empty
        };
    }

    private static string FormatMonthTitle(DateOnly date, CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{date.Year}年{date.Month}月";
        }

        return date.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", culture);
    }

    private static CultureInfo ResolveSafeCulture(string? cultureName)
    {
        string name = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentUICulture;
        }
    }
}
