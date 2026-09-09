using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>
/// Pure display composition rules ported VERBATIM from the built-in
/// GlanceWidgetViewModel (FormatTimeText / RemoveAmPmDesignator /
/// FormatDateText / FormatCompactCalendarDateText / ShowCalendar) so the
/// native widget renders byte-identical strings. Keep in lockstep with the
/// host view model when either side changes.
/// </summary>
internal static class GlanceDisplayPolicy
{
    /// <summary>
    /// Built-in ShowCalendar: the user setting gated by the responsive floor
    /// below which the calendar surface does not fit.
    /// </summary>
    public static bool ShowCalendarEffective(bool showCalendar, double availableWidth, double availableHeight) =>
        showCalendar && availableWidth >= 300 && availableHeight >= 280;

    /// <summary>
    /// Built-in UpdateClockTimer cadence: a time display needs per-minute
    /// ticks; date/weekday/calendar-only needs one tick per midnight;
    /// nothing that displays time or dates needs no clock at all.
    /// </summary>
    public static ClockCadence ComputeClockCadence(
        bool showTime, bool showDate, bool showWeekday, bool showCalendarEffective,
        GlanceTraditionalCalendarMode traditionalMode)
    {
        bool needsCalendarClock = showDate || showWeekday || showCalendarEffective ||
                                  traditionalMode != GlanceTraditionalCalendarMode.None;
        if (!showTime && !needsCalendarClock)
        {
            return ClockCadence.None;
        }
        return showTime ? ClockCadence.PerMinute : ClockCadence.PerMidnight;
    }

    /// <summary>Delay until the cadence's next boundary (minute or midnight,
    /// +100 ms past-midnight guard mirroring the built-in).</summary>
    public static TimeSpan DelayToBoundary(ClockCadence cadence, DateTime now) =>
        cadence switch
        {
            ClockCadence.PerMidnight => now.Date.AddDays(1).AddMilliseconds(100) - now,
            _ => DelayToNextMinute(now),
        };

    /// <summary>One-shot interval to the next minute boundary (+50 ms guard
    /// so the tick lands just past the boundary, mirroring the built-in cadence).</summary>
    public static TimeSpan DelayToNextMinute(DateTime now)
    {
        double remainingMs = 60_000 - (now.Second * 1000) - now.Millisecond;
        return TimeSpan.FromMilliseconds(Math.Max(1, remainingMs) + 50);
    }

    public static string FormatTimeText(
        DateTime date,
        GlanceTimeFormatMode mode,
        CultureInfo displayCulture,
        CultureInfo? systemCulture = null)
    {
        CultureInfo timeCulture = mode == GlanceTimeFormatMode.FollowSystem
            ? systemCulture ?? CultureInfo.CurrentCulture
            : displayCulture;
        string pattern = mode switch
        {
            GlanceTimeFormatMode.FollowSystem => RemoveAmPmDesignator(
                timeCulture.DateTimeFormat.ShortTimePattern),
            GlanceTimeFormatMode.Hour24 => "HH:mm",
            GlanceTimeFormatMode.Hour12 => "h:mm",
            _ => RemoveAmPmDesignator(
                CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern)
        };
        return date.ToString(pattern, timeCulture);
    }

    /// <summary>Strips AM/PM designator tokens from a time pattern while
    /// respecting quoted literals and backslash escapes (built-in algorithm).</summary>
    private static string RemoveAmPmDesignator(string pattern)
    {
        StringBuilder result = new(pattern.Length);
        for (int index = 0; index < pattern.Length; index++)
        {
            char current = pattern[index];
            if (current is '\'' or '"')
            {
                char quote = current;
                result.Append(current);
                while (++index < pattern.Length)
                {
                    result.Append(pattern[index]);
                    if (pattern[index] == quote)
                    {
                        break;
                    }
                }
                continue;
            }

            if (current == '\\' && index + 1 < pattern.Length)
            {
                result.Append(current);
                result.Append(pattern[++index]);
                continue;
            }

            if (current == 't')
            {
                while (index + 1 < pattern.Length && pattern[index + 1] == 't')
                {
                    index++;
                }
                continue;
            }

            result.Append(current);
        }

        return result.ToString().Trim();
    }

    public static string FormatDateText(DateTime date, CultureInfo culture, bool includeYear)
    {
        if (!includeYear)
        {
            return date.ToString("M", culture);
        }

        // LongDatePattern already carries the locale's natural year/month/day
        // order. Remove its weekday token because weekday is an independent
        // Glance display option.
        string pattern = Regex.Replace(
            culture.DateTimeFormat.LongDatePattern,
            @"(?<!d)d{3,4}(?!d)",
            string.Empty,
            RegexOptions.CultureInvariant);
        pattern = pattern.Trim().Trim(',', '，', '،').Trim();
        return date.ToString(pattern, culture);
    }

    public static string FormatCompactCalendarDateText(DateTime date, CultureInfo culture)
    {
        string day = date.Day.ToString(culture);
        return culture.TwoLetterISOLanguageName switch
        {
            "zh" or "ja" => $"{day}日",
            "ko" => $"{day}일",
            _ => day
        };
    }
}

internal enum ClockCadence
{
    None,
    PerMinute,
    PerMidnight,
}
