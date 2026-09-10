extern alias GlancePkg;

using System.Globalization;
using DeskBox.Services;
using Navigation = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceCalendarNavigationPolicy;
using Pipeline = GlancePkg::DeskBox.GlancePackage.Rendering.GlanceMonthPipeline;
using Mode = GlancePkg::DeskBox.Models.GlanceTraditionalCalendarMode;

namespace DeskBox.Tests;

/// <summary>Pure navigation and production month-pipeline coverage; no CalendarView activation.</summary>
public sealed class NativeGlanceCalendarNavigationTests
{
    [Theory]
    [InlineData(120, 2026, 7)]
    [InlineData(-120, 2026, 9)]
    [InlineData(960, 2026, 7)]
    [InlineData(-960, 2026, 9)]
    [InlineData(1, 2026, 7)]
    [InlineData(-1, 2026, 9)]
    [InlineData(0, 2026, 8)]
    public void WheelMovesOneWholeMonthRegardlessOfDeltaMagnitude(int delta, int year, int month)
    {
        DateOnly current = new(2026, 8, 19);
        DateOnly actual = Navigation.ResolveWheelTarget(current, delta);
        Assert.Equal(new DateOnly(year, month, 1), actual);
        Assert.Equal(GlanceCalendarNavigationResolver.ResolveWheelTarget(
            current, delta, new DateOnly(1900, 1, 1), new DateOnly(2100, 12, 1)), actual);
    }

    [Fact]
    public void WheelCrossesYearBoundariesAndClampsBothEnds()
    {
        Assert.Equal(new DateOnly(2025, 12, 1), Navigation.ResolveWheelTarget(new(2026, 1, 31), 120));
        Assert.Equal(new DateOnly(2027, 1, 1), Navigation.ResolveWheelTarget(new(2026, 12, 31), -120));
        Assert.Equal(new DateOnly(1900, 1, 1), Navigation.ResolveWheelTarget(new(1900, 1, 1), 120));
        Assert.Equal(new DateOnly(2100, 12, 1), Navigation.ResolveWheelTarget(new(2100, 12, 31), -120));
        Assert.Equal(new DateOnly(1900, 1, 1), Navigation.ResolveWheelTarget(DateOnly.MinValue, int.MaxValue));
        Assert.Equal(new DateOnly(2100, 12, 1), Navigation.ResolveWheelTarget(DateOnly.MaxValue, int.MinValue));
        Assert.Equal(new DateOnly(1900, 1, 1), Navigation.ResolveWheelTarget(DateOnly.MinValue, 0));
        Assert.Equal(new DateOnly(2100, 12, 1), Navigation.ClampMonth(DateOnly.MaxValue));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("zh-CN")]
    [InlineData("ar-SA")]
    public async Task VisibleMonthMatchesBuiltInForEveryMonthAndCultureWeekStart(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var source = new LocalCalendarPresentationSource();
        // Includes leap February and grids crossing December / January.
        for (int year = 2024; year <= 2026; year++)
        for (int month = 1; month <= 12; month++)
        {
            DateOnly requested = new(year, month, 1);
            var grid = await source.GetMonthAsync(requested, culture);
            DateOnly[] visible = grid.Days.Select(day => day.Date).ToArray();
            DateOnly fallback = requested.AddMonths(-1);
            Assert.Equal(requested, Navigation.ResolveDisplayedMonth(visible, fallback));
            Assert.Equal(GlanceCalendarNavigationResolver.ResolveDisplayedMonth(visible, fallback),
                Navigation.ResolveDisplayedMonth(visible, fallback));
        }
    }

    [Fact]
    public void VisibleMonthTieUsesDistanceFromExistingMonth()
    {
        DateOnly[] visible = [new(2026, 8, 30), new(2026, 8, 31), new(2026, 9, 1), new(2026, 9, 2)];
        Assert.Equal(new DateOnly(2026, 9, 1), Navigation.ResolveDisplayedMonth(visible, new(2026, 9, 18)));
        Assert.Equal(new DateOnly(2026, 8, 1), Navigation.ResolveDisplayedMonth(visible, new(2026, 7, 18)));
    }

    [Fact]
    public void EmptyOrRecycledVisibleDatesCannotEscapeRangeOrOvercountOneDate()
    {
        Assert.Equal(new DateOnly(2027, 3, 1), Navigation.ResolveDisplayedMonth([], new(2027, 3, 18)));
        Assert.Equal(new DateOnly(1900, 1, 1), Navigation.ResolveDisplayedMonth([], DateOnly.MinValue));
        Assert.Equal(new DateOnly(2100, 12, 1), Navigation.ResolveDisplayedMonth([DateOnly.MaxValue], DateOnly.MaxValue));
        DateOnly[] visible = [new(2026, 8, 31), new(2026, 8, 31), new(2026, 8, 31), new(2026, 9, 1), new(2026, 9, 2)];
        Assert.Equal(new DateOnly(2026, 9, 1), Navigation.ResolveDisplayedMonth(visible, new(2026, 8, 1)));
    }

    [Theory]
    [InlineData(0, 0, 40, 30, true)]
    [InlineData(-20, 0, 40, 30, true)]
    [InlineData(0, -15, 40, 30, true)]
    [InlineData(279, 199, 40, 30, true)]
    [InlineData(-40, 0, 40, 30, false)]
    [InlineData(0, -30, 40, 30, false)]
    [InlineData(280, 0, 40, 30, false)]
    [InlineData(0, 200, 40, 30, false)]
    [InlineData(0, 0, 0, 30, false)]
    [InlineData(0, 0, 40, 0, false)]
    public void VisibilityUsesIntersectionInsteadOfAllRealizedDays(
        double left, double top, double width, double height, bool expected)
    {
        Assert.Equal(expected, Navigation.IsVisible(left, top, width, height, 280, 200));
    }

    [Fact]
    public void InvalidOrUnmeasuredBoundsDoNotCountAsVisible()
    {
        Assert.False(Navigation.IsVisible(double.NaN, 0, 40, 30, 280, 200));
        Assert.False(Navigation.IsVisible(0, 0, double.PositiveInfinity, 30, 280, 200));
        Assert.False(Navigation.IsVisible(0, 0, 40, 30, 0, 200));
    }

    [Theory]
    [InlineData("en-US", DayOfWeek.Sunday)]
    [InlineData("en-GB", DayOfWeek.Monday)]
    public void CultureControlsFirstDayOfWeek(string language, DayOfWeek expected)
    {
        Assert.Equal(expected, Navigation.ResolveFirstDayOfWeek(CultureInfo.GetCultureInfo(language)));
    }

    [Fact]
    public void FirstDayHonorsTheSuppliedCultureIncludingCustomSettings()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Saturday;
        Assert.Equal(DayOfWeek.Saturday, Navigation.ResolveFirstDayOfWeek(culture));
    }

    [Fact]
    public void TitleUsesTodayOnlyWithinTheDisplayedGregorianMonth()
    {
        DateOnly today = new(2026, 9, 10);
        Assert.Equal(today, Navigation.ResolveTitleDate(new(2026, 9, 28), today));
        Assert.Equal(new DateOnly(2026, 8, 15), Navigation.ResolveTitleDate(new(2026, 8, 31), today));
        Assert.Equal(new DateOnly(2025, 9, 15), Navigation.ResolveTitleDate(new(2025, 9, 1), today));
        Assert.Equal(new DateOnly(2024, 2, 15), Navigation.ResolveTitleDate(new(2024, 2, 29), today));
    }

    [Fact]
    public void OmittedMonthKeepsCurrentMonthAndTodayTitle()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        DateOnly before = DateOnly.FromDateTime(DateTime.Today);
        var result = Pipeline.Build(true, Mode.ChineseLunar, culture, 440, 560);
        DateOnly after = DateOnly.FromDateTime(DateTime.Today);
        var service = new GlanceTraditionalCalendarService();
        // Allow the wall clock to cross midnight during the call.
        Assert.Contains(result.Month.Month, new[] { new DateOnly(before.Year, before.Month, 1), new DateOnly(after.Year, after.Month, 1) });
        Assert.Contains(result.Month.TraditionalTitle, new[]
        {
            service.FormatTitle(before, DeskBox.Models.GlanceTraditionalCalendarMode.ChineseLunar, culture),
            service.FormatTitle(after, DeskBox.Models.GlanceTraditionalCalendarMode.ChineseLunar, culture),
        });
    }

    [Fact]
    public void SuppliedDateIsOneSnapshotForMonthTitleAndTodayMarker()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        DateOnly snapshot = new(2030, 1, 1);
        var result = Pipeline.Build(true, Mode.ChineseLunar, culture, 440, 560,
            displayedMonth: null, currentDate: snapshot);

        Assert.Equal(new DateOnly(2030, 1, 1), result.Month.Month);
        Assert.Equal(snapshot, Assert.Single(result.Month.Days, day => day.IsToday).Date);
        Assert.Equal(new GlanceTraditionalCalendarService().FormatTitle(
            snapshot, DeskBox.Models.GlanceTraditionalCalendarMode.ChineseLunar, culture),
            result.Month.TraditionalTitle);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    public async Task BrowsedMonthPipelineMatchesBuiltInCalendarTraditionalAndFestivalData(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly requested = today.Year == 2024 && today.Month == 2 ? new(2025, 2, 1) : new(2024, 2, 1);
        var result = Pipeline.Build(true, Mode.Auto, culture, 440, 560, requested.AddDays(20));

        var traditional = new GlanceTraditionalCalendarService();
        var mode = traditional.ResolveMode(DeskBox.Models.GlanceTraditionalCalendarMode.Auto, culture.Name);
        var expected = await new LocalCalendarPresentationSource().GetMonthAsync(requested, culture);
        expected = traditional.Apply(expected, mode, culture, requested.AddDays(14));
        expected = new GlanceFestivalService().Apply(expected, true, mode, culture);

        Assert.Equal(requested, result.Month.Month);
        Assert.Equal(expected.TraditionalTitle, result.Month.TraditionalTitle);
        Assert.Equal(expected.WeekdayHeaders, result.Month.WeekdayHeaders);
        Assert.Equal(expected.Days.Select(day => (day.Date, day.DayText, day.IsCurrentMonth, day.TraditionalText, day.FestivalText)),
            result.Month.Days.Select(day => (day.Date, day.DayText, day.IsCurrentMonth, day.TraditionalText, day.FestivalText)));
    }

    [Fact]
    public void CultureAndFestivalToggleRebuildTheBrowsedMonth()
    {
        DateOnly month = new(2024, 2, 1);
        var chinese = Pipeline.Build(true, Mode.Auto, CultureInfo.GetCultureInfo("zh-CN"), 440, 560, month);
        Assert.Equal("春节", chinese.Month.Days.Single(day => day.Date == new DateOnly(2024, 2, 10)).FestivalText);
        var disabled = Pipeline.Build(false, Mode.Auto, CultureInfo.GetCultureInfo("zh-CN"), 440, 560, month);
        Assert.All(disabled.Month.Days, day => Assert.Empty(day.FestivalText));
        Assert.Equal(chinese.Month.TraditionalTitle, disabled.Month.TraditionalTitle);
        var english = Pipeline.Build(true, Mode.Auto, CultureInfo.GetCultureInfo("en-GB"), 440, 560, month);
        Assert.Equal(month, english.Month.Month);
        Assert.Equal(Mode.None, english.EffectiveMode);
        Assert.Empty(english.Month.TraditionalTitle);
        Assert.All(english.Month.Days, day => Assert.Empty(day.TraditionalText));
        Assert.All(english.Month.Days, day => Assert.Empty(day.FestivalText));
        Assert.Equal(DayOfWeek.Monday, english.Month.Days[0].Date.DayOfWeek);
    }

    [Theory]
    [InlineData(1, 1, 1900, 1)]
    [InlineData(9999, 12, 2100, 12)]
    public void PipelineClampsToTheSameSupportedRange(int year, int month, int expectedYear, int expectedMonth)
    {
        var result = Pipeline.Build(false, Mode.None, CultureInfo.GetCultureInfo("en-US"), 440, 560, new DateOnly(year, month, 1));
        Assert.Equal(new DateOnly(expectedYear, expectedMonth, 1), result.Month.Month);
        Assert.Equal(42, result.Month.Days.Count);
    }
}
