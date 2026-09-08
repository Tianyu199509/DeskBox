using System.Globalization;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Glance.NativePackage;

/// <summary>
/// Shared month-building pipeline for both slice views: production month
/// source, then production traditional-calendar and festival services, plus
/// the mirrored panel metrics from GlanceWidgetViewModel.
/// </summary>
internal static class RealGlanceModel
{
    // Mirrors GlanceWidgetViewModel sizing at the probe's 440x560 content area:
    // CalendarPanelMaximumWidth=360, CalendarPanelHorizontalInset=28.
    public const double AvailableWidth = 440;
    public const double AvailableHeight = 560;

    // Pinned so festival assertions are deterministic; today-highlighting still
    // follows the machine clock and is not asserted.
    public const int PinnedYear = 2026;
    public const int PinnedMonth = 9;

    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("zh-CN");

    public static (GlanceCalendarMonth Month, bool IsCompact, double PanelHeight, double PanelWidth, double DayItemHeight, bool ShowSecondaryText) Build()
    {
        DateOnly month = new(PinnedYear, PinnedMonth, 1);
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);

        GlanceCalendarMonth calendarMonth = new LocalCalendarPresentationSource()
            .GetMonthAsync(month, Culture).GetAwaiter().GetResult();
        DateOnly titleDate = month == new DateOnly(today.Year, today.Month, 1) ? today : month.AddDays(14);
        calendarMonth = new GlanceTraditionalCalendarService().Apply(
            calendarMonth, GlanceTraditionalCalendarMode.ChineseLunar, Culture, titleDate);
        calendarMonth = new GlanceFestivalService().Apply(
            calendarMonth, showChineseFestivals: true, GlanceTraditionalCalendarMode.ChineseLunar, Culture);

        bool isCompact = GlanceCalendarLayoutCalculator.IsCompact(AvailableHeight);
        double panelHeight = GlanceCalendarLayoutCalculator.CalculatePanelHeight(AvailableHeight, isCompact, true);
        double panelWidth = Math.Round(Math.Clamp(AvailableWidth - 28, 272, 360));
        double dayItemHeight = Math.Round(GlanceCalendarLayoutCalculator.CalculateDayHeight(panelHeight, isCompact, true) * 2) / 2;
        bool showSecondaryText = GlanceCalendarLayoutCalculator.ShouldShowTraditionalDetails(panelWidth, dayItemHeight, isCompact, true);
        return (calendarMonth, isCompact, panelHeight, panelWidth, dayItemHeight, showSecondaryText);
    }

    public static RealGlancePresentation CreatePresentation(
        GlanceCalendarMonth month, bool isCompact, double panelHeight, double panelWidth)
    {
        CultureInfo culture = Culture;
        DateTime now = DateTime.Now;
        double compactFontSize = Math.Round(Math.Clamp(Math.Min(AvailableWidth * 0.078, AvailableHeight * 0.095), 22, 28) * 2) / 2;
        return new RealGlancePresentation
        {
            TimeText = now.ToString("HH:mm", culture),
            DateText = now.ToString("M月d日", culture),
            WeekdayText = culture.DateTimeFormat.GetDayName(now.DayOfWeek),
            CompactCalendarDateText = now.ToString("M月d日", culture),
            TraditionalCalendarTitle = month.TraditionalTitle,
            TimeFontFamily = new Microsoft.UI.Xaml.Media.FontFamily("XamlAutoFontFamily"),
            CompactTimeFontSize = compactFontSize,
            CalendarCompactTimeFontSize = compactFontSize,
            CalendarPanelHeight = panelHeight,
            CalendarPanelWidth = panelWidth,
            CalendarPanelMaxWidth = 360,
            CalendarCornerRadius = new Microsoft.UI.Xaml.CornerRadius(12),
            ShowTime = true,
            ShowDate = true,
            ShowWeekday = true,
            IsCalendarLayout = true,
            IsCompactCalendarPresentation = isCompact,
            IsExpandedCalendarPresentation = !isCompact,
            CalendarLayoutVisibility = Microsoft.UI.Xaml.Visibility.Visible,
            IsCompactVisibility = isCompact ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed,
            IsExpandedVisibility = isCompact ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible,
            ShowTimeVisibility = Microsoft.UI.Xaml.Visibility.Visible,
            ShowDateVisibility = Microsoft.UI.Xaml.Visibility.Visible,
            ShowWeekdayVisibility = Microsoft.UI.Xaml.Visibility.Visible,
        };
    }
}
