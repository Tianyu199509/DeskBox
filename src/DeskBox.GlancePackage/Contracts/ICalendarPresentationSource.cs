using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Contracts;

/// <summary>
/// Owned by DeskBox.GlancePackage since D3 Phase 2 (copied from
/// src/DeskBox.Abstractions/Contracts/ICalendarPresentationSource.cs).
/// </summary>
public interface ICalendarPresentationSource
{
    Task<GlanceCalendarMonth> GetMonthAsync(
        DateOnly month,
        CultureInfo culture,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GlanceCalendarEvent>> GetAgendaAsync(
        DateOnly startDate,
        int dayCount,
        CultureInfo culture,
        CancellationToken cancellationToken = default);
}
