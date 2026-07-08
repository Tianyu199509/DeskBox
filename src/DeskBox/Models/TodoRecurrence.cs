namespace DeskBox.Models;

public static class TodoRecurrenceMode
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Weekdays = "weekdays";

    public static readonly string[] SupportedModes =
    [
        None,
        Daily,
        Weekly,
        Monthly,
        Weekdays
    ];

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return None;
        }

        string normalized = mode.Trim().ToLowerInvariant();
        return SupportedModes.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : None;
    }
}

public sealed class TodoRecurrence
{
    public string Mode { get; set; } = TodoRecurrenceMode.None;

    public DateTimeOffset? AnchorDueDate { get; set; }

    public static TodoRecurrence? Normalize(TodoRecurrence? recurrence, DateTimeOffset? dueDate)
    {
        if (dueDate is not { } normalizedDueDate || recurrence is null)
        {
            return null;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(recurrence.Mode);
        if (string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return null;
        }

        recurrence.Mode = normalizedMode;
        recurrence.AnchorDueDate = NormalizeDueDate(recurrence.AnchorDueDate) ?? normalizedDueDate;
        return recurrence;
    }

    public TodoRecurrence Clone()
    {
        return new TodoRecurrence
        {
            Mode = TodoRecurrenceMode.Normalize(Mode),
            AnchorDueDate = NormalizeDueDate(AnchorDueDate)
        };
    }

    private static DateTimeOffset? NormalizeDueDate(DateTimeOffset? dueDate)
    {
        if (dueDate is not { } value)
        {
            return null;
        }

        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Offset);
    }
}
