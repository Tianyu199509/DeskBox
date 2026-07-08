using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoReminderServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _settingsRoot;
    private readonly string _widgetsDataRoot;

    public TodoReminderServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _settingsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "settings")).FullName;
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public async Task CheckNowAsync_NotifiesOnceAndMarksStore()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(4);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);
        int repeatedCount = await service.CheckNowAsync(now.AddSeconds(20));

        Assert.Equal(1, notifiedCount);
        Assert.Equal(0, repeatedCount);
        var notification = Assert.Single(notifications);
        Assert.Equal(1, notification.Count);
        Assert.Equal("todo-widget", notification.WidgetId);
        Assert.Equal("task", notification.ItemId);
        Assert.True(notification.HasTodayDueItem);
        Assert.Contains("Send build", notification.Message, StringComparison.Ordinal);

        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.Equal(now, item.ReminderLastNotifiedAt);
        Assert.Equal(dueDate, item.ReminderDismissedForDueDate);
    }

    [Fact]
    public async Task CheckNowAsync_NotifiesAgainWhenDueDateChanges()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(4)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        Assert.Equal(1, await service.CheckNowAsync(now));

        var data = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(data.Items);
        item.DueDate = now.AddMinutes(10);
        await CreateStore("todo-widget").SaveAsync(data);

        Assert.Equal(1, await service.CheckNowAsync(now.AddMinutes(6)));
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public async Task CheckNowAsync_SkipsWhenReminderSettingDisabled()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        settingsService.Settings.TodoReminderEnabled = false;
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(4)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);

        Assert.Equal(0, notifiedCount);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CheckNowAsync_UsesPerTaskReminderOffsetBeforeGlobalDefault()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        settingsService.Settings.TodoDefaultReminderOffsetMinutes = 5;
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(20),
                    ReminderOffsetMinutes = 30
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);

        Assert.Equal(1, notifiedCount);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task CheckNowAsync_SkipsWhenPerTaskReminderIsOff()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(4),
                    ReminderOffsetMinutes = TodoReminderOptions.ReminderOff
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);

        Assert.Equal(0, notifiedCount);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CheckNowAsync_SnoozedTaskNotifiesAndClearsSnooze()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(1);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate,
                    ReminderDismissedForDueDate = dueDate,
                    SnoozedUntil = now
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        int notifiedCount = await service.CheckNowAsync(now);

        Assert.Equal(1, notifiedCount);
        Assert.Single(notifications);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.Null(item.SnoozedUntil);
        Assert.Equal(now, item.SnoozeLastNotifiedAt);
        Assert.Equal(dueDate, item.ReminderDismissedForDueDate);
    }

    [Fact]
    public async Task CheckNowAsync_SnoozedTaskRestoresAfterServiceRestart()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(1);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate
                }
            ]
        });
        var initialNotifications = new List<TodoReminderNotification>();
        var initialService = CreateService(settingsService, now, initialNotifications);

        Assert.True(await initialService.SnoozeAsync("todo-widget", "task", TimeSpan.FromMinutes(10)));
        initialService.Dispose();

        var beforeNotifications = new List<TodoReminderNotification>();
        var beforeRestartedService = CreateService(settingsService, now.AddMinutes(9), beforeNotifications);
        Assert.Equal(0, await beforeRestartedService.CheckNowAsync(now.AddMinutes(9)));
        Assert.Empty(beforeNotifications);
        beforeRestartedService.Dispose();

        var afterNotifications = new List<TodoReminderNotification>();
        var afterRestartedService = CreateService(settingsService, now.AddMinutes(10), afterNotifications);
        Assert.Equal(1, await afterRestartedService.CheckNowAsync(now.AddMinutes(10)));
        Assert.Single(afterNotifications);

        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.Null(item.SnoozedUntil);
        Assert.Equal(now.AddMinutes(10), item.SnoozeLastNotifiedAt);
    }

    [Fact]
    public async Task CheckNowAsync_SkipsCompletedTaskEvenWhenDueOrSnoozed()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    IsCompleted = true,
                    DueDate = now.AddSeconds(-30),
                    SnoozedUntil = now
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        Assert.Equal(0, await service.CheckNowAsync(now));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task CheckNowAsync_NotifiesOverdueTaskWithinMissedReminderGrace()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddSeconds(-45)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        Assert.Equal(1, await service.CheckNowAsync(now));
        Assert.Single(notifications);
    }

    [Fact]
    public async Task CheckNowAsync_SkipsStaleOverdueTaskAfterMissedReminderGrace()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = now.AddMinutes(-2)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        Assert.Equal(0, await service.CheckNowAsync(now));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task SnoozeAsync_PersistsSnoozeAndDismissesCurrentDueReminder()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(1);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        bool snoozed = await service.SnoozeAsync("todo-widget", "task", TimeSpan.FromMinutes(10));

        Assert.True(snoozed);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.Equal(now.AddMinutes(10), item.SnoozedUntil);
        Assert.Equal(dueDate, item.ReminderDismissedForDueDate);
    }

    [Fact]
    public async Task CompleteAsync_MarksTaskCompletedAndClearsSnooze()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(1);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate,
                    SnoozedUntil = now.AddMinutes(10)
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        bool completed = await service.CompleteAsync("todo-widget", "task");

        Assert.True(completed);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        var item = Assert.Single(reloaded.Items);
        Assert.True(item.IsCompleted);
        Assert.Equal(now.ToUniversalTime(), item.CompletedAt);
        Assert.Null(item.SnoozedUntil);
        Assert.Null(item.SnoozeLastNotifiedAt);
    }

    [Fact]
    public async Task CompleteAsync_ForRecurringTaskCreatesNextOccurrence()
    {
        DateTimeOffset now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset dueDate = now.AddMinutes(1);
        var settingsService = CreateSettingsService("todo-widget");
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = "task",
                    Text = "Send build",
                    DueDate = dueDate,
                    ReminderOffsetMinutes = 30,
                    Recurrence = new TodoRecurrence
                    {
                        Mode = TodoRecurrenceMode.Daily,
                        AnchorDueDate = dueDate
                    }
                }
            ]
        });
        var notifications = new List<TodoReminderNotification>();
        var service = CreateService(settingsService, now, notifications);

        bool completed = await service.CompleteAsync("todo-widget", "task");

        Assert.True(completed);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        Assert.Equal(2, reloaded.Items.Count);
        var completedItem = reloaded.Items.Single(item => item.Id == "task");
        var nextItem = reloaded.Items.Single(item => item.Id != "task");
        Assert.True(completedItem.IsCompleted);
        Assert.False(nextItem.IsCompleted);
        Assert.Equal(completedItem.GeneratedNextItemId, nextItem.Id);
        Assert.Equal(TodoRecurrenceMode.Daily, nextItem.Recurrence?.Mode);
        Assert.Equal(30, nextItem.ReminderOffsetMinutes);
        Assert.Null(nextItem.SnoozedUntil);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private SettingsService CreateSettingsService(string widgetId)
    {
        var settingsService = new SettingsService(_settingsRoot);
        var settings = settingsService.Settings;
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = SettingsService.DefaultTodoReminderOffsetMinutes;
        settings.Widgets =
        [
            new WidgetConfig
            {
                Id = widgetId,
                Name = "Todo",
                WidgetKind = WidgetKind.Todo
            }
        ];
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
        return settingsService;
    }

    private TodoReminderService CreateService(
        SettingsService settingsService,
        DateTimeOffset now,
        List<TodoReminderNotification> notifications)
    {
        var localization = TestServices.CreateLocalizationService();
        return new TodoReminderService(
            settingsService,
            localization,
            dispatcherQueue: null,
            notifications.Add,
            CreateStore,
            () => now);
    }

    private TodoWidgetStore CreateStore(string widgetId)
    {
        return new TodoWidgetStore(_widgetsDataRoot, widgetId);
    }
}
