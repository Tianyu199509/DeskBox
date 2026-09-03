using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.Tests;

public sealed class ContentWidgetWindowFactorySurfaceTests
{
    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    public void InjectedSurfaceFactory_CreatesLegacyKindsAsWindowIndependentContent(
        WidgetKind kind)
    {
        var config = new WidgetConfig
        {
            Id = $"surface-{kind}",
            Name = kind.ToString(),
            WidgetKind = kind
        };
        var factory = new ContentWidgetWindowFactory(
            TestServices.CreateWidgetContentFactory(),
            new SettingsService(),
            quickCaptureContentFactory:
                candidate => new StubContent(candidate),
            fileContentFactory:
                candidate => new StubContent(candidate));

        ContentWidgetWindowPlan plan =
            factory.CreateContentWindowPlan(config);

        Assert.True(factory.CanCreateContentWindow(kind));
        Assert.Same(config, plan.Config);
        Assert.Same(config, plan.Content.Config);
        Assert.Equal(kind, plan.Content.WidgetKind);
        Assert.Equal(kind, plan.Descriptor.WidgetKind);
    }

    [Fact]
    public void SurfaceFactory_OnlyInvokesDelegateForRequestedKind()
    {
        int fileCreations = 0;
        int quickCreations = 0;
        var factory = new ContentWidgetWindowFactory(
            TestServices.CreateWidgetContentFactory(),
            new SettingsService(),
            quickCaptureContentFactory: config =>
            {
                quickCreations++;
                return new StubContent(config);
            },
            fileContentFactory: config =>
            {
                fileCreations++;
                return new StubContent(config);
            });

        factory.CreateContentWindowPlan(new WidgetConfig
        {
            Id = "file",
            WidgetKind = WidgetKind.File
        });

        Assert.Equal(1, fileCreations);
        Assert.Equal(0, quickCreations);
    }

    [Theory]
    [InlineData(WidgetKind.File)]
    [InlineData(WidgetKind.QuickCapture)]
    [InlineData(WidgetKind.Todo)]
    [InlineData(WidgetKind.Music)]
    [InlineData(WidgetKind.Weather)]
    [InlineData(WidgetKind.Search)]
    [InlineData(WidgetKind.Pomodoro)]
    public void EveryCurrentlyAvailableKind_HasUnifiedSurfaceContent(
        WidgetKind kind)
    {
        var factory = new ContentWidgetWindowFactory(
            TestServices.CreateWidgetContentFactory(),
            new SettingsService(),
            quickCaptureContentFactory:
                candidate => new StubContent(candidate),
            fileContentFactory:
                candidate => new StubContent(candidate));

        Assert.True(factory.CanCreateContentWindow(kind));
    }

    private sealed class StubContent(WidgetConfig config) : IWidgetContent
    {
        public WidgetConfig Config { get; } = config;

        public string WidgetId => Config.Id;

        public WidgetKind WidgetKind => Config.WidgetKind;

        public FrameworkElement View =>
            throw new NotSupportedException(
                "The factory plan test does not create a XAML view.");

        public Task InitializeAsync() => Task.CompletedTask;

        public Task RefreshAsync() => Task.CompletedTask;

        public void ApplyAppearance()
        {
        }

        public void OnActivated()
        {
        }

        public void OnDeactivated()
        {
        }
    }
}
