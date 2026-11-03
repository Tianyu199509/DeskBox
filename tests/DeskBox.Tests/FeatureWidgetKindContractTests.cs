using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Pins the user-enableable widget kind set. <see cref="FeatureWidgetSettings"/>
/// derives its ordered list from the content descriptor table, so flipping
/// <c>IsFeatureWidget</c> on a descriptor changes the enable/disable semantics
/// that every user's settings are normalized against. These assertions make
/// that a deliberate edit rather than a silent one.
/// </summary>
public sealed class FeatureWidgetKindContractTests
{
    [Fact]
    public void FeatureKinds_AreExactlyTheUserEnableableOnes()
    {
        Assert.Equal(
            new[]
            {
                WidgetKind.QuickCapture,
                WidgetKind.Todo,
                WidgetKind.Music,
                WidgetKind.Weather,
                WidgetKind.Search,
                WidgetKind.Glance,
                WidgetKind.Custom,
                WidgetKind.Clipboard
            },
            FeatureWidgetSettings.FeatureKinds);
    }

    [Fact]
    public void NonFeatureKinds_AreNotUserEnableable()
    {
        Assert.False(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.File));
        Assert.False(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.Tags));
        Assert.False(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.SystemMonitor));
        Assert.False(FeatureWidgetSettings.IsFeatureWidget(WidgetKind.Productivity));
    }

    [Fact]
    public void DescriptorTable_DeclaresEachKindOnce()
    {
        WidgetKind[] kinds = WidgetContentFactory.DescriptorList
            .Select(descriptor => descriptor.WidgetKind)
            .ToArray();

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }

    [Fact]
    public void EveryWidgetKindIsDescribedExceptTheKnownGap()
    {
        // WidgetKind.Productivity is declared in the enum but has neither a
        // content descriptor nor a WidgetRegistry entry, so nothing can create
        // it and nothing enumerates the enum to notice. It stays listed here as
        // a known gap: either give it a descriptor (Placeholder/Planned, like
        // Tags) or drop the enum member, then remove it from this list.
        WidgetKind[] knownGap = [WidgetKind.Productivity];

        WidgetKind[] described = WidgetContentFactory.DescriptorList
            .Select(descriptor => descriptor.WidgetKind)
            .ToArray();

        WidgetKind[] missing = Enum.GetValues<WidgetKind>()
            .Where(kind => !described.Contains(kind))
            .Where(kind => !knownGap.Contains(kind))
            .ToArray();

        Assert.Empty(missing);
    }
}
