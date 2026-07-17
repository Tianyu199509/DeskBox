using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class FeatureWidgetEntryFactoryTests
{
    [Fact]
    public void CreateEntries_OnlyIncludesAvailableImplementedKinds()
    {
        var settingsService = new SettingsService();
        var localizationService = TestServices.CreateLocalizationService();
        var factory = new FeatureWidgetEntryFactory(
            localizationService,
            TestServices.CreateWidgetContentFactory(),
            WidgetRegistry.Default,
            kind => FeatureWidgetSettings.IsEnabled(settingsService.Settings, kind));

        var entries = factory.CreateEntries();

        Assert.Equal(
        [
            WidgetKind.QuickCapture,
            WidgetKind.Todo,
            WidgetKind.Music,
            WidgetKind.Weather
        ], entries.Select(entry => entry.Kind));
        Assert.All(entries, entry =>
        {
            Assert.True(entry.ShowToggle);
            Assert.True(entry.CanToggle);
            Assert.True(entry.IsAvailable);
        });
        Assert.DoesNotContain(entries, entry =>
            entry.Kind is WidgetKind.Tags or WidgetKind.SystemMonitor);
    }
}
