using System.Globalization;
using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Owned by DeskBox.GlancePackage since D3 Phase 2 (copied from
/// src/DeskBox/Services/LocalCalendarPresentationSource.cs).
/// </summary>
public sealed class LocalCalendarPresentationSource : ICalendarPresentationSource
{
    public Task<GlanceCalendarMonth> GetMonthAsync(
        DateOnly month,
        CultureInfo culture,
        CancellationToken cancellationToken = default)
        => GetMonthAsync(month, culture, DateOnly.FromDateTime(DateTime.Today), cancellationToken);

    internal Task<GlanceCalendarMonth> GetMonthAsync(
        DateOnly month,
        CultureInfo culture,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateOnly first = new(month.Year, month.Month, 1);
        DayOfWeek firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
        int leadingDays = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        DateOnly gridStart = first.AddDays(-leadingDays);
        var headers = new List<string>(7);
        string[] shortestDayNames = culture.DateTimeFormat.ShortestDayNames;
        for (int index = 0; index < 7; index++)
        {
            headers.Add(shortestDayNames[((int)firstDayOfWeek + index) % 7]);
        }

        var days = new List<GlanceCalendarDay>(42);
        for (int index = 0; index < 42; index++)
        {
            DateOnly date = gridStart.AddDays(index);
            days.Add(new GlanceCalendarDay(date, date.Day.ToString(culture), date.Month == first.Month, date == today));
        }

        return Task.FromResult(new GlanceCalendarMonth(first, headers, days));
    }

    public Task<IReadOnlyList<GlanceCalendarEvent>> GetAgendaAsync(
        DateOnly startDate,
        int dayCount,
        CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dayCount);
        return Task.FromResult<IReadOnlyList<GlanceCalendarEvent>>([]);
    }
}
