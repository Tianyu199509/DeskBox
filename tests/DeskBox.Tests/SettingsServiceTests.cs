using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _tempRoot;
    private readonly string _settingsRoot;

    public SettingsServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _settingsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "settings")).FullName;
    }

    [Fact]
    public async Task LoadAsync_PreservesQuickCaptureWidgetsAndRemovesLegacyProductivityWidgets()
    {
        var settings = new AppSettings
        {
            QuickCaptureEnabled = true,
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "quick-capture",
                    Name = "Quick Capture",
                    WidgetKind = WidgetKind.QuickCapture,
                    IsVisible = true
                },
                new WidgetConfig
                {
                    Id = "legacy-productivity",
                    Name = "Legacy",
                    WidgetKind = WidgetKind.Productivity,
                    IsVisible = true
                }
            ]
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.True(service.Settings.QuickCaptureEnabled);
        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal("quick-capture", widget.Id);
        Assert.Equal(WidgetKind.QuickCapture, widget.WidgetKind);
    }

    [Fact]
    public async Task LoadAsync_PreservesFutureWidgetKindsAndMetadata()
    {
        var settings = new AppSettings
        {
            Widgets =
            [
                new WidgetConfig
                {
                    Id = "weather",
                    Name = "Weather",
                    WidgetKind = WidgetKind.Weather,
                    Metadata =
                    {
                        ["city"] = "Shanghai",
                        ["unit"] = "metric"
                    }
                }
            ]
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal(WidgetKind.Weather, widget.WidgetKind);
        Assert.Equal("Shanghai", widget.Metadata["city"]);
        Assert.Equal("metric", widget.Metadata["unit"]);
    }

    [Fact]
    public async Task LoadAsync_SafelyDowngradesUnknownWidgetKind()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "widgets": [
                {
                  "id": "unknown-kind",
                  "name": "Unknown",
                  "widgetKind": "FutureExperimentalWidget",
                  "isVisible": true
                }
              ]
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        var widget = Assert.Single(service.Settings.Widgets);
        Assert.Equal("unknown-kind", widget.Id);
        Assert.Equal(WidgetKind.File, widget.WidgetKind);
    }

    [Fact]
    public async Task LoadAsync_NormalizesQuickCaptureRecentLimit()
    {
        var settings = new AppSettings
        {
            QuickCaptureRecentLimit = 2
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(QuickCaptureService.DefaultRecentLimit, service.Settings.QuickCaptureRecentLimit);
    }

    [Fact]
    public async Task LoadAsync_DefaultsQuickCaptureImageRecordingToEnabled()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            "{}");

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.True(service.Settings.QuickCaptureImageClipboardEnabled);
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyFeatureWidgetEnabledStates()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": false,
              "todoEnabled": true
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.True(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Todo));
        Assert.False(service.Settings.QuickCaptureEnabled);
        Assert.True(service.Settings.TodoEnabled);
    }

    [Fact]
    public async Task LoadAsync_FeatureWidgetEnabledStatesSynchronizeLegacyMirrors()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "quickCaptureEnabled": true,
              "todoEnabled": true,
              "featureWidgetEnabledStates": {
                "QuickCapture": false,
                "Todo": false
              }
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.QuickCapture));
        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Todo));
        Assert.False(service.Settings.QuickCaptureEnabled);
        Assert.False(service.Settings.TodoEnabled);
    }

    [Fact]
    public async Task LoadAsync_ClampsQuickCaptureRecentLimitToMaximum()
    {
        var settings = new AppSettings
        {
            QuickCaptureRecentLimit = 500
        };

        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            JsonSerializer.Serialize(settings, s_jsonOptions));

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(QuickCaptureService.MaxRecentLimit, service.Settings.QuickCaptureRecentLimit);
    }

    [Fact]
    public async Task LoadAsync_NormalizesTodoSettings()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "todoNewTaskPosition": "Middle",
              "todoDefaultFilter": "Someday",
              "todoShowCompletedTasks": false,
              "todoShowFooterStats": false,
              "todoShowClearCompletedButton": false,
              "todoConfirmBeforeDelete": true
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.TodoNewTaskPositionTop, service.Settings.TodoNewTaskPosition);
        Assert.Equal(SettingsService.TodoDefaultFilterAll, service.Settings.TodoDefaultFilter);
        Assert.False(service.Settings.TodoShowCompletedTasks);
        Assert.False(service.Settings.TodoShowFooterStats);
        Assert.False(service.Settings.TodoShowClearCompletedButton);
        Assert.True(service.Settings.TodoConfirmBeforeDelete);
    }

    [Fact]
    public async Task LoadAsync_NormalizesWidgetChromeSettingsAndMetadata()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_settingsRoot, "settings.json"),
            """
            {
              "displayWidgetChromeMode": "Floaty",
              "interactiveWidgetChromeMode": "hidden",
              "widgets": [
                {
                  "id": "music",
                  "name": "Music",
                  "widgetKind": "Music",
                  "metadata": {
                    "ChromeMode": "compact"
                  }
                },
                {
                  "id": "todo",
                  "name": "Todo",
                  "widgetKind": "Todo",
                  "metadata": {
                    "ChromeMode": "System"
                  }
                }
              ]
            }
            """);

        var service = new SettingsService(_settingsRoot);
        await service.LoadAsync();

        Assert.Equal(SettingsService.WidgetChromeModeOverlay, service.Settings.DisplayWidgetChromeMode);
        Assert.Equal(SettingsService.WidgetChromeModeHidden, service.Settings.InteractiveWidgetChromeMode);
        Assert.Equal(SettingsService.WidgetChromeModeCompact, service.Settings.Widgets[0].Metadata[WidgetChromeModeNames.MetadataKey]);
        Assert.False(service.Settings.Widgets[1].Metadata.ContainsKey(WidgetChromeModeNames.MetadataKey));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
