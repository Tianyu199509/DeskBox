# Todo Recurrence Logic Audit

## 🎯 审计目标

审查 DeskBox 的任务循环重复逻辑，识别时间计算错误、状态同步问题和用户期望偏差。

---

## ⚠️ Critical Issues

### Issue #TODO-001: Incorrect Recurrence Time Zone Handling

**Detected Pattern**:
```csharp
public class RecurringTodoService
{
    public NextOccurrence CalculateNextOccurrence(TodoItem todo)
    {
        // ❌ Assumes UTC without user timezone consideration!
        var nextDate = todo.LastCompletedUtc.Add(todo.RecurringInterval);
        
        // But stored in local time → off by timezone offset!
        return new NextOccurrence
        {
            Date = nextDate.ToString("yyyy-MM-dd HH:mm")
        };
    }
}
```

**Impact Analysis**:
- **User in PST**: Task scheduled for 9 AM PST shows up at 12 PM EST (3-hour shift)
- **DST transitions cause missed notifications**: Spring forward/fall back not handled
- **Database inconsistency**: Some tasks use local time, some use UTC

**Fix Required**: Proper timezone conversion with DST handling

```csharp
public class TimeZoneAwareRecurrenceService
{
    private readonly IUserPreferences _userPrefs;
    
    public NextOccurrence CalculateNextOccurrence(TodoItem todo, DateTime now)
    {
        // Get user's current timezone
        var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(_userPrefs.TimeZoneId);
        
        // Convert to user's local time
        var localLastCompleted = TimeZoneInfo.ConvertTimeFromUtc(
            todo.LastCompletedUtc, userTimeZone);
        
        // Add recurrence interval in local time
        var nextLocal = localLastCompleted + todo.RecurringInterval;
        
        // Handle DST transitions safely
        if (!userTimeZone.IsDaylightSavingTime(nextLocal))
        {
            // Check if we crossed a DST boundary
            var previousDay = nextLocal.Date.AddDays(-1).TimeOfDay;
            if (userTimeZone.IsDaylightSavingTime(previousDay.ToDateTimeOffset()))
            {
                // Spring forward: add 1 hour
                nextLocal = nextLocal.AddHours(1);
            }
        }
        
        // Store back in UTC for consistency
        return new NextOccurrence
        {
            DateLocal = nextLocal.ToString("yyyy-MM-dd HH:mm"),
            DateUtc = TimeZoneInfo.ConvertTimeToUtc(nextLocal),
            TimeZoneId = userTimeZone.Id
        };
    }
}
```

**Testing Requirements**:
```csharp
[Test]
public void CalculateNextOccurrence_RespectsUserTimeZone()
{
    // Arrange
    var service = new TimeZoneAwareRecurrenceService(new UserPreferences { TimeZoneId = "Pacific Standard Time" });
    var todo = new TodoItem { LastCompletedUtc = new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc) };
    
    // Act
    var result = service.CalculateNextOccurrence(todo, DateTime.Now);
    
    // Assert - Should be 9 AM Pacific Time, not UTC
    result.DateLocal.Should().Contain("09:00");
}

[Test]
public void CalculateNextOccurrence_HandlesDSTSpringForward()
{
    // Arrange - DST starts March 10, 2024 at 2 AM in US
    var service = new TimeZoneAwareRecurrenceService(new UserPreferences { TimeZoneId = "Pacific Standard Time" });
    var todo = new TodoItem 
    { 
        LastCompletedUtc = new DateTime(2024, 3, 10, 1, 30, 0, DateTimeKind.Utc) // 9:30 PM PST day before
    };
    
    // Act
    var result = service.CalculateNextOccurrence(todo, TimeSpan.FromDays(1));
    
    // Assert - Should skip ahead due to DST transition
    // At 2 AM clocks jump to 3 AM, so task should appear at 3 AM instead of 2 AM
    result.DateLocal.Should().NotContain("02:00"); // No 2 AM occurrence
}
```

---

### Issue #TODO-002: No Snooze or Defer Mechanism

**Problem**: Tasks only show at exact recurrence time, no flexibility

**Better Approach**: Add defer/snooze options

```csharp
public async Task DeferTaskAsync(Guid taskId, TimeSpan deferDuration)
{
    var todo = await GetTodoByIdAsync(taskId);
    
    // Save original recurrence pattern for later restoration
    var originalPattern = todo.RecurrenceRule;
    
    // Temporarily override next occurrence
    todo.NextOccurrence = DateTime.Now + deferDuration;
    
    // Mark as modified but preserve original rule
    todo.DeferredUntil = DateTime.Now + deferDuration;
    todo.WasDeferred = true;
    
    await SaveTodoAsync(todo);
}

public async Task UndeferTaskAsync(Guid taskId)
{
    var todo = await GetTodoByIdAsync(taskId);
    
    // Restore original next occurrence from deferred position
    todo.NextOccurrence = todo.DeferredAt + todo.RecurrenceInterval;
    todo.DeferredUntil = null;
    todo.WasDeferred = false;
    
    await SaveTodoAsync(todo);
}
```

**User Experience Options**:
- 🕐 **Snooze 15 minutes** - Common immediate action
- 🕐 **Snooze 1 hour** - Short delay
- 📅 **Defer until tomorrow** - Common daily task behavior
- 📅 **Defer until next week** - For weekly recurring tasks

---

### Issue #TODO-003: DST (Daylight Saving Time) Not Handled

**Specific Scenario**:
When DST transitions occur:
- **Spring forward** (March): Task appears 1 hour later than expected
- **Fall back** (November): Task might appear twice in one day

**Real Example**:
```
Before DST change (Nov 3, 1:30 AM):
  Task set to repeat every Monday at 1:30 AM

After DST change (Nov 4, 1:30 AM occurs TWICE):
  First occurrence (standard time): Task fires normally
  Second occurrence (daylight time): Task fires again!
  Result: User sees duplicate reminder within 5 minutes
```

**Solution Implementation**:
```csharp
public class DSSafeRecurrenceCalculator
{
    public DateTime CalculateSafeNextOccurrence(DateTime lastOccurrence, TimeSpan interval, string timeZoneId)
    {
        var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        
        // Use DateTimeOffset with timezone info
        var lastWithTimezone = new DateTimeOffset(lastOccurrence, userTimeZone.GetUtcOffset(lastOccurrence));
        var nextWithTimezone = lastWithTimezone + interval;
        
        // Detect DST overlap
        var utcOffsetBefore = userTimeZone.GetUtcOffset(nextWithTimezone.LocalDateTime);
        var testTime = nextWithTimezone.LocalDateTime.AddHours(-1);
        var utcOffsetAfter = userTimeZone.GetUtcOffset(testTime);
        
        if (Math.Abs((utcOffsetBefore - utcOffsetAfter).TotalHours) == 1)
        {
            // DST transition detected
            // In fall-back case, prefer the first occurrence
            if (utcOffsetBefore > utcOffsetAfter)
            {
                nextWithTimezone = nextWithTimezone.AddHours(-1);
            }
        }
        
        return nextWithTimezone.UtcDateTime;
    }
}
```

---

### Issue #TODO-004: Multiple Recurrence Rules Conflict

**Edge Case Detected**:
Some users may want tasks that recur on multiple patterns:
- "Review metrics" - Daily AND Weekly
- "Team meeting" - Every Monday AND Wednesday

**Current Behavior**:
If both rules exist, it's unclear which takes precedence.

**Design Recommendation**:
```csharp
public enum RecurrenceConflictStrategy
{
    MergeAllRules,      // Show task whenever ANY rule triggers
    PriorityBased,       // Highest priority rule wins
    FrequencyDominates   // Most frequent rule wins
}

public class CompositeRecurrenceRule
{
    public List<SimpleRecurrenceRule> SubRules { get; } = new();
    public RecurrenceConflictStrategy ConflictResolution { get; set; }
    
    public bool IsDue(DateTime checkTime)
    {
        return ConflictResolution switch
        {
            RecurrenceConflictStrategy.MergeAllRules => 
                SubRules.Any(rule => rule.IsDue(checkTime)),
            
            RecurrenceConflictStrategy.PriorityBased =>
                SubRules.OrderByDescending(r => r.Priority)
                        .Any(rule => rule.IsDue(checkTime)),
            
            RecurrenceConflictStrategy.FrequencyDominates =>
                SubRules.OrderByDescending(r => r.FrequencyScore)
                        .First()
                        .IsDue(checkTime),
            
            _ => false
        };
    }
}
```

**Database Schema Extension**:
```sql
CREATE TABLE TaskRecurrenceRules (
    TaskId UNIQUEIDENTIFIER NOT NULL,
    RuleIndex INTEGER NOT NULL,
    IntervalType ENUM('Daily', 'Weekly', 'Monthly', 'Custom'),
    IntervalValue INTEGER NOT NULL,  -- e.g., every 2 weeks = 2
    DayOfWeek INTEGER,               -- 0=Sunday, 6=Saturday
    MonthDay INTEGER,                -- Day of month for monthly recurrence
    Priority INTEGER DEFAULT 0,      -- Higher = more important
    FrequencyScore REAL,             -- Calculated value for conflict resolution
    PRIMARY KEY (TaskId, RuleIndex)
);
```

---

### Issue #TODO-005: Completed Tasks History Not Tracked

**Missing Functionality**:
When a recurring task is marked complete, there's no audit trail of:
- When was it actually completed?
- Was it completed late or early?
- What was the streak count?

**Benefits of Tracking**:
- Motivation through streak counting (like Duolingo)
- Analytics: "You complete morning tasks 80% on time"
- Pattern detection: "You tend to snooze evening tasks 3x per week"

**Implementation**:
```csharp
public class TaskCompletionTracker
{
    private readonly Dictionary<Guid, List<CompletionRecord>> _completionHistory = new();
    
    public record CompletionRecord
    {
        public DateTime ScheduledTime { get; init; }
        public DateTime ActualCompletionTime { get; init; }
        public TimeSpan Lateness { get; init; }  // Positive = late, Negative = early
        public int StreakCount { get; init; }
        public bool WasSnoozed { get; init; }
    }
    
    public CompletionRecord RecordCompletion(Guid taskId, DateTime actualCompletionTime)
    {
        var task = await GetTodoByIdAsync(taskId);
        var scheduledTime = task.NextOccurrence;
        
        var completion = new CompletionRecord
        {
            ScheduledTime = scheduledTime,
            ActualCompletionTime = actualCompletionTime,
            Lateness = actualCompletionTime - scheduledTime,
            StreakCount = CalculateCurrentStreak(taskId),
            WasSnoozed = task.WasDeferred ?? false
        };
        
        _completionHistory[taskId].Add(completion);
        
        // Update streak counter
        task.CurrentStreak += 1;
        task.LongestStreak = Math.Max(task.LongestStreak, task.CurrentStreak);
        
        return completion;
    }
    
    public int CalculateCurrentStreak(Guid taskId)
    {
        var history = _completionHistory[taskId];
        var currentStreak = 0;
        var referenceDate = DateTime.Now.Date;
        
        // Count backwards through completed tasks
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var completion = history[i];
            var completionDate = completion.ActualCompletionTime.Date;
            
            if (completionDate == referenceDate)
            {
                currentStreak++;
                referenceDate -= TimeSpan.FromDays(1);
            }
            else if (completionDate < referenceDate)
            {
                // Gap found - streak broken
                break;
            }
        }
        
        return currentStreak;
    }
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Always store recurrence dates in UTC internally
- Convert to user's local time ONLY for display purposes
- Track DST transitions explicitly in calculation logic
- Provide snooze/defer UI for user flexibility
- Maintain completion history for analytics and gamification
- Support composite recurrence rules for complex schedules

### ❌ DON'T

- Assume user's timezone is always consistent
- Ignore DST spring-forward/fall-back edge cases
- Force rigid adherence to exact times without flexibility
- Lose track of when tasks were actually completed
- Use floating-point arithmetic for time calculations

---

## 🧪 Test Matrix

| Scenario | Expected Behavior | Status |
|----------|------------------|--------|
| Daily task at 9 AM, user changes timezone | Task moves with timezone offset | ⏳ Needs Test |
| Task repeats every Monday, DST starts Sunday night | Task appears at correct Monday time | ⏳ Needs Test |
| User snoozes recurring task once | Original recurrence preserved | ⏳ Needs Test |
| Multiple recurrence rules overlap | Both trigger appropriately | ⏳ Needs Test |
| Completion streak reaches milestone | Notification shown to user | ⏳ Needs Test |

---

## 📈 Success Metrics

| Metric | Target | Measurement Method |
|--------|--------|-------------------|
| Recurrence accuracy | 100% ±1 minute | Compare scheduled vs actual trigger time |
| DST transition handling | 0 errors logged | Monitor exception rate during March/November |
| User snooze rate | <20% of tasks | Track defer actions / total completions |
| Completion tracking coverage | 100% of recurring tasks | Database query for non-null history |
| Streak activation rate | >30% of users with recurring tasks | Feature usage analytics |

---

<div align="center">

**"Recurrence logic must respect reality – users don't live in ideal time zones."**

*Generated: July 22, 2026*  
*Version: 2.0 (Expanded)*  
*Status: Ready for Implementation Review*

</div>
