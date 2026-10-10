using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Pure builder for the glance calendar day-item decoration. Today is an
/// explicit parameter (the fake-clock seam): the native CalendarView caches
/// its own today once at creation and never refreshes it across midnight
/// (microsoft-ui-xaml #11205), so every today-driven visual — text highlight
/// and the accent background alike — must derive from this host-side value.
/// </summary>
public static class GlanceCalendarDayDecorationBuilder
{
    public static GlanceCalendarDayDecoration Build(
        GlanceCalendarDay? day,
        DateOnly date,
        DateOnly today,
        bool showTraditionalDetails,
        DateOnly displayedCalendarMonth,
        string calendarLanguage)
    {
        string secondaryText = showTraditionalDetails
            ? !string.IsNullOrWhiteSpace(day?.FestivalText)
                ? day.FestivalText
                : day?.TraditionalText ?? string.Empty
            : string.Empty;
        bool hasSecondaryText = !string.IsNullOrWhiteSpace(secondaryText);
        bool isFestival = hasSecondaryText && day?.HasFestival == true;
        bool isCurrentMonth = day?.IsCurrentMonth ??
            (date.Year == displayedCalendarMonth.Year &&
             date.Month == displayedCalendarMonth.Month);
        return new GlanceCalendarDayDecoration(
            day?.DayText ?? date.Day.ToString(
                CultureInfo.GetCultureInfo(calendarLanguage)),
            secondaryText,
            hasSecondaryText,
            date == today,
            isFestival,
            isCurrentMonth ? 1.0 : 0.42,
            !isCurrentMonth ? 0.34 : isFestival ? 0.88 : 0.62);
    }
}
