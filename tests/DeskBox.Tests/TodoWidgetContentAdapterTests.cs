using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class TodoWidgetContentAdapterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _widgetsDataRoot;

    public TodoWidgetContentAdapterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public async Task InitializeAsync_LoadsTodoDataWithoutMakingTodoCreatable()
    {
        await CreateStore("todo-widget").SaveAsync(new TodoWidgetData
        {
            Items =
            [
                new TodoItem { Id = "first", Text = "first task", SortOrder = 0 },
                new TodoItem { Id = "second", Text = "second task", SortOrder = 1, IsCompleted = true }
            ]
        });
        var config = CreateConfig("todo-widget");
        var viewModel = new TodoWidgetViewModel(CreateStore("todo-widget"));
        var adapter = new TodoWidgetContentAdapter(config, viewModel);

        await adapter.InitializeAsync();

        Assert.Equal(config, adapter.Config);
        Assert.Equal("todo-widget", adapter.WidgetId);
        Assert.Equal(WidgetKind.Todo, adapter.WidgetKind);
        Assert.True(adapter.ViewModel.IsInitialized);
        Assert.Collection(
            adapter.ViewModel.Items,
            item => Assert.Equal("first task", item.Text),
            item =>
            {
                Assert.Equal("second task", item.Text);
                Assert.True(item.IsCompleted);
            });
        Assert.False(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Todo));
    }

    [Fact]
    public async Task RefreshAsync_ReloadsTodoData()
    {
        var config = CreateConfig("todo-widget");
        var store = CreateStore("todo-widget");
        var adapter = new TodoWidgetContentAdapter(config, new TodoWidgetViewModel(store));

        await adapter.InitializeAsync();
        Assert.Empty(adapter.ViewModel.Items);

        await store.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "later", Text = "later task" }]
        });

        await adapter.RefreshAsync();

        Assert.Equal("later task", Assert.Single(adapter.ViewModel.Items).Text);
    }

    [Fact]
    public void Constructor_RejectsNonTodoConfig()
    {
        var config = new WidgetConfig
        {
            Id = "file-widget",
            WidgetKind = WidgetKind.File
        };

        Assert.Throws<ArgumentException>(() => new TodoWidgetContentAdapter(config, CreateStore("file-widget")));
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

    private static WidgetConfig CreateConfig(string widgetId)
    {
        return new WidgetConfig
        {
            Id = widgetId,
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };
    }
}
