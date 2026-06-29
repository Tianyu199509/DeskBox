using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class WidgetContentFactoryTests
{
    [Theory]
    [InlineData(WidgetKind.File, "DeskBox", false)]
    [InlineData(WidgetKind.QuickCapture, "Quick Capture", false)]
    [InlineData(WidgetKind.Weather, "Weather", true)]
    [InlineData(WidgetKind.Todo, "Todo", true)]
    [InlineData(WidgetKind.Tags, "Tags", true)]
    [InlineData(WidgetKind.Music, "Music", true)]
    [InlineData(WidgetKind.SystemMonitor, "System Monitor", true)]
    public void GetDescriptor_ReturnsContentMetadata(WidgetKind widgetKind, string title, bool hasPlaceholderContent)
    {
        var factory = new WidgetContentFactory();

        var descriptor = factory.GetDescriptor(widgetKind);

        Assert.Equal(widgetKind, descriptor.WidgetKind);
        Assert.Equal(title, descriptor.DefaultTitle);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultGlyph));
        Assert.Equal(hasPlaceholderContent, descriptor.HasPlaceholderContent);
    }

    [Fact]
    public void GetDescriptor_RejectsLegacyProductivityKind()
    {
        var factory = new WidgetContentFactory();

        Assert.Throws<NotSupportedException>(() => factory.GetDescriptor(WidgetKind.Productivity));
    }

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
        Assert.False(WidgetRegistry.Default.CanCreateWindow(widgetKind));
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
