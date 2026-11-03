using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Task ③ (deskbox.countdown v3.0) acceptance tests: payday weekend rules, count-up
/// milestones, work-hour progress phases, urgency tiers, and the shared built-in
/// China statutory holiday schedule (2025/2026 official, 2027 provisional).
/// </summary>
public sealed class CustomWidgetCountdownV3Tests
{
    private const string ZhCn = "zh-CN";

    private static DateTimeOffset At(int year, int month, int day, int hour = 10, int minute = 0) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    // ---------------------------------------------------------------- holiday schedule

    [Fact]
    public void HolidaySchedule_ClassifiesOfficial2025And2026Days()
    {
        Assert.Equal(CustomWidgetHolidayDayType.StatutoryHoliday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 1, 1)));
        Assert.Equal(CustomWidgetHolidayDayType.StatutoryHoliday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 2, 23)));
        Assert.Equal(CustomWidgetHolidayDayType.StatutoryHoliday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2025, 10, 8)));

        // Weekend swap-workdays declared by the official notices (both Sundays and Saturdays).
        Assert.Equal(CustomWidgetHolidayDayType.SwapWorkday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 1, 4)));
        Assert.Equal(CustomWidgetHolidayDayType.SwapWorkday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 9, 20)));
        Assert.Equal(CustomWidgetHolidayDayType.SwapWorkday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 10, 10)));
        Assert.Equal(CustomWidgetHolidayDayType.SwapWorkday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2025, 1, 26)));
        Assert.Equal(CustomWidgetHolidayDayType.SwapWorkday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2025, 2, 8)));

        // Regular days stay regular: Wednesday 2026-11-04 and plain weekends.
        Assert.Equal(CustomWidgetHolidayDayType.Ordinary, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 11, 4)));
        Assert.Equal(CustomWidgetHolidayDayType.Weekend, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 11, 7)));
        Assert.Equal(CustomWidgetHolidayDayType.Weekend, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2026, 9, 19)));

        // Beyond table coverage the classifier degrades to weekend detection only.
        Assert.Equal(CustomWidgetHolidayDayType.Weekend, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2028, 1, 1)));
        Assert.Equal(CustomWidgetHolidayDayType.Ordinary, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2028, 1, 3)));
    }

    [Fact]
    public void HolidaySchedule_TryGetNextHoliday_WalksBlocksAndCrossesYears()
    {
        // From "today" (2026-11-04, after National Day) the next block is the
        // provisional 2027 New Year — 58 days ahead.
        Assert.True(CustomWidgetHolidaySchedule.TryGetNextHoliday(new DateOnly(2026, 11, 4), out CustomWidgetHolidayEntry next));
        Assert.Equal(CustomWidgetHolidaySchedule.NameKeyNewYear, next.NameKey);
        Assert.Equal(new DateOnly(2027, 1, 1), next.Start);
        Assert.True(next.IsProvisional);
        Assert.Equal(58, next.Start.DayNumber - new DateOnly(2026, 11, 4).DayNumber);

        // Mid-Autumn 2026 (official, no swap days).
        Assert.True(CustomWidgetHolidaySchedule.TryGetNextHoliday(new DateOnly(2026, 8, 15), out CustomWidgetHolidayEntry midAutumn));
        Assert.Equal(CustomWidgetHolidaySchedule.NameKeyMidAutumn, midAutumn.NameKey);
        Assert.Equal(new DateOnly(2026, 9, 25), midAutumn.Start);
        Assert.False(midAutumn.IsProvisional);
        Assert.Empty(midAutumn.SwapWorkdays);

        // A holiday already in progress still matches (End >= from).
        Assert.True(CustomWidgetHolidaySchedule.TryGetNextHoliday(new DateOnly(2026, 10, 3), out CustomWidgetHolidayEntry national));
        Assert.Equal(new DateOnly(2026, 10, 1), national.Start);
        Assert.Equal(new DateOnly(2026, 10, 7), national.End);
        Assert.Equal(2, national.SwapWorkdays.Count);

        // Beyond the supported table there is no holiday claim.
        Assert.False(CustomWidgetHolidaySchedule.TryGetNextHoliday(new DateOnly(2028, 1, 1), out _));
    }

    [Fact]
    public void HolidaySchedule_2027ProvisionalLayer_MakesNoSwapClaims()
    {
        Assert.True(CustomWidgetHolidaySchedule.IsYearProvisional(2027));
        Assert.False(CustomWidgetHolidaySchedule.IsYearOfficial(2027));
        Assert.True(CustomWidgetHolidaySchedule.IsYearOfficial(2026));
        Assert.Contains("provisional", CustomWidgetHolidaySchedule.GetProvenance(2027), StringComparison.Ordinal);

        foreach (CustomWidgetHolidayEntry entry in CustomWidgetHolidaySchedule.GetEntries(2027))
        {
            Assert.True(entry.IsProvisional);
            Assert.Empty(entry.SwapWorkdays);
        }

        // Statutory-only spans: Spring Festival = 除夕 + 初一~初三 (02-05..02-08).
        CustomWidgetHolidayEntry spring = CustomWidgetHolidaySchedule.GetEntries(2027)
            .Single(e => e.NameKey == CustomWidgetHolidaySchedule.NameKeySpringFestival);
        Assert.Equal(new DateOnly(2027, 2, 5), spring.Start);
        Assert.Equal(new DateOnly(2027, 2, 8), spring.End);

        // 2027 statutory days are still holidays (law-fixed), never swap workdays.
        Assert.Equal(CustomWidgetHolidayDayType.StatutoryHoliday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2027, 1, 1)));
        Assert.Equal(CustomWidgetHolidayDayType.StatutoryHoliday, CustomWidgetHolidaySchedule.GetDayType(new DateOnly(2027, 10, 3)));
    }

    // ---------------------------------------------------------------- payday weekend rule

    [Theory]
    [InlineData("2026-01-10", "friday", "2026-01-09")] // Saturday -> Friday
    [InlineData("2026-01-11", "friday", "2026-01-09")] // Sunday -> Friday
    [InlineData("2026-01-10", "monday", "2026-01-12")] // Saturday -> Monday
    [InlineData("2026-01-11", "monday", "2026-01-12")] // Sunday -> Monday
    [InlineData("2026-01-08", "friday", "2026-01-08")] // weekday untouched
    [InlineData("2026-01-08", "monday", "2026-01-08")]
    [InlineData("2026-01-10", "none", "2026-01-10")]
    [InlineData("2026-01-10", "bogus", "2026-01-10")]
    public void AdjustForWeekendRule_MovesWeekendPaydaysOnly(string candidateIso, string rule, string expectedIso)
    {
        DateOnly candidate = DateOnly.Parse(candidateIso);
        DateOnly actual = CustomWidgetCountdownEngine.AdjustForWeekendRule(candidate, rule);
        Assert.Equal(DateOnly.Parse(expectedIso), actual);
    }

    [Fact]
    public void CountdownEngine_MonthlyPaydayAppliesWeekendRuleEndToEnd()
    {
        DateTimeOffset now = At(2026, 1, 6); // Tuesday before payday Saturday 2026-01-10

        CustomWidgetCountdownEventView fridayPayday = BuildMonthlyPayday(now, CustomWidgetCountdownEngine.WeekendRuleFriday);
        Assert.Equal(new DateOnly(2026, 1, 9), fridayPayday.ResolvedTargetDate);
        Assert.Equal(3, fridayPayday.DisplayDays);
        Assert.Equal(CountdownDirection.Countdown, fridayPayday.Direction);
        Assert.Equal("friday", fridayPayday.WeekendRule);

        CustomWidgetCountdownEventView mondayPayday = BuildMonthlyPayday(now, CustomWidgetCountdownEngine.WeekendRuleMonday);
        Assert.Equal(new DateOnly(2026, 1, 12), mondayPayday.ResolvedTargetDate);
        Assert.Equal(6, mondayPayday.DisplayDays);

        // Legacy default keeps v2.1 byte-identical targeting.
        CustomWidgetCountdownEventView plainPayday = BuildMonthlyPayday(now, null);
        Assert.Equal(new DateOnly(2026, 1, 10), plainPayday.ResolvedTargetDate);
    }

    [Fact]
    public void CountdownEngine_LegacyRecordWithoutWeekendRuleField_ResolvesLikeNone()
    {
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal)
        {
            [CustomWidgetCountdownEngine.CollectionName] =
            [
                new CustomWidgetRecord
                {
                    Id = "legacy1",
                    CreatedAtUtcTicks = At(2026, 1, 2).UtcTicks,
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["title"] = "房贷还款日",
                        ["emoji"] = "\uE8C7",
                        ["date"] = "2026-01-10",
                        ["calendar"] = "solar",
                        ["repeat"] = "monthly",
                        ["pinned"] = "1",
                        ["createdDate"] = "2026-01-02",
                        ["lunarLeap"] = "0"
                        // deliberately NO weekendRule field (v2.1 store.json shape)
                    }
                }
            ]
        };

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            collections,
            At(2026, 1, 6),
            cultureName: ZhCn);

        CustomWidgetCountdownEventView hero = Assert.IsType<CustomWidgetCountdownEventView>(snap.HeroEvent);
        Assert.Equal(new DateOnly(2026, 1, 10), hero.ResolvedTargetDate);
        Assert.Equal(CustomWidgetCountdownEngine.WeekendRuleNone, hero.WeekendRule);
    }

    [Fact]
    public void CountdownEngine_FridayPaydayLandingTodayStillRollsForwardNextMonth()
    {
        // Payday Sat 2026-01-10 with friday rule: on Friday 01-09 it is TODAY;
        // on Saturday 01-10 the adjusted Friday already passed -> next occurrence 2026-02-10.
        CustomWidgetCountdownEventView onAdjustedDay = BuildMonthlyPayday(At(2026, 1, 9), CustomWidgetCountdownEngine.WeekendRuleFriday);
        Assert.Equal(CountdownDirection.Today, onAdjustedDay.Direction);
        Assert.Equal(new DateOnly(2026, 1, 9), onAdjustedDay.ResolvedTargetDate);

        CustomWidgetCountdownEventView dayAfter = BuildMonthlyPayday(At(2026, 1, 10), CustomWidgetCountdownEngine.WeekendRuleFriday);
        Assert.Equal(new DateOnly(2026, 2, 10), dayAfter.ResolvedTargetDate);
        Assert.Equal(31, dayAfter.DisplayDays);
    }

    private static CustomWidgetCountdownEventView BuildMonthlyPayday(DateTimeOffset now, string? weekendRule)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);

        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "发薪日",
            emoji: "\uE8C7",
            dateIso: "2026-01-10",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatMonthly,
            pinned: true,
            isLunarLeap: false,
            now: now,
            cultureName: ZhCn,
            weekendRule: weekendRule);

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(state, settings, collections, now, cultureName: ZhCn);
        return Assert.IsType<CustomWidgetCountdownEventView>(snap.HeroEvent);
    }

    // ---------------------------------------------------------------- count-up milestones

    [Fact]
    public void CountdownEngine_ComputesDayCountAndAnniversaryMilestones()
    {
        // 289 days elapsed -> 300-day milestone in 11 days (beats the 76-day anniversary).
        Assert.True(CustomWidgetCountdownEngine.TryComputeNextMilestone(
            new DateOnly(2025, 8, 1), new DateOnly(2026, 5, 17), out bool isAnniv, out int number, out int days));
        Assert.False(isAnniv);
        Assert.Equal(300, number);
        Assert.Equal(11, days);

        // 725 days elapsed -> 2nd anniversary (5 days) beats the 1000-day marker (275 days).
        Assert.True(CustomWidgetCountdownEngine.TryComputeNextMilestone(
            new DateOnly(2024, 7, 25), new DateOnly(2026, 7, 20), out isAnniv, out number, out days));
        Assert.True(isAnniv);
        Assert.Equal(2, number);
        Assert.Equal(5, days);

        // Day 0 -> first marker 100 in 100 days.
        Assert.True(CustomWidgetCountdownEngine.TryComputeNextMilestone(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), out _, out number, out days));
        Assert.Equal(100, number);
        Assert.Equal(100, days);
    }

    [Fact]
    public void CountdownEngine_CountUpHeroSubtitleCarriesMilestoneHint()
    {
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "在一起",
            emoji: "❤️",
            dateIso: "2025-08-01",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: true,
            isLunarLeap: false,
            now: At(2026, 5, 17),
            cultureName: ZhCn);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["expiredMode"] = "countup" };
        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal), settings, collections, At(2026, 5, 17), cultureName: ZhCn);

        CustomWidgetCountdownEventView hero = Assert.IsType<CustomWidgetCountdownEventView>(snap.HeroEvent);
        Assert.Equal(289, hero.DisplayDays);
        Assert.Equal("距 300 天还有 11 天", hero.MilestoneText);
        Assert.Contains("距 300 天还有 11 天", hero.HeroSubtitle, StringComparison.Ordinal);

        // Archive mode dims the event and must not advertise milestones.
        settings["expiredMode"] = "archive";
        CustomWidgetCountdownSnapshot archived = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal), settings, collections, At(2026, 5, 17), cultureName: ZhCn);
        Assert.True(archived.HeroEvent!.IsArchivedExpired);
        Assert.Equal(string.Empty, archived.HeroEvent.MilestoneText);
    }

    [Fact]
    public void CountdownEngine_MilestoneImminenceHonorsUrgentDaysWindow()
    {
        // 11 days to the 300-day milestone: quiet under the default urgentDays=7 window...
        CustomWidgetCountdownEventView hero = BuildCountUpAnniversary(At(2026, 5, 17), urgentDays: null);
        Assert.Equal(11, hero.MilestoneDaysRemaining);
        Assert.False(hero.IsMilestoneImminent);
        Assert.DoesNotContain("距 300 天", hero.HeroBaseSubtitle, StringComparison.Ordinal);
        Assert.Equal($"{hero.HeroBaseSubtitle} · {hero.MilestoneText}", hero.HeroSubtitle);

        // ...warms up to amber inside the window, and widens with urgentDays=14.
        Assert.True(BuildCountUpAnniversary(At(2026, 5, 25), urgentDays: null).IsMilestoneImminent); // 2 days
        Assert.True(BuildCountUpAnniversary(At(2026, 5, 17), urgentDays: "14").IsMilestoneImminent); // 11 days
        Assert.False(BuildCountUpAnniversary(At(2026, 5, 17), urgentDays: "3").IsMilestoneImminent);

        // Countdown-direction and archived events never claim milestone imminence.
        var archivedSettings = new Dictionary<string, string>(StringComparer.Ordinal) { ["expiredMode"] = "archive" };
        CustomWidgetCountdownSnapshot archived = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            archivedSettings,
            BuildAnniversaryCollections(At(2026, 5, 17)),
            At(2026, 5, 17),
            cultureName: ZhCn);
        Assert.True(archived.HeroEvent!.IsArchivedExpired);
        Assert.Equal(0, archived.HeroEvent.MilestoneDaysRemaining);
        Assert.False(archived.HeroEvent.IsMilestoneImminent);
        Assert.Equal(archived.HeroEvent.HeroSubtitle, archived.HeroEvent.HeroBaseSubtitle);
    }

    private static Dictionary<string, List<CustomWidgetRecord>> BuildAnniversaryCollections(DateTimeOffset now)
    {
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "在一起",
            emoji: "❤️",
            dateIso: "2025-08-01",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: true,
            isLunarLeap: false,
            now: now,
            cultureName: ZhCn);
        return collections;
    }

    private static CustomWidgetCountdownEventView BuildCountUpAnniversary(DateTimeOffset now, string? urgentDays)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["expiredMode"] = "countup" };
        if (urgentDays is not null)
        {
            settings["urgentDays"] = urgentDays;
        }

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            settings,
            BuildAnniversaryCollections(now),
            now,
            cultureName: ZhCn);
        return Assert.IsType<CustomWidgetCountdownEventView>(snap.HeroEvent);
    }

    // ---------------------------------------------------------------- work-hour progress

    [Fact]
    public void CountdownEngine_ComputesWorkdayProgressPhases()
    {
        CustomWidgetWorkdayProgressSnapshot before = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 8, 59).DateTime,null, ZhCn);
        Assert.Equal(CustomWidgetWorkdayPhase.BeforeWork, before.Phase);
        Assert.Equal(0.0, before.Percent);
        Assert.Equal("09:00-18:00", before.RangeText);

        CustomWidgetWorkdayProgressSnapshot lunch = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 13, 30).DateTime,"09:00-18:00", ZhCn);
        Assert.Equal(CustomWidgetWorkdayPhase.Working, lunch.Phase);
        Assert.Equal(50.0, lunch.Percent, 2);
        Assert.Contains("已过 50%", lunch.Summary, StringComparison.Ordinal);

        CustomWidgetWorkdayProgressSnapshot after = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 19, 5).DateTime,"09:00-18:00", ZhCn);
        Assert.Equal(CustomWidgetWorkdayPhase.AfterWork, after.Phase);
        Assert.Equal(100.0, after.Percent);
        Assert.Contains("已圆满结束", after.Summary, StringComparison.Ordinal);

        CustomWidgetWorkdayProgressSnapshot allDay = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 12, 0).DateTime,"day24h", ZhCn);
        Assert.True(allDay.IsAllDay);
        Assert.Equal(CustomWidgetWorkdayPhase.Working, allDay.Phase);
        Assert.Equal(50.0, allDay.Percent, 2);

        // Garbage spec falls back to the default 09:00-18:00 window instead of crashing.
        CustomWidgetWorkdayProgressSnapshot fallback = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 10, 0).DateTime,"not-a-range", ZhCn);
        Assert.Equal("09:00-18:00", fallback.RangeText);
        Assert.Equal(CustomWidgetWorkdayPhase.Working, fallback.Phase);
    }

    [Fact]
    public void CountdownEngine_WorkdayProgressParsesCustomShifts()
    {
        CustomWidgetWorkdayProgressSnapshot early = CustomWidgetCountdownEngine.ComputeWorkdayProgress(
            At(2026, 11, 4, 12, 0).DateTime, "08:30-17:30", "en-US");
        Assert.Equal("08:30-17:30", early.RangeText);
        // 08:30 -> 12:00 = 210 min of the 540-min window.
        Assert.Equal(38.9, Math.Round(early.Percent, 1), 1);
        Assert.Equal("Working", early.Phase.ToString());
    }

    // ---------------------------------------------------------------- urgency tiers & holiday card

    [Fact]
    public void CountdownSnapshot_UrgencyTiersRespectUrgentDaysSetting()
    {
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        (string Title, string DateIso)[] fixtures =
        [
            ("近两日", "2026-11-06"),
            ("近五日", "2026-11-09"),
            ("近十日", "2026-11-14")
        ];
        foreach ((string title, string dateIso) in fixtures)
        {
            CustomWidgetCountdownEngine.UpsertEvent(
                collections,
                eventId: null,
                title,
                emoji: "\uE787",
                dateIso,
                calendar: CustomWidgetCountdownEngine.CalendarSolar,
                repeat: CustomWidgetCountdownEngine.RepeatNone,
                pinned: false,
                isLunarLeap: false,
                now: At(2026, 11, 4),
                cultureName: ZhCn);
        }

        // Default urgentDays = 7: coral <= 3 (kept from v2.1), amber 4..7 (new tier).
        CustomWidgetCountdownSnapshot def = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            collections,
            At(2026, 11, 4),
            cultureName: ZhCn);
        Assert.True(Assert.Single(def.AllEvents, e => e.Title == "近两日").IsUrgent);
        Assert.False(Assert.Single(def.AllEvents, e => e.Title == "近两日").IsApproaching);
        Assert.False(Assert.Single(def.AllEvents, e => e.Title == "近五日").IsUrgent);
        Assert.True(Assert.Single(def.AllEvents, e => e.Title == "近五日").IsApproaching);
        Assert.False(Assert.Single(def.AllEvents, e => e.Title == "近十日").IsApproaching);

        // urgentDays = 30 widens the amber window to include 10 days out.
        var wider = new Dictionary<string, string>(StringComparer.Ordinal) { ["urgentDays"] = "30" };
        CustomWidgetCountdownSnapshot snap30 = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal), wider, collections, At(2026, 11, 4), cultureName: ZhCn);
        Assert.True(Assert.Single(snap30.AllEvents, e => e.Title == "近十日").IsApproaching);
        Assert.False(Assert.Single(snap30.AllEvents, e => e.Title == "近十日").IsUrgent);

        // urgentDays = 3 keeps the pure v2.1 behavior (no amber tier at 5 days).
        var legacy = new Dictionary<string, string>(StringComparer.Ordinal) { ["urgentDays"] = "3" };
        CustomWidgetCountdownSnapshot snap3 = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal), legacy, collections, At(2026, 11, 4), cultureName: ZhCn);
        Assert.False(Assert.Single(snap3.AllEvents, e => e.Title == "近五日").IsApproaching);
    }

    [Fact]
    public void CountdownSnapshot_BuiltInHolidayCardAndTokens()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            state, settings, collections, At(2026, 11, 4, 10, 0), cultureName: ZhCn);

        Assert.NotNull(snap.NextHoliday);
        Assert.Equal("元旦", snap.NextHoliday!.Title);
        Assert.Equal(new DateOnly(2027, 1, 1), snap.NextHoliday.StartDate);
        Assert.Equal(58, snap.NextHoliday.DaysRemaining);
        Assert.True(snap.NextHoliday.IsProvisional);
        Assert.Equal(0, snap.NextHoliday.SwapWorkdayCount);
        Assert.Contains("以官方发布为准", snap.NextHoliday.Subtitle, StringComparison.Ordinal);

        // Official (2026) entries advertise their swap workdays in the subtitle.
        // From 09-10 the next block is Mid-Autumn (no swap days)...
        CustomWidgetCountdownSnapshot midAutumn = CustomWidgetCountdownEngine.ComputeSnapshot(
            state, settings, collections, At(2026, 9, 10), cultureName: ZhCn);
        Assert.NotNull(midAutumn.NextHoliday);
        Assert.Equal("中秋节", midAutumn.NextHoliday!.Title);
        Assert.Equal(0, midAutumn.NextHoliday.SwapWorkdayCount);

        // ...and after Mid-Autumn the National Day block carries 2 swap workdays.
        CustomWidgetCountdownSnapshot national = CustomWidgetCountdownEngine.ComputeSnapshot(
            state, settings, collections, At(2026, 9, 28), cultureName: ZhCn);
        Assert.NotNull(national.NextHoliday);
        Assert.Equal("国庆节", national.NextHoliday!.Title);
        Assert.Equal(2, national.NextHoliday.SwapWorkdayCount);
        Assert.Contains("补班 2 天", national.NextHoliday.Subtitle, StringComparison.Ordinal);

        // Toggle off hides the card entirely.
        var off = new Dictionary<string, string>(StringComparer.Ordinal) { ["showNextHoliday"] = "false" };
        CustomWidgetCountdownSnapshot hidden = CustomWidgetCountdownEngine.ComputeSnapshot(
            state, off, collections, At(2026, 11, 4), cultureName: ZhCn);
        Assert.Null(hidden.NextHoliday);

        // Tokens resolve for capsule/declarative use.
        var evalCtx = new CustomWidgetEvaluationContext
        {
            Settings = settings,
            State = state,
            Collections = collections,
            ClockOverride = At(2026, 11, 4, 10, 0),
            CultureName = ZhCn
        };
        Assert.Equal("元旦", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.holiday.title}", evalCtx));
        Assert.Equal("58", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.holiday.days}", evalCtx));
        // 09:00-18:00 at 10:00 -> 60/540 minutes (UTC+8 wall clock, matching the suite's convention).
        Assert.Equal(
            snap.Workday.Percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            CustomWidgetExpressionEvaluator.Interpolate("${$countdown.work.percent}", evalCtx));
        Assert.Equal("Working", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.work.phase}", evalCtx));
        Assert.Equal("84.4", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.year.percent}", evalCtx));
    }

    [Fact]
    public void CountdownSnapshot_WorkdayProgressReadsWorkHoursSetting()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["workHours"] = "08:30-17:30" };
        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(StringComparer.Ordinal),
            settings,
            new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal),
            At(2026, 11, 4, 12, 0),
            cultureName: ZhCn);

        Assert.Equal("08:30-17:30", snap.Workday.RangeText);
        Assert.Equal(38.9, Math.Round(snap.Workday.Percent, 1), 1);

        // Week progress: 2026-11-04 is a Wednesday -> Monday-first index 3.
        Assert.Equal(3, snap.TimeProgress.WeekDayIndex);
    }

    // ---------------------------------------------------------------- manifest settings surface

    [Fact]
    public void CountdownPreset_V3DeclaresNewSettingsConsumedByEngine()
    {
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.CountdownTemplateId);
        CustomWidgetParseResult parsed = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        Assert.Empty(parsed.Diagnostics);

        CustomWidgetManifest manifest = parsed.Manifest!;
        Assert.Equal("3.0.0", manifest.Version);

        CustomWidgetSettingDefinition modules = Assert.Single(manifest.Settings, s => s.Key == "modules");
        Assert.Equal("hero:1,list:1,yearProgress:1", modules.DefaultValue);

        CustomWidgetSettingDefinition progressScope = Assert.Single(manifest.Settings, s => s.Key == "progressScope");
        Assert.Equal("workAndYear", progressScope.DefaultValue);
        Assert.Contains(progressScope.Options, o => o.Value == "allFour");
        Assert.Contains(progressScope.Options, o => o.Value == "year");

        CustomWidgetSettingDefinition workHours = Assert.Single(manifest.Settings, s => s.Key == "workHours");
        Assert.Equal("09:00-18:00", workHours.DefaultValue);
        Assert.Contains(workHours.Options, o => o.Value == "day24h");

        CustomWidgetSettingDefinition urgentDays = Assert.Single(manifest.Settings, s => s.Key == "urgentDays");
        Assert.Equal("7", urgentDays.DefaultValue);

        CustomWidgetSettingDefinition showNextHoliday = Assert.Single(manifest.Settings, s => s.Key == "showNextHoliday");
        Assert.Equal("toggle", showNextHoliday.Type);
        Assert.Equal("true", showNextHoliday.DefaultValue);
    }
}
