using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTopologyAppearanceTests
{
    [Fact]
    public async Task LegacySeedMigratesToLayoutStoreAndSurvivesRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "deskbox-appearance-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var topology = Topology(3840, 2160, 2);
            var legacy = new AppSettings { WidgetTopologyAppearanceDefaults = new(),
                ActiveWidgetTopologyKey = "v1-legacy",
                WidgetTopologyLayouts = new() { ["v1-legacy"] = new() {
                    Monitors = topology.Monitors.ToList(),
                    Appearance = new() { IconSize = 42, TextSize = 13 } } } };
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(legacy,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var first = new SettingsService(root);
            await first.LoadAsync();
            var service = new WidgetTopologyLayoutService();
            Assert.True(service.Activate(first.Settings, topology, startup: true));
            Assert.Equal(42, first.Settings.IconSize);
            await first.SaveAsync(notifySubscribers: false);
            var second = new SettingsService(root);
            await second.LoadAsync();
            Assert.NotNull(second.Settings.WidgetTopologyAppearanceDefaults);
            Assert.Equal(42, second.Settings.WidgetTopologyLayouts[topology.Key].Appearance!.IconSize);
            second.Settings.IconSize = 24;
            service.Activate(second.Settings, topology, startup: true);
            Assert.Equal(42, second.Settings.IconSize);
            Assert.Equal(13, second.Settings.TextSize);
            Assert.True(File.Exists(Path.Combine(root, "widget-layout.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FillingMissingAppearanceRequestsPersistenceEvenWhenProjectionMatches()
    {
        var settings = new AppSettings { WidgetTopologyAppearanceDefaults = new(),
            IconSize = 36, TextSize = 11.5, HorizontalSpacingScale = 0.1,
            VerticalSpacingScale = 0.3, FileNameWidthScale = 0.25 };
        var profile = new WidgetTopologyLayoutProfile();
        Assert.True(WidgetTopologyAppearanceService.Apply(settings, profile));
        Assert.False(WidgetTopologyAppearanceService.Apply(settings, profile));
    }

    [Fact]
    public void LegacySettingsRemainGlobalWhenNotOptedIn()
    {
        var settings = new AppSettings { IconSize = 31, TextSize = 12 };
        var profile = new WidgetTopologyLayoutProfile { Appearance = new() { IconSize = 42 } };
        Assert.False(WidgetTopologyAppearanceService.Apply(settings, profile));
        Assert.Equal(31, settings.IconSize);
        Assert.Equal(12, settings.TextSize);
    }

    [Fact]
    public void SwitchingAndRestartRestoreIndependentDimensionsBeforeCapacityPlanning()
    {
        var standard = Topology(2560, 1440, 1.5);
        var high = Topology(3840, 2160, 2);
        var settings = new AppSettings { IconSize = 36, TextSize = 11.5,
            WidgetTopologyAppearanceDefaults = new() };
        var service = new WidgetTopologyLayoutService();
        service.Activate(settings, standard);
        service.Activate(settings, high);
        settings.IconSize = 42;
        settings.TextSize = 13;
        settings.VerticalSpacingScale = 0.4;
        service.Activate(settings, standard);
        Assert.Equal(36, settings.IconSize);
        Assert.Equal(11.5, settings.TextSize);
        service.Activate(settings, high);
        Assert.Equal(42, settings.IconSize);
        Assert.Equal(13, settings.TextSize);
        Assert.Equal(0.4, settings.VerticalSpacingScale);
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        loaded.IconSize = 24; // stale global projection on startup must not replace saved profile
        service.Activate(loaded, high, startup: true);
        Assert.Equal(42, loaded.IconSize);
        service.Activate(loaded, standard);
        Assert.Equal(36, loaded.IconSize);
    }

    [Fact]
    public void MissingProfileUsesBaselineAndDoesNotCopyLargeOutgoingStyle()
    {
        var settings = new AppSettings { IconSize = 42, TextSize = 13,
            WidgetTopologyAppearanceDefaults = new() };
        WidgetTopologyAppearanceService.Apply(settings, new());
        Assert.Equal(36, settings.IconSize);
        Assert.Equal(11.5, settings.TextSize);
    }

    [Fact]
    public void LateAppearanceSignalCannotOverwriteAnotherTopologysProfile()
    {
        var profile = new WidgetTopologyLayoutProfile { Appearance = new() };
        var settings = new AppSettings { IconSize = 42, ActiveWidgetTopologyKey = "old",
            WidgetTopologyAppearanceDefaults = new(), WidgetTopologyLayouts = new() { ["old"] = profile } };
        Assert.False(WidgetTopologyAppearanceService.CaptureActive(settings, "new"));
        Assert.Equal(36, profile.Appearance!.IconSize);
        Assert.True(WidgetTopologyAppearanceService.CaptureActive(settings, "old"));
        Assert.False(WidgetTopologyAppearanceService.CaptureActive(settings, "old"));
        Assert.Equal(42, profile.Appearance!.IconSize);
    }

    [Fact]
    public void InvalidValuesAreBoundedAndWidgetOverridesArePreserved()
    {
        var widget = new WidgetConfig { IconSizeOverride = 48 };
        var settings = new AppSettings { Widgets = [widget], WidgetTopologyAppearanceDefaults = new() };
        WidgetTopologyAppearanceService.Apply(settings, new() { Appearance = new() {
            IconSize = 999, TextSize = double.NaN, VerticalSpacingScale = double.PositiveInfinity } });
        Assert.Equal(SettingsService.MaxIconSize, settings.IconSize);
        Assert.True(double.IsFinite(settings.TextSize));
        Assert.Equal(0.3, settings.VerticalSpacingScale);
        Assert.Equal(48, widget.IconSizeOverride);
    }

    private static WidgetDisplayTopologySnapshot Topology(int width, int height, double dpi) =>
        WidgetTopologyLayoutService.CreateSnapshotForTest(new WidgetTopologyMonitorProfile {
            StableId = "test-monitor", DeviceName = @"\\.\DISPLAY1", IsPrimary = true,
            MonitorWidth = width, MonitorHeight = height, WorkAreaWidth = width,
            WorkAreaHeight = height - 72, DpiScale = dpi });

}
