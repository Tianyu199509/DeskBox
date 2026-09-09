using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Month-data pipeline: production source → traditional calendar → festival →
/// presentation. Mirrors GlanceWidgetViewModel sizing at 440x560.
/// </summary>
internal static class GlanceMonthPipeline
{
    public const int PinnedYear = 2026;
    public const int PinnedMonth = 9;
    public const double AvailableWidth = 440;
    public const double AvailableHeight = 560;
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("zh-CN");

    public static (GlanceCalendarMonth Month, bool IsCompact, double PanelHeight, double PanelWidth, double DayItemHeight, bool ShowSecondary) Build(
        bool showTraditional, bool showFestivals)
    {
        DateOnly month = new(PinnedYear, PinnedMonth, 1);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        GlanceCalendarMonth calendarMonth = new LocalCalendarPresentationSource()
            .GetMonthAsync(month, Culture).GetAwaiter().GetResult();
        DateOnly titleDate = month == new DateOnly(today.Year, today.Month, 1) ? today : month.AddDays(14);
        GlanceTraditionalCalendarMode mode = showTraditional
            ? GlanceTraditionalCalendarMode.ChineseLunar
            : GlanceTraditionalCalendarMode.None;
        calendarMonth = new GlanceTraditionalCalendarService().Apply(calendarMonth, mode, Culture, titleDate);
        calendarMonth = new GlanceFestivalService().Apply(calendarMonth, showChineseFestivals: showFestivals && showTraditional, mode, Culture);
        bool isCompact = GlanceCalendarLayoutCalculator.IsCompact(AvailableHeight);
        double panelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(AvailableHeight, isCompact, true);
        double panelWidth = Math.Round(Math.Clamp(AvailableWidth - 28, 272, 360));
        double dayItemHeight = Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(panelHeight, isCompact, true) * 2) / 2;
        bool showSecondary = GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(panelWidth, dayItemHeight, isCompact, true);
        return (calendarMonth, isCompact, panelHeight, panelWidth, dayItemHeight, showSecondary);
    }

    public static GlancePresentation CreatePresentation(
        GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth)
    {
        CultureInfo culture = Culture;
        DateTime now = DateTime.Now;
        double compactFontSize = Math.Round(Math.Clamp(Math.Min(AvailableWidth * 0.078, AvailableHeight * 0.095), 22, 28) * 2) / 2;
        return new GlancePresentation
        {
            TimeText = now.ToString("HH:mm", culture),
            DateText = now.ToString("M月d日", culture),
            WeekdayText = culture.DateTimeFormat.GetDayName(now.DayOfWeek),
            TraditionalCalendarTitle = month.TraditionalTitle,
            TimeFontFamily = new FontFamily("XamlAutoFontFamily"),
            CompactTimeFontSize = compactFontSize,
            CalendarCompactTimeFontSize = compactFontSize,
            CalendarPanelHeight = panelHeight,
            CalendarPanelWidth = panelWidth,
            CalendarPanelMaxWidth = 360,
            CalendarCornerRadius = new CornerRadius(12),
            PlayIconVisibility = Visibility.Collapsed,
            PauseIconVisibility = Visibility.Visible,
        };
    }
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(CalendarCompactTimeFontSize),
    nameof(CalendarCornerRadius),
    nameof(CalendarPanelHeight),
    nameof(CalendarPanelMaxWidth),
    nameof(CalendarPanelWidth),
    nameof(CompactTimeFontSize),
    nameof(DateText),
    nameof(PauseIconVisibility),
    nameof(PlayIconVisibility),
    nameof(TimeFontFamily),
    nameof(TimeText),
    nameof(TraditionalCalendarTitle),
    nameof(WeekdayText)
], [])]
public sealed partial class GlancePresentation
{
    public string TimeText { get; init; } = "";
    public string DateText { get; init; } = "";
    public string WeekdayText { get; init; } = "";
    public string TraditionalCalendarTitle { get; init; } = "";
    public FontFamily TimeFontFamily { get; init; } = new("XamlAutoFontFamily");
    public double CompactTimeFontSize { get; init; }
    public double CalendarCompactTimeFontSize { get; init; }
    public double CalendarPanelHeight { get; init; }
    public double CalendarPanelWidth { get; init; }
    public double CalendarPanelMaxWidth { get; init; }
    public CornerRadius CalendarCornerRadius { get; init; }
    public Visibility PlayIconVisibility { get; set; }
    public Visibility PauseIconVisibility { get; set; }
}
