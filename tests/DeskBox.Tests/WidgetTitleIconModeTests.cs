using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetTitleIconModeTests
{
    [Fact]
    public void SearchWidget_UsesDedicatedSearchIconFamily()
    {
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromWidgetKind(WidgetKind.Search));
        Assert.Equal(WidgetTitleIconKindNames.Search, WidgetTitleIconKindNames.FromLegacyGlyph("\uE721"));
        Assert.Equal("search", WidgetTitleIconKindNames.GetColorAssetName(WidgetTitleIconKind.Search));
        Assert.Equal("WidgetTitleIcon.Label.Search", WidgetTitleIconKindNames.GetLocalizationKey(WidgetTitleIconKind.Search));
    }
}
