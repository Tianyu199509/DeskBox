using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetRegistryTests
{
    [Fact]
    public void Default_KnowsFutureWidgetKindsAndCreatesImplementedWindows()
    {
        var registry = WidgetRegistry.Default;

        Assert.True(registry.IsKnown(WidgetKind.Weather));
        Assert.False(registry.CanCreateWindow(WidgetKind.Weather));
        Assert.True(registry.CanCreateWindow(WidgetKind.File));
        Assert.True(registry.CanCreateWindow(WidgetKind.QuickCapture));
        Assert.True(registry.CanCreateWindow(WidgetKind.Todo));
        Assert.True(registry.CanCreateWindow(WidgetKind.Music));
    }

    [Fact]
    public void IsAvailableForSession_RespectsQuickCaptureEnabledSetting()
    {
        var registry = WidgetRegistry.Default;
        var quickCaptureWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.QuickCapture
        };

        Assert.False(registry.IsAvailableForSession(
            quickCaptureWidget,
            new AppSettings { QuickCaptureEnabled = false }));
        Assert.True(registry.IsAvailableForSession(
            quickCaptureWidget,
            new AppSettings { QuickCaptureEnabled = true }));
    }

    [Fact]
    public void IsAvailableForSession_UsesFeatureWidgetStateOverLegacyQuickCaptureSetting()
    {
        var registry = WidgetRegistry.Default;
        var quickCaptureWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.QuickCapture
        };
        var settings = new AppSettings { QuickCaptureEnabled = true };
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.QuickCapture, false);

        Assert.False(registry.IsAvailableForSession(quickCaptureWidget, settings));
    }

    [Fact]
    public void IsAvailableForSession_RejectsFutureKindsUntilImplemented()
    {
        var registry = WidgetRegistry.Default;
        var weatherWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Weather
        };

        Assert.False(registry.IsAvailableForSession(weatherWidget, new AppSettings()));
    }

    [Fact]
    public void IsAvailableForSession_RespectsTodoEnabledSetting()
    {
        var registry = WidgetRegistry.Default;
        var todoWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Todo
        };

        Assert.False(registry.IsAvailableForSession(
            todoWidget,
            new AppSettings { TodoEnabled = false }));
        Assert.True(registry.IsAvailableForSession(
            todoWidget,
            new AppSettings { TodoEnabled = true }));
    }

    [Fact]
    public void IsAvailableForSession_UsesFeatureWidgetStateOverLegacyTodoSetting()
    {
        var registry = WidgetRegistry.Default;
        var todoWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Todo
        };
        var settings = new AppSettings { TodoEnabled = true };
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, false);

        Assert.False(registry.IsAvailableForSession(todoWidget, settings));
    }

    [Fact]
    public void IsAvailableForSession_RespectsMusicFeatureWidgetState()
    {
        var registry = WidgetRegistry.Default;
        var musicWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Music
        };
        var settings = new AppSettings();

        Assert.False(registry.IsAvailableForSession(musicWidget, settings));

        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Music, true);

        Assert.True(registry.IsAvailableForSession(musicWidget, settings));
    }
}
