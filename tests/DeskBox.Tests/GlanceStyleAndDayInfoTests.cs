using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Glance v11: style as the source of truth, size adaptation, statutory
/// holiday marks and the To-do agenda bridge.
/// </summary>
public sealed class GlanceStyleAndDayInfoTests
{
    [Theory]
    [InlineData(GlanceLayoutMode.Immersive, GlanceStyle.PhotoClock, GlancePhotoClockAlignment.BottomLeft)]
    [InlineData(GlanceLayoutMode.Centered, GlanceStyle.PhotoClock, GlancePhotoClockAlignment.Center)]
    [InlineData(GlanceLayoutMode.Editorial, GlanceStyle.Editorial, GlancePhotoClockAlignment.Center)]
    [InlineData(GlanceLayoutMode.Calendar, GlanceStyle.MonthCalendar, GlancePhotoClockAlignment.Center)]
    public void LegacyLayout_MapsOntoStyle(
        GlanceLayoutMode layout,
        GlanceStyle expectedStyle,
        GlancePhotoClockAlignment expectedAlignment)
    {
        var settings = new GlanceWidgetData();

        GlanceWidgetSettingsPolicy.SetLayout(settings, layout);

        Assert.Equal(expectedStyle, settings.Style);
        Assert.Equal(expectedAlignment, settings.PhotoClockAlignment);
        Assert.Equal(layout, settings.Layout);
        Assert.Equal(layout == GlanceLayoutMode.Calendar, settings.ShowCalendar);
    }

    [Theory]
    [InlineData(GlanceStyle.TodayCard, GlanceLayoutMode.Centered, false)]
    [InlineData(GlanceStyle.WeekStrip, GlanceLayoutMode.Centered, false)]
    [InlineData(GlanceStyle.MonthCalendar, GlanceLayoutMode.Calendar, true)]
    [InlineData(GlanceStyle.Editorial, GlanceLayoutMode.Editorial, false)]
    public void Style_KeepsLegacyMirrorsReadableByOlderBuilds(
        GlanceStyle style,
        GlanceLayoutMode expectedLayout,
        bool expectedShowCalendar)
    {
        var settings = new GlanceWidgetData();

        GlanceWidgetSettingsPolicy.SetStyle(settings, style);

        Assert.Equal(style, settings.Style);
        Assert.Equal(expectedLayout, settings.Layout);
        Assert.Equal(expectedShowCalendar, settings.ShowCalendar);
    }

    [Fact]
    public async Task Store_MigratesVersion10LayoutToStyleOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "glance.json");
            await File.WriteAllTextAsync(path, """
                {
                  "version": 10,
                  "layout": "Calendar",
                  "showCalendar": true
                }
                """);
            var store = new GlanceWidgetStore(root);

            GlanceWidgetData migrated = await store.LoadAsync();

            Assert.Equal(GlanceWidgetData.CurrentVersion, migrated.Version);
            Assert.Equal(GlanceStyle.MonthCalendar, migrated.Style);
            Assert.True(migrated.ShowStatutoryHolidays);
            Assert.True(migrated.ShowTodoAgenda);

            // Once on v11 the style wins even if a stale mirror disagrees.
            migrated.Style = GlanceStyle.TodayCard;
            await store.SaveAsync(migrated);
            GlanceWidgetData reloaded = await new GlanceWidgetStore(root).LoadAsync();
            Assert.Equal(GlanceStyle.TodayCard, reloaded.Style);
            Assert.Equal(GlanceLayoutMode.Centered, reloaded.Layout);
            Assert.False(reloaded.ShowCalendar);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(360, 260, GlanceStyle.MonthCalendar)]
    [InlineData(360, 180, GlanceStyle.WeekStrip)]
    [InlineData(220, 300, GlanceStyle.WeekStrip)]
    public void MonthCalendar_DegradesToWeekStripWhenTooSmall(double width, double height, GlanceStyle expected)
    {
        Assert.Equal(
            expected,
            GlanceStyleLayoutCalculator.ResolveEffectiveStyle(GlanceStyle.MonthCalendar, width, height));
        Assert.Equal(
            GlanceStyle.TodayCard,
            GlanceStyleLayoutCalculator.ResolveEffectiveStyle(GlanceStyle.TodayCard, width, height));
    }

    [Fact]
    public void TodayCard_ScalesAgendaWithHeight()
    {
        Assert.Equal(0, GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(360, 180));
        Assert.Equal(1, GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(360, 240));
        Assert.Equal(2, GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(360, 300));
        Assert.Equal(3, GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(360, 360));
        Assert.Equal(0, GlanceStyleLayoutCalculator.CalculateTodayCardAgendaCapacity(180, 400));
        Assert.True(
            GlanceStyleLayoutCalculator.CalculateTodayCardDayFontSize(400, 400, 1) >
            GlanceStyleLayoutCalculator.CalculateTodayCardDayFontSize(200, 200, 1));
    }

    [Theory]
    [InlineData(2026, 10, 1, GlanceHolidayMark.Off)]
    [InlineData(2026, 10, 10, GlanceHolidayMark.Work)]
    [InlineData(2026, 2, 14, GlanceHolidayMark.Work)]
    [InlineData(2026, 11, 9, GlanceHolidayMark.None)]
    [InlineData(2026, 11, 8, GlanceHolidayMark.None)]
    public void HolidayMarks_FollowTheOfficialSchedule(int year, int month, int day, GlanceHolidayMark expected)
    {
        Assert.Equal(expected, GlanceHolidayMarkService.GetMark(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh-TW", false)]
    [InlineData("en-US", false)]
    [InlineData("ja-JP", false)]
    public void HolidayMarks_OnlyApplyToSimplifiedChinese(string culture, bool expected)
    {
        Assert.Equal(expected, GlanceHolidayMarkService.IsApplicable(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public async Task HolidayMarks_DecorateMonthAndCanBeDisabled()
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        GlanceCalendarMonth month = await new LocalCalendarPresentationSource()
            .GetMonthAsync(new DateOnly(2026, 10, 1), culture);

        GlanceCalendarMonth marked = GlanceHolidayMarkService.Apply(month, enabled: true, culture);
        GlanceCalendarDay nationalDay = Assert.Single(marked.Days, day => day.Date == new DateOnly(2026, 10, 1));
        GlanceCalendarDay swap = Assert.Single(marked.Days, day => day.Date == new DateOnly(2026, 10, 10));
        Assert.Equal(GlanceHolidayMark.Off, nationalDay.HolidayMark);
        Assert.False(string.IsNullOrWhiteSpace(nationalDay.HolidayName));
        Assert.Equal(GlanceHolidayMark.Work, swap.HolidayMark);

        GlanceCalendarMonth disabled = GlanceHolidayMarkService.Apply(month, enabled: false, culture);
        Assert.All(disabled.Days, day => Assert.Equal(GlanceHolidayMark.None, day.HolidayMark));
        GlanceCalendarMonth english = GlanceHolidayMarkService.Apply(month, enabled: true, CultureInfo.GetCultureInfo("en-US"));
        Assert.All(english.Days, day => Assert.Equal(GlanceHolidayMark.None, day.HolidayMark));
    }

    [Fact]
    public void HolidayCountdown_CountsToTheNextBlockOrReportsTheCurrentDay()
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");

        GlanceHolidayCountdown? before = GlanceHolidayMarkService.GetCountdown(new DateOnly(2026, 9, 28), culture);
        Assert.NotNull(before);
        Assert.False(before!.IsInProgress);
        Assert.Equal(3, before.DaysUntil);

        GlanceHolidayCountdown? during = GlanceHolidayMarkService.GetCountdown(new DateOnly(2026, 10, 3), culture);
        Assert.NotNull(during);
        Assert.True(during!.IsInProgress);
        Assert.Equal(3, during.DayOfBreak);
    }

    [Fact]
    public void TodoAgenda_ProjectsOpenDueItemsAndOverdueOnes()
    {
        DateOnly today = new(2026, 11, 8);
        DateTimeOffset At(int day, int hour) =>
            new DateTimeOffset(new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Local));
        TodoItem[] items =
        [
            new() { Id = "due-today", Text = "Ship build", DueDate = At(8, 15) },
            new() { Id = "overdue", Text = "Reply mail", DueDate = At(5, 9) },
            new() { Id = "future", Text = "Dentist", DueDate = At(12, 10), HasDueTime = false },
            new() { Id = "done", Text = "Done", DueDate = At(8, 9), IsCompleted = true },
            new() { Id = "deleted", Text = "Gone", DueDate = At(8, 9), IsDeleted = true },
            new() { Id = "undated", Text = "Someday" },
            new() { Id = "outside", Text = "Next month", DueDate = At(30, 9).AddMonths(1) }
        ];

        IReadOnlyList<GlanceAgendaItem> agenda = GlanceTodoAgendaSource.Sort(
            GlanceTodoAgendaSource.Project(items, "todo-1", today, new DateOnly(2026, 12, 5), includeOverdueBefore: today));

        Assert.Equal(["overdue", "due-today", "future"], agenda.Select(item => item.Id).ToArray());
        Assert.Equal(new TimeOnly(15, 0), agenda[1].Time);
        Assert.Null(agenda[2].Time);
        Assert.All(agenda, item => Assert.Equal("todo-1", item.SourceWidgetId));
    }

    [Fact]
    public async Task DisplaySeconds_CouplesWithTimeAndNormalizesInStore()
    {
        Assert.Contains(GlanceDisplayElement.Seconds, GlanceWidgetSettingsPolicy.SelectableDisplayElements);
        var settings = new GlanceWidgetData();
        Assert.False(settings.ShowSeconds);
        Assert.False(GlanceWidgetSettingsPolicy.IsDisplayElementVisible(settings, GlanceDisplayElement.Seconds));

        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Time, false);
        Assert.False(settings.ShowTime);

        // Turning Seconds on automatically re-enables Time.
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Seconds, true);
        Assert.True(settings.ShowTime);
        Assert.True(settings.ShowSeconds);
        Assert.True(GlanceWidgetSettingsPolicy.IsDisplayElementVisible(settings, GlanceDisplayElement.Seconds));

        // Turning Time off automatically clears Seconds.
        GlanceWidgetSettingsPolicy.SetDisplayElement(settings, GlanceDisplayElement.Time, false);
        Assert.False(settings.ShowTime);
        Assert.False(settings.ShowSeconds);

        // Direct store writes with ShowTime=false clear stale ShowSeconds=true.
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GlanceWidgetStore(root);
            await store.SaveAsync(new GlanceWidgetData { ShowTime = false, ShowSeconds = true });
            GlanceWidgetData reloaded = await store.LoadAsync();
            Assert.False(reloaded.ShowTime);
            Assert.False(reloaded.ShowSeconds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FormatTimeText_FormatsSecondsAcrossAllModesAndScalesWidth()
    {
        var sample = new DateTime(2026, 11, 8, 21, 5, 7);
        CultureInfo zhCn = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");

        Assert.Equal(
            "21:05:07",
            DeskBox.ViewModels.GlanceWidgetViewModel.FormatTimeText(
                sample,
                GlanceTimeFormatMode.Hour24,
                zhCn,
                includeSeconds: true));
        Assert.Equal(
            "9:05:07",
            DeskBox.ViewModels.GlanceWidgetViewModel.FormatTimeText(
                sample,
                GlanceTimeFormatMode.Hour12,
                enUs,
                includeSeconds: true));
        Assert.Equal(
            "9:05:07",
            DeskBox.ViewModels.GlanceWidgetViewModel.FormatTimeText(
                sample,
                GlanceTimeFormatMode.FollowSystem,
                zhCn,
                systemCulture: enUs,
                includeSeconds: true));

        var customNoSeconds = (CultureInfo)enUs.Clone();
        customNoSeconds.DateTimeFormat.LongTimePattern = "HH:mm";
        Assert.Equal(
            "21:05:07",
            DeskBox.ViewModels.GlanceWidgetViewModel.FormatTimeText(
                sample,
                GlanceTimeFormatMode.FollowSystem,
                zhCn,
                systemCulture: customNoSeconds,
                includeSeconds: true));

        double withoutSeconds = GlanceStyleLayoutCalculator.CalculateWeekStripTimeFontSize(280, 180, 1.0, includeSeconds: false);
        double withSeconds = GlanceStyleLayoutCalculator.CalculateWeekStripTimeFontSize(280, 180, 1.0, includeSeconds: true);
        Assert.True(withSeconds < withoutSeconds);
    }

    [Fact]
    public async Task Store_MigratesPreVersion10ShowCalendarAndRefreshesStaleCacheAfterExternalWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "glance.json");
            await File.WriteAllTextAsync(path, """
                {
                  "version": 9,
                  "layout": "Immersive",
                  "showCalendar": true
                }
                """);

            var settingsHostStore = new GlanceWidgetStore(root);
            GlanceWidgetData migrated = await settingsHostStore.LoadAsync();
            Assert.Equal(GlanceStyle.MonthCalendar, migrated.Style);
            Assert.Equal(GlanceLayoutMode.Calendar, migrated.Layout);
            Assert.True(migrated.ShowCalendar);

            // Simulate main-process context menu updating the same file on disk.
            var mainProcessStore = new GlanceWidgetStore(root);
            await mainProcessStore.UpdateAsync(data =>
            {
                GlanceWidgetSettingsPolicy.SetStyle(data, GlanceStyle.WeekStrip);
                GlanceWidgetSettingsPolicy.SetDisplayElement(data, GlanceDisplayElement.Seconds, true);
            });

            GlanceWidgetData refreshed = await settingsHostStore.LoadAsync();
            Assert.Equal(GlanceStyle.WeekStrip, refreshed.Style);
            Assert.True(refreshed.ShowSeconds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FestivalService_ComputesSingleDateFestivalForTodaySurface()
    {
        var service = new GlanceFestivalService();
        DateOnly midAutumn2026 = new(2026, 9, 25);

        Assert.Equal(
            "中秋",
            service.GetFestivalText(
                midAutumn2026,
                showChineseFestivals: true,
                GlanceTraditionalCalendarMode.Auto,
                CultureInfo.GetCultureInfo("zh-CN")));
        Assert.Equal(
            string.Empty,
            service.GetFestivalText(
                midAutumn2026,
                showChineseFestivals: false,
                GlanceTraditionalCalendarMode.ChineseLunar,
                CultureInfo.GetCultureInfo("zh-CN")));
    }
}
