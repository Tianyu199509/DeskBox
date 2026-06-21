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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
