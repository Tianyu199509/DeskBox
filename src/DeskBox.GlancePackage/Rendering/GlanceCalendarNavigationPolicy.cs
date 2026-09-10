using System.Globalization;

namespace DeskBox.GlancePackage.Rendering;

/// <summary>Package-owned, UI-free counterpart of the built-in calendar navigation resolver.</summary>
internal static class GlanceCalendarNavigationPolicy
{
    public static readonly DateOnly MinimumMonth = new(1900, 1, 1);
    public static readonly DateOnly MaximumMonth = new(2100, 12, 1);

    public static DateOnly ClampMonth(DateOnly date)
    {
        DateOnly month = new(date.Year, date.Month, 1);
        return month < MinimumMonth ? MinimumMonth : month > MaximumMonth ? MaximumMonth : month;
    }

    public static DateOnly ResolveWheelTarget(DateOnly currentMonth, int wheelDelta)
    {
        DateOnly month = ClampMonth(currentMonth);
        // Clamp before AddMonths as well, so DateOnly.MinValue/MaxValue are safe.
        return wheelDelta == 0 ? month : ClampMonth(month.AddMonths(wheelDelta > 0 ? -1 : 1));
    }

    public static DateOnly ResolveDisplayedMonth(IEnumerable<DateOnly> visibleDates, DateOnly fallbackMonth)
    {
        ArgumentNullException.ThrowIfNull(visibleDates);
        DateOnly fallback = ClampMonth(fallbackMonth);
        return visibleDates
            .Where(date => date >= MinimumMonth && date <= MaximumMonth.AddMonths(1).AddDays(-1))
            .Distinct()
            .GroupBy(date => new DateOnly(date.Year, date.Month, 1))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => Math.Abs((group.Key.Year - fallback.Year) * 12 + group.Key.Month - fallback.Month))
            .Select(group => group.Key)
            .FirstOrDefault(fallback);
    }

    public static DateOnly ResolveTitleDate(DateOnly displayedMonth, DateOnly today)
    {
        DateOnly month = ClampMonth(displayedMonth);
        return month.Year == today.Year && month.Month == today.Month ? today : month.AddDays(14);
    }

    public static DayOfWeek ResolveFirstDayOfWeek(CultureInfo culture) => culture.DateTimeFormat.FirstDayOfWeek;

    public static bool IsVisible(double left, double top, double width, double height, double viewportWidth, double viewportHeight) =>
        double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && double.IsFinite(height) &&
        double.IsFinite(viewportWidth) && double.IsFinite(viewportHeight) &&
        width > 0 && height > 0 && viewportWidth > 0 && viewportHeight > 0 &&
        left + width > 0 && left < viewportWidth && top + height > 0 && top < viewportHeight;
}
