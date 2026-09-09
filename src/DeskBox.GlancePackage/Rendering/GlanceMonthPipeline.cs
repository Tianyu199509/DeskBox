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

    public static (GlanceCalendarMonth Month, bool IsCompact, double PanelHeight, double PanelWidth, double DayItemHeight, bool ShowSecondary, GlanceTraditionalCalendarMode EffectiveMode) Build(
        bool showFestivals, GlanceTraditionalCalendarMode traditionalMode, CultureInfo culture, double availableWidth, double availableHeight)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly month = new(today.Year, today.Month, 1);
        GlanceCalendarMonth calendarMonth = new LocalCalendarPresentationSource()
            .GetMonthAsync(month, culture).GetAwaiter().GetResult();
        DateOnly titleDate = today;
        // The real mode travels through the pipeline (audit round 18: a bool
        // collapsed Hebrew/Japanese/Persian/... into Chinese lunar).
        GlanceTraditionalCalendarMode mode = traditionalMode == GlanceTraditionalCalendarMode.Auto
            ? new GlanceTraditionalCalendarService().ResolveMode(GlanceTraditionalCalendarMode.Auto, culture.Name)
            : traditionalMode;
        calendarMonth = new GlanceTraditionalCalendarService().Apply(calendarMonth, mode, culture, titleDate);
        calendarMonth = new GlanceFestivalService().Apply(
            calendarMonth, showChineseFestivals: showFestivals && mode == GlanceTraditionalCalendarMode.ChineseLunar, mode, culture);
        // Built-in parity (audit round 19): the layout calculators reserve
        // space for the secondary line only when a traditional calendar is
        // actually enabled - never a hardcoded true.
        bool hasTraditional = mode != GlanceTraditionalCalendarMode.None;
        bool isCompact = GlanceCalendarLayoutCalculator.IsCompact(availableHeight);
        double panelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(availableHeight, isCompact, hasTraditional);
        double panelWidth = Math.Round(Math.Clamp(availableWidth - 28, 272, 360));
        double dayItemHeight = Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(panelHeight, isCompact, hasTraditional) * 2) / 2;
        bool showSecondary = GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(panelWidth, dayItemHeight, isCompact, hasTraditional);
        return (calendarMonth, isCompact, panelHeight, panelWidth, dayItemHeight, showSecondary, mode);
    }

    public static GlancePresentation CreatePresentation(
        GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth,
        GlanceWidgetData settings, CultureInfo culture, double availableWidth, double availableHeight)
    {
        DateTime now = DateTime.Now;
        double compactFontSize = Math.Round(Math.Clamp(Math.Min(availableWidth * 0.078, availableHeight * 0.095), 22, 28) * 2) / 2;
        // Built-in layout resolution: Calendar degrades to Immersive when the
        // calendar is off or below the responsive floor.
        bool calendarEffective = GlanceDisplayPolicy.ShowCalendarEffective(
            settings.ShowCalendar, availableWidth, availableHeight);
        NativeLayout layout = GlanceDisplayPolicy.ResolveLayout(settings.Layout, calendarEffective);
        bool foreground = settings.ShowTime || settings.ShowDate || settings.ShowWeekday || calendarEffective;
        return new GlancePresentation
        {
            TimeText = GlanceDisplayPolicy.FormatTimeText(now, settings.TimeFormat, culture),
            // Culture-aware month-day (or long-date-with-year) pattern.
            DateText = GlanceDisplayPolicy.FormatDateText(now, culture, settings.ShowYear),
            WeekdayText = now.ToString("dddd", culture),
            CompactCalendarDateText = GlanceDisplayPolicy.FormatCompactCalendarDateText(now, culture),
            TraditionalCalendarTitle = month.TraditionalTitle,
            TimeFontFamily = new FontFamily("XamlAutoFontFamily"),
            TimeFontSize = GlanceDisplayPolicy.TimeFontSize(availableWidth, availableHeight),
            CompactTimeFontSize = compactFontSize,
            CalendarCompactTimeFontSize = compactFontSize,
            CalendarPanelHeight = panelHeight,
            CalendarPanelWidth = panelWidth,
            CalendarPanelMaxWidth = 360,
            CalendarCornerRadius = new CornerRadius(12),
            PlayIconVisibility = Visibility.Collapsed,
            PauseIconVisibility = Visibility.Visible,
        // Built-in display toggles gate each element; the calendar
        // surface follows the resolved layout.
        TimeVisibility = settings.ShowTime ? Visibility.Visible : Visibility.Collapsed,
        DateVisibility = settings.ShowDate ? Visibility.Visible : Visibility.Collapsed,
        WeekdayVisibility = settings.ShowWeekday ? Visibility.Visible : Visibility.Collapsed,
        CalendarHeaderVisibility = isCompact && layout == NativeLayout.Calendar ? Visibility.Visible : Visibility.Collapsed,
        CalendarSurfaceVisibility = layout == NativeLayout.Calendar ? Visibility.Visible : Visibility.Collapsed,
        ForegroundVisibility = foreground ? Visibility.Visible : Visibility.Collapsed,
        ImmersiveVisibility = foreground && layout == NativeLayout.Immersive ? Visibility.Visible : Visibility.Collapsed,
        CenteredVisibility = foreground && layout == NativeLayout.Centered ? Visibility.Visible : Visibility.Collapsed,
        EditorialVisibility = foreground && layout == NativeLayout.Editorial ? Visibility.Visible : Visibility.Collapsed,
        // Built-in ReadabilityOpacity: strength only counts when the
        // foreground is visible (background-only shows no darkening).
        ReadabilityOpacity = foreground ? settings.Readability switch
        {
            GlanceReadabilityMode.None => 0,
            GlanceReadabilityMode.Strong => 0.5,
            _ => 0.28,
        } : 0,
    };
    }
}

[WinRT.GeneratedBindableCustomProperty([
    nameof(CalendarCompactTimeFontSize),
    nameof(CalendarCornerRadius),
    nameof(CalendarHeaderVisibility),
    nameof(CalendarPanelHeight),
    nameof(CalendarPanelMaxWidth),
    nameof(CalendarPanelWidth),
    nameof(CalendarSurfaceVisibility),
    nameof(CenteredVisibility),
    nameof(CompactCalendarDateText),
    nameof(CompactTimeFontSize),
    nameof(DateText),
    nameof(DateVisibility),
    nameof(EditorialVisibility),
    nameof(ForegroundVisibility),
    nameof(ImmersiveVisibility),
    nameof(PauseIconVisibility),
    nameof(PlayIconVisibility),
    nameof(ReadabilityOpacity),
    nameof(TimeFontFamily),
    nameof(TimeFontSize),
    nameof(TimeText),
    nameof(TimeVisibility),
    nameof(TraditionalCalendarTitle),
    nameof(WeekdayText),
    nameof(WeekdayVisibility)
], [])]
public sealed partial class GlancePresentation
{
    public string TimeText { get; init; } = "";
    public string DateText { get; init; } = "";
    public string WeekdayText { get; init; } = "";
    public string CompactCalendarDateText { get; init; } = "";
    public string TraditionalCalendarTitle { get; init; } = "";
    public FontFamily TimeFontFamily { get; init; } = new("XamlAutoFontFamily");
    public double TimeFontSize { get; init; }
    public double CompactTimeFontSize { get; init; }
    public double CalendarCompactTimeFontSize { get; init; }
    public double CalendarPanelHeight { get; init; }
    public double CalendarPanelWidth { get; init; }
    public double CalendarPanelMaxWidth { get; init; }
    public CornerRadius CalendarCornerRadius { get; init; }
    public Visibility TimeVisibility { get; init; }
    public Visibility DateVisibility { get; init; }
    public Visibility WeekdayVisibility { get; init; }
    public Visibility CalendarHeaderVisibility { get; init; }
    public Visibility CalendarSurfaceVisibility { get; init; }
    public Visibility ForegroundVisibility { get; init; }
    public Visibility ImmersiveVisibility { get; init; }
    public Visibility CenteredVisibility { get; init; }
    public Visibility EditorialVisibility { get; init; }
    public double ReadabilityOpacity { get; init; }
    public Visibility PlayIconVisibility { get; set; }
    public Visibility PauseIconVisibility { get; set; }
}
