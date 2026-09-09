using System.Globalization;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Month-data pipeline: production source → traditional calendar → festival
/// → presentation. D3 product migration: culture comes from the host config
/// channel (HostApi v2 GetConfigJson), the month is the CURRENT month, and
/// sizing is parameterized by the live viewport (defaults mirror
/// GlanceWidgetViewModel at 440x560 until the first ViewportChanged event).
/// </summary>
internal static class GlanceMonthPipeline
{
    public const double DefaultWidth = 440;
    public const double DefaultHeight = 560;

    public static (GlanceCalendarMonth Month, bool IsCompact, double PanelHeight, double PanelWidth, double DayItemHeight, bool ShowSecondary) Build(
        bool showTraditional, bool showFestivals, CultureInfo culture, double availableWidth, double availableHeight)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly month = new(today.Year, today.Month, 1);
        GlanceCalendarMonth calendarMonth = new LocalCalendarPresentationSource()
            .GetMonthAsync(month, culture).GetAwaiter().GetResult();
        DateOnly titleDate = today;
        GlanceTraditionalCalendarMode mode = showTraditional
            ? GlanceTraditionalCalendarMode.ChineseLunar
            : GlanceTraditionalCalendarMode.None;
        calendarMonth = new GlanceTraditionalCalendarService().Apply(calendarMonth, mode, culture, titleDate);
        calendarMonth = new GlanceFestivalService().Apply(calendarMonth, showChineseFestivals: showFestivals && showTraditional, mode, culture);
        bool isCompact = GlanceCalendarLayoutCalculator.IsCompact(availableHeight);
        double panelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(availableHeight, isCompact, true);
        double panelWidth = Math.Round(Math.Clamp(availableWidth - 28, 272, 360));
        double dayItemHeight = Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(panelHeight, isCompact, true) * 2) / 2;
        bool showSecondary = GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(panelWidth, dayItemHeight, isCompact, true);
        return (calendarMonth, isCompact, panelHeight, panelWidth, dayItemHeight, showSecondary);
    }

    public static GlancePresentation CreatePresentation(
        GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth,
        CultureInfo culture, double availableWidth, double availableHeight)
    {
        DateTime now = DateTime.Now;
        double compactFontSize = Math.Round(Math.Clamp(Math.Min(availableWidth * 0.078, availableHeight * 0.095), 22, 28) * 2) / 2;
        return new GlancePresentation
        {
            TimeText = now.ToString("HH:mm", culture),
            // "M" is the culture-aware month-day pattern (Chinese locales
            // render their native month-day form); never hard-code one
            // locale's literal format here.
            DateText = now.ToString("M", culture),
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
