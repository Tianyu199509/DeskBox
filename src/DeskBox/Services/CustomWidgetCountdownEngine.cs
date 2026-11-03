using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

public enum CountdownDirection
{
    Countdown,
    Today,
    CountUp
}

public sealed record CustomWidgetCountdownEventView(
    string Id,
    string Title,
    string Emoji,
    string RawDate,
    string CalendarType,
    string RepeatMode,
    bool IsLunarLeap,
    bool IsPinned,
    DateOnly ResolvedTargetDate,
    int DaysDelta,
    int DisplayDays,
    CountdownDirection Direction,
    bool IsUrgent,
    bool IsApproaching,
    bool IsArchivedExpired,
    double ProgressPercent,
    string StatusWord,
    string HeroSubtitle,
    string ListSubtitle,
    string BadgeText,
    string MilestoneText,
    string WeekendRule,
    string HeroBaseSubtitle,
    int MilestoneDaysRemaining,
    bool IsMilestoneImminent);

public sealed record CustomWidgetTimeProgressSnapshot(
    int Year,
    int DayOfYear,
    int DaysInYear,
    int YearRemainingDays,
    double YearPercent,
    int Month,
    int DayOfMonth,
    int DaysInMonth,
    int MonthRemainingDays,
    double MonthPercent,
    int WeekDayIndex,
    double WeekPercent,
    string YearTitle,
    string YearSummary,
    string MonthTitle,
    string MonthSummary,
    string WeekTitle,
    string WeekSummary);

/// <summary>Workday phases for the "today's work hours" progress scope (v3.0).</summary>
public enum CustomWidgetWorkdayPhase
{
    BeforeWork,
    Working,
    AfterWork
}

public sealed record CustomWidgetWorkdayProgressSnapshot(
    CustomWidgetWorkdayPhase Phase,
    bool IsAllDay,
    double Percent,
    string Title,
    string RangeText,
    string Summary);

/// <summary>Built-in "next statutory holiday" countdown view (v3.0, shared holiday table).</summary>
public sealed record CustomWidgetCountdownHolidayView(
    string Title,
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalDays,
    int DaysRemaining,
    bool IsInProgress,
    int SwapWorkdayCount,
    bool IsProvisional,
    string Subtitle);

public sealed record CustomWidgetCountdownSnapshot(
    CustomWidgetCountdownEventView? HeroEvent,
    IReadOnlyList<CustomWidgetCountdownEventView> AllEvents,
    IReadOnlyList<CustomWidgetCountdownEventView> SecondaryEvents,
    CustomWidgetTimeProgressSnapshot TimeProgress,
    CustomWidgetWorkdayProgressSnapshot Workday,
    CustomWidgetCountdownHolidayView? NextHoliday,
    string CapsuleTitle,
    string CapsuleSummary,
    string CapsuleGlyph,
    string CapsuleBadge,
    bool CapsuleIsWarning,
    double CapsuleProgress);

/// <summary>
/// Deterministic solar + Chinese lunisolar (1901–2100) countdown &amp; anniversary engine
/// for the official <c>deskbox.countdown</c> Custom Widget preset.
/// </summary>
public static class CustomWidgetCountdownEngine
{
    public const string CollectionName = "events";
    public const string KeySeeded = "countdown.seeded";

    public const string CalendarSolar = "solar";
    public const string CalendarLunar = "lunar";

    public const string RepeatNone = "none";
    public const string RepeatMonthly = "monthly";
    public const string RepeatYearly = "yearly";
    public const string RepeatWeekly = "weekly";

    /// <summary>Monthly payday rules for dates landing on Sat/Sun (v3.0): keep / move to Friday / move to Monday.</summary>
    public const string WeekendRuleNone = "none";
    public const string WeekendRuleFriday = "friday";
    public const string WeekendRuleMonday = "monday";

    public const string DefaultWorkHours = "09:00-18:00";
    public const string WorkHoursAllDay = "day24h";
    public const int DefaultUrgentDays = 7;

    private static readonly ChineseLunisolarCalendar LunarCalendar = new();

    private static readonly string[] LunarMonthNames =
    [
        "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    ];

    private static readonly string[] LunarDayNames =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];

    private static readonly string[] WeekdayShortZhHans =
    [
        "周日", "周一", "周二", "周三", "周四", "周五", "周六"
    ];

    private static readonly string[] WeekdayShortZhHant =
    [
        "週日", "週一", "週二", "週三", "週四", "週五", "週六"
    ];

    public const string DefaultEventIconGlyph = "\uE787";
    private const string KeyIconsMigratedV2 = "countdown.iconsMigratedV2";

    public static IReadOnlyList<(string Glyph, string Label)> PresetFluentIcons =>
        GetPresetFluentIcons();

    public static IReadOnlyList<(string Glyph, string Label)> GetPresetFluentIcons(string? cultureName = null)
    {
        string c = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return
        [
            ("\uE787", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Schedule")),
            ("\uE734", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Holiday")),
            ("\uE7C1", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Milestone")),
            ("\uEB51", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Anniversary")),
            ("\uE8C7", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Finance")),
            ("\uE709", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Travel")),
            ("\uE7BE", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Study")),
            ("\uE7E7", LocalizationService.TranslateForCulture(c, "CustomWidget.Countdown.Icon.Reminder"))
        ];
    }

    public static IReadOnlyList<string> PresetEmojis { get; } =
    [
        "🧧", "🎂", "💰", "❤️", "🚀", "✈️", "💳", "🎓", "🎉", "📅"
    ];

    public static bool EnsureSeeded(
        IDictionary<string, string> state,
        IDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset now,
        string? cultureName = null)
    {
        if (state.TryGetValue(KeySeeded, out string? flag) &&
            string.Equals(flag, "1", StringComparison.Ordinal))
        {
            return MigrateLegacyDefaultSeedIcons(state, collections);
        }

        if (!collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? list))
        {
            list = [];
            collections[CollectionName] = list;
        }

        state[KeySeeded] = "1";
        state[KeyIconsMigratedV2] = "1";
        if (list.Count > 0)
        {
            return true;
        }

        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        string todayIso = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 1. Pinned Hero: Next Lunar Spring Festival (正月初一)
        int lunarYear = today.Year;
        if (TrySolarToLunar(today, out int curLunarYear, out _, out _, out _))
        {
            lunarYear = curLunarYear;
        }
        if (TryLunarToSolar(lunarYear, 1, 1, isLeapMonth: false, out DateOnly thisSpring) && thisSpring < today)
        {
            lunarYear++;
        }

        CustomWidgetRecord springRec = CreateEventRecord(
            title: LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.Seed.SpringFestival"),
            emoji: "\uE734",
            dateIso: $"{lunarYear:D4}-01-01",
            calendar: CalendarLunar,
            repeat: RepeatYearly,
            pinned: true,
            createdDateIso: todayIso,
            now: now,
            cultureName: culture);
        springRec.Fields["seedKey"] = "CustomWidget.Countdown.Seed.SpringFestival";
        list.Add(springRec);

        // 2. Solar New Year (01-01)
        int nextJanYear = today is { Month: 1, Day: 1 } ? today.Year : today.Year + 1;
        CustomWidgetRecord newYearRec = CreateEventRecord(
            title: LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.Seed.NewYear"),
            emoji: "\uE787",
            dateIso: $"{nextJanYear:D4}-01-01",
            calendar: CalendarSolar,
            repeat: RepeatYearly,
            pinned: false,
            createdDateIso: todayIso,
            now: now,
            cultureName: culture);
        newYearRec.Fields["seedKey"] = "CustomWidget.Countdown.Seed.NewYear";
        list.Add(newYearRec);

        // 3. Monthly Payday (每月 15 日)
        CustomWidgetRecord paydayRec = CreateEventRecord(
            title: LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.Seed.Payday"),
            emoji: "\uE8C7",
            dateIso: $"{today.Year:D4}-{today.Month:D2}-15",
            calendar: CalendarSolar,
            repeat: RepeatMonthly,
            pinned: false,
            createdDateIso: todayIso,
            now: now,
            cultureName: culture);
        paydayRec.Fields["seedKey"] = "CustomWidget.Countdown.Seed.Payday";
        list.Add(paydayRec);

        // 4. Count-up Anniversary example (2026-01-01 or earlier milestone)
        DateOnly startOfYear = new(today.Year, 1, 1);
        DateOnly milestoneStart = today == startOfYear ? today.AddDays(-30) : startOfYear;
        CustomWidgetRecord yearStartRec = CreateEventRecord(
            title: LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.Seed.YearStarted"),
            emoji: "\uE7C1",
            dateIso: milestoneStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            calendar: CalendarSolar,
            repeat: RepeatNone,
            pinned: false,
            createdDateIso: todayIso,
            now: now,
            cultureName: culture);
        yearStartRec.Fields["seedKey"] = "CustomWidget.Countdown.Seed.YearStarted";
        list.Add(yearStartRec);

        return true;
    }

    private static bool MigrateLegacyDefaultSeedIcons(
        IDictionary<string, string> state,
        IDictionary<string, List<CustomWidgetRecord>> collections)
    {
        if (state.TryGetValue(KeyIconsMigratedV2, out string? migrated) &&
            string.Equals(migrated, "1", StringComparison.Ordinal))
        {
            return false;
        }

        state[KeyIconsMigratedV2] = "1";
        bool changed = true;

        if (!collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? list) || list.Count == 0)
        {
            return changed;
        }

        for (int i = 0; i < list.Count; i++)
        {
            CustomWidgetRecord rec = list[i];
            string title = rec.GetField("title");
            string icon = rec.GetField("emoji");
            string? upgradedGlyph = (title, icon) switch
            {
                ("春节假期", "🧧") => "\uE734",
                ("元旦跨年", "🎉") => "\uE787",
                ("发薪日", "💰") => "\uE8C7",
                ("今年已启程", "🚀") => "\uE7C1",
                (_, "📅") => "\uE787",
                _ => null
            };
            if (upgradedGlyph is not null)
            {
                var nextFields = new Dictionary<string, string>(rec.Fields, StringComparer.Ordinal)
                {
                    ["emoji"] = upgradedGlyph
                };
                list[i] = new CustomWidgetRecord
                {
                    Id = rec.Id,
                    CreatedAtUtcTicks = rec.CreatedAtUtcTicks,
                    Fields = nextFields
                };
            }
        }

        return true;
    }

    public static CustomWidgetRecord CreateEventRecord(
        string title,
        string emoji,
        string dateIso,
        string calendar,
        string repeat,
        bool pinned,
        string createdDateIso,
        DateTimeOffset now,
        bool isLunarLeap = false,
        string? existingId = null,
        string? cultureName = null,
        string? weekendRule = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = string.IsNullOrWhiteSpace(title)
                ? LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.UntitledEvent")
                : title.Trim(),
            ["emoji"] = string.IsNullOrWhiteSpace(emoji) ? DefaultEventIconGlyph : emoji.Trim(),
            ["date"] = dateIso,
            ["calendar"] = NormalizeCalendar(calendar),
            ["repeat"] = NormalizeRepeat(repeat),
            ["pinned"] = pinned ? "1" : "0",
            ["createdDate"] = createdDateIso,
            ["lunarLeap"] = isLunarLeap ? "1" : "0",
            ["weekendRule"] = NormalizeWeekendRule(weekendRule)
        };

        return new CustomWidgetRecord
        {
            Id = string.IsNullOrWhiteSpace(existingId) ? Guid.NewGuid().ToString("N")[..10] : existingId,
            CreatedAtUtcTicks = now.UtcTicks,
            Fields = fields
        };
    }

    public static void UpsertEvent(
        IDictionary<string, List<CustomWidgetRecord>> collections,
        string? eventId,
        string title,
        string emoji,
        string dateIso,
        string calendar,
        string repeat,
        bool pinned,
        bool isLunarLeap,
        DateTimeOffset now,
        string? cultureName = null,
        string? weekendRule = null)
    {
        if (!collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? list))
        {
            list = [];
            collections[CollectionName] = list;
        }

        string todayIso = DateOnly.FromDateTime(now.ToLocalTime().DateTime)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (pinned)
        {
            foreach (CustomWidgetRecord existing in list)
            {
                existing.Fields["pinned"] = "0";
            }
        }

        int idx = string.IsNullOrWhiteSpace(eventId)
            ? -1
            : list.FindIndex(r => string.Equals(r.Id, eventId, StringComparison.Ordinal));

        if (idx >= 0)
        {
            string preservedCreated = list[idx].GetField("createdDate");
            if (string.IsNullOrWhiteSpace(preservedCreated))
            {
                preservedCreated = todayIso;
            }

            list[idx] = CreateEventRecord(
                title,
                emoji,
                dateIso,
                calendar,
                repeat,
                pinned,
                preservedCreated,
                new DateTimeOffset(list[idx].CreatedAtUtcTicks, TimeSpan.Zero),
                isLunarLeap,
                list[idx].Id,
                cultureName,
                weekendRule);
        }
        else
        {
            if (list.Count == 0)
            {
                pinned = true;
            }

            list.Insert(0, CreateEventRecord(
                title,
                emoji,
                dateIso,
                calendar,
                repeat,
                pinned,
                todayIso,
                now,
                isLunarLeap,
                cultureName: cultureName,
                weekendRule: weekendRule));
        }
    }

    public static bool PinEvent(
        IDictionary<string, List<CustomWidgetRecord>> collections,
        string eventId)
    {
        if (!collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? list) ||
            string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        bool found = false;
        foreach (CustomWidgetRecord rec in list)
        {
            bool match = string.Equals(rec.Id, eventId, StringComparison.Ordinal);
            rec.Fields["pinned"] = match ? "1" : "0";
            if (match)
            {
                found = true;
            }
        }

        return found;
    }

    public static bool DeleteEvent(
        IDictionary<string, List<CustomWidgetRecord>> collections,
        string eventId)
    {
        if (!collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? list) ||
            string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        int removed = list.RemoveAll(r => string.Equals(r.Id, eventId, StringComparison.Ordinal));
        return removed > 0;
    }

    public static CustomWidgetCountdownSnapshot ComputeSnapshot(
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset now,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        DateTime localNow = now.ToLocalTime().DateTime;
        DateOnly today = DateOnly.FromDateTime(localNow);

        string expiredMode = settings.TryGetValue("expiredMode", out string? em) &&
                             string.Equals(em, "archive", StringComparison.OrdinalIgnoreCase)
            ? "archive"
            : "countup";

        string sortMode = settings.TryGetValue("sortMode", out string? sm) &&
                          string.Equals(sm, "custom", StringComparison.OrdinalIgnoreCase)
            ? "custom"
            : "smart";

        int urgentDays = settings.TryGetValue("urgentDays", out string? ud) &&
                         int.TryParse(ud, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedUrgent) &&
                         parsedUrgent is >= 1 and <= 60
            ? parsedUrgent
            : DefaultUrgentDays;

        var views = new List<CustomWidgetCountdownEventView>();
        if (collections.TryGetValue(CollectionName, out List<CustomWidgetRecord>? rawRecords) &&
            rawRecords is not null)
        {
            foreach (CustomWidgetRecord rec in rawRecords)
            {
                if (TryBuildEventView(rec, today, expiredMode, urgentDays, culture, out CustomWidgetCountdownEventView? ev) &&
                    ev is not null)
                {
                    views.Add(ev);
                }
            }
        }

        List<CustomWidgetCountdownEventView> ordered = sortMode == "custom"
            ? views
            : views
                .OrderBy(static e => GetSortBucket(e))
                .ThenBy(static e => e.Direction == CountdownDirection.CountUp ? -e.DisplayDays : e.DisplayDays)
                .ToList();

        CustomWidgetCountdownEventView? hero = ordered.FirstOrDefault(static e => e.IsPinned) ??
                                               ordered.FirstOrDefault();

        List<CustomWidgetCountdownEventView> secondary = hero is null
            ? []
            : ordered.Where(e => !string.Equals(e.Id, hero.Id, StringComparison.Ordinal)).ToList();

        CustomWidgetTimeProgressSnapshot timeProg = ComputeTimeProgress(today, culture);

        string? workHoursSetting = settings.TryGetValue("workHours", out string? wh) &&
                                   !string.IsNullOrWhiteSpace(wh)
            ? wh.Trim()
            : null;
        if (workHoursSetting is not null &&
            !string.Equals(workHoursSetting, WorkHoursAllDay, StringComparison.OrdinalIgnoreCase) &&
            !TryParseWorkHours(workHoursSetting, out _, out _))
        {
            workHoursSetting = DefaultWorkHours;
        }

        CustomWidgetWorkdayProgressSnapshot workday = ComputeWorkdayProgress(localNow, workHoursSetting, culture);

        bool showNextHoliday = !settings.TryGetValue("showNextHoliday", out string? snh) ||
                               !string.Equals(snh, "false", StringComparison.OrdinalIgnoreCase);

        CustomWidgetCountdownHolidayView? nextHoliday = null;
        if (showNextHoliday &&
            CustomWidgetHolidaySchedule.TryGetNextHoliday(today, out CustomWidgetHolidayEntry holiday))
        {
            nextHoliday = BuildHolidayView(holiday, today, culture);
        }

        string capsuleTitle;
        string capsuleSummary;
        string capsuleGlyph;
        string capsuleBadge;
        bool capsuleWarn;
        double capsulePct;

        if (hero is not null)
        {
            capsuleGlyph = hero.Emoji;
            capsuleTitle = hero.Direction switch
            {
                CountdownDirection.Today => LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.CapsuleTodayFormat",
                    hero.Title),
                CountdownDirection.Countdown => LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.CapsuleDaysLeftFormat",
                    hero.Title,
                    hero.DisplayDays),
                _ => LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.CapsuleDaysElapsedFormat",
                    hero.Title,
                    hero.DisplayDays)
            };
            capsuleSummary = hero.HeroSubtitle;
            capsuleBadge = hero.BadgeText;
            capsuleWarn = hero.IsUrgent || hero.Direction == CountdownDirection.Today;
            capsulePct = hero.ProgressPercent;
        }
        else
        {
            capsuleGlyph = DefaultEventIconGlyph;
            capsuleTitle = LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.CapsuleFallbackTitleFormat",
                timeProg.YearTitle);
            capsuleSummary = timeProg.YearSummary;
            capsuleBadge = FormattableString.Invariant($"{timeProg.YearPercent:0}%");
            capsuleWarn = false;
            capsulePct = timeProg.YearPercent;
        }

        return new CustomWidgetCountdownSnapshot(
            HeroEvent: hero,
            AllEvents: ordered,
            SecondaryEvents: secondary,
            TimeProgress: timeProg,
            Workday: workday,
            NextHoliday: nextHoliday,
            CapsuleTitle: capsuleTitle,
            CapsuleSummary: capsuleSummary,
            CapsuleGlyph: capsuleGlyph,
            CapsuleBadge: capsuleBadge,
            CapsuleIsWarning: capsuleWarn,
            CapsuleProgress: capsulePct);
    }

    /// <summary>Builds the localized "next statutory holiday" card view from the shared holiday table.</summary>
    public static CustomWidgetCountdownHolidayView BuildHolidayView(
        CustomWidgetHolidayEntry entry,
        DateOnly today,
        string culture)
    {
        string title = CustomWidgetHolidaySchedule.GetLocalizedName(entry, culture);
        bool inProgress = today >= entry.Start && today <= entry.End;
        int daysRemaining = Math.Max(0, entry.Start.DayNumber - today.DayNumber);
        string startMd = entry.Start.ToString("MM-dd", CultureInfo.InvariantCulture);
        string endMd = entry.End.ToString("MM-dd", CultureInfo.InvariantCulture);

        string subtitle = entry.TotalDays == 1
            ? startMd
            : LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.HolidaySubtitleRangeFormat",
                startMd,
                endMd,
                entry.TotalDays);

        if (entry.SwapWorkdays.Count > 0)
        {
            subtitle += " · " + LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.HolidaySwapWorkdaysFormat",
                entry.SwapWorkdays.Count);
        }

        if (entry.IsProvisional)
        {
            subtitle += " · " + LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.Countdown.HolidayProvisionalNote");
        }

        return new CustomWidgetCountdownHolidayView(
            Title: title,
            StartDate: entry.Start,
            EndDate: entry.End,
            TotalDays: entry.TotalDays,
            DaysRemaining: daysRemaining,
            IsInProgress: inProgress,
            SwapWorkdayCount: entry.SwapWorkdays.Count,
            IsProvisional: entry.IsProvisional,
            Subtitle: subtitle);
    }

    public static string ResolveToken(
        string path,
        IReadOnlyDictionary<string, string> state,
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> collections,
        DateTimeOffset now,
        string? cultureName = null) =>
        ResolveToken(path, ComputeSnapshot(state, settings, collections, now, cultureName), cultureName);

    public static string ResolveToken(
        string path,
        CustomWidgetCountdownSnapshot snap,
        string? cultureName = null)
    {
        _ = cultureName;
        return path switch
        {
            "capsuleTitle" => snap.CapsuleTitle,
            "capsuleSummary" => snap.CapsuleSummary,
            "capsuleGlyph" => snap.CapsuleGlyph,
            "capsuleBadge" => snap.CapsuleBadge,
            "capsuleIsWarning" => snap.CapsuleIsWarning ? "true" : "false",
            "capsuleProgress" => snap.CapsuleProgress.ToString("0.0", CultureInfo.InvariantCulture),
            "hero.title" or "title" => snap.HeroEvent?.Title ?? string.Empty,
            "hero.days" or "daysLeft" or "days" => (snap.HeroEvent?.DisplayDays ?? 0).ToString(CultureInfo.InvariantCulture),
            "hero.subtitle" or "subtitle" => snap.HeroEvent?.HeroSubtitle ?? string.Empty,
            "hero.badge" => snap.HeroEvent?.BadgeText ?? string.Empty,
            "hero.isToday" => snap.HeroEvent?.Direction == CountdownDirection.Today ? "true" : "false",
            "hero.milestone" => snap.HeroEvent?.MilestoneText ?? string.Empty,
            "yearPercent" or "year.percent" => snap.TimeProgress.YearPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "year.remainingDays" => snap.TimeProgress.YearRemainingDays.ToString(CultureInfo.InvariantCulture),
            "monthPercent" or "month.percent" => snap.TimeProgress.MonthPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "month.remainingDays" => snap.TimeProgress.MonthRemainingDays.ToString(CultureInfo.InvariantCulture),
            "week.percent" => snap.TimeProgress.WeekPercent.ToString("0.0", CultureInfo.InvariantCulture),
            "week.dayIndex" => snap.TimeProgress.WeekDayIndex.ToString(CultureInfo.InvariantCulture),
            "work.percent" => snap.Workday.Percent.ToString("0.0", CultureInfo.InvariantCulture),
            "work.phase" => snap.Workday.Phase.ToString(),
            "work.summary" => snap.Workday.Summary,
            "work.range" => snap.Workday.RangeText,
            "work.title" => snap.Workday.Title,
            "holiday.title" => snap.NextHoliday?.Title ?? string.Empty,
            "holiday.days" => (snap.NextHoliday?.DaysRemaining ?? 0).ToString(CultureInfo.InvariantCulture),
            "holiday.subtitle" => snap.NextHoliday?.Subtitle ?? string.Empty,
            "holiday.inProgress" => snap.NextHoliday?.IsInProgress == true ? "true" : "false",
            "count" => snap.AllEvents.Count.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    public static CustomWidgetTimeProgressSnapshot ComputeTimeProgress(
        DateOnly today,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        int year = today.Year;
        int daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        int dayOfYear = today.DayOfYear;
        int yearRem = Math.Max(0, daysInYear - dayOfYear);
        double yearPct = Math.Clamp((double)dayOfYear / daysInYear * 100.0, 0.0, 100.0);

        int month = today.Month;
        int daysInMonth = DateTime.DaysInMonth(year, month);
        int dayOfMonth = today.Day;
        int monthRem = Math.Max(0, daysInMonth - dayOfMonth);
        double monthPct = Math.Clamp((double)dayOfMonth / daysInMonth * 100.0, 0.0, 100.0);

        bool isZh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string monthDisplay = month.ToString(CultureInfo.InvariantCulture);
        if (!isZh)
        {
            try
            {
                var ci = CultureInfo.GetCultureInfo(culture);
                monthDisplay = new DateTime(year, month, 1).ToString("MMMM", ci);
            }
            catch (CultureNotFoundException)
            {
                monthDisplay = new DateTime(year, month, 1).ToString("MMMM", CultureInfo.InvariantCulture);
            }
        }

        // Monday-first week index (1..7) and elapsed percent of the current week.
        int weekDayIndex = ((int)today.DayOfWeek + 6) % 7 + 1;
        double weekPct = Math.Clamp(weekDayIndex / 7.0 * 100.0, 0.0, 100.0);

        return new CustomWidgetTimeProgressSnapshot(
            Year: year,
            DayOfYear: dayOfYear,
            DaysInYear: daysInYear,
            YearRemainingDays: yearRem,
            YearPercent: yearPct,
            Month: month,
            DayOfMonth: dayOfMonth,
            DaysInMonth: daysInMonth,
            MonthRemainingDays: monthRem,
            MonthPercent: monthPct,
            WeekDayIndex: weekDayIndex,
            WeekPercent: weekPct,
            YearTitle: LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.YearProgressTitleFormat",
                year),
            YearSummary: LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.YearProgressSummaryFormat",
                yearPct.ToString("0", CultureInfo.InvariantCulture),
                yearRem),
            MonthTitle: LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.MonthProgressTitleFormat",
                monthDisplay),
            MonthSummary: LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.MonthProgressSummaryFormat",
                dayOfMonth,
                daysInMonth,
                monthRem),
            WeekTitle: LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.Countdown.WeekProgressTitle"),
            WeekSummary: LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.WeekProgressSummaryFormat",
                weekDayIndex,
                weekPct.ToString("0", CultureInfo.InvariantCulture)));
    }

    public static bool TryLunarToSolar(
        int lunarYear,
        int logicalMonth,
        int lunarDay,
        bool isLeapMonth,
        out DateOnly solarDate)
    {
        solarDate = default;
        if (lunarYear is < 1902 or > 2099 || logicalMonth is < 1 or > 12 || lunarDay is < 1 or > 30)
        {
            return false;
        }

        try
        {
            int leapMonth = LunarCalendar.GetLeapMonth(lunarYear);
            int calMonth = logicalMonth;
            if (leapMonth > 0)
            {
                if (isLeapMonth && leapMonth == logicalMonth + 1)
                {
                    calMonth = leapMonth;
                }
                else if (logicalMonth >= leapMonth)
                {
                    calMonth = logicalMonth + 1;
                }
            }

            int maxDays = LunarCalendar.GetDaysInMonth(lunarYear, calMonth);
            int clampedDay = Math.Clamp(lunarDay, 1, maxDays);
            DateTime dt = LunarCalendar.ToDateTime(lunarYear, calMonth, clampedDay, 0, 0, 0, 0);
            solarDate = DateOnly.FromDateTime(dt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySolarToLunar(
        DateOnly solarDate,
        out int lunarYear,
        out int logicalMonth,
        out int lunarDay,
        out bool isLeapMonth)
    {
        lunarYear = solarDate.Year;
        logicalMonth = solarDate.Month;
        lunarDay = solarDate.Day;
        isLeapMonth = false;

        DateTime dt = solarDate.ToDateTime(TimeOnly.MinValue);
        if (dt < LunarCalendar.MinSupportedDateTime || dt > LunarCalendar.MaxSupportedDateTime)
        {
            return false;
        }

        try
        {
            lunarYear = LunarCalendar.GetYear(dt);
            int rawMonth = LunarCalendar.GetMonth(dt);
            lunarDay = LunarCalendar.GetDayOfMonth(dt);
            int leapMonth = LunarCalendar.GetLeapMonth(lunarYear);

            if (leapMonth > 0)
            {
                if (rawMonth == leapMonth)
                {
                    isLeapMonth = true;
                    logicalMonth = rawMonth - 1;
                }
                else if (rawMonth > leapMonth)
                {
                    logicalMonth = rawMonth - 1;
                }
                else
                {
                    logicalMonth = rawMonth;
                }
            }
            else
            {
                logicalMonth = rawMonth;
            }

            logicalMonth = Math.Clamp(logicalMonth, 1, 12);
            lunarDay = Math.Clamp(lunarDay, 1, 30);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int GetLunarLeapLogicalMonth(int lunarYear)
    {
        if (lunarYear is < 1902 or > 2099)
        {
            return 0;
        }

        try
        {
            int leapMonth = LunarCalendar.GetLeapMonth(lunarYear);
            return leapMonth > 1 ? leapMonth - 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryRawLunarMonthToSolar(
        int lunarYear,
        int rawCalMonth,
        int lunarDay,
        out DateOnly solarDate)
    {
        solarDate = default;
        if (lunarYear is < 1902 or > 2099 || lunarDay is < 1 or > 30)
        {
            return false;
        }

        try
        {
            int monthsInYear = LunarCalendar.GetMonthsInYear(lunarYear);
            if (rawCalMonth < 1 || rawCalMonth > monthsInYear)
            {
                return false;
            }

            int maxDays = LunarCalendar.GetDaysInMonth(lunarYear, rawCalMonth);
            int clampedDay = Math.Clamp(lunarDay, 1, maxDays);
            DateTime dt = LunarCalendar.ToDateTime(lunarYear, rawCalMonth, clampedDay, 0, 0, 0, 0);
            solarDate = DateOnly.FromDateTime(dt);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatLunarMonthDay(
        int logicalMonth,
        int lunarDay,
        bool isLeapMonth = false,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        if (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool isHant = culture.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                          culture.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                          culture.Contains("Hant", StringComparison.OrdinalIgnoreCase);
            string monthText = GetLunarMonthName(logicalMonth);
            if (isHant && logicalMonth == 12)
            {
                monthText = "臘月";
            }
            string dayText = GetLunarDayName(lunarDay);
            string leapPrefix = isHant ? "閏" : "闰";
            return isLeapMonth ? $"{leapPrefix}{monthText}{dayText}" : $"{monthText}{dayText}";
        }

        return LocalizationService.FormatForCulture(
            culture,
            isLeapMonth
                ? "CustomWidget.Countdown.LunarLeapMonthDayFormat"
                : "CustomWidget.Countdown.LunarMonthDayFormat",
            logicalMonth,
            lunarDay);
    }

    public static string GetLunarMonthName(int logicalMonth, string? cultureName = null)
    {
        int clamped = Math.Clamp(logicalMonth, 1, 12);
        if (!string.IsNullOrWhiteSpace(cultureName) &&
            !cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"M{clamped}";
        }

        if (!string.IsNullOrWhiteSpace(cultureName) &&
            clamped == 12 &&
            (cultureName.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
             cultureName.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
             cultureName.Contains("Hant", StringComparison.OrdinalIgnoreCase)))
        {
            return "臘月";
        }

        return LunarMonthNames[clamped - 1];
    }

    public static string GetLunarDayName(int lunarDay, string? cultureName = null)
    {
        int clamped = Math.Clamp(lunarDay, 1, 30);
        if (!string.IsNullOrWhiteSpace(cultureName) &&
            !cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return clamped.ToString(CultureInfo.InvariantCulture);
        }

        return LunarDayNames[clamped - 1];
    }

    public static string NormalizeCalendar(string? raw) =>
        string.Equals(raw?.Trim(), CalendarLunar, StringComparison.OrdinalIgnoreCase)
            ? CalendarLunar
            : CalendarSolar;

    public static string NormalizeRepeat(string? raw) =>
        (raw?.Trim().ToLowerInvariant()) switch
        {
            RepeatMonthly => RepeatMonthly,
            RepeatYearly => RepeatYearly,
            RepeatWeekly => RepeatWeekly,
            _ => RepeatNone
        };

    public static string NormalizeWeekendRule(string? raw) =>
        (raw?.Trim().ToLowerInvariant()) switch
        {
            WeekendRuleFriday => WeekendRuleFriday,
            WeekendRuleMonday => WeekendRuleMonday,
            _ => WeekendRuleNone
        };

    /// <summary>
    /// Applies a payday weekend rule to a candidate date: Saturday moves to the preceding
    /// Friday (<paramref name="weekendRule"/> == <c>friday</c>, 1-2 days earlier) or the
    /// following Monday (<c>monday</c>, 1-2 days later). Weekday dates pass through unchanged,
    /// so the historical default (<c>none</c>) keeps byte-identical v2.1 behavior.
    /// </summary>
    public static DateOnly AdjustForWeekendRule(DateOnly candidate, string? weekendRule)
    {
        return NormalizeWeekendRule(weekendRule) switch
        {
            WeekendRuleFriday => candidate.DayOfWeek switch
            {
                DayOfWeek.Saturday => candidate.AddDays(-1),
                DayOfWeek.Sunday => candidate.AddDays(-2),
                _ => candidate
            },
            WeekendRuleMonday => candidate.DayOfWeek switch
            {
                DayOfWeek.Saturday => candidate.AddDays(2),
                DayOfWeek.Sunday => candidate.AddDays(1),
                _ => candidate
            },
            _ => candidate
        };
    }

    /// <summary>
    /// Computes the next count-up milestone (whole years + the 100/200/300/365/500/1000-day
    /// ladder, then every 500 days) for an elapsed anniversary such as a relationship or
    /// employment start date. Returns false when no bounded milestone remains (&lt;= 0 days).
    /// </summary>
    public static bool TryComputeNextMilestone(
        DateOnly originDate,
        DateOnly today,
        out bool isAnniversary,
        out int number,
        out int daysRemaining)
    {
        isAnniversary = false;
        number = 0;
        daysRemaining = 0;

        if (today < originDate)
        {
            return false;
        }

        int elapsedDays = today.DayNumber - originDate.DayNumber;

        // Next whole-year anniversary (calendar years, leap-safe).
        int years = 0;
        while (originDate.AddYears(years + 1) <= today)
        {
            years++;
        }

        int annivDays = int.MaxValue;
        int annivNumber = 0;
        if (years + 1 <= 100)
        {
            DateOnly nextAnniv = originDate.AddYears(years + 1);
            annivDays = nextAnniv.DayNumber - today.DayNumber;
            annivNumber = years + 1;
        }

        // Next day-count milestone from the ladder (bounded loop keeps NativeAOT-friendly).
        int dayDays = int.MaxValue;
        int dayNumber = 0;
        foreach (int marker in MilestoneLadder)
        {
            if (marker > elapsedDays)
            {
                dayDays = marker - elapsedDays;
                dayNumber = marker;
                break;
            }
        }

        if (dayDays == int.MaxValue && annivDays == int.MaxValue)
        {
            return false;
        }

        // Prefer the anniversary label on exact ties (365 elapsed on a non-leap span).
        if (annivDays <= dayDays)
        {
            isAnniversary = true;
            number = annivNumber;
            daysRemaining = annivDays;
        }
        else
        {
            isAnniversary = false;
            number = dayNumber;
            daysRemaining = dayDays;
        }

        return daysRemaining > 0 || isAnniversary;
    }

    private static readonly int[] MilestoneLadder =
    [
        100, 200, 300, 365, 500, 1000, 1500, 2000, 2500, 3000, 4000, 5000, 7500, 10000
    ];

    /// <summary>
    /// Parses a work-hours spec ("HH:mm-HH:mm" or <c>day24h</c>) and computes minute-smooth
    /// progress with three phases: BeforeWork / Working / AfterWork. After work the percent
    /// pins to 100 and callers render the gentle "work is done" copy — never an overflowing bar.
    /// </summary>
    public static CustomWidgetWorkdayProgressSnapshot ComputeWorkdayProgress(
        DateTime localNow,
        string? workHoursSpec,
        string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        string title = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.WorkdayTitle");

        string spec = string.IsNullOrWhiteSpace(workHoursSpec) ? DefaultWorkHours : workHoursSpec.Trim();
        if (string.Equals(spec, WorkHoursAllDay, StringComparison.OrdinalIgnoreCase))
        {
            double dayPct = Math.Clamp(localNow.TimeOfDay.TotalMinutes / (24.0 * 60.0) * 100.0, 0.0, 100.0);
            return new CustomWidgetWorkdayProgressSnapshot(
                Phase: CustomWidgetWorkdayPhase.Working,
                IsAllDay: true,
                Percent: dayPct,
                Title: title,
                RangeText: "00:00-24:00",
                Summary: LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.WorkdayAllDaySummaryFormat",
                    dayPct.ToString("0", CultureInfo.InvariantCulture)));
        }

        if (!TryParseWorkHours(spec, out int startMin, out int endMin) || endMin <= startMin)
        {
            (startMin, endMin) = (9 * 60, 18 * 60);
        }

        string rangeText = $"{MinutesToClock(startMin)}-{MinutesToClock(endMin)}";
        int nowMin = (int)localNow.TimeOfDay.TotalMinutes;
        double pct;
        CustomWidgetWorkdayPhase phase;
        string summary;

        if (nowMin < startMin)
        {
            phase = CustomWidgetWorkdayPhase.BeforeWork;
            pct = 0.0;
            summary = LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.WorkdayBeforeSummaryFormat",
                MinutesToClock(startMin));
        }
        else if (nowMin >= endMin)
        {
            phase = CustomWidgetWorkdayPhase.AfterWork;
            pct = 100.0;
            summary = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.WorkdayAfterSummary");
        }
        else
        {
            phase = CustomWidgetWorkdayPhase.Working;
            pct = Math.Clamp((double)(nowMin - startMin) / (endMin - startMin) * 100.0, 0.0, 100.0);
            summary = LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.WorkdayWorkingSummaryFormat",
                pct.ToString("0", CultureInfo.InvariantCulture),
                MinutesToClock(endMin));
        }

        return new CustomWidgetWorkdayProgressSnapshot(
            Phase: phase,
            IsAllDay: false,
            Percent: pct,
            Title: title,
            RangeText: rangeText,
            Summary: summary);
    }

    public static bool TryParseWorkHours(string? raw, out int startMinutes, out int endMinutes)
    {
        startMinutes = 0;
        endMinutes = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string spec = raw.Trim();
        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash >= spec.Length - 1)
        {
            return false;
        }

        return TryParseClock(spec[..dash], out startMinutes) &&
               TryParseClock(spec[(dash + 1)..], out endMinutes);
    }

    private static bool TryParseClock(string raw, out int minutes)
    {
        minutes = 0;
        int colon = raw.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon >= raw.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(raw[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
            !int.TryParse(raw[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
        {
            return false;
        }

        if (h is < 0 or > 24 || m is < 0 or > 59)
        {
            return false;
        }

        minutes = h * 60 + m;
        return true;
    }

    private static string MinutesToClock(int minutes) =>
        FormattableString.Invariant($"{minutes / 60:D2}:{minutes % 60:D2}");

    public static bool TryParseYmd(string? raw, out int year, out int month, out int day)
    {
        year = 2026;
        month = 1;
        day = 1;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string[] parts = raw.Trim().Split('-', '/', '.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out year) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out month) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out day))
        {
            return false;
        }

        year = Math.Clamp(year, 1902, 2099);
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, 31);
        return true;
    }

    private static int GetSortBucket(CustomWidgetCountdownEventView ev)
    {
        if (ev.IsArchivedExpired)
        {
            return 3;
        }

        return ev.Direction switch
        {
            CountdownDirection.Today => 0,
            CountdownDirection.Countdown => 1,
            _ => 2
        };
    }

    private static bool TryBuildEventView(
        CustomWidgetRecord rec,
        DateOnly today,
        string expiredMode,
        int urgentDays,
        string culture,
        out CustomWidgetCountdownEventView? view)
    {
        view = null;
        string rawDate = rec.GetField("date");
        if (!TryParseYmd(rawDate, out int baseY, out int baseM, out int baseD))
        {
            return false;
        }

        string seedKey = rec.GetField("seedKey");
        string title = !string.IsNullOrWhiteSpace(seedKey)
            ? LocalizationService.TranslateForCulture(culture, seedKey)
            : rec.GetField("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.UntitledEvent");
        }

        string emoji = rec.GetField("emoji");
        if (string.IsNullOrWhiteSpace(emoji))
        {
            emoji = DefaultEventIconGlyph;
        }

        string calendar = NormalizeCalendar(rec.GetField("calendar"));
        string repeat = NormalizeRepeat(rec.GetField("repeat"));
        bool isLunar = calendar == CalendarLunar;
        bool isLunarLeap = string.Equals(rec.GetField("lunarLeap"), "1", StringComparison.Ordinal);
        bool isPinned = string.Equals(rec.GetField("pinned"), "1", StringComparison.Ordinal);
        string weekendRule = NormalizeWeekendRule(rec.GetField("weekendRule"));

        DateOnly baseSolar;
        if (isLunar)
        {
            if (!TryLunarToSolar(baseY, baseM, baseD, isLunarLeap, out baseSolar))
            {
                return false;
            }
        }
        else
        {
            int clampedDay = Math.Min(baseD, DateTime.DaysInMonth(baseY, baseM));
            baseSolar = new DateOnly(baseY, baseM, clampedDay);
        }

        DateOnly resolvedTarget = ResolveNextOrBaseDate(
            today,
            baseY,
            baseM,
            baseD,
            baseSolar,
            isLunar,
            isLunarLeap,
            repeat,
            weekendRule,
            out DateOnly cycleStart);

        int daysDelta = resolvedTarget.DayNumber - today.DayNumber;
        CountdownDirection direction;
        bool isArchived = false;
        string statusWord;
        int displayDays;
        string badgeText;

        if (daysDelta == 0)
        {
            direction = CountdownDirection.Today;
            statusWord = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.StatusToday");
            displayDays = 0;
            badgeText = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.BadgeToday");
        }
        else if (daysDelta > 0)
        {
            direction = CountdownDirection.Countdown;
            statusWord = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.StatusDaysLeft");
            displayDays = daysDelta;
            badgeText = LocalizationService.FormatForCulture(culture, "CustomWidget.Countdown.BadgeDaysLeftFormat", daysDelta);
        }
        else
        {
            direction = CountdownDirection.CountUp;
            displayDays = Math.Abs(daysDelta);
            if (expiredMode == "archive")
            {
                isArchived = true;
                statusWord = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.StatusPastArchived");
                badgeText = LocalizationService.FormatForCulture(culture, "CustomWidget.Countdown.BadgePastArchivedFormat", displayDays);
            }
            else
            {
                statusWord = LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.StatusElapsed");
                badgeText = LocalizationService.FormatForCulture(culture, "CustomWidget.Countdown.BadgeElapsedFormat", displayDays);
            }
        }

        double progressPct = ComputeEventProgress(
            today,
            resolvedTarget,
            cycleStart,
            rec.GetField("createdDate"),
            direction,
            repeat,
            displayDays);

        bool isZh = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string milestoneText = string.Empty;
        int milestoneDaysRemaining = 0;
        string heroBaseSubtitle;
        string weekday = FormatShortWeekday(resolvedTarget.DayOfWeek, culture);
        string targetYmd = resolvedTarget.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string targetMd = resolvedTarget.ToString("MM-dd", CultureInfo.InvariantCulture);

        string lunarSuffix = isLunar
            ? LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.LunarTagFormat",
                FormatLunarMonthDay(baseM, baseD, isLunarLeap, culture))
            : string.Empty;
        string heroSubtitle;
        string listSubtitle;

        if (direction == CountdownDirection.CountUp)
        {
            heroSubtitle = isLunar
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.SinceDateWeekdayLunarFormat",
                    targetYmd,
                    weekday,
                    lunarSuffix)
                : LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.SinceDateWeekdayFormat",
                    targetYmd,
                    weekday);
            listSubtitle = LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.SinceDateFormat",
                targetYmd);
            heroBaseSubtitle = heroSubtitle;

            // v3.0: count-up focus cards append the next milestone hint
            // (e.g. "距 300 天还有 11 天" / "距 2 周年还有 76 天").
            if (!isArchived &&
                TryComputeNextMilestone(resolvedTarget, today, out bool isAnniv, out int milestoneNumber, out int milestoneDays))
            {
                milestoneDaysRemaining = milestoneDays;
                milestoneText = LocalizationService.FormatForCulture(
                    culture,
                    isAnniv
                        ? "CustomWidget.Countdown.MilestoneAnniversaryFormat"
                        : "CustomWidget.Countdown.MilestoneDaysFormat",
                    milestoneNumber,
                    milestoneDays);
                heroSubtitle = $"{heroSubtitle} · {milestoneText}";
            }
        }
        else
        {
            string repeatTag = repeat switch
            {
                RepeatMonthly => isLunar
                    ? LocalizationService.FormatForCulture(
                        culture,
                        "CustomWidget.Countdown.RepeatMonthlyLunarFormat",
                        isZh ? GetLunarDayName(baseD) : baseD.ToString(CultureInfo.InvariantCulture))
                    : LocalizationService.FormatForCulture(
                        culture,
                        "CustomWidget.Countdown.RepeatMonthlySolarFormat",
                        baseD),
                RepeatYearly => ComputeYearlyTag(baseY, resolvedTarget.Year, isLunar, lunarSuffix, culture),
                RepeatWeekly => LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.RepeatWeeklyFormat",
                    isZh && weekday.Length > 1 ? weekday[1..] : weekday),
                _ => lunarSuffix
            };

            heroSubtitle = string.IsNullOrEmpty(repeatTag)
                ? $"{targetYmd} {weekday}"
                : $"{targetYmd} {weekday} · {repeatTag}";
            listSubtitle = string.IsNullOrEmpty(repeatTag)
                ? $"{targetYmd} {weekday}"
                : $"{targetMd} {weekday} · {repeatTag}";
            heroBaseSubtitle = heroSubtitle;
        }

        // v3.0: base subtitle kept separate from the milestone hint so the hero can warm the
        // hint up to the shared amber "approaching" semantic inside the urgentDays window.
        bool isMilestoneImminent =
            milestoneDaysRemaining > 0 &&
            milestoneDaysRemaining <= Math.Max(urgentDays, 3);

        view = new CustomWidgetCountdownEventView(
            Id: rec.Id,
            Title: title,
            Emoji: emoji,
            RawDate: rawDate,
            CalendarType: calendar,
            RepeatMode: repeat,
            IsLunarLeap: isLunarLeap,
            IsPinned: isPinned,
            ResolvedTargetDate: resolvedTarget,
            DaysDelta: daysDelta,
            DisplayDays: displayDays,
            Direction: direction,
            IsUrgent: direction == CountdownDirection.Countdown && displayDays <= 3,
            IsApproaching: direction == CountdownDirection.Countdown && displayDays > 3 && displayDays <= Math.Max(urgentDays, 3),
            IsArchivedExpired: isArchived,
            ProgressPercent: progressPct,
            StatusWord: statusWord,
            HeroSubtitle: heroSubtitle,
            ListSubtitle: listSubtitle,
            BadgeText: badgeText,
            MilestoneText: milestoneText,
            WeekendRule: weekendRule,
            HeroBaseSubtitle: heroBaseSubtitle,
            MilestoneDaysRemaining: milestoneDaysRemaining,
            IsMilestoneImminent: isMilestoneImminent);

        return true;
    }

    internal static string FormatShortWeekday(DayOfWeek dayOfWeek, string culture)
    {
        if (culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            bool isHant = culture.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                          culture.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                          culture.Contains("Hant", StringComparison.OrdinalIgnoreCase);
            return isHant ? WeekdayShortZhHant[(int)dayOfWeek] : WeekdayShortZhHans[(int)dayOfWeek];
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture).DateTimeFormat.GetAbbreviatedDayName(dayOfWeek);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(dayOfWeek);
        }
    }

    private static string ComputeYearlyTag(
        int baseYear,
        int resolvedSolarYear,
        bool isLunar,
        string lunarSuffix,
        string culture)
    {
        int yearsDiff = resolvedSolarYear - baseYear;
        if (isLunar)
        {
            return yearsDiff > 0 && baseYear < resolvedSolarYear - 1
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.Countdown.LunarAnniversaryFormat",
                    lunarSuffix,
                    yearsDiff)
                : lunarSuffix;
        }

        return yearsDiff > 0
            ? LocalizationService.FormatForCulture(
                culture,
                "CustomWidget.Countdown.RepeatYearlyAnniversaryFormat",
                yearsDiff)
            : LocalizationService.TranslateForCulture(culture, "CustomWidget.Countdown.RepeatYearlyLabel");
    }

    private static DateOnly ResolveNextOrBaseDate(
        DateOnly today,
        int baseY,
        int baseM,
        int baseD,
        DateOnly baseSolar,
        bool isLunar,
        bool isLunarLeap,
        string repeat,
        string weekendRule,
        out DateOnly cycleStart)
    {
        switch (repeat)
        {
            case RepeatYearly:
            {
                if (isLunar)
                {
                    int refLunarYear = today.Year;
                    if (TrySolarToLunar(today, out int curLy, out _, out _, out _))
                    {
                        refLunarYear = curLy;
                    }

                    for (int y = refLunarYear - 1; y <= refLunarYear + 2; y++)
                    {
                        if (TryLunarToSolar(y, baseM, baseD, isLeapMonth: isLunarLeap, out DateOnly candidate) &&
                            candidate >= today)
                        {
                            TryLunarToSolar(y - 1, baseM, baseD, isLeapMonth: isLunarLeap, out cycleStart);
                            if (cycleStart == default)
                            {
                                cycleStart = candidate.AddDays(-365);
                            }
                            return candidate;
                        }
                    }

                    cycleStart = baseSolar.AddDays(-365);
                    return baseSolar;
                }
                else
                {
                    int dThis = Math.Min(baseD, DateTime.DaysInMonth(today.Year, baseM));
                    DateOnly cand = new(today.Year, baseM, dThis);
                    if (cand < today)
                    {
                        int nextY = today.Year + 1;
                        int dNext = Math.Min(baseD, DateTime.DaysInMonth(nextY, baseM));
                        cycleStart = cand;
                        return new DateOnly(nextY, baseM, dNext);
                    }

                    int prevY = today.Year - 1;
                    int dPrev = Math.Min(baseD, DateTime.DaysInMonth(prevY, baseM));
                    cycleStart = new DateOnly(prevY, baseM, dPrev);
                    return cand;
                }
            }

            case RepeatMonthly:
            {
                DateTime dtToday = today.ToDateTime(TimeOnly.MinValue);
                if (isLunar &&
                    dtToday >= LunarCalendar.MinSupportedDateTime &&
                    dtToday <= LunarCalendar.MaxSupportedDateTime)
                {
                    int y = LunarCalendar.GetYear(dtToday);
                    int rawM = LunarCalendar.GetMonth(dtToday);
                    for (int step = 0; step < 3; step++)
                    {
                        if (TryRawLunarMonthToSolar(y, rawM, baseD, out DateOnly cand) && cand >= today)
                        {
                            int prevY = rawM == 1 ? y - 1 : y;
                            int prevRawM = rawM == 1
                                ? (prevY >= 1902 ? LunarCalendar.GetMonthsInYear(prevY) : 12)
                                : rawM - 1;
                            if (!TryRawLunarMonthToSolar(prevY, prevRawM, baseD, out cycleStart))
                            {
                                cycleStart = cand.AddDays(-30);
                            }
                            return cand;
                        }

                        rawM++;
                        if (rawM > LunarCalendar.GetMonthsInYear(y))
                        {
                            rawM = 1;
                            y++;
                            if (y > 2099)
                            {
                                break;
                            }
                        }
                    }
                }

                int dayCur = Math.Min(baseD, DateTime.DaysInMonth(today.Year, today.Month));
                DateOnly curMonthDate = new(today.Year, today.Month, dayCur);

                // v3.0 payday weekend rule (solar monthly only; lunar keeps v2.1 behavior).
                bool applyWeekendRule = !isLunar && NormalizeWeekendRule(weekendRule) != WeekendRuleNone;
                if (applyWeekendRule)
                {
                    DateOnly adjustedCur = AdjustForWeekendRule(curMonthDate, weekendRule);
                    DateOnly monthAfterCur = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
                    int dayNext = Math.Min(baseD, DateTime.DaysInMonth(monthAfterCur.Year, monthAfterCur.Month));
                    DateOnly adjustedNext = AdjustForWeekendRule(
                        new DateOnly(monthAfterCur.Year, monthAfterCur.Month, dayNext),
                        weekendRule);

                    if (adjustedCur >= today)
                    {
                        DateOnly prevMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                        int dayPrev = Math.Min(baseD, DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month));
                        cycleStart = AdjustForWeekendRule(new DateOnly(prevMonth.Year, prevMonth.Month, dayPrev), weekendRule);
                        return adjustedCur;
                    }

                    cycleStart = adjustedCur;
                    return adjustedNext;
                }

                if (curMonthDate >= today)
                {
                    DateOnly prevMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                    int dayPrev = Math.Min(baseD, DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month));
                    cycleStart = new DateOnly(prevMonth.Year, prevMonth.Month, dayPrev);
                    return curMonthDate;
                }

                DateOnly nextMonth2 = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
                int dayNext2 = Math.Min(baseD, DateTime.DaysInMonth(nextMonth2.Year, nextMonth2.Month));
                cycleStart = curMonthDate;
                return new DateOnly(nextMonth2.Year, nextMonth2.Month, dayNext2);
            }

            case RepeatWeekly:
            {
                DayOfWeek targetDow = baseSolar.DayOfWeek;
                int diff = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
                DateOnly next = today.AddDays(diff);
                cycleStart = next.AddDays(-7);
                return next;
            }

            default:
            {
                cycleStart = baseSolar;
                return baseSolar;
            }
        }
    }

    private static double ComputeEventProgress(
        DateOnly today,
        DateOnly resolvedTarget,
        DateOnly cycleStart,
        string createdDateRaw,
        CountdownDirection direction,
        string repeat,
        int displayDays)
    {
        if (direction == CountdownDirection.Today)
        {
            return 100.0;
        }

        if (direction == CountdownDirection.CountUp)
        {
            int mod100 = displayDays % 100;
            return mod100 == 0 ? 100.0 : Math.Clamp(mod100, 6.0, 98.0);
        }

        if (repeat is RepeatYearly or RepeatMonthly or RepeatWeekly)
        {
            int totalSpan = resolvedTarget.DayNumber - cycleStart.DayNumber;
            int elapsed = today.DayNumber - cycleStart.DayNumber;
            if (totalSpan > 0)
            {
                return Math.Clamp((double)elapsed / totalSpan * 100.0, 4.0, 99.0);
            }
        }

        if (TryParseYmd(createdDateRaw, out int cy, out int cm, out int cd))
        {
            int clamped = Math.Min(cd, DateTime.DaysInMonth(cy, cm));
            DateOnly created = new(cy, cm, clamped);
            int span = resolvedTarget.DayNumber - created.DayNumber;
            if (span > 0 && today >= created)
            {
                int elapsed = today.DayNumber - created.DayNumber;
                return Math.Clamp((double)elapsed / span * 100.0, 6.0, 99.0);
            }
        }

        return Math.Clamp((100.0 - Math.Min(displayDays, 95)), 8.0, 94.0);
    }
}
