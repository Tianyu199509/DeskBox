using DeskBox.Models;
using DeskBox.Services;
namespace DeskBox.Tests;

public sealed class FeatureWidgetEntryFactoryTests
{
    [Fact]
    public void CreateEntries_AreDescriptorDrivenAndIncludePlannedKinds()
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
            WidgetKind.Weather,
            WidgetKind.Tags,
            WidgetKind.SystemMonitor
        ], entries.Select(entry => entry.Kind));
        Assert.All(entries.Where(entry => entry.Kind is WidgetKind.QuickCapture or WidgetKind.Todo or WidgetKind.Music), entry =>
        {
            Assert.True(entry.ShowToggle);
            Assert.True(entry.CanToggle);
            Assert.True(entry.IsAvailable);
        });
        Assert.All(entries.Where(entry => entry.Kind is not (WidgetKind.QuickCapture or WidgetKind.Todo or WidgetKind.Music)), entry =>
        {
            Assert.False(entry.ShowToggle);
            Assert.False(entry.CanToggle);
            Assert.False(entry.IsAvailable);
        });
    }
}
