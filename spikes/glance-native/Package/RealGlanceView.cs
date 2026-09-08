using System.Globalization;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using WinRT;

namespace DeskBox.Glance.NativePackage;

/// <summary>
/// Batch B real-Glance slice: renders production XAML with production business
/// services (local month source, traditional calendar, festivals, layout
/// calculator, day-decoration record). Only the glue is package-local.
/// </summary>
internal static class RealGlanceView
{
    // Pinned so festival assertions are deterministic; today-highlighting still
    // follows the machine clock and is not asserted.
    private const int PinnedYear = 2026;
    private const int PinnedMonth = 9;

    // Mirrors GlanceWidgetViewModel sizing at the probe's 440x560 content area:
    // CalendarPanelMaximumWidth=360, CalendarPanelHorizontalInset=28.
    private const double AvailableWidth = 440;
    private const double AvailableHeight = 560;

    public static FrameworkElement Create(string root)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo("zh-CN");
        DateOnly month = new(PinnedYear, PinnedMonth, 1);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        GlanceCalendarMonth calendarMonth = new LocalCalendarPresentationSource()
            .GetMonthAsync(month, culture).GetAwaiter().GetResult();
        DateOnly titleDate = month == new DateOnly(today.Year, today.Month, 1) ? today : month.AddDays(14);
        calendarMonth = new GlanceTraditionalCalendarService().Apply(
            calendarMonth, GlanceTraditionalCalendarMode.ChineseLunar, culture, titleDate);
        calendarMonth = new GlanceFestivalService().Apply(
            calendarMonth, showChineseFestivals: true, GlanceTraditionalCalendarMode.ChineseLunar, culture);

        bool isCompact = GlanceCalendarLayoutCalculator.IsCompact(AvailableHeight);
        double panelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(AvailableHeight, isCompact, true);
        double panelWidth = Math.Round(Math.Clamp(AvailableWidth - 28, 272, 360));
        double dayItemHeight = Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(panelHeight, isCompact, true) * 2) / 2;
        bool showSecondaryText = GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(panelWidth, dayItemHeight, isCompact, true);

        DateTime now = DateTime.Now;
        var presentation = new RealGlancePresentation
        {
            TimeText = now.ToString("HH:mm", culture),
            DateText = now.ToString("M月d日", culture),
            WeekdayText = culture.DateTimeFormat.GetDayName(now.DayOfWeek),
            CompactCalendarDateText = now.ToString("M月d日", culture),
            TraditionalCalendarTitle = calendarMonth.TraditionalTitle,
            TimeFontFamily = new FontFamily("XamlAutoFontFamily"),
            CompactTimeFontSize = Math.Round(Math.Clamp(Math.Min(AvailableWidth * 0.078, AvailableHeight * 0.095), 22, 28) * 2) / 2,
            CalendarCompactTimeFontSize = Math.Round(Math.Clamp(Math.Min(AvailableWidth * 0.078, AvailableHeight * 0.095), 22, 28) * 2) / 2,
            CalendarPanelHeight = panelHeight,
            CalendarPanelWidth = panelWidth,
            CalendarPanelMaxWidth = 360,
            CalendarCornerRadius = new CornerRadius(12),
            CalendarLayoutVisibility = Visibility.Visible,
            IsCompactVisibility = isCompact ? Visibility.Visible : Visibility.Collapsed,
            IsExpandedVisibility = isCompact ? Visibility.Collapsed : Visibility.Visible,
            ShowTimeVisibility = Visibility.Visible,
            ShowDateVisibility = Visibility.Visible,
            ShowWeekdayVisibility = Visibility.Visible,
        };

        FrameworkElement content = (FrameworkElement)XamlReader.Load(
            File.ReadAllText(Path.Combine(root, "glance-real.xaml")));
        content.DataContext = presentation;

        var calendarView = content.FindName("NativeCalendarView").As<CalendarView>();
        Dictionary<DateOnly, GlanceCalendarDay> days = calendarMonth.Days.ToDictionary(day => day.Date);
        int decorated = 0;
        calendarView.CalendarViewDayItemChanging += (_, args) =>
        {
            CalendarViewDayItem item = args.Item;
            if (args.InRecycleQueue)
            {
                item.Tag = null;
                return;
            }
            // Replicates GlanceWidgetContent.ApplyCalendarDayDecoration.
            DateOnly date = DateOnly.FromDateTime(item.Date.DateTime);
            days.TryGetValue(date, out GlanceCalendarDay? day);
            string secondaryText = showSecondaryText
                ? !string.IsNullOrWhiteSpace(day?.FestivalText)
                    ? day.FestivalText
                    : day?.TraditionalText ?? string.Empty
                : string.Empty;
            bool hasSecondaryText = !string.IsNullOrWhiteSpace(secondaryText);
            bool isFestival = hasSecondaryText && day?.HasFestival == true;
            bool isCurrentMonth = day?.IsCurrentMonth ?? date.Month == month.Month;
            item.MinHeight = dayItemHeight;
            item.Height = dayItemHeight;
            item.Tag = new RealGlanceDayDecoration(
                day?.DayText ?? date.Day.ToString(culture),
                secondaryText,
                hasSecondaryText ? Visibility.Visible : Visibility.Collapsed,
                date == today ? Visibility.Visible : Visibility.Collapsed,
                date == today ? Visibility.Collapsed : Visibility.Visible,
                isFestival ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                isCurrentMonth ? 1.0 : 0.42,
                !isCurrentMonth ? 0.34 : isFestival ? 0.88 : 0.62);
            if (hasSecondaryText) decorated++;
        };

        // Festival evidence is derived from the real month data (deterministic);
        // the decoration counter only grows once the CalendarView realizes day
        // items, so the summary is rewritten after the view has loaded.
        List<string> festivalDays = calendarMonth.Days
            .Where(day => day.HasFestival)
            .Select(day => $"{day.Date:yyyy-MM-dd} {day.FestivalText}")
            .ToList();
        Dictionary<string, object?> Summary() => new()
        {
            ["pinnedMonth"] = $"{PinnedYear:0000}-{PinnedMonth:00}",
            ["traditionalTitle"] = calendarMonth.TraditionalTitle,
            ["showSecondaryText"] = showSecondaryText,
            ["panelHeight"] = panelHeight,
            ["panelWidth"] = panelWidth,
            ["dayItemHeight"] = dayItemHeight,
            ["festivalDays"] = festivalDays,
            ["traditionalTextDayCount"] = calendarMonth.Days.Count(day => !string.IsNullOrWhiteSpace(day.TraditionalText)),
            ["decoratedDayCount"] = decorated,
        };
        WriteSummary(root, Summary());
        content.Loaded += (_, _) =>
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(600);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                WriteSummary(root, Summary());
            };
            timer.Start();
        };
        return content;
    }

    private static void WriteSummary(string root, Dictionary<string, object?> values)
    {
        using var stream = File.Create(Path.Combine(root, "real-summary.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> pair in values)
        {
            switch (pair.Value)
            {
                case null: writer.WriteNull(pair.Key); break;
                case bool flag: writer.WriteBoolean(pair.Key, flag); break;
                case double number: writer.WriteNumber(pair.Key, number); break;
                case int number: writer.WriteNumber(pair.Key, number); break;
                case List<string> list:
                    writer.WriteStartArray(pair.Key);
                    foreach (string entry in list) writer.WriteStringValue(entry);
                    writer.WriteEndArray();
                    break;
                default: writer.WriteString(pair.Key, pair.Value.ToString()); break;
            }
        }
        writer.WriteEndObject();
    }
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(CalendarCompactTimeFontSize),
    nameof(CalendarCornerRadius),
    nameof(CalendarLayoutVisibility),
    nameof(CalendarPanelHeight),
    nameof(CalendarPanelMaxWidth),
    nameof(CalendarPanelWidth),
    nameof(CompactCalendarDateText),
    nameof(CompactTimeFontSize),
    nameof(DateText),
    nameof(IsCompactVisibility),
    nameof(IsExpandedVisibility),
    nameof(ShowDateVisibility),
    nameof(ShowTimeVisibility),
    nameof(ShowWeekdayVisibility),
    nameof(TimeFontFamily),
    nameof(TimeText),
    nameof(TraditionalCalendarTitle),
    nameof(WeekdayText)
], [])]
public sealed partial class RealGlancePresentation
{
    public string TimeText { get; init; } = "";
    public string DateText { get; init; } = "";
    public string WeekdayText { get; init; } = "";
    public string CompactCalendarDateText { get; init; } = "";
    public string TraditionalCalendarTitle { get; init; } = "";
    public FontFamily TimeFontFamily { get; init; } = new("XamlAutoFontFamily");
    public double CompactTimeFontSize { get; init; }
    public double CalendarCompactTimeFontSize { get; init; }
    public double CalendarPanelHeight { get; init; }
    public double CalendarPanelWidth { get; init; }
    public double CalendarPanelMaxWidth { get; init; }
    public CornerRadius CalendarCornerRadius { get; init; }
    public Visibility CalendarLayoutVisibility { get; init; }
    public Visibility IsCompactVisibility { get; init; }
    public Visibility IsExpandedVisibility { get; init; }
    public Visibility ShowTimeVisibility { get; init; }
    public Visibility ShowDateVisibility { get; init; }
    public Visibility ShowWeekdayVisibility { get; init; }
}

// Package-local mirror of the production GlanceCalendarDayDecoration contract:
// the runtime-loaded XAML cannot use converters (no XAML metadata provider
// reaches this DLL's namespace), so visibility/font-weight are pre-computed
// here while the underlying values and opacity rules stay verbatim from
// GlanceWidgetContent.ApplyCalendarDayDecoration.
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record RealGlanceDayDecoration(
    string DayText,
    string SecondaryText,
    Visibility SecondaryVisibility,
    Visibility TodayVisibility,
    Visibility NonTodayVisibility,
    Windows.UI.Text.FontWeight SecondaryFontWeight,
    double PrimaryOpacity,
    double SecondaryOpacity);
