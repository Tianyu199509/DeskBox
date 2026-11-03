using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetKindCoverageTests
{
    private static readonly WidgetKind[] LegacyWidgetKinds = [WidgetKind.Productivity, WidgetKind.Search];

    private static readonly WidgetKind[] ActiveWidgetKinds = Enum
        .GetValues<WidgetKind>()
        .Except(LegacyWidgetKinds)
        .ToArray();

    [Fact]
    public void RegistryAndContentFactory_CoverTheSameActiveWidgetKinds()
    {
        var registry = WidgetRegistry.Default;
        var factory = TestServices.CreateWidgetContentFactory();
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

    [Theory]
    [InlineData(WidgetKind.Productivity)]
    [InlineData(WidgetKind.Search)]
    public void LegacyWidgetKinds_AreNotRegisteredAsActiveContent(WidgetKind legacyKind)
    {
        var registry = WidgetRegistry.Default;
        var factory = TestServices.CreateWidgetContentFactory();

        Assert.False(registry.IsKnown(legacyKind));
        Assert.Throws<NotSupportedException>(() => factory.GetDescriptor(legacyKind));
        Assert.False(factory.HasImplementedContent(legacyKind));
        Assert.False(factory.CanShowInCreateEntry(legacyKind));
    }

    [Fact]
    public void WindowCreatableKinds_AreImplementedAndAvailableContent()
    {
        var registry = WidgetRegistry.Default;
        var factory = TestServices.CreateWidgetContentFactory();

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
        var factory = TestServices.CreateWidgetContentFactory();

        foreach (var descriptor in factory.GetDescriptors().Where(descriptor => descriptor.IsPlanned))
        {
            Assert.True(descriptor.IsPlaceholderOnly);
            Assert.True(factory.CanCreatePlaceholderContent(descriptor.WidgetKind));
            Assert.False(registry.CanCreateWindow(descriptor.WidgetKind));
        }
    }
}
