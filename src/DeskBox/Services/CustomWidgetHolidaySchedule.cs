using System.Globalization;

namespace DeskBox.Services;

/// <summary>Day classification returned by <see cref="CustomWidgetHolidaySchedule.GetDayType"/>: 普通 / 周末 / 法定休 / 调休班.</summary>
public enum CustomWidgetHolidayDayType
{
    /// <summary>普通工作日。</summary>
    Ordinary,
    /// <summary>周六 / 周末。</summary>
    Weekend,
    /// <summary>法定节假日（放假）。</summary>
    StatutoryHoliday,
    /// <summary>周末调休补班日。</summary>
    SwapWorkday
}

/// <summary>
/// One contiguous official holiday block (e.g. 2026 Spring Festival 02-15..02-23),
/// the weekend swap-workdays the notice ties to it, plus provenance metadata.
/// </summary>
public sealed record CustomWidgetHolidayEntry(
    int Year,
    DateOnly Start,
    DateOnly End,
    string NameKey,
    IReadOnlyList<DateOnly> SwapWorkdays,
    bool IsProvisional,
    string Provenance)
{
    public int TotalDays => End.DayNumber - Start.DayNumber + 1;
}

/// <summary>
/// Zero-network, zero-reflection static lookup table of China's statutory holiday
/// arrangements published by the State Council General Office (2025 &amp; 2026 official
/// notices) plus a conservative provisional 2027 layer (law-fixed statutory dates only,
/// no swap-workday claims while the 2027 notice is unpublished).
/// Shared by the <c>deskbox.countdown</c> widget (task ③) and future schedule
/// templates (task ⑥) — keep this API surface clean and allocation-free on the hot path.
/// </summary>
public static class CustomWidgetHolidaySchedule
{
    public const int FirstSupportedYear = 2025;
    public const int LastSupportedYear = 2027;

    public const string NameKeyNewYear = "CustomWidget.Holiday.NewYear";
    public const string NameKeySpringFestival = "CustomWidget.Holiday.SpringFestival";
    public const string NameKeyQingming = "CustomWidget.Holiday.Qingming";
    public const string NameKeyLabourDay = "CustomWidget.Holiday.LabourDay";
    public const string NameKeyDragonBoat = "CustomWidget.Holiday.DragonBoat";
    public const string NameKeyMidAutumn = "CustomWidget.Holiday.MidAutumn";
    public const string NameKeyNationalDay = "CustomWidget.Holiday.NationalDay";
    public const string NameKeyNationalDayWithMidAutumn = "CustomWidget.Holiday.NationalDayWithMidAutumn";

    public const string Provenance2025 = "official:国办发明电〔2024〕7号";
    public const string Provenance2026 = "official:国办发明电〔2025〕7号";
    public const string Provenance2027Provisional =
        "provisional:《全国年节及纪念日放假办法》法定日期推算，2027 年调休安排尚未发布";

    private static readonly DateOnly[] NoSwapDays = [];

    /// <summary>
    /// 2025 official arrangement (国办发明电〔2024〕7号, published 2024-11):
    /// 元旦 1/1；春节 1/28-2/4（补班 1/26、2/8）；清明 4/4-4/6；
    /// 劳动节 5/1-5/5（补班 4/27）；端午 5/31-6/2；国庆、中秋 10/1-10/8（补班 9/28、10/11）。
    /// </summary>
    private static readonly CustomWidgetHolidayEntry[] Entries2025 =
    [
        new(2025, new(2025, 1, 1), new(2025, 1, 1), NameKeyNewYear, NoSwapDays, false, Provenance2025),
        new(2025, new(2025, 1, 28), new(2025, 2, 4), NameKeySpringFestival,
            [new(2025, 1, 26), new(2025, 2, 8)], false, Provenance2025),
        new(2025, new(2025, 4, 4), new(2025, 4, 6), NameKeyQingming, NoSwapDays, false, Provenance2025),
        new(2025, new(2025, 5, 1), new(2025, 5, 5), NameKeyLabourDay, [new(2025, 4, 27)], false, Provenance2025),
        new(2025, new(2025, 5, 31), new(2025, 6, 2), NameKeyDragonBoat, NoSwapDays, false, Provenance2025),
        new(2025, new(2025, 10, 1), new(2025, 10, 8), NameKeyNationalDayWithMidAutumn,
            [new(2025, 9, 28), new(2025, 10, 11)], false, Provenance2025)
    ];

    /// <summary>
    /// 2026 official arrangement (国办发明电〔2025〕7号, published 2025-11-04):
    /// 元旦 1/1-1/3（补班 1/4）；春节 2/15-2/23（补班 2/14、2/28）；清明 4/4-4/6；
    /// 劳动节 5/1-5/5（补班 5/9）；端午 6/19-6/21；中秋 9/25-9/27；国庆 10/1-10/7（补班 9/20、10/10）。
    /// </summary>
    private static readonly CustomWidgetHolidayEntry[] Entries2026 =
    [
        new(2026, new(2026, 1, 1), new(2026, 1, 3), NameKeyNewYear, [new(2026, 1, 4)], false, Provenance2026),
        new(2026, new(2026, 2, 15), new(2026, 2, 23), NameKeySpringFestival,
            [new(2026, 2, 14), new(2026, 2, 28)], false, Provenance2026),
        new(2026, new(2026, 4, 4), new(2026, 4, 6), NameKeyQingming, NoSwapDays, false, Provenance2026),
        new(2026, new(2026, 5, 1), new(2026, 5, 5), NameKeyLabourDay, [new(2026, 5, 9)], false, Provenance2026),
        new(2026, new(2026, 6, 19), new(2026, 6, 21), NameKeyDragonBoat, NoSwapDays, false, Provenance2026),
        new(2026, new(2026, 9, 25), new(2026, 9, 27), NameKeyMidAutumn, NoSwapDays, false, Provenance2026),
        new(2026, new(2026, 10, 1), new(2026, 10, 7), NameKeyNationalDay,
            [new(2026, 9, 20), new(2026, 10, 10)], false, Provenance2026)
    ];

    /// <summary>
    /// 2027 conservative provisional layer — the annual State Council notice is NOT yet
    /// published (checked 2026-11-04). Only law-fixed statutory days from
    /// 《全国年节及纪念日放假办法》(2024 revision: 除夕、5月2日 added) are listed:
    /// 元旦 1/1；春节(除夕+初一~初三) 2/5-2/8；清明 4/5；劳动节 5/1-5/2；
    /// 端午 6/9；中秋 9/15；国庆 10/1-10/3. No swap-workday data — UI must not claim any 调休.
    /// </summary>
    private static readonly CustomWidgetHolidayEntry[] Entries2027 =
    [
        new(2027, new(2027, 1, 1), new(2027, 1, 1), NameKeyNewYear, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 2, 5), new(2027, 2, 8), NameKeySpringFestival, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 4, 5), new(2027, 4, 5), NameKeyQingming, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 5, 1), new(2027, 5, 2), NameKeyLabourDay, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 6, 9), new(2027, 6, 9), NameKeyDragonBoat, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 9, 15), new(2027, 9, 15), NameKeyMidAutumn, NoSwapDays, true, Provenance2027Provisional),
        new(2027, new(2027, 10, 1), new(2027, 10, 3), NameKeyNationalDay, NoSwapDays, true, Provenance2027Provisional)
    ];

    private static readonly CustomWidgetHolidayEntry[][] EntriesByYear =
    [
        Entries2025,
        Entries2026,
        Entries2027
    ];

    /// <summary>Returns true when the year has an official (non-provisional) published arrangement.</summary>
    public static bool IsYearOfficial(int year) => year is >= FirstSupportedYear and <= 2026;

    /// <summary>Returns true when the year only carries the conservative provisional statutory-date layer.</summary>
    public static bool IsYearProvisional(int year) => year == 2027;

    public static string GetProvenance(int year) => year switch
    {
        2025 => Provenance2025,
        2026 => Provenance2026,
        2027 => Provenance2027Provisional,
        _ => "unsupported"
    };

    /// <summary>All entries of one calendar year (empty for unsupported years).</summary>
    public static IReadOnlyList<CustomWidgetHolidayEntry> GetEntries(int year)
    {
        int idx = year - FirstSupportedYear;
        return idx >= 0 && idx < EntriesByYear.Length ? EntriesByYear[idx] : Array.Empty<CustomWidgetHolidayEntry>();
    }

    /// <summary>
    /// First holiday block whose end date is on/after <paramref name="from"/> (a holiday already
    /// in progress still counts). Searches up to 400 days ahead across the supported years;
    /// returns false beyond the table's coverage.
    /// </summary>
    public static bool TryGetNextHoliday(DateOnly from, out CustomWidgetHolidayEntry entry)
    {
        for (int year = from.Year; year <= from.Year + 1 && year <= LastSupportedYear; year++)
        {
            foreach (CustomWidgetHolidayEntry candidate in GetEntries(year))
            {
                if (candidate.End >= from)
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>The holiday block covering <paramref name="date"/>, if any.</summary>
    public static bool TryGetHolidayAt(DateOnly date, out CustomWidgetHolidayEntry entry)
    {
        foreach (CustomWidgetHolidayEntry candidate in GetEntries(date.Year))
        {
            if (date >= candidate.Start && date <= candidate.End)
            {
                entry = candidate;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>True when <paramref name="date"/> is a weekend swap-workday (调休补班).</summary>
    public static bool IsSwapWorkday(DateOnly date)
    {
        foreach (CustomWidgetHolidayEntry candidate in GetEntries(date.Year))
        {
            foreach (DateOnly swap in candidate.SwapWorkdays)
            {
                if (swap == date)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Classify a date: 法定休 &gt; 调休班 &gt; 周末 &gt; 普通. Dates outside the table fall back to weekend detection.</summary>
    public static CustomWidgetHolidayDayType GetDayType(DateOnly date)
    {
        if (TryGetHolidayAt(date, out _))
        {
            return CustomWidgetHolidayDayType.StatutoryHoliday;
        }

        if (IsSwapWorkday(date))
        {
            return CustomWidgetHolidayDayType.SwapWorkday;
        }

        return date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? CustomWidgetHolidayDayType.Weekend
            : CustomWidgetHolidayDayType.Ordinary;
    }

    /// <summary>Localized display name for a holiday entry (falls back to the zh-CN catalog value).</summary>
    public static string GetLocalizedName(CustomWidgetHolidayEntry entry, string? cultureName = null)
    {
        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;
        return LocalizationService.TranslateForCulture(culture, entry.NameKey);
    }
}
