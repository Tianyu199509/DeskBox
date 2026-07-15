using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class TodoWidgetViewModelTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _widgetsDataRoot;

    public TodoWidgetViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public async Task InitializeAsync_LoadsExistingItemsInSortOrder()
    {
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "second", Text = "second", SortOrder = 1 },
                new TodoItem { Id = "first", Text = "first", SortOrder = 0, IsCompleted = true }
            ]
        });
        var viewModel = CreateViewModel("todo-widget");

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsInitialized);
        Assert.Collection(
            viewModel.Items,
            item =>
            {
                Assert.Equal("first", item.Id);
                Assert.True(item.IsCompleted);
            },
            item => Assert.Equal("second", item.Id));
        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.ActiveCount);
        Assert.Equal(1, viewModel.CompletedCount);
    }

    [Fact]
    public async Task AddItemAsync_TrimsTextAddsNewestFirstAndPersists()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();

        var first = await viewModel.AddItemAsync(" first ");
        var second = await viewModel.AddItemAsync("second");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Collection(
            viewModel.Items,
            item =>
            {
                Assert.Equal(second.Id, item.Id);
                Assert.Equal("second", item.Text);
                Assert.Equal(0, item.SortOrder);
            },
            item =>
            {
                Assert.Equal(first.Id, item.Id);
                Assert.Equal("first", item.Text);
                Assert.Equal(1, item.SortOrder);
            });

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Collection(
            reloaded.Items,
            item => Assert.Equal(second.Id, item.Id),
            item => Assert.Equal(first.Id, item.Id));
    }

    [Fact]
    public async Task AddItemAsync_UsesConfiguredBottomPosition()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.TodoNewTaskPosition = SettingsService.TodoNewTaskPositionBottom;
        var viewModel = CreateViewModel("todo-widget", settingsService);
        await viewModel.InitializeAsync();

        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Collection(
            viewModel.Items,
            item => Assert.Equal(first.Id, item.Id),
            item => Assert.Equal(second.Id, item.Id));
    }

    [Fact]
    public async Task AddInputAsync_ClearsInputOnlyAfterAdding()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();

        viewModel.InputText = "  task  ";
        var added = await viewModel.AddInputAsync();

        Assert.NotNull(added);
        Assert.Equal(string.Empty, viewModel.InputText);
        Assert.False(viewModel.CanAddInput);

        viewModel.InputText = "   ";
        var ignored = await viewModel.AddInputAsync();

        Assert.Null(ignored);
        Assert.Equal("   ", viewModel.InputText);
    }

    [Fact]
    public async Task UpdateItemTextAsync_TrimsAndPersistsText()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("old");
        Assert.NotNull(item);
        var originalUpdatedAt = item.UpdatedAt;

        bool updated = await viewModel.UpdateItemTextAsync(item.Id, " new ");

        Assert.True(updated);
        Assert.Equal("new", item.Text);
        Assert.True(item.UpdatedAt >= originalUpdatedAt);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Equal("new", Assert.Single(reloaded.Items).Text);
    }

    [Fact]
    public async Task UpdateItemTextAsync_RejectsEmptyTextAndMissingItem()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("keep");
        Assert.NotNull(item);

        Assert.False(await viewModel.UpdateItemTextAsync(item.Id, "   "));
        Assert.False(await viewModel.UpdateItemTextAsync("missing", "new"));
        Assert.Equal("keep", item.Text);
    }

    [Fact]
    public async Task SetCompletedAsync_UpdatesCountsFilterAndPersistence()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);

        bool completed = await viewModel.SetCompletedAsync(item.Id, true);

        Assert.True(completed);
        Assert.True(item.IsCompleted);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(0, viewModel.ActiveCount);
        Assert.Equal(1, viewModel.CompletedCount);
        Assert.True(viewModel.HasCompletedItems);

        viewModel.SelectedFilter = TodoFilter.Active;
        Assert.Empty(viewModel.VisibleItems);
        viewModel.SelectedFilter = TodoFilter.Completed;
        Assert.Single(viewModel.VisibleItems);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.True(Assert.Single(reloaded.Items).IsCompleted);
        Assert.NotNull(Assert.Single(reloaded.Items).CompletedAt);
    }

    [Fact]
    public async Task SetCompletedAsync_StoresAndClearsCompletedAt()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);

        Assert.True(await viewModel.SetCompletedAsync(item.Id, true));
        Assert.NotNull(item.CompletedAt);
        Assert.True(item.ContentOpacity < 1);
        Assert.Equal(Windows.UI.Text.TextDecorations.Strikethrough, item.TextDecorations);

        Assert.True(await viewModel.SetCompletedAsync(item.Id, false));
        Assert.Null(item.CompletedAt);
        Assert.Equal(1, item.ContentOpacity);
        Assert.Equal(Windows.UI.Text.TextDecorations.None, item.TextDecorations);
    }

    [Fact]
    public async Task SetImportantAsync_UpdatesFilterAndPersistence()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var normal = await viewModel.AddItemAsync("normal");
        var important = await viewModel.AddItemAsync("important");
        Assert.NotNull(normal);
        Assert.NotNull(important);

        bool marked = await viewModel.SetImportantAsync(important.Id, true);

        Assert.True(marked);
        Assert.True(important.IsImportant);
        viewModel.SelectedFilter = TodoFilter.Important;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(important.Id, viewModel.VisibleItems[0].Id);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.True(reloaded.Items.Single(item => item.Id == important.Id).IsImportant);
        Assert.False(await viewModel.SetImportantAsync("missing", true));
    }

    [Fact]
    public async Task SetColorMarkerAsync_FiltersAndPersistsColorMarkers()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var normal = await viewModel.AddItemAsync("normal");
        var red = await viewModel.AddItemAsync("red");
        var blue = await viewModel.AddItemAsync("blue");
        Assert.NotNull(normal);
        Assert.NotNull(red);
        Assert.NotNull(blue);

        bool marked = await viewModel.SetColorMarkerAsync(red.Id, TodoItem.RedColorMarker);
        bool blueMarked = await viewModel.SetColorMarkerAsync(blue.Id, TodoItem.BlueColorMarker);

        Assert.True(marked);
        Assert.True(blueMarked);
        Assert.True(red.HasRedMarker);
        Assert.True(blue.HasColorMarker);
        Assert.Equal(1, viewModel.RedColorFilterCount);
        Assert.Equal(1, viewModel.BlueColorFilterCount);
        Assert.Equal(2, viewModel.AnyColorFilterCount);
        viewModel.SelectedColorFilter = TodoColorFilter.Red;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(red.Id, viewModel.VisibleItems[0].Id);
        viewModel.SelectedColorFilter = TodoColorFilter.Blue;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(blue.Id, viewModel.VisibleItems[0].Id);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        var reloadedRed = reloaded.Items.Single(item => item.Id == red.Id);
        Assert.True(reloadedRed.HasRedMarker);
        Assert.Equal(TodoItem.BlueColorMarker, reloaded.Items.Single(item => item.Id == blue.Id).ColorMarker);
        Assert.False(await viewModel.SetColorMarkerAsync("missing", TodoItem.RedColorMarker));
    }

    [Fact]
    public async Task SetColorMarkerAsync_ClearsMarkerAndKeepsSelectedFilter()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);

        Assert.True(await viewModel.SetColorMarkerAsync(item.Id, TodoItem.RedColorMarker));
        Assert.True(item.HasRedMarker);
        viewModel.SelectedColorFilter = TodoColorFilter.Red;
        Assert.Single(viewModel.VisibleItems);

        Assert.True(await viewModel.SetColorMarkerAsync(item.Id, null));
        Assert.False(item.HasColorMarker);
        Assert.Empty(viewModel.VisibleItems);
        Assert.Equal(0, viewModel.RedColorFilterCount);
    }

    [Fact]
    public async Task ColorFilters_AreAlwaysVisibleAndSingleSelection()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();

        Assert.Equal(Visibility.Visible, viewModel.ColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.RedColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.OrangeColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.YellowColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.GreenColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.BlueColorFilterVisibility);
        Assert.Equal(Visibility.Visible, viewModel.PurpleColorFilterVisibility);
        Assert.Equal(TodoColorFilter.All, viewModel.SelectedColorFilter);
        Assert.False(viewModel.IsRedColorFilterSelected);
        Assert.False(viewModel.IsBlueColorFilterSelected);

        viewModel.SetColorFilter(TodoColorFilter.Red);

        Assert.True(viewModel.IsRedColorFilterSelected);
        Assert.False(viewModel.IsBlueColorFilterSelected);

        viewModel.SetColorFilter(TodoColorFilter.Blue);

        Assert.False(viewModel.IsRedColorFilterSelected);
        Assert.True(viewModel.IsBlueColorFilterSelected);
    }

    [Fact]
    public async Task EditState_CommitsAndCancelsWithoutLosingOriginalText()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("old");
        Assert.NotNull(item);

        viewModel.BeginEdit(item.Id);
        Assert.True(item.IsEditing);
        Assert.Equal("old", item.EditText);

        item.EditText = " new ";
        bool committed = await viewModel.CommitEditAsync(item.Id);

        Assert.True(committed);
        Assert.False(item.IsEditing);
        Assert.Equal("new", item.Text);

        viewModel.BeginEdit(item.Id);
        item.EditText = "discard";
        viewModel.CancelEdit(item.Id);

        Assert.False(item.IsEditing);
        Assert.Equal("new", item.Text);
        Assert.Equal("new", item.EditText);
    }

    [Fact]
    public async Task DeleteItemAsync_RemovesItemAndPersists()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");
        Assert.NotNull(first);
        Assert.NotNull(second);

        bool deleted = await viewModel.DeleteItemAsync(first.Id);

        Assert.True(deleted);
        Assert.Single(viewModel.Items);
        Assert.Equal(second.Id, viewModel.Items[0].Id);
        Assert.Equal(0, viewModel.Items[0].SortOrder);
        Assert.False(await viewModel.DeleteItemAsync("missing"));

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Equal(second.Id, Assert.Single(reloaded.Items).Id);
    }

    [Fact]
    public async Task BatchActions_UpdateAndDeleteSelectedItemsOnly()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");
        var third = await viewModel.AddItemAsync("third");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        await viewModel.SetDueDateAsync(first.Id, DateTimeOffset.Now.AddDays(1));

        int colored = await viewModel.SetColorMarkerAsync([first.Id, third.Id], TodoItem.BlueColorMarker);
        int reminded = await viewModel.SetReminderOffsetAsync([first.Id, third.Id], 60);
        int deleted = await viewModel.DeleteItemsAsync([first.Id, third.Id]);

        Assert.Equal(2, colored);
        Assert.Equal(1, reminded);
        Assert.Equal(2, deleted);
        Assert.Equal(second.Id, Assert.Single(viewModel.Items).Id);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Equal(second.Id, Assert.Single(reloaded.Items).Id);
    }

    [Fact]
    public async Task BatchDueDateAndRecurrence_UpdateSelectedItemsOnly()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");
        var third = await viewModel.AddItemAsync("third");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        int dated = await viewModel.SetDueDatePresetAsync(
            [first.Id, third.Id],
            TodoDuePreset.Tomorrow);
        int repeated = await viewModel.SetRecurrenceAsync(
            [first.Id, third.Id],
            TodoRecurrenceMode.Weekly);

        Assert.Equal(2, dated);
        Assert.Equal(2, repeated);
        Assert.NotNull(first.DueDate);
        Assert.NotNull(third.DueDate);
        Assert.Null(second.DueDate);
        Assert.Equal(TodoRecurrenceMode.Weekly, first.RecurrenceMode);
        Assert.Equal(TodoRecurrenceMode.Weekly, third.RecurrenceMode);
        Assert.Equal(TodoRecurrenceMode.None, second.RecurrenceMode);

        Assert.Equal(2, await viewModel.SetDueDatePresetAsync(
            [first.Id, third.Id],
            TodoDuePreset.Clear));
        Assert.Null(first.DueDate);
        Assert.Null(first.Recurrence);
        Assert.Null(third.DueDate);
        Assert.Null(third.Recurrence);
    }

    [Fact]
    public async Task ClearCompletedAsync_RemovesOnlyCompletedItems()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var active = await viewModel.AddItemAsync("active");
        var completed = await viewModel.AddItemAsync("completed");
        Assert.NotNull(active);
        Assert.NotNull(completed);
        await viewModel.SetCompletedAsync(completed.Id, true);

        int removed = await viewModel.ClearCompletedAsync();

        Assert.Equal(1, removed);
        Assert.Single(viewModel.Items);
        Assert.Equal(active.Id, viewModel.Items[0].Id);
        Assert.False(viewModel.HasCompletedItems);
        Assert.Equal(0, await viewModel.ClearCompletedAsync());
    }

    [Fact]
    public async Task SelectedFilter_ControlsVisibleItems()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var active = await viewModel.AddItemAsync("active");
        var completed = await viewModel.AddItemAsync("completed");
        Assert.NotNull(active);
        Assert.NotNull(completed);
        await viewModel.SetCompletedAsync(completed.Id, true);

        viewModel.SelectedFilter = TodoFilter.All;
        Assert.Equal(2, viewModel.VisibleItems.Count);

        viewModel.SelectedFilter = TodoFilter.Active;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(active.Id, viewModel.VisibleItems[0].Id);

        await viewModel.SetImportantAsync(active.Id, true);
        viewModel.SelectedFilter = TodoFilter.Important;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(active.Id, viewModel.VisibleItems[0].Id);

        viewModel.SelectedFilter = TodoFilter.Completed;
        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(completed.Id, viewModel.VisibleItems[0].Id);
    }

    [Fact]
    public async Task TodayFilter_ShowsTasksDueTodayOnly()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var today = await viewModel.AddItemAsync("today");
        var tomorrow = await viewModel.AddItemAsync("tomorrow");
        Assert.NotNull(today);
        Assert.NotNull(tomorrow);

        await viewModel.SetDueDatePresetAsync(today.Id, TodoDuePreset.Today);
        await viewModel.SetDueDatePresetAsync(tomorrow.Id, TodoDuePreset.Tomorrow);
        viewModel.SelectedFilter = TodoFilter.Today;

        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(today.Id, viewModel.VisibleItems[0].Id);
    }

    [Fact]
    public async Task SetDueDateAsync_PreservesTimePrecision()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("timed task");
        Assert.NotNull(item);
        var localDueDate = DateTimeOffset.Now.Date
            .AddDays(3)
            .AddHours(18)
            .AddMinutes(30)
            .AddSeconds(15);
        var dueDate = new DateTimeOffset(localDueDate, TimeZoneInfo.Local.GetUtcOffset(localDueDate));

        Assert.True(await viewModel.SetDueDateAsync(item.Id, dueDate));

        Assert.Equal(dueDate, item.DueDate);
        Assert.Contains("18:30:15", item.DueStatusText, StringComparison.Ordinal);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Equal(dueDate, Assert.Single(reloaded.Items).DueDate);
    }

    [Fact]
    public async Task SetRecurrenceAsync_PersistsAndPrefixesDueStatus()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("repeat");
        Assert.NotNull(item);
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Local));

        Assert.True(await viewModel.SetDueDateAsync(item.Id, dueDate));
        Assert.True(await viewModel.SetRecurrenceAsync(item.Id, TodoRecurrenceMode.Daily));

        Assert.Equal(TodoRecurrenceMode.Daily, item.RecurrenceMode);
        Assert.Contains("Daily", item.DueStatusText, StringComparison.Ordinal);

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        var reloadedItem = Assert.Single(reloaded.Items);
        Assert.Equal(TodoRecurrenceMode.Daily, reloadedItem.RecurrenceMode);
        Assert.Equal(dueDate, reloadedItem.Recurrence?.AnchorDueDate);
    }

    [Fact]
    public async Task SetDueDateAsync_ClearingDueDateAlsoClearsRecurrence()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("repeat");
        Assert.NotNull(item);
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Local));

        Assert.True(await viewModel.SetDueDateAsync(item.Id, dueDate));
        Assert.True(await viewModel.SetRecurrenceAsync(item.Id, TodoRecurrenceMode.Weekly));
        Assert.True(await viewModel.SetReminderOffsetAsync(item.Id, 30));
        Assert.True(await viewModel.SetDueDateAsync(item.Id, null));

        Assert.Null(item.DueDate);
        Assert.Null(item.Recurrence);
        Assert.Null(item.ReminderOffsetMinutes);
        Assert.Equal(string.Empty, item.DueStatusText);
    }

    [Fact]
    public async Task SetReminderOffsetAsync_RejectsTaskWithoutDueDate()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("no due date");
        Assert.NotNull(item);

        Assert.False(await viewModel.SetReminderOffsetAsync(item.Id, 30));
        Assert.Null(item.ReminderOffsetMinutes);
    }

    [Fact]
    public async Task SetCompletedAsync_ForRecurringTask_CreatesNextOccurrence()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("repeat");
        Assert.NotNull(item);
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Local));

        Assert.True(await viewModel.SetDueDateAsync(item.Id, dueDate));
        Assert.True(await viewModel.SetRecurrenceAsync(item.Id, TodoRecurrenceMode.Daily));
        Assert.True(await viewModel.SetCompletedAsync(item.Id, true));

        Assert.Equal(2, viewModel.Items.Count);
        Assert.True(item.IsCompleted);
        Assert.NotNull(item.CompletedAt);
        Assert.False(string.IsNullOrWhiteSpace(item.Item.GeneratedNextItemId));

        var nextItem = Assert.Single(viewModel.Items.Where(entry => entry.Id != item.Id));
        Assert.False(nextItem.IsCompleted);
        Assert.Equal(item.Text, nextItem.Text);
        Assert.Equal(TodoRecurrenceMode.Daily, nextItem.RecurrenceMode);
        Assert.True(nextItem.DueDate > item.CompletedAt);
        Assert.Equal(item.Recurrence?.AnchorDueDate, nextItem.Recurrence?.AnchorDueDate);
    }

    [Fact]
    public async Task SetCompletedAsync_UncompletingRecurringTaskRemovesUntouchedGeneratedOccurrence()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("repeat");
        Assert.NotNull(item);
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Local));

        Assert.True(await viewModel.SetDueDateAsync(item.Id, dueDate));
        Assert.True(await viewModel.SetRecurrenceAsync(item.Id, TodoRecurrenceMode.Weekly));
        Assert.True(await viewModel.SetCompletedAsync(item.Id, true));
        Assert.True(await viewModel.SetCompletedAsync(item.Id, false));

        Assert.Single(viewModel.Items);
        Assert.False(item.IsCompleted);
        Assert.Null(item.CompletedAt);
        Assert.Null(item.Item.GeneratedNextItemId);
    }

    [Fact]
    public async Task RefreshVisibleItems_CollapsesCompletedRecurringHistoryByDefaultAndCanExpand()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("repeat");
        Assert.NotNull(first);
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 10, 30, 0, DateTimeKind.Local));

        Assert.True(await viewModel.SetDueDateAsync(first.Id, dueDate));
        Assert.True(await viewModel.SetRecurrenceAsync(first.Id, TodoRecurrenceMode.Daily));
        Assert.True(await viewModel.SetCompletedAsync(first.Id, true));

        var second = Assert.Single(viewModel.Items.Where(item => !item.IsCompleted));
        Assert.True(await viewModel.SetCompletedAsync(second.Id, true));

        var activeCurrent = Assert.Single(viewModel.Items.Where(item => !item.IsCompleted));
        var standalone = await viewModel.AddItemAsync("one-off");
        Assert.NotNull(standalone);
        Assert.True(await viewModel.SetCompletedAsync(standalone.Id, true));

        viewModel.SelectedFilter = TodoFilter.All;

        var recurringLead = Assert.Single(viewModel.VisibleItems.Where(item => item.IsRecurringHistoryLead));
        Assert.Equal(2, recurringLead.RecurringHistoryItemCount);
        Assert.False(recurringLead.IsRecurringHistoryExpanded);
        Assert.Equal(1, recurringLead.HiddenRecurringHistoryCount);
        Assert.Contains(activeCurrent, viewModel.VisibleItems);
        Assert.Contains(standalone, viewModel.VisibleItems);
        Assert.Equal(3, viewModel.VisibleItems.Count);

        viewModel.ToggleRecurringHistoryGroup(recurringLead.RecurrenceSeriesId);

        recurringLead = Assert.Single(viewModel.VisibleItems.Where(item => item.IsRecurringHistoryLead));
        Assert.True(recurringLead.IsRecurringHistoryExpanded);
        Assert.Equal(4, viewModel.VisibleItems.Count);
        Assert.Equal(2, viewModel.VisibleItems.Count(item =>
            string.Equals(item.RecurrenceSeriesId, recurringLead.RecurrenceSeriesId, StringComparison.Ordinal) &&
            item.IsCompleted));
    }

    [Fact]
    public async Task VisibleItems_SortActiveByDueDateAndCompletedAfterActive()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var noDue = await viewModel.AddItemAsync("no due");
        var later = await viewModel.AddItemAsync("later");
        var sooner = await viewModel.AddItemAsync("sooner");
        var completed = await viewModel.AddItemAsync("completed");
        Assert.NotNull(noDue);
        Assert.NotNull(later);
        Assert.NotNull(sooner);
        Assert.NotNull(completed);
        var now = DateTimeOffset.Now;

        await viewModel.SetDueDateAsync(later.Id, now.AddHours(3));
        await viewModel.SetDueDateAsync(sooner.Id, now.AddHours(1));
        await viewModel.SetCompletedAsync(completed.Id, true);
        viewModel.SelectedFilter = TodoFilter.All;

        Assert.Collection(
            viewModel.VisibleItems,
            item => Assert.Equal(sooner.Id, item.Id),
            item => Assert.Equal(later.Id, item.Id),
            item => Assert.Equal(noDue.Id, item.Id),
            item => Assert.Equal(completed.Id, item.Id));
    }

    [Fact]
    public async Task HideCompletedSetting_HidesCompletedOutsideCompletedFilter()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.TodoShowCompletedTasks = false;
        var viewModel = CreateViewModel("todo-widget", settingsService);
        await viewModel.InitializeAsync();
        var active = await viewModel.AddItemAsync("active");
        var completed = await viewModel.AddItemAsync("completed");
        Assert.NotNull(active);
        Assert.NotNull(completed);

        await viewModel.SetCompletedAsync(completed.Id, true);
        viewModel.SelectedFilter = TodoFilter.All;

        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(active.Id, viewModel.VisibleItems[0].Id);

        viewModel.SelectedFilter = TodoFilter.Completed;

        Assert.Single(viewModel.VisibleItems);
        Assert.Equal(completed.Id, viewModel.VisibleItems[0].Id);
    }

    [Fact]
    public async Task DefaultFilterSetting_AppliesOnInitialize()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.TodoDefaultFilter = SettingsService.TodoDefaultFilterImportant;
        var viewModel = CreateViewModel("todo-widget", settingsService);

        await viewModel.InitializeAsync();

        Assert.Equal(TodoFilter.Important, viewModel.SelectedFilter);
    }

    [Fact]
    public async Task DeleteAndClearCompleted_CanUndo()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var active = await viewModel.AddItemAsync("active");
        var completed = await viewModel.AddItemAsync("completed");
        Assert.NotNull(active);
        Assert.NotNull(completed);
        await viewModel.SetCompletedAsync(completed.Id, true);

        Assert.True(await viewModel.DeleteItemAsync(active.Id));
        Assert.True(viewModel.CanUndoLastAction);
        Assert.True(await viewModel.UndoLastActionAsync());
        Assert.Equal(2, viewModel.Items.Count);

        Assert.Equal(1, await viewModel.ClearCompletedAsync());
        Assert.Single(viewModel.Items);
        Assert.True(await viewModel.UndoLastActionAsync());
        Assert.Equal(2, viewModel.Items.Count);
    }

    [Fact]
    public async Task MoveItemAsync_ReordersVisibleItemsAndPersists()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");
        var third = await viewModel.AddItemAsync("third");
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);

        Assert.True(await viewModel.MoveItemAsync(third.Id, 2));

        Assert.Collection(
            viewModel.Items,
            item => Assert.Equal(second.Id, item.Id),
            item => Assert.Equal(first.Id, item.Id),
            item => Assert.Equal(third.Id, item.Id));

        var reloaded = CreateViewModel("todo-widget");
        await reloaded.InitializeAsync();
        Assert.Collection(
            reloaded.Items,
            item => Assert.Equal(second.Id, item.Id),
            item => Assert.Equal(first.Id, item.Id),
            item => Assert.Equal(third.Id, item.Id));
    }

    [Fact]
    public async Task MoveItemAsync_ManualOrderWinsOverImportanceAndDueDate()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var first = await viewModel.AddItemAsync("first");
        var second = await viewModel.AddItemAsync("second");
        Assert.NotNull(first);
        Assert.NotNull(second);

        await viewModel.SetImportantAsync(first.Id, true);
        await viewModel.SetDueDateAsync(first.Id, DateTimeOffset.Now.AddDays(-1));
        Assert.True(await viewModel.MoveItemAsync(first.Id, 0));

        Assert.Equal(new[] { first.Id, second.Id }, viewModel.VisibleItems.Select(item => item.Id));
    }

    [Fact]
    public async Task EmptyAndListVisibility_FollowVisibleItems()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasVisibleItems);
        Assert.Equal(Visibility.Collapsed, viewModel.ListVisibility);
        Assert.Equal(Visibility.Visible, viewModel.EmptyStateVisibility);
        Assert.Equal("Add a task to get started.", viewModel.EmptyStateText);

        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);

        Assert.True(viewModel.HasVisibleItems);
        Assert.Equal(Visibility.Visible, viewModel.ListVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.EmptyStateVisibility);

        await viewModel.SetCompletedAsync(item.Id, true);
        viewModel.SelectedFilter = TodoFilter.Active;

        Assert.False(viewModel.HasVisibleItems);
        Assert.Equal(Visibility.Collapsed, viewModel.ListVisibility);
        Assert.Equal(Visibility.Visible, viewModel.EmptyStateVisibility);
        Assert.Equal("No active tasks.", viewModel.EmptyStateText);

        viewModel.SelectedFilter = TodoFilter.Important;

        Assert.False(viewModel.HasVisibleItems);
        Assert.Equal("No important tasks.", viewModel.EmptyStateText);
    }

    [Fact]
    public async Task AddInputAsync_AppliesAndResetsDraftMetadata()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        DateTimeOffset dueDate = DateTimeOffset.Now.AddDays(1);
        viewModel.InputText = "task with metadata";
        viewModel.DraftImportant = true;
        viewModel.DraftDueDate = dueDate;

        TodoItemViewModel? item = await viewModel.AddInputAsync();

        Assert.NotNull(item);
        Assert.Equal("task with metadata", item.Text);
        Assert.True(item.IsImportant);
        Assert.Equal(dueDate.ToLocalTime().Date, item.DueDate?.ToLocalTime().Date);
        Assert.Equal(dueDate.ToLocalTime().Hour, item.DueDate?.ToLocalTime().Hour);
        Assert.Equal(dueDate.ToLocalTime().Minute, item.DueDate?.ToLocalTime().Minute);
        Assert.Empty(viewModel.InputText);
        Assert.False(viewModel.DraftImportant);
        Assert.Null(viewModel.DraftDueDate);

        TodoWidgetData reloaded = await CreateStore("todo-widget").LoadAsync();
        TodoItem savedItem = Assert.Single(reloaded.Items);
        Assert.True(savedItem.IsImportant);
        Assert.Equal(item.DueDate, savedItem.DueDate);
    }

    [Fact]
    public async Task ToggleExpanded_KeepsOnlyOneExpandedItemAndCancelsPreviousEdit()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        TodoItemViewModel first = (await viewModel.AddItemAsync("first"))!;
        TodoItemViewModel second = (await viewModel.AddItemAsync("second"))!;

        viewModel.ToggleExpanded(first.Id);
        viewModel.BeginEdit(first.Id);
        viewModel.ToggleExpanded(second.Id);

        Assert.False(first.IsExpanded);
        Assert.False(first.IsEditing);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public async Task ImportantItems_AreSortedBeforeOtherActiveItems()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        TodoItemViewModel regular = (await viewModel.AddItemAsync("regular"))!;
        TodoItemViewModel important = (await viewModel.AddItemAsync("important"))!;

        Assert.True(await viewModel.SetImportantAsync(important.Id, true));

        Assert.Equal(important.Id, viewModel.VisibleItems[0].Id);
        Assert.Equal(regular.Id, viewModel.VisibleItems[1].Id);
    }

    [Fact]
    public async Task AllCompletedEmptyState_IsShownWhenCompletedTasksAreHidden()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.Language = SettingsService.LanguageChinese;
        settingsService.Settings.TodoShowCompletedTasks = false;
        var viewModel = CreateViewModel("todo-widget", settingsService);
        await viewModel.InitializeAsync();
        TodoItemViewModel item = (await viewModel.AddItemAsync("task"))!;

        await viewModel.SetCompletedAsync(item.Id, true);

        Assert.True(viewModel.IsAllTasksCompletedEmptyState);
        Assert.Equal(LocalizationService.DefaultText("Todo.Empty.AllCompleted"), viewModel.EmptyStateTitle);
        Assert.Equal(LocalizationService.DefaultText("Todo.Empty.AllCompletedDesc"), viewModel.EmptyStateText);
        Assert.Equal(Visibility.Visible, viewModel.EmptyStateVisibility);
    }

    [Fact]
    public async Task DetailNavigation_OpensSelectedTaskAndReturnsToList()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        TodoItemViewModel item = (await viewModel.AddItemAsync("task"))!;

        TodoItemViewModel? opened = viewModel.OpenDetail(item.Id);

        Assert.Same(item, opened);
        Assert.Same(item, viewModel.SelectedDetailItem);
        Assert.True(viewModel.IsDetailPageOpen);
        Assert.Equal(Visibility.Collapsed, viewModel.ListPageVisibility);
        Assert.Equal(Visibility.Visible, viewModel.DetailPageVisibility);

        viewModel.CloseDetail();

        Assert.Null(viewModel.SelectedDetailItem);
        Assert.False(viewModel.IsDetailPageOpen);
        Assert.Equal(Visibility.Visible, viewModel.ListPageVisibility);
    }

    [Fact]
    public async Task NewDetail_EmptyTitleDiscardsDraftWithoutPersisting()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();

        TodoItemViewModel draft = viewModel.OpenNewDetail();
        Assert.True(viewModel.IsCreatingDetailItem);
        Assert.Same(draft, viewModel.SelectedDetailItem);
        Assert.Empty(viewModel.Items);
        Assert.Empty(viewModel.VisibleItems);

        Assert.NotNull(await viewModel.AddStepAsync(draft.Id, "draft step"));
        Assert.True(await viewModel.UpdateNotesAsync(draft.Id, "draft note"));
        Assert.True(await viewModel.SetColorMarkerAsync(draft.Id, TodoItem.BlueColorMarker));
        Assert.Empty((await CreateStore("todo-widget").LoadAsync()).Items);

        TodoItemViewModel? finalized = await viewModel.FinalizeDetailAsync("   ");

        Assert.Null(finalized);
        Assert.False(viewModel.IsCreatingDetailItem);
        Assert.Null(viewModel.SelectedDetailItem);
        Assert.Empty(viewModel.Items);
        Assert.Empty((await CreateStore("todo-widget").LoadAsync()).Items);
    }

    [Fact]
    public async Task NewDetail_WithTitleFinalizesDraftAndPersistsDetailContent()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        TodoItemViewModel draft = viewModel.OpenNewDetail();

        Assert.NotNull(await viewModel.AddStepAsync(draft.Id, "first step"));
        Assert.True(await viewModel.UpdateNotesAsync(draft.Id, "detail note"));
        Assert.True(await viewModel.SetColorMarkerAsync(draft.Id, TodoItem.GreenColorMarker));

        TodoItemViewModel? finalized = await viewModel.FinalizeDetailAsync("  new task  ");

        Assert.Same(draft, finalized);
        Assert.False(viewModel.IsCreatingDetailItem);
        Assert.Null(viewModel.SelectedDetailItem);
        Assert.Same(draft, Assert.Single(viewModel.Items));
        Assert.Same(draft, Assert.Single(viewModel.VisibleItems));
        Assert.Equal("new task", draft.Text);

        TodoItem saved = Assert.Single((await CreateStore("todo-widget").LoadAsync()).Items);
        Assert.Equal("new task", saved.Text);
        Assert.Equal("first step", Assert.Single(saved.Steps).Text);
        Assert.Equal("detail note", saved.Notes);
        Assert.Equal(TodoItem.GreenColorMarker, saved.ColorMarker);
    }

    [Fact]
    public async Task DetailContent_PersistsStepsNotesAndAttachments()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        TodoItemViewModel item = (await viewModel.AddItemAsync("task"))!;

        TodoStepViewModel step = (await viewModel.AddStepAsync(item.Id, "first step"))!;
        Assert.True(await viewModel.SetStepCompletedAsync(item.Id, step.Id, true));
        Assert.True(await viewModel.UpdateNotesAsync(item.Id, "detail note"));
        TodoAttachmentViewModel attachment = (await viewModel.AddAttachmentAsync(
            item.Id,
            "C:\\Temp\\brief.pdf",
            "brief.pdf",
            "pdf"))!;

        Assert.Equal("1/1", item.StepProgressText);
        Assert.Equal("detail note", item.Notes);
        Assert.Single(item.Attachments);

        TodoWidgetData saved = await CreateStore("todo-widget").LoadAsync();
        TodoItem savedItem = Assert.Single(saved.Items);
        Assert.True(Assert.Single(savedItem.Steps).IsCompleted);
        Assert.Equal("detail note", savedItem.Notes);
        Assert.Equal("brief.pdf", Assert.Single(savedItem.Attachments).DisplayName);

        Assert.True(await viewModel.DeleteAttachmentAsync(item.Id, attachment.Id));
        Assert.True(await viewModel.DeleteStepAsync(item.Id, step.Id));
        Assert.Empty(item.Attachments);
        Assert.Empty(item.Steps);
    }

    [Fact]
    public void ApplyAppearance_UsesGlobalTextSize()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.TextSize = 15;
        var viewModel = CreateViewModel("todo-widget", settingsService);

        Assert.Equal(15, viewModel.TextSize);
        Assert.Equal(14, viewModel.SecondaryTextSize);
        Assert.Equal(16, viewModel.TitleTextSize);
        Assert.Equal(15, viewModel.FilterTextSize);
        Assert.Equal(15, viewModel.SmallIconSize);
        Assert.Equal(18, viewModel.StandardIconSize);
        Assert.Equal(20, viewModel.PrimaryIconSize);
        Assert.Equal(22, viewModel.CompletionIndicatorSize);

        settingsService.Settings.TextSize = 12;
        viewModel.ApplyAppearance();

        Assert.Equal(12, viewModel.TextSize);
        Assert.Equal(11, viewModel.SecondaryTextSize);
        Assert.Equal(13, viewModel.TitleTextSize);
        Assert.Equal(12, viewModel.FilterTextSize);
        Assert.Equal(13, viewModel.SmallIconSize);
        Assert.Equal(15, viewModel.StandardIconSize);
        Assert.Equal(17, viewModel.PrimaryIconSize);
        Assert.Equal(19, viewModel.CompletionIndicatorSize);
    }

    [Fact]
    public async Task ApplyAppearance_DoesNotRebuildVisibleItemsForUnrelatedSettingsChanges()
    {
        var settingsService = new SettingsService();
        var viewModel = CreateViewModel("todo-widget", settingsService);
        await viewModel.InitializeAsync();
        TodoItemViewModel item = (await viewModel.AddItemAsync("task"))!;
        int collectionChangeCount = 0;
        viewModel.VisibleItems.CollectionChanged += (_, _) => collectionChangeCount++;

        settingsService.Settings.RecentOrganizationHistory.Add(new OrganizationHistoryEntry());
        viewModel.ApplyAppearance();

        Assert.Equal(0, collectionChangeCount);
        Assert.Same(item, Assert.Single(viewModel.VisibleItems));
    }

    [Fact]
    public async Task SetReminderOffsetAsync_PersistsTaskReminderOverride()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);
        await viewModel.SetDueDateAsync(item.Id, DateTimeOffset.Now.AddHours(2));

        Assert.True(await viewModel.SetReminderOffsetAsync(item.Id, 30));

        Assert.Equal(30, item.ReminderOffsetMinutes);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        Assert.Equal(30, Assert.Single(reloaded.Items).ReminderOffsetMinutes);
    }

    [Fact]
    public async Task SnoozeReminderAsync_PersistsSnoozeAndClearsWhenDueDateChanges()
    {
        var viewModel = CreateViewModel("todo-widget");
        await viewModel.InitializeAsync();
        var item = await viewModel.AddItemAsync("task");
        Assert.NotNull(item);
        var dueDate = DateTimeOffset.Now.AddHours(2);
        await viewModel.SetDueDateAsync(item.Id, dueDate);
        DateTimeOffset normalizedDueDate = item.DueDate!.Value;

        Assert.True(await viewModel.SnoozeReminderAsync(item.Id, TimeSpan.FromMinutes(10)));
        Assert.NotNull(item.SnoozedUntil);
        Assert.Equal(normalizedDueDate, item.Item.ReminderDismissedForDueDate);

        Assert.True(await viewModel.SetDueDateAsync(item.Id, normalizedDueDate.AddHours(1)));

        Assert.Null(item.SnoozedUntil);
        Assert.Null(item.Item.SnoozeLastNotifiedAt);
        Assert.Null(item.Item.ReminderDismissedForDueDate);
        var reloaded = await CreateStore("todo-widget").LoadAsync();
        Assert.Null(Assert.Single(reloaded.Items).SnoozedUntil);
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

    private TodoWidgetStore CreateStore(string widgetId)
    {
        return new TodoWidgetStore(_widgetsDataRoot, widgetId);
    }

    private TodoWidgetViewModel CreateViewModel(string widgetId, SettingsService? settingsService = null)
    {
        var config = new WidgetConfig
        {
            Id = widgetId,
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };

        return new TodoWidgetViewModel(
            CreateStore(widgetId),
            settingsService is null
                ? TestServices.CreateLocalizationService()
                : new LocalizationService(settingsService),
            config,
            settingsService);
    }
}
