using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class WidgetContentFactoryTests
{
    [Theory]
    [InlineData(WidgetKind.File, "DeskBox", WidgetContentStage.Implemented, true)]
    [InlineData(WidgetKind.QuickCapture, "Quick Capture", WidgetContentStage.Implemented, false)]
    [InlineData(WidgetKind.Weather, "Weather", WidgetContentStage.Placeholder, false)]
    [InlineData(WidgetKind.Todo, "Todo", WidgetContentStage.Placeholder, false)]
    [InlineData(WidgetKind.Tags, "Tags", WidgetContentStage.Placeholder, false)]
    [InlineData(WidgetKind.Music, "Music", WidgetContentStage.Placeholder, false)]
    [InlineData(WidgetKind.SystemMonitor, "System Monitor", WidgetContentStage.Placeholder, false)]
    public void GetDescriptor_ReturnsContentMetadata(
        WidgetKind widgetKind,
        string title,
        WidgetContentStage stage,
        bool canShowInCreateEntry)
    {
        var factory = new WidgetContentFactory();

        var descriptor = factory.GetDescriptor(widgetKind);

        Assert.Equal(widgetKind, descriptor.WidgetKind);
        Assert.Equal(title, descriptor.DefaultTitle);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultGlyph));
        Assert.Equal(stage, descriptor.ContentStage);
        Assert.Equal(canShowInCreateEntry, descriptor.CanShowInCreateEntry);
    }

    [Fact]
    public void GetDescriptors_ReturnsStableKnownContentKinds()
    {
        var factory = new WidgetContentFactory();

        var descriptors = factory.GetDescriptors();

        Assert.Equal(
        [
            WidgetKind.File,
            WidgetKind.QuickCapture,
            WidgetKind.Weather,
            WidgetKind.Todo,
            WidgetKind.Tags,
            WidgetKind.Music,
            WidgetKind.SystemMonitor
        ], descriptors.Select(descriptor => descriptor.WidgetKind));
    }

    [Fact]
    public void GetCreateEntryDescriptors_OnlyReturnsCurrentlyCreatableContentEntries()
    {
        var factory = new WidgetContentFactory();

        var descriptors = factory.GetCreateEntryDescriptors();

        var descriptor = Assert.Single(descriptors);
        Assert.Equal(WidgetKind.File, descriptor.WidgetKind);
        Assert.True(WidgetRegistry.Default.CanCreateWindow(descriptor.WidgetKind));
    }

    [Theory]
    [InlineData(WidgetKind.File, true, false, true)]
    [InlineData(WidgetKind.QuickCapture, true, false, false)]
    [InlineData(WidgetKind.Weather, false, true, false)]
    [InlineData(WidgetKind.Todo, false, true, false)]
    [InlineData(WidgetKind.Tags, false, true, false)]
    [InlineData(WidgetKind.Music, false, true, false)]
    [InlineData(WidgetKind.SystemMonitor, false, true, false)]
    [InlineData(WidgetKind.Productivity, false, false, false)]
    public void ContentCapabilityQueries_ReturnExpectedReadOnlyState(
        WidgetKind widgetKind,
        bool hasImplementedContent,
        bool isPlaceholderOnly,
        bool canShowInCreateEntry)
    {
        var factory = new WidgetContentFactory();

        Assert.Equal(hasImplementedContent, factory.HasImplementedContent(widgetKind));
        Assert.Equal(isPlaceholderOnly, factory.IsPlaceholderOnly(widgetKind));
        Assert.Equal(canShowInCreateEntry, factory.CanShowInCreateEntry(widgetKind));
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
