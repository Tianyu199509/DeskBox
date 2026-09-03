// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSettingsMenuHelperTests
{
    [Theory]
    [InlineData(WidgetKind.File, "FileDisplaySettings")]
    [InlineData(WidgetKind.QuickCapture, "QuickCaptureSettings")]
    [InlineData(WidgetKind.Todo, "TodoSettings")]
    [InlineData(WidgetKind.Music, "MusicSettings")]
    [InlineData(WidgetKind.Weather, "WeatherSettings")]
    [InlineData(WidgetKind.Glance, "GlanceSettings")]
    [InlineData(WidgetKind.Search, "SearchSettings")]
    public void GetSettingsSectionTag_Returns_Expected_Tag(WidgetKind kind, string expectedTag)
    {
        Assert.Equal(expectedTag, WidgetSettingsMenuHelper.GetSettingsSectionTag(kind));
    }

    [Theory]
    [InlineData(WidgetKind.Tags)]
    [InlineData(WidgetKind.SystemMonitor)]
    [InlineData(WidgetKind.Productivity)]
    [InlineData(WidgetKind.Pomodoro)]
    public void GetSettingsSectionTag_Returns_Null_For_Unmapped_Kinds(WidgetKind kind)
    {
        Assert.Null(WidgetSettingsMenuHelper.GetSettingsSectionTag(kind));
    }

    [Fact]
    public void GetLocalizationKey_Returns_Specific_Key_For_Known_Kinds()
    {
        Assert.Equal("Widget.Settings.FileWidget", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.File));
        Assert.Equal("Widget.Settings.QuickCapture", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.QuickCapture));
        Assert.Equal("Widget.Settings.Todo", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Todo));
        Assert.Equal("Widget.Settings.Music", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Music));
        Assert.Equal("Widget.Settings.Weather", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Weather));
        Assert.Equal("Widget.Settings.Glance", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Glance));
        Assert.Equal("Widget.Settings.Search", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Search));
    }

    [Fact]
    public void GetLocalizationKey_Falls_Back_To_Configure_For_Unknown_Kinds()
    {
        Assert.Equal("Common.Configure", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.Tags));
        Assert.Equal("Common.Configure", WidgetSettingsMenuHelper.GetLocalizationKey(WidgetKind.SystemMonitor));
    }
}
