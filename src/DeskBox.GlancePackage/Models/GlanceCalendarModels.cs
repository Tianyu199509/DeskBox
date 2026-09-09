namespace DeskBox.Models;

/// <summary>
/// Owned by DeskBox.GlancePackage since D3 Phase 2 (copied from
/// src/DeskBox.Abstractions/Models/GlanceCalendarModels.cs).
/// </summary>
public sealed record GlanceCalendarDay(
    DateOnly Date,
    string DayText,
    bool IsCurrentMonth,
    bool IsToday,
    string TraditionalText = "",
    string FestivalText = "")
{
    public bool HasTraditionalText => !string.IsNullOrWhiteSpace(TraditionalText);
    public bool HasFestival => !string.IsNullOrWhiteSpace(FestivalText);
    public bool HasTraditionalTextOnly => HasTraditionalText && !HasFestival;
    public bool HasSecondaryText => HasFestival || HasTraditionalText;
}

public sealed record GlanceCalendarMonth(
    DateOnly Month,
    IReadOnlyList<string> WeekdayHeaders,
    IReadOnlyList<GlanceCalendarDay> Days,
    string TraditionalTitle = "")
{
    public bool HasTraditionalTitle => !string.IsNullOrWhiteSpace(TraditionalTitle);
}

public sealed record GlanceCalendarEvent(
    string Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool IsAllDay,
    string? CalendarColor = null);
