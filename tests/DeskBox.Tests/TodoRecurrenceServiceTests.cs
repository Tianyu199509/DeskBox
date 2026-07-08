using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoRecurrenceServiceTests
{
    [Fact]
    public void TryGetNextDueDate_DailyRecurrenceAdvancesPastCompletionTime()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(new DateTime(2026, 7, 8, 9, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryGetNextDueDate_WeekdaysRecurrenceSkipsWeekend()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 30, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Weekdays, dueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(DayOfWeek.Monday, nextDueDate.DayOfWeek);
        Assert.Equal(new DateTime(2026, 7, 13, 18, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryGetNextDueDate_MonthlyRecurrencePreservesAnchorDayAcrossShortMonth()
    {
        var anchorDueDate = new DateTimeOffset(new DateTime(2026, 1, 31, 18, 0, 0, DateTimeKind.Local));
        var currentDueDate = new DateTimeOffset(new DateTime(2026, 2, 28, 18, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 2, 28, 19, 0, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Monthly, currentDueDate, anchorDueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(new DateTime(2026, 3, 31, 18, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryCreateNextOccurrence_PreservesRecurrenceAndClearsReminderState()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Weekly, dueDate);
        item.IsImportant = true;
        item.ColorMarker = TodoItem.BlueColorMarker;
        item.ReminderLastNotifiedAt = completedAt.AddMinutes(-10);
        item.ReminderDismissedForDueDate = dueDate;
        item.ReminderOffsetMinutes = 30;
        item.SnoozedUntil = completedAt.AddMinutes(10);
        item.SnoozeLastNotifiedAt = completedAt.AddMinutes(-5);

        bool created = TodoRecurrenceService.TryCreateNextOccurrence(item, completedAt, out TodoItem? nextItem);

        Assert.True(created);
        Assert.NotNull(nextItem);
        Assert.False(nextItem.IsCompleted);
        Assert.Equal(item.Text, nextItem.Text);
        Assert.Equal(TodoRecurrenceMode.Weekly, nextItem.Recurrence?.Mode);
        Assert.Equal(item.Recurrence?.AnchorDueDate, nextItem.Recurrence?.AnchorDueDate);
        Assert.Null(nextItem.ReminderLastNotifiedAt);
        Assert.Null(nextItem.ReminderDismissedForDueDate);
        Assert.Equal(30, nextItem.ReminderOffsetMinutes);
        Assert.Null(nextItem.SnoozedUntil);
        Assert.Null(nextItem.SnoozeLastNotifiedAt);
    }

    private static TodoItem CreateRecurringItem(string recurrenceMode, DateTimeOffset dueDate, DateTimeOffset? anchorDueDate = null)
    {
        return new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = "task",
            DueDate = dueDate,
            Recurrence = new TodoRecurrence
            {
                Mode = recurrenceMode,
                AnchorDueDate = anchorDueDate ?? dueDate
            }
        };
    }
}
