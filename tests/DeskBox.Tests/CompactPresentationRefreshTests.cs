using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class CompactPresentationRefreshTests
{
    [Theory]
    [InlineData(nameof(WeatherWidgetViewModel.CurrentTemperatureText))]
    [InlineData(nameof(WeatherWidgetViewModel.CurrentDescription))]
    [InlineData(nameof(WeatherWidgetViewModel.PrecipitationText))]
    [InlineData(nameof(WeatherWidgetViewModel.CurrentWeatherCode))]
    [InlineData(nameof(WeatherWidgetViewModel.UsesRichSkin))]
    [InlineData(nameof(WeatherWidgetViewModel.RichSkinUsesLightText))]
    [InlineData(nameof(WeatherWidgetViewModel.RichBackdropTopColor))]
    [InlineData(nameof(WeatherWidgetViewModel.RichBackdropBottomColor))]
    public void WeatherCompactPresentation_RefreshesForDisplayedWeatherProperties(
        string propertyName)
    {
        Assert.True(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Weather,
            propertyName));
    }

    [Theory]
    [InlineData(nameof(WeatherWidgetViewModel.LayoutMode))]
    [InlineData(nameof(WeatherWidgetViewModel.ExpandedSunriseVisibility))]
    [InlineData(nameof(WeatherWidgetViewModel.ExpandedHourlyPrecipVisibility))]
    [InlineData(nameof(WeatherWidgetViewModel.ExpandedHourlyCardHeight))]
    [InlineData(nameof(WeatherWidgetViewModel.ExpandedHourlyDividerVisibility))]
    public void WeatherCompactPresentation_IgnoresExpandedLayoutProperties(
        string propertyName)
    {
        Assert.False(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Weather,
            propertyName));
    }

    [Theory]
    [InlineData(nameof(MusicWidgetViewModel.Title))]
    [InlineData(nameof(MusicWidgetViewModel.IsPlaying))]
    [InlineData(nameof(MusicWidgetViewModel.SeekValue))]
    [InlineData(nameof(MusicWidgetViewModel.CurrentLyricLine))]
    public void MusicCompactPresentation_RefreshesForDisplayedMusicProperties(
        string propertyName)
    {
        Assert.True(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Music,
            propertyName));
    }

    [Fact]
    public void MusicCompactPresentation_IgnoresExpandedOnlyPositionText()
    {
        Assert.False(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Music,
            nameof(MusicWidgetViewModel.PositionText)));
    }

    [Theory]
    [InlineData(nameof(GlanceWidgetViewModel.TimeText))]
    [InlineData(nameof(GlanceWidgetViewModel.DateText))]
    [InlineData(nameof(GlanceWidgetViewModel.WeekdayText))]
    [InlineData(nameof(GlanceWidgetViewModel.TraditionalCalendarTitle))]
    [InlineData(nameof(GlanceWidgetViewModel.CurrentImagePath))]
    [InlineData(nameof(GlanceWidgetViewModel.BackgroundImageOpacity))]
    [InlineData(nameof(GlanceWidgetViewModel.HasVisibleCurrentImage))]
    [InlineData(nameof(GlanceWidgetViewModel.ReadabilityOpacity))]
    [InlineData(nameof(GlanceWidgetViewModel.ReadabilityStrengthOpacity))]
    public void GlanceCompactPresentation_RefreshesForVisibleClockAndImageProperties(
        string propertyName)
    {
        Assert.True(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Glance,
            propertyName));
    }

    [Fact]
    public void GlanceCompactPresentation_IgnoresExpandedOnlyLoadingStatus()
    {
        Assert.False(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Glance,
            nameof(GlanceWidgetViewModel.StatusText)));
    }

    [Fact]
    public void UnknownPropertyNotification_AlwaysRefreshes()
    {
        Assert.True(ContentWidgetWindow.IsCompactPresentationPropertyRelevant(
            WidgetKind.Weather,
            propertyName: null));
    }
}
