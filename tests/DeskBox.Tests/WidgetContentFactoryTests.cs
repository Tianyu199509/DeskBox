using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class WidgetContentFactoryTests
{
    [Theory]
    [InlineData(WidgetKind.Weather)]
    [InlineData(WidgetKind.Todo)]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.Music)]
    [InlineData(WidgetKind.SystemMonitor)]
    public void CanCreatePlaceholderContent_ForFutureWidgetKinds(WidgetKind widgetKind)
    {
        var factory = new WidgetContentFactory();

        Assert.True(factory.CanCreatePlaceholderContent(widgetKind));
    }

    [Fact]
    public void CreatePlaceholderContent_ReturnsContentWithoutMakingKindCreatable()
    {
        var factory = new WidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "weather-test",
            Name = "Weather",
            WidgetKind = WidgetKind.Weather
        };

        var content = factory.CreatePlaceholderContent(config);

        Assert.IsType<PlaceholderWidgetContent>(content);
        Assert.Equal("weather-test", content.WidgetId);
        Assert.Equal(WidgetKind.Weather, content.WidgetKind);
        Assert.False(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Weather));
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    public void CreatePlaceholderContent_RejectsImplementedKinds(WidgetKind widgetKind)
    {
        var factory = new WidgetContentFactory();
        var config = new WidgetConfig
        {
            WidgetKind = widgetKind
        };

        Assert.False(factory.CanCreatePlaceholderContent(widgetKind));
        Assert.Throws<NotSupportedException>(() => factory.CreatePlaceholderContent(config));
    }
}
