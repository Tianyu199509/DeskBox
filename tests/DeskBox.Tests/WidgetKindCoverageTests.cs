using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetKindCoverageTests
{
    private static readonly WidgetKind[] LegacyWidgetKinds = [WidgetKind.Productivity];

    private static readonly WidgetKind[] ActiveWidgetKinds = Enum
        .GetValues<WidgetKind>()
        .Except(LegacyWidgetKinds)
        .ToArray();

    [Fact]
    public void RegistryAndContentFactory_CoverTheSameActiveWidgetKinds()
    {
        var registry = WidgetRegistry.Default;
        var factory = new WidgetContentFactory();
        var expectedKinds = ActiveWidgetKinds
            .OrderBy(widgetKind => widgetKind)
            .ToArray();

        var contentKinds = factory
            .GetDescriptors()
            .Select(descriptor => descriptor.WidgetKind)
            .OrderBy(widgetKind => widgetKind)
            .ToArray();

        Assert.Equal(expectedKinds, contentKinds);

        foreach (var widgetKind in expectedKinds)
        {
            Assert.True(registry.IsKnown(widgetKind));
            Assert.NotNull(factory.GetDescriptor(widgetKind));
        }
    }

    [Fact]
    public void LegacyProductivityKind_IsNotRegisteredAsActiveContent()
    {
        var registry = WidgetRegistry.Default;
        var factory = new WidgetContentFactory();

        Assert.False(registry.IsKnown(WidgetKind.Productivity));
        Assert.Throws<NotSupportedException>(() => factory.GetDescriptor(WidgetKind.Productivity));
        Assert.False(factory.HasImplementedContent(WidgetKind.Productivity));
        Assert.False(factory.CanShowInCreateEntry(WidgetKind.Productivity));
    }

    [Fact]
    public void WindowCreatableKinds_AreImplementedAndAvailableContent()
    {
        var registry = WidgetRegistry.Default;
        var factory = new WidgetContentFactory();

        foreach (var descriptor in factory.GetDescriptors())
        {
            bool canCreateWindow = registry.CanCreateWindow(descriptor.WidgetKind);

            if (canCreateWindow)
            {
                Assert.True(registry.IsImplemented(descriptor.WidgetKind));
                Assert.True(descriptor.HasImplementedContent);
                Assert.True(descriptor.IsAvailable);
                continue;
            }

            Assert.False(descriptor.CanShowInCreateEntry);
        }
    }

    [Fact]
    public void PlannedKinds_ArePlaceholderOnlyAndNotWindowCreatable()
    {
        var registry = WidgetRegistry.Default;
        var factory = new WidgetContentFactory();

        foreach (var descriptor in factory.GetDescriptors().Where(descriptor => descriptor.IsPlanned))
        {
            Assert.True(descriptor.IsPlaceholderOnly);
            Assert.True(factory.CanCreatePlaceholderContent(descriptor.WidgetKind));
            Assert.False(registry.CanCreateWindow(descriptor.WidgetKind));
        }
    }
}
