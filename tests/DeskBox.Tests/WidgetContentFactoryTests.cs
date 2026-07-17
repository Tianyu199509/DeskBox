using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class WidgetContentFactoryTests
{
    [Theory]
    [InlineData(WidgetKind.File, "DeskBox", WidgetContentStage.Implemented, true, WidgetContentAvailability.Available)]
    [InlineData(WidgetKind.QuickCapture, "Quick Capture", WidgetContentStage.Implemented, false, WidgetContentAvailability.Available)]
    [InlineData(WidgetKind.Weather, "Weather", WidgetContentStage.Implemented, false, WidgetContentAvailability.Available)]
    [InlineData(WidgetKind.Todo, "Todo", WidgetContentStage.Implemented, false, WidgetContentAvailability.Available)]
    [InlineData(WidgetKind.Tags, "Tags", WidgetContentStage.Placeholder, false, WidgetContentAvailability.Planned)]
    [InlineData(WidgetKind.Music, "Music", WidgetContentStage.Implemented, false, WidgetContentAvailability.Available)]
    [InlineData(WidgetKind.SystemMonitor, "System Monitor", WidgetContentStage.Placeholder, false, WidgetContentAvailability.Planned)]
    public void GetDescriptor_ReturnsContentMetadata(
        WidgetKind widgetKind,
        string title,
        WidgetContentStage stage,
        bool canShowInCreateEntry,
        WidgetContentAvailability availability)
    {
        var factory = TestServices.CreateWidgetContentFactory();

        var descriptor = factory.GetDescriptor(widgetKind);

        Assert.Equal(widgetKind, descriptor.WidgetKind);
        Assert.Equal(title, descriptor.DefaultTitle);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultGlyph));
        Assert.Equal(stage, descriptor.ContentStage);
        Assert.Equal(canShowInCreateEntry, descriptor.CanShowInCreateEntry);
        Assert.Equal(availability, descriptor.Availability);
        Assert.StartsWith($"WidgetContent.{widgetKind}.", descriptor.StatusLabelKey);
        Assert.StartsWith($"WidgetContent.{widgetKind}.", descriptor.StatusDescriptionKey);
    }

    [Fact]
    public void GetDescriptors_ReturnsStableKnownContentKinds()
    {
        var factory = TestServices.CreateWidgetContentFactory();

        var descriptors = factory.GetDescriptors();

        Assert.Equal(
        [
            WidgetKind.File,
            WidgetKind.QuickCapture,
            WidgetKind.Todo,
            WidgetKind.Music,
            WidgetKind.Weather,
            WidgetKind.Tags,
            WidgetKind.SystemMonitor
        ], descriptors.Select(descriptor => descriptor.WidgetKind));
    }

    [Theory]
    [InlineData(WidgetKind.File, WidgetChromeCategory.Interactive, WidgetChromeMode.Standard)]
    [InlineData(WidgetKind.QuickCapture, WidgetChromeCategory.Interactive, WidgetChromeMode.Standard)]
    [InlineData(WidgetKind.Todo, WidgetChromeCategory.Interactive, WidgetChromeMode.Standard)]
    [InlineData(WidgetKind.Tags, WidgetChromeCategory.Interactive, WidgetChromeMode.Standard)]
    [InlineData(WidgetKind.Music, WidgetChromeCategory.Display, WidgetChromeMode.Overlay)]
    [InlineData(WidgetKind.Weather, WidgetChromeCategory.Display, WidgetChromeMode.Overlay)]
    [InlineData(WidgetKind.SystemMonitor, WidgetChromeCategory.Display, WidgetChromeMode.Overlay)]
    public void GetDescriptor_ReturnsChromeDefaults(
        WidgetKind widgetKind,
        WidgetChromeCategory expectedCategory,
        WidgetChromeMode expectedDefaultMode)
    {
        var factory = TestServices.CreateWidgetContentFactory();

        var descriptor = factory.GetDescriptor(widgetKind);

        Assert.Equal(expectedCategory, descriptor.ChromeCategory);
        Assert.Equal(expectedDefaultMode, descriptor.DefaultChromeMode);
        Assert.True(descriptor.CanUseOverlayChrome);
        Assert.True(descriptor.CanHideChrome);
    }

    [Fact]
    public void GetCreateEntryDescriptors_OnlyReturnsCurrentlyCreatableContentEntries()
    {
        var factory = TestServices.CreateWidgetContentFactory();

        var descriptors = factory.GetCreateEntryDescriptors();

        Assert.Equal([WidgetKind.File], descriptors.Select(descriptor => descriptor.WidgetKind));
        Assert.All(descriptors, descriptor => Assert.True(WidgetRegistry.Default.CanCreateWindow(descriptor.WidgetKind)));
        Assert.All(descriptors, descriptor => Assert.False(string.IsNullOrWhiteSpace(descriptor.CreateEntryTextKey)));
        Assert.Equal("Common.NewWidget", descriptors.Single(descriptor => descriptor.WidgetKind == WidgetKind.File).CreateEntryTextKey);
    }

    [Fact]
    public void GetFeatureWidgetEntryDescriptors_OnlyReturnsAvailableImplementedWidgets()
    {
        var factory = TestServices.CreateWidgetContentFactory();

        var descriptors = factory.GetFeatureWidgetEntryDescriptors();

        Assert.Equal(
        [
            WidgetKind.QuickCapture,
            WidgetKind.Todo,
            WidgetKind.Music,
            WidgetKind.Weather
        ], descriptors.Select(descriptor => descriptor.WidgetKind));
        Assert.DoesNotContain(descriptors, descriptor => descriptor.WidgetKind == WidgetKind.File);
        Assert.DoesNotContain(descriptors, descriptor => descriptor.IsPlanned);
        Assert.Contains(descriptors, descriptor => descriptor.WidgetKind == WidgetKind.Todo && descriptor.HasImplementedContent);
        Assert.Contains(descriptors, descriptor => descriptor.WidgetKind == WidgetKind.Music && descriptor.HasImplementedContent);
        Assert.Contains(descriptors, descriptor => descriptor.WidgetKind == WidgetKind.Weather && descriptor.HasImplementedContent);
    }

    [Theory]
    [InlineData(WidgetKind.File, true, false, true, true, false)]
    [InlineData(WidgetKind.QuickCapture, true, false, false, true, false)]
    [InlineData(WidgetKind.Weather, true, false, false, true, false)]
    [InlineData(WidgetKind.Todo, true, false, false, true, false)]
    [InlineData(WidgetKind.Tags, false, true, false, false, true)]
    [InlineData(WidgetKind.Music, true, false, false, true, false)]
    [InlineData(WidgetKind.SystemMonitor, false, true, false, false, true)]
    [InlineData(WidgetKind.Productivity, false, false, false, false, false)]
    public void ContentCapabilityQueries_ReturnExpectedReadOnlyState(
        WidgetKind widgetKind,
        bool hasImplementedContent,
        bool isPlaceholderOnly,
        bool canShowInCreateEntry,
        bool isAvailable,
        bool isPlanned)
    {
        var factory = TestServices.CreateWidgetContentFactory();

        Assert.Equal(hasImplementedContent, factory.HasImplementedContent(widgetKind));
        Assert.Equal(isPlaceholderOnly, factory.IsPlaceholderOnly(widgetKind));
        Assert.Equal(canShowInCreateEntry, factory.CanShowInCreateEntry(widgetKind));
        Assert.Equal(isAvailable, factory.IsAvailable(widgetKind));
        Assert.Equal(isPlanned, factory.IsPlanned(widgetKind));
    }

    [Fact]
    public void StatusKeys_AreStableLocalizationKeys()
    {
        var factory = TestServices.CreateWidgetContentFactory();

        foreach (var descriptor in factory.GetDescriptors())
        {
            Assert.EndsWith(".StatusLabel", descriptor.StatusLabelKey);
            Assert.EndsWith(".StatusDescription", descriptor.StatusDescriptionKey);
            Assert.DoesNotContain(' ', descriptor.StatusLabelKey);
            Assert.DoesNotContain(' ', descriptor.StatusDescriptionKey);
        }
    }

    [Fact]
    public void GetDescriptor_RejectsLegacyProductivityKind()
    {
        var factory = TestServices.CreateWidgetContentFactory();

        Assert.Throws<NotSupportedException>(() => factory.GetDescriptor(WidgetKind.Productivity));
    }

    [Theory]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.SystemMonitor)]
    public void CanCreatePlaceholderContent_ForFutureWidgetKinds(WidgetKind widgetKind)
    {
        var factory = TestServices.CreateWidgetContentFactory();

        Assert.True(factory.CanCreatePlaceholderContent(widgetKind));
        Assert.False(WidgetRegistry.Default.CanCreateWindow(widgetKind));
    }

    [Fact]
    public void CreatePlaceholderContent_ReturnsContentWithoutMakingKindCreatable()
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "tags-test",
            Name = "Tags",
            WidgetKind = WidgetKind.Tags
        };

        var content = factory.CreatePlaceholderContent(config);

        Assert.IsType<PlaceholderWidgetContent>(content);
        Assert.Equal("tags-test", content.WidgetId);
        Assert.Equal(WidgetKind.Tags, content.WidgetKind);
        Assert.False(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Tags));
    }

    [Fact]
    public void CreateTodoContent_ReturnsImplementedAdapterForCreatableTodoKind()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "todo-test",
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };

        try
        {
            string widgetsDataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;
            var store = new TodoWidgetStore(widgetsDataRoot, config.Id);

            var content = factory.CreateTodoContent(config, store);

            Assert.IsType<TodoWidgetContentAdapter>(content);
            Assert.Equal("todo-test", content.WidgetId);
            Assert.Equal(WidgetKind.Todo, content.WidgetKind);
            Assert.True(factory.HasImplementedContent(WidgetKind.Todo));
            Assert.False(factory.IsPlaceholderOnly(WidgetKind.Todo));
            Assert.False(factory.CanShowInCreateEntry(WidgetKind.Todo));
            Assert.True(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Todo));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CreateTodoContent_RejectsNonTodoConfig()
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            WidgetKind = WidgetKind.File
        };

        Assert.Throws<ArgumentException>(() => factory.CreateTodoContent(config));
    }

    [Fact]
    public void CreateDetachedContent_ReturnsTodoAdapterForContentWindow()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "todo-detached",
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };

        try
        {
            string widgetsDataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;

            var content = factory.CreateDetachedContent(
                config,
                widget => new TodoWidgetStore(widgetsDataRoot, widget.Id));

            Assert.IsType<TodoWidgetContentAdapter>(content);
            Assert.Equal(WidgetKind.Todo, content.WidgetKind);
            Assert.True(factory.CanCreateDetachedContent(WidgetKind.Todo));
            Assert.False(factory.CanShowInCreateEntry(WidgetKind.Todo));
            Assert.True(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Todo));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void CreateDetachedContent_UsesSettingsAwareTodoProviderWhenSettingsServiceIsProvided()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        var factory = TestServices.CreateWidgetContentFactory();
        var settingsService = new SettingsService();
        settingsService.Settings.TextSize = 17;
        var config = new WidgetConfig
        {
            Id = "todo-settings-aware",
            Name = "Todo",
            WidgetKind = WidgetKind.Todo
        };

        try
        {
            string widgetsDataRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;

            var content = factory.CreateDetachedContent(
                config,
                widget => new TodoWidgetStore(widgetsDataRoot, widget.Id),
                settingsService);

            var adapter = Assert.IsType<TodoWidgetContentAdapter>(content);
            Assert.Equal(WidgetKind.Todo, adapter.WidgetKind);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.SystemMonitor)]
    public void CreateDetachedContent_ReturnsPlaceholderForFutureKinds(WidgetKind widgetKind)
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "future-detached",
            Name = widgetKind.ToString(),
            WidgetKind = widgetKind
        };

        var content = factory.CreateDetachedContent(config);

        Assert.IsType<PlaceholderWidgetContent>(content);
        Assert.Equal(widgetKind, content.WidgetKind);
        Assert.True(factory.CanCreateDetachedContent(widgetKind));
        Assert.False(factory.CanShowInCreateEntry(widgetKind));
        Assert.False(WidgetRegistry.Default.CanCreateWindow(widgetKind));
    }

    [Fact]
    public void CreateDetachedContent_ReturnsMusicAdapterForImplementedMusicKind()
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            Id = "music-detached",
            Name = "Music",
            WidgetKind = WidgetKind.Music
        };

        var content = factory.CreateDetachedContent(config);

        Assert.IsType<MusicWidgetContentAdapter>(content);
        Assert.Equal(WidgetKind.Music, content.WidgetKind);
        Assert.True(factory.CanCreateDetachedContent(WidgetKind.Music));
        Assert.False(factory.CanShowInCreateEntry(WidgetKind.Music));
        Assert.True(WidgetRegistry.Default.CanCreateWindow(WidgetKind.Music));
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    [InlineData(WidgetKind.Productivity)]
    public void CreateDetachedContent_RejectsLegacyAndWindowOwnedKinds(WidgetKind widgetKind)
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            WidgetKind = widgetKind
        };

        Assert.False(factory.CanCreateDetachedContent(widgetKind));
        Assert.Throws<NotSupportedException>(() => factory.CreateDetachedContent(config));
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    public void CreatePlaceholderContent_RejectsImplementedKinds(WidgetKind widgetKind)
    {
        var factory = TestServices.CreateWidgetContentFactory();
        var config = new WidgetConfig
        {
            WidgetKind = widgetKind
        };

        Assert.False(factory.CanCreatePlaceholderContent(widgetKind));
        Assert.Throws<NotSupportedException>(() => factory.CreatePlaceholderContent(config));
    }
}
