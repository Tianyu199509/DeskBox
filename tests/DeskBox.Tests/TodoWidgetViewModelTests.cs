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
    public void ApplyAppearance_UsesGlobalTextSize()
    {
        var settingsService = new SettingsService();
        settingsService.Settings.TextSize = 15;
        var viewModel = CreateViewModel("todo-widget", settingsService);

        Assert.Equal(15, viewModel.TextSize);
        Assert.Equal(13, viewModel.SecondaryTextSize);
        Assert.Equal(18, viewModel.TitleTextSize);
        Assert.Equal(15, viewModel.FilterTextSize);

        settingsService.Settings.TextSize = 12;
        viewModel.ApplyAppearance();

        Assert.Equal(12, viewModel.TextSize);
        Assert.Equal(10, viewModel.SecondaryTextSize);
        Assert.Equal(15, viewModel.TitleTextSize);
        Assert.Equal(12, viewModel.FilterTextSize);
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
