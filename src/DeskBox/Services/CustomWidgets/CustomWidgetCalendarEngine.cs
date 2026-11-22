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
    string FirstEventTitle,
    string CornerBadgeKind = "",
    bool HasHighPriorityEvent = false);

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
    private static readonly object s_monthCacheGate = new();
    private static (DateOnly FirstOfMonth, DateOnly Today, string CultureName, GlanceCalendarMonth Month)? s_cachedMonth;

    private static GlanceCalendarMonth GetOrBuildEnrichedMonth(DateOnly targetMonthDate, DateOnly today, CultureInfo culture)
    {
        DateOnly firstOfMonth = new(targetMonthDate.Year, targetMonthDate.Month, 1);
        string cultureKey = culture.Name;
        lock (s_monthCacheGate)
        {
            if (s_cachedMonth is { } cached &&
                cached.FirstOfMonth == firstOfMonth &&
                cached.Today == today &&
                string.Equals(cached.CultureName, cultureKey, StringComparison.OrdinalIgnoreCase))
            {
                return cached.Month;
            }
        }

        GlanceCalendarMonth rawMonth = PresentationSource.GetMonthAsync(firstOfMonth, culture).GetAwaiter().GetResult();
        GlanceCalendarMonth withTrad = TraditionalService.Apply(rawMonth, GlanceTraditionalCalendarMode.Auto, culture, today);
        GlanceCalendarMonth full = FestivalService.Apply(withTrad, showChineseFestivals: true, GlanceTraditionalCalendarMode.Auto, culture);

        lock (s_monthCacheGate)
        {
            s_cachedMonth = (firstOfMonth, today, cultureKey, full);
        }

        return full;
    }

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

        string trimmedPath = path.Trim();
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);

        switch (trimmedPath)
        {
            case "date":
            case "today":
                return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "year":
                return today.Year.ToString(CultureInfo.InvariantCulture);
            case "month":
                return today.Month.ToString(CultureInfo.InvariantCulture);
            case "monthPadded":
                return today.Month.ToString("D2", CultureInfo.InvariantCulture);
            case "day":
                return today.Day.ToString(CultureInfo.InvariantCulture);
            case "dayPadded":
                return today.Day.ToString("D2", CultureInfo.InvariantCulture);
            case "daysInMonth":
                return DateTime.DaysInMonth(today.Year, today.Month).ToString(CultureInfo.InvariantCulture);
            case "weekOfYear":
                return ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture);
            case "selectedDate":
            {
                DateOnly selected = today;
                if (state is not null &&
                    state.TryGetValue("selectedDate", out string? selRaw) &&
                    DateOnly.TryParse(selRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsedSel))
                {
                    selected = parsedSel;
                }

                return selected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        CultureInfo culture = ResolveSafeCulture(cultureName);
        switch (trimmedPath)
        {
            case "weekday":
                return today.ToDateTime(TimeOnly.MinValue).ToString("dddd", culture);
            case "weekdayShort":
                return today.ToDateTime(TimeOnly.MinValue).ToString("ddd", culture);
            case "monthTitle":
            case "title":
                return FormatMonthTitle(today, culture);
        }

        // Build or reuse current month snapshot for today's traditional/festival info
        GlanceCalendarMonth full = GetOrBuildEnrichedMonth(today, today, culture);

        GlanceCalendarDay? todayCell = full.Days.FirstOrDefault(d => d.Date == today);
        string lunarText = todayCell?.TraditionalText ?? string.Empty;
        string festivalText = ResolveSolarOrLunarFestival(today, todayCell?.FestivalText, culture.Name);
        string subtitle = !string.IsNullOrWhiteSpace(festivalText) ? festivalText : lunarText;

        return trimmedPath switch
        {
            "lunar" or "traditional" or "lunarDate" => lunarText,
            "lunarTitle" or "traditionalTitle" or "ganzhi" => full.TraditionalTitle,
            "festival" or "holiday" or "holidayName" => festivalText,
            "subtitle" => subtitle,
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
        GlanceCalendarMonth full = GetOrBuildEnrichedMonth(firstOfMonth, today, culture);

        Dictionary<DateOnly, (int Count, string FirstTitle, bool HasHighPriority)> eventMap = BuildEventDayLookup(
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
            string cornerBadgeKind = ResolveCornerBadgeKind(day.Date, culture.Name);
            eventMap.TryGetValue(day.Date, out (int Count, string FirstTitle, bool HasHighPriority) evInfo);

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
                FirstEventTitle: evInfo.FirstTitle ?? string.Empty,
                CornerBadgeKind: cornerBadgeKind,
                HasHighPriorityEvent: evInfo.HasHighPriority));
        }

        return new CustomWidgetCalendarMonthView(
            MonthFirstDay: firstOfMonth,
            MonthTitle: FormatMonthTitle(firstOfMonth, culture),
            TraditionalTitle: full.TraditionalTitle,
            WeekdayHeaders: full.WeekdayHeaders,
            Cells: cells);
    }

    public static bool TryGetNextStatutoryHolidayCountdown(
        DateOnly today,
        string? cultureName,
        out string holidayLabel,
        out int daysUntil)
    {
        holidayLabel = string.Empty;
        daysUntil = 0;

        CultureInfo culture = ResolveSafeCulture(cultureName);
        if (!culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!CustomWidgetHolidaySchedule.TryGetNextHoliday(today, out CustomWidgetHolidayEntry entry))
        {
            return false;
        }

        daysUntil = Math.Max(0, entry.Start.DayNumber - today.DayNumber);
        holidayLabel = CustomWidgetHolidaySchedule.GetLocalizedName(entry, culture.Name);
        return !string.IsNullOrWhiteSpace(holidayLabel);
    }

    private static string ResolveCornerBadgeKind(DateOnly date, string cultureName)
    {
        if (!cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return CustomWidgetHolidaySchedule.GetDayType(date) switch
        {
            CustomWidgetHolidayDayType.StatutoryHoliday => "holiday",
            CustomWidgetHolidayDayType.SwapWorkday => "swapWork",
            CustomWidgetHolidayDayType.Weekend => "weekend",
            _ => string.Empty
        };
    }

    private static bool IsRecordCompleted(CustomWidgetRecord rec)
    {
        foreach (string key in (ReadOnlySpan<string>)["done", "completed", "isCompleted"])
        {
            string val = rec.GetField(key);
            if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(val, "1", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRecordHighPriority(CustomWidgetRecord rec)
    {
        string prio = rec.GetField("priority").Trim().ToLowerInvariant();
        if (prio is "high" or "1")
        {
            return true;
        }

        foreach (string key in (ReadOnlySpan<string>)["important", "isImportant"])
        {
            string val = rec.GetField(key);
            if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(val, "1", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<DateOnly, (int Count, string FirstTitle, bool HasHighPriority)> BuildEventDayLookup(
        IReadOnlyList<GlanceCalendarDay> visibleDays,
        IReadOnlyList<CustomWidgetRecord>? records,
        string? explicitDateField)
    {
        var map = new Dictionary<DateOnly, (int Count, string FirstTitle, bool HasHighPriority)>();
        if (records is null || records.Count == 0 || visibleDays.Count == 0)
        {
            return map;
        }

        DateOnly gridStart = visibleDays[0].Date;
        DateOnly gridEnd = visibleDays[^1].Date;

        foreach (CustomWidgetRecord rec in records)
        {
            if (rec.IsDeleted || IsRecordCompleted(rec))
            {
                continue;
            }

            bool isHighPriority = IsRecordHighPriority(rec);
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
                            AddHit(map, solarHit, title, isHighPriority);
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
                                AddHit(map, candidate, title, isHighPriority);
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
                                AddHit(map, d, title, isHighPriority);
                            }
                        }
                    }
                    else if (parsed >= gridStart && parsed <= gridEnd)
                    {
                        AddHit(map, parsed, title, isHighPriority);
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
                            AddHit(map, candidate, title, isHighPriority);
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
                    AddHit(map, createdDate, title, isHighPriority);
                }
            }
        }

        return map;
    }

    private static void AddHit(
        Dictionary<DateOnly, (int Count, string FirstTitle, bool HasHighPriority)> map,
        DateOnly date,
        string title,
        bool isHighPriority)
    {
        if (map.TryGetValue(date, out (int Count, string FirstTitle, bool HasHighPriority) cur))
        {
            map[date] = (
                cur.Count + 1,
                string.IsNullOrWhiteSpace(cur.FirstTitle) ? title : cur.FirstTitle,
                cur.HasHighPriority || isHighPriority);
        }
        else
        {
            map[date] = (1, title, isHighPriority);
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
            (1, 1) => ResolveLocalizedFestival(cultureName, "CustomWidget.Calendar.NewYear", "New Year"),
            (12, 25) => ResolveLocalizedFestival(cultureName, "CustomWidget.Calendar.Christmas", "Christmas"),
            _ => string.Empty
        };
    }

    private static string ResolveLocalizedFestival(string cultureName, string key, string fallback)
    {
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return !string.IsNullOrWhiteSpace(resolved) && !string.Equals(resolved, key, StringComparison.Ordinal)
            ? resolved
            : fallback;
    }

    private static string FormatMonthTitle(DateOnly date, CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{date.Year}年{date.Month}月";
        }

        return date.ToDateTime(TimeOnly.MinValue).ToString(
            "MMMM yyyy",
            LocalizationService.EnsureGregorianCalendar(culture));
    }

    private static CultureInfo ResolveSafeCulture(string? cultureName)
    {
        string name = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        try
        {
            return LocalizationService.EnsureGregorianCalendar(CultureInfo.GetCultureInfo(name));
        }
        catch (CultureNotFoundException)
        {
            return LocalizationService.EnsureGregorianCalendar(CultureInfo.CurrentUICulture);
        }
    }
}
