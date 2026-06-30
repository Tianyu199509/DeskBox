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
    public void CreateHiddenContentWindowPlan_ReturnsTodoAdapterWithoutOpeningRegistry()
    {
        var config = CreateConfig("todo-window", WidgetKind.Todo);
        var factory = CreateFactory();

        var plan = factory.CreateHiddenContentWindowPlan(config);

        Assert.Equal(config, plan.Config);
        Assert.Equal(WidgetKind.Todo, plan.Descriptor.WidgetKind);
        Assert.IsType<TodoWidgetContentAdapter>(plan.Content);
        Assert.True(factory.CanCreateHiddenContentWindow(WidgetKind.Todo));
        Assert.False(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Todo));
    }

    [Theory]
    [InlineData(WidgetKind.Weather)]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.Music)]
    [InlineData(WidgetKind.SystemMonitor)]
    public void CreateHiddenContentWindowPlan_ReturnsPlaceholderForFutureKinds(WidgetKind widgetKind)
    {
        var config = CreateConfig("future-window", widgetKind);
        var factory = CreateFactory();

        var plan = factory.CreateHiddenContentWindowPlan(config);

        Assert.Equal(widgetKind, plan.Descriptor.WidgetKind);
        Assert.IsType<PlaceholderWidgetContent>(plan.Content);
        Assert.True(factory.CanCreateHiddenContentWindow(widgetKind));
        Assert.False(WidgetRegistry.Default.CanCreateWindow(widgetKind));
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    [InlineData(WidgetKind.Productivity)]
    public void CreateHiddenContentWindowPlan_RejectsWindowOwnedAndLegacyKinds(WidgetKind widgetKind)
    {
        var config = CreateConfig("unsupported-window", widgetKind);
        var factory = CreateFactory();

        Assert.False(factory.CanCreateHiddenContentWindow(widgetKind));
        Assert.Throws<NotSupportedException>(() => factory.CreateHiddenContentWindowPlan(config));
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
            new WidgetContentFactory(),
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
