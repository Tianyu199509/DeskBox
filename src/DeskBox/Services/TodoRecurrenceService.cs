using DeskBox.Models;

namespace DeskBox.Services;

public static class TodoRecurrenceService
{
    public static bool IsRecurring(TodoItem item)
    {
        return item.DueDate is not null &&
               item.Recurrence is not null &&
               !string.Equals(
                   TodoRecurrenceMode.Normalize(item.Recurrence.Mode),
                   TodoRecurrenceMode.None,
                   StringComparison.Ordinal);
    }

    public static TodoRecurrence? CreateRecurrence(string? mode, DateTimeOffset? dueDate)
    {
        if (dueDate is not { } normalizedDueDate)
        {
            return null;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        if (string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return null;
        }

        return new TodoRecurrence
        {
            Mode = normalizedMode,
            AnchorDueDate = normalizedDueDate
        };
    }

    public static string GetLocalizationKey(string? mode)
    {
        return TodoRecurrenceMode.Normalize(mode) switch
        {
            TodoRecurrenceMode.Daily => "Todo.Recurrence.Daily",
            TodoRecurrenceMode.Weekly => "Todo.Recurrence.Weekly",
            TodoRecurrenceMode.Monthly => "Todo.Recurrence.Monthly",
            TodoRecurrenceMode.Weekdays => "Todo.Recurrence.Weekdays",
            _ => "Todo.Recurrence.None"
        };
    }

    public static bool TryCreateNextOccurrence(TodoItem sourceItem, DateTimeOffset completedAt, out TodoItem? nextItem)
    {
        nextItem = null;
        if (!TryGetNextDueDate(sourceItem, completedAt, out DateTimeOffset nextDueDate))
        {
            return false;
        }

        string recurrenceSeriesId = NormalizeSeriesId(sourceItem.RecurrenceSeriesId) ?? Guid.NewGuid().ToString("N");

        nextItem = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = sourceItem.Text,
            IsCompleted = false,
            IsImportant = sourceItem.IsImportant,
            ColorMarker = sourceItem.ColorMarker,
            DueDate = nextDueDate,
            HasDueTime = sourceItem.HasDueTime,
            Recurrence = sourceItem.Recurrence?.Clone(),
            Steps = sourceItem.Steps
                .OrderBy(step => step.SortOrder)
                .Select(step => new TodoStep
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = step.Text,
                    IsCompleted = false,
                    SortOrder = step.SortOrder
                })
                .ToList(),
            Notes = sourceItem.Notes,
            Attachments = sourceItem.Attachments
                .Select(attachment => new TodoAttachment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FilePath = attachment.FilePath,
                    DisplayName = attachment.DisplayName,
                    Type = attachment.Type,
                    AddedAt = completedAt
                })
                .ToList(),
            CompletedAt = null,
            ReminderLastNotifiedAt = null,
            ReminderDismissedForDueDate = null,
            ReminderOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(sourceItem.ReminderOffsetMinutes),
            SnoozedUntil = null,
            SnoozeLastNotifiedAt = null,
            RecurrenceSeriesId = recurrenceSeriesId,
            GeneratedNextItemId = null,
            SortOrder = sourceItem.SortOrder + 1,
            CreatedAt = completedAt,
            UpdatedAt = completedAt
        };

        return true;
    }

    public static bool TryGetNextDueDate(TodoItem item, DateTimeOffset completedAt, out DateTimeOffset nextDueDate)
    {
        nextDueDate = default;
        if (!IsRecurring(item) || item.DueDate is not { } dueDate)
        {
            return false;
        }

        string mode = TodoRecurrenceMode.Normalize(item.Recurrence!.Mode);
        bool hasDueTime = item.HasDueTime;
        DateTimeOffset localDueDate = hasDueTime
            ? dueDate.ToLocalTime()
            : TodoWidgetStore.NormalizeAllDayDueDate(dueDate);
        DateTimeOffset anchorSource = item.Recurrence.AnchorDueDate ?? dueDate;
        DateTimeOffset anchorDueDate = hasDueTime
            ? anchorSource.ToLocalTime()
            : TodoWidgetStore.NormalizeAllDayDueDate(anchorSource);
        DateTimeOffset localCompletedAt = completedAt.ToLocalTime();

        nextDueDate = mode switch
        {
            TodoRecurrenceMode.Daily => AdvanceByFixedDays(localDueDate, localCompletedAt, 1, hasDueTime),
            TodoRecurrenceMode.Weekly => AdvanceByFixedDays(localDueDate, localCompletedAt, 7, hasDueTime),
            TodoRecurrenceMode.Monthly => AdvanceMonthly(localDueDate, anchorDueDate, localCompletedAt, hasDueTime),
            TodoRecurrenceMode.Weekdays => AdvanceWeekdays(localDueDate, localCompletedAt, hasDueTime),
            _ => default
        };

        return nextDueDate != default;
    }

    public static bool ShouldRemoveGeneratedOccurrence(TodoItem sourceItem, TodoItem generatedItem)
    {
        if (string.IsNullOrWhiteSpace(sourceItem.GeneratedNextItemId) ||
            !string.Equals(sourceItem.GeneratedNextItemId, generatedItem.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (generatedItem.IsCompleted ||
            generatedItem.CreatedAt != generatedItem.UpdatedAt)
        {
            return false;
        }

        return string.Equals(sourceItem.Text, generatedItem.Text, StringComparison.Ordinal) &&
               sourceItem.IsImportant == generatedItem.IsImportant &&
               sourceItem.HasDueTime == generatedItem.HasDueTime &&
               string.Equals(sourceItem.ColorMarker, generatedItem.ColorMarker, StringComparison.Ordinal) &&
               Nullable.Equals(
                   TodoReminderOptions.NormalizeOffsetMinutes(sourceItem.ReminderOffsetMinutes),
                   TodoReminderOptions.NormalizeOffsetMinutes(generatedItem.ReminderOffsetMinutes)) &&
               string.Equals(
                   NormalizeSeriesId(sourceItem.RecurrenceSeriesId),
                   NormalizeSeriesId(generatedItem.RecurrenceSeriesId),
                   StringComparison.Ordinal) &&
               AreEquivalent(sourceItem.Recurrence, generatedItem.Recurrence);
    }

    public static string? NormalizeSeriesId(string? seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        return seriesId.Trim();
    }

    private static bool AreEquivalent(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        if (!string.Equals(leftMode, rightMode, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(leftMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return true;
        }

        return Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate);
    }

    private static DateTimeOffset AdvanceByFixedDays(
        DateTimeOffset currentDueDate,
        DateTimeOffset completedAt,
        int days,
        bool hasDueTime)
    {
        DateTimeOffset candidate = AddLocalDays(currentDueDate, days);
        while (HasCandidatePassed(candidate, completedAt, hasDueTime))
        {
            candidate = AddLocalDays(candidate, days);
        }

        return candidate;
    }

    private static DateTimeOffset AdvanceWeekdays(
        DateTimeOffset currentDueDate,
        DateTimeOffset completedAt,
        bool hasDueTime)
    {
        DateTimeOffset candidate = currentDueDate;
        do
        {
            candidate = AddLocalDays(candidate, 1);
        }
        while (HasCandidatePassed(candidate, completedAt, hasDueTime) || IsWeekend(candidate.DayOfWeek));

        return candidate;
    }

    private static DateTimeOffset AdvanceMonthly(
        DateTimeOffset currentDueDate,
        DateTimeOffset anchorDueDate,
        DateTimeOffset completedAt,
        bool hasDueTime)
    {
        int monthOffset = MonthsBetween(anchorDueDate, currentDueDate) + 1;
        DateTimeOffset candidate = CreateMonthlyCandidate(anchorDueDate, monthOffset);
        while (HasCandidatePassed(candidate, completedAt, hasDueTime))
        {
            monthOffset++;
            candidate = CreateMonthlyCandidate(anchorDueDate, monthOffset);
        }

        return candidate;
    }

    private static bool HasCandidatePassed(
        DateTimeOffset candidate,
        DateTimeOffset completedAt,
        bool hasDueTime)
    {
        return hasDueTime
            ? candidate <= completedAt
            : candidate.Date < completedAt.Date;
    }

    private static int MonthsBetween(DateTimeOffset anchorDueDate, DateTimeOffset targetDueDate)
    {
        return ((targetDueDate.Year - anchorDueDate.Year) * 12) +
               (targetDueDate.Month - anchorDueDate.Month);
    }

    private static DateTimeOffset CreateMonthlyCandidate(DateTimeOffset anchorDueDate, int monthOffset)
    {
        DateTime anchorLocal = anchorDueDate.LocalDateTime;
        DateTime targetMonth = new(anchorLocal.Year, anchorLocal.Month, 1, anchorLocal.Hour, anchorLocal.Minute, anchorLocal.Second, anchorLocal.Kind);
        targetMonth = targetMonth.AddMonths(monthOffset);
        int targetDay = Math.Min(anchorLocal.Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
        DateTime localCandidate = new(
            targetMonth.Year,
            targetMonth.Month,
            targetDay,
            anchorLocal.Hour,
            anchorLocal.Minute,
            anchorLocal.Second,
            DateTimeKind.Local);
        return new DateTimeOffset(localCandidate);
    }

    private static bool IsWeekend(DayOfWeek dayOfWeek)
    {
        return dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private static DateTimeOffset AddLocalDays(DateTimeOffset value, int days)
    {
        DateTime localDateTime = value.LocalDateTime.AddDays(days);
        return new DateTimeOffset(localDateTime);
    }
}
