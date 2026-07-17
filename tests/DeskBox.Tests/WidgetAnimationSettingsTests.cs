using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetAnimationSettingsTests
{
    [Fact]
    public void GetDirectionalOffset_NoneFallsBackToRightwardMotion()
    {
        var offset = WidgetAnimationSettings.GetDirectionalOffset(
            SettingsService.WidgetAnimationSlideDirectionNone,
            (Left: 320, Right: 480, Up: 240, Down: 360));

        Assert.Equal((480d, 0d), offset);
    }

    [Fact]
    public void From_SlideWithNoDirectionFallsBackToRight()
    {
        var settings = new DeskBox.Models.AppSettings
        {
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionNone
        };

        var options = WidgetAnimationSettings.From(settings);

        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionRight, options.SlideDirection);
    }

    [Fact]
    public void UsesGroupOffset_SlideMovesTheNativeWindowGroup()
    {
        Assert.True(WidgetAnimationSettings.UsesGroupOffset(
            SettingsService.WidgetAnimationEffectSlideFade));
    }

    [Fact]
    public void UsesGroupOffset_LegacyFullSlideStillUsesBatchGeometry()
    {
        Assert.True(WidgetAnimationSettings.UsesGroupOffset(
            SettingsService.WidgetAnimationEffectSlideRight));
    }

    [Theory]
    [InlineData(SettingsService.WidgetAnimationSpeedVeryFast, 120)]
    [InlineData(SettingsService.WidgetAnimationSpeedFast, 220)]
    [InlineData(SettingsService.WidgetAnimationSpeedStandard, 240)]
    [InlineData(SettingsService.WidgetAnimationSpeedRelaxed, 520)]
    [InlineData(SettingsService.WidgetAnimationSpeedSlow, 680)]
    public void GetDurationMs_ReturnsCalibratedDuration(string speed, int expectedDurationMs)
    {
        Assert.Equal(expectedDurationMs, WidgetAnimationSettings.GetDurationMs(speed));
    }
}
