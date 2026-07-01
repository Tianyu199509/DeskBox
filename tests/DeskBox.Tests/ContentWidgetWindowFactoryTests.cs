using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class ContentWidgetWindowFactoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _widgetsDataRoot;

    public ContentWidgetWindowFactoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _widgetsDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "widgets")).FullName;
    }

    [Fact]
    public void CreateContentWindowPlan_ReturnsTodoAdapterForCreatableTodoKind()
    {
        var config = CreateConfig("todo-window", WidgetKind.Todo);
        var factory = CreateFactory();

        var plan = factory.CreateContentWindowPlan(config);

        Assert.Equal(config, plan.Config);
        Assert.Equal(WidgetKind.Todo, plan.Descriptor.WidgetKind);
        Assert.IsType<TodoWidgetContentAdapter>(plan.Content);
        Assert.True(factory.CanCreateContentWindow(WidgetKind.Todo));
        Assert.True(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Todo));
    }

    [Fact]
    public void CreateContentWindowPlan_ReturnsMusicAdapterForCreatableMusicKind()
    {
        var config = CreateConfig("music-window", WidgetKind.Music);
        var factory = CreateFactory();

        var plan = factory.CreateContentWindowPlan(config);

        Assert.Equal(config, plan.Config);
        Assert.Equal(WidgetKind.Music, plan.Descriptor.WidgetKind);
        Assert.IsType<MusicWidgetContentAdapter>(plan.Content);
        Assert.True(factory.CanCreateContentWindow(WidgetKind.Music));
        Assert.True(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Music));
    }

    [Theory]
    [InlineData(WidgetKind.Weather)]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.SystemMonitor)]
    public void CreateContentWindowPlan_ReturnsPlaceholderForFutureKinds(WidgetKind widgetKind)
    {
        var config = CreateConfig("future-window", widgetKind);
        var factory = CreateFactory();

        var plan = factory.CreateContentWindowPlan(config);

        Assert.Equal(widgetKind, plan.Descriptor.WidgetKind);
        Assert.IsType<PlaceholderWidgetContent>(plan.Content);
        Assert.True(factory.CanCreateContentWindow(widgetKind));
        Assert.False(WidgetRegistry.Default.CanCreateWindow(widgetKind));
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    [InlineData(WidgetKind.Productivity)]
    public void CreateContentWindowPlan_RejectsWindowOwnedAndLegacyKinds(WidgetKind widgetKind)
    {
        var config = CreateConfig("unsupported-window", widgetKind);
        var factory = CreateFactory();

        Assert.False(factory.CanCreateContentWindow(widgetKind));
        Assert.Throws<NotSupportedException>(() => factory.CreateContentWindowPlan(config));
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

    private ContentWidgetWindowFactory CreateFactory()
    {
        return new ContentWidgetWindowFactory(
            TestServices.CreateWidgetContentFactory(),
            new SettingsService(),
            todoStoreFactory: widget => new TodoWidgetStore(_widgetsDataRoot, widget.Id));
    }

    private static WidgetConfig CreateConfig(string id, WidgetKind widgetKind)
    {
        return new WidgetConfig
        {
            Id = id,
            Name = widgetKind.ToString(),
            WidgetKind = widgetKind
        };
    }
}
