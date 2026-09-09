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
    /// Built-in layout resolution: Centered/Editorial map to themselves; the
    /// Calendar layout degrades to Immersive (background + non-calendar
    /// foreground) when the calendar is off or below the responsive floor.
    /// </summary>
    public static NativeLayout ResolveLayout(GlanceLayoutMode mode, bool showCalendarEffective) => mode switch
    {
        GlanceLayoutMode.Centered => NativeLayout.Centered,
        GlanceLayoutMode.Editorial => NativeLayout.Editorial,
        GlanceLayoutMode.Calendar => showCalendarEffective ? NativeLayout.Calendar : NativeLayout.Immersive,
        _ => NativeLayout.Immersive,
    };

    /// <summary>Built-in TimeFontSize (TimeScale stays at 1 until the
    /// font/scale settings batch).</summary>
    public static double TimeFontSize(double availableWidth, double availableHeight) =>
        RoundToHalf(Math.Clamp(Math.Min(availableWidth * 0.18, availableHeight * 0.28), 38, 78));

    private static double RoundToHalf(double value) => Math.Round(value * 2) / 2;

    /// <summary>Built-in font-size formulas share one shape: clamp first,
    /// then multiply the user's time scale, then round to halves.</summary>
    public static double ScaledFontSize(
        double availableWidth, double availableHeight, double timeScale) =>
        Math.Round(RoundToHalf(Math.Clamp(Math.Min(availableWidth * 0.18, availableHeight * 0.28), 38, 78)) * timeScale * 2) / 2;

    public static double ScaledCompactFontSize(
        double availableWidth, double availableHeight, double timeScale) =>
        Math.Round(RoundToHalf(Math.Clamp(Math.Min(availableWidth * 0.13, availableHeight * 0.2), 30, 60)) * timeScale * 2) / 2;

    public static double ScaledCalendarCompactFontSize(
        double availableWidth, double availableHeight, double timeScale) =>
        Math.Round(RoundToHalf(Math.Clamp(Math.Min(availableWidth * 0.078, availableHeight * 0.095), 22, 28)) * timeScale * 2) / 2;

    /// <summary>Built-in ImageFocus mapping: horizontal and vertical
    /// alignment default to Center, with the focus point pinning the
    /// corresponding edge/axis.</summary>
    public static (Microsoft.UI.Xaml.Media.AlignmentX X, Microsoft.UI.Xaml.Media.AlignmentY Y) ResolveImageFocus(
        GlanceImageFocus focus) => focus switch
    {
        GlanceImageFocus.Left => (Microsoft.UI.Xaml.Media.AlignmentX.Left, Microsoft.UI.Xaml.Media.AlignmentY.Center),
        GlanceImageFocus.Right => (Microsoft.UI.Xaml.Media.AlignmentX.Right, Microsoft.UI.Xaml.Media.AlignmentY.Center),
        GlanceImageFocus.Top => (Microsoft.UI.Xaml.Media.AlignmentX.Center, Microsoft.UI.Xaml.Media.AlignmentY.Top),
        GlanceImageFocus.Bottom => (Microsoft.UI.Xaml.Media.AlignmentX.Center, Microsoft.UI.Xaml.Media.AlignmentY.Bottom),
        _ => (Microsoft.UI.Xaml.Media.AlignmentX.Center, Microsoft.UI.Xaml.Media.AlignmentY.Center),
    };

    /// <summary>
    /// Built-in UpdateClockTimer cadence: a time display needs per-minute
    /// ticks; date/weekday/calendar-only needs one tick per midnight;
    /// nothing that displays time or dates needs no clock at all. Uses the
    /// RAW showCalendar setting (the built-in does not apply the responsive
    /// floor here - a floored-off calendar still displays dates elsewhere
    /// or may come back on resize).
    /// </summary>
    public static ClockCadence ComputeClockCadence(
        bool showTime, bool showDate, bool showWeekday, bool showCalendar,
        GlanceTraditionalCalendarMode traditionalMode)
    {
        bool needsCalendarClock = showDate || showWeekday || showCalendar ||
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

/// <summary>The four built-in glance layouts (native resolution).</summary>
internal enum NativeLayout
{
    Immersive,
    Centered,
    Editorial,
    Calendar,
}
