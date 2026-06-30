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
    public void IsAvailableForSession_AllowsTodoWithoutFeatureFlag()
    {
        var registry = WidgetRegistry.Default;
        var todoWidget = new WidgetConfig
        {
            WidgetKind = WidgetKind.Todo
        };

        Assert.True(registry.IsAvailableForSession(todoWidget, new AppSettings()));
    }
}
