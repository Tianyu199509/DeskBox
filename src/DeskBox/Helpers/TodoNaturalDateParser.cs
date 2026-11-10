using System.Globalization;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Helpers;

internal readonly record struct TodoNaturalDateMatch(
    DateTimeOffset? DueDate,
    bool HasDueTime,
    string RecurrenceMode)
{
    public bool HasDueDate => DueDate is not null;

    public bool HasRecurrence =>
        !string.Equals(RecurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);

    public bool HasAny => HasDueDate || HasRecurrence;
}

internal static class TodoNaturalDateParser
{
    private static readonly Regex UrlSpanRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IsoFullDateRegex = new(
        @"(?<![0-9])(?<year>20\d{2})[-/](?<month>1[0-2]|0?[1-9])[-/](?<day>3[01]|[12]\d|0?[1-9])(?![0-9])",
        RegexOptions.CultureInvariant);

    private static readonly Regex ChineseFullDateRegex = new(
        @"(?:(?<year>20\d{2})\s*年\s*)?(?<month>1[0-2]|0?[1-9])\s*月\s*(?<day>3[01]|[12]\d|0?[1-9])(?![0-9])\s*[日号]?",
        RegexOptions.CultureInvariant);

    private static readonly Regex SlashMonthDayRegex = new(
        @"(?<![0-9A-Za-z/.-])(?<month>1[0-2]|0?[1-9])/(?<day>3[01]|[12]\d|0?[1-9])(?![0-9A-Za-z/.-])",
        RegexOptions.CultureInvariant);

    private static readonly Regex MonthlyDayRegex = new(
        @"每(?:个)?月\s*(?<day>3[01]|[12]\d|0?[1-9])(?![0-9])\s*[日号]",
        RegexOptions.CultureInvariant);

    private static readonly Regex RecurringWeekdayRegex = new(
        @"每(?:周|星期|礼拜)(?<dow>[一二三四五六日天])",
        RegexOptions.CultureInvariant);

    private static readonly Regex NextWeekWeekdayRegex = new(
        @"下(?:周|星期|礼拜)(?<dow>[一二三四五六日天])",
        RegexOptions.CultureInvariant);

    private static readonly Regex CurrentOrUpcomingWeekdayRegex = new(
        @"(?<prefix>本周|这周|周|星期|礼拜)(?<dow>[一二三四五六日天])",
        RegexOptions.CultureInvariant);

    private static readonly Regex ColonTimeRegex = new(
        @"(?:(?<period>凌晨|早上|上午|中午|下午|傍晚|晚上)\s*)?(?<![0-9:])(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)(?![0-9:])(?:\s*(?<ampm>am|pm))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseSpokenTimeRegex = new(
        @"(?:(?<period>凌晨|早上|上午|中午|下午|傍晚|晚上)\s*)?(?<![0-9])(?<hour>[01]?\d|2[0-3])\s*点(?:\s*(?:(?<half>半)|(?<minute>[0-5]?\d)\s*分?))?",
        RegexOptions.CultureInvariant);

    private static readonly Regex EnglishWordTokenRegex = new(
        @"\b(?<token>every\s+day|every\s+week|every\s+weekday|every\s+month|daily|weekly|weekdays|monthly|tonight|tomorrow|tmr|today)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string? input, DateTimeOffset now, out TodoNaturalDateMatch match)
    {
        match = new TodoNaturalDateMatch(null, false, TodoRecurrenceMode.None);
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string sanitized = BlankOutUrls(input);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return false;
        }

        DateTimeOffset localNow = now.ToLocalTime();
        DateTime today = localNow.Date;

        string recurrenceMode = ParseRecurrence(sanitized);
        DateTime? targetDate = null;
        int? defaultHour = null;

        // 1. Monthly specific day: "每月15号" / "每月15日"
        Match monthlyDayMatch = MonthlyDayRegex.Match(sanitized);
        if (monthlyDayMatch.Success &&
            int.TryParse(monthlyDayMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int monthlyDay))
        {
            recurrenceMode = TodoRecurrenceMode.Monthly;
            targetDate = ResolveMonthlyDay(today, monthlyDay);
        }

        // 2. Recurring specific weekday: "每周一" .. "每周日"
        if (targetDate is null)
        {
            Match recurringDowMatch = RecurringWeekdayRegex.Match(sanitized);
            if (recurringDowMatch.Success &&
                TryMapChineseDayOfWeek(recurringDowMatch.Groups["dow"].Value, out DayOfWeek recurringDow))
            {
                recurrenceMode = TodoRecurrenceMode.Weekly;
                int daysAhead = ((int)recurringDow - (int)today.DayOfWeek + 7) % 7;
                targetDate = today.AddDays(daysAhead);
            }
        }

        // 3. Explicit ISO date: "2026-05-20" / "2026/5/20"
        if (targetDate is null)
        {
            Match isoMatch = IsoFullDateRegex.Match(sanitized);
            if (isoMatch.Success &&
                int.TryParse(isoMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int isoYear) &&
                int.TryParse(isoMatch.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int isoMonth) &&
                int.TryParse(isoMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int isoDay) &&
                TryCreateValidDate(isoYear, isoMonth, isoDay, out DateTime isoDate))
            {
                targetDate = isoDate;
            }
        }

        // 4. Explicit Chinese month/day: "5月20日" / "2026年5月20日"
        if (targetDate is null)
        {
            Match cnDateMatch = ChineseFullDateRegex.Match(sanitized);
            if (cnDateMatch.Success &&
                int.TryParse(cnDateMatch.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int cnMonth) &&
                int.TryParse(cnDateMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int cnDay))
            {
                if (cnDateMatch.Groups["year"].Success &&
                    int.TryParse(cnDateMatch.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int cnYear))
                {
                    if (TryCreateValidDate(cnYear, cnMonth, cnDay, out DateTime explicitCnDate))
                    {
                        targetDate = explicitCnDate;
                    }
                }
                else if (TryResolveMonthDay(today, cnMonth, cnDay, out DateTime resolvedCnDate))
                {
                    targetDate = resolvedCnDate;
                }
            }
        }

        // 5. Explicit slash M/d: "5/20"
        if (targetDate is null)
        {
            Match slashMatch = SlashMonthDayRegex.Match(sanitized);
            if (slashMatch.Success &&
                int.TryParse(slashMatch.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int slashMonth) &&
                int.TryParse(slashMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int slashDay) &&
                TryResolveMonthDay(today, slashMonth, slashDay, out DateTime resolvedSlashDate))
            {
                targetDate = resolvedSlashDate;
            }
        }

        // 6. Relative Chinese day expressions & weekdays
        if (targetDate is null)
        {
            if (sanitized.Contains("大后天", StringComparison.Ordinal))
            {
                targetDate = today.AddDays(3);
            }
            else if (sanitized.Contains("后天", StringComparison.Ordinal))
            {
                targetDate = today.AddDays(2);
            }
            else if (sanitized.Contains("明早", StringComparison.Ordinal))
            {
                targetDate = today.AddDays(1);
                defaultHour = 9;
            }
            else if (sanitized.Contains("明晚", StringComparison.Ordinal))
            {
                targetDate = today.AddDays(1);
                defaultHour = 20;
            }
            else if (sanitized.Contains("明天", StringComparison.Ordinal) ||
                     sanitized.Contains("明日", StringComparison.Ordinal))
            {
                targetDate = today.AddDays(1);
            }
            else if (sanitized.Contains("今晚", StringComparison.Ordinal))
            {
                targetDate = today;
                defaultHour = 20;
            }
            else if (sanitized.Contains("今早", StringComparison.Ordinal))
            {
                targetDate = today;
                defaultHour = 9;
            }
            else if (sanitized.Contains("今天", StringComparison.Ordinal) ||
                     sanitized.Contains("今日", StringComparison.Ordinal))
            {
                targetDate = today;
            }
            else
            {
                Match nextWeekMatch = NextWeekWeekdayRegex.Match(sanitized);
                if (nextWeekMatch.Success &&
                    TryMapChineseDayOfWeek(nextWeekMatch.Groups["dow"].Value, out DayOfWeek nextDow))
                {
                    int currentOffsetFromMonday = ((int)today.DayOfWeek + 6) % 7;
                    DateTime nextMonday = today.AddDays(7 - currentOffsetFromMonday);
                    int targetOffsetFromMonday = ((int)nextDow + 6) % 7;
                    targetDate = nextMonday.AddDays(targetOffsetFromMonday);
                }
                else
                {
                    Match weekdayMatch = CurrentOrUpcomingWeekdayRegex.Match(sanitized);
                    if (weekdayMatch.Success &&
                        TryMapChineseDayOfWeek(weekdayMatch.Groups["dow"].Value, out DayOfWeek targetDow))
                    {
                        int daysAhead = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
                        targetDate = today.AddDays(daysAhead);
                    }
                }
            }
        }

        // 7. English relative day keywords
        if (targetDate is null)
        {
            foreach (Match tokenMatch in EnglishWordTokenRegex.Matches(sanitized))
            {
                string token = tokenMatch.Groups["token"].Value.ToLowerInvariant();
                if (token == "tonight")
                {
                    targetDate = today;
                    defaultHour = 20;
                    break;
                }

                if (token is "tomorrow" or "tmr")
                {
                    targetDate = today.AddDays(1);
                    break;
                }

                if (token == "today")
                {
                    targetDate = today;
                    break;
                }
            }
        }

        // 8. Time expressions ("HH:mm", "下午3点半", "晚上8点", etc.)
        bool hasExplicitTime = TryParseTime(sanitized, defaultHour == 20, out int hour, out int minute);
        bool hasDueTime = false;
        if (hasExplicitTime)
        {
            hasDueTime = true;
            if (targetDate is null)
            {
                DateTime candidateToday = new(today.Year, today.Month, today.Day, hour, minute, 0, DateTimeKind.Unspecified);
                TimeSpan candidateOffset = TimeZoneInfo.Local.GetUtcOffset(candidateToday);
                DateTimeOffset candidateDto = new(candidateToday, candidateOffset);
                bool hasRecurrence = !string.Equals(recurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);
                targetDate = !hasRecurrence && candidateDto < localNow
                    ? today.AddDays(1)
                    : today;
            }
        }
        else if (defaultHour is { } impliedHour && targetDate is not null)
        {
            hasDueTime = true;
            hour = impliedHour;
            minute = 0;
        }

        bool hasRecurrenceMode = !string.Equals(recurrenceMode, TodoRecurrenceMode.None, StringComparison.Ordinal);
        if (targetDate is null && hasRecurrenceMode)
        {
            targetDate = recurrenceMode == TodoRecurrenceMode.Weekdays &&
                         today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? today.AddDays(today.DayOfWeek == DayOfWeek.Saturday ? 2 : 1)
                : today;
        }

        if (targetDate is null && !hasRecurrenceMode)
        {
            return false;
        }

        DateTimeOffset? resolvedDueDate = null;
        if (targetDate is { } date)
        {
            if (hasDueTime)
            {
                DateTime localDateTime = new(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
                TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localDateTime);
                resolvedDueDate = new DateTimeOffset(localDateTime, offset);
            }
            else
            {
                DateTime localMidnight = new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
                TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
                resolvedDueDate = TodoWidgetStore.NormalizeAllDayDueDate(new DateTimeOffset(localMidnight, offset));
            }
        }

        match = new TodoNaturalDateMatch(resolvedDueDate, hasDueTime, recurrenceMode);
        return match.HasAny;
    }

    private static string BlankOutUrls(string input)
    {
        return UrlSpanRegex.Replace(input, static m => new string(' ', m.Length));
    }

    private static string ParseRecurrence(string sanitized)
    {
        if (sanitized.Contains("每天", StringComparison.Ordinal) ||
            sanitized.Contains("每日", StringComparison.Ordinal))
        {
            return TodoRecurrenceMode.Daily;
        }

        if (sanitized.Contains("工作日", StringComparison.Ordinal))
        {
            return TodoRecurrenceMode.Weekdays;
        }

        if (sanitized.Contains("每周", StringComparison.Ordinal) ||
            sanitized.Contains("每星期", StringComparison.Ordinal) ||
            sanitized.Contains("每礼拜", StringComparison.Ordinal))
        {
            return TodoRecurrenceMode.Weekly;
        }

        if (sanitized.Contains("每月", StringComparison.Ordinal) ||
            sanitized.Contains("每个月", StringComparison.Ordinal))
        {
            return TodoRecurrenceMode.Monthly;
        }

        foreach (Match tokenMatch in EnglishWordTokenRegex.Matches(sanitized))
        {
            string normalized = Regex.Replace(tokenMatch.Groups["token"].Value.Trim().ToLowerInvariant(), @"\s+", " ");
            switch (normalized)
            {
                case "daily":
                case "every day":
                    return TodoRecurrenceMode.Daily;
                case "weekly":
                case "every week":
                    return TodoRecurrenceMode.Weekly;
                case "weekdays":
                case "every weekday":
                    return TodoRecurrenceMode.Weekdays;
                case "monthly":
                case "every month":
                    return TodoRecurrenceMode.Monthly;
            }
        }

        return TodoRecurrenceMode.None;
    }

    private static bool TryParseTime(string sanitized, bool eveningContext, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        Match colonMatch = ColonTimeRegex.Match(sanitized);
        if (colonMatch.Success &&
            int.TryParse(colonMatch.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int colonHour) &&
            int.TryParse(colonMatch.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int colonMinute))
        {
            string period = colonMatch.Groups["period"].Value;
            string ampm = colonMatch.Groups["ampm"].Value;
            hour = NormalizePeriodHour(colonHour, period, ampm, eveningContext);
            minute = colonMinute;
            return true;
        }

        Match spokenMatch = ChineseSpokenTimeRegex.Match(sanitized);
        if (spokenMatch.Success &&
            int.TryParse(spokenMatch.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int spokenHour))
        {
            int spokenMinute = 0;
            if (spokenMatch.Groups["half"].Success)
            {
                spokenMinute = 30;
            }
            else if (spokenMatch.Groups["minute"].Success &&
                     !string.IsNullOrEmpty(spokenMatch.Groups["minute"].Value))
            {
                if (!int.TryParse(spokenMatch.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out spokenMinute))
                {
                    return false;
                }
            }

            string period = spokenMatch.Groups["period"].Value;
            hour = NormalizePeriodHour(spokenHour, period, string.Empty, eveningContext);
            minute = spokenMinute;
            return true;
        }

        return false;
    }

    private static int NormalizePeriodHour(int rawHour, string period, string ampm, bool eveningContext)
    {
        if (!string.IsNullOrEmpty(ampm))
        {
            if (string.Equals(ampm, "pm", StringComparison.OrdinalIgnoreCase) && rawHour is >= 1 and <= 11)
            {
                return rawHour + 12;
            }

            if (string.Equals(ampm, "am", StringComparison.OrdinalIgnoreCase) && rawHour == 12)
            {
                return 0;
            }
        }

        if (period is "下午" or "傍晚" or "晚上")
        {
            return rawHour is >= 1 and <= 11 ? rawHour + 12 : rawHour == 12 && period == "晚上" ? 0 : rawHour;
        }

        if (period == "中午")
        {
            return rawHour is >= 1 and <= 4 ? rawHour + 12 : rawHour;
        }

        if (period is "凌晨" or "早上" or "上午")
        {
            return rawHour == 12 && period == "凌晨" ? 0 : rawHour;
        }

        if (eveningContext && rawHour is >= 6 and <= 11)
        {
            return rawHour + 12;
        }

        return rawHour;
    }

    private static DateTime ResolveMonthlyDay(DateTime today, int requestedDay)
    {
        int clampedThisMonth = Math.Min(requestedDay, DateTime.DaysInMonth(today.Year, today.Month));
        DateTime candidate = new(today.Year, today.Month, clampedThisMonth);
        if (candidate >= today)
        {
            return candidate;
        }

        DateTime nextMonth = new DateTime(today.Year, today.Month, 1).AddMonths(1);
        int clampedNextMonth = Math.Min(requestedDay, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTime(nextMonth.Year, nextMonth.Month, clampedNextMonth);
    }

    private static bool TryResolveMonthDay(DateTime today, int month, int day, out DateTime resolved)
    {
        if (TryCreateValidDate(today.Year, month, day, out DateTime thisYearCandidate))
        {
            if (thisYearCandidate >= today)
            {
                resolved = thisYearCandidate;
                return true;
            }

            if (TryCreateValidDate(today.Year + 1, month, day, out DateTime nextYearCandidate))
            {
                resolved = nextYearCandidate;
                return true;
            }
        }
        else if (TryCreateValidDate(today.Year + 1, month, day, out DateTime nextLeapCandidate))
        {
            resolved = nextLeapCandidate;
            return true;
        }

        resolved = default;
        return false;
    }

    private static bool TryCreateValidDate(int year, int month, int day, out DateTime date)
    {
        if (year is >= 2000 and <= 2100 &&
            month is >= 1 and <= 12 &&
            day >= 1 &&
            day <= DateTime.DaysInMonth(year, month))
        {
            date = new DateTime(year, month, day);
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryMapChineseDayOfWeek(string token, out DayOfWeek dayOfWeek)
    {
        switch (token)
        {
            case "一":
                dayOfWeek = DayOfWeek.Monday;
                return true;
            case "二":
                dayOfWeek = DayOfWeek.Tuesday;
                return true;
            case "三":
                dayOfWeek = DayOfWeek.Wednesday;
                return true;
            case "四":
                dayOfWeek = DayOfWeek.Thursday;
                return true;
            case "五":
                dayOfWeek = DayOfWeek.Friday;
                return true;
            case "六":
                dayOfWeek = DayOfWeek.Saturday;
                return true;
            case "日":
            case "天":
                dayOfWeek = DayOfWeek.Sunday;
                return true;
            default:
                dayOfWeek = default;
                return false;
        }
    }
}
