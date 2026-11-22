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
        item.Notes = "repeat this context";
        item.Steps =
        [
            new TodoStep { Text = "first", IsCompleted = true, SortOrder = 0 },
            new TodoStep { Text = "second", IsCompleted = false, SortOrder = 1 }
        ];

        bool created = TodoRecurrenceService.TryCreateNextOccurrence(item, completedAt, out TodoItem? nextItem);

        Assert.True(created);
        Assert.NotNull(nextItem);
        Assert.False(nextItem.IsCompleted);
        Assert.True(nextItem.IsGeneratedOccurrence);
        Assert.Equal(item.Text, nextItem.Text);
        Assert.Equal(TodoRecurrenceMode.Weekly, nextItem.Recurrence?.Mode);
        Assert.Equal(item.Recurrence?.AnchorDueDate, nextItem.Recurrence?.AnchorDueDate);
        Assert.Null(nextItem.ReminderLastNotifiedAt);
        Assert.Null(nextItem.ReminderDismissedForDueDate);
        Assert.Equal(30, nextItem.ReminderOffsetMinutes);
        Assert.Null(nextItem.SnoozedUntil);
        Assert.Null(nextItem.SnoozeLastNotifiedAt);
        Assert.Equal(item.Notes, nextItem.Notes);
        Assert.Collection(
            nextItem.Steps,
            step =>
            {
                Assert.Equal("first", step.Text);
                Assert.False(step.IsCompleted);
            },
            step =>
            {
                Assert.Equal("second", step.Text);
                Assert.False(step.IsCompleted);
            });
    }

    [Fact]
    public void TryGetNextDueDate_OverdueAllDayAndTimedDailyItemsAdvanceToTomorrow()
    {
        var allDayYesterday = new DateTimeOffset(new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Local));
        var completedTodayMorning = new DateTimeOffset(new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Local));
        var allDayItem = CreateRecurringItem(TodoRecurrenceMode.Daily, allDayYesterday);
        allDayItem.HasDueTime = false;

        Assert.True(TodoRecurrenceService.TryGetNextDueDate(allDayItem, completedTodayMorning, out DateTimeOffset nextAllDay));
        Assert.Equal(new DateTime(2026, 11, 10, 0, 0, 0), nextAllDay.LocalDateTime);

        var timedYesterdayEvening = new DateTimeOffset(new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Local));
        var timedItem = CreateRecurringItem(TodoRecurrenceMode.Daily, timedYesterdayEvening);
        timedItem.HasDueTime = true;

        Assert.True(TodoRecurrenceService.TryGetNextDueDate(timedItem, completedTodayMorning, out DateTimeOffset nextTimed));
        Assert.Equal(new DateTime(2026, 11, 10, 18, 0, 0), nextTimed.LocalDateTime);
    }

    [Fact]
    public void IsDeferredOccurrence_DistinguishesGeneratedFutureFromTodayAndManualFuture()
    {
        var today = new DateTime(2026, 11, 9);
        var tomorrowDue = new DateTimeOffset(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Local));
        var generatedFuture = CreateRecurringItem(TodoRecurrenceMode.Daily, tomorrowDue);
        generatedFuture.IsGeneratedOccurrence = true;

        Assert.True(TodoRecurrenceService.IsDeferredOccurrence(generatedFuture, today));
        Assert.False(TodoRecurrenceService.IsDeferredOccurrence(generatedFuture, new DateTime(2026, 11, 10)));

        var manualFuture = CreateRecurringItem(TodoRecurrenceMode.Daily, tomorrowDue);
        manualFuture.IsGeneratedOccurrence = false;
        Assert.False(TodoRecurrenceService.IsDeferredOccurrence(manualFuture, today));
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
