using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class SettingsViewModelChromeModeTests
{
    [Fact]
    public void ResetWidgetChromeOverrides_UpdatesOnlyDisplayWidgets()
    {
        using var scope = new TempSettingsScope();
        var settingsService = scope.CreateSettingsService();
        settingsService.Settings.Widgets =
        [
            CreateWidget(WidgetKind.Music, WidgetChromeMode.Standard),
            CreateWidget(WidgetKind.Weather, WidgetChromeMode.Standard),
            CreateWidget(WidgetKind.File, WidgetChromeMode.Hidden),
            CreateWidget(WidgetKind.Todo, WidgetChromeMode.Hidden)
        ];

        var changed = SettingsViewModel.ResetWidgetChromeOverrides(
            settingsService.Settings,
            TestServices.CreateWidgetContentFactory(),
            WidgetChromeCategory.Display,
            SettingsService.WidgetChromeModeOverlay);

        Assert.Equal(2, changed);
        AssertChromeMode(settingsService.Settings.Widgets[0], SettingsService.WidgetChromeModeOverlay);
        AssertChromeMode(settingsService.Settings.Widgets[1], SettingsService.WidgetChromeModeOverlay);
        AssertChromeMode(settingsService.Settings.Widgets[2], SettingsService.WidgetChromeModeHidden);
        AssertChromeMode(settingsService.Settings.Widgets[3], SettingsService.WidgetChromeModeHidden);
    }

    [Fact]
    public void ResetWidgetChromeOverrides_UpdatesOnlyInteractiveWidgets()
    {
        using var scope = new TempSettingsScope();
        var settingsService = scope.CreateSettingsService();
        settingsService.Settings.Widgets =
        [
            CreateWidget(WidgetKind.File, WidgetChromeMode.Hidden),
            CreateWidget(WidgetKind.QuickCapture, WidgetChromeMode.Hidden),
            CreateWidget(WidgetKind.Todo, WidgetChromeMode.Hidden),
            CreateWidget(WidgetKind.Music, WidgetChromeMode.Standard)
        ];

        var changed = SettingsViewModel.ResetWidgetChromeOverrides(
            settingsService.Settings,
            TestServices.CreateWidgetContentFactory(),
            WidgetChromeCategory.Interactive,
            SettingsService.WidgetChromeModeCompact);

        Assert.Equal(3, changed);
        AssertChromeMode(settingsService.Settings.Widgets[0], SettingsService.WidgetChromeModeCompact);
        AssertChromeMode(settingsService.Settings.Widgets[1], SettingsService.WidgetChromeModeCompact);
        AssertChromeMode(settingsService.Settings.Widgets[2], SettingsService.WidgetChromeModeCompact);
        AssertChromeMode(settingsService.Settings.Widgets[3], SettingsService.WidgetChromeModeStandard);
    }

    private static WidgetConfig CreateWidget(WidgetKind kind, WidgetChromeMode mode)
    {
        var config = new WidgetConfig
        {
            Id = Guid.NewGuid().ToString(),
            WidgetKind = kind,
            Name = kind.ToString()
        };
        WidgetChromeModeNames.SetOverrideMode(config, mode);
        return config;
    }

    private static void AssertChromeMode(WidgetConfig config, string expected)
    {
        Assert.True(config.Metadata.TryGetValue(WidgetChromeModeNames.MetadataKey, out string? actual));
        Assert.Equal(expected, actual);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "DeskBoxTests", Guid.NewGuid().ToString("N"));

        public SettingsService CreateSettingsService()
        {
            return new SettingsService(_path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
