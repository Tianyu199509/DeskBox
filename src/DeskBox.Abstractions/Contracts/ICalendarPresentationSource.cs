using System.Globalization;
using DeskBox.Models;

namespace DeskBox.Contracts;

/// <summary>
/// Presentation-only calendar seam. Account providers and CalDAV clients can
/// later feed this contract without becoming dependencies of the widget view.
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
