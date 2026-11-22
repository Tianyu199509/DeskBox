using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetAnimationSettingsTests
{
    [Fact]
    public void GetDirectionalOffset_NoneFallsBackToDownwardMotion()
    {
        var offset = WidgetAnimationSettings.GetDirectionalOffset(
            SettingsService.WidgetAnimationSlideDirectionNone,
            (Left: 320, Right: 480, Up: 240, Down: 360));

        Assert.Equal((0d, 360d), offset);
    }

    [Fact]
    public void From_DefaultSettingsUsesScreenEdgeSlideRightRelaxed()
    {
        var settings = new AppSettings();

        var options = WidgetAnimationSettings.From(settings);

        Assert.Equal(SettingsService.WidgetAnimationEffectScreenEdgeSlide, options.Effect);
        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionRight, options.SlideDirection);
        Assert.Equal(SettingsService.WidgetAnimationSpeedRelaxed, options.Speed);
        Assert.Equal(300, options.ShowDurationMs);
        Assert.Equal(210, options.HideDurationMs);
    }

    [Fact]
    public void From_SlideFadeWithNoDirectionFallsBackToDown()
    {
        var settings = new AppSettings
        {
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectSlideFade,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionNone
        };

        var options = WidgetAnimationSettings.From(settings);

        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionDown, options.SlideDirection);
    }

    [Fact]
    public void From_ScreenEdgeSlideWithNoDirectionDefaultsToAuto()
    {
        var settings = new AppSettings
        {
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectScreenEdgeSlide,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionNone
        };

        var options = WidgetAnimationSettings.From(settings);

        Assert.Equal(SettingsService.WidgetAnimationSlideDirectionAuto, options.SlideDirection);
    }

    [Fact]
    public void UsesGroupOffset_IsDisabledSoContainmentRunsPerWidget()
    {
        Assert.False(WidgetAnimationSettings.UsesGroupOffset(
            SettingsService.WidgetAnimationEffectSlideFade));
        Assert.False(WidgetAnimationSettings.UsesGroupOffset(
            SettingsService.WidgetAnimationEffectScreenEdgeSlide));
    }

    [Theory]
    [InlineData(SettingsService.WidgetAnimationSpeedVeryFast, true, 150)]
    [InlineData(SettingsService.WidgetAnimationSpeedFast, true, 150)]
    [InlineData(SettingsService.WidgetAnimationSpeedFast, false, 110)]
    [InlineData(SettingsService.WidgetAnimationSpeedStandard, true, 210)]
    [InlineData(SettingsService.WidgetAnimationSpeedStandard, false, 150)]
    [InlineData(SettingsService.WidgetAnimationSpeedRelaxed, true, 300)]
    [InlineData(SettingsService.WidgetAnimationSpeedRelaxed, false, 210)]
    [InlineData(SettingsService.WidgetAnimationSpeedSlow, false, 210)]
    public void GetDurationMs_ReturnsAsymmetricShowAndHideDurations(
        string speed,
        bool isShowing,
        int expectedDurationMs)
    {
        Assert.Equal(expectedDurationMs, WidgetAnimationSettings.GetDurationMs(speed, isShowing));
    }

    [Fact]
    public void ComputeStaggerDelaysMs_OrdersAlongMotionDirectionAndCapsSpread()
    {
        // Moving right (+36, 0): rightmost widget (X=800) leads with 0ms so
        // trailing widgets never overtake leading widgets.
        var items = new (double BaseX, double BaseY, double MotionX, double MotionY)[]
        {
            (100, 100, 36, 0),
            (450, 100, 36, 0),
            (800, 100, 36, 0)
        };

        int[] delays = WidgetAnimationSettings.ComputeStaggerDelaysMs(
            items,
            staggerEnabled: true,
            SettingsService.WidgetAnimationEffectSlideFade);

        Assert.Equal(0, delays[2]);
        Assert.True(delays[2] < delays[1] && delays[1] < delays[0]);
        Assert.True(delays.Max() <= WidgetAnimationSettings.MaxTotalStaggerSpreadMs);
    }

    [Fact]
    public void ComputeStaggerDelaysMs_WorksForStationaryScaleFadeAndSkipsNone()
    {
        var items = new (double BaseX, double BaseY, double MotionX, double MotionY)[]
        {
            (500, 300, 0, 0),
            (100, 100, 0, 0),
            (300, 100, 0, 0)
        };

        int[] active = WidgetAnimationSettings.ComputeStaggerDelaysMs(
            items,
            staggerEnabled: true,
            SettingsService.WidgetAnimationEffectScaleFade);
        Assert.Equal(0, active[1]);
        Assert.True(active[1] < active[2] && active[2] < active[0]);

        int[] disabled = WidgetAnimationSettings.ComputeStaggerDelaysMs(
            items,
            staggerEnabled: true,
            SettingsService.WidgetAnimationEffectNone);
        Assert.All(disabled, delay => Assert.Equal(0, delay));
    }

    [Fact]
    public void Ease_SpringOvershootsGentlyOnShow_AndNeverBouncesBackwardOnHide()
    {
        double springShowPeak = Enumerable.Range(1, 99)
            .Select(step => WidgetAnimationSettings.Ease(
                step / 100.0,
                SettingsService.WidgetAnimationEasingSpring,
                isShowing: true))
            .Max();
        Assert.InRange(springShowPeak, 1.005, 1.08);

        double[] hideSamples = Enumerable.Range(0, 101)
            .Select(step => WidgetAnimationSettings.Ease(
                step / 100.0,
                SettingsService.WidgetAnimationEasingSpring,
                isShowing: false))
            .ToArray();
        Assert.All(hideSamples, sample => Assert.InRange(sample, 0.0, 1.0));
    }
}
