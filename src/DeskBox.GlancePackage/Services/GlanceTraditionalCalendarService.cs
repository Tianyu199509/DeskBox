using System.Globalization;
using DeskBox.GlancePackage.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using Windows.Globalization;
using WinCalendar = Windows.Globalization.Calendar;

namespace DeskBox.Services;

/// <summary>
/// Owned by DeskBox.GlancePackage since D3 Phase 2 (copied from
/// src/DeskBox/Services/GlanceTraditionalCalendarService.cs; seam references
/// rewritten to package-owned CultureRules/PackageLogger). Adds an optional,
/// presentation-only traditional calendar layer to the Gregorian month grid.
/// It stays independent from calendar account/event providers so a future
/// CalDAV implementation does not own locale rules.
/// </summary>
internal sealed class GlanceTraditionalCalendarService
{
    private static readonly string[] ChineseMonthNames =
        ["", "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly string[] ChineseDayNames =
    [
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];
    private static readonly string[] HeavenlyStems = ["甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸"];
    private static readonly string[] EarthlyBranches = ["子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥"];
    private static readonly string[] IndianMonthNames =
        ["", "चैत्र", "वैशाख", "ज्येष्ठ", "आषाढ़", "श्रावण", "भाद्र", "आश्विन", "कार्तिक", "अग्रहायण", "पौष", "माघ", "फाल्गुन"];
    private static readonly string[] BanglaMonthNames =
        ["", "বৈশাখ", "জ্যৈষ্ঠ", "আষাঢ়", "শ্রাবণ", "ভাদ্র", "আশ্বিন", "কার্তিক", "অগ্রহায়ণ", "পৌষ", "মাঘ", "ফাল্গুন", "চৈত্র"];

    public GlanceCalendarMonth Apply(
        GlanceCalendarMonth month,
        GlanceTraditionalCalendarMode configuredMode,
        CultureInfo culture,
        DateOnly today)
    {
        GlanceTraditionalCalendarMode mode = ResolveMode(configuredMode, culture.Name);
        if (mode == GlanceTraditionalCalendarMode.None)
        {
            return month with
            {
                Days = month.Days.Select(day => day with { TraditionalText = string.Empty }).ToList(),
                TraditionalTitle = string.Empty
            };
        }

        var days = month.Days
            .Select(day => day with { TraditionalText = FormatDay(day.Date, mode, culture) })
            .ToList();
        return month with
        {
            Days = days,
            TraditionalTitle = FormatTitle(today, mode, culture)
        };
    }

    public GlanceTraditionalCalendarMode ResolveMode(
        GlanceTraditionalCalendarMode configuredMode,
        string? cultureName)
    {
        if (configuredMode != GlanceTraditionalCalendarMode.Auto)
        {
            return configuredMode;
        }

        string language = GetLanguage(cultureName);
        return language switch
        {
            "zh" => GlanceTraditionalCalendarMode.ChineseLunar,
            "ar" => GlanceTraditionalCalendarMode.UmAlQura,
            "hi" => GlanceTraditionalCalendarMode.IndianSaka,
            "ja" => GlanceTraditionalCalendarMode.JapaneseEra,
            "bn" => GlanceTraditionalCalendarMode.Bangla,
            "ru" => GlanceTraditionalCalendarMode.Julian,
            "he" => GlanceTraditionalCalendarMode.Hebrew,
            "fa" => GlanceTraditionalCalendarMode.Persian,
            "th" => GlanceTraditionalCalendarMode.ThaiBuddhist,
            _ => GlanceTraditionalCalendarMode.None
        };
    }

    public string FormatTitle(
        DateOnly date,
        GlanceTraditionalCalendarMode configuredMode,
        CultureInfo culture)
    {
        GlanceTraditionalCalendarMode mode = ResolveMode(configuredMode, culture.Name);
        try
        {
            return mode switch
            {
                GlanceTraditionalCalendarMode.None => string.Empty,
                GlanceTraditionalCalendarMode.ChineseLunar => FormatChineseTitle(
                    date,
                    CultureRules.IsTraditionalChineseCulture(culture.Name)),
                GlanceTraditionalCalendarMode.IndianSaka => FormatIndianTitle(date),
                GlanceTraditionalCalendarMode.Bangla => FormatBanglaTitle(date),
                _ => FormatSystemCalendarTitle(date, mode, culture)
            };
        }
        catch (Exception ex)
        {
            PackageLogger.LogVerbose($"[GlanceTraditionalCalendar] Failed to format {mode}: {ex.Message}");
            return string.Empty;
        }
    }

    internal string FormatDay(
        DateOnly date,
        GlanceTraditionalCalendarMode configuredMode,
        CultureInfo culture)
    {
        GlanceTraditionalCalendarMode mode = ResolveMode(configuredMode, culture.Name);
        try
        {
            return mode switch
            {
                GlanceTraditionalCalendarMode.None => string.Empty,
                GlanceTraditionalCalendarMode.ChineseLunar => FormatChineseDay(
                    date,
                    CultureRules.IsTraditionalChineseCulture(culture.Name)),
                GlanceTraditionalCalendarMode.IndianSaka => FormatIndianDay(date),
                GlanceTraditionalCalendarMode.Bangla => FormatBanglaDay(date),
                GlanceTraditionalCalendarMode.JapaneseEra or
                GlanceTraditionalCalendarMode.ThaiBuddhist => string.Empty,
                _ => FormatSystemCalendarDay(date, mode, culture)
            };
        }
        catch (Exception ex)
        {
            PackageLogger.LogVerbose($"[GlanceTraditionalCalendar] Failed to format day {date:yyyy-MM-dd} as {mode}: {ex.Message}");
            return string.Empty;
        }
    }

    private static string FormatChineseDay(DateOnly date, bool useTraditional)
    {
        ChineseDate value = GetChineseDate(date);
        string text = value.Day == 1
            ? $"{(value.IsLeapMonth ? "闰" : string.Empty)}{ChineseMonthNames[value.Month]}"
            : ChineseDayNames[value.Day];
        return useTraditional ? ChineseTextConverter.ToTraditional(text) : text;
    }

    private static string FormatChineseTitle(DateOnly date, bool useTraditional)
    {
        ChineseDate value = GetChineseDate(date);
        string cyclicalYear = $"{HeavenlyStems[(value.SexagenaryYear - 1) % 10]}{EarthlyBranches[(value.SexagenaryYear - 1) % 12]}年";
        string month = $"{(value.IsLeapMonth ? "闰" : string.Empty)}{ChineseMonthNames[value.Month]}";
        string text = $"{cyclicalYear} {month}{ChineseDayNames[value.Day]}";
        return useTraditional ? ChineseTextConverter.ToTraditional(text) : text;
    }

    private static ChineseDate GetChineseDate(DateOnly date)
    {
        var calendar = new ChineseLunisolarCalendar();
        DateTime value = date.ToDateTime(TimeOnly.MinValue);
        int year = calendar.GetYear(value);
        int calendarMonth = calendar.GetMonth(value);
        int leapMonth = calendar.GetLeapMonth(year);
        bool isLeapMonth = leapMonth > 0 && calendarMonth == leapMonth;
        int month = leapMonth > 0 && calendarMonth >= leapMonth
            ? calendarMonth - 1
            : calendarMonth;
        return new ChineseDate(
            month,
            calendar.GetDayOfMonth(value),
            isLeapMonth,
            calendar.GetSexagenaryYear(value));
    }

    private static string FormatIndianDay(DateOnly date)
    {
        TraditionalDate value = GetIndianSakaDate(date);
        return value.Day == 1
            ? $"{ToNativeDigits(value.Month, DevanagariDigits)}/{ToNativeDigits(value.Day, DevanagariDigits)}"
            : ToNativeDigits(value.Day, DevanagariDigits);
    }

    private static string FormatIndianTitle(DateOnly date)
    {
        TraditionalDate value = GetIndianSakaDate(date);
        return $"शक {ToNativeDigits(value.Year, DevanagariDigits)} {IndianMonthNames[value.Month]} {ToNativeDigits(value.Day, DevanagariDigits)}";
    }

    private static TraditionalDate GetIndianSakaDate(DateOnly date)
    {
        DateTime value = date.ToDateTime(TimeOnly.MinValue);
        int anchorYear = value.Year;
        DateTime start = GetIndianYearStart(anchorYear);
        if (value < start)
        {
            anchorYear--;
            start = GetIndianYearStart(anchorYear);
        }

        int offset = (value - start).Days;
        int chaitraLength = DateTime.IsLeapYear(anchorYear) ? 31 : 30;
        int month;
        int day;
        if (offset < chaitraLength)
        {
            month = 1;
            day = offset + 1;
        }
        else
        {
            offset -= chaitraLength;
            if (offset < 5 * 31)
            {
                month = 2 + (offset / 31);
                day = (offset % 31) + 1;
            }
            else
            {
                offset -= 5 * 31;
                month = 7 + (offset / 30);
                day = (offset % 30) + 1;
            }
        }

        return new TraditionalDate(anchorYear - 78, month, day);
    }

    private static DateTime GetIndianYearStart(int gregorianYear) =>
        new(gregorianYear, 3, DateTime.IsLeapYear(gregorianYear) ? 21 : 22);

    private static string FormatBanglaDay(DateOnly date)
    {
        TraditionalDate value = GetBanglaDate(date);
        return value.Day == 1
            ? $"{ToNativeDigits(value.Month, BanglaDigits)}/{ToNativeDigits(value.Day, BanglaDigits)}"
            : ToNativeDigits(value.Day, BanglaDigits);
    }

    private static string FormatBanglaTitle(DateOnly date)
    {
        TraditionalDate value = GetBanglaDate(date);
        return $"বাংলা {ToNativeDigits(value.Year, BanglaDigits)} {BanglaMonthNames[value.Month]} {ToNativeDigits(value.Day, BanglaDigits)}";
    }

    private static TraditionalDate GetBanglaDate(DateOnly date)
    {
        DateTime value = date.ToDateTime(TimeOnly.MinValue);
        int startYear = value >= new DateTime(value.Year, 4, 14)
            ? value.Year
            : value.Year - 1;
        DateTime start = new(startYear, 4, 14);
        int offset = (value - start).Days;
        int[] monthLengths = [31, 31, 31, 31, 31, 31, 30, 30, 30, 30, DateTime.IsLeapYear(startYear + 1) ? 30 : 29, 30];
        int month = 1;
        while (month <= monthLengths.Length && offset >= monthLengths[month - 1])
        {
            offset -= monthLengths[month - 1];
            month++;
        }

        return new TraditionalDate(startYear - 593, month, offset + 1);
    }

    private static string FormatSystemCalendarDay(
        DateOnly date,
        GlanceTraditionalCalendarMode mode,
        CultureInfo culture)
    {
        WinCalendar calendar = CreateSystemCalendar(date, mode, culture);
        if (calendar.Day != 1)
        {
            return calendar.DayAsString();
        }

        return $"{calendar.MonthAsNumericString()}/{calendar.DayAsString()}";
    }

    private static string FormatSystemCalendarTitle(
        DateOnly date,
        GlanceTraditionalCalendarMode mode,
        CultureInfo culture)
    {
        WinCalendar calendar = CreateSystemCalendar(date, mode, culture);
        string month = calendar.MonthAsSoloString(8);
        if (mode == GlanceTraditionalCalendarMode.JapaneseEra)
        {
            return $"{calendar.EraAsString(2)}{calendar.YearAsString()}年 {month}{calendar.DayAsString()}日";
        }

        return $"{calendar.DayAsString()} {month} {calendar.YearAsString()}".Trim();
    }

    private static WinCalendar CreateSystemCalendar(
        DateOnly date,
        GlanceTraditionalCalendarMode mode,
        CultureInfo culture)
    {
        (string calendarSystem, string language) = mode switch
        {
            GlanceTraditionalCalendarMode.UmAlQura => (CalendarIdentifiers.UmAlQura, "ar-SA"),
            GlanceTraditionalCalendarMode.Hijri => (CalendarIdentifiers.Hijri, "ar-SA"),
            GlanceTraditionalCalendarMode.JapaneseEra => (CalendarIdentifiers.Japanese, "ja-JP"),
            GlanceTraditionalCalendarMode.Julian => (CalendarIdentifiers.Julian, GetUsableLanguage(culture.Name, "ru-RU")),
            GlanceTraditionalCalendarMode.Hebrew => (CalendarIdentifiers.Hebrew, "he-IL"),
            GlanceTraditionalCalendarMode.Persian => (CalendarIdentifiers.Persian, "fa-IR"),
            GlanceTraditionalCalendarMode.ThaiBuddhist => (CalendarIdentifiers.Thai, "th-TH"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        var calendar = new WinCalendar([language], calendarSystem, ClockIdentifiers.TwentyFourHour);
        DateTime localNoon = DateTime.SpecifyKind(
            date.ToDateTime(new TimeOnly(12, 0)),
            DateTimeKind.Local);
        calendar.SetDateTime(new DateTimeOffset(localNoon));
        return calendar;
    }

    private static string GetUsableLanguage(string? cultureName, string fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(cultureName)
                ? fallback
                : CultureInfo.GetCultureInfo(cultureName).Name;
        }
        catch
        {
            return fallback;
        }
    }

    private static string GetLanguage(string? cultureName)
    {
        try
        {
            return CultureInfo.GetCultureInfo(cultureName ?? string.Empty).TwoLetterISOLanguageName;
        }
        catch
        {
            int separator = cultureName?.IndexOfAny(['-', '_']) ?? -1;
            return (separator > 0 ? cultureName![..separator] : cultureName ?? string.Empty)
                .ToLowerInvariant();
        }
    }

    private static readonly char[] DevanagariDigits = "०१२३४५६७८९".ToCharArray();
    private static readonly char[] BanglaDigits = "০১২৩৪৫৬৭৮৯".ToCharArray();

    private static string ToNativeDigits(int value, IReadOnlyList<char> digits)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return string.Create(text.Length, (text, digits), static (span, state) =>
        {
            for (int index = 0; index < state.text.Length; index++)
            {
                char character = state.text[index];
                span[index] = character is >= '0' and <= '9'
                    ? state.digits[character - '0']
                    : character;
            }
        });
    }

    private readonly record struct ChineseDate(
        int Month,
        int Day,
        bool IsLeapMonth,
        int SexagenaryYear);

    private readonly record struct TraditionalDate(int Year, int Month, int Day);
}
