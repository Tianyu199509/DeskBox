using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class GlanceCalendarDayChangePolicyTests
{
    private static readonly DateOnly October2026 = new(2026, 10, 1);
    private static readonly DateOnly November2026 = new(2026, 11, 1);

    [Fact]
    public void SameDate_DoesNotRequireRefresh()
    {
        GlanceCalendarDayChangeDecision decision = GlanceCalendarDayChangePolicy.Evaluate(
            previousDate: new DateOnly(2026, 10, 10),
            currentDate: new DateOnly(2026, 10, 10),
            displayedMonth: October2026);

        Assert.False(decision.CalendarRefreshRequired);
        Assert.Equal(October2026, decision.DisplayedMonth);
        Assert.False(decision.DisplayedMonthChanged(October2026));
    }

    [Fact]
    public void DayChangeWithinMonth_RefreshesWithoutTouchingBrowsedMonth()
    {
        GlanceCalendarDayChangeDecision decision = GlanceCalendarDayChangePolicy.Evaluate(
            previousDate: new DateOnly(2026, 10, 10),
            currentDate: new DateOnly(2026, 10, 11),
            displayedMonth: October2026);

        Assert.True(decision.CalendarRefreshRequired);
        Assert.Equal(October2026, decision.DisplayedMonth);
        Assert.False(decision.DisplayedMonthChanged(October2026));
    }

    [Fact]
    public void MonthChangeWhileBrowsingPreviousMonth_FollowsIntoNewMonth()
    {
        GlanceCalendarDayChangeDecision decision = GlanceCalendarDayChangePolicy.Evaluate(
            previousDate: new DateOnly(2026, 10, 31),
            currentDate: new DateOnly(2026, 11, 1),
            displayedMonth: October2026);

        Assert.True(decision.CalendarRefreshRequired);
        Assert.Equal(November2026, decision.DisplayedMonth);
        Assert.True(decision.DisplayedMonthChanged(October2026));
    }

    [Fact]
    public void MonthChangeWhileBrowsingAnotherMonth_KeepsUserBrowsing()
    {
        // The user is viewing August; midnight leaves October. Their browsing
        // intent survives the tick.
        GlanceCalendarDayChangeDecision decision = GlanceCalendarDayChangePolicy.Evaluate(
            previousDate: new DateOnly(2026, 10, 31),
            currentDate: new DateOnly(2026, 11, 1),
            displayedMonth: new DateOnly(2026, 8, 1));

        Assert.True(decision.CalendarRefreshRequired);
        Assert.Equal(new DateOnly(2026, 8, 1), decision.DisplayedMonth);
        Assert.False(decision.DisplayedMonthChanged(new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void MultiDayJumpWhileHidden_StillRefreshesOnReveal()
    {
        GlanceCalendarDayChangeDecision decision = GlanceCalendarDayChangePolicy.Evaluate(
            previousDate: new DateOnly(2026, 10, 1),
            currentDate: new DateOnly(2026, 10, 5),
            displayedMonth: October2026);

        Assert.True(decision.CalendarRefreshRequired);
        Assert.Equal(October2026, decision.DisplayedMonth);
    }
}

public sealed class GlanceCalendarDayDecorationBuilderTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);
    private static readonly DateOnly Tomorrow = new(2026, 10, 11);

    [Fact]
    public void TodayFlag_FollowsInjectedClock()
    {
        GlanceCalendarDayDecoration todayBefore =
            Build(date: Today, today: Today);
        GlanceCalendarDayDecoration todayAfter =
            Build(date: Today, today: Tomorrow);
        GlanceCalendarDayDecoration tomorrowAfter =
            Build(date: Tomorrow, today: Tomorrow);

        Assert.True(todayBefore.IsToday);
        Assert.False(todayAfter.IsToday);
        Assert.True(tomorrowAfter.IsToday);
        // The decoration record is the single Tag the template binds to; a
        // day rollover must produce a new record or the realized day item
        // keeps the stale visual (background and text together).
        Assert.NotEqual(todayBefore, todayAfter);
    }

    [Fact]
    public void SecondaryText_FestivalWinsOverTraditionalAndHidesWhenDisabled()
    {
        var day = new GlanceCalendarDay(
            Today,
            "10",
            IsCurrentMonth: true,
            IsToday: false,
            TraditionalText: "初一",
            FestivalText: "国庆节");

        Assert.Equal("国庆节", GlanceCalendarDayDecorationBuilder.Build(
                day, Today, Today, showTraditionalDetails: true, October(), "en-US")
            .SecondaryText);
        Assert.Equal("初一", GlanceCalendarDayDecorationBuilder.Build(
                new GlanceCalendarDay(Today, "10", true, false, "初一", ""),
                Today, Today, showTraditionalDetails: true, October(), "en-US")
            .SecondaryText);
        Assert.Equal(string.Empty, GlanceCalendarDayDecorationBuilder.Build(
                day, Today, Today, showTraditionalDetails: false, October(), "en-US")
            .SecondaryText);
    }

    [Fact]
    public void FallbackDayText_UsesCalendarLanguageWhenSourceDayMissing()
    {
        GlanceCalendarDayDecoration decoration = GlanceCalendarDayDecorationBuilder.Build(
            day: null,
            date: new DateOnly(2026, 10, 9),
            today: Today,
            showTraditionalDetails: false,
            displayedCalendarMonth: October(),
            calendarLanguage: "en-US");

        Assert.Equal("9", decoration.DayText);
        Assert.False(decoration.IsToday);
    }

    [Fact]
    public void OpacityMatrix_FollowsScopeAndFestival()
    {
        var inScopePlain = new GlanceCalendarDay(Today, "10", true, false);
        var inScopeFestival = new GlanceCalendarDay(Today, "10", true, false, "", "中秋");
        var outOfScope = new GlanceCalendarDay(Today, "10", false, false);

        Assert.Equal(1.0, Build(inScopePlain).PrimaryOpacity);
        Assert.Equal(0.62, Build(inScopePlain).SecondaryOpacity);
        Assert.Equal(0.88, Build(inScopeFestival).SecondaryOpacity);
        Assert.Equal(0.42, Build(outOfScope).PrimaryOpacity);
        Assert.Equal(0.34, Build(outOfScope).SecondaryOpacity);
    }

    private static GlanceCalendarDayDecoration Build(
        GlanceCalendarDay? day = null,
        DateOnly date = default,
        DateOnly today = default) =>
        GlanceCalendarDayDecorationBuilder.Build(
            day,
            date == default ? Today : date,
            today == default ? Today : today,
            showTraditionalDetails: true,
            October(),
            "en-US");

    private static DateOnly October() => new(2026, 10, 1);
}
